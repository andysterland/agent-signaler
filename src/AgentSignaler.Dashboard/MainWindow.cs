using System.Net;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using AgentSignaler.Contracts;
using AgentSignaler.Service;
using AgentSignaler.Tunneling;
using Microsoft.UI;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI;

namespace AgentSignaler.Dashboard;

internal sealed partial class MainWindow : Window
{
    private const string PollErrorMessage = "Machine status could not be refreshed. Retrying every second. Displayed cards may be stale. " +
        "Check that the local database is accessible; restart Agent Signaler if this persists.";
    private const string ConfiguratorTestMessage = "A test connection was received from AgentSignaler.Configurator.";
    private readonly Grid _root = new() { Padding = new Thickness(32, 16, 32, 32), RowSpacing = 16 };
    private readonly Grid _cards = new()
    {
        ColumnSpacing = MachineCardPresentation.CardSpacing, RowSpacing = MachineCardPresentation.CardSpacing
    };
    private readonly Grid _machineBody = new() { Visibility = Visibility.Collapsed };
    private readonly DashboardStartupController _startup = new();
    private readonly TextBlock _machineCount = Text("No development machines", 18);
    private readonly Button _settingsButton = new();
    private readonly Button _compactButton = new()
    {
        IsEnabled = false, Padding = new Thickness(20, 10, 20, 10),
        CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _empty = Text("Waiting for machines", 20);
    private readonly TextBlock _host = Text("");
    private readonly ProgressBar _startupProgress = new() { IsIndeterminate = true };
    private readonly TextBlock _startupStatus = Text("Starting Dashboard...");
    private readonly StackPanel _startupPanel = new() { Spacing = 8 };
    private readonly Button _copyUrl = new()
    {
        Content = new SymbolIcon(Symbol.Copy), IsEnabled = false, Visibility = Visibility.Collapsed,
        Width = 32, Height = 32, Padding = new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly InfoBar _problem = new() { IsClosable = true, Severity = InfoBarSeverity.Error };
    private readonly InfoBar _connectionTest = new()
    {
        IsClosable = true, Severity = InfoBarSeverity.Informational,
        Title = "Test connection", Message = ConfiguratorTestMessage
    };
    private readonly Dictionary<Guid, MachineCard> _cardMap = [];
    private readonly DispatcherQueueTimer _timer;
    private readonly nint _handle;
    private DashboardSettings _settings = new();
    private MachineStore? _store;
    private DashboardServer? _server;
    private TrayIcon? _tray;
    private WindowStateMonitor? _windowStateMonitor;
    private CompactWindow? _compactWindow;
    private bool _minimized;
    private WindowsAppConnectionController? _connections;
    private WindowsAppConnectionActions? _connectionActions;
    private ContentDialog? _activeDialog;
    private ContentDialog? _machineDetailsDialog;
    private bool _closeDialogForNavigation;
    private TaskCompletionSource? _dialogFinished;
    private Action? _updateConnectionControls;
    private Task _refreshTask = Task.CompletedTask;
    private Task _mutationTask = Task.CompletedTask;
    private Task? _initializationTask;
    private Task? _shutdownTask;
    private bool _running;
    private bool _exiting;
    private bool _allowClose;
    private bool _dialogOpen;
    private bool _pollErrorShown;
    private int _runningPort;
    private DashboardConnectionMode _runningMode;
    private string? _runningCliPath;
    private string? _runningAzureCliPath;
    private string? _effectiveAzureCliPath;
    private Guid? _detailsId;
    private Action<MachineView?>? _updateDetails;
    private static string AppVersion =>
        typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public MainWindow()
    {
        Title = $"Agent Signaler v{AppVersion}";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "AgentSignaler.ico"));
        SystemBackdrop = new MicaBackdrop();
        Content = _root;
        _handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        BuildLayout();
        try { _settings = DashboardSettings.Load(); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _settings = DashboardSettings.RecoveryDefaults;
            ShowProblem("Settings could not be read. Defaults are in use with automatic sharing disabled. Check dashboard-settings.json in " +
                "%LOCALAPPDATA%\\AgentSignaler; saving settings will replace that file.");
        }
        ApplyAppearance();
        try
        {
            _windowStateMonitor = new WindowStateMonitor(_handle,
                () => DispatcherQueue.TryEnqueue(HandleWindowStateChange));
        }
        catch (Win32Exception)
        {
            ShowProblem("Minimized window monitoring is unavailable. Minimize will use the taskbar instead of the compact view. Restart Agent Signaler to retry.");
        }
        AppWindow.Closing += async (sender, args) =>
        {
            if (_allowClose) return;
            args.Cancel = true;
            if (_tray is { IsAvailable: true })
            {
                _minimized = false;
                _compactWindow?.AppWindow.Hide();
                AppWindow.Hide();
            }
            else await ExitAsync();
        };
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += async (_, _) =>
        {
            if (!_exiting && _refreshTask.IsCompleted)
            {
                _refreshTask = RefreshAsync();
                await _refreshTask;
            }
        };
    }

