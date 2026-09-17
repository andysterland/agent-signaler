using AgentSignaler.Contracts;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

public static class SessionPresentation
{
    public static IReadOnlyList<RuntimeSession> Project(MachineView machine, DateTimeOffset now) =>
        Array.AsReadOnly(machine.Sessions
            .OrderBy(s => (s.Source ?? SourceDescriptor.LegacyCli).Kind, StringComparer.Ordinal)
            .ThenBy(s => (s.Source ?? SourceDescriptor.LegacyCli).ScopeId, StringComparer.Ordinal)
            .ThenBy(s => s.SessionId, StringComparer.Ordinal)
            .Select(s => new RuntimeSession(s, StateReducer.Effective(s, now))
            {
                MachineId = machine.MachineId,
                IsOffline = machine.State == AgentState.Offline,
                IsConnected = machine.State != AgentState.Offline && s.UnderlyingState != AgentState.Idle &&
                    s.LatestEvent != AgentEvent.SessionEnd
            }).ToArray());

    public static string SourceLabel(string kind) => kind switch
    {
        "copilot-cli" => "Copilot CLI",
        "visual-studio" => "Visual Studio",
        "vscode" => "VS Code",
        _ => "Copilot"
    };

    public static string EventLabel(AgentEvent? kind) => kind switch
    {
        AgentEvent.SessionStart => "Session started",
        AgentEvent.UserPromptSubmitted => "Prompt submitted",
        AgentEvent.PreToolUse => "Tool started",
        AgentEvent.PostToolUse => "Tool completed",
        AgentEvent.PermissionRequest => "Permission requested",
        AgentEvent.AgentStop => "Turn completed",
        AgentEvent.SessionEnd => "Session ended",
        AgentEvent.ErrorOccurred => "Error occurred",
        AgentEvent.PostToolUseFailure => "Tool failed",
        AgentEvent.ExecutionStopped => "Execution stopped",
        _ => "Last event unavailable"
    };

    public static string RelativeTime(DateTimeOffset timestamp, DateTimeOffset now)
    {
        var elapsed = now - timestamp;
        if (elapsed < TimeSpan.FromMinutes(1)) return "moments ago";
        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return $"{minutes} minute{(minutes == 1 ? "" : "s")} ago";
        }
        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return $"{hours} hour{(hours == 1 ? "" : "s")} ago";
        }
        var days = (int)elapsed.TotalDays;
        return $"{days} day{(days == 1 ? "" : "s")} ago";
    }
}
