using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts;

public enum PresenceKind { Started = 1, Heartbeat, Hook, Offline }
public enum PresenceMode { Legacy, Managed }

/// <summary>A sanitized report from a single managed reporting run.</summary>
public sealed record PresenceReport
{
    [JsonRequired] public int ProtocolVersion { get; init; } = PresenceProtocol.Version;
    [JsonRequired] public PresenceKind Kind { get; init; }
    [JsonRequired] public Guid EventId { get; init; }
    [JsonRequired] public Guid MachineId { get; init; }
    [JsonRequired] public string MachineName { get; init; } = "";
    [JsonRequired] public string Client { get; init; } = "copilot-cli";
    [JsonRequired] public string ClientVersion { get; init; } = "";
    [JsonRequired] public long Generation { get; init; }
    [JsonRequired] public long Sequence { get; init; }
    [JsonRequired] public DateTimeOffset ReportedAtUtc { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HeartbeatIntervalSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SessionSnapshot>? Sessions { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StatusRequest? Hook { get; init; }
}

/// <summary>Confirms that a report was committed, or was already superseded.</summary>
public sealed record PresenceResponse(bool Duplicate);

/// <summary>Advertises managed presence support without changing legacy health.</summary>
public sealed record PresenceHealthResponse(int ProtocolVersion, string Status);

public static class PresenceProtocol
{
    public const int Version = 2;
    public const int SourceVersion = 3;
    public const int EnrichedVersion = 4;
    public const int DefaultHeartbeatIntervalSeconds = 300;
    public const int MinHeartbeatIntervalSeconds = 60;
    public const int MaxHeartbeatIntervalSeconds = 3600;
    private static readonly JsonSerializerOptions PayloadJson = new(Protocol.Json) { AllowDuplicateProperties = false };
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(PayloadJson);
        options.Converters.Add(new PresenceReportConverter());
        return options;
    }

    private sealed class PresenceReportConverter : JsonConverter<PresenceReport>
    {
        public override PresenceReport? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            RejectDuplicateProperties(document.RootElement);
            var report = document.RootElement.Deserialize<PresenceReport>(PayloadJson);
            if (report is null) return null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var snapshotField = property.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
                    property.Name.Equals("heartbeatIntervalSeconds", StringComparison.OrdinalIgnoreCase);
                var hookField = property.Name.Equals("hook", StringComparison.OrdinalIgnoreCase);
                if (snapshotField && report.Kind is not (PresenceKind.Started or PresenceKind.Heartbeat) ||
                    hookField && report.Kind != PresenceKind.Hook)
                    throw new JsonException("Property is not allowed for this report kind.");
                if (report.ProtocolVersion is not (SourceVersion or EnrichedVersion) &&
                    (property.Name.Equals("hook", StringComparison.OrdinalIgnoreCase) && HasSource(property.Value) ||
                     property.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) &&
                     property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().Any(HasSource)))
                    throw new JsonException("Source fields require presence v3.");
                if (report.ProtocolVersion != EnrichedVersion &&
                    property.Name.Equals("sessions", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Array && property.Value.EnumerateArray().Any(HasLatestEvent))
                    throw new JsonException("Latest event fields require presence v4.");
            }
            return report;
        }

        public override void Write(Utf8JsonWriter writer, PresenceReport value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, PayloadJson);

