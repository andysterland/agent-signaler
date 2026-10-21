using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentSignaler.Updates;

public sealed record UpdatePinnedRelease(ReleaseCandidate Candidate, string ChecksumSha256, string PackageSha256);

public sealed class ReleasePinning(HttpClient http, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<UpdatePinnedRelease> PinAsync(ReleaseCandidate offered, UpdatePackage package,
        CancellationToken cancellationToken = default)
    {
        UpdatePolicy.ValidateCandidate(offered);
        _ = UpdatePolicy.AssetName(package);
        using var deadline = new CancellationTokenSource(UpdatePolicy.MetadataDeadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = linked.Token;
        try
        {
            using var request = Request(new($"https://api.github.com/repos/{UpdatePolicy.Repository}/releases/{offered.Id}"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("update-selected-release-unavailable");
            var metadata = await ReleaseDiscovery.ReadBoundedAsync(response.Content, UpdatePolicy.MetadataLimit, token).ConfigureAwait(false);
            using var document = StrictUpdateJson.Parse(metadata, UpdatePolicy.MetadataLimit);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean()) throw new InvalidDataException("update-selected-release-changed");
            var selected = ReleaseDiscovery.ParseCandidate(root);
            if (selected.Id != offered.Id || selected.Tag != offered.Tag || selected.PublishedAt != offered.PublishedAt ||
                selected.Prerelease != offered.Prerelease || !selected.Assets.OrderBy(a => a.Id).SequenceEqual(offered.Assets.OrderBy(a => a.Id)))
                throw new InvalidDataException("update-selected-release-changed");
            var asset = selected.Assets.Single(a => a.Name == "SHA256SUMS.txt");
            var manifest = await ReadManifestAsync(selected, asset, Math.Min(65536, UpdatePolicy.MetadataLimit - metadata.Length), token).ConfigureAwait(false);
            var checksums = ReadChecksums(manifest);
            return new(selected, Convert.ToHexString(SHA256.HashData(manifest)), checksums[UpdatePolicy.AssetName(package)]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new UpdateDiscoveryException("update-pin-timeout"); }
        catch (HttpRequestException) { throw new UpdateDiscoveryException("update-pin-network-failed"); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException or IOException or InvalidDataException)
        { throw new UpdateDiscoveryException("update-pin-validation-failed"); }
    }

    private async Task<byte[]> ReadManifestAsync(ReleaseCandidate selected, ReleaseAsset asset, int maximum, CancellationToken token)
    {
        var target = new Uri($"https://api.github.com/repos/{UpdatePolicy.Repository}/releases/assets/{asset.Id}");
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            using var request = Request(target, binary: true);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var bytes = await ReleaseDiscovery.ReadBoundedAsync(response.Content, maximum, token).ConfigureAwait(false);
                if (bytes.LongLength != asset.Size) throw new InvalidDataException("update-checksum-size-changed");
                return bytes;
            }
            if (redirects == 5 || response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) ||
                response.Headers.Location is not { } location)
                throw new InvalidDataException("update-checksum-download-failed");
            target = new Uri(target, location);
            if (target.AbsoluteUri.Length > 8192 || target.Scheme != "https" || !target.IsDefaultPort ||
                target.UserInfo.Length != 0 || target.Fragment.Length != 0 ||
                target.Host is not ("github.com" or "objects.githubusercontent.com" or "release-assets.githubusercontent.com") ||
                target.Host == "github.com" && (target.AbsolutePath != $"/{UpdatePolicy.Repository}/releases/download/{selected.Tag}/SHA256SUMS.txt" || target.Query.Length != 0))
                throw new InvalidDataException("update-checksum-redirect-rejected");
        }
        throw new InvalidDataException("update-checksum-download-failed");
    }

    private static HttpRequestMessage Request(Uri target, bool binary = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.UserAgent.ParseAdd("AgentSignaler-Updates/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(binary ? "application/octet-stream" : "application/vnd.github+json"));
        if (target.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    public static IReadOnlyDictionary<string, string> ReadChecksums(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 65536) throw new InvalidDataException("update-checksums-limit");
        foreach (var value in bytes) if (value > 127 || value == 0) throw new InvalidDataException("update-checksums-encoding");
        using var reader = new StringReader(Encoding.ASCII.GetString(bytes));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? line;
        var count = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            if (++count > 128) throw new InvalidDataException("update-checksums-limit");
            if (string.IsNullOrWhiteSpace(line)) continue;
            var match = Regex.Match(line, @"\A([a-fA-F0-9]{64})  (AgentSignaler\.(?:Dashboard|Remote)\.msi)\z", RegexOptions.CultureInvariant);
            if (!match.Success || !result.TryAdd(match.Groups[2].Value, match.Groups[1].Value.ToUpperInvariant()))
                throw new InvalidDataException("update-checksums-invalid");
        }
        if (result.Count != 2) throw new InvalidDataException("update-checksums-missing");
        return result;
    }
}