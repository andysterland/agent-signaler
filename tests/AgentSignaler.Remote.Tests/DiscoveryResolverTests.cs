using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class DiscoveryResolverTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly Guid _identity = Guid.NewGuid();
    private readonly ClientRuntimeTests.ManualClock _clock = new();
    private static readonly Uri Endpoint = new("https://abcd-51820.use.devtunnels.ms/");
    private DiscoveryConnectionPackage Package => new(_identity,
        new Uri("https://fixtureaccount.blob.core.windows.net/discovery-fixture/endpoint.json?sv=2025-06-01&sr=b&spr=https&si=reader-fixture&sig=" +
            Uri.EscapeDataString(Convert.ToBase64String(new byte[32]))), DiscoveryCodec.ExportPublicKey(_key));

    private byte[] Document(long revision = 1, DiscoveryState state = DiscoveryState.Available, TimeSpan? validity = null)
        => DiscoveryCodec.Sign(new(1, _identity, revision, _clock.GetUtcNow(),
            _clock.GetUtcNow() + (validity ?? TimeSpan.FromHours(24)), state,
            state == DiscoveryState.Available ? Endpoint : null), _key);

    private static HttpResponseMessage Json(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("application/json") } },
        Headers = { ETag = new("\"fixture-etag\"") }
    };

    [Fact]
    public async Task VerifiedConditionalReadsPreserveRevisionAndNeverExtendSignedExpiry()
    {
        var body = Document(validity: TimeSpan.FromMinutes(1));
        var handler = new Handler((_, index, _) => Task.FromResult(index == 1 ? Json(body) : new HttpResponseMessage(HttpStatusCode.NotModified)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => 0.5);
        var first = await resolver.ResolveAsync();
        Assert.Equal(Endpoint, first.Snapshot!.BaseUri);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(1), first.Snapshot.ValidUntilUtc);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var second = await resolver.RefreshAfterFailureAsync();
        Assert.Equal(first.Snapshot.BaseUri, second.Snapshot!.BaseUri);
        Assert.Equal(first.Snapshot.EndpointRevision, second.Snapshot.EndpointRevision);
        Assert.Equal(first.Snapshot.ValidUntilUtc, second.Snapshot.ValidUntilUtc);
        Assert.True(handler.Requests.Last().Conditional);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var expired = await resolver.RefreshAfterFailureAsync();
        Assert.Null(expired.Snapshot);
        Assert.Equal("document-expired", expired.Status);
        Assert.All(handler.Requests, r => Assert.Equal("fixtureaccount.blob.core.windows.net", r.Host));
    }

    [Fact]
    public async Task TransientOutageUsesOnlyTenMinuteVerifiedCache()
    {
        var bytes = Document();
        var handler = new Handler((_, count, _) => Task.FromResult(count == 1 ? Json(bytes) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => 0.5);
        var first = await resolver.ResolveAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));
        var outage = await resolver.ResolveAsync();
        Assert.Equal(first.Snapshot, outage.Snapshot);
        Assert.Equal("storage-unavailable", outage.Status);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(401, "reader-unauthorized")]
    [InlineData(403, "reader-unauthorized")]
    [InlineData(404, "blob-missing")]
    [InlineData(302, "redirect-refused")]
    public async Task AuthorizationMissingAndRedirectResponsesInvalidateImmediately(int status, string category)
    {
        var bytes = Document();
        var handler = new Handler((_, count, _) => Task.FromResult(count == 1 ? Json(bytes) : new HttpResponseMessage((HttpStatusCode)status)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => 0.5);
        Assert.NotNull((await resolver.ResolveAsync()).Snapshot);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var rejected = await resolver.RefreshAfterFailureAsync();
        Assert.Null(rejected.Snapshot);
        Assert.Equal(category, rejected.Status);
        Assert.DoesNotContain("anonymous", DiscoveryEndpointResolver.DescribeStatus(category), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task UtcJumpsCannotBypassFailureRefreshFloorOrStretchPeriodicRefresh()
    {
        var clock = new MovingClock { Utc = _clock.GetUtcNow() };
        var bytes = Document();
        var handler = new Handler((_, _, _) => Task.FromResult(Json(bytes)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, clock, jitter: () => 0.5);
        await resolver.ResolveAsync();
        clock.Utc = clock.Utc.AddHours(1);
        await resolver.RefreshAfterFailureAsync();
        Assert.Single(handler.Requests);
        clock.Utc = clock.Utc.AddHours(-2);
        var cached = await resolver.ResolveAsync();
        Assert.Equal(clock.Utc.AddMinutes(5), cached.NextAttemptUtc);
        clock.Elapsed = TimeSpan.FromSeconds(30).Ticks;
        await resolver.RefreshAfterFailureAsync();
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SignedUnavailableAndInvalidSignaturesInvalidateExistingCache()
    {
        var valid = Document();
        var unavailable = Document(2, DiscoveryState.Unavailable);
        var handler = new Handler((_, count, _) => Task.FromResult(Json(count == 1 ? valid : unavailable)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => 0.5);
        Assert.NotNull((await resolver.ResolveAsync()).Snapshot);
        _clock.Advance(TimeSpan.FromSeconds(30));
        var result = await resolver.RefreshAfterFailureAsync();
        Assert.Null(result.Snapshot);
        Assert.Equal("publisher-unavailable", result.Status);
        unavailable[unavailable.Length - 4] ^= 1;
        _clock.Advance(TimeSpan.FromSeconds(30));
        result = await resolver.ResolveAsync();
        Assert.Null(result.Snapshot);
        Assert.Equal("document-invalid", result.Status);
    }

    [Fact]
    public async Task PersistedCheckpointRejectsReplayAcrossResolverInstances()
    {
        var checkpoint = new RevisionStore();
        var newer = Document(2);
        var older = Document(1);
        using var firstClient = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(newer))));
        await using (var first = new DiscoveryEndpointResolver(Package, firstClient, _clock, checkpoint))
            Assert.NotNull((await first.ResolveAsync()).Snapshot);
        using var secondClient = new HttpClient(new Handler((_, _, _) => Task.FromResult(Json(older))));
        await using var second = new DiscoveryEndpointResolver(Package, secondClient, _clock, checkpoint);
        var result = await second.ResolveAsync();
        Assert.Null(result.Snapshot);
        Assert.Equal("document-invalid", result.Status);
        Assert.Equal(2, checkpoint.Value!.Revision);
    }

    [Fact]
    public async Task FailureRefreshHasThirtySecondFloorAndHonorsLongerServerThrottle()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(11));
        var handler = new Handler((_, _, _) => Task.FromResult(response));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => 0);
        var result = await resolver.ResolveAsync();
        Assert.Equal(_clock.GetUtcNow().AddMinutes(11), result.NextAttemptUtc);
        await resolver.RefreshAfterFailureAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        await resolver.RefreshAfterFailureAsync();
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneRequestAndOneVerifiedSnapshot()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = Document();
        var handler = new Handler(async (_, _, token) => { entered.SetResult(); await release.Task.WaitAsync(token); return Json(body); });
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock);
        var first = resolver.ResolveAsync();
        await entered.Task;
        var second = resolver.ResolveAsync();
        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Single(handler.Requests);
        Assert.Equal(results[0].Snapshot, results[1].Snapshot);
        Assert.NotNull(results[1].Snapshot);
    }

    [Fact]
    public async Task DeadlineBoundsIgnoringAdapterAndRetainsSingleInflightBarrier()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler((_, _, _) => { entered.TrySetResult(); return pending.Task; });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => 0.5);
        var request = resolver.ResolveAsync();
        await entered.Task;
        _clock.Advance(TimeSpan.FromSeconds(5));
        var result = await request.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("storage-timeout", result.Status);
        Assert.Null(result.Snapshot);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null((await resolver.ResolveAsync()).Snapshot);
        Assert.Single(handler.Requests);
        pending.SetResult(new(HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task OversizedStreamWithoutContentLengthFailsAtEightKiBBoundary()
    {
        var handler = new Handler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamingContent(new byte[8193]) { Headers = { ContentType = new("application/json") } } }));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock);
        var result = await resolver.ResolveAsync();
        Assert.Equal("document-invalid", result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task UnexpectedMediaTypeAndTlsFailuresNeverProduceAnEndpoint()
    {
        var bytes = Document();
        var handler = new Handler((_, _, _) =>
        {
            var response = Json(bytes);
            response.Content.Headers.ContentType = new("text/html");
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        await using (var resolver = new DiscoveryEndpointResolver(Package, client, _clock))
        {
            var result = await resolver.ResolveAsync();
            Assert.Null(result.Snapshot);
            Assert.Equal("document-invalid", result.Status);
        }
        using var tlsClient = new HttpClient(new Handler((_, _, _) => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError, "Synthetic TLS failure")));
        await using var tls = new DiscoveryEndpointResolver(Package, tlsClient, _clock);
        var rejected = await tls.ResolveAsync();
        Assert.Null(rejected.Snapshot);
        Assert.Equal("tls-validation-failed", rejected.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RetryJitterStaysWithinThirtySecondsAndFiveMinutes(double jitter)
    {
        var handler = new Handler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock, jitter: () => jitter);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var result = await resolver.ResolveAsync();
            var delay = result.NextAttemptUtc - _clock.GetUtcNow();
            Assert.InRange(delay, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
            _clock.Advance(delay);
        }
        Assert.Equal(8, handler.Requests.Count);
    }

    [Fact]
    public async Task RepeatedConnectionFailuresCannotRefreshBeforeThirtySeconds()
    {
        var bytes = Document();
        var handler = new Handler((_, _, _) => Task.FromResult(Json(bytes)));
        using var client = new HttpClient(handler);
        await using var resolver = new DiscoveryEndpointResolver(Package, client, _clock);
        await resolver.ResolveAsync();
        for (var count = 0; count < 20; count++) await resolver.RefreshAfterFailureAsync();
        _clock.Advance(TimeSpan.FromSeconds(30).Add(TimeSpan.FromTicks(-1)));
        await resolver.RefreshAfterFailureAsync();
        Assert.Single(handler.Requests);
        _clock.Advance(TimeSpan.FromTicks(1));
        await resolver.RefreshAfterFailureAsync();
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task DisposalCancelsOutstandingDiscoveryAndCannotResurrectRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, _, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.SetResult(); throw; }
            throw new InvalidOperationException();
        });
        using var client = new HttpClient(handler);
        var resolver = new DiscoveryEndpointResolver(Package, client, _clock);
        var request = resolver.ResolveAsync();
        await entered.Task;
        await resolver.DisposeAsync();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => resolver.ResolveAsync());
        Assert.Single(handler.Requests);
    }

    public void Dispose() => _key.Dispose();

    private sealed class Handler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public ConcurrentQueue<(string Host, bool Conditional)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.Host, request.Headers.IfNoneMatch.Count != 0));
            return send(request, Requests.Count, cancellationToken);
        }
    }

    private sealed class RevisionStore : IDiscoveryRevisionStore
    {
        public DiscoveryRevisionCheckpoint? Value { get; private set; }
        public DiscoveryRevisionCheckpoint? Read(Guid discoveryId) => Value;
        public void Write(Guid discoveryId, DiscoveryRevisionCheckpoint checkpoint) => Value = checkpoint;
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => CreateContentReadStreamAsync();
    }

    private sealed class MovingClock : TimeProvider
    {
        public DateTimeOffset Utc;
        public long Elapsed;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => Elapsed;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}