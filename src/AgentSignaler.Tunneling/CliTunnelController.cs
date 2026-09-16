using System.ComponentModel;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentSignaler.Tunneling;

/// <summary>Dashboard-owned CLI hosting. Startup uses the existing CLI account and never invokes login.</summary>
public sealed class CliTunnelController : IAsyncDisposable
{
    private readonly TunnelOptions options;
    private readonly Func<TunnelIdentity, CancellationToken, Task> persist;
    private readonly ITunnelProcessRunner runner;
    private readonly ITunnelHealthProbe probe;
    private readonly bool ownsProbe;
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly object sync = new();
    private CancellationTokenSource? activeStart;
    private ITunnelHostProcess? host;
    private CancellationTokenSource? hostLifetime;
    private Task monitoring = Task.CompletedTask;
    private long generation;
    private int receiverPort;
    private bool disposed;
    private TunnelIdentity identity;
    private TunnelStatus status = new(TunnelState.Stopped, "Internet sharing is stopped.");

    /// <param name="persistIdentity">
    /// Required atomic, bounded durable write. It must finish before returning and must not
    /// reenter this controller. Returned resource IDs are persisted even during cancellation.
    /// </param>
    public CliTunnelController(TunnelOptions options, Func<TunnelIdentity, CancellationToken, Task> persistIdentity)
        : this(options, persistIdentity, new WindowsTunnelProcessRunner(), new AnonymousHealthProbe())
        => ownsProbe = true;

    public CliTunnelController(TunnelOptions options, Func<TunnelIdentity, CancellationToken, Task> persistIdentity,
        ITunnelProcessRunner runner, ITunnelHealthProbe probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(persistIdentity);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(probe);
        if (options.Port is < 1 or > 65535 || options.CommandTimeout <= TimeSpan.Zero ||
            options.CommandTimeout > TimeSpan.FromMinutes(2) || options.StartupTimeout <= TimeSpan.Zero ||
            options.StartupTimeout > TimeSpan.FromMinutes(3) ||
            options.HealthCheckInterval < TimeSpan.FromMilliseconds(50) ||
            options.HealthCheckInterval > TimeSpan.FromMinutes(5) ||
            options.HealthCheckTimeout < TimeSpan.FromMilliseconds(50) ||
            options.HealthCheckTimeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (!Guid.TryParseExact(options.Identity.InstallationMarker, "N", out var marker) || marker == Guid.Empty)
            throw new ArgumentException("InstallationMarker must be a persisted GUID in N format.", nameof(options));
        if (options.Identity.TunnelId is { } id) TunnelValidation.ValidateId(id);
        if (options.Identity.PendingTunnelId is { } pending) TunnelValidation.ValidateId(pending, full: false);
        this.options = options;
        receiverPort = options.Port;
        persist = persistIdentity;
        this.runner = runner;
        this.probe = probe;
        identity = options.Identity;
    }

    public event EventHandler<TunnelStatus>? StatusChanged;
    public static string WinGetCliPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WinGet", "Links", "devtunnel.exe");
    public static string PrerequisiteCliPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "Microsoft Dev Tunnels CLI", "devtunnel.exe");
    public static string DefaultCliPath => DiscoverCliPath(null, File.Exists);

    public static string DiscoverCliPath(string? explicitPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
        if (fileExists(WinGetCliPath)) return WinGetCliPath;
        return fileExists(PrerequisiteCliPath) ? PrerequisiteCliPath : WinGetCliPath;
    }
    public TunnelStatus Status => Volatile.Read(ref status);
    public TunnelStatus Snapshot => Status;
    public TunnelIdentity Identity => Volatile.Read(ref identity);
    public bool SupportsAutomaticResume => true;
    public Task MonitoringCompletion => Volatile.Read(ref monitoring);

    public Task StartAsync(CancellationToken cancellationToken = default) =>
        StartCoreAsync(null, cancellationToken);

    public Task StartAsync(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return StartCoreAsync(port, cancellationToken);
    }

