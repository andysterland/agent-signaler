using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentSignaler.Tunneling;
using Xunit;

namespace AgentSignaler.Tunneling.Tests;

public sealed partial class TunnelTests
{
    private const string Account = """{"status":"Logged in","provider":"microsoft","username":"not-persisted","tenantId":"11111111-1111-1111-1111-111111111111","objectId":"22222222-2222-2222-2222-222222222222"}""";
    private const string Acl = """[{"type":"Anonymous","subjects":[],"scopes":["connect"]}]""";
    private const int Port = 51839;
    private const string Marker = "33333333333333333333333333333333";
    private const string VersionOutput = """
        Tunnel CLI version: 1.0.2030+fc9273aa0f

        Tunnel service URI        : https://global.rel.tunnels.api.visualstudio.com/
        """;

    [Theory]
    [InlineData("")]
    [InlineData("Welcome to dev tunnels!\nCLI version: 1.0.2030+fc9273aa0f\nlicense privacy\n")]
    public void QualifiedVersionMetadataAndWelcomeBannerAreAccepted(string banner) =>
        TunnelValidation.VerifyCliVersion(new(0, banner + VersionOutput, ""));

    [Theory]
    [InlineData("1.0.2030+fc9273aa0f")]
    [InlineData("CLI version: 1.0.2030+fc9273aa0f")]
    [InlineData("Tunnel CLI version: 1.0.9999+unknown")]
    [InlineData("Tunnel CLI version: 1.0.2030+fc9273aa0f\nTunnel CLI version: 1.0.9999+unknown")]
    [InlineData("CLI version: 1.0.9999+unknown\nTunnel CLI version: 1.0.2030+fc9273aa0f")]
    public void UnknownOrConflictingVersionOutputIsRejected(string output) =>
        Assert.Equal(TunnelState.Unsupported,
            Assert.Throws<TunnelException>(() => TunnelValidation.VerifyCliVersion(new(0, output, ""))).State);

