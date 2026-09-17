using System.Collections.Immutable;
using AgentSignaler.Contracts;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal sealed record TranscriptEntry(long Sequence, string Header, string Text, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"Transcript entry {Sequence}";
}

internal sealed record TranscriptViewState(
    bool Visible, bool Loading, ImmutableArray<TranscriptSessionInfo> Sessions,
    TranscriptSelection? Selection, ImmutableArray<TranscriptEntry> Entries,
    string Message, bool HasOlder, bool HasNewActivity, bool FollowingLatest,
    Guid ReceiverEpoch, long InvalidationGeneration, TranscriptRetainedRange RetainedRange)
{
    public static TranscriptViewState Empty { get; } = new(false, false, [], null, [],
        "Select Transcript to view currently retained observations.", false, false, true, Guid.Empty, 0, new(null, null));
    public override string ToString() => $"Transcript viewer: {Entries.Length} entries";
}

/// <summary>Owns one bounded volatile viewer; it never performs capture or network operations.</summary>
internal sealed class TranscriptViewController : IDisposable
{
    internal const string ProductionCaptureNotice = "Assistant transcript-file extraction is unavailable in this build: " +
        "no production format profile is verified. Receiver field support does not verify host capture.";
    private readonly object _sync = new();
    private readonly ITranscriptReader _reader;
    private readonly TimeProvider _clock;
    private readonly ITimer _poll;
    private readonly ITimer _expiry;
    private TranscriptViewState _state = TranscriptViewState.Empty;
    private ViewStamp? _viewStamp;
    private string? _previousCursor;
    private string? _readBeforeCursor;
    private sealed record ViewStamp(TranscriptSelection Selection, Guid Epoch, long Generation, TranscriptRetainedRange Range);
    private Guid _machineId;
    private long _revision;
    private bool _offline;
    private bool _disposed;
    private bool _pending;
    private bool _latest = true;
    private bool _selectInitial = true;
    private TranscriptSelection? _sessionScope;
    private long? _beforeSequence;
    private CancellationTokenSource? _readCancellation;
    private TaskCompletionSource? _work;

    public TranscriptViewController(ITranscriptReader reader, TimeProvider? clock = null)
    {
        _reader = reader;
        _clock = clock ?? TimeProvider.System;
        _poll = _clock.CreateTimer(_ => _ = RefreshAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _expiry = _clock.CreateTimer(_ => Expire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _reader.Invalidated += OnInvalidated;
    }

    public event Action? Changed;

    public TranscriptViewState State
    {
        get
        {
            lock (_sync)
            {
                ExpireLocked();
                if (_viewStamp is not null && !IsDisplayedCurrentLocked())
                {
                    CancelSelectionLocked();
                    _viewStamp = null;
                    _state = _state with { Entries = [], HasOlder = false, HasNewActivity = false,
                        Message = "Retained history changed or the receiver restarted; stale text was released." };
                }
                return _state;
            }
        }
    }

    public Task ShowAsync(Guid machineId, bool offline)
        => ShowAsync(machineId, offline, null);

    public Task ShowAsync(TranscriptSelection selection, bool offline)
        => ShowAsync(selection.MachineId, offline, selection);

    private Task ShowAsync(Guid machineId, bool offline, TranscriptSelection? selection)
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            CancelSelectionLocked();
            _machineId = machineId;
            _offline = offline;
            _latest = true;
            _selectInitial = selection is null;
            _sessionScope = selection;
            _beforeSequence = null;
            _state = TranscriptViewState.Empty with { Visible = true, Selection = selection };
            _viewStamp = null;
            _previousCursor = _readBeforeCursor = null;
            _poll.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        return RefreshAsync();
    }

    public void SetOffline(bool offline)
    {
        lock (_sync) _offline = offline;
    }

    public void Hide()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CancelSelectionLocked();
            _pending = false;
            _state = TranscriptViewState.Empty;
            _sessionScope = null;
            _viewStamp = null;
            _previousCursor = _readBeforeCursor = null;
            _poll.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        Changed?.Invoke();
    }

