using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts.Rpc.V1;

/// <summary>An independently versioned current domain snapshot, never a historical capture.</summary>
public sealed record RpcSnapshot<T>(int ProtocolVersion, string HostInstanceId, string Domain, string Revision, T State,
    bool IsStale = false);

/// <summary>A lightweight domain invalidation containing no replacement state.</summary>
public sealed record RpcInvalidation(string HostInstanceId, string Domain, string Revision, string? MachineId = null);

/// <summary>A stateless page against a current domain or entity revision.</summary>
public sealed record RpcPage<T>(IReadOnlyList<T> Items, int Offset, int Limit,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? NextOffset, int TotalCount);

/// <summary>A bounded chunk of a note, including unmodified legacy notes.</summary>
public sealed record RpcNoteChunk(string Text, int Offset,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? NextOffset, int TotalLength);

/// <summary>Stable sanitized error context; cancellation never implies rollback.</summary>
public sealed record RpcErrorData(string? Field = null, string? Action = null, bool Retryable = false,
    string CommitState = "notCommitted", string? Recovery = null);

/// <summary>The single stdout transport-readiness record, not receiver readiness.</summary>
public sealed record RpcTransportReady(string State, int RpcPort, int ProtocolVersion, string HostInstanceId);

/// <summary>Cancellation admission outcome, independent of operation completion.</summary>
public sealed record RpcCancelResult(string Status);

/// <summary>Acknowledges a shutdown request before transport closure.</summary>
public sealed record RpcShutdownResult(string State);

/// <summary>Protocol features and fixed limits.</summary>
public sealed record RpcCapabilities(int ProtocolVersion, string HostInstanceId, IReadOnlyList<string> Methods,
    RpcLimitProfile Limits, string Authentication, string StateSynchronization);

/// <summary>Immutable protocol-v1 resource limits.</summary>
public sealed record RpcLimitProfile(int InboundBytes, int OutboundBytes, int MaximumDepth,
    int PropertiesPerObject, int BatchEntries, int IdLength, int InputStringLength,
    int NoteLength, int DefaultPageSize, int MaximumPageSize, int ApplicationSlots,
    int ControlSlots, int QueueMessages, int QueueBytes);
