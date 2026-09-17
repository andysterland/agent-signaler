using System.Collections.Immutable;
using AgentSignaler.Contracts;
using AgentSignaler.Service;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AgentSignaler.Dashboard;

internal sealed class TranscriptDetailsView : IDisposable
{
    private readonly TranscriptViewController _controller;
    private readonly DispatcherQueue _dispatcher;
    private readonly TranscriptSelection _selection;
    private readonly ComboBox _sessions = new()
    {
        Header = "Retained stream for this Copilot", PlaceholderText = "No retained streams",
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _status = MainWindow.Text("");
    private readonly Button _older = new() { Content = "Older retained entries" };
    private readonly Button _latest = new() { Content = "Latest" };
    private readonly ListView _entries = new()
    {
        SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private ImmutableArray<TranscriptSessionInfo> _displayedSessions = [];
    private ImmutableArray<TranscriptEntry> _displayedEntries = [];
    private ScrollViewer? _scroll;
    private bool _updating, _disposed, _visible, _offline;
    private int _queued;

    public TranscriptDetailsView(ITranscriptReader reader, DispatcherQueue dispatcher, TranscriptSelection selection, bool offline)
    {
        _controller = new(reader);
        _dispatcher = dispatcher;
        _selection = selection;
        _offline = offline;
        Root = new Grid { RowSpacing = 8, Padding = new Thickness(0, 12, 0, 0) };
        Root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Root.Children.Add(_sessions);
        var statusRegion = new ScrollViewer { Content = _status, MaxHeight = 84 };
        Grid.SetRow(statusRegion, 1);
        Root.Children.Add(statusRegion);
        // A bounded ListView owns its scroll region; no surrounding ScrollViewer defeats virtualization.
        _entries.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="6" Margin="0,8,12,12">
                <TextBlock TextWrapping="Wrap" IsTextSelectionEnabled="False"
                           FontWeight="SemiBold" />
                <TextBlock TextWrapping="Wrap" IsTextSelectionEnabled="False" />
              </StackPanel>
            </DataTemplate>
            """);
        _entries.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is not StackPanel row) return;
            var item = !args.InRecycleQueue && !_disposed && _visible ? args.Item as TranscriptEntry : null;
            if (item is not null && !_controller.State.Entries.Contains(item)) item = null;
            ((TextBlock)row.Children[0]).Text = item?.Header ?? "";
            ((TextBlock)row.Children[1]).Text = item?.Text ?? "";
            args.Handled = true;
        };
        AutomationProperties.SetName(_entries, "Read-only retained transcript observations");
        Grid.SetRow(_entries, 2);
        Root.Children.Add(_entries);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        navigation.Children.Add(_older);
        navigation.Children.Add(_latest);
        Grid.SetRow(navigation, 3);
        Root.Children.Add(navigation);
        _sessions.SelectionChanged += async (_, _) =>
        {
            if (!_updating && _sessions.SelectedItem is SessionChoice choice)
                await _controller.SelectAsync(choice.Session.Selection);
        };
        _older.Click += async (_, _) => await _controller.OlderAsync();
        _latest.Click += async (_, _) => await _controller.LatestAsync();
        _entries.Loaded += (_, _) =>
        {
            if (_disposed || _scroll is not null) return;
            _scroll = FindScrollViewer(_entries);
            if (_scroll is not null) _scroll.ViewChanged += OnScrollChanged;
        };
        _controller.Changed += OnChanged;
    }

    public Grid Root { get; }

    public async Task SetVisibleAsync(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (visible) await _controller.ShowAsync(_selection, _offline);
        else
        {
            _controller.Hide();
            ReleaseText();
        }
    }

    public void SetMachine(MachineView? machine)
    {
        if (machine is null)
        {
            _controller.Hide();
            ReleaseText();
            return;
        }
        _offline = machine.State == AgentState.Offline;
        _controller.SetOffline(_offline);
    }

    private void OnChanged()
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) Render();
        else if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
                {
                    Interlocked.Exchange(ref _queued, 0);
                    if (!_disposed) Render();
                }))
                Interlocked.Exchange(ref _queued, 0);
        }
    }

    private void Render()
    {
        if (_disposed) return;
        var state = _controller.State;
        if (!_visible || !state.Visible)
        {
            ReleaseText();
            return;
        }
        _updating = true;
        try
        {
            if (!_displayedSessions.SequenceEqual(state.Sessions))
            {
                _displayedSessions = state.Sessions;
                _sessions.ItemsSource = state.Sessions.Select(session => new SessionChoice(session)).ToArray();
            }
            _sessions.SelectedItem = _sessions.Items.OfType<SessionChoice>().FirstOrDefault(choice =>
                state.Selection is { } selection && TranscriptViewController.SameSelection(choice.Session.Selection, selection));
            _status.Text = (state.Loading ? "Loading… " : "") + state.Message;
            _older.IsEnabled = state.HasOlder && !state.Loading;
            _latest.IsEnabled = state.Selection is not null && !state.Loading;
            _latest.Content = state.HasNewActivity ? "New activity — Latest" : "Latest";
            if (_displayedEntries != state.Entries)
            {
                ClearRenderedText();
                _displayedEntries = state.Entries;
                _entries.ItemsSource = state.Entries;
                if (!state.Entries.IsEmpty)
                    _entries.ScrollIntoView(state.FollowingLatest ? state.Entries[^1] : state.Entries[0]);
            }
        }
        finally { _updating = false; }
    }

    private void OnScrollChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_updating || _disposed || _scroll is null || args.IsIntermediate) return;
        _controller.SetFollowingLatest(_scroll.VerticalOffset >= _scroll.ScrollableHeight - 4);
    }

    private void ReleaseText()
    {
        _updating = true;
        ClearRenderedText();
        _entries.ItemsSource = null;
        _sessions.ItemsSource = null;
        _displayedEntries = [];
        _displayedSessions = [];
        _status.Text = "";
        _updating = false;
    }

    private void ClearRenderedText()
    {
        for (var i = 0; i < _entries.Items.Count; i++)
        {
            if (_entries.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: StackPanel row })
                foreach (var child in row.Children.OfType<TextBlock>()) child.Text = "";
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private sealed record SessionChoice(TranscriptSessionInfo Session)
    {
        public override string ToString() =>
            $"{SessionSourcePresentation.Describe(Session.Selection.Source)} · {Session.Selection.SessionId}" +
            $" · stream {Session.Selection.StreamId}" + (Session.Closed ? " · ended/closed (retained)" : "");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scroll is not null) _scroll.ViewChanged -= OnScrollChanged;
        _controller.Changed -= OnChanged;
        _controller.Dispose();
        ReleaseText();
    }
}
