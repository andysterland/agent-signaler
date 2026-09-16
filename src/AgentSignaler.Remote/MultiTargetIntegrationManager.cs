using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AgentSignaler.Remote.Tests")]

namespace AgentSignaler.Remote;

public sealed record IntegrationArtifact
{
    public string Kind { get; init; } = "hook";
    public string Path { get; init; } = "";
    public string Hash { get; init; } = "";
    public IReadOnlyList<string> TargetIds { get; init; } = [];
    public IReadOnlyList<string> Locations { get; init; } = [];
    public byte[]? OriginalBytes { get; init; }
}

internal sealed record IntegrationFileChange(string Path, byte[]? Before, byte[]? After);

public sealed class MultiTargetIntegrationPlan
{
    public RemoteConfiguration Config { get; }
    public string ConfigPath { get; }
    public string RelayPath => Config.RelayPath!;
    public IReadOnlyList<IntegrationTarget> Targets => Config.Integrations;
    public bool IsRemoval { get; }
    public string Preview { get; }
    internal IReadOnlyList<IntegrationFileChange> Changes { get; }
    internal IntegrationManifest? Prior { get; }
    internal byte[] ConfigBytes { get; }
    internal Func<string, bool> LoaderHookExists { get; }

    internal MultiTargetIntegrationPlan(RemoteConfiguration config, string configPath,
        IReadOnlyList<IntegrationFileChange> changes, IntegrationManifest? prior, bool removal,
        Func<string, bool>? loaderHookExists = null)
    {
        Config = config;
        ConfigPath = configPath;
        Changes = changes;
        Prior = prior;
        IsRemoval = removal;
        ConfigBytes = MultiTargetIntegrationManager.Serialize(config);
        LoaderHookExists = loaderHookExists ?? File.Exists;
        Preview = string.Join("\n\n", changes.Where(c => !MultiTargetIntegrationManager.Equal(c.Before, c.After))
            .Select(c => $"{(c.After is null ? "REMOVE" : "WRITE")} {c.Path}" +
                (c.After is null ? "" : "\n" + Encoding.UTF8.GetString(c.After)))) +
            "\n\nShared scopes install one artifact; removing a row does not isolate hosts sharing that scope." +
            "\nVS Code is local Windows only. Profile sync, policy and workspace hooks can override user hooks; reload/new sessions may be required." +
            "\nIsolated CLI/IDE scopes may coexist. Overlapping default or explicit loaders are blocked; unrelated hook locations are never disabled." +
            "\nSelected hook files and profile locations are configured automatically; configuration is not proof of actual host event delivery." +
            $"\nOne tray Client; heartbeat {config.HeartbeatIntervalSeconds / 60} minutes. " +
            (removal ? "Retain configuration and sign-in startup; do not start a stopped Client." :
                config.Integrations.Count == 0 ? "Save reporter configuration and owned sign-in startup only. Use Start client explicitly to begin reporting; no hook files are installed." :
                "Register owned sign-in startup and remove an owned legacy heartbeat task. First setup/migration starts Client; updates only reload a running Client.") +
            (removal ? "" : $"\nREGISTER HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\{IntegrationStartup.Name(configPath)}" +
                $"\n{IntegrationStartup.Command(Path.Combine(Path.GetDirectoryName(config.RelayPath!)!, "AgentSignaler.Client.exe"), configPath)}" +
                $"\nREMOVE owned legacy task {ScheduledTaskDefinition.Name(config.MachineId)} if present; no scheduled task is installed.") +
            "\nDashboard source-aware capability verification is required before any version 4 installation changes." +
            "\nConcurrent changes require a new preview. Ownership-checked rollback retains a recovery journal on conflicts." +
            (config.BaseUri.Scheme == Uri.UriSchemeHttps
                ? "\nHTTPS is encrypted but anonymous: anyone reaching Dashboard can submit status."
                : "\nHTTP is unencrypted and anonymous; use a trusted LAN or VPN.");
    }
}

