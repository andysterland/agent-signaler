using AgentSignaler.Contracts;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow
{
    private Action? _updateDiscoveryControls;
    private CancellationTokenSource? _discoverySettingsLifetime;

    private void BuildDiscoverySettings(StackPanel panel, StackPanel help)
    {
        _discoverySettingsLifetime = CancellationTokenSource.CreateLinkedTokenSource(_tunnelLifetime.Token);
        var cancellation = _discoverySettingsLifetime;
        var busy = false;
        panel.Children.Add(Text("Private Azure Blob discovery", 18));
        panel.Children.Add(Text("Give trusted Clients one connection package. Dashboard publishes only a signed verified tunnel address, never machine or conversation data."));
        var tenant = new TextBox { Header = "Azure tenant ID (GUID)", PlaceholderText = "Pinned Microsoft Entra tenant" };
        var subscription = new TextBox { Header = "Azure subscription ID (GUID)" };
        var group = new TextBox { Header = "Existing resource group" };
        var account = new TextBox { Header = "Existing public Azure storage account", PlaceholderText = "Lowercase account name" };
        foreach (var control in new[] { tenant, subscription, group, account }) panel.Children.Add(control);
        var status = Text("");
        var preview = Text("");
        panel.Children.Add(status);
        panel.Children.Add(preview);
        var createConsent = new CheckBox { Content = "Create the exact private container and read-only policy shown in this preview." };
        var destructiveConsent = new CheckBox { Content = "I understand reader-group impact: distribute a rotated package before retiring old readers; revocation/removal disconnects all readers." };
        panel.Children.Add(createConsent);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var inspect = new Button { Content = "Preview setup" };
        var provision = new Button { Content = "Create discovery" };
        buttons.Children.Add(inspect);
        buttons.Children.Add(provision);
        panel.Children.Add(buttons);
        var copy = new Button { Content = "Copy Client connection package" };
        panel.Children.Add(copy);
        var maintain = new StackPanel { Spacing = 8 };
        var recover = new Button { Content = "Recover / Verify" };
        var renew = new Button { Content = "Renew policy" };
        var refresh = new Button { Content = "Publish current state" };
        var testReader = new Button { Content = "Test reader access" };
        foreach (var button in new[] { recover, renew, refresh, testReader }) maintain.Children.Add(button);
        panel.Children.Add(maintain);
        var publishing = new Button { Content = "Disable publishing" };
        panel.Children.Add(publishing);
        panel.Children.Add(destructiveConsent);
        var destructive = new StackPanel { Spacing = 8 };
        var rotate = new Button { Content = "Rotate package" };
        var retire = new Button { Content = "Retire previous reader group" };
        var revoke = new Button { Content = "Revoke discovery" };
        var remove = new Button { Content = "Remove discovery" };
        foreach (var button in new[] { rotate, retire, revoke, remove }) destructive.Children.Add(button);
        panel.Children.Add(destructive);
        var outcome = Text("");
        panel.Children.Add(outcome);
        help.Children.Add(Text("Sign in yourself with Azure CLI before setup. Dashboard uses AzureCliCredential pinned to this tenant and subscription, never interactive sign-in, account keys, or a default credential fallback."));
        help.Children.Add(Text("Requirements: existing public Azure Blob account with Shared Key authorization enabled for service SAS, outbound HTTPS, Storage Blob Data Owner (or custom setAcl permission), and management listServiceSas/action. Dashboard does not create accounts or grant roles."));
        help.Children.Add(Text("The private container has no anonymous access. The package contains an HTTPS, single-blob, stored-policy SAS and a public signing key. Treat it as a secret: anyone holding it can read the signed tunnel address. It does not authenticate senders to an anonymous tunnel."));
        help.Children.Add(Text("Policies last 90 days and renew with fewer than 30 days remaining on startup and daily while Dashboard runs. Signed documents expire after 24 hours and refresh every six hours. Rotation temporarily keeps both reader groups: distribute the new package, then explicitly retire the previous group. Closed/offline Dashboard cannot renew. Revocation may take 30 seconds; stopping sharing or disabling publishing is not SAS revocation."));
        help.Children.Add(Text("Copy disables clipboard history and cloud roaming; Dashboard does not flush the package into persistent clipboard storage. Removing discovery revokes the policy and deletes only the owned discovery blob; an empty owned container remains as a tombstone. Foreign edits are never overwritten."));

        void Update()
        {
            var current = _runtime.Discovery.State;
            var enabled = !busy && !_exiting && !cancellation.IsCancellationRequested;
            status.Text = current.Message + (current.PolicyExpiresAtUtc is { } expiry ? $" Policy expires {expiry:u}." : "") +
                (current.PublishedAtUtc is { } published ? $" Last published {published:u}." : "") +
                (current.ReaderGroupId is { } reader ? $" Reader group: {reader}." : "") +
                (current.PreviousReaderGroupId is { } previous ? $" Previous reader group awaiting retirement: {previous}." : "");
            if (current.PublicationPending) status.Text += " Publication pending; last publication is not proof of current availability.";
            if (current.PolicyExpiresAtUtc is { } policyExpiry && policyExpiry < DateTimeOffset.UtcNow.AddDays(30))
                status.Text += " Renewal warning: fewer than 30 days remain. Keep Dashboard running with Azure access or renew explicitly.";
            preview.Text = current.Preview is { } planned
                ? $"Preview (expires {planned.ExpiresAtUtc:u}): tenant {planned.Target.TenantId:D}; subscription {planned.Target.SubscriptionId:D}; resource group {planned.Target.ResourceGroup}; Blob endpoint {planned.Target.Endpoint}; public networking {planned.Account.PublicNetworkAccess}; default network access {planned.Account.DefaultNetworkAction}; Shared Key authorization allowed {planned.Account.SharedKeyAllowed}; SAS expiration action {planned.Account.SasExpirationAction}, maximum interval {planned.Account.SasMaximumInterval?.ToString() ?? "not configured"}; private container {planned.Container}; blob endpoint.json; read-only policy {planned.PolicyId}; owner marker {planned.Owner}. Creates current-user-only DPAPI state private-discovery.dpapi under Dashboard data directory. Policy maintenance authorized for 90-day validity and automatic renewal below 30 days. Azure storage and transaction charges apply. No account policies, networking or roles will be changed." : "";
            inspect.IsEnabled = enabled && !current.Configured && !current.Recoverable && _runningMode == DashboardConnectionMode.DevTunnel;
            provision.IsEnabled = enabled && current.Preview is not null && createConsent.IsChecked == true;
            copy.IsEnabled = enabled && current.Configured && !current.Recoverable && !current.Revoked;
            recover.IsEnabled = enabled && current.Configured;
            renew.IsEnabled = refresh.IsEnabled = testReader.IsEnabled = publishing.IsEnabled = enabled && current.Configured && !current.Recoverable && !current.Revoked;
            publishing.Content = current.PublishingEnabled ? "Disable publishing (does not revoke readers)" : "Enable publishing";
            rotate.IsEnabled = enabled && current.Configured && !current.Recoverable && !current.Revoked && current.PreviousReaderGroupId is null && destructiveConsent.IsChecked == true;
            retire.IsEnabled = enabled && current.Configured && !current.Recoverable && !current.Revoked && current.PreviousReaderGroupId is not null && destructiveConsent.IsChecked == true;
            revoke.IsEnabled = remove.IsEnabled = enabled && current.Configured && destructiveConsent.IsChecked == true;
            foreach (var control in new[] { tenant, subscription, group, account }) control.IsEnabled = enabled && !current.Configured;
        }
        async Task Run(Func<CancellationToken, Task<RuntimeResult<PrivateDiscoveryStatus>>> action)
        {
            if (busy || cancellation.IsCancellationRequested) return;
            busy = true;
            outcome.Text = "Working…";
            Update();
            try
            {
                var result = await action(cancellation.Token);
                if (!cancellation.IsCancellationRequested)
                    outcome.Text = result.Succeeded ? "Completed. Review the updated status above." :
                        RuntimeFailureMessage(result.Error!) + " Recovery may be required; do not delete the discovery journal.";
            }
            catch (Exception error) when (error is ArgumentException or FormatException or DiscoveryException or OperationCanceledException)
            {
                if (!cancellation.IsCancellationRequested) outcome.Text = "Check the Azure target fields and retry. No automatic sign-in or permission changes are performed.";
            }
            finally
            {
                busy = false;
                if (!cancellation.IsCancellationRequested)
                {
                    createConsent.IsChecked = destructiveConsent.IsChecked = false;
                    Update();
                }
            }
        }
        inspect.Click += async (_, _) => await Run(token => _runtime.PreviewDiscoveryAsync(new(
            Guid.Parse(tenant.Text.Trim()), Guid.Parse(subscription.Text.Trim()), group.Text.Trim(), account.Text.Trim()), token));
        provision.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Provision,
            createConsent.IsChecked == true, _runtime.Discovery.State.Preview?.ConfirmationId, token));
        recover.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Recover, token: token));
        renew.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Renew, token: token));
        refresh.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Refresh, token: token));
        testReader.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.TestReader, token: token));
        publishing.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(_runtime.Discovery.State.PublishingEnabled
            ? RuntimeDiscoveryOperation.DisablePublishing : RuntimeDiscoveryOperation.EnablePublishing, token: token));
        rotate.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Rotate, destructiveConsent.IsChecked == true, token: token));
        retire.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.RetirePrevious, destructiveConsent.IsChecked == true, token: token));
        revoke.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Revoke, destructiveConsent.IsChecked == true, token: token));
        remove.Click += async (_, _) => await Run(token => _runtime.DiscoveryAsync(RuntimeDiscoveryOperation.Remove, destructiveConsent.IsChecked == true, token: token));
        copy.Click += (_, _) =>
        {
            try
            {
                var package = new DataPackage();
                package.SetText(_runtime.CopyDiscoveryPackage());
                var copied = Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
                outcome.Text = copied ? "Connection package copied without clipboard history or cloud roaming." : "Clipboard is unavailable. Retry copying.";
            }
            catch (Exception error) when (error is DiscoveryException or RuntimeCommandException or System.Runtime.InteropServices.COMException)
            { outcome.Text = "Package unavailable. Renew or recover discovery, then retry."; }
        };
        createConsent.Checked += (_, _) => Update();
        createConsent.Unchecked += (_, _) => Update();
        destructiveConsent.Checked += (_, _) => Update();
        destructiveConsent.Unchecked += (_, _) => Update();
        _updateDiscoveryControls = Update;
        Update();
    }

    private void ReleaseDiscoverySettings()
    {
        _updateDiscoveryControls = null;
        _discoverySettingsLifetime?.Cancel();
        _discoverySettingsLifetime?.Dispose();
        _discoverySettingsLifetime = null;
    }
}