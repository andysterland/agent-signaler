using AgentSignaler.Configurator;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class HookTargetRowTests
{
    [Fact]
    public void CustomLocationStartsUncheckedAndEditsNotifyThePreview()
    {
        var row = new HookTargetRow();
        Assert.True(row.IsDraft);
        Assert.False(row.Enabled);
        Assert.Empty(row.Location);
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        row.Kind = "vscode";
        row.Location = Path.Combine(AppContext.BaseDirectory, "custom-profile");
        row.Enabled = true;
        row.Enabled = true;
        Assert.Equal(new[] { "Kind", "Location", "Enabled", "HasPendingChange", "Status" }, changes);
        Assert.True(row.HasPendingChange);
        Assert.Equal("Will enable on Apply settings", row.Status);
        var target = row.Prepare(Path.Combine(AppContext.BaseDirectory, "absent-config", "remote.json"));
        Assert.True(target.IsCustom);
        Assert.Equal(IntegrationCapability.Configured, target.Capability);
        Assert.Equal(Path.Combine(row.Location, "settings.json"), target.SettingsPath);
    }

    [Fact]
    public void SavedSelectionAndPathAreRepresentedWithoutWrites()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "profile");
        var target = AutomaticHookConfiguration.Custom("vscode", path);
        var row = new HookTargetRow(target, enabled: true);
        Assert.False(row.IsDraft);
        Assert.True(row.Enabled);
        Assert.Same(target, row.Target);
        Assert.Equal(path, row.Location);
        Assert.Equal("Ready to configure", row.Status);
        row.Enabled = false;
        Assert.True(row.HasPendingChange);
        Assert.Equal("Will disable on Apply settings", row.Status);
        Assert.Equal(IntegrationCapability.VerificationRequired, target.Capability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevertingSelectionClearsPendingChangeWithoutChangingTarget(bool enabled)
    {
        var target = AutomaticHookConfiguration.Custom("copilot-cli", Path.Combine(AppContext.BaseDirectory, "hooks"));
        var row = new HookTargetRow(target, enabled);
        var initialStatus = row.Status;
        Assert.False(row.HasPendingChange);

        row.Enabled = !enabled;
        Assert.True(row.HasPendingChange);
        Assert.Equal(enabled ? "Will disable on Apply settings" : "Will enable on Apply settings", row.Status);

        row.Enabled = enabled;
        Assert.False(row.HasPendingChange);
        Assert.Equal(initialStatus, row.Status);
        Assert.Equal(IntegrationCapability.VerificationRequired, target.Capability);
    }
}
