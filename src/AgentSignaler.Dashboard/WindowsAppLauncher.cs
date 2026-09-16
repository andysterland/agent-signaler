using System.Collections.Concurrent;
using System.Security;
using System.Text.Json;
using AgentSignaler.Service;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Dashboard;

internal sealed class WindowsAppOperationGate
{
    public static WindowsAppOperationGate Shared { get; } = new();
    private readonly ConcurrentDictionary<Guid, byte> active = new();
    public bool IsBusy(Guid machineId) => active.ContainsKey(machineId);

    public IDisposable Enter(Guid machineId)
    {
        if (!active.TryAdd(machineId, 0))
            throw new WindowsAppConnectionException(WindowsAppFailure.Busy);
        return new Lease(this, machineId);
    }

    private sealed class Lease(WindowsAppOperationGate owner, Guid machineId) : IDisposable
    {
        private WindowsAppOperationGate? owner = owner;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.active.TryRemove(machineId, out _);
    }
}

internal sealed record WindowsAppLaunchResult(
    WindowsAppConnection Mapping, WindowsAppActivationDisposition Disposition)
{
    public override string ToString() => Disposition.ToString();
}

internal sealed class WindowsAppLauncher(
    IDevBoxConnectionResolver resolver,
    IWindowsAppPlatform platform,
    Func<Guid, WindowsAppConnection, CancellationToken, Task> persist,
    WindowsAppOperationGate? gate = null)
{
    private readonly WindowsAppOperationGate gate = gate ?? WindowsAppOperationGate.Shared;

    public WindowsAppLauncher(MachineStore store)
        : this(new DevBoxConnectionResolver(new AzureCliProcess()), new WindowsAppPlatform(), store.SetWindowsAppConnectionAsync) { }

    public Task<WindowsAppLaunchResult> OpenAsync(
        Guid machineId, WindowsAppConnection? mapping, CancellationToken cancellationToken = default)
        => OpenCurrentAsync(machineId, _ => Task.FromResult(mapping), cancellationToken);

    public async Task<WindowsAppLaunchResult> OpenCurrentAsync(
        Guid machineId, Func<CancellationToken, Task<WindowsAppConnection?>> read,
        CancellationToken cancellationToken = default)
    {
        using var operation = gate.Enter(machineId);
        cancellationToken.ThrowIfCancellationRequested();
        var mapping = await read(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsAppConnectionValidator.ValidateMapping(mapping);
        if (platform.TryActivateExisting(mapping!.DevBoxName) == WindowsAppActivationDisposition.ExistingWindowActivated)
            return new(mapping, WindowsAppActivationDisposition.ExistingWindowActivated);
        cancellationToken.ThrowIfCancellationRequested();
        RequireProtocol();
        ResolvedDevBoxConnection resolved;
        try { resolved = await resolver.ResolveAsync(mapping!, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new OperationCanceledException("The connection operation was cancelled.", cancellationToken); }
        cancellationToken.ThrowIfCancellationRequested();
        if (resolved.AzureTenantId != mapping!.AzureTenantId ||
            !string.Equals(resolved.AzureAccountUpn, mapping.AzureAccountUpn, StringComparison.OrdinalIgnoreCase) ||
            resolved.SubscriptionId == Guid.Empty)
            throw new WindowsAppConnectionException(WindowsAppFailure.AccountMismatch);
        var uri = WindowsAppConnectionValidator.Validate(resolved.ConnectionUri.OriginalString, mapping.AzureAccountUpn);
        var refreshed = mapping with
        {
            AzureAccountUpn = resolved.AzureAccountUpn,
            AzureTenantId = resolved.AzureTenantId,
            LastKnownConnectionUri = uri.OriginalString,
            ConnectionUriRetrievedAtUtc = resolved.RetrievedAtUtc
        };
        WindowsAppConnectionValidator.ValidateMapping(refreshed);
        try { await persist(machineId, refreshed, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new OperationCanceledException("The connection operation was cancelled.", cancellationToken); }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            throw new WindowsAppConnectionException(WindowsAppFailure.PersistenceFailed);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(refreshed, platform.Activate(uri, mapping.DevBoxName));
    }

    public Task<WindowsAppActivationDisposition> OpenLastKnownAsync(
        Guid machineId, WindowsAppConnection? mapping, CancellationToken cancellationToken = default)
        => OpenCurrentLastKnownAsync(machineId, _ => Task.FromResult(mapping), cancellationToken);

    public async Task<WindowsAppActivationDisposition> OpenCurrentLastKnownAsync(
        Guid machineId, Func<CancellationToken, Task<WindowsAppConnection?>> read,
        CancellationToken cancellationToken = default)
    {
        using var operation = gate.Enter(machineId);
        cancellationToken.ThrowIfCancellationRequested();
        var mapping = await read(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        WindowsAppConnectionValidator.ValidateMapping(mapping);
        if (mapping!.LastKnownConnectionUri is null)
            throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping);
        var uri = WindowsAppConnectionValidator.Validate(mapping.LastKnownConnectionUri, mapping.AzureAccountUpn);
        if (platform.TryActivateExisting(mapping.DevBoxName) == WindowsAppActivationDisposition.ExistingWindowActivated)
            return WindowsAppActivationDisposition.ExistingWindowActivated;
        cancellationToken.ThrowIfCancellationRequested();
        RequireProtocol();
        cancellationToken.ThrowIfCancellationRequested();
        return platform.Activate(uri, mapping.DevBoxName);
    }

    private void RequireProtocol()
    {
        if (!platform.IsProtocolAvailable())
            throw new WindowsAppConnectionException(WindowsAppFailure.ProtocolMissing);
    }
}
