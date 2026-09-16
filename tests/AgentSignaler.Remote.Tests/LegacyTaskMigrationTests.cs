using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class LegacyTaskMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly Guid id = Guid.NewGuid();
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string RelayPath => Path.Combine(root, "AgentSignaler.Relay.exe");
    private string JournalPath => IntegrationManager.LegacyHeartbeatJournalPath(ConfigPath, "transaction");
    private string TaskXml
    {
        get
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return new XElement(ns + "Task",
                new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", ScheduledTaskDefinition.Owner(id))),
                new XElement(ns + "Actions", new XElement(ns + "Exec",
                    new XElement(ns + "Command", RelayPath),
                    new XElement(ns + "Arguments", "heartbeat --config " + ScheduledTaskDefinition.QuoteArgument(ConfigPath)))))
                .ToString();
        }
    }

    private IntegrationManager Manager(FakeScheduler scheduler)
    {
        var forbidden = new ForbiddenEffects();
        return new(scheduler, (_, _) => throw new InvalidOperationException("No cloud access allowed."), forbidden, forbidden);
    }

    private Dictionary<string, byte[]> SaveFixture()
    {
        var config = new RemoteConfiguration { Host = "localhost", MachineId = id, RelayPath = RelayPath }.ToVersion2();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json);
        var hookPath = Path.Combine(root, "hooks", "agent-signaler.json");
        var hook = Encoding.UTF8.GetBytes("{\"unchanged\":true}");
        var manifest = new IntegrationManifest(id, hookPath, Hash(hook), Hash(bytes), RelayPath);
        AtomicFile.Write(ConfigPath, bytes);
        AtomicFile.Write(hookPath, hook);
        AtomicFile.Write(Path.Combine(root, "integration.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, Protocol.Json));
        AtomicFile.Write(RemotePaths.Identity(root), Encoding.UTF8.GetBytes(id.ToString("D")));
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
    }

    [Fact]
    public void MigrationOnlyRemovesTaskAndBacksUpExactXml()
    {
        var original = SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.Null(scheduler.Xml);
        Assert.Equal(1, scheduler.Deletes);
        Assert.Equal(0, scheduler.Writes);
        Assert.True(File.Exists(JournalPath));
        Assert.Equal(TaskXml, File.ReadAllText(Assert.Single(Directory.GetFiles(root, "heartbeat-task.xml.agent-signaler.*.backup"))));
        Assert.All(original, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
    }

    [Fact]
    public void RollbackRestoresExactTaskAndOnlyDeletesTaskJournal()
    {
        var original = SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.Equal(TaskXml, scheduler.Xml);
        Assert.Equal(1, scheduler.Writes);
        Assert.False(File.Exists(JournalPath));
        Assert.All(original, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public void CommitDeletesJournalWithoutRecreatingTask()
    {
        var original = SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.CommitLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.CommitLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.Null(scheduler.Xml);
        Assert.Equal(0, scheduler.Writes);
        Assert.False(File.Exists(JournalPath));
        Assert.All(original, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public void MissingManifestCreatesNothingAndDoesNotInspectTasks()
    {
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.CommitLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, scheduler.Reads);
        Assert.Equal(TaskXml, scheduler.Xml);
    }

    [Fact]
    public void MissingTaskCreatesNoJournalAndRollbackIsNoOp()
    {
        SaveFixture();
        var scheduler = new FakeScheduler();
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.False(File.Exists(JournalPath));
        Assert.Empty(Directory.GetFiles(root, "*.backup"));
        Assert.Equal(0, scheduler.Deletes);
        Assert.Equal(0, scheduler.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackIsSafeWhenDeletionFailsBeforeOrAfterMutation(bool afterDelete)
    {
        SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml, FailBeforeDelete = !afterDelete, FailAfterDelete = afterDelete };
        var manager = Manager(scheduler);
        Assert.Throws<IOException>(() => manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction"));
        Assert.True(File.Exists(JournalPath));
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.Equal(TaskXml, scheduler.Xml);
        Assert.Equal(afterDelete ? 1 : 0, scheduler.Writes);
        Assert.False(File.Exists(JournalPath));
    }

    [Theory]
    [InlineData("Command", "C:\\foreign\\AgentSignaler.Relay.exe")]
    [InlineData("Arguments", "heartbeat --config \"C:\\foreign\\remote.json\"")]
    [InlineData("Arguments", "test --config \"C:\\foreign\\remote.json\"")]
    public void ModifiedTaskIsNeverRemovedOrJournaled(string element, string value)
    {
        SaveFixture();
        var task = XDocument.Parse(TaskXml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        task.Root!.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + element)!.Value = value;
        var scheduler = new FakeScheduler { Xml = task.ToString() };
        var original = scheduler.Xml;
        Assert.Throws<InvalidDataException>(() => Manager(scheduler).MigrateLegacyHeartbeat(ConfigPath, root, "transaction"));
        Assert.Equal(original, scheduler.Xml);
        Assert.Equal(0, scheduler.Deletes);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void RollbackRefusesForeignReplacementAndRetainsRecoveryJournal()
    {
        SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        scheduler.Xml = "<Task />";
        Assert.Throws<InvalidDataException>(() => manager.RollbackLegacyHeartbeat(ConfigPath, root, "transaction"));
        Assert.Equal("<Task />", scheduler.Xml);
        Assert.Equal(0, scheduler.Writes);
        Assert.True(File.Exists(JournalPath));
    }

    [Fact]
    public void ForeignInstallationCannotMigrateOrRestoreTask()
    {
        SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        Assert.Throws<InvalidDataException>(() => manager.MigrateLegacyHeartbeat(ConfigPath, Path.Combine(root, "foreign"), "transaction"));
        Assert.Equal(TaskXml, scheduler.Xml);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        Assert.Throws<InvalidDataException>(() => manager.RollbackLegacyHeartbeat(ConfigPath, Path.Combine(root, "foreign"), "transaction"));
        Assert.Null(scheduler.Xml);
        Assert.True(File.Exists(JournalPath));
    }

    [Fact]
    public void IdentityMismatchCannotRemoveTask()
    {
        SaveFixture();
        var config = RemoteConfiguration.Load(ConfigPath) with { MachineId = Guid.NewGuid() };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var scheduler = new FakeScheduler { Xml = TaskXml };
        Assert.Throws<InvalidDataException>(() => Manager(scheduler).MigrateLegacyHeartbeat(ConfigPath, root, "transaction"));
        Assert.Equal(TaskXml, scheduler.Xml);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void UnrelatedTransactionCannotRestoreOrCommitAnotherJournal()
    {
        SaveFixture();
        var scheduler = new FakeScheduler { Xml = TaskXml };
        var manager = Manager(scheduler);
        manager.MigrateLegacyHeartbeat(ConfigPath, root, "transaction");
        manager.RollbackLegacyHeartbeat(ConfigPath, root, "other");
        manager.CommitLegacyHeartbeat(ConfigPath, root, "other");
        Assert.Null(scheduler.Xml);
        Assert.True(File.Exists(JournalPath));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class FakeScheduler : IIntegrationTaskScheduler
    {
        public string? Xml;
        public int Reads;
        public int Writes;
        public int Deletes;
        public bool FailBeforeDelete;
        public bool FailAfterDelete;
        public string? ReadXml(string name) { Reads++; return Xml; }
        public void Write(string name, string xml) { Writes++; Xml = xml; }
        public void Delete(string name)
        {
            Deletes++;
            if (FailBeforeDelete) throw new IOException("Failed before deletion.");
            Xml = null;
            if (FailAfterDelete) throw new IOException("Failed after deletion.");
        }
    }

    private sealed class ForbiddenEffects : IIntegrationStartup, IIntegrationRuntime
    {
        public string? Read(string name) => throw new InvalidOperationException("Startup access forbidden.");
        public void Replace(string name, string? expected, string? command) => throw new InvalidOperationException("Startup mutation forbidden.");
        public bool IsDisabled(string name) => throw new InvalidOperationException("Startup access forbidden.");
        public Task<IntegrationRuntimeState> QueryAsync(string configPath, CancellationToken token) => throw new InvalidOperationException("Runtime access forbidden.");
        public Task<IntegrationRuntimeState> ReloadAsync(string configPath, CancellationToken token) => throw new InvalidOperationException("Runtime mutation forbidden.");
        public Task<IntegrationRuntimeState> StartAsync(string clientPath, string configPath, CancellationToken token) => throw new InvalidOperationException("Runtime launch forbidden.");
        public Task StopAsync(string configPath, CancellationToken token) => throw new InvalidOperationException("Runtime mutation forbidden.");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
