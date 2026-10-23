using AgentSignaler.Remote;

namespace AgentSignaler.Configurator;

internal static class ConfiguratorConnectionPresentation
{
    // Use only fixed application-owned text. Never echo unrecognized IPC status content.
    // These are resolver states reported by Client, not Configurator health-test results.
    internal static string ClientDnsHealth(IntegrationRuntimeState runtime, string savedRevision)
    {
        if (!runtime.Running) return "Client DNS health: Client is stopped; it is not resolving DNS or reporting.";
        var text = runtime.DiscoveryStatus switch
        {
            "resolved" => "Client DNS health: the running Client reports a resolved public TXT endpoint. This does not authenticate the receiver or prove Dashboard health or event delivery.",
            "cached" => "Client DNS health: the running Client is using an endpoint cached within its DNS TTL while lookup is unavailable. Reporting must stop when the lease expires.",
            "pending" => "Client DNS health: the running Client is waiting for DNS resolution. Reporting is suspended; no direct URL fallback is used.",
            "unavailable" => "Client DNS health: no usable DNS endpoint is available to the running Client. Reporting is suspended; no direct URL fallback is used.",
            "expired" => "Client DNS health: the endpoint TTL lease expired. Reporting is suspended until a fresh valid DNS answer is available; no direct URL fallback is used.",
            "direct" => "Client DNS health: the running Client reports Direct URL mode, not DNS discovery. The saved DNS configuration is not confirmed.",
            "not-running" => "Client DNS health: DNS reporting is not running. A successful Configurator test does not start Client discovery.",
            _ => "Client DNS health: the running Client has not supplied a recognized DNS status. A successful Configurator test does not establish Client discovery health."
        };
        var reporting = runtime.State switch
        {
            "connected" => "connected",
            "retrying" => "retrying; current Dashboard reachability is not confirmed",
            "discovery-unavailable" => "suspended; no valid DNS endpoint lease",
            "reloading" => "reloading settings",
            "stopping" => "stopping",
            "unconfigured" => "not configured",
            _ => "not observed"
        };
        text += "\nClient reporting: " + reporting + ".";
        if (runtime.EffectiveRevision != savedRevision)
            text += " The saved settings revision is not yet effective; discovery health for the saved hostname is not confirmed.";
        if (!string.IsNullOrWhiteSpace(runtime.Message))
        {
            // A valid TTL lease can coexist with a failed refresh. Surface that warning even
            // when the reporting state remains connected, without echoing arbitrary IPC content.
            string[] dnsWarnings =
            [
                DnsTxtEndpointResolver.DescribeStatus("dns-records-invalid"),
                DnsTxtEndpointResolver.DescribeStatus("endpoint-cache-expired"),
                DnsTxtEndpointResolver.DescribeStatus("dns-unavailable")
            ];
            var warning = dnsWarnings.FirstOrDefault(value => value == runtime.Message) ??
                "Client reported a warning or error. Its reporting state is not an all-clear DNS lookup or delivery result.";
            text += "\nClient warning: " + warning;
        }
        return text;
    }
}
