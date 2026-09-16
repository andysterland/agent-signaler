using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientIpcTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");
    private RemoteConfiguration Configuration => new RemoteConfiguration
    {
        Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test"
    };
    private void Save(bool managed = true) => AtomicFile.Write(ConfigPath,
        JsonSerializer.SerializeToUtf8Bytes(managed ? Configuration.ToVersion3() : Configuration, Protocol.Json));

    [Fact]
    public async Task RealPipeAcceptsOnlySanitizedHooksAndSupportsStatusReloadStop()
    {
        Save();
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        await using var server = new ClientIpcServer(ConfigPath, runtime.HandleAsync);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.StopAcknowledged += () => stopped.TrySetResult();
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.Equal("connected", (await ClientIpc.StatusAsync(ConfigPath)).State);
        var http = new RejectHttpHandler();
        using var client = new HttpClient(http);
        var hook = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sessionId = "test-session", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            prompt = "SECRET-PROMPT", toolArgs = new { value = "SECRET-CODE" }
        }));
        Assert.Equal(0, await new RelayEngine(client).RunAsync(["hook", "--event", "sessionStart", "--config", ConfigPath],
            new MemoryStream(hook)));
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Hook));
        Assert.Equal(0, http.Calls);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(transport.Reports, Protocol.Json));
        Assert.False((await ClientIpc.ReloadAsync(ConfigPath, "wrong")).Accepted);
        Assert.True((await ClientIpc.ReloadAsync(ConfigPath, ClientConfigurationRevision.Read(ConfigPath))).Accepted);
        Assert.True((await ClientIpc.StopAsync(ConfigPath)).Accepted);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False((await ClientIpc.HookAsync(ConfigPath, AgentEvent.SessionEnd,
            new("test-session", DateTimeOffset.UtcNow, false))).Accepted);
        Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
    }

    [Fact]
    public async Task ActivateRequestIsSentToTheExistingClient()
    {
        Save();
        var activations = 0;
        await using var server = new ClientIpcServer(ConfigPath, (request, _) =>
        {
            if (request.Command == "activate") Interlocked.Increment(ref activations);
            return Task.FromResult(new ClientIpcResponse(request.Command == "activate", "connected"));
        });

        Assert.True((await ClientIpc.ActivateAsync(ConfigPath)).Accepted);
        Assert.Equal(1, Volatile.Read(ref activations));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentClientNeverFallsBackToHttpOrCapturesHooks(bool managed)
    {
        Save(managed);
        var http = new RejectHttpHandler();
        using var client = new HttpClient(http);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sessionId = "lost", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        }));
        Assert.Equal(0, await new RelayEngine(client).RunAsync(
            ["hook", "--event", "sessionStart", "--config", ConfigPath], new MemoryStream(body)));
        Assert.Equal(0, http.Calls);
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
        Assert.False(File.Exists(Path.Combine(_root, "client-generation")));
    }

    [Fact]
    public async Task PipeRejectsVersionCommandsExtraFieldsAndOversizedFramesThenRecovers()
    {
        Save();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport());
        await using var server = new ClientIpcServer(ConfigPath, runtime.HandleAsync);
        runtime.Start();
        Assert.False((await ClientIpc.SendAsync(ConfigPath, new(99, "status"))).Accepted);
        Assert.False((await ClientIpc.SendAsync(ConfigPath, new(1, "launch"))).Accepted);
        Assert.False((await ClientIpc.SendAsync(ConfigPath, new(1, "status", AgentEvent.SessionStart))).Accepted);
        using (var pipe = new NamedPipeClientStream(".", ClientIdentity.PipeName(ConfigPath),
                   PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token);
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, ClientIpc.MaximumMessageBytes + 1);
            await pipe.WriteAsync(header, timeout.Token);
            var result = new byte[1];
            try { Assert.Equal(0, await pipe.ReadAsync(result, timeout.Token)); }
            catch (IOException) { }
        }
        Assert.True((await ClientIpc.StatusAsync(ConfigPath)).Accepted);
    }

    [Fact]
    public async Task PartialFrameCannotKeepAnIpcWorkerForever()
    {
        Save();
        await using var server = new ClientIpcServer(ConfigPath, (_, _) => Task.FromResult(new ClientIpcResponse(true, "test")));
        using var pipe = new NamedPipeClientStream(".", ClientIdentity.PipeName(ConfigPath),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        try { Assert.Equal(0, await pipe.ReadAsync(new byte[1], timeout.Token)); }
        catch (IOException) { }
        Assert.True((await ClientIpc.StatusAsync(ConfigPath)).Accepted);
        Assert.Contains("ipc-request-timeout", await File.ReadAllTextAsync(RemotePaths.Log(ConfigPath)));
    }

    [Fact]
    public async Task RepeatedInvalidIpcIsDiagnosedWithoutPayloadOrUnboundedLogGrowth()
    {
        Save();
        await using var server = new ClientIpcServer(ConfigPath, (_, _) =>
            Task.FromResult(new ClientIpcResponse(true, "test")));
        for (var i = 0; i < 12; i++)
            Assert.False((await ClientIpc.SendAsync(ConfigPath, new(99, "SECRET-COMMAND"))).Accepted);
        var lines = await File.ReadAllLinesAsync(RemotePaths.Log(ConfigPath));
        Assert.Single(lines);
        Assert.Contains("ipc-version-rejected", lines[0]);
        Assert.DoesNotContain("SECRET", lines[0]);
    }

    [Fact]
    public async Task LostStopReplyStillCompletesHostCleanupAndServicingOwnerBarrier()
    {
        Save();
        var offlineEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOffline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new RecordingTransport(async (report, token) =>
        {
            if (report.Kind == PresenceKind.Offline)
            {
                offlineEntered.TrySetResult();
                await releaseOffline.Task.WaitAsync(token);
            }
            return true;
        });
        var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        var server = new ClientIpcServer(ConfigPath, runtime.HandleAsync);
        var replies = 0;
        server.StopAcknowledged += () => Interlocked.Increment(ref replies);
        var hostExit = Task.Run(async () =>
        {
            await ClientLifetime.WaitForExitAsync(runtime);
            await server.DisposeAsync();
            await runtime.DisposeAsync();
        });
        try
        {
            runtime.Start();
            await Eventually(() => runtime.Status().State == "connected");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var adapter = new ClientIntegrationRuntime(new DisconnectingStopPlatform(
                offlineEntered.Task, () => releaseOffline.TrySetResult()));
            await adapter.StopAsync(ConfigPath, timeout.Token);
            await hostExit.WaitAsync(timeout.Token);
            Assert.Equal(0, Volatile.Read(ref replies));
            Assert.False(new WindowsClientRuntimePlatform().IsOwnerRunning(ConfigPath));
            Assert.Equal(PresenceKind.Offline, transport.Reports.Last().Kind);
            Assert.Contains("ipc-connection-failed", await File.ReadAllTextAsync(RemotePaths.Log(ConfigPath)));
        }
        finally
        {
            releaseOffline.TrySetResult();
            await runtime.StopAsync();
            await hostExit;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class RejectHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private sealed class DisconnectingStopPlatform(Task offlineEntered, Action releaseOffline) : IClientRuntimePlatform
    {
        public bool IsOwnerRunning(string configPath) => new WindowsClientRuntimePlatform().IsOwnerRunning(configPath);
        public void Launch(string clientPath, string configPath) => throw new InvalidOperationException("Unexpected launch.");

        public async Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request, CancellationToken token)
        {
            using (var pipe = new NamedPipeClientStream(".", ClientIdentity.PipeName(configPath),
                       PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                await pipe.ConnectAsync(token);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Protocol.Json);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                await pipe.WriteAsync(header, token);
                await pipe.WriteAsync(bytes, token);
                await pipe.FlushAsync(token);
                await offlineEntered.WaitAsync(token);
            }
            releaseOffline();
            return new(false, "unavailable", Error: "Synthetic caller disconnected before the stop reply.");
        }
    }
}
