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
    private string? _tunnelSetupError => _runtime.Status.State.Stage == "sharing" &&
        _runtime.Status.State.Lifecycle == RuntimeLifecycle.Degraded ? "Sharing is unavailable. Check prerequisites and retry." : null;
    private string? _effectiveTunnelCliPath;
    private int _ownedTunnelPort;
    private bool _transcriptTransportSuspended;

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
            _transcriptTransportSuspended = false;
            await RunTunnelOperationAsync(RuntimeSharingOperation.Start);
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
            await RunTunnelOperationAsync(RuntimeSharingOperation.Delete, _deleteConsent?.IsChecked == true);
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
            await RunTunnelOperationAsync(RuntimeSharingOperation.Logout, _logoutConsent?.IsChecked == true);
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

    private async Task RunTunnelOperationAsync(RuntimeSharingOperation operation, bool confirmed = false)
    {
        if (_tunnel is null || _exiting || _tunnelBusy)
        {
            ShowProblem("Sharing is unavailable or another sharing operation is in progress.");
            return;
        }
        _tunnelBusy = true;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_tunnelLifetime.Token);
        _tunnelOperationCancellation = cancellation;
        _tunnelOperation = ExecuteTunnelOperationAsync(operation, confirmed, cancellation);
        UpdateTunnelControls();
        await _tunnelOperation;
    }

    private async Task ExecuteTunnelOperationAsync(RuntimeSharingOperation operation, bool confirmed, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await _runtime.SharingAsync(operation, confirmed, cancellation.Token);
            _settings = _runtime.Settings.State.Saved;
            if (!_exiting && result.Error is { } error) ShowProblem(RuntimeFailureMessage(error));
        }
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
        _runtime.CancelSharing();
        _tunnelOperationCancellation?.Cancel();
        await _tunnelOperation;
        if (_tunnel is not null && !_exiting)
            await RunTunnelOperationAsync(RuntimeSharingOperation.Stop);
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
        _runtime.CancelStartup();
        _runtime.CancelSharing();
        _tunnelLifetime.Cancel();
    }
}
