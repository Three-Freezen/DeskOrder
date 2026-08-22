using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

namespace DesktopZones.Views.Components;

/// <summary>
/// One tab in the property strip. Key is the stable identity (e.g. "zone-{guid}");
/// Title is the display label; IsPinned=true means a long-lived tab, false means a
/// transient preview tab. IsActive is set by PropertyTabStrip when this tab is the
/// selected one and drives the visual selected state via DataTrigger.
/// ponytail: DisplayTitle truncates at 4 chars so a packed strip stays compact;
/// remove the property and bind directly to Title if wider tabs are wanted.
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
/// ponytail: ItemsControl (not TabControl) because we need a flat horizontal strip with
/// our own visuals; selected-state plumbing is one bool on PropertyTab + a single-pass
/// refresh when ActiveTab changes.
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

    /// <summary>Fires after ActiveTab changes (host uses this to sync the docked
    /// panel target / floating-Activate routing).</summary>
    public event EventHandler? ActiveTabChanged;

    // ponytail: drag-to-reorder state. MouseDown captures origin; MouseMove
    // starts reorder once |Δx| > 5px; MouseUp commits via MoveTab(from, to).
    PropertyTab? _dragTab;
    int _dragFromIndex = -1;
    Point _dragOrigin;
    bool _dragArmed;

    // ponytail: drag-to-float state. When the user drags a tab > 40px horizontally
    // and releases outside the strip, pop out the target as a floating window.
    PropertyTab? _dragOutTab;
    bool _dragOutArmed;
    Window? _dragOutFeedback;  // temporary translucent preview during drag

    public PropertyTabStrip()
    {
        InitializeComponent();
        TabsHost.ItemsSource = Tabs;
    }

    static void OnActiveTabChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var strip = (PropertyTabStrip)d;
        strip.RefreshActiveFlag();
        strip.ActiveTabChanged?.Invoke(strip, EventArgs.Empty);
    }

    void RefreshActiveFlag()
    {
        // ponytail: O(n) flag sweep on ActiveTab change; tabs count is small (<20),
        // upgrade to per-tab subscription if it ever shows in a profile.
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, ActiveTab);
    }

    /// <summary>EventSetter target on the tab root Border — click selects the tab.</summary>
    void TabRoot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragArmed) { _dragArmed = false; return; }
        if (sender is Border { DataContext: PropertyTab tab })
            ActiveTab = tab;
    }

    /// <summary>MouseDown handler on the same Border — capture origin for drag-to-reorder.</summary>
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
        }
    }

    /// <summary>MouseMove on the tab Border — once Δx crosses 5 px we mark the
    /// drag as armed so the upcoming MouseUp doesn't also fire a "click select".
    /// Reordering itself is committed on MouseUp (when the user drops on a slot)
    /// rather than continuously while dragging, to avoid jitter when the strip
    /// is narrow and tabs share X coordinates.</summary>
    void TabRoot_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragTab == null || _dragFromIndex < 0) return;
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);

        // ponytail: two thresholds — 5px for reorder, 40px for drag-out.
        if (!_dragArmed && Math.Abs(pos.X - _dragOrigin.X) > 5)
            _dragArmed = true;

        if (!_dragOutArmed && Math.Abs(pos.X - _dragOrigin.X) > 40)
        {
            _dragOutArmed = true;
            CaptureMouse();
        }

        // Show drag feedback when armed for drag-out.
        if (_dragOutArmed && _dragOutFeedback == null && _dragOutTab != null)
        {
            _dragOutFeedback = new Window
            {
                Width = 160,
                Height = 32,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Content = new Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("Brush.Bg.Chrome"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("Brush.Accent"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Child = new TextBlock
                    {
                        Text = _dragOutTab.Title,
                        Foreground = (System.Windows.Media.Brush)FindResource("Brush.Text.Primary"),
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                        FontSize = 12,
                    }
                }
            };
            _dragOutFeedback.Show();
        }

        // Move feedback window with cursor.
        if (_dragOutFeedback != null)
        {
            var screen = PointToScreen(e.GetPosition(this));
            _dragOutFeedback.Left = screen.X - 80;
            _dragOutFeedback.Top = screen.Y - 16;
        }
    }

    /// <summary>MouseUp at strip level commits the reorder when armed.</summary>
    void TabsHost_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // If drag-out was armed, check if cursor is outside the strip.
        if (_dragOutArmed && _dragOutTab != null)
        {
            CleanupDragOutFeedback();

            // Check if cursor is outside the strip bounds.
            var pos = e.GetPosition(this);
            var stripBounds = new Rect(0, 0, ActualWidth, ActualHeight);
            if (!stripBounds.Contains(pos))
            {
                // ponytail: cursor is outside — pop out the target.
                var screenPos = PointToScreen(e.GetPosition(this));
                HandleDragOutDrop(_dragOutTab, screenPos);
                _dragOutTab = null;
                _dragOutArmed = false;
                ResetDrag();
                e.Handled = true;
                return;
            }
            // Cursor still inside strip — treat as reorder, not drag-out.
        }

        // Original reorder logic.
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

    int ComputeDropIndex(double x)
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

    void ResetDrag()
    {
        _dragTab = null;
        _dragFromIndex = -1;
        _dragArmed = false;
        _dragOutTab = null;
        _dragOutArmed = false;
        CleanupDragOutFeedback();
    }

    void HandleDragOutDrop(PropertyTab tab, Point screenPos)
    {
        // Find the ManagementWindow and resolve the target from the tab key.
        var main = Window.GetWindow(this) as Views.ManagementWindow;
        if (main == null) return;

        // Resolve target from tab key (format: "TypeName:Guid").
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

        // Pop out at cursor position.
        main.OpenFloatingProperty(target);

        // Also close the tab from the docked strip.
        CloseTab(tab.Key);
    }

    void CleanupDragOutFeedback()
    {
        if (_dragOutFeedback != null)
        {
            try { _dragOutFeedback.Close(); } catch { }
            _dragOutFeedback = null;
        }
    }

    /// <summary>Find a tab by key and set it active; if missing, create as preview.</summary>
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

    /// <summary>Promote an existing tab to pinned so it survives close-all-preview flows.
    /// Called from property-edit save paths; a tab only "sticks" to the strip once
    /// the user actually mutates the instance.</summary>
    public void PinTab(string key)
    {
        foreach (var t in Tabs)
            if (t.Key == key) { t.IsPinned = true; return; }
    }

    /// <summary>Remove a tab from the strip; if it was active, fall back to the previous neighbor.</summary>
    public void CloseTab(string key)
    {
        for (int i = 0; i < Tabs.Count; i++)
        {
            if (Tabs[i].Key != key) continue;
            bool wasActive = ReferenceEquals(Tabs[i], ActiveTab);
            Tabs.RemoveAt(i);
            if (!wasActive) { RefreshActiveFlag(); return; }
            // pick neighbor: previous, else first
            if (i > 0) ActiveTab = Tabs[i - 1];
            else if (Tabs.Count > 0) ActiveTab = Tabs[0];
            else ActiveTab = null;
            return;
        }
    }

    /// <summary>Close whichever tab is currently active. Returns true if a tab was removed.</summary>
    public bool CloseActiveTab()
    {
        if (ActiveTab == null) return false;
        CloseTab(ActiveTab.Key);
        return true;
    }

    /// <summary>Close every preview (unpinned) tab — called by the host when the user
    /// navigates to a different section so stale previews don't accumulate.</summary>
    public void CloseAllPreviewTabs()
    {
        for (int i = Tabs.Count - 1; i >= 0; i--)
            if (!Tabs[i].IsPinned) Tabs.RemoveAt(i);
        RefreshActiveFlag();
    }

    /// <summary>Move tab from one index to another (drag-to-reorder).
    /// No-op when out of range or same index.</summary>
    public void MoveTab(int from, int to)
    {
        if (from < 0 || from >= Tabs.Count) return;
        if (to < 0 || to >= Tabs.Count) return;
        if (from == to) return;
        Tabs.Move(from, to);
        RefreshActiveFlag();
    }
}

/// <summary>Tiny ICommand shim — same shape as the project uses elsewhere.</summary>
