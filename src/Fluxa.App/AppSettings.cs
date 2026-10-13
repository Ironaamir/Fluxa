using System.Text.Json;
using Fluxa.Core;

namespace Fluxa.App;

internal sealed class AppSettings
{
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Converted");
    public string OutputExtension { get; set; } = "png";
    public int Quality { get; set; } = 90;
    public bool StripMetadata { get; set; } = true;
    public FramePolicy Frames { get; set; } = FramePolicy.Preserve;
    public bool CloseToTray { get; set; }
    public bool ContextMenuEnabled { get; set; } = true;
    public string ContextMenuExtension { get; set; } = "png";
    public string AccentColor { get; set; } = "Blue";
    public bool LightMode { get; set; }
    public bool ConversionExpanded { get; set; } = true;
    public bool CompressionExpanded { get; set; } = true;
    private static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluxa", "settings.json");

    public static AppSettings Load(out string? warning) => LoadFrom(SettingsPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileConverter", "settings.json"), out warning);

    internal static AppSettings LoadFrom(string path, string legacy, out string? warning)
    {
        warning = null;
        try
        {
            // Read-only fallback keeps existing conversion preferences after the rename.
            var source = File.Exists(path) ? path : legacy;
            if (!File.Exists(source)) return new();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(source)) ?? new();
            settings.Quality = Math.Clamp(settings.Quality, 1, 100);
            if (string.IsNullOrWhiteSpace(settings.OutputFolder)) settings.OutputFolder = new AppSettings().OutputFolder;
            if (!Enum.IsDefined(settings.Frames)) settings.Frames = FramePolicy.Preserve;
            settings.AccentColor = ThemePalette.Find(settings.AccentColor).Name;
            if (!ImageConversionEngine.OutputFormats.Any(f => f.Extension == settings.ContextMenuExtension)) settings.ContextMenuExtension = "png";
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warning = "Preferences could not be loaded. Defaults are in use. " + ex.Message;
            return new();
        }
    }

    public void Save() => SaveTo(SettingsPath);

    internal void SaveTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
