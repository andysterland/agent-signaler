using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using AgentSignaler.Contracts.Rpc.V1;
using AgentSignaler.Dashboard;

namespace AgentSignaler.RpcHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RpcHostOptions options;
        try { options = RpcHostOptions.Parse(args); }
        catch (ArgumentException) { return Fail(2, "invalidArguments: use --rpc-port 1024..65535 and an absolute --data-directory."); }
        DashboardResourceLease lease;
        try { lease = DashboardResourceLease.Acquire(options.DataDirectory); }
        catch (DashboardOwnershipException) { return Fail(3, "resourceOwned: exit Dashboard or RpcHost; upgrade older Dashboard versions before retrying."); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
        {
            return Fail(2, "invalidDataDirectory: use one accessible absolute local data directory.");
        }
        using (lease)
        {
            DashboardRuntime runtime;
            try
            {
                runtime = new(lease, new DashboardRuntimeOptions
                {
                    RpcPortOverride = options.RpcPort, InstalledReceiverPort = InstalledReceiverMetadata.Read(),
                    RejectInvalidSavedPorts = true
                });
            }
            catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or SecurityException)
            {
                return Fail(2, "invalidConfiguration: verify operational settings.");
            }
            var rpcPort = runtime.Settings.State.Effective.RpcPort;
            if (rpcPort == runtime.Receiver.State.Port)
                return Fail(2, "portConflict: supply --rpc-port with a different non-privileged port.");
            RpcShutdown? shutdown = null;
            using var application = new RuntimeRpcApplication(runtime, () => shutdown!.Request(),
                accepted => shutdown!.Request(accepted));
            var transport = new RpcServer(application, rpcPort);
            shutdown = new(runtime.ShutdownAsync, () => transport.StopAdmission(cancelConnections: false),
                () => transport.DisposeAsync().AsTask());
            try { await transport.StartAsync(); }
            catch (Exception error) when (error is IOException or SocketException or InvalidOperationException)
            {
                shutdown.Request();
                await shutdown.Completion;
                return Fail(4, "rpcBindFailed: supply --rpc-port with an available non-privileged port.");
            }
            ConsoleCancelEventHandler cancel = (_, signal) =>
            {
                signal.Cancel = true;
                shutdown.Request();
            };
            Console.CancelKeyPress += cancel;
            var exitCode = 0;
            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new RpcTransportReady("transportReady",
                    rpcPort, 1, runtime.HostInstanceId.ToString("D")), RpcProtocol.Json));
                await Console.Out.FlushAsync();
                var initialization = runtime.InitializeAsync();
                var first = await Task.WhenAny(initialization, shutdown.Requested);
                if (first == initialization)
                {
                    await initialization;
                    await shutdown.Requested;
                }
            }
            catch (Exception)
            {
                // The process boundary deliberately emits only a fixed category, never exception text.
                exitCode = Fail(5, "fatalRuntimeFailure: operational startup could not continue.");
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
                shutdown.Request();
                if (!await shutdown.Completion)
                    exitCode = Fail(6, "uncleanShutdown: owned cleanup did not complete within its deadline.");
            }
            return exitCode;
        }
    }

    private static int Fail(int code, string category)
    {
        try { Console.Error.WriteLine(category); }
        catch (IOException) { }
        return code;
    }
}
