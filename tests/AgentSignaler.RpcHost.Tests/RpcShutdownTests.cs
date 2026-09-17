using AgentSignaler.Dashboard;
using AgentSignaler.RpcHost;
using Xunit;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcShutdownTests
{
    [Fact]
    public async Task DelayedBatchAcknowledgementCannotConsumeAwaitedNativeCleanupReserve()
    {
        var clock = new ManualClock();
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reserveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeStarted = false;
        var reserveCompleted = false;
        var admissionStopped = false;
        async Task<RuntimeShutdownResult> StopRuntime(CancellationToken token)
        {
            runtimeStarted = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            var reserve = Task.Delay(TimeSpan.FromSeconds(5), clock);
            reserveStarted.TrySetResult();
            await reserve;
            reserveCompleted = true;
            return new(false);
        }
        var shutdown = new RpcShutdown(StopRuntime, () => admissionStopped = true,
            () => { transportStopped.TrySetResult(); return Task.CompletedTask; }, clock);
        shutdown.Request(acknowledgement.Task);
        Assert.True(runtimeStarted);
        Assert.True(admissionStopped);
        Assert.False(transportStopped.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(9));
        shutdown.Request();
        Assert.False(shutdown.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await transportStopped.Task.WaitAsync(TestContext.Token);
        Assert.False(shutdown.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        await reserveStarted.Task.WaitAsync(TestContext.Token);
        Assert.False(shutdown.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.False(reserveCompleted);
        Assert.False(shutdown.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await shutdown.Completion.WaitAsync(TestContext.Token));
        Assert.True(reserveCompleted);
        Assert.Equal(TimeSpan.FromSeconds(25), clock.Elapsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledAcknowledgementStillAwaitsBothCleanupComponents(bool cancelled)
    {
        var runtime = new TaskCompletionSource<RuntimeShutdownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = new List<string>();
        var shutdown = new RpcShutdown(_ => { attempts.Add("runtime"); return runtime.Task; },
            () => attempts.Add("admission"), () => { attempts.Add("transport"); return transport.Task; });
        shutdown.Request(cancelled ? Task.FromCanceled(new CancellationToken(true)) : Task.FromException(new IOException()));
        Assert.Equal(["admission", "runtime", "transport"], attempts);
        Assert.False(shutdown.Completion.IsCompleted);
        runtime.TrySetResult(new(true));
        Assert.False(shutdown.Completion.IsCompleted);
        transport.TrySetResult();
        Assert.False(await shutdown.Completion.WaitAsync(TestContext.Token));
    }

    [Fact]
    public async Task RuntimeCleanupStartsImmediatelyButTransportWaitsForSuccessfulAcknowledgement()
    {
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeCalls = 0;
        var transportCalls = 0;
        var shutdown = new RpcShutdown(_ =>
        {
            runtimeCalls++;
            return Task.FromResult(new RuntimeShutdownResult(true));
        }, () => { }, () => { transportCalls++; return Task.CompletedTask; });
        shutdown.Request(acknowledgement.Task);
        shutdown.Request();
        Assert.Equal(1, runtimeCalls);
        Assert.Equal(0, transportCalls);
        acknowledgement.TrySetResult();
        Assert.True(await shutdown.Completion.WaitAsync(TestContext.Token));
        Assert.Equal(1, transportCalls);
    }

    [Fact]
    public async Task TransportFailureCannotAbandonAwaitedRuntimeCleanup()
    {
        var runtime = new TaskCompletionSource<RuntimeShutdownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new RpcShutdown(_ => runtime.Task, () => { }, () => throw new IOException());
        shutdown.Request();
        Assert.False(shutdown.Completion.IsCompleted);
        runtime.TrySetResult(new(true));
        Assert.False(await shutdown.Completion.WaitAsync(TestContext.Token));
    }

    [Fact]
    public async Task OverallThirtySecondDeadlineCannotBeRestartedByRepeatedShutdown()
    {
        var clock = new ManualClock();
        var runtime = new TaskCompletionSource<RuntimeShutdownResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdown = new RpcShutdown(_ => runtime.Task, () => { }, () => transport.Task, clock);
        shutdown.Request();
        clock.Advance(TimeSpan.FromSeconds(29));
        shutdown.Request();
        Assert.False(shutdown.Completion.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await shutdown.Completion.WaitAsync(TestContext.Token));
        runtime.TrySetResult(new(false));
        transport.TrySetResult();
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<ManualTimer> timers = [];
        private long ticks;
        public TimeSpan Elapsed { get { lock (sync) return TimeSpan.FromTicks(ticks); } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + Elapsed;
        public override long GetTimestamp() => Elapsed.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (sync)
            {
                var timer = new ManualTimer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] pending;
            lock (sync) { ticks += elapsed.Ticks; pending = timers.ToArray(); }
            foreach (var timer in pending) timer.Fire();
        }
        private sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state) : ITimer
        {
            private long next = long.MaxValue;
            private long period;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan interval)
            {
                lock (owner.sync)
                {
                    if (disposed) return false;
                    next = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner.ticks + dueTime.Ticks;
                    period = interval == Timeout.InfiniteTimeSpan ? 0 : interval.Ticks;
                    return true;
                }
            }
            public void Fire()
            {
                lock (owner.sync)
                {
                    if (disposed || next > owner.ticks) return;
                    next = period > 0 ? owner.ticks + period : long.MaxValue;
                }
                callback(state);
            }
            public void Dispose()
            {
                lock (owner.sync) { disposed = true; owner.timers.Remove(this); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
