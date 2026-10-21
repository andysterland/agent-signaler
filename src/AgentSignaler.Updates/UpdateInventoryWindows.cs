using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Updates;

[SupportedOSPlatform("windows")]
public static class WindowsUpdateInventoryPath
{
    // Install ACLs need not be storage ACLs: inherited read/execute grants are normal.
    // Only the installing user, SYSTEM and local administrators may hold mutation rights.
    public static IDisposable PinVerifiedDirectory(string directory, string userSid)
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
            var user = new SecurityIdentifier(userSid);
            var security = new DirectoryInfo(path).GetAccessControl();
            if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new UnauthorizedAccessException("update_install_directory_owner");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            const FileSystemRights mutation = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>())
                if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & mutation) != 0 &&
                    !user.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference) && !administrators.Equals(rule.IdentityReference))
                    throw new UnauthorizedAccessException("update_install_directory_permissions");
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