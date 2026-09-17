namespace AgentSignaler.Tunneling;

public enum TunnelOperationOutcome { Succeeded, Cancelled, TimedOut, Failed }
public enum TunnelCommitState { NotCommitted, Committed, Unknown }
public enum TunnelOperationFailure { Operation, Persistence }
public sealed record TunnelOperationResult(TunnelOperationOutcome Outcome, TunnelCommitState CommitState,
    TunnelStatus Status, TunnelOperationFailure Failure = TunnelOperationFailure.Operation);
