using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public PropertyTabStrip()
    {
        InitializeComponent();
        TabsHost.ItemsSource = Tabs;
    }

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
        }
    }

    void TabRoot_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || _dragFromIndex < 0) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);

        if (!_dragArmed && Math.Abs(pos.X - _dragOrigin.X) > 5)
            _dragArmed = true;

        if (!_dragOutArmed && Math.Abs(pos.X - _dragOrigin.X) > 40)
            _dragOutArmed = true;
        // No CaptureMouse — window-level PreviewMouseMove routes events here.

        if (_dragOutArmed && _dragOutFeedback == null && _dragOutTab != null)
        {
            _dragOutFeedback = new Window
            {
                Width = 160, Height = 32,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true, ShowInTaskbar = false, Opacity = 0.85,
                Content = new Border
                {
                    Background = (Brush)FindResource("Brush.Bg.Chrome"),
                    BorderBrush = (Brush)FindResource("Brush.Accent"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
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
            var screen = PointToScreen(e.GetPosition(this));
            _dragOutFeedback.Left = screen.X - 80;
            _dragOutFeedback.Top = screen.Y - 16;
        }

        // Detect cursor leaving the strip — mark as drag-out.
        if (_dragOutArmed && !_isDragOut)
        {
            var stripBounds = new Rect(0, 0, ActualWidth, ActualHeight);
            if (!stripBounds.Contains(pos))
                _isDragOut = true;
        }
    }

    // ── Window-level preview handlers (called from PropertyWindow) ──

    /// <summary>Called by PropertyWindow.RootBorder_PreviewMouseMove so drag-out
    /// detection works even when the cursor has left the strip bounds.</summary>
    public void HandlePreviewMouseMove(MouseEventArgs e)
    {
        if (_dragTab == null || !_dragOutArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);

        // Update feedback window position.
        if (_dragOutFeedback != null)
        {
            var screen = PointToScreen(e.GetPosition(this));
            _dragOutFeedback.Left = screen.X - 80;
            _dragOutFeedback.Top = screen.Y - 16;
        }

        // Detect cursor leaving the strip.
        if (!_isDragOut)
        {
            var stripBounds = new Rect(0, 0, ActualWidth, ActualHeight);
            if (!stripBounds.Contains(pos))
                _isDragOut = true;
        }
    }

    /// <summary>Called by PropertyWindow.RootBorder_PreviewMouseLeftButtonUp so the
    /// drop is always detected, even when the cursor is outside the strip.</summary>
    public void HandlePreviewMouseLeftButtonUp(MouseEventArgs e)
    {
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

    void TabsHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // If drag-out was in progress, the window-level handler already dealt with it.
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
        _isDragOut = false;
        CleanupDragOutFeedback();
    }

    void HandleDragOutDrop(PropertyTab tab, Point screenPos)
    {
        var main = Window.GetWindow(this) as ManagementWindow;
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
}
