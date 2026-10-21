using System.Collections.Immutable;
using System.Security.Cryptography;
using AgentSignaler.Updates;

namespace AgentSignaler.Integration.Tests;

public sealed class UpdateHandoffTests
{
    [Fact]
    public async Task StagesOnlyInstalledPinnedScriptsThenCommitsPeersBeforeInitiator()
    {
        using var fixture = new Fixture();
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        var ready = await fixture.Launcher.LaunchAsync(ticket, fixture.Request);
        Assert.Equal(6, fixture.Trust.Verified.Count);
        Assert.True(fixture.Handoff.ReadyWaited);
        Assert.DoesNotContain("commit", fixture.Transport.Commands.Select(c => c.Command));
        var directory = Path.GetDirectoryName(fixture.Handoff.EntryScript)!;
        var manifest = UpdateStrictJson.Read<UpdateHandoffManifest>(File.ReadAllBytes(Path.Combine(directory, "handoff.json")));
        Assert.Equal(ticket.RunId.ToString("N"), manifest.RunId);
        Assert.Equal(fixture.Request.Candidate.Id, manifest.ReleaseId);
        Assert.Equal(fixture.Request.Candidate.Assets.ToArray(), manifest.Assets.ToArray());
        foreach (var script in fixture.Request.Scripts)
            Assert.Equal(File.ReadAllBytes(Path.Combine(fixture.Install, script.Name)), File.ReadAllBytes(Path.Combine(directory, script.Name)));
        await ticket.CommitAsync(ready);
        Assert.Equal(["prepare", "prepare", "reserve", "reserve", "commit", "commit"], fixture.Transport.Commands.Select(c => c.Command));
        Assert.Equal([2, 1], fixture.Transport.Commands.Where(c => c.Command == "commit").Select(c => c.Pid));
        var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(fixture.Storage.Read("gate.json")!);
        Assert.Equal(UpdateMaintenancePhase.Servicing, gate.Phase);
        Assert.Equal(fixture.Processes.Updater, gate.Owner);
        Assert.Null(fixture.Storage.Read("restart-" + ticket.RunId.ToString("N") + ".json"));
        Assert.Null(fixture.Storage.Read("restart.json"));
    }

    [Fact]
    public async Task StoppedClientIsNeverIncludedInConfiguratorRestartManifest()
    {
        using var fixture = new Fixture(clientRunning: false);
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        await fixture.Launcher.LaunchAsync(ticket, fixture.Request);
        var manifest = UpdateStrictJson.Read<UpdateHandoffManifest>(fixture.Storage.ReadRun(ticket.RunId, "handoff.json")!);
        Assert.Equal(2, manifest.Participants.Length);
        Assert.All(manifest.Participants, participant =>
        {
            Assert.Equal(UpdateApplication.Configurator, participant.Restart.Application);
            Assert.False(participant.Restart.Background);
            Assert.Equal(Path.Combine(fixture.Install, "AgentSignaler.Configurator.exe"), participant.Process.ExecutablePath);
        });
        Assert.DoesNotContain(manifest.Participants, participant => participant.Restart.Application == UpdateApplication.Client);
    }

