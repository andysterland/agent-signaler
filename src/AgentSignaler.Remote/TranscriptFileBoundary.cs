using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Remote;

public sealed class TranscriptFileBoundaryException(string category, bool transient = false) : IOException(category)
{
    public string Category { get; } = category;
    public bool IsTransient { get; } = transient;
}

public sealed class ValidatedTranscriptFile(FileStream stream, string identity) : IDisposable
{
    public FileStream Stream { get; } = stream;
    public string Identity { get; } = identity;
    public void Dispose() => Stream.Dispose();
}

/// <summary>Opens each component relative to its already validated parent handle.</summary>
public static class TranscriptFileBoundary
{
    public static bool IsExpectedPath(LocalTranscriptReference reference, ITranscriptFileAdapter adapter)
    {
        if (!reference.IsValid || reference.Source != adapter.Source ||
            !CanonicalLocalPath(reference.Path) || !CanonicalLocalPath(adapter.TranscriptRoot)) return false;
        var name = adapter.GetExpectedFileName(reference.SessionId);
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(['\\', '/', ':']) >= 0 ||
            name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.')) return false;
        return string.Equals(reference.Path,
            System.IO.Path.Combine(adapter.TranscriptRoot, name), StringComparison.OrdinalIgnoreCase);
    }

    public static ValidatedTranscriptFile Open(LocalTranscriptReference reference, ITranscriptFileAdapter adapter)
    {
        if (!OperatingSystem.IsWindows() || !IsExpectedPath(reference, adapter))
            throw new TranscriptFileBoundaryException(TranscriptReaderCategories.PathRejected);
        return OpenWindows(reference.Path);
    }

    private static bool CanonicalLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Length < 3 ||
            !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.AsSpan(2).IndexOf(':') >= 0 || path.Contains('/') || path.Any(char.IsControl)) return false;
        var parts = path[3..].Split('\\');
        return parts.All(p => p.Length > 0 && p is not "." and not ".." &&
            !p.EndsWith(' ') && !p.EndsWith('.') && p.IndexOfAny(['*', '?', '"', '<', '>', '|']) < 0);
    }

    [SupportedOSPlatform("windows")]
    private static ValidatedTranscriptFile OpenWindows(string path)
    {
        var root = path[..3];
        if (GetDriveType(root) != 3)
            throw new TranscriptFileBoundaryException(TranscriptReaderCategories.PathRejected);
        var handles = new List<SafeFileHandle>();
        try
        {
            var drive = CreateFile(root, 0x120080, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (drive.IsInvalid)
            {
                drive.Dispose();
                ThrowLastError();
            }
            handles.Add(drive);
            var components = path[3..].Split('\\');
            var expected = root.TrimEnd('\\');
            for (var i = 0; i < components.Length; i++)
            {
                var final = i == components.Length - 1;
                var child = OpenRelative(handles[^1], components[i], final);
                handles.Add(child);
                expected += "\\" + components[i];
                ValidateLocation(child, expected, final);
            }
            var file = handles[^1];
            ValidateOwner(handles[^2]);
            ValidateOwner(file);
            if (!GetFileInformationByHandle(file, out var info)) ThrowLastError();
            if (info.NumberOfLinks != 1)
                throw new TranscriptFileBoundaryException(TranscriptReaderCategories.PathRejected);
            var identity = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}:" +
                $"{info.CreationTimeHigh:X8}{info.CreationTimeLow:X8}";
            // Recheck the final location after ownership and metadata inspection, on the same handle.
            ValidateLocation(file, path, true);
            var stream = new FileStream(file, FileAccess.Read, 1, isAsync: true);
            handles.RemoveAt(handles.Count - 1);
            return new ValidatedTranscriptFile(stream, identity);
        }
        finally
        {
            foreach (var handle in handles) handle.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool file)
    {
        var buffer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = buffer
            }, unicodePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = 0x40 // OBJ_CASE_INSENSITIVE; each component is opened without following reparses.
            };
            var result = NtCreateFile(out var handle, file ? 0x80000000u : 0x120080u,
                ref attributes, out _, IntPtr.Zero, 0, 7, 1,
                file ? 0x00200040u : 0x00200021u, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (result < 0)
            {
                handle?.Dispose();
                ThrowError(unchecked((int)RtlNtStatusToDosError(result)));
            }
            return handle!;
        }
        finally
        {
            Marshal.FreeHGlobal(unicodePointer);
            Marshal.FreeHGlobal(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateLocation(SafeFileHandle handle, string expected, bool file)
    {
        if (!GetFileInformationByHandle(handle, out var info)) ThrowLastError();
        if ((info.FileAttributes & 0x400) != 0 || ((info.FileAttributes & 0x10) == 0) != file ||
            GetFileType(handle) != 1)
            throw new TranscriptFileBoundaryException(TranscriptReaderCategories.PathRejected);
        var result = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, result, (uint)result.Capacity, 0);
        if (length == 0 || length >= result.Capacity ||
            !string.Equals(result.ToString(), @"\\?\" + expected, StringComparison.OrdinalIgnoreCase))
            throw new TranscriptFileBoundaryException(TranscriptReaderCategories.PathRejected);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateOwner(SafeFileHandle file)
    {
        var error = GetSecurityInfo(file, 1, 1, out var owner, IntPtr.Zero,
            IntPtr.Zero, IntPtr.Zero, out var descriptor);
        try
        {
            if (error != 0) ThrowError((int)error);
            using var current = WindowsIdentity.GetCurrent();
            if (owner == IntPtr.Zero || current.User is null ||
                !new SecurityIdentifier(owner).Equals(current.User))
                throw new TranscriptFileBoundaryException(TranscriptReaderCategories.PathRejected);
        }
        finally
        {
            if (descriptor != IntPtr.Zero) LocalFree(descriptor);
        }
    }

    private static void ThrowLastError() => ThrowError(Marshal.GetLastPInvokeError());
    private static void ThrowError(int error) => throw new TranscriptFileBoundaryException(
        TranscriptReaderCategories.Unavailable, error is 2 or 3 or 32 or 33);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status, Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes, CreationTimeLow, CreationTimeHigh, LastAccessTimeLow, LastAccessTimeHigh,
            LastWriteTimeLow, LastWriteTimeHigh, VolumeSerialNumber, FileSizeHigh, FileSizeLow,
            NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle file, uint access, ref ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition,
        uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string path);
    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInformation,
        out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
