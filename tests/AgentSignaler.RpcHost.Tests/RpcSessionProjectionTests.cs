using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.RpcHost;
using AgentSignaler.Service;
using Xunit;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcSessionProjectionTests
{
    [Fact]
    public async Task InProcessSessionsKeepWaitingSemanticsAndV1DtoAllowlist()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AgentSignaler-rpc-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var lease = DashboardResourceLease.Acquire(directory, directory);
            await using var runtime = new DashboardRuntime(lease);
            using var application = new RuntimeRpcApplication(runtime, () => { });
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var failed = StateReducer.Apply(null, "a", AgentEvent.PostToolUseFailure, now, now,
                source: new("copilot-cli", "synthetic"));
            var waiting = StateReducer.Apply(failed, "a", AgentEvent.PermissionRequest,
                now.AddSeconds(1), now.AddSeconds(1));
            var running = StateReducer.Apply(null, "b", AgentEvent.UserPromptSubmitted, now, now,
                source: new("copilot-cli", "synthetic"));
            var machine = new MachineView(id, "synthetic", null, "copilot-cli", "synthetic",
                StateReducer.Aggregate([waiting, running], now), AgentEvent.PermissionRequest, now, [running, waiting]);
            runtime.PublishMachines([machine]);
            var parameters = JsonSerializer.SerializeToElement(new { machineId = id, limit = 1 }, RpcProtocol.Json);
            var before = await application.ExecuteAsync("machines.getSessions", parameters, CancellationToken.None);
            var json = JsonSerializer.SerializeToElement(before.State, RpcProtocol.Json);
            var first = json.GetProperty("state").GetProperty("items")[0];
            Assert.Equal("waiting", first.GetProperty("state").GetString());
            Assert.Equal(new[]
            {
                "awaitingUserInput", "resultState", "resultUntilUtc", "sessionId", "source",
                "state", "underlyingState", "updatedAtUtc"
            }, first.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
            Assert.False(first.TryGetProperty("latestEvent", out _));
            Assert.False(first.TryGetProperty("latestEventAtUtc", out _));
            var previousRevision = json.GetProperty("revision").GetString();
            running = StateReducer.Apply(running, "b", AgentEvent.PostToolUse, now.AddSeconds(2), now.AddSeconds(2));
            runtime.PublishMachines([machine with { Sessions = [waiting, running] }]);
            var stale = JsonSerializer.SerializeToElement(new
            {
                machineId = id, offset = 1, limit = 1, expectedRevision = previousRevision
            }, RpcProtocol.Json);
            Assert.Equal(1004, (await Assert.ThrowsAsync<RpcFault>(() =>
                application.ExecuteAsync("machines.getSessions", stale, CancellationToken.None))).Code);
            Assert.Equal(AgentState.Waiting, runtime.GetMachine(id).State.Machine.State);
            Assert.Null(runtime.Server);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
