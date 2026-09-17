using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;

namespace AgentSignaler.Remote;

public enum TranscriptOperation { Capabilities, Open, Event, Close }

public sealed record TranscriptTransportResult(int StatusCode, ReadOnlyMemory<byte> Body,
    string? Failure = null, TimeSpan? RetryAfter = null)
{
    public bool Retryable => Failure == "network-unavailable" ||
        StatusCode is 408 or 429 or >= 500 and <= 599;
}

public interface ITranscriptTransport : IDisposable
{
    Task<TranscriptTransportResult> SendAsync(Uri baseUri, TranscriptOperation operation,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
}

/// <summary>Content-only transport; its caller owns admission, ordering, retries and lifetime.</summary>
public sealed class TranscriptTransport : ITranscriptTransport
{
    private readonly HttpClient _client;
    private readonly TimeProvider _clock;

    public TranscriptTransport(HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _client = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = true,
            Credentials = null,
            DefaultProxyCredentials = null,
            ConnectTimeout = TimeSpan.FromMilliseconds(700)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<TranscriptTransportResult> SendAsync(Uri baseUri, TranscriptOperation operation,
        ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (!IsCanonicalHttps(baseUri)) return new(0, default, "https-required");
        if (body.Length == 0 || body.Length > (operation == TranscriptOperation.Event ? 32768 : 4096))
            return new(0, default, "invalid-protocol");
        var route = operation switch
        {
            TranscriptOperation.Capabilities => "/api/transcripts/v1/capabilities",
            TranscriptOperation.Open => "/api/transcripts/v1/streams/open",
            TranscriptOperation.Event => "/api/transcripts/v1/events",
            TranscriptOperation.Close => "/api/transcripts/v1/streams/close",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, route));
        request.Content = new ReadOnlyMemoryContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and <= 399) return new(status, default, "redirect-refused");
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date is { } date ? date - _clock.GetUtcNow() : null);
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            if (response.Content.Headers.ContentLength > 4096)
                return new(status, default, "invalid-protocol");
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return status is 408 or 429 or >= 500 and <= 599
                    ? new(status, default, RetryAfter: delay)
                    : new(status, default, "invalid-protocol");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[4097];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            return count > 4096 ? new(status, default, "invalid-protocol") :
                new(status, bytes.AsMemory(0, count), RetryAfter: delay);
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.SecureConnectionError ||
                                             ex.InnerException is AuthenticationException)
        {
            return new(0, default, "tls-validation-failed");
        }
        catch (HttpRequestException) { return new(0, default, "network-unavailable"); }
        catch (IOException) { return new(0, default, "network-unavailable"); }
    }

    public static bool IsCanonicalHttps(Uri uri) => uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsoluteUri == uri.GetLeftPart(UriPartial.Authority) + "/";

    public void Dispose() => _client.Dispose();
}
