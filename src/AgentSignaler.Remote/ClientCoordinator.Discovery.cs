namespace AgentSignaler.Remote;

public sealed partial class ClientCoordinator
{
    private readonly Func<RemoteConfiguration, IDiscoveryEndpointResolver> _discoveryResolverFactory;
    private IDiscoveryEndpointResolver? _discoveryResolver;
    private CancellationTokenSource? _discoveryRun;
    private Task? _discoveryWorker;
    private EffectiveEndpointSnapshot? _endpointSnapshot;
    private ITimer? _endpointExpiry;
    private string _discoveryStatus = "not-resolved";
    private string? _discoveryError;
    private readonly SemaphoreSlim _discoveryWake = new(0, 1);

    public string DiscoveryStatusText
    {
        get
        {
            lock (_sync) return !_configuration.IsDiscovery ? "Direct URL" :
                _endpointSnapshot?.IsValid(_clock) == true ? _discoveryStatus == "verified" ? "Discovery: verified" :
                    "Discovery: cached; storage unavailable" : "Discovery: unavailable";
        }
    }

    private IDiscoveryEndpointResolver CreateDiscoveryResolver(RemoteConfiguration configuration)
        => DiscoveryEndpointResolver.FromConfiguration(configuration, _configPath, _clock);

    private void StartDiscovery()
    {
        _discoveryRun = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var run = _discoveryRun;
        var configuration = _configuration;
        var revision = _revision;
        _discoveryWorker = Task.Run(() => DiscoverAsync(configuration, revision, run));
        _ = _discoveryWorker.ContinueWith(WorkerFaulted, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void StopDiscovery()
    {
        _endpointExpiry?.Dispose();
        _endpointExpiry = null;
        _discoveryRun?.Cancel();
    }

    private void RequestDiscoveryRefresh()
    {
        lock (_sync)
            if (_configuration.IsDiscovery && !_stopping && _discoveryWake.CurrentCount == 0)
                _discoveryWake.Release();
    }

    private async Task DiscoverAsync(RemoteConfiguration configuration, string revision, CancellationTokenSource run)
    {
        IDiscoveryEndpointResolver? resolver = null;
        try
        {
            resolver = _discoveryResolverFactory(configuration);
            lock (_sync)
            {
                if (!ReferenceEquals(run, _discoveryRun) || _stopping) return;
                _discoveryResolver = resolver;
            }
            var failureTriggered = false;
            while (!run.IsCancellationRequested)
            {
                var result = failureTriggered
                    ? await resolver.RefreshAfterFailureAsync(run.Token)
                    : await resolver.ResolveAsync(run.Token);
                await CommitDiscoveryEndpointAsync(result.Snapshot, revision, run);
                lock (_sync)
                {
                    if (ReferenceEquals(run, _discoveryRun) && _revision == revision && !_stopping)
                    {
                        _discoveryStatus = result.Status;
                        _discoveryError = result.Snapshot is null || result.Status != "verified"
                            ? DiscoveryEndpointResolver.DescribeStatus(result.Status) : _endpointSnapshot is null
                            ? "Discovery is verified; previous transport work is still draining. Reporting remains suspended." : null;
                    }
                }
                var delay = result.NextAttemptUtc - _clock.GetUtcNow();
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
                var timer = Task.Delay(delay, _clock, wait.Token);
                var trigger = _discoveryWake.WaitAsync(wait.Token);
                var completed = await Task.WhenAny(timer, trigger);
                await completed;
                failureTriggered = ReferenceEquals(completed, trigger);
                await wait.CancelAsync();
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex) || ex is AgentSignaler.Contracts.DiscoveryException or System.Security.Cryptography.CryptographicException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(run, _discoveryRun) && !_stopping)
                {
                    InvalidateDiscoveryEndpoint();
                    _discoveryStatus = "credential-unavailable";
                    _discoveryError = "Discovery credentials or protected revision state are unavailable. Repair the saved connection in Configurator.";
                    _log.Write("discovery-state-unavailable");
                }
            }
        }
        finally
        {
            if (resolver is not null) await resolver.DisposeAsync();
            lock (_sync)
                if (ReferenceEquals(_discoveryResolver, resolver)) _discoveryResolver = null;
        }
    }

    private void InvalidateDiscoveryEndpoint()
    {
        _endpointSnapshot = null;
        _endpointExpiry?.Dispose();
        _endpointExpiry = null;
        _connected = false;
        _effectiveRevision = null;
        _startedAcknowledged = false;
        _interrupt.Cancel();
        SuspendTranscripts("discovery-unavailable", invalidateEndpoint: true);
        _hooks.Clear();
        _pending = null;
    }

