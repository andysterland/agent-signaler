using AgentSignaler.Service;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

public sealed partial class DashboardRuntime
{
    private readonly AsyncLocal<CommitTracker?> currentMappingCommit = new();
    private readonly object prerequisiteSync = new();
    private readonly object detailedReceptionSync = new();
    private long detailedReceptionOptOut;
    private long detailedReceptionEnabled;

    internal Task<RuntimeResult<RuntimeSettings>> UpdateSettingsAsync(DashboardSettings next, Guid expectedHostInstanceId,
        long expectedRevision, CancellationToken cancellationToken = default, bool explicitDetailedReceptionEnable = false) =>
        RunCommandAsync("settings", ["settings"], TimeSpan.FromSeconds(10), async (token, commit) =>
        {
            RequireInstance(expectedHostInstanceId);
            if (Settings.Revision != expectedRevision) throw new RuntimeCommandException(new(1004, "expectedRevision", true));
            next = next with { ExtensionData = Settings.State.Saved.ExtensionData };
            try { DashboardSettings.Validate(next); }
            catch (InvalidDataException) { throw new RuntimeCommandException(new(1001, "settings", false)); }
            token.ThrowIfCancellationRequested();
            var enableVersion = next.ReceiveDetailedConversations &&
                (explicitDetailedReceptionEnable || !Settings.State.Saved.ReceiveDetailedConversations)
                ? Interlocked.Read(ref detailedReceptionOptOut) : (long?)null;
            if (!next.ReceiveDetailedConversations) DisableDetailedReception();
            await SaveSettingsFileAsync(next, commit, token).ConfigureAwait(false);
            commit.State = RuntimeCommitState.Committed;
            return PublishSettings(next, enableVersion);
        }, cancellationToken);

    internal void DisableDetailedReception()
    {
        lock (detailedReceptionSync)
        {
            Interlocked.Increment(ref detailedReceptionOptOut);
            server?.Transcripts.SetEnabled(false);
            var current = Settings.State;
            settings.Publish(current with { Effective = current.Effective with { ReceiveDetailedConversations = false } });
            receiver.Publish(Receiver.State with { ReceiveDetailedConversations = false });
        }
    }

