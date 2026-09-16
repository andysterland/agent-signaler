using System.Text.Json.Serialization;

namespace AgentSignaler.Dashboard;

[JsonConverter(typeof(JsonStringEnumConverter<DashboardConnectionMode>))]
internal enum DashboardConnectionMode { Lan, DevTunnel }

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
