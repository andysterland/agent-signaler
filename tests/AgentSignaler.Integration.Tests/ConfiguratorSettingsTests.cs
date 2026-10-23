using System.Text;
using System.Text.Json;
using AgentSignaler.Configurator;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "test-data", Guid.NewGuid().ToString("N"));
    private readonly Guid machineId = Guid.NewGuid();
    private string SettingsPath => Path.Combine(directory, "configurator-settings.json");

    [Fact]
    public void CustomRelayLocationIsCanonicalAndSurvivesConfigurationReload()
    {
        var relay = Path.Combine(directory, "remote installation", "AgentSignaler.Relay.exe");
        AtomicFile.Write(relay, []);
        var configPath = Path.Combine(directory, "remote.json");
        var config = Configuration("https://example.test").ToVersion4() with
        {
            RelayPath = ConfiguratorSettings.RelayLocation("  " + relay + "  ")
        };
        AtomicFile.Write(configPath, JsonSerializer.SerializeToUtf8Bytes(config, Protocol.Json));

        Assert.Equal(relay, RemoteConfiguration.Load(configPath).RelayPath);
        using var json = JsonDocument.Parse(File.ReadAllBytes(configPath));
        Assert.Equal(relay, json.RootElement.GetProperty("relayPath").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("AgentSignaler.Relay.exe")]
    [InlineData(@"relative\AgentSignaler.Relay.exe")]
    [InlineData(@"C:\")]
    public void InvalidRelayLocationsAreRejected(string path) =>
        Assert.Throws<InvalidDataException>(() => ConfiguratorSettings.RelayLocation(path));

    [Fact]
    public void MissingRelayAndWrongExecutableAreRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            ConfiguratorSettings.RelayLocation(Path.Combine(directory, "AgentSignaler.Relay.exe")));
        var wrongExecutable = Path.Combine(directory, "AgentSignaler.Client.exe");
        AtomicFile.Write(wrongExecutable, []);
        Assert.Throws<InvalidDataException>(() => ConfiguratorSettings.RelayLocation(wrongExecutable));
    }

    [Theory]
    [InlineData(1, 60)]
    [InlineData(5, 300)]
    [InlineData(60, 3600)]
    public void WholeMinuteIntervalsConvertToSeconds(double minutes, int seconds) =>
        Assert.Equal(seconds, ConfiguratorSettings.HeartbeatSeconds(minutes));

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidIntervalsAreRejected(double minutes) =>
        Assert.Throws<InvalidDataException>(() => ConfiguratorSettings.HeartbeatSeconds(minutes));

    [Fact]
    public void MissingSettingsLeaveExistingConfigurationAsFallback()
    {
        Assert.Null(ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.False(Directory.Exists(directory));
    }

    [Theory]
    [InlineData("https://example.devtunnels.ms/", "https://example.devtunnels.ms")]
    [InlineData("https://example.test:8443", "https://example.test:8443")]
    [InlineData("http://localhost:51820/", "http://localhost:51820")]
    [InlineData("http://[::1]:51820/", "http://[::1]:51820")]
    public void SuccessfulUrlSurvivesReload(string url, string expected)
    {
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration(url));

        Assert.Equal(expected, ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void LatestSuccessfulUrlReplacesPreviousWithoutChangingIntegration()
    {
        var configPath = Path.Combine(directory, "remote.json");
        var original = JsonSerializer.SerializeToUtf8Bytes(Configuration("https://installed.test"), Protocol.Json);
        AtomicFile.Write(configPath, original);
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://first.test"));
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://latest.test"));

        Assert.Equal("https://latest.test", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.Equal(original, File.ReadAllBytes(configPath));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Fact]
    public void DnsChoicePreservesPreviousDirectUrlButSavesOnlyHostnameForDiscovery()
    {
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://previous.test"));
        var config = Configuration("https://unused.test").WithDnsTxtDiscovery("signaler.cablesorter.io");
        ConfiguratorSettings.SaveUrl(SettingsPath, config);
        Assert.Equal(RemoteConnectionMode.DnsTxtDiscovery, ConfiguratorSettings.LoadMode(SettingsPath));
        Assert.Equal("https://previous.test", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.Equal("signaler.cablesorter.io", ConfiguratorSettings.LoadDnsRecordName(SettingsPath));
        var saved = File.ReadAllText(SettingsPath);
        Assert.Contains("previous.test", saved);
        Assert.DoesNotContain("unused.test", saved);
        Assert.DoesNotContain("sig=", saved);
        Assert.DoesNotContain("discoveryId", saved);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"privateAzureDiscovery\"")]
    public void RemovedSavedModeNeverOptsIntoDnsOrMutatesPreferences(string mode)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"lastSuccessfulDashboardUrl\":\"https://previous.test/\",\"selectedConnectionMode\":" + mode + "}");
        AtomicFile.Write(SettingsPath, bytes);
        Assert.Equal(RemoteConnectionMode.Direct, ConfiguratorSettings.LoadMode(SettingsPath));
        Assert.True(ConfiguratorSettings.HasRemovedMode(SettingsPath));
        Assert.Equal("https://previous.test", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.Equal(bytes, File.ReadAllBytes(SettingsPath));
        Assert.NotEqual(1, (int)RemoteConnectionMode.DnsTxtDiscovery);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://signaler.cablesorter.io")]
    [InlineData("name/path")]
    [InlineData("-bad.example")]
    [InlineData("two..example")]
    public void InvalidDnsHostnamesAreRejectedBeforeSaving(string name)
    {
        Assert.Throws<InvalidDataException>(() => ConfiguratorSettings.ValidateDnsRecordName(name));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public void DnsDefaultsAreSuggestedWithoutSelectingDns()
    {
        Assert.Equal(RemoteConnectionMode.Direct, ConfiguratorSettings.LoadMode(SettingsPath));
        Assert.Equal("signaler.cablesorter.io", ConfiguratorSettings.LoadDnsRecordName(SettingsPath));
        Assert.Equal("signaler.cablesorter.io", ConfiguratorSettings.ValidateDnsRecordName(" signaler.cablesorter.io "));
    }

    [Fact]
    public void DirectSaveRetainsLastDnsChoiceWithoutEnablingIt()
    {
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://unused.test").WithDnsTxtDiscovery("signaler.cablesorter.io"));
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("http://localhost:51820"));
        Assert.Equal(RemoteConnectionMode.Direct, ConfiguratorSettings.LoadMode(SettingsPath));
        Assert.Equal("http://localhost:51820", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.Equal("signaler.cablesorter.io", ConfiguratorSettings.LoadDnsRecordName(SettingsPath));
    }

    [Fact]
    public void OldDirectPreferencesRemainDirectAndReadable()
    {
        AtomicFile.Write(SettingsPath, Encoding.UTF8.GetBytes("{\"lastSuccessfulDashboardUrl\":\"http://localhost:51820/\"}"));
        Assert.Equal(RemoteConnectionMode.Direct, ConfiguratorSettings.LoadMode(SettingsPath));
        Assert.Equal("http://localhost:51820", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
    }

    [Fact]
    public void DnsPreviewContainsPublicHostnameAndNoResolvedUrlAndDoesNotWrite()
    {
        var relay = Path.Combine(directory, "AgentSignaler.Relay.exe");
        AtomicFile.Write(relay, []);
        var configPath = Path.Combine(directory, "remote.json");
        var configuration = Configuration("https://not-the-dns-endpoint.test").WithDnsTxtDiscovery("signaler.cablesorter.io") with
        {
            DetailedReportingEnabled = false,
            HeartbeatIntervalSeconds = 420
        };

        var preview = MultiTargetIntegrationManager.Preview(configuration, configPath, [], relay, startClientAtSignIn: false);

        Assert.Equal(7, preview.Config.Version);
        Assert.Equal(RemoteConnectionMode.DnsTxtDiscovery, preview.Config.ConnectionMode);
        Assert.Equal("signaler.cablesorter.io", preview.Config.DnsRecordName);
        Assert.Null(preview.Config.DashboardBaseUrl);
        Assert.False(preview.Config.DetailedReportingEnabled);
        Assert.Contains("signaler.cablesorter.io", preview.Preview);
        Assert.Contains("No direct fallback", preview.Preview);
        Assert.DoesNotContain("not-the-dns-endpoint", preview.Preview);
        Assert.DoesNotContain("credential", preview.Preview, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(configPath));
        Assert.Single(Directory.GetFiles(directory));
    }

    [Fact]
    public void UnsupportedSavedPrivateConfigurationIsNotOverwrittenBySupportedPreview()
    {
        var relay = Path.Combine(directory, "AgentSignaler.Relay.exe");
        AtomicFile.Write(relay, []);
        var configPath = Path.Combine(directory, "remote.json");
        var bytes = Encoding.UTF8.GetBytes("{\"version\":6,\"machineId\":\"" + machineId +
            "\",\"connectionMode\":\"privateAzureDiscovery\"}");
        AtomicFile.Write(configPath, bytes);
        Assert.Throws<InvalidDataException>(() =>
            MultiTargetIntegrationManager.Preview(Configuration("http://localhost:51820").ToVersion5(),
                configPath, [], relay, startClientAtSignIn: false));
        Assert.Equal(bytes, File.ReadAllBytes(configPath));
        Assert.Equal(2, Directory.GetFiles(directory).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ReadingDirectConfigurationNeverImplicitlySelectsDns(int version)
    {
        var configPath = Path.Combine(directory, "remote.json");
        var configuration = version == 1
            ? new RemoteConfiguration { MachineId = machineId, Host = "localhost", Port = 51820 }
            : Configuration("http://localhost:51820") with { Version = version };
        AtomicFile.Write(configPath, JsonSerializer.SerializeToUtf8Bytes(configuration, Protocol.Json));
        var restored = RemoteConfiguration.Load(configPath);
        Assert.Equal(RemoteConnectionMode.Direct, restored.ConnectionMode);
        Assert.False(restored.IsDiscovery);
        Assert.Equal("http://localhost:51820", restored.BaseUri.GetLeftPart(UriPartial.Authority));
    }

    [Fact]
    public void InvalidSavedModeIsRejectedInsteadOfFallingBackToDirectUrl()
    {
        AtomicFile.Write(SettingsPath, Encoding.UTF8.GetBytes("{\"lastSuccessfulDashboardUrl\":\"https://old.test/\",\"selectedConnectionMode\":99}"));
        Assert.Throws<JsonException>(() => ConfiguratorSettings.LoadMode(SettingsPath));
        Assert.ThrowsAny<JsonException>(() => ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
    }

    [Fact]
    public void InvalidConfigurationCannotReplaceSuccessfulUrl()
    {
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://working.test"));

        Assert.Throws<InvalidDataException>(() =>
            ConfiguratorSettings.SaveUrl(SettingsPath, new RemoteConfiguration()));
        Assert.Equal("https://working.test", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("""{"lastSuccessfulDashboardUrl":"https://example.test/path"}""")]
    [InlineData("""{"lastSuccessfulDashboardUrl":"https://user:password@example.test"}""")]
    public void InvalidSavedSettingsAreReported(string json)
    {
        AtomicFile.Write(SettingsPath, Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
    }

    [Fact]
    public void MalformedJsonIsReported()
    {
        AtomicFile.Write(SettingsPath, Encoding.UTF8.GetBytes("{"));
        Assert.ThrowsAny<JsonException>(() => ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
    }

    [Fact]
    public void FailedWritePreservesPreviousSuccessfulUrl()
    {
        ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://working.test"));
        using (var held = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() =>
                ConfiguratorSettings.SaveUrl(SettingsPath, Configuration("https://new.test")));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }
        Assert.Equal("https://working.test", ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
        Assert.Single(Directory.GetFiles(directory));
    }

    private RemoteConfiguration Configuration(string url) =>
        new RemoteConfiguration { MachineId = machineId }.WithDashboardUrl(url);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
