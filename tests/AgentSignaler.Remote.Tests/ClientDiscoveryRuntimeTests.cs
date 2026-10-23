using System.Collections.Concurrent;
using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;
using static AgentSignaler.Remote.Tests.ClientRuntimeTests;

namespace AgentSignaler.Remote.Tests;

public sealed class ClientDiscoveryRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "test-data", Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_root, "remote.json");

    private void Save()
    {
        var configuration = new RemoteConfiguration { MachineId = Guid.NewGuid(), MachineName = "fixture" }
            .WithDnsTxtDiscovery("first.example.test");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
    }

    [Fact]
    public async Task DiscoveryHealthCommandReportsResolverStateWithoutTriggeringLookupOrExposingAddress()
    {
        Save();
        var clock = new ManualClock();
        var resolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ => resolver,
            endpointTransportFactory: (_, _) => new RecordingTransport());
        var request = new ClientIpcRequest(ClientIpc.Version, "discovery-status");
        Assert.Equal("not-running", (await runtime.HandleAsync(request)).State);
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        var health = await runtime.HandleAsync(request);
        Assert.Equal("resolved", health.State);
        Assert.Null(health.Error);
        Assert.Null(health.EffectiveRevision);
        Assert.Null(health.EffectiveCredentialRevision);
        var lookups = resolver.Calls;
        await runtime.HandleAsync(request);
        Assert.Equal(lookups, resolver.Calls);
        resolver.Status = "dns-unavailable";
        runtime.RequestSnapshot();
        await Eventually(() => runtime.HandleAsync(request).GetAwaiter().GetResult().State == "cached");
        resolver.Endpoint = null;
        runtime.RequestSnapshot();
        await Eventually(() => runtime.HandleAsync(request).GetAwaiter().GetResult().State == "unavailable");
        Assert.False((await runtime.HandleAsync(new(ClientIpc.LegacyVersion, "discovery-status"))).Accepted);
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
    public async Task HostnameReloadAndDirectConversionUseConfigurationRevisionsNotLiveEndpoint()
    {
        Save();
        var clock = new ManualClock();
        var firstResolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var nextResolver = new Resolver(clock) { Endpoint = new(new("https://second-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var transports = new ConcurrentQueue<(EffectiveEndpointSnapshot Endpoint, RecordingTransport Transport)>();
        await using var runtime = new ClientCoordinator(ConfigPath, clock,
            discoveryResolverFactory: configuration => configuration.DnsRecordName == "first.example.test" ? firstResolver : nextResolver,
            endpointTransportFactory: (_, endpoint) =>
            {
                var transport = new RecordingTransport();
                transports.Enqueue((endpoint, transport));
                return transport;
            });
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.Null(runtime.Status().EffectiveCredentialRevision);
        var configuration = RemoteConfiguration.Load(ConfigPath) with
        {
            DnsRecordName = "second.example.test"
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var changed = await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath));
        Assert.True(changed.Accepted);
        Assert.Null(changed.EffectiveCredentialRevision);
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
        Assert.Equal(7, RemoteConfiguration.Load(ConfigPath).Version);
    }

    [Fact]
    public async Task DirectToDnsReloadDoesNotResolveUntilPersistentClientAppliesTheSavedRevision()
    {
        Save();
        var configuration = RemoteConfiguration.Load(ConfigPath).WithDashboardUrl("https://direct.example/");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var clock = new ManualClock();
        var resolver = new Resolver(clock) { Endpoint = new(new("https://first-51820.use.devtunnels.ms/"), 1, clock.GetUtcNow().AddMinutes(10)) };
        var resolutions = 0;
        var transports = new ConcurrentQueue<RecordingTransport>();
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ =>
        {
            Interlocked.Increment(ref resolutions);
            return resolver;
        }, endpointTransportFactory: (_, _) =>
        {
            var transport = new RecordingTransport();
            transports.Enqueue(transport);
            return transport;
        });
        runtime.Start();
        await Eventually(() => runtime.Status().State == "connected");
        Assert.Equal(0, resolutions);
        configuration = configuration.WithDnsTxtDiscovery("first.example.test");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        Assert.Equal(0, resolutions);
        var result = await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath));
        Assert.True(result.Accepted);
        Assert.Equal(1, resolutions);
        Assert.Equal(ClientConfigurationRevision.Read(ConfigPath), result.EffectiveRevision);
        Assert.True(transports.First().Disposed);
        Assert.Equal(PresenceKind.Started, transports.Last().Reports.First().Kind);
        Assert.True(transports.Last().Reports.First().Generation > transports.First().Reports.First().Generation);
    }

    [Fact]
    public async Task CanceledReloadCannotCommitLateAnswerFromPreviousHostname()
    {
        Save();
        var clock = new ManualClock();
        var resolver = new DeferredResolver(clock);
        var transports = 0;
        await using var runtime = new ClientCoordinator(ConfigPath, clock, discoveryResolverFactory: _ => resolver,
            endpointTransportFactory: (_, _) =>
            {
                Interlocked.Increment(ref transports);
                return new RecordingTransport();
            });
        runtime.Start();
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var configuration = RemoteConfiguration.Load(ConfigPath).WithDnsTxtDiscovery("changed.example.test");
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        Assert.False((await runtime.ReloadAsync(ClientConfigurationRevision.Read(ConfigPath), caller.Token)).Accepted);
        resolver.Complete.SetResult(new(new(new("https://previous-51820.use.devtunnels.ms/"), 1,
            clock.GetUtcNow().AddMinutes(10)), "resolved", clock.GetUtcNow().AddMinutes(5)));
        await Eventually(() => resolver.Disposed);
        Assert.Equal(0, transports);
        Assert.Equal("discovery-unavailable", runtime.Status().State);
        Assert.Null(runtime.Status().EffectiveRevision);
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
        public string? Status { get; set; }
        public int Calls;
        public bool Disposed { get; private set; }
        public Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new DiscoveryResolution(Endpoint, Status ?? (Endpoint is null ? "dns-unavailable" : "resolved"),
                clock.GetUtcNow().AddMinutes(5)));
        }
        public Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default)
            => ResolveAsync(cancellationToken);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class DeferredResolver(TimeProvider clock) : IDiscoveryEndpointResolver
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DiscoveryResolution> Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public Task<DiscoveryResolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return Complete.Task;
        }
        public Task<DiscoveryResolution> RefreshAfterFailureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new DiscoveryResolution(null, "dns-unavailable", clock.GetUtcNow().AddMinutes(5)));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}