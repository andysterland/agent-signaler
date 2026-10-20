using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AgentSignaler.Contracts;
using Microsoft.Win32.SafeHandles;

namespace AgentSignaler.Discovery;

public sealed record DiscoveryCredentialReference(string Id, string CiphertextHash)
{
    public override string ToString() => "Protected discovery credential reference";

    public void Validate()
    {
        if (Id is not { Length: 32 } || !Guid.TryParseExact(Id, "N", out var id) || id == Guid.Empty || Id != id.ToString("N") ||
            CiphertextHash is not { Length: 64 } || CiphertextHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DiscoveryException("discovery_store_reference");
    }
}

public sealed record DiscoveryPreparedCredential(DiscoveryCredentialReference Reference, byte[] Ciphertext)
{
    public override string ToString() => "Protected discovery credential update";
}

/// <summary>Configuration keeps only the opaque reference; the package remains current-user protected.</summary>
public interface IDiscoveryCredentialStore
{
    Task<DiscoveryCredentialReference> StoreAsync(DiscoveryConnectionPackage package, CancellationToken cancellationToken = default);
    Task<DiscoveryConnectionPackage> LoadAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default);
}

public interface IDiscoverySigningKeyStore
{
    Task<DiscoveryCredentialReference> CreateSigningKeyAsync(CancellationToken cancellationToken = default);
    Task<ECDsa> LoadSigningKeyAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default);
    Task DeleteAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default);
}

/// <summary>Owns only randomly named, DPAPI current-user protected files beneath an exclusive directory.</summary>
[SupportedOSPlatform("windows")]
public sealed class CurrentUserDiscoveryCredentialStore : IDiscoveryCredentialStore, IDiscoverySigningKeyStore
{
    private const int MaximumCiphertextBytes = 24 * 1024;
    private const uint ReadControl = 0x00020000;
    private const uint ReadAttributes = 0x80;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private readonly string root;

