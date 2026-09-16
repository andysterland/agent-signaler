using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal enum WindowsAppFailure
{
    MissingMapping, InvalidMapping, ProtocolMissing, CliUnsupported, SignInRequired,
    CliUnavailable, TimedOut, AccountMismatch, DevBoxUnavailable, ApiUnavailable,
    MalformedResponse, UnsafeUri, ActivationFailed, PersistenceFailed, Busy, DiscoveryLimitExceeded
}

internal sealed class WindowsAppConnectionException(WindowsAppFailure failure, string? diagnosticDetail = null) : Exception(MessageFor(failure))
{
    public WindowsAppFailure Failure { get; } = failure;
    internal string? DiagnosticDetail { get; } = diagnosticDetail;
    public string Status => Failure switch
    {
        WindowsAppFailure.MissingMapping => "Not configured",
        WindowsAppFailure.SignInRequired => "Sign-in required",
        _ => "Unavailable"
    };

    private static string MessageFor(WindowsAppFailure failure) => failure switch
    {
        WindowsAppFailure.MissingMapping => "Configure a Dev Box connection to continue.",
        WindowsAppFailure.InvalidMapping => "The Dev Box mapping is invalid. Refresh the Dev Box list, select a Dev Box, and save its mapping.",
        WindowsAppFailure.ProtocolMissing => "Windows App is not installed or ms-cloudpc is not registered. Install Windows App 2.0.804.0 or later.",
        WindowsAppFailure.CliUnsupported => "Install Azure CLI 2.90.0 or later and check its path in Settings. Use Test for installation details.",
        WindowsAppFailure.SignInRequired => "Sign in with Azure CLI, then retry the connection.",
        WindowsAppFailure.CliUnavailable => "Azure CLI is unavailable. Check its installation and retry.",
        WindowsAppFailure.TimedOut => "Azure CLI timed out. Retry the operation.",
        WindowsAppFailure.AccountMismatch => "Azure CLI is using a different account or tenant. Sign in with the configured identity.",
        WindowsAppFailure.DevBoxUnavailable => "The Dev Box was not found, is unavailable, or access was denied.",
        WindowsAppFailure.ApiUnavailable => "The Dev Center API is unavailable. Retry the operation.",
        WindowsAppFailure.MalformedResponse => "The Dev Center API or Azure CLI returned malformed data.",
        WindowsAppFailure.DiscoveryLimitExceeded => "Discovery exceeded the supported resource limit.",
        WindowsAppFailure.UnsafeUri => "The service returned an unsafe or unsupported connection URI.",
        WindowsAppFailure.ActivationFailed => "Windows could not activate the Windows App connection. Check Windows App and retry.",
        WindowsAppFailure.PersistenceFailed => "The refreshed connection could not be saved. Check local storage and retry.",
        WindowsAppFailure.Busy => "A connection operation is already running for this machine.",
        _ => "The connection is unavailable."
    };
}

internal static class WindowsAppConnectionValidator
{
    public static void ValidateMapping(WindowsAppConnection? mapping)
    {
        if (mapping is null)
            throw new WindowsAppConnectionException(WindowsAppFailure.MissingMapping);
        try { mapping.Validate(); }
        catch (ArgumentException) { throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping); }
    }

    public static Uri Validate(string connectionUri, string azureAccountUpn)
    {
        try { return WindowsAppConnection.ValidateConnectionUri(connectionUri, azureAccountUpn); }
        catch (ArgumentException) { throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri); }
    }
}
