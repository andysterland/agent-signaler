using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class CompactSessionPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, 64)]
    [InlineData(1, 4, 70)]
    [InlineData(10, 4, 70)]
    [InlineData(11, 9, 75)]
    [InlineData(64, 34, 100)]
    public void EveryConnectedSessionFitsCompleteDeterministicGrid(int count, int height, int tileHeight)
    {
        var sessions = Enumerable.Range(0, count).Select(i => Session($"session-{i:00}")).Reverse().ToArray();
        var machine = Machine(sessions);
        var layout = CompactSessionPresentation.Project(machine, Now);
        Assert.Equal(count, layout.Indicators.Length);
        Assert.Equal(height, layout.IndicatorHeight);
        Assert.Equal(tileHeight, layout.TileHeight);
        Assert.Equal(4, CompactSessionPresentation.SquareSize);
        Assert.Equal(1, CompactSessionPresentation.Gap);
        Assert.Equal(50, CompactSessionPresentation.RegionWidth);
        Assert.Equal(SessionPresentation.Project(machine, Now).Select(session => session.SessionKey),
            layout.Indicators.Select(indicator => indicator.Key));
        Assert.All(layout.Indicators, indicator =>
        {
            Assert.InRange(indicator.Left + 4, 4, 50);
            Assert.InRange(indicator.Top + 4, 4, Math.Max(4, height));
            Assert.Contains("Waiting for input", indicator.Label);
        });
        Assert.Equal(count, layout.Indicators.Select(indicator => (indicator.Left, indicator.Top)).Distinct().Count());
    }

    [Fact]
    public void OfflineAndEndedSessionsHaveNoConnectedSquares()
    {
        var sessions = new[] { Session("active"), Session("ended") with { LatestEvent = AgentEvent.SessionEnd } };
        Assert.Single(CompactSessionPresentation.Project(Machine(sessions), Now).Indicators);
        var offline = CompactSessionPresentation.Project(Machine(sessions) with { State = AgentState.Offline }, Now);
        Assert.Empty(offline.Indicators);
        Assert.Equal(64, offline.TileHeight);
        Assert.Equal(0, offline.IndicatorHeight);
    }

    [Fact]
    public void AnotherSessionResumesWithoutMovingIndicatorsOrHidingWaitingSquare()
    {
        var a = Session("a");
        var b = Session("b");
        var before = CompactSessionPresentation.Project(Machine(a, b), Now);
        var after = CompactSessionPresentation.Project(Machine(b with
            { UnderlyingState = AgentState.Executing, LatestEvent = AgentEvent.PreToolUse }, a), Now);
        Assert.Equal(before.Indicators.Select(indicator => indicator.Key), after.Indicators.Select(indicator => indicator.Key));
        Assert.Equal(AgentState.Waiting, after.Indicators[0].State);
        Assert.Equal(AgentState.Executing, after.Indicators[1].State);
        Assert.Equal(before.Indicators.Select(indicator => (indicator.Left, indicator.Top)),
            after.Indicators.Select(indicator => (indicator.Left, indicator.Top)));
    }

    private static SessionSnapshot Session(string id) => new()
    {
        Source = new("copilot-cli", "scope"), SessionId = id, UnderlyingState = AgentState.Waiting,
        LatestEvent = AgentEvent.PermissionRequest, LatestEventAtUtc = Now, UpdatedAtUtc = Now
    };
    private static MachineView Machine(params SessionSnapshot[] sessions) =>
        new(Guid.NewGuid(), "synthetic", null, "copilot-cli", "1", AgentState.Waiting, null, Now, sessions);
}
