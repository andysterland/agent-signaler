using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignaler.Tunneling;

/// <summary>Atomic, non-secret ownership persistence, separate from dashboard preferences.</summary>
public sealed class TunnelIdentityStore
{
    private const int MaxBytes = 4096;
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 4
    };

    public TunnelIdentityStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<TunnelIdentity> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
            {
                var created = new TunnelIdentity("", Guid.NewGuid().ToString("N"));
                await WriteCoreAsync(created, overwrite: false, cancellationToken).ConfigureAwait(false);
                return created;
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxBytes) throw InvalidState();
            var bytes = new byte[MaxBytes + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            if (count > MaxBytes) throw InvalidState();
            try
            {
                using var document = TunnelValidation.Parse(System.Text.Encoding.UTF8.GetString(bytes, 0, count));
                var stored = document.RootElement.Deserialize<StoredIdentity>(Json);
                if (stored is null || stored.Version != 1 || stored.Identity is null) throw InvalidState();
                Validate(stored.Identity);
                return stored.Identity;
            }
            catch (JsonException) { throw InvalidState(); }
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(TunnelIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Validate(identity);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteCoreAsync(identity, overwrite: true, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task WriteCoreAsync(TunnelIdentity identity, bool overwrite, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(1, identity), Json);
        if (bytes.Length > MaxBytes) throw InvalidState();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var stagingPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.pending");
        try
        {
            await using (var file = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(stagingPath, path, overwrite);
        }
        finally
        {
            if (File.Exists(stagingPath)) File.Delete(stagingPath);
        }
    }

    private static void Validate(TunnelIdentity identity)
    {
        if (identity.OwnerHash is null ||
            (identity.OwnerHash.Length != 0 && (identity.OwnerHash.Length != 64 ||
                identity.OwnerHash.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))) ||
            !Guid.TryParseExact(identity.InstallationMarker, "N", out var marker) || marker == Guid.Empty ||
            (identity.OwnerHash.Length == 0 && (identity.TunnelId is not null || identity.PendingTunnelId is not null)))
            throw InvalidState();
        if (identity.TunnelId is { } id) TunnelValidation.ValidateId(id);
        if (identity.PendingTunnelId is { } pending)
        {
            TunnelValidation.ValidateId(pending, full: false);
            if (identity.TunnelId is { } full && !full.StartsWith(pending + ".", StringComparison.Ordinal))
                throw InvalidState();
        }
    }

    private static TunnelException InvalidState() => new(
        "Tunnel ownership state is invalid or unsupported. Preserve the file for recovery; do not silently reset ownership.");

    private sealed record StoredIdentity(int Version, TunnelIdentity Identity);
}
