using AgentSignaler.Configurator;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorActionStateTests
{
    public static TheoryData<bool, bool, bool> WorkflowStates
    {
        get
        {
            var data = new TheoryData<bool, bool, bool>();
            for (var bits = 0; bits < 8; bits++)
                data.Add((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WorkflowStates))]
    public void ActionsRespectBusyAndCancellationOwnership(bool busy, bool discovery, bool connectionTest)
    {
        var state = new ConfiguratorActionState(busy, discovery, connectionTest);
        Assert.Equal(!busy, state.CanUseConnection);
        Assert.Equal(!busy, state.CanChangeIntegration);
        Assert.Equal(!busy, state.CanApply);
        Assert.Equal(discovery, state.CanCancelDiscovery);
        Assert.Equal(connectionTest, state.CanCancelTest);
    }

    [Fact]
    public void ApplyDoesNotRequireAManualPreviewOrVerificationStep()
    {
        var ready = new ConfiguratorActionState(false, false, false);
        Assert.True(ready.CanApply);
        Assert.False((ready with { Busy = true }).CanApply);
        Assert.True((ready with { Busy = false }).CanApply);
    }
}
