using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow
{
    private DevBoxCatalogController? _catalog;
    private Action? _updateDevBoxSettingsControls;

    private Func<DashboardSettings> BuildDevBoxSettings(StackPanel panel, StackPanel help)
    {
        panel.Children.Add(Text("Dev Center discovery", 18));
        help.Children.Add(Text("Azure CLI searches enabled subscriptions available to the signed-in user in the current tenant, " +
            "then lists assigned Dev Boxes in the discovered Dev Centers. Azure Resource Manager read access to Dev Centers is required; " +
            "Dev Box User access alone may not allow discovery. No Dev Center endpoints need to be entered or saved."));
        help.Children.Add(Text("Refresh uses the running Azure CLI path. After changing the path, Save, Exit and reopen before refreshing. " +
            "Discovery does not switch the Azure CLI account, subscription, or tenant."));
        var subscriptionInput = new TextBox
        {
            Header = "Subscription ID (optional)",
            PlaceholderText = "Leave blank to search all eligible subscriptions",
            Text = _settings.DevBoxSubscriptionId?.ToString() ?? ""
        };
        var devCenterInput = new TextBox
        {
            Header = "Dev Center name (optional, instead of subscription ID)",
            PlaceholderText = "For example: devcenter-tfotz75rskxty-dc",
            Text = _settings.DevCenterName ?? ""
        };
        var targetValidation = Text("");
        panel.Children.Add(subscriptionInput);
        panel.Children.Add(devCenterInput);
        panel.Children.Add(targetValidation);
        help.Children.Add(Text("Supply a subscription GUID to search it directly using the signed-in account. " +
            "Or supply a Dev Center name to list your Dev Boxes with az devcenter dev dev-box list --user-id me. " +
            "Leave both blank for automatic discovery. Neither option changes the selected Azure CLI subscription."));
        help.Children.Add(Text("Refresh Dev Boxes and Save remember these search fields across restarts. " +
            "Clear both fields and refresh or save to restore automatic discovery. Results remain session-only."));
        help.Children.Add(Text("For Azure CLI paths, readiness checks, and devcenter extension setup, use Settings > Prerequisite."));
        panel.Children.Add(Text("Azure CLI account", 18));
        var identity = Text("");
        identity.IsTextSelectionEnabled = true;
        var discovery = Text("");
        var failures = new ListView { MaxHeight = 140, SelectionMode = ListViewSelectionMode.None };
        panel.Children.Add(identity);
        panel.Children.Add(discovery);
        panel.Children.Add(failures);
        help.Children.Add(Text("Account details reflect the latest refresh attempt, not a live sign-in check. " +
            "After signing in or changing accounts in Azure CLI, refresh to update them."));
        panel.Children.Add(Text("Available Dev Boxes", 18));
        var refresh = new Button { Content = "Refresh Dev Boxes" };
        var cancel = new Button { Content = "Cancel refresh", Visibility = Visibility.Collapsed };
        var activity = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(activity, "Dev Box discovery in progress");
        var status = Text("");
        var retrieved = Text("");
        var count = Text("");
        var account = Text("");
        account.IsTextSelectionEnabled = true;
        var centers = new ListView { MaxHeight = 120, SelectionMode = ListViewSelectionMode.None };
        var list = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.None };
        panel.Children.Add(refresh);
        panel.Children.Add(cancel);
        panel.Children.Add(activity);
        panel.Children.Add(status);
        panel.Children.Add(retrieved);
        panel.Children.Add(count);
        panel.Children.Add(account);
        panel.Children.Add(Text("Discovered Dev Centers", 16));
        panel.Children.Add(centers);
        panel.Children.Add(list);
        help.Children.Add(Text("This list is informational. Open a machine's details to explicitly select and save its Dev Box mapping."));
        _updateDevBoxSettingsControls = () =>
        {
            var state = _catalog?.State;
            var busy = state?.IsBusy == true;
            var editingEnabled = !_exiting && !_prerequisitesClosing && !busy && !AzurePrerequisiteBusy;
            var validTarget = TryReadSettings(out _, out var validationMessage);
            subscriptionInput.IsEnabled = editingEnabled;
            devCenterInput.IsEnabled = editingEnabled;
            targetValidation.Text = validationMessage;
            refresh.IsEnabled = editingEnabled && _catalog is not null && validTarget;
            cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            cancel.IsEnabled = busy && !_exiting;
            activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (_activeDialog is not null) _activeDialog.IsPrimaryButtonEnabled = editingEnabled && !PrerequisiteBusy;
            _updatePrerequisiteControls?.Invoke();
            status.Text = state is null ? "Local storage and discovery are unavailable. Restart Dashboard and retry."
                : $"{state.Status}: {state.Message}";
            var progress = state?.Progress;
            identity.Text = progress?.Account is { } current
                ? $"Signed-in user: {current.Upn}\nTenant ID: {current.Tenant}\nSelected subscription ID: {current.Subscription}\n" +
                    $"Account type: Interactive user | Subscription state: Enabled\n" +
                    $"Account validated: {progress.AccountCheckedAtUtc?.ToLocalTime().ToString("G")}"
                : busy ? "Checking Azure CLI account..."
                : progress is null ? "Account not checked. Select Refresh Dev Boxes."
                : "Azure CLI account could not be validated during the latest refresh attempt. See the refresh status below.";
            discovery.Text = progress is null ? "Discovery has not started."
                : $"{(busy ? "Current stage" : "Last stage")}: {progress.StageText}\n" +
                    $"Search scope: {state?.TargetDevCenterName ?? (state?.TargetSubscription is { } target ? target.ToString() : "All eligible subscriptions")}\n" +
                    (state?.TargetDevCenterName is not null ? "Subscription discovery: Skipped (Dev Center name supplied)\n" :
                        $"Subscriptions processed: {progress.SubscriptionsCompleted} / {progress.SubscriptionCount?.ToString() ?? "Not determined"} | Failed: {progress.SubscriptionFailures.Count}\n") +
                    $"Dev Centers searched: {progress.DevCentersCompleted} / {progress.DevCenterCount?.ToString() ?? "Not determined"}\n" +
                    $"Discovery responses read: {progress.PagesRead} | Dev Boxes found this attempt: {progress.DevBoxCount?.ToString() ?? "Not determined"}" +
                    (progress.CurrentSubscription is { } subscription ? $"\nSubscription being searched: {subscription}" : "") +
                    (progress.CurrentDevCenterHost is { } host ? $"\nDev Center being searched: {host}" : "") +
                    (progress.CurrentPage is { } page ? $"\nCurrent / last requested page: {page}" : "");
            failures.ItemsSource = progress?.SubscriptionFailures.Select(failure =>
                $"Subscription {failure.SubscriptionId}: {failure.Message}").ToArray();
            var snapshot = state?.Snapshot;
            retrieved.Text = $"Last successful catalog refresh: {snapshot?.RetrievedAtUtc.ToLocalTime().ToString("G") ?? "Never"}";
            count.Text = $"Discovered Dev Centers: {snapshot?.DevCenterEndpoints.Count ?? 0} | Dev Boxes: {snapshot?.Items.Count ?? 0}";
            account.Text = snapshot is null ? "No catalog has been retrieved."
                : $"Displayed catalog account: {snapshot.AzureAccountUpn}\nDisplayed catalog tenant: {snapshot.AzureTenantId}";
            centers.ItemsSource = snapshot?.DevCenterEndpoints.Select(endpoint => endpoint.IdnHost).ToArray();
            list.ItemsSource = snapshot?.Items.Select(item =>
                $"{item.DevBoxName} | {item.ProjectName} | {item.PoolName} | {item.PowerState} | {item.DevCenterEndpoint.IdnHost}").ToArray();
        };
        refresh.Click += async (_, _) =>
        {
            if (!TryReadSettings(out var next, out var message))
            {
                targetValidation.Text = message;
                return;
            }
            try
            {
                next.Save();
                _settings = next;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                targetValidation.Text = "Discovery settings could not be saved. Check local application-data permissions and retry. Discovery has not started.";
                return;
            }
            if (_catalog is not null)
                await _catalog.RefreshAsync(subscriptionId: next.DevBoxSubscriptionId, devCenterName: next.DevCenterName);
        };
        subscriptionInput.TextChanged += (_, _) => _updateDevBoxSettingsControls?.Invoke();
        devCenterInput.TextChanged += (_, _) => _updateDevBoxSettingsControls?.Invoke();
        cancel.Click += (_, _) => _catalog?.Cancel();
        return ReadSettings;

        DashboardSettings ReadSettings() => _settings.WithDiscoveryTarget(subscriptionInput.Text, devCenterInput.Text);

        bool TryReadSettings(out DashboardSettings settings, out string message)
        {
            settings = _settings;
            message = "";
            try { settings = ReadSettings(); }
            catch (ArgumentException error)
            {
                message = error.Message;
                return false;
            }
            return true;
        }
    }
}
