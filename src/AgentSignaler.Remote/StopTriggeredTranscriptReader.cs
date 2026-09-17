using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

/// <summary>
/// Client-owned, stop-only work. The admission callback must atomically recheck its
/// revision/readiness, transfer text to the bounded delivery queue, and honor cancellation.
/// </summary>
public sealed class StopTriggeredTranscriptReader : IAsyncDisposable
{
    public const int MaximumQueuedSessions = 16;
    public const int MaximumContexts = 32;
    public const int MaximumContextsPerSource = 8;
    public const int MaximumFileBytes = 64 * 1024 * 1024;
    public const int MaximumReadBytes = 2 * 1024 * 1024;
    public const int MaximumRecordBytes = 64 * 1024;
    public const int MaximumRecords = 128;
    public const int MaximumReplies = 16;
    public const int MaximumOutputBytes = 256 * 1024;
    public const int ScratchBytes = 448 * 1024;
    private static readonly TimeSpan PassDuration = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan QueueLifetime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ContextLifetime = TimeSpan.FromMinutes(30);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object _gate = new();
    private readonly ITranscriptFileAdapterRegistry _registry;
    private readonly Func<LocalTranscriptReference, bool> _isEligibleAndReady;
    private readonly Func<TranscriptReaderOutput, CancellationToken, ValueTask<bool>> _admitOutput;
    private readonly TranscriptScratchBudget _scratch;
    private readonly TimeProvider _time;
    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Context> _contexts = new(StringComparer.Ordinal);
    private CancellationTokenSource _generation = new();
    private Task? _worker;
    private bool _disposed;

