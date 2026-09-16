// Keep Debug.WriteLine calls in Release builds as well as Debug builds.
#define DEBUG

using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard;

internal sealed class AzureCliCommand
{
    private readonly string[] arguments;
    private AzureCliCommand(string[] arguments, TimeSpan timeout)
    {
        this.arguments = arguments;
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
    public static AzureCliCommand Login(Guid? tenantId = null)
    {
        if (tenantId == Guid.Empty) throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping);
        return new(tenantId is { } tenant ? ["login", "--tenant", tenant.ToString("D")] : ["login"],
            TimeSpan.FromMinutes(10));
    }

    public static AzureCliCommand AccountShow() => new(["account", "show", "--output", "json"], TimeSpan.FromSeconds(15));
    public static AzureCliCommand AccountList() => new(["account", "list", "--all", "--output", "json"], TimeSpan.FromSeconds(30));
    public static AzureCliCommand Version() => new(["version", "--output", "json"], TimeSpan.FromSeconds(30));
    public static AzureCliCommand ListExtensions() => new(["extension", "list", "--output", "json"], TimeSpan.FromSeconds(30));
    internal bool IsVersion => arguments[0] == "version";
    internal string DebugCommand => "az " + string.Join(" ", arguments.Select(argument => JsonSerializer.Serialize(argument)));

    internal const string DevCenterArmApiVersion = "2025-02-01";
    private static readonly Uri ArmEndpoint = new("https://management.azure.com/");

    internal static Uri DevCentersUri(Guid subscription)
    {
        if (subscription == Guid.Empty) throw new WindowsAppConnectionException(WindowsAppFailure.MalformedResponse);
        return new(ArmEndpoint, $"subscriptions/{subscription:D}/providers/Microsoft.DevCenter/devcenters?api-version={DevCenterArmApiVersion}");
    }

    public static AzureCliCommand ListDevCenters(Guid subscription) =>
        ListDevCentersPage(subscription, DevCentersUri(subscription));

