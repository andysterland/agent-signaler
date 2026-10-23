using AgentSignaler.AppUpdates;
using AgentSignaler.Updates;
using Microsoft.UI.Xaml.Controls;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow
{
    private UpdatePanel? _updates;
    private readonly AppUpdateHost _updateHost;
    private WinUiUpdateParticipant? _updateParticipant;
    private bool _updatePreparing;
    private bool _updateCompactWasVisible;

    private void InitializeUpdates()
    {
        _updates = new UpdatePanel(UpdatePackage.Dashboard, DispatcherQueue, BeginUpdateAsync, _updateHost.ReadRecoveryAsync,
            _updateHost.StartupWarning);
        AddRow(_updates.Banner, 4);
        AddRow(_updates.RecoveryBanner, 5);
        _updateParticipant = new WinUiUpdateParticipant(DispatcherQueue, PrepareUpdateAsync,
            CancelUpdatePreparation, ExitAsync, () => _running && !_exiting && _initializationTask is { IsCompletedSuccessfully: true });
    }

    private async Task<bool> PrepareUpdateAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_exiting || _updatePreparing || _dialogOpen || !_mutationTask.IsCompleted || _tunnelBusy || PrerequisiteBusy ||
            _catalog?.State.IsBusy == true || _initializationTask is not { IsCompletedSuccessfully: true } ||
            _connections is not null && _cardMap.Keys.Any(_connections.IsBusy))
        {
            ShowProblem("Update preparation was declined. Save or cancel open edits and wait for active workflows, then retry Update now. Dashboard is still running.");
            return false;
        }
        _updatePreparing = true;
        _settingsButton.IsEnabled = false;
        _root.IsHitTestVisible = false;
        // Capture on the dispatcher after freezing interactions, but before
        // hiding our transient compact UI. Startup --background is not current
        // visibility, and a visible compact view is not tray-only/background.
        _updateCompactWasVisible = _compactWindow?.AppWindow.IsVisible == true;
        var restart = new UpdateRestartContext(UpdateApplication.Dashboard, DashboardSettings.DataDirectory,
            !AppWindow.IsVisible && !_updateCompactWasVisible);
        _compactWindow?.Hide();
        _updates?.SetConflicting(true);
        await Task.Run(() => _updateHost.RefreshRestartContextAsync(restart, token), token);
        return true;
    }

    private void CancelUpdatePreparation()
    {
        if (_exiting) return;
        _updatePreparing = false;
        _root.IsHitTestVisible = true;
        _settingsButton.IsEnabled = true;
        _updates?.SetConflicting(false);
        if (_updateCompactWasVisible) UpdateCompactView();
        _updateCompactWasVisible = false;
        // Do not activate/restore a window that was hidden in the notification area.
    }

    private async Task StopUpdatesAsync()
    {
        _updateParticipant?.Dispose();
        if (_updates is not null) await _updates.DisposeAsync();
        await _updateHost.DisposeAsync();
    }

    private async Task<string?> BeginUpdateAsync(ReleaseCandidate candidate, CancellationToken token)
    {
        if (_dialogOpen || _exiting) return "Save or cancel the open dialog before updating.";
        var confirm = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = "Update and restart Dashboard?",
            Content = "Dashboard will close and receiving reports will pause during servicing. This does not update or start Client. Dashboard will restart in its previous visible or background mode.",
            PrimaryButtonText = "Update and restart", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        using var cancelDialog = token.Register(() => DispatcherQueue.TryEnqueue(confirm.Hide));
        if (await confirm.ShowAsync() != ContentDialogResult.Primary || token.IsCancellationRequested)
            return "Update cancelled. Dashboard is still running.";
        return await Task.Run(() => _updateHost.BeginUpdateAsync(candidate, token), token);
    }
}
