using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentSignaler.Dashboard;

internal static class WindowsAppWindowTitleMatcher
{
    internal const int MaximumTitleLength = 1024;

    public static bool IsMatch(string? title, string devBoxName)
    {
        if (string.IsNullOrEmpty(title) || title.Length > MaximumTitleLength ||
            string.IsNullOrEmpty(devBoxName)) return false;
        for (var start = 0; start <= title.Length - devBoxName.Length; start++)
        {
            var index = title.IndexOf(devBoxName, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return false;
            if (IsBoundary(title, index - 1, -1) &&
                IsBoundary(title, index + devBoxName.Length, 1)) return true;
            start = index;
        }
        return false;
    }

    private static bool IsBoundary(string title, int index, int direction)
    {
        if (index < 0 || index >= title.Length) return true;
        var character = title[index];
        if (char.IsWhiteSpace(character) || character is '\u2013' or '\u2014' or ':' or '(' or ')' or '[' or ']')
            return true;
        // Hyphens also belong to resource names. Require a standalone title separator.
        var outer = index + direction;
        return character == '-' &&
            (outer < 0 || outer >= title.Length || char.IsWhiteSpace(title[outer]));
    }
}

internal interface ITopLevelWindowPlatform
{
    IReadOnlyList<nint> Enumerate();
    string? GetTitle(nint window);
    bool IsWindow(nint window);
    bool IsMinimized(nint window);
    bool Restore(nint window);
    bool BringToForeground(nint window);
}

internal sealed class WindowsAppWindowPlatform : ITopLevelWindowPlatform
{
    public IReadOnlyList<nint> Enumerate()
    {
        var windows = new List<nint>();
        ExceptionDispatchInfo? callbackFailure = null;
        EnumWindowsCallback callback = (window, _) =>
        {
            try { windows.Add(window); return true; }
            catch (Exception error)
            {
                // Never unwind through User32; preserve programming errors on the managed side.
                callbackFailure = ExceptionDispatchInfo.Capture(error);
                return false;
            }
        };
        var succeeded = EnumWindows(callback, 0);
        GC.KeepAlive(callback);
        callbackFailure?.Throw();
        if (!succeeded) throw new Win32Exception();
        return windows;
    }

    public string? GetTitle(nint window)
    {
        var length = GetWindowTextLength(window);
        if (length is <= 0 or > WindowsAppWindowTitleMatcher.MaximumTitleLength) return null;
        // One extra character detects a title growing beyond the limit between calls.
        var title = new StringBuilder(WindowsAppWindowTitleMatcher.MaximumTitleLength + 2);
        var copied = GetWindowText(window, title, title.Capacity);
        return copied is > 0 and <= WindowsAppWindowTitleMatcher.MaximumTitleLength && IsWindow(window)
            ? title.ToString() : null;
    }

    public bool IsWindow(nint window) => NativeIsWindow(window);
    public bool IsMinimized(nint window) => IsIconic(window);
    public bool Restore(nint window) => ShowWindowAsync(window, 9);
    public bool BringToForeground(nint window) => SetForegroundWindow(window);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", ExactSpelling = true)]
    private static extern int GetWindowTextLength(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowText(nint window, StringBuilder title, int maximumCount);

    [DllImport("user32.dll", EntryPoint = "IsWindow", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeIsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);
}
