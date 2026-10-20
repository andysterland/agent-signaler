using System.Threading.Channels;
using AgentSignaler.Contracts;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

internal enum RuntimeDiscoveryOperation { Provision, Recover, Renew, Rotate, Revoke, Remove, Refresh, RetirePrevious, TestReader, DisablePublishing, EnablePublishing }

public sealed partial class DashboardRuntime
{
    private readonly RuntimeDomain<PrivateDiscoveryStatus> discovery;
    private PrivateDiscoveryController? discoveryController;
    private Task? discoveryPublisher;
    private Task? discoveryMaintenance;
    private readonly SemaphoreSlim discoveryBackground = new(1, 1);
    private readonly object discoverySignalSync = new();
    private Uri? lastDiscoveryEndpoint;
    private bool discoveryObserved;
    private readonly Channel<long> discoveryUpdates = Channel.CreateBounded<long>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
    internal DomainSnapshot<PrivateDiscoveryStatus> Discovery => discovery.Read();

    private async Task InitializeDiscoveryAsync(CancellationToken token)
    {
        discoveryController = new(options.DiscoveryJournal ?? new PrivateDiscoveryJournal(lease.CanonicalDirectory, () => lease.IsHeld),
            options.DiscoveryAzure ?? (target => new PrivateDiscoveryAzure(target)), options.TimeProvider, VerifyDiscoveryEndpointAsync);
        discoveryController.Changed += () => PublishDiscoveryState();
        try
        {
            await discoveryController.InitializeAsync(token).ConfigureAwait(false);
            if (discoveryController.Registration?.Phase == PrivateDiscoveryPhase.Ready)
            {
                var result = await DiscoveryAsync(RuntimeDiscoveryOperation.Renew, false, token: token).ConfigureAwait(false);
                if (!result.Succeeded && !Discovery.State.Message.Contains("atomic concurrency", StringComparison.Ordinal))
                    discovery.Publish(Discovery.State with { Message = "Discovery renewal failed. Check Azure sign-in, permissions, and ownership before retrying." });
            }
        }
        catch (Exception error) when (IsExpected(error))
        {
            discovery.Publish(new(false, true, false, "Discovery journal could not be loaded. Preserve the journal and repair access; no Azure resources were changed."));
        }
        ObserveDiscoveryTunnel(Sharing.State);
        discoveryPublisher = PublishDiscoveryLoopAsync();
        discoveryMaintenance = MaintainDiscoveryAsync();
    }

    internal Task<RuntimeResult<PrivateDiscoveryStatus>> PreviewDiscoveryAsync(PrivateDiscoveryTarget target,
        CancellationToken token = default) => RunCommandAsync("discovery", ["discovery", "settings"], TimeSpan.FromSeconds(45),
        async (ct, _) =>
        {
            if (discoveryController is null || Receiver.State.Mode != DashboardConnectionMode.DevTunnel)
                throw new RuntimeCommandException(new(1005, "discovery", false));
            await discoveryController.PreviewAsync(target, ct).ConfigureAwait(false);
            return PublishDiscoveryState();
        }, token);

