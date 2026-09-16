using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class SourcePresentationTests
{
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
