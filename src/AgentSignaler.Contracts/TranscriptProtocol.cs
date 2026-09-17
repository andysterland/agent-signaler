using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts;

public enum CaptureOrigin { Hook, TranscriptFile }
public enum TranscriptOrdering { Arrival, Host }
public enum TranscriptRole { User, Assistant }
public enum TranscriptMessageIdOrigin { Host, Local }
public enum TranscriptMessageAvailability { Complete, Partial }
public enum TranscriptTruncationReason { None, SerializedLimit }
public enum TranscriptToolPhase { Requested, Completed }
public enum TranscriptToolOutcome { Requested, Completed, Failed, Unknown }
public enum TranscriptLifecycleSignal { SessionStarted, SessionEnded, StopObserved }
public enum TranscriptGapReason
{
    CaptureStarted, QueueOverflow, Expired, Disconnected, ReceiverReset, Unobserved,
    FileUnavailable, FormatUnverified, FormatChanged, BaselineEstablished, ReadBudgetExceeded,
    FileReset, Capacity, PartialWrite
}
public enum TranscriptDisposition { Accepted, Duplicate, Rejected }
public enum TranscriptRejection
{
    None, InvalidSchema, Oversized, ContentType, Disabled, Unavailable, EpochReset,
    OpenRevisionConflict, RetiredStream, UnknownManagedMachine, SequenceConflict, Capacity, RateLimited, Timeout
}

/// <summary>Allowlisted observation provenance; never contains a local file reference.</summary>
public sealed record TranscriptProvenance(
    string HostEventName, string AdapterVersion, DateTimeOffset ObservedAtUtc,
    DateTimeOffset AcceptedAtUtc, TranscriptOrdering Ordering, CaptureOrigin CaptureOrigin,
    string FormatProfileId, Guid? TriggeringCaptureId = null, string? TurnId = null);

/// <summary>A closed, event-specific union of permitted transcript fields.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TranscriptMessage), "message")]
[JsonDerivedType(typeof(TranscriptToolActivity), "tool")]
[JsonDerivedType(typeof(TranscriptLifecycle), "lifecycle")]
[JsonDerivedType(typeof(TranscriptGap), "gap")]
public abstract record TranscriptPayload;

/// <summary>Plain user or completed available main-assistant text.</summary>
public sealed record TranscriptMessage(TranscriptRole Role, string MessageId,
    TranscriptMessageIdOrigin MessageIdOrigin, string Text,
    TranscriptMessageAvailability Availability = TranscriptMessageAvailability.Complete,
    bool Truncated = false, TranscriptTruncationReason TruncationReason = TranscriptTruncationReason.None) : TranscriptPayload;

/// <summary>Tool name and observed lifecycle only; no arguments, output or raw errors.</summary>
public sealed record TranscriptToolActivity(string ToolName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InvocationId,
    TranscriptToolPhase Phase, TranscriptToolOutcome Outcome) : TranscriptPayload;

/// <summary>A literal supported lifecycle observation, not inferred turn success.</summary>
public sealed record TranscriptLifecycle(TranscriptLifecycleSignal Signal) : TranscriptPayload;

/// <summary>A known missing interval or an explicit unknown-range discontinuity.</summary>
public sealed record TranscriptGap(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? FromSequence,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? ThroughSequence, TranscriptGapReason Reason) : TranscriptPayload;

/// <summary>A separately versioned volatile transcript delivery, ordered within its source stream.</summary>
public sealed record TranscriptEvent
{
    [JsonRequired] public int ProtocolVersion { get; init; } = TranscriptProtocol.Version;
    [JsonRequired] public Guid EventId { get; init; }
    [JsonRequired] public Guid MachineId { get; init; }
    [JsonRequired] public long Generation { get; init; }
    [JsonRequired] public Guid ReceiverEpoch { get; init; }
    [JsonRequired] public Guid StreamId { get; init; }
    [JsonRequired] public long Sequence { get; init; }
    [JsonRequired] public SourceDescriptor Source { get; init; } = null!;
    [JsonRequired] public string SessionId { get; init; } = "";
    [JsonRequired] public TranscriptProvenance Provenance { get; init; } = null!;
    [JsonRequired] public TranscriptPayload Payload { get; init; } = null!;
}

