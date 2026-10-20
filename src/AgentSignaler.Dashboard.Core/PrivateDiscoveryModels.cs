using System.Text.RegularExpressions;
using AgentSignaler.Contracts;

namespace AgentSignaler.Dashboard;

internal sealed partial record PrivateDiscoveryTarget(Guid TenantId, Guid SubscriptionId, string ResourceGroup, string Account)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || SubscriptionId == Guid.Empty || !AccountPattern().IsMatch(Account ?? "") ||
            string.IsNullOrWhiteSpace(ResourceGroup) || ResourceGroup.Length > 90 ||
            ResourceGroup.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '(' or ')')) || ResourceGroup.EndsWith('.'))
            throw new DiscoveryException("discovery_target");
    }
    public Uri Endpoint => new($"https://{Account}.blob.core.windows.net");
    public string ResourceId => $"/subscriptions/{SubscriptionId:D}/resourceGroups/{Uri.EscapeDataString(ResourceGroup)}/providers/Microsoft.Storage/storageAccounts/{Account}";
    [GeneratedRegex("\\A[a-z0-9]{3,24}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AccountPattern();
}

internal sealed record PrivateDiscoveryPolicy(string Id, DateTimeOffset StartsAtUtc, DateTimeOffset ExpiresAtUtc, string Permissions);
internal sealed record PrivateDiscoveryAccountInfo(Uri Endpoint, string PublicNetworkAccess, string DefaultNetworkAction,
    bool SharedKeyAllowed, string SasExpirationAction, TimeSpan? SasMaximumInterval = null,
    bool AtomicPolicyUpdatesSupported = true);
internal sealed record PrivateDiscoveryRemote(string ContainerETag, string? BlobETag,
    IReadOnlyDictionary<string, string> Metadata, bool IsPrivate, IReadOnlyList<PrivateDiscoveryPolicy> Policies,
    IReadOnlyList<string> Blobs, IReadOnlyDictionary<string, string>? BlobMetadata = null, byte[]? Document = null);
internal interface IPrivateDiscoveryLease : IAsyncDisposable
{
    Task<PrivateDiscoveryRemote> ReadAsync(CancellationToken token);
    Task WritePoliciesAsync(IReadOnlyList<PrivateDiscoveryPolicy> policies, string etag, CancellationToken token);
    Task WriteBlobAsync(byte[] bytes, string etag, CancellationToken token);
    Task DeleteBlobAsync(string etag, CancellationToken token);
}
internal interface IPrivateDiscoveryAzure
{
    Task<PrivateDiscoveryAccountInfo> ValidateAccountAsync(CancellationToken token);
    Task<PrivateDiscoveryRemote?> InspectAsync(string container, CancellationToken token);
    Task CreateAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] initialDocument, CancellationToken token);
    Task InitializeBlobAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] initialDocument, string containerETag, CancellationToken token);
    Task<IPrivateDiscoveryLease> AcquireAsync(string container, CancellationToken token);
    Task<Uri> CreateReadSasAsync(string container, string policyId, CancellationToken token);
    Task VerifyReadSasAsync(Uri sas, byte[] expectedDocument, CancellationToken token);
    Task VerifyAnonymousDeniedAsync(string container, CancellationToken token);
}
internal enum PrivateDiscoveryPhase { Prepared, ContainerCreated, PolicyPending, Ready, RevocationPending, Revoked, RemovalPending, Removed, RetirementPending }
internal sealed record PrivateDiscoveryRegistration(PrivateDiscoveryTarget Target, Guid DiscoveryId, string Owner,
    string Container, string PolicyId, string SigningKey, string PublicKey, PrivateDiscoveryPhase Phase,
    long Revision, DateTimeOffset? PolicyExpiry = null, string? Package = null,
    IReadOnlyList<string>? RetiredPolicyIds = null, string? PendingPolicyId = null,
    string? PreviousPolicyId = null, DateTimeOffset? PreviousPolicyExpiry = null, bool PublishingEnabled = true, byte[]? PendingDocument = null,
    DateTimeOffset? LastPublishedAtUtc = null, string? PendingBlobETag = null)
{
    public override string ToString() => "Private discovery registration (protected)";
}
internal sealed record PrivateDiscoveryPreview(PrivateDiscoveryTarget Target, Guid DiscoveryId, string Owner, string Container,
    string PolicyId, Guid ConfirmationId, DateTimeOffset ExpiresAtUtc, PrivateDiscoveryAccountInfo Account);
internal sealed record PrivateDiscoveryStatus(bool Configured, bool Recoverable, bool Revoked, string Message,
    DateTimeOffset? PolicyExpiresAtUtc = null, DateTimeOffset? PublishedAtUtc = null, long Revision = 0,
    PrivateDiscoveryPreview? Preview = null, bool PublishingEnabled = true, string? ReaderGroupId = null, string? PreviousReaderGroupId = null,
    bool PublicationPending = false, DateTimeOffset? RetryNotBeforeUtc = null, DateTimeOffset? LastMaintenanceCheckAtUtc = null);
internal sealed class PrivateDiscoveryCloudException(DateTimeOffset? retryNotBeforeUtc) : Exception("discovery_cloud_request_failed")
{
    public DateTimeOffset? RetryNotBeforeUtc { get; } = retryNotBeforeUtc;
}
internal interface IPrivateDiscoveryJournal
{
    Task<PrivateDiscoveryRegistration?> LoadAsync(CancellationToken token);
    Task SaveAsync(PrivateDiscoveryRegistration registration, CancellationToken token);
}