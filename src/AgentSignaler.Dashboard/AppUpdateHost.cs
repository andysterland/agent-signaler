using System.Security.Principal;
using AgentSignaler.Updates;

namespace AgentSignaler.AppUpdates;

internal sealed record UpdateRecoveryNotice(Guid RunId, string Message);

internal sealed class UpdateStartupRefusedException(string code, string userMessage) : Exception(code)
{
    public string UserMessage { get; } = userMessage;
}

// Application composition boundary. Inventory/storage/process work never runs in
// a XAML handler or native window procedure.
internal sealed class AppUpdateHost : IAsyncDisposable, IDisposable
{
    private readonly UpdateStorage? storage;
    private readonly UpdateMaintenanceCoordinator? maintenance;
    private IAsyncDisposable? registration;
    private bool disposed;
    public UpdateInstalledPackage? Installation { get; }

    private AppUpdateHost(UpdateInstalledPackage? installation, UpdateStorage? storage,
        UpdateMaintenanceCoordinator? maintenance)
    {
        Installation = installation;
        this.storage = storage;
        this.maintenance = maintenance;
    }

    public static AppUpdateHost Open(UpdatePackage package)
    {
        try { return OpenCore(package); }
        catch (UpdateStartupRefusedException) { throw; }
        catch (Exception error) when (IsExpectedMaintenanceFailure(error))
        { throw StartupRefusal("update_maintenance_unverified", "Installation ownership or maintenance state could not be verified."); }
    }

