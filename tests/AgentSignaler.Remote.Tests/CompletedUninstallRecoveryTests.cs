using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class CompletedUninstallRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly Guid machineId = Guid.NewGuid();
    private readonly Scheduler scheduler = new();
    private readonly Startup startup = new();
    private readonly Runtime runtime = new();
    private readonly Installer installer = new();
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string ManifestPath => Path.Combine(root, "integration.json");
    private string RelayPath => Path.Combine(root, "AgentSignaler.Relay.exe");
    private string HookPath => Path.Combine(root, "cli", "hooks", "agent-signaler.json");
    private string JournalPath => IntegrationManager.RemovalJournalPath(ConfigPath, "fixture");
    private IntegrationManager Manager => new(scheduler, (_, _) => throw new InvalidOperationException("No delivery during recovery."),
        startup, runtime, installer);
    private RemoteConfiguration Configuration => new RemoteConfiguration { Host = "localhost", MachineId = machineId }
        .ToVersion5().WithDashboardUrl("https://synthetic.example/") with { DetailedReportingEnabled = false };

    public CompletedUninstallRecoveryTests()
    {
        Directory.CreateDirectory(root);
        AtomicFile.Write(RelayPath, []);
        AtomicFile.Write(Path.Combine(root, "AgentSignaler.Client.exe"), []);
    }

    private async Task Prepare(bool legacy = false, string? transactionId = "fixture")
    {
        if (legacy)
        {
            var config = new RemoteConfiguration { Host = "localhost", MachineId = machineId };
            await new IntegrationManager(scheduler, (_, _) => Task.FromResult(true), startup, runtime, installer)
                .ApplyAsync(IntegrationManager.Preview(config, ConfigPath, Path.Combine(root, "cli"), RelayPath), default);
        }
        else
        {
            var target = new IntegrationTarget
            {
                Id = "cli", Kind = "copilot-cli", DisplayName = "CLI", InstallationId = "cli",
                HostVersion = "1.0.83", ScopeId = "isolated", HookDirectory = Path.GetDirectoryName(HookPath)!,
                Capability = IntegrationCapability.Verified, SupportedEvents = HookAdapters.Events("copilot-cli")
            };
            await new MultiTargetIntegrationManager(scheduler, (_, _) => Task.FromResult(true), startup, runtime)
                .ApplyAsync(MultiTargetIntegrationManager.Preview(Configuration, ConfigPath, [target], RelayPath), default);
        }
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        scheduler.Value = new XElement(ns + "Task",
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", ScheduledTaskDefinition.Owner(machineId))),
            new XElement(ns + "Actions", new XElement(ns + "Exec", new XElement(ns + "Command", RelayPath),
                new XElement(ns + "Arguments", "heartbeat --config " + ScheduledTaskDefinition.QuoteArgument(ConfigPath))))).ToString();
        Manager.PrepareUninstall(ConfigPath, transactionId);
        scheduler.ForbidMutations = startup.ForbidMutations = runtime.ForbidCalls = true;
    }

    private MultiTargetIntegrationJournal Journal() =>
        JsonSerializer.Deserialize<MultiTargetIntegrationJournal>(File.ReadAllBytes(JournalPath), Protocol.Json)!;

    private void Rewrite(MultiTargetIntegrationJournal journal) =>
        AtomicFile.Write(JournalPath, JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));

    private Dictionary<string, byte[]> Files() => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => p, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private void AssertFilesUnchanged(Dictionary<string, byte[]> before)
    {
        var after = Files();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("fixture")]
    public async Task CompletedV2RequiresExplicitApprovalAndOnlyClearsBackedUpMetadata(string? transactionId)
    {
        await Prepare(transactionId: transactionId);
        var path = IntegrationManager.RemovalJournalPath(ConfigPath, transactionId);
        var before = Files();
        Assert.True(IntegrationManager.HasPendingUninstall(ConfigPath));
        var blocked = Assert.Throws<InvalidOperationException>(() => MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [], RelayPath));
        Assert.Contains("Review & maintenance > Complete finished uninstall", blocked.Message);
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        Assert.Equal(1, plan.Count);
        Assert.Contains("Confirm that Windows Installer has finished successfully", plan.Preview);
        Assert.DoesNotContain("synthetic.example", plan.Preview);
        AssertFilesUnchanged(before);
        Assert.Throws<InvalidOperationException>(() => MultiTargetIntegrationManager.Preview(
            RemoteConfiguration.Load(ConfigPath), ConfigPath, [], RelayPath));

        // Servicing has removed binaries; metadata finalization must not require or launch them.
        File.Delete(RelayPath);
        File.Delete(Path.Combine(root, "AgentSignaler.Client.exe"));
        before = Files();
        Manager.CompleteUninstallRecovery(plan);
        var after = Files();
        var backup = Assert.Single(after.Keys.Except(before.Keys));
        Assert.Equal(plan.BackupPath, backup);
        Assert.Equal(before[path], after[backup]);
        Assert.False(File.Exists(path));
        foreach (var (file, bytes) in before.Where(p => p.Key != path)) Assert.Equal(bytes, after[file]);
        Assert.Equal(before.Count, after.Count);
        Assert.False(IntegrationManager.HasPendingUninstall(ConfigPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        Assert.False(File.Exists(ManifestPath));
        Assert.False(File.Exists(HookPath));
        Assert.NotNull(MultiTargetIntegrationManager.Preview(RemoteConfiguration.Load(ConfigPath), ConfigPath, [], RelayPath));
        Assert.Throws<InvalidOperationException>(() => Manager.CompleteUninstallRecovery(plan));
    }

    [Theory]
    [InlineData("integration-recovery.json")]
    [InlineData("heartbeat-migration-fixture.json")]
    [InlineData(@"hook-verification\probe-fixture.json")]
    public void OtherPendingRecoveryWithoutUninstallKeepsItsExistingBlocker(string relative)
    {
        AtomicFile.Write(Path.Combine(root, relative), "{}"u8);
        var before = Files();
        var error = Assert.Throws<InvalidOperationException>(() =>
            MultiTargetIntegrationManager.Preview(Configuration, ConfigPath, [], RelayPath));
        Assert.DoesNotContain("Complete finished uninstall", error.Message);
        Assert.Contains("Integration recovery or installer servicing is pending", error.Message);
        AssertFilesUnchanged(before);
    }

    [Fact]
    public void PreCancelledPreviewDoesNotCreateTheIntegrationLock()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = Files();
        Assert.Throws<OperationCanceledException>(() =>
            Manager.PreviewCompletedUninstallRecovery(ConfigPath, cancellation.Token));
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledCompletionPreservesAllRecoveryMaterial(bool legacy)
    {
        await Prepare(legacy);
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var before = Files();
        Assert.Throws<OperationCanceledException>(() => Manager.CompleteUninstallRecovery(plan, cancellation.Token));
        AssertFilesUnchanged(before);
        Assert.False(File.Exists(plan.BackupPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewObservesCancellationAfterRegistrationInspection(bool legacy)
    {
        await Prepare(legacy);
        using var cancellation = new CancellationTokenSource();
        scheduler.OnRead = cancellation.Cancel;
        var before = Files();
        Assert.Throws<OperationCanceledException>(() =>
            Manager.PreviewCompletedUninstallRecovery(ConfigPath, cancellation.Token));
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CancellationBeforeOrAfterBackupNeverDeletesTheJournal(int checkpoint)
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        installer.Check = () =>
        {
            if (++calls == checkpoint) cancellation.Cancel();
            return false;
        };
        var before = Files();
        Assert.Throws<OperationCanceledException>(() => Manager.CompleteUninstallRecovery(plan, cancellation.Token));
        var after = Files();
        if (checkpoint > 1)
        {
            Assert.Equal(before[JournalPath], after[plan.BackupPath]);
            after.Remove(plan.BackupPath);
        }
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("renamedJournal")]
    [InlineData("config")]
    [InlineData("hook")]
    [InlineData("manifest")]
    [InlineData("task")]
    public async Task StaleApprovalAndIncompleteAfterStatesAreRejectedWithoutRestoring(string change)
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        var journal = Journal();
        switch (change)
        {
            case "journal": File.AppendAllText(JournalPath, " "); break;
            case "renamedJournal": File.Move(JournalPath, IntegrationManager.RemovalJournalPath(ConfigPath)); break;
            case "config": File.AppendAllText(ConfigPath, " "); break;
            case "hook": AtomicFile.Write(HookPath, journal.Changes.Single(c => c.Path == HookPath).Before!); break;
            case "manifest": AtomicFile.Write(ManifestPath, journal.Changes.Single(c => c.Path == ManifestPath).Before!); break;
            case "task": scheduler.Value = journal.TaskBefore; break;
        }
        var before = Files();
        Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
        AssertFilesUnchanged(before);
        if (change is not ("journal" or "renamedJournal"))
            Assert.Throws<InvalidDataException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("shortcut")]
    [InlineData("legacy")]
    [InlineData("shortcutApproval")]
    [InlineData("legacyApproval")]
    public async Task AllStartupAfterStateFieldsAreRequiredAtPreviewAndCompletion(string field)
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        startup.State = field switch
        {
            "command" => startup.State with { Command = "changed" },
            "shortcut" => startup.State with { Shortcut = [1] },
            "legacy" => startup.State with { LegacyCommand = "changed" },
            "shortcutApproval" => startup.State with { ShortcutApproval = [2, 0, 0, 0] },
            _ => startup.State with { LegacyApproval = [2, 0, 0, 0] }
        };
        var before = Files();
        Assert.Throws<InvalidDataException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData("integration-recovery.json")]
    [InlineData("heartbeat-migration-fixture.json")]
    [InlineData(@"hook-verification\probe-fixture.json")]
    public async Task OtherPendingRecoveryAppearingAfterApprovalBlocksCompletion(string relative)
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        AtomicFile.Write(Path.Combine(root, relative), "{}"u8);
        var before = Files();
        Assert.Throws<InvalidOperationException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.Throws<InvalidOperationException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task MultipleJournalsAreExplicitlyBlockedIncludingOnStaleApproval()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        AtomicFile.Write(IntegrationManager.RemovalJournalPath(ConfigPath), File.ReadAllBytes(JournalPath));
        var before = Files();
        Assert.Contains("Multiple uninstall journals", Assert.Throws<InvalidOperationException>(
            () => Manager.CompleteUninstallRecovery(plan)).Message);
        Assert.Throws<InvalidOperationException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task ActiveOrUnavailableInstallerCheckPreservesEverything()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        var before = Files();
        installer.Check = () => true;
        Assert.Contains("Windows Installer is active", Assert.Throws<InvalidOperationException>(
            () => Manager.CompleteUninstallRecovery(plan)).Message);
        Assert.Throws<InvalidOperationException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        installer.Check = () => throw new UnauthorizedAccessException("Synthetic installer status access failure.");
        Assert.Throws<UnauthorizedAccessException>(() => Manager.CompleteUninstallRecovery(plan));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task InstallerStartingAtFinalCheckLeavesTheOriginalAndExactBackup()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        var original = File.ReadAllBytes(JournalPath);
        var calls = 0;
        installer.Check = () => ++calls == 3;
        Assert.Throws<InvalidOperationException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.Equal(original, File.ReadAllBytes(JournalPath));
        Assert.Equal(original, File.ReadAllBytes(plan.BackupPath));
    }

    [Fact]
    public async Task JournalEditedDuringStateInspectionIsNotDeleted()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        scheduler.OnRead = () =>
        {
            scheduler.OnRead = null;
            File.AppendAllText(JournalPath, " ");
        };
        Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.True(File.Exists(JournalPath));
        Assert.False(File.Exists(plan.BackupPath));
    }

    [Fact]
    public async Task PendingRecoveryAppearingAtFinalInstallerCheckIsPreserved()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        var calls = 0;
        installer.Check = () =>
        {
            if (++calls == 2) AtomicFile.Write(Path.Combine(root, "integration-recovery.json"), "{}"u8);
            return false;
        };
        Assert.Throws<InvalidOperationException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.True(File.Exists(JournalPath));
        Assert.Equal(File.ReadAllBytes(JournalPath), File.ReadAllBytes(plan.BackupPath));
    }

    [Fact]
    public async Task ConfigEditedDuringFinalRegistrationInspectionRetainsJournalAndBackup()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        var reads = 0;
        scheduler.OnRead = () =>
        {
            if (++reads == 2) File.AppendAllText(ConfigPath, " ");
        };
        Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.True(File.Exists(JournalPath));
        Assert.Equal(File.ReadAllBytes(JournalPath), File.ReadAllBytes(plan.BackupPath));
        Assert.False(RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
    }

    [Fact]
    public async Task SharedIntegrationLockBlocksCompletion()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        using (AtomicFile.Acquire(ConfigPath + ".integration.lock", TimeSpan.Zero))
            Assert.Throws<IOException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.True(File.Exists(JournalPath));
        Assert.False(File.Exists(plan.BackupPath));
    }

    [Fact]
    public async Task FailedBackupOrDeletionRetainsRecoveryMaterial()
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        Directory.CreateDirectory(plan.BackupPath);
        Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.True(File.Exists(JournalPath));
        Directory.Delete(plan.BackupPath);
        using (var pinned = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.Equal(File.ReadAllBytes(JournalPath), File.ReadAllBytes(plan.BackupPath));
    }

    [Theory]
    [InlineData("integration-uninstall-bad.json")]
    [InlineData("integration-uninstall.json.json")]
    [InlineData("integration-uninstall-000000000000000000000000000000000000000000000000000000000000000g.json")]
    [InlineData("integration-uninstall-000000000000000000000000000000000000000000000000000000000000000.json")]
    public async Task MalformedFilenamesAreDetectedButNeverAcknowledged(string name)
    {
        await Prepare();
        File.Move(JournalPath, Path.Combine(root, name));
        var before = Files();
        Assert.True(IntegrationManager.HasPendingUninstall(ConfigPath));
        Assert.Throws<InvalidDataException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData("removal")]
    [InlineData("configIdentity")]
    [InlineData("manifestIdentity")]
    [InlineData("missingConfig")]
    [InlineData("missingHook")]
    [InlineData("startupSnapshots")]
    [InlineData("startupName")]
    [InlineData("taskName")]
    [InlineData("nullChanges")]
    [InlineData("unknownField")]
    [InlineData("duplicateField")]
    [InlineData("manifestNullArtifacts")]
    [InlineData("changedPreservedConfig")]
    [InlineData("configurationIdentityBytes")]
    [InlineData("foreignPath")]
    [InlineData("alternateStream")]
    [InlineData("aliasedPath")]
    [InlineData("networkPath")]
    [InlineData("taskAfter")]
    [InlineData("hookAfter")]
    [InlineData("version")]
    public async Task MalformedOrUnownedJournalIsPreservedWithExpectedFailure(string invalid)
    {
        await Prepare();
        var journal = Journal();
        switch (invalid)
        {
            case "removal": Rewrite(journal with { Removal = false }); break;
            case "configIdentity": Rewrite(journal with { ConfigPath = Path.Combine(root, "other.json") }); break;
            case "missingConfig": Rewrite(journal with { Changes = journal.Changes.Where(c => c.Path != ConfigPath).ToArray() }); break;
            case "missingHook": Rewrite(journal with { Changes = journal.Changes.Where(c => c.Path != HookPath).ToArray() }); break;
            case "startupSnapshots": Rewrite(journal with { StartupStateBefore = null, StartupStateAfter = null }); break;
            case "startupName": Rewrite(journal with { StartupName = "foreign" }); break;
            case "taskName": Rewrite(journal with { TaskName = "foreign" }); break;
            case "taskAfter": Rewrite(journal with { TaskAfter = journal.TaskBefore }); break;
            case "version": Rewrite(journal with { Version = 1 }); break;
            case "nullChanges": Rewrite(journal with { Changes = null! }); break;
            case "hookAfter":
                Rewrite(journal with { Changes = journal.Changes.Select(c => c.Path == HookPath
                    ? c with { After = c.Before } : c).ToArray() });
                break;
            case "configurationIdentityBytes":
                var foreignConfig = JsonSerializer.SerializeToUtf8Bytes(Configuration with { MachineId = Guid.NewGuid() }, Protocol.Json);
                Rewrite(journal with { Changes = journal.Changes.Select(c => c.Path == ConfigPath
                    ? c with { Before = foreignConfig, After = foreignConfig } : c).ToArray() });
                break;
            case "foreignPath":
            case "alternateStream":
            case "aliasedPath":
            case "networkPath":
                var foreignPath = invalid switch
                {
                    "alternateStream" => HookPath + ":foreign",
                    "aliasedPath" => Path.Combine(root, "SHORT~1", "agent-signaler.json"),
                    "networkPath" => @"\\synthetic-host\share\agent-signaler.json",
                    _ => Path.Combine(root, "foreign", "agent-signaler.json")
                };
                Rewrite(journal with { Changes = journal.Changes.Select(c => c.Path == HookPath
                    ? c with { Path = foreignPath } : c).ToArray() });
                break;
            case "changedPreservedConfig":
                Rewrite(journal with
                {
                    Changes = journal.Changes.Select(c => c.Path == ConfigPath
                        ? c with { After = JsonSerializer.SerializeToUtf8Bytes(Configuration with { DetailedReportingEnabled = true }, Protocol.Json) }
                        : c).ToArray()
                });
                break;
            case "unknownField":
            case "duplicateField":
                var text = File.ReadAllText(JournalPath);
                AtomicFile.Write(JournalPath, Encoding.UTF8.GetBytes(text[..^1] +
                    (invalid == "unknownField" ? ",\"unknown\":true}" : ",\"removal\":true}")));
                break;
            default:
                var change = journal.Changes.Single(c => c.Path == ManifestPath);
                var manifest = JsonSerializer.Deserialize<IntegrationManifest>(change.Before!, Protocol.Json)!;
                manifest = invalid == "manifestIdentity" ? manifest with { MachineId = Guid.Empty } : manifest with { Artifacts = null! };
                Rewrite(journal with { Changes = journal.Changes.Select(c => c == change
                    ? c with { Before = JsonSerializer.SerializeToUtf8Bytes(manifest, Protocol.Json) } : c).ToArray() });
                break;
        }
        var before = Files();
        var error = Record.Exception(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        Assert.NotNull(error);
        Assert.True(RemoteFailure.IsExpected(error), error.GetType().Name);
        AssertFilesUnchanged(before);
    }

    [Theory]
    [InlineData(16777216, true)]
    [InlineData(16777217, false)]
    public async Task SingleJournalAggregateByteLimitIsExact(int size, bool allowed)
    {
        await Prepare();
        var bytes = File.ReadAllBytes(JournalPath);
        var padded = new byte[size];
        padded.AsSpan().Fill((byte)' ');
        bytes.CopyTo(padded, 0);
        AtomicFile.Write(JournalPath, padded);
        if (allowed)
        {
            var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
            Manager.CompleteUninstallRecovery(plan);
            Assert.Equal(padded, File.ReadAllBytes(plan.BackupPath));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
            Assert.Equal(padded, File.ReadAllBytes(JournalPath));
        }
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task ArtifactAndFileSnapshotCountLimitsAreExact(int count, bool allowed)
    {
        await Prepare();
        var journal = Journal();
        var manifestChange = journal.Changes.Single(c => c.Path == ManifestPath);
        var manifest = JsonSerializer.Deserialize<IntegrationManifest>(manifestChange.Before!, Protocol.Json)!;
        var extra = Enumerable.Range(1, count - 1).Select(i => new IntegrationArtifact
        {
            Path = Path.Combine(root, "extra-" + i, "agent-signaler.json"),
            Hash = manifest.Artifacts[0].Hash
        }).ToArray();
        manifest = manifest with { Artifacts = [.. manifest.Artifacts, .. extra] };
        Rewrite(journal with
        {
            Changes = [.. journal.Changes.Select(c => c == manifestChange
                ? c with { Before = JsonSerializer.SerializeToUtf8Bytes(manifest, Protocol.Json) } : c),
                .. extra.Select(a => new IntegrationFileChange(a.Path, null, null))]
        });
        if (allowed)
        {
            var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
            Manager.CompleteUninstallRecovery(plan);
            Assert.False(File.Exists(JournalPath));
        }
        else
        {
            var before = Files();
            Assert.Throws<InvalidDataException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
            AssertFilesUnchanged(before);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsAfterStateIsPreservedAndConcurrentEditsAreRejected(bool edit)
    {
        await Prepare();
        var journal = Journal();
        var manifestChange = journal.Changes.Single(c => c.Path == ManifestPath);
        var manifest = JsonSerializer.Deserialize<IntegrationManifest>(manifestChange.Before!, Protocol.Json)!;
        var settingsPath = Path.Combine(root, "profile", "settings.json");
        var original = "// Keep this user's unrelated setting.\n{\"editor.fontSize\": 16}"u8.ToArray();
        var location = Path.GetDirectoryName(HookPath)!;
        var installed = JsoncHookSettings.AddLocation(original, location);
        var artifact = new IntegrationArtifact
        {
            Kind = "settings", Path = settingsPath, Hash = MultiTargetIntegrationManager.Hash(installed),
            Locations = [location], OriginalBytes = original
        };
        manifest = manifest with { Artifacts = [.. manifest.Artifacts, artifact] };
        Rewrite(journal with
        {
            Changes = [.. journal.Changes.Select(c => c == manifestChange
                ? c with { Before = JsonSerializer.SerializeToUtf8Bytes(manifest, Protocol.Json) } : c),
                new(settingsPath, installed, original)]
        });
        AtomicFile.Write(settingsPath, original);
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        if (edit)
        {
            File.AppendAllText(settingsPath, " ");
            var before = Files();
            Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
            AssertFilesUnchanged(before);
        }
        else
        {
            Manager.CompleteUninstallRecovery(plan);
            Assert.Equal(original, File.ReadAllBytes(settingsPath));
            Assert.False(File.Exists(HookPath));
        }
    }

    [Theory]
    [InlineData("hook")]
    [InlineData("journal")]
    [InlineData("lock")]
    public async Task ReparsePathsAreRejectedBeforeReadingOrClearingMetadata(string kind)
    {
        await Prepare();
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        var physical = Path.Combine(root, "physical");
        var junction = kind switch
        {
            "hook" => Path.GetDirectoryName(HookPath)!,
            "journal" => JournalPath,
            _ => ConfigPath + ".integration.lock"
        };
        if (kind == "hook") Directory.Move(junction, physical);
        else
        {
            Directory.CreateDirectory(physical);
            File.Move(junction, Path.Combine(physical, "original"));
        }
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{physical}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Contains("Reparse-point", Assert.Throws<InvalidDataException>(
                () => Manager.CompleteUninstallRecovery(plan)).Message);
            Assert.Contains("Reparse-point", Assert.Throws<InvalidDataException>(
                () => Manager.PreviewCompletedUninstallRecovery(ConfigPath)).Message);
            Assert.False(File.Exists(plan.BackupPath));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public async Task LegacyWithExactConfigHashAndFullStartupSnapshotsCanBeAcknowledged()
    {
        await Prepare(legacy: true);
        var before = File.ReadAllBytes(ConfigPath);
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        Manager.CompleteUninstallRecovery(plan);
        Assert.False(File.Exists(JournalPath));
        Assert.Equal(before, File.ReadAllBytes(ConfigPath));
        Assert.False(File.Exists(HookPath));
        Assert.False(File.Exists(ManifestPath));
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("config")]
    [InlineData("hook")]
    [InlineData("task")]
    public async Task AmbiguousLegacyJournalIsRetained(string state)
    {
        await Prepare(legacy: true);
        var journal = JsonSerializer.Deserialize<IntegrationRemovalJournal>(File.ReadAllBytes(JournalPath), Protocol.Json)!;
        switch (state)
        {
            case "startup":
                AtomicFile.Write(JournalPath, JsonSerializer.SerializeToUtf8Bytes(journal with
                    { StartupStateBefore = null, StartupStateAfter = null }, Protocol.Json));
                break;
            case "config": File.AppendAllText(ConfigPath, " "); break;
            case "hook": AtomicFile.Write(HookPath, journal.HookBytes!); break;
            case "task": scheduler.Value = journal.TaskXml; break;
        }
        var before = Files();
        Assert.Throws<InvalidDataException>(() => Manager.PreviewCompletedUninstallRecovery(ConfigPath));
        AssertFilesUnchanged(before);
    }

    [Fact]
    public async Task LegacyConfigurationChangeAfterApprovalCannotBeFinalized()
    {
        await Prepare(legacy: true);
        var plan = Manager.PreviewCompletedUninstallRecovery(ConfigPath);
        File.AppendAllText(ConfigPath, " ");
        Assert.Throws<InvalidDataException>(() => Manager.CompleteUninstallRecovery(plan));
        Assert.True(File.Exists(JournalPath));
    }

    [Fact]
    public async Task InstallerMutexProbeUsesAnIsolatedMutexWithoutStartingServices()
    {
        var name = @"Local\AgentSignaler-Test-Installer-" + Guid.NewGuid().ToString("N");
        Assert.False(WindowsIntegrationInstallerActivity.IsMutexActive(name));
        using var mutex = new Mutex(false, name);
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = Task.Run(() =>
        {
            mutex.WaitOne();
            try { acquired.Set(); release.Wait(); }
            finally { mutex.ReleaseMutex(); }
        });
        try
        {
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(WindowsIntegrationInstallerActivity.IsMutexActive(name));
        }
        finally { release.Set(); await owner; }
        Assert.False(WindowsIntegrationInstallerActivity.IsMutexActive(name));
    }

    public void Dispose() => Directory.Delete(root, true);

    private sealed class Installer : IIntegrationInstallerActivity
    {
        public Func<bool> Check = () => false;
        public bool IsActive() => Check();
    }

    private sealed class Scheduler : IIntegrationTaskScheduler
    {
        public string? Value;
        public bool ForbidMutations;
        public Action? OnRead;
        public string? ReadXml(string name) { OnRead?.Invoke(); return Value; }
        public void Write(string name, string xml)
        {
            if (ForbidMutations) throw new InvalidOperationException("Recovery cannot write tasks.");
            Value = xml;
        }
        public void Delete(string name)
        {
            if (ForbidMutations) throw new InvalidOperationException("Recovery cannot delete tasks.");
            Value = null;
        }
    }

    private sealed class Startup : IIntegrationStartup
    {
        public IntegrationStartupState State = new(null, ShortcutApproval: [3, 0, 0, 0], LegacyApproval: [3, 0, 0, 0]);
        public bool ForbidMutations;
        public string? Read(string name) => State.Command;
        public bool IsDisabled(string name) => true;
        public IntegrationStartupState Capture(string name) => State;
        public IntegrationStartupState Prepare(string name, IntegrationStartupState before, string? command) =>
            before with { Command = command, Shortcut = command is null ? null : [1, 2, 3], LegacyCommand = null };
        public void Replace(string name, string? expected, string? command) =>
            ReplaceState(name, State, Prepare(name, State, command));
        public void ReplaceState(string name, IntegrationStartupState before, IntegrationStartupState after)
        {
            if (ForbidMutations) throw new InvalidOperationException("Recovery cannot change startup.");
            Assert.True(IntegrationStartup.Equivalent(State, before));
            State = after;
        }
    }

    private sealed class Runtime : IIntegrationRuntime
    {
        public bool ForbidCalls;
        private IntegrationRuntimeState State(string? config = null)
        {
            if (ForbidCalls) throw new InvalidOperationException("Recovery cannot contact Client.");
            return new(config is not null, config is null ? null : ClientConfigurationRevision.Read(config));
        }
        public Task<IntegrationRuntimeState> QueryAsync(string configPath, CancellationToken token) => Task.FromResult(State());
        public Task<IntegrationRuntimeState> StartAsync(string clientPath, string configPath, CancellationToken token) => Task.FromResult(State(configPath));
        public Task<IntegrationRuntimeState> ReloadAsync(string configPath, CancellationToken token) => Task.FromResult(State(configPath));
        public Task StopAsync(string configPath, CancellationToken token) { _ = State(); return Task.CompletedTask; }
        public Task SuspendTranscriptAsync(string configPath, CancellationToken token) { _ = State(); return Task.CompletedTask; }
    }
}
