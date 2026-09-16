using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

public sealed class DevBoxCatalogServiceTests
{
    private static readonly Uri Endpoint = new("https://example.region.devcenter.azure.com/");
    private static readonly Uri SecondEndpoint = new("https://second.region.devcenter.azure.com/");
    private static AzureCliResult Account => new(0, ConnectionTestData.Account, "");
    private static Guid SubscriptionId => ConnectionTestData.Subscription;
    private static readonly Guid OtherSubscription = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void RefreshHasNoManualEndpointOverload()
    {
        var method = Assert.Single(typeof(IDevBoxCatalogService).GetMethods());
        Assert.Equal("RefreshAsync", method.Name);
        Assert.Equal([typeof(CancellationToken), typeof(Action<DevBoxCatalogProgress>), typeof(Guid?), typeof(string)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task NamedDevCenterUsesCliArrayAndItemUrisWithoutSubscriptionOrArmDiscovery()
    {
        var item = Item();
        item["uri"] = $"{Endpoint}projects/project.one/users/me/devboxes/devbox-01";
        var json = new JsonArray(item).ToJsonString();
        var cli = new FakeCli(Account, new AzureCliResult(0, json, "")) { AutoDiscover = false };
        var observed = new List<DevBoxCatalogProgress>();

        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add, devCenterName: "test-center");

        Assert.Equal(Endpoint, Assert.Single(snapshot.DevCenterEndpoints));
        Assert.Equal(Endpoint, Assert.Single(snapshot.Items).DevCenterEndpoint);
        Assert.Equal("devbox-01", snapshot.Items[0].DevBoxName);
        Assert.Equal("user@example.com", snapshot.AzureAccountUpn);
        Assert.Equal(2, cli.Commands.Count);
        Assert.Equal("devcenter", Arguments(cli.Commands[1])[0]);
        Assert.Contains("test-center", Arguments(cli.Commands[1]));
        Assert.Equal(DevBoxCatalogStage.DevBoxes, observed[^1].Stage);
        Assert.Null(observed[^1].SubscriptionCount);
        Assert.Equal(1, observed[^1].DevBoxCount);
        Assert.Equal(1, observed[^1].DevCentersCompleted);
    }

    [Fact]
    public async Task NamedDevCenterEmptyArrayIsAnEmptySnapshot()
    {
        var cli = new FakeCli(Account, new AzureCliResult(0, "[]", "")) { AutoDiscover = false };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default, devCenterName: "test-center");
        Assert.Empty(snapshot.Items);
        Assert.Empty(snapshot.DevCenterEndpoints);
        Assert.Equal(2, cli.Commands.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("{\"value\":[]}")]
    public async Task NamedDevCenterRejectsMalformedArrayAndMissingUri(string json)
    {
        var cli = new FakeCli(Account, new AzureCliResult(0, json, "")) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, devCenterName: "test-center"));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(DevBoxCatalogStage.DevBoxes, error.Stage);
    }

