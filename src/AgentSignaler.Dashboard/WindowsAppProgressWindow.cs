using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AgentSignaler.Dashboard;

internal sealed class WindowsAppProgressWindow : Window, IDisposable
{
    private readonly WindowsAppConnectionController _controller;
    private readonly Guid _machineId;
    private readonly TextBlock _status = new()
    {
        Text = "Reading the saved Dev Box connection...",
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button _cancel = new() { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
    private bool _closed;
    private bool _cancelling;

    public WindowsAppProgressWindow(WindowsAppConnectionController controller, Guid machineId,
        string machineName, ElementTheme theme, WindowId ownerId)
    {
        _controller = controller;
        _machineId = machineId;
        Title = "Open in Windows App";
        SystemBackdrop = new MicaBackdrop();
        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24), RequestedTheme = theme };
        panel.Children.Add(new TextBlock
        {
            Text = WindowsAppConnectionController.LaunchLabel(machineName),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 20
        });
        var progress = new ProgressBar { IsIndeterminate = true };
        AutomationProperties.SetName(progress, "Windows App connection in progress");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        panel.Children.Add(progress);
        panel.Children.Add(_status);
        panel.Children.Add(_cancel);
        Content = new ScrollViewer { Content = panel };
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        var workArea = DisplayArea.GetFromWindowId(ownerId, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        var scale = NativeWindow.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)Math.Round(440 * scale), (int)Math.Round(260 * scale)));
        AppWindow.Move(new PointInt32(
            workArea.X + Math.Max(0, (workArea.Width - AppWindow.Size.Width) / 2),
            workArea.Y + Math.Max(0, (workArea.Height - AppWindow.Size.Height) / 2)));
        _cancel.Click += (_, _) => Cancel();
        AppWindow.Closing += (_, args) =>
        {
            if (_closed) return;
            args.Cancel = true;
            Cancel();
        };
        _controller.Changed += OnConnectionChanged;
    }

    private void OnConnectionChanged(Guid id)
    {
        if (id != _machineId) return;
        var message = _controller.State(id).Message;
        void Update()
        {
            if (!_closed && !_cancelling) _status.Text = message;
        }
        if (DispatcherQueue.HasThreadAccess) Update();
        else DispatcherQueue.TryEnqueue(Update);
    }

    private void Cancel()
    {
        _cancelling = true;
        _cancel.IsEnabled = false;
        _status.Text = "Cancelling the connection operation...";
        _controller.Cancel(_machineId);
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _controller.Changed -= OnConnectionChanged;
        AppWindow.Hide();
        Close();
    }
}
