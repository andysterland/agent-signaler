namespace AgentSignaler.Dashboard;

internal sealed record DevBoxCatalogState(
    DevBoxCatalogSnapshot? Snapshot, bool IsBusy, string Status, string Message)
{
    public DevBoxCatalogProgress? Progress { get; init; }
    public Guid? TargetSubscription { get; init; }
    public string? TargetDevCenterName { get; init; }

    public override string ToString() => $"{Status}: {Message}";
}

internal sealed record DevBoxCatalogRefreshResult(bool Succeeded, string Message);

internal sealed class DevBoxCatalogController(IDevBoxCatalogService service)
{
    private readonly object sync = new();
    private DevBoxCatalogState state = new(null, false, "Not refreshed",
        "Select Refresh Dev Boxes to discover Dev Centers and assigned Dev Boxes using Azure CLI.");
    private Pending? pending;
    private bool stopping;
    private sealed record Pending(CancellationTokenSource Cancellation, TaskCompletionSource Completion);

    public event Action? Changed;
    public DevBoxCatalogState State { get { lock (sync) return state; } }

    public Task<DevBoxCatalogRefreshResult> RefreshAsync(CancellationToken cancellationToken = default, Guid? subscriptionId = null,
        string? devCenterName = null)
    {
        DevBoxDiscoveryTarget.Validate(subscriptionId, devCenterName);
        Pending work;
        lock (sync)
        {
            if (stopping)
                return Task.FromResult(new DevBoxCatalogRefreshResult(false, "Dashboard is exiting. Catalog refresh was cancelled."));
            if (pending is not null)
                return Task.FromResult(new DevBoxCatalogRefreshResult(false, "A Dev Box catalog refresh is already running."));
            work = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
                new(TaskCreationOptions.RunContinuationsAsynchronously));
            pending = work;
            state = state with
            {
                IsBusy = true, Status = "Refreshing",
                Message = "Discovering Dev Centers and assigned Dev Boxes using Azure CLI...",
                Progress = new(DevBoxCatalogStage.Account),
                TargetSubscription = subscriptionId,
                TargetDevCenterName = devCenterName
            };
        }
        Changed?.Invoke();
        return RunAsync(work, subscriptionId, devCenterName);
    }

    private async Task<DevBoxCatalogRefreshResult> RunAsync(Pending work, Guid? subscriptionId, string? devCenterName)
    {
        var token = work.Cancellation.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var snapshot = await Task.Run(() => service.RefreshAsync(token, progress =>
            {
                lock (sync)
                {
                    if (pending != work || token.IsCancellationRequested) return;
                    state = state with { Progress = progress };
                }
                Changed?.Invoke();
            }, subscriptionId, devCenterName), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string message;
            lock (sync)
            {
                // An empty result must not discard a previously usable catalog.
                var retain = snapshot.Items.Count == 0 && state.Snapshot?.Items.Count > 0;
                var emptyMessage = devCenterName is not null
                    ? "No assigned Dev Boxes were returned from the requested Dev Center."
                    : snapshot.DevCenterEndpoints.Count == 0
                    ? "No accessible Dev Centers were found. Check the Azure CLI tenant and Dev Center read permissions."
                    : "No assigned Dev Boxes were returned from the discovered Dev Centers.";
                message = snapshot.Items.Count == 0
                    ? emptyMessage + (retain ? " The previous catalog is retained; existing mappings are unchanged." : "")
                    : $"Refresh succeeded. {snapshot.Items.Count} Dev Boxes available across {snapshot.DevCenterEndpoints.Count} Dev Centers.";
                var partial = snapshot.SubscriptionFailures.Count != 0;
                if (partial)
                    message = $"Subscription searches failed: {snapshot.SubscriptionFailures.Count}; results may be incomplete. " + message;
                state = state with { Snapshot = retain ? state.Snapshot : snapshot, Status = partial ? "Partial results" : "Ready", Message = message };
            }
            return new(true, message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            const string message = "Dev Box catalog refresh cancelled. The previous catalog and mappings are unchanged.";
            SetOutcome("Cancelled", message);
            return new(false, message);
        }
        catch (DevBoxCatalogException error)
        {
            SetOutcome(error.Failure == WindowsAppFailure.SignInRequired ? "Sign-in required" : "Unavailable", error.Message);
            return new(false, error.Message);
        }
        catch (WindowsAppConnectionException error)
        {
            SetOutcome(error.Status, error.Message);
            return new(false, error.Message);
        }
        finally
        {
            lock (sync)
            {
                state = state with { IsBusy = false };
                pending = null;
                work.Cancellation.Dispose();
            }
            work.Completion.TrySetResult();
            Changed?.Invoke();
        }
    }

    private void SetOutcome(string status, string message)
    {
        lock (sync) state = state with { Status = status, Message = message };
    }

    public void Cancel()
    {
        lock (sync) pending?.Cancellation.Cancel();
    }

    public Task CancelAndWaitAsync()
    {
        lock (sync)
        {
            if (pending is not { } work) return Task.CompletedTask;
            work.Cancellation.Cancel();
            return work.Completion.Task;
        }
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            return CancelAndWaitAsync();
        }
    }
}
