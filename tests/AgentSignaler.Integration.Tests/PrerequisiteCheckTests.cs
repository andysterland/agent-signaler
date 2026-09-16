using AgentSignaler.Dashboard;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Integration.Tests;

public sealed class PrerequisiteCheckTests
{
    [Fact]
    public async Task ClosingCancelsAndAwaitsCleanupAndPreventsFurtherChecks()
    {
        var check = new PrerequisiteCheck("Example");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = check.RunAsync(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
            return Passed("Must not succeed");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(check.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => check.RunAsync(_ => Task.FromResult(Passed("duplicate"))));
        var closing = check.CancelAndWaitAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.True(check.IsBusy);
        cleanup.SetResult();
        await Task.WhenAll(running, closing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(check.IsBusy);
        Assert.Equal("Example check cancelled.", check.Result);
        Assert.Equal(PrerequisiteCheckState.Cancelled, check.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => check.RunAsync(_ => Task.FromResult(Passed("closed"))));
        await check.CancelAndWaitAsync();
    }

    [Fact]
    public async Task CancelDoesNotChangeOtherChecksAndExplicitRetryRetainsItsResult()
    {
        var first = new PrerequisiteCheck("First");
        var second = new PrerequisiteCheck("Second");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await second.RunAsync(_ => Task.FromResult(Passed("Tested path: saved.exe")));
        var pending = first.RunAsync(async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Passed("");
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        first.Cancel();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Tested path: saved.exe", second.Result);
        await first.RunAsync(_ => Task.FromResult(Passed("Tested path: unsaved.exe")));
        Assert.Equal("Tested path: unsaved.exe", first.Result);
        await first.CancelAndWaitAsync();
        Assert.Equal("Tested path: unsaved.exe", first.Result);
    }

    [Fact]
    public async Task LateSuccessfulResponseAfterCancellationIsNotReportedAsReady()
    {
        var check = new PrerequisiteCheck("Example");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<PrerequisiteDiagnosticResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = check.RunAsync(_ =>
        {
            entered.SetResult();
            return response.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        check.Cancel();
        response.SetResult(Passed("Ready"));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Example check cancelled.", check.Result);
        Assert.Equal(PrerequisiteCheckState.Cancelled, check.State);
    }

    [Fact]
    public async Task UnexpectedFailuresAreNotSwallowedAndReleaseBusyState()
    {
        var check = new PrerequisiteCheck("Example");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            check.RunAsync(_ => throw new InvalidOperationException("Unexpected failure")));
        Assert.False(check.IsBusy);
        Assert.Equal(PrerequisiteCheckState.Failed, check.State);
        Assert.Equal("Example check failed unexpectedly.", check.Result);
        await check.RunAsync(_ => Task.FromResult(Passed("Retry succeeded")));
        Assert.Equal("Retry succeeded", check.Result);
    }

    [Theory]
    [InlineData(true, "A passed check can mention failed sign-in guidance.", "Passed")]
    [InlineData(false, "Tested path: C:\\ready\\available\\cli.exe", "Failed")]
    public async Task SummaryUsesTypedOutcomeNotDisplayText(bool passed, string details, string summary)
    {
        var check = new PrerequisiteCheck("Example");
        Assert.Equal(PrerequisiteCheckState.NotChecked, check.State);
        Assert.Equal("Not checked", check.Summary);
        await check.RunAsync(_ => Task.FromResult(new PrerequisiteDiagnosticResult(passed, details)));
        Assert.Equal(passed ? PrerequisiteCheckState.Passed : PrerequisiteCheckState.Failed, check.State);
        Assert.Equal(summary, check.Summary);
        Assert.Equal(details, check.Result);
    }

    [Fact]
    public async Task QueuedAzureCheckWaitsForCleanupButStillRunsAfterReportedFailure()
    {
        var account = new PrerequisiteCheck("Azure");
        var extension = new PrerequisiteCheck("Extension");
        var independent = new PrerequisiteCheck("Independent");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountResult = new TaskCompletionSource<PrerequisiteDiagnosticResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountRun = account.RunAsync(_ => { entered.SetResult(); return accountResult.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var extensionCalls = 0;
        var extensionRun = extension.RunAsync(_ =>
        {
            Assert.False(account.IsBusy);
            Interlocked.Increment(ref extensionCalls);
            return Task.FromResult(Passed("Extension installed"));
        }, accountRun);
        Assert.True(extension.IsBusy);
        Assert.Equal(PrerequisiteCheckState.Queued, extension.State);
        Assert.Equal("Queued", extension.Summary);
        Assert.Equal(0, extensionCalls);
        await independent.RunAsync(_ => Task.FromResult(Passed("Available")));
        Assert.Equal(PrerequisiteCheckState.Passed, independent.State);
        Assert.Equal(PrerequisiteCheckState.Running, account.State);
        accountResult.SetResult(new(false, "No saved account"));
        await Task.WhenAll(accountRun, extensionRun).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrerequisiteCheckState.Failed, account.State);
        Assert.Equal(PrerequisiteCheckState.Passed, extension.State);
        Assert.Equal(1, extensionCalls);
    }

    [Fact]
    public async Task CancellingQueuedCheckNeverExecutesItOrCancelsItsPredecessor()
    {
        var account = new PrerequisiteCheck("Azure");
        var extension = new PrerequisiteCheck("Extension");
        var result = new TaskCompletionSource<PrerequisiteDiagnosticResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountRun = account.RunAsync(_ => result.Task);
        var extensionRun = extension.RunAsync(_ => throw new InvalidOperationException("Must not run"), accountRun);
        await extension.CancelAndWaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrerequisiteCheckState.Cancelled, extension.State);
        Assert.True(account.IsBusy);
        result.SetResult(Passed("Signed in"));
        await Task.WhenAll(accountRun, extensionRun).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PrerequisiteCheckState.Passed, account.State);
    }

    [Fact]
    public async Task CancelAllWaitsForRunningAndQueuedChecksWithoutStartingQueuedWork()
    {
        var first = new PrerequisiteCheck("First");
        var queued = new PrerequisiteCheck("Queued");
        var independent = new PrerequisiteCheck("Independent");
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PrerequisiteDiagnosticResult> Wait(CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { await cleanup.Task; }
            return Passed("Must not succeed");
        }
        var firstRun = first.RunAsync(Wait);
        var queuedRun = queued.RunAsync(_ => throw new InvalidOperationException("Must not run"), firstRun);
        var independentRun = independent.RunAsync(Wait);
        var closing = Task.WhenAll(new[] { first, queued, independent }.Select(check => check.CancelAndWaitAsync()));
        cleanup.SetResult();
        await Task.WhenAll(firstRun, queuedRun, independentRun, closing).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(new[] { first, queued, independent }, check =>
        {
            Assert.False(check.IsBusy);
            Assert.Equal(PrerequisiteCheckState.Cancelled, check.State);
        });
    }

    private static PrerequisiteDiagnosticResult Passed(string details) => new(true, details);
}
