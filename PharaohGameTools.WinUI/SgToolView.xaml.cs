using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using PharaohGameTools.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PharaohGameTools.WinUI;

public sealed partial class SgToolView : UserControl
{
    private const double LayoutAdjustmentEpsilon = 0.5;
    private const double DefaultLeftPaneRatio = 1.1;
    private const double DefaultMiddlePaneRatio = 1.9;
    private const double DefaultRightPaneRatio = 1.25;
    private const double DefaultPreviewPaneRatio = 1.6;
    private const double DefaultDetailsPaneRatio = 0.9;
    private readonly ObservableCollection<ArchiveRow> _archiveRows = new();
    private readonly ObservableCollection<ImageRow> _imageRows = new();
    private readonly List<ArchiveItem> _archiveItems = new();
    private readonly List<ImageEntry> _previewAnimationFrames = new();
    private readonly List<SortCriterion> _archiveSortCriteria = new();
    private readonly List<SortCriterion> _imageSortCriteria = new();
    private readonly DispatcherTimer _previewAnimationTimer = new();
    private bool _hideSystemItems = true;
    private bool _animatePreviewPreferred = true;
    private bool _layoutTouched;
    private bool _isNormalizingPaneLayout;
    private bool _suppressArchiveSelectionChanged;
    private bool _suppressImageSelectionChanged;
    private bool _suppressAnimatePreviewToggle;
    private int _previewAnimationFrameIndex;
    private int _previewAnimationDirection = 1;
    private bool _previewAnimationCanReverse;
    private WriteableBitmap? _previewBitmap;
    private Rectangle _previewAnimationBounds = Rectangle.Empty;
    private ImageEntry? _previewAnimationBaseImage;
    private string _previewAnimationMode = "static";
    private ArchiveItem? _currentArchiveItem;
    private ImageEntry? _currentImage;
    private SgToolLayoutSettings? _pendingLayoutSettings;
    private double _leftPaneRatio = DefaultLeftPaneRatio;
    private double _middlePaneRatio = DefaultMiddlePaneRatio;
    private double _rightPaneRatio = DefaultRightPaneRatio;
    private double _previewPaneRatio = DefaultPreviewPaneRatio;
    private double _detailsPaneRatio = DefaultDetailsPaneRatio;

    private sealed class ArchiveRow
    {
        public required ArchiveItem ArchiveItem { get; init; }
        public required string DisplayName { get; init; }
        public required string TypeText { get; init; }
        public required string StateText { get; init; }
    }

    private sealed class ImageRow
    {
        public required ImageEntry Image { get; init; }
        public required string DisplayIdText { get; init; }
        public required string Name { get; init; }
        public required string GroupName { get; init; }
        public required string SubgroupName { get; init; }
        public required string TypeText { get; init; }
        public required string SizeText { get; init; }
        public required string Source555Name { get; init; }
        public required string MirrorText { get; init; }
    }