    [Fact]
    public async Task OrdinaryExitReservationRefusalNeverTransfersTheGateOrCommitsShutdown()
    {
        using var fixture = new Fixture();
        var ticket = await fixture.Coordinator.PrepareAsync();
        var ready = await fixture.Launcher.LaunchAsync(ticket, fixture.Request);
        fixture.Transport.RefuseReservePid = 2;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ticket.CommitAsync(ready));
        Assert.Equal("update_commit_declined", error.Message);
        var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(fixture.Storage.Read("gate.json")!);
        Assert.Equal(UpdateMaintenancePhase.Preparing, gate.Phase);
        Assert.Equal(fixture.Processes.Current, gate.Owner);
        Assert.DoesNotContain(fixture.Transport.Commands, command => command.Command == "commit");
        await ticket.DisposeAsync();
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.Equal(2, fixture.Transport.Commands.Count(command => command.Command == "cancel"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitOrUpdaterLossDuringReservationAbortsBeforeGateTransfer(bool updaterExited)
    {
        using var fixture = new Fixture();
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        var ready = await fixture.Launcher.LaunchAsync(ticket, fixture.Request);
        fixture.Transport.OnReserve = registration =>
        {
            var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(fixture.Storage.Read("gate.json")!);
            Assert.Equal(UpdateMaintenancePhase.Preparing, gate.Phase);
            if (registration.Process.ProcessId != 2) return;
            if (updaterExited) fixture.Processes.UpdaterAlive = false;
            else fixture.Processes.Apps = fixture.Processes.Apps.RemoveAt(1);
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ticket.CommitAsync(ready));
        Assert.Equal(updaterExited ? "update_readiness_invalid" : "update_inventory_changed", error.Message);
        Assert.Equal(UpdateMaintenancePhase.Preparing, UpdateStrictJson.Read<UpdateMaintenanceGate>(fixture.Storage.Read("gate.json")!).Phase);
        Assert.DoesNotContain(fixture.Transport.Commands, command => command.Command == "commit");
    }

    [Fact]
    public async Task ChangedInstalledScriptLeavesEveryAppRunning()
    {
        using var fixture = new Fixture();
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        File.AppendAllText(Path.Combine(fixture.Install, UpdateHandoffLauncher.RequiredScripts[0]), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Launcher.LaunchAsync(ticket, fixture.Request));
        Assert.Null(fixture.Handoff.EntryScript);
        Assert.DoesNotContain("commit", fixture.Transport.Commands.Select(c => c.Command));
    }

    [Fact]
    public async Task UntrustedInstalledScriptNeverLaunchesUpdater()
    {
        using var fixture = new Fixture();
        fixture.Trust.Reject = true;
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Launcher.LaunchAsync(ticket, fixture.Request));
        Assert.Null(fixture.Handoff.EntryScript);
        Assert.DoesNotContain("commit", fixture.Transport.Commands.Select(c => c.Command));
    }

    [Fact]
    public async Task MissingUpdaterReadinessNeverCommitsAndTicketDisposalCancels()
    {
        using var fixture = new Fixture();
        fixture.Handoff.RefuseReadiness = true;
        var ticket = await fixture.Coordinator.PrepareAsync();
        await Assert.ThrowsAsync<IOException>(() => fixture.Launcher.LaunchAsync(ticket, fixture.Request));
        await ticket.DisposeAsync();
        Assert.Equal(2, fixture.Transport.Commands.Count(c => c.Command == "cancel"));
        Assert.DoesNotContain("commit", fixture.Transport.Commands.Select(c => c.Command));
        Assert.Null(fixture.Storage.Read("gate.json"));
    }

    [Fact]
    public async Task InternalReadinessDeadlineIsBoundedFailureWithoutCommitting()
    {
        using var fixture = new Fixture();
        fixture.Handoff.ReadinessTimeout = true;
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Launcher.LaunchAsync(ticket, fixture.Request));
        Assert.Equal("update_handoff_timeout", error.Message);
        Assert.DoesNotContain("commit", fixture.Transport.Commands.Select(c => c.Command));
    }

