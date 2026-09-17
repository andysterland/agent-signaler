using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace AgentSignaler.Dashboard;

internal sealed class CompactHoverPreview : IDisposable
{
    private readonly FrameworkElement _target;
    private readonly Flyout _flyout;
    private readonly DispatcherQueueTimer _timer;
    private readonly PointerEventHandler _pressed;
    private bool _hovering;
    private bool _disposed;

    public CompactHoverPreview(FrameworkElement target, FrameworkElement content)
    {
        _target = target;
        _flyout = new Flyout
        {
            Content = content,
            Placement = FlyoutPlacementMode.Left,
            ShowMode = FlyoutShowMode.Transient,
            // A compact window is only 64 DIPs wide; its preview must use a separate popup window.
            ShouldConstrainToRootBounds = false,
            AreOpenCloseAnimationsEnabled = false,
            LightDismissOverlayMode = LightDismissOverlayMode.Off,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(Control.BorderThicknessProperty, new Thickness(0)),
                    new Setter(FrameworkElement.MaxWidthProperty, 370d),
                    new Setter(Control.IsTabStopProperty, false),
                    new Setter(UIElement.IsHitTestVisibleProperty, false)
                }
            }
        };
        _timer = target.DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.IsRepeating = false;
        _timer.Tick += Show;
        _pressed = OnPointerPressed;
        target.PointerEntered += OnPointerEntered;
        target.PointerExited += OnPointerExited;
        target.Unloaded += OnUnloaded;
        target.AddHandler(UIElement.PointerPressedEvent, _pressed, handledEventsToo: true);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
        _hovering = true;
        if (!_flyout.IsOpen) _timer.Start();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (_disposed || !_target.IsLoaded)
        {
            Hide();
            return;
        }
        var position = args.GetCurrentPoint(_target).Position;
        if (position.X >= 0 && position.Y >= 0 && position.X < _target.ActualWidth && position.Y < _target.ActualHeight)
            return;
        Hide();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args) => Hide();
    private void OnUnloaded(object sender, RoutedEventArgs args) => Hide();

    private void Show(DispatcherQueueTimer sender, object args)
    {
        if (!_disposed && _hovering && _target.IsLoaded && _target.XamlRoot is not null)
            _flyout.ShowAt(_target);
    }

    public void Hide()
    {
        _hovering = false;
        _timer.Stop();
        _flyout.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        _timer.Tick -= Show;
        _target.PointerEntered -= OnPointerEntered;
        _target.PointerExited -= OnPointerExited;
        _target.Unloaded -= OnUnloaded;
        _target.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
    }
}
