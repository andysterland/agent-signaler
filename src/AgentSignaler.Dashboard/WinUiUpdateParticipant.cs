using AgentSignaler.Updates;
using Microsoft.UI.Dispatching;

namespace AgentSignaler.AppUpdates;

internal sealed class WinUiUpdateParticipant(DispatcherQueue dispatcher,
    Func<CancellationToken, Task<bool>> prepare, Action cancel, Func<Task> exit,
    Func<bool> ready) : IUpdateMaintenanceParticipant, IUpdateMaintenanceHealth, IDisposable
{
    private Guid preparedRun;
    private Timer? timeout;
    private bool disposed;

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => completion.TrySetResult(!disposed && ready()))) completion.TrySetResult(false);
        return completion.Task.WaitAsync(cancellationToken);
    }

    public async Task<bool> PrepareAsync(Guid runId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        if (!dispatcher.TryEnqueue(async () =>
        {
            if (disposed || cancellationToken.IsCancellationRequested) { completion.TrySetResult(false); return; }
            if (preparedRun != Guid.Empty) { completion.TrySetResult(preparedRun == runId); return; }
            try
            {
                if (!await prepare(cancellationToken)) { completion.TrySetResult(false); return; }
                if (cancellationToken.IsCancellationRequested) { cancel(); completion.TrySetResult(false); return; }
                preparedRun = runId;
                timeout = new Timer(_ => dispatcher.TryEnqueue(() => Reset(runId)), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
                completion.TrySetResult(true);
            }
            catch (OperationCanceledException) { cancel(); completion.TrySetResult(false); }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UpdateDiscoveryException or
                UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception or
                InvalidOperationException or System.Runtime.InteropServices.COMException)
            { cancel(); completion.TrySetResult(false); }
        })) completion.TrySetResult(false);
        return await completion.Task;
    }

    public Task CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() => { Reset(runId); completion.TrySetResult(); })) completion.TrySetResult();
        return completion.Task.WaitAsync(cancellationToken);
    }

    public Task CommitExitAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Acknowledge before running shutdown: shutdown disposes the pipe endpoint.
        // The independent updater verifies real process exit before touching MSI.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(() =>
        {
            if (disposed || preparedRun != runId) { completion.TrySetException(new InvalidOperationException("update_not_prepared")); return; }
            timeout?.Dispose(); timeout = null;
            if (!dispatcher.TryEnqueue(async () => await exit()))
            { completion.TrySetException(new InvalidOperationException("update_dispatch_unavailable")); return; }
            completion.TrySetResult();
        })) completion.TrySetException(new InvalidOperationException("update_dispatch_unavailable"));
        return completion.Task.WaitAsync(cancellationToken);
    }

    private void Reset(Guid runId)
    {
        if (disposed || preparedRun != runId) return;
        timeout?.Dispose(); timeout = null;
        preparedRun = Guid.Empty;
        cancel();
    }

    public void Dispose() { disposed = true; timeout?.Dispose(); timeout = null; }
}
