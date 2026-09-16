using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class ProbePipelineTests : IDisposable
{
    private const string PrivatePayload = "SYNTHETIC-PROMPT-TOOL-TRANSCRIPT-MUST-NOT-BE-STORED";
    private const string Session = "synthetic-private-session-identity";
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");

    [Theory]
    [InlineData("visual-studio")]
    [InlineData("vscode")]
    public async Task EmptyTargetReporterAcceptsOnlyConsentedProbeThroughRelayAndRealIpc(string kind)
    {
        var plan = await PrepareAsync(kind);
        var transport = new RecordingTransport();
        var received = new ConcurrentQueue<ClientIpcRequest>();
        using var httpHandler = new NoHookHttpHandler();
        using var http = new HttpClient(httpHandler);
        await using var coordinator = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        await using var ipc = new ClientIpcServer(ConfigPath, (request, token) =>
        {
            received.Enqueue(request);
            return coordinator.HandleAsync(request, token);
        });
        coordinator.Start();
        await WaitForStartedAsync(coordinator);
        Assert.Empty(RemoteConfiguration.Load(ConfigPath).Integrations);
        var originalState = await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath));
        HookVerification.Begin(plan, consent: true);
        try
        {
            var baseline = kind == "vscode"
                ? new[] { "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop" }
                : ["sessionStart", "userPromptSubmitted", "preToolUse", "postToolUse", "agentStop"];
            foreach (var name in baseline) await RelayAsync(http, plan.Target, name, plan.ProbeId);

            var evidence = HookVerification.ReadEvidence(ConfigPath, plan.ProbeId);
            Assert.Equal(5, evidence.Acknowledgements);
            Assert.True(evidence.StableSessionObserved);
            Assert.Equal(baseline.Order(), evidence.Events.Order());
            Assert.Equal(5, received.Count(r => r.Command == "probe"));
            Assert.All(received.Where(r => r.Command == "probe"), request =>
            {
                Assert.Equal(plan.Target.Kind, request.Hook!.Source!.Kind);
                Assert.Equal(plan.Target.ScopeId, request.Hook.Source.ScopeId);
                Assert.DoesNotContain(PrivatePayload, JsonSerializer.Serialize(request, Protocol.Json));
            });

            var denied = await ClientIpc.HookAsync(ConfigPath, AgentEvent.SessionStart,
                new(Session, DateTimeOffset.UtcNow, false, Source: Source(plan.Target)));
            Assert.False(denied.Accepted);
            var legacyDenied = await ClientIpc.HookAsync(ConfigPath, AgentEvent.SessionStart,
                new(Session, DateTimeOffset.UtcNow, false));
            Assert.False(legacyDenied.Accepted);
            await RelayAsync(http, plan.Target, baseline[0], probeId: null);
            using (var legacyInput = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                sessionId = Session, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            })))
                Assert.Equal(0, await new RelayEngine(http).RunAsync(
                    ["hook", "--config", ConfigPath, "--event", "sessionStart"], legacyInput));
            Assert.Equal(5, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            Assert.Equal(originalState, await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath)));
            Assert.Single(transport.Reports);
            Assert.Equal(0, httpHandler.Requests);

            var journal = await File.ReadAllTextAsync(Path.Combine(root, "hook-verification", $"probe-{plan.ProbeId:N}.json"));
            Assert.DoesNotContain(PrivatePayload, journal);
            Assert.DoesNotContain(Session, journal);
            Assert.Throws<InvalidOperationException>(() => HookVerification.Complete(plan,
                new(false, false, false, false, "Synthetic fixture, not a live IDE", "Not verified")));
            Assert.False(HookVerification.Resolve(plan.Target, ConfigPath).CanInstall);
            Assert.True(HookVerification.HasPendingCleanup(ConfigPath));
        }
        finally { HookVerification.Cancel(plan); }
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
    }

    [Fact]
    public async Task ProbeJournalRejectsDifferentKindScopeVersionOrIdentifier()
    {
        var plan = await PrepareAsync("visual-studio");
        using var httpHandler = new NoHookHttpHandler();
        using var http = new HttpClient(httpHandler);
        var transport = new RecordingTransport();
        await using var coordinator = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        await using var ipc = new ClientIpcServer(ConfigPath, coordinator.HandleAsync);
        coordinator.Start();
        await WaitForStartedAsync(coordinator);
        HookVerification.Begin(plan, consent: true);
        try
        {
            await RelayAsync(http, plan.Target with { Kind = "copilot-cli" }, "sessionStart", plan.ProbeId);
            await RelayAsync(http, plan.Target with { ScopeId = "wrong-scope" }, "sessionStart", plan.ProbeId);
            await RelayAsync(http, plan.Target with { HostVersion = "different-version" }, "sessionStart", plan.ProbeId);
            await RelayAsync(http, plan.Target, "sessionStart", Guid.NewGuid());
            Assert.Equal(0, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            await RelayAsync(http, plan.Target, "sessionStart", plan.ProbeId);
            Assert.Equal(1, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            Assert.False(HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).StableSessionObserved);
            Assert.Single(transport.Reports);
            Assert.Equal(0, httpHandler.Requests);
        }
        finally { HookVerification.Cancel(plan); }
    }

    [Fact]
    public async Task ExitAndAbsentClientNeverResumeProbesOrFallBackToHttp()
    {
        var plan = await PrepareAsync("visual-studio");
        var transport = new RecordingTransport();
        using var httpHandler = new NoHookHttpHandler();
        using var http = new HttpClient(httpHandler);
        try
        {
            await using (var coordinator = new ClientCoordinator(ConfigPath, transportFactory: _ => transport))
            await using (var ipc = new ClientIpcServer(ConfigPath, coordinator.HandleAsync))
            {
                coordinator.Start();
                await WaitForStartedAsync(coordinator);
                HookVerification.Begin(plan, consent: true);
                await RelayAsync(http, plan.Target, "sessionStart", plan.ProbeId);
                Assert.Equal(1, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
                Assert.True((await ClientIpc.StopAsync(ConfigPath)).Accepted);
                var reportsAfterExit = transport.Reports.Count;
                var stateAfterExit = await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath));
                await RelayAsync(http, plan.Target, "preToolUse", plan.ProbeId);
                Assert.Equal(reportsAfterExit, transport.Reports.Count);
                Assert.Equal(stateAfterExit, await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath)));
            }
            var count = transport.Reports.Count;
            await RelayAsync(http, plan.Target, "preToolUse", plan.ProbeId);
            Assert.Equal(count, transport.Reports.Count);
            Assert.Equal(1, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            Assert.Equal(0, httpHandler.Requests);
        }
        finally { HookVerification.Cancel(plan); }
    }

    private async Task<HookVerificationPlan> PrepareAsync(string kind)
    {
        var relay = Path.ChangeExtension((await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "relay-path.txt"))).Trim(), ".exe");
        Assert.True(File.Exists(relay));
        var configuration = new RemoteConfiguration
        {
            MachineId = Guid.NewGuid(), RelayPath = relay, MachineName = "synthetic-probe-only"
        }.WithDashboardUrl("http://127.0.0.1:51820").ToVersion4();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var syntheticHost = Path.Combine(root, "synthetic-host.exe");
        AtomicFile.Write(syntheticHost, [0]);
        return HookVerification.Preview(new IntegrationTarget
        {
            Id = "synthetic-ide-target", InstallationId = "synthetic-installation", Kind = kind,
            DisplayName = "Synthetic probe pipeline fixture — not live IDE verification", HostVersion = "test-build",
            ExecutablePath = syntheticHost,
            ScopeId = "synthetic-physical-scope", HookDirectory = Path.Combine(root, "hooks"),
            SettingsPath = kind == "vscode" ? Path.Combine(root, "profile", "settings.json") : null
        }, ConfigPath, relay);
    }

    private async Task RelayAsync(HttpClient http, IntegrationTarget target, string name, Guid? probeId)
    {
        var arguments = new List<string>
        {
            "hook", "--config", ConfigPath, "--event", name, "--source", target.Kind,
            "--scope", target.ScopeId, "--source-version", target.HostVersion, "--adapter", target.Kind
        };
        if (probeId is { } id) arguments.AddRange(["--probe", id.ToString("D")]);
        var payload = target.Kind == "vscode"
            ? JsonSerializer.SerializeToUtf8Bytes(new
            {
                session_id = Session, timestamp = DateTimeOffset.UtcNow.ToString("O"), hook_event_name = name,
                prompt = PrivatePayload, tool_input = PrivatePayload, tool_response = PrivatePayload,
                transcript_path = PrivatePayload
            })
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                sessionId = Session, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                prompt = PrivatePayload, toolArgs = PrivatePayload, toolResult = new { output = PrivatePayload },
                transcriptPath = PrivatePayload
            });
        using var input = new MemoryStream(payload);
        Assert.Equal(0, await new RelayEngine(http).RunAsync(arguments.ToArray(), input));
    }

    private static SourceDescriptor Source(IntegrationTarget target) => new(target.Kind, target.ScopeId, target.HostVersion);

    private static async Task WaitForStartedAsync(ClientCoordinator coordinator)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (coordinator.Status().EffectiveRevision is null)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordingTransport : IPresenceTransport
    {
        public ConcurrentQueue<PresenceReport> Reports { get; } = new();
        public Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken)
        {
            Reports.Enqueue(report);
            return Task.FromResult(true);
        }
        public void Dispose() { }
    }

    private sealed class NoHookHttpHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
