namespace AgentSignaler.Integration.Tests;

internal sealed class LiveTunnelFactAttribute : FactAttribute
{
    public LiveTunnelFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SIGNALER_LIVE_TUNNEL_TEST") != "1")
            Skip = "Opt-in cloud test: requires approved anonymous exposure and an already signed-in qualified devtunnel CLI.";
    }
}
