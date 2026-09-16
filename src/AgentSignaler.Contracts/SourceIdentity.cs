using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentSignaler.Contracts;

/// <summary>The originating product and local integration scope, not the reporting agent.</summary>
public sealed record SourceDescriptor(string Kind, string ScopeId, string Version = "unknown")
{
    public static SourceDescriptor LegacyCli { get; } = new("copilot-cli", "legacy-cli");

    [JsonIgnore]
    public bool IsValid => Kind is "copilot-cli" or "visual-studio" or "vscode" &&
        ValidText(ScopeId, 128) && ValidText(Version, 64);

    private static bool ValidText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

public static class SourceIdentity
{
    /// <summary>Returns a bounded internal key; keep the original host session ID for display.</summary>
    public static string SessionKey(SourceDescriptor? source, string hostSessionId)
    {
        source ??= SourceDescriptor.LegacyCli;
        ArgumentNullException.ThrowIfNull(hostSessionId);
        if (!source.IsValid) throw new ArgumentException("Invalid source descriptor.", nameof(source));
        // Length prefixes avoid delimiter collisions; versions are metadata, never identity.
        var identity = FormattableString.Invariant(
            $"{source.Kind.Length}:{source.Kind}{source.ScopeId.Length}:{source.ScopeId}{hostSessionId.Length}:{hostSessionId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }
}
