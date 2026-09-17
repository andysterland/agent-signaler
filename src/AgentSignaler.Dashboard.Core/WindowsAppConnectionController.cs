using System.Security;
using System.Text.Json;
using AgentSignaler.Service;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Dashboard;

internal enum WindowsAppOperation { Map, SignIn, Refresh, Open, OpenLastKnown, Clear }

internal sealed record WindowsAppConnectionState(
    WindowsAppConnection? Mapping, string Status, string Message, WindowsAppOperation? Operation = null)
{
    public bool IsBusy => Operation is not null;
    public bool FieldsEnabled => !IsBusy;
    public bool ActionsEnabled => !IsBusy;
    public bool CompactLaunchEnabled => !IsBusy;
    public bool CanOpenLastKnown => !IsBusy && Mapping?.LastKnownConnectionUri is not null;
    public string? LastRefresh => Mapping?.ConnectionUriRetrievedAtUtc?.ToLocalTime().ToString("G");
    public override string ToString() => $"{Status}: {Message}";
}

internal sealed record WindowsAppOperationResult(bool Succeeded, string Message, WindowsAppFailure? Failure = null);

// Owns all details/compact work, including its cancellation lifetime. The launcher owns
// the gate for launch operations; other operations acquire that same gate here.
internal sealed class WindowsAppConnectionController(
    Func<Guid, CancellationToken, Task<WindowsAppConnection?>> read,
    Func<Guid, WindowsAppConnection, CancellationToken, Task> persist,
    Func<Guid, CancellationToken, Task> clear,
    IAzureCliProcess cli,
    IDevBoxConnectionResolver resolver,
    WindowsAppLauncher launcher,
    WindowsAppOperationGate gate,
    Func<bool>? catalogBusy = null,
    Action<RuntimeCommitState>? sideEffect = null)
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, WindowsAppConnectionState> states = [];
    private readonly Dictionary<Guid, Pending> pending = [];
    private bool stopping;
    private sealed record Pending(CancellationTokenSource Cancellation, TaskCompletionSource Completion);
    public event Action<Guid>? Changed;
    public static string LaunchLabel(string name) => $"Open {name} in Windows App";

    public WindowsAppConnectionState State(Guid id)
    {
        lock (sync)
            return states.GetValueOrDefault(id) ?? new(null, "Not configured", "");
    }

    public void Observe(Guid id, WindowsAppConnection? mapping)
    {
        lock (sync)
        {
            if (pending.ContainsKey(id)) return;
            if (!states.TryGetValue(id, out var previous))
                states[id] = new(mapping, mapping is null ? "Not configured" : "Ready", "");
            else if (previous.Mapping != mapping)
                states[id] = previous with
                {
                    Mapping = mapping,
                    Status = mapping is null ? "Not configured" : previous.Status == "Not configured" ? "Ready" : previous.Status
                };
        }
    }

    public bool IsBusy(Guid id)
    {
        lock (sync) return pending.ContainsKey(id) || gate.IsBusy(id);
    }

    public Task<WindowsAppOperationResult> OpenWindowsAppAsync(Guid id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(id, WindowsAppOperation.Open, cancellationToken: cancellationToken);

    public Task<WindowsAppOperationResult> ExecuteAsync(Guid id, WindowsAppOperation operation,
        DevBoxMappingSelection? selection = null, CancellationToken cancellationToken = default)
    {
        Pending work;
        lock (sync)
        {
            if (stopping)
                return Task.FromResult(new WindowsAppOperationResult(false, "The runtime is stopping. The connection operation was cancelled."));
            if (id == Guid.Empty || !Enum.IsDefined(operation))
                return FailureResult(WindowsAppFailure.InvalidMapping);
            if (pending.ContainsKey(id) || gate.IsBusy(id)) return FailureResult(WindowsAppFailure.Busy);
            if (operation == WindowsAppOperation.Map && catalogBusy?.Invoke() == true)
                return FailureResult(WindowsAppFailure.Busy);
            work = new(CancellationTokenSource.CreateLinkedTokenSource(cancellationToken), new(TaskCreationOptions.RunContinuationsAsynchronously));
            pending.Add(id, work);
            states[id] = State(id) with { Operation = operation, Message = Progress(operation) };
        }
        Changed?.Invoke(id);
        return RunAsync(id, operation, selection, work);
    }

    private static Task<WindowsAppOperationResult> FailureResult(WindowsAppFailure failure) =>
        Task.FromResult(new WindowsAppOperationResult(false, new WindowsAppConnectionException(failure).Message, failure));

    private async Task<WindowsAppOperationResult> RunAsync(Guid id, WindowsAppOperation operation,
        DevBoxMappingSelection? selection, Pending work)
    {
        var token = work.Cancellation.Token;
        try
        {
            var disposition = WindowsAppActivationDisposition.NoExistingWindow;
            // Installation file access and SQLite may perform synchronous I/O before their
            // first await. Keep those, and process startup, off the WinUI thread.
            var mapping = await Task.Run(async () =>
            {
                WindowsAppConnection? current = null;
                async Task<WindowsAppConnection?> ReadCurrent(CancellationToken ct)
                {
                    current = await read(id, ct).ConfigureAwait(false);
                    SetState(id, State(id) with { Mapping = current });
                    return current;
                }
                if (operation == WindowsAppOperation.Open)
                {
                    var result = await launcher.OpenCurrentAsync(id, ReadCurrent, token,
                        stage => ReportLaunchProgress(id, stage)).ConfigureAwait(false);
                    disposition = result.Disposition;
                    return result.Mapping;
                }
                if (operation == WindowsAppOperation.OpenLastKnown)
                {
                    disposition = await launcher.OpenCurrentLastKnownAsync(id, ReadCurrent, token,
                        stage => ReportLaunchProgress(id, stage)).ConfigureAwait(false);
                    return current;
                }
                using var lease = gate.Enter(id);
                var stored = await ReadCurrent(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (operation == WindowsAppOperation.Clear)
                {
                    await clear(id, token).ConfigureAwait(false);
                    return null;
                }
                if (operation == WindowsAppOperation.SignIn)
                {
                    var tenant = selection is { AzureTenantId: var selectedTenant } && selectedTenant != Guid.Empty
                        ? selectedTenant : stored?.AzureTenantId;
                    var command = AzureCliCommand.Login(tenant);
                    sideEffect?.Invoke(RuntimeCommitState.Unknown);
                    var result = await cli.RunAsync(command, command.Timeout, token).ConfigureAwait(false);
                    if (result.ExitCode == 0) sideEffect?.Invoke(RuntimeCommitState.Committed);
                    token.ThrowIfCancellationRequested();
                    if (result.ExitCode != 0)
                        throw new WindowsAppConnectionException(WindowsAppFailure.SignInRequired);
                    if (stored is null)
                    {
                        var account = await AzureAccount.GetAsync(cli, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (tenant is not null && account.Tenant != tenant)
                            throw new WindowsAppConnectionException(WindowsAppFailure.AccountMismatch);
                        return null;
                    }
                }
                var requested = operation == WindowsAppOperation.Map ? ValidateSelection(selection) : stored;
                WindowsAppConnectionValidator.ValidateMapping(requested);
                // Resolve always validates account show before requesting the connection.
                var discovered = await resolver.ResolveAsync(requested!, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (discovered.AzureTenantId != requested!.AzureTenantId || discovered.SubscriptionId == Guid.Empty ||
                    !string.Equals(discovered.AzureAccountUpn, requested.AzureAccountUpn, StringComparison.OrdinalIgnoreCase))
                    throw new WindowsAppConnectionException(WindowsAppFailure.AccountMismatch);
                var refreshed = requested with
                {
                    AzureAccountUpn = discovered.AzureAccountUpn, AzureTenantId = discovered.AzureTenantId,
                    LastKnownConnectionUri = WindowsAppConnectionValidator.Validate(
                        discovered.ConnectionUri.OriginalString, requested.AzureAccountUpn).OriginalString,
                    ConnectionUriRetrievedAtUtc = discovered.RetrievedAtUtc
                };
                WindowsAppConnectionValidator.ValidateMapping(refreshed);
                await persist(id, refreshed, token).ConfigureAwait(false);
                return refreshed;
            }, token).ConfigureAwait(false);
            var message = operation switch
            {
                WindowsAppOperation.Open when disposition == WindowsAppActivationDisposition.ExistingWindowActivated =>
                    "Brought the existing Windows App connection to the foreground.",
                WindowsAppOperation.OpenLastKnown when disposition == WindowsAppActivationDisposition.ExistingWindowActivated =>
                    "Brought the existing Windows App connection to the foreground without refreshing.",
                WindowsAppOperation.Open => "Opened in Windows App.",
                WindowsAppOperation.OpenLastKnown => "Opened the last known connection in Windows App without refreshing.",
                WindowsAppOperation.Clear => "Connection mapping cleared.",
                WindowsAppOperation.SignIn when mapping is null => "Signed in with Azure CLI. Select a Dev Box and save its mapping to continue.",
                _ => "Connection discovered and saved."
            };
            SetState(id, new(mapping, mapping is null ? "Not configured" : "Ready", message));
            return new(true, message);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            const string message = "The connection operation was cancelled.";
            SetState(id, State(id) with { Message = message, Operation = null });
            return new(false, message);
        }
        catch (WindowsAppConnectionException error)
        {
            SetState(id, State(id) with { Status = error.Status, Message = error.Message, Operation = null });
            return new(false, error.Message, error.Failure);
        }
        catch (KeyNotFoundException)
        {
            var failure = new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping);
            SetState(id, State(id) with { Status = failure.Status, Message = failure.Message, Operation = null });
            return new(false, failure.Message, WindowsAppFailure.InvalidMapping);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            var failure = new WindowsAppConnectionException(WindowsAppFailure.PersistenceFailed);
            SetState(id, State(id) with { Status = failure.Status, Message = failure.Message, Operation = null });
            return new(false, failure.Message, WindowsAppFailure.PersistenceFailed);
        }
        finally
        {
            lock (sync)
            {
                pending.Remove(id);
                states[id] = State(id) with { Operation = null };
                work.Cancellation.Dispose();
            }
            work.Completion.TrySetResult();
            Changed?.Invoke(id);
        }
    }

    private void SetState(Guid id, WindowsAppConnectionState state)
    {
        lock (sync) states[id] = state;
    }

    private void ReportLaunchProgress(Guid id, WindowsAppLaunchStage stage)
    {
        var message = stage switch
        {
            WindowsAppLaunchStage.SearchingLocalWindows => "Searching local windows for an existing Windows App connection...",
            WindowsAppLaunchStage.RefreshingConnection => "No matching local window found. Refreshing the Dev Box connection...",
            WindowsAppLaunchStage.Launching => "Launching Windows App...",
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        SetState(id, State(id) with { Message = message });
        Changed?.Invoke(id);
    }

    private static WindowsAppConnection ValidateSelection(DevBoxMappingSelection? selection)
    {
        if (selection?.DevBox is not { } devBox)
            throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping);
        var mapping = new WindowsAppConnection(devBox.DevCenterEndpoint, devBox.ProjectName,
            devBox.DevBoxName, selection.AzureAccountUpn, selection.AzureTenantId, null, null);
        WindowsAppConnectionValidator.ValidateMapping(mapping);
        return mapping;
    }

    public void Cancel(Guid id)
    {
        lock (sync)
            if (pending.TryGetValue(id, out var work)) work.Cancellation.Cancel();
    }

    public Task CancelAndWaitAsync(Guid id)
    {
        lock (sync)
        {
            if (!pending.TryGetValue(id, out var work)) return Task.CompletedTask;
            work.Cancellation.Cancel();
            return work.Completion.Task;
        }
    }

    public Task StopAsync()
    {
        lock (sync)
        {
            stopping = true;
            foreach (var work in pending.Values) work.Cancellation.Cancel();
            return Task.WhenAll(pending.Values.Select(work => work.Completion.Task));
        }
    }

    internal void ForgetExcept(IReadOnlySet<Guid> ids)
    {
        lock (sync)
            foreach (var id in states.Keys.Where(id => !ids.Contains(id) && !pending.ContainsKey(id)).ToArray())
                states.Remove(id);
    }

    private static string Progress(WindowsAppOperation operation) => operation switch
    {
        WindowsAppOperation.Map => "Verifying and saving the selected Dev Box mapping…",
        WindowsAppOperation.SignIn => "Signing in with Azure CLI and verifying the account…",
        WindowsAppOperation.Refresh => "Refreshing and saving the Dev Box connection…",
        WindowsAppOperation.Open => "Reading the saved Dev Box connection...",
        WindowsAppOperation.OpenLastKnown => "Reading the last known Dev Box connection...",
        WindowsAppOperation.Clear => "Clearing the connection mapping…",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}
