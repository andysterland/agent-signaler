using System.Buffers;
using System.Diagnostics;
using System.Security.Authentication;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>Client-owned, volatile detail delivery. No status lock, file reads or persistent state.</summary>
public sealed class TranscriptDeliveryCoordinator : IAsyncDisposable
{
    public const int MaximumEventBytes = 3 * 1024 * 1024;
    public const int MaximumEvents = 256;
    public const int MaximumSourceBytes = 1024 * 1024;
    public const int MaximumSourceEvents = 64;
    public const int MaximumStreams = 16;
    public const int MaximumProjectedTextCharacters = 64 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly object _sync = new();
    private readonly ITranscriptTransport _transport;
    private readonly Func<SourceDescriptor, long, bool> _eligible;
    private readonly TimeProvider _clock;
    private readonly Func<double> _jitter;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _run = new();
    private readonly LinkedList<Entry> _queue = new();
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);
    private readonly Task _worker;
    private RemoteConfiguration? _configuration;
    private long _generation;
    private long _revision;
    private bool _started;
    private bool _suspended = true;
    private bool _capturePaused;
    private bool _stopping;
    private long _stopTimestamp;
    private string _status = "not-started";
    private TranscriptCapabilitiesResponse? _capabilities;
    private DateTimeOffset _lease;
    private DateTimeOffset _refresh;
    private DateTimeOffset _negotiateStarted;
    private int _negotiateAttempts;
    private int _bytes;
    private ITimer? _leaseTimer;
    private Entry? _inflight;
    private Task<TranscriptTransportResult>? _activeRequest;
    private Entry? _activeRequestEntry;
    private int _transportDisposed;
    private bool _disposeRequested;
    private bool _disposeReady;
    private bool _resourcesDisposed;
    private bool _requestCleanupPending;
    private CloseWork? _close;

    public TranscriptDeliveryCoordinator(ITranscriptTransport transport,
        Func<SourceDescriptor, long, bool> isCurrentEligible, TimeProvider? timeProvider = null,
        Func<double>? jitter = null)
    {
        _transport = transport;
        _eligible = isCurrentEligible;
        _clock = timeProvider ?? TimeProvider.System;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _worker = Task.Run(DeliverAsync);
    }

    public event Action<SourceDescriptor?, string>? Invalidated;
    public long Revision { get { lock (_sync) return _revision; } }
    public string Status { get { lock (_sync) return _status; } }
    public bool IsReady { get { lock (_sync) return ReadyCore(); } }
    public TranscriptFieldCapabilities? Capabilities
    {
        get { lock (_sync) return ReadyCore() ? _capabilities?.Capabilities : null; }
    }
    public int PendingEvents
    {
        get { lock (_sync) return _queue.Count + (_inflight is { Node.List: null } ? 1 : 0); }
    }
    public int AccountedBytes { get { lock (_sync) return _bytes; } }

    public void Configure(RemoteConfiguration configuration, long generation, long revision)
    {
        configuration.Validate();
        if (generation <= 0 || revision < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        lock (_sync)
        {
            if (_stopping) return;
            if (_configuration == configuration && _generation == generation && _revision == revision) return;
            ResetCore("configuration-changed", close: true);
            _configuration = configuration;
            _generation = generation;
            _revision = revision;
            _started = false;
            _suspended = configuration.Version != 5 || !configuration.DetailedReportingEnabled ||
                !TranscriptTransport.IsCanonicalHttps(configuration.BaseUri);
            _status = configuration.Version != 5 ? "legacy-status-only" :
                !configuration.DetailedReportingEnabled ? "sharing-disabled" :
                !TranscriptTransport.IsCanonicalHttps(configuration.BaseUri) ? "https-required" : "waiting-for-presence";
        }
        Invalidated?.Invoke(null, "configuration-changed");
        Wake();
    }

    public void StartedAcknowledged()
    {
        lock (_sync)
        {
            if (_stopping || _configuration is null || _suspended || _started) return;
            _started = true;
            _negotiateStarted = _clock.GetUtcNow();
            _negotiateAttempts = 0;
            _refresh = DateTimeOffset.MinValue;
        }
        Wake();
    }

    public bool TryAccept(TranscriptEvent projected, long revision)
    {
        if (projected.Source is not { IsValid: true } || projected.Provenance is null ||
            !_eligible(projected.Source, revision)) return false;
        lock (_sync)
        {
            if (!ReadyCore() || revision != _revision || !Supported(projected.Payload)) return false;
            var now = _clock.GetUtcNow();
            var value = projected with
            {
                ProtocolVersion = TranscriptProtocol.Version, EventId = Guid.NewGuid(),
                MachineId = _configuration!.MachineId, Generation = _generation,
                ReceiverEpoch = _capabilities!.ReceiverEpoch, StreamId = Guid.NewGuid(),
                Sequence = 1, Provenance = projected.Provenance with { AcceptedAtUtc = now }
            };
            if (!BoundProjectedMessage(ref value) || !TranscriptProtocol.Validate(value)) return false;
            var key = SourceIdentity.SessionKey(projected.Source, "");
            if (!_streams.TryGetValue(key, out var stream))
            {
                if (_streams.Count == MaximumStreams) { _status = "stream-capacity"; return false; }
                stream = new StreamState(projected.Source, _clock.GetUtcNow());
                _streams.Add(key, stream);
                stream.NextSequence = 2;
            }
            if (stream.NextSequence >= long.MaxValue - 1) return false;
            value = value with { StreamId = stream.Id, Sequence = stream.NextSequence };
            if (!TranscriptProtocol.Validate(value) || !TrySerializeEvent(value, out var serialized)) return false;
            if (stream.StartReason is { } startReason)
            {
                stream.Gap = NewGap(projected.SessionId, 1, 1, startReason, unknown: true);
                stream.StartReason = null;
            }
            var charge = TranscriptProtocol.AccountedEventBytes(serialized.Length);
            while (_queue.Count + (_inflight is { Node.List: null } ? 1 : 0) >= MaximumEvents ||
                   _bytes + charge > MaximumEventBytes ||
                   stream.Count >= MaximumSourceEvents || stream.Bytes + charge > MaximumSourceBytes)
            {
                var sourceExceeded = stream.Count >= MaximumSourceEvents || stream.Bytes + charge > MaximumSourceBytes;
                var oldest = _queue.First;
                while (oldest is not null && (ReferenceEquals(oldest.Value, _inflight) ||
                       sourceExceeded && !ReferenceEquals(oldest.Value.Stream, stream))) oldest = oldest.Next;
                if (oldest is null) return false;
                DropCore(oldest.Value, TranscriptGapReason.QueueOverflow);
            }
            stream.NextSequence++;
            var entry = new Entry(stream, value.EventId, value.Sequence, value.SessionId, now,
                serialized.ToArray(), charge);
            entry.Node = _queue.AddLast(entry);
            _bytes += charge;
            stream.Bytes += charge;
            stream.Count++;
        }
        Wake();
        return true;
    }

    public void Suspend(string category)
    {
        lock (_sync)
        {
            if (_stopping) return;
            ResetCore(category, close: true);
            _suspended = true;
        }
        Invalidated?.Invoke(null, category);
        Wake();
    }

    private void SuspendCurrent(string category, long revision, CancellationToken token)
    {
        lock (_sync)
        {
            if (!Current(revision, token)) return;
            ResetCore(category, close: true);
            _suspended = true;
        }
        Invalidated?.Invoke(null, category);
        Wake();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_resourcesDisposed) return;
            if (!_stopping)
            {
                ResetCore("stopped", close: true);
                _stopping = true;
                _stopTimestamp = Stopwatch.GetTimestamp();
                _suspended = true;
            }
        }
        Invalidated?.Invoke(null, "stopped");
        Wake();
        var remaining = TimeSpan.FromSeconds(3.5) - Stopwatch.GetElapsedTime(_stopTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            DisposeTransport();
            _lifetime.Cancel();
            return;
        }
        try { await _worker.WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { _lifetime.Cancel(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { _lifetime.Cancel(); }
        finally
        {
            DisposeTransport();
            lock (_sync)
                if (_activeRequest is not null) _status = "shutdown-cleanup-pending";
        }
    }

    private bool ReadyCore() => !_stopping && !_suspended && !_capturePaused && !_requestCleanupPending && _started &&
        _capabilities is { Enabled: true, Ready: true } && _clock.GetUtcNow() < _lease;

    private bool Supported(TranscriptPayload payload) => (_capabilities!.Capabilities, payload) switch
    {
        ({ UserMessages: true }, TranscriptMessage { Role: TranscriptRole.User }) => true,
        ({ MainAssistantCompleteMessages: true }, TranscriptMessage { Role: TranscriptRole.Assistant }) => true,
        ({ ToolNames: true }, TranscriptToolActivity { InvocationId: null }) => true,
        ({ ToolNames: true, ToolCorrelation: true }, TranscriptToolActivity) => true,
        ({ SessionStarted: true }, TranscriptLifecycle { Signal: TranscriptLifecycleSignal.SessionStarted }) => true,
        ({ SessionEnded: true }, TranscriptLifecycle { Signal: TranscriptLifecycleSignal.SessionEnded }) => true,
        ({ StopObserved: true }, TranscriptLifecycle { Signal: TranscriptLifecycleSignal.StopObserved }) => true,
        (_, TranscriptGap) => true,
        _ => false
    };

    private void ResetCore(string category, bool close)
    {
        _leaseTimer?.Dispose();
        _leaseTimer = null;
        _run.Cancel();
        _run.Dispose();
        _run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (close && _configuration is not null && _capabilities is not null)
        {
            var stream = _streams.Values.FirstOrDefault(s => s.Opened);
            if (stream is not null)
                _close = new(_configuration.BaseUri, new(TranscriptProtocol.Version, _configuration.MachineId,
                    _generation, _capabilities.ReceiverEpoch, stream.Id, true));
        }
        _queue.Clear();
        _streams.Clear();
        _bytes = _inflight?.Charge ?? 0;
        if (_inflight is { } owned)
        {
            owned.Stream.Bytes = owned.Charge;
            owned.Stream.Count = 1;
        }
        _capabilities = null;
        _capturePaused = false;
        _lease = DateTimeOffset.MinValue;
        _status = category;
    }

    private async Task DeliverAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                CloseWork? close;
                bool stop;
                Task<TranscriptTransportResult>? barrier;
                lock (_sync) { barrier = _activeRequest; stop = _stopping; }
                if (barrier is not null)
                {
                    if (stop)
                    {
                        await Task.WhenAny(barrier, Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(false);
                        lock (_sync)
                        {
                            if (_activeRequest is not null)
                            {
                                _status = "shutdown-cleanup-pending";
                                return;
                            }
                        }
                        continue;
                    }
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(250), _lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                lock (_sync) { close = _close; _close = null; stop = _stopping; }
                if (close is not null)
                    await SendAsync(close.Endpoint, TranscriptOperation.Close, Serialize(close.Request),
                        _lifetime.Token).ConfigureAwait(false);
                if (stop) return;
                if (!await PumpAsync().ConfigureAwait(false))
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(250), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async Task<bool> PumpAsync()
    {
        RemoteConfiguration config;
        long revision;
        CancellationToken token;
        bool negotiate;
        bool expired = false;
        lock (_sync)
        {
            var now = _clock.GetUtcNow();
            var node = _queue.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!ReferenceEquals(node.Value, _inflight) && now >= node.Value.Expires)
                    DropCore(node.Value, TranscriptGapReason.Expired);
                node = next;
            }
            if (_configuration is null || !_started || _suspended || _stopping) return false;
            config = _configuration;
            revision = _revision;
            token = _run.Token;
            if (_capabilities is not null && now >= _lease)
            {
                ResetCore("capability-expired", close: false);
                token = _run.Token;
                expired = true;
            }
            negotiate = now >= _refresh;
        }
        if (expired) Invalidated?.Invoke(null, "capability-expired");
        try
        {
            if (negotiate) return await NegotiateAsync(config, revision, token).ConfigureAwait(false);
            StreamState? stream;
            Entry? entry;
            GapState? gap;
            lock (_sync)
            {
                if (!ReadyCore() || token.IsCancellationRequested) return false;
                entry = _queue.First?.Value;
                stream = entry?.Stream ?? _streams.Values.FirstOrDefault(s => s.Gap is not null);
                if (stream is null) return false;
                gap = stream.Gap;
                if (entry is not null && gap is not null && entry.Sequence < gap.From) gap = null;
                if (_clock.GetUtcNow() < (gap?.NextAttempt ?? entry?.NextAttempt ?? stream.NextAttempt)) return false;
            }
            if (!_eligible(stream.Source, revision))
            {
                SuspendCurrent("source-ineligible", revision, token);
                return false;
            }
            if (!stream.Opened) return await OpenAsync(config, stream, revision, token).ConfigureAwait(false);
            return gap is not null
                ? await DeliverGapAsync(config, stream, gap, revision, token).ConfigureAwait(false)
                : entry is not null && await DeliverEntryAsync(config, entry, revision, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return false;
        }
        catch (JsonException) { SuspendCurrent("invalid-protocol", revision, token); return false; }
        catch (InvalidDataException) { SuspendCurrent("invalid-protocol", revision, token); return false; }
    }

    private async Task<bool> NegotiateAsync(RemoteConfiguration config, long revision, CancellationToken token)
    {
        var sources = config.Integrations.Count == 0 ? [SourceDescriptor.LegacyCli] :
            config.Integrations.Select(target => new SourceDescriptor(target.Kind, target.ScopeId,
                target.Kind == "visual-studio" ? "shared" : target.HostVersion));
        if (!sources.Any(source => _eligible(source, revision)))
        {
            SuspendCurrent("source-ineligible", revision, token);
            return false;
        }
        lock (_sync)
        {
            if (!Current(revision, token)) return false;
            if (_negotiateAttempts >= 8 || _clock.GetUtcNow() >= _negotiateStarted + Lifetime)
            {
                _suspended = true;
                _status = "capability-unavailable";
                return false;
            }
            _negotiateAttempts++;
        }
        var result = await SendAsync(config.BaseUri, TranscriptOperation.Capabilities,
            Serialize(new TranscriptCapabilitiesRequest(TranscriptProtocol.Version, config.MachineId)), token).ConfigureAwait(false);
        if (token.IsCancellationRequested) return false;
        if (result.StatusCode == 200 && result.Failure is null)
        {
            var response = Deserialize<TranscriptCapabilitiesResponse>(result.Body);
            if (response.ProtocolVersion != TranscriptProtocol.Version || response.ReceiverEpoch == Guid.Empty ||
                response.OpenRevision < 0 || response.Limits != TranscriptProtocol.Limits || response.Capabilities is null)
            { SuspendCurrent("incompatible-receiver", revision, token); return false; }
            if (!response.Enabled || !response.Ready) { SuspendCurrent("receiver-disabled", revision, token); return false; }
            bool reset;
            bool lostCapability;
            lock (_sync)
            {
                if (!Current(revision, token)) return false;
                lostCapability = _capabilities is { } old &&
                    !Covers(response.Capabilities, old.Capabilities);
                reset = _capabilities is { } prior && prior.ReceiverEpoch != response.ReceiverEpoch;
                _capabilities = response;
                _lease = _clock.GetUtcNow().AddSeconds(60);
                _leaseTimer?.Dispose();
                var lease = _lease;
                _leaseTimer = _clock.CreateTimer(_ => ExpireLease(revision, lease), null,
                    TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                _refresh = _clock.GetUtcNow().AddSeconds(45);
                _negotiateAttempts = 0;
                _negotiateStarted = _clock.GetUtcNow();
                _status = "ready";
                _capturePaused = false;
                if (reset) RebaseCore(response.ReceiverEpoch);
            }
            if (lostCapability) { SuspendCurrent("incompatible-receiver", revision, token); return false; }
            if (reset) Invalidated?.Invoke(null, "receiver-reset");
            return true;
        }
        if (HandleFatal(result, revision, token)) return false;
        lock (_sync)
        {
            if (Current(revision, token))
                _refresh = _clock.GetUtcNow() + Delay(_negotiateAttempts, result.RetryAfter);
        }
        return false;
    }

    private void ExpireLease(long revision, DateTimeOffset lease)
    {
        lock (_sync)
        {
            if (_stopping || _suspended || _revision != revision || _lease != lease) return;
            ResetCore("capability-expired", close: false);
        }
        Invalidated?.Invoke(null, "capability-expired");
        Wake();
    }

    private static bool Covers(TranscriptFieldCapabilities current, TranscriptFieldCapabilities previous) =>
        (!previous.UserMessages || current.UserMessages) &&
        (!previous.MainAssistantCompleteMessages || current.MainAssistantCompleteMessages) &&
        (!previous.ToolNames || current.ToolNames) && (!previous.ToolCorrelation || current.ToolCorrelation) &&
        (!previous.SessionStarted || current.SessionStarted) &&
        (!previous.SessionEnded || current.SessionEnded) && (!previous.StopObserved || current.StopObserved);

    private async Task<bool> OpenAsync(RemoteConfiguration config, StreamState stream, long revision,
        CancellationToken token)
    {
        TranscriptOpenRequest? request = null;
        lock (_sync)
        {
            if (!Current(revision, token) || !ReadyCore()) return false;
            if (_clock.GetUtcNow() < stream.NextAttempt) return false;
            if (stream.Attempts >= 8 || _clock.GetUtcNow() >= stream.Created + Lifetime)
                RetireCore(stream);
            else
            {
                stream.ExpectedRevision ??= _capabilities!.OpenRevision;
                request = new(TranscriptProtocol.Version, config.MachineId, _generation,
                    _capabilities!.ReceiverEpoch, stream.Id, stream.Source, stream.ExpectedRevision.Value, stream.OpenId);
                stream.Attempts++;
            }
        }
        if (request is null) { Invalidated?.Invoke(stream.Source, "delivery-exhausted"); return false; }
        if (!_eligible(stream.Source, revision)) { SuspendCurrent("source-ineligible", revision, token); return false; }
        var result = await SendAsync(config.BaseUri, TranscriptOperation.Open, Serialize(request), token,
            stream.Created + Lifetime).ConfigureAwait(false);
        if (token.IsCancellationRequested) return false;
        if (result.StatusCode == 200 && result.Failure is null)
        {
            var response = Deserialize<TranscriptOpenResponse>(result.Body);
            if (response.ReceiverEpoch != request.ReceiverEpoch || response.StreamId != stream.Id ||
                response.OpenRevision < request.ExpectedOpenRevision + 1)
            { SuspendCurrent("invalid-protocol", revision, token); return false; }
            lock (_sync)
            {
                if (!Current(revision, token)) return false;
                stream.Opened = true;
                _capabilities = _capabilities! with { OpenRevision = response.OpenRevision };
            }
            return true;
        }
        if (await HandleConflictAsync(result, stream, revision, token).ConfigureAwait(false) ||
            HandleFatal(result, revision, token)) return false;
        lock (_sync)
            if (Current(revision, token)) stream.NextAttempt = _clock.GetUtcNow() + Delay(stream.Attempts, result.RetryAfter);
        return false;
    }

    private async Task<bool> DeliverEntryAsync(RemoteConfiguration config, Entry entry, long revision,
        CancellationToken token)
    {
        if (!_eligible(entry.Stream.Source, revision)) { SuspendCurrent("source-ineligible", revision, token); return false; }
        lock (_sync)
        {
            if (!Current(revision, token) || !ReadyCore() || entry.Node?.List is null) return false;
            if (entry.Attempts >= 8 || _clock.GetUtcNow() >= entry.Expires)
            { DropCore(entry, TranscriptGapReason.Expired); return true; }
            _inflight = entry;
            entry.Attempts++;
            _status = "delivering";
        }
        TranscriptTransportResult result;
        try
        {
            result = await SendAsync(config.BaseUri, TranscriptOperation.Event, entry.Bytes, token,
                entry.Expires).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_inflight, entry) && !ReferenceEquals(_activeRequestEntry, entry))
                {
                    _inflight = null;
                    if (entry.Node?.List is null)
                    {
                        _bytes -= entry.Charge;
                        entry.Stream.Bytes -= entry.Charge;
                        entry.Stream.Count--;
                    }
                }
            }
        }
        if (token.IsCancellationRequested) return false;
        if (ValidAck(result, entry.Stream, entry.Sequence))
        {
            lock (_sync)
                if (Current(revision, token)) { RemoveCore(entry); _status = "ready"; }
            return true;
        }
        if (await HandleConflictAsync(result, entry.Stream, revision, token).ConfigureAwait(false) ||
            HandleFatal(result, revision, token)) return false;
        lock (_sync)
        {
            if (!Current(revision, token)) return false;
            var delay = Delay(entry.Attempts, result.RetryAfter);
            if (entry.Attempts >= 8 || _clock.GetUtcNow() + delay >= entry.Expires)
                DropCore(entry, TranscriptGapReason.Expired);
            else
            {
                entry.NextAttempt = _clock.GetUtcNow() + delay;
                _status = ReferenceEquals(_activeRequestEntry, entry) ? "request-cleanup-pending" : "retrying";
            }
        }
        return false;
    }

    private async Task<bool> DeliverGapAsync(RemoteConfiguration config, StreamState stream, GapState gap,
        long revision, CancellationToken token)
    {
        byte[]? bytes = null;
        lock (_sync)
        {
            if (!Current(revision, token) || !ReadyCore()) return false;
            if (gap.Attempts >= 8 || _clock.GetUtcNow() >= gap.Expires)
                RetireCore(stream);
            else
            {
                gap.Attempts++;
                bytes = Serialize(GapEvent(stream, gap));
            }
        }
        if (bytes is null) { Invalidated?.Invoke(stream.Source, "delivery-exhausted"); return false; }
        if (!_eligible(stream.Source, revision)) { SuspendCurrent("source-ineligible", revision, token); return false; }
        var result = await SendAsync(config.BaseUri, TranscriptOperation.Event, bytes, token,
            gap.Expires).ConfigureAwait(false);
        if (token.IsCancellationRequested) return false;
        if (ValidAck(result, stream, gap.Through))
        {
            lock (_sync)
            {
                if (Current(revision, token) && ReferenceEquals(stream.Gap, gap)) stream.Gap = null;
            }
            return true;
        }
        if (await HandleConflictAsync(result, stream, revision, token).ConfigureAwait(false) ||
            HandleFatal(result, revision, token)) return false;
        lock (_sync)
        {
            if (Current(revision, token))
            {
                gap.NextAttempt = _clock.GetUtcNow() + Delay(gap.Attempts, result.RetryAfter);
                if (stream.Gap is { } replacement && !ReferenceEquals(replacement, gap))
                {
                    replacement.Attempts = Math.Max(replacement.Attempts, gap.Attempts);
                    if (replacement.NextAttempt < gap.NextAttempt) replacement.NextAttempt = gap.NextAttempt;
                }
                _status = _activeRequest is null ? "retrying" : "request-cleanup-pending";
            }
        }
        return false;
    }

    private bool ValidAck(TranscriptTransportResult result, StreamState stream, long sequence)
    {
        if (result.StatusCode != 202 || result.Failure is not null) return false;
        var ack = Deserialize<TranscriptAcknowledgement>(result.Body);
        lock (_sync)
        {
            if (ack.ReceiverEpoch == _capabilities?.ReceiverEpoch && ack.StreamId == stream.Id &&
                ack.AcknowledgedSequence == sequence && ack.Disposition is TranscriptDisposition.Accepted or TranscriptDisposition.Duplicate)
                return true;
        }
        throw new InvalidDataException("Invalid transcript acknowledgement.");
    }

    private Task<bool> HandleConflictAsync(TranscriptTransportResult result, StreamState stream,
        long revision, CancellationToken token)
    {
        if (result.StatusCode != 409 || result.Failure is not null) return Task.FromResult(false);
        var error = Deserialize<TranscriptError>(result.Body);
        string? invalidation = null;
        lock (_sync)
        {
            if (!Current(revision, token)) return Task.FromResult(true);
            switch (error.Category)
            {
                case TranscriptRejection.EpochReset:
                    _refresh = DateTimeOffset.MinValue;
                    _capturePaused = true;
                    _status = "receiver-reset";
                    invalidation = "receiver-reset";
                    break;
                case TranscriptRejection.OpenRevisionConflict:
                    stream.ExpectedRevision = null;
                    stream.OpenId = Guid.NewGuid();
                    _refresh = DateTimeOffset.MinValue;
                    break;
                case TranscriptRejection.UnknownManagedMachine:
                    _started = false;
                    _status = "waiting-for-presence";
                    break;
                case TranscriptRejection.RetiredStream:
                case TranscriptRejection.SequenceConflict:
                    RetireCore(stream, error.Category == TranscriptRejection.SequenceConflict
                        ? TranscriptGapReason.Disconnected : TranscriptGapReason.CaptureStarted);
                    invalidation = error.Category == TranscriptRejection.RetiredStream ? "stream-retired" : "sequence-conflict";
                    _status = invalidation;
                    _refresh = DateTimeOffset.MinValue;
                    break;
                default:
                    throw new InvalidDataException("Invalid transcript conflict.");
            }
        }
        if (invalidation is not null)
            Invalidated?.Invoke(invalidation == "receiver-reset" ? null : stream.Source, invalidation);
        return Task.FromResult(true);
    }

    private bool HandleFatal(TranscriptTransportResult result, long revision, CancellationToken token)
    {
        if (result.Failure is not null && result.Failure != "network-unavailable")
        { SuspendCurrent(result.Failure, revision, token); return true; }
        if (result.Body.Length != 0 && result.StatusCode == 503)
        {
            var error = Deserialize<TranscriptError>(result.Body);
            if (error.Category is TranscriptRejection.Disabled or TranscriptRejection.Unavailable)
            { SuspendCurrent("receiver-disabled", revision, token); return true; }
        }
        if (!result.Retryable) { SuspendCurrent("incompatible-receiver", revision, token); return true; }
        return false;
    }

    private void RebaseCore(Guid epoch)
    {
        foreach (var stream in _streams.Values)
        {
            stream.Id = Guid.NewGuid();
            stream.OpenId = Guid.NewGuid();
            stream.Opened = false;
            stream.ExpectedRevision = null;
            stream.NextSequence = 2;
            stream.NextAttempt = default;
            stream.Created = _clock.GetUtcNow();
            stream.Attempts = 0;
            stream.Gap = null;
            stream.StartReason = TranscriptGapReason.ReceiverReset;
            foreach (var entry in _queue.Where(e => ReferenceEquals(e.Stream, stream)))
            {
                var value = Deserialize<TranscriptEvent>(entry.Bytes) with
                { ReceiverEpoch = epoch, StreamId = stream.Id, Sequence = stream.NextSequence++ };
                if (!TrySerializeEvent(value, out var serialized))
                    throw new InvalidDataException("Invalid retained transcript.");
                var bytes = serialized.ToArray();
                var charge = TranscriptProtocol.AccountedEventBytes(bytes.Length);
                _bytes += charge - entry.Charge;
                stream.Bytes += charge - entry.Charge;
                entry.Bytes = bytes;
                entry.Charge = charge;
                entry.Sequence = value.Sequence;
            }
            var first = _queue.FirstOrDefault(e => ReferenceEquals(e.Stream, stream));
            if (first is not null)
            {
                stream.Gap = NewGap(first.SessionId, 1, 1, TranscriptGapReason.ReceiverReset, unknown: true);
                stream.StartReason = null;
            }
        }
    }

    private GapState NewGap(string session, long from, long through,
        TranscriptGapReason reason, bool unknown = false, DateTimeOffset? accepted = null) =>
        new(session, from, through, reason, unknown, accepted ?? _clock.GetUtcNow());

    private TranscriptEvent GapEvent(StreamState stream, GapState gap) => new()
    {
        EventId = gap.Id, MachineId = _configuration!.MachineId, Generation = _generation,
        ReceiverEpoch = _capabilities!.ReceiverEpoch, StreamId = stream.Id, Sequence = gap.Through,
        Source = stream.Source, SessionId = gap.SessionId,
        Provenance = new("discontinuity", "transcript-v1", gap.Accepted, gap.Accepted,
            TranscriptOrdering.Arrival, CaptureOrigin.Hook, "delivery-v1"),
        Payload = new TranscriptGap(gap.Unknown ? null : gap.From, gap.Unknown ? null : gap.Through, gap.Reason)
    };

    private void DropCore(Entry entry, TranscriptGapReason reason)
    {
        var stream = entry.Stream;
        var old = stream.Gap;
        var gap = NewGap(entry.SessionId, Math.Min(old?.From ?? entry.Sequence, entry.Sequence),
            Math.Max(old?.Through ?? 0, entry.Sequence), reason, accepted: old?.Accepted);
        if (old is not null)
        {
            gap.Attempts = old.Attempts;
            gap.NextAttempt = old.NextAttempt;
        }
        stream.Gap = gap;
        RemoveCore(entry);
        _status = reason == TranscriptGapReason.Expired ? "events-expired" : "queue-overflow";
    }

    private void RemoveCore(Entry entry)
    {
        if (entry.Node?.List is null) return;
        _queue.Remove(entry.Node);
        if (ReferenceEquals(_activeRequestEntry, entry)) return;
        _bytes -= entry.Charge;
        entry.Stream.Bytes -= entry.Charge;
        entry.Stream.Count--;
    }

    private void RetireCore(StreamState stream, TranscriptGapReason reason = TranscriptGapReason.Expired)
    {
        foreach (var entry in _queue.Where(e => ReferenceEquals(e.Stream, stream)).ToArray()) RemoveCore(entry);
        stream.Id = Guid.NewGuid();
        stream.OpenId = Guid.NewGuid();
        stream.Opened = false;
        stream.ExpectedRevision = null;
        stream.NextSequence = 2;
        stream.Created = _clock.GetUtcNow();
        stream.Attempts = 0;
        stream.NextAttempt = default;
        stream.Gap = null;
        stream.StartReason = reason;
    }

    private bool Current(long revision, CancellationToken token) =>
        !_stopping && !_suspended && _revision == revision && !token.IsCancellationRequested;

    private TimeSpan Delay(int attempts, TimeSpan? retryAfter)
    {
        var seconds = attempts switch { <= 1 => 1, 2 => 2, 3 => 4, 4 => 8, _ => 15 };
        var delay = TimeSpan.FromSeconds(seconds * (0.8 + Math.Clamp(_jitter(), 0, 1) * 0.4));
        if (retryAfter is { } requested && requested > delay) delay = requested;
        return delay > Lifetime ? Lifetime : delay;
    }

    private async Task<TranscriptTransportResult> SendAsync(Uri endpoint, TranscriptOperation operation,
        ReadOnlyMemory<byte> bytes, CancellationToken token, DateTimeOffset? expires = null)
    {
        token.ThrowIfCancellationRequested();
        var budget = TimeSpan.FromSeconds(1.5);
        if (expires is { } expiry && expiry - _clock.GetUtcNow() < budget) budget = expiry - _clock.GetUtcNow();
        if (budget <= TimeSpan.Zero) return new(408, default);
        using var timeout = new CancellationTokenSource(budget, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        Task<TranscriptTransportResult> request;
        Entry? owner;
        lock (_sync)
        {
            if (_activeRequest is not null) throw new InvalidOperationException("Transcript request still owns transport.");
            if (_transportDisposed != 0) throw new OperationCanceledException(token);
            owner = operation == TranscriptOperation.Event ? _inflight : null;
        }
        try { request = _transport.SendAsync(endpoint, operation, bytes, linked.Token); }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError ||
                                                    exception.InnerException is AuthenticationException)
        { return new(0, default, "tls-validation-failed"); }
        catch (HttpRequestException) { return new(0, default, "network-unavailable"); }
        catch (IOException) { return new(0, default, "network-unavailable"); }
        lock (_sync) { _activeRequest = request; _activeRequestEntry = owner; }
        _ = request.ContinueWith(completed => RequestUnwound(completed, owner), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await request.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            bool pending;
            lock (_sync)
            {
                pending = ReferenceEquals(_activeRequest, request) &&
                    !token.IsCancellationRequested && !_stopping && !_suspended;
                if (pending) { _requestCleanupPending = true; _status = "request-cleanup-pending"; }
            }
            if (pending) Invalidated?.Invoke(null, "request-cleanup-pending");
            return new(408, default);
        }
        catch (HttpRequestException exception) when (exception.HttpRequestError == HttpRequestError.SecureConnectionError ||
                                                    exception.InnerException is AuthenticationException)
        { return new(0, default, "tls-validation-failed"); }
        catch (HttpRequestException) { return new(0, default, "network-unavailable"); }
        catch (IOException) { return new(0, default, "network-unavailable"); }
    }

    private static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json);
    private static T Deserialize<T>(ReadOnlyMemory<byte> bytes) =>
        JsonSerializer.Deserialize<T>(bytes.Span, TranscriptProtocol.Json) ??
        throw new InvalidDataException("Missing transcript response.");

    private static bool BoundProjectedMessage(ref TranscriptEvent value)
    {
        if (value.Payload is not TranscriptMessage { Text: { Length: > TranscriptProtocol.MaxEventBytes } text } message)
            return true;
        if (text.Length > MaximumProjectedTextCharacters || !Enum.IsDefined(message.TruncationReason) ||
            message.Truncated != (message.TruncationReason != TranscriptTruncationReason.None)) return false;
        var remaining = text.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
        }
        var length = TranscriptProtocol.MaxEventBytes;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        value = value with
        {
            Payload = message with
            {
                Text = text[..length], Truncated = true,
                TruncationReason = TranscriptTruncationReason.SerializedLimit
            }
        };
        return true;
    }

    private static bool TrySerializeEvent(TranscriptEvent value, out ReadOnlyMemory<byte> bytes)
    {
        if (value.Payload is not TranscriptMessage message)
        {
            bytes = Serialize(value);
            return bytes.Length <= TranscriptProtocol.MaxEventBytes;
        }
        // Serialize only content-free metadata with the general serializer. Encode text directly
        // into owned bounded scratch, rather than renting a worst-case escaping buffer containing it.
        var metadata = Serialize(value with { Payload = message with { Text = "" } });
        var textLength = EncodedPrefix(message.Text, TranscriptProtocol.MaxEventBytes - metadata.Length, out var encodedLength);
        if (textLength != message.Text.Length)
        {
            metadata = Serialize(value with { Payload = message with { Text = "", Truncated = true,
                TruncationReason = TranscriptTruncationReason.SerializedLimit } });
            textLength = EncodedPrefix(message.Text, TranscriptProtocol.MaxEventBytes - metadata.Length, out encodedLength);
        }
        if (textLength == 0) { bytes = default; return false; }
        var marker = metadata.AsSpan().IndexOf("\"text\":\"\""u8);
        if (marker < 0) throw new InvalidDataException("Invalid transcript message schema.");
        var insertion = marker + "\"text\":\""u8.Length;
        var scratch = new byte[TranscriptProtocol.MaxEventBytes];
        metadata.AsSpan(0, insertion).CopyTo(scratch);
        var written = EncodeText(message.Text.AsSpan(0, textLength), scratch.AsSpan(insertion));
        metadata.AsSpan(insertion).CopyTo(scratch.AsSpan(insertion + written));
        bytes = scratch.AsMemory(0, metadata.Length + encodedLength);
        return true;
    }

    private static int EncodedPrefix(string text, int budget, out int bytes)
    {
        Span<char> encoded = stackalloc char[12];
        bytes = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            var length = char.IsHighSurrogate(text[offset]) ? 2 : 1;
            if (JavaScriptEncoder.Default.Encode(text.AsSpan(offset, length), encoded, out var consumed,
                    out var written, isFinalBlock: true) != OperationStatus.Done || consumed != length)
                throw new InvalidDataException("Invalid transcript text.");
            var count = Encoding.UTF8.GetByteCount(encoded[..written]);
            if (count > budget - bytes) break;
            bytes += count;
            offset += length;
        }
        return offset;
    }

    private static int EncodeText(ReadOnlySpan<char> text, Span<byte> destination)
    {
        Span<char> encoded = stackalloc char[12];
        var output = 0;
        var offset = 0;
        while (offset < text.Length)
        {
            var length = char.IsHighSurrogate(text[offset]) ? 2 : 1;
            if (JavaScriptEncoder.Default.Encode(text.Slice(offset, length), encoded, out var consumed,
                    out var written, isFinalBlock: true) != OperationStatus.Done || consumed != length)
                throw new InvalidDataException("Invalid transcript text.");
            output += Encoding.UTF8.GetBytes(encoded[..written], destination[output..]);
            offset += length;
        }
        return output;
    }

    private void Wake()
    {
        lock (_sync) if (_resourcesDisposed) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
        }
        await StopAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        DisposeTransport();
        lock (_sync) _disposeReady = true;
        _ = _worker.ContinueWith(_ => DisposeResourcesWhenUnwound(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        DisposeResourcesWhenUnwound();
    }

    private void RequestUnwound(Task<TranscriptTransportResult> request, Entry? owner)
    {
        _ = request.Exception;
        lock (_sync)
        {
            if (!ReferenceEquals(_activeRequest, request)) return;
            _activeRequest = null;
            _activeRequestEntry = null;
            _requestCleanupPending = false;
            if (owner is not null && ReferenceEquals(_inflight, owner))
            {
                _inflight = null;
                if (owner.Node?.List is null)
                {
                    _bytes -= owner.Charge;
                    owner.Stream.Bytes -= owner.Charge;
                    owner.Stream.Count--;
                }
            }
            if (_stopping && _status == "shutdown-cleanup-pending") _status = "stopped";
            else if (!_stopping && _status == "request-cleanup-pending") _status = "retrying";
        }
        Wake();
        DisposeResourcesWhenUnwound();
    }

    private void DisposeTransport()
    {
        if (Interlocked.Exchange(ref _transportDisposed, 1) == 0) _transport.Dispose();
    }

    private void DisposeResourcesWhenUnwound()
    {
        lock (_sync)
        {
            if (!_disposeReady || _resourcesDisposed || !_worker.IsCompleted || _activeRequest is not null) return;
            _resourcesDisposed = true;
            _run.Dispose();
            _lifetime.Dispose();
            _wake.Dispose();
        }
    }

    private sealed class StreamState(SourceDescriptor source, DateTimeOffset created)
    {
        public SourceDescriptor Source { get; } = source;
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid OpenId { get; set; } = Guid.NewGuid();
        public DateTimeOffset Created { get; set; } = created;
        public bool Opened { get; set; }
        public long? ExpectedRevision { get; set; }
        public long NextSequence { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public int Count { get; set; }
        public int Bytes { get; set; }
        public GapState? Gap { get; set; }
        public TranscriptGapReason? StartReason { get; set; } = TranscriptGapReason.CaptureStarted;
    }

    private sealed class Entry(StreamState stream, Guid id, long sequence, string session,
        DateTimeOffset accepted, byte[] bytes, int charge)
    {
        public StreamState Stream { get; } = stream;
        public Guid Id { get; } = id;
        public string SessionId { get; } = session;
        public long Sequence { get; set; } = sequence;
        public DateTimeOffset Expires { get; } = accepted + Lifetime;
        public byte[] Bytes { get; set; } = bytes;
        public int Charge { get; set; } = charge;
        public int Attempts { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
        public LinkedListNode<Entry>? Node { get; set; }
    }

    private sealed class GapState(string session, long from, long through, TranscriptGapReason reason,
        bool unknown, DateTimeOffset accepted)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string SessionId { get; } = session;
        public long From { get; } = from;
        public long Through { get; } = through;
        public TranscriptGapReason Reason { get; } = reason;
        public bool Unknown { get; } = unknown;
        public DateTimeOffset Accepted { get; } = accepted;
        public DateTimeOffset Expires { get; } = accepted + Lifetime;
        public int Attempts { get; set; }
        public DateTimeOffset NextAttempt { get; set; }
    }

    private sealed record CloseWork(Uri Endpoint, TranscriptCloseRequest Request);
}
