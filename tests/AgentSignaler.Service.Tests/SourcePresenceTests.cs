using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Service.Tests;

public sealed class SourcePresenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"source-presence-{Guid.NewGuid()}");
    private readonly ManualTimeProvider _clock = new();
    private readonly Guid _machine = Guid.NewGuid();
    private readonly MachineStore _store;
    private string Database => Path.Combine(_directory, "state.db");
    private static readonly SourceDescriptor Code = new("vscode", "profile-one", "1.137");
    private static readonly SourceDescriptor Studio = new("visual-studio", "shared-scope", "18.12");

    public SourcePresenceTests() => _store = new MachineStore(Database, _clock);

    private PresenceReport Report(long sequence = 1) =>
        PresenceTests.Started(_machine, _clock.Now) with
        {
            ProtocolVersion = PresenceProtocol.SourceVersion, Client = "agent-signaler",
            Kind = sequence == 1 ? PresenceKind.Started : PresenceKind.Heartbeat, Sequence = sequence
        };

    private PresenceReport Hook(SourceDescriptor? source, AgentEvent kind, long sequence, DateTimeOffset? at = null)
    {
        var report = Report(sequence) with
        {
            Kind = PresenceKind.Hook, Sessions = null, HeartbeatIntervalSeconds = null,
            ReportedAtUtc = at ?? _clock.Now
        };
        return report with
        {
            Hook = new StatusRequest
            {
                ProtocolVersion = PresenceProtocol.SourceVersion, Source = source,
                Client = (source ?? SourceDescriptor.LegacyCli).Kind,
                ClientVersion = (source ?? SourceDescriptor.LegacyCli).Version,
                MachineId = _machine, MachineName = report.MachineName, EventId = report.EventId,
                SessionId = "same-host-id", Event = kind, ReportedAtUtc = report.ReportedAtUtc
            }
        };
    }

    [Fact]
    public void IdentityIsBoundedUnambiguousAndIndependentOfVersion()
    {
        var key = SourceIdentity.SessionKey(Code, new string('s', 128));
        Assert.Equal(64, key.Length);
        Assert.Equal(key, SourceIdentity.SessionKey(Code with { Version = "next" }, new string('s', 128)));
        Assert.Equal(SourceIdentity.SessionKey(null, "id"), SourceIdentity.SessionKey(SourceDescriptor.LegacyCli, "id"));
        Assert.NotEqual(SourceIdentity.SessionKey(Code, "id"), SourceIdentity.SessionKey(Studio, "id"));
        Assert.NotEqual(SourceIdentity.SessionKey(Code, "id"), SourceIdentity.SessionKey(Code with { ScopeId = "profile-two" }, "id"));
        Assert.NotEqual(SourceIdentity.SessionKey(new("vscode", "a:b"), "c"),
            SourceIdentity.SessionKey(new("vscode", "a"), "b:c"));
        Assert.DoesNotContain("isValid", JsonSerializer.Serialize(Code, Protocol.Json));
    }

    [Theory]
    [InlineData("unknown", "scope", "1")]
    [InlineData("vscode", "", "1")]
    [InlineData("vscode", "scope\n", "1")]
    [InlineData("vscode", "scope", "")]
    [InlineData("vscode", "scope", "1\t")]
    public void InvalidSourcesFailWithoutThrowing(string kind, string scope, string version)
    {
        var source = new SourceDescriptor(kind, scope, version);
        Assert.False(source.IsValid);
        Assert.NotEmpty(PresenceProtocol.Validate(Hook(source, AgentEvent.ExecutionStopped, 2)));
        Assert.NotEmpty(PresenceProtocol.Validate(Report() with
        {
            Sessions = [new() { Source = source, SessionId = "id", UpdatedAtUtc = _clock.Now }]
        }));
    }

    [Fact]
    public void SnapshotIdentityIncludesScopeAndTreatsNullAsLegacy()
    {
        var session = new SessionSnapshot { SessionId = "id", UpdatedAtUtc = _clock.Now };
        Assert.Empty(PresenceProtocol.Validate(Report() with
        {
            Sessions = [session, session with { Source = Code }, session with { Source = Studio },
                session with { Source = Code with { ScopeId = "other" } }]
        }));
        Assert.NotEmpty(PresenceProtocol.Validate(Report() with
        {
            Sessions = [session, session with { Source = SourceDescriptor.LegacyCli }]
        }));
        Assert.NotEmpty(PresenceProtocol.Validate(Report() with
        {
            Sessions = [session with { Source = Code }, session with { Source = Code with { Version = "next" } }]
        }));
        Assert.NotEmpty(PresenceProtocol.Validate(Report() with { Sessions = [session with { SessionId = null! }] }));
    }

    [Fact]
    public void V3RequiresReporterIdentityAndRejectsMalformedOriginWithoutExceptions()
    {
        var hook = Hook(Code, AgentEvent.ExecutionStopped, 2);
        Assert.Empty(PresenceProtocol.Validate(hook));
        Assert.NotEmpty(PresenceProtocol.Validate(hook with
        {
            Hook = hook.Hook! with { Client = "agent-signaler", ClientVersion = hook.ClientVersion }
        }));
        Assert.NotEmpty(PresenceProtocol.Validate(hook with
        {
            Hook = hook.Hook! with { ClientVersion = "mismatched-version" }
        }));
        Assert.NotEmpty(PresenceProtocol.Validate(hook with { Client = "vscode" }));
        Assert.NotEmpty(PresenceProtocol.Validate(hook with { Hook = hook.Hook! with { ProtocolVersion = 1 } }));
        Assert.NotEmpty(PresenceProtocol.Validate(hook with { Hook = hook.Hook! with { MachineId = Guid.NewGuid() } }));
        Assert.NotEmpty(PresenceProtocol.Validate(hook with { Hook = hook.Hook! with { Source = new(null!, null!, null!) } }));
        Assert.False(new SourceDescriptor("vscode", new string('x', 129)).IsValid);
        Assert.False(new SourceDescriptor("vscode", "scope", new string('x', 65)).IsValid);
        Assert.True(new SourceDescriptor("vscode", new string('x', 128), new string('v', 64)).IsValid);
    }

    [Fact]
    public void ExecutionStoppedClearsPendingInputAndResultWithoutEndingSession()
    {
        var previous = new SessionSnapshot
        {
            SessionId = "id", Source = Code, UnderlyingState = AgentState.Waiting,
            AwaitingUserInput = true, ResultState = AgentState.Failed,
            ResultUntilUtc = _clock.Now.AddSeconds(60), UpdatedAtUtc = _clock.Now
        };
        var stopped = StateReducer.Apply(previous, "id", AgentEvent.ExecutionStopped,
            _clock.Now.AddSeconds(1), _clock.Now.AddSeconds(1));
        Assert.Equal(Code, stopped.Source);
        Assert.Equal(AgentState.Waiting, stopped.UnderlyingState);
        Assert.False(stopped.AwaitingUserInput);
        Assert.Null(stopped.ResultState);
        Assert.Null(stopped.ResultUntilUtc);
        Assert.Same(stopped, StateReducer.Apply(stopped, "id", AgentEvent.SessionEnd, _clock.Now, _clock.Now));
    }

    [Fact]
    public async Task CoexistingSourcesStopAndSnapshotOrderingSurviveRestart()
    {
        await _store.AcceptAsync(Report());
        await _store.AcceptAsync(Hook(Code, AgentEvent.ErrorOccurred, 2));
        await _store.AcceptAsync(Hook(Studio, AgentEvent.PreToolUse, 3));
        await _store.AcceptAsync(Hook(null, AgentEvent.SessionStart, 4));
        _clock.Advance(TimeSpan.FromSeconds(1));
        await _store.AcceptAsync(Hook(Code, AgentEvent.ExecutionStopped, 5));
        var before = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(3, before.Sessions.Count);
        Assert.All(before.Sessions, s => Assert.Equal("same-host-id", s.SessionId));
        var stopped = Assert.Single(before.Sessions, s => s.Source == Code);
        Assert.Equal(AgentState.Waiting, stopped.UnderlyingState);
        Assert.Null(stopped.ResultState);
        Assert.Null(stopped.ResultUntilUtc);
        Assert.False(stopped.AwaitingUserInput);
        Assert.Equal("agent-signaler", before.Client);
        Assert.Equal(AgentState.Waiting, before.State);
        await _store.AcceptAsync(Report(6) with
        {
            Sessions = before.Sessions.Select(s => PresenceProtocol.ProjectSession(s, PresenceProtocol.SourceVersion) with
            {
                UpdatedAtUtc = s.UpdatedAtUtc.AddSeconds(-1), UnderlyingState = AgentState.Idle
            }).ToArray()
        });
        using var reopened = new MachineStore(Database, _clock);
        var after = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(before.Sessions, after.Sessions);
        Assert.Equal(6, after.Sequence);
        Assert.True((await reopened.AcceptAsync(Hook(Code, AgentEvent.PreToolUse, 5))).Duplicate);
    }

    [Fact]
    public async Task RetirementDoesNotSuppressAnotherSourceAndIsPersisted()
    {
        await _store.AcceptAsync(Report());
        await _store.AcceptAsync(Hook(Code, AgentEvent.SessionStart, 2));
        await _store.AcceptAsync(Report(3));
        using var reopened = new MachineStore(Database, _clock);
        await reopened.AcceptAsync(Hook(Code, AgentEvent.PreToolUse, 4));
        Assert.Empty(Assert.Single(await reopened.GetMachinesAsync()).Sessions);
        await reopened.AcceptAsync(Hook(Studio, AgentEvent.PreToolUse, 5, _clock.Now.AddSeconds(-1)));
        await reopened.AcceptAsync(Hook(Code with { ScopeId = "other-profile" }, AgentEvent.PreToolUse, 6));
        Assert.Equal(2, Assert.Single(await reopened.GetMachinesAsync()).Sessions.Count);
    }

    [Fact]
    public async Task FutureClockInAnotherSourceDoesNotBlockRetiringItsIdleSessionsAtCapacity()
    {
        var future = _clock.Now.AddYears(1);
        var sessions = EnrichedPresenceTests.BoundedSnapshot(Enumerable.Range(0, Protocol.MaxSessions).Select(i => new SessionSnapshot
        {
            Source = Studio, SessionId = $"future-{i}", UnderlyingState = AgentState.Idle, UpdatedAtUtc = future
        }));
        await _store.AcceptAsync(Report() with { ReportedAtUtc = future, Sessions = sessions });
        await _store.AcceptAsync(Hook(Code, AgentEvent.PreToolUse, 2));
        var machine = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(sessions.Length, machine.Sessions.Count);
        Assert.Equal(AgentState.Executing, Assert.Single(machine.Sessions, s => s.Source == Code).UnderlyingState);
        using var reopened = new MachineStore(Database, _clock);
        var late = Hook(Studio, AgentEvent.PreToolUse, 3, future);
        await reopened.AcceptAsync(late with
        {
            Hook = late.Hook! with { SessionId = "future-0" }
        });
        Assert.DoesNotContain(Assert.Single(await reopened.GetMachinesAsync()).Sessions, s => s.SessionId == "future-0");
    }

    [Fact]
    public async Task LegacyMigrationPreservesReceiptsDetailsMappingsResultsAndGeneration()
    {
        var legacy = Hook(null, AgentEvent.AgentStop, 2).Hook! with { ProtocolVersion = Protocol.Version };
        await _store.AcceptAsync(legacy);
        await _store.UpdateDetailsAsync(_machine, "My name", "My note");
        var mapping = new WindowsAppConnection(new Uri("https://example.region.devcenter.azure.com/"),
            "project", "dev-box", "test@example.com", Guid.NewGuid(),
            "ms-cloudpc:connect?cpcid=11111111-2222-3333-4444-555555555555&username=test%40example.com&environment=public&version=2&source=dashboard",
            _clock.Now);
        await _store.SetWindowsAppConnectionAsync(_machine, mapping);
        var session = PresenceProtocol.ProjectSession(Assert.Single(Assert.Single(await _store.GetMachinesAsync()).Sessions), PresenceProtocol.Version);
        await _store.AcceptAsync(PresenceTests.Started(_machine, _clock.Now) with { Generation = 7, Sessions = [session] });
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Snapshot FROM Machines";
            var snapshot = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            snapshot.Remove("schemaVersion");
            snapshot.Remove("retiredSources");
            snapshot["retiredThroughUtc"] = _clock.Now;
            command.CommandText = "UPDATE Machines SET Snapshot=$snapshot";
            command.Parameters.AddWithValue("$snapshot", snapshot.ToJsonString());
            command.ExecuteNonQuery();
        }
        using var reopened = new MachineStore(Database, _clock);
        var original = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal("My name", original.DisplayName);
        Assert.Equal("My note", original.Note);
        Assert.Equal(mapping, original.WindowsAppConnection);
        Assert.Equal(session, Assert.Single(original.Sessions));
        await reopened.AcceptAsync(Report(2) with { Generation = 7, Sessions = original.Sessions });
        var after = Assert.Single(await reopened.GetMachinesAsync());
        Assert.Equal(7, after.Generation);
        Assert.Equal(2, after.Sequence);
        using var database = new SqliteConnection($"Data Source={Database};Pooling=False");
        database.Open();
        using var saved = database.CreateCommand();
        saved.CommandText = "SELECT Snapshot FROM Machines";
        var json = JsonNode.Parse((string)saved.ExecuteScalar()!)!;
        Assert.Equal(2, json["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(json["retiredSources"]![SourceIdentity.SessionKey(null, "")]);
        saved.CommandText = "SELECT COUNT(*) FROM Receipts";
        Assert.Equal(1L, saved.ExecuteScalar());
    }

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
