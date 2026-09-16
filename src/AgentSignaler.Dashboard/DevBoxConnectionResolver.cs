using AgentSignaler.Service;
using static AgentSignaler.Dashboard.AzureCliResponse;

namespace AgentSignaler.Dashboard;

internal interface IDevBoxConnectionResolver
{
    Task<ResolvedDevBoxConnection> ResolveAsync(WindowsAppConnection mapping, CancellationToken cancellationToken);
}

internal sealed record ResolvedDevBoxConnection(
    Uri ConnectionUri, string AzureAccountUpn, Guid AzureTenantId, Guid SubscriptionId,
    DateTimeOffset RetrievedAtUtc)
{
    public override string ToString() => "Validated Dev Box connection (identity and URI withheld)";
}

internal sealed class DevBoxConnectionResolver(IAzureCliProcess process, TimeProvider? timeProvider = null) : IDevBoxConnectionResolver
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ResolvedDevBoxConnection> ResolveAsync(WindowsAppConnection mapping, CancellationToken cancellationToken)
    {
        WindowsAppConnectionValidator.ValidateMapping(mapping);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var account = await AzureAccount.GetAsync(process, cancellationToken, mapping).ConfigureAwait(false);
            var remoteCommand = AzureCliCommand.RemoteConnection(mapping);
            var remoteResult = await process.RunAsync(remoteCommand, remoteCommand.Timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckResult(remoteResult, account: false);
            using var document = ParseJson(remoteResult.StandardOutput);
            var uri = WindowsAppConnectionValidator.Validate(
                RequiredString(document.RootElement, "cloudPcConnectionUrl"), mapping.AzureAccountUpn);
            cancellationToken.ThrowIfCancellationRequested();
            return new(uri, account.Upn, account.Tenant, account.Subscription, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            throw new OperationCanceledException("The connection operation was cancelled.", cancellationToken);
        }
    }

}
