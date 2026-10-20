using System.Net;
using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class EffectiveEndpointTests
{
    [Fact]
    public void ClockRollbackCannotExtendAnExplicitMonotonicCacheLease()
    {
        var clock = new RollbackClock();
        var endpoint = new EffectiveEndpointSnapshot(new Uri("https://new-51820.use.devtunnels.ms/"), 1,
            clock.Utc.AddMinutes(10)) { VerifiedTimestamp = 0, CacheLifetime = TimeSpan.FromMinutes(10) };
        clock.Utc = clock.Utc.AddHours(-2);
        clock.Elapsed = TimeSpan.FromMinutes(10).Ticks;
        Assert.False(endpoint.IsValid(clock));
        Assert.Equal(TimeSpan.Zero, endpoint.RemainingLifetime(clock));
    }

    [Fact]
    public async Task ExpiredSnapshotNeverStartsHealthOrReportRequests()
    {
        var clock = new ClientRuntimeTests.ManualClock();
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var config = Configuration();
        var endpoint = new EffectiveEndpointSnapshot(new Uri("https://new-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow());
        using var transport = new PresenceTransport(config, endpoint, client, clock);
        Assert.False(await transport.SendAsync(Report(clock), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SnapshotRoutesHealthAndPresenceTogetherWithoutChangingSavedUrl()
    {
        var clock = new ClientRuntimeTests.ManualClock();
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var config = Configuration();
        var endpoint = new EffectiveEndpointSnapshot(new Uri("https://new-51820.use.devtunnels.ms/"), 1,
            clock.GetUtcNow().AddMinutes(10));
        using var transport = new PresenceTransport(config, endpoint, client, clock);
        Assert.True(await transport.SendAsync(Report(clock), CancellationToken.None));
        Assert.Equal(new[] { "/api/v5/health", "/api/v5/reports" }, handler.Requests.Select(r => r.AbsolutePath));
        Assert.All(handler.Requests, r => Assert.Equal(endpoint.BaseUri.Host, r.Host));
        Assert.Equal("https://saved.example/", config.DashboardBaseUrl);
        Assert.Throws<InvalidOperationException>(() => transport.RenewEndpoint(endpoint with
            { BaseUri = new Uri("https://replacement-51820.use.devtunnels.ms/") }));
    }

    [Fact]
    public async Task SameEndpointRenewalPreservesNegotiationAndExtendsOnlyTheExplicitLease()
    {
        var clock = new ClientRuntimeTests.ManualClock();
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        var endpoint = new EffectiveEndpointSnapshot(new Uri("https://new-51820.use.devtunnels.ms/"), 1,
            clock.GetUtcNow().AddMinutes(1));
        using var transport = new PresenceTransport(Configuration(), endpoint, client, clock);
        Assert.True(await transport.SendAsync(Report(clock), CancellationToken.None));
        transport.RenewEndpoint(endpoint with { ValidUntilUtc = clock.GetUtcNow().AddMinutes(10) });
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(await transport.SendAsync(Report(clock), CancellationToken.None));
        Assert.Single(handler.Requests, r => r.AbsolutePath.EndsWith("health", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromMinutes(8));
        Assert.False(await transport.SendAsync(Report(clock), CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    private static RemoteConfiguration Configuration() => new()
    {
        Version = 5, MachineId = Guid.NewGuid(), MachineName = "fixture", DashboardBaseUrl = "https://saved.example/"
    };

    private static PresenceReport Report(TimeProvider clock) => new()
    {
        ProtocolVersion = PresenceProtocol.DisplayNameVersion, MachineId = Guid.NewGuid(), MachineName = "fixture", Client = "agent-signaler",
        ClientVersion = "test", EventId = Guid.NewGuid(), Generation = 1, Sequence = 1,
        Kind = PresenceKind.Started, ReportedAtUtc = clock.GetUtcNow(), Sessions = [], HeartbeatIntervalSeconds = 300
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(request.Method == HttpMethod.Get ? new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new PresenceHealthResponse(PresenceProtocol.DisplayNameVersion, "ok"), Protocol.Json),
                    System.Text.Encoding.UTF8, "application/json")
            } : new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private sealed class RollbackClock : TimeProvider
    {
        public DateTimeOffset Utc = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public long Elapsed;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}