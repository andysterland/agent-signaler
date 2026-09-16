using System.Text.RegularExpressions;

namespace AgentSignaler.Service;

public sealed partial record WindowsAppConnection(
    Uri DevCenterEndpoint,
    string ProjectName,
    string DevBoxName,
    string AzureAccountUpn,
    Guid AzureTenantId,
    string? LastKnownConnectionUri,
    DateTimeOffset? ConnectionUriRetrievedAtUtc)
{
    public void Validate()
    {
        ValidateEndpoint(DevCenterEndpoint);
        ValidateResourceName(ProjectName, nameof(ProjectName));
        ValidateResourceName(DevBoxName, nameof(DevBoxName));
        ValidateUpn(AzureAccountUpn);
        if (AzureTenantId == Guid.Empty)
            throw new ArgumentException("Azure tenant ID must be a non-empty GUID.", nameof(AzureTenantId));
        if ((LastKnownConnectionUri is null) != (ConnectionUriRetrievedAtUtc is null))
            throw new ArgumentException("The cached connection URI and retrieval time must both be present or absent.");
        if (LastKnownConnectionUri is not null)
            ValidateConnectionUri(LastKnownConnectionUri, AzureAccountUpn);
    }

    public static void ValidateEndpoint(Uri? endpoint)
    {
        if (endpoint is null || !endpoint.IsAbsoluteUri ||
            endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 ||
            endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" ||
            !endpoint.IsDefaultPort || !IsEndpointHostValid(endpoint) ||
            endpoint.OriginalString.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Dev Center endpoint must be a public Azure HTTPS root endpoint.", nameof(endpoint));
    }

    public static void ValidateResourceName(string? name, string parameterName = "name")
    {
        if (name is null || !ResourceNamePattern().IsMatch(name))
            throw new ArgumentException("Resource name must contain 3-63 supported characters.", parameterName);
    }

    public static Uri ValidateConnectionUri(string connectionUri, string azureAccountUpn)
    {
        ValidateUpn(azureAccountUpn);
        const string error = "The connection URI is unsafe or unsupported.";
        const string commandPrefix = "connect?";
        if (string.IsNullOrEmpty(connectionUri) || connectionUri.Length > 4096 ||
            connectionUri.Any(char.IsControl))
            throw new ArgumentException(error, nameof(connectionUri));
        for (var index = 0; index < connectionUri.Length; index++)
        {
            if (connectionUri[index] != '%') continue;
            if (index + 2 >= connectionUri.Length || !Uri.IsHexDigit(connectionUri[index + 1]) ||
                !Uri.IsHexDigit(connectionUri[index + 2]))
                throw new ArgumentException(error, nameof(connectionUri));
            index += 2;
        }

        // Inspect the original text as well: System.Uri can canonicalize unsafe command shapes.
        var colon = connectionUri.IndexOf(':');
        if (colon < 0 || !connectionUri[..colon].Equals("ms-cloudpc", StringComparison.OrdinalIgnoreCase) ||
            !connectionUri[(colon + 1)..].StartsWith(commandPrefix, StringComparison.Ordinal) ||
            connectionUri.Contains('#') ||
            !Uri.TryCreate(connectionUri, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("ms-cloudpc", StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath != "connect" || uri.Authority.Length != 0 ||
            uri.UserInfo.Length != 0 || uri.Port != -1 || uri.Fragment.Length != 0)
            throw new ArgumentException(error, nameof(connectionUri));

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in connectionUri[(colon + 1 + commandPrefix.Length)..].Split('&'))
        {
            var equals = parameter.IndexOf('=');
            if (equals <= 0)
                throw new ArgumentException(error, nameof(connectionUri));
            var name = parameter[..equals];
            if (name is not ("cpcid" or "username" or "environment" or "version" or "source"))
                throw new ArgumentException(error, nameof(connectionUri));
            var value = Uri.UnescapeDataString(parameter[(equals + 1)..]);
            if (value.Length is < 1 or > 512 || value.Any(char.IsControl) || !values.TryAdd(name, value))
                throw new ArgumentException(error, nameof(connectionUri));
        }
        if (values.Count != 5 || !Guid.TryParse(values["cpcid"], out var cloudPcId) || cloudPcId == Guid.Empty ||
            !values["username"].Equals(azureAccountUpn, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(error, nameof(connectionUri));
        return uri;
    }

    private static bool IsEndpointHostValid(Uri endpoint)
    {
        try { return EndpointHostPattern().IsMatch(endpoint.IdnHost); }
        catch (UriFormatException) { return false; }
    }

    public static void ValidateUpn(string? upn)
    {
        var at = upn?.IndexOf('@') ?? -1;
        if (upn is null || upn.Length is < 1 or > 320 || at <= 0 || at == upn.Length - 1 ||
            at != upn.LastIndexOf('@') || upn.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Azure account UPN must be trimmed and contain one non-edge @ without whitespace or controls.", nameof(upn));
    }

    [GeneratedRegex(@"\A[a-z0-9-]+\.[a-z0-9-]+\.devcenter\.azure\.com\z", RegexOptions.CultureInvariant)]
    private static partial Regex EndpointHostPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{2,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceNamePattern();
}
