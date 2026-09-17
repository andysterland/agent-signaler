using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AgentSignaler.Dashboard;
using AgentSignaler.Tunneling;
using Xunit;
using static AgentSignaler.RpcHost.Tests.RpcRuntimeTests;
using static AgentSignaler.RpcHost.Tests.RpcTransportTests;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcSharingTests
{
    [Fact]
    public async Task SharingLifecycleUsesRealControllerAndSurvivesControllerDisconnect()
    {
        var runner = new SyntheticTunnelRunner();
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            TunnelRunner = runner, TunnelHealthProbe = new SyntheticProbe()
        });
        runner.Port = fixture.ReceiverPort;
        using var socket = await fixture.ConnectAsync();
        var started = await fixture.CallAsync(socket, "sharing.start");
        Assert.Equal("connected", started.GetProperty("state").GetProperty("state").GetString());
        Assert.True(started.GetProperty("state").GetProperty("publicEndpointVerified").GetBoolean());
        Assert.False(runner.Host!.Disposed);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "synthetic disconnect", TestContext.Token);
        Assert.False(runner.Host.Disposed);
        using var replacement = await ConnectEventuallyAsync(fixture);
        var current = await fixture.CallAsync(replacement, "sharing.getStatus");
        Assert.Equal("connected", current.GetProperty("state").GetProperty("state").GetString());
        using var http = new HttpClient();
        using var health = await http.GetAsync(new Uri(fixture.ReceiverUri, "health"), TestContext.Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var stopped = await fixture.CallAsync(replacement, "sharing.stop");
        Assert.Equal("stopped", stopped.GetProperty("state").GetProperty("state").GetString());
        Assert.True(runner.Host.Disposed);
        Assert.Equal(1009, (await fixture.CallErrorAsync(replacement, "sharing.delete")).GetProperty("code").GetInt32());
        await fixture.CallAsync(replacement, "sharing.delete", new { confirmed = true });
        Assert.True(runner.Deleted);
        Assert.Equal(1009, (await fixture.CallErrorAsync(replacement, "sharing.logout")).GetProperty("code").GetInt32());
        await fixture.CallAsync(replacement, "sharing.logout", new { confirmed = true });
        Assert.True(runner.LoggedOut);
    }

    [Fact]
    public async Task SyntheticTunnelHttpForwardingCannotReachControlRpc()
    {
        var runner = new SyntheticTunnelRunner();
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            TunnelRunner = runner, TunnelHealthProbe = new SyntheticProbe()
        });
        using var listener = new HttpListener();
        var proxyPort = FreePort();
        listener.Prefixes.Add($"http://localhost:{proxyPort}/");
        listener.Start();
        using var http = new HttpClient();
        var proxy = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var request = await listener.GetContextAsync().WaitAsync(TestContext.Token);
                using var upstream = await http.GetAsync(new Uri(fixture.ReceiverUri, request.Request.Url!.AbsolutePath), TestContext.Token);
                request.Response.StatusCode = (int)upstream.StatusCode;
                await upstream.Content.CopyToAsync(request.Response.OutputStream, TestContext.Token);
                request.Response.Close();
            }
        });
        using var publicHealth = await http.GetAsync($"http://localhost:{proxyPort}/health", TestContext.Token);
        Assert.Equal(HttpStatusCode.OK, publicHealth.StatusCode);
        using var publicControl = await http.GetAsync($"http://localhost:{proxyPort}/rpc", TestContext.Token);
        Assert.Equal(HttpStatusCode.NotFound, publicControl.StatusCode);
        await proxy.WaitAsync(TestContext.Token);
    }

    private static async Task<ClientWebSocket> ConnectEventuallyAsync(RuntimeFixture fixture)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            try { return await fixture.ConnectAsync(); }
            catch (WebSocketException) { await Task.Delay(20, deadline.Token); }
        }
    }

    private sealed class SyntheticProbe : ITunnelHealthProbe
    {
        public Task VerifyAsync(Uri baseUri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class SyntheticHost : ITunnelHostProcess
    {
        private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed;
        public Task<int> Completion => exit.Task;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            exit.TrySetResult(0);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SyntheticTunnelRunner : ITunnelProcessRunner
    {
        private const string Acl = """[{"type":"Anonymous","subjects":[],"scopes":["connect"]}]""";
        private string? id;
        private string description = "";
        private bool portExists;
        private bool aclExists;
        public int Port;
        public bool Deleted;
        public bool LoggedOut;
        public SyntheticHost? Host;

        public Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = arguments.ToArray();
            string json;
            switch (args[0])
            {
                case "--version":
                    json = "Tunnel CLI version: 1.0.2030+fc9273aa0f\nTunnel service URI : https://global.rel.tunnels.api.visualstudio.com/";
                    break;
                case "user" when args[1] == "show":
                    json = """{"status":"Logged in","provider":"microsoft","username":"synthetic","tenantId":"11111111-1111-1111-1111-111111111111","objectId":"22222222-2222-2222-2222-222222222222"}""";
                    break;
                case "user":
                    LoggedOut = true;
                    json = "";
                    break;
                case "create":
                    id = args[1] + ".usw2";
                    description = args[Array.IndexOf(args, "--description") + 1];
                    Deleted = portExists = aclExists = false;
                    json = TunnelJson();
                    break;
                case "show":
                    if (Deleted)
                    {
                        var split = args[1].LastIndexOf('.');
                        return Task.FromResult(new CliCommandResult(2, $"Tunnel not found in {args[1][(split + 1)..]}: {args[1][..split]}", ""));
                    }
                    json = TunnelJson();
                    break;
                case "port":
                    if (args[1] == "create") portExists = true;
                    json = "{\"port\":" + PortJson() + "}";
                    break;
                case "access":
                    if (args[1] == "create") aclExists = true;
                    json = "{\"accessControlEntries\":" + (args.Contains("--port-number") && aclExists ? Acl : "[]") + "}";
                    break;
                case "delete":
                    Deleted = true;
                    json = JsonSerializer.Serialize(new { deletedTunnel = id });
                    break;
                default: throw new InvalidOperationException("Unexpected synthetic tunnel command.");
            }
            return Task.FromResult(new CliCommandResult(0, json, ""));
        }

        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Host = new();
            outputLine($"Hosting port: {Port}");
            outputLine($"Connect via browser: https://synthetic-{Port}.usw2.devtunnels.ms");
            outputLine($"Ready to accept connections for tunnel: {id}");
            return Task.FromResult<ITunnelHostProcess>(Host);
        }
        private string TunnelJson()
        {
            var data = new Dictionary<string, object?>
            {
                ["tunnelId"] = id, ["description"] = description, ["hostConnections"] = 0,
                ["accessControl"] = Array.Empty<object>()
            };
            if (portExists) data["ports"] = new[] { JsonSerializer.Deserialize<JsonElement>(PortJson()) };
            return JsonSerializer.Serialize(new { tunnel = data });
        }
        private string PortJson() => JsonSerializer.Serialize(new
        {
            tunnelId = id, portNumber = Port, protocol = "http", accessControl = JsonSerializer.Deserialize<JsonElement>(aclExists ? Acl : "[]")
        });
    }
}
