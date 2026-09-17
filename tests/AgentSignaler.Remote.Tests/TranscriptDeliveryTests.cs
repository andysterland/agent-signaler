using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class TranscriptDeliveryTests
{
    private static readonly SourceDescriptor Source = new("copilot-cli", "synthetic", "test");

    [Fact]
    public async Task V5RequiresAcknowledgedStartedAndNegotiationBeforeAdmission()
    {
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        delivery.Configure(Configuration(), 7, 1);
        Assert.False(delivery.TryAccept(Message(), 1));
        Assert.Empty(transport.Calls);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.IsReady);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => transport.Events.Any(e => e.Payload is TranscriptMessage));
        Assert.Equal([TranscriptOperation.Capabilities, TranscriptOperation.Open,
            TranscriptOperation.Event, TranscriptOperation.Event], transport.Calls.Take(4).Select(c => c.Operation));
        var events = transport.Events;
        Assert.IsType<TranscriptGap>(events[0].Payload);
        Assert.Equal(TranscriptGapReason.CaptureStarted, ((TranscriptGap)events[0].Payload).Reason);
        Assert.Equal(1, events[0].Sequence);
        Assert.Equal(2, events[1].Sequence);
        Assert.Equal(7, events[1].Generation);
        Assert.Equal(1, transport.MaximumConcurrent);
        Assert.All(transport.Calls, c => Assert.Equal("synthetic.invalid", c.Endpoint.Host));
    }

    [Fact]
    public async Task RepeatedStartedAcknowledgementsDoNotRenegotiateOrRenewTheLease()
    {
        var clock = new ManualClock();
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock);
        await Ready(delivery);
        clock.Advance(TimeSpan.FromSeconds(40));
        for (var index = 0; index < 10; index++) delivery.StartedAcknowledged();
        await Task.Delay(300);
        Assert.Single(transport.Calls, c => c.Operation == TranscriptOperation.Capabilities);
        delivery.Suspend("incompatible-receiver");
        delivery.StartedAcknowledged();
        Assert.False(delivery.IsReady);
        Assert.Equal("incompatible-receiver", delivery.Status);
    }

    [Fact]
    public async Task VisualStudioOnlyConfigurationNegotiatesWithTheSharedHookSourceVersion()
    {
        const string scope = "synthetic-visual-studio";
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport,
            (source, revision) => revision == 1 && source.Kind == "visual-studio" &&
                source.ScopeId == scope && source.Version == "shared");
        delivery.Configure(Configuration() with
        {
            Integrations =
            [
                new IntegrationTarget
                {
                    Id = "synthetic-vs", Kind = "visual-studio", ScopeId = scope,
                    HostVersion = "17.14.11", DisplayName = "Synthetic Visual Studio",
                    InstallationId = "synthetic-installation",
                    HookDirectory = Path.Combine(Directory.GetCurrentDirectory(), "synthetic-vs-hooks"),
                    Capability = IntegrationCapability.Configured,
                    SupportedEvents = HookAdapters.Events("visual-studio"),
                    Provenance = "synthetic-test-only"
                }
            ]
        }, 1, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.IsReady);
        Assert.Single(transport.Calls, call => call.Operation == TranscriptOperation.Capabilities);
        Assert.True(delivery.TryAccept(Message() with { Source = new("visual-studio", scope, "shared") }, 1));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Contains(transport.Events, value => value.Payload is TranscriptMessage);
        Assert.All(transport.Events, value => Assert.Equal("shared", value.Source.Version));
    }

    [Theory]
    [InlineData(4, true, "https://synthetic.invalid/", "legacy-status-only")]
    [InlineData(5, false, "https://synthetic.invalid/", "sharing-disabled")]
    [InlineData(5, true, "http://synthetic.invalid:5000/", "https-required")]
    public async Task LegacyOptOutAndHttpRemainStatusOnly(int version, bool enabled, string endpoint, string category)
    {
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        delivery.Configure(Configuration() with
        { Version = version, DetailedReportingEnabled = enabled, DashboardBaseUrl = endpoint }, 1, 1);
        delivery.StartedAcknowledged();
        Assert.False(delivery.IsReady);
        Assert.False(delivery.TryAccept(Message(), 1));
        Assert.Equal(category, delivery.Status);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task RetryKeepsIdenticalUtf8EventIdentitySequenceAndAcceptance()
    {
        var clock = new ManualClock();
        var attempts = 0;
        var transport = new FakeTransport
        {
            EventReply = (value, _) => Task.FromResult<TranscriptTransportResult?>(value.Payload is TranscriptMessage &&
                Interlocked.Increment(ref attempts) == 1 ? new(429, default, RetryAfter: TimeSpan.FromSeconds(10)) : null)
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock, () => 0.5);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => attempts == 1);
        clock.Advance(TimeSpan.FromSeconds(9));
        await Task.Delay(300);
        Assert.Equal(1, attempts);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => attempts == 2 && delivery.PendingEvents == 0);
        var sent = transport.Calls.Where(c => c.Operation == TranscriptOperation.Event &&
            Read<TranscriptEvent>(c.Bytes).Payload is TranscriptMessage).ToArray();
        Assert.Equal(sent[0].Bytes, sent[1].Bytes);
    }

    [Fact]
    public async Task RetryAfterBeyondOriginalLifetimeDropsContentAndSendsKnownGap()
    {
        var transport = new FakeTransport
        {
            EventReply = (value, _) => Task.FromResult<TranscriptTransportResult?>(value.Payload is TranscriptMessage
                ? new(429, default, RetryAfter: TimeSpan.FromMinutes(3)) : null)
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => transport.Events.Any(e => e.Payload is TranscriptGap { Reason: TranscriptGapReason.Expired }));
        Assert.Single(transport.Events, e => e.Payload is TranscriptMessage);
        var gap = Assert.IsType<TranscriptGap>(transport.Events.Last().Payload);
        Assert.Equal(2, gap.FromSequence);
        Assert.Equal(2, gap.ThroughSequence);
        Assert.Equal(0, delivery.PendingEvents);
    }

    [Fact]
    public async Task EightFailedAttemptsExhaustOneOriginalDeliveryWithoutResettingItsId()
    {
        var clock = new ManualClock();
        var transport = new FakeTransport
        {
            EventReply = (value, _) => Task.FromResult<TranscriptTransportResult?>(value.Payload is TranscriptMessage
                ? new(500, default) : null)
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock, () => 0.5);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        var delays = new[] { 1, 2, 4, 8, 15, 15, 15 };
        for (var attempt = 1; attempt <= 7; attempt++)
        {
            var expected = attempt;
            await Eventually(() => transport.Events.Count(e => e.Payload is TranscriptMessage) == expected &&
                delivery.Status == "retrying");
            clock.Advance(TimeSpan.FromSeconds(delays[attempt - 1]));
        }
        await Eventually(() => transport.Events.Count(e => e.Payload is TranscriptMessage) == 8 &&
            delivery.PendingEvents == 0 && transport.Events.Any(e => e.Payload is TranscriptGap
                { Reason: TranscriptGapReason.Expired }));
        Assert.Single(transport.Events.Where(e => e.Payload is TranscriptMessage).Select(e => e.EventId).Distinct());
        Assert.Contains(transport.Events, e => e.Payload is TranscriptGap { Reason: TranscriptGapReason.Expired });
    }

    [Fact]
    public async Task RequestTimeoutCancelsAtOnePointFiveSecondsAndRetainsOriginalEventForRetry()
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, token) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        Assert.False(canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Eventually(() => delivery.Status == "retrying");
        Assert.Equal(1, delivery.PendingEvents);
    }

    [Fact]
    public async Task UncooperativeDeadlinePausesAdmissionAndPreventsOverlappingRetriesUntilActualUnwind()
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, _) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var charge = delivery.AccountedBytes;
            clock.Advance(TimeSpan.FromSeconds(1.5));
            await Eventually(() => delivery.Status == "request-cleanup-pending");
            Assert.False(delivery.IsReady);
            Assert.False(delivery.TryAccept(Message("not-admitted"), 1));
            Assert.Equal(charge, delivery.AccountedBytes);
            clock.Advance(TimeSpan.FromSeconds(15));
            await Task.Delay(300);
            Assert.Single(transport.Events, value => value.Payload is TranscriptMessage);
            Assert.Equal(1, transport.MaximumConcurrent);
            await delivery.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.True(transport.Disposed);
            Assert.Equal("shutdown-cleanup-pending", delivery.Status);
            Assert.Equal(1, delivery.PendingEvents);
            Assert.Equal(charge, delivery.AccountedBytes);
            release.TrySetResult();
            await Eventually(() => delivery.PendingEvents == 0);
            Assert.Equal("stopped", delivery.Status);
            Assert.Equal(0, delivery.AccountedBytes);
            Assert.Single(transport.Events, value => value.Payload is TranscriptMessage);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task TerminalShutdownDisposesTransportToAbortANonCooperativeRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            OnDispose = () => aborted.TrySetCanceled(),
            EventReply = async (value, _) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    await aborted.Task;
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await delivery.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(transport.Disposed);
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Equal(0, delivery.AccountedBytes);
        Assert.False(delivery.TryAccept(Message(), 1));
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task PerSourceEntryOverflowRetainsInflightAndCoalescesLostPrefix()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, token) =>
            {
                if (value.Sequence == 2)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            for (var index = 0; index < 70; index++) Assert.True(delivery.TryAccept(Message(), 1));
            Assert.Equal(64, delivery.PendingEvents);
            Assert.InRange(delivery.AccountedBytes, 1, TranscriptDeliveryCoordinator.MaximumSourceBytes);
            release.TrySetResult();
            await Eventually(() => delivery.PendingEvents == 0);
            var gap = transport.Events.Single(e => e.Payload is TranscriptGap { Reason: TranscriptGapReason.QueueOverflow });
            Assert.Equal(3, ((TranscriptGap)gap.Payload).FromSequence);
            Assert.Equal(9, ((TranscriptGap)gap.Payload).ThroughSequence);
            Assert.Contains(transport.Events, e => e.Sequence == 2 && e.Payload is TranscriptMessage);
            Assert.Equal(1, transport.MaximumConcurrent);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverflowGapNeverJumpsOverAnEarlierInflightEventWhoseAcknowledgementFailed(bool expire)
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var transport = new FakeTransport
        {
            EventReply = async (value, token) =>
            {
                if (value.Sequence == 2 && Interlocked.Increment(ref attempts) == 1)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return new(429, default, RetryAfter: expire ? TimeSpan.FromMinutes(3) : null);
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock, () => 0.5);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            for (var index = 0; index < 70; index++) Assert.True(delivery.TryAccept(Message(), 1));
            release.TrySetResult();
            if (!expire)
            {
                await Eventually(() => delivery.Status == "retrying");
                Assert.DoesNotContain(transport.Events, e => e.Payload is TranscriptGap { Reason: TranscriptGapReason.QueueOverflow });
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            await Eventually(() => delivery.PendingEvents == 0);
            var events = transport.Events;
            if (expire)
            {
                Assert.Equal([1L, 2L, 9L], events.Take(3).Select(e => e.Sequence));
                Assert.Equal(2, Assert.IsType<TranscriptGap>(events[2].Payload).FromSequence);
            }
            else
            {
                Assert.Equal([1L, 2L, 2L, 9L], events.Take(4).Select(e => e.Sequence));
                Assert.IsType<TranscriptMessage>(events[2].Payload);
                Assert.IsType<TranscriptGap>(events[3].Payload);
            }
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(20)]
    [InlineData(29000)]
    public async Task AccountedByteAndGlobalCountLimitsIncludeTheInflightOwner(int textLength)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, token) =>
            {
                if (value.Payload is TranscriptMessage && !entered.Task.IsCompleted)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(new string('x', textLength)), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var firstCharge = delivery.AccountedBytes;
            Assert.Equal(0, firstCharge % 256);
            for (var source = 0; source < 4; source++)
                for (var index = 0; index < 64; index++)
                    Assert.True(delivery.TryAccept(Message(new string('x', textLength)) with
                    { Source = Source with { ScopeId = $"scope-{source}" } }, 1));
            Assert.InRange(delivery.AccountedBytes, firstCharge, TranscriptDeliveryCoordinator.MaximumEventBytes);
            Assert.InRange(delivery.PendingEvents, 1, TranscriptDeliveryCoordinator.MaximumEvents);
            if (textLength == 20) Assert.Equal(TranscriptDeliveryCoordinator.MaximumEvents, delivery.PendingEvents);
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task StreamMetadataSlotsAreBoundedAndMalformedAdmissionDoesNotCreateStreams()
    {
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        for (var index = 0; index < 20; index++)
            Assert.False(delivery.TryAccept(Message() with
            { Source = Source with { ScopeId = $"invalid-{index}" }, SessionId = "" }, 1));
        for (var index = 0; index < 16; index++)
            Assert.True(delivery.TryAccept(Message() with { Source = Source with { ScopeId = $"valid-{index}" } }, 1));
        Assert.False(delivery.TryAccept(Message() with { Source = Source with { ScopeId = "seventeenth" } }, 1));
    }

    [Fact]
    public async Task SuspendCancelsInflightClearsOwnedTextAndRejectsStaleRevision()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, token) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        delivery.Suspend("sharing-disabled");
        Assert.False(delivery.TryAccept(Message(), 1));
        Assert.InRange(delivery.PendingEvents, 0, 1);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Equal(0, delivery.AccountedBytes);
        await Eventually(() => transport.Calls.Any(c => c.Operation == TranscriptOperation.Close));
        Assert.True(Read<TranscriptCloseRequest>(transport.Calls.Last().Bytes).Purge);
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task EndpointChangeNeverCarriesOldPendingContentToNewDestination()
    {
        var transport = new FakeTransport
        {
            EventReply = (value, _) => Task.FromResult<TranscriptTransportResult?>(value.Payload is TranscriptMessage
                ? new(429, default, RetryAfter: TimeSpan.FromMinutes(1)) : null)
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message("old-content-marker"), 1));
        await Eventually(() => transport.Events.Any(e => e.Payload is TranscriptMessage));
        delivery.Configure(Configuration() with { DashboardBaseUrl = "https://other.invalid/" }, 1, 2);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.IsReady);
        Assert.False(delivery.TryAccept(Message(), 1));
        Assert.Equal(0, delivery.PendingEvents);
        Assert.DoesNotContain(transport.Calls, c => c.Endpoint.Host == "other.invalid" &&
            Encoding.UTF8.GetString(c.Bytes).Contains("old-content-marker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EligibilityIsRecheckedAfterAdmissionBeforeEveryContentSend()
    {
        var eligible = true;
        var transport = new FakeTransport
        {
            OpenReply = (_, _) =>
            {
                eligible = false;
                return Task.FromResult<TranscriptTransportResult?>(null);
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => eligible);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => delivery.Status == "source-ineligible");
        Assert.Empty(transport.Events);
        Assert.Equal(0, delivery.PendingEvents);
    }

    [Fact]
    public async Task LeaseExpiryStopsAdmissionAndInvalidatesReaderContexts()
    {
        var clock = new ManualClock();
        var calls = 0;
        var transport = new FakeTransport
        {
            CapabilitiesReply = () => Interlocked.Increment(ref calls) > 1
                ? new(429, default, RetryAfter: TimeSpan.FromMinutes(1)) : null
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock);
        var invalidations = new ConcurrentQueue<string>();
        delivery.Invalidated += (_, reason) => invalidations.Enqueue(reason);
        await Ready(delivery);
        clock.Advance(TimeSpan.FromSeconds(45));
        await Eventually(() => calls >= 2);
        Assert.True(delivery.IsReady);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.False(delivery.IsReady);
        Assert.False(delivery.TryAccept(Message(), 1));
        await Eventually(() => invalidations.Contains("capability-expired"));
        Assert.Equal(0, delivery.AccountedBytes);
    }

    [Fact]
    public async Task LeaseTimerCancelsAnAlreadyInflightContentRequestImmediately()
    {
        var clock = new ManualClock();
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            CapabilitiesReply = () => Interlocked.Increment(ref calls) > 1
                ? new(429, default, RetryAfter: TimeSpan.FromMinutes(1)) : null,
            EventReply = async (value, token) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                    catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock);
        await Ready(delivery);
        clock.Advance(TimeSpan.FromSeconds(45));
        await Eventually(() => calls == 2);
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(1));
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.False(delivery.IsReady);
        Assert.Equal(0, delivery.PendingEvents);
        Assert.Equal("capability-expired", delivery.Status);
    }

    [Theory]
    [InlineData("tls-validation-failed")]
    [InlineData("redirect-refused")]
    [InlineData("invalid-protocol")]
    public async Task FatalTransportRepliesSuspendWithoutRetry(string failure)
    {
        var transport = new FakeTransport { CapabilitiesReply = () => new(0, default, failure) };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        delivery.Configure(Configuration(), 1, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.Status == failure);
        Assert.False(delivery.IsReady);
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task EpochResetReassignsOnlyPendingEventsPreservingIdsAndOriginalAcceptance()
    {
        var transport = new FakeTransport();
        var reset = false;
        transport.EventReply = (value, _) =>
        {
            if (value.Payload is TranscriptMessage { Text: "pending" } && !reset)
            {
                reset = true;
                transport.Epoch = Guid.NewGuid();
                transport.OpenRevision = 0;
                return Task.FromResult<TranscriptTransportResult?>(Reply(409,
                    new TranscriptError(TranscriptRejection.EpochReset, transport.Epoch, 0)));
            }
            return Task.FromResult<TranscriptTransportResult?>(null);
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message("acknowledged"), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.True(delivery.TryAccept(Message("pending"), 1));
        await Eventually(() => transport.Events.Count(e => e.Payload is TranscriptMessage { Text: "pending" }) == 2);
        var messages = transport.Events.Where(e => e.Payload is TranscriptMessage { Text: "pending" }).ToArray();
        Assert.Equal(messages[0].EventId, messages[1].EventId);
        Assert.Equal(messages[0].Provenance.AcceptedAtUtc, messages[1].Provenance.AcceptedAtUtc);
        Assert.NotEqual(messages[0].StreamId, messages[1].StreamId);
        Assert.NotEqual(messages[0].ReceiverEpoch, messages[1].ReceiverEpoch);
        Assert.Single(transport.Events, e => e.Payload is TranscriptMessage { Text: "acknowledged" });
        Assert.Contains(transport.Events, e => e.Payload is TranscriptGap { Reason: TranscriptGapReason.ReceiverReset });
    }

    [Theory]
    [InlineData(TranscriptRejection.RetiredStream)]
    [InlineData(TranscriptRejection.SequenceConflict)]
    public async Task RetiredAndConflictingStreamsPurgePendingAndInvalidateReaders(TranscriptRejection reason)
    {
        var transport = new FakeTransport();
        transport.EventReply = (value, _) => Task.FromResult<TranscriptTransportResult?>(value.Payload is TranscriptMessage
            ? Reply(409, new TranscriptError(reason, transport.Epoch, transport.OpenRevision)) : null);
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        var invalidations = new ConcurrentQueue<(SourceDescriptor?, string)>();
        delivery.Invalidated += (source, category) => invalidations.Enqueue((source, category));
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => invalidations.Any(i => i.Item1 == Source));
        Assert.Equal(0, delivery.PendingEvents);
        Assert.Single(transport.Events, e => e.Payload is TranscriptMessage);
        transport.EventReply = null;
        await Eventually(() => delivery.IsReady);
        Assert.True(delivery.TryAccept(Message("new-only"), 1));
        await Eventually(() => transport.Events.Any(e => e.Payload is TranscriptMessage { Text: "new-only" }));
        Assert.Equal(2, transport.Calls.Count(c => c.Operation == TranscriptOperation.Open));
        if (reason == TranscriptRejection.SequenceConflict)
            Assert.Contains(transport.Events, e => e.Payload is TranscriptGap { Reason: TranscriptGapReason.Disconnected });
    }

    [Fact]
    public async Task OpenRetryIsByteIdenticalUntilExplicitRevisionConflict()
    {
        var clock = new ManualClock();
        var count = 0;
        var transport = new FakeTransport
        {
            OpenReply = (_, _) => Task.FromResult<TranscriptTransportResult?>(Interlocked.Increment(ref count) == 1
                ? new(408, default) : null)
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock, () => 0.5);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => count == 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => count == 2 && delivery.PendingEvents == 0);
        var opens = transport.Calls.Where(c => c.Operation == TranscriptOperation.Open).ToArray();
        Assert.Equal(opens[0].Bytes, opens[1].Bytes);
    }

    [Fact]
    public async Task OpenRevisionConflictRenegotiatesBeforeOpeningWithNewCompareAndSwap()
    {
        var count = 0;
        var transport = new FakeTransport();
        transport.OpenReply = (_, _) =>
        {
            if (Interlocked.Increment(ref count) != 1) return Task.FromResult<TranscriptTransportResult?>(null);
            transport.OpenRevision = 12;
            return Task.FromResult<TranscriptTransportResult?>(Reply(409,
                new TranscriptError(TranscriptRejection.OpenRevisionConflict, transport.Epoch, 12)));
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        var opens = transport.Calls.Where(c => c.Operation == TranscriptOperation.Open)
            .Select(c => Read<TranscriptOpenRequest>(c.Bytes)).ToArray();
        Assert.Equal(2, opens.Length);
        Assert.Equal(0, opens[0].ExpectedOpenRevision);
        Assert.Equal(12, opens[1].ExpectedOpenRevision);
        Assert.NotEqual(opens[0].OpenId, opens[1].OpenId);
        Assert.Equal(2, transport.Calls.Count(c => c.Operation == TranscriptOperation.Capabilities));
    }

    [Fact]
    public async Task UnknownManagedMachineWaitsForStartedButDoesNotRetainExpiredText()
    {
        var clock = new ManualClock();
        var transport = new FakeTransport();
        transport.OpenReply = (_, _) => Task.FromResult<TranscriptTransportResult?>(Reply(409,
            new TranscriptError(TranscriptRejection.UnknownManagedMachine, transport.Epoch, 0)));
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true, clock);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await Eventually(() => delivery.Status == "waiting-for-presence");
        Assert.False(delivery.IsReady);
        clock.Advance(TimeSpan.FromMinutes(2));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Equal(0, delivery.AccountedBytes);
        Assert.Single(transport.Calls, c => c.Operation == TranscriptOperation.Open);
    }

    [Fact]
    public async Task DuplicateOrUnknownControlPropertiesSuspendWithoutContentAdmission()
    {
        var transport = new FakeTransport
        {
            CapabilitiesReply = () => new(200, Encoding.UTF8.GetBytes(
                """{"protocolVersion":1,"protocolVersion":1,"unexpected":"synthetic"}"""))
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        delivery.Configure(Configuration(), 1, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.Status == "invalid-protocol");
        Assert.False(delivery.TryAccept(Message(), 1));
    }

    [Fact]
    public async Task StrictBoundedSerializationTruncatesEscapingHeavyAndRejectsInvalidUnicodeInput()
    {
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(new string('"', 32700)), 1));
        Assert.False(delivery.TryAccept(Message("\ud800"), 1));
        Assert.True(delivery.TryAccept(Message("fictional.person@example.invalid"), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        Assert.Contains(transport.Events, e => e.Payload is TranscriptMessage
            { Truncated: true, TruncationReason: TranscriptTruncationReason.SerializedLimit });
        Assert.All(transport.Calls.Where(c => c.Operation == TranscriptOperation.Event),
            c => Assert.InRange(c.Bytes.Length, 1, TranscriptProtocol.MaxEventBytes));
    }

    [Fact]
    public async Task OwnedUtf8SerializationPreservesUnicodeEscapesAndFictionalPii()
    {
        const string text = "Synthetic 😀 reply\n\"quoted\" \\ slash\tJosé <test> fictional@example.invalid";
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(text), 1));
        await Eventually(() => transport.Events.Any(e => e.Payload is TranscriptMessage));
        var message = Assert.IsType<TranscriptMessage>(transport.Events.Last().Payload);
        Assert.Equal(text, message.Text);
        Assert.False(message.Truncated);
        var oversized = string.Concat(Enumerable.Repeat("😀", 16000));
        Assert.True(delivery.TryAccept(Message(oversized), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        var truncated = Assert.IsType<TranscriptMessage>(transport.Events.Last().Payload);
        Assert.True(truncated.Truncated);
        Assert.True(char.IsLowSurrogate(truncated.Text[^1]));
        Assert.True(TranscriptProtocol.Validate(transport.Events.Last()));
    }

    [Theory]
    [InlineData("x", 40960)]
    [InlineData("x", 65536)]
    [InlineData("\"", 50000)]
    [InlineData("😀", 24000)]
    public async Task LargeCompletedAssistantProjectionIsTruncatedBeforeWireValidation(string unit, int repetitions)
    {
        var text = string.Concat(Enumerable.Repeat(unit, repetitions));
        var trigger = Guid.NewGuid();
        var projected = Message(text) with
        {
            Provenance = new("agentStop", "test-v1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                TranscriptOrdering.Arrival, CaptureOrigin.TranscriptFile, "synthetic-file-v1", trigger),
            Payload = new TranscriptMessage(TranscriptRole.Assistant, "native-complete-reply",
                TranscriptMessageIdOrigin.Host, text)
        };
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(projected, 1));
        await Eventually(() => delivery.PendingEvents == 0);
        var sent = Assert.Single(transport.Events, e => e.Payload is TranscriptMessage);
        var reply = Assert.IsType<TranscriptMessage>(sent.Payload);
        Assert.Equal(TranscriptRole.Assistant, reply.Role);
        Assert.Equal(TranscriptMessageAvailability.Complete, reply.Availability);
        Assert.True(reply.Truncated);
        Assert.Equal(TranscriptTruncationReason.SerializedLimit, reply.TruncationReason);
        Assert.StartsWith(reply.Text, text, StringComparison.Ordinal);
        Assert.NotEmpty(reply.Text);
        Assert.Equal("native-complete-reply", reply.MessageId);
        Assert.Equal(trigger, sent.Provenance.TriggeringCaptureId);
        Assert.Equal(CaptureOrigin.TranscriptFile, sent.Provenance.CaptureOrigin);
        Assert.True(TranscriptProtocol.Validate(sent));
        Assert.All(transport.Calls.Where(c => c.Operation == TranscriptOperation.Event),
            c => Assert.InRange(c.Bytes.Length, 1, TranscriptProtocol.MaxEventBytes));
    }

    [Fact]
    public async Task OversizedProjectionPreservesSurrogateBoundariesAndRejectsMalformedDiscardedTail()
    {
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        var boundary = new string('x', 32767) + "😀" + new string('y', 18000);
        Assert.True(delivery.TryAccept(Message(boundary), 1));
        Assert.False(delivery.TryAccept(Message(new string('x', 40000) + "\ud800"), 1));
        Assert.False(delivery.TryAccept(Message(new string('x',
            TranscriptDeliveryCoordinator.MaximumProjectedTextCharacters + 1)), 1));
        await Eventually(() => delivery.PendingEvents == 0);
        var sent = Assert.Single(transport.Events, e => e.Payload is TranscriptMessage);
        Assert.True(TranscriptProtocol.Validate(sent));
        Assert.True(((TranscriptMessage)sent.Payload).Truncated);
        Assert.StartsWith(((TranscriptMessage)sent.Payload).Text, boundary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CapabilityRequestsAlsoRecheckTheCurrentRevisionPredicate()
    {
        var transport = new FakeTransport();
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => false);
        delivery.Configure(Configuration(), 1, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.Status == "source-ineligible");
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task ExitCancelsContentAndCompletesWithinOwnedShutdownBudget()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, token) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return null;
            }
        };
        await using var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await delivery.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(delivery.TryAccept(Message(), 1));
        Assert.Equal(0, delivery.AccountedBytes);
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task RepeatedStopAndDisposeShareOneBudgetWithNonCooperativeTransport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeTransport
        {
            EventReply = async (value, _) =>
            {
                if (value.Payload is TranscriptMessage)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return null;
            }
        };
        var delivery = new TranscriptDeliveryCoordinator(transport, (_, _) => true);
        await Ready(delivery);
        Assert.True(delivery.TryAccept(Message(), 1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            await delivery.StopAsync().WaitAsync(TimeSpan.FromSeconds(4));
            await delivery.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromMilliseconds(400));
            Assert.False(delivery.IsReady);
            Assert.Equal(1, delivery.PendingEvents);
            Assert.True(delivery.AccountedBytes > 0);
            Assert.Single(transport.Events, e => e.Payload is TranscriptMessage);
            release.TrySetResult();
            await Eventually(() => delivery.PendingEvents == 0);
            Assert.Equal(0, delivery.AccountedBytes);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData("http://synthetic.invalid:5000/")]
    [InlineData("https://synthetic.invalid/path")]
    [InlineData("https://user:password@synthetic.invalid/")]
    [InlineData("https://synthetic.invalid/?target=other")]
    public async Task NetworkTransportRejectsNoncanonicalEndpointsBeforeHandler(string endpoint)
    {
        var handler = new SyntheticHandler(_ => throw new InvalidOperationException("No request allowed."));
        using var transport = new TranscriptTransport(handler);
        var response = await transport.SendAsync(new Uri(endpoint), TranscriptOperation.Capabilities,
            "{}"u8.ToArray(), CancellationToken.None);
        Assert.Equal("https-required", response.Failure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NetworkTransportRefusesRedirectAndBoundsChunkedControlResponse()
    {
        var handler = new SyntheticHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/transcripts/v1/capabilities", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://other.invalid/") } };
        });
        using (var transport = new TranscriptTransport(handler))
        {
            var result = await transport.SendAsync(new("https://synthetic.invalid/"), TranscriptOperation.Capabilities,
                "{}"u8.ToArray(), CancellationToken.None);
            Assert.Equal("redirect-refused", result.Failure);
            Assert.Equal(1, handler.Calls);
        }
        var oversized = new SyntheticHandler(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekingStream(new byte[4097]))
            { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
        });
        using var bounded = new TranscriptTransport(oversized);
        Assert.Equal("invalid-protocol", (await bounded.SendAsync(new("https://synthetic.invalid/"),
            TranscriptOperation.Capabilities, "{}"u8.ToArray(), CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task NetworkTransportTreatsTlsFailureAsFatalNotRetriableNetworkLoss()
    {
        var handler = new SyntheticHandler(_ => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError, "synthetic"));
        using var transport = new TranscriptTransport(handler);
        var response = await transport.SendAsync(new("https://synthetic.invalid/"), TranscriptOperation.Capabilities,
            "{}"u8.ToArray(), CancellationToken.None);
        Assert.Equal("tls-validation-failed", response.Failure);
        Assert.False(response.Retryable);
    }

    private static RemoteConfiguration Configuration() => new()
    {
        Version = 5, DashboardBaseUrl = "https://synthetic.invalid/", MachineId = Guid.NewGuid(),
        MachineName = "synthetic", ClientVersion = "test"
    };

    private static TranscriptEvent Message(string text = "synthetic allowed message") => new()
    {
        Source = Source, SessionId = "synthetic-session",
        Provenance = new("userPromptSubmitted", "test-v1", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, TranscriptOrdering.Arrival, CaptureOrigin.Hook, "test-hook-v1"),
        Payload = new TranscriptMessage(TranscriptRole.User, Guid.NewGuid().ToString("N"),
            TranscriptMessageIdOrigin.Local, text)
    };

    private static async Task Ready(TranscriptDeliveryCoordinator delivery)
    {
        delivery.Configure(Configuration(), 1, 1);
        delivery.StartedAcknowledged();
        await Eventually(() => delivery.IsReady);
    }

    private static T Read<T>(byte[] bytes) => JsonSerializer.Deserialize<T>(bytes, TranscriptProtocol.Json)!;
    private static TranscriptTransportResult Reply<T>(int status, T value) =>
        new(status, JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json));

    private sealed class FakeTransport : ITranscriptTransport
    {
        private readonly ConcurrentQueue<Call> _calls = new();
        private readonly ConcurrentQueue<TranscriptEvent> _events = new();
        private int _active;
        public Guid Epoch { get; set; } = Guid.NewGuid();
        public long OpenRevision { get; set; }
        public Func<TranscriptEvent, CancellationToken, Task<TranscriptTransportResult?>>? EventReply { get; set; }
        public Func<TranscriptOpenRequest, CancellationToken, Task<TranscriptTransportResult?>>? OpenReply { get; set; }
        public Func<TranscriptTransportResult?>? CapabilitiesReply { get; init; }
        public IReadOnlyList<Call> Calls => _calls.ToArray();
        public IReadOnlyList<TranscriptEvent> Events => _events.ToArray();
        public int MaximumConcurrent { get; private set; }
        public Action? OnDispose { get; init; }
        public bool Disposed { get; private set; }
        public async Task<TranscriptTransportResult> SendAsync(Uri endpoint, TranscriptOperation operation,
            ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumConcurrent = Math.Max(MaximumConcurrent, Interlocked.Increment(ref _active));
            _calls.Enqueue(new(endpoint, operation, body.ToArray()));
            try
            {
                switch (operation)
                {
                    case TranscriptOperation.Capabilities:
                        return CapabilitiesReply?.Invoke() ?? Reply(200, new TranscriptCapabilitiesResponse(1,
                            Epoch, OpenRevision, true, true, TranscriptProtocol.Limits, TranscriptProtocol.ImplementedCapabilities));
                    case TranscriptOperation.Open:
                        var open = Read<TranscriptOpenRequest>(body.ToArray());
                        var openResult = OpenReply is null ? null : await OpenReply(open, cancellationToken);
                        return openResult ?? Reply(200, new TranscriptOpenResponse(Epoch, open.StreamId, ++OpenRevision, false));
                    case TranscriptOperation.Event:
                        var value = Read<TranscriptEvent>(body.ToArray());
                        _events.Enqueue(value);
                        var result = EventReply is null ? null : await EventReply(value, cancellationToken);
                        return result ?? Reply(202, new TranscriptAcknowledgement(Epoch, value.StreamId,
                            value.Sequence, TranscriptDisposition.Accepted));
                    case TranscriptOperation.Close:
                        var close = Read<TranscriptCloseRequest>(body.ToArray());
                        return Reply(200, new TranscriptCloseResponse(Epoch, close.StreamId, ++OpenRevision, false));
                    default: throw new InvalidOperationException();
                }
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Dispose() { Disposed = true; OnDispose?.Invoke(); }
    }

    private sealed record Call(Uri Endpoint, TranscriptOperation Operation, byte[] Bytes);
    private sealed class SyntheticHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class NonSeekingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
