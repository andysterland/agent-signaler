using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class AzureCliProcessTests
{
    [Fact]
    public async Task DebugLoggingIncludesEveryInvocationAndFullResponseIncludingVersionProbe()
    {
        var stdout = new string('x', 2047) + char.ConvertFromUtf32(0x1F600) + "\r\n{\"environmentName\":null}";
        const string stderr = "CLI warning\r\nwith details";
        var runner = new SequenceRunner(new FakeChild("{\"azure-cli\":\"2.90.0\"}"), new FakeChild(stdout, stderr, 7));
        var lines = new List<string>();
        var process = new AzureCliProcess(runner, new FakeInstallation { RequiresVersionCheck = true },
            executablePath: @"C:\CLI\az.exe", debugOutput: lines.Add);

        var result = await process.RunAsync(AzureCliCommand.AccountList(), TimeSpan.FromSeconds(30), default);

        Assert.Equal(stdout, result.StandardOutput);
        Assert.Equal(stderr, result.StandardError);
        Assert.Equal(7, result.ExitCode);
        var invocations = lines.Where(line => line.Contains("Invoking ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, invocations.Length);
        Assert.Contains("az \"version\"", invocations[0]);
        Assert.Contains("az \"account\" \"list\" \"--all\"", invocations[1]);
        var prefix = invocations[1][..(invocations[1].IndexOf(']') + 1)];
        var response = lines.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        Assert.Contains(response, line => line.Contains("Response: exitCode=7; elapsed=", StringComparison.Ordinal));
        Assert.Equal(stdout, string.Concat(response.Where(line => line.Contains(" stdout[", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<string>(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]))));
        Assert.Equal(stderr, string.Concat(response.Where(line => line.Contains(" stderr[", StringComparison.Ordinal))
            .Select(line => JsonSerializer.Deserialize<string>(line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..]))));
        Assert.All(lines, line => { Assert.DoesNotContain("\r", line); Assert.DoesNotContain("\n", line); });
    }

    [Fact]
    public async Task DebugLoggingRecordsStartupFailureWithoutClaimingAResponse()
    {
        var lines = new List<string>();
        var runner = new FakeRunner(new FakeChild()) { FailStart = true };
        var process = new AzureCliProcess(runner, new FakeInstallation(), debugOutput: lines.Add);

        await Assert.ThrowsAsync<WindowsAppConnectionException>(() =>
            process.RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default));

        Assert.Contains(lines, line => line.Contains("Invoking ", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Process failure: Win32Exception", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("No complete response:", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Response: exitCode=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultDebuggerOutputDoesNotRequireAnAttachedDebugger()
    {
        var result = await Run(new FakeChild("{\"environmentName\":null}", "CLI diagnostic", 7));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("{\"environmentName\":null}", result.StandardOutput);
        Assert.Equal("CLI diagnostic", result.StandardError);
    }

    [Fact]
    public void CommandsHaveOnlyTypedFactoriesAndNoEscapeHatches()
    {
        var type = typeof(AzureCliCommand);
        Assert.True(type.IsSealed);
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
            c => !c.IsPrivate);
        var factories = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(["AccountList", "AccountShow", "ListDevBoxes", "ListDevBoxesByName", "ListDevBoxesPage", "ListDevCenters", "ListDevCentersPage", "ListExtensions", "Login", "RemoteConnection", "Version"], factories.Select(m => m.Name).Order().ToArray());
        Assert.All(factories.SelectMany(m => m.GetParameters()), parameter =>
            Assert.True(parameter.ParameterType == typeof(Guid?) || parameter.ParameterType == typeof(Guid) || parameter.ParameterType == typeof(Uri) ||
                parameter.ParameterType == typeof(AgentSignaler.Service.WindowsAppConnection) ||
                parameter.ParameterType == typeof(string) && parameter.Member.Name == nameof(AzureCliCommand.ListDevBoxesByName)));
        Assert.DoesNotContain(type.GetProperties(), p => p.SetMethod is not null);
        Assert.Empty(type.GetFields(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(["login"], AzureCliCommand.Login().CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(["login", "--tenant", ConnectionTestData.Tenant.ToString()],
            AzureCliCommand.Login(ConnectionTestData.Tenant).CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(["account", "show", "--output", "json"], AzureCliCommand.AccountShow().CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(["account", "list", "--all", "--output", "json"], AzureCliCommand.AccountList().CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(TimeSpan.FromSeconds(30), AzureCliCommand.AccountList().Timeout);
        Assert.Equal(["version", "--output", "json"], AzureCliCommand.Version().CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(["extension", "list", "--output", "json"], AzureCliCommand.ListExtensions().CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Equal(["rest", "--method", "get", "--resource", "https://devcenter.azure.com", "--url",
            "https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01/remoteConnection?api-version=2025-02-01"],
            AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping).CreateStartInfo(@"C:\CLI\az.exe").ArgumentList);
        Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.Login(Guid.Empty));
    }

    [Fact]
    public void NamedDevCenterCommandUsesCurrentUserJsonAndNeverInstallsExtensions()
    {
        var command = AzureCliCommand.ListDevBoxesByName("devcenter-tfotz75rskxty-dc");
        var start = command.CreateStartInfo(@"C:\CLI\az.exe");
        Assert.Equal(["devcenter", "dev", "dev-box", "list", "--dev-center-name", "devcenter-tfotz75rskxty-dc",
            "--user-id", "me", "--output", "json"], start.ArgumentList);
        Assert.Equal("no", start.Environment["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"]);
        Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
        Assert.False(start.UseShellExecute);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("--debug")]
    [InlineData("name --debug")]
    [InlineData("name/center")]
    [InlineData("name\n")]
    public void NamedDevCenterCommandRejectsInvalidNames(string? name) =>
        Assert.Throws<ArgumentException>(() => AzureCliCommand.ListDevBoxesByName(name!));

    [Fact]
    public void ArmDiscoveryFactoriesUseDocumentedGetOnlyApiAndLiteralScopedPagination()
    {
        var subscription = ConnectionTestData.Subscription;
        var expected = $"https://management.azure.com/subscriptions/{subscription}/providers/Microsoft.DevCenter/devcenters?api-version=2025-02-01";
        var next = new Uri($"{expected}&$skiptoken=abc%2Bdef%3D&literal=%26%22--method%20post");
        var first = AzureCliCommand.ListDevCenters(subscription);
        var subsequent = AzureCliCommand.ListDevCentersPage(subscription, next);
        foreach (var command in new[] { first, subsequent })
        {
            var start = command.CreateStartInfo(@"C:\CLI\az.exe");
            Assert.Equal(["rest", "--method", "get", "--resource", "https://management.azure.com/", "--url"], start.ArgumentList.Take(6));
            Assert.Equal(7, start.ArgumentList.Count);
            Assert.False(start.UseShellExecute);
            Assert.Empty(start.Arguments);
            Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
            Assert.DoesNotContain(subscription.ToString(), command.ToString());
            Assert.DoesNotContain("--subscription", start.ArgumentList);
        }
        Assert.Equal(expected, first.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[^1]);
        Assert.Equal(next.AbsoluteUri, subsequent.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[^1]);
        Assert.Equal(WindowsAppFailure.MalformedResponse,
            Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.ListDevCenters(Guid.Empty)).Failure);
        Assert.Equal(WindowsAppFailure.MalformedResponse,
            Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.ListDevCentersPage(Guid.Empty, next)).Failure);
    }

    public static IEnumerable<object[]> UnsafeArmLinks()
    {
        var path = $"/subscriptions/{ConnectionTestData.Subscription}/providers/Microsoft.DevCenter/devcenters";
        var url = $"https://management.azure.com{path}?api-version=2025-02-01";
        foreach (var next in new[]
        {
            $"http://management.azure.com{path}?api-version=2025-02-01",
            $"https://management.usgovcloudapi.net{path}?api-version=2025-02-01",
            $"https://management.azure.com.evil.test{path}?api-version=2025-02-01",
            $"https://management.azure.com:444{path}?api-version=2025-02-01",
            $"https://user@management.azure.com{path}?api-version=2025-02-01",
            $"https://management.azure.com./{path}?api-version=2025-02-01",
            $"{url}#fragment", $"{url}&token=%0a", $"{url}&token=%zz", $"{url}\n",
            $"{url}&api-version=2025-02-01", $"{url}&API-VERSION=2025-02-01",
            url.Replace("2025-02-01", "2025-02-01-preview"),
            url.Replace(ConnectionTestData.Subscription.ToString(), "55555555-5555-5555-5555-555555555555"),
            url.Replace("devcenters?", "projects?"),
            url.Replace("devcenters?", "devcenters/center-one?"),
            url.Replace("/providers/", "/resourceGroups/rg-one/providers/"),
            url.Replace("/providers/", "/wrong/../providers/"),
            url.Replace("/providers/", "/wrong/%2e%2e/providers/"),
            url.Replace("/providers/", "%2fproviders/"),
            url.Replace("/providers/", "\\providers/"),
            $"https://management.azure.com{path}", "/relative", ""
        }) yield return [next];
    }

    [Theory]
    [MemberData(nameof(UnsafeArmLinks))]
    public void ArmPaginationRejectsUnsafeOriginSubscriptionPathAndVersion(string next)
    {
        var error = Assert.Throws<WindowsAppConnectionException>(() =>
            AzureCliCommand.ListDevCentersPage(ConnectionTestData.Subscription, new Uri(next, UriKind.RelativeOrAbsolute)));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        Assert.DoesNotContain("management.azure.com", error.Message);
    }

    [Fact]
    public void CatalogCommandsAreGetOnlyAndPreserveLiteralPaginationArgument()
    {
        var endpoint = ConnectionTestData.Mapping.DevCenterEndpoint;
        var next = new Uri(endpoint, "devboxes?api-version=2025-02-01&$skiptoken=abc%2Bdef%3D&x=%26%22--method%20post");
        foreach (var command in new[] { AzureCliCommand.ListDevBoxes(endpoint), AzureCliCommand.ListDevBoxesPage(endpoint, next) })
        {
            var start = command.CreateStartInfo(@"C:\CLI\az.exe");
            Assert.Equal(["rest", "--method", "get", "--resource", "https://devcenter.azure.com", "--url"],
                start.ArgumentList.Take(6));
            Assert.Equal(7, start.ArgumentList.Count);
            Assert.False(start.UseShellExecute);
            Assert.Empty(start.Arguments);
            Assert.Equal(TimeSpan.FromSeconds(30), command.Timeout);
            Assert.DoesNotContain(endpoint.IdnHost, command.ToString());
        }
        Assert.Equal("https://example.region.devcenter.azure.com/devboxes?api-version=2025-02-01",
            AzureCliCommand.ListDevBoxes(endpoint).CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[^1]);
        Assert.Equal(next.AbsoluteUri, AzureCliCommand.ListDevBoxesPage(endpoint, next).CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[^1]);
    }

    [Theory]
    [InlineData("http://example.region.devcenter.azure.com/devboxes")]
    [InlineData("https://other.region.devcenter.azure.com/devboxes")]
    [InlineData("https://example.region.devcenter.azure.com.evil.test/devboxes")]
    [InlineData("https://example.region.devcenter.azure.com:444/devboxes")]
    [InlineData("https://user@example.region.devcenter.azure.com/devboxes")]
    [InlineData("https://example.region.devcenter.azure.com/devboxes#fragment")]
    [InlineData("https://example.region.devcenter.azure.com/devboxes?token=%0a")]
    [InlineData("https://example.region.devcenter.azure.com/devboxes?token=%zz")]
    [InlineData("https://example.region.devcenter.azure.com/dev\\boxes")]
    [InlineData("https://example.region.devcenter.azure.com/devboxes\n")]
    [InlineData("/devboxes?token=next")]
    public void CatalogPaginationRejectsUnsafeOriginsAndUriShapes(string next)
    {
        var error = Assert.Throws<WindowsAppConnectionException>(() =>
            AzureCliCommand.ListDevBoxesPage(ConnectionTestData.Mapping.DevCenterEndpoint, new Uri(next, UriKind.RelativeOrAbsolute)));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        Assert.DoesNotContain(next, error.Message);
    }

    [Fact]
    public void CatalogFactoriesValidateEndpointBeforeUsingPagination()
    {
        var endpoint = new Uri("https://evil.test/");
        var page = new Uri(endpoint, "devboxes");
        Assert.Equal(WindowsAppFailure.InvalidMapping,
            Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.ListDevBoxes(endpoint)).Failure);
        Assert.Equal(WindowsAppFailure.InvalidMapping,
            Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.ListDevBoxesPage(endpoint, page)).Failure);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("az vm delete")]
    [InlineData("--body {}")]
    [InlineData("https://evil.example/action")]
    public void UnsupportedShapesCannotBecomeMethodsActionsOrCommands(string unsupported)
    {
        Assert.Throws<WindowsAppConnectionException>(() => AzureCliCommand.RemoteConnection(
            ConnectionTestData.Mapping with { ProjectName = $"abc/{unsupported}" }));
        var arguments = AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping with { DevBoxName = "restart" }).CreateStartInfo(@"C:\CLI\az.exe").ArgumentList;
        Assert.Equal("get", arguments[2]);
        Assert.EndsWith("/users/me/devboxes/restart/remoteConnection?api-version=2025-02-01", arguments[^1]);
        Assert.DoesNotContain("--body", arguments);
    }

    [Fact]
    public void ProcessStartIsFixedNoShellAndUsesArgumentListOnly()
    {
        foreach (var command in new[] { AzureCliCommand.Login(), AzureCliCommand.AccountShow(), AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping) })
        {
            var start = command.CreateStartInfo(@"C:\CLI\az.exe");
            Assert.Equal(@"C:\CLI\az.exe", start.FileName);
            Assert.True(Path.IsPathFullyQualified(start.FileName));
            Assert.False(start.UseShellExecute);
            Assert.True(start.RedirectStandardError && start.RedirectStandardOutput && start.CreateNoWindow);
            Assert.Empty(start.Arguments);
            Assert.DoesNotContain("devcenter.azure.com", command.ToString());
        }
        Assert.Equal(TimeSpan.FromMinutes(10), AzureCliCommand.Login().Timeout);
        Assert.Equal(TimeSpan.FromSeconds(15), AzureCliCommand.AccountShow().Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping).Timeout);
    }

    [Theory]
    [InlineData("2.89.9", false)]
    [InlineData("2.90.0", true)]
    [InlineData("3.0.0", true)]
    public void InstallationRequiresCliVersionRatherThanLauncherFileVersion(string version, bool valid)
    {
        void Validate() => AzureCliDiagnostics.ReadVersion(new(0, $"{{\"azure-cli\":\"{version}\"}}", ""));
        if (valid) Validate();
        else Assert.Equal(WindowsAppFailure.CliUnsupported, Assert.Throws<WindowsAppConnectionException>(Validate).Failure);
    }

    [Theory]
    [InlineData("az.exe")]
    [InlineData("az.cmd")]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData(@"C:\other\other.exe")]
    [InlineData(@"C:\other\az.exe:stream")]
    [InlineData(@"\\server\share\az.exe")]
    [InlineData("C:\\other\\az.exe\n")]
    [InlineData("\tC:\\other\\az.cmd")]
    [InlineData("\r\n")]
    public void AlternateExecutableLocationsAreRejected(string path) =>
        Assert.Throws<ArgumentException>(() => AzureCliInstallation.ResolvePath(path));

    [Theory]
    [InlineData(@"C:\Custom CLI\az.exe", @"C:\Custom CLI\az.exe")]
    [InlineData(@"C:\Custom CLI\wbin\az.cmd", @"C:\Custom CLI\wbin\az.cmd")]
    [InlineData(@" C:\Custom CLI\wbin\..\az.exe ", @"C:\Custom CLI\az.exe")]
    public void CustomPathsAreNormalized(string path, string expected) =>
        Assert.Equal(expected, AzureCliInstallation.ResolvePath(path));

    [Fact]
    public void DefaultOnlyProbesOfficialLocations()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft SDKs", "Azure", "CLI2", "wbin");
        var exe = Path.Combine(root, "az.exe");
        Assert.Equal(File.Exists(exe) ? exe : Path.Combine(root, "az.cmd"), AzureCliInstallation.ResolvePath(null));
        Assert.Equal(AzureCliInstallation.ResolvePath(null), AzureCliInstallation.ResolvePath(" "));
    }

    [Fact]
    public void CmdUsesDirectIsolatedRuntimeAndLiteralArgumentsEvenWithShellMetacharacters()
    {
        var start = AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping).CreateStartInfo(
            @"C:\CLI & echo %TOKEN% !name! (test)\wbin\az.cmd");
        Assert.Equal(@"C:\CLI & echo %TOKEN% !name! (test)\python.exe", start.FileName);
        Assert.Equal(["-IBm", "azure.cli", "rest", "--method", "get", "--resource",
            "https://devcenter.azure.com", "--url",
            "https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01/remoteConnection?api-version=2025-02-01"], start.ArgumentList);
        Assert.Equal("MSI", start.Environment["AZ_INSTALLER"]);
        Assert.False(start.UseShellExecute);
        Assert.Empty(start.Arguments);
    }

    [Fact]
    public void ModifiedOrMisplacedCmdLauncherIsRejected()
    {
        const string launcher = "@IF EXIST \"%~dp0\\..\\python.exe\" (\nSET AZ_INSTALLER=MSI\n\"%~dp0\\..\\python.exe\" -IBm azure.cli %*\n) ELSE (\necho Failed to load python executable.\nexit /b 1\n)";
        AzureCliInstallation.ValidateLauncher(@"C:\CLI\wbin\az.cmd", ":: official comment\r\n" + launcher);
        Assert.Throws<WindowsAppConnectionException>(() => AzureCliInstallation.ValidateLauncher(@"C:\CLI\az.cmd", launcher));
        Assert.Throws<WindowsAppConnectionException>(() => AzureCliInstallation.ValidateLauncher(@"C:\CLI\wbin\az.cmd", launcher + "\necho hacked"));
    }

    [Theory]
    [InlineData(@"C:\CLI\az.exe", @"C:\CLI\az.exe")]
    [InlineData(@"C:\CLI\wbin\az.cmd", @"C:\CLI\python.exe")]
    public async Task CapturedCustomPathIsUsedForVersionAndActualCommand(string path, string childPath)
    {
        var runner = new SequenceRunner(new FakeChild("{\"azure-cli\":\"2.90.0\"}"), new FakeChild("{}"));
        var process = new AzureCliProcess(runner, new FakeInstallation { RequiresVersionCheck = true }, executablePath: path);
        await process.RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default);
        Assert.Equal(2, runner.StartInfos.Count);
        Assert.All(runner.StartInfos, start => Assert.Equal(childPath, start.FileName));
        Assert.Contains("version", runner.StartInfos[0].ArgumentList);
        Assert.Contains("account", runner.StartInfos[1].ArgumentList);
    }

    [Fact]
    public async Task UnsupportedVersionPreventsAccountProcessStart()
    {
        var runner = new SequenceRunner(new FakeChild("{\"azure-cli\":\"2.89.0\"}"));
        var process = new AzureCliProcess(runner, new FakeInstallation { RequiresVersionCheck = true }, executablePath: @"C:\CLI\az.exe");
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() =>
            process.RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default));
        Assert.Equal(WindowsAppFailure.CliUnsupported, error.Failure);
        Assert.Single(runner.StartInfos);
    }

    [Fact]
    public async Task VersionProbeSharesTheOriginalOperationDeadline()
    {
        var time = new TestTimeProvider();
        var account = new FakeChild { BlockExit = true };
        var runner = new SequenceRunner(
            new FakeChild("{\"azure-cli\":\"2.90.0\"}") { AfterWait = () => time.Advance(TimeSpan.FromSeconds(6)) },
            account);
        var process = new AzureCliProcess(runner, new FakeInstallation { RequiresVersionCheck = true }, time, @"C:\CLI\az.exe");
        var pending = process.RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default);
        Assert.Equal(2, runner.StartInfos.Count);
        time.Advance(TimeSpan.FromSeconds(8));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => pending);
        Assert.Equal(WindowsAppFailure.TimedOut, error.Failure);
        Assert.Equal(1, account.Kills);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task StatusChecksVersionAndUserWithoutInspectingPeOrSignature(bool cmd, bool signedIn)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"AgentSignaler-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "wbin"));
        try
        {
            var path = Path.Combine(directory, cmd ? @"wbin\az.cmd" : "az.exe");
            if (cmd)
            {
                const string launcher = "@IF EXIST \"%~dp0\\..\\python.exe\" (\nSET AZ_INSTALLER=MSI\n\"%~dp0\\..\\python.exe\" -IBm azure.cli %*\n) ELSE (\necho Failed to load python executable.\nexit /b 1\n)";
                File.WriteAllText(path, launcher);
            }
            File.WriteAllText(cmd ? Path.Combine(directory, "python.exe") : path,
                "No PE header or Authenticode signature. Only the injected runner executes this fixture.");
            const string account = """
                {"id":"22222222-2222-2222-2222-222222222222","tenantId":"11111111-1111-1111-1111-111111111111",
                "state":"Enabled","user":{"name":"user@example.com","type":"user"}}
                """;
            var runner = new SequenceRunner(new FakeChild("{\"azure-cli\":\"2.90.0\"}"),
                signedIn ? new FakeChild(account) : new FakeChild("", "Please run az login", 1));
            var report = await AzureCliDiagnostics.TestAsync(path, default,
                selected => new AzureCliProcess(runner: runner, executablePath: selected));
            Assert.Equal(2, runner.StartInfos.Count);
            Assert.Contains("version", runner.StartInfos[0].ArgumentList);
            Assert.Contains("account", runner.StartInfos[1].ArgumentList);
            Assert.Contains("Installed Azure CLI version: 2.90.0", report);
            if (signedIn) Assert.Contains("Account/sign-in: signed in", report);
            else
            {
                Assert.Contains("Account/sign-in check failed", report);
                Assert.DoesNotContain("Account/sign-in: signed in", report);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NonzeroExitIsReturnedWithBothStreamsWithoutLoggingThem()
    {
        var child = new FakeChild("secret-stdout", "secret-stderr", 42);
        var runner = new FakeRunner(child);
        var install = new FakeInstallation();
        var result = await new AzureCliProcess(runner, install).RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default);
        Assert.Equal(42, result.ExitCode);
        Assert.Equal("secret-stdout", result.StandardOutput);
        Assert.Equal("secret-stderr", result.StandardError);
        Assert.DoesNotContain("secret", result.ToString());
        Assert.True(child.Disposed && install.Disposed);
        Assert.Equal(0, child.Kills);
        Assert.Equal(1, runner.Starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EachRawStreamHasAnIndependentOneMibLimit(bool stderr)
    {
        var bytes = new byte[AzureCliProcess.MaximumOutputBytes + 1];
        Array.Fill(bytes, (byte)'x');
        var child = new FakeChild { StandardOutput = stderr ? new BlockingStream() : new MemoryStream(bytes),
            StandardError = stderr ? new MemoryStream(bytes) : new BlockingStream(), BlockExit = true };
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => Run(child).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(WindowsAppFailure.CliUnavailable, error.Failure);
        Assert.Equal(1, child.Kills);
        Assert.True(child.Disposed);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ExactByteCapIsAcceptedAndMultibyteCapCountsBytes()
    {
        var full = new string('x', AzureCliProcess.MaximumOutputBytes);
        var result = await Run(new FakeChild(full, full));
        Assert.Equal(full.Length, result.StandardOutput.Length);
        Assert.Equal(full.Length, result.StandardError.Length);
        var child = new FakeChild(new string('é', AzureCliProcess.MaximumOutputBytes / 2 + 1), "");
        await Assert.ThrowsAsync<WindowsAppConnectionException>(() => Run(child));
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task InvalidUtf8AndStreamFailureAreSanitizedAndKillOnlyOwnedChild()
    {
        foreach (var stream in new Stream[] { new MemoryStream([0xff]), new ThrowingStream() })
        {
            var child = new FakeChild { StandardOutput = stream, BlockExit = true };
            var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => Run(child));
            Assert.Equal(1, child.Kills);
            Assert.DoesNotContain("secret", error.ToString());
        }
    }

    [Fact]
    public async Task ExitObservationFailureKillsOnlyStartedTreeAndDoesNotExposeProcessDetails()
    {
        var child = new FakeChild { WaitThrows = true };
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => Run(child));
        Assert.Equal(WindowsAppFailure.CliUnavailable, error.Failure);
        Assert.Equal(1, child.Kills);
        Assert.True(child.Disposed);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public async Task DeadlineReachedDuringInstallationCheckPreventsProcessStart()
    {
        var time = new TestTimeProvider();
        var command = AzureCliCommand.AccountShow();
        var runner = new FakeRunner(new FakeChild());
        var installation = new FakeInstallation { AfterValidate = () => time.Advance(command.Timeout) };
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() =>
            new AzureCliProcess(runner, installation, time).RunAsync(command, command.Timeout, default));
        Assert.Equal(WindowsAppFailure.TimedOut, error.Failure);
        Assert.Equal(0, runner.Starts);
        Assert.True(installation.Disposed);
    }

    [Fact]
    public async Task CancellationBeforeStartNeverValidatesOrStarts()
    {
        var runner = new FakeRunner(new FakeChild());
        var installation = new FakeInstallation();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AzureCliProcess(runner, installation)
            .RunAsync(AzureCliCommand.Login(), TimeSpan.FromMinutes(10), new CancellationToken(true)));
        Assert.Equal(0, runner.Starts);
        Assert.Equal(0, installation.Validations);
    }

    [Fact]
    public async Task CancellationKillsStartedTreeAndReleasesFile()
    {
        using var cancellation = new CancellationTokenSource();
        var child = new FakeChild { StandardOutput = new BlockingStream(), BlockExit = true, KillThrows = true };
        var runner = new FakeRunner(child);
        var installation = new FakeInstallation();
        var pending = new AzureCliProcess(runner, installation).RunAsync(AzureCliCommand.Login(), TimeSpan.FromMinutes(10), cancellation.Token);
        await runner.Started.Task;
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, child.Kills);
        Assert.True(child.Disposed && installation.Disposed);
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ExactOperationDeadlinesUseInjectedTime(int operation)
    {
        var command = operation switch
        {
            0 => AzureCliCommand.AccountShow(),
            1 => AzureCliCommand.RemoteConnection(ConnectionTestData.Mapping),
            3 => AzureCliCommand.ListDevBoxes(ConnectionTestData.Mapping.DevCenterEndpoint),
            4 => AzureCliCommand.ListDevBoxesPage(ConnectionTestData.Mapping.DevCenterEndpoint,
                new Uri(ConnectionTestData.Mapping.DevCenterEndpoint, "devboxes?continuation=next")),
            _ => AzureCliCommand.Login()
        };
        var time = new TestTimeProvider();
        var child = new FakeChild { BlockExit = true };
        var runner = new FakeRunner(child);
        var pending = new AzureCliProcess(runner, new FakeInstallation(), time).RunAsync(command, command.Timeout, default);
        await runner.Started.Task;
        time.Advance(command.Timeout - TimeSpan.FromTicks(1));
        Assert.False(pending.IsCompleted);
        time.Advance(TimeSpan.FromTicks(1));
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => pending);
        Assert.Equal(WindowsAppFailure.TimedOut, error.Failure);
        Assert.Equal(1, child.Kills);
    }

    [Fact]
    public async Task UnsupportedInstallationOrStartFailureNeverKillsAnUnstartedProcess()
    {
        foreach (var installationFails in new[] { false, true })
        {
            var child = new FakeChild();
            var runner = new FakeRunner(child) { FailStart = true };
            var installation = new FakeInstallation { Fail = installationFails };
            var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() => new AzureCliProcess(runner, installation)
                .RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default));
            Assert.Equal(installationFails ? WindowsAppFailure.CliUnsupported : WindowsAppFailure.CliUnavailable, error.Failure);
            Assert.Equal(installationFails ? 0 : 1, runner.Starts);
            Assert.Equal(0, child.Kills);
            Assert.DoesNotContain("secret", error.ToString());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16)]
    public async Task CallerCannotDisableOrExtendDeadline(int seconds)
    {
        var runner = new FakeRunner(new FakeChild());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new AzureCliProcess(runner, new FakeInstallation())
            .RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(seconds), default));
        Assert.Equal(0, runner.Starts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ProgrammerErrorsPropagateWhileOwnedResourcesAreCleanedUp(int boundary)
    {
        var expected = new InvalidOperationException("Programming error");
        var child = new FakeChild
        {
            StandardOutput = boundary == 2 ? new ThrowingStream(expected) : new MemoryStream(),
            WaitError = boundary == 3 ? expected : null,
            DisposeError = new IOException("secret cleanup details")
        };
        var runner = new FakeRunner(child) { StartError = boundary == 1 ? expected : null };
        var installation = new FakeInstallation
        {
            AfterValidate = () => { if (boundary == 0) throw expected; }
        };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AzureCliProcess(runner, installation).RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default));
        Assert.Same(expected, error);
        Assert.Equal(boundary >= 2 ? 1 : 0, child.Kills);
        Assert.Equal(boundary >= 2, child.Disposed);
        Assert.Equal(boundary != 0, installation.Disposed);
    }

    [Fact]
    public async Task SuccessfulProcessWithDisposeIoFailureIsSanitizedAndStillReleasesInstallation()
    {
        var child = new FakeChild { DisposeError = new IOException("secret " + ConnectionTestData.Uri) };
        var installation = new FakeInstallation();
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() =>
            new AzureCliProcess(new FakeRunner(child), installation).RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default));
        Assert.Equal(WindowsAppFailure.CliUnavailable, error.Failure);
        Assert.True(child.Disposed && installation.Disposed);
        Assert.Equal(0, child.Kills);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("ms-cloudpc:", error.ToString());
    }

    private static Task<AzureCliResult> Run(FakeChild child) => new AzureCliProcess(new FakeRunner(child), new FakeInstallation())
        .RunAsync(AzureCliCommand.AccountShow(), TimeSpan.FromSeconds(15), default);

    private sealed class FakeInstallation : IAzureCliInstallation, IDisposable
    {
        public bool RequiresVersionCheck { get; init; }
        public int Validations;
        public bool Disposed, Fail;
        public Action? AfterValidate;
        public IDisposable Validate()
        {
            Validations++;
            if (Fail) throw new WindowsAppConnectionException(WindowsAppFailure.CliUnsupported);
            AfterValidate?.Invoke();
            return this;
        }
        public void Dispose() => Disposed = true;
    }

    private sealed class SequenceRunner(params FakeChild[] children) : IAzureCliProcessRunner
    {
        public List<ProcessStartInfo> StartInfos { get; } = [];
        public IAzureCliChildProcess Start(ProcessStartInfo startInfo)
        {
            StartInfos.Add(startInfo);
            return children[StartInfos.Count - 1];
        }
    }

    private sealed class FakeRunner(FakeChild child) : IAzureCliProcessRunner
    {
        public int Starts;
        public bool FailStart;
        public Exception? StartError;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IAzureCliChildProcess Start(ProcessStartInfo startInfo)
        {
            Starts++;
            if (StartError is not null) throw StartError;
            if (FailStart) throw new Win32Exception("secret command output");
            Started.TrySetResult();
            return child;
        }
    }

    private sealed class FakeChild : IAzureCliChildProcess
    {
        public FakeChild(string stdout = "", string stderr = "", int exitCode = 0)
        {
            StandardOutput = new MemoryStream(Encoding.UTF8.GetBytes(stdout));
            StandardError = new MemoryStream(Encoding.UTF8.GetBytes(stderr));
            ExitCode = exitCode;
        }
        public Stream StandardOutput { get; init; }
        public Stream StandardError { get; init; }
        public int ExitCode { get; }
        public bool BlockExit, KillThrows, Disposed, WaitThrows;
        public Exception? WaitError, DisposeError;
        public Action? AfterWait;
        public int Kills;
        public Task WaitForExitAsync(CancellationToken token)
        {
            if (WaitError is not null) throw WaitError;
            if (WaitThrows) throw new IOException("secret child process information");
            AfterWait?.Invoke();
            return BlockExit ? Task.Delay(Timeout.Infinite, token) : Task.CompletedTask;
        }
        public void KillTree()
        {
            Kills++;
            if (KillThrows) throw new Win32Exception("secret");
        }
        public void Dispose()
        {
            Disposed = true;
            StandardOutput.Dispose();
            StandardError.Dispose();
            if (DisposeError is not null) throw DisposeError;
        }
    }

    private class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream(Exception? error = null) : BlockingStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(error ?? new IOException("secret output"));
    }
}

internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset now = ConnectionTestData.RetrievedAt;
    private readonly List<TestTimer> timers = [];
    public override DateTimeOffset GetUtcNow() => now;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TestTimer(this, callback, state);
        timer.Change(dueTime, period);
        timers.Add(timer);
        return timer;
    }
    public void Advance(TimeSpan amount)
    {
        now += amount;
        foreach (var timer in timers.ToArray()) timer.Fire();
    }
    private sealed class TestTimer(TestTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? due;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }
        public void Fire()
        {
            if (due is { } deadline && deadline <= owner.now)
            {
                due = null;
                callback(state);
            }
        }
        public void Dispose() => due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
