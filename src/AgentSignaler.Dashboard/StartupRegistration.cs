using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AgentSignaler.Dashboard;

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
