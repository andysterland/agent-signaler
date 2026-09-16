using AgentSignaler.Contracts;

namespace AgentSignaler.Service;

public sealed record MachineView(Guid MachineId, string MachineName, string? DisplayName,
    string Client, string ClientVersion, AgentState State, AgentEvent? LatestEvent,
    DateTimeOffset LastContactUtc, IReadOnlyList<SessionSnapshot> Sessions,
    WindowsAppConnection? WindowsAppConnection = null)
{
    public string? Note { get; init; }
    public PresenceMode PresenceMode { get; init; }
    public int? HeartbeatIntervalSeconds { get; init; }
    public long Generation { get; init; }
    public long Sequence { get; init; }
    public bool ExplicitOffline { get; init; }
    public DateTimeOffset OfflineDeadlineUtc { get; init; }
    public DateTimeOffset? LatestEventUtc { get; init; }
    public string Name => string.IsNullOrWhiteSpace(DisplayName) ? MachineName : DisplayName;
}
