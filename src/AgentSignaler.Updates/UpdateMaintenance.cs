using System.Collections.Immutable;
using System.Security.Cryptography;

namespace AgentSignaler.Updates;

public enum UpdateApplication { Dashboard, Client, Configurator }
public enum UpdateMaintenancePhase { Preparing, Ready, Servicing, Restarting, Completed, Failed, Cancelled }
public sealed record UpdateProcessIdentity(int ProcessId, long CreationTimeUtcTicks, string UserSid, int SessionId, string ExecutablePath);
public sealed record UpdateRestartContext(UpdateApplication Application, string ContextPath, bool Background);
public sealed record UpdateInstanceRegistration(int SchemaVersion, UpdateProcessIdentity Process, UpdateRestartContext Restart, string PipeName);
public sealed record UpdateMaintenanceGate(int SchemaVersion, Guid RunId, string InstallDirectory,
    UpdateProcessIdentity Owner, DateTimeOffset ExpiresAt, UpdateMaintenancePhase Phase,
    UpdateProcessIdentity Controller);
public sealed record UpdateMaintenanceOperation(Guid RunId, UpdateMaintenancePhase Phase, bool OwnerLive, DateTimeOffset ExpiresAt);

public sealed class UpdateMaintenanceBusyException(Guid? runId, UpdateMaintenancePhase? phase) : IOException("update_maintenance_busy")
{
    public Guid? RunId { get; } = runId;
    public UpdateMaintenancePhase? Phase { get; } = phase;
}

public sealed class UpdateMaintenanceRollbackException(Guid runId) : IOException("update_prepare_rollback_incomplete")
{
    public Guid RunId { get; } = runId;
}

public interface IUpdateProcessAdapter
{
    UpdateProcessIdentity Current { get; }
    UpdateProcessIdentity? Find(int processId);
    ImmutableArray<UpdateProcessIdentity> Enumerate(UpdateInstalledPackage installation);
}

public interface IUpdateMaintenanceParticipant
{
    // Drain/reject work and freeze new work. Never exit in Prepare.
    Task<bool> PrepareAsync(Guid runId, CancellationToken cancellationToken);
    Task CancelAsync(Guid runId, CancellationToken cancellationToken);
    // UI owners queue their existing graceful exit workflow here. Do not await disposal
    // of this registration from inside its own request callback.
    Task CommitExitAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IUpdateMaintenanceHealth
{
    // Local runtime readiness only; an offline remote receiver must not cause failure.
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);
}

public interface IUpdateMaintenanceCommitConsent
{
    // Atomically distinguish ordinary Exit from an authorized update shutdown before gate transfer.
    bool TryAcceptCommit(Guid runId);
}

public interface IUpdateMaintenanceTransport
{
    Task<IAsyncDisposable> ListenAsync(UpdateInstanceRegistration registration, IUpdateMaintenanceParticipant participant,
        CancellationToken cancellationToken);
    Task<bool> SendAsync(UpdateInstanceRegistration registration, Guid runId, string command, CancellationToken cancellationToken);
}

public static class UpdateMaintenanceValidation
{
    public const int MaximumParticipants = 32;
    public static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PreparationReservation = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan OperationLease = TimeSpan.FromMinutes(30);
    public static TimeSpan RequestTimeout(string command) => command == "prepare" ? PrepareTimeout : TimeSpan.FromSeconds(15);
    public static bool ShouldCancelPreparation(Guid runId, DateTimeOffset expiresAt, UpdateMaintenanceGate? gate, DateTimeOffset now) =>
        gate?.Phase != UpdateMaintenancePhase.Servicing && (gate is null || gate.RunId != runId || expiresAt <= now);
    public static string Executable(UpdateApplication application) => application switch
    {
        UpdateApplication.Dashboard => "AgentSignaler.Dashboard.exe",
        UpdateApplication.Client => "AgentSignaler.Client.exe",
        UpdateApplication.Configurator => "AgentSignaler.Configurator.exe",
        _ => throw new InvalidDataException("update_application_invalid")
    };

    public static void Process(UpdateProcessIdentity identity)
    {
        if (identity.ProcessId <= 0 || identity.CreationTimeUtcTicks <= 0 || identity.SessionId < 0 ||
            identity.CreationTimeUtcTicks > DateTime.MaxValue.Ticks ||
            string.IsNullOrWhiteSpace(identity.UserSid) || identity.UserSid.Length > 184 ||
            !identity.UserSid.StartsWith("S-1-", StringComparison.Ordinal)) throw new InvalidDataException("update_process_invalid");
        UpdatePath.Canonical(identity.ExecutablePath);
    }

