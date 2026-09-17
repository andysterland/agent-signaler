using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentSignaler.Contracts.Rpc.V1;

namespace AgentSignaler.RpcHost;

internal sealed class RpcConnection(WebSocket socket, IRpcApplication application, CancellationToken stopping)
{
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    private readonly CancellationTokenSource receiveCancellation = new();
    private Task<ValueWebSocketReceiveResult>? pendingReceive;
    private readonly Channel<Outgoing> outgoing = Channel.CreateBounded<Outgoing>(
        new BoundedChannelOptions(RpcProtocol.QueueMessages) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly object operationLock = new();
    private readonly Dictionary<RpcId, Operation> operations = [];
    private readonly HashSet<Operation> activeOperations = [];
    private readonly HashSet<RpcId> completed = [];
    private readonly Queue<RpcId> completionOrder = [];
    private readonly ConcurrentDictionary<long, Task> work = new();
    private readonly Dictionary<string, RpcPublication> invalidations = [];
    private readonly SemaphoreSlim applications = new(RpcProtocol.ApplicationSlots);
    private readonly SemaphoreSlim controls = new(RpcProtocol.ControlSlots);
    private long nextWork;
    private long queuedBytes;
    private int queuedMessages;
    private int closing;
    private int shutdownAccepted;
    private TaskCompletionSource? shutdownAcknowledgement;
    private WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure;
    public Task Drain { get; private set; } = Task.CompletedTask;

    public async Task RunAsync()
    {
        using var acknowledgementCancellation = lifetime.Token.Register(() =>
            Volatile.Read(ref shutdownAcknowledgement)?.TrySetCanceled(lifetime.Token));
        application.Published += OnPublished;
        var writer = WriteAsync();
        var publisher = PublishInvalidationsAsync();
        var closer = CloseWhenCancelledAsync(writer);
        try
        {
            while (!lifetime.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var bytes = await ReceiveAsync();
                if (bytes is null) break;
                RpcMessage message;
                try { message = RpcProtocol.Parse(bytes); }
                catch (RpcFault fault) { Enqueue(RpcProtocol.Response(default, null, fault)); continue; }
                var key = Interlocked.Increment(ref nextWork);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                work[key] = completion.Task;
                _ = ProcessTrackedAsync(key, completion, message);
            }
        }
        catch (DecoderFallbackException) { closeStatus = WebSocketCloseStatus.InvalidPayloadData; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (WebSocketException) { }
        catch (IOException) { }
        finally
        {
            application.Published -= OnPublished;
            ObserveCancellation(lifetime);
            Drain = Task.WhenAll(work.Values.ToArray());
            try { await Drain.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { application.ReportDrainTimeout(); }
            outgoing.Writer.TryComplete();
            try { await Task.WhenAll(writer, publisher, closer); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        }
    }

    private async Task<byte[]?> ReceiveAsync()
    {
        using var stream = new MemoryStream();
        var buffer = new byte[16384];
        using var incomplete = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var first = true;
        while (true)
        {
            if (lifetime.IsCancellationRequested) return null;
            ValueWebSocketReceiveResult result;
            try
            {
                pendingReceive = socket.ReceiveAsync(buffer.AsMemory(), receiveCancellation.Token).AsTask();
                result = await pendingReceive.WaitAsync(incomplete.Token);
            }
            catch (OperationCanceledException) when (!lifetime.IsCancellationRequested)
            {
                Close(WebSocketCloseStatus.PolicyViolation);
                return null;
            }

            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                Close(WebSocketCloseStatus.InvalidMessageType);
                return null;
            }
            if (first) { incomplete.CancelAfter(TimeSpan.FromSeconds(10)); first = false; }
            if (stream.Length + result.Count > RpcProtocol.InboundBytes)
            {
                Close(WebSocketCloseStatus.MessageTooBig);
                return null;
            }
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                var bytes = stream.ToArray();
                _ = RpcProtocol.Utf8.GetCharCount(bytes);
                return bytes;
            }
        }
    }

    private async Task CloseWhenCancelledAsync(Task writer)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token); }
        catch (OperationCanceledException) { }
        outgoing.Writer.TryComplete();
        await writer;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(closeStatus, "RPC connection closed", deadline.Token);
            if (pendingReceive is { IsCompleted: false } pending)
                await pending.WaitAsync(deadline.Token);
            if (socket.State is WebSocketState.CloseSent or WebSocketState.CloseReceived)
                await socket.CloseAsync(closeStatus, "RPC connection closed", deadline.Token);
        }
        catch (OperationCanceledException) { socket.Abort(); }
        catch (WebSocketException) { socket.Abort(); }
        finally
        {
            receiveCancellation.Cancel();
            if (pendingReceive is { } pending)
                _ = pending.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task ProcessTrackedAsync(long key, TaskCompletionSource completion, RpcMessage message)
    {
        try { await ProcessMessageAsync(message); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { Close(WebSocketCloseStatus.InternalServerError); }
        finally { completion.TrySetResult(); work.TryRemove(key, out _); }
    }

    private async Task ProcessMessageAsync(RpcMessage message)
    {
        var responseCount = message.Requests.Count(r => r.HasId);
        var budget = !message.IsBatch || responseCount == 0 ? RpcProtocol.OutboundBytes :
            (RpcProtocol.OutboundBytes - 1 - responseCount) / responseCount;
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replies = await Task.WhenAll(message.Requests.Select(request => ExecuteAsync(request, budget, acknowledgement)));
        var responses = replies.Where(r => r.Bytes is not null).Select(r => r.Bytes!).ToArray();
        if (responses.Length == 0) return;
        byte[] encoded;
        if (!message.IsBatch) encoded = responses[0];
        else
        {
            using var stream = new MemoryStream();
            stream.WriteByte((byte)'[');
            for (var i = 0; i < responses.Length; i++)
            {
                if (i != 0) stream.WriteByte((byte)',');
                stream.Write(responses[i]);
            }
            stream.WriteByte((byte)']');
            encoded = stream.ToArray();
        }
        var shutdown = replies.Any(r => r.Shutdown);
        var sent = shutdown ? acknowledgement : null;
        Enqueue(encoded, sent);
        if (sent is not null)
        {
            try { await sent.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token); }
            catch { sent.TrySetCanceled(); throw; }
            finally { application.RequestShutdown(); }
        }
    }

    private async Task<Reply> ExecuteAsync(RpcRequest request, int budget, TaskCompletionSource acknowledgement)
    {
        if (request.Fault is { } fault) return new(RpcProtocol.Response(request.Id, null, fault));
        RpcId.TryCreate(request.Id, out var id);
        var slots = RpcMethods.IsControl(request.Method) ? controls : applications;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(RpcMethods.Deadline(request.Method));
        var operation = new Operation(cancellation, request.Method, request.Parameters);
        var entered = false;
        var registered = false;
        var commitState = "notCommitted";
        try
        {
            if (request.HasId)
            {
                lock (operationLock)
                {
                    if (operations.ContainsKey(id)) throw new RpcFault(-32600);
                    operations.Add(id, operation);
                    completed.Remove(id);
                    registered = true;
                }
            }
            entered = slots.Wait(0);
            if (!entered) throw new RpcFault(1003, new(Retryable: true));
            if (Volatile.Read(ref shutdownAccepted) != 0 && !RpcMethods.IsControl(request.Method))
                throw new RpcFault(1003, new(Retryable: false));
            lock (operationLock) activeOperations.Add(operation);
            if (!RpcMethods.All.Contains(request.Method, StringComparer.Ordinal)) throw new RpcFault(-32601);
            if (request.Parameters.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object) &&
                !(request.Parameters.ValueKind == JsonValueKind.Array && request.Parameters.GetArrayLength() == 0))
                throw new RpcFault(-32602);
            object? result;
            if (request.Method == "operations.cancel") result = Cancel(request.Parameters);
            else if (request.Method == "system.getCapabilities")
            {
                RequireEmpty(request.Parameters);
                result = new RpcCapabilities(1, application.HostInstanceId.ToString("D"), RpcMethods.All,
                    RpcProtocol.Limits, "none", "domain-invalidation-refetch");
            }
            else if (request.Method == "system.shutdown")
            {
                RequireEmpty(request.Parameters);
                if (Interlocked.Exchange(ref shutdownAccepted, 1) != 0) throw new RpcFault(1003);
                Volatile.Write(ref shutdownAcknowledgement, acknowledgement);
                if (lifetime.IsCancellationRequested) acknowledgement.TrySetCanceled(lifetime.Token);
                application.AcceptShutdown(acknowledgement.Task);
                lock (operationLock)
                    foreach (var active in activeOperations.Where(o => o.Method is not ("system.shutdown" or "operations.cancel")))
                        RequestCancellation(active);
                result = new RpcShutdownResult("stopping");
                if (!request.HasId)
                {
                    acknowledgement.TrySetResult();
                    application.RequestShutdown();
                }
            }
            else
            {
                var (method, parameters) = PrepareDomainCancellation(request.Method, request.Parameters);
                var execution = await application.ExecuteAsync(method, parameters, cancellation.Token);
                result = execution.State;
                commitState = execution.CommitState;
            }
            if (!request.HasId) return new(null);
            return new(RpcProtocol.Response(request.Id, result, budget: budget), request.Method == "system.shutdown");
        }
        catch (RpcFault error)
        {
            if (error.Code == 1006 && cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested &&
                !IsCancellationRequested(id))
                error = new(1007, error.Data);
            return new(request.HasId ? RpcProtocol.Response(request.Id, null, error) : null);
        }
        catch (RpcResultLimitException)
        {
            return new(request.HasId ? RpcProtocol.Response(request.Id, null,
                new(1011, new(CommitState: commitState, Recovery: "refetchDomainOrUseSmallerPage"))) : null);
        }
        catch (OperationCanceledException)
        {
            var code = cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested &&
                !IsCancellationRequested(id) ? 1007 : 1006;
            return new(request.HasId ? RpcProtocol.Response(request.Id, null,
                new(code, new(CommitState: commitState))) : null);
        }
        catch (Exception)
        {
            return new(request.HasId ? RpcProtocol.Response(request.Id, null,
                new(-32603, new(CommitState: "unknown"))) : null);
        }
        finally
        {
            lock (operationLock) activeOperations.Remove(operation);
            if (entered) slots.Release();
            if (registered)
            {
                lock (operationLock)
                {
                    operations.Remove(id);
                    if (completed.Add(id)) completionOrder.Enqueue(id);
                    while (completionOrder.Count > 256) completed.Remove(completionOrder.Dequeue());
                }
            }
        }
    }

    private bool IsCancellationRequested(RpcId id)
    {
        lock (operationLock) return operations.TryGetValue(id, out var operation) && operation.CancelRequested;
    }

    private RpcCancelResult Cancel(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() != 1 ||
            !parameters.TryGetProperty("requestId", out var target) || !RpcId.TryCreate(target, out var id))
            throw new RpcFault(-32602);
        lock (operationLock)
        {
            if (!operations.TryGetValue(id, out var operation))
                return new(completed.Contains(id) ? "alreadyCompleted" : "notFound");
            RequestCancellation(operation);
            return new("cancelRequested");
        }
    }

    private (string Method, JsonElement Parameters) PrepareDomainCancellation(string method, JsonElement parameters)
    {
        if (method is not ("sharing.cancel" or "devboxes.cancel" or "prerequisites.cancel" or "windowsApp.cancel"))
            return (method, parameters);
        Guid? machine = null;
        string? prerequisite = null;
        if (method == "windowsApp.cancel") machine = new RpcParameters(parameters, "machineId").Guid("machineId");
        else if (method == "prerequisites.cancel")
        {
            prerequisite = new RpcParameters(parameters, "id").String("id");
            if (prerequisite is not (null or "azureCli" or "devCenterExtension" or "devTunnel" or "windowsApp"))
                throw new RpcFault(1001, new(Field: "id"));
        }
        else RequireEmpty(parameters);
        lock (operationLock)
        {
            foreach (var operation in activeOperations)
            {
                var match = method switch
                {
                    "sharing.cancel" => operation.Method is "sharing.start" or "sharing.stop" or "sharing.delete" or "sharing.logout",
                    "devboxes.cancel" => operation.Method == "devboxes.refresh",
                    "prerequisites.cancel" => operation.Method == "prerequisites.checkAll" ||
                        operation.Method == "prerequisites.check" && (prerequisite is null ||
                            MatchesString(operation.Parameters, "id", prerequisite)),
                    _ => operation.Method is "windowsApp.map" or "windowsApp.signIn" or "windowsApp.refresh" or
                        "windowsApp.open" or "windowsApp.openLastKnown" or "windowsApp.clear" &&
                        MatchesString(operation.Parameters, "machineId", machine!.Value.ToString("D"))
                };
                if (!match) continue;
                RequestCancellation(operation);
            }
        }
        return method switch
        {
            "sharing.cancel" => ("sharing.getStatus", default),
            "devboxes.cancel" => ("devboxes.getCatalog", default),
            "prerequisites.cancel" => ("prerequisites.getStatus", default),
            _ => ("windowsApp.getState", parameters)
        };
    }

    private static bool MatchesString(JsonElement value, string field, string expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var property) &&
        property.ValueKind == JsonValueKind.String && string.Equals(property.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    private static void RequestCancellation(Operation operation)
    {
        operation.CancelRequested = true;
        ObserveCancellation(operation.Cancellation);
    }

    private static void ObserveCancellation(CancellationTokenSource source) =>
        _ = source.CancelAsync().ContinueWith(task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal static void RequireEmpty(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Undefined ||
            parameters.ValueKind == JsonValueKind.Object && !parameters.EnumerateObject().Any() ||
            parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() == 0) return;
        throw new RpcFault(-32602);
    }

    private void OnPublished(object? sender, RpcPublication publication)
    {
        if (lifetime.IsCancellationRequested) return;
        if (publication.Invalidation is not { } invalidation)
        {
            Enqueue(RpcProtocol.Notification(publication.Method, publication.State));
            return;
        }
        lock (invalidations)
        {
            var key = invalidation.Domain + ":" + invalidation.MachineId;
            if (invalidations.TryGetValue(key, out var previous) &&
                ulong.Parse(previous.Invalidation!.Revision) >= ulong.Parse(invalidation.Revision)) return;
            if (!invalidations.ContainsKey(key) && invalidations.Count >= RpcProtocol.QueueMessages)
            {
                Close(WebSocketCloseStatus.PolicyViolation);
                return;
            }
            invalidations[key] = publication;
        }
    }

    private async Task PublishInvalidationsAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(lifetime.Token))
        {
            RpcPublication[] pending;
            lock (invalidations) { pending = invalidations.Values.ToArray(); invalidations.Clear(); }
            foreach (var item in pending) Enqueue(RpcProtocol.Notification(item.Method, item.State));
        }
    }

    private void Enqueue(byte[] bytes, TaskCompletionSource? sent = null)
    {
        if (bytes.Length > RpcProtocol.OutboundBytes)
        {
            sent?.TrySetCanceled();
            Close(WebSocketCloseStatus.PolicyViolation);
            return;
        }
        var totalBytes = Interlocked.Add(ref queuedBytes, bytes.Length);
        var totalMessages = Interlocked.Increment(ref queuedMessages);
        if (totalBytes > RpcProtocol.QueueBytes || totalMessages > RpcProtocol.QueueMessages)
        {
            Interlocked.Add(ref queuedBytes, -bytes.Length);
            Interlocked.Decrement(ref queuedMessages);
            sent?.TrySetCanceled();
            Close(WebSocketCloseStatus.PolicyViolation);
            return;
        }
        if (!outgoing.Writer.TryWrite(new(bytes, sent)))
        {
            Interlocked.Add(ref queuedBytes, -bytes.Length);
            Interlocked.Decrement(ref queuedMessages);
            sent?.TrySetCanceled();
            Close(WebSocketCloseStatus.PolicyViolation);
        }
    }

    private async Task WriteAsync()
    {
        try
        {
            await foreach (var message in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                if (lifetime.IsCancellationRequested)
                {
                    message.Sent?.TrySetCanceled();
                    Interlocked.Add(ref queuedBytes, -message.Bytes.Length);
                    Interlocked.Decrement(ref queuedMessages);
                    break;
                }
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    await socket.SendAsync(message.Bytes.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                    message.Sent?.TrySetResult();
                }
                catch
                {
                    message.Sent?.TrySetCanceled();
                    throw;
                }
                finally
                {
                    Interlocked.Add(ref queuedBytes, -message.Bytes.Length);
                    Interlocked.Decrement(ref queuedMessages);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { Close(WebSocketCloseStatus.PolicyViolation); }
        catch (WebSocketException) { Close(WebSocketCloseStatus.PolicyViolation); }
        catch (IOException) { Close(WebSocketCloseStatus.PolicyViolation); }
        catch (ObjectDisposedException) { Close(WebSocketCloseStatus.PolicyViolation); }
        finally
        {
            outgoing.Writer.TryComplete();
            while (outgoing.Reader.TryRead(out var pending))
            {
                pending.Sent?.TrySetCanceled();
                Interlocked.Add(ref queuedBytes, -pending.Bytes.Length);
                Interlocked.Decrement(ref queuedMessages);
            }
            Volatile.Read(ref shutdownAcknowledgement)?.TrySetCanceled();
        }
    }

    private void Close(WebSocketCloseStatus status)
    {
        if (Interlocked.Exchange(ref closing, 1) == 0) closeStatus = status;
        Volatile.Read(ref shutdownAcknowledgement)?.TrySetCanceled();
        ObserveCancellation(lifetime);
    }

    private sealed record Outgoing(byte[] Bytes, TaskCompletionSource? Sent);
    private sealed record Reply(byte[]? Bytes, bool Shutdown = false);
    private sealed class Operation(CancellationTokenSource cancellation, string method, JsonElement parameters)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public string Method { get; } = method;
        public JsonElement Parameters { get; } = parameters;
        public bool CancelRequested { get; set; }
    }
}
