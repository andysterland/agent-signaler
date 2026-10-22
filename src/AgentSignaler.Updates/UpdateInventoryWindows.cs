using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Updates;

[SupportedOSPlatform("windows")]
public static class WindowsUpdateInventoryPath
{
    public static IDisposable PinVerifiedDirectory(string directory)
    {
        var pins = new Pins();
        try
        {
            var path = UpdatePath.Canonical(directory);
            for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                UpdatePath.RequireNoReparsePoints(current);
                var handle = CreateFile(current, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("update_install_directory_pin"); }
                pins.Handles.Add(handle);
            }
            return pins;
        }
        catch { pins.Dispose(); throw; }
    }

    private sealed class Pins : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); Handles.Clear(); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}