    public static void Registration(UpdateInstanceRegistration registration, UpdateInstalledPackage installation)
    {
        Process(registration.Process);
        var restart = registration.Restart;
        if (registration.SchemaVersion != 1 || registration.Process.UserSid != installation.UserSid ||
            (installation.Package == UpdatePackage.Dashboard) != (restart.Application == UpdateApplication.Dashboard) ||
            !string.Equals(registration.Process.ExecutablePath, Path.Combine(installation.InstallDirectory, Executable(restart.Application)), StringComparison.OrdinalIgnoreCase) ||
            registration.PipeName != PipeName(installation, registration.Process))
            throw new InvalidDataException("update_registration_invalid");
        UpdatePath.Canonical(restart.ContextPath);
        if (restart.Application == UpdateApplication.Configurator && restart.Background)
            throw new InvalidDataException("update_restart_invalid");
    }

    public static string PipeName(UpdateInstalledPackage installation, UpdateProcessIdentity identity)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(installation.InstallDirectory.ToUpperInvariant() + "|" + identity.UserSid);
        return "AgentSignaler.Update.v1." + Convert.ToHexString(SHA256.HashData(bytes))[..24] + "." + identity.ProcessId + "." + identity.CreationTimeUtcTicks;
    }

    public static bool SameProcess(UpdateProcessIdentity? actual, UpdateProcessIdentity expected) => actual is not null &&
        actual.ProcessId == expected.ProcessId && actual.CreationTimeUtcTicks == expected.CreationTimeUtcTicks &&
        actual.UserSid == expected.UserSid && actual.SessionId == expected.SessionId &&
        string.Equals(actual.ExecutablePath, expected.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    public static void Gate(UpdateMaintenanceGate gate, UpdateInstalledPackage installation, DateTimeOffset now)
    {
        Process(gate.Owner);
        Process(gate.Controller);
        if (gate.SchemaVersion != 1 || gate.RunId == Guid.Empty || gate.Owner.UserSid != installation.UserSid ||
            gate.Controller.UserSid != gate.Owner.UserSid || gate.Controller.SessionId != gate.Owner.SessionId ||
            !UpdatePath.EqualsDirectory(gate.InstallDirectory, installation.InstallDirectory) ||
            gate.ExpiresAt.Offset != TimeSpan.Zero || gate.ExpiresAt > now + OperationLease ||
            !Enum.IsDefined(gate.Phase)) throw new InvalidDataException("update_gate_invalid");
        var controllerName = Path.GetFileName(gate.Controller.ExecutablePath);
        var allowed = installation.Package == UpdatePackage.Dashboard ? controllerName == "AgentSignaler.Dashboard.exe" :
            controllerName is "AgentSignaler.Client.exe" or "AgentSignaler.Configurator.exe";
        if (!allowed || !UpdatePath.EqualsDirectory(Path.GetDirectoryName(gate.Controller.ExecutablePath)!, installation.InstallDirectory))
            throw new InvalidDataException("update_gate_controller");
    }
}

