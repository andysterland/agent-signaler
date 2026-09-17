using System.Security;
using System.Security.Principal;
using Microsoft.Win32;

namespace AgentSignaler.RpcHost;

internal static class InstalledReceiverMetadata
{
    internal const string UpgradeCode = "{A8D1F2A9-762B-4B2C-A5A3-451463D743B2}";
    internal const string RegistryPath = @"Software\AgentSignaler\Installer\RpcHost";

    public static int? Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            using var identity = WindowsIdentity.GetCurrent();
            return key is null ? null : Validate(key.GetValue("UpgradeCode"), key.GetValue("InstallDirectory"),
                key.GetValue("ReceiverPort"), key.GetValue("UserSid"), Environment.ProcessPath, identity.User?.Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }

    internal static int? Validate(object? upgradeCode, object? installDirectory, object? receiverPort,
        object? userSid, string? executablePath, string? currentSid)
    {
        if (upgradeCode is not string upgrade || !string.Equals(upgrade, UpgradeCode, StringComparison.OrdinalIgnoreCase) ||
            installDirectory is not string directory || !Path.IsPathFullyQualified(directory) ||
            receiverPort is not int port || port is < 1024 or > 65535 ||
            userSid is not string sid || currentSid is null || sid != currentSid || executablePath is null)
            return null;
        try
        {
            return string.Equals(Path.GetFullPath(Path.Combine(directory, "AgentSignaler.RpcHost.exe")),
                Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase) ? port : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
