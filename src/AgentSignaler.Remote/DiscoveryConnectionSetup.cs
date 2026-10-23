namespace AgentSignaler.Remote;

/// <summary>Read-only connection checks; never changes credentials, installs hooks or starts Client.</summary>
public static class DiscoveryConnectionSetup
{
    public static Task<EffectiveEndpointSnapshot> TestAsync(RemoteConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        TestCoreAsync(configuration, null, null, cancellationToken);

    internal static async Task<EffectiveEndpointSnapshot> TestCoreAsync(RemoteConfiguration configuration,
        IDiscoveryEndpointResolver? resolver, HttpClient? dashboardClient, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(DashboardConnection.TestTimeout);
        cancellationToken = budget.Token;
        cancellationToken.ThrowIfCancellationRequested();
        configuration.Validate();
        EffectiveEndpointSnapshot endpoint;
        if (configuration.IsDiscovery)
        {
            await using var discovery = resolver ?? DnsTxtEndpointResolver.FromConfiguration(configuration);
            var result = await discovery.ResolveAsync(cancellationToken).ConfigureAwait(false);
            endpoint = result.Snapshot ?? throw new InvalidDataException(DnsTxtEndpointResolver.DescribeStatus(result.Status));
        }
        else endpoint = EffectiveEndpointSnapshot.Direct(configuration);
        var ownsClient = dashboardClient is null;
        dashboardClient ??= RemoteHttpTransport.CreateClient(endpoint.BaseUri, interactive: true);
        try
        {
            await DashboardConnection.TestAsync(configuration, endpoint, dashboardClient, cancellationToken)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!endpoint.IsValid(TimeProvider.System))
                throw new InvalidDataException("DNS endpoint TTL expired during the connection check. Test again.");
            return endpoint;
        }
        finally { if (ownsClient) dashboardClient.Dispose(); }
    }
}
