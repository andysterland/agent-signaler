using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.RpcHost;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;
using Xunit;
using static AgentSignaler.RpcHost.Tests.RpcTransportTests;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcRuntimeTests
{
    [Fact]
    public async Task MutationGuardsApplyToMissingParamsNotificationsAndExplicitNullRequestIds()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var initial = await fixture.CallAsync(socket, "settings.get");
        var host = fixture.Runtime.HostInstanceId;
        var revision = initial.GetProperty("revision").GetString()!;
        var invalidParameters = new object[]
        {
            new { expectedRevision = revision, settings = new { receiveDetailedConversations = true } },
            new { hostInstanceId = host, settings = new { receiveDetailedConversations = true } },
            new { hostInstanceId = Guid.NewGuid(), expectedRevision = revision, settings = new { receiveDetailedConversations = true } },
            new { hostInstanceId = host, expectedRevision = "999999", settings = new { receiveDetailedConversations = true } }
        };
        var requests = new List<string>
        {
            Request("settings.update", "null"),
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":null}"
        };
        requests.AddRange(invalidParameters.Select(parameters =>
            "{\"jsonrpc\":\"2.0\",\"method\":\"settings.update\",\"params\":" +
            JsonSerializer.Serialize(parameters, RpcProtocol.Json) + "}"));
        await SendAsync(socket, "[" + string.Join(',', requests) + "]");
        while (true)
        {
            using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));
            if (response.RootElement.ValueKind != JsonValueKind.Array) continue;
            var entry = Assert.Single(response.RootElement.EnumerateArray());
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("id").ValueKind);
            Assert.Equal(-32602, entry.GetProperty("error").GetProperty("code").GetInt32());
            break;
        }
        var unchanged = await fixture.CallAsync(socket, "settings.get");
        Assert.Equal(revision, unchanged.GetProperty("revision").GetString());
        Assert.False(unchanged.GetProperty("state").GetProperty("saved").GetProperty("receiveDetailedConversations").GetBoolean());
        for (var i = 0; i < invalidParameters.Length; i++)
        {
            var error = await fixture.CallErrorAsync(socket, "settings.update", invalidParameters[i]);
            Assert.Equal(i < 2 ? -32602 : 1004, error.GetProperty("code").GetInt32());
        }
        await SendAsync(socket, Request("settings.update", "null", JsonSerializer.Serialize(new
        {
            hostInstanceId = host, expectedRevision = revision, settings = new { receiveDetailedConversations = true }
        }, RpcProtocol.Json)));
        while (true)
        {
            using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));
            if (!response.RootElement.TryGetProperty("id", out var id)) continue;
            Assert.Equal(JsonValueKind.Null, id.ValueKind);
            var result = response.RootElement.GetProperty("result");
            Assert.NotEqual(revision, result.GetProperty("revision").GetString());
            Assert.True(result.GetProperty("state").GetProperty("saved").GetProperty("receiveDetailedConversations").GetBoolean());
            break;
        }
    }

    [Fact]
    public async Task LifecycleProblemReadyAndShutdownEventsUseSanitizedExplicitWireDtos()
    {
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            StoreFactory = (_, _) => throw new IOException("synthetic raw exception must not escape")
        }, initialize: false);
        using var socket = await fixture.ConnectAsync();
        var initial = await fixture.CallAsync(socket, "system.getStatus");
        Assert.Equal("transportReady", initial.GetProperty("state").GetProperty("lifecycle").GetString());
        await fixture.Runtime.InitializeAsync();
        var events = new Dictionary<string, JsonElement>();
        while (!events.ContainsKey("system.ready") || !events.ContainsKey("system.problem") || !events.ContainsKey("system.changed"))
        {
            using var message = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2)));
            Assert.DoesNotContain("synthetic raw exception", message.RootElement.GetRawText());
            events[message.RootElement.GetProperty("method").GetString()!] = message.RootElement.GetProperty("params").Clone();
        }
        Assert.Equal("degraded", events["system.ready"].GetProperty("state").GetProperty("lifecycle").GetString());
        Assert.True(events["system.ready"].GetProperty("state").GetProperty("initialAttemptCompleted").GetBoolean());
        Assert.Equal(1008, events["system.problem"].GetProperty("code").GetInt32());
        Assert.False(events["system.changed"].TryGetProperty("state", out _));
        var cancelled = await fixture.CallAsync(socket, "system.cancelStartup");
        Assert.Equal("system", cancelled.GetProperty("domain").GetString());
        await fixture.CallAsync(socket, "system.shutdown");
        while (true)
        {
            using var message = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2)));
            if (message.RootElement.GetProperty("method").GetString() != "system.shuttingDown") continue;
            Assert.Equal("stopping", message.RootElement.GetProperty("params").GetProperty("state").GetString());
            break;
        }
    }

    [Fact]
    public async Task ReceiverConnectionTestNotificationContainsNoPeerIdentity()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var receiver = await fixture.CallAsync(socket, "receiver.getStatus");
        Assert.True(receiver.GetProperty("state").GetProperty("running").GetBoolean());
        Assert.Equal(fixture.ReceiverPort, receiver.GetProperty("state").GetProperty("port").GetInt32());
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add(Protocol.ConnectionTestHeader, "1");
        using var health = await client.GetAsync(new Uri(fixture.ReceiverUri, "health"), TestContext.Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        while (true)
        {
            using var message = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2)));
            if (message.RootElement.GetProperty("method").GetString() != "receiver.connectionTestReceived") continue;
            var parameters = message.RootElement.GetProperty("params");
            Assert.Single(parameters.EnumerateObject());
            Assert.Equal(fixture.Runtime.HostInstanceId.ToString("D"), parameters.GetProperty("hostInstanceId").GetString());
            break;
        }
    }

    [Fact]
    public async Task ClockDrivenExpiryInvalidatesWithoutIncomingReportsOrBrowserPolling()
    {
        var clock = new AdjustableClock(DateTimeOffset.UtcNow);
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions { TimeProvider = clock });
        var id = Guid.NewGuid();
        await fixture.Runtime.Store!.AcceptAsync(new StatusRequest
        {
            EventId = Guid.NewGuid(), MachineId = id, MachineName = "synthetic", ClientVersion = "synthetic",
            Event = AgentEvent.AgentStop, ReportedAtUtc = clock.GetUtcNow(), SessionId = "synthetic"
        });
        await fixture.Runtime.RefreshMachinesAsync();
        using var socket = await fixture.ConnectAsync();
        var before = await fixture.CallAsync(socket, "machines.getSessions", new { machineId = id });
        Assert.Equal("succeeded", before.GetProperty("state").GetProperty("items")[0].GetProperty("state").GetString());
        var latency = System.Diagnostics.Stopwatch.StartNew();
        clock.Advance(TimeSpan.FromSeconds(61));
        await ReadMachineInvalidationAsync(socket);
        Assert.InRange(latency.Elapsed.TotalSeconds, 0, 2);
        var after = await fixture.CallAsync(socket, "machines.getSessions", new { machineId = id });
        Assert.Equal("waiting", after.GetProperty("state").GetProperty("items")[0].GetProperty("state").GetString());
        Assert.NotEqual(before.GetProperty("revision").GetString(), after.GetProperty("revision").GetString());
        latency.Restart();
        clock.Advance(TimeSpan.FromMinutes(5));
        // Ignore an already queued collection/entity partner; the next query is made only after
        // an observed poll publication with a newer machine revision.
        var expected = long.Parse(after.GetProperty("revision").GetString()!);
        while (true)
        {
            var change = await ReadMachineInvalidationAsync(socket);
            if (change.TryGetProperty("machineId", out _) && long.Parse(change.GetProperty("revision").GetString()!) > expected) break;
        }
        Assert.InRange(latency.Elapsed.TotalSeconds, 0, 2);
        var offline = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
        Assert.Equal("offline", offline.GetProperty("state").GetProperty("state").GetString());
    }

    private static async Task<JsonElement> ReadMachineInvalidationAsync(ClientWebSocket socket)
    {
        while (true)
        {
            using var message = JsonDocument.Parse(await ReceiveTextAsync(socket, TimeSpan.FromSeconds(2)));
            if (message.RootElement.TryGetProperty("method", out var method) && method.GetString() == "machines.changed")
                return message.RootElement.GetProperty("params").Clone();
        }
    }

    private sealed class AdjustableClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks = initial.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }

    private static SessionSnapshot[] MaximumFittingSessions(Func<int, SessionSnapshot> create)
    {
        var sessions = Enumerable.Range(0, Protocol.MaxSessions).Select(create).ToList();
        while (!PresenceProtocol.FitsSnapshot(sessions)) sessions.RemoveAt(sessions.Count - 1);
        Assert.InRange(sessions.Count, 2, Protocol.MaxSessions);
        Assert.False(PresenceProtocol.FitsSnapshot(sessions.Append(create(sessions.Count))));
        return sessions.ToArray();
    }

    [Fact]
    public async Task MaximumMachineAndSessionFixtureFitsBoundedSummariesAndUsesDecimalCounters()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var now = DateTimeOffset.UtcNow;
        var snapshots = MaximumFittingSessions(index => new SessionSnapshot
        {
            SessionId = index.ToString("D3") + new string('s', 125),
            UnderlyingState = AgentState.Executing, UpdatedAtUtc = now
        });
        for (var i = 0; i < 25; i++)
            await fixture.Runtime.Store!.AcceptAsync(new PresenceReport
            {
                Kind = PresenceKind.Started, EventId = Guid.NewGuid(), MachineId = Guid.NewGuid(),
                MachineName = new string('m', 128), ClientVersion = new string('v', 64),
                Generation = long.MaxValue, Sequence = long.MaxValue, ReportedAtUtc = now,
                HeartbeatIntervalSeconds = 60, Sessions = snapshots
            });
        await fixture.Runtime.RefreshMachinesAsync();
        using var socket = await fixture.ConnectAsync();
        var response = await fixture.CallAsync(socket, "machines.list", new { limit = 250 });
        Assert.Equal(1001, (await fixture.CallErrorAsync(socket, "machines.list", new { limit = 251 })).GetProperty("code").GetInt32());
        var items = response.GetProperty("state").GetProperty("items");
        Assert.Equal(25, items.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("state").GetProperty("nextOffset").ValueKind);
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal(snapshots.Length, item.GetProperty("sessionCount").GetInt32());
            Assert.Equal(long.MaxValue.ToString(), item.GetProperty("generation").GetString());
            Assert.Equal(long.MaxValue.ToString(), item.GetProperty("sequence").GetString());
            var sessions = await fixture.CallAsync(socket, "machines.getSessions", new { machineId = item.GetProperty("machineId").GetString(), limit = 250 });
            Assert.Equal(snapshots.Length, sessions.GetProperty("state").GetProperty("items").GetArrayLength());
        }
    }

    [Fact]
    public async Task NoteMutationBoundaryAndUnknownFieldsAreStrictWithoutChangingStateOnFailure()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var id = Guid.NewGuid();
        await fixture.Runtime.Store!.AcceptAsync(new StatusRequest
        {
            EventId = Guid.NewGuid(), MachineId = id, MachineName = "synthetic", ClientVersion = "synthetic",
            Event = AgentEvent.SessionStart, ReportedAtUtc = DateTimeOffset.UtcNow, SessionId = "synthetic"
        });
        await fixture.Runtime.RefreshMachinesAsync();
        using var socket = await fixture.ConnectAsync();
        var detail = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
        var updated = await fixture.CallAsync(socket, "machines.updateDetails", new
        {
            machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = detail.GetProperty("revision").GetString(),
            note = new string('a', 16384)
        });
        Assert.Equal(16384, updated.GetProperty("state").GetProperty("noteLength").GetInt32());
        var failure = await fixture.CallErrorAsync(socket, "machines.updateDetails", new
        {
            machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = updated.GetProperty("revision").GetString(),
            note = new string('a', 16385)
        });
        Assert.Equal(1001, failure.GetProperty("code").GetInt32());
        var settings = await fixture.CallAsync(socket, "settings.get");
        var unknown = await fixture.CallErrorAsync(socket, "settings.update", new
        {
            hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = settings.GetProperty("revision").GetString(),
            settings = new { unrecognized = true }
        });
        Assert.Equal(-32602, unknown.GetProperty("code").GetInt32());
        Assert.Equal(16384, fixture.Runtime.GetMachine(id).State.Machine.Note!.Length);
    }

    [Fact]
    public async Task RealReceiverSqliteAndRpcMutationsPreservePrivacyRevisionsAndSettings()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var socket = await fixture.ConnectAsync();
        var machine = Guid.NewGuid();
        using var client = new HttpClient();
        using (var accepted = await client.PostAsJsonAsync(new Uri(fixture.ReceiverUri, "api/v1/status"), new StatusRequest
        {
            MachineId = machine, MachineName = "synthetic-workstation", EventId = Guid.NewGuid(),
            ClientVersion = "synthetic", SessionId = "synthetic-session", Event = AgentEvent.SessionStart,
            ReportedAtUtc = DateTimeOffset.UtcNow
        }, Protocol.Json, TestContext.Token))
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        await fixture.Runtime.RefreshMachinesAsync(TestContext.Token);
        var listed = await fixture.CallAsync(socket, "machines.list");
        Assert.Equal(machine.ToString("D"), listed.GetProperty("state").GetProperty("items")[0].GetProperty("machineId").GetString());
        var detail = await fixture.CallAsync(socket, "machines.get", new { machineId = machine });
        var settings = await fixture.CallAsync(socket, "settings.get");
        var changed = await fixture.CallAsync(socket, "machines.updateDetails", new
        {
            machineId = machine, hostInstanceId = fixture.Runtime.HostInstanceId,
            expectedRevision = detail.GetProperty("revision").GetString(), displayName = "Synthetic display", note = "Synthetic note"
        });
        Assert.Equal("Synthetic display", changed.GetProperty("state").GetProperty("displayName").GetString());
        Assert.False(changed.GetProperty("state").TryGetProperty("note", out _));
        Assert.NotEqual(detail.GetProperty("revision").GetString(), changed.GetProperty("revision").GetString());
        var note = await fixture.CallAsync(socket, "machines.getNote", new { machineId = machine });
        Assert.Equal("Synthetic note", note.GetProperty("state").GetProperty("text").GetString());
        var sessions = await fixture.CallAsync(socket, "machines.getSessions", new { machineId = machine });
        Assert.Equal("synthetic-session", sessions.GetProperty("state").GetProperty("items")[0].GetProperty("sessionId").GetString());
        var stale = await fixture.CallErrorAsync(socket, "machines.updateDetails", new
        {
            machineId = machine, hostInstanceId = fixture.Runtime.HostInstanceId,
            expectedRevision = detail.GetProperty("revision").GetString(), note = "must not persist"
        });
        Assert.Equal(1004, stale.GetProperty("code").GetInt32());
        Assert.Equal("notCommitted", stale.GetProperty("data").GetProperty("commitState").GetString());
        var saved = await fixture.CallAsync(socket, "settings.update", new
        {
            hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = settings.GetProperty("revision").GetString(),
            settings = new { rpcPort = FreePort(), autoStartSharing = false }
        });
        Assert.Contains("rpcPort", saved.GetProperty("state").GetProperty("restartRequired").EnumerateArray().Select(e => e.GetString()));
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "dashboard-settings.json")));
        Assert.Equal("Dark", persisted.RootElement.GetProperty("Theme").GetString());
        Assert.True(persisted.RootElement.GetProperty("FutureSetting").GetProperty("preserved").GetBoolean());
        Assert.False(saved.GetProperty("state").GetProperty("saved").TryGetProperty("futureSetting", out _));
        Assert.False(saved.GetProperty("state").GetProperty("saved").TryGetProperty("theme", out _));
        var noConfirmation = await fixture.CallErrorAsync(socket, "machines.remove", new
        {
            machineId = machine, hostInstanceId = fixture.Runtime.HostInstanceId,
            expectedRevision = changed.GetProperty("revision").GetString()
        });
        Assert.Equal(1009, noConfirmation.GetProperty("code").GetInt32());
        await fixture.CallAsync(socket, "machines.remove", new
        {
            machineId = machine, hostInstanceId = fixture.Runtime.HostInstanceId,
            expectedRevision = changed.GetProperty("revision").GetString(), confirmed = true
        });
        Assert.Equal(1002, (await fixture.CallErrorAsync(socket, "machines.get", new { machineId = machine }))
            .GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task MaximumSessionsAndLegacyNoteUseStatelessEntityRevisionPages()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var machine = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var snapshots = MaximumFittingSessions(index => new SessionSnapshot
        {
            SessionId = $"session-{index:D3}", UnderlyingState = AgentState.Waiting,
            UpdatedAtUtc = now.AddTicks(index)
        });
        foreach (var session in snapshots)
            await fixture.Runtime.Store!.AcceptAsync(new StatusRequest
            {
                MachineId = machine, MachineName = "synthetic", ClientVersion = "synthetic", EventId = Guid.NewGuid(),
                SessionId = session.SessionId, Event = AgentEvent.SessionStart, ReportedAtUtc = session.UpdatedAtUtc
            });
        await Assert.ThrowsAsync<CapacityException>(() => fixture.Runtime.Store!.AcceptAsync(new StatusRequest
        {
            MachineId = machine, MachineName = "synthetic", ClientVersion = "synthetic", EventId = Guid.NewGuid(),
            SessionId = $"session-{snapshots.Length:D3}", Event = AgentEvent.SessionStart,
            ReportedAtUtc = now.AddTicks(snapshots.Length)
        }));
        var legacy = new string('n', 16383) + "\U0001F600" + new string('n', 23617) + "\U0001F600";
        await fixture.Runtime.Store!.UpdateDetailsAsync(machine, "Synthetic legacy", legacy);
        await fixture.Runtime.RefreshMachinesAsync();
        using var socket = await fixture.ConnectAsync();
        var first = await fixture.CallAsync(socket, "machines.getSessions", new { machineId = machine, limit = 1 });
        Assert.Equal(snapshots.Length, first.GetProperty("state").GetProperty("totalCount").GetInt32());
        var revision = first.GetProperty("revision").GetString();
        var next = await fixture.CallAsync(socket, "machines.getSessions", new
        {
            machineId = machine, offset = 1, limit = 250, expectedRevision = revision
        });
        Assert.Equal(snapshots.Length - 1, next.GetProperty("state").GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("state").GetProperty("nextOffset").ValueKind);
        var all = "";
        var offset = 0;
        do
        {
            var chunk = await fixture.CallAsync(socket, "machines.getNote", new { machineId = machine, offset, expectedRevision = revision });
            if (offset == 0)
            {
                Assert.Equal(16383, chunk.GetProperty("state").GetProperty("text").GetString()!.Length);
                Assert.Equal(16383, chunk.GetProperty("state").GetProperty("nextOffset").GetInt32());
            }
            all += chunk.GetProperty("state").GetProperty("text").GetString();
            var n = chunk.GetProperty("state").GetProperty("nextOffset");
            offset = n.ValueKind == JsonValueKind.Null ? -1 : n.GetInt32();
        } while (offset >= 0);
        Assert.Equal(legacy, all);
        var splitSurrogate = await fixture.CallErrorAsync(socket, "machines.getNote", new
        {
            machineId = machine, offset = 16384, expectedRevision = revision
        });
        Assert.Equal(1001, splitSurrogate.GetProperty("code").GetInt32());
        await fixture.CallAsync(socket, "machines.updateDetails", new
        {
            machineId = machine, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = revision,
            displayName = "Updated without touching legacy note"
        });
        Assert.Equal(legacy, fixture.Runtime.GetMachine(machine).State.Machine.Note);
        var stale = await fixture.CallErrorAsync(socket, "machines.getNote", new { machineId = machine, offset = 1, expectedRevision = revision });
        Assert.Equal(1004, stale.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task DiagnosticsCaptureUnsavedPathsAndReturnSanitizedTypedState()
    {
        var seen = new List<string?>();
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            Diagnostic = (kind, path, token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (seen) seen.Add(path);
                return Task.FromResult(new PrerequisiteDiagnosticResult(true, "synthetic diagnostic passed"));
            }
        });
        using var socket = await fixture.ConnectAsync();
        var path = Path.Combine(fixture.Directory, "synthetic-devtunnel.exe");
        var checkedState = await fixture.CallAsync(socket, "prerequisites.check", new { id = "devTunnel", path });
        var check = checkedState.GetProperty("state").GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == "devTunnel");
        Assert.Equal(path, check.GetProperty("testedPath").GetString());
        Assert.Equal("passed", check.GetProperty("state").GetString());
        Assert.Contains(path, seen);
        var settings = await fixture.CallAsync(socket, "settings.get");
        Assert.Equal(Path.Combine(fixture.Directory, "synthetic", "devtunnel.exe"),
            settings.GetProperty("state").GetProperty("saved").GetProperty("devTunnelCliPath").GetString());
        var all = await fixture.CallAsync(socket, "prerequisites.checkAll");
        Assert.Equal(4, all.GetProperty("state").GetProperty("checks").GetArrayLength());
        Assert.All(all.GetProperty("state").GetProperty("checks").EnumerateArray(),
            item => Assert.Equal("passed", item.GetProperty("state").GetString()));
        var status = await fixture.CallAsync(socket, "prerequisites.getStatus");
        Assert.Equal(all.GetProperty("revision").GetString(), status.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task StorageFailureKeepsControlAvailableInDegradedMode()
    {
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            StoreFactory = (_, _) => throw new IOException("private raw storage failure")
        });
        using var socket = await fixture.ConnectAsync();
        var status = await fixture.CallAsync(socket, "system.getStatus");
        Assert.Equal("degraded", status.GetProperty("state").GetProperty("lifecycle").GetString());
        Assert.DoesNotContain("private raw", status.GetRawText());
        var settings = await fixture.CallAsync(socket, "settings.get");
        Assert.True(settings.GetProperty("state").TryGetProperty("saved", out _));
    }

    [Fact]
    public async Task ActualReportListenerCannotServeRpcAndControlPortIsNotLanBound()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://localhost");
        var uri = new UriBuilder(fixture.ReceiverUri) { Scheme = "ws", Path = "/rpc" }.Uri;
        var error = await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(uri, TestContext.Token));
        Assert.Contains("404", error.Message);
        var lan = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(ip =>
            ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip));
        if (lan is not null)
        {
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) });
            var rejection = await Record.ExceptionAsync(() =>
                client.GetAsync($"http://{lan}:{fixture.Port}/health", TestContext.Token));
            Assert.True(rejection is HttpRequestException or TaskCanceledException);
            using var report = await client.GetAsync($"http://{lan}:{fixture.ReceiverPort}/health", TestContext.Token);
            Assert.Equal(HttpStatusCode.OK, report.StatusCode);
            using var forbidden = await client.GetAsync($"http://{lan}:{fixture.ReceiverPort}/rpc", TestContext.Token);
            Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        }
    }

    [Fact]
    public async Task WindowsMappingDtoNeverExposesCachedConnectionUri()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        var id = Guid.NewGuid();
        await fixture.Runtime.Store!.AcceptAsync(new StatusRequest
        {
            MachineId = id, MachineName = "synthetic", ClientVersion = "synthetic", EventId = Guid.NewGuid(),
            SessionId = "synthetic", Event = AgentEvent.SessionStart, ReportedAtUtc = DateTimeOffset.UtcNow
        });
        var mapping = new WindowsAppConnection(new("https://synthetic.synthetic.devcenter.azure.com/"),
            "project", "devbox", "synthetic@example.test", Guid.NewGuid(),
            $"ms-cloudpc:connect?cpcid={Guid.NewGuid():D}&username=synthetic%40example.test&environment=prod&version=1&source=test",
            DateTimeOffset.UtcNow);
        await fixture.Runtime.Store.SetWindowsAppConnectionAsync(id, mapping);
        await fixture.Runtime.RefreshMachinesAsync();
        using var socket = await fixture.ConnectAsync();
        var windows = await fixture.CallAsync(socket, "windowsApp.getState", new { machineId = id });
        var machine = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
        foreach (var json in new[] { windows, machine })
        {
            Assert.DoesNotContain("ms-cloudpc", json.GetRawText());
            Assert.DoesNotContain("lastKnownConnectionUri", json.GetRawText());
            Assert.Contains("synthetic@example.test", json.GetRawText());
        }
        Assert.True(windows.GetProperty("state").GetProperty("canOpenLastKnown").GetBoolean());
    }

    internal sealed class RuntimeFixture : IAsyncDisposable
    {
        public string Directory { get; }
        public DashboardRuntime Runtime { get; }
        private readonly DashboardResourceLease lease;
        private readonly RuntimeRpcApplication application;
        private readonly RpcServer server;
        private int requestId;
        public int Port { get; }
        public int ReceiverPort { get; }
        public Uri ReceiverUri => new($"http://127.0.0.1:{ReceiverPort}/");
        private RuntimeFixture(string directory, DashboardResourceLease lease, DashboardRuntime runtime, int port, int receiverPort)
        {
            Directory = directory;
            this.lease = lease;
            Runtime = runtime;
            Port = port;
            ReceiverPort = receiverPort;
            application = new(runtime, () => { });
            server = new(application, port);
        }
        public static async Task<RuntimeFixture> StartAsync(DashboardRuntimeOptions? options = null, bool initialize = true)
        {
            var directory = TestDirectory.Create();
            var lease = DashboardResourceLease.Acquire(directory, directory);
            var port = FreePort();
            var receiver = FreePort();
            using var extra = JsonDocument.Parse("{\"preserved\":true}");
            new DashboardSettings
            {
                Port = receiver, RpcPort = port,
                ConnectionMode = options?.TunnelRunner is null ? DashboardConnectionMode.Lan : DashboardConnectionMode.DevTunnel,
                AutoStartSharing = false, ReceiveDetailedConversations = false, Theme = "Dark",
                AzureCliPath = Path.Combine(directory, "synthetic", "az.exe"),
                DevTunnelCliPath = Path.Combine(directory, "synthetic", "devtunnel.exe"),
                ExtensionData = new Dictionary<string, JsonElement> { ["FutureSetting"] = extra.RootElement.Clone() }
            }.Save(directory);
            var runtime = new DashboardRuntime(lease, options);
            var fixture = new RuntimeFixture(directory, lease, runtime, port, receiver);
            try
            {
                await fixture.server.StartAsync(TestContext.Token);
                if (initialize) await runtime.InitializeAsync(TestContext.Token);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async Task<ClientWebSocket> ConnectAsync()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", "http://localhost");
            try { await socket.ConnectAsync(new($"ws://127.0.0.1:{Port}/rpc"), TestContext.Token); return socket; }
            catch { socket.Dispose(); throw; }
        }
        public async Task<JsonElement> CallAsync(ClientWebSocket socket, string method, object? parameters = null)
        {
            var response = await RequestAsync(socket, method, parameters);
            Assert.False(response.TryGetProperty("error", out _), response.GetRawText());
            return response.GetProperty("result").Clone();
        }
        public async Task<JsonElement> CallErrorAsync(ClientWebSocket socket, string method, object? parameters = null) =>
            (await RequestAsync(socket, method, parameters)).GetProperty("error").Clone();
        private async Task<JsonElement> RequestAsync(ClientWebSocket socket, string method, object? parameters)
        {
            var id = Interlocked.Increment(ref requestId).ToString();
            await SendAsync(socket, Request(method, JsonSerializer.Serialize(id),
                parameters is null ? null : JsonSerializer.Serialize(parameters, RpcProtocol.Json)));
            while (true)
            {
                using var response = JsonDocument.Parse(await ReceiveTextAsync(socket));
                if (response.RootElement.TryGetProperty("id", out var found) && found.GetString() == id)
                    return response.RootElement.Clone();
            }
        }
        public async ValueTask DisposeAsync()
        {
            server.StopAdmission();
            await server.DisposeAsync();
            await Runtime.DisposeAsync();
            application.Dispose();
            lease.Dispose();
            TestDirectory.Delete(Directory);
        }
    }
}

internal static class TestDirectory
{
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentSignaler.slnx")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
        }
    }
    public static string Create()
    {
        var path = Path.Combine(RepositoryRoot, ".rpc-test-work", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
    public static void Delete(string path)
    {
        var cleanup = System.Diagnostics.Stopwatch.StartNew();
        while (System.IO.Directory.Exists(path))
        {
            try { System.IO.Directory.Delete(path, recursive: true); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 &&
                cleanup.Elapsed < TimeSpan.FromSeconds(5))
            {
                // Terminated fixture processes can retain file handles briefly during kernel cleanup.
                Thread.Sleep(25);
            }
        }
        var parent = Path.GetDirectoryName(path)!;
        if (System.IO.Directory.Exists(parent) && !System.IO.Directory.EnumerateFileSystemEntries(parent).Any())
        {
            try { System.IO.Directory.Delete(parent); }
            catch (IOException) { }
        }
    }
}
