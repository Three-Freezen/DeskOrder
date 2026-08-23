using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// Floating, topmost shell that hosts the same PropertyPanel + tab strip used in the
/// docked panel area. Exposes Body / Tabs so callers (e.g. zone right-click undock)
/// can drive content; Closing is re-exposed so the host can persist window placement.
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

    bool _isClosing;
    Storyboard? _closeSb;

    // ── Close animation ──

    void BuildCloseStoryboard()
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation(1, 0, (Duration)FindResource("Motion.Fast"))
        {
            EasingFunction = (IEasingFunction)FindResource("Motion.StandardSpline")
        };
        Storyboard.SetTarget(fade, this);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);

        var scaleX = new DoubleAnimation(1, 0.95, (Duration)FindResource("Motion.Fast"))
        {
            EasingFunction = (IEasingFunction)FindResource("Motion.StandardSpline")
        };
        Storyboard.SetTarget(scaleX, RootScale);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
        sb.Children.Add(scaleX);

        var scaleY = new DoubleAnimation(1, 0.95, (Duration)FindResource("Motion.Fast"))
        {
            EasingFunction = (IEasingFunction)FindResource("Motion.StandardSpline")
        };
        Storyboard.SetTarget(scaleY, RootScale);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleY);

        sb.Completed += (_, _) =>
        {
            if (_isClosing) Close();
        };
        _closeSb = sb;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isClosing) return;
        if (WindowState == WindowState.Minimized) return;

        e.Cancel = true;
        _isClosing = true;
        Opacity = 1;
        RootScale.ScaleX = 1;
        RootScale.ScaleY = 1;
        _closeSb?.Begin();
    }

    // ── Constructors ──

    public PropertyWindow(object target, ConfigService configService)
    {
        InitializeComponent();
        BuildCloseStoryboard();
        Closed += (_, _) => { _isClosing = false; TabStrip.CancelDrag(); };
        Target = target;
        Title = target is Zone z ? z.Name : target?.GetType().Name;
    }

    public PropertyWindow()
    {
        InitializeComponent();
        BuildCloseStoryboard();
        Closed += (_, _) => { _isClosing = false; TabStrip.CancelDrag(); };
    }

    // ── Target sync ──

    void OnTargetChanged()
    {
        Body.Target = Target;
        if (Target != null)
            TabStrip.OpenOrFocus(
                PropertyWindowManager.TargetKey(Target),
                Target is Zone z ? z.Name : Target.GetType().Name,
                Target is Zone ? "Icon.Zones" : "Icon.Settings");
    }

    public new event CancelEventHandler? Closing
    {
        add => base.Closing += value;
        remove => base.Closing -= value;
    }

    public event EventHandler<DockBackEventArgs>? DockBackRequested;

    // ── Title-bar drag — Win32 move during drag, WPF sync on release only ──

    Point _dragStartScreen;
    double _dragLeft, _dragTop; // tracked position during drag (avoids WPF overhead)
    bool _isDragging;
    bool _dockBackRaised;

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragLeft = Left;
        _dragTop = Top;
        _isDragging = true;
        _dockBackRaised = false;
    }

    void TitleBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;

        var cursorScreen = PointToScreen(e.GetPosition(this));
        var dx = cursorScreen.X - _dragStartScreen.X;
        var dy = cursorScreen.Y - _dragStartScreen.Y;

        // Track logical position (cheap — just field assignments).
        _dragLeft += dx;
        _dragTop += dy;
        _dragStartScreen = cursorScreen;

        // Win32 move — bypasses WPF layout/render pipeline, no stutter.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                (int)_dragLeft, (int)_dragTop, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Real-time dock-back detection using tracked position.
        var bounds = new Rect(_dragLeft, _dragTop, ActualWidth, ActualHeight);
        if (!bounds.Contains(cursorScreen) && !_dockBackRaised)
        {
            _dockBackRaised = true;
            var args = new DockBackEventArgs(cursorScreen);
            DockBackRequested?.Invoke(this, args);
            if (args.Handled)
            {
                _isDragging = false;
                _dockBackRaised = false;
            }
        }
    }

    void TitleBar_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        _dockBackRaised = false;

        // Sync WPF properties once on release (so persistence/bindings work).
        Left = _dragLeft;
        Top = _dragTop;
    }

    // ── Safety nets ──

    void Window_Deactivated(object sender, EventArgs e)
    {
        TabStrip.CancelDrag();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            TabStrip.CancelDrag();
    }
}

public class DockBackEventArgs : EventArgs
{
    public Point CursorScreen { get; }
    public bool Handled { get; set; }
    public DockBackEventArgs(Point cursorScreen) { CursorScreen = cursorScreen; }
}
