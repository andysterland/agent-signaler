using System.Text;
using System.Text.Json;
using AgentSignaler.Configurator;
using AgentSignaler.Contracts;
using AgentSignaler.Remote;

namespace AgentSignaler.Integration.Tests;

public sealed class ConfiguratorSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
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
        Assert.Throws<JsonException>(() => ConfiguratorSettings.LoadUrl(SettingsPath, machineId));
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
