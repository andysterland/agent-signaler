using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class IdeDiscoveryVerificationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string RelayPath => Path.Combine(root, "AgentSignaler.Relay.exe");
    private DiscoveryEnvironment EnvironmentFixture => new(Path.Combine(root, "user"), Path.Combine(root, "local"),
        Path.Combine(root, "roaming"), Path.Combine(root, "programs"), Path.Combine(root, "programs-x86"),
        Path.Combine(root, "bin"), Path.Combine(root, "standalone-home"));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private void Write(string path, string text = "") => AtomicFile.Write(path, Encoding.UTF8.GetBytes(text));

    private void SaveConfiguration()
    {
        Write(RelayPath);
        Write(Path.Combine(root, "FixtureIde.exe"));
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(new RemoteConfiguration
        {
            Version = 3, DashboardBaseUrl = "http://localhost:51820/", MachineId = Guid.NewGuid(),
            RelayPath = RelayPath
        }, Protocol.Json));
    }

    private IntegrationTarget Target(string kind = "visual-studio") => new()
    {
        Id = "fixture-target", Kind = kind, DisplayName = "Disposable IDE fixture", InstallationId = "fixture-instance",
        ExecutablePath = Path.Combine(root, "FixtureIde.exe"),
        HostVersion = "18.12.1/1.0.83", ScopeId = "fixture-shared-scope", HookDirectory = Path.Combine(root, "candidate-hooks"),
        SettingsPath = kind == "vscode" ? Path.Combine(root, "profile", "settings.json") : null
    };

    [Fact]
    public async Task StandaloneAndEachVisualStudioInstanceAreIndependentCandidates()
    {
        var env = EnvironmentFixture;
        var cli = Path.Combine(env.PathVariable, "copilot.exe");
        var vswhere = Path.Combine(env.ProgramFilesX86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        var main = Path.Combine(root, "custom-main");
        var preview = Path.Combine(root, "custom-preview");
        Write(cli);
        Write(vswhere);
        Write(Path.Combine(main, "Common7", "IDE", "CopilotCli", "copilot.exe"));
        var probe = new FakeProbe(JsonSerializer.Serialize(new[]
        {
            new { instanceId = "main", installationPath = main, installationVersion = "18.12.12211.431", isLaunchable = true },
            new { instanceId = "preview", installationPath = preview, installationVersion = "18.12.12211.348", isLaunchable = true }
        }));

        var found = await Discovery.InspectTargetsAsync(env, probe);

        var standalone = Assert.Single(found, t => t.Kind == "copilot-cli");
        Assert.Equal(cli, standalone.ExecutablePath);
        Assert.Equal(Path.Combine(env.CopilotHome!, "hooks"), standalone.HookDirectory);
        Assert.True(standalone.CanInstall);
        var instances = found.Where(t => t.Kind == "visual-studio").ToArray();
        Assert.Equal(2, instances.Length);
        Assert.Equal(instances[0].ScopeId, instances[1].ScopeId);
        Assert.NotEqual(instances[0].Id, instances[1].Id);
        Assert.All(instances, t =>
        {
            Assert.Equal(IntegrationCapability.VerificationRequired, t.Capability);
            Assert.False(t.CanInstall);
            Assert.Empty(t.SupportedEvents);
        });
    }

    [Fact]
    public async Task FailedCliHomeDoesNotHideVsCodeProfiles()
    {
        var env = EnvironmentFixture with { CopilotHome = "relative-home" };
        Write(Path.Combine(env.PathVariable, "copilot.exe"));
        Directory.CreateDirectory(Path.Combine(env.AppData, "Code", "User", "profiles", "selected"));
        var custom = Path.Combine(root, "custom-profile");
        Directory.CreateDirectory(custom);
        var found = await Discovery.InspectTargetsAsync(env, new FakeProbe("[]"), customProfileRoots: [custom]);
        Assert.Equal(IntegrationCapability.DiscoveryFailed, found.Single(t => t.Kind == "copilot-cli").Capability);
        var profiles = found.Where(t => t.Kind == "vscode").ToArray();
        Assert.Equal(3, profiles.Length);
        Assert.All(profiles, t => Assert.False(t.CanInstall));
        Assert.Equal(3, profiles.Select(t => t.ScopeId).Distinct().Count());
    }

    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("visual-studio")]
    [InlineData("vscode")]
    public async Task SavedCustomLocationsAreRediscoveredWithoutManualSetup(string kind)
    {
        var custom = AutomaticHookConfiguration.Prepare(
            AutomaticHookConfiguration.Custom(kind, Path.Combine(root, "custom-location")), ConfigPath);
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid() }.ToVersion4() with
        {
            Integrations = [custom]
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var found = await Discovery.InspectTargetsAsync(EnvironmentFixture, new FakeProbe("[]"), ConfigPath);
        var restored = Assert.Single(found, t => t.Id == custom.Id);
        Assert.True(restored.IsCustom);
        Assert.Equal(custom.HookDirectory, restored.HookDirectory);
        Assert.Equal(custom.SettingsPath, restored.SettingsPath);
        Assert.Equal(IntegrationCapability.Configured, restored.Capability);
        AutomaticHookConfiguration.RequireInstallable(restored, ConfigPath);
    }

    [Fact]
    public async Task VersionFailureIsNotAbsenceAndCancellationIsHonored()
    {
        var env = EnvironmentFixture;
        Write(Path.Combine(env.PathVariable, "copilot.exe"));
        var found = await Discovery.InspectTargetsAsync(env, new FakeProbe("[]", fail: true));
        var cli = found.Single(t => t.Kind == "copilot-cli");
        Assert.Equal(IntegrationCapability.DiscoveryFailed, cli.Capability);
        Assert.Contains("timed out", cli.Reason);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Discovery.InspectTargetsAsync(env, new FakeProbe("[]"), cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public async Task BundledCliOnPathNeverBecomesStandalone()
    {
        var env = EnvironmentFixture with { PathVariable = Path.Combine(root, "VS", "Common7", "IDE", "CopilotCli") };
        Write(Path.Combine(env.PathVariable, "copilot.exe"));
        var found = await Discovery.InspectTargetsAsync(env, new FakeProbe("[]"));
        var cli = found.Single(t => t.Kind == "copilot-cli");
        Assert.Null(cli.ExecutablePath);
        Assert.Equal(IntegrationCapability.NotInstalled, cli.Capability);
        Assert.Equal(Path.Combine(env.CopilotHome!, "hooks"), cli.HookDirectory);
    }

    [Fact]
    public void SchemasUseDifferentEventsAndExecutionProperties()
    {
        var cli = Target("copilot-cli") with { SupportedEvents = ["sessionStart", "agentStop"] };
        using var cliJson = JsonDocument.Parse(HookAdapters.Generate(cli, RelayPath, ConfigPath));
        var command = cliJson.RootElement.GetProperty("hooks").GetProperty("agentStop")[0];
        Assert.Equal(RelayPath, command.GetProperty("exec").GetString());
        Assert.Equal(3, command.GetProperty("timeoutSec").GetInt32());
        Assert.False(command.TryGetProperty("windows", out _));
        var code = Target("vscode") with { SupportedEvents = ["SessionStart", "Stop"] };
        using var codeJson = JsonDocument.Parse(HookAdapters.Generate(code, RelayPath, ConfigPath));
        var stop = codeJson.RootElement.GetProperty("hooks").GetProperty("Stop")[0];
        Assert.Equal(3, stop.GetProperty("timeout").GetInt32());
        Assert.Contains("\"--adapter\" \"vscode\"", stop.GetProperty("windows").GetString());
        Assert.False(stop.TryGetProperty("exec", out _));
        Assert.False(codeJson.RootElement.GetProperty("hooks").TryGetProperty("SessionEnd", out _));
        Assert.Throws<InvalidDataException>(() => HookAdapters.Generate(code with { SupportedEvents = ["SubagentStop"] }, RelayPath, ConfigPath));
    }

    [Theory]
    [InlineData("space path")]
    [InlineData("a&b (fixture)^")]
    [InlineData("unicode-\u00e9")]
    public void ShellPathsAreQuotedWithoutPayloadInterpolation(string directory)
    {
        var relay = Path.Combine(root, directory, "AgentSignaler.Relay.exe");
        var command = WindowsHookCommand.Encode(relay, ["hook", "--config", ConfigPath]);
        Assert.StartsWith("\"" + relay + "\" ", command);
        Assert.EndsWith("\"" + ConfigPath + "\"", command);
    }

    [Theory]
    [InlineData("%PATH%")]
    [InlineData("!expanded!")]
    [InlineData("bad\"quote")]
    public void UnsupportedShellExpansionFailsClosed(string unsafeArgument) =>
        Assert.Throws<InvalidDataException>(() => WindowsHookCommand.Encode(RelayPath, [unsafeArgument]));

    [Fact]
    public void VerificationRequiresConsentAndRealWorkflowConfirmationNotJustAProbe()
    {
        SaveConfiguration();
        var target = Target();
        var plan = HookVerification.Preview(target, ConfigPath, RelayPath);
        Assert.Throws<InvalidOperationException>(() => HookVerification.Begin(plan, false));
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
        HookVerification.Begin(plan, true);
        try
        {
            Observe(plan, AgentEvent.SessionStart);
            Assert.False(HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).StableSessionObserved);
            Assert.Throws<InvalidOperationException>(() => HookVerification.Complete(plan, Confirmed));
            ObserveBaseline(plan);
            Assert.True(HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).StableSessionObserved);
            Assert.Throws<InvalidOperationException>(() => HookVerification.Complete(plan, Confirmed with { ActualIdeWorkflow = false }));
            Assert.Throws<InvalidOperationException>(() => HookVerification.RequireVerified(target, ConfigPath));
            var verified = HookVerification.Complete(plan, Confirmed);
            Assert.Equal(IntegrationCapability.PartiallySupported, verified.Capability);
            Assert.True(verified.CanInstall);
            HookVerification.RequireVerified(verified, ConfigPath);
            Assert.False(File.Exists(plan.HookPath));
            Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
            Assert.Equal(IntegrationCapability.VerificationRequired,
                HookVerification.Resolve(verified with { HostVersion = "changed" }, ConfigPath).Capability);
        }
        finally { HookVerification.Cancel(plan); }
    }

    [Fact]
    public void ProfileProbeRecoveryPreservesJsoncAndConcurrentUnrelatedEdits()
    {
        SaveConfiguration();
        var target = Target("vscode");
        var original = "{\n // Keep this comment\n \"editor.fontSize\": 14\n}";
        Write(target.SettingsPath!, original);
        var plan = HookVerification.Preview(target, ConfigPath, RelayPath);
        HookVerification.Begin(plan, true);
        var installed = File.ReadAllText(target.SettingsPath!);
        Assert.Contains("// Keep this comment", installed);
        Write(target.SettingsPath!, installed.Replace("\"editor.fontSize\": 14", "\"editor.fontSize\": 18", StringComparison.Ordinal));
        HookVerification.Recover(ConfigPath);
        var recovered = File.ReadAllText(target.SettingsPath!);
        Assert.Contains("// Keep this comment", recovered);
        Assert.Contains("\"editor.fontSize\": 18", recovered);
        Assert.DoesNotContain("candidate-hooks", recovered);
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
    }

    [Fact]
    public void ModifiedProbeIsNeverDeletedAndCleanupRemainsPending()
    {
        SaveConfiguration();
        var plan = HookVerification.Preview(Target(), ConfigPath, RelayPath);
        HookVerification.Begin(plan, true);
        Write(plan.HookPath, "{\"user\":\"changed\"}");
        Assert.Throws<InvalidDataException>(() => HookVerification.Cancel(plan));
        Assert.Equal("{\"user\":\"changed\"}", File.ReadAllText(plan.HookPath));
        Assert.True(HookVerification.HasPendingCleanup(ConfigPath));
    }

    [Fact]
    public void DifferentScopesAndUnstableSessionsCannotVerifyTarget()
    {
        SaveConfiguration();
        var plan = HookVerification.Preview(Target(), ConfigPath, RelayPath);
        HookVerification.Begin(plan, true);
        try
        {
            Assert.False(HookVerification.AcceptProbe(ConfigPath, plan.ProbeId, AgentEvent.SessionStart,
                new HookData("session", DateTimeOffset.UtcNow, false, Source: new("copilot-cli", plan.Target.ScopeId, plan.Target.HostVersion))));
            foreach (var kind in new[] { AgentEvent.SessionStart, AgentEvent.UserPromptSubmitted, AgentEvent.PreToolUse,
                AgentEvent.PostToolUse, AgentEvent.AgentStop })
                Observe(plan, kind, kind.ToString());
            Assert.False(HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).StableSessionObserved);
            Assert.Throws<InvalidOperationException>(() => HookVerification.Complete(plan, Confirmed));
        }
        finally { HookVerification.Cancel(plan); }
    }

    [Fact]
    public void PolicyObservationNeverEnablesHooksOrChangesProfileSettings()
    {
        SaveConfiguration();
        var target = Target("vscode");
        Write(target.SettingsPath!, "{ /* unchanged */ }");
        var unavailable = HookVerification.RecordUnavailable(target, ConfigPath, IntegrationCapability.BlockedByPolicy,
            "Selected profile reports hooks disabled by organization policy.");
        Assert.False(unavailable.CanInstall);
        Assert.Equal(IntegrationCapability.BlockedByPolicy, HookVerification.Resolve(target, ConfigPath).Capability);
        Assert.Equal("{ /* unchanged */ }", File.ReadAllText(target.SettingsPath!));
        Assert.Throws<InvalidOperationException>(() => HookVerification.RequireVerified(unavailable, ConfigPath));
        Assert.Throws<InvalidOperationException>(() => HookVerification.Preview(unavailable, ConfigPath, RelayPath));
    }

    [Fact]
    public void PreviewDetectsConcurrentSettingsEditsBeforeInstallingDiagnostic()
    {
        SaveConfiguration();
        var target = Target("vscode");
        Write(target.SettingsPath!, "{}");
        var plan = HookVerification.Preview(target, ConfigPath, RelayPath);
        Write(target.SettingsPath!, "{\"userChanged\":true}");
        Assert.Throws<InvalidDataException>(() => HookVerification.Begin(plan, true));
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
        Assert.Equal("{\"userChanged\":true}", File.ReadAllText(target.SettingsPath!));
    }

    [Fact]
    public void SharedVisualStudioHooksDoNotClaimAnInstanceVersion()
    {
        var target = Target() with { SupportedEvents = ["sessionStart"] };
        var first = HookAdapters.Generate(target, RelayPath, ConfigPath);
        var second = HookAdapters.Generate(target with { Id = "other-instance", HostVersion = "18.13.1/1.0.84" }, RelayPath, ConfigPath);
        Assert.Equal(first, second);
        Assert.Contains("shared", Encoding.UTF8.GetString(first));
        Assert.DoesNotContain(target.HostVersion, Encoding.UTF8.GetString(first));
    }

    [Fact]
    public void IdeUpgradeDuringProbeCannotProduceVerifiedRecord()
    {
        SaveConfiguration();
        var target = Target();
        var plan = HookVerification.Preview(target, ConfigPath, RelayPath);
        HookVerification.Begin(plan, true);
        try
        {
            Observe(plan, AgentEvent.SessionStart);
            ObserveBaseline(plan);
            Write(target.ExecutablePath!, "changed installation");
            Assert.Throws<InvalidOperationException>(() => HookVerification.Complete(plan, Confirmed));
            Assert.False(HookVerification.Resolve(target, ConfigPath).CanInstall);
        }
        finally { HookVerification.Cancel(plan); }
    }

    private static VerificationConfirmation Confirmed => new(true, true, true, true, "synthetic fixture only", "new fixture session");

    private void ObserveBaseline(HookVerificationPlan plan)
    {
        foreach (var kind in new[] { AgentEvent.UserPromptSubmitted, AgentEvent.PreToolUse, AgentEvent.PostToolUse, AgentEvent.AgentStop })
            Observe(plan, kind);
    }

    private void Observe(HookVerificationPlan plan, AgentEvent kind, string session = "synthetic-stable-id") =>
        Assert.True(HookVerification.AcceptProbe(ConfigPath, plan.ProbeId, kind, new HookData(session, DateTimeOffset.UtcNow, false,
            Source: new(plan.Target.Kind, plan.Target.ScopeId, plan.Target.HostVersion))));

    private sealed class FakeProbe(string inventory, bool fail = false) : IInstallationProbe
    {
        public Task<InstallationProbeResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(fail ? new InstallationProbeResult(false, "", "Fixture version probe timed out.") :
                new(true, Path.GetFileName(executable) == "vswhere.exe" ? inventory : "1.0.83", ""));
        }
    }
}
