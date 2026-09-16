using AgentSignaler.Tunneling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow
{
    private readonly List<PrerequisiteCheck> _prerequisiteChecks = [];
    private PrerequisiteCheck? _azureCliCheck;
    private PrerequisiteCheck? _devCenterExtensionCheck;
    private Action? _updatePrerequisiteControls;
    private bool _prerequisitesClosing;
    private bool AzurePrerequisiteBusy => _azureCliCheck?.IsBusy == true || _devCenterExtensionCheck?.IsBusy == true;
    private bool PrerequisiteBusy => _prerequisiteChecks.Any(check => check.IsBusy);

    private (TextBox TunnelPath, TextBox AzurePath) BuildPrerequisiteSettings(StackPanel panel, StackPanel help)
    {
        _prerequisitesClosing = false;
        panel.Children.Add(Text("Prerequisite", 18));
        var checkAll = new Button { Content = "Check all" };
        var cancelAll = new Button { Content = "Cancel all checks", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(checkAll, "CheckAllPrerequisites");
        AutomationProperties.SetAutomationId(cancelAll, "CancelAllPrerequisites");
        panel.Children.Add(checkAll);
        panel.Children.Add(cancelAll);
        var summaries = new StackPanel { Spacing = 6 };
        panel.Children.Add(summaries);
        var captures = new Dictionary<PrerequisiteCheck, Func<Func<CancellationToken, Task<PrerequisiteDiagnosticResult>>>>();
        help.Children.Add(Text("Checks inspect only: they never install software, start sign-in, launch a connection, or change Internet sharing. " +
            "They are available in either connection mode, including while sharing is active."));
        help.Children.Add(Text("Checks execute the paths entered here, including unsaved edits. Results retain their tested path; " +
            "editing a path does not retest it. Save, Exit and reopen to change the paths used by running operations."));

        panel.Children.Add(Text("Dev Tunnels CLI", 18));
        var tunnelPath = new TextBox
        {
            Header = "Optional absolute devtunnel.exe path (restart required)",
            Text = _settings.DevTunnelCliPath ?? "",
            PlaceholderText = "Default: Microsoft WinGet or prerequisite installation"
        };
        panel.Children.Add(tunnelPath);
        panel.Children.Add(Text($"Effective sharing CLI path: {_effectiveTunnelCliPath ?? "not initialized (Internet mode and restart required)"}."));
        help.Children.Add(Text("Dev Tunnels CLI is separate from Azure CLI and its devcenter extension. " +
            "Install the Microsoft-signed CLI separately with \"winget install Microsoft.devtunnel\", then sign in explicitly with \"devtunnel user login\". " +
            "When using a custom path, run that executable for setup. The CLI credential cache may be shared with other CLI sessions."));
        var tunnelCheck = AddCheck("Dev Tunnels CLI", () =>
        {
            var selectedPath = tunnelPath.Text.Trim();
            return token => DevTunnelDiagnostics.CheckAsync(selectedPath, token);
        }, () => true);

        panel.Children.Add(Text("Azure CLI", 18));
        var azurePath = new TextBox
        {
            Header = "Optional absolute az.exe or az.cmd path (restart required)",
            Text = _settings.AzureCliPath ?? "",
            PlaceholderText = "Default: Azure CLI installation in Program Files"
        };
        panel.Children.Add(azurePath);
        panel.Children.Add(Text($"Effective Dev Box CLI path: {_effectiveAzureCliPath ?? "not initialized"}."));
        help.Children.Add(Text("Install Azure CLI 2.90.0 or later separately using Microsoft's Azure CLI installer. " +
            "Choose only an installation you trust: checks execute it with your Windows account. Leave blank for the default installation."));
        help.Children.Add(Text("az.cmd must be the official MSI wbin launcher with its adjacent Python runtime. " +
            "The entire installation, including CLI modules and dependencies, must be protected from untrusted changes. " +
            "Azure CLI signatures and executable architecture are not inspected."));
        help.Children.Add(Text("The account check validates the CLI's saved user, tenant, and enabled subscription, " +
            "not live token validity or Dev Box permissions. Sign in separately with az login if needed. " +
            "Machine mappings and explicit tenant-specific sign-in remain in machine details."));
        var azureCheck = _azureCliCheck = AddCheck("Azure CLI status", () =>
        {
            var selectedPath = azurePath.Text.Trim();
            return token => AzureCliDiagnostics.CheckAsync(selectedPath, token);
        }, () => !AzurePrerequisiteBusy && _catalog?.State.IsBusy != true);

        panel.Children.Add(Text("Azure CLI devcenter extension", 18));
        help.Children.Add(Text("The Azure CLI devcenter extension is required only for discovery by Dev Center name, not automatic or subscription-ID discovery. " +
            "Check uses the Azure CLI path above and lists locally installed extensions without installing any. " +
            "If missing, install separately in that CLI with \"az extension add --name devcenter\"."));
        var extensionCheck = _devCenterExtensionCheck = AddCheck("devcenter extension", () =>
        {
            var selectedPath = azurePath.Text.Trim();
            return token => AzureCliDiagnostics.CheckDevCenterExtensionAsync(selectedPath, token);
        }, () => !AzurePrerequisiteBusy && _catalog?.State.IsBusy != true);

        panel.Children.Add(Text("Windows App", 18));
        help.Children.Add(Text("Install or update Windows App separately from Microsoft Store. Version 2.0.804.0 or later is required. " +
            "Check inspects the current user's ms-cloudpc association only; it does not verify the app version or launch Windows App."));
        var windowsCheck = AddCheck("Windows App protocol", () => WindowsAppDiagnostics.CheckAsync, () => true);

        checkAll.Click += async (_, _) =>
        {
            var operations = captures.ToDictionary(pair => pair.Key, pair => pair.Value());
            var azureRun = azureCheck.RunAsync(operations[azureCheck]);
            // Azure diagnostics share CLI state; reserve the extension check while the account check runs.
            await Task.WhenAll(azureRun, extensionCheck.RunAsync(operations[extensionCheck], azureRun),
                tunnelCheck.RunAsync(operations[tunnelCheck]), windowsCheck.RunAsync(operations[windowsCheck]));
        };
        cancelAll.Click += (_, _) =>
        {
            foreach (var check in _prerequisiteChecks) check.Cancel();
        };

        _updatePrerequisiteControls += () =>
        {
            checkAll.IsEnabled = !_exiting && !_prerequisitesClosing && !PrerequisiteBusy && _catalog?.State.IsBusy != true;
            cancelAll.Visibility = PrerequisiteBusy ? Visibility.Visible : Visibility.Collapsed;
            cancelAll.IsEnabled = !_exiting && !_prerequisitesClosing;
            tunnelPath.IsEnabled = !_exiting && !_prerequisitesClosing && !tunnelCheck.IsBusy;
            azurePath.IsEnabled = !_exiting && !_prerequisitesClosing && !AzurePrerequisiteBusy && _catalog?.State.IsBusy != true;
            if (_activeDialog is not null)
                _activeDialog.IsPrimaryButtonEnabled = !_exiting && !_prerequisitesClosing && !PrerequisiteBusy && _catalog?.State.IsBusy != true;
        };
        _updatePrerequisiteControls?.Invoke();
        return (tunnelPath, azurePath);

        PrerequisiteCheck AddCheck(string name, Func<Func<CancellationToken, Task<PrerequisiteDiagnosticResult>>> capture, Func<bool> available)
        {
            var check = new PrerequisiteCheck(name);
            _prerequisiteChecks.Add(check);
            captures.Add(check, capture);
            var summary = new Grid { ColumnSpacing = 8 };
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var progress = new ProgressRing
            {
                Width = 20, Height = 20, MinWidth = 0, MinHeight = 0,
                IsActive = false, Visibility = Visibility.Collapsed
            };
            var icon = new FontIcon { FontSize = 16, Visibility = Visibility.Collapsed };
            var summaryText = Text("");
            summaryText.TextWrapping = TextWrapping.NoWrap;
            summaryText.TextTrimming = TextTrimming.CharacterEllipsis;
            summaryText.VerticalAlignment = VerticalAlignment.Center;
            summaryText.IsTextSelectionEnabled = true;
            AutomationProperties.SetAutomationId(summaryText, $"PrerequisiteSummary-{name}");
            AutomationProperties.SetLiveSetting(summaryText, AutomationLiveSetting.Polite);
            AutomationProperties.SetAccessibilityView(progress, AccessibilityView.Raw);
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            Grid.SetColumn(summaryText, 1);
            summary.Children.Add(progress);
            summary.Children.Add(icon);
            summary.Children.Add(summaryText);
            summaries.Children.Add(summary);
            var run = new Button { Content = $"Check {name}" };
            var cancel = new Button { Content = $"Cancel {name} check", Visibility = Visibility.Collapsed };
            var status = Text(check.Result);
            status.IsTextSelectionEnabled = true;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            panel.Children.Add(run);
            panel.Children.Add(cancel);
            panel.Children.Add(status);
            _updatePrerequisiteControls += () =>
            {
                run.IsEnabled = !_exiting && !_prerequisitesClosing && !check.IsBusy && available();
                cancel.Visibility = check.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                status.Text = check.Result;
                summaryText.Text = $"{name}: {check.Summary}";
                ToolTipService.SetToolTip(summaryText, summaryText.Text);
                progress.IsActive = check.IsBusy;
                progress.Visibility = check.IsBusy ? Visibility.Visible : Visibility.Collapsed;
                icon.Visibility = check.State is PrerequisiteCheckState.Passed or PrerequisiteCheckState.Failed
                    ? Visibility.Visible : Visibility.Collapsed;
                icon.Glyph = check.State == PrerequisiteCheckState.Passed ? "\uE73E" : "\uE711";
                icon.Foreground = (Brush)Application.Current.Resources[check.State == PrerequisiteCheckState.Passed
                    ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
            };
            check.Changed += () =>
            {
                _updatePrerequisiteControls?.Invoke();
                _updateDevBoxSettingsControls?.Invoke();
            };
            run.Click += async (_, _) => await check.RunAsync(capture());
            cancel.Click += (_, _) => check.Cancel();
            return check;
        }
    }

    private async Task CancelPrerequisiteChecksAsync()
    {
        _prerequisitesClosing = true;
        await Task.WhenAll(_prerequisiteChecks.Select(check => check.CancelAndWaitAsync()));
    }

    private void ReleasePrerequisiteSettings()
    {
        _updatePrerequisiteControls = null;
        _azureCliCheck = _devCenterExtensionCheck = null;
        _prerequisiteChecks.Clear();
    }
}
