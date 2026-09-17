using System.Security.Cryptography;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>The single owner of local session mutation and ordered, bounded network delivery.</summary>
public sealed partial class ClientCoordinator : IAsyncDisposable
{
    public const int MaximumPendingHooks = 64;
    private static readonly string ReporterVersion = typeof(ClientCoordinator).Assembly.GetName().Version?.ToString() ?? "unknown";
    private readonly string _configPath;
    private readonly FileStream _owner;
    private readonly TimeProvider _clock;
    private readonly Func<RemoteConfiguration, IPresenceTransport> _transportFactory;
    private readonly DiagnosticLog _log;
    private readonly SessionStore _sessions;
    private readonly object _sync = new();
    private readonly Queue<StatusRequest> _hooks = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly SemaphoreSlim _retryWake = new(0, 1);
    private readonly SemaphoreSlim _delivery = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _interrupt = new();
    private RemoteConfiguration _configuration;
    private IPresenceTransport _transport;
    private string _revision;
    private string? _effectiveRevision;
    private int _acknowledgedInterval;
    private long _generation;
    private long _sequence;
    private bool _startedAcknowledged;
    private bool _snapshotPending;
    private bool _heartbeatDue;
    private bool _reconnectPending;
    private bool _stopping;
    private bool _reloading;
    private bool _connected;
    private PresenceReport? _pending;
    private Task<bool>? _inflightSend;
    private ITimer? _timer;
    private Task? _worker;
    private Task<ClientIpcResponse>? _stop;
    private DateTimeOffset _lastDiagnostic;
    private string? _deliveryError;
    private string? _terminalReason;
    private AggregateException? _unexpectedFailure;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ClientCoordinator(string configPath, TimeProvider? timeProvider = null,
        Func<RemoteConfiguration, IPresenceTransport>? transportFactory = null,
        ITranscriptTransport? transcriptTransport = null,
        ITranscriptFileAdapterRegistry? transcriptFileAdapters = null)
    {
        _configPath = ClientIdentity.CanonicalPath(configPath);
        _owner = ClientIdentity.AcquireOwner(_configPath);
        try
        {
            (_configuration, _revision) = LoadRevision(_configPath);
            _clock = timeProvider ?? TimeProvider.System;
            _transportFactory = transportFactory ?? (config => new PresenceTransport(config));
            _transport = _transportFactory(_configuration);
            _acknowledgedInterval = _configuration.HeartbeatIntervalSeconds;
            _log = new DiagnosticLog(RemotePaths.Log(_configPath));
            _sessions = new SessionStore(RemotePaths.State(_configPath), _log);
            InitializeTranscripts(transcriptTransport, transcriptFileAdapters);
        }
        catch { _owner.Dispose(); throw; }
    }

    public Task Completion => _stopped.Task;
    public string? TerminalReason { get { lock (_sync) return _terminalReason; } }