    public static AzureCliCommand ListDevCentersPage(Guid subscription, Uri nextLink)
    {
        var expected = DevCentersUri(subscription);
        ValidateCatalogUri(ArmEndpoint, nextLink);
        var original = nextLink.OriginalString;
        var pathStart = original.IndexOf('/', original.IndexOf("://", StringComparison.Ordinal) + 3);
        var queryStart = original.IndexOf('?', pathStart < 0 ? 0 : pathStart);
        // Compare the original path too, so canonicalization cannot hide traversal or encoded separators.
        if (pathStart < 0 || queryStart < 0 ||
            !original[pathStart..queryStart].Equals(expected.AbsolutePath, StringComparison.OrdinalIgnoreCase) ||
            !nextLink.AbsolutePath.Equals(expected.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
        var versions = nextLink.Query[1..].Split('&')
            .Where(part => Uri.UnescapeDataString(part.Split('=')[0]).Equals("api-version", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions.Length != 1 || versions[0] != $"api-version={DevCenterArmApiVersion}")
            throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
        return new(["rest", "--method", "get", "--resource", "https://management.azure.com/", "--url", nextLink.AbsoluteUri],
            TimeSpan.FromSeconds(30));
    }

    public static AzureCliCommand ListDevBoxes(Uri endpoint)
    {
        ValidateCatalogEndpoint(endpoint);
        return ListDevBoxesPage(endpoint, new Uri(endpoint, "devboxes?api-version=2025-02-01"));
    }

    public static AzureCliCommand ListDevBoxesByName(string devCenterName)
    {
        WindowsAppConnection.ValidateResourceName(devCenterName, nameof(devCenterName));
        return new(["devcenter", "dev", "dev-box", "list", "--dev-center-name", devCenterName,
            "--user-id", "me", "--output", "json"], TimeSpan.FromSeconds(30));
    }

    public static AzureCliCommand ListDevBoxesPage(Uri endpoint, Uri nextLink)
    {
        ValidateCatalogEndpoint(endpoint);
        ValidateCatalogUri(endpoint, nextLink);
        return new(["rest", "--method", "get", "--resource", "https://devcenter.azure.com", "--url", nextLink.AbsoluteUri],
            TimeSpan.FromSeconds(30));
    }

    private static void ValidateCatalogEndpoint(Uri endpoint)
    {
        try { WindowsAppConnection.ValidateEndpoint(endpoint); }
        catch (ArgumentException) { throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping); }
    }

    internal static void ValidateCatalogUri(Uri endpoint, Uri uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.OriginalString.Length > 16384 ||
            uri.OriginalString.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
            throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
        try
        {
            if (!uri.IdnHost.Equals(endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) ||
                !uri.IsWellFormedOriginalString())
                throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
            var decoded = Uri.UnescapeDataString(uri.OriginalString);
            if (decoded.Any(c => char.IsControl(c) || c == '\\'))
                throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri);
        }
        catch (UriFormatException) { throw new WindowsAppConnectionException(WindowsAppFailure.UnsafeUri); }
    }

    public static AzureCliCommand RemoteConnection(WindowsAppConnection mapping)
    {
        WindowsAppConnectionValidator.ValidateMapping(mapping);
        var endpoint = mapping.DevCenterEndpoint;
        var url = new Uri(endpoint,
            $"projects/{Uri.EscapeDataString(mapping.ProjectName)}/users/me/devboxes/{Uri.EscapeDataString(mapping.DevBoxName)}/remoteConnection?api-version=2025-02-01");
        if (url.Scheme != Uri.UriSchemeHttps || url.IdnHost != endpoint.IdnHost ||
            url.Port != endpoint.Port || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping);
        return new(["rest", "--method", "get", "--resource", "https://devcenter.azure.com", "--url", url.AbsoluteUri],
            TimeSpan.FromSeconds(30));
    }

    internal ProcessStartInfo CreateStartInfo(string? executablePath = null)
    {
        var path = AzureCliInstallation.ResolvePath(executablePath);
        var isCmd = Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isCmd ? AzureCliInstallation.RuntimePath(path) : path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (arguments[0] is "devcenter" or "extension")
            start.Environment["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no";
        if (isCmd)
        {
            // Reproduce the official MSI launcher without passing any input through cmd.exe.
            start.ArgumentList.Add("-IBm");
            start.ArgumentList.Add("azure.cli");
            start.Environment["AZ_INSTALLER"] = "MSI";
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    public override string ToString() => "Azure CLI command (arguments withheld)";
}

internal sealed record AzureCliResult(int ExitCode, string StandardOutput, string StandardError)
{
    public override string ToString() => $"Azure CLI result: exit code {ExitCode} (output withheld)";
}

internal interface IAzureCliProcess
{
    Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken);
}

internal interface IAzureCliProcessRunner
{
    IAzureCliChildProcess Start(ProcessStartInfo startInfo);
}

internal interface IAzureCliChildProcess : IDisposable
{
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken);
    void KillTree();
}

internal interface IAzureCliInstallation
{
    IDisposable Validate();
    bool RequiresVersionCheck => true;
}

internal sealed class AzureCliProcess : IAzureCliProcess
{
    public const int MaximumOutputBytes = 1024 * 1024;
    private readonly IAzureCliProcessRunner runner;
    private readonly IAzureCliInstallation installation;
    private readonly TimeProvider timeProvider;
    private readonly string executablePath;
    private readonly Action<string>? debugOutput;
    private static long nextInvocationId;
    private bool versionChecked;

    public AzureCliProcess(IAzureCliProcessRunner? runner = null, IAzureCliInstallation? installation = null,
        TimeProvider? timeProvider = null, string? executablePath = null, Action<string>? debugOutput = null)
    {
        this.executablePath = AzureCliInstallation.ResolvePath(executablePath);
        this.runner = runner ?? new AzureCliProcessRunner();
        this.installation = installation ?? new AzureCliInstallation(this.executablePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.debugOutput = debugOutput;
    }

    public async Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (timeout <= TimeSpan.Zero || timeout > command.Timeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Use a positive timeout no greater than the command timeout.");
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            if (!command.IsVersion && !versionChecked && installation.RequiresVersionCheck)
            {
                var versionCommand = AzureCliCommand.Version();
                var result = await RunCoreAsync(versionCommand, timeout < versionCommand.Timeout ? timeout : versionCommand.Timeout,
                    linked.Token).ConfigureAwait(false);
                AzureCliDiagnostics.ReadVersion(result);
                versionChecked = true;
            }
            var response = await RunCoreAsync(command, timeout, linked.Token).ConfigureAwait(false);
            if (command.IsVersion)
            {
                AzureCliDiagnostics.ReadVersion(response);
                versionChecked = true;
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The connection operation was cancelled.", cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.TimedOut,
                "Process timed out; the owned process tree was stopped.");
        }
    }

    private async Task<AzureCliResult> RunCoreAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (timeout <= TimeSpan.Zero || timeout > command.Timeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Use a positive timeout no greater than the command timeout.");
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(timeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        IAzureCliChildProcess? child = null;
        IDisposable? verifiedFile = null;
        var tasks = new List<Task>();
        var completedSuccessfully = false;
        var invocationId = Interlocked.Increment(ref nextInvocationId);
        var started = Stopwatch.GetTimestamp();
        WriteDebug(invocationId, $"Invoking {command.DebugCommand}; executable={JsonSerializer.Serialize(executablePath)}; timeout={timeout.TotalSeconds:F1}s");
        try
        {
            verifiedFile = installation.Validate();
            linked.Token.ThrowIfCancellationRequested();
            child = runner.Start(command.CreateStartInfo(executablePath));
            var stdout = ReadBoundedAsync(child.StandardOutput, linked.Token);
            var stderr = ReadBoundedAsync(child.StandardError, linked.Token);
            tasks.Add(stdout);
            tasks.Add(stderr);
            tasks.Add(child.WaitForExitAsync(linked.Token));
            var pending = new List<Task>(tasks);
            // Observe each completion immediately: WhenAll would hang on a full pipe after a reader fails.
            while (pending.Count != 0)
            {
                var completed = await Task.WhenAny(pending).WaitAsync(linked.Token).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                pending.Remove(completed);
            }
            linked.Token.ThrowIfCancellationRequested();
            var result = new AzureCliResult(child.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            WriteDebug(invocationId, $"Response: exitCode={result.ExitCode}; elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms; stdoutChars={result.StandardOutput.Length}; stderrChars={result.StandardError.Length}");
            WriteDebugStream(invocationId, "stdout", result.StandardOutput);
            WriteDebugStream(invocationId, "stderr", result.StandardError);
            completedSuccessfully = true;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The connection operation was cancelled.", cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.TimedOut, "Process timed out; the owned process tree was stopped.");
        }
        catch (WindowsAppConnectionException error)
        {
            WriteDebug(invocationId, $"Failed: {error.Failure}");
            throw;
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or SecurityException or DecoderFallbackException)
        {
            WriteDebug(invocationId, $"Process failure: {error.GetType().Name}");
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable, error switch
            {
                Win32Exception native => $"Process startup/operation failed (native error {native.NativeErrorCode}). Check the executable and Windows access policy.",
                UnauthorizedAccessException or SecurityException => "Access denied while starting or reading Azure CLI.",
                DecoderFallbackException => "Process response was not valid UTF-8.",
                _ => "Process I/O failed; check installation files and access permissions."
            });
        }
        finally
        {
            if (!completedSuccessfully)
                WriteDebug(invocationId, $"No complete response: elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms; cancellationRequested={cancellationToken.IsCancellationRequested}; deadlineExpired={deadline.IsCancellationRequested}");
            try
            {
                if (!completedSuccessfully)
                {
                    linked.Cancel();
                    try { child?.KillTree(); }
                    catch (Win32Exception) { /* A process exit/access race must not replace the original failure. */ }
                    catch (AggregateException error) when (error.InnerExceptions.All(inner => inner is Win32Exception))
                    { /* Kill(entireProcessTree) aggregates native failures for child processes. */ }
                }
            }
            finally
            {
                try { DisposeOwned(child, completedSuccessfully); }
                finally
                {
                    try { DisposeOwned(verifiedFile, completedSuccessfully); }
                    finally
                    {
                        foreach (var task in tasks)
                            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
            }
        }
    }

    private bool DebugOutputEnabled => debugOutput is not null || Debugger.IsAttached;

    private void WriteDebug(long invocationId, string message)
    {
        if (!DebugOutputEnabled) return;
        var line = $"[AzureCLI #{invocationId}] {message}";
        if (debugOutput is not null) debugOutput(line);
        else Debug.WriteLine(line);
    }

    private void WriteDebugStream(long invocationId, string stream, string output)
    {
        if (!DebugOutputEnabled) return;
        // Bound individual debugger messages and escape control characters without losing response data.
        const int chunkSize = 2048;
        if (output.Length == 0) WriteDebug(invocationId, $"{stream}: \"\"");
        for (var offset = 0; offset < output.Length;)
        {
            var length = Math.Min(chunkSize, output.Length - offset);
            if (offset + length < output.Length && char.IsHighSurrogate(output[offset + length - 1]) &&
                char.IsLowSurrogate(output[offset + length])) length--;
            WriteDebug(invocationId, $"{stream}[{offset}..{offset + length}]: {JsonSerializer.Serialize(output.Substring(offset, length))}");
            offset += length;
        }
    }

    private static void DisposeOwned(IDisposable? resource, bool completedSuccessfully)
    {
        try { resource?.Dispose(); }
        catch (IOException) when (!completedSuccessfully)
        { /* Preserve the original failure while still releasing all other owned resources. */ }
        catch (IOException) { throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable); }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > MaximumOutputBytes)
                throw new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable, "Process output exceeded the 1 MiB per-stream limit.");
            output.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, (int)output.Length);
    }
}

internal sealed class AzureCliProcessRunner : IAzureCliProcessRunner
{
    public IAzureCliChildProcess Start(ProcessStartInfo startInfo)
    {
        var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            if (!process.Start()) throw new Win32Exception();
            started = true;
            return new Child(process);
        }
        finally
        {
            if (!started) process.Dispose();
        }
    }

