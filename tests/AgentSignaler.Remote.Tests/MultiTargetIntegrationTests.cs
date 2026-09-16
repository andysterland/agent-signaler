using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class MultiTargetIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string RelayPath => Path.Combine(root, "AgentSignaler.Relay.exe");
    private string ManifestPath => Path.Combine(root, "integration.json");
    private readonly FakeScheduler scheduler = new();
    private readonly FakeStartup startup = new();
    private readonly FakeRuntime runtime = new();
    private readonly Guid machineId = Guid.NewGuid();
    private RemoteConfiguration Config => new RemoteConfiguration { Host = "localhost", MachineId = machineId }.ToVersion3();

    public MultiTargetIntegrationTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(RelayPath, []);
        File.WriteAllBytes(Path.Combine(root, "AgentSignaler.Client.exe"), []);
    }

    private IntegrationTarget Cli(string id = "cli", string scope = "cli-scope", string? directory = null) => new()
    {
        Id = id, Kind = "copilot-cli", DisplayName = id, InstallationId = id,
        HostVersion = "1.0.83", ScopeId = scope, HookDirectory = directory ?? Path.Combine(root, id, "hooks"),
        Capability = IntegrationCapability.Verified, SupportedEvents = HookAdapters.Events("copilot-cli")
    };

    private MultiTargetIntegrationManager Manager(Func<RemoteConfiguration, CancellationToken, Task<bool>>? verify = null) =>
        new(scheduler, verify ?? ((_, _) => Task.FromResult(true)), startup, runtime);
    private IntegrationManager Legacy() => new(scheduler, (_, _) => Task.FromResult(true), startup, runtime);
    private IntegrationManifest Manifest() => JsonSerializer.Deserialize<IntegrationManifest>(File.ReadAllBytes(ManifestPath), Protocol.Json)!;

    private IntegrationTarget VerifiedIde(string kind, string id, string? hookDirectory = null, string? hostVersion = null)
    {
        if (!File.Exists(ConfigPath))
            AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Config with { RelayPath = RelayPath }, Protocol.Json));
        var executable = Path.Combine(root, id + "-synthetic-ide.exe");
        File.WriteAllBytes(executable, []);
        var target = Cli(id) with
        {
            Kind = kind, Capability = IntegrationCapability.VerificationRequired,
            ExecutablePath = executable,
            HookDirectory = hookDirectory ?? Path.Combine(root, id, "hooks"),
            HostVersion = hostVersion ?? "1.0.83",
            SupportedEvents = [], SettingsPath = kind == "vscode" ? Path.Combine(root, id, "settings.json") : null
        };
        var probe = HookVerification.Preview(target, ConfigPath, RelayPath);
        HookVerification.Begin(probe, true);
        var events = new[] { AgentEvent.SessionStart, AgentEvent.UserPromptSubmitted, AgentEvent.PreToolUse,
            AgentEvent.PostToolUse, kind == "vscode" ? AgentEvent.ExecutionStopped : AgentEvent.AgentStop };
        foreach (var e in events)
            Assert.True(HookVerification.AcceptProbe(ConfigPath, probe.ProbeId, e,
                new("synthetic-session", DateTimeOffset.UtcNow, false, Source: new(kind, target.ScopeId, target.HostVersion))));
        return HookVerification.Complete(probe, new(true, true, true, true, "synthetic test workflow", "new session"));
    }

    [Fact]
    public void Version4RoundTripsAndLegacyUrlEditsPreserveCollection()
    {
        var config = Config.ToVersion4() with { Integrations = [Cli()], HeartbeatIntervalSeconds = 600 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json);
        var loaded = JsonSerializer.Deserialize<RemoteConfiguration>(bytes, Protocol.Json)!;
        Assert.Equal(4, loaded.Version);
        Assert.Equal("cli", Assert.Single(loaded.Integrations).Id);
        Assert.Equal(600, loaded.HeartbeatIntervalSeconds);
        Assert.Equal(4, loaded.WithDashboardUrl("https://example.test").Version);
        Assert.Throws<InvalidDataException>(() => (loaded with { Version = 3 }).Validate());
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(
            Encoding.UTF8.GetString(bytes).Replace("\"version\":4", "\"version\":3"), Protocol.Json));
    }

    [Fact]
    public async Task ApplyAllowsDashboardVerificationBeyondTheOldThreeSecondBudget()
    {
        var target = Cli();
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath);
        var manager = Manager(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(3200), token);
            Assert.False(File.Exists(ConfigPath));
            Assert.False(File.Exists(Path.Combine(target.HookDirectory, "agent-signaler.json")));
            return true;
        });

        Assert.True((await manager.ApplyAsync(plan, default)).SettingsCommitted);
        Assert.True(File.Exists(Path.Combine(target.HookDirectory, "agent-signaler.json")));
    }

    [Fact]
    public async Task DashboardTimeoutIdentifiesTheStageAndPreservesExistingIntegration()
    {
        var target = Cli();
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath), default);
        var originals = new[] { ConfigPath, ManifestPath, Path.Combine(target.HookDirectory, "agent-signaler.json") }
            .ToDictionary(path => path, File.ReadAllBytes);
        var oldStartup = startup.Value;
        var updated = RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 };
        var plan = MultiTargetIntegrationManager.Preview(updated, ConfigPath, [target], RelayPath);
        var timeout = new OperationCanceledException("Synthetic HTTP timeout.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager((_, _) => Task.FromException<bool>(timeout)).ApplyAsync(plan, default));

        Assert.Same(timeout, error.InnerException);
        Assert.Contains("Dashboard capability check timed out", error.Message);
        Assert.Contains(updated.BaseUri.AbsoluteUri, error.Message);
        Assert.Contains("No settings or hooks were changed", error.Message);
        Assert.Contains("Test connection", error.Message);
        foreach (var original in originals) Assert.Equal(original.Value, File.ReadAllBytes(original.Key));
        Assert.Equal(oldStartup, startup.Value);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task DashboardVerificationRemainsBoundedEvenWhenTheVerifierIgnoresCancellation()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [], RelayPath);
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Manager((_, _) => pending.Task).ApplyAsync(plan, default)
                    .WaitAsync(DashboardConnection.TestTimeout + TimeSpan.FromSeconds(5)));
            Assert.Contains("Dashboard capability check timed out", error.Message);
            Assert.False(File.Exists(ConfigPath));
            Assert.False(File.Exists(ManifestPath));
            Assert.Null(startup.Value);
            Assert.Equal(0, runtime.Starts);
        }
        finally { pending.TrySetResult(false); }
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsADashboardTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [], RelayPath);
        var manager = Manager((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<bool>(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ApplyAsync(plan, cancellation.Token));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(ManifestPath));
        Assert.Null(startup.Value);
    }

    [Fact]
    public async Task LocalClientTimeoutIsNotReportedAsADashboardFailure()
    {
        runtime.QueryFailure = new OperationCanceledException("Synthetic IPC timeout.");
        var verified = false;
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [], RelayPath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager((_, _) => { verified = true; return Task.FromResult(true); }).ApplyAsync(plan, default));

        Assert.Contains("local Client did not respond", error.Message);
        Assert.Same(runtime.QueryFailure, error.InnerException);
        Assert.False(verified);
        Assert.False(File.Exists(ConfigPath));
        Assert.Null(startup.Value);
    }

    [Fact]
    public async Task EmptySelectionBootstrapsReporterConfigurationWithoutStartingClient()
    {
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [], RelayPath);
        var result = await Manager().ApplyAsync(plan, default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.Equal(4, RemoteConfiguration.Load(ConfigPath).Version);
        Assert.Empty(RemoteConfiguration.Load(ConfigPath).Integrations);
        Assert.Empty(Manifest().Artifacts);
        Assert.NotNull(startup.Value);
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Stops);
    }

    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("visual-studio")]
    [InlineData("vscode")]
    public async Task ChangingRelayLocationUpdatesSavedConfigurationHooksAndStartup(string kind)
    {
        var target = AutomaticHookConfiguration.Prepare(
            AutomaticHookConfiguration.Custom(kind, Path.Combine(root, "custom", kind)), ConfigPath);
        await Manager().ApplyAsync(
            MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath, _ => false), default);
        var before = File.ReadAllBytes(ConfigPath);
        var selectedRelay = Path.Combine(root, "remote installation", "AgentSignaler.Relay.exe");
        var selectedClient = Path.Combine(Path.GetDirectoryName(selectedRelay)!, "AgentSignaler.Client.exe");
        AtomicFile.Write(selectedRelay, []);
        AtomicFile.Write(selectedClient, []);
        var updated = RemoteConfiguration.Load(ConfigPath) with { RelayPath = selectedRelay };
        var plan = MultiTargetIntegrationManager.Preview(updated, ConfigPath, [target], selectedRelay, _ => false);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));

        Assert.True((await Manager().ApplyAsync(plan, default)).SettingsCommitted);

        Assert.Equal(selectedRelay, RemoteConfiguration.Load(ConfigPath).RelayPath);
        Assert.Equal(selectedRelay, Manifest().RelayPath);
        Assert.Equal(IntegrationStartup.Command(selectedClient, ConfigPath), startup.Value);
        Assert.Equal(HookAdapters.Generate(target, selectedRelay, ConfigPath),
            File.ReadAllBytes(Path.Combine(target.HookDirectory, "agent-signaler.json")));
    }

    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("visual-studio")]
    [InlineData("vscode")]
    public async Task AutomaticSelectionInstallsRepairsAndRemovesWithoutManualVerification(string kind)
    {
        var path = Path.Combine(root, "custom", kind);
        var target = AutomaticHookConfiguration.Prepare(AutomaticHookConfiguration.Custom(kind, path), ConfigPath);
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath, _ => false);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(Directory.Exists(path));
        Assert.True((await Manager().ApplyAsync(plan, default)).SettingsCommitted);
        var saved = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(IntegrationCapability.Configured, Assert.Single(saved.Integrations).Capability);
        Assert.True(saved.Integrations[0].IsCustom);
        Assert.True(File.Exists(Path.Combine(target.HookDirectory, "agent-signaler.json")));
        if (kind == "vscode")
            Assert.True(JsoncHookSettings.HasOwnedValue(File.ReadAllBytes(target.SettingsPath!), target.HookDirectory));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
        Assert.Throws<InvalidOperationException>(() => HookVerification.RequireVerified(target, ConfigPath));
        AutomaticHookConfiguration.RequireInstallable(target, ConfigPath);

        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(saved, ConfigPath, [target], RelayPath, _ => false), default);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(saved, ConfigPath, [], RelayPath, _ => false), default);
        Assert.Empty(RemoteConfiguration.Load(ConfigPath).Integrations);
        Assert.False(File.Exists(Path.Combine(target.HookDirectory, "agent-signaler.json")));
        if (kind == "vscode") Assert.False(File.Exists(target.SettingsPath));
    }

    [Fact]
    public void AutomaticSelectionDoesNotOverwriteAnUnownedHook()
    {
        var target = AutomaticHookConfiguration.Prepare(
            AutomaticHookConfiguration.Custom("visual-studio", Path.Combine(root, "hooks")), ConfigPath);
        var path = Path.Combine(target.HookDirectory, "agent-signaler.json");
        AtomicFile.Write(path, "{}"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath));
        Assert.Equal("{}", File.ReadAllText(path));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Version4ConfigurationRevisionSupportsMultipleTargetMetadataBeyondLegacyLimit()
    {
        var config = Config.ToVersion4() with
        {
            Integrations = Enumerable.Range(0, 12).Select(index =>
                Cli("cli-" + index, "scope-" + index) with { Reason = new string('x', 512) }).ToArray()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json);
        Assert.True(bytes.Length > 8192);
        AtomicFile.Write(ConfigPath, bytes);
        Assert.Equal(12, RemoteConfiguration.Load(ConfigPath).Integrations.Count);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            ClientConfigurationRevision.Read(ConfigPath));
    }

    [Fact]
    public async Task SharedScopeHasOneArtifactAndReferenceCountedRemovalRetainsStartup()
    {
        var first = Cli();
        var second = first with { Id = "cli-alias", DisplayName = "Alias", InstallationId = "other-install" };
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [first, second], RelayPath);
        Assert.True((await Manager().ApplyAsync(plan, default)).SettingsCommitted);
        Assert.Equal(2, Assert.Single(Manifest().Artifacts).TargetIds.Count);
        Assert.Equal(1, runtime.Starts);
        var command = startup.Value;
        var remove = MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [first.Id]);
        await Manager().ApplyAsync(remove, default);
        Assert.Equal(second.Id, Assert.Single(Assert.Single(Manifest().Artifacts).TargetIds));
        Assert.True(File.Exists(Path.Combine(first.HookDirectory, "agent-signaler.json")));
        Assert.Equal(command, startup.Value);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [second.Id]), default);
        Assert.Empty(Manifest().Artifacts);
        Assert.True(File.Exists(ConfigPath));
        Assert.Equal(command, startup.Value);
        Assert.Equal(0, runtime.Stops);
    }

    [Fact]
    public async Task VerifiedVisualStudioVersionsShareOneUnattributedArtifact()
    {
        var directory = Path.Combine(root, "shared-studio", "hooks");
        var first = VerifiedIde("visual-studio", "studio-main", directory, "18.12.1");
        var second = VerifiedIde("visual-studio", "studio-preview", directory, "18.12.2");
        Assert.Equal(HookAdapters.Generate(first, RelayPath, ConfigPath),
            HookAdapters.Generate(second, RelayPath, ConfigPath));
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [first, second], RelayPath), default);
        Assert.Equal(2, Assert.Single(Manifest().Artifacts).TargetIds.Count);
        var hookPath = Path.Combine(directory, "agent-signaler.json");
        var hook = File.ReadAllText(hookPath);
        Assert.Contains("\"shared\"", hook);
        Assert.DoesNotContain("18.12.", hook);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [first.Id]), default);
        Assert.Equal(hook, File.ReadAllText(hookPath));
        Assert.Equal(second.Id, Assert.Single(Assert.Single(Manifest().Artifacts).TargetIds));
    }

    [Fact]
    public async Task VerifiedIsolatedCodeProfileAndCustomCliHomeCanCoexist()
    {
        var code = VerifiedIde("vscode", "isolated-code");
        var cli = Cli("custom-cli", "custom-cli-scope");
        var plan = MultiTargetIntegrationManager.Preview(RemoteConfiguration.Load(ConfigPath),
            ConfigPath, [cli, code], RelayPath, _ => false);
        await Manager().ApplyAsync(plan, default);
        Assert.Equal(2, RemoteConfiguration.Load(ConfigPath).Integrations.Count);
        Assert.Equal(2, Manifest().Artifacts.Count(a => a.Kind == "hook"));
        Assert.Single(Manifest().Artifacts, a => a.Kind == "settings");
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [cli.Id]), default);
        Assert.Equal(code.Id, Assert.Single(RemoteConfiguration.Load(ConfigPath).Integrations).Id);
        Assert.True(File.Exists(Path.Combine(code.HookDirectory, "agent-signaler.json")));
    }

    [Fact]
    public void ExistingDefaultOrExplicitCrossScopeCodeLoaderRemainsBlocked()
    {
        var code = VerifiedIde("vscode", "loader-conflict");
        var cli = Cli("custom-cli", "custom-cli-scope");
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [cli, code], RelayPath, _ => true));
        AtomicFile.Write(code.SettingsPath!, JsoncHookSettings.Add("{}"u8.ToArray(), cli.HookDirectory));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [cli, code], RelayPath, _ => false));
    }

    [Fact]
    public async Task ThreeFreshVerifiedIsolatedSourcesCoexistAndPreserveUnrelatedHooks()
    {
        var unrelatedDirectory = Path.Combine(root, "user-hooks");
        var unrelatedFile = Path.Combine(unrelatedDirectory, "user-owned.json");
        AtomicFile.Write(unrelatedFile, "{\"user-owned\":true}"u8);
        var settingsPath = Path.Combine(root, "mixed-code", "settings.json");
        var originalSettings = "{\r\n// preserve unrelated hooks and comment\r\n\"chat.hookFilesLocations\": {" +
            JsonSerializer.Serialize(unrelatedDirectory) + ": true},\r\n\"editor.fontSize\": 17\r\n}";
        AtomicFile.Write(settingsPath, Encoding.UTF8.GetBytes(originalSettings));
        var code = VerifiedIde("vscode", "mixed-code");
        var studio = VerifiedIde("visual-studio", "mixed-studio", Path.Combine(root, "isolated-studio", "hooks"), "18.12.1");
        var cli = Cli("isolated-cli", "isolated-cli-scope");
        Assert.Equal(originalSettings, File.ReadAllText(settingsPath));
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(RemoteConfiguration.Load(ConfigPath),
            ConfigPath, [cli, studio, code], RelayPath, _ => false), default);
        Assert.Equal(3, RemoteConfiguration.Load(ConfigPath).Integrations.Count);
        Assert.Equal(3, Manifest().Artifacts.Count(a => a.Kind == "hook"));
        Assert.Equal("{\"user-owned\":true}", File.ReadAllText(unrelatedFile));
        Assert.Contains("// preserve unrelated hooks and comment", File.ReadAllText(settingsPath));
        Assert.Contains(JsonSerializer.Serialize(unrelatedDirectory) + ": true", File.ReadAllText(settingsPath));
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [cli.Id]), default);
        Assert.Equal(2, RemoteConfiguration.Load(ConfigPath).Integrations.Count);
        Assert.Equal("{\"user-owned\":true}", File.ReadAllText(unrelatedFile));
        Assert.Contains(JsonSerializer.Serialize(unrelatedDirectory) + ": true", File.ReadAllText(settingsPath));
    }

    [Fact]
    public void MixedSourceInstallationRejectsStaleIdeVerification()
    {
        var code = VerifiedIde("vscode", "stale-mixed-code");
        var studio = VerifiedIde("visual-studio", "stale-mixed-studio");
        File.WriteAllText(studio.ExecutablePath!, "changed executable fingerprint");
        Assert.Throws<InvalidOperationException>(() => MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [Cli(), studio, code], RelayPath, _ => false));
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task CapabilityFailureAndConcurrentEditsDoNotChangeAnything()
    {
        var target = Cli();
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Manager((_, _) => Task.FromResult(false)).ApplyAsync(plan, default));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(ManifestPath));
        Assert.Null(startup.Value);
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Config, Protocol.Json));
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager().ApplyAsync(plan, default));
        Assert.Equal(3, RemoteConfiguration.Load(ConfigPath).Version);
    }

    [Fact]
    public async Task LegacyOwnedHookMigratesWithoutOrphaningAndLegacyApiCannotDowngrade()
    {
        var home = Path.Combine(root, "legacy");
        await Legacy().ApplyAsync(IntegrationManager.Preview(Config, ConfigPath, home, RelayPath), default);
        var oldHook = Path.Combine(home, "hooks", "agent-signaler.json");
        Assert.True(File.Exists(oldHook));
        var target = Cli();
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath), default);
        Assert.False(File.Exists(oldHook));
        Assert.Equal(2, Manifest().Version);
        Assert.Equal("cli", Assert.Single(Assert.Single(Manifest().Artifacts).TargetIds));
        await Assert.ThrowsAsync<InvalidDataException>(() => Legacy().ApplyAsync(
            IntegrationManager.Preview(Config, ConfigPath, home, RelayPath), default));
    }

    [Fact]
    public async Task RepairRecreatesMissingHookButRefusesModifiedOwnedHook()
    {
        var target = Cli();
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath), default);
        var path = Path.Combine(target.HookDirectory, "agent-signaler.json");
        File.Delete(path);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false), default);
        Assert.True(File.Exists(path));
        File.WriteAllText(path, "{\"user\":true}");
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [target.Id]));
    }

    [Fact]
    public async Task RemovalCannotInstallNewUnverifiedTargetsOrRepairMissingOtherHooks()
    {
        var first = Cli("first", "first-scope");
        var second = Cli("second", "second-scope");
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, [first, second], RelayPath), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        var secondPath = Path.Combine(second.HookDirectory, "agent-signaler.json");
        File.Delete(secondPath);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [first.Id]), default);
        Assert.False(File.Exists(secondPath));
        var candidate = Cli("new-ide", "new-scope") with
        {
            Kind = "visual-studio", Capability = IntegrationCapability.VerificationRequired, SupportedEvents = []
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(
            saved with { Integrations = [second, candidate] }, Protocol.Json));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [second.Id]));
        Assert.False(File.Exists(Path.Combine(candidate.HookDirectory, "agent-signaler.json")));
    }

    [Fact]
    public async Task FailureRollsBackAllFilesAndStartupWithoutStartingClient()
    {
        startup.Fail = true;
        var plan = MultiTargetIntegrationManager.Preview(Config, ConfigPath, [Cli("one"), Cli("two", "scope-two")], RelayPath);
        await Assert.ThrowsAsync<IOException>(() => Manager().ApplyAsync(plan, default));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(ManifestPath));
        Assert.All(plan.Targets, t => Assert.False(File.Exists(Path.Combine(t.HookDirectory, "agent-signaler.json"))));
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task RollbackPreservesConcurrentEditAndRecoveryIsPersistent()
    {
        var target = Cli();
        var hookPath = Path.Combine(target.HookDirectory, "agent-signaler.json");
        startup.OnReplace = () => { File.WriteAllText(hookPath, "{\"user\":true}"); throw new IOException("synthetic failure"); };
        await Assert.ThrowsAsync<AggregateException>(() => Manager().ApplyAsync(
            MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath), default));
        Assert.Equal("{\"user\":true}", File.ReadAllText(hookPath));
        Assert.True(File.Exists(Path.Combine(root, "integration-recovery.json")));
        Assert.Throws<AggregateException>(() => Manager().RecoverPending(ConfigPath));
        File.Delete(hookPath);
        Manager().RecoverPending(ConfigPath);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task StoppedClientIsNotResurrectedByUpdateOrPerTargetRemoval()
    {
        var target = Cli();
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, [target], RelayPath), default);
        runtime.Running = false;
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false), default);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [target.Id]), default);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(0, runtime.Stops);
        Assert.Equal(0, runtime.Reloads);
    }

    [Fact]
    public async Task ServicingPrepareRollbackAndFullRemovalUnderstandCollections()
    {
        var targets = new[] { Cli("one"), Cli("two", "scope-two") };
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, targets, RelayPath), default);
        var manifest = File.ReadAllBytes(ManifestPath);
        var config = File.ReadAllBytes(ConfigPath);
        var command = startup.Value;
        Legacy().PrepareUninstall(ConfigPath, "synthetic-msi");
        Assert.False(File.Exists(ManifestPath));
        Assert.True(File.Exists(ConfigPath));
        Assert.All(targets, t => Assert.False(File.Exists(Path.Combine(t.HookDirectory, "agent-signaler.json"))));
        Assert.Null(startup.Value);
        Legacy().RollbackUninstall(ConfigPath, "synthetic-msi");
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath));
        Assert.Equal(command, startup.Value);
        Assert.All(targets, t => Assert.True(File.Exists(Path.Combine(t.HookDirectory, "agent-signaler.json"))));
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Legacy().Uninstall(ConfigPath);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(ManifestPath));
        Assert.Null(startup.Value);
    }

    [Fact]
    public async Task VerifiedVsCodeSettingsPreserveUserChangesAcrossRepairAndRemove()
    {
        var settings = Path.Combine(root, "code", "settings.json");
        var original = Encoding.UTF8.GetBytes("{\r\n // original comment\r\n \"editor.fontSize\": 14,\r\n}\r\n");
        AtomicFile.Write(settings, original);
        var target = VerifiedIde("vscode", "code");
        Assert.Equal(original, File.ReadAllBytes(settings));
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false), default);
        var installed = File.ReadAllText(settings).Replace("\"editor.fontSize\": 14", "\"editor.fontSize\": 18");
        File.WriteAllText(settings, installed);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false), default);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [target.Id]), default);
        var remaining = File.ReadAllText(settings);
        Assert.Contains("// original comment", remaining);
        Assert.Contains("\"editor.fontSize\": 18", remaining);
        Assert.DoesNotContain(JsonSerializer.Serialize(target.HookDirectory), remaining);
        Assert.NotNull(startup.Value);
    }

    [Fact]
    public async Task FullVsCodeRemovalAndRollbackPreserveConcurrentUnrelatedSettings()
    {
        var target = VerifiedIde("vscode", "code-servicing");
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false), default);
        var settings = target.SettingsPath!;
        var installed = File.ReadAllText(settings);
        var edited = installed.Insert(installed.LastIndexOf('}'), "\n\"editor.fontSize\": 21,\n");
        File.WriteAllText(settings, edited);
        Legacy().PrepareUninstall(ConfigPath, "settings-removal");
        Assert.Contains("\"editor.fontSize\": 21", File.ReadAllText(settings));
        Assert.False(JsoncHookSettings.HasOwnedValue(File.ReadAllBytes(settings), target.HookDirectory));
        Legacy().RollbackUninstall(ConfigPath, "settings-removal");
        Assert.Equal(edited, File.ReadAllText(settings));
        Assert.True(JsoncHookSettings.HasOwnedValue(File.ReadAllBytes(settings), target.HookDirectory));
        Legacy().Uninstall(ConfigPath);
        Assert.Contains("\"editor.fontSize\": 21", File.ReadAllText(settings));
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task SettingsChangedDuringCapabilityCheckAreNotOverwritten()
    {
        var target = VerifiedIde("vscode", "code-concurrent");
        var plan = MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false);
        var before = File.ReadAllBytes(ConfigPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager((config, _) =>
        {
            Assert.Equal(4, config.Version);
            AtomicFile.Write(target.SettingsPath!, "{\"user.setting\":true}"u8);
            return Task.FromResult(true);
        }).ApplyAsync(plan, default));
        Assert.Equal("{\"user.setting\":true}", File.ReadAllText(target.SettingsPath!));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.False(File.Exists(ManifestPath));
    }

    [Fact]
    public async Task ExplicitFalseOwnedSettingIsNeverRemoved()
    {
        var target = VerifiedIde("vscode", "code-disabled");
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [target], RelayPath, _ => false), default);
        var settings = File.ReadAllText(target.SettingsPath!).Replace(": true", ": false");
        File.WriteAllText(target.SettingsPath!, settings);
        await Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [target.Id]), default);
        Assert.Equal(settings, File.ReadAllText(target.SettingsPath!));
    }

    [Fact]
    public async Task Version2ManifestUsesExistingTaskOnlyServicingLifecycle()
    {
        await Manager().ApplyAsync(MultiTargetIntegrationManager.Preview(Config, ConfigPath, [Cli()], RelayPath), default);
        var manifest = File.ReadAllBytes(ManifestPath);
        var config = File.ReadAllBytes(ConfigPath);
        var taskName = ScheduledTaskDefinition.Name(machineId);
        var xml = $"<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo><Description>" +
            $"{ScheduledTaskDefinition.Owner(machineId)}</Description></RegistrationInfo><Actions><Exec><Command>" +
            $"{System.Security.SecurityElement.Escape(RelayPath)}</Command><Arguments>heartbeat --config " +
            $"{System.Security.SecurityElement.Escape(ScheduledTaskDefinition.QuoteArgument(ConfigPath))}</Arguments></Exec></Actions></Task>";
        scheduler.Write(taskName, xml);
        Legacy().MigrateLegacyHeartbeat(ConfigPath, root, "version4-update");
        Assert.Null(scheduler.ReadXml(taskName));
        Assert.Equal(manifest, File.ReadAllBytes(ManifestPath));
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Legacy().RollbackLegacyHeartbeat(ConfigPath, root, "version4-update");
        Assert.Equal(xml, scheduler.ReadXml(taskName));
        Legacy().MigrateLegacyHeartbeat(ConfigPath, root, "version4-update");
        Legacy().CommitLegacyHeartbeat(ConfigPath, root, "version4-update");
        Assert.Null(scheduler.ReadXml(taskName));
    }

    [Fact]
    public void UnverifiedIdeAndOverlappingScopesAreRejected()
    {
        var target = Cli();
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(Config, ConfigPath,
            [target with { Kind = "visual-studio", Capability = IntegrationCapability.VerificationRequired }], RelayPath));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(Config, ConfigPath,
            [target, target with { Id = "other", ScopeId = "other" }], RelayPath));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(Config, ConfigPath,
            [target, Cli("nested", "nested", Path.Combine(target.HookDirectory, "nested"))], RelayPath));
    }

    [Fact]
    public void JsoncTokenEditingPreservesCommentsBomAndUnrelatedExactBytes()
    {
        var text = "{\r\n // alpha , }\r\n \"chat.hookFilesLocations\": { /* keep */ \"other\": false, // tail\r\n },\r\n \"editor.fontSize\": 14,\r\n}\r\n";
        var original = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var added = JsoncHookSettings.Add(original, @"Q:\fixture\hooks");
        Assert.True(added.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.True(JsoncHookSettings.HasOwnedValue(added, @"Q:\fixture\hooks"));
        var removed = Encoding.UTF8.GetString(JsoncHookSettings.Remove(added, @"Q:\fixture\hooks"));
        Assert.Contains("/* keep */ \"other\": false, // tail\r\n", removed);
        Assert.Contains("\"editor.fontSize\": 14,\r\n", removed);
        Assert.Contains("// alpha , }", removed);
        Assert.DoesNotContain("fixture", removed);
    }

    [Theory]
    [InlineData("{\"chat.hookFilesLocations\":[],\"other\":1}")]
    [InlineData("{\"chat.hookFilesLocations\":{},\"chat.hookFilesLocations\":{}}")]
    [InlineData("{\"chat.hookFilesLocations\":{\"other\":true,\"other\":false}}")]
    public void AmbiguousJsoncSettingsAreNotRewritten(string json) =>
        Assert.Throws<InvalidDataException>(() => JsoncHookSettings.Add(Encoding.UTF8.GetBytes(json), @"Q:\fixture\hooks"));

    [Fact]
    public void AliasedPathsAndTrailingJsoncContentAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.Preview(Config, ConfigPath,
            [Cli(directory: Path.Combine(root, "SHORT~1", "hooks"))], RelayPath));
        Assert.ThrowsAny<JsonException>(() => JsoncHookSettings.Add(Encoding.UTF8.GetBytes(" \n{} {}"), @"Q:\fixture\hooks"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeScheduler : IIntegrationTaskScheduler
    {
        private readonly Dictionary<string, string> tasks = [];
        public string? ReadXml(string name) => tasks.GetValueOrDefault(name);
        public void Write(string name, string xml) => tasks[name] = xml;
        public void Delete(string name) => tasks.Remove(name);
    }

    private sealed class FakeStartup : IIntegrationStartup
    {
        public string? Value;
        public bool Fail;
        public Action? OnReplace;
        public string? Read(string name) => Value;
        public bool IsDisabled(string name) => false;
        public void Replace(string name, string? expected, string? command)
        {
            if (Value != expected) throw new InvalidDataException("Concurrent synthetic startup change.");
            OnReplace?.Invoke();
            if (Fail) throw new IOException("synthetic startup failure");
            Value = command;
        }
    }

    private sealed class FakeRuntime : IIntegrationRuntime
    {
        public int Starts, Reloads, Stops;
        public bool Running;
        public Exception? QueryFailure;
        public Task<IntegrationRuntimeState> QueryAsync(string path, CancellationToken token) =>
            QueryFailure is null ? Task.FromResult(new IntegrationRuntimeState(Running)) :
                Task.FromException<IntegrationRuntimeState>(QueryFailure);
        public Task<IntegrationRuntimeState> StartAsync(string client, string config, CancellationToken token)
        { Starts++; Running = true; return Task.FromResult(new IntegrationRuntimeState(true, ClientConfigurationRevision.Read(config))); }
        public Task<IntegrationRuntimeState> ReloadAsync(string config, CancellationToken token)
        { Reloads++; return Task.FromResult(new IntegrationRuntimeState(Running, ClientConfigurationRevision.Read(config))); }
        public Task StopAsync(string config, CancellationToken token) { Stops++; Running = false; return Task.CompletedTask; }
    }
}