    public void Start()
    {
        lock (_sync)
        {
            if (_worker is not null || _stopping) throw new InvalidOperationException("Client already started or stopped.");
            _generation = ClientIdentity.AllocateGeneration(_configPath);
            ConfigureTranscripts(_configuration, _revision, _generation, false);
            _transcriptMonitor = Task.Run(MonitorTranscriptConfigurationAsync);
            // Hooks lost during deliberate Exit cannot safely be inferred at the next start.
            AtomicFile.Write(RemotePaths.State(_configPath), JsonSerializer.SerializeToUtf8Bytes(
                new StoredRemoteState(2, _clock.GetUtcNow().AddTicks(-1), []), Protocol.Json));
            var interval = TimeSpan.FromSeconds(_configuration.HeartbeatIntervalSeconds);
            _timer = _clock.CreateTimer(_ => QueueSnapshot(interruptRetry: false), null, interval, interval);
            _worker = Task.Run(DeliverAsync);
            _ = _worker.ContinueWith(WorkerFaulted, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Wake();
        }
    }

    public ClientIpcResponse Status()
    {
        lock (_sync) return Response(true);
    }

    private ClientIpcResponse Response(bool accepted, string? error = null) => new(accepted,
        _stopping ? "stopping" : _worker is null ? "unconfigured" : _reloading ? "reloading" :
        _connected ? "connected" : "retrying", _effectiveRevision,
        _effectiveRevision is null ? null : _acknowledgedInterval, error ?? _terminalReason ?? _deliveryError);

    public Task<ClientIpcResponse> HandleAsync(ClientIpcRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Version is not (ClientIpc.Version or ClientIpc.LegacyVersion))
            return Task.FromResult(new ClientIpcResponse(false, "incompatible"));
        if (request.Command.StartsWith("transcript-", StringComparison.Ordinal))
            return Task.FromResult(HandleTranscript(request));
        if (request.Transcript is not null || request.TranscriptSource is not null || request.TranscriptRead is not null)
            return Task.FromResult(new ClientIpcResponse(false, "invalid"));
        if (request.Command is not ("hook" or "probe") && (request.Event is not null || request.Hook is not null) ||
            request.Command != "reload" && request.ExpectedRevision is not null ||
            request.Command != "probe" && request.ProbeId is not null)
            return Task.FromResult(new ClientIpcResponse(false, "invalid"));
        return request.Command switch
        {
            "status" => Task.FromResult(Status()),
            "hook" => Task.FromResult(AcceptHook(request.Event, request.Hook)),
            "probe" => Task.FromResult(AcceptProbe(request)),
            "reload" => ReloadAsync(request.ExpectedRevision, cancellationToken),
            "stop" => StopAsync(),
            _ => Task.FromResult(new ClientIpcResponse(false, "invalid"))
        };
    }

