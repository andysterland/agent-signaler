using AgentSignaler.Tunneling;
using Xunit;

namespace AgentSignaler.Tunneling.Tests;

public sealed partial class TunnelTests
{
    [Theory]
    [InlineData("start")]
    [InlineData("delete")]
    [InlineData("logout")]
    public async Task TypedNativeCommandTimeoutIsNotMisclassifiedAsPrerequisiteFailure(string operation)
    {
        var fake = new FakeRunner { Id = "existing.usw2" };
        var runner = new AfterCommandRunner(fake, _ =>
            throw new TunnelException("Synthetic command deadline.") { FailureKind = CliFailureKind.CommandTimeout });
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)), (_, _) => Task.CompletedTask, runner, new FakeProbe());
        var result = await (operation switch
        {
            "start" => controller.StartWithResultAsync(),
            "delete" => controller.DeleteWithResultAsync(),
            _ => controller.LogoutWithResultAsync()
        });
        Assert.Equal(TunnelOperationOutcome.TimedOut, result.Outcome);
        Assert.Equal(TunnelCommitState.NotCommitted, result.CommitState);
    }

    [Fact]
    public async Task TypedDeleteFailureRetainsIdentityWithoutClaimingSuccessOrCommit()
    {
        var fake = new FakeRunner { Id = "existing.usw2", FailShow = true };
        await using var controller = Create(fake, identity: Bound(fake.Id));
        var result = await controller.DeleteWithResultAsync();
        Assert.Equal(TunnelOperationOutcome.Failed, result.Outcome);
        Assert.Equal(TunnelCommitState.NotCommitted, result.CommitState);
        Assert.Equal(fake.Id, controller.Identity.TunnelId);
    }

    [Fact]
    public async Task TypedDeleteVerificationFailureRetainsItsConfirmedRemoteCommit()
    {
        var fake = new FakeRunner { Id = "existing.usw2", DeleteConfirmsAbsence = false };
        await using var controller = Create(fake, identity: Bound(fake.Id));
        var result = await controller.DeleteWithResultAsync();
        Assert.Equal(TunnelOperationOutcome.Failed, result.Outcome);
        Assert.Equal(TunnelCommitState.Committed, result.CommitState);
        Assert.Equal(fake.Id, controller.Identity.TunnelId);
    }

    [Fact]
    public async Task TypedDeleteCancellationAfterRemoteAcknowledgementRetainsCommitAndIdentity()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeRunner { Id = "existing.usw2", DeleteConfirmsAbsence = true };
        var runner = new AfterCommandRunner(fake, arguments =>
        {
            if (arguments[0] == "delete") cancellation.Cancel();
        });
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)), (_, _) => Task.CompletedTask, runner, new FakeProbe());
        var result = await controller.DeleteWithResultAsync(cancellation.Token);
        Assert.Equal(TunnelOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(TunnelCommitState.Committed, result.CommitState);
        Assert.Equal(fake.Id, controller.Identity.TunnelId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedLogoutFailureOrCancellationCannotBecomeSuccessful(bool cancelled)
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeRunner
        {
            BeforeLogout = () =>
            {
                if (!cancelled) throw new IOException("Synthetic failure.");
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
        };
        await using var controller = Create(fake, identity: Bound(null));
        var result = await controller.LogoutWithResultAsync(cancellation.Token);
        Assert.Equal(cancelled ? TunnelOperationOutcome.Cancelled : TunnelOperationOutcome.Failed, result.Outcome);
        Assert.Equal(TunnelCommitState.Unknown, result.CommitState);
    }

    [Fact]
    public async Task TypedSuccessfulLogoutFollowedByCancellationReportsCommittedCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeRunner { BeforeLogout = cancellation.Cancel };
        await using var controller = Create(fake, identity: Bound(null));
        var result = await controller.LogoutWithResultAsync(cancellation.Token);
        Assert.Equal(TunnelOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(TunnelCommitState.Committed, result.CommitState);
    }

    [Fact]
    public async Task TypedStartupCancellationStopsOwnedHostAndIsNotPrerequisiteFailure()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeRunner();
        var probe = new FakeProbe { BlockPublic = true };
        await using var controller = Create(fake, probe);
        var starting = controller.StartWithResultAsync(cancellation.Token);
        await probe.PublicEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TunnelOperationOutcome.Cancelled, result.Outcome);
        Assert.Equal(TunnelCommitState.Committed, result.CommitState);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task TypedStartupTimeoutIsDistinctFromCancellationAndPreservesDurableIntent()
    {
        var fake = new FakeRunner { BeforeCreate = () => throw new TimeoutException("Synthetic timeout.") };
        await using var controller = Create(fake);
        var result = await controller.StartWithResultAsync();
        Assert.Equal(TunnelOperationOutcome.TimedOut, result.Outcome);
        Assert.Equal(TunnelCommitState.Committed, result.CommitState);
        Assert.NotNull(controller.Identity.PendingTunnelId);
    }

    [Fact]
    public async Task TypedDeletePersistenceFailureRetainsRemoteCommitAndReportsPersistence()
    {
        var fake = new FakeRunner { Id = "existing.usw2", DeleteConfirmsAbsence = true };
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)),
            (_, _) => throw new IOException("Synthetic persistence failure."), fake, new FakeProbe());
        var result = await controller.DeleteWithResultAsync();
        Assert.Equal(TunnelOperationOutcome.Failed, result.Outcome);
        Assert.Equal(TunnelOperationFailure.Persistence, result.Failure);
        Assert.Equal(TunnelCommitState.Committed, result.CommitState);
        Assert.Equal(fake.Id, controller.Identity.TunnelId);
    }

    private sealed class AfterCommandRunner(FakeRunner inner, Action<IReadOnlyList<string>> after) : ITunnelProcessRunner
    {
        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            var result = await inner.RunAsync(executable, arguments, timeout, token);
            after(arguments);
            return result;
        }
        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken token) => inner.StartHostAsync(executable, arguments, outputLine, token);
    }
}
