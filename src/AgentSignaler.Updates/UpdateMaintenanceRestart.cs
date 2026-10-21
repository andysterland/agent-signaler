using System.Globalization;

namespace AgentSignaler.Updates;

public sealed record UpdateRestartAuthorization(string RunId, string Nonce, int OriginalProcessId)
{
    public const string RunEnvironmentVariable = "AGENT_SIGNALER_UPDATE_RUN";
    public const string NonceEnvironmentVariable = "AGENT_SIGNALER_UPDATE_NONCE";
    public const string ParticipantEnvironmentVariable = "AGENT_SIGNALER_UPDATE_PARTICIPANT";

    public static UpdateRestartAuthorization? FromEnvironment()
    {
        var run = Environment.GetEnvironmentVariable(RunEnvironmentVariable);
        var nonce = Environment.GetEnvironmentVariable(NonceEnvironmentVariable);
        var participant = Environment.GetEnvironmentVariable(ParticipantEnvironmentVariable);
        if (run is null && nonce is null && participant is null) return null;
        if (run is null || nonce is null || !Guid.TryParseExact(run, "N", out var id) || id == Guid.Empty ||
            run != id.ToString("N") || !UpdateHandoffLauncher.IsHash(nonce) ||
            !int.TryParse(participant, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            throw new InvalidDataException("update_restart_authorization_invalid");
        return new(run, nonce, pid);
    }

    public static void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable(RunEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(NonceEnvironmentVariable, null);
        Environment.SetEnvironmentVariable(ParticipantEnvironmentVariable, null);
    }
}

public sealed record UpdateRestartGrant(int SchemaVersion, string RunId, int OriginalProcessId,
    UpdateProcessIdentity Process, UpdateRestartContext Restart);

internal static class UpdateMaintenanceRestart
{
    public static string GrantName(UpdateProcessIdentity process) =>
        $"restart-grant-{process.ProcessId}-{process.CreationTimeUtcTicks}.json";

    public static bool Authorize(UpdateRestartAuthorization? authorization, UpdateMaintenanceGate marker,
        UpdateInstalledPackage installation, IUpdateStorage storage, UpdateProcessIdentity current, UpdateRestartContext? restart,
        CancellationToken cancellationToken = default)
    {
        if (authorization is null || authorization.RunId != marker.RunId.ToString("N") ||
            authorization.OriginalProcessId <= 0 || !UpdateHandoffLauncher.IsHash(authorization.Nonce)) return false;
        var manifest = UpdateStrictJson.Read<UpdateHandoffManifest>(storage.ReadRun(marker.RunId, "handoff.json") ??
            throw new InvalidDataException("update_restart_manifest_missing"));
        if (manifest.SchemaVersion != 1 || manifest.RunId != authorization.RunId || manifest.Nonce != authorization.Nonce ||
            manifest.Package != installation.Package.ToString() ||
            !UpdatePath.EqualsDirectory(manifest.InstallDirectory, installation.InstallDirectory) ||
            !UpdateMaintenanceValidation.SameProcess(manifest.Parent, marker.Controller) || manifest.Participants.IsDefaultOrEmpty ||
            manifest.Participants.Length > UpdateMaintenanceValidation.MaximumParticipants)
            return false;
        var matches = manifest.Participants.Where(p => p.Process.ProcessId == authorization.OriginalProcessId).ToArray();
        if (matches.Length != 1) return false;
        var captured = matches[0];
        UpdateMaintenanceValidation.Registration(captured, installation);
        UpdateMaintenanceValidation.Process(current);
        if (!(current.UserSid == marker.Owner.UserSid && current.SessionId == marker.Owner.SessionId &&
            string.Equals(current.ExecutablePath, captured.Process.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            (restart is null || restart.Application == captured.Restart.Application && restart.Background == captured.Restart.Background &&
                UpdatePath.EqualsDirectory(restart.ContextPath, captured.Restart.ContextPath)))) return false;

        // Process.Start returns before the updater can publish the exact child identity.
        // The updater writes this atomic grant without taking the startup-held lease.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        byte[]? bytes;
        while ((bytes = storage.Read(GrantName(current))) is null && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(25);
        }
        if (bytes is null) return false;
        var grant = UpdateStrictJson.Read<UpdateRestartGrant>(bytes);
        return grant.SchemaVersion == 1 && grant.RunId == authorization.RunId &&
            grant.OriginalProcessId == authorization.OriginalProcessId &&
            UpdateMaintenanceValidation.SameProcess(grant.Process, current) && grant.Restart == captured.Restart;
    }
}