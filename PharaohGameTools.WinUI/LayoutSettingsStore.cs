using System;
using System.IO;
using System.Text.Json;

namespace PharaohGameTools.WinUI;

internal sealed class LayoutSettings
{
    public int Version { get; set; }
    public MainWindowLayoutSettings MainWindow { get; set; } = new();
    public SgToolLayoutSettings SgTool { get; set; } = new();
}

internal sealed class MainWindowLayoutSettings
{
    public bool HasSavedBounds { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 900;
    public int Height { get; set; } = 600;
}

internal sealed class SgToolLayoutSettings
{
    public bool HasSavedLayout { get; set; }
    public double LeftPaneWidth { get; set; }
    public double MiddlePaneWidth { get; set; }
    public double RightPaneWidth { get; set; }
    public double PreviewPaneHeight { get; set; }
    public double DetailsPaneHeight { get; set; }
}

internal static class LayoutSettingsStore
{
    private const int CurrentLayoutVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PharaohGameTools");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "layout.json");

    public static LayoutSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new LayoutSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            LayoutSettings settings = JsonSerializer.Deserialize<LayoutSettings>(json, JsonOptions) ?? new LayoutSettings();
            return Migrate(settings);
        }
        catch
        {
            return new LayoutSettings();
        }
    }

    public static void Save(LayoutSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            settings.Version = CurrentLayoutVersion;
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Layout persistence should never crash the app.
        }
    }

    private static LayoutSettings Migrate(LayoutSettings settings)
    {
        if (settings.Version >= CurrentLayoutVersion)
        {
            return settings;
        }

        // The SG tool pane structure changed, so old saved splitter values can
        // produce heavily skewed layouts. Keep the window bounds but reset the
        // SG panel layout to the new defaults.
        settings.SgTool = new SgToolLayoutSettings();
        settings.Version = CurrentLayoutVersion;
        return settings;
    }
}