public sealed class UpdateMaintenanceCoordinator(UpdateInstalledPackage installation, IUpdateStorage storage,
    IUpdateProcessAdapter processes, IUpdateMaintenanceTransport transport, TimeProvider timeProvider,
    Func<UpdateRestartAuthorization?>? restartAuthorization = null)
{
    public async Task<IAsyncDisposable> RegisterAsync(UpdateRestartContext restart, IUpdateMaintenanceParticipant participant,
        CancellationToken cancellationToken = default)
    {
        using var lease = storage.AcquireLease();
        if (!CheckStartupAllowedUnderLease(restart: restart, cancellationToken: cancellationToken)) throw new InvalidOperationException("update_maintenance_active");
        var registration = new UpdateInstanceRegistration(1, processes.Current, restart,
            UpdateMaintenanceValidation.PipeName(installation, processes.Current));
        UpdateMaintenanceValidation.Registration(registration, installation);
        foreach (var existingName in storage.List("instance-*.json", 32))
        {
            var existing = UpdateStrictJson.Read<UpdateInstanceRegistration>(storage.Read(existingName) ?? throw new InvalidDataException("update_registration_missing"));
            UpdateMaintenanceValidation.Registration(existing, installation);
            if (!UpdateMaintenanceValidation.SameProcess(processes.Find(existing.Process.ProcessId), existing.Process)) storage.Delete(existingName);
        }
        if (storage.List("instance-*.json", 32).Count >= 32) throw new InvalidDataException("update_participant_capacity");
        var endpoint = await transport.ListenAsync(registration, participant, cancellationToken).ConfigureAwait(false);
        var name = Name(registration.Process);
        try
        {
            storage.Write(name, UpdateStrictJson.Write(registration));
            storage.Delete(UpdateMaintenanceRestart.GrantName(registration.Process));
        }
        catch { await endpoint.DisposeAsync().ConfigureAwait(false); throw; }
        if (restartAuthorization is null) UpdateRestartAuthorization.ClearEnvironment();
        return new RegistrationLifetime(installation, storage, name, registration, endpoint);
    }

    // A participant may refresh its own safe launch context after freezing/draining Prepare.
    // Do not acquire maintenance.lock here: the coordinating process owns it during Prepare.
    public Task RefreshRestartContextAsync(UpdateRestartContext restart, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = processes.Current;
        var name = Name(process);
        var existing = UpdateStrictJson.Read<UpdateInstanceRegistration>(storage.Read(name) ?? throw new InvalidDataException("update_registration_missing"));
        UpdateMaintenanceValidation.Registration(existing, installation);
        if (!UpdateMaintenanceValidation.SameProcess(existing.Process, process)) throw new UnauthorizedAccessException("update_registration_owner");
        if (storage.Read("gate.json") is { } bytes)
        {
            var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(bytes);
            UpdateMaintenanceValidation.Gate(gate, installation, timeProvider.GetUtcNow());
            if (gate.Phase != UpdateMaintenancePhase.Preparing) throw new InvalidOperationException("update_registration_frozen");
        }
        var updated = existing with { Restart = restart };
        UpdateMaintenanceValidation.Registration(updated, installation);
        storage.Write(name, UpdateStrictJson.Write(updated));
        return Task.CompletedTask;
    }

    public bool CheckStartupAllowed()
    {
        using var lease = storage.AcquireLease();
        return CheckStartupAllowedUnderLease();
    }

    public UpdateMaintenanceOperation? ReadActiveOperation()
    {
        foreach (var name in new[] { "gate.json", "restart.json" })
        {
            if (storage.Read(name) is not { } bytes) continue;
            var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(bytes);
            var now = timeProvider.GetUtcNow();
            UpdateMaintenanceValidation.Gate(gate, installation, now);
            if (name == "restart.json" && gate.Phase != UpdateMaintenancePhase.Restarting)
                throw new InvalidDataException("update_restart_marker_invalid");
            var live = UpdateMaintenanceValidation.SameProcess(processes.Find(gate.Owner.ProcessId), gate.Owner);
            if (live || gate.Phase == UpdateMaintenancePhase.Servicing || gate.ExpiresAt > now)
                return new(gate.RunId, gate.Phase, live, gate.ExpiresAt);
        }
        return null;
    }

    private UpdateMaintenanceBusyException Busy()
    {
        var operation = ReadActiveOperation();
        return new(operation?.RunId, operation?.Phase);
    }

    private bool CheckStartupAllowedUnderLease(bool permitAuthorizedRestart = true, UpdateRestartContext? restart = null,
        CancellationToken cancellationToken = default)
    {
        UpdateMaintenanceGate? restarting = null;
        var bytes = storage.Read("gate.json");
        if (bytes is not null)
        {
            var gate = UpdateStrictJson.Read<UpdateMaintenanceGate>(bytes);
            UpdateMaintenanceValidation.Gate(gate, installation, timeProvider.GetUtcNow());
            if (gate.Phase == UpdateMaintenancePhase.Restarting) restarting = gate;
            else
            {
                // A live owner always blocks, even when its lease expired: MSI may still be running.
                if (UpdateMaintenanceValidation.SameProcess(processes.Find(gate.Owner.ProcessId), gate.Owner)) return false;
                // Dead updater does not prove MSI stopped. Require explicit recovery for servicing/unknown outcome.
                if (gate.Phase == UpdateMaintenancePhase.Servicing || gate.ExpiresAt > timeProvider.GetUtcNow()) return false;
                storage.Delete("gate.json");
            }
        }
        if (storage.Read("restart.json") is { } restartBytes)
        {
            var marker = UpdateStrictJson.Read<UpdateMaintenanceGate>(restartBytes);
            var now = timeProvider.GetUtcNow();
            UpdateMaintenanceValidation.Gate(marker, installation, now);
            if (marker.Phase != UpdateMaintenancePhase.Restarting) throw new InvalidDataException("update_restart_marker_invalid");
            if (restarting is not null && restarting != marker) throw new InvalidDataException("update_restart_gate_mismatch");
            var live = UpdateMaintenanceValidation.SameProcess(processes.Find(marker.Owner.ProcessId), marker.Owner);
            if (!live && marker.ExpiresAt <= now && restarting is null) storage.Delete("restart.json");
            else
            {
                if (!permitAuthorizedRestart || !live || marker.ExpiresAt <= now) return false;
                var authorized = UpdateMaintenanceRestart.Authorize((restartAuthorization ?? UpdateRestartAuthorization.FromEnvironment)(), marker,
                    installation, storage, processes.Current, restart, cancellationToken);
                return authorized && marker.ExpiresAt > timeProvider.GetUtcNow() &&
                    UpdateMaintenanceValidation.SameProcess(processes.Find(marker.Owner.ProcessId), marker.Owner);
            }
        }
        return restarting is null;
    }

    public async Task<UpdateMaintenanceTicket> PrepareAsync(CancellationToken cancellationToken = default)
    {
        IDisposable lease;
        try { lease = storage.AcquireLease(); }
        catch (IOException) { throw Busy(); }
        var prepared = new List<UpdateInstanceRegistration>();
        Guid runId = Guid.NewGuid();
        var gateWritten = false;
        try
        {
            if (!CheckStartupAllowedUnderLease(permitAuthorizedRestart: false)) throw Busy();
            var owner = processes.Current;
            UpdateMaintenanceValidation.Process(owner);
            if (owner.UserSid != installation.UserSid || !UpdatePath.EqualsDirectory(Path.GetDirectoryName(owner.ExecutablePath)!, installation.InstallDirectory))
                throw new UnauthorizedAccessException("update_initiator_invalid");
            var live = processes.Enumerate(installation);
            if (live.IsDefaultOrEmpty || live.Length > UpdateMaintenanceValidation.MaximumParticipants ||
                live.Select(p => p.ProcessId).Distinct().Count() != live.Length) throw new InvalidDataException("update_process_inventory_invalid");
            var registrations = storage.List("instance-*.json", 32).Select(name =>
                UpdateStrictJson.Read<UpdateInstanceRegistration>(storage.Read(name) ?? throw new InvalidDataException("update_registration_missing"))).ToArray();
            foreach (var registration in registrations) UpdateMaintenanceValidation.Registration(registration, installation);
            var affected = new List<UpdateInstanceRegistration>();
            foreach (var process in live)
            {
                if (process.UserSid != owner.UserSid || process.SessionId != owner.SessionId)
                    throw new UnauthorizedAccessException("update_participant_session");
                var matches = registrations.Where(r => UpdateMaintenanceValidation.SameProcess(r.Process, process)).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("update_participant_unsupported");
                affected.Add(matches[0]);
            }
            var gate = new UpdateMaintenanceGate(1, runId, installation.InstallDirectory, owner,
                timeProvider.GetUtcNow() + UpdateMaintenanceValidation.OperationLease, UpdateMaintenancePhase.Preparing, owner);
            storage.Write("gate.json", UpdateStrictJson.Write(gate));
            gateWritten = true;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(UpdateMaintenanceValidation.PrepareTimeout);
            foreach (var registration in affected)
            {
                // Include a possibly-prepared participant in rollback even when its reply is lost.
                prepared.Add(registration);
                if (!await transport.SendAsync(registration, runId, "prepare", budget.Token).ConfigureAwait(false))
                    throw new InvalidOperationException("update_prepare_refused");
                var refreshed = UpdateStrictJson.Read<UpdateInstanceRegistration>(storage.Read(Name(registration.Process)) ??
                    throw new InvalidDataException("update_registration_missing"));
                UpdateMaintenanceValidation.Registration(refreshed, installation);
                if (!UpdateMaintenanceValidation.SameProcess(refreshed.Process, registration.Process) || refreshed.PipeName != registration.PipeName)
                    throw new InvalidDataException("update_registration_changed");
                prepared[^1] = refreshed;
            }
            var after = processes.Enumerate(installation);
            if (after.Length != live.Length || after.Any(p => !live.Any(l => UpdateMaintenanceValidation.SameProcess(l, p))))
                throw new InvalidOperationException("update_inventory_changed");
            return new(installation, storage, processes, transport, timeProvider, lease, gate, prepared.ToImmutableArray());
        }
        catch (Exception failure)
        {
            var rollbackIncomplete = false;
            foreach (var registration in prepared)
            {
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { if (!await transport.SendAsync(registration, runId, "cancel", rollback.Token).ConfigureAwait(false)) rollbackIncomplete = true; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { rollbackIncomplete = true; }
            }
            if (gateWritten) storage.Delete("gate.json");
            lease.Dispose();
            if (rollbackIncomplete) throw new UpdateMaintenanceRollbackException(runId);
            if (failure is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                throw new IOException("update_prepare_timeout");
            throw;
        }
    }

    private static string Name(UpdateProcessIdentity process) => $"instance-{process.ProcessId}-{process.CreationTimeUtcTicks}.json";

    private sealed class RegistrationLifetime(UpdateInstalledPackage installation, IUpdateStorage storage, string name, UpdateInstanceRegistration registration,
        IAsyncDisposable endpoint) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await endpoint.DisposeAsync().ConfigureAwait(false);
            var bytes = storage.Read(name);
            if (bytes is null) return;
            var current = UpdateStrictJson.Read<UpdateInstanceRegistration>(bytes);
            UpdateMaintenanceValidation.Registration(current, installation);
            if (UpdateMaintenanceValidation.SameProcess(current.Process, registration.Process) && current.PipeName == registration.PipeName) storage.Delete(name);
        }
    }
}

