using System.Diagnostics;
using System.Net.NetworkInformation;
using AgentSignaler.Remote;

namespace AgentSignaler.Client;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ClientCoordinator? coordinator = null;
        ClientIpcServer? server = null;
        TrayWindow? window = null;
        NetworkAvailabilityChangedEventHandler? availabilityChanged = null;
        NetworkAddressChangedEventHandler? addressChanged = null;
        using var completionLifetime = new CancellationTokenSource();
        try
        {
            var configPath = RemotePaths.DefaultConfig;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--background") continue;
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else throw new InvalidDataException("Use AgentSignaler.Client.exe [--background] [--config <absolute path>].");
            }
            configPath = ClientIdentity.CanonicalPath(configPath);
            var config = RemoteConfiguration.Load(configPath);
            RemotePaths.ValidateRelayInstallation(AppContext.BaseDirectory, config.RelayPath);
            var configurator = Path.Combine(AppContext.BaseDirectory, "AgentSignaler.Configurator.exe");
            if (!File.Exists(configurator))
                throw new InvalidDataException("Configurator is missing beside Client. Repair the Remote installation.");

            bool TryOpenConfigurator(out string? error)
            {
                try
                {
                    var start = new ProcessStartInfo(configurator) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
                    start.ArgumentList.Add("--config");
                    start.ArgumentList.Add(configPath);
                    using var process = Process.Start(start)
                        ?? throw new InvalidOperationException("Windows did not start Configurator.");
                    error = null;
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or
                    System.Security.SecurityException)
                {
                    error = "Configurator could not be opened. Repair the Remote installation.";
                    return false;
                }
            }

            try { coordinator = new ClientCoordinator(configPath); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
            {
                var response = ClientIpc.ActivateAsync(configPath).GetAwaiter().GetResult();
                if (!response.Accepted)
                    TrayWindow.ShowError("Agent Signaler Client is already running for this data directory. " +
                        "Its icon may be in another Windows sign-in session. Use that icon or Configurator to control it.");
                return 0;
            }
            var runtime = coordinator;
            window = new TrayWindow(() =>
            {
                if (!TryOpenConfigurator(out var error)) TrayWindow.ShowError(error!);
            }, runtime.Status, runtime.RequestSnapshot);
            var tray = window;
            var exitStarted = 0;
            window.ExitRequested += quiet =>
            {
                if (Interlocked.Exchange(ref exitStarted, 1) != 0) return;
                _ = Task.Run(async () =>
                {
                    var response = await runtime.StopAsync();
                    tray.Close(quiet ? null : response.Error);
                });
            };
            server = new ClientIpcServer(configPath, (request, cancellationToken) =>
            {
                if (request.Command != "activate") return runtime.HandleAsync(request, cancellationToken);
                if (request.Event is not null || request.Hook is not null || request.ExpectedRevision is not null || request.ProbeId is not null)
                    return Task.FromResult(new ClientIpcResponse(false, "invalid"));
                return Task.FromResult(TryOpenConfigurator(out var error)
                    ? runtime.Status()
                    : new ClientIpcResponse(false, runtime.Status().State, Error: error));
            });
            server.StopAcknowledged += () => tray.Close(null);
            runtime.Start();
            availabilityChanged = (_, change) =>
            {
                if (change.IsAvailable) runtime.RequestSnapshot();
            };
            addressChanged = (_, _) => runtime.RequestSnapshot();
            NetworkChange.NetworkAvailabilityChanged += availabilityChanged;
            NetworkChange.NetworkAddressChanged += addressChanged;
            _ = CloseOnCompletionAsync(runtime, tray, completionLifetime.Token);
            window.Run();
            return runtime.Completion.IsFaulted ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Text.Json.JsonException or ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or OverflowException)
        {
            TrayWindow.ShowError("Agent Signaler Client could not start. " + ex.Message);
            return 1;
        }
        finally
        {
            completionLifetime.Cancel();
            if (availabilityChanged is not null)
                NetworkChange.NetworkAvailabilityChanged -= availabilityChanged;
            if (addressChanged is not null)
                NetworkChange.NetworkAddressChanged -= addressChanged;
            // The file owner lock is released only after IPC, networking, and the icon are gone.
            try
            {
                if (coordinator is not null) coordinator.StopAsync().GetAwaiter().GetResult();
            }
            finally
            {
                try
                {
                    if (server is not null) server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                finally
                {
                    try { window?.Dispose(); }
                    finally
                    {
                        if (coordinator is not null) coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    }
                }
            }
        }
    }

    private static async Task CloseOnCompletionAsync(ClientCoordinator coordinator, TrayWindow window,
        CancellationToken cancellationToken)
    {
        try { window.Close(await ClientLifetime.WaitForExitAsync(coordinator, cancellationToken)); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
