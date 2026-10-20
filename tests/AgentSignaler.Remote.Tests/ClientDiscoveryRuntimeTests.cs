using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientDiscoveryRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");

    private void Save()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var configuration = new RemoteConfiguration { MachineId = Guid.NewGuid(), MachineName = "fixture" }
            .WithDiscovery(new(Guid.NewGuid().ToString("N"), new string('a', 64)), Guid.NewGuid(), DiscoveryCodec.ExportPublicKey(key), 1);
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
    }

    [Fact]
    public async Task EndpointReplacementUsesNewGenerationAndNeverOfflinesOldEndpointOrChangesSettings()
    {
        Save();
        var original = File.ReadAllBytes(ConfigPath);
        var clock = new ManualClock();
        var resolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var transports = new ConcurrentQueue<(EffectiveEndpointSnapshot Endpoint, RecordingTransport Transport)>();
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ => resolver,
            endpointTransportFactory: (_, snapshot) =>
            {
                var transport = new RecordingTransport();
                transports.Enqueue((snapshot, transport));
                return transport;
            });
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var first = Assert.Single(transports);
        Assert.Equal(PresenceKind.Started, first.Transport.Reports[0].Kind);
        resolver.Endpoint = new(new("https://second-51820.use.devtunnels.ms/"), 2, clock.GetUtcNow().AddMinutes(10));
        runtime.RequestSnapshot();
        await Eventually(() => transports.Count == 2 && runtime.Status().State == "connected");
        var second = transports.Last();
        Assert.True(first.Transport.Disposed);
        Assert.DoesNotContain(first.Transport.Reports, r => r.Kind == PresenceKind.Offline);
        Assert.Equal(PresenceKind.Started, second.Transport.Reports[0].Kind);
        Assert.True(second.Transport.Reports[0].Generation > first.Transport.Reports[0].Generation);
        Assert.Equal(1, second.Transport.Reports[0].Sequence);
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));
        var oldCount = first.Transport.Reports.Count;
        await runtime.StopAsync();
        Assert.Equal(oldCount, first.Transport.Reports.Count);
        Assert.Equal(PresenceKind.Offline, second.Transport.Reports.Last().Kind);
        Assert.True(resolver.Disposed);
    }

    [Fact]
    public async Task ExpirySuspendsSendsAndExitDoesNotSendOfflineToExpiredEndpoint()
    {
        Save();
        var clock = new ManualClock();
        var resolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddSeconds(30)) };
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ => resolver,
            endpointTransportFactory: (_, _) => transport);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        resolver.Endpoint = null;
        var count = transport.Reports.Count;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Eventually(() => runtime.Status().State == "discovery-unavailable");
        runtime.RequestSnapshot();
        await runtime.StopAsync();
        Assert.Equal(count, transport.Reports.Count);
        Assert.DoesNotContain(transport.Reports, r => r.Kind == PresenceKind.Offline);
        Assert.False(runtime.AcceptHook(AgentEvent.SessionStart, new("session", clock.GetUtcNow(), false)).Accepted);
    }

    [Fact]
    public async Task EndpointReplacementCancelsInflightPresenceAndRejectsItsLateAcknowledgement()
    {
        Save();
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var transports = new ConcurrentQueue<RecordingTransport>();
        var old = new RecordingTransport(async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { canceled.TrySetResult(); throw; }
            return true;
        });
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ => resolver,
            endpointTransportFactory: (_, endpoint) =>
            {
                var transport = endpoint.EndpointRevision == 1 ? old : new RecordingTransport();
                transports.Enqueue(transport);
                return transport;
            });
        runtime.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(runtime.Status().EffectiveRevision);
        resolver.Endpoint = new(new("https://second-51820.use.devtunnels.ms/"), 2, clock.GetUtcNow().AddMinutes(10));
        runtime.RequestSnapshot();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Eventually(() => runtime.Status().State == "connected" && transports.Count == 2);
        Assert.DoesNotContain(old.Reports, r => r.Kind == PresenceKind.Offline);
        Assert.Equal(PresenceKind.Started, transports.Last().Reports.First().Kind);
        Assert.Equal(ClientConfigurationRevision.Read(ConfigPath), runtime.Status().EffectiveRevision);
    }

    [Fact]
    public async Task ExplicitCredentialReplacementAcknowledgesNewProtectedRevisionAndDirectConversionClearsIt()
    {
        Save();
        var clock = new ManualClock();
        var firstResolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var nextResolver = new Resolver(clock) { Endpoint = new(new("https://second-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var transports = new ConcurrentQueue<(EffectiveEndpointSnapshot Endpoint, RecordingTransport Transport)>();
        await using var runtime = new ClientCoordinator(ConfigPath, clock,
            discoveryResolverFactory: configuration => configuration.DiscoveryCredentialRevision == 1 ? firstResolver : nextResolver,
            endpointTransportFactory: (_, endpoint) =>
            {
                var transport = new RecordingTransport();
                transports.Enqueue((endpoint, transport));
                return transport;
            });
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.Equal(1, runtime.Status().EffectiveCredentialRevision);
        var configuration = RemoteConfiguration.Load(ConfigPath) with
        {
            Discovery = new(Guid.NewGuid().ToString("N"), new string('b', 64)), DiscoveryCredentialRevision = 2
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var changed = await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath));
        Assert.True(changed.Accepted);
        Assert.Equal(2, changed.EffectiveCredentialRevision);
        Assert.True(firstResolver.Disposed);
        Assert.Equal("second-51820.use.devtunnels.ms", transports.Last().Endpoint.BaseUri.Host);
        Assert.DoesNotContain(transports.First().Transport.Reports, report => report.Kind == PresenceKind.Offline);
        configuration = configuration.WithDashboardUrl("https://explicit-direct.example/");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var direct = await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath));
        Assert.True(direct.Accepted);
        Assert.Null(direct.EffectiveCredentialRevision);
        Assert.True(nextResolver.Disposed);
        Assert.Equal("explicit-direct.example", transports.Last().Endpoint.BaseUri.Host);
        Assert.Equal(6, RemoteConfiguration.Load(ConfigPath).Version);
    }

    [Fact]
    public async Task HooksWhileDiscoveryIsUnavailableBecomeCurrentStateWithoutEventReplay()
    {
        Save();
        var configuration = RemoteConfiguration.Load(ConfigPath) with
        {
            Integrations = [new()
            {
                Id = "fixture-cli", Kind = "copilot-cli", DisplayName = "Fixture", InstallationId = "fixture",
                ScopeId = "fixture-scope", HostVersion = "test", HookDirectory = Path.Combine(_root, "hooks"),
                Capability = IntegrationCapability.Configured, Provenance = "synthetic-test-only",
                SupportedEvents = ["sessionStart"]
            }]
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var clock = new ManualClock();
        var resolver = new Resolver(clock);
        var transport = new RecordingTransport();
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ => resolver,
            endpointTransportFactory: (_, _) => transport);
        runtime.Start();
        Assert.True(runtime.AcceptHook(AgentEvent.SessionStart, new("observed-while-unavailable", clock.GetUtcNow(), false)).Accepted);
        resolver.Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10));
        runtime.RequestSnapshot();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.Contains(transport.Reports[0].Sessions!, session => session.SessionId == "observed-while-unavailable");
        Assert.DoesNotContain(transport.Reports, report => report.Kind == PresenceKind.Hook);
        Assert.All(transport.Reports, report => Assert.Null(report.Hook));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Resolver(TimeProvider clock) : IDiscoveryEndpointResolver
    {
        public EffectiveEndpointSnapshot? Endpoint { get; set; }
        public bool Disposed { get; private set; }
        public Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DiscoveryResolution(Endpoint, Endpoint is null ? "publisher-unavailable" : "verified",
                clock.GetUtcNow().AddMinutes(5)));
        }
        public Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default)
            => ResolveAsync(cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}