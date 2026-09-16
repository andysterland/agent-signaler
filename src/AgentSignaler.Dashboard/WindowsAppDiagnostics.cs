using System.Security;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

internal static class WindowsAppDiagnostics
{
    public static Task<string> TestAsync(CancellationToken cancellationToken) =>
        GetDetailsAsync(CheckAsync(cancellationToken));

    internal static Task<string> TestAsync(CancellationToken cancellationToken, Func<bool> probe) =>
        GetDetailsAsync(CheckAsync(cancellationToken, probe));

    private static async Task<string> GetDetailsAsync(Task<PrerequisiteDiagnosticResult> check) =>
        (await check.ConfigureAwait(false)).Details;

    public static Task<PrerequisiteDiagnosticResult> CheckAsync(CancellationToken cancellationToken) =>
        CheckAsync(cancellationToken, new WindowsAppPlatform().CheckProtocolAvailability);

    internal static Task<PrerequisiteDiagnosticResult> CheckAsync(CancellationToken cancellationToken, Func<bool> probe)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string guidance = "Windows App version: not verified by this protocol check. " +
            "Install or update Windows App from Microsoft Store separately and verify version 2.0.804.0 or later in the app. No connection was launched.";
        try
        {
            var available = probe();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PrerequisiteDiagnosticResult(available, (available
                ? "ms-cloudpc protocol: available for the current Windows user. Registration alone does not prove Windows App is installed or working.\n"
                : "ms-cloudpc protocol: missing for the current Windows user. Install or repair Windows App and check again.\n") + guidance));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult(new PrerequisiteDiagnosticResult(false,
                "ms-cloudpc protocol check failed: unable to read the current user's association. Check Windows profile permissions and retry.\n" + guidance));
        }
    }
}
