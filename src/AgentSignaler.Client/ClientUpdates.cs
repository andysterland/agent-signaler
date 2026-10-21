using AgentSignaler.Updates;
using AgentSignaler.Remote;
using AgentSignaler.AppUpdates;

namespace AgentSignaler.Client;

internal sealed class ClientUpdates : IUpdateMaintenanceParticipant, IUpdateMaintenanceHealth, IUpdateMaintenanceCommitConsent, IAsyncDisposable
{
    private readonly UpdateMonitor monitor = UpdateMonitor.CreateDefault(UpdatePackage.Remote, AppContext.BaseDirectory);
    private readonly TrayWindow tray;
    private readonly ClientCoordinator runtime;
    private readonly AppUpdateHost updateHost;
    private readonly string configPath;
    private readonly Task recoveryObservation;
    private readonly CancellationTokenSource lifetime = new();
    private int busy;
    private Guid prepared;
    private Timer? timeout;
    private readonly object sync = new();
    private bool disposed;
    private bool ordinaryExit;
    private bool commitAccepted;
    private bool shutdownQueued;
    private bool cleanUpdateShutdown;
    private Task? updateShutdown;
    private bool IsPrepared { get { lock (sync) return prepared != Guid.Empty; } }
    public bool CanRequestOrdinaryExit { get { lock (sync) return !disposed && !ordinaryExit; } }

