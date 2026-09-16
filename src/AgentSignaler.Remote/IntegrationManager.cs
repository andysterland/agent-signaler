using System.Security.Cryptography;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed record IntegrationManifest(Guid MachineId, string HookPath, string HookHash, string ConfigHash, string RelayPath,
    string? ClientPath = null, string? StartupName = null, string? StartupCommand = null)
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<IntegrationArtifact> Artifacts { get; init; } = [];
}
public sealed record IntegrationRemovalJournal(string ConfigPath, string HookPath, byte[]? HookBytes,
    byte[] ManifestBytes, string TaskName, string? TaskXml, string? StartupName = null,
    string? StartupCommand = null, bool WasRunning = false);
public sealed record LegacyHeartbeatMigrationJournal(int Version, string ConfigPath, Guid MachineId,
    string RelayPath, string TaskName, string TaskXml);
public sealed record IntegrationPlan(RemoteConfiguration Config, string ConfigPath, string HookPath, string RelayPath,
    byte[] ConfigBytes, byte[] HookBytes, string TaskName)
{
    public string ClientPath => Path.Combine(Path.GetDirectoryName(RelayPath)!, "AgentSignaler.Client.exe");
    public string StartupName => IntegrationStartup.Name(ConfigPath);
    public string StartupCommand => IntegrationStartup.Command(ClientPath, ConfigPath);
    public string Preview => $"WRITE {ConfigPath}\n{System.Text.Encoding.UTF8.GetString(ConfigBytes)}\n\n" +
        $"WRITE owned hook file {HookPath}\n{System.Text.Encoding.UTF8.GetString(HookBytes)}\n\n" +
        $"Client: {ClientPath}\nRelay: {RelayPath}\n" +
        $"REGISTER HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\{StartupName}\n{StartupCommand}\n" +
        $"Heartbeat: {Config.HeartbeatIntervalSeconds / 60} minutes. Offline deadline: {Config.HeartbeatIntervalSeconds * 2 + 60} seconds.\n" +
        "Startup is at this user's sign-in, not before login. First setup and first legacy migration launch Client immediately; " +
        "updates reload an already-running Client only. After Exit, use Start client explicitly.\n\n" +
        $"REMOVE owned legacy heartbeat task {TaskName} if present. No scheduled task is installed.\n\n" +
        "Existing changed files and task XML receive timestamped backups. " +
        "All changes roll back if validation or test delivery fails. No other hook files are modified. " +
        "Configuration version 3 requires the updated Client, Configurator and Relay together. " +
        "Legacy downgrade requires stopping Client, removing its owned startup/integration, and restoring matching old binaries/configuration backups. " +
        "Back up Dashboard display names, notes and mappings, then explicitly remove/reset the machine locally in Dashboard to clear managed mode and replay history. " +
        "Restoring version-1 configuration alone cannot downgrade a managed machine. Never lower generation or identity files. " +
        (Config.BaseUri.Scheme == Uri.UriSchemeHttps ?
            "HTTPS encrypts reports, but clients are anonymous. Anyone who can reach this URL can submit status. Stop sharing to disable access." :
            "HTTP is unencrypted and unauthenticated; use only a trusted LAN or VPN.");
}

