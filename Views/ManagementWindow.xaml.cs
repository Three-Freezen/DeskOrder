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

    // ponytail 2026-08-27: 全部显示/隐藏/最小化 各自独立的预设菜单(字母 A/H/M)，
    // 不再复用便签的 N 预设。
    private static readonly (string Label, int Modifiers, int Key, bool Enabled)[] ShowAllHotkeyPresets = BuildGlobalHotkeyPresets('A');
    private static readonly (string Label, int Modifiers, int Key, bool Enabled)[] HideAllHotkeyPresets = BuildGlobalHotkeyPresets('H');
    private static readonly (string Label, int Modifiers, int Key, bool Enabled)[] MinimizeAllHotkeyPresets = BuildGlobalHotkeyPresets('M');

    static (string Label, int Modifiers, int Key, bool Enabled)[] BuildGlobalHotkeyPresets(char key)
    {
        int vk = key;
        return new[]
        {
            ("None",           0,      0,    false),
            ($"Alt+{key}",     0x0001, vk,   true ),
            ($"Ctrl+{key}",    0x0002, vk,   true ),
            ($"Win+{key}",     0x0008, vk,   true ),
            ($"Alt+Shift+{key}",0x0005, vk,  true ),
        };
    }

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
        // ponytail 2026-08-25: central Persist wiring for BOTH panels. Pages
        // previously overwrote DockedPanel.Persist with a type-specific handler,
        // so editing a Clock tab while the Zones page was active silently
        // dropped every change. One dispatcher handles all target types;
        // floating editors get the same via PropertyWindowManager.OpenFloating.
        WirePropertyPanelPersist(DockedPanel);
        DockedPanel.UndockRequested += (_, _) =>
        {
            // ponytail: undock = pop-out flow. Clear the docked target first so
            // the pop-out helper doesn't re-clear it (idempotent but cleaner).
            var target = DockedPanel.Target;
            if (target == null) return;
            DockedPanel.Target = null;
            PropertyWindowManager.Instance.PopOutTarget(target, _configService, this, requester: this);
        };
        // ponytail: header X in docked panel closes the active tab. When the
        // last tab goes, fold the right column so the list takes the room.
        DockedPanel.CloseTabRequested += (_, _) =>
        {
            var closed = DockedTabs.CloseActiveTab();
            if (closed && DockedTabs.Tabs.Count == 0)
            {
                SetPropertyPanelVisible(false, persist: true);
                DockedPanel.Target = null;
            }
            else DockedPanel.Target = DockedTabs.ActiveTab?.Key != null ? null : DockedPanel.Target;
        };
        // ponytail: keep docked panel target in sync with the active tab. When
        // the tab strip says "show me zone X", the panel rebuilds for X.
        DockedTabs.ActiveTabChanged += (_, _) =>
        {
            var active = DockedTabs.ActiveTab;
            if (active == null) { DockedPanel.Target = null; return; }
            var t = ResolveTargetFromKey(active.Key);
            DockedPanel.Target = t;
            DockedPanel.IsCloseable = DockedTabs.Tabs.Count > 0;
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
            Background = ThemeBrushes.BgChromeModern,
            CornerRadius = new CornerRadius(10),
            BorderBrush = ThemeBrushes.BorderDefaultModern,
            BorderThickness = new Thickness(1)
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new Border
        {
            Background = ThemeBrushes.BgChromeModern,
            CornerRadius = new CornerRadius(10, 10, 0, 0),
            Padding = new Thickness(14, 8, 14, 8),
            Cursor = Cursors.SizeAll
        };
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal };
        titlePanel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            VerticalAlignment = VerticalAlignment.Center
        });

        var closeBtn = new Button
        {
            Content = "✕", Width = 28, Height = 28, FontSize = 12,
            Cursor = Cursors.Hand, Background = Brushes.Transparent,
            Foreground = ThemeBrushes.TextSecondaryModern,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.Click += (_, _) => dlg.Close();

        var titleRow = new Grid();
        titleRow.Children.Add(titlePanel);
        titleRow.Children.Add(closeBtn);
        titleBar.Child = titleRow;

        // ponytail 2026-08-28: 分隔线改接管理界面同款文字自适应色（与液态玻璃二级窗口一致）。
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 0)
        };
        separator.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Menu.Separator");

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
        PropertyWindowManager.Instance.PopOutTarget(target, _configService, this, requester: null);

    public void OpenFloatingProperty(object target, Window? requester) =>
        PropertyWindowManager.Instance.PopOutTarget(target, _configService, this, requester: requester);

    // ponytail: drag-out entry — caller passes the cursor's screen position so
    // the new floating PropertyWindow opens where the user dropped the tab,
    // instead of falling through to the right-edge fallback in ResolvePopPosition.
    public void OpenFloatingProperty(object target, Point cursorScreen) =>
        PropertyWindowManager.Instance.PopOutTarget(target, _configService, this, requester: null, cursorScreen: cursorScreen);

    // ponytail: drag-out with explicit initial dimensions — overrides the
    // persisted config size so a freshly dragged-out window starts at a known
    // width/height instead of inheriting whatever the user last resized.
    public void OpenFloatingProperty(object target, Point cursorScreen, Size initialSize) =>
        PropertyWindowManager.Instance.PopOutTarget(target, _configService, this, requester: null, cursorScreen: cursorScreen, initialSize: initialSize);

    /// <summary>Remove the docked tab and clear the docked panel when it shows
    /// <paramref name="target"/> — the docked half of
    /// <see cref="PropertyWindowService.CloseEditorsFor"/>, called by the delete
    /// funnels. Closing the tab fires ActiveTabChanged which re-targets the
    /// panel at the neighbouring tab (or null), so a deleted entity never
    /// lingers as a ghost tab/panel.</summary>
    public void CloseDockedEditorsFor(object target)
    {
        if (target == null) return;
        var key = PropertyWindowManager.TargetKey(target);
        if (string.IsNullOrEmpty(key)) return;
        if (DockedPanel?.Target != null && PropertyWindowManager.TargetKey(DockedPanel.Target) == key)
            DockedPanel.Target = null;
        DockedTabs?.CloseTab(key);
        // The deleted target's tab was the last one — fold the right column
        // (mirrors the header-X flow) so an empty ghost panel doesn't stay open.
        if (DockedTabs != null && DockedTabs.Tabs.Count == 0 && _propertyPanelVisible)
            SetPropertyPanelVisible(false, persist: false);
    }

    /// <summary>Make sure the docked property column is visible. Used by the
    /// workspace dock flow so a freshly-clicked list row lights up the right
    /// panel without the user having to manually un-collapse it first.</summary>
    public void EnsurePropertyPanelVisible()
    {
        if (!_propertyPanelVisible) SetPropertyPanelVisible(true, persist: false);
    }

    /// <summary>
    /// Central Persist wiring for property panels. The docked panel is wired by
    /// the active page (same handlers as the row toggles); floating editors
    /// (gear-button pop-outs, drag-out tabs) had NO Persist — edits were
    /// in-memory only. PropertyWindowManager.OpenFloating calls this so every
    /// editor instance pushes edits through the owning service.
    /// </summary>
    public void WirePropertyPanelPersist(Components.PropertyPanel panel)
    {
        panel.Persist = obj =>
        {
            switch (obj)
            {
                case Zone z:
                    _zoneManager.UpdateZone(z);
                    break;
                case DesktopClock c:
                    _widgetService?.UpdateClock(c);
                    break;
                case DesktopCalendar cal:
                    _widgetService?.UpdateCalendar(cal);
                    break;
                case StickyNote n:
                    _notesService?.UpdateNote(n);
                    break;
                case PanelConfig p:
                    // Panel POCO is mutated in place on the LIVE AppConfig —
                    // save that instance, then repaint the live panel window.
                    _configService.Save(LiveConfig);
                    _panelService?.RefreshAppearance();
                    break;
                case MergedGroupTarget g:
                    // ponytail 2026-08-26: group editor edits live on the master
                    // zone (MergedGroupStyle / membership / window-level fields)
                    // — UpdateZone refreshes the merged window + persists.
                    _zoneManager.UpdateZone(g.Master);
                    break;
                case ZoneItem sub when sub.Type == ItemType.SubFolder:
                    // ponytail 2026-08-26: ZoneItem edits mutate the live instance
                    // in place (sub is the same reference as parentZone.Items[i]),
                    // so persist via the parent Zone. UpdateZone already handles
                    // SaveConfig + ZonesChanged + window.RefreshZone — no need to
                    // touch _zoneWindows here. Reference equality on Contains() is
                    // safe because PropertyPanel edits the live ZoneItem.
                    var parent = _zoneManager.Zones.FirstOrDefault(z => z.Items.Contains(sub));
                    if (parent != null)
                    {
                        _zoneManager.UpdateZone(parent);
                    }
                    else
                    {
                        // ponytail: defensive — the subfolder SHOULD belong to a
                        // Zone. If it doesn't (delete race, dangling ref), skip
                        // rather than throwing into the PropertyPanel callback.
                        System.Diagnostics.Debug.WriteLine(
                            $"[WirePropertyPanelPersist] Subfolder ZoneItem {sub.Id} has no parent Zone — skipping persist");
                    }
                    break;
            }
            // ponytail: edit = intent to keep the docked tab (previously ZonesPage
            // pinned on every edit). Pin the tab for the edited target so it
            // survives preview cleanup when navigating to another section.
            if (panel == DockedPanel && DockedTabs != null)
                DockedTabs.PinTab(Components.PropertyWindowManager.TargetKey(obj));
        };

        // ── 预览回调（Apply 前实时刷新桌面窗口，不落盘）──
        // 字段编辑现在走 PropertyPanel.Save → Preview，只把内存里的改动画到
        // 对应桌面窗口上；只有 Apply 才走上面的 Persist 写盘。这样「应用之前
        // 都是预览模式，取消即回退」的两段式语义才能成立。
        panel.Preview = obj =>
        {
            switch (obj)
            {
                case Zone z:
                    _zoneManager.GetZoneWindow(z.Id)?.RefreshZone(z);
                    break;
                case MergedGroupTarget g:
                    _zoneManager.GetZoneWindow(g.Master.Id)?.RefreshZone(g.Master);
                    break;
                case DesktopClock c:
                    _widgetService?.GetClockWindow(c.Id)?.RefreshAppearance(c);
                    break;
                case DesktopCalendar cal:
                    _widgetService?.GetCalendarWindow(cal.Id)?.RefreshAppearance(cal);
                    break;
                case StickyNote n:
                    if (_notesService?.Windows.TryGetValue(n.Id, out var nw) == true)
                        nw.RefreshAppearance(n);
                    break;
                case PanelConfig p:
                    // Panel POCO 在 live AppConfig 上原地改动 — RefreshAppearance
                    // 读 _zoneManager.GetConfig().Panel,即被预览改动的同一实例。
                    _panelService?.RefreshAppearance();
                    break;
                case ZoneItem sub when sub.Type == ItemType.SubFolder:
                    var parent = _zoneManager.Zones.FirstOrDefault(z => z.Items.Contains(sub));
                    if (parent != null)
                        _zoneManager.GetZoneWindow(parent.Id)?.RefreshZone(parent);
                    break;
            }
            // 预览编辑同样视为「想保留这个 docked tab」。
            if (panel == DockedPanel && DockedTabs != null)
                DockedTabs.PinTab(Components.PropertyWindowManager.TargetKey(obj));
        };

        // ── 状态区操作回调（PropertyPanel 顶部实时状态条）──
        // 列表行右键菜单已取消，显示/锁定/删除/组合/快捷键操作全部移到面板状态区。

        panel.ToggleVisibility = obj =>
        {
            switch (obj)
            {
                case Zone z:
                    // HideZone 自带「无恢复按钮则彻底隐藏」语义（ZoneManager 内部处理）。
                    if (z.IsVisible) _zoneManager.HideZone(z.Id);
                    else _zoneManager.ShowZone(z);
                    break;
                case MergedGroupTarget g:
                    if (g.Master.IsVisible) _zoneManager.HideZone(g.Master.Id);
                    else _zoneManager.ShowZone(g.Master);
                    break;
                case DesktopClock c: ToggleClockWindowImpl(c); break;
                case DesktopCalendar cal: ToggleCalendarWindowImpl(cal); break;
                case StickyNote n: ToggleNoteWindow(n); break;
                case PanelConfig p:
                    p.PanelEnabled = !p.PanelEnabled;
                    _configService.Save(LiveConfig);
                    if (p.PanelEnabled) _panelService?.Show(LiveConfig);
                    else _panelService?.CloseAndClear();
                    break;
            }
        };

        panel.DeleteTarget = obj =>
        {
            switch (obj)
            {
                case Zone z: DeleteZoneWithConfirm(z); break;
                case MergedGroupTarget g:
                    if (MessageBox.Show(_loc.Get("MergePage.DisbandConfirm",
                            g.Master.MergedGroupMembership.DisplayName),
                            _loc["MergePage.DisbandTitle"],
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        DisbandEntireGroup(g.Master);
                    break;
                case DesktopClock c:
                    if (MessageBox.Show(_loc.Get("ClockPage.DeleteConfirm", "Clock"),
                            _loc["ClockPage.DeleteTitle"],
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        DeleteClock(c);
                    break;
                case DesktopCalendar cal:
                    if (MessageBox.Show(_loc.Get("CalendarPage.DeleteConfirm",
                            $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}"),
                            _loc["CalendarPage.DeleteTitle"],
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        DeleteCalendar(cal);
                    break;
                case StickyNote n:
                    if (MessageBox.Show(_loc.Get("StickyNotePage.DeleteConfirm", n.Title),
                            _loc["StickyNotePage.DeleteTitle"],
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        DeleteNote(n);
                    break;
            }
        };

        panel.AddZoneToMerge = z => ShowMergeDialog(z);
        panel.DisbandSingleFromGroup = z => DisbandSingleZoneImpl(z);
        // 快捷键菜单选择/录制完成后，回调面板刷新状态区的当前值文本。
        panel.ShowNoteHotkeyMenu = (note, placement) => NoteHotkeySetImpl(note, placement, () => panel.RefreshStatusArea());
        // PanelHotkey 字段在 AppConfig 上（PanelConfig 自身不含热键），始终用 LiveConfig。
        panel.ShowPanelHotkeyMenu = (_, placement) => PanelHotkeySetImpl(LiveConfig, placement, () => panel.RefreshStatusArea());
        panel.GetPanelHotkeyLabel = () =>
        {
            var h = LiveConfig.PanelHotkey;
            return h.PanelHotkeyEnabled
                ? GetHotkeyLabel(h.PanelHotkeyModifiers, h.PanelHotkeyKey)
                : _loc["Hotkey.None"];
        };
    }

    /// <summary>Resolve a domain target from a TabStrip key ("Type:Id"). Looks
    /// up the matching instance in ZoneManager / widget services. Falls back
    /// to null if the instance was deleted while the tab lingered.</summary>
    object? ResolveTargetFromKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var sep = key.IndexOf(':');
        if (sep < 0) return null;
        var typeName = key.Substring(0, sep);
        var idStr = key.Substring(sep + 1);
        // ponytail 2026-08-25: PanelConfig is a singleton with a fixed literal
        // key ("PanelConfig:panel"), not a Guid — resolve it BEFORE the Guid
        // parse. Previously the hashcode-based key fell into Guid.TryParse,
        // failed, and the docked panel silently showed no field tree.
        if (typeName == nameof(PanelConfig))
            return LiveConfig.Panel;
        if (typeName == nameof(MergedGroupTarget) && Guid.TryParse(idStr, out var groupId))
        {
            // ponytail 2026-08-26: group key carries the stable GroupId — resolve
            // the current master (it may have been promoted after a detach).
            var master = _zoneManager.Zones.FirstOrDefault(z =>
                z.MergedGroupMembership.GroupId == groupId &&
                z.MergedGroupMembership.SubZoneIds.Count > 0);
            return master != null ? MergedGroupTarget.For(master) : null;
        }
        if (!Guid.TryParse(idStr, out var id)) return null;
        return typeName switch
        {
            nameof(Zone) => _zoneManager.Zones.FirstOrDefault(z => z.Id == id),
            nameof(DesktopClock) => _widgetService?.Clocks.FirstOrDefault(c => c.Id == id),
            nameof(DesktopCalendar) => _widgetService?.Calendars.FirstOrDefault(c => c.Id == id),
            nameof(StickyNote) => _notesService?.Notes.FirstOrDefault(n => n.Id == id),
            _ => null,
        };
    }

    /// <summary>The live AppConfig instance held by ZoneManager — the canonical
    /// reference the Panel POCO and its property editor must mutate in place.</summary>
    public AppConfig LiveConfig => _zoneManager.GetConfig();

    public void DeleteZoneWithConfirm(Zone zone)
    {
        if (MessageBox.Show(_loc.Get("Dialog.DeleteZoneMsg", zone.Name),
            _loc["Dialog.DeleteZoneTitle"], MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes)
            _zoneManager.DeleteZone(zone.Id);
    }

    public void ShowMergeDialog(Zone sourceZone) => ShowMergeDialogImpl(sourceZone);
    /// <summary>组合分区页「新建」按钮 — 打开选择独立分区的二级窗口，勾选后创建新组合。</summary>
    public Zone? ShowCreateMergedGroupDialog() => ShowCreateMergedGroupDialogImpl();
    public void DisbandEntireGroup(Zone masterZone)
    {
        var result = MessageBox.Show(
            _loc.Get("Merge.ConfirmDisband", masterZone.MergedGroupMembership.DisplayName),
            _loc["Merge.DisbandTitle"],
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        if (masterZone.MergedGroupMembership.GroupId is not Guid groupId) return;
        _zoneManager.DisbandMergedGroup(groupId);
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
        // ponytail: 2026-08-23 batch wave — zones play first (staggered by position),
        // then widgets/notes continue the same cascade after the zone slots.
        _zoneManager.ShowAll();
        ShowAllWidgets(baseDelayMs: (_zoneManager?.Zones?.Count ?? 0) * HoverExpandBehavior.BatchStaggerMs);
    }
    public void HideAll()
    {
        // ponytail: batch wave — mirror of ShowAll (zones first, then widgets/notes).
        _zoneManager.HideAll();
        HideAllWidgets(baseDelayMs: (_zoneManager?.Zones?.Count ?? 0) * HoverExpandBehavior.BatchStaggerMs);
    }
    public void FullHideAll()
    {
        _zoneManager.FullHideAll();
        FullHideAllWidgets();
    }

    /// <summary>Confirm before performing a full hide-all (used by sidebar).</summary>
    public bool ConfirmHideAll()
    {
        var count = CountHideAllWindows();
        var msg = _loc.Get("Merge.HideAllConfirm", count);
        var res = MessageBox.Show(msg,
            _loc["Merge.HideAll"],
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return res == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 统计「全部隐藏」会影响的窗口数：分区(合并分区算 1 个窗口) + 时钟 + 日历 + 便签。
    /// 旧实现只数 Zone 总数，既漏了小挂件，又把合并分区里的子分区分开算。
    /// </summary>
    int CountHideAllWindows()
    {
        int count = 0;
        if (_zoneManager != null)
        {
            foreach (var z in _zoneManager.Zones)
            {
                // 合并分区里的子分区随主分区显示，不单独算窗口。
                bool isSubZone = z.MergedGroupMembership.GroupId.HasValue
                    && z.MergedGroupMembership.SubZoneIds.Count == 0;
                if (!isSubZone) count++;
            }
        }
        if (_widgetService != null)
        {
            count += _widgetService.Clocks.Count;
            count += _widgetService.Calendars.Count;
        }
        if (_notesService != null) count += _notesService.Notes.Count;
        return count;
    }

    public void ShowAllWidgetsFromVm() => ShowAllWidgets(baseDelayMs: (_zoneManager?.Zones?.Count ?? 0) * HoverExpandBehavior.BatchStaggerMs);
    public void HideAllWidgetsFromVm() => HideAllWidgets(baseDelayMs: (_zoneManager?.Zones?.Count ?? 0) * HoverExpandBehavior.BatchStaggerMs);
    public void FullHideAllWidgetsFromVm() => FullHideAllWidgets();

    void ShowAllWidgets(double baseDelayMs = 0)
    {
        if (_isBatchWidgetOperation) return;
        _isBatchWidgetOperation = true;
        try
        {
            var app = (App)System.Windows.Application.Current;
            // ponytail: 2026-08-23 CRITICAL — iterate SNAPSHOTS of the live collections.
            // ShowNote/ShowClock/ShowCalendar call UpdateNote/UpdateClock/UpdateCalendar,
            // which REPLACE the item inside the very collection being enumerated
            // (Notes[idx] = note). The next MoveNext of the foreach then throws
            // "Collection was modified" OUTSIDE the per-item try/catch, the outer
            // catch { } swallows it, and every widget after the first note is silently
            // skipped — exactly the reported "全部显示后时钟和日历还要额外点击".
            // Verified in D:\BS\he_debug.log: the note line printed, the clock/calendar
            // loops never ran.
            //
            // ponytail: batch wave — each window plays its OWN configured animation at
            // its stagger slot (sorted by screen position), so "Show All" opens as a
            // synchronized left-to-right / top-to-bottom cascade.
            if (_notesService != null)
            {
                int i = 0;
                foreach (var note in _notesService.Notes.OrderBy(n => n.Y).ThenBy(n => n.X).ToList())
                {
                    double delay = baseDelayMs + i * HoverExpandBehavior.BatchStaggerMs;
                    i++;
                    try
                    {
                        if (app.IsNoteWindowOpen(note.Id))
                        {
                            if (_notesService.Windows.TryGetValue(note.Id, out var w)
                                && (!note.IsVisible || !w.IsVisible
                                    || w.MainContent.Visibility != Visibility.Visible
                                    || w.RestoreButton.Visibility == Visibility.Visible))
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    $"[ShowAllWidgets] note {note.Id}: modelVisible={note.IsVisible} -> ShowNote delay={delay}");
                                w.ShowNote(delay);
                            }
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[ShowAllWidgets] note {note.Id}: opening new window delay={delay}");
                            OpenNoteWindow(note);
                            if (delay > 0 && _notesService.Windows.TryGetValue(note.Id, out var nw))
                                nw.PlayEntranceAnimation(delay);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ShowAllWidgets] note {note.Id} failed: {ex}");
                    }
                }
            }
            if (_widgetService != null)
            {
                // ponytail: snapshot iteration — ShowClock's UpdateClock replaces the
                // enumerated element and would abort the loop (same trap as the notes).
                int i = 0;
                foreach (var clock in _widgetService.Clocks.OrderBy(c => c.Y).ThenBy(c => c.X).ToList())
                {
                    double delay = baseDelayMs + i * HoverExpandBehavior.BatchStaggerMs;
                    i++;
                    try
                    {
                        if (_openClockWindows.TryGetValue(clock.Id, out var w) && w is ClockWidget cw)
                        {
                            // ponytail: 2026-08-23 — restore from the WINDOW state, not the
                            // model alone: after "minimize all" the window is a RestoreButton
                            // (content collapsed) and the model may lag in either direction.
                            // ShowClock only runs when the window is not already fully expanded.
                            bool collapsed = !cw.IsVisible
                                || cw.MainContent.Visibility != Visibility.Visible
                                || cw.RestoreButton.Visibility == Visibility.Visible;
                            System.Diagnostics.Debug.WriteLine(
                                $"[ShowAllWidgets] clock {clock.Id}: modelVisible={clock.IsVisible} winVisible={cw.IsVisible} content={cw.MainContent.Visibility} restore={cw.RestoreButton.Visibility} -> show={!clock.IsVisible || collapsed} delay={delay}");
                            if (!clock.IsVisible || collapsed)
                                cw.ShowClock(false, delay);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[ShowAllWidgets] clock {clock.Id}: opening new window delay={delay}");
                            clock.IsVisible = true;
                            OpenClockWindow(clock);
                            if (delay > 0 && _openClockWindows.TryGetValue(clock.Id, out var nw) && nw is ClockWidget ncw)
                                ncw.PlayEntranceAnimation(delay);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ShowAllWidgets] clock {clock.Id} failed: {ex}");
                    }
                }
                // ponytail: snapshot iteration — see the clock loop above.
                int j = 0;
                foreach (var cal in _widgetService.Calendars.OrderBy(c => c.Y).ThenBy(c => c.X).ToList())
                {
                    double delay = baseDelayMs + j * HoverExpandBehavior.BatchStaggerMs;
                    j++;
                    try
                    {
                        if (_openCalendarWindows.TryGetValue(cal.Id, out var w) && w is CalendarWidget caw)
                        {
                            bool collapsed = !caw.IsVisible
                                || caw.MainContent.Visibility != Visibility.Visible
                                || caw.RestoreButton.Visibility == Visibility.Visible;
                            System.Diagnostics.Debug.WriteLine(
                                $"[ShowAllWidgets] cal {cal.Id}: modelVisible={cal.IsVisible} winVisible={caw.IsVisible} content={caw.MainContent.Visibility} restore={caw.RestoreButton.Visibility} -> show={!cal.IsVisible || collapsed} delay={delay}");
                            if (!cal.IsVisible || collapsed)
                                caw.ShowCalendar(false, delay);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[ShowAllWidgets] cal {cal.Id}: opening new window delay={delay}");
                            cal.IsVisible = true;
                            OpenCalendarWindow(cal);
                            if (delay > 0 && _openCalendarWindows.TryGetValue(cal.Id, out var nw) && nw is CalendarWidget ncw)
                                ncw.PlayEntranceAnimation(delay);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ShowAllWidgets] cal {cal.Id} failed: {ex}");
                    }
                }
            }
        }
        catch { }
        finally { _isBatchWidgetOperation = false; }
    }

    void HideAllWidgets(double baseDelayMs = 0)
    {
        if (_isBatchWidgetOperation) return;
        _isBatchWidgetOperation = true;
        try
        {
            // ponytail: 2026-08-23 — route through the widgets' own hide methods instead
            // of raw Window.Hide(). Raw Hide() left the model IsVisible=true, the
            // HoverExpandBehavior expanded and the DWM acrylic enabled on the hidden
            // HWND — and made ShowAllWidgets() skip the windows so they never came back.
            // HideClock/HideCalendar/HideNote respect each widget's EnableRestoreButton
            // (collapse to the RestoreButton, or full hide when disabled) and reset the
            // acrylic gate correctly. Sweep the LIVE app windows (not just the dicts) so
            // a window that lost its dictionary entry can never dodge "hide all".
            //
            // ponytail: batch wave — each window collapses with its OWN configured
            // animation at its stagger slot (sorted by screen position), mirroring the
            // "Show All" cascade.
            var live = System.Windows.Application.Current.Windows.OfType<Window>()
                .Where(w => w is ClockWidget or CalendarWidget or StickyNoteWindow)
                .OrderBy(w => w.Top).ThenBy(w => w.Left)
                .ToList();
            int i = 0;
            foreach (var w in live)
            {
                double delay = baseDelayMs + i * HoverExpandBehavior.BatchStaggerMs;
                i++;
                try
                {
                    switch (w)
                    {
                        case ClockWidget cw when cw.MainContent.Visibility == Visibility.Visible:
                            cw.HideClock(delay);
                            break;
                        case CalendarWidget caw when caw.MainContent.Visibility == Visibility.Visible:
                            caw.HideCalendar(delay);
                            break;
                        case StickyNoteWindow snw when snw.MainContent.Visibility == Visibility.Visible:
                            snw.HideNote(delay);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[HideAllWidgets] {w.GetType().Name} failed: {ex}");
                }
            }
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
            // ponytail: 2026-08-23 — close every live widget window, including sticky
            // notes (owned by NotesService, not the dicts above) and any window that
            // lost its dictionary entry. Then persist IsVisible=false so the management
            // rows, "Show All" and the next startup all agree.
            foreach (var w in System.Windows.Application.Current.Windows.OfType<ClockWidget>().ToList())
                try { w.Close(); } catch { }
            foreach (var w in System.Windows.Application.Current.Windows.OfType<CalendarWidget>().ToList())
                try { w.Close(); } catch { }
            foreach (var w in System.Windows.Application.Current.Windows.OfType<StickyNoteWindow>().ToList())
                try { w.Close(); } catch { }
            _openClockWindows.Clear();
            _openCalendarWindows.Clear();
            _notesService?.Windows.Clear();
            // ponytail: snapshot iteration — UpdateNote/UpdateClock/UpdateCalendar
            // replace the enumerated element and would abort the remaining loop
            // (same "Collection was modified" trap as ShowAllWidgets).
            if (_notesService != null)
                foreach (var note in _notesService.Notes.ToList())
                {
                    note.IsVisible = false;
                    _notesService.UpdateNote(note);
                }
            if (_widgetService != null)
            {
                foreach (var clock in _widgetService.Clocks.ToList())
                {
                    clock.IsVisible = false;
                    _widgetService.UpdateClock(clock);
                }
                foreach (var cal in _widgetService.Calendars.ToList())
                {
                    cal.IsVisible = false;
                    _widgetService.UpdateCalendar(cal);
                }
            }
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

    /// <summary>启动时恢复可见的小组件窗口（位置 + 显示状态持久化，与分区一致）。
    /// 只打开模型里 IsVisible=true 且尚未开窗的组件；隐藏组件保持隐藏。</summary>
    public void RestoreVisibleWidgets()
    {
        var app = (App)System.Windows.Application.Current;
        if (_notesService != null)
        {
            foreach (var note in _notesService.Notes.Where(n => n.IsVisible).ToList())
            {
                try
                {
                    if (!app.IsNoteWindowOpen(note.Id))
                        OpenNoteWindow(note);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] note {note.Id} failed: {ex}"); }
            }
        }
        if (_widgetService != null)
        {
            foreach (var clock in _widgetService.Clocks.Where(c => c.IsVisible).ToList())
            {
                try
                {
                    if (!_openClockWindows.ContainsKey(clock.Id))
                        OpenClockWindow(clock);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] clock {clock.Id} failed: {ex}"); }
            }
            foreach (var cal in _widgetService.Calendars.Where(c => c.IsVisible).ToList())
            {
                try
                {
                    if (!_openCalendarWindows.ContainsKey(cal.Id))
                        OpenCalendarWindow(cal);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[RestoreVisibleWidgets] calendar {cal.Id} failed: {ex}"); }
            }
        }
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
            // ponytail: minimize routes through PanelService.Hide → PanelWindow.HidePanel,
            // the SAME code the panel's own top-right "─" button runs.
            if (_panelService.IsOpen) _panelService.Hide();
            else _panelService.Show(config);
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.ToString(), _loc["Merge.PanelToggleError"]); }
    }

    void TogglePropertyPanel() => SetPropertyPanelVisible(!_propertyPanelVisible, persist: true);

    void SetPropertyPanelVisible(bool visible, bool persist)
    {
        _propertyPanelVisible = visible;
        RightCol.Width = new GridLength(visible ? 360 : 0);
        RightPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!persist) return;
        try
        {
            var config = _configService.Load();
            config.PropertyPanelCollapsed = !visible;
            _configService.Save(config);
        }
        catch { }
    }

    // ── 快捷键预设菜单（便签/面板共用）──
    //
    // 状态区快捷键行的「设置快捷键」按钮弹出预设菜单（无/Alt+X/Ctrl+X/…）+
    // 「新增…」自定义录制。泛化实现消除便签/面板两套重复代码。

    void ShowHotkeyPresetMenuImpl(
        FrameworkElement placement,
        (string Label, int Modifiers, int Key, bool Enabled)[] presets,
        Func<(int Modifiers, int Key, bool Enabled)> getCurrent,
        Action<(int Modifiers, int Key, bool Enabled)> onPick,
        Action onCustom)
    {
        try
        {
            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = placement,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                StaysOpen = false,
                AllowsTransparency = true
            };

            var border = new Border
            {
                Background = ThemeBrushes.BgChromeModern,
                BorderBrush = ThemeBrushes.BorderDefaultModern,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4)
            };

            var stack = new StackPanel();
            var current = getCurrent();
            foreach (var preset in presets)
            {
                var captured = preset;
                string label = captured.Enabled
                    ? GetHotkeyLabel(captured.Modifiers, captured.Key)
                    : _loc["Hotkey.None"];
                bool isCurrent = current.Enabled == captured.Enabled
                    && current.Modifiers == captured.Modifiers
                    && current.Key == captured.Key;

                var item = new Border
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Background = isCurrent ? ThemeBrushes.AccentSolidModern : Brushes.Transparent
                };
                item.Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Foreground = isCurrent ? ThemeBrushes.TextPrimaryModern : ThemeBrushes.TextSecondaryModern
                };
                item.MouseLeftButtonDown += (_, _) =>
                {
                    onPick((captured.Modifiers, captured.Key, captured.Enabled));
                    popup.IsOpen = false;
                };
                item.MouseEnter += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = ThemeBrushes.AccentWashModern; };
                item.MouseLeave += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = Brushes.Transparent; };
                stack.Children.Add(item);
            }

            var separator = new Border
            {
                Height = 1,
                Background = ThemeBrushes.BorderSubtleModern,
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
                Text = _loc["Hotkey.New"],
                FontSize = 11,
                Foreground = ThemeBrushes.AccentSolidModern
            };
            newItem.MouseLeftButtonDown += (_, _) =>
            {
                popup.IsOpen = false;
                onCustom();
            };
            newItem.MouseEnter += (s3, _) => { if (s3 is Border b3) b3.Background = ThemeBrushes.BgHoverModern; };
            newItem.MouseLeave += (s3, _) => { if (s3 is Border b3) b3.Background = Brushes.Transparent; };
            stack.Children.Add(newItem);

            border.Child = stack;
            popup.Child = border;
            popup.IsOpen = true;
        }
        catch { }
    }

    void NoteHotkeySetImpl(StickyNote note, FrameworkElement placement, Action? onSaved = null)
    {
        ShowHotkeyPresetMenuImpl(placement, HotkeyPresets,
            getCurrent: () => (note.HotkeyModifiers, note.HotkeyKey, note.HotkeyEnabled),
            onPick: picked =>
            {
                note.HotkeyEnabled = picked.Enabled;
                note.HotkeyModifiers = picked.Modifiers;
                note.HotkeyKey = picked.Key;
                _notesService?.UpdateNote(note);
                if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
                onSaved?.Invoke();
            },
            onCustom: () => ShowNoteHotkeyRecorderDialogImpl(note, onSaved));
    }

    void PanelHotkeySetImpl(AppConfig cfg, FrameworkElement placement, Action? onSaved = null)
    {
        ShowHotkeyPresetMenuImpl(placement, PanelHotkeyPresets,
            getCurrent: () => (cfg.PanelHotkey.PanelHotkeyModifiers, cfg.PanelHotkey.PanelHotkeyKey, cfg.PanelHotkey.PanelHotkeyEnabled),
            onPick: picked =>
            {
                cfg.PanelHotkey.PanelHotkeyEnabled = picked.Enabled;
                cfg.PanelHotkey.PanelHotkeyModifiers = picked.Modifiers;
                cfg.PanelHotkey.PanelHotkeyKey = picked.Key;
                if (System.Windows.Application.Current is App app)
                {
                    if (picked.Enabled) app.RegisterPanelHotkey(picked.Modifiers, picked.Key);
                    else app.UnregisterPanelHotkey();
                }
                _configService.Save(cfg);
                onSaved?.Invoke();
            },
            onCustom: () => ShowPanelHotkeyRecorderDialogImpl(cfg, onSaved));
    }

    // ── 快捷键录制对话框（泛化：UI 骨架共享，保存回调按目标类型分派）──

    void ShowHotkeyRecorderDialogImpl(Action<int, int> onSave, Action? onSaved = null)
    {
        var dlg = new Window
        {
            Title = _loc["Hotkey.Record"],
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
            Background = ThemeBrushes.BgChromeModern,
            BorderBrush = ThemeBrushes.BorderDefaultModern,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10)
        };

        var grid = new Grid { Margin = new Thickness(18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBar = new Border { Height = 30, Background = Brushes.Transparent, Cursor = Cursors.SizeAll, Margin = new Thickness(0, 0, 0, 8) };
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };
        titleBar.Child = new TextBlock
        {
            Text = _loc["Hotkey.Record"],
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        // ponytail 2026-08-28: 与液态玻璃二级窗口一致 — 标题栏与主体之间补一条
        // 自适应分隔线（管理界面文字颜色同款）。
        var separator = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 0, 0, 10)
        };
        separator.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Menu.Separator");
        Grid.SetRow(separator, 1);
        grid.Children.Add(separator);

        var instruction = new TextBlock
        {
            Text = _loc["Hotkey.PressHint"],
            FontSize = 12, Foreground = ThemeBrushes.TextSecondaryModern,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instruction, 2);
        grid.Children.Add(instruction);

        var hotkeyDisplay = new TextBox
        {
            Text = "", IsReadOnly = true, FontSize = 16, FontWeight = FontWeights.Bold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            Background = ThemeBrushes.BgHoverModern,
            BorderBrush = ThemeBrushes.BgHoverModern,
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hotkeyDisplay, 3);
        grid.Children.Add(hotkeyDisplay);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelButton = new Button
        {
            Content = _loc["Common.Cancel"], Width = 60, Height = 28, FontSize = 11, Cursor = Cursors.Hand,
            Style = (Style)FindResource("OutlineBtn"),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.Click += (_, _) => dlg.Close();
        var saveButton = new Button
        {
            Content = _loc["Common.Save"], Width = 60, Height = 28, FontSize = 11, Cursor = Cursors.Hand,
            Style = (Style)FindResource("FillBtn"),
            IsEnabled = false
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(saveButton);
        Grid.SetRow(buttonPanel, 4);
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
            saveButton.Background = ThemeBrushes.BtnSolidModern;
            saveButton.Foreground = ThemeBrushes.BtnOnModern;
            isRecording = false;
        };

        saveButton.Click += (_, _) =>
        {
            onSave(recordedModifiers, recordedKey);
            onSaved?.Invoke();
            dlg.Close();
        };

        dlg.ShowDialog();
    }

    void ShowNoteHotkeyRecorderDialogImpl(StickyNote note, Action? onSaved = null)
    {
        ShowHotkeyRecorderDialogImpl((mods, key) =>
        {
            if (note.CustomHotkeys == null) note.CustomHotkeys = new List<CustomHotkey>();
            note.CustomHotkeys.Add(new CustomHotkey { Modifiers = mods, Key = key });
            note.HotkeyEnabled = true;
            note.HotkeyModifiers = mods;
            note.HotkeyKey = key;
            _notesService?.UpdateNote(note);
            if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
        }, onSaved);
    }

    void ShowPanelHotkeyRecorderDialogImpl(AppConfig cfg, Action? onSaved = null)
    {
        ShowHotkeyRecorderDialogImpl((mods, key) =>
        {
            cfg.PanelHotkey.PanelHotkeyEnabled = true;
            cfg.PanelHotkey.PanelHotkeyModifiers = mods;
            cfg.PanelHotkey.PanelHotkeyKey = key;
            _configService.Save(cfg);
            if (System.Windows.Application.Current is App app) app.RegisterPanelHotkey(mods, key);
        }, onSaved);
    }

    void ShowMergedGroupContextMenuImpl(Zone masterZone, Button placementBtn)
    {
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade,
            StaysOpen = false
        };

        var bgBrush = ThemeBrushes.BgChromeModern;
        var fgBrush = ThemeBrushes.TextPrimaryModern;
        var hoverBrush = ThemeBrushes.AccentWashModern;

        var menuBorder = new Border
        {
            Background = bgBrush,
            BorderBrush = ThemeBrushes.BorderDefaultModern,
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
            stack.Children.Add(MakeItem(_loc["Merge.DisbandSingle"], () => DisbandSingleZoneImpl(masterZone)));
        stack.Children.Add(MakeItem(_loc["Merge.DisbandAll"], () => DisbandEntireGroup(masterZone)));
        stack.Children.Add(new Border { Height = 1, Background = ThemeBrushes.BorderSubtleModern, Margin = new Thickness(6, 4, 6, 4) });
        stack.Children.Add(MakeItem(_loc["Merge.AddZone"], () => ShowMergeDialogImpl(masterZone)));
        if (_zoneManager.Zones.Any(z => z.MergedGroupMembership.SubZoneIds.Count > 0 && z.Id != masterZone.Id))
            stack.Children.Add(MakeItem(_loc["Merge.MergeBtn"], () => MergeWithAnotherGroupImpl(masterZone)));

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
        var dialogTitle = _loc["Merge.SelectZoneToDisband"];
        var dialog = new Window
        {
            Title = dialogTitle, Width = 300, Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize
        };

        var bgBorder = new Border
        {
            Background = ThemeBrushes.BgChromeModern,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid()
        };

        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = _loc["Merge.SelectZoneLabel"],
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var listBox = new ListBox
        {
            Background = ThemeBrushes.BgHoverModern,
            Foreground = ThemeBrushes.TextPrimaryModern,
            BorderBrush = ThemeBrushes.BorderSubtleModern,
            BorderThickness = new Thickness(1), FontSize = 12, Margin = new Thickness(0, 0, 0, 12)
        };
        foreach (var subId in masterZone.MergedGroupMembership.SubZoneIds)
        {
            var subZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == subId);
            if (subZone != null)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
                if (!string.IsNullOrEmpty(subZone.IconChar))
                    if (Helpers.IconGlyph.CreateIcon(subZone.IconChar, ThemeBrushes.TextTertiaryModern, fontSize: 14, pathSize: 14) is { } subIcon)
                        itemPanel.Children.Add(subIcon);
                itemPanel.Children.Add(new TextBlock { Text = subZone.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.TextPrimaryModern });
                listBox.Items.Add(new ListBoxItem { Content = itemPanel, Tag = subZone, Padding = new Thickness(6, 4, 6, 4) });
            }
        }
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        Grid.SetRow(listBox, 1);
        grid.Children.Add(listBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = _loc["Common.Cancel"], Width = 70, Height = 28, Style = (Style)FindResource("OutlineBtn"), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dialog.Close();
        var disbandBtn = new Button { Content = _loc["Merge.DisbandSingle"], Width = 80, Height = 28, Style = (Style)FindResource("FillBtn"), FontSize = 11, Cursor = Cursors.Hand };
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
        var otherGroups = _zoneManager.Zones.Where(z => z.MergedGroupMembership.SubZoneIds.Count > 0 && z.Id != sourceMaster.Id).ToList();
        if (otherGroups.Count == 0)
        {
            MessageBox.Show(_loc["Merge.NoTargets"],
                _loc["Merge.Info"], MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mergeTargetTitle = _loc["Merge.SelectTarget"];
        var dialog = new Window { Title = mergeTargetTitle, Width = 360, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };

        var bgBorder = new Border { Background = ThemeBrushes.BgChromeModern, CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid() };
        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = _loc["Merge.SelectTargetLabel"], FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrushes.TextPrimaryModern, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var listBox = new ListBox { Background = ThemeBrushes.BgHoverModern, Foreground = ThemeBrushes.TextPrimaryModern, BorderBrush = ThemeBrushes.BorderSubtleModern, BorderThickness = new Thickness(1), FontSize = 12, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var targetGroup in otherGroups)
        {
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            if (!string.IsNullOrEmpty(targetGroup.MergedGroupMembership.Icon))
                if (Helpers.IconGlyph.CreateIcon(targetGroup.MergedGroupMembership.Icon, ThemeBrushes.TextTertiaryModern, fontSize: 14, pathSize: 14) is { } groupIcon)
                    itemPanel.Children.Add(groupIcon);
            itemPanel.Children.Add(new TextBlock { Text = targetGroup.MergedGroupMembership.DisplayName, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.TextPrimaryModern });
            listBox.Items.Add(new ListBoxItem { Content = itemPanel, Tag = targetGroup, Padding = new Thickness(6, 4, 6, 4) });
        }
        if (listBox.Items.Count > 0) listBox.SelectedIndex = 0;
        Grid.SetRow(listBox, 1);
        grid.Children.Add(listBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = _loc["Common.Cancel"], Width = 70, Height = 28, Style = (Style)FindResource("OutlineBtn"), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dialog.Close();
        var mergeBtn = new Button { Content = _loc["Merge.MergeBtn"], Width = 80, Height = 28, Style = (Style)FindResource("FillBtn"), FontSize = 11, Cursor = Cursors.Hand };
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

    Zone? ShowCreateMergedGroupDialogImpl()
    {
        var eligibleZones = _zoneManager.Zones
            .Where(z => z.MergedGroupMembership.GroupId == null)
            .ToList();

        var dlg = new Window { Title = _loc["Merge.Title"], Width = 360, Height = 380, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this, ResizeMode = ResizeMode.NoResize };
        var bgBorder = new Border { Background = ThemeBrushes.BgChromeModern, CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid() };
        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = _loc["Merge.SelectZonesToMerge"], FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrushes.TextPrimaryModern, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var selectAllPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var selectAllCheckBox = new CheckBox { Content = _loc["Merge.SelectAll"], Foreground = ThemeBrushes.TextPrimaryModern, FontSize = 12, IsChecked = false };
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
                if (Helpers.IconGlyph.CreateIcon(z.IconChar, ThemeBrushes.TextTertiaryModern, fontSize: 14, pathSize: 14) is { } zoneIcon)
                    itemPanel.Children.Add(zoneIcon);
            itemPanel.Children.Add(new TextBlock { Text = z.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.TextPrimaryModern });
            var checkBox = new CheckBox { Content = itemPanel, Tag = z, Margin = new Thickness(0, 2, 0, 2), Foreground = ThemeBrushes.TextPrimaryModern, FontSize = 12 };
            checkBoxes.Add(checkBox);
            zonesPanel.Children.Add(checkBox);
        }
        scrollViewer.Content = zonesPanel;
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);

        selectAllCheckBox.Checked += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        selectAllCheckBox.Unchecked += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };

        Zone? result = null;
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = _loc["Rename.Cancel"], Width = 70, Height = 28, Style = (Style)FindResource("OutlineBtn"), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dlg.Close();
        var createBtn = new Button { Content = _loc["Merge.CreateGroupBtn"], Width = 90, Height = 28, Style = (Style)FindResource("FillBtn"), FontSize = 11, Cursor = Cursors.Hand, IsEnabled = eligibleZones.Count >= 2 };
        createBtn.Click += (_, _) =>
        {
            var selected = checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Tag as Zone).Where(z => z != null).ToList();
            if (selected.Count < 2) return; // 按钮已禁用,此处仅作防御,不再弹提示
            var master = selected[0]!;
            foreach (var tz in selected.Skip(1)) _zoneManager.MergeZones(master.Id, tz!.Id);
            result = master;
            dlg.Close();
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(createBtn);
        Grid.SetRow(btnRow, 3);
        grid.Children.Add(btnRow);

        // 勾选不足两个时直接禁用创建按钮,不再弹「请至少选择两个」提示。
        void UpdateCreateEnabled() => createBtn.IsEnabled = checkBoxes.Count(cb => cb.IsChecked == true) >= 2;
        foreach (var cb in checkBoxes)
        {
            cb.Checked += (_, _) => UpdateCreateEnabled();
            cb.Unchecked += (_, _) => UpdateCreateEnabled();
        }

        WrapDialogWithDarkTitleBar(dlg, bgBorder, _loc["Merge.Title"]);
        dlg.ShowDialog();
        return result;
    }

    void ShowMergeDialogImpl(Zone sourceZone)
    {
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
        var bgBorder = new Border { Background = ThemeBrushes.BgChromeModern, CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Child = new Grid() };
        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock { Text = _loc["Merge.SelectZonesToMerge"], FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = ThemeBrushes.TextPrimaryModern, Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var selectAllPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var selectAllCheckBox = new CheckBox { Content = _loc["Merge.SelectAll"], Foreground = ThemeBrushes.TextPrimaryModern, FontSize = 12, IsChecked = false };
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
                if (Helpers.IconGlyph.CreateIcon(z.IconChar, ThemeBrushes.TextTertiaryModern, fontSize: 14, pathSize: 14) is { } zoneIcon)
                    itemPanel.Children.Add(zoneIcon);
            itemPanel.Children.Add(new TextBlock { Text = z.Name, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrushes.TextPrimaryModern });
            var checkBox = new CheckBox { Content = itemPanel, Tag = z, Margin = new Thickness(0, 2, 0, 2), Foreground = ThemeBrushes.TextPrimaryModern, FontSize = 12 };
            checkBoxes.Add(checkBox);
            zonesPanel.Children.Add(checkBox);
        }
        scrollViewer.Content = zonesPanel;
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);

        selectAllCheckBox.Checked += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = true; };
        selectAllCheckBox.Unchecked += (_, _) => { foreach (var cb in checkBoxes) cb.IsChecked = false; };

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = _loc["Rename.Cancel"], Width = 70, Height = 28, Style = (Style)FindResource("OutlineBtn"), FontSize = 11, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        cancelBtn.Click += (_, _) => dlg.Close();
        var mergeBtn = new Button { Content = _loc["Merge.MergeBtn"], Width = 80, Height = 28, Style = (Style)FindResource("FillBtn"), FontSize = 11, Cursor = Cursors.Hand };
        mergeBtn.Click += (_, _) =>
        {
            var selected = checkBoxes.Where(cb => cb.IsChecked == true).Select(cb => cb.Tag as Zone).Where(z => z != null).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show(_loc["Merge.SelectAtLeastOne"], _loc["Merge.Info"], MessageBoxButton.OK, MessageBoxImage.Information);
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
            // ponytail: 2026-08-26 — route EVERY show/hide through the widget's own
            // ShowClock/HideClock and decide the direction from the RestoreButton (the
            // minimized-state indicator), not MainContent.Visibility. MainContent misread
            // windows mid-animation / full-hidden, letting the widget strand as a
            // full-size transparent ghost with the RestoreButton floating in it (the
            // reported "四周有透明边框").
            bool show = !cw.IsVisible || cw.RestoreButton.Visibility == Visibility.Visible;
#if DEBUG
            DzTrace.Log($"[Toggle] ToggleClockWindow -> {(show ? "ShowClock" : "HideClock")} (winVisible={cw.IsVisible} content={cw.MainContent.Visibility} btn={cw.RestoreButton.Visibility})");
#endif
            if (show) cw.ShowClock();
            else cw.HideClock();
        }
        else
        {
#if DEBUG
            DzTrace.Log($"[Toggle] ToggleClockWindow -> window not open, OpenClockWindow (modelVisible={clock.IsVisible})");
#endif
            clock.IsVisible = true;
            OpenClockWindow(clock);
        }
    }

    void ToggleCalendarWindowImpl(DesktopCalendar cal)
    {
        if (_openCalendarWindows.TryGetValue(cal.Id, out var w) && w is CalendarWidget caw)
        {
            bool show = !caw.IsVisible || caw.RestoreButton.Visibility == Visibility.Visible;
#if DEBUG
            DzTrace.Log($"[Toggle] ToggleCalendarWindow -> {(show ? "ShowCalendar" : "HideCalendar")} (winVisible={caw.IsVisible} content={caw.MainContent.Visibility} btn={caw.RestoreButton.Visibility})");
#endif
            if (show) caw.ShowCalendar();
            else caw.HideCalendar();
        }
        else
        {
#if DEBUG
            DzTrace.Log($"[Toggle] ToggleCalendarWindow -> window not open, OpenCalendarWindow (modelVisible={cal.IsVisible})");
#endif
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
            "settings" => BuildSettingsPage(),
            _          => new ZonesPage(this, _viewModel, _zoneManager)
        };
        try { MainContent.Content = page; ApplyLoc(); } catch { }
        try { UpdateBreadcrumb(section, GetSectionCountLabel(section)); _lastSection = section; _lastCountLabel = GetSectionCountLabel(section); } catch { }
        try { if (SideNav != null) SideNav.ActiveSection = section; } catch { }
        // ponytail: section switch drops preview tabs so the strip doesn't carry
        // stale entries from the previous page. Pinned tabs (edited before)
        // survive — they're "real" interest, not browse-previews.
        try { DockedTabs?.CloseAllPreviewTabs(); } catch { }
    }

    private void SideNav_SectionChanged(object? sender, string section)
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
            "zones"    => _loc.Get("Manage.Sidebar.Zones", zones),
            "merged"   => _loc.Get("Manage.Sidebar.MergedGroups", merged),
            "panel"    => _loc["Manage.Sidebar.Panel"],
            "calendar" => _loc.Get("Manage.Sidebar.Calendars", calendars),
            "clock"    => _loc.Get("Manage.Sidebar.Clocks", clocks),
            "sticky"   => _loc.Get("Manage.Sidebar.Notes", notes),
            "about"    => _loc["Manage.Sidebar.About"],
            "settings" => _loc["Manage.Sidebar.Settings"],
            _          => ""
        };
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }

    // ponytail 2026-08-27: 构造 SettingsPage 并注入热键编辑/双击开关回调。
    SettingsPage BuildSettingsPage()
    {
        var page = new SettingsPage(_configService, App.UpdateService);
        var loc = LocalizationService.Instance;

        page.GetShowAllHotkeyLabel = () => HotkeyText(LiveConfig.ShowAllHotkey.Modifiers, LiveConfig.ShowAllHotkey.Key);
        page.GetMinimizeAllHotkeyLabel = () => HotkeyText(LiveConfig.MinimizeAllHotkey.Modifiers, LiveConfig.MinimizeAllHotkey.Key);
        page.GetHideAllHotkeyLabel = () => HotkeyText(LiveConfig.HideAllHotkey.Modifiers, LiveConfig.HideAllHotkey.Key);
        // getter 是在页面构造之后才注入的，注入后立刻刷新一次当前值文本。
        page.RefreshHotkeyLabels();

        page.OnShowAllHotkeyPicked = btn => GlobalHotkeyPicker(btn, LiveConfig.ShowAllHotkey,
            h => { LiveConfig.ShowAllHotkey.Modifiers = h.Modifiers; LiveConfig.ShowAllHotkey.Key = h.Key;
                   _configService.Save(LiveConfig);
                   if (Application.Current is App app) app.ReRegisterGlobalHotkeys();
                   page.RefreshHotkeyLabels(); },
            ShowAllHotkeyPresets);
        page.OnMinimizeAllHotkeyPicked = btn => GlobalHotkeyPicker(btn, LiveConfig.MinimizeAllHotkey,
            h => { LiveConfig.MinimizeAllHotkey.Modifiers = h.Modifiers; LiveConfig.MinimizeAllHotkey.Key = h.Key;
                   _configService.Save(LiveConfig);
                   if (Application.Current is App app) app.ReRegisterGlobalHotkeys();
                   page.RefreshHotkeyLabels(); },
            MinimizeAllHotkeyPresets);
        page.OnHideAllHotkeyPicked = btn => GlobalHotkeyPicker(btn, LiveConfig.HideAllHotkey,
            h => { LiveConfig.HideAllHotkey.Modifiers = h.Modifiers; LiveConfig.HideAllHotkey.Key = h.Key;
                   _configService.Save(LiveConfig);
                   if (Application.Current is App app) app.ReRegisterGlobalHotkeys();
                   page.RefreshHotkeyLabels(); },
            HideAllHotkeyPresets);

        page.OnDoubleClickToggleShowHideChanged = enabled =>
        {
            LiveConfig.DoubleClickToggleShowHide = enabled;
            if (Application.Current is App app) app.SetDesktopDoubleClickEnabled(enabled);
            _configService.Save(LiveConfig);
        };
        return page;
    }

    void GlobalHotkeyPicker(FrameworkElement placement, CustomHotkey current, Action<CustomHotkey> onSaved,
        (string Label, int Modifiers, int Key, bool Enabled)[] presets)
    {
        ShowHotkeyPresetMenuImpl(placement, presets,
            getCurrent: () => (current.Modifiers, current.Key, current.Modifiers != 0 && current.Key != 0),
            onPick: picked => onSaved(picked.Enabled
                ? new CustomHotkey { Modifiers = picked.Modifiers, Key = picked.Key }
                : new CustomHotkey { Modifiers = 0, Key = 0 }),
            onCustom: () =>
            {
                ShowHotkeyRecorderDialogImpl((mods, vk) =>
                {
                    current.Modifiers = mods;
                    current.Key = vk;
                    onSaved(current);
                });
            });
    }

    static string HotkeyText(int modifiers, int vk)
    {
        if (modifiers == 0 || vk == 0) return LocalizationService.Instance["Settings.Hotkey.NotSet"];
        return GetHotkeyLabel(modifiers, vk);
    }
}