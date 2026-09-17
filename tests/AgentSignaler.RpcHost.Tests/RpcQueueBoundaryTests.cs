using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentSignaler.RpcHost;
using Xunit;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcQueueBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrTimedOutShutdownSendAlwaysCompletesAcknowledgement(bool failSend)
    {
        var application = new WaitingApplication();
        using var socket = new BlockedSocket(
            "{\"jsonrpc\":\"2.0\",\"method\":\"system.shutdown\",\"id\":\"stop\"}", failSend: failSend);
        var connection = new RpcConnection(socket, application, CancellationToken.None);
        var running = connection.RunAsync();
        try
        {
            var acknowledgement = await application.ShutdownAccepted.Task.WaitAsync(TestContext.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acknowledgement.WaitAsync(TimeSpan.FromSeconds(14)));
            Assert.True(acknowledgement.IsCanceled);
            await running.WaitAsync(TestContext.Token);
        }
        finally
        {
            socket.ReleaseSend.TrySetResult();
            socket.Abort();
            await running.WaitAsync(TestContext.Token);
        }
    }

    [Fact]
    public async Task DisconnectCompletesUnsentBatchAcknowledgementBeforeSlowMemberDrain()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var application = new WaitingApplication(async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await cleanup.Task;
                throw;
            }
            return new(null);
        });
        using var socket = new BlockedSocket(
            "[{\"jsonrpc\":\"2.0\",\"method\":\"devboxes.refresh\",\"id\":\"work\"}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"system.shutdown\",\"id\":\"stop\"}]");
        var connection = new RpcConnection(socket, application, CancellationToken.None);
        var running = connection.RunAsync();
        try
        {
            var acknowledgement = await application.ShutdownAccepted.Task.WaitAsync(TestContext.Token);
            await cancelled.Task.WaitAsync(TestContext.Token);
            Assert.False(acknowledgement.IsCompleted);
            socket.Abort();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acknowledgement.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(cleanup.Task.IsCompleted);
        }
        finally
        {
            cleanup.TrySetResult();
            socket.ReleaseSend.TrySetResult();
            socket.Abort();
            await running.WaitAsync(TestContext.Token);
        }
    }

    [Fact]
    public async Task ClosureCompletesQueuedShutdownAcknowledgementBehindBlockedSend()
    {
        var application = new WaitingApplication((_, _, _) => Task.FromResult(new RpcExecutionResult(null)));
        using var socket = new BlockedSocket("{\"jsonrpc\":\"2.0\",\"method\":\"machines.list\",\"id\":\"read\"}");
        var connection = new RpcConnection(socket, application, CancellationToken.None);
        var running = connection.RunAsync();
        try
        {
            await socket.SendStarted.Task.WaitAsync(TestContext.Token);
            socket.Receive("{\"jsonrpc\":\"2.0\",\"method\":\"system.shutdown\",\"id\":\"stop\"}");
            var acknowledgement = await application.ShutdownAccepted.Task.WaitAsync(TestContext.Token);
            socket.Abort();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acknowledgement.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            socket.ReleaseSend.TrySetResult();
            socket.Abort();
            await running.WaitAsync(TestContext.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteSingleAndBatchResponsesAcceptExactFourMiBAndRejectOneByteOver(bool batch)
    {
        using var id = JsonDocument.Parse("0");
        var count = batch ? 3 : 1;
        var budget = batch ? (RpcProtocol.OutboundBytes - count - 1) / count : RpcProtocol.OutboundBytes;
        var overhead = RpcProtocol.Response(id.RootElement, "").Length;
        foreach (var overflow in new[] { false, true })
        {
            var payload = new string('a', budget - overhead);
            var calls = 0;
            var application = new WaitingApplication((_, parameters, _) =>
            {
                Interlocked.Increment(ref calls);
                var result = overflow && parameters.GetProperty("extra").GetBoolean() ? payload + "a" : payload;
                return Task.FromResult(new RpcExecutionResult(result, "committed"));
            });
            var requests = Enumerable.Range(0, count).Select(i =>
                "{\"jsonrpc\":\"2.0\",\"method\":\"machines.list\",\"id\":" + i +
                ",\"params\":{\"extra\":" + (i == 0 ? "true" : "false") + "}}");
            var request = batch ? "[" + string.Join(',', requests) + "]" : requests.Single();
            using var socket = new BlockedSocket(request, captureOutput: true);
            socket.ReleaseSend.TrySetResult();
            var connection = new RpcConnection(socket, application, CancellationToken.None);
            var running = connection.RunAsync();
            try
            {
                var frame = await socket.SentPayload.Task.WaitAsync(TestContext.Token);
                using var response = JsonDocument.Parse(frame);
                var entries = batch ? response.RootElement.EnumerateArray().ToArray() : [response.RootElement];
                Assert.Equal(count, entries.Length);
                Assert.Equal(count, Volatile.Read(ref calls));
                if (!overflow)
                {
                    Assert.Equal(RpcProtocol.OutboundBytes, frame.Length);
                    Assert.All(entries, entry => Assert.Equal(payload.Length, entry.GetProperty("result").GetString()!.Length));
                }
                else
                {
                    Assert.True(frame.Length < RpcProtocol.OutboundBytes);
                    Assert.Equal(1011, entries[0].GetProperty("error").GetProperty("code").GetInt32());
                    Assert.Equal("committed", entries[0].GetProperty("error").GetProperty("data").GetProperty("commitState").GetString());
                    Assert.All(entries.Skip(1), entry => Assert.Equal(payload.Length, entry.GetProperty("result").GetString()!.Length));
                }
            }
            finally
            {
                socket.Abort();
                await running.WaitAsync(TestContext.Token);
            }
        }
    }

    [Fact]
    public async Task BlockedSendUsesTenSecondDeadlineAndCancelsClientWork()
    {
        var app = new WaitingApplication();
        using var socket = new BlockedSocket();
        var connection = new RpcConnection(socket, app, CancellationToken.None);
        var running = connection.RunAsync();
        try
        {
            await app.Admitted.Task.WaitAsync(TestContext.Token);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            app.Publish("synthetic blocked send");
            await socket.SendStarted.Task.WaitAsync(TestContext.Token);
            await app.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await running.WaitAsync(TestContext.Token);
            Assert.InRange(watch.Elapsed.TotalSeconds, 9, 14);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        }
        finally
        {
            socket.ReleaseSend.TrySetResult();
            socket.Abort();
            await running.WaitAsync(TestContext.Token);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueEnforcesExactMessageAndEncodedByteLimitsIncludingCurrentSend(bool byteLimit)
    {
        var app = new WaitingApplication();
        using var socket = new BlockedSocket();
        var connection = new RpcConnection(socket, app, CancellationToken.None);
        var running = connection.RunAsync();
        await app.Admitted.Task.WaitAsync(TestContext.Token);
        var payload = byteLimit ? new string('a', RpcProtocol.OutboundBytes -
            RpcProtocol.Notification("system.problem", "").Length) : "synthetic terminal";
        var count = byteLimit ? 4 : 64;
        try
        {
            for (var i = 0; i < count; i++) app.Publish(payload);
            await socket.SendStarted.Task.WaitAsync(TestContext.Token);
            Assert.False(app.Cancelled.Task.IsCompleted);
            app.Publish("one above the exact bound");
            await app.Cancelled.Task.WaitAsync(TestContext.Token);
            socket.ReleaseSend.TrySetResult();
            await running.WaitAsync(TestContext.Token);
            Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        }
        finally
        {
            socket.ReleaseSend.TrySetResult();
            socket.Abort();
            await running.WaitAsync(TestContext.Token);
        }
    }

    private sealed class WaitingApplication(
        Func<string, JsonElement, CancellationToken, Task<RpcExecutionResult>>? execute = null) : IRpcApplication
    {
        public Guid HostInstanceId { get; } = Guid.NewGuid();
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Task> ShutdownAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<RpcPublication>? Published;
        public async Task<RpcExecutionResult> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
        {
            Admitted.TrySetResult();
            if (execute is not null) return await execute(method, parameters, cancellationToken);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return new(null);
        }
        public void Publish(string payload) => Published?.Invoke(this, new("system.problem", payload));
        public void RequestShutdown() { }
        public void AcceptShutdown(Task acknowledgement) => ShutdownAccepted.TrySetResult(acknowledgement);
        public void ReportDrainTimeout() { }
        public void ReportDrainCompleted() { }
    }

    private sealed class BlockedSocket : WebSocket
    {
        private readonly Channel<byte[]?> incoming = Channel.CreateUnbounded<byte[]?>();
        private WebSocketState state = WebSocketState.Open;
        private WebSocketCloseStatus? closeStatus;
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<byte[]> SentPayload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool captureOutput;
        private readonly bool failSend;
        public override WebSocketCloseStatus? CloseStatus => closeStatus;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => state;
        public override string? SubProtocol => null;

        public BlockedSocket(string? request = null, bool captureOutput = false, bool failSend = false)
        {
            this.captureOutput = captureOutput;
            this.failSend = failSend;
            incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(request ??
                "{\"jsonrpc\":\"2.0\",\"method\":\"devboxes.refresh\",\"id\":\"work\"}"));
        }
        public void Receive(string request) => incoming.Writer.TryWrite(Encoding.UTF8.GetBytes(request));
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var frame = await incoming.Reader.ReadAsync(cancellationToken);
            if (frame is null)
            {
                state = WebSocketState.Closed;
                return new(0, WebSocketMessageType.Close, true, closeStatus, null);
            }
            frame.CopyTo(buffer.AsSpan());
            return new(frame.Length, WebSocketMessageType.Text, true);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult();
            if (failSend) throw new WebSocketException();
            if (captureOutput && !SentPayload.Task.IsCompleted) SentPayload.TrySetResult(buffer.ToArray());
            await ReleaseSend.Task.WaitAsync(cancellationToken);
        }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            closeStatus = status;
            state = WebSocketState.CloseSent;
            incoming.Writer.TryWrite(null);
            return Task.CompletedTask;
        }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken cancellationToken)
        {
            closeStatus = status;
            state = WebSocketState.Closed;
            incoming.Writer.TryWrite(null);
            return Task.CompletedTask;
        }
        public override void Abort()
        {
            state = WebSocketState.Aborted;
            incoming.Writer.TryWrite(null);
        }
        public override void Dispose() => Abort();
    }
}
