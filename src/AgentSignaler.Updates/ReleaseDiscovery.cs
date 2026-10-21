using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentSignaler.Updates;

public sealed record ReleaseDiscoveryResult(ReleaseCandidate Candidate, string? ETag, bool SinglePage);

public sealed class UpdateDiscoveryException(string code, DateTimeOffset? retryAt = null) : Exception(code)
{
    public string Code { get; } = code;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}

public sealed class ReleaseDiscovery(HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private const string Endpoint = "https://api.github.com/repos/andysterland/agent-signaler/releases";

    public static HttpClient CreatePublicHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        ConnectTimeout = TimeSpan.FromSeconds(10)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ReleaseDiscoveryResult> DiscoverAsync(UpdateCheckCache? cache, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(UpdatePolicy.MetadataDeadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try { return await EnumerateAsync(cache, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateDiscoveryException("update-check-timeout"); }
        catch (HttpRequestException) { throw new UpdateDiscoveryException("update-network-failed"); }
        catch (JsonException) { throw new UpdateDiscoveryException("update-metadata-invalid"); }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new UpdateDiscoveryException("update-metadata-invalid"); }
        catch (InvalidDataException) { throw new UpdateDiscoveryException("update-metadata-invalid"); }
        catch (IOException) { throw new UpdateDiscoveryException("update-network-failed"); }
    }

    private async Task<ReleaseDiscoveryResult> EnumerateAsync(UpdateCheckCache? cache, CancellationToken token)
    {
        JsonElement? selected = null;
        DateTimeOffset newest = DateTimeOffset.MinValue;
        var ambiguous = false;
        var ids = new HashSet<long>();
        var remaining = UpdatePolicy.MetadataLimit;
        string? etag = null;
        for (var page = 1; page <= UpdatePolicy.PageLimit; page++)
        {
            var uri = PageUri(page);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AgentSignaler-Updates/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (page == 1 && cache is { SinglePage: true, Candidate: not null, ETag: not null })
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cache.ETag));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                if (page != 1 || cache is not { SinglePage: true, Candidate: not null, ETag: not null } ||
                    HasNext(response, page))
                    throw new UpdateDiscoveryException("update-metadata-invalid");
                UpdatePolicy.ValidateCandidate(cache.Candidate);
                return new(cache.Candidate, cache.ETag, true);
            }
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw new UpdateDiscoveryException("update-rate-limited", RetryAt(response));
            if (response.StatusCode != HttpStatusCode.OK)
                throw new UpdateDiscoveryException("update-http-failed");
            if (page == 1 && response.Headers.ETag is { } header)
            {
                etag = header.ToString();
                if (etag.Length > 256) throw new UpdateDiscoveryException("update-metadata-invalid");
            }
            var body = await ReadBoundedAsync(response.Content, remaining, token).ConfigureAwait(false);
            remaining -= body.Length;
            using var document = StrictUpdateJson.Parse(body, UpdatePolicy.MetadataLimit);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > UpdatePolicy.PageSize)
                throw new UpdateDiscoveryException("update-metadata-invalid");
            foreach (var release in root.EnumerateArray())
            {
                var id = StrictUpdateJson.PositiveId(release.GetProperty("id"));
                if (!ids.Add(id)) throw new UpdateDiscoveryException("update-check-incomplete");
                if (release.GetProperty("draft").GetBoolean()) continue;
                var published = StrictUpdateJson.Utc(release.GetProperty("published_at"));
                if (published == newest) { ambiguous = true; continue; }
                if (published < newest) continue;
                newest = published;
                ambiguous = false;
                selected = release.Clone();
            }
            var next = HasNext(response, page) || root.GetArrayLength() == UpdatePolicy.PageSize;
            if (next && page == UpdatePolicy.PageLimit)
                throw new UpdateDiscoveryException("update-check-incomplete");
            if (next) continue;
            if (ambiguous) throw new UpdateDiscoveryException("update-check-incomplete");
            if (selected is null) throw new UpdateDiscoveryException("update-no-published-release");
            return new(ParseCandidate(selected.Value), page == 1 ? etag : null, page == 1);
        }
        throw new UpdateDiscoveryException("update-check-incomplete");
    }

    internal static ReleaseCandidate ParseCandidate(JsonElement release)
    {
        var tag = StrictUpdateJson.Text(release.GetProperty("tag_name"), 14);
        var assets = release.GetProperty("assets");
        if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() != 3)
            throw new InvalidDataException("update-assets-invalid");
        var candidate = new ReleaseCandidate(StrictUpdateJson.PositiveId(release.GetProperty("id")), tag,
            MsiReleaseVersion.Parse(tag, tag: true), StrictUpdateJson.Utc(release.GetProperty("published_at")),
            release.GetProperty("prerelease").GetBoolean(), assets.EnumerateArray().Select(a => new ReleaseAsset(
                StrictUpdateJson.PositiveId(a.GetProperty("id")), StrictUpdateJson.Text(a.GetProperty("name"), 64),
                StrictUpdateJson.PositiveId(a.GetProperty("size")))).ToImmutableArray());
        UpdatePolicy.ValidateCandidate(candidate);
        return candidate;
    }

    private static Uri PageUri(int page) => new($"{Endpoint}?per_page=20&page={page}");

    private static bool HasNext(HttpResponseMessage response, int page)
    {
        if (!response.Headers.TryGetValues("Link", out var headers)) return false;
        var text = string.Join(",", headers);
        if (text.Length > 4096) throw new UpdateDiscoveryException("update-metadata-invalid");
        var found = false;
        foreach (var part in text.Split(','))
        {
            var sections = part.Trim().Split(';', StringSplitOptions.TrimEntries);
            if (sections.Length != 2 || !sections[0].StartsWith('<') || !sections[0].EndsWith('>') ||
                !Uri.TryCreate(sections[0][1..^1], UriKind.Absolute, out var target) ||
                target.Scheme != "https" || target.Host != "api.github.com" || target.AbsolutePath != new Uri(Endpoint).AbsolutePath ||
                !string.IsNullOrEmpty(target.UserInfo) || !target.IsDefaultPort || target.Fragment.Length != 0)
                throw new UpdateDiscoveryException("update-metadata-invalid");
            if (sections[1] != "rel=\"next\"") continue;
            // GitHub may reorder the two query parameters, but cannot change their meaning.
            if (found || (target.Query != $"?per_page=20&page={page + 1}" && target.Query != $"?page={page + 1}&per_page=20"))
                throw new UpdateDiscoveryException("update-metadata-invalid");
            found = true;
        }
        return found;
    }

    private DateTimeOffset RetryAt(HttpResponseMessage response)
    {
        var now = time.GetUtcNow();
        var retry = now.AddMinutes(15);
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            retry = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now.Add(delta);
        if (response.Headers.RetryAfter?.Date is { } date && date > retry) retry = date;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) &&
            long.TryParse(values.SingleOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            seconds is >= 0 and <= 253402300799)
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (reset > retry) retry = reset;
        }
        return retry;
    }

    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (maximum < 0 || content.Headers.ContentLength is { } length && length > maximum)
            throw new InvalidDataException("update-metadata-too-large");
        await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, maximum - (int)buffer.Length + 1)), token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > maximum) throw new InvalidDataException("update-metadata-too-large");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

internal static class StrictUpdateJson
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes, int maximum)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("update-json-too-large");
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { CheckDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new InvalidDataException("update-json-duplicate");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }
    internal static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != names.Length ||
            element.EnumerateObject().Any(p => !names.Contains(p.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("update-json-fields");
    }
    internal static string Text(JsonElement element, int maximum)
    {
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } text ||
            text.Length is 0 || text.Length > maximum || text.Any(char.IsControl))
            throw new InvalidDataException("update-json-string");
        return text;
    }
    internal static long PositiveId(JsonElement element)
    {
        if (!element.TryGetInt64(out var id) || id <= 0) throw new InvalidDataException("update-json-id");
        return id;
    }
    internal static DateTimeOffset Utc(JsonElement element)
    {
        if (!element.TryGetDateTimeOffset(out var date) || date == default || date.Offset != TimeSpan.Zero)
            throw new InvalidDataException("update-json-time");
        return date;
    }
}