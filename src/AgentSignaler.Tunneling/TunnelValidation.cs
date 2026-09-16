using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentSignaler.Tunneling;

public static partial class TunnelValidation
{
    public const string SupportedCliVersion = "1.0.2030+fc9273aa0f";

    internal static void VerifyCliVersion(CliCommandResult result)
    {
        const string prefix = "Tunnel CLI version: ";
        const string welcomePrefix = "CLI version: ";
        var expected = prefix + SupportedCliVersion;
        var outputLines = result.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var lines = outputLines.Concat(result.StandardError.Split('\n').Select(line => line.TrimEnd('\r')));
        if (result.ExitCode != 0 || !outputLines.Contains(expected, StringComparer.Ordinal) ||
            lines.Any(line =>
                (line.StartsWith("Tunnel CLI version:", StringComparison.Ordinal) && line != expected) ||
                (line.StartsWith("CLI version:", StringComparison.Ordinal) && line != welcomePrefix + SupportedCliVersion)))
            throw new TunnelException(
                $"Install the qualified Microsoft-signed Dev Tunnels CLI {SupportedCliVersion}.", TunnelState.Unsupported);
    }

    public static string AccountOwner(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        if (Text(root, "status") != "Logged in")
            throw new TunnelException("Sign in explicitly with the Dev Tunnels CLI, then retry.", TunnelState.AccountRequired);
        if (Text(root, "provider") != "microsoft")
            throw new TunnelException("Only the qualified Microsoft account identity schema is supported.", TunnelState.Unsupported);
        if (!Guid.TryParse(Text(root, "tenantId"), out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParse(Text(root, "objectId"), out var user) || user == Guid.Empty)
            throw new TunnelException("The CLI did not return a stable Microsoft account identity.", TunnelState.Unsupported);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"microsoft\n{tenant:D}\n{user:D}")));
    }

    public static void ValidateId(string id, bool full = true)
    {
        if (id.Length > 160 || !(full ? FullId() : PendingId()).IsMatch(id))
            throw new TunnelException("The CLI returned an unsupported tunnel identifier.", TunnelState.Unsupported);
    }

    internal static bool IsConfirmedAbsent(CliCommandResult result, string id)
    {
        if (result.ExitCode != 2 || id.Length > 160) return false;
        string expected;
        if (FullId().IsMatch(id))
        {
            var separator = id.LastIndexOf('.');
            expected = $"Tunnel not found in {id[(separator + 1)..]}: {id[..separator]}";
        }
        else if (PendingId().IsMatch(id)) expected = $"Tunnel not found: {id}";
        else return false;
        static string? LastLine(string output) => output.Split('\n')
            .Select(line => line.TrimEnd('\r')).LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        // The qualified CLI can prepend its welcome/license banner. Only its exact final
        // not-found diagnostic for the requested ID and exit code proves absence.
        return LastLine(result.StandardError) == expected ||
            (string.IsNullOrWhiteSpace(result.StandardError) && LastLine(result.StandardOutput) == expected);
    }

    public static Uri ValidatePublicUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.UserInfo.Length != 0 || !uri.IsDefaultPort || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !uri.IdnHost.EndsWith(".devtunnels.ms", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.Contains("-inspect", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.StartsWith("inspect.", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.Contains("-relay", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.StartsWith("relay.", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.Contains(".relay.", StringComparison.OrdinalIgnoreCase))
            throw new TunnelException("The host returned an unsupported public HTTPS endpoint.");
        var root = $"https://{uri.Authority}";
        if (!value.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !value.Equals(root + "/", StringComparison.OrdinalIgnoreCase))
            throw new TunnelException("The public endpoint must be an unambiguous HTTPS root URL.");
        return uri;
    }

    internal static JsonDocument Parse(string json)
    {
        if (json.Length > 65536) throw new TunnelException("CLI JSON exceeded the output limit.");
        try
        {
            var result = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            try { CheckDuplicates(result.RootElement); return result; }
            catch { result.Dispose(); throw; }
        }

        catch (JsonException) { throw new TunnelException("Unsupported or malformed CLI JSON.", TunnelState.Unsupported); }
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new TunnelException("Ambiguous duplicate CLI JSON properties.", TunnelState.Unsupported);
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }

    internal static JsonElement Property(JsonElement element, string name, JsonValueKind kind)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value) || value.ValueKind != kind)
            throw new TunnelException("The CLI returned an unsupported response schema.", TunnelState.Unsupported);
        return value;
    }

    internal static string Text(JsonElement element, string name) => Property(element, name, JsonValueKind.String).GetString()!;
    internal static int Number(JsonElement element, string name)
    {
        if (!Property(element, name, JsonValueKind.Number).TryGetInt32(out var number))
            throw new TunnelException("The CLI returned an invalid number.", TunnelState.Unsupported);
        return number;
    }

    internal static void NoTunnelAcl(JsonElement entries)
    {
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 0)
            throw new TunnelException("Tunnel-level access control drift detected; no changes were made.");
    }

    public static void VerifyPortAcl(string json)
    {
        using var doc = Parse(json);
        PortAcl(Property(doc.RootElement, "accessControlEntries", JsonValueKind.Array));
    }

    internal static void PortAcl(JsonElement entries)
    {
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 1)
            throw new TunnelException("The receiver port must have exactly one anonymous connect permission.");
        var entry = entries[0];
        if (Text(entry, "type") != "Anonymous" ||
            Property(entry, "subjects", JsonValueKind.Array).GetArrayLength() != 0)
            throw new TunnelException("Unsafe receiver port access control.");
        var scopes = Property(entry, "scopes", JsonValueKind.Array);
        if (scopes.GetArrayLength() != 1 || scopes[0].ValueKind != JsonValueKind.String || scopes[0].GetString() != "connect")
            throw new TunnelException("Only anonymous connect permission is allowed.");
        foreach (var property in entry.EnumerateObject())
        {
            if (property.Name is "type" or "subjects" or "scopes") continue;
            if (property.Name is "isDeny" or "isInverse" && property.Value.ValueKind == JsonValueKind.False) continue;
            if (property.Name == "provider" && property.Value.ValueKind == JsonValueKind.Null) continue;
            throw new TunnelException("Unsupported or unsafe receiver ACL flags.");
        }
    }

    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{2,100}\\.[a-z0-9]{2,20}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex FullId();
    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{2,100}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PendingId();
}