    private Task SaveSettingsFileAsync(DashboardSettings next, CommitTracker commit, CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            MergeCommit(commit, RuntimeCommitState.Unknown);
            next.Save(lease.CanonicalDirectory);
            commit.State = RuntimeCommitState.Committed;
        }, token);

    private DomainSnapshot<RuntimeSettings> PublishSettings(DashboardSettings next, long? enableVersion = null)
    {
        lock (detailedReceptionSync)
        {
            if (enableVersion == detailedReceptionOptOut) detailedReceptionEnabled = detailedReceptionOptOut;
            var current = Settings.State;
            var effective = current.Effective with
            {
                AutoStartSharing = next.AutoStartSharing,
                DevBoxSubscriptionId = next.DevBoxSubscriptionId,
                DevCenterName = next.DevCenterName,
                ReceiveDetailedConversations = next.ReceiveDetailedConversations && detailedReceptionOptOut == detailedReceptionEnabled,
                Theme = next.Theme, Compact = next.Compact,
                ShowCompactViewWhenMinimized = next.ShowCompactViewWhenMinimized
            };
            var restart = new List<string>();
            if (next.Port != effective.Port) restart.Add("port");
            if (next.ConnectionMode != effective.ConnectionMode) restart.Add("connectionMode");
            if ((string.IsNullOrWhiteSpace(next.DevTunnelCliPath) ? CliTunnelController.DefaultCliPath : next.DevTunnelCliPath) != effective.DevTunnelCliPath)
                restart.Add("devTunnelCliPath");
            if (AzureCliInstallation.ResolvePath(next.AzureCliPath) != effective.AzureCliPath) restart.Add("azureCliPath");
            if (next.RpcPort != effective.RpcPort) restart.Add("rpcPort");
            settings.Publish(new(next, effective, current.RpcPortOverridden, restart.AsReadOnly(), false));
            server?.Transcripts.SetEnabled(Settings.State.Effective.ReceiveDetailedConversations);
            receiver.Publish(Receiver.State with { ReceiveDetailedConversations = Settings.State.Effective.ReceiveDetailedConversations });
            UpdateTranscriptReadiness();
            return Settings;
        }
    }

    internal Task<RuntimeResult<DevBoxCatalogState>> RefreshCatalogAsync(Guid? subscriptionId, string? devCenterName,
        Guid expectedHostInstanceId, long expectedSettingsRevision, CancellationToken cancellationToken = default) =>
        RunCommandAsync("devboxes", ["azure", "settings"], TimeSpan.FromMinutes(5), async (token, commit) =>
        {
            RequireInstance(expectedHostInstanceId);
            if (Settings.Revision != expectedSettingsRevision) throw new RuntimeCommandException(new(1004, "expectedRevision", true));
            if (catalogController is null) throw new RuntimeCommandException(new(1005, "catalog", true));
            DevBoxDiscoveryTarget.Validate(subscriptionId, devCenterName);
            var next = Settings.State.Saved with { DevBoxSubscriptionId = subscriptionId, DevCenterName = devCenterName };
            await SaveSettingsFileAsync(next, commit, token).ConfigureAwait(false);
            commit.State = RuntimeCommitState.Committed;
            PublishSettings(next);
            var result = await catalogController.RefreshAsync(token, subscriptionId, devCenterName).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                token.ThrowIfCancellationRequested();
                throw new RuntimeCommandException(new(1010, "catalog", true));
            }
            return Catalog;
        }, cancellationToken);

    internal void CancelCatalog() { CancelGroup("devboxes"); catalogController?.Cancel(); }

    internal Task<RuntimeResult<TunnelStatus>> SharingAsync(RuntimeSharingOperation operation, bool confirmed = false,
        CancellationToken cancellationToken = default)
    {
        if (operation == RuntimeSharingOperation.Stop) return StopSharingAsync(cancellationToken);
        return ExecuteSharingAsync(operation, confirmed, cancellationToken);
    }

    private async Task<RuntimeResult<TunnelStatus>> StopSharingAsync(CancellationToken cancellationToken)
    {
        CancelSharing();
        CancelPrerequisites(RuntimePrerequisiteKind.DevTunnel);
        try
        {
            await Task.WhenAll(DrainGroupAsync("sharing"), DrainGroupAsync($"prerequisite:{RuntimePrerequisiteKind.DevTunnel}"))
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return new(null, new(1006, "sharing", true), RuntimeCommitState.NotCommitted); }
        catch (TimeoutException) { return new(null, new(1003, "sharing", true, RuntimeCommitState.Unknown), RuntimeCommitState.Unknown); }
        return await RunCommandAsync("sharing", ["tunnel"], TimeSpan.FromMinutes(3), async (token, commit) =>
        {
            if (tunnel is null) throw new RuntimeCommandException(new(1005, "sharing", true));
            server?.SetTranscriptReadiness(false);
            var outcome = await tunnel.StopWithResultAsync(token).ConfigureAwait(false);
            ApplyTunnelOutcome(outcome, commit, token);
            sharing.Publish(outcome.Status);
            try
            {
                using var settingsLease = gates.Enter(["settings"]);
                var next = Settings.State.Saved with { AutoStartSharing = false };
                await SaveSettingsFileAsync(next, commit, token).ConfigureAwait(false);
                PublishSettings(next);
            }
            catch (RuntimeCommandException error) when (error.Error.Code == 1003)
            {
                throw new RuntimeCommandException(new(1003, "settings", true));
            }
            return Sharing;
        }, cancellationToken, control: true).ConfigureAwait(false);
    }

    private Task<RuntimeResult<TunnelStatus>> ExecuteSharingAsync(RuntimeSharingOperation operation, bool confirmed,
        CancellationToken cancellationToken) => RunCommandAsync("sharing", ["settings", "tunnel"], TimeSpan.FromMinutes(3),
        async (token, commit) =>
        {
            if (!Enum.IsDefined(operation)) throw new RuntimeCommandException(new(1001, "operation", false));
            if (operation is RuntimeSharingOperation.Delete or RuntimeSharingOperation.Logout && !confirmed)
                throw new RuntimeCommandException(new(1009, "confirmed", false));
            if (tunnel is null || !Receiver.State.Running) throw new RuntimeCommandException(new(1005, "sharing", true));
            var enabled = operation == RuntimeSharingOperation.Start;
            var next = Settings.State.Saved with { AutoStartSharing = enabled };
            Exception? saveFailure = null;
            try
            {
                await SaveSettingsFileAsync(next, commit, token).ConfigureAwait(false);
                commit.State = RuntimeCommitState.Committed;
                PublishSettings(next);
            }
            catch (Exception error) when (IsPersistenceFailure(error) && !enabled) { saveFailure = error; }
            if (!enabled) server?.SetTranscriptReadiness(false);
            var outcome = operation switch
            {
                RuntimeSharingOperation.Start => await tunnel.StartWithResultAsync(token).ConfigureAwait(false),
                RuntimeSharingOperation.Delete => await tunnel.DeleteWithResultAsync(token).ConfigureAwait(false),
                RuntimeSharingOperation.Logout => await tunnel.LogoutWithResultAsync(token).ConfigureAwait(false),
                _ => throw new RuntimeCommandException(new(1001, "operation", false))
            };
            ApplyTunnelOutcome(outcome, commit, token);
            if (saveFailure is not null) throw new RuntimeCommandException(new(1008, "settings", true));
            return sharing.Publish(outcome.Status);
        }, cancellationToken, control: operation == RuntimeSharingOperation.Stop);

    private static void MergeCommit(CommitTracker commit, RuntimeCommitState state)
    {
        if (commit.State != RuntimeCommitState.Committed && state != RuntimeCommitState.NotCommitted)
            commit.State = state;
    }

    private static void ApplyTunnelOutcome(TunnelOperationResult outcome, CommitTracker commit, CancellationToken token)
    {
        MergeCommit(commit, outcome.CommitState switch
        {
            TunnelCommitState.Committed => RuntimeCommitState.Committed,
            TunnelCommitState.Unknown => RuntimeCommitState.Unknown,
            _ => RuntimeCommitState.NotCommitted
        });
        token.ThrowIfCancellationRequested();
        if (outcome.Outcome != TunnelOperationOutcome.Succeeded)
            throw new RuntimeCommandException(new(outcome.Outcome switch
            {
                TunnelOperationOutcome.Cancelled => 1006,
                TunnelOperationOutcome.TimedOut => 1007,
                _ => outcome.Failure == TunnelOperationFailure.Persistence ? 1008 : 1010
            }, "sharing", true));
    }

    internal void CancelSharing() => CancelGroup("sharing");

    internal DomainSnapshot<WindowsAppConnectionState> GetWindowsAppState(Guid id)
    {
        _ = GetMachine(id);
        lock (windowsSync)
        {
            if (!windowsDomains.TryGetValue(id, out var domain))
            {
                domain = new(HostInstanceId, "windowsApp", connections?.State(id) ?? new(null, "Unavailable", ""),
                    change => Publish(change with { MachineId = id }), NextWindowsRevision(), NextWindowsRevision);
                windowsDomains.Add(id, domain);
            }
            return domain.Read();
        }
    }

    private void UpdateWindowsState(Guid id)
    {
        if (connections is null) return;
        RuntimeDomain<WindowsAppConnectionState> domain;
        lock (windowsSync)
        {
            if (!windowsDomains.TryGetValue(id, out domain!))
            {
                if (windowsDomains.Count >= 25) return;
                domain = new(HostInstanceId, "windowsApp", connections.State(id), change => Publish(change with { MachineId = id }),
                    NextWindowsRevision(), NextWindowsRevision);
                windowsDomains.Add(id, domain);
            }
        }
        domain.Publish(connections.State(id));
    }

    private long NextWindowsRevision() => Interlocked.Increment(ref windowsRevision);

    private async Task PersistMappingAsync(Guid id, WindowsAppConnection mapping, CancellationToken token)
    {
        TrackSideEffect(RuntimeCommitState.Unknown);
        await store!.SetWindowsAppConnectionAsync(id, mapping, token).ConfigureAwait(false);
        if (currentMappingCommit.Value is { } commit) commit.State = RuntimeCommitState.Committed;
    }
    private async Task ClearMappingAsync(Guid id, CancellationToken token)
    {
        TrackSideEffect(RuntimeCommitState.Unknown);
        await store!.ClearWindowsAppConnectionAsync(id, token).ConfigureAwait(false);
        if (currentMappingCommit.Value is { } commit) commit.State = RuntimeCommitState.Committed;
    }

    internal Task<RuntimeResult<WindowsAppConnectionState>> WindowsAppAsync(Guid id, WindowsAppOperation operation,
        DevBoxMappingSelection? selection, bool confirmed, Guid expectedHostInstanceId, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        bool localOpen;
        lock (machineSync)
            localOpen = operation == WindowsAppOperation.Open &&
                machines.State.Any(item => item.Machine.MachineId == id && MachineNavigation.IsLocal(item.Machine));
        string[] resources = localOpen || operation is WindowsAppOperation.Clear or WindowsAppOperation.OpenLastKnown
            ? [$"machine:{id:D}"] : ["azure", $"machine:{id:D}"];
        return RunCommandAsync($"windowsApp:{id:D}", resources, TimeSpan.FromMinutes(3), async (token, commit) =>
        {
            RequireMachine(id, expectedHostInstanceId, expectedRevision);
            if (connections is null) throw new RuntimeCommandException(new(1005, "windowsApp", true));
            if (operation == WindowsAppOperation.Clear && !confirmed)
                throw new RuntimeCommandException(new(1009, "confirmed", false));
            currentMappingCommit.Value = commit;
            try
            {
                var result = await connections.ExecuteAsync(id, operation, selection, cancellationToken: token).ConfigureAwait(false);
                if (result.Succeeded && operation is WindowsAppOperation.Open or WindowsAppOperation.OpenLastKnown or WindowsAppOperation.SignIn)
                    commit.State = RuntimeCommitState.Committed;
                await RefreshMachinesAsync(token).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    token.ThrowIfCancellationRequested();
                    throw new RuntimeCommandException(new(result.Failure == WindowsAppFailure.TimedOut ? 1007 :
                        result.Failure == WindowsAppFailure.PersistenceFailed ? 1008 :
                        result.Failure == WindowsAppFailure.Busy ? 1003 : 1010, "windowsApp", true));
                }
                UpdateWindowsState(id);
                return GetWindowsAppState(id);
            }
            finally { currentMappingCommit.Value = null; }
        }, cancellationToken);
    }

    internal void CancelWindowsApp(Guid id)
    {
        CancelGroup($"windowsApp:{id:D}");
        connections?.Cancel(id);
    }

    private void TrackSideEffect(RuntimeCommitState state)
    {
        if (currentMappingCommit.Value is { } commit && commit.State != RuntimeCommitState.Committed)
            commit.State = state;
    }

    private sealed class TrackingWindowsAppPlatform(IWindowsAppPlatform inner, Action<RuntimeCommitState> track) : IWindowsAppPlatform
    {
        public void MinimizeSessions()
        {
            track(RuntimeCommitState.Unknown);
            inner.MinimizeSessions();
            track(RuntimeCommitState.Committed);
        }
        public bool IsProtocolAvailable() => inner.IsProtocolAvailable();
        public WindowsAppActivationDisposition TryActivateExisting(string name)
        {
            try
            {
                var result = inner.TryActivateExisting(name);
                if (result == WindowsAppActivationDisposition.ExistingWindowActivated) track(RuntimeCommitState.Committed);
                return result;
            }
            catch (WindowsAppConnectionException) { track(RuntimeCommitState.Unknown); throw; }
        }
        public WindowsAppActivationDisposition Activate(Uri uri, string name)
        {
            track(RuntimeCommitState.Unknown);
            var result = inner.Activate(uri, name);
            track(RuntimeCommitState.Committed);
            return result;
        }
    }

    internal async Task<RuntimeResult<IReadOnlyList<RuntimePrerequisite>>> CheckPrerequisiteAsync(RuntimePrerequisiteKind kind,
        string? testedPath = null, CancellationToken cancellationToken = default)
    {
        var group = $"prerequisite:{kind}";
        string[] resources = kind switch
        {
            RuntimePrerequisiteKind.AzureCli or RuntimePrerequisiteKind.DevCenterExtension => ["azure"],
            RuntimePrerequisiteKind.DevTunnel => ["tunnel"],
            _ => [group]
        };
        DomainSnapshot<IReadOnlyList<RuntimePrerequisite>>? completed = null;
        var outcome = await RunCommandAsync(group, resources, TimeSpan.FromMinutes(3), async (token, _) =>
        {
            var captured = CapturePrerequisitePath(kind, testedPath);
            SetPrerequisite(kind, captured, PrerequisiteCheckState.Running, null);
            try
            {
                var result = await (options.Diagnostic is { } diagnostic ? diagnostic(kind, captured, token) : kind switch
                {
                    RuntimePrerequisiteKind.AzureCli => AzureCliDiagnostics.CheckAsync(captured, token),
                    RuntimePrerequisiteKind.DevCenterExtension => AzureCliDiagnostics.CheckDevCenterExtensionAsync(captured, token),
                    RuntimePrerequisiteKind.DevTunnel => DevTunnelDiagnostics.CheckAsync(captured, token),
                    _ => WindowsAppDiagnostics.CheckAsync(token)
                }).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                SetPrerequisite(kind, captured, result.Passed ? PrerequisiteCheckState.Passed : PrerequisiteCheckState.Failed, result);
                completed = Prerequisites;
                token.ThrowIfCancellationRequested();
                if (!result.Passed) throw new RuntimeCommandException(new(1010, kind.ToString(), true));
                return completed;
            }
            catch (OperationCanceledException)
            {
                SetPrerequisite(kind, captured, PrerequisiteCheckState.Cancelled, null);
                throw;
            }
            catch (Exception error) when (IsExpected(error))
            {
                SetPrerequisite(kind, captured, PrerequisiteCheckState.Failed, null);
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);
        return outcome with { Snapshot = outcome.Snapshot ?? completed };
    }

    internal static string? CapturePrerequisitePath(RuntimePrerequisiteKind kind, string? testedPath)
    {
        if (!Enum.IsDefined(kind) || testedPath is { Length: > 32768 })
            throw new ArgumentException("Invalid prerequisite.");
        if (kind == RuntimePrerequisiteKind.DevTunnel)
        {
            if (string.IsNullOrWhiteSpace(testedPath)) return CliTunnelController.DefaultCliPath;
            if (!Path.IsPathFullyQualified(testedPath) || testedPath.Any(char.IsControl))
                throw new ArgumentException("Invalid prerequisite path.");
            return testedPath;
        }
        return kind is RuntimePrerequisiteKind.AzureCli or RuntimePrerequisiteKind.DevCenterExtension
            ? AzureCliInstallation.ResolvePath(testedPath) : null;
    }

    internal Task<RuntimeResult<IReadOnlyList<RuntimePrerequisite>>> CheckAllPrerequisitesAsync(
        string? azureCliPath = null, string? devTunnelCliPath = null, CancellationToken cancellationToken = default) =>
        RunCommandAsync("prerequisites", ["prerequisiteBatch"], TimeSpan.FromMinutes(3), async (token, _) =>
        {
            var azure = CheckAzureSequenceAsync();
            var results = await Task.WhenAll(azure,
                CheckPrerequisiteAsync(RuntimePrerequisiteKind.DevTunnel, devTunnelCliPath, token),
                CheckPrerequisiteAsync(RuntimePrerequisiteKind.WindowsApp, null, token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var failure = results.FirstOrDefault(result => !result.Succeeded);
            if (failure?.Error is { } error) throw new RuntimeCommandException(error);
            return Prerequisites;

            async Task<RuntimeResult<IReadOnlyList<RuntimePrerequisite>>> CheckAzureSequenceAsync()
            {
                var first = await CheckPrerequisiteAsync(RuntimePrerequisiteKind.AzureCli, azureCliPath, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return first;
                var second = await CheckPrerequisiteAsync(RuntimePrerequisiteKind.DevCenterExtension, azureCliPath, token).ConfigureAwait(false);
                return first.Succeeded ? second : first;
            }
        }, cancellationToken);

    private void SetPrerequisite(RuntimePrerequisiteKind kind, string? path, PrerequisiteCheckState state, PrerequisiteDiagnosticResult? result)
    {
        lock (prerequisiteSync)
        {
            var items = Prerequisites.State.Select(item => item.Id == kind.ToString()
                ? new RuntimePrerequisite(item.Id, path, state, result) : item).ToArray();
            prerequisites.Publish(Array.AsReadOnly(items));
        }
    }

    internal void CancelPrerequisites(RuntimePrerequisiteKind? kind = null)
    {
        if (kind is null) CancelGroup("prerequisites");
        foreach (var selected in kind is { } one ? [one] : Enum.GetValues<RuntimePrerequisiteKind>())
            CancelGroup($"prerequisite:{selected}");
    }
}
