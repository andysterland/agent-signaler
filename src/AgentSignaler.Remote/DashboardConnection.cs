using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public static class DashboardConnection
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    internal static async Task VerifyBeforeApplyAsync(RemoteConfiguration config,
        Func<RemoteConfiguration, CancellationToken, Task<bool>> verify, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TestTimeout);
        try
        {
            if (!await verify(config, timeout.Token).WaitAsync(timeout.Token))
                throw new InvalidOperationException("Dashboard capability verification failed; no settings changed.");
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Dashboard capability check timed out for {config.BaseUri}. No settings or hooks were changed. " +
                "Use Connection > Test connection and check that the dashboard is running and sharing at this URL.", ex);
        }
    }

    public static async Task TestAsync(RemoteConfiguration config, HttpClient client, CancellationToken token)
        => _ = await NegotiateAsync(config, client, token);

    public static async Task<int> NegotiateAsync(RemoteConfiguration config, HttpClient client, CancellationToken token)
    {
        if (config.Version < 3)
        {
            await TestVersionAsync(config, client, Protocol.Version, token);
            return Protocol.Version;
        }
        foreach (var version in new[] { PresenceProtocol.DisplayNameVersion, PresenceProtocol.EnrichedVersion })
        {
            try
            {
                await TestVersionAsync(config, client, version, token);
                return version;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }
        }
        var legacy = config.Version >= 4 ? PresenceProtocol.SourceVersion : PresenceProtocol.Version;
        try { await TestVersionAsync(config, client, legacy, token); }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            throw new InvalidDataException($"Upgrade the dashboard receiver first. This configuration requires protocol v{legacy} or newer; source identity cannot be removed.", ex);
        }
        return legacy;
    }

    private static async Task TestVersionAsync(RemoteConfiguration config, HttpClient client, int expectedVersion, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            expectedVersion == Protocol.Version ? config.HealthEndpoint : new Uri(config.BaseUri, $"/api/v{expectedVersion}/health"));
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Add(Protocol.ConnectionTestHeader, "1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode != System.Net.HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > 4096)
            throw new InvalidDataException("The endpoint did not return a valid Agent Signaler health response.");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var body = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (body.Length + read > 4096)
                throw new InvalidDataException("The health response is too large.");
            body.Write(buffer, 0, read);
        }
        int? protocolVersion;
        string? status;
        try
        {
            if (config.Version >= 3)
            {
                var health = JsonSerializer.Deserialize<PresenceHealthResponse>(body.ToArray(), PresenceProtocol.Json);
                protocolVersion = health?.ProtocolVersion;
                status = health?.Status;
            }
            else
            {
                var health = JsonSerializer.Deserialize<HealthResponse>(body.ToArray(), PresenceProtocol.Json);
                protocolVersion = health?.ProtocolVersion;
                status = health?.Status;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The endpoint returned malformed JSON instead of an Agent Signaler health response.", ex);
        }
        if (protocolVersion != expectedVersion || status != "ok")
            throw new InvalidDataException($"The endpoint is not a compatible Agent Signaler dashboard. Required protocol v{expectedVersion}.");
    }

    public static string DescribeFailure(HttpRequestException error) => error.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
            "Tunnel access is restricted. Ask the dashboard owner to enable anonymous access; do not sign in on this remote computer.",
        System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable =>
            "The tunnel or receiver may be stopped, unavailable, or deleted. Ask the dashboard owner to check sharing and the current URL.",
        System.Net.HttpStatusCode.TooManyRequests =>
            "The endpoint is rate limited. Wait before testing again.",
        System.Net.HttpStatusCode.ProxyAuthenticationRequired =>
            "The system proxy requires authentication. This application will not prompt for proxy credentials.",
        >= System.Net.HttpStatusCode.MultipleChoices and < System.Net.HttpStatusCode.BadRequest =>
            "The endpoint redirected the request. Login pages and HTTP downgrades are not accepted.",
        _ => error.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => "DNS lookup failed. Check the URL, network and DNS settings.",
            HttpRequestError.SecureConnectionError => "TLS certificate or secure connection validation failed. Check the URL and system trust settings; certificate validation cannot be bypassed.",
            HttpRequestError.ProxyTunnelError => "The system proxy could not establish the HTTPS connection. Check organizational proxy policy.",
            HttpRequestError.ConnectionError => "Connection failed. Check the network, proxy, and whether the dashboard is sharing.",
            _ => "The endpoint could not complete the request. Check the dashboard and network, then retry."
        }
    };
}
