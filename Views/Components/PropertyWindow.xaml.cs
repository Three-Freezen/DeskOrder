using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// Floating, topmost shell that hosts the same PropertyPanel + tab strip used in the
/// docked panel area. Exposes Body / Tabs so callers (e.g. zone right-click undock)
/// can drive content; Closing is re-exposed so the host can persist window placement.
/// ponytail: no drag/resize logic here — the caller owns placement and lifetime, this
/// is just a content host. Drag a Border over the title bar in Task 13+ if needed.
/// </summary>
public partial class PropertyWindow : Window
{
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(object), typeof(PropertyWindow),
        new PropertyMetadata(null, (d, _) => ((PropertyWindow)d).OnTargetChanged()));

    public PropertyPanel Body => BodyPanel;
    public PropertyTabStrip Tabs => TabStrip;

    public object? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    public PropertyWindow(object target, ConfigService configService)
    {
        InitializeComponent();
        Target = target;
        Title = target is Zone z ? z.Name : target?.GetType().Name;
    }

    public PropertyWindow()
    {
        InitializeComponent();
    }

    void OnTargetChanged() { Body.Target = Target; }

    // ponytail: re-expose Closing verbatim so subscribers can save Left/Top/Width/Height
    // without touching Window's protected base event API.
    public new event CancelEventHandler? Closing
    {
        add => base.Closing += value;
        remove => base.Closing -= value;
    }

    /// <summary>Fires when the user drags the title bar out of the window bounds
    /// (typically toward the main window's right column). Host decides whether
    /// the drop target is a valid dock slot and reacts accordingly.</summary>
    public event EventHandler<DockBackEventArgs>? DockBackRequested;

    /// <summary>Title-bar drag. Wired as PreviewMouseLeftButtonDown (tunnel) so
    /// the gesture reaches the root even when the click lands on a tab/Button
    /// inside PropertyTabStrip. Custom handler — not WPF's DragMove — so we
    /// can detect "drag out of window bounds" and route to DockBackRequested.
    /// Double-click still toggles maximize.</summary>
    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _isDragging = true;
        CaptureMouse();
    }

    Point _dragStartScreen;
    bool _isDragging;
    bool _dockBackRaised;

    void TitleBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var cursorScreen = PointToScreen(e.GetPosition(this));
        var dx = cursorScreen.X - _dragStartScreen.X;
        var dy = cursorScreen.Y - _dragStartScreen.Y;

        // Always move the window manually — never call DragMove() which blocks.
        Left += dx;
        Top += dy;
        _dragStartScreen = cursorScreen;

        // Once cursor leaves window bounds, try dock-back exactly once.
        var bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        if (!bounds.Contains(cursorScreen) && !_dockBackRaised)
        {
            _dockBackRaised = true;
            var args = new DockBackEventArgs(cursorScreen);
            DockBackRequested?.Invoke(this, args);
            if (args.Handled)
            {
                // Host accepted dock — stop dragging.
                ReleaseMouseCapture();
                _isDragging = false;
            }
        }
    }

    void TitleBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging)
        {
            ReleaseMouseCapture();
            _isDragging = false;
            _dockBackRaised = false;
        }
    }
}

/// <summary>Carrier for DockBackRequested so the host can report back whether
/// it handled the dock (and therefore wants the floating window to close).</summary>
public class DockBackEventArgs : EventArgs
{
    public Point CursorScreen { get; }
    public bool Handled { get; set; }
    public DockBackEventArgs(Point cursorScreen) { CursorScreen = cursorScreen; }
}