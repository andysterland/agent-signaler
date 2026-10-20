using System.Net;
using System.Net.Http.Headers;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;

namespace AgentSignaler.Remote;

public sealed class DiscoveryEndpointResolver : IDiscoveryEndpointResolver
{
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinimumFailureRefresh = TimeSpan.FromSeconds(30);
    private readonly DiscoveryConnectionPackage _package;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeProvider _clock;
    private readonly IDiscoveryRevisionStore _revisions;
    private readonly Func<double> _jitter;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private EffectiveEndpointSnapshot? _snapshot;
    private DateTimeOffset _verifiedAt;
    private DateTimeOffset _documentExpiry;
    private long _scheduledAt;
    private TimeSpan _nextDelay;
    private long? _lastFailureRefresh;
    private long? _lastRequestAt;
    private long _serverScheduledAt;
    private TimeSpan _serverDelay;
    private Uri? _verifiedEndpoint;
    private Task? _pendingIo;
    private string? _etag;
    private string _status = "not-resolved";
    private int _failures;
    private long _endpointRevision;
    private bool _disposed;

    public DiscoveryEndpointResolver(DiscoveryConnectionPackage package, HttpClient? client = null,
        TimeProvider? timeProvider = null, IDiscoveryRevisionStore? revisions = null, Func<double>? jitter = null)
    {
        DiscoveryProtocol.ValidatePackage(package);
        _ = DiscoveryCodec.EncodePackage(package);
        _package = package;
        _clock = timeProvider ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _revisions = revisions ?? new MemoryDiscoveryRevisionStore();
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = true, Credentials = null,
            DefaultProxyCredentials = null, ConnectTimeout = RequestDeadline
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = client is null;
    }

    public static DiscoveryEndpointResolver FromConfiguration(RemoteConfiguration configuration, string configPath,
        TimeProvider? timeProvider = null)
    {
        configuration.Validate();
        if (!configuration.IsDiscovery || configuration.Discovery is null)
            throw new InvalidDataException("A private discovery configuration is required.");
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = DiscoveryCredentialTransactionStore.DirectoryPath(configPath);
        var package = new CurrentUserDiscoveryCredentialStore(directory).LoadAsync(configuration.Discovery).GetAwaiter().GetResult();
        if (package.DiscoveryId != configuration.DiscoveryId || package.SigningPublicKey != configuration.DiscoverySigningPublicKey)
            throw new DiscoveryException("discovery_identity_mismatch");
        return new(package, timeProvider: timeProvider,
            revisions: new ProtectedDiscoveryRevisionStore(Path.Combine(Path.GetDirectoryName(configPath)!, "discovery-replay")));
    }

