using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PharaohGameTools.Core;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PharaohGameTools.WinUI;

public sealed partial class TextToolView : UserControl
{
    private const string TextSourceFormatTxt = "txt";
    private const string TextSourceFormatEng = "eng";
    private readonly Button _openButton;
    private readonly Button _saveTxtButton;
    private readonly Button _saveEngButton;
    private readonly ComboBox _encodingComboBox;
    private readonly InfoBar _statusInfoBar;
    private readonly TextBox _editorTextBox;

    private string? _sourcePath;
    private string _sourceFormat = TextSourceFormatTxt;
    private string _resolvedEncodingName = "windows-1252";
    private string? _loadedEngEncodingName;
    private string _loadedSourceText = string.Empty;
    private string _originalText = string.Empty;
    private byte[]? _originalBytes;
    private bool _sourceWasEng;
    private bool _suppressEncodingReload = false;
    private bool _suppressDirtyTracking;

    public TextToolView()
    {
        InitializeComponent();
        _openButton = (Button)FindName(nameof(OpenButton));
        _saveTxtButton = (Button)FindName(nameof(SaveTxtButton));
        _saveEngButton = (Button)FindName(nameof(SaveEngButton));
        _encodingComboBox = (ComboBox)FindName(nameof(EncodingComboBox));
        _statusInfoBar = (InfoBar)FindName(nameof(StatusInfoBar));
        _editorTextBox = (TextBox)FindName(nameof(EditorTextBox));
    }

    public async Task PromptOpenAsync()
    {
        FileOpenPicker picker = PickerInterop.CreateOpenPicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".eng");

