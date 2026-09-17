using System.Net;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public interface IPresenceTransport : IDisposable
{
    Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken);
}

public sealed class PresenceTransport(RemoteConfiguration configuration, HttpClient? client = null) : IPresenceTransport
{
    private readonly HttpClient _client = client ?? RemoteHttpTransport.CreateClient(configuration);
    private int? _version;

    public async Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken)
    {
        if (PresenceProtocol.Validate(report).Count != 0) return false;
        var version = _version ??= await DashboardConnection.NegotiateAsync(configuration, _client, cancellationToken);
        var projected = PresenceProtocol.Project(report, version);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projected, PresenceProtocol.Json);
        if (bytes.Length > Protocol.MaxBodyBytes) return false;
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(configuration.BaseUri, $"/api/v{version}/reports"))
        {
            Content = content
        };
        request.Headers.Accept.Add(new("application/json"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode == HttpStatusCode.Accepted;
    }

    public void Dispose()
    {
        if (client is null) _client.Dispose();
    }
}