    private sealed class Child(Process process) : IAzureCliChildProcess
    {
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Stream StandardError => process.StandardError.BaseStream;
        public int ExitCode => process.ExitCode;
        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);
        public void KillTree()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited)
            { /* The owned process may exit between observing a failure and killing it. */ }
        }
        public void Dispose() => process.Dispose();
    }
}

internal sealed class AzureCliInstallation(string? executablePath = null) : IAzureCliInstallation
{
    private readonly string path = ResolvePath(executablePath);
    private static string DefaultExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.exe");
    public static string ExecutablePath => ResolvePath(null);
    internal static readonly Version MinimumVersion = new(2, 90, 0, 0);

    public static string ResolvePath(string? configuredPath)
    {
        if (configuredPath is not null && configuredPath.Any(char.IsControl))
            throw new ArgumentException("The Azure CLI path must not contain control characters.", nameof(configuredPath));
        if (string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(DefaultExePath) ? DefaultExePath : Path.ChangeExtension(DefaultExePath, ".cmd");
        var candidate = configuredPath.Trim();
        if (!Path.IsPathFullyQualified(candidate) || candidate.StartsWith(@"\\", StringComparison.Ordinal) ||
            candidate.Any(c => char.IsControl(c) || c is '"' or '<' or '>' or '|' or '*' or '?') ||
            candidate.AsSpan(2).Contains(':'))
            throw new ArgumentException("Use an absolute local path ending in az.exe or az.cmd.", nameof(configuredPath));
        var fullPath = Path.GetFullPath(candidate);
        var name = Path.GetFileName(fullPath);
        if (!name.Equals("az.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("az.cmd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select az.exe or the official az.cmd launcher.", nameof(configuredPath));
        return fullPath;
    }

    internal static string RuntimePath(string launcherPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(launcherPath)!, "..", "python.exe"));

    internal static void ValidateLauncher(string path, string content)
    {
        var lines = content.Split('\n').Select(line => line.Trim())
            .Where(line => line.Length != 0 && !line.StartsWith("::", StringComparison.Ordinal));
        const string expected = "@IF EXIST \"%~dp0\\..\\python.exe\" (\nSET AZ_INSTALLER=MSI\n\"%~dp0\\..\\python.exe\" -IBm azure.cli %*\n) ELSE (\necho Failed to load python executable.\nexit /b 1\n)";
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "wbin", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(string.Join("\n", lines), expected, StringComparison.OrdinalIgnoreCase))
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnsupported,
                "Launcher type/layout rejected: az.cmd must be the unmodified official MSI wbin launcher with adjacent ..\\python.exe.");
    }

