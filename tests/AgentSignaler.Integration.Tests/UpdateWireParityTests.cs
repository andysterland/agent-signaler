using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Integration.Tests;

[SupportedOSPlatform("windows")]
public sealed class UpdateWireParityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ManagedHandoffRoundTripsThroughRealPowerShellStorageAndHealth(bool existingHealthRecord, bool customInstall)
    {
        using var root = new TemporaryRoot();
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var install = customInstall ? Path.Combine(root.Path, "CustomInstall") : Path.Combine(local, "Programs", "AgentSignaler", "Remote");
        Directory.CreateDirectory(install);
        var repository = RepositoryRoot();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var native = new WindowsUpdateProcessAdapter();
        var actualHost = native.Current;
        var original = new UpdateProcessIdentity(601, DateTime.UtcNow.Ticks, sid, actualHost.SessionId,
            Path.Combine(install, "AgentSignaler.Client.exe"));
        var restarted = actualHost with { ExecutablePath = original.ExecutablePath };
        // Only installed-app facts are synthetic. The updater and pipe peer are real processes.
        var processes = new Processes(original, native);
        var installation = new UpdateInstalledPackage(UpdatePackage.Remote, Guid.NewGuid(),
            UpdatePolicy.UpgradeCode(UpdatePackage.Remote), new Version(1, 0, 0), install, sid);
        using var storage = new UpdateStorage(local, install);
        storage.Write("wire-control.json", UpdateStrictJson.Write(new { actualHost, restartProcess = restarted }));
        var transport = new Transport();
        var coordinator = new UpdateMaintenanceCoordinator(installation, storage, processes, transport, TimeProvider.System, () => null);
        var restart = new UpdateRestartContext(UpdateApplication.Client, Path.Combine(root.Path, "remote.json"), true);
        await using var registration = await coordinator.RegisterAsync(restart, new Participant());
        await using var ticket = await coordinator.PrepareAsync();
        var pins = ImmutableArray.CreateBuilder<UpdateScriptPin>();
        foreach (var name in UpdateHandoffLauncher.RequiredScripts)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(repository, "scripts", name));
            await File.WriteAllBytesAsync(Path.Combine(install, name), bytes);
            pins.Add(new(name, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        await using var handoff = new FixtureHandoff(repository, local, processes);
        var launcher = new UpdateHandoffLauncher(storage, processes, new FixtureTrust(), handoff, TimeProvider.System);
        var request = new UpdateHandoffRequest(installation, new(10, "v1.0.1", new(1, 0, 1), DateTimeOffset.UtcNow, false,
            [new(11, "AgentSignaler.Dashboard.msi", 100), new(12, "AgentSignaler.Remote.msi", 100), new(13, "SHA256SUMS.txt", 100)]),
            pins.ToImmutable(), new('b', 64), new('c', 64));
        var ready = await launcher.LaunchAsync(ticket, request);
        var manifest = UpdateStrictJson.Read<UpdateHandoffManifest>(storage.ReadRun(ticket.RunId, "handoff.json")!);
        Assert.Equal(ticket.Participants.ToArray(), manifest.Participants.ToArray());
        Assert.Equal(request.Scripts.ToArray(), manifest.Scripts.ToArray());
        Assert.Equal(request.Candidate.Tag, manifest.Tag);
        await ticket.CommitAsync(ready);
        var committed = UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("gate.json")!);
        Assert.Equal(UpdateMaintenancePhase.Servicing, committed.Phase);
        var healthName = "restart-" + ticket.RunId.ToString("N") + ".json";
        Assert.Null(storage.Read(healthName));
        if (existingHealthRecord) storage.Write(healthName, UpdateStrictJson.Write(committed));
        storage.Write("wire-committed.json", "{}"u8);
        await handoff.ExpectAsync("restart-written");
        var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("gate.json")!);
        var marker = UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("restart.json")!);
        var health = UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read(healthName)!);
        Assert.Equal(gate, marker);
        Assert.Equal(UpdateMaintenancePhase.Restarting, gate.Phase);
        Assert.Equal(UpdateMaintenancePhase.Servicing, health.Phase);
        Assert.Equal(ready.Updater, health.Owner);
        Assert.Equal(original, health.Controller);
        Assert.Equal(ticket.RunId, health.RunId);
        Assert.Equal(install, health.InstallDirectory);
        Assert.False(coordinator.CheckStartupAllowed());
        var busy = await Assert.ThrowsAsync<UpdateMaintenanceBusyException>(() => coordinator.PrepareAsync());
        Assert.Equal(ticket.RunId, busy.RunId);
        Assert.Equal(UpdateMaintenancePhase.Restarting, busy.Phase);

        processes.CurrentIdentity = restarted;
        var grantName = UpdateMaintenanceRestart.GrantName(restarted);
        var grant = UpdateStrictJson.Read<UpdateRestartGrant>(storage.Read(grantName)!);
        Assert.Equal(restarted, grant.Process);
        Assert.Equal(original.ProcessId, grant.OriginalProcessId);
        Assert.Equal(restart, grant.Restart);
        var healthParticipant = new Participant();
        var restartTransport = new WindowsUpdateMaintenanceTransport(installation, storage, processes, TimeProvider.System);
        var authorized = new UpdateMaintenanceCoordinator(installation, storage, processes, restartTransport, TimeProvider.System,
            () => new(manifest.RunId, manifest.Nonce, original.ProcessId));
        Assert.True(authorized.CheckStartupAllowed());
        await using var restartedRegistration = await authorized.RegisterAsync(restart, healthParticipant);
        Assert.Null(storage.Read(grantName));
        Assert.False(coordinator.CheckStartupAllowed());
        storage.Write("wire-registered.json", "{}"u8);
        await handoff.ExpectAsync("health-not-ready");
        Assert.Equal(1, healthParticipant.HealthCalls);
        healthParticipant.Ready = true;
        storage.Write("wire-ready.json", "{}"u8);
        await handoff.ExpectAsync("health-ready");
        Assert.Equal(2, healthParticipant.HealthCalls);
        Assert.Equal(gate, UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("gate.json")!));
        await Assert.ThrowsAsync<UpdateMaintenanceBusyException>(() => authorized.PrepareAsync());
        storage.Write("wire-finish.json", "{}"u8);
        await handoff.AssertExitedAsync();
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    public async Task NativeClientCommitReservationNeverConfusesOrdinaryExitWithUpdateShutdown(bool ordinaryExit, bool supportsConsent, bool failShutdown)
    {
        using var root = new TemporaryRoot();
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var install = Path.Combine(local, "Programs", "AgentSignaler", "Remote");
        var native = new WindowsUpdateProcessAdapter();
        var current = native.Current with { ExecutablePath = Path.Combine(install, "AgentSignaler.Client.exe") };
        var processes = new Processes(current, native);
        var installation = new UpdateInstalledPackage(UpdatePackage.Remote, Guid.NewGuid(),
            UpdatePolicy.UpgradeCode(UpdatePackage.Remote), new Version(1, 0, 0), install, current.UserSid);
        using var storage = new UpdateStorage(local, install);
        var transport = new WindowsUpdateMaintenanceTransport(installation, storage, processes, TimeProvider.System);
        var coordinator = new UpdateMaintenanceCoordinator(installation, storage, processes, transport, TimeProvider.System, () => null);
        var participant = new Participant { FailShutdown = failShutdown };
        IUpdateMaintenanceParticipant target = supportsConsent ? participant : new NonConsentingClient();
        await using var registration = await coordinator.RegisterAsync(new(UpdateApplication.Client,
            Path.Combine(root.Path, "remote.json"), true), target);
        await using var ticket = await coordinator.PrepareAsync();
        if (ordinaryExit) Assert.True(participant.TryOrdinaryExit());
        if (ordinaryExit || !supportsConsent)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ticket.CommitAsync(new(ticket.RunId, current)));
            Assert.Equal("update_commit_declined", error.Message);
            Assert.Equal(UpdateMaintenancePhase.Preparing, UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("gate.json")!).Phase);
            Assert.False(participant.Exited.Task.IsCompleted);
            await ticket.DisposeAsync();
            Assert.Null(storage.Read("gate.json"));
        }
        else
        {
            await ticket.CommitAsync(new(ticket.RunId, current));
            await participant.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(participant.TryOrdinaryExit());
            Assert.Equal(UpdateMaintenancePhase.Servicing, UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("gate.json")!).Phase);
            if (failShutdown)
            {
                var instance = UpdateStrictJson.Read<UpdateInstanceRegistration>(storage.Read(storage.List("instance-*.json", 32).Single())!);
                Assert.False(await transport.SendAsync(instance, ticket.RunId, "commit", CancellationToken.None));
                Assert.False(participant.Cancelled.Task.IsCompleted);
            }
        }
    }

    [Theory]
    [InlineData("reserve")]
    [InlineData("commit")]
    public async Task NativeAcknowledgementFailureReleasesReservationBeforeShutdownIsQueued(string command)
    {
        using var root = new TemporaryRoot();
        var local = Path.Combine(root.Path, "Local");
        Directory.CreateDirectory(local);
        var install = Path.Combine(local, "Programs", "AgentSignaler", "Remote");
        var native = new WindowsUpdateProcessAdapter();
        var current = native.Current with { ExecutablePath = Path.Combine(install, "AgentSignaler.Client.exe") };
        var processes = new Processes(current, native);
        var installation = new UpdateInstalledPackage(UpdatePackage.Remote, Guid.NewGuid(),
            UpdatePolicy.UpgradeCode(UpdatePackage.Remote), new Version(1, 0, 0), install, current.UserSid);
        using var storage = new UpdateStorage(local, install);
        var transport = new WindowsUpdateMaintenanceTransport(installation, storage, processes, TimeProvider.System);
        var coordinator = new UpdateMaintenanceCoordinator(installation, storage, processes, transport, TimeProvider.System, () => null);
        var participant = new Participant();
        await using var registration = await coordinator.RegisterAsync(new(UpdateApplication.Client,
            Path.Combine(root.Path, "remote.json"), true), participant);
        var instance = UpdateStrictJson.Read<UpdateInstanceRegistration>(storage.Read(storage.List("instance-*.json", 32).Single())!);
        var runId = Guid.NewGuid();
        var gate = new UpdateMaintenanceGate(1, runId, install, current, DateTimeOffset.UtcNow + UpdateMaintenanceValidation.OperationLease,
            UpdateMaintenancePhase.Preparing, current);
        storage.Write("gate.json", UpdateStrictJson.Write(gate));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Assert.True(await transport.SendAsync(instance, runId, "prepare", budget.Token));
        if (command == "commit")
        {
            Assert.True(await transport.SendAsync(instance, runId, "reserve", budget.Token));
            Assert.False(participant.TryOrdinaryExit());
            gate = gate with { Phase = UpdateMaintenancePhase.Servicing };
            storage.Write("gate.json", UpdateStrictJson.Write(gate));
        }

        var requestRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        processes.BeforeFind = () =>
        {
            requestRead.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test request was not released.");
        };
        try
        {
            using var client = new NamedPipeClientStream(".", instance.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(budget.Token);
            await UpdatePipeFrame.WriteAsync(client, new UpdateMaintenanceMessage(1, runId, command), budget.Token);
            await requestRead.Task.WaitAsync(budget.Token);
            client.Dispose();
            release.Set();
            Assert.Equal(runId, await participant.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(participant.Exited.Task.IsCompleted);
            Assert.True(participant.TryOrdinaryExit());
            Assert.Equal(command == "reserve", await transport.SendAsync(instance, runId, "cancel", budget.Token));
            Assert.Equal(gate, UpdateStrictJson.Read<UpdateMaintenanceGate>(storage.Read("gate.json")!));
        }
        finally
        {
            processes.BeforeFind = null;
            release.Set();
        }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "UpdaterPolicy.ps1"))) return directory.FullName;
        throw new DirectoryNotFoundException("update_fixture_repository_unavailable");
    }

    private sealed class TemporaryRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AS.Wire." + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
    private sealed class Processes(UpdateProcessIdentity current, WindowsUpdateProcessAdapter native) : IUpdateProcessAdapter
    {
        public UpdateProcessIdentity CurrentIdentity = current;
        public Action? BeforeFind;
        public UpdateProcessIdentity Current => CurrentIdentity;
        public UpdateProcessIdentity? Find(int pid)
        {
            BeforeFind?.Invoke();
            return pid == Current.ProcessId ? Current : native.Find(pid);
        }
        public ImmutableArray<UpdateProcessIdentity> Enumerate(UpdateInstalledPackage installation) => [Current];
    }
    private sealed class Transport : IUpdateMaintenanceTransport
    {
        public Task<IAsyncDisposable> ListenAsync(UpdateInstanceRegistration registration, IUpdateMaintenanceParticipant participant, CancellationToken token) =>
            Task.FromResult<IAsyncDisposable>(new Lease());
        public Task<bool> SendAsync(UpdateInstanceRegistration registration, Guid runId, string command, CancellationToken token) => Task.FromResult(true);
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class Participant : IUpdateMaintenanceParticipant, IUpdateMaintenanceHealth, IUpdateMaintenanceCommitConsent
    {
        private readonly object sync = new();
        private Guid prepared;
        private bool ordinaryExit;
        private bool commitAccepted;
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Guid> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailShutdown;
        public volatile bool Ready;
        public int HealthCalls;
        public Task<bool> IsReadyAsync(CancellationToken token)
        {
            Interlocked.Increment(ref HealthCalls);
            return Task.FromResult(Ready);
        }
        public Task<bool> PrepareAsync(Guid runId, CancellationToken token)
        {
            lock (sync)
            {
                if (ordinaryExit || commitAccepted) return Task.FromResult(false);
                prepared = runId;
                return Task.FromResult(true);
            }
        }
        public bool TryOrdinaryExit()
        {
            lock (sync)
            {
                if (commitAccepted) return false;
                ordinaryExit = true; prepared = Guid.Empty;
                return true;
            }
        }
        public bool TryAcceptCommit(Guid runId)
        {
            lock (sync)
            {
                if (ordinaryExit || prepared != runId) return false;
                commitAccepted = true;
                return true;
            }
        }
        public Task CancelAsync(Guid runId, CancellationToken token)
        {
            lock (sync) { prepared = Guid.Empty; commitAccepted = false; }
            Cancelled.TrySetResult(runId);
            return Task.CompletedTask;
        }
        public Task CommitExitAsync(Guid runId, CancellationToken token)
        {
            lock (sync)
            {
                if (ordinaryExit || !commitAccepted || prepared != runId) throw new InvalidOperationException("update_commit_declined");
                Exited.TrySetResult();
            }
            return FailShutdown ? Task.FromException(new IOException("Synthetic queued shutdown failure.")) : Task.CompletedTask;
        }
    }

    private sealed class NonConsentingClient : IUpdateMaintenanceParticipant
    {
        public Task<bool> PrepareAsync(Guid runId, CancellationToken token) => Task.FromResult(true);
        public Task CancelAsync(Guid runId, CancellationToken token) => Task.CompletedTask;
        public Task CommitExitAsync(Guid runId, CancellationToken token) => throw new InvalidOperationException("Client without commit consent must not shut down.");
    }

    private sealed class FixtureTrust : IUpdateScriptTrust
    {
        public Task VerifyAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.Contains(Path.GetFileName(path), UpdateHandoffLauncher.RequiredScripts);
            return Task.CompletedTask;
        }
    }

    private sealed class FixtureHandoff(string repository, string local, Processes processes) : IUpdateHandoffAdapter, IAsyncDisposable
    {
        private NamedPipeServerStream? readiness;
        private Process? child;
        private Task<string>? errors;
        public Task<UpdateProcessIdentity> LaunchAsync(string entryScript, string manifestPath, string pipeName, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            readiness = new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);
            var fixture = Path.Combine(Path.GetDirectoryName(manifestPath)!, "UpdateWireFixture.ps1");
            File.Copy(Path.Combine(repository, "tests", "fixtures", "UpdateWireFixture.ps1"), fixture);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var start = new ProcessStartInfo(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "USERPROFILE", "TEMP", "TMP" })
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["LOCALAPPDATA"] = local;
            start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(powershell)!, "Modules");
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", fixture, "-ManifestPath", manifestPath }) start.ArgumentList.Add(argument);
            child = Process.Start(start) ?? throw new IOException("update_fixture_launch_failed");
            errors = child.StandardError.ReadToEndAsync();
            return Task.FromResult(processes.Find(child.Id) ?? throw new IOException("update_fixture_exited"));
        }

        public async Task WaitForReadyAsync(UpdateProcessIdentity updater, Guid runId, string nonce, string pipeName, CancellationToken token)
        {
            var pipe = readiness!;
            var connected = pipe.WaitForConnectionAsync(token);
            if (await Task.WhenAny(connected, child!.WaitForExitAsync(token)) != connected)
                Assert.Fail("Pure PowerShell wire fixture failed: " + await errors!);
            await connected;
            Assert.True(WindowsUpdateMaintenanceTransport.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid));
            Assert.Equal(updater.ProcessId, checked((int)pid));
            var acknowledgement = await UpdatePipeFrame.ReadAsync<UpdateHandoffAcknowledgement>(pipe, token);
            Assert.Equal(new UpdateHandoffAcknowledgement(1, runId.ToString("N"), nonce, "ready"), acknowledgement);
            pipe.Dispose(); readiness = null;
        }

        public async Task ExpectAsync(string expected)
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var line = await child!.StandardOutput.ReadLineAsync(budget.Token);
            if (line is null) Assert.Fail("Pure PowerShell wire fixture failed: " + await errors!);
            Assert.Equal(expected, line);
        }

        public async Task AssertExitedAsync()
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await child!.WaitForExitAsync(budget.Token);
            Assert.True(child.ExitCode == 0, "Pure PowerShell wire fixture failed: " + await errors!);
            Assert.Equal("", await child.StandardOutput.ReadToEndAsync());
        }

        public async ValueTask DisposeAsync()
        {
            readiness?.Dispose();
            if (child is null) return;
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
            child.Dispose();
        }
    }
}