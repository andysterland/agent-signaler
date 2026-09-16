using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed record VerificationConfirmation(bool ActualIdeWorkflow, bool OtherHostsExcluded,
    bool LoaderIsolationConfirmed, bool ExecutionContractConfirmed, string Experience, string ReloadInstructions);

public sealed record ProbeObservation(string Event, string SessionHash);
public sealed record ProbeEvidence(string[] Events, int Acknowledgements, bool StableSessionObserved);
public sealed record HookVerificationPlan(Guid ProbeId, IntegrationTarget Target, string ConfigPath,
    string HookPath, byte[] HookBytes, byte[]? OriginalSettings, byte[]? InstalledSettings,
    string InstallationFingerprint = "")
{
    public string Preview => $"DIAGNOSTIC ONLY: {Target.DisplayName}\nIdentity: {Target.InstallationId}\n" +
        $"Version: {Target.HostVersion}; adapter: {Target.AdapterVersion}\nCandidate scope: {Target.HookDirectory}\n" +
        $"CREATE {HookPath}\n{Encoding.UTF8.GetString(HookBytes)}\n" +
        (Target.SettingsPath is null ? "" : $"UPDATE {Target.SettingsPath}\n{Encoding.UTF8.GetString(InstalledSettings!)}\n") +
        "Requires explicit consent and invalidates this target's previous verification until the new test completes. No prompts, tools, transcripts or raw stdin are recorded. " +
        "An already-running tray Client acknowledges this temporary probe through local IPC; it does not send probe activity to Dashboard. " +
        "Use only this identified IDE/profile, excluding other loaders (including standalone/bundled CLI). " +
        "In its actual agent experience start a NEW session, submit a harmless request that reads a disposable fixture, " +
        "allow the tool to finish, and wait for execution to stop. Inspect effective hook locations in the IDE. " +
        "A bundled CLI command is NOT an IDE test. Confirm direct-exec (Visual Studio) or cmd.exe (VS Code), " +
        "three-second timeout, reload requirements, and exactly one observer invocation per event. " +
        "Cancel/Complete removes unchanged diagnostic artifacts; interrupted cleanup is recoverable.";
}

public sealed record HookProbeJournal(int Version, HookVerificationPlan Plan, DateTimeOffset ExpiresAtUtc,
    List<ProbeObservation> Observations);
public sealed record HookCompatibilityRecord(int Version, string Fingerprint, IntegrationTarget Target,
    DateTimeOffset VerifiedAtUtc, VerificationConfirmation Confirmation);

public static class HookVerification
{
    private static readonly JsonSerializerOptions Json = new(Protocol.Json) { WriteIndented = true };
    private const int MaximumJournalBytes = 1048576;
    private static string DirectoryPath(string configPath) => Path.Combine(Path.GetDirectoryName(configPath)!, "hook-verification");
    private static string ProbePath(string configPath, Guid id) => Path.Combine(DirectoryPath(configPath), $"probe-{id:N}.json");
    private static string RecordPath(string configPath, string targetId) =>
        Path.Combine(DirectoryPath(configPath), $"compatibility-{IntegrationScope.Id("target", targetId)}.json");

    public static HookVerificationPlan Preview(IntegrationTarget target, string configPath, string relayPath)
    {
        target.Validate();
        if (target.Kind == "copilot-cli" || target.Capability is IntegrationCapability.NotInstalled or
            IntegrationCapability.BlockedByPolicy or IntegrationCapability.DiscoveryFailed)
            throw new InvalidOperationException("Select an installed IDE candidate. Blocked policy must not be overridden.");
        if (target.HostVersion == "unknown" || target.ExecutablePath is null || !File.Exists(target.ExecutablePath))
            throw new InvalidOperationException("Select this profile's actual installed IDE executable and establish its version before verifying.");
        RemotePaths.ValidateRelayInstallation(Path.GetDirectoryName(relayPath)!, relayPath);
        _ = RemoteConfiguration.Load(configPath);
        var id = Guid.NewGuid();
        var hookPath = Path.Combine(target.HookDirectory, $"agent-signaler-probe-{id:N}.json");
        var bytes = HookAdapters.Generate(target, relayPath, configPath, id);
        byte[]? original = null;
        byte[]? installed = null;
        if (target.SettingsPath is { } settings)
        {
            original = File.Exists(settings) ? AtomicFile.ReadBounded(settings, 262144) : null;
            installed = JsoncHookSettings.AddLocation(original ?? Encoding.UTF8.GetBytes("{}"), target.HookDirectory);
        }
        return new(id, target, configPath, hookPath, bytes, original, installed, Fingerprint(target));
    }

