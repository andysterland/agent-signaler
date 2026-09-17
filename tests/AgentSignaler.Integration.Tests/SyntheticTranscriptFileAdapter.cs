using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

// TEST-ONLY framing contract. This is not a Copilot host format or evidence of host support.
internal sealed class SyntheticTranscriptFileAdapter(string root, SourceDescriptor source)
    : ITranscriptFileAdapter, ITranscriptFileAdapterRegistry
{
    private int _parsedRecords;
    public string ProfileId => "test-only-lf-json-v1";
    public SourceDescriptor Source => source;
    public string TranscriptRoot => root;
    public int ParsedRecords => Volatile.Read(ref _parsedRecords);
    public string GetExpectedFileName(string sessionId) => sessionId + ".synthetic-jsonl";
    public ITranscriptFileAdapter? Find(SourceDescriptor candidate) => candidate == Source ? this : null;

    public TranscriptFileRecord ParseRecord(ReadOnlySpan<byte> utf8Record, string sessionId)
    {
        Interlocked.Increment(ref _parsedRecords);
        try
        {
            var reader = new Utf8JsonReader(utf8Record, new JsonReaderOptions { MaxDepth = 16 });
            using var document = JsonDocument.ParseValue(ref reader);
            var value = document.RootElement;
            if (reader.BytesConsumed != utf8Record.Length || value.ValueKind != JsonValueKind.Object ||
                value.GetProperty("format").GetString() != ProfileId)
                return new(TranscriptFileRecordKind.Invalid);
            if (value.GetProperty("session").GetString() != sessionId)
                return new(TranscriptFileRecordKind.SessionMismatch);
            if (value.GetProperty("role").GetString() != "assistant" ||
                !value.GetProperty("complete").GetBoolean() ||
                value.GetProperty("audience").GetString() != "user")
                return new(TranscriptFileRecordKind.Ignored);
            return new(TranscriptFileRecordKind.Assistant,
                value.GetProperty("text").GetString(), value.GetProperty("id").GetString());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new(TranscriptFileRecordKind.Invalid);
        }
    }

    public string Frame(string session, string id, string text, string role = "assistant",
        bool complete = true, string audience = "user") =>
        JsonSerializer.Serialize(new
        {
            format = ProfileId, session, id, role, complete, audience, text,
            toolArguments = TranscriptEndToEndTests.ForbiddenArguments,
            toolResult = TranscriptEndToEndTests.ForbiddenResult,
            reasoning = TranscriptEndToEndTests.ForbiddenReasoning
        }) + "\n";
}
