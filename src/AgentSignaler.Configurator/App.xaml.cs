using Microsoft.UI.Xaml;
using AgentSignaler.AppUpdates;

namespace AgentSignaler.Configurator;

public partial class App : Application
{
    private Window? window;
    private readonly AppUpdateHost updates;
    internal App(AppUpdateHost updates) { this.updates = updates; InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow(updates);
        window.Activate();
    }
}
