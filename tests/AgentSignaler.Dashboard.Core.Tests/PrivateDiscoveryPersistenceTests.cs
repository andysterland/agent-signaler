using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using AgentSignaler.Dashboard;
using AgentSignaler.Discovery;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class PrivateDiscoveryPersistenceTests
{
    [Fact]
    public void SecretContainingRecordsHaveRedactedStringRepresentations()
    {
        var registration = Registration();
        Assert.Equal("Private discovery registration (protected)", registration.ToString());
        var reference = new DiscoveryCredentialReference("secret-reference", "secret-hash");
        Assert.DoesNotContain("secret", reference.ToString(), StringComparison.Ordinal);
        var staged = new DiscoveryPreparedCredential(reference, "secret-ciphertext"u8.ToArray());
        Assert.DoesNotContain("secret", staged.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JournalProtectsSecretsWithCurrentUserOnlyAclAndRoundTripsState()
    {
        using var directory = new OwnedDirectory();
        var journal = new PrivateDiscoveryJournal(directory.Path, () => true);
        var registration = Registration();
        await journal.SaveAsync(registration, default);
        var bytes = await File.ReadAllBytesAsync(System.IO.Path.Combine(directory.Path, "private-discovery.dpapi"));
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(registration.SigningKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-package-value", text, StringComparison.Ordinal);
        var restored = await journal.LoadAsync(default);
        Assert.Equal(registration.SigningKey, restored!.SigningKey);
        Assert.Equal(registration.Package, restored.Package);
        Assert.Equal(registration.DiscoveryId, restored.DiscoveryId);
        var security = new FileInfo(System.IO.Path.Combine(directory.Path, "private-discovery.dpapi")).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Single(security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)));
    }

    [Fact]
    public async Task JournalRejectsExternalCiphertextEditsAndPreservesThem()
    {
        using var directory = new OwnedDirectory();
        var journal = new PrivateDiscoveryJournal(directory.Path, () => true);
        var registration = Registration();
        await journal.SaveAsync(registration, default);
        var path = System.IO.Path.Combine(directory.Path, "private-discovery.dpapi");
        var original = await File.ReadAllBytesAsync(path);
        var altered = original.ToArray();
        altered[^1] ^= 1;
        await File.WriteAllBytesAsync(path, altered);
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.SaveAsync(registration with { Revision = 2 }, default));
        Assert.Equal(altered, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.new"));
    }

    [Fact]
    public async Task JournalRejectsWeakenedAclRatherThanAdoptingFile()
    {
        using var directory = new OwnedDirectory();
        var journal = new PrivateDiscoveryJournal(directory.Path, () => true);
        await journal.SaveAsync(Registration(), default);
        var file = new FileInfo(System.IO.Path.Combine(directory.Path, "private-discovery.dpapi"));
        var security = file.GetAccessControl();
        security.SetAccessRuleProtection(false, true);
        file.SetAccessControl(security);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => journal.LoadAsync(default));
    }

    [Fact]
    public async Task LostCanonicalLeasePreventsAnyJournalMutation()
    {
        using var directory = new OwnedDirectory();
        var journal = new PrivateDiscoveryJournal(directory.Path, () => false);
        await Assert.ThrowsAsync<DashboardOwnershipException>(() => journal.SaveAsync(Registration(), default));
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RuntimeDiscoveryCommandsShareSettingsResourceGateWithoutBlockingTunnelControl()
    {
        using var directory = new OwnedDirectory();
        (new DashboardSettings { AutoStartSharing = false }).Save(directory.Path);
        using var lease = DashboardResourceLease.Acquire(directory.Path, directory.Path);
        var azure = new BlockingAzure();
        await using var runtime = new DashboardRuntime(lease, new()
        {
            ReceiverPortOverride = 0,
            DiscoveryAzure = _ => azure,
            DiscoveryJournal = new EmptyJournal()
        });
        await runtime.InitializeAsync();
        var preview = runtime.PreviewDiscoveryAsync(new(Guid.NewGuid(), Guid.NewGuid(), "test-group", "testaccount"));
        await azure.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var settings = await runtime.UpdateSettingsAsync(runtime.Settings.State.Saved with { Compact = false },
                runtime.HostInstanceId, runtime.Settings.Revision);
            Assert.Equal(1003, settings.Error!.Code);
            Assert.Equal("discovery", runtime.Discovery.Domain);
            Assert.Equal(runtime.HostInstanceId, runtime.Discovery.HostInstanceId);
            Assert.Equal(0, runtime.Discovery.Revision);
        }
        finally { azure.Release.TrySetResult(); }
        var result = await preview;
        Assert.True(result.Succeeded);
        Assert.NotNull(result.Snapshot!.State.Preview);
        Assert.True(result.Snapshot.Revision > 0);
    }

    private static PrivateDiscoveryRegistration Registration()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Guid.NewGuid();
        return new(new(Guid.NewGuid(), Guid.NewGuid(), "test-group", "testaccount"), id, Guid.NewGuid().ToString("N"),
            "agentsignaler-" + id.ToString("N"), "as-" + Guid.NewGuid().ToString("N"),
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()), DiscoveryCodec.ExportPublicKey(key),
            PrivateDiscoveryPhase.Ready, 1, Package: "secret-package-value", RetiredPolicyIds: []);
    }
    private sealed class OwnedDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.GetFullPath(System.IO.Path.Combine("test-artifacts", "discovery-" + Guid.NewGuid().ToString("N")));
        public OwnedDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
    private sealed class EmptyJournal : IPrivateDiscoveryJournal
    {
        public Task<PrivateDiscoveryRegistration?> LoadAsync(CancellationToken token) => Task.FromResult<PrivateDiscoveryRegistration?>(null);
        public Task SaveAsync(PrivateDiscoveryRegistration registration, CancellationToken token) => throw new InvalidOperationException();
    }
    private sealed class BlockingAzure : IPrivateDiscoveryAzure
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PrivateDiscoveryAccountInfo> ValidateAccountAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(new("https://testaccount.blob.core.windows.net/"), "Enabled", "Allow", true, "None");
        }
        public Task<PrivateDiscoveryRemote?> InspectAsync(string container, CancellationToken token) => Task.FromResult<PrivateDiscoveryRemote?>(null);
        public Task CreateAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] document, CancellationToken token) => throw new InvalidOperationException();
        public Task InitializeBlobAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] document, string etag, CancellationToken token) => throw new InvalidOperationException();
        public Task<IPrivateDiscoveryLease> AcquireAsync(string container, CancellationToken token) => throw new InvalidOperationException();
        public Task<Uri> CreateReadSasAsync(string container, string policyId, CancellationToken token) => throw new InvalidOperationException();
        public Task VerifyReadSasAsync(Uri sas, byte[] expected, CancellationToken token) => throw new InvalidOperationException();
        public Task VerifyAnonymousDeniedAsync(string container, CancellationToken token) => throw new InvalidOperationException();
    }
}
