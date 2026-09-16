using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class SettingsServicingTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private string ConfigPath => Path.Combine(root, "remote.json");
    private RemoteConfiguration Config => new RemoteConfiguration { Host = "localhost", MachineId = id }.ToVersion3();
    private IntegrationPlan Plan(int seconds = 300) => IntegrationManager.Preview(Config with
    { HeartbeatIntervalSeconds = seconds }, ConfigPath, Path.Combine(root, "copilot"), Path.Combine(root, "AgentSignaler.Relay.exe"));

    private IntegrationManager Manager(FakeStartup startup, FakeRuntime runtime, FakeScheduler? scheduler = null,
        Func<RemoteConfiguration, CancellationToken, Task<bool>>? verify = null)
    {
        AtomicFile.Write(Plan().RelayPath, []);
        AtomicFile.Write(Plan().ClientPath, []);
        return new(scheduler ?? new(), verify ?? ((_, _) => Task.FromResult(true)), startup, runtime);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void MissingIntervalDefaultsAndReadsDoNotRewrite(int version)
    {
        var fields = version == 1 ? "\"host\":\"localhost\",\"port\":51820" : "\"dashboardBaseUrl\":\"http://localhost:51820/\"";
        var bytes = Encoding.UTF8.GetBytes($"{{\"version\":{version},{fields},\"machineId\":\"{id}\"}}");
        AtomicFile.Write(ConfigPath, bytes);
        var loaded = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(300, loaded.HeartbeatIntervalSeconds);
        Assert.Equal(id, loaded.ToVersion3().MachineId);
        Assert.Equal(version, loaded.Version);
        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
    }

    [Fact]
    public void VersionThreeHasExactSchemaAndPreservesMetadata()
    {
        var config = Config with { HeartbeatIntervalSeconds = 60, MachineName = "machine", ClientVersion = "1.2.3" };
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        Assert.Equal($"{{\"version\":3,\"dashboardBaseUrl\":\"http://localhost:51820/\",\"machineId\":\"{id}\"," +
            "\"machineName\":\"machine\",\"clientVersion\":\"1.2.3\",\"heartbeatIntervalSeconds\":60}", json);
        Assert.Equal(config, JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(300)]
    [InlineData(3600)]
    public void ValidIntervalsRoundTrip(int seconds)
    {
        var config = Config with { HeartbeatIntervalSeconds = seconds };
        Assert.Equal(config, JsonSerializer.Deserialize<RemoteConfiguration>(JsonSerializer.Serialize(config, Protocol.Json), Protocol.Json));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    [InlineData(59)]
    [InlineData(61)]
    [InlineData(3601)]
    [InlineData(3660)]
    public void InvalidIntervalsRejectedOnReadAndWrite(int seconds)
    {
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Serialize(Config with { HeartbeatIntervalSeconds = seconds }, Protocol.Json));
        var json = JsonSerializer.Serialize(Config, Protocol.Json).Replace("\"heartbeatIntervalSeconds\":300", $"\"heartbeatIntervalSeconds\":{seconds}");
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void LegacySchemaRejectsIntervalEvenWhenDefault(int version)
    {
        var config = new RemoteConfiguration { Host = "localhost", MachineId = id };
        if (version == 2) config = config.ToVersion2();
        var json = JsonSerializer.Serialize(config, Protocol.Json).TrimEnd('}') + ",\"heartbeatIntervalSeconds\":300}";
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Theory]
    [InlineData("\"heartbeatIntervalSeconds\":300,\"HEARTBEATINTERVALSECONDS\":300")]
    [InlineData("\"heartbeatIntervalSeconds\":300,\"unknown\":true")]
    [InlineData("\"heartbeatIntervalSeconds\":300,\"host\":\"localhost\"")]
    [InlineData("\"heartbeatIntervalSeconds\":300,\"port\":51820")]
    public void VersionThreeRejectsDuplicateUnknownAndLegacyFields(string fields)
    {
        var json = $"{{\"version\":3,\"dashboardBaseUrl\":\"https://dashboard/\",\"machineId\":\"{id}\",{fields}}}";
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Fact]
    public void StartupCommandHasStableExactOwnership()
    {
        var plan = Plan();
        Assert.Equal(plan.StartupName, IntegrationStartup.Name(ConfigPath.ToUpperInvariant()));
        Assert.Equal($"\"{plan.ClientPath}\" --background --config \"{ConfigPath.ToUpperInvariant()}\"", plan.StartupCommand);
        Assert.Contains(plan.ClientPath, plan.Preview);
        Assert.Contains("660 seconds", plan.Preview);
        Assert.Contains("HKCU\\", plan.Preview);
        Assert.Contains("First setup and first legacy migration launch", plan.Preview);
        Assert.Contains("remove/reset the machine locally in Dashboard", plan.Preview);
        Assert.Contains("display names, notes and mappings", plan.Preview);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public async Task ManagedHealthIsNonmutatingAndRequiresV2(int version, bool compatible)
    {
        using var client = new HttpClient(new HealthHandler(version));
        if (compatible) await DashboardConnection.TestAsync(Config, client, default);
        else await Assert.ThrowsAsync<InvalidDataException>(() => DashboardConnection.TestAsync(Config, client, default));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DisabledSignInStartupIsVisibleAfterSuccessfulActivation()
    {
        var manager = Manager(new FakeStartup { Disabled = true }, new());
        var result = await manager.ApplyAsync(Plan(), default);
        Assert.True(result.RuntimeApplied);
        Assert.Contains("disabled sign-in startup", result.Message);
    }

    [Fact]
    public async Task StaleEffectiveRevisionIsNotReportedAsApplied()
    {
        var manager = Manager(new(), new FakeRuntime { StaleRevision = true });
        var result = await manager.ApplyAsync(Plan(), default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.Equal("stale", result.EffectiveRevision);
    }

    [Fact]
    public async Task TaskReplacedDuringHealthCheckIsNeverRemoved()
    {
        var scheduler = new FakeScheduler { Xml = LegacyTask };
        var manager = Manager(new(), new(), scheduler, (_, _) =>
        {
            scheduler.Xml = "<Task />";
            return Task.FromResult(true);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyAsync(Plan(), default));
        Assert.Equal("<Task />", scheduler.Xml);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task FirstApplyStartsOnlyAfterCommitAndUpdatesReloadRunningClient()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var manager = Manager(startup, runtime);
        var first = await manager.ApplyAsync(Plan(), default);
        Assert.True(first.RuntimeApplied);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(Plan().StartupCommand, startup.Value);
        var update = await manager.ApplyAsync(Plan(600), default);
        Assert.True(update.RuntimeApplied);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(1, runtime.Reloads);
        Assert.Equal(600, RemoteConfiguration.Load(ConfigPath).HeartbeatIntervalSeconds);
    }

    [Fact]
    public async Task ExistingStoppedClientIsNeverRestartedByApply()
    {
        var runtime = new FakeRuntime();
        var manager = Manager(new(), runtime);
        await manager.ApplyAsync(Plan(), default);
        runtime.Running = false;
        var result = await manager.ApplyAsync(Plan(600), default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.Contains("Start client", result.Message);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(0, runtime.Reloads);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FirstLegacyMigrationStartsClientButSubsequentStoppedManagedApplyDoesNot(int version)
    {
        var plan = Plan();
        var legacy = new RemoteConfiguration { Host = "localhost", MachineId = id, RelayPath = plan.RelayPath };
        if (version == 2) legacy = legacy.ToVersion2();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(legacy, Protocol.Json);
        AtomicFile.Write(ConfigPath, bytes);
        AtomicFile.Write(plan.HookPath, plan.HookBytes);
        AtomicFile.Write(Path.Combine(root, "integration.json"), JsonSerializer.SerializeToUtf8Bytes(
            new IntegrationManifest(id, plan.HookPath, Convert.ToHexString(SHA256.HashData(plan.HookBytes)),
                Convert.ToHexString(SHA256.HashData(bytes)), plan.RelayPath), Protocol.Json));
        var runtime = new FakeRuntime();
        var scheduler = new FakeScheduler { Xml = LegacyTask };
        var manager = Manager(new(), runtime, scheduler);
        Assert.True((await manager.ApplyAsync(plan, default)).RuntimeApplied);
        Assert.Equal(1, runtime.Starts);
        Assert.Null(scheduler.Xml);
        Assert.Equal(id, RemoteConfiguration.Load(ConfigPath).MachineId);
        runtime.Running = false;
        Assert.False((await manager.ApplyAsync(Plan(600), default)).RuntimeApplied);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(0, runtime.Reloads);
    }

    [Theory]
    [InlineData("Command", "C:\\foreign\\AgentSignaler.Relay.exe")]
    [InlineData("Arguments", "test --config \"C:\\foreign\\remote.json\"")]
    [InlineData("Arguments", "heartbeat --config \"C:\\foreign\\remote.json\"")]
    public async Task ModifiedLegacyActionIsPreservedDuringApplyAndRemoval(string element, string value)
    {
        var runtime = new FakeRuntime();
        var scheduler = new FakeScheduler();
        var manager = Manager(new(), runtime, scheduler);
        await manager.ApplyAsync(Plan(), default);
        var document = System.Xml.Linq.XDocument.Parse(LegacyTask);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        document.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + element)!.Value = value;
        scheduler.Xml = document.ToString();
        var originalTask = scheduler.Xml;
        var originalConfig = File.ReadAllBytes(ConfigPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyAsync(Plan(600), default));
        Assert.Throws<InvalidDataException>(() => manager.Uninstall(ConfigPath));
        Assert.Equal(originalTask, scheduler.Xml);
        Assert.Equal(originalConfig, File.ReadAllBytes(ConfigPath));
        Assert.True(runtime.Running);
        Assert.Null(runtime.StoppedConfig);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeFailureDoesNotUndoCommittedSettings(bool launchFailure)
    {
        var runtime = new FakeRuntime();
        var manager = Manager(new(), runtime);
        if (!launchFailure) await manager.ApplyAsync(Plan(), default);
        runtime.FailActivation = true;
        var result = await manager.ApplyAsync(Plan(600), default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        Assert.Equal(600, RemoteConfiguration.Load(ConfigPath).HeartbeatIntervalSeconds);
        Assert.True(File.Exists(Plan().HookPath));
    }

    [Fact]
    public async Task CapabilityFailureLeavesStartupFilesAndRuntimeUntouched()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var manager = Manager(startup, runtime, verify: (_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(Plan(), default));
        Assert.Null(startup.Value);
        Assert.False(File.Exists(ConfigPath));
        Assert.Equal(0, runtime.Starts);
        Assert.Equal(0, runtime.Reloads);
    }

    [Fact]
    public async Task ForeignStartupIsNeverOverwritten()
    {
        var startup = new FakeStartup { Value = "\"foreign.exe\"" };
        var manager = Manager(startup, new());
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyAsync(Plan(), default));
        Assert.Equal("\"foreign.exe\"", startup.Value);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task StartupFailureRestoresFilesAndRemovedLegacyTask()
    {
        var startup = new FakeStartup { FailAfterWrite = true };
        var scheduler = new FakeScheduler { Xml = LegacyTask };
        var runtime = new FakeRuntime();
        var manager = Manager(startup, runtime, scheduler);
        await Assert.ThrowsAsync<IOException>(() => manager.ApplyAsync(Plan(), default));
        Assert.Null(startup.Value);
        Assert.Equal(LegacyTask, scheduler.Xml);
        Assert.Equal(1, scheduler.Writes);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(Plan().HookPath));
        Assert.Equal(0, runtime.Starts);
    }

    [Fact]
    public async Task RollbackNeverOverwritesConcurrentForeignStartup()
    {
        var startup = new FakeStartup { ForeignAfterWrite = true };
        var manager = Manager(startup, new());
        await Assert.ThrowsAsync<AggregateException>(() => manager.ApplyAsync(Plan(), default));
        Assert.Equal("foreign", startup.Value);
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task UninstallStopsExactClientAndJournalRestoresOwnedArtifactsWithoutLaunchingGui()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var scheduler = new FakeScheduler();
        var manager = Manager(startup, runtime, scheduler);
        await manager.ApplyAsync(Plan(), default);
        scheduler.Xml = LegacyTask;
        manager.PrepareUninstall(ConfigPath, "transaction");
        Assert.Equal(ConfigPath, runtime.StoppedConfig);
        Assert.False(runtime.Running);
        Assert.Null(startup.Value);
        Assert.Null(scheduler.Xml);
        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(Plan().HookPath));
        manager.RollbackUninstall(ConfigPath, "transaction");
        Assert.Equal(Plan().StartupCommand, startup.Value);
        Assert.Equal(LegacyTask, scheduler.Xml);
        Assert.True(File.Exists(Plan().HookPath));
        Assert.Equal(1, runtime.Starts);
    }

    [Fact]
    public async Task ShutdownFailurePreservesIntegrationAndStartup()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var manager = Manager(startup, runtime);
        await manager.ApplyAsync(Plan(), default);
        runtime.FailStop = true;
        Assert.Throws<IOException>(() => manager.PrepareUninstall(ConfigPath));
        Assert.Equal(Plan().StartupCommand, startup.Value);
        Assert.True(File.Exists(Plan().HookPath));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
    }

    [Fact]
    public void UninstallWithoutManifestStillStopsTheExactConfigClient()
    {
        var runtime = new FakeRuntime { Running = true };
        var manager = Manager(new(), runtime);
        manager.Uninstall(ConfigPath);
        Assert.False(runtime.Running);
        Assert.Equal(ConfigPath, runtime.StoppedConfig);
    }

    [Fact]
    public async Task FailedSettingsCommitPreservesOldRuntimeRevisionAndIdentity()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var manager = Manager(startup, runtime);
        await manager.ApplyAsync(Plan(), default);
        var original = File.ReadAllBytes(ConfigPath);
        startup.FailAfterWrite = true;
        await Assert.ThrowsAsync<IOException>(() => manager.ApplyAsync(Plan(600), default));
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));
        Assert.Equal(id, RemoteConfiguration.Load(ConfigPath).MachineId);
        Assert.Equal(Plan().StartupCommand, startup.Value);
        Assert.True(runtime.Running);
        Assert.Equal(0, runtime.Reloads);
    }

    [Fact]
    public async Task UpdateStopPreservesEveryFileStartupAndTaskAndNeverRestarts()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var scheduler = new FakeScheduler();
        var manager = Manager(startup, runtime, scheduler);
        await manager.ApplyAsync(Plan(), default);
        scheduler.Xml = LegacyTask;
        var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        await manager.StopClientForUpdateAsync(ConfigPath, root, default);
        Assert.False(runtime.Running);
        Assert.Equal(ConfigPath.ToUpperInvariant(), runtime.StoppedConfig);
        Assert.Equal(1, runtime.Starts);
        Assert.Equal(0, runtime.Reloads);
        Assert.Equal(Plan().StartupCommand, startup.Value);
        Assert.Equal(LegacyTask, scheduler.Xml);
        Assert.Equal(files.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var pair in files) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
    }

    [Fact]
    public async Task UpdateStopRefusesForeignInstallationBeforeSendingIpc()
    {
        var runtime = new FakeRuntime();
        var manager = Manager(new(), runtime);
        await manager.ApplyAsync(Plan(), default);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.StopClientForUpdateAsync(ConfigPath, Path.Combine(root, "other-installation"), default));
        Assert.Null(runtime.StoppedConfig);
        Assert.True(runtime.Running);
    }

    [Fact]
    public async Task UpdateStopRefusesUnregisteredRunningClientButAllowsUnconfiguredInstall()
    {
        var runtime = new FakeRuntime();
        var manager = Manager(new(), runtime);
        await manager.StopClientForUpdateAsync(ConfigPath, root, default);
        Assert.Null(runtime.StoppedConfig);
        runtime.Running = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.StopClientForUpdateAsync(ConfigPath, root, default));
        Assert.Null(runtime.StoppedConfig);
    }

    [Fact]
    public async Task UpdateStopReportsFailureWithoutRemovingIntegration()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var manager = Manager(startup, runtime);
        await manager.ApplyAsync(Plan(), default);
        runtime.FailStop = true;
        await Assert.ThrowsAsync<IOException>(() => manager.StopClientForUpdateAsync(ConfigPath, root, default));
        Assert.Equal(Plan().StartupCommand, startup.Value);
        Assert.True(File.Exists(Plan().HookPath));
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task UpdateStopRefusesConfigurationIdentityConflict()
    {
        var runtime = new FakeRuntime();
        var manager = Manager(new(), runtime);
        await manager.ApplyAsync(Plan(), default);
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Config with { MachineId = Guid.NewGuid() }, Protocol.Json));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.StopClientForUpdateAsync(ConfigPath, root, default));
        Assert.Null(runtime.StoppedConfig);
        Assert.True(runtime.Running);
    }

    [Fact]
    public async Task UninstallRollbackPreservesForeignReplacementStartup()
    {
        var startup = new FakeStartup();
        var manager = Manager(startup, new());
        await manager.ApplyAsync(Plan(), default);
        manager.PrepareUninstall(ConfigPath);
        startup.Value = "foreign";
        Assert.Throws<InvalidDataException>(() => manager.RollbackUninstall(ConfigPath));
        Assert.Equal("foreign", startup.Value);
        Assert.True(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
    }

    [Fact]
    public async Task PendingInstallerTaskMigrationBlocksApplyUntilRollbackCompletes()
    {
        var startup = new FakeStartup();
        var runtime = new FakeRuntime();
        var scheduler = new FakeScheduler();
        var manager = Manager(startup, runtime, scheduler);
        await manager.ApplyAsync(Plan(), default);
        scheduler.Xml = LegacyTask;
        var config = File.ReadAllBytes(ConfigPath);
        var hook = File.ReadAllBytes(Plan().HookPath);
        var startupCommand = startup.Value;

        manager.MigrateLegacyHeartbeat(ConfigPath, root, "task-transaction");
        Assert.Null(scheduler.Xml);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(Plan(600), default));
        Assert.Contains("heartbeat migration", error.Message);
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Assert.Equal(hook, File.ReadAllBytes(Plan().HookPath));
        Assert.Equal(startupCommand, startup.Value);
        Assert.Equal(0, runtime.Reloads);

        manager.RollbackLegacyHeartbeat(ConfigPath, root, "task-transaction");
        Assert.Equal(LegacyTask, scheduler.Xml);
        Assert.True((await manager.ApplyAsync(Plan(600), default)).RuntimeApplied);
        Assert.Null(scheduler.Xml);
        Assert.Equal(1, runtime.Reloads);
    }

    private string LegacyTask => "<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><RegistrationInfo>" +
        $"<Description>{ScheduledTaskDefinition.Owner(id)}</Description></RegistrationInfo><Actions><Exec>" +
        $"<Command>{System.Security.SecurityElement.Escape(Plan().RelayPath)}</Command>" +
        $"<Arguments>heartbeat --config {System.Security.SecurityElement.Escape(ScheduledTaskDefinition.QuoteArgument(ConfigPath))}</Arguments>" +
        "</Exec></Actions></Task>";

    private sealed class FakeStartup : IIntegrationStartup
    {
        public string? Value;
        public bool FailAfterWrite;
        public bool ForeignAfterWrite;
        public bool Disabled;
        public string? Read(string name) => Value;
        public bool IsDisabled(string name) => Disabled;
        public void Replace(string name, string? expected, string? command)
        {
            if (Value != expected) throw new InvalidDataException("foreign");
            Value = command;
            if (ForeignAfterWrite) { ForeignAfterWrite = false; Value = "foreign"; throw new IOException("failed"); }
            if (FailAfterWrite) { FailAfterWrite = false; throw new IOException("failed"); }
        }
    }

    private sealed class FakeScheduler : IIntegrationTaskScheduler
    {
        public string? Xml;
        public int Writes;
        public string? ReadXml(string name) => Xml;
        public void Write(string name, string xml) { Xml = xml; Writes++; }
        public void Delete(string name) => Xml = null;
    }

    private sealed class FakeRuntime : IIntegrationRuntime
    {
        public bool Running;
        public bool FailActivation;
        public bool FailStop;
        public bool StaleRevision;
        public int Starts;
        public int Reloads;
        public string? StoppedConfig;
        public Task<IntegrationRuntimeState> QueryAsync(string path, CancellationToken token) =>
            Task.FromResult(new IntegrationRuntimeState(Running));
        public Task<IntegrationRuntimeState> StartAsync(string client, string path, CancellationToken token)
        { Starts++; return Activate(path); }
        public Task<IntegrationRuntimeState> ReloadAsync(string path, CancellationToken token)
        { Reloads++; return Activate(path); }
        private Task<IntegrationRuntimeState> Activate(string path)
        {
            if (FailActivation) throw new IOException("Cannot activate");
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "integration.json")));
            Running = true;
            return Task.FromResult(new IntegrationRuntimeState(true, StaleRevision ? "stale" : Convert.ToHexString(SHA256.HashData(
                AtomicFile.ReadBounded(path, 8192)))));
        }

        public Task StopAsync(string path, CancellationToken token)
        {
            if (FailStop) throw new IOException("Client could not be stopped safely");
            StoppedConfig = path;
            Running = false;
            return Task.CompletedTask;
        }
    }

    private sealed class HealthHandler(int version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v2/health", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"protocolVersion\":{version},\"status\":\"ok\"}}", Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
