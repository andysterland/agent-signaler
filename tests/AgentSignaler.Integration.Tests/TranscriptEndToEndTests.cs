using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Remote;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class TranscriptEndToEndTests : IAsyncLifetime
{
    internal const string ForbiddenArguments = "SYNTHETIC-FORBIDDEN-TOOL-ARGUMENTS";
    internal const string ForbiddenResult = "SYNTHETIC-FORBIDDEN-TOOL-RESULT";
    internal const string ForbiddenReasoning = "SYNTHETIC-FORBIDDEN-HIDDEN-REASONING";
    private const string Prompt = "SYNTHETIC-USER: Mira Example <mira@example.invalid> 👩‍💻 <script>inert</script>";
    private const string Assistant = "SYNTHETIC-ASSISTANT: Mira Example, your fictional appointment is ready. 🌍";
    private const string Historical = "SYNTHETIC-HISTORY-MUST-NOT-BACKFILL";
    private const string FileUser = "SYNTHETIC-FILE-USER-MUST-NOT-CAPTURE";
    private const string Suppressed = "SYNTHETIC-OPTED-OUT-MESSAGE";
    private const string Session = "synthetic-session";
    private static readonly SourceDescriptor Source = new("copilot-cli", "test-only-transcript-scope", "test-only-v1");
    private readonly string _root = Path.Combine(Directory.GetCurrentDirectory(), "transcript-e2e", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Path, byte[] Body)> _wire = new();
    private string ConfigPath => Path.Combine(_root, "remote.json");
    private string HostRoot => Path.Combine(_root, "host-input-fixtures");
    private string HostFile => Path.Combine(HostRoot, Session + ".synthetic-jsonl");
    private string _relay = "";
    private Uri _listener = null!;
    private RemoteConfiguration _configuration = null!;
    private MachineStore _machines = null!;
    private DashboardServer _dashboard = null!;
    private LoopbackTlsDashboard? _tls;
    private ClientCoordinator? _client;
    private ClientIpcServer? _ipc;
    private HttpClient? _presenceClient;
    private SyntheticTranscriptFileAdapter _adapter = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(HostRoot);
        _relay = Path.ChangeExtension((await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "relay-path.txt"))).Trim(), ".exe");
        Assert.True(File.Exists(_relay));
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener = new Uri($"http://127.0.0.1:{port}/");
        _machines = new MachineStore(Path.Combine(_root, "dashboard.db"));
        _dashboard = new DashboardServer(_machines, port, options: new()
        {
            ListenerMode = DashboardListenerMode.Internet,
            TranscriptTunnelReady = true
        });
        await _dashboard.StartAsync();
        _configuration = new RemoteConfiguration
        {
            Version = 5, DashboardBaseUrl = _listener.AbsoluteUri, MachineId = Guid.NewGuid(),
            MachineName = "SYNTHETIC-TRANSCRIPT-E2E", ClientVersion = "test", RelayPath = _relay,
            Integrations =
            [
                new()
                {
                    Id = "test-only-cli", Kind = Source.Kind, ScopeId = Source.ScopeId,
                    DisplayName = "Synthetic CLI hook input", InstallationId = "test-only-installation",
                    HostVersion = Source.Version, HookDirectory = Path.Combine(_root, "unused-hook-target"),
                    Capability = IntegrationCapability.Configured, SupportedEvents = HookAdapters.Events(Source.Kind),
                    Provenance = "synthetic-test-only"
                }
            ]
        };
        _adapter = new(HostRoot, Source);
        await File.WriteAllTextAsync(HostFile, _adapter.Frame(Session, "historical", Historical), new UTF8Encoding(false));
    }

    [Fact]
    public async Task ActualRelayToViewerPreservesAllowedHookTextAndOnlyNewCompletedAssistantFileReplies()
    {
        await StartAsync();
        var capability = await WaitForNegotiationAsync(enabled: true);
        Assert.True(capability.AssistantFile);
        Assert.Equal(_adapter.ProfileId, capability.FormatProfileId);
        using var viewer = new TranscriptViewController(_dashboard.Transcripts);
        await viewer.ShowAsync(_configuration.MachineId, offline: false);
        Assert.Empty(viewer.State.Entries);
        Assert.Equal(0, _adapter.ParsedRecords);
        viewer.Hide();

        await HookAsync("userPromptSubmitted", Prompt);
        await WaitForEventsAsync(events => events.Any(value => value.Payload is TranscriptMessage { Text: Prompt }));
        await HookAsync("preToolUse", toolName: "synthetic_tool");
        await WaitForEventsAsync(events => events.Any(value => value.Payload is TranscriptToolActivity));
        await HookAsync("agentStop", transcriptPath: HostFile);
        var baseline = await WaitForEventsAsync(events => events.Any(value =>
            value.Payload is TranscriptGap { Reason: TranscriptGapReason.BaselineEstablished }));
        Assert.DoesNotContain(baseline, value => value.Payload is TranscriptMessage { Role: TranscriptRole.Assistant });
        var original = await File.ReadAllBytesAsync(HostFile);
        Assert.Equal(Encoding.UTF8.GetBytes(_adapter.Frame(Session, "historical", Historical)), original);

        var suffix = _adapter.Frame(Session, "file-user", FileUser, role: "user") +
            _adapter.Frame(Session, "tool", ForbiddenResult, role: "tool") +
            _adapter.Frame(Session, "reasoning", ForbiddenReasoning, audience: "internal") +
            _adapter.Frame(Session, "subagent", ForbiddenResult, audience: "subagent") +
            _adapter.Frame(Session, "delta", ForbiddenReasoning, complete: false) +
            _adapter.Frame(Session, "new-assistant", Assistant);
        await File.AppendAllTextAsync(HostFile, suffix, new UTF8Encoding(false));
        var expectedHostBytes = await File.ReadAllBytesAsync(HostFile);
        var parsedBeforeOpening = _adapter.ParsedRecords;
        viewer.Hide();
        await viewer.ShowAsync(_configuration.MachineId, false);
        await viewer.RefreshAsync();
        Assert.Equal(parsedBeforeOpening, _adapter.ParsedRecords);
        Assert.DoesNotContain(viewer.State.Entries, entry => entry.Text == Assistant);
        viewer.Hide();

        await HookAsync("agentStop", transcriptPath: HostFile);
        var events = await WaitForEventsAsync(values => values.Any(value =>
            value.Payload is TranscriptMessage { Role: TranscriptRole.Assistant }));
        var reply = Assert.Single(events, value => value.Payload is TranscriptMessage { Role: TranscriptRole.Assistant });
        var message = Assert.IsType<TranscriptMessage>(reply.Payload);
        Assert.Equal(Assistant, message.Text);
        Assert.Equal("new-assistant", message.MessageId);
        Assert.Equal(TranscriptMessageIdOrigin.Host, message.MessageIdOrigin);
        Assert.Equal(TranscriptMessageAvailability.Complete, message.Availability);
        Assert.Equal(CaptureOrigin.TranscriptFile, reply.Provenance.CaptureOrigin);
        Assert.Equal(_adapter.ProfileId, reply.Provenance.FormatProfileId);
        Assert.NotNull(reply.Provenance.TriggeringCaptureId);
        var user = Assert.Single(events, value => value.Payload is TranscriptMessage { Role: TranscriptRole.User });
        Assert.Equal(Prompt, Assert.IsType<TranscriptMessage>(user.Payload).Text);
        Assert.Equal(CaptureOrigin.Hook, user.Provenance.CaptureOrigin);
        var tool = Assert.Single(events.Select(value => value.Payload).OfType<TranscriptToolActivity>());
        Assert.Equal("synthetic_tool", tool.ToolName);
        Assert.Equal(TranscriptToolPhase.Requested, tool.Phase);
        Assert.Equal(TranscriptToolOutcome.Requested, tool.Outcome);
        Assert.Null(tool.InvocationId);
        Assert.All(events, value =>
        {
            Assert.Equal(_configuration.MachineId, value.MachineId);
            Assert.Equal(Source, value.Source);
            Assert.Equal(Session, value.SessionId);
            Assert.Equal(TranscriptOrdering.Arrival, value.Provenance.Ordering);
            Assert.True(TranscriptProtocol.Validate(value));
        });
        Assert.True(user.Sequence < reply.Sequence);
        Assert.Equal(events.Select(value => value.Sequence).Order(), events.Select(value => value.Sequence));

        var stopCount = events.Count(IsStop);
        await HookAsync("agentStop", transcriptPath: HostFile);
        await WaitForEventsAsync(values => values.Count(IsStop) == stopCount + 1);
        await HookAsync("sessionEnd");
        events = await WaitForEventsAsync(values => values.Any(value =>
            value.Payload is TranscriptLifecycle { Signal: TranscriptLifecycleSignal.SessionEnded }));
        Assert.Single(events, value => value.Payload is TranscriptMessage { Role: TranscriptRole.Assistant });
        Assert.Equal(expectedHostBytes, await File.ReadAllBytesAsync(HostFile));
        await viewer.ShowAsync(_configuration.MachineId, false);
        Assert.Equal(Source, viewer.State.Selection!.Source);
        Assert.Equal(Session, viewer.State.Selection.SessionId);
        Assert.Contains(viewer.State.Entries, entry => entry.Text == Prompt);
        Assert.Contains(viewer.State.Entries, entry => entry.Text == Assistant &&
            entry.Header.Contains("Transcript file", StringComparison.Ordinal));
        Assert.Contains(viewer.State.Entries, entry => entry.Text.Contains("synthetic_tool", StringComparison.Ordinal));
        Assert.NotEmpty(viewer.State.Sessions);
        Assert.All(viewer.State.Entries, entry => AssertExcluded(entry.Header + entry.Text));
        viewer.Hide();
        Assert.Empty(viewer.State.Entries);
        Assert.Empty(viewer.State.Sessions);
        AssertWirePrivacy();
        await AssertPersistentPrivacyAsync();
    }

    [Fact]
    public async Task OptOutPurgesViewerKeepsStatusAndReenableDoesNotBackfillHostFile()
    {
        await StartAsync();
        await WaitForNegotiationAsync(true);
        await HookAsync("userPromptSubmitted", Prompt);
        await WaitForEventsAsync(events => events.Any(value => value.Payload is TranscriptMessage));
        await HookAsync("agentStop", transcriptPath: HostFile);
        await WaitForEventsAsync(events => events.Any(value =>
            value.Payload is TranscriptGap { Reason: TranscriptGapReason.BaselineEstablished }));
        using var viewer = new TranscriptViewController(_dashboard.Transcripts);
        await viewer.ShowAsync(_configuration.MachineId, false);
        Assert.Contains(viewer.State.Entries, entry => entry.Text == Prompt);

        _configuration = _configuration with { DetailedReportingEnabled = false };
        SaveConfiguration();
        var reloaded = await ClientIpc.SendAsync(ConfigPath, new(ClientIpc.Version, "transcript-reload",
            ExpectedRevision: ClientConfigurationRevision.Read(ConfigPath)));
        Assert.True(reloaded.Accepted);
        await WaitForNegotiationAsync(false);
        await WaitUntilAsync(() => Task.FromResult(_dashboard.Transcripts.RetainedEventCount == 0));
        Assert.Empty(viewer.State.Entries);
        viewer.Hide();
        var sendsAtOptOut = DetailEventRequests;
        var parsedAtOptOut = _adapter.ParsedRecords;
        await File.AppendAllTextAsync(HostFile, _adapter.Frame(Session, "opted-out", Suppressed), new UTF8Encoding(false));
        var expectedHostBytes = await File.ReadAllBytesAsync(HostFile);
        await HookAsync("userPromptSubmitted", Suppressed);
        await WaitForMachineAsync(AgentEvent.UserPromptSubmitted);
        await HookAsync("agentStop", transcriptPath: HostFile);
        await WaitForMachineAsync(AgentEvent.AgentStop);
        Assert.Equal(sendsAtOptOut, DetailEventRequests);
        Assert.Equal(parsedAtOptOut, _adapter.ParsedRecords);
        Assert.Equal(expectedHostBytes, await File.ReadAllBytesAsync(HostFile));

        _configuration = _configuration with { DetailedReportingEnabled = true };
        SaveConfiguration();
        Assert.True((await ClientIpc.SendAsync(ConfigPath, new(ClientIpc.Version, "transcript-reload",
            ExpectedRevision: ClientConfigurationRevision.Read(ConfigPath)))).Accepted);
        await WaitForNegotiationAsync(true);
        await HookAsync("agentStop", transcriptPath: HostFile);
        var afterEnable = await WaitForEventsAsync(events => events.Any(value =>
            value.Payload is TranscriptGap { Reason: TranscriptGapReason.BaselineEstablished }));
        Assert.DoesNotContain(afterEnable, value => value.Payload is TranscriptMessage);
        Assert.Equal(expectedHostBytes, await File.ReadAllBytesAsync(HostFile));
        AssertWirePrivacy();
        Assert.DoesNotContain(_wire, request => Encoding.UTF8.GetString(request.Body).Contains(Suppressed, StringComparison.Ordinal));

        Assert.True((await ClientIpc.StopAsync(ConfigPath)).Accepted);
        await WaitUntilAsync(async () => (await _machines.GetMachinesAsync()).Single().ExplicitOffline);
        var state = await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath));
        var sendsAtExit = DetailEventRequests;
        await HookAsync("userPromptSubmitted", Suppressed);
        Assert.Equal(sendsAtExit, DetailEventRequests);
        Assert.Equal(state, await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath)));
        await AssertPersistentPrivacyAsync();
    }

    [Fact]
    public async Task ProductionUnverifiedFormatRetainsHookCaptureWithoutReadingOrClaimingAssistantSupport()
    {
        await StartAsync(useTestAdapter: false);
        var negotiation = await WaitForNegotiationAsync(true);
        Assert.False(negotiation.AssistantFile);
        Assert.Null(negotiation.FormatProfileId);
        Assert.Equal("format-unverified", negotiation.Availability);
        var original = await File.ReadAllBytesAsync(HostFile);
        await HookAsync("userPromptSubmitted", Prompt);
        await WaitForEventsAsync(events => events.Any(value => value.Payload is TranscriptMessage));
        await HookAsync("agentStop", transcriptPath: HostFile);
        var events = await WaitForEventsAsync(values => values.Any(value =>
            value.Payload is TranscriptGap { Reason: TranscriptGapReason.FormatUnverified }));
        Assert.Single(events, value => value.Payload is TranscriptMessage { Role: TranscriptRole.User });
        Assert.DoesNotContain(events, value => value.Payload is TranscriptMessage { Role: TranscriptRole.Assistant });
        Assert.Equal(0, _adapter.ParsedRecords);
        Assert.Equal(original, await File.ReadAllBytesAsync(HostFile));
        using var viewer = new TranscriptViewController(_dashboard.Transcripts);
        await viewer.ShowAsync(_configuration.MachineId, false);
        Assert.Contains(viewer.State.Entries, entry => entry.Text == Prompt);
        _dashboard.Transcripts.SetEnabled(false);
        Assert.Empty(viewer.State.Entries);
        Assert.Equal(0, _dashboard.Transcripts.RetainedEventCount);
        await HookAsync("preToolUse", toolName: "synthetic_tool");
        await WaitForMachineAsync(AgentEvent.PreToolUse);
        AssertWirePrivacy();
        await AssertPersistentPrivacyAsync();
    }

    [Theory]
    [InlineData("opt-out")]
    [InlineData("legacy")]
    [InlineData("http")]
    [InlineData("tls")]
    [InlineData("redirect")]
    [InlineData("incompatible")]
    [InlineData("receiver-disabled")]
    [InlineData("receiver-unavailable")]
    public async Task DetailPrerequisiteFailuresPreserveActualRelayStatusAndNeverSendText(string failure)
    {
        await StartAsync(failure: failure);
        var expectedAvailability = failure switch
        {
            "opt-out" => "sharing-disabled",
            "legacy" => "legacy-status-only",
            "http" => "https-required",
            "tls" => "tls-validation-failed",
            "redirect" => "redirect-refused",
            "incompatible" => "incompatible-receiver",
            _ => "receiver-disabled"
        };
        await WaitUntilAsync(() => Task.FromResult(_client!.TranscriptAvailability == expectedAvailability));
        await WaitForNegotiationAsync(false);
        var original = await File.ReadAllBytesAsync(HostFile);
        await HookAsync("userPromptSubmitted", Prompt);
        await WaitForMachineAsync(AgentEvent.UserPromptSubmitted);
        await HookAsync("agentStop", transcriptPath: HostFile);
        await WaitForMachineAsync(AgentEvent.AgentStop);
        Assert.Equal(0, DetailEventRequests);
        Assert.Equal(0, _adapter.ParsedRecords);
        Assert.Equal(0, _dashboard.Transcripts.RetainedEventCount);
        Assert.Equal(original, await File.ReadAllBytesAsync(HostFile));
        AssertWirePrivacy();
        await AssertPersistentPrivacyAsync();
    }

    [Fact]
    public async Task ReceiverRestartInvalidatesViewerAndRestoresNoConversationFromTheStatusDatabase()
    {
        await StartAsync();
        await WaitForNegotiationAsync(true);
        await HookAsync("userPromptSubmitted", Prompt);
        await WaitForEventsAsync(events => events.Any(value => value.Payload is TranscriptMessage { Text: Prompt }));
        await WaitUntilAsync(() => Task.FromResult(_client!.TranscriptAvailability == "ready"));
        using var viewer = new TranscriptViewController(_dashboard.Transcripts);
        await viewer.ShowAsync(_configuration.MachineId, false);
        Assert.Contains(viewer.State.Entries, entry => entry.Text == Prompt);
        var epoch = _dashboard.Transcripts.ReceiverEpoch;
        var hostBytes = await File.ReadAllBytesAsync(HostFile);

        await _dashboard.DisposeAsync();
        Assert.Empty(viewer.State.Entries);
        _dashboard = new DashboardServer(_machines, _listener.Port, options: new()
        {
            ListenerMode = DashboardListenerMode.Internet,
            TranscriptTunnelReady = true
        });
        await _dashboard.StartAsync();
        Assert.NotEqual(epoch, _dashboard.Transcripts.ReceiverEpoch);
        Assert.Equal(0, _dashboard.Transcripts.RetainedEventCount);
        Assert.Single(await _machines.GetMachinesAsync());
        using var reopened = new TranscriptViewController(_dashboard.Transcripts);
        await reopened.ShowAsync(_configuration.MachineId, false);
        Assert.Empty(reopened.State.Sessions);
        Assert.Empty(reopened.State.Entries);
        Assert.Equal(0, _adapter.ParsedRecords);
        Assert.Equal(hostBytes, await File.ReadAllBytesAsync(HostFile));
        await AssertPersistentPrivacyAsync();
    }

    private int DetailEventRequests => _wire.Count(request => request.Path == TranscriptProtocol.EventsPath);

    private async Task StartAsync(bool useTestAdapter = true, string? failure = null)
    {
        _tls = new LoopbackTlsDashboard(_listener)
        {
            TranscriptRedirectStatus = failure == "redirect" ? 307 : null,
            TranscriptCapabilitiesBody = failure == "incompatible"
                ? JsonSerializer.Serialize(_dashboard.Transcripts.GetCapabilities() with { ProtocolVersion = 99 }, TranscriptProtocol.Json)
                : null
        };
        await _tls.StartAsync();
        _configuration = _configuration.WithDashboardUrl(
            failure == "http" ? _listener.AbsoluteUri : _tls.HttpsAddress.AbsoluteUri) with
        {
            Version = failure == "legacy" ? 4 : 5,
            DetailedReportingEnabled = failure != "opt-out"
        };
        SaveConfiguration(omitDefault: failure is null);
        Assert.Equal(failure != "opt-out", RemoteConfiguration.Load(ConfigPath).DetailedReportingEnabled);
        if (failure == "receiver-disabled") _dashboard.Transcripts.SetEnabled(false);
        if (failure == "receiver-unavailable") _dashboard.Transcripts.SetReadiness(false);
        _presenceClient = new HttpClient(new WireObserver(_wire, _tls.CreateTrustedHandler(_configuration)))
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        var detailHandler = failure == "tls"
            ? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }
            : _tls.CreateTrustedHandler(_configuration);
        _client = new ClientCoordinator(ConfigPath,
            transportFactory: config => new PresenceTransport(config, _presenceClient),
            transcriptTransport: new TranscriptTransport(new WireObserver(_wire, detailHandler)),
            transcriptFileAdapters: useTestAdapter ? _adapter : null);
        _ipc = new ClientIpcServer(ConfigPath, _client.HandleAsync, _client.TranscriptScratch);
        _client.Start();
        await WaitUntilAsync(async () => (await _machines.GetMachinesAsync()).Any(machine =>
            machine.MachineId == _configuration.MachineId && machine.PresenceMode == PresenceMode.Managed));
    }

    private void SaveConfiguration(bool omitDefault = false)
    {
        var json = JsonSerializer.SerializeToNode(_configuration, Protocol.Json)!.AsObject();
        if (omitDefault) json.Remove("detailedReportingEnabled");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(json));
    }

    private async Task<TranscriptNegotiation> WaitForNegotiationAsync(bool enabled)
    {
        TranscriptNegotiation? result = null;
        await WaitUntilAsync(async () =>
        {
            result = (await ClientIpc.SendAsync(ConfigPath,
                new(ClientIpc.Version, "transcript-negotiate", TranscriptSource: Source))).Transcript;
            return result?.Enabled == enabled;
        });
        return result!;
    }

    private async Task<TranscriptEvent[]> WaitForEventsAsync(Func<TranscriptEvent[], bool> predicate)
    {
        TranscriptEvent[] result = [];
        await WaitUntilAsync(async () =>
        {
            var sessions = await _dashboard.Transcripts.ListSessionsAsync(_configuration.MachineId);
            var selection = sessions.Sessions.SingleOrDefault(session => session.Selection.SessionId == Session)?.Selection;
            result = selection is null ? [] :
                (await _dashboard.Transcripts.ReadEventsAsync(selection)).Events.Select(value => value.Event).ToArray();
            return predicate(result);
        });
        return result;
    }

    private Task WaitForMachineAsync(AgentEvent expected) => WaitUntilAsync(async () =>
        (await _machines.GetMachinesAsync()).Any(machine => machine.MachineId == _configuration.MachineId &&
            machine.LatestEvent == expected));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }

    private static bool IsStop(TranscriptEvent value) =>
        value.Payload is TranscriptLifecycle { Signal: TranscriptLifecycleSignal.StopObserved };

    private async Task HookAsync(string eventName, string? prompt = null, string? toolName = null, string? transcriptPath = null)
    {
        using var process = new Process
        {
            StartInfo = new(_relay)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in new[]
        {
            "hook", "--event", eventName, "--config", ConfigPath, "--source", Source.Kind,
            "--scope", Source.ScopeId, "--source-version", Source.Version, "--adapter", Source.Kind
        })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["AGENT_SIGNALER_DATA_DIR"] = _root;
        var elapsed = Stopwatch.StartNew();
        Assert.True(process.Start());
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                sessionId = Session, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                prompt, toolName, transcriptPath, toolArgs = new { value = ForbiddenArguments },
                toolResult = new { resultType = "success", output = ForbiddenResult }, reasoning = ForbiddenReasoning
            }));
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        Assert.Equal(0, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Empty(await stderr);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Relay exceeded its observational hook budget.");
    }

    private void AssertWirePrivacy()
    {
        foreach (var request in _wire)
        {
            var text = Encoding.UTF8.GetString(request.Body);
            AssertExcluded(text);
            Assert.DoesNotContain("transcriptPath", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("transcript_path", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Path.GetFileName(HostFile), text);
            Assert.DoesNotContain(Path.GetFileName(HostRoot), text);
            if (request.Path != TranscriptProtocol.EventsPath)
            {
                Assert.DoesNotContain("SYNTHETIC-USER", text);
                Assert.DoesNotContain("SYNTHETIC-ASSISTANT", text);
            }
        }
        Assert.NotNull(_tls);
        Assert.Equal(0, _tls.PlaintextConnections);
        Assert.All(_tls.Requests, request =>
        {
            Assert.Empty(request.Cookie);
            Assert.Empty(request.Authorization);
            Assert.Empty(request.TunnelAuthorization);
        });
    }

    private static void AssertExcluded(string text)
    {
        foreach (var marker in new[] { ForbiddenArguments, ForbiddenResult, ForbiddenReasoning, Historical, FileUser })
            Assert.DoesNotContain(marker, text);
    }

    private async Task AssertPersistentPrivacyAsync()
    {
        await StopClientAsync();
        AssertWirePrivacy();
        var files = Directory.GetFiles(_root, "*", SearchOption.AllDirectories);
        Assert.Contains(ConfigPath, files);
        Assert.Contains(RemotePaths.State(ConfigPath), files);
        Assert.Contains(Path.Combine(_root, "dashboard.db"), files);
        foreach (var file in files.Where(file => !string.Equals(file, HostFile, StringComparison.OrdinalIgnoreCase)))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes);
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode })
            {
                var text = encoding.GetString(bytes.ToArray());
                AssertExcluded(text);
                Assert.DoesNotContain("SYNTHETIC-USER", text);
                Assert.DoesNotContain("SYNTHETIC-ASSISTANT", text);
                Assert.DoesNotContain(Suppressed, text);
                Assert.DoesNotContain(Path.GetFileName(HostFile), text);
                Assert.DoesNotContain(Path.GetFileName(HostRoot), text);
            }
        }
    }

    public async Task DisposeAsync()
    {
        await StopClientAsync();
        _presenceClient?.Dispose();
        if (_tls is not null) await _tls.DisposeAsync();
        if (_dashboard is not null) await _dashboard.DisposeAsync();
        _machines?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task StopClientAsync()
    {
        if (_ipc is not null) { await _ipc.DisposeAsync(); _ipc = null; }
        if (_client is not null) { await _client.DisposeAsync(); _client = null; }
    }

    private sealed class WireObserver(ConcurrentQueue<(string Path, byte[] Body)> requests, HttpMessageHandler inner)
        : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Enqueue((request.RequestUri!.AbsolutePath, request.Content is null ? [] :
                await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
