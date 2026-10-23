using AgentSignaler.Contracts;
using DnsClient;
using DnsClient.Protocol;

namespace AgentSignaler.Remote;

public sealed record DnsTxtRecord(IReadOnlyList<string> Chunks, TimeSpan TimeToLive);
public sealed record DnsTxtAnswer(IReadOnlyList<DnsTxtRecord> Records, bool TransientFailure = false);

public interface IDnsTxtLookup
{
    Task<DnsTxtAnswer> QueryAsync(string recordName, CancellationToken cancellationToken);
}

/// <summary>Uses the machine's configured DNS servers, not a public DoH service or a saved address.</summary>
public sealed class SystemDnsTxtLookup : IDnsTxtLookup
{
    public const int MaximumNameServers = 16;
    internal static LookupClientOptions CreateOptions(NameServer[] servers)
    {
        if (servers.Length is 0 or > MaximumNameServers) throw new ArgumentOutOfRangeException(nameof(servers));
        return new(servers)
        {
            AutoResolveNameServers = false,
            UseCache = false, Retries = 0, Timeout = DnsTxtEndpointResolver.RequestDeadline,
            UseTcpFallback = true, ThrowDnsErrors = false
        };
    }

    public async Task<DnsTxtAnswer> QueryAsync(string recordName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Explicitly disable the library's public-server fallback, including when the
        // machine has no configured DNS servers. Re-read adapters after network changes.
        var servers = NameServer.ResolveNameServers(skipIPv6SiteLocal: true, fallbackToGooglePublicDns: false)
            .Take(MaximumNameServers + 1).ToArray();
        if (servers.Length is 0 or > MaximumNameServers) return new([], TransientFailure: true);
        var client = new LookupClient(CreateOptions(servers));
        var result = await client.QueryAsync(recordName, QueryType.TXT, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (result.HasError)
            return new([], result.Header.ResponseCode is DnsHeaderResponseCode.ServerFailure or DnsHeaderResponseCode.Refused);
        // Fail closed on unexpected answer types and bound the materialized data. CNAME aliases
        // are deliberately unsupported: the selected name must itself carry the one TXT RR.
        if (result.Answers.Count > DiscoveryProtocol.MaximumRecords ||
            result.Answers.Any(record => record is not TxtRecord ||
                !record.DomainName.Value.TrimEnd('.').Equals(recordName, StringComparison.OrdinalIgnoreCase)))
            throw new DiscoveryException("dns_records");
        return new(result.Answers.Cast<TxtRecord>().Select(record =>
            new DnsTxtRecord(record.Text.ToArray(), TimeSpan.FromSeconds(record.TimeToLive))).ToArray());
    }
}

/// <summary>One in-flight lookup and an in-memory, monotonic TTL lease. DNS is public, not authenticated.</summary>
public sealed class DnsTxtEndpointResolver : IDiscoveryEndpointResolver
{
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumCacheAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinimumFailureRefresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MinimumRetry = TimeSpan.FromSeconds(5);
    private readonly string _recordName;
    private readonly IDnsTxtLookup _lookup;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private EffectiveEndpointSnapshot? _snapshot;
    private Task<DnsTxtAnswer>? _pending;
    private long _scheduledAt;
    private TimeSpan _nextDelay;
    private long? _lastRequestAt;
    private string _status = "not-resolved";
    private int _failures;
    private long _endpointRevision;
    private Uri? _lastEndpoint;
    private bool _disposed;

    public DnsTxtEndpointResolver(string recordName, IDnsTxtLookup? lookup = null, TimeProvider? timeProvider = null)
    {
        _recordName = DiscoveryProtocol.NormalizeRecordName(recordName);
        _lookup = lookup ?? new SystemDnsTxtLookup();
        _clock = timeProvider ?? TimeProvider.System;
    }

    public static DnsTxtEndpointResolver FromConfiguration(RemoteConfiguration configuration, TimeProvider? timeProvider = null)
    {
        configuration.Validate();
        if (!configuration.IsDiscovery) throw new InvalidDataException("A DNS TXT discovery configuration is required.");
        return new(configuration.DnsRecordName!, timeProvider: timeProvider);
    }

    public Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(false, cancellationToken);
    public Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default) =>
        ResolveCoreAsync(true, cancellationToken);

    private DiscoveryResolution Result()
    {
        if (_snapshot is not null && !_snapshot.IsValid(_clock))
        {
            _snapshot = null;
            _status = "endpoint-cache-expired";
        }
        var delay = _nextDelay - _clock.GetElapsedTime(_scheduledAt);
        return new(_snapshot, _status, _clock.GetUtcNow() + (delay > TimeSpan.Zero ? delay : TimeSpan.Zero));
    }

