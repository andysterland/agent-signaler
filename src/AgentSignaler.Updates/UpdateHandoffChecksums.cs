using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AgentSignaler.Updates;

public sealed record UpdateChecksumPins(string ChecksumSha256, string PackageSha256);

// Explicit-click preflight only. Caller supplies the public no-redirect/no-credentials
// transport factory used for release discovery. No token, redirect URI or raw body is persisted.
public sealed class UpdateHandoffChecksumReader(HttpClient http)
{
    public async Task<UpdateChecksumPins> ReadAsync(ReleaseCandidate candidate, UpdatePackage package, CancellationToken cancellationToken = default)
    {
        UpdatePolicy.ValidateCandidate(candidate);
        if (http.DefaultRequestHeaders.Authorization is not null) throw new InvalidOperationException("update_public_transport_required");
        var asset = candidate.Assets.Single(a => a.Name == "SHA256SUMS.txt");
        var uri = new Uri($"https://api.github.com/repos/{UpdatePolicy.Repository}/releases/assets/{asset.Id}");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(15));
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            RequireApproved(uri);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AgentSignaler-Updater/1.0");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
                throw new InvalidOperationException("update_redirect_transport_required");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (response.Headers.Location is not { } location || redirect == 5) throw new InvalidDataException("update_redirect_invalid");
                uri = new(uri, location);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("update_checksum_download");
            using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
            var bytes = new byte[checked((int)asset.Size)];
            await stream.ReadExactlyAsync(bytes, budget.Token).ConfigureAwait(false);
            var overflow = new byte[1];
            if (await stream.ReadAsync(overflow, budget.Token).ConfigureAwait(false) != 0) throw new InvalidDataException("update_checksum_size");
            return Parse(bytes, package);
        }
        throw new InvalidDataException("update_redirect_invalid");
    }

    public static UpdateChecksumPins Parse(ReadOnlySpan<byte> bytes, UpdatePackage package)
    {
        if (bytes.Length is < 1 or > 65536) throw new InvalidDataException("update_checksum_limit");
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var lines = text.Split('\n');
        if (lines.Length > 128) throw new InvalidDataException("update_checksum_entries");
        var checksums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length < 67 || line[64..66] != "  " || !UpdateHandoffLauncher.IsHash(line[..64]) ||
                line[66..] is not ("AgentSignaler.Dashboard.msi" or "AgentSignaler.Remote.msi") ||
                !checksums.TryAdd(line[66..], line[..64].ToUpperInvariant())) throw new InvalidDataException("update_checksum_entry");
        }
        if (checksums.Count != 2) throw new InvalidDataException("update_checksum_missing");
        return new(Convert.ToHexString(SHA256.HashData(bytes)), checksums[UpdatePolicy.AssetName(package)]);
    }

    private static void RequireApproved(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.IdnHost is "api.github.com" or "github.com" || uri.IdnHost.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("update_redirect_host");
    }
}