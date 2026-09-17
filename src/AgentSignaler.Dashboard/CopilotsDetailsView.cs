using System.Collections.ObjectModel;
using AgentSignaler.Contracts;
using AgentSignaler.Service;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;

namespace AgentSignaler.Dashboard;

internal sealed class CopilotsDetailsView : IDisposable
{
    private readonly CopilotsController _controller;
    private readonly ITranscriptReader? _reader;
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<Row> _rows = [];
    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock _summary = MainWindow.Text("");
    private readonly CheckBox _retained = new() { Content = "Include retained history", IsChecked = true };
    private readonly Grid _listRoot = new() { RowSpacing = 8 };
    private readonly Grid _drill = new() { RowSpacing = 8, Visibility = Visibility.Collapsed };
    private TranscriptDetailsView? _transcript;
    private MachineView? _machine;
    private bool _visible, _disposed, _updating;
    private int _queued;

    public CopilotsDetailsView(ITranscriptReader? reader, DispatcherQueue dispatcher, MachineView machine)
    {
        _reader = reader;
        _dispatcher = dispatcher;
        _machine = machine;
        _controller = new(reader);
        Root = new Grid { Padding = new Thickness(0, 12, 0, 0) };
        _listRoot.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _listRoot.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _listRoot.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        _listRoot.Children.Add(new ScrollViewer { Content = _summary, MaxHeight = 100 });
        Grid.SetRow(_retained, 1);
        _listRoot.Children.Add(_retained);
        _list.ItemsSource = _rows;
        _list.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Spacing="4" Margin="0,8,12,12">
                <TextBlock TextWrapping="Wrap" FontWeight="SemiBold" />
                <TextBlock TextWrapping="Wrap" />
                <TextBlock TextWrapping="Wrap" />
                <TextBlock TextWrapping="Wrap" />
                <TextBlock TextWrapping="Wrap" />
                <Button Content="View transcript" />
              </StackPanel>
            </DataTemplate>
            """);
        _list.ContainerContentChanging += (_, args) =>
        {
            RenderContainer(args.ItemContainer, args.InRecycleQueue ? null : args.Item as Row);
            args.Handled = true;
        };
        _list.SelectionChanged += (_, _) =>
        {
            if (!_updating) _controller.Select((_list.SelectedItem as Row)?.Value.Key);
        };
        _retained.Checked += (_, _) => Render();
        _retained.Unchecked += (_, _) => Render();
        AutomationProperties.SetName(_list, "Copilot sessions and latest accepted status events");
        Grid.SetRow(_list, 2);
        _listRoot.Children.Add(_list);
        _drill.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _drill.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var back = new Button { Content = "Back to Copilots" };
        back.Click += (_, _) => Back();
        _drill.Children.Add(back);
        Root.Children.Add(_listRoot);
        Root.Children.Add(_drill);
        _controller.Changed += OnChanged;
        _controller.SetMachine(machine);
    }

    public Grid Root { get; }

    public async Task SetVisibleAsync(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (visible) await _controller.ShowAsync();
        else
        {
            CloseTranscript();
            _controller.Hide();
        }
    }

    public void SetMachine(MachineView? machine)
    {
        if (_disposed) return;
        _machine = machine;
        _transcript?.SetMachine(machine);
        if (machine is null) CloseTranscript();
        _controller.SetMachine(machine);
    }

    private async void OnViewTranscript(object sender, RoutedEventArgs args)
    {
        if (_disposed || !_visible || _machine is null || sender is not Button { Tag: Row row }) return;
        var current = _controller.State.Rows.FirstOrDefault(item => item.Key == row.Value.Key);
        if (current is null) return;
        _controller.Select(current.Key);
        _list.SelectedItem = row;
        CloseTranscript();
        _listRoot.Visibility = Visibility.Collapsed;
        _drill.Visibility = Visibility.Visible;
        if (_reader is null)
        {
            var message = MainWindow.Text("Transcript receiver unavailable. Status reporting remains available.");
            Grid.SetRow(message, 1);
            _drill.Children.Add(message);
            return;
        }
        var selection = current.Streams.FirstOrDefault()?.Selection ??
            new TranscriptSelection(current.MachineId, current.Source, current.SessionId, Guid.Empty);
        _transcript = new(_reader, _dispatcher, selection, _machine.State == AgentState.Offline);
        Grid.SetRow(_transcript.Root, 1);
        _drill.Children.Add(_transcript.Root);
        await _transcript.SetVisibleAsync(true);
    }

    private void Back()
    {
        CloseTranscript();
        Render();
        _list.Focus(FocusState.Programmatic);
    }

    private void CloseTranscript()
    {
        _transcript?.Dispose();
        _transcript = null;
        while (_drill.Children.Count > 1) _drill.Children.RemoveAt(1);
        _drill.Visibility = Visibility.Collapsed;
        _listRoot.Visibility = Visibility.Visible;
    }

    private void OnChanged()
    {
        if (_disposed) return;
        if (_dispatcher.HasThreadAccess) Render();
        else if (Interlocked.Exchange(ref _queued, 1) == 0 &&
            !_dispatcher.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                Interlocked.Exchange(ref _queued, 0);
                if (!_disposed) Render();
            }))
            Interlocked.Exchange(ref _queued, 0);
    }

    private void Render()
    {
        if (_disposed) return;
        var state = _controller.State;
        _summary.Text = (state.Loading ? "Loading retained metadata… " : "") + state.Message +
            (state.Rows.IsEmpty ? " No Copilot sessions observed." : "");
        var rows = state.Rows.Where(row => _retained.IsChecked == true || !row.RetainedOnly).ToArray();
        _updating = true;
        try
        {
            var keys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
            for (var i = _rows.Count - 1; i >= 0; i--)
                if (!keys.Contains(_rows[i].Value.Key)) _rows.RemoveAt(i);
            for (var i = 0; i < rows.Length; i++)
            {
                var existing = _rows.FirstOrDefault(row => row.Value.Key == rows[i].Key);
                if (existing is null) _rows.Insert(i, new(rows[i]));
                else
                {
                    var index = _rows.IndexOf(existing);
                    if (index != i) _rows.Move(index, i);
                    existing.Update(rows[i]);
                }
            }
            _list.SelectedItem = _rows.FirstOrDefault(row => row.Value.Key == state.SelectedKey);
            foreach (var row in _rows)
                if (_list.ContainerFromItem(row) is ListViewItem container) RenderContainer(container, row);
        }
        finally { _updating = false; }
    }

    private void RenderContainer(SelectorItem container, Row? row)
    {
        if (container.ContentTemplateRoot is not StackPanel panel || panel.Children[^1] is not Button button) return;
        var value = row?.Value;
        string[] labels = [value?.Heading ?? "", value?.Status ?? "", value?.LastEvent ?? "",
            value?.EventTime ?? "", value?.TranscriptStatus ?? ""];
        for (var i = 0; i < labels.Length; i++) ((TextBlock)panel.Children[i]).Text = labels[i];
        AutomationProperties.SetName(container, string.Join(". ", labels));
        AutomationProperties.SetName(button, value is null ? "" : $"View transcript for {value.Heading}");
        button.Click -= OnViewTranscript;
        button.Tag = row;
        if (row is not null) button.Click += OnViewTranscript;
    }

    private sealed class Row(CopilotRow value)
    {
        public CopilotRow Value { get; private set; } = value;
        public void Update(CopilotRow value) => Value = value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseTranscript();
        _controller.Changed -= OnChanged;
        _controller.Dispose();
        _list.ItemsSource = null;
        _rows.Clear();
    }
}
