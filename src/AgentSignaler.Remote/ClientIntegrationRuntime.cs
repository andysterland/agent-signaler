using System.Diagnostics;

namespace AgentSignaler.Remote;

public interface IClientRuntimePlatform
{
    bool IsOwnerRunning(string configPath);
    Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request, CancellationToken token);
    void Launch(string clientPath, string configPath);
}

public sealed class ClientIntegrationRuntime(IClientRuntimePlatform? platform = null) : IIntegrationRuntime
{
    private readonly IClientRuntimePlatform platform = platform ?? new WindowsClientRuntimePlatform();

    public async Task<IntegrationRuntimeState> QueryAsync(string configPath, CancellationToken token)
    {
        configPath = ClientIdentity.CanonicalPath(configPath);
        token.ThrowIfCancellationRequested();
        if (!platform.IsOwnerRunning(configPath)) return new(false);
        var response = await platform.SendAsync(configPath, new(ClientIpc.Version, "status"), token).WaitAsync(token);
        if (!response.Accepted || response.Version != ClientIpc.Version)
        {
            if (!platform.IsOwnerRunning(configPath)) return new(false);
            throw new InvalidOperationException("A Client owns this configuration directory but its exact-config IPC is unavailable. Exit that Client from its tray menu and retry.");
        }
        return State(response);
    }

    public async Task<IntegrationRuntimeState> ReloadAsync(string configPath, CancellationToken token)
    {
        configPath = ClientIdentity.CanonicalPath(configPath);
        if (File.Exists(configPath) && RemoteConfiguration.Load(configPath).Version >= 5)
            await ReloadTranscriptAsync(configPath, token);
        var current = await QueryAsync(configPath, token);
        if (!current.Running) return new(false, Message: "Client is stopped. Use Start client to resume reporting.");
        var revision = ClientConfigurationRevision.Read(configPath);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        var response = await platform.SendAsync(configPath, new(ClientIpc.Version, "reload", ExpectedRevision: revision),
            budget.Token).WaitAsync(budget.Token);
        if (!response.Accepted)
            return current with { Message = response.Error ?? "Client did not acknowledge the saved settings." };
        return await AwaitRevision(configPath, revision, State(response), token, budget.Token);
    }

    public Task SuspendTranscriptAsync(string configPath, CancellationToken token) =>
        SendTranscriptReloadAsync(configPath, "suspend", token);

    public Task PauseTranscriptAsync(string configPath, CancellationToken token) =>
        SendTranscriptReloadAsync(configPath, "pause", token);

    public Task ReloadTranscriptAsync(string configPath, CancellationToken token) =>
        SendTranscriptReloadAsync(configPath, null, token);

    private async Task SendTranscriptReloadAsync(string configPath, string? control, CancellationToken token)
    {
        configPath = ClientIdentity.CanonicalPath(configPath);
        token.ThrowIfCancellationRequested();
        if (!platform.IsOwnerRunning(configPath)) return;
        var revision = control ?? ClientConfigurationRevision.Read(configPath);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(1));
        var response = await platform.SendAsync(configPath,
            new(3, "transcript-reload", ExpectedRevision: revision), budget.Token).WaitAsync(budget.Token);
        if (!response.Accepted || response.Version != 3)
            throw new InvalidOperationException("Client did not acknowledge detailed conversation settings. " +
                "Exit the exact Client and upgrade Client, Relay and Configurator together before retrying.");
    }

    public async Task<IntegrationRuntimeState> StartAsync(string clientPath, string configPath, CancellationToken token)
    {
        configPath = ClientIdentity.CanonicalPath(configPath);
        _ = IntegrationStartup.Command(clientPath, configPath);
        var config = RemoteConfiguration.Load(configPath);
        if (config.RelayPath is not null && !string.Equals(Path.GetFullPath(config.RelayPath),
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(clientPath))!, "AgentSignaler.Relay.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Client and configured Relay must belong to the same installation.");
        var current = await QueryAsync(configPath, token);
        if (current.Running) return current;
        var revision = ClientConfigurationRevision.Read(configPath);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        platform.Launch(Path.GetFullPath(clientPath), configPath);
        return await AwaitRevision(configPath, revision, new(false), token, budget.Token);
    }

    private async Task<IntegrationRuntimeState> AwaitRevision(string configPath, string revision,
        IntegrationRuntimeState latest, CancellationToken caller, CancellationToken budget)
    {
        try
        {
            while (true)
            {
                try { latest = await QueryAsync(configPath, budget); }
                catch (InvalidOperationException) { }
                if (latest.Running && latest.EffectiveRevision == revision) return latest;
                await Task.Delay(100, budget);
            }
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return latest with { Message = latest.Running ?
                "Client is running, but the dashboard has not acknowledged the saved configuration." :
                "Client did not become responsive. Repair the installation and retry Start client." };
        }
    }

    public async Task StopAsync(string configPath, CancellationToken token)
    {
        configPath = ClientIdentity.CanonicalPath(configPath);
        token.ThrowIfCancellationRequested();
        if (!platform.IsOwnerRunning(configPath)) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            _ = await platform.SendAsync(configPath, new(ClientIpc.Version, "stop"), budget.Token).WaitAsync(budget.Token);
            while (platform.IsOwnerRunning(configPath))
                await Task.Delay(50, budget.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!platform.IsOwnerRunning(configPath)) return;
            throw new InvalidOperationException("The exact Client still owns its configuration after the shutdown deadline. Exit it using its tray menu and retry; no process was force-killed.");
        }
    }

    private static IntegrationRuntimeState State(ClientIpcResponse response) =>
        new(true, response.EffectiveRevision, response.Error, response.HeartbeatIntervalSeconds);
}

public sealed class WindowsClientRuntimePlatform : IClientRuntimePlatform
{
    public bool IsOwnerRunning(string configPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(configPath)!, "client-owner.lock");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Client ownership lock must not be a symbolic link.");
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return true; }
    }

    public Task<ClientIpcResponse> SendAsync(string configPath, ClientIpcRequest request, CancellationToken token) =>
        ClientIpc.SendAsync(configPath, request, token);

    public void Launch(string clientPath, string configPath)
    {
        if (!File.Exists(clientPath)) throw new InvalidDataException("Client executable is missing. Repair the Remote installation.");
        var start = new ProcessStartInfo(clientPath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(clientPath)! };
        start.ArgumentList.Add("--background");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(configPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Windows could not launch Client.");
    }
}
