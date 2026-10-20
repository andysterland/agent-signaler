using System.Security.Cryptography;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using AgentSignaler.Dashboard;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Dashboard.Core.Tests;

public sealed class PrivateDiscoveryTests
{
    private static readonly PrivateDiscoveryTarget Target = new(Guid.NewGuid(), Guid.NewGuid(), "test-group", "testaccount");
    private static readonly Uri Endpoint = new("https://sample-51820.euw.devtunnels.ms/");

    [Fact]
    public async Task PreviewIsReadOnlyAndRequiresExactUnexpiredConfirmation()
    {
        var fixture = new Fixture();
        var preview = await fixture.Controller.PreviewAsync(Target, default);
        Assert.Equal(0, fixture.Azure.Mutations);
        Assert.Null(fixture.Journal.Value);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.ProvisionAsync(Guid.NewGuid(), default));
        Assert.Equal(0, fixture.Azure.Mutations);
        fixture.Clock.Now = preview.ExpiresAtUtc;
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.ProvisionAsync(preview.ConfirmationId, default));
        Assert.Null(fixture.Journal.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingAtomicPolicyCapabilityBlocksBeforeCloudOrJournalCreation(bool capabilityLostAfterPreview)
    {
        var fixture = new Fixture();
        PrivateDiscoveryPreview? preview = null;
        if (capabilityLostAfterPreview) preview = await fixture.Controller.PreviewAsync(Target, default);
        fixture.Azure.AtomicPolicyUpdatesSupported = false;
        var error = await Assert.ThrowsAsync<DiscoveryException>(async () =>
        {
            if (preview is null) await fixture.Controller.PreviewAsync(Target, default);
            else await fixture.Controller.ProvisionAsync(preview.ConfirmationId, default);
        });
        Assert.Equal("discovery_acl_atomic_concurrency_unsupported", error.Code);
        Assert.Equal(0, fixture.Azure.Mutations);
        Assert.Null(fixture.Azure.Remote);
        Assert.Null(fixture.Journal.Value);
        Assert.Contains("atomic concurrency", fixture.Controller.Status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProvisionCreatesPrivateOwnedSignedUnavailableDocumentAndPolicyOnlySas()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var registration = fixture.Journal.Value!;
        Assert.Equal(PrivateDiscoveryPhase.Ready, registration.Phase);
        Assert.Equal("agentsignaler-" + registration.DiscoveryId.ToString("N"), registration.Container);
        Assert.True(fixture.Azure.Remote!.IsPrivate);
        var policy = Assert.Single(fixture.Azure.Remote.Policies);
        Assert.Equal("r", policy.Permissions);
        Assert.Equal(fixture.Clock.Now.AddDays(90), policy.ExpiresAtUtc);
        Assert.Equal(fixture.Clock.Now.AddMinutes(-5), policy.StartsAtUtc);
        var package = DiscoveryCodec.ParsePackage(fixture.Controller.CopyPackage());
        var verified = DiscoveryCodec.Verify(fixture.Azure.Remote.Document!, package, fixture.Clock.Now);
        Assert.Equal(DiscoveryState.Unavailable, verified.Document.State);
        Assert.Null(verified.Document.DashboardBaseUrl);
        Assert.Equal(registration.DiscoveryId, package.DiscoveryId);
        Assert.Equal(1, fixture.Azure.SasVerifications);
        Assert.DoesNotContain("sig=", fixture.Controller.Status.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(registration.SigningKey, fixture.Controller.Status.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublisherUsesVerifiedGenerationAndMonotonicallyIncreasesRevision()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var firstRevision = fixture.Journal.Value!.Revision;
        var availableGeneration = fixture.Controller.ObserveTunnel(Endpoint);
        var unavailableGeneration = fixture.Controller.ObserveTunnel(null);
        var mutations = fixture.Azure.Mutations;
        await fixture.Controller.PublishAsync(availableGeneration, default);
        Assert.Equal(mutations, fixture.Azure.Mutations);
        await fixture.Controller.PublishAsync(unavailableGeneration, default);
        var package = DiscoveryCodec.ParsePackage(fixture.Controller.CopyPackage());
        var unavailable = DiscoveryCodec.Verify(fixture.Azure.Remote!.Document!, package, fixture.Clock.Now).Document;
        Assert.Equal(DiscoveryState.Unavailable, unavailable.State);
        Assert.Equal(firstRevision, unavailable.Revision);
        await fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), default);
        var available = DiscoveryCodec.Verify(fixture.Azure.Remote.Document!, package, fixture.Clock.Now).Document;
        Assert.Equal(Endpoint, available.DashboardBaseUrl);
        Assert.Equal(DiscoveryState.Available, available.State);
        Assert.True(available.Revision > unavailable.Revision);
        Assert.Equal(TimeSpan.FromHours(24), available.ExpiresAtUtc - available.PublishedAtUtc);
    }

    [Fact]
    public async Task PolicyRenewsBelowThirtyDaysAndPreservesPackage()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var package = fixture.Controller.CopyPackage();
        var writes = fixture.Azure.PolicyWrites;
        fixture.Clock.Now = fixture.Clock.Now.AddDays(60).AddTicks(-1);
        await fixture.Controller.RenewAsync(default);
        Assert.Equal(writes, fixture.Azure.PolicyWrites);
        fixture.Clock.Now = fixture.Clock.Now.AddTicks(1);
        await fixture.Controller.RenewAsync(default);
        Assert.Equal(writes, fixture.Azure.PolicyWrites);
        fixture.Clock.Now = fixture.Clock.Now.AddTicks(1);
        await fixture.Controller.RenewAsync(default);
        Assert.Equal(writes + 1, fixture.Azure.PolicyWrites);
        var expectedExpiry = fixture.Clock.Now.AddTicks(-(fixture.Clock.Now.Ticks % TimeSpan.TicksPerSecond)).AddDays(90);
        Assert.Equal(expectedExpiry, Assert.Single(fixture.Azure.Remote!.Policies).ExpiresAtUtc);
        Assert.Equal(package, fixture.Controller.CopyPackage());
    }

    [Fact]
    public async Task RotationPermanentlyRetiresOldIdentifierAndRevocationCannotRenew()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var oldPolicy = fixture.Journal.Value!.PolicyId;
        var oldPackage = fixture.Controller.CopyPackage();
        await fixture.Controller.RotateAsync(default);
        Assert.DoesNotContain(oldPolicy, fixture.Journal.Value!.RetiredPolicyIds!);
        Assert.Equal(oldPolicy, fixture.Journal.Value.PreviousPolicyId);
        Assert.NotEqual(oldPolicy, fixture.Journal.Value.PolicyId);
        Assert.NotEqual(oldPackage, fixture.Controller.CopyPackage());
        Assert.Contains(fixture.Azure.Remote!.Policies, p => p.Id == oldPolicy);
        await fixture.Controller.RetirePreviousAsync(default);
        Assert.Contains(oldPolicy, fixture.Journal.Value.RetiredPolicyIds!);
        Assert.Null(fixture.Journal.Value.PreviousPolicyId);
        Assert.DoesNotContain(fixture.Azure.Remote.Policies, p => p.Id == oldPolicy);
        var rotated = fixture.Journal.Value.PolicyId;
        await fixture.Controller.RevokeAsync(false, default);
        Assert.Empty(fixture.Azure.Remote.Policies);
        Assert.Contains(rotated, fixture.Journal.Value!.RetiredPolicyIds!);
        Assert.True(fixture.Controller.Status.Revoked);
        Assert.Throws<DiscoveryException>(() => fixture.Controller.CopyPackage());
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.RenewAsync(default));
        await fixture.Controller.RecoverAsync(default);
        Assert.Empty(fixture.Azure.Remote.Policies);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("public")]
    [InlineData("foreign-blob")]
    [InlineData("foreign-policy")]
    [InlineData("blob-metadata")]
    [InlineData("signature")]
    public async Task ForeignOrEditedResourcesAreNeverOverwritten(string kind)
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var remote = fixture.Azure.Remote!;
        fixture.Azure.Remote = kind switch
        {
            "metadata" => remote with { Metadata = new Dictionary<string, string>() },
            "public" => remote with { IsPrivate = false },
            "foreign-blob" => remote with { Blobs = [PrivateDiscoveryAzure.BlobName, "user-data"] },
            "foreign-policy" => remote with { Policies = [new("foreign", fixture.Clock.Now, fixture.Clock.Now.AddDays(1), "r")] },
            "blob-metadata" => remote with { BlobMetadata = new Dictionary<string, string>() },
            _ => remote with { Document = "{}"u8.ToArray() }
        };
        var before = fixture.Azure.Mutations;
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.RenewAsync(default));
        Assert.Equal(before, fixture.Azure.Mutations);
    }

    [Fact]
    public async Task InterruptedCreationRecoversOnlyExactlyOwnedEmptyContainer()
    {
        var fixture = new Fixture();
        fixture.Azure.FailInitialBlob = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.ProvisionAsync());
        Assert.Equal(PrivateDiscoveryPhase.Prepared, fixture.Journal.Value!.Phase);
        Assert.Empty(fixture.Azure.Remote!.Blobs);
        fixture.Azure.FailInitialBlob = false;
        var restarted = fixture.NewController();
        await restarted.InitializeAsync(default);
        await restarted.RecoverAsync(default);
        Assert.Equal(PrivateDiscoveryPhase.Ready, fixture.Journal.Value.Phase);
        Assert.Single(fixture.Azure.Remote.Blobs);
        Assert.Single(fixture.Azure.Remote.Policies);
    }

    [Fact]
    public async Task FailedSasVerificationRemainsRecoverableAndDoesNotExposePackage()
    {
        var fixture = new Fixture();
        fixture.Azure.FailSasVerification = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.ProvisionAsync());
        Assert.Equal(PrivateDiscoveryPhase.PolicyPending, fixture.Journal.Value!.Phase);
        Assert.Null(fixture.Journal.Value.Package);
        Assert.True(fixture.Controller.Status.Recoverable);
        Assert.Throws<DiscoveryException>(() => fixture.Controller.CopyPackage());
        fixture.Azure.FailSasVerification = false;
        await fixture.Controller.RecoverAsync(default);
        Assert.Equal(PrivateDiscoveryPhase.Ready, fixture.Journal.Value.Phase);
    }

    [Fact]
    public async Task FailedRotationRecoveryNeverReusesRetiredPolicy()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var original = fixture.Journal.Value!.PolicyId;
        fixture.Azure.FailSasVerification = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.RotateAsync(default));
        var replacement = fixture.Journal.Value!.PendingPolicyId;
        Assert.DoesNotContain(original, fixture.Journal.Value.RetiredPolicyIds!);
        Assert.NotNull(replacement);
        fixture.Azure.FailSasVerification = false;
        var restarted = fixture.NewController();
        await restarted.InitializeAsync(default);
        await restarted.RecoverAsync(default);
        Assert.Equal(replacement, fixture.Journal.Value.PolicyId);
        Assert.Contains(fixture.Azure.Remote!.Policies, p => p.Id == original);
        await restarted.RetirePreviousAsync(default);
        Assert.DoesNotContain(fixture.Azure.Remote.Policies, p => p.Id == original);
    }

    [Fact]
    public async Task CancellationBeforeMutationDoesNotCreateResources()
    {
        var fixture = new Fixture();
        var preview = await fixture.Controller.PreviewAsync(Target, default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Controller.ProvisionAsync(preview.ConfirmationId, cancellation.Token));
        Assert.Equal(0, fixture.Azure.Mutations);
        Assert.Null(fixture.Journal.Value);
    }

    [Fact]
    public async Task RemovalDeletesOnlyOwnedBlobAndKeepsRetirementTombstone()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var policy = fixture.Journal.Value!.PolicyId;
        await fixture.Controller.RevokeAsync(true, default);
        Assert.Empty(fixture.Azure.Remote!.Policies);
        Assert.Empty(fixture.Azure.Remote.Blobs);
        Assert.Equal(PrivateDiscoveryPhase.Removed, fixture.Journal.Value!.Phase);
        Assert.Contains(policy, fixture.Journal.Value.RetiredPolicyIds!);
        Assert.False(fixture.Controller.Status.Configured);
        Assert.Throws<DiscoveryException>(() => fixture.Controller.CopyPackage());
    }

    [Fact]
    public async Task DisablePublishingDoesNotRevokePolicyAndEnablePublishesCurrentVerifiedEndpoint()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var package = fixture.Controller.CopyPackage();
        var policy = Assert.Single(fixture.Azure.Remote!.Policies);
        await fixture.Controller.SetPublishingAsync(false, default);
        var before = fixture.Azure.Mutations;
        await fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), default);
        Assert.Equal(before, fixture.Azure.Mutations);
        Assert.Equal(policy, Assert.Single(fixture.Azure.Remote.Policies));
        Assert.Equal(package, fixture.Controller.CopyPackage());
        await fixture.Controller.SetPublishingAsync(true, default);
        await fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), default);
        Assert.Equal(Endpoint, DiscoveryCodec.Verify(fixture.Azure.Remote.Document!, DiscoveryCodec.ParsePackage(package), fixture.Clock.Now).Document.DashboardBaseUrl);
    }

    [Fact]
    public async Task UncertainPublicationCommitReconcilesExactIntentWithoutReuploadOrNewRevision()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        fixture.Azure.FailAfterBlobWrite = true;
        var generation = fixture.Controller.ObserveTunnel(Endpoint);
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.PublishAsync(generation, default));
        var revision = fixture.Journal.Value!.Revision;
        var intent = fixture.Journal.Value.PendingDocument;
        Assert.NotNull(intent);
        Assert.Equal(intent, fixture.Azure.Remote!.Document);
        var mutations = fixture.Azure.Mutations;
        await fixture.Controller.PublishAsync(generation, default);
        Assert.Equal(mutations, fixture.Azure.Mutations);
        Assert.Equal(revision, fixture.Journal.Value.Revision);
        Assert.Null(fixture.Journal.Value.PendingDocument);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartReconcilesCommittedIntentOrSupersedesItWithUnavailableAfterStop(bool stopped)
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        fixture.Azure.FailAfterBlobWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), default));
        var intendedRevision = fixture.Journal.Value!.Revision;
        var intendedBytes = fixture.Journal.Value.PendingDocument;
        Assert.NotNull(intendedBytes);
        Assert.NotNull(fixture.Journal.Value.PendingBlobETag);
        Assert.Equal(intendedBytes, fixture.Azure.Remote!.Document);
        var mutations = fixture.Azure.Mutations;
        var restarted = fixture.NewController();
        await restarted.InitializeAsync(default);
        await restarted.PublishAsync(restarted.ObserveTunnel(stopped ? null : Endpoint), default);
        var actual = DiscoveryCodec.Verify(fixture.Azure.Remote.Document!, DiscoveryCodec.ParsePackage(restarted.CopyPackage()), fixture.Clock.Now).Document;
        Assert.Null(fixture.Journal.Value.PendingDocument);
        Assert.Null(fixture.Journal.Value.PendingBlobETag);
        if (stopped)
        {
            Assert.Equal(DiscoveryState.Unavailable, actual.State);
            Assert.Null(actual.DashboardBaseUrl);
            Assert.True(actual.Revision > intendedRevision);
            Assert.Equal(mutations + 1, fixture.Azure.Mutations);
        }
        else
        {
            Assert.Equal(DiscoveryState.Available, actual.State);
            Assert.Equal(intendedRevision, actual.Revision);
            Assert.Equal(intendedBytes, fixture.Azure.Remote.Document);
            Assert.Equal(mutations, fixture.Azure.Mutations);
        }
    }

    [Fact]
    public async Task RestartRefusesChangedSignedContentAtTheIntendedRevision()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        fixture.Azure.FailAfterBlobWrite = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), default));
        var current = fixture.Journal.Value!;
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(current.SigningKey), out _);
        fixture.Azure.Remote = fixture.Azure.Remote! with
        {
            Document = DiscoveryCodec.Sign(new(DiscoveryProtocol.Version, current.DiscoveryId, current.Revision,
                fixture.Clock.Now, fixture.Clock.Now.AddHours(24), DiscoveryState.Available,
                new("https://different-4318.usw2.devtunnels.ms/")), key)
        };
        var mutations = fixture.Azure.Mutations;
        var restarted = fixture.NewController();
        await restarted.InitializeAsync(default);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => restarted.PublishAsync(restarted.ObserveTunnel(Endpoint), default));
        Assert.Equal("discovery_same_revision_conflict", error.Code);
        Assert.Equal(mutations, fixture.Azure.Mutations);
        Assert.Equal(current.Revision, fixture.Journal.Value!.Revision);
    }

    [Fact]
    public async Task RestartRefusesToBypassThePersistedExpectedEtagAfterAnUncommittedUpload()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        fixture.Azure.BlockNextBlobWrite = true;
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), cancellation.Token);
        await fixture.Azure.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        fixture.Azure.Remote = fixture.Azure.Remote! with { BlobETag = "externally-changed-etag" };
        var mutations = fixture.Azure.Mutations;
        var intendedRevision = fixture.Journal.Value!.Revision;
        var restarted = fixture.NewController();
        await restarted.InitializeAsync(default);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => restarted.PublishAsync(restarted.ObserveTunnel(Endpoint), default));
        Assert.Equal("discovery_publication_conflict", error.Code);
        Assert.Equal(mutations, fixture.Azure.Mutations);
        Assert.Equal(intendedRevision, fixture.Journal.Value.Revision);
    }

    [Fact]
    public async Task RotationPreservesPreviousPolicyDuringRenewalAndBlocksThirdReaderGroup()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var previous = Assert.Single(fixture.Azure.Remote!.Policies);
        await fixture.Controller.RotateAsync(default);
        Assert.Equal(2, fixture.Azure.Remote.Policies.Count);
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Controller.RotateAsync(default));
        fixture.Clock.Now = fixture.Clock.Now.AddDays(61);
        await fixture.Controller.RenewAsync(default);
        Assert.Contains(previous, fixture.Azure.Remote.Policies);
        Assert.Equal(2, fixture.Azure.Remote.Policies.Count);
        await fixture.Controller.RetirePreviousAsync(default);
        Assert.Single(fixture.Azure.Remote.Policies);
        Assert.Contains(previous.Id, fixture.Journal.Value!.RetiredPolicyIds!);
    }

    [Fact]
    public async Task StopGenerationCancelsDelayedAvailableWriteBeforeUnavailablePublication()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        fixture.Azure.BlockNextBlobWrite = true;
        var available = fixture.Controller.PublishAsync(fixture.Controller.ObserveTunnel(Endpoint), default);
        await fixture.Azure.WriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var unavailableGeneration = fixture.Controller.ObserveTunnel(null);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => available);
        fixture.Azure.AllowWrite.TrySetResult();
        var package = DiscoveryCodec.ParsePackage(fixture.Controller.CopyPackage());
        Assert.Equal(DiscoveryState.Unavailable, DiscoveryCodec.Verify(fixture.Azure.Remote!.Document!, package, fixture.Clock.Now).Document.State);
        var canceledRevision = fixture.Journal.Value!.Revision;
        await fixture.Controller.PublishAsync(unavailableGeneration, default);
        var final = DiscoveryCodec.Verify(fixture.Azure.Remote.Document!, package, fixture.Clock.Now).Document;
        Assert.Equal(DiscoveryState.Unavailable, final.State);
        Assert.Null(final.DashboardBaseUrl);
        Assert.True(final.Revision > canceledRevision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeRefreshesAtSixHoursOnlyWhileSharingIsVerified(bool verified)
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var clock = new DashboardRuntimeTests.ManualClock();
        clock.Advance(fixture.Clock.Now - clock.GetUtcNow());
        await using var runtimeFixture = new DashboardRuntimeTests.Fixture(new()
        {
            TimeProvider = clock, DiscoveryJournal = fixture.Journal, DiscoveryAzure = _ => fixture.Azure,
            TunnelRunner = new DashboardRuntimeTests.FakeTunnelRunner(), TunnelHealthProbe = new DashboardRuntimeTests.FakeProbe()
        }, System.Text.Json.JsonSerializer.Serialize(new DashboardSettings { AutoStartSharing = verified }));
        var runtime = runtimeFixture.Runtime;
        await runtime.InitializeAsync();
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        Assert.Equal(verified, runtime.Sharing.State.CanCopy);
        var revision = fixture.Journal.Value!.Revision;
        var writes = fixture.Azure.Mutations;
        clock.Advance(TimeSpan.FromHours(5));
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.LastMaintenanceCheckAtUtc == clock.GetUtcNow());
        Assert.Equal(revision, fixture.Journal.Value.Revision);
        Assert.Equal(writes, fixture.Azure.Mutations);
        clock.Advance(TimeSpan.FromHours(1));
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.LastMaintenanceCheckAtUtc == clock.GetUtcNow());
        if (verified)
        {
            await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
            Assert.True(fixture.Journal.Value.Revision > revision);
        }
        else Assert.Equal(revision, fixture.Journal.Value.Revision);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SixHourRefreshNeverPublishesAvailableWhenFreshPublicHealthFails(int successfulRechecks)
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var package = DiscoveryCodec.ParsePackage(fixture.Controller.CopyPackage());
        var clock = new DashboardRuntimeTests.ManualClock();
        clock.Advance(fixture.Clock.Now - clock.GetUtcNow());
        var probe = new DiscoveryProbe();
        await using var runtimeFixture = new DashboardRuntimeTests.Fixture(new()
        {
            TimeProvider = clock, DiscoveryJournal = fixture.Journal, DiscoveryAzure = _ => fixture.Azure,
            TunnelRunner = new DashboardRuntimeTests.FakeTunnelRunner(), TunnelHealthProbe = probe
        }, System.Text.Json.JsonSerializer.Serialize(new DashboardSettings { AutoStartSharing = true }));
        var runtime = runtimeFixture.Runtime;
        await runtime.InitializeAsync();
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        Assert.Equal(DiscoveryState.Available, DiscoveryCodec.Verify(fixture.Azure.Remote!.Document!, package, clock.GetUtcNow()).Document.State);
        var committed = fixture.Azure.PublishedDocuments.Count;
        probe.SuccessfulPublicRechecksRemaining = successfulRechecks;
        clock.Advance(TimeSpan.FromHours(6));
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        Assert.False(runtime.Sharing.State.CanCopy);
        var documents = fixture.Azure.PublishedDocuments.Skip(committed).ToArray();
        Assert.NotEmpty(documents);
        Assert.All(documents, bytes => Assert.Equal(DiscoveryState.Unavailable,
            DiscoveryCodec.Verify(bytes, package, clock.GetUtcNow()).Document.State));
    }

    [Fact]
    public async Task StopCancelsInFlightFreshHealthRecheckAndCannotCommitAvailable()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var package = DiscoveryCodec.ParsePackage(fixture.Controller.CopyPackage());
        var clock = new DashboardRuntimeTests.ManualClock();
        clock.Advance(fixture.Clock.Now - clock.GetUtcNow());
        var probe = new DiscoveryProbe();
        var runner = new DashboardRuntimeTests.FakeTunnelRunner();
        await using var runtimeFixture = new DashboardRuntimeTests.Fixture(new()
        {
            TimeProvider = clock, DiscoveryJournal = fixture.Journal, DiscoveryAzure = _ => fixture.Azure,
            TunnelRunner = runner, TunnelHealthProbe = probe
        }, System.Text.Json.JsonSerializer.Serialize(new DashboardSettings { AutoStartSharing = true }));
        var runtime = runtimeFixture.Runtime;
        await runtime.InitializeAsync();
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        var committed = fixture.Azure.PublishedDocuments.Count;
        probe.BlockPublicHealth = true;
        clock.Advance(TimeSpan.FromHours(6));
        await probe.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await runtime.SharingAsync(RuntimeSharingOperation.Stop)).Succeeded);
        Assert.True(runner.Host!.Disposed);
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        var documents = fixture.Azure.PublishedDocuments.Skip(committed).ToArray();
        Assert.NotEmpty(documents);
        Assert.All(documents, bytes => Assert.Equal(DiscoveryState.Unavailable,
            DiscoveryCodec.Verify(bytes, package, clock.GetUtcNow()).Document.State));
    }

    [Fact]
    public async Task RuntimeChecksRenewalAtStartupAndDailyWithoutRenewingAtExactlyThirtyDays()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var originalExpiry = fixture.Journal.Value!.PolicyExpiry;
        var clock = new DashboardRuntimeTests.ManualClock();
        clock.Advance(fixture.Clock.Now.AddDays(59) - clock.GetUtcNow());
        var policyWrites = fixture.Azure.PolicyWrites;
        var readerChecks = fixture.Azure.SasVerifications;
        await using var runtimeFixture = new DashboardRuntimeTests.Fixture(new()
        {
            TimeProvider = clock, DiscoveryJournal = fixture.Journal, DiscoveryAzure = _ => fixture.Azure,
            TunnelRunner = new DashboardRuntimeTests.FakeTunnelRunner(), TunnelHealthProbe = new DashboardRuntimeTests.FakeProbe()
        }, System.Text.Json.JsonSerializer.Serialize(new DashboardSettings { AutoStartSharing = true }));
        var runtime = runtimeFixture.Runtime;
        await runtime.InitializeAsync();
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        Assert.True(fixture.Azure.SasVerifications > readerChecks);
        Assert.Equal(policyWrites, fixture.Azure.PolicyWrites);
        clock.Advance(TimeSpan.FromDays(1));
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.LastMaintenanceCheckAtUtc == clock.GetUtcNow());
        Assert.Equal(originalExpiry, fixture.Journal.Value.PolicyExpiry);
        Assert.Equal(policyWrites, fixture.Azure.PolicyWrites);
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.PublishedAtUtc == clock.GetUtcNow() && !runtime.Discovery.State.PublicationPending);
        clock.Advance(TimeSpan.FromDays(1));
        await WaitForRuntimeAsync(runtime, () => runtime.Discovery.State.LastMaintenanceCheckAtUtc == clock.GetUtcNow());
        Assert.Equal(policyWrites + 1, fixture.Azure.PolicyWrites);
        Assert.Equal(clock.GetUtcNow().AddDays(90), fixture.Journal.Value.PolicyExpiry);
    }

    [Fact]
    public async Task RuntimeStartupRenewsPolicyWithFewerThanThirtyDaysRemaining()
    {
        var fixture = new Fixture();
        await fixture.ProvisionAsync();
        var package = fixture.Controller.CopyPackage();
        var clock = new DashboardRuntimeTests.ManualClock();
        clock.Advance(fixture.Clock.Now.AddDays(61) - clock.GetUtcNow());
        var writes = fixture.Azure.PolicyWrites;
        await using var runtimeFixture = new DashboardRuntimeTests.Fixture(new()
        {
            TimeProvider = clock, DiscoveryJournal = fixture.Journal, DiscoveryAzure = _ => fixture.Azure,
            TunnelRunner = new DashboardRuntimeTests.FakeTunnelRunner(), TunnelHealthProbe = new DashboardRuntimeTests.FakeProbe()
        }, System.Text.Json.JsonSerializer.Serialize(new DashboardSettings { AutoStartSharing = false }));
        await runtimeFixture.Runtime.InitializeAsync();
        Assert.Equal(writes + 1, fixture.Azure.PolicyWrites);
        Assert.Equal(clock.GetUtcNow().AddDays(90), fixture.Journal.Value!.PolicyExpiry);
        Assert.Equal(package, runtimeFixture.Runtime.CopyDiscoveryPackage());
    }

    private static async Task WaitForRuntimeAsync(DashboardRuntime runtime, Func<bool> predicate)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(RuntimeInvalidation change)
        {
            if (change.Domain == "discovery" && predicate()) completion.TrySetResult();
        }
        runtime.Changed += Changed;
        try
        {
            if (predicate()) completion.TrySetResult();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { runtime.Changed -= Changed; }
    }

    private sealed class DiscoveryProbe : ITunnelHealthProbe
    {
        public int SuccessfulPublicRechecksRemaining = int.MaxValue;
        public bool BlockPublicHealth;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task VerifyAsync(Uri baseUri, CancellationToken token)
        {
            if (baseUri.Scheme != Uri.UriSchemeHttps) return;
            if (BlockPublicHealth)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            if (Interlocked.Decrement(ref SuccessfulPublicRechecksRemaining) < 0)
                throw new TunnelException("Synthetic anonymous public health failure.");
        }
    }

    private sealed class Fixture
    {
        public Clock Clock { get; } = new();
        public Journal Journal { get; } = new();
        public FakeAzure Azure { get; } = new();
        public PrivateDiscoveryController Controller { get; }
        public Fixture() => Controller = NewController();
        public PrivateDiscoveryController NewController() => new(Journal, _ => Azure, Clock);
        public async Task ProvisionAsync()
        {
            var preview = await Controller.PreviewAsync(Target, default);
            await Controller.ProvisionAsync(preview.ConfirmationId, default);
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Journal : IPrivateDiscoveryJournal
    {
        public PrivateDiscoveryRegistration? Value { get; private set; }
        public Task<PrivateDiscoveryRegistration?> LoadAsync(CancellationToken token) => Task.FromResult(Value);
        public Task SaveAsync(PrivateDiscoveryRegistration registration, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Value = registration;
            return Task.CompletedTask;
        }
    }
    private sealed class FakeAzure : IPrivateDiscoveryAzure
    {
        public PrivateDiscoveryRemote? Remote { get; set; }
        public bool AtomicPolicyUpdatesSupported { get; set; } = true;
        public int Mutations { get; private set; }
        public int PolicyWrites { get; private set; }
        public int SasVerifications { get; private set; }
        public List<byte[]> PublishedDocuments { get; } = [];
        public bool FailInitialBlob { get; set; }
        public bool FailSasVerification { get; set; }
        public bool FailAfterBlobWrite { get; set; }
        public bool BlockNextBlobWrite { get; set; }
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int revision;
        private string NextETag() => "etag-" + ++revision;
        public Task<PrivateDiscoveryAccountInfo> ValidateAccountAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new PrivateDiscoveryAccountInfo(Target.Endpoint, "Enabled", "Allow", true, "None",
                AtomicPolicyUpdatesSupported: AtomicPolicyUpdatesSupported));
        }
        public Task VerifyAnonymousDeniedAsync(string container, CancellationToken token) => Task.CompletedTask;
        public Task<PrivateDiscoveryRemote?> InspectAsync(string container, CancellationToken token) => Task.FromResult(Remote);
        public Task CreateAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] document, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Remote is not null) throw new IOException("collision");
            Mutations++;
            Remote = new(NextETag(), null, metadata, true, [], []);
            if (FailInitialBlob) throw new IOException("interrupted");
            return InitializeBlobAsync(container, metadata, document, Remote.ContainerETag, token);
        }
        public Task InitializeBlobAsync(string container, IReadOnlyDictionary<string, string> metadata, byte[] document, string containerETag, CancellationToken token)
        {
            Assert.Equal(Remote!.ContainerETag, containerETag);
            Assert.Empty(Remote.Blobs);
            Mutations++;
            Remote = Remote with { BlobETag = NextETag(), Blobs = [PrivateDiscoveryAzure.BlobName], BlobMetadata = metadata, Document = document };
            return Task.CompletedTask;
        }
        public Task<IPrivateDiscoveryLease> AcquireAsync(string container, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Assert.NotNull(Remote);
            return Task.FromResult<IPrivateDiscoveryLease>(new Lease(this));
        }
        public Task<Uri> CreateReadSasAsync(string container, string policyId, CancellationToken token) => Task.FromResult(new Uri(
            $"https://testaccount.blob.core.windows.net/{container}/endpoint.json?sv=2025-01-05&sr=b&spr=https&si={policyId}&sig={Uri.EscapeDataString(Convert.ToBase64String(new byte[32]))}"));
        public Task VerifyReadSasAsync(Uri sas, byte[] expectedDocument, CancellationToken token)
        {
            SasVerifications++;
            if (FailSasVerification) throw new IOException("sas verification failed");
            Assert.Equal(Remote!.Document, expectedDocument);
            return Task.CompletedTask;
        }
        private sealed class Lease(FakeAzure owner) : IPrivateDiscoveryLease
        {
            public Task<PrivateDiscoveryRemote> ReadAsync(CancellationToken token) => Task.FromResult(owner.Remote!);
            public Task WritePoliciesAsync(IReadOnlyList<PrivateDiscoveryPolicy> policies, string etag, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Assert.Equal(owner.Remote!.ContainerETag, etag);
                owner.PolicyWrites++;
                owner.Mutations++;
                owner.Remote = owner.Remote with { ContainerETag = owner.NextETag(), Policies = policies.ToArray() };
                return Task.CompletedTask;
            }
            public async Task WriteBlobAsync(byte[] bytes, string etag, CancellationToken token)
            {
                if (owner.BlockNextBlobWrite)
                {
                    owner.BlockNextBlobWrite = false;
                    owner.WriteEntered.TrySetResult();
                    await owner.AllowWrite.Task.WaitAsync(token);
                }
                token.ThrowIfCancellationRequested();
                Assert.Equal(owner.Remote!.BlobETag, etag);
                owner.Mutations++;
                owner.PublishedDocuments.Add(bytes.ToArray());
                owner.Remote = owner.Remote with { BlobETag = owner.NextETag(), Document = bytes };
                if (owner.FailAfterBlobWrite)
                {
                    owner.FailAfterBlobWrite = false;
                    throw new IOException("uncertain write");
                }
            }
            public Task DeleteBlobAsync(string etag, CancellationToken token)
            {
                Assert.Equal(owner.Remote!.BlobETag, etag);
                owner.Mutations++;
                owner.Remote = owner.Remote with { BlobETag = null, Blobs = [], BlobMetadata = null, Document = null };
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
