using System.Text.Json.Serialization;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

[JsonConverter(typeof(JsonStringEnumConverter<DashboardConnectionMode>))]
internal enum DashboardConnectionMode { Lan, DevTunnel }

internal enum ReceiverStartupState { Preparing, Starting, Running, Failed, Stopping, Stopped }

internal sealed record StartupStatusPresentation(string Service, string Tunnel, bool IsTunnelStarting)
{
    internal static StartupStatusPresentation Create(ReceiverStartupState receiver, DashboardConnectionMode mode,
        int port, TunnelStatus? tunnel, string? tunnelSetupError = null)
    {
        var service = receiver switch
        {
            ReceiverStartupState.Preparing => "Preparing local storage",
            ReceiverStartupState.Starting => $"Starting HTTP listener on port {port}",
            ReceiverStartupState.Running => $"Running - {(mode == DashboardConnectionMode.DevTunnel ? "loopback" : "LAN")} HTTP listener on port {port}",
            ReceiverStartupState.Failed => "Failed to start - see the error details",
            ReceiverStartupState.Stopping => "Stopping",
            ReceiverStartupState.Stopped => "Stopped",
            _ => throw new ArgumentOutOfRangeException(nameof(receiver))
        };
        string sharing;
        if (mode == DashboardConnectionMode.Lan)
            sharing = "Not used (LAN mode)";
        else if (receiver is ReceiverStartupState.Preparing or ReceiverStartupState.Starting)
            sharing = "Waiting for the local service";
        else if (receiver != ReceiverStartupState.Running)
            sharing = "Unavailable - local service is not running";
        else if (tunnelSetupError is not null)
            sharing = $"Setup failed - {tunnelSetupError}";
        else if (tunnel is null)
            sharing = "Loading tunnel configuration";
        else
        {
            var phase = tunnel.State switch
            {
                TunnelState.CheckingAccount => "Checking CLI and account",
                TunnelState.AccountRequired => "Sign-in required",
                TunnelState.Starting => "Connecting",
                TunnelState.Verifying => "Verifying public HTTPS",
                TunnelState.Faulted => "Error",
                TunnelState.Unsupported => "CLI unavailable",
                _ => tunnel.State.ToString()
            };
            sharing = $"{phase} - {tunnel.Message}";
        }
        var isTunnelStarting = mode == DashboardConnectionMode.DevTunnel &&
            receiver == ReceiverStartupState.Running && tunnelSetupError is null &&
            (tunnel is null || tunnel.State is TunnelState.CheckingAccount or TunnelState.Creating or
                TunnelState.Starting or TunnelState.Verifying or TunnelState.Reconnecting);
        return new($"Service: {service}", $"Dev Tunnels: {sharing}", isTunnelStarting);
    }
}

internal sealed record ConnectionPresentation(string Address, string? CopyUrl, string Warning)
{
    internal const string InternetWarning =
        "HTTPS encrypts reports, but clients are anonymous. Anyone who can reach this URL can submit status. Stop sharing to disable access.";
    internal const string LanWarning =
        "Use only on a trusted LAN or VPN. Anyone who can reach this HTTP receiver can submit status. Do not expose it to the Internet.";

    internal static ConnectionPresentation Create(DashboardConnectionMode mode, bool receiverRunning,
        string hostname, int port, bool publicVerified, Uri? publicUrl, string tunnelStatus)
    {
        if (mode == DashboardConnectionMode.Lan)
        {
            var url = $"http://{hostname}:{port}";
            return new(receiverRunning ? url : "Receiver not running",
                receiverRunning ? url : null, LanWarning);
        }

        var canCopy = receiverRunning && publicVerified && publicUrl is
        {
            IsAbsoluteUri: true, Scheme: "https", UserInfo.Length: 0, AbsolutePath: "/",
            Query.Length: 0, Fragment.Length: 0
        } && !publicUrl.IsLoopback &&
            !publicUrl.Host.Contains("-inspect.", StringComparison.OrdinalIgnoreCase) &&
            publicUrl.Host.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase);
        var address = canCopy ? publicUrl!.AbsoluteUri : $"Public URL unavailable: {tunnelStatus}";
        return new(address, canCopy ? address : null, InternetWarning);
    }
}
