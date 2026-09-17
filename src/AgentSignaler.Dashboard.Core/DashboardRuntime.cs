using System.ComponentModel;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Dashboard;

/// <summary>Shared resource owner for WinUI and RPC; it borrows the entry point's process lease.</summary>
public sealed partial class DashboardRuntime : IAsyncDisposable
{
    private readonly DashboardResourceLease lease;
    private readonly DashboardRuntimeOptions options;
    private readonly RuntimeCommandGate gates = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationTokenSource startup = new();
    private readonly object lifecycleSync = new();
    private readonly object operationSync = new();
    private readonly Dictionary<long, PendingCommand> commands = [];
    private long nextCommand;
    private volatile bool stopping;
    private Task? initialization;
    private Task? polling;
    private Task<RuntimeShutdownResult>? shutdown;
    private readonly RuntimeDomain<RuntimeStatus> status;
    private readonly RuntimeDomain<RuntimeSettings> settings;
    private readonly RuntimeDomain<RuntimeReceiver> receiver;
    private readonly RuntimeDomain<TunnelStatus> sharing;
    private readonly RuntimeDomain<DevBoxCatalogState> catalog;
    private readonly RuntimeDomain<IReadOnlyList<RuntimePrerequisite>> prerequisites;
    private MachineStore? store;
    private DashboardServer? server;
    private CliTunnelController? tunnel;
    private DevBoxCatalogController? catalogController;
    private WindowsAppConnectionController? connections;
    private readonly Dictionary<Guid, RuntimeDomain<WindowsAppConnectionState>> windowsDomains = [];
    private readonly object windowsSync = new();
    private long windowsRevision;
    private readonly SemaphoreSlim machineRefresh = new(1, 1);
    private readonly object machineSync = new();
    private DomainSnapshot<IReadOnlyList<RuntimeMachine>> machines;
    private readonly Dictionary<Guid, long> machineRevisions = [];
    private long nextMachineRevision;
    private sealed record PendingCommand(string Group, CancellationTokenSource Cancellation, TaskCompletionSource Completion, bool Control);
    private sealed class CommitTracker { public RuntimeCommitState State; }
    public Guid HostInstanceId { get; } = Guid.NewGuid();
    public event Action<RuntimeInvalidation>? Changed;
    public event Action<RuntimeStatus>? Ready;
    public event Action<RuntimeError>? Problem;
    public event Action? ConnectionTestReceived;
    public DomainSnapshot<RuntimeStatus> Status => status.Read();
    internal DomainSnapshot<RuntimeSettings> Settings => settings.Read();
    internal DomainSnapshot<RuntimeReceiver> Receiver => receiver.Read();
    internal DomainSnapshot<TunnelStatus> Sharing => sharing.Read();
    internal DomainSnapshot<DevBoxCatalogState> Catalog => catalog.Read();
    internal DomainSnapshot<IReadOnlyList<RuntimePrerequisite>> Prerequisites => prerequisites.Read();
    internal MachineStore? Store => store;
    internal DashboardServer? Server => server;
    internal CliTunnelController? Tunnel => tunnel;
    internal DevBoxCatalogController? CatalogController => catalogController;
    internal WindowsAppConnectionController? Connections => connections;

