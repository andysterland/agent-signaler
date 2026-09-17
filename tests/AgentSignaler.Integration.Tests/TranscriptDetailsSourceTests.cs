namespace AgentSignaler.Integration.Tests;

public sealed class TranscriptDetailsSourceTests
{
    [Fact]
    public void SettingsIsInitialAndRetainsDraftControlsAndGuardedActions()
    {
        var source = ReadDashboard("MainWindow.cs");
        Assert.Contains("Header = \"Settings\", IsClosable = false", source);
        Assert.Contains("Header = \"Copilots\", IsClosable = false", source);
        Assert.DoesNotContain("Header = \"Transcript\", IsClosable = false", source);
        Assert.DoesNotContain("content.Children.Add(sessions);", source);
        Assert.Contains("tabs.SelectedItem = settingsTab;", source);
        Assert.Contains("Content = detailsLayout,", source);
        foreach (var control in new[] { "name", "note", "current", "picker", "mappingSummary",
                     "refreshCatalog", "catalogStatus", "connectionStatus", "refreshed", "connectionUri", "copyConnectionUri" })
            Assert.Contains($"content.Children.Add({control});", source);
        Assert.Contains("saveDetails.Visibility = removeMachine.Visibility = settingsSelected ? Visibility.Visible : Visibility.Collapsed;", source);
        Assert.Contains("saveDetails.IsEnabled = removeMachine.IsEnabled = SettingsSelected() && machineExists && !busy;", source);
        Assert.Contains("if (!SettingsSelected() || !machineExists || connections.IsBusy(id) || savingDetails || _exiting)", source);
        Assert.Contains("if (savingDetails && !_exiting)", source);
        Assert.Contains("closeDetails.IsEnabled = !savingDetails;", source);
        Assert.Contains("if ((saved || _closeDialogForNavigation) && !_exiting)", source);
        Assert.Contains("result = requestedResult;", source);
        Assert.Contains("sharedProgress.Children.Add(progress);", source);
        Assert.Contains("sharedProgress.Children.Add(cancel);", source);
        Assert.Contains("sharedProgress.Children.Add(cancelCatalog);", source);
        Assert.Contains("Content = \"Clear transcript\"", source);
        Assert.Contains("_server?.Transcripts.ClearMachine(id, removed: true);", source);
    }

    [Fact]
    public void RemoteButtonRemainsInSharedFooterAndUsesGuardedWindowsAppOpenFlow()
    {
        var source = ReadDashboard("MainWindow.cs");
        Assert.Contains("var local = MachineNavigation.IsLocal(card.Machine);", source);
        Assert.Contains("var remote = new Button { Content = local ? \"Return to local\" : \"Remote\" };", source);
        Assert.Contains("new[] { saveDetails, removeMachine, remote, closeDetails }", source);
        Assert.Contains("Grid.SetRow(detailActions, 2);", source);
        Assert.Contains("detailsLayout.Children.Add(detailActions);", source);
        Assert.DoesNotContain("content.Children.Add(remote);", source);
        Assert.Contains("remote.IsEnabled = open.IsEnabled && !_exiting;", source);
        Assert.Contains("remote.Click += async (_, _) => await RunConnectionAsync(WindowsAppOperation.Open);", source);
        Assert.Contains("var result = await ExecuteRuntimeConnectionAsync(id, operation, ReadSelection());", source);
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
    public void CopilotsPreservesKeyedVirtualizedRowsAndScopesDrillInWithoutLoadingBodies()
    {
        var source = ReadDashboard("CopilotsDetailsView.cs");
        Assert.Contains("private readonly ListView _list", source);
        Assert.Contains("ObservableCollection<Row>", source);
        Assert.Contains("_rows.Move(index, i)", source);
        Assert.Contains("row.Value.Key == state.SelectedKey", source);
        Assert.Contains("Content=\"View transcript\"", source);
        Assert.Contains("Content = \"Back to Copilots\"", source);
        Assert.Contains("_controller.Select(current.Key)", source);
        Assert.Contains("_transcript?.Dispose()", source);
        Assert.Contains("TranscriptSelection(current.MachineId, current.Source, current.SessionId, Guid.Empty)", source);
        Assert.DoesNotContain("Content = _list,", source);
        Assert.DoesNotContain("ReadLatestEventsAsync", ReadDashboard("CopilotsController.cs"));
        Assert.DoesNotContain("ReadEventsAsync", ReadDashboard("CopilotsController.cs"));
        var viewer = ReadDashboard("TranscriptDetailsView.cs");
        Assert.Contains("_controller.ShowAsync(_selection, _offline)", viewer);
        Assert.Contains("Header = \"Retained stream for this Copilot\"", viewer);
    }

    [Fact]
    public void TunnelChangesUseCapturedReadinessAndPurgeBeforeAsynchronousTeardown()
    {
        var source = ReadDashboard("MainWindow.Tunneling.cs");
        Assert.Contains("UpdateTranscriptReadiness(_tunnel?.Status);", source);
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
