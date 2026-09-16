namespace AgentSignaler.Tunneling;

public enum TunnelState
{
    Stopped, CheckingAccount, AccountRequired, Creating, Starting, Verifying,
    Connected, Stopping, Faulted, Unsupported, Reconnecting
}

public sealed record TunnelStatus(TunnelState State, string Message, Uri? PublicUrl = null)
{
    public bool CanCopy => State == TunnelState.Connected && PublicUrl is not null;
}

public sealed record TunnelIdentity(
    string OwnerHash, string InstallationMarker, string? TunnelId = null, string? PendingTunnelId = null);

public sealed record TunnelOptions(string CliPath, int Port, TunnelIdentity Identity)
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HealthCheckInterval { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan HealthCheckTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record CliCommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface ITunnelProcessRunner
{
    Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken);
    Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
        Action<string> outputLine, CancellationToken cancellationToken);
}

public interface ITunnelHostProcess : IAsyncDisposable
{
    Task<int> Completion { get; }
}

public interface ITunnelHealthProbe
{
    Task VerifyAsync(Uri baseUri, CancellationToken cancellationToken);
}

public sealed class TunnelException : Exception
{
    public TunnelState State { get; }
    internal CliFailureKind FailureKind { get; init; }
    internal int? ErrorCode { get; init; }
    public TunnelException(string message, TunnelState state = TunnelState.Faulted) : base(message) => State = state;
}

internal enum CliFailureKind
{
    None, InvalidPath, MissingPath, InaccessiblePath, UntrustedSignature, WrongPublisher,
    CommandTimeout, OutputLimit, CommandFailed, UnsupportedPlatform
}
