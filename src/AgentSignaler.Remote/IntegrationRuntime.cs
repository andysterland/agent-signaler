namespace AgentSignaler.Remote;

public sealed record IntegrationRuntimeState(bool Running, string? EffectiveRevision = null, string? Message = null,
    int? HeartbeatIntervalSeconds = null);

public interface IIntegrationRuntime
{
    Task<IntegrationRuntimeState> QueryAsync(string configPath, CancellationToken token);
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