public sealed class UpdateMaintenanceTicket : IAsyncDisposable
{
    private readonly UpdateInstalledPackage installation;
    private readonly IUpdateStorage storage;
    private readonly IUpdateProcessAdapter processes;
    private readonly IUpdateMaintenanceTransport transport;
    private readonly TimeProvider time;
    private IDisposable? lease;
    private UpdateMaintenanceGate gate;
    private bool committed;
    public Guid RunId => gate.RunId;
    internal UpdateInstalledPackage Installation => installation;
    public ImmutableArray<UpdateInstanceRegistration> Participants { get; }
    internal UpdateMaintenanceTicket(UpdateInstalledPackage installation, IUpdateStorage storage, IUpdateProcessAdapter processes,
        IUpdateMaintenanceTransport transport, TimeProvider time, IDisposable lease, UpdateMaintenanceGate gate,
        ImmutableArray<UpdateInstanceRegistration> participants)
    {
        this.installation = installation; this.storage = storage; this.processes = processes;
        this.transport = transport; this.time = time; this.lease = lease; this.gate = gate; Participants = participants;
    }

    // Only a readiness proof returned by the handoff launcher can authorize exit.
    public async Task CommitAsync(UpdateHandoffReady ready, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(lease is null, this);
        if (committed || ready.RunId != RunId || time.GetUtcNow() >= gate.ExpiresAt ||
            !UpdateMaintenanceValidation.SameProcess(processes.Find(ready.Updater.ProcessId), ready.Updater) ||
            ready.Updater.UserSid != gate.Owner.UserSid || ready.Updater.SessionId != gate.Owner.SessionId)
            throw new InvalidOperationException("update_readiness_invalid");
        var current = processes.Enumerate(installation);
        if (current.Length != Participants.Length || current.Any(p => !Participants.Any(r => UpdateMaintenanceValidation.SameProcess(p, r.Process))))
            throw new InvalidOperationException("update_inventory_changed");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        foreach (var participant in Participants)
            if (!await transport.SendAsync(participant, RunId, "reserve", budget.Token).ConfigureAwait(false))
                throw new InvalidOperationException("update_commit_declined");
        current = processes.Enumerate(installation);
        if (current.Length != Participants.Length || current.Any(p => !Participants.Any(r => UpdateMaintenanceValidation.SameProcess(p, r.Process))))
            throw new InvalidOperationException("update_inventory_changed");
        if (time.GetUtcNow() >= gate.ExpiresAt || !UpdateMaintenanceValidation.SameProcess(processes.Find(ready.Updater.ProcessId), ready.Updater))
            throw new InvalidOperationException("update_readiness_invalid");
        gate = gate with { Owner = ready.Updater, Phase = UpdateMaintenancePhase.Servicing };
        storage.Write("gate.json", UpdateStrictJson.Write(gate));
        committed = true;
        // Preserve gate on any partial shutdown. Never roll back or release an updater's gate.
        lease.Dispose(); lease = null;
        foreach (var participant in Participants.OrderBy(r => r.Process.ProcessId == processes.Current.ProcessId))
            if (!await transport.SendAsync(participant, RunId, "commit", budget.Token).ConfigureAwait(false))
                throw new InvalidOperationException("update_commit_failed");
    }

    public async ValueTask DisposeAsync()
    {
        if (lease is null) return;
        var rollbackIncomplete = false;
        try
        {
            if (!committed)
            {
                foreach (var participant in Participants)
                {
                    using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { if (!await transport.SendAsync(participant, RunId, "cancel", budget.Token).ConfigureAwait(false)) rollbackIncomplete = true; }
                    catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { rollbackIncomplete = true; }
                }
                storage.Delete("gate.json");
            }
        }
        finally { lease.Dispose(); lease = null; }
        if (rollbackIncomplete) throw new UpdateMaintenanceRollbackException(RunId);
    }
}