    private void BuildLayout()
    {
        for (var i = 0; i < 4; i++) _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 0, 0, 12) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        heading.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var brand = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var title = Text("Agent Signaler", 34);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        brand.Children.Add(title);
        _machineCount.Visibility = Visibility.Collapsed;
        brand.Children.Add(_machineCount);
        heading.Children.Add(brand);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        _compactButton.Content = ActionContent(Symbol.View, "Compact View");
        AutomationProperties.SetName(_compactButton, "Compact View");
        _compactButton.Click += (_, _) =>
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
        };
        ToolTipService.SetToolTip(_compactButton, "Show glyph-only status tiles. Enable 'Compact view when minimized' in Settings.");
        actions.Children.Add(_compactButton);
        var settings = _settingsButton;
        settings.Content = ActionContent(Symbol.Setting, "Settings");
        AutomationProperties.SetName(settings, "Settings");
        settings.VerticalAlignment = VerticalAlignment.Center;
        settings.Padding = new Thickness(20, 10, 20, 10);
        settings.CornerRadius = new CornerRadius(10);
        settings.Background = new SolidColorBrush(Color.FromArgb(255, 37, 99, 235));
        settings.Foreground = new SolidColorBrush(Colors.White);
        settings.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 29, 78, 216));
        settings.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 30, 64, 175));
        settings.Resources["ButtonForegroundPointerOver"] = settings.Foreground;
        settings.Resources["ButtonForegroundPressed"] = settings.Foreground;
        settings.Click += async (_, _) => await ShowSettingsAsync();
        actions.Children.Add(settings);
        Grid.SetColumn(actions, 1);
        heading.Children.Add(actions);
        heading.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 720;
            Grid.SetColumnSpan(brand, narrow ? 2 : 1);
            Grid.SetRow(actions, narrow ? 1 : 0);
            Grid.SetColumn(actions, narrow ? 0 : 1);
            Grid.SetColumnSpan(actions, narrow ? 2 : 1);
            heading.RowSpacing = narrow ? 12 : 0;
        };
        AddRow(heading, 0);
        _root.ActualThemeChanged += (_, _) => UpdateCanvasAppearance();
        UpdateCanvasAppearance();

        // Shrink to the URL for adjacent copying, but constrain long URLs to the brand column.
        var address = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        address.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _host.IsTextSelectionEnabled = true;
        _host.TextWrapping = TextWrapping.NoWrap;
        _host.TextTrimming = TextTrimming.CharacterEllipsis;
        _host.VerticalAlignment = VerticalAlignment.Center;
        address.Children.Add(_host);
        AutomationProperties.SetName(_copyUrl, "Copy effective dashboard connection URL");
        ToolTipService.SetToolTip(_copyUrl, "Copy URL");
        _copyUrl.Click += (_, _) =>
        {
            var presentation = GetConnectionPresentation();
            if (presentation.CopyUrl is not { } copyUrl)
            {
                ShowProblem("The connection URL is unavailable. Start the receiver and verify sharing before copying the public URL.");
                return;
            }
            try
            {
                var package = new DataPackage();
                package.SetText(copyUrl);
                Clipboard.SetContent(package);
                Clipboard.Flush();
            }
            catch (Exception error) when (error is COMException or UnauthorizedAccessException)
            {
                ShowProblem("Windows could not access the clipboard. Select the host URL and copy it manually.");
            }
        };
        Grid.SetColumn(_copyUrl, 1);
        address.Children.Add(_copyUrl);
        brand.Children.Add(address);
        AutomationProperties.SetName(_startupProgress, "Dashboard startup in progress");
        _startupPanel.Children.Add(_startupProgress);
        _startupPanel.Children.Add(_startupStatus);
        AddRow(_startupPanel, 1);
        AddRow(_problem, 2);
        AddRow(_connectionTest, 3);

        var body = _machineBody;
        var scroll = new ScrollViewer
        {
            Content = _cards,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        scroll.SizeChanged += (_, _) => LayoutCards(Math.Max(1, scroll.ActualWidth - 16));
        body.Children.Add(scroll);
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.Text = "Waiting for machines\nConfigure a remote client with the URL above.";
        _empty.TextAlignment = TextAlignment.Center;
        body.Children.Add(_empty);
        AddRow(body, 4);
    }

    private static StackPanel ActionContent(Symbol symbol, string label)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new SymbolIcon(symbol) { VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(Text(label));
        return content;
    }

    public Task InitializeAsync(bool background) => _initializationTask ??= InitializeWithProgressAsync(background);

    private async Task InitializeWithProgressAsync(bool background)
    {
        try
        {
            await _startup.RunAsync(InitializeCoreAsync, _tunnelLifetime.Token);
        }
        finally
        {
            if (!_exiting) UpdateStartupPresentation();
        }
        if (!_exiting && background && _tray is { IsAvailable: true } && !_problem.IsOpen) AppWindow.Hide();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        ReportStartupProgress("Setting up the notification-area icon...");
        try
        {
            _tray = new TrayIcon(_handle, ShowDashboard, async () => await ExitAsync());
        }
        catch (Win32Exception)
        {
            ShowProblem("The notification-area icon is unavailable. Closing this window will exit Agent Signaler. " +
                "Restart Explorer or sign in again to restore the notification area.");
        }
        try
        {
            ReportStartupProgress("Loading local data and Dev Box connections...");
            _runningPort = _settings.Port;
            _runningMode = _settings.ConnectionMode;
            _runningCliPath = _settings.DevTunnelCliPath;
            _runningAzureCliPath = _settings.AzureCliPath;
            Directory.CreateDirectory(DashboardSettings.DataDirectory);
            _store = new MachineStore(DashboardSettings.DatabasePath);
            _effectiveAzureCliPath = AzureCliInstallation.ResolvePath(_runningAzureCliPath);
            var cli = new AzureCliProcess(executablePath: _effectiveAzureCliPath);
            var resolver = new DevBoxConnectionResolver(cli);
            _catalog = new DevBoxCatalogController(new DevBoxCatalogService(cli));
            _catalog.Changed += () =>
            {
                void UpdateCatalogAvailability()
                {
                    if (_exiting) return;
                    _updateDevBoxSettingsControls?.Invoke();
                    _updateConnectionControls?.Invoke();
                }
                if (DispatcherQueue.HasThreadAccess) UpdateCatalogAvailability();
                else DispatcherQueue.TryEnqueue(UpdateCatalogAvailability);
            };
            var gate = WindowsAppOperationGate.Shared;
            var launcher = new WindowsAppLauncher(resolver, new WindowsAppPlatform(), _store.SetWindowsAppConnectionAsync, gate);
            _connections = new WindowsAppConnectionController(async (id, token) =>
            {
                var machine = (await _store.GetMachinesAsync(token)).FirstOrDefault(machine => machine.MachineId == id)
                    ?? throw new WindowsAppConnectionException(WindowsAppFailure.InvalidMapping);
                return machine.WindowsAppConnection;
            }, _store.SetWindowsAppConnectionAsync, _store.ClearWindowsAppConnectionAsync, cli, resolver, launcher, gate,
                () => _catalog?.State.IsBusy == true);
            void UpdateConnectionAvailability()
            {
                if (_exiting) return;
                _updateConnectionControls?.Invoke();
                _compactWindow?.UpdateConnectionAvailability();
            }
            _connections.Changed += _ =>
            {
                if (DispatcherQueue.HasThreadAccess) UpdateConnectionAvailability();
                else DispatcherQueue.TryEnqueue(UpdateConnectionAvailability);
            };
            _connectionActions = new WindowsAppConnectionActions(_connections, RefreshAsync, () => _exiting,
                ShowDashboard, ShowProblem, ShowConnectionDetailsAsync, id =>
                {
                    var progress = new WindowsAppProgressWindow(_connections, id,
                        _cardMap.TryGetValue(id, out var card) ? card.Machine.Name : "Dev Box",
                        _root.RequestedTheme, _compactWindow?.AppWindow.Id ?? AppWindow.Id);
                    progress.Activate();
                    return progress;
                });
            _server = new DashboardServer(_store, _runningPort, () =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!_exiting) _connectionTest.IsOpen = true;
                });
            }, new DashboardServerOptions
            {
                ListenerMode = _runningMode == DashboardConnectionMode.DevTunnel
                    ? DashboardListenerMode.Internet : DashboardListenerMode.Lan
            });
            ReportStartupProgress("Starting the local receiver...");
            await _server.StartAsync(cancellationToken);
            _running = true;
            if (_exiting) return;
            ReportStartupProgress("Preparing connection settings...");
            await InitializeTunnelAsync(cancellationToken);
            if (_exiting) return;
            UpdateConnectionPresentation();
            ReportStartupProgress("Loading computer tiles...");
            _refreshTask = RefreshAsync();
            await _refreshTask;
            if (_exiting) return;
            _timer.Start();
            // Keep all machine views gated until sharing finishes public HTTPS verification
            // (or is skipped/failed). A running receiver or a populated cache is not readiness.
            await StartSharingOnStartupAsync();
        }
        catch (Exception error) when (error is IOException or SocketException or SqliteException or UnauthorizedAccessException)
        {
            if (_exiting) return;
            UpdateConnectionPresentation();
            ShowProblem("The receiver could not start. Check that the configured port is unused and that " +
                "%LOCALAPPDATA%\\AgentSignaler is writable. Change the port in Settings, then Exit and restart. " +
                "No firewall rule or URL ACL is required to start the local receiver.");
        }
    }

    private void UpdateStartupPresentation()
    {
        _startupProgress.IsIndeterminate = _startup.IsLoading;
        _startupPanel.Visibility = _startup.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        _machineBody.Visibility = _startup.ShowMachines ? Visibility.Visible : Visibility.Collapsed;
        _machineCount.Visibility = _machineBody.Visibility;
        _compactButton.IsEnabled = _startup.ShowMachines && _cardMap.Count > 0 && _settings.ShowCompactViewWhenMinimized;
        UpdateCompactView();
    }

    private void ReportStartupProgress(string message)
    {
        if (!_exiting && _startup.IsLoading) _startupStatus.Text = message;
    }

    public void ShowDashboard()
    {
        if (_exiting) return;
        _minimized = false;
        _compactWindow?.AppWindow.Hide();
        AppWindow.Show();
        NativeWindow.ShowWindow(_handle, 9);
        NativeWindow.SetForegroundWindow(_handle);
        Activate();
    }

    private void HandleWindowStateChange()
    {
        if (_exiting) return;
        _minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (_minimized && _tray is { IsAvailable: true }) AppWindow.Hide();
        UpdateCompactView();
    }

    private void UpdateCompactView()
    {
        if (_exiting || !_startup.ShowMachines || !_minimized || !_settings.ShowCompactViewWhenMinimized || _cardMap.Count == 0)
        {
            _compactWindow?.AppWindow.Hide();
            return;
        }
        try
        {
            _compactWindow ??= new CompactWindow(ShowDashboard,
                async id => await OpenWindowsAppAsync(id, compact: true), id => _connections?.IsBusy(id) == true,
                ExitAsync);
            _compactWindow.Update(_cardMap.Values.Select(card => card.Machine), _root.RequestedTheme, AppWindow.Id);
            _compactWindow.AppWindow.Show(activateWindow: false);
        }
        catch (Exception error) when (error is COMException or Win32Exception)
        {
            ShowDashboard();
            ShowProblem("The compact view could not be displayed. Restart Agent Signaler or disable 'Compact view when minimized' in Settings.");
        }
    }

    public void ShowProblem(string message)
    {
        _problem.Title = "Action needed";
        _problem.Message = message;
        _problem.Severity = InfoBarSeverity.Error;
        _problem.IsOpen = true;
        if (_compactWindow?.AppWindow.IsVisible == true) ShowDashboard();
    }

    private Task<WindowsAppActionResult> OpenWindowsAppAsync(Guid machineId, bool compact = false) =>
        _connectionActions?.OpenWindowsAppAsync(machineId, compact) ??
        Task.FromResult(new WindowsAppActionResult(false, "Local storage is unavailable. Restart Dashboard and retry.", compact));

    private async Task ShowConnectionDetailsAsync(Guid machineId, string message)
    {
        if (_detailsId == machineId && _activeDialog == _machineDetailsDialog) return;
        if (_activeDialog is not null && _dialogFinished is { } finished)
        {
            _closeDialogForNavigation = true;
            _activeDialog.Hide();
            await finished.Task;
        }
        if (!_exiting) await ShowDetailsAsync(machineId, message);
    }

    private async Task RefreshAsync()
    {
        if (_store is null || _exiting) return;
        try
        {
            // DispatcherQueueTimer and this captured UI context keep all control updates on the UI thread.
            var machines = await _store.GetMachinesAsync();
            if (_exiting) return;
            var ids = machines.Select(m => m.MachineId).ToHashSet();
            var layoutChanged = false;
            foreach (var id in _cardMap.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                _cards.Children.Remove(_cardMap[id].Button);
                _cardMap.Remove(id);
                layoutChanged = true;
            }
            foreach (var machine in machines.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (!_cardMap.TryGetValue(machine.MachineId, out var card))
                {
                    card = new MachineCard(machine, async () => await ShowDetailsAsync(machine.MachineId));
                    _cardMap.Add(machine.MachineId, card);
                    _cards.Children.Add(card.Button);
                    layoutChanged = true;
                }
                if (card.Machine.Name != machine.Name) layoutChanged = true;
                card.Update(machine);
                _connections?.Observe(machine.MachineId, machine.WindowsAppConnection);
            }
            _empty.Visibility = machines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _machineCount.Text = $"{machines.Count} development machine{(machines.Count == 1 ? "" : "s")}";
            _compactButton.IsEnabled = _startup.ShowMachines && machines.Count > 0 && _settings.ShowCompactViewWhenMinimized;
            if (layoutChanged) LayoutCards(Math.Max(1, _cards.ActualWidth));
            UpdateCompactView();
            if (_detailsId is { } detailsId) _updateDetails?.Invoke(machines.FirstOrDefault(m => m.MachineId == detailsId));
            if (_pollErrorShown && _problem.Message == PollErrorMessage) _problem.IsOpen = false;
            _pollErrorShown = false;
        }
        catch (Exception error) when (error is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        {
            if (!_pollErrorShown)
            {
                ShowProblem(PollErrorMessage);
                _pollErrorShown = true;
            }
        }
    }

    private void LayoutCards(double width)
    {
        if (width <= 1) return;
        var (columns, cardWidth) = MachineCardPresentation.Layout(width, _settings.Compact);
        _cards.ColumnDefinitions.Clear();
        _cards.RowDefinitions.Clear();
        for (var i = 0; i < columns; i++) _cards.ColumnDefinitions.Add(new ColumnDefinition());
        var ordered = _cardMap.Values.OrderBy(c => c.Machine.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        for (var i = 0; i < (ordered.Length + columns - 1) / columns; i++)
            _cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < ordered.Length; i++)
        {
            var card = ordered[i].Button;
            card.Width = cardWidth;
            card.MinHeight = MachineCardPresentation.MinimumHeight;
            card.Height = double.NaN;
            Grid.SetColumn(card, i % columns);
            Grid.SetRow(card, i / columns);
        }
    }

    private void ApplyAppearance()
    {
        _root.RequestedTheme = _settings.Theme switch
        {
            "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = false;
        var scale = NativeWindow.GetDpiForWindow(_handle) / 96.0;
        if (scale <= 0) scale = 1;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = _settings.Compact
            ? new SizeInt32((int)(900 * scale), (int)(600 * scale))
            : new SizeInt32((int)(1040 * scale), (int)(640 * scale));
        AppWindow.Resize(new SizeInt32(Math.Min(size.Width, workArea.Width), Math.Min(size.Height, workArea.Height)));
        _compactButton.IsEnabled = _startup.ShowMachines && _cardMap.Count > 0 && _settings.ShowCompactViewWhenMinimized;
        LayoutCards(_cards.ActualWidth);
        UpdateCompactView();
    }

    private void UpdateCanvasAppearance()
    {
        var dark = _root.ActualTheme == ElementTheme.Dark;
        _root.Background = new SolidColorBrush(dark
            ? Color.FromArgb(255, 17, 24, 39) : Color.FromArgb(255, 248, 250, 252));
        _machineCount.Foreground = new SolidColorBrush(dark
            ? Color.FromArgb(255, 148, 163, 184) : Color.FromArgb(255, 71, 85, 105));
        _compactButton.Background = new SolidColorBrush(dark
            ? Color.FromArgb(255, 30, 41, 59) : Color.FromArgb(255, 226, 232, 240));
    }

    private async Task ShowDetailsAsync(Guid id, string? connectionMessage = null)
    {
        if (_exiting || _dialogOpen || _store is null || _connections is null || !_cardMap.TryGetValue(id, out var card))
        {
            ShowProblem("Machine details are unavailable. Close the current dialog or refresh the computer list and retry.");
            return;
        }
        _dialogOpen = true;
        _closeDialogForNavigation = false;
        _dialogFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = _connections;
        connections.Observe(id, card.Machine.WindowsAppConnection);
        var name = new TextBox
        {
            Header = "Display name (blank uses hostname)", Text = card.Machine.DisplayName ?? "",
            MaxLength = 128, PlaceholderText = card.Machine.MachineName
        };
        var note = new TextBox
        {
            Header = "Note/description", Text = card.Machine.Note ?? "",
            PlaceholderText = "Shown when hovering over the computer tile",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80
        };
        var current = Text("");
        current.IsTextSelectionEnabled = true;
        var sessions = Text("");
        sessions.IsTextSelectionEnabled = true;
        var validation = Text("");
        var picker = new ComboBox
        {
            Header = "Mapped Dev Box", PlaceholderText = "Refresh the Dev Box list, then choose a Dev Box",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var mappingSummary = Text("");
        var catalogStatus = Text("");
        var refreshCatalog = new Button { Content = "Refresh Dev Box list" };
        var cancelCatalog = new Button { Content = "Cancel list refresh", Visibility = Visibility.Collapsed };
        var connectionStatus = Text("");
        var refreshed = Text("");
        var connectionUri = Text("");
        connectionUri.IsTextSelectionEnabled = true;
        AutomationProperties.SetName(connectionUri, "Windows App launch URI");
        var copyConnectionUri = new Button { Content = "Copy URI", IsEnabled = false };
        AutomationProperties.SetName(copyConnectionUri, "Copy Windows App launch URI");
        var progress = Text(connectionMessage ?? connections.State(id).Message);
        var discover = new Button { Content = "Save mapping" };
        var signIn = new Button { Content = "Sign in with Azure CLI" };
        var refresh = new Button { Content = "Refresh connection" };
        var open = new Button { Content = "Open in Windows App" };
        var cached = new Button { Content = "Open last known connection" };
        var cachedTime = Text("");
        var clear = new Button { Content = "Clear connection mapping" };
        var cancel = new Button { Content = "Cancel connection operation", Visibility = Visibility.Collapsed };
        Button[] actions = [discover, signIn, refresh, open, cached, clear];
        DevBoxCatalogSnapshot? displayedCatalog = null;
        WindowsAppConnection? displayedMapping = null;
        void LoadPicker(bool useSavedMapping = false)
        {
            var snapshot = _catalog?.State.Snapshot;
            var mapping = connections.State(id).Mapping;
            if (!useSavedMapping && ReferenceEquals(displayedCatalog, snapshot) && displayedMapping == mapping) return;
            var previous = useSavedMapping ? null : (picker.SelectedItem as ComboBoxItem)?.Tag as DevBoxCatalogItem;
            displayedCatalog = snapshot;
            displayedMapping = mapping;
            var presentation = DevBoxMappingPresentation.CreatePicker(snapshot, mapping);
            picker.Items.Clear();
            var selectedIndex = previous is null ? presentation.SelectedIndex : -1;
            foreach (var option in presentation.Options)
            {
                picker.Items.Add(new ComboBoxItem
                {
                    Content = option.DisplayName, Tag = option.DevBox
                });
                if (previous is not null && option.DevBox is { } item &&
                    DevBoxMappingPresentation.MatchesIdentity(item, previous.DevCenterEndpoint, previous.ProjectName, previous.DevBoxName))
                    selectedIndex = picker.Items.Count - 1;
            }
            picker.SelectedIndex = selectedIndex;
        }
        DevBoxMappingSelection? ReadSelection() =>
            (picker.SelectedItem as ComboBoxItem)?.Tag is DevBoxCatalogItem item && displayedCatalog is { } snapshot
                ? new(item, snapshot.AzureAccountUpn, snapshot.AzureTenantId) : null;
        LoadPicker(useSavedMapping: true);
        var content = new StackPanel { Spacing = 12, MinWidth = 280 };
        content.Children.Add(name);
        content.Children.Add(note);
        content.Children.Add(current);
        content.Children.Add(Text("Sessions", 18));
        content.Children.Add(sessions);
        content.Children.Add(validation);
        content.Children.Add(Text("Windows App connection", 18));
        content.Children.Add(Text("Use the assigned Dev Box mapping, not the reported hostname or display name."));
        content.Children.Add(Text("Only Save mapping commits your selection after verifying a fresh connection. Refresh connection and Open use the saved mapping."));
        content.Children.Add(picker);
        content.Children.Add(mappingSummary);
        content.Children.Add(refreshCatalog);
        content.Children.Add(cancelCatalog);
        content.Children.Add(catalogStatus);
        content.Children.Add(connectionStatus);
        content.Children.Add(refreshed);
        content.Children.Add(Text("Windows App launch URI (last retrieved)", 16));
        content.Children.Add(connectionUri);
        content.Children.Add(copyConnectionUri);
        content.Children.Add(Text("Uses the saved Dev Box mapping. Save a mapping or refresh the connection to retrieve its URI. " +
            "The URI includes account information; only share it with people you trust."));
        foreach (var action in actions)
        {
            content.Children.Add(action);
            if (action == cached) content.Children.Add(cachedTime);
        }
        content.Children.Add(progress);
        content.Children.Add(cancel);
        content.Children.Add(Text("Closing the sign-in browser does not cancel sign-in. Use Cancel, close this dialog, or Exit Dashboard."));
        var dialog = new ContentDialog
        {
            XamlRoot = _root.XamlRoot, Title = "Machine details",
            Content = new ScrollViewer { Content = content, MaxHeight = 470 },
            PrimaryButtonText = "Save details", SecondaryButtonText = "Remove", CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        };
        _activeDialog = dialog;
        _machineDetailsDialog = dialog;
        var machineExists = true;
        var catalogRefreshOwned = false;
        var updatingControls = false;
        ContentDialog? clearConfirmation = null;
        _updateConnectionControls = () =>
        {
            if (updatingControls) return;
            updatingControls = true;
            try
            {
                var state = connections.State(id);
                var busy = connections.IsBusy(id);
                var catalogBusy = _catalog?.State.IsBusy == true;
                LoadPicker();
                picker.IsEnabled = machineExists && !busy && !catalogBusy;
                foreach (var action in actions) action.IsEnabled = machineExists && state.ActionsEnabled && !busy;
                discover.IsEnabled &= DevBoxMappingPresentation.CanSaveMapping(busy, catalogBusy, ReadSelection() is not null);
                refreshCatalog.IsEnabled = machineExists && !catalogBusy && !busy && _catalog is not null;
                cancelCatalog.Visibility = catalogBusy && catalogRefreshOwned ? Visibility.Visible : Visibility.Collapsed;
                catalogStatus.Text = _catalog?.State.Message ?? "Discovery is unavailable. Restart Dashboard and retry.";
                var selectedItem = (picker.SelectedItem as ComboBoxItem)?.Tag as DevBoxCatalogItem;
                mappingSummary.Text = selectedItem is not null
                    ? $"Selected: {selectedItem.DevBoxName}\nDev Center: {selectedItem.DevCenterEndpoint.IdnHost}\nProject: {selectedItem.ProjectName}\nPool: {selectedItem.PoolName}\n" +
                      "Account and tenant: validated catalog identity (view in Dev Box Settings)."
                    : state.Mapping is { } saved
                        ? $"Saved: {saved.DevBoxName}\nDev Center: {saved.DevCenterEndpoint.IdnHost}\nProject: {saved.ProjectName}\nPool: unavailable in this catalog.\n" +
                          "The saved account and tenant remain unchanged. Refresh connection and Open still use this mapping."
                        : "No Dev Box mapped. Refresh the list and explicitly choose a Dev Box.";
                refresh.IsEnabled &= state.Mapping is not null;
                clear.IsEnabled &= state.Mapping is not null;
                cached.IsEnabled &= state.CanOpenLastKnown;
                cached.Visibility = cachedTime.Visibility = state.Mapping?.LastKnownConnectionUri is not null
                    ? Visibility.Visible : Visibility.Collapsed;
                cachedTime.Text = $"Retrieved locally: {state.LastRefresh ?? "Never"}";
                refreshed.Text = $"Last successful refresh: {state.LastRefresh ?? "Never"}";
                var uri = machineExists ? state.Mapping?.LastKnownConnectionUri : null;
                var uriText = uri ?? "No connection URI available.";
                if (connectionUri.Text != uriText) connectionUri.Text = uriText;
                copyConnectionUri.IsEnabled = machineExists && !busy && state.ActionsEnabled && uri is not null;
                connectionStatus.Text = state.Status;
                if (busy) connectionMessage = null;
                progress.Text = connectionMessage ?? state.Message;
                cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
                cancel.IsEnabled = busy;
                name.IsEnabled = note.IsEnabled = dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = machineExists && !busy;
                if (clearConfirmation is not null) clearConfirmation.IsSecondaryButtonEnabled = machineExists && !busy;
                _compactWindow?.UpdateConnectionAvailability();
            }
            finally { updatingControls = false; }
        };
        async Task RunConnectionAsync(WindowsAppOperation operation)
        {
            connectionMessage = null;
            var result = operation == WindowsAppOperation.Open
                ? await OpenWindowsAppAsync(id)
                : await connections.ExecuteAsync(id, operation, ReadSelection());
            if (_exiting) return;
            await RefreshAsync();
            if (result.Succeeded && operation is WindowsAppOperation.Map or WindowsAppOperation.Clear)
                LoadPicker(useSavedMapping: true);
            connectionMessage = result.Message;
            _updateConnectionControls?.Invoke();
        }
        picker.SelectionChanged += (_, _) => _updateConnectionControls?.Invoke();
        copyConnectionUri.Click += (_, _) =>
        {
            if (!machineExists || connections.IsBusy(id) ||
                connections.State(id).Mapping?.LastKnownConnectionUri is not { } uri)
            {
                connectionMessage = "The connection URI is unavailable. Wait for any operation to finish, then save a mapping or refresh the connection.";
            }
            else
            {
                try
                {
                    var package = new DataPackage();
                    package.SetText(uri);
                    Clipboard.SetContent(package);
                    Clipboard.Flush();
                    connectionMessage = "Windows App launch URI copied.";
                }
                catch (Exception error) when (error is COMException or UnauthorizedAccessException)
                {
                    connectionMessage = "Windows could not access the clipboard. Select the connection URI and copy it manually.";
                }
            }
            _updateConnectionControls?.Invoke();
        };
        discover.Click += async (_, _) => await RunConnectionAsync(WindowsAppOperation.Map);
        refreshCatalog.Click += async (_, _) =>
        {
            if (_catalog is null) return;
            catalogRefreshOwned = true;
            try
            {
                await _catalog.RefreshAsync(subscriptionId: _settings.DevBoxSubscriptionId, devCenterName: _settings.DevCenterName);
            }
            finally
            {
                catalogRefreshOwned = false;
                _updateConnectionControls?.Invoke();
            }
        };
        cancelCatalog.Click += (_, _) => _catalog?.Cancel();
        signIn.Click += async (_, _) => await RunConnectionAsync(WindowsAppOperation.SignIn);
        refresh.Click += async (_, _) => await RunConnectionAsync(WindowsAppOperation.Refresh);
        open.Click += async (_, _) => await RunConnectionAsync(WindowsAppOperation.Open);
        cached.Click += async (_, _) => await RunConnectionAsync(WindowsAppOperation.OpenLastKnown);
        cancel.Click += (_, _) => connections.Cancel(id);
        var clearRequested = false;
        clear.Click += (_, _) => { clearRequested = true; dialog.Hide(); };
        dialog.Closing += (_, _) =>
        {
            connections.Cancel(id);
            if (catalogRefreshOwned) _catalog?.Cancel();
        };
        dialog.Opened += (_, _) =>
        {
            if (connections.State(id).Mapping is null || connectionMessage is not null)
            {
                picker.StartBringIntoView();
                picker.Focus(FocusState.Programmatic);
            }
        };
        _detailsId = id;
        _updateDetails = machine =>
        {
            machineExists = machine is not null;
            _updateConnectionControls?.Invoke();
            if (machine is null)
            {
                current.Text = "This machine was removed.";
                sessions.Text = "";
                return;
            }
            current.Text = $"Hostname: {machine.MachineName}\nReporter: {machine.Client} {machine.ClientVersion}\n" +
                $"Session sources: {SessionSourcePresentation.Summary(machine.Sessions)}\n" +
                $"Status: {MachineCard.StatusText(machine.State)}\nLatest event: {machine.LatestEvent?.ToString() ?? "No hook received"}\n" +
                $"Latest event time: {machine.LatestEventUtc?.ToLocalTime().ToString("G") ?? "None"}\n" +
                $"Server last contact: {machine.LastContactUtc.ToLocalTime():G}\n" +
                $"Reporting: {(machine.PresenceMode == PresenceMode.Managed ? $"Managed, every {machine.HeartbeatIntervalSeconds / 60} minute(s)" : "Legacy, five-minute timeout")}\n" +
                $"Offline: {(machine.State != AgentState.Offline ? "No" : machine.ExplicitOffline ? "Reported by client" : "Contact timeout")}\n" +
                $"Machine ID: {machine.MachineId}";
            sessions.Text = machine.Sessions.Count == 0 ? "No active sessions." :
                string.Join("\n\n", machine.Sessions.Select(s =>
                    $"{SessionSourcePresentation.Describe(s.Source)}\n{s.SessionId}\n{MachineCard.StatusText(StateReducer.Effective(s, DateTimeOffset.UtcNow))}" +
                    $" · last observed {s.UpdatedAtUtc.ToLocalTime():G}"));
        };
        _updateDetails(card.Machine);
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (name.Text.Any(char.IsControl))
                {
                    validation.Text = "Use a display name without control characters.";
                    args.Cancel = true;
                    return;
                }
                _mutationTask = _store.UpdateDetailsAsync(id, name.Text, note.Text);
                await _mutationTask;
                await RefreshAsync();
            }
            catch (Exception error) when (error is SqliteException or JsonException or IOException or UnauthorizedAccessException or KeyNotFoundException)
            {
                validation.Text = error is KeyNotFoundException ? "This machine was removed." :
                    "The details could not be saved. Check database access and try again.";
                args.Cancel = true;
            }
            finally { deferral.Complete(); }
        };
        try
        {
            ContentDialogResult result;
            var clearConfirmed = false;
            while (true)
            {
                clearRequested = false;
                _activeDialog = dialog;
                var showing = dialog.ShowAsync();
                if (clearConfirmed)
                {
                    clearConfirmed = false;
                    await RunConnectionAsync(WindowsAppOperation.Clear);
                }
                result = await showing;
                await connections.CancelAndWaitAsync(id);
                if (catalogRefreshOwned && _catalog is not null) await _catalog.CancelAndWaitAsync();
                if (!clearRequested || _exiting || _closeDialogForNavigation) break;
                var confirmClear = new ContentDialog
                {
                    XamlRoot = _root.XamlRoot, Title = "Clear connection mapping?",
                    Content = "This deletes only the Dev Box mapping and its last known connection. Machine history, display name, and note are retained.",
                    SecondaryButtonText = "Clear connection mapping", CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close
                };
                _activeDialog = confirmClear;
                clearConfirmation = confirmClear;
                _updateConnectionControls?.Invoke();
                clearConfirmed = await confirmClear.ShowAsync() == ContentDialogResult.Secondary && !_exiting;
                clearConfirmation = null;
                if (_exiting || _closeDialogForNavigation) break;
            }
            if (result == ContentDialogResult.Secondary && !_exiting && !_closeDialogForNavigation)
            {
                var confirm = new ContentDialog
                {
                    XamlRoot = _root.XamlRoot, Title = "Remove this machine?",
                    Content = "This removes its local history, display name, and note, releases its event receipts, and resets replay protection. " +
                        "Previously seen reports can be accepted again. The machine will reappear if a client reports again.",
                    PrimaryButtonText = "Remove", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
                };
                _activeDialog = confirm;
                if (await confirm.ShowAsync() == ContentDialogResult.Primary && !_exiting && !_closeDialogForNavigation)
                {
                    _mutationTask = _store.RemoveAsync(id);
                    await _mutationTask;
                    await RefreshAsync();
                }
            }
        }
        catch (Exception error) when (error is SqliteException or JsonException or IOException or UnauthorizedAccessException or COMException)
        {
            ShowProblem("The machine action could not finish. Check local database access and try again.");
        }
        finally
        {
            await connections.CancelAndWaitAsync(id);
            if (catalogRefreshOwned && _catalog is not null) await _catalog.CancelAndWaitAsync();
            _detailsId = null;
            _updateDetails = null;
            _updateConnectionControls = null;
            _activeDialog = null;
            _machineDetailsDialog = null;
            _dialogOpen = false;
            _dialogFinished.TrySetResult();
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (_dialogOpen || _exiting) return;
        _dialogOpen = true;
        _dialogFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var port = new NumberBox
            {
                Header = "Receiver TCP port (restart required)", Value = _settings.Port, Minimum = 1024, Maximum = 65535,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                ValidationMode = NumberBoxValidationMode.Disabled
            };
            var compact = new ToggleSwitch { Header = "Compact view", IsOn = _settings.Compact };
            var minimizedCompact = new ToggleSwitch
            {
                Header = "Compact view when minimized", IsOn = _settings.ShowCompactViewWhenMinimized
            };
            var mode = new ComboBox
            {
                Header = "Connection mode (restart required)", HorizontalAlignment = HorizontalAlignment.Stretch
            };
            mode.Items.Add("Trusted LAN / VPN");
            mode.Items.Add("Internet HTTPS (Dev Tunnels CLI)");
            mode.SelectedIndex = _settings.ConnectionMode == DashboardConnectionMode.Lan ? 0 : 1;
            var startupEnabled = StartupRegistration.IsEnabled();
            var startup = new ToggleSwitch { Header = "Start at Windows sign-in (this user)", IsOn = startupEnabled };
            var theme = new ComboBox { Header = "Appearance", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var item in new[] { "System", "Light", "Dark" }) theme.Items.Add(item);
            theme.SelectedItem = _settings.Theme;
            var outcome = Text("");
            outcome.IsTextSelectionEnabled = true;
            var tabs = new TabView
            {
                IsAddTabButtonVisible = false, CanDragTabs = false, CanReorderTabs = false,
                TabWidthMode = TabViewWidthMode.SizeToContent
            };
            TabViewItem AddSection(string title, StackPanel section, StackPanel help)
            {
                var layout = new Grid { Padding = new Thickness(0, 12, 12, 0), ColumnSpacing = 12 };
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                layout.Children.Add(new ScrollViewer
                {
                    Content = section,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch
                });
                var helpButton = new Button
                {
                    Content = new SymbolIcon(Symbol.Help), Width = 32, Height = 32, Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top
                };
                AutomationProperties.SetName(helpButton, $"{title} help");
                ToolTipService.SetToolTip(helpButton, new ToolTip
                {
                    Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Left,
                    MaxWidth = 440,
                    Content = new ScrollViewer
                    {
                        Content = help, MaxWidth = 400, MaxHeight = 360,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    }
                });
                Grid.SetColumn(helpButton, 1);
                layout.Children.Add(helpButton);
                var tab = new TabViewItem
                {
                    Header = title, IsClosable = false, Content = layout
                };
                tabs.TabItems.Add(tab);
                return tab;
            }
            var general = new StackPanel { Spacing = 14 };
            var generalHelp = new StackPanel { Spacing = 10 };
            general.Children.Add(theme);
            general.Children.Add(compact);
            general.Children.Add(minimizedCompact);
            general.Children.Add(startup);
            generalHelp.Children.Add(Text("When enabled, minimizing shows an always-on-top vertical list of 64 x 64 computer tiles. " +
                "Select a tile to open its configured Dev Box in Windows App. Use the notification-area icon to restore the dashboard. Closing hides it in the notification area. " +
                "The receiver stays running. Use Exit to stop it."));
            var generalTab = AddSection("General", general, generalHelp);
            var network = new StackPanel { Spacing = 14 };
            var networkHelp = new StackPanel { Spacing = 10 };
            network.Children.Add(mode);
            network.Children.Add(port);
            network.Children.Add(Text($"Effective mode: {_runningMode}. Running local port: {(_running ? _runningPort.ToString() : "not running")}."));
            networkHelp.Children.Add(Text("After changing connection settings, Save, Exit and reopen. Internet mode binds loopback only and starts sharing automatically unless disabled."));
            network.Children.Add(Text("Windows Firewall", 18));
            networkHelp.Children.Add(Text("LAN mode: explicit administrator approval is required for a Private-network rule. " +
                "Internet mode needs outbound access only; no inbound firewall rule, router forwarding, or local certificate is needed."));
            var allow = new Button { Content = "Add Private firewall rule…", IsEnabled = _running && _runningMode == DashboardConnectionMode.Lan };
            var remove = new Button { Content = "Remove firewall rule…" };
            async Task ChangeFirewall(bool add)
            {
                allow.IsEnabled = false;
                remove.IsEnabled = false;
                try { outcome.Text = await Firewall.ChangeAsync(add, _running ? _runningPort : _settings.Port); }
                catch (Exception error) when (error is Win32Exception or IOException or COMException or UnauthorizedAccessException or SecurityException)
                {
                    outcome.Text = "The firewall helper could not run. Check the installation and administrator permissions.";
                }
                finally
                {
                    allow.IsEnabled = _running && _runningMode == DashboardConnectionMode.Lan;
                    remove.IsEnabled = true;
                }
            }
            allow.Click += async (_, _) => await ChangeFirewall(true);
            remove.Click += async (_, _) => await ChangeFirewall(false);
            network.Children.Add(allow);
            network.Children.Add(remove);
            var networkTab = AddSection("Network", network, networkHelp);
            var sharing = new StackPanel { Spacing = 14 };
            var sharingHelp = new StackPanel { Spacing = 10 };
            BuildTunnelSettings(sharing, sharingHelp);
            AddSection("Internet sharing", sharing, sharingHelp);
            var azure = new StackPanel { Spacing = 14 };
            var azureHelp = new StackPanel { Spacing = 10 };
            var readDevBoxSettings = BuildDevBoxSettings(azure, azureHelp);
            var azureTab = AddSection("Dev Box", azure, azureHelp);
            var prerequisites = new StackPanel { Spacing = 14 };
            var prerequisiteHelp = new StackPanel { Spacing = 10 };
            var (cliPath, azureCliPath) = BuildPrerequisiteSettings(prerequisites, prerequisiteHelp);
            var prerequisiteTab = AddSection("Prerequisite", prerequisites, prerequisiteHelp);
            tabs.SelectedItem = generalTab;
            var content = new Grid { MinWidth = 280, Height = 480, RowSpacing = 12 };
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.Children.Add(tabs);
            var feedback = new ScrollViewer { Content = outcome, MaxHeight = 100 };
            Grid.SetRow(feedback, 1);
            content.Children.Add(feedback);
            var dialog = new ContentDialog
            {
                XamlRoot = _root.XamlRoot, Title = "Dashboard settings",
                Content = content,
                PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            _activeDialog = dialog;
            _updateDevBoxSettingsControls?.Invoke();
            dialog.Closing += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                _catalog?.Cancel();
                try
                {
                    var checks = CancelPrerequisiteChecksAsync();
                    _updatePrerequisiteControls?.Invoke();
                    _updateDevBoxSettingsControls?.Invoke();
                    if (_catalog is not null) await _catalog.CancelAndWaitAsync();
                    await checks;
                }
                finally { deferral.Complete(); }
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (_catalog?.State.IsBusy == true || PrerequisiteBusy)
                {
                    outcome.Text = "Cancel or wait for Dev Box discovery and prerequisite checks before saving.";
                    args.Cancel = true;
                    return;
                }
                if (!double.IsFinite(port.Value) || port.Value != Math.Truncate(port.Value) || port.Value is < 1024 or > 65535)
                {
                    tabs.SelectedItem = networkTab;
                    outcome.Text = "Enter a whole-number TCP port from 1024 to 65535.";
                    args.Cancel = true;
                    return;
                }
                var selectedPath = cliPath.Text.Trim();
                try { DevTunnelDiagnostics.ResolvePath(selectedPath); }
                catch (ArgumentException error)
                {
                    tabs.SelectedItem = prerequisiteTab;
                    cliPath.Focus(FocusState.Programmatic);
                    outcome.Text = error.Message;
                    args.Cancel = true;
                    return;
                }
                var selectedAzureCliPath = azureCliPath.Text.Trim();
                try { AzureCliInstallation.ResolvePath(selectedAzureCliPath); }
                catch (ArgumentException error)
                {
                    tabs.SelectedItem = prerequisiteTab;
                    azureCliPath.Focus(FocusState.Programmatic);
                    outcome.Text = error.Message;
                    args.Cancel = true;
                    return;
                }
                DashboardSettings discoverySettings;
                try { discoverySettings = readDevBoxSettings(); }
                catch (ArgumentException error)
                {
                    tabs.SelectedItem = azureTab;
                    outcome.Text = error.Message;
                    args.Cancel = true;
                    return;
                }
                try
                {
                    var next = discoverySettings with
                    {
                        Port = (int)port.Value, Compact = compact.IsOn,
                        ShowCompactViewWhenMinimized = minimizedCompact.IsOn,
                        Theme = (string)theme.SelectedItem,
                        ConnectionMode = mode.SelectedIndex == 0 ? DashboardConnectionMode.Lan : DashboardConnectionMode.DevTunnel,
                        DevTunnelCliPath = selectedPath.Length == 0 ? null : selectedPath,
                        AzureCliPath = selectedAzureCliPath.Length == 0 ? null : selectedAzureCliPath
                    };
                    if (startup.IsOn != startupEnabled)
                    {
                        StartupRegistration.SetEnabled(startup.IsOn);
                        startupEnabled = startup.IsOn;
                    }
                    next.Save();
                    _settings = next;
                    ApplyAppearance();
                    if (_running && (_settings.Port != _runningPort || _settings.ConnectionMode != _runningMode ||
                        _settings.DevTunnelCliPath != _runningCliPath || _settings.AzureCliPath != _runningAzureCliPath))
                    {
                        _problem.Title = "Restart required";
                        _problem.Message = $"Connection settings saved. The receiver still uses {_runningMode} on port {_runningPort}. " +
                            "Exit and reopen Agent Signaler to use the new settings, including any Azure CLI path change. " +
                            (_settings.ShouldStartSharing ? "Internet sharing will start automatically after restart." : "Automatic Internet sharing is disabled.");
                        _problem.Severity = InfoBarSeverity.Informational;
                        _problem.IsOpen = true;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or COMException)
                {
                    outcome.Text = "Some settings could not be saved. Check local application-data and startup-registry " +
                        "permissions, then retry. A sign-in setting already applied may remain changed.";
                    args.Cancel = true;
                }
            };
            await dialog.ShowAsync();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or COMException)
        {
            ShowProblem("Settings could not be opened. Check access to your Windows startup settings and restart the app.");
        }
        finally
        {
            if (_catalog is not null) await _catalog.CancelAndWaitAsync();
            await CancelPrerequisiteChecksAsync();
            _updateDevBoxSettingsControls = null;
            ReleasePrerequisiteSettings();
            ReleaseTunnelSettings();
            _dialogOpen = false;
            _activeDialog = null;
            _dialogFinished.TrySetResult();
        }
    }

    public Task ExitAsync() => _shutdownTask ??= ShutdownAsync();

    private async Task ShutdownAsync()
    {
        _exiting = true;
        var connectionShutdown = _connections?.StopAsync() ?? Task.CompletedTask;
        var catalogShutdown = _catalog?.StopAsync() ?? Task.CompletedTask;
        var prerequisiteShutdown = CancelPrerequisiteChecksAsync();
        _activeDialog?.Hide();
        CancelTunnelOperations();
        _timer.Stop();
        _root.IsHitTestVisible = false;
        _compactWindow?.CloseForExit();
        _compactWindow = null;
        AppWindow.Hide();
        var failed = false;
        try
        {
            await prerequisiteShutdown;
            if (_initializationTask is not null) await _initializationTask;
            try { await ShutdownTunnelAsync(); }
            catch (Exception error) when (error is IOException or Win32Exception or TunnelException or TimeoutException or OperationCanceledException)
            { failed = true; }
            await _refreshTask;
            try { await _mutationTask; }
            catch (Exception error) when (error is SqliteException or JsonException or IOException or UnauthorizedAccessException)
            { failed = true; }
            if (_server is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await _server.StopAsync(timeout.Token); }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested) { failed = true; }
                catch (Exception error) when (error is IOException or SocketException) { failed = true; }
                try
                {
                    await _server.DisposeAsync();
                }
                catch (Exception error) when (error is IOException or SocketException) { failed = true; }
            }
        }
        finally
        {
            await catalogShutdown;
            await connectionShutdown;
            _store?.Dispose();
            _windowStateMonitor?.Dispose();
            _tray?.Dispose();
            _running = false;
            _allowClose = true;
            if (failed)
                NativeWindow.ShowError("Agent Signaler encountered a shutdown error. The process will exit. " +
                    "Restart the app and check local database access if the problem recurs.");
            Close();
            Application.Current.Exit();
        }
    }

    private void AddRow(FrameworkElement element, int row)
    {
        Grid.SetRow(element, row);
        _root.Children.Add(element);
    }

    internal static TextBlock Text(string value, double size = 14) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap
    };
}

