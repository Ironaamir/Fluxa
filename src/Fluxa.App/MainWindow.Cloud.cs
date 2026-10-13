using Fluxa.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluxa.App;

public sealed partial class MainWindow
{
    private readonly HttpClient _cloudHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly WindowsCloudTokenStore _cloudTokens = new();
    private readonly Dictionary<FileSourceKind, ICloudFileSource> _cloudProviders = [];
    private CloudImportSession? _imports;
    private CancellationTokenSource? _cloudWork;
    private bool _cloudDialog;

    private ICloudFileSource Provider(FileSourceKind kind)
    {
        var client = kind == FileSourceKind.GoogleDrive ? _settings.GoogleDriveClientId : _settings.DropboxAppKey;
        if (_cloudProviders.TryGetValue(kind, out var existing)) return existing;
        var config = new CloudAppConfig(kind, client, kind == FileSourceKind.GoogleDrive ? "http://127.0.0.1:53681/fluxa/oauth/" : CloudOAuth.DropboxRedirect);
        return _cloudProviders[kind] = new CloudFileSource(config, _cloudTokens, _cloudHttp);
    }

    private async Task ShowCloudAsync(FileSourceKind kind)
    {
        if (_cloudDialog || _settingsDialog || _conversion != null) return;
        if (string.IsNullOrWhiteSpace(kind == FileSourceKind.GoogleDrive ? _settings.GoogleDriveClientId : _settings.DropboxAppKey))
        {
            Notify("Cloud app setup required", "Set your " + CloudSources.Name(kind) + " OAuth app identifier in Settings → Cloud connections, then connect your account.", InfoBarSeverity.Warning);
            await ShowSettingsAsync();
            return;
        }
        _cloudDialog = true;
        using var work = new CancellationTokenSource();
        _cloudWork = work;
        try
        {
            var provider = Provider(kind);
            var panel = new StackPanel { Spacing = 12 };
            var status = Text(provider.IsConnected ? "Connected · read-only access" : "Not connected. Sign in using your browser.", 13, "Muted");
            var location = Text("Root", 14);
            var files = new ListView { SelectionMode = ListViewSelectionMode.Multiple, IsItemClickEnabled = false, Height = 230 };
            Name(files, "Cloud files");
            var toolbar = new StackPanel { Spacing = 8 };
            var folders = new Stack<(string? Id, string Label)>();
            string? parent = null, continuation = null;
            var currentLabel = "Root";
            var busy = false;
            ContentDialog? dialog = null;
            Button connect = null!, disconnect = null!, up = null!, open = null!, more = null!, reload = null!;
            async Task Run(Func<Task> action)
            {
                if (busy) return;
                busy = true;
                foreach (var button in new[] { connect, disconnect, up, open, more, reload }) button.IsEnabled = false;
                if (dialog != null) dialog.IsPrimaryButtonEnabled = false;
                try { await action(); }
                catch (OperationCanceledException) { status.Text = "Cloud operation cancelled."; }
                catch (Exception ex) { status.Text = ex.Message; }
                finally
                {
                    busy = false;
                    connect.IsEnabled = true; disconnect.IsEnabled = provider.IsConnected;
                    up.IsEnabled = folders.Count > 0 && provider.IsConnected;
                    open.IsEnabled = provider.IsConnected; reload.IsEnabled = provider.IsConnected;
                    more.IsEnabled = continuation != null && provider.IsConnected;
                    if (dialog != null) dialog.IsPrimaryButtonEnabled = provider.IsConnected;
                }
            }
            async Task Load(bool append)
            {
                status.Text = "Loading files…";
                var page = await provider.ListFilesAsync(parent, append ? continuation : null, work.Token);
                if (!append) files.Items.Clear();
                foreach (var file in page.Files)
                    if (file.IsFolder || (file.CanDownload && file.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.Ordinal) != true && ImageConversionEngine.IsSupportedInput(file.Name) && file.Size is not > ImageConversionEngine.MaxInputBytes))
                        files.Items.Add(file);
                continuation = page.Continuation;
                location.Text = currentLabel;
                status.Text = $"{files.Items.Count} folders/images shown · " + (continuation != null ? "Load more for the next page." : "Select images to import; select a folder and Open folder to browse.");
            }
            connect = ActionButton("Connect", () => Run(async () => { status.Text = "Complete sign-in in your browser. Close this dialog to cancel."; await provider.ConnectAsync(work.Token); parent = null; currentLabel = "Root"; folders.Clear(); await Load(false); }));
            disconnect = ActionButton("Disconnect", () => Run(async () => { await provider.DisconnectAsync(work.Token); files.Items.Clear(); folders.Clear(); continuation = null; status.Text = "Disconnected. Local saved credentials removed."; }));
            up = ActionButton("Up", () => Run(async () => { if (folders.Count > 0) { var previous = folders.Pop(); parent = previous.Id; currentLabel = previous.Label; await Load(false); } }));
            open = ActionButton("Open folder", () => Run(async () =>
            {
                if (files.SelectedItems.Count != 1 || files.SelectedItem is not CloudFile { IsFolder: true } folder)
                { status.Text = "Select exactly one folder to open."; return; }
                folders.Push((parent, currentLabel)); parent = folder.Id; currentLabel = folder.Name; await Load(false);
            }));
            reload = ActionButton("Refresh", () => Run(() => Load(false)));
            more = ActionButton("Load more", () => Run(() => Load(true)));
            foreach (var buttons in new[] { new[] { connect, disconnect, reload }, new[] { up, open, more } })
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                foreach (var button in buttons) row.Children.Add(button);
                toolbar.Children.Add(row);
            }
            var stop = ActionButton("Cancel cloud operation", () => { work.Cancel(); status.Text = "Cancelled. Close and reopen the import browser to try again."; return Task.CompletedTask; });
            Name(stop, "Cancel cloud operation");
            panel.Children.Add(status); panel.Children.Add(toolbar); panel.Children.Add(stop); panel.Children.Add(location); panel.Children.Add(files);
            panel.Children.Add(Text("Only supported raster images are shown. Imports are downloaded locally, then queued without conversion. Limits: 512 MB per image, 1 GB downloads per app session, 500 pending files.", 12, "Muted"));
            dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme, Title = CloudSources.Name(kind) + " import", Content = panel,
                PrimaryButtonText = "Import selected", CloseButtonText = "Close", IsPrimaryButtonEnabled = provider.IsConnected };
            dialog.Opened += async (_, _) =>
            {
                if (provider.IsConnected) await Run(() => Load(false));
                else { disconnect.IsEnabled = up.IsEnabled = open.IsEnabled = reload.IsEnabled = more.IsEnabled = false; }
            };
            dialog.Closed += (_, _) => work.Cancel();
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                var selected = files.SelectedItems.OfType<CloudFile>().Where(f => !f.IsFolder).ToArray();
                if (selected.Length == 0) { status.Text = "Select at least one image."; return; }
                if (selected.Length > FileQueue<QueueItem>.PendingLimit - _files.PendingCount) { status.Text = "Not enough room in the pending queue. Import fewer images."; return; }
                var deferral = args.GetDeferral();
                try
                {
                    await Run(async () =>
                    {
                        _imports ??= new CloudImportSession();
                        var imported = 0;
                        foreach (var file in selected)
                        {
                            status.Text = $"Downloading {++imported}/{selected.Length} · {file.Name}";
                            var local = await _imports.DownloadAsync(provider, file, work.Token);
                            AddFiles([local]);
                        }
                        status.Text = $"Imported {imported} images into the queue. Close this dialog to convert them.";
                        files.SelectedItems.Clear();
                    });
                }
                finally { deferral.Complete(); }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { Notify("Cloud import unavailable", ex.Message, InfoBarSeverity.Error); }
        finally { work.Cancel(); _cloudWork = null; _cloudDialog = false; }
    }

    private void CleanupCloud()
    {
        _cloudWork?.Cancel();
        _cloudHttp.Dispose();
        if (_imports == null) return;
        try { _imports.Dispose(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Debug.WriteLine("Fluxa temporary import cleanup failed: " + ex.GetType().Name); }
    }
}
