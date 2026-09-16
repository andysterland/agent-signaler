using System.Globalization;

namespace AgentSignaler.Tunneling;

internal sealed class TunnelHostOutputParser(string tunnelId, int port)
{
    private int? announcedPort;
    private Uri? candidate;
    private bool ready;

    internal Uri? Feed(string line)
    {
        const string portPrefix = "Hosting port: ";
        const string browserPrefix = "Connect via browser: ";
        const string readyPrefix = "Ready to accept connections for tunnel: ";
        if (line.StartsWith(portPrefix, StringComparison.Ordinal))
        {
            if (!int.TryParse(line[portPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value is < 1 or > 65535)
                throw new TunnelException("The host announced an invalid receiver port.", TunnelState.Unsupported);
            announcedPort = value;
        }
        else if (line.StartsWith(browserPrefix, StringComparison.Ordinal) && announcedPort == port)
        {
            var url = TunnelValidation.ValidatePublicUrl(line[browserPrefix.Length..].Trim());
            if (candidate is not null && candidate != url)
                throw new TunnelException("The host announced conflicting URLs for the receiver port.");
            candidate = url;
        }
        else if (line.StartsWith(readyPrefix, StringComparison.Ordinal))
        {
            if (line[readyPrefix.Length..] != tunnelId)
                throw new TunnelException("The host announced readiness for a different tunnel.");
            ready = true;
        }
        // Relay-restored messages alone are not readiness. Inspection URLs are never candidates.
        return ready ? candidate : null;
    }
}
