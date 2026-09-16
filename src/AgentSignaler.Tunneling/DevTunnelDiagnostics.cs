using System.ComponentModel;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AgentSignaler.Tunneling;

/// <summary>Dashboard prerequisite testing and validation of newly entered CLI paths.</summary>
public static partial class DevTunnelDiagnostics
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Checks the supplied, possibly unsaved, CLI path independently of any controller.
    /// Only --version and user show --json run, with Microsoft signature verification and
    /// bounded, independently owned jobs. Cancellation propagates to the caller.
    /// </summary>
    public static Task<PrerequisiteDiagnosticResult> CheckAsync(string? configuredPath, CancellationToken cancellationToken = default) =>
        CheckAsync(configuredPath, new WindowsTunnelProcessRunner(), ValidateCliPath, cancellationToken);

    public static async Task<string> TestAsync(string? configuredPath, CancellationToken cancellationToken = default) =>
        (await CheckAsync(configuredPath, cancellationToken).ConfigureAwait(false)).Details;

    /// <summary>
    /// Normalizes an absolute local .exe path. Blank selects a known installation path, never PATH.
    /// Does not require the file to exist or validate its signature. Intended for Save, not legacy settings loading.
    /// </summary>
    /// <exception cref="ArgumentException">The supplied path is not a syntactically valid absolute local .exe path.</exception>
    public static string ResolvePath(string? configuredPath)
    {
        var path = CliTunnelController.DiscoverCliPath(configuredPath?.Trim(), File.Exists);
        try
        {
            ValidateLocalExecutable(path);
            path = Path.GetFullPath(path);
            ValidateLocalExecutable(path);
            return path;
        }
        catch (Exception exception) when (exception is TunnelException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(
                "Choose an absolute local CLI .exe path, not a relative path, command, URL, or network share.",
                nameof(configuredPath));
        }
    }

    internal static string ValidateCliPath(string? configuredPath)
    {
        var path = CliTunnelController.DiscoverCliPath(configuredPath?.Trim(), File.Exists);
        try
        {
            path = ResolvePath(path);
            var file = new FileInfo(path);
            if ((File.GetAttributes(file.FullName) & FileAttributes.Directory) != 0)
                throw new TunnelException("Choose a CLI executable file, not a directory.", TunnelState.Unsupported)
                { FailureKind = CliFailureKind.InvalidPath };
            path = file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
            ValidateLocalExecutable(path);
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new TunnelException("Choose a CLI executable file, not a directory.", TunnelState.Unsupported)
                { FailureKind = CliFailureKind.InvalidPath };
            return path;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new TunnelException("Choose an absolute local CLI .exe path.", TunnelState.Unsupported)
            { FailureKind = CliFailureKind.InvalidPath };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new TunnelException("The CLI executable or link target is missing. Repair the installation or select an existing local executable.", TunnelState.Unsupported)
            { FailureKind = CliFailureKind.MissingPath };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new TunnelException("The CLI path cannot be read or resolved. Select an accessible absolute local .exe path.", TunnelState.Unsupported)
            { FailureKind = CliFailureKind.InaccessiblePath, ErrorCode = exception.HResult };
        }
    }

    private static void ValidateLocalExecutable(string path)
    {
        if (path.Any(char.IsControl) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 ||
            path.Contains('*') || path.Contains('?') ||
            !Path.IsPathFullyQualified(path) || path.StartsWith('\\') || path.StartsWith('/') ||
            path.AsSpan(2).Contains(':') ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new TunnelException("Choose an absolute local CLI .exe path, not a relative path, command, URL, or network share.", TunnelState.Unsupported)
            { FailureKind = CliFailureKind.InvalidPath };
    }

    internal static async Task<string> TestAsync(string? configuredPath, ITunnelProcessRunner runner,
        Func<string?, string> validatePath, CancellationToken cancellationToken) =>
        (await CheckAsync(configuredPath, runner, validatePath, cancellationToken).ConfigureAwait(false)).Details;

    internal static async Task<PrerequisiteDiagnosticResult> CheckAsync(string? configuredPath, ITunnelProcessRunner runner,
        Func<string?, string> validatePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = CliTunnelController.DiscoverCliPath(configuredPath?.Trim(), File.Exists);
        var pathReady = false;
        var versionReady = false;
        CliCommandResult? version = null;
        string Header() => $"Tested CLI path: {SafePath(path)}{Environment.NewLine}" +
            $"Required: Microsoft-signed Dev Tunnels CLI {TunnelValidation.SupportedCliVersion}.{Environment.NewLine}" +
            VersionDetail(version) + Environment.NewLine;
        try
        {
            path = validatePath(path);
            pathReady = true;
            version = await CliPrerequisiteChecks.ReadVersionAsync(runner, path, CommandTimeout, cancellationToken).ConfigureAwait(false);
            CliPrerequisiteChecks.VerifyVersion(version);
            versionReady = true;
            await CliPrerequisiteChecks.CheckAccountAsync(runner, path, CommandTimeout, cancellationToken).ConfigureAwait(false);
            return new(true, Header() + "Signature/version: verified and supported. Microsoft account: ready. " +
                "No sharing or saved identity was changed; saved tunnel ownership is checked separately when sharing starts.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is TunnelException or IOException or UnauthorizedAccessException or
            SecurityException or Win32Exception or CryptographicException or TimeoutException or ArgumentException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!pathReady)
                return new(false, Header() + FailureDetail(exception, "Path validation") + " Signature/version and account: not checked.");
            if (!versionReady)
            {
                var detail = version is { ExitCode: 0 }
                    ? "Version: unsupported, conflicting, or unrecognized metadata. Select or explicitly install the required Microsoft-signed CLI."
                    : FailureDetail(exception, "--version");
                return new(false, Header() + (version is null ? "Signature/version: not verified. " : "Microsoft signature: verified. ") +
                    detail + " Account: not checked.");
            }
            var accountMessage = exception is TunnelException { State: TunnelState.AccountRequired }
                ? "Microsoft account: signed out. Sign in explicitly with the selected Dev Tunnels CLI, then retry."
                : exception is TunnelException { State: TunnelState.Unsupported, FailureKind: CliFailureKind.None }
                    ? "Microsoft account: unsupported response schema. Use the required CLI version and a Microsoft account with a stable tenant/object identity."
                    : "Microsoft account: not ready. " + FailureDetail(exception, "user show --json");
            return new(false, Header() + "Signature/version: verified and supported. " + accountMessage);
        }
    }

    private static string FailureDetail(Exception exception, string stage) => exception switch
    {
        TunnelException { FailureKind: CliFailureKind.InvalidPath } or ArgumentException or NotSupportedException =>
            "Path: invalid. Choose an absolute local .exe path without arguments, wildcards, or network shares.",
        TunnelException { FailureKind: CliFailureKind.MissingPath } =>
            "Path: executable or link target not found. Select an existing absolute local .exe path, or install the required CLI explicitly.",
        TunnelException { FailureKind: CliFailureKind.InaccessiblePath } failure =>
            $"Path: inaccessible or link resolution failed{Code(failure.ErrorCode)}. Check file permissions and repair the installation.",
        TunnelException { FailureKind: CliFailureKind.UntrustedSignature } failure =>
            $"Authenticode signature: invalid or untrusted{Code(failure.ErrorCode)}. No CLI code ran for this command. Reinstall the required Microsoft-signed CLI; check Windows certificate trust.",
        TunnelException { FailureKind: CliFailureKind.WrongPublisher } =>
            "Signature publisher: not Microsoft Corporation. No CLI code ran for this command. Select the required Microsoft-signed CLI.",
        TunnelException { FailureKind: CliFailureKind.CommandTimeout } or TimeoutException =>
            $"{stage}: timed out (30-second command limit). The diagnostic job was stopped. Check file access/connectivity and retry.",
        TunnelException { FailureKind: CliFailureKind.OutputLimit } =>
            $"{stage}: bounded output limit exceeded; the diagnostic job was stopped. Select the qualified CLI and retry.",
        TunnelException { FailureKind: CliFailureKind.CommandFailed } failure =>
            $"{stage}: command failed (exit code {failure.ErrorCode}). Check explicit CLI sign-in, connectivity, and the required CLI installation.",
        TunnelException { FailureKind: CliFailureKind.UnsupportedPlatform } =>
            "Platform: Windows is required for signature verification and contained CLI execution.",
        Win32Exception failure =>
            $"{stage}: Windows process setup or execution failed (native error {failure.NativeErrorCode}, 0x{failure.NativeErrorCode:X8}). Check executable access, Windows job restrictions, and installation compatibility.",
        UnauthorizedAccessException or SecurityException =>
            $"{stage}: access denied. Check executable and account-cache permissions, then retry.",
        CryptographicException failure =>
            $"Signature/certificate validation failed (HRESULT 0x{failure.HResult:X8}). Repair Windows certificate trust or reinstall the required Microsoft-signed CLI.",
        IOException failure =>
            $"{stage}: file or process I/O failed (HRESULT 0x{failure.HResult:X8}). Check file access and retry.",
        _ => $"{stage}: validation failed. Select the required Microsoft-signed CLI and check explicit Microsoft sign-in/connectivity. Raw output was not displayed."
    };

    private static string Code(int? code) => code is { } value ? $" (code 0x{value:X8})" : "";

    private static string VersionDetail(CliCommandResult? result)
    {
        if (result is null) return "Checked version: not available (command did not complete).";
        var versions = result.StandardOutput.Split('\n').Concat(result.StandardError.Split('\n'))
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.StartsWith("Tunnel CLI version: ", StringComparison.Ordinal) ? line[20..] :
                line.StartsWith("CLI version: ", StringComparison.Ordinal) ? line[13..] : "")
            .Where(value => SafeVersion().IsMatch(value)).Distinct(StringComparer.Ordinal).Take(3).ToArray();
        return versions.Length == 0 ? "Checked version: unrecognized metadata (raw output hidden)." :
            $"Checked version metadata: {string.Join(", ", versions)}.";
    }

    private static string SafePath(string path) =>
        new(path.Take(1024).Select(character => char.IsControl(character) ? '?' : character).ToArray());

    [GeneratedRegex(@"\A[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}(?:[-+][0-9A-Za-z.-]{1,40})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersion();
}
