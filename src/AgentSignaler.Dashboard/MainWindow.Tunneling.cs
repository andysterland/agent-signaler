using System.ComponentModel;
using System.Net;
using System.Security;
using System.Text.Json;
using AgentSignaler.Tunneling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow
{
    private readonly CancellationTokenSource _tunnelLifetime = new();
    private CliTunnelController? _tunnel;
    private Task _tunnelOperation = Task.CompletedTask;
    private bool _tunnelBusy;
    private CancellationTokenSource? _tunnelOperationCancellation;
    private TextBlock? _tunnelDetails;
    private CheckBox? _deleteConsent;
    private CheckBox? _logoutConsent;
    private Button? _startSharing;
    private Button? _stopSharing;
    private Button? _deleteTunnel;
    private Button? _logoutTunnel;
    private string? _tunnelSetupError;
    private string? _effectiveTunnelCliPath;
    private int _ownedTunnelPort;
    private bool _transcriptTransportSuspended;

    private static string TunnelStatePath => Path.Combine(DashboardSettings.DataDirectory, "tunnel-state.json");

    private async Task InitializeTunnelAsync(CancellationToken cancellationToken)
    {
        if (_runningMode != DashboardConnectionMode.DevTunnel) return;
        try
        {
            var store = new TunnelIdentityStore(TunnelStatePath);
            var identity = await store.LoadOrCreateAsync(cancellationToken);
            if (_exiting) return;
            var cliPath = _runningCliPath ?? CliTunnelController.DefaultCliPath;
            _effectiveTunnelCliPath = cliPath;
            _tunnel = new CliTunnelController(new TunnelOptions(cliPath, _runningPort, identity),
                store.SaveAsync);
            _ownedTunnelPort = _runningPort;
            _tunnel.StatusChanged += (_, status) =>
            {
                UpdateTranscriptReadiness(status);
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_exiting)
                    {
                        UpdateConnectionPresentation();
                        ReportStartupProgress(_tunnel.Status.Message);
                    }
                });
            };
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or TunnelException)
        {
            _tunnelSetupError = "Tunnel settings could not be loaded. Check the CLI path and tunnel-state.json in the dashboard data directory. " +
                "Do not remove the state file until any saved cloud resource has been cleaned up.";
            if (!_exiting) ShowProblem(_tunnelSetupError);
        }
    }

    private ConnectionPresentation GetConnectionPresentation() =>
        ConnectionPresentation.Create(_runningMode, _running, Dns.GetHostName(), _runningPort,
            _tunnel?.Status.CanCopy == true, _tunnel?.Status.PublicUrl,
            _tunnel?.Status.Message ?? _tunnelSetupError ?? "Sharing is stopped");

    private void UpdateConnectionPresentation()
    {
        UpdateTranscriptReadiness(_tunnel?.Status);
        var view = GetConnectionPresentation();
        _host.Text = view.CopyUrl ?? "";
        _copyUrl.IsEnabled = view.CopyUrl is not null;
        _copyUrl.Visibility = view.CopyUrl is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateTunnelControls();
    }

    private void UpdateTranscriptReadiness(TunnelStatus? status) =>
        _server?.SetTranscriptReadiness(TranscriptReceiverPolicy.IsReady(_runningMode, _running,
            _runningPort, _ownedTunnelPort, status?.CanCopy == true, _exiting,
            _transcriptConnectionChanged || _transcriptTransportSuspended));

    private string TranscriptReceiverDescription()
    {
        var capabilities = _server?.Transcripts.GetCapabilities();
        if (capabilities is null) return "Compatible transcript receiver unavailable.";
        if (!capabilities.Enabled) return "Receive detailed conversations is disabled. Status reporting is unchanged.";
        if (!capabilities.Ready) return "Details unavailable: HTTPS with this listener's owned running Dev Tunnel is required. LAN remains status-only.";
        return "Receiving transient detailed observations over the owned HTTPS Dev Tunnel. Receiver fields: user and available assistant text, " +
            "tool names/correlation and observed lifecycle. " + TranscriptViewController.ProductionCaptureNotice + " " +
            "Client sharing is last-observed, not remotely acknowledged here.";
    }

    private void SuspendTranscriptTransport()
    {
        _transcriptTransportSuspended = true;
        _server?.SetTranscriptReadiness(false);
    }

    private async Task StartSharingOnStartupAsync()
    {
        if (_exiting || !_settings.ShouldStartSharing || _runningMode != DashboardConnectionMode.DevTunnel || _tunnel is null)
            return;
        ReportStartupProgress("Starting the shared public endpoint...");
        await RunTunnelOperationAsync(token => _tunnel.StartAsync(token));
        if (!_exiting && _settings.ShouldStartSharing && !_tunnel.Status.CanCopy)
            ShowProblem("Automatic Internet sharing could not start. " + _tunnel.Status.Message +
                " Resolve the issue and select Enable sharing / Retry. The local receiver remains available.");
    }

    private bool SaveAutomaticSharing(bool enabled)
    {
        var next = _settings with { AutoStartSharing = enabled };
        if (!enabled)
        {
            _settings = next;
        }
        try
        {
            next.Save();
            _settings = next;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            ShowProblem(enabled
                ? "Automatic sharing could not be saved. Check dashboard settings write access before enabling sharing."
                : "Automatic restart could not be disabled on disk. Stopping sharing is still being attempted. " +
                    "Restore dashboard settings write access and stop sharing again before restarting the application.");
            return false;
        }
    }

    private void BuildTunnelSettings(StackPanel panel, StackPanel help)
    {
        panel.Children.Add(Text("Internet sharing", 18));
        help.Children.Add(Text(ConnectionPresentation.InternetWarning));
        help.Children.Add(Text("For the Dev Tunnels CLI path, installation/account checks, and setup guidance, use Settings > Prerequisite."));
        help.Children.Add(Text("Internet sharing starts automatically using your existing CLI sign-in. No additional consent is required. " +
            "The application never opens sign-in automatically. This preview service is intended for development/testing and has no SLA."));
        _tunnelDetails = Text("");
        panel.Children.Add(_tunnelDetails);
        _startSharing = new Button { Content = "Enable sharing / Retry" };
        _startSharing.Click += async (_, _) =>
        {
            if (!SaveAutomaticSharing(true)) return;
            _transcriptTransportSuspended = false;
            await RunTunnelOperationAsync(token => _tunnel!.StartAsync(token));
        };
        panel.Children.Add(_startSharing);
        _stopSharing = new Button { Content = "Stop sharing / Cancel" };
        _stopSharing.Click += async (_, _) => await StopSharingAsync();
        panel.Children.Add(_stopSharing);
        _deleteConsent = new CheckBox { Content = "Confirm deletion: remote clients using this URL will stop working." };
        _deleteConsent.Checked += (_, _) => UpdateTunnelControls();
        _deleteConsent.Unchecked += (_, _) => UpdateTunnelControls();
        panel.Children.Add(_deleteConsent);
        _deleteTunnel = new Button { Content = "Delete tunnel" };
        _deleteTunnel.Click += async (_, _) =>
        {
            SuspendTranscriptTransport();
            SaveAutomaticSharing(false);
            await RunTunnelOperationAsync(token => _tunnel!.DeleteAsync(token));
        };
        panel.Children.Add(_deleteTunnel);
        _logoutConsent = new CheckBox { Content = "Confirm CLI sign-out: this may affect my other devtunnel sessions." };
        _logoutConsent.Checked += (_, _) => UpdateTunnelControls();
        _logoutConsent.Unchecked += (_, _) => UpdateTunnelControls();
        panel.Children.Add(_logoutConsent);
        _logoutTunnel = new Button { Content = "Stop sharing and sign out" };
        _logoutTunnel.Click += async (_, _) =>
        {
            SuspendTranscriptTransport();
            SaveAutomaticSharing(false);
            await RunTunnelOperationAsync(token => _tunnel!.LogoutAsync(token));
        };
        panel.Children.Add(_logoutTunnel);
        UpdateTunnelControls();
    }

    private void UpdateTunnelControls()
    {
        var ready = !_exiting && _running && _runningMode == DashboardConnectionMode.DevTunnel && _tunnel is not null;
        var busy = _tunnelBusy;
        if (_tunnelDetails is not null)
            _tunnelDetails.Text = _runningMode == DashboardConnectionMode.Lan
                ? "Save Internet mode and restart before using sharing controls. The effective listener is still LAN."
                : _tunnelSetupError ?? _tunnel?.Status.Message ?? "Sharing is unavailable.";
        if (_startSharing is not null)
            _startSharing.IsEnabled = ready && !busy && _tunnel?.Status.CanCopy != true;
        if (_stopSharing is not null) _stopSharing.IsEnabled = ready;
        if (_deleteTunnel is not null) _deleteTunnel.IsEnabled = ready && !busy && _deleteConsent?.IsChecked == true;
        if (_logoutTunnel is not null) _logoutTunnel.IsEnabled = ready && !busy && _logoutConsent?.IsChecked == true;
    }

    private async Task RunTunnelOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_tunnel is null || _exiting || _tunnelBusy)
        {
            ShowProblem("Sharing is unavailable or another sharing operation is in progress.");
            return;
        }
        _tunnelBusy = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_tunnelLifetime.Token);
        _tunnelOperationCancellation = cancellation;
        _tunnelOperation = ExecuteTunnelOperationAsync(operation, cancellation);
        UpdateTunnelControls();
        await _tunnelOperation;
    }

    private async Task ExecuteTunnelOperationAsync(Func<CancellationToken, Task> operation, CancellationTokenSource cancellation)
    {
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_exiting) ShowProblem("Sharing operation cancelled. Check sharing status before retrying.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidOperationException or TunnelException or TimeoutException)
        {
            if (!_exiting) ShowProblem(_tunnel?.Status.Message ??
                "Sharing could not complete. Check the CLI installation, sign-in, network, and saved tunnel identity.");
        }
        finally
        {
            _tunnelOperationCancellation = null;
            cancellation.Dispose();
            _tunnelBusy = false;
            if (!_exiting) UpdateConnectionPresentation();
        }
    }

    private async Task StopSharingAsync()
    {
        SuspendTranscriptTransport();
        SaveAutomaticSharing(false);
        _tunnelOperationCancellation?.Cancel();
        await _tunnelOperation;
        if (_tunnel is not null && !_exiting)
            await RunTunnelOperationAsync(token => _tunnel.StopAsync(token));
    }

    private void ReleaseTunnelSettings()
    {
        _tunnelDetails = null;
        _deleteConsent = _logoutConsent = null;
        _startSharing = _stopSharing = _deleteTunnel = _logoutTunnel = null;
    }

    private void CancelTunnelOperations()
    {
        SuspendTranscriptTransport();
        _tunnelLifetime.Cancel();
    }

    private async Task ShutdownTunnelAsync()
    {
        try
        {
            await _tunnelOperation;
            if (_tunnel is not null) await _tunnel.DisposeAsync();
        }
        finally { _tunnelLifetime.Dispose(); }
    }
}
