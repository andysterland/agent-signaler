using System.Security.Cryptography;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;

namespace AgentSignaler.Remote;

public sealed record DiscoveryCredentialUpdate(DiscoveryCredentialReference Reference, byte[] Ciphertext)
{
    public void Validate()
    {
        if (Ciphertext is not { Length: > 0 and <= 24576 } || Reference is null ||
            !string.Equals(Convert.ToHexStringLower(SHA256.HashData(Ciphertext)), Reference.CiphertextHash, StringComparison.Ordinal))
            throw new InvalidDataException("Protected discovery credential changed since import. Import it again.");
    }

    public override string ToString() => "Protected discovery credential update";
}

internal sealed record DiscoveryCredentialChange(DiscoveryCredentialReference Reference, byte[]? Before, byte[]? After);

public interface IDiscoveryCredentialTransactionStore
{
    byte[]? Read(DiscoveryCredentialReference reference);
    void Write(DiscoveryCredentialReference reference, byte[] ciphertext);
    void Delete(DiscoveryCredentialReference reference);
}

public sealed class DiscoveryCredentialTransactionStore : IDiscoveryCredentialTransactionStore
{
    private readonly string directory;
    private readonly CurrentUserDiscoveryCredentialStore store;

    public DiscoveryCredentialTransactionStore(string configPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        directory = DirectoryPath(configPath);
        store = new(directory);
    }

    public static string DirectoryPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "discovery-credentials");

    public static DiscoveryCredentialUpdate Prepare(DiscoveryConnectionPackage package)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var prepared = CurrentUserDiscoveryCredentialStore.Prepare(package);
        return new(prepared.Reference, prepared.Ciphertext);
    }

    public byte[]? Read(DiscoveryCredentialReference reference)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidateReference(reference);
        if (!File.Exists(Path.Combine(directory, reference.Id + ".bin"))) return null;
        return store.ReadCiphertextAsync(reference).GetAwaiter().GetResult();
    }

    public void Write(DiscoveryCredentialReference reference, byte[] ciphertext)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidateReference(reference);
        store.WritePreparedAsync(new(reference, ciphertext)).GetAwaiter().GetResult();
    }

    public void Delete(DiscoveryCredentialReference reference)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidateReference(reference);
        store.DeleteAsync(reference).GetAwaiter().GetResult();
    }

    internal static void ValidateReference(DiscoveryCredentialReference reference)
    {
        if (reference is null || !Guid.TryParseExact(reference.Id, "N", out var id) || id == Guid.Empty || reference.Id != id.ToString("N") ||
            reference.CiphertextHash is not { Length: 64 } || reference.CiphertextHash.Any(c => c is not (>= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new InvalidDataException("Invalid protected discovery credential reference.");
    }
}