    public DashboardRuntime(DashboardResourceLease lease, DashboardRuntimeOptions? options = null)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (!lease.IsHeld) throw new DashboardOwnershipException();
        this.options = options ?? new();
        if (this.options.RpcPortOverride is < 1024 or > 65535) throw new ArgumentException("Invalid RPC port.");
        DashboardSettings saved;
        var recovered = false;
        try { saved = DashboardSettings.Load(lease.CanonicalDirectory, this.options.RejectInvalidSavedPorts); }
        catch (Exception error) when (IsPersistenceFailure(error) || error is System.Text.DecoderFallbackException)
        {
            saved = DashboardSettings.RecoveryDefaults;
            recovered = true;
        }
        var effective = saved with
        {
            RpcPort = this.options.RpcPortOverride ?? saved.RpcPort,
            AzureCliPath = AzureCliInstallation.ResolvePath(saved.AzureCliPath),
            DevTunnelCliPath = string.IsNullOrWhiteSpace(saved.DevTunnelCliPath) ? CliTunnelController.DefaultCliPath : saved.DevTunnelCliPath
        };
        settings = new(HostInstanceId, "settings", new(saved, effective, this.options.RpcPortOverride.HasValue, [], recovered), Publish);
        status = new(HostInstanceId, "system", new(RuntimeLifecycle.TransportReady, "transportReady", false, false, false, false), Publish);
        receiver = new(HostInstanceId, "receiver", new(false, this.options.ReceiverPortOverride ?? effective.Port,
            effective.ConnectionMode, effective.ReceiveDetailedConversations, this.options.InstalledReceiverPort), Publish);
        sharing = new(HostInstanceId, "sharing", new(TunnelState.Stopped, "Sharing is stopped."), Publish);
        catalog = new(HostInstanceId, "devboxes", new(null, false, "Not refreshed", "Catalog has not been refreshed."), Publish);
        prerequisites = new(HostInstanceId, "prerequisites", Array.AsReadOnly(Enum.GetValues<RuntimePrerequisiteKind>()
            .Select(kind => new RuntimePrerequisite(kind.ToString(), null, PrerequisiteCheckState.NotChecked, null)).ToArray()), Publish);
        machines = new(HostInstanceId, "machines", 0, Array.Empty<RuntimeMachine>());
    }

    private void Publish(RuntimeInvalidation change) => Changed?.Invoke(change);

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycleSync)
            return initialization ?? (stopping ? Task.CompletedTask : initialization = Task.Run(() => InitializeCoreAsync(cancellationToken)));
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3), options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, startup.Token, lifetime.Token);
        var token = linked.Token;
        status.Publish(Status.State with { Lifecycle = RuntimeLifecycle.Initializing, Stage = "storage" });
        try
        {
            token.ThrowIfCancellationRequested();
            if (!lease.IsHeld) throw new DashboardOwnershipException();
            var effective = Settings.State.Effective;
            var cli = options.AzureCli ?? new AzureCliProcess(executablePath: effective.AzureCliPath, timeProvider: options.TimeProvider);
            var resolver = options.ConnectionResolver ?? new DevBoxConnectionResolver(cli, options.TimeProvider);
            catalogController = new(options.CatalogService ?? new DevBoxCatalogService(cli, options.TimeProvider));
            catalogController.Changed += () => catalog.Publish(FreezeCatalog(catalogController.State));
            try
            {
                var databasePath = Path.Combine(lease.CanonicalDirectory, "dashboard.db");
                store = options.CreateStoreAsync is { } create
                    ? await create(databasePath, options.TimeProvider, token).ConfigureAwait(false)
                    : (options.StoreFactory ?? ((path, clock) => new MachineStore(path, clock)))(databasePath, options.TimeProvider);
                var gate = new WindowsAppOperationGate();
                var launcher = new WindowsAppLauncher(resolver,
                    new TrackingWindowsAppPlatform(options.WindowsAppPlatform ?? new WindowsAppPlatform(), TrackSideEffect),
                    PersistMappingAsync, gate);
                connections = new(async (id, ct) =>
                    (await store.GetMachinesAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.MachineId == id)?.WindowsAppConnection,
                    PersistMappingAsync, ClearMappingAsync, cli, resolver, launcher, gate,
                    () => catalogController.State.IsBusy, TrackSideEffect,
                    id => MachineNavigation.IsLocal(GetMachine(id).State.Machine));
                connections.Changed += UpdateWindowsState;
                status.Publish(Status.State with { StorageAvailable = true, Stage = "receiver" });
                server = new(store, Receiver.State.Port, () => ConnectionTestReceived?.Invoke(), new DashboardServerOptions
                {
                    ListenerMode = effective.ConnectionMode == DashboardConnectionMode.DevTunnel
                        ? DashboardListenerMode.Internet : DashboardListenerMode.Lan,
                    ReceiveDetailedConversations = effective.ReceiveDetailedConversations,
                    AllowEphemeralPort = options.ReceiverPortOverride == 0
                });
                lock (detailedReceptionSync)
                    server.Transcripts.SetEnabled(Settings.State.Effective.ReceiveDetailedConversations);
                try
                {
                    await server.StartAsync(token).ConfigureAwait(false);
                    receiver.Publish(Receiver.State with { Running = true, Port = server.BoundPort });
                    status.Publish(Status.State with { ReceiverAvailable = true });
                }
                catch (Exception error) when (IsExpected(error)) { ReportFailure(error, "receiver"); }
                status.Publish(Status.State with { Stage = "machines" });
                await RefreshMachinesAsync(token).ConfigureAwait(false);
                polling = PollAsync();
            }
            catch (Exception error) when (IsExpected(error)) { ReportFailure(error, "storage"); }
            token.ThrowIfCancellationRequested();
            if (effective.ConnectionMode == DashboardConnectionMode.DevTunnel && Receiver.State.Running)
            {
                status.Publish(Status.State with { Stage = "sharing" });
                try
                {
                    var identityStore = new TunnelIdentityStore(Path.Combine(lease.CanonicalDirectory, "tunnel-state.json"));
                    var identity = await identityStore.LoadOrCreateAsync(token).ConfigureAwait(false);
                    var tunnelOptions = new TunnelOptions(effective.DevTunnelCliPath ?? CliTunnelController.DefaultCliPath,
                        Receiver.State.Port, identity);
                    tunnel = options.TunnelRunner is null && options.TunnelHealthProbe is null
                        ? new(tunnelOptions, identityStore.SaveAsync)
                        : new(tunnelOptions, identityStore.SaveAsync,
                            options.TunnelRunner ?? new WindowsTunnelProcessRunner(),
                            options.TunnelHealthProbe ?? throw new ArgumentException("A test tunnel runner requires a health probe."));
                    tunnel.StatusChanged += (_, value) =>
                    {
                        sharing.Publish(value);
                        UpdateTranscriptReadiness();
                    };
                    status.Publish(Status.State with { SharingAvailable = true });
                    if (Settings.State.Saved.ShouldStartSharing)
                    {
                        var result = await SharingAsync(RuntimeSharingOperation.Start, true, token).ConfigureAwait(false);
                        if (!result.Succeeded) status.Publish(Status.State with { Lifecycle = RuntimeLifecycle.Degraded });
                    }
                }
                catch (Exception error) when (IsExpected(error)) { ReportFailure(error, "sharing"); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            status.Publish(Status.State with { Lifecycle = RuntimeLifecycle.Degraded, Stage = deadline.IsCancellationRequested ? "startupTimeout" : "startupCancelled" });
        }
        catch (Exception error) when (IsExpected(error)) { ReportFailure(error, "initialization"); }
        finally
        {
            if (!lifetime.IsCancellationRequested)
            {
                var current = Status.State;
                status.Publish(current with
                {
                    InitialAttemptCompleted = true,
                    Lifecycle = current.Lifecycle == RuntimeLifecycle.Degraded || !current.StorageAvailable || !current.ReceiverAvailable
                        ? RuntimeLifecycle.Degraded : RuntimeLifecycle.Operational,
                    Stage = current.Lifecycle == RuntimeLifecycle.Degraded ? current.Stage : "ready"
                });
                Ready?.Invoke(Status.State);
            }
        }
    }

    public void CancelStartup() { lock (operationSync) if (!stopping) startup.Cancel(); }

    private void ReportFailure(Exception error, string field)
    {
        status.Publish(Status.State with { Lifecycle = RuntimeLifecycle.Degraded, Stage = field });
        Problem?.Invoke(new(IsPersistenceFailure(error) ? 1008 : 1005, field, true));
    }

    internal void UpdateTranscriptReadiness() => server?.SetTranscriptReadiness(TranscriptReceiverPolicy.IsReady(
        Receiver.State.Mode, Receiver.State.Running, Receiver.State.Port, Receiver.State.Port,
        Sharing.State.CanCopy, lifetime.IsCancellationRequested,
        Settings.State.Saved.Port != Settings.State.Effective.Port ||
        Settings.State.Saved.ConnectionMode != Settings.State.Effective.ConnectionMode ||
        Settings.State.RestartRequired.Contains("devTunnelCliPath")));

    private static DevBoxCatalogState FreezeCatalog(DevBoxCatalogState value) => value with
    {
        Progress = value.Progress is not { } progress ? null : progress with
        {
            SubscriptionFailures = Array.AsReadOnly(progress.SubscriptionFailures.ToArray())
        },
        Snapshot = value.Snapshot is not { } snapshot ? null : snapshot with
        {
            Items = Array.AsReadOnly(snapshot.Items.ToArray()),
            DevCenterEndpoints = Array.AsReadOnly(snapshot.DevCenterEndpoints.ToArray()),
            SubscriptionFailures = Array.AsReadOnly(snapshot.SubscriptionFailures.ToArray())
        }
    };

    private static bool IsPersistenceFailure(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or SecurityException or JsonException or SqliteException;
    private static bool IsExpected(Exception error) => IsPersistenceFailure(error) || error is SocketException or
        Win32Exception or TunnelException or WindowsAppConnectionException or DevBoxCatalogException or TimeoutException or HttpRequestException;

    private async Task<RuntimeResult<T>> RunCommandAsync<T>(string group, IEnumerable<string> resources, TimeSpan timeout,
        Func<CancellationToken, CommitTracker, Task<DomainSnapshot<T>>> action, CancellationToken cancellationToken, bool control = false)
    {
        IDisposable? resourceLease = null;
        PendingCommand? pending = null;
        long id = 0;
        var commit = new CommitTracker();
        var actionField = group.StartsWith("windowsApp:", StringComparison.Ordinal) ? "windowsApp" : group;
        using var deadline = new CancellationTokenSource(timeout, options.TimeProvider);
        CancellationTokenSource source;
        lock (operationSync)
        {
            if (stopping) return new(null, new(1005, "runtime", false), RuntimeCommitState.NotCommitted);
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, lifetime.Token);
        }
        using var linked = source;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            lock (operationSync)
            {
                if (stopping || !lease.IsHeld) throw new RuntimeCommandException(new(1005, "runtime", false));
                if (control ? commands.Values.Count(c => c.Control) >= 4 : commands.Values.Count(c => !c.Control) >= 32)
                    throw new RuntimeCommandException(new(1011, "operations", true));
                resourceLease = gates.Enter(resources);
                id = ++nextCommand;
                pending = new(group, linked, new(TaskCreationOptions.RunContinuationsAsynchronously), control);
                commands.Add(id, pending);
            }
            var snapshot = await action(linked.Token, commit).ConfigureAwait(false);
            return new(snapshot, null, commit.State);
        }
        catch (RuntimeCommandException error) { return new(null, error.Error with { CommitState = commit.State }, commit.State); }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return new(null, new(deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? 1007 : 1006,
                actionField, true, commit.State), commit.State);
        }
        catch (OperationCanceledException)
        {
            return new(null, new(1007, actionField, true, commit.State), commit.State);
        }
        catch (KeyNotFoundException) { return new(null, new(1002, "machineId", false, commit.State), commit.State); }
        catch (ArgumentException) { return new(null, new(1001, actionField, false, commit.State), commit.State); }
        catch (Exception error) when (IsExpected(error))
        {
            var failure = new RuntimeError(IsPersistenceFailure(error) ? 1008 : 1010, actionField, true, commit.State);
            Problem?.Invoke(failure);
            return new(null, failure, commit.State);
        }
        finally
        {
            resourceLease?.Dispose();
            lock (operationSync)
            {
                commands.Remove(id);
                pending?.Completion.TrySetResult();
            }
        }
    }

    private void CancelGroup(string group)
    {
        lock (operationSync)
            foreach (var command in commands.Values.Where(c => c.Group == group)) command.Cancellation.Cancel();
    }

    private Task DrainGroupAsync(string group)
    {
        lock (operationSync) return Task.WhenAll(commands.Values.Where(c => c.Group == group).Select(c => c.Completion.Task));
    }

    private void RequireInstance(Guid expected)
    {
        if (expected != HostInstanceId) throw new RuntimeCommandException(new(1004, "hostInstanceId", true));
    }

    public Task<RuntimeShutdownResult> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        lock (lifecycleSync) return shutdown ??= Task.Run(() => ShutdownCoreAsync(cancellationToken));
    }

    private async Task<RuntimeShutdownResult> ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        lock (operationSync) stopping = true;
        status.Publish(Status.State with { Lifecycle = RuntimeLifecycle.Stopping, Stage = "stopping" });
        lifetime.Cancel();
        startup.Cancel();
        server?.SetTranscriptReadiness(false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25), options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
        var clean = true;
        async Task<bool> Attempt(Func<Task> action)
        {
            Task? task = null;
            try
            {
                task = action();
                await task.WaitAsync(linked.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception error) when (IsExpected(error) || error is OperationCanceledException)
            {
                clean = false;
                if (task is not null)
                    _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return false;
            }
        }
        var initialized = initialization is null || await Attempt(() => initialization).ConfigureAwait(false);
        Task[] pending;
        lock (operationSync) pending = commands.Values.Select(c => c.Completion.Task).ToArray();
        var drained = await Attempt(() => Task.WhenAll(pending)).ConfigureAwait(false);
        if (catalogController is not null) drained &= await Attempt(catalogController.StopAsync).ConfigureAwait(false);
        if (connections is not null) drained &= await Attempt(connections.StopAsync).ConfigureAwait(false);
        if (polling is not null) drained &= await Attempt(() => polling).ConfigureAwait(false);
        if (tunnel is not null) await Attempt(() => tunnel.DisposeAsync().AsTask()).ConfigureAwait(false);
        var receiverStopped = true;
        if (server is not null)
        {
            receiverStopped = await Attempt(() => server.StopAsync(linked.Token)).ConfigureAwait(false);
            receiverStopped &= await Attempt(() => server.DisposeAsync().AsTask()).ConfigureAwait(false);
        }
        if (initialized && drained && receiverStopped)
            await Attempt(() => { store?.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
        if (!clean)
        {
            // Closing only our CLI jobs is the final reserve. Never dispose storage under an undrained writer.
            AzureCliProcessRunner.StopOwnedChildren();
            try { await NativeChild.StopOwnedChildrenAsync().ConfigureAwait(false); }
            catch (Exception error) when (IsExpected(error) || error is OperationCanceledException) { }
        }
        receiver.Publish(receiverStopped ? Receiver.State with { Running = false } : Receiver.State, stale: !receiverStopped);
        status.Publish(Status.State with { Lifecycle = RuntimeLifecycle.Stopped, Stage = clean ? "stopped" : "uncleanShutdown" });
        if (clean)
        {
            lock (operationSync)
            {
                lifetime.Dispose();
                startup.Dispose();
                machineRefresh.Dispose();
            }
        }
        return new(clean);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);
}
