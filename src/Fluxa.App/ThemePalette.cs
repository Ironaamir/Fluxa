namespace Fluxa.App;

internal sealed record ThemePalette(string Name, string Ink, string Deep, string Pressed, string Hover, string Accent)
{
    public static IReadOnlyList<ThemePalette> All { get; } =
    [
        new("Red", "#270B0B", "#5A1414", "#8B2C2C", "#9D3131", "#B34040"),
        new("Orange", "#27190B", "#5A3714", "#8B5B2C", "#9D6731", "#B37A40"),
        new("Yellow", "#27250B", "#5A5414", "#8B832C", "#9D9431", "#B3A940"),
        new("Green", "#11270B", "#3C5A14", "#598B2C", "#669D31", "#71B340"),
        new("Blue", "#0B1427", "#142B5A", "#2C4C8B", "#31559D", "#4066B3"),
        new("Purple", "#1E0B27", "#43145A", "#6B2C8B", "#79319D", "#8D40B3")
    ];

    public static ThemePalette Find(string? name) => All.FirstOrDefault(p => p.Name == name) ?? All[4];

    public IReadOnlyDictionary<string, string> Colors(bool light)
    {
        var background = light ? "#F0F2EF" : "#1C1D1C";
        return new Dictionary<string, string>
        {
            ["Background"] = background,
            ["Surface"] = light ? "#E3E7E4" : "#292B2A",
            ["Text"] = light ? "#1C211D" : "#F3F5F1",
            ["Muted"] = light ? "#505B53" : "#B6BCB4",
            ["Border"] = light ? "#C5CEC7" : "#424842",
            ["NavHover"] = light ? "#D1D7D3" : "#3B3C3C",
            ["NavPressed"] = light ? "#C4CDC7" : "#454747",
            ["Selected"] = light ? Mix(Accent, "#FFFFFF", .82) : Deep,
            ["SelectedText"] = light ? Ink : "#F3F5F1",
            ["Accent"] = Accent,
            ["AccentHover"] = Hover,
            ["AccentPressed"] = Pressed,
            ["AccentInk"] = Contrast(Accent, "#FFFFFF") >= 4.5 ? "#FFFFFF" : "#101810",
            ["AccentText"] = Readable(Accent, background, light),
            ["Warning"] = light ? "#8C3C0C" : "#F1B985"
        };
    }

    private static string Readable(string accent, string background, bool light)
    {
        // Keep the supplied hue; only text accents need brighter/darker variants for contrast.
        for (var step = 0; step <= 20; step++)
        {
            var candidate = Mix(accent, light ? "#000000" : "#FFFFFF", step / 20d);
            if (Contrast(candidate, background) >= 5.5) return candidate;
        }
        return light ? "#000000" : "#FFFFFF";
    }

    private static string Mix(string first, string second, double amount)
    {
        var a = Rgb(first);
        var b = Rgb(second);
        return $"#{(int)Math.Round(a[0] + (b[0] - a[0]) * amount):X2}{(int)Math.Round(a[1] + (b[1] - a[1]) * amount):X2}{(int)Math.Round(a[2] + (b[2] - a[2]) * amount):X2}";
    }

    internal static double Contrast(string first, string second)
    {
        static double Luminance(string color) => Rgb(color).Select(v => v / 255d)
            .Select(v => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4))
            .Zip(new[] { .2126, .7152, .0722 }, (v, weight) => v * weight).Sum();
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static int[] Rgb(string hex) => [Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16)];
}