    public static void Begin(HookVerificationPlan plan, bool consent)
    {
        if (!consent) throw new InvalidOperationException("Explicit consent is required before diagnostic hooks or IDE settings are modified.");
        plan.Target.Validate();
        if (plan.InstallationFingerprint != Fingerprint(plan.Target))
            throw new InvalidDataException("IDE installation changed since preview; refresh and verify the current build.");
        if (plan.ProbeId == Guid.Empty || plan.HookPath != Path.Combine(plan.Target.HookDirectory, $"agent-signaler-probe-{plan.ProbeId:N}.json"))
            throw new InvalidDataException("Invalid verification preview.");
        var expected = HookAdapters.Generate(plan.Target, RemoteConfiguration.Load(plan.ConfigPath).RelayPath ??
            throw new InvalidDataException("Save the co-installed Relay path before verifying."), plan.ConfigPath, plan.ProbeId);
        if (!expected.AsSpan().SequenceEqual(plan.HookBytes))
            throw new InvalidDataException("Verification preview changed; preview again.");
        using var held = AtomicFile.Acquire(plan.ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1));
        if (HasPendingCleanup(plan.ConfigPath))
            throw new InvalidOperationException("A diagnostic probe is pending. Cancel or recover its owned artifacts before starting another.");
        if (File.Exists(plan.HookPath)) throw new InvalidDataException("Diagnostic hook destination is occupied.");
        if (plan.Target.SettingsPath is { } settings)
        {
            CheckCurrent(settings, plan.OriginalSettings);
            var settingsBytes = JsoncHookSettings.AddLocation(plan.OriginalSettings ?? Encoding.UTF8.GetBytes("{}"), plan.Target.HookDirectory);
            if (plan.InstalledSettings is null || !settingsBytes.AsSpan().SequenceEqual(plan.InstalledSettings))
                throw new InvalidDataException("Diagnostic settings preview changed.");
        }
        var journal = new HookProbeJournal(1, plan, DateTimeOffset.UtcNow.AddMinutes(30), []);
        var pending = plan.Target with { Capability = IntegrationCapability.VerificationRequired, SupportedEvents = [],
            Reason = "IDE verification is incomplete. Complete the consented test or retry after cancelling/recovering the diagnostic probe.",
            Provenance = "verification pending" };
        AtomicFile.Write(RecordPath(plan.ConfigPath, plan.Target.Id), JsonSerializer.SerializeToUtf8Bytes(
            new HookCompatibilityRecord(1, Fingerprint(pending), pending, DateTimeOffset.UtcNow,
                new(false, false, false, false, "not verified", "complete or retry verification")), Json));
        AtomicFile.Write(ProbePath(plan.ConfigPath, plan.ProbeId), JsonSerializer.SerializeToUtf8Bytes(journal, Json));
        try
        {
            AtomicFile.Write(plan.HookPath, plan.HookBytes);
            if (plan.Target.SettingsPath is { } path) AtomicFile.Write(path, plan.InstalledSettings!);
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex))
        {
            try { Cleanup(journal); }
            catch (Exception cleanup) when (RemoteFailure.IsExpected(cleanup))
            {
                throw new AggregateException("Diagnostic setup failed and cleanup is pending. Recover owned probe changes before retrying.", ex, cleanup);
            }
            throw;
        }
    }

    public static bool AcceptProbe(string configPath, Guid probeId, AgentEvent kind, HookData hook)
    {
        if (probeId == Guid.Empty || hook.Source is not { IsValid: true } source)
            return false;
        using var held = AtomicFile.Acquire(ProbePath(configPath, probeId) + ".lock", TimeSpan.FromMilliseconds(100));
        var journal = ReadJournal(configPath, probeId);
        if (journal.ExpiresAtUtc < DateTimeOffset.UtcNow || journal.Observations.Count >= 128 ||
            source.Kind != journal.Plan.Target.Kind || source.ScopeId != journal.Plan.Target.ScopeId ||
            source.Version != journal.Plan.Target.HostVersion || !RemoteConfiguration.ValidText(hook.SessionId, 128))
            return false;
        var eventName = EventName(source.Kind, kind);
        if (eventName is null || !HookAdapters.Events(source.Kind).Contains(eventName, StringComparer.Ordinal))
            return false;
        journal.Observations.Add(new(eventName, Hash(Encoding.UTF8.GetBytes(hook.SessionId))));
        AtomicFile.Write(ProbePath(configPath, probeId), JsonSerializer.SerializeToUtf8Bytes(journal, Json));
        return true;
    }

    public static ProbeEvidence ReadEvidence(string configPath, Guid probeId)
    {
        var journal = ReadJournal(configPath, probeId);
        var required = RequiredEvents(journal.Plan.Target.Kind);
        return new(journal.Observations.Select(o => o.Event).Distinct(StringComparer.Ordinal).ToArray(),
            journal.Observations.Count, journal.Observations.GroupBy(o => o.SessionHash)
                .Any(g => required.All(e => g.Any(o => o.Event == e))));
    }

    public static IntegrationTarget Complete(HookVerificationPlan plan, VerificationConfirmation confirmation)
    {
        if (!confirmation.ActualIdeWorkflow || !confirmation.OtherHostsExcluded ||
            !confirmation.LoaderIsolationConfirmed || !confirmation.ExecutionContractConfirmed ||
            !RemoteConfiguration.ValidText(confirmation.Experience, 128) ||
            !RemoteConfiguration.ValidText(confirmation.ReloadInstructions, 256))
            throw new InvalidOperationException("Confirm the actual isolated IDE experience, effective loaders, execution contract and reload steps. CLI execution alone cannot verify an IDE.");
        using var held = AtomicFile.Acquire(plan.ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1));
        var journal = ReadJournal(plan.ConfigPath, plan.ProbeId);
        if (journal.Plan.InstallationFingerprint != Fingerprint(plan.Target))
            throw new InvalidOperationException("IDE installation changed during verification. Cancel this probe and repeat with the current build.");
        if (journal.Plan.Target != plan.Target && Fingerprint(journal.Plan.Target) != Fingerprint(plan.Target))
            throw new InvalidDataException("Verification target changed.");
        var evidence = ReadEvidence(plan.ConfigPath, plan.ProbeId);
        if (journal.ExpiresAtUtc < DateTimeOffset.UtcNow || !evidence.StableSessionObserved)
            throw new InvalidOperationException("Verification incomplete or expired. Require start, prompt, pre-tool, post-tool and stop from one stable IDE session. Target remains unverified.");
        var allEvents = HookAdapters.Events(plan.Target.Kind);
        var verified = plan.Target with
        {
            Capability = evidence.Events.Length == allEvents.Count ? IntegrationCapability.Verified : IntegrationCapability.PartiallySupported,
            SupportedEvents = allEvents.Where(e => evidence.Events.Contains(e, StringComparer.Ordinal)).ToArray(),
            Provenance = "verified IDE workflow",
            Reason = $"Verified only for {confirmation.Experience}. Reload: {confirmation.ReloadInstructions}. " +
                (plan.Target.Kind == "visual-studio" ? "Shared hook scope: source is Visual Studio, not an inferred instance. " : "") +
                "Only observed events enabled; policy, profile/workspace overrides and other agent modes can prevent delivery. " +
                (plan.Target.Kind == "vscode" ? "Stop means last-observed Waiting, never success or session termination." : "")
        };
        Cleanup(journal);
        AtomicFile.Write(RecordPath(plan.ConfigPath, plan.Target.Id), JsonSerializer.SerializeToUtf8Bytes(
            new HookCompatibilityRecord(1, Fingerprint(verified), verified, DateTimeOffset.UtcNow, confirmation), Json));
        return verified;
    }

    public static IntegrationTarget Resolve(IntegrationTarget target, string configPath)
    {
        if (target.Kind == "copilot-cli" || target.Capability is IntegrationCapability.NotInstalled or
            IntegrationCapability.BlockedByPolicy or IntegrationCapability.DiscoveryFailed) return target;
        var path = RecordPath(configPath, target.Id);
        if (!File.Exists(path)) return target;
        try
        {
            var record = JsonSerializer.Deserialize<HookCompatibilityRecord>(AtomicFile.ReadBounded(path, 65536), Protocol.Json)
                ?? throw new InvalidDataException("Missing compatibility record.");
            if (record.Target is null || record.Confirmation is null)
                throw new InvalidDataException("Missing compatibility facts.");
            if (record.Version != 1 || record.Fingerprint != Fingerprint(target))
                return target with { Capability = IntegrationCapability.VerificationRequired,
                    Reason = "IDE/host version, adapter or scope changed. Repeat the real IDE test; old verification is not applicable." };
            record.Target.Validate();
            if (record.Fingerprint != Fingerprint(record.Target) ||
                record.Target.CanInstall && (!record.Confirmation.ActualIdeWorkflow ||
                    !record.Confirmation.OtherHostsExcluded || !record.Confirmation.LoaderIsolationConfirmed ||
                    !record.Confirmation.ExecutionContractConfirmed ||
                    !RequiredEvents(target.Kind).All(e => record.Target.SupportedEvents.Contains(e, StringComparer.Ordinal))))
                throw new InvalidDataException("Compatibility evidence is inconsistent.");
            return target with { Capability = record.Target.Capability, SupportedEvents = record.Target.SupportedEvents,
                Provenance = record.Target.Provenance, Reason = record.Target.Reason };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return target with { Capability = IntegrationCapability.VerificationFailed,
                Reason = "Compatibility evidence is unreadable or invalid; repeat verification." };
        }
    }

    public static void RequireVerified(IntegrationTarget target, string configPath)
    {
        target.Validate();
        if (!target.CanInstall || target.Capability == IntegrationCapability.Configured)
            throw new InvalidOperationException("Integration is unavailable, blocked, or still requires verification.");
        if (target.Kind == "copilot-cli")
        {
            return;
        }
        if (target.Kind == "vscode" && !Directory.Exists(Path.GetDirectoryName(target.SettingsPath!)))
            throw new InvalidOperationException("The verified local VS Code profile was removed. Restore and reverify it before reporting or repair.");
        var candidate = target with { Capability = IntegrationCapability.VerificationRequired, SupportedEvents = [] };
        var verified = Resolve(candidate, configPath);
        if (!verified.CanInstall || !verified.SupportedEvents.SequenceEqual(target.SupportedEvents, StringComparer.Ordinal))
            throw new InvalidOperationException("Actual IDE hook location and event behavior are not verified for this installation/scope. Run Verify integration first.");
    }

    public static bool HasPendingCleanup(string configPath) =>
        Directory.Exists(DirectoryPath(configPath)) && Directory.EnumerateFiles(DirectoryPath(configPath), "probe-*.json").Any();

    public static IntegrationTarget RecordUnavailable(IntegrationTarget target, string configPath,
        IntegrationCapability capability, string reason)
    {
        target.Validate();
        if (target.Kind == "copilot-cli" || capability is not (IntegrationCapability.BlockedByPolicy or IntegrationCapability.VerificationFailed) ||
            !RemoteConfiguration.ValidText(reason, 256))
            throw new InvalidDataException("Choose a bounded policy/verification diagnostic, not payload or error content.");
        var unavailable = target with { Capability = capability, SupportedEvents = [], Reason = reason,
            Provenance = "user-observed IDE capability" };
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        AtomicFile.Write(RecordPath(configPath, target.Id), JsonSerializer.SerializeToUtf8Bytes(
            new HookCompatibilityRecord(1, Fingerprint(unavailable), unavailable, DateTimeOffset.UtcNow,
                new(false, false, false, false, "not verified", "resolve policy/capability and verify again")), Json));
        return unavailable;
    }

    public static void Cancel(HookVerificationPlan plan)
    {
        using var held = AtomicFile.Acquire(plan.ConfigPath + ".integration.lock", TimeSpan.FromSeconds(1));
        if (File.Exists(ProbePath(plan.ConfigPath, plan.ProbeId))) Cleanup(ReadJournal(plan.ConfigPath, plan.ProbeId));
    }

    public static void Recover(string configPath)
    {
        using var held = AtomicFile.Acquire(configPath + ".integration.lock", TimeSpan.FromSeconds(1));
        if (!Directory.Exists(DirectoryPath(configPath))) return;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath(configPath), "probe-*.json").Take(64))
        {
            var journal = JsonSerializer.Deserialize<HookProbeJournal>(AtomicFile.ReadBounded(path, MaximumJournalBytes), Protocol.Json)
                ?? throw new InvalidDataException("Invalid pending probe journal.");
            if (journal.Plan is null || !SameConfig(journal.Plan.ConfigPath, configPath) ||
                !string.Equals(path, ProbePath(configPath, journal.Plan.ProbeId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Diagnostic recovery ownership mismatch.");
            Cleanup(journal);
        }
    }

    private static HookProbeJournal ReadJournal(string configPath, Guid id)
    {
        var journal = JsonSerializer.Deserialize<HookProbeJournal>(AtomicFile.ReadBounded(ProbePath(configPath, id), MaximumJournalBytes), Protocol.Json)
            ?? throw new InvalidDataException("Missing diagnostic journal.");
        if (journal.Version != 1 || journal.Plan is null || journal.Plan.Target is null ||
            !SameConfig(journal.Plan.ConfigPath, configPath) || journal.Plan.ProbeId != id ||
            journal.Observations is null || journal.Observations.Count > 128 ||
            journal.Observations.Any(o => o is null || !RemoteConfiguration.ValidText(o.Event, 64) ||
                o.SessionHash is null || o.SessionHash.Length != 64 || o.SessionHash.Any(c => !char.IsAsciiHexDigit(c))))
            throw new InvalidDataException("Invalid diagnostic journal.");
        return journal;
    }

    private static void Cleanup(HookProbeJournal journal)
    {
        var plan = journal.Plan;
        using var held = AtomicFile.Acquire(ProbePath(plan.ConfigPath, plan.ProbeId) + ".lock", TimeSpan.FromMilliseconds(250));
        plan.Target.Validate();
        if (plan.HookPath != Path.Combine(plan.Target.HookDirectory, $"agent-signaler-probe-{plan.ProbeId:N}.json"))
            throw new InvalidDataException("Diagnostic hook ownership mismatch.");
        if (File.Exists(plan.HookPath))
        {
            CheckCurrent(plan.HookPath, plan.HookBytes);
            File.Delete(plan.HookPath);
        }
        if (plan.Target.SettingsPath is { } settings && File.Exists(settings))
        {
            var current = AtomicFile.ReadBounded(settings, 262144);
            if (current.AsSpan().SequenceEqual(plan.InstalledSettings))
            {
                if (plan.OriginalSettings is null) File.Delete(settings);
                else AtomicFile.Write(settings, plan.OriginalSettings);
            }
            else if (plan.OriginalSettings is null || !plan.OriginalSettings.AsSpan().SequenceEqual(plan.InstalledSettings))
            {
                var removed = JsoncHookSettings.RemoveLocation(current, plan.Target.HookDirectory);
                if (!current.AsSpan().SequenceEqual(removed)) AtomicFile.Write(settings, removed);
            }
        }
        File.Delete(ProbePath(plan.ConfigPath, plan.ProbeId));
    }

    private static void CheckCurrent(string path, byte[]? expected)
    {
        var current = File.Exists(path) ? AtomicFile.ReadBounded(path, MaximumJournalBytes) : null;
        if ((current is null) != (expected is null) || current is not null && !current.AsSpan().SequenceEqual(expected))
            throw new InvalidDataException("A previewed/owned artifact changed. Preserve it and create a new preview or resolve pending cleanup.");
    }

    private static string Fingerprint(IntegrationTarget target)
    {
        var executable = target.ExecutablePath is { } path && File.Exists(path) ? new FileInfo(path) : null;
        return Hash(Encoding.UTF8.GetBytes(string.Join("\n", target.Id, target.Kind, target.InstallationId,
            target.HostVersion, target.AdapterVersion, target.ScopeId, IntegrationScope.CanonicalDirectory(target.HookDirectory).ToUpperInvariant(),
            target.SettingsPath?.ToUpperInvariant(), executable?.FullName.ToUpperInvariant(), executable?.Length.ToString(),
            executable?.LastWriteTimeUtc.Ticks.ToString())));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static bool SameConfig(string left, string right) =>
        ClientIdentity.CanonicalPath(left) == ClientIdentity.CanonicalPath(right);
    private static string[] RequiredEvents(string kind) => kind == "vscode"
        ? ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop"]
        : ["sessionStart", "userPromptSubmitted", "preToolUse", "postToolUse", "agentStop"];

    private static string? EventName(string source, AgentEvent kind) => source == "vscode" ? kind switch
    {
        AgentEvent.SessionStart => "SessionStart",
        AgentEvent.UserPromptSubmitted => "UserPromptSubmit",
        AgentEvent.PreToolUse => "PreToolUse",
        AgentEvent.PostToolUse => "PostToolUse",
        AgentEvent.ExecutionStopped => "Stop",
        _ => null
    } : JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());
}