    private async Task StartCoreAsync(int? port, CancellationToken cancellationToken)
    {
        var requestedGeneration = Interlocked.Read(ref generation);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenSource? start = null;
        var keepHost = false;
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (requestedGeneration != generation) return;
                if (host is not null && !host.Completion.IsCompleted &&
                    Status.State is TunnelState.Connected or TunnelState.Reconnecting or TunnelState.Verifying &&
                    (port is null || port == receiverPort)) { keepHost = true; return; }
                if (port is { } requestedPort) receiverPort = requestedPort;
                start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                start.CancelAfter(options.StartupTimeout);
                activeStart = start;
            }
            await StopHostAsync().ConfigureAwait(false);
            var token = start.Token;
            SetStatus(TunnelState.CheckingAccount, "Checking the installed CLI and signed-in account.");
            await CheckAccountCoreAsync(token).ConfigureAwait(false);
            await probe.VerifyAsync(new Uri($"http://127.0.0.1:{receiverPort}/"), token).ConfigureAwait(false);
            var pending = Identity.PendingTunnelId is not null;
            JsonDocument? tunnel = null;
            var recreated = false;
            if (Identity.TunnelId is { } existing)
            {
                tunnel = await LookupAsync(existing, token).ConfigureAwait(false);
                recreated = tunnel is null;
            }
            else if (Identity.PendingTunnelId is { } intent)
                tunnel = await LookupAsync(intent, token).ConfigureAwait(false);
            if (tunnel is null)
            {
                SetStatus(TunnelState.Creating, recreated
                    ? "The previous tunnel is confirmed absent. Creating a replacement; update remote settings with its new URL."
                    : "Creating a private application-owned tunnel.");
                var intentId = Identity.PendingTunnelId ?? $"agentsignaler-{Guid.NewGuid():N}";
                await SaveAsync(Identity with { TunnelId = null, PendingTunnelId = intentId }, token).ConfigureAwait(false);
                pending = true;
                tunnel = await JsonCommandAsync(token, "create", intentId, "--description", Description,
                    "--expiration", "1d", "--json").ConfigureAwait(false);
            }
            using (tunnel)
            {
                var resource = TunnelValidation.Property(tunnel.RootElement, "tunnel", JsonValueKind.Object);
                var fullId = VerifyOwnedTunnel(resource);
                if (Identity.TunnelId != fullId)
                    await SaveAsync(Identity with { TunnelId = fullId }, CancellationToken.None).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            var id = Identity.TunnelId!;
            await ConfigureAndVerifyAsync(id, pending, token).ConfigureAwait(false);
            if (pending)
                await SaveAsync(Identity with { PendingTunnelId = null }, token).ConfigureAwait(false);

            SetStatus(TunnelState.Starting, "Starting the owned relay host.");
            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            var outputFailure = new TaskCompletionSource<TunnelException>(TaskCreationOptions.RunContinuationsAsynchronously);
            var parser = new TunnelHostOutputParser(id, receiverPort);
            var outputSync = new object();
            void OnLine(string line)
            {
                lock (outputSync)
                {
                    try
                    {
                        if (parser.Feed(line) is { } candidate) ready.TrySetResult(candidate);
                    }
                    catch (TunnelException exception)
                    {
                        ready.TrySetException(exception);
                        outputFailure.TrySetResult(exception);
                    }
                }
            }
            host = await runner.StartHostAsync(options.CliPath, ["host", id], OnLine, token).ConfigureAwait(false);
            var currentHost = host;
            var first = await Task.WhenAny(ready.Task, currentHost.Completion).WaitAsync(token).ConfigureAwait(false);
            if (first == currentHost.Completion) throw new TunnelException("The relay host exited before becoming ready.");
            var uri = await ready.Task.ConfigureAwait(false);
            SetStatus(TunnelState.Verifying, "Relay ready; verifying anonymous public HTTPS health.");
            await probe.VerifyAsync(uri, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (currentHost.Completion.IsCompleted) throw new TunnelException("The relay host exited during verification.");
            if (outputFailure.Task.IsCompleted) throw await outputFailure.Task.ConfigureAwait(false);
            lock (sync)
            {
                token.ThrowIfCancellationRequested();
                if (requestedGeneration != generation) throw new OperationCanceledException(token);
                SetStatus(TunnelState.Connected, recreated
                    ? "Replacement tunnel verified. Update remote settings with this new public URL."
                    : "Anonymous public HTTPS health verified.", uri);
            }
            hostLifetime = new CancellationTokenSource();
            monitoring = MonitorHostAsync(currentHost, requestedGeneration, uri, outputFailure.Task, hostLifetime.Token);
            keepHost = true;
        }
        catch (OperationCanceledException)
        {
            await StopHostAsync().ConfigureAwait(false);
            SetStatus(TunnelState.Stopped, "Sharing startup was cancelled or timed out; saved identity was retained.");
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            var failedStage = Status.State;
            await StopHostAsync().ConfigureAwait(false);
            if (failedStage == TunnelState.Verifying && exception is HttpRequestException or TimeoutException)
                SetStatus(TunnelState.Faulted, "The relay became ready, but anonymous public HTTPS verification failed. The owned host was stopped; retry explicitly.");
            else Fail(exception);
        }
        finally
        {
            try
            {
                if (!keepHost)
                {
                    await StopHostAsync().ConfigureAwait(false);
                    if (Status.State is not (TunnelState.Stopped or TunnelState.Faulted or TunnelState.Unsupported or TunnelState.AccountRequired))
                        SetStatus(TunnelState.Faulted, "Sharing startup did not complete. The owned host has been stopped.");
                }
            }
            finally
            {
                lock (sync) { if (ReferenceEquals(activeStart, start)) activeStart = null; }
                start?.Dispose();
                operations.Release();
            }
        }
    }

