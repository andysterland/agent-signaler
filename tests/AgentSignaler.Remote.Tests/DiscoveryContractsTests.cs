using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AgentSignaler.Remote.Tests;

[SupportedOSPlatform("windows")]
public sealed class DiscoveryContractsTests
{
    private static readonly Guid Identity = Guid.Parse("d56d7d82-6abc-453b-9124-8b0d337f0be0");
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private const string Sas = "https://agenttest.blob.core.windows.net/discovery/endpoint.json?sv=2025-06-01&sr=b&spr=https&si=reader-one&sig=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%3D";

    [Fact]
    public void PackageRoundTripsWithPinnedKeyAndSecretSafeDisplay()
    {
        using var key = Key();
        var package = Package(key);
        var encoded = DiscoveryCodec.EncodePackage(package);
        Assert.StartsWith(DiscoveryProtocol.PackagePrefix, encoded);
        Assert.Equal(package, DiscoveryCodec.ParsePackage(encoded));
        Assert.DoesNotContain("sig=", package.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("blob.core", package.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("devtunnels", Document().ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://evil.example/container/endpoint.json")]
    [InlineData("http://agenttest.blob.core.windows.net/discovery/endpoint.json")]
    [InlineData("https://agenttest.blob.core.windows.net.evil.example/discovery/endpoint.json")]
    [InlineData("https://agenttest.blob.core.usgovcloudapi.net/discovery/endpoint.json")]
    [InlineData("https://agenttest.blob.core.windows.net/discovery")]
    [InlineData("https://user@agenttest.blob.core.windows.net/discovery/endpoint.json")]
    [InlineData("https://agenttest.blob.core.windows.net:443/discovery/endpoint.json")]
    [InlineData("https://agenttest.blob.core.windows.net/discovery/%2e%2e/endpoint.json")]
    public void RejectsNonCanonicalOrNonPublicCloudBlobScopes(string uri)
    {
        using var key = Key();
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.EncodePackage(Package(key) with { BlobSasUri = new(uri) }));
    }

    [Theory]
    [InlineData("sr=b", "sr=c")]
    [InlineData("spr=https", "spr=https,http")]
    [InlineData("si=reader-one", "sp=r")]
    [InlineData("si=reader-one", "si=reader-one&sp=rw")]
    [InlineData("si=reader-one", "si=reader-one&se=2026-12-01")]
    [InlineData("si=reader-one", "si=reader-one&sip=127.0.0.1")]
    [InlineData("sr=b", "sr=b&sr=b")]
    [InlineData("sig=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA%3D", "sig=bad")]
    [InlineData("sv=2025-06-01", "sv=not-a-version")]
    public void RejectsUnsafeOrAmbiguousSas(string oldValue, string newValue)
    {
        using var key = Key();
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.EncodePackage(Package(key) with { BlobSasUri = new(Sas.Replace(oldValue, newValue, StringComparison.Ordinal)) }));
    }

    [Fact]
    public void StrictPackageShapeVersionBase64AndExactSizeBoundaries()
    {
        using var key = Key();
        var json = PackageJson(key);
        foreach (var invalid in new[]
        {
            json.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            json.Replace("\"version\":1", "\"version\":1,\"unknown\":true", StringComparison.Ordinal),
            json.Replace("\"version\":1", "\"version\":\"1\"", StringComparison.Ordinal),
            json.Replace("\"version\":1", "\"Version\":1", StringComparison.Ordinal),
            json.Replace(Identity.ToString("D"), Guid.Empty.ToString("D"), StringComparison.Ordinal)
        })
            Assert.Throws<DiscoveryException>(() => DiscoveryCodec.ParsePackage(EncodeJson(invalid)));
        var package = DiscoveryCodec.EncodePackage(Package(key));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.ParsePackage(package + "="));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.ParsePackage(" " + package));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.ParsePackage(package + "\n"));
        var maximumDecoded = (DiscoveryProtocol.MaximumPackageBytes - DiscoveryProtocol.PackagePrefix.Length) * 3 / 4;
        var atLimit = EncodeJson(json.PadRight(maximumDecoded, ' '));
        Assert.Equal(DiscoveryProtocol.MaximumPackageBytes - 1, atLimit.Length);
        Assert.Equal(Identity, DiscoveryCodec.ParsePackage(atLimit).DiscoveryId);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.ParsePackage(atLimit + "A"));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.ParsePackage(EncodeJson(json.PadRight(maximumDecoded + 1, ' '))));
    }

    [Fact]
    public void RejectsNonP256OrMalformedSpki()
    {
        using var key = Key();
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var package = Package(key) with { SigningPublicKey = Convert.ToBase64String(wrong.ExportSubjectPublicKeyInfo()) };
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.EncodePackage(package));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.EncodePackage(Package(key) with { SigningPublicKey = "not-a-key" }));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Sign(Document(), wrong));
    }

    [Fact]
    public void ExactSignedBytesUseP1363AndRoundTripAvailableAndUnavailable()
    {
        using var key = Key();
        foreach (var document in new[] { Document(), Document() with { State = DiscoveryState.Unavailable, DashboardBaseUrl = null } })
        {
            var signed = DiscoveryCodec.Sign(document, key);
            using var json = JsonDocument.Parse(signed);
            var payload = json.RootElement.GetProperty("payload").GetBytesFromBase64();
            var signature = json.RootElement.GetProperty("signature").GetBytesFromBase64();
            Assert.Equal(64, signature.Length);
            Assert.True(key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            var verified = DiscoveryCodec.Verify(signed, Package(key), Now);
            Assert.Equal(document, verified.Document);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), verified.ReplayState.PayloadFingerprint);
        }
    }

    [Fact]
    public void SignatureIsCheckedBeforePayloadParsingAndPinsIdentityAndKey()
    {
        using var key = Key();
        using var wrong = Key();
        var signed = DiscoveryCodec.Sign(Document(), key);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(signed, Package(wrong), Now));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(signed, Package(key) with { DiscoveryId = Guid.NewGuid() }, Now));
        var malformed = Envelope(Encoding.UTF8.GetBytes("not json"), new byte[64]);
        Assert.Equal("discovery_signature", Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(malformed, Package(key), Now)).Code);
        using var parsed = JsonDocument.Parse(signed);
        var payload = parsed.RootElement.GetProperty("payload").GetBytesFromBase64();
        var signature = parsed.RootElement.GetProperty("signature").GetBytesFromBase64();
        var altered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload) + " ");
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(Envelope(altered, signature), Package(key), Now));
    }

    [Fact]
    public void StrictEnvelopeRejectsUnknownDuplicateMalformedDepthAndOversize()
    {
        using var key = Key();
        var signed = DiscoveryCodec.Sign(Document(), key);
        var json = Encoding.UTF8.GetString(signed);
        foreach (var invalid in new[]
        {
            json.Insert(1, "\"unknown\":true,"),
            json.Insert(1, "\"payload\":\"AAAA\","),
            json.Insert(1, "\"signature\":\"AAAA\","),
            json.Replace("\"payload\":", "\"payload\":null,\"unused\":", StringComparison.Ordinal),
            "{\"payload\":" + new string('[', 9) + "0" + new string(']', 9) + ",\"signature\":\"AA==\"}"
        })
            Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(Encoding.UTF8.GetBytes(invalid), Package(key), Now));
        var atLimit = Encoding.UTF8.GetBytes(json.PadRight(DiscoveryProtocol.MaximumEnvelopeBytes, ' '));
        Assert.Equal(Document(), DiscoveryCodec.Verify(atLimit, Package(key), Now).Document);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify([.. atLimit, (byte)' '], Package(key), Now));
    }

    [Theory]
    [InlineData("\"revision\":1", "\"revision\":0")]
    [InlineData("\"revision\":1", "\"revision\":9223372036854775808")]
    [InlineData("\"version\":1", "\"version\":2")]
    [InlineData("\"version\":1", "\"version\":1,\"version\":1")]
    [InlineData("\"version\":1", "\"version\":1,\"extra\":false")]
    [InlineData("\"state\":\"available\"", "\"state\":\"unknown\"")]
    [InlineData("\"state\":\"available\"", "\"state\":\"unavailable\"")]
    [InlineData("+00:00", "+01:00")]
    public void AuthenticatedMalformedPayloadIsRejected(string oldValue, string newValue)
    {
        using var key = Key();
        using var json = JsonDocument.Parse(DiscoveryCodec.Sign(Document(), key));
        var payload = Encoding.UTF8.GetString(json.RootElement.GetProperty("payload").GetBytesFromBase64());
        var changed = Encoding.UTF8.GetBytes(payload.Replace(oldValue, newValue, StringComparison.Ordinal));
        Assert.NotEqual(payload, Encoding.UTF8.GetString(changed));
        var signature = key.SignData(changed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(Envelope(changed, signature), Package(key), Now));
    }

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
    public void FreshnessUsesExactExpiryAndFiveMinuteFutureBound()
    {
        using var key = Key();
        var package = Package(key);
        var future = Document() with { PublishedAtUtc = Now.AddMinutes(5), ExpiresAtUtc = Now.AddMinutes(5).AddHours(24) };
        Assert.Equal(future, DiscoveryCodec.Verify(DiscoveryCodec.Sign(future, key), package, Now).Document);
        var tooFuture = future with { PublishedAtUtc = future.PublishedAtUtc.AddTicks(1), ExpiresAtUtc = future.ExpiresAtUtc.AddTicks(1) };
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(DiscoveryCodec.Sign(tooFuture, key), package, Now));
        var signed = DiscoveryCodec.Sign(Document(), key);
        Assert.Equal(Document(), DiscoveryCodec.Verify(signed, package, Document().ExpiresAtUtc.AddTicks(-1)).Document);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(signed, package, Document().ExpiresAtUtc));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Sign(Document() with { ExpiresAtUtc = Now.AddHours(24).AddTicks(1) }, key));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Sign(Document() with { ExpiresAtUtc = Now }, key));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Sign(Document() with { PublishedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) }, key));
    }

    [Fact]
    public void ReplayRejectsLowerRevisionOrChangedBytesButRevalidatesIdenticalPayload()
    {
        using var key = Key();
        var package = Package(key);
        var signed = DiscoveryCodec.Sign(Document(), key);
        var initial = DiscoveryCodec.Verify(signed, package, Now);
        Assert.Equal(initial, DiscoveryCodec.Verify(signed, package, Now, initial.ReplayState));
        var higher = DiscoveryCodec.Verify(DiscoveryCodec.Sign(Document() with { Revision = 2 }, key), package, Now, initial.ReplayState);
        Assert.Equal(2, higher.ReplayState.Revision);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(signed, package, Now, higher.ReplayState));
        var changed = DiscoveryCodec.Sign(Document() with { State = DiscoveryState.Unavailable, DashboardBaseUrl = null }, key);
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(changed, package, Now, initial.ReplayState));
        Assert.Throws<DiscoveryException>(() => DiscoveryCodec.Verify(signed, package, Now, initial.ReplayState with { DiscoveryId = Guid.NewGuid() }));
        var maximum = DiscoveryCodec.Verify(DiscoveryCodec.Sign(Document() with { Revision = long.MaxValue }, key), package, Now);
        Assert.Equal(long.MaxValue, maximum.ReplayState.Revision);
    }

    [Fact]
    public async Task ProtectedStoreRoundTripsCiphertextAndSigningKeysWithoutPlaintextFiles()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        var package = Package(key);
        var reference = await fixture.Store.StoreAsync(package);
        var path = Path.Combine(fixture.Root, reference.Id + ".bin");
        var ciphertext = await File.ReadAllBytesAsync(path);
        Assert.Equal(reference.CiphertextHash, Convert.ToHexStringLower(SHA256.HashData(ciphertext)));
        Assert.DoesNotContain("agentsignaler-discovery", Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", Encoding.UTF8.GetString(ciphertext), StringComparison.Ordinal);
        Assert.Equal(package, await fixture.Store.LoadAsync(reference));
        var signingReference = await fixture.Store.CreateSigningKeyAsync();
        using var signingKey = await fixture.Store.LoadSigningKeyAsync(signingReference);
        using var signingAgain = await fixture.Store.LoadSigningKeyAsync(signingReference);
        Assert.Equal(DiscoveryCodec.ExportPublicKey(signingKey), DiscoveryCodec.ExportPublicKey(signingAgain));
        var signingCiphertext = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, signingReference.Id + ".bin"));
        using var plaintextParser = Key();
        Assert.Throws<CryptographicException>(() => plaintextParser.ImportPkcs8PrivateKey(signingCiphertext, out _));
        Assert.NotEqual(reference.Id, signingReference.Id);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(signingReference));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadSigningKeyAsync(reference));
        await fixture.Store.DeleteAsync(reference);
        await fixture.Store.DeleteAsync(signingReference);
        Assert.Empty(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task ProtectedStoreRejectsHashMismatchTamperingInvalidReferencesAndCancellation()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        var reference = await fixture.Store.StoreAsync(Package(key));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference with { CiphertextHash = new string('0', 64) }));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.DeleteAsync(reference with { CiphertextHash = new string('0', 64) }));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference with { Id = "..\\foreign" }));
        var path = Path.Combine(fixture.Root, reference.Id + ".bin");
        var ciphertext = File.ReadAllBytes(path);
        ciphertext[0] ^= 1;
        File.WriteAllBytes(path, ciphertext);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference));
        var modifiedReference = reference with { CiphertextHash = Convert.ToHexStringLower(SHA256.HashData(ciphertext)) };
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(modifiedReference));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.StoreAsync(Package(key), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.LoadAsync(reference, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.DeleteAsync(reference, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.CreateSigningKeyAsync(cancellation.Token));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

    [Fact]
    public async Task ProtectedStoreRefusesForeignAclAndDoesNotAdoptExistingDirectory()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        Directory.CreateDirectory(fixture.Root);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.StoreAsync(Package(key)));
        Directory.Delete(fixture.Root);
        var reference = await fixture.Store.StoreAsync(Package(key));
        var directory = new DirectoryInfo(fixture.Root);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        directory.SetAccessControl(security);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.DeleteAsync(reference));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.StoreAsync(Package(key)));
        Assert.Single(Directory.GetFiles(fixture.Root));
    }

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

    [Fact]
    public async Task PreparedCredentialsAreCiphertextOnlyCreateOnlyAndRecoverable()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        var prepared = CurrentUserDiscoveryCredentialStore.Prepare(Package(key));
        Assert.False(Directory.Exists(fixture.Root));
        Assert.DoesNotContain("sig=", Encoding.UTF8.GetString(prepared.Ciphertext), StringComparison.Ordinal);
        await fixture.Store.WritePreparedAsync(prepared);
        Assert.Equal(prepared.Ciphertext, await fixture.Store.ReadCiphertextAsync(prepared.Reference));
        await fixture.Store.WritePreparedAsync(prepared);
        Assert.Single(Directory.GetFiles(fixture.Root));
        await fixture.Store.DeleteAsync(prepared.Reference);
        Assert.Null(await fixture.Store.ReadCiphertextAsync(prepared.Reference));
        await fixture.Store.WritePreparedAsync(prepared);
        Assert.Equal(Package(key), await fixture.Store.LoadAsync(prepared.Reference));
        var changed = prepared.Ciphertext.ToArray();
        changed[0] ^= 1;
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.WritePreparedAsync(prepared with { Ciphertext = changed }));
        var foreignPath = Path.Combine(fixture.Root, prepared.Reference.Id + ".bin");
        File.WriteAllBytes(foreignPath, [1, 2, 3]);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.WritePreparedAsync(prepared));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(foreignPath));
    }

    [Fact]
    public async Task PublicStoreFailuresExposeOnlyFreshCategoryCodes()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        var prepared = CurrentUserDiscoveryCredentialStore.Prepare(Package(key));
        await fixture.Store.WritePreparedAsync(prepared);
        await fixture.Store.DeleteAsync(prepared.Reference);
        var foreignPath = Path.Combine(fixture.Root, prepared.Reference.Id + ".bin");
        Directory.CreateDirectory(foreignPath);
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.WritePreparedAsync(prepared));
        Assert.Equal("discovery_store_io", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.Root, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", failure.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(foreignPath));
        Assert.DoesNotContain(prepared.Reference.Id, prepared.Reference.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedStoreRejectsDirectoryJunctionsWithoutFollowingOrMutatingThem()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        var reference = await fixture.Store.StoreAsync(Package(key));
        var target = fixture.Root + "-original";
        Directory.Move(fixture.Root, target);
        Directory.CreateDirectory(fixture.Root);
        CreateJunction(fixture.Root, target);
        try
        {
            Assert.Equal("discovery_store_reparse", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference))).Code);
            Assert.Throws<DiscoveryException>(() => new CurrentUserDiscoveryReplayStore(fixture.Root).LoadReadOnly(Identity));
            await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.DeleteAsync(reference));
            await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.StoreAsync(Package(key)));
            var descendant = new CurrentUserDiscoveryCredentialStore(Path.Combine(fixture.Root, "nested"));
            await Assert.ThrowsAsync<DiscoveryException>(() => descendant.StoreAsync(Package(key)));
            Assert.Single(Directory.GetFiles(target));
            Assert.False(Directory.Exists(Path.Combine(target, "nested")));
        }
        finally { Directory.Delete(fixture.Root); }
    }

    [Fact]
    public async Task ProtectedStoreRefusesFileAclDriftAndOversizeCiphertext()
    {
        using var fixture = new StoreFixture();
        using var key = Key();
        var reference = await fixture.Store.StoreAsync(Package(key));
        var file = new FileInfo(Path.Combine(fixture.Root, reference.Id + ".bin"));
        var altered = file.GetAccessControl();
        var foreignRule = new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read, AccessControlType.Allow);
        altered.AddAccessRule(foreignRule);
        file.SetAccessControl(altered);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference));
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.DeleteAsync(reference));
        altered.RemoveAccessRuleAll(foreignRule);
        file.SetAccessControl(altered);
        Assert.Equal(Package(key), await fixture.Store.LoadAsync(reference));
        File.WriteAllBytes(file.FullName, new byte[24 * 1024 + 1]);
        Assert.Equal("discovery_store_size", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Store.LoadAsync(reference))).Code);
    }

    private static ECDsa Key() => ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static DiscoveryConnectionPackage Package(ECDsa key) => new(Identity, new(Sas), DiscoveryCodec.ExportPublicKey(key));
    private static DiscoveryDocument Document() => new(1, Identity, 1, Now, Now.AddHours(24), DiscoveryState.Available,
        new("https://my-tunnel-4318.usw2.devtunnels.ms/"));
    private static string PackageJson(ECDsa key) => JsonSerializer.Serialize(new
    {
        version = 1, discoveryId = Identity, blobSasUri = Sas, signingPublicKey = DiscoveryCodec.ExportPublicKey(key)
    });
    private static string EncodeJson(string json) => DiscoveryProtocol.PackagePrefix +
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Envelope(byte[] payload, byte[] signature) => JsonSerializer.SerializeToUtf8Bytes(new { payload, signature });

    private static void CreateJunction(string path, string target)
    {
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var data = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), checked((ushort)(data.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), checked((ushort)print.Length));
        substitute.CopyTo(data, 16);
        print.CopyTo(data, 18 + substitute.Length);
        using var handle = CreateFile(path, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        Assert.True(DeviceIoControl(handle, 0x000900A4, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength,
        IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    private sealed class StoreFixture : IDisposable
    {
        private readonly string parent = Path.Combine(Path.GetTempPath(), "discovery-" + Guid.NewGuid().ToString("N"));
        public string Root { get; }
        public CurrentUserDiscoveryCredentialStore Store { get; }
        public StoreFixture()
        {
            Directory.CreateDirectory(parent);
            Root = Path.Combine(parent, "credentials");
            Store = new(Root);
        }
        public void Dispose() => Directory.Delete(parent, true);
    }
}