    public Task SelectAsync(TranscriptSelection selection)
    {
        lock (_sync)
        {
            if (_disposed || !_state.Visible || selection.MachineId != _machineId ||
                !_state.Sessions.Any(session => SameSelection(session.Selection, selection)))
                return Task.CompletedTask;
            CancelSelectionLocked();
            _latest = true;
            _beforeSequence = null;
            _state = _state with { Selection = selection, Entries = [], HasOlder = false, HasNewActivity = false, FollowingLatest = true };
            _viewStamp = null;
            _previousCursor = _readBeforeCursor = null;
        }
        return RefreshAsync();
    }

    public void SetFollowingLatest(bool following)
    {
        lock (_sync)
        {
            if (_disposed || !_state.Visible) return;
            _state = _state with { FollowingLatest = following && _latest && !_state.HasNewActivity };
        }
    }

    public Task OlderAsync()
    {
        lock (_sync)
        {
            if (_disposed || !_state.Visible || !_state.HasOlder || _state.Entries.IsEmpty) return Task.CompletedTask;
            _beforeSequence = _state.Entries[0].Sequence;
            _readBeforeCursor = _previousCursor;
            _latest = false;
            CancelSelectionLocked();
            _state = _state with { FollowingLatest = false };
            ScheduleExpiryLocked();
        }
        return RefreshAsync();
    }

    public Task LatestAsync()
    {
        lock (_sync)
        {
            if (_disposed || !_state.Visible) return Task.CompletedTask;
            _latest = true;
            _beforeSequence = null;
            _readBeforeCursor = null;
            CancelSelectionLocked();
            _state = _state with { FollowingLatest = true, HasNewActivity = false };
            ScheduleExpiryLocked();
        }
        return RefreshAsync();
    }

    public Task RefreshAsync()
    {
        TaskCompletionSource work;
        lock (_sync)
        {
            if (_disposed || !_state.Visible) return Task.CompletedTask;
            _pending = true;
            if (_work is not null) return _work.Task;
            work = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _work = work;
        }
        _ = Task.Run(() => RunAsync(work));
        return work.Task;
    }

