using AgentSignaler.Configurator;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorEditStateTests
{
    private static ConfiguratorEditState Saved => new("https://previous.test", 5, 0, true,
        @"C:\Remote\AgentSignaler.Relay.exe", false, "previous.example.test");

    [Fact]
    public void SettingsSaveAcceptsConnectionAndStartupChangesWithoutAcceptingRelayEdits()
    {
        var current = new ConfiguratorEditState("", 10, 1, false,
            @"C:\Unsaved\AgentSignaler.Relay.exe", true, "signaler.cablesorter.io");

        var saved = Saved.WithSavedConnection(current);

        Assert.Equal(current with { Relay = Saved.Relay }, saved);
        Assert.NotEqual(current, saved);
        Assert.Equal(Saved.Relay, saved.Relay);
    }

    [Fact]
    public void SettingsSaveLeavesNoPendingConnectionEditsWhenRelayIsUnchanged()
    {
        var current = Saved with { Heartbeat = 10, ShareDetails = false, Startup = true };

        Assert.Equal(current, Saved.WithSavedConnection(current));
    }

    [Fact]
    public void NoOpSettingsSavePreservesBaseline()
    {
        Assert.Equal(Saved, Saved.WithSavedConnection(Saved));
    }
}
