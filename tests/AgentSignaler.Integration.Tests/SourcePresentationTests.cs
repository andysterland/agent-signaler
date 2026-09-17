using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class SourcePresentationTests
{
    [Fact]
    public void TranscriptDisplayNameMatchesSourceIdentityAndKeepsTheIdInDetails()
    {
        var id = Guid.NewGuid();
        var source = new SourceDescriptor("copilot-cli", "scope", "1");
        var selection = new TranscriptSelection(id, source with { Version = "2" }, "same", Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var machine = new MachineView(id, "synthetic", null, "copilot-cli", "1",
            AgentState.Waiting, null, now,
            [
                new() { SessionId = "same", Source = source with { ScopeId = "other" }, DisplayName = "Other scope" },
                new() { SessionId = "same", Source = source with { Kind = "vscode" }, DisplayName = "Other source" },
                new() { SessionId = "same", Source = source, DisplayName = "Friendly session" }
            ]);
        var displayName = SessionSourcePresentation.DisplayName(machine, selection);
        Assert.Equal("Friendly session", displayName);
        var stream = new TranscriptSessionInfo(selection, now, true, new(null, null), false, false);
        var label = SessionSourcePresentation.DescribeStream(stream, displayName);
        Assert.StartsWith("Friendly session · ", label);
        Assert.Contains("Session ID: same", label);
        Assert.Contains(selection.StreamId.ToString(), label);
        Assert.Contains("ended/closed", label);
        Assert.Equal("same", SessionSourcePresentation.DisplayName(null, selection));
        Assert.Equal("same", SessionSourcePresentation.DisplayName(machine with { MachineId = Guid.NewGuid() }, selection));
        Assert.Equal("same", SessionSourcePresentation.DisplayName(machine with { Sessions = [] }, selection));
        Assert.Equal("same", SessionSourcePresentation.DisplayName(machine with
        {
            Sessions = [new() { SessionId = "same", Source = source }]
        }, selection));
    }

    [Fact]
    public void LegacyAndMixedSourcesArePresentedWithoutRelabelingTheMachine()
    {
        Assert.Equal("Copilot CLI · scope legacy-cli · unknown", SessionSourcePresentation.Describe(null));
        Assert.Equal("Visual Studio · scope shared-hook-scope · 18.12",
            SessionSourcePresentation.Describe(new("visual-studio", "shared-hook-scope", "18.12")));
        Assert.Equal("Copilot CLI, VS Code, Visual Studio", SessionSourcePresentation.Summary([
            new() { SessionId = "same" },
            new() { SessionId = "same", Source = new("visual-studio", "shared-hook-scope") },
            new() { SessionId = "same", Source = new("vscode", "profile-one") },
            new() { SessionId = "same", Source = new("vscode", "profile-two") }
        ]));
        Assert.Equal("None observed", SessionSourcePresentation.Summary([]));
    }
}