    private ClientIpcResponse AcceptProbe(ClientIpcRequest request)
    {
        lock (_sync)
        {
            if (_stopping || _reloading || _worker is null) return Response(false);
            if (request.ProbeId is not { } probe || probe == Guid.Empty ||
                request.Hook?.Source is null || !HookPayloadAdapters.IsSanitized(request.Event, request.Hook))
                return Response(false, "Invalid sanitized probe.");
            try { return Response(HookVerification.AcceptProbe(_configPath, probe, request.Event!.Value, request.Hook)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException)
            {
                return Response(false, "Probe unavailable or expired.");
            }
        }
    }

    public ClientIpcResponse AcceptHook(AgentEvent? kind, HookData? hook)
    {
        lock (_sync)
        {
            if (_stopping || _reloading || _worker is null || _hooks.Count >= MaximumPendingHooks)
                return Response(false, "Client is not accepting hooks.");
            if (kind is null || hook is null || !HookPayloadAdapters.IsSanitized(kind, hook) ||
                !HookPayloadAdapters.IsAllowed(_configuration, kind.Value, hook.Source))
                return Response(false, "Invalid sanitized hook.");
            if (!HasCurrentVerification(hook.Source))
                return Response(false, "IDE integration changed or is no longer verified. Repeat hook verification.");
            var now = _clock.GetUtcNow();
            var local = _sessions.UpdateReport(kind, hook with { Timestamp = hook.Timestamp > now ? now : hook.Timestamp }, now);
            if (!local.Accepted) return Response(true);
            _hooks.Enqueue(new StatusRequest
            {
                EventId = Guid.NewGuid(), MachineId = _configuration.MachineId, MachineName = _configuration.MachineName,
                ProtocolVersion = _configuration.Version >= 4 ? PresenceProtocol.SourceVersion : Protocol.Version,
                Client = _configuration.Version >= 4 ? hook.Source?.Kind ?? "copilot-cli" : "copilot-cli",
                Source = _configuration.Version >= 4 ? hook.Source ?? SourceDescriptor.LegacyCli : null,
                ClientVersion = _configuration.Version >= 4 ? (hook.Source ?? SourceDescriptor.LegacyCli).Version : _configuration.ClientVersion,
                Event = kind.Value, SessionId = hook.SessionId,
                ReportedAtUtc = local.ReportedAtUtc, ToolFailed = hook.ToolFailed,
                ToolRequiresUserInput = hook.ToolRequiresUserInput
            });
            _snapshotPending = true;
            Wake();
            return Response(true);
        }
    }

    public void RequestSnapshot() => QueueSnapshot(interruptRetry: true);

    private void QueueSnapshot(bool interruptRetry)
    {
        lock (_sync)
        {
            if (_stopping || _worker is null) return;
            _snapshotPending = true;
            _heartbeatDue = true;
            if (interruptRetry)
            {
                _reconnectPending = true;
                if (_retryWake.CurrentCount == 0) _retryWake.Release();
            }
            Wake();
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private PresenceReport NewReport(PresenceKind kind, StatusRequest? hook = null)
    {
        var local = kind is PresenceKind.Started or PresenceKind.Heartbeat
            ? _sessions.UpdateReport(null, null, _clock.GetUtcNow()) : null;
        var verifiedScopes = new Dictionary<string, bool>(StringComparer.Ordinal);
        return new PresenceReport
        {
            ProtocolVersion = _configuration.Version >= 4 ? PresenceProtocol.SourceVersion : PresenceProtocol.Version,
            Client = _configuration.Version >= 4 ? "agent-signaler" : "copilot-cli",
            Kind = kind, EventId = hook?.EventId ?? Guid.NewGuid(), MachineId = _configuration.MachineId,
            MachineName = _configuration.MachineName,
            ClientVersion = _configuration.Version >= 4 ? ReporterVersion : _configuration.ClientVersion,
            Generation = _generation, Sequence = checked(++_sequence),
            ReportedAtUtc = hook?.ReportedAtUtc ?? local?.ReportedAtUtc ?? _clock.GetUtcNow(),
            HeartbeatIntervalSeconds = local is null ? null : _configuration.HeartbeatIntervalSeconds,
            Sessions = local?.Sessions.Where(s => IsSessionAllowed(s.Source, verifiedScopes))
                .Select(s => _configuration.Version >= 4 ? s : s with { Source = null }).ToList(),
            Hook = hook
        };
    }

    private bool IsSessionAllowed(SourceDescriptor? source, Dictionary<string, bool> verifiedScopes)
    {
        var effective = source ?? SourceDescriptor.LegacyCli;
        if (effective.Kind == "visual-studio" && effective.Version != "shared") return false;
        return _configuration.Version < 4 ? effective == SourceDescriptor.LegacyCli :
            _configuration.Integrations.Any(target => HookPayloadAdapters.IsEnabled(target) && target.Kind == effective.Kind &&
                (effective == SourceDescriptor.LegacyCli || target.ScopeId == effective.ScopeId)) &&
            HasCurrentVerification(source, verifiedScopes);
    }

    private bool HasCurrentVerification(SourceDescriptor? source, Dictionary<string, bool>? verifiedScopes = null)
    {
        if (source is null || source.Kind == "copilot-cli") return true;
        var scopeKey = SourceIdentity.SessionKey(source, "");
        if (verifiedScopes is not null && verifiedScopes.TryGetValue(scopeKey, out var cached)) return cached;
        var targets = _configuration.Integrations.Where(target =>
            target.Kind == source.Kind && target.ScopeId == source.ScopeId).ToList();
        if (targets.Count == 0) return false;
        var verified = true;
        try
        {
            // A shared scope cannot identify the invoking IDE; every selected host must
            // remain eligible under its automatic or verified configuration.
            foreach (var target in targets) AutomaticHookConfiguration.RequireInstallable(target, _configPath);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { verified = false; }
        if (verifiedScopes is not null) verifiedScopes[scopeKey] = verified;
        return verified;
    }

    private async Task DeliverAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _wake.WaitAsync(_lifetime.Token);
                var failed = false;
                await _delivery.WaitAsync(_lifetime.Token);
                try
                {
                    PresenceReport? report;
                    CancellationToken interrupt;
                    lock (_sync)
                    {
                        if (_stopping || _reloading) continue;
                        interrupt = _interrupt.Token;
                        _retryWake.Wait(0);
                        if (_reconnectPending && _startedAcknowledged)
                        {
                            // The local snapshot already includes dropped activity. Reconcile once
                            // on reconnect instead of replaying failed/missed reports in a burst.
                            _pending = null;
                            _hooks.Clear();
                            _reconnectPending = false;
                        }
                        if (_pending is null)
                        {
                            if (!_startedAcknowledged) _pending = NewReport(PresenceKind.Started);
                            else if (_heartbeatDue)
                            {
                                _heartbeatDue = false;
                                _snapshotPending = false;
                                _pending = NewReport(PresenceKind.Heartbeat);
                            }
                            else if (_hooks.TryDequeue(out var hook)) _pending = NewReport(PresenceKind.Hook, hook);
                            else if (_snapshotPending)
                            {
                                _snapshotPending = false;
                                _pending = NewReport(PresenceKind.Heartbeat);
                            }
                        }
                        report = _pending;
                    }
                    if (report is null) continue;
                    var delivered = await SendBoundedAsync(report, interrupt);
                    lock (_sync)
                    {
                        if (_stopping || _reloading) continue;
                        _connected = delivered;
                        if (delivered)
                        {
                            _pending = null;
                            if (report.Kind is PresenceKind.Started or PresenceKind.Heartbeat)
                            {
                                _startedAcknowledged = true;
                                TranscriptStartedAcknowledged();
                                _effectiveRevision = _revision;
                                if (_acknowledgedInterval != _configuration.HeartbeatIntervalSeconds)
                                {
                                    _acknowledgedInterval = _configuration.HeartbeatIntervalSeconds;
                                    ChangeTimer(_acknowledgedInterval);
                                }
                            }
                            if (_hooks.Count != 0 || _snapshotPending) Wake();
                        }
                        else
                        {
                            failed = true;
                            if (_clock.GetUtcNow() - _lastDiagnostic >= TimeSpan.FromMinutes(1))
                            {
                                _log.Write("presence-retrying");
                                _lastDiagnostic = _clock.GetUtcNow();
                            }
                        }
                    }
                }
                finally { _delivery.Release(); }
                if (failed)
                {
                    await WaitForRetryAsync();
                    lock (_sync) Wake();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex) || ex is OverflowException)
        {
            _log.Write("presence-runtime-failed");
            lock (_sync)
            {
                _connected = false;
                _terminalReason = "Client stopped after a reporting failure. Check relay.log beside the configuration, repair local state if needed, and restart Client.";
            }
            _ = StopAsync();
        }
    }

    private void WorkerFaulted(Task worker)
    {
        lock (_sync)
        {
            _unexpectedFailure = worker.Exception;
            _connected = false;
            _terminalReason = "Client stopped after an unexpected reporting failure. Check relay.log and restart Client; repair the installation if the failure repeats.";
        }
        _log.Write("presence-runtime-unexpected");
        _ = StopAsync();
    }

    private async Task WaitForRetryAsync()
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var delay = Task.Delay(TimeSpan.FromSeconds(5), _clock, wait.Token);
        var reconnect = _retryWake.WaitAsync(wait.Token);
        try { await await Task.WhenAny(delay, reconnect); }
        finally { await wait.CancelAsync(); }
    }

    private async Task<bool> SendBoundedAsync(PresenceReport report, CancellationToken cancellationToken,
        bool terminalOffline = false)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            if (_inflightSend is { } previous)
            {
                using var grace = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                if (terminalOffline) grace.CancelAfter(TimeSpan.FromMilliseconds(250));
                try { await previous.WaitAsync(grace.Token); }
                catch (OperationCanceledException) when (terminalOffline && !previous.IsCompleted && !budget.IsCancellationRequested)
                {
                    // Only terminal offline may bypass an uncooperative old send. Its newer
                    // sequence seals the run at the receiver, so a late old commit cannot revive it.
                    _log.Write("offline-cancel-grace-expired");
                    _ = previous.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception ex) when (previous.IsCompleted &&
                    ex is InvalidDataException or HttpRequestException or OperationCanceledException or IOException) { }
                _inflightSend = null;
            }
            budget.Token.ThrowIfCancellationRequested();
            // Canceling WaitAsync does not mean the transport has finished unwinding.
            // Keep the actual send as the barrier for normal reports and cooperative shutdown.
            _inflightSend = _transport.SendAsync(report, budget.Token);
            var accepted = await _inflightSend.WaitAsync(budget.Token);
            lock (_sync) _deliveryError = accepted ? null : "Dashboard delivery has not been acknowledged.";
            return accepted;
        }
        catch (InvalidDataException)
        {
            lock (_sync) _deliveryError = $"Dashboard is incompatible. Upgrade Dashboard to presence protocol v{(_configuration.Version >= 4 ? 3 : 2)}.";
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) { return false; }
        finally
        {
            if (_inflightSend?.IsCompleted == true) _inflightSend = null;
        }
    }

    private void ChangeTimer(int seconds)
    {
        var interval = TimeSpan.FromSeconds(seconds);
        _timer?.Change(interval, interval);
    }

    public async Task<ClientIpcResponse> ReloadAsync(string? expectedRevision, CancellationToken cancellationToken = default)
    {
        SuspendTranscripts("settings-reloading");
        RemoteConfiguration config;
        string revision;
        try
        {
            (config, revision) = LoadRevision(_configPath);
            if (revision != expectedRevision) return new(false, "revision-mismatch", Error: "Committed configuration revision changed.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        { return new(false, "invalid", Error: "Committed configuration could not be loaded."); }
        lock (_sync)
        {
            if (_stopping || _reloading || _worker is null) return Response(false);
            if (config.MachineId != _configuration.MachineId)
                return Response(false, "Machine identity changes require a client restart.");
            if (revision == _revision && _effectiveRevision == revision)
            {
                ConfigureTranscripts(config, revision, _generation, _startedAcknowledged);
                return Response(true);
            }
            _reloading = true;
            _interrupt.Cancel();
        }
        var acquired = false;
        try
        {
            await _delivery.WaitAsync(cancellationToken);
            acquired = true;
            bool endpointChanged;
            lock (_sync)
            {
                if (_stopping) return Response(false);
                endpointChanged = config.BaseUri != _configuration.BaseUri ||
                    (config.Version >= 4) != (_configuration.Version >= 4) ||
                    (!_startedAcknowledged && revision != _revision);
            }
            var oldOffline = true;
            if (endpointChanged)
            {
                PresenceReport offline;
                lock (_sync) offline = NewReport(PresenceKind.Offline);
                oldOffline = await SendBoundedAsync(offline, cancellationToken);
                var next = ClientIdentity.AllocateGeneration(_configPath);
                var transport = _transportFactory(config);
                _transport.Dispose();
                _transport = transport;
                lock (_sync)
                {
                    _generation = next;
                    _sequence = 0;
                    _startedAcknowledged = false;
                    _effectiveRevision = null;
                }
            }
            PresenceReport announcement;
            lock (_sync)
            {
                if (_stopping) return Response(false);
                var retry = revision == _revision ? _pending : null;
                _configuration = config;
                _revision = revision;
                ConfigureTranscripts(config, revision, _generation, _startedAcknowledged);
                _hooks.Clear();
                _snapshotPending = retry is not null;
                _pending = null;
                ChangeTimer(Math.Min(_acknowledgedInterval, config.HeartbeatIntervalSeconds));
                announcement = retry ?? NewReport(_startedAcknowledged ? PresenceKind.Heartbeat : PresenceKind.Started);
                _pending = announcement;
            }
            var delivered = await SendBoundedAsync(announcement, cancellationToken);
            lock (_sync)
            {
                _connected = delivered;
                if (delivered)
                {
                    _pending = null;
                    _startedAcknowledged = true;
                    TranscriptStartedAcknowledged();
                    _effectiveRevision = revision;
                    _acknowledgedInterval = config.HeartbeatIntervalSeconds;
                    ChangeTimer(_acknowledgedInterval);
                }
                return Response(delivered, !oldOffline ? "Previous dashboard offline delivery was not confirmed; it will age out." :
                    !delivered ? "Settings saved, but dashboard acknowledgement is pending." : null);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            OperationCanceledException or OverflowException)
        {
            _log.Write("reload-failed");
            lock (_sync) _terminalReason = "Client stopped because settings could not be reloaded. Resolve configuration or generation state and restart Client.";
            _ = StopAsync();
            lock (_sync) return Response(false, "Runtime reload failed. Resolve configuration or generation state and restart Client.");
        }
        finally
        {
            lock (_sync)
            {
                _reloading = false;
                _interrupt.Dispose();
                _interrupt = new();
                if (!_stopping) Wake();
            }
            if (acquired) _delivery.Release();
        }
    }

    public Task<ClientIpcResponse> StopAsync()
    {
        lock (_sync)
        {
            if (_stop is not null) return _stop;
            _stopping = true;
            SuspendTranscripts("stopped");
            _timer?.Dispose();
            _lifetime.Cancel();
            _interrupt.Cancel();
            _hooks.Clear();
            _snapshotPending = false;
            _pending = null;
            _stop = Task.Run(StopCoreAsync);
            _ = CompleteLifetimeAsync(_stop);
            return _stop;
        }
    }

    private async Task CompleteLifetimeAsync(Task<ClientIpcResponse> stop)
    {
        await Task.WhenAny(stop);
        var worker = _worker;
        if (worker is not null) await Task.WhenAny(worker);
        lock (_sync)
        {
            if (worker?.IsFaulted == true)
            {
                _unexpectedFailure ??= worker.Exception;
                _terminalReason ??= "Client stopped after an unexpected reporting failure. Check relay.log and restart Client.";
            }
            if (stop.IsFaulted)
            {
                _unexpectedFailure ??= stop.Exception;
                _terminalReason = "Client stopped after an unexpected shutdown failure. Check relay.log and restart Client; repair the installation if the failure repeats.";
                _log.Write("presence-shutdown-unexpected");
            }
            if (_unexpectedFailure is { } failure) _stopped.TrySetException(failure.InnerExceptions);
            else _stopped.TrySetResult();
        }
    }

    private async Task<ClientIpcResponse> StopCoreAsync()
    {
        var delivered = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var detailsStopped = _transcripts.StopAsync(budget.Token);
        var readerStopped = _transcriptReader.DisposeAsync().AsTask();
        try
        {
            await _delivery.WaitAsync(budget.Token);
            try
            {
                if (_generation != 0)
                {
                    PresenceReport offline;
                    lock (_sync) offline = NewReport(PresenceKind.Offline);
                    delivered = await SendBoundedAsync(offline, budget.Token, terminalOffline: true);
                }
            }
            finally { _delivery.Release(); }
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex) || ex is OverflowException)
        {
            _log.Write("offline-report-failed");
        }
        finally
        {
            lock (_sync)
            {
                if (!delivered)
                    _terminalReason ??= "Offline delivery could not be confirmed; dashboard timeout will apply.";
            }
        }
        if (!delivered) _log.Write("offline-unconfirmed");
        try { await Task.WhenAll(detailsStopped, readerStopped).WaitAsync(budget.Token); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _log.Write("transcript-shutdown-timeout");
        }
        lock (_sync) return Response(true);
    }

    private static (RemoteConfiguration Configuration, string Revision) LoadRevision(string path)
    {
        var bytes = AtomicFile.ReadBounded(path, 262144);
        var config = JsonSerializer.Deserialize<RemoteConfiguration>(bytes, Protocol.Json) ?? throw new InvalidDataException();
        config.Validate();
        if (config.Version is not (3 or 4 or 5))
            throw new InvalidDataException("Apply managed configuration in Configurator before starting Client.");
        return (config, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
            if (_worker is not null) await _worker;
            if (_transcriptMonitor is not null) await _transcriptMonitor;
            _transcripts.Invalidated -= TranscriptInvalidated;
            await _transcripts.DisposeAsync();
        }
        finally
        {
            try
            {
                _timer?.Dispose();
                _transport.Dispose();
            }
            finally
            {
                _lifetime.Dispose();
                _interrupt.Dispose();
                _wake.Dispose();
                _retryWake.Dispose();
                _delivery.Dispose();
                _owner.Dispose();
            }
        }
    }
}