    internal Task<RuntimeResult<PrivateDiscoveryStatus>> DiscoveryAsync(RuntimeDiscoveryOperation operation, bool confirmed = false,
        Guid? confirmationId = null, CancellationToken token = default) => RunCommandAsync("discovery", ["discovery", "settings"],
        TimeSpan.FromSeconds(45), async (ct, commit) =>
        {
            if (discoveryController is null) throw new RuntimeCommandException(new(1005, "discovery", true));
            if (operation is RuntimeDiscoveryOperation.Provision or RuntimeDiscoveryOperation.Rotate or RuntimeDiscoveryOperation.Revoke or RuntimeDiscoveryOperation.Remove or RuntimeDiscoveryOperation.RetirePrevious && !confirmed)
                throw new RuntimeCommandException(new(1009, "confirmed", false));
            // Every cloud operation can commit before cancellation or a network failure becomes observable.
            commit.State = RuntimeCommitState.Unknown;
            switch (operation)
            {
                case RuntimeDiscoveryOperation.Provision:
                    await discoveryController.ProvisionAsync(confirmationId ?? Guid.Empty, ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.Recover:
                    await discoveryController.RecoverAsync(ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.Renew:
                    await discoveryController.RenewAsync(ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.Rotate:
                    await discoveryController.RotateAsync(ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.RetirePrevious:
                    await discoveryController.RetirePreviousAsync(ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.TestReader:
                    await discoveryController.TestReaderAsync(ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.DisablePublishing:
                case RuntimeDiscoveryOperation.EnablePublishing:
                    await discoveryController.SetPublishingAsync(operation == RuntimeDiscoveryOperation.EnablePublishing, ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.Revoke:
                case RuntimeDiscoveryOperation.Remove:
                    await discoveryController.RevokeAsync(operation == RuntimeDiscoveryOperation.Remove, ct).ConfigureAwait(false); break;
                case RuntimeDiscoveryOperation.Refresh:
                    await discoveryController.PublishAsync(discoveryController.ObserveTunnel(VerifiedDiscoveryEndpoint()), ct).ConfigureAwait(false); break;
                default: throw new RuntimeCommandException(new(1001, "discovery", false));
            }
            commit.State = RuntimeCommitState.Committed;
            if (operation is not (RuntimeDiscoveryOperation.Revoke or RuntimeDiscoveryOperation.Remove))
                discoveryUpdates.Writer.TryWrite(discoveryController.ObserveTunnel(VerifiedDiscoveryEndpoint()));
            return PublishDiscoveryState();
        }, token);

    internal string CopyDiscoveryPackage()
    {
        if (!lease.IsHeld || stopping || discoveryController is null) throw new DashboardOwnershipException();
        using var held = gates.Enter(["discovery"]);
        return discoveryController.CopyPackage();
    }

    private Uri? VerifiedDiscoveryEndpoint()
    {
        var value = Sharing.State;
        if (!Receiver.State.Running || Receiver.State.Mode != DashboardConnectionMode.DevTunnel || !value.CanCopy) return null;
        try { return DiscoveryProtocol.ValidateEndpoint(value.PublicUrl!); }
        catch (DiscoveryException) { return null; }
    }

    private async Task VerifyDiscoveryEndpointAsync(Uri endpoint, CancellationToken token)
    {
        if (!lease.IsHeld || stopping || tunnel is null || VerifiedDiscoveryEndpoint() != endpoint)
            throw new DiscoveryException("discovery_generation_changed");
        await tunnel.VerifyPublishingEndpointAsync(endpoint, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!lease.IsHeld || stopping || VerifiedDiscoveryEndpoint() != endpoint)
            throw new DiscoveryException("discovery_generation_changed");
    }

    private void ObserveDiscoveryTunnel(TunnelStatus _, bool force = false)
    {
        if (discoveryController is null || stopping) return;
        var endpoint = VerifiedDiscoveryEndpoint();
        lock (discoverySignalSync)
        {
            if (!force && discoveryObserved && endpoint == lastDiscoveryEndpoint) return;
            lastDiscoveryEndpoint = endpoint;
            discoveryObserved = true;
            if (Discovery.State.Configured && Discovery.State.PublishingEnabled)
                discovery.Publish(Discovery.State with { PublicationPending = true });
            discoveryUpdates.Writer.TryWrite(discoveryController.ObserveTunnel(endpoint));
        }
    }

    private async Task PublishDiscoveryLoopAsync()
    {
        try
        {
            var failures = 0;
            await foreach (var generation in discoveryUpdates.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                if (discoveryController!.Registration?.Phase != PrivateDiscoveryPhase.Ready || !Discovery.State.PublishingEnabled) continue;
                var result = await RunDiscoveryBackgroundAsync(() => RunCommandAsync("discoveryPublisher", ["discovery"], TimeSpan.FromSeconds(45), async (ct, commit) =>
                {
                    commit.State = RuntimeCommitState.Unknown;
                    await discoveryController!.PublishAsync(generation, ct).ConfigureAwait(false);
                    commit.State = RuntimeCommitState.Committed;
                    return PublishDiscoveryState();
                }, lifetime.Token)).ConfigureAwait(false);
                if (result.Succeeded) failures = 0;
                else if (discoveryController!.Registration?.Phase == PrivateDiscoveryPhase.Ready)
                {
                    var retrySeconds = result.Error?.Code == 1003 ? 1 :
                        Math.Clamp(30 * Math.Pow(2, Math.Min(failures++, 4)) * (1 + Random.Shared.NextDouble() * .1), 30, 300);
                    var delay = TimeSpan.FromSeconds(retrySeconds);
                    if (Discovery.State.RetryNotBeforeUtc is { } retryAt && retryAt - options.TimeProvider.GetUtcNow() > delay)
                        delay = retryAt - options.TimeProvider.GetUtcNow();
                    using var retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    var timer = Task.Delay(delay, options.TimeProvider, retryCancellation.Token);
                    var changed = discoveryUpdates.Reader.WaitToReadAsync(retryCancellation.Token).AsTask();
                    var completed = await Task.WhenAny(timer, changed).ConfigureAwait(false);
                    retryCancellation.Cancel();
                    if (completed == timer) ObserveDiscoveryTunnel(Sharing.State, force: true);
                }

            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task<RuntimeResult<PrivateDiscoveryStatus>> RunDiscoveryBackgroundAsync(
        Func<Task<RuntimeResult<PrivateDiscoveryStatus>>> action)
    {
        await discoveryBackground.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        finally { discoveryBackground.Release(); }
    }

    private void InvalidateDiscoveryPublishing()
    {
        discoveryController?.ObserveTunnel(null);
        CancelGroup("discoveryPublisher");
        CancelGroup("discovery");
        lock (discoverySignalSync) discoveryObserved = false;
    }

    private DomainSnapshot<PrivateDiscoveryStatus> PublishDiscoveryState() => discovery.Publish(discoveryController!.Status with
        { LastMaintenanceCheckAtUtc = Discovery.State.LastMaintenanceCheckAtUtc });

    private async Task MaintainDiscoveryAsync()
    {
        var renewed = options.TimeProvider.GetUtcNow();
        var refreshed = renewed;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), options.TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                var now = options.TimeProvider.GetUtcNow();
                if (discoveryController?.Registration?.Phase != PrivateDiscoveryPhase.Ready) continue;
                if (now - renewed >= TimeSpan.FromDays(1))
                {
                    var result = await RunDiscoveryBackgroundAsync(() => DiscoveryAsync(RuntimeDiscoveryOperation.Renew, token: lifetime.Token)).ConfigureAwait(false);
                    if (result.Succeeded) renewed = now;
                }
                if (now - refreshed >= TimeSpan.FromHours(6) && Sharing.State.CanCopy && Discovery.State.PublishingEnabled)
                {
                    ObserveDiscoveryTunnel(Sharing.State, force: true);
                    refreshed = now;
                }
                discovery.Publish(Discovery.State with { LastMaintenanceCheckAtUtc = now });
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task StopDiscoveryAsync()
    {
        if (discoveryController is null) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3), options.TimeProvider);
        Task? pending = null;
        try
        {
            var generation = discoveryController.ObserveTunnel(null);
            pending = Task.WhenAll(new[] { discoveryPublisher, discoveryMaintenance }.OfType<Task>());
            await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            pending = discoveryController.PublishAsync(generation, deadline.Token);
            await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsExpected(error) || error is OperationCanceledException)
        {
            discovery.Publish(Discovery.State with { Message = "Shutdown unavailable publication was not confirmed. The last signed document expires within 24 hours." }, stale: true);
            if (pending is not null) _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}