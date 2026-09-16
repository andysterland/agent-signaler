using AgentSignaler.Remote;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentSignaler.Configurator;

internal static class Program
{
    private static string? requestedConfig;
    internal static string ConfigPath => requestedConfig ?? RemotePaths.DefaultConfig;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--config", var path])
            {
                requestedConfig = IntegrationStartup.CanonicalConfigPath(path);
                args = [];
            }
            _ = ConfigPath;
        }
        catch (Exception ex) when (RemoteFailure.IsExpected(ex)) { return 2; }
        if (args.Length != 0)
        {
            if (args is not ["--uninstall-integration"]) return 2;
            try
            {
                new IntegrationManager(new WindowsTaskScheduler(), (_, _) => Task.FromResult(false),
                    new WindowsIntegrationStartup(), new ClientIntegrationRuntime())
                    .Uninstall(ConfigPath);
                return 0;
            }
            catch (Exception ex) when (RemoteFailure.IsExpected(ex))
            {
                new DiagnosticLog(RemotePaths.Log(ConfigPath)).Write("integration-uninstall-failed");
                return 1;
            }
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
