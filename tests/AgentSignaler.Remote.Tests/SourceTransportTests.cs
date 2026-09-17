using System.Net;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class SourceTransportTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private string ConfigPath => Path.Combine(_root, "remote.json");
    private SourceDescriptor Code => new("vscode", "profile-a", "1.137");
    private RemoteConfiguration Config => new RemoteConfiguration
    {
        Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test"
    }.ToVersion3() with
    {
        Version = 4,
        Integrations =
        [
            Target("vscode", "profile-a", ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop"]),
            Target("copilot-cli", "cli-a", ["sessionStart", "userPromptSubmitted", "sessionEnd"])
        ]
    };

    private IntegrationTarget Target(string kind, string scope, string[] events) => new()
    {
        Id = scope, Kind = kind, DisplayName = scope, InstallationId = scope,
        HostVersion = "1.137", AdapterVersion = HookAdapters.Version, ScopeId = scope,
        HookDirectory = Path.Combine(_root, scope), SupportedEvents = events,
        SettingsPath = kind == "vscode" ? Path.Combine(_root, scope, "settings.json") : null,
        Capability = IntegrationCapability.Verified, Provenance = "verified", Reason = ""
    };

    private byte[] CodePayload(string eventName = "Stop", string timestamp = "2026-09-15T11:00:00Z") =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            session_id = "shared", hook_event_name = eventName, timestamp,
            prompt = "SECRET-PROMPT", tool_name = "ask_user", tool_input = new { code = "SECRET-CODE" },
            tool_response = "SECRET failure", transcript_path = "SECRET-PATH"
        }));

    private void Save(RemoteConfiguration config)
    {
        if (!config.Integrations.Any(t => t.Kind != "copilot-cli"))
        {
            AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
            return;
        }
        var relay = config.RelayPath ?? Path.Combine(_root, "AgentSignaler.Relay.exe");
        if (!File.Exists(relay)) AtomicFile.Write(relay, []);
        var targets = config.Integrations.Select(target =>
        {
            if (target.Kind == "copilot-cli") return target;
            var executable = target.ExecutablePath ?? Path.Combine(_root, target.ScopeId + "-host.exe");
            if (!File.Exists(executable)) AtomicFile.Write(executable, [1, 2, 3]);
            return target with { ExecutablePath = executable };
        }).ToList();
        config = config with { RelayPath = relay, Integrations = targets };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (target.Kind == "copilot-cli") continue;
            var plan = HookVerification.Preview(target, ConfigPath, relay);
            HookVerification.Begin(plan, consent: true);
            try
            {
                foreach (var name in HookAdapters.Events(target.Kind))
                    Assert.True(HookVerification.AcceptProbe(ConfigPath, plan.ProbeId,
                        HookPayloadAdapters.Event(target.Kind, name),
                        new("isolated-fixture-session", _now, false,
                            Source: new(target.Kind, target.ScopeId, target.HostVersion))));
                targets[i] = HookVerification.Complete(plan,
                    new(true, true, true, true, "synthetic test fixture", "fixture only"));
            }
            finally { HookVerification.Cancel(plan); }
        }
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config with { Integrations = targets }, Protocol.Json));
    }

    [Theory]
    [InlineData("SessionStart", AgentEvent.SessionStart)]
    [InlineData("UserPromptSubmit", AgentEvent.UserPromptSubmitted)]
    [InlineData("PreToolUse", AgentEvent.PreToolUse)]
    [InlineData("PostToolUse", AgentEvent.PostToolUse)]
    [InlineData("Stop", AgentEvent.ExecutionStopped)]
    public void CodeEventsUseExactDocumentedSchemaWithoutCliToolGuesses(string name, AgentEvent expected)
    {
        Assert.Equal(expected, HookPayloadAdapters.Event("vscode", name));
        var hook = HookPayloadAdapters.Parse("vscode", name, CodePayload(name), _now, Code);
        Assert.Equal("shared", hook.SessionId);
        Assert.Equal(Code, hook.Source);
        Assert.Equal(_now.AddHours(-1), hook.Timestamp);
        Assert.False(hook.ToolFailed);
        Assert.False(hook.ToolRequiresUserInput);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(hook, Protocol.Json));
    }

    [Theory]
    [InlineData("2026-09-15T13:00:00+02:00")]
    [InlineData("2026-09-15T11:00:00.000Z")]
    public void IsoTimestampsNormalizeToUtc(string timestamp) =>
        Assert.Equal(_now.AddHours(-1), HookPayloadAdapters.Parse("vscode", "Stop",
            CodePayload(timestamp: timestamp), _now, Code).Timestamp);

    [Theory]
    [InlineData("2026-09-15")]
    [InlineData("2026-09-15T11:00:00")]
    [InlineData("1789460000000")]
    public void CodeRejectsGuessedOrLocalTimestamps(string timestamp) =>
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Parse("vscode", "Stop",
            CodePayload(timestamp: timestamp), _now, Code));

    [Fact]
    public void MissingIdentityAndCrossHostSchemaFailClosed()
    {
        Assert.Throws<MissingHookSessionException>(() => HookPayloadAdapters.Parse("vscode", "Stop",
            Encoding.UTF8.GetBytes("""{"timestamp":"2026-09-15T11:00:00Z","hook_event_name":"Stop"}"""), _now, Code));
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Parse("vscode", "SessionStart",
            CodePayload(), _now, Code));
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Event("vscode", "stop"));
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Event("vscode", "SubagentStop"));
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Event("vscode", "PreCompact"));
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Event("copilot-cli", "executionStopped"));
        Assert.Throws<InvalidDataException>(() => HookPayloadAdapters.Parse("copilot-cli", "sessionStart",
            CodePayload(), _now, Code));
    }

    [Theory]
    [InlineData("PreToolUse", AgentEvent.PreToolUse)]
    [InlineData("PostToolUse", AgentEvent.PostToolUse)]
    public void CodePreservesOnlyDocumentedBoundedToolInvocationIdentifier(string name, AgentEvent kind)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            session_id = "shared", hook_event_name = name, timestamp = "2026-09-15T11:00:00Z",
            tool_use_id = "opaque-tool-123", toolUseId = "SECRET-ALIAS", tool_input = "SECRET-CONTENT"
        });
        var hook = HookPayloadAdapters.Parse("vscode", name, payload, _now, Code);
        Assert.Equal("opaque-tool-123", hook.InvocationId);
        Assert.True(HookPayloadAdapters.IsSanitized(kind, hook));
        Assert.False(HookPayloadAdapters.IsSanitized(kind, hook with { InvocationId = new string('x', 129) }));
        Assert.False(HookPayloadAdapters.IsSanitized(kind, hook with { Source = SourceDescriptor.LegacyCli }));
        Assert.False(HookPayloadAdapters.IsSanitized(AgentEvent.ExecutionStopped, hook));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(hook, Protocol.Json));
    }

    [Fact]
    public void CliDoesNotGuessToolInvocationAliases()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sessionId = "cli", timestamp = _now.ToUnixTimeMilliseconds(),
            tool_use_id = "SECRET", toolUseId = "SECRET"
        });
        Assert.Null(HookParser.Parse(payload, AgentEvent.PreToolUse, _now).InvocationId);
    }

    [Fact]
    public async Task ToolOperationIdentifierSurvivesIpcWithoutAssumingCallbacksAreDuplicates()
    {
        Save(Config);
        var transport = new RecordingTransport();
        var identifiers = new System.Collections.Concurrent.ConcurrentQueue<string?>();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        await using var server = new ClientIpcServer(ConfigPath, (request, token) =>
        {
            identifiers.Enqueue(request.Hook?.InvocationId);
            return runtime.HandleAsync(request, token);
        });
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var hook = new HookData("tool-session", _now, false, Source: Code, InvocationId: "tool-operation");
        Assert.True((await ClientIpc.HookAsync(ConfigPath, AgentEvent.PreToolUse, hook)).Accepted);
        Assert.True((await ClientIpc.HookAsync(ConfigPath, AgentEvent.PreToolUse, hook)).Accepted);
        await Eventually(() => transport.Reports.Count(r => r.Kind == PresenceKind.Hook) == 2);
        Assert.Equal(new[] { "tool-operation", "tool-operation" }, identifiers);
    }

    [Fact]
    public void CompositeStorageMigratesLegacyAndKeepsOrderingIndependent()
    {
        var path = RemotePaths.State(ConfigPath);
        var legacy = StateReducer.Apply(null, "shared", AgentEvent.UserPromptSubmitted, _now, _now);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(
            new StoredRemoteState(1, _now, [new(legacy, _now)]), Protocol.Json));
        var store = new SessionStore(path, new DiagnosticLog(RemotePaths.Log(ConfigPath)));
        var codeHook = new HookData("shared", _now.AddSeconds(-1), false, Source: Code);
        var report = store.UpdateReport(AgentEvent.SessionStart, codeHook, _now.AddSeconds(1));
        Assert.True(report.Accepted);
        Assert.Equal(2, report.Sessions.Count);
        Assert.Contains(report.Sessions, s => s.Source == SourceDescriptor.LegacyCli && s.UnderlyingState == AgentState.Executing);
        Assert.Contains(report.Sessions, s => s.Source == Code && s.UnderlyingState == AgentState.Waiting);
        Assert.False(store.UpdateReport(AgentEvent.UserPromptSubmitted,
            codeHook with { Timestamp = _now.AddSeconds(-2) }, _now.AddSeconds(2)).Accepted);
        var stopped = store.UpdateReport(AgentEvent.ExecutionStopped,
            codeHook with { Timestamp = _now }, _now.AddSeconds(3));
        Assert.Null(stopped.Sessions.Single(s => s.Source == Code).ResultState);
        Assert.True(stopped.ReportedAtUtc > report.ReportedAtUtc);
        var saved = JsonSerializer.Deserialize<StoredRemoteState>(File.ReadAllBytes(path), Protocol.Json)!;
        Assert.Equal(2, saved.Version);
        Assert.Equal(2, saved.Sessions.Count);
    }

    [Fact]
    public async Task RealRelayPipeProducesV4AndRejectsDisabledScopesAndProbeMisuse()
    {
        Save(Config);
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        await using var server = new ClientIpcServer(ConfigPath, runtime.HandleAsync);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var handler = new RecordingHttpHandler(3);
        using var http = new HttpClient(handler);
        var args = new[] { "hook", "--config", ConfigPath, "--source", "vscode", "--scope", "profile-a",
            "--source-version", "1.137", "--adapter", "vscode", "--event", "Stop" };
        Assert.Equal(0, await new RelayEngine(http).RunAsync(args, new MemoryStream(CodePayload())));
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Hook));
        var report = transport.Reports.Single(r => r.Kind == PresenceKind.Hook);
        Assert.Equal(PresenceProtocol.EnrichedVersion, report.ProtocolVersion);
        Assert.Equal("agent-signaler", report.Client);
        Assert.Equal(Code, report.Hook!.Source);
        Assert.Equal("vscode", report.Hook.Client);
        Assert.Equal(Code.Version, report.Hook.ClientVersion);
        Assert.Equal(3, report.Hook.ProtocolVersion);
        Assert.Equal(AgentEvent.ExecutionStopped, report.Hook.Event);
        Assert.Empty(PresenceProtocol.Validate(report));
        Assert.Empty(handler.Paths);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(transport.Reports, Protocol.Json));
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("bad", _now, false,
            Source: Code with { ScopeId = "disabled" })).Accepted);
        Assert.False(runtime.AcceptHook(AgentEvent.AgentStop, new("bad", _now, false, Source: Code)).Accepted);
        Assert.False(runtime.AcceptHook(AgentEvent.PostToolUse, new("bad", _now, true, Source: Code)).Accepted);
        Assert.False((await runtime.HandleAsync(new(ClientIpc.Version, "status", ProbeId: Guid.NewGuid()))).Accepted);
        Assert.False((await runtime.HandleAsync(new(ClientIpc.Version, "probe", AgentEvent.SessionStart,
            new("probe", _now, false, Source: Code)))).Accepted);
        await runtime.StopAsync();
        Assert.False((await runtime.HandleAsync(new(ClientIpc.Version, "probe", AgentEvent.SessionStart,
            new("probe", _now, false, Source: Code), ProbeId: Guid.NewGuid()))).Accepted);
    }

    [Fact]
    public async Task AutomaticallyConfiguredIdeReportsWithoutACompatibilityRecord()
    {
        var target = AutomaticHookConfiguration.Prepare(Config.Integrations[0] with
        {
            Capability = IntegrationCapability.VerificationRequired, SupportedEvents = []
        }, ConfigPath);
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Config with { Integrations = [target] }, Protocol.Json));
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.True(runtime.AcceptHook(AgentEvent.ExecutionStopped, new("automatic-session", _now, false, Source: Code)).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Hook));
        Assert.Equal(Code, transport.Reports.Single(r => r.Kind == PresenceKind.Hook).Hook!.Source);
        Assert.False(runtime.AcceptHook(AgentEvent.ExecutionStopped,
            new("disabled", _now, false, Source: Code with { ScopeId = "unchecked" })).Accepted);
        Assert.False(HookVerification.HasPendingCleanup(ConfigPath));
    }

    [Fact]
    public async Task LegacyCliAllowedOnlyWhenSelectedAndV3ConfigRejectsIdeOrigins()
    {
        var config = Config with { Integrations = [Target("vscode", "profile-a", ["SessionStart"])] };
        Save(config);
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport());
        runtime.Start();
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("legacy", _now, false)).Accepted);
        Assert.True(HookPayloadAdapters.IsAllowed(Config, AgentEvent.SessionStart, null));
        Assert.False(HookPayloadAdapters.IsAllowed(Config, AgentEvent.AgentStop, null));
        var legacyConfig = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid() }.ToVersion3();
        Assert.True(HookPayloadAdapters.IsAllowed(legacyConfig, AgentEvent.SessionStart, null));
        Assert.False(HookPayloadAdapters.IsAllowed(legacyConfig, AgentEvent.SessionStart, Code));
    }

    [Fact]
    public async Task LegacyArgumentsOnV4UseCanonicalLegacySourceVersion()
    {
        Save(Config);
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("legacy", _now, false)).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Hook));
        var report = transport.Reports.Single(r => r.Kind == PresenceKind.Hook);
        Assert.Equal(SourceDescriptor.LegacyCli, report.Hook!.Source);
        Assert.Equal(SourceDescriptor.LegacyCli.Version, report.Hook.ClientVersion);
        Assert.Empty(PresenceProtocol.Validate(report));
    }

    [Fact]
    public async Task ReloadDisablingScopeStopsItsHooksAndRemovesItFromHeartbeat()
    {
        var config = Config;
        Save(config);
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("active", _now, false, Source: Code)).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Sessions?.Any(s => s.Source == Code) == true));
        Save(config with { Integrations = config.Integrations.Where(t => t.Kind != "vscode").ToList() });
        Assert.True((await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath))).Accepted);
        Assert.False(runtime.AcceptHook(AgentEvent.ExecutionStopped, new("active", _now.AddSeconds(1), false, Source: Code)).Accepted);
        Assert.Empty(transport.Reports.Last(r => r.Kind == PresenceKind.Heartbeat).Sessions!);
    }

    [Fact]
    public async Task VisualStudioNormalHooksRequireSharedMetadataInsteadOfClaimingAnInstanceVersion()
    {
        var target = Target("visual-studio", "vs-shared", ["sessionStart"]);
        Save(Config with { Integrations = [target] });
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var source = new SourceDescriptor("visual-studio", target.ScopeId, target.HostVersion);
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("vs", _now, false, Source: source)).Accepted);
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart,
            new("vs", _now, false, Source: source with { ScopeId = "other", Version = "shared" })).Accepted);
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart,
            new("vs", _now, false, Source: source with { Version = "shared" })).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Kind == PresenceKind.Hook));
        var hook = transport.Reports.Single(r => r.Kind == PresenceKind.Hook).Hook!;
        Assert.Equal("visual-studio", hook.Client);
        Assert.Equal("shared", hook.ClientVersion);
        Assert.Equal("shared", hook.Source!.Version);
    }

    [Fact]
    public async Task ChangingVerifiedIdeBinaryWhileClientRunsRejectsFurtherHooksAndSnapshots()
    {
        Save(Config);
        var target = RemoteConfiguration.Load(ConfigPath).Integrations.Single(t => t.Kind == "vscode");
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("before-update", _now, false, Source: Code)).Accepted);
        await Eventually(() => transport.Reports.Any(r => r.Sessions?.Any(s => s.Source == Code) == true));
        File.AppendAllText(target.ExecutablePath!, "changed fixture binary");
        var rejected = runtime.AcceptHook(AgentEvent.SessionStart, new("after-update", _now.AddSeconds(1), false, Source: Code));
        Assert.False(rejected.Accepted);
        Assert.Contains("verification", rejected.Error!);
        var before = transport.Reports.Count;
        runtime.RequestSnapshot();
        await Eventually(() => transport.Reports.Count > before);
        Assert.Empty(transport.Reports.Last(r => r.Kind == PresenceKind.Heartbeat).Sessions!);
    }

    [Fact]
    public async Task ClaimedVerifiedConfigWithoutCompatibilityEvidenceCannotReportIdeHooks()
    {
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Config, Protocol.Json));
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport());
        runtime.Start();
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("unverified", _now, false, Source: Code)).Accepted);
    }

    [Fact]
    public async Task SharedVisualStudioScopeRequiresEverySelectedHostToRemainVerified()
    {
        var first = Target("visual-studio", "shared-vs", ["sessionStart"]) with
        {
            Id = "vs-first", ExecutablePath = Path.Combine(_root, "vs-first.exe")
        };
        var second = first with
        {
            Id = "vs-second", InstallationId = "second", HostVersion = "2",
            ExecutablePath = Path.Combine(_root, "vs-second.exe")
        };
        Save(Config with { Integrations = [first, second] });
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport());
        runtime.Start();
        var source = new SourceDescriptor("visual-studio", first.ScopeId, "shared");
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("before", _now, false, Source: source)).Accepted);
        File.AppendAllText(second.ExecutablePath!, "upgraded second fixture");
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("after", _now, false, Source: source)).Accepted);
    }

    [Fact]
    public async Task MultiTargetConfigurationBeyondLegacySizeLimitStartsAndReloads()
    {
        var config = Config with
        {
            Integrations = Enumerable.Range(0, 24)
                .Select(i => Target("copilot-cli", "cli-" + i, ["sessionStart"])).ToList()
        };
        Save(config);
        Assert.True(new FileInfo(ConfigPath).Length > 8192);
        await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => new RecordingTransport());
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Save(config with { HeartbeatIntervalSeconds = 600 });
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ConfigPath)));
        Assert.True((await runtime.ReloadAsync(revision)).Accepted);
        Assert.Equal(600, runtime.Status().HeartbeatIntervalSeconds);
    }

    [Fact]
    public void StoredVersionsAreMetadataAndProfilesNeverCollide()
    {
        var store = new SessionStore(RemotePaths.State(ConfigPath), new DiagnosticLog(RemotePaths.Log(ConfigPath)));
        var first = new HookData("shared", _now, false, Source: Code);
        store.UpdateReport(AgentEvent.SessionStart, first, _now);
        store.UpdateReport(AgentEvent.SessionStart,
            first with { Source = Code with { ScopeId = "profile-b" } }, _now.AddSeconds(1));
        var result = store.UpdateReport(AgentEvent.UserPromptSubmitted,
            first with { Source = Code with { Version = "new" }, Timestamp = _now.AddSeconds(1) }, _now.AddSeconds(2));
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal(AgentState.Executing, result.Sessions.Single(s => s.Source!.ScopeId == "profile-a").UnderlyingState);
        Assert.Equal(AgentState.Waiting, result.Sessions.Single(s => s.Source!.ScopeId == "profile-b").UnderlyingState);
    }

    [Theory]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    public async Task TransportUsesMatchingHealthAndReportProtocol(int configVersion, int protocolVersion)
    {
        var config = configVersion == 4 ? Config : new RemoteConfiguration
        {
            Host = "localhost", MachineId = Guid.NewGuid(), ClientVersion = "test"
        }.ToVersion3();
        var handler = new RecordingHttpHandler(protocolVersion);
        using var client = new HttpClient(handler);
        using var transport = new PresenceTransport(config, client);
        var report = new PresenceReport
        {
            ProtocolVersion = protocolVersion, Client = protocolVersion == 3 ? "agent-signaler" : "copilot-cli",
            Kind = PresenceKind.Started, EventId = Guid.NewGuid(), MachineId = config.MachineId,
            MachineName = config.MachineName, ClientVersion = config.ClientVersion, Generation = 1, Sequence = 1,
            ReportedAtUtc = _now, HeartbeatIntervalSeconds = 300, Sessions = []
        };
        Assert.True(await transport.SendAsync(report, CancellationToken.None));
        Assert.Equal(new[] { "/api/v4/health", $"/api/v{protocolVersion}/health", $"/api/v{protocolVersion}/reports" }, handler.Paths);
    }

    [Fact]
    public async Task V4DoesNotFallBackToLegacyDashboard()
    {
        var handler = new RecordingHttpHandler(2);
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => DashboardConnection.TestAsync(Config, client, CancellationToken.None));
        Assert.Equal(new[] { "/api/v4/health", "/api/v3/health" }, handler.Paths);
    }

    [Fact]
    public async Task MissingStableIdOnlyProducesBoundedDiagnosticAndNoNetwork()
    {
        Save(Config);
        var handler = new RecordingHttpHandler(3);
        using var client = new HttpClient(handler);
        var args = new[] { "hook", "--config", ConfigPath, "--source", "vscode", "--scope", "profile-a",
            "--source-version", "1.137", "--adapter", "vscode", "--event", "Stop" };
        Assert.Equal(0, await new RelayEngine(client).RunAsync(args,
            new MemoryStream(Encoding.UTF8.GetBytes("""{"prompt":"SECRET"}"""))));
        Assert.Empty(handler.Paths);
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
        var log = await File.ReadAllTextAsync(RemotePaths.Log(ConfigPath));
        Assert.Contains("hook-missing-stable-session", log);
        Assert.DoesNotContain("SECRET", log);
    }

    [Fact]
    public async Task DiagnosticProbeAcknowledgesCandidateWithoutSessionOrReportingMutation()
    {
        var relayPath = Path.Combine(_root, "AgentSignaler.Relay.exe");
        AtomicFile.Write(relayPath, []);
        var config = Config with { RelayPath = relayPath, Integrations = [] };
        Save(config);
        var idePath = Path.Combine(_root, "Code.exe");
        AtomicFile.Write(idePath, []);
        var candidate = Target("vscode", "profile-a", []) with
        {
            Capability = IntegrationCapability.VerificationRequired, ExecutablePath = idePath
        };
        var plan = HookVerification.Preview(candidate, ConfigPath, relayPath);
        HookVerification.Begin(plan, consent: true);
        try
        {
            var transport = new RecordingTransport();
            await using var runtime = new ClientCoordinator(ConfigPath, transportFactory: _ => transport);
            await using var server = new ClientIpcServer(ConfigPath, runtime.HandleAsync);
            runtime.Start();
            await Eventually(() => runtime.Status().State == "connected");
            Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("unselected", _now, false, Source: Code)).Accepted);
            Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("legacy-unselected", _now, false)).Accepted);
            var stateBefore = File.ReadAllBytes(RemotePaths.State(ConfigPath));
            var args = new[] { "hook", "--config", ConfigPath, "--source", "vscode", "--scope", "profile-a",
                "--source-version", "1.137", "--adapter", "vscode", "--event", "Stop", "--probe", plan.ProbeId.ToString("D") };
            var handler = new RecordingHttpHandler(3);
            using var client = new HttpClient(handler);
            Assert.Equal(0, await new RelayEngine(client).RunAsync(args, new MemoryStream(CodePayload())));
            Assert.Empty(handler.Paths);
            Assert.Equal(1, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
            Assert.Equal(stateBefore, File.ReadAllBytes(RemotePaths.State(ConfigPath)));
            Assert.DoesNotContain(transport.Reports, r => r.Kind == PresenceKind.Hook);
            Assert.False((await runtime.HandleAsync(new(ClientIpc.Version, "probe", AgentEvent.ExecutionStopped,
                new("shared", _now, false, Source: Code), ProbeId: Guid.NewGuid()))).Accepted);
            Assert.False((await ClientIpc.SendAsync(ConfigPath, new(ClientIpc.Version, "probe", AgentEvent.ExecutionStopped,
                new("shared", _now, false, Source: Code with { ScopeId = "other-profile" }), ProbeId: plan.ProbeId))).Accepted);
            Assert.False((await ClientIpc.SendAsync(ConfigPath, new(ClientIpc.Version, "probe", AgentEvent.ExecutionStopped,
                new("shared", _now, false, Source: Code with { Version = "changed" }), ProbeId: plan.ProbeId))).Accepted);
            await runtime.StopAsync();
            Assert.Equal(0, await new RelayEngine().RunAsync(args, new MemoryStream(CodePayload())));
            Assert.Equal(1, HookVerification.ReadEvidence(ConfigPath, plan.ProbeId).Acknowledgements);
        }
        finally { HookVerification.Cancel(plan); }
    }

    private sealed class RecordingHttpHandler(int healthVersion) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath != $"/api/v{healthVersion}/health")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(request.Method == HttpMethod.Get
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"protocolVersion":{{healthVersion}},"status":"ok"}""", Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
