using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class DashboardRuntimeTests
{
    [Fact]
    public async Task FailedImmediatePrivacyOptOutCannotBeUndoneByUnrelatedSettingsSave()
    {
        await using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        Assert.True(runtime.Settings.State.Saved.ReceiveDetailedConversations);
        runtime.DisableDetailedReception();
        using (var locked = new FileStream(Path.Combine(fixture.Directory, "dashboard-settings.json"),
            FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failed = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved with { ReceiveDetailedConversations = false },
                runtime.HostInstanceId, runtime.Settings.Revision);
            Assert.Equal(1008, failed.Error!.Code);
            var failedEnable = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved,
                runtime.HostInstanceId, runtime.Settings.Revision, explicitDetailedReceptionEnable: true);
            Assert.Equal(1008, failedEnable.Error!.Code);
            Assert.False(runtime.Settings.State.Effective.ReceiveDetailedConversations);
        }
        Assert.True(runtime.Settings.State.Saved.ReceiveDetailedConversations);
        var unrelated = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved with { Compact = false },
            runtime.HostInstanceId, runtime.Settings.Revision);
        Assert.True(unrelated.Succeeded);
        Assert.True(unrelated.Snapshot!.State.Saved.ReceiveDetailedConversations);
        Assert.False(unrelated.Snapshot.State.Effective.ReceiveDetailedConversations);
        Assert.False(runtime.Receiver.State.ReceiveDetailedConversations);
        var enabled = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved,
            runtime.HostInstanceId, runtime.Settings.Revision, explicitDetailedReceptionEnable: true);
        Assert.True(enabled.Succeeded);
        Assert.True(enabled.Snapshot!.State.Effective.ReceiveDetailedConversations);
    }

    [Fact]
    public async Task NewPrivacyOptOutDuringEnablePublicationRemainsEffective()
    {
        await using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        runtime.DisableDetailedReception();
        var disabledAgain = false;
        runtime.Changed += change =>
        {
            if (change.Domain != "settings" || disabledAgain || !runtime.Settings.State.Effective.ReceiveDetailedConversations) return;
            disabledAgain = true;
            runtime.DisableDetailedReception();
        };
        var enabled = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved,
            runtime.HostInstanceId, runtime.Settings.Revision, explicitDetailedReceptionEnable: true);
        Assert.True(enabled.Succeeded);
        Assert.True(disabledAgain);
        Assert.False(enabled.Snapshot!.State.Effective.ReceiveDetailedConversations);
        Assert.False(runtime.Receiver.State.ReceiveDetailedConversations);
    }

    [Fact]
    public async Task InvalidOrBusyDiagnosticCannotReuseCachedSuccessForAnotherPath()
    {
        var entered = Signal();
        await using var fixture = new Fixture(new()
        {
            Diagnostic = async (kind, _, token) =>
            {
                if (kind == RuntimePrerequisiteKind.DevCenterExtension)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return new(true, "Synthetic passed.");
            }
        });
        var runtime = fixture.Runtime;
        var success = await runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli, @"C:\synthetic\az.exe");
        Assert.True(success.Succeeded);
        Assert.True(PrerequisiteCheck.FromRuntimeResult(success, RuntimePrerequisiteKind.AzureCli,
            @"C:\synthetic\az.exe", _ => "Rejected").Passed);
        Assert.False(PrerequisiteCheck.FromRuntimeResult(success, RuntimePrerequisiteKind.AzureCli,
            @"C:\other\az.exe", _ => "Rejected").Passed);
        var invalid = await runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli, "relative.exe");
        Assert.Equal(1001, invalid.Error!.Code);
        Assert.False(PrerequisiteCheck.FromRuntimeResult(invalid, RuntimePrerequisiteKind.AzureCli, "relative.exe", _ => "Rejected").Passed);
        var occupying = runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.DevCenterExtension, @"C:\synthetic\az.exe");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var busy = await runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli, @"C:\other\az.exe");
            Assert.Equal(1003, busy.Error!.Code);
            Assert.True(runtime.Prerequisites.State.Single(item => item.Id == "AzureCli").Result!.Passed);
            Assert.False(PrerequisiteCheck.FromRuntimeResult(busy, RuntimePrerequisiteKind.AzureCli, @"C:\other\az.exe", _ => "Rejected").Passed);
        }
        finally { runtime.CancelPrerequisites(); }
        Assert.Equal(1006, (await occupying).Error!.Code);
    }

    [Fact]
    public async Task FailedDiagnosticReturnsOnlyItsOwnMatchingRequestDetails()
    {
        await using var fixture = new Fixture(new()
        {
            Diagnostic = (_, _, _) => Task.FromResult(new PrerequisiteDiagnosticResult(false, "Synthetic prerequisite is missing."))
        });
        var outcome = await fixture.Runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli, @"C:\synthetic\az.exe");
        Assert.Equal(1010, outcome.Error!.Code);
        var result = PrerequisiteCheck.FromRuntimeResult(outcome, RuntimePrerequisiteKind.AzureCli, @"C:\synthetic\az.exe", _ => "Rejected");
        Assert.False(result.Passed);
        Assert.Equal("Synthetic prerequisite is missing.", result.Details);
        Assert.NotEqual(result.Details, PrerequisiteCheck.FromRuntimeResult(outcome,
            RuntimePrerequisiteKind.AzureCli, @"C:\other\az.exe", _ => "Rejected").Details);
    }

    [Fact]
    public async Task StorageProgressPrecedesWorkAndStartupStaysLoadingUntilCancellation()
    {
        var entered = Signal();
        DashboardRuntime? runtime = null;
        await using var fixture = new Fixture(new()
        {
            CreateStoreAsync = async (_, _, token) =>
            {
                Assert.Equal("storage", runtime!.Status.State.Stage);
                Assert.Equal(RuntimeLifecycle.Initializing, runtime.Status.State.Lifecycle);
                Assert.False(runtime.Status.State.InitialAttemptCompleted);
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException("Synthetic storage creation must be cancelled.");
            }
        });
        runtime = fixture.Runtime;
        var progress = new ConcurrentQueue<RuntimeStatus>();
        runtime.Changed += change =>
        {
            if (change.Domain == "system") progress.Enqueue(runtime.Status.State);
        };
        var initialization = runtime.InitializeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initialization.IsCompleted);
            Assert.Null(runtime.Server);
            Assert.Contains(progress, state => state.Stage == "storage" && !state.InitialAttemptCompleted);
        }
        finally { runtime.CancelStartup(); }
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(runtime.Status.State.InitialAttemptCompleted);
        Assert.Equal("startupCancelled", runtime.Status.State.Stage);
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "dashboard.db")));
    }

    [Fact]
    public async Task StartupStagesPrecedeReceiverMachinesAndSharingWorkAndStayInitializingUntilVerificationCompletes()
    {
        var verifying = Signal();
        var verified = Signal();
        var reads = 0;
        DashboardRuntime? runtime = null;
        await using var fixture = new Fixture(new()
        {
            TunnelRunner = new FakeTunnelRunner(),
            TunnelHealthProbe = new StartupProgressProbe(async token =>
            {
                Assert.Equal("sharing", runtime!.Status.State.Stage);
                Assert.False(runtime.Status.State.InitialAttemptCompleted);
                verifying.TrySetResult();
                await verified.Task.WaitAsync(token);
            }),
            ReadMachinesAsync = (store, token) =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                    Assert.Equal("machines", runtime!.Status.State.Stage);
                return store.GetMachinesAsync(token);
            }
        }, """{"ConnectionMode":"DevTunnel","AutoStartSharing":true,"ReceiveDetailedConversations":false}""");
        runtime = fixture.Runtime;
        var stages = new ConcurrentQueue<string>();
        runtime.Changed += change =>
        {
            if (change.Domain != "system") return;
            var status = runtime.Status.State;
            stages.Enqueue(status.Stage);
            if (status.Lifecycle != RuntimeLifecycle.Initializing) return;
            if (status.Stage == "receiver" && !status.ReceiverAvailable) Assert.Null(runtime.Server);
            if (status.Stage == "machines") Assert.Equal(0, Volatile.Read(ref reads));
            if (status.Stage == "sharing" && !status.SharingAvailable) Assert.Null(runtime.Tunnel);
        };
        var initialization = runtime.InitializeAsync();
        try
        {
            await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(initialization.IsCompleted);
            Assert.Equal(RuntimeLifecycle.Initializing, runtime.Status.State.Lifecycle);
            Assert.False(runtime.Status.State.InitialAttemptCompleted);
            Assert.Equal(new[] { "storage", "receiver", "machines", "sharing" }, stages.Distinct());
        }
        finally { verified.TrySetResult(); }
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeLifecycle.Operational, runtime.Status.State.Lifecycle);
        Assert.True(runtime.Status.State.InitialAttemptCompleted);
        Assert.Equal(new[] { "storage", "receiver", "machines", "sharing", "ready" }, stages.Distinct());
    }

    private sealed class StartupProgressProbe(Func<CancellationToken, Task> verify) : ITunnelHealthProbe
    {
        public Task VerifyAsync(Uri baseUri, CancellationToken token) => verify(token);
    }

    [Fact]
    public async Task EntryPointOwnsLeaseAcrossRuntimeDisposalAndReceiverReallyStartsAndStops()
    {
        await using var fixture = new Fixture();
        var runtime = fixture.Runtime;
        Assert.Equal(RuntimeLifecycle.TransportReady, runtime.Status.State.Lifecycle);
        Assert.Null(runtime.Store);
        Assert.Equal(0, runtime.Settings.Revision);
        await runtime.InitializeAsync();
        Assert.Equal(RuntimeLifecycle.Operational, runtime.Status.State.Lifecycle);
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://127.0.0.1:{runtime.Receiver.State.Port}";
        var tests = 0;
        runtime.ConnectionTestReceived += () => Interlocked.Increment(ref tests);
        client.DefaultRequestHeaders.Add(Protocol.ConnectionTestHeader, "1");
        Assert.True((await client.GetAsync(url + "/health")).IsSuccessStatusCode);
        Assert.Equal(1, tests);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.GetAsync(url + "/rpc")).StatusCode);
        Assert.True((await runtime.ShutdownAsync()).Clean);
        Assert.True((await runtime.ShutdownAsync()).Clean);
        Assert.True(fixture.Lease.IsHeld);
        Assert.Throws<DashboardOwnershipException>(() => DashboardResourceLease.Acquire(fixture.Directory, fixture.Directory));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(url + "/health"));
        fixture.Lease.Dispose();
        using var next = DashboardResourceLease.Acquire(fixture.Directory, fixture.Directory);
        Assert.True(next.IsHeld);
    }

    [Fact]
    public async Task RuntimeRefusesDisposedLeaseWithoutOpeningMutableStorage()
    {
        await using var fixture = new Fixture();
        fixture.Lease.Dispose();
        Assert.Throws<DashboardOwnershipException>(() => new DashboardRuntime(fixture.Lease));
        Assert.False(File.Exists(Path.Combine(fixture.Directory, "dashboard.db")));
    }

    [Fact]
    public async Task MachineStateAndRevisionsAreIndependentAndLegacyNotesAreChunkedWithoutRewrite()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.InitializeAsync();
        var firstId = await fixture.AddMachineAsync();
        var secondId = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var settingsRevision = runtime.Settings.Revision;
        var first = runtime.GetMachine(firstId);
        var other = runtime.GetMachine(secondId);
        var result = await runtime.UpdateMachineAsync(firstId, "Synthetic name", "note", runtime.HostInstanceId, first.Revision);
        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.Equal("note", result.Snapshot!.State.Machine.Note);
        Assert.True(result.Snapshot.Revision > first.Revision);
        Assert.Equal(other.Revision, runtime.GetMachine(secondId).Revision);
        Assert.Equal(settingsRevision, runtime.Settings.Revision);
        Assert.Null(first.State.Machine.Note);
        var legacy = new string('n', 40000);
        await runtime.Store!.UpdateDetailsAsync(firstId, "Legacy", legacy);
        await runtime.RefreshMachinesAsync();
        var chunk = runtime.GetNote(firstId);
        Assert.Equal(16384, chunk.State.Text.Length);
        var next = runtime.GetNote(firstId, chunk.State.NextOffset!.Value, expectedRevision: chunk.Revision);
        var last = runtime.GetNote(firstId, next.State.NextOffset!.Value, expectedRevision: chunk.Revision);
        Assert.Equal(legacy, chunk.State.Text + next.State.Text + last.State.Text);
        Assert.Null(last.State.NextOffset);
        var invalid = await runtime.UpdateMachineAsync(firstId, "New", legacy + "x", runtime.HostInstanceId, chunk.Revision);
        Assert.Equal(1001, invalid.Error!.Code);
        Assert.Equal(legacy, runtime.GetMachine(firstId).State.Machine.Note);
    }

    [Fact]
    public async Task PaginationUsesCurrentRevisionAndRejectsStaleMissingAndInvalidRanges()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var page = runtime.GetMachines(limit: 1);
        Assert.Equal(2, page.State.Total);
        Assert.Single(page.State.Items);
        Assert.Equal(1, page.State.NextOffset);
        Assert.Equal(1001, Assert.Throws<RuntimeCommandException>(() => runtime.GetMachines(1)).Error.Code);
        Assert.Single(runtime.GetMachines(1, 1, page.Revision).State.Items);
        Assert.Equal(1001, Assert.Throws<RuntimeCommandException>(() => runtime.GetMachines(limit: 251)).Error.Code);
        Assert.Equal(1001, Assert.Throws<RuntimeCommandException>(() => runtime.GetMachines(-1)).Error.Code);
        var changed = await runtime.UpdateMachineAsync(id, "Changed", null, runtime.HostInstanceId, runtime.GetMachine(id).Revision);
        Assert.True(changed.Succeeded);
        Assert.Equal(1004, Assert.Throws<RuntimeCommandException>(() => runtime.GetMachines(1, 1, page.Revision)).Error.Code);
    }

    [Fact]
    public async Task MaximumSupportedMachinesAndSessionsRemainCompleteAndDeterministicallyPaged()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        var now = DateTimeOffset.UtcNow;
        var sessions = Enumerable.Range(0, Protocol.MaxSessions).Select(index => new SessionSnapshot
        {
            SessionId = $"session-{index:D3}", UnderlyingState = AgentState.Executing, UpdatedAtUtc = now
        }).ToArray();
        var template = new PresenceReport
        {
            Kind = PresenceKind.Started, EventId = Guid.NewGuid(), MachineId = Guid.NewGuid(),
            MachineName = "synthetic", ClientVersion = "1.0", Generation = 1, Sequence = 1,
            ReportedAtUtc = now, HeartbeatIntervalSeconds = 60, Sessions = sessions
        };
        for (var index = 0; index < 25; index++)
            await runtime.Store!.AcceptAsync(template with { MachineId = Guid.NewGuid(), EventId = Guid.NewGuid() });
        await runtime.RefreshMachinesAsync();
        var page = runtime.GetMachines(limit: 250);
        Assert.Equal(25, page.State.Items.Count);
        Assert.Equal(25, page.State.Total);
        Assert.Null(page.State.NextOffset);
        Assert.Equal(page.State.Items.Select(item => item.Machine.MachineId).Order(), page.State.Items.Select(item => item.Machine.MachineId));
        foreach (var machine in page.State.Items)
        {
            var first = runtime.GetSessions(machine.Machine.MachineId, limit: 32);
            var second = runtime.GetSessions(machine.Machine.MachineId, 32, 32, first.Revision);
            Assert.Equal(Protocol.MaxSessions, first.State.Items.Count + second.State.Items.Count);
            Assert.Null(second.State.NextOffset);
        }
        await Assert.ThrowsAsync<CapacityException>(() => runtime.Store!.AcceptAsync(template));
    }

    [Fact]
    public async Task MutationsRequireCurrentHostRevisionAndDestructiveConfirmation()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var revision = runtime.GetMachine(id).Revision;
        Assert.Equal(1004, (await runtime.UpdateMachineAsync(id, null, "x", Guid.NewGuid(), revision)).Error!.Code);
        Assert.Equal(1004, (await runtime.UpdateMachineAsync(id, null, "x", runtime.HostInstanceId, revision + 1)).Error!.Code);
        Assert.Equal(1009, (await runtime.RemoveMachineAsync(id, false, runtime.HostInstanceId, revision)).Error!.Code);
        Assert.Equal(1009, (await runtime.WindowsAppAsync(id, WindowsAppOperation.Clear, null, false,
            runtime.HostInstanceId, revision)).Error!.Code);
        var removed = await runtime.RemoveMachineAsync(id, true, runtime.HostInstanceId, revision);
        Assert.True(removed.Succeeded);
        Assert.Empty(removed.Snapshot!.State.Items);
        Assert.Equal(RuntimeCommitState.Committed, removed.CommitState);
    }

    [Fact]
    public async Task SettingsPersistSavedNotOverriddenValuesAndPreserveUnknownAndVisualFields()
    {
        await using var fixture = new Fixture(new() { RpcPortOverride = 55001 }, """
            {"Port":51820,"RpcPort":55002,"ConnectionMode":0,"AutoStartSharing":false,
             "Theme":"Dark","Compact":false,"Future":{"untouched":[1,true,null]}}
            """);
        var runtime = fixture.Runtime;
        var snapshot = runtime.Settings;
        Assert.Equal(55001, snapshot.State.Effective.RpcPort);
        var updated = await runtime.UpdateSettingsAsync(snapshot.State.Saved with { Port = 54001 },
            runtime.HostInstanceId, snapshot.Revision);
        Assert.True(updated.Succeeded);
        Assert.Equal(["port", "rpcPort"], updated.Snapshot!.State.RestartRequired);
        Assert.Equal(51820, updated.Snapshot.State.Effective.Port);
        var saved = DashboardSettings.Load(fixture.Directory);
        Assert.Equal(55002, saved.RpcPort);
        Assert.Equal("Dark", saved.Theme);
        Assert.False(saved.Compact);
        Assert.True(JsonElement.DeepEquals(snapshot.State.Saved.ExtensionData!["Future"], saved.ExtensionData!["Future"]));
        Assert.Equal(1004, (await runtime.UpdateSettingsAsync(saved, runtime.HostInstanceId, snapshot.Revision)).Error!.Code);
    }

    [Fact]
    public async Task CorruptSettingsDisableAutomaticSharingAndExposeRecoveryWhileStorageFailureStaysControllable()
    {
        await using var fixture = new Fixture(new()
        {
            StoreFactory = (_, _) => throw new IOException("private path must not escape")
        }, "{bad json");
        Assert.True(fixture.Runtime.Settings.State.Recovered);
        Assert.False(fixture.Runtime.Settings.State.Effective.AutoStartSharing);
        var errors = new List<RuntimeError>();
        fixture.Runtime.Problem += errors.Add;
        await fixture.Runtime.InitializeAsync();
        Assert.Equal(RuntimeLifecycle.Degraded, fixture.Runtime.Status.State.Lifecycle);
        Assert.True(fixture.Runtime.Status.State.InitialAttemptCompleted);
        Assert.Single(errors);
        var saved = await fixture.Runtime.UpdateSettingsAsync(fixture.Runtime.Settings.State.Saved with { Port = 54009 },
            fixture.Runtime.HostInstanceId, fixture.Runtime.Settings.Revision);
        Assert.True(saved.Succeeded);
    }

    [Fact]
    public async Task AzureAccountGateIsAtomicAndCancellationHoldsItUntilCleanupFinishes()
    {
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        await using var fixture = new Fixture(new()
        {
            Diagnostic = async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); await cleanup.Task; }
                return new(true, "synthetic");
            }
        });
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        var running = runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli, @"C:\Synthetic\az.exe");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await runtime.RefreshCatalogAsync(null, null, runtime.HostInstanceId, runtime.Settings.Revision);
        Assert.Equal(1003, busy.Error!.Code);
        runtime.CancelPrerequisites(RuntimePrerequisiteKind.AzureCli);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(running.IsCompleted);
        Assert.Equal(1003, (await runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.DevCenterExtension)).Error!.Code);
        var settings = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved with { Compact = false },
            runtime.HostInstanceId, runtime.Settings.Revision);
        Assert.True(settings.Succeeded);
        cleanup.TrySetResult();
        Assert.Equal(1006, (await running).Error!.Code);
    }

    [Fact]
    public async Task MappingCommitSurvivesCancellationBeforeWindowsActivation()
    {
        var platform = new FakePlatform();
        await using var fixture = new Fixture(new() { ConnectionResolver = new FakeResolver(), WindowsAppPlatform = platform });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        await runtime.Store!.SetWindowsAppConnectionAsync(id, Mapping);
        await runtime.RefreshMachinesAsync();
        using var cancelled = new CancellationTokenSource();
        platform.BeforeActivate = cancelled.Cancel;
        var result = await runtime.WindowsAppAsync(id, WindowsAppOperation.Open, null, false,
            runtime.HostInstanceId, runtime.GetMachine(id).Revision, cancelled.Token);
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.Equal(1006, result.Error!.Code);
        var stored = (await runtime.Store.GetMachinesAsync()).Single();
        Assert.NotNull(stored.WindowsAppConnection!.LastKnownConnectionUri);
    }

    [Fact]
    public async Task CachedActivationReportsUnknownWhenPlatformCannotConfirmItsSideEffect()
    {
        var platform = new FakePlatform
        {
            BeforeActivate = () => throw new WindowsAppConnectionException(WindowsAppFailure.ActivationFailed)
        };
        await using var fixture = new Fixture(new() { WindowsAppPlatform = platform });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var resolved = await new FakeResolver().ResolveAsync(Mapping, CancellationToken.None);
        await runtime.Store!.SetWindowsAppConnectionAsync(id, Mapping with
        {
            LastKnownConnectionUri = resolved.ConnectionUri.OriginalString,
            ConnectionUriRetrievedAtUtc = resolved.RetrievedAtUtc
        });
        await runtime.RefreshMachinesAsync();
        var result = await runtime.WindowsAppAsync(id, WindowsAppOperation.OpenLastKnown, null, false,
            runtime.HostInstanceId, runtime.GetMachine(id).Revision);
        Assert.Equal(RuntimeCommitState.Unknown, result.CommitState);
        Assert.Equal(RuntimeCommitState.Unknown, result.Error!.CommitState);
        Assert.Equal(1010, result.Error.Code);
    }

    [Fact]
    public async Task SuccessfulSignInRemainsCommittedWhenSubsequentConnectionResolutionFails()
    {
        var resolver = new FakeResolver
        {
            Resolve = (_, _) => Task.FromException<ResolvedDevBoxConnection>(
                new WindowsAppConnectionException(WindowsAppFailure.AccountMismatch))
        };
        await using var fixture = new Fixture(new() { AzureCli = new FakeAzure(), ConnectionResolver = resolver });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        await runtime.Store!.SetWindowsAppConnectionAsync(id, Mapping);
        await runtime.RefreshMachinesAsync();
        var result = await runtime.WindowsAppAsync(id, WindowsAppOperation.SignIn, null, false,
            runtime.HostInstanceId, runtime.GetMachine(id).Revision);
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.Equal(RuntimeCommitState.Committed, result.Error!.CommitState);
        Assert.Equal(1010, result.Error.Code);
        Assert.Equal(Mapping, runtime.GetMachine(id).State.Machine.WindowsAppConnection);
    }

    [Fact]
    public async Task PerMachineGateBlocksRemovalWhileFreshOpenOwnsItAndAllowsOtherMachine()
    {
        var entered = Signal();
        var resolver = new FakeResolver { Resolve = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        }};
        await using var fixture = new Fixture(new() { ConnectionResolver = resolver, WindowsAppPlatform = new FakePlatform() });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var other = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        await runtime.Store!.SetWindowsAppConnectionAsync(id, Mapping);
        await runtime.RefreshMachinesAsync();
        var pending = runtime.WindowsAppAsync(id, WindowsAppOperation.Open, null, false,
            runtime.HostInstanceId, runtime.GetMachine(id).Revision);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1003, (await runtime.RemoveMachineAsync(id, true, runtime.HostInstanceId, runtime.GetMachine(id).Revision)).Error!.Code);
        Assert.True((await runtime.UpdateMachineAsync(other, "Independent", null,
            runtime.HostInstanceId, runtime.GetMachine(other).Revision)).Succeeded);
        runtime.CancelWindowsApp(id);
        Assert.Equal(1006, (await pending).Error!.Code);
    }

    [Fact]
    public async Task DiagnosticsCaptureUnsavedPathsAndNeverSwitchOperationalSettings()
    {
        var paths = new ConcurrentBag<string?>();
        await using var fixture = new Fixture(new()
        {
            Diagnostic = (_, path, _) => { paths.Add(path); return Task.FromResult(new PrerequisiteDiagnosticResult(true, "Synthetic passed.")); }
        });
        var runtime = fixture.Runtime;
        var saved = runtime.Settings;
        var result = await runtime.CheckAllPrerequisitesAsync(@"C:\Unsaved\az.exe", @"C:\Unsaved\devtunnel.exe");
        Assert.True(result.Succeeded);
        Assert.All(result.Snapshot!.State, item => Assert.Equal(PrerequisiteCheckState.Passed, item.State));
        Assert.Equal(2, paths.Count(path => path == @"C:\Unsaved\az.exe"));
        Assert.Contains(@"C:\Unsaved\devtunnel.exe", paths);
        Assert.Equal(saved, runtime.Settings);
    }

    [Fact]
    public async Task ClockOnlySessionExpiryPublishesWithinPollingIntervalWithoutReports()
    {
        var clock = new ManualClock();
        await using var fixture = new Fixture(new() { TimeProvider = clock });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync(AgentEvent.AgentStop);
        var runtime = fixture.Runtime;
        var before = runtime.GetMachine(id);
        Assert.Equal(AgentState.Succeeded, before.State.Sessions.Single().State);
        var changed = Signal();
        runtime.Changed += change => { if (change.Domain == "machines" && change.MachineId == id) changed.TrySetResult(); };
        clock.Advance(Protocol.ResultDuration + TimeSpan.FromSeconds(1));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var after = runtime.GetMachine(id);
        Assert.Equal(AgentState.Waiting, after.State.Sessions.Single().State);
        Assert.True(after.Revision > before.Revision);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task DomainCancellationCancelsCheckAllIncludingItsNotYetAdmittedAzureExtension()
    {
        var entered = Signal();
        var calls = new ConcurrentBag<RuntimePrerequisiteKind>();
        await using var fixture = new Fixture(new()
        {
            Diagnostic = async (kind, _, token) =>
            {
                calls.Add(kind);
                if (kind == RuntimePrerequisiteKind.AzureCli)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }
                return new(true, "Synthetic passed.");
            }
        });
        var pending = fixture.Runtime.CheckAllPrerequisitesAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Runtime.CancelPrerequisites();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1006, result.Error!.Code);
        Assert.DoesNotContain(RuntimePrerequisiteKind.DevCenterExtension, calls);
    }

    [Fact]
    public async Task ReappearingMachineNeverReusesEarlierEntityRevisions()
    {
        await using var fixture = new Fixture();
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var before = runtime.GetWindowsAppState(id);
        var machine = runtime.GetMachine(id);
        Assert.True((await runtime.RemoveMachineAsync(id, true, runtime.HostInstanceId, machine.Revision)).Succeeded);
        await fixture.AddMachineAsync(machineId: id);
        Assert.True(runtime.GetMachine(id).Revision > machine.Revision);
        Assert.True(runtime.GetWindowsAppState(id).Revision > before.Revision);
    }

    [Theory]
    [InlineData((int)WindowsAppOperation.Map)]
    [InlineData((int)WindowsAppOperation.SignIn)]
    [InlineData((int)WindowsAppOperation.Refresh)]
    [InlineData((int)WindowsAppOperation.Open)]
    [InlineData((int)WindowsAppOperation.OpenLastKnown)]
    [InlineData((int)WindowsAppOperation.Clear)]
    public async Task EveryWindowsAppCommandUsesTheSharedRuntime(int operationValue)
    {
        var operation = (WindowsAppOperation)operationValue;
        await using var fixture = new Fixture(new()
        {
            AzureCli = new FakeAzure(), ConnectionResolver = new FakeResolver(), WindowsAppPlatform = new FakePlatform()
        });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var resolved = await new FakeResolver().ResolveAsync(Mapping, default);
        await runtime.Store!.SetWindowsAppConnectionAsync(id, Mapping with
        {
            LastKnownConnectionUri = resolved.ConnectionUri.OriginalString, ConnectionUriRetrievedAtUtc = resolved.RetrievedAtUtc
        });
        await runtime.RefreshMachinesAsync();
        var selection = new DevBoxMappingSelection(new(Mapping.DevCenterEndpoint, Mapping.ProjectName, "synthetic-pool",
            Mapping.DevBoxName, "Running", "Succeeded", null, null, null, null), Mapping.AzureAccountUpn, Mapping.AzureTenantId);
        var result = await runtime.WindowsAppAsync(id, operation, selection, true, runtime.HostInstanceId, runtime.GetMachine(id).Revision);
        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.False(result.Snapshot!.State.IsBusy);
        Assert.Equal(operation != WindowsAppOperation.Clear, result.Snapshot.State.Mapping is not null);
    }

    [Fact]
    public async Task CatalogProgressAndResultsPublishFromOneRuntimeAndPersistDiscoveryTarget()
    {
        var items = new List<DevBoxCatalogItem> { new(Mapping.DevCenterEndpoint, Mapping.ProjectName, "pool",
            Mapping.DevBoxName, "Running", "Succeeded", null, null, null, null) };
        await using var fixture = new Fixture(new() { CatalogService = new FakeCatalog(items) });
        await fixture.Runtime.InitializeAsync();
        var changes = new ConcurrentBag<RuntimeInvalidation>();
        var runtime = fixture.Runtime;
        runtime.Changed += changes.Add;
        var result = await runtime.RefreshCatalogAsync(Tenant, null, runtime.HostInstanceId, runtime.Settings.Revision);
        Assert.True(result.Succeeded);
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.Single(result.Snapshot!.State.Snapshot!.Items);
        Assert.Equal(Tenant, DashboardSettings.Load(fixture.Directory).DevBoxSubscriptionId);
        Assert.Contains(changes, change => change.Domain == "devboxes");
        items.Clear();
        Assert.Single(runtime.Catalog.State.Snapshot!.Items);
    }

    [Fact]
    public async Task CatalogCancellationRetainsCompletedSnapshotAndHoldsGatesUntilCliCleanupFinishes()
    {
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        var calls = 0;
        var cli = new CatalogCancellationAzure(async (command, token) =>
        {
            var call = Interlocked.Increment(ref calls);
            var arguments = command.CreateStartInfo(@"C:\synthetic\az.exe").ArgumentList;
            if (call % 2 == 1)
            {
                Assert.Equal(new[] { "account", "show" }, arguments.Take(2));
                return new(0, JsonSerializer.Serialize(new
                {
                    id = Tenant, tenantId = Tenant, state = "Enabled",
                    user = new { name = Mapping.AzureAccountUpn, type = "user" }
                }), "");
            }
            Assert.Equal(new[] { "devcenter", "dev", "dev-box", "list" }, arguments.Take(4));
            if (call == 4)
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cancelled.TrySetResult();
                    await cleanup.Task;
                }
            }
            var name = call switch
            {
                2 => "original-box", 4 => "cancelled-box", 6 => "replacement-box",
                _ => throw new InvalidOperationException("Unexpected synthetic catalog command.")
            };
            return new(0, JsonSerializer.Serialize(new[]
            {
                new
                {
                    name, projectName = Mapping.ProjectName, poolName = "synthetic-pool",
                    uri = $"{Mapping.DevCenterEndpoint}projects/{Mapping.ProjectName}/users/me/devboxes/{name}",
                    powerState = "Running", provisioningState = "Succeeded"
                }
            }), "");
        });
        await using var fixture = new Fixture(new()
        {
            AzureCli = cli,
            Diagnostic = (_, _, _) => Task.FromResult(new PrerequisiteDiagnosticResult(true, "Synthetic passed."))
        });
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        var first = await runtime.RefreshCatalogAsync(null, "synthetic-center", runtime.HostInstanceId, runtime.Settings.Revision);
        Assert.True(first.Succeeded);
        var completed = first.Snapshot!.State.Snapshot!;
        Assert.Equal("original-box", Assert.Single(completed.Items).DevBoxName);
        var pending = runtime.RefreshCatalogAsync(null, "synthetic-center", runtime.HostInstanceId, runtime.Settings.Revision);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.CancelCatalog();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.True(runtime.Catalog.State.IsBusy);
            AssertRetained();
            Assert.Equal(1003, (await runtime.RefreshCatalogAsync(null, "synthetic-center",
                runtime.HostInstanceId, runtime.Settings.Revision)).Error!.Code);
            Assert.Equal(1003, (await runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli)).Error!.Code);
            Assert.Equal(1003, (await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved with { Compact = false },
                runtime.HostInstanceId, runtime.Settings.Revision)).Error!.Code);
            Assert.Equal(4, Volatile.Read(ref calls));
            cleanup.TrySetResult();
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Succeeded);
            Assert.Equal(1006, result.Error!.Code);
            Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
            Assert.False(runtime.Catalog.State.IsBusy);
            AssertRetained();
            var retried = await runtime.RefreshCatalogAsync(null, "synthetic-center",
                runtime.HostInstanceId, runtime.Settings.Revision);
            Assert.True(retried.Succeeded);
            Assert.Equal("replacement-box", Assert.Single(retried.Snapshot!.State.Snapshot!.Items).DevBoxName);
            Assert.Equal(6, Volatile.Read(ref calls));
        }
        finally
        {
            cleanup.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }

        void AssertRetained()
        {
            var current = runtime.Catalog.State.Snapshot!;
            Assert.Equal(completed.Items.ToArray(), current.Items.ToArray());
            Assert.Equal(completed.DevCenterEndpoints.ToArray(), current.DevCenterEndpoints.ToArray());
            Assert.Equal(completed.AzureAccountUpn, current.AzureAccountUpn);
            Assert.Equal(completed.AzureTenantId, current.AzureTenantId);
            Assert.Equal(completed.RetrievedAtUtc, current.RetrievedAtUtc);
        }
    }

    private sealed class CatalogCancellationAzure(
        Func<AzureCliCommand, CancellationToken, Task<AzureCliResult>> run) : IAzureCliProcess
    {
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken token) =>
            run(command, token);
    }

    [Fact]
    public async Task SharingLifecycleUsesOwnedIdentityExplicitConfirmationsAndPreservesEstablishedServiceOnClientCancellation()
    {
        var runner = new FakeTunnelRunner();
        await using var fixture = new Fixture(new() { TunnelRunner = runner, TunnelHealthProbe = new FakeProbe() },
            """{"ConnectionMode":"DevTunnel","AutoStartSharing":false}""");
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        Assert.NotNull(runtime.Tunnel);
        using var client = new CancellationTokenSource();
        var started = await runtime.SharingAsync(RuntimeSharingOperation.Start, cancellationToken: client.Token);
        Assert.True(started.Succeeded, started.Error?.ToString());
        Assert.True(started.Snapshot!.State.CanCopy);
        client.Cancel();
        Assert.True(runtime.Sharing.State.CanCopy);
        Assert.False(runner.Host!.Disposed);
        Assert.Equal(1009, (await runtime.SharingAsync(RuntimeSharingOperation.Delete)).Error!.Code);
        Assert.Equal(1009, (await runtime.SharingAsync(RuntimeSharingOperation.Logout)).Error!.Code);
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Stop)).Succeeded);
        Assert.True(runner.Host.Disposed);
        Assert.False(runtime.Settings.State.Saved.AutoStartSharing);
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Delete, true)).Succeeded);
        Assert.Null(runtime.Tunnel!.Identity.TunnelId);
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Logout, true)).Succeeded);
        Assert.True(runner.LoggedOut);
    }

    [Fact]
    public async Task StopSharingStopsOwnedHostDespiteCatalogSettingsGateAndPreservesBusyPrivacyOptOut()
    {
        var runner = new FakeTunnelRunner();
        var entered = Signal();
        var attempts = 0;
        var catalogService = new CallbackCatalogService(async token =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return new([], Mapping.AzureAccountUpn, Mapping.AzureTenantId, DateTimeOffset.UtcNow);
        });
        await using var fixture = new Fixture(new()
        {
            TunnelRunner = runner, TunnelHealthProbe = new FakeProbe(), CatalogService = catalogService
        }, """{"ConnectionMode":"DevTunnel","AutoStartSharing":false,"ReceiveDetailedConversations":true}""");
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Start)).Succeeded);
        var catalog = runtime.RefreshCatalogAsync(null, null, runtime.HostInstanceId, runtime.Settings.Revision);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            runtime.DisableDetailedReception();
            var privacySave = await runtime.UpdateSettingsAsync(
                runtime.Settings.State.Saved with { ReceiveDetailedConversations = false },
                runtime.HostInstanceId, runtime.Settings.Revision);
            Assert.Equal(1003, privacySave.Error!.Code);
            var stopped = await runtime.SharingAsync(RuntimeSharingOperation.Stop);
            Assert.False(stopped.Succeeded);
            Assert.Equal(1003, stopped.Error!.Code);
            Assert.Equal(RuntimeCommitState.Committed, stopped.CommitState);
            Assert.True(runner.Host!.Disposed);
            Assert.Equal(TunnelState.Stopped, runtime.Sharing.State.State);
            Assert.False(catalog.IsCompleted);
            Assert.True(runtime.Settings.State.Saved.AutoStartSharing);
            Assert.False(runtime.Server!.Transcripts.GetCapabilities().Enabled);
        }
        finally { runtime.CancelCatalog(); }
        Assert.Equal(1006, (await catalog.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Code);
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Stop)).Succeeded);
        Assert.False(runtime.Settings.State.Saved.AutoStartSharing);
        Assert.True((await runtime.RefreshCatalogAsync(null, null, runtime.HostInstanceId, runtime.Settings.Revision)).Succeeded);
        Assert.True(runtime.Settings.State.Saved.ReceiveDetailedConversations);
        Assert.False(runtime.Settings.State.Effective.ReceiveDetailedConversations);
        Assert.False(runtime.Server!.Transcripts.GetCapabilities().Enabled);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SharingDeleteAndLogoutReturnTypedFailureOrCancellationAfterHostStop(bool logout, bool cancelled)
    {
        var inner = new FakeTunnelRunner();
        var runner = new RuntimeFaultTunnelRunner(inner);
        await using var fixture = new Fixture(new() { TunnelRunner = runner, TunnelHealthProbe = new FakeProbe() },
            """{"ConnectionMode":"DevTunnel","AutoStartSharing":false}""");
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Start)).Succeeded);
        var identity = runtime.Tunnel!.Identity;
        runner.BeforeCommand = (arguments, token) =>
        {
            if (logout ? arguments[0] != "user" || arguments[1] != "logout" : arguments[0] != "delete") return;
            if (!cancelled) throw new IOException("Synthetic CLI failure.");
            runtime.CancelSharing();
            throw new OperationCanceledException(token);
        };
        var result = await runtime.SharingAsync(logout ? RuntimeSharingOperation.Logout : RuntimeSharingOperation.Delete, true);
        Assert.False(result.Succeeded);
        Assert.Equal(cancelled ? 1006 : 1010, result.Error!.Code);
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.True(inner.Host!.Disposed);
        Assert.Equal(identity, runtime.Tunnel.Identity);
    }

    [Fact]
    public async Task SharingStartupCancellationReturnsCancelledRatherThanPrerequisiteFailure()
    {
        var entered = Signal();
        await using var fixture = new Fixture(new()
        {
            TunnelRunner = new FakeTunnelRunner(),
            TunnelHealthProbe = new StartupProgressProbe(async token =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            })
        }, """{"ConnectionMode":"DevTunnel","AutoStartSharing":false}""");
        await fixture.Runtime.InitializeAsync();
        var runtime = fixture.Runtime;
        var starting = runtime.SharingAsync(RuntimeSharingOperation.Start);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.CancelSharing();
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Succeeded);
        Assert.Equal(1006, result.Error!.Code);
        Assert.Equal(RuntimeCommitState.Committed, result.CommitState);
        Assert.False(runtime.Sharing.State.CanCopy);
    }

    private sealed class CallbackCatalogService(Func<CancellationToken, Task<DevBoxCatalogSnapshot>> refresh) : IDevBoxCatalogService
    {
        public Task<DevBoxCatalogSnapshot> RefreshAsync(CancellationToken token, Action<DevBoxCatalogProgress>? progress = null,
            Guid? subscriptionId = null, string? devCenterName = null) => refresh(token);
    }

    private sealed class RuntimeFaultTunnelRunner(FakeTunnelRunner inner) : ITunnelProcessRunner
    {
        public Action<IReadOnlyList<string>, CancellationToken>? BeforeCommand { get; set; }
        public Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            BeforeCommand?.Invoke(arguments, token);
            return inner.RunAsync(executable, arguments, timeout, token);
        }
        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken token) => inner.StartHostAsync(executable, arguments, outputLine, token);
    }

    [Fact]
    public async Task InitializationAndCliDeadlinesUseClockAndRetainControlState()
    {
        var clock = new ManualClock();
        var entered = Signal();
        await using var fixture = new Fixture(new()
        {
            TimeProvider = clock,
            CreateStoreAsync = async (_, _, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            },
            Diagnostic = async (_, _, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new(true, "");
            }
        });
        var initialization = fixture.Runtime.InitializeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(3));
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeLifecycle.Degraded, fixture.Runtime.Status.State.Lifecycle);
        Assert.Equal("startupTimeout", fixture.Runtime.Status.State.Stage);
        Assert.True(fixture.Runtime.Status.State.InitialAttemptCompleted);
        var diagnostic = fixture.Runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1007, (await diagnostic.WaitAsync(TimeSpan.FromSeconds(5))).Error!.Code);
        Assert.NotNull(fixture.Runtime.Settings.State.Saved);
    }

    [Fact]
    public async Task PollFailureRetainsLastGoodStateAndRecoveryInvalidatesWithoutChangingUnrelatedDomains()
    {
        var clock = new ManualClock();
        var fail = false;
        await using var fixture = new Fixture(new()
        {
            TimeProvider = clock,
            ReadMachinesAsync = (store, token) => fail ? throw new IOException("synthetic unavailable") : store.GetMachinesAsync(token)
        });
        await fixture.Runtime.InitializeAsync();
        var id = await fixture.AddMachineAsync();
        var runtime = fixture.Runtime;
        var settings = runtime.Settings;
        var good = runtime.GetMachines();
        var stale = Signal();
        var recovered = Signal();
        runtime.Changed += change =>
        {
            if (change.Domain != "machines" || change.MachineId is not null) return;
            if (runtime.GetMachines().IsStale) stale.TrySetResult();
            else recovered.TrySetResult();
        };
        fail = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        await stale.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runtime.GetMachines().IsStale);
        Assert.Equal(good.State.Items.Single().Machine.MachineId, runtime.GetMachines().State.Items.Single().Machine.MachineId);
        fail = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(runtime.GetMachines().IsStale);
        Assert.True(runtime.GetMachines().Revision > good.Revision);
        Assert.Equal(settings, runtime.Settings);
        Assert.Equal(id, runtime.GetMachine(id).State.Machine.MachineId);
    }

    [Fact]
    public async Task ShutdownWaitsForClientOwnedCleanupAndDoesNotReleaseEntrypointLease()
    {
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        await using var fixture = new Fixture(new()
        {
            Diagnostic = async (_, _, token) =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); await cleanup.Task; }
                return new(true, "");
            }
        });
        var running = fixture.Runtime.CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var shutdown = fixture.Runtime.ShutdownAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(shutdown.IsCompleted);
        Assert.True(fixture.Lease.IsHeld);
        cleanup.TrySetResult();
        Assert.True((await shutdown.WaitAsync(TimeSpan.FromSeconds(5))).Clean);
        Assert.Equal(1006, (await running).Error!.Code);
        Assert.True(fixture.Lease.IsHeld);
    }
    private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly WindowsAppConnection Mapping = new(new Uri("https://synthetic.region.devcenter.azure.com/"),
        "synthetic-project", "synthetic-box", "synthetic@example.invalid", Tenant, null, null);
    private sealed class FakeResolver : IDevBoxConnectionResolver
    {
        public Func<WindowsAppConnection, CancellationToken, Task<ResolvedDevBoxConnection>>? Resolve { get; init; }
        public Task<ResolvedDevBoxConnection> ResolveAsync(WindowsAppConnection mapping, CancellationToken token) =>
            Resolve?.Invoke(mapping, token) ?? Task.FromResult(new ResolvedDevBoxConnection(
                new Uri("ms-cloudpc:connect?cpcid=11111111-1111-1111-1111-111111111111&username=synthetic%40example.invalid&environment=prod&version=1&source=test"),
                mapping.AzureAccountUpn, mapping.AzureTenantId, Tenant, DateTimeOffset.UtcNow));
    }
    private sealed class FakeAzure : IAzureCliProcess
    {
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken token) =>
            Task.FromResult(new AzureCliResult(0, "", ""));
    }
    private sealed class FakeCatalog(IReadOnlyList<DevBoxCatalogItem> items) : IDevBoxCatalogService
    {
        public Task<DevBoxCatalogSnapshot> RefreshAsync(CancellationToken token, Action<DevBoxCatalogProgress>? progress = null,
            Guid? subscriptionId = null, string? devCenterName = null)
        {
            progress?.Invoke(new(DevBoxCatalogStage.DevBoxes) { DevBoxCount = items.Count });
            return Task.FromResult(new DevBoxCatalogSnapshot(items, Mapping.AzureAccountUpn, Mapping.AzureTenantId, DateTimeOffset.UtcNow)
            {
                DevCenterEndpoints = [Mapping.DevCenterEndpoint]
            });
        }
    }
    private sealed class FakeProbe : ITunnelHealthProbe
    {
        public Task VerifyAsync(Uri baseUri, CancellationToken token) => Task.CompletedTask;
    }
    private sealed class FakeTunnelHost : ITunnelHostProcess
    {
        private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Completion => completion.Task;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; completion.TrySetResult(0); return ValueTask.CompletedTask; }
    }
    private sealed class FakeTunnelRunner : ITunnelProcessRunner
    {
        private const string Account = """{"status":"Logged in","provider":"microsoft","username":"synthetic","tenantId":"11111111-1111-1111-1111-111111111111","objectId":"22222222-2222-2222-2222-222222222222"}""";
        private const string Acl = """[{"type":"Anonymous","subjects":[],"scopes":["connect"]}]""";
        private string? id, description;
        private int port;
        private bool hasPort, hasAcl, absent;
        public bool LoggedOut { get; private set; }
        public FakeTunnelHost? Host { get; private set; }
        public Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var args = arguments.ToArray();
            string json;
            switch (args[0])
            {
                case "--version": json = "Tunnel CLI version: 1.0.2030+fc9273aa0f"; break;
                case "user" when args[1] == "show": json = Account; break;
                case "user": LoggedOut = true; json = ""; break;
                case "create":
                    id = args[1] + ".usw2";
                    description = args[Array.IndexOf(args, "--description") + 1];
                    absent = hasPort = hasAcl = false;
                    json = TunnelJson();
                    break;
                case "show":
                    if (absent)
                    {
                        var requested = args[1];
                        var separator = requested.LastIndexOf('.');
                        var text = separator < 0 ? $"Tunnel not found: {requested}" :
                            $"Tunnel not found in {requested[(separator + 1)..]}: {requested[..separator]}";
                        return Task.FromResult(new CliCommandResult(2, text, ""));
                    }
                    json = TunnelJson();
                    break;
                case "port":
                    port = int.Parse(args[Array.IndexOf(args, args[1] == "create" ? "-p" : "--port-number") + 1],
                        System.Globalization.CultureInfo.InvariantCulture);
                    if (args[1] == "create") hasPort = true;
                    json = "{\"port\":" + PortJson() + "}";
                    break;
                case "access":
                    if (args[1] == "create") hasAcl = true;
                    json = "{\"accessControlEntries\":" + (args.Contains("--port-number") && hasAcl ? Acl : "[]") + "}";
                    break;
                case "delete": absent = true; json = JsonSerializer.Serialize(new { deletedTunnel = id }); break;
                default: throw new InvalidOperationException("Unexpected synthetic command.");
            }
            return Task.FromResult(new CliCommandResult(0, json, ""));
        }
        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken token)
        {
            Host = new();
            outputLine($"Hosting port: {port}");
            outputLine($"Connect via browser: https://sample-{port}.usw2.devtunnels.ms");
            outputLine($"Ready to accept connections for tunnel: {id}");
            return Task.FromResult<ITunnelHostProcess>(Host);
        }
        private string TunnelJson()
        {
            var value = new Dictionary<string, object?>
            {
                ["tunnelId"] = id, ["description"] = description, ["hostConnections"] = 0,
                ["accessControl"] = Array.Empty<object>()
            };
            if (hasPort) value["ports"] = new[] { JsonSerializer.Deserialize<JsonElement>(PortJson()) };
            return JsonSerializer.Serialize(new { tunnel = value });
        }
        private string PortJson() => JsonSerializer.Serialize(new
        {
            tunnelId = id, portNumber = port, protocol = "http",
            accessControl = JsonSerializer.Deserialize<JsonElement>(hasAcl ? Acl : "[]")
        });
    }
    private sealed class FakePlatform : IWindowsAppPlatform
    {
        public Action? BeforeActivate { get; set; }
        public bool IsProtocolAvailable() => true;
        public WindowsAppActivationDisposition TryActivateExisting(string name) => WindowsAppActivationDisposition.NoExistingWindow;
        public WindowsAppActivationDisposition Activate(Uri uri, string name)
        {
            BeforeActivate?.Invoke();
            return WindowsAppActivationDisposition.ConnectionUriActivated;
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.GetFullPath(Path.Combine("test-artifacts", $"core-{Guid.NewGuid():N}"));
        public DashboardResourceLease Lease { get; }
        public DashboardRuntime Runtime { get; }
        private readonly TimeProvider clock;
        public Fixture(DashboardRuntimeOptions? options = null, string? json = null)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (json is null) (new DashboardSettings { ConnectionMode = DashboardConnectionMode.Lan, AutoStartSharing = false }).Save(Directory);
            else File.WriteAllText(Path.Combine(Directory, "dashboard-settings.json"), json);
            Lease = DashboardResourceLease.Acquire(Directory, Directory);
            clock = options?.TimeProvider ?? TimeProvider.System;
            Runtime = new(Lease, (options ?? new()) with { ReceiverPortOverride = 0 });
        }
        public async Task<Guid> AddMachineAsync(AgentEvent kind = AgentEvent.UserPromptSubmitted, Guid? machineId = null)
        {
            var id = machineId ?? Guid.NewGuid();
            await Runtime.Store!.AcceptAsync(new StatusRequest
            {
                MachineId = id, MachineName = "synthetic", Client = "copilot-cli", ClientVersion = "1.0",
                EventId = Guid.NewGuid(), SessionId = "session", Event = kind, ReportedAtUtc = clock.GetUtcNow()
            });
            await Runtime.RefreshMachinesAsync();
            return id;
        }
        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            Lease.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 16, 0, 0, 0, TimeSpan.Zero);
        private readonly List<ClockTimer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(this, callback, state);
            lock (timers) timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            now += duration;
            ClockTimer[] copy;
            lock (timers) copy = timers.ToArray();
            foreach (var timer in copy) timer.Fire();
        }
        private sealed class ClockTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            private TimeSpan period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                this.period = period;
                return true;
            }
            public void Fire()
            {
                if (due is not { } scheduled || scheduled > clock.now) return;
                due = period == Timeout.InfiniteTimeSpan ? null : clock.now + period;
                callback(state);
            }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
