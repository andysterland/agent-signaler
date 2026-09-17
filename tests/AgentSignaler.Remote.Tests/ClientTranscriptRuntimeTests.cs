using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientTranscriptRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");
    private static SourceDescriptor Source => new("copilot-cli", "test-scope", "test");

    private RemoteConfiguration Save(int version = 5, bool enabled = true, string scheme = "https")
    {
        var configuration = new RemoteConfiguration
        {
            Version = version, DashboardBaseUrl = $"{scheme}://localhost:51820/", MachineId = Guid.NewGuid(),
            MachineName = "fixture", DetailedReportingEnabled = enabled,
            Integrations = [new()
            {
                Id = "test-cli", Kind = "copilot-cli", DisplayName = "Fixture", InstallationId = "fixture",
                ScopeId = Source.ScopeId, HostVersion = Source.Version, HookDirectory = Path.Combine(_root, "hooks"),
                Capability = IntegrationCapability.Configured, Provenance = "synthetic-test-only",
                SupportedEvents = ["userPromptSubmitted", "agentStop", "sessionStart", "preToolUse"]
            }]
        };
        Write(configuration);
        return configuration;
    }

    private void Write(RemoteConfiguration configuration) =>
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));

    [Fact]
    public async Task RelayProjectsStatusBeforeNegotiatingAndNeverPersistsAllowedText()
    {
        Save();
        var transport = new TranscriptFake();
        var requests = new ConcurrentQueue<ClientIpcRequest>();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport(),
            transcriptTransport: transport);
        await using var server = new ClientIpcServer(ConfigPath, (request, token) =>
        {
            requests.Enqueue(request);
            return runtime.HandleAsync(request, token);
        }, runtime.TranscriptScratch);
        runtime.Start();
        await Ready(runtime);
        const string allowed = "Fictional Robin robin@example.invalid";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sessionId = "session", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            prompt = allowed, toolArgs = "EXCLUDED-ARGS", reasoning = "EXCLUDED-REASONING"
        });
        Assert.Equal(0, await new RelayEngine().RunAsync(
            ["hook", "--event", "userPromptSubmitted", "--config", ConfigPath, "--source", Source.Kind,
                "--scope", Source.ScopeId, "--source-version", Source.Version, "--adapter", Source.Kind],
            new MemoryStream(payload)));
        await Eventually(() => transport.Events.Any(e => e.Payload is TranscriptMessage));
        Assert.Equal(new[] { "hook", "transcript-negotiate", "transcript-hook" }, requests.Select(r => r.Command));
        Assert.Equal(2, requests.First().Version);
        Assert.Equal(allowed, Assert.IsType<TranscriptMessage>(transport.Events.Last().Payload).Text);
        var captured = JsonSerializer.Serialize(transport.Events, TranscriptProtocol.Json);
        Assert.DoesNotContain("EXCLUDED", captured);
        await runtime.StopAsync();
        foreach (var file in Directory.GetFiles(_root))
        {
            if (file.EndsWith(".lock", StringComparison.Ordinal)) continue;
            Assert.DoesNotContain(allowed, await File.ReadAllTextAsync(file));
            Assert.DoesNotContain("EXCLUDED", await File.ReadAllTextAsync(file));
        }
    }

    [Theory]
    [InlineData(4, true, "https")]
    [InlineData(5, false, "https")]
    [InlineData(5, true, "http")]
    public async Task MissingPrerequisitesRemainStatusOnly(int version, bool enabled, string scheme)
    {
        Save(version, enabled, scheme);
        var status = new RecordingTransport();
        var transcript = new TranscriptFake();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => status, transcriptTransport: transcript);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var negotiation = await Negotiate(runtime);
        Assert.False(negotiation.Transcript!.Enabled);
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("session", DateTimeOffset.UtcNow, false, Source: Source)).Accepted);
        await Eventually(() => status.Reports.Any(r => r.Kind == PresenceKind.Hook));
        Assert.Empty(transcript.Events);
        Assert.Equal(0, transcript.Calls);
    }

    [Fact]
    public async Task ExplicitSuspensionCancelsSendingAndStaysSuspendedAfterUnsuccessfulSave()
    {
        var config = Save();
        var transcript = new TranscriptFake { HoldMessages = true };
        var status = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => status, transcriptTransport: transcript);
        runtime.Start();
        var revision = await Ready(runtime);
        Assert.True((await runtime.HandleAsync(Message(revision))).Accepted);
        await transcript.MessageEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopped = await runtime.HandleAsync(new(3, "transcript-reload", ExpectedRevision: "suspend"));
        Assert.True(stopped.Accepted);
        await transcript.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False((await runtime.HandleAsync(Message(revision))).Accepted);
        Assert.True(config.DetailedReportingEnabled);
        Assert.False((await Negotiate(runtime)).Transcript!.Enabled);
        Assert.Contains("preference-not-confirmed", runtime.TranscriptAvailability);
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("status-still-works", DateTimeOffset.UtcNow, false, Source: Source)).Accepted);
        await Eventually(() => status.Reports.Any(r => r.Kind == PresenceKind.Hook));
        Write(config with { DetailedReportingEnabled = false });
        Assert.True((await runtime.HandleAsync(new(3, "transcript-reload",
            ExpectedRevision: ClientConfigurationRevision.Read(ConfigPath)))).Accepted);
        Assert.False((await Negotiate(runtime)).Transcript!.Enabled);
    }

    [Fact]
    public async Task InvalidConfigurationRevisionCancelsDetailWithoutWaitingForPresence()
    {
        Save();
        var transcript = new TranscriptFake { HoldMessages = true };
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport(),
            transcriptTransport: transcript);
        runtime.Start();
        var revision = await Ready(runtime);
        Assert.True((await runtime.HandleAsync(Message(revision))).Accepted);
        await transcript.MessageEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        AtomicFile.Write(ConfigPath, Encoding.UTF8.GetBytes("""{"version":5}"""));
        Assert.False((await runtime.HandleAsync(Message(revision))).Accepted);
        await transcript.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("connected", runtime.Status().State);
    }

    [Fact]
    public async Task ConcurrentAndDelayedOldReloadsCannotUndoAnUnsavedOptOut()
    {
        Save();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport(),
            transcriptTransport: new TranscriptFake());
        runtime.Start();
        await Ready(runtime);
        var hash = ClientConfigurationRevision.Read(ConfigPath);
        await Task.WhenAll(
            Task.Run(() => runtime.HandleAsync(new(3, "transcript-reload", ExpectedRevision: hash))),
            Task.Run(() => runtime.HandleAsync(new(3, "transcript-reload", ExpectedRevision: "suspend"))));
        Assert.False((await runtime.HandleAsync(new(3, "transcript-reload", ExpectedRevision: hash))).Accepted);
        Assert.False((await Negotiate(runtime)).Transcript!.Enabled);
        Assert.Contains("preference-not-confirmed", runtime.TranscriptAvailability);
        Assert.True((await runtime.ReloadAsync(hash)).Accepted);
        Assert.False((await Negotiate(runtime)).Transcript!.Enabled);
    }

    [Fact]
    public async Task TransactionPauseCanReconcileAnUnchangedEnabledConfiguration()
    {
        Save();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport(),
            transcriptTransport: new TranscriptFake());
        runtime.Start();
        var oldRevision = await Ready(runtime);
        Assert.True((await runtime.HandleAsync(new(3, "transcript-reload", ExpectedRevision: "pause"))).Accepted);
        Assert.False((await runtime.HandleAsync(Message(oldRevision))).Accepted);
        Assert.True((await runtime.HandleAsync(new(3, "transcript-reload",
            ExpectedRevision: ClientConfigurationRevision.Read(ConfigPath)))).Accepted);
        Assert.True(await Ready(runtime) > oldRevision);
    }

    [Fact]
    public async Task NewRelayNeverSendsTextToIncompatibleClient()
    {
        Save();
        var requests = new ConcurrentQueue<ClientIpcRequest>();
        await using var server = new ClientIpcServer(ConfigPath, (request, _) =>
        {
            requests.Enqueue(request);
            return Task.FromResult(new ClientIpcResponse(request.Version == 2, request.Version == 2 ? "connected" : "incompatible"));
        });
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sessionId = "session", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), prompt = "NEVER-SEND-TO-OLD-CLIENT"
        });
        Assert.Equal(0, await new RelayEngine().RunAsync(
            ["hook", "--event", "userPromptSubmitted", "--config", ConfigPath], new MemoryStream(payload)));
        Assert.Equal(new[] { "hook", "transcript-negotiate" }, requests.Select(r => r.Command));
        Assert.All(requests, request => Assert.Null(request.Transcript));
        Assert.DoesNotContain("NEVER-SEND", JsonSerializer.Serialize(requests, ClientIpc.Json));
    }

    [Fact]
    public async Task EndpointReloadDropsOldTextAndWaitsForNewManagedPresence()
    {
        var config = Save();
        var transcript = new TranscriptFake { HoldMessages = true };
        var presence = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => presence,
            transcriptTransport: transcript);
        runtime.Start();
        var revision = await Ready(runtime);
        Assert.True((await runtime.HandleAsync(Message(revision))).Accepted);
        await transcript.MessageEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Write(config.WithDashboardUrl("https://localhost:51821/"));
        var hash = ClientConfigurationRevision.Read(ConfigPath);
        Assert.True((await runtime.HandleAsync(new(3, "transcript-reload", ExpectedRevision: hash))).Accepted);
        await transcript.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False((await Negotiate(runtime)).Transcript!.Enabled);
        var count = presence.Reports.Count;
        runtime.RequestSnapshot();
        await Eventually(() => presence.Reports.Count > count);
        Assert.False((await Negotiate(runtime)).Transcript!.Enabled);
        Assert.DoesNotContain(transcript.Endpoints, endpoint => endpoint.Port == 51821);
        Assert.True((await runtime.ReloadAsync(hash)).Accepted);
        await Ready(runtime);
        Assert.Contains(transcript.Endpoints, endpoint => endpoint.Port == 51821);
        Assert.Single(transcript.Events, entry => entry.Payload is TranscriptMessage);
    }

    private static ClientIpcRequest Message(long revision) => new(3, "transcript-hook", Transcript:
        new(Source, "session", "userPromptSubmitted", DateTimeOffset.UtcNow, Guid.NewGuid(), revision,
            new TranscriptMessage(TranscriptRole.User, Guid.NewGuid().ToString("N"), TranscriptMessageIdOrigin.Local,
                "synthetic message")));
    private static Task<ClientIpcResponse> Negotiate(ClientCoordinator runtime) =>
        runtime.HandleAsync(new(3, "transcript-negotiate", TranscriptSource: Source));
    private static async Task<long> Ready(ClientCoordinator runtime)
    {
        await Eventually(() => runtime.TranscriptAvailability == "ready");
        var result = await Negotiate(runtime);
        Assert.True(result.Transcript!.Enabled);
        Assert.False(result.Transcript.AssistantFile);
        Assert.Equal("format-unverified", result.Transcript.Availability);
        return result.Transcript.SettingsRevision;
    }

    private sealed class TranscriptFake : ITranscriptTransport
    {
        private readonly Guid _epoch = Guid.NewGuid();
        private long _revision;
        public ConcurrentQueue<TranscriptEvent> Events { get; } = new();
        public ConcurrentQueue<Uri> Endpoints { get; } = new();
        public int Calls;
        public bool HoldMessages { get; init; }
        public TaskCompletionSource MessageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TranscriptTransportResult> SendAsync(Uri baseUri, TranscriptOperation operation,
            ReadOnlyMemory<byte> body, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Endpoints.Enqueue(baseUri);
            switch (operation)
            {
                case TranscriptOperation.Capabilities:
                    return Response(200, new TranscriptCapabilitiesResponse(1, _epoch, _revision, true, true,
                        TranscriptProtocol.Limits, TranscriptProtocol.ImplementedCapabilities));
                case TranscriptOperation.Open:
                    var open = JsonSerializer.Deserialize<TranscriptOpenRequest>(body.Span, TranscriptProtocol.Json)!;
                    return Response(200, new TranscriptOpenResponse(_epoch, open.StreamId, ++_revision, false));
                case TranscriptOperation.Close:
                    var close = JsonSerializer.Deserialize<TranscriptCloseRequest>(body.Span, TranscriptProtocol.Json)!;
                    return Response(200, new TranscriptCloseResponse(_epoch, close.StreamId, ++_revision, false));
                default:
                    var value = JsonSerializer.Deserialize<TranscriptEvent>(body.Span, TranscriptProtocol.Json)!;
                    Events.Enqueue(value);
                    if (HoldMessages && value.Payload is TranscriptMessage)
                    {
                        MessageEntered.TrySetResult();
                        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            Cancelled.TrySetResult();
                            throw;
                        }
                    }
                    return Response(202, new TranscriptAcknowledgement(_epoch, value.StreamId, value.Sequence,
                        TranscriptDisposition.Accepted));
            }
        }
        private static TranscriptTransportResult Response<T>(int status, T value) =>
            new(status, JsonSerializer.SerializeToUtf8Bytes(value, TranscriptProtocol.Json));
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
