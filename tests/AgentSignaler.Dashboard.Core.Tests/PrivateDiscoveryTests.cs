using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class PrivateDiscoveryTests
{
    private static readonly PrivateDiscoveryTarget Target = new(Guid.NewGuid(), Guid.NewGuid(), "test-group", "testaccount");

    [Fact]
    public async Task PreviewIsReadOnlyAndRequiresExactUnexpiredConfirmation()
    {
        var fixture = new Fixture();
        var preview = await fixture.Controller.PreviewAsync(Target, default);
        Assert.Equal(0, fixture.Azure.Mutations);
        Assert.Null(fixture.Journal.Value);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.ProvisionAsync(Guid.NewGuid(), default));
        Assert.Equal(0, fixture.Azure.Mutations);
        fixture.Clock.Now = preview.ExpiresAtUtc;
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.ProvisionAsync(preview.ConfirmationId, default));
        Assert.Null(fixture.Journal.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAtomicPolicyCapabilityBlocksBeforeCloudOrJournalCreation(bool capabilityLostAfterPreview)
    {
        var fixture = new Fixture();
        PrivateDiscoveryPreview? preview = null;
        if (capabilityLostAfterPreview) preview = await fixture.Controller.PreviewAsync(Target, default);
        fixture.Azure.AtomicPolicyUpdatesSupported = false;
        var error = await Assert.ThrowsAsync<DiscoveryException>(async () =>
        {
            if (preview is null) await fixture.Controller.PreviewAsync(Target, default);
            else await fixture.Controller.ProvisionAsync(preview.ConfirmationId, default);
        });
        Assert.Equal("discovery_acl_atomic_concurrency_unsupported", error.Code);
        Assert.Equal(0, fixture.Azure.Mutations);
        Assert.Null(fixture.Azure.Remote);
        Assert.Null(fixture.Journal.Value);
        Assert.Contains("atomic concurrency", fixture.Controller.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationBeforeMutationDoesNotCreateResources()
    {
        var fixture = new Fixture();
        var preview = await fixture.Controller.PreviewAsync(Target, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Controller.ProvisionAsync(preview.ConfirmationId, cancellation.Token));
        Assert.Equal(0, fixture.Azure.Mutations);
        Assert.Null(fixture.Journal.Value);
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public Journal Journal { get; } = new();
        public FakeAzure Azure { get; } = new();
        public PrivateDiscoveryController Controller { get; }
        public Fixture() => Controller = new(Journal, _ => Azure, Clock);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Journal : IPrivateDiscoveryJournal
    {
        public PrivateDiscoveryRegistration? Value { get; private set; }
        public Task<PrivateDiscoveryRegistration?> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(PrivateDiscoveryRegistration registration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Value = registration;
            return Task.CompletedTask;
        }
    }
    private sealed class FakeAzure : IPrivateDiscoveryAzure
    {
        public PrivateDiscoveryRemote? Remote => null;
        public bool AtomicPolicyUpdatesSupported { get; set; } = true;
        public int Mutations { get; private set; }
        public Task<PrivateDiscoveryAccountInfo> ValidateAccountAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new PrivateDiscoveryAccountInfo(Target.Endpoint, "Enabled", "Allow", true, "None",
                AtomicPolicyUpdatesSupported: AtomicPolicyUpdatesSupported));
        }
        public Task VerifyAnonymousDeniedAsync(string container, CancellationToken token) => Task.CompletedTask;
        public Task<PrivateDiscoveryRemote?> InspectAsync(string container, CancellationToken token) => Task.FromResult(Remote);
        public Task CreateAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] document, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Mutations++;
            throw new InvalidOperationException("Unexpected cloud mutation.");
        }
        public Task InitializeBlobAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] document, string containerETag, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected cloud mutation.");
        public Task<IPrivateDiscoveryLease> AcquireAsync(string container, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected cloud mutation.");
        public Task<Uri> CreateReadSasAsync(string container, string policyId, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected credential creation.");
        public Task VerifyReadSasAsync(Uri sas, byte[] expectedDocument, CancellationToken token) =>
            throw new InvalidOperationException("Unexpected credential verification.");
    }
}