    public CurrentUserDiscoveryCredentialStore(string rootDirectory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        root = Path.GetFullPath(rootDirectory);
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || root.Length > 240 ||
            root.IndexOf(':', 2) >= 0 || Path.GetPathRoot(root) == root || !Path.IsPathFullyQualified(rootDirectory))
            throw new DiscoveryException("discovery_store_path");
    }

    public Task<DiscoveryCredentialReference> StoreAsync(DiscoveryConnectionPackage package, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = Encoding.UTF8.GetBytes(DiscoveryCodec.EncodePackage(package));
        try { return Task.FromResult(Io(() => Store(plaintext, "credential", cancellationToken))); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>Creates ciphertext in memory for a transactional preview or journal without writing a file.</summary>
    public static DiscoveryPreparedCredential Prepare(DiscoveryConnectionPackage package)
    {
        var plaintext = Encoding.UTF8.GetBytes(DiscoveryCodec.EncodePackage(package));
        try
        {
            var ciphertext = Protect(plaintext, "credential", decrypt: false);
            return new(new(Guid.NewGuid().ToString("N"), Fingerprint(ciphertext)), ciphertext);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task<byte[]?> ReadCiphertextAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Io<byte[]?>(() =>
        {
            ValidateReference(reference);
            using var paths = LockPath(false);
            var path = CredentialPath(reference);
            if (!EntryExists(path)) return null;
            using var handle = OpenCheckedFile(path, delete: false);
            var ciphertext = ReadCiphertext(handle);
            ValidateCiphertext(ciphertext, reference);
            return ciphertext;
        }));
    }

    /// <summary>Restores only authenticated protected data, refusing existing files whose ciphertext changed.</summary>
    public Task WritePreparedAsync(DiscoveryPreparedCredential prepared, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(prepared);
        ValidateReference(prepared.Reference);
        if (prepared.Ciphertext is not { Length: > 0 and <= MaximumCiphertextBytes })
            throw new DiscoveryException("discovery_store_size");
        var ciphertext = prepared.Ciphertext.ToArray();
        ValidateCiphertext(ciphertext, prepared.Reference);
        var plaintext = Protect(ciphertext, "credential", decrypt: true);
        try
        {
            if (plaintext.Length > DiscoveryProtocol.MaximumPackageBytes) throw new DiscoveryException("discovery_store_size");
            _ = Io(() => DiscoveryCodec.ParsePackage(new UTF8Encoding(false, true).GetString(plaintext)));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Io(() => { WriteCiphertext(prepared.Reference, ciphertext, cancellationToken); return true; });
        return Task.CompletedTask;
    }

    public Task<DiscoveryConnectionPackage> LoadAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = Io(() => Load(reference, "credential", cancellationToken));
        try { return Task.FromResult(Io(() => DiscoveryCodec.ParsePackage(new UTF8Encoding(false, true).GetString(plaintext)))); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task<DiscoveryCredentialReference> CreateSigningKeyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plaintext = key.ExportPkcs8PrivateKey();
        try { return Task.FromResult(Io(() => Store(plaintext, "signing-key", cancellationToken))); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task<ECDsa> LoadSigningKeyAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = Io(() => Load(reference, "signing-key", cancellationToken));
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(plaintext, out var read);
            if (read != plaintext.Length) throw new DiscoveryException("discovery_store_key");
            _ = DiscoveryCodec.ExportPublicKey(key);
            return Task.FromResult(key);
        }
        catch (CryptographicException) { key.Dispose(); throw new DiscoveryException("discovery_store_key"); }
        catch { key.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public Task DeleteAsync(DiscoveryCredentialReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Io(() =>
        {
            ValidateReference(reference);
            using var paths = LockPath(false);
            var path = CredentialPath(reference);
            using var handle = OpenCheckedFile(path, delete: true);
            ValidateCiphertext(ReadCiphertext(handle), reference);
            cancellationToken.ThrowIfCancellationRequested();
            var disposition = new FileDisposition { DeleteFile = true };
            if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                throw new DiscoveryException("discovery_store_delete");
            return true;
        });
        return Task.CompletedTask;
    }

    private DiscoveryCredentialReference Store(byte[] plaintext, string purpose, CancellationToken cancellationToken)
    {
        if (plaintext.Length > DiscoveryProtocol.MaximumPackageBytes) throw new DiscoveryException("discovery_store_size");
        var ciphertext = Protect(plaintext, purpose, decrypt: false);
        if (ciphertext.Length > MaximumCiphertextBytes) throw new DiscoveryException("discovery_store_size");
        var reference = new DiscoveryCredentialReference(Guid.NewGuid().ToString("N"), Fingerprint(ciphertext));
        WriteCiphertext(reference, ciphertext, cancellationToken);
        return reference;
    }

    private void WriteCiphertext(DiscoveryCredentialReference reference, byte[] ciphertext, CancellationToken cancellationToken)
    {
        using var paths = LockPath(true);
        var path = CredentialPath(reference);
        if (File.Exists(path))
        {
            using var handle = OpenCheckedFile(path, delete: false);
            ValidateCiphertext(ReadCiphertext(handle), reference);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        WriteNewProtectedFile(path, ciphertext);
    }

    private static void WriteNewProtectedFile(string path, byte[] ciphertext)
    {
        var security = new FileSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.SetOwner(identity.User!);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        // Create-new and an ACL supplied at creation prevent replacing or exposing foreign files.
        using (var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
            4096, FileOptions.WriteThrough, security))
        {
            try
            {
                file.Write(ciphertext);
                file.Flush(flushToDisk: true);
            }
            catch
            {
                var disposition = new FileDisposition { DeleteFile = true };
                if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                    throw new DiscoveryException("discovery_store_rollback");
                throw;
            }
        }
    }

    private byte[] Load(DiscoveryCredentialReference reference, string purpose, CancellationToken cancellationToken)
    {
        ValidateReference(reference);
        using var paths = LockPath(false);
        using var handle = OpenCheckedFile(CredentialPath(reference), delete: false);
        var ciphertext = ReadCiphertext(handle);
        ValidateCiphertext(ciphertext, reference);
        cancellationToken.ThrowIfCancellationRequested();
        var plaintext = Protect(ciphertext, purpose, decrypt: true);
        if (plaintext.Length > DiscoveryProtocol.MaximumPackageBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new DiscoveryException("discovery_store_size");
        }
        return plaintext;
    }

    private PathLocks? LockPath(bool create, bool allowMissing = false)
    {
        var locks = new PathLocks();
        try
        {
            var current = Path.GetPathRoot(root)!;
            locks.Add(OpenPath(current, directory: true));
            var segments = root[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
            {
                current = Path.Combine(current, segments[i]);
                var isRoot = i == segments.Length - 1;
                if (!Directory.Exists(current))
                {
                    if (allowMissing)
                    {
                        if (EntryExists(current))
                        {
                            using var invalid = OpenPath(current, directory: true);
                            throw new DiscoveryException("discovery_store_directory_missing");
                        }
                        locks.Dispose();
                        return null;
                    }
                    if (!create || !isRoot) throw new DiscoveryException("discovery_store_directory_missing");
                    using var identity = WindowsIdentity.GetCurrent();
                    var security = new DirectorySecurity();
                    security.SetOwner(identity.User!);
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    new DirectoryInfo(current).Create(security);
                }
                locks.Add(OpenPath(current, directory: true));
            }
            ValidateSecurity(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            return locks;
        }
        catch { locks.Dispose(); throw; }
    }

    private static SafeFileHandle OpenCheckedFile(string path, bool delete, bool write = false, bool shareDelete = false)
    {
        var handle = OpenPath(path, directory: false, delete, write, shareDelete);
        try
        {
            ValidateSecurity(new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access));
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle OpenPath(string path, bool directory, bool delete = false, bool write = false, bool shareDelete = false)
    {
        var handle = CreateFile(path, ReadControl | ReadAttributes | (directory ? 0 : GenericRead) | (delete ? DeleteAccess : 0) | (write ? GenericWrite : 0),
            FileShare.Read | (directory ? FileShare.Write : 0) | (shareDelete ? FileShare.Delete : 0), IntPtr.Zero, OpenExisting,
            OpenReparsePoint | (directory ? BackupSemantics : 0), IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new DiscoveryException("discovery_store_open"); }
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, (uint)Marshal.SizeOf<FileAttributeTag>()) ||
            (attributes.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((attributes.FileAttributes & (uint)FileAttributes.Directory) != 0) != directory)
        {
            handle.Dispose();
            throw new DiscoveryException("discovery_store_reparse");
        }
        return handle;
    }

    private static void ValidateSecurity(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!identity.User!.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected)
            throw new DiscoveryException("discovery_store_owner");
        var ownerAccess = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!identity.User.Equals(rule.IdentityReference)) throw new DiscoveryException("discovery_store_acl");
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) ownerAccess = true;
        }
        if (!ownerAccess) throw new DiscoveryException("discovery_store_acl");
    }

    private static byte[] ReadCiphertext(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        if (length is <= 0 or > MaximumCiphertextBytes) throw new DiscoveryException("discovery_store_size");
        var ciphertext = new byte[(int)length];
        var offset = 0;
        while (offset < ciphertext.Length)
        {
            var count = RandomAccess.Read(handle, ciphertext.AsSpan(offset), offset);
            if (count == 0) throw new DiscoveryException("discovery_store_read");
            offset += count;
        }
        return ciphertext;
    }

    private static void ValidateReference(DiscoveryCredentialReference reference)
    {
        if (reference is null) throw new DiscoveryException("discovery_store_reference");
        reference.Validate();
    }

    private static void ValidateCiphertext(byte[] ciphertext, DiscoveryCredentialReference reference)
    {
        if (!string.Equals(Fingerprint(ciphertext), reference.CiphertextHash, StringComparison.Ordinal))
            throw new DiscoveryException("discovery_store_hash");
    }

    private string CredentialPath(DiscoveryCredentialReference reference) => Path.Combine(root, reference.Id + ".bin");
    private static string Fingerprint(byte[] ciphertext) => Convert.ToHexStringLower(SHA256.HashData(ciphertext));

    private static T Io<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DecoderFallbackException)
        { throw new DiscoveryException("discovery_store_io"); }
    }

    internal DiscoveryReplayState? LoadReplay(Guid discoveryId, bool readOnly = false)
    {
        if (discoveryId == Guid.Empty) throw new DiscoveryException("discovery_identity");
        using var paths = LockPath(create: !readOnly, allowMissing: readOnly);
        if (paths is null) return null;
        var path = Path.Combine(root, discoveryId.ToString("N") + ".replay");
        if (!EntryExists(path))
        {
            var stagedPath = path + ".pending";
            if (!EntryExists(stagedPath)) return null;
            using var staged = OpenCheckedFile(stagedPath, delete: false, shareDelete: true);
            var recovered = DecodeReplay(ReadCiphertext(staged), discoveryId);
            if (!readOnly) File.Move(stagedPath, path, overwrite: false);
            return recovered;
        }
        using var handle = OpenCheckedFile(path, delete: false);
        return DecodeReplay(ReadCiphertext(handle), discoveryId);
    }

    internal void SaveReplay(DiscoveryReplayState state)
    {
        CurrentUserDiscoveryReplayStore.Validate(state);
        var plaintext = CurrentUserDiscoveryReplayStore.Encode(state);
        byte[] ciphertext;
        try { ciphertext = Protect(plaintext, "replay:" + state.DiscoveryId.ToString("N"), decrypt: false); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        using var paths = LockPath(true);
        var path = Path.Combine(root, state.DiscoveryId.ToString("N") + ".replay");
        if (EntryExists(path))
        {
            using var handle = OpenCheckedFile(path, delete: false, write: true, shareDelete: true);
            var before = ReadCiphertext(handle);
            var previous = DecodeReplay(before, state.DiscoveryId);
            if (state.Revision < previous.Revision || state.Revision == previous.Revision && state.PayloadFingerprint != previous.PayloadFingerprint)
                throw new DiscoveryException("discovery_replay");
            if (state.Revision == previous.Revision) return;
            WriteReplayAtomically(path, ciphertext, state.DiscoveryId, replace: true);
            return;
        }
        WriteReplayAtomically(path, ciphertext, state.DiscoveryId, replace: false);
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
    }

    private static void WriteReplayAtomically(string path, byte[] ciphertext, Guid discoveryId, bool replace)
    {
        var stagedPath = path + ".pending";
        if (EntryExists(stagedPath))
        {
            using var stale = OpenCheckedFile(stagedPath, delete: true);
            _ = DecodeReplay(ReadCiphertext(stale), discoveryId);
            MarkDeleted(stale);
        }
        WriteNewProtectedFile(stagedPath, ciphertext);
        try
        {
            if (replace) File.Replace(stagedPath, path, destinationBackupFileName: null);
            else File.Move(stagedPath, path, overwrite: false);
        }
        finally
        {
            if (EntryExists(stagedPath))
            {
                using var owned = OpenCheckedFile(stagedPath, delete: true);
                if (Fingerprint(ReadCiphertext(owned)) != Fingerprint(ciphertext))
                    throw new DiscoveryException("discovery_replay_staging_changed");
                MarkDeleted(owned);
            }
        }
    }

    private static void MarkDeleted(SafeFileHandle handle)
    {
        var disposition = new FileDisposition { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new DiscoveryException("discovery_replay_rollback");
    }

    private static DiscoveryReplayState DecodeReplay(byte[] ciphertext, Guid discoveryId)
    {
        var plaintext = Protect(ciphertext, "replay:" + discoveryId.ToString("N"), decrypt: true);
        try { return CurrentUserDiscoveryReplayStore.Decode(plaintext, discoveryId); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] Protect(byte[] input, string purpose, bool decrypt)
    {
        var entropy = Encoding.UTF8.GetBytes("AgentSignaler.Discovery:v1:" + purpose);
        var inputBlob = Allocate(input);
        var entropyBlob = Allocate(entropy);
        DataBlob output = default;
        try
        {
            var success = decrypt
                ? CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success || output.Length <= 0 || output.Length > MaximumCiphertextBytes)
                throw new DiscoveryException("discovery_store_protection");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Free(inputBlob, local: false);
            Free(entropyBlob, local: false);
            Free(output, local: true);
        }
    }

    private static DataBlob Allocate(byte[] bytes)
    {
        var blob = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Marshal.Copy(bytes, 0, blob.Data, bytes.Length);
        return blob;
    }

    private static void Free(DataBlob blob, bool local)
    {
        if (blob.Data == IntPtr.Zero) return;
        for (var i = 0; i < blob.Length; i++) Marshal.WriteByte(blob.Data, i, 0);
        if (local) _ = LocalFree(blob.Data); else Marshal.FreeHGlobal(blob.Data);
    }

    private sealed class PathLocks : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        public void Add(SafeFileHandle handle) => handles.Add(handle);
        public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag { public uint FileAttributes; public uint ReparseTag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTag info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileDisposition info, uint size);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
