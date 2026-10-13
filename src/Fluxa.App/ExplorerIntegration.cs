using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Fluxa.Core;
using Microsoft.Win32;

namespace Fluxa.App;

[SupportedOSPlatform("windows")]
internal static class ExplorerIntegration
{
    internal const string ConvertVerb = "Fluxa.Convert";
    internal const string OpenVerb = "Fluxa.Open";

    public static void Apply(bool enabled, string extension)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        ApplyTo(classes, ImageConversionEngine.InputExtensions, Environment.ProcessPath!, enabled, extension);
        SHChangeNotify(0x08000000, 0, 0, 0); // SHCNE_ASSOCCHANGED: ask Explorer to refresh associations.
    }

    internal static void ApplyTo(RegistryKey classes, IEnumerable<string> inputs, string executable, bool enabled, string extension)
    {
        if (!ImageConversionEngine.OutputFormats.Any(f => f.Extension == extension))
            throw new ArgumentException("Unsupported context-menu output format.", nameof(extension));
        foreach (var input in inputs)
        {
            var shell = @"SystemFileAssociations\" + input + @"\shell";
            if (!enabled)
            {
                classes.DeleteSubKeyTree(shell + "\\" + ConvertVerb, throwOnMissingSubKey: false);
                classes.DeleteSubKeyTree(shell + "\\" + OpenVerb, throwOnMissingSubKey: false);
                continue;
            }
            Register(classes, shell, ConvertVerb, "Convert to ." + extension + " with Fluxa", executable, "--convert");
            Register(classes, shell, OpenVerb, "Open in Fluxa", executable, "--open");
        }
    }

    private static void Register(RegistryKey classes, string shell, string verb, string label, string executable, string action)
    {
        using var key = classes.CreateSubKey(shell + "\\" + verb);
        key.SetValue("", label);
        key.SetValue("Icon", "\"" + executable + "\",0");
        // Document verbs are launched once per selected file; single-instance routing
        // collects each activation. Explorer limits classic Document verbs to 15 files.
        key.SetValue("MultiSelectModel", "Document");
        using var command = key.CreateSubKey("command");
        command.SetValue("", "\"" + executable + "\" " + action + " \"%1\"");
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
