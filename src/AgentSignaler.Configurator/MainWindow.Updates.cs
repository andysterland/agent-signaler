using AgentSignaler.AppUpdates;
using AgentSignaler.Updates;
using Microsoft.UI.Xaml.Controls;

namespace AgentSignaler.Configurator;

public sealed partial class MainWindow
{
    private UpdatePanel? updates;
    private readonly AppUpdateHost updateHost;
    private WinUiUpdateParticipant? updateParticipant;
    private bool updatePreparing;
    private bool allowUpdateClose;
    private bool updateClosing;
    private bool updateCleanupFailed;
    private Task? updateCloseTask;
    private ConfiguratorEditState? savedUpdateState;

    private ConfiguratorEditState CurrentUpdateState() => new(UrlBox.Text, HeartbeatBox.Value,
        ConnectionModeBox.SelectedIndex, ShareDetailsSwitch.IsOn, RelayPathBox.Text,
        StartClientAtSignInCheckBox.IsChecked == true, DnsRecordBox.Text);

    private void CaptureSavedUpdateState() => savedUpdateState = CurrentUpdateState();
    private void CaptureSavedConnectionState(string savedRelayPath)
    {
        var current = CurrentUpdateState();
        var previous = savedUpdateState ?? current with { Relay = savedRelayPath };
        savedUpdateState = previous.WithSavedConnection(current);
    }
    private bool HasUnsavedUpdateEdits => savedUpdateState is null || savedUpdateState != CurrentUpdateState() ||
        rows.Any(row => row.HasPendingChange || row.IsDraft);

    private void InitializeUpdates()
    {
        updates = new UpdatePanel(UpdatePackage.Remote, DispatcherQueue, BeginUpdateAsync, updateHost.ReadRecoveryAsync);
        UpdateBannerHost.Content = updates.Banner;
        UpdateRecoveryHost.Content = updates.RecoveryBanner;
        UpdateSettingsHost.Content = updates.Settings;
        updateParticipant = new WinUiUpdateParticipant(DispatcherQueue, PrepareUpdateAsync,
            CancelUpdatePreparation, CommitUpdateExitAsync, () => !closed && !busy && savedUpdateState is not null);
    }

    private async Task<bool> PrepareUpdateAsync(CancellationToken token)
    {
        if (closed || busy || updatePreparing || discoveryCancellation is not null || connectionTest is not null ||
            savedUpdateState is null || !startupPreferenceLoaded)
            return false;
        updatePreparing = true;
        UpdateActionStates();
        var prepared = false;
        try
        {
            if (HasUnsavedUpdateEdits)
            {
                var choice = new ContentDialog
                {
                    XamlRoot = RootPanel.XamlRoot,
                    Title = "Unsaved Remote setup changes",
                    Content = "Connection, integration or startup edits have not been saved. Save changes uses Apply settings to save all edits, including pending hooks and the relay location, after preview and confirmation. Discard reloads saved settings. Cancel leaves this app running.",
                    PrimaryButtonText = "Save changes", SecondaryButtonText = "Discard changes", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                using var cancelDialog = token.Register(() => DispatcherQueue.TryEnqueue(choice.Hide));
                var result = await choice.ShowAsync();
                token.ThrowIfCancellationRequested();
                if (result == ContentDialogResult.Primary)
                {
                    if (!await ApplySettingsAsync(token)) return false;
                }
                else if (result == ContentDialogResult.Secondary)
                {
                    SetBusy(true);
                    try
                    {
                        await RefreshAsync(preserveSelection: false);
                        var config = File.Exists(ConfigPath) ? AgentSignaler.Remote.RemoteConfiguration.Load(ConfigPath) : null;
                        UrlBox.Text = config is { IsDiscovery: false } ? config.BaseUri.GetLeftPart(UriPartial.Authority) : "";
                        HeartbeatBox.Value = (config?.HeartbeatIntervalSeconds ?? 1800) / 60;
                        CaptureSavedUpdateState();
                    }
                    finally { SetBusy(false); }
                }
                else return false;
                if (HasUnsavedUpdateEdits) return false;
            }
            token.ThrowIfCancellationRequested();
            await Task.Run(() => updateHost.RefreshRestartContextAsync(
                new UpdateRestartContext(UpdateApplication.Configurator, ConfigPath, false), token), token);
            prepared = true;
            return true;
        }
        finally
        {
            // A successful preparation remains frozen until commit/cancel (or the
            // participant's bounded preparation lease expires).
            if (!prepared) CancelUpdatePreparation();
        }
    }

    private void CancelUpdatePreparation()
    {
        if (closed) return;
        updatePreparing = false;
        UpdateActionStates();
    }

    private Task CommitUpdateExitAsync() => CloseForUpdateOrExitAsync();

    private Task CloseForUpdateOrExitAsync()
    {
        if (updateClosing) return updateCloseTask ?? Task.CompletedTask;
        updateClosing = true;
        return updateCloseTask = CloseForUpdateOrExitCoreAsync();
    }

    private async Task CloseForUpdateOrExitCoreAsync()
    {
        try
        {
            if (closed) return;
            windowLifetime.Cancel();
            connectionTest?.Cancel();
            discoveryCancellation?.Cancel();
            try { await StopUpdatesAsync(); }
            catch (Exception error) when (error is IOException or InvalidDataException or UpdateDiscoveryException or UnauthorizedAccessException or System.Security.SecurityException or
                InvalidOperationException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception)
            {
                updateCleanupFailed = true;
                updatePreparing = false;
                SetBusy(true);
                ShowStatus("Maintenance cleanup failed (update_cleanup_failed). This window remains open; update shutdown has not completed. " +
                    "Check any updater progress or recovery result before closing this window again. Setup actions are disabled until restart.",
                    InfoBarSeverity.Error, "Update shutdown incomplete");
                return;
            }
            updatePreparing = false;
            allowUpdateClose = true;
            Close();
        }
        finally
        {
            updateClosing = false;
            updateCloseTask = null;
        }
    }

    private async Task StopUpdatesAsync()
    {
        updateParticipant?.Dispose();
        if (updates is not null) await updates.DisposeAsync();
        await updateHost.DisposeAsync();
    }

    private async Task<string?> BeginUpdateAsync(ReleaseCandidate candidate, CancellationToken token)
    {
        if (closed || busy || updatePreparing) return "Finish or cancel the active operation before updating.";
        SetBusy(true);
        bool confirmed;
        try
        {
            confirmed = await ConfirmAsync("Update and restart the complete Remote package?",
                "Client and all open Configurator windows will close. Reporting pauses during servicing. Only previously running apps will restart. Unsaved changes can be saved through Apply settings, discarded, or cancelled before shutdown.",
                "Update and restart", token);
        }
        finally { SetBusy(false); }
        if (!confirmed) return "Update cancelled. Remote setup is still running.";
        return await Task.Run(() => updateHost.BeginUpdateAsync(candidate, token), token);
    }
}
