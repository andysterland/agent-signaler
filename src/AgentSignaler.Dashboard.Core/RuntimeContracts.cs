using AgentSignaler.Contracts;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard;

public enum RuntimeCommitState { NotCommitted, Committed, Unknown }
public enum RuntimeLifecycle { TransportReady, Initializing, Operational, Degraded, Stopping, Stopped }
public sealed record RuntimeError(int Code, string Field, bool Retryable,
    RuntimeCommitState CommitState = RuntimeCommitState.NotCommitted);
public sealed class RuntimeCommandException(RuntimeError error) : Exception("The runtime command could not complete.")
{
    public RuntimeError Error { get; } = error;
}
public sealed record DomainSnapshot<T>(Guid HostInstanceId, string Domain, long Revision, T State, bool IsStale = false);
public sealed record RuntimeResult<T>(DomainSnapshot<T>? Snapshot, RuntimeError? Error, RuntimeCommitState CommitState)
{
    public bool Succeeded => Error is null;
}
public sealed record RuntimeInvalidation(Guid HostInstanceId, string Domain, long Revision, Guid? MachineId = null);
public sealed record RuntimeStatus(RuntimeLifecycle Lifecycle, string Stage, bool InitialAttemptCompleted,
    bool StorageAvailable, bool ReceiverAvailable, bool SharingAvailable);
public sealed record RuntimeShutdownResult(bool Clean);
public sealed record RuntimePage<T>(IReadOnlyList<T> Items, int Total, int? NextOffset);
public sealed record RuntimeSession(SessionSnapshot Snapshot, AgentState State);
public sealed record RuntimeMachine(MachineView Machine, IReadOnlyList<RuntimeSession> Sessions);
public sealed record RuntimeNote(string Text, int TotalLength, int? NextOffset);
internal sealed record RuntimeSettings(DashboardSettings Saved, DashboardSettings Effective,
    bool RpcPortOverridden, IReadOnlyList<string> RestartRequired, bool Recovered);
internal sealed record RuntimeReceiver(bool Running, int Port, DashboardConnectionMode Mode,
    bool ReceiveDetailedConversations, int? InstalledReceiverPort = null)
{
    public bool FirewallPortMismatch => InstalledReceiverPort is { } installed && installed != Port;
}
internal sealed record RuntimePrerequisite(string Id, string? TestedPath, PrerequisiteCheckState State,
    PrerequisiteDiagnosticResult? Result);
internal enum RuntimeSharingOperation { Start, Stop, Delete, Logout }
internal enum RuntimePrerequisiteKind { AzureCli, DevCenterExtension, DevTunnel, WindowsApp }

public sealed record DashboardRuntimeOptions
{
    public int? RpcPortOverride { get; init; }
    public bool RejectInvalidSavedPorts { get; init; }
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
    public int? InstalledReceiverPort { get; init; }
    internal int? ReceiverPortOverride { get; init; }
    internal IAzureCliProcess? AzureCli { get; init; }
    internal IDevBoxCatalogService? CatalogService { get; init; }
    internal IDevBoxConnectionResolver? ConnectionResolver { get; init; }
    internal IWindowsAppPlatform? WindowsAppPlatform { get; init; }
    internal ITunnelProcessRunner? TunnelRunner { get; init; }
    internal ITunnelHealthProbe? TunnelHealthProbe { get; init; }
    internal Func<string, TimeProvider, MachineStore>? StoreFactory { get; init; }
    internal Func<string, TimeProvider, CancellationToken, Task<MachineStore>>? CreateStoreAsync { get; init; }
    internal Func<MachineStore, CancellationToken, Task<IReadOnlyList<MachineView>>>? ReadMachinesAsync { get; init; }
    internal Func<RuntimePrerequisiteKind, string?, CancellationToken, Task<PrerequisiteDiagnosticResult>>? Diagnostic { get; init; }
}

internal sealed class RuntimeDomain<T>(Guid host, string name, T initial, Action<RuntimeInvalidation> changed,
    long initialRevision = 0, Func<long>? nextRevision = null)
{
    private readonly object sync = new();
    private DomainSnapshot<T> snapshot = new(host, name, initialRevision, initial);
    public DomainSnapshot<T> Read() { lock (sync) return snapshot; }
    public TResult Read<TResult>(Func<DomainSnapshot<T>, TResult> read) { lock (sync) return read(snapshot); }
    public DomainSnapshot<T> Publish(T state, bool stale = false, bool force = false)
    {
        DomainSnapshot<T> next;
        lock (sync)
        {
            if (!force && Equals(snapshot.State, state) && snapshot.IsStale == stale) return snapshot;
            next = snapshot = new(host, name, nextRevision?.Invoke() ?? checked(snapshot.Revision + 1), state, stale);
        }
        changed(new(host, name, next.Revision));
        return next;
    }
}

internal sealed class RuntimeCommandGate
{
    private readonly object sync = new();
    private readonly HashSet<string> held = new(StringComparer.Ordinal);
    public IDisposable Enter(IEnumerable<string> resources)
    {
        var keys = resources.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        lock (sync)
        {
            if (keys.Any(held.Contains)) throw new RuntimeCommandException(new(1003, "operation", true));
            foreach (var key in keys) held.Add(key);
        }
        return new Lease(this, keys);
    }
    private sealed class Lease(RuntimeCommandGate owner, string[] keys) : IDisposable
    {
        private RuntimeCommandGate? owner = owner;
        public void Dispose()
        {
            var target = Interlocked.Exchange(ref owner, null);
            if (target is null) return;
            lock (target.sync) foreach (var key in keys) target.held.Remove(key);
        }
    }
}
