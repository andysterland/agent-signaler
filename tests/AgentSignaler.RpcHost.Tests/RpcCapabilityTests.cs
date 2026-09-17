using System.Net.WebSockets;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Dashboard;
using AgentSignaler.RpcHost;
using AgentSignaler.Service;
using Xunit;
using static AgentSignaler.RpcHost.Tests.RpcRuntimeTests;
using static AgentSignaler.RpcHost.Tests.RpcTransportTests;

namespace AgentSignaler.RpcHost.Tests;

public sealed class RpcCapabilityTests
{
    [Fact]
    public async Task CatalogRefreshPagesCurrentStateAndRejectsRevisionChurn()
    {
        var catalog = new SyntheticCatalog();
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions { CatalogService = catalog });
        using var socket = await fixture.ConnectAsync();
        var settings = await fixture.CallAsync(socket, "settings.get");
        var refreshed = await fixture.CallAsync(socket, "devboxes.refresh", new
        {
            hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = settings.GetProperty("revision").GetString()
        });
        Assert.Equal(1000, refreshed.GetProperty("state").GetProperty("totalCount").GetInt32());
        var first = await fixture.CallAsync(socket, "devboxes.getCatalog", new { limit = 250 });
        var revision = first.GetProperty("revision").GetString();
        var offset = 0;
        var names = new List<string>();
        while (true)
        {
            var page = await fixture.CallAsync(socket, "devboxes.getCatalog", new { offset, limit = 250, expectedRevision = revision });
            names.AddRange(page.GetProperty("state").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("devBoxName").GetString()!));
            var next = page.GetProperty("state").GetProperty("nextOffset");
            if (next.ValueKind == JsonValueKind.Null) break;
            offset = next.GetInt32();
        }
        Assert.Equal(1000, names.Count);
        Assert.Equal(1000, names.Distinct().Count());
        Assert.Equal(names.Order(StringComparer.Ordinal), names);
        settings = await fixture.CallAsync(socket, "settings.get");
        await fixture.CallAsync(socket, "devboxes.refresh", new
        {
            hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = settings.GetProperty("revision").GetString()
        });
        var stale = await fixture.CallErrorAsync(socket, "devboxes.getCatalog", new { offset = 250, expectedRevision = revision });
        Assert.Equal(1004, stale.GetProperty("code").GetInt32());
        Assert.Equal(2, catalog.Refreshes);
    }

    [Fact]
    public async Task EveryWindowsAppCommandUsesSharedControllersAndNeverReturnsConnectionUris()
    {
        var resolver = new SyntheticResolver();
        var platform = new SyntheticPlatform();
        var cli = new SyntheticAzure();
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            AzureCli = cli, ConnectionResolver = resolver, WindowsAppPlatform = platform
        });
        var id = await AddMachineAsync(fixture);
        using var socket = await fixture.ConnectAsync();
        var state = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
        var mapped = await fixture.CallAsync(socket, "windowsApp.map", new
        {
            machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = state.GetProperty("revision").GetString(),
            selection = new
            {
                devCenterEndpoint = "https://synthetic.synthetic.devcenter.azure.com/", projectName = "project", devBoxName = "devbox",
                azureAccountUpn = SyntheticAzure.Upn, azureTenantId = SyntheticAzure.Tenant
            }
        });
        Assert.True(mapped.GetProperty("state").GetProperty("canOpenLastKnown").GetBoolean());
        foreach (var method in new[] { "windowsApp.signIn", "windowsApp.refresh", "windowsApp.open", "windowsApp.openLastKnown" })
        {
            state = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
            var result = await fixture.CallAsync(socket, method, new
            {
                machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = state.GetProperty("revision").GetString()
            });
            Assert.DoesNotContain("ms-cloudpc", result.GetRawText());
            Assert.False(result.GetProperty("state").GetProperty("isBusy").GetBoolean());
        }
        Assert.True(cli.Calls > 0);
        Assert.True(resolver.Calls >= 3);
        Assert.Equal(1, platform.Activations);
        Assert.True(platform.Reuses >= 1);
        state = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
        var unconfirmed = await fixture.CallErrorAsync(socket, "windowsApp.clear", new
        {
            machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = state.GetProperty("revision").GetString()
        });
        Assert.Equal(1009, unconfirmed.GetProperty("code").GetInt32());
        var cleared = await fixture.CallAsync(socket, "windowsApp.clear", new
        {
            machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = state.GetProperty("revision").GetString(), confirmed = true
        });
        Assert.False(cleared.GetProperty("state").TryGetProperty("mapping", out _));
        Assert.Null(fixture.Runtime.GetMachine(id).State.Machine.WindowsAppConnection);
    }

    [Fact]
    public async Task CancellationAfterMappingCommitDoesNotClaimRollback()
    {
        var resolver = new SyntheticResolver();
        var platform = new SyntheticPlatform();
        await using var fixture = await RuntimeFixture.StartAsync(new DashboardRuntimeOptions
        {
            ConnectionResolver = resolver, WindowsAppPlatform = platform
        });
        var id = await AddMachineAsync(fixture);
        await fixture.Runtime.Store!.SetWindowsAppConnectionAsync(id, new(
            new("https://synthetic.synthetic.devcenter.azure.com/"), "project", "devbox",
            SyntheticAzure.Upn, SyntheticAzure.Tenant, null, null));
        await fixture.Runtime.RefreshMachinesAsync();
        using var socket = await fixture.ConnectAsync();
        var state = await fixture.CallAsync(socket, "machines.get", new { machineId = id });
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        platform.OnActivate = () => { activated.TrySetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        try
        {
            await SendAsync(socket, Request("windowsApp.open", "\"open\"", JsonSerializer.Serialize(new
            {
                machineId = id, hostInstanceId = fixture.Runtime.HostInstanceId, expectedRevision = state.GetProperty("revision").GetString()
            }, RpcProtocol.Json)));
            await activated.Task.WaitAsync(TestContext.Token);
            await SendAsync(socket, Request("operations.cancel", "\"cancel\"", "{\"requestId\":\"open\"}"));
            using var cancelled = JsonDocument.Parse(await RpcProcessTests.ReceiveUntilIdAsync(socket, "cancel"));
            Assert.Equal("cancelRequested", cancelled.RootElement.GetProperty("result").GetProperty("status").GetString());
            release.Set();
            using var outcome = JsonDocument.Parse(await RpcProcessTests.ReceiveUntilIdAsync(socket, "open"));
            if (outcome.RootElement.TryGetProperty("error", out var error))
                Assert.Equal("committed", error.GetProperty("data").GetProperty("commitState").GetString());
            else Assert.True(outcome.RootElement.GetProperty("result").GetProperty("state").GetProperty("canOpenLastKnown").GetBoolean());
            await fixture.Runtime.RefreshMachinesAsync();
            Assert.NotNull(fixture.Runtime.GetMachine(id).State.Machine.WindowsAppConnection!.LastKnownConnectionUri);
        }
        finally { release.Set(); }
    }

    private static async Task<Guid> AddMachineAsync(RuntimeFixture fixture)
    {
        var id = Guid.NewGuid();
        await fixture.Runtime.Store!.AcceptAsync(new StatusRequest
        {
            MachineId = id, MachineName = "synthetic", ClientVersion = "synthetic", EventId = Guid.NewGuid(),
            SessionId = "synthetic", Event = AgentEvent.SessionStart, ReportedAtUtc = DateTimeOffset.UtcNow
        });
        await fixture.Runtime.RefreshMachinesAsync();
        return id;
    }

    private sealed class SyntheticCatalog : IDevBoxCatalogService
    {
        public int Refreshes;
        public Task<DevBoxCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken,
            Action<DevBoxCatalogProgress>? reportProgress = null, Guid? subscriptionId = null, string? devCenterName = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Refreshes++;
            reportProgress?.Invoke(new(DevBoxCatalogStage.DevBoxes) { DevBoxCount = 1000, PagesRead = 10 });
            return Task.FromResult(new DevBoxCatalogSnapshot(Enumerable.Range(0, 1000).Reverse().Select(i =>
                new DevBoxCatalogItem(new("https://synthetic.synthetic.devcenter.azure.com/"), "project", "pool",
                    $"devbox-{i:D4}", "Running", "Succeeded", "Windows", 8, 32, Guid.NewGuid())).ToArray(),
                SyntheticAzure.Upn, SyntheticAzure.Tenant, DateTimeOffset.UtcNow));
        }
    }

    private sealed class SyntheticAzure : IAzureCliProcess
    {
        public const string Upn = "synthetic@example.test";
        public static readonly Guid Tenant = new("11111111-1111-4111-8111-111111111111");
        public static readonly Guid Subscription = new("22222222-2222-4222-8222-222222222222");
        public int Calls;
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new AzureCliResult(0, JsonSerializer.Serialize(new
            {
                id = Subscription, tenantId = Tenant, state = "Enabled", user = new { type = "user", name = Upn }
            }), ""));
        }
    }

    private sealed class SyntheticResolver : IDevBoxConnectionResolver
    {
        public int Calls;
        public Task<ResolvedDevBoxConnection> ResolveAsync(WindowsAppConnection mapping, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new ResolvedDevBoxConnection(
                new($"ms-cloudpc:connect?cpcid={Guid.NewGuid():D}&username=synthetic%40example.test&environment=prod&version=1&source=test"),
                SyntheticAzure.Upn, SyntheticAzure.Tenant, SyntheticAzure.Subscription, DateTimeOffset.UtcNow));
        }
    }

    private sealed class SyntheticPlatform : IWindowsAppPlatform
    {
        public void MinimizeSessions() => throw new InvalidOperationException("Must not minimize remote connections.");
        public int Activations;
        public int Reuses;
        public Action? OnActivate;
        public bool IsProtocolAvailable() => true;
        public WindowsAppActivationDisposition TryActivateExisting(string devBoxName)
        {
            if (Activations == 0) return WindowsAppActivationDisposition.NoExistingWindow;
            Reuses++;
            return WindowsAppActivationDisposition.ExistingWindowActivated;
        }
        public WindowsAppActivationDisposition Activate(Uri connectionUri, string devBoxName)
        {
            Activations++;
            OnActivate?.Invoke();
            return WindowsAppActivationDisposition.ConnectionUriActivated;
        }
    }
}
