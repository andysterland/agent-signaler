using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

internal sealed record MultiTargetIntegrationJournal
{
    public int Version { get; init; } = 2;
    public string ConfigPath { get; init; } = "";
    public bool Removal { get; init; }
    public IReadOnlyList<IntegrationFileChange> Changes { get; init; } = [];
    public string TaskName { get; init; } = "";
    public string? TaskBefore { get; init; }
    public string? TaskAfter { get; init; }
    public string StartupName { get; init; } = "";
    public string? StartupBefore { get; init; }
    public string? StartupAfter { get; init; }
    public IntegrationStartupState? StartupStateBefore { get; init; }
    public IntegrationStartupState? StartupStateAfter { get; init; }
}

public sealed partial class MultiTargetIntegrationManager
{
    internal async Task ExecuteAsync(MultiTargetIntegrationJournal journal, string journalPath,
        CancellationToken token, bool retainJournal = false, bool stopClient = false, bool backupUnchangedFiles = false)
    {
        CheckFiles(journal.Changes);
        var journalBytes = Serialize(journal);
        if (journalBytes.Length > 16777216)
            throw new InvalidDataException("Integration transaction exceeds the bounded recovery journal size; no files changed.");
        var suffix = $".agent-signaler.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.{Guid.NewGuid():N}.backup";
        foreach (var change in journal.Changes.Where(c => c.Before is not null &&
            (backupUnchangedFiles || !Equal(c.Before, c.After))))
            AtomicFile.Write(change.Path + suffix, change.Before!);
        if (journal.TaskBefore is not null && journal.TaskBefore != journal.TaskAfter)
            AtomicFile.Write(Path.Combine(Path.GetDirectoryName(journal.ConfigPath)!,
                "heartbeat-task.xml" + suffix), System.Text.Encoding.UTF8.GetBytes(journal.TaskBefore));
        AtomicFile.Write(journalPath, journalBytes);
        try
        {
            if (stopClient && runtime is not null)
                await Bounded(async t => { await runtime.StopAsync(journal.ConfigPath, t); return true; }, token);
            foreach (var change in journal.Changes)
            {
                token.ThrowIfCancellationRequested();
                CheckFiles([change]);
                if (Equal(change.Before, change.After)) continue;
                if (change.After is null) File.Delete(change.Path);
                else AtomicFile.Write(change.Path, change.After);
            }
            token.ThrowIfCancellationRequested();
            if (journal.TaskBefore != journal.TaskAfter)
            {
                if (scheduler.ReadXml(journal.TaskName) != journal.TaskBefore)
                    throw new InvalidDataException("Legacy task changed during apply; it will not be overwritten.");
                if (journal.TaskAfter is null) scheduler.Delete(journal.TaskName);
                else scheduler.Write(journal.TaskName, journal.TaskAfter);
                if (scheduler.ReadXml(journal.TaskName) != journal.TaskAfter)
                    throw new InvalidOperationException("Legacy task change failed.");
            }
            token.ThrowIfCancellationRequested();
            if (journal.StartupStateBefore is { } before && journal.StartupStateAfter is { } after)
            {
                if (!IntegrationStartup.Equivalent(before, after))
                {
                    if (startup is null) throw new InvalidOperationException("Startup servicing is unavailable.");
                    startup.ReplaceState(journal.StartupName, before, after);
                }
            }
            else if (journal.StartupBefore != journal.StartupAfter)
            {
                if (startup is null) throw new InvalidOperationException("Startup servicing is unavailable.");
                startup.Replace(journal.StartupName, journal.StartupBefore, journal.StartupAfter);
            }
            CheckFiles(journal.Changes, false);
            if (!retainJournal) File.Delete(journalPath);
        }
        catch (Exception failure) when (RemoteFailure.IsExpected(failure))
        {
            try
            {
                Restore(journal);
                File.Delete(journalPath);
            }
            catch (Exception rollback) when (RemoteFailure.IsExpected(rollback))
            {
                throw new AggregateException("Rollback incomplete. Concurrent edits were preserved; use RecoverPending after resolving conflicts. Recovery journal and backups were retained.", failure, rollback);
            }
            throw;
        }
    }