    public StopTriggeredTranscriptReader(
        ITranscriptFileAdapterRegistry registry,
        Func<LocalTranscriptReference, bool> isEligibleAndReady,
        Func<TranscriptReaderOutput, CancellationToken, ValueTask<bool>> admitOutput,
        TranscriptScratchBudget? scratch = null,
        TimeProvider? timeProvider = null)
    {
        _registry = registry;
        _isEligibleAndReady = isEligibleAndReady;
        _admitOutput = admitOutput;
        _scratch = scratch ?? new TranscriptScratchBudget();
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <returns>Null on volatile admission, otherwise an application-defined gap/unavailable category.</returns>
    public string? TrySchedule(LocalTranscriptReference reference)
    {
        if (!reference.IsValid) return TranscriptReaderCategories.InvalidReference;
        if (!_isEligibleAndReady(reference)) return TranscriptReaderCategories.Ineligible;
        var adapter = _registry.Find(reference.Source);
        if (adapter is null) return TranscriptReaderCategories.FormatUnverified;
        if (!LocalTranscriptReference.ValidIdentity(adapter.ProfileId, 64) ||
            !TranscriptFileBoundary.IsExpectedPath(reference, adapter))
            return TranscriptReaderCategories.PathRejected;
        var key = SourceIdentity.SessionKey(reference.Source, reference.SessionId);
        lock (_gate)
        {
            if (_disposed) return TranscriptReaderCategories.Ineligible;
            var request = new Pending(reference, adapter, _time.GetTimestamp(), _generation.Token);
            if (_pending.ContainsKey(key))
            {
                _pending[key] = request;
                return null;
            }
            if (_pending.Count >= MaximumQueuedSessions) return TranscriptReaderCategories.Capacity;
            _pending.Add(key, request);
            _order.Enqueue(key);
            _worker ??= Task.Run(WorkAsync);
            return null;
        }
    }

    /// <summary>Call before opt-out, endpoint/source changes, clear/reset, or shutdown work.</summary>
    public void Reset()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            if (_disposed) return;
            old = _generation;
            _generation = new();
            _pending.Clear();
            _order.Clear();
            _contexts.Clear();
        }
        old.Cancel();
        old.Dispose();
    }

    public Task WhenIdleAsync()
    {
        lock (_gate) return _worker ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _pending.Clear();
            _order.Clear();
            _contexts.Clear();
            worker = _worker;
        }
        _generation.Cancel();
        if (worker is not null) await worker.ConfigureAwait(false);
        _generation.Dispose();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Pending pending;
            string key;
            lock (_gate)
            {
                if (_order.Count == 0 || _disposed)
                {
                    _worker = null;
                    return;
                }
                key = _order.Dequeue();
                pending = _pending[key];
                _pending.Remove(key);
            }
            try
            {
                if (_time.GetElapsedTime(pending.AcceptedTimestamp) >= QueueLifetime)
                    await OutputAsync(pending, TranscriptReaderCategories.Expired, pending.Token).ConfigureAwait(false);
                else
                    await ProcessAsync(key, pending).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Includes the pass deadline expiring during initial gap admission.
                if (!pending.Token.IsCancellationRequested)
                {
                    try
                    {
                        await OutputAsync(pending, TranscriptReaderCategories.Budget, pending.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pending.Token.IsCancellationRequested) { }
                }
            }
        }
    }

    private async Task ProcessAsync(string key, Pending pending)
    {
        if (!Ready(pending)) return;
        using var deadline = new CancellationTokenSource(PassDuration, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pending.Token, deadline.Token);
        var token = linked.Token;
        var start = _time.GetTimestamp();
        Context? context;
        string? initialCategory = null;
        lock (_gate)
        {
            if (pending.Token.IsCancellationRequested) return;
            var expired = _contexts.Where(pair =>
                _time.GetElapsedTime(pair.Value.LastStopTimestamp) >= ContextLifetime).Select(pair => pair.Key).ToArray();
            if (expired.Contains(key)) initialCategory = "cursor-expired";
            foreach (var expiredKey in expired) _contexts.Remove(expiredKey);
            _contexts.TryGetValue(key, out context);
            if (context is not null && (context.ProfileId != pending.Adapter.ProfileId ||
                context.Source != pending.Reference.Source || context.Revision != pending.Reference.SettingsRevision))
            {
                _contexts.Remove(key);
                context = null;
                initialCategory = TranscriptReaderCategories.FormatChanged;
            }
            if (context is null)
            {
                if (_contexts.Count >= MaximumContexts || _contexts.Values.Count(value =>
                    value.Source.Kind == pending.Reference.Source.Kind &&
                    value.Source.ScopeId == pending.Reference.Source.ScopeId) >= MaximumContextsPerSource)
                {
                    initialCategory = TranscriptReaderCategories.Capacity;
                }
                else
                {
                    context = new Context(pending.Reference, pending.Adapter.ProfileId, _time.GetTimestamp());
                    _contexts.Add(key, context);
                }
            }
            else context.LastStopTimestamp = _time.GetTimestamp();
        }
        if (initialCategory is not null)
            await OutputAsync(pending, initialCategory, token).ConfigureAwait(false);
        if (context is null) return;
        using var reservation = _scratch.TryReserve(ScratchBytes);
        if (reservation is null)
        {
            await OutputAsync(pending, TranscriptReaderCategories.Capacity, token).ConfigureAwait(false);
            // No scratch means no safe inspection or baseline; abandon the cursor instead of backfilling.
            lock (_gate) _contexts.Remove(key);
            return;
        }
        var budget = new PassBudget();
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var due = TimeSpan.FromMilliseconds(attempt == 0 ? 0 : attempt == 1 ? 100 : 300);
                var remaining = due - _time.GetElapsedTime(start);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, _time, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!Ready(pending)) return;
                try
                {
                    using var file = TranscriptFileBoundary.Open(pending.Reference, pending.Adapter);
                    token.ThrowIfCancellationRequested();
                    if (!Ready(pending)) return;
                    if (!await ReadPassAsync(pending, context, file, budget, token).ConfigureAwait(false))
                        return;
                }
                catch (TranscriptFileBoundaryException ex)
                {
                    if (!ex.IsTransient || attempt == 2)
                    {
                        context.Identity = null;
                        await OutputAsync(pending, ex.Category, token).ConfigureAwait(false);
                        return;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    context.Identity = null;
                    await OutputAsync(pending, TranscriptReaderCategories.Unavailable, token).ConfigureAwait(false);
                    return;
                }
                catch (IOException ex) when (attempt < 2 && (ex.HResult & 0xffff) is 2 or 3 or 32 or 33)
                {
                    // A host can briefly lock a range after opening; keep this stop's cursor and budgets.
                }
                catch (IOException)
                {
                    context.Identity = null;
                    await OutputAsync(pending, TranscriptReaderCategories.Unavailable, token).ConfigureAwait(false);
                    return;
                }
            }
            await OutputAsync(pending, TranscriptReaderCategories.Waiting, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !pending.Token.IsCancellationRequested)
        {
            // Opening again after a timeout would exceed the pass budget. A fresh stop must baseline.
            context.Identity = null;
            await OutputAsync(pending, TranscriptReaderCategories.Budget, pending.Token).ConfigureAwait(false);
        }
    }

    // True means an incomplete trailing write can use one of this stop's remaining retries.
    private async Task<bool> ReadPassAsync(Pending pending, Context context, ValidatedTranscriptFile file,
        PassBudget budget, CancellationToken token)
    {
        var stream = file.Stream;
        var eof = stream.Length;
        if (eof > MaximumFileBytes)
        {
            context.Identity = null;
            await OutputAsync(pending, TranscriptReaderCategories.Budget, token).ConfigureAwait(false);
            return false;
        }
        if (context.Identity is null || context.Identity != file.Identity || eof < context.ObservedEof)
        {
            var category = context.Identity is null ? TranscriptReaderCategories.Baseline :
                context.Identity != file.Identity ? TranscriptReaderCategories.IdentityChanged : TranscriptReaderCategories.Truncated;
            await BaselineAsync(context, file, eof, budget, token).ConfigureAwait(false);
            await OutputAsync(pending, category, token).ConfigureAwait(false);
            return false;
        }
        context.ObservedEof = eof;
        if (context.Offset == eof) return context.DiscardFrame;
        var chunk = new byte[16 * 1024];
        var record = new byte[MaximumRecordBytes];
        var recordLength = 0;
        var position = context.Offset;
        stream.Position = position;
        try
        {
            while (position < eof)
            {
                token.ThrowIfCancellationRequested();
                if (!Ready(pending)) return false;
                if (budget.Bytes >= MaximumReadBytes - 1 || budget.Records >= MaximumRecords ||
                    budget.Replies >= MaximumReplies || budget.OutputBytes >= MaximumOutputBytes)
                    return await ExhaustAsync(pending, context, file, eof, budget, token).ConfigureAwait(false);
                var count = (int)Math.Min(Math.Min(chunk.Length, eof - position), MaximumReadBytes - budget.Bytes - 1);
                var read = await stream.ReadAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
                if (read == 0)
                {
                    context.Identity = null;
                    await OutputAsync(pending, TranscriptReaderCategories.Truncated, token).ConfigureAwait(false);
                    return false;
                }
                budget.Bytes += read;
                for (var i = 0; i < read; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var value = chunk[i];
                    position++;
                    if (context.DiscardFrame)
                    {
                        if (value == (byte)'\n')
                        {
                            context.DiscardFrame = false;
                            context.Offset = position;
                        }
                        else context.Offset = position;
                        continue;
                    }
                    if (value != (byte)'\n')
                    {
                        if (recordLength == record.Length - 1)
                            return await ExhaustAsync(pending, context, file, eof, budget, token).ConfigureAwait(false);
                        record[recordLength++] = value;
                        continue;
                    }
                    if (budget.Records >= MaximumRecords || budget.Replies >= MaximumReplies ||
                        budget.OutputBytes >= MaximumOutputBytes)
                        return await ExhaustAsync(pending, context, file, eof, budget, token).ConfigureAwait(false);
                    budget.Records++;
                    TranscriptFileRecord parsed;
                    try
                    {
                        // Validate all bytes, including ignored fields, without allocating a decoded copy.
                        StrictUtf8.GetCharCount(record.AsSpan(0, recordLength));
                        parsed = pending.Adapter.ParseRecord(record.AsSpan(0, recordLength), pending.Reference.SessionId);
                    }
                    catch (Exception ex) when (ex is DecoderFallbackException or JsonException or InvalidOperationException)
                    {
                        parsed = new(TranscriptFileRecordKind.Invalid);
                    }
                    if (parsed.Kind is TranscriptFileRecordKind.Invalid or TranscriptFileRecordKind.SessionMismatch ||
                        !ValidRecord(parsed))
                    {
                        await BaselineAsync(context, file, eof, budget, token).ConfigureAwait(false);
                        await OutputAsync(pending, parsed.Kind == TranscriptFileRecordKind.SessionMismatch ?
                            TranscriptReaderCategories.SessionMismatch : TranscriptReaderCategories.FormatChanged, token).ConfigureAwait(false);
                        return false;
                    }
                    if (parsed.Kind == TranscriptFileRecordKind.Assistant)
                    {
                        int bytes;
                        try { bytes = StrictUtf8.GetByteCount(parsed.AssistantText!); }
                        catch (EncoderFallbackException)
                        {
                            await BaselineAsync(context, file, eof, budget, token).ConfigureAwait(false);
                            await OutputAsync(pending, TranscriptReaderCategories.FormatChanged, token).ConfigureAwait(false);
                            return false;
                        }
                        if (bytes > MaximumOutputBytes - budget.OutputBytes)
                            return await ExhaustAsync(pending, context, file, eof, budget, token).ConfigureAwait(false);
                        if (!Ready(pending)) return false;
                        var admitted = await _admitOutput(new(pending.Reference, pending.Adapter.ProfileId,
                            TranscriptReaderCategories.Assistant, parsed.AssistantText, parsed.NativeMessageId,
                            context.Offset), token).ConfigureAwait(false);
                        if (!admitted)
                            await OutputAsync(pending, TranscriptReaderCategories.QueueRejected, token).ConfigureAwait(false);
                        budget.Replies++;
                        budget.OutputBytes += bytes;
                    }
                    context.Offset = position;
                    Array.Clear(record, 0, recordLength);
                    recordLength = 0;
                }
            }
            return recordLength > 0 || context.DiscardFrame;
        }
        finally
        {
            Array.Clear(chunk);
            Array.Clear(record);
        }
    }

    private static bool ValidRecord(TranscriptFileRecord record) =>
        record.Kind == TranscriptFileRecordKind.Ignored
            ? record.AssistantText is null && record.NativeMessageId is null
            : record.Kind == TranscriptFileRecordKind.Assistant &&
                record.AssistantText is { Length: > 0 and <= MaximumRecordBytes } &&
                (record.NativeMessageId is null || LocalTranscriptReference.ValidIdentity(record.NativeMessageId, 128));

    private async Task<bool> ExhaustAsync(Pending pending, Context context, ValidatedTranscriptFile file,
        long eof, PassBudget budget, CancellationToken token)
    {
        await BaselineAsync(context, file, eof, budget, token).ConfigureAwait(false);
        await OutputAsync(pending, TranscriptReaderCategories.Budget, token).ConfigureAwait(false);
        return false;
    }

    private static async Task BaselineAsync(Context context, ValidatedTranscriptFile file, long eof,
        PassBudget budget, CancellationToken token)
    {
        context.Identity = file.Identity;
        context.Offset = eof;
        context.ObservedEof = eof;
        context.DiscardFrame = eof != 0;
        // Reserve this byte inside the same inspection limit; if exhausted, conservatively discard
        // the next frame rather than risk emitting a pre-baseline partial frame.
        if (eof == 0 || budget.Bytes >= MaximumReadBytes) return;
        var last = new byte[1];
        try
        {
            file.Stream.Position = eof - 1;
            var read = await file.Stream.ReadAsync(last, token).ConfigureAwait(false);
            budget.Bytes += read;
            context.DiscardFrame = read != 1 || last[0] != (byte)'\n';
        }
        finally { Array.Clear(last); }
    }

    private bool Ready(Pending pending) => !pending.Token.IsCancellationRequested &&
        _isEligibleAndReady(pending.Reference);

    private async ValueTask OutputAsync(Pending pending, string category, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Ready(pending))
            await _admitOutput(new(pending.Reference, pending.Adapter.ProfileId, category), token).ConfigureAwait(false);
    }

    private sealed record Pending(LocalTranscriptReference Reference, ITranscriptFileAdapter Adapter,
        long AcceptedTimestamp, CancellationToken Token);

    private sealed class Context(LocalTranscriptReference reference, string profileId, long lastStopTimestamp)
    {
        public AgentSignaler.Contracts.SourceDescriptor Source { get; } = reference.Source;
        public long Revision { get; } = reference.SettingsRevision;
        public string ProfileId { get; } = profileId;
        public long LastStopTimestamp { get; set; } = lastStopTimestamp;
        public string? Identity { get; set; }
        public long Offset { get; set; }
        public long ObservedEof { get; set; }
        public bool DiscardFrame { get; set; }
    }

    private sealed class PassBudget
    {
        public int Bytes, Records, Replies, OutputBytes;
    }
}
