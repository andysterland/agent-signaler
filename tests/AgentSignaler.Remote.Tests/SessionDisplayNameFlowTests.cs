using System.Net;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;
using static AgentSignaler.Tests.CurrentUserOwnedTranscriptFixture;

namespace AgentSignaler.Remote.Tests;

public sealed class SessionDisplayNameFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private string StatePath => Path.Combine(_root, "state.json");
    private SessionStore Store(Func<SessionSnapshot, string?>? reader = null) =>
        new(StatePath, new DiagnosticLog(Path.Combine(_root, "relay.log")), reader);

    [Fact]
    public void NamesPersistSeparatelyAndRefreshWithoutChangingStateOrIdentity()
    {
        string? name = "Fix login";
        var store = Store(_ => name);
        var initial = Assert.Single(store.UpdateReport(AgentEvent.PermissionRequest, new("id", Now, false), Now).Sessions);
        Assert.Equal(name, initial.DisplayName);
        Assert.DoesNotContain("displayName", File.ReadAllText(StatePath));
        Assert.DoesNotContain("displayName", File.ReadAllText(StatePath + ".events-v1"));
        Assert.Equal(initial, Assert.Single(Store().UpdateReport(null, null, Now.AddSeconds(1)).Sessions));

        name = "Login regression";
        var renamed = Assert.Single(store.UpdateReport(null, null, Now.AddSeconds(2)).Sessions);
        Assert.Equal(initial with { DisplayName = name }, renamed);
        name = null;
        Assert.Equal(renamed, Assert.Single(store.UpdateReport(null, null, Now.AddSeconds(3)).Sessions));
        var stale = store.UpdateReport(AgentEvent.UserPromptSubmitted,
            new("id", Now.AddSeconds(-1), false, DisplayName: "Stale name"), Now.AddSeconds(4));
        Assert.False(stale.Accepted);
        Assert.Equal(renamed, Assert.Single(stale.Sessions));
    }

    [Fact]
    public void DuplicateNamesAndSessionIdsInDifferentSourcesRemainIndependent()
    {
        var store = Store();
        var a = new SourceDescriptor("copilot-cli", "a", "test");
        var b = a with { ScopeId = "b" };
        store.UpdateReport(AgentEvent.SessionStart, new("same-id", Now, false, Source: a, DisplayName: "Same name"), Now);
        var sessions = store.UpdateReport(AgentEvent.SessionStart,
            new("same-id", Now, false, Source: b, DisplayName: "Same name"), Now).Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.All(sessions, s => Assert.Equal("Same name", s.DisplayName));
        Assert.Equal(2, sessions.Select(s => SourceIdentity.SessionKey(s.Source, s.SessionId)).Distinct().Count());
        Assert.Equal(sessions, Store().UpdateReport(null, null, Now).Sessions);
    }

    [Fact]
    public void InterruptedPublicationRecoversMatchingNamesAndResetInvalidatesThem()
    {
        var store = Store();
        store.UpdateReport(AgentEvent.SessionStart, new("id", Now, false, DisplayName: "Original"), Now);
        var committed = File.ReadAllBytes(StatePath);
        store.UpdateReport(AgentEvent.PreToolUse, new("id", Now.AddSeconds(1), false, DisplayName: "Renamed"), Now.AddSeconds(1));
        AtomicFile.Write(StatePath, committed);
        Assert.Equal("Original", Assert.Single(Store().UpdateReport(null, null, Now.AddSeconds(2)).Sessions).DisplayName);
        AtomicFile.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(new StoredRemoteState(2, Now.AddDays(1), []), Protocol.Json));
        Assert.Empty(Store().UpdateReport(null, null, Now.AddDays(1)).Sessions);
    }

    [Fact]
    public void OptionalTitlesCannotRejectPreviouslyAdmittedStatusUpdates()
    {
        var store = Store();
        LocalReport report = null!;
        for (var i = 0; i <= Protocol.MaxSessions; i++)
        {
            report = store.UpdateReport(AgentEvent.SessionStart, new(i.ToString(), Now, false), Now);
            if (report.CapacityExceeded) break;
        }
        var count = report.Sessions.Count;
        var refreshed = Store(_ => new string('\uFFFF', Protocol.MaxDisplayNameLength))
            .UpdateReport(null, null, Now.AddSeconds(1));
        Assert.True(refreshed.Accepted);
        Assert.Equal(count, refreshed.Sessions.Count);
        Assert.True(PresenceProtocol.FitsSnapshot(refreshed.Sessions));
        Assert.All(refreshed.Sessions, s => Assert.Null(s.DisplayName));
        Assert.Contains("session-names-capacity-omitted", File.ReadAllText(Path.Combine(_root, "relay.log")));
    }

    [Fact]
    public void ReclaimingAnEndedSessionRechecksOptionalNamePressure()
    {
        const string name = "n";
        var incomingId = new string('i', 128);
        SessionSnapshot Snapshot(string id) => StateReducer.Apply(null, id, AgentEvent.SessionStart, Now, Now,
            source: SourceDescriptor.LegacyCli, displayName: name);
        var count = Enumerable.Range(1, Protocol.MaxSessions).First(n =>
        {
            var before = Enumerable.Range(0, n).Select(i => Snapshot($"session-{i:D3}")).ToList();
            var after = before.Append(Snapshot(incomingId) with { DisplayName = null }).ToList();
            var reclaimed = after.Skip(1).ToList();
            return PresenceProtocol.FitsSnapshot(before) &&
                !PresenceProtocol.FitsSnapshot(after.Select(s => s with { DisplayName = null })) &&
                !PresenceProtocol.FitsSnapshot(reclaimed) &&
                PresenceProtocol.FitsSnapshot(reclaimed.Select(s => s with { DisplayName = null }));
        });
        var store = Store();
        for (var i = 0; i < count; i++)
            Assert.True(store.UpdateReport(AgentEvent.SessionStart,
                new($"session-{i:D3}", Now, false, DisplayName: name), Now).Accepted);
        Assert.True(store.UpdateReport(AgentEvent.SessionEnd, new("session-000", Now.AddSeconds(1), false), Now.AddSeconds(1)).Accepted);
        var admitted = store.UpdateReport(AgentEvent.SessionStart,
            new(incomingId, Now.AddSeconds(2), false), Now.AddSeconds(2));
        Assert.True(admitted.Accepted);
        Assert.False(admitted.CapacityExceeded);
        Assert.Equal(count, admitted.Sessions.Count);
        Assert.DoesNotContain(admitted.Sessions, s => s.SessionId == "session-000");
        Assert.Contains(admitted.Sessions, s => s.SessionId == incomingId);
        Assert.All(admitted.Sessions, s => Assert.Null(s.DisplayName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("line\nbreak")]
    public void InvalidNamesAreRejectedAtTheIpcBoundary(string name)
    {
        Assert.False(HookPayloadAdapters.IsSanitized(AgentEvent.SessionStart, new("id", Now, false, DisplayName: name)));
        Assert.Throws<ArgumentException>(() => StateReducer.Apply(null, "id", AgentEvent.SessionStart, Now, Now, displayName: name));
    }

    [Fact]
    public async Task ClientDeliversNameInBothHookAndRecoverySnapshot()
    {
        var configPath = Path.Combine(_root, "remote.json");
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }.ToVersion3();
        AtomicFile.Write(configPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var transport = new RecordingTransport();
        await using var client = new ClientCoordinator(configPath, transportFactory: _ => transport);
        await using var server = new ClientIpcServer(configPath, client.HandleAsync);
        client.Start();
        await Eventually(() => client.Status().State == "connected");
        Assert.True((await ClientIpc.HookAsync(configPath, AgentEvent.SessionStart,
            new("id", Now, false, DisplayName: "Fix login"))).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Heartbeat && r.Sessions!.Count == 1));
        Assert.Equal("Fix login", transport.Reports.Single(r => r.Kind == PresenceKind.Hook).Hook!.DisplayName);
        Assert.Equal("Fix login", transport.Reports.Last(r => r.Kind == PresenceKind.Heartbeat).Sessions![0].DisplayName);
        Assert.All(transport.Reports, r => Assert.Empty(PresenceProtocol.Validate(r)));
    }

    [Fact]
    public async Task ClientReadsConfiguredWorkspaceNameAndRefreshesAnIdleRename()
    {
        CreateOwnedDirectory(_root);
        var id = Guid.NewGuid().ToString("D");
        var home = Path.Combine(_root, "copilot");
        var sessionDirectory = Path.Combine(home, "session-state", id);
        CreateOwnedDirectory(sessionDirectory);
        var metadata = Path.Combine(sessionDirectory, "workspace.yaml");
        WriteTranscriptFile(metadata, $"id: {id}\nname: Original\ncwd: PRIVATE-PATH\n");
        var configPath = Path.Combine(_root, "remote.json");
        var source = new SourceDescriptor("copilot-cli", "configured-scope", "test");
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }
            .ToVersion4() with
        {
            Integrations = [new()
            {
                Id = "cli-target", Kind = source.Kind, ScopeId = source.ScopeId,
                DisplayName = "CLI", InstallationId = "fixture", HookDirectory = Path.Combine(home, "hooks"),
                Capability = IntegrationCapability.Configured, SupportedEvents = HookAdapters.Events(source.Kind)
            }]
        };
        AtomicFile.Write(configPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var transport = new RecordingTransport();
        await using var client = new ClientCoordinator(configPath, transportFactory: _ => transport);
        client.Start();
        await Eventually(() => client.Status().State == "connected");
        Assert.True(client.AcceptHook(AgentEvent.SessionStart, new(id, Now, false, Source: source)).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Heartbeat && r.Sessions!.Count == 1));
        var original = transport.Reports.Last(r => r.Kind == PresenceKind.Heartbeat).Sessions![0];
        Assert.Equal("Original", original.DisplayName);
        Assert.Equal("Original", transport.Reports.Single(r => r.Kind == PresenceKind.Hook).Hook!.DisplayName);

        WriteTranscriptFile(metadata, $"id: {id}\nname: Renamed\ncwd: PRIVATE-PATH\n");
        client.RequestSnapshot();
        await Eventually(() => transport.Reports.Any(r => r.Sessions?.Any(s => s.DisplayName == "Renamed") == true));
        var renamed = transport.Reports.Last(r => r.Kind == PresenceKind.Heartbeat).Sessions![0];
        Assert.Equal(original with { DisplayName = "Renamed" }, renamed);
        Assert.DoesNotContain("PRIVATE-PATH", JsonSerializer.Serialize(transport.Reports, Protocol.Json));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task TransportNegotiatesNamesAndOmitsThemForOlderReceivers(int version)
    {
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }
            .ToVersion3() with { Version = version == 2 ? 3 : 4 };
        var session = StateReducer.Apply(null, "id", AgentEvent.SessionStart, Now, Now, displayName: "Fix login");
        var report = new PresenceReport
        {
            ProtocolVersion = PresenceProtocol.DisplayNameVersion, Client = "agent-signaler", ClientVersion = "test",
            MachineId = config.MachineId, MachineName = "test", EventId = Guid.NewGuid(),
            Kind = PresenceKind.Started, Generation = 1, Sequence = 1, ReportedAtUtc = Now,
            HeartbeatIntervalSeconds = 300, Sessions = [session]
        };
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Get)
                return request.RequestUri!.AbsolutePath == $"/api/v{version}/health"
                    ? new(HttpStatusCode.OK) { Content = new StringContent(
                        JsonSerializer.Serialize(new PresenceHealthResponse(version, "ok"), Protocol.Json), Encoding.UTF8, "application/json") }
                    : new(HttpStatusCode.NotFound);
            Assert.Equal($"/api/v{version}/reports", request.RequestUri!.AbsolutePath);
            var json = await request.Content!.ReadAsStringAsync(token);
            var wire = JsonSerializer.Deserialize<PresenceReport>(json, PresenceProtocol.Json)!;
            Assert.Empty(PresenceProtocol.Validate(wire));
            Assert.Equal(version == 5 ? "Fix login" : null,
                wire.Kind == PresenceKind.Hook ? wire.Hook!.DisplayName : Assert.Single(wire.Sessions!).DisplayName);
            if (wire.Kind == PresenceKind.Hook)
                Assert.Equal(version == 5 ? 5 : version >= 3 ? 3 : 1, wire.Hook!.ProtocolVersion);
            if (version < 5) Assert.DoesNotContain("displayName", json);
            return new(HttpStatusCode.Accepted);
        }));
        using var transport = new PresenceTransport(config, http);
        Assert.True(await transport.SendAsync(report, CancellationToken.None));
        Assert.True(await transport.SendAsync(report with
        {
            Kind = PresenceKind.Hook, Sessions = null, HeartbeatIntervalSeconds = null, Sequence = 2,
            Hook = new StatusRequest
            {
                ProtocolVersion = PresenceProtocol.DisplayNameVersion,
                EventId = report.EventId, MachineId = report.MachineId, MachineName = report.MachineName,
                ClientVersion = SourceDescriptor.LegacyCli.Version, SessionId = "id", DisplayName = "Fix login",
                Event = AgentEvent.SessionStart, ReportedAtUtc = Now
            }
        }, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
