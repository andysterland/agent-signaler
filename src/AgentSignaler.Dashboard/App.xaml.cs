using Microsoft.UI.Xaml;
using AgentSignaler.AppUpdates;

namespace AgentSignaler.Dashboard;

public partial class App : Application
{
    private readonly SingleInstance _instance;
    private readonly DashboardResourceLease _resourceLease;
    private readonly bool _background;
    private readonly AppUpdateHost _updates;
    private MainWindow? _window;

    internal App(SingleInstance instance, DashboardResourceLease resourceLease, AppUpdateHost updates, bool background)
    {
        _instance = instance;
        _resourceLease = resourceLease;
        _background = background;
        _updates = updates;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            e.Handled = false;
            NativeWindow.ShowError("An unexpected application error occurred and Agent Signaler will close. " +
                "Restart the app. If the problem persists, check the application installation.");
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(_resourceLease, _updates);
        _window.Activate();
        _instance.Listen(_window.DispatcherQueue, _window.ShowDashboard,
            async () => await _window.ExitAsync(),
            () => _window.ShowProblem("Second-instance activation is unavailable. Use the notification-area icon " +
                "to show the dashboard, then restart Agent Signaler to restore activation."));
        await _window.InitializeAsync(_background);
    }
}
