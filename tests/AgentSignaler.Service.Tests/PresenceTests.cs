using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service.Tests;

public sealed class PresenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"presence-tests-{Guid.NewGuid()}");
    private readonly ManualTimeProvider _clock = new();
    private readonly MachineStore _store;
    private readonly Guid _machine = Guid.NewGuid();
    private string Database => Path.Combine(_directory, "state.db");
    public PresenceTests() => _store = new MachineStore(Database, _clock);

    internal static PresenceReport Started(Guid machine, DateTimeOffset now) => new()
    {
        Kind = PresenceKind.Started, EventId = Guid.NewGuid(), MachineId = machine,
        MachineName = "test-machine", ClientVersion = "1.0", Generation = 1, Sequence = 1,
        ReportedAtUtc = now, HeartbeatIntervalSeconds = 300, Sessions = []
    };

    private PresenceReport Report(PresenceKind kind, long sequence, long generation = 1) =>
        Started(_machine, _clock.Now) with
        {
            Kind = kind, Sequence = sequence, Generation = generation,
            HeartbeatIntervalSeconds = kind is PresenceKind.Started or PresenceKind.Heartbeat ? 300 : null,
            Sessions = kind is PresenceKind.Started or PresenceKind.Heartbeat ? [] : null
        };

    private PresenceReport Hook(AgentEvent kind, long sequence, string session = "session", DateTimeOffset? timestamp = null)
    {
        var report = Report(PresenceKind.Hook, sequence) with { ReportedAtUtc = timestamp ?? _clock.Now };
        return report with { Hook = new StatusRequest
        {
            EventId = report.EventId, MachineId = report.MachineId, MachineName = report.MachineName,
            ClientVersion = report.ClientVersion, ReportedAtUtc = report.ReportedAtUtc,
            SessionId = session, Event = kind
        } };
    }

    [Theory]
    [InlineData(60, 180)]
    [InlineData(300, 660)]
    [InlineData(3600, 7260)]
    public async Task OfflineAtExactDeadlineAndCurrentHooksRenewContact(int interval, int deadline)
    {
        await _store.AcceptAsync(Report(PresenceKind.Started, 1) with { HeartbeatIntervalSeconds = interval });
        var first = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(AgentState.Idle, first.State);
        Assert.Null(first.LatestEvent);
        Assert.Equal(_clock.Now.AddSeconds(deadline), first.OfflineDeadlineUtc);
        _clock.Advance(TimeSpan.FromSeconds(deadline) - TimeSpan.FromTicks(1));
        Assert.Equal(AgentState.Idle, Assert.Single(await _store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(AgentState.Offline, Assert.Single(await _store.GetMachinesAsync()).State);
        await _store.AcceptAsync(Hook(AgentEvent.PreToolUse, 2));
        var renewed = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(AgentState.Executing, renewed.State);
        Assert.Equal(_clock.Now, renewed.LastContactUtc);
    }

    [Fact]
    public async Task OrderingAndTerminalOfflineSurviveReopenAndPreserveDetails()
    {
        var start = Report(PresenceKind.Started, 1);
        await _store.AcceptAsync(start);
        await _store.UpdateDetailsAsync(_machine, "Custom name", "Keep this note");
        await _store.AcceptAsync(Hook(AgentEvent.ErrorOccurred, 2));
        var offline = Report(PresenceKind.Offline, 4);
        await _store.AcceptAsync(offline);
        _clock.Advance(TimeSpan.FromMinutes(1));
        using var reopened = new MachineStore(Database, _clock);
        Assert.True((await reopened.AcceptAsync(start)).Duplicate);
        Assert.True((await reopened.AcceptAsync(Report(PresenceKind.Heartbeat, 3))).Duplicate);
        Assert.True((await reopened.AcceptAsync(offline)).Duplicate);
        await Assert.ThrowsAsync<PresenceConflictException>(() => reopened.AcceptAsync(Report(PresenceKind.Heartbeat, 5)));
        await Assert.ThrowsAsync<PresenceConflictException>(() => reopened.AcceptAsync(Hook(AgentEvent.SessionEnd, 6).Hook!));
        var terminal = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(AgentState.Offline, terminal.State);
        Assert.True(terminal.ExplicitOffline);
        Assert.Single(terminal.Sessions);
        Assert.Equal(offline.ReportedAtUtc, terminal.LastContactUtc);
        await reopened.AcceptAsync(Report(PresenceKind.Started, 1, 2));
        var restarted = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(AgentState.Idle, restarted.State);
        Assert.Empty(restarted.Sessions);
        Assert.False(restarted.ExplicitOffline);
        Assert.Equal("Custom name", restarted.DisplayName);
        Assert.Equal("Keep this note", restarted.Note);
        Assert.True((await reopened.AcceptAsync(Report(PresenceKind.Offline, 999))).Duplicate);
        Assert.Equal(AgentState.Idle, Assert.Single(await reopened.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task InvalidLifecycleAndMalformedReportsNeverAdvanceWatermark()
    {
        await Assert.ThrowsAsync<PresenceConflictException>(() => _store.AcceptAsync(Report(PresenceKind.Heartbeat, 1)));
        Assert.Empty(await _store.GetMachinesAsync());
        await _store.AcceptAsync(Report(PresenceKind.Started, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AcceptAsync(Report(PresenceKind.Heartbeat, 100) with { HeartbeatIntervalSeconds = 61 }));
        await Assert.ThrowsAsync<PresenceConflictException>(() => _store.AcceptAsync(Report(PresenceKind.Started, 101)));
        await Assert.ThrowsAsync<PresenceConflictException>(() => _store.AcceptAsync(Report(PresenceKind.Heartbeat, 1, 2)));
        Assert.False((await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 2))).Duplicate);
        Assert.Equal(2, Assert.Single(await _store.GetMachinesAsync()).Sequence);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedSnapshotTimesDoNotAdvanceContactOrSequence(bool updatedAfterEnvelope)
    {
        await _store.AcceptAsync(Report(PresenceKind.Started, 1));
        var before = Assert.Single(await _store.GetMachinesAsync());
        _clock.Advance(TimeSpan.FromMinutes(1));
        var session = new SessionSnapshot
        {
            SessionId = "result", UnderlyingState = AgentState.Waiting,
            UpdatedAtUtc = _clock.Now,
            ResultState = AgentState.Succeeded, ResultUntilUtc = _clock.Now + Protocol.ResultDuration
        };
        var valid = Report(PresenceKind.Heartbeat, 2) with { Sessions = [session] };
        var malformed = valid with
        {
            Sessions = [updatedAfterEnvelope
                ? session with { UpdatedAtUtc = valid.ReportedAtUtc.AddTicks(1) }
                : session with { ResultUntilUtc = session.ResultUntilUtc!.Value.AddTicks(1) }]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => _store.AcceptAsync(malformed));
        var unchanged = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(before.Sequence, unchanged.Sequence);
        Assert.Equal(before.LastContactUtc, unchanged.LastContactUtc);
        Assert.Empty(unchanged.Sessions);
        Assert.False((await _store.AcceptAsync(valid)).Duplicate);
        Assert.Equal(_clock.Now, Assert.Single(await _store.GetMachinesAsync()).LastContactUtc);
    }

    [Fact]
    public async Task FutureSourceTimelineIsAcceptedWithoutUsingWallClockForOrdering()
    {
        var future = _clock.Now.AddYears(1);
        var result = new SessionSnapshot
        {
            SessionId = "future", UnderlyingState = AgentState.Waiting, UpdatedAtUtc = future,
            ResultState = AgentState.Failed, ResultUntilUtc = future + Protocol.ResultDuration
        };
        await _store.AcceptAsync(Report(PresenceKind.Started, 1) with { ReportedAtUtc = future, Sessions = [result] });
        var machine = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(AgentState.Failed, machine.State);
        Assert.Equal(_clock.Now, machine.LastContactUtc);
        Assert.Equal(_clock.Now + Protocol.ResultDuration, Assert.Single(machine.Sessions).ResultUntilUtc);
        Assert.Equal(future, Assert.Single(machine.Sessions).UpdatedAtUtc);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 2));
        Assert.Equal(2, Assert.Single(await _store.GetMachinesAsync()).Sequence);
        Assert.Empty(Assert.Single(await _store.GetMachinesAsync()).Sessions);
    }

    [Fact]
    public async Task RepeatedHeartbeatsUseBoundedStorageAndDoNotExtendResultsOrReplaceLatestHook()
    {
        await _store.AcceptAsync(Report(PresenceKind.Started, 1));
        await _store.AcceptAsync(Hook(AgentEvent.AgentStop, 2));
        var original = Assert.Single(await _store.GetMachinesAsync());
        for (var sequence = 3; sequence <= 100; sequence++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _store.AcceptAsync(Report(PresenceKind.Heartbeat, sequence) with
            {
                Sessions = original.Sessions.Select(s => PresenceProtocol.ProjectSession(s, PresenceProtocol.Version)).ToArray()
            });
        }
        var machine = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(AgentState.Waiting, machine.State);
        Assert.Equal(original.LatestEvent, machine.LatestEvent);
        Assert.Equal(original.LatestEventUtc, machine.LatestEventUtc);
        Assert.Equal(original.Sessions[0].ResultUntilUtc, machine.Sessions[0].ResultUntilUtc);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Receipts";
        Assert.Equal(0L, command.ExecuteScalar());
        command.CommandText = "SELECT length(Snapshot) FROM Machines";
        Assert.InRange((long)command.ExecuteScalar()!, 1, 2000);
    }

    [Fact]
    public async Task SnapshotsReconcileMissingSessionsPreserveWaitingAndRejectLateResurrection()
    {
        await _store.AcceptAsync(Report(PresenceKind.Started, 1));
        var asking = new SessionSnapshot
        {
            SessionId = "asking", UnderlyingState = AgentState.Waiting,
            AwaitingUserInput = true, UpdatedAtUtc = _clock.Now
        };
        await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 2) with { Sessions = [asking] });
        Assert.Equal(AgentState.Waiting, Assert.Single(await _store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 3) with
        {
            Sessions = [asking with { UnderlyingState = AgentState.Idle, AwaitingUserInput = false, UpdatedAtUtc = asking.UpdatedAtUtc.AddSeconds(-1) }]
        });
        Assert.True(Assert.Single(Assert.Single(await _store.GetMachinesAsync()).Sessions).AwaitingUserInput);
        await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 4));
        await _store.AcceptAsync(Hook(AgentEvent.PreToolUse, 5, "asking", asking.UpdatedAtUtc));
        Assert.Empty(Assert.Single(await _store.GetMachinesAsync()).Sessions);
        await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 6) with { Sessions = [asking] });
        Assert.Empty(Assert.Single(await _store.GetMachinesAsync()).Sessions);
        await _store.AcceptAsync(Hook(AgentEvent.PreToolUse, 7, "asking"));
        Assert.Equal(AgentState.Executing, Assert.Single(await _store.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task FailedPersistenceRollsBackOrderingAndAllowsExactRetry()
    {
        await _store.AcceptAsync(Report(PresenceKind.Started, 1));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER FailUpdate BEFORE UPDATE ON Machines BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
        command.ExecuteNonQuery();
        var heartbeat = Report(PresenceKind.Heartbeat, 2);
        await Assert.ThrowsAsync<SqliteException>(() => _store.AcceptAsync(heartbeat));
        Assert.Equal(1, Assert.Single(await _store.GetMachinesAsync()).Sequence);
        command.CommandText = "DROP TRIGGER FailUpdate";
        command.ExecuteNonQuery();
        Assert.False((await _store.AcceptAsync(heartbeat)).Duplicate);
    }

    [Fact]
    public async Task SessionCapacityFailureDoesNotCommitAndSnapshotCanRepairIt()
    {
        var full = EnrichedPresenceTests.BoundedSnapshot(Enumerable.Range(0, Protocol.MaxSessions).Select(i => new SessionSnapshot
        {
            SessionId = $"session-{i}", UnderlyingState = AgentState.Executing, UpdatedAtUtc = _clock.Now
        }));
        await _store.AcceptAsync(Report(PresenceKind.Started, 1) with { Sessions = full });
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<CapacityException>(() => _store.AcceptAsync(Hook(AgentEvent.PreToolUse, 3, "overflow")));
        Assert.Equal(1, Assert.Single(await _store.GetMachinesAsync()).Sequence);
        await _store.AcceptAsync(Report(PresenceKind.Heartbeat, 2));
        Assert.False((await _store.AcceptAsync(Hook(AgentEvent.PreToolUse, 3, "overflow"))).Duplicate);
        Assert.Single(Assert.Single(await _store.GetMachinesAsync()).Sessions);
    }

    [Fact]
    public async Task ManagedPresenceDoesNotConsumeOrDependOnLegacyReceiptCapacity()
    {
        using var limited = new MachineStore(Database, _clock, new MachineStoreOptions { ReceiptLimit = 1 });
        await limited.AcceptAsync(Hook(AgentEvent.SessionStart, 1).Hook!);
        await limited.AcceptAsync(Report(PresenceKind.Started, 1));
        for (var i = 2; i < 20; i++) await limited.AcceptAsync(Report(PresenceKind.Heartbeat, i));
        await Assert.ThrowsAsync<ReceiptCapacityException>(() => limited.AcceptAsync(
            Hook(AgentEvent.SessionStart, 1).Hook! with { MachineId = Guid.NewGuid() }));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Total FROM ReceiptCount WHERE Id=1";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task MachineLimitAndConcurrentStoresPreserveWatermarks()
    {
        await _store.AcceptAsync(Report(PresenceKind.Started, 1));
        using var other = new MachineStore(Database, _clock);
        await Task.WhenAll(
            Task.Run(() => _store.AcceptAsync(Report(PresenceKind.Heartbeat, 3))),
            Task.Run(() => other.AcceptAsync(Report(PresenceKind.Heartbeat, 2))));
        Assert.Equal(3, Assert.Single(await _store.GetMachinesAsync()).Sequence);
        for (var i = 1; i < 25; i++) await _store.AcceptAsync(Started(Guid.NewGuid(), _clock.Now));
        await Assert.ThrowsAsync<CapacityException>(() => _store.AcceptAsync(Started(Guid.NewGuid(), _clock.Now)));
        Assert.Equal(25, (await _store.GetMachinesAsync()).Count);
    }

    [Fact]
    public async Task LegacySnapshotAndHistoricalHeartbeatMigrationRetainFiveMinutePolicy()
    {
        var hook = Hook(AgentEvent.SessionStart, 1).Hook!;
        await _store.AcceptAsync(hook);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Snapshot FROM Machines";
        var snapshot = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
        foreach (var field in new[] { "presenceMode", "heartbeatIntervalSeconds", "generation", "sequence", "explicitOffline" })
            snapshot.Remove(field);
        snapshot["latestEvent"] = "heartbeat";
        snapshot["heartbeatWatermarkUtc"] = _clock.Now;
        snapshot.Remove("retiredThroughUtc");
        command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
        command.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
        command.ExecuteNonQuery();
        using var reopened = new MachineStore(Database, _clock);
        _clock.Advance(TimeSpan.FromMinutes(5));
        var old = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(PresenceMode.Legacy, old.PresenceMode);
        Assert.Null(old.LatestEvent);
        Assert.Equal(AgentState.Offline, old.State);
        await reopened.AcceptAsync(Report(PresenceKind.Started, 1));
        Assert.Equal(PresenceMode.Managed, Assert.Single(await reopened.GetMachinesAsync()).PresenceMode);
        await Assert.ThrowsAsync<PresenceConflictException>(() => reopened.AcceptAsync(hook));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(61)]
    [InlineData(3599)]
    [InlineData(3601)]
    public void InvalidIntervalsRejected(int interval) =>
        Assert.NotEmpty(PresenceProtocol.Validate(Report(PresenceKind.Started, 1) with { HeartbeatIntervalSeconds = interval }));

    [Fact]
    public void StrictPerKindValidationAndSnapshotBounds()
    {
        var start = Report(PresenceKind.Started, 1);
        Assert.Empty(PresenceProtocol.Validate(start));
        foreach (var invalid in new[]
        {
            start with { Generation = 0 }, start with { Sequence = 0 }, start with { Sessions = null },
            start with { Hook = Hook(AgentEvent.SessionStart, 1).Hook },
            start with { ReportedAtUtc = _clock.Now.ToOffset(TimeSpan.FromHours(1)) },
            Report(PresenceKind.Offline, 2) with { Sessions = [] },
            Report(PresenceKind.Hook, 2),
            Hook(AgentEvent.SessionStart, 2) with { MachineName = "mismatch" },
            start with { Sessions = Enumerable.Range(0, 65).Select(i => new SessionSnapshot { SessionId = i.ToString(), UpdatedAtUtc = _clock.Now }).ToArray() },
            start with { Sessions = [new() { SessionId = "a", UpdatedAtUtc = _clock.Now }, new() { SessionId = "a", UpdatedAtUtc = _clock.Now }] },
            start with { Sessions = [new() { SessionId = "a", UnderlyingState = AgentState.Offline, UpdatedAtUtc = _clock.Now }] }
        }) Assert.NotEmpty(PresenceProtocol.Validate(invalid));
        var json = JsonSerializer.Serialize(start, Protocol.Json);
        Assert.Equal(start.Kind, JsonSerializer.Deserialize<PresenceReport>(json, PresenceProtocol.Json)!.Kind);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PresenceReport>(json.Replace("\"sequence\":1", "\"sequence\":1,\"sequence\":2"), PresenceProtocol.Json));
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={Database};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        _store.Dispose();
        Directory.Delete(_directory, true);
    }
}
