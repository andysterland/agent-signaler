using System.Collections.Immutable;
using System.Text;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class UpdateMaintenanceTests
{
    private const string Sid = "S-1-5-21-111-222-333-1001";
    private const string Install = @"C:\Users\test\AppData\Local\Programs\AgentSignaler\Remote";
    private static readonly UpdateInstalledPackage Installed = new(UpdatePackage.Remote, Guid.NewGuid(),
        UpdatePolicy.UpgradeCode(UpdatePackage.Remote), new Version(1, 2, 3), Install, Sid);

    [Theory]
    [InlineData("prepare", 60)]
    [InlineData("commit", 15)]
    [InlineData("reserve", 15)]
    [InlineData("cancel", 15)]
    [InlineData("health", 15)]
    [InlineData("unknown", 15)]
    public void OnlyCooperativePrepareReceivesExtendedRequestBudget(string command, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), UpdateMaintenanceValidation.RequestTimeout(command));

    [Theory]
    [InlineData(UpdateMaintenancePhase.Preparing, 1, false)]
    [InlineData(UpdateMaintenancePhase.Preparing, 0, true)]
    [InlineData(UpdateMaintenancePhase.Preparing, -1, true)]
    [InlineData(UpdateMaintenancePhase.Servicing, -100, false)]
    public void PreparationReservationExpiresExactlyButNeverThawsServicing(UpdateMaintenancePhase phase, int secondsUntilDeadline, bool cancel)
    {
        var now = new Clock().GetUtcNow();
        var runId = Guid.NewGuid();
        var gate = new UpdateMaintenanceGate(1, runId, Install, Process(1), now.AddMinutes(30), phase, Process(1));
        Assert.Equal(cancel, UpdateMaintenanceValidation.ShouldCancelPreparation(runId, now.AddSeconds(secondsUntilDeadline), gate, now));
    }

    [Fact]
    public void RemovedPreparingGateImmediatelyUnfreezesAnAbandonedParticipant()
    {
        var now = new Clock().GetUtcNow();
        Assert.True(UpdateMaintenanceValidation.ShouldCancelPreparation(Guid.NewGuid(), now.AddSeconds(90), null, now));
        Assert.Equal(TimeSpan.FromSeconds(90), UpdateMaintenanceValidation.PreparationReservation);
    }

    [Fact]
    public async Task CompetingPrepareReturnsValidatedExistingRunWithoutReleasingItsLease()
    {
        var fixture = new Fixture(1);
        var runId = Guid.NewGuid();
        var owner = Process(90);
        fixture.Processes.Live = fixture.Processes.Live.Add(owner);
        fixture.Storage.Write("gate.json", UpdateStrictJson.Write(new UpdateMaintenanceGate(1, runId, Install, owner,
            fixture.Time.GetUtcNow().AddMinutes(10), UpdateMaintenancePhase.Servicing, Process(1))));
        fixture.Storage.Held = true;
        var error = await Assert.ThrowsAsync<UpdateMaintenanceBusyException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Equal(runId, error.RunId);
        Assert.Equal(UpdateMaintenancePhase.Servicing, error.Phase);
        Assert.Equal("update_maintenance_busy", error.Message);
        Assert.True(fixture.Storage.Held);
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task RefusedRollbackIsReportedAndStillCancelsEveryPreparedParticipant()
    {
        var fixture = new Fixture(2);
        fixture.Transport.RefusePid = 2;
        fixture.Transport.RefuseCancel = true;
        var error = await Assert.ThrowsAsync<UpdateMaintenanceRollbackException>(() => fixture.Coordinator.PrepareAsync());
        Assert.NotEqual(Guid.Empty, error.RunId);
        Assert.Equal("update_prepare_rollback_incomplete", error.Message);
        Assert.Equal(2, fixture.Transport.Commands.Count(c => c.Command == "cancel"));
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Storage.Held);
    }

    [Fact]
    public async Task TicketDisposalReportsIncompleteRollbackInsteadOfSilentlyLeavingGate()
    {
        var fixture = new Fixture(2);
        var ticket = await fixture.Coordinator.PrepareAsync();
        fixture.Transport.RefuseCancel = true;
        var error = await Assert.ThrowsAsync<UpdateMaintenanceRollbackException>(() => ticket.DisposeAsync().AsTask());
        Assert.Equal(ticket.RunId, error.RunId);
        Assert.Equal(2, fixture.Transport.Commands.Count(c => c.Command == "cancel"));
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Storage.Held);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public async Task PreparationNeverCommitsAndDisposalCancelsAll(int count)
    {
        var fixture = new Fixture(count);
        var ticket = await fixture.Coordinator.PrepareAsync();
        Assert.Equal(count, ticket.Participants.Length);
        Assert.Equal(Enumerable.Repeat("prepare", count), fixture.Transport.Commands.Select(c => c.Command));
        Assert.Equal(UpdateMaintenancePhase.Preparing, fixture.Gate.Phase);
        await ticket.DisposeAsync();
        Assert.Equal(count, fixture.Transport.Commands.Count(c => c.Command == "cancel"));
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Storage.Held);
    }

    [Fact]
    public async Task PreparationCapturesCurrentVisibilityAfterParticipantFreezes()
    {
        var fixture = new Fixture(1);
        fixture.Transport.AfterPrepare = () => fixture.Coordinator.RefreshRestartContextAsync(
            new(UpdateApplication.Client, @"C:\Users\test\config.json", false)).GetAwaiter().GetResult();
        await using var ticket = await fixture.Coordinator.PrepareAsync();
        Assert.False(ticket.Participants.Single().Restart.Background);
        Assert.Equal(@"C:\Users\test\config.json", ticket.Participants.Single().Restart.ContextPath);
    }

    [Fact]
    public async Task RegistrationPrunesThirtyTwoProvenDeadInstancesBeforeCapacityCheck()
    {
        var fixture = new Fixture(32);
        fixture.Processes.CurrentIdentity = Process(100);
        fixture.Processes.Live = [fixture.Processes.Current];
        await using var registration = await fixture.Coordinator.RegisterAsync(
            new(UpdateApplication.Client, @"C:\Users\test\config.json", true), new ParticipantFake());
        var name = Assert.Single(fixture.Storage.List("instance-*.json", 32));
        var current = UpdateStrictJson.Read<UpdateInstanceRegistration>(fixture.Storage.Read(name)!);
        Assert.Equal(fixture.Processes.Current, current.Process);
        Assert.Equal("instance-100-200.json", name);
    }

    [Fact]
    public async Task ThirtyThreeParticipantsFailsWithoutAnyConsentOrGateMutation()
    {
        var fixture = new Fixture(33);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Empty(fixture.Transport.Commands);
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Storage.Held);
    }

    [Fact]
    public async Task DifferentWindowsSessionAbortsBeforePreparation()
    {
        var fixture = new Fixture(2);
        fixture.Processes.Live = fixture.Processes.Live.SetItem(1, fixture.Processes.Live[1] with { SessionId = 2 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task UnregisteredExactInstalledProcessIsNotSilentlyIgnored()
    {
        var fixture = new Fixture(2);
        fixture.Storage.Files.Remove(fixture.Storage.Files.Keys.Last());
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task ReusedPidCannotUsePreviousRegistration()
    {
        var fixture = new Fixture(1);
        fixture.Processes.Live = [fixture.Processes.Current with { CreationTimeUtcTicks = 999 }];
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Empty(fixture.Transport.Commands);
    }

    [Fact]
    public async Task RefusalCancelsEvenParticipantWhosePreparationReplyWasLost()
    {
        var fixture = new Fixture(2);
        fixture.Transport.RefusePid = 2;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Equal(["prepare", "prepare", "cancel", "cancel"], fixture.Transport.Commands.Select(c => c.Command));
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Storage.Held);
    }

    [Fact]
    public async Task CancelledPreparationRollsBackAndPropagatesCancellation()
    {
        var fixture = new Fixture(1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Coordinator.PrepareAsync(cancelled.Token));
        Assert.Equal("cancel", fixture.Transport.Commands.Last().Command);
        Assert.Null(fixture.Storage.Read("gate.json"));
    }

    [Fact]
    public async Task InternalPreparationDeadlineIsBoundedFailureNotCallerCancellation()
    {
        var fixture = new Fixture(1);
        fixture.Transport.AfterPrepare = () => throw new OperationCanceledException();
        var error = await Assert.ThrowsAsync<IOException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Equal("update_prepare_timeout", error.Message);
        Assert.Equal(["prepare", "cancel"], fixture.Transport.Commands.Select(c => c.Command));
        Assert.Null(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Storage.Held);
    }

    [Fact]
    public async Task LaunchRaceAfterPreparationAbortsAndUnfreezesParticipants()
    {
        var fixture = new Fixture(1);
        fixture.Transport.AfterPrepare = () => fixture.Processes.Live = fixture.Processes.Live.Add(Process(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Equal(["prepare", "cancel"], fixture.Transport.Commands.Select(c => c.Command));
    }

    [Theory]
    [InlineData(UpdateMaintenancePhase.Preparing, false, -1, true)]
    [InlineData(UpdateMaintenancePhase.Preparing, false, 0, true)]
    [InlineData(UpdateMaintenancePhase.Preparing, false, 1, false)]
    [InlineData(UpdateMaintenancePhase.Preparing, true, -1, false)]
    [InlineData(UpdateMaintenancePhase.Servicing, false, -1, false)]
    public void StartupUsesExactLivenessAndNeverClearsUnknownServicing(UpdateMaintenancePhase phase, bool live, int expiryOffsetSeconds, bool allowed)
    {
        var fixture = new Fixture(1);
        var owner = Process(90);
        if (live) fixture.Processes.Live = fixture.Processes.Live.Add(owner);
        fixture.Storage.Write("gate.json", UpdateStrictJson.Write(new UpdateMaintenanceGate(1, Guid.NewGuid(), Install,
            owner, fixture.Time.GetUtcNow().AddSeconds(expiryOffsetSeconds), phase, Process(1))));
        Assert.Equal(allowed, fixture.Coordinator.CheckStartupAllowed());
        Assert.Equal(allowed, fixture.Storage.Read("gate.json") is null);
    }

    [Fact]
    public void GateBeyondMaximumLeaseIsRejectedNotTreatedAsStale()
    {
        var fixture = new Fixture(1);
        fixture.Storage.Write("gate.json", UpdateStrictJson.Write(new UpdateMaintenanceGate(1, Guid.NewGuid(), Install,
            Process(1), fixture.Time.GetUtcNow() + UpdateMaintenanceValidation.OperationLease + TimeSpan.FromTicks(1),
            UpdateMaintenancePhase.Preparing, Process(1))));
        Assert.Throws<InvalidDataException>(() => fixture.Coordinator.CheckStartupAllowed());
        Assert.NotNull(fixture.Storage.Read("gate.json"));
    }

    [Fact]
    public async Task AuthorizedRestartCanStartButCannotPrepareACompetingUpdate()
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        Assert.True(fixture.Coordinator.CheckStartupAllowed());
        var busy = await Assert.ThrowsAsync<UpdateMaintenanceBusyException>(() => fixture.Coordinator.PrepareAsync());
        Assert.Equal(fixture.Gate.RunId, busy.RunId);
        Assert.Equal(UpdateMaintenancePhase.Restarting, busy.Phase);
        Assert.Empty(fixture.Transport.Commands);
        Assert.NotNull(fixture.Storage.Read("gate.json"));
        Assert.NotNull(fixture.Storage.Read("restart.json"));
    }

    [Fact]
    public void OrdinaryStartupRemainsBlockedThroughoutRestartHealth()
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        fixture.Authorization = null;
        Assert.False(fixture.Coordinator.CheckStartupAllowed());
        Assert.NotNull(fixture.Storage.Read("restart.json"));
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("participant")]
    [InlineData("session")]
    [InlineData("owner-pid-reuse")]
    public void RestartAuthorizationIsBoundToExactManifestParticipantAndLiveUpdater(string mismatch)
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        switch (mismatch)
        {
            case "nonce": fixture.Authorization = fixture.Authorization! with { Nonce = new('c', 64) }; break;
            case "participant": fixture.Authorization = fixture.Authorization! with { OriginalProcessId = 999 }; break;
            case "session": fixture.Processes.CurrentIdentity = fixture.Processes.Current with { SessionId = 9 }; break;
            default: fixture.Processes.Live = fixture.Processes.Live.SetItem(1, fixture.Processes.Live[1] with { CreationTimeUtcTicks = 999 }); break;
        }
        Assert.False(fixture.Coordinator.CheckStartupAllowed());
    }

    [Fact]
    public async Task AuthorizedRestartRegistrationCannotChangeCapturedConfiguration()
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        Assert.True(fixture.Coordinator.CheckStartupAllowed());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Coordinator.RegisterAsync(
            new(UpdateApplication.Client, @"C:\Users\test\different.json", true), new ParticipantFake()));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("creation")]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("executable")]
    [InlineData("run")]
    [InlineData("participant")]
    [InlineData("context")]
    public void RestartGrantRejectsEveryIdentityOrContextMismatch(string mismatch)
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        var name = UpdateMaintenanceRestart.GrantName(fixture.Processes.Current);
        var grant = UpdateStrictJson.Read<UpdateRestartGrant>(fixture.Storage.Read(name)!);
        grant = mismatch switch
        {
            "pid" => grant with { Process = grant.Process with { ProcessId = 999 } },
            "creation" => grant with { Process = grant.Process with { CreationTimeUtcTicks = 999 } },
            "user" => grant with { Process = grant.Process with { UserSid = Sid + "0" } },
            "session" => grant with { Process = grant.Process with { SessionId = 9 } },
            "executable" => grant with { Process = grant.Process with { ExecutablePath = Path.Combine(Install, "AgentSignaler.Configurator.exe") } },
            "run" => grant with { RunId = Guid.NewGuid().ToString("N") },
            "participant" => grant with { OriginalProcessId = 999 },
            _ => grant with { Restart = grant.Restart with { ContextPath = @"C:\Users\test\other.json" } }
        };
        fixture.Storage.Write(name, UpdateStrictJson.Write(grant));
        Assert.False(fixture.Coordinator.CheckStartupAllowed());
        Assert.NotNull(fixture.Storage.Read("gate.json"));
        Assert.NotNull(fixture.Storage.Read("restart.json"));
    }

    [Fact]
    public async Task RegistrationConsumesExactRestartGrantWithoutOpeningTheOperationGate()
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        var name = UpdateMaintenanceRestart.GrantName(fixture.Processes.Current);
        Assert.True(fixture.Coordinator.CheckStartupAllowed());
        Assert.NotNull(fixture.Storage.Read(name));
        await using var registration = await fixture.Coordinator.RegisterAsync(
            new(UpdateApplication.Client, @"C:\Users\test\config.json", true), new ParticipantFake());
        Assert.Null(fixture.Storage.Read(name));
        Assert.NotNull(fixture.Storage.Read("gate.json"));
        Assert.False(fixture.Coordinator.CheckStartupAllowed());
        fixture.Authorization = null;
        Assert.False(fixture.Coordinator.CheckStartupAllowed());
        await Assert.ThrowsAsync<UpdateMaintenanceBusyException>(() => fixture.Coordinator.PrepareAsync());
    }

    [Fact]
    public void StartupWaitsForExactGrantPublishedAfterProcessLaunch()
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        var name = UpdateMaintenanceRestart.GrantName(fixture.Processes.Current);
        var bytes = fixture.Storage.Read(name)!;
        fixture.Storage.Delete(name);
        var reads = 0;
        fixture.Storage.BeforeRead = readName =>
        {
            if (readName == name && ++reads == 3) fixture.Storage.Write(name, bytes);
        };
        Assert.True(fixture.Coordinator.CheckStartupAllowed());
        Assert.Equal(3, reads);
        Assert.NotNull(fixture.Storage.Read("gate.json"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedRestartGateNeverAdmitsWithoutItsMatchingMarker(bool mismatch)
    {
        var fixture = new Fixture(1);
        AddRestartMarker(fixture);
        if (mismatch)
        {
            var marker = fixture.Gate with { RunId = Guid.NewGuid() };
            fixture.Storage.Write("restart.json", UpdateStrictJson.Write(marker));
            Assert.Throws<InvalidDataException>(() => fixture.Coordinator.CheckStartupAllowed());
        }
        else
        {
            fixture.Storage.Delete("restart.json");
            Assert.False(fixture.Coordinator.CheckStartupAllowed());
        }
        Assert.NotNull(fixture.Storage.Read("gate.json"));
    }

    private static void AddRestartMarker(Fixture fixture)
    {
        var runId = Guid.NewGuid();
        var nonce = new string('a', 64);
        var updater = Process(90) with { ExecutablePath = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" };
        fixture.Processes.Live = fixture.Processes.Live.Add(updater);
        var captured = new UpdateInstanceRegistration(1, Process(2), new(UpdateApplication.Client, @"C:\Users\test\config.json", true),
            UpdateMaintenanceValidation.PipeName(Installed, Process(2)));
        var marker = new UpdateMaintenanceGate(1, runId, Install, updater, fixture.Time.GetUtcNow().AddMinutes(10), UpdateMaintenancePhase.Restarting, Process(1));
        fixture.Storage.Write("restart.json", UpdateStrictJson.Write(marker));
        fixture.Storage.Write("gate.json", UpdateStrictJson.Write(marker));
        var manifest = new UpdateHandoffManifest(1, runId.ToString("N"), nonce, "Remote", Install, Installed.ProductCode.ToString("B"), "1.2.3",
            1, "v1.2.4", [], new('b', 64), new('c', 64), Process(1), [captured], [], "AgentSignaler.Update.Ready.v1." + nonce, marker.ExpiresAt);
        fixture.Storage.RunFiles[(runId, "handoff.json")] = UpdateStrictJson.Write(manifest);
        fixture.Authorization = new(runId.ToString("N"), nonce, 2);
        fixture.Storage.Write(UpdateMaintenanceRestart.GrantName(fixture.Processes.Current), UpdateStrictJson.Write(
            new UpdateRestartGrant(1, runId.ToString("N"), 2, fixture.Processes.Current, captured.Restart)));
    }

    private sealed class ParticipantFake : IUpdateMaintenanceParticipant
    {
        public Task<bool> PrepareAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CancelAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CommitExitAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"runId\":\"00000000-0000-0000-0000-000000000001\",\"command\":\"prepare\"}")]
    [InlineData("{\"schemaVersion\":1,\"runId\":\"00000000-0000-0000-0000-000000000001\",\"command\":\"prepare\",\"extra\":true}")]
    [InlineData("{\"schemaVersion\":1,\"command\":\"prepare\"}")]
    public void StrictJsonRejectsDuplicateUnknownAndMissingProperties(string json) =>
        Assert.Throws<System.Text.Json.JsonException>(() => UpdateStrictJson.Read<UpdateMaintenanceMessage>(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData(65536, true)]
    [InlineData(65537, false)]
    public void DocumentByteLimitIsExact(int length, bool accepted)
    {
        var bytes = Encoding.UTF8.GetBytes("\"" + new string('a', length - 2) + "\"");
        if (accepted) Assert.Equal(length - 2, UpdateStrictJson.Read<string>(bytes).Length);
        else Assert.Throws<InvalidDataException>(() => UpdateStrictJson.Read<string>(bytes));
    }

    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public async Task PipeFrameLimitIsExact(int length, bool accepted)
    {
        using var stream = new MemoryStream();
        var body = new string('a', length - 2);
        if (accepted)
        {
            await UpdatePipeFrame.WriteAsync(stream, body, default);
            stream.Position = 0;
            Assert.Equal(body, await UpdatePipeFrame.ReadAsync<string>(stream, default));
        }
        else await Assert.ThrowsAsync<InvalidDataException>(() => UpdatePipeFrame.WriteAsync(stream, body, default));
    }

    [Theory]
    [InlineData(@"C:\install\..\other")]
    [InlineData(@"\\server\share\app")]
    [InlineData(@"C:\app\file:stream")]
    [InlineData("relative")]
    public void UnsafeRestartPathsRejected(string path) => Assert.Throws<InvalidDataException>(() => UpdatePath.Canonical(path));

    private static UpdateProcessIdentity Process(int id) => new(id, 100 + id, Sid, 1, Path.Combine(Install, "AgentSignaler.Client.exe"));
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 20, 0, 0, TimeSpan.Zero); }
    private sealed class Fixture
    {
        public readonly MemoryStorage Storage = new();
        public readonly ProcessFake Processes = new();
        public readonly TransportFake Transport = new();
        public readonly Clock Time = new();
        public UpdateRestartAuthorization? Authorization;
        public UpdateMaintenanceCoordinator Coordinator { get; }
        public UpdateMaintenanceGate Gate => UpdateStrictJson.Read<UpdateMaintenanceGate>(Storage.Read("gate.json")!);
        public Fixture(int count)
        {
            Processes.Live = Enumerable.Range(1, count).Select(Process).ToImmutableArray();
            foreach (var process in Processes.Live)
            {
                var registration = new UpdateInstanceRegistration(1, process,
                    new(UpdateApplication.Client, @"C:\Users\test\config.json", true), UpdateMaintenanceValidation.PipeName(Installed, process));
                Storage.Write($"instance-{process.ProcessId}-{process.CreationTimeUtcTicks}.json", UpdateStrictJson.Write(registration));
            }
            Coordinator = new(Installed, Storage, Processes, Transport, Time, () => Authorization);
        }
    }
    private sealed class ProcessFake : IUpdateProcessAdapter
    {
        public ImmutableArray<UpdateProcessIdentity> Live = [];
        public UpdateProcessIdentity CurrentIdentity = Process(1);
        public UpdateProcessIdentity Current => CurrentIdentity;
        public UpdateProcessIdentity? Find(int processId) => Live.FirstOrDefault(p => p.ProcessId == processId);
        public ImmutableArray<UpdateProcessIdentity> Enumerate(UpdateInstalledPackage installation) => Live;
    }
    private sealed class TransportFake : IUpdateMaintenanceTransport
    {
        public readonly List<(int Pid, string Command)> Commands = [];
        public int RefusePid;
        public bool RefuseCancel;
        public Action? AfterPrepare;
        public Task<IAsyncDisposable> ListenAsync(UpdateInstanceRegistration registration, IUpdateMaintenanceParticipant participant, CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new Endpoint());
        public Task<bool> SendAsync(UpdateInstanceRegistration registration, Guid runId, string command, CancellationToken cancellationToken)
        {
            Commands.Add((registration.Process.ProcessId, command));
            cancellationToken.ThrowIfCancellationRequested();
            if (command == "prepare") AfterPrepare?.Invoke();
            return Task.FromResult(command == "cancel" ? !RefuseCancel : command != "prepare" || registration.Process.ProcessId != RefusePid);
        }
        private sealed class Endpoint : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class MemoryStorage : IUpdateStorage
    {
        public readonly Dictionary<string, byte[]> Files = [];
        public readonly Dictionary<(Guid Run, string Name), byte[]> RunFiles = [];
        public bool Held;
        public Action<string>? BeforeRead;
        public string DirectoryPath => @"C:\storage";
        public IDisposable AcquireLease()
        {
            if (Held) throw new IOException("busy");
            Held = true;
            return new Lease(this);
        }
        public byte[]? Read(string name, int maximumBytes = 65536)
        {
            BeforeRead?.Invoke(name);
            return Files.GetValueOrDefault(name);
        }
        public void Write(string name, ReadOnlySpan<byte> bytes) => Files[name] = bytes.ToArray();
        public void Delete(string name) => Files.Remove(name);
        public IReadOnlyList<string> List(string pattern, int maximumCount) => Files.Keys.Where(k => k.StartsWith("instance-", StringComparison.Ordinal)).ToArray();
        public string CreateRunDirectory(Guid runId) => throw new NotSupportedException();
        public byte[]? ReadRun(Guid runId, string name) => RunFiles.GetValueOrDefault((runId, name));
        public IReadOnlyList<Guid> ListRuns() => RunFiles.Keys.Select(k => k.Run).Distinct().ToArray();
        private sealed class Lease(MemoryStorage storage) : IDisposable { public void Dispose() => storage.Held = false; }
    }
}