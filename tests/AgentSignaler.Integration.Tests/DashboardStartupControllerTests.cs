using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class DashboardStartupControllerTests
{
    [Fact]
    public async Task FailedMutationStillStartsAndAwaitsRuntimeShutdown()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var pending = DashboardStartupController.DrainForShutdownAsync(
            [Task.FromException(new RuntimeCommandException(new(1003, "operation", true)))], () =>
            {
                calls++;
                return stopped.Task;
            });
        Assert.Equal(1, calls);
        Assert.False(pending.IsCompleted);
        stopped.SetResult(true);
        Assert.False(await pending);
    }

    [Fact]
    public async Task UnexpectedUiFailureStillAwaitsShutdownWithoutBeingSwallowed()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Synthetic UI failure.");
        var pending = DashboardStartupController.DrainForShutdownAsync([Task.FromException(failure)], () => stopped.Task);
        Assert.False(pending.IsCompleted);
        stopped.SetResult(true);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => pending));
    }

    [Fact]
    public void MachinesAreHiddenBeforeInitializationBegins()
    {
        var startup = new DashboardStartupController();
        Assert.True(startup.IsLoading);
        Assert.False(startup.ShowMachines);
    }

    [Fact]
    public async Task LoadedMachinesStayHiddenUntilFinalEndpointStepCompletes()
    {
        var startup = new DashboardStartupController();
        var endpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reachedEndpoint = false;
        var task = startup.RunAsync(async token =>
        {
            // Receiver, tunnel configuration, and initial machine refresh have completed.
            reachedEndpoint = true;
            await endpoint.Task.WaitAsync(token);
        }, CancellationToken.None);

        Assert.True(reachedEndpoint);
        Assert.False(task.IsCompleted);
        Assert.True(startup.IsLoading);
        Assert.False(startup.ShowMachines);
        endpoint.SetResult();
        await task;
        Assert.False(startup.IsLoading);
        Assert.True(startup.ShowMachines);
    }

    [Theory]
    [InlineData(false, true)] // LAN
    [InlineData(true, false)] // Sharing explicitly disabled (also settings recovery)
    public async Task SkippedSharingDoesNotLeaveLoaderRunning(bool internet, bool autoStart)
    {
        var settings = new DashboardSettings
        {
            ConnectionMode = internet ? DashboardConnectionMode.DevTunnel : DashboardConnectionMode.Lan,
            AutoStartSharing = autoStart
        };
        var startup = new DashboardStartupController();
        await startup.RunAsync(_ =>
        {
            Assert.False(settings.ShouldStartSharing);
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.False(startup.IsLoading);
        Assert.True(startup.ShowMachines);
    }

    [Fact]
    public async Task HandledStartupFailureReleasesViewForRecovery()
    {
        var startup = new DashboardStartupController();
        string? problem = null;
        await startup.RunAsync(_ =>
        {
            // Mirrors MainWindow's existing safe InfoBar failure handling.
            problem = "Automatic Internet sharing could not start.";
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.NotNull(problem);
        Assert.False(startup.IsLoading);
        Assert.True(startup.ShowMachines);
    }

    [Fact]
    public async Task UnexpectedFailureIsNotSwallowedAndClearsBusyState()
    {
        var startup = new DashboardStartupController();
        var error = new InvalidOperationException("Synthetic failure");
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            startup.RunAsync(_ => Task.FromException(error), CancellationToken.None)));
        Assert.False(startup.IsLoading);
    }

    [Fact]
    public async Task ExitCancelsPendingEndpointWithoutRevealingMachines()
    {
        var startup = new DashboardStartupController();
        using var lifetime = new CancellationTokenSource();
        var task = startup.RunAsync(token => Task.Delay(Timeout.Infinite, token), lifetime.Token);
        lifetime.Cancel();
        await task;
        Assert.False(startup.IsLoading);
        Assert.False(startup.ShowMachines);
    }

    [Fact]
    public async Task ExitKeepsMachinesHiddenEvenWhenOperationHandlesCancellation()
    {
        var startup = new DashboardStartupController();
        using var lifetime = new CancellationTokenSource();
        await startup.RunAsync(_ =>
        {
            lifetime.Cancel();
            return Task.CompletedTask;
        }, lifetime.Token);
        Assert.False(startup.IsLoading);
        Assert.False(startup.ShowMachines);
    }

    [Fact]
    public async Task ExitBeforeStartupSkipsInitialization()
    {
        var startup = new DashboardStartupController();
        using var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        var invoked = false;
        await startup.RunAsync(_ =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, lifetime.Token);
        Assert.False(invoked);
        Assert.False(startup.IsLoading);
        Assert.False(startup.ShowMachines);
    }
}
