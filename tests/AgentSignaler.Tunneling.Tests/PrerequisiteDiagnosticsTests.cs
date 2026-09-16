using AgentSignaler.Tunneling;
using Xunit;

namespace AgentSignaler.Tunneling.Tests;

public sealed partial class TunnelTests
{
    private const string DiagnosticPath = @"C:\unsaved installation\devtunnel.exe";

    [Fact]
    public async Task DiagnosticsUseUnsavedPathAndOnlyBoundedReadOnlyCommands()
    {
        var runner = new DiagnosticRunner();
        var check = await DevTunnelDiagnostics.CheckAsync(DiagnosticPath, runner, path => path!, CancellationToken.None);
        Assert.True(check.Passed);
        var result = check.Details;
        Assert.Equal(result, await Diagnose(new DiagnosticRunner()));
        Assert.Contains(DiagnosticPath, result);
        Assert.Contains(TunnelValidation.SupportedCliVersion, result);
        Assert.Contains("Checked version metadata: " + TunnelValidation.SupportedCliVersion, result);
        Assert.Contains("Signature/version: verified and supported", result);
        Assert.Contains("Microsoft account: ready", result);
        Assert.Equal(new[] { "--version", "user show --json" }, runner.Commands);
        Assert.All(runner.Paths, path => Assert.Equal(DiagnosticPath, path));
        Assert.All(runner.Timeouts, timeout => Assert.Equal(TimeSpan.FromSeconds(30), timeout));
        AssertNoAccountDetails(result);
    }

    [Theory]
    [InlineData("devtunnel.exe")]
    [InlineData(@"C:\installed\devtunnel.cmd")]
    [InlineData(@"\\server\share\devtunnel.exe")]
    [InlineData("//server/share/devtunnel.exe")]
    [InlineData(@"C:\installed\devtunnel.exe --version")]
    [InlineData(@"C:\installed\file:devtunnel.exe")]
    [InlineData(@"C:\installed\*.exe")]
    [InlineData(@"C:\installed\?.exe")]
    [InlineData("C:\\installed\\bad\0.exe")]
    public async Task InvalidDiagnosticPathsAreActionableAndNeverExecuted(string path)
    {
        var runner = new DiagnosticRunner();
        var result = await DevTunnelDiagnostics.TestAsync(
            path, runner, DevTunnelDiagnostics.ValidateCliPath, CancellationToken.None);
        Assert.Contains("Path: invalid", result);
        Assert.Contains("absolute local .exe", result);
        Assert.Contains("account: not checked", result);
        Assert.DoesNotContain('\0', result);
        Assert.Empty(runner.Commands);
        Assert.Throws<TunnelException>(() => DevTunnelDiagnostics.ValidateCliPath(path));
        Assert.Throws<ArgumentException>(() => DevTunnelDiagnostics.ResolvePath(path));
    }