    private async Task RunAsync(TaskCompletionSource work)
    {
        try
        {
            while (true)
            {
                long revision;
                Guid machine;
                TranscriptSelection? selection;
                bool latest, following;
                long? before;
                string? beforeCursor;
                CancellationTokenSource cancellation;
                lock (_sync)
                {
                    if (_disposed || !_state.Visible || !_pending) break;
                    _pending = false;
                    revision = _revision;
                    machine = _machineId;
                    selection = _state.Selection;
                    latest = _latest;
                    before = _beforeSequence;
                    beforeCursor = _readBeforeCursor;
                    following = _state.FollowingLatest;
                    cancellation = new();
                    _readCancellation = cancellation;
                    _state = _state with { Loading = true };
                }
                Changed?.Invoke();
                try
                {
                    await ReadAsync(machine, selection, revision, latest, following, before, beforeCursor, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or TranscriptRejectedException)
                {
                    lock (_sync)
                    {
                        if (CurrentLocked(machine, revision))
                        {
                            _viewStamp = null;
                            _state = _state with { Entries = [], Loading = false, Message = "Transcript read failed. Retrying while this tab is visible." };
                        }
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_readCancellation, cancellation)) _readCancellation = null;
                        if (CurrentLocked(machine, revision)) _state = _state with { Loading = false };
                    }
                    cancellation.Dispose();
                    Changed?.Invoke();
                }
            }
        }
        finally
        {
            bool restart;
            lock (_sync)
            {
                _work = null;
                restart = !_disposed && _state.Visible && _pending;
            }
            work.TrySetResult();
            if (restart) _ = RefreshAsync();
        }
    }

    private async Task ReadAsync(Guid machine, TranscriptSelection? selection, long revision,
        bool latest, bool following, long? before, string? beforeCursor, CancellationToken token)
    {
        var first = await TranscriptMetadata.ReadAsync(_reader, machine, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!CurrentLocked(machine, revision)) return;
            var sessions = first.Sessions.Where(session => _sessionScope is null ||
                SameSession(session.Selection, _sessionScope)).ToImmutableArray();
            _state = _state with { Sessions = sessions, ReceiverEpoch = first.ReceiverEpoch };
            if (first.Availability is TranscriptAvailability.Disabled or TranscriptAvailability.Unavailable)
            {
                _viewStamp = null;
                _state = _state with { Entries = [], Message = Availability(first.Availability, first.ResetReason, _offline), HasOlder = false };
                return;
            }
            if (selection is not null && !sessions.Any(session => SameSelection(selection, session.Selection)))
            {
                _viewStamp = null;
                _state = _state with { Selection = null, Entries = [], Message = "Selected history is no longer retained. Choose a retained stream of this Copilot, or go Back to Copilots.", HasOlder = false };
                return;
            }
            if (selection is null && _selectInitial)
            {
                selection = sessions.FirstOrDefault()?.Selection;
                if (selection is not null) _selectInitial = false;
            }
            _state = _state with { Selection = selection };
            if (selection is null || first.Availability is TranscriptAvailability.Disabled or TranscriptAvailability.Unavailable)
            {
                _viewStamp = null;
                var message = _sessionScope is not null && first.ResetReason == TranscriptResetReason.None
                    ? (_offline ? "Machine offline; only last-observed retained activity is available. " : "") +
                        (sessions.IsEmpty ? "No retained events for this Copilot. Sharing may be disabled, expired, or unsupported." :
                            "Choose a retained stream for this Copilot. The previous stream is no longer selected.")
                    : Availability(first.Availability, first.ResetReason, _offline);
                _state = _state with { Entries = [], Message = message, HasOlder = false };
                return;
            }
            var info = sessions.First(session => SameSelection(session.Selection, selection));
            if (!following && !_state.Entries.IsEmpty && before == _beforeSequence &&
                (!latest || _state.Entries[0].Sequence >= (info.RetainedRange.FirstSequence ?? 0)))
            {
                _state = _state with { HasNewActivity = info.RetainedRange.LastSequence > _state.Entries[^1].Sequence };
                // A page navigation request still needs a read; passive refresh must preserve the displayed window.
                if (before is null || _state.Entries[0].Sequence < before) return;
            }
        }
        var page = latest
            ? await _reader.ReadLatestEventsAsync(selection, token).ConfigureAwait(false)
            : await _reader.ReadEventsBeforeAsync(selection, beforeCursor ??
                throw new InvalidOperationException("Older page cursor unavailable."), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!SameSelection(selection, page.Selection) || page.ReceiverEpoch != first.ReceiverEpoch)
            throw new InvalidDataException("Transcript selection changed.");
        if (page.Events.Length > 32 || page.Events.Sum(item => (long)item.SerializedBytes) > 128 * 1024 ||
            page.Events.Any(item => item.SerializedBytes is <= 0 or > TranscriptProtocol.MaxEventBytes) ||
            page.Events.Any(item => item.Event.MachineId != machine || item.Event.StreamId != selection.StreamId ||
                item.Event.SessionId != selection.SessionId || !SameSource(item.Event.Source, selection.Source)))
            throw new InvalidDataException("Transcript page bounds or selection failed.");
        lock (_sync)
        {
            if (!CurrentLocked(machine, revision)) return;
            if (!_reader.IsCurrent(page))
            {
                _viewStamp = null;
                _state = _state with { Entries = [], HasOlder = false, HasNewActivity = false,
                    Message = "Retained history changed or expired during the read. Stale text was released." };
                return;
            }
            var now = _clock.GetUtcNow();
            var entries = page.ResetReason == TranscriptResetReason.None
                ? page.Events.Where(item => item.ExpiresAtUtc > now).Select(Present).ToImmutableArray() : [];
            if (_state.Entries.SequenceEqual(entries)) entries = _state.Entries;
            _viewStamp = entries.IsEmpty ? null : new(selection, page.ReceiverEpoch, page.InvalidationGeneration,
                new(entries[0].Sequence, entries[^1].Sequence));
            _previousCursor = page.PreviousCursor;
            _state = _state with
            {
                Selection = selection, Entries = entries, ReceiverEpoch = page.ReceiverEpoch,
                InvalidationGeneration = page.InvalidationGeneration, RetainedRange = page.RetainedRange,
                HasOlder = _previousCursor is not null && !entries.IsEmpty && page.RetainedRange.FirstSequence < entries[0].Sequence,
                HasNewActivity = !entries.IsEmpty && page.RetainedRange.LastSequence > entries[^1].Sequence,
                Message = Availability(entries.IsEmpty && page.Events.Any(item => item.ExpiresAtUtc <= now)
                    ? TranscriptAvailability.Expired : page.Availability, page.ResetReason, _offline) +
                    (page.Gap is { } gap ? " " + GapMessage(gap.Reason) : page.HasGaps ? " Gaps are present." : "") +
                    (page.HasTruncation ? " Some text was truncated." : "")
            };
            ScheduleExpiryLocked();
        }
    }

    private bool CurrentLocked(Guid machine, long revision) =>
        !_disposed && _state.Visible && _machineId == machine && _revision == revision;

    private bool IsDisplayedCurrentLocked() => _viewStamp is { } stamp &&
        _reader.IsCurrent(stamp.Selection, stamp.Epoch, stamp.Generation, stamp.Range);

    private void OnInvalidated(object? sender, TranscriptInvalidation invalidation)
    {
        lock (_sync)
        {
            if (_disposed || !_state.Visible || invalidation.MachineId is { } machine && machine != _machineId ||
                invalidation.Selection is { } selection && _state.Selection is { } current && !SameSelection(selection, current)) return;
            if (invalidation.Reason is TranscriptResetReason.Expired or TranscriptResetReason.Evicted &&
                IsDisplayedCurrentLocked()) return;
            CancelSelectionLocked();
            _viewStamp = null;
            _previousCursor = _readBeforeCursor = null;
            _beforeSequence = null;
            _latest = true;
            _state = _state with
            {
                Entries = [], Sessions = [], HasOlder = false, HasNewActivity = false,
                ReceiverEpoch = invalidation.ReceiverEpoch, InvalidationGeneration = invalidation.InvalidationGeneration,
                Message = Availability(TranscriptAvailability.NoEvents, invalidation.Reason, _offline)
            };
        }
        Changed?.Invoke();
    }

    private void Expire()
    {
        bool changed;
        lock (_sync) changed = ExpireLocked();
        if (changed) Changed?.Invoke();
    }

    private bool ExpireLocked()
    {
        if (_disposed || _state.Entries.IsEmpty || _state.Entries.All(entry => entry.ExpiresAtUtc > _clock.GetUtcNow())) return false;
        CancelSelectionLocked();
        _viewStamp = null;
        _previousCursor = _readBeforeCursor = null;
        _state = _state with { Entries = [], HasOlder = false, HasNewActivity = false, Message = "History expired; only currently retained observations can be read." };
        return true;
    }

    private void ScheduleExpiryLocked()
    {
        var delay = _state.Entries.IsEmpty ? Timeout.InfiniteTimeSpan :
            _state.Entries.Min(entry => entry.ExpiresAtUtc) - _clock.GetUtcNow();
        _expiry.Change(delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelSelectionLocked()
    {
        _revision++;
        _readCancellation?.Cancel();
        _expiry.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal static bool SameSelection(TranscriptSelection left, TranscriptSelection right) =>
        left.StreamId == right.StreamId && SameSession(left, right);

    internal static bool SameSession(TranscriptSelection left, TranscriptSelection right) =>
        left.MachineId == right.MachineId && left.SessionId == right.SessionId && SameSource(left.Source, right.Source);

    private static bool SameSource(SourceDescriptor left, SourceDescriptor right) =>
        left.Kind == right.Kind && left.ScopeId == right.ScopeId;

    internal static TranscriptEntry Present(TranscriptReadEvent value)
    {
        var item = value.Event;
        var provenance = item.Provenance;
        var role = item.Payload switch
        {
            TranscriptMessage message => message.Role == TranscriptRole.User ? "User" : "Assistant",
            TranscriptToolActivity => "Tool activity",
            TranscriptLifecycle => "Lifecycle",
            _ => "Gap"
        };
        var origin = provenance.CaptureOrigin == CaptureOrigin.TranscriptFile ? "Transcript file, read after stop" : "Hook";
        var ordering = provenance.Ordering == TranscriptOrdering.Arrival ? "Local arrival order; host correlation unavailable" : "Host-provided correlation";
        var header = $"{role} · {provenance.ObservedAtUtc:O} · #{item.Sequence}\n" +
            $"{SessionSourcePresentation.Describe(item.Source)} · session {item.SessionId}\n{origin} · {ordering}";
        var text = item.Payload switch
        {
            TranscriptMessage message => message.Text + (message.Truncated ? "\n[Text truncated]" : "") +
                (message.Availability == TranscriptMessageAvailability.Partial ? "\n[Partial message]" : ""),
            TranscriptToolActivity tool => $"{tool.ToolName} · observed {tool.Outcome}" +
                (tool.InvocationId is null ? " · tool correlation unavailable" : $" · invocation {tool.InvocationId}"),
            TranscriptLifecycle lifecycle => lifecycle.Signal switch
            {
                TranscriptLifecycleSignal.SessionStarted => "Session started.",
                TranscriptLifecycleSignal.SessionEnded => "Session ended. Retained content expires normally.",
                _ => "Stop observed. This does not establish success or session end; waiting for an available assistant reply."
            },
            TranscriptGap gap => GapMessage(gap.Reason) + (gap.FromSequence is { } first
                ? $" Missing delivery range #{first}–#{gap.ThroughSequence}." : " Missing range unknown."),
            _ => "Unsupported observation."
        };
        return new(item.Sequence, header, text, value.ExpiresAtUtc);
    }

    internal static string GapMessage(TranscriptGapReason reason) => reason switch
    {
        TranscriptGapReason.FormatUnverified => "Assistant transcript format unverified; hook prompts/activity remain partial.",
        TranscriptGapReason.FormatChanged => "Assistant transcript format changed; extraction unavailable.",
        TranscriptGapReason.FileUnavailable => "Assistant transcript file unavailable; waiting for the next stop.",
        TranscriptGapReason.ReadBudgetExceeded => "Assistant read budget exceeded; replies are missing.",
        TranscriptGapReason.BaselineEstablished => "Capture started here; earlier replies unavailable. Waiting for the next stop.",
        TranscriptGapReason.PartialWrite => "Assistant reply is not yet complete; waiting for the next stop.",
        TranscriptGapReason.ReceiverReset => "Receiver restarted; earlier history is unavailable.",
        TranscriptGapReason.Expired => "History expired.",
        _ => $"Incomplete capture: {reason}. Missing observations are not successful empty replies."
    };

    internal static string Availability(TranscriptAvailability availability, TranscriptResetReason reset, bool offline) =>
        (offline ? "Machine offline; this is last-observed retained activity. " : "") + (reset switch
        {
            TranscriptResetReason.Cleared => "Transcript cleared locally. Future reception is unchanged.",
            TranscriptResetReason.Removed => "Machine removed; transcript released.",
            TranscriptResetReason.Disabled => "Receive detailed conversations is disabled; history was purged.",
            TranscriptResetReason.Unavailable => "HTTPS with a compatible receiver and its owned running Dev Tunnel is required.",
            TranscriptResetReason.ReceiverReset => "Receiver restarted; previous history is unavailable.",
            TranscriptResetReason.Expired => "History expired; only retained observations are available.",
            TranscriptResetReason.Evicted => "Older history was evicted; the retained range has changed.",
            TranscriptResetReason.CursorExpired or TranscriptResetReason.InvalidCursor => "The page cursor expired or is unavailable. Select Latest to read the retained range.",
            _ => availability switch
            {
                TranscriptAvailability.Disabled => "Receive detailed conversations is disabled. Client sharing may also be disabled independently.",
                TranscriptAvailability.Unavailable => "HTTPS with a compatible receiver and its owned running Dev Tunnel is required.",
                TranscriptAvailability.NoEvents => "No events received. Client sharing may be disabled, legacy, or unsupported.",
                TranscriptAvailability.Expired => "History expired.",
                _ => "Partial, best-effort capture; not an archive. " + ProductionCaptureNotice + " Missing replies do not mean completion."
            }
        });

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CancelSelectionLocked();
            _disposed = true;
            _pending = false;
            _state = TranscriptViewState.Empty;
            _viewStamp = null;
            _previousCursor = _readBeforeCursor = null;
            _reader.Invalidated -= OnInvalidated;
            _poll.Dispose();
            _expiry.Dispose();
        }
        Changed?.Invoke();
        Changed = null;
    }
}
