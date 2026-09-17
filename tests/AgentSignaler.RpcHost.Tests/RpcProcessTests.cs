using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.RpcHost;
using Xunit;
using static AgentSignaler.RpcHost.Tests.RpcTransportTests;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcProcessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedHostingConfigurationCannotAddOrReplaceRpcListenersOrOccupyReceiverPort(bool preferHostingUrls)
    {
        var extraPort = FreePort();
        // Unavailable storage isolates the RPC transport from the independently hosted report receiver.
        await using var fixture = await ProcessFixture.StartAsync(storageUnavailable: true, configure: (start, receiver) =>
        {
            start.Environment["Kestrel__Endpoints__extra__Url"] = $"http://0.0.0.0:{extraPort}";
            start.Environment["Kestrel__Endpoints__receiver__Url"] = $"http://0.0.0.0:{receiver}";
            start.Environment["ASPNETCORE_URLS"] = $"http://0.0.0.0:{receiver}";
            start.Environment["ASPNETCORE_PREFERHOSTINGURLS"] = preferHostingUrls.ToString();
            start.Environment["DOTNET_PREFERHOSTINGURLS"] = preferHostingUrls.ToString();
            start.Environment["preferHostingUrls"] = preferHostingUrls.ToString();
        });
        Assert.NotEqual(extraPort, fixture.Port);
        Assert.NotEqual(extraPort, fixture.ReceiverPort);
        using var socket = await fixture.ConnectAsync();
        await WaitUntilAsync(async () =>
            (await fixture.CallAsync(socket, "system.getStatus")).GetProperty("state").GetProperty("initialAttemptCompleted").GetBoolean());
        var status = await fixture.CallAsync(socket, "system.getStatus");
        Assert.Equal("degraded", status.GetProperty("state").GetProperty("lifecycle").GetString());
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var ipv6 = await client.GetAsync($"http://[::1]:{fixture.Port}/health", TestContext.Token);
        Assert.Equal(HttpStatusCode.OK, ipv6.StatusCode);
        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Where(endpoint => endpoint.Port == fixture.Port).Select(endpoint => endpoint.Address).ToArray();
        Assert.Equal(2, listeners.Length);
        Assert.Contains(IPAddress.Loopback, listeners);
        Assert.Contains(IPAddress.IPv6Loopback, listeners);
        foreach (var unowned in new[] { extraPort, fixture.ReceiverPort })
        {
            using var available = new TcpListener(IPAddress.Any, unowned);
            available.Start();
        }
        await fixture.CallAsync(socket, "system.shutdown");
        await fixture.Process.WaitForExitAsync(TestContext.Token);
        Assert.Equal(0, fixture.Process.ExitCode);
    }

    [Fact]
    public async Task SubprocessReadyIsSingleLineAndExplicitShutdownAcknowledgesBeforeExit()
    {
        await using var fixture = await ProcessFixture.StartAsync();
        Assert.Equal("transportReady", fixture.Ready.GetProperty("state").GetString());
        Assert.Equal(1, fixture.Ready.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(fixture.Port, fixture.Ready.GetProperty("rpcPort").GetInt32());
        using var socket = await fixture.ConnectAsync();
        await fixture.WaitOperationalAsync(socket);
        var response = await fixture.CallAsync(socket, "system.shutdown");
        Assert.Equal("stopping", response.GetProperty("state").GetString());
        await fixture.Process.WaitForExitAsync(TestContext.Token);
        Assert.Equal(0, fixture.Process.ExitCode);
        Assert.Empty(await fixture.Process.StandardOutput.ReadToEndAsync(TestContext.Token));
        Assert.Empty(await fixture.Process.StandardError.ReadToEndAsync(TestContext.Token));
    }

    [Fact]
    public async Task SubprocessOwnershipConflictAndPortFailuresUseStableCodesWithoutStartup()
    {
        await using var fixture = await ProcessFixture.StartAsync();
        using (var competitor = ProcessFixture.Start(fixture.Executable, fixture.Directory, FreePort()))
        {
            await competitor.WaitForExitAsync(TestContext.Token);
            Assert.Equal(3, competitor.ExitCode);
            Assert.Empty(await competitor.StandardOutput.ReadToEndAsync(TestContext.Token));
            Assert.Contains("resourceOwned", await competitor.StandardError.ReadToEndAsync(TestContext.Token));
        }
        var directory = ProcessFixture.CreateDataDirectory(out var port, out var receiver);
        try
        {
            using (var conflict = ProcessFixture.Start(ProcessFixture.BuildExecutable, directory, receiver))
            {
                await conflict.WaitForExitAsync(TestContext.Token);
                Assert.Equal(2, conflict.ExitCode);
                Assert.Contains("--rpc-port", await conflict.StandardError.ReadToEndAsync(TestContext.Token));
                Assert.False(File.Exists(Path.Combine(directory, "dashboard.db")));
            }
            using var occupied = new TcpListener(IPAddress.Loopback, port);
            occupied.Start();
            using var bind = ProcessFixture.Start(ProcessFixture.BuildExecutable, directory, port);
            await bind.WaitForExitAsync(TestContext.Token);
            Assert.Equal(4, bind.ExitCode);
            Assert.Contains("--rpc-port", await bind.StandardError.ReadToEndAsync(TestContext.Token));
            Assert.False(File.Exists(Path.Combine(directory, "dashboard.db")));
        }
        finally { TestDirectory.Delete(directory); }
    }

    [Fact]
    public async Task ParentProcessExitDoesNotStopForegroundRpcHost()
    {
        var directory = ProcessFixture.CreateDataDirectory(out var port, out _);
        var stdout = Path.Combine(directory, "ready.json");
        var stderr = Path.Combine(directory, "errors.txt");
        Process? host = null;
        try
        {
            var executable = ProcessFixture.BuildExecutable;
            var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var command = "$env:AGENT_SIGNALER_DATA_DIR=" + Quote(directory) + ";" +
                "$p=Start-Process -FilePath " + Quote(executable) + " -ArgumentList " +
                Quote($"--rpc-port {port} --data-directory \"{directory}\"") +
                " -RedirectStandardOutput " + Quote(stdout) + " -RedirectStandardError " + Quote(stderr) +
                " -PassThru; $p.Id";
            using var parent = Process.Start(new ProcessStartInfo(ps)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", command }
            })!;
            var id = int.Parse((await parent.StandardOutput.ReadLineAsync(TestContext.Token))!.Trim());
            host = Process.GetProcessById(id);
            await parent.WaitForExitAsync(TestContext.Token);
            Assert.Equal(0, parent.ExitCode);
            await WaitUntilAsync(() => File.Exists(stdout) && new FileInfo(stdout).Length > 0);
            Assert.False(host.HasExited);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", "http://localhost");
            await socket.ConnectAsync(new($"ws://127.0.0.1:{port}/rpc"), TestContext.Token);
            await SendAsync(socket, Request("system.getCapabilities", "\"alive\""));
            Assert.Contains("\"alive\"", await ReceiveUntilIdAsync(socket, "alive"));
            await SendAsync(socket, Request("system.shutdown", "\"stop\""));
            await ReceiveUntilIdAsync(socket, "stop");
            await host.WaitForExitAsync(TestContext.Token);
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (host is not null)
            {
                if (!host.HasExited) { host.Kill(); await host.WaitForExitAsync(TestContext.Token); }
                host.Dispose();
            }
            TestDirectory.Delete(directory);
        }
    }

    [Fact]
    public async Task ForcedHostTerminationStopsOnlyOwnedAzureChildrenAndGrandchildren()
    {
        var childDirectory = TestDirectory.Create();
        var unrelatedDirectory = TestDirectory.Create();
        var fixtureExecutable = Path.Combine(TestDirectory.RepositoryRoot, "tests", "AgentSignaler.RpcHost.Tests",
            "ChildFixture", "bin", "x64", ProcessFixture.Configuration, "net10.0", "win-x64", "az.exe");
        using var unrelated = Process.Start(new ProcessStartInfo(fixtureExecutable)
        {
            UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--owned-grandchild" },
            Environment = { ["AGENT_SIGNALER_TEST_CHILD_DIRECTORY"] = unrelatedDirectory }
        })!;
        var owned = new List<Process>();
        try
        {
            await using var fixture = await ProcessFixture.StartAsync(childDirectory: childDirectory);
            using var socket = await fixture.ConnectAsync();
            await fixture.WaitOperationalAsync(socket);
            await SendAsync(socket, Request("prerequisites.check", "\"child\"",
                JsonSerializer.Serialize(new { id = "azureCli", path = fixtureExecutable }, RpcProtocol.Json)));
            await WaitUntilAsync(() => Directory.GetFiles(childDirectory, "*.running").Length == 2);
            var ids = Directory.GetFiles(childDirectory, "*.running").Select(path => int.Parse(Path.GetFileNameWithoutExtension(path))).ToArray();
            foreach (var id in ids)
            {
                var process = Process.GetProcessById(id);
                _ = process.SafeHandle;
                owned.Add(process);
                Assert.False(process.HasExited);
                Assert.Equal(fixtureExecutable, process.MainModule!.FileName, ignoreCase: true);
            }
            fixture.Process.Kill(entireProcessTree: false);
            await fixture.Process.WaitForExitAsync(TestContext.Token);
            await WaitUntilAsync(() => owned.All(process => process.HasExited));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            if (!unrelated.HasExited) { unrelated.Kill(); await unrelated.WaitForExitAsync(TestContext.Token); }
            foreach (var remaining in owned)
            {
                if (!remaining.HasExited) { remaining.Kill(); await remaining.WaitForExitAsync(TestContext.Token); }
                remaining.Dispose();
            }
            TestDirectory.Delete(childDirectory);
            TestDirectory.Delete(unrelatedDirectory);
        }
    }

    [PublishedExeFact]
    public async Task PublishedExeAloneExtractsNativeSqliteAndPersistsRpcMutation()
    {
        var published = Environment.GetEnvironmentVariable("AGENT_SIGNALER_RPC_TEST_EXE")!;
        var lockDirectory = Path.Combine(TestDirectory.RepositoryRoot, "artifacts", "test-firewall");
        Directory.CreateDirectory(lockDirectory);
        using var fixedPathLease = new FileStream(Path.Combine(lockDirectory, "published-exe.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var directory = Path.Combine(TestDirectory.RepositoryRoot, ".rpc-test-work", "published-exe");
        Assert.False(Directory.Exists(directory), "Fixed EXE fixture already exists; inspect the previous run before removing it.");
        var executableDirectory = Path.Combine(directory, "exe-only");
        var extraction = Path.Combine(directory, "extract");
        Directory.CreateDirectory(executableDirectory);
        var executable = Path.Combine(executableDirectory, "AgentSignaler.RpcHost.exe");
        try
        {
            File.Copy(published, executable);
            await using var fixture = await ProcessFixture.StartAsync(executable, extraction: extraction);
            Assert.Single(Directory.EnumerateFiles(executableDirectory));
            using var socket = await fixture.ConnectAsync();
            await fixture.WaitOperationalAsync(socket);
            var id = Guid.NewGuid();
            using var client = new HttpClient();
            using var accepted = await client.PostAsJsonAsync(new Uri(fixture.ReceiverUri, "api/v1/status"), new StatusRequest
            {
                MachineId = id, MachineName = "exe-only-synthetic", EventId = Guid.NewGuid(), ClientVersion = "synthetic",
                SessionId = "synthetic", Event = AgentEvent.SessionStart, ReportedAtUtc = DateTimeOffset.UtcNow
            }, Protocol.Json, TestContext.Token);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            JsonElement machine = default;
            await WaitUntilAsync(async () =>
            {
                var list = await fixture.CallAsync(socket, "machines.list");
                if (list.GetProperty("state").GetProperty("items").GetArrayLength() == 0) return false;
                machine = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
                return true;
            });
            await fixture.CallAsync(socket, "machines.updateDetails", new
            {
                machineId = id, hostInstanceId = fixture.Ready.GetProperty("hostInstanceId").GetString(),
                expectedRevision = machine.GetProperty("revision").GetString(), note = "EXE-only SQLite persisted"
            });
            await fixture.CallAsync(socket, "system.shutdown");
            await fixture.Process.WaitForExitAsync(TestContext.Token);
            Assert.Equal(0, fixture.Process.ExitCode);
            Assert.True(File.Exists(Path.Combine(fixture.Directory, "dashboard.db")));
            Assert.NotEmpty(Directory.EnumerateFiles(extraction, "*.dll", SearchOption.AllDirectories));
            using var restarted = ProcessFixture.Start(executable, fixture.Directory, fixture.Port, extraction: extraction);
            try
            {
                var ready = await restarted.StandardOutput.ReadLineAsync(TestContext.Token);
                Assert.NotNull(ready);
                using var second = new ClientWebSocket();
                second.Options.SetRequestHeader("Origin", "http://localhost");
                await second.ConnectAsync(new($"ws://127.0.0.1:{fixture.Port}/rpc"), TestContext.Token);
                await WaitUntilAsync(async () =>
                {
                    await SendAsync(second, Request("system.getStatus", "\"status\""));
                    using var status = JsonDocument.Parse(await ReceiveUntilIdAsync(second, "status"));
                    return status.RootElement.GetProperty("result").GetProperty("state").GetProperty("initialAttemptCompleted").GetBoolean();
                });
                await SendAsync(second, Request("machines.getNote", "\"note\"", JsonSerializer.Serialize(new { machineId = id })));
                var note = await ReceiveUntilIdAsync(second, "note");
                Assert.Contains("EXE-only SQLite persisted", note);
                await SendAsync(second, Request("system.shutdown", "\"stop\""));
                await ReceiveUntilIdAsync(second, "stop");
                await restarted.WaitForExitAsync(TestContext.Token);
                Assert.Equal(0, restarted.ExitCode);
            }
            finally
            {
                if (!restarted.HasExited)
                {
                    restarted.Kill(entireProcessTree: true);
                    await restarted.WaitForExitAsync(TestContext.Token);
                }
            }
        }
        finally { TestDirectory.Delete(directory); }
    }

    [PublishedExeFact]
    public async Task PublishedExeFailsBeforeReadinessWhenExtractionLocationIsNotADirectory()
    {
        var directory = ProcessFixture.CreateDataDirectory(out var port, out _);
        var blocked = Path.Combine(directory, "blocked-extraction");
        await File.WriteAllTextAsync(blocked, "synthetic extraction blocker");
        try
        {
            using var process = ProcessFixture.Start(Environment.GetEnvironmentVariable("AGENT_SIGNALER_RPC_TEST_EXE")!,
                directory, port, extraction: blocked);
            await process.WaitForExitAsync(TestContext.Token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Empty(await process.StandardOutput.ReadToEndAsync(TestContext.Token));
            var diagnostics = await process.StandardError.ReadToEndAsync(TestContext.Token);
            Assert.False(string.IsNullOrWhiteSpace(diagnostics));
            Assert.InRange(diagnostics.Length, 1, 8192);
            Assert.False(File.Exists(Path.Combine(directory, "dashboard.db")));
        }
        finally { TestDirectory.Delete(directory); }
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''") + "'";
    internal static async Task WaitUntilAsync(Func<bool> condition) => await WaitUntilAsync(() => Task.FromResult(condition()));
    internal static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!await condition()) await Task.Delay(25, deadline.Token);
    }
    internal static async Task<string> ReceiveUntilIdAsync(ClientWebSocket socket, string id)
    {
        while (true)
        {
            var text = await ReceiveTextAsync(socket);
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("id", out var found) && found.GetString() == id) return text;
        }
    }

    internal sealed class ProcessFixture : IAsyncDisposable
    {
        public static string Configuration => AppContext.BaseDirectory.Contains("\\Release\\", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        public static string BuildExecutable => Path.Combine(TestDirectory.RepositoryRoot, "src", "AgentSignaler.RpcHost",
            "bin", "x64", Configuration, "net10.0-windows10.0.22621.0", "win-x64", "AgentSignaler.RpcHost.exe");
        public string Executable { get; }
        public string Directory { get; }
        public int Port { get; }
        public int ReceiverPort { get; }
        public Process Process { get; }
        public JsonElement Ready { get; private set; }
        private int requestId;
        public Uri ReceiverUri => new($"http://127.0.0.1:{ReceiverPort}/");
        private ProcessFixture(string executable, string directory, int port, int receiver, Process process)
        {
            Executable = executable; Directory = directory; Port = port; ReceiverPort = receiver; Process = process;
        }
        public static string CreateDataDirectory(out int port, out int receiver)
        {
            var directory = TestDirectory.Create();
            port = FreePort();
            receiver = FreePort();
            new DashboardSettings
            {
                Port = receiver, RpcPort = port, ConnectionMode = DashboardConnectionMode.Lan,
                AutoStartSharing = false, ReceiveDetailedConversations = false,
                AzureCliPath = Path.Combine(directory, "synthetic", "az.exe"),
                DevTunnelCliPath = Path.Combine(directory, "synthetic", "devtunnel.exe")
            }.Save(directory);
            return directory;
        }
        public static Process Start(string executable, string directory, int port, string? extraction = null, string? childDirectory = null,
            Action<ProcessStartInfo>? configure = null)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = directory,
                ArgumentList = { "--rpc-port", port.ToString(), "--data-directory", directory },
                Environment = { ["AGENT_SIGNALER_DATA_DIR"] = directory, ["ASPNETCORE_URLS"] = "http://0.0.0.0:1" }
            };
            if (extraction is not null) start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = extraction;
            if (childDirectory is not null) start.Environment["AGENT_SIGNALER_TEST_CHILD_DIRECTORY"] = childDirectory;
            configure?.Invoke(start);
            return Process.Start(start)!;
        }
        public static async Task<ProcessFixture> StartAsync(string? executable = null, string? extraction = null, string? childDirectory = null,
            Action<ProcessStartInfo, int>? configure = null, bool storageUnavailable = false)
        {
            executable ??= BuildExecutable;
            var directory = CreateDataDirectory(out var port, out var receiver);
            if (storageUnavailable) System.IO.Directory.CreateDirectory(Path.Combine(directory, "dashboard.db"));
            var process = Start(executable, directory, port, extraction, childDirectory, start => configure?.Invoke(start, receiver));
            var fixture = new ProcessFixture(executable, directory, port, receiver, process);
            try
            {
                var line = await process.StandardOutput.ReadLineAsync(TestContext.Token);
                Assert.False(string.IsNullOrEmpty(line), "Host failed before transport ready.");
                using var json = JsonDocument.Parse(line);
                fixture.Ready = json.RootElement.Clone();
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", "http://localhost");
            try { await socket.ConnectAsync(new($"ws://127.0.0.1:{Port}/rpc"), TestContext.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
        public async Task WaitOperationalAsync(ClientWebSocket socket)
        {
            await WaitUntilAsync(async () =>
            {
                var status = await CallAsync(socket, "system.getStatus");
                return status.GetProperty("state").GetProperty("initialAttemptCompleted").GetBoolean();
            });
            var status = await CallAsync(socket, "system.getStatus");
            Assert.Equal("operational", status.GetProperty("state").GetProperty("lifecycle").GetString());
        }
        public async Task<JsonElement> CallAsync(ClientWebSocket socket, string method, object? parameters = null)
        {
            var id = Interlocked.Increment(ref requestId).ToString();
            await SendAsync(socket, Request(method, JsonSerializer.Serialize(id), parameters is null ? null : JsonSerializer.Serialize(parameters, RpcProtocol.Json)));
            using var response = JsonDocument.Parse(await ReceiveUntilIdAsync(socket, id));
            Assert.False(response.RootElement.TryGetProperty("error", out _), response.RootElement.GetRawText());
            return response.RootElement.GetProperty("result").Clone();
        }
        public async ValueTask DisposeAsync()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync(TestContext.Token);
            }
            Process.Dispose();
            TestDirectory.Delete(Directory);
        }
    }
}

internal sealed class PublishedExeFactAttribute : FactAttribute
{
    public PublishedExeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENT_SIGNALER_RPC_TEST_EXE")))
            Skip = "Publish gate: set AGENT_SIGNALER_RPC_TEST_EXE to the actual single-file win-x64 EXE.";
    }
}
