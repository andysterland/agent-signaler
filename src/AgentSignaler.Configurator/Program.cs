using AgentSignaler.Remote;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using AgentSignaler.AppUpdates;
using AgentSignaler.Updates;
using System.Runtime.InteropServices;

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
        try
        {
            using var updates = AppUpdateHost.Open(UpdatePackage.Remote);
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(updates);
            });
            return 0;
        }
        catch (UpdateStartupRefusedException error)
        {
            ShowStartupError(0, error.UserMessage, "Agent Signaler Remote setup", 0x10);
            return 1;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or COMException)
        {
            ShowStartupError(0, "Remote setup cannot start while this installation is being serviced or its maintenance state cannot be verified. " +
                "Wait for the update to finish, or use the updater's recovery instructions before retrying.", "Agent Signaler Remote setup", 0x10);
            return 1;
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int ShowStartupError(nint window, string text, string title, uint type);
}
