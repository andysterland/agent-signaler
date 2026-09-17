using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class RemoteTests : IDisposable
{
    // Test scratch stays under the project output, never the real profile or integration directories.
    private readonly string root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    private string ConfigPath => Path.Combine(root, "remote.json");
    private RemoteConfiguration Config => new() { Host = "localhost", MachineId = Guid.Parse("9e40ba11-c2c8-4212-bbfb-c051c12cfe3f"), ClientVersion = "1.0.80" };

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
    private void SaveConfig() => AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Config, Protocol.Json));

    [Theory]
    [InlineData("http://desktop-pc:51820", "desktop-pc", 51820)]
    [InlineData(" http://127.0.0.1:1024/ ", "127.0.0.1", 1024)]
    [InlineData("http://[::1]:65535", "[::1]", 65535)]
    [InlineData("https://desktop-pc", "desktop-pc", 443)]
    [InlineData("https://desktop-pc:443/", "desktop-pc", 443)]
    [InlineData("https://127.0.0.1:1", "127.0.0.1", 1)]
    [InlineData("https://[::1]:65535/", "[::1]", 65535)]
    public void DashboardUrlParsesAndPreservesConfiguration(string url, string host, int port)
    {
        var config = Config.WithDashboardUrl(url);
        Assert.Equal(2, config.Version);
        Assert.Equal(host, config.BaseUri.Host);
        Assert.Equal(port, config.BaseUri.Port);
        Assert.Equal(Config.MachineId, config.MachineId);
        Assert.Equal(Config.MachineName, config.MachineName);
        Assert.Equal(Config.ClientVersion, config.ClientVersion);
        Assert.Equal("/api/v1/status", config.Endpoint.AbsolutePath);
        Assert.Equal("/health", config.HealthEndpoint.AbsolutePath);
        Assert.Equal(config.BaseUri.Scheme, config.Endpoint.Scheme);
        Assert.Equal(config.BaseUri.Authority, config.Endpoint.Authority);
        Assert.Equal(config, config.WithDashboardUrl(config.Endpoint.GetLeftPart(UriPartial.Authority)));
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        Assert.Equal(config, RemoteConfiguration.Load(ConfigPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("desktop-pc:51820")]
    [InlineData("http://desktop-pc")]
    [InlineData("ftp://desktop-pc:51820")]
    [InlineData("https://desktop-pc:0")]
    [InlineData("https://desktop-pc:65536")]
    [InlineData("https://desktop-pc/path/..")]
    [InlineData("https://desktop-pc/.")]
    [InlineData("https://desktop-pc/%2e")]
    [InlineData("https://desktop-pc/?")]
    [InlineData("https://desktop-pc/#")]
    [InlineData("https://@desktop-pc")]
    [InlineData("https://user:password@desktop-pc")]
    [InlineData("\nhttps://desktop-pc")]
    [InlineData("https://desktop-pc\r")]
    [InlineData("http://desktop-pc:1023")]
    [InlineData("http://desktop-pc:65536")]
    [InlineData("http://desktop-pc:abc")]
    [InlineData("http://desktop-pc:51820/api/v1/status")]
    [InlineData("http://user:password@desktop-pc:51820")]
    [InlineData("http://desktop-pc:51820/?test=true")]
    [InlineData("http://desktop-pc:51820/#section")]
    [InlineData("http://desktop-pc:51820\\path")]
    [InlineData("http://desk\ntop-pc:51820")]
    public void InvalidDashboardUrlIsRejected(string url) =>
        Assert.Throws<InvalidDataException>(() => Config.WithDashboardUrl(url));

    private byte[] Hook(string id = "session", string status = "success", string? toolName = null) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            sessionId = id, timestamp = now.ToUnixTimeMilliseconds(), toolName,
            prompt = "SECRET-PROMPT", toolArgs = new { source = "SECRET-CODE" },
            toolResult = new { resultType = status, textResultForLlm = "SECRET-OUTPUT" },
            error = "SECRET-ERROR"
        }));

    [Theory]
    [InlineData("success", false)]
    [InlineData("failure", true)]
    public void ParserReadsOnlyIdentifierTimestampAndToolMetadata(string status, bool failed)
    {
        var parsed = HookParser.Parse(Hook(status: status), AgentEvent.PostToolUse, now);
        Assert.Equal("session", parsed.SessionId);
        Assert.Equal(failed, parsed.ToolFailed);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(parsed));
    }

    [Theory]
    [InlineData(AgentEvent.PreToolUse, "ask_user", true)]
    [InlineData(AgentEvent.PostToolUse, "ask_user", true)]
    [InlineData(AgentEvent.PostToolUseFailure, "ask_user", true)]
    [InlineData(AgentEvent.SessionStart, "ask_user", false)]
    [InlineData(AgentEvent.PreToolUse, "powershell", false)]
    [InlineData(AgentEvent.PreToolUse, "custom_ask_user", false)]
    [InlineData(AgentEvent.PreToolUse, null, false)]
    public void ParserRecognizesUserInputToolOnlyOnToolEvents(AgentEvent kind, string? toolName, bool expected)
    {
        var parsed = HookParser.Parse(Hook(toolName: toolName), kind, now);
        Assert.Equal(expected, parsed.ToolRequiresUserInput);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(parsed));
        Assert.DoesNotContain("ask_user", JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void UserInputWaitPersistsAcrossRelayInvocationsUntilCompletion()
    {
        var path = Path.Combine(root, "sessions.json");
        var log = new DiagnosticLog(Path.Combine(root, "relay.log"));
        var pending = new SessionStore(path, log).Update(AgentEvent.PreToolUse,
            HookParser.Parse(Hook(toolName: "ask_user"), AgentEvent.PreToolUse, now), now);
        Assert.True(Assert.Single(pending).AwaitingUserInput);
        var unrelated = new SessionStore(path, log).Update(AgentEvent.PostToolUse,
            new HookData("session", now.AddSeconds(1), false), now.AddSeconds(1));
        Assert.Equal(AgentState.Waiting, StateReducer.Aggregate(unrelated, now.AddSeconds(1)));
        var completed = new SessionStore(path, log).Update(AgentEvent.PostToolUse,
            new HookData("session", now.AddSeconds(2), false, true), now.AddSeconds(2));
        Assert.False(Assert.Single(completed).AwaitingUserInput);
        Assert.Equal(AgentState.Executing, StateReducer.Aggregate(completed, now.AddSeconds(2)));
        Assert.DoesNotContain("SECRET", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"sessionId\":\"\",\"timestamp\":1}")]
    [InlineData("{\"sessionId\":\"abc\",\"timestamp\":\"2026-01-01\"}")]
    [InlineData("{\"sessionId\":\"abc\",\"timestamp\":9223372036854775807}")]
    public void MalformedHookRejected(string value) =>
        Assert.ThrowsAny<Exception>(() => HookParser.Parse(Encoding.UTF8.GetBytes(value), AgentEvent.SessionStart, now));

    [Fact]
    public void IdentityPersistsAcrossConfigRemoval()
    {
        var id = MachineIdentity.GetOrCreate(root);
        SaveConfig();
        File.Delete(ConfigPath);
        Assert.Equal(id, MachineIdentity.GetOrCreate(root));
    }

    [Fact]
    public void CorruptIdentityIsNeverSilentlyReplaced()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(RemotePaths.Identity(root), "invalid");
        Assert.Throws<InvalidDataException>(() => MachineIdentity.GetOrCreate(root));
    }

    [Fact]
    public async Task StateWritesAreAtomicAndBoundedAcrossWriters()
    {
        var log = new DiagnosticLog(Path.Combine(root, "relay.log"));
        var path = Path.Combine(root, "sessions.json");
        for (var batch = 0; batch < 20; batch++)
        {
            var current = batch;
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() =>
                new SessionStore(path, log).Update(AgentEvent.PreToolUse, new HookData($"{current}-{i}", now, false), now))));
        }
        var sessions = new SessionStore(path, log).Update(null, null, now);
        Assert.Equal(Protocol.MaxSessions, sessions.Count);
        Assert.Equal(sessions.Count, sessions.Select(s => s.SessionId).Distinct().Count());
    }

    [Fact]
    public void DelayedResultsRemainValidForSharedProtocol()
    {
        var store = new SessionStore(Path.Combine(root, "sessions.json"), new DiagnosticLog(Path.Combine(root, "relay.log")));
        var sessions = store.Update(AgentEvent.AgentStop, new HookData("s", now.AddSeconds(-30), false), now);
        var result = Assert.Single(sessions);
        Assert.Equal(AgentState.Succeeded, result.ResultState);
        Assert.Equal(now + Protocol.ResultDuration, result.ResultUntilUtc);
    }

    [Fact]
    public void UnicodeSessionsRemainWithinWireByteLimit()
    {
        var path = Path.Combine(root, "sessions.json");
        var store = new SessionStore(path, new DiagnosticLog(Path.Combine(root, "relay.log")));
        List<SessionSnapshot> sessions = [];
        for (var i = 0; i < 80; i++)
            sessions = store.Update(AgentEvent.PreToolUse, new HookData(new string('界', 120) + i, now, false), now);
        var report = store.UpdateReport(null, null, now);
        var request = Request() with { ReportedAtUtc = report.ReportedAtUtc, SessionId = report.Sessions[0].SessionId };
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(request, Protocol.Json).Length, 1, Protocol.MaxBodyBytes);
        Assert.Empty(Protocol.Validate(request));
    }

    [Fact]
    public void ReportClockIsPersistentAndMonotonicAcrossLateCrossSessionHooks()
    {
        var path = Path.Combine(root, "sessions.json");
        var log = new DiagnosticLog(Path.Combine(root, "relay.log"));
        var first = new SessionStore(path, log).UpdateReport(AgentEvent.PreToolUse, new HookData("a", now, false), now);
        var second = new SessionStore(path, log).UpdateReport(AgentEvent.SessionStart, new HookData("b", now.AddSeconds(-2), false), now);
        Assert.True(second.ReportedAtUtc > first.ReportedAtUtc);
        Assert.Equal(second.ReportedAtUtc, second.Sessions.Single(s => s.SessionId == "b").UpdatedAtUtc);
        Assert.True(second.Accepted);
    }

    [Fact]
    public void EndedSessionsRetainResultsThenRetireAndRejectOlderHooks()
    {
        var store = new SessionStore(Path.Combine(root, "sessions.json"), new DiagnosticLog(Path.Combine(root, "relay.log")));
        store.UpdateReport(AgentEvent.AgentStop, new HookData("a", now, false), now);
        var ended = store.UpdateReport(AgentEvent.SessionEnd, new HookData("a", now.AddSeconds(1), false), now.AddSeconds(1));
        Assert.Equal(AgentState.Succeeded, StateReducer.Aggregate(ended.Sessions, ended.ReportedAtUtc));
        var expired = store.UpdateReport(null, null, now.AddSeconds(61));
        Assert.Empty(expired.Sessions);
        var late = store.UpdateReport(AgentEvent.PreToolUse, new HookData("a", now.AddSeconds(-1), false), now.AddSeconds(62));
        Assert.False(late.Accepted);
        Assert.Empty(late.Sessions);
    }

    [Fact]
    public async Task HookFailureIsZeroAndTestFailureIsNonzero()
    {
        SaveConfig();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        var engine = new RelayEngine(http);
        Assert.Equal(0, await engine.RunAsync(["hook", "--event", "preToolUse", "--config", ConfigPath], new MemoryStream(Hook())));
        Assert.Equal(1, await engine.RunAsync(["test", "--config", ConfigPath], Stream.Null));
        Assert.Equal(0, await engine.RunAsync(["hook", "--event", "wrong", "--config", ConfigPath], Stream.Null));
        Assert.Equal(0, await engine.RunAsync(["hook", "--event", "preToolUse", "--config", ConfigPath], new MemoryStream(Encoding.UTF8.GetBytes("{}"))));
        Assert.False(File.Exists(Path.Combine(root, "sessions.json")));
        Assert.DoesNotContain("SECRET", File.ReadAllText(Path.Combine(root, "relay.log")));
    }

    [Fact]
    public async Task HooksWithoutClientNeverSendHttpOrMutateMissingAndCorruptState()
    {
        SaveConfig();
        var received = new List<StatusRequest>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            received.Add(JsonSerializer.Deserialize<StatusRequest>(await request.Content!.ReadAsByteArrayAsync(token), Protocol.Json)!);
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }));
        var engine = new RelayEngine(http);
        Assert.Equal(0, await engine.RunAsync(["hook", "--event", "preToolUse", "--config", ConfigPath], new MemoryStream(Hook())));
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
        File.WriteAllText(Path.Combine(root, "sessions.json"), "broken");
        Assert.Equal(0, await engine.RunAsync(["hook", "--event", "preToolUse", "--config", ConfigPath], new MemoryStream(Hook())));
        Assert.Empty(received);
        Assert.Equal("broken", File.ReadAllText(RemotePaths.State(ConfigPath)));
    }

    [Fact]
    public async Task RemovedHeartbeatModeDoesNotSendOrWriteState()
    {
        SaveConfig();
        using var http = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("Unexpected request.")));
        Assert.Equal(2, await new RelayEngine(http).RunAsync(["heartbeat", "--config", ConfigPath], Stream.Null));
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
    }

    [Fact]
    public async Task TestModeProbesHealthWithoutWritingSessionState()
    {
        SaveConfig();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"protocolVersion\":1,\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        }));
        Assert.Equal(0, await new RelayEngine(http).RunAsync(["test", "--config", ConfigPath], Stream.Null));
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
    }

    [Fact]
    public async Task RetryIsBoundedAndUsesSameEventId()
    {
        var ids = new List<Guid>();
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            ids.Add(JsonSerializer.Deserialize<StatusRequest>(await request.Content!.ReadAsByteArrayAsync(token), Protocol.Json)!.EventId);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }));
        Assert.False(await new RelayEngine(http).SendAsync(Config, Request(), CancellationToken.None));
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
    }

    [Fact]
    public async Task MissingClientDoesNotInvokeHttpAndReturnsWithinHookBudget()
    {
        SaveConfig();
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, await new RelayEngine(http).RunAsync(["hook", "--event", "preToolUse", "--config", ConfigPath], new MemoryStream(Hook())));
        Assert.InRange(watch.Elapsed.TotalSeconds, 0, 3.5);
        Assert.False(File.Exists(RemotePaths.State(ConfigPath)));
    }

    [Fact]
    public void DiagnosticLogsAreBoundedAndRejectArbitraryMessages()
    {
        var path = Path.Combine(root, "relay.log");
        var log = new DiagnosticLog(path);
        Assert.Throws<ArgumentException>(() => log.Write("a payload with secrets!"));
        Assert.False(File.Exists(path));
        for (var i = 0; i < 1400; i++) log.Write("delivery-failed");
        Assert.InRange(new FileInfo(path).Length, 1, 33000);
        Assert.InRange(new FileInfo(path + ".1").Length, 1, 33000);
    }

    [Fact]
    public void QuietActiveSessionIsNotDiscardedAfterOneDay()
    {
        var store = new SessionStore(Path.Combine(root, "sessions.json"), new DiagnosticLog(Path.Combine(root, "relay.log")));
        store.UpdateReport(AgentEvent.SessionStart, new HookData("waiting", now, false), now);
        var report = store.UpdateReport(null, null, now.AddDays(2));
        Assert.Equal(AgentState.Waiting, StateReducer.Aggregate(report.Sessions, report.ReportedAtUtc));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NoContent, false)]
    [InlineData(HttpStatusCode.Accepted, true)]
    public async Task DeliveryRequiresProtocolAcceptedResponse(HttpStatusCode code, bool expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(code))));
        Assert.Equal(expected, await new RelayEngine(http).SendAsync(Config, Request(), CancellationToken.None));
    }

    [Fact]
    public async Task HealthTestsUnsavedEndpointWithoutWritingConfiguration()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/health", request.RequestUri!.AbsolutePath);
            Assert.Equal("new-dashboard", request.RequestUri.Host);
            Assert.Equal("1", Assert.Single(request.Headers.GetValues(Protocol.ConnectionTestHeader)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"protocolVersion\":1,\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        }));
        await DashboardConnection.TestAsync(Config with { Host = "new-dashboard" }, http, CancellationToken.None);
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("{\"protocolVersion\":2,\"status\":\"ok\"}")]
    [InlineData("{\"protocolVersion\":1,\"status\":\"not-ok\"}")]
    [InlineData("{}")]
    public async Task HealthRejectsIncompatibleServices(string json)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        })));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DashboardConnection.TestAsync(Config, http, CancellationToken.None));
    }

    [Fact]
    public async Task RepeatedRepairAndRemovalKeepDistinctTimestampedBackups()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        await manager.ApplyAsync(plan, CancellationToken.None);
        await manager.ApplyAsync(plan, CancellationToken.None);
        manager.Uninstall(ConfigPath);
        var backups = Directory.GetFiles(root, "remote.json.agent-signaler.*.backup");
        Assert.Equal(3, backups.Length);
        Assert.All(backups, file => Assert.Equal(plan.ConfigBytes, File.ReadAllBytes(file)));
        Assert.All(backups, file => Assert.Matches(@"remote\.json\.agent-signaler\.\d{8}T\d{13}Z\.[a-f0-9]{32}\.backup$", file));
    }

    [Fact]
    public void HookPreviewIncludesAllEventsAndOnlyLegacyTaskCleanup()
    {
        var plan = Plan();
        Assert.Equal(plan.RelayPath, plan.Config.RelayPath);
        using var configDocument = JsonDocument.Parse(plan.ConfigBytes);
        Assert.Equal(plan.RelayPath, configDocument.RootElement.GetProperty("relayPath").GetString());
        using var document = JsonDocument.Parse(plan.HookBytes);
        Assert.Equal(1, document.RootElement.GetProperty("version").GetInt32());
        var hooks = document.RootElement.GetProperty("hooks");
        Assert.Equal(HookAdapters.Events("copilot-cli").Count, hooks.EnumerateObject().Count());
        Assert.False(hooks.TryGetProperty("executionStopped", out _));
        Assert.False(hooks.TryGetProperty("heartbeat", out _));
        var failure = hooks.GetProperty("postToolUseFailure")[0];
        Assert.Equal(plan.RelayPath, failure.GetProperty("exec").GetString());
        Assert.Equal(3, failure.GetProperty("timeoutSec").GetInt32());
        Assert.Equal(new[] { "hook", "--event", "postToolUseFailure", "--config", ConfigPath },
            failure.GetProperty("args").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains($"REMOVE owned legacy heartbeat task {plan.TaskName} if present", plan.Preview);
        Assert.Contains("No scheduled task is installed", plan.Preview);
        Assert.Contains("current-user Startup programs shortcut", plan.Preview);
        Assert.Contains(plan.StartupName + ".lnk", plan.Preview);
        Assert.DoesNotContain("REGISTER HKCU", plan.Preview);
        Assert.DoesNotContain("<Task", plan.Preview);
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void LegacyTaskOwnershipRequiresMatchingDescriptionAndNamespace()
    {
        Assert.True(ScheduledTaskDefinition.IsOwned(LegacyTaskXml, Config.MachineId, Plan().RelayPath, ConfigPath));
        Assert.False(ScheduledTaskDefinition.IsOwned(LegacyTaskXml, Guid.NewGuid(), Plan().RelayPath, ConfigPath));
        var task = XDocument.Parse(LegacyTaskXml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        task.Root!.Element(ns + "RegistrationInfo")!.Element(ns + "Description")!.Value = "unrelated task";
        Assert.False(ScheduledTaskDefinition.IsOwned(task.ToString(), Config.MachineId, Plan().RelayPath, ConfigPath));
        Assert.False(ScheduledTaskDefinition.IsOwned(
            $"<Task><RegistrationInfo><Description>{ScheduledTaskDefinition.Owner(Config.MachineId)}</Description></RegistrationInfo></Task>",
            Config.MachineId, Plan().RelayPath, ConfigPath));
        Assert.False(ScheduledTaskDefinition.IsOwned("not xml", Config.MachineId, Plan().RelayPath, ConfigPath));
    }

    [Fact]
    public async Task InstallAndUninstallOnlyOwnedArtifactsAndKeepIdentity()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var id = MachineIdentity.GetOrCreate(root);
        Directory.CreateDirectory(Path.GetDirectoryName(plan.HookPath)!);
        var unrelated = Path.Combine(Path.GetDirectoryName(plan.HookPath)!, "unrelated.json");
        File.WriteAllText(unrelated, "do not touch");
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        Assert.True(File.Exists(plan.HookPath));
        Assert.Equal(plan.RelayPath, RemoteConfiguration.Load(ConfigPath).RelayPath);
        var manifest = JsonSerializer.Deserialize<IntegrationManifest>(
            File.ReadAllBytes(Path.Combine(root, "integration.json")), Protocol.Json);
        Assert.Equal(plan.RelayPath, manifest!.RelayPath);
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        Assert.Equal(0, scheduler.Writes);
        Assert.Equal(0, scheduler.Deletes);
        manager.Uninstall(ConfigPath);
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(File.Exists(ConfigPath));
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        Assert.Equal("do not touch", File.ReadAllText(unrelated));
        Assert.Equal(id, MachineIdentity.GetOrCreate(root));
        Assert.Equal(0, scheduler.Writes);
        Assert.Equal(0, scheduler.Deletes);
    }

    [Fact]
    public async Task FailedDeliveryRollsBackPriorFilesAndTaskDefinition()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        var oldConfig = File.ReadAllBytes(ConfigPath);
        var oldHook = File.ReadAllBytes(plan.HookPath);
        var oldTask = scheduler.ReadXml(plan.TaskName);
        var updated = IntegrationManager.Preview(Config with { Host = "changed-host" }, ConfigPath,
            Path.Combine(root, "home"), plan.RelayPath);
        var failing = new IntegrationManager(scheduler, (_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApplyAsync(updated, CancellationToken.None));
        Assert.Equal(oldConfig, File.ReadAllBytes(ConfigPath));
        Assert.Equal(oldHook, File.ReadAllBytes(plan.HookPath));
        Assert.Equal(oldTask, scheduler.ReadXml(plan.TaskName));
    }

    [Fact]
    public async Task SchedulerRemovalFailureRollsBackNewFiles()
    {
        var scheduler = new FakeScheduler { FailNextDelete = true };
        var plan = Plan();
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        AtomicFile.Write(plan.RelayPath, []);
        await Assert.ThrowsAsync<IOException>(() => new IntegrationManager(scheduler, (_, _) => Task.FromResult(true))
            .ApplyAsync(plan, CancellationToken.None));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(File.Exists(Path.Combine(root, "integration.json")));
        Assert.Equal(LegacyTaskXml, scheduler.ReadXml(plan.TaskName));
        Assert.Equal(1, scheduler.Writes);
    }

    [Fact]
    public async Task ApplyRemovesAndBacksUpOwnedLegacyTaskWithoutRegisteringAnother()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        scheduler.Write("unrelated-task", "<Task />");
        AtomicFile.Write(plan.RelayPath, []);
        await new IntegrationManager(scheduler, (_, _) =>
        {
            Assert.Equal(LegacyTaskXml, scheduler.ReadXml(plan.TaskName));
            return Task.FromResult(true);
        }).ApplyAsync(plan, CancellationToken.None);
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        Assert.Equal("<Task />", scheduler.ReadXml("unrelated-task"));
        Assert.Equal(2, scheduler.Writes);
        Assert.Equal(1, scheduler.Deletes);
        Assert.Equal(LegacyTaskXml, File.ReadAllText(Assert.Single(
            Directory.GetFiles(root, "heartbeat-task.xml.agent-signaler.*.backup"))));
    }

    [Fact]
    public async Task FailedFreshInstallDoesNotCreateOrDeleteTasks()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IntegrationManager(scheduler, (_, _) => Task.FromResult(false)).ApplyAsync(plan, CancellationToken.None));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.Equal(0, scheduler.Writes);
        Assert.Equal(0, scheduler.Deletes);
    }

    [Theory]
    [InlineData("<Task />")]
    [InlineData("not xml")]
    public async Task ApplyRefusesUnrelatedTaskWithoutChangingFiles(string xml)
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        scheduler.Write(plan.TaskName, xml);
        AtomicFile.Write(plan.RelayPath, []);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new IntegrationManager(scheduler, (_, _) => Task.FromResult(true)).ApplyAsync(plan, CancellationToken.None));
        Assert.Equal(xml, scheduler.ReadXml(plan.TaskName));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.Equal(1, scheduler.Writes);
        Assert.Equal(0, scheduler.Deletes);
    }

    [Fact]
    public async Task FailedCapabilityCheckDoesNotOverwriteReplacementTask()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        AtomicFile.Write(plan.RelayPath, []);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new IntegrationManager(scheduler, (_, _) =>
            {
                scheduler.Write(plan.TaskName, "<Task />");
                return Task.FromResult(false);
            }).ApplyAsync(plan, CancellationToken.None));
        Assert.Equal("<Task />", scheduler.ReadXml(plan.TaskName));
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.Empty(Directory.GetFiles(root, "heartbeat-task.xml.agent-signaler.*.backup"));
    }

    [Fact]
    public async Task RefuseOverwritingUnrelatedHook()
    {
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        AtomicFile.Write(plan.HookPath, Encoding.UTF8.GetBytes("unrelated"));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new IntegrationManager(new FakeScheduler(), (_, _) => Task.FromResult(true)).ApplyAsync(plan, CancellationToken.None));
        Assert.Equal("unrelated", File.ReadAllText(plan.HookPath));
    }

    [Fact]
    public async Task RefuseDeletingModifiedOwnedHook()
    {
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(new FakeScheduler(), (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        File.AppendAllText(plan.HookPath, " ");
        Assert.Throws<InvalidDataException>(() => manager.Uninstall(ConfigPath));
        Assert.True(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionalUninstallPreservesSettingsAndRollsBack(bool legacyTask)
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        if (legacyTask) scheduler.Write(plan.TaskName, LegacyTaskXml);
        var config = File.ReadAllBytes(ConfigPath);
        var hook = File.ReadAllBytes(plan.HookPath);
        var task = scheduler.ReadXml(plan.TaskName);
        manager.PrepareUninstall(ConfigPath);
        manager.PrepareUninstall(ConfigPath);
        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        manager.RollbackUninstall(ConfigPath);
        manager.RollbackUninstall(ConfigPath);
        Assert.Equal(config, File.ReadAllBytes(ConfigPath));
        Assert.Equal(hook, File.ReadAllBytes(plan.HookPath));
        Assert.Equal(task, scheduler.ReadXml(plan.TaskName));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
        Assert.Equal(legacyTask ? 2 : 0, scheduler.Writes);
        Assert.Equal(legacyTask ? 1 : 0, scheduler.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallRemovesOwnedLegacyTask(bool prepare)
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        if (prepare) manager.PrepareUninstall(ConfigPath);
        else manager.Uninstall(ConfigPath);
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        Assert.False(File.Exists(plan.HookPath));
        Assert.Equal(1, scheduler.Writes);
        Assert.Equal(1, scheduler.Deletes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstallRefusesUnrelatedTaskWithoutChangingFiles(bool prepare)
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        scheduler.Write(plan.TaskName, "<Task />");
        Assert.Throws<InvalidDataException>(() =>
        {
            if (prepare) manager.PrepareUninstall(ConfigPath);
            else manager.Uninstall(ConfigPath);
        });
        Assert.Equal("<Task />", scheduler.ReadXml(plan.TaskName));
        Assert.Equal(plan.ConfigBytes, File.ReadAllBytes(ConfigPath));
        Assert.Equal(plan.HookBytes, File.ReadAllBytes(plan.HookPath));
        Assert.True(File.Exists(Path.Combine(root, "integration.json")));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
        Assert.Equal(0, scheduler.Deletes);
    }

    [Fact]
    public async Task UninstallTaskRemovalFailurePreservesOwnedIntegration()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        scheduler.FailNextDelete = true;
        Assert.Throws<IOException>(() => manager.Uninstall(ConfigPath));
        Assert.Equal(LegacyTaskXml, scheduler.ReadXml(plan.TaskName));
        Assert.Equal(plan.ConfigBytes, File.ReadAllBytes(ConfigPath));
        Assert.Equal(plan.HookBytes, File.ReadAllBytes(plan.HookPath));
        Assert.True(File.Exists(Path.Combine(root, "integration.json")));
        Assert.Equal(1, scheduler.Writes);
    }

    [Fact]
    public async Task TransactionalRollbackDoesNotOverwriteReplacementTask()
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        scheduler.Write(plan.TaskName, LegacyTaskXml);
        manager.PrepareUninstall(ConfigPath);
        scheduler.Write(plan.TaskName, "<Task />");
        Assert.Throws<InvalidDataException>(() => manager.RollbackUninstall(ConfigPath));
        Assert.Equal("<Task />", scheduler.ReadXml(plan.TaskName));
        Assert.False(File.Exists(plan.HookPath));
        Assert.True(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionalUninstallCommitOnlyDeletesJournal(bool legacyTask)
    {
        var scheduler = new FakeScheduler();
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        if (legacyTask) scheduler.Write(plan.TaskName, LegacyTaskXml);
        manager.PrepareUninstall(ConfigPath);
        manager.CommitUninstall(ConfigPath);
        manager.CommitUninstall(ConfigPath);
        manager.RollbackUninstall(ConfigPath);
        Assert.True(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        Assert.Equal(legacyTask ? 1 : 0, scheduler.Writes);
    }

    [Fact]
    public async Task TransactionalRollbackDoesNotOverwriteConcurrentChanges()
    {
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(new FakeScheduler(), (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        manager.PrepareUninstall(ConfigPath);
        AtomicFile.Write(plan.HookPath, Encoding.UTF8.GetBytes("new unrelated file"));
        Assert.Throws<InvalidDataException>(() => manager.RollbackUninstall(ConfigPath));
        Assert.Equal("new unrelated file", File.ReadAllText(plan.HookPath));
        Assert.True(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath)));
    }

    [Fact]
    public async Task TransactionIdentifiersPreventStaleRollbackAndRequireCommitBeforeReinstall()
    {
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        var manager = new IntegrationManager(new FakeScheduler(), (_, _) => Task.FromResult(true));
        await manager.ApplyAsync(plan, CancellationToken.None);
        manager.PrepareUninstall(ConfigPath, "first uninstall transaction");
        manager.RollbackUninstall(ConfigPath, "unrelated transaction");
        Assert.False(File.Exists(plan.HookPath));
        Assert.True(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath, "first uninstall transaction")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(plan, CancellationToken.None));
        manager.CommitUninstall(ConfigPath, "first uninstall transaction");
        await manager.ApplyAsync(plan, CancellationToken.None);
        manager.PrepareUninstall(ConfigPath, "second uninstall transaction");
        manager.RollbackUninstall(ConfigPath, "second uninstall transaction");
        Assert.True(File.Exists(plan.HookPath));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath, "first uninstall transaction")));
        Assert.False(File.Exists(IntegrationManager.RemovalJournalPath(ConfigPath, "second uninstall transaction")));
    }

    [Theory]
    [InlineData("evil/path")]
    [InlineData("user@host")]
    [InlineData("http://host")]
    [InlineData("host?secret")]
    public void EndpointRejectsNonHostComponents(string host) =>
        Assert.Throws<InvalidDataException>(() => (Config with { Host = host }).Validate());

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void EndpointRejectsPortsOutsideNonprivilegedRange(int port) =>
        Assert.Throws<InvalidDataException>(() => (Config with { Port = port }).Validate());

    [Theory]
    [InlineData(1024)]
    [InlineData(65535)]
    public void EndpointAcceptsNonprivilegedBoundaryPorts(int port) => (Config with { Port = port }).Validate();

    [Fact]
    public void EndpointUsesVersionedServiceRoute() => Assert.Equal("http://localhost:51820/api/v1/status", Config.Endpoint.AbsoluteUri);

    [Fact]
    public void ConfigurationContainsOnlyPersistedSchemaProperties()
    {
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(Config, Protocol.Json));
        Assert.Equal(new[] { "clientVersion", "host", "machineId", "machineName", "port", "version" },
            document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConfigurationRoundTripsRelayPathWithoutRequiringInstalledBinary(int version)
    {
        var config = version == 1 ? Config : Config.ToVersion2();
        config = config with { RelayPath = Path.Combine(root, "installed app", "AgentSignaler.Relay.exe") };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        using var document = JsonDocument.Parse(File.ReadAllBytes(ConfigPath));
        Assert.Equal(config.RelayPath, document.RootElement.GetProperty("relayPath").GetString());
        Assert.Equal(config, RemoteConfiguration.Load(ConfigPath));
        Assert.Equal(config.RelayPath, config.WithDashboardUrl("https://dashboard").RelayPath);
        Assert.False(File.Exists(config.RelayPath));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ConfigurationWithoutRelayPathLoadsWithoutRewriting(int version)
    {
        var config = version == 1 ? Config : Config.ToVersion2();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json);
        AtomicFile.Write(ConfigPath, bytes);
        Assert.Null(RemoteConfiguration.Load(ConfigPath).RelayPath);
        Assert.Equal(bytes, File.ReadAllBytes(ConfigPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AgentSignaler.Relay.exe")]
    [InlineData(@"C:AgentSignaler.Relay.exe")]
    [InlineData(@"C:\Remote\Another.exe")]
    [InlineData("C:\\Remote\\\nAgentSignaler.Relay.exe")]
    public void ConfigurationRejectsInvalidRelayPaths(string relayPath)
    {
        var fields = new Dictionary<string, object>
        {
            ["host"] = Config.Host, ["machineId"] = Config.MachineId, ["relayPath"] = relayPath
        };
        AtomicFile.Write(ConfigPath, JsonSerializer.SerializeToUtf8Bytes(fields, Protocol.Json));
        Assert.Throws<InvalidDataException>(() => RemoteConfiguration.Load(ConfigPath));
    }

    [Fact]
    public void RelayInstallationRequiresBinaryBesideConfigurator()
    {
        var expected = Path.Combine(root, "AgentSignaler.Relay.exe");
        var error = Assert.Throws<InvalidDataException>(() => RemotePaths.ValidateRelayInstallation(root, null));
        Assert.Contains(expected, error.Message);
        Assert.False(Directory.Exists(root));
        Directory.CreateDirectory(expected);
        Assert.Throws<InvalidDataException>(() => RemotePaths.ValidateRelayInstallation(root, expected));
        Directory.Delete(expected);
        AtomicFile.Write(expected, []);
        Assert.Equal(expected, RemotePaths.ValidateRelayInstallation(root, null));
        Assert.Equal(expected, RemotePaths.ValidateRelayInstallation(root, expected.ToUpperInvariant()));
        var elsewhere = Path.Combine(root, "another installation", "AgentSignaler.Relay.exe");
        AtomicFile.Write(elsewhere, []);
        error = Assert.Throws<InvalidDataException>(() => RemotePaths.ValidateRelayInstallation(root, elsewhere));
        Assert.Contains(expected, error.Message);
    }

    [Fact]
    public void PreviewRejectsConflictingConfiguredRelayLocation()
    {
        var config = Config with { RelayPath = Path.Combine(root, "another installation", "AgentSignaler.Relay.exe") };
        Assert.Throws<InvalidDataException>(() => IntegrationManager.Preview(config, ConfigPath,
            Path.Combine(root, "home"), Path.Combine(root, "AgentSignaler.Relay.exe")));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public async Task ApplyRechecksRelayPresenceBeforeChangingIntegration()
    {
        var plan = Plan();
        AtomicFile.Write(plan.RelayPath, []);
        RemotePaths.ValidateRelayInstallation(Path.GetDirectoryName(plan.RelayPath)!, plan.Config.RelayPath);
        File.Delete(plan.RelayPath);
        var scheduler = new FakeScheduler();
        var manager = new IntegrationManager(scheduler, (_, _) => throw new InvalidOperationException("Delivery must not run."));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.ApplyAsync(plan, CancellationToken.None));
        Assert.Contains(plan.RelayPath, error.Message);
        Assert.False(File.Exists(ConfigPath));
        Assert.False(File.Exists(plan.HookPath));
        Assert.Null(scheduler.ReadXml(plan.TaskName));
        Assert.False(File.Exists(Path.Combine(root, "integration.json")));
    }

    [Fact]
    public void Version2ContainsOnlyCanonicalEndpointAndOriginalMetadata()
    {
        var config = Config.WithDashboardUrl("https://DASHBOARD:443");
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));
        Assert.Equal(new[] { "clientVersion", "dashboardBaseUrl", "machineId", "machineName", "version" },
            document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("https://dashboard/", document.RootElement.GetProperty("dashboardBaseUrl").GetString());
        Assert.Equal(1, Protocol.Version);
    }

    [Fact]
    public void LoadingLegacyConfigurationDoesNotMigrateFiles()
    {
        SaveConfig();
        var original = File.ReadAllBytes(ConfigPath);
        var loaded = RemoteConfiguration.Load(ConfigPath);
        Assert.Equal(Config, loaded);
        Assert.Equal("http://localhost:51820/", loaded.BaseUri.AbsoluteUri);
        var plan = Plan();
        Assert.Equal(3, plan.Config.Version);
        Assert.Equal(Config.MachineId, plan.Config.MachineId);
        Assert.Equal(original, File.ReadAllBytes(ConfigPath));
    }

    [Theory]
    [InlineData("\"version\":3,\"host\":\"localhost\",\"port\":51820")]
    [InlineData("\"version\":1,\"host\":\"localhost\",\"dashboardBaseUrl\":null")]
    [InlineData("\"version\":2,\"dashboardBaseUrl\":\"https://dashboard/\",\"host\":null")]
    [InlineData("\"version\":2,\"dashboardBaseUrl\":\"https://dashboard/\",\"port\":51820")]
    [InlineData("\"version\":2,\"dashboardBaseUrl\":\"https://dashboard/\",\"PORT\":null")]
    [InlineData("\"version\":2,\"dashboardBaseUrl\":null")]
    [InlineData("\"version\":2,\"dashboardBaseUrl\":\"https://dashboard\"")]
    [InlineData("\"version\":1,\"host\":\"localhost\",\"version\":2")]
    [InlineData("\"version\":1,\"host\":\"localhost\",\"unknown\":1")]
    public void ConfigurationRejectsMixedUnknownAndAmbiguousSchemas(string fields)
    {
        AtomicFile.Write(ConfigPath, Encoding.UTF8.GetBytes(
            $"{{{fields},\"machineId\":\"{Config.MachineId}\"}}"));
        Assert.Throws<InvalidDataException>(() => RemoteConfiguration.Load(ConfigPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyMigrationUsesApprovedApplyAndPreservesRollback(bool delivered)
    {
        SaveConfig();
        var legacy = File.ReadAllBytes(ConfigPath);
        var statePath = RemotePaths.State(ConfigPath);
        AtomicFile.Write(statePath, Encoding.UTF8.GetBytes("state-is-not-part-of-endpoint-migration"));
        var plan = IntegrationManager.Preview(Config.WithDashboardUrl("https://dashboard"), ConfigPath,
            Path.Combine(root, "home"), Path.Combine(root, "AgentSignaler.Relay.exe"));
        AtomicFile.Write(plan.RelayPath, []);
        var scheduler = new FakeScheduler();
        var manager = new IntegrationManager(scheduler, (_, _) => Task.FromResult(delivered));
        if (delivered)
        {
            await manager.ApplyAsync(plan, CancellationToken.None);
            Assert.Equal(plan.Config, RemoteConfiguration.Load(ConfigPath));
            Assert.Equal(Config.MachineId, RemoteConfiguration.Load(ConfigPath).MachineId);
            manager.Uninstall(ConfigPath);
            Assert.False(File.Exists(ConfigPath));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ApplyAsync(plan, CancellationToken.None));
            Assert.Equal(legacy, File.ReadAllBytes(ConfigPath));
            Assert.False(File.Exists(plan.HookPath));
            Assert.Null(scheduler.ReadXml(plan.TaskName));
        }
        if (delivered)
            Assert.Equal(legacy, File.ReadAllBytes(Assert.Single(Directory.GetFiles(root, "remote.json.agent-signaler.*.backup"),
                path => File.ReadAllBytes(path).AsSpan().SequenceEqual(legacy))));
        else Assert.Empty(Directory.GetFiles(root, "remote.json.agent-signaler.*.backup"));
        Assert.Equal("state-is-not-part-of-endpoint-migration", File.ReadAllText(statePath));
    }

    [Fact]
    public async Task FailedOwnedLegacyMigrationRestoresManifestHooksAndTaskBytes()
    {
        var scheduler = new FakeScheduler();
        var oldPlan = Plan();
        AtomicFile.Write(oldPlan.RelayPath, []);
        await new IntegrationManager(scheduler, (_, _) => Task.FromResult(true)).ApplyAsync(oldPlan, CancellationToken.None);
        var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(Config, Protocol.Json);
        AtomicFile.Write(ConfigPath, legacyBytes);
        var manifestPath = Path.Combine(root, "integration.json");
        var legacyManifest = JsonSerializer.Deserialize<IntegrationManifest>(File.ReadAllBytes(manifestPath), Protocol.Json)! with
        { ConfigHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(legacyBytes)) };
        AtomicFile.Write(manifestPath, JsonSerializer.SerializeToUtf8Bytes(legacyManifest, Protocol.Json));
        scheduler.Write(oldPlan.TaskName, LegacyTaskXml);
        var originals = new[] { ConfigPath, oldPlan.HookPath, Path.Combine(root, "integration.json") }
            .ToDictionary(path => path, File.ReadAllBytes);
        var oldTask = scheduler.ReadXml(oldPlan.TaskName);
        var update = IntegrationManager.Preview(RemoteConfiguration.Load(ConfigPath).WithDashboardUrl("https://dashboard"),
            ConfigPath, Path.Combine(root, "home"), oldPlan.RelayPath);
        var failing = new IntegrationManager(scheduler, (_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ApplyAsync(update, CancellationToken.None));
        foreach (var original in originals) Assert.Equal(original.Value, File.ReadAllBytes(original.Key));
        Assert.Equal(oldTask, scheduler.ReadXml(oldPlan.TaskName));
        Assert.Equal(1, RemoteConfiguration.Load(ConfigPath).Version);
        failing.Uninstall(ConfigPath);
        Assert.False(File.Exists(ConfigPath));
    }

    [Theory]
    [InlineData("http://localhost:51820", false, false)]
    [InlineData("https://dashboard", true, false)]
    [InlineData("http://localhost:51820", false, true)]
    [InlineData("https://dashboard", true, true)]
    public void SharedTransportHasExplicitAnonymousPolicy(string url, bool useProxy, bool interactive)
    {
        using var handler = RemoteHttpTransport.CreateHandler(Config.WithDashboardUrl(url), interactive);
        Assert.Equal(useProxy, handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.Credentials);
        Assert.Null(handler.DefaultProxyCredentials);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(handler.SslOptions.ClientCertificates);
        Assert.Equal(interactive ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(700), handler.ConnectTimeout);
        using var client = RemoteHttpTransport.CreateClient(Config.WithDashboardUrl(url), interactive);
        Assert.Equal(interactive ? DashboardConnection.TestTimeout : TimeSpan.FromMilliseconds(1500), client.Timeout);
    }

    [Fact]
    public async Task HttpsHealthAndStatusAreAnonymousJsonRequests()
    {
        var config = Config.WithDashboardUrl("https://dashboard");
        var requests = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests++;
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("dashboard", request.RequestUri.Host);
            Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("X-Tunnel-Authorization"));
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal(request.Method == HttpMethod.Get ? "/health" : "/api/v1/status", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(request.Method == HttpMethod.Get ? HttpStatusCode.OK : HttpStatusCode.Accepted)
            {
                Content = new StringContent("{\"protocolVersion\":1,\"status\":\"ok\"}", Encoding.UTF8, "application/json")
            });
        }));
        await DashboardConnection.TestAsync(config, http, CancellationToken.None);
        Assert.True(await new RelayEngine(http).SendAsync(config, Request(), CancellationToken.None));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("text/html", "<html>Sign in</html>")]
    [InlineData("application/json", "<html>Interstitial</html>")]
    [InlineData("application/json", "{")]
    [InlineData("text/plain", "{\"protocolVersion\":1,\"status\":\"ok\"}")]
    public async Task HealthRejectsInterstitialAndMalformedResponses(string contentType, string body)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, contentType)
        })));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DashboardConnection.TestAsync(Config.WithDashboardUrl("https://dashboard"), client, CancellationToken.None));
    }

    [Fact]
    public async Task HealthRejectsOversizedJsonResponse()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string(' ', 4097), Encoding.UTF8, "application/json")
        })));
        await Assert.ThrowsAsync<InvalidDataException>(() => DashboardConnection.TestAsync(Config, client, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task RelayDoesNotRetryAuthenticationRedirectOrValidationFailures(HttpStatusCode status)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(status));
        }));
        Assert.False(await new RelayEngine(client).SendAsync(Config.WithDashboardUrl("https://dashboard"), Request(), CancellationToken.None));
        Assert.Equal(1, requests);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            DashboardConnection.TestAsync(Config, client, CancellationToken.None));
        Assert.Equal(status, exception.StatusCode);
        Assert.NotEmpty(DashboardConnection.DescribeFailure(exception));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, 1, 2)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 60, 1)]
    public async Task RetryAfterIsHonoredOnlyWithinBudget(HttpStatusCode status, int seconds, int expectedRequests)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            var response = new HttpResponseMessage(requests == 1 ? status : HttpStatusCode.Accepted);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
            return Task.FromResult(response);
        }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(expectedRequests == 2, await new RelayEngine(client).SendAsync(Config, Request(), CancellationToken.None));
        Assert.Equal(expectedRequests, requests);
        if (expectedRequests == 2) Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(seconds));
        else Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TlsFailureIsNeverRetried()
    {
        var requests = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            throw new HttpRequestException(HttpRequestError.SecureConnectionError);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new RelayEngine(client).SendAsync(Config.WithDashboardUrl("https://dashboard"), Request(), CancellationToken.None));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "DNS")]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS")]
    [InlineData(HttpRequestError.ProxyTunnelError, "proxy")]
    public void ConnectionDiagnosticsUseSafeCategories(HttpRequestError error, string category)
    {
        var message = DashboardConnection.DescribeFailure(new HttpRequestException(error, "PRIVATE-ENDPOINT-DETAIL"));
        Assert.Contains(category, message);
        Assert.DoesNotContain("PRIVATE-ENDPOINT-DETAIL", message);
    }

    [Fact]
    public void DataOverrideRequiresAbsolutePathWithoutCreatingAnything()
    {
        Assert.Throws<InvalidDataException>(() => RemotePaths.ResolveDirectory("relative-data"));
        Assert.Equal(Path.GetFullPath(root), RemotePaths.ResolveDirectory(root));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void UnexpectedProgrammingErrorsAreNotSwallowed()
    {
        Assert.False(RemoteFailure.IsExpected(new NullReferenceException()));
        Assert.False(RemoteFailure.IsExpected(new OutOfMemoryException()));
        Assert.False(RemoteFailure.IsExpected(new AggregateException(new NullReferenceException())));
        Assert.True(RemoteFailure.IsExpected(new IOException()));
        Assert.True(RemoteFailure.IsExpected(new InvalidDataException()));
        Assert.True(RemoteFailure.IsExpected(new AggregateException(new IOException())));
    }

    private IntegrationPlan Plan() => IntegrationManager.Preview(Config, ConfigPath, Path.Combine(root, "home"),
        Path.Combine(root, "installed app", "AgentSignaler.Relay.exe"));

    private string LegacyTaskXml => $"""
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>AgentSignaler integration 9e40ba11-c2c8-4212-bbfb-c051c12cfe3f</Description></RegistrationInfo>
          <Triggers><TimeTrigger><Repetition><Interval>PT1M</Interval></Repetition><StartBoundary>2025-01-01T00:00:00</StartBoundary></TimeTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>S-1-5-21-1234</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Actions Context="Author"><Exec><Command>{System.Security.SecurityElement.Escape(Plan().RelayPath)}</Command><Arguments>heartbeat --config {System.Security.SecurityElement.Escape(ScheduledTaskDefinition.QuoteArgument(ConfigPath))}</Arguments></Exec></Actions>
        </Task>
        """;
    private StatusRequest Request() => new()
    {
        MachineId = Config.MachineId, MachineName = "test", ClientVersion = "1.0.80",
        EventId = Guid.NewGuid(), Event = AgentEvent.SessionStart, ReportedAtUtc = now,
        SessionId = "session"
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class FakeScheduler : IIntegrationTaskScheduler
    {
        private readonly Dictionary<string, string> tasks = [];
        public bool FailNextDelete { get; set; }
        public int Writes { get; private set; }
        public int Deletes { get; private set; }
        public string? ReadXml(string name) => tasks.GetValueOrDefault(name);
        public void Write(string name, string xml)
        {
            Writes++;
            tasks[name] = xml;
        }
        public void Delete(string name)
        {
            Deletes++;
            if (FailNextDelete) { FailNextDelete = false; throw new IOException("Simulated failure."); }
            tasks.Remove(name);
        }
    }
}
