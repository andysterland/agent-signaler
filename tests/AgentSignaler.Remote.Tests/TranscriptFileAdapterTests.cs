using System.Text;
using AgentSignaler.Contracts;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class TranscriptFileAdapterTests
{
    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("vscode")]
    [InlineData("visual-studio")]
    public void ProductionRegistryNeverGuessesAProfile(string kind)
    {
        Assert.Null(VerifiedTranscriptFileAdapterRegistry.Production.Find(new(kind, "scope", "latest")));
        Assert.Equal("format-unverified", VerifiedTranscriptFileAdapterRegistry.UnavailableReason);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("tool")]
    [InlineData("reasoning")]
    [InlineData("subagent")]
    [InlineData("system")]
    [InlineData("attachment")]
    [InlineData("delta")]
    public void TestOnlyProfileDiscardsExcludedFields(string kind)
    {
        var adapter = new SyntheticTranscriptFileAdapter(@"C:\synthetic");
        var result = adapter.ParseRecord(Encoding.UTF8.GetBytes(
            SyntheticTranscriptFileAdapter.Record("EXCLUDED-SYNTHETIC-TEXT", kind: kind)), "session-a");
        Assert.Equal(TranscriptFileRecordKind.Ignored, result.Kind);
        Assert.Null(result.AssistantText);
        Assert.Null(result.NativeMessageId);
    }

    [Fact]
    public void TestOnlyProfileRequiresCompletedAssistantAndExactSession()
    {
        var adapter = new SyntheticTranscriptFileAdapter(@"C:\synthetic");
        var text = "Fictional Ada Example <ada@example.invalid> 😀";
        var data = Encoding.UTF8.GetBytes(SyntheticTranscriptFileAdapter.Record(text));
        Assert.Equal(text, adapter.ParseRecord(data, "session-a").AssistantText);
        Assert.Equal(TranscriptFileRecordKind.SessionMismatch, adapter.ParseRecord(data, "session-b").Kind);
        Assert.Equal(TranscriptFileRecordKind.Ignored, adapter.ParseRecord(Encoding.UTF8.GetBytes(
            SyntheticTranscriptFileAdapter.Record(text, complete: false)), "session-a").Kind);
    }

    [Theory]
    [InlineData("{\"session\":\"session-a\",\"session\":\"session-a\"}")]
    [InlineData("{\"unknown\":\"excluded\"}")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void TestOnlyProfileRejectsDuplicateUnknownAndMissingProperties(string json)
    {
        var adapter = new SyntheticTranscriptFileAdapter(@"C:\synthetic");
        Assert.Equal(TranscriptFileRecordKind.Invalid,
            adapter.ParseRecord(Encoding.UTF8.GetBytes(json), "session-a").Kind);
    }

    [Fact]
    public void MetadataToStringCannotLeakPathsOrText()
    {
        var reference = new LocalTranscriptReference(new("copilot-cli", "scope"), "session-a",
            DateTimeOffset.UtcNow, "capture-a", 1, @"C:\PRIVATE-PATH\session-a");
        Assert.DoesNotContain("PRIVATE-PATH", reference.ToString());
        Assert.DoesNotContain("PRIVATE-TEXT", new TranscriptFileRecord(
            TranscriptFileRecordKind.Assistant, "PRIVATE-TEXT").ToString());
        Assert.DoesNotContain("PRIVATE-TEXT", new TranscriptReaderOutput(reference, "test", "assistant",
            "PRIVATE-TEXT").ToString());
    }
}
