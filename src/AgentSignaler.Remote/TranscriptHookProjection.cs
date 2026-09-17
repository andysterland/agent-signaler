using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed record TranscriptHookObservation(SourceDescriptor Source, string SessionId,
    string HostEventName, DateTimeOffset ObservedAtUtc, Guid CaptureId, long SettingsRevision,
    TranscriptPayload Payload)
{
    public override string ToString() => nameof(TranscriptHookObservation);
}

public sealed record TranscriptNegotiation(long SettingsRevision, bool Enabled,
    bool UserMessages, bool ToolNames, bool ToolCorrelation, bool AssistantFile,
    string? FormatProfileId, string Availability);

/// <summary>Only invoked after content-free Client negotiation has enabled this source.</summary>
public static class TranscriptHookProjection
{
    public static TranscriptHookObservation? Parse(string adapter, string eventName,
        ReadOnlyMemory<byte> input, HookData status, long settingsRevision)
    {
        if (input.Length > 65536 || !HookPayloadAdapters.IsSanitized(
            HookPayloadAdapters.Event(adapter, eventName), status)) return null;
        using var document = JsonDocument.Parse(input,
            new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        var root = document.RootElement;
        var kind = HookPayloadAdapters.Event(adapter, eventName);
        TranscriptPayload? payload = kind switch
        {
            AgentEvent.UserPromptSubmitted => ReadPrompt(root),
            AgentEvent.PreToolUse or AgentEvent.PostToolUse or AgentEvent.PostToolUseFailure =>
                ReadTool(root, adapter, kind, status),
            AgentEvent.SessionStart => new TranscriptLifecycle(TranscriptLifecycleSignal.SessionStarted),
            AgentEvent.SessionEnd => new TranscriptLifecycle(TranscriptLifecycleSignal.SessionEnded),
            AgentEvent.AgentStop or AgentEvent.ExecutionStopped => new TranscriptLifecycle(TranscriptLifecycleSignal.StopObserved),
            _ => null
        };
        if (payload is null) return null;
        var observation = new TranscriptHookObservation(status.Source ?? SourceDescriptor.LegacyCli,
            status.SessionId, eventName, status.Timestamp, Guid.NewGuid(), settingsRevision, payload);
        return Fit(observation);
    }

    public static bool IsValid(TranscriptHookObservation? observation)
    {
        if (observation is null || observation.Source is not { IsValid: true } ||
            observation.CaptureId == Guid.Empty || observation.SettingsRevision < 0) return false;
        var candidate = ToEvent(observation);
        if (!TranscriptProtocol.Validate(candidate)) return false;
        var name = observation.HostEventName;
        var adapter = observation.Source.Kind;
        return observation.Payload switch
        {
            TranscriptMessage { Role: TranscriptRole.User, MessageIdOrigin: TranscriptMessageIdOrigin.Local } =>
                name == HookPayloadAdapters.EventName(adapter, AgentEvent.UserPromptSubmitted),
            TranscriptToolActivity tool =>
                tool.Phase == TranscriptToolPhase.Requested
                    ? name == HookPayloadAdapters.EventName(adapter, AgentEvent.PreToolUse)
                    : name == HookPayloadAdapters.EventName(adapter, AgentEvent.PostToolUse) ||
                      name == HookPayloadAdapters.EventName(adapter, AgentEvent.PostToolUseFailure),
            TranscriptLifecycle lifecycle => lifecycle.Signal switch
            {
                TranscriptLifecycleSignal.SessionStarted => name == HookPayloadAdapters.EventName(adapter, AgentEvent.SessionStart),
                TranscriptLifecycleSignal.SessionEnded => name == HookPayloadAdapters.EventName(adapter, AgentEvent.SessionEnd),
                TranscriptLifecycleSignal.StopObserved => name == HookPayloadAdapters.EventName(adapter, AgentEvent.AgentStop) ||
                    name == HookPayloadAdapters.EventName(adapter, AgentEvent.ExecutionStopped),
                _ => false
            },
            _ => false
        };
    }

    public static LocalTranscriptReference? ReadReference(string adapter, string eventName,
        ReadOnlyMemory<byte> input, TranscriptHookObservation observation, bool verifiedProfile)
    {
        if (!verifiedProfile || observation.Payload is not TranscriptLifecycle { Signal: TranscriptLifecycleSignal.StopObserved } ||
            input.Length > 65536) return null;
        var field = (adapter, eventName) switch
        {
            ("copilot-cli", "agentStop") => "transcriptPath",
            ("vscode", "Stop") => "transcript_path",
            _ => null
        };
        if (field is null) return null;
        using var document = JsonDocument.Parse(input,
            new JsonDocumentOptions { MaxDepth = 64, AllowDuplicateProperties = false });
        if (!document.RootElement.TryGetProperty(field, out var path) ||
            path.ValueKind != JsonValueKind.String) return null;
        var reference = new LocalTranscriptReference(observation.Source, observation.SessionId,
            observation.ObservedAtUtc, observation.CaptureId.ToString("N"), observation.SettingsRevision, path.GetString()!);
        if (!reference.IsValid) return null;
        var request = new ClientIpcRequest(ClientIpc.Version, "transcript-read", TranscriptRead: reference);
        return JsonSerializer.SerializeToUtf8Bytes(request, ClientIpc.Json).Length <= ClientIpc.MaximumMessageBytes
            ? reference : null;
    }

    private static TranscriptMessage? ReadPrompt(JsonElement root)
    {
        if (!root.TryGetProperty("prompt", out var prompt) || prompt.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(prompt.GetString())) return null;
        return new(TranscriptRole.User, Guid.NewGuid().ToString("N"), TranscriptMessageIdOrigin.Local, prompt.GetString()!);
    }

    private static TranscriptToolActivity? ReadTool(JsonElement root, string adapter, AgentEvent kind, HookData status)
    {
        if (!root.TryGetProperty(adapter == "vscode" ? "tool_name" : "toolName", out var tool) ||
            tool.ValueKind != JsonValueKind.String || !TranscriptProtocol.ValidIdentifier(tool.GetString(), 128)) return null;
        var requested = kind == AgentEvent.PreToolUse;
        return new(tool.GetString()!, adapter == "vscode" ? status.InvocationId : null,
            requested ? TranscriptToolPhase.Requested : TranscriptToolPhase.Completed,
            requested ? TranscriptToolOutcome.Requested :
            kind == AgentEvent.PostToolUseFailure || status.ToolFailed ? TranscriptToolOutcome.Failed :
            adapter == "vscode" ? TranscriptToolOutcome.Unknown : TranscriptToolOutcome.Completed);
    }

    private static TranscriptHookObservation? Fit(TranscriptHookObservation observation)
    {
        bool Fits(TranscriptHookObservation value) =>
            JsonSerializer.SerializeToUtf8Bytes(new ClientIpcRequest(ClientIpc.Version, "transcript-hook",
                Transcript: value), ClientIpc.Json).Length <= ClientIpc.MaximumFrameBytes - 2048 &&
            JsonSerializer.SerializeToUtf8Bytes(ToEvent(value), TranscriptProtocol.Json).Length <= TranscriptProtocol.MaxEventBytes;
        if (Fits(observation)) return IsValid(observation) ? observation : null;
        if (observation.Payload is not TranscriptMessage message) return null;
        var low = 0;
        var high = message.Text.Length;
        while (low < high)
        {
            var middle = low + (high - low + 1) / 2;
            var length = SafePrefixLength(message.Text, middle);
            var candidate = observation with { Payload = message with { Text = message.Text[..length],
                Truncated = true, TruncationReason = TranscriptTruncationReason.SerializedLimit } };
            if (Fits(candidate)) low = middle;
            else high = middle - 1;
        }
        observation = observation with { Payload = message with { Text = message.Text[..SafePrefixLength(message.Text, low)],
            Truncated = true, TruncationReason = TranscriptTruncationReason.SerializedLimit } };
        return IsValid(observation) ? observation : null;
    }

    private static int SafePrefixLength(string text, int length) =>
        length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) &&
        char.IsLowSurrogate(text[length]) ? length - 1 : length;

    private static TranscriptEvent ToEvent(TranscriptHookObservation value) => new()
    {
        EventId = value.CaptureId, MachineId = value.CaptureId, Generation = 1,
        ReceiverEpoch = value.CaptureId, StreamId = value.CaptureId, Sequence = 1,
        Source = value.Source, SessionId = value.SessionId,
        Provenance = new(value.HostEventName, HookAdapters.Version, value.ObservedAtUtc, value.ObservedAtUtc,
            TranscriptOrdering.Arrival, CaptureOrigin.Hook, "hook-v1", value.CaptureId),
        Payload = value.Payload
    };
}
