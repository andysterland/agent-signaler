using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow
{
    private async Task SaveRuntimeSettingsAsync(DashboardSettings next, bool explicitDetailedReceptionEnable = false)
    {
        var result = await _runtime.UpdateSettingsAsync(next, _runtime.HostInstanceId, _runtime.Settings.Revision,
            explicitDetailedReceptionEnable: explicitDetailedReceptionEnable);
        if (result.Error is { } error) throw new RuntimeCommandException(error);
        _settings = result.Snapshot!.State.Saved;
    }

    private async Task RefreshRuntimeCatalogAsync(DashboardSettings next)
    {
        var result = await _runtime.RefreshCatalogAsync(next.DevBoxSubscriptionId, next.DevCenterName,
            _runtime.HostInstanceId, _runtime.Settings.Revision);
        _settings = _runtime.Settings.State.Saved;
        if (result.Error is { } error) ShowProblem(RuntimeFailureMessage(error));
    }

    private async Task<WindowsAppOperationResult> ExecuteRuntimeConnectionAsync(Guid id, WindowsAppOperation operation,
        DevBoxMappingSelection? selection = null)
    {
        try
        {
            var machine = _runtime.GetMachine(id);
            var result = await _runtime.WindowsAppAsync(id, operation, selection, operation == WindowsAppOperation.Clear,
                _runtime.HostInstanceId, machine.Revision);
            return result.Succeeded
                ? new(true, result.Snapshot!.State.Message)
                : new(false, MachineNavigation.IsLocal(machine.State.Machine) && result.Error!.Code == 1010 &&
                    operation is WindowsAppOperation.Open or WindowsAppOperation.OpenLastKnown
                    ? new WindowsAppConnectionException(WindowsAppFailure.MinimizeFailed).Message
                    : RuntimeFailureMessage(result.Error!));
        }
        catch (RuntimeCommandException error) { return new(false, RuntimeFailureMessage(error.Error)); }
    }

    private async Task UpdateRuntimeMachineAsync(Guid id, string? name, string? note, long expectedRevision)
    {
        var result = await _runtime.UpdateMachineAsync(id, name, note, _runtime.HostInstanceId, expectedRevision);
        if (result.Error is { } error) throw new RuntimeCommandException(error);
    }

    private async Task RemoveRuntimeMachineAsync(Guid id, long expectedRevision)
    {
        var result = await _runtime.RemoveMachineAsync(id, true, _runtime.HostInstanceId, expectedRevision);
        if (result.Error is { } error) throw new RuntimeCommandException(error);
    }

    private async Task<PrerequisiteDiagnosticResult> CheckRuntimePrerequisiteAsync(
        RuntimePrerequisiteKind kind, string? path, CancellationToken token)
    {
        var result = await _runtime.CheckPrerequisiteAsync(kind, path, token);
        token.ThrowIfCancellationRequested();
        return PrerequisiteCheck.FromRuntimeResult(result, kind, path, RuntimeFailureMessage);
    }

    private static string RuntimeFailureMessage(RuntimeError error) => error.Code switch
    {
        1001 when error.Field == "localMachine" => "The local machine cannot be removed.",
        1001 => "The supplied value is invalid. Check the fields and retry.",
        1002 => "The machine was removed.",
        1003 => "Another operation uses these resources. Wait or cancel it, then retry.",
        1004 => "The state changed while editing. Reopen the details and retry.",
        1006 => "The operation was cancelled. Previously saved changes remain saved.",
        1007 => "The operation timed out. Check its state before retrying.",
        1008 => "Persistence failed. Check application-data permissions and retry. Previously saved changes remain saved.",
        _ => "The operation could not complete. Check prerequisite and operation status, then retry."
    };
}
