using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace AgentSignaler.Remote;

[SupportedOSPlatform("windows")]
internal static class WindowsStartupShortcut
{
    private const string Description = "Agent Signaler owned Client sign-in startup: ";

    public static byte[] Create(string name, string command)
    {
        var parsed = WindowsIntegrationStartup.ParseCommand(name, command);
        return WithLink((link, persist) =>
        {
            link.SetPath(parsed.Client);
            link.SetArguments(parsed.Arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(parsed.Client)!);
            link.SetDescription(Description + name);
            link.SetShowCmd(1);
            var stream = SHCreateMemStream(null, 0);
            try
            {
                persist.Save(stream, true);
                stream.Stat(out var stat, 1);
                if (stat.cbSize is <= 0 or > 65536) throw new InvalidDataException("Startup shortcut is too large.");
                var bytes = new byte[(int)stat.cbSize];
                stream.Seek(0, 0, IntPtr.Zero);
                stream.Read(bytes, bytes.Length, IntPtr.Zero);
                return bytes;
            }
            finally { Marshal.FinalReleaseComObject(stream); }
        });
    }

    public static string Read(string name, byte[] bytes)
    {
        if (bytes.Length is < 76 or > 65536 || BitConverter.ToUInt32(bytes) != 76)
            throw new InvalidDataException("Unrelated or invalid startup shortcut was preserved.");
        // Reject links that request elevation, environment expansion or advertised/MSI targets.
        var flags = BitConverter.ToUInt32(bytes, 20);
        const uint allowedFlags = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x20 | 0x80 | 0x100;
        if ((flags & ~allowedFlags) != 0)
            throw new InvalidDataException("Unsupported startup shortcut features were preserved.");
        return WithLink((link, persist) =>
        {
            var stream = SHCreateMemStream(bytes, (uint)bytes.Length);
            try { persist.Load(stream); }
            finally { Marshal.FinalReleaseComObject(stream); }
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var description = new StringBuilder(1024);
            var workingDirectory = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetDescription(description, description.Capacity);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            link.GetShowCmd(out var show);
            link.GetHotkey(out var hotkey);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            var command = ScheduledTaskDefinition.QuoteArgument(target.ToString()) + " " + arguments;
            var parsed = WindowsIntegrationStartup.ParseCommand(name, command);
            if (description.ToString() != Description + name || show != 1 || hotkey != 0 ||
                icon.Length != 0 || iconIndex != 0 ||
                !string.Equals(workingDirectory.ToString(), Path.GetDirectoryName(parsed.Client),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unrelated startup shortcut was preserved.");
            return command;
        });
    }

    private static T WithLink<T>(Func<IShellLinkW, IPersistStream, T> action)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            return action((IShellLinkW)link, (IPersistStream)link);
        }
        catch (COMException ex) { throw new InvalidDataException("Client startup shortcut could not be accessed.", ex); }
        finally { if (link is not null) Marshal.FinalReleaseComObject(link); }
    }

    [DllImport("shlwapi.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Interface)]
    private static extern IStream SHCreateMemStream(byte[]? bytes, uint length);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("00000109-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistStream
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load(IStream stream);
        void Save(IStream stream, [MarshalAs(UnmanagedType.Bool)] bool clearDirty);
        void GetSizeMax(out long size);
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