    [Fact]
    public void OwnerUsesStableIdentityNotUsername()
    {
        var owner = TunnelValidation.AccountOwner(Account);
        Assert.Equal(64, owner.Length);
        Assert.Equal(owner, TunnelValidation.AccountOwner(Account.Replace("not-persisted", "renamed")));
        Assert.NotEqual(owner, TunnelValidation.AccountOwner(Account.Replace("22222222", "44444444")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("""{"status":"Logged in","provider":"github","username":"octocat"}""")]
    [InlineData("""{"status":"Logged in","provider":"microsoft","tenantId":"bad","objectId":"bad"}""")]
    [InlineData("""{"status":"Logged in","status":"Logged out","provider":"microsoft"}""")]
    public void UnknownAccountSchemaIsRejected(string json) =>
        Assert.Throws<TunnelException>(() => TunnelValidation.AccountOwner(json));

    [Fact]
    public void LoggedOutIsDistinct() =>
        Assert.Equal(TunnelState.AccountRequired, Assert.Throws<TunnelException>(
            () => TunnelValidation.AccountOwner("""{"status":"Not logged in"}""")).State);

    [Theory]
    [InlineData("http://foo-51839.usw2.devtunnels.ms/")]
    [InlineData("https://foo-51839-inspect.usw2.devtunnels.ms/")]
    [InlineData("https://foo-51839.usw2.devtunnels.ms/health")]
    [InlineData("https://foo-51839.usw2.devtunnels.ms/?token=secret")]
    [InlineData("https://foo-51839.usw2.devtunnels.ms/#fragment")]
    [InlineData("https://user:pass@foo-51839.usw2.devtunnels.ms/")]
    [InlineData("https://foo-51839.usw2.devtunnels.ms:444/")]
    [InlineData("https://example.com/")]
    [InlineData("https://localhost/")]
    [InlineData("https://foo-51839.usw2.devtunnels.ms/./")]
    [InlineData("https://foo-51839.usw2.devtunnels.ms/other/..")]
    public void UnsafeUrlsRejected(string url) =>
        Assert.Throws<TunnelException>(() => TunnelValidation.ValidatePublicUrl(url));

    [Fact]
    public void OnlyExactHostingPortIsSelected()
    {
        var parser = new TunnelHostOutputParser("custom.usw2", Port);
        Assert.Null(parser.Feed("Hosting port: 123"));
        Assert.Null(parser.Feed("Connect via browser: https://foo-123.usw2.devtunnels.ms"));
        Assert.Null(parser.Feed("Hosting port: 51839"));
        Assert.Null(parser.Feed("Inspect network activity: https://foo-51839-inspect.usw2.devtunnels.ms"));
        Assert.Null(parser.Feed("Connect via browser: https://foo-51839.usw2.devtunnels.ms"));
        Assert.Equal("https://foo-51839.usw2.devtunnels.ms/",
            parser.Feed("Ready to accept connections for tunnel: custom.usw2")!.AbsoluteUri);
    }

    [Fact]
    public void QualifiedLiveHostFixtureUsesBrowserUrlRatherThanCustomTunnelId()
    {
        var parser = new TunnelHostOutputParser("agentsignaler-proof-f170634e4738.usw2", Port);
        string[] lines =
        [
            "Connection to host tunnel relay restored.",
            "Hosting port: 51839",
            "Connect via browser: https://0mqhhmls-51839.usw2.devtunnels.ms",
            "Inspect network activity: https://0mqhhmls-51839-inspect.usw2.devtunnels.ms",
            ""
        ];
        foreach (var line in lines) Assert.Null(parser.Feed(line));
        Assert.Equal(new Uri("https://0mqhhmls-51839.usw2.devtunnels.ms/"),
            parser.Feed("Ready to accept connections for tunnel: agentsignaler-proof-f170634e4738.usw2"));
    }

    [Fact]
    public void BrowserUrlWithoutMatchingPortIsNotReadiness()
    {
        var parser = new TunnelHostOutputParser("custom.usw2", Port);
        Assert.Null(parser.Feed("Connection to host tunnel relay restored."));
        Assert.Null(parser.Feed("Connect via browser: https://sample-51839.usw2.devtunnels.ms"));
        Assert.Null(parser.Feed("Ready to accept connections for tunnel: custom.usw2"));
    }

    [Fact]
    public void HostReadinessForAnotherTunnelIsRejected()
    {
        var parser = new TunnelHostOutputParser("custom.usw2", Port);
        Assert.Throws<TunnelException>(() => parser.Feed("Ready to accept connections for tunnel: unrelated.usw2"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["host"]}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":["x"],"scopes":["connect"]}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["connect","manage"]}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["connect"],"isDeny":true}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["connect"],"isInverse":true}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["connect"],"provider":"microsoft"}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["connect"],"futureFlag":false}]""")]
    [InlineData("""[{"type":"Anonymous","subjects":[],"scopes":["connect"],"scopes":["manage"]}]""")]
    public void AclDriftFailsClosed(string acl) =>
        Assert.Throws<TunnelException>(() => TunnelValidation.VerifyPortAcl("""{"accessControlEntries":""" + acl + "}"));

    [Fact]
    public void ExactLeastPrivilegeAclAccepted() =>
        TunnelValidation.VerifyPortAcl("""{"accessControlEntries":""" + Acl + "}");

    [Theory]
    [InlineData("foo")]
    [InlineData("--all.usw2")]
    [InlineData("foo.usw2 --all")]
    [InlineData("foo.usw2\n")]
    public void InvalidFullIdsRejected(string id) =>
        Assert.Throws<TunnelException>(() => TunnelValidation.ValidateId(id));

    [Fact]
    public async Task CompleteFlowPersistsIntentBeforeCreateAndOnlyCopiesVerifiedUrl()
    {
        var fake = new FakeRunner();
        var saved = new List<TunnelIdentity>();
        var states = new List<TunnelStatus>();
        fake.BeforeCreate = () => Assert.NotNull(saved[^1].PendingTunnelId);
        var health = new FakeProbe();
        await using var controller = Create(fake, health, saved);
        controller.StatusChanged += (_, status) => states.Add(status);
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.True(controller.Status.CanCopy);
        Assert.True(controller.SupportsAutomaticResume);
        Assert.EndsWith(".usw2", controller.Identity.TunnelId);
        Assert.Null(controller.Identity.PendingTunnelId);
        Assert.Equal(2, health.Calls.Count);
        Assert.All(states.Where(x => x.State != TunnelState.Connected), x => Assert.False(x.CanCopy));
        Assert.Contains(fake.Commands, x => x.SequenceEqual(new[] { "access", "create", fake.Id!, "--port-number", "51839", "--anonymous", "--scopes", "connect", "--json" }));
        Assert.DoesNotContain(fake.Commands, x => x.Contains("--allow-anonymous") || x.Contains("token") || x.Contains("login"));
        await controller.StopAsync();
        Assert.True(fake.Host.Disposed);
        Assert.False(controller.Status.CanCopy);
        Assert.NotNull(controller.Identity.TunnelId);
    }

    [Fact]
    public async Task ExplicitAccountCheckBindsWithoutCreatingOrHosting()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake);
        var checkedStatus = await controller.CheckAccountAsync();
        Assert.Equal(checkedStatus, controller.Status);
        Assert.Equal(TunnelState.Stopped, controller.Status.State);
        Assert.NotEmpty(controller.Identity.OwnerHash);
        Assert.Equal(controller.Status, controller.Snapshot);
        Assert.Equal(2, fake.Commands.Count);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "create" || command[0] == "host" || command.Contains("login"));
    }

    [Fact]
    public async Task ExplicitPortOverloadSupportsReceiverStartedByGui()
    {
        var fake = new FakeRunner();
        await using var controller = new CliTunnelController(
            Options() with { Port = 51820 }, (_, _) => Task.CompletedTask, fake, new FakeProbe());
        await controller.StartAsync(Port);
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.Contains(fake.Commands, command => command.Contains("51839"));
    }

    [Fact]
    public async Task QualifiedNewTunnelOmitsPortsUntilReceiverPortIsCreated()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake);
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.Single(fake.Commands, command => command.Take(2).SequenceEqual(new[] { "port", "create" }));
    }

    [Fact]
    public async Task EstablishedTunnelWithOmittedPortsIsDriftNotPermissionToRewrite()
    {
        var fake = new FakeRunner { Id = "existing.usw2" };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.DoesNotContain(fake.Commands, command => command.Contains("create") || command[0] == "host");
    }

    [Fact]
    public async Task PortChangeRequiresExplicitDeletionWithActionableMessage()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true, Drift = "port" };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.Contains("Explicitly delete the old tunnel", controller.Status.Message);
        Assert.Equal("existing.usw2", controller.Identity.TunnelId);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "delete" || command.Contains("create"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NullOrMissingFinalPortsNeverBecomeConnected(bool nullPorts, bool omitFinal)
    {
        var fake = new FakeRunner { NullPorts = nullPorts, OmitPortsAlways = omitFinal };
        await using var controller = Create(fake);
        await controller.StartAsync();
        Assert.False(controller.Status.CanCopy);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "host");
    }

    [Fact]
    public async Task DeletedResourceAllowsExplicitAccountRebinding()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true, DeleteConfirmsAbsence = true };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        var original = controller.Identity.OwnerHash;
        await controller.DeleteAsync();
        fake.Drift = "owner";
        var result = await controller.CheckAccountAsync();
        Assert.Equal(TunnelState.Stopped, result.State);
        Assert.NotEqual(original, controller.Identity.OwnerHash);
        Assert.Null(controller.Identity.TunnelId);
    }

    [Fact]
    public async Task PendingIntentStillPreventsAccountRebinding()
    {
        var fake = new FakeRunner { Drift = "owner" };
        await using var controller = Create(fake, identity: Bound(null, "pending"));
        var original = controller.Identity.OwnerHash;
        await controller.CheckAccountAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.Equal(original, controller.Identity.OwnerHash);
    }

    [Fact]
    public async Task PeriodicHealthFailureAndRecoveryReverifyTheSameHost()
    {
        var fake = new FakeRunner();
        var probe = new FakeProbe();
        await using var controller = Create(fake, probe, fastMonitor: true);
        await controller.StartAsync();
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reverified = false;
        controller.StatusChanged += (_, status) =>
        {
            if (status.State == TunnelState.Reconnecting) reconnecting.TrySetResult();
            if (status.State == TunnelState.Verifying) reverified = true;
            if (status.State == TunnelState.Connected) recovered.TrySetResult();
        };
        probe.FailPublic = true;
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controller.Status.CanCopy);
        await controller.StartAsync();
        Assert.Single(fake.Commands, command => command[0] == "host");
        Assert.False(fake.Host.Disposed);
        probe.FailPublic = false;
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(reverified);
        Assert.True(controller.Status.CanCopy);
        Assert.Single(fake.Commands, command => command[0] == "host");
    }

    [Fact]
    public async Task PeriodicProbeTimeoutClearsCopyWithoutRestartingHost()
    {
        var fake = new FakeRunner();
        var probe = new FakeProbe();
        await using var controller = Create(fake, probe, fastMonitor: true);
        await controller.StartAsync();
        var reconnecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, status) => { if (status.State == TunnelState.Reconnecting) reconnecting.TrySetResult(); };
        probe.OnPublic = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        await reconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(controller.Status.PublicUrl);
        Assert.False(fake.Host.Disposed);
        Assert.Single(fake.Commands, command => command[0] == "host");
    }

    [Fact]
    public async Task StopDuringPeriodicProbeCannotPublishStaleConnected()
    {
        var fake = new FakeRunner();
        var probe = new FakeProbe();
        await using var controller = Create(fake, probe, fastMonitor: true);
        await controller.StartAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.OnPublic = _ => { entered.TrySetResult(); return release.Task; };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.StopAsync();
        release.TrySetResult();
        await Task.Delay(150);
        Assert.Equal(TunnelState.Stopped, controller.Status.State);
        Assert.False(controller.Status.CanCopy);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task ChangedPostReadyUrlFaultsAndStopsOwnedHost()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake, fastMonitor: true);
        await controller.StartAsync();
        fake.OutputLine!("Hosting port: 51839");
        fake.OutputLine("Connect via browser: https://different-51839.usw2.devtunnels.ms");
        await controller.MonitoringCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.False(controller.Status.CanCopy);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task ChangedUrlDuringStartupProbeNeverPublishesConnected()
    {
        var fake = new FakeRunner();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe { OnPublic = _ => release.Task };
        await using var controller = Create(fake, probe);
        var publishedConnected = false;
        controller.StatusChanged += (_, snapshot) => publishedConnected |= snapshot.State == TunnelState.Connected;
        var startup = controller.StartAsync();
        await probe.PublicEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fake.OutputLine!("Connect via browser: https://changed-51839.usw2.devtunnels.ms");
        release.TrySetResult();
        await startup;
        Assert.False(publishedConnected);
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task ProcessMonitorFailureIsVisibleInsteadOfSilentlyDiscarded()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake, fastMonitor: true);
        await controller.StartAsync();
        fake.Host.Exit.TrySetException(new IOException("Owned process pipe failed."));
        await controller.MonitoringCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task UnexpectedMonitorExceptionIsSurfacedAndSharingStops()
    {
        var fake = new FakeRunner();
        var probe = new FakeProbe();
        var controller = Create(fake, probe, fastMonitor: true);
        await controller.StartAsync();
        probe.OnPublic = _ => throw new InvalidOperationException("Programming error in probe");
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.MonitoringCompletion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.True(fake.Host.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task PersistenceProgrammingErrorsAreNotConvertedToSuccessShapedStatus()
    {
        var fake = new FakeRunner();
        await using var controller = new CliTunnelController(Options(),
            (_, _) => throw new InvalidOperationException("Programming error"), fake, new FakeProbe());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "create");
    }

    [Fact]
    public async Task ResumeReusesExactIdentityWithoutCreating()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.DoesNotContain(fake.Commands, x => x[0] == "create" || x.Take(2).SequenceEqual(new[] { "port", "create" }));
        Assert.Equal("existing.usw2", controller.Identity.TunnelId);
    }

    [Fact]
    public async Task PendingRecoveryInspectsExactIntentAndFinishesPrivateConfiguration()
    {
        var fake = new FakeRunner { Id = "pending.usw2" };
        await using var controller = Create(fake, identity: Bound(null, "pending"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.Contains(fake.Commands, x => x.SequenceEqual(new[] { "show", "pending", "--json" }));
        Assert.DoesNotContain(fake.Commands, x => x[0] == "create");
        Assert.Null(controller.Identity.PendingTunnelId);
    }

    [Fact]
    public async Task ConfirmedAbsentPendingIntentRetriesSameCustomId()
    {
        var fake = new FakeRunner { Id = "pending.usw2", Absent = true };
        var saved = new List<TunnelIdentity>();
        fake.BeforeCreate = () => Assert.Equal("pending", saved[^1].PendingTunnelId);
        await using var controller = Create(fake, saved: saved, identity: Bound(null, "pending"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.Equal("pending.usw2", controller.Identity.TunnelId);
        Assert.Equal("pending", Assert.Single(fake.Commands, command => command[0] == "create")[1]);
    }

    [Theory]
    [InlineData(2, "Tunnel not found: pending", true)]
    [InlineData(2, "Welcome banner\nTunnel not found: pending\n", true)]
    [InlineData(2, "Tunnel not found: another", false)]
    [InlineData(2, "Forbidden", false)]
    [InlineData(1, "Tunnel not found: pending", false)]
    [InlineData(2, "Tunnel not found in usw2: pending", false)]
    public void ShortIdAbsenceRequiresExactQualifiedDiagnostic(int exit, string text, bool expected) =>
        Assert.Equal(expected, TunnelValidation.IsConfirmedAbsent(new(exit, text, ""), "pending"));

    [Fact]
    public void CliDiscoveryUsesExplicitThenWinGetThenPrerequisiteWithoutPathSearch()
    {
        Assert.True(Path.IsPathFullyQualified(CliTunnelController.WinGetCliPath));
        Assert.EndsWith(Path.Combine("Microsoft", "WinGet", "Links", "devtunnel.exe"), CliTunnelController.WinGetCliPath);
        Assert.EndsWith(Path.Combine("Programs", "Microsoft Dev Tunnels CLI", "devtunnel.exe"), CliTunnelController.PrerequisiteCliPath);
        Assert.Equal(@"C:\explicit\devtunnel.exe", CliTunnelController.DiscoverCliPath(@"C:\explicit\devtunnel.exe", _ => throw new Exception()));
        Assert.Equal(CliTunnelController.WinGetCliPath, CliTunnelController.DiscoverCliPath(null, _ => true));
        Assert.Equal(CliTunnelController.PrerequisiteCliPath, CliTunnelController.DiscoverCliPath(null, path => path == CliTunnelController.PrerequisiteCliPath));
        Assert.Equal(CliTunnelController.WinGetCliPath, CliTunnelController.DiscoverCliPath(null, _ => false));
    }

    [Fact]
    public async Task UnknownLookupFailureNeverCreatesReplacement()
    {
        var fake = new FakeRunner { FailShow = true };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.DoesNotContain(fake.Commands, x => x[0] == "create" || x[0] == "host");
        Assert.Equal("existing.usw2", controller.Identity.TunnelId);
    }

    [Fact]
    public async Task FailedCreateRetainsIntentAndRetryDoesNotCreateAgain()
    {
        var fake = new FakeRunner { FailCreate = true, FailShow = true };
        await using var controller = Create(fake);
        await controller.StartAsync();
        var intent = controller.Identity.PendingTunnelId;
        Assert.NotNull(intent);
        await controller.StartAsync();
        Assert.Equal(intent, controller.Identity.PendingTunnelId);
        Assert.Single(fake.Commands, x => x[0] == "create");
    }

    [Fact]
    public async Task PersistenceFailurePreventsCloudCreation()
    {
        var fake = new FakeRunner();
        await using var controller = new CliTunnelController(Options(), (_, _) => throw new IOException("disk"),
            fake, new FakeProbe());
        await controller.StartAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.DoesNotContain(fake.Commands, x => x[0] == "create");
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("description")]
    [InlineData("host")]
    [InlineData("tunnelAcl")]
    [InlineData("port")]
    [InlineData("portAcl")]
    [InlineData("version")]
    public async Task UnsafeExistingStateDoesNotHostOrRewrite(string drift)
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true, Drift = drift };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.StartAsync();
        Assert.False(controller.Status.CanCopy);
        Assert.True(controller.Status.State is TunnelState.Faulted or TunnelState.Unsupported);
        Assert.DoesNotContain(fake.Commands, x => x[0] == "host" || x[0] == "create" || x.Contains("create"));
    }

    [Fact]
    public async Task PublicHealthFailureStopsOwnedHostAndNeverPublishesUrl()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake, new FakeProbe { FailPublic = true });
        await controller.StartAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.True(fake.Host.Disposed);
        Assert.Null(controller.Status.PublicUrl);
    }

    [Fact]
    public async Task StopCancelsInFlightStartWithoutStaleConnected()
    {
        var fake = new FakeRunner();
        var health = new FakeProbe { BlockPublic = true };
        await using var controller = Create(fake, health);
        var start = controller.StartAsync();
        await health.PublicEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await controller.StopAsync();
        await start;
        Assert.Equal(TunnelState.Stopped, controller.Status.State);
        Assert.Null(controller.Status.PublicUrl);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task HostExitInvalidatesCopyableUrl()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake);
        await controller.StartAsync();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.StatusChanged += (_, status) => { if (status.State == TunnelState.Faulted) failed.TrySetResult(); };
        fake.Host.Exit.TrySetResult(1);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(controller.Status.CanCopy);
    }

    [Fact]
    public async Task ObserverExceptionsAreSurfacedAndOwnedHostIsStopped()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake);
        controller.StatusChanged += (_, snapshot) =>
        {
            if (snapshot.State == TunnelState.Connected) throw new InvalidOperationException("Broken observer");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync());
        Assert.False(controller.Status.CanCopy);
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.True(fake.Host.Disposed);
    }

    [Fact]
    public async Task DeleteDoesNotClearIdentityWhileResourceStillPresent()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.DeleteAsync();
        Assert.Equal(TunnelState.Faulted, controller.Status.State);
        Assert.Equal("existing.usw2", controller.Identity.TunnelId);
    }

    [Fact]
    public async Task QualifiedDeletionConfirmsAbsenceBeforeClearingIdentity()
    {
        var fake = new FakeRunner { Id = "existing.usw2", PortExists = true, HasAcl = true, DeleteConfirmsAbsence = true };
        var saved = new List<TunnelIdentity>();
        await using var controller = Create(fake, saved: saved, identity: Bound("existing.usw2"));
        await controller.DeleteAsync();
        Assert.Equal(TunnelState.Stopped, controller.Status.State);
        Assert.Null(controller.Identity.TunnelId);
        Assert.Null(controller.Identity.PendingTunnelId);
        Assert.NotEmpty(controller.Identity.OwnerHash);
        Assert.Null(saved[^1].TunnelId);
        Assert.Contains(fake.Commands, command => command.SequenceEqual(new[] { "delete", "existing.usw2", "--force", "--json" }));
    }

    [Fact]
    public async Task AlreadyAbsentTunnelCanBeForgottenWithoutDeletingAnything()
    {
        var fake = new FakeRunner { Id = "existing.usw2", Absent = true };
        await using var controller = Create(fake, identity: Bound("existing.usw2"));
        await controller.DeleteAsync();
        Assert.Null(controller.Identity.TunnelId);
        Assert.DoesNotContain(fake.Commands, command => command[0] == "delete");
    }

    [Fact]
    public async Task ConfirmedMissingFullIdIsRecreatedOnlyAfterPersistingNewIntent()
    {
        var fake = new FakeRunner { Id = "existing.usw2", Absent = true };
        var saved = new List<TunnelIdentity>();
        fake.BeforeCreate = () =>
        {
            Assert.Null(saved[^1].TunnelId);
            Assert.NotNull(saved[^1].PendingTunnelId);
        };
        await using var controller = Create(fake, saved: saved, identity: Bound("existing.usw2"));
        await controller.StartAsync();
        Assert.Equal(TunnelState.Connected, controller.Status.State);
        Assert.NotEqual("existing.usw2", controller.Identity.TunnelId);
        Assert.Contains("Update remote settings", controller.Status.Message);
        Assert.Single(fake.Commands, command => command[0] == "create");
    }

    [Theory]
    [InlineData(2, "Tunnel not found in usw2: existing", "", true)]
    [InlineData(2, "Welcome banner\r\nVersion/license help\r\nTunnel not found in usw2: existing\r\n", "", true)]
    [InlineData(2, "Welcome banner\r\nVersion/license help\r\n", "Tunnel not found in usw2: existing", true)]
    [InlineData(1, "Tunnel not found in usw2: existing", "", false)]
    [InlineData(0, "Tunnel not found in usw2: existing", "", false)]
    [InlineData(2, "Tunnel not found in use2: existing", "", false)]
    [InlineData(2, "Tunnel not found in usw2: other", "", false)]
    [InlineData(2, "Tunnel not found in usw2: existing\nAuthentication failed", "", false)]
    [InlineData(2, "Tunnel not found in usw2: existing", "Network error", false)]
    [InlineData(2, "Resource unavailable", "", false)]
    public void AbsenceRequiresQualifiedExitAndExactRequestedIdentity(int exit, string output, string error, bool expected) =>
        Assert.Equal(expected, TunnelValidation.IsConfirmedAbsent(new(exit, output, error), "existing.usw2"));

    [Fact]
    public async Task LogoutStopsFirstAndKeepsOwnerBinding()
    {
        var fake = new FakeRunner();
        await using var controller = Create(fake);
        await controller.StartAsync();
        fake.BeforeLogout = () => Assert.True(fake.Host.Disposed);
        await controller.LogoutAsync();
        Assert.Equal(TunnelState.AccountRequired, controller.Status.State);
        Assert.NotEmpty(controller.Identity.OwnerHash);
    }

    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\", "\"a\\\\\"")]
    public void NativeCommandArgumentsAreQuoted(string argument, string expected) =>
        Assert.Equal(expected, NativeChild.QuoteArgument(argument));

    [Fact]
    public async Task NativeContainedCommandCapturesBothStreams()
    {
        if (!OperatingSystem.IsWindows()) return;
        var output = new StringBuilder();
        var error = new StringBuilder();
        await using var child = NativeChild.Start(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.WriteLine('hello'); [Console]::Error.WriteLine('error')"], output, error, null, 65536, verifyTrust: false);
        Assert.Equal(0, await child.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("hello", output.ToString());
        Assert.Contains("error", error.ToString());
    }

    [Fact]
    public async Task NativeJobStopsDescendantsWithoutNameBasedKill()
    {
        if (!OperatingSystem.IsWindows()) return;
        var pid = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        await using var parent = NativeChild.Start(powershell,
            ["-NoProfile", "-NonInteractive", "-Command",
             "$p=Start-Process -FilePath \"$env:SystemRoot\\System32\\ping.exe\" -ArgumentList '-n 120 127.0.0.1' -PassThru -WindowStyle Hidden; Write-Output $p.Id; Start-Sleep -Seconds 120"],
            null, null, line => { if (int.TryParse(line, out var value)) pid.TrySetResult(value); }, 65536, verifyTrust: false);
        var childId = await pid.Task.WaitAsync(TimeSpan.FromSeconds(15));
        using var descendant = Process.GetProcessById(childId);
        Assert.False(descendant.HasExited);
        await parent.DisposeAsync();
        await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(descendant.HasExited);
    }

    [Fact]
    public async Task NativeOutputOverflowTerminatesChild()
    {
        if (!OperatingSystem.IsWindows()) return;
        var child = NativeChild.Start(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", "1..10000 | ForEach-Object { [Console]::Out.WriteLine('bounded') }"], new StringBuilder(), null, null, 128, verifyTrust: false);
        await Assert.ThrowsAsync<TunnelException>(() => child.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<TunnelException>(() => child.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task NativeCancellationStopsOnlyOwnedProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        await using var child = NativeChild.Start(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120"],
            null, null, null, 65536, verifyTrust: false);
        child.BindCancellation(cancellation.Token);
        cancellation.Cancel();
        await child.Completion.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NativeRunnerRejectsRelativePathWithoutExecuting()
    {
        var runner = new WindowsTunnelProcessRunner();
        await Assert.ThrowsAsync<TunnelException>(() => runner.RunAsync(
            "devtunnel.exe", ["--version"], TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task CancelledCommandNeverStartsExecutable()
    {
        var runner = new WindowsTunnelProcessRunner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            "not-an-executable", ["--version"], TimeSpan.FromSeconds(1), new CancellationToken(canceled: true)));
    }

    private static TunnelOptions Options(TunnelIdentity? identity = null) =>
        new(@"C:\installed\devtunnel.exe", Port, identity ?? new("", Marker));
    private static TunnelIdentity Bound(string? id, string? pending = null) =>
        new(TunnelValidation.AccountOwner(Account), Marker, id, pending);
    private static CliTunnelController Create(FakeRunner fake, FakeProbe? health = null,
        List<TunnelIdentity>? saved = null, TunnelIdentity? identity = null, bool fastMonitor = false) =>
        new(Options(identity) with
        {
            HealthCheckInterval = fastMonitor ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(20),
            HealthCheckTimeout = fastMonitor ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(15)
        }, (value, _) => { saved?.Add(value); return Task.CompletedTask; }, fake, health ?? new FakeProbe());

    private sealed class FakeProbe : ITunnelHealthProbe
    {
        public readonly List<Uri> Calls = [];
        public bool FailPublic, BlockPublic;
        public Func<CancellationToken, Task>? OnPublic;
        public readonly TaskCompletionSource PublicEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task VerifyAsync(Uri baseUri, CancellationToken cancellationToken)
        {
            Calls.Add(baseUri);
            if (baseUri.Scheme != "https") return;
            PublicEntered.TrySetResult();
            if (FailPublic) throw new TunnelException("Public health failed.");
            if (BlockPublic) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (OnPublic is not null) await OnPublic(cancellationToken);
        }
    }

    private sealed class FakeHost : ITunnelHostProcess
    {
        public bool Disposed;
        public readonly TaskCompletionSource<int> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> Completion => Exit.Task;
        public ValueTask DisposeAsync() { Disposed = true; Exit.TrySetResult(0); return ValueTask.CompletedTask; }
    }

    private sealed class FakeRunner : ITunnelProcessRunner
    {
        public readonly List<string[]> Commands = [];
        public FakeHost Host = new();
        public string? Id;
        public string? Drift;
        public bool PortExists, HasAcl, FailShow, FailCreate, Absent, DeleteConfirmsAbsence;
        public bool OmitPortsAlways, NullPorts;
        public Action<string>? OutputLine;
        public Action? BeforeCreate, BeforeLogout;
        public Task<CliCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var args = arguments.ToArray();
            Commands.Add(args);
            var command = args[0];
            string json;
            if (command == "--version") json = Drift == "version" ? "unknown" : VersionOutput;
            else if (command == "user" && args[1] == "show") json = Drift == "owner" ? Account.Replace("22222222", "55555555") : Account;
            else if (command == "user") { BeforeLogout?.Invoke(); json = ""; }
            else if (command == "create")
            {
                BeforeCreate?.Invoke();
                Id = args[1] + ".usw2";
                if (FailCreate) return Task.FromResult(new CliCommandResult(1, "", "secret raw diagnostic"));
                Absent = PortExists = HasAcl = false;
                json = Tunnel();
            }
            else if (command == "show")
            {
                if (FailShow) return Task.FromResult(new CliCommandResult(1, "", "not found or forbidden or offline"));
                if (Absent)
                {
                    var requested = args[1];
                    var separator = requested.LastIndexOf('.');
                    var diagnostic = separator < 0 ? $"Tunnel not found: {requested}" :
                        $"Tunnel not found in {requested[(separator + 1)..]}: {requested[..separator]}";
                    return Task.FromResult(new CliCommandResult(2,
                        $"Welcome banner\nVersion/license help\n{diagnostic}\n", ""));
                }
                json = Tunnel();
            }
            else if (command == "port")
            {
                if (args[1] == "create") PortExists = true;
                json = """{"port":""" + PortJson() + "}";
            }
            else if (command == "access")
            {
                if (args[1] == "create") HasAcl = true;
                var acl = args.Contains("--port-number") ? (HasAcl ? Acl : "[]") : (Drift == "tunnelAcl" ? Acl : "[]");
                if (Drift == "portAcl" && args.Contains("--port-number")) acl = Acl.Replace("connect", "manage");
                json = """{"accessControlEntries":""" + acl + "}";
            }
            else if (command == "delete")
            {
                Absent = DeleteConfirmsAbsence;
                json = JsonSerializer.Serialize(new { deletedTunnel = Id });
            }
            else throw new InvalidOperationException("Unexpected command");
            return Task.FromResult(new CliCommandResult(0, json, ""));
        }

        public Task<ITunnelHostProcess> StartHostAsync(string executable, IReadOnlyList<string> arguments,
            Action<string> outputLine, CancellationToken cancellationToken)
        {
            Commands.Add(arguments.ToArray());
            Host = new FakeHost();
            OutputLine = outputLine;
            outputLine("Connection to host tunnel relay restored.");
            outputLine("Hosting port: 51839");
            outputLine("Connect via browser: https://sample-51839.usw2.devtunnels.ms");
            outputLine("Inspect network activity: https://sample-51839-inspect.usw2.devtunnels.ms");
            outputLine($"Ready to accept connections for tunnel: {Id}");
            return Task.FromResult<ITunnelHostProcess>(Host);
        }

        private string Tunnel()
        {
            var tunnel = new Dictionary<string, object?>
            {
                ["tunnelId"] = Id ?? "existing.usw2",
                ["description"] = Drift == "description" ? "someone else" : $"AgentSignaler owner:{Marker}",
                ["hostConnections"] = Drift == "host" ? 1 : 0,
                ["accessControl"] = JsonSerializer.Deserialize<JsonElement>(Drift == "tunnelAcl" ? Acl : "[]")
            };
            if (NullPorts) tunnel["ports"] = null;
            else if (PortExists && !OmitPortsAlways) tunnel["ports"] = new[] { JsonSerializer.Deserialize<JsonElement>(PortJson()) };
            return JsonSerializer.Serialize(new { tunnel });
        }

        private string PortJson() => JsonSerializer.Serialize(new
        {
            tunnelId = Id ?? "existing.usw2",
            portNumber = Drift == "port" ? 8080 : Port,
            protocol = "http",
            accessControl = JsonSerializer.Deserialize<JsonElement>(HasAcl ? (Drift == "portAcl" ? Acl.Replace("connect", "manage") : Acl) : "[]")
        });
    }
}
