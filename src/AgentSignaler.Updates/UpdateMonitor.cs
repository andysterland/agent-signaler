using System.Reflection;
using System.Runtime.Versioning;

namespace AgentSignaler.Updates;

public sealed class UpdateMonitor : IAsyncDisposable
{
    private readonly ReleaseDiscovery discovery;
    private readonly IUpdateCheckStore store;
    private readonly Func<CancellationToken, Task<MsiReleaseVersion>> currentVersion;
    private readonly TimeProvider time;
    private readonly Func<double> jitter;
    private readonly SemaphoreSlim checking = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient? ownedHttp;
    private Task? schedule;
    private int disposed;
    private UpdateCheckResult state = new(UpdateCheckStatus.Unknown);
    public UpdateCheckResult State => Volatile.Read(ref state);
    public event Action<UpdateCheckResult>? Changed;

    public UpdateMonitor(ReleaseDiscovery discovery, IUpdateCheckStore store,
        Func<CancellationToken, Task<MsiReleaseVersion>> currentVersion, TimeProvider? timeProvider = null,
        Func<double>? jitter = null, HttpClient? ownedHttp = null)
    {
        this.discovery = discovery;
        this.store = store;
        this.currentVersion = currentVersion;
        time = timeProvider ?? TimeProvider.System;
        this.jitter = jitter ?? Random.Shared.NextDouble;
        this.ownedHttp = ownedHttp;
    }

    [SupportedOSPlatform("windows")]
    public static UpdateMonitor CreateDefault(UpdatePackage package, string installationDirectory)
    {
        var http = ReleaseDiscovery.CreatePublicHttpClient();
        return new(new ReleaseDiscovery(http), new WindowsUpdateCheckStore(package, installationDirectory),
            async token =>
            {
                token.ThrowIfCancellationRequested();
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var sid = identity.User?.Value ?? throw new UnauthorizedAccessException("update-user-unavailable");
                var inventory = new UpdateInventory(new WindowsUpdateInventoryAdapter(), sid, local);
                var installed = await inventory.FindAsync(package, token).ConfigureAwait(false);
                if (installed is not null && UpdatePath.EqualsDirectory(installed.InstallDirectory, installationDirectory))
                    return new MsiReleaseVersion(installed.Version.Major, installed.Version.Minor, installed.Version.Build);
                var version = Assembly.GetEntryAssembly()?.GetName().Version
                    ?? throw new InvalidDataException("update-current-version-unavailable");
                return new MsiReleaseVersion(version.Major, version.Minor, version.Build);
            }, ownedHttp: http);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (schedule is not null) throw new InvalidOperationException("update-monitor-already-started");
        schedule = RunAsync(lifetime.Token);
    }

    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = linked.Token;
        var requestedAt = time.GetUtcNow();
        await checking.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Publish(State with { Status = UpdateCheckStatus.Checking });
            await using var lease = await store.AcquireAsync(token).ConfigureAwait(false);
            var cache = await store.ReadAsync(token).ConfigureAwait(false);
            var now = time.GetUtcNow();
            if (cache is not null && cache.CheckedAt > now.AddMinutes(1))
                throw new InvalidDataException("update-cache-clock-invalid");
            var version = await Task.Run(() => currentVersion(token), token).ConfigureAwait(false);
            if (cache is not null &&
                (cache.FailureCount > 0 && now < cache.NextCheckAt ||
                 cache.FailureCount == 0 && ((!manual && now - cache.CheckedAt < UpdatePolicy.CheckInterval) || cache.CheckedAt >= requestedAt)))
            {
                Publish(Result(cache, version, fromCache: true));
                return;
            }
            try
            {
                var result = await discovery.DiscoverAsync(cache, token).ConfigureAwait(false);
                now = time.GetUtcNow();
                cache = new(1, result.Candidate, result.ETag, result.SinglePage, now, now + UpdatePolicy.CheckInterval, 0);
            }
            catch (UpdateDiscoveryException failure)
            {
                now = time.GetUtcNow();
                var count = Math.Min(32, (cache?.FailureCount ?? 0) + 1);
                var next = now.AddMinutes(Math.Min(360, 15 * Math.Pow(2, Math.Min(count - 1, 5))));
                if (failure.RetryAt is { } rateLimit && rateLimit > next) next = rateLimit;
                cache = new(1, cache?.Candidate, cache?.ETag, cache?.SinglePage ?? false, now, next, count, failure.Code);
            }
            await store.WriteAsync(cache, token).ConfigureAwait(false);
            Publish(Result(cache, version, fromCache: false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Publish(State with { Status = UpdateCheckStatus.CheckFailed, DiagnosticCode = "update-check-cancelled" });
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            InvalidDataException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Publish(new(UpdateCheckStatus.CheckFailed, CheckedAt: time.GetUtcNow(),
                NextCheckAt: time.GetUtcNow().AddMinutes(15), DiagnosticCode: "update-local-check-failed"));
        }
        finally { checking.Release(); }
    }

    private static UpdateCheckResult Result(UpdateCheckCache cache, MsiReleaseVersion version, bool fromCache) =>
        new(cache.DiagnosticCode is not null
                ? cache.DiagnosticCode == "update-check-incomplete" ? UpdateCheckStatus.CheckIncomplete : UpdateCheckStatus.CheckFailed
                : cache.Candidate is { } candidate && candidate.Version > version ? UpdateCheckStatus.Available : UpdateCheckStatus.UpToDate,
            cache.Candidate, cache.CheckedAt, cache.NextCheckAt, cache.DiagnosticCode, fromCache);

    private void Publish(UpdateCheckResult result)
    {
        Volatile.Write(ref state, result);
        Changed?.Invoke(result);
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), time, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                await CheckAsync(false, token).ConfigureAwait(false);
                var delay = State.NextCheckAt is { } next ? next - time.GetUtcNow() : UpdatePolicy.CheckInterval;
                if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
                if (delay > TimeSpan.FromDays(1)) delay = TimeSpan.FromDays(1);
                var factor = Math.Clamp(jitter(), 0, 1) * 0.1;
                delay += TimeSpan.FromTicks((long)(Math.Min(delay.Ticks, UpdatePolicy.CheckInterval.Ticks) * factor));
                await Task.Delay(delay, time, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (schedule is not null) await schedule.ConfigureAwait(false);
        await checking.WaitAsync().ConfigureAwait(false);
        checking.Release();
        ownedHttp?.Dispose();
        lifetime.Dispose();
    }
}