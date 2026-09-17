using System.Security.Cryptography;
using System.Text;

namespace AgentSignaler.Remote;

public interface IIntegrationStartup
{
    string? Read(string name);
    void Replace(string name, string? expected, string? command);
    bool IsDisabled(string name);

    IntegrationStartupState Capture(string name) => new(Read(name));
    IntegrationStartupState Prepare(string name, IntegrationStartupState before, string? command) => new(command);
    void ReplaceState(string name, IntegrationStartupState before, IntegrationStartupState after) =>
        Replace(name, before.Command, after.Command);
    void RestoreState(string name, IntegrationStartupState before, IntegrationStartupState after)
    {
        if (!IntegrationStartup.Equivalent(Capture(name), before)) ReplaceState(name, after, before);
    }
    void RestoreLegacyState(string name, string? original, string? applied)
    {
        if (Read(name) != original) Replace(name, applied, original);
    }
}

public sealed record IntegrationStartupState(string? Command, byte[]? Shortcut = null,
    string? LegacyCommand = null, byte[]? ShortcutApproval = null, byte[]? LegacyApproval = null);

public static class IntegrationStartup
{
    public static bool Equivalent(IntegrationStartupState a, IntegrationStartupState b) =>
        a.Command == b.Command && a.LegacyCommand == b.LegacyCommand &&
        Equal(a.Shortcut, b.Shortcut) && Equal(a.ShortcutApproval, b.ShortcutApproval) &&
        Equal(a.LegacyApproval, b.LegacyApproval);

    internal static bool Equal(byte[]? a, byte[]? b) =>
        a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);

    internal static void ValidateJournalStates(IntegrationStartupState? before, IntegrationStartupState? after,
        string? beforeCommand, string? afterCommand)
    {
        if (before is null && after is null) return;
        if (before is null || after is null || before.Command != beforeCommand || after.Command != afterCommand)
            throw new InvalidDataException("Startup recovery state mismatch.");
        foreach (var state in new[] { before, after })
            if (state.Shortcut?.Length > 65536 || state.ShortcutApproval?.Length is < 4 or > 256 ||
                state.LegacyApproval?.Length is < 4 or > 256 ||
                (state.LegacyCommand is not null && state.LegacyCommand != state.Command))
                throw new InvalidDataException("Invalid startup recovery state.");
    }

    public static string Name(string configPath) => "AgentSignaler-Client-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalConfigPath(configPath))))[..24];

    public static string CanonicalConfigPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new InvalidDataException("An absolute configuration path is required.");
        return Path.GetFullPath(path).ToUpperInvariant();
    }

    public static string Command(string clientPath, string configPath)
    {
        if (!Path.IsPathFullyQualified(clientPath) || clientPath.Any(char.IsControl) ||
            !string.Equals(Path.GetFileName(clientPath), "AgentSignaler.Client.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An absolute path to AgentSignaler.Client.exe is required.");
        return $"{ScheduledTaskDefinition.QuoteArgument(Path.GetFullPath(clientPath))} --background --config " +
            ScheduledTaskDefinition.QuoteArgument(CanonicalConfigPath(configPath));
    }
}
