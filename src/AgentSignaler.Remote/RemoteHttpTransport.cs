namespace AgentSignaler.Remote;

public static class RemoteHttpTransport
{
    public static SocketsHttpHandler CreateHandler(RemoteConfiguration config, bool interactive = false) =>
        CreateHandler(config.BaseUri, interactive);

    public static SocketsHttpHandler CreateHandler(Uri endpoint, bool interactive = false) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = endpoint.Scheme == Uri.UriSchemeHttps,
        Credentials = null,
        DefaultProxyCredentials = null,
        ConnectTimeout = interactive ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(700)
    };

    public static HttpClient CreateClient(RemoteConfiguration config, bool interactive = false) =>
        CreateClient(config.BaseUri, interactive);

    public static HttpClient CreateClient(Uri endpoint, bool interactive = false) =>
        new(CreateHandler(endpoint, interactive))
        {
            Timeout = interactive ? DashboardConnection.TestTimeout : TimeSpan.FromMilliseconds(1500)
        };
}
