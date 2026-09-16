using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSignaler.Contracts;

namespace AgentSignaler.Tunneling;

public sealed class AnonymousHealthProbe : ITunnelHealthProbe, IDisposable
{
    private static readonly JsonSerializerOptions HealthJson = new(Protocol.Json)
    {
        NumberHandling = JsonNumberHandling.Strict
    };
    private readonly HttpClient publicClient;
    private readonly HttpClient loopbackClient;

    public AnonymousHealthProbe() : this(CreateHandler(useProxy: true), CreateHandler(useProxy: false)) { }

    internal static HttpClientHandler CreateHandler(bool useProxy) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
        Credentials = null, PreAuthenticate = false, UseProxy = useProxy,
        Proxy = useProxy ? new CredentiallessSystemProxy(HttpClient.DefaultProxy) : null,
        DefaultProxyCredentials = null,
        AutomaticDecompression = DecompressionMethods.None
    };

    internal AnonymousHealthProbe(HttpMessageHandler publicHandler, HttpMessageHandler? loopbackHandler = null)
    {
        publicClient = new HttpClient(publicHandler) { Timeout = TimeSpan.FromSeconds(15) };
        loopbackClient = loopbackHandler is null ? publicClient :
            new HttpClient(loopbackHandler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task VerifyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        cancellationToken = deadline.Token;
        var isLocal = baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback;
        if (!isLocal)
            TunnelValidation.ValidatePublicUrl(baseUri.AbsoluteUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "health"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var client = isLocal ? loopbackClient : publicClient;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK ||
            response.Content.Headers.ContentType?.MediaType != "application/json" ||
            response.Content.Headers.ContentLength > 4096)
            throw new TunnelException("Anonymous health verification failed (HTTP status, content type, or size).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[4097];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        if (count > 4096) throw new TunnelException("Anonymous health response exceeded 4096 bytes.");
        try
        {
            using var doc = TunnelValidation.Parse(System.Text.Encoding.UTF8.GetString(bytes, 0, count));
            var health = doc.RootElement.Deserialize<HealthResponse>(HealthJson);
            if (health?.ProtocolVersion != Protocol.Version || health.Status != "ok")
                throw new TunnelException("Anonymous health returned an incompatible protocol or status.");
        }
        catch (JsonException) { throw new TunnelException("Anonymous health returned invalid JSON."); }
    }

    public void Dispose()
    {
        try { publicClient.Dispose(); }
        finally { if (!ReferenceEquals(loopbackClient, publicClient)) loopbackClient.Dispose(); }
    }

    internal sealed class CredentiallessSystemProxy(IWebProxy systemProxy) : IWebProxy
    {
        public ICredentials? Credentials
        {
            get => null;
            set
            {
                if (value is not null) throw new NotSupportedException("Anonymous probes cannot use proxy credentials.");
            }
        }

        public Uri? GetProxy(Uri destination) => systemProxy.GetProxy(destination);
        public bool IsBypassed(Uri host) => systemProxy.IsBypassed(host);
    }
}
