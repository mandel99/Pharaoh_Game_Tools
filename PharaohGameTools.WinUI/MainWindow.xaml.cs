// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT.Interop;

namespace PharaohGameTools.WinUI;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidthDip = 900;
    private const int DefaultWindowHeightDip = 600;
    private readonly LayoutSettings _layoutSettings;
    private bool _initialLayoutApplied;
    private bool _closeConfirmed;
    private bool _closePromptInProgress;

    public MainWindow()
    {
        InitializeComponent();
        _layoutSettings = LayoutSettingsStore.Load();
        MenuHideSystemItemsItem.IsChecked = true;

        // Use a dedicated drag bar above the menu so the window always
        // has a clear, non-interactive region for moving it around.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowDragBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        UpdateMenuForActiveTab();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialLayoutApplied)
        {
            return;
        }

        _initialLayoutApplied = true;
        ApplyWindowLayout();
        SgToolViewHost.ApplyLayout(_layoutSettings.SgTool);
    }

    private void ApplyWindowLayout()
    {
        MainWindowLayoutSettings settings = _layoutSettings.MainWindow;
        bool hasSavedBounds = settings.HasSavedBounds || settings.X != 0 || settings.Y != 0;
        SizeInt32 defaultScaledSize = GetScaledDefaultWindowSize();
        int width = hasSavedBounds ? Math.Max(DefaultWindowWidthDip, settings.Width) : defaultScaledSize.Width;
        int height = hasSavedBounds ? Math.Max(DefaultWindowHeightDip, settings.Height) : defaultScaledSize.Height;
        RectInt32 targetRect = hasSavedBounds
            ? new RectInt32(settings.X, settings.Y, width, height)
            : CreateCenteredRect(width, height);

        targetRect = EnsureVisibleRect(targetRect);

        AppWindow.MoveAndResize(targetRect);
    }

    private SizeInt32 GetScaledDefaultWindowSize()
    {
        double scale = Content?.XamlRoot?.RasterizationScale ?? GetWindowDpiScale();
        int width = Math.Max(DefaultWindowWidthDip, (int)Math.Round(DefaultWindowWidthDip * scale));
        int height = Math.Max(DefaultWindowHeightDip, (int)Math.Round(DefaultWindowHeightDip * scale));
        return new SizeInt32(width, height);
    }

    private double GetWindowDpiScale()
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
            {
                return 1.0;
            }

            uint dpi = GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    private RectInt32 EnsureVisibleRect(RectInt32 rect)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;

        int width = Math.Min(rect.Width, workArea.Width);
        int height = Math.Min(rect.Height, workArea.Height);

        bool isOutOfBounds =
            rect.X < workArea.X ||
            rect.Y < workArea.Y ||
            rect.X + rect.Width > workArea.X + workArea.Width ||
            rect.Y + rect.Height > workArea.Y + workArea.Height;

        if (!isOutOfBounds)
        {
            return new RectInt32(rect.X, rect.Y, width, height);
        }

        return CreateCenteredRect(width, height);
    }

    private RectInt32 CreateCenteredRect(int width, int height)
    {
        DisplayArea displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 workArea = displayArea.WorkArea;
        int boundedWidth = Math.Min(width, workArea.Width);
        int boundedHeight = Math.Min(height, workArea.Height);
        int centeredX = workArea.X + Math.Max(0, (workArea.Width - boundedWidth) / 2);
        int centeredY = workArea.Y + Math.Max(0, (workArea.Height - boundedHeight) / 2);
        return new RectInt32(centeredX, centeredY, boundedWidth, boundedHeight);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        BikPlayerViewHost?.Shutdown();
        MainWindowLayoutSettings settings = _layoutSettings.MainWindow;
        settings.HasSavedBounds = true;
        settings.X = AppWindow.Position.X;
        settings.Y = AppWindow.Position.Y;
        settings.Width = AppWindow.Size.Width;
        settings.Height = AppWindow.Size.Height;
        _layoutSettings.SgTool = SgToolViewHost.CaptureLayout();
        LayoutSettingsStore.Save(_layoutSettings);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closeConfirmed || _closePromptInProgress)
        {
            return;
        }

        string unsavedSummary = BuildUnsavedChangesSummary();
        if (string.IsNullOrWhiteSpace(unsavedSummary))
        {
            return;
        }

        args.Cancel = true;
        _closePromptInProgress = true;
        _ = ConfirmCloseWithUnsavedChangesAsync(unsavedSummary);
    }

    private async Task ConfirmCloseWithUnsavedChangesAsync(string unsavedSummary)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Unsaved Changes",
                Content = new TextBlock
                {
                    Text = "The following tools contain unsaved changes:"
                        + Environment.NewLine
                        + Environment.NewLine
                        + unsavedSummary
                        + Environment.NewLine
                        + Environment.NewLine
                        + "Do you want to close the application without saving?",
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = "Close Without Saving",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        finally
        {
            _closePromptInProgress = false;
        }
    }

    private string BuildUnsavedChangesSummary()
    {
        var lines = new StringBuilder();

        if (SgToolViewHost?.HasUnsavedArchiveChanges() == true)
        {
            lines.AppendLine("- SG3 Tool");
        }

        if (TextToolViewHost?.HasUnsavedDocumentChanges() == true)
        {
            lines.AppendLine("- Text Tool");
        }

        if (PakToolViewHost?.HasUnsavedPakChanges() == true)
        {
            lines.AppendLine("- PAK Tool");
        }

        return lines.ToString().TrimEnd();
    }

    private async void OpenFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        switch (TabControl.SelectedIndex)
        {
            case 0:
                await SgToolViewHost.PromptOpenAsync();
                break;
            case 1:
                await TextToolViewHost.PromptOpenAsync();
                break;
            case 2:
                await PakToolViewHost.PromptOpenAsync();
                break;
            case 3:
                await BikPlayerViewHost.PromptOpenAsync();
                break;
            default:
                await ShowMessageAsync("Open File", "Select a tool tab first.");
                break;
        }
    }

    private async void SaveFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        switch (TabControl.SelectedIndex)
        {
            case 0:
                await SgToolViewHost.PromptSaveAsync();
                break;
            case 1:
                await PromptSaveFromTextToolAsync();
                break;
            case 2:
                await PromptSaveFromPakToolAsync();
                break;
            case 3:
                await ShowMessageAsync("Save File", "Bik Player does not save source BIK files. Use Export AVI or Export MP4 inside the tab.");
                break;
            default:
                await ShowMessageAsync("Save File", "There is nothing to save in the current tab.");
                break;
        }
    }

    private async void SaveAllMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TabControl.SelectedIndex == 0)
        {
            await SgToolViewHost.PromptSaveAllAsync();
            return;
        }

        await ShowMessageAsync("Save All", "Save All is only available for the sg3/555 tab.");
    }

    private async void BatchExportMenuItem_Click(object sender, RoutedEventArgs e)
    {
        switch (TabControl.SelectedIndex)
        {
            case 0:
                await SgToolViewHost.PromptBatchExportAsync();
                break;
            case 2:
                await PakToolViewHost.PromptBatchExportAsync();
                break;
            default:
                await ShowMessageAsync("Batch Export", "Batch export is not available for the current tab.");
                break;
        }
    }

    private async void BatchImportMenuItem_Click(object sender, RoutedEventArgs e)
    {
        switch (TabControl.SelectedIndex)
        {
            case 0:
                await SgToolViewHost.PromptBatchImportAsync();
                break;
            case 2:
                await PakToolViewHost.PromptBatchImportAsync();
                break;
            default:
                await ShowMessageAsync("Batch Import", "Batch import is not available for the current tab.");
                break;
        }
    }

    private async void HideSystemItemsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TabControl.SelectedIndex == 0)
        {
            SgToolViewHost.SetHideSystemItems(MenuHideSystemItemsItem.IsChecked);
            return;
        }

        if (TabControl.SelectedIndex != 0)
        {
            await ShowMessageAsync("View", "Hide system items applies to the sg3/555 image browser.");
        }
    }

    private async void SgToolHelpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("sg3/555 Tool Help", BuildSgToolHelpText());
    }

    private async void TextToolHelpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("Text Tool Help", BuildTextToolHelpText());
    }

    private async void PakToolHelpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("PAK Tool Help", BuildPakToolHelpText());
    }

    private async void BikToolHelpMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowMessageAsync("Bik Player Help", BuildBikToolHelpText());
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMenuForActiveTab();
    }

    private void UpdateMenuForActiveTab()
    {
        bool isSgTool = TabControl.SelectedIndex == 0;
        bool isTextTool = TabControl.SelectedIndex == 1;
        bool isPakTool = TabControl.SelectedIndex == 2;
        bool isBikTool = TabControl.SelectedIndex == 3;

        MenuSaveAllItem.IsEnabled = isSgTool;
        MenuBatchExportItem.IsEnabled = isSgTool || isPakTool;
        MenuBatchImportItem.IsEnabled = isSgTool || isPakTool;
        MenuHideSystemItemsItem.IsEnabled = isSgTool;
        if (isSgTool)
        {
            MenuHideSystemItemsItem.IsChecked = SgToolViewHost.GetHideSystemItems();
        }

        if (isTextTool)
        {
            MenuOpenFileItem.Text = "Open File...";
            MenuSaveFileItem.Text = "Save File...";
        }
        else if (isBikTool)
        {
            MenuOpenFileItem.Text = "Open File...";
            MenuSaveFileItem.Text = "Save File...";
        }
        else if (isPakTool || isSgTool)
        {
            MenuOpenFileItem.Text = "Open File...";
            MenuSaveFileItem.Text = "Save File...";
        }
    }

    private async Task PromptSaveFromTextToolAsync()
    {
        if (TextToolViewHost == null)
        {
            return;
        }

        await TextToolViewHost.PromptSaveUsingSourceFormatAsync();
    }

    private async Task PromptSaveFromPakToolAsync()
    {
        if (PakToolViewHost == null)
        {
            return;
        }

        await PakToolViewHost.PromptSaveAsync();
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
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
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static string BuildSgToolHelpText()
    {
        var help = new StringBuilder();
        help.AppendLine("sg3/555 Tool");
        help.AppendLine();
        help.AppendLine("Purpose");
        help.AppendLine("Browse SG3 archives, inspect image records, preview sprites, replace images, and save rebuilt SG3/555 data.");
        help.AppendLine();
        help.AppendLine("Main workflow");
        help.AppendLine("1. Open one or more SG3 files from File > Open File...");
        help.AppendLine("2. Select a package in the left list.");
        help.AppendLine("3. Browse image entries in the middle list.");
        help.AppendLine("4. Preview the selected image on the right.");
        help.AppendLine("5. Use Save Image or Replace Image for the selected entry.");
        help.AppendLine("6. Use Save File... or Save All... to write your changes.");
        help.AppendLine();
        help.AppendLine("Notes");
        help.AppendLine("- The middle list supports sorting by clicking column headers.");
        help.AppendLine("- The View menu can hide common system.bmp entries.");
        help.AppendLine("- Mirrored images are resolved at runtime. Batch export writes only the original non-mirrored source images.");
        help.AppendLine("- Batch Export scans a folder of SG3 files and exports PNG workspaces.");
        help.AppendLine("- Batch Import rebuilds SG3/555 files from those exported PNG workspaces.");
        help.AppendLine();
        help.AppendLine("Saving behavior");
        help.AppendLine("- Unsaved changes are tracked in the file list.");
        help.AppendLine("- The application warns before closing when SG3 files contain unsaved changes.");
        return help.ToString();
    }

    private static string BuildTextToolHelpText()
    {
        var help = new StringBuilder();
        help.AppendLine("Text Tool");
        help.AppendLine();
        help.AppendLine("Purpose");
        help.AppendLine("Open, view, convert, edit, and save TXT and ENG text resources.");
        help.AppendLine();
        help.AppendLine("Main workflow");
        help.AppendLine("1. Open a TXT or ENG file from File > Open File... or from the Text Tool button.");
        help.AppendLine("2. Choose the text encoding from the selector under the buttons.");
        help.AppendLine("3. Review or edit the text in the editor.");
        help.AppendLine("4. Save the current content as TXT or ENG.");
        help.AppendLine();
        help.AppendLine("Notes");
        help.AppendLine("- Changing the selected encoding reloads the currently open source file.");
        help.AppendLine("- ENG files are converted to editable text in the editor and can be written back to ENG.");
        help.AppendLine("- The status label shows the detected file type and active encoding.");
        help.AppendLine("- Save File... follows the currently open file type when the Text Tool tab is active.");
        return help.ToString();
    }

    private static string BuildPakToolHelpText()
    {
        var help = new StringBuilder();
        help.AppendLine("PAK Tool");
        help.AppendLine();
        help.AppendLine("Purpose");
        help.AppendLine("Open Pharaoh mission PAK archives, inspect contained SAV entries, replace entries, and rebuild the archive.");
        help.AppendLine();
        help.AppendLine("Main workflow");
        help.AppendLine("1. Open a PAK file from File > Open File... or the Open PAK button.");
        help.AppendLine("2. Browse and sort entries in the list by Name, File, Offset, Size, or State.");
        help.AppendLine("3. Extract an entry or replace it with a SAV or MAP file.");
        help.AppendLine("4. Use Save PAK or Save File... to write the rebuilt archive.");
        help.AppendLine();
        help.AppendLine("Batch operations");
        help.AppendLine("- Batch Export writes the current PAK entries as individual SAV files.");
        help.AppendLine("- Batch Import matches files back by their numeric prefix.");
        help.AppendLine("- Missing imported files keep the original PAK entry unchanged.");
        help.AppendLine("- Extra files are ignored and reported.");
        help.AppendLine("- If import errors occur, a report is shown before saving and you can choose whether to save the rebuilt PAK.");
        help.AppendLine();
        help.AppendLine("Notes");
        help.AppendLine("- Entry offsets are recalculated on save, so larger or smaller replacements are supported.");
        help.AppendLine("- The application warns before closing when the loaded PAK has unsaved changes.");
        return help.ToString();
    }

    private static string BuildBikToolHelpText()
    {
        var help = new StringBuilder();
        help.AppendLine("Bik Player");
        help.AppendLine();
        help.AppendLine("Purpose");
        help.AppendLine("Open, preview, and export BIKf video files directly inside the viewer.");
        help.AppendLine();
        help.AppendLine("Main workflow");
        help.AppendLine("1. Open a BIK file from File > Open File... or the Open BIK button.");
        help.AppendLine("2. Review the detected resolution, frame count, and frame rate.");
        help.AppendLine("3. Use Play/Pause to start or pause playback.");
        help.AppendLine("4. Use Stop to return to the beginning.");
        help.AppendLine("5. Use Export AVI... or Export MP4... to write a converted video file.");
        help.AppendLine();
        help.AppendLine("Notes");
        help.AppendLine("- Audio playback is supported for the currently loaded BIK file.");
        help.AppendLine("- The decoder currently supports plain single-track BIKf videos, which matches the source runtime implementation.");
        help.AppendLine("- AVI export uses MJPEG for video and uncompressed PCM for audio.");
        help.AppendLine("- MP4 export currently uses MJPEG for video and PCM audio in an experimental MP4 layout.");
        return help.ToString();
    }
}