    private async Task<DiscoveryResolution> ResolveCoreAsync(bool failureTriggered, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _singleFlight.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_disposed) throw new OperationCanceledException("DNS discovery stopped.", linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_lastRequestAt is { } last &&
                (_clock.GetElapsedTime(last) < MinimumRetry ||
                (_clock.GetElapsedTime(_scheduledAt) < _nextDelay &&
                    (!failureTriggered || _clock.GetElapsedTime(last) < MinimumFailureRefresh))))
                return Result();
            // A cancellation-uncooperative injected resolver can never accumulate requests,
            // and its eventual result is never used to renew an expired lease.
            if (_pending is { IsCompleted: false }) return Failure("dns-timeout", false);
            _lastRequestAt = _clock.GetTimestamp();
            var started = _lastRequestAt.Value;
            using var timeout = new CancellationTokenSource(RequestDeadline, _clock);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
            try
            {
                _pending = _lookup.QueryAsync(_recordName, request.Token);
                _ = _pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var answer = await _pending.WaitAsync(request.Token).ConfigureAwait(false);
                request.Token.ThrowIfCancellationRequested();
                if (answer.TransientFailure) return Failure("dns-unavailable", false);
                if (answer.Records.Count != 1 || answer.Records.Count > DiscoveryProtocol.MaximumRecords)
                    return Failure("dns-records-invalid", true);
                var record = answer.Records[0];
                if (record.Chunks.Count is < 1 or > DiscoveryProtocol.MaximumTxtChunks ||
                    record.Chunks.Any(chunk => chunk is null || chunk.Length > 255) ||
                    record.Chunks.Sum(chunk => chunk.Length) is < 1 or > DiscoveryProtocol.MaximumTxtLength)
                    return Failure("dns-records-invalid", true);
                var text = string.Concat(record.Chunks);
                if (text.Any(c => c > 127 || char.IsControl(c)) ||
                    !Uri.TryCreate(text, UriKind.Absolute, out var endpoint))
                    return Failure("dns-records-invalid", true);
                DiscoveryProtocol.ValidateEndpoint(endpoint);
                // Start at the request timestamp, not completion: latency must not extend TTL.
                var lifetime = record.TimeToLive < MaximumCacheAge ? record.TimeToLive : MaximumCacheAge;
                var remaining = lifetime - _clock.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) return Failure("endpoint-cache-expired", true);
                if (_lastEndpoint != endpoint) _endpointRevision = checked(_endpointRevision + 1);
                _lastEndpoint = endpoint;
                _snapshot = new(endpoint, _endpointRevision, _clock.GetUtcNow() + remaining)
                { VerifiedTimestamp = started, CacheLifetime = lifetime };
                _failures = 0;
                _status = "resolved";
                _scheduledAt = _clock.GetTimestamp();
                // Refresh at 80% of the remaining TTL; never stretch short or zero TTLs.
                _nextDelay = TimeSpan.FromTicks(Math.Max(MinimumRetry.Ticks, remaining.Ticks * 4 / 5));
                return Result();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { return Failure("dns-timeout", false); }
            catch (DnsResponseException ex) when (ex.Code is DnsResponseCode.ConnectionTimeout or DnsResponseCode.ServerFailure or DnsResponseCode.Refused)
            { return Failure("dns-unavailable", false); }
            catch (DnsResponseException) { return Failure("dns-records-invalid", true); }
            catch (System.Net.Sockets.SocketException) { return Failure("dns-unavailable", false); }
            catch (System.Net.NetworkInformation.NetworkInformationException) { return Failure("dns-unavailable", false); }
            catch (IOException) { return Failure("dns-unavailable", false); }
            catch (DiscoveryException) { return Failure("dns-records-invalid", true); }
        }
        finally { _singleFlight.Release(); }
    }

    private DiscoveryResolution Failure(string status, bool invalidate)
    {
        _status = status;
        if (invalidate) _snapshot = null;
        _failures = Math.Min(5, _failures + 1);
        _scheduledAt = _clock.GetTimestamp();
        _nextDelay = TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << (_failures - 1))));
        if (_snapshot is { } snapshot && snapshot.IsValid(_clock))
        {
            var earlyRetry = TimeSpan.FromTicks(snapshot.RemainingLifetime(_clock).Ticks / 2);
            if (earlyRetry < _nextDelay) _nextDelay = earlyRetry > MinimumRetry ? earlyRetry : MinimumRetry;
        }
        return Result();
    }

    public static string DescribeStatus(string status) => status switch
    {
        "dns-records-invalid" => "DNS TXT discovery returned missing, ambiguous or invalid data. Reporting is suspended; check the selected record with its owner. No direct fallback was used.",
        "endpoint-cache-expired" => "The DNS endpoint TTL expired. Reporting is suspended until a fresh valid TXT answer is available.",
        _ => "DNS TXT discovery is temporarily unavailable. Check the system DNS resolver and network. The last endpoint is usable only within its original TTL. DNS discovery does not authenticate the endpoint."
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _singleFlight.WaitAsync().ConfigureAwait(false);
        _singleFlight.Release();
        _lifetime.Dispose();
    }
}
