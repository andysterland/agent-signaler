using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Updates;

public interface IUpdateStorage
{
    string DirectoryPath { get; }
    IDisposable AcquireLease();
    byte[]? Read(string name, int maximumBytes = UpdatePolicy.CacheLimit);
    void Write(string name, ReadOnlySpan<byte> bytes);
    void Delete(string name);
    IReadOnlyList<string> List(string pattern, int maximumCount);
    byte[]? ReadRun(Guid runId, string name);
    IReadOnlyList<Guid> ListRuns();
    string CreateRunDirectory(Guid runId);
}

public static class UpdateStrictJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static T Read<T>(ReadOnlySpan<byte> bytes, int maximumBytes = UpdatePolicy.CacheLimit)
    {
        if (bytes.Length is < 1 || bytes.Length > maximumBytes) throw new InvalidDataException("update_document_limit");
        return JsonSerializer.Deserialize<T>(bytes, Options) ?? throw new InvalidDataException("update_document_invalid");
    }

    public static byte[] Write<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > UpdatePolicy.CacheLimit) throw new InvalidDataException("update_document_limit");
        return bytes;
    }
}

// Intentionally independent of Remote.AtomicFile: Updates cannot depend on Remote. Mirrors its
// create-new / flush-to-disk / atomic-rename pattern, adding protected ACLs and pinned directories.
[SupportedOSPlatform("windows")]
public sealed class UpdateStorage : IUpdateStorage, IDisposable
{
    public const int MaximumDocumentBytes = 65536;
    private readonly SecurityIdentifier user;
    private readonly List<SafeFileHandle> directories = [];
    private readonly bool directoryPresent;
    public string DirectoryPath { get; }

    public UpdateStorage(string localAppData, string installDirectory) : this(localAppData, installDirectory, createMissing: true) { }

