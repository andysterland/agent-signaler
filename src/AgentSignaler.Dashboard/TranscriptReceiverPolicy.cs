namespace AgentSignaler.Dashboard;

internal static class TranscriptReceiverPolicy
{
    internal static bool IsReady(DashboardConnectionMode mode, bool listenerRunning, int listenerPort,
        int ownedTunnelPort, bool ownedTunnelConnected, bool exiting, bool connectionSettingsChanged) =>
        mode == DashboardConnectionMode.DevTunnel && listenerRunning && ownedTunnelConnected &&
        listenerPort is >= 1024 and <= 65535 && listenerPort == ownedTunnelPort &&
        !exiting && !connectionSettingsChanged;
}
