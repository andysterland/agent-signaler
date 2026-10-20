using System.Net;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

[SupportedOSPlatform("windows")]
public sealed class DiscoveryConnectionSetupTests : IDisposable
{
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string Sas = "https://agenttest.blob.core.windows.net/discovery/endpoint.json?sv=2025-06-01&sr=b&spr=https&si=reader-one&sig=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%3D";
    private string ConfigPath => Path.Combine(root, "remote.json");
    private DiscoveryConnectionPackage Package => new(Guid.NewGuid(), new(Sas), DiscoveryCodec.ExportPublicKey(key));

    private RemoteConfiguration Configuration(DiscoveryConnectionPackage package) =>
        new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDiscovery(
            new(Guid.NewGuid().ToString("N"), new string('a', 64)), package.DiscoveryId, package.SigningPublicKey, 1);

    private byte[] Document(DiscoveryConnectionPackage package, DiscoveryState state = DiscoveryState.Available, long revision = 1)
    {
        var now = DateTimeOffset.UtcNow;
        return DiscoveryCodec.Sign(new(1, package.DiscoveryId, revision, now, now.AddHours(24), state,
            state == DiscoveryState.Available ? new("https://synthetic-4318.usw2.devtunnels.ms/") : null), key);
    }

    [Fact]
    public async Task ImportedPackageTestReadsSignedBlobAndReceiverWithoutPersistingAnything()
    {
        var package = Package;
        var config = Configuration(package);
        using var discovery = new HttpClient(new Handler(request =>
        {
            Assert.Equal(package.BlobSasUri, request.RequestUri);
            return Json(Document(package));
        }));
        var healthRequests = 0;
        using var dashboard = new HttpClient(new Handler(request =>
        {
            healthRequests++;
            Assert.Equal("synthetic-4318.usw2.devtunnels.ms", request.RequestUri!.Host);
            Assert.Empty(request.RequestUri.Query);
            Assert.Empty(request.Headers.Authorization?.ToString() ?? "");
            return Json(JsonSerializer.SerializeToUtf8Bytes(new PresenceHealthResponse(PresenceProtocol.DisplayNameVersion, "ok"), Protocol.Json));
        }));
        var endpoint = await DiscoveryConnectionSetup.TestAsync(config, ConfigPath, package, discovery, dashboard);
        Assert.Equal("https://synthetic-4318.usw2.devtunnels.ms/", endpoint.BaseUri.AbsoluteUri);
        Assert.Equal(1, healthRequests);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task WholeSetupOwnsTenSecondDeadlineEvenWhenCallerAndHealthHandlerProvideNone()
    {
        var package = Package;
        using var discovery = new HttpClient(new Handler(_ => Json(Document(package))));
        var stalled = new StalledHealth();
        using var dashboard = new HttpClient(stalled) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DiscoveryConnectionSetup.TestAsync(
                Configuration(package), ConfigPath, package, discovery, dashboard).WaitAsync(TimeSpan.FromSeconds(13)));
            Assert.True(stalled.Requested);
            Assert.False(Directory.Exists(root));
        }
        finally { stalled.Response.TrySetCanceled(); }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task SetupHonorsDurableCheckpointWithoutWritingOrAdvancingIt(long servedRevision, bool accepted)
    {
        Directory.CreateDirectory(root);
        var package = Package;
        var checkpointDocument = Document(package, revision: 2);
        var checkpoint = DiscoveryCodec.Verify(checkpointDocument, package, DateTimeOffset.UtcNow).ReplayState;
        var replayDirectory = Path.Combine(root, "discovery-replay");
        var replay = new CurrentUserDiscoveryReplayStore(replayDirectory);
        replay.Save(checkpoint);
        var replayPath = Path.Combine(replayDirectory, package.DiscoveryId.ToString("N") + ".replay");
        var before = File.ReadAllBytes(replayPath);
        var document = servedRevision == 2 ? checkpointDocument : Document(package, revision: servedRevision);
        using var discovery = new HttpClient(new Handler(_ => Json(document)));
        var healthCalls = 0;
        using var dashboard = new HttpClient(new Handler(_ =>
        {
            healthCalls++;
            return Json(JsonSerializer.SerializeToUtf8Bytes(new PresenceHealthResponse(PresenceProtocol.DisplayNameVersion, "ok"), Protocol.Json));
        }));
        if (accepted)
            await DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery, dashboard);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery, dashboard));
        Assert.Equal(accepted ? 1 : 0, healthCalls);
        Assert.Equal(before, File.ReadAllBytes(replayPath));
        Assert.Equal(checkpoint, replay.Load(package.DiscoveryId));
        Assert.Single(Directory.GetFiles(replayDirectory));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task ReadOnlySetupUsesPendingCheckpointWithoutRecoveringOrRenamingIt()
    {
        Directory.CreateDirectory(root);
        var package = Package;
        var document = Document(package, revision: 2);
        var state = DiscoveryCodec.Verify(document, package, DateTimeOffset.UtcNow).ReplayState;
        var replayDirectory = Path.Combine(root, "discovery-replay");
        new CurrentUserDiscoveryReplayStore(replayDirectory).Save(state);
        var replayPath = Path.Combine(replayDirectory, package.DiscoveryId.ToString("N") + ".replay");
        var pendingPath = replayPath + ".pending";
        File.Move(replayPath, pendingPath);
        var before = File.ReadAllBytes(pendingPath);
        using var discovery = new HttpClient(new Handler(_ => Json(document)));
        using var dashboard = new HttpClient(new Handler(_ => Json(JsonSerializer.SerializeToUtf8Bytes(
            new PresenceHealthResponse(PresenceProtocol.DisplayNameVersion, "ok"), Protocol.Json))));
        await DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery, dashboard);
        Assert.False(File.Exists(replayPath));
        Assert.Equal(before, File.ReadAllBytes(pendingPath));
        Assert.Single(Directory.GetFiles(replayDirectory));
    }

    [Fact]
    public async Task CorruptDurableCheckpointBlocksSetupBeforeAnyNetwork()
    {
        Directory.CreateDirectory(root);
        var package = Package;
        var state = DiscoveryCodec.Verify(Document(package), package, DateTimeOffset.UtcNow).ReplayState;
        var replayDirectory = Path.Combine(root, "discovery-replay");
        new CurrentUserDiscoveryReplayStore(replayDirectory).Save(state);
        var replayPath = Path.Combine(replayDirectory, package.DiscoveryId.ToString("N") + ".replay");
        File.WriteAllBytes(replayPath, [1, 2, 3]);
        using var discovery = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request expected.")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery));
        Assert.Null(error.InnerException);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(replayPath));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task ReaderExceptionsNeverExposeSasOrRawInnerException()
    {
        var package = Package;
        using var discovery = new HttpClient(new Handler(_ => throw new HttpRequestException("SECRET-SAS " + Sas,
            new InvalidOperationException("SECRET-INNER " + Sas))));
        using var dashboard = new HttpClient(new Handler(_ => throw new InvalidOperationException("Receiver must not be contacted.")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery, dashboard));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Sas, error.ToString());
        Assert.DoesNotContain("SECRET", error.ToString());
        Assert.DoesNotContain("sig=", error.ToString());
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task ReaderFailureDoesNotProbeReceiverOrWriteSettings(HttpStatusCode status)
    {
        var package = Package;
        using var discovery = new HttpClient(new Handler(_ => new(status)));
        using var dashboard = new HttpClient(new Handler(_ => throw new InvalidOperationException("Receiver must not be contacted.")));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(
            Configuration(package), ConfigPath, package, discovery, dashboard));
        Assert.DoesNotContain("sig=", error.ToString());
        Assert.DoesNotContain("anonymous", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task SignedUnavailableBlocksApplyPreflightWithoutReceiverContact()
    {
        var package = Package;
        using var discovery = new HttpClient(new Handler(_ => Json(Document(package, DiscoveryState.Unavailable))));
        using var dashboard = new HttpClient(new Handler(_ => throw new InvalidOperationException("Receiver must not be contacted.")));
        await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery, dashboard));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task MismatchedPinnedIdentityBlocksBeforeNetwork()
    {
        var package = Package;
        using var discovery = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request expected.")));
        var config = Configuration(package) with { DiscoveryId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(config, ConfigPath, package, discovery));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task TamperedSignatureBlocksBeforeReceiverContact()
    {
        var package = Package;
        var bytes = Document(package);
        using var parsed = JsonDocument.Parse(bytes);
        var signature = Convert.FromBase64String(parsed.RootElement.GetProperty("signature").GetString()!);
        signature[0] ^= 1;
        var tampered = JsonSerializer.SerializeToUtf8Bytes(new { payload = parsed.RootElement.GetProperty("payload").GetString(), signature = Convert.ToBase64String(signature) });
        using var discovery = new HttpClient(new Handler(_ => Json(tampered)));
        using var dashboard = new HttpClient(new Handler(_ => throw new InvalidOperationException("Receiver must not be contacted.")));
        await Assert.ThrowsAsync<InvalidDataException>(() => DiscoveryConnectionSetup.TestAsync(Configuration(package), ConfigPath, package, discovery, dashboard));
        Assert.False(Directory.Exists(root));
    }

    private static HttpResponseMessage Json(byte[] bytes) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Encoding.UTF8.GetString(bytes), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }

    private sealed class StalledHealth : HttpMessageHandler
    {
        public bool Requested;
        public TaskCompletionSource<HttpResponseMessage> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested = true;
            return Response.Task;
        }
    }

    public void Dispose()
    {
        key.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
