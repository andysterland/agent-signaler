using System.Globalization;
using System.Text.Json;
using AgentSignaler.Contracts.Rpc.V1;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;

namespace AgentSignaler.RpcHost;

internal sealed class RuntimeRpcApplication : IRpcApplication, IDisposable
{
    private readonly DashboardRuntime runtime;
    private readonly Action shutdown;
    private readonly Action<Task>? acceptShutdown;
    private readonly object statusLock = new();
    private DomainSnapshot<RpcSystemState> system;
    private long lastRuntimeStatusRevision;
    private int drainIncomplete;
    public Guid HostInstanceId => runtime.HostInstanceId;
    public event EventHandler<RpcPublication>? Published;

    public RuntimeRpcApplication(DashboardRuntime runtime, Action shutdown, Action<Task>? acceptShutdown = null)
    {
        this.runtime = runtime;
        this.shutdown = shutdown;
        this.acceptShutdown = acceptShutdown;
        var initial = runtime.Status;
        lastRuntimeStatusRevision = initial.Revision;
        system = new(HostInstanceId, "system", 0, SystemState(initial.State));
        runtime.Changed += Changed;
        runtime.Ready += Ready;
        runtime.Problem += Problem;
        runtime.ConnectionTestReceived += ConnectionTest;
    }