    public async Task<TunnelStatus> CheckAccountAsync(CancellationToken cancellationToken = default)
    {
        var requestedGeneration = Interlocked.Read(ref generation);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (requestedGeneration != generation) return Status;
                activeStart = operation;
            }
            if (host is null) SetStatus(TunnelState.CheckingAccount, "Checking the installed CLI and signed-in Microsoft account.");
            await CheckAccountCoreAsync(operation.Token).ConfigureAwait(false);
            if (host is null) SetStatus(TunnelState.Stopped, "Microsoft CLI account is ready. Sharing starts only when explicitly requested.");
        }
        catch (OperationCanceledException)
        {
            if (host is null) SetStatus(TunnelState.Stopped, "Account check cancelled.");
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            await StopHostAsync().ConfigureAwait(false);
            Fail(exception);
        }
        finally
        {
            lock (sync) { if (ReferenceEquals(activeStart, operation)) activeStart = null; }
            operations.Release();
        }
        return Status;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancelStart();
        // Stopping must still terminate the owned host when the caller's token is cancelled.
        await operations.WaitAsync().ConfigureAwait(false);
        Task monitor;
        try
        {
            monitor = monitoring;
            try { SetStatus(TunnelState.Stopping, "Stopping the owned relay host."); }
            finally { await StopHostAsync().ConfigureAwait(false); }
            SetStatus(TunnelState.Stopped, "Internet sharing is stopped; the tunnel identity is retained.");
        }
        finally { operations.Release(); }
        await monitor.ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        var requestedGeneration = Interlocked.Read(ref generation);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (requestedGeneration != generation) return;
                activeStart = operation;
            }
            var token = operation.Token;
            await StopHostAsync().ConfigureAwait(false);
            await CheckAccountCoreAsync(token).ConfigureAwait(false);
            var id = Identity.TunnelId ?? Identity.PendingTunnelId;
            if (id is null) return;
            using var before = await LookupAsync(id, token).ConfigureAwait(false);
            if (before is not null)
            {
                var fullId = VerifyOwnedTunnel(TunnelValidation.Property(before.RootElement, "tunnel", JsonValueKind.Object));
                if (Identity.TunnelId != fullId)
                    await SaveAsync(Identity with { TunnelId = fullId }, CancellationToken.None).ConfigureAwait(false);
                using var deleted = await JsonCommandAsync(token, "delete", fullId, "--force", "--json").ConfigureAwait(false);
                if (TunnelValidation.Text(deleted.RootElement, "deletedTunnel") != fullId)
                    throw new TunnelException("The CLI did not confirm deletion of the requested tunnel. Identity retained.");
                using var after = await LookupAsync(fullId, token).ConfigureAwait(false);
                if (after is not null)
                    throw new TunnelException("The tunnel is still present after deletion. Identity retained; retry explicitly.");
            }
            await SaveAsync(Identity with { TunnelId = null, PendingTunnelId = null }, token).ConfigureAwait(false);
            SetStatus(TunnelState.Stopped, "Tunnel absence confirmed. Saved resource identity cleared.");
        }
        catch (OperationCanceledException) { SetStatus(TunnelState.Stopped, "Deletion cancelled; identity retained."); }
        catch (Exception exception) when (IsOperational(exception)) { Fail(exception); }
        finally
        {
            lock (sync) { if (ReferenceEquals(activeStart, operation)) activeStart = null; }
            operations.Release();
        }
    }

    /// <summary>Call only after explicit confirmation: CLI credentials may be shared with other applications.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync().ConfigureAwait(false);
        var requestedGeneration = Interlocked.Read(ref generation);
        await operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (requestedGeneration != generation) return;
                activeStart = operation;
            }
            await StopHostAsync().ConfigureAwait(false);
            await CheckVersionAsync(operation.Token).ConfigureAwait(false);
            await CommandAsync(operation.Token, "user", "logout").ConfigureAwait(false);
            SetStatus(TunnelState.AccountRequired, "CLI logout completed. Browser cookies are unchanged; tunnel identity is retained.");
        }
        catch (OperationCanceledException) { SetStatus(TunnelState.Stopped, "Logout cancelled; hosting is stopped."); }
        catch (Exception exception) when (IsOperational(exception)) { Fail(exception); }
        finally
        {
            lock (sync) { if (ReferenceEquals(activeStart, operation)) activeStart = null; }
            operations.Release();
        }
    }

    private string Description => $"AgentSignaler owner:{Identity.InstallationMarker}";

    private Task CheckVersionAsync(CancellationToken token) =>
        CliPrerequisiteChecks.CheckVersionAsync(runner, options.CliPath, options.CommandTimeout, token);

    private async Task CheckAccountCoreAsync(CancellationToken token)
    {
        await CheckVersionAsync(token).ConfigureAwait(false);
        var owner = await CliPrerequisiteChecks.CheckAccountAsync(runner, options.CliPath, options.CommandTimeout, token).ConfigureAwait(false);
        if (!string.Equals(Identity.OwnerHash, owner, StringComparison.Ordinal))
        {
            if (Identity.TunnelId is not null || Identity.PendingTunnelId is not null)
                throw new TunnelException("The signed-in account does not own this dashboard's saved tunnel. Sign in to the original account.");
            await SaveAsync(Identity with { OwnerHash = owner }, token).ConfigureAwait(false);
        }
    }

    private string VerifyOwnedTunnel(JsonElement tunnel)
    {
        var id = TunnelValidation.Text(tunnel, "tunnelId");
        TunnelValidation.ValidateId(id);
        if ((Identity.TunnelId is not null && id != Identity.TunnelId) ||
            (Identity.PendingTunnelId is not null && !id.StartsWith(Identity.PendingTunnelId + ".", StringComparison.Ordinal)) ||
            TunnelValidation.Text(tunnel, "description") != Description)
            throw new TunnelException("Tunnel ownership mismatch; the resource will not be changed or hosted.");
        if (TunnelValidation.Number(tunnel, "hostConnections") != 0)
            throw new TunnelException("The service still reports a connected host. If you just stopped sharing, wait briefly and retry. " +
                "Otherwise stop the other host explicitly; this dashboard will not evict it.");
        TunnelValidation.NoTunnelAcl(TunnelValidation.Property(tunnel, "accessControl", JsonValueKind.Array));
        return id;
    }

    private async Task ConfigureAndVerifyAsync(string id, bool pending, CancellationToken token)
    {
        using var show = await JsonCommandAsync(token, "show", id, "--json").ConfigureAwait(false);
        var tunnel = TunnelValidation.Property(show.RootElement, "tunnel", JsonValueKind.Object);
        VerifyOwnedTunnel(tunnel);
        var hasPorts = tunnel.TryGetProperty("ports", out var ports);
        if (!hasPorts && !pending)
            throw new TunnelException("The saved tunnel no longer has its receiver port; no changes were made.");
        var portCount = hasPorts ? TunnelValidation.Property(tunnel, "ports", JsonValueKind.Array).GetArrayLength() : 0;
        using var tunnelAcl = await JsonCommandAsync(token, "access", "list", id, "--json").ConfigureAwait(false);
        TunnelValidation.NoTunnelAcl(TunnelValidation.Property(tunnelAcl.RootElement, "accessControlEntries", JsonValueKind.Array));
        if (portCount == 0 && pending)
        {
            using var created = await JsonCommandAsync(token, "port", "create", id, "-p", PortText, "--protocol", "http", "--json").ConfigureAwait(false);
            VerifyPort(TunnelValidation.Property(created.RootElement, "port", JsonValueKind.Object), id, requireAcl: false);
        }
        else
        {
            if (portCount != 1) throw new TunnelException("Tunnel port drift detected; exactly one receiver port is required.");
            VerifyPort(ports[0], id, requireAcl: false);
        }
        using var port = await JsonCommandAsync(token, "port", "show", id, "--port-number", PortText, "--json").ConfigureAwait(false);
        var portValue = TunnelValidation.Property(port.RootElement, "port", JsonValueKind.Object);
        VerifyPort(portValue, id, requireAcl: false);
        var access = TunnelValidation.Property(portValue, "accessControl", JsonValueKind.Array);
        using var portAcl = await JsonCommandAsync(token, "access", "list", id, "--port-number", PortText, "--json").ConfigureAwait(false);
        var listed = TunnelValidation.Property(portAcl.RootElement, "accessControlEntries", JsonValueKind.Array);
        if (pending && access.GetArrayLength() == 0 && listed.GetArrayLength() == 0)
        {
            using var grant = await JsonCommandAsync(token, "access", "create", id, "--port-number", PortText,
                "--anonymous", "--scopes", "connect", "--json").ConfigureAwait(false);
            TunnelValidation.PortAcl(TunnelValidation.Property(grant.RootElement, "accessControlEntries", JsonValueKind.Array));
        }
        else
        {
            TunnelValidation.PortAcl(access);
            TunnelValidation.PortAcl(listed);
        }
        using var finalShow = await JsonCommandAsync(token, "show", id, "--json").ConfigureAwait(false);
        var finalTunnel = TunnelValidation.Property(finalShow.RootElement, "tunnel", JsonValueKind.Object);
        VerifyOwnedTunnel(finalTunnel);
        var finalPorts = TunnelValidation.Property(finalTunnel, "ports", JsonValueKind.Array);
        if (finalPorts.GetArrayLength() != 1) throw new TunnelException("Tunnel port drift detected before hosting.");
        VerifyPort(finalPorts[0], id, requireAcl: false);
        using var finalAcl = await JsonCommandAsync(token, "access", "list", id, "--json").ConfigureAwait(false);
        TunnelValidation.NoTunnelAcl(TunnelValidation.Property(finalAcl.RootElement, "accessControlEntries", JsonValueKind.Array));
        using var finalPort = await JsonCommandAsync(token, "port", "show", id, "--port-number", PortText, "--json").ConfigureAwait(false);
        VerifyPort(TunnelValidation.Property(finalPort.RootElement, "port", JsonValueKind.Object), id, requireAcl: true);
        using var finalPortAcl = await JsonCommandAsync(token, "access", "list", id, "--port-number", PortText, "--json").ConfigureAwait(false);
        TunnelValidation.PortAcl(TunnelValidation.Property(finalPortAcl.RootElement, "accessControlEntries", JsonValueKind.Array));
    }

    private string PortText => receiverPort.ToString(CultureInfo.InvariantCulture);

    private void VerifyPort(JsonElement port, string id, bool requireAcl)
    {
        if (TunnelValidation.Number(port, "portNumber") != receiverPort)
            throw new TunnelException("The saved tunnel uses a different receiver port. Explicitly delete the old tunnel before changing the receiver port, then enable sharing again. No port changes were made.");
        if (TunnelValidation.Text(port, "protocol") != "http" ||
            (port.TryGetProperty("tunnelId", out _) && TunnelValidation.Text(port, "tunnelId") != id))
            throw new TunnelException("Receiver port identity or protocol drift detected.");
        if (requireAcl) TunnelValidation.PortAcl(TunnelValidation.Property(port, "accessControl", JsonValueKind.Array));
    }

    private async Task<CliCommandResult> CommandAsync(CancellationToken token, params string[] args)
    {
        var result = await runner.RunAsync(options.CliPath, args, options.CommandTimeout, token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new TunnelException("The CLI command failed. Check explicit CLI sign-in, connectivity, permissions, and quota, then retry. Saved identity was retained.");
        return result;
    }

    private async Task<JsonDocument> JsonCommandAsync(CancellationToken token, params string[] args)
        => TunnelValidation.Parse((await CommandAsync(token, args).ConfigureAwait(false)).StandardOutput);

    private async Task<JsonDocument?> LookupAsync(string id, CancellationToken token)
    {
        var result = await runner.RunAsync(options.CliPath, ["show", id, "--json"],
            options.CommandTimeout, token).ConfigureAwait(false);
        if (TunnelValidation.IsConfirmedAbsent(result, id)) return null;
        if (result.ExitCode != 0)
            throw new TunnelException("Tunnel lookup failed without confirmed absence. Check CLI sign-in, connectivity, and permissions; saved identity was retained.");
        return TunnelValidation.Parse(result.StandardOutput);
    }

    private async Task SaveAsync(TunnelIdentity value, CancellationToken token)
    {
        try { await persist(value, token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            throw new TunnelException("Cannot persist tunnel ownership state. Sharing is blocked; recover the saved pending intent before retrying.");
        }
        Volatile.Write(ref identity, value);
    }

    private void CancelStart()
    {
        lock (sync)
        {
            Interlocked.Increment(ref generation);
            activeStart?.Cancel();
        }
    }

    private async Task StopHostAsync()
    {
        var previous = host;
        host = null;
        var lifetime = hostLifetime;
        hostLifetime = null;
        try
        {
            lifetime?.Cancel();
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
        }
        finally { lifetime?.Dispose(); }
    }

    private async Task MonitorHostAsync(ITunnelHostProcess process, long expectedGeneration, Uri uri,
        Task<TunnelException> outputFailure, CancellationToken token)
    {
        Exception? failure = null;
        var completedNormally = false;
        try
        {
            while (true)
            {
                var nextCheck = Task.Delay(options.HealthCheckInterval, token);
                var signal = await Task.WhenAny(process.Completion, outputFailure, nextCheck).WaitAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (signal == outputFailure) throw await outputFailure.ConfigureAwait(false);
                if (signal == process.Completion)
                {
                    await process.Completion.ConfigureAwait(false);
                    throw new TunnelException("The relay host stopped. The public URL is offline; retry explicitly.");
                }
                if (Status.State != TunnelState.Connected)
                    await UpdateMonitorStatusAsync(process, expectedGeneration, TunnelState.Verifying,
                        "Rechecking anonymous HTTPS health on the existing relay host.", null, token).ConfigureAwait(false);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(options.HealthCheckTimeout);
                    await probe.VerifyAsync(uri, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    await UpdateMonitorStatusAsync(process, expectedGeneration, TunnelState.Reconnecting,
                        "Public health verification timed out. Waiting to reverify the existing relay; no competing host will start.", null, token).ConfigureAwait(false);
                    continue;
                }
                catch (Exception exception) when (IsOperational(exception))
                {
                    await UpdateMonitorStatusAsync(process, expectedGeneration, TunnelState.Reconnecting,
                        "Public health verification failed. Waiting to reverify the existing relay; no competing host will start.", null, token).ConfigureAwait(false);
                    continue;
                }
                if (process.Completion.IsCompleted) throw new TunnelException("The relay host exited during health verification.");
                if (outputFailure.IsCompleted) throw await outputFailure.ConfigureAwait(false);
                await UpdateMonitorStatusAsync(process, expectedGeneration, TunnelState.Connected,
                    "Anonymous public HTTPS health verified.", uri, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { completedNormally = true; }
        catch (Exception exception) when (IsOperational(exception)) { failure = exception; completedNormally = true; }
        finally
        {
            if (failure is not null || !completedNormally)
            {
                await operations.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (ReferenceEquals(host, process) && Interlocked.Read(ref generation) == expectedGeneration)
                    {
                        try { await StopHostAsync().ConfigureAwait(false); }
                        catch (Exception exception) when (IsOperational(exception)) { failure = exception; }
                        if (failure is not null) Fail(failure);
                        else SetStatus(TunnelState.Faulted, "Unexpected relay-monitor failure. Sharing is stopped; inspect the failed monitoring task.");
                    }
                }
                finally { operations.Release(); }
            }
        }
    }

    private async Task UpdateMonitorStatusAsync(ITunnelHostProcess process, long expectedGeneration, TunnelState state,
        string message, Uri? uri, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (ReferenceEquals(host, process) && Interlocked.Read(ref generation) == expectedGeneration)
                SetStatus(state, message, uri);
        }
        finally { operations.Release(); }
    }

    private static bool IsOperational(Exception exception) => exception is
        TunnelException or IOException or UnauthorizedAccessException or SecurityException or Win32Exception or
        HttpRequestException or JsonException or CryptographicException or TimeoutException;

    private void Fail(Exception exception) => SetStatus(
        exception is TunnelException tunnel ? tunnel.State : TunnelState.Faulted,
        exception is TunnelException known ? known.Message : "Tunnel operation failed. No credentials or CLI output were logged. Retry explicitly.");

    private void SetStatus(TunnelState state, string message, Uri? uri = null)
    {
        var snapshot = new TunnelStatus(state, message, state == TunnelState.Connected ? uri : null);
        Volatile.Write(ref status, snapshot);
        StatusChanged?.Invoke(this, snapshot);
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        try { await StopAsync().ConfigureAwait(false); }
        finally { if (ownsProbe && probe is IDisposable disposable) disposable.Dispose(); }
    }
}
