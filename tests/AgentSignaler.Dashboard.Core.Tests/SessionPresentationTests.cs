using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class SessionPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IdentityOrderingAndEventsAreSessionSpecific()
    {
        var id = Guid.NewGuid();
        var cli = Session("same", new("copilot-cli", "b", "2"), AgentEvent.PermissionRequest);
        var legacy = Session("same", null, AgentEvent.SessionStart);
        var vs = Session("same", new("visual-studio", "a"), AgentEvent.UserPromptSubmitted);
        var code = Session("same", new("vscode", "a"), AgentEvent.PostToolUseFailure);
        var rows = SessionPresentation.Project(Machine(id, [code, vs, legacy, cli]), Now);
        Assert.Equal(new[] { "b", "legacy-cli", "a", "a" }, rows.Select(s => s.Source.ScopeId));
        Assert.Equal(4, rows.Select(s => s.SessionKey).Distinct().Count());
        Assert.All(rows, row => Assert.Equal(id, row.MachineId));
        Assert.All(rows, row => Assert.True(row.IsConnected));
        Assert.Equal(AgentEvent.PermissionRequest, rows[0].LatestEvent);
        Assert.Equal(Now, rows[0].LatestEventAtUtc);
        Assert.Equal(AgentState.Waiting, rows[0].State);
        var upgraded = SessionPresentation.Project(Machine(id, [cli with
        {
            Source = cli.Source! with { Version = "100" }
        }]), Now).Single();
        Assert.Equal(rows[0].SessionKey, upgraded.SessionKey);
    }

    [Fact]
    public void LegacyMetadataStaysUnknownAndOfflineRowsAreNotConnected()
    {
        var session = new SessionSnapshot { SessionId = "quiet", UnderlyingState = AgentState.Waiting, UpdatedAtUtc = Now };
        var machine = Machine(Guid.NewGuid(), [session]) with
        {
            State = AgentState.Offline, LatestEvent = AgentEvent.PostToolUse, LatestEventUtc = Now
        };
        var row = Assert.Single(SessionPresentation.Project(machine, Now.AddDays(1)));
        Assert.Null(row.LatestEvent);
        Assert.Null(row.LatestEventAtUtc);
        Assert.Equal("Last event unavailable", SessionPresentation.EventLabel(row.LatestEvent));
        Assert.True(row.IsOffline);
        Assert.False(row.IsConnected);
        Assert.Equal(AgentState.Waiting, row.State);
        Assert.Contains("Offline", row.LifecycleLabel);
    }

    [Fact]
    public void EndedSessionDoesNotConnectAndWaitSurvivesElapsedSilence()
    {
        var ended = Session("ended", null, AgentEvent.SessionEnd);
        var waiting = Session("quiet", null, AgentEvent.PermissionRequest);
        var rows = SessionPresentation.Project(Machine(Guid.NewGuid(), [waiting, ended]), Now.AddDays(30));
        Assert.True(rows[0].IsEnded);
        Assert.False(rows[0].IsConnected);
        Assert.True(rows[1].IsConnected);
        Assert.Equal(AgentState.Waiting, rows[1].State);
        Assert.Equal(Now, rows[1].LatestEventAtUtc);
    }

    [Fact]
    public void LegacyEndTombstoneKeepsUnknownEventAndNeverContributesAConnectedIndicator()
    {
        var ended = new SessionSnapshot
        {
            SessionId = "legacy-ended", UnderlyingState = AgentState.Idle, UpdatedAtUtc = Now,
            ResultState = AgentState.Succeeded, ResultUntilUtc = Now.AddMinutes(1)
        };
        var row = Assert.Single(SessionPresentation.Project(Machine(Guid.NewGuid(), [ended]), Now));
        Assert.True(row.IsEnded);
        Assert.False(row.IsConnected);
        Assert.Null(row.LatestEvent);
        Assert.Equal("Last event unavailable", SessionPresentation.EventLabel(row.LatestEvent));
        Assert.Equal(AgentState.Idle, StateReducer.Aggregate([ended], Now));
    }

    [Fact]
    public void EventLabelsAreDefinedAndDoNotConflateTurnAndSessionEnd()
    {
        foreach (var kind in Enum.GetValues<AgentEvent>())
            Assert.NotEqual("Last event unavailable", SessionPresentation.EventLabel(kind));
        Assert.NotEqual(SessionPresentation.EventLabel(AgentEvent.AgentStop),
            SessionPresentation.EventLabel(AgentEvent.SessionEnd));
    }

    [Fact]
    public async Task SessionOnlyChangesAdvanceRevisionAndRejectStalePaginationWithoutStartingReceiver()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSignaler-session-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var lease = DashboardResourceLease.Acquire(directory, directory);
            await using var runtime = new DashboardRuntime(lease);
            var id = Guid.NewGuid();
            var a = Session("a", null, AgentEvent.PermissionRequest);
            var b = Session("b", null, AgentEvent.UserPromptSubmitted);
            var machine = Machine(id, [b, a]);
            runtime.PublishMachines([machine]);
            var before = runtime.GetSessions(id, limit: 1);
            var settingsRevision = runtime.Settings.Revision;
            Assert.Equal("a", before.State.Items[0].SessionId);
            runtime.PublishMachines([machine with { Sessions = [a, b] }]);
            Assert.Equal(before.Revision, runtime.GetMachine(id).Revision);
            b = StateReducer.Apply(b, b.SessionId, AgentEvent.PostToolUse,
                Now.AddSeconds(1), Now.AddSeconds(1));
            runtime.PublishMachines([machine with { Sessions = [a, b] }]);
            var after = runtime.GetMachine(id);
            Assert.True(after.Revision > before.Revision);
            Assert.Equal(AgentState.Waiting, after.State.Machine.State);
            Assert.Equal(settingsRevision, runtime.Settings.Revision);
            Assert.Equal(1004, Assert.Throws<RuntimeCommandException>(() =>
                runtime.GetSessions(id, 1, 1, before.Revision)).Error.Code);
            var next = runtime.GetSessions(id, 1, 1, after.Revision);
            Assert.Equal(AgentEvent.PostToolUse, next.State.Items[0].LatestEvent);
            Assert.Equal(Now.AddSeconds(1), next.State.Items[0].LatestEventAtUtc);
            Assert.Null(runtime.Server);
            runtime.PublishMachines([]);
            Assert.Equal(1002, Assert.Throws<RuntimeCommandException>(() => runtime.GetMachine(id)).Error.Code);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SessionSnapshot Session(string id, SourceDescriptor? source, AgentEvent kind) =>
        StateReducer.Apply(null, id, kind, Now, Now, source: source);

    private static MachineView Machine(Guid id, IReadOnlyList<SessionSnapshot> sessions) =>
        new(id, "synthetic", null, "copilot-cli", "synthetic", AgentState.Waiting,
            null, Now, sessions);
}
