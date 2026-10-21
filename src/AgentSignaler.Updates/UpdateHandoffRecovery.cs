using System.Collections.Immutable;

namespace AgentSignaler.Updates;

public enum UpdateInstallerState { NotStarted, Starting, Running, Completed }
public enum UpdateServicingOutcome { Pending, Completed, RebootRequired, InstallerBusy, Cancelled, InstallerFailed, Failed }
public enum UpdateRestartState { Ready, StillRunning, Failed }
public sealed record UpdateRestartResult(UpdateApplication Application, int ProcessId, UpdateRestartState State);
public sealed record UpdateDurableResult(int SchemaVersion, string RunId, UpdateStage Stage,
    UpdateServicingOutcome Outcome, UpdateInstallerState MsiState, int? InstallerExitCode,
    int? InstallerProcessId, long? InstallerCreationTimeUtcTicks, string TargetVersion,
    string? DiagnosticCode, ImmutableArray<UpdateRestartResult> Restarts);
public sealed record UpdateRecoveryResult(Guid RunId, UpdateDurableResult Result, bool RequiresManualRecovery);

// Observation only. A dead updater or absent result never authorizes gate deletion, retry,
// installer termination or a restart; Windows Installer may still be servicing the files.
public sealed class UpdateHandoffRecovery(UpdateInstalledPackage installation, IUpdateStorage storage)
{
    public ImmutableArray<UpdateRecoveryResult> ReadResults()
    {
        var results = ImmutableArray.CreateBuilder<UpdateRecoveryResult>();
        foreach (var runId in storage.ListRuns())
        {
            var bytes = storage.ReadRun(runId, "result.json");
            if (bytes is null) continue;
            var manifest = UpdateStrictJson.Read<UpdateHandoffManifest>(storage.ReadRun(runId, "handoff.json") ??
                throw new InvalidDataException("update_recovery_manifest_missing"));
            if (manifest.SchemaVersion != 1 || manifest.RunId != runId.ToString("N") ||
                manifest.Package != installation.Package.ToString() || manifest.Parent.UserSid != installation.UserSid ||
                !UpdatePath.EqualsDirectory(manifest.InstallDirectory, installation.InstallDirectory))
                throw new InvalidDataException("update_recovery_identity");
            if (!UpdateHandoffLauncher.IsHash(manifest.Nonce) || !UpdateHandoffLauncher.IsHash(manifest.ChecksumSha256) ||
                !UpdateHandoffLauncher.IsHash(manifest.PackageSha256) || manifest.Participants.IsDefaultOrEmpty || manifest.Participants.Length > 32 ||
                MsiReleaseVersion.Parse(manifest.InstalledVersion) >= MsiReleaseVersion.Parse(manifest.Tag, true))
                throw new InvalidDataException("update_recovery_manifest");
            UpdatePolicy.ValidateCandidate(new(manifest.ReleaseId, manifest.Tag, MsiReleaseVersion.Parse(manifest.Tag, true),
                DateTimeOffset.UnixEpoch, false, manifest.Assets));
            foreach (var participant in manifest.Participants) UpdateMaintenanceValidation.Registration(participant, installation);
            var result = ReadResult(bytes, runId, manifest.Tag);
            if (result.Restarts.Length > manifest.Participants.Length || result.Restarts.Any(r =>
                !manifest.Participants.Any(p => p.Restart.Application == r.Application)))
                throw new InvalidDataException("update_recovery_restart");
            results.Add(new(runId, result, result.MsiState is UpdateInstallerState.Starting or UpdateInstallerState.Running ||
                result.Outcome == UpdateServicingOutcome.Pending || result.DiagnosticCode == "update_recovery_required"));
        }
        return results.ToImmutable();
    }

    public static UpdateDurableResult ReadResult(ReadOnlySpan<byte> bytes, Guid runId, string targetTag)
    {
        var result = UpdateStrictJson.Read<UpdateDurableResult>(bytes);
        if (runId == Guid.Empty || result.SchemaVersion != 1 || result.RunId != runId.ToString("N") ||
            MsiReleaseVersion.Parse(result.TargetVersion) != MsiReleaseVersion.Parse(targetTag, true) ||
            !Enum.IsDefined(result.Stage) || !Enum.IsDefined(result.Outcome) || !Enum.IsDefined(result.MsiState) ||
            result.Restarts.IsDefault || result.Restarts.Length > 32 ||
            result.DiagnosticCode is not (null or "update_installer_unknown" or "update_servicing_failed" or
                "update_prior_installation_intact" or "update_recovery_required" or "update_restart_not_ready"))
            throw new InvalidDataException("update_result_invalid");
        if (result.InstallerProcessId.HasValue != result.InstallerCreationTimeUtcTicks.HasValue ||
            result.InstallerProcessId is <= 0 || result.InstallerCreationTimeUtcTicks is <= 0 or > 3155378975999999999 ||
            result.MsiState == UpdateInstallerState.NotStarted && (result.InstallerExitCode is not null || result.InstallerProcessId is not null) ||
            result.MsiState is UpdateInstallerState.Running or UpdateInstallerState.Completed && result.InstallerProcessId is null ||
            result.MsiState == UpdateInstallerState.Completed != result.InstallerExitCode.HasValue ||
            result.InstallerExitCode == 0 && result.Outcome != UpdateServicingOutcome.Completed ||
            result.InstallerExitCode == 3010 && result.Outcome != UpdateServicingOutcome.RebootRequired ||
            result.InstallerExitCode == 1618 && result.Outcome != UpdateServicingOutcome.InstallerBusy ||
            result.InstallerExitCode == 1602 && result.Outcome != UpdateServicingOutcome.Cancelled ||
            result.InstallerExitCode is not (null or 0 or 3010 or 1618 or 1602) && result.Outcome != UpdateServicingOutcome.InstallerFailed ||
            result.Stage == UpdateStage.Completed && (result.Outcome != UpdateServicingOutcome.Completed || result.InstallerExitCode != 0) ||
            result.Outcome == UpdateServicingOutcome.RebootRequired && result.Restarts.Length != 0)
            throw new InvalidDataException("update_result_installer_state");
        foreach (var restart in result.Restarts)
            if (!Enum.IsDefined(restart.Application) || !Enum.IsDefined(restart.State) || restart.ProcessId < 0 ||
                (restart.State == UpdateRestartState.Failed) != (restart.ProcessId == 0))
                throw new InvalidDataException("update_result_restart");
        return result;
    }
}