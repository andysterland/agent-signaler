using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentSignaler.Dashboard;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("--firewall-", StringComparison.Ordinal))
            return Firewall.RunHelper(args);
        if (args.Length > 0 && args[0] is "--uninstall-integration" or "--rollback-uninstall-integration"
            or "--commit-uninstall-integration")
            return StartupRegistration.RunInstallerHelper(args);

        if (args.Length > 1 || args.Length == 1 && args[0] is not ("--background" or "--exit"))
        {
            NativeWindow.ShowError("Unknown command. Start Agent Signaler without arguments.");
            return 2;
        }

        try
        {
            using var instance = new SingleInstance();
            if (!instance.IsPrimary)
            {
                try
                {
                    instance.SendCommand(args.Contains("--exit") ? (byte)2 : (byte)1);
                    return 0;
                }
                catch (Exception e) when (e is IOException or TimeoutException)
                {
                    NativeWindow.ShowError("Agent Signaler is already running but could not show its window. " +
                        "Use Show in its notification-area menu. If necessary, Exit that instance and reopen the app.");
                    return 1;
                }
            }

            if (args.Contains("--exit")) return 0;

            using var resourceLease = DashboardResourceLease.Acquire();
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new App(instance, resourceLease, args.Contains("--background"));
            });
            return 0;
        }
        catch (DashboardOwnershipException)
        {
            NativeWindow.ShowError("Dashboard or RpcHost already owns this data directory. Exit that process before starting Dashboard. " +
                "Older Dashboard versions must be exited and upgraded before sharing state.");
            return 3;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            COMException or Win32Exception or DllNotFoundException or BadImageFormatException or TypeLoadException)
        {
            // This is the process boundary: do not expose paths or machine/event data in errors.
            NativeWindow.ShowError("Agent Signaler could not start. Check that the application is fully extracted, " +
                "that Windows 11 is up to date, and that your local application-data folder is writable.");
            return 1;
        }

    }

}

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _listener;
    public bool IsPrimary { get; }

    public SingleInstance()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("A Windows user identity is required.");
        _pipeName = $"AgentSignaler.Dashboard.{sid}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}" +
            DashboardSettings.InstanceSuffix;
        _mutex = new Mutex(true, $@"Local\{_pipeName}", out var created);
        IsPrimary = created;
    }

    public void SendCommand(byte command)
    {
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
            PipeOptions.CurrentUserOnly);
        pipe.Connect(8000);
        pipe.WriteByte(command);
    }

    public void Listen(DispatcherQueue dispatcher, Action activate, Action exit, Action failed)
    {
        _listener = Task.Run(async () =>
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    await using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(_stopping.Token);
                    var command = new byte[1];
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    try
                    {
                        if (await pipe.ReadAsync(command, timeout.Token) == 1)
                        {
                            if (command[0] == 1) dispatcher.TryEnqueue(() => activate());
                            else if (command[0] == 2) dispatcher.TryEnqueue(() => exit());
                        }
                    }
                    catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                    {
                        // An incomplete activation must not block subsequent activations.
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
            {
                dispatcher.TryEnqueue(() => failed());
            }
        });
        dispatcher.TryEnqueue(async () => await _listener);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener?.GetAwaiter().GetResult();
        _stopping.Dispose();
        if (IsPrimary) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
