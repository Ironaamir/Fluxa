using System.Runtime.Versioning;
using Fluxa.App;
using Microsoft.Win32;
using Xunit;

namespace Fluxa.Tests;

public sealed class ExplorerTests
{
    [Theory]
    [InlineData("--open", false)]
    [InlineData("--convert", true)]
    public void LaunchActionsKeepMultiplePathsIntact(string action, bool convert)
    {
        var paths = new[] { Path.GetFullPath("space & quote's.png"), Path.GetFullPath("second.jpg") };
        var request = LaunchRequest.Parse([action, .. paths]);
        Assert.Equal(convert, request.ConvertImmediately);
        Assert.Equal(paths, request.Files);
        Assert.Empty(LaunchRequest.Parse([]).Files);
    }

    [Fact]
    public void ContextFormatDefaultsToPngAndPersistsSeparatelyFromWorkspaceFormat()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var path = Path.Combine(folder, "settings.json");
        try
        {
            Assert.Equal("png", new AppSettings().ContextMenuExtension);
            new AppSettings { ContextMenuExtension = "jpg", OutputExtension = "webp", ContextMenuEnabled = false }.SaveTo(path);
            var loaded = AppSettings.LoadFrom(path, path, out var warning);
            Assert.Null(warning);
            Assert.Equal("jpg", loaded.ContextMenuExtension);
            Assert.Equal("webp", loaded.OutputExtension);
            Assert.False(loaded.ContextMenuEnabled);
            new AppSettings { ContextMenuExtension = "not-a-format" }.SaveTo(path);
            Assert.Equal("png", AppSettings.LoadFrom(path, path, out _).ContextMenuExtension);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void RegistryVerbsQuotePathsUpdateLabelsAndRemoveOnlyOwnedKeys()
    {
        var path = @"Software\Fluxa.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(path);
            const string exe = @"C:\Folder With Spaces\Fluxa.exe";
            using (var unrelated = root.CreateSubKey(@"SystemFileAssociations\.png\shell\OtherApp")) unrelated.SetValue("", "Leave alone");
            ExplorerIntegration.ApplyTo(root, [".png", ".jpg"], exe, true, "png");
            using (var convert = root.OpenSubKey(@"SystemFileAssociations\.png\shell\Fluxa.Convert"))
            {
                Assert.Equal("Convert to .png with Fluxa", convert!.GetValue(""));
                Assert.Equal("Document", convert.GetValue("MultiSelectModel"));
                using var command = convert.OpenSubKey("command");
                Assert.Equal("\"" + exe + "\" --convert \"%1\"", command!.GetValue(""));
            }
            using (var open = root.OpenSubKey(@"SystemFileAssociations\.jpg\shell\Fluxa.Open\command"))
                Assert.Equal("\"" + exe + "\" --open \"%1\"", open!.GetValue(""));
            ExplorerIntegration.ApplyTo(root, [".png", ".jpg"], exe, true, "jpg");
            using (var convert = root.OpenSubKey(@"SystemFileAssociations\.png\shell\Fluxa.Convert"))
                Assert.Equal("Convert to .jpg with Fluxa", convert!.GetValue(""));
            Assert.Throws<ArgumentException>(() => ExplorerIntegration.ApplyTo(root, [".png"], exe, true, "pdf"));
            ExplorerIntegration.ApplyTo(root, [".png", ".jpg"], exe, false, "jpg");
            Assert.Null(root.OpenSubKey(@"SystemFileAssociations\.png\shell\Fluxa.Convert"));
            Assert.Null(root.OpenSubKey(@"SystemFileAssociations\.jpg\shell\Fluxa.Open"));
            using var kept = root.OpenSubKey(@"SystemFileAssociations\.png\shell\OtherApp");
            Assert.Equal("Leave alone", kept!.GetValue(""));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }
}
