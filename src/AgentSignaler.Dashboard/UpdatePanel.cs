using AgentSignaler.Updates;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentSignaler.AppUpdates;

// Linked into both WinUI applications. Owns presentation only; discovery and servicing
// remain in the Updates library and the application's lifecycle adapter.
internal sealed class UpdatePanel : IAsyncDisposable
{
    private readonly UpdateMonitor monitor;
    private readonly DispatcherQueue dispatcher;
    private readonly Func<ReleaseCandidate, CancellationToken, Task<string?>> install;
    private readonly UpdateEligibilityWarning? startupWarning;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Button update = new() { Content = "Update now" };
    private readonly Button later = new() { Content = "Later" };
    private readonly HyperlinkButton details = new() { Content = "Release details" };
    private readonly Button check = new() { Content = "Check for updates" };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Task recoveryObservation;
    private long? dismissed;
    private bool busy;
    private bool conflicting;
    private bool disposed;

    public InfoBar Banner { get; } = new()
    {
        Title = "Software updates", IsClosable = false,
        Severity = InfoBarSeverity.Informational
    };
    public StackPanel Settings { get; } = new() { Spacing = 8 };
    public InfoBar RecoveryBanner { get; } = new()
    {
        Title = "Retained update results", IsClosable = true, Severity = InfoBarSeverity.Warning
    };

