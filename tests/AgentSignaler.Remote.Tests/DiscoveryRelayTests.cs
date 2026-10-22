using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class DiscoveryRelayTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");

    private static RemoteConfiguration Configuration()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDiscovery(
            new(Guid.NewGuid().ToString("N"), new string('a', 64)), Guid.NewGuid(), DiscoveryCodec.ExportPublicKey(key), 1);
    }

    [Fact]
    public async Task DiscoveryHooksUseOnlyLocalIpcAndNegotiateDetailsWithoutResolvingBaseUri()
    {
        var config = Configuration() with
        {
            Integrations = [new IntegrationTarget
            {
                Id = "synthetic-cli", Kind = "copilot-cli", DisplayName = "Synthetic CLI", InstallationId = "synthetic",
                ScopeId = "synthetic", HookDirectory = Path.Combine(root, "hooks"),
                Capability = IntegrationCapability.Verified, SupportedEvents = ["sessionStart"]
            }]
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var commands = new List<string>();
        await using var server = new ClientIpcServer(ConfigPath, (request, _) =>
        {
            commands.Add(request.Command);
            return Task.FromResult(new ClientIpcResponse(true, "connected"));
        });
        using var http = new HttpClient(new NoNetwork());
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { sessionId = "synthetic-session", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), prompt = "PRIVATE-PROMPT" }));
        Assert.Equal(0, await new RelayEngine(http).RunAsync(
            ["hook", "--event", "sessionStart", "--config", ConfigPath], new MemoryStream(payload)));
        Assert.Equal(new[] { "hook", "transcript-negotiate" }, commands);
        Assert.False(Directory.Exists(DiscoveryCredentialTransactionStore.DirectoryPath(ConfigPath)));
        Assert.DoesNotContain("PRIVATE-PROMPT", File.ReadAllText(RemotePaths.Log(ConfigPath)));
    }

    [Fact]
    public async Task LegacyHttpSenderRejectsDiscoveryBeforeAnyNetworkOrProtectedStorageAccess()
    {
        using var http = new HttpClient(new NoNetwork());
        var config = Configuration();
        var request = new StatusRequest
        {
            MachineId = config.MachineId, MachineName = "synthetic", ClientVersion = "1.0",
            EventId = Guid.NewGuid(), Event = AgentEvent.SessionStart, ReportedAtUtc = DateTimeOffset.UtcNow, SessionId = "synthetic"
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RelayEngine(http).SendAsync(config, request, default));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("reload", 12, 12)]
    [InlineData("stop", 5, 5)]
    [InlineData("hook", 2, 5)]
    [InlineData("probe", 2, 5)]
    [InlineData("status", 2, 5)]
    [InlineData("transcript-negotiate", 2, 5)]
    public void OnlyExplicitReloadReceivesTheLongerIpcDeadline(string command, int requestSeconds, int workSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(requestSeconds), ClientIpc.RequestTimeout(command));
        Assert.Equal(TimeSpan.FromSeconds(workSeconds), ClientIpc.WorkTimeout(command));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Relay must not perform discovery or reporting network I/O.");
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
