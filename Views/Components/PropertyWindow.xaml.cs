using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
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

    // ── Title-bar drag — Timer-driven cursor polling + Win32 move ──
    // ponytail: previous design drove moves off PreviewMouseMove, which on WPF
    // fires at the mouse's input rate (125-1000Hz). SetWindowPos + WM_WINDOWPOSCHANGED
    // + render invalidation on each tick saturated the UI thread and produced
    // visible stutter ("拖动一卡一卡的"). The new design polls Win32 cursor state
    // on a 16ms timer (locked to the render rate) and uses GetAsyncKeyState for
    // release detection — works across all windows, no Mouse.Capture, no routed-
    // event dependency.

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    const int VK_LBUTTON = 0x01;

    DispatcherTimer? _dragTimer;
    Point _dragGrabOffset;          // cursor - window-Left/Top at drag start
    double _dragLeft, _dragTop;     // tracked position during drag
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
        // Snapshot initial cursor (screen) and offset relative to window.
        GetCursorPos(out var pt);
        var cursorScreen = new Point(pt.X, pt.Y);
        _dragGrabOffset = new Point(cursorScreen.X - Left, cursorScreen.Y - Top);
        _dragLeft = Left;
        _dragTop = Top;
        _isDragging = true;
        _dockBackRaised = false;
        StartDragTimer();
    }

    void StartDragTimer()
    {
        if (_dragTimer != null) return;
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += OnDragTick;
        _dragTimer.Start();
    }

    void StopDragTimer()
    {
        if (_dragTimer == null) return;
        _dragTimer.Stop();
        _dragTimer.Tick -= OnDragTick;
        _dragTimer = null;
    }

    void OnDragTick(object? sender, EventArgs e)
    {
        if (!_isDragging) { StopDragTimer(); return; }

        // Release detection via Win32 — works regardless of cursor location.
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
        {
            Left = _dragLeft;
            Top = _dragTop;
            _isDragging = false;
            StopDragTimer();
            return;
        }

        if (!GetCursorPos(out var pt)) return;
        var cursorScreen = new Point(pt.X, pt.Y);

        // Win32 move — bypasses WPF layout/render pipeline.
        _dragLeft = cursorScreen.X - _dragGrabOffset.X;
        _dragTop = cursorScreen.Y - _dragGrabOffset.Y;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                (int)_dragLeft, (int)_dragTop, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        // Dock-back detection: dragged window's own bounds check removed (it
        // fired whenever cursor left the dragged window — wrong). Subscribers
        // (PropertyWindowManager) check the main window's right-column zone
        // themselves; one shot per drag.
        if (!_dockBackRaised)
        {
            var args = new DockBackEventArgs(cursorScreen);
            DockBackRequested?.Invoke(this, args);
            if (args.Handled)
            {
                _isDragging = false;
                StopDragTimer();
                return;
            }
            _dockBackRaised = true;
        }
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
