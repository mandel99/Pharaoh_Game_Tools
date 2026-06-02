using BinkInspector;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PharaohGameTools;
using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PharaohGameTools.WinUI;

public sealed partial class BikPlayerView : UserControl
{
    private const int BikFrameQueueLimit = 12;
    private const string BikAudioAlias = "ImpressionsSgViewerWinUiBikAudio";
    private const int ThumbnailTargetFrameSamplePercent = 10;
    private const int ThumbnailMaxWidth = 152;
    private const int ThumbnailMaxHeight = 92;
    private const double SeekCheckpointIntervalSeconds = 2.0;
    private readonly DispatcherTimer _playbackTimer;
    private readonly Stopwatch _playbackClock = new();
    private readonly ObservableCollection<BikThumbnailItem> _thumbnailItems = new();
    private readonly object _checkpointSync = new();
    private readonly Button _openButton;
    private readonly Button _openFolderButton;
    private readonly Button _exportAviButton;
    private readonly Button _exportMp4Button;
    private readonly InfoBar _statusInfoBar;
    private readonly Image _previewImage;
    private readonly TextBlock _overlayTextBlock;
    private readonly Slider _timelineSlider;
    private readonly Button _playPauseButton;
    private readonly Button _stopButton;
    private readonly TextBlock _positionTextBlock;
    private readonly TextBlock _durationTextBlock;
    private readonly TextBlock _infoTextBlock;
    private readonly Border _thumbnailBrowserBorder;
    private readonly ListView _thumbnailGridView;
    private readonly TextBlock _thumbnailHeaderTextBlock;

