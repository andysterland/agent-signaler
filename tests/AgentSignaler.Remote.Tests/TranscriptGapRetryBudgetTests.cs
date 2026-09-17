using System.Collections.Concurrent;
using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class TranscriptGapRetryBudgetTests
{
    [Fact]
    public async Task OverflowDuringSendAndBackoffPreservesGapRetryAfterAttemptsAndOriginalAcceptance()
    {
        var clock = new ManualClock();
        var transport = new GapTransport();
        var scheduledGapAttempts = 0;
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock, () =>
        {
            // Reading Status after this callback acquires the lock protecting the assigned retry deadline.
            Volatile.Write(ref scheduledGapAttempts, transport.Gaps.Count);
            return 0.5;
        });
        delivery.Configure(new()
        {
            Version = 5, DashboardBaseUrl = "https://synthetic.invalid/", MachineId = Guid.NewGuid(),
            MachineName = "synthetic", ClientVersion = "test"
        }, 1, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.IsReady);
        Assert.True(delivery.TryAccept(Message(), 1));
        await transport.MessageEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            for (var index = 0; index < 70; index++) Assert.True(delivery.TryAccept(Message(), 1));
            transport.ReleaseMessage.TrySetResult();
            await transport.GapEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var index = 0; index < 5; index++) Assert.True(delivery.TryAccept(Message(), 1));
            transport.ReleaseGap.TrySetResult();
            await Eventually(() => Volatile.Read(ref scheduledGapAttempts) == 1 && delivery.Status == "retrying");
            for (var index = 0; index < 5; index++) Assert.True(delivery.TryAccept(Message(), 1));
            clock.Advance(TimeSpan.FromSeconds(9));
            await Task.Delay(300);
            Assert.Single(transport.Gaps);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Eventually(() => Volatile.Read(ref scheduledGapAttempts) == 2 && delivery.Status == "retrying");
            for (var expected = 3; expected <= 8; expected++)
            {
                for (var index = 0; index < 3; index++) Assert.True(delivery.TryAccept(Message(), 1));
                clock.Advance(TimeSpan.FromSeconds(expected > 5 ? 15 : 10));
                var count = expected;
                await Eventually(() => Volatile.Read(ref scheduledGapAttempts) == count && delivery.Status == "retrying");
            }
            for (var index = 0; index < 3; index++) Assert.True(delivery.TryAccept(Message(), 1));
            clock.Advance(TimeSpan.FromSeconds(15));
            await Eventually(() => delivery.PendingEvents == 0);
            Assert.Equal(8, transport.Gaps.Count);
            var originalAcceptance = transport.Gaps.First().Provenance.AcceptedAtUtc;
            Assert.All(transport.Gaps, gap => Assert.Equal(originalAcceptance, gap.Provenance.AcceptedAtUtc));
            Assert.True(transport.Gaps.Last().Sequence > transport.Gaps.First().Sequence);
        }
        finally
        {
            transport.ReleaseMessage.TrySetResult();
            transport.ReleaseGap.TrySetResult();
        }
    }

    private static TranscriptEvent Message() => new()
    {
        Source = new("copilot-cli", "synthetic-gap-budget", "test"),
        SessionId = "synthetic-session",
        Provenance = new("userPromptSubmitted", "test-v1", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, TranscriptOrdering.Arrival, CaptureOrigin.Hook, "test-hook-v1"),
        Payload = new TranscriptMessage(TranscriptRole.User, Guid.NewGuid().ToString("N"),
            TranscriptMessageIdOrigin.Local, "synthetic message")
    };

    private sealed class GapTransport : ITranscriptTransport
    {
        private readonly Guid _epoch = Guid.NewGuid();
        private long _revision;
        public TaskCompletionSource MessageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMessage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource GapEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseGap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<TranscriptEvent> Gaps { get; } = new();

        public async Task<TranscriptTransportResult> SendAsync(Uri baseUri, TranscriptOperation operation,
            ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            switch (operation)
            {
                case TranscriptOperation.Capabilities:
                    return Reply(200, new TranscriptCapabilitiesResponse(1, _epoch, _revision, true, true,
                        TranscriptProtocol.Limits, TranscriptProtocol.ImplementedCapabilities));
                case TranscriptOperation.Open:
                    var open = Read<TranscriptOpenRequest>(body);
                    return Reply(200, new TranscriptOpenResponse(_epoch, open.StreamId, ++_revision, false));
                case TranscriptOperation.Close:
                    var close = Read<TranscriptCloseRequest>(body);
                    return Reply(200, new TranscriptCloseResponse(_epoch, close.StreamId, ++_revision, false));
                case TranscriptOperation.Event:
                    var value = Read<TranscriptEvent>(body);
                    if (value is { Sequence: 2, Payload: TranscriptMessage })
                    {
                        MessageEntered.TrySetResult();
                        await ReleaseMessage.Task.WaitAsync(cancellationToken);
                    }
                    if (value.Payload is TranscriptGap { FromSequence: not null })
                    {
                        Gaps.Enqueue(value);
                        if (Gaps.Count == 1)
                        {
                            GapEntered.TrySetResult();
                            await ReleaseGap.Task.WaitAsync(cancellationToken);
                        }
                        return new(429, default, RetryAfter: TimeSpan.FromSeconds(10));
                    }
                    return Reply(202, new TranscriptAcknowledgement(_epoch, value.StreamId,
                        value.Sequence, TranscriptDisposition.Accepted));
                default: throw new InvalidOperationException();
            }
        }

        private static T Read<T>(ReadOnlyMemory<byte> bytes) =>
            JsonSerializer.Deserialize<T>(bytes.Span, TranscriptProtocol.Json)!;
        private static TranscriptTransportResult Reply<T>(int status, T value) =>
            new(status, JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json));
        public void Dispose() { }
    }
}
