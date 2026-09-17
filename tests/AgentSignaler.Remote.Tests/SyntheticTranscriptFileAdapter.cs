using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote.Tests;

// Invented test grammar. This is deliberately not registered or shipped as a host adapter.
internal sealed class SyntheticTranscriptFileAdapter(string root, string scope = "reader-test")
    : ITranscriptFileAdapter, ITranscriptFileAdapterRegistry
{
    public string ProfileId => "test-only-utf8-lf-v1";
    public SourceDescriptor Source { get; } = new("copilot-cli", scope, "test-only-1");
    public string TranscriptRoot { get; } = root;
    public string GetExpectedFileName(string sessionId) => sessionId + ".synthetic";
    public ITranscriptFileAdapter? Find(SourceDescriptor source) => source == Source ? this : null;
    public Action? Parsing { get; set; }

    public TranscriptFileRecord ParseRecord(ReadOnlySpan<byte> utf8Record, string sessionId)
    {
        Parsing?.Invoke();
        var reader = new Utf8JsonReader(utf8Record, new JsonReaderOptions { MaxDepth = 16 });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return new(TranscriptFileRecordKind.Invalid);
        string? session = null, kind = null, id = null;
        var textStart = 0;
        var textLength = 0;
        bool? complete = null;
        var fields = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) return new(TranscriptFileRecordKind.Invalid);
            var field = reader.GetString() switch
            {
                "session" => 1, "kind" => 2, "complete" => 4, "text" => 8, "id" => 16, _ => 0
            };
            if (field == 0 || (fields & field) != 0 || !reader.Read())
                return new(TranscriptFileRecordKind.Invalid);
            fields |= field;
            if (field == 4)
            {
                if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                    return new(TranscriptFileRecordKind.Invalid);
                complete = reader.GetBoolean();
            }
            else
            {
                if (reader.TokenType != JsonTokenType.String) return new(TranscriptFileRecordKind.Invalid);
                switch (field)
                {
                    case 1: session = reader.GetString(); break;
                    case 2: kind = reader.GetString(); break;
                    case 8:
                        textStart = checked((int)reader.TokenStartIndex);
                        textLength = checked((int)reader.BytesConsumed - textStart);
                        break;
                    case 16: id = reader.GetString(); break;
                }
            }
        }
        if (fields != 31 || reader.TokenType != JsonTokenType.EndObject || reader.Read() ||
            kind is not ("assistant" or "user" or "tool" or "reasoning" or "subagent" or "system" or "attachment" or "delta"))
            return new(TranscriptFileRecordKind.Invalid);
        if (session != sessionId) return new(TranscriptFileRecordKind.SessionMismatch);
        if (kind != "assistant" || complete != true) return new(TranscriptFileRecordKind.Ignored);
        var textReader = new Utf8JsonReader(utf8Record.Slice(textStart, textLength));
        textReader.Read();
        return new(TranscriptFileRecordKind.Assistant, textReader.GetString(), id);
    }

    public static string Record(string text = "Synthetic assistant", string session = "session-a",
        string kind = "assistant", bool complete = true, string id = "message-a") =>
        JsonSerializer.Serialize(new { session, kind, complete, text, id }) + "\n";
}