/// <summary>Content-free receiver negotiation.</summary>
public sealed record TranscriptCapabilitiesRequest(int ProtocolVersion, Guid MachineId);
/// <summary>An idempotent compare-and-swap source-stream open request.</summary>
public sealed record TranscriptOpenRequest(int ProtocolVersion, Guid MachineId, long Generation,
    Guid ReceiverEpoch, Guid StreamId, SourceDescriptor Source, long ExpectedOpenRevision, Guid OpenId);
/// <summary>A stream close, optionally dropping its retained content.</summary>
public sealed record TranscriptCloseRequest(int ProtocolVersion, Guid MachineId, long Generation,
    Guid ReceiverEpoch, Guid StreamId, bool Purge);
/// <summary>Implemented fields, not a claim of any host's verified capture coverage.</summary>
public sealed record TranscriptFieldCapabilities(bool UserMessages, bool MainAssistantCompleteMessages,
    bool ToolNames, bool ToolCorrelation, bool SessionStarted, bool SessionEnded, bool StopObserved);
/// <summary>Fixed receiver ceilings advertised without conversation content.</summary>
public sealed record TranscriptLimits(int EventBytes, int ControlBytes, int RetentionSeconds,
    int EventPageCount, int EventPageBytes);
/// <summary>Receiver-wide epoch/revision and current reception availability.</summary>
public sealed record TranscriptCapabilitiesResponse(int ProtocolVersion, Guid ReceiverEpoch,
    long OpenRevision, bool Enabled, bool Ready, TranscriptLimits Limits, TranscriptFieldCapabilities Capabilities);
/// <summary>Confirms volatile stream opening; the revision is receiver-wide.</summary>
public sealed record TranscriptOpenResponse(Guid ReceiverEpoch, Guid StreamId, long OpenRevision, bool Duplicate);
/// <summary>Confirms a stream was closed or already closed.</summary>
public sealed record TranscriptCloseResponse(Guid ReceiverEpoch, Guid StreamId, long OpenRevision, bool Duplicate);
/// <summary>Volatile receipt only, never a persistence or completeness guarantee.</summary>
public sealed record TranscriptAcknowledgement(Guid ReceiverEpoch, Guid StreamId, long AcknowledgedSequence,
    TranscriptDisposition Disposition);
/// <summary>A content-free, application-defined rejection category.</summary>
public sealed record TranscriptError(TranscriptRejection Category, Guid ReceiverEpoch, long OpenRevision);

public static class TranscriptProtocol
{
    public const int Version = 1;
    public const int MaxEventBytes = 32_768;
    public const int MaxControlBytes = 4_096;
    public const string CapabilitiesPath = "/api/transcripts/v1/capabilities";
    public const string OpenPath = "/api/transcripts/v1/streams/open";
    public const string EventsPath = "/api/transcripts/v1/events";
    public const string ClosePath = "/api/transcripts/v1/streams/close";
    public static TranscriptFieldCapabilities ImplementedCapabilities { get; } = new(true, true, true, true, true, true, true);
    public static TranscriptLimits Limits { get; } = new(MaxEventBytes, MaxControlBytes, 1800, 32, 128 * 1024);
    public static JsonSerializerOptions Json { get; } = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            RespectRequiredConstructorParameters = true,
            MaxDepth = 16,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static bool Validate(TranscriptCapabilitiesRequest value) =>
        value.ProtocolVersion == Version && value.MachineId != Guid.Empty;
    public static bool Validate(TranscriptOpenRequest value) =>
        value.ProtocolVersion == Version && value.MachineId != Guid.Empty && value.Generation > 0 &&
        value.ReceiverEpoch != Guid.Empty && value.StreamId != Guid.Empty && ValidSource(value.Source) &&
        value.ExpectedOpenRevision >= 0 && value.OpenId != Guid.Empty;
    public static bool Validate(TranscriptCloseRequest value) =>
        value.ProtocolVersion == Version && value.MachineId != Guid.Empty && value.Generation > 0 &&
        value.ReceiverEpoch != Guid.Empty && value.StreamId != Guid.Empty;

