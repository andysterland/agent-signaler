using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service;

public sealed class TranscriptRejectedException(TranscriptRejection category) : Exception("Transcript request rejected.")
{
    public TranscriptRejection Category { get; } = category;
}

/// <summary>Owned immutable UTF-8 storage. All state, receipts, cursors and indexes are volatile.</summary>
public sealed class TranscriptStore : ITranscriptReader, IDisposable
{
    private readonly object _gate = new();
    private readonly Func<Guid, long, CancellationToken, ValueTask<bool>> _managedPresence;
    private readonly TranscriptStoreOptions _options;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Dictionary<Guid, StreamState> _streams = [];
    private readonly Dictionary<Guid, MachineState> _machines = [];
    private readonly LinkedList<Entry> _events = [];
    private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
    private long _bytes, _revision, _changes, _invalidation;
    private long _lastResetGeneration;
    private TranscriptResetReason _lastResetReason;
    private Guid _epoch = Guid.NewGuid();
    private bool _enabled = true, _ready, _disposed;
    private int _readerActive;

    private sealed class MachineState
    {
        public long Generation;
        public TranscriptResetReason Reason;
        public DateTimeOffset RetainUntilUtc;
    }

    private sealed class StreamState(TranscriptOpenRequest open, DateTimeOffset now)
    {
        public TranscriptOpenRequest Open { get; } = open;
        public bool Closed;
        public DateTimeOffset LastReceived = now;
        public long HighWater;
        public Guid LatestEventId;
        public byte[]? Fingerprint;
        public readonly Dictionary<string, SessionState> Sessions = new(StringComparer.Ordinal);
        public TranscriptGap? Gap;
    }

    private sealed class SessionState(TranscriptSelection selection)
    {
        public TranscriptSelection Selection { get; } = selection;
        public long Generation;
        public TranscriptResetReason Reason;
        public DateTimeOffset LastReceived;
        public bool HasGaps = true;
        public bool HasTruncation;
    }

    // These buffers are never exposed, pooled or mutated after admission.
    private sealed record Entry(byte[] Utf8, int Charge, long Sequence, StreamState Stream,
        SessionState Session, DateTimeOffset ReceivedAt, DateTimeOffset ExpiresAt);