public sealed partial class MultiTargetIntegrationManager(IIntegrationTaskScheduler scheduler,
    Func<RemoteConfiguration, CancellationToken, Task<bool>> verifyDelivery,
    IIntegrationStartup? startup = null, IIntegrationRuntime? runtime = null)
{
    private static readonly JsonSerializerOptions DisplayJson = new(Protocol.Json) { WriteIndented = true };
    internal static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, DisplayJson);
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static bool Equal(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
    internal static string ManifestPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "integration.json");
    internal static byte[]? Read(string path) => File.Exists(path) ? AtomicFile.ReadBounded(path, 4194304) : null;
    private static string RecoveryPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "integration-recovery.json");

    public static MultiTargetIntegrationPlan Preview(RemoteConfiguration config, string configPath,
        IReadOnlyList<IntegrationTarget> targets, string relayPath) =>
        Preview(config, configPath, targets, relayPath, File.Exists);

    internal static MultiTargetIntegrationPlan Preview(RemoteConfiguration config, string configPath,
        IReadOnlyList<IntegrationTarget> targets, string relayPath, Func<string, bool> loaderHookExists)
    {
        config.Validate();
        configPath = Canonical(configPath);
        relayPath = Canonical(relayPath);
        RemotePaths.ValidateRelayPath(relayPath);
        if (config.RelayPath is not null && !Same(config.RelayPath, relayPath))
            throw new InvalidDataException("Configured relay does not match the selected installation.");
        var selected = targets.Select(t => t with
        {
            HookDirectory = Canonical(t.HookDirectory),
            SettingsPath = t.SettingsPath is null ? null : Canonical(t.SettingsPath),
            SupportedEvents = t.SupportedEvents.ToArray()
        }).ToArray();
        config = config.ToVersion4() with { RelayPath = relayPath, Integrations = selected };
        config.Validate();
        foreach (var target in selected)
        {
            if (!target.CanInstall) throw new InvalidDataException($"{target.DisplayName}: {target.Reason} Verification is required.");
            AutomaticHookConfiguration.RequireInstallable(target, configPath);
        }
        ValidateScopes(selected, loaderHookExists);
        return Build(config, configPath, false, loaderHookExists);
    }

    public static MultiTargetIntegrationPlan PreviewRemove(string configPath, IReadOnlyList<string> targetIds)
    {
        configPath = Canonical(configPath);
        var config = RemoteConfiguration.Load(configPath);
        if (config.Version != 4 || targetIds.Count == 0 ||
            targetIds.Any(id => !config.Integrations.Any(t => t.Id == id)))
            throw new InvalidDataException("Select installed version 4 integrations to remove.");
        return Build(config with { Integrations = config.Integrations.Where(t => !targetIds.Contains(t.Id)).ToArray() },
            configPath, true);
    }

    private static MultiTargetIntegrationPlan Build(RemoteConfiguration config, string configPath, bool removal,
        Func<string, bool>? loaderHookExists = null)
    {
        EnsureNoPending(configPath);
        var manifestPath = Canonical(ManifestPath(configPath));
        if (Same(configPath, manifestPath) || Same(configPath, RecoveryPath(configPath)) ||
            Path.GetFileName(configPath).StartsWith("integration-uninstall", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Configuration path overlaps reserved ownership/recovery files.");
        var manifestBytes = Read(manifestPath);
        var prior = ReadManifest(manifestPath, configPath);
        if (prior is not null && prior.MachineId != config.MachineId)
            throw new InvalidDataException("Ownership manifest identity conflict.");
        var existingConfig = Read(configPath);
        if (existingConfig is not null && RemoteConfiguration.Load(configPath).MachineId != config.MachineId)
            throw new InvalidDataException("Saved configuration identity conflict.");
        var oldArtifacts = Artifacts(prior);
        var changes = new Dictionary<string, IntegrationFileChange>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<IntegrationArtifact>();
        void Change(string path, byte[]? before, byte[]? after)
        {
            path = Canonical(path);
            if (Same(path, configPath) || Same(path, manifestPath) || Same(path, config.RelayPath!) ||
                Same(path, ClientPath(config.RelayPath!)))
                throw new InvalidDataException("An integration artifact overlaps application configuration or binaries.");
            changes[path] = new(path, before, after);
        }
        foreach (var group in config.Integrations.GroupBy(t => t.HookDirectory, StringComparer.OrdinalIgnoreCase))
        {
            var target = group.First();
            var path = Canonical(Path.Combine(target.HookDirectory, "agent-signaler.json"));
            var old = oldArtifacts.SingleOrDefault(a => Same(a.Path, path));
            var current = Read(path);
            if (removal && (old?.Kind != "hook" || group.Any(t => !old.TargetIds.Contains(t.Id))))
                throw new InvalidDataException("Removal cannot install a new or moved target. Restore the saved owned selection and create a fresh preview.");
            if (current is not null && (old?.Kind != "hook" || Hash(current) != old.Hash))
                throw new InvalidDataException($"Hook is not an unchanged owned artifact: {path}");
            var bytes = removal ? current : HookAdapters.Generate(target, config.RelayPath!, configPath);
            Change(path, current, bytes);
            artifacts.Add(new() { Path = path, Hash = bytes is null ? old!.Hash : Hash(bytes), TargetIds = group.Select(t => t.Id).ToArray() });
        }
        foreach (var group in config.Integrations.Where(t => t.Kind == "vscode").GroupBy(t => t.SettingsPath!, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) throw new InvalidDataException("VS Code requires a verified profile settings path.");
            var path = Canonical(group.Key);
            if (changes.ContainsKey(path)) throw new InvalidDataException("Settings and hook destinations overlap.");
            var old = oldArtifacts.SingleOrDefault(a => Same(a.Path, path));
            if (old is not null && old.Kind != "settings") throw new InvalidDataException("Artifact types conflict.");
            var current = Read(path);
            var bytes = current;
            var locations = group.Select(t => Canonical(t.HookDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (removal && (old?.Kind != "settings" || group.Any(t => !old.TargetIds.Contains(t.Id)) ||
                locations.Any(location => !old.Locations.Contains(location, StringComparer.OrdinalIgnoreCase))))
                throw new InvalidDataException("Removal cannot register a new settings location; use a verified installation preview.");
            foreach (var location in old?.Locations ?? [])
            {
                if (locations.Contains(location, StringComparer.OrdinalIgnoreCase)) continue;
                if (bytes is not null) bytes = JsoncHookSettings.Remove(bytes, location);
            }
            foreach (var location in locations)
            {
                if (old?.Locations.Contains(location, StringComparer.OrdinalIgnoreCase) == true)
                {
                    if (bytes is null || !JsoncHookSettings.HasOwnedValue(bytes, location))
                        throw new InvalidDataException($"Owned VS Code setting changed or disappeared: {path}. Preserve the user edit and resolve manually.");
                }
                else bytes = JsoncHookSettings.Add(bytes, location);
            }
            Change(path, current, bytes);
            artifacts.Add(new()
            {
                Kind = "settings", Path = path, Hash = Hash(bytes!), Locations = locations,
                OriginalBytes = old is null ? current : RemoveSettings(current, old), TargetIds = group.Select(t => t.Id).ToArray()
            });
        }
        foreach (var old in oldArtifacts.Where(a => !artifacts.Any(b => Same(a.Path, b.Path))))
        {
            var current = Read(Canonical(old.Path));
            if (old.Kind == "hook")
            {
                if (current is not null && Hash(current) != old.Hash)
                    throw new InvalidDataException($"Previous hook was modified; it will not be removed: {old.Path}");
                Change(old.Path, current, null);
            }
            else Change(old.Path, current, RemoveSettings(current, old));
        }
        var configBytes = Serialize(config);
        if (configBytes.Length > 262144)
            throw new InvalidDataException("Selected integration configuration exceeds the bounded configuration size.");
        var manifest = new IntegrationManifest(config.MachineId, "", "", Hash(configBytes), config.RelayPath!,
            prior?.ClientPath, prior?.StartupName, prior?.StartupCommand)
        { Version = 2, Artifacts = artifacts };
        var nextManifestBytes = Serialize(manifest);
        if (nextManifestBytes.Length > 4194304)
            throw new InvalidDataException("Owned settings backups exceed the bounded manifest size; reduce the selected integration set.");
        changes[configPath] = new(configPath, existingConfig, configBytes);
        changes[manifestPath] = new(manifestPath, manifestBytes, nextManifestBytes);
        CheckFiles(changes.Values);
        return new(config, configPath, changes.Values.ToArray(), prior, removal, loaderHookExists);
    }

    private static void ValidateScopes(IReadOnlyList<IntegrationTarget> targets, Func<string, bool> loaderHookExists)
    {
        foreach (var group in targets.GroupBy(t => t.HookDirectory, StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            if (group.Any(t => t.Kind != first.Kind || t.ScopeId != first.ScopeId ||
                t.AdapterVersion != first.AdapterVersion || !t.SupportedEvents.SequenceEqual(first.SupportedEvents)))
                throw new InvalidDataException("Shared hook destinations require the same verified source scope, adapter and event set; instance isolation cannot be promised.");
        }
        foreach (var group in targets.GroupBy(t => (t.Kind, t.ScopeId)))
            if (group.Select(t => t.HookDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                throw new InvalidDataException("One source scope cannot identify different hook directories.");
        foreach (var group in targets.Where(t => t.SettingsPath is not null)
            .GroupBy(t => t.SettingsPath!, StringComparer.OrdinalIgnoreCase))
            if (group.Select(t => t.HookDirectory).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                throw new InvalidDataException("One VS Code profile cannot register multiple observer scopes; that would duplicate every hook invocation.");
        var directories = targets.Select(t => t.HookDirectory).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var a in directories)
        foreach (var b in directories)
            if (!Same(a, b) && b.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Nested hook directories may be loaded twice; choose verified isolated scopes.");
        if (targets.Any(t => t.Kind == "vscode"))
        {
            var defaultCliDirectory = Canonical(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "hooks"));
            var cliHome = Environment.GetEnvironmentVariable("COPILOT_HOME");
            if (string.IsNullOrWhiteSpace(cliHome))
                cliHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
            var cliDirectory = Canonical(Path.Combine(cliHome, "hooks"));
            var studioDirectory = Canonical(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "VisualStudio", "CopilotCli", "hooks"));
            if (targets.Where(t => t.Kind == "vscode").Any(t => Same(t.HookDirectory, defaultCliDirectory) ||
                Same(t.HookDirectory, cliDirectory) || Same(t.HookDirectory, studioDirectory)))
                throw new InvalidDataException("VS Code must use an isolated app-owned directory, not a CLI or Visual Studio default hook directory.");
            var cliHook = Path.Combine(defaultCliDirectory, "agent-signaler.json");
            if (targets.Any(t => t.Kind != "vscode" && Same(t.HookDirectory, defaultCliDirectory)) ||
                loaderHookExists(cliHook))
                throw new InvalidDataException("The default ~/.copilot/hooks observer may also be loaded by VS Code. " +
                    "Use a verified isolated custom CLI home, or resolve the default-loader conflict. " +
                    "Automatic file exclusions are not verified by this implementation; unrelated hooks will not be disabled.");
            foreach (var code in targets.Where(t => t.Kind == "vscode"))
            {
                var settings = Read(code.SettingsPath!);
                if (settings is null) continue;
                foreach (var location in JsoncHookSettings.EnabledLocations(settings))
                {
                    var expanded = location.StartsWith("~/", StringComparison.Ordinal) || location.StartsWith(@"~\", StringComparison.Ordinal)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), location[2..]) : location;
                    if (!Path.IsPathFullyQualified(expanded)) continue;
                    var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
                    if (targets.Where(t => !Same(t.HookDirectory, code.HookDirectory)).Any(t =>
                        Same(path, Path.Combine(t.HookDirectory, "agent-signaler.json")) || Same(path, t.HookDirectory) ||
                        t.HookDirectory.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("This VS Code profile explicitly loads another selected observer scope. " +
                            "Resolve and re-verify loader isolation; unrelated settings will not be changed.");
                }
            }
        }
    }

    internal static byte[]? RemoveSettings(byte[]? current, IntegrationArtifact artifact)
    {
        if (current is null) return null;
        if (Hash(current) == artifact.Hash) return artifact.OriginalBytes;
        foreach (var location in artifact.Locations) current = JsoncHookSettings.Remove(current, location);
        return current;
    }

    internal static IReadOnlyList<IntegrationArtifact> Artifacts(IntegrationManifest? manifest) =>
        manifest is null ? [] : manifest.Version == 2 ? manifest.Artifacts :
        [new() { Path = manifest.HookPath, Hash = manifest.HookHash }];

    internal static IntegrationManifest? ReadManifest(string path, string configPath)
    {
        var manifest = IntegrationManager.ReadManifest(path);
        if (manifest is null) return null;
        if (manifest.Version is not (1 or 2) || manifest.MachineId == Guid.Empty)
            throw new InvalidDataException("Unsupported ownership manifest.");
        IntegrationManager.ValidateStartupManifest(manifest, configPath);
        RemotePaths.ValidateRelayPath(manifest.RelayPath);
        var artifacts = Artifacts(manifest);
        if (artifacts is null || artifacts.Count > 128 || artifacts.Any(a => a is null || a.Hash is null ||
                a.TargetIds is null || a.Locations is null) ||
            artifacts.Select(a => Canonical(a.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != artifacts.Count ||
            artifacts.Any(a => a.Kind is not ("hook" or "settings") || a.Hash.Length != 64 ||
                a.TargetIds.Count > 64 || a.Locations.Count > 64 ||
                (a.Kind == "hook" && !string.Equals(Path.GetFileName(a.Path), "agent-signaler.json", StringComparison.OrdinalIgnoreCase)) ||
                (a.Kind == "settings" && (!string.Equals(Path.GetFileName(a.Path), "settings.json", StringComparison.OrdinalIgnoreCase) ||
                    a.OriginalBytes?.Length > 1048576)) ||
                Same(a.Path, configPath) || Same(a.Path, path)))
            throw new InvalidDataException("Invalid artifact ownership collection.");
        return manifest;
    }

    internal static string Canonical(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new InvalidDataException("Absolute local integration paths are required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.StartsWith(@"\\", StringComparison.Ordinal) || full[2..].Contains(':') ||
            full.Split('\\').Any(p => p.EndsWith(' ') || p.EndsWith('.') || p.Contains('~')))
            throw new InvalidDataException("Network, device, alternate-stream and aliased integration paths are not supported.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Reparse-point integration paths are blocked: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return full;
    }

    internal static bool Same(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static string ClientPath(string relay) => Path.Combine(Path.GetDirectoryName(relay)!, "AgentSignaler.Client.exe");

    private static void EnsureNoPending(string configPath)
    {
        var directory = Path.GetDirectoryName(configPath)!;
        if (HookVerification.HasPendingCleanup(configPath) || File.Exists(RecoveryPath(configPath)) || (Directory.Exists(directory) &&
            (Directory.EnumerateFiles(directory, "integration-uninstall*.json").Any() ||
             Directory.EnumerateFiles(directory, "heartbeat-migration-*.json").Any())))
            throw new InvalidOperationException("Integration recovery or installer servicing is pending. Recover/complete it before creating a preview.");
    }

    private static void CheckFiles(IEnumerable<IntegrationFileChange> changes, bool before = true)
    {
        foreach (var change in changes)
            if (!Equal(Read(Canonical(change.Path)), before ? change.Before : change.After))
                throw new InvalidDataException($"File changed since preview: {change.Path}. Create a new preview; no concurrent edits will be overwritten.");
    }

    public async Task<IntegrationApplyResult> ApplyAsync(MultiTargetIntegrationPlan plan, CancellationToken token)
    {
        if (!Equal(Serialize(plan.Config), plan.ConfigBytes))
            throw new InvalidDataException("Preview was changed; create a fresh preview.");
        using var held = AtomicFile.Acquire(plan.ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1));
        EnsureNoPending(plan.ConfigPath);
        CheckFiles(plan.Changes);
        if (!plan.IsRemoval)
        {
            foreach (var target in plan.Targets)
            {
                if (!target.CanInstall) throw new InvalidDataException("Selected integration is not installable.");
                AutomaticHookConfiguration.RequireInstallable(target, plan.ConfigPath);
            }
            ValidateScopes(plan.Targets, plan.LoaderHookExists);
            RemotePaths.ValidateRelayInstallation(Path.GetDirectoryName(plan.RelayPath)!, plan.RelayPath);
        }
        var clientPath = ClientPath(plan.RelayPath);
        if (runtime is not null && !File.Exists(clientPath))
            throw new InvalidDataException("Client executable is missing; repair the installation.");
        var taskName = ScheduledTaskDefinition.Name(plan.Config.MachineId);
        var task = scheduler.ReadXml(taskName);
        if (task is not null && !ScheduledTaskDefinition.IsOwned(task, plan.Config.MachineId,
            plan.Prior?.RelayPath ?? plan.RelayPath, plan.ConfigPath))
            throw new InvalidDataException("Legacy task is modified or unrelated; no changes made.");
        var startupName = IntegrationStartup.Name(plan.ConfigPath);
        var startupCommand = IntegrationStartup.Command(clientPath, plan.ConfigPath);
        var oldStartup = startup?.Read(startupName);
        if (oldStartup is not null && oldStartup != startupCommand && oldStartup != plan.Prior?.StartupCommand)
            throw new InvalidDataException("An unrelated startup command occupies the owned name.");
        var wasRunning = runtime is not null && (await Bounded(t => runtime.QueryAsync(plan.ConfigPath, t), token)).Running;
        if (!plan.IsRemoval)
            await DashboardConnection.VerifyBeforeApplyAsync(plan.Config, verifyDelivery, token);
        CheckFiles(plan.Changes);
        if (scheduler.ReadXml(taskName) != task || startup?.Read(startupName) != oldStartup)
            throw new InvalidDataException("Startup or legacy task changed during validation.");
        token.ThrowIfCancellationRequested();
        var changes = plan.Changes.ToList();
        if (!plan.IsRemoval && startup is not null)
        {
            var index = changes.FindIndex(c => Same(c.Path, ManifestPath(plan.ConfigPath)));
            var manifest = JsonSerializer.Deserialize<IntegrationManifest>(changes[index].After!, Protocol.Json)!;
            changes[index] = changes[index] with { After = Serialize(manifest with
            { ClientPath = clientPath, StartupName = startupName, StartupCommand = startupCommand }) };
            if (changes[index].After!.Length > 4194304)
                throw new InvalidDataException("Ownership manifest exceeds the bounded size; no files changed.");
        }
        var journal = new MultiTargetIntegrationJournal
        {
            ConfigPath = plan.ConfigPath, Changes = changes, TaskName = taskName,
            TaskBefore = task, TaskAfter = plan.IsRemoval ? task : null,
            StartupName = startupName, StartupBefore = oldStartup,
            StartupAfter = plan.IsRemoval || startup is null ? oldStartup : startupCommand
        };
        await ExecuteAsync(journal, RecoveryPath(plan.ConfigPath), token);
        if (runtime is null) return new(true, false, "Settings saved; runtime activation was not requested.");
        if (!wasRunning && (plan.IsRemoval || plan.Targets.Count == 0 || plan.Prior?.ClientPath is not null))
            return new(true, false, "Settings saved; Client is stopped. Use Start client explicitly.");
        try
        {
            var active = await Bounded(t => wasRunning ? runtime.ReloadAsync(plan.ConfigPath, t) :
                runtime.StartAsync(clientPath, plan.ConfigPath, t), token);
            var applied = active.Running && active.EffectiveRevision == Hash(plan.ConfigBytes);
            return new(true, applied, applied ? "Settings saved and applied to Client." :
                "Settings saved; use Start client or retry Apply. " + active.Message, active.EffectiveRevision);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            return new(true, false, "Settings saved, but Client activation failed. Use Start client. " + ex.Message);
        }
    }

    private static async Task<T> Bounded<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        try { return await action(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "The local Client did not respond in time. Exit Client from its tray menu and retry; " +
                "use Start client after applying settings. This is a local Client timeout, not a dashboard connection failure.", ex);
        }
    }
}
