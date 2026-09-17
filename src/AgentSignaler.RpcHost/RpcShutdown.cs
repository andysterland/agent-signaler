using AgentSignaler.Dashboard;

namespace AgentSignaler.RpcHost;

internal sealed class RpcShutdown(
    Func<CancellationToken, Task<RuntimeShutdownResult>> stopRuntime,
    Action stopAdmission,
    Func<Task> stopTransport,
    TimeProvider? timeProvider = null)
{
    internal static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan RuntimeDrainTimeout = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly TaskCompletionSource requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task<bool>? completion;
    public Task Requested => requested.Task;
    public Task<bool> Completion
    {
        get { lock (sync) return completion ?? throw new InvalidOperationException("Shutdown has not started."); }
    }

    public void Request(Task? acknowledgement = null)
    {
        lock (sync)
        {
            if (completion is not null) return;
            completion = StopAsync(acknowledgement ?? Task.CompletedTask);
            requested.TrySetResult();
        }
    }

    private async Task<bool> StopAsync(Task acknowledgement)
    {
        using var total = new CancellationTokenSource(TotalTimeout, clock);
        // Start runtime cancellation immediately. Its independent five-second native-child reserve
        // must be awaited after drain cancellation, not abandoned by that cancellation token.
        using var drain = new CancellationTokenSource(RuntimeDrainTimeout, clock);
        var clean = true;
        try { stopAdmission(); }
        catch (Exception) { clean = false; }
        var runtime = AttemptAsync(async () => (await stopRuntime(drain.Token).ConfigureAwait(false)).Clean, total.Token);
        try { await acknowledgement.WaitAsync(AcknowledgementTimeout, clock, total.Token).ConfigureAwait(false); }
        catch (Exception) { clean = false; }
        var transport = AttemptAsync(async () => { await stopTransport().ConfigureAwait(false); return true; }, total.Token);
        var results = await Task.WhenAll(runtime, transport).ConfigureAwait(false);
        return clean && results.All(result => result);
    }

    private static async Task<bool> AttemptAsync(Func<Task<bool>> action, CancellationToken deadline)
    {
        Task<bool>? task = null;
        try
        {
            task = action();
            return await task.WaitAsync(deadline).ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (task is not null)
                _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }
    }
}