    public IDisposable Validate()
    {
        var files = new List<FileStream>();
        var validated = false;
        try
        {
            // Keep the selected installation files locked against replacement until the child has finished.
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(file);
            if (Path.GetExtension(path).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                if (file.Length > 8192)
                    throw new WindowsAppConnectionException(WindowsAppFailure.CliUnsupported, "Launcher type rejected: az.cmd is too large to be the official MSI launcher.");
                using var reader = new StreamReader(file, Encoding.UTF8, true, leaveOpen: true);
                ValidateLauncher(path, reader.ReadToEnd());
                var runtime = RuntimePath(path);
                var runtimeFile = new FileStream(runtime, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(runtimeFile);
            }
            validated = true;
            return new LockedFiles(files);
        }
        catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or SecurityException)
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.CliUnsupported, error switch
            {
                FileNotFoundException or DirectoryNotFoundException => "Installation file missing: check the selected launcher and, for az.cmd, adjacent ..\\python.exe.",
                UnauthorizedAccessException or SecurityException => "Installation access denied: check file permissions.",
                Win32Exception native => $"Installation verification failed (native error {native.NativeErrorCode}).",
                _ => "Installation file could not be read or locked; check permissions and whether another process is modifying it."
            });
        }
        finally
        {
            if (!validated)
            {
                try { new LockedFiles(files).Dispose(); }
                catch (IOException) { /* Preserve the validation failure. */ }
            }
        }
    }

    private sealed class LockedFiles(List<FileStream> files) : IDisposable
    {
        public void Dispose()
        {
            foreach (var file in files) file.Dispose();
        }
    }

}
