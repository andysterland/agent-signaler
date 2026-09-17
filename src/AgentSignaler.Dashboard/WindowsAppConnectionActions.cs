namespace AgentSignaler.Dashboard;

internal sealed record WindowsAppActionResult(bool Succeeded, string Message, bool RestoreDetails);

internal sealed class WindowsAppConnectionActions(
    WindowsAppConnectionController controller, Func<Task> refresh, Func<bool> exiting,
    Action restoreDashboard, Action<string> showError, Func<Guid, string, Task> showDetails,
    Func<Guid, IDisposable>? showProgress = null,
    Func<Guid, Task<WindowsAppOperationResult>>? open = null)
{
    public async Task<WindowsAppActionResult> OpenWindowsAppAsync(Guid id, bool compact = false)
    {
        WindowsAppOperationResult outcome;
        using (compact && !exiting() && !controller.IsBusy(id) ? showProgress?.Invoke(id) : null)
            outcome = await (open?.Invoke(id) ?? controller.OpenWindowsAppAsync(id));
        var result = new WindowsAppActionResult(outcome.Succeeded, outcome.Message, compact && !outcome.Succeeded);
        if (exiting()) return result;
        await refresh();
        if (result.RestoreDetails && !exiting())
        {
            restoreDashboard();
            showError(result.Message);
            await showDetails(id, result.Message);
        }
        return result;
    }
}
