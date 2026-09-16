using System.Text.Json;
using AgentSignaler.Dashboard;
using AgentSignaler.Remote;
using AgentSignaler.Tunneling;

namespace AgentSignaler.Integration.Tests;

public sealed class DashboardConnectionTests
{
    [Theory]
    [InlineData("Preparing", "Preparing local storage")]
    [InlineData("Starting", "Starting HTTP listener on port 51839")]
    [InlineData("Running", "Running - loopback HTTP listener on port 51839")]
    [InlineData("Failed", "Failed to start")]
    [InlineData("Stopping", "Stopping")]
    [InlineData("Stopped", "Stopped")]
    public void ServiceStatusDescribesItsActualPhase(string phase, string expected)
    {
        var view = StartupStatusPresentation.Create(Enum.Parse<ReceiverStartupState>(phase),
            DashboardConnectionMode.DevTunnel, 51839, null);
        Assert.StartsWith("Service: ", view.Service);
        Assert.Contains(expected, view.Service);
    }

    [Theory]
    [InlineData(TunnelState.Stopped, "Stopped", false)]
    [InlineData(TunnelState.CheckingAccount, "Checking CLI and account", true)]
    [InlineData(TunnelState.AccountRequired, "Sign-in required", false)]
    [InlineData(TunnelState.Creating, "Creating", true)]
    [InlineData(TunnelState.Starting, "Connecting", true)]
    [InlineData(TunnelState.Verifying, "Verifying public HTTPS", true)]
    [InlineData(TunnelState.Connected, "Connected", false)]
    [InlineData(TunnelState.Stopping, "Stopping", false)]
    [InlineData(TunnelState.Faulted, "Error", false)]
    [InlineData(TunnelState.Unsupported, "CLI unavailable", false)]
    [InlineData(TunnelState.Reconnecting, "Reconnecting", true)]
    public void TunnelStatusShowsPhaseAndDetailsIndependentlyOfRunningService(TunnelState state, string phase, bool isStarting)
    {
        var view = StartupStatusPresentation.Create(ReceiverStartupState.Running,
            DashboardConnectionMode.DevTunnel, 51839, new TunnelStatus(state, "Current operation details"));
        Assert.Contains("Service: Running", view.Service);
        Assert.Equal($"Dev Tunnels: {phase} - Current operation details", view.Tunnel);
        Assert.Equal(isStarting, view.IsTunnelStarting);
    }

    [Fact]
    public void TunnelProgressIsHiddenWithoutAnInternetReceiverOrAfterSetupFailure()
    {
        foreach (var state in Enum.GetValues<TunnelState>())
        {
            var tunnel = new TunnelStatus(state, "Current operation details");
            foreach (var receiver in Enum.GetValues<ReceiverStartupState>())
            {
                Assert.False(StartupStatusPresentation.Create(receiver,
                    DashboardConnectionMode.Lan, 51839, tunnel).IsTunnelStarting);
                if (receiver != ReceiverStartupState.Running)
                    Assert.False(StartupStatusPresentation.Create(receiver,
                        DashboardConnectionMode.DevTunnel, 51839, tunnel).IsTunnelStarting);
            }
            Assert.False(StartupStatusPresentation.Create(ReceiverStartupState.Running,
                DashboardConnectionMode.DevTunnel, 51839, tunnel, "Check saved tunnel identity").IsTunnelStarting);
        }
    }

    [Fact]
    public void TunnelStatusWaitsForServiceAndDoesNotClaimConnectedAfterServiceFailure()
    {
        var connected = new TunnelStatus(TunnelState.Connected, "Ready", new Uri("https://host.devtunnels.ms/"));
        var starting = StartupStatusPresentation.Create(ReceiverStartupState.Starting,
            DashboardConnectionMode.DevTunnel, 51839, connected);
        Assert.Equal("Dev Tunnels: Waiting for the local service", starting.Tunnel);
        var failed = StartupStatusPresentation.Create(ReceiverStartupState.Failed,
            DashboardConnectionMode.DevTunnel, 51839, connected);
        Assert.Equal("Dev Tunnels: Unavailable - local service is not running", failed.Tunnel);
    }

