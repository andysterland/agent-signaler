using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class DevBoxCatalogControllerTests
{
    private static readonly Uri Endpoint = new("https://center.region.devcenter.azure.com/");
    private static readonly DevBoxCatalogSnapshot Snapshot = new(
        [new(Endpoint, "project", "pool", "devbox", "Running", "Succeeded", null, null, null, null)],
        "user@example.com", Guid.NewGuid(), DateTimeOffset.UtcNow) { DevCenterEndpoints = [Endpoint] };

    [Fact]
    public async Task ReplacesSnapshotsAtomicallyAndNotifiesOnlyWithSafeState()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        var observed = new List<DevBoxCatalogState>();
        controller.Changed += () => observed.Add(controller.State);
        Assert.Null(controller.State.Snapshot);
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Same(Snapshot, controller.State.Snapshot);
        Assert.True(observed[0].IsBusy);
        Assert.Null(observed[0].Snapshot);
        Assert.False(observed[^1].IsBusy);
        Assert.DoesNotContain(Snapshot.AzureAccountUpn, controller.State.ToString());
        Assert.DoesNotContain("https:", controller.State.ToString());
        var replacement = Snapshot with { RetrievedAtUtc = Snapshot.RetrievedAtUtc.AddMinutes(1) };
        service.Run = _ => Task.FromResult(replacement);
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Same(replacement, controller.State.Snapshot);
    }

    [Fact]
    public async Task CurrentAttemptIdentityIsVisibleWhileBusyAndAfterFailureWithoutReplacingCatalog()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        var entered = Signal();
        var release = Signal();
        var progress = new DevBoxCatalogProgress(DevBoxCatalogStage.Subscriptions)
        {
            Account = new("new-user@example.com", Guid.NewGuid(), Guid.NewGuid()),
            AccountCheckedAtUtc = DateTimeOffset.UtcNow
        };
        service.ReportProgress = report => report!(progress);
        service.Run = async _ =>
        {
            entered.SetResult();
            await release.Task;
            throw new DevBoxCatalogException(WindowsAppFailure.MalformedResponse, DevBoxCatalogStage.Subscriptions);
        };
        var observed = new List<DevBoxCatalogState>();
        controller.Changed += () => observed.Add(controller.State);

        var refresh = controller.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(controller.State.IsBusy);
        Assert.Same(progress, controller.State.Progress);
        Assert.Same(Snapshot, controller.State.Snapshot);
        release.SetResult();
        Assert.False((await refresh).Succeeded);
        Assert.Same(progress, controller.State.Progress);
        Assert.Same(Snapshot, controller.State.Snapshot);
        Assert.Contains(observed, state => state.IsBusy && state.Progress == progress);
        Assert.DoesNotContain(progress.Account.Upn, controller.State.ToString());
        Assert.DoesNotContain(progress.Account.Tenant.ToString(), controller.State.ToString());

        service.ReportProgress = null;
        service.Run = _ => throw new DevBoxCatalogException(WindowsAppFailure.SignInRequired, DevBoxCatalogStage.Account);
        Assert.False((await controller.RefreshAsync()).Succeeded);
        Assert.Null(controller.State.Progress!.Account);
        Assert.Null(controller.State.Progress.AccountCheckedAtUtc);
        Assert.Equal(DevBoxCatalogStage.Account, controller.State.Progress.Stage);
        Assert.Same(Snapshot, controller.State.Snapshot);
    }

    [Fact]
    public async Task ProgressReportedAfterCancellationDoesNotPublishIdentity()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        service.ReportProgress = report =>
        {
            controller.Cancel();
            report!(new(DevBoxCatalogStage.Subscriptions)
            {
                Account = new("user@example.com", Guid.NewGuid(), Guid.NewGuid())
            });
        };

        Assert.False((await controller.RefreshAsync()).Succeeded);
        Assert.Null(controller.State.Progress!.Account);
        Assert.Null(controller.State.Snapshot);
    }

    [Fact]
    public async Task NamedDevCenterTargetIsForwardedAndEmptyResultsExplainScope()
    {
        var service = new FakeService { Run = _ => Task.FromResult(Snapshot with { Items = [], DevCenterEndpoints = [] }) };
        var controller = new DevBoxCatalogController(service);

        Assert.True((await controller.RefreshAsync(devCenterName: "test-center")).Succeeded);
        Assert.Equal("test-center", service.TargetDevCenterName);
        Assert.Equal("test-center", controller.State.TargetDevCenterName);
        Assert.Null(service.TargetSubscription);
        Assert.Contains("requested Dev Center", controller.State.Message);
        Assert.DoesNotContain("test-center", controller.State.ToString());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            controller.RefreshAsync(subscriptionId: Guid.NewGuid(), devCenterName: "test-center"));
        Assert.Equal(1, service.Calls);
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Null(service.TargetDevCenterName);
        Assert.Null(controller.State.TargetDevCenterName);
    }

    [Fact]
    public async Task SuppliedSubscriptionIsForwardedAndDefaultRefreshClearsTheTarget()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        var target = Guid.NewGuid();

        Assert.True((await controller.RefreshAsync(subscriptionId: target)).Succeeded);
        Assert.Equal(target, service.TargetSubscription);
        Assert.Equal(target, controller.State.TargetSubscription);
        Assert.DoesNotContain(target.ToString(), controller.State.ToString());
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Null(service.TargetSubscription);
        Assert.Null(controller.State.TargetSubscription);
        var calls = service.Calls;
        await Assert.ThrowsAsync<ArgumentException>(() => controller.RefreshAsync(subscriptionId: Guid.Empty));
        Assert.Equal(calls, service.Calls);
        Assert.False(controller.State.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialSubscriptionResultsAreReportedAndEmptyResultsRetainPreviousCatalog(bool empty)
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        var subscription = Guid.NewGuid();
        var partial = Snapshot with
        {
            Items = empty ? [] : Snapshot.Items,
            SubscriptionFailures = [new(subscription, WindowsAppFailure.DevBoxUnavailable)]
        };
        service.Run = _ => Task.FromResult(partial);

        var result = await controller.RefreshAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("Partial results", controller.State.Status);
        Assert.Contains("Subscription searches failed: 1", result.Message);
        Assert.Contains("results may be incomplete", result.Message);
        Assert.DoesNotContain(subscription.ToString(), result.Message);
        Assert.Same(empty ? Snapshot : partial, controller.State.Snapshot);
        if (empty) Assert.Contains("previous catalog is retained", result.Message);
    }

    [Fact]
    public async Task FailedAndEmptyRefreshRetainPreviousCatalog()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        service.Run = _ => throw new WindowsAppConnectionException(WindowsAppFailure.TimedOut);
        Assert.False((await controller.RefreshAsync()).Succeeded);
        Assert.Same(Snapshot, controller.State.Snapshot);
        Assert.Contains("timed out", controller.State.Message);
        service.Run = _ => Task.FromResult(Snapshot with { Items = [] });
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Same(Snapshot, controller.State.Snapshot);
        Assert.Contains("previous catalog is retained", controller.State.Message);
        service.Run = _ => Task.FromResult(Snapshot with { Items = [], DevCenterEndpoints = [] });
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Contains("No accessible Dev Centers", controller.State.Message);
        Assert.Same(Snapshot, controller.State.Snapshot);
    }

    [Fact]
    public async Task FirstSuccessfulEmptyRefreshIsDistinctFromNeverLoaded()
    {
        var controller = new DevBoxCatalogController(new FakeService
        {
            Run = _ => Task.FromResult(Snapshot with { Items = [] })
        });
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.NotNull(controller.State.Snapshot);
        Assert.Empty(controller.State.Snapshot.Items);
        Assert.Contains("No assigned Dev Boxes", controller.State.Message);
    }

    [Fact]
    public async Task NoDiscoveredCentersIsDistinctFromNoAssignedBoxesAndNeedsNoManualConfiguration()
    {
        var controller = new DevBoxCatalogController(new FakeService
        {
            Run = _ => Task.FromResult(Snapshot with { Items = [], DevCenterEndpoints = [] })
        });
        Assert.True((await controller.RefreshAsync()).Succeeded);
        Assert.Empty(controller.State.Snapshot!.DevCenterEndpoints);
        Assert.Contains("No accessible Dev Centers", controller.State.Message);
        Assert.Contains("read permissions", controller.State.Message);
        Assert.DoesNotContain("Add a Dev Center", controller.State.Message);
    }

    [Fact]
    public async Task CatalogFailureShowsOnlyClassifiedEndpointErrorAndRetainsIdentity()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        service.Run = _ => throw new DevBoxCatalogException(WindowsAppFailure.DevBoxUnavailable, Endpoint.IdnHost);
        var result = await controller.RefreshAsync();
        Assert.False(result.Succeeded);
        Assert.Same(Snapshot, controller.State.Snapshot);
        Assert.Contains(Endpoint.IdnHost, result.Message);
        Assert.DoesNotContain(Snapshot.AzureAccountUpn, result.Message);
        Assert.DoesNotContain(Snapshot.AzureTenantId.ToString(), result.Message);
        Assert.DoesNotContain("https:", result.Message);
        Assert.Equal("Unavailable", controller.State.Status);
    }

    [Fact]
    public async Task ArmAccessFailureRetainsCatalogAndExplainsReadPermissionWithoutManualSetup()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        service.Run = _ => throw new DevBoxCatalogException(WindowsAppFailure.DevBoxUnavailable, DevBoxCatalogStage.DevCenters);
        var result = await controller.RefreshAsync();
        Assert.False(result.Succeeded);
        Assert.Same(Snapshot, controller.State.Snapshot);
        Assert.Contains("Azure Resource Manager permission to read Dev Centers", result.Message);
        Assert.DoesNotContain(Snapshot.AzureAccountUpn, result.Message);
        Assert.DoesNotContain("Configure", result.Message);
        Assert.DoesNotContain("Add", result.Message);
        Assert.Contains(new WindowsAppConnectionException(WindowsAppFailure.CliUnsupported).Message,
            new DevBoxCatalogException(WindowsAppFailure.CliUnsupported, DevBoxCatalogStage.Account).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosureAndShutdownAwaitCleanupAndRejectConcurrentRefresh(bool shutdown)
    {
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        var service = new FakeService
        {
            Run = async token =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.SetResult(); await cleanup.Task; }
                return Snapshot;
            }
        };
        var controller = new DevBoxCatalogController(service);
        var refresh = controller.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(controller.State.IsBusy);
        Assert.False((await controller.RefreshAsync()).Succeeded);
        var closing = shutdown ? controller.StopAsync() : controller.CancelAndWaitAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.True(controller.State.IsBusy);
        cleanup.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False((await refresh).Succeeded);
        Assert.False(controller.State.IsBusy);
        Assert.Null(controller.State.Snapshot);
        Assert.Equal("Cancelled", controller.State.Status);
        service.Run = _ => Task.FromResult(Snapshot);
        Assert.Equal(!shutdown, (await controller.RefreshAsync()).Succeeded);
    }

    [Fact]
    public async Task PreCancellationAndLateSuccessfulResponseNeverReplaceSnapshot()
    {
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = service.Calls;
        Assert.False((await controller.RefreshAsync(cancellation.Token)).Succeeded);
        Assert.Equal(calls, service.Calls);
        service.Run = _ =>
        {
            controller.Cancel();
            return Task.FromResult(Snapshot with { Items = [] });
        };
        Assert.False((await controller.RefreshAsync()).Succeeded);
        Assert.Same(Snapshot, controller.State.Snapshot);
    }

    [Fact]
    public async Task RefreshKeepsPreviousSnapshotAndCentersWhileBusy()
    {
        var entered = Signal();
        var release = Signal();
        var service = new FakeService();
        var controller = new DevBoxCatalogController(service);
        await controller.RefreshAsync();
        var replacementEndpoint = new Uri("https://second.region.devcenter.azure.com/");
        service.Run = async _ =>
        {
            entered.SetResult();
            await release.Task;
            return Snapshot with
            {
                RetrievedAtUtc = Snapshot.RetrievedAtUtc.AddHours(1), DevCenterEndpoints = [replacementEndpoint]
            };
        };
        var refreshing = controller.RefreshAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(Snapshot, controller.State.Snapshot);
        release.SetResult();
        Assert.True((await refreshing).Succeeded);
        Assert.NotSame(Snapshot, controller.State.Snapshot);
        Assert.Equal(replacementEndpoint, Assert.Single(controller.State.Snapshot!.DevCenterEndpoints));
    }

    [Fact]
    public void SettingsAndDetailsDiscoverCentersWithoutEditorsNavigationOrIdentityBanners()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var directory = Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard");
        var main = File.ReadAllText(Path.Combine(directory, "MainWindow.cs"));
        var settings = File.ReadAllText(Path.Combine(directory, "MainWindow.DevBox.cs"));
        var cli = File.ReadAllText(Path.Combine(directory, "MainWindow.Prerequisites.cs"));
        Assert.Contains("AddSection(\"Dev Box\", azure, azureHelp)", main);
        Assert.DoesNotContain("AddSection(\"Azure CLI\"", main);
        Assert.Contains("BuildPrerequisiteSettings(prerequisites, prerequisiteHelp)", main);
        Assert.DoesNotContain("BuildAzureCliSettings", settings);
        Assert.DoesNotContain("SeedFromMappings", settings);
        Assert.DoesNotContain("ReadEndpoints", settings);
        Assert.DoesNotContain("Add Dev Center", settings);
        Assert.DoesNotContain("_settings.DevCenterEndpoints", main);
        Assert.Contains("await _catalog.RefreshAsync(subscriptionId: next.DevBoxSubscriptionId, devCenterName: next.DevCenterName)", settings);
        Assert.Contains("await _catalog.RefreshAsync(subscriptionId: _settings.DevBoxSubscriptionId, devCenterName: _settings.DevCenterName)", main);
        Assert.Contains("Text = _settings.DevBoxSubscriptionId?.ToString()", settings);
        Assert.Contains("Text = _settings.DevCenterName", settings);
        Assert.Contains("_settings.WithDiscoveryTarget(subscriptionInput.Text, devCenterInput.Text)", settings);
        Assert.True(settings.IndexOf("next.Save()", StringComparison.Ordinal) <
            settings.IndexOf("_settings = next", StringComparison.Ordinal));
        Assert.True(settings.IndexOf("_settings = next", StringComparison.Ordinal) <
            settings.IndexOf("await _catalog.RefreshAsync", StringComparison.Ordinal));
        Assert.Contains("discoverySettings = readDevBoxSettings()", main);
        Assert.Contains("var next = discoverySettings with", main);
        Assert.DoesNotContain("devBoxSettingsRequested", main);
        Assert.Contains("snapshot?.DevCenterEndpoints.Select", settings);
        Assert.Contains("Azure Resource Manager read access", settings);
        Assert.Contains("await _catalog.CancelAndWaitAsync()", main);
        Assert.Contains("_catalog?.StopAsync()", main);
        Assert.True(main.IndexOf("await catalogShutdown", StringComparison.Ordinal) <
            main.IndexOf("_store?.Dispose()", StringComparison.Ordinal));
        Assert.Contains("_activeDialog.IsPrimaryButtonEnabled = editingEnabled", settings);
        Assert.Contains("_catalog?.State.IsBusy", cli);
        Assert.Contains("snapshot.AzureAccountUpn", settings);
        Assert.DoesNotContain("snapshot.AzureAccountUpn", main.Replace(
            "new(item, snapshot.AzureAccountUpn, snapshot.AzureTenantId)", "", StringComparison.Ordinal));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FakeService : IDevBoxCatalogService
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<DevBoxCatalogSnapshot>> Run { get; set; } =
            _ => Task.FromResult(Snapshot);

        public Action<Action<DevBoxCatalogProgress>?>? ReportProgress { get; set; }
        public Guid? TargetSubscription { get; private set; }
        public string? TargetDevCenterName { get; private set; }

        public Task<DevBoxCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken,
            Action<DevBoxCatalogProgress>? reportProgress = null, Guid? subscriptionId = null, string? devCenterName = null)
        {
            Calls++;
            TargetSubscription = subscriptionId;
            TargetDevCenterName = devCenterName;
            ReportProgress?.Invoke(reportProgress);
            return Run(cancellationToken);
        }
    }
}
