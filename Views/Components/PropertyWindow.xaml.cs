using System.ComponentModel;
using System.Runtime.InteropServices;
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
        Closed += (_, _) => { _isClosing = false; TabStrip.CancelDrag(); StopDragLoop(); };
        // ponytail: header X closes the floating window itself (dock-back stays
        // on the toggle button). Close() runs the standard fade+scale animation.
        Body.CloseWindowRequested += (_, _) => Close();
        Target = target;
        Title = PropertyWindowManager.TitleOf(target);
    }

    public PropertyWindow()
    {
        InitializeComponent();
        BuildCloseStoryboard();
        Closed += (_, _) => { _isClosing = false; TabStrip.CancelDrag(); StopDragLoop(); };
        Body.CloseWindowRequested += (_, _) => Close();
    }

    // ── Target sync ──

    void OnTargetChanged()
    {
        Body.Target = Target;
        // ponytail: floating X is visible whenever the window has a target
        // (it closes the window, not the tab — docked semantics stay with the
        // docked host wiring CloseTabRequested).
        Body.IsCloseable = Target != null;
        if (Target != null)
            TabStrip.OpenOrFocus(
                PropertyWindowManager.TargetKey(Target),
                PropertyWindowManager.TitleOf(Target),
                PropertyWindowManager.IconOf(Target));
    }

    public new event CancelEventHandler? Closing
    {
        add => base.Closing += value;
        remove => base.Closing -= value;
    }

    public event EventHandler<DockBackEventArgs>? DockBackRequested;

    // ── Title-bar drag — per-frame cursor polling + Win32 move ──
    // ponytail: the earlier designs both stuttered. PreviewMouseMove fires at the
    // mouse's input rate (125-1000Hz) and saturated the UI thread; the 16ms
    // DispatcherTimer ran at Background priority, so its ticks were delayed and
    // batched under load ("拖动一卡一卡"). Movement is now driven by
    // CompositionTarget.Rendering — exactly one update per compositor frame,
    // vsync-aligned — with GetAsyncKeyState release detection that works
    // regardless of where the cursor is (no Mouse.Capture, no routed events).
    //
    // DPI note: GetCursorPos returns physical pixels while Window.Left/Top are
    // DIPs. The grab offset is therefore computed in physical px (window origin
    // converted via the window's current DPI) and every SetWindowPos is fed
    // physical px. The old mixed math (px - DIPs) made the window drift away
    // from the grab point on scaled displays ("拖动不跟手"), and writing the
    // physical value back into Left/Top on release made it jump once more.

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    const int VK_LBUTTON = 0x01;

    bool _dragLoopActive;
    Point _dragGrabOffsetPx;          // cursor - window origin at drag start, physical px
    double _dragLeftPx, _dragTopPx;   // tracked window origin, physical px
    int _lastMoveX = int.MinValue, _lastMoveY = int.MinValue;
    bool _isDragging;
    bool _dockBackRaised;

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // ponytail: a press on a tab belongs to the tab strip (reorder /
        // drag-out). Arming the window drag too made the whole floating window
        // chase the cursor while the user was dragging a tab — the two drag
        // systems fought each other and the window lurched around.
        if (IsOnPropertyTab(e.OriginalSource as DependencyObject)) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        // Snapshot the initial cursor (screen px) and the window origin converted
        // to the same unit system (physical px).
        GetCursorPos(out var pt);
        var cursorPx = new Point(pt.X, pt.Y);
        var dpi = VisualTreeHelper.GetDpi(this);
        var leftPx = Left * dpi.DpiScaleX;
        var topPx = Top * dpi.DpiScaleY;
        _dragGrabOffsetPx = new Point(cursorPx.X - leftPx, cursorPx.Y - topPx);
        _dragLeftPx = leftPx;
        _dragTopPx = topPx;
        _lastMoveX = int.MinValue;
        _lastMoveY = int.MinValue;
        _isDragging = true;
        _dockBackRaised = false;
        StartDragLoop();
    }

    /// <summary>True when the press landed on a tab item (or anything inside it).</summary>
    static bool IsOnPropertyTab(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: PropertyTab })
                return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    void StartDragLoop()
    {
        if (_dragLoopActive) return;
        _dragLoopActive = true;
        CompositionTarget.Rendering += OnDragFrame;
    }

    void StopDragLoop()
    {
        if (!_dragLoopActive) return;
        _dragLoopActive = false;
        CompositionTarget.Rendering -= OnDragFrame;
    }

    void OnDragFrame(object? sender, EventArgs e)
    {
        // Window closed / visual detached mid-drag — bail out.
        if (PresentationSource.FromVisual(this) == null)
        {
            _isDragging = false;
            StopDragLoop();
            return;
        }

        if (!_isDragging) { StopDragLoop(); return; }

        // Release detection via Win32 — works regardless of cursor location.
        if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0)
        {
            // Land exactly under the cursor before letting go (all physical px,
            // no DIP mixing). WPF picks Left/Top (DIPs) up from the
            // WM_WINDOWPOSCHANGED this move sends.
            if (GetCursorPos(out var rel))
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                        (int)(rel.X - _dragGrabOffsetPx.X), (int)(rel.Y - _dragGrabOffsetPx.Y),
                        0, 0,
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOREDRAW);
            }
            _isDragging = false;
            StopDragLoop();
            return;
        }

        if (!GetCursorPos(out var pt)) return;
        var cursorScreen = new Point(pt.X, pt.Y);

        _dragLeftPx = pt.X - _dragGrabOffsetPx.X;
        _dragTopPx = pt.Y - _dragGrabOffsetPx.Y;
        int x = (int)_dragLeftPx, y = (int)_dragTopPx;

        // Skip no-op moves — each SetWindowPos round-trips through WPF's
        // WM_WINDOWPOSCHANGED, and doing that redundantly every frame adds jank.
        if (x != _lastMoveX || y != _lastMoveY)
        {
            _lastMoveX = x;
            _lastMoveY = y;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // ponytail 2026-08-25: SWP_NOREDRAW keeps WPF from re-rasterizing
                // the layered window surface on every move (the other historical
                // "拖动一卡一卡" source).
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOREDRAW);
            }
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
                StopDragLoop();
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
