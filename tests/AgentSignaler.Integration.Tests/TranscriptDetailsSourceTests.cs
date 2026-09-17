namespace AgentSignaler.Integration.Tests;

public sealed class TranscriptDetailsSourceTests
{
    [Fact]
    public void SettingsIsInitialAndRetainsDraftControlsAndGuardedActions()
    {
        var source = ReadDashboard("MainWindow.cs");
        Assert.Contains("Header = \"Settings\", IsClosable = false", source);
        Assert.Contains("Header = \"Transcript\", IsClosable = false", source);
        Assert.Contains("tabs.SelectedItem = settingsTab;", source);
        Assert.Contains("Content = detailsLayout,", source);
        foreach (var control in new[] { "name", "note", "current", "sessions", "picker", "mappingSummary",
                     "refreshCatalog", "catalogStatus", "connectionStatus", "refreshed", "connectionUri", "copyConnectionUri" })
            Assert.Contains($"content.Children.Add({control});", source);
        Assert.Contains("dialog.PrimaryButtonText = settingsSelected ? \"Save details\" : \"\";", source);
        Assert.Contains("dialog.SecondaryButtonText = settingsSelected ? \"Remove\" : \"\";", source);
        Assert.Contains("dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = SettingsSelected() && machineExists && !busy;", source);
        Assert.Contains("if (!SettingsSelected() || !machineExists || connections.IsBusy(id) || _exiting)", source);
        Assert.Contains("sharedProgress.Children.Add(progress);", source);
        Assert.Contains("sharedProgress.Children.Add(cancel);", source);
        Assert.Contains("sharedProgress.Children.Add(cancelCatalog);", source);
        Assert.Contains("Content = \"Clear transcript\"", source);
        Assert.Contains("_server?.Transcripts.ClearMachine(id, removed: true);", source);
    }

    [Fact]
    public void ViewerUsesVirtualizedInertTextWithoutClipboardOrCaptureActions()
    {
        var source = ReadDashboard("TranscriptDetailsView.cs");
        Assert.Contains("private readonly ListView _entries", source);
        Assert.Contains("SelectionMode = ListViewSelectionMode.None", source);
        Assert.Contains("IsTextSelectionEnabled=\"False\"", source);
        Assert.Contains("_entries.ContainerContentChanging", source);
        Assert.Contains("args.InRecycleQueue", source);
        Assert.Contains("child.Text = \"\";", source);
        Assert.Contains("_entries.ItemsSource = null;", source);
        Assert.Contains("_controller.Dispose();", source);
        Assert.DoesNotContain("Clipboard", source);
        Assert.DoesNotContain("Hyperlink", source);
        Assert.DoesNotContain("WebView", source);
        Assert.DoesNotContain("Process.Start", source);
        Assert.DoesNotContain("HttpClient", source);
        Assert.DoesNotContain("File.Read", source);
        Assert.DoesNotContain("Content = _entries", source);
    }

    [Fact]
    public void TunnelChangesUseCapturedReadinessAndPurgeBeforeAsynchronousTeardown()
    {
        var source = ReadDashboard("MainWindow.Tunneling.cs");
        Assert.Contains("UpdateTranscriptReadiness(status);", source);
        Assert.Matches(@"private async Task StopSharingAsync\(\)\s*\{\s*SuspendTranscriptTransport\(\);", source);
        Assert.Contains("_server?.SetTranscriptReadiness(false);", source);
        Assert.Contains("_transcriptConnectionChanged || _transcriptTransportSuspended", source);
    }

    private static string ReadDashboard(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", name));
    }
}
