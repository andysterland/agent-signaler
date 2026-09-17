using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class TranscriptReceiverPolicyTests
{
    [Theory]
    [InlineData((int)DashboardConnectionMode.DevTunnel, true, 51820, true, false, false, true)]
    [InlineData((int)DashboardConnectionMode.Lan, true, 51820, true, false, false, false)]
    [InlineData((int)DashboardConnectionMode.DevTunnel, false, 51820, true, false, false, false)]
    [InlineData((int)DashboardConnectionMode.DevTunnel, true, 51821, true, false, false, false)]
    [InlineData((int)DashboardConnectionMode.DevTunnel, true, 51820, false, false, false, false)]
    [InlineData((int)DashboardConnectionMode.DevTunnel, true, 51820, true, true, false, false)]
    [InlineData((int)DashboardConnectionMode.DevTunnel, true, 51820, true, false, true, false)]
    public void RequiresExactOwnedRunningInternetListener(int mode, bool running,
        int tunnelPort, bool connected, bool exiting, bool changed, bool expected) =>
        Assert.Equal(expected, TranscriptReceiverPolicy.IsReady((DashboardConnectionMode)mode, running, 51820,
            tunnelPort, connected, exiting, changed));
}
