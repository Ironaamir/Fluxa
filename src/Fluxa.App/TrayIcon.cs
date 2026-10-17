using System.ComponentModel;
using System.Runtime.InteropServices;
using Fluxa.Core;

namespace Fluxa.App;

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 42;
    private readonly nint _window;
    private readonly SubclassProc _callback;
    private readonly Action<ConversionMode?> _open;
    private readonly Action _exit;
    private readonly uint _taskbarCreated;
    private NotifyIconData _data;
    private bool _disposed;

    public TrayIcon(nint window, string iconPath, Action<ConversionMode?> open, Action exit)
    {
        _window = window;
        _open = open;
        _exit = exit;
        _callback = WindowProc;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var icon = LoadImage(0, iconPath, 1, 32, 32, 0x10);
        if (icon == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the tray icon.");
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = window, Id = 1,
            Flags = 1 | 2 | 4, Callback = CallbackMessage, Icon = icon,
            Tip = "Fluxa — choose a conversion mode", Info = "", InfoTitle = ""
        };
        if (!SetWindowSubclass(window, _callback, 1, 0))
        {
            DestroyIcon(icon);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register tray callbacks.");
        }
        if (!AddIcon())
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not create the tray icon.");
        }
    }

    private bool AddIcon()
    {
        if (!Shell_NotifyIcon(0, ref _data)) return false;
        _data.Version = 4;
        Shell_NotifyIcon(4, ref _data);
        return true;
    }

    private nint WindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (!_disposed && message == _taskbarCreated) AddIcon();
        if (!_disposed && message == CallbackMessage)
        {
            var notification = (uint)((long)lParam & 0xffff);
            if (notification is 0x400 or 0x401 or 0x7b or 0x202 or 0x205) ShowMenu();
            return 0;
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            AppendMenu(menu, 0, 1, "Images");
            AppendMenu(menu, 0, 2, "Video");
            AppendMenu(menu, 0, 3, "Audio — coming later");
            AppendMenu(menu, 0, 4, "PDF — coming later");
            AppendMenu(menu, 0x800, 0, null);
            AppendMenu(menu, 0, 5, "Open Fluxa");
            AppendMenu(menu, 0, 6, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var selected = TrackPopupMenu(menu, 0x100 | 0x2, point.X, point.Y, 0, _window, 0);
            PostMessage(_window, 0, 0, 0);
            if (selected is >= 1 and <= 4) _open((ConversionMode)(selected - 1));
            else if (selected == 5) _open(null);
            else if (selected == 6) _exit();
        }
        finally { DestroyMenu(menu); }
    }

    public static void Restore(nint window)
    {
        ShowWindow(window, 9);
        SetForegroundWindow(window);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shell_NotifyIcon(2, ref _data);
        RemoveWindowSubclass(_window, _callback, 1);
        if (_data.Icon != 0) DestroyIcon(_data.Icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate nint SubclassProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);
    [DllImport("comctl32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, nuint id, nuint data);
    [DllImport("comctl32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
}
