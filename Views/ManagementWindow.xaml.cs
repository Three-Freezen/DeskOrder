using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Pages;
using DesktopZones.Views.Components;

namespace DesktopZones.Views;

public partial class ManagementWindow : Window
{
    private readonly ManagementViewModel _viewModel;
    private readonly ZoneManager _zoneManager;
    private readonly ConfigService _configService;
    private readonly NotesService? _notesService;
    private readonly WidgetService? _widgetService;
    private readonly PanelService? _panelService;
    private readonly LocalizationService _loc = LocalizationService.Instance;

    // Track widget windows opened from management window
    private readonly Dictionary<Guid, Window> _openClockWindows = new();
    private readonly Dictionary<Guid, Window> _openCalendarWindows = new();

    private bool IsNoteWindowOpen(Guid id) => ((App)System.Windows.Application.Current).IsNoteWindowOpen(id);
    private Window? GetNoteWindow(Guid id) => _notesService?.Windows.TryGetValue(id, out var w) == true ? w : null;

    // Guard flag to prevent re-entrant batch widget operations
    private bool _isBatchWidgetOperation;
    private bool _propertyPanelVisible;

    // Hotkey presets for notes
    private static readonly (string Label, int Modifiers, int Key, bool Enabled)[] HotkeyPresets = new[]
    {
        ("None",           0,           0x4E, false),
        ("Alt+N",          0x0001,      0x4E, true ),
        ("Ctrl+N",         0x0002,      0x4E, true ),
        ("Win+N",          0x0008,      0x4E, true ),
        ("Alt+Shift+N",    0x0005,      0x4E, true ),
    };

    // Hotkey presets for panel
    private static readonly (string Label, int Modifiers, int Key, bool Enabled)[] PanelHotkeyPresets = new[]
    {
        ("None",           0,           0x50, false),
        ("Alt+P",          0x0001,      0x50, true ),
        ("Ctrl+P",         0x0002,      0x50, true ),
        ("Win+P",          0x0008,      0x50, true ),
        ("Alt+Shift+P",    0x0005,      0x50, true ),
    };

    public NotesService? NotesService => _notesService;
    public WidgetService? WidgetService => _widgetService;
    public System.Collections.ObjectModel.ObservableCollection<Zone> Zones => _zoneManager.Zones;

