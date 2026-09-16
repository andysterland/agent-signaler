using System.Collections.ObjectModel;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentSignaler.Configurator;

public sealed partial class MainWindow : Window
{
    private static string ConfigPath => Program.ConfigPath;
    private static string SettingsPath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "configurator-settings.json");
    private static string IntegrationRecoveryPath => Path.Combine(Path.GetDirectoryName(ConfigPath)!, "integration-recovery.json");
    private readonly ObservableCollection<HookTargetRow> rows = [];
    private (TabViewItem Tab, FrameworkElement Element)? navigationTarget;
    private Guid machineId;
    private bool busy;
    private bool closed;
    private CancellationTokenSource? connectionTest;
    private CancellationTokenSource? discoveryCancellation;
    private readonly IntegrationManager integration;
    private readonly MultiTargetIntegrationManager multiIntegration;
    private readonly IIntegrationRuntime runtime = new ClientIntegrationRuntime();
    private readonly IIntegrationStartup startup = new WindowsIntegrationStartup();

    public MainWindow()
    {
        InitializeComponent();
        HooksGrid.ItemsSource = rows;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "AgentSignaler.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1050, 960));
        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        Title = $"Agent Signaler - Remote setup (v{version})";
        HeadingText.Text = $"Agent Signaler (v{version})";
        VersionText.Text = $"Remote integration setup - v{version}";
        integration = new IntegrationManager(new WindowsTaskScheduler(), VerifyDelivery, startup, runtime);
        multiIntegration = new MultiTargetIntegrationManager(new WindowsTaskScheduler(), VerifyDelivery, startup, runtime);
        ConfigPathText.Text = $"Configuration: {ConfigPath}";
        RelayPathBox.Text = Path.Combine(AppContext.BaseDirectory, "AgentSignaler.Relay.exe");
        RelayPathText.Text = $"Relay: {Path.Combine(AppContext.BaseDirectory, "AgentSignaler.Relay.exe")}";
        RootPanel.Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            closed = true;
            connectionTest?.Cancel();
            discoveryCancellation?.Cancel();
        };
    }

    private async Task InitializeAsync()
    {
        SetBusy(true);
        try
        {
            machineId = MachineIdentity.GetOrCreate(Path.GetDirectoryName(ConfigPath)!);
            IdentityBox.Text = machineId.ToString("D");
            if (File.Exists(ConfigPath))
            {
                var config = RemoteConfiguration.Load(ConfigPath);
                if (config.MachineId != machineId) throw new InvalidDataException("Configuration and persistent identity disagree.");
                UrlBox.Text = config.Endpoint.GetLeftPart(UriPartial.Authority);
                HeartbeatBox.Value = config.HeartbeatIntervalSeconds / 60;
                RelayPathBox.Text = config.RelayPath ?? Path.Combine(AppContext.BaseDirectory, "AgentSignaler.Relay.exe");
            }
            try
            {
                var savedUrl = ConfiguratorSettings.LoadUrl(SettingsPath, machineId);
                if (savedUrl is not null) UrlBox.Text = savedUrl;
            }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex))
            {
                ShowError(ex, "Saved dashboard URL could not be loaded");
            }
            await RefreshAsync();
            if (HookVerification.HasPendingCleanup(ConfigPath))
                ShowStatus("An interrupted diagnostic has pending cleanup. Use Clean up legacy diagnostic hooks on Review & maintenance before applying settings.",
                    InfoBarSeverity.Warning, "Diagnostic cleanup pending");
            else if (File.Exists(IntegrationRecoveryPath))
                ShowStatus("An integration transaction needs recovery. Preserve its journal and backups, resolve conflicting user edits, then choose Recover interrupted integration transaction.",
                    InfoBarSeverity.Warning, "Integration recovery pending");
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async Task RefreshAsync(bool preserveSelection = true)
    {
        InvalidatePreview();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        discoveryCancellation = timeout;
        UpdateActionStates();
        IReadOnlyList<IntegrationTarget> targets;
        try { targets = await Discovery.InspectTargetsAsync(ConfigPath, cancellationToken: timeout.Token); }
        finally
        {
            discoveryCancellation = null;
            UpdateActionStates();
        }
        var config = File.Exists(ConfigPath) ? RemoteConfiguration.Load(ConfigPath) : null;
        var savedRelayPath = config?.RelayPath ?? Path.Combine(AppContext.BaseDirectory, "AgentSignaler.Relay.exe");
        RelayPathText.Text = $"Relay: {savedRelayPath}";
        if (!preserveSelection) RelayPathBox.Text = savedRelayPath;
        var pending = preserveSelection
            ? rows.Where(r => r.Target is not null).ToDictionary(r => r.Target!.Id, r => r.Enabled, StringComparer.Ordinal)
            : new Dictionary<string, bool>(StringComparer.Ordinal);
        var drafts = preserveSelection ? rows.Where(r => r.IsDraft).ToArray() : [];
        var saved = config?.Integrations ?? [];
        var manifestPath = Path.Combine(Path.GetDirectoryName(ConfigPath)!, "integration.json");
        IntegrationManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            manifest = JsonSerializer.Deserialize<IntegrationManifest>(AtomicFile.ReadBounded(manifestPath, 4194304), Protocol.Json);
            if (manifest is null || manifest.MachineId != machineId)
                throw new InvalidDataException("The ownership manifest cannot be matched to this machine.");
        }
        rows.Clear();
        foreach (var target in targets)
        {
            var legacyOwned = manifest is { Version: 1 } &&
                string.Equals(manifest.HookPath, Path.Combine(target.HookDirectory, "agent-signaler.json"), StringComparison.OrdinalIgnoreCase);
            var row = new HookTargetRow(target, saved.Any(t => t.Id == target.Id) || legacyOwned);
            if (pending.TryGetValue(target.Id, out var selected)) row.Enabled = selected;
            AddRow(row);
        }
        foreach (var draft in drafts) rows.Add(draft);
        DiscoveryText.Text = $"{targets.Count} Copilot locations; {saved.Count} enabled in saved settings.";
        UpdateHookSelection();
        await RefreshRuntimeAsync();
    }

    private void AddRow(HookTargetRow row)
    {
        row.PropertyChanged += (_, _) =>
        {
            InvalidatePreview();
            UpdateHookSelection();
        };
        rows.Add(row);
    }

    private void UpdateHookSelection()
    {
        var pending = rows.Count(r => r.HasPendingChange);
        HookSelectionText.Text = $"{rows.Count(r => r.Enabled)} selected; {pending} unsaved selection changes.";
    }

    private void IntegrationKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Virtualized ComboBoxes clear their selection while rebinding; that is not a user edit.
        if (sender is ComboBox { DataContext: HookTargetRow row, SelectedItem: string kind } && row.IsDraft)
            row.Kind = kind;
    }

    private void AddLocation_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var row = new HookTargetRow();
        AddRow(row);
        HooksGrid.SelectedItem = row;
        HooksGrid.ScrollIntoView(row, null);
    }

    private IReadOnlyList<IntegrationTarget> SelectedTargets()
    {
        var selected = new List<IntegrationTarget>();
        foreach (var row in rows.Where(r => r.Enabled))
        {
            try
            {
                var target = row.Prepare(ConfigPath);
                if (row.IsDraft && rows.Any(r => r.Target is { } existing && existing.Kind == target.Kind &&
                    string.Equals(existing.HookDirectory, target.HookDirectory, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("This location already has a row. Enable that row instead of adding a custom duplicate.");
                if (selected.Any(t => t.Id == target.Id ||
                    (t.IsCustom || target.IsCustom) && t.Kind == target.Kind &&
                    string.Equals(t.HookDirectory, target.HookDirectory, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("This location is selected more than once. Enable its existing row instead of adding a duplicate.");
                selected.Add(target);
            }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex))
            {
                HooksGrid.SelectedItem = row;
                HooksGrid.ScrollIntoView(row, null);
                throw new InputValidationException(new InvalidOperationException($"{row.DisplayName}: {ex.Message}", ex), HooksTab, HooksGrid);
            }
        }
        return selected;
    }

    private MultiTargetIntegrationPlan CreatePlan()
    {
        var config = ReadConfiguration();
        return MultiTargetIntegrationManager.Preview(config, ConfigPath, SelectedTargets(), config.RelayPath!);
    }

    private async Task RefreshRuntimeAsync()
    {
        if (!File.Exists(ConfigPath))
        {
            RuntimeText.Text = "No saved settings. Client is not configured.";
            return;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var state = await runtime.QueryAsync(ConfigPath, timeout.Token);
        var saved = RemoteConfiguration.Load(ConfigPath);
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        RuntimeText.Text = $"Saved heartbeat: {saved.HeartbeatIntervalSeconds / 60} minutes. " +
            (state.Running ? $"Client running; effective heartbeat: {(state.HeartbeatIntervalSeconds is { } seconds ? $"{seconds / 60} minutes" : "unknown")}; " +
                $"effective revision: {state.EffectiveRevision ?? "unknown"}. " +
                (revision == state.EffectiveRevision ? "Saved settings are effective." : "Saved settings are not yet effective.") :
                "Client not running; saved settings are not active.") +
            (startup.IsDisabled(IntegrationStartup.Name(ConfigPath)) ? " Windows has disabled sign-in startup." : "");
    }

    private async void StartClient_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            var config = RemoteConfiguration.Load(ConfigPath);
            var relayPath = ConfiguratorSettings.RelayLocation(config.RelayPath ??
                Path.Combine(AppContext.BaseDirectory, "AgentSignaler.Relay.exe"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            var state = await runtime.StartAsync(Path.Combine(Path.GetDirectoryName(relayPath)!, "AgentSignaler.Client.exe"),
                ConfigPath, timeout.Token);
            var effective = state.Running && state.EffectiveRevision == ClientConfigurationRevision.Read(ConfigPath);
            ShowStatus(effective ? "Client started with the saved settings. Reporting continues after Configurator closes." :
                state.Running ? "Client is running, but saved settings are not yet acknowledged. " + state.Message :
                "Client did not start. Repair the installation and retry. " + state.Message,
                effective ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            await RefreshRuntimeAsync();
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex, tab: ConnectionTab, action: StartClientButton); }
        finally { SetBusy(false); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try { await RefreshAsync(); }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex, tab: HooksTab, action: RefreshButton); }
        finally { SetBusy(false); }
    }

    private void CancelDiscovery_Click(object sender, RoutedEventArgs e) => discoveryCancellation?.Cancel();

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        InvalidatePreview();
        try
        {
            ShowPreview(CreatePlan().Preview);
            ShowStatus("No files have changed. Apply settings to confirm and configure the selected integrations.", InfoBarSeverity.Informational);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex, tab: ReviewTab, action: PreviewButton); }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var settingsCommitted = false;
        SetBusy(true);
        try
        {
            var approved = CreatePlan();
            ShowPreview(approved.Preview);
            if (!await ConfirmAsync("Apply settings and selected hook integrations?", approved.Preview, "Apply settings")) return;
            var result = await multiIntegration.ApplyAsync(approved, CancellationToken.None);
            settingsCommitted = result.SettingsCommitted;
            InvalidatePreview();
            if (RememberSuccessfulUrl(approved.Config, result.RuntimeApplied ? "Installation/update" : "Saving integration settings"))
                ShowStatus(result.Message, result.RuntimeApplied ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
            await RefreshAsync(preserveSelection: false);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            InvalidatePreview();
            ShowError(ex, settingsCommitted ? "Settings saved; status refresh failed" : "Apply settings failed",
                ReviewTab, PreviewButton);
        }
        finally { SetBusy(false); }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        ShowStatus("Testing dashboard connection...", InfoBarSeverity.Informational);
        try
        {
            var config = ReadConfiguration();
            using var timeout = new CancellationTokenSource(DashboardConnection.TestTimeout);
            connectionTest = timeout;
            UpdateActionStates();
            using var client = RemoteHttpTransport.CreateClient(config, interactive: true);
            await DashboardConnection.TestAsync(config, client, timeout.Token);
            if (RememberSuccessfulUrl(config, "Connection test"))
                ShowStatus("Dashboard is reachable and supports this protocol. URL saved for next time. Active integration configuration has not been changed.",
                    InfoBarSeverity.Success, "Connection test succeeded");
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            ShowError(ex, ex is OperationCanceledException
                ? "Connection test cancelled or timed out" : "Connection test failed",
                ConnectionTab, TestButton);
        }
        finally
        {
            connectionTest = null;
            SetBusy(false);
        }
    }

    private void CancelTest_Click(object sender, RoutedEventArgs e) => connectionTest?.Cancel();

    private async Task<bool> ConfirmAsync(string title, string preview, string approve)
    {
        if (closed) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = RootPanel.XamlRoot, Title = title,
            Content = new ScrollViewer
            {
                MaxHeight = 450,
                Content = new TextBlock { Text = preview, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            },
            PrimaryButtonText = approve, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        var result = await dialog.ShowAsync();
        return !closed && result == ContentDialogResult.Primary;
    }

    private async void RecoverVerification_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            if (!await ConfirmAsync("Clean up legacy diagnostic hooks?",
                "Remove only unchanged app-owned diagnostic hooks and restore their owned profile settings entries. " +
                "Changed/user-owned files are preserved. This does not install hooks or start Client.", "Clean up diagnostics")) return;
            HookVerification.Recover(ConfigPath);
            await RefreshAsync();
            ShowStatus("Legacy diagnostic hooks cleaned up. You can now apply your selected integrations.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex, "Diagnostic recovery incomplete", ReviewTab, RecoverVerificationButton); }
        finally { SetBusy(false); }
    }

    private bool RememberSuccessfulUrl(RemoteConfiguration config, string operation)
    {
        try
        {
            ConfiguratorSettings.SaveUrl(SettingsPath, config);
            return true;
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            ShowStatus($"{operation} succeeded, but its dashboard URL could not be saved. " +
                $"Check file access to {SettingsPath} and retry.",
                InfoBarSeverity.Error, "Dashboard URL not saved");
            return false;
        }
    }

    private async void RecoverIntegration_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        InvalidatePreview();
        try
        {
            if (!File.Exists(IntegrationRecoveryPath))
            {
                ShowStatus("No pending integration transaction.", InfoBarSeverity.Informational);
                return;
            }
            if (!await ConfirmAsync("Restore the interrupted owned integration transaction?",
                $"Recovery journal:\n{IntegrationRecoveryPath}\n\n" +
                "Restore the prior owned files, profile settings, startup entry and legacy task recorded by the previously approved transaction. " +
                "Concurrent user edits are not overwritten. Preserve the journal and backups if a conflict remains. No stopped Client is started.",
                "Recover owned transaction")) return;
            await Task.Run(() => multiIntegration.RecoverPending(ConfigPath));
            await RefreshAsync(preserveSelection: false);
            ShowStatus("Integration transaction recovered. Check saved versus effective runtime settings.", InfoBarSeverity.Warning);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex, "Integration recovery incomplete", ReviewTab, RecoverIntegrationButton); }
        finally { SetBusy(false); }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            if (!await ConfirmAsync("Remove this user's entire integration?",
                integration.UninstallPreview(ConfigPath), "Remove owned integration")) return;
            await Task.Run(() => integration.Uninstall(ConfigPath));
            InvalidatePreview();
            await RefreshAsync(preserveSelection: false);
            ShowStatus("Owned integration removed. Persistent machine UUID retained.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { ShowError(ex, tab: ReviewTab, action: UninstallButton); }
        finally { SetBusy(false); }
    }

    private static async Task<bool> VerifyDelivery(RemoteConfiguration config, CancellationToken token)
    {
        using var client = RemoteHttpTransport.CreateClient(config, interactive: true);
        await DashboardConnection.TestAsync(config, client, token);
        return true;
    }

    private RemoteConfiguration ReadConfiguration()
    {
        var url = UrlBox.Text?.Trim();
        if (string.IsNullOrEmpty(url))
            throw new InputValidationException(new InvalidDataException("Enter the dashboard base URL."), ConnectionTab, UrlBox);
        var config = File.Exists(ConfigPath) ? RemoteConfiguration.Load(ConfigPath) : new RemoteConfiguration { MachineId = machineId };
        if (config.MachineId != machineId)
            throw new InvalidDataException("Configuration and persistent identity disagree.");
        var relayPath = ValidateInput(() => ConfiguratorSettings.RelayLocation(RelayPathBox.Text), HooksTab, RelayPathBox);
        config = ValidateInput(() => config.WithDashboardUrl(url).ToVersion4(), ConnectionTab, UrlBox);
        var heartbeatSeconds = ValidateInput(() => ConfiguratorSettings.HeartbeatSeconds(HeartbeatBox.Value), ConnectionTab, HeartbeatBox);
        return config with { RelayPath = relayPath, HeartbeatIntervalSeconds = heartbeatSeconds };
    }

    private void HeartbeatChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => InvalidatePreview();
    private void SettingsChanged(object sender, TextChangedEventArgs e) => InvalidatePreview();
    private void InvalidatePreview()
    {
        if (PreviewBox is not null) PreviewBox.Text = "";
    }

    private void SetBusy(bool value)
    {
        busy = value;
        UpdateActionStates();
        if (!value) QueueNavigationFocus();
    }

    private void UpdateActionStates()
    {
        if (closed) return;
        var state = new ConfiguratorActionState(busy, discoveryCancellation is not null, connectionTest is not null);
        UrlBox.IsEnabled = HeartbeatBox.IsEnabled = TestButton.IsEnabled = StartClientButton.IsEnabled = state.CanUseConnection;
        RelayPathBox.IsEnabled = RefreshButton.IsEnabled = AddLocationButton.IsEnabled = HooksGrid.IsEnabled = PreviewButton.IsEnabled =
            RecoverVerificationButton.IsEnabled = RecoverIntegrationButton.IsEnabled = UninstallButton.IsEnabled = state.CanChangeIntegration;
        ApplyButton.IsEnabled = state.CanApply;
        CancelDiscoveryButton.IsEnabled = state.CanCancelDiscovery;
        CancelTestButton.IsEnabled = state.CanCancelTest;
    }

    private void NavigateTo(TabViewItem tab, FrameworkElement? element = null)
    {
        if (closed) return;
        TaskTabs.SelectedItem = tab;
        navigationTarget = element is null ? null : (tab, element);
        QueueNavigationFocus();
    }

    private void QueueNavigationFocus()
    {
        if (closed || navigationTarget is not { } target) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (closed || navigationTarget != target || !ReferenceEquals(TaskTabs.SelectedItem, target.Tab)) return;
            RootPanel.UpdateLayout();
            target.Element.StartBringIntoView();
            // Consent dialogs can temporarily disable the control awaiting focus.
            if (busy) return;
            if (target.Element is Control control) control.Focus(FocusState.Programmatic);
            navigationTarget = null;
        });
    }

    private void ShowPreview(string preview)
    {
        PreviewBox.Text = preview;
        NavigateTo(ReviewTab, PreviewBox);
    }

    private sealed class InputValidationException(Exception error, TabViewItem tab, Control input)
        : InvalidOperationException(error.Message, error)
    {
        internal TabViewItem Tab { get; } = tab;
        internal Control Input { get; } = input;
    }

    private static T ValidateInput<T>(Func<T> validate, TabViewItem tab, Control input)
    {
        try { return validate(); }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { throw new InputValidationException(ex, tab, input); }
    }

    private void ShowError(Exception ex, string title = "", TabViewItem? tab = null, FrameworkElement? action = null)
    {
        if (ex is InputValidationException input) NavigateTo(input.Tab, input.Input);
        else if (tab is not null) NavigateTo(tab, action);
        ShowStatus(ex is AggregateException ?
            "Rollback was incomplete. Preserve the timestamped .agent-signaler.*.backup files for recovery." :
            ex is HttpRequestException http ? DashboardConnection.DescribeFailure(http) :
            ex is OperationCanceledException ? "Operation cancelled or timed out. Check connectivity and retry when ready." :
            ex is InvalidDataException or InvalidOperationException ? ex.Message :
            RemoteFailure.DescribeLocalFailure(ex),
            InfoBarSeverity.Error, title);
    }

    private void ShowStatus(string message, InfoBarSeverity severity, string title = "")
    {
        if (closed) return;
        StatusBar.Title = title;
        StatusText.Text = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StatusBar,
            string.IsNullOrEmpty(title) ? message : $"{title}: {message}");
        var peer = Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(StatusBar)
            ?? Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(StatusBar);
        peer?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
