using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentSignaler.Contracts;

public sealed class DiscoveryException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Public DNS discovery validation. DNS does not authenticate the endpoint or sender.</summary>
public static partial class DiscoveryProtocol
{
    public const string DefaultRecordName = "signaler.cablesorter.io";
    public const int MaximumRecordNameLength = 253;
    public const int MaximumRecords = 16;
    public const int MaximumTxtLength = 2048;
    public const int MaximumTxtChunks = 32;

    public static string NormalizeRecordName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaximumRecordNameLength + 1 ||
            name.Any(c => c > 127 || char.IsControl(c)))
            throw new DiscoveryException("dns_record_name");
        var normalized = name.ToLowerInvariant();
        if (normalized.EndsWith('.')) normalized = normalized[..^1];
        if (normalized.Length > MaximumRecordNameLength || !normalized.Contains('.') ||
            normalized.Split('.').Any(label => label.Length is < 1 or > 63 ||
                !HostnameLabel().IsMatch(label)) ||
            Uri.CheckHostName(normalized) != UriHostNameType.Dns)
            throw new DiscoveryException("dns_record_name");
        return normalized;
    }

    public static Uri ValidateEndpoint(Uri endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || !endpoint.IsDefaultPort ||
            endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || !EndpointHost().IsMatch(endpoint.IdnHost) ||
            endpoint.IdnHost.Contains("-inspect", StringComparison.Ordinal) || endpoint.IdnHost.Contains("-relay", StringComparison.Ordinal) ||
            endpoint.OriginalString != "https://" + endpoint.IdnHost && endpoint.OriginalString != "https://" + endpoint.IdnHost + "/" &&
            endpoint.OriginalString != "https://" + endpoint.IdnHost + ":443" && endpoint.OriginalString != "https://" + endpoint.IdnHost + ":443/")
            throw new DiscoveryException("dns_endpoint");
        var label = endpoint.IdnHost.Split('.')[0];
        if (label.Length > 63 || !int.TryParse(label[(label.LastIndexOf('-') + 1)..], CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            throw new DiscoveryException("dns_endpoint");
        return endpoint;
    }

    [GeneratedRegex("\\A[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostnameLabel();
    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{1,98}[a-z0-9]-[1-9][0-9]{0,4}\\.[a-z0-9]{2,20}\\.devtunnels\\.ms\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointHost();
}
