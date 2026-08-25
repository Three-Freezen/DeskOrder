using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views.Components;

/// <summary>
/// One tab in the property strip. Key is the stable identity (e.g. "zone-{guid}");
/// Title is the display label; IsPinned=true means a long-lived tab, false means a
/// transient preview tab. IsActive is set by PropertyTabStrip when this tab is the
/// selected one and drives the visual selected state via DataTrigger.
/// </summary>
public class PropertyTab : INotifyPropertyChanged
{
    string _title = "";
    public string Key     { get; set; } = "";
    public string IconKey { get; set; } = "Icon.Zones";
    public ICommand CloseCommand { get; set; } = null!;

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(DisplayTitle)); }
    }

    bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned == value) return; _isPinned = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsPreview)); }
    }
    public bool IsPreview => !_isPinned;

    bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { if (_isActive == value) return; _isActive = value; OnPropertyChanged(); }
    }

    public string DisplayTitle => _title.Length > 4 ? _title.Substring(0, 4) + "…" : _title;

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Horizontal strip of PropertyTab items: pinned + preview tabs share a row; selected
/// tab gets the accent indicator and Surface background; hover reveals a close-x that
/// invokes CloseCommand on the tab. Tabs is the source of truth (ObservableCollection);
/// OpenOrFocus / PinTab / CloseTab mutate it and update IsActive in lockstep.
///
/// ponytail: drag is driven by a DispatcherTimer that polls Win32 cursor state
/// (GetCursorPos + GetAsyncKeyState) — no Mouse.Capture, no routed-event
/// preview dependency. The previous design relied on PreviewMouseLeftButtonUp
/// tunnelling through RootBorder, which silently stops when the cursor leaves
/// the source window's visual tree (the user's "窗口拖不出来" symptom). Win32
/// polling doesn't care where the cursor is — desktop, other app, anywhere.
/// </summary>
public partial class PropertyTabStrip : UserControl
{
    public ObservableCollection<PropertyTab> Tabs { get; } = new();

