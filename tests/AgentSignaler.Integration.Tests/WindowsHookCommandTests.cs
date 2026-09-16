using System.Diagnostics;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class WindowsHookCommandTests
{
    [Theory]
    [InlineData("space fixture")]
    [InlineData("ampersand & (parentheses) ^ unicode-\u00e9")]
    public async Task CmdExecutesExactRelayAndConfigurationWithoutControlOutput(string folder)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var binaries = Path.Combine(root, folder);
        var configurationDirectory = Path.Combine(root, folder, "configuration & data");
        Directory.CreateDirectory(binaries);
        Directory.CreateDirectory(configurationDirectory);
        try
        {
            var builtRelay = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "relay-path.txt"))).Trim();
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(builtRelay)!))
                File.Copy(file, Path.Combine(binaries, Path.GetFileName(file)));
            var relay = Path.Combine(binaries, "AgentSignaler.Relay.exe");
            var configPath = Path.Combine(configurationDirectory, "remote.json");
            AtomicFile.Write(configPath, JsonSerializer.SerializeToUtf8Bytes(new RemoteConfiguration
            {
                Version = 4, DashboardBaseUrl = "http://localhost:51820/", MachineId = Guid.NewGuid(),
                RelayPath = relay
            }, Protocol.Json));
            var target = new IntegrationTarget
            {
                Id = "shell-fixture", Kind = "vscode", DisplayName = "Synthetic shell fixture", InstallationId = "fixture",
                ScopeId = "shell-fixture", HostVersion = "1.137", ExecutablePath = relay,
                HookDirectory = Path.Combine(configurationDirectory, "hooks"),
                SettingsPath = Path.Combine(configurationDirectory, "settings.json")
            };
            var probe = HookVerification.Preview(target, configPath, relay);
            HookVerification.Begin(probe, consent: true);
            await using var runtime = new ClientCoordinator(configPath, transportFactory: _ => new FixtureTransport());
            await using var ipc = new ClientIpcServer(configPath, runtime.HandleAsync);
            runtime.Start();
            using var hookJson = JsonDocument.Parse(probe.HookBytes);
            var command = hookJson.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("windows").GetString()!;
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
                {
                    Arguments = "/d /s /c \"" + command + "\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            process.StartInfo.Environment["AGENT_SIGNALER_DATA_DIR"] = root;
            var elapsed = Stopwatch.StartNew();
            Assert.True(process.Start());
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                session_id = "synthetic-shell-session", timestamp = DateTimeOffset.UtcNow.ToString("O"),
                hook_event_name = "Stop", prompt = "synthetic-private-payload"
            }));
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await output);
            Assert.Empty(await errors);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), $"Observer exceeded the three-second hook timeout: {elapsed.Elapsed}.");
            var log = await File.ReadAllTextAsync(RemotePaths.Log(configPath));
            Assert.Contains("hook-accepted-locally", log);
            Assert.DoesNotContain("operation-failed", log);
            Assert.DoesNotContain("synthetic-private-payload", log);
            Assert.Equal(1, HookVerification.ReadEvidence(configPath, probe.ProbeId).Acknowledgements);
            HookVerification.Cancel(probe);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FixtureTransport : IPresenceTransport
    {
        public Task<bool> SendAsync(PresenceReport report, CancellationToken cancellationToken) => Task.FromResult(true);
        public void Dispose() { }
    }
}