    [Fact]
    public async Task LaunchRaceAfterReadinessRefusesCommitAndDoesNotTransferGate()
    {
        using var fixture = new Fixture();
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        var ready = await fixture.Launcher.LaunchAsync(ticket, fixture.Request);
        fixture.Processes.Apps = fixture.Processes.Apps.Add(fixture.Processes.Apps[0] with { ProcessId = 3 });
        await Assert.ThrowsAsync<InvalidOperationException>(() => ticket.CommitAsync(ready));
        var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(fixture.Storage.Read("gate.json")!);
        Assert.Equal(UpdateMaintenancePhase.Preparing, gate.Phase);
        Assert.Equal(fixture.Processes.Current, gate.Owner);
        Assert.DoesNotContain("commit", fixture.Transport.Commands.Select(c => c.Command));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AgentSignaler.Handoff.Tests." + Guid.NewGuid().ToString("N"));
        public string Install { get; }
        public UpdateStorage Storage { get; }
        public ProcessFake Processes { get; }
        public TransportFake Transport { get; } = new();
        public TrustFake Trust { get; } = new();
        public HandoffFake Handoff { get; }
        public UpdateMaintenanceCoordinator Coordinator { get; }
        public UpdateHandoffLauncher Launcher { get; }
        public UpdateHandoffRequest Request { get; }
        public Fixture(bool clientRunning = true)
        {
            Install = Path.Combine(root, "Programs", "AgentSignaler", "Remote");
            Directory.CreateDirectory(Install);
            var installed = new UpdateInstalledPackage(UpdatePackage.Remote, Guid.NewGuid(), UpdatePolicy.UpgradeCode(UpdatePackage.Remote), new Version(1, 2, 3), Install, "S-1-5-21-111-222-333-1001");
            Storage = new(root, Install);
            var application = clientRunning ? UpdateApplication.Client : UpdateApplication.Configurator;
            Processes = new(Install, installed.UserSid, application);
            Handoff = new(Processes);
            Coordinator = new(installed, Storage, Processes, Transport, TimeProvider.System);
            Launcher = new(Storage, Processes, Trust, Handoff, TimeProvider.System);
            foreach (var app in Processes.Apps)
            {
                var registration = new UpdateInstanceRegistration(1, app, new(application, Path.Combine(root, "config.json"), clientRunning), UpdateMaintenanceValidation.PipeName(installed, app));
                Storage.Write($"instance-{app.ProcessId}-{app.CreationTimeUtcTicks}.json", UpdateStrictJson.Write(registration));
            }
            var pins = ImmutableArray.CreateBuilder<UpdateScriptPin>();
            foreach (var name in UpdateHandoffLauncher.RequiredScripts)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("# Synthetic non-executable trust fixture " + name);
                File.WriteAllBytes(Path.Combine(Install, name), bytes);
                pins.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
            }
            Request = new(installed, new(10, "v1.2.4", new(1, 2, 4), DateTimeOffset.UtcNow, false,
                [new(20, "AgentSignaler.Dashboard.msi", 100), new(21, "AgentSignaler.Remote.msi", 100), new(22, "SHA256SUMS.txt", 100)]),
                pins.ToImmutable(), new('a', 64), new('b', 64));
        }
        public void Dispose() { Storage.Dispose(); Directory.Delete(root, true); }
    }
    private sealed class ProcessFake(string install, string user, UpdateApplication application) : IUpdateProcessAdapter
    {
        public ImmutableArray<UpdateProcessIdentity> Apps = [new(1, 101, user, 1, Path.Combine(install, $"AgentSignaler.{application}.exe")), new(2, 102, user, 1, Path.Combine(install, $"AgentSignaler.{application}.exe"))];
        public bool UpdaterAlive = true;
        public UpdateProcessIdentity Current => Apps[0];
        public UpdateProcessIdentity Updater { get; } = new(90, 190, user, 1, @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe");
        public UpdateProcessIdentity? Find(int processId) => processId == 90 ? UpdaterAlive ? Updater : null : Apps.FirstOrDefault(p => p.ProcessId == processId);
        public ImmutableArray<UpdateProcessIdentity> Enumerate(UpdateInstalledPackage installation) => Apps;
    }
    private sealed class TransportFake : IUpdateMaintenanceTransport
    {
        public List<(int Pid, string Command)> Commands { get; } = [];
        public int RefuseReservePid;
        public Action<UpdateInstanceRegistration>? OnReserve;
        public Task<IAsyncDisposable> ListenAsync(UpdateInstanceRegistration registration, IUpdateMaintenanceParticipant participant, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> SendAsync(UpdateInstanceRegistration registration, Guid runId, string command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Commands.Add((registration.Process.ProcessId, command));
            if (command == "reserve") OnReserve?.Invoke(registration);
            return Task.FromResult(command != "reserve" || registration.Process.ProcessId != RefuseReservePid);
        }
    }
    private sealed class TrustFake : IUpdateScriptTrust
    {
        public List<string> Verified { get; } = [];
        public bool Reject;
        public Task VerifyAsync(string path, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (Reject) throw new UnauthorizedAccessException(); Verified.Add(path); return Task.CompletedTask; }
    }
    private sealed class HandoffFake(ProcessFake processes) : IUpdateHandoffAdapter
    {
        public string? EntryScript;
        public bool ReadyWaited;
        public bool RefuseReadiness;
        public bool ReadinessTimeout;
        public Task<UpdateProcessIdentity> LaunchAsync(string entryScript, string manifestPath, string pipeName, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); EntryScript = entryScript; return Task.FromResult(processes.Updater); }
        public Task WaitForReadyAsync(UpdateProcessIdentity updater, Guid runId, string nonce, string pipeName, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); if (ReadinessTimeout) throw new OperationCanceledException(); if (RefuseReadiness) throw new IOException("synthetic no readiness"); ReadyWaited = true; return Task.CompletedTask; }
    }
}