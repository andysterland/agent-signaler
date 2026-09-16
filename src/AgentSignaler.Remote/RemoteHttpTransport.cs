namespace AgentSignaler.Remote;

public static class RemoteHttpTransport
{
    public static SocketsHttpHandler CreateHandler(RemoteConfiguration config, bool interactive = false) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = config.BaseUri.Scheme == Uri.UriSchemeHttps,
        Credentials = null,
        DefaultProxyCredentials = null,
        ConnectTimeout = interactive ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(700)
    };

    public static HttpClient CreateClient(RemoteConfiguration config, bool interactive = false) =>
        new(CreateHandler(config, interactive))
        {
            Timeout = interactive ? DashboardConnection.TestTimeout : TimeSpan.FromMilliseconds(1500)
        };
}
