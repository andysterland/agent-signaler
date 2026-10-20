using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSignaler.Contracts;

public enum DiscoveryState { Available, Unavailable }

public sealed record DiscoveryConnectionPackage(Guid DiscoveryId, Uri BlobSasUri, string SigningPublicKey)
{
    public override string ToString() => "Discovery connection package (protected)";
}

public sealed record DiscoveryDocument(int Version, Guid DiscoveryId, long Revision,
    DateTimeOffset PublishedAtUtc, DateTimeOffset ExpiresAtUtc, DiscoveryState State,
    Uri? DashboardBaseUrl = null)
{
    public override string ToString() => "Signed discovery document";
}

public sealed record DiscoveryReplayState(Guid DiscoveryId, long Revision, string PayloadFingerprint);
public sealed record DiscoveryVerificationResult(DiscoveryDocument Document, DiscoveryReplayState ReplayState);

public sealed class DiscoveryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static partial class DiscoveryProtocol
{
    public const int Version = 1;
    public const string PackagePrefix = "agentsignaler-discovery:v1:";
    public const int MaximumPackageBytes = 16 * 1024;
    public const int MaximumEnvelopeBytes = 8 * 1024;
    public const int MaximumDepth = 8;
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);

    public static DiscoveryConnectionPackage ParsePackageJson(ReadOnlySpan<byte> json)
    {
        using var parsed = ParseObject(json, MaximumPackageBytes, "version", "discoveryId", "blobSasUri", "signingPublicKey");
        var root = parsed.RootElement;
        if (Number(root, "version") != Version) throw new DiscoveryException("discovery_package_version");
        var package = new DiscoveryConnectionPackage(Identity(root), AbsoluteUri(Text(root, "blobSasUri")), Text(root, "signingPublicKey"));
        ValidatePackage(package);
        return package;
    }

    public static DiscoveryDocument ParseDocument(ReadOnlySpan<byte> json)
    {
        using var parsed = ParseObject(json, MaximumEnvelopeBytes, "version", "discoveryId", "revision",
            "publishedAtUtc", "expiresAtUtc", "state", "dashboardBaseUrl");
        var root = parsed.RootElement;
        if (!root.TryGetProperty("revision", out var revision) || revision.ValueKind != JsonValueKind.Number || !revision.TryGetInt64(out var revisionValue))
            throw new DiscoveryException("discovery_revision");
        var state = Text(root, "state") switch
        {
            "available" => DiscoveryState.Available,
            "unavailable" => DiscoveryState.Unavailable,
            _ => throw new DiscoveryException("discovery_state")
        };
        var hasEndpoint = root.TryGetProperty("dashboardBaseUrl", out _);
        if (hasEndpoint != (state == DiscoveryState.Available)) throw new DiscoveryException("discovery_endpoint_state");
        var document = new DiscoveryDocument(Number(root, "version"), Identity(root), revisionValue,
            Timestamp(root, "publishedAtUtc"), Timestamp(root, "expiresAtUtc"), state,
            hasEndpoint ? AbsoluteUri(Text(root, "dashboardBaseUrl")) : null);
        ValidateDocument(document);
        return document;
    }

    public static void ValidatePackage(DiscoveryConnectionPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.DiscoveryId == Guid.Empty || string.IsNullOrEmpty(package.SigningPublicKey) || package.SigningPublicKey.Length > 256)
            throw new DiscoveryException("discovery_package_fields");
        ValidateBlobSasUri(package.BlobSasUri);
    }

    public static void ValidateDocument(DiscoveryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != Version || document.DiscoveryId == Guid.Empty || document.Revision <= 0)
            throw new DiscoveryException("discovery_document_fields");
        if (document.PublishedAtUtc.Offset != TimeSpan.Zero || document.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            document.PublishedAtUtc == DateTimeOffset.MinValue || document.ExpiresAtUtc <= document.PublishedAtUtc ||
            document.ExpiresAtUtc - document.PublishedAtUtc > MaximumLifetime)
            throw new DiscoveryException("discovery_validity");
        if (document.State == DiscoveryState.Available && document.DashboardBaseUrl is not null)
            ValidateEndpoint(document.DashboardBaseUrl);
        else if (document.State != DiscoveryState.Unavailable || document.DashboardBaseUrl is not null)
            throw new DiscoveryException("discovery_endpoint_state");
    }

    public static Uri ValidateEndpoint(Uri endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || !EndpointHost().IsMatch(endpoint.IdnHost) ||
            endpoint.IdnHost.Contains("-inspect", StringComparison.Ordinal) || endpoint.IdnHost.Contains("-relay", StringComparison.Ordinal) ||
            endpoint.OriginalString != "https://" + endpoint.IdnHost && endpoint.OriginalString != "https://" + endpoint.IdnHost + "/")
            throw new DiscoveryException("discovery_endpoint");
        var label = endpoint.IdnHost.Split('.')[0];
        if (label.Length > 63 || !int.TryParse(label[(label.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture, out var port) || port > 65535)
            throw new DiscoveryException("discovery_endpoint");
        return endpoint;
    }

    public static Uri ValidateBlobSasUri(Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Fragment.Length != 0 || uri.Query.Length == 0 || uri.OriginalString.Length > 8192 || !BlobHost().IsMatch(uri.IdnHost) ||
            !uri.OriginalString.StartsWith("https://" + uri.IdnHost + "/", StringComparison.Ordinal))
            throw new DiscoveryException("discovery_blob_uri");
        var segments = uri.AbsolutePath.Split('/');
        if (segments.Length < 3 || !ContainerName().IsMatch(segments[1]) || segments[1].Contains("--", StringComparison.Ordinal) ||
            segments.Skip(2).Any(segment => segment.Length == 0 || Uri.UnescapeDataString(segment) is "." or "..") ||
            uri.AbsolutePath.Contains('%') || uri.AbsolutePath.Contains('\\') ||
            uri.OriginalString[..uri.OriginalString.IndexOf('?')] != "https://" + uri.IdnHost + uri.AbsolutePath)
            throw new DiscoveryException("discovery_blob_scope");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var split = pair.Split('=', 2);
            if (split.Length != 2 || !fields.TryAdd(split[0], Uri.UnescapeDataString(split[1])) || fields[split[0]].Length == 0)
                throw new DiscoveryException("discovery_sas_shape");
        }
        if (fields.Count != 5 || !fields.TryGetValue("sr", out var scope) || scope != "b" ||
            !fields.TryGetValue("spr", out var transport) || transport != "https" ||
            !fields.TryGetValue("si", out var policy) || policy.Length > 64 || policy.Any(c => c > 127 || char.IsControl(c)) ||
            !fields.TryGetValue("sv", out var version) || !DateOnly.TryParseExact(version, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !fields.TryGetValue("sig", out var signature) || signature.Length > 128)
            throw new DiscoveryException("discovery_sas_scope");
        try
        {
            if (Convert.FromBase64String(signature).Length != 32) throw new DiscoveryException("discovery_sas_signature");
        }
        catch (FormatException) { throw new DiscoveryException("discovery_sas_signature"); }
        return uri;
    }

    public static JsonDocument ParseObject(ReadOnlySpan<byte> json, int maximumBytes, params string[] allowedProperties)
    {
        if (json.Length == 0 || json.Length > maximumBytes) throw new DiscoveryException("discovery_size");
        JsonDocument parsed;
        try { parsed = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = MaximumDepth }); }
        catch (JsonException) { throw new DiscoveryException("discovery_json"); }
        try
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new DiscoveryException("discovery_json_object");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in parsed.RootElement.EnumerateObject())
                if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                    throw new DiscoveryException("discovery_json_property");
            return parsed;
        }
        catch { parsed.Dispose(); throw; }
    }

    public static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw new DiscoveryException("discovery_json_field");
        return value.GetString()!;
    }

    private static int Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new DiscoveryException("discovery_json_field");
        return number;
    }

    private static Guid Identity(JsonElement root)
    {
        if (!Guid.TryParseExact(Text(root, "discoveryId"), "D", out var identity) || identity == Guid.Empty)
            throw new DiscoveryException("discovery_identity");
        return identity;
    }

    private static DateTimeOffset Timestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            !value.TryGetDateTimeOffset(out var timestamp) || timestamp.Offset != TimeSpan.Zero ||
            !(value.GetString()!.EndsWith('Z') || value.GetString()!.EndsWith("+00:00", StringComparison.Ordinal)))
            throw new DiscoveryException("discovery_timestamp");
        return timestamp;
    }

    private static Uri AbsoluteUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? uri : throw new DiscoveryException("discovery_uri");

    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{1,98}[a-z0-9]-[1-9][0-9]{0,4}\\.[a-z0-9]{2,20}\\.devtunnels\\.ms\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointHost();
    [GeneratedRegex("\\A[a-z0-9]{3,24}\\.blob\\.core\\.windows\\.net\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BlobHost();
    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{1,61}[a-z0-9]\\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerName();
}
