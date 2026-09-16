using System.ComponentModel;
using System.Diagnostics;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class AzureCliDiagnosticsTests
{
    private const string Path = @"C:\Selected CLI\wbin\az.cmd";
    private const string VersionJson = "{\"azure-cli\":\"2.90.0\"}";
    private const string AccountJson = "{\"id\":\"22222222-2222-2222-2222-222222222222\",\"tenantId\":\"11111111-1111-1111-1111-111111111111\",\"state\":\"Enabled\",\"user\":{\"name\":\"private@example.com\",\"type\":\"user\"}}";

    [Fact]
    public async Task TestReportsPathVersionAndSignInWithoutExposingAccountOutput()
    {
        var process = new FakeProcess(new(0, VersionJson, ""), new(0, AccountJson, ""));
        var result = await AzureCliDiagnostics.CheckAsync(Path, default, path =>
        {
            Assert.Equal(Path, path);
            return process;
        });
        Assert.True(result.Passed);
        var report = result.Details;
        Assert.Equal(report, await AzureCliDiagnostics.TestAsync(Path, default,
            _ => new FakeProcess(new(0, VersionJson, ""), new(0, AccountJson, ""))));
        Assert.Contains(Path, report);
        Assert.Contains("2.90.0", report);
        Assert.Contains("signed in", report);
        Assert.Contains("saved account, not live token validity or Dev Box permissions", report);
        Assert.Contains("trusted code", report);
        Assert.DoesNotContain("private@", report);
        Assert.Equal(["version", "account"], process.Commands);
    }

    [Theory]
    [InlineData("", "Malformed version")]
    [InlineData("[]", "Malformed version")]
    [InlineData("{\"azure-cli\":17}", "Malformed version")]
    [InlineData("{\"azure-cli\":\"secret-token\"}", "Malformed version")]
    [InlineData("{\"azure-cli\":\"\\uD800\"}", "Malformed version")]
    [InlineData("{\"azure-cli\":\"2.89.0\"}", "2.89.0.0 rejected")]
    public async Task VersionErrorsAreSafeAndPreventAccountCheck(string output, string expected)
    {
        var process = new FakeProcess(new AzureCliResult(0, output, "secret-token"));
        var result = await AzureCliDiagnostics.CheckAsync(Path, default, _ => process);
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains("Version check failed", report);
        Assert.Contains(expected, report);
        Assert.DoesNotContain("secret-token", report);
        Assert.Single(process.Commands);
    }

    [Theory]
    [InlineData(29, true, "Version")]
    [InlineData(31, false, "Account/sign-in")]
    public async Task NonzeroExitReportsStageAndExitCodeNotOutput(int code, bool version, string stage)
    {
        var failure = new AzureCliResult(code, "secret-token ms-cloudpc://connection", "secret-token");
        var process = version ? new FakeProcess(failure) : new FakeProcess(new(0, VersionJson, ""), failure);
        var result = await AzureCliDiagnostics.CheckAsync(Path, default, _ => process);
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains(stage, report);
        Assert.Contains($"exit code {code}", report);
        Assert.DoesNotContain("secret-token", report);
        Assert.DoesNotContain("ms-cloudpc:", report);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("secret-token")]
    [InlineData("{\"tenantId\":\"invalid\",\"user\":{\"name\":\"secret-token\"}}")]
    public async Task MalformedAccountResponseIsClassified(string json)
    {
        var result = await AzureCliDiagnostics.CheckAsync(Path, default,
            _ => new FakeProcess(new(0, VersionJson, ""), new(0, json, "secret-token")));
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains("Malformed account response", report);
        Assert.Contains("Installed Azure CLI version", report);
        Assert.DoesNotContain("secret-token", report);
    }

    [Theory]
    [InlineData("user", "servicePrincipal", "not an interactive user")]
    [InlineData("Enabled", "Disabled", "subscription is not enabled")]
    [InlineData("private@example.com", "\\uD800", "Malformed account response")]
    public async Task UnusableAccountDoesNotReportSuccess(string original, string replacement, string expected)
    {
        var json = original == "user"
            ? AccountJson.Replace("\"type\":\"user\"", $"\"type\":\"{replacement}\"")
            : AccountJson.Replace(original, replacement);
        var result = await AzureCliDiagnostics.CheckAsync(Path, default,
            _ => new FakeProcess(new(0, VersionJson, ""), new(0, json, "")));
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains(expected, report);
        Assert.DoesNotContain("Account/sign-in: signed in", report);
    }

    [Theory]
    [InlineData("Please run az login. secret-token", "Sign-in is missing")]
    [InlineData("AADSTS50076 secret-token", "requires interaction")]
    [InlineData("CERTIFICATE_VERIFY_FAILED secret-token", "network, proxy, DNS, or TLS")]
    public async Task AccountFailureProvidesSafeActionableReason(string stderr, string expected)
    {
        var report = await AzureCliDiagnostics.TestAsync(Path, default,
            _ => new FakeProcess(new(0, VersionJson, ""), new(1, "", stderr)));
        Assert.Contains(expected, report);
        Assert.DoesNotContain("secret-token", report);
    }

    [Fact]
    public async Task MissingInstallationIsActionable()
    {
        var report = await AzureCliDiagnostics.TestAsync(@"C:\nonexistent-cli-diagnostics\az.exe", default);
        Assert.Contains("Installation file missing", report);
    }

    [Fact]
    public async Task NativeStartupErrorIncludesCodeButNotNativeMessage()
    {
        var report = await AzureCliDiagnostics.TestAsync(Path, default, path =>
            new AzureCliProcess(new FailingRunner(), new BypassInstallation(), executablePath: path));
        Assert.Contains("native error 5", report);
        Assert.DoesNotContain("secret-token", report);
    }

    [Theory]
    [InlineData((int)WindowsAppFailure.TimedOut, "Process timed out.")]
    [InlineData((int)WindowsAppFailure.CliUnsupported, "Installation file missing.")]
    [InlineData((int)WindowsAppFailure.CliUnavailable, "Process startup/operation failed (native error 193).")]
    public async Task ClassifiedFailureDetailsAreReported(int failure, string detail)
    {
        var report = await AzureCliDiagnostics.TestAsync(Path, default,
            _ => new FailingProcess(new WindowsAppConnectionException((WindowsAppFailure)failure, detail)));
        Assert.Contains(detail, report);
    }

    [Fact]
    public async Task InvalidPathDoesNotCreateProcessAndCancellationPropagates()
    {
        var report = await AzureCliDiagnostics.TestAsync("az.cmd", default, _ => throw new InvalidOperationException());
        Assert.Contains("Path validation failed", report);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureCliDiagnostics.TestAsync(Path, new CancellationToken(true), _ => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task TypedFailureDoesNotInferSuccessFromPathOrDiagnosticText()
    {
        const string path = @"C:\ready available signed in\az.exe";
        const string detail = "Installation is ready; account check unavailable.";
        var error = new WindowsAppConnectionException(WindowsAppFailure.CliUnavailable, detail);
        var result = await AzureCliDiagnostics.CheckAsync(path, default, _ => new FailingProcess(error));
        Assert.False(result.Passed);
        Assert.Contains(path, result.Details);
        Assert.Contains(detail, result.Details);
        Assert.Equal(result.Details, await AzureCliDiagnostics.TestAsync(path, default, _ => new FailingProcess(error)));
    }

    [Fact]
    public async Task TypedInvalidPathFailsAndCancellationPropagates()
    {
        var result = await AzureCliDiagnostics.CheckAsync("az.cmd", default, _ => throw new InvalidOperationException());
        Assert.False(result.Passed);
        Assert.Contains("Path validation failed", result.Details);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureCliDiagnostics.CheckAsync(Path, new CancellationToken(true), _ => throw new InvalidOperationException()));
    }

    private sealed class FakeProcess(params AzureCliResult[] results) : IAzureCliProcess
    {
        public List<string> Commands { get; } = [];
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(command.Timeout, timeout);
            Commands.Add(command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[0]);
            return Task.FromResult(results[Commands.Count - 1]);
        }
    }

    private sealed class FailingProcess(Exception error) : IAzureCliProcess
    {
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromException<AzureCliResult>(error);
    }

    private sealed class BypassInstallation : IAzureCliInstallation, IDisposable
    {
        public IDisposable Validate() => this;
        public void Dispose() { }
    }

    private sealed class FailingRunner : IAzureCliProcessRunner
    {
        public IAzureCliChildProcess Start(ProcessStartInfo startInfo) =>
            throw new Win32Exception(5, "secret-token ms-cloudpc://connection");
    }
}
