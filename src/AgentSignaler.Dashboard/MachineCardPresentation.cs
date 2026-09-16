using AgentSignaler.Contracts;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal static class MachineCardPresentation
{
    public const double CardSpacing = 24;
    public const double MinimumHeight = 220;

    public static (int Columns, double CardWidth) Layout(double width, bool dense)
    {
        var desired = dense ? 300 : 370;
        var columns = Math.Max(1, (int)((width + CardSpacing) / (desired + CardSpacing)));
        return (columns, Math.Max(1, (width - (columns - 1) * CardSpacing) / columns));
    }

    public static string Activity(MachineView machine, DateTimeOffset now)
    {
        if (machine.State == AgentState.Offline)
            return $"Last seen {RelativeTime(machine.LastContactUtc, now)}";
        if (machine.State == AgentState.Idle) return "Client connected";
        var source = machine.Sessions.MaxBy(session => session.UpdatedAtUtc)?.Source?.Kind ?? machine.Client;
        var activity = machine.LatestEvent switch
        {
            AgentEvent.SessionStart => "session started",
            AgentEvent.UserPromptSubmitted => "prompt submitted",
            AgentEvent.PreToolUse => "preToolUse",
            AgentEvent.PostToolUse => "postToolUse",
            AgentEvent.PermissionRequest => "permission request",
            AgentEvent.AgentStop or AgentEvent.SessionEnd => "session complete",
            AgentEvent.ErrorOccurred or AgentEvent.PostToolUseFailure => "tool failure",
            AgentEvent.ExecutionStopped => "execution stopped",
            _ => "Awaiting hook activity"
        };
        return $"{SessionSourcePresentation.Name(source)} \u00B7 {activity}";
    }

    public static string Footer(MachineView machine, DateTimeOffset now) => machine.State switch
    {
        AgentState.Waiting => "Needs user input",
        AgentState.Succeeded => $"Result visible for {Protocol.ResultDuration.TotalSeconds:0} seconds",
        AgentState.Failed => "No prompt or response content stored",
        AgentState.Idle => "Ready for the next session",
        AgentState.Offline when machine.ExplicitOffline => "Client disconnected",
        AgentState.Offline => "Heartbeat deadline exceeded",
        _ => $"Updated {RelativeTime(machine.LatestEventUtc ?? machine.LastContactUtc, now)}"
    };

    private static string RelativeTime(DateTimeOffset timestamp, DateTimeOffset now)
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
