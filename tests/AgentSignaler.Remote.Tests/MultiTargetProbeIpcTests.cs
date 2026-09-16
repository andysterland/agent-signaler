using System.Net;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class MultiTargetProbeIpcTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");

    private HookVerificationPlan CreateProbe()
    {
        var relay = Path.Combine(root, "AgentSignaler.Relay.exe");
        var ide = Path.Combine(root, "synthetic-ide.exe");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(relay, []);
        File.WriteAllBytes(ide, []);
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid(), RelayPath = relay }.ToVersion4();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var target = new IntegrationTarget
        {
            Id = "probe-profile", Kind = "vscode", DisplayName = "Synthetic local profile",
            InstallationId = "synthetic-ide", HostVersion = "1.137.0", ScopeId = "probe-scope",
            HookDirectory = Path.Combine(root, "profile", "observer"),
            SettingsPath = Path.Combine(root, "profile", "settings.json"),
            ExecutablePath = ide, Capability = IntegrationCapability.VerificationRequired
        };
        var plan = HookVerification.Preview(target, ConfigPath, relay);
        HookVerification.Begin(plan, true);
        return plan;
    }

    private string[] Arguments(HookVerificationPlan plan, string eventName, string? scope = null,
        string? version = null, Guid? probe = null, bool normalHook = false)
    {
        var args = new List<string>
        {
            "hook", "--config", ConfigPath, "--source", "vscode", "--scope", scope ?? plan.Target.ScopeId,
            "--source-version", version ?? plan.Target.HostVersion, "--adapter", "vscode", "--event", eventName
        };
        if (!normalHook) args.AddRange(["--probe", (probe ?? plan.ProbeId).ToString("D")]);
        return args.ToArray();
    }

    private static MemoryStream Payload(string eventName) => new(JsonSerializer.SerializeToUtf8Bytes(new
    {
        session_id = "SECRET-SESSION", hook_event_name = eventName,
        timestamp = DateTimeOffset.UtcNow.ToString("O"), prompt = "SECRET-PROMPT",
        tool_input = new { text = "SECRET-CODE" }, tool_response = "SECRET-OUTPUT",
        transcript_path = "SECRET-TRANSCRIPT"
    }));

    [Fact]
    public async Task RelayProbeUsesOnlyRunningClientPipeAndPersistsSanitizedMatchingEvidence()
    {
        var plan = CreateProbe();
        var transport = new RecordingTransport();
        await using var coordinator = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        await using var server = new ClientIpcServer(ConfigPath, coordinator.HandleAsync);
        var http = new DetectUnexpectedHttp();
        using var client = new HttpClient(http);
        var relay = new RelayEngine(client);
        coordinator.Start();
        await Eventually(() => coordinator.Status().State == "connected");
        try
        {
            Assert.Empty(RemoteConfiguration.Load(ConfigPath).Integrations);
            Assert.False((await ClientIpc.HookAsync(ConfigPath, AgentEvent.SessionStart,
                new("normal", DateTimeOffset.UtcNow, false,
                    Source: new("vscode", plan.Target.ScopeId, plan.Target.HostVersion)))).Accepted);
            Assert.Equal(0, await relay.RunAsync(Arguments(plan, "SessionStart", normalHook: true), Payload("SessionStart")));
            Assert.Equal(0, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);

            Assert.Equal(0, await relay.RunAsync(Arguments(plan, "SessionStart", scope: "wrong-scope"), Payload("SessionStart")));
            Assert.Equal(0, await relay.RunAsync(Arguments(plan, "SessionStart", version: "wrong-version"), Payload("SessionStart")));
            Assert.Equal(0, await relay.RunAsync(Arguments(plan, "SessionStart", probe: Guid.NewGuid()), Payload("SessionStart")));
            Assert.False((await ClientIpc.SendAsync(ConfigPath, new(ClientIpc.Version, "probe", AgentEvent.SessionStart,
                new("wrong-kind", DateTimeOffset.UtcNow, false,
                    Source: new("visual-studio", plan.Target.ScopeId, plan.Target.HostVersion)), ProbeId: plan.ProbeId))).Accepted);
            Assert.Equal(0, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);

            foreach (var eventName in HookAdapters.Events("vscode"))
                Assert.Equal(0, await relay.RunAsync(Arguments(plan, eventName), Payload(eventName)));
            var evidence = HookVerification.ReadEvidence(ConfigPath, plan.ProbeId);
            Assert.Equal(5, evidence.Acknowledgements);
            Assert.True(evidence.StableSessionObserved);
            Assert.Equal(0, http.Calls);
            Assert.DoesNotContain(transport.Reports, report => report.Kind == PresenceKind.Hook);
            Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(transport.Reports, Protocol.Json));
            var journalPath = Assert.Single(Directory.GetFiles(Path.Combine(root, "hook-verification"), "probe-*.json"));
            Assert.DoesNotContain("SECRET", File.ReadAllText(journalPath));
            Assert.DoesNotContain("SECRET", File.ReadAllText(RemotePaths.State(ConfigPath)));

            await coordinator.StopAsync();
            Assert.Equal(0, await relay.RunAsync(Arguments(plan, "SessionStart"), Payload("SessionStart")));
            Assert.Equal(5, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            Assert.Equal("stopping", coordinator.Status().State);
            Assert.Equal(0, http.Calls);
            Assert.DoesNotContain(transport.Reports, report => report.Kind == PresenceKind.Hook);
        }
        finally { HookVerification.Cancel(plan); }
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
    }

    [Fact]
    public async Task ProbeWithoutClientNeverLaunchesReporterOrUsesHttp()
    {
        var plan = CreateProbe();
        var http = new DetectUnexpectedHttp();
        using var client = new HttpClient(http);
        try
        {
            Assert.Equal(0, await new RelayEngine(client).RunAsync(Arguments(plan, "SessionStart"), Payload("SessionStart")));
            Assert.Equal(0, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            Assert.Equal(0, http.Calls);
            Assert.False(File.Exists(Path.Combine(root, "client-generation")));
            Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
        }
        finally { HookVerification.Cancel(plan); }
    }

    [Fact]
    public void FullServicingCleansPendingProbeWithoutOrphaningOrReplayingDiagnostics()
    {
        var plan = CreateProbe();
        var config = RemoteConfiguration.Load(ConfigPath);
        var configBytes = File.ReadAllBytes(ConfigPath);
        var manifestPath = Path.Combine(root, "integration.json");
        AtomicFile.Write(manifestPath, JsonSerializer.SerializeToUtf8Bytes(
            new IntegrationManifest(config.MachineId, "", "",
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(configBytes)), config.RelayPath!)
            { Version = 2 }, Protocol.Json));
        var settings = plan.Target.SettingsPath!;
        var installed = File.ReadAllText(settings);
        File.WriteAllText(settings, installed.Insert(installed.LastIndexOf('}'), "\n\"editor.fontSize\": 21,\n"));
        var manager = new IntegrationManager(new NoTasks(), (_, _) => Task.FromResult(false));
        var preview = manager.UninstallPreview(ConfigPath);
        Assert.Contains(plan.HookPath, preview);
        Assert.Contains(settings, preview);
        manager.PrepareUninstall(ConfigPath, "pending-probe-servicing");
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
        Assert.False(JsoncHookSettings.HasOwnedValue(File.ReadAllBytes(settings), plan.Target.HookDirectory));
        Assert.Contains("\"editor.fontSize\": 21", File.ReadAllText(settings));
        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(manifestPath));
        manager.RollbackUninstall(ConfigPath, "pending-probe-servicing");
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
        manager.Uninstall(ConfigPath);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(manifestPath));
        Assert.Contains("\"editor.fontSize\": 21", File.ReadAllText(settings));
    }

    [Fact]
    public void FullRemovalRefusesToDeleteModifiedDiagnosticHook()
    {
        var plan = CreateProbe();
        File.WriteAllText(plan.HookPath, "{\"user-modified\":true}");
        var manager = new IntegrationManager(new NoTasks(), (_, _) => Task.FromResult(false));
        Assert.Throws<InvalidDataException>(() => manager.Uninstall(ConfigPath));
        Assert.True(HookVerification.HasPendingCleanup(ConfigPath));
        Assert.Equal("{\"user-modified\":true}", File.ReadAllText(plan.HookPath));
        Assert.True(File.Exists(ConfigPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class DetectUnexpectedHttp : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }

    }

    private sealed class NoTasks : IIntegrationTaskScheduler
    {
        public string? ReadXml(string name) => null;
        public void Write(string name, string xml) => throw new InvalidOperationException("No task writes expected.");
        public void Delete(string name) => throw new InvalidOperationException("No task deletes expected.");
    }
}
