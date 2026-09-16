using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public sealed class ReceiverControlTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"AgentSignaler-controls-{Guid.NewGuid()}");
    private MachineStore _store = null!;
    private DashboardServer _server = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _store = new MachineStore(Path.Combine(_directory, "state.db"));
        _server = new DashboardServer(_store, _port,
            options: new DashboardServerOptions { ListenerMode = DashboardListenerMode.Internet });
        await _server.StartAsync();
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}"), Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private async Task ConfigureAsync(DashboardServerOptions options, Action? callback = null)
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        _server = new DashboardServer(_store, _port, callback, options);
        await _server.StartAsync();
    }

    [Fact]
    public async Task InternetBindsOnlyIpv4AndIpv6Loopback()
    {
        Assert.Equal(DashboardListenerMode.Internet, _server.ListenerMode);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(x => x.Port == _port).ToArray();
        Assert.NotEmpty(listeners);
        Assert.All(listeners, endpoint => Assert.True(IPAddress.IsLoopback(endpoint.Address)));
        Assert.Contains(listeners, endpoint => endpoint.Address.Equals(IPAddress.Loopback));
        Assert.Equal(new HealthResponse(Protocol.Version, "ok"),
            await _client.GetFromJsonAsync<HealthResponse>("/health", Protocol.Json));
        if (Socket.OSSupportsIPv6)
        {
            Assert.Contains(listeners, endpoint => endpoint.Address.Equals(IPAddress.IPv6Loopback));
            Assert.Equal(new HealthResponse(Protocol.Version, "ok"),
                await _client.GetFromJsonAsync<HealthResponse>($"http://[::1]:{_port}/health", Protocol.Json));
        }
    }

    [Fact]
    public async Task DefaultOptionsRetainLanAnyInterfaceBinding()
    {
        await ConfigureAsync(new DashboardServerOptions());
        Assert.Equal(DashboardListenerMode.Lan, _server.ListenerMode);
        Assert.Contains(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(),
            endpoint => endpoint.Port == _port && (endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)));
    }

    [Fact]
    public async Task TwentyFiveMachinesCanBurstConcurrentHooks()
    {
        var requests = Enumerable.Range(0, 25).SelectMany(_ =>
        {
            var hook = StateTests.Request(AgentEvent.PreToolUse);
            return new[]
            {
                hook,
                hook with { EventId = Guid.NewGuid(), SessionId = "second" },
                hook with
                {
                    EventId = Guid.NewGuid(), Event = AgentEvent.SessionStart,
                    ReportedAtUtc = hook.ReportedAtUtc.AddSeconds(-1)
                }
            };
        }).ToArray();
        var responses = await Task.WhenAll(requests.Select(request => _client.PostAsJsonAsync("/api/v1/status", request, Protocol.Json)));
        try
        {
            Assert.Equal(75, responses.Length);
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            var machines = await _store.GetMachinesAsync();
            Assert.Equal(25, machines.Count);
            Assert.All(machines, machine =>
            {
                Assert.Equal(AgentState.Executing, machine.State);
                Assert.Equal(2, machine.Sessions.Count);
            });
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task GlobalRateLimitCountsAllRoutesIgnoresForwardedIdentityAndRecovers()
    {
        await ConfigureAsync(new DashboardServerOptions
        {
            ListenerMode = DashboardListenerMode.Internet, RequestBurstLimit = 1, RequestsPerSecond = 1
        });
        using var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/status")
        {
            Content = JsonContent.Create(StateTests.Request(AgentEvent.SessionStart), options: Protocol.Json)
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.1");
        using var rejected = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter!.Delta);
        Assert.NotEmpty((await rejected.Content.ReadFromJsonAsync<ValidationResponse>(Protocol.Json))!.Errors);
        Assert.Empty(await _store.GetMachinesAsync());
        using var unknown = await _client.GetAsync("/not-an-endpoint");
        Assert.Equal(HttpStatusCode.TooManyRequests, unknown.StatusCode);
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        using var recovered = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ConcurrencyLimitRejectsImmediatelyWithoutQueueAndReleasesPermit()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await ConfigureAsync(new DashboardServerOptions
        {
            ListenerMode = DashboardListenerMode.Internet, ConcurrentRequestLimit = 1
        }, () =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
        });
        using var heldRequest = new HttpRequestMessage(HttpMethod.Get, "/health");
        heldRequest.Headers.Add(Protocol.ConnectionTestHeader, "1");
        var held = _client.SendAsync(heldRequest);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var rejected = await _client.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter!.Delta);
        }
        finally
        {
            release.Set();
            using var response = await held;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var malformed = await _client.PostAsJsonAsync("/api/v1/status", new { prompt = "private-data" });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.DoesNotContain("private-data", await malformed.Content.ReadAsStringAsync());
        using var recovered = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InternetPreservesBodyLimitsAndProtocolValidation(bool chunked)
    {
        var bytes = new byte[Protocol.MaxBodyBytes + 1];
        using HttpContent content = chunked ? new StreamingContent(bytes) : new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/status") { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ExpectContinue = !chunked;
        using var oversized = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var invalidVersion = await _client.PostAsJsonAsync("/api/v1/status",
            StateTests.Request(AgentEvent.SessionStart) with { ProtocolVersion = 99 }, Protocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, invalidVersion.StatusCode);
        using var invalidType = await _client.PostAsync("/api/v1/status", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, invalidType.StatusCode);
        Assert.Empty(await _store.GetMachinesAsync());
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    [Fact]
    public async Task ReceiptCapacityIsAnExplicitSafeStorageFailure()
    {
        using var limited = new MachineStore(Path.Combine(_directory, "state.db"), options: new MachineStoreOptions { ReceiptLimit = 1 });
        var original = StateTests.Request(AgentEvent.SessionStart);
        await limited.AcceptAsync(original);
        await _server.StopAsync();
        await _server.DisposeAsync();
        _server = new DashboardServer(limited, _port, options: new DashboardServerOptions { ListenerMode = DashboardListenerMode.Internet });
        await _server.StartAsync();
        using var response = await _client.PostAsJsonAsync("/api/v1/status", StateTests.Request(AgentEvent.SessionStart), Protocol.Json);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter!.Delta);
        Assert.NotEmpty((await response.Content.ReadFromJsonAsync<ValidationResponse>(Protocol.Json))!.Errors);
        using var duplicate = await _client.PostAsJsonAsync("/api/v1/status", original, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
        Assert.True((await duplicate.Content.ReadFromJsonAsync<StatusResponse>(Protocol.Json))!.Duplicate);
        await _server.StopAsync();
    }

    [Fact]
    public void InvalidModesAndUnboundedLimitsAreRejected()
    {
        foreach (var options in new[]
        {
            new DashboardServerOptions { ListenerMode = (DashboardListenerMode)99 },
            new DashboardServerOptions { ConcurrentRequestLimit = 0 },
            new DashboardServerOptions { ConcurrentRequestLimit = 101 },
            new DashboardServerOptions { RequestsPerSecond = 0 },
            new DashboardServerOptions { RequestBurstLimit = 201 },
            new DashboardServerOptions { RequestBurstLimit = 1, RequestsPerSecond = 2 }
        })
            Assert.Throws<ArgumentOutOfRangeException>(() => new DashboardServer(_store, _port, options: options));
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