public sealed class IntegrationManager(IIntegrationTaskScheduler scheduler,
    Func<RemoteConfiguration, CancellationToken, Task<bool>> verifyDelivery,
    IIntegrationStartup? startup = null, IIntegrationRuntime? runtime = null)
{
    private static readonly JsonSerializerOptions DisplayJson = new(Protocol.Json) { WriteIndented = true };
    private static string ManifestPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "integration.json");
    public static string RemovalJournalPath(string configPath, string? transactionId = null)
    {
        if (transactionId is not null && !RemoteConfiguration.ValidText(transactionId, 256))
            throw new ArgumentException("Invalid uninstall transaction identifier.", nameof(transactionId));
        var suffix = transactionId is null ? "" : "-" + Hash(System.Text.Encoding.UTF8.GetBytes(transactionId));
        return Path.Combine(Path.GetDirectoryName(configPath)!, $"integration-uninstall{suffix}.json");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static IntegrationPlan Preview(RemoteConfiguration config, string configPath, string copilotHome,
        string relayPath)
    {
        if (config.Version >= 4)
            throw new InvalidDataException("Use a multi-target preview for version 4; legacy setup cannot discard selected integrations.");
        config.Validate();
        foreach (var path in new[] { configPath, copilotHome, relayPath })
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("All integration paths must be absolute.");
        if (config.RelayPath is not null && !SamePath(config.RelayPath, relayPath))
            throw new InvalidDataException("Configured relay location does not match the integration relay path.");
        relayPath = Path.GetFullPath(relayPath);
        configPath = Path.GetFullPath(configPath);
        config = (config with { RelayPath = relayPath }).ToVersion3();
        var hooks = new Dictionary<string, object>();
        foreach (var name in HookAdapters.Events("copilot-cli"))
        {
            hooks[name] = new[] { new { type = "command", exec = relayPath, args = new[] { "hook", "--event", name, "--config", configPath }, timeoutSec = 3 } };
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, hooks }, DisplayJson);
        using var parsed = JsonDocument.Parse(bytes);
        if (parsed.RootElement.GetProperty("hooks").EnumerateObject().Count() != HookAdapters.Events("copilot-cli").Count)
            throw new InvalidDataException("Incomplete hooks.");
        return new IntegrationPlan(config, configPath, Path.Combine(copilotHome, "hooks", "agent-signaler.json"),
            relayPath, JsonSerializer.SerializeToUtf8Bytes(config, DisplayJson), bytes,
            ScheduledTaskDefinition.Name(config.MachineId));
    }

    public async Task<IntegrationApplyResult> ApplyAsync(IntegrationPlan plan, CancellationToken token)
    {
        var checkedPlan = Preview(plan.Config, plan.ConfigPath, Path.GetDirectoryName(Path.GetDirectoryName(plan.HookPath)!)!, plan.RelayPath);
        if (checkedPlan != plan && (!checkedPlan.ConfigBytes.AsSpan().SequenceEqual(plan.ConfigBytes) ||
            !checkedPlan.HookBytes.AsSpan().SequenceEqual(plan.HookBytes) || checkedPlan.TaskName != plan.TaskName ||
            !SamePath(checkedPlan.HookPath, plan.HookPath)))
            throw new InvalidDataException("Integration preview was changed; create a fresh preview.");
        using var held = AtomicFile.Acquire(plan.ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var dataDirectory = Path.GetDirectoryName(plan.ConfigPath)!;
        if (Directory.EnumerateFiles(dataDirectory, "integration-uninstall*.json").Any() ||
            Directory.EnumerateFiles(dataDirectory, "heartbeat-migration-*.json").Any() ||
            File.Exists(Path.Combine(dataDirectory, "integration-recovery.json")) ||
            HookVerification.HasPendingCleanup(plan.ConfigPath))
            throw new InvalidOperationException("An integration removal or heartbeat migration is pending. Complete or roll back the installer operation before applying changes.");
        RemotePaths.ValidateRelayInstallation(Path.GetDirectoryName(plan.RelayPath)!, plan.Config.RelayPath);
        var manifestPath = ManifestPath(plan.ConfigPath);
        var prior = ReadManifest(manifestPath);
        if (prior?.Version == 2)
            throw new InvalidDataException("A multi-target ownership manifest requires a multi-target preview.");
        ValidateStartupManifest(prior, plan.ConfigPath);
        if (prior is not null && prior.MachineId != plan.Config.MachineId) throw new InvalidDataException("Identity conflict.");
        if (File.Exists(plan.ConfigPath) && RemoteConfiguration.Load(plan.ConfigPath).MachineId != plan.Config.MachineId)
            throw new InvalidDataException("Configuration identity conflict.");
        if (runtime is not null && !File.Exists(plan.ClientPath))
            throw new InvalidDataException($"Client executable is missing: '{plan.ClientPath}'. Repair the installation.");
        if (File.Exists(plan.HookPath) &&
            (prior is null || !SamePath(prior.HookPath, plan.HookPath) ||
             !OwnedHook(plan.HookPath, prior))) throw new InvalidDataException("Existing hook file is not an unmodified owned file.");
        if (prior is not null && !SamePath(prior.HookPath, plan.HookPath) &&
            File.Exists(prior.HookPath) && !OwnedHook(prior.HookPath, prior))
            throw new InvalidDataException("Previous hook file was changed; preserve it and resolve manually.");
        var oldTask = scheduler.ReadXml(plan.TaskName);
        if (oldTask is not null && !ScheduledTaskDefinition.IsOwned(oldTask, plan.Config.MachineId,
            prior?.RelayPath ?? plan.RelayPath, plan.ConfigPath))
            throw new InvalidDataException("Scheduled task name is occupied by an unrelated task.");
        var oldStartup = startup?.Read(plan.StartupName);
        if (oldStartup is not null && oldStartup != plan.StartupCommand && oldStartup != prior?.StartupCommand)
            throw new InvalidDataException("Startup name is occupied by an unrelated command.");
        var paths = new[] { plan.ConfigPath, plan.HookPath, manifestPath, prior?.HookPath }
            .Where(p => p is not null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var originals = paths.ToDictionary(p => p, p => File.Exists(p) ? AtomicFile.ReadBounded(p, 262144) : null);
        var wasRunning = runtime is not null && (await Bounded(t => runtime.QueryAsync(plan.ConfigPath, t), token)).Running;
        await DashboardConnection.VerifyBeforeApplyAsync(plan.Config, verifyDelivery, token);
        foreach (var pair in originals)
        {
            var current = File.Exists(pair.Key) ? AtomicFile.ReadBounded(pair.Key, 262144) : null;
            if ((current is null) != (pair.Value is null) ||
                (current is not null && !current.AsSpan().SequenceEqual(pair.Value)))
                throw new InvalidDataException("Integration files changed during validation; create a new preview.");
        }
        if (scheduler.ReadXml(plan.TaskName) != oldTask)
            throw new InvalidDataException("Scheduled task changed during validation; nothing was changed.");
        token.ThrowIfCancellationRequested();
        BackupFiles(originals, oldTask, plan.ConfigPath);
        var taskRemovalAttempted = false;
        var startupAttempted = false;
        try
        {
            AtomicFile.Write(plan.ConfigPath, plan.ConfigBytes);
            AtomicFile.Write(plan.HookPath, plan.HookBytes);
            if (prior is not null && !SamePath(prior.HookPath, plan.HookPath) && File.Exists(prior.HookPath)) File.Delete(prior.HookPath);
            _ = RemoteConfiguration.Load(plan.ConfigPath);
            using var hookCheck = JsonDocument.Parse(AtomicFile.ReadBounded(plan.HookPath, 32768));
            if (oldTask is not null)
            {
                if (scheduler.ReadXml(plan.TaskName) != oldTask)
                    throw new InvalidDataException("Scheduled task changed; the unrelated task will not be removed.");
                taskRemovalAttempted = true;
                scheduler.Delete(plan.TaskName);
                if (scheduler.ReadXml(plan.TaskName) is not null)
                    throw new InvalidOperationException("Legacy scheduled task removal failed.");
            }
            if (startup is not null)
            {
                startupAttempted = true;
                startup.Replace(plan.StartupName, oldStartup, plan.StartupCommand);
            }
            AtomicFile.Write(manifestPath, JsonSerializer.SerializeToUtf8Bytes(new IntegrationManifest(
                plan.Config.MachineId, plan.HookPath, Hash(plan.HookBytes), Hash(plan.ConfigBytes), plan.RelayPath,
                startup is null ? null : plan.ClientPath, startup is null ? null : plan.StartupName,
                startup is null ? null : plan.StartupCommand), Protocol.Json));
        }
        catch (Exception failure) when (RemoteFailure.IsExpected(failure))
        {
            var rollbackErrors = new List<Exception>();
            try { if (startupAttempted) RestoreStartup(plan.StartupName, plan.StartupCommand, oldStartup); }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { rollbackErrors.Add(ex); }
            try { if (taskRemovalAttempted) RestoreLegacyTask(plan.TaskName, oldTask!); }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { rollbackErrors.Add(ex); }
            foreach (var pair in originals)
            {
                try { if (pair.Value is null) File.Delete(pair.Key); else AtomicFile.Write(pair.Key, pair.Value); }
                catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { rollbackErrors.Add(ex); }
            }
            if (rollbackErrors.Count != 0) throw new AggregateException("Rollback incomplete; retained backups require manual recovery.", new[] { failure }.Concat(rollbackErrors));
            throw;
        }
        if (runtime is null)
            return new(true, false, "Settings saved. Runtime activation was not requested.");
        try
        {
            if (!wasRunning && prior?.ClientPath is not null)
                return new(true, false, "Settings saved; Client is not running. Use Start client to resume reporting.");
            var active = await Bounded(t => wasRunning ? runtime.ReloadAsync(plan.ConfigPath, t) :
                runtime.StartAsync(plan.ClientPath, plan.ConfigPath, t), token);
            var expectedRevision = Hash(plan.ConfigBytes);
            var applied = active.Running && active.EffectiveRevision == expectedRevision;
            var disabled = startup?.IsDisabled(plan.StartupName) == true;
            return new(true, applied, applied ?
                "Settings saved and applied to Client." + (disabled ? " Windows has disabled sign-in startup; enable it in Startup Apps." : "") :
                "Settings saved but not applied to Client. Use Start client or retry Apply. " + active.Message,
                active.EffectiveRevision);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            return new(true, false, "Settings saved, but Client activation failed. Use Start client or retry Apply. " + ex.Message);
        }
    }

    public string UninstallPreview(string configPath)
    {
        var probeCleanup = PendingProbePreview(configPath);
        var manifest = ReadManifest(ManifestPath(configPath));
        if (manifest?.Version == 2)
            return probeCleanup + MultiTargetIntegrationManager.FullRemovalPreview(configPath);
        return probeCleanup + (manifest is null ? "No owned integration is registered." :
            $"Remove owned hook: {manifest.HookPath}\nRemove owned legacy task if present: {ScheduledTaskDefinition.Name(manifest.MachineId)}\n" +
            $"Stop exact Client for: {configPath}\nRemove owned sign-in startup: {manifest.StartupName ?? "(legacy installation)"}\n" +
            $"Remove unmodified owned config: {configPath}\nKeep machine-id and diagnostics. Modified or unrelated files are never removed.");
    }

    private static string PendingProbePreview(string configPath)
    {
        if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException("Absolute config path required.");
        if (!HookVerification.HasPendingCleanup(configPath)) return "";
        var lines = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(Path.GetDirectoryName(configPath)!, "hook-verification"), "probe-*.json").Take(64))
        {
            var journal = JsonSerializer.Deserialize<HookProbeJournal>(AtomicFile.ReadBounded(path, 1048576), Protocol.Json)
                ?? throw new InvalidDataException("Invalid diagnostic cleanup journal.");
            var plan = journal.Plan;
            if (plan is null || plan.Target is null || !SamePath(plan.ConfigPath, configPath) ||
                plan.ProbeId == Guid.Empty || !SamePath(plan.HookPath,
                    Path.Combine(plan.Target.HookDirectory, $"agent-signaler-probe-{plan.ProbeId:N}.json")))
                throw new InvalidDataException("Diagnostic cleanup ownership mismatch.");
            plan.Target.Validate();
            lines.Add($"CLEANUP unchanged owned diagnostic hook {plan.HookPath}");
            if (plan.Target.SettingsPath is { } settings)
                lines.Add($"RESTORE only owned diagnostic location entry in {settings}; preserve concurrent unrelated settings.");
        }
        return string.Join("\n", lines) + "\n";
    }

    public void Uninstall(string configPath, bool preserveConfiguration = false) =>
        UninstallCore(configPath, preserveConfiguration, false);

    public void PrepareUninstall(string configPath, string? transactionId = null) =>
        UninstallCore(configPath, true, true, transactionId);

    public static string LegacyHeartbeatJournalPath(string configPath, string transactionId)
    {
        if (!Path.IsPathFullyQualified(configPath) || !RemoteConfiguration.ValidText(transactionId, 256))
            throw new InvalidDataException("Legacy heartbeat migration requires an absolute configuration path and transaction identifier.");
        return Path.Combine(Path.GetDirectoryName(configPath)!,
            $"heartbeat-migration-{Hash(System.Text.Encoding.UTF8.GetBytes(transactionId))}.json");
    }

    public void MigrateLegacyHeartbeat(string configPath, string applicationDirectory, string transactionId)
    {
        var journalPath = LegacyHeartbeatJournalPath(configPath, transactionId);
        var manifestPath = ManifestPath(configPath);
        if (!File.Exists(manifestPath) && !File.Exists(journalPath)) return;
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        LegacyHeartbeatMigrationJournal journal;
        if (File.Exists(journalPath))
        {
            journal = ReadLegacyHeartbeatJournal(journalPath, configPath, applicationDirectory);
            var current = scheduler.ReadXml(journal.TaskName);
            if (current is null) return;
            if (current != journal.TaskXml)
                throw new InvalidDataException("The heartbeat task changed during migration; the replacement task will not be removed.");
        }
        else
        {
            var manifest = ReadManifest(manifestPath);
            if (manifest is null) return;
            var taskName = ScheduledTaskDefinition.Name(manifest.MachineId);
            var task = scheduler.ReadXml(taskName);
            if (task is null) return;
            ValidateServicingInstallation(manifest, configPath, applicationDirectory);
            if (!ScheduledTaskDefinition.IsOwned(task, manifest.MachineId, manifest.RelayPath, configPath))
                throw new InvalidDataException("The legacy heartbeat task is modified or unrelated; migration will not remove it.");
            journal = new(1, Path.GetFullPath(configPath), manifest.MachineId, manifest.RelayPath, taskName, task);
            BackupFiles(new Dictionary<string, byte[]?>(), task, configPath);
            AtomicFile.Write(journalPath, JsonSerializer.SerializeToUtf8Bytes(journal, Protocol.Json));
        }
        if (scheduler.ReadXml(journal.TaskName) != journal.TaskXml)
            throw new InvalidDataException("The heartbeat task changed before removal; the replacement task will not be removed.");
        scheduler.Delete(journal.TaskName);
        if (scheduler.ReadXml(journal.TaskName) is not null)
            throw new InvalidOperationException("Legacy heartbeat task removal failed. The retained task-only journal allows rollback.");
    }

    public void RollbackLegacyHeartbeat(string configPath, string applicationDirectory, string transactionId)
    {
        var journalPath = LegacyHeartbeatJournalPath(configPath, transactionId);
        if (!File.Exists(journalPath)) return;
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var journal = ReadLegacyHeartbeatJournal(journalPath, configPath, applicationDirectory);
        RestoreLegacyTask(journal.TaskName, journal.TaskXml);
        File.Delete(journalPath);
    }

    public void CommitLegacyHeartbeat(string configPath, string applicationDirectory, string transactionId)
    {
        var journalPath = LegacyHeartbeatJournalPath(configPath, transactionId);
        if (!File.Exists(journalPath)) return;
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        _ = ReadLegacyHeartbeatJournal(journalPath, configPath, applicationDirectory);
        File.Delete(journalPath);
    }

    private static LegacyHeartbeatMigrationJournal ReadLegacyHeartbeatJournal(string journalPath,
        string configPath, string applicationDirectory)
    {
        var journal = JsonSerializer.Deserialize<LegacyHeartbeatMigrationJournal>(
            AtomicFile.ReadBounded(journalPath, 524288), Protocol.Json)
            ?? throw new InvalidDataException("Invalid heartbeat migration journal.");
        if (journal.Version != 1 || !Path.IsPathFullyQualified(applicationDirectory) ||
            !Path.IsPathFullyQualified(journal.ConfigPath) || !Path.IsPathFullyQualified(journal.RelayPath) ||
            !SamePath(journal.ConfigPath, configPath) ||
            !SamePath(journal.RelayPath, Path.Combine(applicationDirectory, "AgentSignaler.Relay.exe")) ||
            journal.TaskName != ScheduledTaskDefinition.Name(journal.MachineId) ||
            !ScheduledTaskDefinition.IsOwned(journal.TaskXml, journal.MachineId, journal.RelayPath, configPath))
            throw new InvalidDataException("Heartbeat migration journal ownership mismatch; no task was changed.");
        return journal;
    }

    private static void ValidateServicingInstallation(IntegrationManifest manifest, string configPath, string applicationDirectory)
    {
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new InvalidDataException("The servicing helper installation path must be absolute.");
        var relayPath = Path.Combine(applicationDirectory, "AgentSignaler.Relay.exe");
        var clientPath = Path.Combine(applicationDirectory, "AgentSignaler.Client.exe");
        RemotePaths.ValidateRelayPath(manifest.RelayPath);
        ValidateStartupManifest(manifest, configPath);
        if (manifest.MachineId == Guid.Empty || !SamePath(manifest.RelayPath, relayPath) ||
            (manifest.ClientPath is not null && !SamePath(manifest.ClientPath, clientPath)))
            throw new InvalidDataException("The integration belongs to a different installation. Run its matching Relay servicing helper.");
        if (File.Exists(configPath))
        {
            var config = RemoteConfiguration.Load(configPath);
            if (config.MachineId != manifest.MachineId ||
                (config.RelayPath is not null && !SamePath(config.RelayPath, relayPath)))
                throw new InvalidDataException("Saved configuration does not match the owned installation. Resolve the identity/path conflict before servicing.");
        }
    }

    public async Task StopClientForUpdateAsync(string configPath, string applicationDirectory, CancellationToken token)
    {
        configPath = ClientIdentity.CanonicalPath(configPath);
        if (!Path.IsPathFullyQualified(applicationDirectory))
            throw new InvalidDataException("The servicing helper installation path must be absolute.");
        if (runtime is null) throw new InvalidOperationException("Client shutdown servicing is unavailable.");
        var clientPath = ClientIdentity.CanonicalPath(Path.Combine(applicationDirectory, "AgentSignaler.Client.exe"));
        var relayPath = Path.Combine(applicationDirectory, "AgentSignaler.Relay.exe");
        var manifest = ReadManifest(ManifestPath(configPath));
        if (manifest is null)
        {
            var current = await Bounded(t => runtime.QueryAsync(configPath, t), token);
            if (current.Running)
                throw new InvalidDataException("Client is running without an ownership manifest. Exit that Client from its tray menu, then retry the update.");
            return;
        }
        ValidateStartupManifest(manifest, configPath);
        if (manifest.MachineId == Guid.Empty || !SamePath(manifest.RelayPath, relayPath) ||
            (manifest.ClientPath is not null && !SamePath(manifest.ClientPath, clientPath)))
            throw new InvalidDataException("The integration belongs to a different installation. Run its matching Relay servicing helper or exit that Client manually.");
        if (File.Exists(configPath))
        {
            var config = RemoteConfiguration.Load(configPath);
            if (config.MachineId != manifest.MachineId ||
                (config.RelayPath is not null && !SamePath(config.RelayPath, relayPath)))
                throw new InvalidDataException("Saved configuration does not match the owned installation. Resolve the identity/path conflict before updating.");
        }
        await Bounded(async t => { await runtime.StopAsync(configPath, t); return true; }, token);
    }

    private void UninstallCore(string configPath, bool preserveConfiguration, bool prepare, string? transactionId = null)
    {
        if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException("Absolute config path required.");
        if (File.Exists(Path.Combine(Path.GetDirectoryName(configPath)!, "integration-recovery.json")))
            throw new InvalidOperationException("Owned integration recovery is pending. Recover it before uninstalling.");
        if (HookVerification.HasPendingCleanup(configPath)) HookVerification.Recover(configPath);
        if (HookVerification.HasPendingCleanup(configPath))
            throw new InvalidOperationException("Diagnostic cleanup is incomplete. Recover remaining owned probes before uninstalling.");
        if (ReadManifest(ManifestPath(configPath))?.Version == 2 ||
            MultiTargetIntegrationManager.IsRemovalJournal(configPath, transactionId))
        {
            new MultiTargetIntegrationManager(scheduler, verifyDelivery, startup, runtime)
                .Uninstall(configPath, preserveConfiguration, prepare, transactionId);
            return;
        }
        if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException("Absolute config path required.");
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        if (HookVerification.HasPendingCleanup(configPath))
            throw new InvalidOperationException("A diagnostic probe started during removal. Retry the full removal preview.");
        var journalPath = RemovalJournalPath(configPath, transactionId);
        if (File.Exists(journalPath))
        {
            if (prepare && !File.Exists(ManifestPath(configPath))) return;
            throw new InvalidOperationException("A removal transaction already exists; commit or roll it back first.");
        }
        var manifestPath = ManifestPath(configPath);
        var manifest = ReadManifest(manifestPath);
        if (manifest is null)
        {
            if (runtime is not null)
                Bounded(async t => { await runtime.StopAsync(configPath, t); return true; }, CancellationToken.None)
                    .GetAwaiter().GetResult();
            return;
        }
        ValidateStartupManifest(manifest, configPath);
        var startupCommand = manifest.StartupName is null ? null : startup?.Read(manifest.StartupName);
        if (startupCommand is not null && startupCommand != manifest.StartupCommand)
            throw new InvalidDataException("Unrelated startup command occupies the registered name; nothing removed.");
        if (manifest.StartupName is not null && startup is null)
            throw new InvalidOperationException("Startup servicing is unavailable; use the matching installed uninstall helper.");
        var taskName = ScheduledTaskDefinition.Name(manifest.MachineId);
        var task = scheduler.ReadXml(taskName);
        if (task is not null && !ScheduledTaskDefinition.IsOwned(task, manifest.MachineId, manifest.RelayPath, configPath))
            throw new InvalidDataException("Unrelated task occupies the registered name; nothing removed.");
        if (File.Exists(manifest.HookPath) && !OwnedHook(manifest.HookPath, manifest))
            throw new InvalidDataException("Owned hook has been modified; nothing removed.");
        var wasRunning = runtime is not null &&
            Bounded(t => runtime.QueryAsync(configPath, t), CancellationToken.None).GetAwaiter().GetResult().Running;
        if (manifest.ClientPath is not null && runtime is null)
            throw new InvalidOperationException("Client shutdown servicing is unavailable; use the matching installed uninstall helper.");
        var paths = new[] { manifest.HookPath, configPath, manifestPath };
        var originals = paths.ToDictionary(p => p, p => File.Exists(p) ? AtomicFile.ReadBounded(p, 262144) : null);
        BackupFiles(originals, task, configPath);
        if (prepare)
            AtomicFile.Write(journalPath, JsonSerializer.SerializeToUtf8Bytes(new IntegrationRemovalJournal(
                configPath, manifest.HookPath, originals[manifest.HookPath], originals[manifestPath]!, taskName, task,
                manifest.StartupName, startupCommand, wasRunning), Protocol.Json));
        var taskRemovalAttempted = false;
        var startupRemovalAttempted = false;
        try
        {
            if (runtime is not null)
                Bounded(async t => { await runtime.StopAsync(configPath, t); return true; }, CancellationToken.None)
                    .GetAwaiter().GetResult();
            if (manifest.StartupName is not null && startupCommand is not null)
            {
                startupRemovalAttempted = true;
                startup!.Replace(manifest.StartupName, startupCommand, null);
            }
            if (task is not null)
            {
                if (scheduler.ReadXml(taskName) != task)
                    throw new InvalidDataException("Scheduled task changed; the unrelated task will not be removed.");
                taskRemovalAttempted = true;
                scheduler.Delete(taskName);
                if (scheduler.ReadXml(taskName) is not null)
                    throw new InvalidOperationException("Legacy scheduled task removal failed.");
            }
            File.Delete(manifest.HookPath);
            if (!preserveConfiguration && File.Exists(configPath) && Hash(AtomicFile.ReadBounded(configPath, 8192)) == manifest.ConfigHash)
                File.Delete(configPath);
            File.Delete(manifestPath);
        }
        catch (Exception failure) when (RemoteFailure.IsExpected(failure))
        {
            var errors = new List<Exception> { failure };
            foreach (var pair in originals)
                try { if (pair.Value is not null) AtomicFile.Write(pair.Key, pair.Value); }
                catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { errors.Add(ex); }
            try { if (taskRemovalAttempted) RestoreLegacyTask(taskName, task!); }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { errors.Add(ex); }
            try { if (startupRemovalAttempted) RestoreStartup(manifest.StartupName!, null, startupCommand); }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { errors.Add(ex); }
            if (errors.Count == 1 && prepare) File.Delete(journalPath);
            if (errors.Count > 1) throw new AggregateException("Uninstall rollback incomplete.", errors);
            throw;
        }
    }

    public void RollbackUninstall(string configPath, string? transactionId = null)
    {
        if (MultiTargetIntegrationManager.IsRemovalJournal(configPath, transactionId))
        {
            new MultiTargetIntegrationManager(scheduler, verifyDelivery, startup, runtime)
                .RollbackUninstall(configPath, transactionId);
            return;
        }
        if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException("Absolute config path required.");
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var journalPath = RemovalJournalPath(configPath, transactionId);
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<IntegrationRemovalJournal>(AtomicFile.ReadBounded(journalPath, 524288), Protocol.Json)
            ?? throw new InvalidDataException("Invalid removal journal.");
        var manifest = JsonSerializer.Deserialize<IntegrationManifest>(journal.ManifestBytes, Protocol.Json)
            ?? throw new InvalidDataException("Invalid journal ownership.");
        ValidateStartupManifest(manifest, configPath);
        if (!SamePath(journal.ConfigPath, configPath) || !SamePath(journal.HookPath, manifest.HookPath) ||
            journal.TaskName != ScheduledTaskDefinition.Name(manifest.MachineId) ||
            (journal.HookBytes is not null && Hash(journal.HookBytes) != manifest.HookHash) ||
            (journal.TaskXml is not null && !ScheduledTaskDefinition.IsOwned(journal.TaskXml, manifest.MachineId, manifest.RelayPath, configPath)) ||
            journal.StartupName != manifest.StartupName ||
            (journal.StartupCommand is not null && journal.StartupCommand != manifest.StartupCommand))
            throw new InvalidDataException("Removal journal ownership mismatch.");
        var task = scheduler.ReadXml(journal.TaskName);
        if (task is not null && task != journal.TaskXml)
            throw new InvalidDataException("Task changed during uninstall; rollback will not overwrite it.");
        if (journal.StartupName is not null)
        {
            if (startup is null) throw new InvalidOperationException("Startup rollback servicing is unavailable.");
            var current = startup.Read(journal.StartupName);
            if (current is not null && current != journal.StartupCommand)
                throw new InvalidDataException("Startup changed during uninstall; rollback will not overwrite it.");
        }
        var originals = new Dictionary<string, byte[]?>
        {
            [journal.HookPath] = journal.HookBytes, [ManifestPath(configPath)] = journal.ManifestBytes
        };
        foreach (var pair in originals)
            if (File.Exists(pair.Key) && (pair.Value is null ||
                !AtomicFile.ReadBounded(pair.Key, 262144).AsSpan().SequenceEqual(pair.Value)))
                throw new InvalidDataException("Files changed during uninstall; rollback will not overwrite them.");
        if (task is null && journal.TaskXml is not null) scheduler.Write(journal.TaskName, journal.TaskXml);
        if (journal.StartupName is not null)
            RestoreStartup(journal.StartupName, null, journal.StartupCommand);
        foreach (var pair in originals)
            if (pair.Value is not null) AtomicFile.Write(pair.Key, pair.Value);
        File.Delete(journalPath);
    }

    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        return await Task.Run(() => action(timeout.Token), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    private void RestoreStartup(string name, string? applied, string? original)
    {
        var current = startup!.Read(name);
        if (current == original) return;
        startup.Replace(name, applied, original);
    }

    internal static void ValidateStartupManifest(IntegrationManifest? manifest, string configPath)
    {
        if (manifest is null || (manifest.ClientPath is null && manifest.StartupName is null && manifest.StartupCommand is null))
            return;
        if (manifest.ClientPath is null || manifest.StartupName != IntegrationStartup.Name(configPath) ||
            manifest.StartupCommand != IntegrationStartup.Command(manifest.ClientPath, configPath) ||
            !SamePath(manifest.ClientPath, Path.Combine(Path.GetDirectoryName(manifest.RelayPath)!, "AgentSignaler.Client.exe")))
            throw new InvalidDataException("Invalid startup ownership manifest.");
    }

    public void CommitUninstall(string configPath, string? transactionId = null)
    {
        if (!Path.IsPathFullyQualified(configPath)) throw new InvalidDataException("Absolute config path required.");
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        File.Delete(RemovalJournalPath(configPath, transactionId));
    }

    private void RestoreLegacyTask(string name, string xml)
    {
        var current = scheduler.ReadXml(name);
        if (current is not null && current != xml)
            throw new InvalidDataException("Task changed during integration update; rollback will not overwrite it.");
        if (current is null) scheduler.Write(name, xml);
    }

    private static void BackupFiles(IReadOnlyDictionary<string, byte[]?> originals, string? task, string configPath)
    {
        var suffix = $".agent-signaler.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.{Guid.NewGuid():N}.backup";
        foreach (var pair in originals.Where(p => p.Value is not null))
            AtomicFile.Write(pair.Key + suffix, pair.Value!);
        if (task is not null) AtomicFile.Write(Path.Combine(Path.GetDirectoryName(configPath)!,
            "heartbeat-task.xml" + suffix), System.Text.Encoding.UTF8.GetBytes(task));
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool OwnedHook(string path, IntegrationManifest manifest) =>
        Hash(AtomicFile.ReadBounded(path, 32768)) == manifest.HookHash;
    internal static IntegrationManifest? ReadManifest(string path) => File.Exists(path) ?
        JsonSerializer.Deserialize<IntegrationManifest>(AtomicFile.ReadBounded(path, 4194304), Protocol.Json)
            ?? throw new InvalidDataException("Invalid ownership manifest.") : null;
}
