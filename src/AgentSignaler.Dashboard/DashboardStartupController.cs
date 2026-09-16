namespace AgentSignaler.Dashboard;

// One startup attempt, including the final public-endpoint verification.
// A failed attempt releases the view for recovery; window exit never reveals it.
internal sealed class DashboardStartupController
{
    public bool IsLoading { get; private set; } = true;
    public bool ShowMachines { get; private set; }

    public async Task RunAsync(Func<CancellationToken, Task> initialize, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await initialize(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Window-owned cancellation is normal shutdown, not a startup error.
        }
        finally
        {
            IsLoading = false;
            ShowMachines = !cancellationToken.IsCancellationRequested;
        }
    }
}
