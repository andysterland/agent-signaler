using AgentSignaler.Tunneling;
using Xunit;

namespace AgentSignaler.Tunneling.Tests;

public sealed partial class TunnelTests
{
    [Fact]
    public async Task ExistingTunnelReadsOverlapInFourBoundedPairsBeforeHosting()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true };
        var runner = new PairedReadRunner(fake);
        using var cancellation = new CancellationTokenSource();
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)),
            (_, _) => Task.CompletedTask, runner, new FakeProbe());
        var startup = controller.StartAsync(cancellation.Token);
        try
        {
            for (var i = 0; i < runner.Batches.Length; i++)
            {
                var batch = runner.Batches[i];
                await batch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, runner.Active);
                Assert.Equal(5 + i * 2, fake.Commands.Count);
                Assert.False(controller.Status.CanCopy);
                Assert.DoesNotContain(fake.Commands, command => command[0] == "host" || command.Contains("create"));
                batch.Release();
            }
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, runner.MaximumActive);
            Assert.Equal(0, runner.Active);
            Assert.Equal(TunnelState.Connected, controller.Status.State);
            Assert.Equal(3, fake.Commands.Count(command => command[0] == "show"));
            Assert.Equal(2, fake.Commands.Count(command => command.Take(2).SequenceEqual(new[] { "port", "show" })));
            Assert.Equal(4, fake.Commands.Count(command => command.Take(2).SequenceEqual(new[] { "access", "list" })));
        }
        finally
        {
            cancellation.Cancel();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedParallelReadDrainsItsSiblingAndNeverHosts(bool cancel)
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true };
        var runner = new PairedReadRunner(fake) { FailRead = 0 };
        using var cancellation = new CancellationTokenSource();
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)),
            (_, _) => Task.CompletedTask, runner, new FakeProbe());
        var startup = controller.StartAsync(cancellation.Token);
        try
        {
            var batch = runner.Batches[0];
            await batch.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            batch.First.TrySetResult();
            await batch.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(startup.IsCompleted);
            Assert.Equal(1, runner.Active);
            if (cancel) cancellation.Cancel();
            else batch.Second.TrySetResult();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, runner.Active);
            Assert.False(controller.Status.CanCopy);
            Assert.Equal("existing.usw2", controller.Identity.TunnelId);
            Assert.DoesNotContain(fake.Commands, command => command[0] == "host" || command.Contains("create"));
        }
        finally
        {
            cancellation.Cancel();
            await startup.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task EveryFinalParallelReadStillRejectsDrift(int read)
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true };
        var runner = new PairedReadRunner(fake) { DriftRead = read, AutoRelease = true };
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)),
            (_, _) => Task.CompletedTask, runner, new FakeProbe());
        await controller.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.False(controller.Status.CanCopy);
        Assert.Equal(0, runner.Active);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "host" || command.Contains("create"));
    }

    [Fact]
    public async Task StartupReportsDistinctStagesAndRedactedTimings()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true };
        var logs = new List<string>();
        var statuses = new List<TunnelStatus>();
        await using var controller = new CliTunnelController(Options(Bound(fake.Id)),
            (_, _) => Task.CompletedTask, fake, new FakeProbe(), logs.Add);
        controller.StatusChanged += (_, status) => statuses.Add(status);
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        string[] stages =
        [
            "Checking Dev Tunnels CLI signature and version",
            "Checking the Dev Tunnels signed-in Microsoft account",
            "Checking local receiver health",
            "Looking up the saved Dev Tunnel",
            "Checking Dev Tunnel ownership",
            "Checking the Dev Tunnel receiver port",
            "Revalidating Dev Tunnel ownership",
            "Starting the owned relay host",
            "Relay ready; verifying anonymous public HTTPS health"
        ];
        var indices = stages.Select(stage => statuses.FindIndex(status => status.Message.StartsWith(stage, StringComparison.Ordinal))).ToArray();
        Assert.All(indices, index => Assert.True(index >= 0));
        Assert.Equal(indices.Order().ToArray(), indices);
        Assert.All(statuses.Where(status => status.State != TunnelState.Connected), status => Assert.False(status.CanCopy));
        Assert.Contains(logs, line => line.Contains("Sharing startup: completed; elapsed="));
        Assert.Contains(logs, line => line.Contains("Dev Tunnels account check: completed; elapsed="));
        Assert.Contains(logs, line => line.Contains("Revalidating Dev Tunnel") && line.Contains("completed; elapsed="));
        Assert.DoesNotContain(logs, line => line.Contains("incomplete"));
        Assert.DoesNotContain(logs, line => line.Contains(fake.Id!) || line.Contains(Marker) ||
            line.Contains("not-persisted") || line.Contains("C:\\installed"));
    }

    [Fact]
    public async Task FailedStartupTimingDoesNotClaimCompletionOrExposeCliOutput()
    {
        var fake = new FakeRunner { FailCreate = true };
        var logs = new List<string>();
        await using var controller = new CliTunnelController(Options(), (_, _) => Task.CompletedTask,
            fake, new FakeProbe(), logs.Add);
        await controller.StartAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.Contains(logs, line => line.Contains("Sharing startup: incomplete; elapsed="));
        Assert.DoesNotContain(logs, line => line.Contains("Sharing startup: completed") ||
            line.Contains("secret raw diagnostic") || line.Contains(fake.Id!));
    }

    [Fact]
    public void TimingScopesLinkPhasesAndOnlyLogSafeCommandNames()
    {
        var logs = new List<string>();
        var diagnostics = new TunnelDiagnostics(logs.Add);
        using (var command = diagnostics.Begin($"Command {TunnelDiagnostics.CommandName(["show", "secret-id", "--json"])}"))
        {
            using (var phase = command.BeginPhase("Signature verification")) phase.Complete();
            using (command.BeginPhase("Process startup")) { }
            command.Complete(1);
        }
        Assert.Contains(logs, line => line.Contains("parent=#") && line.Contains("Signature verification: completed; elapsed="));
        Assert.Contains(logs, line => line.Contains("Process startup: incomplete; elapsed="));
        Assert.Contains(logs, line => line.Contains("Command show: completed; elapsed=") && line.Contains("exitCode=1"));
        Assert.DoesNotContain(logs, line => line.Contains("secret-id"));
        Assert.Equal("command", TunnelDiagnostics.CommandName(["secret-command", "secret-argument"]));
    }

    private sealed class PairedReadRunner(FakeRunner inner) : ITunnelProcessRunner
    {
        internal sealed class Batch
        {
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource First = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Second = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource FirstFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void Release() { First.TrySetResult(); Second.TrySetResult(); }
        }

        internal readonly Batch[] Batches = [new(), new(), new(), new()];
        internal int Active, MaximumActive;
        internal int? FailRead, DriftRead;
        internal bool AutoRelease;
        private bool lookedUp;
        private int reads;

        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = await inner.RunAsync(executable, arguments, timeout, cancellationToken);
            var name = TunnelDiagnostics.CommandName(arguments);
            if (name is not ("show" or "port show" or "access list")) return result;
            if (!lookedUp) { lookedUp = true; return result; }
            var read = reads++;
            var batch = Batches[read / 2];
            var active = Interlocked.Increment(ref Active);
            MaximumActive = Math.Max(MaximumActive, active);
            try
            {
                if (read % 2 == 1)
                {
                    batch.Entered.TrySetResult();
                    if (AutoRelease) batch.Release();
                }
                await (read % 2 == 0 ? batch.First.Task : batch.Second.Task).WaitAsync(cancellationToken);
                if (FailRead == read) return new(1, "", "secret failed read");
                if (DriftRead == read)
                {
                    var json = name switch
                    {
                        "show" => result.StandardOutput.Replace($"AgentSignaler owner:{Marker}", "someone else"),
                        "port show" => result.StandardOutput.Replace("connect", "manage"),
                        _ => """{"accessControlEntries":[{"type":"Anonymous","subjects":[],"scopes":["manage"]}]}"""
                    };
                    return result with { StandardOutput = json };
                }
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref Active);
                if (read % 2 == 0) batch.FirstFinished.TrySetResult();
            }
        }

        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken cancellationToken)
        {
            Assert.Equal(0, Active);
            return inner.StartHostAsync(executable, arguments, outputLine, cancellationToken);
        }
    }
}