    public static bool Validate(TranscriptEvent value)
    {
        if (value.ProtocolVersion != Version || value.EventId == Guid.Empty || value.MachineId == Guid.Empty ||
            value.Generation <= 0 || value.ReceiverEpoch == Guid.Empty || value.StreamId == Guid.Empty ||
            value.Sequence <= 0 || value.Sequence == long.MaxValue || !ValidSource(value.Source) ||
            !ValidIdentifier(value.SessionId, 128) || value.Provenance is not { } provenance ||
            !ValidIdentifier(provenance.HostEventName, 64) || !ValidIdentifier(provenance.AdapterVersion, 64) ||
            !ValidIdentifier(provenance.FormatProfileId, 64) || !IsUtc(provenance.ObservedAtUtc) ||
            !IsUtc(provenance.AcceptedAtUtc) || !Enum.IsDefined(provenance.Ordering) ||
            !Enum.IsDefined(provenance.CaptureOrigin) || provenance.TriggeringCaptureId == Guid.Empty ||
            provenance.TurnId is { } turn && !ValidIdentifier(turn, 128))
            return false;
        if (provenance.CaptureOrigin == CaptureOrigin.TranscriptFile &&
            (provenance.TriggeringCaptureId is null || value.Payload is not TranscriptMessage { Role: TranscriptRole.Assistant }))
            return false;
        return value.Payload switch
        {
            TranscriptMessage message => Enum.IsDefined(message.Role) && Enum.IsDefined(message.MessageIdOrigin) &&
                Enum.IsDefined(message.Availability) && Enum.IsDefined(message.TruncationReason) &&
                ValidIdentifier(message.MessageId, 128) && !string.IsNullOrEmpty(message.Text) &&
                message.Text.Length <= MaxEventBytes && ValidUnicode(message.Text) &&
                message.Truncated == (message.TruncationReason != TranscriptTruncationReason.None) &&
                (message.Role != TranscriptRole.User || provenance.CaptureOrigin == CaptureOrigin.Hook) &&
                (message.Role != TranscriptRole.Assistant || message.Availability == TranscriptMessageAvailability.Complete),
            TranscriptToolActivity tool => ValidIdentifier(tool.ToolName, 128) &&
                (tool.InvocationId is null || ValidIdentifier(tool.InvocationId, 128)) &&
                Enum.IsDefined(tool.Phase) && Enum.IsDefined(tool.Outcome) &&
                (tool.Phase == TranscriptToolPhase.Requested ? tool.Outcome == TranscriptToolOutcome.Requested :
                    tool.Outcome != TranscriptToolOutcome.Requested),
            TranscriptLifecycle lifecycle => Enum.IsDefined(lifecycle.Signal),
            TranscriptGap gap => Enum.IsDefined(gap.Reason) &&
                (gap.FromSequence is null && gap.ThroughSequence is null ||
                 gap.FromSequence is > 0 && gap.ThroughSequence >= gap.FromSequence && gap.ThroughSequence == value.Sequence),
            _ => false
        };
    }

    public static int AccountedEventBytes(int serializedBytes) => checked((serializedBytes + 1024 + 255) / 256 * 256);
    private static bool ValidSource(SourceDescriptor? source) => source is { IsValid: true } &&
        ValidIdentifier(source.ScopeId, 128) && ValidIdentifier(source.Version, 64);
    public static bool ValidIdentifier(string? value, int max) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl) && ValidUnicode(value);
    private static bool IsUtc(DateTimeOffset value) => value.Year is >= 1970 and <= 9998 && value.Offset == TimeSpan.Zero;
    private static bool ValidUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) continue;
            if (!char.IsHighSurrogate(value[index]) || ++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }
}