    public Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default)
        => ResolveCoreAsync(false, cancellationToken);

    public Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default)
        => ResolveCoreAsync(true, cancellationToken);

    private DiscoveryResolution Result()
    {
        if (_snapshot is not null && !_snapshot.IsValid(_clock))
        {
            _snapshot = null;
            if (_status == "verified") _status = "endpoint-cache-expired";
        }
        var remaining = RemainingDelay(_scheduledAt, _nextDelay);
        var server = RemainingDelay(_serverScheduledAt, _serverDelay);
        if (server > remaining) remaining = server;
        var now = _clock.GetUtcNow();
        return new(_snapshot, _status, remaining >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + remaining);
    }

    private async Task<DiscoveryResolution> ResolveCoreAsync(bool failureTriggered, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _singleFlight.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (RemainingDelay(_serverScheduledAt, _serverDelay) > TimeSpan.Zero) return Result();
            if (RemainingDelay(_scheduledAt, _nextDelay) > TimeSpan.Zero &&
                (!failureTriggered || ElapsedSince(_lastFailureRefresh) < MinimumFailureRefresh))
                return Result();
            if (failureTriggered)
            {
                if (ElapsedSince(_lastFailureRefresh) < MinimumFailureRefresh || ElapsedSince(_lastRequestAt) < MinimumFailureRefresh)
                    return Result();
                _lastFailureRefresh = _clock.GetTimestamp();
            }
            if (_pendingIo is { IsCompleted: false }) return Failure("storage-timeout", false);
            _lastRequestAt = _clock.GetTimestamp();
            using var timeout = new CancellationTokenSource(RequestDeadline, _clock);
            using var requestToken = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _package.BlobSasUri);
                request.Headers.Accept.Add(new("application/json"));
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                if (_etag is not null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(_etag));
                var send = _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken.Token);
                _pendingIo = send;
                HttpResponseMessage received;
                try { received = await send.WaitAsync(requestToken.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!send.IsCompleted)
                {
                    _ = send.ContinueWith(completed =>
                    {
                        if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                        else _ = completed.Exception;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }
                using var response = received;
                var retryAfter = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - _clock.GetUtcNow() : null);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    return Failure(response.StatusCode == HttpStatusCode.NotFound ? "blob-missing" : "reader-unauthorized", true, retryAfter);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    if (_etag is null || _verifiedEndpoint is null || _clock.GetUtcNow() >= _documentExpiry)
                        return Failure("document-expired", true);
                    _verifiedAt = _clock.GetUtcNow();
                    _snapshot = CreateSnapshot(_verifiedEndpoint);
                    return Success("verified");
                }
                if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout ||
                    (int)response.StatusCode >= 500)
                    return Failure(response.StatusCode == HttpStatusCode.TooManyRequests ? "storage-throttled" : "storage-unavailable", false, retryAfter);
                if (response.StatusCode != HttpStatusCode.OK)
                    return Failure((int)response.StatusCode is >= 300 and <= 399 ? "redirect-refused" : "storage-response-invalid", true);
                if (response.Content.Headers.ContentType?.MediaType != "application/json" ||
                    response.Content.Headers.ContentLength > 8192)
                    return Failure("document-invalid", true);
                var streamTask = response.Content.ReadAsStreamAsync(requestToken.Token);
                _pendingIo = streamTask;
                Stream contentStream;
                try { contentStream = await streamTask.WaitAsync(requestToken.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!streamTask.IsCompleted)
                {
                    _ = streamTask.ContinueWith(completed =>
                    {
                        if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                        else _ = completed.Exception;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    throw;
                }
                using var stream = contentStream;
                var bytes = new byte[8193];
                var count = 0;
                while (count < bytes.Length)
                {
                    var readTask = stream.ReadAsync(bytes.AsMemory(count), requestToken.Token).AsTask();
                    _pendingIo = readTask;
                    _ = readTask.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    var read = await readTask.WaitAsync(requestToken.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }
                if (count > 8192) return Failure("document-invalid", true);
                var document = VerifyDocument(bytes.AsSpan(0, count));
                if (document.Endpoint is null)
                {
                    _etag = null;
                    return Failure("publisher-unavailable", true);
                }
                if (_verifiedEndpoint != document.Endpoint) _endpointRevision = checked(_endpointRevision + 1);
                _verifiedEndpoint = document.Endpoint;
                _verifiedAt = _clock.GetUtcNow();
                _documentExpiry = document.ExpiresAtUtc;
                _snapshot = CreateSnapshot(document.Endpoint);
                _etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                return Success("verified");
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { return Failure("storage-timeout", false); }
            catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
            { return Failure("tls-validation-failed", true); }
            catch (HttpRequestException) { return Failure("storage-unavailable", false); }
            catch (IOException) { return Failure("storage-unavailable", false); }
            catch (Exception ex) when (ex is DiscoveryException or InvalidDataException or System.Security.Cryptography.CryptographicException or
                System.Text.Json.JsonException or FormatException or OverflowException)
            { return Failure("document-invalid", true); }
        }
        finally { _singleFlight.Release(); }
    }

    private (Uri? Endpoint, DateTimeOffset ExpiresAtUtc) VerifyDocument(ReadOnlySpan<byte> bytes)
    {
        var checkpoint = _revisions.Read(_package.DiscoveryId);
        var previous = checkpoint is null ? null : new DiscoveryReplayState(_package.DiscoveryId,
            checkpoint.Revision, checkpoint.PayloadFingerprint);
        var verified = DiscoveryCodec.Verify(bytes, _package, _clock.GetUtcNow(), previous);
        _revisions.Write(_package.DiscoveryId, new(verified.ReplayState.Revision, verified.ReplayState.PayloadFingerprint));
        return (verified.Document.DashboardBaseUrl, verified.Document.ExpiresAtUtc);
    }

    private DateTimeOffset CacheExpiry()
    {
        var expiry = _verifiedAt + MaximumCacheAge;
        return expiry < _documentExpiry ? expiry : _documentExpiry;
    }

    private EffectiveEndpointSnapshot CreateSnapshot(Uri endpoint)
    {
        var expiry = CacheExpiry();
        return new(endpoint, _endpointRevision, expiry)
        {
            VerifiedTimestamp = _clock.GetTimestamp(), CacheLifetime = expiry - _verifiedAt
        };
    }

    private DiscoveryResolution Success(string status)
    {
        _failures = 0;
        _status = status;
        _scheduledAt = _clock.GetTimestamp();
        _nextDelay = TimeSpan.FromMinutes(5) * (0.9 + 0.2 * UnitJitter());
        return Result();
    }

    private DiscoveryResolution Failure(string status, bool invalidate, TimeSpan? retryAfter = null)
    {
        _status = status;
        if (invalidate) { _snapshot = null; _verifiedEndpoint = null; _etag = null; }
        _failures = Math.Min(_failures + 1, 5);
        var seconds = Math.Min(300, 30 * Math.Pow(2, _failures - 1));
        var delay = TimeSpan.FromSeconds(Math.Clamp(seconds * (0.9 + 0.2 * UnitJitter()), 30, 300));
        if (retryAfter is { } requested && requested > delay) delay = requested;
        _scheduledAt = _clock.GetTimestamp();
        _nextDelay = delay;
        if (retryAfter is { } serverRetry && serverRetry > TimeSpan.Zero)
        {
            _serverScheduledAt = _scheduledAt;
            _serverDelay = delay;
        }
        return Result();
    }

    private TimeSpan RemainingDelay(long started, TimeSpan delay)
    {
        var remaining = delay - _clock.GetElapsedTime(started);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan ElapsedSince(long? timestamp) => timestamp is { } value ? _clock.GetElapsedTime(value) : TimeSpan.MaxValue;

    private double UnitJitter() => Math.Clamp(_jitter(), 0, 1);

    public static string DescribeStatus(string status) => status switch
    {
        "reader-unauthorized" => "Private discovery read access was denied. Ask the Dashboard owner to renew or replace the reader-group credential; tunnel sign-in will not fix Blob authorization.",
        "blob-missing" => "The private discovery document is missing. Ask the Dashboard owner to repair discovery.",
        "publisher-unavailable" => "The Dashboard publisher marked sharing unavailable. Wait for verified Internet sharing.",
        "document-expired" or "document-invalid" => "Discovery trust or freshness verification failed. Check the system clock and ask the Dashboard owner to republish; reimport only an explicitly issued replacement package.",
        "endpoint-cache-expired" => "The last verified discovery endpoint lease expired. Reporting is suspended until Azure Storage can revalidate a fresh signed document.",
        "tls-validation-failed" => "Private discovery TLS verification failed. Check network and system trust; certificate validation cannot be bypassed.",
        "redirect-refused" => "Private discovery redirected. Only the exact configured Azure Blob endpoint is allowed.",
        "storage-throttled" => "Azure Storage is throttling discovery. Retry after the indicated delay.",
        _ => "Private discovery is unavailable. Check network access to Azure Blob Storage and retry. Reporting is suspended when the verified endpoint lease expires."
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _singleFlight.WaitAsync().ConfigureAwait(false);
        _singleFlight.Release();
        if (_ownsClient) _client.Dispose();
        _lifetime.Dispose();
    }

    private sealed class MemoryDiscoveryRevisionStore : IDiscoveryRevisionStore
    {
        private readonly Dictionary<Guid, DiscoveryRevisionCheckpoint> _state = [];
        public DiscoveryRevisionCheckpoint? Read(Guid discoveryId) => _state.GetValueOrDefault(discoveryId);
        public void Write(Guid discoveryId, DiscoveryRevisionCheckpoint checkpoint) => _state[discoveryId] = checkpoint;
    }

    private sealed class ProtectedDiscoveryRevisionStore(string directory) : IDiscoveryRevisionStore
    {
        public DiscoveryRevisionCheckpoint? Read(Guid discoveryId)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var checkpoint = new CurrentUserDiscoveryReplayStore(directory).Load(discoveryId);
            return checkpoint is null ? null : new(checkpoint.Revision, checkpoint.PayloadFingerprint);
        }

        public void Write(Guid discoveryId, DiscoveryRevisionCheckpoint checkpoint)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            new CurrentUserDiscoveryReplayStore(directory).Save(new(discoveryId, checkpoint.Revision, checkpoint.PayloadFingerprint));
        }
    }
}