    [Fact]
    public void SavePathResolutionDoesNotRequireInstalledExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"not-installed-{Guid.NewGuid():N}.exe");
        Assert.False(File.Exists(path));
        Assert.Equal(path, DevTunnelDiagnostics.ResolvePath("  " + path + "  "));
        Assert.Equal(CliTunnelController.DefaultCliPath, DevTunnelDiagnostics.ResolvePath(null));
    }

    [Fact]
    public async Task DashboardDiagnosticEntryPointIsActionableAndCancellable()
    {
        var result = await DevTunnelDiagnostics.TestAsync("relative.exe");
        Assert.Contains("absolute local .exe", result);
        Assert.Contains("account: not checked", result);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelDiagnostics.TestAsync(null, new CancellationToken(true)));
    }

    [Fact]
    public async Task MissingDiagnosticExecutableIsActionable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"missing-{Guid.NewGuid():N}.exe");
        var result = await DevTunnelDiagnostics.TestAsync(path);
        Assert.Contains(path, result);
        Assert.Contains("install the required CLI explicitly", result);
        Assert.Contains("account: not checked", result);
    }

    [Fact]
    public async Task UnsignedExecutableFailsProductionSignatureCheck()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(AppContext.BaseDirectory, $"unsigned-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllTextAsync(path, "This is not a signed executable.");
            var exception = await Assert.ThrowsAsync<TunnelException>(() =>
                new WindowsTunnelProcessRunner().RunAsync(path, ["--version"], TimeSpan.FromSeconds(5), CancellationToken.None));
            Assert.Contains("signature", exception.Message);
            var result = await DevTunnelDiagnostics.TestAsync(path);
            Assert.Contains(path, result);
            Assert.Contains("Microsoft-signed CLI", result);
            Assert.Contains("Authenticode signature: invalid or untrusted", result);
            Assert.Contains("code 0x", result);
            Assert.Contains("No CLI code ran", result);
            Assert.Contains("Account: not checked", result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingExecutablePathIsNormalizedWithoutRunningIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\..\v1.0\powershell.exe");
        Assert.Equal(Path.GetFullPath(path), DevTunnelDiagnostics.ValidateCliPath("  " + path + "  "));
    }

    [Fact]
    public async Task BlankDiagnosticPathUsesKnownInstallationDiscovery()
    {
        var runner = new DiagnosticRunner();
        var result = await DevTunnelDiagnostics.TestAsync(
            " ", runner, path => path!, CancellationToken.None);
        Assert.All(runner.Paths, path => Assert.Equal(CliTunnelController.DefaultCliPath, path));
        Assert.Contains(CliTunnelController.DefaultCliPath, result);
    }

    [Theory]
    [InlineData(0, "Tunnel CLI version: 1.0.9999+unknown")]
    [InlineData(0, "secret raw diagnostic")]
    [InlineData(1, "Tunnel CLI version: 1.0.2030+fc9273aa0f")]
    public async Task UnsupportedOrFailedVersionDoesNotCheckAccount(int exit, string output)
    {
        var runner = new DiagnosticRunner { Version = new(exit, output, "secret raw diagnostic") };
        var check = await DevTunnelDiagnostics.CheckAsync(DiagnosticPath, runner, path => path!, CancellationToken.None);
        Assert.False(check.Passed);
        var result = check.Details;
        Assert.Contains("Microsoft signature: verified", result);
        Assert.Contains(exit == 0 ? "Version: unsupported" : "command failed (exit code 1)", result);
        if (output.StartsWith("Tunnel CLI version: ", StringComparison.Ordinal))
            Assert.Contains("Checked version metadata: " + output[20..], result);
        else Assert.Contains("Checked version: unrecognized metadata", result);
        Assert.Contains("Account: not checked", result);
        Assert.Equal(new[] { "--version" }, runner.Commands);
        AssertNoAccountDetails(result);
    }

    [Theory]
    [InlineData(0, "{}")]
    [InlineData(0, "secret raw diagnostic")]
    [InlineData(0, """{"status":"Logged in","provider":"github","username":"not-persisted"}""")]
    [InlineData(0, """{"status":"Logged in","provider":"microsoft","tenantId":"bad","objectId":"bad"}""")]
    [InlineData(1, """{"status":"Logged in","provider":"microsoft","tenantId":"11111111-1111-1111-1111-111111111111","objectId":"22222222-2222-2222-2222-222222222222"}""")]
    public async Task UnsupportedOrFailedAccountIsNotReadyWithoutLeakingOutput(int exit, string output)
    {
        var runner = new DiagnosticRunner { AccountResult = new(exit, output, "secret raw diagnostic") };
        var check = await DevTunnelDiagnostics.CheckAsync(DiagnosticPath, runner, path => path!, CancellationToken.None);
        Assert.False(check.Passed);
        var result = check.Details;
        Assert.Contains("Signature/version: verified and supported", result);
        Assert.Contains(exit == 0 ? "Microsoft account: unsupported response schema" :
            "Microsoft account: not ready", result);
        if (exit != 0) Assert.Contains("user show --json: command failed (exit code 1)", result);
        AssertNoAccountDetails(result);
    }

    [Fact]
    public async Task SignedOutDiagnosticGivesExplicitSignInAction()
    {
        var runner = new DiagnosticRunner { AccountResult = new(0, """{"status":"Not logged in"}""", "") };
        var check = await DevTunnelDiagnostics.CheckAsync(DiagnosticPath, runner, path => path!, CancellationToken.None);
        Assert.False(check.Passed);
        var result = check.Details;
        Assert.Contains("Microsoft account: signed out", result);
        Assert.Contains("Sign in explicitly", result);
        Assert.Equal(new[] { "--version", "user show --json" }, runner.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerFailureIsSafeAndActionable(bool timeout)
    {
        var runner = new DiagnosticRunner
        {
            Failure = timeout ? new TimeoutException("secret raw diagnostic") :
                new TunnelException("secret raw diagnostic", TunnelState.Unsupported)
        };
        var result = await Diagnose(runner);
        Assert.Contains(timeout ? "--version: timed out" : "--version: validation failed", result);
        AssertNoAccountDetails(result);
    }

    [Theory]
    [InlineData("UntrustedSignature", "Authenticode signature: invalid or untrusted")]
    [InlineData("WrongPublisher", "Signature publisher: not Microsoft Corporation")]
    [InlineData("CommandTimeout", "--version: timed out (30-second command limit)")]
    [InlineData("OutputLimit", "--version: bounded output limit exceeded")]
    [InlineData("UnsupportedPlatform", "Platform: Windows is required")]
    public async Task StructuredRunnerFailuresHaveDistinctSafeActions(string kind, string expected)
    {
        var runner = new DiagnosticRunner
        {
            Failure = new TunnelException("secret raw diagnostic", TunnelState.Unsupported)
            { FailureKind = Enum.Parse<CliFailureKind>(kind), ErrorCode = unchecked((int)0x800B0109) }
        };
        var check = await DevTunnelDiagnostics.CheckAsync(DiagnosticPath, runner, path => path!, CancellationToken.None);
        Assert.False(check.Passed);
        var result = check.Details;
        Assert.Contains(expected, result);
        Assert.Contains("Account: not checked", result);
        Assert.Contains("Signature/version: not verified", result);
        if (kind == "UntrustedSignature") Assert.Contains("code 0x800B0109", result);
        AssertNoAccountDetails(result);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("user show --json")]
    public async Task NativeErrorsReportCodeAndStageWithoutRawMessage(string stage)
    {
        var runner = new DiagnosticRunner
        {
            Failure = new System.ComponentModel.Win32Exception(5, "secret raw diagnostic"),
            FailureCommand = stage
        };
        var result = await Diagnose(runner);
        Assert.Contains(stage + ": Windows process setup or execution failed", result);
        Assert.Contains("native error 5, 0x00000005", result);
        Assert.Contains(stage == "--version" ? "Account: not checked" : "Microsoft account: not ready", result);
        AssertNoAccountDetails(result);
    }

    [Fact]
    public async Task AccountTimeoutKeepsVerifiedVersionAndReportsItsStage()
    {
        var runner = new DiagnosticRunner
        {
            Failure = new TunnelException("secret raw diagnostic") { FailureKind = CliFailureKind.CommandTimeout },
            FailureCommand = "user show --json"
        };
        var result = await Diagnose(runner);
        Assert.Contains("Signature/version: verified and supported", result);
        Assert.Contains("Microsoft account: not ready", result);
        Assert.Contains("user show --json: timed out", result);
        AssertNoAccountDetails(result);
    }

    [Fact]
    public async Task ConflictingVersionMetadataIsShownWithoutUnrelatedOutput()
    {
        var runner = new DiagnosticRunner
        {
            Version = new(0, VersionOutput + "\nTunnel CLI version: 1.0.9999+unknown\nsecret raw diagnostic",
                "CLI version: not-persisted\n" + Account)
        };
        var result = await Diagnose(runner);
        Assert.Contains("Checked version metadata: " + TunnelValidation.SupportedCliVersion + ", 1.0.9999+unknown", result);
        Assert.Contains("Version: unsupported, conflicting, or unrecognized metadata", result);
        Assert.Equal(new[] { "--version" }, runner.Commands);
        AssertNoAccountDetails(result);
    }

    [Fact]
    public async Task InaccessiblePathReportsSafeFailureCode()
    {
        var runner = new DiagnosticRunner();
        var result = await DevTunnelDiagnostics.TestAsync(DiagnosticPath, runner,
            _ => throw new TunnelException("secret raw diagnostic")
            { FailureKind = CliFailureKind.InaccessiblePath, ErrorCode = unchecked((int)0x80070005) },
            CancellationToken.None);
        Assert.Contains("Path: inaccessible or link resolution failed (code 0x80070005)", result);
        Assert.Contains("Signature/version and account: not checked", result);
        Assert.Empty(runner.Commands);
        AssertNoAccountDetails(result);
    }

    [Fact]
    public async Task PreCancelledDiagnosticsNeverResolveOrRun()
    {
        var runner = new DiagnosticRunner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelDiagnostics.TestAsync(DiagnosticPath, runner,
                _ => throw new InvalidOperationException("Should not resolve"), new CancellationToken(true)));
        Assert.Empty(runner.Commands);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("user show --json")]
    public async Task CancellationPropagatesToReadOnlyCommand(string blockedCommand)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new DiagnosticRunner { BlockedCommand = blockedCommand };
        var check = Diagnose(runner, cancellation.Token);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.Equal(cancellation.Token, runner.LastToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("owner")]
    [InlineData("version")]
    public async Task StandaloneDiagnosticsNeverChangeActiveHostingOrIdentity(string? drift)
    {
        var runner = new FakeRunner();
        var saved = new List<TunnelIdentity>();
        await using var controller = Create(runner, saved: saved);
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        var host = runner.Host;
        var status = controller.Status;
        var identity = controller.Identity;
        var writes = saved.Count;
        var commands = runner.Commands.Count;
        var changed = 0;
        controller.StatusChanged += (_, _) => changed++;
        runner.Drift = drift;

        var result = await DevTunnelDiagnostics.TestAsync(
            DiagnosticPath, runner, path => path!, CancellationToken.None);

        Assert.Contains(drift == "version" ? "Account: not checked" : "Microsoft account: ready", result);
        Assert.Same(host, runner.Host);
        Assert.False(host.Disposed);
        Assert.False(host.Completion.IsCompleted);
        Assert.Same(status, controller.Status);
        Assert.Same(identity, controller.Identity);
        Assert.Equal(writes, saved.Count);
        Assert.Equal(0, changed);
        Assert.All(runner.Commands.Skip(commands), command =>
            Assert.True(command.SequenceEqual(new[] { "--version" }) ||
                command.SequenceEqual(new[] { "user", "show", "--json" })));
    }

    [Fact]
    public async Task CancellingDiagnosticsDoesNotCancelActiveHost()
    {
        var hosting = new FakeRunner();
        var saved = new List<TunnelIdentity>();
        await using var controller = Create(hosting, saved: saved);
        await controller.StartAsync();
        var status = controller.Status;
        var identity = controller.Identity;
        var writes = saved.Count;
        var commands = hosting.Commands.Count;
        using var cancellation = new CancellationTokenSource();
        var diagnostics = new DiagnosticRunner { BlockedCommand = "user show --json" };
        var check = Diagnose(diagnostics, cancellation.Token);
        await diagnostics.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => check);
        Assert.False(hosting.Host.Disposed);
        Assert.False(hosting.Host.Completion.IsCompleted);
        Assert.Same(status, controller.Status);
        Assert.Same(identity, controller.Identity);
        Assert.Equal(writes, saved.Count);
        Assert.Equal(commands, hosting.Commands.Count);
    }

    [Fact]
    public async Task SignedOutDiagnosticsDoNotStopActiveHosting()
    {
        var hosting = new FakeRunner();
        var saved = new List<TunnelIdentity>();
        await using var controller = Create(hosting, saved: saved);
        await controller.StartAsync();
        var status = controller.Status;
        var identity = controller.Identity;
        var writes = saved.Count;
        var commands = hosting.Commands.Count;
        var diagnostics = new DiagnosticRunner { AccountResult = new(0, """{"status":"Not logged in"}""", "") };
        Assert.Contains("Microsoft account: signed out", await Diagnose(diagnostics));
        Assert.False(hosting.Host.Disposed);
        Assert.False(hosting.Host.Completion.IsCompleted);
        Assert.Same(status, controller.Status);
        Assert.Same(identity, controller.Identity);
        Assert.Equal(writes, saved.Count);
        Assert.Equal(commands, hosting.Commands.Count);
    }

    private static Task<string> Diagnose(DiagnosticRunner runner, CancellationToken token = default) =>
        DevTunnelDiagnostics.TestAsync(DiagnosticPath, runner, path => path!, token);

    [Fact]
    public async Task TypedDiagnosticsDoNotInferSuccessFromReadyPathOrNotReadyAccount()
    {
        const string path = @"C:\ready verified and supported\devtunnel.exe";
        var runner = new DiagnosticRunner { AccountResult = new(1, "", "secret raw diagnostic") };
        var result = await DevTunnelDiagnostics.CheckAsync(path, runner, value => value!, CancellationToken.None);
        Assert.False(result.Passed);
        Assert.Contains(path, result.Details);
        Assert.Contains("Signature/version: verified and supported", result.Details);
        Assert.Contains("Microsoft account: not ready", result.Details);
        AssertNoAccountDetails(result.Details);
        Assert.Equal(result.Details, await DevTunnelDiagnostics.TestAsync(path,
            new DiagnosticRunner { AccountResult = runner.AccountResult }, value => value!, CancellationToken.None));
    }

    [Fact]
    public async Task TypedDiagnosticEntryPointRejectsInvalidPathAndCancellation()
    {
        var result = await DevTunnelDiagnostics.CheckAsync("ready.exe");
        Assert.False(result.Passed);
        Assert.Contains("Path: invalid", result.Details);
        Assert.Contains("account: not checked", result.Details);
        Assert.Equal(result.Details, await DevTunnelDiagnostics.TestAsync("ready.exe"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DevTunnelDiagnostics.CheckAsync(null, new CancellationToken(true)));
    }

    private static void AssertNoAccountDetails(string result)
    {
        Assert.DoesNotContain("secret raw diagnostic", result);
        Assert.DoesNotContain("not-persisted", result);
        Assert.DoesNotContain("11111111", result);
        Assert.DoesNotContain("22222222", result);
        Assert.DoesNotContain(TunnelValidation.AccountOwner(Account), result);
    }

    private sealed class DiagnosticRunner : ITunnelProcessRunner
    {
        internal readonly List<string> Commands = [];
        internal readonly List<string> Paths = [];
        internal readonly List<TimeSpan> Timeouts = [];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CliCommandResult Version = new(0, VersionOutput, "");
        internal CliCommandResult AccountResult = new(0, Account, "");
        internal Exception? Failure;
        internal string? FailureCommand;
        internal string? BlockedCommand;
        internal CancellationToken LastToken;

        public async Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = string.Join(" ", arguments);
            Commands.Add(command);
            Paths.Add(executable);
            Timeouts.Add(timeout);
            LastToken = cancellationToken;
            if (Failure is { } failure && (FailureCommand is null || FailureCommand == command)) throw failure;
            if (command == BlockedCommand)
            {
                Entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return command switch
            {
                "--version" => Version,
                "user show --json" => AccountResult,
                _ => throw new InvalidOperationException("Diagnostics must only run read-only prerequisite commands.")
            };
        }

        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Diagnostics must never host.");
    }
}
