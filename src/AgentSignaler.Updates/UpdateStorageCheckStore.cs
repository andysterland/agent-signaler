using System.Runtime.Versioning;

namespace AgentSignaler.Updates;

[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateCheckStore(UpdatePackage package, string installationDirectory) : IUpdateCheckStore
{
    private UpdateStorage? active;
    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(package)) throw new ArgumentOutOfRangeException(nameof(package));
        if (active is not null) throw new InvalidOperationException("update_cache_lease_active");
        var storage = new UpdateStorage(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), installationDirectory);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(15));
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                try
                {
                    var lease = storage.AcquireCheckLease();
                    active = storage;
                    return new CheckLease(this, storage, lease);
                }
                catch (IOException) { await Task.Delay(100, budget.Token).ConfigureAwait(false); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            storage.Dispose();
            throw new IOException("update_check_lease_timeout");
        }
        catch { storage.Dispose(); throw; }
    }

    public Task<UpdateCheckCache?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = (active ?? throw new InvalidOperationException("update_cache_lease_required")).Read("check.json");
        return Task.FromResult(bytes is null ? null : UpdateCheckCacheCodec.Read(bytes));
    }

    public Task WriteAsync(UpdateCheckCache cache, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (active ?? throw new InvalidOperationException("update_cache_lease_required")).Write("check.json", UpdateCheckCacheCodec.Write(cache));
        return Task.CompletedTask;
    }

    private sealed class CheckLease(WindowsUpdateCheckStore owner, UpdateStorage storage, IDisposable lease) : IAsyncDisposable
    {
        private int disposed;
        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
            owner.active = null;
            lease.Dispose();
            storage.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}