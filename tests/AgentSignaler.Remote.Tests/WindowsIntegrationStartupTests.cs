using AgentSignaler.Remote;
using AgentSignaler.Contracts;
using System.Runtime.Versioning;
using System.Text.Json;
using Xunit;

namespace AgentSignaler.Remote.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsIntegrationStartupTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly StartupRegistry registry = new();
    private string Config => Path.Combine(root, "configuration with spaces", "remote.json");
    private string Client => Path.Combine(root, "installation with spaces", "AgentSignaler.Client.exe");
    private string Name => IntegrationStartup.Name(Config);
    private string Command => IntegrationStartup.Command(Client, Config);
    private string Shortcut => Path.Combine(root, "Startup", Name + ".lnk");
    private WindowsIntegrationStartup Startup => new(Path.Combine(root, "Startup"), registry);

    [Fact]
    public void MissingDirectoryReadsUnregisteredAndIsCreatedOnlyWhenApplyingShortcut()
    {
        var startup = Startup;
        Assert.False(Directory.Exists(root));
        Assert.Null(startup.Read(Name));
        Assert.False(startup.IsDisabled(Name));
        var before = startup.Capture(Name);
        var after = startup.Prepare(Name, before, Command);
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, registry.LegacyWrites);
        Assert.Equal(0, registry.ApprovalWrites);
        startup.ReplaceState(Name, before, after);
        Assert.True(File.Exists(Shortcut));
        Assert.Equal(Command, startup.Read(Name));
    }

    [Fact]
    public void UnavailableFolderDoesNotFailConstructionAndRefreshCanRetry()
    {
        var path = "";
        var resolutions = 0;
        var startup = new WindowsIntegrationStartup(() => { resolutions++; return path; }, registry);
        Assert.Equal(0, resolutions);
        var error = Assert.Throws<InvalidOperationException>(() => startup.Read(Name));
        Assert.Contains("Startup folder is unavailable", error.Message);
        Assert.Equal(1, resolutions);
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, registry.LegacyWrites);
        Assert.Equal(0, registry.ApprovalWrites);
        path = Path.Combine(root, "Startup");
        Assert.Null(startup.Read(Name));
        Assert.Equal(2, resolutions);
        Assert.False(Directory.Exists(root));
        startup.Replace(Name, null, Command);
        Assert.True(File.Exists(Shortcut));
        Assert.Equal(2, resolutions);
    }

    [Fact]
    public void FolderResolutionFailureIsDeferredAndDoesNotMutateState()
    {
        var unavailable = true;
        var startup = new WindowsIntegrationStartup(() => unavailable
            ? throw new UnauthorizedAccessException("Synthetic unavailable folder.")
            : Path.Combine(root, "Startup"), registry);
        Assert.Throws<UnauthorizedAccessException>(() => startup.Read(Name));
        Assert.Throws<UnauthorizedAccessException>(() => startup.Replace(Name, null, Command));
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, registry.LegacyWrites);
        Assert.Equal(0, registry.ApprovalWrites);
        unavailable = false;
        Assert.Null(startup.Read(Name));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void InvalidFolderIsRejectedOnAccessRatherThanDuringConstruction()
    {
        var startup = new WindowsIntegrationStartup("relative-startup-folder", registry);
        Assert.Throws<InvalidDataException>(() => startup.Read(Name));
        Assert.Throws<InvalidDataException>(() => startup.Replace(Name, null, Command));
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, registry.LegacyWrites);
        Assert.Equal(0, registry.ApprovalWrites);
    }

    [Fact]
    public void CreatesRealOwnedShortcutWithCanonicalTargetArgumentsAndNoRunRegistration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Client)!);
        File.WriteAllBytes(Client, []);
        Startup.Replace(Name, null, Command);
        var bytes = File.ReadAllBytes(Shortcut);
        Assert.Equal(76u, BitConverter.ToUInt32(bytes));
        Assert.Equal(new Guid("00021401-0000-0000-c000-000000000046"), new Guid(bytes.AsSpan(4, 16)));
        Assert.Equal(Command, WindowsStartupShortcut.Read(Name, bytes));
        Assert.Equal(Command, Startup.Read(Name));
        Assert.Null(registry.Legacy);
        Assert.Equal(0, registry.LegacyWrites);
        var before = File.GetLastWriteTimeUtc(Shortcut);
        Startup.Replace(Name, Command, Command);
        Assert.Equal(bytes, File.ReadAllBytes(Shortcut));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Shortcut));
        Startup.Replace(Name, Command, null);
        Assert.False(File.Exists(Shortcut));
        Assert.Null(Startup.Read(Name));
    }

    [Fact]
    public void MigratesLegacyWithoutDuplicateLaunchAndRetainsDisabledPreference()
    {
        registry.Legacy = Command;
        registry.LegacyApproval = [3, 0, 0, 0, 1, 2, 3, 4, 0, 0, 0, 0];
        registry.OnLegacyWrite = () => Assert.False(File.Exists(Shortcut));
        var before = Startup.Capture(Name);
        var after = Startup.Prepare(Name, before, Command);
        Startup.ReplaceState(Name, before, after);
        Assert.Null(registry.Legacy);
        Assert.True(File.Exists(Shortcut));
        Assert.True(Startup.IsDisabled(Name));
        Assert.Equal(before.LegacyApproval, registry.ShortcutApproval);
        Startup.RestoreState(Name, before, after);
        Assert.False(File.Exists(Shortcut));
        Assert.Equal(Command, registry.Legacy);
        Assert.Null(registry.ShortcutApproval);
        Assert.Equal(before.LegacyApproval, registry.LegacyApproval);
    }

    [Fact]
    public void DisabledShortcutStaysDisabledAcrossRemoveReenableAndUninstallRollback()
    {
        registry.ShortcutApproval = [7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Startup.Replace(Name, null, Command);
        var before = Startup.Capture(Name);
        var after = Startup.Prepare(Name, before, null);
        Startup.ReplaceState(Name, before, after);
        Assert.Equal(before.ShortcutApproval, registry.ShortcutApproval);
        Startup.RestoreState(Name, before, after);
        Assert.True(Startup.IsDisabled(Name));
        Assert.Equal(before.Shortcut, File.ReadAllBytes(Shortcut));
        Startup.Replace(Name, Command, null);
        Startup.Replace(Name, null, Command);
        Assert.True(Startup.IsDisabled(Name));
        Assert.Equal(0, registry.ApprovalWrites);
    }

    [Fact]
    public void DisabledLegacyPreferenceSurvivesExplicitRemovalThenReenable()
    {
        registry.Legacy = Command;
        registry.LegacyApproval = [7, 0, 0, 0];
        Startup.Replace(Name, Command, null);
        Assert.Null(Startup.Read(Name));
        Assert.Null(registry.Legacy);
        Assert.False(File.Exists(Shortcut));
        Startup.Replace(Name, null, Command);
        Assert.True(Startup.IsDisabled(Name));
        Assert.Equal(registry.LegacyApproval, registry.ShortcutApproval);
    }

    [Fact]
    public void MigrationRecoveryHandlesCrashAfterRemovingLegacyBeforeWritingShortcut()
    {
        registry.Legacy = Command;
        registry.LegacyApproval = [3, 0, 0, 0];
        var before = Startup.Capture(Name);
        var after = Startup.Prepare(Name, before, Command);
        registry.Legacy = null;
        registry.ShortcutApproval = after.ShortcutApproval;
        Startup.RestoreState(Name, before, after);
        Assert.Equal(Command, registry.Legacy);
        Assert.Null(registry.ShortcutApproval);
        Assert.False(File.Exists(Shortcut));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerMigratesOrDisablesAndUninstallRollbackRestoresExactSavedPreference(bool enabled)
    {
        var (manager, config, relay) = CreateManager();
        registry.Legacy = Command;
        registry.LegacyApproval = [3, 0, 0, 0];
        await manager.ApplyAsync(MultiTargetIntegrationManager.Preview(config, Config, [], relay,
            startClientAtSignIn: enabled), default);
        Assert.Null(registry.Legacy);
        Assert.Equal(enabled, File.Exists(Shortcut));
        var saved = Startup.Capture(Name);
        var servicing = new IntegrationManager(new Scheduler(), (_, _) => Task.FromResult(true), Startup, new Runtime());
        servicing.PrepareUninstall(Config, "isolated-startup");
        Assert.Null(Startup.Read(Name));
        servicing.RollbackUninstall(Config, "isolated-startup");
        Assert.True(IntegrationStartup.Equivalent(saved, Startup.Capture(Name)));
        Assert.Equal(enabled, File.Exists(Shortcut));
        if (enabled) Assert.True(Startup.IsDisabled(Name));
        servicing.Uninstall(Config);
        Assert.Null(Startup.Read(Name));
    }

    [Fact]
    public async Task PersistedJournalRecoversLegacyMigrationWhenLogicalCommandDidNotChange()
    {
        var (manager, config, relay) = CreateManager();
        await manager.ApplyAsync(MultiTargetIntegrationManager.Preview(config, Config, [], relay,
            startClientAtSignIn: false), default);
        registry.Legacy = Command;
        registry.LegacyApproval = [3, 0, 0, 0];
        var before = Startup.Capture(Name);
        var after = Startup.Prepare(Name, before, Command);
        var manifestPath = Path.Combine(Path.GetDirectoryName(Config)!, "integration.json");
        var manifest = File.ReadAllBytes(manifestPath);
        var journal = new MultiTargetIntegrationJournal
        {
            ConfigPath = Config, Changes = [new(manifestPath, manifest, manifest)],
            TaskName = ScheduledTaskDefinition.Name(config.MachineId),
            StartupName = Name, StartupBefore = Command, StartupAfter = Command,
            StartupStateBefore = before, StartupStateAfter = after
        };
        var path = Path.Combine(Path.GetDirectoryName(Config)!, "integration-recovery.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
        registry.Legacy = null;
        registry.ShortcutApproval = after.ShortcutApproval;
        manager.RecoverPending(Config);
        Assert.False(File.Exists(path));
        Assert.True(IntegrationStartup.Equivalent(before, Startup.Capture(Name)));
        Assert.False(File.Exists(Shortcut));
    }

    [Fact]
    public async Task PreShortcutRecoveryJournalRestoresDisabledRunWithoutEnablingANewShortcut()
    {
        var (manager, config, relay) = CreateManager();
        await manager.ApplyAsync(MultiTargetIntegrationManager.Preview(config, Config, [], relay,
            startClientAtSignIn: false), default);
        registry.LegacyApproval = [3, 0, 0, 0];
        var manifestPath = Path.Combine(Path.GetDirectoryName(Config)!, "integration.json");
        var manifest = File.ReadAllBytes(manifestPath);
        var journal = new MultiTargetIntegrationJournal
        {
            ConfigPath = Config, Changes = [new(manifestPath, manifest, manifest)],
            TaskName = ScheduledTaskDefinition.Name(config.MachineId),
            StartupName = Name, StartupBefore = Command, StartupAfter = null
        };
        var path = Path.Combine(Path.GetDirectoryName(Config)!, "integration-recovery.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
        manager.RecoverPending(Config);
        Assert.False(File.Exists(path));
        Assert.Equal(Command, registry.Legacy);
        Assert.True(Startup.IsDisabled(Name));
        Assert.False(File.Exists(Shortcut));
        Assert.Null(registry.ShortcutApproval);
    }

    private (MultiTargetIntegrationManager Manager, RemoteConfiguration Config, string Relay) CreateManager()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Client)!);
        File.WriteAllBytes(Client, []);
        var relay = Path.Combine(Path.GetDirectoryName(Client)!, "AgentSignaler.Relay.exe");
        File.WriteAllBytes(relay, []);
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid() }.ToVersion3();
        return (new(new Scheduler(), (_, _) => Task.FromResult(true), Startup, new Runtime()), config, relay);
    }

    private sealed class Scheduler : IIntegrationTaskScheduler
    {
        public string? ReadXml(string name) => null;
        public void Write(string name, string xml) => throw new InvalidOperationException("Unexpected task write.");
        public void Delete(string name) => throw new InvalidOperationException("Unexpected task deletion.");
    }

    private sealed class Runtime : IIntegrationRuntime
    {
        public Task<IntegrationRuntimeState> QueryAsync(string config, CancellationToken token) =>
            Task.FromResult(new IntegrationRuntimeState(false));
        public Task<IntegrationRuntimeState> StartAsync(string client, string config, CancellationToken token) =>
            throw new InvalidOperationException("A startup preference must not launch Client.");
        public Task<IntegrationRuntimeState> ReloadAsync(string config, CancellationToken token) =>
            throw new InvalidOperationException("A stopped Client must not be reloaded.");
        public Task StopAsync(string config, CancellationToken token) => Task.CompletedTask;
    }

    [Fact]
    public void OwnedDuplicateLegacyIsRemovedWithoutRewritingShortcut()
    {
        Startup.Replace(Name, null, Command);
        var bytes = File.ReadAllBytes(Shortcut);
        registry.Legacy = Command;
        Startup.Replace(Name, Command, Command);
        Assert.Equal(bytes, File.ReadAllBytes(Shortcut));
        Assert.Null(registry.Legacy);
    }

    [Theory]
    [InlineData("other.exe --background")]
    [InlineData("\"C:\\other\\AgentSignaler.Client.exe\" --background --config \"C:\\other\\remote.json\"")]
    public void ForeignRunEntryIsPreserved(string foreign)
    {
        registry.Legacy = foreign;
        Assert.Throws<InvalidDataException>(() => Startup.Replace(Name, null, Command));
        Assert.Equal(foreign, registry.Legacy);
        Assert.False(File.Exists(Shortcut));
    }

    [Fact]
    public void ForeignShortcutAndConflictingOwnedShortcutArePreserved()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Shortcut)!);
        byte[] foreign = [1, 2, 3, 4];
        File.WriteAllBytes(Shortcut, foreign);
        Assert.Throws<InvalidDataException>(() => Startup.Replace(Name, null, Command));
        Assert.Equal(foreign, File.ReadAllBytes(Shortcut));
        var otherCommand = IntegrationStartup.Command(Path.Combine(root, "other", "AgentSignaler.Client.exe"), Config);
        var other = WindowsStartupShortcut.Create(Name, otherCommand);
        File.WriteAllBytes(Shortcut, other);
        registry.Legacy = Command;
        Assert.Throws<InvalidDataException>(() => Startup.Replace(Name, Command, null));
        Assert.Equal(other, File.ReadAllBytes(Shortcut));
        Assert.Equal(Command, registry.Legacy);
    }

    [Fact]
    public void ConcurrentWindowsPreferenceChangeBlocksApplyAndRecovery()
    {
        Startup.Replace(Name, null, Command);
        var before = Startup.Capture(Name);
        var after = Startup.Prepare(Name, before, null);
        registry.ShortcutApproval = [3, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => Startup.ReplaceState(Name, before, after));
        Assert.Throws<InvalidDataException>(() => Startup.RestoreState(Name, before, after));
        Assert.True(File.Exists(Shortcut));
        Assert.Equal(new byte[] { 3, 0, 0, 0 }, registry.ShortcutApproval);
    }

    [Theory]
    [InlineData("..\\outside")]
    [InlineData("AgentSignaler")]
    [InlineData("AgentSignaler-Client-XXXXXXXXXXXXXXXXXXXXXXXX")]
    public void RejectsNamesOutsideOwnedNamespace(string name)
    {
        Assert.Throws<InvalidDataException>(() => Startup.Read(name));
        Assert.False(Directory.Exists(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    internal sealed class StartupRegistry : IStartupRegistry
    {
        public string? Legacy;
        public byte[]? LegacyApproval;
        public byte[]? ShortcutApproval;
        public int LegacyWrites;
        public int ApprovalWrites;
        public Action? OnLegacyWrite;
        public string? ReadLegacy(string name) => Legacy;
        public byte[]? ReadApproval(string name, bool legacy) => legacy ? LegacyApproval : ShortcutApproval;
        public void ReplaceLegacy(string name, string? expected, string? command)
        {
            Assert.Equal(expected, Legacy);
            OnLegacyWrite?.Invoke();
            Legacy = command;
            LegacyWrites++;
        }
        public void ReplaceApproval(string name, byte[]? expected, byte[]? value)
        {
            Assert.Equal(expected, ShortcutApproval);
            ShortcutApproval = value;
            ApprovalWrites++;
        }
    }
}
