using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using AgentSignaler.Contracts;

namespace AgentSignaler.Discovery;

public interface IDiscoveryReplayStore
{
    DiscoveryReplayState? Load(Guid discoveryId);
    void Save(DiscoveryReplayState state);
}

/// <summary>Persists monotonic authenticated publication checkpoints as current-user protected ciphertext.</summary>
[SupportedOSPlatform("windows")]
public sealed class CurrentUserDiscoveryReplayStore(string directory) : IDiscoveryReplayStore
{
    private readonly CurrentUserDiscoveryCredentialStore store = new(directory);
    public DiscoveryReplayState? Load(Guid discoveryId)
    {
        try { return store.LoadReplay(discoveryId); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new DiscoveryException("discovery_replay_read"); }
    }

    /// <summary>Reads an authenticated checkpoint without creating directories or promoting staged recovery files.</summary>
    public DiscoveryReplayState? LoadReadOnly(Guid discoveryId)
    {
        try { return store.LoadReplay(discoveryId, readOnly: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new DiscoveryException("discovery_replay_read"); }
    }

    public void Save(DiscoveryReplayState state)
    {
        try { store.SaveReplay(state); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new DiscoveryException("discovery_replay_write"); }
    }

    internal static void Validate(DiscoveryReplayState state)
    {
        if (state is null || state.DiscoveryId == Guid.Empty || state.Revision <= 0 || state.PayloadFingerprint is not { Length: 64 } ||
            state.PayloadFingerprint.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DiscoveryException("discovery_replay_fields");
    }

    internal static byte[] Encode(DiscoveryReplayState state)
    {
        var bytes = new byte[92];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, DiscoveryProtocol.Version);
        state.DiscoveryId.TryWriteBytes(bytes.AsSpan(4, 16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(20), state.Revision);
        Encoding.ASCII.GetBytes(state.PayloadFingerprint, bytes.AsSpan(28));
        return bytes;
    }

    internal static DiscoveryReplayState Decode(byte[] bytes, Guid discoveryId)
    {
        if (bytes.Length != 92 || BinaryPrimitives.ReadInt32LittleEndian(bytes) != DiscoveryProtocol.Version ||
            new Guid(bytes.AsSpan(4, 16)) != discoveryId)
            throw new DiscoveryException("discovery_replay_state");
        var state = new DiscoveryReplayState(discoveryId, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(20)),
            Encoding.ASCII.GetString(bytes.AsSpan(28)));
        Validate(state);
        return state;
    }
}