    [Fact]
    public void LanStatusExplainsThatTunnelsAreNotUsed()
    {
        var view = StartupStatusPresentation.Create(ReceiverStartupState.Running,
            DashboardConnectionMode.Lan, 51839, null);
        Assert.Contains("LAN HTTP listener on port 51839", view.Service);
        Assert.Equal("Dev Tunnels: Not used (LAN mode)", view.Tunnel);
        Assert.False(view.IsTunnelStarting);
    }

    [Fact]
    public void TunnelConfigurationProgressAndFailureRemainVisible()
    {
        var loading = StartupStatusPresentation.Create(ReceiverStartupState.Running,
            DashboardConnectionMode.DevTunnel, 51839, null);
        Assert.Equal("Dev Tunnels: Loading tunnel configuration", loading.Tunnel);
        Assert.True(loading.IsTunnelStarting);
        var failed = StartupStatusPresentation.Create(ReceiverStartupState.Running,
            DashboardConnectionMode.DevTunnel, 51839, null, "Check saved tunnel identity");
        Assert.Equal("Dev Tunnels: Setup failed - Check saved tunnel identity", failed.Tunnel);
        Assert.Contains("Running", failed.Service);
        Assert.False(failed.IsTunnelStarting);
    }

    [Fact]
    public void SettingsWithoutModeUseTheInternetDefault()
    {
        var settings = DashboardSettings.FromJson("""{"Port":51820,"Compact":true,"Theme":"System"}""");
        Assert.Equal(DashboardConnectionMode.DevTunnel, settings.ConnectionMode);
        Assert.Null(settings.DevTunnelCliPath);
        Assert.Null(settings.AzureCliPath);
        Assert.True(settings.ShouldStartSharing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\Custom Azure CLI\az.exe")]
    [InlineData(@"D:\Tools\Azure\wbin\az.cmd")]
    public void AzureCliPathRoundTripsIndependentlyOfDevTunnelPath(string? path)
    {
        var settings = new DashboardSettings
        {
            AzureCliPath = path, DevTunnelCliPath = @"C:\Tools\devtunnel.exe"
        };
        var restored = DashboardSettings.FromJson(JsonSerializer.Serialize(settings));
        Assert.Equal(path, restored.AzureCliPath);
        Assert.Equal(settings.DevTunnelCliPath, restored.DevTunnelCliPath);
    }

    [Theory]
    [InlineData("az.exe")]
    [InlineData(@".\az.cmd")]
    [InlineData(@"C:\Tools\powershell.exe")]
    [InlineData("C:\\Tools\\az.exe\n")]
    public void InvalidAzureCliSettingsHaveExplicitRecoveryError(string path)
    {
        var json = JsonSerializer.Serialize(new DashboardSettings { AzureCliPath = path });
        var error = Assert.Throws<InvalidDataException>(() => DashboardSettings.FromJson(json));
        Assert.Contains("Azure CLI path", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Port":51820,"Compact":false,"Theme":"Dark"}""")]
    public void MinimizedCompactViewDefaultsOnForExistingSettings(string json)
    {
        Assert.True(new DashboardSettings().ShowCompactViewWhenMinimized);
        Assert.True(DashboardSettings.FromJson(json).ShowCompactViewWhenMinimized);
        Assert.True(DashboardSettings.RecoveryDefaults.ShowCompactViewWhenMinimized);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void MinimizedCompactPreferenceRoundTripsIndependentlyOfDashboardDensity(bool enabled, bool compact)
    {
        var settings = new DashboardSettings
        {
            ShowCompactViewWhenMinimized = enabled, Compact = compact
        };
        var restored = DashboardSettings.FromJson(JsonSerializer.Serialize(settings));
        Assert.Equal(enabled, restored.ShowCompactViewWhenMinimized);
        Assert.Equal(compact, restored.Compact);
    }

    [Fact]
    public void NewSetupsDefaultToAutomaticDevTunnelsWithoutConsent()
    {
        var settings = new DashboardSettings();
        Assert.Equal(DashboardConnectionMode.DevTunnel, settings.ConnectionMode);
        Assert.True(settings.AutoStartSharing);
        Assert.True(settings.ShouldStartSharing);
        Assert.DoesNotContain("Consent", JsonSerializer.Serialize(settings));
    }

    [Fact]
    public void ExistingInternetSettingsDefaultToAutomaticSharing()
    {
        var settings = DashboardSettings.FromJson("""{"ConnectionMode":"DevTunnel"}""");
        Assert.True(settings.ShouldStartSharing);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ModeAndStopPreferenceControlStartup(bool internet, bool enabled, bool expected)
    {
        var settings = new DashboardSettings
        {
            ConnectionMode = internet ? DashboardConnectionMode.DevTunnel : DashboardConnectionMode.Lan,
            AutoStartSharing = enabled
        };
        var restored = DashboardSettings.FromJson(JsonSerializer.Serialize(settings));
        Assert.Equal(expected, restored.ShouldStartSharing);
        Assert.Equal(enabled, restored.AutoStartSharing);
    }

    [Fact]
    public void InvalidSettingsRecoveryDoesNotExposeAPublicEndpoint()
    {
        Assert.Equal(DashboardConnectionMode.DevTunnel, DashboardSettings.RecoveryDefaults.ConnectionMode);
        Assert.False(DashboardSettings.RecoveryDefaults.ShouldStartSharing);
    }

    [Fact]
    public void ModeRoundTripsAsExplicitSetting()
    {
        var settings = new DashboardSettings { ConnectionMode = DashboardConnectionMode.DevTunnel };
        var json = JsonSerializer.Serialize(settings);
        Assert.Contains("\"ConnectionMode\":\"DevTunnel\"", json);
        Assert.Equal(json, JsonSerializer.Serialize(JsonSerializer.Deserialize<DashboardSettings>(json)));
    }

    [Fact]
    public void PublicDisplayAndClipboardMatchCanonicalBaseUrl()
    {
        var publicUrl = new Uri("https://example-51820.usw2.devtunnels.ms");
        var view = ConnectionPresentation.Create(DashboardConnectionMode.DevTunnel, true,
            "private-machine", 51820, true, publicUrl, "Connected");
        Assert.Equal(publicUrl.AbsoluteUri, view.Address);
        Assert.Equal(view.Address, view.CopyUrl);
        Assert.Equal(ConnectionPresentation.InternetWarning, view.Warning);
        var configuration = new RemoteConfiguration { MachineId = Guid.NewGuid() }.WithDashboardUrl(view.CopyUrl!);
        Assert.Equal(publicUrl.AbsoluteUri, configuration.DashboardBaseUrl);
    }

    [Theory]
    [InlineData("Starting")]
    [InlineData("Connecting")]
    [InlineData("Reconnecting")]
    [InlineData("Stopped")]
    [InlineData("SignInRequired")]
    [InlineData("Error")]
    public void UnavailablePublicStateNeverCopiesRetainedOrLanUrl(string status)
    {
        var view = ConnectionPresentation.Create(DashboardConnectionMode.DevTunnel, true,
            "private-machine", 51820, false, new Uri("https://old.devtunnels.ms"), status);
        Assert.Null(view.CopyUrl);
        Assert.DoesNotContain("private-machine", view.Address);
        Assert.Contains("unavailable", view.Address);
    }

    [Theory]
    [InlineData("http://localhost:51820")]
    [InlineData("https://host.devtunnels.ms/health")]
    [InlineData("https://host.devtunnels.ms/?token=secret")]
    [InlineData("https://user@host.devtunnels.ms/")]
    [InlineData("https://host-inspect.usw2.devtunnels.ms/")]
    [InlineData("https://localhost:51820/")]
    [InlineData("https://global.rel.tunnels.api.visualstudio.com/")]
    public void InvalidPublicBaseUrlCannotBeCopied(string url)
    {
        var view = ConnectionPresentation.Create(DashboardConnectionMode.DevTunnel, true,
            "private-machine", 51820, true, new Uri(url), "Connected");
        Assert.Null(view.CopyUrl);
    }

    [Fact]
    public void StoppedReceiverCannotCopyEvenWithVerifiedTunnel()
    {
        Assert.Null(ConnectionPresentation.Create(DashboardConnectionMode.DevTunnel, false,
            "private-machine", 51820, true, new Uri("https://host.devtunnels.ms"), "Connected").CopyUrl);
    }

    [Fact]
    public void EffectiveLanModeKeepsOriginalCopyBehavior()
    {
        var view = ConnectionPresentation.Create(DashboardConnectionMode.Lan, true,
            "private-machine", 51820, true, new Uri("https://host.devtunnels.ms"), "Connected");
        Assert.Equal("http://private-machine:51820", view.CopyUrl);
        Assert.Equal(view.Address, view.CopyUrl);
        Assert.Equal(ConnectionPresentation.LanWarning, view.Warning);
    }
}
