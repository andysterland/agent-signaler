using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public sealed class WebhookTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"AgentSignaler-http-{Guid.NewGuid()}");
    private MachineStore _store = null!;
    private DashboardServer _server = null!;
    private HttpClient _client = null!;
    private int _connectionTests;

    public async Task InitializeAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _store = new MachineStore(Path.Combine(_directory, "state.db"));
        _server = new DashboardServer(_store, port, () => Interlocked.Increment(ref _connectionTests));
        await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    }

    [Fact]
    public async Task HealthAndAcceptedWebhookRoundTrip()
    {
        var health = await _client.GetFromJsonAsync<HealthResponse>("/health", Protocol.Json);
        Assert.Equal(1, health!.ProtocolVersion);
        var request = StateTests.Request(AgentEvent.SessionStart);
        using var response = await _client.PostAsJsonAsync("/api/v1/status", request, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<StatusResponse>(Protocol.Json))!.Duplicate);
        using var duplicate = await _client.PostAsJsonAsync("/api/v1/status", request, Protocol.Json);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<StatusResponse>(Protocol.Json))!.Duplicate);
        Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(0, Volatile.Read(ref _connectionTests));
    }

    [Fact]
    public async Task PresenceHealthAndLifecycleAreSeparateFromLegacyProtocol()
    {
        Assert.Equal("{\"protocolVersion\":1,\"status\":\"ok\"}", await _client.GetStringAsync("/health"));
        Assert.Equal(new PresenceHealthResponse(2, "ok"),
            await _client.GetFromJsonAsync<PresenceHealthResponse>("/api/v2/health", Protocol.Json));
        Assert.Empty(await _store.GetMachinesAsync());
        var started = PresenceTests.Started(Guid.NewGuid(), DateTimeOffset.UtcNow);
        using var response = await _client.PostAsJsonAsync("/api/v2/reports", started, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<PresenceResponse>(Protocol.Json))!.Duplicate);
        using var duplicate = await _client.PostAsJsonAsync("/api/v2/reports", started, Protocol.Json);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<PresenceResponse>(Protocol.Json))!.Duplicate);
        using var legacy = await _client.PostAsJsonAsync("/api/v1/status",
            StateTests.Request(AgentEvent.SessionStart) with { MachineId = started.MachineId }, Protocol.Json);
        Assert.Equal(HttpStatusCode.Conflict, legacy.StatusCode);
        var offline = started with
        {
            Kind = PresenceKind.Offline, EventId = Guid.NewGuid(), Sequence = 2,
            HeartbeatIntervalSeconds = null, Sessions = null
        };
        using var stopped = await _client.PostAsJsonAsync("/api/v2/reports", offline, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        Assert.Equal(AgentState.Offline, Assert.Single(await _store.GetMachinesAsync()).State);
        using var revived = await _client.PostAsJsonAsync("/api/v2/reports",
            started with { Kind = PresenceKind.Heartbeat, Sequence = 3 }, Protocol.Json);
        Assert.Equal(HttpStatusCode.Conflict, revived.StatusCode);
    }

    [Theory]
    [InlineData(PresenceKind.Started, "hook")]
    [InlineData(PresenceKind.Heartbeat, "HOOK")]
    [InlineData(PresenceKind.Offline, "sessions")]
    [InlineData(PresenceKind.Offline, "heartbeatIntervalSeconds")]
    [InlineData(PresenceKind.Offline, "hook")]
    [InlineData(PresenceKind.Hook, "sessions")]
    [InlineData(PresenceKind.Hook, "heartbeatIntervalSeconds")]
    public async Task ForbiddenPresenceFieldsAreRejectedEvenWhenExplicitlyNull(PresenceKind kind, string field)
    {
        var started = PresenceTests.Started(Guid.NewGuid(), DateTimeOffset.UtcNow);
        using var accepted = await _client.PostAsJsonAsync("/api/v2/reports", started, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var original = Assert.Single(await _store.GetMachinesAsync());
        var report = started with
        {
            Kind = kind, Sequence = 2,
            Sessions = kind is PresenceKind.Started or PresenceKind.Heartbeat ? [] : null,
            HeartbeatIntervalSeconds = kind is PresenceKind.Started or PresenceKind.Heartbeat ? 300 : null,
            Hook = kind == PresenceKind.Hook ? new StatusRequest
            {
                EventId = started.EventId, MachineId = started.MachineId, MachineName = started.MachineName,
                ClientVersion = started.ClientVersion, ReportedAtUtc = started.ReportedAtUtc,
                SessionId = "session", Event = AgentEvent.SessionStart
            } : null
        };
        var json = System.Text.Json.JsonSerializer.Serialize(report, Protocol.Json);
        json = json[..^1] + $",\"{field}\":null}}";
        using var response = await _client.PostAsync("/api/v2/reports", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var unchanged = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(original.Sequence, unchanged.Sequence);
        Assert.Equal(original.LastContactUtc, unchanged.LastContactUtc);
    }

    [Theory]
    [InlineData("/api/v1/status")]
    [InlineData("/api/v2/reports")]
    public async Task LegacyRoutesRejectExplicitNullSource(string path)
    {
        var hook = StateTests.Request(AgentEvent.SessionStart);
        var json = System.Text.Json.JsonSerializer.Serialize(hook, Protocol.Json);
        json = json[..^1] + ",\"SoUrCe\":null}";
        if (path.Contains("v2"))
        {
            var started = PresenceTests.Started(hook.MachineId, hook.ReportedAtUtc);
            using var accepted = await _client.PostAsJsonAsync(path, started, PresenceProtocol.Json);
            var report = started with
            {
                Kind = PresenceKind.Hook, Sequence = 2, Sessions = null, HeartbeatIntervalSeconds = null, Hook = hook
            };
            var envelope = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(report, Protocol.Json))!;
            envelope["hook"] = System.Text.Json.Nodes.JsonNode.Parse(json);
            json = envelope.ToJsonString();
        }
        using var response = await _client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SourcePresenceHealthAndRoutesEnforceVersionBoundary()
    {
        Assert.Equal(new PresenceHealthResponse(3, "ok"),
            await _client.GetFromJsonAsync<PresenceHealthResponse>("/api/v3/health", Protocol.Json));
        var started = PresenceTests.Started(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            ProtocolVersion = 3, Client = "agent-signaler"
        };
        using var wrongRoute = await _client.PostAsJsonAsync("/api/v2/reports", started, PresenceProtocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, wrongRoute.StatusCode);
        using var oldReport = await _client.PostAsJsonAsync("/api/v3/reports",
            started with { ProtocolVersion = 2, Client = "copilot-cli" }, PresenceProtocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, oldReport.StatusCode);
        using var accepted = await _client.PostAsJsonAsync("/api/v3/reports", started, PresenceProtocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var hook = StateTests.Request(AgentEvent.ExecutionStopped) with
        {
            ProtocolVersion = 3, MachineId = started.MachineId, MachineName = started.MachineName,
            ReportedAtUtc = started.ReportedAtUtc, Client = "vscode",
            Source = new("vscode", "profile", "origin-version"), ClientVersion = "origin-version"
        };
        var report = started with
        {
            Kind = PresenceKind.Hook, Sequence = 2, EventId = hook.EventId,
            Sessions = null, HeartbeatIntervalSeconds = null, Hook = hook
        };
        foreach (var invalidHook in new[]
        {
            hook with { Client = "agent-signaler" },
            hook with { ClientVersion = "different-origin-version" }
        })
        {
            using var rejected = await _client.PostAsJsonAsync("/api/v3/reports",
                report with { Hook = invalidHook }, PresenceProtocol.Json);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Equal(1, Assert.Single(await _store.GetMachinesAsync()).Sequence);
        }
        using var stopped = await _client.PostAsJsonAsync("/api/v3/reports", report, PresenceProtocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
        var session = Assert.Single(Assert.Single(await _store.GetMachinesAsync()).Sessions);
        Assert.Equal(hook.Source, session.Source);
        Assert.Equal(AgentState.Waiting, session.UnderlyingState);
        Assert.Null(session.ResultState);
        using var legacy = await _client.PostAsJsonAsync("/api/v1/status",
            hook with { ProtocolVersion = 1, Source = null, Client = "copilot-cli" }, Protocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
    }

    [Fact]
    public async Task LegacySnapshotRejectsSourceFieldEvenWhenNull()
    {
        var started = PresenceTests.Started(Guid.NewGuid(), DateTimeOffset.UtcNow) with
        {
            Sessions = [new() { SessionId = "session", UpdatedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }]
        };
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(started, Protocol.Json))!;
        node["sessions"]![0]!["source"] = null;
        using var response = await _client.PostAsync("/api/v2/reports",
            new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _store.GetMachinesAsync());
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("0", 0)]
    [InlineData("1, 1", 0)]
    [InlineData("1", 1)]
    public async Task OnlyExplicitConnectionTestsNotifyWithoutCreatingMachines(string? marker, int expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        if (marker is not null) request.Headers.Add(Protocol.ConnectionTestHeader, marker);
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var health = await response.Content.ReadFromJsonAsync<HealthResponse>(Protocol.Json);
        Assert.Equal(new HealthResponse(Protocol.Version, "ok"), health);
        Assert.Equal(expected, Volatile.Read(ref _connectionTests));
        Assert.Empty(await _store.GetMachinesAsync());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"prompt\":\"sensitive content must not be echoed\"}")]
    public async Task MalformedRequestsHaveExplicitSafeErrors(string json)
    {
        foreach (var path in new[] { "/api/v1/status", "/api/v2/reports" })
        {
            using var response = await _client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<ValidationResponse>(Protocol.Json);
            Assert.NotEmpty(error!.Errors);
            Assert.DoesNotContain("sensitive content", await response.Content.ReadAsStringAsync());
            Assert.Empty(await _store.GetMachinesAsync());
        }
    }

    [Fact]
    public async Task InvalidProtocolAndContentTypeAreRejected()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/status",
            StateTests.Request(AgentEvent.SessionStart) with { ProtocolVersion = 99 }, Protocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var wrongType = await _client.PostAsync("/api/v1/status", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SizeLimitAppliesToFixedLengthAndChunkedBodies(bool chunked)
    {
        foreach (var path in new[] { "/api/v1/status", "/api/v2/reports" })
        {
            var bytes = Encoding.UTF8.GetBytes(new string(' ', Protocol.MaxBodyBytes + 1));
            using HttpContent content = chunked ? new StreamingContent(bytes) : new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            request.Headers.TransferEncodingChunked = chunked;
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
