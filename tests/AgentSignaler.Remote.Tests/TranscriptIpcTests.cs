using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class TranscriptIpcTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");

    [Fact]
    public async Task NewServerReturnsExactLegacyResponseWithoutTranscriptFields()
    {
        Directory.CreateDirectory(_root);
        ClientIpcRequest? observed = null;
        await using var server = new ClientIpcServer(ConfigPath, (request, _) =>
        {
            observed = request;
            return Task.FromResult(new ClientIpcResponse(true, "connected", "revision", 300));
        });
        var response = await Exchange("""{"version":2,"command":"status"}""");
        using var parsed = JsonDocument.Parse(response);
        Assert.Equal(2, parsed.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(new[] { "accepted", "effectiveRevision", "error", "heartbeatIntervalSeconds", "state", "version" },
            parsed.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(2, observed!.Version);
        Assert.True((await ClientIpc.StatusAsync(ConfigPath)).Accepted);
    }

    [Theory]
    [InlineData("""{"version":3,"version":3,"command":"transcript-negotiate"}""")]
    [InlineData("""{"version":3,"command":"status","command":"transcript-hook"}""")]
    [InlineData("""{"version":"3","command":"status"}""")]
    [InlineData("""{"version":2,"command":"status","transcript":null}""")]
    [InlineData("""{"version":2,"command":"transcript-negotiate"}""")]
    public async Task FramingRejectsAmbiguousOrMixedSchemasBeforeHandler(string body)
    {
        Directory.CreateDirectory(_root);
        var calls = 0;
        await using var server = new ClientIpcServer(ConfigPath, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ClientIpcResponse(true, "connected"));
        });
        Assert.Empty(await Exchange(body));
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.True((await ClientIpc.StatusAsync(ConfigPath)).Accepted);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Theory]
    [InlineData(2, "hook")]
    [InlineData(3, "status")]
    [InlineData(3, "transcript-negotiate")]
    [InlineData(3, "transcript-read")]
    [InlineData(3, "transcript-reload")]
    public async Task LegacyAndControlFramesRemainAtFourKib(int version, string command)
    {
        Directory.CreateDirectory(_root);
        var calls = 0;
        await using var server = new ClientIpcServer(ConfigPath, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new ClientIpcResponse(true, "connected"));
        });
        var body = JsonSerializer.Serialize(new { version, command, expectedRevision = new string('x', 4096) });
        Assert.Empty(await Exchange(body));
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.True((await ClientIpc.StatusAsync(ConfigPath)).Accepted);
    }

    private async Task<byte[]> Exchange(string body)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", ClientIdentity.PipeName(ConfigPath),
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(budget.Token);
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, budget.Token);
        await pipe.WriteAsync(bytes, budget.Token);
        await pipe.FlushAsync(budget.Token);
        try
        {
            var first = await pipe.ReadAsync(header.AsMemory(0, 1), budget.Token);
            if (first == 0) return [];
            await pipe.ReadExactlyAsync(header.AsMemory(1), budget.Token);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header);
            Assert.InRange(count, 1, ClientIpc.MaximumMessageBytes);
            var response = new byte[count];
            await pipe.ReadExactlyAsync(response, budget.Token);
            return response;
        }
        catch (IOException) { return []; }
    }

    [Fact]
    public async Task TwoContentOperationsLeaveStatusCapacityAndScratchIsReleased()
    {
        Directory.CreateDirectory(_root);
        var scratch = new TranscriptScratchBudget();
        var admitted = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new ClientIpcServer(ConfigPath, async (request, token) =>
        {
            if (request.Command == "transcript-hook")
            {
                if (Interlocked.Increment(ref admitted) == 2) both.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return new(true, "connected");
        }, scratch);
        var first = ClientIpc.SendAsync(ConfigPath, new(3, "transcript-hook"));
        var second = ClientIpc.SendAsync(ConfigPath, new(3, "transcript-hook"));
        try
        {
            await both.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False((await ClientIpc.SendAsync(ConfigPath, new(3, "transcript-hook"))).Accepted);
            Assert.True((await ClientIpc.StatusAsync(ConfigPath)).Accepted);
            Assert.Equal(2, Volatile.Read(ref admitted));
            Assert.InRange(scratch.UsedBytes, 1, TranscriptScratchBudget.MaximumBytes);
        }
        finally { release.TrySetResult(); }
        Assert.True((await first).Accepted);
        Assert.True((await second).Accepted);
        await ClientRuntimeTests.Eventually(() => scratch.UsedBytes == 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
