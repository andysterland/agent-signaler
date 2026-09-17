using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts.Rpc.V1;
using AgentSignaler.RpcHost;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcTransportTests
{
    [Fact]
    public async Task ActualIpv6LoopbackControllerUsesTheSameOriginAndHostPolicy()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "https://[::1]:3000");
        await socket.ConnectAsync(new($"ws://[::1]:{fixture.Port}/rpc"), TestContext.Token);
        await SendAsync(socket, Request("system.getCapabilities", "\"ipv6\""));
        Assert.Contains("\"ipv6\"", await ReceiveTextAsync(socket));
    }

    [Fact]
    public async Task KeepalivePongsPreserveHealthyIdleControllerBeyondTimeout()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var received = socket.ReceiveAsync(new byte[8192].AsMemory(), deadline.Token).AsTask();
        await Task.Delay(TimeSpan.FromSeconds(95), deadline.Token);
        Assert.False(received.IsCompleted);
        await SendAsync(socket, Request("system.getCapabilities", "\"healthy-idle\""));
        var reply = await received.WaitAsync(deadline.Token);
        Assert.Equal(WebSocketMessageType.Text, reply.MessageType);
        Assert.True(reply.EndOfMessage);
        Assert.Equal(WebSocketState.Open, socket.State);
    }

    [Fact]
    public async Task RealWebSocketAcceptsFragmentedTextMixedBatchesAndExactIds()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var bytes = Encoding.UTF8.GetBytes("[{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\",\"id\":9007199254740993.00}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\"},1]");
        await socket.SendAsync(bytes.AsMemory(0, 41), WebSocketMessageType.Text, false, TestContext.Token);
        await socket.SendAsync(bytes.AsMemory(41), WebSocketMessageType.Text, true, TestContext.Token);
        var response = await ReceiveTextAsync(socket);
        using var parsed = JsonDocument.Parse(response);
        Assert.Equal(2, parsed.RootElement.GetArrayLength());
        Assert.Contains("\"id\":9007199254740993.00", response);
        Assert.Equal(2, fixture.Application.Calls);
        Assert.Equal(-32600, parsed.RootElement[1].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ValidNotificationsNeverReplyIncludingErrors()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, "[{\"jsonrpc\":\"2.0\",\"method\":\"unknown\"}," +
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.get\",\"params\":[1]}]");
        await SendAsync(socket, Request("system.getCapabilities", "\"barrier\""));
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("barrier", response.RootElement.GetProperty("id").GetString());
        Assert.Equal(0, fixture.Application.Calls);
    }

    [Fact]
    public async Task ActiveEquivalentNumericIdIsRejectedWithoutExecutingDuplicate()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            admitted.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(new RpcCancelResult("completed"));
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, Request("settings.get", "1.00"));
        await admitted.Task.WaitAsync(TestContext.Token);
        await SendAsync(socket, Request("settings.get", "1e0"));
        using (var duplicate = JsonDocument.Parse(await ReceiveTextAsync(socket)))
        {
            Assert.Equal(-32600, duplicate.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("1e0", duplicate.RootElement.GetProperty("id").GetRawText());
        }
        release.TrySetResult();
        Assert.Contains("\"id\":1.00", await ReceiveTextAsync(socket));
        Assert.Equal(1, fixture.Application.Calls);
    }

    [Fact]
    public async Task ReservedCancellationAdmissionWorksWhileAllApplicationSlotsAreOccupied()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref count) == 32) admitted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, "[" + string.Join(',', Enumerable.Range(0, 32).Select(i =>
            Request("devboxes.refresh", $"\"work{i}\""))) + "]");
        await admitted.Task.WaitAsync(TestContext.Token);
        await SendAsync(socket, Request("settings.get", "\"overflow\""));
        using (var busy = JsonDocument.Parse(await ReceiveTextAsync(socket)))
            Assert.Equal(1003, busy.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        await SendAsync(socket, Request("operations.cancel", "\"cancel\"", "{\"requestId\":\"work0\"}"));
        using var cancelled = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.Equal("cancelRequested", cancelled.RootElement.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(32, fixture.Application.Calls);
    }

    [Fact]
    public async Task FourReservedControlSlotsRejectTheFifthWithoutBlockingOrdinaryQueries()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        await using var fixture = await Fixture.StartAsync(async (method, _, token) =>
        {
            if (method != "sharing.stop") return new(new RpcCancelResult("complete"));
            if (Interlocked.Increment(ref count) == 4) admitted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, "[" + string.Join(',', Enumerable.Range(0, 4).Select(i => Request("sharing.stop", $"\"stop{i}\""))) + "]");
        await admitted.Task.WaitAsync(TestContext.Token);
        await SendAsync(socket, Request("operations.cancel", "\"overflow\"", "{\"requestId\":\"stop0\"}"));
        using (var response = JsonDocument.Parse(await ReceiveTextAsync(socket)))
            Assert.Equal(1003, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        await SendAsync(socket, Request("settings.get", "\"query\""));
        using var query = JsonDocument.Parse(await ReceiveTextAsync(socket));
        Assert.True(query.RootElement.TryGetProperty("result", out _));
        Assert.Equal(4, count);
    }

    [Fact]
    public async Task LocalCommandDeadlineIsTenSecondsAndReportsTimeoutRatherThanCancellation()
    {
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await SendAsync(socket, Request("settings.get", "\"deadline\""));
        using var reply = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(15)));
        Assert.Equal(1007, reply.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.InRange(watch.Elapsed.TotalSeconds, 9, 14);
    }

    [Fact]
    public async Task BatchOutputBudgetReturnsResourceErrorAndDoesNotRepeatCommittedMutation()
    {
        await using var fixture = await Fixture.StartAsync((_, _, _) =>
            Task.FromResult(new RpcExecutionResult(new RpcNoteChunk(new string('n', 200000), 0, null, 200000), "committed")));
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, "[" + string.Join(',', Enumerable.Range(0, 32).Select(i =>
            Request("settings.update", $"\"{i}\""))) + "]");
        var text = await ReceiveTextAsync(socket);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= RpcProtocol.OutboundBytes);
        using var response = JsonDocument.Parse(text);
        Assert.Equal(32, response.RootElement.GetArrayLength());
        foreach (var entry in response.RootElement.EnumerateArray())
        {
            Assert.Equal(1011, entry.GetProperty("error").GetProperty("code").GetInt32());
            Assert.Equal("committed", entry.GetProperty("error").GetProperty("data").GetProperty("commitState").GetString());
        }
        Assert.Equal(32, fixture.Application.Calls);
    }

    [Fact]
    public async Task CompetingAndDrainingControllersAreRejectedUntilCleanupFinishes()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            admitted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); await cleanup.Task; throw; }
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        using (var competing = new ClientWebSocket())
        {
            competing.Options.SetRequestHeader("Origin", "http://localhost:3000");
            var error = await Assert.ThrowsAsync<WebSocketException>(() => competing.ConnectAsync(fixture.RpcUri, TestContext.Token));
            Assert.Contains("409", error.Message);
        }
        await SendAsync(socket, Request("devboxes.refresh", "\"long\""));
        await admitted.Task.WaitAsync(TestContext.Token);
        socket.Abort();
        await cancelled.Task.WaitAsync(TestContext.Token);
        using (var draining = new ClientWebSocket())
        {
            draining.Options.SetRequestHeader("Origin", "http://localhost:3000");
            var error = await Assert.ThrowsAsync<WebSocketException>(() => draining.ConnectAsync(fixture.RpcUri, TestContext.Token));
            Assert.Contains("409", error.Message);
        }
        cleanup.TrySetResult();
        using var replacement = await fixture.ConnectEventuallyAsync();
        await SendAsync(replacement, Request("operations.cancel", "\"cancel\"", "{\"requestId\":\"long\"}"));
        using var response = JsonDocument.Parse(await ReceiveTextAsync(replacement));
        Assert.Equal("notFound", response.RootElement.GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvalidationsAreSubscribedBeforeQueriesCoalescedAndBoundedLatency()
    {
        var instance = Guid.NewGuid();
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            queried.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(new RpcSnapshot<RpcCancelResult>(1, instance.ToString("D"), "machines", "1", new("complete")));
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, Request("machines.list", "\"query\""));
        await queried.Task.WaitAsync(TestContext.Token);
        var latency = System.Diagnostics.Stopwatch.StartNew();
        fixture.Application.Invalidate("machines", "3");
        fixture.Application.Invalidate("machines", "2");
        using (var notification = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2))))
        {
            Assert.Equal("machines.changed", notification.RootElement.GetProperty("method").GetString());
            Assert.Equal("3", notification.RootElement.GetProperty("params").GetProperty("revision").GetString());
            Assert.False(notification.RootElement.GetProperty("params").TryGetProperty("state", out _));
        }
        Assert.InRange(latency.Elapsed.TotalSeconds, 0, 2);
        release.TrySetResult();
        using (var response = JsonDocument.Parse(await ReceiveTextAsync(socket)))
            Assert.Equal("1", response.RootElement.GetProperty("result").GetProperty("revision").GetString());
        fixture.Application.Invalidate("settings", "1");
        using var independent = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2)));
        Assert.Equal("settings.changed", independent.RootElement.GetProperty("method").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.test")]
    [InlineData("null")]
    [InlineData("http://localhost.evil.test")]
    public async Task ActualHandshakeRejectsMissingOrNonlocalOrigins(string? origin)
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = new ClientWebSocket();
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        var error = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(fixture.RpcUri, TestContext.Token));
        Assert.Contains("403", error.Message);
    }

    [Fact]
    public async Task ActualHealthAndHostChecksRemainSeparateFromControllerAdmission()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = new HttpClient();
        using var valid = await client.GetAsync(new Uri(fixture.HttpUri, "health"), TestContext.Token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.HttpUri, "health"));
        invalidRequest.Headers.Host = $"evil.test:{fixture.Port}";
        using var invalid = await client.SendAsync(invalidRequest, TestContext.Token);
        Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        using var socket = await fixture.ConnectAsync();
        using var stillHealthy = await client.GetAsync(new Uri(fixture.HttpUri, "health"), TestContext.Token);
        Assert.Equal(HttpStatusCode.OK, stillHealthy.StatusCode);
    }

    [Theory]
    [InlineData(WebSocketMessageType.Binary, WebSocketCloseStatus.InvalidMessageType)]
    [InlineData(WebSocketMessageType.Text, WebSocketCloseStatus.InvalidPayloadData)]
    public async Task ActualSocketRejectsBinaryAndInvalidUtf8(WebSocketMessageType type, WebSocketCloseStatus expected)
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        await socket.SendAsync(new byte[] { 0xc0, 0xaf }.AsMemory(), type, true, TestContext.Token);
        var result = await socket.ReceiveAsync(new byte[1024].AsMemory(), TestContext.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(expected, socket.CloseStatus);
    }

    [Fact]
    public async Task ActualFragmentAggregateAcceptsExactLimitAndClosesOneByteAbove()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var request = Encoding.UTF8.GetBytes(Request("system.getCapabilities", "\"limit\""));
        var bytes = Enumerable.Repeat((byte)' ', RpcProtocol.InboundBytes).ToArray();
        request.CopyTo(bytes, 0);
        await socket.SendAsync(bytes.AsMemory(0, bytes.Length / 2), WebSocketMessageType.Text, false, TestContext.Token);
        await socket.SendAsync(bytes.AsMemory(bytes.Length / 2), WebSocketMessageType.Text, true, TestContext.Token);
        Assert.Contains("\"id\":\"limit\"", await ReceiveTextAsync(socket));
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, false, TestContext.Token);
        await socket.SendAsync(new byte[] { (byte)' ' }.AsMemory(), WebSocketMessageType.Text, true, TestContext.Token);
        var result = await socket.ReceiveAsync(new byte[1024].AsMemory(), TestContext.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
    }

    [Fact]
    public async Task ActualIncompleteMessageHasTenSecondDeadlineWithoutIdleDisconnect()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        await socket.SendAsync(new byte[] { (byte)'{' }.AsMemory(), WebSocketMessageType.Text, false, TestContext.Token);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await socket.ReceiveAsync(new byte[1024].AsMemory(), TestContext.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.InRange(started.Elapsed.TotalSeconds, 9, 14);
    }

    [Fact]
    public async Task ActualSlowConsumerIsClosedRatherThanDroppingTerminalNotifications()
    {
        await using var fixture = await Fixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, Request("system.getCapabilities", "\"subscribed\""));
        await ReceiveTextAsync(socket);
        var payload = new RpcNoteChunk(new string('x', 200000), 0, null, 200000);
        for (var i = 0; i < 128; i++) fixture.Application.Publish("system.problem", payload);
        var buffer = new byte[32768];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
        }
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    public async Task DomainCancellationIncludesNotificationsButCannotCancelOtherMachines()
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var machine = Guid.NewGuid();
        await using var fixture = await Fixture.StartAsync(async (method, _, token) =>
        {
            if (method != "windowsApp.open") return new(new RpcCancelResult("queried"));
            admitted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", method = "windowsApp.open", @params = new { machineId = machine }
        }));
        await admitted.Task.WaitAsync(TestContext.Token);
        await SendAsync(socket, Request("windowsApp.cancel", "\"wrong\"", JsonSerializer.Serialize(new { machineId = Guid.NewGuid() })));
        await ReceiveTextAsync(socket);
        Assert.False(cancelled.Task.IsCompleted);
        await SendAsync(socket, Request("windowsApp.cancel", "\"right\"", JsonSerializer.Serialize(new { machineId = machine })));
        await ReceiveTextAsync(socket);
        await cancelled.Task.WaitAsync(TestContext.Token);
    }

    [Theory]
    [InlineData("sharing.cancel", "sharing.start", "sharing.getStatus")]
    [InlineData("devboxes.cancel", "devboxes.refresh", "devboxes.getCatalog")]
    [InlineData("prerequisites.cancel", "prerequisites.checkAll", "prerequisites.getStatus")]
    public async Task EveryDomainCancellationTargetsItsConnectionRegistryAndReturnsCurrentQuery(
        string cancelMethod, string workMethod, string queryMethod)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async (method, _, token) =>
        {
            if (method == queryMethod) return new(new RpcCancelResult("current"));
            Assert.Equal(workMethod, method);
            admitted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, Request(workMethod, "\"work\""));
        await admitted.Task.WaitAsync(TestContext.Token);
        await SendAsync(socket, Request(cancelMethod, "\"cancel\""));
        var replies = new Dictionary<string, JsonElement>();
        while (replies.Count < 2)
        {
            using var message = JsonDocument.Parse(await ReceiveTextAsync(socket));
            if (message.RootElement.TryGetProperty("id", out var id))
                replies.Add(id.GetString()!, message.RootElement.Clone());
        }
        Assert.Equal(1006, replies["work"].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("current", replies["cancel"].GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(2, fixture.Application.Calls);
    }

    [Fact]
    public async Task DisconnectDrainDeadlineFailsClosedUntilActualCleanupCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { await cleanup.Task; throw; }
            return new(null);
        });
        try
        {
            using var socket = await fixture.ConnectAsync();
            await SendAsync(socket, Request("devboxes.refresh", "\"drain\""));
            await started.Task.WaitAsync(TestContext.Token);
            socket.Abort();
            await fixture.Application.DrainTimedOut.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var competing = new ClientWebSocket();
            competing.Options.SetRequestHeader("Origin", "http://localhost");
            var error = await Assert.ThrowsAsync<WebSocketException>(() => competing.ConnectAsync(fixture.RpcUri, TestContext.Token));
            Assert.Contains("409", error.Message);
            cleanup.TrySetResult();
            using var replacement = await fixture.ConnectEventuallyAsync();
            await SendAsync(replacement, Request("system.getCapabilities", "\"replacement\""));
            Assert.Contains("replacement", await ReceiveTextAsync(replacement));
        }
        finally { cleanup.TrySetResult(); }
    }

    [Fact]
    public async Task ShutdownInConcurrentBatchCancelsWorkBeforeWaitingForBatchAcknowledgement()
    {
        await using var fixture = await Fixture.StartAsync(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(null);
        });
        using var socket = await fixture.ConnectAsync();
        await SendAsync(socket, "[" + Request("devboxes.refresh", "\"work\"") + "," +
            Request("system.shutdown", "\"shutdown\"") + "]");
        using var response = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, response.RootElement.GetArrayLength());
        Assert.Equal(1006, response.RootElement[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("stopping", response.RootElement[1].GetProperty("result").GetProperty("state").GetString());
    }

    [Fact]
    public void RemoteEndpointDefenseRejectsNonloopbackEvenWithAcceptedHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("localhost", 51821);
        context.Request.Headers.Origin = "http://localhost";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.False(RpcEndpointPolicy.IsRequestAllowed(context, 51821, true));
        context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        Assert.True(RpcEndpointPolicy.IsRequestAllowed(context, 51821, true));
    }

    internal static string Request(string method, string id, string? parameters = null) =>
        $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"id\":{id}" +
        (parameters is null ? "}" : ",\"params\":" + parameters + "}");

    internal static async Task SendAsync(ClientWebSocket socket, string text) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(text).AsMemory(), WebSocketMessageType.Text, true, TestContext.Token);

    internal static async Task<string> ReceiveTextAsync(ClientWebSocket socket, TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        using var stream = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            stream.Write(buffer, 0, result.Count);
            Assert.True(stream.Length <= RpcProtocol.OutboundBytes);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        public FakeApplication Application { get; }
        private readonly RpcServer server;
        public int Port { get; }
        public Uri RpcUri => new($"ws://127.0.0.1:{Port}/rpc");
        public Uri HttpUri => new($"http://127.0.0.1:{Port}/");
        private Fixture(FakeApplication application, int port)
        {
            Application = application;
            Port = port;
            server = new(application, port);
        }
        public static async Task<Fixture> StartAsync(
            Func<string, JsonElement, CancellationToken, Task<RpcExecutionResult>>? execute = null)
        {
            var fixture = new Fixture(new(execute), FreePort());
            await fixture.server.StartAsync(TestContext.Token);
            return fixture;
        }
        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", "http://localhost:3000");
            try { await socket.ConnectAsync(RpcUri, TestContext.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
        public async Task<ClientWebSocket> ConnectEventuallyAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                try { return await ConnectAsync(); }
                catch (WebSocketException) { await Task.Delay(20, deadline.Token); }
            }
        }
        public ValueTask DisposeAsync() => server.DisposeAsync();
    }

    internal sealed class FakeApplication(
        Func<string, JsonElement, CancellationToken, Task<RpcExecutionResult>>? execute) : IRpcApplication
    {
        private int calls;
        public TaskCompletionSource DrainTimedOut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref calls);
        public Guid HostInstanceId { get; } = Guid.NewGuid();
        public event EventHandler<RpcPublication>? Published;
        public Task<RpcExecutionResult> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return execute?.Invoke(method, parameters, cancellationToken) ??
                Task.FromResult(new RpcExecutionResult(new RpcCancelResult("complete")));
        }
        public void RequestShutdown() { }
        public void AcceptShutdown(Task acknowledgement) { }
        public void ReportDrainTimeout() => DrainTimedOut.TrySetResult();
        public void ReportDrainCompleted() { }
        public void Publish(string method, object state) => Published?.Invoke(this, new(method, state));
        public void Invalidate(string domain, string revision)
        {
            var invalidation = new RpcInvalidation(HostInstanceId.ToString("D"), domain, revision);
            Published?.Invoke(this, new(domain + ".changed", invalidation, invalidation));
        }
    }

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

internal static class TestContext
{
    public static CancellationToken Token => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
