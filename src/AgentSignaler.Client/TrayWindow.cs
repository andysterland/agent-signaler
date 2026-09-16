using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentSignaler.Remote;

namespace AgentSignaler.Client;

internal sealed class TrayWindow : IDisposable
{
    private const uint TrayMessage = 0x8001, CloseMessage = 0x8002;
    private readonly WindowProcedure _procedure;
    private readonly Action _open;
    private readonly Func<ClientIpcResponse> _status;
    private readonly Action _resume;
    private readonly uint _taskbarCreated;
    private readonly string _className = "AgentSignaler.Client." + Guid.NewGuid().ToString("N");
    private readonly nint _instance;
    private nint _window;
    private NotifyIconData _icon;
    private string? _exitWarning;
    private bool _disposed;
    private bool _closing;
    public event Action<bool>? ExitRequested;

    public TrayWindow(Action open, Func<ClientIpcResponse> status, Action resume)
    {
        _open = open;
        _status = status;
        _resume = resume;
        _procedure = Procedure;
        _instance = GetModuleHandle(null);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _instance,
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), ClassName = _className
        };
        if (_taskbarCreated == 0 || RegisterClassEx(ref definition) == 0)
            throw new Win32Exception("The Client window could not be registered.");
        try
        {
            // An invisible top-level window receives Explorer's broadcast; HWND_MESSAGE would not.
            _window = CreateWindowEx(0, _className, "Agent Signaler Client", 0, 0, 0, 0, 0, 0, 0, _instance, 0);
            if (_window == 0) throw new Win32Exception("The Client message window could not be created.");
            var icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "AgentSignaler.ico"), 1, 0, 0, 0x0010 | 0x0040);
            if (icon == 0) throw new Win32Exception("The Client icon is missing. Repair the Remote installation.");
            _icon = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1, Flags = 1 | 2 | 4,
                CallbackMessage = TrayMessage, Icon = icon, Tip = "Agent Signaler — starting",
                Info = "", InfoTitle = ""
            };
            if (!ShellNotifyIcon(0, ref _icon)) throw new Win32Exception("The Client notification-area icon could not be created.");
            if (SetTimer(_window, 1, 1000, 0) == 0) throw new Win32Exception("The Client status timer could not be created.");
        }
        catch { Dispose(); throw; }
    }

    public void Run()
    {
        int result;
        while ((result = GetMessage(out var message, 0, 0, 0)) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        if (result < 0) throw new Win32Exception("The Client message loop failed.");
    }

    public void Close(string? warning)
    {
        if (_disposed) return;
        _exitWarning = warning;
        PostMessage(_window, CloseMessage, 0, 0);
    }

    private nint Procedure(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == TrayMessage)
        {
            if ((uint)lParam is 0x0202 or 0x0203) _open();
            if ((uint)lParam is 0x0205 or 0x007B) ShowMenu();
            return 0;
        }
        if (message == _taskbarCreated && !_disposed)
        {
            if (!ShellNotifyIcon(0, ref _icon))
            {
                // Stop first: never leave an invisible and uncontrollable reporter alive.
                ExitRequested?.Invoke(true);
                ShowError("The notification-area icon could not be restored after Explorer restarted. " +
                    "Client is stopping. Start Agent Signaler Client again from the Start menu.");
            }
            return 0;
        }
        switch (message)
        {
            case 0x0113: // WM_TIMER
                if (wParam == 2) { PostQuitMessage(0); return 0; }
                var state = _status().State;
                _icon.Tip = "Agent Signaler — " + state;
                ShellNotifyIcon(1, ref _icon);
                return 0;
            case 0x0010: // WM_CLOSE
                ExitRequested?.Invoke(false);
                return 0;
            case 0x0011: // WM_QUERYENDSESSION
                ExitRequested?.Invoke(true);
                return 1;
            case 0x0016: // WM_ENDSESSION
                if (wParam != 0) ExitRequested?.Invoke(true);
                return 0;
            case 0x0218: // WM_POWERBROADCAST
                if (wParam is 7 or 18) _resume();
                return 1;
            case CloseMessage:
                if (_closing) return 0;
                _closing = true;
                if (_exitWarning is not null)
                {
                    // A bounded balloon notification, not a modal dialog that could prevent Exit.
                    _icon.Flags |= 0x10;
                    _icon.InfoTitle = "Client stopped";
                    _icon.Info = _exitWarning;
                    _icon.InfoFlags = 2;
                    ShellNotifyIcon(1, ref _icon);
                    if (SetTimer(_window, 2, 500, 0) != 0) return 0;
                }
                PostQuitMessage(0);
                return 0;
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) { ShowError("The Client menu could not be opened. Use Configurator to stop the client."); return; }
        try
        {
            AppendMenu(menu, 0, 1, "Open Configurator");
            AppendMenu(menu, 0, 2, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(_window);
            var command = TrackPopupMenu(menu, 0x0100 | 0x0002, point.X, point.Y, 0, _window, 0);
            if (command == 1) _open();
            else if (command == 2) ExitRequested?.Invoke(false);
            PostMessage(_window, 0, 0, 0);
        }
        finally { DestroyMenu(menu); }
    }

    public static void ShowError(string text) => MessageBox(0, text, "Agent Signaler Client", 0x00040010);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_icon.Icon != 0) { ShellNotifyIcon(2, ref _icon); DestroyIcon(_icon.Icon); }
        if (_window != 0) { KillTimer(_window, 1); KillTimer(_window, 2); DestroyWindow(_window); }
        UnregisterClass(_className, _instance);
        GC.KeepAlive(_procedure);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id, Flags, CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIcon;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string name, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out Message message, nint window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(nint window, nuint id);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint command, ref NotifyIconData icon);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)] private static extern int MessageBox(nint window, string text, string caption, uint type);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
}