        private static bool HasSource(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Any(p => p.Name.Equals("source", StringComparison.OrdinalIgnoreCase));

        private static void RejectDuplicateProperties(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!names.Add(property.Name)) throw new JsonException("Duplicate property.");
                    RejectDuplicateProperties(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }

        private static bool HasLatestEvent(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Any(p => p.Name.Equals("latestEvent", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals("latestEventAtUtc", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsValidHeartbeatInterval(int seconds) =>
        seconds is >= MinHeartbeatIntervalSeconds and <= MaxHeartbeatIntervalSeconds && seconds % 60 == 0;

    public static TimeSpan OfflineAfter(int seconds)
    {
        if (!IsValidHeartbeatInterval(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        return TimeSpan.FromSeconds(2 * seconds + 60);
    }

    public static IReadOnlyList<string> Validate(PresenceReport report)
    {
        var errors = new List<string>();
        var sourceAware = report.ProtocolVersion is SourceVersion or EnrichedVersion;
        if (report.ProtocolVersion is not (Version or SourceVersion or EnrichedVersion)) errors.Add("Unsupported protocolVersion.");
        if (!Enum.IsDefined(report.Kind)) errors.Add("Unknown report kind.");
        if (report.EventId == Guid.Empty) errors.Add("eventId must be a nonempty UUID.");
        if (report.MachineId == Guid.Empty) errors.Add("machineId must be a nonempty UUID.");
        CheckText(report.MachineName, 128, "machineName", errors);
        CheckText(report.ClientVersion, 64, "clientVersion", errors);
        if (report.Client != (sourceAware ? "agent-signaler" : "copilot-cli"))
            errors.Add(sourceAware ? "client must be agent-signaler." : "client must be copilot-cli.");
        if (report.Generation <= 0) errors.Add("generation must be positive.");
        if (report.Sequence <= 0) errors.Add("sequence must be positive.");
        if (!IsUtc(report.ReportedAtUtc)) errors.Add("reportedAtUtc must be a UTC timestamp.");
        if (report.Kind is PresenceKind.Started or PresenceKind.Heartbeat)
        {
            if (report.HeartbeatIntervalSeconds is not { } interval || !IsValidHeartbeatInterval(interval))
                errors.Add("heartbeatIntervalSeconds must be a whole minute between 60 and 3600 seconds.");
            if (report.Sessions is null || report.Sessions.Count > Protocol.MaxSessions)
                errors.Add("sessions must be a snapshot containing at most 64 sessions.");
            else
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var session in report.Sessions)
                {
                    if (session is null) { errors.Add("Invalid session snapshot."); continue; }
                    CheckText(session.SessionId, 128, "sessionId", errors);
                    if (session.Source is { IsValid: false }) errors.Add("Invalid source descriptor.");
                    else if (session.SessionId is not null &&
                        !ids.Add(SourceIdentity.SessionKey(session.Source, session.SessionId)))
                        errors.Add("Duplicate session identity.");
                    if (!sourceAware && session.Source is not null) errors.Add("source requires presence v3.");
                    if (!ValidLatestEvent(session) ||
                        report.ProtocolVersion != EnrichedVersion && (session.LatestEvent is not null || session.LatestEventAtUtc is not null))
                        errors.Add("Invalid latest event metadata or unsupported presence version.");
                    if (session.UnderlyingState is not (AgentState.Idle or AgentState.Waiting or AgentState.Executing) ||
                        session.ResultState is not (null or AgentState.Succeeded or AgentState.Failed) ||
                        session.ResultState.HasValue != session.ResultUntilUtc.HasValue ||
                        (session.AwaitingUserInput && session.UnderlyingState != AgentState.Waiting) ||
                        !IsUtc(session.UpdatedAtUtc) ||
                        session.ResultUntilUtc is { } until && !IsUtc(until))
                        errors.Add("Invalid session state or UTC timestamp.");
                    if (session.UpdatedAtUtc > report.ReportedAtUtc)
                        errors.Add("Session updatedAtUtc must not exceed reportedAtUtc.");
                    if (session.ResultUntilUtc is { } expiry && expiry - session.UpdatedAtUtc > Protocol.ResultDuration)
                        errors.Add("Session result duration must not exceed 60 seconds.");
                }

            }
            if (report.Hook is not null) errors.Add("hook is not allowed for snapshots.");
        }
        else
        {
            if (report.HeartbeatIntervalSeconds is not null || report.Sessions is not null)
                errors.Add("Interval and sessions are only allowed for started and heartbeat.");
            if (report.Kind == PresenceKind.Hook)
            {
                if (report.Hook is not { } hook) errors.Add("hook is required.");
                else
                {
                    errors.AddRange(sourceAware ? Protocol.ValidateSource(hook) : Protocol.Validate(hook));
                    if (hook.EventId != report.EventId || hook.MachineId != report.MachineId ||
                        hook.MachineName != report.MachineName || hook.ReportedAtUtc != report.ReportedAtUtc ||
                        !sourceAware && (hook.Client != report.Client || hook.ClientVersion != report.ClientVersion))
                        errors.Add("Hook identity and timestamp must match the report.");
                }
            }
            else if (report.Hook is not null) errors.Add("hook is only allowed for hook reports.");
        }
        return errors;
    }

    public static bool ValidLatestEvent(SessionSnapshot session) =>
        session.LatestEvent.HasValue == session.LatestEventAtUtc.HasValue &&
        (session.LatestEvent is null || Enum.IsDefined(session.LatestEvent.Value)) &&
        (session.LatestEvent is null || (session.LatestEvent == AgentEvent.SessionEnd) == (session.UnderlyingState == AgentState.Idle)) &&
        (session.LatestEventAtUtc is not { } at || IsUtc(at) && at <= session.UpdatedAtUtc);

    public static SessionSnapshot ProjectStoredSession(SessionSnapshot session) => session with
    {
        LatestEvent = null, LatestEventAtUtc = null
    };

    public static SessionSnapshot ProjectSession(SessionSnapshot session, int version)
    {
        if (version is not (Protocol.Version or Version or SourceVersion or EnrichedVersion))
            throw new ArgumentOutOfRangeException(nameof(version));
        if (version < SourceVersion && session.Source is { } source &&
            SourceIdentity.SessionKey(source, "") != SourceIdentity.SessionKey(null, ""))
            throw new InvalidOperationException("Source-aware sessions require receiver v3 or newer. Upgrade the receiver first.");
        // Legacy readers cannot distinguish a permission wait from an ordinary waiting result.
        // Suppress only the wire overlay; do not manufacture a pending ask_user operation.
        var hideResult = version < EnrichedVersion && session.LatestEvent == AgentEvent.PermissionRequest;
        return session with
        {
            Source = version < SourceVersion ? null : session.Source,
            ResultState = hideResult ? null : session.ResultState,
            ResultUntilUtc = hideResult ? null : session.ResultUntilUtc,
            LatestEvent = version == EnrichedVersion ? session.LatestEvent : null,
            LatestEventAtUtc = version == EnrichedVersion ? session.LatestEventAtUtc : null
        };
    }

    public static PresenceReport Project(PresenceReport report, int version)
    {
        if (version is not (Version or SourceVersion or EnrichedVersion))
            throw new InvalidOperationException("Managed reporting requires receiver v2 or newer.");
        var hook = report.Hook;
        if (hook is not null)
        {
            _ = ProjectSession(new() { Source = hook.Source }, version);
            if (version < SourceVersion && hook.Event == AgentEvent.ExecutionStopped)
                throw new InvalidOperationException("This event requires receiver v3 or newer.");
            var source = hook.Source ?? SourceDescriptor.LegacyCli;
            hook = hook with
            {
                ProtocolVersion = version >= SourceVersion ? SourceVersion : Protocol.Version,
                Source = version >= SourceVersion ? source : null,
                Client = version >= SourceVersion ? source.Kind : "copilot-cli",
                ClientVersion = version >= SourceVersion ? source.Version : report.ClientVersion
            };
        }
        return report with
        {
            ProtocolVersion = version, Client = version >= SourceVersion ? "agent-signaler" : "copilot-cli",
            Sessions = report.Sessions?.Select(s => ProjectSession(s, version)).ToArray(), Hook = hook
        };
    }

    // Version is mutable but not identity. Also reserve the explicit legacy descriptor for null sources.
    public static bool FitsSnapshot(IEnumerable<SessionSnapshot> sessions)
    {
        var reserved = sessions.Select(s => s with
        {
            Source = (s.Source ?? SourceDescriptor.LegacyCli) with { Version = new string('\uFFFF', 64) },
            UnderlyingState = AgentState.Executing, AwaitingUserInput = true,
            ResultState = AgentState.Succeeded, ResultUntilUtc = DateTimeOffset.MaxValue,
            UpdatedAtUtc = DateTimeOffset.MaxValue,
            LatestEvent = AgentEvent.UserPromptSubmitted, LatestEventAtUtc = DateTimeOffset.MaxValue
        }).ToArray();
        return reserved.Length <= Protocol.MaxSessions &&
            JsonSerializer.SerializeToUtf8Bytes(reserved, Protocol.Json).Length <= Protocol.MaxBodyBytes - 4096;
    }

    private static bool IsUtc(DateTimeOffset value) =>
        value.Year is >= 1970 and <= 9998 && value.Offset == TimeSpan.Zero;

    private static void CheckText(string? value, int max, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            errors.Add($"{name} must contain 1-{max} characters without control characters.");
    }
}