    private sealed class PreviewBitmapData
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] Pixels { get; init; }
    }

    private sealed class SortCriterion
    {
        public required string Key { get; init; }
        public bool Descending { get; set; }
    }

    public SgToolView()
    {
        InitializeComponent();
        HideSystemItemsCheckBox.IsChecked = true;
        AnimatePreviewCheckBox.IsChecked = true;
        AnimatePreviewCheckBox.IsEnabled = false;
        _archiveSortCriteria.Add(new SortCriterion { Key = "DisplayName", Descending = false });
        _imageSortCriteria.Add(new SortCriterion { Key = "Id", Descending = false });
        ArchivesListView.ItemsSource = _archiveRows;
        ImagesListView.ItemsSource = _imageRows;
        _previewAnimationTimer.Tick += PreviewAnimationTimer_Tick;
        UpdateSortHeaderButtons();
        UpdateUiState();
        Loaded += SgToolView_Loaded;
    }

    internal SgToolLayoutSettings CaptureLayout()
    {
        return new SgToolLayoutSettings
        {
            HasSavedLayout = _layoutTouched,
            LeftPaneWidth = LeftPaneColumn.ActualWidth,
            MiddlePaneWidth = MiddlePaneColumn.ActualWidth,
            RightPaneWidth = RightPaneColumn.ActualWidth,
            PreviewPaneHeight = PreviewPaneRow.ActualHeight,
            DetailsPaneHeight = DetailsPaneRow.ActualHeight
        };
    }

    internal void ApplyLayout(SgToolLayoutSettings? settings)
    {
        if (settings == null || !settings.HasSavedLayout)
        {
            return;
        }

        _layoutTouched = true;
        _pendingLayoutSettings = settings;
        ApplyTrackedRatiosFromSettings(settings);
        NormalizePaneLayout();
    }

    private void SgToolView_Loaded(object sender, RoutedEventArgs e)
    {
        bool appliedPendingLayout = false;
        if (_pendingLayoutSettings != null)
        {
            ApplyTrackedRatiosFromSettings(_pendingLayoutSettings);
            _pendingLayoutSettings = null;
            appliedPendingLayout = true;
        }

        if (!appliedPendingLayout)
        {
            UpdatePaneRatiosFromDefinitions();
        }

        NormalizePaneLayout();
    }

    private void ResizeThumb_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        InputSystemCursorShape shape = sender is Thumb thumb && string.Equals(thumb.Name, nameof(PreviewDetailsThumb), StringComparison.Ordinal)
            ? InputSystemCursorShape.SizeNorthSouth
            : InputSystemCursorShape.SizeWestEast;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private void ResizeThumb_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        ProtectedCursor = null;
    }

    public async Task PromptOpenAsync()
    {
        FileOpenPicker picker = PickerInterop.CreateOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".sg3");

        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await OpenFilesAsync(files.Select(static f => f.Path));
        }
    }

    public async Task PromptSaveAsync()
    {
        ArchiveItem? archiveItem = GetSelectedArchiveItem();
        if (archiveItem == null || archiveItem.Container == null)
        {
            await ShowMessageAsync("Save File", "Select a loaded SG3 or 555 package first.");
            return;
        }

        StorageFolder? folder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (folder == null)
        {
            return;
        }

        try
        {
            SetBusy("Saving package...", "Writing SG3 / 555 output files...");
            SaveResult result = await Task.Run(() => SgArchive.SaveContainer(archiveItem.Container, folder.Path));
            archiveItem.WasSaved = result.WrittenFiles.Count > 0;
            RefreshArchiveRows(preserveSelection: archiveItem.Path);
            RefreshImageRows();
            SetReady(result.WrittenFiles.Count > 0
                ? "Package saved."
                : "There were no changes to save.");
        }
        catch (Exception ex)
        {
            SetError("Save failed.");
            await ShowMessageAsync("Save File Failed", ex.Message);
        }
    }

    public async Task PromptSaveAllAsync()
    {
        List<SgContainer> containers = _archiveItems
            .Where(static x => x.Container != null && (x.Container.HasPendingChanges || x.Container.IsLoose555))
            .Select(static x => x.Container!)
            .ToList();

        if (containers.Count == 0)
        {
            await ShowMessageAsync("Save All", "There are no changed SG3 / 555 packages to save.");
            return;
        }

        StorageFolder? folder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (folder == null)
        {
            return;
        }

        try
        {
            SetBusy("Saving packages...", "Writing all changed SG3 / 555 files...");
            await Task.Run(() =>
            {
                foreach (SgContainer container in containers)
                {
                    SgArchive.SaveContainer(container, folder.Path);
                }
            });

            foreach (ArchiveItem item in _archiveItems.Where(static x => x.Container != null))
            {
                item.WasSaved = true;
            }

            RefreshArchiveRows(preserveSelection: _currentArchiveItem?.Path);
            RefreshImageRows();
            SetReady("All changed packages were saved.");
        }
        catch (Exception ex)
        {
            SetError("Save all failed.");
            await ShowMessageAsync("Save All Failed", ex.Message);
        }
    }

    public async Task PromptBatchExportAsync()
    {
        StorageFolder? sourceFolder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (sourceFolder == null)
        {
            return;
        }

        StorageFolder? outputFolder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (outputFolder == null)
        {
            return;
        }

        bool? includeSystemImages = await AskIncludeSystemImagesAsync();
        if (!includeSystemImages.HasValue)
        {
            return;
        }

        try
        {
            SetBusy("Batch export...", "Scanning SG3 files and exporting PNG workspaces...");
            int exportedCount = await Task.Run(() =>
            {
                List<ArchiveItem> exportItems = ArchiveRepository.ScanFolder(sourceFolder.Path);
                if (exportItems.Count == 0)
                {
                    throw new InvalidOperationException("The selected folder does not contain any SG3 files.");
                }

                int count = 0;
                foreach (ArchiveItem archiveItem in exportItems)
                {
                    SgContainer container = ArchiveRepository.LoadArchive(archiveItem);
                    string targetFolder = Path.Combine(outputFolder.Path, Path.GetFileNameWithoutExtension(archiveItem.Path));
                    BatchWorkspace.ExportContainer(container, targetFolder, !includeSystemImages.Value);
                    count++;
                }

                return count;
            });

            SetReady($"Exported {exportedCount} SG3 package(s).");
        }
        catch (Exception ex)
        {
            SetError("Batch export failed.");
            await ShowMessageAsync("Batch Export Failed", ex.Message);
        }
    }

    public async Task PromptBatchImportAsync()
    {
        StorageFolder? sourceFolder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (sourceFolder == null)
        {
            return;
        }

        StorageFolder? importFolder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (importFolder == null)
        {
            return;
        }

        StorageFolder? outputFolder = await PickerInterop.CreateFolderPicker().PickSingleFolderAsync();
        if (outputFolder == null)
        {
            return;
        }

        try
        {
            SetBusy("Batch import...", "Applying imported PNG workspaces back to SG3 packages...");
            string summary = await Task.Run(() =>
            {
                List<ArchiveItem> importItems = ArchiveRepository.ScanFolder(sourceFolder.Path);
                if (importItems.Count == 0)
                {
                    throw new InvalidOperationException("The selected SG3 source folder does not contain any SG3 files.");
                }

                int importedCount = 0;
                int sizeMismatchCount = 0;
                List<string> messages = new();
                foreach (ArchiveItem archiveItem in importItems)
                {
                    string workspaceFolder = Path.Combine(importFolder.Path, Path.GetFileNameWithoutExtension(archiveItem.Path));
                    if (!Directory.Exists(workspaceFolder))
                    {
                        continue;
                    }

                    SgContainer container = ArchiveRepository.LoadArchive(archiveItem);
                    BatchWorkspace.ImportResult result = BatchWorkspace.ApplyImport(container, workspaceFolder);
                    sizeMismatchCount += result.SizeMismatchCount;
                    messages.AddRange(result.Messages);
                    if (result.ChangedCount > 0)
                    {
                        SgArchive.SaveContainer(container, outputFolder.Path);
                        importedCount++;
                    }
                }

                string summary = $"Imported {importedCount} SG3 package(s).";
                if (sizeMismatchCount > 0)
                {
                    string details = string.Join(Environment.NewLine, messages.Take(12));
                    if (messages.Count > 12)
                    {
                        details += Environment.NewLine + $"... and {messages.Count - 12} more mismatch(es).";
                    }

                    summary += Environment.NewLine + Environment.NewLine
                        + $"Skipped {sizeMismatchCount} image(s) because their resolution does not match SG3."
                        + Environment.NewLine + Environment.NewLine
                        + details;
                }

                return summary;
            });

            SetReady("Batch import completed.");
            await ShowMessageAsync("Batch Import", summary);
        }
        catch (Exception ex)
        {
            SetError("Batch import failed.");
            await ShowMessageAsync("Batch Import Failed", ex.Message);
        }
    }

    public void SetHideSystemItems(bool hideSystemItems)
    {
        _hideSystemItems = hideSystemItems;
        if (HideSystemItemsCheckBox.IsChecked != hideSystemItems)
        {
            HideSystemItemsCheckBox.IsChecked = hideSystemItems;
        }

        RefreshImageRows();
    }

    public bool GetHideSystemItems()
    {
        return _hideSystemItems;
    }

    public bool HasUnsavedArchiveChanges()
    {
        return _archiveItems.Any(static x => x.Container != null && x.Container.HasPendingChanges);
    }

    private async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        List<ArchiveItem> newItems = CreateArchiveItems(paths);
        if (newItems.Count == 0)
        {
            await ShowMessageAsync("Open File", "No SG3 files were selected.");
            return;
        }

        List<ArchiveItem> addedItems = new();
        foreach (ArchiveItem item in newItems)
        {
            if (_archiveItems.Any(existing => string.Equals(existing.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _archiveItems.Add(item);
            addedItems.Add(item);
        }

        if (addedItems.Count == 0)
        {
            ArchiveItem? existingMatch = _archiveItems.FirstOrDefault(existing =>
                newItems.Any(candidate => string.Equals(candidate.Path, existing.Path, StringComparison.OrdinalIgnoreCase)));

            if (existingMatch != null)
            {
                SelectArchiveByPath(existingMatch.Path);
                await LoadArchiveSelectionAsync(existingMatch);
                SetReady("Selected the already loaded package.");
                return;
            }
        }

        _archiveItems.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        RefreshArchiveRows(preserveSelection: addedItems[0].Path);
        await LoadArchiveSelectionAsync(addedItems[0]);
        SetReady($"Added {addedItems.Count} file(s).");
    }

    private static List<ArchiveItem> CreateArchiveItems(IEnumerable<string> paths)
    {
        List<ArchiveItem> items = new();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".sg3", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ArchiveItem
                {
                    Path = path,
                    SourceDirectory = Path.GetDirectoryName(path),
                    IsLoose555 = false
                });
            }
            else if (path.EndsWith(".555", StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new ArchiveItem
                {
                    Path = path,
                    SourceDirectory = Path.GetDirectoryName(path),
                    IsLoose555 = true
                });
            }
        }

        return items;
    }

    private void RefreshArchiveRows(string? preserveSelection = null)
    {
        _suppressArchiveSelectionChanged = true;
        try
        {
            _archiveRows.Clear();
            foreach (ArchiveItem archiveItem in SortArchiveItems(_archiveItems))
            {
                _archiveRows.Add(new ArchiveRow
                {
                    ArchiveItem = archiveItem,
                    DisplayName = archiveItem.DisplayName,
                    TypeText = archiveItem.IsLoose555 ? "555" : "SG3",
                    StateText = GetArchiveStateText(archiveItem)
                });
            }

            if (!string.IsNullOrWhiteSpace(preserveSelection))
            {
                ArchiveRow? match = _archiveRows.FirstOrDefault(x =>
                    string.Equals(x.ArchiveItem.Path, preserveSelection, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    ArchivesListView.SelectedItem = match;
                }
            }
        }
        finally
        {
            _suppressArchiveSelectionChanged = false;
        }

        UpdateUiState();
    }

    private void RefreshImageRows()
    {
        int? preferredDisplayId = _currentImage?.DisplayId;
        _suppressImageSelectionChanged = true;
        try
        {
            _imageRows.Clear();
            _currentImage = null;
            _previewAnimationTimer.Stop();
            _previewAnimationFrames.Clear();
            _previewAnimationFrameIndex = 0;
            _previewAnimationDirection = 1;
            _previewAnimationCanReverse = false;
            _previewAnimationBounds = Rectangle.Empty;
            _previewAnimationBaseImage = null;
            PreviewImage.Source = null;
            OverlayTextBlock.Visibility = Visibility.Visible;
            ReplaceImageButton.IsEnabled = false;
            SaveImageButton.IsEnabled = false;
            AnimatePreviewCheckBox.IsEnabled = false;

            if (_currentArchiveItem?.Container == null)
            {
                PackageInfoTextBlock.Text = "Package:";
                InfoTextBlock.Text = "Select a package and an image.";
                UpdateUiState();
                return;
            }

            IEnumerable<ImageEntry> images = _currentArchiveItem.Container.Images
                .Where(IsDisplayableImage);

            if (_hideSystemItems)
            {
                images = images.Where(static image => !BatchWorkspace.IsSystemImage(image));
            }

            foreach (ImageEntry image in SortImages(images))
            {
                _imageRows.Add(new ImageRow
                {
                    Image = image,
                    DisplayIdText = image.DisplayId.ToString("D4"),
                    Name = image.Name,
                    GroupName = image.GroupName,
                    SubgroupName = image.SubgroupName,
                    TypeText = image.Record.Type.ToString(),
                    SizeText = $"{image.Record.Width}x{image.Record.Height}",
                    Source555Name = string.IsNullOrWhiteSpace(image.Source555Name) ? "-" : image.Source555Name,
                    MirrorText = GetMirrorDisplayText(image)
                });
            }

            PackageInfoTextBlock.Text = $"Package: {_currentArchiveItem.DisplayName}";
            InfoTextBlock.Text = _imageRows.Count > 0
                ? $"Loaded {_imageRows.Count} record(s). Select an item to load its preview."
                : "This package does not contain any displayable image records.";

            if (preferredDisplayId.HasValue)
            {
                ImageRow? preferredRow = _imageRows.FirstOrDefault(x => x.Image.DisplayId == preferredDisplayId.Value);
                if (preferredRow != null)
                {
                    ImagesListView.SelectedItem = preferredRow;
                }
            }
        }
        finally
        {
            _suppressImageSelectionChanged = false;
        }

        UpdateUiState();
    }

    private async Task EnsureArchiveLoadedAsync(ArchiveItem archiveItem)
    {
        if (archiveItem.IsLoaded || !string.IsNullOrWhiteSpace(archiveItem.LoadError))
        {
            return;
        }

        if (archiveItem.LoadingTask == null)
        {
            archiveItem.LoadingTask = Task.Run(() => ArchiveRepository.LoadArchive(archiveItem));
        }

        try
        {
            archiveItem.Container = await archiveItem.LoadingTask;
            archiveItem.IsLoaded = true;
        }
        catch (Exception ex)
        {
            archiveItem.LoadError = ex.Message;
            throw;
        }
        finally
        {
            archiveItem.LoadingTask = null;
        }
    }

    private async Task LoadArchiveSelectionAsync(ArchiveItem? archiveItem)
    {
        _currentArchiveItem = archiveItem;
        if (archiveItem == null)
        {
            RefreshImageRows();
            return;
        }

        if (!archiveItem.IsLoaded)
        {
            try
            {
                SetBusy($"Loading {archiveItem.DisplayName}...", "Reading SG3 records and source data...");
                await EnsureArchiveLoadedAsync(archiveItem);
            }
            catch (Exception ex)
            {
                RefreshArchiveRows(preserveSelection: archiveItem.Path);
                SetError("Load failed.");
                await ShowMessageAsync("Load Error", $"{archiveItem.DisplayName}:{Environment.NewLine}{ex.Message}");
                return;
            }
        }

        RefreshImageRows();
        await SelectImageAndUpdatePreviewAsync();
        SetReady(_imageRows.Count > 0
            ? $"Selected {archiveItem.DisplayName}. Loaded {_imageRows.Count} record(s)."
            : $"Selected {archiveItem.DisplayName}. This package does not contain any displayable image records.");
    }

    private void SelectArchiveByPath(string path)
    {
        ArchiveRow? row = _archiveRows.FirstOrDefault(x => string.Equals(x.ArchiveItem.Path, path, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            ArchivesListView.SelectedItem = row;
        }
    }

    private ArchiveItem? GetSelectedArchiveItem()
    {
        return (ArchivesListView.SelectedItem as ArchiveRow)?.ArchiveItem;
    }

    private ImageEntry? GetSelectedImage()
    {
        return (ImagesListView.SelectedItem as ImageRow)?.Image;
    }

    private async Task UpdatePreviewAsync()
    {
        ImageEntry? image = GetSelectedImage();
        _currentImage = image;
        if (image == null)
        {
            _previewAnimationTimer.Stop();
            PreviewImage.Source = null;
            OverlayTextBlock.Visibility = Visibility.Visible;
            InfoTextBlock.Text = "Select a package and an image.";
            ReplaceImageButton.IsEnabled = false;
            SaveImageButton.IsEnabled = false;
            AnimatePreviewCheckBox.IsEnabled = false;
            return;
        }

        try
        {
            RebuildPreviewAnimationFrames(image);
            if (AnimatePreviewCheckBox.IsChecked == true && _previewAnimationFrames.Count > 1)
            {
                int selectedFrameIndex = _previewAnimationFrames.FindIndex(x => ReferenceEquals(x, image));
                _previewAnimationFrameIndex = selectedFrameIndex >= 0
                    ? selectedFrameIndex
                    : Math.Min(_previewAnimationFrameIndex, _previewAnimationFrames.Count - 1);
                _previewAnimationDirection = 1;
                _previewAnimationTimer.Interval = TimeSpan.FromMilliseconds(GetPreviewAnimationInterval(_previewAnimationFrames[_previewAnimationFrameIndex]));
                await ShowPreviewFrameAsync(image, _previewAnimationFrames[_previewAnimationFrameIndex], _previewAnimationFrameIndex + 1, _previewAnimationFrames.Count);
                _previewAnimationTimer.Start();
            }
            else
            {
                _previewAnimationTimer.Stop();
                _previewAnimationFrameIndex = 0;
                _previewAnimationDirection = 1;
                await ShowPreviewFrameAsync(image, image, 1, Math.Max(1, _previewAnimationFrames.Count));
            }
        }
        catch (Exception ex)
        {
            _previewAnimationTimer.Stop();
            PreviewImage.Source = null;
            OverlayTextBlock.Visibility = Visibility.Visible;
            InfoTextBlock.Text = "Preview could not be created: " + ex.Message;
            SaveImageButton.IsEnabled = false;
            ReplaceImageButton.IsEnabled = false;
        }
    }

    private async Task SaveSelectedImageAsync()
    {
        ImageEntry? image = GetSelectedImage();
        if (image == null)
        {
            return;
        }

        FileSavePicker picker = PickerInterop.CreateSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = $"{image.DisplayId:D4}.png";
        picker.FileTypeChoices.Add("PNG image", new[] { ".png" });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using Bitmap bitmap = ImagingCodec.DecodeImage(image.Container, image);
                bitmap.Save(file.Path, ImageFormat.Png);
            });

            SetReady("Image exported.");
        }
        catch (Exception ex)
        {
            SetError("Image export failed.");
            await ShowMessageAsync("Save Image Failed", ex.Message);
        }
    }

    private async Task ReplaceSelectedImageAsync()
    {
        ImageEntry? image = GetSelectedImage();
        if (image == null)
        {
            return;
        }

        if (image.Record.IsMirror)
        {
            await ShowMessageAsync("Replace Image", "A mirrored record cannot be replaced directly. Select the original non-mirrored record.");
            return;
        }

        FileOpenPicker picker = PickerInterop.CreateOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            using Bitmap temp = new(file.Path);
            if (temp.Width != image.Record.Width || temp.Height != image.Record.Height)
            {
                throw new InvalidOperationException($"Image size must be {image.Record.Width}x{image.Record.Height}.");
            }

            image.ReplacementBitmap?.Dispose();
            image.ReplacementBitmap = new Bitmap(temp);
            image.IsModified = true;
            image.Container.HasPendingChanges = true;
            image.CachedPreview?.Dispose();
            image.CachedPreview = null;

            RefreshArchiveRows(preserveSelection: image.Container.SourcePath);
            RefreshImageRows();
            SelectImageById(image.Container, image.DisplayId);
            await UpdatePreviewAsync();
            SetReady("Replacement image loaded.");
        }
        catch (Exception ex)
        {
            SetError("Image replace failed.");
            await ShowMessageAsync("Replace Image Failed", ex.Message);
        }
    }

    private void SelectImageById(SgContainer container, int displayId)
    {
        ImageRow? row = _imageRows.FirstOrDefault(x => ReferenceEquals(x.Image.Container, container) && x.Image.DisplayId == displayId);
        if (row != null)
        {
            ImagesListView.SelectedItem = row;
        }
    }

    private async Task SelectImageAndUpdatePreviewAsync(int? preferredDisplayId = null)
    {
        if (_imageRows.Count == 0)
        {
            _currentImage = null;
            PreviewImage.Source = null;
            OverlayTextBlock.Visibility = Visibility.Visible;
            AnimatePreviewCheckBox.IsEnabled = false;
            return;
        }

        int selectedIndex = 0;
        if (preferredDisplayId.HasValue)
        {
            int matchIndex = _imageRows
                .Select((row, index) => new { row, index })
                .Where(x => x.row.Image.DisplayId == preferredDisplayId.Value)
                .Select(x => x.index)
                .DefaultIfEmpty(-1)
                .First();
            if (matchIndex >= 0)
            {
                selectedIndex = matchIndex;
            }
        }

        _suppressImageSelectionChanged = true;
        try
        {
            ImagesListView.SelectedIndex = selectedIndex;
        }
        finally
        {
            _suppressImageSelectionChanged = false;
        }

        await UpdatePreviewAsync();
    }

    private static string BuildImageInfoText(ImageEntry selectedImage, ImageEntry frameImage, int frameNumber, int totalFrames, string animationMode, ImageEntry? baseImage)
    {
        return string.Join(Environment.NewLine, new[]
        {
            $"ID: {frameImage.DisplayId:D4}",
            $"Name: {frameImage.Name}",
            $"Group: {frameImage.GroupName}",
            $"Subgroup: {frameImage.SubgroupName}",
            $"Type: {frameImage.Record.Type}",
            $"NumSprites: {frameImage.Record.NumSprites}",
            $"Size: {frameImage.Record.Width}x{frameImage.Record.Height}",
            $"Offset: {frameImage.Record.SpriteOffsetX}, {frameImage.Record.SpriteOffsetY}",
            $"Speed ID: {frameImage.Record.SpeedId}",
            $"Source: {(string.IsNullOrWhiteSpace(frameImage.Source555Name) ? "-" : frameImage.Source555Name)}",
            $"Mirror of: {GetMirrorDisplayText(frameImage)}",
            $"State: {(frameImage.IsModified ? "Modified" : "Original")}",
            $"Frame: {frameNumber}/{Math.Max(1, totalFrames)}",
            $"Animation mode: {animationMode}",
            $"Animation base: {(baseImage == null ? "-" : baseImage.DisplayId.ToString("D4"))}"
        });
    }

    private static string GetArchiveStateText(ArchiveItem archiveItem)
    {
        if (!string.IsNullOrEmpty(archiveItem.LoadError))
        {
            return "Error";
        }

        if (archiveItem.Container != null && archiveItem.Container.HasPendingChanges)
        {
            return "Unsaved";
        }

        if (archiveItem.WasSaved)
        {
            return "Saved";
        }

        return "Unchanged";
    }

    private static string GetMirrorDisplayText(ImageEntry image)
    {
        if (image.Record.MirrorOfIndex.HasValue)
        {
            return image.Record.MirrorOfIndex.Value.ToString("D4");
        }

        if (image.Record.IsMirror)
        {
            return "?";
        }

        return "-";
    }

    private static PreviewBitmapData DecodePreviewBitmapData(IReadOnlyList<ImageEntry> images, Rectangle contentBounds)
    {
        if (images == null || images.Count == 0)
        {
            return new PreviewBitmapData
            {
                Width = 1,
                Height = 1,
                Pixels = new byte[4]
            };
        }

        ImageEntry anchorImage = images[images.Count - 1];
        Rectangle frameBounds = contentBounds.IsEmpty
            ? new Rectangle(-anchorImage.Record.SpriteOffsetX, -anchorImage.Record.SpriteOffsetY, anchorImage.Record.Width, anchorImage.Record.Height)
            : contentBounds;
        int width = Math.Max(1, frameBounds.Width);
        int height = Math.Max(1, frameBounds.Height);

        using Bitmap canvas = new(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(System.Drawing.Color.Transparent);
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;

            foreach (ImageEntry image in images)
            {
                using Bitmap bitmap = ImagingCodec.DecodeImage(image.Container, image);
                int destX = -frameBounds.X - image.Record.SpriteOffsetX;
                int destY = -frameBounds.Y - image.Record.SpriteOffsetY;
                graphics.DrawImageUnscaled(bitmap, destX, destY);
            }
        }

        Rectangle rect = new(0, 0, canvas.Width, canvas.Height);
        BitmapData data = canvas.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = width * 4;
            byte[] pixels = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
            {
                IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(rowPtr, pixels, y * rowBytes, rowBytes);
            }

            return new PreviewBitmapData
            {
                Width = width,
                Height = height,
                Pixels = pixels
            };
        }
        finally
        {
            canvas.UnlockBits(data);
        }
    }

    private BitmapSource CreateBitmapSource(PreviewBitmapData previewData)
    {
        WriteableBitmap target = EnsurePreviewBitmap(previewData.Width, previewData.Height);
        using Stream stream = target.PixelBuffer.AsStream();
        stream.Position = 0;
        stream.Write(previewData.Pixels, 0, previewData.Pixels.Length);
        target.Invalidate();
        return target;
    }

    private async Task ShowPreviewFrameAsync(ImageEntry selectedImage, ImageEntry frameImage, int frameNumber, int totalFrames)
    {
        IReadOnlyList<ImageEntry> compositeImages = GetPreviewCompositeImages(frameImage);
        ImageEntry boundsAnchor = compositeImages[compositeImages.Count - 1];
        Rectangle contentBounds = _previewAnimationBounds.IsEmpty
            ? new Rectangle(-boundsAnchor.Record.SpriteOffsetX, -boundsAnchor.Record.SpriteOffsetY, boundsAnchor.Record.Width, boundsAnchor.Record.Height)
            : _previewAnimationBounds;

        PreviewBitmapData previewData = await Task.Run(() => DecodePreviewBitmapData(compositeImages, contentBounds));
        if (!ReferenceEquals(selectedImage, GetSelectedImage()))
        {
            return;
        }

        PreviewImage.Source = CreateBitmapSource(previewData);
        OverlayTextBlock.Visibility = Visibility.Collapsed;
        PackageInfoTextBlock.Text = $"Package: {selectedImage.Container.DisplayName}";
        InfoTextBlock.Text = BuildImageInfoText(selectedImage, frameImage, frameNumber, totalFrames, _previewAnimationMode, _previewAnimationBaseImage);
        SaveImageButton.IsEnabled = true;
        ReplaceImageButton.IsEnabled = (AnimatePreviewCheckBox.IsChecked != true) && !selectedImage.Record.IsMirror;
    }

    private void RebuildPreviewAnimationFrames(ImageEntry selectedImage)
    {
        _previewAnimationFrames.Clear();
        _previewAnimationBounds = Rectangle.Empty;
        _previewAnimationCanReverse = false;
        _previewAnimationBaseImage = null;
        _previewAnimationMode = "static";
        if (selectedImage == null)
        {
            AnimatePreviewCheckBox.IsEnabled = false;
            return;
        }

        List<ImageEntry>? frames;
        if (TryGetOverlayAnimationFramesFromSpriteCount(selectedImage, out ImageEntry? countedOverlayBaseImage, out List<ImageEntry>? countedOverlayFrames))
        {
            _previewAnimationBaseImage = countedOverlayBaseImage;
            frames = countedOverlayFrames;
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "overlay_sprite_count";
            }
        }
        else if (IsTerrainStyleAnimationPack(selectedImage.Container))
        {
            frames = TryGetTerrainAnimationFrames(selectedImage);
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "terrain";
            }
        }
        else
        {
            frames = TryGetSpriteCountAnimationFrames(selectedImage);
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "sprite_count";
            }
        }

        if ((frames == null || frames.Count <= 1) && TryGetStringGroupAnimationFrames(selectedImage, out List<ImageEntry>? stringGroupFrames))
        {
            frames = stringGroupFrames;
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "string_group";
            }
        }

        if ((frames == null || frames.Count <= 1) && IsEnemyStyleAnimationPack(selectedImage.Container))
        {
            frames = TryGetEnemyMacroAnimationFrames(selectedImage);
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "enemy";
            }
        }
        else if ((frames == null || frames.Count <= 1) && IsAmbientStyleAnimationPack(selectedImage.Container))
        {
            frames = TryGetAmbientAnimationFrames(selectedImage);
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "ambient";
            }
        }

        if ((frames == null || frames.Count <= 1) && TryGetOverlayAnimationFrames(selectedImage, out ImageEntry? overlayBaseImage, out List<ImageEntry>? overlayFrames))
        {
            _previewAnimationBaseImage = overlayBaseImage;
            frames = overlayFrames;
            if (frames != null && frames.Count > 1)
            {
                _previewAnimationMode = "overlay_generic";
            }
        }

        if (frames == null || frames.Count == 0)
        {
            _previewAnimationFrames.Add(selectedImage);
            _previewAnimationMode = "static";
        }
        else
        {
            _previewAnimationFrames.AddRange(frames);
        }

        _previewAnimationCanReverse = _previewAnimationFrames.Count > 1
            && _previewAnimationFrames.All(x => x.Record != null && x.Record.CanReverse);
        _previewAnimationBounds = BuildPreviewAnimationBounds(_previewAnimationFrames, _previewAnimationBaseImage);

        AnimatePreviewCheckBox.IsEnabled = _previewAnimationFrames.Count > 1;
        _suppressAnimatePreviewToggle = true;
        try
        {
            AnimatePreviewCheckBox.IsChecked = AnimatePreviewCheckBox.IsEnabled && _animatePreviewPreferred;
        }
        finally
        {
            _suppressAnimatePreviewToggle = false;
        }
    }

    private IReadOnlyList<ImageEntry> GetPreviewCompositeImages(ImageEntry frameImage)
    {
        if (_previewAnimationBaseImage == null || ReferenceEquals(frameImage, _previewAnimationBaseImage))
        {
            return new[] { frameImage };
        }

        return new[] { _previewAnimationBaseImage, frameImage };
    }

    private bool TryGetStringGroupAnimationFrames(ImageEntry selectedImage, out List<ImageEntry>? frames)
    {
        frames = null;
        if (selectedImage?.Container == null || selectedImage.Record == null || selectedImage.Resolution == null)
        {
            return false;
        }

        StructuralSubgroup? subgroup = selectedImage.Container.StructuralSubgroups
            .FirstOrDefault(x => x.SlotIndex == selectedImage.Resolution.StructuralSubgroupSlot);
        if (subgroup == null)
        {
            return false;
        }

        if (!StringGroupProfile.TryResolve(selectedImage.Container, selectedImage.Record, subgroup, out StringGroupProfileEntry entry))
        {
            return false;
        }

        if (!StringGroupProfile.ShouldAnimate(entry, subgroup))
        {
            return true;
        }

        List<ImageEntry> subgroupFrames = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .Where(x => x.Resolution != null && x.Resolution.StructuralSubgroupSlot == subgroup.SlotIndex)
            .OrderBy(x => x.DisplayId)
            .ToList();
        if (subgroupFrames.Count <= 1)
        {
            return true;
        }

        int directionCount = StringGroupProfile.GetDirectionCount(entry, subgroup);
        if (directionCount > 0 && subgroupFrames.Count % directionCount == 0)
        {
            int framesPerDirection = subgroupFrames.Count / directionCount;
            if (framesPerDirection > 1)
            {
                int selectedPosition = subgroupFrames.FindIndex(x => ReferenceEquals(x, selectedImage));
                if (selectedPosition >= 0)
                {
                    int directionOffset = selectedPosition % directionCount;
                    frames = new List<ImageEntry>();
                    for (int i = directionOffset; i < subgroupFrames.Count; i += directionCount)
                    {
                        frames.Add(subgroupFrames[i]);
                    }

                    return true;
                }
            }
        }

        frames = subgroupFrames;
        return true;
    }

    private List<ImageEntry>? TryGetSpriteCountAnimationFrames(ImageEntry selectedImage)
    {
        if (selectedImage == null || selectedImage.Record == null || selectedImage.Record.NumSprites <= 1)
        {
            return null;
        }

        List<ImageEntry> ordered = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .OrderBy(x => x.DisplayId)
            .ToList();
        int selectedIndex = ordered.FindIndex(x => ReferenceEquals(x, selectedImage));
        if (selectedIndex < 0)
        {
            return null;
        }

        int runStart = selectedIndex;
        int runEnd = selectedIndex;
        while (runStart > 0 && IsAnimationFrameCompatible(selectedImage, ordered[runStart - 1]))
        {
            runStart--;
        }

        while (runEnd + 1 < ordered.Count && IsAnimationFrameCompatible(selectedImage, ordered[runEnd + 1]))
        {
            runEnd++;
        }

        List<ImageEntry> contiguousRun = ordered.Skip(runStart).Take(runEnd - runStart + 1).ToList();
        int spriteCount = selectedImage.Record.NumSprites;
        if (contiguousRun.Count < spriteCount)
        {
            return null;
        }

        int relativeIndex = selectedIndex - runStart;
        int chunkStart = runStart + ((relativeIndex / spriteCount) * spriteCount);
        if (chunkStart < 0 || chunkStart + spriteCount > ordered.Count)
        {
            return null;
        }

        List<ImageEntry> chunk = ordered.Skip(chunkStart).Take(spriteCount).ToList();
        return chunk.All(x => IsAnimationFrameCompatible(selectedImage, x)) ? chunk : null;
    }

    private List<ImageEntry>? TryGetTerrainAnimationFrames(ImageEntry selectedImage)
    {
        if (selectedImage?.Container == null || selectedImage.Record == null)
        {
            return null;
        }

        if (selectedImage.Record.NumSprites > 1 || (selectedImage.Record.Type != 0 && selectedImage.Record.Type != 256))
        {
            return null;
        }

        List<ImageEntry> ordered = selectedImage.Container.Images
            .Where(x => x != null && x.Record != null && x.Record.HasData && !x.Record.IsMirror)
            .OrderBy(x => x.DisplayId)
            .ToList();
        int selectedIndex = ordered.FindIndex(x => ReferenceEquals(x, selectedImage));
        if (selectedIndex < 0)
        {
            return null;
        }

        int runStart = selectedIndex;
        int runEnd = selectedIndex;
        while (runStart > 0 && IsTerrainAnimationFrameCompatible(selectedImage, ordered[runStart - 1]))
        {
            runStart--;
        }

        while (runEnd + 1 < ordered.Count && IsTerrainAnimationFrameCompatible(selectedImage, ordered[runEnd + 1]))
        {
            runEnd++;
        }

        List<ImageEntry> run = ordered.Skip(runStart).Take(runEnd - runStart + 1).ToList();
        return run.Count == 8 ? run : null;
    }

    private List<ImageEntry>? TryGetEnemyMacroAnimationFrames(ImageEntry selectedImage)
    {
        if (selectedImage?.Container == null || selectedImage.Resolution == null)
        {
            return null;
        }

        StructuralSubgroup? subgroup = selectedImage.Container.StructuralSubgroups
            .FirstOrDefault(x => x.SlotIndex == selectedImage.Resolution.StructuralSubgroupSlot);
        if (subgroup == null)
        {
            return null;
        }

        if (selectedImage.Container.StructuralSubgroups.Count == 10)
        {
            int subgroupLength = subgroup.EndImage - subgroup.StartImage + 1;
            if (subgroupLength == 8)
            {
                return TryGetCompactSubgroupAnimationFrames(selectedImage);
            }

            if (subgroupLength == 128
                && (selectedImage.GroupName.IndexOf("TRANSPORT", StringComparison.OrdinalIgnoreCase) >= 0
                    || selectedImage.GroupName.IndexOf("WARSHIP", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return TryGetSteppedAnimationFrames(selectedImage, 32);
            }

            return TryGetDirectionalAnimationFrames(selectedImage);
        }

        return subgroup.PhysicalOrder switch
        {
            2 or 6 or 9 or 12 or 15 => TryGetCompactSubgroupAnimationFrames(selectedImage),
            _ => TryGetDirectionalAnimationFrames(selectedImage)
        };
    }

    private List<ImageEntry>? TryGetSteppedAnimationFrames(ImageEntry selectedImage, int step)
    {
        if (selectedImage == null || selectedImage.Record == null || selectedImage.Resolution == null || step <= 1)
        {
            return null;
        }

        int subgroupSlot = selectedImage.Resolution.StructuralSubgroupSlot;
        if (subgroupSlot <= 0)
        {
            return null;
        }

        List<ImageEntry> subgroupFrames = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .Where(x => x.Resolution != null && x.Resolution.StructuralSubgroupSlot == subgroupSlot)
            .OrderBy(x => x.DisplayId)
            .ToList();
        if (subgroupFrames.Count < step * 2 || subgroupFrames.Count % step != 0)
        {
            return null;
        }

        int selectedPosition = subgroupFrames.FindIndex(x => ReferenceEquals(x, selectedImage));
        if (selectedPosition < 0)
        {
            return null;
        }

        int offset = selectedPosition % step;
        List<ImageEntry> frames = new();
        for (int i = offset; i < subgroupFrames.Count; i += step)
        {
            ImageEntry frame = subgroupFrames[i];
            if (frame.Record.Type != selectedImage.Record.Type
                || frame.Record.BitmapId != selectedImage.Record.BitmapId
                || !string.Equals(frame.Source555Name, selectedImage.Source555Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            frames.Add(frame);
        }

        return frames.Count > 1 ? frames : null;
    }

    private List<ImageEntry>? TryGetDirectionalAnimationFrames(ImageEntry selectedImage)
    {
        if (selectedImage == null || selectedImage.Record == null || selectedImage.Resolution == null)
        {
            return null;
        }

        int subgroupSlot = selectedImage.Resolution.StructuralSubgroupSlot;
        if (subgroupSlot <= 0)
        {
            return null;
        }

        List<ImageEntry> subgroupFrames = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .Where(x => x.Resolution != null && x.Resolution.StructuralSubgroupSlot == subgroupSlot)
            .OrderBy(x => x.DisplayId)
            .ToList();
        if (subgroupFrames.Count < 16 || subgroupFrames.Count % 8 != 0)
        {
            return null;
        }

        int selectedPosition = subgroupFrames.FindIndex(x => ReferenceEquals(x, selectedImage));
        if (selectedPosition < 0)
        {
            return null;
        }

        int directionOffset = selectedPosition % 8;
        List<ImageEntry> frames = new();
        for (int i = directionOffset; i < subgroupFrames.Count; i += 8)
        {
            ImageEntry frame = subgroupFrames[i];
            if (frame.Record.Type != selectedImage.Record.Type
                || frame.Record.BitmapId != selectedImage.Record.BitmapId
                || !string.Equals(frame.Source555Name, selectedImage.Source555Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            frames.Add(frame);
        }

        return frames.Count > 1 ? frames : null;
    }

    private List<ImageEntry>? TryGetCompactSubgroupAnimationFrames(ImageEntry selectedImage)
    {
        if (selectedImage == null || selectedImage.Record == null || selectedImage.Resolution == null)
        {
            return null;
        }

        int subgroupSlot = selectedImage.Resolution.StructuralSubgroupSlot;
        if (subgroupSlot <= 0 || selectedImage.Record.NumSprites > 1)
        {
            return null;
        }

        List<ImageEntry> subgroupFrames = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .Where(x => x.Resolution != null && x.Resolution.StructuralSubgroupSlot == subgroupSlot)
            .OrderBy(x => x.DisplayId)
            .ToList();
        if (subgroupFrames.Count <= 1 || subgroupFrames.Count > 16)
        {
            return null;
        }

        if (subgroupFrames.Any(x => x.Record.IsMirror || x.Record.MirrorOfIndex.HasValue))
        {
            return null;
        }

        if (subgroupFrames.Any(x =>
            x.Record.Type != selectedImage.Record.Type
            || x.Record.BitmapId != selectedImage.Record.BitmapId
            || !string.Equals(x.Source555Name, selectedImage.Source555Name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return subgroupFrames;
    }

    private List<ImageEntry>? TryGetAmbientAnimationFrames(ImageEntry selectedImage)
    {
        if (selectedImage == null || selectedImage.Resolution == null)
        {
            return null;
        }

        int subgroupSlot = selectedImage.Resolution.StructuralSubgroupSlot;
        if (subgroupSlot <= 0)
        {
            return null;
        }

        List<ImageEntry> subgroupFrames = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .Where(x => x.Resolution != null && x.Resolution.StructuralSubgroupSlot == subgroupSlot)
            .OrderBy(x => x.DisplayId)
            .ToList();
        if (subgroupFrames.Count <= 1)
        {
            return null;
        }

        StructuralSubgroup? subgroup = selectedImage.Container.StructuralSubgroups
            .FirstOrDefault(x => x.SlotIndex == subgroupSlot);
        SprAmbientProfileEntry? profileEntry = SprAmbientProfile.FindEntry(selectedImage.Container, selectedImage.Record, subgroup);
        if (profileEntry != null)
        {
            int directionCount = SprAmbientProfile.GetDirectionCount(profileEntry);
            int framesPerDirection = SprAmbientProfile.GetFramesPerDirection(profileEntry);
            if (directionCount > 0 && framesPerDirection <= 1)
            {
                return null;
            }

            if (directionCount > 0 && framesPerDirection > 1)
            {
                int selectedPosition = subgroupFrames.FindIndex(x => ReferenceEquals(x, selectedImage));
                if (selectedPosition >= 0
                    && subgroupFrames.Count >= directionCount
                    && subgroupFrames.Count % directionCount == 0)
                {
                    int directionOffset = selectedPosition % directionCount;
                    List<ImageEntry> frames = new();
                    for (int i = directionOffset; i < subgroupFrames.Count; i += directionCount)
                    {
                        frames.Add(subgroupFrames[i]);
                    }

                    return frames.Count > 1 ? frames : null;
                }
            }

            return subgroupFrames.Count > 1 ? subgroupFrames : null;
        }

        if (subgroupFrames.Any(x =>
            x.Record.Type != selectedImage.Record.Type
            || x.Record.BitmapId != selectedImage.Record.BitmapId
            || !string.Equals(x.Source555Name, selectedImage.Source555Name, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        int count = subgroupFrames.Count;
        if (subgroupFrames.Any(x => x.Record.IsMirror || x.Record.MirrorOfIndex.HasValue))
        {
            if (count < 8 || count % 8 != 0)
            {
                return null;
            }

            int selectedPosition = subgroupFrames.FindIndex(x => ReferenceEquals(x, selectedImage));
            if (selectedPosition < 0)
            {
                return null;
            }

            int directionOffset = selectedPosition % 8;
            List<ImageEntry> frames = new();
            for (int i = directionOffset; i < subgroupFrames.Count; i += 8)
            {
                frames.Add(subgroupFrames[i]);
            }

            return frames.Count > 1 ? frames : null;
        }

        return subgroupFrames;
    }

    private bool TryGetOverlayAnimationFrames(ImageEntry selectedImage, out ImageEntry? baseImage, out List<ImageEntry>? frames)
    {
        baseImage = null;
        frames = null;
        if (selectedImage?.Container == null || selectedImage.Record == null)
        {
            return false;
        }

        List<ImageEntry> ordered = selectedImage.Container.Images
            .Where(IsAnimationCandidateImage)
            .OrderBy(x => x.DisplayId)
            .ToList();
        if (ordered.Count < 3)
        {
            return false;
        }

        for (int baseIndex = 0; baseIndex < ordered.Count - 2; baseIndex++)
        {
            ImageEntry candidateBase = ordered[baseIndex];
            if (!IsOverlayAnimationBaseCandidate(candidateBase))
            {
                continue;
            }

            List<ImageEntry> candidateFrames = BuildOverlayAnimationSequence(ordered, baseIndex);
            if (candidateFrames.Count < 2)
            {
                continue;
            }

            bool selectedParticipates = ReferenceEquals(selectedImage, candidateBase)
                || candidateFrames.Any(frame => ReferenceEquals(frame, selectedImage));
            if (!selectedParticipates)
            {
                continue;
            }

            baseImage = candidateBase;
            frames = candidateFrames;
            return true;
        }

        return false;
    }

    private bool TryGetOverlayAnimationFramesFromSpriteCount(ImageEntry selectedImage, out ImageEntry? baseImage, out List<ImageEntry>? frames)
    {
        baseImage = null;
        frames = null;
        if (selectedImage?.Container == null || selectedImage.Record == null)
        {
            return false;
        }

        ImageEntry? candidateBase = null;
        if (selectedImage.Record.NumSprites > 1)
        {
            candidateBase = selectedImage;
        }
        else
        {
            for (int baseId = selectedImage.DisplayId - 1; baseId >= 0; baseId--)
            {
                ImageEntry? previous = selectedImage.Container.Images.FirstOrDefault(x => x.DisplayId == baseId);
                if (previous?.Record == null || previous.Record.NumSprites <= 1)
                {
                    continue;
                }

                int frameCount = previous.Record.NumSprites;
                if (selectedImage.DisplayId <= previous.DisplayId + frameCount)
                {
                    candidateBase = previous;
                    break;
                }
            }
        }

        if (!IsOverlaySpriteCountBaseCandidate(candidateBase))
        {
            return false;
        }

        List<ImageEntry> candidateFrames = BuildOverlayAnimationSequenceFromSpriteCount(candidateBase.Container.Images, candidateBase);
        if (candidateFrames.Count < 2)
        {
            return false;
        }

        bool selectedParticipates = ReferenceEquals(selectedImage, candidateBase)
            || candidateFrames.Any(frame => ReferenceEquals(frame, selectedImage));
        if (!selectedParticipates)
        {
            return false;
        }

        baseImage = candidateBase;
        frames = candidateFrames;
        return true;
    }

    private static List<ImageEntry> BuildOverlayAnimationSequence(IReadOnlyList<ImageEntry> ordered, int baseIndex)
    {
        ImageEntry baseImage = ordered[baseIndex];
        List<ImageEntry> frames = new();
        bool hasAnimatedSignal = false;

        for (int i = baseIndex + 1; i < ordered.Count; i++)
        {
            ImageEntry candidate = ordered[i];
            if (candidate.DisplayId != ordered[i - 1].DisplayId + 1)
            {
                break;
            }

            if (!IsOverlayAnimationFrameCompatible(baseImage, candidate))
            {
                break;
            }

            if (candidate.Record.SpeedId > 0
                || candidate.Record.SpriteOffsetX != baseImage.Record.SpriteOffsetX
                || candidate.Record.SpriteOffsetY != baseImage.Record.SpriteOffsetY
                || candidate.Record.Width != baseImage.Record.Width
                || candidate.Record.Height != baseImage.Record.Height)
            {
                hasAnimatedSignal = true;
            }

            frames.Add(candidate);
        }

        return hasAnimatedSignal ? frames : new List<ImageEntry>();
    }

    private static List<ImageEntry> BuildOverlayAnimationSequenceFromSpriteCount(IReadOnlyList<ImageEntry> images, ImageEntry baseImage)
    {
        if (!IsOverlaySpriteCountBaseCandidate(baseImage) || baseImage.Record.NumSprites <= 1)
        {
            return new List<ImageEntry>();
        }

        int expectedFrameCount = baseImage.Record.NumSprites;
        List<ImageEntry> frames = new(expectedFrameCount);
        for (int i = 1; i <= expectedFrameCount; i++)
        {
            ImageEntry? candidate = images.FirstOrDefault(x => x.DisplayId == baseImage.DisplayId + i);
            if (candidate == null)
            {
                return new List<ImageEntry>();
            }

            if (!IsOverlayAnimationFrameFromSpriteCountCompatible(baseImage, candidate))
            {
                return new List<ImageEntry>();
            }

            frames.Add(candidate);
        }

        return frames;
    }

    private static bool IsAnimationCandidateImage(ImageEntry image)
    {
        if (image == null || image.Record == null || image.Record.Width <= 0 || image.Record.Height <= 0)
        {
            return false;
        }

        return image.Record.HasData || image.Record.IsMirror || image.Record.MirrorOfIndex.HasValue;
    }

    private static bool IsDisplayableImage(ImageEntry image)
    {
        return image != null
            && image.Record != null
            && image.Record.Width > 0
            && image.Record.Height > 0
            && (image.Record.HasData || image.Record.IsMirror || image.Record.MirrorOfIndex.HasValue);
    }

    private static bool IsOverlayAnimationBaseCandidate(ImageEntry image)
    {
        return image != null
            && image.Record != null
            && image.Record.HasData
            && !image.Record.IsMirror
            && !image.Record.MirrorOfIndex.HasValue
            && image.Record.Width > 0
            && image.Record.Height > 0
            && image.Record.NumSprites <= 1;
    }

    private static bool IsOverlaySpriteCountBaseCandidate(ImageEntry image)
    {
        return image != null
            && image.Record != null
            && image.Record.HasData
            && !image.Record.IsMirror
            && !image.Record.MirrorOfIndex.HasValue
            && image.Record.Width > 0
            && image.Record.Height > 0
            && image.Record.NumSprites > 1;
    }

    private static bool IsOverlayAnimationFrameCompatible(ImageEntry baseImage, ImageEntry candidate)
    {
        if (!IsOverlayAnimationBaseCandidate(baseImage)
            || candidate == null
            || candidate.Record == null
            || !candidate.Record.HasData
            || candidate.Record.IsMirror
            || candidate.Record.MirrorOfIndex.HasValue)
        {
            return false;
        }

        return candidate.Record.NumSprites <= 1
            && candidate.Record.BitmapId == baseImage.Record.BitmapId
            && candidate.Record.GroupIndex == baseImage.Record.GroupIndex
            && string.Equals(candidate.GroupName, baseImage.GroupName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.SubgroupName, baseImage.SubgroupName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Source555Name, baseImage.Source555Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOverlayAnimationFrameFromSpriteCountCompatible(ImageEntry baseImage, ImageEntry candidate)
    {
        if (!IsOverlaySpriteCountBaseCandidate(baseImage)
            || candidate == null
            || candidate.Record == null
            || !candidate.Record.HasData
            || candidate.Record.IsMirror
            || candidate.Record.MirrorOfIndex.HasValue)
        {
            return false;
        }

        return candidate.Record.NumSprites == 0
            && candidate.Record.BitmapId == baseImage.Record.BitmapId
            && candidate.Record.Width > 0
            && candidate.Record.Height > 0;
    }

    private static bool IsAnimationFrameCompatible(ImageEntry reference, ImageEntry candidate)
    {
        if (reference == null || candidate == null || reference.Record == null || candidate.Record == null)
        {
            return false;
        }

        return reference.Record.NumSprites > 1
            && candidate.Record.NumSprites == reference.Record.NumSprites
            && candidate.Record.Type == reference.Record.Type
            && candidate.Record.BitmapId == reference.Record.BitmapId
            && string.Equals(candidate.Source555Name, reference.Source555Name, StringComparison.OrdinalIgnoreCase)
            && IsAnimationCandidateImage(candidate);
    }

    private static bool IsTerrainAnimationFrameCompatible(ImageEntry reference, ImageEntry candidate)
    {
        if (reference == null || candidate == null || reference.Record == null || candidate.Record == null)
        {
            return false;
        }

        return candidate.Record.NumSprites == 0
            && candidate.Record.Type == reference.Record.Type
            && candidate.Record.BitmapId == reference.Record.BitmapId
            && candidate.Record.Width == reference.Record.Width
            && candidate.Record.Height == reference.Record.Height
            && candidate.Record.GroupIndex == reference.Record.GroupIndex
            && candidate.Record.SpeedId == reference.Record.SpeedId
            && string.Equals(candidate.Source555Name, reference.Source555Name, StringComparison.OrdinalIgnoreCase)
            && candidate.Record.HasData
            && !candidate.Record.IsMirror;
    }

    private static bool IsAmbientStyleAnimationPack(SgContainer container)
    {
        if (container?.Bitmaps == null || container.StructuralSubgroups == null)
        {
            return false;
        }

        string[] names = container.Bitmaps
            .Select(x => (x?.FileName ?? string.Empty).ToUpperInvariant())
            .ToArray();
        string joined = string.Join("|", names);
        return container.StructuralSubgroups.Count >= 20
            && joined.Contains("CARGOFLOTSAM")
            && joined.Contains("BUBBLES")
            && joined.Contains("HIPPO")
            && joined.Contains("ANTELOPE");
    }

    private static bool IsTerrainStyleAnimationPack(SgContainer container)
    {
        if (container?.Bitmaps == null)
        {
            return false;
        }

        string[] names = container.Bitmaps
            .Select(x => (x?.FileName ?? string.Empty).ToUpperInvariant())
            .ToArray();
        string joined = string.Join("|", names);
        return joined.Contains("LAND1A")
            && joined.Contains("LAND2A")
            && joined.Contains("TRANSPORT");
    }

    private static bool IsEnemyStyleAnimationPack(SgContainer container)
    {
        if (container?.Bitmaps == null || container.StructuralSubgroups == null)
        {
            return false;
        }

        string[] names = container.Bitmaps
            .Select(x => (x?.FileName ?? string.Empty).ToUpperInvariant())
            .ToArray();
        string joined = string.Join("|", names);
        int subgroupCount = container.StructuralSubgroups.Count;
        bool hasMissile = joined.Contains("MISS");
        bool hasAux = joined.Contains("AUX");
        bool hasTransport = joined.Contains("TRANSPORT");
        bool hasWarship = joined.Contains("WARSHIP");
        bool hasChariot = joined.Contains("CHARIOT");

        if (!(hasMissile && hasAux && hasTransport && hasWarship))
        {
            return false;
        }

        return (names.Length == 5 && subgroupCount == 17)
            || (names.Length == 4 && subgroupCount == 10)
            || (hasChariot && subgroupCount >= 10);
    }

    private static Rectangle BuildPreviewAnimationBounds(IReadOnlyList<ImageEntry> frames, ImageEntry? baseImage = null)
    {
        IEnumerable<ImageEntry> allFrames = baseImage == null
            ? frames ?? Array.Empty<ImageEntry>()
            : (frames ?? Array.Empty<ImageEntry>()).Concat(new[] { baseImage });
        List<ImageEntry> frameList = allFrames.ToList();
        if (frameList.Count == 0)
        {
            return Rectangle.Empty;
        }

        Rectangle bounds = Rectangle.Empty;
        int maxWidth = 0;
        int maxHeight = 0;
        foreach (ImageEntry frame in frameList)
        {
            if (frame == null || frame.Record == null || frame.Record.Width <= 0 || frame.Record.Height <= 0)
            {
                continue;
            }

            maxWidth = Math.Max(maxWidth, frame.Record.Width);
            maxHeight = Math.Max(maxHeight, frame.Record.Height);
            Rectangle frameBounds = new(
                -frame.Record.SpriteOffsetX,
                -frame.Record.SpriteOffsetY,
                frame.Record.Width,
                frame.Record.Height);

            bounds = bounds.IsEmpty ? frameBounds : Rectangle.Union(bounds, frameBounds);
        }

        if (bounds.IsEmpty)
        {
            return Rectangle.Empty;
        }

        if (maxWidth > 0 && maxHeight > 0
            && (bounds.Width > maxWidth * 3 || bounds.Height > maxHeight * 3))
        {
            return Rectangle.Empty;
        }

        return bounds;
    }

    private static int GetPreviewAnimationInterval(ImageEntry image)
    {
        if (image == null || image.Record == null)
        {
            return 120;
        }

        int speedId = image.Record.SpeedId;
        if (speedId <= 0)
        {
            return 120;
        }

        return Math.Max(80, Math.Min(1000, 80 + speedId * 40));
    }

    private WriteableBitmap EnsurePreviewBitmap(int width, int height)
    {
        if (_previewBitmap == null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
        {
            _previewBitmap = new WriteableBitmap(width, height);
        }

        return _previewBitmap;
    }

    private async Task<bool?> AskIncludeSystemImagesAsync()
    {
        CheckBox checkBox = new()
        {
            Content = "Include system.bmp images in exported workspaces",
            IsChecked = !_hideSystemItems
        };

        ContentDialog dialog = new()
        {
            Title = "Batch Export",
            Content = checkBox,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot
        };

        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return checkBox.IsChecked == true;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                }
            },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };

        await dialog.ShowAsync();
    }

    private void SetBusy(string title, string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }

    private void SetReady(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Success;
        StatusInfoBar.Title = "Ready";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
        UpdateUiState();
    }

    private void SetError(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = "Error";
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        bool hasLoadedArchive = _currentArchiveItem?.Container != null;
        bool hasAnyArchives = _archiveItems.Count > 0;
        bool hasImage = _currentImage != null;

        SaveButton.IsEnabled = hasLoadedArchive;
        SaveAllButton.IsEnabled = _archiveItems.Any(static x => x.Container != null && (x.Container.HasPendingChanges || x.Container.IsLoose555));
        SaveImageButton.IsEnabled = hasImage;
        ReplaceImageButton.IsEnabled = hasImage && _currentImage?.Record.IsMirror == false && AnimatePreviewCheckBox.IsChecked != true;
        ArchivesListView.IsEnabled = hasAnyArchives;
        ImagesListView.IsEnabled = hasLoadedArchive;
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptOpenAsync();
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptSaveAsync();
    }

    private async void SaveAllButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptSaveAllAsync();
    }

    private async void BatchExportButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptBatchExportAsync();
    }

    private async void BatchImportButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptBatchImportAsync();
    }

    private async void ArchivesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressArchiveSelectionChanged) return;
        await LoadArchiveSelectionAsync(GetSelectedArchiveItem());
    }

    private async void ImagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressImageSelectionChanged) return;
        await UpdatePreviewAsync();
    }

    private async void PreviewAnimationTimer_Tick(object? sender, object e)
    {
        ImageEntry? selectedImage = GetSelectedImage();
        if (selectedImage == null || AnimatePreviewCheckBox.IsChecked != true || _previewAnimationFrames.Count <= 1)
        {
            _previewAnimationTimer.Stop();
            return;
        }

        if (_previewAnimationCanReverse && _previewAnimationFrames.Count > 1)
        {
            _previewAnimationFrameIndex += _previewAnimationDirection;
            if (_previewAnimationFrameIndex >= _previewAnimationFrames.Count)
            {
                _previewAnimationFrameIndex = Math.Max(0, _previewAnimationFrames.Count - 2);
                _previewAnimationDirection = -1;
            }
            else if (_previewAnimationFrameIndex < 0)
            {
                _previewAnimationFrameIndex = _previewAnimationFrames.Count > 1 ? 1 : 0;
                _previewAnimationDirection = 1;
            }
        }
        else
        {
            _previewAnimationFrameIndex++;
            if (_previewAnimationFrameIndex >= _previewAnimationFrames.Count)
            {
                _previewAnimationFrameIndex = 0;
            }
        }

        try
        {
            ImageEntry frame = _previewAnimationFrames[_previewAnimationFrameIndex];
            _previewAnimationTimer.Interval = TimeSpan.FromMilliseconds(GetPreviewAnimationInterval(frame));
            await ShowPreviewFrameAsync(selectedImage, frame, _previewAnimationFrameIndex + 1, _previewAnimationFrames.Count);
        }
        catch
        {
            _previewAnimationTimer.Stop();
        }
    }

    private async void SaveImageButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveSelectedImageAsync();
    }

    private async void ReplaceImageButton_Click(object sender, RoutedEventArgs e)
    {
        await ReplaceSelectedImageAsync();
    }

    private void HideSystemItemsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        int? preferredDisplayId = _currentImage?.DisplayId;
        _hideSystemItems = HideSystemItemsCheckBox.IsChecked != false;
        RefreshImageRows();
        _ = SelectImageAndUpdatePreviewAsync(preferredDisplayId);
    }

    private async void AnimatePreviewCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressAnimatePreviewToggle)
        {
            return;
        }

        _animatePreviewPreferred = AnimatePreviewCheckBox.IsChecked == true;
        await UpdatePreviewAsync();
    }

    private void LeftPaneThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _layoutTouched = true;
        ResizeColumns(LeftPaneColumn, MiddlePaneColumn, e.HorizontalChange);
        UpdateColumnRatios(LeftPaneColumn.Width.Value, MiddlePaneColumn.Width.Value, RightPaneColumn.ActualWidth);
        NormalizePaneLayout();
    }

    private void RightPaneThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _layoutTouched = true;
        ResizeColumns(MiddlePaneColumn, RightPaneColumn, e.HorizontalChange);
        UpdateColumnRatios(LeftPaneColumn.ActualWidth, MiddlePaneColumn.Width.Value, RightPaneColumn.Width.Value);
        NormalizePaneLayout();
    }

    private void PreviewDetailsThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _layoutTouched = true;
        ResizeRows(PreviewPaneRow, DetailsPaneRow, e.VerticalChange);
        UpdateRowRatios(PreviewPaneRow.Height.Value, DetailsPaneRow.Height.Value);
        NormalizePaneLayout();
    }

    private void PanelsGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pendingLayoutSettings != null && PanelsGrid.ActualWidth > 0)
        {
            ApplyTrackedRatiosFromSettings(_pendingLayoutSettings);
            _pendingLayoutSettings = null;
        }

        NormalizePaneLayout();
    }

    private void NormalizePaneLayout()
    {
        if (_isNormalizingPaneLayout)
        {
            return;
        }

        _isNormalizingPaneLayout = true;
        try
        {
            NormalizeColumnsToAvailableWidth();
            NormalizeRowsToAvailableHeight();
        }
        finally
        {
            _isNormalizingPaneLayout = false;
        }
    }

    private void NormalizeColumnsToAvailableWidth()
    {
        if (PanelsGrid.ActualWidth <= 0)
        {
            return;
        }

        double splitterWidth = PanelsGrid.ColumnDefinitions[1].ActualWidth + PanelsGrid.ColumnDefinitions[3].ActualWidth;
        double availableWidth = Math.Max(0, PanelsGrid.ActualWidth - splitterWidth);
        double minLeft = LeftPaneColumn.MinWidth;
        double minMiddle = MiddlePaneColumn.MinWidth;
        double minRight = RightPaneColumn.MinWidth;
        double ratioTotal = Math.Max(LayoutAdjustmentEpsilon, _leftPaneRatio + _middlePaneRatio + _rightPaneRatio);
        double left = ResolveTrackedSize(LeftPaneColumn.ActualWidth, minLeft, availableWidth * (_leftPaneRatio / ratioTotal));
        double middle = ResolveTrackedSize(MiddlePaneColumn.ActualWidth, minMiddle, availableWidth * (_middlePaneRatio / ratioTotal));
        double right = ResolveTrackedSize(RightPaneColumn.ActualWidth, minRight, availableWidth * (_rightPaneRatio / ratioTotal));

        (double normalizedLeft, double normalizedMiddle, double normalizedRight) = NormalizeSizes(
            availableWidth,
            (left, minLeft),
            (middle, minMiddle),
            (right, minRight));

        ApplyColumnWidthIfNeeded(LeftPaneColumn, normalizedLeft);
        ApplyColumnWidthIfNeeded(MiddlePaneColumn, normalizedMiddle);
        ApplyColumnWidthIfNeeded(RightPaneColumn, normalizedRight);
        UpdateColumnRatios(normalizedLeft, normalizedMiddle, normalizedRight);
    }

    private void NormalizeRowsToAvailableHeight()
    {
        if (PreviewDetailsGrid is null
            || PreviewPaneRow is null
            || DetailsPaneRow is null
            || PreviewDetailsGrid.RowDefinitions.Count < 4)
        {
            return;
        }

        if (PreviewDetailsGrid.ActualHeight <= 0)
        {
            return;
        }

        double fixedHeight = PreviewDetailsGrid.RowDefinitions[0].ActualHeight + PreviewDetailsGrid.RowDefinitions[2].ActualHeight;
        double availableHeight = Math.Max(0, PreviewDetailsGrid.ActualHeight - fixedHeight);
        double minPreviewHeight = PreviewPaneRow.MinHeight;
        double minDetailsHeight = DetailsPaneRow.MinHeight;
        double ratioTotal = Math.Max(LayoutAdjustmentEpsilon, _previewPaneRatio + _detailsPaneRatio);
        double previewHeight = ResolveTrackedSize(PreviewPaneRow.ActualHeight, minPreviewHeight, availableHeight * (_previewPaneRatio / ratioTotal));
        double detailsHeight = ResolveTrackedSize(DetailsPaneRow.ActualHeight, minDetailsHeight, availableHeight * (_detailsPaneRatio / ratioTotal));

        (double normalizedPreview, double normalizedDetails) = NormalizeSizes(
            availableHeight,
            (previewHeight, minPreviewHeight),
            (detailsHeight, minDetailsHeight));

        ApplyRowHeightIfNeeded(PreviewPaneRow, normalizedPreview);
        ApplyRowHeightIfNeeded(DetailsPaneRow, normalizedDetails);
        UpdateRowRatios(normalizedPreview, normalizedDetails);
    }

    private void UpdatePaneRatiosFromDefinitions()
    {
        UpdateColumnRatios(LeftPaneColumn.ActualWidth, MiddlePaneColumn.ActualWidth, RightPaneColumn.ActualWidth);
        UpdateRowRatios(PreviewPaneRow.ActualHeight, DetailsPaneRow.ActualHeight);
    }

    private void ApplyTrackedRatiosFromSettings(SgToolLayoutSettings settings)
    {
        UpdateColumnRatios(settings.LeftPaneWidth, settings.MiddlePaneWidth, settings.RightPaneWidth);
        UpdateRowRatios(settings.PreviewPaneHeight, settings.DetailsPaneHeight);
    }

    private void UpdateColumnRatios(double left, double middle, double right)
    {
        double safeLeft = Math.Max(LeftPaneColumn.MinWidth, left);
        double safeMiddle = Math.Max(MiddlePaneColumn.MinWidth, middle);
        double safeRight = Math.Max(RightPaneColumn.MinWidth, right);
        double total = safeLeft + safeMiddle + safeRight;
        if (total <= LayoutAdjustmentEpsilon)
        {
            _leftPaneRatio = DefaultLeftPaneRatio;
            _middlePaneRatio = DefaultMiddlePaneRatio;
            _rightPaneRatio = DefaultRightPaneRatio;
            return;
        }

        _leftPaneRatio = safeLeft / total;
        _middlePaneRatio = safeMiddle / total;
        _rightPaneRatio = safeRight / total;
    }

    private void UpdateRowRatios(double preview, double details)
    {
        double safePreview = Math.Max(PreviewPaneRow.MinHeight, preview);
        double safeDetails = Math.Max(DetailsPaneRow.MinHeight, details);
        double total = safePreview + safeDetails;
        if (total <= LayoutAdjustmentEpsilon)
        {
            _previewPaneRatio = DefaultPreviewPaneRatio;
            _detailsPaneRatio = DefaultDetailsPaneRatio;
            return;
        }

        _previewPaneRatio = safePreview / total;
        _detailsPaneRatio = safeDetails / total;
    }

    private static double ResolveTrackedSize(double actualSize, double minSize, double fallbackSize)
    {
        if (actualSize > LayoutAdjustmentEpsilon)
        {
            return actualSize;
        }

        return Math.Max(minSize, fallbackSize);
    }

    private static (double, double) NormalizeSizes(double available, (double current, double min) first, (double current, double min) second)
    {
        double minTotal = first.min + second.min;
        if (available <= minTotal + LayoutAdjustmentEpsilon)
        {
            return (first.min, second.min);
        }

        double currentTotal = Math.Max(1, first.current + second.current);
        double extraAvailable = available - minTotal;
        double firstWeight = Math.Max(0, first.current - first.min);
        double secondWeight = Math.Max(0, second.current - second.min);
        double totalWeight = firstWeight + secondWeight;
        if (totalWeight <= LayoutAdjustmentEpsilon)
        {
            firstWeight = Math.Max(1, first.current);
            secondWeight = Math.Max(1, second.current);
            totalWeight = firstWeight + secondWeight;
        }

        double normalizedFirst = first.min + extraAvailable * (firstWeight / totalWeight);
        double normalizedSecond = available - normalizedFirst;
        return (normalizedFirst, normalizedSecond);
    }

    private static (double, double, double) NormalizeSizes(double available, (double current, double min) first, (double current, double min) second, (double current, double min) third)
    {
        double minTotal = first.min + second.min + third.min;
        if (available <= minTotal + LayoutAdjustmentEpsilon)
        {
            return (first.min, second.min, third.min);
        }

        double extraAvailable = available - minTotal;
        double firstWeight = Math.Max(0, first.current - first.min);
        double secondWeight = Math.Max(0, second.current - second.min);
        double thirdWeight = Math.Max(0, third.current - third.min);
        double totalWeight = firstWeight + secondWeight + thirdWeight;
        if (totalWeight <= LayoutAdjustmentEpsilon)
        {
            firstWeight = Math.Max(1, first.current);
            secondWeight = Math.Max(1, second.current);
            thirdWeight = Math.Max(1, third.current);
            totalWeight = firstWeight + secondWeight + thirdWeight;
        }

        double normalizedFirst = first.min + extraAvailable * (firstWeight / totalWeight);
        double normalizedSecond = second.min + extraAvailable * (secondWeight / totalWeight);
        double normalizedThird = available - normalizedFirst - normalizedSecond;
        if (normalizedThird < third.min)
        {
            double deficit = third.min - normalizedThird;
            normalizedThird = third.min;
            double reducibleSecond = Math.Max(0, normalizedSecond - second.min);
            double takeFromSecond = Math.Min(deficit, reducibleSecond);
            normalizedSecond -= takeFromSecond;
            deficit -= takeFromSecond;
            if (deficit > 0)
            {
                normalizedFirst = Math.Max(first.min, normalizedFirst - deficit);
            }
        }

        return (normalizedFirst, normalizedSecond, normalizedThird);
    }

    private static void ApplyColumnWidthIfNeeded(ColumnDefinition column, double width)
    {
        if (Math.Abs(column.ActualWidth - width) <= LayoutAdjustmentEpsilon)
        {
            return;
        }

        column.Width = new GridLength(width, GridUnitType.Pixel);
    }

    private static void ApplyRowHeightIfNeeded(RowDefinition row, double height)
    {
        if (Math.Abs(row.ActualHeight - height) <= LayoutAdjustmentEpsilon)
        {
            return;
        }

        row.Height = new GridLength(height, GridUnitType.Pixel);
    }

    private static void ResizeColumns(ColumnDefinition leftColumn, ColumnDefinition rightColumn, double horizontalChange)
    {
        double leftWidth = leftColumn.ActualWidth;
        double rightWidth = rightColumn.ActualWidth;
        double newLeft = Math.Max(leftColumn.MinWidth, leftWidth + horizontalChange);
        double newRight = Math.Max(rightColumn.MinWidth, rightWidth - horizontalChange);

        double deltaCorrection = (newLeft - leftWidth) + (newRight - rightWidth);
        if (Math.Abs(deltaCorrection) > 0.01)
        {
            if (horizontalChange > 0)
            {
                newLeft = Math.Max(leftColumn.MinWidth, newLeft - deltaCorrection);
            }
            else
            {
                newRight = Math.Max(rightColumn.MinWidth, newRight - deltaCorrection);
            }
        }

        leftColumn.Width = new GridLength(newLeft, GridUnitType.Pixel);
        rightColumn.Width = new GridLength(newRight, GridUnitType.Pixel);
    }

    private static void ResizeRows(RowDefinition topRow, RowDefinition bottomRow, double verticalChange)
    {
        double topHeight = topRow.ActualHeight;
        double bottomHeight = bottomRow.ActualHeight;
        double newTop = Math.Max(topRow.MinHeight, topHeight + verticalChange);
        double newBottom = Math.Max(bottomRow.MinHeight, bottomHeight - verticalChange);

        double deltaCorrection = (newTop - topHeight) + (newBottom - bottomHeight);
        if (Math.Abs(deltaCorrection) > 0.01)
        {
            if (verticalChange > 0)
            {
                newTop = Math.Max(topRow.MinHeight, newTop - deltaCorrection);
            }
            else
            {
                newBottom = Math.Max(bottomRow.MinHeight, newBottom - deltaCorrection);
            }
        }

        topRow.Height = new GridLength(newTop, GridUnitType.Pixel);
        bottomRow.Height = new GridLength(newBottom, GridUnitType.Pixel);
    }

    private async void SortHeaderTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is not TextBlock textBlock || textBlock.Tag is not string tag)
        {
            return;
        }

        string[] parts = tag.Split(':');
        if (parts.Length != 2)
        {
            return;
        }

        bool additive = IsShiftSortModifierActive();
        List<SortCriterion> criteria = string.Equals(parts[0], "Archive", StringComparison.OrdinalIgnoreCase)
            ? _archiveSortCriteria
            : _imageSortCriteria;
        ToggleSort(criteria, parts[1], additive);
        UpdateSortHeaderButtons();

        if (string.Equals(parts[0], "Archive", StringComparison.OrdinalIgnoreCase))
        {
            RefreshArchiveRows(_currentArchiveItem?.Path);
            if (_currentArchiveItem != null)
            {
                await LoadArchiveSelectionAsync(_currentArchiveItem);
            }
        }
        else
        {
            int? preferredDisplayId = _currentImage?.DisplayId;
            RefreshImageRows();
            await SelectImageAndUpdatePreviewAsync(preferredDisplayId);
        }
    }

    private static bool IsShiftSortModifierActive()
    {
        return Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private static void ToggleSort(List<SortCriterion> criteria, string key, bool additive)
    {
        SortCriterion? existing = criteria.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal));
        if (!additive)
        {
            if (existing == null)
            {
                criteria.Clear();
                criteria.Add(new SortCriterion { Key = key, Descending = false });
            }
            else
            {
                bool nextDescending = !existing.Descending;
                criteria.Clear();
                criteria.Add(new SortCriterion { Key = key, Descending = nextDescending });
            }

            return;
        }

        if (existing == null)
        {
            criteria.Add(new SortCriterion { Key = key, Descending = false });
        }
        else
        {
            existing.Descending = !existing.Descending;
        }
    }

    private IEnumerable<ArchiveItem> SortArchiveItems(IEnumerable<ArchiveItem> items)
    {
        List<ArchiveItem> ordered = items.ToList();
        if (ordered.Count <= 1)
        {
            return ordered;
        }

        ordered.Sort((a, b) =>
        {
            foreach (SortCriterion criterion in _archiveSortCriteria)
            {
                int result = criterion.Key switch
                {
                    "DisplayName" => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
                    "Type" => string.Compare(a.IsLoose555 ? "555" : "SG3", b.IsLoose555 ? "555" : "SG3", StringComparison.OrdinalIgnoreCase),
                    "State" => string.Compare(GetArchiveStateText(a), GetArchiveStateText(b), StringComparison.OrdinalIgnoreCase),
                    _ => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase)
                };

                if (result != 0)
                {
                    return criterion.Descending ? -result : result;
                }
            }

            return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        return ordered;
    }

    private IEnumerable<ImageEntry> SortImages(IEnumerable<ImageEntry> images)
    {
        List<ImageEntry> ordered = images.ToList();
        if (ordered.Count <= 1)
        {
            return ordered;
        }

        ordered.Sort((a, b) =>
        {
            foreach (SortCriterion criterion in _imageSortCriteria)
            {
                int result = CompareImageEntriesByKey(a, b, criterion.Key);
                if (result != 0)
                {
                    return criterion.Descending ? -result : result;
                }
            }

            return a.DisplayId.CompareTo(b.DisplayId);
        });

        return ordered;
    }

    private static int CompareImageEntriesByKey(ImageEntry a, ImageEntry b, string key)
    {
        return key switch
        {
            "Id" => a.DisplayId.CompareTo(b.DisplayId),
            "Name" => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            "Group" => string.Compare(a.GroupName, b.GroupName, StringComparison.OrdinalIgnoreCase),
            "Subgroup" => string.Compare(a.SubgroupName, b.SubgroupName, StringComparison.OrdinalIgnoreCase),
            "Type" => a.Record.Type.CompareTo(b.Record.Type),
            "Size" => a.Record.Width != b.Record.Width
                ? a.Record.Width.CompareTo(b.Record.Width)
                : a.Record.Height.CompareTo(b.Record.Height),
            "Source" => string.Compare(a.Source555Name, b.Source555Name, StringComparison.OrdinalIgnoreCase),
            "Mirror" => Nullable.Compare(a.Record.MirrorOfIndex, b.Record.MirrorOfIndex),
            _ => a.DisplayId.CompareTo(b.DisplayId)
        };
    }

    private void UpdateSortHeaderButtons()
    {
        UpdateSortButtonContent(ArchiveDisplayNameSortButton, "Available Files", _archiveSortCriteria, "DisplayName");
        UpdateSortButtonContent(ArchiveTypeSortButton, "Type", _archiveSortCriteria, "Type");
        UpdateSortButtonContent(ArchiveStateSortButton, "State", _archiveSortCriteria, "State");

        UpdateSortButtonContent(ImageIdSortButton, "ID", _imageSortCriteria, "Id");
        UpdateSortButtonContent(ImageNameSortButton, "Name", _imageSortCriteria, "Name");
        UpdateSortButtonContent(ImageGroupSortButton, "Group", _imageSortCriteria, "Group");
        UpdateSortButtonContent(ImageSubgroupSortButton, "Subgroup", _imageSortCriteria, "Subgroup");
        UpdateSortButtonContent(ImageTypeSortButton, "Type", _imageSortCriteria, "Type");
        UpdateSortButtonContent(ImageSizeSortButton, "Size", _imageSortCriteria, "Size");
        UpdateSortButtonContent(ImageSourceSortButton, "Source", _imageSortCriteria, "Source");
        UpdateSortButtonContent(ImageMirrorSortButton, "Mirror", _imageSortCriteria, "Mirror");
    }

    private static void UpdateSortButtonContent(TextBlock textBlock, string baseText, List<SortCriterion> criteria, string key)
    {
        int index = criteria.FindIndex(x => string.Equals(x.Key, key, StringComparison.Ordinal));
        if (index < 0)
        {
            textBlock.Text = baseText;
            return;
        }

        SortCriterion criterion = criteria[index];
        string arrow = criterion.Descending ? "\u25BC" : "\u25B2";
        string orderText = criteria.Count > 1 ? (index + 1).ToString() : string.Empty;
        textBlock.Text = $"{baseText} {arrow}{orderText}";
    }
}

