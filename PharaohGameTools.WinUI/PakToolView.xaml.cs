using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PharaohGameTools.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PharaohGameTools.WinUI;

public sealed partial class PakToolView : UserControl
{
    private readonly ObservableCollection<PakEntryRow> _rows = new();
    private PakContainer? _pakContainer;

    private sealed class PakBatchImportResult
    {
        public PakContainer? WorkingContainer { get; set; }
        public List<string> ReplacedEntries { get; } = new();
        public List<string> MissingEntries { get; } = new();
        public List<string> ExtraEntries { get; } = new();
        public List<string> ErrorEntries { get; } = new();
        public int ExpectedEntryCount { get; set; }
        public int ImportedFileCount { get; set; }
        public bool CountMismatch { get; set; }
    }

    private sealed class PakEntryRow
    {
        public required PakEntry Entry { get; init; }
        public required string IdText { get; init; }
        public required string DisplayName { get; init; }
        public required string MissionDisplayName { get; init; }
        public required string FileDisplayName { get; init; }
        public required string FileVersionText { get; init; }
        public required string OffsetText { get; init; }
        public required string SizeText { get; init; }
        public required string StateText { get; init; }
    }

    public PakToolView()
    {
        InitializeComponent();
        EntriesListView.ItemsSource = _rows;
        UpdateSelectionState();
    }

