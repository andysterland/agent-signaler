using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientActivationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(root, "remote.json");
    private string ClientPath => Path.Combine(root, "AgentSignaler.Client.exe");

    private void Save()
    {
        var config = new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid() }.ToVersion3();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }));
    }

    [Fact]
    public async Task AbsentOwnerQueryAndReloadNeverLaunchOrCreateFiles()
    {
        var platform = new FakePlatform();
        var runtime = new ClientIntegrationRuntime(platform);
        Assert.False((await runtime.QueryAsync(ConfigPath, default)).Running);
        Assert.False((await runtime.ReloadAsync(ConfigPath, default)).Running);
        Assert.Equal(0, platform.Launches);
        Assert.Empty(platform.Commands);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("connected", "connected")]
    [InlineData("retrying", "retrying")]
    [InlineData("discovery-unavailable", "discovery-unavailable")]
    [InlineData("reloading", "reloading")]
    [InlineData("stopping", "stopping")]
    [InlineData("unconfigured", "unconfigured")]
    [InlineData("unexpected-private-payload", "unknown")]
    public async Task RuntimeStateExposesBoundedObservedReportingStateSeparatelyFromSetupTests(string reported, string expected)
    {
        var platform = new FakePlatform { Owned = true, Response = new(true, reported, Error: "Application-defined warning.") };
        var state = await new ClientIntegrationRuntime(platform).QueryAsync(ConfigPath, default);
        Assert.Equal(expected, state.State);
        Assert.Equal("Application-defined warning.", state.Message);
        Assert.True(state.Running);
        Assert.Null(state.EffectiveRevision);
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task UnresponsiveOwnedDirectoryIsNotMistakenForStoppedClient()
    {
        var platform = new FakePlatform { Owned = true, Response = new(false, "unavailable") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ClientIntegrationRuntime(platform).QueryAsync(ConfigPath, default));
        Assert.Equal(0, platform.Launches);
    }

    [Theory]
    [InlineData("direct", "direct")]
    [InlineData("not-running", "not-running")]
    [InlineData("pending", "pending")]
    [InlineData("resolved", "resolved")]
    [InlineData("cached", "cached")]
    [InlineData("unavailable", "unavailable")]
    [InlineData("expired", "expired")]
    [InlineData("unexpected-private-payload", "unknown")]
    public async Task DiscoveryStatusQueryExposesOnlyBoundedCodesAndDoesNotReadConfiguration(string reported, string expected)
    {
        var platform = new FakePlatform { Owned = true };
        platform.Reply = request => new(true, request.Command == "discovery-status" ? reported : "connected");
        var state = await new ClientIntegrationRuntime(platform).QueryDiscoveryStatusAsync(ConfigPath, default);
        Assert.Equal(expected, state.DiscoveryStatus);
        Assert.Equal("connected", state.State);
        Assert.Equal(new[] { "status", "discovery-status" }, platform.Commands.Select(request => request.Command));
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task DiscoveryStatusQueryIsCompatibleWithOlderClientAndStoppedOwner()
    {
        var platform = new FakePlatform { Owned = true };
        platform.Reply = request => request.Command == "discovery-status" ? new(false, "invalid") : new(true, "connected");
        var runtime = new ClientIntegrationRuntime(platform);
        Assert.Equal("unknown", (await runtime.QueryDiscoveryStatusAsync(ConfigPath, default)).DiscoveryStatus);
        platform.Commands.Clear();
        platform.Owned = false;
        Assert.Equal("not-running", (await runtime.QueryDiscoveryStatusAsync(ConfigPath, default)).DiscoveryStatus);
        Assert.Empty(platform.Commands);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DnsReloadWaitsForConfigurationAcknowledgementWithoutCredentialRevision()
    {
        var config = new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDnsTxtDiscovery("signaler.cablesorter.io");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        var statusReads = 0;
        var platform = new FakePlatform { Owned = true };
        platform.Reply = request =>
        {
            if (request.Command == "status") statusReads++;
            return new(true, "connected", statusReads >= 3 ? revision : null, 300);
        };
        var state = await new ClientIntegrationRuntime(platform).ReloadAsync(ConfigPath, default);
        Assert.Equal(revision, state.EffectiveRevision);
        Assert.Null(state.EffectiveCredentialRevision);
        Assert.Equal(3, statusReads);
        Assert.Equal(0, platform.Launches);
        Assert.DoesNotContain(platform.Commands, request => request.Command is not ("status" or "reload"));
    }

    [Fact]
    public async Task VersionSixDirectTransitionUsesOnlyCoherentConnectionReload()
    {
        var config = new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDashboardUrl("https://synthetic.example/").ToVersion6();
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        var platform = new FakePlatform { Owned = true };
        platform.Reply = request => request.Command == "transcript-reload"
            ? new(false, "connection-reload-required") : new(true, "connected", revision, 300);
        var state = await new ClientIntegrationRuntime(platform).ReloadAsync(ConfigPath, default);
        Assert.Equal(revision, state.EffectiveRevision);
        Assert.Single(platform.Commands, request => request.Command == "reload");
        Assert.DoesNotContain(platform.Commands, request => request.Command == "transcript-reload");
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task ReloadUsesExactCommittedRevisionWithoutLaunching()
    {
        Save();
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        var platform = new FakePlatform { Owned = true, Response = new(true, "connected", revision, 300) };
        var state = await new ClientIntegrationRuntime(platform).ReloadAsync(ConfigPath, default);
        Assert.True(state.Running);
        Assert.Equal(300, state.HeartbeatIntervalSeconds);
        Assert.Equal(revision, Assert.Single(platform.Commands, c => c.Command == "reload").ExpectedRevision);
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task TranscriptSuspendDoesNotReadConfigOrAwaitStatusAndUsesV3()
    {
        var platform = new FakePlatform { Owned = true, Response = new(true, "suspended") { Version = 3 } };
        await new ClientIntegrationRuntime(platform).SuspendTranscriptAsync(ConfigPath, default);
        var request = Assert.Single(platform.Commands);
        Assert.Equal(3, request.Version);
        Assert.Equal("transcript-reload", request.Command);
        Assert.Equal("suspend", request.ExpectedRevision);
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task TranscriptPauseIsDistinctFromExplicitSuspendAndDoesNotReadConfig()
    {
        var platform = new FakePlatform { Owned = true, Response = new(true, "paused") { Version = 3 } };
        await new ClientIntegrationRuntime(platform).PauseTranscriptAsync(ConfigPath, default);
        var request = Assert.Single(platform.Commands);
        Assert.Equal(3, request.Version);
        Assert.Equal("transcript-reload", request.Command);
        Assert.Equal("pause", request.ExpectedRevision);
        Assert.False(Directory.Exists(root));
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task TranscriptSuspendRejectsOldClientWithoutSendingContentOrLaunching()
    {
        var platform = new FakePlatform { Owned = true, Response = new(true, "connected") { Version = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClientIntegrationRuntime(platform).SuspendTranscriptAsync(ConfigPath, default));
        Assert.Single(platform.Commands);
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task TranscriptReloadUsesCommittedRevisionWithoutStatusOrNetworkReload()
    {
        Save();
        var platform = new FakePlatform { Owned = true, Response = new(true, "ready") { Version = 3 } };
        await new ClientIntegrationRuntime(platform).ReloadTranscriptAsync(ConfigPath, default);
        var request = Assert.Single(platform.Commands);
        Assert.Equal("transcript-reload", request.Command);
        Assert.Equal(ClientConfigurationRevision.Read(ConfigPath), request.ExpectedRevision);
    }

    [Fact]
    public async Task StoppedTranscriptReloadAndSuspendNeverCreateFilesOrStartClient()
    {
        var platform = new FakePlatform();
        var runtime = new ClientIntegrationRuntime(platform);
        await runtime.SuspendTranscriptAsync(ConfigPath, default);
        await runtime.PauseTranscriptAsync(ConfigPath, default);
        await runtime.ReloadTranscriptAsync(ConfigPath, default);
        Assert.Empty(platform.Commands);
        Assert.Equal(0, platform.Launches);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ExplicitStartLaunchesOnceAndRequiresMatchingEffectiveRevision()
    {
        Save();
        var revision = ClientConfigurationRevision.Read(ConfigPath);
        var platform = new FakePlatform { Response = new(true, "connected", revision, 300) };
        var runtime = new ClientIntegrationRuntime(platform);
        Assert.Equal(revision, (await runtime.StartAsync(ClientPath, ConfigPath, default)).EffectiveRevision);
        Assert.True((await runtime.StartAsync(ClientPath, ConfigPath, default)).Running);
        Assert.Equal(1, platform.Launches);
        Assert.Equal(ClientPath, platform.LaunchedClient);
        Assert.Equal(ClientIdentity.CanonicalPath(ConfigPath), platform.LaunchedConfig);
    }

    [Fact]
    public async Task ShutdownAcceptsOfflineWarningOnlyAfterOwnerIsReleased()
    {
        var platform = new FakePlatform { Owned = true, ReleaseOnStop = true, Response = new(true, "stopping", Error: "Offline delivery unconfirmed") };
        await new ClientIntegrationRuntime(platform).StopAsync(ConfigPath, default);
        Assert.False(platform.Owned);
        Assert.Equal("stop", Assert.Single(platform.Commands).Command);
        Assert.Equal(0, platform.Launches);
    }

    [Fact]
    public async Task ShutdownRefusesUnacknowledgedStillOwnedClient()
    {
        var platform = new FakePlatform { Owned = true, Response = new(false, "unavailable") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ClientIntegrationRuntime(platform).StopAsync(ConfigPath, default));
        Assert.True(platform.Owned);
    }

    [Fact]
    public async Task LostShutdownReplyStillWaitsForDelayedOwnerReleaseWithoutLaunching()
    {
        var platform = new FakePlatform { Owned = true, Response = new(false, "unavailable") };
        var stop = new ClientIntegrationRuntime(platform).StopAsync(ConfigPath, default);
        Assert.Equal("stop", Assert.Single(platform.Commands).Command);
        Assert.False(stop.IsCompleted);
        platform.Owned = false;
        await stop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(0, platform.Launches);
        Assert.False(platform.Owned);
    }

    [Fact]
    public async Task ShutdownCannotClaimSuccessWhileAcknowledgedClientStillOwnsLock()
    {
        var platform = new FakePlatform { Owned = true, Response = new(true, "stopping") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClientIntegrationRuntime(platform).StopAsync(ConfigPath, timeout.Token));
        Assert.True(platform.Owned);
    }

    private sealed class FakePlatform : IClientRuntimePlatform
    {
        public bool Owned;
        public bool ReleaseOnStop;
        public int Launches;
        public string? LaunchedClient;
        public string? LaunchedConfig;
        public ClientIpcResponse Response = new(true, "connected");
        public Func<ClientIpcRequest, ClientIpcResponse>? Reply;
        public List<ClientIpcRequest> Commands = [];
        public bool IsOwnerRunning(string configPath) => Owned;
        public Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request, CancellationToken token)
        {
            Commands.Add(request);
            if (request.Command == "stop" && ReleaseOnStop) Owned = false;
            return Task.FromResult(Reply?.Invoke(request) ?? Response);
        }
        public void Launch(string clientPath, string configPath)
        { Launches++; LaunchedClient = clientPath; LaunchedConfig = configPath; Owned = true; }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