    private void Restore(MultiTargetIntegrationJournal journal)
    {
        var errors = new List<Exception>();
        foreach (var change in journal.Changes.Reverse())
        {
            try
            {
                var current = Read(Canonical(change.Path));
                if (Equal(current, change.Before)) continue;
                if (!Equal(current, change.After))
                    throw new InvalidDataException($"Recovery will not overwrite concurrent edits: {change.Path}");
                if (Same(change.Path, journal.ConfigPath) && current is not null)
                {
                    var saved = JsonSerializer.Deserialize<RemoteConfiguration>(current, Protocol.Json)!;
                    var previous = change.Before is null ? null :
                        JsonSerializer.Deserialize<RemoteConfiguration>(change.Before, Protocol.Json);
                    if (saved.Version >= 5 && !saved.DetailedReportingEnabled &&
                        (previous is null || previous.Version < 5 || previous.DetailedReportingEnabled))
                        throw new InvalidDataException("Recovery conflict: the saved detailed conversation opt-out was preserved. " +
                            "Resolve the retained transaction without replacing the explicit false preference.");
                }
                if (change.Before is null) File.Delete(change.Path);
                else AtomicFile.Write(change.Path, change.Before);
            }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { errors.Add(ex); }
        }
        try
        {
            var current = scheduler.ReadXml(journal.TaskName);
            if (current != journal.TaskBefore)
            {
                if (current != journal.TaskAfter)
                    throw new InvalidDataException("Recovery will not overwrite a changed task.");
                if (journal.TaskBefore is null) scheduler.Delete(journal.TaskName);
                else scheduler.Write(journal.TaskName, journal.TaskBefore);
                if (scheduler.ReadXml(journal.TaskName) != journal.TaskBefore)
                    throw new InvalidOperationException("Legacy task rollback failed.");
            }
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { errors.Add(ex); }
        try
        {
            if (journal.StartupStateBefore is { } before && journal.StartupStateAfter is { } after)
            {
                if (!IntegrationStartup.Equivalent(before, after))
                {
                    if (startup is null) throw new InvalidOperationException("Startup recovery is unavailable.");
                    startup.RestoreState(journal.StartupName, before, after);
                }
            }
            else if (journal.StartupBefore != journal.StartupAfter)
            {
                if (startup is null) throw new InvalidOperationException("Startup recovery is unavailable.");
                startup.RestoreLegacyState(journal.StartupName, journal.StartupBefore, journal.StartupAfter);
            }
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { errors.Add(ex); }
        if (errors.Count != 0) throw new AggregateException("Owned integration recovery is incomplete.", errors);
    }

    public void RecoverPending(string configPath)
    {
        configPath = Canonical(configPath);
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var path = RecoveryPath(configPath);
        if (!File.Exists(path)) return;
        var journal = ReadJournal(path, configPath);
        if (runtime is not null && journal.Changes.Where(c => Same(c.Path, configPath))
            .SelectMany(c => new[] { c.Before, c.After }).Where(b => b is not null)
            .Any(b => JsonSerializer.Deserialize<RemoteConfiguration>(b!, Protocol.Json)!.Version >= 5))
            Bounded(async t => { await runtime.SuspendTranscriptAsync(configPath, t); return true; },
                CancellationToken.None).GetAwaiter().GetResult();
        Restore(journal);
        File.Delete(path);
    }

    internal static bool IsRemovalJournal(string configPath, string? transactionId)
    {
        var path = IntegrationManager.RemovalJournalPath(configPath, transactionId);
        if (!File.Exists(path)) return false;
        using var document = JsonDocument.Parse(AtomicFile.ReadBounded(path, 16777216));
        return document.RootElement.TryGetProperty("version", out var version) && version.GetInt32() == 2;
    }

    internal static string FullRemovalPreview(string configPath)
    {
        var manifest = ReadManifest(ManifestPath(configPath), configPath);
        return manifest is null ? "No owned integration is registered." :
            string.Join("\n", Artifacts(manifest).Select(a => a.Kind == "settings"
                ? $"REMOVE only unchanged owned location entries from {a.Path}"
                : $"REMOVE unchanged owned hook {a.Path}")) +
            $"\nStop exact Client for {configPath}; remove owned startup and legacy task." +
            "\nRemove only unchanged owned config. Keep machine identity, diagnostics and all unrelated settings/hooks.";
    }

    internal void Uninstall(string configPath, bool preserveConfiguration, bool prepare, string? transactionId)
    {
        configPath = Canonical(configPath);
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var journalPath = IntegrationManager.RemovalJournalPath(configPath, transactionId);
        if (File.Exists(journalPath))
        {
            if (prepare && !File.Exists(ManifestPath(configPath)))
            {
                _ = ReadJournal(journalPath, configPath);
                return;
            }
            throw new InvalidOperationException("A removal transaction is pending; commit or roll it back.");
        }
        EnsureNoPending(configPath);
        var manifestPath = ManifestPath(configPath);
        var manifest = ReadManifest(manifestPath, configPath);
        if (manifest is null) return;
        if (manifest.ClientPath is not null && runtime is null)
            throw new InvalidOperationException("Client shutdown servicing is unavailable.");
        if (manifest.StartupName is not null && startup is null)
            throw new InvalidOperationException("Startup servicing is unavailable.");
        var taskName = ScheduledTaskDefinition.Name(manifest.MachineId);
        var task = scheduler.ReadXml(taskName);
        if (task is not null && !ScheduledTaskDefinition.IsOwned(task, manifest.MachineId, manifest.RelayPath, configPath))
            throw new InvalidDataException("Unrelated legacy task occupies the name; nothing removed.");
        var startupName = manifest.StartupName ?? IntegrationStartup.Name(configPath);
        var startupState = startup?.Capture(startupName);
        var startupValue = startupState?.Command;
        if (startupValue is not null && startupValue != manifest.StartupCommand)
            throw new InvalidDataException("Unrelated startup occupies the name; nothing removed.");
        var changes = new List<IntegrationFileChange>();
        foreach (var artifact in Artifacts(manifest))
        {
            var path = Canonical(artifact.Path);
            var bytes = Read(path);
            if (artifact.Kind == "hook" && bytes is not null && Hash(bytes) != artifact.Hash)
                throw new InvalidDataException($"Owned hook changed; nothing removed: {path}");
            changes.Add(new(path, bytes, artifact.Kind == "hook" ? null : RemoveSettings(bytes, artifact)));
        }
        var configBytes = Read(configPath);
        changes.Add(new(configPath, configBytes, !preserveConfiguration && configBytes is not null &&
            Hash(configBytes) == manifest.ConfigHash ? null : configBytes));
        changes.Add(new(manifestPath, Read(manifestPath), null));
        var journal = new MultiTargetIntegrationJournal
        {
            ConfigPath = configPath, Removal = true, Changes = changes,
            TaskName = taskName, TaskBefore = task, StartupName = startupName, StartupBefore = startupValue,
            StartupStateBefore = startupState,
            StartupStateAfter = startup?.Prepare(startupName, startupState!, null)
        };
        ExecuteAsync(journal, journalPath, CancellationToken.None, prepare, true).GetAwaiter().GetResult();
    }

    internal void RollbackUninstall(string configPath, string? transactionId)
    {
        configPath = Canonical(configPath);
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var path = IntegrationManager.RemovalJournalPath(configPath, transactionId);
        if (!File.Exists(path)) return;
        var journal = ReadJournal(path, configPath);
        if (!journal.Removal) throw new InvalidDataException("Journal is not an uninstall transaction.");
        Restore(journal);
        File.Delete(path);
    }

    private static MultiTargetIntegrationJournal ReadJournal(string path, string configPath)
    {
        var journal = JsonSerializer.Deserialize<MultiTargetIntegrationJournal>(
            AtomicFile.ReadBounded(path, 16777216), Protocol.Json) ?? throw new InvalidDataException("Invalid recovery journal.");
        if (journal.Version != 2 || !Same(journal.ConfigPath, configPath) ||
            journal.Changes.Count > 130 ||
            journal.Changes.Select(c => Canonical(c.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Changes.Count)
            throw new InvalidDataException("Recovery journal scope mismatch.");
        var manifestChange = journal.Changes.SingleOrDefault(c => Same(c.Path, ManifestPath(configPath)))
            ?? throw new InvalidDataException("Recovery journal has no ownership manifest.");
        var manifests = new[] { manifestChange.Before, manifestChange.After }.Where(b => b is not null)
            .Select(b => JsonSerializer.Deserialize<IntegrationManifest>(b!, Protocol.Json)
                ?? throw new InvalidDataException("Invalid recovery manifest.")).ToArray();
        if (manifests.Length == 0) throw new InvalidDataException("Missing recovery ownership.");
        var identity = manifests[0].MachineId;
        var allowedPaths = manifests.SelectMany(Artifacts).Select(a => Canonical(a.Path))
            .Append(Canonical(configPath)).Append(Canonical(ManifestPath(configPath))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var manifest in manifests)
        {
            IntegrationManager.ValidateStartupManifest(manifest, configPath);
            if (manifest.MachineId != identity || manifest.Version is not (1 or 2))
                throw new InvalidDataException("Recovery manifest identity mismatch.");
        }
        if (journal.Changes.Any(c => !allowedPaths.Contains(Canonical(c.Path))) ||
            journal.StartupName != IntegrationStartup.Name(configPath) ||
            journal.TaskName != ScheduledTaskDefinition.Name(identity) ||
            (journal.TaskBefore is not null && !manifests.Any(m =>
                ScheduledTaskDefinition.IsOwned(journal.TaskBefore, identity, m.RelayPath, configPath))) ||
            (journal.TaskAfter is not null && journal.TaskAfter != journal.TaskBefore) ||
            (journal.StartupBefore is not null && !manifests.Any(m => journal.StartupBefore == m.StartupCommand ||
                journal.StartupBefore == IntegrationStartup.Command(ClientPath(m.RelayPath), configPath))) ||
            (journal.StartupAfter is not null && !manifests.Any(m => journal.StartupAfter == m.StartupCommand ||
                journal.StartupAfter == IntegrationStartup.Command(ClientPath(m.RelayPath), configPath))))
            throw new InvalidDataException("Recovery journal ownership mismatch.");
        IntegrationStartup.ValidateJournalStates(journal.StartupStateBefore, journal.StartupStateAfter,
            journal.StartupBefore, journal.StartupAfter);
        return journal;
    }
}
