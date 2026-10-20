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
            var monotonic = lifetime - clock.GetElapsedTime(timestamp);
            if (monotonic < remaining) remaining = monotonic;
        }
        return remaining;
    }

    public static EffectiveEndpointSnapshot Direct(RemoteConfiguration configuration) =>
        new(configuration.BaseUri, 0, DateTimeOffset.MaxValue);
}