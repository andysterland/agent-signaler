using System.Collections.Concurrent;
using AgentSignaler.Dashboard;
using AgentSignaler.Service;

namespace AgentSignaler.Integration.Tests;

public sealed class WindowsAppConnectionControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactPublishesLiveProgressWhileBusyAndSkipsUnneededStages(bool reuse)
    {
        var h = new Harness();
        h.Platform.Reuse = reuse;
        var messages = new ConcurrentQueue<string>();
        h.Controller.Changed += id =>
        {
            Assert.Equal(h.Id, id);
            var state = h.Controller.State(id);
            if (!state.IsBusy) return;
            Assert.True(h.Controller.IsBusy(id));
            Assert.False(state.CompactLaunchEnabled);
            AssertSafe(state.Message);
            messages.Enqueue(state.Message);
        };
        Assert.True((await h.Actions.OpenWindowsAppAsync(h.Id, compact: true)).Succeeded);
        var expected = new List<string>
        {
            "Reading the saved Dev Box connection...",
            "Searching local windows for an existing Windows App connection..."
        };
        if (!reuse)
        {
            expected.Add("No matching local window found. Refreshing the Dev Box connection...");
            expected.Add("Launching Windows App...");
        }
        Assert.Equal(expected, messages);
        Assert.False(h.Controller.State(h.Id).IsBusy);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("reuse")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("exit")]
    [InlineData("exception")]
    public async Task CompactProgressClosesBeforeRefreshOrErrorNavigationOnEveryOutcome(string outcome)
    {
        var h = new Harness();
        h.Platform.Reuse = outcome == "reuse";
        if (outcome == "failure") h.Stored = null;
        if (outcome is "cancel" or "exit" or "exception")
            h.BeforeRead = token =>
            {
                if (outcome == "exception") throw new InvalidOperationException("Programming error");
                h.Exiting = outcome == "exit";
                h.Controller.Cancel(h.Id);
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            };
        var events = new List<string>();
        var actions = new WindowsAppConnectionActions(h.Controller,
            () => { events.Add("refresh"); return Task.CompletedTask; }, () => h.Exiting,
            () => events.Add("restore"), _ => events.Add("error"),
            (_, _) => { events.Add("details"); return Task.CompletedTask; },
            id =>
            {
                Assert.Equal(h.Id, id);
                Assert.False(h.Controller.IsBusy(id));
                events.Add("show");
                return new ProgressScope(() => events.Add("close"));
            });
        if (outcome == "exception")
            await Assert.ThrowsAsync<InvalidOperationException>(() => actions.OpenWindowsAppAsync(h.Id, compact: true));
        else
            await actions.OpenWindowsAppAsync(h.Id, compact: true);

        Assert.Equal(outcome switch
        {
            "exception" or "exit" => ["show", "close"],
            "failure" or "cancel" => ["show", "close", "refresh", "restore", "error", "details"],
            _ => new[] { "show", "close", "refresh" }
        }, events);
        Assert.False(h.Controller.IsBusy(h.Id));
    }

    [Fact]
    public async Task DetailsLaunchDoesNotShowCompactProgress()
    {
        var h = new Harness();
        var actions = new WindowsAppConnectionActions(h.Controller, () => Task.CompletedTask,
            () => false, () => { }, _ => { }, (_, _) => Task.CompletedTask,
            _ => throw new InvalidOperationException("Details should use its existing progress UI"));
        Assert.True((await actions.OpenWindowsAppAsync(h.Id)).Succeeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalCompactActionSkipsLaunchProgressAndOnlyRestoresDetailsOnFailure(bool succeeded)
    {
        var h = new Harness();
        var events = new List<string>();
        var actions = new WindowsAppConnectionActions(h.Controller,
            () => { events.Add("refresh"); return Task.CompletedTask; }, () => false,
            () => events.Add("restore"), _ => events.Add("error"),
            (_, _) => { events.Add("details"); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("Local must not show launch progress."),
            id => { Assert.Equal(h.Id, id); events.Add("local"); return Task.FromResult(new WindowsAppOperationResult(succeeded, "Local result")); },
            id => id == h.Id);
        var result = await actions.OpenWindowsAppAsync(h.Id, compact: true);
        Assert.Equal(succeeded, result.Succeeded);
        Assert.Equal(!succeeded, result.RestoreDetails);
        Assert.Equal(succeeded ? ["local", "refresh"] : new[] { "local", "refresh", "restore", "error", "details" }, events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailsAndCompactReuseStoredDevBoxWithoutRefreshingOrRestoringDashboard(bool compact)
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping with { DevBoxName = "mapped-not-machine-name" } };
        var stored = h.Stored;
        h.Controller.Observe(h.Id, ConnectionTestData.Mapping);
        h.Platform.Reuse = true;
        var result = await h.Actions.OpenWindowsAppAsync(h.Id, compact);
        Assert.True(result.Succeeded);
        Assert.False(result.RestoreDetails);
        Assert.Equal("Brought the existing Windows App connection to the foreground.", result.Message);
        Assert.Equal(result.Message, h.Controller.State(h.Id).Message);
        Assert.Same(stored, h.Stored);
        Assert.Same(stored, h.Controller.State(h.Id).Mapping);
        Assert.Equal("mapped-not-machine-name", Assert.Single(h.Platform.Names));
        Assert.Equal(0, h.Platform.ProtocolCalls);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Cli.Commands);
        Assert.Empty(h.Platform.Launched);
        Assert.Empty(h.Details);
        Assert.Equal(["refresh-ui"], h.Events);
        Assert.False(h.Controller.IsBusy(h.Id));
        AssertSafe(h.Controller.State(h.Id).ToString());
        Assert.DoesNotContain(stored.DevBoxName, h.Controller.State(h.Id).ToString());
    }

    [Theory]
    [InlineData(false, false, "Opened in Windows App.")]
    [InlineData(false, true, "Brought the existing Windows App connection to the foreground.")]
    [InlineData(true, false, "Opened the last known connection in Windows App without refreshing.")]
    [InlineData(true, true, "Brought the existing Windows App connection to the foreground without refreshing.")]
    public async Task SuccessMessagesReflectFinalActivationDisposition(bool cached, bool reuseAtActivation, string message)
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping };
        h.Platform.ReuseAtActivation = reuseAtActivation;
        var result = await h.Controller.ExecuteAsync(h.Id, cached ? WindowsAppOperation.OpenLastKnown : WindowsAppOperation.Open);
        Assert.True(result.Succeeded);
        Assert.Equal(message, result.Message);
        Assert.Equal(message, h.Controller.State(h.Id).Message);
        Assert.Equal(cached ? 0 : 1, h.Saves);
        Assert.Equal(reuseAtActivation ? 0 : 1, h.Platform.Launched.Count);
        Assert.Equal(2, h.Platform.Names.Count);
        Assert.All(h.Platform.Names, name => Assert.Equal(h.Stored!.DevBoxName, name));
        Assert.False(h.Controller.IsBusy(h.Id));
        AssertSafe(h.Controller.State(h.Id).ToString());
    }

    [Fact]
    public async Task CachedEarlyReuseIsOfflineAndKeepsOriginalRetrievalTime()
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping with { ConnectionUriRetrievedAtUtc = DateTimeOffset.UnixEpoch } };
        var stored = h.Stored;
        h.Platform.Reuse = true;
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.OpenLastKnown);
        Assert.True(result.Succeeded);
        Assert.Equal("Brought the existing Windows App connection to the foreground without refreshing.", result.Message);
        Assert.Same(stored, h.Controller.State(h.Id).Mapping);
        Assert.Same(stored, h.Stored);
        Assert.Equal(stored.DevBoxName, Assert.Single(h.Platform.Names));
        Assert.Equal(0, h.Platform.ProtocolCalls);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Cli.Commands);
        Assert.Empty(h.Platform.Launched);
        Assert.False(h.Controller.IsBusy(h.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReuseFailureReleasesBusyStateAndAllowsExplicitRetry(bool cached)
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping };
        h.Platform.Reuse = true;
        h.Platform.Failure = WindowsAppFailure.ActivationFailed;
        var cachedResult = cached ? await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.OpenLastKnown) : null;
        var result = cached
            ? new WindowsAppActionResult(cachedResult!.Succeeded, cachedResult.Message, false)
            : await h.Actions.OpenWindowsAppAsync(h.Id, compact: true);
        Assert.False(result.Succeeded);
        Assert.Equal(!cached, result.RestoreDetails);
        AssertSafe(result.Message);
        AssertSafe(h.Controller.State(h.Id).ToString());
        Assert.Equal("Unavailable", h.Controller.State(h.Id).Status);
        Assert.False(h.Controller.IsBusy(h.Id));
        Assert.True(h.Controller.State(h.Id).ActionsEnabled);
        Assert.True(h.Controller.State(h.Id).CompactLaunchEnabled);
        Assert.Equal(0, h.Platform.ProtocolCalls);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Cli.Commands);
        Assert.Empty(h.Platform.Launched);
        h.Platform.Failure = null;
        Assert.True((await h.Controller.ExecuteAsync(h.Id, cached ? WindowsAppOperation.OpenLastKnown : WindowsAppOperation.Open)).Succeeded);
    }

    [Fact]
    public async Task DetailsConnectionUriFollowsSavedMappingRefreshAndClear()
    {
        var h = new Harness();
        h.Controller.Observe(h.Id, h.Stored);
        Assert.Null(h.Controller.State(h.Id).Mapping?.LastKnownConnectionUri);

        Assert.True((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Refresh)).Succeeded);
        Assert.Equal(ConnectionTestData.Uri, h.Controller.State(h.Id).Mapping?.LastKnownConnectionUri);
        Assert.Equal(h.Stored!.ConnectionUriRetrievedAtUtc,
            h.Controller.State(h.Id).Mapping?.ConnectionUriRetrievedAtUtc);
        Assert.Empty(h.Platform.Launched);

        Assert.True((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Clear)).Succeeded);
        Assert.Null(h.Controller.State(h.Id).Mapping?.LastKnownConnectionUri);
        Assert.Null(h.Stored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetailsAndCompactUseSameFreshLaunchAndReadStorageNotObservedCard(bool compact)
    {
        var h = new Harness();
        var reads = 0;
        h.BeforeRead = _ =>
        {
            Assert.Equal(1, ++reads);
            return Task.CompletedTask;
        };
        h.Controller.Observe(h.Id, ConnectionTestData.CachedMapping);
        h.Stored = ConnectionTestData.Mapping with { DevBoxName = "different-devbox" };
        Assert.True((await h.Actions.OpenWindowsAppAsync(h.Id, compact)).Succeeded);
        Assert.Contains("different-devbox", h.Cli.Commands.Last().Last());
        Assert.Equal(["account", "rest"], h.Cli.Commands.Select(c => c[0]));
        Assert.Equal(["save", "launch", "refresh-ui"], h.Events);
        Assert.Equal(ConnectionTestData.Uri, Assert.Single(h.Platform.Launched));
        Assert.Equal("different-devbox", h.Stored!.DevBoxName);
        Assert.Empty(h.Details);
    }

    [Fact]
    public async Task SelectedTenantIsUsedButSignInNeverReplacesTheStoredIdentity()
    {
        var h = new Harness
        {
            Stored = ConnectionTestData.Mapping with { AzureTenantId = Guid.NewGuid() }
        };
        var stored = h.Stored;
        h.Controller.Observe(h.Id, stored);
        Assert.False((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn,
            Selection(ConnectionTestData.Mapping))).Succeeded);
        Assert.Equal(["login", "--tenant", ConnectionTestData.Tenant.ToString()], h.Cli.Commands.First());
        Assert.Equal(stored, h.Stored);
        Assert.Equal(stored, h.Controller.State(h.Id).Mapping);
        Assert.Equal(0, h.Saves);
        Assert.DoesNotContain(h.Cli.Commands, c => c[0] == "rest");
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData(WindowsAppOperation.SignIn)]
    [InlineData(WindowsAppOperation.Map)]
    public async Task CancellationDuringDiscoveryPreventsSaveEvenIfCliReturnsSuccessfully(object value)
    {
        var operation = (WindowsAppOperation)value;
        var h = new Harness();
        h.Cli.Run = (command, _) =>
        {
            var name = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[0];
            if (name == "rest")
            {
                h.Controller.Cancel(h.Id);
                return Task.FromResult(new AzureCliResult(0, ConnectionTestData.Response, ""));
            }
            return Task.FromResult(new AzureCliResult(0, name == "account" ? ConnectionTestData.Account : "[]", ""));
        };
        var result = await h.Controller.ExecuteAsync(h.Id, operation, Selection(h.Stored!));
        Assert.False(result.Succeeded);
        Assert.Equal(operation == WindowsAppOperation.SignIn ? ["login", "account", "rest"] : new[] { "account", "rest" },
            h.Cli.Commands.Select(c => c[0]));
        Assert.Equal(ConnectionTestData.Mapping, h.Stored);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task InvalidRequestsReturnExplicitErrorsInsteadOfSuccessOrSilentNoOps()
    {
        var h = new Harness();
        Assert.False((await h.Controller.OpenWindowsAppAsync(Guid.Empty)).Succeeded);
        var result = await h.Controller.ExecuteAsync(h.Id, (WindowsAppOperation)100);
        Assert.False(result.Succeeded);
        Assert.Equal(new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping).Message, result.Message);
        Assert.Empty(h.Cli.Commands);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task MissingMappingRestoresAndOpensCorrectDetailsWithSafePrompt()
    {
        var h = new Harness { Stored = null };
        var result = await h.Actions.OpenWindowsAppAsync(h.Id, compact: true);
        Assert.False(result.Succeeded);
        Assert.True(result.RestoreDetails);
        Assert.Equal("Configure a Dev Box connection to continue.", result.Message);
        Assert.Equal(h.Id, Assert.Single(h.Details));
        Assert.Equal(["refresh-ui", "restore", "error", "details"], h.Events);
        Assert.Empty(h.Cli.Commands);
        Assert.Equal("Not configured", h.Controller.State(h.Id).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureRestoresDetailsButNeverAutomaticallyLaunchesCache(bool cached)
    {
        var h = new Harness { Stored = cached ? ConnectionTestData.CachedMapping : ConnectionTestData.Mapping };
        h.Cli.Run = (_, _) => Task.FromResult(new AzureCliResult(1, ConnectionTestData.Uri, "az login secret"));
        var result = await h.Actions.OpenWindowsAppAsync(h.Id, compact: true);
        Assert.False(result.Succeeded);
        Assert.Equal(h.Id, Assert.Single(h.Details));
        Assert.Empty(h.Platform.Launched);
        Assert.Equal(0, h.Saves);
        Assert.Equal("Sign-in required", h.Controller.State(h.Id).Status);
        Assert.Equal(cached, h.Controller.State(h.Id).CanOpenLastKnown);
        AssertSafe(result.Message);
        AssertSafe(h.Controller.State(h.Id).ToString());
    }

    [Fact]
    public async Task CachedLaunchIsExplicitTimestampedAndRejectedFromCompact()
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping with
            { ConnectionUriRetrievedAtUtc = ConnectionTestData.RetrievedAt.AddYears(-10) } };
        h.Controller.Observe(h.Id, h.Stored);
        Assert.Equal(h.Stored.ConnectionUriRetrievedAtUtc!.Value.ToLocalTime().ToString("G"),
            h.Controller.State(h.Id).LastRefresh);
        Assert.DoesNotContain(typeof(WindowsAppConnectionController).GetMethods().SelectMany(method => method.GetParameters()),
            parameter => parameter.Name == "compact");
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.OpenLastKnown);
        Assert.True(result.Succeeded);
        Assert.Equal(ConnectionTestData.Uri, Assert.Single(h.Platform.Launched));
        Assert.Empty(h.Cli.Commands);
        Assert.Equal(0, h.Saves);
        Assert.Equal(ConnectionTestData.RetrievedAt.AddYears(-10), h.Stored.ConnectionUriRetrievedAtUtc);
    }

    [Fact]
    public async Task CacheDisappearingFromStorageCannotLaunchStaleObservedData()
    {
        var h = new Harness();
        h.Controller.Observe(h.Id, ConnectionTestData.CachedMapping);
        h.Stored = null;
        Assert.False((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.OpenLastKnown)).Succeeded);
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData(WindowsAppOperation.Map)]
    [InlineData(WindowsAppOperation.Refresh)]
    [InlineData(WindowsAppOperation.SignIn)]
    public async Task DiscoverySavesVerifiedIdentityAndTimeWithoutLaunching(object value)
    {
        var operation = (WindowsAppOperation)value;
        var h = new Harness();
        if (operation == WindowsAppOperation.Map) h.Stored = null;
        var result = await h.Controller.ExecuteAsync(h.Id, operation, Selection(ConnectionTestData.Mapping));
        Assert.True(result.Succeeded);
        Assert.Equal(1, h.Saves);
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Equal("Ready", h.Controller.State(h.Id).Status);
        Assert.Empty(h.Platform.Launched);
        Assert.Equal(operation == WindowsAppOperation.SignIn ? ["login", "account", "rest"] : new[] { "account", "rest" },
            h.Cli.Commands.Select(c => c[0]));
        if (operation == WindowsAppOperation.SignIn)
            Assert.Equal(["login", "--tenant", ConnectionTestData.Tenant.ToString()], h.Cli.Commands.First());
    }

    [Fact]
    public async Task SignInUsesStoredTenantWhenSelectedTenantIsEmptyAndRefreshesOnlyStoredMapping()
    {
        var h = new Harness();
        var selection = Selection(h.Stored!) with { AzureTenantId = Guid.Empty,
            DevBox = Selection(h.Stored!).DevBox with { DevBoxName = "unsaved-devbox" } };
        Assert.True((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn, selection)).Succeeded);
        Assert.Equal(["login", "--tenant", ConnectionTestData.Tenant.ToString()], h.Cli.Commands.First());
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Equal(1, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task MapSavesWhenRemoteResponseIncludesAllAzureConnectionProperties()
    {
        var h = new Harness { Stored = null };
        h.Cli.Run = (command, _) =>
        {
            var account = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[0] == "account";
            var response = System.Text.Json.JsonSerializer.Serialize(new
            {
                cloudPcConnectionUrl = ConnectionTestData.Uri,
                rdpConnectionUrl = "ms-avd:connect?resourceid=ignored",
                webUrl = "https://example.test/ignored"
            });
            return Task.FromResult(new AzureCliResult(0, account ? ConnectionTestData.Account : response, ""));
        };

        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, Selection(ConnectionTestData.Mapping));

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, h.Saves);
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Equal(h.Stored, h.Controller.State(h.Id).Mapping);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task SignInWithoutAnyTenantUsesLoginWithoutInventingTenant()
    {
        var h = new Harness { Stored = null };
        Assert.True((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn)).Succeeded);
        Assert.Equal(["login"], h.Cli.Commands.First());
        Assert.Equal(["login", "account"], h.Cli.Commands.Select(c => c[0]));
        Assert.Equal(0, h.Saves);
        Assert.Null(h.Stored);
        Assert.Null(h.Controller.State(h.Id).Mapping);
    }

    [Fact]
    public async Task SignInWithUnsavedSelectionAndNoMappingOnlyValidatesAccount()
    {
        var h = new Harness { Stored = null };
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn,
            Selection(ConnectionTestData.Mapping));
        Assert.True(result.Succeeded);
        Assert.Equal(["login", "--tenant", ConnectionTestData.Tenant.ToString()], h.Cli.Commands.First());
        Assert.Equal(["login", "account"], h.Cli.Commands.Select(c => c[0]));
        Assert.Null(h.Stored);
        Assert.Null(h.Controller.State(h.Id).Mapping);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData(WindowsAppOperation.SignIn)]
    [InlineData(WindowsAppOperation.Refresh)]
    public async Task SignInAndRefreshWithAnotherUnsavedDevBoxRefreshTheStoredDevBoxOnly(object value)
    {
        var h = new Harness();
        var selection = Selection(ConnectionTestData.Mapping with { DevBoxName = "unsaved-devbox" });
        Assert.True((await h.Controller.ExecuteAsync(h.Id, (WindowsAppOperation)value, selection)).Succeeded);
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Contains(ConnectionTestData.Mapping.DevBoxName, h.Cli.Commands.Last().Last());
        Assert.DoesNotContain("unsaved-devbox", h.Cli.Commands.Last().Last());
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("malformed")]
    [InlineData("service-principal")]
    [InlineData("tenant")]
    public async Task SignInWithoutMappingStillRejectsInvalidAccount(string failure)
    {
        var h = new Harness { Stored = null };
        h.Cli.Run = (command, _) =>
        {
            var name = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[0];
            return Task.FromResult(name == "login" ? new AzureCliResult(0, "[]", "") :
                new AzureCliResult(failure == "nonzero" ? 1 : 0,
                    failure switch
                    {
                        "malformed" => "secret malformed",
                        "service-principal" => ConnectionTestData.Account.Replace("\"user\"", "\"servicePrincipal\""),
                        _ => ConnectionTestData.Account
                    }, ""));
        };
        var selection = failure == "tenant"
            ? Selection(ConnectionTestData.Mapping) with { AzureTenantId = Guid.NewGuid() } : null;
        Assert.False((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn, selection)).Succeeded);
        Assert.Null(h.Stored);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapRequiresAnExplicitNonNullSelectionEvenWhenMappingExists(bool nullItem)
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping };
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map,
            nullItem ? Selection(h.Stored) with { DevBox = null! } : null);
        Assert.False(result.Succeeded);
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Equal(h.Stored, h.Controller.State(h.Id).Mapping);
        Assert.Empty(h.Cli.Commands);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("storage")]
    [InlineData("account")]
    [InlineData("tenant")]
    public async Task ExplicitReplacementRollsBackMappingAndCacheOnFailure(string failure)
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping };
        var original = h.Stored;
        h.Controller.Observe(h.Id, original);
        if (failure == "storage") h.SaveFailure = new IOException("secret " + ConnectionTestData.Uri);
        else h.Cli.Run = (command, _) =>
        {
            var name = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[0];
            return Task.FromResult(name == "rest"
                ? new AzureCliResult(1, "", "secret Forbidden")
                : new AzureCliResult(0, failure switch
                {
                    "account" => ConnectionTestData.Account.Replace("USER@example.com", "other@example.com"),
                    "tenant" => ConnectionTestData.Account.Replace(ConnectionTestData.Tenant.ToString(), Guid.NewGuid().ToString()),
                    _ => ConnectionTestData.Account
                }, ""));
        };
        var replacement = Selection(original with { DevBoxName = "explicit-new-devbox" });
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, replacement);
        Assert.False(result.Succeeded);
        Assert.Equal(original, h.Stored);
        Assert.Equal(original, h.Controller.State(h.Id).Mapping);
        Assert.True(h.Controller.State(h.Id).CanOpenLastKnown);
        Assert.Equal(original.ConnectionUriRetrievedAtUtc!.Value.ToLocalTime().ToString("G"),
            h.Controller.State(h.Id).LastRefresh);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
        AssertSafe(result.Message);
    }

    [Fact]
    public async Task ExplicitReplacementResolvesWithoutCacheAndPublishesOnlyAfterPersistence()
    {
        WindowsAppConnection? requested = null;
        var resolver = new FakeResolver((mapping, _) =>
        {
            requested = mapping;
            return Task.FromResult(new ResolvedDevBoxConnection(new(ConnectionTestData.Uri),
                ConnectionTestData.Mapping.AzureAccountUpn, ConnectionTestData.Tenant, Guid.NewGuid(),
                ConnectionTestData.RetrievedAt));
        });
        var h = new Harness(resolver) { Stored = ConnectionTestData.CachedMapping };
        var original = h.Stored;
        h.Controller.Observe(h.Id, original);
        var entered = Signal();
        var complete = Signal();
        h.BeforeSave = async token => { entered.SetResult(); await complete.Task.WaitAsync(token); };
        var selected = Selection(original with { DevBoxName = "explicit-new-devbox" });
        var operation = h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, selected);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(requested);
        Assert.Null(requested.LastKnownConnectionUri);
        Assert.Null(requested.ConnectionUriRetrievedAtUtc);
        Assert.Equal(selected.DevBox.DevBoxName, requested.DevBoxName);
        Assert.Equal(original, h.Stored);
        Assert.Equal(original, h.Controller.State(h.Id).Mapping);
        complete.SetResult();
        Assert.True((await operation).Succeeded);
        Assert.Equal(original with { DevBoxName = selected.DevBox.DevBoxName }, h.Stored);
        Assert.Equal(h.Stored, h.Controller.State(h.Id).Mapping);
        Assert.Empty(h.Platform.Launched);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("tenant")]
    [InlineData("subscription")]
    [InlineData("missing-account")]
    public async Task MapIndependentlyRejectsMismatchedResolverIdentity(string mismatch)
    {
        var resolver = new FakeResolver((_, _) => Task.FromResult(new ResolvedDevBoxConnection(
            new(ConnectionTestData.Uri), mismatch switch
            {
                "account" => "other@example.com",
                "missing-account" => null!,
                _ => ConnectionTestData.Mapping.AzureAccountUpn
            },
            mismatch == "tenant" ? Guid.NewGuid() : ConnectionTestData.Tenant,
            mismatch == "subscription" ? Guid.Empty : Guid.NewGuid(), ConnectionTestData.RetrievedAt)));
        var h = new Harness(resolver) { Stored = ConnectionTestData.CachedMapping };
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map,
            Selection(h.Stored with { DevBoxName = "another-devbox" }));
        Assert.False(result.Succeeded);
        Assert.Equal(new WindowsAppConnectionException(WindowsAppFailure.AccountMismatch).Message, result.Message);
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Equal(h.Stored, h.Controller.State(h.Id).Mapping);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task GlobalCatalogBusyRejectsOnlyMapAndLeavesMappedLaunchAvailable()
    {
        var h = new Harness { CatalogBusy = true, Stored = ConnectionTestData.CachedMapping };
        h.Controller.Observe(h.Id, h.Stored);
        var state = h.Controller.State(h.Id);
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, Selection(h.Stored));
        Assert.False(result.Succeeded);
        Assert.Equal(new WindowsAppConnectionException(WindowsAppFailure.Busy).Message, result.Message);
        Assert.Equal(state, h.Controller.State(h.Id));
        Assert.Empty(h.Cli.Commands);
        Assert.False(h.Controller.IsBusy(h.Id));
        Assert.True((await h.Controller.OpenWindowsAppAsync(h.Id)).Succeeded);
        Assert.Single(h.Platform.Launched);
    }

    [Fact]
    public async Task MappingBusyRejectsAnotherMapOnSameMachine()
    {
        var h = new Harness();
        var entered = Signal();
        h.BeforeRead = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var operation = h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, Selection(h.Stored!));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, Selection(h.Stored!));
        Assert.False(result.Succeeded);
        Assert.Equal(new WindowsAppConnectionException(WindowsAppFailure.Busy).Message, result.Message);
        await h.Controller.CancelAndWaitAsync(h.Id);
        Assert.False((await operation).Succeeded);
        Assert.Empty(h.Cli.Commands);
        Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("account")]
    [InlineData("tenant")]
    [InlineData("timeout")]
    [InlineData("malformed")]
    public async Task FailedSignInOrPostLoginValidationNeverSavesOrLaunches(string failure)
    {
        var h = new Harness();
        h.Cli.Run = (command, _) =>
        {
            var name = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList[0];
            if (name == "login")
            {
                if (failure == "timeout") throw new WindowsAppConnectionException(WindowsAppFailure.TimedOut);
                return Task.FromResult(new AzureCliResult(failure == "nonzero" ? 1 : 0, "secret", ConnectionTestData.Uri));
            }
            Assert.Equal("account", name);
            var json = failure switch
            {
                "account" => ConnectionTestData.Account.Replace("USER@example.com", "other@example.com"),
                "tenant" => ConnectionTestData.Account.Replace(ConnectionTestData.Tenant.ToString(), Guid.NewGuid().ToString()),
                _ => "secret malformed " + ConnectionTestData.Uri
            };
            return Task.FromResult(new AzureCliResult(0, json, ""));
        };
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn, Selection(h.Stored!));
        Assert.False(result.Succeeded);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
        Assert.DoesNotContain(h.Cli.Commands, c => c[0] == "rest");
        Assert.Equal(ConnectionTestData.Mapping, h.Stored);
        AssertSafe(result.Message);
    }

    [Theory]
    [InlineData("Endpoint")]
    [InlineData("Project")]
    [InlineData("DevBox")]
    [InlineData("Upn")]
    [InlineData("Tenant")]
    public async Task InvalidSelectionsAreVisibleAndNeverCallCliOrSave(string field)
    {
        var h = new Harness();
        var selection = Selection(h.Stored!);
        selection = field switch
        {
            "Endpoint" => selection with { DevBox = selection.DevBox with { DevCenterEndpoint = new("https://invalid.example.org/") } },
            "Project" => selection with { DevBox = selection.DevBox with { ProjectName = "../bad" } },
            "DevBox" => selection with { DevBox = selection.DevBox with { DevBoxName = "guest hostname" } },
            "Upn" => selection with { AzureAccountUpn = "secret invalid" },
            _ => selection with { AzureTenantId = Guid.Empty }
        };
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Map, selection);
        Assert.False(result.Succeeded);
        Assert.Equal("Unavailable", h.Controller.State(h.Id).Status);
        AssertSafe(result.Message);
        Assert.Empty(h.Cli.Commands);
        Assert.Equal(0, h.Saves);
    }

    [Theory]
    [InlineData(WindowsAppOperation.Map)]
    [InlineData(WindowsAppOperation.SignIn)]
    [InlineData(WindowsAppOperation.Refresh)]
    [InlineData(WindowsAppOperation.Open)]
    [InlineData(WindowsAppOperation.OpenLastKnown)]
    [InlineData(WindowsAppOperation.Clear)]
    public async Task EveryOperationDisablesConflictingControlsAndUsesSharedGate(object value)
    {
        var operation = (WindowsAppOperation)value;
        var h = new Harness { Stored = ConnectionTestData.CachedMapping };
        var entered = Signal();
        h.BeforeRead = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
        var task = h.Controller.ExecuteAsync(h.Id, operation, Selection(h.Stored));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var state = h.Controller.State(h.Id);
        Assert.True(h.Controller.IsBusy(h.Id));
        Assert.True(state.IsBusy);
        Assert.False(state.FieldsEnabled);
        Assert.False(state.ActionsEnabled);
        Assert.False(state.CompactLaunchEnabled);
        Assert.False(state.CanOpenLastKnown);
        Assert.Equal(operation, state.Operation);
        Assert.False(string.IsNullOrWhiteSpace(state.Message));
        Assert.False((await h.Controller.OpenWindowsAppAsync(h.Id)).Succeeded);
        var error = await Assert.ThrowsAsync<WindowsAppConnectionException>(() =>
            h.Launcher.OpenAsync(h.Id, h.Stored));
        Assert.Equal(WindowsAppFailure.Busy, error.Failure);
        await h.Controller.CancelAndWaitAsync(h.Id);
        Assert.False((await task).Succeeded);
        Assert.False(h.Controller.IsBusy(h.Id));
        Assert.True(h.Controller.State(h.Id).FieldsEnabled);
        Assert.True(h.Controller.State(h.Id).ActionsEnabled);
        Assert.True(h.Controller.State(h.Id).CompactLaunchEnabled);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task DialogClosureCancelsSignInAndAwaitsOwnedCleanup()
    {
        var h = new Harness();
        var entered = Signal();
        var cancelled = Signal();
        var cleanup = Signal();
        h.Cli.Run = async (_, token) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.SetResult(); await cleanup.Task; }
            return new(0, "", "");
        };
        var operation = h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.SignIn, Selection(h.Stored!));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var closing = h.Controller.CancelAndWaitAsync(h.Id);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(closing.IsCompleted);
        Assert.True(h.Controller.IsBusy(h.Id));
        cleanup.SetResult();
        await closing;
        Assert.False((await operation).Succeeded);
        Assert.Single(h.Cli.Commands);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task ExitCancelsEveryMachineAndWaitsBeforeStorageCanBeDisposed()
    {
        var h = new Harness();
        var entered = new SemaphoreSlim(0);
        var cancelled = new SemaphoreSlim(0);
        var cleanup = Signal();
        h.BeforeRead = async token =>
        {
            entered.Release();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.Release(); await cleanup.Task; }
        };
        var first = h.Controller.OpenWindowsAppAsync(h.Id);
        var second = h.Controller.ExecuteAsync(Guid.NewGuid(), WindowsAppOperation.Refresh);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        var exit = h.Controller.StopAsync();
        Assert.True(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(exit.IsCompleted);
        cleanup.SetResult();
        await exit;
        Assert.False((await first).Succeeded);
        Assert.False((await second).Succeeded);
        Assert.False((await h.Controller.OpenWindowsAppAsync(h.Id)).Succeeded);
        Assert.Equal(0, h.Saves);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task ExitDoesNotReopenDashboardFromCancelledCompactLaunch()
    {
        var h = new Harness();
        var entered = Signal();
        h.BeforeRead = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var opening = h.Actions.OpenWindowsAppAsync(h.Id, compact: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Exiting = true;
        await h.Controller.StopAsync();
        Assert.False((await opening).Succeeded);
        Assert.Empty(h.Details);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task ClearOnlyInvokesMappingRemovalAndResetsPresentation()
    {
        var h = new Harness { Stored = ConnectionTestData.CachedMapping };
        Assert.True((await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Clear)).Succeeded);
        Assert.Null(h.Stored);
        Assert.Equal(["clear"], h.Events);
        Assert.Equal("Not configured", h.Controller.State(h.Id).Status);
        Assert.False(h.Controller.State(h.Id).CanOpenLastKnown);
        Assert.Null(h.Controller.State(h.Id).LastRefresh);
        Assert.Empty(h.Cli.Commands);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task ActivationFailureRetainsNewCacheAndFailureStatusForExplicitRetry()
    {
        var h = new Harness();
        h.Platform.Failure = WindowsAppFailure.ActivationFailed;
        Assert.False((await h.Actions.OpenWindowsAppAsync(h.Id, compact: true)).Succeeded);
        Assert.Equal(ConnectionTestData.CachedMapping, h.Stored);
        Assert.Equal("Unavailable", h.Controller.State(h.Id).Status);
        Assert.True(h.Controller.State(h.Id).CanOpenLastKnown);
        Assert.Equal(h.Id, Assert.Single(h.Details));
        AssertSafe(h.Controller.State(h.Id).ToString());
    }

    [Fact]
    public async Task ClassifiedPersistenceFailuresNeverExposeStorageDetails()
    {
        var h = new Harness { SaveFailure = new IOException("secret " + ConnectionTestData.Uri) };
        var result = await h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Refresh);
        Assert.False(result.Succeeded);
        AssertSafe(result.Message);
        Assert.Equal(ConnectionTestData.Mapping, h.Stored);
        Assert.Empty(h.Platform.Launched);
    }

    [Fact]
    public async Task ProgrammingErrorsAreNotBroadlyCaughtAndGateIsStillReleased()
    {
        var h = new Harness { SaveFailure = new InvalidOperationException("programming error") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Controller.ExecuteAsync(h.Id, WindowsAppOperation.Refresh));
        Assert.False(h.Controller.IsBusy(h.Id));
        await h.Controller.StopAsync();
    }

    [Fact]
    public void WindowsAppLabelsAndSafePresentationAreShared()
    {
        Assert.Equal("Open My Dev Box in Windows App", WindowsAppConnectionController.LaunchLabel("My Dev Box"));
        var state = new WindowsAppConnectionState(ConnectionTestData.CachedMapping, "Ready", "");
        AssertSafe(state.ToString());
        AssertSafe(Selection(ConnectionTestData.Mapping).ToString());
        Assert.Equal(["Not configured", "Unavailable", "Sign-in required"],
            Enum.GetValues<WindowsAppFailure>().Select(f => new WindowsAppConnectionException(f).Status).Distinct());
    }

    [Fact]
    public void WinUiWiresSharedActionsCancellationAndDestructiveSecondaryConfirmation()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var main = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", "MainWindow.cs"));
        var compact = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", "CompactWindow.cs"));
        Assert.Contains("async id => await OpenWindowsAppAsync(id, compact: true)", main);
        Assert.Contains("await ExecuteRuntimeConnectionAsync(id, operation, ReadSelection())", main);
        Assert.Matches(@"dialog\.Closing \+= \(sender, args\) =>\s*\{\s*" +
            @"if \(savingDetails && !_exiting\)\s*\{\s*args\.Cancel = true;\s*return;\s*\}\s*" +
            @"_runtime\.CancelWindowsApp\(id\);", main);
        Assert.Contains("await connections.CancelAndWaitAsync(id)", main);
        Assert.Contains("SecondaryButtonText = \"Clear connection mapping\"", main);
        Assert.Contains("confirmClear.ShowAsync() == ContentDialogResult.Secondary", main);
        Assert.Contains("await _runtime.ShutdownAsync()", main);
        Assert.DoesNotContain("_store?.Dispose()", main);
        Assert.Contains("Func<Guid, Task> connect", compact);
        Assert.Contains("tile.Card.Button.IsEnabled = tile.Connect.IsEnabled = !_isBusy(id)", compact);
        Assert.Contains("WindowsAppConnectionController.LaunchLabel(name)", main);
        Assert.Contains("MachineNavigation.Name(machine)", main);
        Assert.Contains("MachineNavigation.Order(_cardMap.Values, c => c.Machine)", main);
        Assert.Contains("MachineNavigation.Order(machines, machine => machine)", compact);
        Assert.Contains("new MachineCard(machine, async () => await ActivateMachineAsync(machine.MachineId))", main);
        Assert.Matches(@"MachineNavigation.IsLocal\(card.Machine\)\)\s*await OpenWindowsAppAsync\(id\);", main);
        Assert.Contains("new MenuFlyoutItem { Text = \"Machine details\" }", main);
        Assert.Contains("details.Click += async (_, _) => await ShowDetailsAsync(machine.MachineId)", main);
        Assert.Contains("MachineNavigation.IsLocal(machine) ? \"Return to local\" : \"Connect\"", compact);
        Assert.Contains("Content = \"Save mapping\"", main);
        Assert.Contains("RunConnectionAsync(WindowsAppOperation.Map)", main);
        Assert.Contains("ExecuteRuntimeConnectionAsync(id, operation, ReadSelection())", main);
        Assert.Contains("DevBoxMappingPresentation.CanSaveMapping(busy, catalogBusy, ReadSelection() is not null)", main);
        Assert.DoesNotContain("new WindowsAppConnectionController", main);
        Assert.Contains("DevBoxMappingPresentation.CreatePicker(snapshot, mapping)", main);
        Assert.Contains("DevBoxMappingPresentation.TileText(machine.WindowsAppConnection)", main);
        Assert.Contains("card.MinHeight = MachineCardPresentation.MinimumHeight", main);
        Assert.Contains("card.Height = double.NaN", main);
        Assert.Contains("picker.StartBringIntoView()", main);
        Assert.Contains("picker.Focus(FocusState.Programmatic)", main);
        Assert.DoesNotContain("WindowsAppMappingFields", main);
        Assert.DoesNotContain("WindowsAppOperation.Discover", main);
    }

    [Fact]
    public void CompactContextMenuWiresConnectRestoreAndGracefulExit()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AgentSignaler.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var main = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", "MainWindow.cs"));
        var compact = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentSignaler.Dashboard", "CompactWindow.cs"));
        Assert.Contains("new MachineCard(machine, async () => await _connect(id), miniature: true)", compact);
        Assert.Contains("new MenuFlyoutItem { Text = \"Connect\" }", compact);
        Assert.Contains("connect.Click += async (_, _) => await _connect(id)", compact);
        Assert.Contains("new MenuFlyoutItem { Text = \"Show full dashboard\" }", compact);
        Assert.Contains("restore.Click += (_, _) => _restore()", compact);
        Assert.Contains("new MenuFlyoutItem { Text = \"Exit\" }", compact);
        Assert.Contains("exit.Click += async (_, _) => await _exit()", compact);
        Assert.Matches(@"menu.Items.Add\(connect\);\s*menu.Items.Add\(restore\);\s*menu.Items.Add\(exit\);", compact);
        Assert.Matches(@"new Border\s*\{[^}]*Child = card.Button,\s*ContextFlyout = menu", compact);
        Assert.Contains("tile.Card.Button.IsEnabled = tile.Connect.IsEnabled = !_isBusy(id)", compact);
        Assert.Matches(@"new CompactWindow\(ShowDashboard,[\s\S]*?compact: true\)[\s\S]*?ExitAsync\);", main);
        Assert.Contains("_compactWindow?.CloseForExit()", main);
        Assert.Contains("Application.Current.Exit()", main);
    }

    private sealed class ProgressScope(Action close) : IDisposable
    {
        public void Dispose() => close();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static DevBoxMappingSelection Selection(WindowsAppConnection mapping) => new(
        new(mapping.DevCenterEndpoint, mapping.ProjectName, "pool-one", mapping.DevBoxName,
            "Running", "Succeeded", null, null, null, null),
        mapping.AzureAccountUpn, mapping.AzureTenantId);
    private static void AssertSafe(string value)
    {
        Assert.DoesNotContain("secret", value);
        Assert.DoesNotContain("ms-cloudpc:", value);
        Assert.DoesNotContain("user@example.com", value);
        Assert.DoesNotContain(ConnectionTestData.Mapping.DevBoxName, value);
        Assert.DoesNotContain("987654", value);
    }

    private sealed class Harness
    {
        public Guid Id { get; } = Guid.NewGuid();
        public WindowsAppConnection? Stored = ConnectionTestData.Mapping;
        public Func<CancellationToken, Task>? BeforeRead;
        public Func<CancellationToken, Task>? BeforeSave;
        public Exception? SaveFailure;
        public int Saves;
        public bool Exiting;
        public bool CatalogBusy;
        public ConcurrentQueue<string> Events { get; } = new();
        public List<Guid> Details { get; } = [];
        public FakeCli Cli { get; } = new();
        public FakePlatform Platform { get; }
        public WindowsAppLauncher Launcher { get; }
        public WindowsAppConnectionController Controller { get; }
        public WindowsAppConnectionActions Actions { get; }

        public Harness(IDevBoxConnectionResolver? connectionResolver = null)
        {
            var gate = new WindowsAppOperationGate();
            var resolver = connectionResolver ?? new DevBoxConnectionResolver(Cli, new FakeClock());
            Platform = new FakePlatform(Events);
            async Task Persist(Guid id, WindowsAppConnection value, CancellationToken token)
            {
                Assert.Equal(Id, id);
                if (BeforeSave is not null) await BeforeSave(token);
                token.ThrowIfCancellationRequested();
                if (SaveFailure is not null) throw SaveFailure;
                Stored = value;
                Saves++;
                Events.Enqueue("save");
            }
            Launcher = new(resolver, Platform, Persist, gate);
            Controller = new(async (_, token) =>
            {
                if (BeforeRead is not null) await BeforeRead(token);
                return Stored;
            }, Persist, (id, token) =>
            {
                Assert.Equal(Id, id);
                token.ThrowIfCancellationRequested();
                Stored = null;
                Events.Enqueue("clear");
                return Task.CompletedTask;
            }, Cli, resolver, Launcher, gate, () => CatalogBusy);
            Actions = new(Controller, () =>
            {
                Controller.Observe(Id, Stored);
                Events.Enqueue("refresh-ui");
                return Task.CompletedTask;
            }, () => Exiting, () => Events.Enqueue("restore"), message =>
            {
                AssertSafe(message);
                Events.Enqueue("error");
            }, (id, message) =>
            {
                AssertSafe(message);
                Events.Enqueue("details");
                Details.Add(id);
                return Task.CompletedTask;
            });
        }
    }

    private sealed class FakeResolver(
        Func<WindowsAppConnection, CancellationToken, Task<ResolvedDevBoxConnection>> resolve) : IDevBoxConnectionResolver
    {
        public Task<ResolvedDevBoxConnection> ResolveAsync(WindowsAppConnection mapping, CancellationToken cancellationToken) =>
            resolve(mapping, cancellationToken);
    }

    private sealed class FakeClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ConnectionTestData.RetrievedAt;
    }

    private sealed class FakeCli : IAzureCliProcess
    {
        public ConcurrentQueue<string[]> Commands { get; } = new();
        public Func<AzureCliCommand, CancellationToken, Task<AzureCliResult>>? Run;
        public Task<AzureCliResult> RunAsync(AzureCliCommand command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Assert.Equal(command.Timeout, timeout);
            cancellationToken.ThrowIfCancellationRequested();
            var arguments = command.CreateStartInfo(@"C:\CLI\az.exe").ArgumentList.ToArray();
            Commands.Enqueue(arguments);
            return Run?.Invoke(command, cancellationToken) ?? Task.FromResult(new AzureCliResult(0,
                arguments[0] switch { "login" => "[]", "account" => ConnectionTestData.Account, _ => ConnectionTestData.Response }, ""));
        }
    }

    private sealed class FakePlatform(ConcurrentQueue<string> events) : IWindowsAppPlatform
    {
        public void MinimizeSessions() => throw new InvalidOperationException("Must not minimize remote connections.");
        public List<string> Launched { get; } = [];
        public WindowsAppFailure? Failure;
        public bool Reuse;
        public bool ReuseAtActivation;
        public int ProtocolCalls;
        public List<string> Names { get; } = [];
        public bool IsProtocolAvailable() { ProtocolCalls++; return true; }
        public WindowsAppActivationDisposition TryActivateExisting(string devBoxName)
        {
            Names.Add(devBoxName);
            if (Reuse && Failure is { } failure) throw new WindowsAppConnectionException(failure);
            return Reuse ? WindowsAppActivationDisposition.ExistingWindowActivated : WindowsAppActivationDisposition.NoExistingWindow;
        }
        public WindowsAppActivationDisposition Activate(Uri uri, string devBoxName)
        {
            Names.Add(devBoxName);
            if (Failure is { } failure) throw new WindowsAppConnectionException(failure);
            if (ReuseAtActivation) return WindowsAppActivationDisposition.ExistingWindowActivated;
            events.Enqueue("launch");
            Launched.Add(uri.OriginalString);
            return WindowsAppActivationDisposition.ConnectionUriActivated;
        }
    }
}
