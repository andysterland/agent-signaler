namespace AgentSignaler.Remote;

public interface IIntegrationInstallerActivity
{
    bool IsActive();
}

internal sealed class WindowsIntegrationInstallerActivity : IIntegrationInstallerActivity
{
    public bool IsActive()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Windows Installer activity cannot be checked on this platform.");
        return IsMutexActive(@"Global\_MSIExecute");
    }

    // The execution mutex does not cover installer UI sequences. Explicit user confirmation is still required.
    internal static bool IsMutexActive(string name)
    {
        if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
        using (mutex)
        {
            try
            {
                if (!mutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException)
            {
                mutex.ReleaseMutex();
                throw new InvalidOperationException(
                    "Windows Installer execution ended unexpectedly. Resolve installer recovery before finalizing uninstall.");
            }
            mutex.ReleaseMutex();
            return false;
        }
    }
}
