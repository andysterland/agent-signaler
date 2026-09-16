using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace AgentSignaler.Remote;

public interface IIntegrationStartup
{
    string? Read(string name);
    void Replace(string name, string? expected, string? command);
    bool IsDisabled(string name);
}

public static class IntegrationStartup
{
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

[SupportedOSPlatform("windows")]
public sealed class WindowsIntegrationStartup : IIntegrationStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        if (value is not string command || key!.GetValueKind(name) != RegistryValueKind.String)
            throw new InvalidDataException("An unrelated startup value occupies the client registration.");
        return command;
    }

    public void Replace(string name, string? expected, string? command)
    {
        if (!string.Equals(Read(name), expected, StringComparison.Ordinal))
            throw new InvalidDataException("Startup registration changed; the unrelated value will not be overwritten.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (command is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, command, RegistryValueKind.String);
        if (!string.Equals(Read(name), command, StringComparison.Ordinal))
            throw new InvalidOperationException("Client startup registration could not be verified.");
    }

    public bool IsDisabled(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
        return key?.GetValue(name) is byte[] { Length: >= 4 } value && (value[0] == 3 || value[0] == 7);
    }
}
