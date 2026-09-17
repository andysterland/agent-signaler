using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class MachineNavigationTests
{
    [Fact]
    public void LocalRoleUsesReportedHostnameNotDisplayNameAndPreservesMachineData()
    {
        var local = Machine(Environment.MachineName.ToLowerInvariant(), "Custom name");
        var remote = Machine(Environment.MachineName + "-remote", "local");
        Assert.True(MachineNavigation.IsLocal(local));
        Assert.Equal("local", MachineNavigation.Name(local));
        Assert.Equal("Custom name", local.DisplayName);
        Assert.False(MachineNavigation.IsLocal(remote));
    }

    [Fact]
    public void BothViewShapesPutLocalFirstAndKeepRemoteNamesAlphabetical()
    {
        var local = Machine(Environment.MachineName, "ZZZ");
        var alpha = Machine("remote-a", "AAA");
        var beta = Machine("remote-b", "BBB");
        MachineView[] machines = [beta, local, alpha];
        Assert.Equal(new[] { local, alpha, beta }, MachineNavigation.Order(machines, m => m));
        var cards = machines.Select(machine => new { Machine = machine }).ToArray();
        Assert.Equal(new[] { local, alpha, beta },
            MachineNavigation.Order(cards, card => card.Machine).Select(card => card.Machine));
        Assert.Equal(new[] { alpha, beta }, MachineNavigation.Order(new[] { beta, alpha }, m => m));
    }

    private static MachineView Machine(string hostname, string displayName) =>
        new(Guid.NewGuid(), hostname, displayName, "copilot-cli", "1.0", AgentState.Offline,
            null, DateTimeOffset.UtcNow, []);
}
