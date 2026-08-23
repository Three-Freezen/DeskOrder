using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

    // ── Drag-to-reorder state ──
    PropertyTab? _dragTab;
    int _dragFromIndex = -1;
    Point _dragOrigin;
    bool _dragArmed;

    // ── Drag-to-float state ──
    PropertyTab? _dragOutTab;
    bool _dragOutArmed;
    bool _isDragOut;           // true when cursor left the strip during drag-out
    Window? _dragOutFeedback;

    // ── Drag visual + transfer state ──
    PropertyTabGhost? _dragGhost;
    AdornerLayer? _dragGhostLayer;
    int _dragInsertIndex = -1;
    bool _isTransferring;
    PropertyTabStrip? _transferTarget;

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

    /// <summary>Force-cancel any in-progress drag and clean up the feedback window.
    /// Called from PropertyWindow on Deactivated / Escape to prevent orphaned windows.</summary>
    public void CancelDrag()
    {
        if (_dragOutFeedback != null || _dragTab != null)
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

    // ── Tab click / drag origin ──

    void TabRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragArmed) { _dragArmed = false; return; }
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
            _dragOutTab = tab;
            _dragOutArmed = false;
            _isDragOut = false;
            _dragInsertIndex = _dragFromIndex;

            // ponytail: capture mouse so MouseMove/MouseUp keep routing here
            // even when the cursor leaves the source window. Without this, the
            // drag-out chip freezes in place and MouseUp never reaches
            // HandlePreviewMouseLeftButtonUp when the user releases on the
            // desktop or another window.
            Mouse.Capture(this);

            // ponytail: create the ghost once on press so the user sees
            // immediate pickup feedback even before threshold is crossed.
            // Hide it until _dragArmed fires (5px threshold) to keep clicks
            // totally clean — only drags get a ghost.
            _dragGhostLayer = AdornerLayer.GetAdornerLayer(TabsScroller);
            if (_dragGhostLayer != null)
            {
                _dragGhost = new PropertyTabGhost(TabsScroller, tab);
                _dragGhost.Visibility = Visibility.Collapsed;
                _dragGhostLayer.Add(_dragGhost);
            }
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

    // ponytail: sole drag-MouseMove handler. Active whenever Mouse.Capture is
    // held by this strip — which is set in TabRoot_MouseLeftButtonDown so every
    // drag has capture. Capture redirects MouseMove to the strip regardless of
    // cursor position, so the previous TabRoot_MouseMove handler on the inner
    // tab Border stopped firing under capture and silently killed all drag
    // logic (arm / ghost / chip-create / reorder / drop-target / commit).
    // Consolidated here so the whole state machine runs on every tick.
    void TabStrip_MouseMove_Captured(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured != this) return;
        if (_dragTab == null || _dragFromIndex < 0) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        var stripBounds = new Rect(0, 0, ActualWidth, ActualHeight);
        bool outsideStrip = !stripBounds.Contains(pos);

        if (!_dragArmed && Math.Abs(pos.X - _dragOrigin.X) > 5)
            _dragArmed = true;

        // ponytail: once armed, reveal the ghost and follow the cursor.
        if (_dragArmed && _dragGhost != null)
        {
            if (_dragGhost.Visibility == Visibility.Collapsed)
                _dragGhost.Visibility = Visibility.Visible;
            var screen = PointToScreen(pos);
            _dragGhost.UpdatePosition(screen);
        }

        // ponytail: arm drag-out when the cursor leaves the strip, not on a
        // 40px horizontal threshold. The strip is narrow so a small vertical
        // wobble used to commit drag-out and pop a stray floating PropertyWindow
        // — that's the "莫名其妙的小浮窗" bug.
        if (!_dragOutArmed && outsideStrip)
            _dragOutArmed = true;

        // Feedback chip only appears once armed AND cursor is already outside
        // the strip, so a normal reorder drag never flashes it.
        if (_dragOutArmed && _dragOutFeedback == null && _dragOutTab != null && outsideStrip)
        {
            // ponytail: hit-test invisible so mouse events pass through to the
            // underlying PropertyWindow and RootBorder.PreviewMouseLeftButtonUp
            // can still clean up. ShowActivated=false keeps the original window
            // activated — otherwise Window_Deactivated would fire and CancelDrag
            // would yank the chip away mid-drag.
            _dragOutFeedback = new Window
            {
                Width = 160, Height = 32,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, Opacity = 0.85,
                IsHitTestVisible = false,
                ShowActivated = false,
                Content = new Border
                {
                    Background = (Brush)FindResource("Brush.Bg.Chrome"),
                    BorderBrush = (Brush)FindResource("Brush.Accent"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = _dragOutTab.Title,
                        Foreground = (Brush)FindResource("Brush.Text.Primary"),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        FontSize = 12,
                    }
                }
            };
            _dragOutFeedback.Show();
        }

        if (_dragOutFeedback != null)
        {
            var screen = PointToScreen(pos);
            _dragOutFeedback.Left = screen.X - 80;
            _dragOutFeedback.Top = screen.Y - 16;
        }

        // ponytail: commit drag-out once armed and the cursor has left the strip.
        if (_dragOutArmed && !_isDragOut && outsideStrip)
            _isDragOut = true;

        // ponytail: cross-window transfer — if drag-out is armed AND cursor is
        // over another strip's hit zone, mark transferring and tell the target
        // to show its drop indicator. Otherwise, leave any previous target.
        if (_dragOutArmed && outsideStrip)
        {
            var screen = PointToScreen(pos);
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

        // ponytail: live reorder while drag stays inside the strip. The ghost
        // and the moved tab occupy the same slot visually (ghost is on top via
        // AdornerLayer), so MoveTab shifts the others — animate them with
        // TranslateTransform per §4.1 of the spec.
        if (_dragArmed && !outsideStrip && _dragTab != null)
        {
            int newIndex = ComputeDropIndex(pos.X);
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
    }

    // ── Window-level preview handlers (called from PropertyWindow) ──

    /// <summary>Called by PropertyWindow.RootBorder_PreviewMouseMove so drag-out
    /// detection works even when the cursor has left the strip bounds.</summary>
    public void HandlePreviewMouseMove(MouseEventArgs e)
    {
        if (_dragTab == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        var stripBounds = new Rect(0, 0, ActualWidth, ActualHeight);
        bool outsideStrip = !stripBounds.Contains(pos);

        // ponytail: arm drag-out here too — once the cursor has left the tab
        // Border but is still on the PropertyWindow, only this handler fires.
        if (!_dragOutArmed && outsideStrip)
            _dragOutArmed = true;

        // Update feedback window position.
        if (_dragOutFeedback != null)
        {
            var screen = PointToScreen(e.GetPosition(this));
            _dragOutFeedback.Left = screen.X - 80;
            _dragOutFeedback.Top = screen.Y - 16;
        }

        // ponytail: commit drag-out once armed and the cursor has left the strip.
        if (!_isDragOut && outsideStrip)
            _isDragOut = true;
    }

    /// <summary>Called by PropertyWindow.RootBorder_PreviewMouseLeftButtonUp so the
    /// drop is always detected, even when the cursor is outside the strip.</summary>
    public void HandlePreviewMouseLeftButtonUp(MouseEventArgs e)
    {
        // ponytail: cross-window transfer takes priority over plain drag-out.
        // If we're transferring into another strip, route the tab there.
        if (_isTransferring && _transferTarget != null && _dragOutTab != null)
        {
            var tab = _dragOutTab;
            var key = tab.Key;
            _transferTarget.HandleTransferDragLeave();
            CleanupDragOutFeedback();
            CleanupDragGhost();
            PropertyWindowManager.Instance.TransferTab(this, _transferTarget, key);
            // ponytail: TransferTab calls OpenOrFocus which creates a NEW
            // PropertyTab instance on the target strip (same Key). The old
            // `tab` reference is from the source strip — ContainerFromItem
            // on the target returns null for it. Look up the new tab by key.
            var newTab = _transferTarget.Tabs.FirstOrDefault(t => t.Key == key);
            if (newTab != null) _transferTarget.ScrollIntoView(newTab);
            ResetDrag();
            e.Handled = true;
            return;
        }
        // not transferring — fall through to drag-out / reorder
        if (_dragOutArmed && _dragOutTab != null)
        {
            CleanupDragOutFeedback();

            if (_isDragOut)
            {
                // Cursor left the strip — pop out the target as a floating window.
                var screenPos = PointToScreen(e.GetPosition(this));
                HandleDragOutDrop(_dragOutTab, screenPos);
                ResetDrag();
                e.Handled = true;
                return;
            }
            // Cursor still inside — fall through to reorder logic.
        }

        if (!_dragArmed || _dragTab == null || _dragFromIndex < 0)
        {
            ResetDrag();
            return;
        }

        var dropX = e.GetPosition(TabsHost).X;
        int dropIndex = ComputeDropIndex(dropX);
        if (dropIndex >= 0 && dropIndex != _dragFromIndex && dropIndex != _dragFromIndex + 1)
        {
            int target = dropIndex;
            if (target > _dragFromIndex) target--;
            MoveTab(_dragFromIndex, target);
        }
        ResetDrag();
        e.Handled = true;
    }

    // ── Strip-level MouseUp (kept for reorder when drop is inside the strip) ──

    void TabsScroller_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // delegate to original strip-level handler logic (kept verbatim below)
        HandleTabsHostMouseLeftButtonUp(e);
    }

    void HandleTabsHostMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragOutArmed && _isDragOut)
        {
            ResetDrag();
            e.Handled = true;
            return;
        }
        if (!_dragArmed || _dragTab == null || _dragFromIndex < 0)
        {
            ResetDrag();
            return;
        }
        var dropX = e.GetPosition(TabsHost).X;
        int dropIndex = ComputeDropIndex(dropX);
        if (dropIndex >= 0 && dropIndex != _dragFromIndex && dropIndex != _dragFromIndex + 1)
        {
            int target = dropIndex;
            if (target > _dragFromIndex) target--;
            MoveTab(_dragFromIndex, target);
        }
        _dragArmed = false;
        ResetDrag();
        e.Handled = true;
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

    // ── Slide animation (§4.1) ──

    void CaptureSlidePositions(int from, int to)
    {
        _pendingSlide.Clear();
        for (int i = from; i <= to; i++)
        {
            if (i < 0 || i >= Tabs.Count) continue;
            var tab = Tabs[i];
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
            var transform = container.RenderTransform as TranslateTransform;
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
        return target.ComputeDropIndex(localInHost.X);
    }

    void ResetDrag()
    {
        // ponytail: release mouse capture before clearing other state so a
        // subsequent click anywhere isn't still routed to this strip.
        if (Mouse.Captured == this) Mouse.Capture(null);
        CleanupDragGhost();
        if (_isTransferring && _transferTarget != null)
            _transferTarget.HandleTransferDragLeave();
        _dragTab = null;
        _dragFromIndex = -1;
        _dragArmed = false;
        _dragOutTab = null;
        _dragOutArmed = false;
        _isDragOut = false;
        _isTransferring = false;
        _transferTarget = null;
        _dragInsertIndex = -1;
        CleanupDragOutFeedback();
    }

    void CleanupDragGhost()
    {
        if (_dragGhost != null && _dragGhostLayer != null)
        {
            _dragGhostLayer.Remove(_dragGhost);
            _dragGhostLayer = null;
            _dragGhost = null;
        }
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
        if (!Guid.TryParse(idStr, out var id)) return;

        object? target = typeName switch
        {
            nameof(Zone) => main.Zones.FirstOrDefault(z => z.Id == id),
            nameof(DesktopClock) => main.WidgetService?.Clocks.FirstOrDefault(c => c.Id == id),
            nameof(DesktopCalendar) => main.WidgetService?.Calendars.FirstOrDefault(c => c.Id == id),
            nameof(StickyNote) => main.NotesService?.Notes.FirstOrDefault(n => n.Id == id),
            _ => null,
        };

        if (target == null) return;

        main.OpenFloatingProperty(target);
        CloseTab(tab.Key);
    }

    void CleanupDragOutFeedback()
    {
        if (_dragOutFeedback == null) return;
        var w = _dragOutFeedback;
        _dragOutFeedback = null; // clear reference first
        try
        {
            w.Hide();           // immediate visual removal
            w.Close();          // release resources
        }
        catch { }
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
