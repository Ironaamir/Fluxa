using Fluxa.App;
using Xunit;

namespace Fluxa.Tests;

public sealed class AppearanceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Fluxa.AppearanceTests", Guid.NewGuid().ToString("N"));
    private string Current => Path.Combine(_folder, "Fluxa", "settings.json");
    private string Legacy => Path.Combine(_folder, "Old", "settings.json");

    [Fact]
    public void DefaultsAreBlueAndDarkWithBothNavigationGroupsExpanded()
    {
        var settings = AppSettings.LoadFrom(Current, Legacy, out var warning);
        Assert.Null(warning);
        Assert.Equal("Blue", settings.AccentColor);
        Assert.False(settings.LightMode);
        Assert.True(settings.ConversionExpanded);
        Assert.True(settings.CompressionExpanded);
        Assert.Equal(new[] { "Red", "Orange", "Yellow", "Green", "Blue", "Purple" }, ThemePalette.All.Select(p => p.Name));
    }

    [Theory]
    [InlineData(false, "#1C1D1C", "#292B2A")]
    [InlineData(true, "#F0F2EF", "#E3E7E4")]
    public void EveryPaletteHasExactSurfacesAndReadableText(bool light, string background, string surface)
    {
        foreach (var palette in ThemePalette.All)
        {
            var colors = palette.Colors(light);
            Assert.Equal(background, colors["Background"]);
            Assert.Equal(surface, colors["Surface"]);
            foreach (var on in new[] { "Background", "Surface" })
            foreach (var text in new[] { "Text", "Muted", "AccentText" })
                Assert.True(ThemePalette.Contrast(colors[text], colors[on]) >= 4.5, $"{palette.Name}/{light}: {text} on {on}");
            Assert.True(ThemePalette.Contrast(colors["AccentInk"], colors["Accent"]) >= 4.5, palette.Name);
            Assert.True(ThemePalette.Contrast(colors["SelectedText"], colors["Selected"]) >= 4.5, palette.Name);
        }
    }

    [Fact]
    public void AppearanceAndGroupPreferencesSurviveReload()
    {
        var settings = new AppSettings { AccentColor = "Purple", LightMode = true, ConversionExpanded = false, CompressionExpanded = false };
        settings.SaveTo(Current);
        var loaded = AppSettings.LoadFrom(Current, Legacy, out var warning);
        Assert.Null(warning);
        Assert.Equal("Purple", loaded.AccentColor);
        Assert.True(loaded.LightMode);
        Assert.False(loaded.ConversionExpanded);
        Assert.False(loaded.CompressionExpanded);
        Assert.False(File.Exists(Current + ".tmp"));
    }

    [Fact]
    public void LegacyPreferencesAreReadWithoutChangingTheLegacyFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Legacy)!);
        const string json = "{\"Quality\":77,\"OutputExtension\":\"webp\"}";
        File.WriteAllText(Legacy, json);
        var settings = AppSettings.LoadFrom(Current, Legacy, out var warning);
        Assert.Null(warning);
        Assert.Equal(77, settings.Quality);
        Assert.Equal("webp", settings.OutputExtension);
        Assert.Equal("Blue", settings.AccentColor);
        settings.SaveTo(Current);
        Assert.Equal(json, File.ReadAllText(Legacy));
        new AppSettings { Quality = 88 }.SaveTo(Current);
        Assert.Equal(88, AppSettings.LoadFrom(Current, Legacy, out _).Quality);
    }

    [Fact]
    public void InvalidPaletteFallsBackToBlueAndCorruptSettingsReportAWarning()
    {
        new AppSettings { AccentColor = "invalid" }.SaveTo(Current);
        Assert.Equal("Blue", AppSettings.LoadFrom(Current, Legacy, out _).AccentColor);
        File.WriteAllText(Current, "not json");
        Assert.Equal("Blue", AppSettings.LoadFrom(Current, Legacy, out var warning).AccentColor);
        Assert.NotNull(warning);
    }

    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }
}
