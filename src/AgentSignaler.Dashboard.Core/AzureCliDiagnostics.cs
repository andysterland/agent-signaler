using System.Text.Json;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

internal static class AzureCliDiagnostics
{
    internal const string TrustNotice = "Trust: the selected CLI is executed for diagnostics; executable architecture and signatures are not inspected. The installation's CLI modules and dependencies are trusted code; keep the entire installation protected from untrusted writes.";

    public static async Task<string> TestAsync(string? configuredPath, CancellationToken cancellationToken) =>
        (await CheckAsync(configuredPath, cancellationToken).ConfigureAwait(false)).Details;

    internal static async Task<string> TestAsync(string? configuredPath, CancellationToken cancellationToken,
        Func<string, IAzureCliProcess> processFactory) =>
        (await CheckAsync(configuredPath, cancellationToken, processFactory).ConfigureAwait(false)).Details;

    public static Task<PrerequisiteDiagnosticResult> CheckAsync(string? configuredPath, CancellationToken cancellationToken) =>
        CheckAsync(configuredPath, cancellationToken, path => new AzureCliProcess(executablePath: path));

    internal static Task<PrerequisiteDiagnosticResult> CheckAsync(string? configuredPath, CancellationToken cancellationToken,
        Func<string, IAzureCliProcess> processFactory) =>
        CheckCoreAsync(configuredPath, cancellationToken, processFactory, checkExtension: false);

    public static async Task<string> TestDevCenterExtensionAsync(string? configuredPath, CancellationToken cancellationToken) =>
        (await CheckDevCenterExtensionAsync(configuredPath, cancellationToken).ConfigureAwait(false)).Details;

    internal static async Task<string> TestDevCenterExtensionAsync(string? configuredPath, CancellationToken cancellationToken,
        Func<string, IAzureCliProcess> processFactory) =>
        (await CheckDevCenterExtensionAsync(configuredPath, cancellationToken, processFactory).ConfigureAwait(false)).Details;

    public static Task<PrerequisiteDiagnosticResult> CheckDevCenterExtensionAsync(string? configuredPath, CancellationToken cancellationToken) =>
        CheckDevCenterExtensionAsync(configuredPath, cancellationToken, path => new AzureCliProcess(executablePath: path));

    internal static Task<PrerequisiteDiagnosticResult> CheckDevCenterExtensionAsync(string? configuredPath, CancellationToken cancellationToken,
        Func<string, IAzureCliProcess> processFactory) =>
        CheckCoreAsync(configuredPath, cancellationToken, processFactory, checkExtension: true);

