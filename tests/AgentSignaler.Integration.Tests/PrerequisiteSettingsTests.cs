using System.Text.RegularExpressions;

namespace AgentSignaler.Integration.Tests;

public sealed class PrerequisiteSettingsTests
{
    [Fact]
    public void FivePersistentTabsKeepOperationalControlsSeparateFromPrerequisites()
    {
        var main = Source("MainWindow.cs");
        var prerequisites = Source("MainWindow.Prerequisites.cs");
        var sharing = Source("MainWindow.Tunneling.cs");
        var devBox = Source("MainWindow.DevBox.cs");
        var headers = Regex.Matches(main, "AddSection\\(\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(["General", "Network", "Internet sharing", "Dev Box", "Prerequisite"], headers);
        Assert.Contains("Content = section", main);
        Assert.DoesNotContain("SelectionChanged", main[main.IndexOf("private async Task ShowSettingsAsync()", StringComparison.Ordinal)..]);
        Assert.Contains("TabWidthMode = TabViewWidthMode.SizeToContent", main);
        Assert.Contains("IsClosable = false", main);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", main);
        Assert.Contains("network.Children.Add(allow)", main);
        Assert.Contains("network.Children.Add(remove)", main);
        Assert.Contains("BuildTunnelSettings(sharing)", main);
        Assert.Contains("Refresh Dev Boxes", devBox);
        Assert.Contains("Stop sharing and sign out", sharing);
        Assert.Contains("Delete tunnel", sharing);
        Assert.Contains("Enable sharing / Retry", sharing);
        Assert.DoesNotContain("new TextBox", sharing);
        Assert.DoesNotContain("BuildAzureCliSettings", devBox);
        Assert.DoesNotContain("winget install", sharing);
        Assert.DoesNotContain("extension add", devBox);
        Assert.Contains("DevTunnelDiagnostics.CheckAsync(selectedPath, token)", prerequisites);
        Assert.Contains("AzureCliDiagnostics.CheckAsync(selectedPath, token)", prerequisites);
        Assert.Contains("AzureCliDiagnostics.CheckDevCenterExtensionAsync(selectedPath, token)", prerequisites);
        Assert.Contains("WindowsAppDiagnostics.CheckAsync", prerequisites);
        Assert.DoesNotContain("CheckAccountAsync", prerequisites + sharing);
        Assert.DoesNotContain("RunTunnelOperationAsync", prerequisites);
        Assert.DoesNotContain("_tunnel.", prerequisites);
        Assert.DoesNotContain("_runningMode ==", prerequisites);
        Assert.Contains("Effective sharing CLI path:", prerequisites);
        Assert.Contains("Effective Dev Box CLI path:", prerequisites);
    }

    [Fact]
    public void InvalidPathsRouteToPrerequisiteAndClosingAwaitsOnlyOwnedDiagnosticsAndDiscovery()
    {
        var main = Source("MainWindow.cs");
        var prerequisites = Source("MainWindow.Prerequisites.cs");
        var settings = main[main.IndexOf("private async Task ShowSettingsAsync()", StringComparison.Ordinal)..
            main.IndexOf("public Task ExitAsync()", StringComparison.Ordinal)];
        Assert.Equal(2, Regex.Matches(settings, "tabs.SelectedItem = prerequisiteTab").Count);
        Assert.Contains("cliPath.Focus(FocusState.Programmatic)", settings);
        Assert.Contains("azureCliPath.Focus(FocusState.Programmatic)", settings);
        Assert.Contains("var deferral = args.GetDeferral()", settings);
        Assert.Contains("await checks", settings);
        Assert.Contains("deferral.Complete()", settings);
        Assert.Contains("await CancelPrerequisiteChecksAsync()", settings);
        Assert.Contains("await _catalog.CancelAndWaitAsync()", settings);
        Assert.DoesNotContain("CancelTunnelOperations", settings);
        Assert.DoesNotContain("StopSharingAsync", settings);
        Assert.Contains("Task.WhenAll(_prerequisiteChecks.Select(check => check.CancelAndWaitAsync()))", prerequisites);
        Assert.Contains("!AzurePrerequisiteBusy && _catalog?.State.IsBusy != true", prerequisites);
        Assert.Contains("!PrerequisiteBusy", Source("MainWindow.DevBox.cs"));
        Assert.Contains("_settings.DevTunnelCliPath != _runningCliPath || _settings.AzureCliPath != _runningAzureCliPath", settings);
    }

    private static string Source(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", name));
    }
}