    public TranscriptStore(Func<Guid, long, CancellationToken, ValueTask<bool>> managedPresence,
        TranscriptStoreOptions? options = null, TimeProvider? timeProvider = null)
    {
        _managedPresence = managedPresence ?? throw new ArgumentNullException(nameof(managedPresence));
        _options = options ?? new TranscriptStoreOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => SweepExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event EventHandler<TranscriptInvalidation>? Invalidated;
    public Guid ReceiverEpoch { get { lock (_gate) return _epoch; } }
    public long OpenRevision { get { lock (_gate) return _revision; } }
    public bool Enabled { get { lock (_gate) return _enabled; } }
    public bool Ready { get { lock (_gate) return _ready && !_disposed; } }
    public long RetainedAccountedBytes { get { lock (_gate) return _bytes; } }
    public int RetainedEventCount { get { lock (_gate) return _events.Count; } }
    public int RetainedStreamCount { get { lock (_gate) return _streams.Count; } }

    public TranscriptCapabilitiesResponse GetCapabilities()
    {
        lock (_gate)
            return new(TranscriptProtocol.Version, _epoch, _revision, _enabled, _ready && !_disposed,
                TranscriptProtocol.Limits, TranscriptProtocol.ImplementedCapabilities);
    }

    public void SetEnabled(bool enabled)
    {
        TranscriptInvalidation? notification = null;
        lock (_gate)
        {
            if (_disposed || _enabled == enabled) return;
            _enabled = enabled;
            if (!enabled) notification = ResetLocked(TranscriptResetReason.Disabled, newEpoch: false);
        }
        Publish(notification);
    }

    public void SetReadiness(bool ready)
    {
        TranscriptInvalidation? notification = null;
        lock (_gate)
        {
            if (_disposed || _ready == ready) return;
            _ready = ready;
            if (!ready) notification = ResetLocked(TranscriptResetReason.Unavailable, newEpoch: true);
        }
        Publish(notification);
    }

    public void Reset()
    {
        TranscriptInvalidation notification;
        lock (_gate) notification = ResetLocked(TranscriptResetReason.ReceiverReset, newEpoch: true);
        Publish(notification);
    }

    public void ClearMachine(Guid machineId, bool removed = false)
    {
        TranscriptInvalidation notification;
        lock (_gate)
        {
            var reason = removed ? TranscriptResetReason.Removed : TranscriptResetReason.Cleared;
            foreach (var stream in _streams.Values.Where(s => s.Open.MachineId == machineId).ToArray())
                RemoveStreamLocked(stream);
            if (_machines.TryGetValue(machineId, out var machine))
            {
                machine.Generation = ++_invalidation;
                machine.Reason = reason;
                machine.RetainUntilUtc = _time.GetUtcNow().AddMinutes(5);
            }
            _changes++;
            notification = new(_epoch, machineId, null, _invalidation, reason);
        }
        Publish(notification);
    }

    public async ValueTask<TranscriptOpenResponse> OpenAsync(TranscriptOpenRequest request, CancellationToken cancellationToken = default)
    {
        if (!TranscriptProtocol.Validate(request)) Reject(TranscriptRejection.InvalidSchema);
        lock (_gate) { RequireAvailable(); RequireEpoch(request.ReceiverEpoch); }
        if (!await _managedPresence(request.MachineId, request.Generation, cancellationToken).ConfigureAwait(false))
            Reject(TranscriptRejection.UnknownManagedMachine);
        cancellationToken.ThrowIfCancellationRequested();
        SweepExpired();
        lock (_gate)
        {
            RequireAvailable();
            RequireEpoch(request.ReceiverEpoch);
            if (_streams.TryGetValue(request.StreamId, out var exact))
            {
                if (!exact.Closed && exact.Open == request)
                    return new(_epoch, request.StreamId, _revision, true);
                Reject(TranscriptRejection.RetiredStream);
            }
            if (request.ExpectedOpenRevision != _revision) Reject(TranscriptRejection.OpenRevisionConflict);
            if (!_machines.ContainsKey(request.MachineId))
            {
                ReclaimMachinesLocked();
                if (_machines.Count >= _options.Machines) Reject(TranscriptRejection.Capacity);
            }
            var previous = _streams.Values.FirstOrDefault(s => !s.Closed &&
                s.Open.MachineId == request.MachineId && SameSource(s.Open.Source, request.Source));
            if (_streams.Count >= _options.Streams ||
                _streams.Values.Count(s => s.Open.MachineId == request.MachineId) >= _options.MachineStreams)
                Reject(TranscriptRejection.Capacity);
            if (previous is not null) { previous.Closed = true; _revision++; }
            _machines.TryAdd(request.MachineId, new MachineState { Generation = ++_invalidation });
            _streams.Add(request.StreamId, new StreamState(request, _time.GetUtcNow()));
            _revision++;
            _changes++;
            return new(_epoch, request.StreamId, _revision, false);
        }
    }

    public TranscriptCloseResponse Close(TranscriptCloseRequest request)
    {
        if (!TranscriptProtocol.Validate(request)) Reject(TranscriptRejection.InvalidSchema);
        TranscriptInvalidation? notification = null;
        TranscriptCloseResponse result;
        lock (_gate)
        {
            RequireAvailable();
            RequireEpoch(request.ReceiverEpoch);
            var stream = FindStream(request.MachineId, request.Generation, request.StreamId, allowClosed: true);
            var duplicate = stream.Closed;
            if (!stream.Closed) { stream.Closed = true; _revision++; }
            if (request.Purge)
            {
                RemoveStreamEventsLocked(stream);
                foreach (var session in stream.Sessions.Values)
                {
                    session.Generation = ++_invalidation;
                    session.Reason = TranscriptResetReason.Cleared;
                }
                _machines[request.MachineId].Generation = ++_invalidation;
                _machines[request.MachineId].Reason = TranscriptResetReason.Cleared;
                notification = new(_epoch, request.MachineId, null, _invalidation, TranscriptResetReason.Cleared);
            }
            _changes++;
            result = new(_epoch, request.StreamId, _revision, duplicate);
        }
        Publish(notification);
        return result;
    }

    public TranscriptAcknowledgement Accept(TranscriptEvent value)
    {
        if (!TranscriptProtocol.Validate(value)) Reject(TranscriptRejection.InvalidSchema);
        // A fixed-size writer rejects before an escaping-heavy value can allocate an oversized buffer.
        var bytes = SerializeBounded(value);
        return AcceptOwned(value, bytes);
    }

    private TranscriptAcknowledgement AcceptOwned(TranscriptEvent value, byte[] bytes)
    {
        SweepExpired();
        var notifications = new List<TranscriptInvalidation>();
        TranscriptAcknowledgement acknowledgement;
        lock (_gate)
        {
            RequireAvailable();
            RequireEpoch(value.ReceiverEpoch);
            var stream = FindStream(value.MachineId, value.Generation, value.StreamId);
            if (!SameSource(stream.Open.Source, value.Source)) Reject(TranscriptRejection.RetiredStream);
            var fingerprint = SHA256.HashData(bytes);
            if (value.Sequence == stream.HighWater && value.EventId == stream.LatestEventId &&
                stream.Fingerprint is not null && CryptographicOperations.FixedTimeEquals(fingerprint, stream.Fingerprint))
                return new(_epoch, value.StreamId, stream.HighWater, TranscriptDisposition.Duplicate);
            if (value.Payload is TranscriptGap { FromSequence: not null, ThroughSequence: { } receivedThrough } &&
                receivedThrough <= stream.HighWater)
                return new(_epoch, value.StreamId, stream.HighWater, TranscriptDisposition.Duplicate);
            if (value.Sequence <= stream.HighWater) Reject(TranscriptRejection.SequenceConflict);
            if (value.Payload is TranscriptGap { FromSequence: { } from, ThroughSequence: { } through })
            {
                if (from > stream.HighWater + 1 || through != value.Sequence) Reject(TranscriptRejection.SequenceConflict);
            }
            else if (value.Sequence != stream.HighWater + 1) Reject(TranscriptRejection.SequenceConflict);

            if (!stream.Sessions.TryGetValue(value.SessionId, out var session))
            {
                if (_streams.Values.Sum(s => s.Sessions.Count) >= _options.Sessions ||
                    _streams.Values.Where(s => s.Open.MachineId == value.MachineId).Sum(s => s.Sessions.Count) >= _options.MachineSessions)
                    Reject(TranscriptRejection.Capacity);
                session = new(new(value.MachineId, value.Source, value.SessionId, value.StreamId)) { Generation = ++_invalidation };
            }
            if (value.Payload is TranscriptGap { FromSequence: not null } knownGap)
                bytes = SerializeBounded(value with { Payload = knownGap with { FromSequence = stream.HighWater + 1 } });
            var charge = TranscriptProtocol.AccountedEventBytes(bytes.Length);
            if (charge > _options.SessionBytes || charge > _options.MachineBytes || charge > _options.RetainedBytes)
                Reject(TranscriptRejection.Capacity);
            EvictForAdmissionLocked(stream, session, charge, notifications);
            var now = _time.GetUtcNow();
            if (value.Payload is TranscriptGap gap)
            {
                // The acknowledgement-lost prefix stays intact. Retain only the newly missing suffix.
                stream.Gap = gap.FromSequence is null ? gap : gap with { FromSequence = stream.HighWater + 1 };
            }
            stream.Sessions.TryAdd(value.SessionId, session);
            session.LastReceived = stream.LastReceived = now;
            session.HasTruncation |= value.Payload is TranscriptMessage { Truncated: true };
            _events.AddLast(new Entry(bytes, charge, value.Sequence, stream, session, now, now + _options.Retention));
            _bytes += charge;
            stream.HighWater = value.Sequence;
            stream.LatestEventId = value.EventId;
            stream.Fingerprint = fingerprint;
            _changes++;
            acknowledgement = new(_epoch, value.StreamId, stream.HighWater, TranscriptDisposition.Accepted);
        }
        Publish(notifications);
        return acknowledgement;
    }

    private void EvictForAdmissionLocked(StreamState stream, SessionState session, int charge, List<TranscriptInvalidation> notifications)
    {
        while (true)
        {
            var sessionEntries = _events.Where(e => e.Session == session).ToArray();
            var machineEntries = _events.Where(e => e.Stream.Open.MachineId == stream.Open.MachineId).ToArray();
            Entry? victim;
            if (sessionEntries.Length >= _options.SessionEvents || sessionEntries.Sum(e => e.Charge) + charge > _options.SessionBytes)
                victim = sessionEntries.FirstOrDefault();
            else if (machineEntries.Length >= _options.MachineEvents || machineEntries.Sum(e => e.Charge) + charge > _options.MachineBytes)
                victim = machineEntries.FirstOrDefault();
            else if (_events.Count >= _options.Events || _bytes + charge > _options.RetainedBytes)
                victim = _events.First?.Value;
            else break;
            if (victim is null) Reject(TranscriptRejection.Capacity);
            RemoveEntryLocked(victim!, TranscriptResetReason.Evicted, notifications);
        }
    }

    public void SweepExpired()
    {
        List<TranscriptInvalidation> notifications = [];
        lock (_gate)
        {
            if (_disposed) return;
            ExpireLocked(notifications);
        }
        Publish(notifications);
    }

    private void ExpireLocked(List<TranscriptInvalidation> notifications)
    {
        var now = _time.GetUtcNow();
        while (_events.First is { } first && first.Value.ExpiresAt <= now)
            RemoveEntryLocked(first.Value, TranscriptResetReason.Expired, notifications);
        foreach (var stream in _streams.Values.ToArray())
        {
            foreach (var session in stream.Sessions.Values.ToArray())
                if (!_events.Any(e => e.Session == session) && now - session.LastReceived >= _options.Retention)
                    stream.Sessions.Remove(session.Selection.SessionId);
            if (now - stream.LastReceived >= _options.Retention && !_events.Any(e => e.Stream == stream))
                RemoveStreamLocked(stream);
        }
        ReclaimMachinesLocked();
    }

    private void RemoveEntryLocked(Entry entry, TranscriptResetReason reason, List<TranscriptInvalidation> notifications)
    {
        _events.Remove(entry);
        _bytes -= entry.Charge;
        entry.Session.HasGaps = true;
        entry.Session.Reason = reason;
        entry.Stream.Gap = new(null, null, reason == TranscriptResetReason.Expired ? TranscriptGapReason.Expired : TranscriptGapReason.Capacity);
        _changes++;
        // Position-aware cursors remain valid when only earlier, unrelated entries disappear.
        if (!notifications.Any(n => n.Selection == entry.Session.Selection))
            notifications.Add(new(_epoch, entry.Stream.Open.MachineId, entry.Session.Selection, entry.Session.Generation, reason));
    }

    private void RemoveStreamLocked(StreamState stream)
    {
        RemoveStreamEventsLocked(stream);
        _streams.Remove(stream.Open.StreamId);
        _revision++;
    }

    private void RemoveStreamEventsLocked(StreamState stream)
    {
        for (var node = _events.First; node is not null;)
        {
            var next = node.Next;
            if (node.Value.Stream == stream) { _bytes -= node.Value.Charge; _events.Remove(node); }
            node = next;
        }
    }

    private void ReclaimMachinesLocked()
    {
        foreach (var machine in _machines.Keys.ToArray())
            if (!_streams.Values.Any(s => s.Open.MachineId == machine) && _machines[machine].RetainUntilUtc <= _time.GetUtcNow())
                _machines.Remove(machine);
    }

    private TranscriptInvalidation ResetLocked(TranscriptResetReason reason, bool newEpoch)
    {
        _revision += _streams.Count;
        _events.Clear(); _streams.Clear(); _machines.Clear();
        _bytes = 0;
        _changes++;
        _invalidation++;
        _lastResetGeneration = _invalidation;
        _lastResetReason = reason;
        if (newEpoch) { _epoch = Guid.NewGuid(); _revision = 0; }
        return new(_epoch, null, null, _invalidation, reason);
    }

    private StreamState FindStream(Guid machine, long generation, Guid streamId, bool allowClosed = false)
    {
        if (!_streams.TryGetValue(streamId, out var stream) || !allowClosed && stream.Closed ||
            stream.Open.MachineId != machine || stream.Open.Generation != generation)
            Reject(TranscriptRejection.RetiredStream);
        return stream!;
    }

    private void RequireAvailable()
    {
        if (!_enabled) Reject(TranscriptRejection.Disabled);
        if (!_ready || _disposed) Reject(TranscriptRejection.Unavailable);
    }
    private void RequireEpoch(Guid epoch) { if (epoch != _epoch) Reject(TranscriptRejection.EpochReset); }
    private static void Reject(TranscriptRejection category) => throw new TranscriptRejectedException(category);
    private static bool SameSource(SourceDescriptor a, SourceDescriptor b) => a.Kind == b.Kind && a.ScopeId == b.ScopeId;
    private void Publish(TranscriptInvalidation? notification) { if (notification is not null) Invalidated?.Invoke(this, notification); }
    private void Publish(List<TranscriptInvalidation> notifications) { foreach (var item in notifications) Publish(item); }

    internal static byte[] SerializeBounded(TranscriptEvent value)
    {
        var scratch = new byte[TranscriptProtocol.MaxEventBytes];
        using var stream = new MemoryStream(scratch, 0, scratch.Length, writable: true, publiclyVisible: false);
        stream.SetLength(0);
        try { JsonSerializer.Serialize(stream, value, TranscriptProtocol.Json); }
        catch (NotSupportedException) { Reject(TranscriptRejection.Oversized); }
        return scratch.AsSpan(0, (int)stream.Length).ToArray();
    }

    public ValueTask<TranscriptSessionsPage> ListSessionsAsync(Guid machineId, string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SweepExpired();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generation = _machines.TryGetValue(machineId, out var machine) ? machine.Generation : _invalidation;
            var reason = ValidateCursorLocked(cursor, machineId, null, generation, out var position);
            var sessions = _streams.Values.Where(s => s.Open.MachineId == machineId)
                .SelectMany(s => s.Sessions.Values.Select(session => (Stream: s, Session: session)))
                .Where(p => _events.Any(e => e.Session == p.Session))
                .OrderBy(p => p.Session.Generation).ToArray();
            if (reason != TranscriptResetReason.None) position = 0;
            var rows = sessions.Where(p => p.Session.Generation > position).Take(16)
                .Select(p => new TranscriptSessionInfo(p.Session.Selection, p.Session.LastReceived,
                    p.Stream.Closed, RangeLocked(p.Session), p.Session.HasGaps, p.Session.HasTruncation)).ToImmutableArray();
            string? next = null;
            if (rows.Length > 0)
            {
                var last = sessions.First(p => p.Session.Selection == rows[^1].Selection).Session.Generation;
                if (sessions.Any(p => p.Session.Generation > last))
                    next = AddCursorLocked(machineId, null, generation, last);
            }
            return ValueTask.FromResult(new TranscriptSessionsPage(_epoch, _changes, generation, rows, next,
                Availability(rows.Length), reason, TranscriptProtocol.ImplementedCapabilities));
        }
    }

