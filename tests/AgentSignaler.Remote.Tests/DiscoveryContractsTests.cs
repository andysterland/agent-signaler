using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using Xunit;

namespace AgentSignaler.Remote.Tests;

[SupportedOSPlatform("windows")]
public sealed class DiscoveryContractsTests
{
    private static readonly Guid Identity = Guid.Parse("d56d7d82-6abc-453b-9124-8b0d337f0be0");

    [Theory]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-65535.euw.devtunnels.ms")]
    public void AcceptsCanonicalPortQualifiedDevTunnelEndpoint(string endpoint) =>
        Assert.Equal(new Uri(endpoint), DiscoveryProtocol.ValidateEndpoint(new(endpoint)));

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://foo.devtunnels.ms/")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms.evil.example/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms/path")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms/?token=one")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms/#fragment")]
    [InlineData("https://user@my-tunnel-4318.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms:443/")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms:444/")]
    [InlineData("http://my-tunnel-4318.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-inspect-4318.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-relay-4318.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-65536.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-0.usw2.devtunnels.ms/")]
    [InlineData("https://my-tunnel-4318.usw2.devtunnels.ms/a/../")]
    public void RejectsNonCanonicalTunnelEndpoints(string endpoint) =>
        Assert.Throws<DiscoveryException>(() => DiscoveryProtocol.ValidateEndpoint(new(endpoint)));

    [Fact]
    public void ProtectedReplayStorePersistsMonotonicRevisionAndFingerprintAcrossInstances()
    {
        using var fixture = new StoreFixture();
        var store = new CurrentUserDiscoveryReplayStore(fixture.Root);
        Assert.Null(store.Load(Identity));
        var initial = new DiscoveryReplayState(Identity, 1, new string('a', 64));
        store.Save(initial);
        Assert.Equal(initial, new CurrentUserDiscoveryReplayStore(fixture.Root).Load(Identity));
        store.Save(initial);
        Assert.Throws<DiscoveryException>(() => store.Save(initial with { PayloadFingerprint = new string('b', 64) }));
        store.Save(initial with { Revision = 2 });
        Assert.Throws<DiscoveryException>(() => store.Save(initial));
        Assert.Equal(2, store.Load(Identity)!.Revision);
        Assert.Single(Directory.GetFiles(fixture.Root));
        Assert.Throws<DiscoveryException>(() => store.Save(initial with { Revision = 0 }));
        Assert.Throws<DiscoveryException>(() => store.Save(initial with { PayloadFingerprint = "bad" }));
        var path = Path.Combine(fixture.Root, Identity.ToString("N") + ".replay");
        var ciphertext = File.ReadAllBytes(path);
        Assert.DoesNotContain(initial.PayloadFingerprint, Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        ciphertext[0] ^= 1;
        File.WriteAllBytes(path, ciphertext);
        Assert.Throws<DiscoveryException>(() => store.Load(Identity));
        Assert.Throws<DiscoveryException>(() => store.Save(initial with { Revision = 3 }));
        Assert.Equal(ciphertext, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadOnlyReplayLoadNeverCreatesDirectoriesOrPromotesPendingState()
    {
        using var fixture = new StoreFixture();
        var store = new CurrentUserDiscoveryReplayStore(fixture.Root);
        Assert.Null(store.LoadReadOnly(Identity));
        Assert.False(Directory.Exists(fixture.Root));
        Assert.Null(new CurrentUserDiscoveryReplayStore(Path.Combine(fixture.Root, "absent", "nested")).LoadReadOnly(Identity));
        Assert.False(Directory.Exists(fixture.Root));
        var state = new DiscoveryReplayState(Identity, 9, new string('a', 64));
        store.Save(state);
        var path = Path.Combine(fixture.Root, Identity.ToString("N") + ".replay");
        var pending = path + ".pending";
        Assert.Equal(state, store.LoadReadOnly(Identity));
        File.Move(path, pending);
        var before = File.ReadAllBytes(pending);
        Assert.Equal(state, store.LoadReadOnly(Identity));
        Assert.False(File.Exists(path));
        Assert.Equal(before, File.ReadAllBytes(pending));
        before[0] ^= 1;
        File.WriteAllBytes(pending, before);
        Assert.Throws<DiscoveryException>(() => store.LoadReadOnly(Identity));
        Assert.False(File.Exists(path));
        Assert.Equal(before, File.ReadAllBytes(pending));
        Assert.Single(Directory.GetFiles(fixture.Root));
        var directory = new DirectoryInfo(fixture.Root);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.Equal("discovery_store_acl", Assert.Throws<DiscoveryException>(() => store.LoadReadOnly(Identity)).Code);
    }

    [Fact]
    public void ProtectedReplayStoreRecoversOnlyAuthenticatedAtomicStagingWithoutResettingCorruption()
    {
        using var fixture = new StoreFixture();
        var store = new CurrentUserDiscoveryReplayStore(fixture.Root);
        var state = new DiscoveryReplayState(Identity, 7, new string('a', 64));
        store.Save(state);
        var path = Path.Combine(fixture.Root, Identity.ToString("N") + ".replay");
        var pending = path + ".pending";
        File.Move(path, pending);
        Assert.Equal(state, new CurrentUserDiscoveryReplayStore(fixture.Root).Load(Identity));
        Assert.False(File.Exists(pending));
        Assert.True(File.Exists(path));
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<DiscoveryException>(() => store.Save(state with { Revision = 8 }));
        Assert.Equal(state, store.Load(Identity));
        Assert.Single(Directory.GetFiles(fixture.Root));
        File.Move(path, pending);
        var corrupt = File.ReadAllBytes(pending);
        corrupt[0] ^= 1;
        File.WriteAllBytes(pending, corrupt);
        Assert.Throws<DiscoveryException>(() => store.Load(Identity));
        Assert.False(File.Exists(path));
        Assert.Equal(corrupt, File.ReadAllBytes(pending));
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string parent = Path.Combine(Path.GetTempPath(), "discovery-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public StoreFixture()
        {
            Directory.CreateDirectory(parent);
            Root = Path.Combine(parent, "credentials");
        }
        public void Dispose() => Directory.Delete(parent, true);
    }
}
