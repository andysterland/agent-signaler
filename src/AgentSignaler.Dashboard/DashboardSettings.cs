using System.Text.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace AgentSignaler.Dashboard;

internal sealed record DashboardSettings
{
    public int Port { get; init; } = 51820;
    public bool Compact { get; init; } = true;
    public bool ShowCompactViewWhenMinimized { get; init; } = true;
    public string Theme { get; init; } = "System";
    public DashboardConnectionMode ConnectionMode { get; init; } = DashboardConnectionMode.DevTunnel;
    public string? DevTunnelCliPath { get; init; }
    public string? AzureCliPath { get; init; }
    public Guid? DevBoxSubscriptionId { get; init; }
    public string? DevCenterName { get; init; }
    public bool AutoStartSharing { get; init; } = true;

    [JsonIgnore]
    public bool ShouldStartSharing => ConnectionMode == DashboardConnectionMode.DevTunnel && AutoStartSharing;

    internal static DashboardSettings RecoveryDefaults => new() { AutoStartSharing = false };

    public static string DataDirectory
    {
        get
        {
            var isolated = Environment.GetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR");
            if (string.IsNullOrWhiteSpace(isolated))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentSignaler");
            if (!Path.IsPathFullyQualified(isolated))
                throw new InvalidDataException("AGENT_SIGNALER_DATA_DIR must be an absolute path.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(isolated));
        }
    }

    internal static string InstanceSuffix =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR")) ? "" :
        "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())));
    public static string FilePath => Path.Combine(DataDirectory, "dashboard-settings.json");
    public static string DatabasePath => Path.Combine(DataDirectory, "dashboard.db");

    public static DashboardSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        return FromJson(File.ReadAllText(FilePath));
    }

    internal static DashboardSettings FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = document.RootElement.Deserialize<DashboardSettings>()
            ?? throw new InvalidDataException("Empty settings.");
        if (settings.Port is < 1024 or > 65535 || settings.Theme is not ("System" or "Light" or "Dark") ||
            !Enum.IsDefined(settings.ConnectionMode) ||
            (settings.DevTunnelCliPath is { Length: > 0 } cliPath &&
                (!Path.IsPathFullyQualified(cliPath) || cliPath.Any(char.IsControl))))
            throw new InvalidDataException("Invalid settings.");
        try { _ = AzureCliInstallation.ResolvePath(settings.AzureCliPath); }
        catch (ArgumentException)
        {
            throw new InvalidDataException("The Azure CLI path in settings must be an absolute path to az.exe or az.cmd.");
        }
        try { DevBoxDiscoveryTarget.Validate(settings.DevBoxSubscriptionId, settings.DevCenterName); }
        catch (ArgumentException)
        {
            throw new InvalidDataException("The saved Dev Box discovery target must be a nonempty subscription GUID or a valid Dev Center name, not both.");
        }
        return settings;
    }

    internal DashboardSettings WithDiscoveryTarget(string? subscriptionId, string? devCenterName)
    {
        Guid? subscription = null;
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            if (!Guid.TryParse(subscriptionId.Trim(), out var parsed) || parsed == Guid.Empty)
                throw new ArgumentException("Enter a valid, nonempty subscription GUID, or leave the field blank.", nameof(subscriptionId));
            subscription = parsed;
        }
        var name = string.IsNullOrWhiteSpace(devCenterName) ? null : devCenterName.Trim();
        DevBoxDiscoveryTarget.Validate(subscription, name);
        return this with { DevBoxSubscriptionId = subscription, DevCenterName = name };
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var pending = FilePath + ".new";
        File.WriteAllText(pending, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, FilePath, overwrite: true);
    }
}

[SupportedOSPlatform("windows")]
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AgentSignaler";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value &&
            string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    private static string Command => $"\"{Environment.ProcessPath
        ?? throw new InvalidOperationException("The application path is unavailable.")}\" --background";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public static int RunInstallerHelper(string[] args)
    {
        if (args.Length != 3 || args[1] != "--transaction-id" ||
            string.IsNullOrWhiteSpace(args[2]) || args[2].Length > 256 || args[2].Any(char.IsControl) ||
            args[0] is not ("--uninstall-integration" or "--rollback-uninstall-integration" or "--commit-uninstall-integration"))
            return 2;

        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AgentSignaler", "installer-transactions");
            var transaction = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(args[2])));
            var snapshot = Path.Combine(folder, $"dashboard-{transaction}.startup");
            var expected = Command;
            var ownership = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(expected)));

            if (args[0] == "--commit-uninstall-integration")
            {
                File.Delete(snapshot);
                return 0;
            }

            if (args[0] == "--rollback-uninstall-integration")
            {
                if (!File.Exists(snapshot)) return 0;
                if (!string.Equals(File.ReadAllText(snapshot), ownership, StringComparison.Ordinal))
                    return 4;
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                if (!key.GetValueNames().Contains(ValueName, StringComparer.OrdinalIgnoreCase))
                    key.SetValue(ValueName, expected, RegistryValueKind.String);
                File.Delete(snapshot);
                return 0;
            }

            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                if (key is null || !IsExactOwnedValue(key, expected)) return 0;
                Directory.CreateDirectory(folder);
                if (File.Exists(snapshot))
                {
                    if (!string.Equals(File.ReadAllText(snapshot), ownership, StringComparison.Ordinal))
                        return 4;
                }
                else
                {
                    // Persist ownership before mutation so MSI rollback can restore it after a failure.
                    using var marker = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    marker.Write(System.Text.Encoding.UTF8.GetBytes(ownership));
                    marker.Flush(flushToDisk: true);
                }
                if (IsExactOwnedValue(key, expected)) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Installer helpers are noninteractive; MSI reports failure using only this result code.
            return 4;
        }
    }

    private static bool IsExactOwnedValue(RegistryKey key, string expected) =>
        key.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string actual &&
        string.Equals(actual, expected, StringComparison.Ordinal) &&
        key.GetValueKind(ValueName) == RegistryValueKind.String;
}