    public UpdatePanel(UpdatePackage package, DispatcherQueue dispatcher,
        Func<ReleaseCandidate, CancellationToken, Task<string?>> install,
        Func<CancellationToken, Task<IReadOnlyList<UpdateRecoveryNotice>>> readRecovery,
        UpdateEligibilityWarning? startupWarning = null)
    {
        this.dispatcher = dispatcher;
        this.install = install;
        this.startupWarning = startupWarning;
        monitor = UpdateMonitor.CreateDefault(package, AppContext.BaseDirectory);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(update);
        actions.Children.Add(later);
        actions.Children.Add(details);
        Banner.Content = actions;
        AutomationProperties.SetLiveSetting(Banner, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(RecoveryBanner, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        Settings.Children.Add(check);
        Settings.Children.Add(status);
        later.Click += (_, _) =>
        {
            if (this.startupWarning is not null) return;
            dismissed = monitor.State.Candidate?.Id;
            Banner.IsOpen = false;
        };
        check.Click += async (_, _) => await CheckAsync();
        update.Click += async (_, _) => await InstallAsync();
        monitor.Changed += Changed;
        Render(monitor.State);
        recoveryObservation = ObserveRecoveryAsync(readRecovery);
        monitor.Start();
    }

    private async Task ObserveRecoveryAsync(Func<CancellationToken, Task<IReadOnlyList<UpdateRecoveryNotice>>> readRecovery)
    {
        try
        {
            var notices = await readRecovery(lifetime.Token).ConfigureAwait(false);
            dispatcher.TryEnqueue(() =>
            {
                if (disposed || notices.Count == 0) return;
                var entries = new StackPanel { Spacing = 10 };
                foreach (var notice in notices)
                    entries.Children.Add(new TextBlock { Text = notice.Message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                entries.Children.Add(RecoveryInstructions());
                RecoveryBanner.Content = new ScrollViewer
                {
                    Content = entries, MaxHeight = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch
                };
                RecoveryBanner.Message = "All retained unresolved or non-successful operations are shown below. No chronological order is implied. Observing these results does not clear servicing state or retry an update.";
                RecoveryBanner.IsOpen = true;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or
            UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            dispatcher.TryEnqueue(() =>
            {
                if (disposed) return;
                RecoveryBanner.Message = "Recovery information could not be verified (update_recovery_unavailable). Do not delete maintenance state or retry an unknown installer. Review updater recovery instructions.";
                RecoveryBanner.Content = RecoveryInstructions();
                RecoveryBanner.IsOpen = true;
            });
        }
    }

    private static HyperlinkButton RecoveryInstructions() => new()
    {
        Content = "Update and recovery instructions",
        NavigateUri = new Uri("https://github.com/andysterland/agent-signaler/blob/main/docs/user-guide.md#application-updates")
    };

    public void SetConflicting(bool value)
    {
        conflicting = value;
        if (!disposed) Render(monitor.State);
    }

    private void Changed(UpdateCheckResult state) => dispatcher.TryEnqueue(() =>
    {
        if (!disposed) Render(state);
    });

    private void Render(UpdateCheckResult state)
    {
        var checking = state.Status == UpdateCheckStatus.Checking;
        var presentation = UpdatePanelPresentation.Create(state, startupWarning, dismissed);
        var candidate = presentation.Candidate;
        var description = presentation.Message;
        status.Text = description + (state.CheckedAt is { } checkedAt ? $" Last check: {checkedAt.ToLocalTime():g}." : "")
            + (state.NextCheckAt is { } next ? $" Next check: {next.ToLocalTime():g}." : "")
            + (state.FromCache ? " Cached result." : "");
        check.IsEnabled = !busy && !checking && !conflicting;
        update.IsEnabled = presentation.CanUpdate && !busy && !checking && !conflicting;
        later.IsEnabled = !busy && !presentation.IsWarning;
        details.IsEnabled = !busy && !presentation.IsWarning;
        later.Visibility = details.Visibility = presentation.IsWarning ? Visibility.Collapsed : Visibility.Visible;
        if (busy) return;
        Banner.Message = description;
        Banner.Severity = presentation.IsWarning ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Banner.IsOpen = presentation.IsOpen;
        if (candidate is not null)
            details.NavigateUri = new Uri("https://github.com/andysterland/agent-signaler/releases/tag/" + Uri.EscapeDataString(candidate.Tag));
    }

    private async Task CheckAsync()
    {
        if (busy || conflicting || disposed) return;
        dismissed = null;
        busy = true;
        Render(monitor.State);
        var failed = false;
        try { await monitor.CheckAsync(true, lifetime.Token); }
        catch (OperationCanceledException) { failed = !lifetime.IsCancellationRequested; }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or UpdateDiscoveryException) { failed = true; }
        finally
        {
            busy = false;
            if (!disposed)
            {
                Render(monitor.State);
                if (failed)
                {
                    status.Text = "The update check could not be verified (update_check_failed). Try again later.";
                    check.IsEnabled = !conflicting;
                }
            }
        }
    }

    private async Task InstallAsync()
    {
        if (busy || conflicting || disposed ||
            !UpdatePanelPresentation.Create(monitor.State, startupWarning, dismissed).CanUpdate ||
            monitor.State.Candidate is not { } candidate) return;
        busy = true;
        Render(monitor.State);
        Banner.IsOpen = true;
        Banner.Message = "Preparing update. Waiting for affected applications before restarting…";
        try
        {
            var task = install(candidate, lifetime.Token);
            var message = await task;
            if (!disposed && message is not null)
            {
                Banner.Message = message;
                Banner.Severity = InfoBarSeverity.Warning;
            }
        }
        catch (OperationCanceledException)
        {
            if (!disposed)
            {
                Banner.Message = "Update preparation was cancelled or timed out. The application is still running. Resolve any active workflow and retry.";
                Banner.Severity = InfoBarSeverity.Warning;
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or UpdateDiscoveryException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.SecurityException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            if (!disposed)
            {
                Banner.Message = "The update could not be prepared (update_preflight_failed). The application is still running. Close conflicting workflows and try again, or use the signed MSI update instructions.";
                Banner.Severity = InfoBarSeverity.Warning;
            }
        }
        finally
        {
            busy = false;
            if (!disposed)
            {
                check.IsEnabled = !conflicting;
                var presentation = UpdatePanelPresentation.Create(monitor.State, startupWarning, dismissed);
                update.IsEnabled = !conflicting && presentation.CanUpdate;
                later.IsEnabled = details.IsEnabled = !presentation.IsWarning;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        monitor.Changed -= Changed;
        lifetime.Cancel();
        await monitor.DisposeAsync();
        await recoveryObservation;
        // The handoff commit may itself be awaiting application shutdown; do not
        // await that operation here and create a shutdown/commit cycle.
        lifetime.Dispose();
    }
}
