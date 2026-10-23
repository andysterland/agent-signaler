namespace AgentSignaler.Configurator;

internal sealed record ConfiguratorEditState(string Url, double Heartbeat, int Mode, bool ShareDetails,
    string Relay, bool Startup, string DnsRecordName)
{
    internal ConfiguratorEditState WithSavedConnection(ConfiguratorEditState current) =>
        current with { Relay = Relay };
}
