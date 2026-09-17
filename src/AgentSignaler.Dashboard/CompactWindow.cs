using AgentSignaler.Service;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace AgentSignaler.Dashboard;

internal sealed class CompactWindow : Window
{
    internal const int TileSize = 64;
    private readonly StackPanel _tiles = new();
    private readonly Dictionary<Guid, CompactTile> _cards = [];
    private readonly Action _restore;
    private readonly Func<Guid, Task> _connect;
    private readonly Func<Guid, bool> _isBusy;
    private readonly Func<Task> _exit;
    private readonly nint _handle;
    private readonly CompactWindowSizing _sizing;
    private bool _allowClose;

    public CompactWindow(Action restore, Func<Guid, Task> connect, Func<Guid, bool> isBusy, Func<Task> exit)
    {
        _restore = restore;
        _connect = connect;
        _isBusy = isBusy;
        _exit = exit;
        Title = "Agent Signaler compact view";
        SystemBackdrop = new MicaBackdrop();
        Content = new ScrollViewer
        {
            Content = _tiles,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 17, 24, 39)),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _sizing = new CompactWindowSizing(_handle);
        Closed += (_, _) =>
        {
            foreach (var tile in _cards.Values) tile.Hover.Dispose();
            _sizing.Dispose();
        };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMinimizable = false;
        presenter.IsMaximizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Closing += (_, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            _restore();
        };
    }

    public void CloseForExit()
    {
        _allowClose = true;
        Hide();
        Close();
    }

    public void Hide()
    {
        foreach (var tile in _cards.Values) tile.Hover.Hide();
        AppWindow.Hide();
    }

    public void Update(IEnumerable<MachineView> machines, ElementTheme theme, WindowId dashboardId)
    {
        _tiles.RequestedTheme = theme;
        var ordered = MachineNavigation.Order(machines, machine => machine).ToArray();
        var ids = ordered.Select(machine => machine.MachineId).ToHashSet();
        foreach (var id in _cards.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _cards[id].Hover.Dispose();
            _tiles.Children.Remove(_cards[id].Container);
            _cards.Remove(id);
        }
        for (var i = 0; i < ordered.Length; i++)
        {
            var machine = ordered[i];
            if (!_cards.TryGetValue(machine.MachineId, out var tile))
            {
                tile = CreateTile(machine);
                _cards.Add(machine.MachineId, tile);
            }
            tile.Card.Update(machine);
            tile.Connect.Text = MachineNavigation.IsLocal(machine) ? "Return to local" : "Connect";
            UpdateConnectionAvailability(machine.MachineId, tile);
            if (_tiles.Children.Count <= i || _tiles.Children[i] != tile.Container)
            {
                _tiles.Children.Remove(tile.Container);
                _tiles.Children.Insert(i, tile.Container);
            }

        }

        var workArea = DisplayArea.GetFromWindowId(dashboardId, DisplayAreaFallback.Primary).WorkArea;
        // Move onto the target monitor before measuring its DPI.
        if (!AppWindow.IsVisible) AppWindow.Move(new PointInt32(workArea.X, workArea.Y));
        var scale = NativeWindow.GetDpiForWindow(_handle) / 96.0;
        var width = (int)Math.Round(TileSize * scale);
        var frameHeight = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var height = Math.Min((int)Math.Round(ordered.Length * TileSize * scale),
            Math.Max(1, workArea.Height - frameHeight));
        var size = new SizeInt32(width, height);
        if (AppWindow.ClientSize != size) AppWindow.ResizeClient(size);
        var position = new PointInt32(workArea.X + workArea.Width - AppWindow.Size.Width, workArea.Y);
        if (AppWindow.Position != position) AppWindow.Move(position);
    }

    public void UpdateConnectionAvailability()
    {
        foreach (var (id, tile) in _cards) UpdateConnectionAvailability(id, tile);
    }

    private void UpdateConnectionAvailability(Guid id, CompactTile tile)
    {
        tile.Card.Button.IsEnabled = tile.Connect.IsEnabled = !_isBusy(id);
    }

    private CompactTile CreateTile(MachineView machine)
    {
        var id = machine.MachineId;
        var card = new MachineCard(machine, async () => await _connect(id), miniature: true);
        var menu = new MenuFlyout();
        var connect = new MenuFlyoutItem { Text = "Connect" };
        connect.Click += async (_, _) => await _connect(id);
        var restore = new MenuFlyoutItem { Text = "Show full dashboard" };
        restore.Click += (_, _) => _restore();
        var exit = new MenuFlyoutItem { Text = "Exit" };
        exit.Click += async (_, _) => await _exit();
        menu.Items.Add(connect);
        menu.Items.Add(restore);
        menu.Items.Add(exit);

        // Keep window actions accessible even when the connection button is disabled.
        var container = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Child = card.Button,
            ContextFlyout = menu
        };
        var hover = new CompactHoverPreview(container, card.HoverPreview
            ?? throw new InvalidOperationException("The compact machine preview is unavailable."));
        return new CompactTile(card, container, connect, hover);
    }

    private sealed record CompactTile(MachineCard Card, Border Container, MenuFlyoutItem Connect, CompactHoverPreview Hover);
}
