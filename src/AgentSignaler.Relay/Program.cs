using AgentSignaler.Remote;

try { _ = RemotePaths.DefaultDirectory; }
catch (Exception ex) when (RemoteFailure.IsExpected(ex))
{
    return args.FirstOrDefault() == "hook" ? 0 : 2;
}

if (args.FirstOrDefault() is "--uninstall-integration" or "--prepare-uninstall-integration" or
    "--rollback-uninstall-integration" or "--commit-uninstall-integration" or "--stop-client-for-update" or
    "--migrate-legacy-heartbeat" or "--rollback-legacy-heartbeat" or "--commit-legacy-heartbeat")
{
    var configPath = RemotePaths.DefaultConfig;
    string? transactionId = null;
    try
    {
        var options = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !options.Add(args[i]))
                throw new InvalidDataException("Servicing options require unique --config <absolute path> and optional --transaction-id <id> pairs.");
            switch (args[i])
            {
                case "--config": configPath = args[i + 1]; break;
                case "--transaction-id": transactionId = args[i + 1]; break;
                default: throw new InvalidDataException("Unknown servicing option. Use --config <absolute path>.");
            }
        }
        if (transactionId is not null && (string.IsNullOrWhiteSpace(transactionId) ||
            transactionId.Length > 256 || transactionId.Any(char.IsControl)))
            throw new InvalidDataException("Invalid servicing transaction identifier.");
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(configPath))
            throw new InvalidDataException("Servicing requires Windows and an absolute configuration path.");
        if (args[0] == "--stop-client-for-update" && transactionId is not null)
            throw new InvalidDataException("--stop-client-for-update does not accept a transaction identifier or change integration.");
        if (args[0] is "--migrate-legacy-heartbeat" or "--rollback-legacy-heartbeat" or "--commit-legacy-heartbeat" &&
            transactionId is null)
            throw new InvalidDataException("Task-only heartbeat servicing requires --transaction-id <id>.");
        var integration = new IntegrationManager(new WindowsTaskScheduler(), (_, _) => Task.FromResult(false),
            new WindowsIntegrationStartup(), new ClientIntegrationRuntime());
        switch (args[0])
        {
            case "--uninstall-integration" when transactionId is not null: integration.PrepareUninstall(configPath, transactionId); break;
            case "--uninstall-integration": integration.Uninstall(configPath, preserveConfiguration: true); break;
            case "--prepare-uninstall-integration": integration.PrepareUninstall(configPath, transactionId); break;
            case "--rollback-uninstall-integration": integration.RollbackUninstall(configPath, transactionId); break;
            case "--commit-uninstall-integration": integration.CommitUninstall(configPath, transactionId); break;
            case "--stop-client-for-update":
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7)))
                    await integration.StopClientForUpdateAsync(configPath, AppContext.BaseDirectory, timeout.Token);
                break;
            case "--migrate-legacy-heartbeat": integration.MigrateLegacyHeartbeat(configPath, AppContext.BaseDirectory, transactionId!); break;
            case "--rollback-legacy-heartbeat": integration.RollbackLegacyHeartbeat(configPath, AppContext.BaseDirectory, transactionId!); break;
            case "--commit-legacy-heartbeat": integration.CommitLegacyHeartbeat(configPath, AppContext.BaseDirectory, transactionId!); break;
        }
        return 0;
    }
    catch (Exception ex) when (RemoteFailure.IsExpected(ex))
    {
        if (args[0] is "--migrate-legacy-heartbeat" or "--rollback-legacy-heartbeat" or "--commit-legacy-heartbeat")
        {
            Console.Error.WriteLine(ex is InvalidDataException or InvalidOperationException ? ex.Message :
                "Legacy heartbeat task servicing failed. Check Task Scheduler and data-directory access. " +
                "Retain heartbeat-migration-*.json and heartbeat-task.xml.agent-signaler.*.backup for recovery; configuration, startup, hooks and Client runtime were not changed.");
            return 1;
        }
        Console.Error.WriteLine(ex is InvalidDataException or InvalidOperationException ? ex.Message :
            ex is OperationCanceledException ? "Client shutdown timed out. Exit the exact Client from its tray menu and retry. No process was force-killed." :
            "Agent Signaler servicing failed. Close the exact Client using its tray Exit action, check access to the saved integration, and retry. " +
            "Do not terminate other users' Client processes. Configuration and startup were preserved where rollback was possible.");
        return 1;
    }
}

return await new RelayEngine().RunAsync(args, Console.OpenStandardInput());
