using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

internal enum PrerequisiteCheckState { NotChecked, Queued, Running, Passed, Failed, Cancelled }

/// <summary>Thread-safe diagnostic presentation state, independent of a UI synchronization context.</summary>
internal sealed class PrerequisiteCheck(string name)
{
    private CancellationTokenSource? cancellation;
    private Task task = Task.CompletedTask;
    private bool closed;
    private readonly object sync = new();
    private PrerequisiteCheckState state;
    private string result = "Not checked.";

    public bool IsBusy { get { lock (sync) return cancellation is not null; } }
    public PrerequisiteCheckState State { get { lock (sync) return state; } private set { lock (sync) state = value; } }
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
    public string Result { get { lock (sync) return result; } private set { lock (sync) result = value; } }
    public event Action? Changed;

    internal static PrerequisiteDiagnosticResult FromRuntimeResult(
        RuntimeResult<IReadOnlyList<RuntimePrerequisite>> outcome, RuntimePrerequisiteKind kind,
        string? requestedPath, Func<RuntimeError, string> describeError)
    {
        if (outcome.Error is { } error && (error.Code != 1010 || outcome.Snapshot is null))
            return new(false, describeError(error));
        var check = outcome.Snapshot?.State.SingleOrDefault(item => item.Id == kind.ToString());
        var expectedPath = DashboardRuntime.CapturePrerequisitePath(kind, requestedPath);
        if (check is not null && string.Equals(check.TestedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            if (outcome.Succeeded && check is { State: PrerequisiteCheckState.Passed, Result.Passed: true })
                return check.Result;
            if (!outcome.Succeeded && check is { State: PrerequisiteCheckState.Failed, Result.Passed: false })
                return check.Result;
        }
        return new(false, "The requested check did not complete.");
    }

    public Task RunAsync(Func<CancellationToken, Task<PrerequisiteDiagnosticResult>> check, Task? startAfter = null)
    {
        Task resultTask;
        lock (sync)
        {
            if (closed || IsBusy) throw new InvalidOperationException("The prerequisite check is closed or already running.");
            var source = cancellation = new CancellationTokenSource();
            State = startAfter is { IsCompleted: false } ? PrerequisiteCheckState.Queued : PrerequisiteCheckState.Running;
            Result = State == PrerequisiteCheckState.Queued ? $"{name} check queued." : $"Checking {name}...";
            resultTask = task = Task.Run(() => ExecuteAsync(check, source, startAfter));
        }
        Changed?.Invoke();
        return resultTask;
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
            lock (sync)
            {
                cancellation = null;
                source.Dispose();
            }
            Changed?.Invoke();
        }
    }

    public void Cancel() { lock (sync) cancellation?.Cancel(); }

    public Task CancelAndWaitAsync()
    {
        lock (sync)
        {
            closed = true;
            Cancel();
            return task;
        }
    }
}
