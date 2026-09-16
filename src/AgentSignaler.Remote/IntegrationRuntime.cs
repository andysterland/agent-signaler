namespace AgentSignaler.Remote;

public sealed record IntegrationRuntimeState(bool Running, string? EffectiveRevision = null, string? Message = null,
    int? HeartbeatIntervalSeconds = null);

public interface IIntegrationRuntime
{
    Task<IntegrationRuntimeState> QueryAsync(string configPath, CancellationToken token);
    Task<IntegrationRuntimeState> ReloadAsync(string configPath, CancellationToken token);
    Task<IntegrationRuntimeState> StartAsync(string clientPath, string configPath, CancellationToken token);
    Task StopAsync(string configPath, CancellationToken token);
}

public sealed record IntegrationApplyResult(bool SettingsCommitted, bool RuntimeApplied, string Message,
    string? EffectiveRevision = null);
