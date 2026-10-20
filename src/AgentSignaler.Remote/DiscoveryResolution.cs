namespace AgentSignaler.Remote;

public sealed record DiscoveryResolution(EffectiveEndpointSnapshot? Snapshot, string Status, DateTimeOffset NextAttemptUtc);

public interface IDiscoveryEndpointResolver : IAsyncDisposable
{
    Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default);
    Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default);
}

public sealed record DiscoveryRevisionCheckpoint(long Revision, string PayloadFingerprint);

public interface IDiscoveryRevisionStore
{
    DiscoveryRevisionCheckpoint? Read(Guid discoveryId);
    void Write(Guid discoveryId, DiscoveryRevisionCheckpoint checkpoint);
}