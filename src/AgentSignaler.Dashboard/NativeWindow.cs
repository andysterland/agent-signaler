using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentSignaler.Dashboard;

internal static class NativeWindow
{
    public static void ShowError(string message) => MessageBox(0, message, "Agent Signaler", 0x00040010);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ScreenToClient(nint window, ref Point point);

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowSubclass(nint window, SubclassProcedure callback, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveWindowSubclass(nint window, SubclassProcedure callback, nuint id);
    [DllImport("comctl32.dll")]
    internal static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
}

internal sealed class WindowStateMonitor : IDisposable
{
    private readonly nint _window;
    private readonly NativeWindow.SubclassProcedure _procedure;
    private readonly Action _changed;
    private bool _minimized;

    public WindowStateMonitor(nint window, Action changed)
    {
        _window = window;
        _changed = changed;
        _procedure = WindowProcedure;
        if (!NativeWindow.SetWindowSubclass(window, _procedure, 2, 0))
            throw new Win32Exception("Window state monitoring could not be initialized.");
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x0005) // WM_SIZE
        {
            var minimized = wParam == 1; // SIZE_MINIMIZED
            if (_minimized != minimized)
            {
                _minimized = minimized;
                _changed();
            }
        }
        return NativeWindow.DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        NativeWindow.RemoveWindowSubclass(_window, _procedure, 2);
        GC.KeepAlive(_procedure);
    }
}

internal sealed class CompactWindowSizing : IDisposable
{
    private readonly nint _window;
    private readonly NativeWindow.SubclassProcedure _procedure;

    public CompactWindowSizing(nint window)
    {
        _window = window;
        _procedure = WindowProcedure;
        if (!NativeWindow.SetWindowSubclass(window, _procedure, 3, 0))
            throw new Win32Exception("Compact window sizing could not be initialized.");
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        var result = NativeWindow.DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0024) // WM_GETMINMAXINFO: override WinUI's default minimum tracking size.
        {
            var limits = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            limits.MinimumTrackSize = new Point { X = 1, Y = 1 };
            Marshal.StructureToPtr(limits, lParam, false);
        }
        return result;
    }

    public void Dispose()
    {
        NativeWindow.RemoveWindowSubclass(_window, _procedure, 3);
        GC.KeepAlive(_procedure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved, MaximumSize, MaximumPosition, MinimumTrackSize, MaximumTrackSize;
    }
}

internal sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private readonly nint _window;
    private readonly NativeWindow.SubclassProcedure _procedure;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly uint _taskbarCreated;
    private NotifyIconData _icon;
    private bool _disposed;
    public bool IsAvailable { get; private set; }

    public TrayIcon(nint window, Action show, Action exit)
    {
        _window = window;
        _show = show;
        _exit = exit;
        _procedure = WindowProcedure;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // LoadImage without LR_SHARED returns an icon owned by this instance.
        var icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "AgentSignaler.ico"),
            1, 0, 0, 0x0010 | 0x0040); // IMAGE_ICON, LR_LOADFROMFILE | LR_DEFAULTSIZE
        if (icon == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The branded notification-area icon could not be loaded.");
        _icon = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = 1,
            Flags = 1 | 2 | 4,
            CallbackMessage = CallbackMessage,
            Icon = icon,
            Tip = "Agent Signaler — Show / Exit",
            Info = "",
            InfoTitle = ""
        };
        if (!NativeWindow.SetWindowSubclass(window, _procedure, 1, 0))
        {
            DestroyIcon(_icon.Icon);
            throw new Win32Exception("The notification-area window integration could not be initialized.");
        }
        if (!ShellNotifyIcon(0, ref _icon))
        {
            NativeWindow.RemoveWindowSubclass(window, _procedure, 1);
            DestroyIcon(_icon.Icon);
            throw new Win32Exception("The notification-area icon could not be created.");
        }
        IsAvailable = true;
    }

    private nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == CallbackMessage)
        {
            if ((uint)lParam is 0x0202 or 0x0203 or 0x0400 or 0x0401) _show();
            else if ((uint)lParam == 0x0205 || (uint)lParam == 0x007B) ShowMenu();
            return 0;
        }
        if (message == _taskbarCreated && !_disposed)
        {
            IsAvailable = ShellNotifyIcon(0, ref _icon);
            if (!IsAvailable)
            {
                _show();
                NativeWindow.ShowError("The notification-area icon could not be restored after Explorer restarted. " +
                    "Restart Agent Signaler before minimizing it.");
            }
        }
        return NativeWindow.DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            _show();
            return;
        }
        try
        {
            AppendMenu(menu, 0, 1, "Show Agent Signaler");
            AppendMenu(menu, 0, 2, "Exit");
            NativeWindow.GetCursorPos(out var point);
            NativeWindow.SetForegroundWindow(_window);
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, _window, 0);
            if (command == 1) _show();
            else if (command == 2) _exit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsAvailable = false;
        ShellNotifyIcon(2, ref _icon);
        NativeWindow.RemoveWindowSubclass(_window, _procedure, 1);
        DestroyIcon(_icon.Icon);
        GC.KeepAlive(_procedure);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(nint menu);
}
