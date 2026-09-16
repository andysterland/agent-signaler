namespace AgentSignaler.Configurator;

internal readonly record struct ConfiguratorActionState(
    bool Busy, bool DiscoveryActive, bool ConnectionTestActive)
{
    internal bool CanUseConnection => !Busy;
    internal bool CanChangeIntegration => !Busy;
    internal bool CanApply => !Busy;
    internal bool CanCancelDiscovery => DiscoveryActive;
    internal bool CanCancelTest => ConnectionTestActive;
}
