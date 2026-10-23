namespace AgentSignaler.Remote;

public sealed record IntegrationRuntimeState(bool Running, string? EffectiveRevision = null, string? Message = null,
    int? HeartbeatIntervalSeconds = null, long? EffectiveCredentialRevision = null)
{
    /// <summary>Last observed Client reporting state, not a setup test or endpoint authentication result.</summary>
    public string? State { get; init; }
    /// <summary>Observed persistent Client resolver state; never a setup-test result or authenticated endpoint claim.</summary>
    public string? DiscoveryStatus { get; init; }
}

public interface IIntegrationRuntime
{
    Task<IntegrationRuntimeState> QueryAsync(string configPath, CancellationToken token);
    Task<IntegrationRuntimeState> QueryDiscoveryStatusAsync(string configPath, CancellationToken token) =>
        QueryAsync(configPath, token);
    Task<IntegrationRuntimeState> ReloadAsync(string configPath, CancellationToken token);
    Task<IntegrationRuntimeState> StartAsync(string clientPath, string configPath, CancellationToken token);
    Task StopAsync(string configPath, CancellationToken token);
    Task SuspendTranscriptAsync(string configPath, CancellationToken token) =>
        throw new InvalidOperationException("Transcript suspension requires an updated Client runtime.");
    Task PauseTranscriptAsync(string configPath, CancellationToken token) =>
        throw new InvalidOperationException("Transcript pause requires an updated Client runtime.");
    Task ReloadTranscriptAsync(string configPath, CancellationToken token) =>
        throw new InvalidOperationException("Transcript settings require an updated Client runtime.");
}

public sealed record IntegrationApplyResult(bool SettingsCommitted, bool RuntimeApplied, string Message,
    string? EffectiveRevision = null);