    public async Task PromptOpenAsync()
    {
        FileOpenPicker picker = PickerInterop.CreateOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".pak");
        picker.FileTypeFilter.Add("*");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            await OpenPakFileAsync(file.Path);
        }
    }

    public async Task PromptSaveAsync()
    {
        if (_pakContainer == null)
        {
            await ShowMessageAsync("Save PAK", "Open a PAK file first.");
            return;
        }

        FileSavePicker picker = PickerInterop.CreateSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = Path.GetFileName(_pakContainer.SourcePath);
        picker.FileTypeChoices.Add("PAK file", new[] { ".pak" });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            SetBusy("Saving PAK...", "Writing rebuilt archive...");
            await Task.Run(() => PakArchive.Save(_pakContainer, file.Path));
            RefreshEntryList();
            SetReady("Saved: " + file.Path);
        }
        catch (Exception ex)
        {
            SetError("Save failed.");
            await ShowMessageAsync("Save PAK Failed", ex.Message);
        }
    }

    public async Task PromptBatchExportAsync()
    {
        if (!await EnsurePakLoadedForBatchOperationAsync())
        {
            return;
        }

        FolderPicker picker = PickerInterop.CreateFolderPicker();
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder == null || _pakContainer == null)
        {
            return;
        }

        string root = Path.Combine(folder.Path, Path.GetFileNameWithoutExtension(_pakContainer.SourcePath));
        try
        {
            SetBusy("Exporting PAK entries...", "Writing SAV files...");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(root);
                foreach (PakEntry entry in _pakContainer.Entries.OrderBy(x => x.Index))
                {
                    string fileName = BuildPakExportFileName(entry);
                    File.WriteAllBytes(Path.Combine(root, fileName), entry.Data ?? Array.Empty<byte>());
                }
            });

            SetReady($"Exported {_pakContainer.Entries.Count} entries to {root}");
            UpdateSelectionInfo(GetSelectedEntry());
        }
        catch (Exception ex)
        {
            SetError("Batch export failed.");
            await ShowMessageAsync("Batch Export Failed", ex.Message);
        }
    }

    public bool HasUnsavedPakChanges()
    {
        return _pakContainer?.HasPendingChanges == true;
    }

    public async Task PromptBatchImportAsync()
    {
        if (_pakContainer == null)
        {
            await ShowMessageAsync("Batch Import", "Open a PAK file first.");
            return;
        }

        FolderPicker importPicker = PickerInterop.CreateFolderPicker();
        StorageFolder? importFolder = await importPicker.PickSingleFolderAsync();
        if (importFolder == null)
        {
            return;
        }

        try
        {
            SetBusy("Importing PAK entries...", "Matching SAV files and rebuilding archive...");
            PakBatchImportResult importResult = await Task.Run(() => BuildBatchImportResult(importFolder.Path));

            if (importResult.ReplacedEntries.Count > 0 && importResult.WorkingContainer != null)
            {
                _pakContainer = importResult.WorkingContainer;
            }

            RefreshEntryList();
            SetSelectionInfoAfterImport(importResult);
            SetBatchImportStatus(importResult);
            await ShowBatchImportReportAsync(importResult);
        }
        catch (Exception ex)
        {
            SetError("Batch import failed.");
            await ShowMessageAsync("Batch Import Failed", ex.Message);
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e) => await PromptOpenAsync();
    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await PromptSaveAsync();
    private async void BatchExportButton_Click(object sender, RoutedEventArgs e) => await PromptBatchExportAsync();
    private async void BatchImportButton_Click(object sender, RoutedEventArgs e) => await PromptBatchImportAsync();

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        PakEntry? entry = GetSelectedEntry();
        if (entry == null)
        {
            return;
        }

        FileSavePicker picker = PickerInterop.CreateSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = BuildPakExportFileName(entry);
        picker.FileTypeChoices.Add("Save file", new[] { ".sav" });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            await FileIO.WriteBytesAsync(file, entry.Data ?? Array.Empty<byte>());
            SetReady("Extracted: " + file.Path);
            UpdateSelectionInfo(entry);
        }
        catch (Exception ex)
        {
            SetError("Extract failed.");
            await ShowMessageAsync("Extract Entry Failed", ex.Message);
        }
    }

    private async void ReplaceButton_Click(object sender, RoutedEventArgs e)
    {
        PakEntry? entry = GetSelectedEntry();
        if (entry == null || _pakContainer == null)
        {
            return;
        }

        FileOpenPicker picker = PickerInterop.CreateOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".sav");
        picker.FileTypeFilter.Add(".map");
        picker.FileTypeFilter.Add("*");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(file.Path);
            PakArchive.ReplaceEntry(_pakContainer, entry, bytes);
            RefreshEntryList();
            SelectEntry(entry.Index);
            SetReady($"Replaced {entry.FileName} with {Path.GetFileName(file.Path)}");
        }
        catch (Exception ex)
        {
            SetError("Replace failed.");
            await ShowMessageAsync("Replace Entry Failed", ex.Message);
        }
    }

    private void EntriesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionState();
    }

    private async Task OpenPakFileAsync(string fileName)
    {
        try
        {
            SetBusy("Loading PAK...", "Parsing mission entries...");
            _pakContainer = await Task.Run(() => PakArchive.Load(fileName));
            RefreshEntryList();
            SetReady($"Loaded: {Path.GetFileName(fileName)} ({_pakContainer.Entries.Count} entries)");
        }
        catch (Exception ex)
        {
            _pakContainer = null;
            _rows.Clear();
            UpdateSelectionState();
            SetError("PAK load failed.");
            await ShowMessageAsync("PAK Tool", ex.Message);
        }
    }

    private void RefreshEntryList()
    {
        int? selectedIndex = GetSelectedEntry()?.Index;
        _rows.Clear();

        if (_pakContainer != null)
        {
            foreach (PakEntry entry in _pakContainer.Entries.OrderBy(x => x.Index))
            {
                _rows.Add(new PakEntryRow
                {
                    Entry = entry,
                    IdText = entry.Index.ToString(),
                    DisplayName = entry.DisplayName,
                    MissionDisplayName = entry.MissionDisplayName,
                    FileDisplayName = string.IsNullOrWhiteSpace(entry.MapFileName) ? entry.FileName : entry.MapFileName,
                    FileVersionText = entry.FileVersion?.ToString() ?? string.Empty,
                    OffsetText = $"0x{entry.Offset:X}",
                    SizeText = entry.Size.ToString(),
                    StateText = entry.IsModified ? "Unsaved" : "Original"
                });
            }
        }

        if (selectedIndex.HasValue)
        {
            SelectEntry(selectedIndex.Value);
        }

        UpdateSelectionState();
    }

    private PakEntry? GetSelectedEntry()
    {
        return (EntriesListView.SelectedItem as PakEntryRow)?.Entry;
    }

    private void SelectEntry(int index)
    {
        PakEntryRow? row = _rows.FirstOrDefault(x => x.Entry.Index == index);
        if (row != null)
        {
            EntriesListView.SelectedItem = row;
            EntriesListView.ScrollIntoView(row);
        }
    }

    private void UpdateSelectionState()
    {
        bool hasContainer = _pakContainer != null;
        bool hasSelection = GetSelectedEntry() != null;

        SaveButton.IsEnabled = hasContainer;
        ExtractButton.IsEnabled = hasSelection;
        ReplaceButton.IsEnabled = hasSelection;
        BatchExportButton.IsEnabled = hasContainer;
        BatchImportButton.IsEnabled = hasContainer;

        UpdateSelectionInfo(GetSelectedEntry());
    }

    private void UpdateSelectionInfo(PakEntry? entry)
    {
        if (entry == null)
        {
            SelectionInfoTextBlock.Text = _pakContainer == null
                ? "Open a PAK file."
                : $"{_pakContainer.Entries.Count} entries loaded.";
            return;
        }

        SelectionInfoTextBlock.Text =
            $"City: {EmptyDash(entry.CityName)} | Mission: {EmptyDash(entry.MissionDisplayName)} | Mission offset: {(entry.MissionTitleOffset.HasValue ? $"0x{entry.MissionTitleOffset.Value:X}" : "-")} | File: {EmptyDash(string.IsNullOrWhiteSpace(entry.MapFileName) ? entry.FileName : entry.MapFileName)}";
    }

    private async Task<bool> EnsurePakLoadedForBatchOperationAsync()
    {
        if (_pakContainer != null)
        {
            return true;
        }

        await PromptOpenAsync();
        return _pakContainer != null;
    }

    private PakBatchImportResult BuildBatchImportResult(string importFolder)
    {
        if (_pakContainer == null)
        {
            throw new InvalidOperationException("Open a PAK file first.");
        }

        var result = new PakBatchImportResult
        {
            WorkingContainer = PakArchive.Clone(_pakContainer)
        };

        Dictionary<int, string> files = Directory.GetFiles(importFolder, "*.sav", SearchOption.AllDirectories)
            .Select(path => new { Path = path, EntryNumber = TryParsePakExportEntryNumber(path) })
            .Where(x => x.EntryNumber.HasValue)
            .GroupBy(x => x.EntryNumber!.Value)
            .ToDictionary(g => g.Key, g => g.First().Path);

        result.ExpectedEntryCount = result.WorkingContainer.Entries.Count;
        result.ImportedFileCount = files.Count;
        result.CountMismatch = result.ImportedFileCount != result.ExpectedEntryCount;

        foreach (int extraKey in files.Keys.Where(key => key < 1 || key > result.ExpectedEntryCount).OrderBy(key => key))
        {
            result.ExtraEntries.Add($"{extraKey:D3}: extra source file ({files[extraKey]})");
        }

        foreach (PakEntry entry in result.WorkingContainer.Entries.OrderBy(x => x.Index))
        {
            int key = entry.Index + 1;
            if (!files.TryGetValue(key, out string? sourcePath))
            {
                result.MissingEntries.Add($"{key:D3}: missing source file");
                continue;
            }

            try
            {
                PakArchive.ReplaceEntry(result.WorkingContainer, entry, File.ReadAllBytes(sourcePath));
                result.ReplacedEntries.Add($"{key:D3}: {(string.IsNullOrWhiteSpace(entry.MapFileName) ? entry.FileName : entry.MapFileName)} <- {sourcePath}");
            }
            catch (Exception ex)
            {
                result.ErrorEntries.Add($"{key:D3}: {ex.Message} ({sourcePath})");
            }
        }

        return result;
    }

    private void SetSelectionInfoAfterImport(PakBatchImportResult result)
    {
        if (result.ReplacedEntries.Count > 0)
        {
            SelectionInfoTextBlock.Text =
                $"Imported {result.ReplacedEntries.Count} entries into the open PAK. Offsets and sizes were recalculated in memory.";
            return;
        }

        SelectionInfoTextBlock.Text = "No matching SAV files were found for import.";
    }

    private void SetBatchImportStatus(PakBatchImportResult result)
    {
        if (result.ReplacedEntries.Count == 0)
        {
            SetReady("Batch import finished. No PAK entries were changed.");
            return;
        }

        StatusInfoBar.Severity = InfoBarSeverity.Warning;
        StatusInfoBar.Title = "Unsaved PAK";
        StatusInfoBar.Message = result.ErrorEntries.Count > 0
            ? $"Imported {result.ReplacedEntries.Count} entries into the open PAK. Some entries failed; review the report. Save the PAK to persist changes."
            : $"Imported {result.ReplacedEntries.Count} entries into the open PAK. Table data, sizes, offsets and states were updated. Save the PAK to persist changes.";
    }

    private async Task ShowBatchImportReportAsync(PakBatchImportResult result)
    {
        var report = new StringBuilder();
        report.AppendLine("PAK batch import finished.");
        report.AppendLine();
        report.AppendLine($"Expected files: {result.ExpectedEntryCount}");
        report.AppendLine($"Found import files: {result.ImportedFileCount}");
        report.AppendLine($"Replaced: {result.ReplacedEntries.Count}");
        report.AppendLine($"Missing: {result.MissingEntries.Count}");
        report.AppendLine($"Extra: {result.ExtraEntries.Count}");
        report.AppendLine($"Errors: {result.ErrorEntries.Count}");
        report.AppendLine();

        if (result.ReplacedEntries.Count > 0)
        {
            report.AppendLine("The open PAK was updated in memory.");
            report.AppendLine("Entry sizes, offsets, and state flags in the table were recalculated.");
            report.AppendLine("Use Save PAK or File > Save File... when you want to write these changes to disk.");
            report.AppendLine();
        }
        else if (result.ErrorEntries.Count > 0)
        {
            report.AppendLine("No changes were applied to the open PAK because no entry could be imported successfully.");
            report.AppendLine();
        }
        else if (result.MissingEntries.Count > 0 || result.ExtraEntries.Count > 0 || result.CountMismatch)
        {
            report.AppendLine("Missing entries were kept from the original PAK, and extra source files were ignored.");
            report.AppendLine();
        }

        AppendReportSection(report, "Replaced entries:", result.ReplacedEntries);
        AppendReportSection(report, "Skipped because the source file was missing:", result.MissingEntries);
        AppendReportSection(report, "Skipped because the source file does not map to any PAK entry:", result.ExtraEntries);

        if (result.ErrorEntries.Count > 0)
        {
            report.AppendLine("Errors:");
            foreach (string line in result.ErrorEntries)
            {
                report.AppendLine(line);
            }
        }

        var dialog = new ContentDialog
        {
            Title = "Batch Import Report",
            Content = new ScrollViewer
            {
                MaxHeight = 540,
                Content = new TextBox
                {
                    Text = report.ToString(),
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new FontFamily("Consolas")
                }
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static void AppendReportSection(StringBuilder report, string title, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        report.AppendLine(title);
        foreach (string line in lines)
        {
            report.AppendLine(line);
        }

        report.AppendLine();
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
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
    }

    private void SetReady(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Informational;
        StatusInfoBar.Title = "Ready";
        StatusInfoBar.Message = message;
    }

    private void SetError(string message)
    {
        StatusInfoBar.Severity = InfoBarSeverity.Error;
        StatusInfoBar.Title = "Error";
        StatusInfoBar.Message = message;
    }

    private static string EmptyDash(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private static string BuildPakExportFileName(PakEntry entry)
    {
        string stableName = $"{entry.Index + 1:D3}";
        string safeLabel = BinaryHelpers.SanitizeFolderName(entry.DisplayName);
        return string.IsNullOrWhiteSpace(safeLabel)
            ? stableName + ".sav"
            : $"{safeLabel}_{stableName}.sav";
    }

    private static int? TryParsePakExportEntryNumber(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        if (fileName.Length == 0)
        {
            return null;
        }

        string trailingDigits = new string(fileName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        if (trailingDigits.Length > 0 && int.TryParse(trailingDigits, out int trailing))
        {
            return trailing;
        }

        string leadingDigits = new string(fileName.TakeWhile(char.IsDigit).ToArray());
        if (leadingDigits.Length > 0 && int.TryParse(leadingDigits, out int leading))
        {
            return leading;
        }

        return null;
    }
}