    private static async Task<PrerequisiteDiagnosticResult> CheckCoreAsync(string? configuredPath, CancellationToken cancellationToken,
        Func<string, IAzureCliProcess> processFactory, bool checkExtension)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path;
        try { path = AzureCliInstallation.ResolvePath(configuredPath); }
        catch (ArgumentException)
        {
            return new(false, "Path validation failed: enter an absolute local path ending in az.exe or the official az.cmd launcher.");
        }
        var summary = $"Tested path: {path}\n{TrustNotice}";
        if (checkExtension)
            summary += "\nThe devcenter extension is required only for discovery by Dev Center name, not automatic or subscription-ID discovery.";
        var stage = "Version";
        try
        {
            var process = processFactory(path);
            var command = AzureCliCommand.Version();
            var version = ReadVersion(await process.RunAsync(command, command.Timeout, cancellationToken).ConfigureAwait(false));
            cancellationToken.ThrowIfCancellationRequested();
            summary += $"\nInstalled Azure CLI version: {version}";
            if (checkExtension)
            {
                stage = "devcenter extension";
                command = AzureCliCommand.ListExtensions();
                var extensions = await process.RunAsync(command, command.Timeout, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var extension = ReadDevCenterExtension(extensions);
                return new(extension.Passed, $"{summary}\n{extension.Details}");
            }
            stage = "Account/sign-in";
            command = AzureCliCommand.AccountShow();
            var account = await process.RunAsync(command, command.Timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (account.ExitCode != 0)
                return new(false, $"{summary}\nAccount/sign-in check failed (exit code {account.ExitCode}). {AccountFailureDetail(account)}");
            ValidateAccount(account.StandardOutput);
            return new(true, $"{summary}\nAccount/sign-in: signed in; user, tenant, and enabled subscription response validated. " +
                "This checks the CLI's saved account, not live token validity or Dev Box permissions. No connection was launched.");
        }
        catch (WindowsAppConnectionException error)
        {
            return new(false, $"{summary}\n{stage} check failed: {error.DiagnosticDetail ?? error.Message}");
        }
    }

    private static PrerequisiteDiagnosticResult ReadDevCenterExtension(AzureCliResult result)
    {
        if (result.ExitCode != 0)
            return new(false, $"devcenter extension check failed (exit code {result.ExitCode}). Check the selected CLI installation and extension directory permissions, then retry.");
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException();
            string? installed = null;
            foreach (var extension in json.RootElement.EnumerateArray())
            {
                if (extension.ValueKind != JsonValueKind.Object ||
                    !extension.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                if (!string.Equals(name.GetString(), "devcenter", StringComparison.OrdinalIgnoreCase)) continue;
                if (installed is not null || !extension.TryGetProperty("version", out var value) ||
                    value.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                installed = value.GetString();
                if (installed is null || installed.Length > 64 ||
                    !System.Text.RegularExpressions.Regex.IsMatch(installed, @"\A[0-9]+(?:\.[0-9]+){1,3}(?:[-+._]?[A-Za-z][A-Za-z0-9.+_-]*)?\z"))
                    throw new JsonException();
            }
            return installed is null
                ? new(false, "devcenter extension: missing. Install it separately with az extension add --name devcenter in the selected Azure CLI, then check again. Nothing was installed.")
                : new(true, $"devcenter extension: available, version {installed}. Availability does not verify Dev Box permissions or service compatibility. No sign-in or discovery was started.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return new(false, "devcenter extension check failed: malformed extension list. Run az extension list separately with the selected CLI and repair its installation.");
        }
    }

    private static string AccountFailureDetail(AzureCliResult result)
    {
        var output = result.StandardError + "\n" + result.StandardOutput;
        if (new[] { "az login", "AADSTS50058", "AADSTS50076", "AADSTS700082", "AADSTS70043",
            "InteractiveAuthenticationRequired", "LoginRequired" }
            .Any(value => output.Contains(value, StringComparison.OrdinalIgnoreCase)))
            return "Sign-in is missing, expired, or requires interaction. Sign in separately with Azure CLI and retry; the test never starts login.";
        if (new[] { "CERTIFICATE_VERIFY_FAILED", "SSLError", "ProxyError", "ConnectionError", "NameResolutionError" }
            .Any(value => output.Contains(value, StringComparison.OrdinalIgnoreCase)))
            return "A network, proxy, DNS, or TLS error prevented the account check. Check connectivity and the CLI's proxy/certificate configuration.";
        return "No usable account was returned. Check the CLI installation, configuration, and sign-in, then retry.";
    }

    internal static Version ReadVersion(AzureCliResult result)
    {
        if (result.ExitCode != 0)
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable,
                $"Version command returned nonzero exit code {result.ExitCode}. Check the Azure CLI installation.");
        Version version;
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("azure-cli", out var value) || value.ValueKind != JsonValueKind.String ||
                !Version.TryParse(value.GetString(), out var parsed) || parsed.Build < 0)
                throw new JsonException();
            version = new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(parsed.Revision, 0));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.MalformedResponse,
                "Malformed version response: expected JSON with an azure-cli semantic version.");
        }
        if (version < AzureCliInstallation.MinimumVersion)
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnsupported,
                $"Installed Azure CLI version {version} rejected: version 2.90.0 or later is required.");
        return version;
    }

    private static void ValidateAccount(string output)
    {
        try
        {
            _ = AzureAccount.Parse(output);
        }
        catch (WindowsAppConnectionException error) when (error.Failure == WindowsAppFailure.MalformedResponse)
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.MalformedResponse,
                "Malformed account response: expected JSON with valid subscription id, tenantId, state, user.name, and user.type. Sign in separately and retry.");
        }
    }
}