        StorageFile? file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            await OpenTextToolFileAsync(file.Path);
        }
    }

    public async Task PromptSaveTxtAsync()
    {
        if (_editorTextBox.IsEnabled)
        {
            string currentText = _editorTextBox.Text ?? string.Empty;
            string selectedEncodingKey = GetSelectedTextEncodingKey();
            string resolvedEncodingName = GetResolvedTextEncodingName(selectedEncodingKey);
            await SaveTextToolOutputAsync(
                ".txt",
                "Save TXT",
                TextSourceFormatTxt,
                () => EncodeTextToolTxt(currentText, selectedEncodingKey),
                fileName => $"Saved TXT: {fileName} ({resolvedEncodingName})",
                () => _resolvedEncodingName = resolvedEncodingName);
        }
    }

    public async Task PromptSaveEngAsync()
    {
        if (_editorTextBox.IsEnabled)
        {
            string currentText = _editorTextBox.Text ?? string.Empty;
            string selectedEncoding = GetSelectedEngEncodingName(GetSelectedTextEncodingKey());
            string sourceText = GetEffectiveEngSourceText(currentText);
            await SaveTextToolOutputAsync(
                ".eng",
                "Save ENG",
                TextSourceFormatEng,
                () => GetTextToolEngOutputBytes(sourceText, selectedEncoding),
                fileName => $"Saved ENG: {fileName} ({selectedEncoding})",
                () =>
                {
                    _loadedEngEncodingName = selectedEncoding;
                    _loadedSourceText = sourceText;
                },
                sourceText);
        }
    }

    public async Task PromptSaveUsingSourceFormatAsync()
    {
        if (!_editorTextBox.IsEnabled)
        {
            return;
        }

        if (string.Equals(_sourceFormat, TextSourceFormatEng, StringComparison.OrdinalIgnoreCase))
        {
            await PromptSaveEngAsync();
        }
        else
        {
            await PromptSaveTxtAsync();
        }
    }

    public bool HasUnsavedDocumentChanges()
    {
        return HasUnsavedChanges();
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptOpenAsync();
    }

    private async void SaveTxtButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptSaveTxtAsync();
    }

    private async void SaveEngButton_Click(object sender, RoutedEventArgs e)
    {
        await PromptSaveEngAsync();
    }

    private async void EncodingComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEncodingReload || !CanReloadTextToolSource())
        {
            return;
        }

        await OpenTextToolFileAsync(_sourcePath!);
    }

    private void EditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_editorTextBox.IsEnabled || _suppressDirtyTracking)
        {
            return;
        }

        UpdateDirtyStatus();
    }

    private async Task OpenTextToolFileAsync(string fileName)
    {
        try
        {
            SetBusy(GetTextToolLoadMessage(fileName), "Loading text resource...");
            string selectedEncodingKey = GetSelectedTextEncodingKey();
            string selectedEncoding = GetSelectedEngEncodingName(selectedEncodingKey);

            var loadResult = await Task.Run(() =>
            {
                byte[] bytes = File.ReadAllBytes(fileName);
                string sourceFormat = DetermineTextSourceFormat(fileName, bytes);
                string editorText = string.Equals(sourceFormat, TextSourceFormatEng, StringComparison.OrdinalIgnoreCase)
                    ? TextEngConverter.ConvertEngToTxt(bytes, selectedEncoding)
                    : DecodeTextToolTxt(bytes, selectedEncodingKey);
                return Tuple.Create(editorText, bytes, sourceFormat);
            });

            _sourcePath = fileName;
            _sourceFormat = loadResult.Item3;
            _sourceWasEng = string.Equals(_sourceFormat, TextSourceFormatEng, StringComparison.OrdinalIgnoreCase);
            _loadedEngEncodingName = selectedEncoding;
            _loadedSourceText = loadResult.Item1;
            _originalBytes = CloneBytes(loadResult.Item2);

            _suppressDirtyTracking = true;
            try
            {
                _editorTextBox.IsEnabled = false;
                _editorTextBox.Text = loadResult.Item1;
                _originalText = _editorTextBox.Text ?? string.Empty;
                _editorTextBox.IsEnabled = true;
            }
            finally
            {
                _suppressDirtyTracking = false;
            }

            _saveTxtButton.IsEnabled = true;
            _saveEngButton.IsEnabled = true;
            UpdateReadyStatus();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Text Tool", ex.Message);
            SetReady("Text load failed.");
        }
    }

    private async Task SaveTextToolOutputAsync(string extension, string title, string savedFormat, Func<byte[]> outputFactory, Func<string, string> successMessageFactory, Action? onSaved = null, string? validationText = null)
    {
        if (!_editorTextBox.IsEnabled)
        {
            return;
        }

        string currentText = validationText ?? _editorTextBox.Text ?? string.Empty;
        if (string.Equals(savedFormat, TextSourceFormatEng, StringComparison.OrdinalIgnoreCase)
            && !TextEngConverter.TryValidateTxtStructure(currentText, out string? validationError))
        {
            await ShowMessageAsync(
                "Invalid Text Format",
                "The current text is not in the expected Pharaoh text format."
                + Environment.NewLine + Environment.NewLine
                + validationError);
            return;
        }

        FileSavePicker picker = PickerInterop.CreateSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.SuggestedFileName = GetTextToolOutputFileName(extension);
        picker.FileTypeChoices.Add(title, new[] { extension });

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
        {
            return;
        }

        try
        {
            SetBusy($"Saving {file.Name}...", "Writing output file...");
            byte[] outputBytes = await Task.Run(outputFactory);
            await FileIO.WriteBytesAsync(file, outputBytes);
            MarkTextToolSaved(savedFormat, outputBytes);
            onSaved?.Invoke();
            SetReady(successMessageFactory(file.Name));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(title + " Failed", ex.Message);
            SetReady(title + " failed.");
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

    private void UpdateReadyStatus()
    {
        string fileName = string.IsNullOrWhiteSpace(_sourcePath) ? "No file" : Path.GetFileName(_sourcePath);
        string kind = _sourceWasEng ? $"ENG, {GetSelectedEngEncodingName()}" : _resolvedEncodingName;
        SetReady($"Loaded: {fileName} ({kind})");
    }

    private void UpdateDirtyStatus()
    {
        if (HasUnsavedChanges())
        {
            _statusInfoBar.Severity = InfoBarSeverity.Warning;
            _statusInfoBar.Title = "Unsaved changes";
            _statusInfoBar.Message = "The editor content differs from the loaded source.";
        }
        else
        {
            UpdateReadyStatus();
        }
    }

    private bool HasUnsavedChanges()
    {
        return _editorTextBox.IsEnabled
            && !string.Equals(_editorTextBox.Text ?? string.Empty, _originalText ?? string.Empty, StringComparison.Ordinal);
    }

    private string DecodeTextToolTxt(byte[] bytes, string encodingKey)
    {
        Encoding encoding = ResolveSelectedTextEncoding(bytes, encodingKey, out string encodingName);
        _resolvedEncodingName = encodingName;
        bytes ??= Array.Empty<byte>();
        byte[] preamble = encoding.GetPreamble();
        int offset = 0;
        if (preamble.Length > 0 && bytes.Length >= preamble.Length)
        {
            bool hasPreamble = true;
            for (int i = 0; i < preamble.Length; i++)
            {
                if (bytes[i] != preamble[i])
                {
                    hasPreamble = false;
                    break;
                }
            }

            if (hasPreamble)
            {
                offset = preamble.Length;
            }
        }

        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }

    private byte[] EncodeTextToolTxt(string text, string key)
    {
        Encoding encoding;
        switch (key)
        {
            case "Windows-1250":
                encoding = Encoding.GetEncoding(1250);
                break;
            case "Windows-1251":
                encoding = Encoding.GetEncoding(1251);
                break;
            case "Windows-1253":
                encoding = Encoding.GetEncoding(1253);
                break;
            case "CP949":
                encoding = Encoding.GetEncoding(949);
                break;
            case "Shift_JIS":
                encoding = Encoding.GetEncoding(932);
                break;
            case "c3-tc":
                encoding = Encoding.GetEncoding(950);
                break;
            case "c3-sc":
                encoding = Encoding.GetEncoding(936);
                break;
            case "Windows-1252":
            case "auto":
            default:
                encoding = Encoding.GetEncoding(1252);
                break;
        }

        byte[] preamble = encoding.GetPreamble();
        byte[] payload = encoding.GetBytes(text ?? string.Empty);
        if (preamble.Length == 0)
        {
            return payload;
        }

        byte[] output = new byte[preamble.Length + payload.Length];
        Buffer.BlockCopy(preamble, 0, output, 0, preamble.Length);
        Buffer.BlockCopy(payload, 0, output, preamble.Length, payload.Length);
        return output;
    }

    private static string GetResolvedTextEncodingName(string key)
    {
        switch (key)
        {
            case "Windows-1250":
                return "windows-1250";
            case "Windows-1251":
                return "windows-1251";
            case "Windows-1253":
                return "windows-1253";
            case "CP949":
                return "cp949";
            case "Shift_JIS":
                return "shift_jis";
            case "c3-tc":
                return "c3-tc";
            case "c3-sc":
                return "c3-sc";
            case "Windows-1252":
            case "auto":
            default:
                return "windows-1252";
        }
    }

    private Encoding ResolveSelectedTextEncoding(byte[] bytes, string encodingKey, out string encodingName)
    {
        switch (encodingKey)
        {
            case "Windows-1250":
                encodingName = "windows-1250";
                return Encoding.GetEncoding(1250);
            case "Windows-1251":
                encodingName = "windows-1251";
                return Encoding.GetEncoding(1251);
            case "Windows-1253":
                encodingName = "windows-1253";
                return Encoding.GetEncoding(1253);
            case "CP949":
                encodingName = "cp949";
                return Encoding.GetEncoding(949);
            case "Shift_JIS":
                encodingName = "shift_jis";
                return Encoding.GetEncoding(932);
            case "c3-tc":
                encodingName = "c3-tc";
                return Encoding.GetEncoding(950);
            case "c3-sc":
                encodingName = "c3-sc";
                return Encoding.GetEncoding(936);
            case "Windows-1252":
                encodingName = "windows-1252";
                return Encoding.GetEncoding(1252);
            case "auto":
            default:
                encodingName = "windows-1252";
                return Encoding.GetEncoding(1252);
        }
    }

    private string GetSelectedTextEncodingKey()
    {
        string selected = _encodingComboBox.SelectedItem as string ?? string.Empty;
        switch (selected)
        {
            case "Windows-1252 - Default":
                return "Windows-1252";
            case "Windows-1250 - Eastern European":
                return "Windows-1250";
            case "Windows-1251 - Cyrillic":
                return "Windows-1251";
            case "Windows-1253 - Greek":
                return "Windows-1253";
            case "Windows-949 - Korean":
                return "CP949";
            case "Windows-932 - Japanese":
                return "Shift_JIS";
            case "Traditional Chinese (C3)":
                return "c3-tc";
            case "Simplified Chinese (C3)":
                return "c3-sc";
            case "Auto (Windows-1252)":
            default:
                return "auto";
        }
    }

    private string GetSelectedEngEncodingName()
    {
        return GetSelectedEngEncodingName(GetSelectedTextEncodingKey());
    }

    private static string GetSelectedEngEncodingName(string key)
    {
        return key == "auto" ? "Windows-1252" : key;
    }

    private byte[] GetTextToolEngOutputBytes(string currentText, string selectedEncoding)
    {
        if (_sourceWasEng
            && _originalBytes != null
            && string.Equals(currentText, _originalText ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(selectedEncoding, _loadedEngEncodingName, StringComparison.OrdinalIgnoreCase))
        {
            return CloneBytes(_originalBytes)!;
        }

        return TextEngConverter.ConvertTxtToEng(currentText, selectedEncoding);
    }

    private string GetEffectiveEngSourceText(string currentText)
    {
        if (!_sourceWasEng
            && string.Equals(_sourceFormat, TextSourceFormatTxt, StringComparison.OrdinalIgnoreCase)
            && string.Equals(currentText, _originalText ?? string.Empty, StringComparison.Ordinal))
        {
            string? reloadedText = TryReloadCurrentTxtSourceText();
            if (!string.IsNullOrEmpty(reloadedText))
            {
                return reloadedText;
            }

            if (!string.IsNullOrEmpty(_loadedSourceText))
            {
                return _loadedSourceText;
            }
        }

        return currentText;
    }

    private string? TryReloadCurrentTxtSourceText()
    {
        if (string.IsNullOrWhiteSpace(_sourcePath)
            || !File.Exists(_sourcePath)
            || !string.Equals(Path.GetExtension(_sourcePath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        byte[] bytes = File.ReadAllBytes(_sourcePath);
        return DecodeTextToolTxt(bytes, GetSelectedTextEncodingKey());
    }

    private string GetTextToolOutputFileName(string extension)
    {
        string baseName = string.IsNullOrWhiteSpace(_sourcePath)
            ? "converted"
            : Path.GetFileNameWithoutExtension(_sourcePath);
        return baseName + extension;
    }

    private bool CanReloadTextToolSource()
    {
        return !string.IsNullOrWhiteSpace(_sourcePath) && File.Exists(_sourcePath);
    }

    private string GetTextToolLoadMessage(string fileName)
    {
        return string.IsNullOrWhiteSpace(_sourcePath) || !string.Equals(_sourcePath, fileName, StringComparison.OrdinalIgnoreCase)
            ? "Loading text file..."
            : "Applying encoding...";
    }

    private static string DetermineTextSourceFormat(string fileName, byte[] bytes)
    {
        string extension = Path.GetExtension(fileName) ?? string.Empty;
        bool isEng = string.Equals(extension, ".eng", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
                && TextEngConverter.DetermineEngFileType(bytes) != EngFileType.Unknown);
        return isEng ? TextSourceFormatEng : TextSourceFormatTxt;
    }

    private void MarkTextToolSaved(string savedFormat, byte[] outputBytes)
    {
        _originalText = _editorTextBox.Text ?? string.Empty;
        _sourceFormat = savedFormat;
        _sourceWasEng = string.Equals(_sourceFormat, TextSourceFormatEng, StringComparison.OrdinalIgnoreCase);
        _loadedSourceText = _originalText;
        _originalBytes = CloneBytes(outputBytes);
        if (_sourceWasEng)
        {
            _loadedEngEncodingName = GetSelectedEngEncodingName();
        }
    }

    private static byte[]? CloneBytes(byte[]? bytes)
    {
        return bytes != null ? (byte[])bytes.Clone() : null;
    }
}

