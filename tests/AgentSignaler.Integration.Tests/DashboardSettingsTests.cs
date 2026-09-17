using System.Text.Json;
using AgentSignaler.Dashboard;

namespace AgentSignaler.Integration.Tests;

[CollectionDefinition("Dashboard settings environment", DisableParallelization = true)]
public sealed class DashboardSettingsEnvironmentCollection { }

[Collection("Dashboard settings environment")]
public sealed class DashboardSettingsTests
{
    [Theory]
    [InlineData("{}", true)]
    [InlineData("""{"ReceiveDetailedConversations":true}""", true)]
    [InlineData("""{"ReceiveDetailedConversations":false}""", false)]
    public void ReceiverPreferenceDefaultsOnAndPreservesExplicitOptOut(string json, bool expected)
    {
        var settings = DashboardSettings.FromJson(json);
        Assert.Equal(expected, settings.ReceiveDetailedConversations);
        Assert.Equal(settings, DashboardSettings.FromJson(JsonSerializer.Serialize(settings)));
        Assert.False(DashboardSettings.RecoveryDefaults.ReceiveDetailedConversations);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Port":51820,"Compact":false,"Theme":"Dark","AzureCliPath":null}""")]
    public void LegacySettingsRequireNoDevCenterConfiguration(string json)
    {
        var settings = DashboardSettings.FromJson(json);
        Assert.Null(settings.DevBoxSubscriptionId);
        Assert.Null(settings.DevCenterName);
        Assert.DoesNotContain("DevCenterEndpoints", JsonSerializer.Serialize(settings));
        Assert.DoesNotContain("DevCenterEndpoints", JsonSerializer.Serialize(new DashboardSettings()));
        Assert.False(DashboardSettings.RecoveryDefaults.ShouldStartSharing);
    }

    [Fact]
    public void AzureCliAndOtherSettingsStillRoundTrip()
    {
        var original = new DashboardSettings
        {
            AzureCliPath = @"C:\Tools\az.cmd",
            DevTunnelCliPath = @"C:\Tools\devtunnel.exe",
            ConnectionMode = DashboardConnectionMode.Lan,
            DevCenterName = "saved-center",
            Port = 51821,
            Compact = false,
            ShowCompactViewWhenMinimized = false,
            Theme = "Dark",
            AutoStartSharing = false,
            ReceiveDetailedConversations = false
        };
        var restored = DashboardSettings.FromJson(JsonSerializer.Serialize(original));
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData(" 11111111-2222-3333-4444-555555555555 ", null, "11111111-2222-3333-4444-555555555555", null)]
    [InlineData(null, "  my-devcenter  ", null, "my-devcenter")]
    [InlineData("", " ", null, null)]
    public void DiscoveryInputsAreNormalizedAndReplaceThePreviousTarget(
        string? subscription, string? center, string? expectedSubscription, string? expectedCenter)
    {
        var original = new DashboardSettings { Theme = "Dark", AutoStartSharing = false, DevCenterName = "old-center" };
        var updated = original.WithDiscoveryTarget(subscription, center);
        Assert.Equal(expectedSubscription, updated.DevBoxSubscriptionId?.ToString());
        Assert.Equal(expectedCenter, updated.DevCenterName);
        Assert.Equal("Dark", updated.Theme);
        Assert.False(updated.AutoStartSharing);
        Assert.Equal("old-center", original.DevCenterName);
        Assert.Equal(updated, DashboardSettings.FromJson(JsonSerializer.Serialize(updated)));
    }

    [Theory]
    [InlineData("not-a-guid", null)]
    [InlineData("00000000-0000-0000-0000-000000000000", null)]
    [InlineData("11111111-2222-3333-4444-555555555555", "my-devcenter")]
    [InlineData(null, "../invalid")]
    [InlineData(null, "invalid\ncenter")]
    public void InvalidDiscoveryInputsDoNotChangeExistingSettings(string? subscription, string? center)
    {
        var original = new DashboardSettings { DevCenterName = "saved-center" };
        Assert.Throws<ArgumentException>(() => original.WithDiscoveryTarget(subscription, center));
        Assert.Equal("saved-center", original.DevCenterName);
        Assert.Null(original.DevBoxSubscriptionId);
    }

    [Theory]
    [InlineData("""{"DevBoxSubscriptionId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"DevBoxSubscriptionId":"11111111-2222-3333-4444-555555555555","DevCenterName":"my-devcenter"}""")]
    [InlineData("""{"DevCenterName":"../invalid"}""")]
    public void InvalidPersistedDiscoveryTargetsRequireSettingsRecovery(string json)
    {
        Assert.Throws<InvalidDataException>(() => DashboardSettings.FromJson(json));
    }

    [Theory]
    [InlineData("""["https://center.westus.devcenter.azure.com/"]""")]
    [InlineData("""["https://center.westus.devcenter.azure.com/","https://CENTER.WESTUS.devcenter.azure.com:443"]""")]
    [InlineData("""["http://unsafe.example/path?secret=value"]""")]
    [InlineData("""["https://["]""")]
    [InlineData("""[42,null,"relative"]""")]
    [InlineData("null")]
    [InlineData("\"not a list\"")]
    public void UnknownEndpointSettingsArePreservedWithoutLosingOtherPreferences(string endpoints)
    {
        var settings = DashboardSettings.FromJson(
            $"{{\"DevCenterEndpoints\":{endpoints},\"Theme\":\"Dark\",\"Port\":51821,\"AutoStartSharing\":false}}");
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(51821, settings.Port);
        Assert.False(settings.AutoStartSharing);
        Assert.Contains("DevCenterEndpoints", JsonSerializer.Serialize(settings));
    }

    [Theory]
    [InlineData("""{"DevCenterEndpoints":["incomplete""")]
    [InlineData("{")]
    public void MalformedJsonStillRequiresSettingsRecovery(string json)
    {
        Assert.ThrowsAny<JsonException>(() => DashboardSettings.FromJson(json));
    }

    [Theory]
    [InlineData("11111111-2222-3333-4444-555555555555", null)]
    [InlineData(null, "my-devcenter")]
    public void DiscoveryTargetsPersistAndClearWhilePreservingLegacyPreferences(string? subscription, string? center)
    {
        var previous = Environment.GetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR");
        var directory = Path.GetFullPath(Path.Combine("test-artifacts", $"settings-{Guid.NewGuid():N}"));
        try
        {
            Environment.SetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR", directory);
            Directory.CreateDirectory(directory);
            const string json = """{"DevCenterEndpoints":["https://old.westus.devcenter.azure.com/"],"Theme":"Dark","AutoStartSharing":false,"ReceiveDetailedConversations":false}""";
            File.WriteAllText(DashboardSettings.FilePath, json);
            var settings = DashboardSettings.Load();
            Assert.Equal(json, File.ReadAllText(DashboardSettings.FilePath));
            settings = settings.WithDiscoveryTarget(subscription, center);
            settings.Save();
            Assert.Contains("DevCenterEndpoints", File.ReadAllText(DashboardSettings.FilePath));
            var restored = DashboardSettings.Load();
            Assert.Equal(settings, restored);
            Assert.Equal(subscription, restored.DevBoxSubscriptionId?.ToString());
            Assert.Equal(center, restored.DevCenterName);
            Assert.Equal("Dark", restored.Theme);
            Assert.False(restored.AutoStartSharing);
            Assert.False(restored.ReceiveDetailedConversations);
            restored.WithDiscoveryTarget("", "").Save();
            var cleared = DashboardSettings.Load();
            Assert.Null(cleared.DevBoxSubscriptionId);
            Assert.Null(cleared.DevCenterName);
            Assert.Equal(restored with { DevBoxSubscriptionId = null, DevCenterName = null }, cleared);
            Assert.False(File.Exists(DashboardSettings.FilePath + ".new"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_SIGNALER_DATA_DIR", previous);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
