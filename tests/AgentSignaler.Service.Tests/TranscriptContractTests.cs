using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public sealed class TranscriptContractTests
{
    internal static readonly SourceDescriptor Source = new("copilot-cli", "synthetic", "test");
    internal static readonly DateTimeOffset Now = new(2026, 9, 17, 0, 0, 0, TimeSpan.Zero);

    internal static TranscriptEvent Event(Guid machine, Guid epoch, Guid stream, long sequence = 1, string session = "synthetic-session") => new()
    {
        EventId = Guid.NewGuid(), MachineId = machine, ReceiverEpoch = epoch, StreamId = stream,
        Generation = 1, Sequence = sequence, Source = Source, SessionId = session,
        Provenance = new("userPromptSubmitted", "synthetic-v1", Now, Now, TranscriptOrdering.Arrival, CaptureOrigin.Hook, "test-only"),
        Payload = new TranscriptMessage(TranscriptRole.User, Guid.NewGuid().ToString("N"), TranscriptMessageIdOrigin.Local,
            "Synthetic Alex, fictional@example.invalid: allowed prompt.")
    };

    [Fact]
    public void AllTypedPayloadsRoundTripAndRemainStrict()
    {
        var value = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        TranscriptPayload[] payloads =
        [
            value.Payload,
            new TranscriptToolActivity("synthetic_tool", "invocation", TranscriptToolPhase.Completed, TranscriptToolOutcome.Unknown),
            new TranscriptLifecycle(TranscriptLifecycleSignal.StopObserved),
            new TranscriptGap(null, null, TranscriptGapReason.BaselineEstablished)
        ];
        foreach (var payload in payloads)
        {
            var input = value with { Payload = payload };
            var encoded = JsonSerializer.Serialize(input, TranscriptProtocol.Json);
            var decoded = JsonSerializer.Deserialize<TranscriptEvent>(encoded, TranscriptProtocol.Json)!;
            Assert.Equal(input, decoded);
            Assert.True(TranscriptProtocol.Validate(decoded));
            Assert.DoesNotContain("transcriptPath", encoded);
            Assert.DoesNotContain("arguments", encoded);
        }
    }

    [Theory]
    [InlineData("\"path\":\"forbidden-marker\",")]
    [InlineData("\"ProtocolVersion\":1,")]
    [InlineData("\"protocolVersion\":1,")]
    public void UnknownWrongCaseAndDuplicatePropertiesAreRejected(string injected)
    {
        var json = JsonSerializer.Serialize(Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), TranscriptProtocol.Json);
        json = "{" + injected + json[1..];
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptEvent>(json, TranscriptProtocol.Json));
    }

    [Theory]
    [InlineData("{\"kind\":\"message\",\"role\":\"user\",\"messageId\":\"a\",\"messageIdOrigin\":\"local\",\"text\":\"a\",\"arguments\":\"forbidden\"}")]
    [InlineData("{\"kind\":\"tool\",\"toolName\":\"a\",\"invocationId\":null,\"phase\":1,\"outcome\":\"requested\"}")]
    [InlineData("{\"kind\":\"tool\",\"toolName\":\"a\",\"toolName\":\"b\",\"invocationId\":null,\"phase\":\"requested\",\"outcome\":\"requested\"}")]
    [InlineData("{\"kind\":\"reasoning\",\"text\":\"forbidden\"}")]
    [InlineData("{\"kind\":\"message\",\"role\":\"user\",\"messageId\":\"a\",\"messageIdOrigin\":\"local\"}")]
    public void NestedPayloadsRejectExcludedFieldsAndInvalidDiscriminators(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptPayload>(json, TranscriptProtocol.Json));

    [Fact]
    public void ProvenanceRolesIntervalsAndUnicodeAreValidated()
    {
        var value = Event(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.False(TranscriptProtocol.Validate(value with { ProtocolVersion = 99 }));
        Assert.False(TranscriptProtocol.Validate(value with { Provenance = value.Provenance with { ObservedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) } }));
        Assert.False(TranscriptProtocol.Validate(value with { Provenance = value.Provenance with { CaptureOrigin = CaptureOrigin.TranscriptFile } }));
        var message = (TranscriptMessage)value.Payload;
        Assert.False(TranscriptProtocol.Validate(value with { Payload = message with { Text = "\ud800" } }));
        Assert.True(TranscriptProtocol.Validate(value with { Payload = message with { Text = "😀\nfictional@example.invalid" } }));
        Assert.False(TranscriptProtocol.Validate(value with { Payload = message with { Truncated = true } }));
        Assert.False(TranscriptProtocol.Validate(value with { Payload = new TranscriptGap(2, 1, TranscriptGapReason.QueueOverflow) }));
        Assert.False(TranscriptProtocol.Validate(value with { Payload = new TranscriptGap(null, 1, TranscriptGapReason.QueueOverflow) }));
        Assert.False(TranscriptProtocol.Validate(value with { Payload = new TranscriptToolActivity("name", null, TranscriptToolPhase.Requested, TranscriptToolOutcome.Failed) }));
        Assert.True(TranscriptProtocol.Validate(value with
        {
            Provenance = value.Provenance with { CaptureOrigin = CaptureOrigin.TranscriptFile, TriggeringCaptureId = Guid.NewGuid() },
            Payload = message with { Role = TranscriptRole.Assistant }
        }));
    }

    [Fact]
    public void RequiredControlsAndStringEnumsCannotBeSilentlyDefaulted()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptOpenRequest>("{}", TranscriptProtocol.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TranscriptCapabilitiesRequest>("{\"machineId\":\"00000000-0000-0000-0000-000000000001\"}", TranscriptProtocol.Json));
        Assert.Equal(1280, TranscriptProtocol.AccountedEventBytes(1));
        Assert.Equal(1280, TranscriptProtocol.AccountedEventBytes(256));
        Assert.Equal(1536, TranscriptProtocol.AccountedEventBytes(257));
    }
}
