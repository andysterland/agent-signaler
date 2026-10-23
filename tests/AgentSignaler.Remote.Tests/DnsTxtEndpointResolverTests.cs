using System.Net;
using System.Text;
using AgentSignaler.Contracts;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class DnsTxtEndpointResolverTests
{
    private const string Endpoint = "https://fixture-51820.usw2.devtunnels.ms/";
    private static DnsTxtAnswer Answer(string url = Endpoint, int ttl = 600) =>
        new([new([url], TimeSpan.FromSeconds(ttl))]);

    private sealed class Lookup : IDnsTxtLookup
    {
        public int Calls;
        public Func<string, CancellationToken, Task<DnsTxtAnswer>> Query = (_, _) => Task.FromResult(Answer());
        public Task<DnsTxtAnswer> QueryAsync(string recordName, CancellationToken cancellationToken)
        { Calls++; return Query(recordName, cancellationToken); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void SystemResolverOptionsDisableImplicitPublicFallbackAndCaching(int count)
    {
        var servers = Enumerable.Range(0, count).Select(_ => new DnsClient.NameServer(IPAddress.Loopback)).ToArray();
        var options = SystemDnsTxtLookup.CreateOptions(servers);
        Assert.False(options.AutoResolveNameServers);
        Assert.False(options.UseCache);
        Assert.Equal(count, options.NameServers.Count);
        Assert.Equal(0, options.Retries);
        Assert.True(options.UseTcpFallback);
        Assert.Equal(DnsTxtEndpointResolver.RequestDeadline, options.Timeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemDnsTxtLookup.CreateOptions([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemDnsTxtLookup.CreateOptions(
            Enumerable.Repeat(servers[0], 17).ToArray()));
    }

    [Theory]
    [InlineData(DnsClient.DnsResponseCode.ConnectionTimeout, true)]
    [InlineData(DnsClient.DnsResponseCode.ServerFailure, true)]
    [InlineData(DnsClient.DnsResponseCode.Refused, true)]
    [InlineData(DnsClient.DnsResponseCode.FormatError, false)]
    [InlineData(DnsClient.DnsResponseCode.NotExistentDomain, false)]
    public async Task OnlyKnownTransientDnsErrorsRetainUnexpiredLease(DnsClient.DnsResponseCode code, bool retainsLease)
    {
        var clock = new ManualClock();
        var lookup = new Lookup();
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        Assert.NotNull((await resolver.ResolveAsync()).Snapshot);
        clock.Advance(TimeSpan.FromSeconds(30));
        lookup.Query = (_, _) => Task.FromException<DnsTxtAnswer>(new DnsClient.DnsResponseException(code));
        Assert.Equal(retainsLease, (await resolver.RefreshAfterFailureAsync()).Snapshot is not null);
    }

    [Fact]
    public async Task ChunksWithinOneRecordConcatenateButSeparateRecordsNeverDo()
    {
        var lookup = new Lookup { Query = (name, _) =>
        {
            Assert.Equal("signaler.cablesorter.io", name);
            return Task.FromResult(new DnsTxtAnswer([new(["https://fixture-", "51820.usw2.devtunnels.ms/"], TimeSpan.FromSeconds(600))]));
        }};
        var clock = new ManualClock();
        await using var resolver = new DnsTxtEndpointResolver("SIGNALER.CableSorter.IO.", lookup, clock);
        var result = await resolver.ResolveAsync();
        Assert.Equal(Endpoint, result.Snapshot!.BaseUri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(600), result.Snapshot.RemainingLifetime(clock));
        Assert.Equal(TimeSpan.FromSeconds(480), result.NextAttemptUtc - clock.GetUtcNow());
        lookup.Query = (_, _) => Task.FromResult(new DnsTxtAnswer([
            new(["https://fixture-"], TimeSpan.FromSeconds(600)),
            new(["51820.usw2.devtunnels.ms/"], TimeSpan.FromSeconds(600))]));
        clock.Advance(TimeSpan.FromSeconds(480));
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("_signaler.example")]
    [InlineData("-signaler.example")]
    [InlineData("signaler-.example")]
    [InlineData("signaler..example")]
    [InlineData("signaler.example..")]
    [InlineData(" signaler.example")]
    [InlineData("signaler.example/")]
    [InlineData("signaler.éxample")]
    [InlineData("signaler.example\n")]
    public void RejectsInvalidHostnamesWithoutLookup(string name) =>
        Assert.Throws<DiscoveryException>(() => new DnsTxtEndpointResolver(name, new Lookup()));

    [Fact]
    public void HostnameLengthAndLabelLimitsAreExact()
    {
        var maximum = string.Join('.', new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61));
        Assert.Equal(253, maximum.Length);
        Assert.Equal(maximum, DiscoveryProtocol.NormalizeRecordName(maximum + "."));
        Assert.Throws<DiscoveryException>(() => DiscoveryProtocol.NormalizeRecordName(maximum + "x"));
        Assert.Throws<DiscoveryException>(() => DiscoveryProtocol.NormalizeRecordName(new string('a', 64) + ".example"));
    }

    [Theory]
    [InlineData("http://fixture-51820.usw2.devtunnels.ms/")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms.evil.example/")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms:444/")]
    [InlineData("https://user@fixture-51820.usw2.devtunnels.ms/")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms/path")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms/?x=1")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms/#x")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms/../")]
    [InlineData("https://fixture-inspect-51820.usw2.devtunnels.ms/")]
    [InlineData("https://fixture-relay-51820.usw2.devtunnels.ms/")]
    [InlineData("https://fixture-0.usw2.devtunnels.ms/")]
    [InlineData("https://fixture-65536.usw2.devtunnels.ms/")]
    [InlineData("https://fixture-51820.usw2.devtunnels.ms/\n")]
    [InlineData("unavailable")]
    [InlineData("v=spf1 -all")]
    public async Task InvalidTxtFailsClosed(string text)
    {
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(Answer(text)) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup);
        var result = await resolver.ResolveAsync();
        Assert.Null(result.Snapshot);
        Assert.Equal("dns-records-invalid", result.Status);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(443)]
    [InlineData(65535)]
    public void ValidTunnelEmbeddedPortPolicy(int port) =>
        Assert.NotNull(DiscoveryProtocol.ValidateEndpoint(new($"https://fixture-{port}.usw2.devtunnels.ms/")));

    [Fact]
    public void StandardHttpsPortIsAcceptedButNonstandardPortsAreNot() =>
        Assert.NotNull(DiscoveryProtocol.ValidateEndpoint(new("https://fixture-51820.usw2.devtunnels.ms:443/")));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(17)]
    public async Task MissingOrMultipleRecordsIncludingIdenticalOrMixedAreRejected(int records)
    {
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(new DnsTxtAnswer(
            Enumerable.Range(0, records).Select(_ => Answer().Records[0]).ToArray())) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup);
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
    }

    [Theory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public async Task ChunkCountBoundary(int count, bool valid)
    {
        var chunks = Enumerable.Repeat("", count - 1).Append(Endpoint).ToArray();
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(new DnsTxtAnswer([new(chunks, TimeSpan.FromMinutes(10))])) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup);
        Assert.Equal(valid, (await resolver.ResolveAsync()).Snapshot is not null);
    }

    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(2048)]
    [InlineData(2049)]
    public async Task BoundedTxtNeverAllowsUnexpectedLongPayload(int length)
    {
        var text = new string('x', length);
        var chunks = text.Chunk(255).Select(chunk => new string(chunk)).ToArray();
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(new DnsTxtAnswer([new(chunks, TimeSpan.FromMinutes(10))])) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup);
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(599, 599)]
    [InlineData(600, 600)]
    [InlineData(601, 600)]
    [InlineData(int.MaxValue, 600)]
    public async Task TtlIsNeverExtendedAndUpperCapIsTenMinutes(int ttl, int expected)
    {
        var clock = new ManualClock();
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(Answer(ttl: ttl)) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var result = await resolver.ResolveAsync();
        if (expected == 0) Assert.Null(result.Snapshot);
        else
        {
            Assert.Equal(TimeSpan.FromSeconds(expected), result.Snapshot!.RemainingLifetime(clock));
            clock.Advance(TimeSpan.FromSeconds(expected));
            Assert.False(result.Snapshot.IsValid(clock));
        }

    }

    [Fact]
    public async Task ShortTtlExpiresBeforeTheMinimumRetryWithoutSpinOrLeaseExtension()
    {
        var clock = new ManualClock();
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(Answer(ttl: 1)) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var first = await resolver.ResolveAsync();
        Assert.Equal(TimeSpan.FromSeconds(5), first.NextAttemptUtc - clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null((await resolver.RefreshAfterFailureAsync()).Snapshot);
        Assert.Equal(1, lookup.Calls);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.NotNull((await resolver.ResolveAsync()).Snapshot);
        Assert.Equal(2, lookup.Calls);
    }

    [Fact]
    public async Task ResponseLatencyIsDeductedAndAlreadyExpiredResponseCannotCreateLease()
    {
        var clock = new ManualClock();
        var pending = new TaskCompletionSource<DnsTxtAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new Lookup { Query = (_, _) => pending.Task };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var result = resolver.ResolveAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        pending.SetResult(Answer(ttl: 3));
        Assert.Null((await result).Snapshot);
        clock.Advance(TimeSpan.FromSeconds(30));
        lookup.Query = (_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(Answer());
        };
        var fresh = await resolver.ResolveAsync();
        Assert.Equal(TimeSpan.FromSeconds(598), fresh.Snapshot!.RemainingLifetime(clock));
    }

    [Fact]
    public async Task TransientFailureRetainsOnlyOriginalLeaseAndExpiryNeverUsesLateResponse()
    {
        var clock = new ManualClock();
        var lookup = new Lookup();
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var original = (await resolver.ResolveAsync()).Snapshot!;
        clock.Advance(TimeSpan.FromSeconds(480));
        lookup.Query = (_, _) => Task.FromResult(new DnsTxtAnswer([], TransientFailure: true));
        var failure = await resolver.ResolveAsync();
        Assert.Same(original, failure.Snapshot);
        Assert.Equal(TimeSpan.FromSeconds(120), failure.Snapshot!.RemainingLifetime(clock));
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
        Assert.False(original.IsValid(clock));
    }

    [Fact]
    public async Task InvalidOrMissingAnswerInvalidatesEvenUnexpiredLastGoodEndpoint()
    {
        var clock = new ManualClock();
        var lookup = new Lookup();
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        Assert.NotNull((await resolver.ResolveAsync()).Snapshot);
        clock.Advance(TimeSpan.FromSeconds(30));
        lookup.Query = (_, _) => Task.FromResult(new DnsTxtAnswer([]));
        Assert.Null((await resolver.RefreshAfterFailureAsync()).Snapshot);
    }

    [Fact]
    public async Task NormalAndFailureTriggeredRefreshAreBoundedAndUseStableEndpointRevision()
    {
        var clock = new ManualClock();
        var lookup = new Lookup();
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var first = await resolver.ResolveAsync();
        clock.Advance(TimeSpan.FromSeconds(29));
        await resolver.RefreshAfterFailureAsync();
        Assert.Equal(1, lookup.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        var renewal = await resolver.RefreshAfterFailureAsync();
        Assert.Equal(2, lookup.Calls);
        Assert.Equal(first.Snapshot!.EndpointRevision, renewal.Snapshot!.EndpointRevision);
        lookup.Query = (_, _) => Task.FromResult(Answer("https://changed-51820.usw2.devtunnels.ms/"));
        clock.Advance(TimeSpan.FromSeconds(30));
        var rotated = await resolver.RefreshAfterFailureAsync();
        Assert.True(rotated.Snapshot!.EndpointRevision > renewal.Snapshot.EndpointRevision);
        Assert.Equal("changed-51820.usw2.devtunnels.ms", rotated.Snapshot.BaseUri.Host);
    }

    [Fact]
    public async Task BackoffAndRetryBoundaryDoNotSpin()
    {
        var clock = new ManualClock();
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(new DnsTxtAnswer([], true)) };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var first = await resolver.ResolveAsync();
        Assert.Equal(TimeSpan.FromSeconds(30), first.NextAttemptUtc - clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(29));
        await resolver.ResolveAsync();
        Assert.Equal(1, lookup.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await resolver.ResolveAsync();
        Assert.Equal(2, lookup.Calls);
        Assert.Equal(TimeSpan.FromSeconds(60), second.NextAttemptUtc - clock.GetUtcNow());
    }

    [Fact]
    public async Task CallerCancellationAndShutdownCancelOnlyOneInflightLookup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new Lookup { Query = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return Answer();
        }};
        var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup);
        var query = resolver.ResolveAsync();
        await entered.Task;
        var queued = resolver.ResolveAsync();
        await resolver.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await canceled.Task;
        Assert.Equal(1, lookup.Calls);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await using var next = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.ResolveAsync(caller.Token));
        Assert.Equal(1, lookup.Calls);
    }

    [Fact]
    public async Task DeadlineIgnoresLateAnswerAndDoesNotAccumulateUncooperativeRequests()
    {
        var clock = new ManualClock();
        var pending = new TaskCompletionSource<DnsTxtAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookup = new Lookup { Query = (_, _) => pending.Task };
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", lookup, clock);
        var request = resolver.ResolveAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Null((await request).Snapshot);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
        Assert.Equal(1, lookup.Calls);
        pending.SetResult(Answer());
        await pending.Task;
        // The late answer is never committed.
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
    }

    [Fact]
    public void EndpointLeaseUsesMonotonicTimeNotWallClock()
    {
        var clock = new IndependentClock();
        var snapshot = new EffectiveEndpointSnapshot(new(Endpoint), 1, clock.GetUtcNow().AddSeconds(600))
        { VerifiedTimestamp = 0, CacheLifetime = TimeSpan.FromSeconds(600) };
        clock.Utc += TimeSpan.FromDays(10);
        Assert.True(snapshot.IsValid(clock));
        clock.Ticks = TimeSpan.FromSeconds(600).Ticks;
        clock.Utc -= TimeSpan.FromDays(20);
        Assert.False(snapshot.IsValid(clock));
    }

    private sealed class IndependentClock : TimeProvider
    {
        public DateTimeOffset Utc = DateTimeOffset.UtcNow;
        public long Ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HealthTestChecksResolvedOrDirectEndpointWithoutSavingConfiguration(bool discovery)
    {
        var config = new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDashboardUrl("https://direct.example/").ToVersion6();
        if (discovery) config = config.WithDnsTxtDiscovery("signaler.cablesorter.io");
        await using var resolver = new DnsTxtEndpointResolver("signaler.cablesorter.io", new Lookup());
        var handler = new HealthHandler();
        using var http = new HttpClient(handler);
        var endpoint = await DiscoveryConnectionSetup.TestCoreAsync(config, resolver, http);
        Assert.Equal(discovery ? Endpoint : "https://direct.example/", endpoint.BaseUri.AbsoluteUri);
        Assert.Equal(endpoint.BaseUri.Host, handler.Host);
        Assert.Equal($"/api/v{PresenceProtocol.DisplayNameVersion}/health", handler.Path);
    }

    [Fact]
    public async Task HealthTestNeverUsesDirectFallbackForInvalidDns()
    {
        var config = new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDnsTxtDiscovery("signaler.cablesorter.io");
        var lookup = new Lookup { Query = (_, _) => Task.FromResult(new DnsTxtAnswer([])) };
        await using var resolver = new DnsTxtEndpointResolver(config.DnsRecordName!, lookup);
        var handler = new HealthHandler();
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestCoreAsync(config, resolver, http));
        Assert.Null(handler.Host);
    }

    [Fact]
    public async Task HealthTestRejectsWrongReceiverAndHonorsCallerCancellation()
    {
        var config = new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDnsTxtDiscovery("signaler.cablesorter.io");
        await using var resolver = new DnsTxtEndpointResolver(config.DnsRecordName!, new Lookup());
        using var http = new HttpClient(new InvalidHealthHandler());
        await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestCoreAsync(config, resolver, http));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscoveryConnectionSetup.TestAsync(config, caller.Token));
    }

    private sealed class InvalidHealthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"protocolVersion\":999,\"status\":\"ok\"}", Encoding.UTF8, "application/json") });
    }

    private sealed class HealthHandler : HttpMessageHandler
    {
        public string? Host, Path;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Host = request.RequestUri!.Host;
            Path = request.RequestUri.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent($"{{\"protocolVersion\":{PresenceProtocol.DisplayNameVersion},\"status\":\"ok\"}}", Encoding.UTF8, "application/json") });
        }
    }
}