    [Theory]
    [InlineData("mailto:user@example.com")]
    [InlineData("file:///C:/secret")]
    [InlineData("https://evil.example/projects/project.one/users/me/devboxes/devbox-01")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/wrong-box")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01?secret=1")]
    public async Task NamedDevCenterRejectsUnsafeOrMismatchedItemUris(string uri)
    {
        var item = Item();
        item["uri"] = uri;
        var cli = new FakeCli(Account, new AzureCliResult(0, new JsonArray(item).ToJsonString(), "")) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, devCenterName: "test-center"));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedDevCenterRejectsDuplicateItemsAndOversizedLists(bool oversized)
    {
        var item = Item();
        item["uri"] = $"{Endpoint}projects/project.one/users/me/devboxes/devbox-01";
        var array = new JsonArray(Enumerable.Range(0, oversized ? DevBoxCatalogService.MaximumItemsPerEndpoint + 1 : 2)
            .Select(_ => item.DeepClone()).ToArray());
        var cli = new FakeCli(Account, new AzureCliResult(0, array.ToJsonString(), "")) { AutoDiscover = false };

        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, devCenterName: "test-center"));

        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
    }

    [Fact]
    public async Task NamedDevCenterCancellationAndInvalidCombinedTargetNeverPublishResults()
    {
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(Account, new AzureCliResult(0, "[]", ""))
        {
            AutoDiscover = false, AfterRun = count => { if (count == 2) cancellation.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token, devCenterName: "test-center"));
        Assert.Equal(2, cli.Commands.Count);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, subscriptionId: OtherSubscription, devCenterName: "test-center"));
        Assert.Equal(2, cli.Commands.Count);
    }

    [Fact]
    public async Task SuppliedSubscriptionBypassesAccountListAndSearchesOnlyThatGuid()
    {
        var cli = new FakeCli(Account, Page([Center(subscription: OtherSubscription)]), Page([Item()]))
        { AutoDiscover = false };
        var observed = new List<DevBoxCatalogProgress>();

        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add, OtherSubscription);

        Assert.Single(snapshot.Items);
        Assert.Equal(3, cli.Commands.Count);
        Assert.Equal(AzureCliCommand.DevCentersUri(OtherSubscription).AbsoluteUri, Arguments(cli.Commands[1])[^1]);
        Assert.DoesNotContain(cli.Commands, command => Arguments(command).Take(2).SequenceEqual(["account", "list"]));
        Assert.Equal(1, observed[^1].SubscriptionCount);
        Assert.Equal(1, observed[^1].SubscriptionsCompleted);
    }

    [Fact]
    public async Task EmptySubscriptionGuidIsRejectedBeforeCliIsInvoked()
    {
        var cli = new FakeCli() { AutoDiscover = false };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, subscriptionId: Guid.Empty));
        Assert.Empty(cli.Commands);
    }

    [Theory]
    [InlineData(WindowsAppFailure.DevBoxUnavailable)]
    [InlineData(WindowsAppFailure.ApiUnavailable)]
    [InlineData(WindowsAppFailure.TimedOut)]
    [InlineData(WindowsAppFailure.SignInRequired)]
    [InlineData(WindowsAppFailure.MalformedResponse)]
    [InlineData(WindowsAppFailure.UnsafeUri)]
    public async Task FailedSubscriptionDoesNotPreventSearchingTheNext(object failure)
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription)))
        { AutoDiscover = false };
        cli.Handler = (command, _) =>
        {
            if (Arguments(command)[^1] == AzureCliCommand.DevCentersUri(SubscriptionId).AbsoluteUri)
                throw new WindowsAppConnectionException((WindowsAppFailure)failure, "secret");
            return Task.FromResult(cli.Commands.Count == 4
                ? Page([Center(subscription: OtherSubscription, endpoint: SecondEndpoint)])
                : Page([Item()]));
        };
        var observed = new List<DevBoxCatalogProgress>();

        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add);

        Assert.Equal(SecondEndpoint, Assert.Single(snapshot.DevCenterEndpoints));
        Assert.Single(snapshot.Items);
        var warning = Assert.Single(snapshot.SubscriptionFailures);
        Assert.Equal(SubscriptionId, warning.SubscriptionId);
        Assert.Equal((WindowsAppFailure)failure, warning.Failure);
        Assert.DoesNotContain("secret", warning.Message);
        Assert.DoesNotContain(SubscriptionId.ToString(), warning.ToString());
        Assert.Equal(2, observed[^1].SubscriptionsCompleted);
        Assert.Single(observed[^1].SubscriptionFailures);
        Assert.Contains(observed, progress => progress.CurrentSubscription == OtherSubscription && progress.SubscriptionFailures.Count == 1);
        Assert.Equal(5, cli.Commands.Count);
    }

    [Fact]
    public async Task FailedLaterPageDiscardsThatSubscriptionsEndpointsAndKeepsOtherResults()
    {
        var third = Guid.NewGuid();
        var cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription), Subscription(third)),
            Page([Center()]), Page([Center(subscription: OtherSubscription, endpoint: SecondEndpoint)], OtherArmNext()),
            new AzureCliResult(1, "", "HTTP 403 secret"), Page([]), Page([Item()]))
        { AutoDiscover = false };

        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);

        Assert.Equal(Endpoint, Assert.Single(snapshot.DevCenterEndpoints));
        Assert.Single(snapshot.Items);
        Assert.Equal(OtherSubscription, Assert.Single(snapshot.SubscriptionFailures).SubscriptionId);
        Assert.Equal(AzureCliCommand.DevCentersUri(third).AbsoluteUri, Arguments(cli.Commands[5])[^1]);
        Assert.Equal(7, cli.Commands.Count);

        string OtherArmNext() => $"{AzureCliCommand.DevCentersUri(OtherSubscription)}&$skiptoken=page-2";
    }

    [Fact]
    public async Task AllFailedSubscriptionsAreAttemptedBeforeRefreshFails()
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription)),
            new AzureCliResult(1, "", "HTTP 403"), new AzureCliResult(1, "", "HTTP 503"))
        { AutoDiscover = false };
        var observed = new List<DevBoxCatalogProgress>();

        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add));

        Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
        Assert.Equal(4, cli.Commands.Count);
        Assert.Equal(2, observed[^1].SubscriptionsCompleted);
        Assert.Equal(2, observed[^1].SubscriptionFailures.Count);
        Assert.Equal(0, observed[^1].DevCenterCount);
    }

    [Fact]
    public async Task CancellationAfterSubscriptionFailureStopsBeforeNextSubscription()
    {
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription)),
            new AzureCliResult(1, "", "HTTP 403")) { AutoDiscover = false };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token, progress =>
            {
                if (progress.SubscriptionFailures.Count != 0) cancellation.Cancel();
            }));

        Assert.Equal(3, cli.Commands.Count);
    }

    [Fact]
    public async Task AccountListCloudNameWithoutEnvironmentNameDiscoversDevBoxes()
    {
        var json = $$"""
            [{
                "id": "{{SubscriptionId}}",
                "name": "Test subscription",
                "state": "Enabled",
                "user": { "name": "user@example.com", "type": "user" },
                "isDefault": true,
                "tenantId": "{{ConnectionTestData.Tenant}}",
                "homeTenantId": "{{ConnectionTestData.Tenant}}",
                "tenantDefaultDomain": "example.com",
                "tenantDisplayName": "Test tenant",
                "managedByTenants": [],
                "cloudName": "AzureCloud"
            }]
            """;
        var cli = new FakeCli(Account, new AzureCliResult(0, json, ""), Page([Center()]), Page([Item()]))
        { AutoDiscover = false };

        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);

        Assert.Equal(Endpoint, Assert.Single(snapshot.DevCenterEndpoints));
        Assert.Equal("devbox-01", Assert.Single(snapshot.Items).DevBoxName);
        Assert.Equal(4, cli.Commands.Count);
    }

    [Fact]
    public async Task ReportsValidatedAccountBeforeSubscriptionDiscoveryFails()
    {
        var subscription = Subscription();
        subscription.Remove("cloudName");
        var cli = new FakeCli(Account, Subscriptions(subscription)) { AutoDiscover = false };
        var observed = new List<DevBoxCatalogProgress>();
        var started = DateTimeOffset.UtcNow;

        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add));

        Assert.Equal(DevBoxCatalogStage.Subscriptions, error.Stage);
        Assert.Equal(2, observed.Count);
        Assert.Null(observed[0].Account);
        var progress = observed[1];
        Assert.Equal(DevBoxCatalogStage.Subscriptions, progress.Stage);
        Assert.Equal(AzureAccount.Parse(ConnectionTestData.Account), progress.Account);
        Assert.InRange(progress.AccountCheckedAtUtc!.Value, started, DateTimeOffset.UtcNow);
        Assert.Null(progress.SubscriptionCount);
        Assert.Null(progress.DevCenterCount);
        Assert.DoesNotContain(progress.Account!.Upn, progress.ToString());
        Assert.DoesNotContain(progress.Account.Tenant.ToString(), progress.ToString());
        Assert.DoesNotContain(progress.Account.Subscription.ToString(), progress.ToString());
    }

    [Fact]
    public async Task ReportsEachDiscoveryStageAndCompletedCounts()
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([Center()]), Page([Item()]))
        { AutoDiscover = false };
        var observed = new List<DevBoxCatalogProgress>();

        await new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add);

        Assert.Equal([DevBoxCatalogStage.Account, DevBoxCatalogStage.Subscriptions,
            DevBoxCatalogStage.DevCenters, DevBoxCatalogStage.DevBoxes], observed.Select(p => p.Stage).Distinct());
        Assert.Equal(1, observed[2].SubscriptionCount);
        Assert.Null(observed[2].DevCenterCount);
        var completed = observed[^1];
        Assert.Equal(1, completed.DevCenterCount);
        Assert.Equal(1, completed.SubscriptionsCompleted);
        Assert.Equal(1, completed.DevCentersCompleted);
        Assert.Equal(1, completed.DevBoxCount);
        Assert.Equal(2, completed.PagesRead);
        Assert.Null(completed.CurrentSubscription);
        Assert.Null(completed.CurrentDevCenterHost);
        Assert.Null(completed.CurrentPage);
        Assert.Equal(observed[1].AccountCheckedAtUtc, completed.AccountCheckedAtUtc);
    }

    [Fact]
    public async Task ReportsRequestedPagesAndRetainsCountsWhenLaterPageFails()
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([Center()]),
            Page([Item()], $"{Endpoint}devboxes?next=1"), new AzureCliResult(1, "", "HTTP 403"))
        { AutoDiscover = false };
        var observed = new List<DevBoxCatalogProgress>();
        cli.AfterRun = calls =>
        {
            if (calls == 5)
            {
                Assert.Equal(2, observed[^1].CurrentPage);
                Assert.Equal(Endpoint.IdnHost, observed[^1].CurrentDevCenterHost);
            }
        };

        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add));

        Assert.Equal(DevBoxCatalogStage.DevBoxes, error.Stage);
        Assert.Equal(1, observed[^1].SubscriptionsCompleted);
        Assert.Equal(0, observed[^1].DevCentersCompleted);
        Assert.Equal(1, observed[^1].DevBoxCount);
        Assert.Equal(2, observed[^1].PagesRead);
        Assert.Equal(2, observed[^1].CurrentPage);
    }

    [Fact]
    public async Task AccountFailureDoesNotReportValidatedIdentity()
    {
        var cli = new FakeCli(new AzureCliResult(1, "", "az login"));
        var observed = new List<DevBoxCatalogProgress>();

        await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default, observed.Add));

        var progress = Assert.Single(observed);
        Assert.Equal(DevBoxCatalogStage.Account, progress.Stage);
        Assert.Null(progress.Account);
        Assert.Null(progress.AccountCheckedAtUtc);
    }

    [Fact]
    public async Task SearchesOnlyEnabledPublicSubscriptionsForActiveTenantAndInteractiveUser()
    {
        var excluded = new[]
        {
            Subscription(Guid.NewGuid(), state: "Disabled"),
            Subscription(Guid.NewGuid(), cloud: "AzureUSGovernment"),
            Subscription(Guid.NewGuid(), tenant: Guid.NewGuid()),
            Subscription(Guid.NewGuid(), user: "other@example.com"),
            Subscription(Guid.NewGuid(), type: "servicePrincipal"),
            Subscription(Guid.NewGuid(), type: "managedIdentity")
        };
        var cli = new FakeCli(Account, Subscriptions([.. excluded, Subscription(OtherSubscription), Subscription()]),
            Page([Center(subscription: OtherSubscription, endpoint: SecondEndpoint)]),
            Page([Center()]), Page([]), Page([Item()])) { AutoDiscover = false };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Equal([Endpoint, SecondEndpoint], snapshot.DevCenterEndpoints);
        Assert.Single(snapshot.Items);
        Assert.Equal(6, cli.Commands.Count);
        Assert.Equal(AzureCliCommand.DevCentersUri(OtherSubscription).AbsoluteUri, Arguments(cli.Commands[2])[^1]);
        Assert.Equal(AzureCliCommand.DevCentersUri(SubscriptionId).AbsoluteUri, Arguments(cli.Commands[3])[^1]);
        Assert.All(cli.Commands.Skip(2), c => Assert.Equal("get", Arguments(c)[2]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("state")]
    [InlineData("cloudName")]
    [InlineData("tenantId")]
    [InlineData("user")]
    [InlineData("type")]
    public async Task ActiveSubscriptionMustRemainUsableInAccountList(string change)
    {
        var current = Subscription();
        switch (change)
        {
            case "state": current["state"] = "Disabled"; break;
            case "cloudName": current["cloudName"] = "AzureChinaCloud"; break;
            case "tenantId": current["tenantId"] = Guid.NewGuid().ToString(); break;
            case "user": current["user"]!["name"] = "other@example.com"; break;
            case "type": current["user"]!["type"] = "servicePrincipal"; break;
        }
        var cli = new FakeCli(Account, change == "missing" ? Subscriptions(Subscription(OtherSubscription)) : Subscriptions(current))
        { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.CliUnavailable, error.Failure);
        Assert.Equal(DevBoxCatalogStage.Subscriptions, error.Stage);
        Assert.Equal(2, cli.Commands.Count);
        AssertSafe(error);
    }

    public static IEnumerable<object[]> MalformedSubscriptions()
    {
        foreach (var json in new[] { "", "{}", "null", "[null]", "[{}]", "[] trailing", "[{\"id\":null}]" })
            yield return [json];
        foreach (var property in new[] { "id", "tenantId", "state", "cloudName", "user" })
        {
            var value = Subscription();
            value.Remove(property);
            yield return [Subscriptions(value).StandardOutput];
            value[property] = 4;
            yield return [Subscriptions(value).StandardOutput];
            value[property] = null;
            yield return [Subscriptions(value).StandardOutput];
        }
        foreach (var property in new[] { "id", "tenantId" })
        foreach (var invalid in new[] { "not-guid", Guid.Empty.ToString() })
        {
            var value = Subscription();
            value[property] = invalid;
            yield return [Subscriptions(value).StandardOutput];
        }
        foreach (var property in new[] { "name", "type" })
        {
            var value = Subscription();
            value["user"]!.AsObject().Remove(property);
            yield return [Subscriptions(value).StandardOutput];
        }
        var valid = Subscription().ToJsonString();
        yield return [$"[{valid[..^1]},\"cloudName\":\"AzureCloud\"}}]"];
        yield return [$"[{valid[..^1]},\"user\":{{\"name\":\"user@example.com\",\"type\":\"user\"}}}}]"];
        yield return [Subscriptions(Subscription(), Subscription()).StandardOutput];
    }

    [Theory]
    [MemberData(nameof(MalformedSubscriptions))]
    public async Task MalformedSubscriptionRecordsFailBeforeArmRequests(string json)
    {
        var cli = new FakeCli(Account, new(0, json, "")) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(DevBoxCatalogStage.Subscriptions, error.Stage);
        Assert.Equal(2, cli.Commands.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(165, false)]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public async Task EligibleSubscriptionCapNeverSilentlyTruncates(int count, bool fails)
    {
        var subscriptions = new[] { Subscription() }.Concat(Enumerable.Range(1, count - 1).Select(_ => Subscription(Guid.NewGuid()))).ToArray();
        var cli = new FakeCli([Account, Subscriptions(subscriptions), .. Enumerable.Range(0, count).Select(_ => Page([]))])
        { AutoDiscover = false };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        if (fails)
        {
            var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending);
            Assert.Equal(WindowsAppFailure.DiscoveryLimitExceeded, error.Failure);
            Assert.Equal(DevBoxCatalogStage.Subscriptions, error.Stage);
            Assert.Contains("more than 1000 subscription records", error.Message);
            Assert.DoesNotContain("malformed", error.Message);
            AssertSafe(error);
        }
        else
        {
            Assert.Empty((await pending).DevCenterEndpoints);
            Assert.Equal(subscriptions.Select(s => s["id"]!.GetValue<string>()),
                cli.Commands.Skip(2).Select(command => new Uri(Arguments(command)[^1]).Segments[2].TrimEnd('/')));
        }
        Assert.Equal(fails ? 2 : count + 2, cli.Commands.Count);
    }

    [Theory]
    [InlineData(1000, false)]
    [InlineData(1001, true)]
    public async Task RawSubscriptionRecordCapIncludesFilteredRecords(int count, bool fails)
    {
        var records = new[] { Subscription() }.Concat(Enumerable.Range(1, count - 1).Select(_ => Subscription(OtherSubscription, state: "Disabled"))).ToArray();
        var cli = new FakeCli(Account, Subscriptions(records), Page([])) { AutoDiscover = false };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        if (fails) Assert.Equal(WindowsAppFailure.DiscoveryLimitExceeded, (await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending)).Failure);
        else Assert.Empty((await pending).Items);
        Assert.Equal(fails ? 2 : 3, cli.Commands.Count);
    }

    [Fact]
    public async Task NoVisibleCentersIsSuccessfulEmptySnapshotWithValidatedAccount()
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([])) { AutoDiscover = false };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Empty(snapshot.Items);
        Assert.Empty(snapshot.DevCenterEndpoints);
        Assert.Equal(ConnectionTestData.Tenant, snapshot.AzureTenantId);
        Assert.Equal("user@example.com", snapshot.AzureAccountUpn);
        Assert.Equal(3, cli.Commands.Count);
    }

    [Fact]
    public async Task ArmPaginationCompletesBeforeDataPlaneAndDuplicateEndpointsAreListedOnce()
    {
        var next = ArmNext(1);
        var cli = new FakeCli(Account, Subscriptions(Subscription()),
            Page([Center()], next),
            Page([Center("center-two", endpoint: new Uri("https://EXAMPLE.region.devcenter.azure.com:443/")), Center("center-three", endpoint: SecondEndpoint)]),
            Page([]), Page([])) { AutoDiscover = false };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Equal([Endpoint, SecondEndpoint], snapshot.DevCenterEndpoints);
        Assert.Equal(next, Arguments(cli.Commands[3])[^1]);
        Assert.Equal(6, cli.Commands.Count);
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<Uri>>(snapshot.DevCenterEndpoints);
    }

    public static IEnumerable<object[]> InvalidCenters()
    {
        foreach (var property in new[] { "id", "properties" })
        {
            var center = Center();
            center.Remove(property);
            yield return [Page([center]).StandardOutput];
        }
        foreach (var property in new[] { "id", "name", "type", "properties" })
        {
            var center = Center();
            center[property] = null;
            yield return [Page([center]).StandardOutput];
        }
        foreach (var id in new[]
        {
            "/subscriptions/not-guid/resourceGroups/rg-one/providers/Microsoft.DevCenter/devcenters/center-one",
            $"/subscriptions/{OtherSubscription}/resourceGroups/rg-one/providers/Microsoft.DevCenter/devcenters/center-one",
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-one/providers/Microsoft.Compute/devcenters/center-one",
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-one/providers/Microsoft.DevCenter/projects/center-one",
            $"/subscriptions/{SubscriptionId}/resourceGroups/../providers/Microsoft.DevCenter/devcenters/center-one",
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg%2fone/providers/Microsoft.DevCenter/devcenters/center-one",
            $"/subscriptions/{SubscriptionId}/resourceGroups/rg-one/providers/Microsoft.DevCenter/devcenters/center-one/extra",
            "https://management.azure.com/secret"
        })
        {
            var center = Center();
            center["id"] = id;
            yield return [Page([center]).StandardOutput];
        }
        foreach (var property in new[] { "name", "type" })
        {
            var center = Center();
            center[property] = "wrong";
            yield return [Page([center]).StandardOutput];
        }
        foreach (var invalid in new JsonNode?[] { null, JsonValue.Create(42), new JsonObject() })
        {
            var center = Center();
            center["properties"]!["devCenterUri"] = invalid;
            yield return [Page([center]).StandardOutput];
        }
        var duplicate = Center().ToJsonString();
        yield return [$"{{\"value\":[{duplicate[..^1]},\"id\":\"duplicate\"}}]}}"];
        yield return ["{\"value\":[],\"value\":[]}"];
        yield return ["{\"value\":[],\"nextLink\":null,\"nextLink\":null}"];
        yield return ["{\"value\":[],\"nextLink\":42}"];
        yield return ["{\"value\":[null]}"];
        yield return ["{\"value\":null}"];
        yield return ["{}"];
        yield return ["[]"];
    }

    [Theory]
    [MemberData(nameof(InvalidCenters))]
    public async Task MalformedArmMetadataNeverReachesDataPlane(string json)
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription()), new(0, json, "")) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
        Assert.Equal(3, cli.Commands.Count);
        Assert.Null(error.EndpointHost);
        AssertSafe(error);
    }

    [Fact]
    public async Task OptionalArmNameAndTypeMayBeAbsentButIdentityAndEndpointAreRequired()
    {
        var center = Center();
        center.Remove("name");
        center.Remove("type");
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([center]), Page([])) { AutoDiscover = false };
        Assert.Equal(Endpoint, Assert.Single((await new DevBoxCatalogService(cli).RefreshAsync(default)).DevCenterEndpoints));
    }

    [Theory]
    [InlineData("http://example.region.devcenter.azure.com/")]
    [InlineData("https://evil.test/")]
    [InlineData("https://example.region.devcenter.azure.com:444/")]
    [InlineData("https://user@example.region.devcenter.azure.com/")]
    [InlineData("https://example.region.devcenter.azure.com/secret")]
    [InlineData("https://example.region.devcenter.azure.com/secret/..")]
    [InlineData("https://example.region.devcenter.azure.com/%2e")]
    [InlineData("https://example.region.devcenter.azure.com/?secret")]
    [InlineData("https://example.region.devcenter.azure.com/#secret")]
    [InlineData("https://example.region.devcenter.azure.com\\")]
    [InlineData("/relative")]
    [InlineData("")]
    public async Task UnsafeDiscoveredEndpointsAreRejected(string endpoint)
    {
        var center = Center();
        center["properties"]!["devCenterUri"] = endpoint;
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([center])) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
        Assert.Equal(3, cli.Commands.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedArmResourceIdentitiesAreRejectedWithinAndAcrossPages(bool acrossPages)
    {
        var duplicate = Center();
        duplicate["id"] = duplicate["id"]!.GetValue<string>().ToUpperInvariant();
        var pages = acrossPages ? new[] { Page([Center()], ArmNext(1)), Page([duplicate]) } : [Page([Center(), duplicate])];
        var cli = new FakeCli([Account, Subscriptions(Subscription()), .. pages]) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task ArmPageCapPreventsFurtherRequests(int pages, bool fails)
    {
        var cli = new FakeCli([Account, Subscriptions(Subscription()), .. Enumerable.Range(0, pages)
            .Select(i => Page([], i == pages - 1 ? null : ArmNext(i + 1)))]) { AutoDiscover = false };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        if (fails) Assert.Equal(WindowsAppFailure.MalformedResponse, (await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending)).Failure);
        else Assert.Empty((await pending).Items);
        Assert.Equal(22, cli.Commands.Count);
    }

    [Theory]
    [InlineData(250, false)]
    [InlineData(251, true)]
    public async Task RawArmCenterLimitCountsDuplicatesOfEndpointAcrossPages(int count, bool fails)
    {
        var centers = Enumerable.Range(0, count).Select(i => Center($"center-{i}")).ToArray();
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page(centers.Take(125), ArmNext(1)),
            Page(centers.Skip(125)), Page([])) { AutoDiscover = false };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        if (fails) Assert.Equal(WindowsAppFailure.MalformedResponse, (await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending)).Failure);
        else Assert.Single((await pending).DevCenterEndpoints);
        Assert.Equal(fails ? 4 : 5, cli.Commands.Count);
    }

    [Fact]
    public async Task ArmCenterLimitIsPerSubscriptionWhileEndpointLimitIsGlobal()
    {
        var first = Enumerable.Range(0, 250).Select(i => Center($"center-{i}")).ToArray();
        var second = Enumerable.Range(0, 250).Select(i => Center($"center-{i}", OtherSubscription, Endpoint)).ToArray();
        var cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription)),
            Page(first), Page(second), Page([])) { AutoDiscover = false };
        Assert.Single((await new DevBoxCatalogService(cli).RefreshAsync(default)).DevCenterEndpoints);
        Assert.Equal(5, cli.Commands.Count);

        first = Enumerable.Range(0, 20).Select(i => Center($"center-{i}", endpoint: new Uri($"https://center-{i}.region.devcenter.azure.com/"))).ToArray();
        cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription)),
            Page(first), Page([Center(subscription: OtherSubscription, endpoint: SecondEndpoint)])) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
        Assert.Equal(4, cli.Commands.Count);
    }

    [Fact]
    public async Task ArmPaginationLoopsFailWithoutExtraRequests()
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([], ArmNext(1)), Page([], ArmNext(1))) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(4, cli.Commands.Count);
    }

    [Theory]
    [MemberData(nameof(AzureCliProcessTests.UnsafeArmLinks), MemberType = typeof(AzureCliProcessTests))]
    public async Task UnsafeArmPaginationNeverReachesCli(string link)
    {
        var cli = new FakeCli(Account, Subscriptions(Subscription()), Page([], link)) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
        Assert.Equal(3, cli.Commands.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false, "HTTP 403 secret", WindowsAppFailure.CliUnavailable)]
    [InlineData(false, "az login secret", WindowsAppFailure.SignInRequired)]
    [InlineData(true, "HTTP 403 secret", WindowsAppFailure.DevBoxUnavailable)]
    [InlineData(true, "HTTP 404 secret", WindowsAppFailure.DevBoxUnavailable)]
    [InlineData(true, "HTTP 503 secret", WindowsAppFailure.ApiUnavailable)]
    [InlineData(true, "az login secret", WindowsAppFailure.SignInRequired)]
    public async Task DiscoveryFailuresIdentifyStageWithoutLeakingMetadata(bool arm, string output, object failure)
    {
        var responses = arm
            ? new[] { Account, Subscriptions(Subscription(), Subscription(OtherSubscription)), Page([Center()]), new AzureCliResult(1, output, ConnectionTestData.Uri), Page([]) }
            : [Account, new AzureCliResult(1, output, ConnectionTestData.Uri)];
        var cli = new FakeCli(responses) { AutoDiscover = false };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        var error = arm
            ? new DevBoxCatalogException(Assert.Single((await pending).SubscriptionFailures).Failure, DevBoxCatalogStage.DevCenters)
            : await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending);
        Assert.Equal((WindowsAppFailure)failure, error.Failure);
        Assert.Equal(arm ? DevBoxCatalogStage.DevCenters : DevBoxCatalogStage.Subscriptions, error.Stage);
        Assert.Contains(arm ? "Dev Center resource discovery" : "Azure subscription discovery", error.Message);
        Assert.Null(error.EndpointHost);
        Assert.Equal(responses.Length, cli.Commands.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DiscoveryChecksBothStreamsForByteBounds(bool arm, bool stderr)
    {
        var output = new string('é', AzureCliProcess.MaximumOutputBytes / 2 + 1);
        var bad = new AzureCliResult(0, stderr ? (arm ? "{\"value\":[]}" : "[]") : output, stderr ? output : "");
        var cli = new FakeCli(arm ? [Account, Subscriptions(Subscription()), bad] : [Account, bad]) { AutoDiscover = false };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(arm ? DevBoxCatalogStage.DevCenters : DevBoxCatalogStage.Subscriptions, error.Stage);
    }

    [Theory]
    [InlineData(false, WindowsAppFailure.TimedOut)]
    [InlineData(true, WindowsAppFailure.TimedOut)]
    [InlineData(false, WindowsAppFailure.CliUnavailable)]
    [InlineData(true, WindowsAppFailure.CliUnavailable)]
    public async Task DiscoveryProcessErrorsRetainSafeStageAndFailure(bool arm, object failure)
    {
        var cli = new FakeCli(arm ? [Account, Subscriptions(Subscription())] : [Account])
        {
            AutoDiscover = false,
            Handler = (_, _) => throw new WindowsAppConnectionException((WindowsAppFailure)failure, $"secret {ConnectionTestData.Uri}")
        };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal((WindowsAppFailure)failure, error.Failure);
        Assert.Equal(arm ? DevBoxCatalogStage.DevCenters : DevBoxCatalogStage.Subscriptions, error.Stage);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task CancellationDuringEveryStageIsPassedThroughAndSanitized(int completedCalls)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responses = new[] { Account, Subscriptions(Subscription()), Page([], ArmNext(1)), Page([Center()]) };
        var cli = new FakeCli(responses.Take(completedCalls).ToArray())
        {
            AutoDiscover = false,
            Handler = async (_, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                throw new InvalidOperationException();
            }
        };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(completedCalls + 1, cli.Commands.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CancellationBetweenArmPagesAndSubscriptionsPreventsNextScope(int cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(Account, Subscriptions(Subscription(), Subscription(OtherSubscription)),
            Page([], ArmNext(1)), Page([Center()]), Page([]))
        {
            AutoDiscover = false,
            AfterRun = calls => { if (calls == cancelAfter) cancellation.Cancel(); }
        };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(cancelAfter, cli.Commands.Count);
    }

    private static System.Collections.ObjectModel.Collection<string> Arguments(AzureCliCommand command) =>
        command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList;

    private static string ArmNext(int page) => $"{AzureCliCommand.DevCentersUri(SubscriptionId)}&$skiptoken=page-{page}";

    private static JsonObject Subscription(Guid? id = null, string state = "Enabled", string cloud = "AzureCloud",
        Guid? tenant = null, string user = "USER@example.com", string type = "user") => new()
    {
        ["id"] = (id ?? SubscriptionId).ToString(), ["tenantId"] = (tenant ?? ConnectionTestData.Tenant).ToString(),
        ["state"] = state, ["cloudName"] = cloud,
        ["user"] = new JsonObject { ["name"] = user, ["type"] = type }
    };

    private static AzureCliResult Subscriptions(params JsonObject[] values) =>
        new(0, new JsonArray(values.Select(value => (JsonNode)value.DeepClone()).ToArray()).ToJsonString(), "");

    private static JsonObject Center(string name = "center-one", Guid? subscription = null, Uri? endpoint = null) => new()
    {
        ["id"] = $"/subscriptions/{subscription ?? SubscriptionId}/resourceGroups/rg-one/providers/Microsoft.DevCenter/devcenters/{name}",
        ["name"] = name, ["type"] = "Microsoft.DevCenter/devcenters",
        ["properties"] = new JsonObject { ["devCenterUri"] = (endpoint ?? Endpoint).OriginalString }
    };

    [Fact]
    public async Task DocumentedApiFieldsAreParsedWithoutPersistingAdditionalPropertiesOrUris()
    {
        // 2025-02-01 DevBoxes_ListAllDevBoxes: projectName, osType and nested hardwareProfile,
        // not ARM properties or flat operatingSystem/vCpus/memoryGb fields.
        var item = Item();
        item["uri"] = $"{Endpoint}projects/project.one/users/33333333-3333-3333-3333-333333333333/devboxes/devbox-01";
        item["hardwareProfile"] = new JsonObject { ["vCPUs"] = 8, ["memoryGB"] = 32 };
        item["osType"] = "Windows";
        item["uniqueId"] = "44444444-4444-4444-4444-444444444444";
        item["storageProfile"] = new JsonObject { ["osDisk"] = new JsonObject { ["diskSizeGB"] = 1024 } };
        item["imageReference"] = new JsonObject { ["name"] = "DevImage", ["version"] = "1.0.0" };
        item["user"] = "33333333-3333-3333-3333-333333333333";
        item["location"] = "centralus";
        item["lastConnectedTime"] = "2022-04-01T00:13:23.323Z";
        item["hibernateSupport"] = "Enabled";
        var cli = new FakeCli(Account, Page([item]));
        var time = new TestTimeProvider();
        var snapshot = await new DevBoxCatalogService(cli, time).RefreshAsync(default);
        var actual = Assert.Single(snapshot.Items);
        Assert.Equal(new DevBoxCatalogItem(Endpoint, "project.one", "pool.one", "devbox-01",
            "Running", "Succeeded", "Windows", 8, 32, Guid.Parse("44444444-4444-4444-4444-444444444444")), actual);
        Assert.Equal("user@example.com", snapshot.AzureAccountUpn);
        Assert.Equal(ConnectionTestData.Tenant, snapshot.AzureTenantId);
        Assert.Equal(time.GetUtcNow(), snapshot.RetrievedAtUtc);
        Assert.Equal(4, cli.Commands.Count);
        Assert.Equal(TimeSpan.FromSeconds(15), cli.Commands[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), cli.Commands[1].Timeout);
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<DevBoxCatalogItem>>(snapshot.Items);
        Assert.DoesNotContain("users/", actual.ToString());
        Assert.DoesNotContain("user@example.com", snapshot.ToString());
    }

    [Fact]
    public async Task MissingDocumentedOptionalFieldsAreAccepted()
    {
        var item = Item();
        item.Remove("powerState");
        item.Remove("provisioningState");
        var snapshot = await Refresh(Page([item]));
        var actual = Assert.Single(snapshot.Items);
        Assert.Equal("Unknown", actual.PowerState);
        Assert.Equal("Unknown", actual.ProvisioningState);
        Assert.Null(actual.OperatingSystem);
        Assert.Null(actual.VCpus);
        Assert.Null(actual.MemoryGb);
        Assert.Null(actual.UniqueId);
    }

    [Fact]
    public async Task MultipleEndpointsAndPagesShareOneAccountAndSortWithoutCollapsingNames()
    {
        var next = $"{Endpoint}devboxes?api-version=2025-02-01&$skiptoken=next%2Bpage";
        var cli = new FakeCli(Account, Page([Item("z-box")], next),
            Page([Item("a-box", "zzz-project"), Item("a-box", "aaa-project")]),
            Page([Item("a-box", "aaa-project")])) { Endpoints = [Endpoint, SecondEndpoint] };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Equal(["a-box", "a-box", "a-box", "z-box"], snapshot.Items.Select(i => i.DevBoxName));
        Assert.Equal(["aaa-project", "aaa-project", "zzz-project", "project.one"], snapshot.Items.Select(i => i.ProjectName));
        Assert.Equal([Endpoint, SecondEndpoint, Endpoint, Endpoint], snapshot.Items.Select(i => i.DevCenterEndpoint));
        Assert.Single(cli.Commands, c => c.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[1] == "show");
        Assert.Equal(next, cli.Commands[4].CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[^1]);
        Assert.All(cli.Commands.Skip(2), c =>
            Assert.Equal("get", c.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[2]));
    }

    [Fact]
    public async Task EveryDiscoveredEndpointIsValidatedBeforeDataPlaneRuns()
    {
        foreach (var endpoints in new IReadOnlyList<Uri>[]
        {
            [Endpoint, new Uri("https://evil.test/")],
            [Endpoint, new Uri("devboxes", UriKind.Relative)],
            Enumerable.Range(0, 21).Select(i => new Uri($"https://center-{i}.region.devcenter.azure.com/")).ToArray()
        })
        {
            var cli = new FakeCli(Account) { Endpoints = endpoints };
            var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
                new DevBoxCatalogService(cli).RefreshAsync(default));
            Assert.Contains(error.Failure, new[] { WindowsAppFailure.UnsafeUri, WindowsAppFailure.MalformedResponse });
            Assert.Equal(DevBoxCatalogStage.DevCenters, error.Stage);
            Assert.Null(error.EndpointHost);
            Assert.Equal(3, cli.Commands.Count);
            AssertSafe(error);
        }
    }

    [Fact]
    public async Task TwentyEndpointsAndSuccessfulEmptyResponsesAreSupported()
    {
        var endpoints = Enumerable.Range(0, 20).Select(i => new Uri($"https://center-{i}.region.devcenter.azure.com/")).ToArray();
        var cli = new FakeCli([Account, .. Enumerable.Range(0, 20).Select(_ => Page([]))]) { Endpoints = endpoints };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Empty(snapshot.Items);
        Assert.Equal(20, snapshot.DevCenterEndpoints.Count);
        Assert.Equal(23, cli.Commands.Count);
    }

    [Fact]
    public async Task ReturnedEndpointAndAccountAreNormalized()
    {
        var account = ConnectionTestData.Account.Replace("user@example.com", "USER@EXAMPLE.COM")
            .Replace("Enabled", "eNaBlEd");
        var cli = new FakeCli(new(0, account, ""), Page([Item()]))
        {
            Endpoints = [new Uri("https://EXAMPLE.region.devcenter.azure.com:443")]
        };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Equal(Endpoint.AbsoluteUri, Assert.Single(snapshot.Items).DevCenterEndpoint.AbsoluteUri);
        Assert.Equal("user@example.com", snapshot.AzureAccountUpn);
        Assert.DoesNotContain(snapshot.AzureAccountUpn, AzureAccount.Parse(account).ToString());
    }

    [Theory]
    [InlineData("Please run az login secret", WindowsAppFailure.SignInRequired)]
    [InlineData("Unexpected account failure secret", WindowsAppFailure.CliUnavailable)]
    public async Task AccountFailurePreventsAllEndpointRequests(string output, object expected)
    {
        var cli = new FakeCli(new AzureCliResult(1, output, ConnectionTestData.Uri));
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal((WindowsAppFailure)expected, error.Failure);
        Assert.Null(error.EndpointHost);
        Assert.Single(cli.Commands);
        AssertSafe(error);
    }

    [Fact]
    public async Task FailedRefreshDoesNotMutatePreviouslyReturnedSnapshot()
    {
        var cli = new FakeCli(Account, Page([Item()]), Account, Page([Item("new-box")]), new(1, "secret", ""));
        var service = new DevBoxCatalogService(cli);
        var previous = await service.RefreshAsync(default);
        cli.Endpoints = [Endpoint, SecondEndpoint];
        await Assert.ThrowsAsync<DevBoxCatalogException>(() => service.RefreshAsync(default));
        Assert.Equal("devbox-01", Assert.Single(previous.Items).DevBoxName);
    }

    [Fact]
    public async Task ItemLimitIsPerEndpointNotPerSnapshot()
    {
        var items = Enumerable.Range(0, 250).Select(i => Item($"box-{i}")).ToArray();
        var cli = new FakeCli(Account, Page(items), Page(items)) { Endpoints = [Endpoint, SecondEndpoint] };
        var snapshot = await new DevBoxCatalogService(cli).RefreshAsync(default);
        Assert.Equal(500, snapshot.Items.Count);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task PageLimitIsEnforcedBeforeStartingAnotherRequest(int pages, bool fails)
    {
        var responses = Enumerable.Range(0, pages).Select(i =>
            Page([], i == pages - 1 ? null : $"{Endpoint}devboxes?continuation={i}"));
        var cli = new FakeCli([Account, .. responses]);
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        if (fails) Assert.Equal(WindowsAppFailure.MalformedResponse, (await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending)).Failure);
        else Assert.Empty((await pending).Items);
        Assert.Equal(23, cli.Commands.Count);
    }

    [Theory]
    [InlineData(250, false)]
    [InlineData(251, true)]
    public async Task ItemLimitSpansPages(int count, bool fails)
    {
        var items = Enumerable.Range(0, count).Select(i => Item($"box-{i}")).ToArray();
        var cli = new FakeCli(Account, Page(items.Take(125), $"{Endpoint}devboxes?next=1"), Page(items.Skip(125)));
        var pending = new DevBoxCatalogService(cli).RefreshAsync(default);
        if (fails) Assert.Equal(WindowsAppFailure.MalformedResponse, (await Assert.ThrowsAsync<DevBoxCatalogException>(() => pending)).Failure);
        else Assert.Equal(count, (await pending).Items.Count);
        Assert.Equal(5, cli.Commands.Count);
    }

    [Fact]
    public async Task RepeatedPaginationLinksFailInsteadOfLooping()
    {
        var next = $"{Endpoint}devboxes?next=1";
        var cli = new FakeCli(Account, Page([], next), Page([], next));
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Equal(5, cli.Commands.Count);
    }

    [Theory]
    [InlineData("http://example.region.devcenter.azure.com/devboxes")]
    [InlineData("https://other.region.devcenter.azure.com/devboxes")]
    [InlineData("https://example.region.devcenter.azure.com:444/devboxes")]
    [InlineData("https://user@example.region.devcenter.azure.com/devboxes")]
    [InlineData("https://example.region.devcenter.azure.com/devboxes#fragment")]
    [InlineData("https://example.region.devcenter.azure.com/devboxes?next=%0a")]
    [InlineData("https://example.region.devcenter.azure.com\\devboxes")]
    [InlineData("/devboxes?next=1")]
    [InlineData("")]
    public async Task UnsafePaginationNeverReachesCli(string next)
    {
        var cli = new FakeCli(Account, Page([], next));
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        Assert.Equal(Endpoint.IdnHost, error.EndpointHost);
        Assert.Equal(4, cli.Commands.Count);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("http://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01")]
    [InlineData("https://other.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01")]
    [InlineData("https://example.region.devcenter.azure.com/projects/wrong/users/me/devboxes/devbox-01")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/wrong")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01/remoteConnection")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01?query")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01#fragment")]
    [InlineData("https://example.region.devcenter.azure.com/projects/wrong/../project.one/users/me/devboxes/devbox-01")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/invalid/devboxes/devbox-01")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox%2f01")]
    [InlineData("https://example.region.devcenter.azure.com/projects/project.one/users/me/devboxes/devbox-01/")]
    [InlineData("/projects/project.one/users/me/devboxes/devbox-01")]
    public async Task ItemUriMustMatchOriginProjectAndDevBox(string uri)
    {
        var item = Item();
        item["uri"] = uri;
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => Refresh(Page([item])));
        Assert.Equal(WindowsAppFailure.UnsafeUri, error.Failure);
        AssertSafe(error);
    }

    [Fact]
    public async Task ItemUriSupportsMeAndEscapedCaseInsensitiveIdentity()
    {
        var item = Item();
        item["uri"] = $"{Endpoint}projects/PROJECT%2eONE/users/me/devboxes/DEVBOX-01";
        Assert.Single((await Refresh(Page([item]))).Items);
    }

    public static IEnumerable<object[]> MalformedLists()
    {
        foreach (var json in new[]
        {
            "", "{", "[]", "null", "42", "{}", "{\"value\":null}", "{\"value\":{}}",
            "{\"value\":[],\"value\":[]}", "{\"Value\":[]}", "{\"value\":[null]}",
            "{\"value\":[{}]}", "{\"value\":[],\"nextLink\":3}",
            "{\"value\":[],\"nextLink\":null,\"nextLink\":null}", "{\"value\":[]} trailing",
            "{\"value\":[],}", "/*comment*/{\"value\":[]}", "{\"value\":[],\"\\uD800\":0}"
        }) yield return [json];
        foreach (var name in new[] { "name", "projectName", "poolName" })
        {
            var item = Item();
            item.Remove(name);
            yield return [Page([item]).StandardOutput];
            foreach (var invalid in new[] { "null", "1", "true", "[]", "{}", "\"ab\"", "\"bad/name\"", "\"secret\\uD800\"" })
            {
                item = Item();
                var json = item.ToJsonString();
                var original = JsonSerializer.Serialize(item[name]!.GetValue<string>());
                yield return [$"{{\"value\":[{json.Replace($"\"{name}\":{original}", $"\"{name}\":{invalid}")}]}}"];
            }
            var valid = Item().ToJsonString();
            yield return [$"{{\"value\":[{valid[..^1]},\"{name}\":\"duplicate\"}}]}}"];
        }
        foreach (var name in new[] { "powerState", "provisioningState", "osType", "uniqueId", "uri", "hardwareProfile" })
        {
            foreach (var invalid in new JsonNode?[] { JsonValue.Create(7), null, new JsonArray() })
            {
                var item = Item();
                item[name] = invalid;
                yield return [Page([item]).StandardOutput];
            }
        }
        foreach (var id in new[] { "", "not-guid", Guid.Empty.ToString() })
        {
            var item = Item();
            item["uniqueId"] = id;
            yield return [Page([item]).StandardOutput];
        }
        foreach (var number in new[] { "0", "-1", "1.5", "2147483648", "\"8\"", "null" })
            yield return [$"{{\"value\":[{Item().ToJsonString()[..^1]},\"hardwareProfile\":{{\"vCPUs\":{number}}}}}]}}"];
        yield return [$"{{\"value\":[{Item().ToJsonString()[..^1]},\"powerState\":\"bad\\nstate\"}}]}}"];
        foreach (var text in new[] { "https://private.example/secret", "ms-cloudpc:connect", "user@example.com", "\u202esecret" })
        {
            var item = Item();
            item["powerState"] = text;
            yield return [Page([item]).StandardOutput];
        }
        yield return ["{\"value\":[],\"ignored\":" + new string('[', 17) + "0" + new string(']', 17) + "}"];
    }

    [Theory]
    [MemberData(nameof(MalformedLists))]
    public async Task MalformedEnvelopeOrFieldsNeverReturnSnapshot(string json)
    {
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() => Refresh(new(0, json, "")));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIdentityIsRejectedAcrossAndWithinPages(bool acrossPages)
    {
        var items = new[] { Item(), Item("DEVBOX-01", "PROJECT.ONE") };
        var cli = acrossPages
            ? new FakeCli(Account, Page(items.Take(1), $"{Endpoint}devboxes?next=1"), Page(items.Skip(1)))
            : new FakeCli(Account, Page(items));
        Assert.Equal(WindowsAppFailure.MalformedResponse,
            (await Assert.ThrowsAsync<DevBoxCatalogException>(() => new DevBoxCatalogService(cli).RefreshAsync(default))).Failure);
    }

    [Theory]
    [MemberData(nameof(DevBoxConnectionResolverTests.InvalidAccountJson), MemberType = typeof(DevBoxConnectionResolverTests))]
    public async Task CatalogUsesSameStrictBoundedAccountParserAsResolver(string json)
    {
        var cli = new FakeCli(new AzureCliResult(0, json, ""));
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
        Assert.Single(cli.Commands);
        Assert.Null(error.EndpointHost);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("id", "not-guid")]
    [InlineData("id", "00000000-0000-0000-0000-000000000000")]
    [InlineData("tenantId", "not-guid")]
    [InlineData("tenantId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("state", "Disabled")]
    [InlineData("type", "servicePrincipal")]
    [InlineData("type", "managedIdentity")]
    [InlineData("type", "USER")]
    [InlineData("name", "invalid")]
    [InlineData("name", " user@example.com")]
    [InlineData("name", "user@@example.com")]
    public async Task UnusableAccountsCannotDiscover(string property, string text)
    {
        var json = JsonNode.Parse(ConnectionTestData.Account)!;
        if (property is "name" or "type") json["user"]![property] = text;
        else json[property] = text;
        var cli = new FakeCli(new AzureCliResult(0, json.ToJsonString(), ""));
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Single(cli.Commands);
        Assert.Equal(property == "name" ? WindowsAppFailure.MalformedResponse : WindowsAppFailure.CliUnavailable, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("az login secret", WindowsAppFailure.SignInRequired)]
    [InlineData("AADSTS50076 secret", WindowsAppFailure.SignInRequired)]
    [InlineData("HTTP 403 secret", WindowsAppFailure.DevBoxUnavailable)]
    [InlineData("HTTP 404 secret", WindowsAppFailure.DevBoxUnavailable)]
    [InlineData("HTTP 503 secret", WindowsAppFailure.ApiUnavailable)]
    public async Task FailureOfLaterEndpointNeverReturnsPartialSnapshotOrRawErrors(string output, object expected)
    {
        var cli = new FakeCli(Account, Page([Item()]), new(1, output, ConnectionTestData.Uri)) { Endpoints = [Endpoint, SecondEndpoint] };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal((WindowsAppFailure)expected, error.Failure);
        Assert.Equal(SecondEndpoint.IdnHost, error.EndpointHost);
        Assert.Contains(SecondEndpoint.IdnHost, error.Message);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(WindowsAppFailure.TimedOut)]
    [InlineData(WindowsAppFailure.CliUnavailable)]
    [InlineData(WindowsAppFailure.CliUnsupported)]
    public async Task ProcessFailuresAreClassifiedWithoutDiagnosticDetails(object failure)
    {
        var cli = new FakeCli(Account)
        {
            Handler = (_, _) => throw new WindowsAppConnectionException((WindowsAppFailure)failure, $"secret {ConnectionTestData.Uri}")
        };
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(default));
        Assert.Equal((WindowsAppFailure)failure, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OutputLimitChecksBothStreamsAndUtf8Bytes(bool stderr, bool multibyte)
    {
        var output = new string(multibyte ? 'é' : 's', AzureCliProcess.MaximumOutputBytes / (multibyte ? 2 : 1) + 1);
        var error = await Assert.ThrowsAsync<DevBoxCatalogException>(() =>
            Refresh(new(0, stderr ? "{\"value\":[]}" : output, stderr ? output : "")));
        Assert.Equal(WindowsAppFailure.MalformedResponse, error.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task CancellationBeforeOrBetweenCallsCannotPublishSnapshot(int cancelAfter)
    {
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(Account, Page([], $"{Endpoint}devboxes?next=1"), Page([]))
        {
            AfterRun = calls => { if (calls == cancelAfter) cancellation.Cancel(); }
        };
        if (cancelAfter == 0) cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token));
        Assert.Equal(cancelAfter, cli.Commands.Count);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task CancellationDuringRequestIsPassedToOwnedProcessAndSanitized()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = new FakeCli(Account)
        {
            Handler = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return Page([]);
            }
        };
        var pending = new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.DoesNotContain("https:", error.ToString());
    }

    [Fact]
    public async Task CancellationBetweenEndpointsNeverStartsNextEndpoint()
    {
        using var cancellation = new CancellationTokenSource();
        var cli = new FakeCli(Account, Page([Item()]))
        {
            Endpoints = [Endpoint, SecondEndpoint],
            AfterRun = calls => { if (calls == 4) cancellation.Cancel(); }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DevBoxCatalogService(cli).RefreshAsync(cancellation.Token));
        Assert.Equal(4, cli.Commands.Count);
    }

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("example.region.devcenter.azure.com/secret")]
    [InlineData("https://example.region.devcenter.azure.com/secret")]
    [InlineData("example.region.devcenter.azure.com\nsecret")]
    public void ExceptionCannotRenderUnvalidatedHostText(string host)
    {
        var error = new DevBoxCatalogException(WindowsAppFailure.ApiUnavailable, host);
        Assert.Null(error.EndpointHost);
        AssertSafe(error);
    }

    private static JsonObject Item(string name = "devbox-01", string project = "project.one") => new()
    {
        ["name"] = name, ["projectName"] = project, ["poolName"] = "pool.one",
        ["powerState"] = "Running", ["provisioningState"] = "Succeeded"
    };

    private static AzureCliResult Page(IEnumerable<JsonObject> items, string? next = null)
    {
        var page = new JsonObject { ["value"] = new JsonArray(items.Select(i => (JsonNode)i.DeepClone()).ToArray()) };
        if (next is not null) page["nextLink"] = next;
        return new(0, page.ToJsonString(), "");
    }

    private static Task<DevBoxCatalogSnapshot> Refresh(AzureCliResult page) =>
        new DevBoxCatalogService(new FakeCli(Account, page)).RefreshAsync(default);

    private static void AssertSafe(Exception error)
    {
        Assert.Null(error.InnerException);
        foreach (var sensitive in new[] { "secret", "https:", "http:", "ms-cloudpc:", "user@example.com", ConnectionTestData.Tenant.ToString(), SubscriptionId.ToString() })
            Assert.DoesNotContain(sensitive, error.ToString());
    }

    private sealed class FakeCli(params AzureCliResult[] responses) : IAzureCliProcess
    {
        private readonly Queue<AzureCliResult> responses = new(responses);
        public List<AzureCliCommand> Commands { get; } = [];
        public Action<int>? AfterRun;
        public Func<AzureCliCommand, CancellationToken, Task<AzureCliResult>>? Handler;
        public bool AutoDiscover = true;
        public IReadOnlyList<Uri> Endpoints = [Endpoint];

        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(command.Timeout, timeout);
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            AfterRun?.Invoke(Commands.Count);
            var args = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList;
            if (AutoDiscover && args[0] == "account" && args[1] == "list") return Task.FromResult(Subscriptions(Subscription()));
            if (AutoDiscover && args[0] == "rest" && args[^1].StartsWith("https://management.azure.com/", StringComparison.Ordinal))
                return Task.FromResult(Page(Endpoints.Select((endpoint, index) => Center($"center-{index}", endpoint: endpoint))));
            return responses.TryDequeue(out var result) ? Task.FromResult(result) : Handler!(command, cancellationToken);
        }
    }
}
