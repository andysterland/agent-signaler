using System.Collections.Immutable;

namespace AgentSignaler.Contracts;

public enum TranscriptResetReason { None, Cleared, Removed, Disabled, Unavailable, ReceiverReset, Expired, Evicted, CursorExpired, InvalidCursor, SelectionChanged }
public enum TranscriptAvailability { Available, NoEvents, Disabled, Unavailable, Partial, Expired }

/// <summary>Full identity of one retained session; versions are descriptive, not identity.</summary>
public sealed record TranscriptSelection(Guid MachineId, SourceDescriptor Source, string SessionId, Guid StreamId);
/// <summary>Current retained delivery interval, not a promise of complete observation.</summary>
public sealed record TranscriptRetainedRange(long? FirstSequence, long? LastSequence);
/// <summary>Content-free bounded selector row, including ended sessions with retained events.</summary>
public sealed record TranscriptSessionInfo(TranscriptSelection Selection, DateTimeOffset LastReceivedAtUtc,
    bool Closed, TranscriptRetainedRange RetainedRange, bool HasGaps, bool HasTruncation);
/// <summary>A bounded session selector response.</summary>
public sealed record TranscriptSessionsPage(Guid ReceiverEpoch, long ChangeCounter, long InvalidationGeneration,
    ImmutableArray<TranscriptSessionInfo> Sessions, string? ContinuationCursor,
    TranscriptAvailability Availability, TranscriptResetReason ResetReason, TranscriptFieldCapabilities Capabilities);
/// <summary>An immutable decoded event with a fixed receipt-based deadline.</summary>
public sealed record TranscriptReadEvent(TranscriptEvent Event, DateTimeOffset ReceivedAtUtc, DateTimeOffset ExpiresAtUtc, int SerializedBytes);
/// <summary>A bounded, in-process page; appends preserve its cursor and selection generation.</summary>
public sealed record TranscriptEventsPage(Guid ReceiverEpoch, TranscriptSelection Selection, long ChangeCounter,
    long InvalidationGeneration, TranscriptRetainedRange RetainedRange, ImmutableArray<TranscriptReadEvent> Events,
    string? ContinuationCursor, TranscriptAvailability Availability, TranscriptResetReason ResetReason,
    bool HasGaps, bool HasTruncation, TranscriptFieldCapabilities Capabilities, TranscriptGap? Gap = null,
    string? PreviousCursor = null);
/// <summary>Immediate content-free invalidation; null machine applies to every selection.</summary>
public sealed record TranscriptInvalidation(Guid ReceiverEpoch, Guid? MachineId, TranscriptSelection? Selection,
    long InvalidationGeneration, TranscriptResetReason Reason);

/// <summary>Bounded memory-only reads; never reads host files or starts reporting.</summary>
public interface ITranscriptReader
{
    event EventHandler<TranscriptInvalidation>? Invalidated;
    bool IsCurrent(TranscriptEventsPage page);
    bool IsCurrent(TranscriptSelection selection, Guid receiverEpoch, long invalidationGeneration, TranscriptRetainedRange displayedRange);
    ValueTask<TranscriptSessionsPage> ListSessionsAsync(Guid machineId, string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<TranscriptEventsPage> ReadEventsAsync(TranscriptSelection selection, string? cursor = null, CancellationToken cancellationToken = default);
    ValueTask<TranscriptEventsPage> ReadLatestEventsAsync(TranscriptSelection selection, CancellationToken cancellationToken = default);
    ValueTask<TranscriptEventsPage> ReadEventsBeforeAsync(TranscriptSelection selection, string cursor, CancellationToken cancellationToken = default);
}
