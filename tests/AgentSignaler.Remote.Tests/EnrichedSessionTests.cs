using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class EnrichedSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), $"enriched-local-{Guid.NewGuid():N}");
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private string StatePath => Path.Combine(_root, "state.json");
    private SessionStore Store => new(StatePath, new DiagnosticLog(Path.Combine(_root, "relay.log")));

    [Fact]
    public void QuietWaitSurvivesCountPressureAndEveryAdmittedSessionCanGrowMetadata()
    {
        var store = Store;
        var filled = FillToCapacity(store);
        var count = filled.Sessions.Count;
        var before = store.UpdateReport(null, null, Now);
        var rejected = store.UpdateReport(AgentEvent.SessionStart, new("overflow", Now, false), Now);
        Assert.False(rejected.Accepted);
        Assert.True(rejected.CapacityExceeded);
        Assert.Equal(before.Sessions, rejected.Sessions);
        for (var i = 0; i < count; i++)
        {
            var update = store.UpdateReport(AgentEvent.ErrorOccurred, new(i.ToString(), Now.AddSeconds(1), false), Now.AddSeconds(1));
            Assert.True(update.Accepted);
            Assert.False(update.CapacityExceeded);
        }
        var asking = store.UpdateReport(AgentEvent.PreToolUse, new("0", Now.AddSeconds(2), false, true), Now.AddSeconds(2));
        Assert.Equal(AgentState.Waiting, StateReducer.Aggregate(asking.Sessions, Now.AddSeconds(2)));
        var pending = asking.Sessions.Single(s => s.SessionId == "0");
        var late = store.UpdateReport(AgentEvent.PostToolUse, new("0", Now, false, true), Now.AddDays(1));
        Assert.False(late.Accepted);
        Assert.Equal(pending, late.Sessions.Single(s => s.SessionId == "0"));
        Assert.Equal(AgentEvent.PreToolUse, pending.LatestEvent);
        Assert.Equal(pending.UpdatedAtUtc, pending.LatestEventAtUtc);
        Assert.Equal(AgentState.Waiting, StateReducer.Aggregate(late.Sessions, Now.AddDays(1)));
    }

    [Fact]
    public void ByteBoundReservesFutureEventsAndRejectsWithoutEvictingWaits()
    {
        var store = Store;
        var source = new SourceDescriptor("vscode", new string('\uFFFF', 128), new string('\uFFFF', 64));
        LocalReport report;
        var count = 0;
        do
        {
            report = store.UpdateReport(AgentEvent.SessionStart,
                new(new string('\uFFFF', 120) + count, Now, false, Source: source), Now);
            if (report.Accepted) count++;
        } while (report.Accepted && count < 65);
        Assert.InRange(count, 1, 63);
        Assert.True(report.CapacityExceeded);
        Assert.Equal(count, report.Sessions.Count);
        foreach (var session in report.Sessions)
        {
            var updated = store.UpdateReport(AgentEvent.ErrorOccurred,
                new(session.SessionId, Now.AddSeconds(1), false, Source: source), Now.AddSeconds(1));
            Assert.True(updated.Accepted);
            Assert.True(PresenceProtocol.FitsSnapshot(updated.Sessions));
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(updated.Sessions, Protocol.Json).Length < Protocol.MaxBodyBytes);
        }
    }

    [Fact]
    public void ReclaimedTombstonesPreserveReplayAcrossReloadAndBaseRemainsRollbackReadable()
    {
        var store = Store;
        var full = FillToCapacity(store);
        var retiredId = full.Sessions[^1].SessionId;
        store.UpdateReport(AgentEvent.SessionEnd, new(retiredId, Now.AddSeconds(1), false), Now.AddSeconds(1));
        var admitted = store.UpdateReport(AgentEvent.SessionStart, new("new", Now.AddSeconds(2), false), Now.AddSeconds(2));
        Assert.True(admitted.Accepted);
        var replay = Store.UpdateReport(AgentEvent.UserPromptSubmitted, new(retiredId, Now, false), Now.AddSeconds(3));
        Assert.False(replay.Accepted);
        Assert.False(replay.CapacityExceeded);
        Assert.DoesNotContain(replay.Sessions, s => s.SessionId == retiredId);
        var legacy = JsonSerializer.Deserialize<StoredRemoteState>(File.ReadAllBytes(StatePath), Protocol.Json)!;
        Assert.Equal(2, legacy.Version);
        Assert.All(legacy.Sessions, s => Assert.Null(s.Snapshot.LatestEvent));
        Assert.Equal(full.Sessions.Count, legacy.Sessions.Count);
        // A rollback Client clean-start changes the base and intentionally invalidates stale enrichment.
        AtomicFile.Write(StatePath, JsonSerializer.SerializeToUtf8Bytes(new StoredRemoteState(2, Now.AddDays(1), []), Protocol.Json));
        Assert.Empty(Store.UpdateReport(null, null, Now.AddDays(1)).Sessions);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(3)]
    public async Task TransportNegotiatesEnrichmentOrExplicitSourcePreservingProjection(int receiverVersion)
    {
        var configuration = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }.ToVersion3() with { Version = receiverVersion == 2 ? 3 : 4 };
        var source = receiverVersion == 2 ? SourceDescriptor.LegacyCli : new SourceDescriptor("vscode", "scope", "1");
        var report = new PresenceReport
        {
            ProtocolVersion = PresenceProtocol.EnrichedVersion, Client = "agent-signaler", ClientVersion = "test",
            Kind = PresenceKind.Started, MachineId = Guid.NewGuid(), EventId = Guid.NewGuid(), MachineName = "test",
            Generation = 1, Sequence = 1, ReportedAtUtc = Now, HeartbeatIntervalSeconds = 300,
            Sessions = [StateReducer.Apply(null, "a", AgentEvent.PermissionRequest, Now, Now, source: source)]
        };
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(async (request, token) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri.AbsolutePath != $"/api/v{receiverVersion}/health")
                    return new(HttpStatusCode.NotFound);
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    JsonSerializer.Serialize(new PresenceHealthResponse(receiverVersion, "ok"), Protocol.Json), Encoding.UTF8, "application/json") };
            }

            Assert.Equal($"/api/v{receiverVersion}/reports", request.RequestUri.AbsolutePath);
            var projected = JsonSerializer.Deserialize<PresenceReport>(await request.Content!.ReadAsStringAsync(token), PresenceProtocol.Json)!;
            Assert.Empty(PresenceProtocol.Validate(projected));
            Assert.Equal(receiverVersion == 2 ? null : source, Assert.Single(projected.Sessions!).Source);
            Assert.Equal(receiverVersion == 4 ? AgentEvent.PermissionRequest : null, projected.Sessions![0].LatestEvent);
            return new(HttpStatusCode.Accepted);
        }));
        using var transport = new PresenceTransport(configuration, client);
        Assert.True(await transport.SendAsync(report, CancellationToken.None));
        Assert.Equal("/api/v5/health", requests[0]);
        if (receiverVersion != 2) Assert.DoesNotContain(requests, p => p.Contains("/v2/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceModeNeverFallsBackToIdentityMergingReceiver()
    {
        var configuration = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }.ToVersion3() with { Version = 4 };
        var paths = new List<string>();
        using var client = new HttpClient(new Handler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DashboardConnection.NegotiateAsync(configuration, client, CancellationToken.None));
        Assert.Contains("Upgrade", error.Message);
        Assert.Equal(new[] { "/api/v5/health", "/api/v4/health", "/api/v3/health" }, paths);
    }

    [Fact]
    public void InterruptedPublicationRecoversMatchingReplayStateAndUnknownFormatsFailWithoutMutation()
    {
        var store = Store;
        var full = FillToCapacity(store);
        var retiredId = full.Sessions[^1].SessionId;
        store.UpdateReport(AgentEvent.SessionEnd, new(retiredId, Now.AddSeconds(1), false), Now.AddSeconds(1));
        store.UpdateReport(AgentEvent.SessionStart, new("new", Now.AddSeconds(2), false), Now.AddSeconds(2));
        var committed = File.ReadAllBytes(StatePath);
        store.UpdateReport(null, null, Now.AddSeconds(3));
        // Simulate a crash after publishing enrichment but before replacing the base.
        AtomicFile.Write(StatePath, committed);
        var replay = Store.UpdateReport(AgentEvent.PreToolUse, new(retiredId, Now, false), Now.AddSeconds(4));
        Assert.False(replay.Accepted);
        Assert.DoesNotContain(replay.Sessions, s => s.SessionId == retiredId);
        var unknown = JsonNode.Parse(File.ReadAllBytes(StatePath + ".events-v1"))!;
        unknown["version"] = 999;
        AtomicFile.Write(StatePath + ".events-v1", Encoding.UTF8.GetBytes(unknown.ToJsonString()));
        var before = File.ReadAllBytes(StatePath);
        Assert.Throws<InvalidDataException>(() => Store.UpdateReport(null, null, Now.AddSeconds(5)));
        Assert.Equal(before, File.ReadAllBytes(StatePath));
        Assert.Equal(unknown.ToJsonString(), File.ReadAllText(StatePath + ".events-v1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VersionHeadroomAdmitsFutureEscapedVersionUpdatesWithoutLosingWaits(bool legacySource)
    {
        var store = Store;
        var shortSource = legacySource ? null : new SourceDescriptor("vscode", "scope", "1");
        LocalReport report;
        var admitted = 0;
        do
        {
            report = store.UpdateReport(AgentEvent.SessionStart,
                new(new string('\uFFFF', 120) + admitted, Now, false, Source: shortSource), Now);
            if (report.Accepted) admitted++;
        } while (report.Accepted && admitted < 65);
        Assert.True(report.CapacityExceeded);
        Assert.InRange(admitted, 1, 63);
        var ids = report.Sessions.Select(s => s.SessionId).ToArray();
        var expanded = (shortSource ?? SourceDescriptor.LegacyCli) with { Version = new string('\uFFFF', 64) };
        var time = Now;
        foreach (var kind in new[] { AgentEvent.ErrorOccurred, AgentEvent.PermissionRequest, AgentEvent.PreToolUse, AgentEvent.SessionEnd })
        {
            time = time.AddSeconds(1);
            foreach (var id in ids)
            {
                report = Store.UpdateReport(kind, new(id, time, false, Source: expanded), time);
                Assert.True(report.Accepted);
                Assert.False(report.CapacityExceeded);
                Assert.Equal(admitted, report.Sessions.Count);
                Assert.Equal(ids.Order(), report.Sessions.Select(s => s.SessionId).Order());
            }
            Assert.True(PresenceProtocol.FitsSnapshot(report.Sessions));
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(report.Sessions, Protocol.Json).Length <= Protocol.MaxBodyBytes - 4096);
        }
        Assert.All(report.Sessions, s => Assert.Equal(AgentEvent.SessionEnd, s.LatestEvent));
    }

    [Theory]
    [InlineData(AgentEvent.ErrorOccurred)]
    [InlineData(AgentEvent.AgentStop)]
    public void PermissionProjectionRetainsCanonicalResultAcrossLocalReloadAndResume(AgentEvent result)
    {
        Store.UpdateReport(result, new("a", Now, false), Now);
        var pending = Store.UpdateReport(AgentEvent.PermissionRequest, new("a", Now.AddSeconds(1), false), Now.AddSeconds(1));
        var canonical = Assert.Single(pending.Sessions);
        var primary = JsonSerializer.Deserialize<StoredRemoteState>(File.ReadAllBytes(StatePath), Protocol.Json)!;
        var persisted = Assert.Single(primary.Sessions).Snapshot;
        Assert.Equal(canonical.ResultState, persisted.ResultState);
        Assert.Equal(canonical.ResultUntilUtc, persisted.ResultUntilUtc);
        Assert.Null(persisted.LatestEvent);
        Assert.Null(persisted.LatestEventAtUtc);
        foreach (var version in new[] { PresenceProtocol.Version, PresenceProtocol.SourceVersion })
        {
            var projected = PresenceProtocol.ProjectSession(canonical, version);
            Assert.False(projected.AwaitingUserInput);
            Assert.Null(projected.ResultState);
            Assert.Null(projected.ResultUntilUtc);
            Assert.Equal(AgentState.Waiting, StateReducer.Effective(projected, Now.AddSeconds(2)));
        }
        var restored = Assert.Single(Store.UpdateReport(null, null, Now.AddSeconds(2)).Sessions);
        Assert.Equal(canonical, restored);
        Assert.NotNull(restored.ResultState);
        Assert.Equal(Now + Protocol.ResultDuration, restored.ResultUntilUtc);
        var resumed = Assert.Single(Store.UpdateReport(AgentEvent.PreToolUse,
            new("a", Now.AddSeconds(3), false), Now.AddSeconds(3)).Sessions);
        Assert.False(resumed.AwaitingUserInput);
        Assert.Equal(AgentState.Executing, resumed.UnderlyingState);
        Assert.Equal(canonical.ResultUntilUtc, resumed.ResultUntilUtc);
    }

    private static LocalReport FillToCapacity(SessionStore store)
    {
        Assert.Equal(64, Protocol.MaxSessions);
        Assert.Equal(32768, Protocol.MaxBodyBytes);
        for (var i = 0; i <= Protocol.MaxSessions; i++)
        {
            var report = store.UpdateReport(AgentEvent.SessionStart, new(i.ToString(), Now, false), Now);
            if (report.CapacityExceeded)
            {
                Assert.False(report.Accepted);
                Assert.InRange(report.Sessions.Count, 1, Protocol.MaxSessions);
                return report;
            }
            Assert.True(report.Accepted);
        }
        throw new InvalidOperationException("Admission exceeded the configured bounds.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handle(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
