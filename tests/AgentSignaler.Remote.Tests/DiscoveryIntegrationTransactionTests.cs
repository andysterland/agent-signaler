using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

[SupportedOSPlatform("windows")]
public sealed class DiscoveryIntegrationTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "test-data", Guid.NewGuid().ToString("N"));
    private readonly Store store = new();
    private readonly Startup startup = new();
    private readonly Runtime runtime = new();
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string RelayPath => Path.Combine(root, "AgentSignaler.Relay.exe");
    private RemoteConfiguration Direct => new RemoteConfiguration { MachineId = Guid.NewGuid() }
        .WithDashboardUrl("https://synthetic.example/").ToVersion5() with { DetailedReportingEnabled = false };

    private MultiTargetIntegrationManager Manager(Func<RemoteConfiguration, CancellationToken, Task<bool>>? verify = null)
    {
        AtomicFile.Write(RelayPath, []);
        AtomicFile.Write(Path.Combine(root, "AgentSignaler.Client.exe"), []);
        return new(new Scheduler(), verify ?? ((_, _) => Task.FromResult(true)), startup, runtime, _ => store);
    }

    private MultiTargetIntegrationPlan Plan(RemoteConfiguration config, DiscoveryCredentialUpdate? update = null) =>
        MultiTargetIntegrationManager.PreviewWithCredential(config, ConfigPath, [], RelayPath, true, update, store);

    private (RemoteConfiguration Config, DiscoveryCredentialUpdate Update) Import(RemoteConfiguration previous, string ciphertext)
    {
        var bytes = Encoding.UTF8.GetBytes(ciphertext);
        var reference = new DiscoveryCredentialReference(Guid.NewGuid().ToString("N"), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return (previous.WithDiscovery(reference, Guid.NewGuid(), DiscoveryCodec.ExportPublicKey(key), previous.DiscoveryCredentialRevision + 1), new(reference, bytes));
    }

    [Fact]
    public async Task PreviewIsReadOnlyRedactedAndApplyCommitsBoundCredentialRevision()
    {
        var manager = Manager();
        await manager.ApplyAsync(Plan(Direct), default);
        var saved = RemoteConfiguration.Load(ConfigPath);
        var imported = Import(saved, "opaque-ciphertext-one");
        var before = File.ReadAllBytes(ConfigPath);
        var plan = Plan(imported.Config, imported.Update);
        Assert.Empty(store.Files);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Contains(imported.Update.Reference.Id, plan.Preview);
        Assert.Contains(imported.Update.Reference.CiphertextHash, plan.Preview);
        Assert.DoesNotContain("opaque-ciphertext-one", plan.Preview);
        runtime.Running = true;
        var result = await manager.ApplyAsync(plan, default);
        Assert.True(result.SettingsCommitted);
        Assert.True(result.RuntimeApplied);
        Assert.Equal(imported.Update.Ciphertext, store.Files[imported.Update.Reference.Id]);
        var loaded = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(1, loaded.DiscoveryCredentialRevision);
        Assert.Equal(saved.MachineId, loaded.MachineId);
        Assert.False(loaded.DetailedReportingEnabled);
        Assert.Throws<InvalidOperationException>(() => loaded.BaseUri);
        Assert.DoesNotContain("blobSasUri", File.ReadAllText(ConfigPath));
        Assert.DoesNotContain("opaque-ciphertext-one", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public async Task FailedHealthCheckWritesNeitherCredentialNorConfiguration()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var before = File.ReadAllBytes(ConfigPath);
        var imported = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-two");
        var manager = Manager((_, _) =>
        {
            Assert.Empty(store.Files);
            Assert.Equal(before, File.ReadAllBytes(ConfigPath));
            return Task.FromResult(false);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(Plan(imported.Config, imported.Update), default));
        Assert.Empty(store.Files);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task FailedStartupRollsBackNewCredentialAndExactPriorConfiguration()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var before = File.ReadAllBytes(ConfigPath);
        var imported = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-three");
        startup.Value = null;
        startup.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => Manager().ApplyAsync(Plan(imported.Config, imported.Update), default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Empty(store.Files);
        Assert.False(File.Exists(Path.Combine(root, "integration-recovery.json")));
    }

    [Fact]
    public async Task FailedOldCredentialRemovalRestoresOldCredentialAndRemovesReplacement()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var first = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-four");
        await Manager().ApplyAsync(Plan(first.Config, first.Update), default);
        var before = File.ReadAllBytes(ConfigPath);
        var replacement = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-five");
        store.FailDeleteId = first.Update.Reference.Id;
        await Assert.ThrowsAsync<IOException>(() => Manager().ApplyAsync(Plan(replacement.Config, replacement.Update), default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(first.Update.Ciphertext, store.Files[first.Update.Reference.Id]);
        Assert.False(store.Files.ContainsKey(replacement.Update.Reference.Id));
        Assert.Single(store.Files);
    }

    [Fact]
    public async Task MissingSavedCredentialBlocksUnchangedRepairButExplicitReplacementRepairsIt()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var first = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-missing-one");
        await Manager().ApplyAsync(Plan(first.Config, first.Update), default);
        store.Files.Clear();
        var saved = RemoteConfiguration.Load(ConfigPath);
        Assert.Throws<InvalidDataException>(() => Plan(saved));
        var replacement = Import(saved, "ciphertext-missing-two");
        var plan = Plan(replacement.Config, replacement.Update);
        Assert.Contains("VERIFY ABSENT", plan.Preview);
        await Manager().ApplyAsync(plan, default);
        Assert.Equal(replacement.Update.Reference, RemoteConfiguration.Load(ConfigPath).Discovery);
        Assert.Single(store.Files);
        Assert.Equal(replacement.Update.Ciphertext, store.Files[replacement.Update.Reference.Id]);
    }

    [Fact]
    public async Task ChangedCredentialAfterPreviewBlocksWithoutOverwritingConcurrentState()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var imported = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-six");
        var plan = Plan(imported.Config, imported.Update);
        var before = File.ReadAllBytes(ConfigPath);
        store.Files.Add(imported.Update.Reference.Id, [42]);
        await Assert.ThrowsAsync<InvalidDataException>(() => Manager().ApplyAsync(plan, default));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Equal(new byte[] { 42 }, store.Files[imported.Update.Reference.Id]);
    }

    [Fact]
    public async Task RunningClientMustAcknowledgeCredentialRevisionAndRetryCanReconcile()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var imported = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-seven");
        runtime.Running = true;
        runtime.AcknowledgeCredential = false;
        var result = await Manager().ApplyAsync(Plan(imported.Config, imported.Update), default);
        Assert.True(result.SettingsCommitted);
        Assert.False(result.RuntimeApplied);
        runtime.AcknowledgeCredential = true;
        result = await Manager().ApplyAsync(Plan(RemoteConfiguration.Load(ConfigPath)), default);
        Assert.True(result.RuntimeApplied);
        Assert.Single(store.Files);
    }

    [Fact]
    public async Task ExplicitDirectConversionDeletesOnlyOwnedCredentialAndKeepsVersionSix()
    {
        await Manager().ApplyAsync(Plan(Direct), default);
        var imported = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-eight");
        await Manager().ApplyAsync(Plan(imported.Config, imported.Update), default);
        var converted = RemoteConfiguration.Load(ConfigPath).WithDashboardUrl("https://direct.synthetic.example/");
        await Manager().ApplyAsync(Plan(converted), default);
        Assert.Empty(store.Files);
        var saved = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(6, saved.Version);
        Assert.False(saved.IsDiscovery);
        Assert.Equal("direct.synthetic.example", saved.BaseUri.Host);
        Assert.Throws<InvalidDataException>(() => Plan(saved with { Version = 5 }));
    }

    [Fact]
    public async Task ProtectedStoreImportAndDirectConversionUseCurrentUserCiphertextOnly()
    {
        AtomicFile.Write(RelayPath, []);
        AtomicFile.Write(Path.Combine(root, "AgentSignaler.Client.exe"), []);
        var manager = new MultiTargetIntegrationManager(new Scheduler(), (_, _) => Task.FromResult(true), startup, runtime);
        await manager.ApplyAsync(MultiTargetIntegrationManager.Preview(Direct, ConfigPath, [], RelayPath), default);
        var package = new DiscoveryConnectionPackage(Guid.NewGuid(),
            new("https://agenttest.blob.core.windows.net/discovery/endpoint.json?sv=2025-06-01&sr=b&spr=https&si=reader-one&sig=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%3D"),
            DiscoveryCodec.ExportPublicKey(key));
        var update = DiscoveryCredentialTransactionStore.Prepare(package);
        var config = RemoteConfiguration.Load(ConfigPath).WithDiscovery(update.Reference, package.DiscoveryId, package.SigningPublicKey, 1);
        var plan = MultiTargetIntegrationManager.PreviewWithCredential(config, ConfigPath, [], RelayPath, true, update);
        Assert.False(Directory.Exists(DiscoveryCredentialTransactionStore.DirectoryPath(ConfigPath)));
        Assert.DoesNotContain(package.BlobSasUri.OriginalString, plan.Preview);
        await manager.ApplyAsync(plan, default);
        var protectedStore = new CurrentUserDiscoveryCredentialStore(DiscoveryCredentialTransactionStore.DirectoryPath(ConfigPath));
        Assert.Equal(package, await protectedStore.LoadAsync(update.Reference));
        Assert.DoesNotContain("sig=", File.ReadAllText(ConfigPath));
        var direct = RemoteConfiguration.Load(ConfigPath).WithDashboardUrl("https://direct.synthetic.example/");
        await manager.ApplyAsync(MultiTargetIntegrationManager.PreviewWithCredential(direct, ConfigPath, [], RelayPath, true, null), default);
        Assert.Empty(Directory.GetFiles(DiscoveryCredentialTransactionStore.DirectoryPath(ConfigPath)));
        Assert.False(RemoteConfiguration.Load(ConfigPath).IsDiscovery);
    }

    [Fact]
    public async Task RetainedCiphertextJournalRecoversPriorConfigAndRemovesOnlyUnreferencedSlot()
    {
        var manager = Manager();
        await manager.ApplyAsync(Plan(Direct), default);
        var before = File.ReadAllBytes(ConfigPath);
        var imported = Import(RemoteConfiguration.Load(ConfigPath), "ciphertext-recovery");
        var plan = Plan(imported.Config, imported.Update);
        var journalPath = Path.Combine(root, "integration-recovery.json");
        var journal = new MultiTargetIntegrationJournal
        {
            ConfigPath = ConfigPath, Changes = plan.Changes, CredentialChanges = plan.CredentialChanges,
            TaskName = ScheduledTaskDefinition.Name(plan.Config.MachineId),
            StartupName = IntegrationStartup.Name(ConfigPath), StartupBefore = startup.Value, StartupAfter = startup.Value
        };
        await manager.ExecuteAsync(journal, journalPath, default, retainJournal: true);
        Assert.True(File.Exists(journalPath));
        Assert.True(RemoteConfiguration.Load(ConfigPath).IsDiscovery);
        Assert.Single(store.Files);
        Assert.DoesNotContain("sig=", File.ReadAllText(journalPath));
        manager.RecoverPending(ConfigPath);
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.Empty(store.Files);
        Assert.False(File.Exists(journalPath));
    }

    private sealed class Store : IDiscoveryCredentialTransactionStore
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public string? FailDeleteId;
        public byte[]? Read(DiscoveryCredentialReference reference) => Files.TryGetValue(reference.Id, out var bytes) ? bytes.ToArray() : null;
        public void Write(DiscoveryCredentialReference reference, byte[] ciphertext)
        {
            if (!Files.TryAdd(reference.Id, ciphertext.ToArray())) throw new IOException("Synthetic immutable slot collision.");
        }
        public void Delete(DiscoveryCredentialReference reference)
        {
            if (FailDeleteId == reference.Id) throw new IOException("Synthetic owned delete failure.");
            Files.Remove(reference.Id);
        }
    }

    private sealed class Runtime : IIntegrationRuntime
    {
        public bool Running;
        public bool AcknowledgeCredential = true;
        public Task<IntegrationRuntimeState> QueryAsync(string path, CancellationToken token) => Task.FromResult(State(path));
        public Task<IntegrationRuntimeState> ReloadAsync(string path, CancellationToken token) => Task.FromResult(State(path));
        public Task<IntegrationRuntimeState> StartAsync(string client, string path, CancellationToken token)
        { Running = true; return Task.FromResult(State(path)); }
        public Task StopAsync(string path, CancellationToken token) { Running = false; return Task.CompletedTask; }
        public Task SuspendTranscriptAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task PauseTranscriptAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task ReloadTranscriptAsync(string path, CancellationToken token) => Task.CompletedTask;
        private IntegrationRuntimeState State(string path) => !Running ? new(false) : new(true,
            ClientConfigurationRevision.Read(path), EffectiveCredentialRevision: AcknowledgeCredential ? RemoteConfiguration.Load(path).DiscoveryCredentialRevision : null);
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
        public void Write(string name, string xml) => throw new InvalidOperationException("Unexpected task mutation.");
        public void Delete(string name) => throw new InvalidOperationException("Unexpected task mutation.");
    }

    public void Dispose()
    {
        key.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
