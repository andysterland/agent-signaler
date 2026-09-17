using System.Collections.Concurrent;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class TranscriptDeliveryStoreTests
{
    private static readonly SourceDescriptor Source = new("copilot-cli", "synthetic-delivery-store", "test-v1");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementAndOverflowPreserveReceivedPrefixAndAdvanceOnlyMissingSuffix(bool expire)
    {
        var config = Configuration();
        using var store = new TranscriptStore((machine, generation, _) =>
            ValueTask.FromResult(machine == config.MachineId && generation == 17));
        store.SetReadiness(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = 0;
        var transport = new StoreTransport(store)
        {
            AfterAccept = async (value, _, token) =>
            {
                if (value.Sequence == 2 && Interlocked.Increment(ref first) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return new(429, default, RetryAfter: expire ? TimeSpan.FromMinutes(3) : null);
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, jitter: () => 0);
        await Ready(delivery, config);
        Assert.True(delivery.TryAccept(Message("received-prefix"), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            for (var index = 0; index < 70; index++)
                Assert.True(delivery.TryAccept(Message($"synthetic-{index}"), 1));
            release.TrySetResult();
            await Eventually(() => delivery.PendingEvents == 0);
            Assert.Empty(transport.Rejections);
            Assert.Equal(1, transport.MaximumConcurrent);
            var sessions = await store.ListSessionsAsync(config.MachineId);
            var session = Assert.Single(sessions.Sessions);
            var page = await store.ReadEventsAsync(session.Selection);
            Assert.Equal(72, page.RetainedRange.LastSequence);
            var retainedPrefix = Assert.Single(page.Events,
                e => e.Event.Payload is TranscriptMessage { Text: "received-prefix" });
            Assert.Equal(2, retainedPrefix.Event.Sequence);
            var knownGap = Assert.Single(page.Events, e => e.Event.Payload is TranscriptGap { FromSequence: not null });
            Assert.Equal(3, ((TranscriptGap)knownGap.Event.Payload).FromSequence);
            Assert.Equal(9, ((TranscriptGap)knownGap.Event.Payload).ThroughSequence);
            Assert.Equal(expire ? 1 : 2, transport.Receipts.Count(r => r.Event.Sequence == 2));
            if (!expire)
                Assert.Contains(transport.Receipts, r => r.Event.Sequence == 2 &&
                    r.Acknowledgement.Disposition == TranscriptDisposition.Duplicate);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task ReceiverResetReopensPendingIdentityWithoutReplayingAcknowledgedHistory()
    {
        var config = Configuration();
        using var store = new TranscriptStore((machine, generation, _) =>
            ValueTask.FromResult(machine == config.MachineId && generation == 17));
        store.SetReadiness(true);
        var reset = false;
        var transport = new StoreTransport(store)
        {
            BeforeAccept = value =>
            {
                if (value.Payload is TranscriptMessage { Text: "pending-after-reset" } && !reset)
                {
                    reset = true;
                    store.Reset();
                }
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery, config);
        Assert.True(delivery.TryAccept(Message("acknowledged-before-reset"), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.True(delivery.TryAccept(Message("pending-after-reset"), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Single(transport.Rejections, r => r == TranscriptRejection.EpochReset);
        var sessions = await store.ListSessionsAsync(config.MachineId);
        var page = await store.ReadEventsAsync(Assert.Single(sessions.Sessions).Selection);
        Assert.Contains(page.Events, e => e.Event.Payload is TranscriptGap { Reason: TranscriptGapReason.ReceiverReset });
        Assert.Single(page.Events, e => e.Event.Payload is TranscriptMessage { Text: "pending-after-reset" });
        Assert.DoesNotContain(page.Events, e => e.Event.Payload is TranscriptMessage { Text: "acknowledged-before-reset" });
        Assert.Single(transport.Attempts, e => e.Payload is TranscriptMessage { Text: "acknowledged-before-reset" });
        var pending = transport.Attempts.Where(e => e.Payload is TranscriptMessage { Text: "pending-after-reset" }).ToArray();
        Assert.Equal(2, pending.Length);
        Assert.Equal(pending[0].EventId, pending[1].EventId);
        Assert.Equal(pending[0].Provenance.AcceptedAtUtc, pending[1].Provenance.AcceptedAtUtc);
        Assert.NotEqual(pending[0].ReceiverEpoch, pending[1].ReceiverEpoch);
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Theory]
    [InlineData("x", 40960)]
    [InlineData("\"", 50000)]
    public async Task LargeCompleteAssistantReplyIsTruncatedAndAcceptedByTheRealReceiver(string unit, int repetitions)
    {
        var config = Configuration();
        using var store = new TranscriptStore((machine, generation, _) =>
            ValueTask.FromResult(machine == config.MachineId && generation == 17));
        store.SetReadiness(true);
        var transport = new StoreTransport(store);
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery, config);
        var text = string.Concat(Enumerable.Repeat(unit, repetitions));
        var projected = Message(text);
        projected = projected with
        {
            Provenance = projected.Provenance with
            {
                CaptureOrigin = CaptureOrigin.TranscriptFile, TriggeringCaptureId = Guid.NewGuid(),
                FormatProfileId = "synthetic-file-v1"
            },
            Payload = new TranscriptMessage(TranscriptRole.Assistant, "native-reply",
                TranscriptMessageIdOrigin.Host, text)
        };
        Assert.True(delivery.TryAccept(projected, 1));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Empty(transport.Rejections);
        var sessions = await store.ListSessionsAsync(config.MachineId);
        var page = await store.ReadEventsAsync(Assert.Single(sessions.Sessions).Selection);
        var item = Assert.Single(page.Events, e => e.Event.Payload is TranscriptMessage);
        var reply = Assert.IsType<TranscriptMessage>(item.Event.Payload);
        Assert.Equal(TranscriptRole.Assistant, reply.Role);
        Assert.Equal(TranscriptMessageAvailability.Complete, reply.Availability);
        Assert.True(reply.Truncated);
        Assert.Equal(TranscriptTruncationReason.SerializedLimit, reply.TruncationReason);
        Assert.StartsWith(reply.Text, text, StringComparison.Ordinal);
        Assert.InRange(item.SerializedBytes, 1, TranscriptProtocol.MaxEventBytes);
    }

    private static RemoteConfiguration Configuration() => new()
    {
        Version = 5, DashboardBaseUrl = "https://synthetic.invalid/", MachineId = Guid.NewGuid(),
        MachineName = "synthetic", ClientVersion = "test"
    };

    private static TranscriptEvent Message(string text) => new()
    {
        Source = Source, SessionId = "synthetic-session",
        Provenance = new("userPromptSubmitted", "test-v1", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, TranscriptOrdering.Arrival, CaptureOrigin.Hook, "test-hook-v1"),
        Payload = new TranscriptMessage(TranscriptRole.User, Guid.NewGuid().ToString("N"),
            TranscriptMessageIdOrigin.Local, text)
    };

    private static async Task Ready(TranscriptDeliveryCoordinator delivery, RemoteConfiguration config)
    {
        delivery.Configure(config, 17, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.IsReady);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class StoreTransport(TranscriptStore store) : ITranscriptTransport
    {
        private int _active;
        public Func<TranscriptEvent, TranscriptAcknowledgement, CancellationToken, Task<TranscriptTransportResult?>>?
            AfterAccept { get; init; }
        public Action<TranscriptEvent>? BeforeAccept { get; init; }
        public ConcurrentQueue<(TranscriptEvent Event, TranscriptAcknowledgement Acknowledgement)> Receipts { get; } = new();
        public ConcurrentQueue<TranscriptEvent> Attempts { get; } = new();
        public ConcurrentQueue<TranscriptRejection> Rejections { get; } = new();
        public int MaximumConcurrent { get; private set; }

        public async Task<TranscriptTransportResult> SendAsync(Uri baseUri, TranscriptOperation operation,
            ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _active));
            try
            {
                switch (operation)
                {
                    case TranscriptOperation.Capabilities: return Reply(200, store.GetCapabilities());
                    case TranscriptOperation.Open:
                        return Reply(200, await store.OpenAsync(Read<TranscriptOpenRequest>(body), cancellationToken));
                    case TranscriptOperation.Close: return Reply(200, store.Close(Read<TranscriptCloseRequest>(body)));
                    case TranscriptOperation.Event:
                        var value = Read<TranscriptEvent>(body);
                        Attempts.Enqueue(value);
                        BeforeAccept?.Invoke(value);
                        var acknowledgement = store.Accept(value);
                        Receipts.Enqueue((value, acknowledgement));
                        return (AfterAccept is null ? null : await AfterAccept(value, acknowledgement, cancellationToken)) ??
                            Reply(202, acknowledgement);
                    default: throw new InvalidOperationException();
                }
            }
            catch (TranscriptRejectedException exception)
            {
                Rejections.Enqueue(exception.Category);
                return Reply(409, new TranscriptError(exception.Category, store.ReceiverEpoch, store.OpenRevision));
            }
            finally { Interlocked.Decrement(ref _active); }
        }

        private static T Read<T>(ReadOnlyMemory<byte> bytes) =>
            JsonSerializer.Deserialize<T>(bytes.Span, TranscriptProtocol.Json)!;
        private static TranscriptTransportResult Reply<T>(int status, T value) =>
            new(status, JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json));
        public void Dispose() { }
    }
}