    public ValueTask<TranscriptEventsPage> ReadEventsAsync(TranscriptSelection selection, string? cursor = null, CancellationToken cancellationToken = default)
        => ReadEventsCore(selection, cursor, backwards: false, before: false, cancellationToken);

    public ValueTask<TranscriptEventsPage> ReadLatestEventsAsync(TranscriptSelection selection, CancellationToken cancellationToken = default)
        => ReadEventsCore(selection, null, backwards: true, before: false, cancellationToken);

    public ValueTask<TranscriptEventsPage> ReadEventsBeforeAsync(TranscriptSelection selection, string cursor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return ReadEventsCore(selection, cursor, backwards: true, before: true, cancellationToken);
    }

    private ValueTask<TranscriptEventsPage> ReadEventsCore(TranscriptSelection selection, string? cursor, bool backwards, bool before, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Source is not { IsValid: true } || !TranscriptProtocol.ValidIdentifier(selection.SessionId, 128))
            throw new ArgumentException("Invalid transcript selection.", nameof(selection));
        if (Interlocked.CompareExchange(ref _readerActive, 1, 0) != 0) Reject(TranscriptRejection.Capacity);
        try
        {
            SweepExpired();
            Entry[] entries;
            TranscriptEventsPage header;
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SessionState? session = null;
                if (_streams.TryGetValue(selection.StreamId, out var stream) && stream.Open.MachineId == selection.MachineId &&
                    SameSource(stream.Open.Source, selection.Source))
                    stream.Sessions.TryGetValue(selection.SessionId, out session);
                var generation = session?.Generation ?? (_machines.TryGetValue(selection.MachineId, out var machine) ? machine.Generation : _invalidation);
                var reason = ValidateCursorLocked(cursor, selection.MachineId, selection, generation, out var position);
                var range = session is null ? new(null, null) : RangeLocked(session);
                if (reason == TranscriptResetReason.None && cursor is not null && position != 0 &&
                    !_events.Any(e => e.Session == session && e.Sequence == position))
                    reason = session?.Reason is TranscriptResetReason.Expired ? TranscriptResetReason.Expired : TranscriptResetReason.Evicted;
                if (reason != TranscriptResetReason.None) position = 0;
                var selected = new List<Entry>(32);
                var total = 0;
                if (reason == TranscriptResetReason.None && session is not null && _ready && _enabled)
                    for (var node = backwards ? _events.Last : _events.First; node is not null;)
                    {
                        var entry = node.Value;
                        node = backwards ? node.Previous : node.Next;
                        if (entry.Session != session || (before ? entry.Sequence >= position : entry.Sequence <= position)) continue;
                        if (selected.Count == 32 || total + entry.Utf8.Length > 128 * 1024) break;
                        selected.Add(entry); total += entry.Utf8.Length;
                    }
                if (backwards) selected.Reverse();
                entries = selected.ToArray();
                var next = reason != TranscriptResetReason.None ? null : entries.Length > 0 ?
                    AddCursorLocked(selection.MachineId, selection, generation, entries[^1].Sequence) : cursor;
                var previous = entries.Length > 0 && _events.Any(e => e.Session == session && e.Sequence < entries[0].Sequence) ?
                    AddCursorLocked(selection.MachineId, selection, generation, entries[0].Sequence) : null;
                header = new(_epoch, selection, _changes, generation, range, [], next,
                    Availability(session is null ? 0 : (int)(range.FirstSequence is null ? 0 : 1)), reason,
                    session?.HasGaps ?? true, session?.HasTruncation ?? false, TranscriptProtocol.ImplementedCapabilities,
                    stream?.Gap ?? new TranscriptGap(null, null, TranscriptGapReason.CaptureStarted), previous);
            }
            // Immutable UTF-8 references are snapshotted under the lock; decoding never holds ingestion.
            // One <=128 KiB page plus decoded/escaped/rendered copies fits the 8 MiB reader partition.
            var decoded = ImmutableArray.CreateBuilder<TranscriptReadEvent>(entries.Length);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                decoded.Add(new(JsonSerializer.Deserialize<TranscriptEvent>(entry.Utf8, TranscriptProtocol.Json)!,
                    entry.ReceivedAt, entry.ExpiresAt, entry.Utf8.Length));
            }
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_epoch != header.ReceiverEpoch || !_enabled || !_ready ||
                    entries.Any(e => !_events.Contains(e)))
                    return ValueTask.FromResult(header with { Events = [], ContinuationCursor = null, PreviousCursor = null,
                        ResetReason = header.ResetReason != TranscriptResetReason.None ? header.ResetReason :
                            !_enabled ? TranscriptResetReason.Disabled : _epoch != header.ReceiverEpoch ?
                            TranscriptResetReason.ReceiverReset : TranscriptResetReason.Evicted });
            }
            return ValueTask.FromResult(header with { Events = decoded.MoveToImmutable() });
        }
        finally { Volatile.Write(ref _readerActive, 0); }
    }

    public bool IsCurrent(TranscriptEventsPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (_gate)
        {
            if (_disposed || page.ReceiverEpoch != _epoch) return false;
            var selection = page.Selection;
            SessionState? session = null;
            if (_streams.TryGetValue(selection.StreamId, out var stream) &&
                stream.Open.MachineId == selection.MachineId && SameSource(stream.Open.Source, selection.Source))
                stream.Sessions.TryGetValue(selection.SessionId, out session);
            var generation = session?.Generation ??
                (_machines.TryGetValue(selection.MachineId, out var machine) ? machine.Generation : _invalidation);
            if (generation != page.InvalidationGeneration) return false;
            if (page.Events.IsEmpty)
                return page.Availability == Availability(session is null || !_events.Any(e => e.Session == session) ? 0 : 1);
            if (!_enabled || !_ready || session is null) return false;
            var now = _time.GetUtcNow();
            foreach (var item in page.Events)
                if (item.ExpiresAtUtc <= now || item.Event.MachineId != selection.MachineId ||
                    item.Event.StreamId != selection.StreamId || item.Event.SessionId != selection.SessionId ||
                    !SameSource(item.Event.Source, selection.Source) ||
                    !_events.Any(e => e.Session == session && e.Sequence == item.Event.Sequence && e.ExpiresAt > now))
                    return false;
            return true;
        }
    }

    public bool IsCurrent(TranscriptSelection selection, Guid receiverEpoch, long invalidationGeneration,
        TranscriptRetainedRange displayedRange)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(displayedRange);
        lock (_gate)
        {
            if (_disposed || !_enabled || !_ready || receiverEpoch != _epoch ||
                selection.Source is not { IsValid: true }) return false;
            SessionState? session = null;
            if (_streams.TryGetValue(selection.StreamId, out var stream) &&
                stream.Open.MachineId == selection.MachineId && SameSource(stream.Open.Source, selection.Source))
                stream.Sessions.TryGetValue(selection.SessionId, out session);
            var generation = session?.Generation ??
                (_machines.TryGetValue(selection.MachineId, out var machine) ? machine.Generation : _invalidation);
            if (generation != invalidationGeneration) return false;
            var now = _time.GetUtcNow();
            if (displayedRange.FirstSequence is null || displayedRange.LastSequence is null)
                return displayedRange.FirstSequence is null && displayedRange.LastSequence is null &&
                    !_events.Any(e => e.Session == session && e.ExpiresAt > now);
            var first = displayedRange.FirstSequence.Value;
            var last = displayedRange.LastSequence.Value;
            if (session is null || first <= 0 || last < first) return false;
            var foundFirst = false;
            var foundLast = false;
            foreach (var entry in _events)
            {
                if (entry.Session != session || entry.Sequence < first || entry.Sequence > last) continue;
                if (entry.ExpiresAt <= now) return false;
                foundFirst |= entry.Sequence == first;
                foundLast |= entry.Sequence == last;
            }
            return foundFirst && foundLast;
        }
    }

    private TranscriptRetainedRange RangeLocked(SessionState session)
    {
        long? first = null, last = null;
        foreach (var entry in _events.Where(e => e.Session == session)) { first ??= entry.Sequence; last = entry.Sequence; }
        return new(first, last);
    }

    private TranscriptAvailability Availability(int count) => !_enabled ? TranscriptAvailability.Disabled :
        !_ready || _disposed ? TranscriptAvailability.Unavailable :
        count == 0 ? TranscriptAvailability.NoEvents : TranscriptAvailability.Partial;

    private TranscriptResetReason ValidateCursorLocked(string? value, Guid machine, TranscriptSelection? selection,
        long generation, out long position)
    {
        position = 0;
        if (value is null) return TranscriptResetReason.None;
        Span<byte> decoded = stackalloc byte[120];
        if (value.Length > 512 || !Convert.TryFromBase64String(value, decoded, out var length) || length != 120)
            return TranscriptResetReason.InvalidCursor;
        if (new Guid(decoded[..16]) != _epoch) return TranscriptResetReason.ReceiverReset;
        Span<byte> expectedMac = stackalloc byte[32];
        HMACSHA256.HashData(_cursorKey, decoded[..88], expectedMac);
        if (!CryptographicOperations.FixedTimeEquals(decoded[88..], expectedMac)) return TranscriptResetReason.InvalidCursor;
        if (BinaryPrimitives.ReadInt64LittleEndian(decoded[80..88]) <= _time.GetUtcNow().UtcTicks)
            return TranscriptResetReason.CursorExpired;
        if (new Guid(decoded[16..32]) != machine ||
            !CryptographicOperations.FixedTimeEquals(decoded[32..64], SelectionFingerprint(selection)))
            return TranscriptResetReason.SelectionChanged;
        var cursorGeneration = BinaryPrimitives.ReadInt64LittleEndian(decoded[64..72]);
        if (cursorGeneration != generation)
        {
            if (!_enabled) return TranscriptResetReason.Disabled;
            if (cursorGeneration <= _lastResetGeneration) return _lastResetReason;
            if (_machines.TryGetValue(machine, out var state) && cursorGeneration < state.Generation &&
                state.Reason != TranscriptResetReason.None) return state.Reason;
            return TranscriptResetReason.Expired;
        }
        position = BinaryPrimitives.ReadInt64LittleEndian(decoded[72..80]);
        return TranscriptResetReason.None;
    }

    private static byte[] SelectionFingerprint(TranscriptSelection? selection) => selection is null ? new byte[32] :
        SHA256.HashData(Encoding.UTF8.GetBytes($"{selection.StreamId:N}:{SourceIdentity.SessionKey(selection.Source, selection.SessionId)}"));

    private string AddCursorLocked(Guid machine, TranscriptSelection? selection, long generation, long position)
    {
        // Fixed stateless, in-process cursor integrity; this does not authenticate network senders.
        Span<byte> bytes = stackalloc byte[120];
        _epoch.TryWriteBytes(bytes[..16]);
        machine.TryWriteBytes(bytes[16..32]);
        SelectionFingerprint(selection).CopyTo(bytes[32..64]);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[64..72], generation);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[72..80], position);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[80..88], _time.GetUtcNow().AddMinutes(5).UtcTicks);
        HMACSHA256.HashData(_cursorKey, bytes[..88], bytes[88..]);
        return Convert.ToBase64String(bytes);
    }

    public void Dispose()
    {
        TranscriptInvalidation notification;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _ready = false;
            notification = ResetLocked(TranscriptResetReason.Unavailable, newEpoch: true);
        }
        _timer.Dispose();
        Publish(notification);
    }
}