    private static AppUpdateHost OpenCore(UpdatePackage package)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("update_user_unavailable");
        var requiresVerifiedInventory = UpdateStorage.HasMaintenanceRecords(local, directory);
        var installed = new UpdateInventory(new WindowsUpdateInventoryAdapter(), sid, local)
            .FindAsync(package).GetAwaiter().GetResult();
        // MSI registration proves the actual installation scope, including a
        // custom INSTALLFOLDER. A different source/portable copy cannot service it.
        if (installed is null || !UpdatePath.EqualsDirectory(directory, installed.InstallDirectory))
        {
            if (requiresVerifiedInventory || UpdateStorage.HasMaintenanceRecords(local, directory))
                throw StartupRefusal("update_maintenance_inventory_unavailable",
                    "This executable directory has retained maintenance state, but its MSI installation could not be verified. " +
                    "Servicing may still be active. It cannot be started as a portable copy or granted an automatic restart.");
            return new(null, null, null);
        }
        var storage = new UpdateStorage(local, installed.InstallDirectory);
        try
        {
            var processes = new WindowsUpdateProcessAdapter();
            var transport = new WindowsUpdateMaintenanceTransport(installed, storage, processes, TimeProvider.System);
            var maintenance = new UpdateMaintenanceCoordinator(installed, storage, processes, transport, TimeProvider.System);
            if (!maintenance.CheckStartupAllowed()) throw ReadStartupRefusal(installed, storage, maintenance);
            return new(installed, storage, maintenance);
        }
        catch { storage.Dispose(); throw; }
    }

    private static UpdateStartupRefusedException ReadStartupRefusal(UpdateInstalledPackage installation,
        UpdateStorage storage, UpdateMaintenanceCoordinator maintenance)
    {
        try
        {
            // These APIs only observe bounded validated records; they do not
            // clear a gate or authorize retry, even when the updater has exited.
            var operation = maintenance.ReadActiveOperation();
            var results = new UpdateHandoffRecovery(installation, storage).ReadResults();
            var unresolved = results.Count(result => result.RequiresManualRecovery);
            var recoveryRequired = unresolved != 0 || operation is { OwnerLive: false, Phase: UpdateMaintenancePhase.Servicing };
            var description = recoveryRequired
                ? "Windows Installer servicing is unresolved or its outcome requires manual recovery. An exited updater does not prove that MSI has stopped."
                : "An update or coordinated application restart is active. Wait for the updater to finish before opening this application.";
            if (operation is not null) description += $" Operation: {operation.RunId:N}.";
            description += $" Retained verified results: {results.Length}; requiring manual recovery: {unresolved}. No chronological order is implied.";
            return StartupRefusal(recoveryRequired ? "update_recovery_required" : "update_maintenance_active", description);
        }
        catch (Exception error) when (IsExpectedMaintenanceFailure(error))
        { return StartupRefusal("update_maintenance_unverified", "Maintenance or retained recovery information could not be verified."); }
    }

    private static UpdateStartupRefusedException StartupRefusal(string code, string description) => new(code,
        $"Startup was refused ({code}). {description}\n\n" +
        "Inspect the updater progress window and retained result.json files under %LOCALAPPDATA%\\AgentSignaler\\Updates. " +
        "Do not start another installer, retry an unknown MSI operation, or delete maintenance gates. " +
        "If no servicing is active, follow the signed MSI recovery or repair instructions. " +
        "No existing application was activated, and no application runtime or installer was started by this request.\n\n" +
        "Recovery instructions: https://github.com/andysterland/agent-signaler/blob/main/docs/user-guide.md#application-updates");

    private static bool IsExpectedMaintenanceFailure(Exception error) => error is IOException or InvalidDataException or
        System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or
        System.Security.SecurityException or System.ComponentModel.Win32Exception or OverflowException;

    public async Task RegisterAsync(UpdateRestartContext restart, IUpdateMaintenanceParticipant participant,
        CancellationToken cancellationToken = default)
    {
        if (maintenance is null) return;
        if (registration is not null) throw new InvalidOperationException("update_already_registered");
        registration = await maintenance.RegisterAsync(restart, participant, cancellationToken).ConfigureAwait(false);
    }

    public Task RefreshRestartContextAsync(UpdateRestartContext restart, CancellationToken cancellationToken) =>
        maintenance?.RefreshRestartContextAsync(restart, cancellationToken)
        ?? throw new InvalidOperationException("update_maintenance_unavailable");

    public Task<IReadOnlyList<UpdateRecoveryNotice>> ReadRecoveryAsync(CancellationToken cancellationToken)
    {
        if (Installation is null || storage is null) return Task.FromResult<IReadOnlyList<UpdateRecoveryNotice>>([]);
        return Task.Run<IReadOnlyList<UpdateRecoveryNotice>>(() =>
        {
            var notices = new List<UpdateRecoveryNotice>();
            // ListRuns/ReadResults are bounded and validate ownership/schema.
            // GUID enumeration is not chronological: never call this a last result.
            foreach (var recovery in new UpdateHandoffRecovery(Installation, storage).ReadResults())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = recovery.Result;
                if (!recovery.RequiresManualRecovery && result.Outcome == UpdateServicingOutcome.Completed &&
                    result.Restarts.All(restart => restart.State == UpdateRestartState.Ready)) continue;
                var explanation = recovery.RequiresManualRecovery
                    ? "Servicing may still be active or its outcome is unknown. Do not retry, start another installer, or delete maintenance files. Review updater progress and recovery instructions."
                    : result.Outcome == UpdateServicingOutcome.RebootRequired
                        ? "A Windows restart is required. Affected apps were not automatically restarted. Restart Windows when ready; Agent Signaler will not reboot automatically."
                        : result.Outcome == UpdateServicingOutcome.Completed
                            ? "Installation completed, but one or more app restarts were not ready. Review updater results before explicitly starting the apps you intend to use."
                            : "The update did not complete. Some apps may remain stopped. Review the retained updater result and signed MSI recovery instructions before retrying.";
                notices.Add(new(recovery.RunId, $"Operation {recovery.RunId:N}, version {result.TargetVersion}: {explanation}"));
            }
            return notices.ToArray();
        }, cancellationToken);
    }

    public async Task<string?> BeginUpdateAsync(ReleaseCandidate candidate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Installation is null)
            return "In-app MSI servicing is unavailable for this portable or source installation. The app has not closed. Use the signed MSI update instructions.";
        if (registration is null || disposed) return "Safe update maintenance is unavailable. The app has not closed. Exit and reopen the app or repair its installation.";

        // Revalidate exactly the offered identity. Never replace the user's
        // selection with whatever happens to be newest during preflight.
        using var http = ReleaseDiscovery.CreatePublicHttpClient();
        UpdatePinnedRelease pinned;
        try { pinned = await new ReleasePinning(http).PinAsync(candidate, Installation.Package, token).ConfigureAwait(false); }
        catch (Exception error) when (error is UpdateDiscoveryException or InvalidDataException)
        {
            return "The selected release could not be verified (update_release_validation_failed). The app has not closed. Check for updates again before retrying.";
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = await new UpdateInventory(new WindowsUpdateInventoryAdapter(), Installation.UserSid, local)
            .FindAsync(Installation.Package, token).ConfigureAwait(false);
        if (current != Installation)
            return "The installed package changed. The app has not closed. Exit, reopen and check for updates again.";
        if (pinned.Candidate.Version <= new MsiReleaseVersion(Installation.Version.Major, Installation.Version.Minor, Installation.Version.Build))
            return "The selected release is not newer than this installation. No update was started. Check for updates again.";

        var processes = new WindowsUpdateProcessAdapter();
        using var adapter = new WindowsUpdateHandoffAdapter(processes);
        var launcher = new UpdateHandoffLauncher(storage!, processes, new WindowsUpdateScriptTrust(), adapter, TimeProvider.System);
        // Prove the complete installed, signed dependency set before disrupting
        // any participant. Source/unsigned/partial payloads cannot consent exit.
        var scripts = await launcher.ReadInstalledPinsAsync(Installation, token).ConfigureAwait(false);
        UpdateMaintenanceTicket prepared;
        try { prepared = await maintenance!.PrepareAsync(token).ConfigureAwait(false); }
        catch (UpdateMaintenanceBusyException busy)
        {
            return busy.RunId is { } runId
                ? $"Update operation {runId:N} is already active{(busy.Phase is { } phase ? $" ({phase})" : "")} (update_maintenance_busy). " +
                    "Use its existing updater progress window or inspect retained recovery results. Do not start another installer. This request did not close the app or start a new installer."
                : "Maintenance information is temporarily busy while another operation acquires or publishes its lease (update_maintenance_busy). " +
                    "Wait briefly and recheck the existing updater progress or recovery results. Do not start another installer. This request did not close the app or start an installer.";
        }
        catch (UpdateMaintenanceRollbackException rollback)
        {
            return $"Preparation for operation {rollback.RunId:N} stopped, but not every application confirmed cancellation (update_prepare_rollback_incomplete). " +
                "Some windows may remain temporarily unavailable. Inspect updater progress and retained recovery results before taking further action; do not start another installer or delete maintenance state. This request did not start an installer.";
        }
        await using var ticket = prepared;
        var request = new UpdateHandoffRequest(Installation, pinned.Candidate, scripts, pinned.ChecksumSha256, pinned.PackageSha256);
        var ready = await launcher.LaunchAsync(ticket, request, token).ConfigureAwait(false);
        try { await ticket.CommitAsync(ready, token).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UpdateDiscoveryException or
            UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            return "The update handoff did not finish. Do not start another installer. Check the updater progress window or its recovery result before retrying.";
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (registration is not null)
            {
                await registration.DisposeAsync().ConfigureAwait(false);
                registration = null;
            }
        }
        finally { storage?.Dispose(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
