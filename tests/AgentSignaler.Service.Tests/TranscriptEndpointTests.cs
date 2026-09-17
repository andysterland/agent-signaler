using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;

namespace AgentSignaler.Service.Tests;

public sealed class TranscriptEndpointTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), $"AgentSignaler-transcript-{Guid.NewGuid():N}");
    private readonly Guid _machine = Guid.NewGuid();
    private MachineStore _machines = null!;
    private DashboardServer _server = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        _port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _machines = new MachineStore(Path.Combine(_directory, "state.db"));
        _server = new DashboardServer(_machines, _port, options: new()
        {
            ListenerMode = DashboardListenerMode.Internet, TranscriptTunnelReady = true
        });
        await _server.StartAsync();
        _client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{_port}"), Timeout = TimeSpan.FromSeconds(10)
        };
    }

    private async Task PresenceAsync() =>
        await _machines.AcceptAsync(new PresenceReport
        {
            ProtocolVersion = PresenceProtocol.SourceVersion, Kind = PresenceKind.Started,
            MachineId = _machine, EventId = Guid.NewGuid(), MachineName = "synthetic",
            Client = "agent-signaler", ClientVersion = "test", Generation = 1, Sequence = 1,
            ReportedAtUtc = DateTimeOffset.UtcNow, HeartbeatIntervalSeconds = 300, Sessions = []
        });

    private async Task<TranscriptOpenRequest> OpenAsync()
    {
        await PresenceAsync();
        var open = new TranscriptOpenRequest(1, _machine, 1, _server.Transcripts.ReceiverEpoch,
            Guid.NewGuid(), TranscriptContractTests.Source, _server.Transcripts.OpenRevision, Guid.NewGuid());
        using var response = await _client.PostAsJsonAsync(TranscriptProtocol.OpenPath, open, TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return open;
    }

    [Fact]
    public async Task FourPostEndpointsUseVolatileAcknowledgementsAndNeverExposeNetworkReads()
    {
        var open = await OpenAsync();
        using var caps = await _client.PostAsJsonAsync(TranscriptProtocol.CapabilitiesPath, new TranscriptCapabilitiesRequest(1, _machine), TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, caps.StatusCode);
        var capabilities = await caps.Content.ReadFromJsonAsync<TranscriptCapabilitiesResponse>(TranscriptProtocol.Json);
        Assert.True(capabilities!.Enabled);
        Assert.True(capabilities.Ready);
        var value = TranscriptContractTests.Event(_machine, capabilities.ReceiverEpoch, open.StreamId);
        using var accepted = await _client.PostAsJsonAsync(TranscriptProtocol.EventsPath, value, TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var acknowledgement = await accepted.Content.ReadFromJsonAsync<TranscriptAcknowledgement>(TranscriptProtocol.Json);
        Assert.Equal(TranscriptDisposition.Accepted, acknowledgement!.Disposition);
        using var duplicate = await _client.PostAsJsonAsync(TranscriptProtocol.EventsPath, value, TranscriptProtocol.Json);
        Assert.Equal(TranscriptDisposition.Duplicate,
            (await duplicate.Content.ReadFromJsonAsync<TranscriptAcknowledgement>(TranscriptProtocol.Json))!.Disposition);
        var selection = new TranscriptSelection(_machine, value.Source, value.SessionId, value.StreamId);
        Assert.Equal(value, Assert.Single((await _server.Transcripts.ReadEventsAsync(selection)).Events).Event);
        using var close = await _client.PostAsJsonAsync(TranscriptProtocol.ClosePath,
            new TranscriptCloseRequest(1, _machine, 1, capabilities.ReceiverEpoch, open.StreamId, true), TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);
        Assert.Empty((await _server.Transcripts.ReadEventsAsync(selection)).Events);
        foreach (var path in new[] { TranscriptProtocol.EventsPath, TranscriptProtocol.CapabilitiesPath, "/api/transcripts/v1/sessions" })
        {
            using var get = await _client.GetAsync(path);
            Assert.True(get.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task AbsentPresenceReadinessAndReceiverDisableDoNotAffectStatus()
    {
        using var unknown = await _client.PostAsJsonAsync(TranscriptProtocol.OpenPath,
            new TranscriptOpenRequest(1, _machine, 1, _server.Transcripts.ReceiverEpoch, Guid.NewGuid(),
                TranscriptContractTests.Source, 0, Guid.NewGuid()), TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode);
        Assert.Equal(TranscriptRejection.UnknownManagedMachine,
            (await unknown.Content.ReadFromJsonAsync<TranscriptError>(TranscriptProtocol.Json))!.Category);
        _server.SetTranscriptReadiness(false);
        using var notReady = await _client.PostAsJsonAsync(TranscriptProtocol.CapabilitiesPath,
            new TranscriptCapabilitiesRequest(1, _machine), TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        _server.SetTranscriptReadiness(true);
        _server.Transcripts.SetEnabled(false);
        using var disabled = await _client.PostAsJsonAsync(TranscriptProtocol.CapabilitiesPath,
            new TranscriptCapabilitiesRequest(1, _machine), TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disabled.StatusCode);
        using var status = await _client.PostAsJsonAsync("/api/v1/status", StateTests.Request(AgentEvent.SessionStart), Protocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, status.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task BothDeclaredAndChunkedBodiesEnforceEventAndControlLimits(bool chunked, bool control)
    {
        var maximum = control ? TranscriptProtocol.MaxControlBytes : TranscriptProtocol.MaxEventBytes;
        using HttpContent content = chunked ? new StreamingContent(new byte[maximum + 1]) : new ByteArrayContent(new byte[maximum + 1]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, control ? TranscriptProtocol.OpenPath : TranscriptProtocol.EventsPath) { Content = content };
        request.Headers.TransferEncodingChunked = chunked;
        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _server.Transcripts.RetainedEventCount);
    }

    [Fact]
    public async Task MalformedDuplicateExcludedAndWrongContentTypesReturnOnlyCategories()
    {
        using var badType = await _client.PostAsync(TranscriptProtocol.EventsPath, new StringContent("{}"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, badType.StatusCode);
        foreach (var json in new[]
        {
            "{\"prompt\":\"excluded-marker\"}",
            "{\"protocolVersion\":1,\"protocolVersion\":1,\"machineId\":\"00000000-0000-0000-0000-000000000001\"}",
            "{\"protocolVersion\":1,\"machineId\":null}"
        })
        {
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var result = await _client.PostAsync(TranscriptProtocol.CapabilitiesPath, body);
            Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
            Assert.DoesNotContain("excluded-marker", await result.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task LanHasNoTranscriptRoutesEvenWithInjectedReady()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        _server = new DashboardServer(_machines, _port, options: new() { TranscriptTunnelReady = true });
        await _server.StartAsync();
        _server.SetTranscriptReadiness(true);
        Assert.False(_server.Transcripts.Ready);
        using var response = await _client.PostAsJsonAsync(TranscriptProtocol.CapabilitiesPath, new TranscriptCapabilitiesRequest(1, _machine), TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ManagedMachineRateBucketIsBoundedAndStatusRetainsCapacity()
    {
        await PresenceAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 18; i++)
        {
            using var response = await _client.PostAsJsonAsync(TranscriptProtocol.CapabilitiesPath,
                new TranscriptCapabilitiesRequest(1, _machine), TranscriptProtocol.Json);
            statuses.Add(response.StatusCode);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter!.Delta);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        using var health = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task SyntheticConversationExistsOnlyInMemoryNotSqliteOrStatusModels()
    {
        var open = await OpenAsync();
        var value = TranscriptContractTests.Event(_machine, _server.Transcripts.ReceiverEpoch, open.StreamId);
        const string marker = "TRANSCRIPT-ONLY-SYNTHETIC-6ba8c746";
        value = value with { Payload = ((TranscriptMessage)value.Payload) with { Text = marker } };
        using var accepted = await _client.PostAsJsonAsync(TranscriptProtocol.EventsPath, value, TranscriptProtocol.Json);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var views = JsonSerializer.Serialize(await _machines.GetMachinesAsync(), Protocol.Json);
        Assert.DoesNotContain(marker, views);
        foreach (var artifact in Directory.EnumerateFiles(_directory))
        {
            var bytes = await File.ReadAllBytesAsync(artifact);
            Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytes));
            Assert.DoesNotContain(marker, Encoding.Unicode.GetString(bytes));
        }
        await _server.StopAsync();
        Assert.Equal(0, _server.Transcripts.RetainedEventCount);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        _machines.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class StreamingContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