    private void ScheduleEndpointExpiry(EffectiveEndpointSnapshot snapshot)
    {
        _endpointExpiry?.Dispose();
        var due = snapshot.RemainingLifetime(_clock);
        _endpointExpiry = _clock.CreateTimer(_ =>
        {
            lock (_sync)
            {
                if (_stopping || !ReferenceEquals(snapshot, _endpointSnapshot) || snapshot.IsValid(_clock)) return;
                InvalidateDiscoveryEndpoint();
                _discoveryStatus = "endpoint-cache-expired";
                _discoveryError = DiscoveryEndpointResolver.DescribeStatus(_discoveryStatus);
                RequestDiscoveryRefresh();
            }
        }, null, due > TimeSpan.Zero ? due : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    private async Task CommitDiscoveryEndpointAsync(EffectiveEndpointSnapshot? endpoint, string revision,
        CancellationTokenSource run)
    {
        lock (_sync)
        {
            if (_stopping || _reloading || _revision != revision || !ReferenceEquals(run, _discoveryRun)) return;
            if (endpoint is null || !endpoint.IsValid(_clock)) { InvalidateDiscoveryEndpoint(); return; }
            if (_endpointSnapshot is { } current && current.BaseUri == endpoint.BaseUri &&
                current.EndpointRevision == endpoint.EndpointRevision)
            {
                // Renew the lease without interrupting a healthy reporting generation.
                _endpointSnapshot = endpoint;
                if (_transport is IEndpointLeaseTransport lease) lease.RenewEndpoint(endpoint);
                _transcripts.RenewEndpoint(endpoint);
                ScheduleEndpointExpiry(endpoint);
                return;
            }
            InvalidateDiscoveryEndpoint();
        }
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, budget.Token);
        var acquired = false;
        try
        {
            await _delivery.WaitAsync(linked.Token);
            acquired = true;
            await _transcripts.DrainEndpointAsync(linked.Token);
            await DrainPresenceEndpointAsync(linked.Token);
            lock (_sync)
            {
                if (_stopping || _reloading || _revision != revision || !ReferenceEquals(run, _discoveryRun) ||
                    !endpoint.IsValid(_clock)) return;
                var next = _transportFactory(_configuration, endpoint);
                _transport.Dispose();
                _transport = next;
                _endpointSnapshot = endpoint;
                _generation = ClientIdentity.AllocateGeneration(_configPath);
                _sequence = 0;
                _interrupt.Dispose();
                _interrupt = new();
                ConfigureTranscripts(_configuration, revision, _generation, false);
                _snapshotPending = true;
                ScheduleEndpointExpiry(endpoint);
                Wake();
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !run.IsCancellationRequested)
        {
            _log.Write("discovery-handover-drain-pending");
        }
        finally { if (acquired) _delivery.Release(); }
    }

    private async Task DrainPresenceEndpointAsync(CancellationToken token)
    {
        if (_inflightSend is not { } previous) return;
        try { await previous.WaitAsync(token); }
        catch (Exception ex) when (previous.IsCompleted && ex is OperationCanceledException or HttpRequestException or IOException) { }
        _inflightSend = null;
    }

    private async Task<ClientIpcResponse> ReloadDiscoveryConfigurationAsync(RemoteConfiguration configuration,
        string revision, CancellationToken token)
    {
        Task? oldWorker;
        lock (_sync)
        {
            if (_stopping || _reloading || _worker is null) return Response(false);
            if (configuration.MachineId != _configuration.MachineId)
                return Response(false, "Machine identity changes require a client restart.");
            if (revision == _revision && _effectiveRevision == revision)
            {
                ConfigureTranscripts(configuration, revision, _generation, _startedAcknowledged);
                return Response(true);
            }
            _reloading = true;
            StopDiscovery();
            oldWorker = _discoveryWorker;
            InvalidateDiscoveryEndpoint();
        }
        var acquired = false;
        try
        {
            if (oldWorker is not null) await oldWorker.WaitAsync(token);
            await _delivery.WaitAsync(token);
            acquired = true;
            await _transcripts.DrainEndpointAsync(token);
            await DrainPresenceEndpointAsync(token);
            lock (_sync)
            {
                if (_stopping) return Response(false);
                _configuration = configuration;
                _revision = revision;
                _discoveryStatus = "not-resolved";
                _discoveryError = null;
                _discoveryRun?.Dispose();
                _discoveryRun = null;
                _discoveryWorker = null;
                _interrupt.Dispose();
                _interrupt = new();
                _transport.Dispose();
                _transport = new UnavailablePresenceTransport();
                _reloading = false;
                if (configuration.IsDiscovery) StartDiscovery();
                else
                {
                    _endpointSnapshot = EffectiveEndpointSnapshot.Direct(configuration);
                    _transport = _transportFactory(configuration, _endpointSnapshot);
                    _generation = ClientIdentity.AllocateGeneration(_configPath);
                    _sequence = 0;
                    ConfigureTranscripts(configuration, revision, _generation, false);
                    Wake();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            lock (_sync) return Response(false, "Connection reload was canceled; reporting remains suspended. Retry the saved configuration.");
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex) || ex is OverflowException or
            AgentSignaler.Contracts.DiscoveryException or System.Security.Cryptography.CryptographicException)
        {
            lock (_sync)
            {
                InvalidateDiscoveryEndpoint();
                _terminalReason = "Client stopped because the saved connection could not be applied. Repair protected credentials or local generation state in Configurator and restart Client.";
                _log.Write("discovery-reload-failed");
                _ = StopAsync();
                return Response(false, _terminalReason);
            }
        }
        finally
        {
            lock (_sync) _reloading = false;
            if (acquired) _delivery.Release();
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(DashboardConnection.TestTimeout);
        try
        {
            while (!budget.IsCancellationRequested)
            {
                lock (_sync)
                {
                    if (_stopping) return Response(false);
                    if (_effectiveRevision == revision) return Response(true);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), budget.Token);
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { }
        lock (_sync) return Response(false, "Settings saved; verified discovery and Dashboard acknowledgement are pending.");
    }

    private sealed class UnavailablePresenceTransport : IPresenceTransport
    {
        public Task<bool> SendAsync(AgentSignaler.Contracts.PresenceReport report, CancellationToken cancellationToken)
            => Task.FromResult(false);
        public void Dispose() { }
    }
}