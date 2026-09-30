using System.Collections.Immutable;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed class CompletedUninstallRecoveryPlan
{
    public int Count => 1;
    public string Preview { get; }
    internal string ConfigPath { get; }
    internal string JournalPath { get; }
    internal string BackupPath { get; }
    internal ImmutableArray<byte> JournalBytes { get; }
    internal string JournalHash { get; }

    internal CompletedUninstallRecoveryPlan(string configPath, string journalPath, byte[] bytes)
    {
        ConfigPath = configPath;
        JournalPath = journalPath;
        JournalBytes = ImmutableArray.CreateRange(bytes);
        JournalHash = MultiTargetIntegrationManager.Hash(bytes);
        BackupPath = journalPath + $".agent-signaler.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.{Guid.NewGuid():N}.backup";
        Preview = $"FINALIZE 1 completed uninstall journal: {journalPath}\nBACK UP exact journal to: {BackupPath}\n" +
            "Confirm that Windows Installer has finished successfully and no installer rollback is pending before continuing.\n" +
            "This explicitly acknowledges removal; it does not restore the integration. " +
            "Only the journal is removed after its exact completed state is rechecked. " +
            "Configuration (including detailed reporting opt-out), hooks, tasks and complete startup state are left unchanged. " +
            "Client is not contacted, started or stopped. Any ambiguity retains the journal.";
    }
}

public sealed partial class IntegrationManager
{
    internal const int MaxCompletedUninstallJournalBytes = 16777216;
    internal const int MaxCompletedUninstallJournals = 1;
    private readonly IIntegrationInstallerActivity installerActivity = new WindowsIntegrationInstallerActivity();

    public IntegrationManager(IIntegrationTaskScheduler scheduler,
        Func<RemoteConfiguration, CancellationToken, Task<bool>> verifyDelivery,
        IIntegrationStartup? startup, IIntegrationRuntime? runtime, IIntegrationInstallerActivity installerActivity)
        : this(scheduler, verifyDelivery, startup, runtime)
    {
        this.installerActivity = installerActivity ?? throw new ArgumentNullException(nameof(installerActivity));
    }

    public static bool HasPendingUninstall(string configPath)
    {
        configPath = RecoveryConfigPath(configPath);
        // Include unsupported candidates so the UI can surface blocked metadata, never silently ignore it.
        return UninstallCandidates(configPath).Any();
    }

    public CompletedUninstallRecoveryPlan PreviewCompletedUninstallRecovery(string configPath, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        configPath = RecoveryConfigPath(configPath);
        using var held = AtomicFile.Acquire(RecoveryLockPath(configPath), TimeSpan.FromSeconds(1));
        token.ThrowIfCancellationRequested();
        var path = SingleUninstallJournal(configPath);
        var bytes = AtomicFile.ReadBounded(path, MaxCompletedUninstallJournalBytes);
        ValidateCompletedUninstall(configPath, bytes, token);
        EnsureInstallerInactive(token);
        return new(configPath, path, bytes);
    }

    // Calling this API represents explicit approval of Preview, including confirmation that the installer finished.
    public void CompleteUninstallRecovery(CompletedUninstallRecoveryPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        token.ThrowIfCancellationRequested();
        var configPath = RecoveryConfigPath(plan.ConfigPath);
        using var held = AtomicFile.Acquire(RecoveryLockPath(configPath), TimeSpan.FromSeconds(1));
        EnsureInstallerInactive(token);
        ValidateApprovedUninstall(plan, token);
        var backupPath = MultiTargetIntegrationManager.Canonical(plan.BackupPath);
        if (Path.Exists(backupPath))
            throw new InvalidDataException("The recovery backup destination is occupied. Create a fresh preview; nothing was removed.");
        token.ThrowIfCancellationRequested();
        AtomicFile.Write(backupPath, plan.JournalBytes.AsSpan());
        if (!AtomicFile.ReadBounded(backupPath, MaxCompletedUninstallJournalBytes).AsSpan().SequenceEqual(plan.JournalBytes.AsSpan()))
            throw new IOException("The uninstall journal backup could not be verified; the journal was retained.");
        EnsureInstallerInactive(token);
        ValidateApprovedUninstall(plan, token);
        EnsureInstallerInactive(token);
        // Exactly one journal is supported: a failed deletion cannot partially finalize a set of transactions.
        token.ThrowIfCancellationRequested();
        File.Delete(plan.JournalPath);
    }

