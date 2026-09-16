using AgentSignaler.Contracts;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class AutomaticHookConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");

    [Theory]
    [InlineData(IntegrationCapability.NotInstalled)]
    [InlineData(IntegrationCapability.DiscoveryFailed)]
    [InlineData(IntegrationCapability.BlockedByPolicy)]
    [InlineData(IntegrationCapability.VerificationFailed)]
    public void KnownUnavailableTargetsAreNotAutomaticallyEnabled(IntegrationCapability capability)
    {
        var target = AutomaticHookConfiguration.Custom("vscode", Path.Combine(root, "profile")) with
        {
            Capability = capability, Reason = "Unavailable fixture"
        };
        Assert.Throws<InvalidOperationException>(() => AutomaticHookConfiguration.Prepare(target, ConfigPath));
    }

    [Fact]
    public void RecordedPolicyBlockIsRecheckedBeforeApplyAndReporting()
    {
        var executable = Path.Combine(root, "host.exe");
        AtomicFile.Write(executable, [1, 2, 3]);
        var candidate = AutomaticHookConfiguration.Custom("visual-studio", Path.Combine(root, "hooks")) with
        {
            ExecutablePath = executable, HostVersion = "18.12"
        };
        var configured = AutomaticHookConfiguration.Prepare(candidate, ConfigPath);
        HookVerification.RecordUnavailable(candidate, ConfigPath, IntegrationCapability.BlockedByPolicy, "Policy blocks hooks");
        Assert.Throws<InvalidOperationException>(() => AutomaticHookConfiguration.Prepare(candidate, ConfigPath));
        Assert.Throws<InvalidOperationException>(() => AutomaticHookConfiguration.RequireInstallable(configured, ConfigPath));
    }

    [Fact]
    public void CustomPathsAreStableDistinctAndLocal()
    {
        var first = AutomaticHookConfiguration.Custom("copilot-cli", Path.Combine(root, "first"));
        var second = AutomaticHookConfiguration.Custom("copilot-cli", Path.Combine(root, "second"));
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.ScopeId, second.ScopeId);
        Assert.Equal(first.Id, AutomaticHookConfiguration.Custom("copilot-cli", first.HookDirectory.ToUpperInvariant()).Id);
        Assert.Throws<InvalidDataException>(() => AutomaticHookConfiguration.Custom("vscode", "relative"));
        Assert.Throws<InvalidDataException>(() => AutomaticHookConfiguration.Custom("vscode", @"\\server\profile"));
        Assert.Throws<InvalidDataException>(() => AutomaticHookConfiguration.Custom("unknown", root));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
