using AgentSignaler.AppUpdates;
using AgentSignaler.Updates;

namespace AgentSignaler.Integration.Tests;

public sealed class DashboardUpdateStartupTests
{
    private const string DirectoryPath = @"C:\AgentSignaler";
    private static readonly ReleaseCandidate Candidate = new(42, "v1.0.99", new(1, 0, 99),
        DateTimeOffset.UnixEpoch, false, []);

    [Theory]
    [InlineData("update_inventory_unavailable")]
    [InlineData("update_inventory_ineligible")]
    [InlineData("update_inventory_ambiguous")]
    [InlineData("update_inventory_limit")]
    public async Task FailedEligibilityWithoutMaintenanceOpensWithWarningAndCannotUpdate(string code)
    {
        var reads = 0;
        using var host = AppUpdateHost.OpenVerified(true, DirectoryPath,
            () => { reads++; return false; },
            () => throw new InvalidDataException(code), UnexpectedInstalled);

        Assert.Equal(2, reads);
        Assert.Null(host.Installation);
        Assert.Equal(code, host.StartupWarning?.Code);
        Assert.Empty(await host.ReadRecoveryAsync(CancellationToken.None));
        // No discovery, registration, processes or servicing should be required.
        Assert.Equal(host.StartupWarning!.Message, await host.BeginUpdateAsync(Candidate, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingOrAppearingRecordsStillRefuseFailedEligibility(bool appearsAfterInventory)
    {
        var reads = 0;
        var error = Assert.Throws<UpdateStartupRefusedException>(() =>
            AppUpdateHost.OpenVerified(true, DirectoryPath,
                () => ++reads > (appearsAfterInventory ? 1 : 0),
                () => throw new InvalidDataException("update_inventory_unavailable"), UnexpectedInstalled));

        Assert.Equal("update_maintenance_inventory_unavailable", error.Message);
        Assert.Equal(appearsAfterInventory ? 2 : 1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableRecordsStillRefuseStartup(bool failsOnRecheck)
    {
        var reads = 0;
        var inventoryReads = 0;
        var error = Assert.Throws<UpdateStartupRefusedException>(() =>
            AppUpdateHost.OpenVerified(true, DirectoryPath,
                () => ++reads > (failsOnRecheck ? 1 : 0) ? throw new IOException("private-record-path") : false,
                () => { inventoryReads++; throw new InvalidDataException("update_inventory_ineligible"); },
                UnexpectedInstalled));

        Assert.Equal("update_maintenance_unverified", error.Message);
        Assert.Equal(failsOnRecheck ? 1 : 0, inventoryReads);
        Assert.DoesNotContain("private-record-path", error.UserMessage);
    }

    [Fact]
    public void StrictSharedStartupStillRefusesEligibilityFailure()
    {
        // Client/Configurator retain Open(package)'s strict policy, not the
        // explicit OpenDashboard opt-in.
        var reads = 0;
        var error = Assert.Throws<UpdateStartupRefusedException>(() =>
            AppUpdateHost.OpenVerified(false, DirectoryPath,
                () => { reads++; return false; },
                () => throw new InvalidDataException("update_inventory_ineligible"), UnexpectedInstalled));
        Assert.Equal("update_maintenance_unverified", error.Message);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NormalPortableOrDifferentInstallationDoesNotShowVerificationWarning(bool differentDirectory)
    {
        var reads = 0;
        using var host = AppUpdateHost.OpenVerified(true, DirectoryPath,
            () => { reads++; return false; },
            () => differentDirectory ? Installed(@"C:\OtherAgentSignaler") : null, UnexpectedInstalled);
        Assert.Equal(2, reads);
        Assert.Null(host.Installation);
        Assert.Null(host.StartupWarning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingInventoryNeverBypassesExistingOrAppearingRecords(bool appearsAfterInventory)
    {
        var reads = 0;
        var error = Assert.Throws<UpdateStartupRefusedException>(() =>
            AppUpdateHost.OpenVerified(true, DirectoryPath,
                () => ++reads > (appearsAfterInventory ? 1 : 0), () => null, UnexpectedInstalled));
        Assert.Equal("update_maintenance_inventory_unavailable", error.Message);
    }

    [Theory]
    [InlineData("update_maintenance_active")]
    [InlineData("update_recovery_required")]
    [InlineData("update_maintenance_unverified")]
    public void VerifiedInstallationStillHonorsCoordinatorRefusal(string code)
    {
        var refusal = new UpdateStartupRefusedException(code, "safe maintenance refusal");
        var error = Assert.Throws<UpdateStartupRefusedException>(() =>
            AppUpdateHost.OpenVerified(true, DirectoryPath, () => true, () => Installed(DirectoryPath),
                _ => throw refusal));
        Assert.Same(refusal, error);
    }

    [Fact]
    public void CoordinatorReadFailureNeverDegradesEvenWhenInitialRecordsWereAbsent()
    {
        var error = Assert.Throws<UpdateStartupRefusedException>(() =>
            AppUpdateHost.OpenVerified(true, DirectoryPath, () => false, () => Installed(DirectoryPath),
                _ => throw new UnauthorizedAccessException("private-maintenance-path")));
        Assert.Equal("update_maintenance_unverified", error.Message);
        Assert.DoesNotContain("private-maintenance-path", error.UserMessage);
    }

    [Fact]
    public void UnexpectedEligibilityExceptionsAreNotSwallowed()
    {
        var unexpected = new NotSupportedException("unexpected");
        Assert.Same(unexpected, Assert.Throws<NotSupportedException>(() =>
            AppUpdateHost.OpenVerified(true, DirectoryPath, () => false,
                () => throw unexpected, UnexpectedInstalled)));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("json")]
    [InlineData("win32")]
    [InlineData("invalid")]
    public void ExpectedEligibilityFailuresUseSafeFallbackCode(string category)
    {
        const string privateText = @"C:\private-path account-id raw-output";
        Exception failure = category switch
        {
            "io" => new IOException(privateText),
            "access" => new UnauthorizedAccessException(privateText),
            "json" => new System.Text.Json.JsonException(privateText),
            "win32" => new System.ComponentModel.Win32Exception(5, privateText),
            _ => new InvalidDataException(privateText)
        };
        using var host = AppUpdateHost.OpenVerified(true, DirectoryPath, () => false,
            () => throw failure, UnexpectedInstalled);
        Assert.Equal("update_inventory_unverified", host.StartupWarning?.Code);
        Assert.DoesNotContain(privateText, host.StartupWarning!.Message);
    }

    [Theory]
    [InlineData(UpdateCheckStatus.Unknown)]
    [InlineData(UpdateCheckStatus.Checking)]
    [InlineData(UpdateCheckStatus.UpToDate)]
    [InlineData(UpdateCheckStatus.Available)]
    [InlineData(UpdateCheckStatus.CheckFailed)]
    [InlineData(UpdateCheckStatus.CheckIncomplete)]
    public void RoutineCheckStatesAndDismissedCachedOfferCannotReplaceStartupWarning(UpdateCheckStatus status)
    {
        var warning = UpdateEligibilityWarning.FromFailure(new InvalidDataException("update_inventory_ineligible"));
        var presentation = UpdatePanelPresentation.Create(
            new(status, Candidate, DiagnosticCode: "private-discovery-error", FromCache: true), warning, Candidate.Id);
        Assert.True(presentation.IsWarning);
        Assert.True(presentation.IsOpen);
        Assert.False(presentation.CanUpdate);
        Assert.Null(presentation.Candidate);
        Assert.Equal(warning.Message, presentation.Message);
        Assert.DoesNotContain("private-discovery-error", presentation.Message);
    }

    [Fact]
    public void NormalUpdatePresentationStillOffersAndDismissesRelease()
    {
        var state = new UpdateCheckResult(UpdateCheckStatus.Available, Candidate);
        var offered = UpdatePanelPresentation.Create(state, null, null);
        Assert.False(offered.IsWarning);
        Assert.True(offered.IsOpen);
        Assert.True(offered.CanUpdate);
        Assert.Equal(Candidate, offered.Candidate);
        Assert.False(UpdatePanelPresentation.Create(state, null, Candidate.Id).IsOpen);
        Assert.False(UpdatePanelPresentation.Create(new(UpdateCheckStatus.UpToDate), null, null).CanUpdate);
    }

    private static UpdateInstalledPackage Installed(string directory) => new(UpdatePackage.Dashboard,
        Guid.Parse("53268273-AC98-4A33-86E2-31B38B37A131"), UpdatePolicy.UpgradeCode(UpdatePackage.Dashboard),
        new Version(1, 0, 28), directory, "test-user");

    private static AppUpdateHost UnexpectedInstalled(UpdateInstalledPackage _) =>
        throw new NotSupportedException("Installed maintenance must not be opened by this fixture.");
}
