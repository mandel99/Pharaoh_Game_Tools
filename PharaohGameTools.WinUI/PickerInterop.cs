using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PharaohGameTools.WinUI;

internal static class PickerInterop
{
    public static void Initialize(object picker)
    {
        Window? window = App.MainAppWindow;
        if (window == null)
        {
            return;
        }

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));
    }

    public static FileOpenPicker CreateOpenPicker()
    {
        var picker = new FileOpenPicker();
        Initialize(picker);
        return picker;
    }

    public static FileSavePicker CreateSavePicker()
    {
        var picker = new FileSavePicker();
        Initialize(picker);
        return picker;
    }

    public static FolderPicker CreateFolderPicker(PickerLocationId startLocation = PickerLocationId.DocumentsLibrary)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = startLocation
        };
        picker.FileTypeFilter.Add("*");
        Initialize(picker);
        return picker;
    }
}

