using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignaler.Dashboard;

// The canonical Dashboard lease owns this file. Secrets never enter dashboard-settings.json.
internal sealed class PrivateDiscoveryJournal(string directory, Func<bool> ownsLease) : IPrivateDiscoveryJournal
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly byte[] Entropy = "AgentSignaler.PrivateDiscovery.v1"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 12 };
    private readonly string path = Path.Combine(directory, "private-discovery.dpapi");
    private byte[]? fingerprint;

    public async Task<PrivateDiscoveryRegistration?> LoadAsync(CancellationToken token)
    {
        RequireLease();
        byte[] encrypted;
        try
        {
            ValidateFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            if (stream.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("Invalid discovery journal.");
            encrypted = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(encrypted, token).ConfigureAwait(false);
            fingerprint = SHA256.HashData(encrypted);
        }
        catch (FileNotFoundException) { return null; }
        var plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
        try
        {
            using var document = JsonDocument.Parse(plain, new JsonDocumentOptions { MaxDepth = 12 });
            ValidateJson(document.RootElement);
            return JsonSerializer.Deserialize<PrivateDiscoveryRegistration>(plain, Json) ?? throw new InvalidDataException("Invalid discovery journal.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task SaveAsync(PrivateDiscoveryRegistration registration, CancellationToken token)
    {
        RequireLease();
        var plain = JsonSerializer.SerializeToUtf8Bytes(registration, Json);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaximumBytes) throw new InvalidDataException("Discovery journal capacity reached.");
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".new";
        var created = false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new UnauthorizedAccessException();
            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            using (var stream = new FileInfo(pending).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough, security))
            {
                created = true;
                await stream.WriteAsync(encrypted, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(true);
            }
            RequireLease();
            if (File.Exists(path))
            {
                ValidateFile(path);
                using var current = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (current.Length > MaximumBytes || fingerprint is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), fingerprint))
                    throw new InvalidDataException("Discovery journal changed outside its owner.");
            }
            else if (fingerprint is not null) throw new InvalidDataException("Discovery journal was removed outside its owner.");
            File.Move(pending, path, true);
            fingerprint = SHA256.HashData(encrypted);
        }
        finally { if (created && File.Exists(pending)) File.Delete(pending); }
    }
    private void RequireLease() { if (!ownsLease()) throw new DashboardOwnershipException(); }
    private static void ValidateJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Invalid discovery journal properties.");
                ValidateJson(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateJson(item);
    }
    private static void ValidateFile(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Discovery journal must not be a link.");
        var security = info.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException();
        if (!user.Equals(security.GetOwner(typeof(SecurityIdentifier))) || !security.AreAccessRulesProtected ||
            security.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && !user.Equals(rule.IdentityReference)))
            throw new UnauthorizedAccessException("Discovery journal ownership or permissions changed.");
    }
}