    public ManagementWindow(ZoneManager zoneManager, ConfigService configService,
        NotesService? notesService = null, WidgetService? widgetService = null, PanelService? panelService = null)
    {
        InitializeComponent();
        _zoneManager = zoneManager;
        _configService = configService;
        _notesService = notesService;
        _widgetService = widgetService;
        _panelService = panelService;
        _viewModel = new ManagementViewModel(zoneManager, configService, notesService, widgetService, panelService);
        DataContext = this;

        Helpers.PropertyWindowService.Init(this);

        // Re-translate dynamic UI text on language switch. ThemeLabel, breadcrumb,
        // and tray events (in App.xaml.cs) are the few things that aren't XAML
        // loc:Loc bindings.
        _loc.LanguageChanged += _ => RefreshDynamicText();
        RefreshDynamicText();

        _zoneManager.ZoneVisibilityChanged += OnZoneVisibilityChanged;

        if (_panelService != null)
            _panelService.WindowClosed += () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (MainContent.Content is PanelPage pp) pp.RefreshList();
                }));
            };

        // Wire SideNav events (after InitializeComponent so x:Name'd control exists)
        SideNav.SectionChanged += SideNav_SectionChanged;
        SideNav.ShowAllClicked += (_, _) => ShowAll();
        SideNav.MinimizeAllClicked += (_, _) => HideAll();
        SideNav.HideAllClicked += (_, _) =>
        {
            if (ConfirmHideAll()) FullHideAll();
        };

        DockedPanel.CollapseRequested += (_, _) => TogglePropertyPanel();
        DockedPanel.UndockRequested += (_, _) =>
        {
            var target = DockedPanel.Target;
            if (target != null)
                PropertyWindowManager.Instance.OpenOrFocus(target, _configService, this);
        };

        Loaded += (_, _) =>
        {
            UpdateThemeIcon();
            ShowSection("zones");
            try
            {
                var config = _configService.Load();
                SetPropertyPanelVisible(!config.PropertyPanelCollapsed, persist: false);
                if (config.Panel.PanelEnabled)
                    _panelService?.Show(config);
            }
            catch { }
        };

        _loc.LanguageChanged += _ => { try { ApplyLoc(); } catch { } };
    }

    void ApplyLoc()
    {
        try
        {
            Title = _loc["Manage.Title"];
            if (MainContent != null && MainContent.Content is UserControl page)
            {
                if (page is ZonesPage zp) zp.ApplyLoc();
                else if (page is MergedGroupsPage mgp) mgp.ApplyLoc();
                else if (page is PanelPage pp) pp.ApplyLoc();
                else if (page is CalendarPage cp) cp.ApplyLoc();
                else if (page is ClockPage clkp) clkp.ApplyLoc();
                else if (page is StickyNotePage snp) snp.ApplyLoc();
                else if (page is SettingsPage sp) sp.ApplyLoc();
                else if (page is AboutPage ap) ap.ApplyLoc();
            }
        }
        catch { }
    }

    // ── Title bar (40px, WindowStyle=None, drag + double-click maximize) ──

    void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try { DragMove(); } catch { }
    }

    void MinimizeButton_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void MaxBtn_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void CloseButton_Click(object s, RoutedEventArgs e) => Hide();

    // ── Theme cycling (System → Light → Dark → System) ──

    void ThemeBtn_Click(object s, RoutedEventArgs e)
    {
        var next = ThemeService.CurrentMode switch
        {
            AppThemeMode.System => AppThemeMode.Light,
            AppThemeMode.Light  => AppThemeMode.Dark,
            AppThemeMode.Dark   => AppThemeMode.System,
            _                   => AppThemeMode.System,
        };
        // ponytail: title-bar cycle button must persist the same way the SettingsPage
        // radio buttons do — without Save(), restart would load the stale ThemeMode
        // and silently revert. Keep write+apply in this order so a Save failure leaves
        // the runtime theme matching the user's last click rather than flipping back.
        var cfg = _configService.Load();
        cfg.ThemeMode = next == AppThemeMode.Light ? "Light"
                      : next == AppThemeMode.Dark  ? "Dark"
                      : "System";
        _configService.Save(cfg);
        ThemeService.Apply(next);
        UpdateThemeIcon();
    }

    void UpdateThemeIcon()
    {
        var resolved = ThemeService.CurrentMode == AppThemeMode.System
            ? ResolveSystemThemeForIcon()
            : ThemeService.CurrentMode;
        if (ThemeIcon == null) return;
        ThemeIcon.Data = resolved == AppThemeMode.Light
            ? (Geometry)FindResource("Icon.Sun")
            : (Geometry)FindResource("Icon.Moon");
        UpdateThemeLabel();
    }

    void UpdateThemeLabel()
    {
        if (ThemeLabel == null) return;
        ThemeLabel.Text = ThemeService.CurrentMode switch
        {
            AppThemeMode.System => _loc["Theme.System"],
            AppThemeMode.Light  => _loc["Theme.Light"],
            _                   => _loc["Theme.Dark"],
        };
    }

    static AppThemeMode ResolveSystemThemeForIcon()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0 ? AppThemeMode.Dark : AppThemeMode.Light;
        }
        catch { return AppThemeMode.Dark; }
    }

    /// <summary>Update breadcrumb + status bar counts for the given section.</summary>
    public void UpdateBreadcrumb(string section, string countLabel)
    {
        try
        {
            if (CrumbSection != null) CrumbSection.Text = GetCrumbSectionLabel(section);
            if (CrumbCurrent != null) CrumbCurrent.Text = GetCrumbCurrentLabel(section);
        }
        catch { }
    }

    string? _lastSection;
    string? _lastCountLabel;

    /// <summary>Re-evaluate the few UI strings that aren't XAML loc:Loc bindings —
    /// theme label + breadcrumb section/current. Called on LanguageChanged
    /// and once on construction so the initial labels are translated.</summary>
    void RefreshDynamicText()
    {
        try { UpdateThemeLabel(); } catch { }
        if (_lastSection != null) UpdateBreadcrumb(_lastSection, _lastCountLabel ?? "");
    }

    static string GetCrumbSectionLabel(string section)
    {
        var loc = LocalizationService.Instance;
        return section switch
        {
            "zones" or "merged" or "panel" => loc["Breadcrumb.GroupZones"],
            "calendar" or "clock" or "sticky" => loc["Breadcrumb.GroupWidgets"],
            _ => "DeskOrder",
        };
    }

    static string GetCrumbCurrentLabel(string section)
    {
        var loc = LocalizationService.Instance;
        return section switch
        {
            "zones"    => loc["Breadcrumb.Zones"],
            "merged"   => loc["Breadcrumb.Merged"],
            "panel"    => loc["Breadcrumb.Panel"],
            "calendar" => loc["Breadcrumb.Calendar"],
            "clock"    => loc["Breadcrumb.Clock"],
            "sticky"   => loc["Breadcrumb.Sticky"],
            "settings" => loc["Breadcrumb.Settings"],
            "about"    => loc["Breadcrumb.About"],
            _          => "",
        };
    }

    /// <summary>Wrap a dialog window with a custom dark title bar (replaces ToolWindow white title bar).</summary>
    public static void WrapDialogWithDarkTitleBar(Window dlg, Border contentBorder, string title)
    {
        dlg.WindowStyle = WindowStyle.None;
        dlg.AllowsTransparency = true;
        dlg.Background = Brushes.Transparent;

        var dlgBg = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Cursor = Cursors.SizeAll
        };
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        });

        var closeBtn = new Button
        {
            Content = "✕", Width = 28, Height = 28, FontSize = 12,
            Cursor = Cursors.Hand, Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0xA0)),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.Click += (_, _) => dlg.Close();

        var titleRow = new Grid();
        titleRow.Children.Add(titlePanel);
        titleRow.Children.Add(closeBtn);
        titleBar.Child = titleRow;

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(12, 0, 12, 0)
        };

        contentBorder.CornerRadius = new CornerRadius(0);
        contentBorder.Padding = new Thickness(0);
        contentBorder.Margin = new Thickness(20);
        contentBorder.Background = Brushes.Transparent;
        contentBorder.BorderThickness = new Thickness(0);

        Grid.SetRow(titleBar, 0);
        Grid.SetRow(separator, 1);
        Grid.SetRow(contentBorder, 2);
        rootGrid.Children.Add(titleBar);
        rootGrid.Children.Add(separator);
        rootGrid.Children.Add(contentBorder);

        dlgBg.Child = rootGrid;
        dlg.Content = dlgBg;
    }

    // ── Hotkey helpers ──

    public static string HotkeyModToString(int mods)
    {
        var parts = new List<string>();
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0008) != 0) parts.Add("Win");
        return parts.Count > 0 ? string.Join("+", parts) : "";
    }

    public static string KeyCodeToString(int key) => key switch
    {
        0x4E => "N", 0x41 => "A", 0x42 => "B", 0x43 => "C", 0x44 => "D",
        0x45 => "E", 0x46 => "F", 0x47 => "G", 0x48 => "H",
        0x49 => "I", 0x4A => "J", 0x4B => "K", 0x4C => "L",
        0x4D => "M", 0x4F => "O", 0x50 => "P", 0x51 => "Q",
        0x52 => "R", 0x53 => "S", 0x54 => "T", 0x55 => "U",
        0x56 => "V", 0x57 => "W", 0x58 => "X", 0x59 => "Y", 0x5A => "Z",
        0x70 => "F1", 0x71 => "F2", 0x72 => "F3", 0x73 => "F4",
        0x74 => "F5", 0x75 => "F6", 0x76 => "F7", 0x77 => "F8",
        0x78 => "F9", 0x79 => "F10", 0x7A => "F11", 0x7B => "F12",
        0x20 => "Space", 0x08 => "Back", 0x09 => "Tab", 0x0D => "Enter",
        0x1B => "Esc", 0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x2D => "Ins", 0x2E => "Del", 0x24 => "Home", 0x23 => "End",
        0x21 => "PgUp", 0x22 => "PgDn",
        _ => ((char)key).ToString()
    };

    public static string GetHotkeyLabel(int mods, int key)
    {
        string modStr = HotkeyModToString(mods);
        string keyStr = KeyCodeToString(key);
        return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr}+{keyStr}";
    }

    // ── Dialog opening methods (called from Page code-behinds via callback) ──

    public void OpenFloatingProperty(object target) =>
        PropertyWindowManager.Instance.OpenOrFocus(target, _configService, this);

    public void DeleteZoneWithConfirm(Zone zone)
    {
        if (MessageBox.Show(_loc.Get("Dialog.DeleteZoneMsg", zone.Name),
            _loc["Dialog.DeleteZoneTitle"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _zoneManager.DeleteZone(zone.Id);
    }

    public void ShowMergeDialog(Zone sourceZone) => ShowMergeDialogImpl(sourceZone);
    public void DisbandEntireGroup(Zone masterZone)
    {
        var cn = _loc.CurrentLanguage == "zh";
        var result = MessageBox.Show(
            cn ? $"确定要解散组合分区「{masterZone.MergedGroupMembership.DisplayName}」吗？\n所有分区将恢复为独立窗口。"
               : $"Disband merged group \"{masterZone.MergedGroupMembership.DisplayName}\"?\nAll zones will return to individual windows.",
            cn ? "解散组合" : "Disband Group",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            _zoneManager.DisbandMergedGroup(masterZone.MergedGroupMembership.GroupId.Value);
    }
    public void MergeWithAnotherGroup(Zone sourceMaster) => MergeWithAnotherGroupImpl(sourceMaster);
    public void DisbandSingleZone(Zone masterZone) => DisbandSingleZoneImpl(masterZone);
    public void ShowMergedGroupContextMenu(Zone masterZone, Button placementBtn) =>
        ShowMergedGroupContextMenuImpl(masterZone, placementBtn);

    public void NoteHotkeySet_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not StickyNote note) return;
        NoteHotkeySetImpl(note, btn);
    }

    public void ShowNoteHotkeyRecorderDialog(StickyNote note) => ShowNoteHotkeyRecorderDialogImpl(note);

    public void ToggleNoteWindow(StickyNote note)
    {
        var app = (App)System.Windows.Application.Current;
        app.ToggleNoteWindow(note);
    }
    public void ToggleClockWindow(DesktopClock clock) => ToggleClockWindowImpl(clock);
    public void ToggleCalendarWindow(DesktopCalendar cal) => ToggleCalendarWindowImpl(cal);

    public void DeleteNote(StickyNote note)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.IsNoteWindowOpen(note.Id) && app.NotesService?.Windows.TryGetValue(note.Id, out var w) == true)
            w.Close();
        _notesService?.DeleteNote(note.Id);
    }

    public void DeleteClock(DesktopClock clock)
    {
        if (_openClockWindows.TryGetValue(clock.Id, out var w)) w.Close();
        _widgetService?.DeleteClock(clock.Id);
    }

    public void DeleteCalendar(DesktopCalendar cal)
    {
        if (_openCalendarWindows.TryGetValue(cal.Id, out var w)) w.Close();
        _widgetService?.DeleteCalendar(cal.Id);
    }

    public void NewNote()
    {
        if (_notesService == null) return;
        var wa = SystemParameters.WorkArea;
        var note = _notesService.CreateNote(wa.Left + (wa.Width - 260) / 2, wa.Top + (wa.Height - 200) / 3);
        OpenNoteWindow(note);
    }

    public void NewClock()
    {
        if (_widgetService == null) return;
        var wa = SystemParameters.WorkArea;
        var clock = _widgetService.CreateClock(wa.Left + (wa.Width - 220) / 2 + 120, wa.Top + 60);
        OpenClockWindow(clock);
    }

    public void NewCalendar()
    {
        if (_widgetService == null) return;
        var wa = SystemParameters.WorkArea;
        var cal = _widgetService.CreateCalendar(wa.Left + (wa.Width - 280) / 2 - 120, wa.Top + 40);
        OpenCalendarWindow(cal);
    }

    public void NewZone() => _viewModel.CreateZoneCommand.Execute(null);

    public void ShowAll()
    {
        _zoneManager.ShowAll();
        ShowAllWidgets();
    }
    public void HideAll()
    {
        _zoneManager.HideAll();
        HideAllWidgets();
    }
    public void FullHideAll()
    {
        _zoneManager.FullHideAll();
        FullHideAllWidgets();
    }

    /// <summary>Confirm before performing a full hide-all (used by sidebar).</summary>
    public bool ConfirmHideAll()
    {
        var cn = _loc.CurrentLanguage == "zh";
        var totalZones = _zoneManager?.Zones?.Count ?? 0;
        var msg = cn
            ? $"隐藏全部 {totalZones} 个窗口？\n可从托盘菜单或本窗口恢复。"
            : $"Hide all {totalZones} windows?\nYou can restore from the tray menu or this window.";
        var res = MessageBox.Show(msg,
            cn ? "全部隐藏" : "Hide All",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return res == MessageBoxResult.Yes;
    }

    public void ShowAllWidgetsFromVm() => ShowAllWidgets();
    public void HideAllWidgetsFromVm() => HideAllWidgets();
    public void FullHideAllWidgetsFromVm() => FullHideAllWidgets();

    void ShowAllWidgets()
    {
        if (_isBatchWidgetOperation) return;
        _isBatchWidgetOperation = true;
        try
        {
            var app = (App)System.Windows.Application.Current;
            if (_notesService != null)
                foreach (var note in _notesService.Notes)
                    if (!app.IsNoteWindowOpen(note.Id))
                        OpenNoteWindow(note);
            if (_widgetService != null)
            {
                foreach (var clock in _widgetService.Clocks)
                    if (!_openClockWindows.ContainsKey(clock.Id))
                        OpenClockWindow(clock);
                foreach (var cal in _widgetService.Calendars)
                    if (!_openCalendarWindows.ContainsKey(cal.Id))
                        OpenCalendarWindow(cal);
            }
        }
        catch { }
        finally { _isBatchWidgetOperation = false; }
    }

    void HideAllWidgets()
    {
        if (_isBatchWidgetOperation) return;
        _isBatchWidgetOperation = true;
        try
        {
            foreach (var w in _openClockWindows.Values.ToList()) w.Hide();
            foreach (var w in _openCalendarWindows.Values.ToList()) w.Hide();
        }
        catch { }
        finally { _isBatchWidgetOperation = false; }
    }

    void FullHideAllWidgets()
    {
        if (_isBatchWidgetOperation) return;
        _isBatchWidgetOperation = true;
        try
        {
            foreach (var w in _openClockWindows.Values.ToList()) w.Close();
            _openClockWindows.Clear();
            foreach (var w in _openCalendarWindows.Values.ToList()) w.Close();
            _openCalendarWindows.Clear();
        }
        catch { }
        finally { _isBatchWidgetOperation = false; }
    }

    public void OpenNoteWindow(StickyNote note)
    {
        var app = (App)System.Windows.Application.Current;
        app.OpenNoteWindowFromManager(note);
    }

    public void OpenClockWindow(DesktopClock clock)
    {
        if (_openClockWindows.ContainsKey(clock.Id)) return;
        var window = new ClockWidget(clock, _widgetService!);
        window.Closed += (_, _) =>
        {
            _openClockWindows.Remove(clock.Id);
            _widgetService!.ClockWindows.Remove(clock.Id);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainContent.Content is ClockPage cp) cp.RefreshList();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        _openClockWindows[clock.Id] = window;
        _widgetService!.ClockWindows[clock.Id] = window;
        window.Show();
    }

    public void OpenCalendarWindow(DesktopCalendar cal)
    {
        if (_openCalendarWindows.ContainsKey(cal.Id)) return;
        var window = new CalendarWidget(cal, _widgetService!);
        window.Closed += (_, _) =>
        {
            _openCalendarWindows.Remove(cal.Id);
            _widgetService!.CalendarWindows.Remove(cal.Id);
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainContent.Content is CalendarPage cp) cp.RefreshList();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        _openCalendarWindows[cal.Id] = window;
        _widgetService!.CalendarWindows[cal.Id] = window;
        window.Show();
        window.Activate();
    }

    // ── Visibility sync ──

    private void OnZoneVisibilityChanged(Guid zoneId, bool isVisible)
    {
        // ponytail: reserved for future tray tooltip sync (2026-08) — currently a no-op
        // subscription to keep ZoneManager.ZoneVisibilityChanged alive for diagnostics.
    }

    void SyncClockWindows()
    {
        if (_widgetService == null) return;
        var activeIds = new HashSet<Guid>(_widgetService.Clocks.Select(c => c.Id));
        foreach (var kv in _openClockWindows.ToList())
        { if (!activeIds.Contains(kv.Key)) { try { kv.Value.Close(); } catch { } _openClockWindows.Remove(kv.Key); } }
    }
    void SyncCalendarWindows()
    {
        if (_widgetService == null) return;
        var activeIds = new HashSet<Guid>(_widgetService.Calendars.Select(c => c.Id));
        foreach (var kv in _openCalendarWindows.ToList())
        { if (!activeIds.Contains(kv.Key)) { try { kv.Value.Close(); } catch { } _openCalendarWindows.Remove(kv.Key); } }
    }

    public void TogglePanel()
    {
        try
        {
            if (_panelService == null) return;
            var config = _configService.Load();
            if (_panelService.IsOpen) _panelService.CloseAndClear();
            else _panelService.Show(config);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.ToString(), "PanelToggle Error"); }
    }

    void TogglePropertyPanel() => SetPropertyPanelVisible(!_propertyPanelVisible, persist: true);

    void SetPropertyPanelVisible(bool visible, bool persist)
    {
        _propertyPanelVisible = visible;
        RightCol.Width = new GridLength(visible ? 360 : 0);
        DockedPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!persist) return;
        try
        {
            var config = _configService.Load();
            config.PropertyPanelCollapsed = !visible;
            _configService.Save(config);
        }
        catch { }
    }

    void NoteHotkeySetImpl(StickyNote note, Button btn)
    {
        try
        {
            var cn = _loc.CurrentLanguage == "zh";
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = btn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4)
            };

            var stack = new StackPanel();
            foreach (var preset in HotkeyPresets)
            {
                var captured = preset;
                string label = captured.Enabled
                    ? GetHotkeyLabel(captured.Modifiers, captured.Key)
                    : (cn ? "无" : "None");
                bool isCurrent = note.HotkeyEnabled == captured.Enabled
                    && note.HotkeyModifiers == captured.Modifiers
                    && note.HotkeyKey == captured.Key;

                var item = new Border
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Background = isCurrent ? new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0x3A, 0xED)) : Brushes.Transparent
                };
                item.Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Foreground = isCurrent ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)) : new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0))
                };
                item.MouseLeftButtonDown += (_, _) =>
                {
                    note.HotkeyEnabled = captured.Enabled;
                    note.HotkeyModifiers = captured.Modifiers;
                    note.HotkeyKey = captured.Key;
                    _notesService?.UpdateNote(note);
                    if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
                    popup.IsOpen = false;
                };
                item.MouseEnter += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0x63, 0xFF)); };
                item.MouseLeave += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = Brushes.Transparent; };
                stack.Children.Add(item);
            }

            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 4, 4, 4)
            };
            stack.Children.Add(separator);

            var newItem = new Border
            {
                Padding = new Thickness(8, 4, 8, 4),
                CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };
            newItem.Child = new TextBlock
            {
                Text = cn ? "新增..." : "New...",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
            };
            newItem.MouseLeftButtonDown += (_, _) =>
            {
                popup.IsOpen = false;
                ShowNoteHotkeyRecorderDialogImpl(note);
            };
            newItem.MouseEnter += (s3, _) => { if (s3 is Border b3) b3.Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)); };
            newItem.MouseLeave += (s3, _) => { if (s3 is Border b3) b3.Background = Brushes.Transparent; };
            stack.Children.Add(newItem);

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }
        catch { }
    }

    void ShowNoteHotkeyRecorderDialogImpl(StickyNote note)
    {
        var cn = _loc.CurrentLanguage == "zh";
        var dlg = new Window
        {
            Title = cn ? "录制快捷键" : "Record Hotkey",
            Width = 320, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent
        };

        var mainBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = new Border { Height = 30, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 12) };
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };
        titleBar.Child = new TextBlock
        {
            Text = cn ? "录制快捷键" : "Record Hotkey",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        var instruction = new TextBlock
        {
            Text = cn ? "请按下快捷键组合..." : "Press hotkey combination...",
            FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instruction, 1);
        grid.Children.Add(instruction);

        var hotkeyDisplay = new TextBox
        {
            Text = "", IsReadOnly = true, FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hotkeyDisplay, 2);
        grid.Children.Add(hotkeyDisplay);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = new Button
        {
            Content = cn ? "取消" : "Cancel", Width = 60, Height = 28, FontSize = 11, Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.Click += (_, _) => dlg.Close();
        var saveButton = new Button
        {
            Content = cn ? "保存" : "Save", Width = 60, Height = 28, FontSize = 11, Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x3A, 0xED)),
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            IsEnabled = false
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(saveButton);
        Grid.SetRow(buttonPanel, 3);
        grid.Children.Add(buttonPanel);

        mainBorder.Child = grid;
        dlg.Content = mainBorder;

        int recordedModifiers = 0, recordedKey = 0;
        bool isRecording = true;

        dlg.KeyDown += (_, e) =>
        {
            if (!isRecording) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
                return;
            recordedModifiers = 0;
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) recordedModifiers |= 0x0002;
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) recordedModifiers |= 0x0001;
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) recordedModifiers |= 0x0004;
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) recordedModifiers |= 0x0008;
            recordedKey = KeyInterop.VirtualKeyFromKey(key);
            hotkeyDisplay.Text = GetHotkeyLabel(recordedModifiers, recordedKey);
            saveButton.IsEnabled = true;
            saveButton.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
            saveButton.Foreground = Brushes.White;
            isRecording = false;
        };

        saveButton.Click += (_, _) =>
        {
            if (note.CustomHotkeys == null) note.CustomHotkeys = new List<CustomHotkey>();
            note.CustomHotkeys.Add(new CustomHotkey { Modifiers = recordedModifiers, Key = recordedKey });
            note.HotkeyEnabled = true;
            note.HotkeyModifiers = recordedModifiers;
            note.HotkeyKey = recordedKey;
            _notesService?.UpdateNote(note);
            if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
            dlg.Close();
        };

        dlg.ShowDialog();
    }

    void ShowMergedGroupContextMenuImpl(Zone masterZone, Button placementBtn)
    {
        var cn = _loc.CurrentLanguage == "zh";
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
            StaysOpen = false
        };

        var bgBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A));
        var fgBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0));
        var hoverBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0x63, 0xFF));

        var menuBorder = new Border
        {
            Background = bgBrush,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(4),
            MinWidth = 180
        };

        var stack = new StackPanel();

        UIElement MakeItem(string text, Action onClick)
        {
            var tb = new TextBlock { Text = text, Foreground = fgBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var itemBorder = new Border
            {
                Background = Brushes.Transparent, CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand, Child = tb
            };
            itemBorder.MouseEnter += (_, _) => itemBorder.Background = hoverBrush;
            itemBorder.MouseLeave += (_, _) => itemBorder.Background = Brushes.Transparent;
            itemBorder.MouseLeftButtonDown += (_, _) => { popup.IsOpen = false; onClick(); };
            return itemBorder;
        }

        if (masterZone.MergedGroupMembership.SubZoneIds.Count > 0)
            stack.Children.Add(MakeItem(cn ? "分离单个分区" : "Disband Single Zone", () => DisbandSingleZoneImpl(masterZone)));
        stack.Children.Add(MakeItem(cn ? "解散组合分区" : "Disband Entire Group", () => DisbandEntireGroup(masterZone)));
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(6, 4, 6, 4) });
        stack.Children.Add(MakeItem(cn ? "添加分区到组合" : "Add Zone to Group", () => ShowMergeDialogImpl(masterZone)));
        if (_zoneManager.Zones.Any(z => z.MergedGroupMembership.SubZoneIds.Count > 0 && z.Id != masterZone.Id))
            stack.Children.Add(MakeItem(cn ? "与其他组合合并" : "Merge with Another Group", () => MergeWithAnotherGroupImpl(masterZone)));

        menuBorder.Child = stack;
        popup.Child = menuBorder;
        popup.PlacementTarget = placementBtn;
        popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

        MouseButtonEventHandler? previewHandler = null;
        previewHandler = (_, _) => { if (popup.IsOpen) popup.IsOpen = false; };
        this.AddHandler(UIElement.PreviewMouseDownEvent, previewHandler);
        popup.Closed += (_, _) => this.RemoveHandler(UIElement.PreviewMouseDownEvent, previewHandler);

        popup.IsOpen = true;
    }

    void DisbandSingleZoneImpl(Zone masterZone)
    {
        var cn = _loc.CurrentLanguage == "zh";
        var dialogTitle = cn ? "选择要分离的分区" : "Select Zone to Disband";
        var dialog = new Window
        {
            Title = dialogTitle, Width = 300, Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize
        };

        var bgBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid()
        };

        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = cn ? "选择要从组合中分离的分区：" : "Select zone to remove from group:",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var listBox = new ListBox
        {
            Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), FontSize = 12, Margin = new Thickness(0, 0, 0, 12)
        };
        foreach (var subId in masterZone.MergedGroupMembership.SubZoneIds)
        {
            var subZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == subId);
            if (subZone != null)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
                if (!string.IsNullOrEmpty(subZone.IconChar))
                    itemPanel.Children.Add(new TextBlock { Text = subZone.IconChar, FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
                itemPanel.Children.Add(new TextBlock { Text = subZone.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)) });
                listBox.Items.Add(new ListBoxItem { Content = itemPanel, Tag = subZone, Padding = new Thickness(6, 4, 6, 4) });
            }
        }
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        Grid.SetRow(listBox, 1);
        grid.Children.Add(listBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = cn ? "取消" : "Cancel", Width = 70, Height = 28, Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)), Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dialog.Close();
        var disbandBtn = new Button { Content = cn ? "分离" : "Disband", Width = 80, Height = 28, Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand };
        disbandBtn.Click += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Tag is Zone selectedZone)
            {
                _zoneManager.RemoveFromMergedGroup(selectedZone.Id);
                dialog.Close();
            }
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(disbandBtn);
        Grid.SetRow(btnRow, 2);
        grid.Children.Add(btnRow);

        WrapDialogWithDarkTitleBar(dialog, bgBorder, dialogTitle);
        dialog.ShowDialog();
    }

    void MergeWithAnotherGroupImpl(Zone sourceMaster)
    {
        var cn = _loc.CurrentLanguage == "zh";
        var otherGroups = _zoneManager.Zones.Where(z => z.MergedGroupMembership.SubZoneIds.Count > 0 && z.Id != sourceMaster.Id).ToList();
        if (otherGroups.Count == 0)
        {
            MessageBox.Show(cn ? "没有其他组合分区可合并。" : "No other merged groups to merge with.",
                cn ? "合并" : "Merge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mergeTargetTitle = cn ? "选择要合并的目标组合" : "Select Target Group to Merge";
        var dialog = new Window { Title = mergeTargetTitle, Width = 360, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };

        var bgBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid() };
        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = cn ? "选择要合并的目标组合：" : "Select target group to merge with:", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var listBox = new ListBox { Background = new SolidColorBrush(Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF)), Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var targetGroup in otherGroups)
        {
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            if (!string.IsNullOrEmpty(targetGroup.MergedGroupMembership.Icon))
                itemPanel.Children.Add(new TextBlock { Text = targetGroup.MergedGroupMembership.Icon, FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            itemPanel.Children.Add(new TextBlock { Text = targetGroup.MergedGroupMembership.DisplayName, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)) });
            listBox.Items.Add(new ListBoxItem { Content = itemPanel, Tag = targetGroup, Padding = new Thickness(6, 4, 6, 4) });
        }
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        Grid.SetRow(listBox, 1);
        grid.Children.Add(listBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = cn ? "取消" : "Cancel", Width = 70, Height = 28, Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)), Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dialog.Close();
        var mergeBtn = new Button { Content = cn ? "合并" : "Merge", Width = 80, Height = 28, Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand };
        mergeBtn.Click += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Tag is Zone targetGroup)
            {
                foreach (var subId in sourceMaster.MergedGroupMembership.SubZoneIds.ToList())
                {
                    _zoneManager.RemoveFromMergedGroup(subId);
                    _zoneManager.MergeZones(targetGroup.Id, subId);
                }
                _zoneManager.RemoveFromMergedGroup(sourceMaster.Id);
                _zoneManager.MergeZones(targetGroup.Id, sourceMaster.Id);
                dialog.Close();
            }
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(mergeBtn);
        Grid.SetRow(btnRow, 2);
        grid.Children.Add(btnRow);

        WrapDialogWithDarkTitleBar(dialog, bgBorder, mergeTargetTitle);
        dialog.ShowDialog();
    }

    void ShowMergeDialogImpl(Zone sourceZone)
    {
        var cn = _loc.CurrentLanguage == "zh";
        var eligibleZones = _zoneManager.Zones
            .Where(z => z.Id != sourceZone.Id
                && (sourceZone.MergedGroupMembership.GroupId == null
                    ? z.MergedGroupMembership.GroupId == null || z.MergedGroupMembership.GroupId != sourceZone.MergedGroupMembership.GroupId
                    : z.MergedGroupMembership.GroupId == null))
            .ToList();
        if (sourceZone.MergedGroupMembership.SubZoneIds.Count > 0)
            eligibleZones = eligibleZones.Where(z => !sourceZone.MergedGroupMembership.SubZoneIds.Contains(z.Id)).ToList();
        if (eligibleZones.Count == 0)
        {
            MessageBox.Show(_loc["Merge.NoTargets"], _loc["Merge.Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Window { Title = _loc["Merge.Title"], Width = 360, Height = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        var bgBorder = new Border { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid() };
        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = cn ? "选择要合并的分区（可多选）：" : "Select zones to merge (multi-select):", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var selectAllPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var selectAllCheckBox = new CheckBox { Content = cn ? "全选" : "Select All", Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), FontSize = 12, IsChecked = false };
        selectAllPanel.Children.Add(selectAllCheckBox);
        Grid.SetRow(selectAllPanel, 1);
        grid.Children.Add(selectAllPanel);

        var checkBoxes = new List<CheckBox>();
        var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200 };
        var zonesPanel = new StackPanel();
        foreach (var z in eligibleZones)
        {
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            if (!string.IsNullOrEmpty(z.IconChar))
                itemPanel.Children.Add(new TextBlock { Text = z.IconChar, FontSize = 14, Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            itemPanel.Children.Add(new TextBlock { Text = z.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)) });
            var checkBox = new CheckBox { Content = itemPanel, Tag = z, Margin = new Thickness(0, 2, 0, 2), Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), FontSize = 12 };
            checkBoxes.Add(checkBox);
            zonesPanel.Children.Add(checkBox);
        }
        scrollViewer.Content = zonesPanel;
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);

        selectAllCheckBox.Checked += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        selectAllCheckBox.Unchecked += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = _loc["Rename.Cancel"], Width = 70, Height = 28, Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)), Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)), BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dlg.Close();
        var mergeBtn = new Button { Content = _loc["Merge.MergeBtn"], Width = 80, Height = 28, Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 11, Cursor = Cursors.Hand };
        mergeBtn.Click += (_, _) =>
        {
            var selected = checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Tag as Zone).Where(z => z != null).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(cn ? "请至少选择一个分区" : "Please select at least one zone", cn ? "提示" : "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var tz in selected) _zoneManager.MergeZones(sourceZone.Id, tz!.Id);
            dlg.Close();
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(mergeBtn);
        Grid.SetRow(btnRow, 3);
        grid.Children.Add(btnRow);

        WrapDialogWithDarkTitleBar(dlg, bgBorder, _loc["Merge.Title"]);
        dlg.ShowDialog();
    }

    void ToggleClockWindowImpl(DesktopClock clock)
    {
        if (_openClockWindows.TryGetValue(clock.Id, out var w) && w is ClockWidget cw)
        {
            if (cw.MainContent.Visibility == Visibility.Visible) cw.HideClock();
            else cw.ShowClock();
        }
        else
        {
            clock.IsVisible = true;
            OpenClockWindow(clock);
        }
    }

    void ToggleCalendarWindowImpl(DesktopCalendar cal)
    {
        if (_openCalendarWindows.TryGetValue(cal.Id, out var w) && w is CalendarWidget caw)
        {
            if (caw.MainContent.Visibility == Visibility.Visible) caw.HideCalendar();
            else caw.ShowCalendar();
        }
        else
        {
            cal.IsVisible = true;
            OpenCalendarWindow(cal);
        }
    }

    // ── Page navigation ──

    public void ShowSection(string section)
    {
        System.Windows.Controls.UserControl? page = section switch
        {
            "zones"    => new ZonesPage(this, _viewModel, _zoneManager),
            "merged"   => new MergedGroupsPage(this, _viewModel, _zoneManager),
            "panel"    => new PanelPage(this, _configService, _panelService),
            "calendar" => new CalendarPage(this, _viewModel, _widgetService),
            "clock"    => new ClockPage(this, _viewModel, _widgetService),
            "sticky"   => new StickyNotePage(this, _viewModel, _notesService),
            "about"    => new AboutPage(),
            "settings" => new SettingsPage(_configService),
            _          => new ZonesPage(this, _viewModel, _zoneManager)
        };
        try { MainContent.Content = page; ApplyLoc(); } catch { }
        try { UpdateBreadcrumb(section, GetSectionCountLabel(section)); _lastSection = section; _lastCountLabel = GetSectionCountLabel(section); } catch { }
        try { if (SideNav != null) SideNav.ActiveSection = section; } catch { }
    }

    private void SideNav_SectionChanged(object sender, string section)
    {
        ShowSection(section);
    }

    private string GetSectionCountLabel(string section)
    {
        int zones = _zoneManager?.Zones?.Count ?? 0;
        int merged = _zoneManager?.Zones?.Count(z => z.MergedGroupMembership.SubZoneIds.Count > 0) ?? 0;
        int notes = _notesService?.Notes?.Count ?? 0;
        int clocks = _widgetService?.Clocks?.Count ?? 0;
        int calendars = _widgetService?.Calendars?.Count ?? 0;
        return section switch
        {
            "zones"    => $"{zones} 分区",
            "merged"   => $"{merged} 个组合",
            "panel"    => _panelService?.IsOpen == true ? "1 面板 · 已启用" : "未启用",
            "calendar" => $"{calendars} 个日历",
            "clock"    => $"{clocks} 个时钟",
            "sticky"   => $"{notes} 张便签",
            "about"    => "关于页面",
            "settings" => "设置面板",
            _          => ""
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
}