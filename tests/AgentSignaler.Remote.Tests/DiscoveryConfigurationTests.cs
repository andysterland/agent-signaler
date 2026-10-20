using System.Text.Json;
using System.Security.Cryptography;
using AgentSignaler.Contracts;
using AgentSignaler.Discovery;
using AgentSignaler.Remote;
using Xunit;

namespace AgentSignaler.Remote.Tests;

public sealed class DiscoveryConfigurationTests
{
    private static RemoteConfiguration Direct => new RemoteConfiguration { MachineId = Guid.NewGuid() }
        .WithDashboardUrl("https://synthetic.example/").ToVersion5() with
        { DetailedReportingEnabled = false, HeartbeatIntervalSeconds = 600 };

    [Fact]
    public void ExplicitUpgradePreservesIdentityPreferencesAndCanonicalDirectEndpoint()
    {
        var before = Direct;
        var upgraded = before.ToVersion6();
        var json = JsonSerializer.Serialize(upgraded, Protocol.Json);
        var loaded = JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json)!;
        Assert.Equal(6, loaded.Version);
        Assert.Equal(RemoteConnectionMode.Direct, loaded.ConnectionMode);
        Assert.False(loaded.IsDiscovery);
        Assert.Equal(before.MachineId, loaded.MachineId);
        Assert.False(loaded.DetailedReportingEnabled);
        Assert.Equal(600, loaded.HeartbeatIntervalSeconds);
        Assert.Equal(before.BaseUri, loaded.BaseUri);
        Assert.Null(loaded.Discovery);
        Assert.Contains("\"connectionMode\":\"direct\"", json);
        Assert.DoesNotContain("\"discovery\"", json);
    }

    [Fact]
    public void CompatibilityHelpersPreserveV6AndDowngradeIsBlocked()
    {
        var config = Direct.ToVersion6();
        Assert.Equal(config, config.ToVersion2());
        Assert.Equal(config, config.ToVersion3());
        Assert.Equal(config, config.ToVersion4());
        Assert.Throws<InvalidDataException>(() => config.ToVersion5());
        Assert.Equal(6, config.WithDashboardUrl("https://other.synthetic.example/").Version);
    }

    [Fact]
    public void DiscoveryRoundTripsOnlyProtectedReferenceAndTrustMetadataAndCannotExposeBaseUri()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var prior = Direct;
        var config = prior.WithDiscovery(new(Guid.NewGuid().ToString("N"), new string('a', 64)),
            Guid.NewGuid(), DiscoveryCodec.ExportPublicKey(key), 1);
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        var loaded = JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json)!;
        Assert.True(loaded.IsDiscovery);
        Assert.Equal(config.Discovery, loaded.Discovery);
        Assert.Equal(config.DiscoveryId, loaded.DiscoveryId);
        Assert.Equal(config.DiscoverySigningPublicKey, loaded.DiscoverySigningPublicKey);
        Assert.Equal(1, loaded.DiscoveryCredentialRevision);
        Assert.Equal(prior.MachineId, loaded.MachineId);
        Assert.False(loaded.DetailedReportingEnabled);
        Assert.Equal(600, loaded.HeartbeatIntervalSeconds);
        Assert.DoesNotContain("dashboardBaseUrl", json);
        Assert.DoesNotContain("sig=", json);
        Assert.Throws<InvalidOperationException>(() => loaded.BaseUri);
        Assert.Throws<InvalidOperationException>(() => loaded.Endpoint);
        Assert.Equal(loaded, loaded.ToVersion4());
        Assert.Throws<InvalidDataException>(() => loaded.ToVersion5());
        Assert.Null(loaded.WithDashboardUrl("https://direct.synthetic.example/").Discovery);
        Assert.DoesNotContain(loaded.Discovery!.Id, loaded.ToString());
        Assert.DoesNotContain(loaded.DiscoverySigningPublicKey!, loaded.ToString());
    }

    [Theory]
    [InlineData(",\"dashboardBaseUrl\":null")]
    [InlineData(",\"discoveryCredentialRevision\":2")]
    public void DiscoveryRejectsMixedAndDuplicateFields(string extra)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = Direct.WithDiscovery(new(Guid.NewGuid().ToString("N"), new string('a', 64)), Guid.NewGuid(), DiscoveryCodec.ExportPublicKey(key), 1);
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json[..^1] + extra + "}", Protocol.Json));
    }

    [Fact]
    public void ProtectedReferenceRejectsDuplicateAndUnknownNestedFields()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = Direct.WithDiscovery(new(Guid.NewGuid().ToString("N"), new string('a', 64)), Guid.NewGuid(), DiscoveryCodec.ExportPublicKey(key), 1);
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        foreach (var field in new[] { "\"unexpected\":true,", "\"ID\":\"" + config.Discovery!.Id + "\"," })
            Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json.Replace("\"discovery\":{", "\"discovery\":{" + field), Protocol.Json));
    }

    [Theory]
    [InlineData(",\"CONNECTIONMODE\":\"direct\"")]
    [InlineData(",\"unknown\":true")]
    [InlineData(",\"host\":\"localhost\"")]
    [InlineData(",\"discovery\":{}")]
    public void V6RejectsDuplicateUnknownAndMixedFields(string extra)
    {
        var json = JsonSerializer.Serialize(Direct.ToVersion6(), Protocol.Json);
        var error = Record.Exception(() => JsonSerializer.Deserialize<RemoteConfiguration>(json[..^1] + extra + "}", Protocol.Json));
        Assert.True(error is InvalidDataException or JsonException);
    }

    [Theory]
    [InlineData("privateAzureDiscovery")]
    [InlineData("unknown")]
    [InlineData("Direct")]
    public void V6RequiresValidExplicitDiscriminatorAndMatchingFields(string mode)
    {
        var json = JsonSerializer.Serialize(Direct.ToVersion6(), Protocol.Json)
            .Replace("\"connectionMode\":\"direct\"", "\"connectionMode\":\"" + mode + "\"");
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Fact]
    public void V6RejectsMissingDiscriminator()
    {
        var json = JsonSerializer.Serialize(Direct.ToVersion6(), Protocol.Json)
            .Replace(",\"connectionMode\":\"direct\"", "");
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json, Protocol.Json));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void LegacySchemaRejectsDiscoveryFields(int version)
    {
        var config = version == 1 ? new RemoteConfiguration { Host = "localhost", MachineId = Guid.NewGuid() } : Direct with
        { Version = version, HeartbeatIntervalSeconds = PresenceProtocol.DefaultHeartbeatIntervalSeconds };
        var json = JsonSerializer.Serialize(config, Protocol.Json);
        Assert.DoesNotContain("connectionMode", json);
        Assert.Throws<InvalidDataException>(() => JsonSerializer.Deserialize<RemoteConfiguration>(json[..^1] + ",\"connectionMode\":\"direct\"}", Protocol.Json));
    }
}
