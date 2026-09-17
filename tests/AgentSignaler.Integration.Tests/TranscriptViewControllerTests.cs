using System.Collections.Immutable;
using System.Runtime;
using System.Runtime.CompilerServices;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

[Collection("Dashboard settings environment")]
public sealed class TranscriptViewControllerTests
{
    private static readonly Guid Machine = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly SourceDescriptor Source = new("copilot-cli", "synthetic-scope", "test");

    [Fact]
    public async Task OpensOnlyRetainedSelectedMachineAndIncludesEndedSessions()
    {
        var reader = new Reader();
        var ended = reader.Add(Machine, Source, "same", "synthetic ended", closed: true);
        reader.Add(Guid.NewGuid(), Source, "same", "other machine");
        var ide = reader.Add(Machine, new("vscode", "different-scope"), "same", "synthetic IDE");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, offline: true);
        Assert.Equal(2, controller.State.Sessions.Length);
        Assert.Contains(controller.State.Sessions, session => session.Closed);
        Assert.Equal("synthetic ended", Assert.Single(controller.State.Entries).Text);
        Assert.Contains("offline", controller.State.Message);
        await controller.SelectAsync(ide);
        Assert.Equal("synthetic IDE", Assert.Single(controller.State.Entries).Text);
        Assert.NotEqual(ended.StreamId, controller.State.Selection!.StreamId);
    }

    [Fact]
    public async Task PreservesCurrentWindowWhileNotFollowingAndShowsNewActivity()
    {
        var reader = new Reader { PageSize = 2 };
        var selection = reader.Add(Machine, Source, "session", "one", "two", "three", "four");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        Assert.Equal(new long[] { 3, 4 }, controller.State.Entries.Select(entry => entry.Sequence));
        await controller.OlderAsync();
        Assert.Equal(new long[] { 1, 2 }, controller.State.Entries.Select(entry => entry.Sequence));
        reader.Append(selection, "five");
        var reads = reader.EventReads;
        await controller.RefreshAsync();
        Assert.Equal(reads, reader.EventReads);
        Assert.True(controller.State.HasNewActivity);
        Assert.False(controller.State.FollowingLatest);
        Assert.Equal(new long[] { 1, 2 }, controller.State.Entries.Select(entry => entry.Sequence));
        await controller.LatestAsync();
        Assert.Equal(5, controller.State.Entries[^1].Sequence);
        Assert.True(controller.State.FollowingLatest);
    }

    [Fact]
    public async Task OlderPagingUsesTheImmediatelyPrecedingBoundedWindow()
    {
        var reader = new Reader { PageSize = 3 };
        reader.Add(Machine, Source, "session", Enumerable.Range(1, 8).Select(index => $"synthetic {index}").ToArray());
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        Assert.Equal(new long[] { 6, 7, 8 }, controller.State.Entries.Select(entry => entry.Sequence));
        await controller.OlderAsync();
        Assert.Equal(new long[] { 3, 4, 5 }, controller.State.Entries.Select(entry => entry.Sequence));
        await controller.OlderAsync();
        Assert.Equal(new long[] { 1, 2 }, controller.State.Entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task RepeatedPagingAndTabSwitchingNeverAccumulateText()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "session", Enumerable.Range(1, 128).Select(index => $"synthetic {index}").ToArray());
        using var controller = new TranscriptViewController(reader, reader.Clock);
        for (var i = 0; i < 5; i++)
        {
            await controller.ShowAsync(Machine, false);
            Assert.Equal(32, controller.State.Entries.Length);
            for (var page = 0; page < 3; page++)
            {
                await controller.OlderAsync();
                Assert.InRange(controller.State.Entries.Length, 1, 32);
            }
            controller.Hide();
            Assert.Empty(controller.State.Entries);
            Assert.Empty(controller.State.Sessions);
        }
        Assert.Equal(1, reader.MaxConcurrentReads);
    }

    [Fact]
    public async Task PollsOnlyWhileVisibleAndReleasesTextOnHideAndDispose()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "session", "synthetic text");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        reader.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(0, reader.SessionReads);
        await controller.ShowAsync(Machine, false);
        var reads = reader.SessionReads;
        reader.Clock.Advance(TimeSpan.FromSeconds(1));
        await controller.RefreshAsync();
        Assert.True(reader.SessionReads > reads);
        controller.Hide();
        reads = reader.SessionReads;
        reader.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(reads, reader.SessionReads);
        Assert.Empty(controller.State.Entries);
        await controller.ShowAsync(Machine, false);
        controller.Dispose();
        Assert.Empty(controller.State.Entries);
        Assert.Equal(0, reader.Subscribers);
    }

    [Theory]
    [InlineData(TranscriptResetReason.Cleared)]
    [InlineData(TranscriptResetReason.Removed)]
    [InlineData(TranscriptResetReason.Disabled)]
    [InlineData(TranscriptResetReason.Unavailable)]
    [InlineData(TranscriptResetReason.ReceiverReset)]
    [InlineData(TranscriptResetReason.Evicted)]
    public async Task InvalidationImmediatelyDropsVisibleAndCachedText(TranscriptResetReason reason)
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "session", "synthetic text");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        Assert.NotEmpty(controller.State.Entries);
        reader.Invalidate(Machine, reason);
        Assert.Empty(controller.State.Entries);
        Assert.Empty(controller.State.Sessions);
    }

    [Fact]
    public async Task ExpiryDeadlineDropsTextEvenWhileNextReadIsBlocked()
    {
        var reader = new Reader { Retention = TimeSpan.FromMilliseconds(500) };
        reader.Add(Machine, Source, "session", "synthetic expires");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.HoldNext();
        var blocked = controller.RefreshAsync();
        await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        reader.Clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Empty(controller.State.Entries);
        Assert.Contains("expired", controller.State.Message);
        reader.Release();
        await blocked;
        Assert.Empty(controller.State.Entries);
    }

    [Fact]
    public async Task SelectionCancelsOldReadRejectsLateResultAndSerializesReads()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "first", "first synthetic");
        var other = reader.Add(Machine, Source, "second", "second synthetic");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.HoldNext();
        var pending = controller.RefreshAsync();
        await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var selected = controller.SelectAsync(other);
        Assert.True(reader.HeldToken.IsCancellationRequested);
        Assert.Empty(controller.State.Entries);
        reader.Release();
        await Task.WhenAll(pending, selected);
        Assert.Equal("second synthetic", Assert.Single(controller.State.Entries).Text);
        Assert.Equal(1, reader.MaxConcurrentReads);
    }

    [Fact]
    public async Task RefreshesCoalesceAndLateInvalidatedPagesCannotRestoreText()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "session", "synthetic secret");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.HoldNext();
        var pending = controller.RefreshAsync();
        await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var repeated = Enumerable.Range(0, 20).Select(_ => controller.RefreshAsync()).ToArray();
        Assert.All(repeated, task => Assert.Same(pending, task));
        reader.Invalidate(Machine, TranscriptResetReason.Cleared);
        controller.Hide();
        reader.Release();
        await pending;
        Assert.Empty(controller.State.Entries);
        Assert.Equal(1, reader.MaxConcurrentReads);
    }

    [Fact]
    public async Task MissingSelectionDoesNotSilentlySwitchToAnotherSession()
    {
        var reader = new Reader();
        var first = reader.Add(Machine, Source, "first", "first");
        reader.Add(Machine, Source, "second", "second");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.Remove(first);
        await controller.RefreshAsync();
        await controller.RefreshAsync();
        Assert.Null(controller.State.Selection);
        Assert.Empty(controller.State.Entries);
    }

    [Theory]
    [InlineData(TranscriptAvailability.Disabled, "disabled")]
    [InlineData(TranscriptAvailability.Unavailable, "HTTPS")]
    [InlineData(TranscriptAvailability.NoEvents, "No events")]
    [InlineData(TranscriptAvailability.Expired, "expired")]
    public async Task EmptyAndUnavailableStatesHaveExplicitExplanations(TranscriptAvailability availability, string expected)
    {
        var reader = new Reader { Availability = availability };
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        Assert.Empty(controller.State.Entries);
        Assert.Contains(expected, controller.State.Message);
    }

    [Fact]
    public async Task ReadFailureHasCategoryOnlyMessageAndReleasesOldText()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "session", "synthetic permitted");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.FailReads = true;
        await controller.RefreshAsync();
        Assert.Empty(controller.State.Entries);
        Assert.Contains("read failed", controller.State.Message);
        Assert.DoesNotContain("FORBIDDEN", controller.State.Message);
    }

    [Fact]
    public async Task SyntheticStoreToViewerPreservesPlainTextAndPurgesOnDisable()
    {
        var clock = new TestTimeProvider();
        using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true), timeProvider: clock);
        store.SetReadiness(true);
        var epoch = store.ReceiverEpoch;
        var stream = Guid.NewGuid();
        await store.OpenAsync(new(1, Machine, 1, epoch, stream, Source, store.OpenRevision, Guid.NewGuid()));
        const string text = "<script>inert synthetic text</script> https://example.invalid ![image](https://example.invalid)";
        store.Accept(CreateEvent(new(Machine, Source, "session", stream), epoch, clock.GetUtcNow(), 1, text));
        using var controller = new TranscriptViewController(store, clock);
        await controller.ShowAsync(Machine, false);
        Assert.Equal(text, Assert.Single(controller.State.Entries).Text);
        Assert.Contains("no production format profile is verified", controller.State.Message);
        Assert.Contains("Receiver field support does not verify host capture", controller.State.Message);
        Assert.DoesNotContain(text, controller.State.ToString());
        Assert.DoesNotContain(text, controller.State.Entries[0].ToString());
        store.SetEnabled(false);
        Assert.Empty(controller.State.Entries);
        Assert.Equal(0, store.RetainedEventCount);
    }

    [Fact]
    public async Task EscapingHeavyStorePagesStayWithinTheReaderAndRenderedWindowBudgets()
    {
        var clock = new TestTimeProvider();
        using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true), timeProvider: clock);
        store.SetReadiness(true);
        var selection = new TranscriptSelection(Machine, Source, "bounded", Guid.NewGuid());
        await store.OpenAsync(new(1, Machine, 1, store.ReceiverEpoch, selection.StreamId, Source,
            store.OpenRevision, Guid.NewGuid()));
        var synthetic = new string('\u2603', 5_000);
        for (var sequence = 1; sequence <= 64; sequence++)
            store.Accept(CreateEvent(selection, store.ReceiverEpoch, clock.GetUtcNow(), sequence, synthetic));
        using var controller = new TranscriptViewController(store, clock);
        await controller.ShowAsync(Machine, false);
        Assert.InRange(controller.State.Entries.Length, 1, 4);
        Assert.Equal(64, controller.State.Entries[^1].Sequence);
        Assert.True(controller.State.Entries.Sum(entry => (long)(entry.Header.Length + entry.Text.Length) * sizeof(char)) < 256 * 1024);
        Assert.InRange(store.RetainedAccountedBytes, 1, 2 * 1024 * 1024);
        await controller.OlderAsync();
        Assert.InRange(controller.State.Entries.Length, 1, 4);
        controller.Hide();
        Assert.Empty(controller.State.Entries);
    }

    [Fact]
    public async Task ManagedDecodedPagingFitsReaderPartitionWithGcDelayedAndReleasesViewModels()
    {
        const long readerPartition = 8 * 1024 * 1024;
        var clock = new TestTimeProvider();
        using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true), timeProvider: clock);
        store.SetReadiness(true);
        var selection = new TranscriptSelection(Machine, Source, "allocation-pressure", Guid.NewGuid());
        await store.OpenAsync(new(1, Machine, 1, store.ReceiverEpoch, selection.StreamId, Source,
            store.OpenRevision, Guid.NewGuid()));
        var synthetic = new string('A', 12_000) + new string('\u2603', 400) + " \ud83d\ude42";
        for (var sequence = 1; sequence <= 128; sequence++)
            store.Accept(CreateEvent(selection, store.ReceiverEpoch, clock.GetUtcNow(), sequence, synthetic));
        using var controller = new TranscriptViewController(store, clock);
        await controller.ShowAsync(Machine, false);
        Assert.True(controller.State.HasOlder);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(GC.TryStartNoGCRegion(readerPartition), "The isolated test runner could not reserve the fixed reader partition.");
        long allocated;
        bool stayedWithinNoGcRegion;
        try
        {
            var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
            // Keep discarded decoded pages alive until this region ends while exercising
            // bounded older windows, presentation records and the current pending read.
            for (var page = 0; page < 8 && controller.State.HasOlder; page++)
                await controller.OlderAsync();
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocationStart;
            stayedWithinNoGcRegion = GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
            Assert.InRange(controller.State.Entries.Length, 1, 32);
            Assert.True(controller.State.Entries.Sum(entry => (long)(entry.Header.Length + entry.Text.Length) * sizeof(char)) <= 256 * 1024);
        }
        finally
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
        }
        Assert.True(stayedWithinNoGcRegion, "Managed paging exceeded the reserved delayed-GC allocation region.");
        Assert.InRange(allocated, 1, readerPartition);
        Assert.InRange(GC.GetTotalMemory(forceFullCollection: false) - before, -readerPartition, readerPartition);
        var references = WeakEntries(controller);
        controller.Hide();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Yield();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.All(references, reference => Assert.False(reference.TryGetTarget(out _)));
        Assert.Empty(controller.State.Entries);
        Assert.Empty(controller.State.Sessions);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<TranscriptEntry>[] WeakEntries(TranscriptViewController controller) =>
        controller.State.Entries.Select(entry => new WeakReference<TranscriptEntry>(entry)).ToArray();

    [Fact]
    public async Task SessionSelectorIsBoundedToTwoPages()
    {
        var reader = new Reader();
        for (var index = 0; index < 33; index++)
            reader.Add(Machine, Source, $"session-{index}", "synthetic");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        Assert.Equal(32, controller.State.Sessions.Length);
        Assert.Equal(2, reader.SessionReads);
    }

    [Fact]
    public async Task MachineChangeRejectsOldEpochAndSelectionCompletion()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "same", "first machine");
        var other = Guid.NewGuid();
        reader.Add(other, Source, "same", "second machine");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.HoldNext();
        var pending = controller.RefreshAsync();
        await reader.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var changed = controller.ShowAsync(other, false);
        Assert.Empty(controller.State.Entries);
        reader.Release();
        await Task.WhenAll(pending, changed);
        Assert.Equal(other, controller.State.Selection!.MachineId);
        Assert.Equal("second machine", Assert.Single(controller.State.Entries).Text);
        Assert.Equal(1, reader.MaxConcurrentReads);
    }

    [Fact]
    public async Task LateDispatcherAccessRevalidatesEpochBeforeReturningText()
    {
        var reader = new Reader();
        reader.Add(Machine, Source, "session", "synthetic old epoch");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.RotateEpochWithoutNotification();
        Assert.Empty(controller.State.Entries);
        Assert.Contains("stale text", controller.State.Message);
    }

    [Fact]
    public async Task ExpiringEarlierUnrenderedPositionsDoesNotResetTheDisplayedPage()
    {
        var reader = new Reader { PageSize = 2 };
        var selection = reader.Add(Machine, Source, "session", "one", "two", "three", "four");
        using var controller = new TranscriptViewController(reader, reader.Clock);
        await controller.ShowAsync(Machine, false);
        reader.ExpireEvent(selection, 1);
        Assert.Equal(new long[] { 3, 4 }, controller.State.Entries.Select(entry => entry.Sequence));
        reader.ExpireEvent(selection, 3);
        Assert.Empty(controller.State.Entries);
    }

    [Theory]
    [InlineData(TranscriptGapReason.FormatUnverified, "unverified")]
    [InlineData(TranscriptGapReason.FormatChanged, "changed")]
    [InlineData(TranscriptGapReason.FileUnavailable, "unavailable")]
    [InlineData(TranscriptGapReason.ReadBudgetExceeded, "budget")]
    [InlineData(TranscriptGapReason.BaselineEstablished, "earlier replies unavailable")]
    [InlineData(TranscriptGapReason.PartialWrite, "next stop")]
    public void ReaderReasonsAreNotPresentedAsSuccessfulEmptyReplies(TranscriptGapReason reason, string expected) =>
        Assert.Contains(expected, TranscriptViewController.GapMessage(reason));

    private static TranscriptEvent CreateEvent(TranscriptSelection selection, Guid epoch, DateTimeOffset now, long sequence, string text) => new()
    {
        EventId = Guid.NewGuid(), MachineId = selection.MachineId, Generation = 1, ReceiverEpoch = epoch,
        StreamId = selection.StreamId, Sequence = sequence, Source = selection.Source, SessionId = selection.SessionId,
        Provenance = new("userPromptSubmitted", "test", now, now, TranscriptOrdering.Arrival, CaptureOrigin.Hook, "test-hook"),
        Payload = new TranscriptMessage(TranscriptRole.User, $"message-{sequence}", TranscriptMessageIdOrigin.Local, text)
    };

    private sealed class Reader : ITranscriptReader
    {
        private readonly Dictionary<TranscriptSelection, List<TranscriptReadEvent>> _events = [];
        private readonly HashSet<TranscriptSelection> _closed = [];
        private EventHandler<TranscriptInvalidation>? _invalidated;
        private TaskCompletionSource? _held;
        private bool _holdNext;
        private int _active;
        private long _generation;
        private Guid _epoch = Guid.NewGuid();
        public TestTimeProvider Clock { get; } = new();
        public int PageSize { get; init; } = 32;
        public TimeSpan Retention { get; init; } = TimeSpan.FromMinutes(30);
        public TranscriptAvailability Availability { get; init; } = TranscriptAvailability.Partial;
        public bool FailReads { get; set; }
        public int SessionReads { get; private set; }
        public int EventReads { get; private set; }
        public int MaxConcurrentReads { get; private set; }
        public int Subscribers { get; private set; }
        public TaskCompletionSource ReadStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken HeldToken { get; private set; }
        public event EventHandler<TranscriptInvalidation>? Invalidated
        {
            add { _invalidated += value; Subscribers++; }
            remove { _invalidated -= value; Subscribers--; }
        }
        public TranscriptSelection Add(Guid machine, SourceDescriptor source, string session, string text, bool closed)
        {
            var selection = Add(machine, source, session, text);
            if (closed) _closed.Add(selection);
            return selection;
        }
        public TranscriptSelection Add(Guid machine, SourceDescriptor source, string session, params string[] text)
        {
            var selection = new TranscriptSelection(machine, source, session, Guid.NewGuid());
            _events.Add(selection, []);
            foreach (var item in text) Append(selection, item);
            return selection;
        }
        public void Append(TranscriptSelection selection, string text)
        {
            var list = _events[selection];
            var now = Clock.GetUtcNow();
            list.Add(new(CreateEvent(selection, _epoch, now, list.Count + 1, text), now, now + Retention, 512));
        }
        public void Remove(TranscriptSelection selection) => _events.Remove(selection);
        public void RotateEpochWithoutNotification() => _epoch = Guid.NewGuid();
        public void ExpireEvent(TranscriptSelection selection, long sequence)
        {
            _events[selection].RemoveAll(item => item.Event.Sequence == sequence);
            _invalidated?.Invoke(this, new(_epoch, selection.MachineId, selection, _generation, TranscriptResetReason.Expired));
        }
        public bool IsCurrent(TranscriptEventsPage page) =>
            page.ReceiverEpoch == _epoch && page.InvalidationGeneration == _generation &&
            _events.TryGetValue(page.Selection, out var entries) &&
            page.Events.All(item => item.ExpiresAtUtc > Clock.GetUtcNow() &&
                entries.Any(current => current.Event.Sequence == item.Event.Sequence));
        public bool IsCurrent(TranscriptSelection selection, Guid receiverEpoch, long invalidationGeneration, TranscriptRetainedRange displayedRange) =>
            receiverEpoch == _epoch && invalidationGeneration == _generation &&
            _events.TryGetValue(selection, out var entries) &&
            entries.Any(item => item.Event.Sequence == displayedRange.FirstSequence && item.ExpiresAtUtc > Clock.GetUtcNow()) &&
            entries.Any(item => item.Event.Sequence == displayedRange.LastSequence && item.ExpiresAtUtc > Clock.GetUtcNow()) &&
            entries.Where(item => item.Event.Sequence >= displayedRange.FirstSequence && item.Event.Sequence <= displayedRange.LastSequence)
                .All(item => item.ExpiresAtUtc > Clock.GetUtcNow());
        public void HoldNext()
        {
            _holdNext = true;
            _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void Release() => _held!.TrySetResult();
        public void Invalidate(Guid machine, TranscriptResetReason reason)
        {
            _generation++;
            foreach (var selection in _events.Keys.Where(selection => selection.MachineId == machine).ToArray()) _events.Remove(selection);
            _invalidated?.Invoke(this, new(_epoch, machine, null, _generation, reason));
        }
        public ValueTask<TranscriptSessionsPage> ListSessionsAsync(Guid machineId, string? cursor = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionReads++;
            var skip = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            var all = _events.Where(pair => pair.Key.MachineId == machineId).ToArray();
            var rows = all.Skip(skip).Take(16).Select(pair => new TranscriptSessionInfo(pair.Key, Clock.GetUtcNow(),
                _closed.Contains(pair.Key), new(1, pair.Value.Count), false, false)).ToImmutableArray();
            return ValueTask.FromResult(new TranscriptSessionsPage(_epoch, EventReads, _generation, rows,
                skip + 16 < all.Length ? (skip + 16).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                Availability, TranscriptResetReason.None, TranscriptProtocol.ImplementedCapabilities));
        }
        public ValueTask<TranscriptEventsPage> ReadLatestEventsAsync(TranscriptSelection selection, CancellationToken cancellationToken = default) =>
            ReadEventsAsync(selection, Math.Max(0, _events[selection].Count - PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);
        public ValueTask<TranscriptEventsPage> ReadEventsAsync(TranscriptSelection selection, string? cursor = null, CancellationToken cancellationToken = default) =>
            ReadEventsCoreAsync(selection, cursor, PageSize, cancellationToken);
        public ValueTask<TranscriptEventsPage> ReadEventsBeforeAsync(TranscriptSelection selection, string cursor, CancellationToken cancellationToken = default)
        {
            var position = int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
            return ReadEventsCoreAsync(selection, Math.Max(0, position - PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Math.Min(position, PageSize), cancellationToken);
        }
        private async ValueTask<TranscriptEventsPage> ReadEventsCoreAsync(TranscriptSelection selection, string? cursor, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EventReads++;
            MaxConcurrentReads = Math.Max(MaxConcurrentReads, Interlocked.Increment(ref _active));
            try
            {
                if (FailReads) throw new IOException("FORBIDDEN diagnostic exception detail");
                var skip = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
                var all = _events[selection];
                var page = new TranscriptEventsPage(_epoch, selection, EventReads, _generation, new(1, all.Count),
                    all.Skip(skip).Take(count).ToImmutableArray(),
                    skip + PageSize < all.Count ? (skip + PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                    Availability, TranscriptResetReason.None, false, false, TranscriptProtocol.ImplementedCapabilities,
                    PreviousCursor: skip > 0 ? skip.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
                if (_holdNext)
                {
                    _holdNext = false;
                    HeldToken = cancellationToken;
                    ReadStarted.TrySetResult();
                    await _held!.Task;
                }
                return page;
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
