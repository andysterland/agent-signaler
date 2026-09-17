using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.RateLimiting;
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
        _client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            // A gated body must start only after Kestrel reads it, not after a client-side fallback timer.
            Expect100ContinueTimeout = Timeout.InfiniteTimeSpan
        })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}"), Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private async Task ConfigureAsync(DashboardServerOptions options,
        Func<TokenBucketRateLimiterOptions, RateLimiter>? tokenBucketLimiterFactory = null)
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        _server = new DashboardServer(_store, _port, null, options, tokenBucketLimiterFactory);
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
        ManualTokenBucketLimiter? limiter = null;
        await ConfigureAsync(new DashboardServerOptions
        {
            ListenerMode = DashboardListenerMode.Internet, RequestBurstLimit = 1, RequestsPerSecond = 1
        }, options =>
        {
            Assert.Equal(1, options.TokenLimit);
            Assert.Equal(1, options.TokensPerPeriod);
            Assert.Equal(0, options.QueueLimit);
            Assert.Equal(TimeSpan.FromSeconds(1), options.ReplenishmentPeriod);
            Assert.True(options.AutoReplenishment);
            return limiter = new ManualTokenBucketLimiter(options);
        });
        using var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.NotNull(limiter);
        var report = StateTests.Request(AgentEvent.SessionStart);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/status")
        {
            Content = JsonContent.Create(report, options: Protocol.Json)
        };
        request.Headers.Add("X-Forwarded-For", "203.0.113.1");
        using var rejected = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter!.Delta);
        Assert.NotEmpty((await rejected.Content.ReadFromJsonAsync<ValidationResponse>(Protocol.Json))!.Errors);
        using var unknownRequest = new HttpRequestMessage(HttpMethod.Get, "/not-an-endpoint");
        unknownRequest.Headers.Add("X-Forwarded-For", "203.0.113.2");
        using var unknown = await _client.SendAsync(unknownRequest);
        Assert.Equal(HttpStatusCode.TooManyRequests, unknown.StatusCode);
        using var depleted = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.TooManyRequests, depleted.StatusCode);
        Assert.Empty(await _store.GetMachinesAsync());

        limiter.Replenish();
        using var accepted = await _client.PostAsJsonAsync("/api/v1/status", report, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.False((await accepted.Content.ReadFromJsonAsync<StatusResponse>(Protocol.Json))!.Duplicate);
        Assert.Equal(report.MachineId, Assert.Single(await _store.GetMachinesAsync()).MachineId);
        using var depletedAgain = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.TooManyRequests, depletedAgain.StatusCode);

        // Unknown routes also consume the single replenished token, not just return 429 while depleted.
        limiter.Replenish();
        using var notFound = await _client.GetAsync("/not-an-endpoint");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        using var limitedAfterUnknown = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedAfterUnknown.StatusCode);
        limiter.Replenish();
        using var recovered = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
    }

    [Fact]
    public async Task ConcurrencyLimitRejectsImmediatelyWithoutQueueAndReleasesPermit()
    {
        await ConfigureAsync(new DashboardServerOptions
        {
            ListenerMode = DashboardListenerMode.Internet, ConcurrentRequestLimit = 1
        });
        var heldReport = StateTests.Request(AgentEvent.SessionStart);
        var rejectedReport = StateTests.Request(AgentEvent.SessionStart);
        using var content = new GatedStreamingContent(JsonSerializer.SerializeToUtf8Bytes(heldReport, Protocol.Json));
        using var heldRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/status")
        {
            Content = content
        };
        heldRequest.Headers.ExpectContinue = true;
        var held = _client.SendAsync(heldRequest);
        try
        {
            // Kestrel sends 100 Continue on the endpoint's first body read, after acquiring the permit.
            await content.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            using var rejected = await _client.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), rejected.Headers.RetryAfter!.Delta);
            using var rejectedMutation = await _client.PostAsJsonAsync("/api/v1/status", rejectedReport, Protocol.Json)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.TooManyRequests, rejectedMutation.StatusCode);
            Assert.Empty(await _store.GetMachinesAsync());
            Assert.False(held.IsCompleted, "Competing requests must be rejected before the held request is released.");
        }
        finally
        {
            content.Release();
            using var response = await held;
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        Assert.Equal(heldReport.MachineId, Assert.Single(await _store.GetMachinesAsync()).MachineId);
        using var accepted = await _client.PostAsJsonAsync("/api/v1/status", rejectedReport, Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.False((await accepted.Content.ReadFromJsonAsync<StatusResponse>(Protocol.Json))!.Duplicate);
        Assert.Equal(2, (await _store.GetMachinesAsync()).Count);
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

    private sealed class GatedStreamingContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public GatedStreamingContent(byte[] bytes)
        {
            _bytes = bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync(_bytes, cancellationToken);
        }

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    // Do not expose ReplenishingRateLimiter: the partition manager would otherwise replenish it on its timer.
    private sealed class ManualTokenBucketLimiter(TokenBucketRateLimiterOptions options) : RateLimiter
    {
        private readonly TokenBucketRateLimiter _limiter = new(new TokenBucketRateLimiterOptions
        {
            TokenLimit = options.TokenLimit,
            TokensPerPeriod = options.TokensPerPeriod,
            QueueLimit = options.QueueLimit,
            QueueProcessingOrder = options.QueueProcessingOrder,
            AutoReplenishment = false,
            // The framework has no TimeProvider seam. A single tick makes an explicit refill eligible
            // after an HTTP round trip; elapsed time alone can never replenish this wrapped bucket.
            ReplenishmentPeriod = TimeSpan.FromTicks(1)
        });

        public void Replenish()
        {
            Assert.Equal(0, _limiter.GetStatistics()!.CurrentAvailablePermits);
            Assert.True(_limiter.TryReplenish());
            Assert.Equal(options.TokensPerPeriod, _limiter.GetStatistics()!.CurrentAvailablePermits);
        }

        public override TimeSpan? IdleDuration => null;
        public override RateLimiterStatistics? GetStatistics() => _limiter.GetStatistics();
        protected override RateLimitLease AttemptAcquireCore(int permitCount) => _limiter.AttemptAcquire(permitCount);
        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            _limiter.AcquireAsync(permitCount, cancellationToken);
        protected override void Dispose(bool disposing) { if (disposing) _limiter.Dispose(); }
        protected override ValueTask DisposeAsyncCore() => _limiter.DisposeAsync();
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
