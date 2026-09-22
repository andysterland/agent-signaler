using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class CompactSessionPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FriendlyLabelsDoNotChangeIndicatorKeysOrPositions()
    {
        var a = Session("a");
        var b = Session("b");
        var before = CompactSessionPresentation.Project(Machine(a, b), Now);
        var after = CompactSessionPresentation.Project(Machine(
            a with { DisplayName = "Z name" }, b with { DisplayName = "A name" }), Now);
        Assert.Contains("session a:", before.Indicators[0].Label);
        Assert.Contains("session Z name:", after.Indicators[0].Label);
        Assert.Contains("session A name:", after.Indicators[1].Label);
        Assert.Equal(before.Indicators.Select(item => (item.Key, item.Left, item.Top)),
            after.Indicators.Select(item => (item.Key, item.Left, item.Top)));
    }

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

    [Theory]
    [InlineData(AgentEvent.SessionStart)]
    [InlineData(AgentEvent.AgentStop)]
    [InlineData(AgentEvent.ExecutionStopped)]
    public void BetweenTurnWaitHidesAtFiveMinutesWithoutRemovingSession(AgentEvent kind)
    {
        var session = StateReducer.Apply(null, "quiet", kind, Now, Now);
        var machine = Machine(session);
        var deadline = Now.AddMinutes(5);
        Assert.Single(CompactSessionPresentation.Project(machine, deadline.AddTicks(-1)).Indicators);
        var hidden = CompactSessionPresentation.Project(machine, deadline);
        Assert.Empty(hidden.Indicators);
        Assert.Equal(0, hidden.IndicatorHeight);
        Assert.Equal(64, hidden.TileHeight);
        Assert.Empty(CompactSessionPresentation.Project(machine, deadline.AddTicks(1)).Indicators);
        Assert.Same(session, Assert.Single(machine.Sessions));
        Assert.True(Assert.Single(SessionPresentation.Project(machine, deadline)).IsConnected);
        Assert.Single(CompactSessionPresentation.Project(machine, deadline, hideInactive: false).Indicators);
    }

    [Theory]
    [InlineData(AgentEvent.UserPromptSubmitted)]
    [InlineData(AgentEvent.PreToolUse)]
    [InlineData(AgentEvent.PermissionRequest)]
    [InlineData(AgentEvent.AgentStop)]
    public void NewActivityRestoresHiddenSessionAndResetsTimeout(AgentEvent kind)
    {
        var session = StateReducer.Apply(null, "quiet", AgentEvent.AgentStop, Now, Now);
        var machine = Machine(session);
        var before = Assert.Single(CompactSessionPresentation.Project(machine, Now).Indicators);
        var resumedAt = Now.AddMinutes(6);
        Assert.Empty(CompactSessionPresentation.Project(machine, resumedAt).Indicators);

        var resumed = StateReducer.Apply(session, session.SessionId, kind, resumedAt, resumedAt);
        var updated = machine with { Sessions = new[] { resumed } };
        var indicator = Assert.Single(CompactSessionPresentation.Project(updated, resumedAt).Indicators);
        Assert.Equal(before.Key, indicator.Key);
        Assert.Equal(StateReducer.Effective(resumed, resumedAt), indicator.State);

        var completedAt = resumedAt.AddSeconds(1);
        var completed = StateReducer.Apply(resumed, session.SessionId, AgentEvent.AgentStop, completedAt, completedAt);
        updated = machine with { Sessions = new[] { completed } };
        Assert.Single(CompactSessionPresentation.Project(updated, completedAt.AddMinutes(5).AddTicks(-1)).Indicators);
        Assert.Empty(CompactSessionPresentation.Project(updated, completedAt.AddMinutes(5)).Indicators);
    }

    [Theory]
    [InlineData(AgentEvent.UserPromptSubmitted)]
    [InlineData(AgentEvent.PreToolUse)]
    [InlineData(AgentEvent.PermissionRequest)]
    [InlineData(AgentEvent.ErrorOccurred)]
    [InlineData(AgentEvent.PostToolUseFailure)]
    public void ExecutingExplicitPermissionAndErrorStatesDoNotTimeOut(AgentEvent kind)
    {
        var session = StateReducer.Apply(null, "active", kind, Now, Now);
        Assert.Single(CompactSessionPresentation.Project(Machine(session), Now.AddDays(1)).Indicators);
    }

    [Fact]
    public void PendingToolInputAndUnknownLegacyWaitsDoNotTimeOut()
    {
        var pending = StateReducer.Apply(null, "pending", AgentEvent.PreToolUse, Now, Now, toolRequiresUserInput: true);
        pending = StateReducer.Apply(pending, pending.SessionId, AgentEvent.PostToolUse, Now.AddSeconds(1), Now.AddSeconds(1));
        var legacy = Session("legacy") with { LatestEvent = null, LatestEventAtUtc = null };
        Assert.Equal(2, CompactSessionPresentation.Project(Machine(pending, legacy), Now.AddDays(1)).Indicators.Length);
    }

    [Fact]
    public void HeartbeatsAndOtherSessionActivityDoNotRenewTimeout()
    {
        var quiet = StateReducer.Apply(null, "quiet", AgentEvent.AgentStop, Now, Now);
        var active = Session("active");
        var machine = Machine(quiet, active);
        var later = Now.AddMinutes(5);
        var heartbeat = machine with { LastContactUtc = later };
        Assert.Single(CompactSessionPresentation.Project(heartbeat, later).Indicators);
        var updated = heartbeat with
        {
            Sessions = new[] { quiet, StateReducer.Apply(active, active.SessionId, AgentEvent.PreToolUse, later, later) }
        };
        var indicator = Assert.Single(CompactSessionPresentation.Project(updated, later).Indicators);
        Assert.Equal(SourceIdentity.SessionKey(active.Source, active.SessionId), indicator.Key);
        Assert.Equal(AgentState.Executing, indicator.State);
    }

    [Fact]
    public void TimeoutReflowsGridAndReturningSessionKeepsIdentityOrder()
    {
        var quiet = StateReducer.Apply(null, "a", AgentEvent.AgentStop, Now, Now, source: new("copilot-cli", "scope"));
        var sessions = new[] { quiet }.Concat(Enumerable.Range(0, 10).Select(i => Session($"b-{i}"))).ToArray();
        var machine = Machine(sessions);
        var before = CompactSessionPresentation.Project(machine, Now);
        var later = Now.AddMinutes(5);
        var hidden = CompactSessionPresentation.Project(machine, later);
        Assert.Equal(75, before.TileHeight);
        Assert.Equal(70, hidden.TileHeight);
        Assert.Equal(before.Indicators.Skip(1).Select(indicator => indicator.Key), hidden.Indicators.Select(indicator => indicator.Key));
        Assert.All(hidden.Indicators, indicator => Assert.Equal(0, indicator.Top));

        var resumed = StateReducer.Apply(quiet, quiet.SessionId, AgentEvent.UserPromptSubmitted, later, later);
        var restored = CompactSessionPresentation.Project(machine with { Sessions = new[] { resumed }.Concat(sessions.Skip(1)).ToArray() }, later);
        Assert.Equal(before.TileHeight, restored.TileHeight);
        Assert.Equal(before.Indicators.Select(indicator => (indicator.Key, indicator.Left, indicator.Top)),
            restored.Indicators.Select(indicator => (indicator.Key, indicator.Left, indicator.Top)));
    }

    private static SessionSnapshot Session(string id) => new()
    {
        Source = new("copilot-cli", "scope"), SessionId = id, UnderlyingState = AgentState.Waiting,
        LatestEvent = AgentEvent.PermissionRequest, LatestEventAtUtc = Now, UpdatedAtUtc = Now
    };
    private static MachineView Machine(params SessionSnapshot[] sessions) =>
        new(Guid.NewGuid(), "synthetic", null, "copilot-cli", "1", AgentState.Waiting, null, Now, sessions);
}
