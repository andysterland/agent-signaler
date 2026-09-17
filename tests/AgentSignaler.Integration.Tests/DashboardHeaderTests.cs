using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class DashboardHeaderTests
{
    [Fact]
    public void UrlIsUnderSubtitleWithAccessibleIconOnlyCopyAndOverflowProtection()
    {
        var main = MainWindowSource();
        Assert.True(main.IndexOf("brand.Children.Add(_machineCount)", StringComparison.Ordinal) <
            main.IndexOf("brand.Children.Add(address)", StringComparison.Ordinal));
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Left", main);
        Assert.Contains("_host.TextWrapping = TextWrapping.NoWrap", main);
        Assert.Contains("_host.TextTrimming = TextTrimming.CharacterEllipsis", main);
        Assert.Contains("new SymbolIcon(Symbol.Copy)", main);
        Assert.Contains("Grid.SetColumn(_copyUrl, 1)", main);
        Assert.Contains("AutomationProperties.SetName(_copyUrl, \"Copy effective dashboard connection URL\")", main);
        Assert.Contains("ToolTipService.SetToolTip(_copyUrl, \"Copy URL\")", main);
        Assert.Contains("package.SetText(copyUrl)", main);
        Assert.DoesNotContain("AddRow(address,", main);
    }

    [Fact]
    public void HeaderActionsKeepLabelsGlyphsAndNarrowLayout()
    {
        var main = MainWindowSource();
        Assert.Contains("ActionContent(Symbol.View, \"Compact View\")", main);
        Assert.Contains("ActionContent(Symbol.Setting, \"Settings\")", main);
        Assert.Contains("Grid.SetRow(actions, narrow ? 1 : 0)", main);
        Assert.Contains("Grid.SetColumnSpan(brand, narrow ? 2 : 1)", main);
        Assert.Contains("presenter.Minimize()", main);
        Assert.Contains("settings.Click += async (_, _) => await ShowSettingsAsync()", main);
        Assert.Contains("new SizeInt32((int)(900 * scale), (int)(600 * scale))", main);
        Assert.Contains("new SizeInt32((int)(1040 * scale), (int)(640 * scale))", main);
    }

    [Fact]
    public void SettingsRemainAvailableDuringStartup()
    {
        var main = MainWindowSource();
        Assert.Contains("private readonly Button _settingsButton = new();", main);
        Assert.DoesNotContain("_settingsButton.IsEnabled =", main);
        var settings = main[main.IndexOf("private async Task ShowSettingsAsync()", StringComparison.Ordinal)..];
        Assert.Contains("if (_dialogOpen || _exiting) return;", settings);
        Assert.DoesNotContain("_startup.IsLoading", settings);
    }

    [Fact]
    public void CopyButtonIsHiddenUntilCopyableUrlExists()
    {
        var main = MainWindowSource();
        Assert.Contains("Content = new SymbolIcon(Symbol.Copy), IsEnabled = false, Visibility = Visibility.Collapsed", main);
        var tunneling = MainWindowSource("MainWindow.Tunneling.cs");
        Assert.Contains("_copyUrl.Visibility = view.CopyUrl is not null ? Visibility.Visible : Visibility.Collapsed;", tunneling);
        Assert.Contains("_copyUrl.IsEnabled = view.CopyUrl is not null;", tunneling);
    }

    [Fact]
    public void StartupStatusAppearsBelowProgressAndTracksWorkUntilCompletion()
    {
        var main = MainWindowSource();
        Assert.True(main.IndexOf("_startupPanel.Children.Add(_startupProgress)", StringComparison.Ordinal) <
            main.IndexOf("_startupPanel.Children.Add(_startupStatus)", StringComparison.Ordinal));
        Assert.Contains("_startupPanel.Visibility = _startup.IsLoading ? Visibility.Visible : Visibility.Collapsed;", main);
        Assert.Contains("if (!_exiting && _startup.IsLoading) _startupStatus.Text = message;", main);
        Assert.Contains("await _startup.RunAsync(InitializeCoreAsync, _tunnelLifetime.Token)", main);
        var forwarding = main[main.IndexOf("_runtime.Changed += change =>", StringComparison.Ordinal)..];
        AssertBefore(forwarding, "var progress = change.Domain switch", "DispatcherQueue.TryEnqueue(() =>");
        Assert.Contains("DashboardStartupController.ProgressMessage(_runtime.Status.State.Stage,", forwarding);
        Assert.Contains("\"sharing\" => _runtime.Sharing.State.Message", forwarding);
        Assert.Contains("if (progress is not null) ReportStartupProgress(progress);", forwarding);
        AssertBefore(forwarding, "ReportStartupProgress(\"Loading computer tiles...\")", "_refreshTask = RefreshAsync();");

        var runtime = MainWindowSource("DashboardRuntime.cs", "AgentSignaler.Dashboard.Core");
        AssertBefore(runtime, "Stage = \"receiver\"", "await server.StartAsync(token)");
        AssertBefore(runtime, "Stage = \"machines\"", "await RefreshMachinesAsync(token)");
        AssertBefore(runtime, "Stage = \"sharing\"", "await SharingAsync(RuntimeSharingOperation.Start, true, token)");
    }

    [Theory]
    [InlineData("storage", false, "Opening local machine history...")]
    [InlineData("receiver", false, "Starting the local receiver...")]
    [InlineData("machines", false, "Loading computer tiles...")]
    [InlineData("sharing", true, "Starting the shared public endpoint...")]
    [InlineData("sharing", false, "Preparing Internet sharing...")]
    [InlineData("ready", false, null)]
    public void RuntimeStartupStagesHaveReadableProgressWithoutClaimingDisabledSharingStarts(
        string stage, bool startSharing, string? expected) =>
        Assert.Equal(expected, DashboardStartupController.ProgressMessage(stage, startSharing));

    [Fact]
    public void ExitUsesFaultTolerantDrainThatAlwaysAwaitsRuntimeShutdown()
    {
        var main = MainWindowSource();
        Assert.Contains("DashboardStartupController.DrainForShutdownAsync(", main);
        Assert.Contains("async () => (await _runtime.ShutdownAsync()).Clean", main);
    }

    private static void AssertBefore(string source, string before, string after)
    {
        var first = source.IndexOf(before, StringComparison.Ordinal);
        var second = source.IndexOf(after, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Missing expected source fragment: {before}");
        Assert.True(second > first, $"Expected {before} before {after}");
    }

    private static string MainWindowSource(string fileName = "MainWindow.cs", string project = "AgentSignaler.Dashboard")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "src", project, fileName));
    }
}
