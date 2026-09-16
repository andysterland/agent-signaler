using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientDeliveryOrderingTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");

    [Fact]
    public async Task OfflineWaitsForCanceledTransportToFinishUnwinding()
    {
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }.ToVersion3();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(async (report, token) =>
        {
            if (report.Kind != PresenceKind.Hook) return true;
            hookEntered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                await releaseHook.Task;
                throw;
            }
            return true;
        });
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("session", DateTimeOffset.UtcNow, false)).Accepted);
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopped = runtime.StopAsync();
        try
        {
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(100);
            Assert.DoesNotContain(transport.Reports, r => r.Kind == PresenceKind.Offline);
            Assert.False(stopped.IsCompleted);
        }
        finally { releaseHook.TrySetResult(); }
        Assert.True((await stopped.WaitAsync(TimeSpan.FromSeconds(5))).Accepted);
        Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
        Assert.Equal(1, transport.MaximumConcurrent);
    }

    [Fact]
    public async Task NonCooperativeTransportAllowsOnlyTerminalOfflineAfterBoundedGrace()
    {
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test" }.ToVersion3();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var hookEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHook = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport((report, _) =>
        {
            if (report.Kind != PresenceKind.Hook) return Task.FromResult(true);
            hookEntered.TrySetResult();
            return releaseHook.Task;
        });
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("session", DateTimeOffset.UtcNow, false)).Accepted);
        await hookEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var stopped = await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(stopped.Accepted);
            Assert.Null(stopped.Error);
            Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
            Assert.Equal(2, transport.MaximumConcurrent);
            Assert.True(elapsed.Elapsed >= TimeSpan.FromMilliseconds(200));
            Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("late", DateTimeOffset.UtcNow, false)).Accepted);
        }
        finally { releaseHook.TrySetResult(false); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
