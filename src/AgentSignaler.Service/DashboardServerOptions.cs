namespace AgentSignaler.Service;

public enum DashboardListenerMode
{
    Lan,
    Internet
}

/// <summary>
/// Internet binds IPv4/IPv6 localhost only. Its global, identity-independent token bucket
/// permits 200 requests initially and replenishes 100 each second, with at most 100
/// executing requests and no queue. LAN retains its existing binding and request behavior.
/// Limits may be reduced, but not disabled or raised above these bounds.
/// </summary>
public sealed record DashboardServerOptions
{
    public DashboardListenerMode ListenerMode { get; init; } = DashboardListenerMode.Lan;
    public int RequestBurstLimit { get; init; } = 200;
    public int RequestsPerSecond { get; init; } = 100;
    public int ConcurrentRequestLimit { get; init; } = 100;

    internal void Validate()
    {
        if (!Enum.IsDefined(ListenerMode)) throw new ArgumentOutOfRangeException(nameof(ListenerMode));
        if (RequestBurstLimit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(RequestBurstLimit));
        if (RequestsPerSecond is < 1 or > 100 || RequestsPerSecond > RequestBurstLimit)
            throw new ArgumentOutOfRangeException(nameof(RequestsPerSecond));
        if (ConcurrentRequestLimit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(ConcurrentRequestLimit));
    }
}
