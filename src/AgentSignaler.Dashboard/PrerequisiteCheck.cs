using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

internal enum PrerequisiteCheckState { NotChecked, Queued, Running, Passed, Failed, Cancelled }

/// <summary>Dialog-owned diagnostics; cancellation never reaches operational controllers.</summary>
internal sealed class PrerequisiteCheck(string name)
{
    private CancellationTokenSource? cancellation;
    private Task task = Task.CompletedTask;
    private bool closed;

    public bool IsBusy => cancellation is not null;
    public PrerequisiteCheckState State { get; private set; }
    public string Summary => State switch
    {
        PrerequisiteCheckState.NotChecked => "Not checked",
        PrerequisiteCheckState.Queued => "Queued",
        PrerequisiteCheckState.Running => "Checking...",
        PrerequisiteCheckState.Passed => "Passed",
        PrerequisiteCheckState.Failed => "Failed",
        PrerequisiteCheckState.Cancelled => "Cancelled",
        _ => throw new InvalidOperationException("Unknown prerequisite check state.")
    };
    public string Result { get; private set; } = "Not checked.";
    public event Action? Changed;

    public Task RunAsync(Func<CancellationToken, Task<PrerequisiteDiagnosticResult>> check, Task? startAfter = null)
    {
        if (closed || IsBusy) throw new InvalidOperationException("The prerequisite check is closed or already running.");
        cancellation = new CancellationTokenSource();
        State = startAfter is { IsCompleted: false } ? PrerequisiteCheckState.Queued : PrerequisiteCheckState.Running;
        Result = State == PrerequisiteCheckState.Queued ? $"{name} check queued." : $"Checking {name}...";
        task = ExecuteAsync(check, cancellation, startAfter);
        Changed?.Invoke();
        return task;
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task<PrerequisiteDiagnosticResult>> check,
        CancellationTokenSource source, Task? startAfter)
    {
        try
        {
            if (startAfter is not null) await startAfter.WaitAsync(source.Token);
            source.Token.ThrowIfCancellationRequested();
            State = PrerequisiteCheckState.Running;
            Result = $"Checking {name}...";
            Changed?.Invoke();
            var result = await Task.Run(() => check(source.Token), source.Token);
            source.Token.ThrowIfCancellationRequested();
            Result = result.Details;
            State = result.Passed ? PrerequisiteCheckState.Passed : PrerequisiteCheckState.Failed;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Result = $"{name} check cancelled.";
            State = PrerequisiteCheckState.Cancelled;
        }
        finally
        {
            if (State is PrerequisiteCheckState.Running or PrerequisiteCheckState.Queued)
            {
                Result = $"{name} check failed unexpectedly.";
                State = PrerequisiteCheckState.Failed;
            }
            cancellation = null;
            source.Dispose();
            Changed?.Invoke();
        }
    }

    public void Cancel() => cancellation?.Cancel();

    public async Task CancelAndWaitAsync()
    {
        closed = true;
        Cancel();
        await task;
    }
}