internal sealed class MachineCard
{
    private readonly bool _miniature;
    private readonly SolidColorBrush _background = new();
    private readonly SolidColorBrush _pointerOverBackground = new();
    private readonly SolidColorBrush _pressedBackground = new();
    private readonly FontIcon _connectionIcon = new()
    {
        Glyph = "\u25CF", FontFamily = new FontFamily("Segoe UI"),
        FontSize = 24, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly FontIcon _icon = new()
    {
        FontFamily = new FontFamily("Segoe UI"), FontSize = 54,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _name = MainWindow.Text("", 22);
    private readonly TextBlock _status = MainWindow.Text("", 26);
    private readonly TextBlock _activity = MainWindow.Text("", 16);
    private readonly TextBlock _mapping = MainWindow.Text("", 16);
    private readonly TextBlock _footer = MainWindow.Text("", 14);
    public Button Button { get; }
    public MachineView Machine { get; private set; }

    public MachineCard(MachineView machine, Action activate, bool miniature = false)
    {
        Machine = machine;
        _miniature = miniature;
        _name.MaxLines = 2;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _status.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _activity.MaxLines = 2;
        _activity.TextTrimming = TextTrimming.CharacterEllipsis;
        _mapping.MaxLines = 2;
        _mapping.TextTrimming = TextTrimming.CharacterEllipsis;
        Button = new Button
        {
            Content = miniature ? _icon : CreateFullContent(),
            Padding = new Thickness(miniature ? 4 : 24),
            CornerRadius = new CornerRadius(miniature ? 4 : 18),
            Background = _background,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        Button.Resources["ButtonBackgroundPointerOver"] = _pointerOverBackground;
        Button.Resources["ButtonBackgroundPressed"] = _pressedBackground;
        if (miniature)
        {
            Button.Width = Button.Height = CompactWindow.TileSize;
            Button.MinWidth = Button.MinHeight = 0;
            _icon.FontSize = 32;
        }
        Button.Click += (_, _) => activate();
        Update(machine);
    }

    private Grid CreateFullContent()
    {
        var content = new Grid { RowSpacing = 16 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.Children.Add(_connectionIcon);
        Grid.SetColumn(_name, 1);
        heading.Children.Add(_name);
        content.Children.Add(heading);

        var state = new Grid { ColumnSpacing = 16, VerticalAlignment = VerticalAlignment.Center };
        state.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        state.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        state.Children.Add(_icon);
        var description = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        description.Children.Add(_status);
        description.Children.Add(_activity);
        Grid.SetColumn(description, 1);
        state.Children.Add(description);
        Grid.SetRow(state, 1);
        content.Children.Add(state);

        var footer = new StackPanel { Spacing = 4 };
        footer.Children.Add(_mapping);
        footer.Children.Add(_footer);
        Grid.SetRow(footer, 2);
        content.Children.Add(footer);
        return content;
    }

    public void Update(MachineView machine)
    {
        Machine = machine;
        _name.Text = machine.Name;
        _status.Text = machine.State == AgentState.Waiting ? "Waiting" : StatusText(machine.State);
        _mapping.Text = DevBoxMappingPresentation.TileText(machine.WindowsAppConnection);
        var now = DateTimeOffset.UtcNow;
        _activity.Text = MachineCardPresentation.Activity(machine, now);
        _footer.Text = MachineCardPresentation.Footer(machine, now);
        var online = machine.State != AgentState.Offline;
        var connectionStatus = online ? "Online" : "Offline";
        ApplyStateAppearance(machine.State);
        AutomationProperties.SetName(_connectionIcon, connectionStatus);
        ToolTipService.SetToolTip(_connectionIcon, connectionStatus);
        var summary = online ? $"{connectionStatus}, {StatusText(machine.State)}" : connectionStatus;
        AutomationProperties.SetName(Button, _miniature ? $"{WindowsAppConnectionController.LaunchLabel(machine.Name)}, {summary}"
            : $"{machine.Name}, {summary}. {_activity.Text}. {_mapping.Text}. {_footer.Text}. Open machine details.");
        var tooltip = _miniature
            ? $"{WindowsAppConnectionController.LaunchLabel(machine.Name)}\nStatus: {StatusText(machine.State)}\n{StatusDescription(machine.State)}\n{ActivityDescription(machine)}"
            : $"{machine.Name} — {summary}\n{_mapping.Text}";
        if (!string.IsNullOrWhiteSpace(machine.Note)) tooltip += $"\n\nNote: {machine.Note}";
        ToolTipService.SetToolTip(Button, tooltip);
        if (_miniature) ToolTipService.SetToolTip(_connectionIcon, tooltip);
    }

    private void ApplyStateAppearance(AgentState state)
    {
        var appearance = MachineCardAppearance.For(state);
        var background = OpaqueColor(appearance.Background);
        var icon = OpaqueColor(appearance.Icon);
        _icon.Glyph = appearance.Glyph;
        _icon.Foreground = new SolidColorBrush(icon);
        _name.Foreground = new SolidColorBrush(OpaqueColor(appearance.Text));
        _status.Foreground = new SolidColorBrush(OpaqueColor(appearance.Status));
        _activity.Foreground = new SolidColorBrush(OpaqueColor(appearance.Activity));
        _mapping.Foreground = new SolidColorBrush(OpaqueColor(appearance.Mapping));
        _footer.Foreground = new SolidColorBrush(OpaqueColor(appearance.Footer));
        _connectionIcon.Foreground = new SolidColorBrush(OpaqueColor(appearance.Connection));
        _background.Color = background;
        _pointerOverBackground.Color = Blend(background, icon, 0.10);
        _pressedBackground.Color = Blend(background, icon, 0.18);
        Button.BorderThickness = new Thickness(1);
        Button.BorderBrush = new SolidColorBrush(OpaqueColor(appearance.Border));
        Button.Resources["ButtonBorderBrushPointerOver"] = _icon.Foreground;
        Button.Resources["ButtonBorderBrushPressed"] = _name.Foreground;
    }

    private static Color OpaqueColor(uint rgb) =>
        Color.FromArgb(255, (byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255));

    private static Color Blend(Color background, Color foreground, double amount) => Color.FromArgb(255,
        (byte)Math.Round(background.R + (foreground.R - background.R) * amount),
        (byte)Math.Round(background.G + (foreground.G - background.G) * amount),
        (byte)Math.Round(background.B + (foreground.B - background.B) * amount));

    public static string StatusText(AgentState state) => state switch
    {
        AgentState.Executing => "Executing",
        AgentState.Waiting => "Waiting for input",
        AgentState.Succeeded => "Succeeded",
        AgentState.Failed => "Failed",
        AgentState.Idle => "Idle",
        _ => "Offline"
    };

    private static string StatusDescription(AgentState state) => state switch
    {
        AgentState.Executing => "The agent is actively processing work.",
        AgentState.Waiting => "The agent is waiting for input or permission.",
        AgentState.Succeeded => "The most recent task completed successfully.",
        AgentState.Failed => "The most recent task reported a failure.",
        AgentState.Idle => "No active work is currently reported.",
        _ => "No recent activity has been received from this computer."
    };

    private static string ActivityDescription(MachineView machine) =>
        machine.LatestEvent is { } latest
            ? $"Latest activity: {latest}; tracked sessions: {machine.Sessions.Count}."
            : "No hook activity has been received yet.";
}
