using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Fluxa.App;

internal static class Program
{
    private static readonly ConcurrentQueue<LaunchRequest> Requests = new();
    private static Action? _drain;
    internal static bool TryDequeue(out LaunchRequest? request) => Requests.TryDequeue(out request);
    internal static void SetReceiver(Action drain) { _drain = drain; drain(); }

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        var instance = AppInstance.FindOrRegisterForKey("Fluxa.Desktop");
        if (!instance.IsCurrent)
        {
            Exception? error = null;
            using var done = new EventWaitHandle(false, EventResetMode.ManualReset);
            _ = Task.Run(async () =>
            {
                try { await instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()); }
                catch (Exception ex) { error = ex; }
                finally { done.Set(); }
            });
            var result = CoWaitForMultipleObjects(0, 15000, 1, [done.SafeWaitHandle.DangerousGetHandle()], out _);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            if (error != null) throw new InvalidOperationException("Could not route files to the running Fluxa instance.", error);
            return 0;
        }
        Requests.Enqueue(LaunchRequest.Parse(args));
        instance.Activated += (_, activation) =>
        {
            if (activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
            {
                var arguments = SplitCommandLine(launch.Arguments);
                // Unpackaged launch activations include the executable in the command line.
                if (arguments.FirstOrDefault() is { } first && Path.GetFileName(first).Equals("Fluxa.exe", StringComparison.OrdinalIgnoreCase))
                    arguments = arguments.Skip(1).ToArray();
                Requests.Enqueue(LaunchRequest.Parse(arguments));
                _drain?.Invoke();
            }
        };
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        GC.KeepAlive(instance);
        return 0;
    }

    private static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var pointer = CommandLineToArgvW(commandLine, out var count);
        if (pointer == 0) throw new System.ComponentModel.Win32Exception();
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))!).ToArray();
        }
        finally { LocalFree(pointer); }
    }

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, nint[] handles, out uint index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint pointer);
}