    private string? _sourcePath;
    private BinkFile? _bikFile;
    private WriteableBitmap? _writeableBitmap;
    private ConcurrentQueue<BikQueuedFrame>? _queuedFrames;
    private CancellationTokenSource? _decodeCancellation;
    private Task? _decodeTask;
    private volatile bool _decodeCompleted;
    private volatile string? _decodeFailure;
    private int _queuedFrameCount;
    private int _displayedFrameIndex = -1;
    private bool _isPaused;
    private bool _isPlaying;
    private bool _audioAvailable;
    private bool _updatingTimeline;
    private bool _timelinePointerActive;
    private string? _audioTempPath;
    private string? _thumbnailFolderPath;
    private CancellationTokenSource? _thumbnailLoadCancellation;
    private int _thumbnailLoadVersion;
    private double _playbackStartSeconds;
    private CancellationTokenSource? _timelineSeekCancellation;
    private int _pendingStartFrameIndex = -1;
    private bool _resumePlaybackAfterTimelineSeek;
    private bool _resumeFromPendingPosition;
    private bool _isTimelineSeekInProgress;
    private int _seekRequestVersion;
    private int _requestedTimelineFrameIndex = -1;
    private PreparedPlaybackStart? _preparedPlaybackStart;
    private List<BikDecoderCheckpoint> _decoderCheckpoints = new();
    private readonly Dictionary<string, List<BikDecoderCheckpoint>> _fileCheckpointCache = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _checkpointBuildCancellation;
    private int _checkpointBuildVersion;

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string command, StringBuilder? returnValue, int returnLength, IntPtr winHandle);

    public BikPlayerView()
    {
        InitializeComponent();

        _openButton = (Button)FindName(nameof(OpenButton));
        _openFolderButton = (Button)FindName("OpenFolderButton");
        _exportAviButton = (Button)FindName(nameof(ExportAviButton));
        _exportMp4Button = (Button)FindName(nameof(ExportMp4Button));
        _statusInfoBar = (InfoBar)FindName(nameof(StatusInfoBar));
        _previewImage = (Image)FindName(nameof(PreviewImage));
        _overlayTextBlock = (TextBlock)FindName(nameof(OverlayTextBlock));
        _timelineSlider = (Slider)FindName(nameof(TimelineSlider));
        _playPauseButton = (Button)FindName(nameof(PlayPauseButton));
        _stopButton = (Button)FindName(nameof(StopButton));
        _positionTextBlock = (TextBlock)FindName(nameof(PositionTextBlock));
        _durationTextBlock = (TextBlock)FindName(nameof(DurationTextBlock));
        _infoTextBlock = (TextBlock)FindName(nameof(InfoTextBlock));
        _thumbnailBrowserBorder = (Border)FindName("ThumbnailBrowserBorder");
        _thumbnailGridView = (ListView)FindName("ThumbnailGridView");
        _thumbnailHeaderTextBlock = (TextBlock)FindName("ThumbnailHeaderTextBlock");

        _playbackTimer = new DispatcherTimer();
        _playbackTimer.Tick += OnPlaybackTimerTick;
        _thumbnailGridView.ItemsSource = _thumbnailItems;

        UpdatePlaybackUi();
    }

    public void Shutdown()
    {
        ReleaseResourcesForShutdown();
    }

    public async Task PromptOpenAsync()
    {
        FileOpenPicker picker = PickerInterop.CreateOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".bik");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            await OpenBikFileAsync(file.Path);
        }
    }

    public async Task PromptOpenFolderAsync()
    {
        StorageFolder? folder = await PickerInterop.CreateFolderPicker(PickerLocationId.DocumentsLibrary).PickSingleFolderAsync();
        if (folder != null)
        {
            await LoadBikFolderAsync(folder.Path);
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptOpenAsync();
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptOpenFolderAsync();
    }

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        await TogglePlaybackAsync();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        LogDebug("StopButton_Click", "Stop requested by user.");
        StopPlayback(clearPreview: false);
        if (_bikFile != null && _bikFile.FrameIndex.Count > 0)
        {
            CancelScheduledTimelineSeek();
            _timelinePointerActive = false;
            _requestedTimelineFrameIndex = -1;
            _pendingStartFrameIndex = 0;
            _resumeFromPendingPosition = false;
            BikQueuedFrame firstFrame = DecodeBikFrame(_bikFile, 0);
            ShowBikFrame(firstFrame.Yuv, firstFrame.FrameIndex);
            SetReady("Playback stopped.");
        }
        UpdatePlaybackUi();
    }

    private async void ExportAviButton_Click(object sender, RoutedEventArgs e)
    {
        if (_bikFile == null || string.IsNullOrWhiteSpace(_sourcePath))
        {
            return;
        }

        FileSavePicker picker = PickerInterop.CreateSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_sourcePath) + ".avi";
        picker.FileTypeChoices.Add("AVI video", new[] { ".avi" });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            SetBusy("Exporting AVI...", "Writing MJPEG video with PCM audio to AVI.");
            await RunExportWithProgressDialogAsync(
                "Exporting AVI",
                "Writing MJPEG video with PCM audio to AVI.",
                progress => AviPcmExporter.Export(_bikFile, file.Path, progress));
            SetReady("AVI export completed.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Export AVI Failed", BuildExceptionDetails(ex));
            SetError("AVI export failed.");
        }
    }

    private async void ExportMp4Button_Click(object sender, RoutedEventArgs e)
    {
        if (_bikFile == null || string.IsNullOrWhiteSpace(_sourcePath))
        {
            return;
        }

        FileSavePicker picker = PickerInterop.CreateSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(_sourcePath) + ".mp4";
        picker.FileTypeChoices.Add("MP4 video", new[] { ".mp4" });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            SetBusy("Exporting MP4...", "Writing experimental MJPEG + PCM MP4 file.");
            await RunExportWithProgressDialogAsync(
                "Exporting MP4",
                "Writing experimental MJPEG + PCM MP4 file.",
                progress => MjpegMp4Muxer.Export(_bikFile, file.Path, progress));
            SetReady("MP4 export completed.");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Export MP4 Failed", BuildExceptionDetails(ex));
            SetError("MP4 export failed.");
        }
    }

    private async Task LoadBikFolderAsync(string folderPath)
    {
        CancelThumbnailLoading();
        try
        {
            string[] bikFiles = Directory.GetFiles(folderPath, "*.bik", SearchOption.TopDirectoryOnly)
                .OrderBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            _thumbnailItems.Clear();
            _thumbnailFolderPath = folderPath;
            UpdateThumbnailBrowserVisibility();

            if (bikFiles.Length == 0)
            {
                SetReady("The selected folder does not contain any BIK files.");
                _thumbnailHeaderTextBlock.Text = "Folder previews";
                return;
            }

            SetBusy("Loading folder...", "Listing BIK files and preparing previews...");
            _thumbnailHeaderTextBlock.Text = $"Folder previews | {Path.GetFileName(folderPath)}";

            foreach (string bikPath in bikFiles)
            {
                _thumbnailItems.Add(new BikThumbnailItem
                {
                    FilePath = bikPath,
                    DisplayName = Path.GetFileName(bikPath),
                    SummaryText = "Loading checkpoints...",
                    OverlayText = string.Empty,
                    IsLoading = true
                });
            }

            UpdateThumbnailBrowserVisibility();
            SetReady($"Loaded {bikFiles.Length} BIK file(s). Previews are being generated in the background.");

            CancellationTokenSource cancellation = new();
            _thumbnailLoadCancellation = cancellation;
            int loadVersion = ++_thumbnailLoadVersion;

            _ = PopulateThumbnailImagesAsync(loadVersion, cancellation.Token);
        }
        catch (Exception ex)
        {
            _thumbnailItems.Clear();
            UpdateThumbnailBrowserVisibility();
            SetError("Folder load failed.");
            await ShowMessageAsync("Bik Player", ex.Message);
        }
    }

    private async Task PopulateThumbnailImagesAsync(int loadVersion, CancellationToken cancellationToken)
    {
        try
        {
            List<Task> decodeTasks = new(_thumbnailItems.Count);

            for (int i = 0; i < _thumbnailItems.Count; i++)
            {
                BikThumbnailItem item = _thumbnailItems[i];
                decodeTasks.Add(PopulateThumbnailItemAsync(item, loadVersion, cancellationToken));
            }

            await Task.WhenAll(decodeTasks);

            if (loadVersion == _thumbnailLoadVersion && !string.IsNullOrWhiteSpace(_thumbnailFolderPath))
            {
                SetReady($"Loaded {_thumbnailItems.Count} BIK preview(s) from {Path.GetFileName(_thumbnailFolderPath)}.");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (loadVersion == _thumbnailLoadVersion)
            {
                SetError("Thumbnail generation failed.");
                await ShowMessageAsync("Bik Player", ex.Message);
            }
        }
    }

    private async Task PopulateThumbnailItemAsync(
        BikThumbnailItem item,
        int loadVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = new Progress<ThumbnailBuildProgress>(update =>
            {
                if (loadVersion != _thumbnailLoadVersion)
                {
                    return;
                }

                item.IsLoading = update.IsLoading;
                item.OverlayText = update.OverlayText;
                if (update.PreviewPixels.Length > 0)
                {
                    item.PreviewImage = CreateBitmapSource(update.Width, update.Height, update.PreviewPixels);
                }
            });

            BikThumbnailData thumbnailData = await Task.Run(() => BuildThumbnailData(item.FilePath, cancellationToken, progress));

            cancellationToken.ThrowIfCancellationRequested();
            if (loadVersion != _thumbnailLoadVersion)
            {
                return;
            }

            item.SummaryText = thumbnailData.SummaryText;
            item.OverlayText = thumbnailData.OverlayText;
            item.IsLoading = false;
            item.PreviewImage = thumbnailData.Pixels.Length > 0
                ? CreateBitmapSource(thumbnailData.Width, thumbnailData.Height, thumbnailData.Pixels)
                : null;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelThumbnailLoading()
    {
        CancellationTokenSource? cancellation = _thumbnailLoadCancellation;
        _thumbnailLoadCancellation = null;
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void InteractiveSurface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }

    private void InteractiveSurface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
    }

    private async void ThumbnailGridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not BikThumbnailItem thumbnailItem)
        {
            return;
        }

        await OpenBikFileAsync(thumbnailItem.FilePath, autoPlay: true);
    }

    private async Task OpenBikFileAsync(string path, bool autoPlay = false)
    {
        try
        {
            CancelCheckpointBuild();
            ClearDecoderCheckpoints();
            StopPlayback(clearPreview: true);
            CleanupAudioTempFile();
            SetBusy("Loading BIK...", "Reading video stream and preparing audio...");

            var result = await Task.Run(() =>
            {
                BinkFile file = BinkFile.Load(path);
                string? audioPath = file.AudioTracks.Count > 0 ? BuildBikAudioWaveFile(file, path) : null;
                BikQueuedFrame firstFrame = file.FrameIndex.Count > 0 ? DecodeBikFrame(file, 0) : new BikQueuedFrame();
                return Tuple.Create(file, audioPath, firstFrame);
            });

            _bikFile = result.Item1;
            _sourcePath = path;
            _audioTempPath = result.Item2;
            _audioAvailable = !string.IsNullOrWhiteSpace(_audioTempPath) && File.Exists(_audioTempPath);
            _displayedFrameIndex = -1;
            _pendingStartFrameIndex = 0;
            _requestedTimelineFrameIndex = -1;
            _decodeFailure = null;
            _decodeCompleted = false;
            LogDebug("OpenBikFileAsync", GetKeyframeSummary(_bikFile));
            StartCheckpointBuild(_bikFile);
            _isPaused = false;
            _isPlaying = false;
            _resumeFromPendingPosition = false;

            if (result.Item3.Yuv.Length > 0)
            {
                ShowBikFrame(result.Item3.Yuv, result.Item3.FrameIndex);
            }
            else
            {
                _previewImage.Source = null;
                _overlayTextBlock.Visibility = Visibility.Visible;
            }

            _infoTextBlock.Text = string.Format(
                "{0} | {1}x{2} | {3} frames | {4:0.###} fps | audio: {5}",
                Path.GetFileName(path),
                _bikFile.Width,
                _bikFile.Height,
                _bikFile.FrameIndex.Count,
                GetFramesPerSecond(_bikFile),
                _audioAvailable ? "yes" : "no");

            double durationSeconds = GetDurationSeconds();
            _durationTextBlock.Text = FormatTime(durationSeconds);
            UpdateTimelineFromFrame(Math.Max(result.Item3.FrameIndex, 0));
            SetReady(_audioAvailable
                ? "Ready. Audio is prepared and playback is available."
                : "Ready. This file was loaded without a playable audio track.");

            if (autoPlay)
            {
                await StartPlaybackAsync();
            }
        }
        catch (Exception ex)
        {
            _bikFile = null;
            _sourcePath = null;
            _audioAvailable = false;
            _previewImage.Source = null;
            _overlayTextBlock.Text = "Open a BIK video to preview and play it here.";
            _overlayTextBlock.Visibility = Visibility.Visible;
            _infoTextBlock.Text = "No BIK file loaded.";
            _positionTextBlock.Text = "00:00";
            _durationTextBlock.Text = "00:00";
            _timelineSlider.IsEnabled = false;
            _timelineSlider.Maximum = 1;
            _timelineSlider.Value = 0;
            await ShowMessageAsync("Bik Player", ex.Message);
            SetError("BIK load failed.");
        }
        finally
        {
            UpdatePlaybackUi();
        }
    }

    private async Task TogglePlaybackAsync()
    {
        LogDebug("TogglePlaybackAsync", "Entered toggle.");
        if (_bikFile == null)
        {
            await ShowMessageAsync("Bik Player", "Open a BIK file first.");
            return;
        }

        if (_isPlaying)
        {
            LogDebug("TogglePlaybackAsync", "Branch=Pause");
            PausePlayback();
            return;
        }

        if (_isPaused)
        {
            LogDebug("TogglePlaybackAsync", "Branch=Resume/Restart");
            await StartPlaybackAsync(GetPlaybackResumeFrameIndex());
            _resumeFromPendingPosition = false;
            return;
        }

        LogDebug("TogglePlaybackAsync", "Branch=Play");
        await StartPlaybackAsync(GetPlaybackResumeFrameIndex());
    }

    private async Task StartPlaybackAsync(int startFrameIndex = 0)
    {
        if (_bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return;
        }

        startFrameIndex = Math.Clamp(startFrameIndex, 0, _bikFile.FrameIndex.Count - 1);
        LogDebug("StartPlaybackAsync", "Requested start frame=" + startFrameIndex.ToString());
        PreparedPlaybackStart? preparedStart = TakePreparedPlaybackStart(startFrameIndex);
        if (preparedStart != null)
        {
            LogDebug("StartPlaybackAsync", "Using prepared start for frame=" + startFrameIndex.ToString());
        }
        StopPlayback(clearPreview: false);
        _decodeFailure = null;
        _decodeCompleted = false;
        _queuedFrameCount = 0;
        _queuedFrames = new ConcurrentQueue<BikQueuedFrame>();
        _decodeCancellation = new CancellationTokenSource();
        _isPlaying = true;
        _isPaused = false;
        _resumeFromPendingPosition = false;
        _displayedFrameIndex = Math.Max(startFrameIndex - 1, -1);
        _pendingStartFrameIndex = startFrameIndex;
        _playbackStartSeconds = startFrameIndex / Math.Max(GetFramesPerSecond(_bikFile), 1.0);
        _playbackClock.Restart();

        _playbackTimer.Interval = GetPlaybackPollInterval(_bikFile);
        _playbackTimer.Start();

        CancellationToken token = _decodeCancellation.Token;
        _decodeTask = Task.Run(() => DecodeFramesLoop(_bikFile, startFrameIndex, preparedStart, token), token);

        try
        {
            PlayAudioFromSeconds(_playbackStartSeconds);
        }
        catch (Exception ex)
        {
            _audioAvailable = false;
            SetError("Audio playback could not be started: " + ex.Message);
        }

        SetBusy("Playing", "Playback started.");
        UpdatePlaybackUi();
        OnPlaybackTimerTick(this, EventArgs.Empty);
        LogDebug("StartPlaybackAsync", "Playback started.");
        await Task.Yield();
    }

    private void PausePlayback()
    {
        if (!_isPlaying)
        {
            return;
        }

        _pendingStartFrameIndex = GetPlaybackResumeFrameIndex();
        _requestedTimelineFrameIndex = -1;
        LogDebug("PausePlayback", "Pending start frame set from current playback position.");
        AbortPlaybackForSeek();
        _isPaused = true;
        SetBusy("Paused", GetFrameStatusText("Paused"));
        UpdatePlaybackUi();
        LogDebug("PausePlayback", "Paused.");
    }

    private void AbortPlaybackForSeek()
    {
        LogDebug("AbortPlaybackForSeek", "Aborting active playback/decode.");
        _playbackTimer.Stop();
        _isPlaying = false;
        _playbackClock.Reset();
        StopAudio();

        CancellationTokenSource? cancellation = _decodeCancellation;
        _decodeCancellation = null;
        if (cancellation != null)
        {
            cancellation.Cancel();
        }

        try
        {
            _decodeTask?.Wait(200);
        }
        catch
        {
        }

        _decodeTask = null;
        if (_queuedFrames != null)
        {
            while (_queuedFrames.TryDequeue(out _))
            {
            }
        }

        _queuedFrames = null;
        _queuedFrameCount = 0;
        _decodeCompleted = false;
        _decodeFailure = null;
        DisposePreparedPlaybackStart();
    }

    private void StopPlayback(bool clearPreview)
    {
        LogDebug("StopPlayback", "clearPreview=" + clearPreview.ToString());
        _playbackTimer.Stop();
        _isPlaying = false;
        _isPaused = false;
        _playbackClock.Reset();
        StopAudio();

        CancellationTokenSource? cancellation = _decodeCancellation;
        _decodeCancellation = null;
        if (cancellation != null)
        {
            cancellation.Cancel();
        }

        try
        {
            _decodeTask?.Wait(200);
        }
        catch
        {
        }

        _decodeTask = null;
        if (_queuedFrames != null)
        {
            while (_queuedFrames.TryDequeue(out _))
            {
            }
        }

        _queuedFrames = null;
        _queuedFrameCount = 0;
        _decodeCompleted = false;
        _decodeFailure = null;
        DisposePreparedPlaybackStart();
        _requestedTimelineFrameIndex = -1;
        _timelinePointerActive = false;
        if (clearPreview)
        {
            _displayedFrameIndex = -1;
            _writeableBitmap = null;
            _previewImage.Source = null;
        }

        if (_bikFile == null)
        {
            _positionTextBlock.Text = "00:00";
            _timelineSlider.Value = 0;
        }
        else
        {
            UpdateTimelineFromFrame(Math.Max(_displayedFrameIndex, 0));
            SetReady("Playback stopped.");
        }
    }

    private void DecodeFramesLoop(BinkFile file, int requestedStartFrameIndex, PreparedPlaybackStart? preparedStart, CancellationToken cancellationToken)
    {
        try
        {
            using BinkSequentialPacketReader reader = preparedStart?.Reader ?? new BinkSequentialPacketReader(file);
            BinkVideoDecoder decoder = preparedStart?.Decoder ?? new BinkVideoDecoder(file);
            int firstFrameIndex = 0;
            if (preparedStart != null)
            {
                LogDebug("DecodeFramesLoop", "Prepared start enqueued frame=" + preparedStart.FirstFrame.FrameIndex.ToString() + ", nextFrame=" + preparedStart.NextFrameIndex.ToString());
                _queuedFrames!.Enqueue(preparedStart.FirstFrame);
                Interlocked.Increment(ref _queuedFrameCount);
                firstFrameIndex = preparedStart.NextFrameIndex;
            }

            for (int frameIndex = firstFrameIndex; frameIndex < file.FrameIndex.Count; frameIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (Volatile.Read(ref _queuedFrameCount) >= BikFrameQueueLimit)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.Sleep(10);
                }

                FramePacket packet = reader.ReadFramePacket(frameIndex);
                BinkDecodedVideoFrame decoded = decoder.Decode(packet);
                if (frameIndex < requestedStartFrameIndex)
                {
                    continue;
                }

                _queuedFrames!.Enqueue(new BikQueuedFrame
                {
                    FrameIndex = frameIndex,
                    Yuv = decoded.Yuv
                });
                Interlocked.Increment(ref _queuedFrameCount);
            }

            _decodeCompleted = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _decodeFailure = ex.Message;
            _decodeCompleted = true;
        }
    }

    private void OnPlaybackTimerTick(object? sender, object e)
    {
        if (!_isPlaying)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_decodeFailure))
        {
            string error = _decodeFailure!;
            StopPlayback(clearPreview: false);
            _ = ShowMessageAsync("Bik Player Decode Failed", error);
            UpdatePlaybackUi();
            return;
        }

        int targetFrameIndex = GetTargetFrameIndex();
        BikQueuedFrame? frameToShow = null;

        while (_queuedFrames != null &&
            _queuedFrames.TryPeek(out BikQueuedFrame? frame) &&
            frame.FrameIndex <= targetFrameIndex)
        {
            _queuedFrames.TryDequeue(out frameToShow);
            Interlocked.Decrement(ref _queuedFrameCount);
        }

        if (frameToShow != null)
        {
            ShowBikFrame(frameToShow.Yuv, frameToShow.FrameIndex);
            SetBusy("Playing", GetFrameStatusText("Playing"));
            return;
        }

        UpdateTimeDisplay(GetPlaybackSeconds());

        if (_bikFile != null && _decodeCompleted && _displayedFrameIndex >= _bikFile.FrameIndex.Count - 1 && !IsAudioStillPlaying())
        {
            StopPlayback(clearPreview: false);
            SetReady(GetFrameStatusText("Playback finished"));
            UpdatePlaybackUi();
            return;
        }
    }

    private void ShowBikFrame(byte[] yuv, int frameIndex)
    {
        if (_bikFile == null)
        {
            return;
        }

        int width = (int)_bikFile.Width;
        int height = (int)_bikFile.Height;
        WriteableBitmap bitmap = EnsureWriteableBitmap(width, height);
        byte[] pixels = ConvertBikYuvToBgra(yuv, width, height);

        using Stream pixelStream = bitmap.PixelBuffer.AsStream();
        pixelStream.Position = 0;
        pixelStream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();

        _previewImage.Source = bitmap;
        _overlayTextBlock.Visibility = Visibility.Collapsed;
        _displayedFrameIndex = frameIndex;
        _pendingStartFrameIndex = frameIndex;
        LogDebug("ShowBikFrame", "Displayed frame=" + frameIndex.ToString());
        UpdateTimelineFromFrame(frameIndex);
        if (_bikFile != null && !_isPlaying)
        {
            double frameSeconds = frameIndex / Math.Max(GetFramesPerSecond(_bikFile), 1.0);
            UpdateTimeDisplay(frameSeconds);
        }
        else
        {
            UpdateTimeDisplay(GetPlaybackSeconds());
        }
    }

    private static BikThumbnailData BuildThumbnailData(string path, CancellationToken cancellationToken, IProgress<ThumbnailBuildProgress>? progress)
    {
        BinkFile file = BinkFile.Load(path);
        if (file.FrameIndex.Count == 0)
        {
            return new BikThumbnailData
            {
                Width = 0,
                Height = 0,
                Pixels = Array.Empty<byte>(),
                OverlayText = "No frames",
                SummaryText = "Empty video stream"
            };
        }

        List<BikDecoderCheckpoint> checkpoints = BuildCheckpointList(file, cancellationToken, progress, out BikQueuedFrame thumbnailFrame);
        StoreFileCheckpointCacheStatic(path, checkpoints);
        byte[] bgraPixels = ConvertBikYuvToBgra(thumbnailFrame.Yuv, (int)file.Width, (int)file.Height);
        ScaledBitmapData scaled = ScaleBgraForThumbnail(bgraPixels, (int)file.Width, (int)file.Height, ThumbnailMaxWidth, ThumbnailMaxHeight);

        return new BikThumbnailData
        {
            Width = scaled.Width,
            Height = scaled.Height,
            Pixels = scaled.Pixels,
            OverlayText = string.Empty,
            SummaryText = string.Format(
                "{0}x{1} | {2} fps",
                file.Width,
                file.Height,
                GetFramesPerSecond(file).ToString("0.###"))
        };
    }

    private WriteableBitmap EnsureWriteableBitmap(int width, int height)
    {
        if (_writeableBitmap == null || _writeableBitmap.PixelWidth != width || _writeableBitmap.PixelHeight != height)
        {
            _writeableBitmap = new WriteableBitmap(width, height);
        }

        return _writeableBitmap;
    }

    private static WriteableBitmap CreateBitmapSource(int width, int height, byte[] pixels)
    {
        var bitmap = new WriteableBitmap(width, height);
        using Stream pixelStream = bitmap.PixelBuffer.AsStream();
        pixelStream.Position = 0;
        pixelStream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    private static BikQueuedFrame DecodeBikFrame(BinkFile file, int frameIndex)
    {
        using var reader = new BinkSequentialPacketReader(file);
        var decoder = new BinkVideoDecoder(file);
        for (int i = 0; i <= frameIndex; i++)
        {
            FramePacket packet = reader.ReadFramePacket(i);
            BinkDecodedVideoFrame decoded = decoder.Decode(packet);
            if (i == frameIndex)
            {
                return new BikQueuedFrame
                {
                    FrameIndex = i,
                    Yuv = decoded.Yuv
                };
            }
        }

        throw new InvalidOperationException("The requested frame could not be decoded.");
    }

    private static byte[] ConvertBikYuvToBgra(byte[] yuv, int width, int height)
    {
        int chromaWidth = (width + 1) >> 1;
        int yPlaneSize = width * height;
        int uvPlaneSize = chromaWidth * ((height + 1) >> 1);
        int uOffset = yPlaneSize;
        int vOffset = yPlaneSize + uvPlaneSize;
        byte[] pixels = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int yRow = y * width;
            int uvRow = (y >> 1) * chromaWidth;
            int pixelRow = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int ySample = yuv[yRow + x];
                int uvIndex = uvRow + (x >> 1);
                int uSample = yuv[uOffset + uvIndex];
                int vSample = yuv[vOffset + uvIndex];

                int c = ySample - 16;
                if (c < 0)
                {
                    c = 0;
                }

                int d = uSample - 128;
                int e = vSample - 128;
                int red = ClampToByte((298 * c + 409 * e + 128) >> 8);
                int green = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8);
                int blue = ClampToByte((298 * c + 516 * d + 128) >> 8);

                int pixelOffset = pixelRow + (x * 4);
                pixels[pixelOffset] = (byte)blue;
                pixels[pixelOffset + 1] = (byte)green;
                pixels[pixelOffset + 2] = (byte)red;
                pixels[pixelOffset + 3] = 255;
            }
        }

        return pixels;
    }

    private static ScaledBitmapData ScaleBgraForThumbnail(byte[] sourcePixels, int sourceWidth, int sourceHeight, int maxWidth, int maxHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourcePixels.Length == 0)
        {
            return new ScaledBitmapData { Width = 0, Height = 0, Pixels = Array.Empty<byte>() };
        }

        double scale = Math.Min((double)maxWidth / sourceWidth, (double)maxHeight / sourceHeight);
        if (scale > 1.0)
        {
            scale = 1.0;
        }

        int targetWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
        int targetHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
        byte[] targetPixels = new byte[targetWidth * targetHeight * 4];

        for (int y = 0; y < targetHeight; y++)
        {
            int sourceY = Math.Min(sourceHeight - 1, (int)(y / scale));
            for (int x = 0; x < targetWidth; x++)
            {
                int sourceX = Math.Min(sourceWidth - 1, (int)(x / scale));
                int sourceOffset = ((sourceY * sourceWidth) + sourceX) * 4;
                int targetOffset = ((y * targetWidth) + x) * 4;
                targetPixels[targetOffset] = sourcePixels[sourceOffset];
                targetPixels[targetOffset + 1] = sourcePixels[sourceOffset + 1];
                targetPixels[targetOffset + 2] = sourcePixels[sourceOffset + 2];
                targetPixels[targetOffset + 3] = sourcePixels[sourceOffset + 3];
            }
        }

        return new ScaledBitmapData
        {
            Width = targetWidth,
            Height = targetHeight,
            Pixels = targetPixels
        };
    }

    private static int ClampToByte(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > 255)
        {
            return 255;
        }

        return value;
    }

    private static double GetFramesPerSecond(BinkFile file)
    {
        if (file.FpsDenominator == 0)
        {
            return 0;
        }

        return (double)file.FpsNumerator / file.FpsDenominator;
    }

    private static TimeSpan GetPlaybackPollInterval(BinkFile file)
    {
        double fps = Math.Max(GetFramesPerSecond(file), 1.0);
        double frameDurationMs = 1000.0 / fps;

        // Poll more frequently than the frame duration so the next frame can be
        // presented close to its target time even when the UI timer jitters a bit.
        double pollMs = Math.Min(8.0, frameDurationMs / 3.0);
        if (pollMs < 4.0)
        {
            pollMs = 4.0;
        }

        return TimeSpan.FromMilliseconds(pollMs);
    }

    private int GetTargetFrameIndex()
    {
        if (_bikFile == null)
        {
            return 0;
        }

        double framesPerSecond = Math.Max(GetFramesPerSecond(_bikFile), 1.0);
        double elapsedSeconds = GetPlaybackSeconds();
        int target = (int)Math.Floor(elapsedSeconds * framesPerSecond);
        if (target < 0)
        {
            return 0;
        }

        if (target >= _bikFile.FrameIndex.Count)
        {
            return _bikFile.FrameIndex.Count - 1;
        }

        return target;
    }

    private double GetPlaybackSeconds()
    {
        if (_audioAvailable && TryGetAudioPositionMs(out long positionMs))
        {
            return positionMs / 1000.0;
        }

        return _playbackStartSeconds + _playbackClock.Elapsed.TotalSeconds;
    }

    private double GetDurationSeconds()
    {
        if (_bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return 0;
        }

        return _bikFile.FrameIndex.Count / Math.Max(GetFramesPerSecond(_bikFile), 1.0);
    }

    private string GetFrameStatusText(string prefix)
    {
        if (_bikFile == null)
        {
            return prefix;
        }

        int totalFrames = _bikFile.FrameIndex.Count;
        int displayedFrameIndex = GetUiFrameIndex();
        int displayed = displayedFrameIndex < 0 ? 0 : displayedFrameIndex + 1;
        return string.Format("{0}. Frame {1}/{2}.", prefix, displayed, totalFrames);
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0)
        {
            seconds = 0;
        }

        TimeSpan value = TimeSpan.FromSeconds(seconds);
        if (value.TotalHours >= 1)
        {
            return value.ToString(@"hh\:mm\:ss");
        }

        return value.ToString(@"mm\:ss");
    }

    private int GetPreferredStartFrameIndex()
    {
        if (_bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return 0;
        }

        if (_pendingStartFrameIndex >= 0)
        {
            return Math.Clamp(_pendingStartFrameIndex, 0, _bikFile.FrameIndex.Count - 1);
        }

        if (_displayedFrameIndex >= 0)
        {
            return Math.Clamp(_displayedFrameIndex, 0, _bikFile.FrameIndex.Count - 1);
        }

        return 0;
    }

    private int GetPlaybackResumeFrameIndex()
    {
        if (_bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return 0;
        }

        if (_pendingStartFrameIndex >= 0)
        {
            return Math.Clamp(_pendingStartFrameIndex, 0, _bikFile.FrameIndex.Count - 1);
        }

        if (_displayedFrameIndex >= 0)
        {
            return Math.Clamp(_displayedFrameIndex, 0, _bikFile.FrameIndex.Count - 1);
        }

        return 0;
    }

    private int GetUiFrameIndex()
    {
        if (_bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return -1;
        }

        if (!_isPlaying)
        {
            return GetPreferredStartFrameIndex();
        }

        if (_displayedFrameIndex >= 0)
        {
            return Math.Clamp(_displayedFrameIndex, 0, _bikFile.FrameIndex.Count - 1);
        }

        return GetPreferredStartFrameIndex();
    }

    private void UpdateTimelineFromFrame(int frameIndex)
    {
        if (_bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            _timelineSlider.IsEnabled = false;
            _timelineSlider.Maximum = 1;
            _timelineSlider.Value = 0;
            return;
        }

        _timelineSlider.IsEnabled = true;
        _timelineSlider.Maximum = Math.Max(_bikFile.FrameIndex.Count - 1, 1);

        _updatingTimeline = true;
        try
        {
            _timelineSlider.Value = Math.Max(0, Math.Min(frameIndex, _bikFile.FrameIndex.Count - 1));
            LogDebug("UpdateTimelineFromFrame", "Slider value programmatically set to frame=" + frameIndex.ToString());
        }
        finally
        {
            _updatingTimeline = false;
        }
    }

    private void UpdateTimeDisplay(double playbackSeconds)
    {
        int frameIndex = GetUiFrameIndex();
        string frameText = _bikFile == null || frameIndex < 0
            ? "F0"
            : "F" + (frameIndex + 1).ToString();
        _positionTextBlock.Text = FormatTime(playbackSeconds) + " | " + frameText;
        _durationTextBlock.Text = FormatTime(GetDurationSeconds());
    }

    private void UpdatePlaybackUi()
    {
        bool hasVideo = _bikFile != null && _bikFile.FrameIndex.Count > 0;
        _playPauseButton.IsEnabled = hasVideo;
        _stopButton.IsEnabled = hasVideo && (_isPlaying || _isPaused || _displayedFrameIndex >= 0);
        _exportAviButton.IsEnabled = hasVideo;
        _exportMp4Button.IsEnabled = hasVideo;
        UpdateThumbnailBrowserVisibility();

        if (_isPlaying)
        {
            _playPauseButton.Content = "Pause";
        }
        else if (_isPaused)
        {
            _playPauseButton.Content = "Resume";
        }
        else
        {
            _playPauseButton.Content = "Play";
        }

        if (!hasVideo)
        {
            _overlayTextBlock.Visibility = Visibility.Visible;
            _overlayTextBlock.Text = "Open a BIK video to preview and play it here.";
        }
    }

    private void UpdateThumbnailBrowserVisibility()
    {
        bool shouldShow = _thumbnailItems.Count > 0 && !_isPlaying && !_isTimelineSeekInProgress;
        _thumbnailBrowserBorder.Visibility = shouldShow ? Visibility.Visible : Visibility.Collapsed;
        if (string.IsNullOrWhiteSpace(_thumbnailFolderPath))
        {
            _thumbnailHeaderTextBlock.Text = "Folder previews";
        }
        else
        {
            _thumbnailHeaderTextBlock.Text = $"Folder previews | {Path.GetFileName(_thumbnailFolderPath)}";
        }
    }

    private string? BuildBikAudioWaveFile(BinkFile file, string sourceFileName)
    {
        if (file.AudioTracks.Count == 0)
        {
            return null;
        }

        AudioTrackInfo track = file.AudioTracks[0];
        using var audioReader = new BinkSequentialPacketReader(file);
        var decoder = new BinkRdfAudioDecoder(track);
        int channels = track.IsStereo ? 2 : 1;
        var samples = new System.Collections.Generic.List<float>(track.MaxDecodedSize > 0 ? (int)track.MaxDecodedSize : 16384);

        for (int frameIndex = 0; frameIndex < file.FrameIndex.Count; frameIndex++)
        {
            FramePacket packet = audioReader.ReadFramePacket(frameIndex);
            foreach (AudioPacket audioPacket in packet.AudioPackets)
            {
                if (audioPacket.Payload.Length == 0)
                {
                    continue;
                }

                float[] decoded = decoder.DecodePacket(audioPacket.Payload);
                samples.AddRange(decoded);
            }
        }

        if (samples.Count == 0)
        {
            return null;
        }

        byte[] wavBytes = BuildWaveFileBytes(samples.ToArray(), track.SampleRate, channels);
        string tempPath = Path.Combine(
            Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(sourceFileName) + "_" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(tempPath, wavBytes);
        return tempPath;
    }

    private static byte[] BuildWaveFileBytes(float[] samples, int sampleRate, int channels)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        int bitsPerSample = 16;
        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;
        int dataLength = samples.Length * 2;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = samples[i];
            if (clamped < -1f)
            {
                clamped = -1f;
            }
            else if (clamped > 1f)
            {
                clamped = 1f;
            }

            short pcm = (short)Math.Round(clamped * short.MaxValue);
            writer.Write(pcm);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private void PlayAudioFromSeconds(double startSeconds)
    {
        if (!_audioAvailable || string.IsNullOrWhiteSpace(_audioTempPath) || !File.Exists(_audioTempPath))
        {
            return;
        }

        long startMs = Math.Max(0, (long)Math.Round(startSeconds * 1000.0));
        ExecuteAudioCommand("close " + BikAudioAlias, throwOnError: false);
        ExecuteAudioCommand("open \"" + _audioTempPath + "\" type waveaudio alias " + BikAudioAlias);
        ExecuteAudioCommand("set " + BikAudioAlias + " time format milliseconds");
        ExecuteAudioCommand("play " + BikAudioAlias + " from " + startMs.ToString());
    }

    private void PauseAudio()
    {
        if (_audioAvailable)
        {
            ExecuteAudioCommand("pause " + BikAudioAlias, throwOnError: false);
        }
    }

    private void ResumeAudio()
    {
        if (_audioAvailable)
        {
            ExecuteAudioCommand("resume " + BikAudioAlias, throwOnError: false);
        }
    }

    private void StopAudio()
    {
        if (_audioAvailable)
        {
            ExecuteAudioCommand("stop " + BikAudioAlias, throwOnError: false);
            ExecuteAudioCommand("close " + BikAudioAlias, throwOnError: false);
        }
    }

    private bool TryGetAudioPositionMs(out long positionMs)
    {
        positionMs = 0;
        if (!_audioAvailable)
        {
            return false;
        }

        var output = new StringBuilder(64);
        int result = mciSendString("status " + BikAudioAlias + " position", output, output.Capacity, IntPtr.Zero);
        return result == 0 && long.TryParse(output.ToString(), out positionMs);
    }

    private bool IsAudioStillPlaying()
    {
        if (!_audioAvailable)
        {
            return _isPlaying;
        }

        var output = new StringBuilder(32);
        int result = mciSendString("status " + BikAudioAlias + " mode", output, output.Capacity, IntPtr.Zero);
        if (result != 0)
        {
            return false;
        }

        string mode = output.ToString().Trim();
        return string.Equals(mode, "playing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "paused", StringComparison.OrdinalIgnoreCase);
    }

    private static void ExecuteAudioCommand(string command, bool throwOnError = true)
    {
        int result = mciSendString(command, null, 0, IntPtr.Zero);
        if (result != 0 && throwOnError)
        {
            throw new InvalidOperationException("Audio command failed: " + command);
        }
    }

    private void CleanupAudioTempFile()
    {
        StopAudio();
        if (!string.IsNullOrWhiteSpace(_audioTempPath))
        {
            try
            {
                if (File.Exists(_audioTempPath))
                {
                    File.Delete(_audioTempPath);
                }
            }
            catch
            {
            }

            _audioTempPath = null;
        }

        _audioAvailable = false;
    }

    private static string BuildExceptionDetails(Exception ex)
    {
        if (ex == null)
        {
            return "Unknown error.";
        }

        var message = new StringBuilder(ex.ToString());
        Exception? inner = ex.InnerException;
        while (inner != null)
        {
            message.AppendLine();
            message.AppendLine();
            message.AppendLine("Inner Exception:");
            message.Append(inner);
            inner = inner.InnerException;
        }

        return message.ToString();
    }

    private async Task RunExportWithProgressDialogAsync(
        string title,
        string message,
        Action<IProgress<ExportProgressInfo>> exportAction)
    {
        var progressBar = new ProgressBar
        {
            IsIndeterminate = false,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 8
        };
        var progressTextBlock = new TextBlock
        {
            Text = "0 %",
            Margin = new Thickness(0, 10, 0, 0)
        };
        var detailTextBlock = new TextBlock
        {
            Text = message,
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap
        };
        var dialogContent = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                progressBar,
                progressTextBlock,
                detailTextBlock
            }
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = dialogContent,
            XamlRoot = XamlRoot,
            CloseButtonText = string.Empty,
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false
        };

        Progress<ExportProgressInfo> progress = new(update =>
        {
            double clampedPercent = Math.Clamp(update.Percent, 0, 100);
            progressBar.Value = clampedPercent;
            progressTextBlock.Text = $"{clampedPercent:0} %";
            detailTextBlock.Text = update.StageText;
        });

        var showDialogTask = dialog.ShowAsync().AsTask();

        try
        {
            await Task.Yield();
            await Task.Run(() => exportAction(progress));
        }
        finally
        {
            dialog.Hide();
            await showDialogTask;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void SetBusy(string title, string message)
    {
        _statusInfoBar.Severity = InfoBarSeverity.Informational;
        _statusInfoBar.Title = title;
        _statusInfoBar.Message = message;
    }

    private void SetReady(string message)
    {
        _statusInfoBar.Severity = InfoBarSeverity.Informational;
        _statusInfoBar.Title = "Ready";
        _statusInfoBar.Message = message;
    }

    private void SetError(string message)
    {
        _statusInfoBar.Severity = InfoBarSeverity.Error;
        _statusInfoBar.Title = "Error";
        _statusInfoBar.Message = message;
    }

    private void TimelineSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_updatingTimeline || _bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return;
        }

        _resumePlaybackAfterTimelineSeek = _isPlaying;
        _resumeFromPendingPosition = _isPaused;
        _timelinePointerActive = true;
        _isTimelineSeekInProgress = true;
        UpdateThumbnailBrowserVisibility();
        _requestedTimelineFrameIndex = (int)Math.Round(_timelineSlider.Value);
        LogDebug("TimelineSlider_PointerPressed", "Pointer pressed. requestedFrame=" + _requestedTimelineFrameIndex.ToString());
        CancelScheduledTimelineSeek();
        _seekRequestVersion++;
        if (_isPlaying)
        {
            AbortPlaybackForSeek();
            UpdatePlaybackUi();
        }
    }

    private void TimelineSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingTimeline || _bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            return;
        }

        if (!_timelinePointerActive)
        {
            _resumePlaybackAfterTimelineSeek = _isPlaying;
            _resumeFromPendingPosition = _isPaused;
            _timelinePointerActive = true;
            _isTimelineSeekInProgress = true;
            UpdateThumbnailBrowserVisibility();
            CancelScheduledTimelineSeek();
            _seekRequestVersion++;
            LogDebug("TimelineSlider_ValueChanged", "Implicit user seek activation.");
            if (_isPlaying)
            {
                AbortPlaybackForSeek();
                UpdatePlaybackUi();
            }
        }

        int targetFrame = (int)Math.Round(e.NewValue);
        _requestedTimelineFrameIndex = targetFrame;
        _pendingStartFrameIndex = targetFrame;
        double previewSeconds = targetFrame / Math.Max(GetFramesPerSecond(_bikFile), 1.0);
        UpdateTimeDisplay(previewSeconds);
        LogDebug("TimelineSlider_ValueChanged", "User selected frame=" + targetFrame.ToString());
        ScheduleTimelineSeek();
    }

    private async void TimelineSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        await CommitTimelineSeekAsync();
    }

    private async void TimelineSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        await CommitTimelineSeekAsync();
    }

    private async Task CommitTimelineSeekAsync()
    {
        if (!_timelinePointerActive)
        {
            return;
        }

        if (_updatingTimeline || _bikFile == null || _bikFile.FrameIndex.Count == 0)
        {
            _timelinePointerActive = false;
            _isTimelineSeekInProgress = false;
            UpdateThumbnailBrowserVisibility();
            return;
        }

        CancelScheduledTimelineSeek();
        await Task.Yield();
        _requestedTimelineFrameIndex = (int)Math.Round(_timelineSlider.Value);
        int targetFrame = _requestedTimelineFrameIndex;
        _timelinePointerActive = false;
        bool resumePlayback = _resumePlaybackAfterTimelineSeek;
        _resumePlaybackAfterTimelineSeek = false;
        _seekRequestVersion++;
        LogDebug("CommitTimelineSeekAsync", "Commit targetFrame=" + targetFrame.ToString() + ", resumePlayback=" + resumePlayback.ToString());
        try
        {
            await SeekToFrameAsync(targetFrame, resumePlayback);
        }
        finally
        {
            _isTimelineSeekInProgress = false;
            UpdateThumbnailBrowserVisibility();
        }
    }

    private void ScheduleTimelineSeek()
    {
        CancelScheduledTimelineSeek();
        CancellationTokenSource cancellation = new();
        _timelineSeekCancellation = cancellation;
        LogDebug("ScheduleTimelineSeek", "Debounced seek scheduled for requestedFrame=" + _requestedTimelineFrameIndex.ToString());
        _ = DebouncedTimelineSeekAsync(cancellation.Token);
    }

    private void CancelScheduledTimelineSeek()
    {
        CancellationTokenSource? cancellation = _timelineSeekCancellation;
        _timelineSeekCancellation = null;
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task DebouncedTimelineSeekAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !_timelinePointerActive || _bikFile == null)
            {
                return;
            }

            int targetFrame = _requestedTimelineFrameIndex >= 0
                ? _requestedTimelineFrameIndex
                : (int)Math.Round(_timelineSlider.Value);
            _seekRequestVersion++;
            LogDebug("DebouncedTimelineSeekAsync", "Preview targetFrame=" + targetFrame.ToString());
            await SeekToFrameAsync(targetFrame, resumePlayback: false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SeekToFrameAsync(int frameIndex, bool resumePlayback)
    {
        if (_bikFile == null)
        {
            return;
        }

        int requestVersion = _seekRequestVersion;
        try
        {
            _pendingStartFrameIndex = frameIndex;
            _requestedTimelineFrameIndex = frameIndex;
            if (_bikFile != null)
            {
                double previewSeconds = frameIndex / Math.Max(GetFramesPerSecond(_bikFile), 1.0);
                UpdateTimeDisplay(previewSeconds);
            }
            LogDebug("SeekToFrameAsync", "Begin frame=" + frameIndex.ToString() + ", resumePlayback=" + resumePlayback.ToString() + ", requestVersion=" + requestVersion.ToString());
            SetBusy("Seeking", "Decoding requested frame...");
            BikDecoderCheckpoint? checkpoint = GetNearestDecoderCheckpoint(frameIndex);
            int seekStartFrameIndex = checkpoint?.FrameIndex ?? GetNearestSeekStartFrameIndex(_bikFile, frameIndex);
            string seekStrategy = checkpoint != null
                ? "synthetic-checkpoint"
                : "sequential-from-keyframe";
            LogDebug("SeekToFrameAsync", "Decoding from seek start frame=" + seekStartFrameIndex.ToString() + " strategy=" + seekStrategy);
            PreparedPlaybackStart preparedStart = await Task.Run(() => PreparePlaybackStart(_bikFile, frameIndex));
            if (requestVersion != _seekRequestVersion)
            {
                preparedStart.Dispose();
                LogDebug("SeekToFrameAsync", "Ignored stale result for frame=" + frameIndex.ToString());
                return;
            }

            DisposePreparedPlaybackStart();
            _preparedPlaybackStart = preparedStart;
            BikQueuedFrame frame = preparedStart.FirstFrame;
            ShowBikFrame(frame.Yuv, frame.FrameIndex);
            _pendingStartFrameIndex = frame.FrameIndex;
            _requestedTimelineFrameIndex = frame.FrameIndex;
            _resumeFromPendingPosition = !resumePlayback;
            SetReady(resumePlayback ? "Seek completed." : "Preview updated.");
            LogDebug("SeekToFrameAsync", "Applied frame=" + frame.FrameIndex.ToString() + ", resumePlayback=" + resumePlayback.ToString());
            if (resumePlayback)
            {
                await StartPlaybackAsync(frame.FrameIndex);
                _requestedTimelineFrameIndex = -1;
            }
            else if (_isPaused)
            {
                _resumeFromPendingPosition = true;
                LogDebug("SeekToFrameAsync", "Paused seek preserved for resume at frame=" + frame.FrameIndex.ToString());
            }
            else if (!_timelinePointerActive)
            {
                _requestedTimelineFrameIndex = -1;
            }
        }
        catch (Exception ex)
        {
            LogDebug("SeekToFrameAsync", "Exception: " + ex.Message);
            SetError("Seeking failed.");
            await ShowMessageAsync("Bik Player", ex.Message);
        }
        finally
        {
        }
    }

    private void LogDebug(string eventName, string message)
    {
        _ = eventName;
        _ = message;
    }

    private void ReleaseResourcesForShutdown()
    {
        CancelScheduledTimelineSeek();
        CancelThumbnailLoading();
        CancelCheckpointBuild();
        ClearDecoderCheckpoints();
        StopPlayback(clearPreview: true);
        CleanupAudioTempFile();
    }

    private PreparedPlaybackStart? TakePreparedPlaybackStart(int startFrameIndex)
    {
        PreparedPlaybackStart? prepared = _preparedPlaybackStart;
        if (prepared == null)
        {
            return null;
        }

        _preparedPlaybackStart = null;
        if (prepared.FirstFrame.FrameIndex != startFrameIndex)
        {
            prepared.Dispose();
            return null;
        }

        return prepared;
    }

    private void DisposePreparedPlaybackStart()
    {
        PreparedPlaybackStart? prepared = _preparedPlaybackStart;
        _preparedPlaybackStart = null;
        prepared?.Dispose();
    }

    private void StartCheckpointBuild(BinkFile file)
    {
        CancelCheckpointBuild();
        ClearDecoderCheckpoints();
        if (TryLoadCheckpointCacheStatic(file.FilePath, out List<BikDecoderCheckpoint>? staticCachedCheckpoints) && staticCachedCheckpoints != null)
        {
            ApplyDecoderCheckpoints(staticCachedCheckpoints);
            StoreFileCheckpointCache(file.FilePath, staticCachedCheckpoints);
            LogDebug("CheckpointBuild", "Loaded static cached checkpoints for active file. " + GetCheckpointSummary());
            return;
        }

        if (TryLoadCheckpointCache(file.FilePath, out List<BikDecoderCheckpoint>? cachedCheckpoints))
        {
            ApplyDecoderCheckpoints(cachedCheckpoints);
            LogDebug("CheckpointBuild", "Loaded cached checkpoints for active file. " + GetCheckpointSummary());
            return;
        }

        CancellationTokenSource cancellation = new();
        _checkpointBuildCancellation = cancellation;
        int buildVersion = ++_checkpointBuildVersion;
        _ = Task.Run(() => BuildDecoderCheckpoints(file, buildVersion, cancellation.Token), cancellation.Token);
    }

    private void CancelCheckpointBuild()
    {
        CancellationTokenSource? cancellation = _checkpointBuildCancellation;
        _checkpointBuildCancellation = null;
        if (cancellation == null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void ClearDecoderCheckpoints()
    {
        lock (_checkpointSync)
        {
            _decoderCheckpoints = new List<BikDecoderCheckpoint>();
        }
    }

    private void ApplyDecoderCheckpoints(List<BikDecoderCheckpoint> checkpoints)
    {
        lock (_checkpointSync)
        {
            _decoderCheckpoints = CloneCheckpointList(checkpoints);
        }
    }

    private void BuildDecoderCheckpoints(BinkFile file, int buildVersion, CancellationToken cancellationToken)
    {
        try
        {
            List<BikDecoderCheckpoint> checkpoints = BuildCheckpointList(file, cancellationToken);
            if (buildVersion != _checkpointBuildVersion)
            {
                return;
            }

            lock (_checkpointSync)
            {
                _decoderCheckpoints = CloneCheckpointList(checkpoints);
            }

            if (buildVersion == _checkpointBuildVersion)
            {
                StoreFileCheckpointCache(file.FilePath, checkpoints);
                LogDebug("CheckpointBuild", "Completed. " + GetCheckpointSummary());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (buildVersion == _checkpointBuildVersion)
            {
                LogDebug("CheckpointBuild", "Failed: " + ex.Message);
            }
        }
    }

    private void TryStoreDecoderCheckpoint(int frameIndex, byte[] state)
    {
        lock (_checkpointSync)
        {
            if (_decoderCheckpoints.Any(checkpoint => checkpoint.FrameIndex == frameIndex))
            {
                return;
            }

            _decoderCheckpoints.Add(new BikDecoderCheckpoint(frameIndex, state));
            _decoderCheckpoints.Sort((left, right) => left.FrameIndex.CompareTo(right.FrameIndex));
        }
    }

    private bool HasCachedCheckpoints(string path)
    {
        lock (_checkpointSync)
        {
            return _fileCheckpointCache.ContainsKey(path);
        }
    }

    private bool TryLoadCheckpointCache(string path, out List<BikDecoderCheckpoint>? checkpoints)
    {
        lock (_checkpointSync)
        {
            if (_fileCheckpointCache.TryGetValue(path, out List<BikDecoderCheckpoint>? cached))
            {
                checkpoints = CloneCheckpointList(cached);
                return true;
            }
        }

        checkpoints = null;
        return false;
    }

    private void StoreFileCheckpointCache(string path, List<BikDecoderCheckpoint> checkpoints)
    {
        lock (_checkpointSync)
        {
            _fileCheckpointCache[path] = CloneCheckpointList(checkpoints);
        }
    }

    private static readonly object s_checkpointCacheSync = new();
    private static readonly Dictionary<string, List<BikDecoderCheckpoint>> s_fileCheckpointCache = new(StringComparer.OrdinalIgnoreCase);

    private static void StoreFileCheckpointCacheStatic(string path, List<BikDecoderCheckpoint> checkpoints)
    {
        lock (s_checkpointCacheSync)
        {
            s_fileCheckpointCache[path] = CloneCheckpointList(checkpoints);
        }
    }

    private static bool TryLoadCheckpointCacheStatic(string path, out List<BikDecoderCheckpoint>? checkpoints)
    {
        lock (s_checkpointCacheSync)
        {
            if (s_fileCheckpointCache.TryGetValue(path, out List<BikDecoderCheckpoint>? cached))
            {
                checkpoints = CloneCheckpointList(cached);
                return true;
            }
        }

        checkpoints = null;
        return false;
    }

    private List<BikDecoderCheckpoint> GetOrBuildCheckpointCache(string path, CancellationToken cancellationToken)
    {
        if (TryLoadCheckpointCacheStatic(path, out List<BikDecoderCheckpoint>? staticCachedCheckpoints) && staticCachedCheckpoints != null)
        {
            StoreFileCheckpointCache(path, staticCachedCheckpoints);
            return staticCachedCheckpoints;
        }

        if (TryLoadCheckpointCache(path, out List<BikDecoderCheckpoint>? cachedCheckpoints) && cachedCheckpoints != null)
        {
            return cachedCheckpoints;
        }

        List<BikDecoderCheckpoint> builtCheckpoints = BuildCheckpointListForPath(path, cancellationToken);
        StoreFileCheckpointCache(path, builtCheckpoints);
        StoreFileCheckpointCacheStatic(path, builtCheckpoints);
        return CloneCheckpointList(builtCheckpoints);
    }

    private List<BikDecoderCheckpoint> GetOrBuildCheckpointCache(string path, BinkFile file)
    {
        if (TryLoadCheckpointCacheStatic(path, out List<BikDecoderCheckpoint>? staticCachedCheckpoints) && staticCachedCheckpoints != null)
        {
            StoreFileCheckpointCache(path, staticCachedCheckpoints);
            return staticCachedCheckpoints;
        }

        if (TryLoadCheckpointCache(path, out List<BikDecoderCheckpoint>? cachedCheckpoints) && cachedCheckpoints != null)
        {
            return cachedCheckpoints;
        }

        List<BikDecoderCheckpoint> builtCheckpoints = BuildCheckpointList(file, CancellationToken.None);
        StoreFileCheckpointCache(path, builtCheckpoints);
        StoreFileCheckpointCacheStatic(path, builtCheckpoints);
        return CloneCheckpointList(builtCheckpoints);
    }

    private List<BikDecoderCheckpoint> SnapshotActiveCheckpoints()
    {
        lock (_checkpointSync)
        {
            return CloneCheckpointList(_decoderCheckpoints);
        }
    }

    private BikDecoderCheckpoint? GetNearestDecoderCheckpoint(int targetFrameIndex)
    {
        lock (_checkpointSync)
        {
            BikDecoderCheckpoint? best = null;
            for (int i = 0; i < _decoderCheckpoints.Count; i++)
            {
                BikDecoderCheckpoint checkpoint = _decoderCheckpoints[i];
                if (checkpoint.FrameIndex > targetFrameIndex)
                {
                    break;
                }

                best = checkpoint;
            }

            return best;
        }
    }

    private string GetCheckpointSummary()
    {
        lock (_checkpointSync)
        {
            string preview = string.Join(",", _decoderCheckpoints.Select(checkpoint => checkpoint.FrameIndex).Take(20));
            return $"Synthetic checkpoints={_decoderCheckpoints.Count} first=[{preview}]";
        }
    }

    private static bool ShouldStoreCheckpoint(int frameIndex, int frameCount, int intervalFrames)
    {
        return frameIndex == 0 ||
            frameIndex == frameCount - 1 ||
            (frameIndex % intervalFrames) == 0;
    }

    private static int GetCheckpointIntervalFrames(BinkFile file)
    {
        return Math.Max(1, (int)Math.Round(GetFramesPerSecond(file) * SeekCheckpointIntervalSeconds, MidpointRounding.AwayFromZero));
    }

    private PreparedPlaybackStart PreparePlaybackStart(BinkFile file, int frameIndex)
    {
        BinkSequentialPacketReader reader = new(file);
        try
        {
            BikDecoderCheckpoint? checkpoint = GetNearestDecoderCheckpoint(frameIndex);
            int startFrameIndex = checkpoint?.FrameIndex + 1 ?? 0;
            var decoder = checkpoint != null
                ? new BinkVideoDecoder(file, checkpoint.State)
                : new BinkVideoDecoder(file);

            if (checkpoint != null && checkpoint.FrameIndex == frameIndex)
            {
                return new PreparedPlaybackStart(
                    reader,
                    decoder,
                    frameIndex + 1,
                    new BikQueuedFrame
                    {
                        FrameIndex = frameIndex,
                        Yuv = checkpoint.State.ToArray()
                    });
            }

            for (int currentFrameIndex = startFrameIndex; currentFrameIndex <= frameIndex; currentFrameIndex++)
            {
                FramePacket packet = reader.ReadFramePacket(currentFrameIndex);
                BinkDecodedVideoFrame decoded = decoder.Decode(packet);
                if (ShouldStoreCheckpoint(currentFrameIndex, file.FrameIndex.Count, GetCheckpointIntervalFrames(file)))
                {
                    TryStoreDecoderCheckpoint(currentFrameIndex, decoder.CaptureReferenceFrameData());
                }

                if (currentFrameIndex == frameIndex)
                {
                    return new PreparedPlaybackStart(
                        reader,
                        decoder,
                        currentFrameIndex + 1,
                        new BikQueuedFrame
                        {
                            FrameIndex = currentFrameIndex,
                            Yuv = decoded.Yuv
                        });
                }
            }

            throw new InvalidOperationException("Could not decode requested BIK frame.");
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private static int GetNearestSeekStartFrameIndex(BinkFile file, int targetFrameIndex)
    {
        int safeTargetFrameIndex = Math.Clamp(targetFrameIndex, 0, file.FrameIndex.Count - 1);
        for (int frameIndex = safeTargetFrameIndex; frameIndex >= 0; frameIndex--)
        {
            if (file.FrameIndex[frameIndex].IsKeyframe)
            {
                return frameIndex;
            }
        }

        return 0;
    }

    private static string GetKeyframeSummary(BinkFile file)
    {
        List<int> keyframes = new();
        for (int frameIndex = 0; frameIndex < file.FrameIndex.Count; frameIndex++)
        {
            if (file.FrameIndex[frameIndex].IsKeyframe)
            {
                keyframes.Add(frameIndex);
            }
        }

        string preview = string.Join(",", keyframes.Take(20));
        return $"Keyframes={keyframes.Count} first=[{preview}]";
    }

    private static List<BikDecoderCheckpoint> BuildCheckpointListForPath(string path, CancellationToken cancellationToken)
    {
        BinkFile file = BinkFile.Load(path);
        return BuildCheckpointList(file, cancellationToken);
    }

    private static List<BikDecoderCheckpoint> BuildCheckpointList(BinkFile file, CancellationToken cancellationToken)
    {
        return BuildCheckpointList(file, cancellationToken, progress: null, out _);
    }

    private static List<BikDecoderCheckpoint> BuildCheckpointList(
        BinkFile file,
        CancellationToken cancellationToken,
        IProgress<ThumbnailBuildProgress>? progress,
        out BikQueuedFrame thumbnailFrame)
    {
        int checkpointIntervalFrames = GetCheckpointIntervalFrames(file);
        int targetFrameIndex = Math.Clamp((int)Math.Round((file.FrameIndex.Count - 1) * (ThumbnailTargetFrameSamplePercent / 100.0)), 0, file.FrameIndex.Count - 1);
        using var reader = new BinkSequentialPacketReader(file);
        var decoder = new BinkVideoDecoder(file);
        List<BikDecoderCheckpoint> checkpoints = new();
        BikQueuedFrame? bestThumbnailFrame = null;
        int bestThumbnailDistance = int.MaxValue;
        int lastReportedPercent = -1;
        for (int frameIndex = 0; frameIndex < file.FrameIndex.Count; frameIndex++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            FramePacket packet = reader.ReadFramePacket(frameIndex);
            BinkDecodedVideoFrame decoded = decoder.Decode(packet);
            int percent = (int)Math.Round(((frameIndex + 1) * 100.0) / Math.Max(file.FrameIndex.Count, 1), MidpointRounding.AwayFromZero);
            if (progress != null && percent != lastReportedPercent)
            {
                lastReportedPercent = percent;
                progress.Report(new ThumbnailBuildProgress
                {
                    IsLoading = percent < 100,
                    OverlayText = string.Empty,
                    Width = 0,
                    Height = 0,
                    PreviewPixels = Array.Empty<byte>()
                });
            }

            if (ShouldStoreCheckpoint(frameIndex, file.FrameIndex.Count, checkpointIntervalFrames))
            {
                checkpoints.Add(new BikDecoderCheckpoint(frameIndex, decoder.CaptureReferenceFrameData()));
                int distance = Math.Abs(frameIndex - targetFrameIndex);
                if (distance < bestThumbnailDistance)
                {
                    bestThumbnailDistance = distance;
                    bestThumbnailFrame = new BikQueuedFrame
                    {
                        FrameIndex = frameIndex,
                        Yuv = decoded.Yuv.ToArray()
                    };

                    if (progress != null)
                    {
                        byte[] bgraPixels = ConvertBikYuvToBgra(bestThumbnailFrame.Yuv, (int)file.Width, (int)file.Height);
                        ScaledBitmapData scaled = ScaleBgraForThumbnail(bgraPixels, (int)file.Width, (int)file.Height, ThumbnailMaxWidth, ThumbnailMaxHeight);
                        progress.Report(new ThumbnailBuildProgress
                        {
                            IsLoading = percent < 100,
                            OverlayText = string.Empty,
                            Width = scaled.Width,
                            Height = scaled.Height,
                            PreviewPixels = scaled.Pixels
                        });
                    }
                }
            }
        }

        thumbnailFrame = bestThumbnailFrame ?? new BikQueuedFrame
        {
            FrameIndex = 0,
            Yuv = DecodeBikFrame(file, 0).Yuv
        };

        return checkpoints;
    }

    private static List<BikDecoderCheckpoint> CloneCheckpointList(IEnumerable<BikDecoderCheckpoint> checkpoints)
    {
        return checkpoints
            .Select(checkpoint => new BikDecoderCheckpoint(checkpoint.FrameIndex, checkpoint.State.ToArray()))
            .ToList();
    }

    private sealed class BikQueuedFrame
    {
        public int FrameIndex { get; set; }
        public byte[] Yuv { get; set; } = Array.Empty<byte>();
    }

    private sealed class PreparedPlaybackStart : IDisposable
    {
        public PreparedPlaybackStart(BinkSequentialPacketReader reader, BinkVideoDecoder decoder, int nextFrameIndex, BikQueuedFrame firstFrame)
        {
            Reader = reader;
            Decoder = decoder;
            NextFrameIndex = nextFrameIndex;
            FirstFrame = firstFrame;
        }

        public BinkSequentialPacketReader Reader { get; }
        public BinkVideoDecoder Decoder { get; }
        public int NextFrameIndex { get; }
        public BikQueuedFrame FirstFrame { get; }

        public void Dispose()
        {
            Reader.Dispose();
        }
    }

    private sealed class BikDecoderCheckpoint
    {
        public BikDecoderCheckpoint(int frameIndex, byte[] state)
        {
            FrameIndex = frameIndex;
            State = state;
        }

        public int FrameIndex { get; }
        public byte[] State { get; }
    }

    private sealed class BikThumbnailItem : INotifyPropertyChanged
    {
        public required string FilePath { get; init; }
        public required string DisplayName { get; init; }
        private string _summaryText = string.Empty;
        private string _overlayText = string.Empty;
        private WriteableBitmap? _previewImage;
        private bool _isLoading;

        public required string SummaryText
        {
            get => _summaryText;
            set
            {
                if (_summaryText == value)
                {
                    return;
                }

                _summaryText = value;
                OnPropertyChanged();
            }
        }

        public required string OverlayText
        {
            get => _overlayText;
            set
            {
                if (_overlayText == value)
                {
                    return;
                }

                _overlayText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OverlayVisibility));
            }
        }

        public WriteableBitmap? PreviewImage
        {
            get => _previewImage;
            set
            {
                if (ReferenceEquals(_previewImage, value))
                {
                    return;
                }

                _previewImage = value;
                OnPropertyChanged();
            }
        }

        public required bool IsLoading
        {
            get => _isLoading;
            set
            {
                if (_isLoading == value)
                {
                    return;
                }

                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(OverlayVisibility));
            }
        }

        public Visibility ProgressVisibility => _isLoading ? Visibility.Visible : Visibility.Collapsed;

        public Visibility OverlayVisibility => string.IsNullOrWhiteSpace(_overlayText) || _isLoading
            ? Visibility.Collapsed
            : Visibility.Visible;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private sealed class BikThumbnailData
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Pixels { get; init; }
        public required string OverlayText { get; init; }
        public required string SummaryText { get; init; }
    }

    private sealed class ThumbnailBuildProgress
    {
        public required bool IsLoading { get; init; }
        public required string OverlayText { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] PreviewPixels { get; init; }
    }

    private sealed class ScaledBitmapData
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Pixels { get; init; }
    }
}

