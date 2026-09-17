using System.Text.Json;
using AgentSignaler.Contracts.Rpc.V1;

namespace AgentSignaler.RpcHost;

internal interface IRpcApplication
{
    Guid HostInstanceId { get; }
    event EventHandler<RpcPublication>? Published;
    Task<RpcExecutionResult> ExecuteAsync(string method, JsonElement parameters, CancellationToken cancellationToken);
    void RequestShutdown();
    void AcceptShutdown(Task acknowledgement);
    void ReportDrainTimeout();
    void ReportDrainCompleted();
}

internal sealed record RpcExecutionResult(object? State, string CommitState = "notCommitted");
internal sealed record RpcPublication(string Method, object State, RpcInvalidation? Invalidation = null);

internal static class RpcMethods
{
    public static readonly string[] All =
    [
        "system.getCapabilities", "system.getStatus", "system.cancelStartup", "system.shutdown",
        "settings.get", "settings.update", "machines.list", "machines.get", "machines.getSessions",
        "machines.getNote", "machines.updateDetails", "machines.remove", "receiver.getStatus",
        "sharing.getStatus", "sharing.start", "sharing.stop", "sharing.delete", "sharing.logout",
        "sharing.cancel", "prerequisites.getStatus", "prerequisites.check", "prerequisites.checkAll",
        "prerequisites.cancel", "devboxes.getCatalog", "devboxes.refresh", "devboxes.cancel",
        "windowsApp.getState", "windowsApp.map", "windowsApp.signIn", "windowsApp.refresh",
        "windowsApp.open", "windowsApp.openLastKnown", "windowsApp.clear", "windowsApp.cancel",
        "operations.cancel"
    ];
    public static bool IsControl(string method) => method is "operations.cancel" or "system.shutdown" or
        "system.cancelStartup" or "sharing.stop" or "sharing.cancel" or "prerequisites.cancel" or
        "devboxes.cancel" or "windowsApp.cancel";

    public static TimeSpan Deadline(string method) => method switch
    {
        "windowsApp.signIn" => TimeSpan.FromMinutes(3),
        "devboxes.refresh" or "windowsApp.map" or "windowsApp.refresh" or "windowsApp.open" or
        "sharing.start" or "sharing.delete" or "sharing.logout" or
        "prerequisites.check" or "prerequisites.checkAll" => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromSeconds(10)
    };
}