    private void ValidateApprovedUninstall(CompletedUninstallRecoveryPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = SingleUninstallJournal(plan.ConfigPath);
        var bytes = AtomicFile.ReadBounded(path, MaxCompletedUninstallJournalBytes);
        if (!SamePath(path, plan.JournalPath) || Hash(bytes) != plan.JournalHash ||
            !bytes.AsSpan().SequenceEqual(plan.JournalBytes.AsSpan()))
            throw new InvalidDataException("The uninstall journal changed since approval. Create a fresh recovery preview; nothing was removed.");
        ValidateCompletedUninstall(plan.ConfigPath, bytes, token);
        if (!SamePath(SingleUninstallJournal(plan.ConfigPath), plan.JournalPath) ||
            !AtomicFile.ReadBounded(plan.JournalPath, MaxCompletedUninstallJournalBytes).AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Uninstall recovery changed during inspection. Create a fresh preview; the journal was retained.");
        token.ThrowIfCancellationRequested();
    }

    private void EnsureInstallerInactive(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var active = installerActivity.IsActive();
        token.ThrowIfCancellationRequested();
        if (active)
            throw new InvalidOperationException("Windows Installer is active. Wait for it to finish successfully, then create a fresh recovery preview.");
    }

    private static string RecoveryConfigPath(string configPath)
    {
        configPath = MultiTargetIntegrationManager.Canonical(configPath);
        var name = Path.GetFileName(configPath);
        if (name.Equals("integration.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("integration-recovery.json", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("integration-uninstall", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("heartbeat-migration-", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".integration.lock", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Configuration path overlaps reserved integration recovery metadata.");
        return configPath;
    }

    private static string RecoveryLockPath(string configPath) =>
        MultiTargetIntegrationManager.Canonical(configPath + ".integration.lock");

    private static IEnumerable<string> UninstallCandidates(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        return Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, "integration-uninstall*.json")
            : [];
    }

    private static string SingleUninstallJournal(string configPath)
    {
        EnsureNoOtherRecovery(configPath);
        var paths = UninstallCandidates(configPath).Take(MaxCompletedUninstallJournals + 1).ToArray();
        if (paths.Length == 0)
            throw new InvalidOperationException("No pending uninstall journal remains. Create a fresh integration preview.");
        if (paths.Length > MaxCompletedUninstallJournals)
            throw new InvalidOperationException("Multiple uninstall journals are pending. Automatic ordering is unsafe; resolve installer transactions individually before recovery. All journals were retained.");
        var path = MultiTargetIntegrationManager.Canonical(paths[0]);
        var name = Path.GetFileName(path);
        const string prefix = "integration-uninstall-";
        if (!name.Equals("integration-uninstall.json", StringComparison.OrdinalIgnoreCase) &&
            !(name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
              name.Length == prefix.Length + 64 + 5 &&
              name.Substring(prefix.Length, 64).All(char.IsAsciiHexDigit)))
            throw new InvalidDataException("Unsupported uninstall journal filename. Preserve it and resolve the original installer transaction.");
        if (Directory.Exists(path))
            throw new InvalidDataException("An uninstall journal path is a directory; recovery metadata was preserved.");
        return path;
    }

    private static void EnsureNoOtherRecovery(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        var recovery = MultiTargetIntegrationManager.Canonical(Path.Combine(directory, "integration-recovery.json"));
        var probes = MultiTargetIntegrationManager.Canonical(Path.Combine(directory, "hook-verification"));
        if (Path.Exists(recovery) ||
            Directory.EnumerateFileSystemEntries(directory, "heartbeat-migration-*.json").Any() ||
            (Directory.Exists(probes) && Directory.EnumerateFileSystemEntries(probes, "probe-*.json").Any()) ||
            HookVerification.HasPendingCleanup(configPath))
            throw new InvalidOperationException("Other integration recovery, heartbeat migration or diagnostic probe cleanup is pending. Resolve it first; uninstall journals were retained.");
        if (Path.Exists(MultiTargetIntegrationManager.Canonical(ManifestPath(configPath))))
            throw new InvalidDataException("An active integration ownership manifest remains. Uninstall completion cannot be acknowledged.");
    }

    private void ValidateCompletedUninstall(string configPath, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid uninstall journal.");
        if (document.RootElement.TryGetProperty("version", out _))
        {
            new MultiTargetIntegrationManager(scheduler, verifyDelivery, startup, runtime)
                .ValidateCompletedUninstall(bytes, configPath, token);
            return;
        }
        if (bytes.Length > 524288)
            throw new InvalidDataException("Legacy uninstall journal exceeds its supported size; preserve it for matching installer recovery.");
        var (journal, manifest) = ReadLegacyRemovalJournal(bytes, configPath);
        if (manifest.Version != 1)
            throw new InvalidDataException("Unsupported legacy uninstall ownership version.");
        MultiTargetIntegrationManager.ValidateManifest(manifest, ManifestPath(configPath), configPath);
        var configBytes = MultiTargetIntegrationManager.Read(configPath);
        if (configBytes is null || Hash(configBytes) != manifest.ConfigHash)
            throw new InvalidDataException("Legacy uninstall has no exact preserved-configuration snapshot. Restore matching installer recovery material or resolve it manually; the journal was retained.");
        ValidateRecoveryConfiguration(configBytes, manifest);
        token.ThrowIfCancellationRequested();
        if (Path.Exists(MultiTargetIntegrationManager.Canonical(journal.HookPath)) ||
            scheduler.ReadXml(journal.TaskName) is not null)
            throw new InvalidDataException("Legacy uninstall hook or task removal is incomplete; the journal was retained.");
        token.ThrowIfCancellationRequested();
        ValidateCompletedStartup(startup, journal.StartupName, journal.StartupStateAfter);
        token.ThrowIfCancellationRequested();
        if (!MultiTargetIntegrationManager.Equal(configBytes, MultiTargetIntegrationManager.Read(configPath)))
            throw new InvalidDataException("Preserved legacy configuration changed during inspection; the journal was retained.");
    }

    internal static void ValidateRecoveryConfiguration(byte[] bytes, IntegrationManifest manifest)
    {
        var config = JsonSerializer.Deserialize<RemoteConfiguration>(bytes, Protocol.Json)
            ?? throw new InvalidDataException("Invalid uninstall configuration snapshot.");
        config.Validate();
        if (config.MachineId != manifest.MachineId ||
            (config.RelayPath is not null && !SamePath(config.RelayPath, manifest.RelayPath)))
            throw new InvalidDataException("Uninstall configuration identity does not match journal ownership.");
    }

    internal static void ValidateCompletedStartup(IIntegrationStartup? startup, string? name, IntegrationStartupState? after)
    {
        if (startup is null || name is null || after is null)
            throw new InvalidDataException("Complete startup snapshots and startup inspection are required. Use matching installer recovery or resolve this legacy journal manually; it was retained.");
        if (after.Command is not null || after.Shortcut is not null || after.LegacyCommand is not null ||
            !IntegrationStartup.Equivalent(startup.Capture(name), after))
            throw new InvalidDataException("Complete startup state does not match finished uninstall (including Startup Apps preferences). The journal was retained.");
    }
}

public sealed partial class MultiTargetIntegrationManager
{
    internal void ValidateCompletedUninstall(byte[] bytes, string configPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var journal = ReadJournal(bytes, configPath);
        var manifestChange = journal.Changes.Single(c => Same(c.Path, ManifestPath(configPath)));
        if (!journal.Removal || manifestChange.Before is null || manifestChange.After is not null ||
            journal.TaskAfter is not null)
            throw new InvalidDataException("The journal is not a completed full uninstall; recovery material was retained.");
        var manifest = JsonSerializer.Deserialize<IntegrationManifest>(manifestChange.Before, Protocol.Json)!;
        ValidateManifest(manifest, ManifestPath(configPath), configPath);
        var artifacts = Artifacts(manifest);
        if (manifest.ConfigHash is null || manifest.ConfigHash.Length != 64 ||
            !manifest.ConfigHash.All(char.IsAsciiHexDigit) || artifacts.Any(a => !a.Hash.All(char.IsAsciiHexDigit)) ||
            journal.Changes.Count != artifacts.Count + 2)
            throw new InvalidDataException("Uninstall journal has incomplete ownership snapshots.");
        foreach (var artifact in artifacts)
        {
            token.ThrowIfCancellationRequested();
            var change = journal.Changes.SingleOrDefault(c => Same(c.Path, artifact.Path))
                ?? throw new InvalidDataException("Uninstall journal is missing an owned artifact snapshot.");
            if (artifact.Kind == "hook"
                ? change.After is not null || (change.Before is not null && Hash(change.Before) != artifact.Hash)
                : !Equal(change.After, RemoveSettings(change.Before, artifact)))
                throw new InvalidDataException("Uninstall artifact snapshots do not describe owned removal.");
        }
        var configChange = journal.Changes.SingleOrDefault(c => Same(c.Path, configPath))
            ?? throw new InvalidDataException("Uninstall journal is missing its configuration snapshot.");
        if (!Equal(configChange.Before, configChange.After) &&
            !(configChange.After is null && configChange.Before is not null && Hash(configChange.Before) == manifest.ConfigHash))
            throw new InvalidDataException("Uninstall journal would change preserved configuration; recovery material was retained.");
        foreach (var configBytes in new[] { configChange.Before, configChange.After }.Where(b => b is not null))
            IntegrationManager.ValidateRecoveryConfiguration(configBytes!, manifest);
        foreach (var change in journal.Changes)
        {
            token.ThrowIfCancellationRequested();
            if (change.Before?.Length > 4194304 || change.After?.Length > 4194304 ||
                Directory.Exists(Canonical(change.Path)))
                throw new InvalidDataException("Invalid uninstall file snapshot or path.");
        }
        if (scheduler.ReadXml(journal.TaskName) != journal.TaskAfter)
            throw new InvalidDataException("Legacy task removal is incomplete; the uninstall journal was retained.");
        token.ThrowIfCancellationRequested();
        IntegrationManager.ValidateCompletedStartup(startup, journal.StartupName, journal.StartupStateAfter);
        CheckFiles(journal.Changes, before: false, token);
    }
}
