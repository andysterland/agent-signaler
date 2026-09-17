using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts;

public enum AgentState { Offline, Idle, Executing, Waiting, Succeeded, Failed }
public enum AgentEvent
{
    SessionStart = 1, UserPromptSubmitted, PreToolUse, PostToolUse,
    PermissionRequest, AgentStop, ErrorOccurred, SessionEnd, PostToolUseFailure, ExecutionStopped
}

public sealed record StatusRequest
{
    [JsonRequired]
    public int ProtocolVersion { get; init; } = Protocol.Version;
    [JsonRequired]
    public Guid EventId { get; init; }
    [JsonRequired]
    public Guid MachineId { get; init; }
    [JsonRequired]
    public string MachineName { get; init; } = "";
    [JsonRequired]
    public string Client { get; init; } = "copilot-cli";
    [JsonRequired]
    public string ClientVersion { get; init; } = "";
    public string? SessionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SourceDescriptor? Source { get; init; }
    [JsonRequired]
    public AgentEvent Event { get; init; }
    [JsonRequired]
    public DateTimeOffset ReportedAtUtc { get; init; }
    public bool ToolFailed { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ToolRequiresUserInput { get; init; }
    public string? RelayVersion { get; init; }
}

public sealed record StatusResponse(bool Duplicate);
public sealed record HealthResponse(int ProtocolVersion, string Status);
public sealed record ValidationResponse(IReadOnlyList<string> Errors);

public static class Protocol
{
    public const int Version = 1;
    public const int DefaultPort = 51820;
    public const string ConnectionTestHeader = "X-AgentSignaler-Connection-Test";
    public const int MaxBodyBytes = 32768;
    public const int MaxSessions = 64;
    public const int MaxDisplayNameLength = 128;
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ResultDuration = TimeSpan.FromSeconds(60);
    public static readonly JsonSerializerOptions Json = CreateJson();

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            MaxDepth = 12
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    public static IReadOnlyList<string> Validate(StatusRequest request)
        => ValidateCore(request, sourceAware: false);

    public static IReadOnlyList<string> ValidateSource(StatusRequest request)
        => ValidateCore(request, sourceAware: true);

    public static bool ValidDisplayName(string? value) =>
        value is null || !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaxDisplayNameLength && !value.Any(char.IsControl);

    private static IReadOnlyList<string> ValidateCore(StatusRequest request, bool sourceAware)
    {
        var errors = new List<string>();
        if (sourceAware ? request.ProtocolVersion is not (PresenceProtocol.SourceVersion or PresenceProtocol.DisplayNameVersion)
            : request.ProtocolVersion != Version)
            errors.Add("Unsupported protocolVersion.");
        if (!ValidDisplayName(request.DisplayName) ||
            request.DisplayName is not null && request.ProtocolVersion != PresenceProtocol.DisplayNameVersion)
            errors.Add("Invalid displayName or unsupported protocol version.");
        if (request.EventId == Guid.Empty) errors.Add("eventId must be a nonempty UUID.");
        if (request.MachineId == Guid.Empty) errors.Add("machineId must be a nonempty UUID.");
        CheckText(request.MachineName, 128, "machineName", errors);
        CheckText(request.ClientVersion, 64, "clientVersion", errors);
        if (sourceAware)
        {
            if (request.Source is { IsValid: false }) errors.Add("Invalid source descriptor.");
            var origin = request.Source ?? SourceDescriptor.LegacyCli;
            if (request.Client != origin.Kind) errors.Add("client must match the originating source kind.");
            if (request.ClientVersion != origin.Version) errors.Add("clientVersion must match the originating source version.");
        }
        else
        {
            if (request.Client != "copilot-cli") errors.Add("client must be copilot-cli.");
            if (request.Source is not null) errors.Add("source requires presence v3.");
            if (request.Event == AgentEvent.ExecutionStopped) errors.Add("executionStopped requires presence v3.");
        }
        if (request.RelayVersion is not null) CheckText(request.RelayVersion, 64, "relayVersion", errors);
        if (!Enum.IsDefined(request.Event)) errors.Add("Unknown event.");
        if (request.ReportedAtUtc.Year is < 1970 or > 9998 || request.ReportedAtUtc.Offset != TimeSpan.Zero)
            errors.Add("reportedAtUtc must be a UTC timestamp.");
        CheckText(request.SessionId, 128, "sessionId", errors);
        if (request.ToolFailed && request.Event != AgentEvent.PostToolUse)
            errors.Add("toolFailed is only allowed for postToolUse.");
        if (request.ToolRequiresUserInput && request.Event is not
            (AgentEvent.PreToolUse or AgentEvent.PostToolUse or AgentEvent.PostToolUseFailure))
            errors.Add("toolRequiresUserInput is only allowed for tool events.");
        return errors;
    }

    private static void CheckText(string? value, int max, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            errors.Add($"{name} must contain 1-{max} characters without control characters.");
    }
}
