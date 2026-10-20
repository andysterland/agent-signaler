using System.Net;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public interface IPresenceTransport : IDisposable
{
    Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken);
}

public interface IEndpointLeaseTransport
{
    void RenewEndpoint(EffectiveEndpointSnapshot endpoint);
}

public sealed class PresenceTransport : IPresenceTransport, IEndpointLeaseTransport
{
    private readonly RemoteConfiguration _configuration;
    private EffectiveEndpointSnapshot _endpoint;
    private readonly TimeProvider _clock;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private int? _version;

    public PresenceTransport(RemoteConfiguration configuration, HttpClient? client = null)
        : this(configuration, EffectiveEndpointSnapshot.Direct(configuration), client) { }

    public PresenceTransport(RemoteConfiguration configuration, EffectiveEndpointSnapshot endpoint,
        HttpClient? client = null, TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _endpoint = endpoint;
        _clock = timeProvider ?? TimeProvider.System;
        _client = client ?? RemoteHttpTransport.CreateClient(endpoint.BaseUri);
        _ownsClient = client is null;
    }

    public async Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken)
    {
        var endpoint = Volatile.Read(ref _endpoint);
        if (!endpoint.IsValid(_clock) || PresenceProtocol.Validate(report).Count != 0) return false;
        var version = _version ??= await DashboardConnection.NegotiateAsync(_configuration, endpoint, _client, cancellationToken);
        if (!endpoint.IsValid(_clock)) return false;
        var projected = PresenceProtocol.Project(report, version);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projected, PresenceProtocol.Json);
        if (bytes.Length > Protocol.MaxBodyBytes) return false;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUri, $"/api/v{version}/reports"))
        {
            Content = content
        };
        request.Headers.Accept.Add(new("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode == HttpStatusCode.Accepted;
    }

    public void RenewEndpoint(EffectiveEndpointSnapshot endpoint)
    {
        var current = Volatile.Read(ref _endpoint);
        if (current.BaseUri != endpoint.BaseUri || current.EndpointRevision != endpoint.EndpointRevision)
            throw new InvalidOperationException("Endpoint replacement requires a new transport.");
        Volatile.Write(ref _endpoint, endpoint);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
