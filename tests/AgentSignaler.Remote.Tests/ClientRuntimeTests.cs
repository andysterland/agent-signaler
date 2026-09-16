using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");
    private RemoteConfiguration Configuration => new RemoteConfiguration
    {
        Host = "localhost", MachineId = Guid.Parse("0fbe9398-5143-4a06-a8fc-58859492a084"), ClientVersion = "test"
    }.ToVersion3();

    private void Save(RemoteConfiguration? config = null) =>
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config ?? Configuration, Protocol.Json));

    [Fact]
    public async Task StartupIsIdleAndLifetimeTimerDoesNotDependOnHooks()
    {
        Save();
        var clock = new ManualClock();
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.Empty(transport.Reports[0].Sessions!);
        Assert.Equal(PresenceKind.Started, transport.Reports[0].Kind);
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(runtime.AcceptHook(AgentEvent.UserPromptSubmitted, new("session", clock.GetUtcNow(), false)).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Hook));
        await Eventually(() => transport.Reports.Count(r => r.Kind == PresenceKind.Heartbeat) >= 1);
        var before = transport.Reports.Count;
        clock.Advance(TimeSpan.FromMinutes(1));
        await Eventually(() => transport.Reports.Count > before);
        var snapshot = transport.Reports.Last(r => r.Kind == PresenceKind.Heartbeat);
        Assert.Contains(snapshot.Sessions!, s => s.SessionId == "session" && s.UnderlyingState == AgentState.Executing);
        Assert.Equal(1, transport.MaximumConcurrent);
        Assert.All(transport.Reports, report => Assert.Empty(PresenceProtocol.Validate(report)));
    }

    [Fact]
    public async Task StartMustBeAcknowledgedAndRetriesKeepIdentityWhilePendingWorkIsBounded()
    {
        Save();
        var clock = new ManualClock();
        var allow = false;
        var transport = new RecordingTransport((_, _) => Task.FromResult(Volatile.Read(ref allow)));
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => transport.Reports.Count == 1);
        for (var i = 0; i < ClientCoordinator.MaximumPendingHooks; i++)
            Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("session-" + i, clock.GetUtcNow(), false)).Accepted);
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("overflow", clock.GetUtcNow(), false)).Accepted);
        await Task.Delay(30);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Eventually(() => transport.Reports.Count >= 2);
        Assert.All(transport.Reports, r => Assert.Equal(PresenceKind.Started, r.Kind));
        Assert.Single(transport.Reports.Select(r => (r.EventId, r.Sequence)).Distinct());
        Volatile.Write(ref allow, true);
        await Task.Delay(30);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Eventually(() => runtime.Status().State == "connected");
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Heartbeat && r.Sessions!.Count == 64));
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task MissedTimerTicksAreCoalescedAndRestartClearsActiveAssumptions()
    {
        Save();
        var clock = new ManualClock();
        var first = new RecordingTransport();
        long generation;
        await using (var runtime = new ClientCoordinator(ConfigPath, clock, _ => first))
        {
            runtime.Start();
            await Eventually(() => runtime.Status().State == "connected");
            generation = first.Reports[0].Generation;
            runtime.AcceptHook(AgentEvent.SessionStart, new("active", clock.GetUtcNow(), false));
            await Eventually(() => first.Reports.Any(r => r.Kind == PresenceKind.Heartbeat));
            var before = first.Reports.Count;
            clock.Advance(TimeSpan.FromDays(1));
            await Eventually(() => first.Reports.Count > before);
            await Task.Delay(40);
            Assert.Equal(before + 1, first.Reports.Count);
        }
        var second = new RecordingTransport();
        await using var restarted = new ClientCoordinator(ConfigPath, clock, _ => second);
        restarted.Start();
        await Eventually(() => restarted.Status().State == "connected");
        Assert.True(second.Reports[0].Generation > generation);
        Assert.Empty(second.Reports[0].Sessions!);
    }

    [Fact]
    public async Task ExitCancelsInflightUsesFreshTokenAndLeavesOfflineLast()
    {
        Save();
        var clock = new ManualClock();
        var hookStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = false;
        var transport = new RecordingTransport(async (report, token) =>
        {
            if (report.Kind == PresenceKind.Hook)
            {
                hookStarted.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { canceled = true; throw; }
            }
            if (report.Kind == PresenceKind.Offline) Assert.False(token.IsCancellationRequested);
            return true;
        });
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        runtime.AcceptHook(AgentEvent.SessionStart, new("session", clock.GetUtcNow(), false));
        await hookStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopped = runtime.StopAsync();
        Assert.False(runtime.AcceptHook(AgentEvent.SessionEnd, new("session", clock.GetUtcNow(), false)).Accepted);
        Assert.True((await stopped.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
        Assert.True(canceled);
        Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
        var lastCount = transport.Reports.Count;
        clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(30);
        Assert.Equal(lastCount, transport.Reports.Count);
        Assert.Equal(transport.Reports.Count, transport.Reports.Select(r => r.Sequence).Distinct().Count());
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task ReloadRequiresExactRevisionAndIntervalWaitsForAcknowledgement()
    {
        Save();
        var clock = new ManualClock();
        var acknowledge = true;
        var transport = new RecordingTransport((_, _) => Task.FromResult(Volatile.Read(ref acknowledge)));
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var oldRevision = runtime.Status().EffectiveRevision;
        Save(Configuration with { HeartbeatIntervalSeconds = 600 });
        Assert.False((await runtime.ReloadAsync(oldRevision)).Accepted);
        Volatile.Write(ref acknowledge, false);
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        Assert.False((await runtime.ReloadAsync(revision)).Accepted);
        Assert.Equal(oldRevision, runtime.Status().EffectiveRevision);
        Assert.Equal(300, runtime.Status().HeartbeatIntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(5), clock.PeriodicInterval);
        Volatile.Write(ref acknowledge, true);
        await Task.Delay(30);
        clock.Advance(TimeSpan.FromSeconds(5));
        await Eventually(() => runtime.Status().EffectiveRevision == revision);
        Assert.Equal(600, runtime.Status().HeartbeatIntervalSeconds);
        Assert.Equal(TimeSpan.FromMinutes(10), clock.PeriodicInterval);
    }

    [Fact]
    public async Task UrlReloadOfflinesOldEndpointAndStartsNewGenerationBeforeOtherReports()
    {
        Save();
        var transports = new List<RecordingTransport>();
        await using var runtime = new ClientCoordinator(ConfigPath, new ManualClock(), _ =>
        {
            var transport = new RecordingTransport();
            transports.Add(transport);
            return transport;
        });
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Save(Configuration.WithDashboardUrl("http://localhost:51821/").ToVersion3());
        Assert.True((await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath))).Accepted);
        Assert.Equal(2, transports.Count);
        Assert.Equal(PresenceKind.Offline, transports[0].Reports.Last().Kind);
        Assert.True(transports[0].Disposed);
        Assert.Equal(PresenceKind.Started, transports[1].Reports[0].Kind);
        Assert.True(transports[1].Reports[0].Generation > transports[0].Reports[0].Generation);
    }

    [Fact]
    public async Task OwnerLockIsCanonicalAndGenerationCorruptionFailsClosed()
    {
        Save();
        await using (var runtime = new ClientCoordinator(ConfigPath, new ManualClock(), _ => new RecordingTransport()))
        {
            var alias = Path.Combine(_root, "..", Path.GetFileName(_root), "remote.json");
            Assert.Throws<IOException>(() => new ClientCoordinator(alias));
            Assert.Equal(ClientIdentity.PipeName(ConfigPath), ClientIdentity.PipeName(alias));
            Assert.Equal(ClientIdentity.PipeName(ConfigPath), ClientIdentity.PipeName(ConfigPath.ToUpperInvariant()));
        }
        AtomicFile.Write(Path.Combine(_root, "client-generation"), Encoding.UTF8.GetBytes("corrupt"));
        var transport = new RecordingTransport();
        await using var corrupt = new ClientCoordinator(ConfigPath, new ManualClock(), _ => transport);
        Assert.Throws<InvalidDataException>(corrupt.Start);
        Assert.Empty(transport.Reports);
    }

    [Fact]
    public async Task UnreachableOfflineIsBoundedAndDoesNotPreventStop()
    {
        Save();
        var transport = new RecordingTransport(async (report, token) =>
        {
            if (report.Kind == PresenceKind.Offline) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        });
        await using var runtime = new ClientCoordinator(ConfigPath, new ManualClock(), _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var response = await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.Accepted);
        Assert.NotNull(response.Error);
    }

    [Fact]
    public async Task ReloadWhileStartAcknowledgementIsLostKeepsRetryIdentityOrUsesNewGeneration()
    {
        Save();
        var clock = new ManualClock();
        var acknowledge = false;
        var transport = new RecordingTransport((_, _) => Task.FromResult(Volatile.Read(ref acknowledge)));
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => transport.Reports.Count != 0);
        var first = transport.Reports[0];
        Assert.False((await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath))).Accepted);
        Assert.All(transport.Reports, report =>
        {
            Assert.Equal(first.EventId, report.EventId);
            Assert.Equal(first.Sequence, report.Sequence);
        });
        Save(Configuration with { HeartbeatIntervalSeconds = 600 });
        Volatile.Write(ref acknowledge, true);
        Assert.True((await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath))).Accepted);
        var currentStart = transport.Reports.Last(r => r.Kind == PresenceKind.Started);
        Assert.True(currentStart.Generation > first.Generation);
        Assert.Equal(1, currentStart.Sequence);
        Assert.Equal(600, currentStart.HeartbeatIntervalSeconds);
    }

    [Fact]
    public async Task DecreasingIntervalSchedulesSmallerValueEvenBeforeAcknowledgement()
    {
        Save(Configuration with { HeartbeatIntervalSeconds = 600 });
        var clock = new ManualClock();
        var acknowledge = true;
        var transport = new RecordingTransport((_, _) => Task.FromResult(Volatile.Read(ref acknowledge)));
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Save(Configuration with { HeartbeatIntervalSeconds = 60 });
        Volatile.Write(ref acknowledge, false);
        Assert.False((await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath))).Accepted);
        Assert.Equal(TimeSpan.FromMinutes(1), clock.PeriodicInterval);
        Assert.Equal(600, runtime.Status().HeartbeatIntervalSeconds);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServicingStopWaitsForOwnerReleaseEvenWhenOfflineIsUnconfirmed(bool offlineAcknowledged)
    {
        Save();
        var transport = new RecordingTransport((report, _) =>
            Task.FromResult(report.Kind != PresenceKind.Offline || offlineAcknowledged));
        ClientCoordinator? runtime = new(ConfigPath, new ManualClock(), _ => transport);
        ClientIpcServer? server = new(ConfigPath, runtime.HandleAsync);
        try
        {
            var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.StopAcknowledged += () => acknowledged.TrySetResult();
            runtime.Start();
            await Eventually(() => runtime.Status().State == "connected");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var servicing = new ClientIntegrationRuntime().StopAsync(ConfigPath, timeout.Token);
            await acknowledged.Task.WaitAsync(timeout.Token);
            Assert.True(new WindowsClientRuntimePlatform().IsOwnerRunning(ConfigPath));
            Assert.False(servicing.IsCompleted);
            Assert.False(runtime.AcceptHook(AgentEvent.SessionStart,
                new("after-exit", DateTimeOffset.UtcNow, false)).Accepted);
            Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
            await server.DisposeAsync();
            server = null;
            await runtime.DisposeAsync();
            runtime = null;
            await servicing;
            Assert.False(new WindowsClientRuntimePlatform().IsOwnerRunning(ConfigPath));
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            if (runtime is not null) await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task WorkerFaultCompletesLifetimeWithActionableSanitizedReason()
    {
        Save();
        var transport = new RecordingTransport((report, _) => report.Kind == PresenceKind.Started
            ? throw new InvalidOperationException("SECRET-INTERNAL-FAILURE")
            : Task.FromResult(true));
        await using var runtime = new ClientCoordinator(ConfigPath, new ManualClock(), _ => transport);
        runtime.Start();
        var reason = await ClientLifetime.WaitForExitAsync(runtime).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(reason);
        Assert.Contains("restart Client", reason);
        Assert.Equal(reason, runtime.Status().Error);
        Assert.Equal("stopping", runtime.Status().State);
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("late", DateTimeOffset.UtcNow, false)).Accepted);
        Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
        var log = await File.ReadAllTextAsync(RemotePaths.Log(ConfigPath));
        Assert.Contains("presence-runtime-failed", log);
        Assert.DoesNotContain("SECRET", log);
        Assert.DoesNotContain("SECRET", reason);
    }

    [Fact]
    public async Task ReconnectInterruptsBackoffButRetainsUnacknowledgedStartIdentity()
    {
        Save();
        var clock = new ManualClock();
        var acknowledge = false;
        var transport = new RecordingTransport((_, _) => Task.FromResult(Volatile.Read(ref acknowledge)));
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => transport.Reports.Count == 1);
        await Task.Delay(30);
        var started = transport.Reports[0];
        runtime.AcceptHook(AgentEvent.SessionStart, new("during-outage", clock.GetUtcNow(), false));
        Volatile.Write(ref acknowledge, true);
        runtime.RequestSnapshot();
        // Do not advance the injected clock: the reconnect signal, not elapsed backoff, must wake delivery.
        await Eventually(() => runtime.Status().State == "connected");
        await Eventually(() => transport.Reports.Any(report => report.Kind == PresenceKind.Heartbeat &&
            report.Sessions!.Any(session => session.SessionId == "during-outage")));
        var starts = transport.Reports.Where(report => report.Kind == PresenceKind.Started).ToArray();
        Assert.Equal(2, starts.Length);
        Assert.All(starts, report =>
        {
            Assert.Equal(started.EventId, report.EventId);
            Assert.Equal(started.Sequence, report.Sequence);
        });
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task ReconnectDuringFailedDeliveryCoalescesIntoOneCurrentSnapshot()
    {
        Save();
        var clock = new ManualClock();
        var failedHeartbeatEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstHeartbeat = 0;
        var transport = new RecordingTransport(async (report, token) =>
        {
            if (report.Kind == PresenceKind.Heartbeat && Interlocked.Increment(ref firstHeartbeat) == 1)
            {
                failedHeartbeatEntered.TrySetResult();
                await releaseFailure.Task.WaitAsync(token);
                return false;
            }
            return true;
        });
        await using var runtime = new ClientCoordinator(ConfigPath, clock, _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        clock.Advance(TimeSpan.FromMinutes(5));
        await failedHeartbeatEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.AcceptHook(AgentEvent.UserPromptSubmitted, new("latest", clock.GetUtcNow(), false));
        for (var i = 0; i < 100; i++) runtime.RequestSnapshot();
        releaseFailure.TrySetResult();
        await Eventually(() => transport.Reports.Count >= 3);
        await Task.Delay(30);
        Assert.Equal(3, transport.Reports.Count);
        var current = transport.Reports.Last();
        Assert.Equal(PresenceKind.Heartbeat, current.Kind);
        Assert.Contains(current.Sessions!, session => session.SessionId == "latest" &&
            session.UnderlyingState == AgentState.Executing);
        Assert.NotEqual(transport.Reports[1].EventId, current.EventId);
        Assert.True(current.Sequence > transport.Reports[1].Sequence);
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Theory]
    [InlineData(PresenceKind.Started)]
    [InlineData(PresenceKind.Offline)]
    public async Task UnexpectedFaultRemainsObservableWhileLifetimeStillCloses(PresenceKind failingKind)
    {
        Save();
        var transport = new RecordingTransport((report, _) => report.Kind == failingKind
            ? throw new NotSupportedException("SECRET-UNEXPECTED-FAULT")
            : Task.FromResult(true));
        var runtime = new ClientCoordinator(ConfigPath, new ManualClock(), _ => transport);
        try
        {
            runtime.Start();
            if (failingKind == PresenceKind.Offline)
            {
                await Eventually(() => runtime.Status().State == "connected");
                await Assert.ThrowsAsync<NotSupportedException>(() => runtime.StopAsync());
            }
            var reason = await ClientLifetime.WaitForExitAsync(runtime).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Contains("unexpected", reason);
            Assert.Contains("restart Client", reason);
            Assert.True(runtime.Completion.IsFaulted);
            await Assert.ThrowsAsync<NotSupportedException>(() => runtime.Completion);
            Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("late", DateTimeOffset.UtcNow, false)).Accepted);
            Assert.DoesNotContain("SECRET", await File.ReadAllTextAsync(RemotePaths.Log(ConfigPath)));
        }
        finally
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => runtime.DisposeAsync().AsTask());
        }
        Assert.False(new WindowsClientRuntimePlatform().IsOwnerRunning(ConfigPath));
    }

    internal static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    internal sealed class RecordingTransport(Func<PresenceReport, CancellationToken, Task<bool>>? send = null) : IPresenceTransport
    {
        private readonly ConcurrentQueue<PresenceReport> _reports = new();
        private int _concurrent;
        public int MaximumConcurrent { get; private set; }
        public bool Disposed { get; private set; }
        public IReadOnlyList<PresenceReport> Reports => _reports.ToArray();
        public async Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _concurrent);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            _reports.Enqueue(report);
            try { return send is null || await send(report, cancellationToken); }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public void Dispose() => Disposed = true;
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (_sync) return _now; }
        public override long GetTimestamp() => GetUtcNow().UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public TimeSpan PeriodicInterval
        {
            get { lock (_sync) return _timers.Single(t => !t.Disposed && t.Period > TimeSpan.Zero).Period; }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_sync)
            {
                var timer = new ManualTimer(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }
        public void Advance(TimeSpan duration)
        {
            List<ManualTimer> fire;
            lock (_sync)
            {
                _now += duration;
                fire = _timers.Where(t => !t.Disposed && t.Next <= _now).ToList();
                foreach (var timer in fire)
                    timer.Next = timer.Period > TimeSpan.Zero ? _now + timer.Period : DateTimeOffset.MaxValue;
            }
            foreach (var timer in fire) timer.Callback(timer.State);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public DateTimeOffset Next { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._sync)
                {
                    if (Disposed) return false;
                    Next = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                    Period = period;
                    return true;
                }
            }
            public void Dispose() { lock (clock._sync) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
