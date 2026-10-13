using Microsoft.UI.Xaml;

namespace Fluxa.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fluxa");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTimeOffset.Now:O}\n{args.Exception}\n");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
        Program.SetReceiver(() => _window.DispatcherQueue.TryEnqueue(() =>
        {
            while (Program.TryDequeue(out var request)) _window.ReceiveLaunch(request!);
        }));
    }
}
