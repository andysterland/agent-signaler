using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentSignaler.Updates;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class ReleaseUpdateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("v0.0.0", "0.0.0")]
    [InlineData("v255.255.65535", "255.255.65535")]
    [InlineData("v1.10.2", "1.10.2")]
    public void CanonicalMsiVersionsRoundTrip(string tag, string version) =>
        Assert.Equal(version, MsiReleaseVersion.Parse(tag, true).ToString());

    [Theory]
    [InlineData("v256.0.0")]
    [InlineData("v0.256.0")]
    [InlineData("v0.0.65536")]
    [InlineData("v01.2.3")]
    [InlineData("v1.2.3-preview")]
    [InlineData("1.2.3")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3\n")]
    [InlineData("v999999999999999999.0.0")]
    public void RejectsNoncanonicalAndOutOfBoundsTags(string text) =>
        Assert.Throws<InvalidDataException>(() => MsiReleaseVersion.Parse(text, true));

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void ManagedPolicyMatchesSharedPowerShellCases()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "UpdatePolicyCases.json")));
        var fixture = document.RootElement;
        Assert.Equal(1, fixture.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(UpdateHandoffPolicy.ClientUpdateExitCode, fixture.GetProperty("clientUpdateExitCode").GetInt32());
        Assert.Equal(UpdatePolicy.Repository, fixture.GetProperty("repository").GetString());
        Assert.Equal(WindowsUpdateScriptTrust.ReleasePublisher, fixture.GetProperty("authorizedPublisher").GetString());
        foreach (var package in Enum.GetValues<UpdatePackage>())
            Assert.Equal(UpdatePolicy.UpgradeCode(package), Guid.Parse(fixture.GetProperty("upgradeCodes").GetProperty(package.ToString()).GetString()!));
        var dependencies = fixture.GetProperty("dependencySet");
        Assert.Equal(UpdateHandoffPolicy.SchemaVersion, dependencies.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(UpdateHandoffPolicy.DependencyPolicyVersion, dependencies.GetProperty("policyVersion").GetInt32());
        Assert.Equal(UpdateHandoffLauncher.RequiredScripts.ToArray(), dependencies.GetProperty("files").EnumerateArray().Select(v => v.GetString()).ToArray());
        foreach (var version in fixture.GetProperty("validVersions").EnumerateArray())
            Assert.Equal(version.GetString(), MsiReleaseVersion.Parse(version.GetString()!).ToString());
        foreach (var version in fixture.GetProperty("invalidVersions").EnumerateArray())
            Assert.Throws<InvalidDataException>(() => MsiReleaseVersion.Parse(version.GetString()!));
        foreach (var comparison in fixture.GetProperty("versionComparisons").EnumerateArray())
        {
            var left = MsiReleaseVersion.Parse(comparison.GetProperty("left").GetString()!);
            var right = MsiReleaseVersion.Parse(comparison.GetProperty("right").GetString()!);
            Assert.Equal(comparison.GetProperty("expected").GetInt32(), Math.Sign(left.CompareTo(right)));
        }
        static ReleaseCandidate Candidate(JsonElement assets) => new(1, "v1.2.3", new(1, 2, 3), Now, false,
            assets.EnumerateArray().Select(a => new ReleaseAsset(a.GetProperty("id").GetInt64(),
                a.GetProperty("name").GetString()!, a.GetProperty("size").GetInt64())).ToImmutableArray());
        UpdatePolicy.ValidateCandidate(Candidate(fixture.GetProperty("validAssets")));
        UpdatePolicy.ValidateCandidate(Candidate(fixture.GetProperty("validBoundaryAssets")));
        foreach (var assetSet in fixture.GetProperty("invalidAssetSets").EnumerateArray())
            Assert.Throws<InvalidDataException>(() => UpdatePolicy.ValidateCandidate(Candidate(assetSet.GetProperty("assets"))));
        foreach (var signature in fixture.GetProperty("signatureCases").EnumerateArray())
        {
            var evidence = new UpdateSignatureEvidence(signature.GetProperty("status").GetString() == "Valid",
                signature.GetProperty("hasTimestamp").GetBoolean(), signature.GetProperty("publisher").GetString()!,
                signature.GetProperty("selfIssued").GetBoolean(), signature.GetProperty("codeSigningEku").GetBoolean()
                    ? ImmutableArray.Create(UpdateHandoffPolicy.CodeSigningEku) : ImmutableArray<string>.Empty);
            if (signature.GetProperty("accepted").GetBoolean()) UpdateHandoffPolicy.RequireSignature(evidence);
            else Assert.Throws<UnauthorizedAccessException>(() => UpdateHandoffPolicy.RequireSignature(evidence));
        }
    }

    [Fact]
    public async Task SelectsNewestPublishedIncludingPrereleaseNotHighestNumericVersionOrDraft()
    {
        using var http = new HttpClient(new Handler((_, _) => Reply("[" +
            Release(1, "v2.0.0", Now.AddDays(-2)) + "," +
            Release(2, "v1.10.0", Now, prerelease: true) + "," +
            Release(3, "v3.0.0", Now.AddDays(1), draft: true) + "]")));
        var result = await new ReleaseDiscovery(http).DiscoverAsync(null, default);
        Assert.Equal(2, result.Candidate.Id);
        Assert.Equal(new MsiReleaseVersion(1, 10, 0), result.Candidate.Version);
        Assert.True(result.Candidate.Prerelease);
        Assert.Equal("https://github.com/andysterland/agent-signaler/releases/tag/v1.10.0", result.Candidate.DetailsUri.AbsoluteUri);
        Assert.Equal("AgentSignaler.Remote.msi", result.Candidate.PackageAsset(UpdatePackage.Remote).Name);
    }

    [Fact]
    public async Task FollowsOnlyBoundedFixedRepositoryPagesAndDoesNotSendCredentials()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            Assert.Null(request.Headers.Referrer);
            var reply = Reply("[" + Release(requests.Count, "v1.0.1", Now.AddDays(requests.Count)) + "]");
            if (requests.Count < 5) Next(reply, requests.Count + 1);
            return reply;
        }));
        var result = await new ReleaseDiscovery(http).DiscoverAsync(null, default);
        Assert.Equal(5, result.Candidate.Id);
        Assert.Equal(5, requests.Count);
        Assert.Equal("https://api.github.com/repos/andysterland/agent-signaler/releases?per_page=20&page=5", requests[^1]);
        Assert.False(result.SinglePage);
        Assert.Null(result.ETag);
    }

    [Theory]
    [InlineData("https://evil.example/releases?per_page=20&page=2")]
    [InlineData("http://api.github.com/repos/andysterland/agent-signaler/releases?per_page=20&page=2")]
    [InlineData("https://api.github.com/repos/other/repo/releases?per_page=20&page=2")]
    [InlineData("https://api.github.com/repos/andysterland/agent-signaler/releases?per_page=100&page=2")]
    public async Task RejectsForeignOrChangedPagination(string target)
    {
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var reply = Reply("[]");
            reply.Headers.TryAddWithoutValidation("Link", $"<{target}>; rel=\"next\"");
            return reply;
        }));
        var error = await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleaseDiscovery(http).DiscoverAsync(null, default));
        Assert.Equal("update-metadata-invalid", error.Code);
    }

    [Fact]
    public async Task PageExhaustionIsIncompleteNeverAnArbitraryPartialWinner()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            count++;
            var reply = Reply("[" + Release(count, "v1.0.1", Now.AddDays(-count)) + "]");
            Next(reply, count + 1);
            return reply;
        }));
        var error = await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleaseDiscovery(http).DiscoverAsync(null, default));
        Assert.Equal("update-check-incomplete", error.Code);
        Assert.Equal(5, count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneHundredRecordsCannotSilentlyProveCompleteness(bool includeNextHeader)
    {
        var page = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            page++;
            var response = Reply("[" + string.Join(',', Enumerable.Range((page - 1) * 20 + 1, 20)
                .Select(id => Release(id, "v1.0.1", Now.AddMinutes(-id)))) + "]");
            if (includeNextHeader) Next(response, page + 1);
            return response;
        }));
        Assert.Equal("update-check-incomplete", (await Assert.ThrowsAsync<UpdateDiscoveryException>(
            () => new ReleaseDiscovery(http).DiscoverAsync(null, default))).Code);
        Assert.Equal(5, page);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("renamed")]
    [InlineData("extra")]
    [InlineData("asset-size")]
    [InlineData("id")]
    [InlineData("tag")]
    [InlineData("published")]
    [InlineData("type")]
    [InlineData("page-count")]
    [InlineData("depth")]
    public async Task RejectsMalformedMetadataAndExactAssetContractViolations(string kind)
    {
        var release = Release(1, "v1.0.1", Now);
        var page = "[" + release + "]";
        page = kind switch
        {
            "duplicate" => page.Replace("\"draft\":false", "\"draft\":false,\"draft\":false", StringComparison.Ordinal),
            "missing" => page.Replace("\"tag_name\"", "\"other\"", StringComparison.Ordinal),
            "renamed" => page.Replace("SHA256SUMS.txt", "other.txt", StringComparison.Ordinal),
            "extra" => page.Replace("\"assets\":[", "\"assets\":[{\"id\":99,\"name\":\"extra\",\"size\":1},", StringComparison.Ordinal),
            "asset-size" => page.Replace("\"size\":100", "\"size\":1073741825", StringComparison.Ordinal),
            "id" => page.Replace("\"id\":1,", "\"id\":0,", StringComparison.Ordinal),
            "tag" => page.Replace("v1.0.1", "v01.0.1", StringComparison.Ordinal),
            "published" => page.Replace("+00:00", "+01:00", StringComparison.Ordinal),
            "type" => page.Replace("\"draft\":false", "\"draft\":1", StringComparison.Ordinal),
            "page-count" => "[" + string.Join(',', Enumerable.Repeat(release, 21)) + "]",
            "depth" => "[" + new string('[', 16) + "0" + new string(']', 16) + "]",
            _ => throw new InvalidOperationException()
        };
        using var http = new HttpClient(new Handler((_, _) => Reply(page)));
        var error = await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleaseDiscovery(http).DiscoverAsync(null, default));
        Assert.Equal("update-metadata-invalid", error.Code);
    }

    [Fact]
    public async Task EqualNewestPublishedTimesAreIncompleteButOlderTiesAreNot()
    {
        var body = "[" + Release(1, "v1.0.1", Now) + "," + Release(2, "v1.0.2", Now) + "]";
        using var http = new HttpClient(new Handler((_, _) => Reply(body)));
        Assert.Equal("update-check-incomplete", (await Assert.ThrowsAsync<UpdateDiscoveryException>(
            () => new ReleaseDiscovery(http).DiscoverAsync(null, default))).Code);
        body = body[..^1] + "," + Release(3, "v1.0.3", Now.AddDays(1)) + "]";
        Assert.Equal(3, (await new ReleaseDiscovery(http).DiscoverAsync(null, default)).Candidate.Id);
    }

    [Fact]
    public async Task MetadataLimitAcceptsExactBoundaryAndRejectsOneMoreByte()
    {
        var body = ("[" + Release(1, "v1.0.1", Now) + "]").PadRight(UpdatePolicy.MetadataLimit);
        using var http = new HttpClient(new Handler((_, _) => Reply(body)));
        Assert.Equal(1, (await new ReleaseDiscovery(http).DiscoverAsync(null, default)).Candidate.Id);
        body += " ";
        Assert.Equal("update-metadata-invalid", (await Assert.ThrowsAsync<UpdateDiscoveryException>(
            () => new ReleaseDiscovery(http).DiscoverAsync(null, default))).Code);
    }

    [Fact]
    public async Task MetadataBudgetIsSharedAcrossPagesNotResetForEveryResponse()
    {
        var count = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            count++;
            var response = Reply(("[" + Release(count, "v1.0.1", Now.AddDays(-count)) + "]").PadRight(3 * 1024 * 1024));
            Next(response, count + 1);
            return response;
        }));
        Assert.Equal("update-metadata-invalid", (await Assert.ThrowsAsync<UpdateDiscoveryException>(
            () => new ReleaseDiscovery(http).DiscoverAsync(null, default))).Code);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task GitHubForbiddenHonorsTheLaterResetAndRetryAfterDeadline()
    {
        var clock = new Clock();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddHours(2));
            response.Headers.Add("X-RateLimit-Reset", Now.AddHours(3).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return response;
        }));
        var error = await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleaseDiscovery(http, clock).DiscoverAsync(null, default));
        Assert.Equal("update-rate-limited", error.Code);
        Assert.Equal(Now.AddHours(3), error.RetryAt);
    }

    [Fact]
    public async Task SinglePageConditional304UsesValidatedCandidate()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("\"fixture\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return new(HttpStatusCode.NotModified);
        }));
        var cache = Cache();
        var result = await new ReleaseDiscovery(http).DiscoverAsync(cache, default);
        Assert.Equal(cache.Candidate, result.Candidate);
        Assert.Equal("\"fixture\"", result.ETag);
        Assert.True(result.SinglePage);
    }

    [Fact]
    public async Task RedirectAndUnexpected304FailClosed()
    {
        foreach (var status in new[] { HttpStatusCode.Redirect, HttpStatusCode.NotModified })
        {
            var handler = new Handler((_, _) => new(status));
            using var http = new HttpClient(handler);
            await Assert.ThrowsAsync<UpdateDiscoveryException>(() => new ReleaseDiscovery(http).DiscoverAsync(null, default));
            Assert.Equal(1, handler.Calls);
        }
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToUpToDate()
    {
        using var source = new CancellationTokenSource();
        using var http = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            source.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("[]");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReleaseDiscovery(http).DiscoverAsync(null, source.Token));
    }

    [Fact]
    public async Task ManualChecksBypassFreshCacheButRespectServerBackoffAndPersistNoRawErrors()
    {
        var clock = new Clock();
        var store = new Store { Cache = Cache() };
        using var http = new HttpClient(new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Content = new StringContent("synthetic private proxy failure") };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromDays(8));
            return response;
        }));
        await using var monitor = new UpdateMonitor(new ReleaseDiscovery(http, clock), store,
            _ => Task.FromResult(new MsiReleaseVersion(1, 0, 0)), clock);
        await monitor.CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.Available, monitor.State.Status);
        Assert.True(monitor.State.FromCache);
        await monitor.CheckAsync(true);
        Assert.Equal(UpdateCheckStatus.CheckFailed, monitor.State.Status);
        Assert.Equal(Now.AddDays(8), monitor.State.NextCheckAt);
        await monitor.CheckAsync(true);
        Assert.True(monitor.State.FromCache);
        Assert.Equal("update-rate-limited", monitor.State.DiagnosticCode);
        var bytes = UpdateCheckCacheCodec.Write(store.Cache!);
        Assert.DoesNotContain("private proxy", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(Now.AddDays(8), UpdateCheckCacheCodec.Read(bytes).NextCheckAt);
    }

    [Theory]
    [InlineData("1.0.1", UpdateCheckStatus.UpToDate)]
    [InlineData("2.0.0", UpdateCheckStatus.UpToDate)]
    [InlineData("1.0.0", UpdateCheckStatus.Available)]
    public async Task EqualOrOlderCandidatesNeverOfferDowngrades(string installed, UpdateCheckStatus expected)
    {
        using var http = new HttpClient(new Handler((_, _) => Reply("[" + Release(1, "v1.0.1", Now) + "]")));
        await using var monitor = new UpdateMonitor(new ReleaseDiscovery(http), new Store(),
            _ => Task.FromResult(MsiReleaseVersion.Parse(installed)));
        await monitor.CheckAsync(true);
        Assert.Equal(expected, monitor.State.Status);
    }

    [Fact]
    public async Task SharedStoreCoalescesConcurrentProcessesAndBackgroundFailureBackoffCapsAtSixHours()
    {
        var clock = new Clock();
        var handler = new Handler((_, _) => Reply("[" + Release(1, "v1.0.1", Now) + "]"));
        using var http = new HttpClient(handler);
        var store = new Store();
        await using var first = new UpdateMonitor(new ReleaseDiscovery(http, clock), store, _ => Task.FromResult(new MsiReleaseVersion(1, 0, 0)), clock);
        await using var second = new UpdateMonitor(new ReleaseDiscovery(http, clock), store, _ => Task.FromResult(new MsiReleaseVersion(1, 0, 0)), clock);
        await Task.WhenAll(first.CheckAsync(false), second.CheckAsync(false));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(UpdateCheckStatus.Available, first.State.Status);
        Assert.Equal(UpdateCheckStatus.Available, second.State.Status);

        using var offline = new HttpClient(new Handler((_, _) => throw new HttpRequestException("synthetic proxy detail")));
        var failures = new Store();
        await using var monitor = new UpdateMonitor(new ReleaseDiscovery(offline, clock), failures,
            _ => Task.FromResult(new MsiReleaseVersion(1, 0, 0)), clock);
        foreach (var minutes in new[] { 15, 30, 60, 120, 240, 360, 360 })
        {
            await monitor.CheckAsync(false);
            Assert.Equal(UpdateCheckStatus.CheckFailed, monitor.State.Status);
            Assert.Equal(TimeSpan.FromMinutes(minutes), monitor.State.NextCheckAt - clock.Now);
            clock.Now = monitor.State.NextCheckAt!.Value;
        }
    }

    [Fact]
    public async Task MetadataDeadlineUsesInjectedTimeAndDoesNotWaitForRealNetwork()
    {
        var clock = new TimerClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new AsyncHandler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("[]");
        }));
        var check = new ReleaseDiscovery(http, clock).DiscoverAsync(null, default);
        await entered.Task;
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal("update-check-timeout", (await Assert.ThrowsAsync<UpdateDiscoveryException>(() => check)).Code);
    }

    [Fact]
    public async Task SchedulerStartsAfterThirtySecondsAndActualExitCancelsIt()
    {
        var clock = new TimerClock();
        var handler = new Handler((_, _) => Reply("[" + Release(1, "v1.0.1", Now) + "]"));
        using var http = new HttpClient(handler);
        var monitor = new UpdateMonitor(new ReleaseDiscovery(http, clock), new Store(),
            _ => Task.FromResult(new MsiReleaseVersion(1, 0, 0)), clock, () => 1);
        var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += result => { if (result.Status == UpdateCheckStatus.Available) available.TrySetResult(); };
        monitor.Start();
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        await available.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(TimeSpan.FromMinutes(396), await clock.PeriodicDelay.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await monitor.DisposeAsync();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void CacheRejectsUnknownDuplicateSchemaAndSuccessWithoutCandidate()
    {
        var encoded = Encoding.UTF8.GetString(UpdateCheckCacheCodec.Write(Cache()));
        foreach (var invalid in new[]
        {
            encoded.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal),
            encoded.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal),
            encoded.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"token\":\"synthetic\"", StringComparison.Ordinal),
            encoded.Replace("\"failureCount\":0", "\"failureCount\":33", StringComparison.Ordinal)
        }) Assert.Throws<InvalidDataException>(() => UpdateCheckCacheCodec.Read(Encoding.UTF8.GetBytes(invalid)));
        Assert.Throws<InvalidDataException>(() => UpdateCheckCacheCodec.Write(Cache() with { Candidate = null }));
        Assert.Throws<InvalidDataException>(() => UpdateCheckCacheCodec.Read(new byte[UpdatePolicy.CacheLimit + 1]));
    }

    [Fact]
    public void ExactAssetBoundsAndIdentityAreValidatedForCachedAndFreshCandidates()
    {
        var candidate = Cache().Candidate!;
        var atLimit = candidate with { Assets = [new(11, "AgentSignaler.Dashboard.msi", 1073741824),
            new(12, "AgentSignaler.Remote.msi", 1073741824), new(13, "SHA256SUMS.txt", 65536)] };
        UpdatePolicy.ValidateCandidate(atLimit);
        foreach (var assets in new ImmutableArray<ReleaseAsset>[]
        {
            [new(11, "AgentSignaler.Dashboard.msi", 1073741825), candidate.Assets[1], candidate.Assets[2]],
            [candidate.Assets[0], candidate.Assets[1], new(13, "SHA256SUMS.txt", 65537)],
            [candidate.Assets[0], candidate.Assets[1], new(13, "SHA256SUMS.txt", 0)],
            [candidate.Assets[0], candidate.Assets[1], new(11, "SHA256SUMS.txt", 1)],
            [candidate.Assets[0], candidate.Assets[1], new(13, "AgentSignaler.Remote.msi", 1)]
        }) Assert.Throws<InvalidDataException>(() => UpdatePolicy.ValidateCandidate(candidate with { Assets = assets }));
        Assert.Equal(new Guid("D67CE744-C442-473A-B751-CA70D3CBCA4D"), UpdatePolicy.UpgradeCode(UpdatePackage.Dashboard));
        Assert.Equal(new Guid("BFA03A34-37C9-4149-9789-9A68EC2A9C3A"), UpdatePolicy.UpgradeCode(UpdatePackage.Remote));
    }

    [Fact]
    public async Task IncompleteCheckCannotReuseCachedAvailabilityAsFreshEligibility()
    {
        var clock = new Clock();
        var page = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            page++;
            var response = Reply("[" + Release(page, "v1.0.2", Now.AddDays(-page)) + "]");
            Next(response, page + 1);
            return response;
        }));
        var store = new Store { Cache = Cache() };
        await using var monitor = new UpdateMonitor(new ReleaseDiscovery(http, clock), store,
            _ => Task.FromResult(new MsiReleaseVersion(1, 0, 0)), clock);
        await monitor.CheckAsync(true);
        Assert.Equal(UpdateCheckStatus.CheckIncomplete, monitor.State.Status);
        Assert.Equal("update-check-incomplete", monitor.State.DiagnosticCode);
        Assert.Equal("v1.0.1", monitor.State.Candidate!.Tag);
        Assert.Equal(5, page);
        await monitor.CheckAsync(false);
        Assert.Equal(UpdateCheckStatus.CheckIncomplete, monitor.State.Status);
        Assert.True(monitor.State.FromCache);
        Assert.Equal(5, page);
    }

    [Fact]
    public void ServicingStateMachineRequiresExplicitPreparationAndEveryOrderedBoundary()
    {
        var machine = new UpdateStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.MoveTo(UpdateStage.Installing));
        foreach (var stage in new[] { UpdateStage.Checking, UpdateStage.Available, UpdateStage.Preparing,
            UpdateStage.AwaitingParticipants, UpdateStage.HandingOff, UpdateStage.WaitingForExit, UpdateStage.Downloading,
            UpdateStage.Validating, UpdateStage.Installing, UpdateStage.Verifying, UpdateStage.Restarting, UpdateStage.Completed })
            machine.MoveTo(stage);
        Assert.Equal(UpdateStage.Completed, machine.Stage);
        Assert.Throws<InvalidOperationException>(() => machine.MoveTo(UpdateStage.Installing));
    }

    private static UpdateCheckCache Cache() => new(1, new(1, "v1.0.1", new(1, 0, 1), Now, false,
        [new(11, "AgentSignaler.Dashboard.msi", 100), new(12, "AgentSignaler.Remote.msi", 100), new(13, "SHA256SUMS.txt", 100)]),
        "\"fixture\"", true, Now.AddMinutes(-1), Now.AddHours(6), 0);

    private static string Release(long id, string tag, DateTimeOffset published, bool prerelease = false, bool draft = false) => JsonSerializer.Serialize(new
    {
        id, tag_name = tag, draft, prerelease, published_at = published,
        assets = new[] { new { id = id * 10 + 1, name = "AgentSignaler.Dashboard.msi", size = 100 },
            new { id = id * 10 + 2, name = "AgentSignaler.Remote.msi", size = 100 },
            new { id = id * 10 + 3, name = "SHA256SUMS.txt", size = 100 } }
    });
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static void Next(HttpResponseMessage reply, int page) => reply.Headers.TryAddWithoutValidation("Link",
        $"<https://api.github.com/repos/andysterland/agent-signaler/releases?per_page=20&page={page}>; rel=\"next\"");

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(reply(request, token)); }
    }
    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request, token);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = ReleaseUpdateTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TimerClock : TimeProvider
    {
        private readonly List<ControlledTimer> timers = [];
        private DateTimeOffset now = Now;
        public TaskCompletionSource<TimeSpan> PeriodicDelay { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(this, callback, state);
            lock (timers) timers.Add(timer);
            timer.Change(dueTime, period);
            if (dueTime >= TimeSpan.FromHours(1)) PeriodicDelay.TrySetResult(dueTime);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            now += amount;
            ControlledTimer[] snapshot;
            lock (timers) snapshot = timers.ToArray();
            foreach (var timer in snapshot) timer.Fire(now);
        }
        private sealed class ControlledTimer(TimerClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? due;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period);
                due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.now + dueTime;
                return true;
            }
            public void Fire(DateTimeOffset current)
            {
                if (due is not { } value || value > current) return;
                due = null;
                callback(state);
            }
            public void Dispose() => due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed class Store : IUpdateCheckStore
    {
        public UpdateCheckCache? Cache { get; set; }
        private readonly SemaphoreSlim lease = new(1, 1);
        public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken token)
        { await lease.WaitAsync(token); return new Releaser(lease); }
        public Task<UpdateCheckCache?> ReadAsync(CancellationToken token) => Task.FromResult(Cache);
        public Task WriteAsync(UpdateCheckCache cache, CancellationToken token) { Cache = cache; return Task.CompletedTask; }
        private sealed class Releaser(SemaphoreSlim lease) : IAsyncDisposable
        { public ValueTask DisposeAsync() { lease.Release(); return ValueTask.CompletedTask; } }
    }
}