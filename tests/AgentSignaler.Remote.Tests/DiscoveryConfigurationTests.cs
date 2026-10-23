using System.Text.Json;
using AgentSignaler.Contracts;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class DiscoveryConfigurationTests
{
    private static RemoteConfiguration Direct => new RemoteConfiguration { MachineId = Guid.NewGuid() }
        .WithDashboardUrl("https://synthetic.example/").ToVersion5() with
        { DetailedReportingEnabled = false, HeartbeatIntervalSeconds = 600 };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void AllPriorDirectVersionsRemainReadable(int version)
    {
        var configuration = version == 1 ? new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid() } :
            Direct with { Version = version, HeartbeatIntervalSeconds = version < 3 ? 300 : 600 };
        var json = JsonSerializer.Serialize(configuration, Protocol.Json);
        var loaded = JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json)!;
        Assert.False(loaded.IsDiscovery);
        Assert.Equal(configuration.BaseUri, loaded.BaseUri);
        Assert.Equal(configuration.HeartbeatIntervalSeconds, loaded.HeartbeatIntervalSeconds);
        Assert.Equal(version >= 5 && configuration.DetailedReportingEnabled, loaded.IsDetailedReportingEnabled);
        Assert.Equal(json, JsonSerializer.Serialize(loaded, Protocol.Json));
    }

    [Fact]
    public void DnsRoundtripPreservesPreferencesAndExplicitDirectConversionKeepsVersion()
    {
        var prior = Direct;
        var config = prior.WithDnsTxtDiscovery("SIGNALER.CableSorter.IO.");
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        var loaded = JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json)!;
        Assert.Equal(7, loaded.Version);
        Assert.Equal(2, (int)loaded.ConnectionMode);
        Assert.True(loaded.IsDiscovery);
        Assert.Equal("signaler.cablesorter.io", loaded.DnsRecordName);
        Assert.Equal(prior.MachineId, loaded.MachineId);
        Assert.False(loaded.DetailedReportingEnabled);
        Assert.Equal(600, loaded.HeartbeatIntervalSeconds);
        Assert.DoesNotContain("dashboardBaseUrl", json);
        Assert.DoesNotContain("discoveryId", json);
        Assert.Throws<InvalidOperationException>(() => loaded.BaseUri);
        Assert.Equal(loaded, loaded.ToVersion4());
        Assert.Equal(loaded, loaded.ToVersion6());
        Assert.Throws<InvalidDataException>(() => loaded.ToVersion5());
        var direct = loaded.WithDashboardUrl("https://direct.synthetic.example/");
        Assert.Equal(7, direct.Version);
        Assert.Null(direct.DnsRecordName);
        Assert.False(direct.IsDiscovery);
        Assert.Equal(direct.BaseUri, JsonSerializer.Deserialize<RemoteConfiguration>(JsonSerializer.Serialize(direct, Protocol.Json), Protocol.Json)!.BaseUri);
    }

    [Theory]
    [InlineData(",\"dashboardBaseUrl\":null")]
    [InlineData(",\"DNSRECORDNAME\":\"other.example\"")]
    [InlineData(",\"unknown\":true")]
    [InlineData(",\"host\":\"localhost\"")]
    [InlineData(",\"discovery\":{}")]
    public void DnsRejectsMixedDuplicateAndUnknownFields(string extra)
    {
        var json = JsonSerializer.Serialize(Direct.WithDnsTxtDiscovery("signaler.cablesorter.io"), Protocol.Json);
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json[..^1] + extra + "}", Protocol.Json));
    }

    [Fact]
    public void VersionSixDirectNeverBecomesDnsAndNumericOneRemainsUnsupported()
    {
        var config = Direct.ToVersion6();
        Assert.Equal(config, config.ToVersion2());
        Assert.Equal(config, config.ToVersion3());
        Assert.Equal(config, config.ToVersion4());
        Assert.Throws<InvalidDataException>(() => config.ToVersion5());
        Assert.Throws<InvalidDataException>(() => (config with { ConnectionMode = (RemoteConnectionMode)1 }).Validate());
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(
            json.Replace("\"direct\"", "\"dnsTxtDiscovery\""), Protocol.Json));
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(
            json[..^1] + ",\"dnsRecordName\":null}", Protocol.Json));
    }

    [Fact]
    public void PrivateAzureConfigurationFailsActionablyBeforeAnyCredentialAccess()
    {
        var json = JsonSerializer.Serialize(Direct.ToVersion6(), Protocol.Json)
            .Replace("\"direct\"", "\"privateAzureDiscovery\"");
        var error = Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
        Assert.Contains("no longer supported", error.Message);
        Assert.Contains("Saved credentials", error.Message);
        Assert.Contains("Configurator", error.Message);
    }
}
