using System.Security.Cryptography;
using System.Text;

namespace AgentSignaler.Remote;

public enum IntegrationCapability
{
    NotInstalled, VerificationRequired, Verified, PartiallySupported,
    BlockedByPolicy, VerificationFailed, DiscoveryFailed, Configured
}

public sealed record IntegrationTarget
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string InstallationId { get; init; } = "";
    public string HostVersion { get; init; } = "unknown";
    public string AdapterVersion { get; init; } = HookAdapters.Version;
    public string ScopeId { get; init; } = "";
    public string HookDirectory { get; init; } = "";
    public string? SettingsPath { get; init; }
    public string? ExecutablePath { get; init; }
    public bool IsCustom { get; init; }
    public IntegrationCapability Capability { get; init; } = IntegrationCapability.VerificationRequired;
    public IReadOnlyList<string> SupportedEvents { get; init; } = [];
    public string Reason { get; init; } = "";
    public string Provenance { get; init; } = "candidate";
    public bool CanInstall => Capability is IntegrationCapability.Verified or IntegrationCapability.PartiallySupported or IntegrationCapability.Configured &&
        SupportedEvents is { Count: > 0 };

    public void Validate()
    {
        if (Kind is not ("copilot-cli" or "visual-studio" or "vscode") ||
            !RemoteConfiguration.ValidText(Id, 128) || !RemoteConfiguration.ValidText(ScopeId, 128) ||
            !RemoteConfiguration.ValidText(InstallationId, 256) || !RemoteConfiguration.ValidText(DisplayName, 256) ||
            !RemoteConfiguration.ValidText(HostVersion, 64) || AdapterVersion != HookAdapters.Version ||
            !Enum.IsDefined(Capability) || SupportedEvents is null || SupportedEvents.Count > 16 ||
            SupportedEvents.Distinct(StringComparer.Ordinal).Count() != SupportedEvents.Count ||
            SupportedEvents.Any(e => !HookAdapters.Events(Kind).Contains(e, StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid integration target or adapter event set.");
        IntegrationScope.CanonicalDirectory(HookDirectory);
        if (SettingsPath is not null)
        {
            IntegrationScope.CanonicalDirectory(Path.GetDirectoryName(SettingsPath)!);
            if (Kind != "vscode" || Path.GetFileName(SettingsPath) != "settings.json")
                throw new InvalidDataException("Invalid profile settings path.");
        }
        if (Kind == "vscode" && SettingsPath is null)
            throw new InvalidDataException("A local VS Code profile settings path is required.");
    }
}

public static class IntegrationScope
{
    public static string Id(string kind, string identity) =>
        kind + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..32].ToLowerInvariant();

    public static string CanonicalDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith(@"\\", StringComparison.Ordinal) || path.Any(char.IsControl) ||
            path.Contains('"') || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            throw new InvalidDataException("Only absolute local Windows paths are supported; remote hosts and UNC paths are excluded.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Reparse-point scopes require an explicit physical path to prevent duplicate loading.");
        return full;
    }
}