    public static readonly DependencyProperty ActiveTabProperty = DependencyProperty.Register(
        nameof(ActiveTab), typeof(PropertyTab), typeof(PropertyTabStrip),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnActiveTabChanged));
    public PropertyTab? ActiveTab
    {
        get => (PropertyTab?)GetValue(ActiveTabProperty);
        set => SetValue(ActiveTabProperty, value);
    }

    public event EventHandler? ActiveTabChanged;

    // ── Win32 cursor polling ──
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }
    const int VK_LBUTTON = 0x01;

    // ── Drag state ──
    PropertyTab? _dragTab;
    int _dragFromIndex = -1;
    Point _dragOrigin;
    bool _dragArmed;
    bool _dragCompleted;     // guard so reset-on-release only runs once

    PropertyTab? _dragOutTab;
    bool _dragOutArmed;
    bool _isDragOut;         // true once cursor has left the strip during drag-out
    DispatcherTimer? _dragTimer;

    // ── Visible drag (tab follows the cursor) ──
    Border? _dragContainer;          // dragged tab's template root Border
    UIElement? _dragContainerPanel;  // its ContentPresenter (for z-order)
    double _dragGrabOffsetX;

    int _dragInsertIndex = -1;
    bool _isTransferring;
    PropertyTabStrip? _transferTarget;
    double _dragLastCursorX = double.NaN; // previous cursor X — drives the leading-edge probe

    // ── Transfer drop indicator (called by source strip's drag loop) ──
    DropIndicatorAdorner? _dropIndicatorAdorner;
    int _pendingInsertIndex = -1;

    readonly Dictionary<PropertyTab, double> _pendingSlide = new();

    public PropertyTabStrip()
    {
        InitializeComponent();
        TabsHost.ItemsSource = Tabs;
    }

    // ponytail: backward-compat alias — the XAML no longer declares the inner
    // ItemsControl directly; it lives inside PropertyTabScroller.TabsHostInner.
    // All existing `TabsHost` references in this file continue to work.
    ItemsControl TabsHost => TabsScroller.TabsHost;

    /// <summary>Forward to scroller so callers don't need to know about the inner host.</summary>
    public void ScrollIntoView(PropertyTab tab) => TabsScroller.ScrollIntoView(tab);

    /// <summary>Force-cancel any in-progress drag. Called from PropertyWindow on
    /// Deactivated / Escape / Closed to release the timer + popup cleanly.</summary>
    public void CancelDrag()
    {
        if (_dragTab != null)
            ResetDrag();
    }

    static void OnActiveTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var strip = (PropertyTabStrip)d;
        strip.RefreshActiveFlag();
        strip.ActiveTabChanged?.Invoke(strip, EventArgs.Empty);
    }

    void RefreshActiveFlag()
    {
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, ActiveTab);
    }

    // ── Tab click ──

    void TabRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // ponytail: only treat as a click if no drag was armed. Don't reset
        // _dragArmed here — the Timer's drop branch owns that state from now
        // on, and resetting it here would race against the Timer's reorder
        // condition (`_dragArmed && !outsideStrip`).
        if (_dragArmed) return;
        if (sender is Border { DataContext: PropertyTab tab })
            ActiveTab = tab;
    }

    void TabRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { DataContext: PropertyTab tab })
        {
            _dragTab = tab;
            _dragFromIndex = Tabs.IndexOf(tab);
            _dragOrigin = e.GetPosition(this);
            _dragArmed = false;
            _dragCompleted = false;
            _dragOutTab = tab;
            _dragOutArmed = false;
            _isDragOut = false;
            _dragInsertIndex = _dragFromIndex;
            _dragLastCursorX = _dragOrigin.X;

            // Visible drag: the tab follows the cursor. Reset any leftover slide
            // transform, record the grab offset and raise the tab above its siblings.
            _dragContainer = sender as Border;
            if (_dragContainer?.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
            }
            _dragGrabOffsetX = _dragOrigin.X - TabLayoutXInStrip(tab);
            _dragContainerPanel = TabsHost.ItemContainerGenerator.ContainerFromItem(tab) as UIElement;
            if (_dragContainerPanel != null)
                Panel.SetZIndex(_dragContainerPanel, 10);

            StartDragTimer();
        }
    }

    void TabRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is Border { DataContext: PropertyTab tab })
        {
            CloseTab(tab.Key);
            PropertyWindowManager.Instance.CheckEmptyFloatingAndClose(this);
            e.Handled = true;
        }
    }

    // ── Drag loop ──

    void StartDragTimer()
    {
        if (_dragTimer != null) return;
        // ponytail: 16ms ≈ 60fps. Fine for cursor polling; WPF UI thread
        // already serializes layout/render so a 60fps timer doesn't add
        // measurable load.
        _dragTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _dragTimer.Tick += OnDragTick;
        _dragTimer.Start();
    }

    void OnDragTick(object? sender, EventArgs e)
    {
        if (_dragTab == null || _dragCompleted) return;

        GetCursorPos(out POINT pt);
        var screen = new Point(pt.X, pt.Y);
        // PointFromScreen converts physical pixels → DIP strip coords, keeping the
        // follow math consistent with GetPosition/layout widths (all DIPs). Mixing
        // raw Win32 pixels with DIPs made the tab's head anchor at the cursor on
        // scaled displays instead of the grab point.
        var pos = PointFromScreen(screen);
        var stripBounds = new Rect(0, 0, ActualWidth, ActualHeight);
        bool outsideStrip = !stripBounds.Contains(pos);

        // ponytail: arm threshold — Euclidean distance (any direction), not
        // X-only. The previous X-only check left vertical drags invisible
        // (ghost at initial position) and is what produced the "两个小浮窗"
        // symptom (ghost stuck inside + chip outside).
        var dragOriginScreen = PointToScreen(_dragOrigin);
        var dx = screen.X - dragOriginScreen.X;
        var dy = screen.Y - dragOriginScreen.Y;
        if (!_dragArmed && (dx * dx + dy * dy) > 25)
            _dragArmed = true;

        // ponytail: arm drag-out when the cursor leaves the strip (works
        // outside the source window too — strip-local pos goes negative or
        // past ActualWidth/Height when cursor is in another window).
        if (!_dragOutArmed && outsideStrip)
            _dragOutArmed = true;

        // Commit drag-out once armed and the cursor has actually left.
        if (_dragOutArmed && !_isDragOut && outsideStrip)
            _isDragOut = true;

        // Visible drag: the dragged tab tracks the cursor while inside the strip;
        // snap it back to its slot once the cursor leaves.
        if (_dragContainer?.RenderTransform is TranslateTransform tt)
        {
            if (_dragArmed && !outsideStrip && _dragTab != null)
                tt.X = pos.X - _dragGrabOffsetX - TabLayoutXInStrip(_dragTab);
            else
                tt.X = 0;
        }

        // ponytail: cross-window transfer — if drag-out is armed AND cursor
        // is over another strip's hit zone, mark transferring and tell the
        // target to show its drop indicator. Otherwise, leave any previous
        // target.
        if (_dragOutArmed && outsideStrip)
        {
            var target = TabDragRouter.FindDropTarget(screen);
            if (target != null && target != this)
            {
                if (!_isTransferring || target != _transferTarget)
                {
                    if (_isTransferring) _transferTarget?.HandleTransferDragLeave();
                    _isTransferring = true;
                    _transferTarget = target;
                    target.HandleTransferDragEnter(ComputeInsertFor(target, screen));
                }
                else
                {
                    target.HandleTransferDragMove(ComputeInsertFor(target, screen));
                }
            }
            else if (_isTransferring)
            {
                _transferTarget?.HandleTransferDragLeave();
                _isTransferring = false;
                _transferTarget = null;
            }
        }

        // ponytail: live reorder while cursor stays inside the strip.
        if (_dragArmed && !outsideStrip && _dragTab != null)
        {
            // Leading-edge probe, direction-aware: the swap fires once the tab's
            // leading edge (right edge when dragging right, left edge when dragging
            // left) crosses a neighbour's midpoint — 拖过一半即换位. Probing the
            // fixed left edge made rightward drags fire a whole tab-width late
            // (the pointer had to reach the neighbour's far end — 拖到底).
            bool movingRight = pos.X >= _dragLastCursorX;
            _dragLastCursorX = pos.X;
            int newIndex = ComputeDropIndex(ComputeProbeX(pos.X, movingRight));
            if (newIndex >= 0 && newIndex != _dragInsertIndex
                && newIndex != _dragFromIndex && newIndex != _dragFromIndex + 1)
            {
                int target = newIndex;
                if (target > _dragFromIndex) target--;
                CaptureSlidePositions(Math.Min(_dragFromIndex, target), Math.Max(_dragFromIndex, target));
                MoveTab(_dragFromIndex, target);
                _dragFromIndex = target;
                _dragInsertIndex = newIndex;
                Dispatcher.BeginInvoke(new Action(PlaySlideAnimations), DispatcherPriority.Loaded);
            }
        }

        // Detect LButton release via Win32 — fires on the desktop, in other
        // windows, anywhere outside WPF's routed-event reach.
        short state = GetAsyncKeyState(VK_LBUTTON);
        if ((state & 0x8000) != 0) return;     // still pressed — keep going

        _dragCompleted = true;
        try
        {
            // ponytail: transfer takes priority over plain drag-out.
            if (_isTransferring && _transferTarget != null && _dragOutTab != null)
            {
                var key = _dragOutTab.Key;
                _transferTarget.HandleTransferDragLeave();
                PropertyWindowManager.Instance.TransferTab(this, _transferTarget, key);
                var newTab = _transferTarget.Tabs.FirstOrDefault(t => t.Key == key);
                if (newTab != null) _transferTarget.ScrollIntoView(newTab);
                return;
            }

            // Plain drag-out — pop a floating PropertyWindow for the tab.
            if (_dragOutArmed && _dragOutTab != null && _isDragOut)
            {
                HandleDragOutDrop(_dragOutTab, screen);
                return;
            }

            // Reorder drop inside the strip.
            if (_dragArmed && _dragTab != null && _dragFromIndex >= 0 && !outsideStrip)
            {
                int dropIndex = ComputeDropIndex(ComputeProbeX(pos.X, pos.X >= _dragLastCursorX));
                if (dropIndex >= 0 && dropIndex != _dragFromIndex && dropIndex != _dragFromIndex + 1)
                {
                    int target = dropIndex;
                    if (target > _dragFromIndex) target--;
                    MoveTab(_dragFromIndex, target);
                }
                // Settle the dragged tab from wherever the cursor left it into its slot.
                SettleDragContainer();
            }
        }
        finally
        {
            ResetDrag();
        }
    }

    /// <summary>Leading-edge probe in TabsHost coords, direction-aware: the dragged
    /// tab's right edge when <paramref name="movingRight"/>, its left edge otherwise.
    /// In-strip swaps fire when this edge crosses a neighbour's midpoint, so both
    /// drag directions behave symmetrically (拖过一半即换位).</summary>
    double ComputeProbeX(double cursorX, bool movingRight)
    {
        double hostX = TabsHost.TranslatePoint(new Point(0, 0), this).X;
        double probeX = cursorX - hostX - _dragGrabOffsetX; // left edge
        if (movingRight)
        {
            var container = TabsHost.ItemContainerGenerator.ContainerFromItem(_dragTab) as FrameworkElement;
            if (container != null) probeX += container.ActualWidth; // right edge
        }
        return probeX;
    }

    internal int ComputeDropIndex(double x)
    {
        double acc = 0;
        for (int i = 0; i < Tabs.Count; i++)
        {
            var container = (FrameworkElement)TabsHost.ItemContainerGenerator.ContainerFromIndex(i);
            if (container == null) continue;
            var w = container.ActualWidth;
            if (x < acc + w / 2) return i;
            acc += w;
        }
        return Tabs.Count;
    }

    /// <summary>Layout origin (x) of a tab in strip coordinates, computed
    /// analytically from sibling widths so it stays valid regardless of any
    /// slide/follow RenderTransform on the tabs.</summary>
    double TabLayoutXInStrip(PropertyTab tab)
    {
        double hostX = TabsHost.TranslatePoint(new Point(0, 0), this).X;
        double acc = 0;
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (ReferenceEquals(Tabs[i], tab)) break;
            var c = (FrameworkElement)TabsHost.ItemContainerGenerator.ContainerFromIndex(i);
            if (c != null) acc += c.ActualWidth;
        }
        return hostX + acc;
    }

    /// <summary>Animate the dragged tab from wherever the cursor left it back into
    /// its final slot.</summary>
    void SettleDragContainer()
    {
        if (_dragContainer?.RenderTransform is not TranslateTransform tt) return;
        var anim = new DoubleAnimation(tt.X, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        tt.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ── Slide animation (§4.1) ──

    void CaptureSlidePositions(int from, int to)
    {
        _pendingSlide.Clear();
        for (int i = from; i <= to; i++)
        {
            if (i < 0 || i >= Tabs.Count) continue;
            var tab = Tabs[i];
            if (ReferenceEquals(tab, _dragTab)) continue; // dragged tab follows the cursor instead
            var container = (FrameworkElement)TabsHost.ItemContainerGenerator.ContainerFromItem(tab);
            if (container == null) continue;
            var x = container.TranslatePoint(new Point(0, 0), TabsScroller).X;
            _pendingSlide[tab] = x;
        }
    }

    void PlaySlideAnimations()
    {
        foreach (var kv in _pendingSlide)
        {
            var container = (FrameworkElement)TabsHost.ItemContainerGenerator.ContainerFromItem(kv.Key);
            if (container == null) continue;
            var newX = container.TranslatePoint(new Point(0, 0), TabsScroller).X;
            var delta = kv.Value - newX;
            if (Math.Abs(delta) < 0.5) continue;
            // ponytail: container is a ContentPresenter (ItemsControl wraps each
            // non-UIElement item), but the TranslateTransform lives on the inner
            // Border (template root). Animating the container's RenderTransform
            // was a no-op (no transform set on ContentPresenter) — tabs snapped
            // to their new positions with no visible slide. Drill in to the
            // Border so the 160ms ease-out actually plays.
            var border = container as Border ?? VisualTreeHelper.GetChild(container, 0) as Border;
            var transform = border?.RenderTransform as TranslateTransform;
            if (transform == null) continue;
            // ponytail: 160ms ease-out per spec §0 决策 9
            var anim = new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            transform.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        _pendingSlide.Clear();
    }

    // ── Cross-window transfer helper ──

    int ComputeInsertFor(PropertyTabStrip target, Point screenPos)
    {
        if (target.TabsHost == null) return -1;
        var localInHost = target.TabsHost.PointFromScreen(screenPos);
        // Left-edge probe — the transfer indicator marks where the dragged tab's
        // left edge would insert in the target strip.
        return target.ComputeDropIndex(localInHost.X - _dragGrabOffsetX);
    }

    void ResetDrag()
    {
        if (_dragTimer != null)
        {
            _dragTimer.Stop();
            _dragTimer.Tick -= OnDragTick;
            _dragTimer = null;
        }
        if (_isTransferring && _transferTarget != null)
            _transferTarget.HandleTransferDragLeave();
        if (_dragContainerPanel != null)
            Panel.SetZIndex(_dragContainerPanel, 0);
        _dragContainerPanel = null;
        _dragContainer = null;
        _dragTab = null;
        _dragFromIndex = -1;
        _dragArmed = false;
        _dragCompleted = false;
        _dragOutTab = null;
        _dragOutArmed = false;
        _isDragOut = false;
        _isTransferring = false;
        _transferTarget = null;
        _dragInsertIndex = -1;
        _dragLastCursorX = double.NaN;
    }

    void HandleDragOutDrop(PropertyTab tab, Point screenPos)
    {
        // ponytail: locate ManagementWindow regardless of which window hosts
        // this strip. `Window.GetWindow(this)` returns the immediate parent
        // (could be a floating PropertyWindow), whose `as ManagementWindow`
        // cast fails → method silently returned → drag-out from a floating
        // window did nothing. Walk Application.Windows instead.
        var main = Application.Current.Windows.OfType<ManagementWindow>().FirstOrDefault();
        if (main == null) return;

        var sep = tab.Key.IndexOf(':');
        if (sep < 0) return;
        var typeName = tab.Key.Substring(0, sep);
        var idStr = tab.Key.Substring(sep + 1);

        object? target;
        if (typeName == nameof(PanelConfig))
        {
            // ponytail 2026-08-25: Panel singleton uses the literal "panel" key
            // (no Guid) — resolve via the live AppConfig's Panel POCO.
            target = main.LiveConfig.Panel;
        }
        else
        {
            if (!Guid.TryParse(idStr, out var id)) return;
            target = typeName switch
            {
                nameof(Zone) => main.Zones.FirstOrDefault(z => z.Id == id),
                nameof(DesktopClock) => main.WidgetService?.Clocks.FirstOrDefault(c => c.Id == id),
                nameof(DesktopCalendar) => main.WidgetService?.Calendars.FirstOrDefault(c => c.Id == id),
                nameof(StickyNote) => main.NotesService?.Notes.FirstOrDefault(n => n.Id == id),
                nameof(MergedGroupTarget) => (main.Zones.FirstOrDefault(z =>
                    z.MergedGroupMembership.GroupId == id && z.MergedGroupMembership.SubZoneIds.Count > 0)
                    is { } m ? MergedGroupTarget.For(m) : null),
                _ => null,
            };
        }

        if (target == null) return;

        // ponytail: pass the cursor's screen position so the new floating
        // PropertyWindow opens at the drop point (not at the right-edge fallback
        // in ResolvePopPosition), and inherit the docked panel's dimensions so
        // the popped-out window is the same shape as the area the tab came
        // from. Fall back to 360×480 if the docked panel hasn't been measured
        // yet (ActualWidth/Height = 0).
        var w = main.DockedPanel?.ActualWidth ?? 0;
        var h = main.DockedPanel?.ActualHeight ?? 0;
        if (w <= 0) w = 360;
        if (h <= 0) h = 480;
        main.OpenFloatingProperty(target, screenPos, new Size(w, h));
        CloseTab(tab.Key);
    }

    // ── Public tab management ──

    public void OpenOrFocus(string key, string title, string iconKey)
    {
        foreach (var t in Tabs)
            if (t.Key == key) { ActiveTab = t; return; }

        var tab = new PropertyTab
        {
            Key = key,
            Title = title,
            IconKey = iconKey,
            IsPinned = false,
            CloseCommand = new RelayCommand(_ => CloseTab(key), _ => true),
        };
        Tabs.Add(tab);
        ActiveTab = tab;
    }

    public void PinTab(string key)
    {
        foreach (var t in Tabs)
            if (t.Key == key) { t.IsPinned = true; return; }
    }

    public void CloseTab(string key)
    {
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].Key != key) continue;
            bool wasActive = ReferenceEquals(Tabs[i], ActiveTab);
            Tabs.RemoveAt(i);
            if (!wasActive) { RefreshActiveFlag(); return; }
            if (i > 0) ActiveTab = Tabs[i - 1];
            else if (Tabs.Count > 0) ActiveTab = Tabs[0];
            else ActiveTab = null;
            return;
        }
    }

    public bool CloseActiveTab()
    {
        if (ActiveTab == null) return false;
        CloseTab(ActiveTab.Key);
        return true;
    }

    public void CloseAllPreviewTabs()
    {
        for (int i = Tabs.Count - 1; i >= 0; i--)
            if (!Tabs[i].IsPinned) Tabs.RemoveAt(i);
        RefreshActiveFlag();
    }

    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count) return;
        if (to < 0 || to >= Tabs.Count) return;
        if (from == to) return;
        Tabs.Move(from, to);
        RefreshActiveFlag();
    }

    // ── Transfer drop indicator (public, called by source strip's drag loop) ──

    public void HandleTransferDragEnter(int insertIndex)
    {
        EnsureDropIndicator();
        if (_dropIndicatorAdorner == null) return;
        _pendingInsertIndex = insertIndex;
        _dropIndicatorAdorner.InvalidateVisual();
    }

    public void HandleTransferDragMove(int insertIndex)
    {
        _pendingInsertIndex = insertIndex;
        _dropIndicatorAdorner?.InvalidateVisual();
    }

    public void HandleTransferDragLeave()
    {
        // ponytail: hide by clearing the index and invalidating — the adorner
        // simply draws nothing when index is -1.
        _pendingInsertIndex = -1;
        _dropIndicatorAdorner?.InvalidateVisual();
    }

    void EnsureDropIndicator()
    {
        if (_dropIndicatorAdorner != null || TabsScroller == null) return;
        var layer = AdornerLayer.GetAdornerLayer(TabsScroller);
        if (layer == null) return;
        _dropIndicatorAdorner = new DropIndicatorAdorner(TabsScroller, this);
        layer.Add(_dropIndicatorAdorner);
    }

    sealed class DropIndicatorAdorner : Adorner
    {
        readonly PropertyTabStrip _owner;
        public DropIndicatorAdorner(UIElement adorned, PropertyTabStrip owner) : base(adorned)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }
        protected override void OnRender(DrawingContext dc)
        {
            var idx = _owner._pendingInsertIndex;
            var host = _owner.TabsScroller;
            if (idx < 0 || host == null || _owner.TabsHost == null) return;
            double x;
            if (idx >= _owner.Tabs.Count)
                x = host.ActualWidth - 1;
            else
            {
                var container = (FrameworkElement)_owner.TabsHost.ItemContainerGenerator.ContainerFromIndex(idx);
                if (container == null) return;
                var p = container.TranslatePoint(new Point(0, 0), host);
                x = p.X - 1;
            }
            var brush = (System.Windows.Media.Brush)_owner.FindResource("Brush.Accent");
            dc.DrawRectangle(brush, null, new Rect(x, 4, 2, host.ActualHeight - 8));
        }
    }
}