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
            return $"Last seen {SessionPresentation.RelativeTime(machine.LastContactUtc, now)}";
        if (machine.State == AgentState.Idle) return "Client connected";
        var source = machine.Sessions.MaxBy(session => session.UpdatedAtUtc)?.Source?.Kind ?? machine.Client;
        var activity = SessionPresentation.EventLabel(machine.LatestEvent);
        return $"{SessionSourcePresentation.Name(source)} \u00B7 {activity}";
    }

    public static string Footer(MachineView machine, DateTimeOffset now) => machine.State switch
    {
        AgentState.Waiting => $"Needs user input · {SessionPresentation.Project(machine, now).Count(session => session.IsConnected && session.State == AgentState.Waiting)} Copilots waiting",
        AgentState.Succeeded => $"Result visible for {Protocol.ResultDuration.TotalSeconds:0} seconds",
        AgentState.Failed => "No prompt or response content stored",
        AgentState.Idle => "Ready for the next session",
        AgentState.Offline when machine.ExplicitOffline => "Client disconnected",
        AgentState.Offline => "Heartbeat deadline exceeded",
        _ => $"Updated {SessionPresentation.RelativeTime(machine.LatestEventUtc ?? machine.LastContactUtc, now)}"
    };
}
