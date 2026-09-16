namespace AgentSignaler.Remote;

public static class ClientLifetime
{
    public static async Task<string?> WaitForExitAsync(ClientCoordinator coordinator,
        CancellationToken cancellationToken = default)
    {
        // Observe terminal state without swallowing its fault: Completion remains faulted,
        // while the host must still close its UI and release ownership.
        var completion = await Task.WhenAny(coordinator.Completion).WaitAsync(cancellationToken);
        _ = completion.Exception;
        // Normally the IPC listener flushes its stop reply first. A disconnected caller must
        // never prevent the host from disposing its icon, IPC, transport, and final owner lock.
        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        return coordinator.TerminalReason;
    }
}