    public async Task<RpcExecutionResult> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            switch (method)
            {
                case "system.getStatus":
                    RpcConnection.RequireEmpty(parameters);
                    return SystemQuery();
                case "system.cancelStartup":
                    RpcConnection.RequireEmpty(parameters);
                    runtime.CancelStartup();
                    return SystemQuery();
                case "settings.get":
                    RpcConnection.RequireEmpty(parameters);
                    return Query(runtime.Settings, SettingsState);
                case "settings.update":
                    return await UpdateSettingsAsync(parameters, cancellationToken);
                case "receiver.getStatus":
                    RpcConnection.RequireEmpty(parameters);
                    return Query(runtime.Receiver, ReceiverState);
                case "sharing.getStatus":
                    RpcConnection.RequireEmpty(parameters);
                    return Query(runtime.Sharing, SharingState);
                case "sharing.start":
                case "sharing.stop":
                case "sharing.delete":
                case "sharing.logout":
                {
                    var p = method is "sharing.delete" or "sharing.logout" ? new RpcParameters(parameters, "confirmed") :
                        new RpcParameters(parameters);
                    var operation = method switch
                    {
                        "sharing.start" => RuntimeSharingOperation.Start,
                        "sharing.stop" => RuntimeSharingOperation.Stop,
                        "sharing.delete" => RuntimeSharingOperation.Delete,
                        _ => RuntimeSharingOperation.Logout
                    };
                    return Result(await runtime.SharingAsync(operation, p.Bool("confirmed"), cancellationToken), SharingState);
                }
                case "sharing.cancel":
                    RpcConnection.RequireEmpty(parameters);
                    runtime.CancelSharing();
                    return Query(runtime.Sharing, SharingState);
                case "machines.list":
                {
                    var p = new RpcParameters(parameters, "offset", "limit", "expectedRevision");
                    var offset = p.Int("offset", 0);
                    var limit = p.Int("limit", 100);
                    return Query(runtime.GetMachines(offset, limit, p.Revision()), state => MachinePage(state, offset, limit));
                }
                case "machines.get":
                {
                    var p = new RpcParameters(parameters, "machineId");
                    return Query(runtime.GetMachine(p.Guid("machineId")), Machine);
                }
                case "machines.getSessions":
                {
                    var p = new RpcParameters(parameters, "machineId", "offset", "limit", "expectedRevision");
                    var offset = p.Int("offset", 0);
                    var limit = p.Int("limit", 100);
                    return Query(runtime.GetSessions(p.Guid("machineId"), offset, limit, p.Revision()), state =>
                        BoundedPage(state.Items.Select(Session).ToArray(), state.Total, offset, limit));
                }
                case "machines.getNote":
                {
                    var p = new RpcParameters(parameters, "machineId", "offset", "length", "expectedRevision");
                    var offset = p.Int("offset", 0);
                    return Query(runtime.GetNote(p.Guid("machineId"), offset, p.Int("length", 16384), p.Revision()),
                        state => new RpcNoteChunk(state.Text, offset, state.NextOffset, state.TotalLength));
                }
                case "machines.updateDetails":
                {
                    var p = new RpcParameters(parameters, "machineId", "hostInstanceId", "expectedRevision", "displayName", "note");
                    if (!p.Has("displayName") && !p.Has("note")) throw new RpcFault(-32602);
                    var id = p.Guid("machineId");
                    var current = runtime.GetMachine(id);
                    var name = p.Has("displayName") ? p.String("displayName") : current.State.Machine.DisplayName;
                    var note = p.Has("note") ? p.String("note") : current.State.Machine.Note;
                    if (p.Has("note") && note is { Length: > 16384 }) throw new RpcFault(1001, new(Field: "note"));
                    return Result(await runtime.UpdateMachineAsync(id, name, note, p.Guid("hostInstanceId"),
                        p.Revision(required: true)!.Value, cancellationToken), Machine);
                }
                case "machines.remove":
                {
                    var p = new RpcParameters(parameters, "machineId", "hostInstanceId", "expectedRevision", "confirmed");
                    return Result(await runtime.RemoveMachineAsync(p.Guid("machineId"), p.Bool("confirmed"),
                        p.Guid("hostInstanceId"), p.Revision(required: true)!.Value, cancellationToken),
                        state => MachinePage(state, 0, 100));
                }
                case "devboxes.getCatalog":
                {
                    var p = new RpcParameters(parameters, "offset", "limit", "expectedRevision");
                    var snapshot = runtime.Catalog;
                    var offset = p.Int("offset", 0);
                    var limit = p.Int("limit", 100);
                    ValidatePage(offset, limit, p.Revision(), snapshot.Revision);
                    return Query(snapshot, state => CatalogState(state, offset, limit));
                }
                case "devboxes.refresh":
                {
                    var p = new RpcParameters(parameters, "hostInstanceId", "expectedRevision", "subscriptionId", "devCenterName");
                    var saved = runtime.Settings.State.Saved;
                    return Result(await runtime.RefreshCatalogAsync(
                        p.Has("subscriptionId") ? p.OptionalGuid("subscriptionId") : saved.DevBoxSubscriptionId,
                        p.Has("devCenterName") ? p.String("devCenterName") : saved.DevCenterName,
                        p.Guid("hostInstanceId"), p.Revision(required: true)!.Value, cancellationToken),
                        state => CatalogState(state, 0, 100));
                }
                case "devboxes.cancel":
                    RpcConnection.RequireEmpty(parameters);
                    runtime.CancelCatalog();
                    return Query(runtime.Catalog, state => CatalogState(state, 0, 100));
                case "prerequisites.getStatus":
                    RpcConnection.RequireEmpty(parameters);
                    return Query(runtime.Prerequisites, PrerequisiteState);
                case "prerequisites.check":
                {
                    var p = new RpcParameters(parameters, "id", "path");
                    var kind = PrerequisiteKind(p.String("id", true)!);
                    var path = p.Has("path") ? p.String("path") : SavedDiagnosticPath(kind);
                    return Result(await runtime.CheckPrerequisiteAsync(kind, path, cancellationToken), PrerequisiteState);
                }
                case "prerequisites.checkAll":
                {
                    var p = new RpcParameters(parameters, "azureCliPath", "devTunnelCliPath");
                    return Result(await runtime.CheckAllPrerequisitesAsync(
                        p.Has("azureCliPath") ? p.String("azureCliPath") : runtime.Settings.State.Saved.AzureCliPath,
                        p.Has("devTunnelCliPath") ? p.String("devTunnelCliPath") : runtime.Settings.State.Saved.DevTunnelCliPath,
                        cancellationToken), PrerequisiteState);
                }
                case "prerequisites.cancel":
                {
                    var p = new RpcParameters(parameters, "id");
                    runtime.CancelPrerequisites(p.Has("id") ? PrerequisiteKind(p.String("id", true)!) : null);
                    return Query(runtime.Prerequisites, PrerequisiteState);
                }
                case "windowsApp.getState":
                {
                    var p = new RpcParameters(parameters, "machineId");
                    var id = p.Guid("machineId");
                    return Query(runtime.GetWindowsAppState(id), state => WindowsState(id, state));
                }
                case "windowsApp.cancel":
                {
                    var p = new RpcParameters(parameters, "machineId");
                    var id = p.Guid("machineId");
                    runtime.CancelWindowsApp(id);
                    return Query(runtime.GetWindowsAppState(id), state => WindowsState(id, state));
                }
                case "windowsApp.map":
                case "windowsApp.signIn":
                case "windowsApp.refresh":
                case "windowsApp.open":
                case "windowsApp.openLastKnown":
                case "windowsApp.clear":
                    return await WindowsCommandAsync(method, parameters, cancellationToken);
                default: throw new RpcFault(-32601);
            }
        }
        catch (RuntimeCommandException error) { throw Fault(error.Error); }
        catch (ArgumentException) { throw new RpcFault(1001); }
    }

    private async Task<RpcExecutionResult> UpdateSettingsAsync(JsonElement parameters, CancellationToken token)
    {
        var p = new RpcParameters(parameters, "hostInstanceId", "expectedRevision", "settings");
        if (p.Get("settings").ValueKind != JsonValueKind.Object) throw new RpcFault(-32602);
        var patch = new RpcParameters(p.Get("settings"), "port", "rpcPort", "connectionMode", "devTunnelCliPath",
            "azureCliPath", "devBoxSubscriptionId", "devCenterName", "autoStartSharing", "receiveDetailedConversations");
        var current = runtime.Settings.State.Saved;
        var next = current with
        {
            Port = patch.Int("port", current.Port),
            RpcPort = patch.Int("rpcPort", current.RpcPort),
            ConnectionMode = patch.Has("connectionMode") ? patch.String("connectionMode", true) switch
            {
                "lan" => DashboardConnectionMode.Lan, "devTunnel" => DashboardConnectionMode.DevTunnel,
                _ => throw new RpcFault(1001, new(Field: "connectionMode"))
            } : current.ConnectionMode,
            DevTunnelCliPath = patch.Has("devTunnelCliPath") ? patch.String("devTunnelCliPath") : current.DevTunnelCliPath,
            AzureCliPath = patch.Has("azureCliPath") ? patch.String("azureCliPath") : current.AzureCliPath,
            DevBoxSubscriptionId = patch.Has("devBoxSubscriptionId") ? patch.OptionalGuid("devBoxSubscriptionId") : current.DevBoxSubscriptionId,
            DevCenterName = patch.Has("devCenterName") ? patch.String("devCenterName") : current.DevCenterName,
            AutoStartSharing = patch.Bool("autoStartSharing", current.AutoStartSharing),
            ReceiveDetailedConversations = patch.Bool("receiveDetailedConversations", current.ReceiveDetailedConversations)
        };
        return Result(await runtime.UpdateSettingsAsync(next, p.Guid("hostInstanceId"),
            p.Revision(required: true)!.Value, token,
            explicitDetailedReceptionEnable: patch.Has("receiveDetailedConversations") && next.ReceiveDetailedConversations), SettingsState);
    }

    private async Task<RpcExecutionResult> WindowsCommandAsync(string method, JsonElement parameters, CancellationToken token)
    {
        var p = method switch
        {
            "windowsApp.map" => new RpcParameters(parameters, "machineId", "hostInstanceId", "expectedRevision", "selection"),
            "windowsApp.clear" => new RpcParameters(parameters, "machineId", "hostInstanceId", "expectedRevision", "confirmed"),
            _ => new RpcParameters(parameters, "machineId", "hostInstanceId", "expectedRevision")
        };
        var operation = method switch
        {
            "windowsApp.map" => WindowsAppOperation.Map, "windowsApp.signIn" => WindowsAppOperation.SignIn,
            "windowsApp.refresh" => WindowsAppOperation.Refresh, "windowsApp.open" => WindowsAppOperation.Open,
            "windowsApp.openLastKnown" => WindowsAppOperation.OpenLastKnown, _ => WindowsAppOperation.Clear
        };
        DevBoxMappingSelection? selection = null;
        if (operation == WindowsAppOperation.Map)
        {
            var s = new RpcParameters(p.Get("selection"), "devCenterEndpoint", "projectName", "devBoxName", "azureAccountUpn", "azureTenantId");
            if (!Uri.TryCreate(s.String("devCenterEndpoint", true), UriKind.Absolute, out var endpoint))
                throw new RpcFault(1001, new(Field: "devCenterEndpoint"));
            var item = new DevBoxCatalogItem(endpoint, s.String("projectName", true)!, "", s.String("devBoxName", true)!,
                "", "", null, null, null, null);
            selection = new(item, s.String("azureAccountUpn", true)!, s.Guid("azureTenantId"));
        }
        var id = p.Guid("machineId");
        return Result(await runtime.WindowsAppAsync(id, operation, selection, p.Bool("confirmed"),
            p.Guid("hostInstanceId"), p.Revision(required: true)!.Value, token), state => WindowsState(id, state));
    }

    private string? SavedDiagnosticPath(RuntimePrerequisiteKind kind) => kind switch
    {
        RuntimePrerequisiteKind.DevTunnel => runtime.Settings.State.Saved.DevTunnelCliPath,
        RuntimePrerequisiteKind.AzureCli or RuntimePrerequisiteKind.DevCenterExtension => runtime.Settings.State.Saved.AzureCliPath,
        _ => null
    };

    private static RuntimePrerequisiteKind PrerequisiteKind(string id) => id switch
    {
        "azureCli" => RuntimePrerequisiteKind.AzureCli, "devCenterExtension" => RuntimePrerequisiteKind.DevCenterExtension,
        "devTunnel" => RuntimePrerequisiteKind.DevTunnel, "windowsApp" => RuntimePrerequisiteKind.WindowsApp,
        _ => throw new RpcFault(1001, new(Field: "id"))
    };

    private RpcSystemState SystemState(RuntimeStatus state) => new(Volatile.Read(ref drainIncomplete) != 0 ? "degraded" : Name(state.Lifecycle), state.Stage,
        state.InitialAttemptCompleted, state.StorageAvailable, state.ReceiverAvailable, state.SharingAvailable,
        Volatile.Read(ref drainIncomplete) != 0);
    private static RpcOperationalSettings OperationalSettings(DashboardSettings state) => new(state.Port, state.RpcPort,
        Name(state.ConnectionMode), state.DevTunnelCliPath, state.AzureCliPath, state.DevBoxSubscriptionId?.ToString("D"),
        state.DevCenterName, state.AutoStartSharing, state.ReceiveDetailedConversations);
    private static RpcSettingsState SettingsState(RuntimeSettings state) => new(OperationalSettings(state.Saved),
        OperationalSettings(state.Effective), state.RpcPortOverridden, state.RestartRequired, state.Recovered);
    private static RpcReceiverState ReceiverState(RuntimeReceiver state) => new(state.Running, state.Port, Name(state.Mode),
        state.ReceiveDetailedConversations, $"http://localhost:{state.Port}", state.InstalledReceiverPort.HasValue ? "msi" : "manual",
        state.InstalledReceiverPort, state.FirewallPortMismatch,
        state.InstalledReceiverPort.HasValue ? "useMsiMaintenanceToChangeReceiverRule" : "configureLanFirewallManually");
    private static RpcSharingState SharingState(TunnelStatus state) => new(Name(state.State), state.Message,
        state.CanCopy ? state.PublicUrl!.AbsoluteUri : null, state.CanCopy);
    private static RpcMapping? Mapping(WindowsAppConnection? mapping) => mapping is null ? null : new(
        mapping.DevCenterEndpoint.AbsoluteUri, mapping.ProjectName, mapping.DevBoxName, mapping.AzureAccountUpn,
        mapping.AzureTenantId.ToString("D"), mapping.ConnectionUriRetrievedAtUtc?.ToUniversalTime(),
        mapping.LastKnownConnectionUri is not null);
    private static RpcMachine Machine(RuntimeMachine state)
    {
        var m = state.Machine;
        return new(m.MachineId.ToString("D"), m.MachineName, m.DisplayName, m.Client, m.ClientVersion,
            Name(m.State), m.LatestEvent is { } e ? Name(e) : null, m.LastContactUtc.ToUniversalTime(),
            state.Sessions.Count, m.Note?.Length ?? 0, Name(m.PresenceMode), m.HeartbeatIntervalSeconds,
            Decimal(m.Generation), Decimal(m.Sequence), m.ExplicitOffline, m.OfflineDeadlineUtc.ToUniversalTime(),
            m.LatestEventUtc?.ToUniversalTime(), Mapping(m.WindowsAppConnection));
    }
    private static RpcSession Session(RuntimeSession state)
    {
        var s = state.Snapshot;
        return new(s.SessionId, s.Source is { } source ? new(source.Kind, source.ScopeId, source.Version) : null,
            Name(state.State), Name(s.UnderlyingState), s.ResultState is { } result ? Name(result) : null,
            s.ResultUntilUtc?.ToUniversalTime(), s.AwaitingUserInput, s.UpdatedAtUtc.ToUniversalTime())
        {
            DisplayName = s.DisplayName
        };
    }
    private static RpcPage<RpcMachine> MachinePage(RuntimePage<RuntimeMachine> state, int offset, int limit) =>
        BoundedPage(state.Items.Select(Machine).ToArray(), state.Total, offset, limit);
    private static RpcPrerequisiteState PrerequisiteState(IReadOnlyList<RuntimePrerequisite> state) =>
        new(state.Select(p => new RpcPrerequisite(JsonNamingPolicy.CamelCase.ConvertName(p.Id), p.TestedPath,
            Name(p.State), p.Result?.Passed, p.Result?.Details)).ToArray());
    private static RpcDevBox DevBox(DevBoxCatalogItem item) => new(item.DevCenterEndpoint.AbsoluteUri, item.ProjectName,
        item.PoolName, item.DevBoxName, item.PowerState, item.ProvisioningState, item.OperatingSystem,
        item.VCpus, item.MemoryGb, item.UniqueId?.ToString("D"));
    private static RpcCatalogState CatalogState(DevBoxCatalogState state, int offset, int limit)
    {
        var items = DevBoxMappingPresentation.SortItems(state.Snapshot?.Items ?? []);
        if (offset > items.Count) throw new RpcFault(1001, new(Field: "offset"));
        var page = BoundedPage(items.Skip(offset).Take(limit).Select(DevBox).ToArray(), items.Count, offset, limit);
        var progress = state.Progress;
        return new(page.Items, offset, limit, page.NextOffset, page.TotalCount, state.IsBusy,
            state.Status, state.Message, progress is null ? null : new(Name(progress.Stage), progress.SubscriptionsCompleted,
                progress.DevCentersCompleted, progress.PagesRead, progress.SubscriptionCount, progress.DevCenterCount, progress.DevBoxCount),
            state.Snapshot?.AzureAccountUpn, state.Snapshot?.AzureTenantId.ToString("D"),
            state.Snapshot?.RetrievedAtUtc.ToUniversalTime(), state.TargetSubscription?.ToString("D"), state.TargetDevCenterName);
    }
    private static RpcWindowsAppState WindowsState(Guid id, WindowsAppConnectionState state) => new(id.ToString("D"),
        Mapping(state.Mapping), state.Status, state.Message, state.Operation is { } op ? Name(op) : null,
        state.IsBusy, state.CanOpenLastKnown);

    // A conservative page budget leaves room for the envelope even inside a maximum-sized batch.
    private static RpcPage<T> BoundedPage<T>(IReadOnlyList<T> candidates, int total, int offset, int limit)
    {
        var items = new List<T>();
        var bytes = 0;
        foreach (var item in candidates)
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(item, RpcProtocol.Json).Length + 1;
            if (bytes + size > 96 * 1024)
            {
                if (items.Count == 0) throw new RpcFault(1011, new(Recovery: "useEntityQueries"));
                break;
            }
            items.Add(item);
            bytes += size;
        }
        return new(items.AsReadOnly(), offset, limit, offset + items.Count < total ? offset + items.Count : null, total);
    }

    private static void ValidatePage(int offset, int limit, long? expected, long actual)
    {
        if (offset < 0 || limit is < 1 or > 250) throw new RpcFault(1001, new(Field: "pagination"));
        if (offset > 0 && expected is null) throw new RpcFault(1001, new(Field: "expectedRevision"));
        if (expected.HasValue && expected.Value != actual) throw new RpcFault(1004, new(Field: "expectedRevision", Retryable: true));
    }
    private static RpcExecutionResult Query<T, TWire>(DomainSnapshot<T> snapshot, Func<T, TWire> map) =>
        new(Snapshot(snapshot, map));
    private static RpcExecutionResult Result<T, TWire>(RuntimeResult<T> result, Func<T, TWire> map)
    {
        if (result.Error is { } error) throw Fault(error);
        if (result.Snapshot is null) throw new RpcFault(-32603, new(CommitState: Commit(result.CommitState)));
        try { return new(Snapshot(result.Snapshot, map), Commit(result.CommitState)); }
        catch (RpcFault fault) when (fault.Code == 1011)
        {
            throw new RpcFault(1011, fault.Data with { CommitState = Commit(result.CommitState) });
        }
    }
    private static RpcSnapshot<TWire> Snapshot<T, TWire>(DomainSnapshot<T> snapshot, Func<T, TWire> map) =>
        new(1, snapshot.HostInstanceId.ToString("D"), snapshot.Domain, Decimal(snapshot.Revision),
            map(snapshot.State), snapshot.IsStale);
    private static RpcFault Fault(RuntimeError error) =>
        new(error.Code, new(Field: error.Field.Split(':', 2)[0], Retryable: error.Retryable, CommitState: Commit(error.CommitState)));
    private static string Commit(RuntimeCommitState state) => state switch
    {
        RuntimeCommitState.NotCommitted => "notCommitted", RuntimeCommitState.Committed => "committed", _ => "unknown"
    };
    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    private void Changed(RuntimeInvalidation change)
    {
        if (change.Domain == "system")
        {
            UpdateSystem();
            return;
        }
        var value = new RpcInvalidation(change.HostInstanceId.ToString("D"), change.Domain, Decimal(change.Revision),
            change.MachineId?.ToString("D"));
        Published?.Invoke(this, new(change.Domain + ".changed", value, value));
    }
    private RpcExecutionResult SystemQuery()
    {
        lock (statusLock) return Query(system, state => state);
    }

    private void UpdateSystem(bool force = false)
    {
        RpcInvalidation invalidation;
        lock (statusLock)
        {
            var current = runtime.Status;
            if (!force && current.Revision <= lastRuntimeStatusRevision) return;
            lastRuntimeStatusRevision = current.Revision;
            system = new(HostInstanceId, "system", checked(system.Revision + 1), SystemState(current.State));
            invalidation = new(HostInstanceId.ToString("D"), "system", Decimal(system.Revision));
        }
        Published?.Invoke(this, new("system.changed", invalidation, invalidation));
    }

    private void Ready(RuntimeStatus _) => Published?.Invoke(this, new("system.ready", SystemQuery().State!));
    private void Problem(RuntimeError error) => Published?.Invoke(this,
        new("system.problem", new RpcProblem(HostInstanceId.ToString("D"), error.Code, Fault(error).Data)));
    private void ConnectionTest() => Published?.Invoke(this, new("receiver.connectionTestReceived", new RpcConnectionTest(HostInstanceId.ToString("D"))));
    public void RequestShutdown()
    {
        Published?.Invoke(this, new("system.shuttingDown", new RpcShutdownResult("stopping")));
        shutdown();
    }
    public void AcceptShutdown(Task acknowledgement) => acceptShutdown?.Invoke(acknowledgement);
    public void ReportDrainTimeout()
    {
        Interlocked.Exchange(ref drainIncomplete, 1);
        UpdateSystem(force: true);
        Problem(new(1003, "controllerDrain", true, RuntimeCommitState.Unknown));
    }
    public void ReportDrainCompleted()
    {
        if (Interlocked.Exchange(ref drainIncomplete, 0) != 0) UpdateSystem(force: true);
    }
    public void Dispose()
    {
        runtime.Changed -= Changed;
        runtime.Ready -= Ready;
        runtime.Problem -= Problem;
        runtime.ConnectionTestReceived -= ConnectionTest;
    }
}
