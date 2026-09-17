using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace AgentSignaler.Dashboard;

internal sealed class CompactHoverPreview : IDisposable
{
    private readonly FrameworkElement _target;
    private readonly Flyout _flyout;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _dismissTimer;
    private readonly nint _window;
    private readonly PointerEventHandler _pressed;
    private bool _hovering;
    private bool _disposed;

    public CompactHoverPreview(FrameworkElement target, FrameworkElement content, nint window)
    {
        _target = target;
        _window = window;
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
        // Unconstrained flyouts use another HWND, so XAML can miss the owner's pointer exit.
        _dismissTimer = target.DispatcherQueue.CreateTimer();
        _dismissTimer.Interval = TimeSpan.FromMilliseconds(100);
        _dismissTimer.IsRepeating = true;
        _dismissTimer.Tick += CheckPointer;
        _flyout.Closed += OnFlyoutClosed;
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
        _dismissTimer.Start();
        if (!_flyout.IsOpen) _timer.Start();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!IsPointerOverTarget()) Hide();
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args) => Hide();
    private void OnUnloaded(object sender, RoutedEventArgs args) => Hide();
    private void OnFlyoutClosed(object? sender, object args) => Hide();

    private void CheckPointer(DispatcherQueueTimer sender, object args)
    {
        if (!IsPointerOverTarget()) Hide();
    }

    private bool IsPointerOverTarget()
    {
        if (_disposed || !_target.IsLoaded || _target.XamlRoot is not { IsHostVisible: true } root)
            return false;

        if (!NativeWindow.GetCursorPos(out var cursor) || !NativeWindow.ScreenToClient(_window, ref cursor))
        {
            Trace.TraceWarning("Compact hover cursor lookup failed (Win32 error {0}).", Marshal.GetLastWin32Error());
            return false;
        }

        var position = new Point(cursor.X / root.RasterizationScale, cursor.Y / root.RasterizationScale);
        var bounds = _target.TransformToVisual(root.Content)
            .TransformBounds(new Rect(0, 0, _target.ActualWidth, _target.ActualHeight));
        return new Rect(0, 0, root.Size.Width, root.Size.Height).Contains(position) && bounds.Contains(position);
    }

    private void Show(DispatcherQueueTimer sender, object args)
    {
        if (_hovering && IsPointerOverTarget())
            _flyout.ShowAt(_target);
        else
            Hide();
    }

    public void Hide()
    {
        _hovering = false;
        _timer.Stop();
        _dismissTimer.Stop();
        if (_flyout.IsOpen) _flyout.Hide();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        _timer.Tick -= Show;
        _dismissTimer.Tick -= CheckPointer;
        _flyout.Closed -= OnFlyoutClosed;
        _target.PointerEntered -= OnPointerEntered;
        _target.PointerExited -= OnPointerExited;
        _target.Unloaded -= OnUnloaded;
        _target.RemoveHandler(UIElement.PointerPressedEvent, _pressed);
    }
}
