using System.Collections.Immutable;
using AgentSignaler.Contracts;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal sealed record CopilotRow(Guid MachineId, string Key, SourceDescriptor Source, string SessionId,
    RuntimeSession? Session, ImmutableArray<TranscriptSessionInfo> Streams, DateTimeOffset Now)
{
    public bool RetainedOnly => Session is null || Session.IsEnded;
    public string DisplayName => Session?.DisplayName ?? SessionId;
    public string Heading => $"{SessionPresentation.SourceLabel(Source.Kind)} · scope {Source.ScopeId} · session {DisplayName}";
    public string Identity => $"Session ID: {SessionId}";
    public string Status => Session is null ? "Retained history only — not connected; host lifecycle unknown" :
        $"{Session.LifecycleLabel} · {(Session.State == AgentState.Waiting ? "Waiting for input" : Session.State)}" +
        (Session.Snapshot.ResultState is { } result ? $" · last result {result}" : "");
    public string LastEvent => SessionPresentation.EventLabel(Session?.LatestEvent);
    public string EventTime => Session?.LatestEventAtUtc is { } timestamp
        ? $"{SessionPresentation.RelativeTime(timestamp, Now)} · {timestamp:O}" : "Event time unavailable";
    public string TranscriptStatus => Streams.IsEmpty ? "No retained transcript for this Copilot." :
        $"{Streams.Length} retained stream(s)" + (Streams.All(stream => stream.Closed) ? " · ended/closed" : "");
}

internal sealed record CopilotsState(ImmutableArray<CopilotRow> Rows, string? SelectedKey, string Message, bool Loading)
{
    public static CopilotsState Empty { get; } = new([], null, "", false);
}

/// <summary>Joins status with bounded, content-free transcript metadata; never reads message bodies.</summary>
internal sealed class CopilotsController : IDisposable
{
    private readonly object _sync = new();
    private readonly ITranscriptReader? _reader;
    private readonly TimeProvider _clock;
    private MachineView? _machine;
    private ImmutableArray<TranscriptSessionInfo> _metadata = [];
    private CopilotsState _state = CopilotsState.Empty;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _work;
    private bool _visible, _disposed, _pending;
    private long _revision;
    private string _message = "";

    public CopilotsController(ITranscriptReader? reader, TimeProvider? clock = null)
    {
        _reader = reader;
        _clock = clock ?? TimeProvider.System;
        if (_reader is not null) _reader.Invalidated += OnInvalidated;
    }

    public event Action? Changed;
    public CopilotsState State { get { lock (_sync) return _state; } }

