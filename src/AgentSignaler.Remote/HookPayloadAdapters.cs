using System.Globalization;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>Explicit host contracts; adding a normalized event does not enable it in any host.</summary>
public static class HookPayloadAdapters
{
    private static readonly IReadOnlyDictionary<string, AgentEvent> CliEvents = new Dictionary<string, AgentEvent>(StringComparer.Ordinal)
    {
        ["sessionStart"] = AgentEvent.SessionStart,
        ["userPromptSubmitted"] = AgentEvent.UserPromptSubmitted,
        ["preToolUse"] = AgentEvent.PreToolUse,
        ["postToolUse"] = AgentEvent.PostToolUse,
        ["permissionRequest"] = AgentEvent.PermissionRequest,
        ["agentStop"] = AgentEvent.AgentStop,
        ["errorOccurred"] = AgentEvent.ErrorOccurred,
        ["sessionEnd"] = AgentEvent.SessionEnd,
        ["postToolUseFailure"] = AgentEvent.PostToolUseFailure
    };
    private static readonly IReadOnlyDictionary<string, AgentEvent> CodeEvents = new Dictionary<string, AgentEvent>(StringComparer.Ordinal)
    {
        ["SessionStart"] = AgentEvent.SessionStart,
        ["UserPromptSubmit"] = AgentEvent.UserPromptSubmitted,
        ["PreToolUse"] = AgentEvent.PreToolUse,
        ["PostToolUse"] = AgentEvent.PostToolUse,
        ["Stop"] = AgentEvent.ExecutionStopped
    };

    private static IReadOnlyDictionary<string, AgentEvent> Events(string adapter) => adapter switch
    {
        "copilot-cli" or "visual-studio" => CliEvents,
        "vscode" => CodeEvents,
        _ => throw new InvalidDataException("Unsupported hook adapter.")
    };

    public static AgentEvent Event(string adapter, string name) =>
        Events(adapter).TryGetValue(name, out var kind) ? kind : throw new InvalidDataException("Unsupported hook event.");

    public static string? EventName(string adapter, AgentEvent kind) =>
        Events(adapter).FirstOrDefault(pair => pair.Value == kind).Key;

    public static bool IsAllowed(RemoteConfiguration config, AgentEvent kind, SourceDescriptor? source)
    {
        if (source is not null && !source.IsValid) return false;
        var effective = source ?? SourceDescriptor.LegacyCli;
        if (effective.Kind == "visual-studio" && effective.Version != "shared") return false;
        var name = EventName(effective.Kind, kind);
        if (name is null) return false;
        if (config.Version < 4) return source is null || effective == SourceDescriptor.LegacyCli;
        return config.Integrations.Any(target => IsEnabled(target) && target.Kind == effective.Kind &&
            (source is null || effective == SourceDescriptor.LegacyCli || target.ScopeId == effective.ScopeId) &&
            target.SupportedEvents.Contains(name, StringComparer.Ordinal));
    }

    internal static bool IsEnabled(IntegrationTarget target) => target.CanInstall;

    public static bool IsSanitized(AgentEvent? kind, HookData? hook) =>
        kind is { } value && Enum.IsDefined(value) && hook is not null &&
        RemoteConfiguration.ValidText(hook.SessionId, 128) &&
        Protocol.ValidDisplayName(hook.DisplayName) &&
        hook.Timestamp.Offset == TimeSpan.Zero && hook.Timestamp.Year is >= 1970 and <= 9998 &&
        (hook.Source is null || hook.Source.IsValid) &&
        (!hook.ToolFailed || value == AgentEvent.PostToolUse) &&
        (!hook.ToolRequiresUserInput || value is AgentEvent.PreToolUse or AgentEvent.PostToolUse or AgentEvent.PostToolUseFailure) &&
        (hook.Source?.Kind != "vscode" || !hook.ToolFailed && !hook.ToolRequiresUserInput) &&
        (hook.InvocationId is null || hook.Source?.Kind == "vscode" &&
            value is AgentEvent.PreToolUse or AgentEvent.PostToolUse &&
            RemoteConfiguration.ValidText(hook.InvocationId, 128)) &&
        EventName(hook.Source?.Kind ?? "copilot-cli", value) is not null;

    public static HookData Parse(string adapter, string eventName, ReadOnlyMemory<byte> input,
        DateTimeOffset now, SourceDescriptor? source)
    {
        if (source is not null && (!source.IsValid || source.Kind != adapter) ||
            source is null && adapter != "copilot-cli")
            throw new InvalidDataException("Invalid hook source.");
        var kind = Event(adapter, eventName);
        if (adapter != "vscode") return HookParser.Parse(input, kind, now) with { Source = source };
        if (input.Length > 65536) throw new InvalidDataException("Invalid hook.");
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("session_id", out var id) || id.ValueKind != JsonValueKind.String ||
            !RemoteConfiguration.ValidText(id.GetString(), 128))
            throw new MissingHookSessionException();
        if (!root.TryGetProperty("hook_event_name", out var eventProperty) ||
            eventProperty.ValueKind != JsonValueKind.String || eventProperty.GetString() != eventName ||
            !root.TryGetProperty("timestamp", out var timestampProperty) || timestampProperty.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(timestampProperty.GetString(),
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) ||
            timestamp.Year is < 1970 or > 9998)
            throw new InvalidDataException("Invalid hook metadata.");
        string? invocationId = null;
        if (kind is AgentEvent.PreToolUse or AgentEvent.PostToolUse &&
            root.TryGetProperty("tool_use_id", out var invocation))
        {
            if (invocation.ValueKind != JsonValueKind.String || !RemoteConfiguration.ValidText(invocation.GetString(), 128))
                throw new InvalidDataException("Invalid tool invocation identifier.");
            invocationId = invocation.GetString();
        }
        // This correlates a tool operation, but does not prove two hook callbacks are duplicates.
        return new HookData(id.GetString()!, timestamp > now ? now : timestamp, false,
            Source: source, InvocationId: invocationId);
    }
}

public sealed class MissingHookSessionException : IOException
{
    public MissingHookSessionException() : base("Hook omitted a stable session identifier.") { }
}
