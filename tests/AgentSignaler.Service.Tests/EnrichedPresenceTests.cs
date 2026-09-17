using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service.Tests;

public sealed class EnrichedPresenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), $"enriched-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new();
    private readonly Guid _machine = Guid.NewGuid();
    private static readonly SourceDescriptor Source = new("vscode", "scope", "1");
    private string Database => Path.Combine(_root, "state.db");

    private PresenceReport Snapshot(long sequence, params SessionSnapshot[] sessions) =>
        PresenceTests.Started(_machine, _clock.Now) with
        {
            ProtocolVersion = PresenceProtocol.EnrichedVersion, Client = "agent-signaler",
            Kind = sequence == 1 ? PresenceKind.Started : PresenceKind.Heartbeat,
            Sequence = sequence, Sessions = sessions
        };

    private PresenceReport Hook(long sequence, string id, AgentEvent kind)
    {
        var report = Snapshot(sequence) with { Kind = PresenceKind.Hook, Sessions = null, HeartbeatIntervalSeconds = null };
        return report with { Hook = new()
        {
            ProtocolVersion = PresenceProtocol.SourceVersion, EventId = report.EventId, MachineId = _machine,
            MachineName = report.MachineName, Source = Source, Client = Source.Kind, ClientVersion = Source.Version,
            SessionId = id, Event = kind, ReportedAtUtc = _clock.Now
        } };
    }

    [Fact]
    public void EnrichedMetadataIsStrictAndLegacyProjectionsNeverEraseSourceIdentity()
    {
        var session = StateReducer.Apply(null, "a", AgentEvent.PermissionRequest, _clock.Now, _clock.Now, source: Source);
        var report = Snapshot(1, session);
        Assert.Empty(PresenceProtocol.Validate(report));
        var json = JsonSerializer.Serialize(report, PresenceProtocol.Json);
        Assert.Equal(session, Assert.Single(JsonSerializer.Deserialize<PresenceReport>(json, PresenceProtocol.Json)!.Sessions!));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PresenceReport>(
            json.Replace("\"latestEvent\":", "\"latestEvent\":\"sessionStart\",\"latestEvent\":"), PresenceProtocol.Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PresenceReport>(
            json.Replace("\"latestEvent\":", "\"prompt\":\"private\",\"latestEvent\":"), PresenceProtocol.Json));
        foreach (var version in new[] { PresenceProtocol.Version, PresenceProtocol.SourceVersion })
        {
            var old = report with { ProtocolVersion = version };
            Assert.NotEmpty(PresenceProtocol.Validate(old));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PresenceReport>(
                JsonSerializer.Serialize(old, Protocol.Json), PresenceProtocol.Json));
        }
        var legacy = PresenceProtocol.Project(report, PresenceProtocol.SourceVersion);
        Assert.Empty(PresenceProtocol.Validate(legacy));
        Assert.Equal(Source, Assert.Single(legacy.Sessions!).Source);
        Assert.Null(legacy.Sessions![0].LatestEvent);
        Assert.DoesNotContain("latestEvent", JsonSerializer.Serialize(legacy, PresenceProtocol.Json));
        Assert.Throws<InvalidOperationException>(() => PresenceProtocol.Project(report, PresenceProtocol.Version));
        Assert.Throws<InvalidOperationException>(() => PresenceProtocol.ProjectSession(session, Protocol.Version));
        foreach (var invalid in new[]
        {
            session with { LatestEvent = (AgentEvent)99 },
            session with { LatestEventAtUtc = null },
            session with { LatestEvent = null },
            session with { LatestEventAtUtc = _clock.Now.AddTicks(1) },
            session with { LatestEventAtUtc = _clock.Now.ToOffset(TimeSpan.FromHours(1)) }
        }) Assert.NotEmpty(PresenceProtocol.Validate(Snapshot(1, invalid)));
    }

    [Fact]
    public async Task IncrementalAndSnapshotRecoveryMatchAndNeverBorrowMachineEvent()
    {
        using var incremental = new MachineStore(Database, _clock);
        using var recovered = new MachineStore(Path.Combine(_root, "recovered.db"), _clock);
        await incremental.AcceptAsync(Snapshot(1));
        SessionSnapshot? a = null, b = null;
        long sequence = 1;
        foreach (var (id, kind) in new[]
        {
            ("a", AgentEvent.PermissionRequest), ("b", AgentEvent.ErrorOccurred),
            ("b", AgentEvent.UserPromptSubmitted), ("b", AgentEvent.AgentStop)
        })
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            var report = Hook(++sequence, id, kind);
            await incremental.AcceptAsync(report);
            var state = StateReducer.Apply(id == "a" ? a : b, id, kind, _clock.Now, _clock.Now, source: Source);
            if (id == "a") a = state; else b = state;
        }
        await recovered.AcceptAsync(Snapshot(1, a!, b!));
        var before = Assert.Single(await incremental.GetMachinesAsync());
        var after = Assert.Single(await recovered.GetMachinesAsync());
        Assert.Equal(before.Sessions, after.Sessions);
        Assert.Equal(AgentState.Waiting, after.State);
        Assert.Equal(AgentEvent.PermissionRequest, after.Sessions[0].LatestEvent);
        var duplicate = Hook(sequence, "a", AgentEvent.UserPromptSubmitted);
        Assert.True((await incremental.AcceptAsync(duplicate)).Duplicate);
        _clock.Advance(TimeSpan.FromMinutes(10));
        await incremental.AcceptAsync(Snapshot(++sequence, a!, b!));
        using var reopened = new MachineStore(Database, _clock);
        Assert.Equal(before.Sessions, Assert.Single(await reopened.GetMachinesAsync()).Sessions);
        Assert.Equal(AgentState.Waiting, Assert.Single(await reopened.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task ReceiverCapacityRejectsAdmissionWithoutCommittingAndRetiresEndedFirst()
    {
        using var store = new MachineStore(Database, _clock);
        var sessions = BoundedSnapshot(Enumerable.Range(0, Protocol.MaxSessions).Select(i => StateReducer.Apply(null, i.ToString(),
            AgentEvent.PermissionRequest, _clock.Now, _clock.Now, source: Source)));
        await store.AcceptAsync(Snapshot(1, sessions));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<CapacityException>(() => store.AcceptAsync(Hook(2, "overflow", AgentEvent.SessionStart)));
        Assert.Equal(sessions, Assert.Single(await store.GetMachinesAsync()).Sessions);
        Assert.Equal(1, Assert.Single(await store.GetMachinesAsync()).Sequence);
        var retiringId = sessions[^1].SessionId;
        await store.AcceptAsync(Hook(2, retiringId, AgentEvent.SessionEnd));
        var endedAt = _clock.Now;
        _clock.Advance(TimeSpan.FromSeconds(1));
        await store.AcceptAsync(Hook(3, "overflow", AgentEvent.SessionStart));
        Assert.Equal(sessions.Length, Assert.Single(await store.GetMachinesAsync()).Sessions.Count);
        var stale = Hook(4, retiringId, AgentEvent.UserPromptSubmitted);
        await store.AcceptAsync(stale with
        {
            ReportedAtUtc = endedAt, Hook = stale.Hook! with { ReportedAtUtc = endedAt }
        });
        Assert.DoesNotContain(Assert.Single(await store.GetMachinesAsync()).Sessions, s => s.SessionId == retiringId);
        Assert.Equal(AgentState.Waiting, Assert.Single(await store.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task DelayedResultsAndTerminalMetadataAreEquivalentToFullSnapshots()
    {
        using var incremental = new MachineStore(Database, _clock);
        using var recovered = new MachineStore(Path.Combine(_root, "delayed.db"), _clock);
        await incremental.AcceptAsync(Snapshot(1));
        var result = Hook(2, "a", AgentEvent.ErrorOccurred);
        var session = StateReducer.Apply(null, "a", AgentEvent.ErrorOccurred, _clock.Now, _clock.Now, source: Source);
        _clock.Advance(TimeSpan.FromMinutes(2));
        await incremental.AcceptAsync(result);
        await recovered.AcceptAsync(Snapshot(1, session));
        Assert.Equal(Assert.Single(await incremental.GetMachinesAsync()).Sessions,
            Assert.Single(await recovered.GetMachinesAsync()).Sessions);
        var end = Hook(3, "a", AgentEvent.SessionEnd);
        await incremental.AcceptAsync(end);
        session = StateReducer.Apply(session, "a", AgentEvent.SessionEnd, _clock.Now, _clock.Now, source: Source);
        await recovered.AcceptAsync(Snapshot(2, session));
        Assert.Equal(Assert.Single(await incremental.GetMachinesAsync()).Sessions,
            Assert.Single(await recovered.GetMachinesAsync()).Sessions);
        Assert.Equal(AgentState.Idle, Assert.Single(await recovered.GetMachinesAsync()).State);
    }

    [Fact]
    public async Task DirectLegacyIngestionAlsoReservesMetadataBytesWithoutEvictingQuietSessions()
    {
        using var store = new MachineStore(Database, _clock);
        var count = 0;
        while (count < 65)
        {
            var request = StateTests.Request(AgentEvent.SessionStart, _machine, _clock.Now) with
            {
                SessionId = new string('\uFFFF', 120) + count
            };
            try { await store.AcceptAsync(request); count++; }
            catch (CapacityException) { break; }
        }
        Assert.InRange(count, 1, 63);
        var sessions = Assert.Single(await store.GetMachinesAsync()).Sessions;
        Assert.Equal(count, sessions.Count);
        _clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var session in sessions)
            await store.AcceptAsync(StateTests.Request(AgentEvent.ErrorOccurred, _machine, _clock.Now) with { SessionId = session.SessionId });
        Assert.Equal(count, Assert.Single(await store.GetMachinesAsync()).Sessions.Count);
    }

    [Fact]
    public async Task MetadataPersistenceIsTransactionalAndRollbackReadableWithoutLosingDetails()
    {
        using var store = new MachineStore(Database, _clock);
        await store.AcceptAsync(Snapshot(1));
        await store.AcceptAsync(Hook(2, "a", AgentEvent.PermissionRequest));
        await store.UpdateDetailsAsync(_machine, "Custom", "Keep");
        var original = Assert.Single(await store.GetMachinesAsync());
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Snapshot FROM Machines";
        var legacy = JsonNode.Parse((string)command.ExecuteScalar()!)!;
        Assert.Equal(2, legacy["schemaVersion"]!.GetValue<int>());
        Assert.Null(legacy["sessions"]![0]!["latestEvent"]);
        command.CommandText = """
            CREATE TRIGGER RejectMetadata BEFORE UPDATE ON SessionMetadataV1
            BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
            """;
        command.ExecuteNonQuery();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var next = Hook(3, "a", AgentEvent.UserPromptSubmitted);
        await Assert.ThrowsAsync<SqliteException>(() => store.AcceptAsync(next));
        Assert.Equal(original.Sessions, Assert.Single(await store.GetMachinesAsync()).Sessions);
        Assert.Equal(2, Assert.Single(await store.GetMachinesAsync()).Sequence);
        command.CommandText = "DROP TRIGGER RejectMetadata";
        command.ExecuteNonQuery();
        Assert.False((await store.AcceptAsync(next)).Duplicate);
        // An older binary only updates the unchanged v2 table. Stale enrichment is never resurrected.
        legacy["note"] = "Edited by rollback";
        command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
        command.Parameters.AddWithValue("$snapshot", legacy.ToJsonString());
        command.ExecuteNonQuery();
        using var reopened = new MachineStore(Database, _clock);
        var rolledBack = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal("Edited by rollback", rolledBack.Note);
        Assert.Equal("Custom", rolledBack.DisplayName);
        Assert.Null(Assert.Single(rolledBack.Sessions).LatestEvent);
        Assert.Equal(AgentState.Waiting, rolledBack.State);
    }

    [Theory]
    [InlineData(AgentEvent.ErrorOccurred)]
    [InlineData(AgentEvent.AgentStop)]
    public async Task PermissionPersistenceRetainsCanonicalOverlaySeparatelyFromLegacyWireProjection(AgentEvent result)
    {
        var original = StateReducer.Apply(null, "a", result, _clock.Now, _clock.Now, source: Source);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var pending = StateReducer.Apply(original, "a", AgentEvent.PermissionRequest, _clock.Now, _clock.Now, source: Source);
        using (var store = new MachineStore(Database, _clock))
        {
            await store.AcceptAsync(Snapshot(1, pending));
            await store.UpdateDetailsAsync(_machine, "Custom", "Keep");
        }
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT Snapshot FROM Machines";
            var primary = JsonNode.Parse((string)command.ExecuteScalar()!)!;
            var persisted = primary["sessions"]![0]!.Deserialize<SessionSnapshot>(Protocol.Json)!;
            Assert.Equal(pending.ResultState, persisted.ResultState);
            Assert.Equal(pending.ResultUntilUtc, persisted.ResultUntilUtc);
            Assert.Null(persisted.LatestEvent);
            Assert.Null(persisted.LatestEventAtUtc);
        }
        using var reopened = new MachineStore(Database, _clock);
        var restored = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(pending, Assert.Single(restored.Sessions));
        Assert.Equal(AgentState.Waiting, restored.State);
        Assert.Equal("Custom", restored.DisplayName);
        Assert.Equal("Keep", restored.Note);
    }

    [Theory]
    [InlineData(2, AgentEvent.ErrorOccurred)]
    [InlineData(2, AgentEvent.AgentStop)]
    [InlineData(3, AgentEvent.ErrorOccurred)]
    [InlineData(3, AgentEvent.AgentStop)]
    public async Task LegacyPermissionRecoverySuppressesOverlayWithoutCreatingQuestionWait(int version, AgentEvent result)
    {
        var source = version == PresenceProtocol.Version ? SourceDescriptor.LegacyCli : Source;
        var original = StateReducer.Apply(null, "a", result, _clock.Now, _clock.Now, source: source);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var pending = StateReducer.Apply(original, "a", AgentEvent.PermissionRequest, _clock.Now, _clock.Now, source: source);
        var projected = PresenceProtocol.Project(Snapshot(1, pending), version);
        var wire = JsonSerializer.Deserialize<PresenceReport>(
            JsonSerializer.Serialize(projected, PresenceProtocol.Json), PresenceProtocol.Json)!;
        Assert.Empty(PresenceProtocol.Validate(wire));
        var legacy = Assert.Single(wire.Sessions!);
        Assert.False(legacy.AwaitingUserInput);
        Assert.Null(legacy.ResultState);
        Assert.Null(legacy.ResultUntilUtc);
        Assert.Null(legacy.LatestEvent);
        Assert.Equal(original.ResultState, pending.ResultState);
        Assert.Equal(original.ResultUntilUtc, pending.ResultUntilUtc);
        using var store = new MachineStore(Database, _clock);
        await store.AcceptAsync(wire);
        Assert.Equal(AgentState.Waiting, Assert.Single(await store.GetMachinesAsync()).State);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = StateReducer.Apply(pending, "a", AgentEvent.PreToolUse, _clock.Now, _clock.Now, source: source);
        Assert.False(resumed.AwaitingUserInput);
        Assert.Equal(AgentState.Executing, resumed.UnderlyingState);
        Assert.Equal(original.ResultUntilUtc, resumed.ResultUntilUtc);
        await store.AcceptAsync(PresenceProtocol.Project(Snapshot(2, resumed), version));
        var restored = Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions);
        Assert.False(restored.AwaitingUserInput);
        Assert.Equal(AgentState.Executing, restored.UnderlyingState);
        Assert.Equal(original.ResultState, restored.ResultState);
        Assert.Equal(original.ResultUntilUtc, restored.ResultUntilUtc);
        Assert.Equal(AgentState.Executing, StateReducer.Effective(restored, original.ResultUntilUtc!.Value));
    }

    [Theory]
    [InlineData(AgentState.Waiting)]
    [InlineData(AgentState.Executing)]
    public async Task ContradictoryTerminalMetadataIsRejectedBeforeMutation(AgentState state)
    {
        using var store = new MachineStore(Database, _clock);
        var active = StateReducer.Apply(null, "a", AgentEvent.PermissionRequest, _clock.Now, _clock.Now, source: Source);
        await store.AcceptAsync(Snapshot(1, active));
        var before = Assert.Single(await store.GetMachinesAsync());
        _clock.Advance(TimeSpan.FromSeconds(1));
        var contradictory = new SessionSnapshot
        {
            Source = Source, SessionId = "a", UnderlyingState = state, UpdatedAtUtc = _clock.Now,
            LatestEvent = AgentEvent.SessionEnd, LatestEventAtUtc = _clock.Now
        };
        Assert.NotEmpty(PresenceProtocol.Validate(Snapshot(2, contradictory)));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AcceptAsync(Snapshot(2, contradictory)));
        var after = Assert.Single(await store.GetMachinesAsync());
        Assert.Equal(before.Sequence, after.Sequence);
        Assert.Equal(before.LastContactUtc, after.LastContactUtc);
        Assert.Equal(before.Sessions, after.Sessions);
        var ended = contradictory with { UnderlyingState = AgentState.Idle };
        Assert.Empty(PresenceProtocol.Validate(Snapshot(2, ended)));
        Assert.NotEmpty(PresenceProtocol.Validate(Snapshot(2, ended with { LatestEvent = AgentEvent.PreToolUse })));
        await store.AcceptAsync(Snapshot(2, ended with { LatestEvent = null, LatestEventAtUtc = null }));
        Assert.Equal(AgentState.Idle, Assert.Single(await store.GetMachinesAsync()).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdmittedVersionsCanGrowAndResumeOrEndWithoutEvictingLiveSessions(bool legacySource)
    {
        using var store = new MachineStore(Database, _clock);
        var shortSource = legacySource ? null : Source;
        var candidates = Enumerable.Range(0, Protocol.MaxSessions).Select(i => new SessionSnapshot
        {
            Source = shortSource, SessionId = new string('\uFFFF', 120) + i,
            UnderlyingState = AgentState.Waiting, UpdatedAtUtc = _clock.Now
        }).ToList();
        while (!PresenceProtocol.FitsSnapshot(candidates)) candidates.RemoveAt(candidates.Count - 1);
        Assert.NotEmpty(candidates);
        await store.AcceptAsync(Snapshot(1, candidates.ToArray()));
        var expandedSource = (shortSource ?? SourceDescriptor.LegacyCli) with { Version = new string('\uFFFF', 64) };
        long sequence = 1;
        foreach (var kind in new[] { AgentEvent.ErrorOccurred, AgentEvent.PermissionRequest, AgentEvent.PreToolUse, AgentEvent.SessionEnd })
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            foreach (var session in candidates)
            {
                var report = Hook(++sequence, session.SessionId, kind);
                await store.AcceptAsync(report with { Hook = report.Hook! with
                {
                    Source = expandedSource, Client = expandedSource.Kind, ClientVersion = expandedSource.Version
                } });
            }
            var sessions = Assert.Single(await store.GetMachinesAsync()).Sessions;
            Assert.Equal(candidates.Count, sessions.Count);
            Assert.All(sessions, s =>
            {
                Assert.Equal(expandedSource, s.Source);
                Assert.Equal(kind, s.LatestEvent);
            });
            Assert.True(PresenceProtocol.FitsSnapshot(sessions));
        }
        Assert.Equal(AgentState.Idle, Assert.Single(await store.GetMachinesAsync()).State);
    }

    internal static SessionSnapshot[] BoundedSnapshot(IEnumerable<SessionSnapshot> candidates)
    {
        var sessions = candidates.Take(Protocol.MaxSessions).ToList();
        while (!PresenceProtocol.FitsSnapshot(sessions)) sessions.RemoveAt(sessions.Count - 1);
        return sessions.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
