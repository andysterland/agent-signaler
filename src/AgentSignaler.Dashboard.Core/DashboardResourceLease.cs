using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Dashboard;

public sealed class DashboardOwnershipException : IOException
{
    public DashboardOwnershipException() : base(
        "The Dashboard data directory is already owned. Exit Dashboard or RpcHost, and upgrade older Dashboard versions before retrying.") { }
}

/// <summary>A process-owned, cross-session lease. Runtimes borrow this handle and never release it.</summary>
public sealed class DashboardResourceLease : IDisposable
{
    private FileStream? file;
    private SafeFileHandle? directory;
    public string CanonicalDirectory { get; }
    public bool IsHeld => Volatile.Read(ref file) is not null;

    private DashboardResourceLease(string path, SafeFileHandle directory, FileStream file)
    {
        CanonicalDirectory = path;
        this.directory = directory;
        this.file = file;
    }

    public static DashboardResourceLease Acquire(string? dataDirectory = null, string? environmentDirectory = null)
    {
        environmentDirectory ??= Environment.GetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR");
        if (string.IsNullOrWhiteSpace(environmentDirectory)) environmentDirectory = null;
        var selected = ValidatePath(dataDirectory ?? environmentDirectory ?? DefaultDirectory);
        Directory.CreateDirectory(selected);
        var handle = OpenDirectory(selected, preventRename: true);
        try
        {
            var canonical = FinalPath(handle);
            if (dataDirectory is not null && !string.IsNullOrWhiteSpace(environmentDirectory))
            {
                var other = ValidatePath(environmentDirectory);
                Directory.CreateDirectory(other);
                using var alias = OpenDirectory(other);
                if (!string.Equals(canonical, FinalPath(alias), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The argument and environment data directories must identify the same directory.");
            }
            RejectLegacyDashboard();
            FileStream lease;
            try
            {
                lease = new FileStream(Path.Combine(canonical, ".dashboard-resource.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                throw new DashboardOwnershipException();
            }
            return new(canonical, handle, lease);
        }
        catch { handle.Dispose(); throw; }
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSignaler");

    public static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Any(char.IsControl))
            throw new ArgumentException("Use an absolute local data directory.");
        path = Environment.ExpandEnvironmentVariables(path);
        if (path.Length > 32768 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an absolute local data directory.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static string ResolveExistingDirectory(string path)
    {
        path = ValidatePath(path);
        if (!Directory.Exists(path)) return path;
        using var handle = OpenDirectory(path);
        return FinalPath(handle);
    }

    private static SafeFileHandle OpenDirectory(string path, bool preventRename = false)
    {
        var handle = CreateFile(path, 0, preventRename ? 3u : 7u, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("The data directory could not be opened.", new Win32Exception());
        }
        return handle;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new IOException("The data directory could not be resolved.");
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
        return Path.TrimEndingDirectorySeparator(result);
    }

    private static void RejectLegacyDashboard()
    {
        foreach (var process in Process.GetProcessesByName("AgentSignaler.Dashboard"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    var executable = process.MainModule?.FileName;
                    var assembly = executable is null ? null : Path.Combine(Path.GetDirectoryName(executable)!, "AgentSignaler.Dashboard.dll");
                    if (assembly is null || !SupportsResourceLease(assembly))
                        throw new DashboardOwnershipException();
                }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) { throw new DashboardOwnershipException(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
                { throw new DashboardOwnershipException(); }
            }
        }
    }

    internal static bool SupportsResourceLease(string assemblyPath)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return false;
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" || reader.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() == 1 && blob.ReadSerializedString() == "DashboardResourceLeaseVersion" &&
                blob.ReadSerializedString() == "1") return true;
        }
        return false;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref file, null)?.Dispose();
        Interlocked.Exchange(ref directory, null)?.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security,
        uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}
