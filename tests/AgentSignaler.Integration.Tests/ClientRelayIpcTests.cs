using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class ClientRelayIpcTests : IAsyncLifetime
{
    private const string Secret = "PRIVATE-HOOK-CONTENT-MUST-NOT-LEAVE-RELAY";
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "client-ipc-e2e", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");
    private string _relay = "";
    private RemoteConfiguration _configuration = null!;
    private MachineStore _store = null!;
    private DashboardServer _dashboard = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _relay = Path.ChangeExtension((await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "relay-path.txt"))).Trim(), ".exe");
        Assert.True(File.Exists(_relay));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _configuration = new RemoteConfiguration
        {
            Host = "127.0.0.1", Port = port, MachineId = Guid.NewGuid(),
            MachineName = "IPC-TEST-ONLY", ClientVersion = "test", RelayPath = _relay
        }.ToVersion3();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(_configuration, Protocol.Json));
        _store = new MachineStore(Path.Combine(_root, "dashboard.db"));
        _dashboard = new DashboardServer(_store, port);
        await _dashboard.StartAsync();
    }

    [Fact]
    public async Task RealRelayUsesTrayOwnershipAndExitIsTerminalUntilExplicitRestart()
    {
        long generation;
        await using (var runtime = new ClientCoordinator(ConfigPath))
        await using (var ipc = new ClientIpcServer(ConfigPath, runtime.HandleAsync))
        {
            runtime.Start();
            var idle = await WaitForMachine(AgentState.Idle);
            generation = idle.Generation;
            Assert.Equal(PresenceMode.Managed, idle.PresenceMode);
            await Hook("preToolUse", "session", "ask_user");
            var waiting = await WaitForMachine(AgentState.Waiting);
            Assert.True(Assert.Single(waiting.Sessions).AwaitingUserInput);
            Assert.True((await ClientIpc.StopAsync(ConfigPath)).Accepted);
            var offline = await WaitForMachine(AgentState.Offline);
            Assert.True(offline.ExplicitOffline);
            var sequence = offline.Sequence;
            var localState = await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath));
            await Hook("sessionEnd");
            Assert.Equal(sequence, (await WaitForMachine(AgentState.Offline)).Sequence);
            Assert.Equal(localState, await File.ReadAllBytesAsync(RemotePaths.State(ConfigPath)));
        }
        await Hook("preToolUse");
        Assert.Equal(AgentState.Offline, Assert.Single(await _store.GetMachinesAsync()).State);
        await using (var restarted = new ClientCoordinator(ConfigPath))
        await using (var ipc = new ClientIpcServer(ConfigPath, restarted.HandleAsync))
        {
            restarted.Start();
            var idle = await WaitForMachine(AgentState.Idle);
            Assert.True(idle.Generation > generation);
            Assert.Empty(idle.Sessions);
        }
        foreach (var path in Directory.GetFiles(_root, "*.json").Concat(Directory.GetFiles(_root, "*.log")))
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrentRealRelaysFeedOneOrderedCoordinator()
    {
        await using var runtime = new ClientCoordinator(ConfigPath);
        await using var ipc = new ClientIpcServer(ConfigPath, runtime.HandleAsync);
        runtime.Start();
        await WaitForMachine(AgentState.Idle);
        await Task.WhenAll(Hook("sessionStart", "one"), Hook("sessionStart", "two"), Hook("sessionStart", "three"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (Assert.Single(await _store.GetMachinesAsync()).Sessions.Count != 3)
            await Task.Delay(20, timeout.Token);
        var machine = Assert.Single(await _store.GetMachinesAsync());
        Assert.Equal(PresenceMode.Managed, machine.PresenceMode);
        Assert.Equal(AgentState.Waiting, machine.State);
        Assert.Equal(3, machine.Sessions.Select(s => s.SessionId).Distinct().Count());
    }

    [Fact]
    public async Task LegacyConfigurationCannotBypassAnAbsentTray()
    {
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(_configuration with { Version = 2 }, Protocol.Json));
        Assert.Equal(2, RemoteConfiguration.Load(ConfigPath).Version);
        await Hook("sessionStart");
        Assert.Empty(await _store.GetMachinesAsync());
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
        Assert.False(File.Exists(Path.Combine(_root, "client-generation")));
    }

    private async Task<MachineView> WaitForMachine(AgentState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (true)
        {
            var machines = await _store.GetMachinesAsync();
            if (machines.Count == 1 && machines[0].State == state) return machines[0];
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task Hook(string kind, string session = "session", string? toolName = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(_relay)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in new[] { "hook", "--event", kind, "--config", ConfigPath })
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment["AGENT_SIGNALER_DATA_DIR"] = _root;
        var elapsed = Stopwatch.StartNew();
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                sessionId = session, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), toolName,
                prompt = Secret, toolArgs = new { value = Secret }, toolResult = new { resultType = "success", output = Secret }
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
        Assert.Equal("", await output);
        Assert.Equal("", await error);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Relay exceeded hook budget: {elapsed.Elapsed}");
    }

    public async Task DisposeAsync()
    {
        if (_dashboard is not null) await _dashboard.DisposeAsync();
        _store?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
