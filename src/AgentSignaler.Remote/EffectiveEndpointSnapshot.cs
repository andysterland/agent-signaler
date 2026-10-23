namespace AgentSignaler.Remote;

public sealed record EffectiveEndpointSnapshot(Uri BaseUri, long EndpointRevision, DateTimeOffset ValidUntilUtc)
{
    public long? VerifiedTimestamp { get; init; }
    public TimeSpan? CacheLifetime { get; init; }

    public bool IsValid(TimeProvider clock) => RemainingLifetime(clock) > TimeSpan.Zero;

    public TimeSpan RemainingLifetime(TimeProvider clock)
    {
        var remaining = ValidUntilUtc - clock.GetUtcNow();
        if (VerifiedTimestamp is { } timestamp && CacheLifetime is { } lifetime)
        {
            remaining = lifetime - clock.GetElapsedTime(timestamp);
        }
        return remaining;
    }

    public static EffectiveEndpointSnapshot Direct(RemoteConfiguration configuration) =>
        new(configuration.BaseUri, 0, DateTimeOffset.MaxValue);
}