using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed record ClientIpcRequest(int Version, string Command, AgentEvent? Event = null,
    HookData? Hook = null, string? ExpectedRevision = null, Guid? ProbeId = null);
public sealed record ClientIpcResponse(bool Accepted, string State, string? EffectiveRevision = null,
    int? HeartbeatIntervalSeconds = null, string? Error = null)
{
    [JsonRequired] public int Version { get; init; } = ClientIpc.Version;
}

public static class ClientIpc
{
    public const int Version = 2;
    public const int MaximumMessageBytes = 4096;
    private static readonly JsonSerializerOptions Json = new(Protocol.Json)
    {
        AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false
    };

    public static Task<ClientIpcResponse> StatusAsync(string configPath, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(Version, "status"), cancellationToken);
    public static Task<ClientIpcResponse> ActivateAsync(string configPath, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(Version, "activate"), cancellationToken);
    public static Task<ClientIpcResponse> ReloadAsync(string configPath, string expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(Version, "reload", ExpectedRevision: expectedRevision), cancellationToken);
    public static Task<ClientIpcResponse> StopAsync(string configPath, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(Version, "stop"), cancellationToken);
    public static Task<ClientIpcResponse> HookAsync(string configPath, AgentEvent kind, HookData hook, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(Version, "hook", kind, hook), cancellationToken);

    public static async Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.Command == "stop" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".", ClientIdentity.PipeName(configPath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(350, budget.Token);
            await WriteAsync(pipe, request, budget.Token);
            var response = await ReadAsync<ClientIpcResponse>(pipe, budget.Token);
            return response.Version == Version ? response : new(false, "incompatible");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
            JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, "unavailable", Error: "Client unavailable or incompatible. Start the client explicitly.");
        }
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is < 1 or > MaximumMessageBytes) throw new InvalidDataException("Invalid IPC length.");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Invalid IPC message.");
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaximumMessageBytes) throw new InvalidDataException("IPC message too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

public sealed class ClientIpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _listeners;
    private readonly DiagnosticLog _log;
    private readonly object _diagnosticSync = new();
    private long? _lastDiagnostic;
    public event Action? StopAcknowledged;

    public ClientIpcServer(string configPath, Func<ClientIpcRequest, CancellationToken, Task<ClientIpcResponse>> handler)
    {
        var name = ClientIdentity.PipeName(configPath);
        _log = new DiagnosticLog(RemotePaths.Log(configPath));
        // Fixed concurrency bounds both slow/malicious callers and accepted local work.
        var pipes = new List<NamedPipeServerStream>();
        try
        {
            for (var i = 0; i < 4; i++) pipes.Add(CreatePipe(name));
            _listeners = pipes.Select(pipe => ListenAsync(name, handler, pipe)).ToArray();
        }
        catch
        {
            WriteDiagnostic("ipc-initialization-failed");
            foreach (var pipe in pipes) pipe.Dispose();
            _lifetime.Dispose();
            throw;
        }
    }

    private static NamedPipeServerStream CreatePipe(string name) => new(name, PipeDirection.InOut, 4,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    private async Task ListenAsync(string name, Func<ClientIpcRequest, CancellationToken, Task<ClientIpcResponse>> handler,
        NamedPipeServerStream? initial)
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var responseProduced = false;
            try
            {
                using var pipe = initial ?? CreatePipe(name);
                initial = null;
                await pipe.WaitForConnectionAsync(_lifetime.Token);
                using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                readBudget.CancelAfter(TimeSpan.FromSeconds(1));
                var request = await ClientIpc.ReadAsync<ClientIpcRequest>(pipe, readBudget.Token);
                using var workBudget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                workBudget.CancelAfter(TimeSpan.FromSeconds(5));
                var response = request.Version == ClientIpc.Version
                    ? await handler(request, workBudget.Token)
                    : new ClientIpcResponse(false, "incompatible", Error: "Unsupported IPC version.");
                responseProduced = true;
                if (request.Version != ClientIpc.Version) WriteDiagnostic("ipc-version-rejected");
                await ClientIpc.WriteAsync(pipe, response, workBudget.Token);
                if (request.Command == "stop" && response.Accepted) StopAcknowledged?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or
                InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                if (!_lifetime.IsCancellationRequested || responseProduced)
                {
                    WriteDiagnostic(ex switch
                    {
                        UnauthorizedAccessException => "ipc-access-denied",
                        OperationCanceledException when !responseProduced => "ipc-request-timeout",
                        JsonException or InvalidDataException or ArgumentException => "ipc-message-invalid",
                        _ => "ipc-connection-failed"
                    });
                    try { await Task.Delay(50, _lifetime.Token); } catch (OperationCanceledException) { }
                }
            }
        }
    }

    private void WriteDiagnostic(string code)
    {
        lock (_diagnosticSync)
        {
            var now = TimeProvider.System.GetTimestamp();
            if (_lastDiagnostic is { } previous &&
                TimeProvider.System.GetElapsedTime(previous, now) < TimeSpan.FromMinutes(1)) return;
            _lastDiagnostic = now;
            _log.Write(code);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await Task.WhenAll(_listeners);
        _lifetime.Dispose();
    }
}
