using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed partial class MultiTargetIntegrationTests
{
    private MultiTargetIntegrationManager OfflineManager() =>
        Manager((_, _) => throw new InvalidOperationException("Save must not verify a receiver or source capability."));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsFirstSetupIsOfflineAndNeverStartsClient(bool startAtSignIn)
    {
        var config = Config.ToVersion5().WithDnsTxtDiscovery("offline.example.test") with { DetailedReportingEnabled = false };
        var plan = MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, startAtSignIn);
        Assert.True(plan.IsSettingsOnly);
        Assert.False(plan.IsRemoval);
        Assert.Contains("No hook repair or legacy task removal", plan.Preview);
        var result = await OfflineManager().ApplyAsync(plan, default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.Equal(startAtSignIn, startup.Value is not null);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Reloads);
        Assert.Equal(0, runtime.Stops);
        var saved = RemoteConfiguration.Load(ConfigPath);
        Assert.True(saved.IsDiscovery);
        Assert.False(saved.DetailedReportingEnabled);
        Assert.Empty(saved.Integrations);
        Assert.Empty(Manifest().Artifacts);
        Assert.Equal(MultiTargetIntegrationManager.Hash(File.ReadAllBytes(ConfigPath)), Manifest().ConfigHash);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SettingsPreserveInstalledArtifactsTargetsRelayAndLegacyTask(bool running, bool enabled)
    {
        var target = VerifiedIde("vscode", "code");
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            Config.ToVersion5() with { DetailedReportingEnabled = false }, ConfigPath, [target], RelayPath,
            _ => false), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        var prior = Manifest();
        var artifacts = MultiTargetIntegrationManager.Serialize(prior.Artifacts);
        var hookPath = Path.Combine(target.HookDirectory, "agent-signaler.json");
        var hook = File.ReadAllBytes(hookPath);
        // A foreign profile preference added after installation is not repaired or incorporated into backups.
        var settings = File.ReadAllText(target.SettingsPath!);
        File.WriteAllText(target.SettingsPath!, settings.Replace("{", "{ /* keep exact profile bytes */", StringComparison.Ordinal));
        var profile = File.ReadAllBytes(target.SettingsPath!);
        var taskName = ScheduledTaskDefinition.Name(machineId);
        var task = $"<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>" +
            $"{ScheduledTaskDefinition.Owner(machineId)}</Description></RegistrationInfo><Actions><Exec><Command>" +
            $"{System.Security.SecurityElement.Escape(RelayPath)}</Command><Arguments>heartbeat --config " +
            $"{System.Security.SecurityElement.Escape(ScheduledTaskDefinition.QuoteArgument(ConfigPath))}</Arguments></Exec></Actions></Task>";
        scheduler.Write(taskName, task);
        runtime.Running = running;
        var starts = runtime.Starts;
        var reloads = runtime.Reloads;
        var updated = saved.WithDnsTxtDiscovery("unavailable.example.test") with { HeartbeatIntervalSeconds = 3600 };
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(updated, ConfigPath, RelayPath, enabled), default);
        var actual = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(hook, File.ReadAllBytes(hookPath));
        Assert.Equal(profile, File.ReadAllBytes(target.SettingsPath!));
        Assert.Equal(artifacts, MultiTargetIntegrationManager.Serialize(Manifest().Artifacts));
        Assert.Equal(MultiTargetIntegrationManager.Serialize(prior with { ConfigHash = Manifest().ConfigHash }),
            MultiTargetIntegrationManager.Serialize(Manifest()));
        Assert.Equal(MultiTargetIntegrationManager.Serialize(saved.Integrations), MultiTargetIntegrationManager.Serialize(actual.Integrations));
        Assert.Equal(saved.RelayPath, actual.RelayPath);
        Assert.Equal(prior.Version, Manifest().Version);
        Assert.Equal(task, scheduler.ReadXml(taskName));
        Assert.False(actual.DetailedReportingEnabled);
        Assert.Equal(3600, actual.HeartbeatIntervalSeconds);
        Assert.Equal(enabled, startup.Value is not null);
        Assert.Equal(starts, runtime.Starts);
        Assert.Equal(reloads + (running ? 1 : 0), runtime.Reloads);
        Assert.Equal(0, runtime.Stops);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task SettingsRetainReadableDirectSchemaWithoutImplicitDowngrade(int version)
    {
        var config = version == 1 ? new RemoteConfiguration { Host = "localhost", MachineId = machineId } :
            Config.ToVersion6() with { Version = version };
        AtomicFile.Write(ConfigPath, MultiTargetIntegrationManager.Serialize(config));
        var plan = MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false);
        await OfflineManager().ApplyAsync(plan, default);
        Assert.Equal(version, RemoteConfiguration.Load(ConfigPath).Version);
        Assert.Equal(0, runtime.Starts);
        Assert.Empty(Manifest().Artifacts);
    }

    [Fact]
    public async Task SettingsRetainLegacyManifestAndHookWithoutMigration()
    {
        var legacyPlan = IntegrationManager.Preview(Config, ConfigPath, Path.Combine(root, "legacy"), RelayPath);
        await Legacy().ApplyAsync(legacyPlan, default);
        var prior = Manifest();
        Assert.Equal(1, prior.Version);
        var hook = File.ReadAllBytes(prior.HookPath);
        runtime.Running = false;
        var saved = RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 };
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false), default);
        var after = Manifest();
        Assert.Equal(MultiTargetIntegrationManager.Serialize(prior with { ConfigHash = after.ConfigHash }),
            MultiTargetIntegrationManager.Serialize(after));
        Assert.Equal(hook, File.ReadAllBytes(prior.HookPath));
    }

    [Fact]
    public async Task SettingsRejectDirtyConfigHooksConflictingSelectionsAndDowngrades()
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config.ToVersion5(), ConfigPath, [Cli()], RelayPath), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        MultiTargetIntegrationPlan Preview(RemoteConfiguration config, string? relay = null) =>
            MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, relay ?? RelayPath, false);
        Assert.Throws<InvalidDataException>(() => Preview(saved with { Integrations = [] }));
        Assert.Throws<InvalidDataException>(() => Preview(saved with { Integrations = [Cli("pending")] }));
        Assert.Throws<InvalidDataException>(() => Preview(saved, Path.Combine(root, "pending", "AgentSignaler.Relay.exe")));
        Assert.Throws<InvalidDataException>(() => Preview(saved with { RelayPath = Path.Combine(root, "other", "AgentSignaler.Relay.exe") }));
        Assert.Throws<InvalidDataException>(() => Preview(saved with { Version = 4 }));
        Assert.Throws<InvalidDataException>(() => Preview(saved with { MachineId = Guid.NewGuid() }));
        var original = File.ReadAllBytes(ConfigPath);
        File.AppendAllText(ConfigPath, " ");
        Assert.Throws<InvalidDataException>(() => Preview(saved));
        File.WriteAllBytes(ConfigPath, original);
        var hookPath = Path.Combine(saved.Integrations[0].HookDirectory, "agent-signaler.json");
        File.AppendAllText(hookPath, " ");
        Assert.Throws<InvalidDataException>(() => Preview(saved));
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("manifest")]
    [InlineData("hook")]
    public async Task SettingsRejectConcurrentFileEditsWithoutOverwriting(string edited)
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config.ToVersion5(), ConfigPath, [Cli()], RelayPath), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        var plan = MultiTargetIntegrationManager.PreviewSettings(saved with { HeartbeatIntervalSeconds = 600 }, ConfigPath, RelayPath, false);
        var path = edited == "config" ? ConfigPath : edited == "manifest" ? ManifestPath :
            Path.Combine(saved.Integrations[0].HookDirectory, "agent-signaler.json");
        File.AppendAllText(path, " ");
        var changed = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => OfflineManager().ApplyAsync(plan, default));
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.NotNull(startup.Value);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task SettingsRejectConcurrentStartupChangeAndMutablePlan()
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config.ToVersion5(), ConfigPath, [Cli()], RelayPath), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        var before = File.ReadAllBytes(ConfigPath);
        var plan = MultiTargetIntegrationManager.PreviewSettings(saved with { HeartbeatIntervalSeconds = 600 }, ConfigPath, RelayPath, false);
        runtime.OnQuery = () => startup.Value = "foreign";
        await Assert.ThrowsAsync<InvalidDataException>(() => OfflineManager().ApplyAsync(plan, default));
        Assert.Equal("foreign", startup.Value);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        runtime.OnQuery = null;
        var targets = (IntegrationTarget[])plan.Config.Integrations;
        targets[0] = targets[0] with { Id = "changed" };
        await Assert.ThrowsAsync<InvalidDataException>(() => OfflineManager().ApplyAsync(plan, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsStartupFailureOrCancellationRollsBackWithoutResumingCapture(bool cancel)
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config.ToVersion5(), ConfigPath, [Cli()], RelayPath), default);
        var before = File.ReadAllBytes(ConfigPath);
        var manifest = File.ReadAllBytes(ManifestPath);
        var command = startup.Value;
        using var cancellation = new CancellationTokenSource();
        startup.Fail = !cancel;
        startup.OnReplace = cancel ? cancellation.Cancel : null;
        var saved = RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 };
        var plan = MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OfflineManager().ApplyAsync(plan, cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => OfflineManager().ApplyAsync(plan, default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath));
        Assert.Equal(command, startup.Value);
        Assert.Equal(0, runtime.Reloads);
        Assert.Equal(0, runtime.TranscriptReloads);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsFailedOptOutRetainsFalseAndJournalAndRecoveryCannotReenable(bool cancel)
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config.ToVersion5(), ConfigPath, [Cli()], RelayPath), default);
        var hookPath = Path.Combine(Cli().HookDirectory, "agent-signaler.json");
        var hook = File.ReadAllBytes(hookPath);
        var saved = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = false };
        using var cancellation = new CancellationTokenSource();
        startup.Fail = !cancel;
        startup.OnReplace = cancel ? cancellation.Cancel : null;
        var reloads = runtime.TranscriptReloads;
        var suspends = runtime.Suspends;
        await Assert.ThrowsAsync<AggregateException>(() => OfflineManager().ApplyAsync(
            MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false), cancellation.Token));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.Equal(suspends + 1, runtime.Suspends);
        Assert.Equal(reloads, runtime.TranscriptReloads);
        Assert.Equal(hook, File.ReadAllBytes(hookPath));
        Assert.True(File.Exists(Path.Combine(root, "integration-recovery.json")));
        startup.Fail = false;
        Assert.Throws<AggregateException>(() => OfflineManager().RecoverPending(ConfigPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.Equal(reloads, runtime.TranscriptReloads);
        Assert.Throws<InvalidOperationException>(() => MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false));
    }

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(3600, true)]
    [InlineData(3601, false)]
    public void SettingsRespectExactHeartbeatBounds(int seconds, bool valid)
    {
        var config = Config.ToVersion5() with { HeartbeatIntervalSeconds = seconds };
        if (valid)
            Assert.Equal(seconds, MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false).Config.HeartbeatIntervalSeconds);
        else
            Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false));
    }

    [Fact]
    public void SettingsRejectOrphanedOrUnownedTargetsAndUnsupportedStoredSchema()
    {
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewSettings(
            Config.ToVersion4() with { Integrations = [Cli()] }, ConfigPath, RelayPath, false));
        AtomicFile.Write(ConfigPath, Encoding.UTF8.GetBytes("{\"version\":99}"));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewSettings(Config, ConfigPath, RelayPath, false));
    }

    [Theory]
    [InlineData(262144, true)]
    [InlineData(262145, false)]
    public void SettingsRespectExactSavedConfigurationByteLimit(int length, bool valid)
    {
        var config = Config.ToVersion5();
        var json = MultiTargetIntegrationManager.Serialize(config);
        var bytes = Enumerable.Repeat((byte)' ', length).ToArray();
        json.CopyTo(bytes, 0);
        AtomicFile.Write(ConfigPath, bytes);
        if (valid)
            Assert.True(MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false).IsSettingsOnly);
        else
            Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false));
        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
    }

    [Theory]
    [InlineData(4194304, true)]
    [InlineData(4194305, false)]
    public async Task SettingsRespectExactSavedManifestByteLimit(int length, bool valid)
    {
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(Config, ConfigPath, RelayPath, false), default);
        var json = File.ReadAllBytes(ManifestPath);
        var bytes = Enumerable.Repeat((byte)' ', length).ToArray();
        json.CopyTo(bytes, 0);
        AtomicFile.Write(ManifestPath, bytes);
        var saved = RemoteConfiguration.Load(ConfigPath);
        if (valid)
            Assert.True(MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false).IsSettingsOnly);
        else
            Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false));
        Assert.Equal(bytes, File.ReadAllBytes(ManifestPath));
    }

    [Fact]
    public async Task SettingsUseTheSharedTransactionLeaseAndDoNotLaunchAfterClientExit()
    {
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(Config, ConfigPath, RelayPath, true), default);
        var saved = RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 };
        var plan = MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false);
        var before = File.ReadAllBytes(ConfigPath);
        using (AtomicFile.Acquire(ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1)))
            await Assert.ThrowsAsync<IOException>(() => OfflineManager().ApplyAsync(plan, default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        runtime.Running = true;
        startup.OnReplace = () => runtime.Running = false;
        var result = await OfflineManager().ApplyAsync(plan, default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.Equal(0, runtime.Starts);
        Assert.False(runtime.Running);
    }

    [Fact]
    public async Task SettingsRetainConcurrentEditsAndRecoverThroughTheExistingJournal()
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config.ToVersion5(), ConfigPath, [Cli()], RelayPath), default);
        var before = File.ReadAllBytes(ConfigPath);
        var manifest = File.ReadAllBytes(ManifestPath);
        var command = startup.Value;
        var saved = RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 };
        var plan = MultiTargetIntegrationManager.PreviewSettings(saved, ConfigPath, RelayPath, false);
        startup.OnReplace = () =>
        {
            startup.OnReplace = null;
            File.AppendAllText(ConfigPath, " ");
        };
        await Assert.ThrowsAsync<AggregateException>(() => OfflineManager().ApplyAsync(plan, default));
        var journal = Path.Combine(root, "integration-recovery.json");
        Assert.True(File.Exists(journal));
        Assert.Equal(plan.ConfigBytes.Concat(new byte[] { (byte)' ' }), File.ReadAllBytes(ConfigPath));
        Assert.Equal(command, startup.Value);
        Assert.Equal(0, runtime.Reloads);
        // Simulate the user resolving the conflict to the exact transaction revision.
        File.WriteAllBytes(ConfigPath, plan.ConfigBytes);
        OfflineManager().RecoverPending(ConfigPath);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath));
        Assert.False(File.Exists(journal));
        Assert.Equal(0, runtime.TranscriptReloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsModernClientReconcilesCommittedOptOutAndLaterExplicitOptIn(bool discovery)
    {
        var config = Config.ToVersion6().WithDashboardUrl("https://localhost:51820/") with { DetailedReportingEnabled = false };
        if (discovery) config = config.WithDnsTxtDiscovery("synthetic.example.test");
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false), default);
        await using var client = new ClientCoordinator(ConfigPath,
            endpointTransportFactory: (_, _) => new ClientRuntimeTests.RecordingTransport(),
            discoveryResolverFactory: _ => new SettingsResolver(),
            transcriptTransport: new UnavailableTranscriptTransport());
        client.Start();
        await ClientRuntimeTests.Eventually(() => client.Status().State == "connected");
        var platform = new SettingsClientPlatform(client);
        var manager = new MultiTargetIntegrationManager(scheduler,
            (_, _) => throw new InvalidOperationException("No connection verification during Save."),
            startup, new ClientIntegrationRuntime(platform));
        config = RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 };
        var result = await manager.ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, false), default);
        Assert.True(result.SettingsCommitted);
        Assert.DoesNotContain("preference-not-confirmed", client.TranscriptAvailability);
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        config = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = true };
        result = await manager.ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(config, ConfigPath, RelayPath, true), default);
        Assert.True(result.SettingsCommitted);
        Assert.DoesNotContain("preference-not-confirmed", client.TranscriptAvailability);
        Assert.Equal(0, platform.Launches);
        Assert.DoesNotContain(platform.Requests, r => r.Command == "discovery-status");
        await client.StopAsync();
    }

    [Fact]
    public async Task SettingsSwitchConnectionModesOfflineWithoutDowngradingOrLosingOptOut()
    {
        var direct = Config.ToVersion6() with { DetailedReportingEnabled = false };
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(direct, ConfigPath, RelayPath, false), default);
        var discovery = RemoteConfiguration.Load(ConfigPath).WithDnsTxtDiscovery("offline.example.test");
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(discovery, ConfigPath, RelayPath, false), default);
        var converted = RemoteConfiguration.Load(ConfigPath).WithDashboardUrl("https://localhost:51821/");
        await OfflineManager().ApplyAsync(MultiTargetIntegrationManager.PreviewSettings(converted, ConfigPath, RelayPath, false), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(7, saved.Version);
        Assert.False(saved.IsDiscovery);
        Assert.Null(saved.DnsRecordName);
        Assert.False(saved.DetailedReportingEnabled);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Reloads);
    }

    private sealed class SettingsClientPlatform(ClientCoordinator client) : IClientRuntimePlatform
    {
        public int Launches;
        public List<ClientIpcRequest> Requests { get; } = [];
        public bool IsOwnerRunning(string configPath) => true;
        public Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request, CancellationToken token)
        {
            Requests.Add(request);
            return client.HandleAsync(request, token);
        }
        public void Launch(string clientPath, string configPath) => Launches++;
    }

    private sealed class SettingsResolver : IDiscoveryEndpointResolver
    {
        public Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryResolution(
                new(new Uri("https://localhost:51820/"), 1, DateTimeOffset.UtcNow.AddMinutes(10)),
                "resolved", DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default) =>
            ResolveAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableTranscriptTransport : ITranscriptTransport
    {
        public Task<TranscriptTransportResult> SendAsync(Uri baseUri, TranscriptOperation operation,
            ReadOnlyMemory<byte> body, CancellationToken token) =>
            Task.FromResult(new TranscriptTransportResult(503, ReadOnlyMemory<byte>.Empty));
        public void Dispose() { }
    }
}
