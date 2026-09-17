using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public sealed class TranscriptStoreTests
{
    private readonly Guid _machine = Guid.NewGuid();

    private TranscriptStore Store(TranscriptStoreOptions? options = null, TimeProvider? clock = null)
    {
        var store = new TranscriptStore((id, generation, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(id == _machine && generation == 1);
        }, options, clock);
        store.SetReadiness(true);
        return store;
    }

    private TranscriptOpenRequest OpenRequest(TranscriptStore store, SourceDescriptor? source = null) =>
        new(1, _machine, 1, store.ReceiverEpoch, Guid.NewGuid(), source ?? TranscriptContractTests.Source, store.OpenRevision, Guid.NewGuid());

    private static TranscriptSelection Selection(TranscriptEvent value) => new(value.MachineId, value.Source, value.SessionId, value.StreamId);

    [Fact]
    public async Task PresenceReadinessEpochAndOpenRevisionAreMandatory()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await RejectedAsync(TranscriptRejection.UnknownManagedMachine, () => store.OpenAsync(open with { MachineId = Guid.NewGuid() }));
        store.SetReadiness(false);
        await RejectedAsync(TranscriptRejection.Unavailable, () => store.OpenAsync(open));
        store.SetReadiness(true);
        await RejectedAsync(TranscriptRejection.EpochReset, () => store.OpenAsync(open));
        open = OpenRequest(store);
        var accepted = await store.OpenAsync(open);
        Assert.False(accepted.Duplicate);
        Assert.True((await store.OpenAsync(open)).Duplicate);
        await RejectedAsync(TranscriptRejection.OpenRevisionConflict, () => store.OpenAsync(open with { StreamId = Guid.NewGuid(), OpenId = Guid.NewGuid() }));
        store.SetEnabled(false);
        await RejectedAsync(TranscriptRejection.Disabled, () => store.OpenAsync(OpenRequest(store)));
    }

    [Fact]
    public async Task ExactLatestRetryIsDuplicateButConflictingAndOlderSequencesNeverAppend()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var one = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        Assert.Equal(TranscriptDisposition.Accepted, store.Accept(one).Disposition);
        Assert.Equal(TranscriptDisposition.Duplicate, store.Accept(one).Disposition);
        Assert.Equal(1, store.RetainedEventCount);
        Rejected(TranscriptRejection.SequenceConflict, () => store.Accept(one with { EventId = Guid.NewGuid() }));
        Rejected(TranscriptRejection.SequenceConflict, () => store.Accept(one with { Payload = new TranscriptLifecycle(TranscriptLifecycleSignal.StopObserved) }));
        Rejected(TranscriptRejection.SequenceConflict, () => store.Accept(one with { Sequence = 3 }));
        store.Accept(one with { Sequence = 2, EventId = Guid.NewGuid() });
        Rejected(TranscriptRejection.SequenceConflict, () => store.Accept(one));
        var page = await store.ReadEventsAsync(Selection(one));
        Assert.Equal(new long[] { 1, 2 }, page.Events.Select(e => e.Event.Sequence));
        Assert.Equal(2, store.RetainedEventCount);
    }

    [Fact]
    public async Task CoalescedGapSkipsOnlyUnreceivedSuffixAndKeepsLostAcknowledgementContent()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var one = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        store.Accept(one);
        var gap = one with { Sequence = 4, EventId = Guid.NewGuid(), Payload = new TranscriptGap(1, 4, TranscriptGapReason.QueueOverflow) };
        store.Accept(gap);
        Assert.Equal(TranscriptDisposition.Duplicate, store.Accept(gap).Disposition);
        var page = await store.ReadEventsAsync(Selection(one));
        Assert.Equal(one, page.Events[0].Event);
        Assert.Equal(new TranscriptGap(2, 4, TranscriptGapReason.QueueOverflow), page.Events[1].Event.Payload);
        Assert.Equal(4, store.Accept(one with { Sequence = 4, EventId = gap.EventId, Payload = gap.Payload }).AcknowledgedSequence);
        store.Accept(one with { Sequence = 5, EventId = Guid.NewGuid() });
        Assert.Equal(TranscriptDisposition.Duplicate, store.Accept(gap with { EventId = Guid.NewGuid() }).Disposition);
        Assert.Equal(3, store.RetainedEventCount);
    }

    [Fact]
    public async Task ReplacementRetiresPreviousStreamAndClearPreventsDelayedReopen()
    {
        using var store = Store();
        var first = OpenRequest(store);
        await store.OpenAsync(first);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, first.StreamId);
        store.Accept(value);
        var replacement = OpenRequest(store);
        await store.OpenAsync(replacement);
        Rejected(TranscriptRejection.RetiredStream, () => store.Accept(value with { Sequence = 2 }));
        Assert.Single((await store.ReadEventsAsync(Selection(value))).Events);
        Assert.True((await store.ListSessionsAsync(_machine)).Sessions[0].Closed);
        store.ClearMachine(_machine);
        Assert.Empty((await store.ReadEventsAsync(Selection(value))).Events);
        await RejectedAsync(TranscriptRejection.OpenRevisionConflict, () => store.OpenAsync(replacement));
        Assert.Equal(0, store.RetainedAccountedBytes);
    }

    [Fact]
    public async Task CloseRetainsEndedSessionsAndPurgeInvalidatesImmediately()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        store.Accept(value);
        var close = new TranscriptCloseRequest(1, _machine, 1, store.ReceiverEpoch, open.StreamId, false);
        Assert.False(store.Close(close).Duplicate);
        Assert.True(store.Close(close).Duplicate);
        Assert.Single((await store.ListSessionsAsync(_machine)).Sessions);
        var notified = 0;
        store.Invalidated += (_, _) => notified++;
        store.Close(close with { Purge = true });
        Assert.Equal(1, notified);
        Assert.Empty((await store.ReadEventsAsync(Selection(value))).Events);
    }

    [Fact]
    public async Task TtlUsesReceiptTimeAndNeitherRetryNorReadExtendsIt()
    {
        var clock = new Clock();
        using var store = Store(clock: clock);
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        store.Accept(value);
        clock.Advance(TimeSpan.FromMinutes(29));
        var page = await store.ReadEventsAsync(Selection(value));
        Assert.Equal(TranscriptContractTests.Now.AddMinutes(30), page.Events[0].ExpiresAtUtc);
        Assert.Equal(TranscriptDisposition.Duplicate, store.Accept(value).Disposition);
        var notifications = new List<TranscriptInvalidation>();
        store.Invalidated += (_, e) => notifications.Add(e);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(store.IsCurrent(page));
        store.SweepExpired();
        Assert.Empty((await store.ReadEventsAsync(Selection(value))).Events);
        Assert.Contains(notifications, e => e.Reason == TranscriptResetReason.Expired);
        Assert.Equal(0, store.RetainedEventCount);
        Assert.Equal(0, store.RetainedStreamCount);
        await RejectedAsync(TranscriptRejection.OpenRevisionConflict, () => store.OpenAsync(open));
    }

    [Fact]
    public async Task CursorSurvivesAppendsButRejectsOtherSelectionsAndExpiredPosition()
    {
        using var store = Store(new() { SessionEvents = 2 });
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var first = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        store.Accept(first);
        var page = await store.ReadEventsAsync(Selection(first));
        Assert.True(store.IsCurrent(page));
        var displayed = new TranscriptRetainedRange(page.Events[0].Event.Sequence, page.Events[^1].Event.Sequence);
        Assert.True(store.IsCurrent(page.Selection, page.ReceiverEpoch, page.InvalidationGeneration, displayed));
        store.Accept(first with { Sequence = 2, EventId = Guid.NewGuid() });
        Assert.True(store.IsCurrent(page));
        Assert.True(store.IsCurrent(page.Selection, page.ReceiverEpoch, page.InvalidationGeneration, displayed));
        var next = await store.ReadEventsAsync(Selection(first), page.ContinuationCursor);
        Assert.Equal(TranscriptResetReason.None, next.ResetReason);
        Assert.Equal(page.InvalidationGeneration, next.InvalidationGeneration);
        Assert.Equal(2, Assert.Single(next.Events).Event.Sequence);
        Assert.Equal(TranscriptResetReason.SelectionChanged,
            (await store.ReadEventsAsync(Selection(first) with { SessionId = "other" }, page.ContinuationCursor)).ResetReason);
        store.Accept(first with { Sequence = 3, EventId = Guid.NewGuid() });
        Assert.False(store.IsCurrent(page));
        Assert.False(store.IsCurrent(page.Selection, page.ReceiverEpoch, page.InvalidationGeneration, displayed));
        Assert.True(store.IsCurrent(next));
        var reset = await store.ReadEventsAsync(Selection(first), page.ContinuationCursor);
        Assert.Equal(TranscriptResetReason.Evicted, reset.ResetReason);
        Assert.Empty(reset.Events);
        Assert.Equal(2, reset.RetainedRange.FirstSequence);
        var preserved = await store.ReadEventsAsync(Selection(first), next.ContinuationCursor);
        Assert.Equal(TranscriptResetReason.None, preserved.ResetReason);
        Assert.Equal(3, Assert.Single(preserved.Events).Event.Sequence);
    }

    [Fact]
    public async Task ClearDisableAndEpochResetHaveExplicitCursorResetReasons()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        store.Accept(value);
        var page = await store.ReadEventsAsync(Selection(value));
        store.ClearMachine(_machine);
        Assert.False(store.IsCurrent(page));
        Assert.Equal(TranscriptResetReason.Cleared, (await store.ReadEventsAsync(Selection(value), page.ContinuationCursor)).ResetReason);
        store.SetEnabled(false);
        Assert.Equal(TranscriptAvailability.Disabled, (await store.ReadEventsAsync(Selection(value))).Availability);
        store.Reset();
        Assert.Equal(TranscriptResetReason.ReceiverReset, (await store.ReadEventsAsync(Selection(value), page.ContinuationCursor)).ResetReason);
    }

    [Fact]
    public async Task ByteAndEntryQuotasEvictOldestWithoutLosingReplayState()
    {
        using var store = Store(new() { Events = 3, MachineEvents = 3, SessionEvents = 2 });
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        for (var sequence = 1; sequence <= 5; sequence++)
            store.Accept(value with { Sequence = sequence, EventId = Guid.NewGuid() });
        Assert.Equal(2, store.RetainedEventCount);
        var page = await store.ReadEventsAsync(Selection(value));
        Assert.Equal(new long[] { 4, 5 }, page.Events.Select(e => e.Event.Sequence));
        Assert.True(page.HasGaps);
        Assert.Equal(TranscriptGapReason.Capacity, page.Gap!.Reason);
        Rejected(TranscriptRejection.SequenceConflict, () => store.Accept(value));
        Assert.Equal(page.Events.Sum(e => (long)TranscriptProtocol.AccountedEventBytes(e.SerializedBytes)), store.RetainedAccountedBytes);
    }

    [Fact]
    public async Task MetadataSlotsAreRefusedInsteadOfEvictingLiveOrderingState()
    {
        using var store = Store(new() { Streams = 1, Sessions = 1 });
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        store.Accept(value);
        await RejectedAsync(TranscriptRejection.Capacity, () => store.OpenAsync(OpenRequest(store, new("vscode", "scope"))));
        Rejected(TranscriptRejection.Capacity, () => store.Accept(value with { Sequence = 2, SessionId = "other" }));
        Assert.Equal(TranscriptDisposition.Duplicate, store.Accept(value).Disposition);
    }

    [Fact]
    public async Task PagesAndSelectorsAreBoundedAndCancellationDoesNotReturnContent()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        for (var i = 1; i <= 40; i++)
            store.Accept(value with { Sequence = i, EventId = Guid.NewGuid() });
        var page = await store.ReadEventsAsync(Selection(value));
        Assert.Equal(32, page.Events.Length);
        Assert.True(page.Events.Sum(e => e.SerializedBytes) <= 128 * 1024);
        Assert.Equal(8, (await store.ReadEventsAsync(Selection(value), page.ContinuationCursor)).Events.Length);
        var latest = await store.ReadLatestEventsAsync(Selection(value));
        Assert.Equal(32, latest.Events.Length);
        Assert.Equal(9, latest.Events[0].Event.Sequence);
        Assert.Equal(40, latest.Events[^1].Event.Sequence);
        Assert.True(store.IsCurrent(latest));
        Assert.Empty((await store.ReadEventsAsync(Selection(value), latest.ContinuationCursor)).Events);
        var older = await store.ReadEventsBeforeAsync(Selection(value), latest.PreviousCursor!);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, older.Events.Select(e => e.Event.Sequence));
        Assert.Null(older.PreviousCursor);
        var forwardAgain = await store.ReadEventsAsync(Selection(value), older.ContinuationCursor);
        Assert.Equal(latest.Events.Select(e => e.Event.Sequence), forwardAgain.Events.Select(e => e.Event.Sequence));
        for (var i = 41; i <= 60; i++)
            store.Accept(value with { Sequence = i, EventId = Guid.NewGuid(), SessionId = $"session-{i}" });
        var sessions = await store.ListSessionsAsync(_machine);
        Assert.Equal(16, sessions.Sessions.Length);
        Assert.Equal(5, (await store.ListSessionsAsync(_machine, sessions.ContinuationCursor)).Sessions.Length);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadEventsAsync(Selection(value), cancellationToken: cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListSessionsAsync(_machine, cancellationToken: cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadLatestEventsAsync(Selection(value), cancel.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadEventsBeforeAsync(Selection(value), latest.PreviousCursor!, cancel.Token).AsTask());
    }

    [Fact]
    public async Task ExactSerializedBoundaryAndEscapingHeavyOversizeAreEnforced()
    {
        using var store = Store();
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = TranscriptContractTests.Event(_machine, store.ReceiverEpoch, open.StreamId);
        var message = (TranscriptMessage)value.Payload;
        value = value with { Payload = message with { Text = "a" } };
        var overhead = JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json).Length - 1;
        value = value with { Payload = message with { Text = new string('a', TranscriptProtocol.MaxEventBytes - overhead) } };
        Assert.Equal(TranscriptProtocol.MaxEventBytes, JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json).Length);
        store.Accept(value);
        Rejected(TranscriptRejection.Oversized, () => store.Accept(value with
        {
            Sequence = 2, EventId = Guid.NewGuid(), Payload = message with { Text = new string('<', 10_000) }
        }));
        Assert.Equal(1, store.RetainedEventCount);
    }

    [Fact]
    public async Task SameSessionNameNeverMergesMachinesSourcesOrStreams()
    {
        using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
        store.SetReadiness(true);
        var otherMachine = Guid.NewGuid();
        var values = new List<TranscriptEvent>();
        foreach (var (machine, source) in new[]
        {
            (_machine, TranscriptContractTests.Source),
            (_machine, new SourceDescriptor("vscode", "synthetic")),
            (otherMachine, TranscriptContractTests.Source)
        })
        {
            var open = new TranscriptOpenRequest(1, machine, 1, store.ReceiverEpoch, Guid.NewGuid(), source, store.OpenRevision, Guid.NewGuid());
            await store.OpenAsync(open);
            var value = TranscriptContractTests.Event(machine, store.ReceiverEpoch, open.StreamId) with { Source = source };
            store.Accept(value);
            values.Add(value);
        }
        foreach (var value in values)
            Assert.Equal(value, Assert.Single((await store.ReadEventsAsync(Selection(value))).Events).Event);
        Assert.Equal(2, (await store.ListSessionsAsync(_machine)).Sessions.Length);
        Assert.Single((await store.ListSessionsAsync(otherMachine)).Sessions);
        store.ClearMachine(_machine, removed: true);
        Assert.Single((await store.ReadEventsAsync(Selection(values[^1]))).Events);
        Assert.Equal(1, store.RetainedEventCount);
    }

    [Fact]
    public async Task CursorExpiresAtFiveMinutesAndReducedByteQuotaIsChargedAtAlignedSize()
    {
        var clock = new Clock();
        var prototype = TranscriptContractTests.Event(_machine, Guid.NewGuid(), Guid.NewGuid());
        var charge = TranscriptProtocol.AccountedEventBytes(JsonSerializer.SerializeToUtf8Bytes(prototype, TranscriptProtocol.Json).Length);
        using var store = Store(new() { SessionBytes = charge * 2 }, clock);
        var open = OpenRequest(store);
        await store.OpenAsync(open);
        var value = prototype with { StreamId = open.StreamId, ReceiverEpoch = store.ReceiverEpoch };
        store.Accept(value);
        var page = await store.ReadEventsAsync(Selection(value));
        Assert.True(page.ContinuationCursor!.Length <= 512);
        for (var i = 0; i < 150; i++)
            await store.ReadEventsAsync(Selection(value));
        Assert.Equal(TranscriptResetReason.None,
            (await store.ReadEventsAsync(Selection(value), page.ContinuationCursor)).ResetReason);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(TranscriptResetReason.CursorExpired,
            (await store.ReadEventsAsync(Selection(value), page.ContinuationCursor)).ResetReason);
        Assert.Equal(1, store.RetainedEventCount);
        store.Accept(value with { Sequence = 2, EventId = Guid.NewGuid() });
        Assert.Equal(2 * charge, store.RetainedAccountedBytes);
        store.Accept(value with { Sequence = 3, EventId = Guid.NewGuid() });
        Assert.Equal(2 * charge, store.RetainedAccountedBytes);
        Assert.Equal(2, store.RetainedEventCount);
    }

    [Fact]
    public async Task UnicodeHeavyLoadStaysWithinAllFixedReceiverPartitions()
    {
        using var store = new TranscriptStore((_, _, _) => ValueTask.FromResult(true));
        store.SetReadiness(true);
        var text = string.Concat(Enumerable.Repeat("😀<\"é", 700));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(text).Length > text.Length * 2);
        var selections = new List<TranscriptSelection>();
        for (var machineIndex = 0; machineIndex < 8; machineIndex++)
        {
            var machine = Guid.NewGuid();
            var open = new TranscriptOpenRequest(1, machine, 1, store.ReceiverEpoch, Guid.NewGuid(),
                TranscriptContractTests.Source, store.OpenRevision, Guid.NewGuid());
            await store.OpenAsync(open);
            for (var sequence = 1; sequence <= 256; sequence++)
            {
                var value = TranscriptContractTests.Event(machine, store.ReceiverEpoch, open.StreamId, sequence, $"session-{sequence % 4}");
                value = value with { Payload = ((TranscriptMessage)value.Payload) with { Text = text } };
                store.Accept(value);
                if (sequence <= 4) selections.Add(Selection(value));
            }
            Assert.True(store.RetainedAccountedBytes <= 48 * 1024 * 1024);
            Assert.True(store.RetainedEventCount <= 2048);
        }
        long total = 0;
        foreach (var group in selections.GroupBy(s => s.MachineId))
        {
            long machineCharge = 0;
            var machineCount = 0;
            foreach (var selection in group)
            {
                string? cursor = null;
                var sessionCount = 0;
                long sessionCharge = 0;
                while (true)
                {
                    var page = await store.ReadEventsAsync(selection, cursor);
                    Assert.True(page.Events.Length <= 32);
                    Assert.True(page.Events.Sum(e => e.SerializedBytes) <= 128 * 1024);
                    if (page.Events.Length == 0) break;
                    sessionCount += page.Events.Length;
                    sessionCharge += page.Events.Sum(e => (long)TranscriptProtocol.AccountedEventBytes(e.SerializedBytes));
                    cursor = page.ContinuationCursor;
                }
                Assert.True(sessionCount <= 128);
                Assert.True(sessionCharge <= 2 * 1024 * 1024);
                machineCharge += sessionCharge;
                machineCount += sessionCount;
            }
            Assert.True(machineCount <= 256);
            Assert.True(machineCharge <= 8 * 1024 * 1024);
            total += machineCharge;
        }
        Assert.Equal(total, store.RetainedAccountedBytes);
        Assert.True(total > 40 * 1024 * 1024);
    }

    private static void Rejected(TranscriptRejection reason, Action action) =>
        Assert.Equal(reason, Assert.Throws<TranscriptRejectedException>(action).Category);
    private static async Task RejectedAsync(TranscriptRejection reason, Func<ValueTask<TranscriptOpenResponse>> action) =>
        Assert.Equal(reason, (await Assert.ThrowsAsync<TranscriptRejectedException>(() => action().AsTask())).Category);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = TranscriptContractTests.Now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }
}