    public void SetMachine(MachineView? machine)
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_machine?.MachineId != machine?.MachineId)
            {
                CancelLocked();
                _metadata = [];
                _state = CopilotsState.Empty;
            }
            _machine = machine;
            RebuildLocked();
        }
        Changed?.Invoke();
        _ = RefreshAsync();
    }

    public Task ShowAsync()
    {
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            _visible = true;
            RebuildLocked();
        }
        Changed?.Invoke();
        return RefreshAsync();
    }

    public void Hide()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _visible = false;
            CancelLocked();
            _metadata = [];
            RebuildLocked();
        }
        Changed?.Invoke();
    }

    public void Select(string? key)
    {
        lock (_sync)
            if (!_disposed && (key is null || _state.Rows.Any(row => row.Key == key)))
                _state = _state with { SelectedKey = key };
    }

    public Task RefreshAsync()
    {
        TaskCompletionSource work;
        lock (_sync)
        {
            if (_disposed || !_visible || _machine is null) return Task.CompletedTask;
            _pending = true;
            if (_work is not null) return _work.Task;
            _work = work = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                Guid machine;
                long revision;
                CancellationTokenSource cancellation;
                lock (_sync)
                {
                    if (_disposed || !_visible || !_pending || _machine is null) break;
                    _pending = false;
                    machine = _machine.MachineId;
                    revision = _revision;
                    _cancellation = cancellation = new();
                    _state = _state with { Loading = true };
                }
                Changed?.Invoke();
                try
                {
                    var page = _reader is null ? null :
                        await TranscriptMetadata.ReadAsync(_reader, machine, cancellation.Token).ConfigureAwait(false);
                    cancellation.Token.ThrowIfCancellationRequested();
                    lock (_sync)
                    {
                        if (!CurrentLocked(machine, revision)) continue;
                        _metadata = page?.Sessions ?? [];
                        _message = page is null ? "Transcript receiver unavailable. Status reporting is independent." :
                            TranscriptViewController.Availability(page.Availability, page.ResetReason, false);
                        RebuildLocked();
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or TranscriptRejectedException)
                {
                    lock (_sync)
                    {
                        if (!CurrentLocked(machine, revision)) continue;
                        _message = "Retained transcript metadata unavailable; showing last-known metadata. Status reporting is independent.";
                        RebuildLocked();
                    }
                }
                finally
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
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
                if (ReferenceEquals(_work, work)) _work = null;
                restart = _pending && !_disposed && _visible;
            }
            work.TrySetResult();
            if (restart) _ = RefreshAsync();
        }
    }

    private bool CurrentLocked(Guid machine, long revision) =>
        !_disposed && _visible && _machine?.MachineId == machine && _revision == revision;

    private void RebuildLocked()
    {
        if (_machine is null)
        {
            _state = CopilotsState.Empty with { Message = "This machine was removed." };
            return;
        }
        var now = _clock.GetUtcNow();
        var sessions = SessionPresentation.Project(_machine, now);
        var retained = _metadata.GroupBy(info => SourceIdentity.SessionKey(info.Selection.Source, info.Selection.SessionId))
            .ToDictionary(group => group.Key, group => group.OrderBy(info => info.Selection.StreamId).ToImmutableArray());
        var rows = sessions.Select(session => new CopilotRow(_machine.MachineId, session.SessionKey,
            session.Source, session.SessionId, session, retained.GetValueOrDefault(session.SessionKey, []), now)).ToList();
        var activeKeys = sessions.Select(session => session.SessionKey).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, streams) in retained.Where(pair => !activeKeys.Contains(pair.Key)))
            rows.Add(new(_machine.MachineId, key, streams[0].Selection.Source, streams[0].Selection.SessionId, null, streams, now));
        var ordered = rows.OrderBy(row => row.Source.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.Source.ScopeId, StringComparer.Ordinal).ThenBy(row => row.SessionId, StringComparer.Ordinal).ToImmutableArray();
        _state = _state with
        {
            Rows = ordered,
            SelectedKey = ordered.Any(row => row.Key == _state.SelectedKey) ? _state.SelectedKey : null,
            Message = $"{sessions.Count(session => session.IsConnected)} connected · " +
                $"{sessions.Count(session => session.IsConnected && session.State == AgentState.Waiting)} waiting for input. " +
                "Observed sessions are not host-process liveness checks. Quiet sessions remain until an end hook or Client disconnect. " + _message
        };
    }

    private void OnInvalidated(object? sender, TranscriptInvalidation invalidation)
    {
        bool refresh;
        lock (_sync)
        {
            if (_disposed || _machine is null ||
                invalidation.MachineId is { } id && id != _machine.MachineId ||
                invalidation.Selection is { } selected && selected.MachineId != _machine.MachineId) return;
            CancelLocked();
            refresh = invalidation.Selection is not null;
            if (invalidation.Selection is { } selection)
            {
                // Entry expiry/eviction does not establish that the stream has no retained history.
                if (invalidation.Reason is not (TranscriptResetReason.Expired or TranscriptResetReason.Evicted))
                    _metadata = _metadata.Where(info => !TranscriptViewController.SameSelection(info.Selection, selection)).ToImmutableArray();
                _message = "Retained stream changed; refreshing its metadata.";
            }
            else
            {
                _metadata = [];
                _message = TranscriptViewController.Availability(TranscriptAvailability.NoEvents, invalidation.Reason, false);
                if (invalidation.Reason == TranscriptResetReason.Removed) _machine = null;
            }
            RebuildLocked();
        }
        Changed?.Invoke();
        if (refresh) _ = RefreshAsync();
    }

    private void CancelLocked()
    {
        _revision++;
        _pending = false;
        _cancellation?.Cancel();
        _state = _state with { Loading = false };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CancelLocked();
            _disposed = true;
            _machine = null;
            _metadata = [];
            _state = CopilotsState.Empty;
            if (_reader is not null) _reader.Invalidated -= OnInvalidated;
        }
        Changed = null;
    }
}

internal static class TranscriptMetadata
{
    // The receiver retains at most 32 machine selections and returns at most 16 per page.
    public static async Task<TranscriptSessionsPage> ReadAsync(ITranscriptReader reader, Guid machine, CancellationToken token)
    {
        var first = await reader.ListSessionsAsync(machine, cancellationToken: token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (first.Sessions.Length > 16) throw new InvalidDataException("Metadata page bound exceeded.");
        if (first.Availability is TranscriptAvailability.Disabled or TranscriptAvailability.Unavailable)
            return first with { Sessions = [], ContinuationCursor = null };
        var sessions = first.Sessions.Where(info => info.Selection.MachineId == machine).ToImmutableArray();
        if (first.ContinuationCursor is { } cursor)
        {
            var second = await reader.ListSessionsAsync(machine, cursor, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (second.ReceiverEpoch != first.ReceiverEpoch || second.InvalidationGeneration != first.InvalidationGeneration ||
                second.ResetReason != TranscriptResetReason.None || second.Sessions.Length > 16 || second.ContinuationCursor is not null)
                throw new InvalidDataException("Metadata changed or exceeded its bound.");
            sessions = sessions.AddRange(second.Sessions.Where(info => info.Selection.MachineId == machine));
        }
        return first with { Sessions = sessions, ContinuationCursor = null };
    }
}
