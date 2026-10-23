using AgentSignaler.Updates;

namespace AgentSignaler.AppUpdates;

// A startup eligibility warning takes precedence over every routine discovery
// status (including cached offers), and cannot be dismissed until a new startup.
internal sealed record UpdatePanelPresentation(string Message, ReleaseCandidate? Candidate,
    bool IsWarning, bool IsOpen)
{
    public bool CanUpdate => Candidate is not null && !IsWarning;

    public static UpdatePanelPresentation Create(UpdateCheckResult state,
        UpdateEligibilityWarning? warning, long? dismissed)
    {
        if (warning is not null) return new(warning.Message, null, true, true);
        var candidate = state.Status == UpdateCheckStatus.Available ? state.Candidate : null;
        var description = state.Status switch
        {
            UpdateCheckStatus.Checking => "Checking for updates…",
            UpdateCheckStatus.UpToDate => "This installation is up to date.",
            UpdateCheckStatus.Available when candidate is not null => $"Version {candidate.Version}{(candidate.Prerelease ? " (preview)" : "")} is available. Update and restart.",
            UpdateCheckStatus.CheckFailed => "The update check failed. Try again later. The app will keep running.",
            UpdateCheckStatus.CheckIncomplete => "The update check is incomplete. No up-to-date result could be established. Try again later.",
            _ => "Updates have not been checked yet."
        };
        return new(description, candidate, false, candidate is not null && dismissed != candidate.Id);
    }
}
