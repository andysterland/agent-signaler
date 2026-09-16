using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class ManagedDeliveryRaceTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "delivery-races", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");

    public ManagedDeliveryRaceTests()
    {
        var config = new RemoteConfiguration
        {
            Host = "localhost", MachineId = Guid.NewGuid(), MachineName = "RACE-TEST", ClientVersion = "test"
        }.ToVersion3();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
    }

    [Fact]
    public async Task HookIgnoringCancellationCannotReviveAfterTerminalOfflineCommits()
    {
        using var store = new MachineStore(Path.Combine(_root, "dashboard.db"));
        var hookStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookCommitted = new TaskCompletionSource<PresenceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<PresenceReport>();
        using var transport = new StoreTransport(async report =>
        {
            reports.Enqueue(report);
            if (report.Kind == PresenceKind.Hook)
            {
                hookStarted.TrySetResult();
                await releaseHook.Task;
                var result = await store.AcceptAsync(report);
                hookCommitted.TrySetResult(result);
            }
            else await store.AcceptAsync(report);
            return true;
        });
        await using var coordinator = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        try
        {
            coordinator.Start();
            await WaitUntil(() => coordinator.Status().State == "connected");
            Assert.True(coordinator.AcceptHook(AgentEvent.PreToolUse,
                new HookData("in-flight", DateTimeOffset.UtcNow, false)).Accepted);
            await hookStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var elapsed = Stopwatch.StartNew();
            var stop = await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stop.Accepted);
            Assert.Null(stop.Error);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
            var terminal = Assert.Single(await store.GetMachinesAsync());
            Assert.Equal(AgentState.Offline, terminal.State);
            Assert.True(terminal.ExplicitOffline);

            releaseHook.TrySetResult();
            Assert.True((await hookCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Duplicate);
            var afterLateCommit = Assert.Single(await store.GetMachinesAsync());
            Assert.Equal(AgentState.Offline, afterLateCommit.State);
            Assert.Equal(terminal.Sequence, afterLateCommit.Sequence);
            Assert.Equal(terminal.LastContactUtc, afterLateCommit.LastContactUtc);
            Assert.Equal(PresenceKind.Offline, reports.Last().Kind);
            Assert.False(coordinator.AcceptHook(AgentEvent.SessionEnd,
                new HookData("in-flight", DateTimeOffset.UtcNow, false)).Accepted);
        }
        finally
        {
            releaseHook.TrySetResult();
            if (hookStarted.Task.IsCompleted)
                await hookCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LostStartedAcknowledgementStillAllowsOfflineAndOnlyNewRunRevives()
    {
        using var store = new MachineStore(Path.Combine(_root, "dashboard.db"));
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reports = new ConcurrentQueue<PresenceReport>();
        using var transport = new StoreTransport(async report =>
        {
            reports.Enqueue(report);
            await store.AcceptAsync(report);
            if (report.Kind != PresenceKind.Started) return true;
            committed.TrySetResult();
            return false;
        });
        long generation;
        await using (var coordinator = new ClientCoordinator(ConfigPath, transportFactory: _ => transport))
        {
            coordinator.Start();
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(coordinator.AcceptHook(AgentEvent.PreToolUse,
                new HookData("not-announced", DateTimeOffset.UtcNow, false)).Accepted);
            Assert.Null((await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(5))).Error);
            var terminal = Assert.Single(await store.GetMachinesAsync());
            generation = terminal.Generation;
            Assert.Equal(AgentState.Offline, terminal.State);
            Assert.True(terminal.ExplicitOffline);
            Assert.DoesNotContain(reports, r => r.Kind is PresenceKind.Hook or PresenceKind.Heartbeat);
            Assert.True((await store.AcceptAsync(reports.First())).Duplicate);
            Assert.Equal(terminal.LastContactUtc, Assert.Single(await store.GetMachinesAsync()).LastContactUtc);
        }
        using var restartedTransport = new StoreTransport(async report =>
        {
            await store.AcceptAsync(report);
            return true;
        });
        await using var restarted = new ClientCoordinator(ConfigPath, transportFactory: _ => restartedTransport);
        restarted.Start();
        await WaitUntil(() => restarted.Status().State == "connected");
        var current = Assert.Single(await store.GetMachinesAsync());
        Assert.True(current.Generation > generation);
        Assert.Equal(AgentState.Idle, current.State);
        Assert.Empty(current.Sessions);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class StoreTransport(Func<PresenceReport, Task<bool>> send) : IPresenceTransport
    {
        public Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken) => send(report);
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
