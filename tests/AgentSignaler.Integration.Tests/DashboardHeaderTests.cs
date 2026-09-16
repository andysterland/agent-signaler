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
        Assert.True(main.IndexOf("ReportStartupProgress(\"Starting the local receiver...\")", StringComparison.Ordinal) <
            main.IndexOf("await _server.StartAsync(cancellationToken)", StringComparison.Ordinal));
        Assert.True(main.IndexOf("ReportStartupProgress(\"Loading computer tiles...\")", StringComparison.Ordinal) <
            main.IndexOf("_refreshTask = RefreshAsync();", main.IndexOf("private async Task InitializeCoreAsync", StringComparison.Ordinal), StringComparison.Ordinal));

        var tunneling = MainWindowSource("MainWindow.Tunneling.cs");
        Assert.Contains("ReportStartupProgress(_tunnel.Status.Message);", tunneling);
        Assert.True(tunneling.IndexOf("ReportStartupProgress(\"Starting the shared public endpoint...\")", StringComparison.Ordinal) <
            tunneling.IndexOf("await RunTunnelOperationAsync(token => _tunnel.StartAsync(token));", StringComparison.Ordinal));
    }

    private static string MainWindowSource(string fileName = "MainWindow.cs")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", fileName));
    }
}
