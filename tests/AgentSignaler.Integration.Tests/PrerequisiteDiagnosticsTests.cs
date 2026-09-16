using System.Security;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class PrerequisiteDiagnosticsTests
{
    private const string SelectedPath = @"C:\Unsaved CLI\az.exe";
    private const string VersionJson = """{"azure-cli":"2.90.0"}""";

    [Theory]
    [InlineData("""[{"name":"devcenter","version":"1.3.0"}]""", "available, version 1.3.0", true)]
    [InlineData("""[{"name":"devcenter","version":"1.3.0b1"}]""", "available, version 1.3.0b1", true)]
    [InlineData("""[{"name":"different","version":"1.0.0"}]""", "missing", false)]
    [InlineData("[]", "missing", false)]
    [InlineData("{}", "malformed", false)]
    [InlineData("""[{"name":"devcenter","version":"secret-token"}]""", "malformed", false)]
    [InlineData("""[{"name":"devcenter"}]""", "malformed", false)]
    [InlineData("""[{"name":"devcenter","version":"1.0.0"},{"name":"devcenter","version":"2.0.0"}]""", "malformed", false)]
    public async Task ExtensionCheckUsesUnsavedPathAndOnlyVersionAndLocalList(string output, string expected, bool passed)
    {
        var process = new ExtensionProcess(new(0, output, "secret-token"));
        var result = await AzureCliDiagnostics.CheckDevCenterExtensionAsync(SelectedPath, default, path =>
        {
            Assert.Equal(SelectedPath, path);
            return process;
        });
        Assert.Equal(passed, result.Passed);
        var report = result.Details;
        Assert.Equal(report, await AzureCliDiagnostics.TestDevCenterExtensionAsync(SelectedPath, default,
            _ => new ExtensionProcess(new(0, output, "secret-token"))));
        Assert.Contains(SelectedPath, report);
        Assert.Contains("Installed Azure CLI version: 2.90.0", report);
        Assert.Contains("required only for discovery by Dev Center name", report);
        Assert.Contains(expected, report);
        Assert.DoesNotContain("secret-token", report);
        Assert.Equal(["version --output json", "extension list --output json"], process.Commands);
    }

    [Fact]
    public async Task ExtensionErrorsAreActionableAndNeverExposeRawOutput()
    {
        var result = await AzureCliDiagnostics.CheckDevCenterExtensionAsync(SelectedPath, default,
            _ => new ExtensionProcess(new(23, "secret-token", "private-account")));
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains("exit code 23", report);
        Assert.Contains("permissions", report);
        Assert.DoesNotContain("secret-token", report);
        Assert.DoesNotContain("private-account", report);
    }

    [Fact]
    public async Task ExtensionCheckRejectsUnsupportedCliAndNeverListsExtensions()
    {
        var process = new ExtensionProcess(new(0, "[]", ""), """{"azure-cli":"2.89.0"}""");
        var result = await AzureCliDiagnostics.CheckDevCenterExtensionAsync(SelectedPath, default, _ => process);
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains("Version check failed", report);
        Assert.Single(process.Commands);
    }

    [Fact]
    public async Task ExtensionCheckHonorsMissingInstallationInvalidPathAndCancellation()
    {
        var missing = await AzureCliDiagnostics.TestDevCenterExtensionAsync(@"C:\nonexistent-prerequisite-cli\az.exe", default);
        Assert.Contains("Installation file missing", missing);
        var invalid = await AzureCliDiagnostics.TestDevCenterExtensionAsync("az.exe", default,
            _ => throw new InvalidOperationException("Must not execute"));
        Assert.Contains("Path validation failed", invalid);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureCliDiagnostics.TestDevCenterExtensionAsync(SelectedPath, new CancellationToken(true),
                _ => throw new InvalidOperationException("Must not execute")));
        using var cancellation = new CancellationTokenSource();
        var process = new ExtensionProcess(new(0, "[]", "")) { OnExtension = cancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureCliDiagnostics.TestDevCenterExtensionAsync(SelectedPath, cancellation.Token, _ => process));
    }

    [Theory]
    [InlineData(true, "available")]
    [InlineData(false, "missing")]
    public async Task WindowsAppReportsProtocolNotVersionAndNeverActivates(bool available, string expected)
    {
        var platform = new WindowsAppPlatform(() => available, _ => throw new InvalidOperationException("Must not launch"));
        var result = await WindowsAppDiagnostics.CheckAsync(default, platform.CheckProtocolAvailability);
        Assert.Equal(available, result.Passed);
        var report = result.Details;
        Assert.Equal(report, await WindowsAppDiagnostics.TestAsync(default, platform.CheckProtocolAvailability));
        Assert.Contains($"ms-cloudpc protocol: {expected}", report);
        Assert.Contains("version: not verified", report);
        Assert.Contains("2.0.804.0", report);
        Assert.Contains("Microsoft Store", report);
        Assert.Contains("No connection was launched", report);
    }

    [Fact]
    public async Task WindowsAppProbeFailureIsNotMisreportedAsMissing()
    {
        var result = await WindowsAppDiagnostics.CheckAsync(default, () => throw new SecurityException("private detail ready"));
        Assert.False(result.Passed);
        var report = result.Details;
        Assert.Contains("check failed", report);
        Assert.Contains("permissions", report);
        Assert.DoesNotContain("private detail", report);
        Assert.DoesNotContain("protocol: missing", report);
        Assert.Equal(report, await WindowsAppDiagnostics.TestAsync(default, () => throw new SecurityException("private detail ready")));
    }

    [Fact]
    public async Task WindowsAppCancellationPreventsProbeAndRejectsLateSuccess()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsAppDiagnostics.TestAsync(new CancellationToken(true), () => throw new InvalidOperationException()));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsAppDiagnostics.TestAsync(cancellation.Token, () => { cancellation.Cancel(); return true; }));
    }

    [Fact]
    public async Task TypedChecksPropagateCancellationRatherThanReturnFailure()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureCliDiagnostics.CheckDevCenterExtensionAsync(SelectedPath, new CancellationToken(true),
                _ => throw new InvalidOperationException("Must not execute")));
        using var extensionCancellation = new CancellationTokenSource();
        var process = new ExtensionProcess(new(0, """[{"name":"devcenter","version":"1.3.0"}]""", ""))
        { OnExtension = extensionCancellation.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureCliDiagnostics.CheckDevCenterExtensionAsync(SelectedPath, extensionCancellation.Token, _ => process));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsAppDiagnostics.CheckAsync(new CancellationToken(true), () => throw new InvalidOperationException("Must not probe")));
        using var protocolCancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsAppDiagnostics.CheckAsync(protocolCancellation.Token, () => { protocolCancellation.Cancel(); return true; }));
    }

    private sealed class ExtensionProcess(AzureCliResult response, string version = VersionJson) : IAzureCliProcess
    {
        public List<string> Commands { get; } = [];
        public Action? OnExtension { get; init; }
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(command.Timeout, timeout);
            var start = command.CreateStartInfo(SelectedPath);
            Commands.Add(string.Join(" ", start.ArgumentList));
            if (Commands.Count == 1) return Task.FromResult(new AzureCliResult(0, version, ""));
            Assert.Equal("no", start.Environment["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"]);
            Assert.Equal("extension list --output json", Commands[^1]);
            OnExtension?.Invoke();
            return Task.FromResult(response);
        }
    }
}
