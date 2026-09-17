using System.Security;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentSignaler.Dashboard;

// One startup attempt, including the final public-endpoint verification.
// A failed attempt releases the view for recovery; window exit never reveals it.
internal sealed class DashboardStartupController
{
    public bool IsLoading { get; private set; } = true;
    public bool ShowMachines { get; private set; }

    public static string? ProgressMessage(string stage, bool startSharing) => stage switch
    {
        "storage" => "Opening local machine history...",
        "receiver" => "Starting the local receiver...",
        "machines" => "Loading computer tiles...",
        "sharing" when startSharing => "Starting the shared public endpoint...",
        "sharing" => "Preparing Internet sharing...",
        "startupCancelled" => "Startup cancelled.",
        "startupTimeout" => "Startup timed out.",
        _ => null
    };

    public static async Task<bool> DrainForShutdownAsync(IEnumerable<Task> pending, Func<Task<bool>> shutdown)
    {
        var stopping = shutdown();
        var clean = true;
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception error) when (error is RuntimeCommandException or SqliteException or JsonException or
            IOException or UnauthorizedAccessException or SecurityException or OperationCanceledException or TimeoutException)
        { clean = false; }
        finally { clean &= await stopping; }
        return clean;
    }

    public async Task RunAsync(Func<CancellationToken, Task> initialize, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await initialize(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Window-owned cancellation is normal shutdown, not a startup error.
        }
        finally
        {
            IsLoading = false;
            ShowMachines = !cancellationToken.IsCancellationRequested;
        }
    }
}
