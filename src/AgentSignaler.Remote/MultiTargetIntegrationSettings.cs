using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed partial class MultiTargetIntegrationManager
{
    /// <summary>
    /// Previews reporter and sign-in settings without installing, repairing or removing integrations.
    /// Callers must pass the saved integration collection and Relay, not pending hook edits.
    /// </summary>
    public static MultiTargetIntegrationPlan PreviewSettings(RemoteConfiguration config, string configPath,
        string relayPath, bool startClientAtSignIn)
    {
        config.Validate();
        configPath = Canonical(configPath);
        relayPath = Canonical(relayPath);
        RemotePaths.ValidateRelayPath(relayPath);
        EnsureNoPending(configPath);
        var manifestPath = Canonical(ManifestPath(configPath));
        if (Same(configPath, manifestPath) || Same(configPath, RecoveryPath(configPath)) ||
            Path.GetFileName(configPath).StartsWith("integration-uninstall", StringComparison.OrdinalIgnoreCase) ||
            Same(configPath, relayPath) || Same(configPath, ClientPath(relayPath)))
            throw new InvalidDataException("Configuration overlaps reserved ownership, recovery or application files.");
        if (config.RelayPath is not null && !Same(config.RelayPath, relayPath))
            throw new InvalidDataException("Settings cannot change the saved Relay selection.");
        var before = Read(configPath);
        var manifestBefore = Read(manifestPath);
        var prior = ReadManifest(manifestPath, configPath);
        if (before is not null)
        {
            var saved = RemoteConfiguration.Load(configPath);
            if (saved.MachineId != config.MachineId || saved.MachineName != config.MachineName ||
                saved.ClientVersion != config.ClientVersion)
                throw new InvalidDataException("Settings cannot change the saved machine identity or metadata.");
            if (config.Version < saved.Version)
                throw new InvalidDataException("Settings cannot downgrade the saved configuration schema.");
            if (!Equal(Serialize(saved.Integrations), Serialize(config.Integrations)) ||
                (saved.RelayPath is not null && !Same(saved.RelayPath, relayPath)))
                throw new InvalidDataException("Settings cannot change saved integration targets or Relay selection.");
            if (prior is null && saved.Integrations.Count != 0)
                throw new InvalidDataException("Saved integration ownership is missing; settings cannot adopt hook artifacts.");
            if (prior is not null && (prior.MachineId != saved.MachineId ||
                prior.ConfigHash != Hash(before) || !Same(prior.RelayPath, relayPath)))
                throw new InvalidDataException("Saved configuration does not match exact integration ownership.");
            // Keep the stored target instances/metadata and Relay spelling, without rebuilding artifacts.
            config = config with { Integrations = saved.Integrations, RelayPath = saved.RelayPath ?? prior?.RelayPath ?? relayPath };
        }
        else
        {
            if (prior is not null || config.Integrations.Count != 0)
                throw new InvalidDataException("First settings save requires zero hooks and no orphaned ownership manifest.");
            config = config with { RelayPath = relayPath };
        }
        if (config.Version < 5 && !config.DetailedReportingEnabled)
            throw new InvalidDataException("Saving a detailed-sharing opt-out requires configuration version 5 or later.");
        config.Validate();
        var after = Serialize(config);
        if (after.Length > 262144)
            throw new InvalidDataException("Settings exceed the bounded configuration size.");
        var manifest = prior is null
            ? new IntegrationManifest(config.MachineId, "", "", Hash(after), config.RelayPath!) { Version = 2 }
            : prior with { ConfigHash = Hash(after) };
        var manifestAfter = Serialize(manifest);
        if (manifestAfter.Length > 4194304)
            throw new InvalidDataException("Ownership manifest exceeds the bounded size.");
        var changes = new List<IntegrationFileChange>();
        foreach (var artifact in Artifacts(prior))
        {
            var path = Canonical(artifact.Path);
            if (Same(path, RecoveryPath(configPath)) || Same(path, relayPath) || Same(path, ClientPath(relayPath)))
                throw new InvalidDataException("Owned artifact overlaps recovery or application files.");
            var bytes = Read(path);
            if (artifact.Kind == "hook" ? bytes is null || Hash(bytes) != artifact.Hash :
                bytes is null || artifact.Locations.Any(location => !JsoncHookSettings.HasOwnedValue(bytes, location)))
                throw new InvalidDataException("Owned hook or profile changed; settings will not repair it.");
            // Read-only guards participate in concurrency checks/recovery, but are never rewritten.
            changes.Add(new(path, bytes, bytes));
        }
        changes.Add(new(configPath, before, after));
        changes.Add(new(manifestPath, manifestBefore, manifestAfter));
        CheckFiles(changes);
        return new(config, configPath, changes, prior, removal: false,
            startClientAtSignIn: startClientAtSignIn, settingsOnly: true);
    }

    private async Task<IntegrationApplyResult> ApplySettingsAsync(MultiTargetIntegrationPlan plan, CancellationToken token)
    {
        using var held = AtomicFile.Acquire(plan.ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1));
        token.ThrowIfCancellationRequested();
        EnsureNoPending(plan.ConfigPath);
        CheckFiles(plan.Changes, token: token);
        var clientPath = Canonical(ClientPath(plan.RelayPath));
        // Local installation validation only. No source rediscovery or network verification.
        RemotePaths.ValidateRelayInstallation(Path.GetDirectoryName(plan.RelayPath)!, plan.RelayPath);
        if (startup is null)
            throw new InvalidOperationException("Sign-in startup servicing is unavailable.");
        if (plan.StartClientAtSignIn && !File.Exists(clientPath))
            throw new InvalidDataException("Client executable is missing; repair the installation.");
        var startupName = IntegrationStartup.Name(plan.ConfigPath);
        var command = IntegrationStartup.Command(clientPath, plan.ConfigPath);
        var startupBefore = startup.Capture(startupName);
        if (startupBefore.Command is not null && startupBefore.Command != command &&
            startupBefore.Command != plan.Prior?.StartupCommand)
            throw new InvalidDataException("An unrelated startup command occupies the owned name.");
        var startupAfter = startup.Prepare(startupName, startupBefore, plan.StartClientAtSignIn ? command : null);
        var taskName = ScheduledTaskDefinition.Name(plan.Config.MachineId);
        var task = scheduler.ReadXml(taskName);
        if (task is not null && !ScheduledTaskDefinition.IsOwned(task, plan.Config.MachineId, plan.RelayPath, plan.ConfigPath))
            throw new InvalidDataException("An unrelated legacy task occupies the owned name.");
        if (plan.Config.Version >= 5 && runtime is not null)
        {
            // Keep the existing fail-closed privacy boundary, including failed/cancelled saves.
            // Never resume capture from rollback or a failed transaction.
            await Bounded(async t =>
            {
                if (plan.Config.DetailedReportingEnabled) await runtime.PauseTranscriptAsync(plan.ConfigPath, t);
                else await runtime.SuspendTranscriptAsync(plan.ConfigPath, t);
                return true;
            }, token);
        }
        var wasRunning = runtime is not null && (await Bounded(t => runtime.QueryAsync(plan.ConfigPath, t), token)).Running;
        CheckFiles(plan.Changes, token: token);
        if (scheduler.ReadXml(taskName) != task ||
            !IntegrationStartup.Equivalent(startup.Capture(startupName), startupBefore))
            throw new InvalidDataException("Startup or legacy task changed during settings validation.");
        var changes = plan.Changes.ToList();
        var index = changes.FindIndex(c => Same(c.Path, ManifestPath(plan.ConfigPath)));
        var manifest = JsonSerializer.Deserialize<IntegrationManifest>(changes[index].After!, Protocol.Json)!;
        var manifestBytes = Serialize(manifest with
        {
            ClientPath = manifest.ClientPath ?? clientPath,
            StartupName = manifest.StartupName ?? startupName,
            StartupCommand = manifest.StartupCommand ?? command
        });
        if (manifestBytes.Length > 4194304)
            throw new InvalidDataException("Ownership manifest exceeds the bounded size.");
        changes[index] = changes[index] with { After = manifestBytes };
        var journal = new MultiTargetIntegrationJournal
        {
            ConfigPath = plan.ConfigPath, Changes = changes, TaskName = taskName,
            TaskBefore = task, TaskAfter = task,
            StartupName = startupName, StartupBefore = startupBefore.Command, StartupAfter = startupAfter.Command,
            StartupStateBefore = startupBefore, StartupStateAfter = startupAfter
        };
        await ExecuteAsync(journal, RecoveryPath(plan.ConfigPath), token);
        if (!wasRunning || runtime is null)
            return new(true, false, "Settings saved. Client remains stopped; use Start client explicitly.");
        try
        {
            // Reload does not launch a stopped Client, even if it exited after the initial query.
            var active = await Bounded(t => runtime.ReloadAsync(plan.ConfigPath, t), token,
                timeoutSeconds: plan.Config.Version >= 6 ? 15 : 7);
            if (plan.Config.Version >= 6 && active.Running)
                await Bounded(async t => { await runtime.ReloadTranscriptAsync(plan.ConfigPath, t); return true; }, token);
            var applied = active.Running && active.EffectiveRevision == Hash(plan.ConfigBytes);
            return new(true, applied, applied ? "Settings saved and applied to the running Client." :
                "Settings saved; the running Client has not acknowledged them. No connection test was performed.",
                active.EffectiveRevision);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            return new(true, false, "Settings saved; Client reload was not acknowledged. No Client was started.");
        }
    }
}
