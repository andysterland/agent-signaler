using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service.Tests;

public sealed class SessionDisplayNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), $"session-names-{Guid.NewGuid():N}");
    private readonly ManualTimeProvider _clock = new();
    private readonly Guid _machine = Guid.NewGuid();
    private static readonly SourceDescriptor Source = new("vscode", "profile-one", "1");
    private string Database => Path.Combine(_root, "state.db");

    private PresenceReport Snapshot(long sequence, params SessionSnapshot[] sessions) =>
        PresenceTests.Started(_machine, _clock.Now) with
        {
            ProtocolVersion = PresenceProtocol.DisplayNameVersion, Client = "agent-signaler",
            Kind = sequence == 1 ? PresenceKind.Started : PresenceKind.Heartbeat,
            Sequence = sequence, Sessions = sessions
        };

    private SessionSnapshot Session(string? name, SourceDescriptor? source = null) =>
        StateReducer.Apply(null, "same-session", AgentEvent.PermissionRequest, _clock.Now, _clock.Now,
            source: source ?? Source, displayName: name);

    private PresenceReport Hook(long sequence, string? name, SourceDescriptor? source = null,
        DateTimeOffset? at = null)
    {
        source ??= Source;
        var report = Snapshot(sequence) with
        {
            Kind = PresenceKind.Hook, Sessions = null, HeartbeatIntervalSeconds = null,
            ReportedAtUtc = at ?? _clock.Now
        };
        return report with { Hook = new()
        {
            ProtocolVersion = PresenceProtocol.DisplayNameVersion,
            EventId = report.EventId, MachineId = _machine, MachineName = report.MachineName,
            Client = source.Kind, ClientVersion = source.Version, Source = source,
            SessionId = "same-session", Event = AgentEvent.UserPromptSubmitted,
            ReportedAtUtc = report.ReportedAtUtc, DisplayName = name
        } };
    }

    [Fact]
    public async Task OrderedSnapshotCanRenameWithoutChangingStateButStaleSnapshotsCannot()
    {
        using var store = new MachineStore(Database, _clock);
        var original = Session("Original");
        await store.AcceptAsync(Snapshot(1, original));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var renamed = original with { DisplayName = "Renamed" };
        await store.AcceptAsync(Snapshot(2, renamed));
        Assert.Equal(renamed, Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions));
        Assert.True((await store.AcceptAsync(Snapshot(2, original))).Duplicate);
        var stale = original with
        {
            UpdatedAtUtc = original.UpdatedAtUtc.AddTicks(-1),
            LatestEventAtUtc = original.LatestEventAtUtc!.Value.AddTicks(-1)
        };
        await store.AcceptAsync(Snapshot(3, stale));
        Assert.Equal(renamed, Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions));
        await store.AcceptAsync(Snapshot(4, renamed with { DisplayName = null }));
        Assert.Equal(renamed, Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions));
        var newer = Session(null);
        await store.AcceptAsync(PresenceProtocol.Project(Snapshot(5, newer), PresenceProtocol.EnrichedVersion));
        Assert.Equal(newer with { DisplayName = "Renamed" },
            Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions));
    }

    [Fact]
    public async Task HookNamesAreMetadataNotIdentityAndNullOrStaleHooksDoNotEraseThem()
    {
        using var store = new MachineStore(Database, _clock);
        var other = Source with { ScopeId = "profile-two" };
        await store.AcceptAsync(Snapshot(1, Session("Shared"), Session("Shared", other)));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var acceptedAt = _clock.Now;
        await store.AcceptAsync(Hook(2, "First renamed"));
        await store.AcceptAsync(Hook(3, "Second renamed", other));
        await store.AcceptAsync(Hook(4, "Stale", at: acceptedAt.AddTicks(-1)));
        await store.AcceptAsync(Hook(5, "Equal timestamp", at: acceptedAt));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await store.AcceptAsync(PresenceProtocol.Project(Hook(6, null), PresenceProtocol.SourceVersion));
        var sessions = Assert.Single(await store.GetMachinesAsync()).Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.Equal("First renamed", Assert.Single(sessions, s => s.Source == Source).DisplayName);
        Assert.Equal("Second renamed", Assert.Single(sessions, s => s.Source == other).DisplayName);
        using var reopened = new MachineStore(Database, _clock);
        Assert.Equal(sessions, Assert.Single(await reopened.GetMachinesAsync()).Sessions);
    }

    [Fact]
    public async Task PersistenceSeparatesNamesFromBothLegacyTablesAndCascadesDeletion()
    {
        var original = Session("Explicit title");
        using (var store = new MachineStore(Database, _clock))
        {
            await store.AcceptAsync(Snapshot(1, original));
            await store.UpdateDetailsAsync(_machine, "Machine label", "Keep this note");
        }
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Snapshot FROM Machines";
        var primary = JsonNode.Parse((string)command.ExecuteScalar()!)!;
        Assert.Null(primary["sessions"]![0]!["displayName"]);
        Assert.Null(primary["sessions"]![0]!["latestEvent"]);
        command.CommandText = "SELECT Sessions FROM SessionMetadataV1";
        var metadata = (string)command.ExecuteScalar()!;
        Assert.DoesNotContain("displayName", metadata);
        Assert.Contains("latestEvent", metadata);
        command.CommandText = "SELECT DisplayNames FROM SessionDisplayNamesV1";
        var names = JsonSerializer.Deserialize<Dictionary<string, string>>((string)command.ExecuteScalar()!, Protocol.Json)!;
        Assert.Equal("Explicit title", Assert.Single(names).Value);
        Assert.Equal(SourceIdentity.SessionKey(Source, original.SessionId), Assert.Single(names).Key);
        using var reopened = new MachineStore(Database, _clock);
        var restored = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(original, Assert.Single(restored.Sessions));
        Assert.Equal("Machine label", restored.DisplayName);
        Assert.Equal("Keep this note", restored.Note);
        await reopened.RemoveAsync(_machine);
        command.CommandText = "SELECT COUNT(*) FROM SessionDisplayNamesV1";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalNamesCannotBlockAdmissionOrEvictActiveSessions(bool hook)
    {
        using var store = new MachineStore(Database, _clock);
        var sessions = new List<SessionSnapshot>();
        SessionSnapshot Next(string? name) => Session(name) with { SessionId = $"session-{sessions.Count}" };
        while (PresenceProtocol.FitsSnapshot(sessions.Append(Next(new string('\uFFFF', 128)))))
            sessions.Add(Next(new string('\uFFFF', 128)));
        while (PresenceProtocol.FitsSnapshot(sessions.Append(Next(null))))
            sessions.Add(Next(null));
        Assert.Contains(sessions, session => session.DisplayName is not null);
        await store.AcceptAsync(Snapshot(1, sessions.ToArray()));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var incoming = StateReducer.Apply(null, $"session-{sessions.Count}", AgentEvent.UserPromptSubmitted,
            _clock.Now, _clock.Now, source: Source);
        Assert.False(PresenceProtocol.FitsSnapshot(sessions.Append(incoming)));
        var expected = sessions.Select(session => session with { DisplayName = null }).Append(incoming).ToArray();
        Assert.True(PresenceProtocol.FitsSnapshot(expected));
        if (hook)
        {
            var report = Hook(2, null);
            await store.AcceptAsync(report with { Hook = report.Hook! with { SessionId = incoming.SessionId } });
        }
        else
            await store.AcceptAsync(Snapshot(2, expected));
        var accepted = Assert.Single(await store.GetMachinesAsync());
        Assert.Equal(2, accepted.Sequence);
        Assert.Equal(expected, accepted.Sessions);
        using var reopened = new MachineStore(Database, _clock);
        Assert.Equal(expected, Assert.Single(await reopened.GetMachinesAsync()).Sessions);
    }

    [Fact]
    public async Task NameWriteFailureRollsBackSnapshotMetadataAndOrdering()
    {
        using var store = new MachineStore(Database, _clock);
        var original = Session("Original");
        await store.AcceptAsync(Snapshot(1, original));
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER RejectNames BEFORE UPDATE ON SessionDisplayNamesV1
            BEGIN SELECT RAISE(ABORT, 'fixture failure'); END;
            """;
        command.ExecuteNonQuery();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var report = Hook(2, "Renamed");
        await Assert.ThrowsAsync<SqliteException>(() => store.AcceptAsync(report));
        var unchanged = Assert.Single(await store.GetMachinesAsync());
        Assert.Equal(1, unchanged.Sequence);
        Assert.Equal(original, Assert.Single(unchanged.Sessions));
        command.CommandText = "DROP TRIGGER RejectNames";
        command.ExecuteNonQuery();
        Assert.False((await store.AcceptAsync(report)).Duplicate);
        Assert.Equal("Renamed", Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions).DisplayName);
    }

    [Fact]
    public async Task OlderWriterCannotResurrectHashMismatchedNames()
    {
        using var store = new MachineStore(Database, _clock);
        await store.AcceptAsync(Snapshot(1, Session("Do not resurrect")));
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Snapshot FROM Machines";
        var oldSnapshot = JsonNode.Parse((string)command.ExecuteScalar()!)!;
        oldSnapshot["note"] = "Older writer";
        command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
        command.Parameters.AddWithValue("$snapshot", oldSnapshot.ToJsonString());
        command.ExecuteNonQuery();
        using var reopened = new MachineStore(Database, _clock);
        var restored = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Null(Assert.Single(restored.Sessions).DisplayName);
        Assert.Equal("Older writer", restored.Note);
        await reopened.RenameAsync(_machine, "Updated");
        Assert.Null(Assert.Single(Assert.Single(await reopened.GetMachinesAsync()).Sessions).DisplayName);
    }

    [Theory]
    [InlineData("unknown-identity", "Title")]
    [InlineData(null, "")]
    [InlineData(null, "Bad\nname")]
    public async Task MatchingHashStillRequiresValidExactSessionNameMapping(string? identity, string name)
    {
        using var store = new MachineStore(Database, _clock);
        await store.AcceptAsync(Snapshot(1, Session("Original")));
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE SessionDisplayNamesV1 SET DisplayNames=$names";
        command.Parameters.AddWithValue("$names", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [identity ?? SourceIdentity.SessionKey(Source, "same-session")] = name
        }, Protocol.Json));
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetMachinesAsync());
    }

    [Fact]
    public async Task UnboundNamesInLegacySnapshotAreRejected()
    {
        using var store = new MachineStore(Database, _clock);
        await store.AcceptAsync(Snapshot(1, Session("Original")));
        using var db = new SqliteConnection($"Data Source={Database};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Snapshot FROM Machines";
        var snapshot = JsonNode.Parse((string)command.ExecuteScalar()!)!;
        snapshot["sessions"]![0]!["displayName"] = "Unbound";
        command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
        command.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetMachinesAsync());
    }

    [Fact]
    public void OldWireProjectionsNeverLeakNamesOrChangeSourceIdentity()
    {
        var session = Session("Private title");
        foreach (var version in new[] { PresenceProtocol.SourceVersion, PresenceProtocol.EnrichedVersion })
        {
            var snapshot = PresenceProtocol.Project(Snapshot(1, session), version);
            var hook = PresenceProtocol.Project(Hook(2, "Private title"), version);
            Assert.Empty(PresenceProtocol.Validate(snapshot));
            Assert.Empty(PresenceProtocol.Validate(hook));
            Assert.Null(Assert.Single(snapshot.Sessions!).DisplayName);
            Assert.Null(hook.Hook!.DisplayName);
            Assert.Equal(Source, hook.Hook.Source);
            Assert.DoesNotContain("displayName", JsonSerializer.Serialize(snapshot, PresenceProtocol.Json));
            Assert.DoesNotContain("Private title", JsonSerializer.Serialize(hook, PresenceProtocol.Json));
        }
        var legacy = session with { Source = null };
        Assert.Null(PresenceProtocol.ProjectSession(legacy, Protocol.Version).DisplayName);
        Assert.Null(PresenceProtocol.ProjectSession(legacy, PresenceProtocol.Version).DisplayName);
    }

    [Fact]
    public async Task V5HttpValidatesTitlesVersionsAndStrictJsonBeforeCommitting()
    {
        using var store = new MachineStore(Database, _clock);
        await using var server = new DashboardServer(store, 0, options: new() { AllowEphemeralPort = true });
        await server.StartAsync();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{server.BoundPort}"), Timeout = TimeSpan.FromSeconds(10)
        };
        var health = await client.GetFromJsonAsync<PresenceHealthResponse>("/api/v5/health", Protocol.Json);
        Assert.Equal(PresenceProtocol.DisplayNameVersion, health!.ProtocolVersion);
        var report = Snapshot(1, Session(new string('n', 128)));
        foreach (var invalid in new[] { "", " ", "bad\nname", "bad\u007fname", new string('n', 129) })
        {
            using var response = await client.PostAsJsonAsync("/api/v5/reports",
                Snapshot(1, Session("Valid") with { DisplayName = invalid }), PresenceProtocol.Json);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var hook = Hook(2, invalid);
            using var hookResponse = await client.PostAsJsonAsync("/api/v5/reports", hook, PresenceProtocol.Json);
            Assert.Equal(HttpStatusCode.BadRequest, hookResponse.StatusCode);
        }
        var json = JsonSerializer.Serialize(report, PresenceProtocol.Json);
        foreach (var invalid in new[]
        {
            json.Replace("\"displayName\":", "\"displayName\":\"Duplicate\",\"displayName\":"),
            json.Replace("\"displayName\":", "\"prompt\":\"private\",\"displayName\":")
        })
        {
            using var content = new StringContent(invalid, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync("/api/v5/reports", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var wrongRoute = await client.PostAsJsonAsync("/api/v4/reports", report, PresenceProtocol.Json))
            Assert.Equal(HttpStatusCode.BadRequest, wrongRoute.StatusCode);
        using (var wrongVersion = await client.PostAsJsonAsync("/api/v5/reports",
            report with { ProtocolVersion = PresenceProtocol.EnrichedVersion }, PresenceProtocol.Json))
            Assert.Equal(HttpStatusCode.BadRequest, wrongVersion.StatusCode);
        var wrongHook = Hook(2, "Title");
        using (var wrongInner = await client.PostAsJsonAsync("/api/v5/reports",
            wrongHook with { Hook = wrongHook.Hook! with { ProtocolVersion = PresenceProtocol.SourceVersion } }, PresenceProtocol.Json))
            Assert.Equal(HttpStatusCode.BadRequest, wrongInner.StatusCode);
        Assert.Empty(await store.GetMachinesAsync());
        using (var accepted = await client.PostAsJsonAsync("/api/v5/reports", report, PresenceProtocol.Json))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        _clock.Advance(TimeSpan.FromSeconds(1));
        using (var accepted = await client.PostAsJsonAsync("/api/v5/reports", Hook(2, "Hook title"), PresenceProtocol.Json))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal("Hook title", Assert.Single(Assert.Single(await store.GetMachinesAsync()).Sessions).DisplayName);
        foreach (var version in new[] { PresenceProtocol.Version, PresenceProtocol.SourceVersion, PresenceProtocol.EnrichedVersion })
        {
            var old = PresenceProtocol.Project(Snapshot(3, Session(null, SourceDescriptor.LegacyCli)), version);
            var oldJson = JsonNode.Parse(JsonSerializer.Serialize(old, PresenceProtocol.Json))!;
            oldJson["sessions"]![0]!["displayName"] = null;
            using var content = new StringContent(oldJson.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync($"/api/v{version}/reports", content);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        var legacyJson = JsonNode.Parse(JsonSerializer.Serialize(StateTests.Request(
            AgentEvent.SessionStart, Guid.NewGuid(), _clock.Now), Protocol.Json))!;
        legacyJson["displayName"] = null;
        using var legacyContent = new StringContent(legacyJson.ToJsonString(), Encoding.UTF8, "application/json");
        using var legacyResponse = await client.PostAsync("/api/v1/status", legacyContent);
        Assert.Equal(HttpStatusCode.BadRequest, legacyResponse.StatusCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
