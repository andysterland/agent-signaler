using AgentSignaler.Contracts;
using AgentSignaler.Discovery;

namespace AgentSignaler.Remote;

/// <summary>Explicit read-only setup checks; never persists credentials or replay state, installs hooks, or starts Client.</summary>
public static class DiscoveryConnectionSetup
{
    public static async Task<EffectiveEndpointSnapshot> TestAsync(RemoteConfiguration configuration, string configPath,
        DiscoveryConnectionPackage? importedPackage = null, HttpClient? discoveryClient = null,
        HttpClient? dashboardClient = null, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DashboardConnection.TestTimeout);
        cancellationToken = budget.Token;
        cancellationToken.ThrowIfCancellationRequested();
        configuration.Validate();
        EffectiveEndpointSnapshot endpoint;
        if (configuration.IsDiscovery)
        {
            try
            {
                var package = importedPackage ?? await LoadPackageAsync(configuration, configPath, cancellationToken).ConfigureAwait(false);
                DiscoveryProtocol.ValidatePackage(package);
                if (package.DiscoveryId != configuration.DiscoveryId || package.SigningPublicKey != configuration.DiscoverySigningPublicKey)
                    throw new InvalidDataException("The protected package does not match the pinned discovery identity and verification key. Import the intended package again.");
                var revisions = ReadCheckpoint(configPath, package.DiscoveryId);
                await using var resolver = new DiscoveryEndpointResolver(package, discoveryClient, revisions: revisions);
                var result = await resolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
                endpoint = result.Snapshot ?? throw new InvalidDataException(DiscoveryEndpointResolver.DescribeStatus(result.Status));
            }
            catch (Exception ex) when (ex is DiscoveryException or HttpRequestException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or System.Text.DecoderFallbackException)
            {
                throw new InvalidDataException("Private discovery reader or protected credential verification failed. Check reader access and explicitly import the intended package again. No fallback URL was used.");
            }
        }
        else
        {
            if (importedPackage is not null) throw new InvalidDataException("An imported private package cannot be tested as a direct URL.");
            endpoint = EffectiveEndpointSnapshot.Direct(configuration);
        }
        var ownsClient = dashboardClient is null;
        dashboardClient ??= RemoteHttpTransport.CreateClient(endpoint.BaseUri, interactive: true);
        try
        {
            await DashboardConnection.TestAsync(configuration, endpoint, dashboardClient, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return endpoint;
        }
        finally { if (ownsClient) dashboardClient.Dispose(); }
    }

    private static Task<DiscoveryConnectionPackage> LoadPackageAsync(RemoteConfiguration configuration, string configPath,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new CurrentUserDiscoveryCredentialStore(DiscoveryCredentialTransactionStore.DirectoryPath(configPath))
            .LoadAsync(configuration.Discovery!, token);
    }

    private static IDiscoveryRevisionStore ReadCheckpoint(string configPath, Guid discoveryId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var checkpoint = new CurrentUserDiscoveryReplayStore(Path.Combine(Path.GetDirectoryName(configPath)!, "discovery-replay"))
            .LoadReadOnly(discoveryId);
        return new SetupRevisionStore(discoveryId, checkpoint);
    }

    private sealed class SetupRevisionStore(Guid discoveryId, DiscoveryReplayState? checkpoint) : IDiscoveryRevisionStore
    {
        private DiscoveryRevisionCheckpoint? state = checkpoint is null ? null : new(checkpoint.Revision, checkpoint.PayloadFingerprint);
        public DiscoveryRevisionCheckpoint? Read(Guid identity) => identity == discoveryId ? state :
            throw new DiscoveryException("discovery_identity_mismatch");

        public void Write(Guid identity, DiscoveryRevisionCheckpoint value)
        {
            if (identity != discoveryId) throw new DiscoveryException("discovery_identity_mismatch");
            state = value;
        }
    }
}