    private UpdateStorage(string localAppData, string installDirectory, bool createMissing)
    {
        using var identity = WindowsIdentity.GetCurrent();
        user = identity.User ?? throw new UnauthorizedAccessException("update_storage_user");
        var root = UpdatePath.Canonical(localAppData);
        var install = UpdatePath.Canonical(installDirectory).ToUpperInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(install))).ToLowerInvariant();
        DirectoryPath = Path.Combine(root, "AgentSignaler", "Updates", key);
        try
        {
            PinAncestors(root);
            var current = root;
            foreach (var segment in new[] { "AgentSignaler", "Updates", key })
            {
                current = Path.Combine(current, segment);
                if (!createMissing)
                {
                    try
                    {
                        if ((File.GetAttributes(current) & FileAttributes.Directory) == 0)
                            throw new InvalidDataException("update_storage_directory");
                    }
                    catch (FileNotFoundException) { return; }
                    catch (DirectoryNotFoundException) { return; }
                }
                // AgentSignaler may already be shared by existing components. It is not repaired.
                if (segment == "AgentSignaler" && Directory.Exists(current))
                {
                    UpdatePath.RequireNoReparsePoints(current);
                    directories.Add(Pin(current));
                    continue;
                }
                if (createMissing) CreateDirectory(current);
                directories.Add(Pin(current));
                Validate(new DirectoryInfo(current));
            }
            directoryPresent = true;
        }
        catch { Dispose(); throw; }
    }

    // MSI registration can disappear during servicing. Its absence must not turn
    // an installed executable into an ungated portable launch. Never clear or
    // infer the outcome of a marker without verified inventory.
    public static bool HasMaintenanceRecords(string localAppData, string installDirectory)
    {
        using var storage = new UpdateStorage(localAppData, installDirectory, createMissing: false);
        return storage.directoryPresent &&
            (storage.Read("gate.json") is not null || storage.Read("restart.json") is not null);
    }

    public IDisposable AcquireLease() => AcquireNamedLease("maintenance.lock");

    public IDisposable AcquireCheckLease() => AcquireNamedLease("check.lock");

    private IDisposable AcquireNamedLease(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path))
        {
            try { using var created = Create(path, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { /* Another owner created it. Validate below. */ }
        }
        Validate(new FileInfo(path));
        return new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    public byte[]? Read(string name, int maximumBytes = MaximumDocumentBytes)
    {
        if (maximumBytes is < 1 or > MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var path = PathFor(name);
        try
        {
            Validate(new FileInfo(path));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 || stream.Length > maximumBytes) throw new InvalidDataException("update_document_limit");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    public void Write(string name, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > MaximumDocumentBytes) throw new InvalidDataException("update_document_limit");
        var path = PathFor(name);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".new";
        try
        {
            using (var stream = Create(pending, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            try { Validate(new FileInfo(path)); }
            catch (FileNotFoundException) { /* Only a genuinely absent path may be created. */ }
            File.Move(pending, path, true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        try { Validate(new FileInfo(path)); }
        catch (FileNotFoundException) { return; }
        File.Delete(path);
    }

    public string CreateRunDirectory(Guid runId)
    {
        if (runId == Guid.Empty) throw new ArgumentException("update_run_invalid", nameof(runId));
        if (Directory.EnumerateDirectories(DirectoryPath).Take(33).Count() >= 32)
            throw new InvalidDataException("update_run_capacity");
        var path = Path.Combine(DirectoryPath, runId.ToString("N"));
        if (Directory.Exists(path)) throw new IOException("update_run_exists");
        CreateDirectory(path);
        Validate(new DirectoryInfo(path));
        directories.Add(Pin(path));
        return path;
    }

    public IReadOnlyList<string> List(string pattern, int maximumCount)
    {
        if (pattern != "instance-*.json" || maximumCount is < 1 or > 64) throw new ArgumentException("update_list_invalid");
        var names = Directory.EnumerateFiles(DirectoryPath, pattern).Take(maximumCount + 1).Select(Path.GetFileName).ToArray();
        if (names.Length > maximumCount) throw new InvalidDataException("update_participant_capacity");
        return names!;
    }

    public IReadOnlyList<Guid> ListRuns()
    {
        Validate(new DirectoryInfo(DirectoryPath));
        var paths = Directory.EnumerateDirectories(DirectoryPath).Take(33).ToArray();
        if (paths.Length > 32) throw new InvalidDataException("update_run_capacity");
        var ids = new List<Guid>();
        foreach (var path in paths)
        {
            Validate(new DirectoryInfo(path));
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out var id) || id == Guid.Empty)
                throw new InvalidDataException("update_run_identity");
            ids.Add(id);
        }
        return ids;
    }

    public byte[]? ReadRun(Guid runId, string name)
    {
        if (runId == Guid.Empty || name is not ("result.json" or "handoff.json")) throw new ArgumentException("update_run_identity");
        var directory = Path.Combine(DirectoryPath, runId.ToString("N"));
        Validate(new DirectoryInfo(DirectoryPath));
        Validate(new DirectoryInfo(directory));
        using var pin = Pin(directory);
        var path = Path.Combine(directory, name);
        try
        {
            Validate(new FileInfo(path), false);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > MaximumDocumentBytes) throw new InvalidDataException("update_document_limit");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
    }

    private string PathFor(string name)
    {
        ObjectDisposedException.ThrowIf(directories.Count == 0, this);
        if (name.Length is < 1 or > 100 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-') || name is "." or "..")
            throw new InvalidDataException("update_storage_name");
        Validate(new DirectoryInfo(DirectoryPath));
        return Path.Combine(DirectoryPath, name);
    }

    private FileStream Create(string path, FileShare share)
    {
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, share, 4096,
            FileOptions.WriteThrough, security);
    }

    private void CreateDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null) throw new UnauthorizedAccessException("update_storage_link");
        if (Directory.Exists(path)) { Validate(new DirectoryInfo(path)); return; }
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
        Validate(new DirectoryInfo(path));
    }

    private void Validate(FileSystemInfo info, bool requireProtected = true)
    {
        if ((File.GetAttributes(info.FullName) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("update_storage_link");
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("update_storage_missing");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("update_storage_link");
        FileSystemSecurity security = info is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)info).GetAccessControl();
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || requireProtected && !security.AreAccessRulesProtected ||
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference)))
            throw new UnauthorizedAccessException("update_storage_acl");
    }

    private void PinAncestors(string path)
    {
        var ancestors = new Stack<string>();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        foreach (var current in ancestors)
        {
            UpdatePath.RequireNoReparsePoints(current);
            directories.Add(Pin(current));
        }
    }

    private static SafeFileHandle Pin(string path)
    {
        var handle = CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("update_storage_pin"); }
        return handle;
    }

    public void Dispose()
    {
        foreach (var directory in directories) directory.Dispose();
        directories.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFileW", SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}