using AgentSignaler.Configurator;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorConnectionPresentationTests
{
    private const string SavedRevision = "saved-revision";
    private static IntegrationRuntimeState Runtime(string? state, bool running = true, string? revision = SavedRevision,
        string? discoveryStatus = "resolved") =>
        new(running, revision) { State = state, DiscoveryStatus = discoveryStatus };

    [Fact]
    public void StoppedClientNeverClaimsResolutionEvenWithPreviousSuccessfulState()
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(Runtime("connected", running: false), SavedRevision);
        Assert.Contains("Client is stopped", text);
        Assert.Contains("not resolving DNS or reporting", text);
        Assert.DoesNotContain("reports a connection", text);
    }

    [Fact]
    public async Task UnavailableClientDnsFromStatusIpcIsShownIndependentlyOfSetupHealthTest()
    {
        var runtime = new ClientIntegrationRuntime(new StatusPlatform("discovery-unavailable", discoveryStatus: "unavailable"));
        var state = await runtime.QueryDiscoveryStatusAsync(Path.GetFullPath("dns-health-test-remote.json"), default);
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(state, SavedRevision);
        Assert.Contains("no usable DNS endpoint", text);
        Assert.Contains("Reporting is suspended", text);
        Assert.Contains("no direct URL fallback", text);
        Assert.DoesNotContain("health check succeeded", text);
        Assert.DoesNotContain("verified", text);
    }

    [Fact]
    public void ResolvedClientDnsDoesNotProveDashboardHealthOrAuthentication()
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(Runtime("connected"), SavedRevision);
        Assert.Contains("reports a resolved public TXT endpoint", text);
        Assert.Contains("does not authenticate the receiver or prove Dashboard health or event delivery", text);
        Assert.Contains("Client reporting: connected", text);
    }

    [Fact]
    public void RetryStateDoesNotPromiseReachabilityAndAllowsOnlyTtlLimitedCache()
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(Runtime("retrying", discoveryStatus: "cached"), SavedRevision);
        Assert.Contains("current Dashboard reachability is not confirmed", text);
        Assert.Contains("cached within its DNS TTL", text);
        Assert.Contains("Reporting must stop when the lease expires", text);
    }

    [Fact]
    public void PriorConnectedRevisionDoesNotEstablishSavedHostnameDiscoveryHealth()
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(Runtime("connected", revision: "prior"), SavedRevision);
        Assert.Contains("saved settings revision is not yet effective", text);
        Assert.Contains("saved hostname is not confirmed", text);
        Assert.DoesNotContain("using the saved DNS configuration", text);
    }

    [Fact]
    public async Task ConnectedClientStillSurfacesFailedDnsRefreshWarningWithinValidLease()
    {
        var warning = DnsTxtEndpointResolver.DescribeStatus("dns-unavailable");
        var runtime = new ClientIntegrationRuntime(new StatusPlatform("connected", warning, "cached"));
        var state = await runtime.QueryDiscoveryStatusAsync(Path.GetFullPath("dns-health-test-remote.json"), default);

        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(state, SavedRevision);

        Assert.Contains("cached within its DNS TTL while lookup is unavailable", text);
        Assert.Contains("Client reporting: connected", text);
        Assert.Contains("Client warning:", text);
        Assert.Contains("DNS TXT discovery is temporarily unavailable", text);
        Assert.Contains("usable only within its original TTL", text);
    }

    [Fact]
    public void UnrecognizedClientWarningIsNotEchoedIntoRuntimeHealth()
    {
        var state = Runtime("retrying") with { Message = "private-output https://secret.example/payload" };
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(state, SavedRevision);
        Assert.Contains("Client reported a warning or error", text);
        Assert.DoesNotContain("private-output", text);
        Assert.DoesNotContain("secret.example", text);
    }

    [Theory]
    [InlineData("reloading", "reloading settings")]
    [InlineData("stopping", "Client reporting: stopping")]
    [InlineData("unconfigured", "Client reporting: not configured")]
    public void TransitionalStatesDoNotClaimAWorkingDnsConnection(string state, string expected)
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(Runtime(state, discoveryStatus: "pending"), SavedRevision);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("reports a connection", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("private-raw-output https://secret.example/sensitive")]
    public void MissingOrUnknownRuntimeHealthCannotBeInferredFromConfiguratorTest(string? status)
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(Runtime(status, discoveryStatus: status), SavedRevision);
        Assert.Contains("has not supplied a recognized DNS status", text);
        Assert.Contains("successful Configurator test does not establish Client discovery health", text);
        Assert.DoesNotContain("private-raw-output", text);
        Assert.DoesNotContain("secret.example", text);
    }

    [Theory]
    [InlineData("expired", "TTL lease expired")]
    [InlineData("unavailable", "no usable DNS endpoint")]
    [InlineData("pending", "waiting for DNS resolution")]
    public void UnusableDiscoveryStatusSuspendsReportingWithoutFallback(string discoveryStatus, string expected)
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(
            Runtime("discovery-unavailable", discoveryStatus: discoveryStatus), SavedRevision);
        Assert.Contains(expected, text);
        Assert.Contains("Reporting is suspended", text);
        Assert.Contains("no direct URL fallback", text);
    }

    [Theory]
    [InlineData("direct", "reports Direct URL mode, not DNS discovery")]
    [InlineData("not-running", "DNS reporting is not running")]
    public void UnsupportedActiveDnsConfigurationCannotAppearResolved(string discoveryStatus, string expected)
    {
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(
            Runtime("connected", discoveryStatus: discoveryStatus), SavedRevision);
        Assert.Contains(expected, text);
        Assert.DoesNotContain("reports a resolved", text);
    }

    [Fact]
    public async Task OlderClientRejectingDiscoveryCommandDoesNotInheritConnectedAsDnsSuccess()
    {
        var runtime = new ClientIntegrationRuntime(new StatusPlatform("connected", discoveryStatus: "unknown", acceptsDiscovery: false));
        var state = await runtime.QueryDiscoveryStatusAsync(Path.GetFullPath("dns-health-test-remote.json"), default);
        var text = ConfiguratorConnectionPresentation.ClientDnsHealth(state, SavedRevision);
        Assert.Contains("has not supplied a recognized DNS status", text);
        Assert.Contains("Client reporting: connected", text);
        Assert.DoesNotContain("reports a resolved", text);
    }

    private sealed class StatusPlatform(string state, string? warning = null, string discoveryStatus = "resolved",
        bool acceptsDiscovery = true) : IClientRuntimePlatform
    {
        public bool IsOwnerRunning(string configPath) => true;
        public Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request, CancellationToken token)
        {
            Assert.Contains(request.Command, new[] { "status", "discovery-status" });
            return Task.FromResult(request.Command == "discovery-status"
                ? new ClientIpcResponse(acceptsDiscovery, discoveryStatus)
                : new ClientIpcResponse(true, state, SavedRevision, Error: warning));
        }
        public void Launch(string clientPath, string configPath) =>
            throw new InvalidOperationException("Status presentation must not launch Client.");
    }
}
