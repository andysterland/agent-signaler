using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.Discovery;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class PrivateDiscoveryAcceptanceTests
{
    [Fact]
    public async Task FakeProvisionImportTransactionalApplyReplaceRenewRotateAndRevoke()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "remote.json");
        var relayPath = Path.Combine(directory, "AgentSignaler.Relay.exe");
        await File.WriteAllBytesAsync(relayPath, []);
        await File.WriteAllBytesAsync(Path.Combine(directory, "AgentSignaler.Client.exe"), []);
        var clock = new Clock();
        var azure = new AzureFixture(clock);
        var journal = new Journal();
        var publisher = new PrivateDiscoveryController(journal, _ => azure, clock);
        using var readerClient = new HttpClient(azure);
        var dashboard = new DashboardFixture();
        using var dashboardClient = new HttpClient(dashboard);
        var slots = new Slots();
        var startup = new Startup();
        ClientCoordinator? client = null;
        try
        {
            var target = new PrivateDiscoveryTarget(Guid.NewGuid(), Guid.NewGuid(), "fixture-group", "fixtureaccount");
            var preview = await publisher.PreviewAsync(target, default);
            Assert.Null(azure.Remote);
            await publisher.ProvisionAsync(preview.ConfirmationId, default);
            Assert.True(azure.Remote!.IsPrivate);
            var copiedPackage = publisher.CopyPackage();
            var imported = DiscoveryCodec.ParsePackage(copiedPackage);
            var ciphertext = new byte[] { 1, 2, 3, 4 };
            var reference = new DiscoveryCredentialReference(Guid.NewGuid().ToString("N"), Convert.ToHexStringLower(SHA256.HashData(ciphertext)));
            var configuration = new RemoteConfiguration { MachineId = Guid.NewGuid(), MachineName = "fixture", DetailedReportingEnabled = false }
                .WithDiscovery(reference, imported.DiscoveryId, imported.SigningPublicKey, 1);
            var manager = new MultiTargetIntegrationManager(new Scheduler(), async (candidate, token) =>
            {
                await DiscoveryConnectionSetup.TestAsync(candidate, configPath, imported, readerClient, dashboardClient, token);
                return true;
            }, startup, new StoppedRuntime(), _ => slots);
            var plan = MultiTargetIntegrationManager.PreviewWithCredential(configuration, configPath, [], relayPath,
                false, new(reference, ciphertext), slots);
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyAsync(plan, default));
            Assert.False(File.Exists(configPath));
            Assert.Empty(slots.Values);
            var first = new Uri("https://first-51820.use.devtunnels.ms/");
            await publisher.PublishAsync(publisher.ObserveTunnel(first), default);
            var applied = await manager.ApplyAsync(plan, default);
            Assert.True(applied.SettingsCommitted);
            Assert.True(RemoteConfiguration.Load(configPath).IsDiscovery);
            var saved = await File.ReadAllBytesAsync(configPath);
            Assert.DoesNotContain("sig=", System.Text.Encoding.UTF8.GetString(saved));
            Assert.DoesNotContain("sig=", plan.Preview);
            client = new ClientCoordinator(configPath, clock,
                discoveryResolverFactory: _ => new DiscoveryEndpointResolver(imported, readerClient, clock, jitter: () => 0.5),
                endpointTransportFactory: (candidate, endpoint) => new PresenceTransport(candidate, endpoint, dashboardClient, clock));
            client.Start();
            await Eventually(() => client.Status().State == "connected");
            Assert.Equal(1, client.Status().EffectiveCredentialRevision);
            var firstReport = dashboard.Reports.Last();
            Assert.Equal(first.Host, firstReport.Host);
            var replacement = new Uri("https://second-51820.use.devtunnels.ms/");
            await publisher.PublishAsync(publisher.ObserveTunnel(replacement), default);
            clock.Advance(TimeSpan.FromSeconds(30));
            client.RequestSnapshot();
            await Eventually(() => dashboard.Reports.Any(r => r.Host == replacement.Host));
            var newRun = dashboard.Reports.First(r => r.Host == replacement.Host);
            Assert.Equal(PresenceKind.Started, newRun.Report.Kind);
            Assert.True(newRun.Report.Generation > firstReport.Report.Generation);
            Assert.DoesNotContain(dashboard.Reports, r => r.Host == first.Host && r.Report.Kind == PresenceKind.Offline);
            Assert.Equal(saved, await File.ReadAllBytesAsync(configPath));
            clock.Advance(TimeSpan.FromDays(61));
            await publisher.RenewAsync(default);
            Assert.Equal(copiedPackage, publisher.CopyPackage());
            Assert.Equal(clock.GetUtcNow().AddDays(90), azure.Remote.Policies.Single().ExpiresAtUtc);
            await publisher.PublishAsync(publisher.ObserveTunnel(replacement), default);
            client.RequestSnapshot();
            await Eventually(() => client.Status().State == "connected" && client.Status().EffectiveCredentialRevision == 1);
            var oldReader = journal.Value!.PolicyId;
            await publisher.RotateAsync(default);
            Assert.NotEqual(copiedPackage, publisher.CopyPackage());
            Assert.Contains(azure.Remote.Policies, p => p.Id == oldReader);
            await publisher.RetirePreviousAsync(default);
            Assert.DoesNotContain(azure.Remote.Policies, p => p.Id == oldReader);
            clock.Advance(TimeSpan.FromSeconds(30));
            client.RequestSnapshot();
            await Eventually(() => client.Status().State == "discovery-unavailable");
            Assert.Null(client.Status().EffectiveCredentialRevision);
            await publisher.RevokeAsync(false, default);
            Assert.Empty(azure.Remote.Policies);
            Assert.True(publisher.Status.Revoked);
            var reportCount = dashboard.Reports.Count;
            await client.StopAsync();
            Assert.Equal(reportCount, dashboard.Reports.Count);
            Assert.All(dashboard.Paths, uri => Assert.Empty(uri.Query));
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private sealed class Journal : IPrivateDiscoveryJournal
    {
        public PrivateDiscoveryRegistration? Value;
        public Task<PrivateDiscoveryRegistration?> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(PrivateDiscoveryRegistration registration, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Value = registration; return Task.CompletedTask; }
    }

    private sealed class AzureFixture(TimeProvider clock) : HttpMessageHandler, IPrivateDiscoveryAzure
    {
        public PrivateDiscoveryRemote? Remote;
        private long revision;
        private string ETag() => "\"" + Interlocked.Increment(ref revision) + "\"";
        public Task<PrivateDiscoveryAccountInfo> ValidateAccountAsync(CancellationToken token) =>
            Task.FromResult(new PrivateDiscoveryAccountInfo(new("https://fixtureaccount.blob.core.windows.net/"), "Enabled", "Allow", true, "None"));
        public Task VerifyAnonymousDeniedAsync(string container, CancellationToken token)
        { Assert.True(Remote!.IsPrivate); return Task.CompletedTask; }
        public Task<PrivateDiscoveryRemote?> InspectAsync(string container, CancellationToken token) => Task.FromResult(Remote);
        public Task CreateAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] initialDocument, CancellationToken token)
        {
            Assert.Null(Remote);
            Remote = new(ETag(), ETag(), metadata, true, [], [PrivateDiscoveryAzure.BlobName], metadata, initialDocument);
            return Task.CompletedTask;
        }
        public Task InitializeBlobAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] initialDocument, string containerETag, CancellationToken token)
            => throw new InvalidOperationException("Unexpected recovery in successful fixture.");
        public Task<IPrivateDiscoveryLease> AcquireAsync(string container, CancellationToken token) => Task.FromResult<IPrivateDiscoveryLease>(new Lease(this));
        public Task<Uri> CreateReadSasAsync(string container, string policyId, CancellationToken token) => Task.FromResult(new Uri(
            $"https://fixtureaccount.blob.core.windows.net/{container}/endpoint.json?sv=2025-06-01&sr=b&spr=https&si={policyId}&sig={Uri.EscapeDataString(Convert.ToBase64String(new byte[32]))}"));
        public Task VerifyReadSasAsync(Uri sas, byte[] expectedDocument, CancellationToken token)
        { Assert.Equal(Remote!.Document, expectedDocument); return Task.CompletedTask; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var policyId = request.RequestUri!.Query.TrimStart('?').Split('&').Single(p => p.StartsWith("si=", StringComparison.Ordinal))[3..];
            var policy = Remote?.Policies.SingleOrDefault(p => p.Id == policyId);
            if (policy is null || policy.ExpiresAtUtc <= clock.GetUtcNow()) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if (request.Headers.IfNoneMatch.Any(p => p.ToString() == Remote!.BlobETag))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Remote!.Document!) { Headers = { ContentType = new("application/json") } },
                Headers = { ETag = new(Remote.BlobETag!) }
            });
        }
        private sealed class Lease(AzureFixture owner) : IPrivateDiscoveryLease
        {
            public Task<PrivateDiscoveryRemote> ReadAsync(CancellationToken token) => Task.FromResult(owner.Remote!);
            public Task WritePoliciesAsync(IReadOnlyList<PrivateDiscoveryPolicy> policies, string etag, CancellationToken token)
            { Assert.Equal(owner.Remote!.ContainerETag, etag); owner.Remote = owner.Remote with { ContainerETag = owner.ETag(), Policies = policies.ToArray() }; return Task.CompletedTask; }
            public Task WriteBlobAsync(byte[] bytes, string etag, CancellationToken token)
            { Assert.Equal(owner.Remote!.BlobETag, etag); owner.Remote = owner.Remote with { BlobETag = owner.ETag(), Document = bytes }; return Task.CompletedTask; }
            public Task DeleteBlobAsync(string etag, CancellationToken token)
            { Assert.Equal(owner.Remote!.BlobETag, etag); owner.Remote = owner.Remote with { BlobETag = null, Blobs = [], Document = null, BlobMetadata = null }; return Task.CompletedTask; }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class DashboardFixture : HttpMessageHandler
    {
        public ConcurrentQueue<(string Host, PresenceReport Report)> Reports { get; } = new();
        public ConcurrentQueue<Uri> Paths { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Enqueue(request.RequestUri!);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new PresenceHealthResponse(PresenceProtocol.DisplayNameVersion, "ok"), Protocol.Json), System.Text.Encoding.UTF8, "application/json") };
            var report = JsonSerializer.Deserialize<PresenceReport>(await request.Content!.ReadAsByteArrayAsync(cancellationToken), PresenceProtocol.Json)!;
            Reports.Enqueue((request.RequestUri!.Host, report));
            return new(HttpStatusCode.Accepted);
        }
    }

    private sealed class Slots : IDiscoveryCredentialTransactionStore
    {
        public Dictionary<string, byte[]> Values { get; } = [];
        public byte[]? Read(DiscoveryCredentialReference reference) => Values.GetValueOrDefault(reference.Id);
        public void Write(DiscoveryCredentialReference reference, byte[] ciphertext) => Values.Add(reference.Id, ciphertext.ToArray());
        public void Delete(DiscoveryCredentialReference reference) => Values.Remove(reference.Id);
    }
    private sealed class Startup : IIntegrationStartup
    {
        public string? Read(string name) => null;
        public bool IsDisabled(string name) => false;
        public void Replace(string name, string? expected, string? command) { Assert.Null(expected); Assert.Null(command); }
    }
    private sealed class Scheduler : IIntegrationTaskScheduler
    {
        public string? ReadXml(string name) => null;
        public void Write(string name, string xml) => throw new InvalidOperationException("Unexpected task mutation.");
        public void Delete(string name) => throw new InvalidOperationException("Unexpected task mutation.");
    }
    private sealed class StoppedRuntime : IIntegrationRuntime
    {
        public Task<IntegrationRuntimeState> QueryAsync(string path, CancellationToken token) => Task.FromResult(new IntegrationRuntimeState(false));
        public Task<IntegrationRuntimeState> ReloadAsync(string path, CancellationToken token) => QueryAsync(path, token);
        public Task<IntegrationRuntimeState> StartAsync(string client, string path, CancellationToken token) => throw new InvalidOperationException("Apply must not start Client.");
        public Task StopAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task SuspendTranscriptAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task PauseTranscriptAsync(string path, CancellationToken token) => Task.CompletedTask;
        public Task ReloadTranscriptAsync(string path, CancellationToken token) => Task.CompletedTask;
    }
}