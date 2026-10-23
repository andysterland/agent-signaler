using System.Xml.Linq;
using AgentSignaler.Configurator;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorLayoutSourceTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void NewConnectionShowsEditableDnsHostnameByDefault()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        XElement Named(string name) => document.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == name);
        var mode = Named("ConnectionModeBox");
        var selectedIndex = int.Parse(mode.Attribute("SelectedIndex")!.Value,
            System.Globalization.CultureInfo.InvariantCulture);
        var selection = mode.Elements().ElementAt(selectedIndex);
        Assert.Contains("DNS TXT", (string?)selection.Attribute("Content"));
        Assert.Equal("ConnectionModeChanged", (string?)mode.Attribute("SelectionChanged"));
        Assert.NotEqual("Collapsed", (string?)Named("DnsDiscoveryPanel").Attribute("Visibility"));
        Assert.Equal("Collapsed", (string?)Named("UrlBox").Attribute("Visibility"));
        var hostname = Named("DnsRecordBox");
        Assert.Equal("TextBox", hostname.Name.LocalName);
        Assert.Equal(ConfiguratorSettings.SuggestedDnsRecordName, (string?)hostname.Attribute("Text"));
        Assert.NotEqual("True", (string?)hostname.Attribute("IsReadOnly"));
        Assert.Equal("DnsRecordChanged", (string?)hostname.Attribute("TextChanged"));
        Assert.Equal("Test discovery and connection", (string?)Named("TestButton").Attribute("Content"));
    }

    [Fact]
    public void StartClientIsImmediatelyBeforeApplyInSharedBottomActionGroup()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var start = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "StartClientButton");
        var apply = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "ApplyButton");
        var group = Assert.IsType<XElement>(start.Parent);
        Assert.Same(group, apply.Parent);
        Assert.Equal("StackPanel", group.Name.LocalName);
        Assert.Equal("Horizontal", (string?)group.Attribute("Orientation"));
        Assert.Equal("Right", (string?)group.Attribute("HorizontalAlignment"));
        Assert.Equal(new[] { "StartClientButton", "ApplyButton" },
            group.Elements().Select(element => (string?)element.Attribute(Xaml + "Name")));
        var footer = Assert.IsType<XElement>(group.Parent);
        var root = document.Descendants().Single(element => (string?)element.Attribute(Xaml + "Name") == "RootPanel");
        Assert.Same(root, footer.Parent);
        Assert.Same(footer, root.Elements().Last());
        var rowDefinitions = Assert.IsType<XElement>(root.Element(root.Name.Namespace + "Grid.RowDefinitions"));
        Assert.Equal((rowDefinitions.Elements().Count() - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            (string?)footer.Attribute("Grid.Row"));
        Assert.Equal("StartClient_Click", (string?)start.Attribute("Click"));
        Assert.Equal("Apply_Click", (string?)apply.Attribute("Click"));
    }

    [Fact]
    public void StartupCheckboxStagesPreferenceForConfirmedApplyWithoutImmediateSideEffects()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var checkbox = document.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "StartClientAtSignInCheckBox");
        Assert.Equal("CheckBox", checkbox.Name.LocalName);
        Assert.Equal("StartupChanged", (string?)checkbox.Attribute("Checked"));
        Assert.Equal("StartupChanged", (string?)checkbox.Attribute("Unchecked"));
        Assert.NotEqual("True", (string?)checkbox.Attribute("IsChecked"));
        var source = File.ReadAllText(SourcePath("MainWindow.xaml.cs"));
        Assert.Contains("StartClientAtSignInCheckBox.IsChecked = startup.Read(IntegrationStartup.Name(ConfigPath)) is not null;", source);
        Assert.Contains("startClientAtSignIn: StartClientAtSignInCheckBox.IsChecked == true", source);
        Assert.Contains("private void StartupChanged(object sender, RoutedEventArgs e) => InvalidatePreview();", source);
        Assert.Contains("StartClientAtSignInCheckBox.IsEnabled =", source);
        Assert.Contains("if (!preserveSelection || !startupPreferenceLoaded) LoadStartupPreference();", source);
        Assert.Contains("ApplyButton.IsEnabled = state.CanApply && startupPreferenceLoaded;", source);
        Assert.Contains("private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplySettingsAsync();", source);
        var apply = MethodSource(source, "private async Task<bool> ApplySettingsAsync(");
        Assert.Contains("CancellationTokenSource.CreateLinkedTokenSource(windowLifetime.Token, cancellationToken)", apply);
        AssertBefore(apply, "var approved = CreatePlan();", "ShowPreview(approved.Preview);");
        AssertBefore(apply, "ShowPreview(approved.Preview);",
            "if (!await ConfirmAsync(\"Apply settings and selected hook integrations?\", approved.Preview, \"Apply settings\", applyToken)) return false;");
        AssertBefore(apply, "if (!await ConfirmAsync(", "await multiIntegration.ApplyAsync(approved, applyToken)");
        Assert.Contains("return settingsCommitted;", apply);
        Assert.DoesNotContain("startup.Replace(", source);
    }

    [Fact]
    public void UpdatePreparationRequiresReadyStateAndExplicitSaveDiscardOrCancelBeforeAcknowledgement()
    {
        var source = File.ReadAllText(SourcePath("MainWindow.Updates.cs"));
        Assert.Contains("() => !closed && !busy && savedUpdateState is not null", source);
        Assert.Contains("rows.Any(row => row.HasPendingChange || row.IsDraft)", source);
        var prepare = MethodSource(source, "private async Task<bool> PrepareUpdateAsync(");
        AssertBefore(prepare, "savedUpdateState is null || !startupPreferenceLoaded", "updatePreparing = true;");
        AssertBefore(prepare, "updatePreparing = true;", "if (HasUnsavedUpdateEdits)");
        AssertBefore(prepare, "UpdateActionStates();", "await choice.ShowAsync()");
        Assert.Contains("PrimaryButtonText = \"Save changes\", SecondaryButtonText = \"Discard changes\", CloseButtonText = \"Cancel\"", prepare);
        Assert.Contains("DefaultButton = ContentDialogButton.Close", prepare);
        Assert.Contains("token.Register(() => DispatcherQueue.TryEnqueue(choice.Hide))", prepare);
        AssertBefore(prepare, "var result = await choice.ShowAsync();", "token.ThrowIfCancellationRequested();");
        Assert.Contains("if (!await ApplySettingsAsync(token)) return false;", prepare);
        Assert.Contains("await RefreshAsync(preserveSelection: false);", prepare);
        Assert.Contains("else return false;", prepare);
        AssertBefore(prepare, "if (HasUnsavedUpdateEdits) return false;", "updateHost.RefreshRestartContextAsync(");
        AssertBefore(prepare, "await Task.Run(() => updateHost.RefreshRestartContextAsync(", "prepared = true;");
        AssertBefore(prepare, "prepared = true;", "return true;");
        Assert.Contains("if (!prepared) CancelUpdatePreparation();", prepare);
        Assert.DoesNotContain("Close();", prepare);
    }

    [Fact]
    public void ClientOrdinaryExitClearsPreparationAndCannotAuthorizeCommittedRestart()
    {
        var source = File.ReadAllText(SourcePath("ClientUpdates.cs", "AgentSignaler.Client"));
        var ordinaryExit = MethodSource(source, "public bool TryBeginOrdinaryExit()");
        Assert.Contains("lock (sync)", ordinaryExit);
        Assert.Contains("if (disposed || ordinaryExit) return false;", ordinaryExit);
        Assert.DoesNotContain("commitAccepted", ordinaryExit);
        AssertBefore(ordinaryExit, "ordinaryExit = true;", "prepared = Guid.Empty;");
        AssertBefore(ordinaryExit, "prepared = Guid.Empty;", "lifetime.Cancel();");
        var accept = MethodSource(source, "public bool TryAcceptCommit(");
        Assert.Contains("lock (sync)", accept);
        Assert.Contains("ordinaryExit || lifetime.IsCancellationRequested || prepared != runId", accept);
        Assert.Contains("runtime.Completion.IsCompleted", accept);
        Assert.Contains("runtime.Status().State is \"stopping\" or \"unconfigured\" or \"reloading\"", accept);
        AssertBefore(accept, "return false;", "commitAccepted = true;");
        var prepare = MethodSource(source, "public async Task<bool> PrepareAsync(");
        AssertBefore(prepare, "ordinaryExit || commitAccepted || lifetime.IsCancellationRequested", "prepared = runId;");
        var commit = MethodSource(source, "public Task CommitExitAsync(");
        AssertBefore(commit, "if (!TryAcceptCommit(runId)) throw", "shutdownQueued = true;");
        Assert.Contains("throw new InvalidDataException(\"update_commit_declined\")", commit);
        AssertBefore(commit, "shutdownQueued = true;", "await runtime.StopAsync();");
        Assert.Contains("return !disposed && !ordinaryExit;", source);
        var exitCode = MethodSource(source, "public async Task<int> GetExitCodeAsync()");
        AssertBefore(exitCode, "await Task.WhenAny(shutdown)", "UpdateHandoffPolicy.GetClientExitCode(ordinaryExit, cleanUpdateShutdown,");
        AssertBefore(exitCode, "await Task.WhenAny(runtime.Completion)", "UpdateHandoffPolicy.GetClientExitCode(ordinaryExit, cleanUpdateShutdown,");
        Assert.Contains("lock (sync) cleanUpdateShutdown = response.Accepted;", commit);

        var program = File.ReadAllText(SourcePath("Program.cs", "AgentSignaler.Client"));
        Assert.Contains("() => updates?.CanRequestOrdinaryExit ?? true", program);
        AssertBefore(program, "if (!updates.TryBeginOrdinaryExit()) return;", "var response = await runtime.StopAsync();");
        AssertBefore(program, "window.Run();", "return updates.GetExitCodeAsync().GetAwaiter().GetResult();");
        var tray = File.ReadAllText(SourcePath("TrayWindow.cs", "AgentSignaler.Client"));
        Assert.Contains("AppendMenu(menu, _canExit() ? 0u : 0x0002u, 2, \"Exit\");", tray);
        Assert.Contains("else if (command == 2 && _canExit()) ExitRequested?.Invoke(false);", tray);
        Assert.Contains("if (_canExit()) ExitRequested?.Invoke(false);", tray);
    }

    [Fact]
    public void ClientRestartConsentIsReservedBeforeUpdaterOwnershipTransfer()
    {
        var source = File.ReadAllText(SourcePath("ClientUpdates.cs", "AgentSignaler.Client"));
        Assert.Contains("IUpdateMaintenanceCommitConsent", source[..source.IndexOf('{')]);
        Assert.Contains("CancelPreparation(runId, releaseCommitReservation: true);", source);
        var cancel = MethodSource(source, "private void CancelPreparation(");
        Assert.Contains("commitAccepted = false;", cancel);
        Assert.Contains("shutdownQueued", cancel);

        var maintenance = File.ReadAllText(SourcePath("UpdateMaintenance.cs", "AgentSignaler.Updates"));
        var commit = MethodSource(maintenance, "public async Task CommitAsync(");
        AssertBefore(commit, "await transport.SendAsync(participant, RunId, \"reserve\", budget.Token)",
            "Phase = UpdateMaintenancePhase.Servicing");
        AssertBefore(commit, "throw new InvalidOperationException(\"update_commit_declined\")",
            "Phase = UpdateMaintenancePhase.Servicing");
        var reserved = commit[commit.IndexOf("\"reserve\"", StringComparison.Ordinal)..];
        AssertBefore(reserved, "current = processes.Enumerate(installation);", "Phase = UpdateMaintenancePhase.Servicing");

        var pipe = File.ReadAllText(SourcePath("UpdateMaintenancePipe.cs", "AgentSignaler.Updates"));
        Assert.Contains("case \"reserve\" when prepared == message.RunId && gate.Phase == UpdateMaintenancePhase.Preparing:", pipe);
        Assert.Contains("participant is IUpdateMaintenanceCommitConsent consent ? consent.TryAcceptCommit(message.RunId) :", pipe);
        Assert.Contains("registration.Restart.Application != UpdateApplication.Client;", pipe);
        Assert.Contains("commitReserved = accepted;", pipe);
        Assert.Contains("case \"commit\" when prepared == message.RunId && commitReserved && gate.Phase == UpdateMaintenancePhase.Servicing:", pipe);
    }

    [Fact]
    public void CompletedUninstallHasASeparateMaintenanceActionFromRollbackRecovery()
    {
        var document = XDocument.Load(SourcePath("MainWindow.xaml"));
        var complete = document.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "CompleteUninstallButton");
        var recover = document.Descendants().Single(element =>
            (string?)element.Attribute(Xaml + "Name") == "RecoverIntegrationButton");
        Assert.Equal("Button", complete.Name.LocalName);
        Assert.Equal("Complete finished uninstall...", (string?)complete.Attribute("Content"));
        Assert.Equal("CompleteUninstall_Click", (string?)complete.Attribute("Click"));
        Assert.Equal("RecoverIntegration_Click", (string?)recover.Attribute("Click"));
        Assert.Same(recover.Parent, complete.Parent);
        Assert.Contains(complete.Ancestors(), element => (string?)element.Attribute(Xaml + "Name") == "ReviewTab");
    }

    private static string MethodSource(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing method: {signature}");
        var end = new[] { "\n    private ", "\n    public ", "\n    internal " }
            .Select(prefix => source.IndexOf(prefix, start + signature.Length, StringComparison.Ordinal))
            .Where(index => index >= 0).DefaultIfEmpty(source.Length).Min();
        return source[start..end];
    }

    private static void AssertBefore(string source, string before, string after)
    {
        var first = source.IndexOf(before, StringComparison.Ordinal);
        var second = source.IndexOf(after, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Missing expected source fragment: {before}");
        Assert.True(second > first, $"Expected {before} before {after}");
    }

    private static string SourcePath(string name, string project = "AgentSignaler.Configurator")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root.FullName, "src", project, name);
    }
}