    public Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(!disposed && !runtime.Completion.IsCompleted && runtime.Status().State is not ("unconfigured" or "stopping"));
    }

    public ClientUpdates(TrayWindow tray, ClientCoordinator runtime, AppUpdateHost updateHost, string configPath)
    {
        this.tray = tray;
        this.runtime = runtime;
        this.updateHost = updateHost;
        this.configPath = configPath;
        tray.UpdateCheckRequested += Check;
        tray.UpdateRequested += Install;
        monitor.Changed += Changed;
        Changed(monitor.State);
        recoveryObservation = ObserveRecoveryAsync();
        monitor.Start();
    }

    private async Task ObserveRecoveryAsync()
    {
        try
        {
            var notices = await updateHost.ReadRecoveryAsync(lifetime.Token);
            if (!disposed && notices.Count != 0)
                tray.SetRecoveryState(new($"{notices.Count} retained unresolved/non-success update result(s). Open Remote setup to review all; no retry has started."));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
            UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (!disposed) tray.SetRecoveryState(new("Recovery information could not be verified (update_recovery_unavailable). Open Remote setup; do not clear servicing state or retry an unknown installer."));
        }
    }

    private void Changed(UpdateCheckResult state)
    {
        if (disposed) return;
        var text = state.Status switch
        {
            UpdateCheckStatus.Checking => "Checking for updates…",
            UpdateCheckStatus.UpToDate => "Up to date",
            UpdateCheckStatus.Available when state.Candidate is { } candidate => $"Version {candidate.Version}{(candidate.Prerelease ? " (preview)" : "")} available",
            UpdateCheckStatus.CheckFailed => "Update check failed; try again later",
            UpdateCheckStatus.CheckIncomplete => "Update check incomplete; try again later",
            _ => "Updates not checked yet"
        };
        var busyNow = Volatile.Read(ref busy) != 0 || IsPrepared || lifetime.IsCancellationRequested;
        var eligible = state.Status == UpdateCheckStatus.Available && updateHost.Installation is not null;
        if (state.Status == UpdateCheckStatus.Available && !eligible)
            text += " — in-app MSI servicing unavailable";
        if (busyNow) text = "Preparing update / checking…";
        if (lifetime.IsCancellationRequested) text = "Stopping Client…";
        lock (sync)
            if (commitAccepted && !ordinaryExit) text = "Update authorized — shutting down for restart";
        text = updateHost.Installation is { } installed
            ? $"Installed MSI {installed.Version.ToString(3)} — {text}"
            : $"Portable/source — {text}";
        tray.SetUpdateState(new(text, eligible,
            busyNow || state.Status == UpdateCheckStatus.Checking));
    }

    private void Check()
    {
        if (disposed || lifetime.IsCancellationRequested || IsPrepared) return;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        Changed(monitor.State);
        _ = Task.Run(async () =>
        {
            var failed = false;
            try { await monitor.CheckAsync(true, lifetime.Token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or UpdateDiscoveryException)
            { failed = true; }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
                Changed(monitor.State);
                if (failed && !disposed)
                    tray.SetUpdateState(new("Update check could not be verified (update_check_failed)", false, IsPrepared));
            }
        });
    }

    private void Install()
    {
        if (disposed || lifetime.IsCancellationRequested || IsPrepared) return;
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        if (monitor.State is not { Status: UpdateCheckStatus.Available, Candidate: { } offered })
        {
            Interlocked.Exchange(ref busy, 0);
            return;
        }
        Changed(monitor.State);
        _ = Task.Run(async () =>
        {
            var restoreStatus = true;
            try
            {
                // Only this explicit tray action may begin disruptive coordination.
                var consent = await UpdateConfirmation.ConfirmAsync(offered.Version, offered.Prerelease, lifetime.Token);
                if (consent == UpdateConsent.Unavailable)
                {
                    restoreStatus = false;
                    tray.SetUpdateState(new("Update confirmation unavailable", true, IsPrepared,
                        "Update confirmation could not be displayed (update_confirmation_unavailable). Client remains running. Repair the Remote installation before retrying."));
                    return;
                }
                if (consent != UpdateConsent.Confirmed) return;
                var message = await updateHost.BeginUpdateAsync(offered, lifetime.Token);
                if (message is not null)
                {
                    restoreStatus = false;
                    tray.SetUpdateState(new("Update did not finish; check updater progress", true, IsPrepared, message));
                }
            }
            catch (OperationCanceledException)
            {
                restoreStatus = false;
                if (!disposed) tray.SetUpdateState(new("Update preparation cancelled or timed out", true, IsPrepared,
                    "The update attempt did not finish. Check any updater progress window before retrying; do not start another installer."));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UpdateDiscoveryException or UnauthorizedAccessException or InvalidOperationException or
                System.Security.SecurityException or System.ComponentModel.Win32Exception)
            {
                restoreStatus = false;
                tray.SetUpdateState(new("Update could not start; Client is still running", true, IsPrepared,
                    "The update could not be prepared (update_preflight_failed). Close conflicting workflows and retry, or repair the installed update components. Client has not closed."));
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
                if (restoreStatus) Changed(monitor.State);
            }
        });
    }

    public bool TryBeginOrdinaryExit()
    {
        lock (sync)
        {
            if (disposed || ordinaryExit) return false;
            ordinaryExit = true;
            prepared = Guid.Empty;
            timeout?.Dispose(); timeout = null;
        }
        lifetime.Cancel();
        Changed(monitor.State);
        return true;
    }

    public bool TryAcceptCommit(Guid runId)
    {
        lock (sync)
        {
            if (disposed || ordinaryExit || lifetime.IsCancellationRequested || prepared != runId ||
                runtime.Completion.IsCompleted || runtime.Status().State is "stopping" or "unconfigured" or "reloading")
                return false;
            commitAccepted = true;
            timeout?.Dispose(); timeout = null;
            Changed(monitor.State);
            return true;
        }
    }

    public async Task<bool> PrepareAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (disposed || ordinaryExit || commitAccepted || lifetime.IsCancellationRequested ||
                cancellationToken.IsCancellationRequested || runtime.Completion.IsCompleted ||
                runtime.Status().State is "stopping" or "unconfigured" or "reloading" || prepared != Guid.Empty && prepared != runId)
                return false;
            prepared = runId;
            Interlocked.Exchange(ref busy, 1);
            timeout?.Dispose();
            timeout = new Timer(_ => CancelPreparation(runId), null, TimeSpan.FromMinutes(2), Timeout.InfiniteTimeSpan);
            Changed(monitor.State);
        }
        try
        {
            await updateHost.RefreshRestartContextAsync(
                new UpdateRestartContext(UpdateApplication.Client, configPath, true), cancellationToken);
            lock (sync)
            {
                if (disposed || prepared != runId || lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested ||
                    runtime.Status().State is "stopping" or "unconfigured" or "reloading")
                { CancelPreparation(runId); return false; }
            }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
            UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        { CancelPreparation(runId); return false; }
    }

    public Task CancelAsync(Guid runId, CancellationToken cancellationToken)
    {
        CancelPreparation(runId, releaseCommitReservation: true);
        return Task.CompletedTask;
    }

    private void CancelPreparation(Guid runId, bool releaseCommitReservation = false)
    {
        lock (sync)
        {
            if (disposed || shutdownQueued || prepared != runId || commitAccepted && !releaseCommitReservation) return;
            prepared = Guid.Empty;
            commitAccepted = false;
            timeout?.Dispose(); timeout = null;
            Interlocked.Exchange(ref busy, 0);
            Changed(monitor.State);
        }
    }

    public Task CommitExitAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (!TryAcceptCommit(runId)) throw new InvalidDataException("update_commit_declined");
            if (shutdownQueued) return Task.CompletedTask;
            shutdownQueued = true;
            timeout?.Dispose(); timeout = null;
            updateShutdown = Task.Run(async () =>
            {
                var response = await runtime.StopAsync();
                lock (sync) cleanUpdateShutdown = response.Accepted;
                tray.Close(response.Error);
            });
        }
        return Task.CompletedTask;
    }

    public async Task<int> GetExitCodeAsync()
    {
        Task? shutdown;
        lock (sync) shutdown = updateShutdown;
        if (shutdown is not null) await Task.WhenAny(shutdown);
        await Task.WhenAny(runtime.Completion);
        if (shutdown?.IsFaulted == true) _ = shutdown.Exception;
        if (runtime.Completion.IsFaulted) _ = runtime.Completion.Exception;
        lock (sync)
            return UpdateHandoffPolicy.GetClientExitCode(ordinaryExit, cleanUpdateShutdown,
                !runtime.Completion.IsCompletedSuccessfully || shutdown is not null && !shutdown.IsCompletedSuccessfully);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            timeout?.Dispose(); timeout = null;
        }
        tray.UpdateCheckRequested -= Check;
        tray.UpdateRequested -= Install;
        monitor.Changed -= Changed;
        lifetime.Cancel();
        await monitor.DisposeAsync();
        await recoveryObservation;
        lifetime.Dispose();
    }
}
