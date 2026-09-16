using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Integration.Tests;

public sealed class RelayToDashboardTests : IAsyncLifetime
{
    private const string SensitiveMarker = "SYNTHETIC-PRIVATE-CONTENT-DO-NOT-STORE";
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "AgentSignaler-e2e", Guid.NewGuid().ToString());
    private readonly Clock _clock = new();
    private MachineStore _store = null!;
    private DashboardServer _server = null!;
    private string _config = "";
    private string _executable = "";
    private int _connectionTests;
    private ClientCoordinator? _runtime;
    private ClientIpcServer? _ipc;
    private readonly ConcurrentQueue<PresenceReport> _delivered = new();

    public async Task InitializeAsync()
    {
        var relayAssembly = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "relay-path.txt"))).Trim();
        _executable = Path.ChangeExtension(relayAssembly, ".exe");
        Assert.True(File.Exists(_executable), $"Build the Relay project before integration tests: {_executable}");
        Directory.CreateDirectory(_directory);
        _config = Path.Combine(_directory, "remote.json");
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var configuration = new RemoteConfiguration
        {
            Host = "127.0.0.1", Port = port, MachineId = Guid.NewGuid(),
            MachineName = "INTEGRATION-TEST", ClientVersion = "test"
        };
        await File.WriteAllTextAsync(_config, JsonSerializer.Serialize(configuration, Protocol.Json));
        _store = new MachineStore(Path.Combine(_directory, "dashboard.db"), _clock);
        _server = new DashboardServer(_store, port, () => Interlocked.Increment(ref _connectionTests));
        await _server.StartAsync();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ActualRelayHooksReachDashboardAndPreservePrivacy(bool version2, bool includeRelayPath)
    {
        var configuration = RemoteConfiguration.Load(_config);
        if (version2) configuration = configuration.ToVersion2();
        if (includeRelayPath) configuration = configuration with { RelayPath = _executable };
        await SaveConfiguration(configuration);
        await Hook("sessionStart");
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.False(File.Exists(RemotePaths.State(_config)));
        await StartClient();
        await Hook("sessionStart");
        Assert.Equal(configuration.MachineId, (await Machine()).MachineId);
        Assert.Equal(AgentState.Waiting, (await Machine()).State);
        await Hook("userPromptSubmitted");
        Assert.Equal(AgentState.Executing, (await Machine()).State);
        await Hook("permissionRequest");
        Assert.Equal(AgentState.Waiting, (await Machine()).State);
        await Hook("postToolUse");
        Assert.Equal(AgentState.Executing, (await Machine()).State);
        await Hook("agentStop");
        Assert.Equal(AgentState.Succeeded, (await Machine()).State);
        await Hook("sessionEnd");
        Assert.Equal(AgentState.Succeeded, (await Machine()).State);
        _clock.Now = (await Machine()).Sessions.Max(s => s.ResultUntilUtc)!.Value;
        Assert.Equal(AgentState.Idle, (await Machine()).State);

        foreach (var file in Directory.GetFiles(_directory, "*.json").Concat(Directory.GetFiles(_directory, "*.log")))
            Assert.DoesNotContain(SensitiveMarker, await File.ReadAllTextAsync(file));
    }

    [Theory]
    [InlineData("postToolUse", "success", AgentState.Executing)]
    [InlineData("postToolUse", "failure", AgentState.Failed)]
    [InlineData("postToolUseFailure", "failure", AgentState.Failed)]
    public async Task ActualAskUserHooksWaitUntilAnsweredOrFailed(string completion, string status, AgentState expected)
    {
        await StartClient();
        await Hook("preToolUse", toolName: "ask_user");
        var pending = await Machine();
        Assert.Equal(AgentState.Waiting, pending.State);
        Assert.Equal(AgentEvent.PreToolUse, pending.LatestEvent);
        Assert.True(Assert.Single(pending.Sessions).AwaitingUserInput);
        await Hook("postToolUse", toolName: "powershell");
        Assert.Equal(AgentState.Waiting, (await Machine()).State);
        await Hook(completion, toolName: "ask_user", status: status);
        var completed = await Machine();
        Assert.Equal(expected, completed.State);
        Assert.False(Assert.Single(completed.Sessions).AwaitingUserInput);
        foreach (var file in Directory.GetFiles(_directory, "*.json").Concat(Directory.GetFiles(_directory, "*.log")))
            Assert.DoesNotContain(SensitiveMarker, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task ConcurrentRelayProcessesUseOneMachineIdentityAndHooksRestoreOnlineState()
    {
        await StartClient();
        await Task.WhenAll(Hook("sessionStart", "one"), Hook("preToolUse", "two"));
        Assert.Equal(2, (await Machine()).Sessions.Count);
        Assert.Equal(AgentState.Waiting, (await Machine()).State);
        await Hook("postToolUseFailure", "two");
        Assert.Equal(AgentState.Failed, (await Machine()).State);
        Assert.Equal(2, (await Machine()).Sessions.Count);
        _clock.Now += TimeSpan.FromMinutes(11);
        Assert.Equal(AgentState.Offline, (await Machine()).State);
        await Hook("preToolUse", "two");
        Assert.Equal(AgentState.Waiting, (await Machine()).State);
    }

    [LiveTunnelFact]
    public async Task ActualRelayReachesAnonymousCliTunnelAndResourceIsDeleted()
    {
        var configuration = RemoteConfiguration.Load(_config);
        await _server.StopAsync();
        await _server.DisposeAsync();
        _server = new DashboardServer(_store, configuration.Port, options: new DashboardServerOptions
        {
            ListenerMode = DashboardListenerMode.Internet
        });
        await _server.StartAsync();
        Assert.Equal(DashboardListenerMode.Internet, _server.ListenerMode);

        var stateDirectory = Path.Combine(_directory, "live-tunnel-state");
        Directory.CreateDirectory(stateDirectory);
        var statePath = Path.Combine(stateDirectory, "tunnel-state.json");
        var identity = new TunnelIdentity("", Guid.NewGuid().ToString("N"));
        var cliPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Links", "devtunnel.exe");
        async Task Persist(TunnelIdentity next, CancellationToken token)
        {
            identity = next;
            await File.WriteAllTextAsync(statePath, JsonSerializer.Serialize(next), token);
        }
        await Persist(identity, CancellationToken.None);
        await using var tunnel = new CliTunnelController(new TunnelOptions(cliPath, configuration.Port, identity), Persist);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await tunnel.StartAsync(timeout.Token);
            Assert.True(tunnel.Status.CanCopy, tunnel.Status.Message);
            configuration = configuration.WithDashboardUrl(tunnel.Status.PublicUrl!.AbsoluteUri);
            await SaveConfiguration(configuration);
            using var client = RemoteHttpTransport.CreateClient(configuration, interactive: true);
            await DashboardConnection.TestAsync(configuration, client, timeout.Token);
            await StartClient();
            var timer = Stopwatch.StartNew();
            await Hook("sessionStart");
            timer.Stop();
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"Actual Relay exceeded its hook budget: {timer.Elapsed}.");
            Assert.Equal(configuration.MachineId, (await Machine()).MachineId);
            Assert.Equal(AgentState.Waiting, (await Machine()).State);
            await StopClient();
            await tunnel.StopAsync(timeout.Token);
            Assert.False(tunnel.Status.CanCopy);
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await tunnel.DeleteAsync(cleanup.Token);
            Assert.True(tunnel.Identity.TunnelId is null && tunnel.Identity.PendingTunnelId is null,
                $"Cloud cleanup was not confirmed. Retained non-secret recovery state: {statePath}. {tunnel.Status.Message}");
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectivityProbeDoesNotReplaceAnActiveMachineWithIdle()
    {
        await StartClient();
        await Hook("preToolUse");
        var probesBefore = Volatile.Read(ref _connectionTests);
        Assert.Equal(1, probesBefore);
        File.Delete(RemotePaths.State(_config));
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        await DashboardConnection.TestAsync(RemoteConfiguration.Load(_config), client, CancellationToken.None);
        Assert.Equal(probesBefore + 1, Volatile.Read(ref _connectionTests));
        var machine = await Machine();
        Assert.Equal(AgentState.Executing, machine.State);
        Assert.Equal(AgentEvent.PreToolUse, machine.LatestEvent);
        Assert.False(File.Exists(RemotePaths.State(_config)));
    }

    [Fact]
    public async Task RepeatedConnectionTestsNotifyWithoutCreatingMachinesOrChangingConfiguration()
    {
        var configuration = RemoteConfiguration.Load(_config);
        var original = await File.ReadAllBytesAsync(_config);
        using var client = RemoteHttpTransport.CreateClient(configuration, interactive: true);
        for (var i = 1; i <= 2; i++)
        {
            await DashboardConnection.TestAsync(configuration, client, CancellationToken.None);
            Assert.Equal(i, Volatile.Read(ref _connectionTests));
        }
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(_config));
        Assert.False(File.Exists(RemotePaths.State(_config)));
    }

    [Fact]
    public async Task CorruptLocalStateRecoversOnNextHookWithDiagnostic()
    {
        await StartClient();
        await File.WriteAllTextAsync(RemotePaths.State(_config), "{invalid}");
        await Hook("preToolUse");
        Assert.Equal(AgentState.Executing, (await Machine()).State);
        var log = await File.ReadAllTextAsync(RemotePaths.Log(_config));
        Assert.NotEmpty(log);
        Assert.DoesNotContain("{invalid}", log);
    }

    [Fact]
    public async Task UnavailableDashboardDoesNotFailHookOrWritePermissionOutput()
    {
        await StartClient();
        await _server.StopAsync();
        var elapsed = Stopwatch.StartNew();
        await Hook("permissionRequest", waitForDelivery: false);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4), $"Hook took {elapsed.Elapsed}.");
        Assert.True(File.Exists(RemotePaths.Log(_config)));
    }

    [Fact]
    public async Task ApplyingManagedSettingsPreservesIdentityAndActiveSessions()
    {
        var original = RemoteConfiguration.Load(_config);
        await StartClient();
        await Hook("preToolUse");
        var state = await File.ReadAllBytesAsync(RemotePaths.State(_config));
        await SaveConfiguration(original.ToVersion3() with { HeartbeatIntervalSeconds = 600 });
        Assert.Equal(state, await File.ReadAllBytesAsync(RemotePaths.State(_config)));
        var migrated = RemoteConfiguration.Load(_config);
        Assert.Equal(3, migrated.Version);
        Assert.Equal(original.MachineId, migrated.MachineId);
        Assert.Equal(original.BaseUri, migrated.BaseUri);
        Assert.True((await ClientIpc.ReloadAsync(_config, ClientConfigurationRevision.Read(_config))).Accepted);

        await Hook("preToolUse");
        var machine = await Machine();
        Assert.Equal(original.MachineId, machine.MachineId);
        Assert.Equal(AgentState.Executing, machine.State);
        Assert.Equal("test-session", Assert.Single(machine.Sessions).SessionId);
    }

    [Fact]
    public async Task TrustedHttpsHealthAndRelayReachDashboardWithoutCookiesOrCredentials()
    {
        var original = RemoteConfiguration.Load(_config);
        await using var tls = new LoopbackTlsDashboard(original.BaseUri);
        await tls.StartAsync();
        var configuration = original.WithDashboardUrl(tls.HttpsAddress.AbsoluteUri).ToVersion3();
        await SaveConfiguration(configuration);
        using var client = tls.CreateTrustedClient(configuration);
        await DashboardConnection.TestAsync(configuration, client, CancellationToken.None);
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.False(File.Exists(RemotePaths.State(_config)));
        await StartClient(client);
        await using var stopBeforeTls = new RuntimeLease(this);

        var engine = new RelayEngine(client);
        await Hook("preToolUse", "tls-session");
        var machine = await Machine();
        Assert.Equal(original.MachineId, machine.MachineId);
        Assert.Equal(AgentState.Executing, machine.State);
        Assert.Equal("tls-session", Assert.Single(machine.Sessions).SessionId);
        var state = await File.ReadAllBytesAsync(RemotePaths.State(_config));

        await DashboardConnection.TestAsync(configuration, client, CancellationToken.None);
        Assert.Equal(state, await File.ReadAllBytesAsync(RemotePaths.State(_config)));
        Assert.Equal(AgentEvent.PreToolUse, (await Machine()).LatestEvent);
        Assert.Equal(0, await engine.RunAsync(["test", "--config", _config], Stream.Null));
        Assert.Equal(AgentState.Executing, (await Machine()).State);
        Assert.Equal(4, tls.Requests.Count(request => request.Method == "GET" && request.Path == "/api/v2/health"));
        Assert.Contains(tls.Requests, request => request.Method == "POST" && request.Path == "/api/v2/reports");
        Assert.DoesNotContain(tls.Requests, request => request.Path is "/api/v1/status" or "/health");
        Assert.All(tls.Requests, request =>
        {
            Assert.Equal("", request.Cookie);
            Assert.Equal("", request.Authorization);
            Assert.Equal("", request.TunnelAuthorization);
            Assert.Equal("application/json", request.Accept);
        });
        Assert.Equal(0, tls.PlaintextConnections);
        Assert.DoesNotContain(SensitiveMarker, await File.ReadAllTextAsync(RemotePaths.State(_config)));
        Assert.DoesNotContain(SensitiveMarker, await File.ReadAllTextAsync(RemotePaths.Log(_config)));
        Assert.True((await ClientIpc.StopAsync(_config)).Accepted);
        Assert.Equal(AgentState.Offline, (await Machine()).State);
        var requestCount = tls.Requests.Count;
        var terminalSequence = (await Machine()).Sequence;
        await Hook("userPromptSubmitted", waitForDelivery: false);
        Assert.Equal(requestCount, tls.Requests.Count);
        Assert.Equal(terminalSequence, (await Machine()).Sequence);
        await StopClient();
        await Hook("sessionStart", waitForDelivery: false);
        Assert.Equal(requestCount, tls.Requests.Count);
        Assert.Equal(AgentState.Offline, (await Machine()).State);
    }

    [Fact]
    public async Task ActualRelayRejectsUntrustedHttpsWithoutDowngradeOrPermissionOutput()
    {
        var original = RemoteConfiguration.Load(_config);
        await using var tls = new LoopbackTlsDashboard(original.BaseUri);
        await tls.StartAsync();
        var configuration = original.WithDashboardUrl(tls.HttpsAddress.AbsoluteUri);
        await SaveConfiguration(configuration);
        using (var trusted = tls.CreateTrustedClient(configuration))
            await DashboardConnection.TestAsync(configuration, trusted, CancellationToken.None);
        var acceptedRequests = tls.Requests.Count;

        using (var untrusted = RemoteHttpTransport.CreateClient(configuration, interactive: true))
        {
            var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
                DashboardConnection.TestAsync(configuration, untrusted, CancellationToken.None));
            Assert.Equal(HttpRequestError.SecureConnectionError, failure.HttpRequestError);
        }
        await Hook("permissionRequest");
        await Run(["heartbeat", "--config", _config], null, expectedExitCode: 2);
        await Run(["test", "--config", _config], null, expectedExitCode: 1);

        Assert.Equal(acceptedRequests, tls.Requests.Count);
        Assert.Equal(0, tls.PlaintextConnections);
        Assert.Empty(await _store.GetMachinesAsync());
        var log = await File.ReadAllTextAsync(RemotePaths.Log(_config));
        Assert.Contains("operation-failed", log);
        Assert.DoesNotContain("delivered", log);
        Assert.DoesNotContain(SensitiveMarker, log);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task HttpsRedirectsNeverFollowAnHttpDowngrade(int status)
    {
        var original = RemoteConfiguration.Load(_config);
        await using var tls = new LoopbackTlsDashboard(original.BaseUri) { RedirectStatus = status };
        await tls.StartAsync();
        var configuration = original.WithDashboardUrl(tls.HttpsAddress.AbsoluteUri);
        using var client = tls.CreateTrustedClient(configuration);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            DashboardConnection.TestAsync(configuration, client, CancellationToken.None));
        Assert.Equal((HttpStatusCode)status, failure.StatusCode);
        Assert.False(await new RelayEngine(client).SendAsync(configuration, new StatusRequest
        {
            EventId = Guid.NewGuid(), MachineId = configuration.MachineId,
            MachineName = configuration.MachineName, ClientVersion = configuration.ClientVersion,
            Event = AgentEvent.PreToolUse, SessionId = "redirect-session", ReportedAtUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None));
        Assert.Equal(2, tls.Requests.Count);
        Assert.Equal(0, tls.PlaintextConnections);
        Assert.Empty(await _store.GetMachinesAsync());
    }

    [Theory]
    [InlineData("text/html", false, false)]
    [InlineData("application/json", true, false)]
    [InlineData("application/json", true, true)]
    public async Task HttpsHealthRejectsNonJsonAndOversizedResponses(
        string contentType, bool oversized, bool chunked)
    {
        var original = RemoteConfiguration.Load(_config);
        var body = JsonSerializer.Serialize(new HealthResponse(Protocol.Version, "ok"), Protocol.Json);
        await using var tls = new LoopbackTlsDashboard(original.BaseUri)
        {
            HealthBody = oversized ? body.PadRight(4097) : body,
            HealthContentType = contentType,
            ChunkedHealth = chunked
        };
        await tls.StartAsync();
        var configuration = original.WithDashboardUrl(tls.HttpsAddress.AbsoluteUri);
        using var client = tls.CreateTrustedClient(configuration);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DashboardConnection.TestAsync(configuration, client, CancellationToken.None));
        Assert.Equal("/health", Assert.Single(tls.Requests).Path);
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.False(File.Exists(RemotePaths.State(_config)));
    }

    private Task SaveConfiguration(RemoteConfiguration configuration) =>
        File.WriteAllTextAsync(_config, JsonSerializer.Serialize(configuration, Protocol.Json));

    private async Task StartClient(HttpClient? transport = null)
    {
        Assert.Null(_runtime);
        await SaveConfiguration(RemoteConfiguration.Load(_config).ToVersion3());
        _runtime = new ClientCoordinator(_config, _clock,
            configuration => new ObservingTransport(new PresenceTransport(configuration, transport), _delivered));
        _ipc = new ClientIpcServer(_config, _runtime.HandleAsync);
        _runtime.Start();
        await WaitUntil(() => _runtime.Status().State == "connected");
    }

    private async Task StopClient()
    {
        if (_runtime is not null) await _runtime.StopAsync();
        if (_ipc is not null) await _ipc.DisposeAsync();
        if (_runtime is not null) await _runtime.DisposeAsync();
        _ipc = null;
        _runtime = null;
    }

    private async Task Hook(string kind, string sessionId = "test-session", string? toolName = null,
        string status = "success", bool waitForDelivery = true)
    {
        var sequence = _delivered.Select(report => report.Sequence).DefaultIfEmpty().Max();
        await Run(["hook", "--event", kind, "--config", _config],
            JsonSerializer.Serialize(HookPayload(sessionId, toolName, status)));
        if (!waitForDelivery || _runtime is null) return;
        await WaitUntil(() =>
        {
            var hook = _delivered.FirstOrDefault(report => report.Sequence > sequence &&
                report.Kind == PresenceKind.Hook && report.Hook?.SessionId == sessionId &&
                JsonNamingPolicy.CamelCase.ConvertName(report.Hook.Event.ToString()) == kind);
            return hook is not null && _delivered.Any(report =>
                report.Kind == PresenceKind.Heartbeat && report.Sequence > hook.Sequence);
        });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static object HookPayload(string sessionId, string? toolName = null, string status = "success") => new
        {
            sessionId, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), toolName,
            prompt = SensitiveMarker, toolArgs = new { source = SensitiveMarker },
            toolResult = new { resultType = status, textResultForLlm = SensitiveMarker }
        };

    private async Task Run(string[] arguments, string? input, int expectedExitCode = 0)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.Environment["AGENT_SIGNALER_DATA_DIR"] = _directory;
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        var elapsed = Stopwatch.StartNew();
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        Assert.Equal("", await output);
        Assert.Equal(expectedExitCode, process.ExitCode);
        Assert.Equal("", await error);
        if (arguments[0] == "hook")
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Hook exceeded its 3-second timeout: {elapsed.Elapsed}.");
    }

    private async Task<MachineView> Machine() => Assert.Single(await _store.GetMachinesAsync());

    public async Task DisposeAsync()
    {
        await StopClient();
        await _server.StopAsync();
        await _server.DisposeAsync();
        _store.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class ObservingTransport(IPresenceTransport inner, ConcurrentQueue<PresenceReport> delivered) : IPresenceTransport
    {
        public async Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken)
        {
            var accepted = await inner.SendAsync(report, cancellationToken);
            if (accepted) delivered.Enqueue(report);
            return accepted;
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class RuntimeLease(RelayToDashboardTests owner) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await owner.StopClient();
    }
}
