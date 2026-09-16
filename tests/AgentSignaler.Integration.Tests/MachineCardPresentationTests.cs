using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class MachineCardPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1216, false, 3)]
    [InlineData(976, true, 3)]
    [InlineData(800, false, 2)]
    [InlineData(500, false, 1)]
    [InlineData(200, true, 1)]
    public void RectangularCardsFitResponsiveColumns(double width, bool dense, int expectedColumns)
    {
        var (columns, cardWidth) = MachineCardPresentation.Layout(width, dense);
        Assert.Equal(expectedColumns, columns);
        Assert.Equal(width, columns * cardWidth + (columns - 1) * MachineCardPresentation.CardSpacing, 6);
        Assert.Equal(220, MachineCardPresentation.MinimumHeight);
    }

    [Theory]
    [InlineData(AgentEvent.PreToolUse, "preToolUse")]
    [InlineData(AgentEvent.PermissionRequest, "permission request")]
    [InlineData(AgentEvent.AgentStop, "session complete")]
    [InlineData(AgentEvent.PostToolUseFailure, "tool failure")]
    [InlineData(AgentEvent.ExecutionStopped, "execution stopped")]
    public void ActivityUsesLatestSessionSourceAndActualEvent(AgentEvent latestEvent, string activity)
    {
        var machine = Machine(AgentState.Executing) with
        {
            LatestEvent = latestEvent,
            Sessions =
            [
                new() { Source = new("vscode", "profile"), UpdatedAtUtc = Now },
                new() { Source = new("visual-studio", "scope"), UpdatedAtUtc = Now.AddMinutes(-1) }
            ]
        };
        Assert.Equal($"VS Code \u00B7 {activity}", MachineCardPresentation.Activity(machine, Now));
    }

    [Fact]
    public void ActivityWithoutSessionsUsesMachineClientWithoutInventingAnEvent()
    {
        Assert.Equal("Copilot CLI \u00B7 Awaiting hook activity",
            MachineCardPresentation.Activity(Machine(AgentState.Waiting), Now));
        Assert.Equal("Client connected", MachineCardPresentation.Activity(Machine(AgentState.Idle), Now));
    }

    [Theory]
    [InlineData(0, "moments ago")]
    [InlineData(1, "1 minute ago")]
    [InlineData(14, "14 minutes ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(120, "2 hours ago")]
    [InlineData(1440, "1 day ago")]
    [InlineData(-1, "moments ago")]
    public void OfflineRecencyUsesRealLastContact(double minutes, string expected)
    {
        var machine = Machine(AgentState.Offline) with { LastContactUtc = Now.AddMinutes(-minutes) };
        Assert.Equal($"Last seen {expected}", MachineCardPresentation.Activity(machine, Now));
    }

    [Fact]
    public void ExecutingRecencyUsesHookTimestampRatherThanHeartbeat()
    {
        var machine = Machine(AgentState.Executing) with { LatestEventUtc = Now.AddMinutes(-2) };
        Assert.Equal("Updated 2 minutes ago", MachineCardPresentation.Footer(machine, Now));
        Assert.Equal("Updated moments ago", MachineCardPresentation.Footer(machine with { LatestEventUtc = null }, Now));
    }

    [Theory]
    [InlineData(AgentState.Waiting, "Needs user input")]
    [InlineData(AgentState.Succeeded, "Result visible for 60 seconds")]
    [InlineData(AgentState.Failed, "No prompt or response content stored")]
    [InlineData(AgentState.Idle, "Ready for the next session")]
    [InlineData(AgentState.Offline, "Heartbeat deadline exceeded")]
    public void FooterExplainsActualState(AgentState state, string expected)
    {
        Assert.Equal(expected, MachineCardPresentation.Footer(Machine(state), Now));
    }

    [Fact]
    public void ExplicitDisconnectDoesNotClaimHeartbeatTimeout()
    {
        Assert.Equal("Client disconnected",
            MachineCardPresentation.Footer(Machine(AgentState.Offline) with { ExplicitOffline = true }, Now));
    }

    [Fact]
    public void CompactContentIsGlyphOnlyAndFullContentIncludesMachineInformation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var main = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", "MainWindow.cs"));
        Assert.Contains("Content = miniature ? _icon : CreateFullContent()", main);
        Assert.Contains("var appearance = MachineCardAppearance.For(state)", main);
        Assert.Contains("heading.Children.Add(_connectionIcon)", main);
        Assert.Contains("heading.Children.Add(_name)", main);
        Assert.Contains("description.Children.Add(_status)", main);
        Assert.Contains("description.Children.Add(_activity)", main);
        Assert.Contains("footer.Children.Add(_mapping)", main);
        Assert.Contains("footer.Children.Add(_footer)", main);
        Assert.Contains("ToolTipService.SetToolTip(Button, tooltip)", main);
    }

    private static MachineView Machine(AgentState state) =>
        new(Guid.NewGuid(), "test-machine", null, "copilot-cli", "1.0", state, null, Now, []);
}
