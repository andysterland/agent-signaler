using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts;

public sealed record SessionSnapshot
{
    public string SessionId { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceDescriptor? Source { get; init; }
    public AgentState UnderlyingState { get; init; } = AgentState.Idle;
    public AgentState? ResultState { get; init; }
    public DateTimeOffset? ResultUntilUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AwaitingUserInput { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public static class StateReducer
{
    public static SessionSnapshot Apply(SessionSnapshot? previous, string sessionId,
        AgentEvent kind, DateTimeOffset reportedAtUtc, DateTimeOffset now, bool toolFailed = false,
        bool toolRequiresUserInput = false, SourceDescriptor? source = null)
    {
        if (previous is not null && reportedAtUtc <= previous.UpdatedAtUtc) return previous;
        var awaitingUserInput = kind switch
        {
            AgentEvent.PreToolUse when toolRequiresUserInput => true,
            AgentEvent.PostToolUse or AgentEvent.PostToolUseFailure when toolRequiresUserInput => false,
            AgentEvent.SessionStart or AgentEvent.SessionEnd or AgentEvent.AgentStop or
                AgentEvent.ExecutionStopped or AgentEvent.ErrorOccurred => false,
            _ => previous?.AwaitingUserInput ?? false
        };
        var underlying = kind switch
        {
            AgentEvent.SessionStart or AgentEvent.PermissionRequest or AgentEvent.AgentStop or
                AgentEvent.ErrorOccurred or AgentEvent.PostToolUseFailure or AgentEvent.ExecutionStopped => AgentState.Waiting,
            AgentEvent.UserPromptSubmitted or AgentEvent.PreToolUse => AgentState.Executing,
            AgentEvent.PostToolUse => toolFailed ? AgentState.Waiting : AgentState.Executing,
            AgentEvent.SessionEnd => AgentState.Idle,
            _ => previous?.UnderlyingState ?? AgentState.Idle
        };
        if (awaitingUserInput) underlying = AgentState.Waiting;
        AgentState? result = kind switch
        {
            AgentEvent.AgentStop => AgentState.Succeeded,
            AgentEvent.ErrorOccurred or AgentEvent.PostToolUseFailure => AgentState.Failed,
            AgentEvent.PostToolUse when toolFailed => AgentState.Failed,
            _ => null
        };
        // Keep the original result expiry even when a pending question takes display priority.
        var until = result.HasValue ? now + Protocol.ResultDuration : previous?.ResultUntilUtc;
        result ??= previous?.ResultState;
        if (kind == AgentEvent.ExecutionStopped) { result = null; until = null; }
        if (until <= now) { result = null; until = null; }
        return new SessionSnapshot
        {
            SessionId = sessionId, Source = source ?? previous?.Source, UnderlyingState = underlying,
            AwaitingUserInput = awaitingUserInput,
            ResultState = result, ResultUntilUtc = until, UpdatedAtUtc = reportedAtUtc
        };
    }

    public static AgentState Effective(SessionSnapshot session, DateTimeOffset now) =>
        session.ResultState is { } result && session.ResultUntilUtc > now &&
        (result == AgentState.Failed || !session.AwaitingUserInput) ? result : session.UnderlyingState;

    public static AgentState Aggregate(IEnumerable<SessionSnapshot> sessions, DateTimeOffset now) =>
        sessions.Select(s => Effective(s, now)).DefaultIfEmpty(AgentState.Idle).MaxBy(Priority);

    public static int Priority(AgentState state) => state switch
    {
        AgentState.Failed => 5, AgentState.Waiting => 4, AgentState.Executing => 3,
        AgentState.Succeeded => 2, AgentState.Idle => 1, _ => 0
    };
}
