using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class DetailedReportingConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly Guid machineId = Guid.NewGuid();
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string RelayPath => Path.Combine(root, "AgentSignaler.Relay.exe");
    private readonly Runtime runtime = new();
    private readonly Startup startup = new();
    private readonly Scheduler scheduler = new();
    private RemoteConfiguration Configuration => new RemoteConfiguration { Host = "localhost", MachineId = machineId }
        .ToVersion5().WithDashboardUrl("https://synthetic.example/");

    private MultiTargetIntegrationManager Manager(Func<RemoteConfiguration, CancellationToken, Task<bool>>? verify = null)
    {
        AtomicFile.Write(RelayPath, []);
        AtomicFile.Write(Path.Combine(root, "AgentSignaler.Client.exe"), []);
        return new(scheduler, verify ?? ((_, _) => Task.FromResult(true)), startup, runtime);
    }

    private MultiTargetIntegrationPlan Plan(RemoteConfiguration configuration) =>
        MultiTargetIntegrationManager.Preview(configuration, ConfigPath, configuration.Integrations, RelayPath);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void OmittedPreferenceDefaultsTrueButLegacyIsStatusOnlyAndLoadDoesNotRewrite(int version)
    {
        var endpoint = version == 1 ? "\"host\":\"localhost\"" : "\"dashboardBaseUrl\":\"https://synthetic.example/\"";
        var bytes = Encoding.UTF8.GetBytes($"{{\"version\":{version},{endpoint},\"machineId\":\"{machineId}\"}}");
        AtomicFile.Write(ConfigPath, bytes);
        var loaded = RemoteConfiguration.Load(ConfigPath);
        Assert.True(loaded.DetailedReportingEnabled);
        Assert.Equal(version >= 5, loaded.IsDetailedReportingEnabled);
        Assert.Equal(version, loaded.Version);
        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
        Assert.True(loaded.ToVersion5().IsDetailedReportingEnabled);
        Assert.Equal(machineId, loaded.ToVersion5().MachineId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void LegacySchemaRejectsDetailedFieldEvenWhenFalse(int version)
    {
        var config = version == 1 ? new RemoteConfiguration { Host = "localhost", MachineId = machineId } :
            Configuration with { Version = version };
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        Assert.DoesNotContain("detailedReportingEnabled", json);
        Assert.False(config.IsDetailedReportingEnabled);
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(
            json[..^1] + ",\"detailedReportingEnabled\":false}", Protocol.Json));
    }

    [Theory]
    [InlineData(",\"DETAILEDREPORTINGENABLED\":false")]
    [InlineData(",\"unknown\":true")]
    [InlineData(",\"host\":\"localhost\"")]
    [InlineData(",\"port\":51820")]
    public void VersionFiveStrictlyRejectsMixedDuplicateAndUnknownFields(string extra)
    {
        var json = JsonSerializer.Serialize(Configuration, Protocol.Json);
        Assert.Throws<InvalidDataException>(() =>
            JsonSerializer.Deserialize<RemoteConfiguration>(json[..^1] + extra + "}", Protocol.Json));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"false\"")]
    [InlineData("0")]
    [InlineData("{}")]
    public void PreferenceMustBeBoolean(string value)
    {
        var json = JsonSerializer.Serialize(Configuration, Protocol.Json)
            .Replace("\"detailedReportingEnabled\":true", "\"detailedReportingEnabled\":" + value);
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Fact]
    public void ExplicitFalseRoundTripsAndCompatibilityHelpersNeverDowngrade()
    {
        var config = Configuration with { DetailedReportingEnabled = false, HeartbeatIntervalSeconds = 600 };
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        Assert.Contains("\"version\":5", json);
        Assert.Contains("\"detailedReportingEnabled\":false", json);
        Assert.DoesNotContain("isDetailedReportingEnabled", json);
        var loaded = JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json)!;
        foreach (var edited in new[] { loaded.ToVersion2(), loaded.ToVersion3(), loaded.ToVersion4(),
                     loaded.ToVersion5(), loaded.WithDashboardUrl("https://other.synthetic.example/") })
        {
            Assert.Equal(5, edited.Version);
            Assert.False(edited.DetailedReportingEnabled);
            Assert.False(edited.IsDetailedReportingEnabled);
            Assert.Equal(600, edited.HeartbeatIntervalSeconds);
        }
    }

    [Fact]
    public async Task OptoutOnlyCommitsOfflineAndUsesOnlyTranscriptReload()
    {
        var manager = Manager();
        await manager.ApplyAsync(Plan(Configuration), default);
        runtime.Running = true;
        runtime.Calls.Clear();
        var changed = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = false };
        var result = await Manager((_, _) => throw new InvalidOperationException("Network must not be used."))
            .ApplyAsync(Plan(changed), default);
        Assert.True(result.SettingsCommitted);
        Assert.True(result.RuntimeApplied);
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.Equal(new[] { "suspend", "query", "transcript-reload" }, runtime.Calls);
        Assert.Equal(ClientConfigurationRevision.Read(ConfigPath), runtime.Revision);
        Assert.False(runtime.Suspended);
    }

    [Fact]
    public async Task EnabledUnchangedRepairPausesWithoutRequestingStickyOptoutAndReconcilesSameRevision()
    {
        await Manager().ApplyAsync(Plan(Configuration), default);
        runtime.Running = true;
        runtime.Calls.Clear();
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        var result = await Manager((_, _) => throw new InvalidOperationException("Unchanged repair must stay local."))
            .ApplyAsync(Plan(RemoteConfiguration.Load(ConfigPath)), default);
        Assert.True(result.SettingsCommitted);
        Assert.True(result.RuntimeApplied);
        Assert.Equal(new[] { "pause", "query", "transcript-reload" }, runtime.Calls);
        Assert.Equal(revision, ClientConfigurationRevision.Read(ConfigPath));
        Assert.Equal(revision, runtime.Revision);
        Assert.True(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
    }

    [Fact]
    public async Task FailedOptoutPreflightRemainsSuspendedAndDoesNotOverwriteSavedPreference()
    {
        await Manager().ApplyAsync(Plan(Configuration), default);
        runtime.Running = true;
        var before = File.ReadAllBytes(ConfigPath);
        var changed = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = false, HeartbeatIntervalSeconds = 600 };
        var manager = Manager((_, _) =>
        {
            Assert.True(runtime.Suspended);
            Assert.Equal(before, File.ReadAllBytes(ConfigPath));
            throw new InvalidOperationException("Synthetic unreachable receiver.");
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(Plan(changed), default));
        Assert.True(runtime.Suspended);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.DoesNotContain("transcript-reload", runtime.Calls);
    }

    [Fact]
    public async Task FailedTransactionAndRecoveryNeverReplaceCommittedFalseWithTrue()
    {
        await Manager().ApplyAsync(Plan(Configuration), default);
        runtime.Running = true;
        startup.Value = null;
        startup.Fail = true;
        var changed = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = false };
        var manager = Manager();
        var failure = await Assert.ThrowsAsync<AggregateException>(() => manager.ApplyAsync(
            MultiTargetIntegrationManager.Preview(changed, ConfigPath, changed.Integrations, RelayPath,
                startClientAtSignIn: true), default));
        Assert.Contains("opt-out", failure.ToString());
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.True(runtime.Suspended);
        var journalPath = Path.Combine(root, "integration-recovery.json");
        Assert.True(File.Exists(journalPath));
        startup.Fail = false;
        Assert.Throws<AggregateException>(() => manager.RecoverPending(ConfigPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.True(File.Exists(journalPath));
        Assert.True(runtime.Suspended);
    }

    [Fact]
    public async Task FailedMigrationRestoresExactLegacyBytesWithoutStartingStoppedClient()
    {
        var legacy = Configuration with { Version = 4, RelayPath = RelayPath };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(legacy, Protocol.Json);
        AtomicFile.Write(ConfigPath, bytes);
        startup.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Manager().ApplyAsync(Plan(legacy.ToVersion5()), default));
        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
        Assert.False(runtime.Running);
        Assert.DoesNotContain("start", runtime.Calls);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task FailedReenableRestoresExactSavedFalse()
    {
        await Manager().ApplyAsync(Plan(Configuration with { DetailedReportingEnabled = false }), default);
        var before = File.ReadAllBytes(ConfigPath);
        runtime.Running = true;
        startup.Value = null;
        startup.Fail = true;
        var changed = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = true };
        await Assert.ThrowsAsync<IOException>(() => Manager().ApplyAsync(
            MultiTargetIntegrationManager.Preview(changed, ConfigPath, changed.Integrations, RelayPath,
                startClientAtSignIn: true), default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.True(runtime.Suspended);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task StalePreviewCannotOverwriteAConcurrentSavedOptout()
    {
        await Manager().ApplyAsync(Plan(Configuration), default);
        var stalePlan = Plan(RemoteConfiguration.Load(ConfigPath) with { HeartbeatIntervalSeconds = 600 });
        var optedOut = RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = false };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(optedOut, Protocol.Json));
        var before = File.ReadAllBytes(ConfigPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager().ApplyAsync(stalePlan, default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.True(runtime.Suspended);
    }

    [Fact]
    public async Task RepairRemovalAndUninstallRollbackPreserveV5Optout()
    {
        var target = new IntegrationTarget
        {
            Id = "synthetic-cli", Kind = "copilot-cli", ScopeId = "synthetic-scope",
            DisplayName = "Synthetic CLI", InstallationId = "synthetic", HostVersion = "1.0.83",
            HookDirectory = Path.Combine(root, "synthetic-hooks"), Capability = IntegrationCapability.Verified,
            SupportedEvents = HookAdapters.Events("copilot-cli")
        };
        var manager = Manager();
        await manager.ApplyAsync(Plan(Configuration with { DetailedReportingEnabled = false, Integrations = [target] }), default);
        var loaded = RemoteConfiguration.Load(ConfigPath);
        await manager.ApplyAsync(Plan(loaded.ToVersion4()), default);
        await manager.ApplyAsync(MultiTargetIntegrationManager.PreviewRemove(ConfigPath, [target.Id]), default);
        var saved = File.ReadAllBytes(ConfigPath);
        var servicing = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true), startup, runtime);
        servicing.PrepareUninstall(ConfigPath, "synthetic");
        servicing.RollbackUninstall(ConfigPath, "synthetic");
        Assert.Equal(saved, File.ReadAllBytes(ConfigPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.Equal(5, RemoteConfiguration.Load(ConfigPath).Version);
        Assert.Throws<InvalidDataException>(() => Plan(loaded with { Version = 4 }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExplicitDowngradeStopsExactClientAndRemovesFlagWithoutStartingReporting(bool enabled)
    {
        await Manager().ApplyAsync(Plan(Configuration with { DetailedReportingEnabled = enabled }), default);
        runtime.Running = true;
        runtime.Calls.Clear();
        var preview = MultiTargetIntegrationManager.PreviewStatusOnlyDowngrade(ConfigPath);
        Assert.True(preview.IsStatusOnlyDowngrade);
        Assert.Contains("not preserved in v4", preview.Preview);
        var result = await Manager((_, _) => throw new InvalidOperationException("Downgrade must not wait on network."))
            .ApplyAsync(preview, default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.False(runtime.Running);
        Assert.Equal(new[] { "query", "stop" }, runtime.Calls);
        var saved = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(4, saved.Version);
        Assert.False(saved.IsDetailedReportingEnabled);
        Assert.DoesNotContain("detailedReportingEnabled", File.ReadAllText(ConfigPath));
        Assert.True(saved.ToVersion5().IsDetailedReportingEnabled);
        Assert.Equal(saved.MachineId, Configuration.MachineId);
    }

    [Fact]
    public async Task DowngradeRequiresSuccessfulExactClientShutdownBeforeWriting()
    {
        await Manager().ApplyAsync(Plan(Configuration with { DetailedReportingEnabled = false }), default);
        runtime.Running = true;
        runtime.StopFailure = true;
        var before = File.ReadAllBytes(ConfigPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Manager().ApplyAsync(MultiTargetIntegrationManager.PreviewStatusOnlyDowngrade(ConfigPath), default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.True(runtime.Running);
    }

    [Fact]
    public async Task DowngradeRefusesModifiedConfigurationOwnership()
    {
        await Manager().ApplyAsync(Plan(Configuration), default);
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(
            RemoteConfiguration.Load(ConfigPath) with { DetailedReportingEnabled = false }, Protocol.Json));
        Assert.Throws<InvalidDataException>(() => MultiTargetIntegrationManager.PreviewStatusOnlyDowngrade(ConfigPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
    }

    private sealed class Runtime : IIntegrationRuntime
    {
        public bool Running;
        public bool Suspended;
        public bool StopFailure;
        public string? Revision;
        public List<string> Calls { get; } = [];
        public Task<IntegrationRuntimeState> QueryAsync(string path, CancellationToken token)
        { Calls.Add("query"); return Task.FromResult(new IntegrationRuntimeState(Running)); }
        public Task<IntegrationRuntimeState> StartAsync(string client, string path, CancellationToken token)
        { Calls.Add("start"); Running = true; return ReloadAsync(path, token); }
        public Task<IntegrationRuntimeState> ReloadAsync(string path, CancellationToken token)
        { Calls.Add("reload"); Suspended = false; return Task.FromResult(new IntegrationRuntimeState(Running, ClientConfigurationRevision.Read(path))); }
        public Task StopAsync(string path, CancellationToken token)
        {
            Calls.Add("stop");
            if (StopFailure) throw new InvalidOperationException("Synthetic shutdown failure.");
            Running = false; Suspended = true; return Task.CompletedTask;
        }
        public Task SuspendTranscriptAsync(string path, CancellationToken token)
        { Calls.Add("suspend"); Suspended = true; return Task.CompletedTask; }
        public Task PauseTranscriptAsync(string path, CancellationToken token)
        { Calls.Add("pause"); Suspended = true; return Task.CompletedTask; }
        public Task ReloadTranscriptAsync(string path, CancellationToken token)
        { Calls.Add("transcript-reload"); Suspended = false; Revision = ClientConfigurationRevision.Read(path); return Task.CompletedTask; }
    }

    private sealed class Startup : IIntegrationStartup
    {
        public string? Value;
        public bool Fail;
        public string? Read(string name) => Value;
        public bool IsDisabled(string name) => false;
        public void Replace(string name, string? expected, string? command)
        {
            if (Value != expected) throw new InvalidDataException("Synthetic ownership conflict.");
            if (Fail) throw new IOException("Synthetic startup failure.");
            Value = command;
        }
    }

    private sealed class Scheduler : IIntegrationTaskScheduler
    {
        public string? ReadXml(string name) => null;
        public void Write(string name, string xml) => throw new InvalidOperationException("Unexpected task write.");
        public void Delete(string name) => throw new InvalidOperationException("Unexpected task deletion.");
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
