using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSignaler.Contracts;

namespace AgentSignaler.Remote;

public sealed record ClientIpcRequest(int Version, string Command, AgentEvent? Event = null,
    HookData? Hook = null, string? ExpectedRevision = null, Guid? ProbeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TranscriptHookObservation? Transcript = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SourceDescriptor? TranscriptSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LocalTranscriptReference? TranscriptRead = null);
public sealed record ClientIpcResponse(bool Accepted, string State, string? EffectiveRevision = null,
    int? HeartbeatIntervalSeconds = null, string? Error = null)
{
    [JsonRequired] public int Version { get; init; } = ClientIpc.Version;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TranscriptNegotiation? Transcript { get; init; }
}

public static class ClientIpc
{
    public const int Version = 3;
    public const int LegacyVersion = 2;
    public const int MaximumMessageBytes = 4096;
    public const int MaximumFrameBytes = 32768;
    public static readonly JsonSerializerOptions Json = new(Protocol.Json)
    {
        AllowDuplicateProperties = false, PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true
    };

    public static Task<ClientIpcResponse> StatusAsync(string configPath, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(LegacyVersion, "status"), cancellationToken);
    public static Task<ClientIpcResponse> ActivateAsync(string configPath, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(LegacyVersion, "activate"), cancellationToken);
    public static Task<ClientIpcResponse> ReloadAsync(string configPath, string expectedRevision, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(LegacyVersion, "reload", ExpectedRevision: expectedRevision), cancellationToken);
    public static Task<ClientIpcResponse> StopAsync(string configPath, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(LegacyVersion, "stop"), cancellationToken);
    public static Task<ClientIpcResponse> HookAsync(string configPath, AgentEvent kind, HookData hook, CancellationToken cancellationToken = default) =>
        SendAsync(configPath, new(LegacyVersion, "hook", kind, hook), cancellationToken);

    public static async Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request,
        CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.Command == "stop" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(2));
        try
        {
            using var pipe = new NamedPipeClientStream(".", ClientIdentity.PipeName(configPath), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(request.Command.StartsWith("transcript-", StringComparison.Ordinal) ? 50 : 350, budget.Token);
            await WriteAsync(pipe, request, budget.Token);
            var response = await ReadAsync<ClientIpcResponse>(pipe, budget.Token);
            return response.Version == request.Version ? response : new(false, "incompatible");
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or
            JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new(false, "unavailable", Error: "Client unavailable or incompatible. Start the client explicitly.");
        }
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        using var frame = await ReadFrameAsync<T>(stream, null, cancellationToken);
        return frame.Value;
    }

    internal static async Task<IpcFrame<T>> ReadFrameAsync<T>(Stream stream, TranscriptScratchBudget? scratch,
        CancellationToken cancellationToken, SemaphoreSlim? contentLimiter = null)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is < 1 or > MaximumFrameBytes) throw new InvalidDataException("Invalid IPC length.");
        var reservation = scratch?.TryReserve(checked(count * 4 + 4096));
        if (scratch is not null && reservation is null) throw new InvalidDataException("IPC scratch capacity.");
        var contentReserved = false;
        try
        {
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            var content = ValidateFrame<T>(bytes);
            if (content && contentLimiter is not null)
            {
                contentReserved = contentLimiter.Wait(0);
                if (!contentReserved) throw new InvalidDataException("IPC content capacity.");
            }
            return new(JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Invalid IPC message."),
                reservation, contentReserved ? contentLimiter : null);
        }
        catch
        {
            if (contentReserved) contentLimiter!.Release();
            reservation?.Dispose();
            throw;
        }
    }

    internal sealed class IpcFrame<T>(T value, IDisposable? reservation, SemaphoreSlim? contentLimiter) : IDisposable
    {
        public T Value { get; } = value;
        public void Dispose()
        {
            reservation?.Dispose();
            contentLimiter?.Release();
        }
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaximumFrameBytes) throw new InvalidDataException("IPC message too large.");
        ValidateFrame<T>(bytes);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static bool ValidateFrame<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            MaxDepth = 12, AllowDuplicateProperties = false
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var value))
            throw new InvalidDataException("Invalid IPC version.");
        var limit = MaximumMessageBytes;
        var content = false;
        if (typeof(T) == typeof(ClientIpcRequest))
        {
            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid IPC command.");
            var name = command.GetString();
            if (value == Version && name == "transcript-hook") limit = MaximumFrameBytes;
            content = value == Version && name is "transcript-hook" or "transcript-read";
            if (value == LegacyVersion && root.EnumerateObject().Any(p =>
                p.Name is not ("version" or "command" or "event" or "hook" or "expectedRevision" or "probeId")))
                throw new InvalidDataException("Invalid legacy IPC field.");
            if (value != Version && name?.StartsWith("transcript-", StringComparison.Ordinal) == true)
                throw new InvalidDataException("Invalid IPC command version.");
        }
        if (bytes.Length > limit) throw new InvalidDataException("IPC message too large.");
        return content;
    }
}

public sealed class ClientIpcServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task[] _listeners;
    private readonly DiagnosticLog _log;
    private readonly object _diagnosticSync = new();
    private readonly SemaphoreSlim _content = new(2, 2);
    private readonly TranscriptScratchBudget _scratch;
    private long? _lastDiagnostic;
    public event Action? StopAcknowledged;

    public ClientIpcServer(string configPath, Func<ClientIpcRequest, CancellationToken, Task<ClientIpcResponse>> handler,
        TranscriptScratchBudget? scratch = null)
    {
        var name = ClientIdentity.PipeName(configPath);
        _scratch = scratch ?? new();
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
                using var frame = await ClientIpc.ReadFrameAsync<ClientIpcRequest>(pipe, _scratch, readBudget.Token, _content);
                var request = frame.Value;
                using var workBudget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                workBudget.CancelAfter(TimeSpan.FromSeconds(5));
                var supported = request.Version is ClientIpc.Version or ClientIpc.LegacyVersion;
                var response = !supported
                    ? new ClientIpcResponse(false, "incompatible", Error: "Unsupported IPC version.")
                    : await handler(request, workBudget.Token);
                response = response with
                {
                    Version = supported ? request.Version : ClientIpc.Version,
                    Transcript = request.Version == ClientIpc.LegacyVersion ? null : response.Transcript
                };
                responseProduced = true;
                if (!supported) WriteDiagnostic("ipc-version-rejected");
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
        _content.Dispose();
        _lifetime.Dispose();
    }
}
