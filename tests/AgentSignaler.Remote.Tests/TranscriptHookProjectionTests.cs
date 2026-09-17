using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class TranscriptHookProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private const string Allowed = "Fictional Alex: alex@example.invalid";
    private const string Forbidden = "EXCLUDED-TOOL-SECRET";

    [Theory]
    [InlineData("copilot-cli", "userPromptSubmitted")]
    [InlineData("visual-studio", "userPromptSubmitted")]
    [InlineData("vscode", "UserPromptSubmit")]
    public void PromptsAreExplicitHookTextWithoutExcludedFields(string adapter, string name)
    {
        var source = new SourceDescriptor(adapter, "synthetic", adapter == "visual-studio" ? "shared" : "test");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            prompt = Allowed, system = Forbidden, reasoning = Forbidden,
            tool_input = Forbidden, toolResult = Forbidden, transcript_path = Forbidden
        });
        var result = TranscriptHookProjection.Parse(adapter, name, bytes, new("session", Now, false, Source: source), 4);
        var message = Assert.IsType<TranscriptMessage>(result!.Payload);
        Assert.Equal(Allowed, message.Text);
        Assert.Equal(TranscriptRole.User, message.Role);
        Assert.Equal(TranscriptMessageIdOrigin.Local, message.MessageIdOrigin);
        var serialized = JsonSerializer.Serialize(result, ClientIpc.Json);
        Assert.DoesNotContain(Forbidden, serialized);
        Assert.DoesNotContain("transcript_path", serialized);
    }

    [Fact]
    public void ToolMetadataUsesOnlyDocumentedNameAndCorrelation()
    {
        var code = new SourceDescriptor("vscode", "fixture", "test");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tool_name = "fixture_tool", tool_input = Forbidden, tool_response = Forbidden,
            response = Forbidden, reason = Forbidden, toolName = Forbidden
        });
        var result = TranscriptHookProjection.Parse("vscode", "PostToolUse", bytes,
            new("session", Now, false, Source: code, InvocationId: "operation"), 4);
        Assert.Equal(new TranscriptToolActivity("fixture_tool", "operation",
            TranscriptToolPhase.Completed, TranscriptToolOutcome.Unknown), result!.Payload);
        Assert.DoesNotContain(Forbidden, JsonSerializer.Serialize(result, ClientIpc.Json));
        Assert.False(TranscriptHookProjection.IsValid(result with
        {
            Payload = new TranscriptMessage(TranscriptRole.Assistant, "invented", TranscriptMessageIdOrigin.Local, "not a reply")
        }));
    }

    [Theory]
    [InlineData("copilot-cli", "agentStop", "transcriptPath")]
    [InlineData("vscode", "Stop", "transcript_path")]
    public void StopReferencesRequireVerifiedProfileAndExactField(string adapter, string name, string field)
    {
        var source = new SourceDescriptor(adapter, "fixture", "test");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            [field] = @"Q:\synthetic-fixture\session.jsonl", ["response"] = Forbidden
        });
        var observation = TranscriptHookProjection.Parse(adapter, name, bytes, new("session", Now, false, Source: source), 2)!;
        Assert.Null(TranscriptHookProjection.ReadReference(adapter, name, bytes, observation, false));
        var reference = TranscriptHookProjection.ReadReference(adapter, name, bytes, observation, true);
        Assert.NotNull(reference);
        Assert.Equal(observation.CaptureId.ToString("N"), reference.CaptureId);
        Assert.DoesNotContain("synthetic-fixture", JsonSerializer.Serialize(observation, ClientIpc.Json));
        var aliases = JsonSerializer.SerializeToUtf8Bytes(new { transcript = reference.Path, transcriptpath = reference.Path });
        Assert.Null(TranscriptHookProjection.ReadReference(adapter, name, aliases, observation, true));
        var oversized = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { [field] = new('x', 513) });
        Assert.Null(TranscriptHookProjection.ReadReference(adapter, name, oversized, observation, true));
    }

    [Fact]
    public void StopCannotSubstituteSubagentResponseForAssistantReply()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { response = Forbidden, assistant = Forbidden });
        var result = TranscriptHookProjection.Parse("copilot-cli", "agentStop", bytes, new("session", Now, false), 1);
        Assert.IsType<TranscriptLifecycle>(result!.Payload);
        Assert.DoesNotContain(Forbidden, JsonSerializer.Serialize(result, ClientIpc.Json));
    }

    [Theory]
    [InlineData("\"\\\n")]
    [InlineData("\ud83d\ude00")]
    public void EscapingHeavyTextFitsRealIpcWithMetadataReserveAndSafeUnicode(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 5000));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { prompt = text },
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Assert.InRange(bytes.Length, 1, 65536);
        var result = TranscriptHookProjection.Parse("copilot-cli", "userPromptSubmitted", bytes, new("session", Now, false), 1);
        var message = Assert.IsType<TranscriptMessage>(result!.Payload);
        Assert.True(message.Truncated);
        Assert.Equal(TranscriptTruncationReason.SerializedLimit, message.TruncationReason);
        Assert.StartsWith(message.Text, text);
        Assert.True(TranscriptHookProjection.IsValid(result));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new ClientIpcRequest(3, "transcript-hook", Transcript: result),
            ClientIpc.Json).Length <= 32768 - 2048);
    }
}
