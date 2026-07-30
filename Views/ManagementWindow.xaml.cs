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
    private static readonly SolidColorBrush ActiveBg = new(Color.FromRgb(0x7C, 0x3A, 0xED));
    private static readonly SolidColorBrush InactiveBg = new(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));

    // Track widget windows opened from management window
    // Notes are managed by App.xaml.cs — use IsNoteWindowOpen() to check
    private readonly Dictionary<Guid, Window> _openClockWindows = new();
    private readonly Dictionary<Guid, Window> _openCalendarWindows = new();

    private bool IsNoteWindowOpen(Guid id) => ((App)System.Windows.Application.Current).IsNoteWindowOpen(id);
    private Window? GetNoteWindow(Guid id) => ((App)System.Windows.Application.Current)._noteWindows.TryGetValue(id, out var w) ? w : null;
    // Track toggle dots for animation
    private readonly Dictionary<Guid, Border> _noteToggleDots = new();
    private readonly Dictionary<Guid, Border> _clockToggleDots = new();
    private readonly Dictionary<Guid, Border> _calendarToggleDots = new();
    private readonly Dictionary<Guid, Border> _zoneToggleDots = new();

    // Track expanded/collapsed state for merged zones
    private readonly Dictionary<Guid, bool> _expandedZones = new();

    // Guard flag to prevent re-entrant batch widget operations
    private bool _isBatchWidgetOperation;

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
        _viewModel = new ManagementViewModel(zoneManager, configService);
        DataContext = this;

        _zoneManager.ZonesChanged += RefreshAll;
        _zoneManager.ZonesChanged += RefreshAllStateButtons;
        _zoneManager.ZoneVisibilityChanged += OnZoneVisibilityChanged;
        if (_notesService != null)
        {
            _notesService.NotesChanged += RefreshAll;
            _notesService.NotesChanged += SyncNoteWindows;
        }
        if (_widgetService != null)
        {
            _widgetService.ClocksChanged += RefreshAll;
            _widgetService.ClocksChanged += SyncClockWindows;
            _widgetService.CalendarsChanged += RefreshAll;
            _widgetService.CalendarsChanged += SyncCalendarWindows;
        }
        Loaded += (_, _) =>
        {
            try { RefreshAll(); } catch { }
            try { ApplyLoc(); } catch { }
            try { RefreshAllStateButtons(); } catch { }
            try
            {
                var config = _configService.Load();
                if (config.PanelEnabled)
                    _panelService?.Show(config);
                if (_panelService?.IsOpen == true && NewPanelBtn != null)
                    NewPanelBtn.ToolTip = _loc["Manage.PanelOpen"];
            }
            catch { }
        };
        _loc.LanguageChanged += _ => { try { ApplyLoc(); } catch { } };
        if (_panelService != null)
            _panelService.WindowClosed += () =>
            {
                if (NewPanelBtn != null) NewPanelBtn.ToolTip = _loc["Manage.NewPanel"];
                Dispatcher.BeginInvoke(new Action(() => RefreshPanelCard()));
            };
    }

    void ApplyLoc()
    {
        try
        {
            var cn = _loc.CurrentLanguage == Services.Language.Chinese;
            Title = _loc["Manage.Title"];
            if (TitleText != null) TitleText.Text = _loc["Manage.Title"];
            if (EmptyStateText != null) EmptyStateText.Text = _loc["Manage.EmptyHint"];
            if (StartWithWindowsLabel != null) StartWithWindowsLabel.Text = _loc["Manage.StartWithWindows"];
            if (NewZoneButton != null) NewZoneButton.ToolTip = _loc["Manage.NewZone"];
            if (BtnShowAll != null) BtnShowAll.Content = _loc["Manage.ShowAll"];
            if (BtnMinAll != null) BtnMinAll.Content = _loc["Manage.HideBtn"];
            if (BtnFullHideAll != null) BtnFullHideAll.Content = cn ? "完全隐藏" : "Full Hide";

            if (ZonesHeader != null) ZonesHeader.Text = _loc["Manage.Zones"];
            if (NotesHeader != null) NotesHeader.Text = _loc["Manage.Notes"];
            if (ClocksHeader != null) ClocksHeader.Text = _loc["Manage.Clocks"];
            if (CalendarHeader != null) CalendarHeader.Text = _loc["Manage.Calendars"];

            if (NewNoteBtn != null) NewNoteBtn.ToolTip = _loc["Manage.NewNote"];
            if (NewClockBtn != null) NewClockBtn.ToolTip = _loc["Manage.NewClock"];
            if (NewCalendarBtn != null) NewCalendarBtn.ToolTip = _loc["Manage.NewCalendar"];
            if (PanelHeader != null) PanelHeader.Text = _loc["Panel.Title"];
            if (PanelHotkeyBtn != null) PanelHotkeyBtn.Content = cn ? "快捷键" : "Hotkey";
            if (NewPanelBtn != null)
            {
                NewPanelBtn.Content = "📋";
                NewPanelBtn.ToolTip = _loc["Manage.NewPanel"];
            }

            try { RefreshAll(); } catch { }
            try { RefreshAllStateButtons(); } catch { }
        }
        catch { }
    }

    public void RefreshAll()
    {
        try
        {
            UpdateEmptyState();
            RefreshZonesList();
            RefreshPanelCard();
            RefreshNotesList();
            RefreshClocksList();
            RefreshCalendarsList();
        }
        catch { }
    }

    void UpdateEmptyState()
    {
        try
        {
            bool hasAny = (_zoneManager?.Zones.Count ?? 0) > 0
                || (_notesService?.Notes.Count ?? 0) > 0
                || (_widgetService?.Clocks.Count ?? 0) > 0
                || (_widgetService?.Calendars.Count ?? 0) > 0;
            if (EmptyStateText != null) EmptyStateText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
            if (ZonesSection != null) ZonesSection.Visibility = (_zoneManager?.Zones.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch { }
    }

    void RefreshZonesList()
    {
        try
        {
            if (ZonesSection == null || ZonesStack == null) return;

            var zones = _zoneManager?.Zones;
            if (zones == null || zones.Count == 0)
            {
                ZonesSection.Visibility = Visibility.Collapsed;
                return;
            }

            ZonesSection.Visibility = Visibility.Visible;
            ZonesStack.Children.Clear();

            // Separate master zones (merged groups) and regular zones
            var masterZones = zones.Where(z => z.MergedSubZoneIds.Count > 0).ToList();
            var regularZones = zones.Where(z => z.MergedSubZoneIds.Count == 0 && !z.MergedGroupId.HasValue).ToList();

            // Display master zones (merged groups) first
            foreach (var zone in masterZones)
            {
                AddMergedZoneCard(zone);
            }

            // Display regular zones
            foreach (var zone in regularZones)
            {
                AddRegularZoneCard(zone);
            }
        }
        catch { }
    }

    void AddMergedZoneCard(Zone masterZone)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        bool isExpanded = _expandedZones.GetValueOrDefault(masterZone.Id, false);

        // Main card border
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Main row: expand button + icon + name + info + controls
        var mainRow = new Grid();
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // expand
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name+info
        mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // controls

        // Expand/collapse button
        var expandBtn = new Border
        {
            Width = 16,
            Height = 16,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Tag = masterZone
        };
        var expandIcon = new TextBlock
        {
            Text = isExpanded ? "▼" : "▶",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0xA0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Name = "ExpandIcon"
        };
        expandBtn.Child = expandIcon;
        expandBtn.MouseLeftButtonDown += ExpandBtn_Click;
        Grid.SetColumn(expandBtn, 0);
        mainRow.Children.Add(expandBtn);

        // Icon
        var iconBorder = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var iconText = new TextBlock
        {
            Text = masterZone.MergedGroupIcon,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = iconText;
        Grid.SetColumn(iconBorder, 1);
        mainRow.Children.Add(iconBorder);

        // Name and info
        var infoStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0)
        };
        var nameText = new TextBlock
        {
            Text = masterZone.MergedGroupName,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
        };
        infoStack.Children.Add(nameText);

        var infoText = new TextBlock
        {
            Text = $"{masterZone.MergedSubZoneIds.Count + 1} {(cn ? "个分区" : "zones")} | {masterZone.Items.Count} {(cn ? "个项目" : "items")}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        infoStack.Children.Add(infoText);
        Grid.SetColumn(infoStack, 2);
        mainRow.Children.Add(infoStack);

        // Controls
        var controlsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Toggle switch
        var toggleBorder = new Border
        {
            Width = 40,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Tag = masterZone,
            Margin = new Thickness(0, 0, 3, 0)
        };
        // Determine initial state based on EnableRestoreButton
        bool isMasterActive;
        if (masterZone.EnableRestoreButton)
        {
            isMasterActive = _zoneManager.IsZoneShown(masterZone.Id) && !_zoneManager.IsZoneMinimized(masterZone.Id);
        }
        else
        {
            isMasterActive = _zoneManager.IsZoneShown(masterZone.Id);
        }
        var toggleDot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = isMasterActive
                ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
            HorizontalAlignment = isMasterActive ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggleBorder.Child = toggleDot;
        toggleBorder.MouseLeftButtonDown += StateToggle_Click;
        _zoneToggleDots[masterZone.Id] = toggleDot;
        controlsStack.Children.Add(toggleBorder);

        // Hide button
        var hideBtn = new Button
        {
            Width = 28,
            Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 10,
            Content = "\u2715",
            Tag = masterZone,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = cn ? "隐藏" : "Hide"
        };
        hideBtn.Click += StateFullHide_Click;
        controlsStack.Children.Add(hideBtn);

        // Disband button
        var disbandBtn = new Button
        {
            Width = 28,
            Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x88, 0x88)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 10,
            Content = "\u2716",
            Tag = masterZone,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = cn ? "解散组合" : "Disband"
        };
        disbandBtn.Click += ZoneDisband_Click;
        controlsStack.Children.Add(disbandBtn);

        // Add zone to group button
        var addBtn = new Button
        {
            Width = 28,
            Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0x8B, 0xEF)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 11,
            Content = "\u271A",
            Tag = masterZone,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = cn ? "添加分区" : "Add Zone"
        };
        addBtn.Click += ZoneMerge_Click;
        controlsStack.Children.Add(addBtn);

        // Settings button
        var settingsBtn = new Button
        {
            Content = "\uD83C\uDFA8",
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            Cursor = Cursors.Hand,
            FontSize = 14,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = masterZone
        };
        settingsBtn.Click += MergedGroupSettings_Click;
        controlsStack.Children.Add(settingsBtn);

        Grid.SetColumn(controlsStack, 3);
        mainRow.Children.Add(controlsStack);

        Grid.SetRow(mainRow, 0);
        grid.Children.Add(mainRow);

        // Sub-zones list (collapsible)
        var subZonePanel = new StackPanel
        {
            Margin = new Thickness(20, 4, 0, 0),
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed
        };

        foreach (var subId in masterZone.MergedSubZoneIds)
        {
            var subZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == subId);
            if (subZone != null)
            {
                var subItem = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var subIcon = new TextBlock
                {
                    Text = subZone.IconChar,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                subItem.Children.Add(subIcon);

                var subName = new TextBlock
                {
                    Text = subZone.Name,
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                subItem.Children.Add(subName);

                // Switch to this sub-zone button
                var switchBtn = new Button
                {
                    Content = cn ? "切换" : "Switch",
                    FontSize = 9,
                    Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    Cursor = Cursors.Hand,
                    Tag = (masterZone, subZone)
                };
                switchBtn.Click += SwitchToSubZone_Click;
                subItem.Children.Add(switchBtn);

                subZonePanel.Children.Add(subItem);
            }
        }

        Grid.SetRow(subZonePanel, 1);
        grid.Children.Add(subZonePanel);

        card.Child = grid;
        ZonesStack.Children.Add(card);
    }

    void AddRegularZoneCard(Zone zone)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: icon + name + items count
        var infoStack = new StackPanel();
        var iconRow = new StackPanel { Orientation = Orientation.Horizontal };

        var iconBorder = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var iconText = new TextBlock
        {
            Text = zone.IconChar,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = iconText;
        iconRow.Children.Add(iconBorder);

        var nameText = new TextBlock
        {
            Text = zone.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        };
        iconRow.Children.Add(nameText);
        infoStack.Children.Add(iconRow);

        var itemsText = new TextBlock
        {
            Text = $"{(cn ? "项目" : "Items")}: {zone.Items.Count}",
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            Margin = new Thickness(34, 2, 0, 0)
        };
        infoStack.Children.Add(itemsText);
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        // Right: controls
        var controlsStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Toggle switch
        var toggleBorder = new Border
        {
            Width = 40,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Tag = zone,
            Margin = new Thickness(0, 0, 3, 0)
        };
        // Determine initial state based on EnableRestoreButton
        bool isZoneActive;
        if (zone.EnableRestoreButton)
        {
            isZoneActive = _zoneManager.IsZoneShown(zone.Id) && !_zoneManager.IsZoneMinimized(zone.Id);
        }
        else
        {
            isZoneActive = _zoneManager.IsZoneShown(zone.Id);
        }
        var toggleDot = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = isZoneActive
                ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44)),
            HorizontalAlignment = isZoneActive ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        toggleBorder.Child = toggleDot;
        toggleBorder.MouseLeftButtonDown += StateToggle_Click;
        _zoneToggleDots[zone.Id] = toggleDot;
        controlsStack.Children.Add(toggleBorder);

        // Hide button
        var hideBtn = new Button
        {
            Width = 28,
            Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 10,
            Content = "\u2715",
            Tag = zone,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = cn ? "隐藏" : "Hide"
        };
        hideBtn.Click += StateFullHide_Click;
        controlsStack.Children.Add(hideBtn);

        // Merge button
        var mergeBtn = new Button
        {
            Width = 28,
            Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0x8B, 0xEF)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 11,
            Content = "\uD83D\uDD17",
            Tag = zone,
            Margin = new Thickness(0, 0, 3, 0),
            ToolTip = cn ? "合并" : "Merge"
        };
        mergeBtn.Click += ZoneMerge_Click;
        controlsStack.Children.Add(mergeBtn);

        // Edit button
        var editBtn = new Button
        {
            Content = "\uD83C\uDFA8",
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            Cursor = Cursors.Hand,
            FontSize = 14,
            Margin = new Thickness(4, 0, 0, 0),
            Tag = zone
        };
        editBtn.Click += ZoneEdit_Click;
        controlsStack.Children.Add(editBtn);

        // Delete button
        var deleteBtn = new Button
        {
            Content = "X",
            Width = 28,
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)),
            Cursor = Cursors.Hand,
            FontSize = 12,
            Tag = zone
        };
        deleteBtn.Click += ZoneDelete_Click;
        controlsStack.Children.Add(deleteBtn);

        Grid.SetColumn(controlsStack, 1);
        grid.Children.Add(controlsStack);

        card.Child = grid;
        ZonesStack.Children.Add(card);
    }

    void ExpandBtn_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Zone masterZone)
        {
            bool isExpanded = _expandedZones.GetValueOrDefault(masterZone.Id, false);
            _expandedZones[masterZone.Id] = !isExpanded;
            RefreshZonesList();
        }
    }

    void SwitchToSubZone_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is (Zone masterZone, Zone subZone))
        {
            _zoneManager.ShowZone(subZone);
        }
    }

    void MergedGroupSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is Zone masterZone)
        {
            ShowMergedGroupSettingsDialog(masterZone);
        }
    }

    void ShowMergedGroupSettingsDialog(Zone masterZone)
    {
        var dialog = new MergedGroupSettingsDialog(masterZone, _zoneManager)
        {
            Owner = this
        };
        dialog.ShowDialog();
        // Dialog handles apply (save+close) and cancel (restore+close) internally
        RefreshAll();
    }

    TextBlock CreateLabel(string text)
    {
        return new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 4)
        };
    }

    StackPanel CreateNumberInput(string label, string defaultValue)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 100
        };
        var textBox = new TextBox
        {
            Text = defaultValue,
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            FontSize = 12,
            Width = 80
        };
        panel.Children.Add(labelText);
        panel.Children.Add(textBox);
        return panel;
    }

    WrapPanel CreateColorPresets(string[] colors, string selectedColor)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

        foreach (var color in colors)
        {
            var border = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Tag = color,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(color == selectedColor ? 3 : 1)
            };

            try
            {
                border.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            }
            catch { }

            border.MouseLeftButtonDown += (_, _) =>
            {
                // Update selection
                foreach (var child in panel.Children)
                {
                    if (child is Border b && b.Tag is string)
                        b.BorderThickness = new Thickness(1);
                }
                border.BorderThickness = new Thickness(3);
            };

            panel.Children.Add(border);
        }

        return panel;
    }

    string GetSelectedColor(WrapPanel presets)
    {
        foreach (var child in presets.Children)
        {
            if (child is Border b && b.Tag is string color && b.BorderThickness.Left == 3)
                return color;
        }
        return "#08000000";
    }

    void RefreshPanelCard()
    {
        try
        {
            if (PanelSection != null)
            {
                var config = _configService?.Load();
                PanelSection.Visibility = Visibility.Visible;
                if (PanelToggleDot != null)
                {
                    bool panelOpen = _panelService?.IsOpen == true;
                    AnimateToggleDot(PanelToggleDot, panelOpen);
                }
                if (PanelHotkeyText != null)
                {
                    if (config?.PanelHotkeyEnabled == true)
                        PanelHotkeyText.Text = GetHotkeyLabel(config.PanelHotkeyModifiers, config.PanelHotkeyKey);
                    else
                    {
                        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
                        PanelHotkeyText.Text = cn ? "未设置" : "None";
                    }
                }
            }
        }
        catch { }
    }

    // ── Notes list (with hotkey + appearance) ──

    void RefreshNotesList()
    {
        try
        {
            if (NotesSection != null) NotesSection.Visibility = (_notesService?.Notes.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (NotesStack != null) NotesStack.Children.Clear();
            _noteToggleDots.Clear();
            if (_notesService == null) return;
            foreach (var note in _notesService.Notes)
            {
                var card = CreateNoteCard(note);
                if (NotesStack != null) NotesStack.Children.Add(card);
            }
        }
        catch { }
    }

    Border CreateNoteCard(StickyNote note)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left side: icon + name + hotkey
        var left = new StackPanel();
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };

        // Icon (matching zone card style: 26x26 rounded)
        var iconBorder = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = new TextBlock
        {
            Text = "📝", FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameRow.Children.Add(iconBorder);
        nameRow.Children.Add(new TextBlock
        {
            Text = note.Title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(nameRow);

        // Hotkey row
        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(34, 4, 0, 0) };
        string hotkeyText = note.HotkeyEnabled
            ? GetHotkeyLabel(note.HotkeyModifiers, note.HotkeyKey)
            : _loc["Note.HotkeyDisabled"];
        var hkLabel = new TextBlock
        {
            Text = "[" + hotkeyText + "]",
            FontSize = 10,
            Foreground = note.HotkeyEnabled
                ? new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED))
                : new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x80)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var hkBtn = new Button
        {
            Content = _loc["Manage.HotkeySet"],
            FontSize = 9, Width = 36, Height = 20,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(6, 0, 0, 0),
            Tag = note
        };
        hkBtn.Click += NoteHotkeySet_Click;
        hotkeyRow.Children.Add(hkLabel);
        hotkeyRow.Children.Add(hkBtn);
        left.Children.Add(hotkeyRow);

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right-side buttons (matching zone card style)
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Appearance button (🎨 paintbrush)
        var appBtn = new Button
        {
            Content = "🎨", Width = 36, Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, FontSize = 9,
            Tag = note
        };
        appBtn.Click += NoteAppearance_Click;
        btns.Children.Add(appBtn);

        // Toggle switch (matching zone/clock/calendar style)
        bool noteVisible = GetNoteWindow(note.Id) is StickyNoteWindow snw && snw.MainContent.Visibility == Visibility.Visible;
        var noteToggleBorder = new Border
        {
            Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand, Margin = new Thickness(3, 0, 0, 0)
        };
        var noteToggleDot = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = noteVisible ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = noteVisible
                ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44))
        };
        noteToggleBorder.Child = noteToggleDot;
        _noteToggleDots[note.Id] = noteToggleDot;
        noteToggleBorder.MouseLeftButtonDown += (_, _) =>
        {
            ToggleNoteWindow(note);
            // Check actual window state after toggle and animate only this dot
            bool nowVisible = GetNoteWindow(note.Id) is StickyNoteWindow snw2 && snw2.MainContent.Visibility == Visibility.Visible;
            AnimateToggleDot(noteToggleDot, nowVisible);
        };
        btns.Children.Add(noteToggleBorder);

        // Delete button (X — matching zone card)
        var delBtn = new Button
        {
            Content = "X", Width = 28, Height = 28,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)),
            Cursor = Cursors.Hand, FontSize = 12,
            Tag = note
        };
        delBtn.Click += (_, _) => DeleteNote(note);
        btns.Children.Add(delBtn);
        Grid.SetColumn(btns, 1);
        grid.Children.Add(btns);

        card.Child = grid;
        return card;
    }

    // ── Per-widget appearance dialog ──

    void NoteAppearance_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not StickyNote note) return;
        ShowWidgetAppearanceDialog(note);
    }

    void ClockAppearance_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not DesktopClock clock) return;
        ShowClockAppearanceDialog(clock);
    }

    void CalendarAppearance_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not DesktopCalendar cal) return;
        ShowCalendarAppearanceDialog(cal);
    }

    void ShowWidgetAppearanceDialog(StickyNote note)
    {
        var dlg = new WidgetSettingsDialog(WidgetSettingsTarget.StickyNote) { Owner = this };
        dlg.LoadFromNote(note);
        if (dlg.ShowDialog() == true && dlg.DialogResultOk)
        {
            note.Width = dlg.ParsedWidth;
            note.Height = dlg.ParsedHeight;
            note.BorderThickness = dlg.ParsedBorderThickness;
            note.BorderColor = dlg.ParsedBorderColor;
            note.FillColor = dlg.ParsedFillColor;
            note.UseGlobalAppearance = dlg.ParsedUseGlobalAppearance;
            note.GlassBlurAmount = dlg.ParsedGlassBlur;
            note.GlassTintOpacity = dlg.ParsedGlassTintOpacity;
            note.GlassTintLuminosity = dlg.ParsedGlassLuminosity;
            note.GlassColorMode = dlg.ParsedGlassColorMode;
            note.EnableLiquidGlass = dlg.ParsedLiquidGlass;
            note.TitleBarFillColor = dlg.ParsedTitleBarFill;
            note.TitleBarOpacity = dlg.ParsedTitleBarOpacity;
            note.ControlOpacity = dlg.ParsedButtonOpacity;
            note.TitleTextColor = dlg.ParsedTitleTextColor;
            note.BackgroundImagePath = dlg.ParsedBgImagePath;
            note.BgImageOffsetX = dlg.ParsedBgOffsetX;
            note.BgImageOffsetY = dlg.ParsedBgOffsetY;
            note.BgImageZoom = dlg.ParsedBgZoom;
            note.BackgroundImageOpacity = dlg.ParsedBgOpacity;
            note.EnableRestoreButton = dlg.EnableRestoreButton;
            _notesService?.UpdateNote(note);
            RefreshNotesList();
            RefreshAllStateButtons();
        }
    }

    void ShowClockAppearanceDialog(DesktopClock clock)
    {
        var dlg = new WidgetSettingsDialog(WidgetSettingsTarget.Clock) { Owner = this };
        dlg.LoadFromClock(clock);
        if (dlg.ShowDialog() == true && dlg.DialogResultOk)
        {
            clock.BorderThickness = dlg.ParsedBorderThickness;
            clock.BorderColor = dlg.ParsedBorderColor;
            clock.FillColor = dlg.ParsedFillColor;
            clock.UseGlobalAppearance = dlg.ParsedUseGlobalAppearance;
            clock.GlassBlurAmount = dlg.ParsedGlassBlur;
            clock.GlassTintOpacity = dlg.ParsedGlassTintOpacity;
            clock.GlassTintLuminosity = dlg.ParsedGlassLuminosity;
            clock.GlassColorMode = dlg.ParsedGlassColorMode;
            clock.EnableLiquidGlass = dlg.ParsedLiquidGlass;
            clock.BackgroundImagePath = dlg.ParsedBgImagePath;
            clock.BgImageOffsetX = dlg.ParsedBgOffsetX;
            clock.BgImageOffsetY = dlg.ParsedBgOffsetY;
            clock.BgImageZoom = dlg.ParsedBgZoom;
            clock.BackgroundImageOpacity = dlg.ParsedBgOpacity;
            clock.DigitalBackgroundImagePath = dlg.ParsedDigitalBgImagePath;
            clock.DigitalBgImageOffsetX = dlg.ParsedDigitalBgOffsetX;
            clock.DigitalBgImageOffsetY = dlg.ParsedDigitalBgOffsetY;
            clock.DigitalBgImageZoom = dlg.ParsedDigitalBgZoom;
            clock.DigitalBackgroundImageOpacity = dlg.ParsedDigitalBgOpacity;
            clock.EnableRestoreButton = dlg.EnableRestoreButton;
            _widgetService?.UpdateClock(clock);
            RefreshClocksList();
            RefreshAllStateButtons();
        }
    }

    void ShowCalendarAppearanceDialog(DesktopCalendar cal)
    {
        var dlg = new WidgetSettingsDialog(WidgetSettingsTarget.Calendar) { Owner = this };
        dlg.LoadFromCalendar(cal);
        if (dlg.ShowDialog() == true && dlg.DialogResultOk)
        {
            cal.BorderThickness = dlg.ParsedBorderThickness;
            cal.BorderColor = dlg.ParsedBorderColor;
            cal.FillColor = dlg.ParsedFillColor;
            cal.UseGlobalAppearance = dlg.ParsedUseGlobalAppearance;
            cal.GlassBlurAmount = dlg.ParsedGlassBlur;
            cal.GlassTintOpacity = dlg.ParsedGlassTintOpacity;
            cal.GlassTintLuminosity = dlg.ParsedGlassLuminosity;
            cal.GlassColorMode = dlg.ParsedGlassColorMode;
            cal.EnableLiquidGlass = dlg.ParsedLiquidGlass;
            cal.BackgroundImagePath = dlg.ParsedBgImagePath;
            cal.BgImageOffsetX = dlg.ParsedBgOffsetX;
            cal.BgImageOffsetY = dlg.ParsedBgOffsetY;
            cal.BgImageZoom = dlg.ParsedBgZoom;
            cal.BackgroundImageOpacity = dlg.ParsedBgOpacity;
            cal.EnableRestoreButton = dlg.EnableRestoreButton;
            _widgetService?.UpdateCalendar(cal);
            RefreshCalendarsList();
            RefreshAllStateButtons();
        }
    }



    // ── Hotkey helpers ──

    static string HotkeyModToString(int mods)
    {
        var parts = new List<string>();
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0008) != 0) parts.Add("Win");
        return parts.Count > 0 ? string.Join("+", parts) : "";
    }

    static string KeyCodeToString(int key) => key switch
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

    static string GetHotkeyLabel(int mods, int key)
    {
        string modStr = HotkeyModToString(mods);
        string keyStr = KeyCodeToString(key);
        return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr}+{keyStr}";
    }

    void NoteHotkeySet_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not StickyNote note) return;
        try
        {
            var cn = _loc.CurrentLanguage == Services.Language.Chinese;
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

            // Add preset hotkeys
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
                    Background = isCurrent
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0x3A, 0xED))
                        : Brushes.Transparent
                };
                item.Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Foreground = isCurrent
                        ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
                        : new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0))
                };
                item.MouseLeftButtonDown += (_, _) =>
                {
                    note.HotkeyEnabled = captured.Enabled;
                    note.HotkeyModifiers = captured.Modifiers;
                    note.HotkeyKey = captured.Key;
                    _notesService?.UpdateNote(note);
                    if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
                    RefreshNotesList();
                    popup.IsOpen = false;
                };
                item.MouseEnter += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0x63, 0xFF)); };
                item.MouseLeave += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = Brushes.Transparent; };
                stack.Children.Add(item);
            }

            // Add custom hotkeys
            if (note.CustomHotkeys != null)
            {
                foreach (var customHotkey in note.CustomHotkeys)
                {
                    var captured = customHotkey;
                    string label = GetHotkeyLabel(captured.Modifiers, captured.Key);
                    bool isCurrent = note.HotkeyEnabled == true
                        && note.HotkeyModifiers == captured.Modifiers
                        && note.HotkeyKey == captured.Key;

                    // Container for hotkey item + delete button
                    var itemGrid = new Grid();
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var item = new Border
                    {
                        Padding = new Thickness(8, 4, 8, 4),
                        CornerRadius = new CornerRadius(3),
                        Cursor = Cursors.Hand,
                        Background = isCurrent
                            ? new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0x3A, 0xED))
                            : Brushes.Transparent
                    };
                    item.Child = new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        Foreground = isCurrent
                            ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
                            : new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0))
                    };
                    item.MouseLeftButtonDown += (_, _) =>
                    {
                        note.HotkeyEnabled = true;
                        note.HotkeyModifiers = captured.Modifiers;
                        note.HotkeyKey = captured.Key;
                        _notesService?.UpdateNote(note);
                        if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
                        RefreshNotesList();
                        popup.IsOpen = false;
                    };
                    item.MouseEnter += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0x63, 0xFF)); };
                    item.MouseLeave += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = Brushes.Transparent; };
                    Grid.SetColumn(item, 0);
                    itemGrid.Children.Add(item);

                    // Delete button (always visible)
                    var deleteBtn = new Border
                    {
                        Width = 20,
                        Height = 20,
                        CornerRadius = new CornerRadius(3),
                        Background = Brushes.Transparent,
                        Cursor = Cursors.Hand,
                        Visibility = Visibility.Visible,
                        Margin = new Thickness(2, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    deleteBtn.Child = new TextBlock
                    {
                        Text = "✕",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    deleteBtn.MouseLeftButtonDown += (_, _) =>
                    {
                        // If this hotkey is currently bound, unbind it
                        if (isCurrent)
                        {
                            note.HotkeyEnabled = false;
                        }
                        note.CustomHotkeys.Remove(captured);
                        _notesService?.UpdateNote(note);
                        if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
                        RefreshNotesList();
                        popup.IsOpen = false;
                    };
                    deleteBtn.MouseEnter += (s3, _) => { if (s3 is Border b3) b3.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0x00, 0x00)); };
                    deleteBtn.MouseLeave += (s3, _) => { if (s3 is Border b3) b3.Background = Brushes.Transparent; };
                    Grid.SetColumn(deleteBtn, 1);
                    itemGrid.Children.Add(deleteBtn);

                    stack.Children.Add(itemGrid);
                }
            }

            // Add separator
            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 4, 4, 4)
            };
            stack.Children.Add(separator);

            // Add "New" option
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
                ShowNoteHotkeyRecorderDialog(note);
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

    private void ShowNoteHotkeyRecorderDialog(StickyNote note)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

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

        // Title bar
        var titleBar = new Border
        {
            Height = 30,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Margin = new Thickness(0, 0, 0, 12)
        };
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };
        var titleText = new TextBlock
        {
            Text = cn ? "录制快捷键" : "Record Hotkey",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBar.Child = titleText;
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        // Instruction
        var instruction = new TextBlock
        {
            Text = cn ? "请按下快捷键组合..." : "Press hotkey combination...",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instruction, 1);
        grid.Children.Add(instruction);

        // Hotkey display
        var hotkeyDisplay = new TextBox
        {
            Text = "",
            IsReadOnly = true,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hotkeyDisplay, 2);
        grid.Children.Add(hotkeyDisplay);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = cn ? "取消" : "Cancel",
            Width = 60,
            Height = 28,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.Click += (_, _) => dlg.Close();
        buttonPanel.Children.Add(cancelButton);

        var saveButton = new Button
        {
            Content = cn ? "保存" : "Save",
            Width = 60,
            Height = 28,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x3A, 0xED)),
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            IsEnabled = false
        };
        buttonPanel.Children.Add(saveButton);

        Grid.SetRow(buttonPanel, 3);
        grid.Children.Add(buttonPanel);

        mainBorder.Child = grid;
        dlg.Content = mainBorder;

        // Hotkey recording logic
        int recordedModifiers = 0;
        int recordedKey = 0;
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
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                recordedModifiers |= 0x0002; // MOD_CONTROL
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                recordedModifiers |= 0x0001; // MOD_ALT
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                recordedModifiers |= 0x0004; // MOD_SHIFT
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
                recordedModifiers |= 0x0008; // MOD_WIN

            recordedKey = KeyInterop.VirtualKeyFromKey(key);

            hotkeyDisplay.Text = GetHotkeyLabel(recordedModifiers, recordedKey);
            saveButton.IsEnabled = true;
            saveButton.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
            saveButton.Foreground = Brushes.White;
            isRecording = false;
        };

        saveButton.Click += (_, _) =>
        {
            if (note.CustomHotkeys == null)
                note.CustomHotkeys = new List<CustomHotkey>();

            note.CustomHotkeys.Add(new CustomHotkey
            {
                Modifiers = recordedModifiers,
                Key = recordedKey
            });

            note.HotkeyEnabled = true;
            note.HotkeyModifiers = recordedModifiers;
            note.HotkeyKey = recordedKey;
            _notesService?.UpdateNote(note);
            if (System.Windows.Application.Current is App app) app.RefreshNoteHotkeys();
            RefreshNotesList();
            dlg.Close();
        };

        dlg.ShowDialog();
    }

    void ToggleNoteWindow(StickyNote note)
    {
        var app = (App)System.Windows.Application.Current;
        app.ToggleNoteWindow(note);
    }

    void DeleteNote(StickyNote note)
    {
        var app = (App)System.Windows.Application.Current;
        if (app.IsNoteWindowOpen(note.Id))
        {
            // Close via App's dictionary
            if (app._noteWindows.TryGetValue(note.Id, out var w)) w.Close();
        }
        _notesService?.DeleteNote(note.Id);
        RefreshNotesList();
    }

    // ── Clocks list ──

    void RefreshClocksList()
    {
        try
        {
            if (ClocksSection != null) ClocksSection.Visibility = (_widgetService?.Clocks.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (ClocksStack != null) ClocksStack.Children.Clear();
            _clockToggleDots.Clear();
            if (_widgetService == null) return;
            foreach (var clock in _widgetService.Clocks)
            {
                string mode = clock.Mode == ClockDisplayMode.Digital
                    ? (_loc.CurrentLanguage == Services.Language.Chinese ? "数字" : "Digital")
                    : (_loc.CurrentLanguage == Services.Language.Chinese ? "钟表" : "Analog");
                var card = CreateWidgetCard("🕐", $"Clock ({mode})", "#20FF9800", "#FFB74D",
                    () => ToggleClockWindow(clock), () => DeleteClock(clock),
                    clock);
                if (ClocksStack != null) ClocksStack.Children.Add(card);
            }
        }
        catch { }
    }

    void ToggleClockWindow(DesktopClock clock)
    {
        if (_openClockWindows.TryGetValue(clock.Id, out var w) && w is ClockWidget cw)
        {
            // Check MainContent visibility instead of IsVisible to handle minimized state correctly
            if (cw.MainContent.Visibility == Visibility.Visible) cw.HideClock();
            else cw.ShowClock();
        }
        else
        {
            // Set IsVisible BEFORE creating the window to prevent constructor from calling ApplyHidden()
            clock.IsVisible = true;
            OpenClockWindow(clock);
        }
    }

    void DeleteClock(DesktopClock clock)
    {
        if (_openClockWindows.TryGetValue(clock.Id, out var w)) w.Close();
        _widgetService?.DeleteClock(clock.Id);
        RefreshClocksList();
    }

    // ── Calendars list ──

    void RefreshCalendarsList()
    {
        try
        {
            if (CalendarsSection != null) CalendarsSection.Visibility = (_widgetService?.Calendars.Count ?? 0) > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (CalendarsStack != null) CalendarsStack.Children.Clear();
            _calendarToggleDots.Clear();
            if (_widgetService == null) return;
            foreach (var cal in _widgetService.Calendars)
            {
                var card = CreateWidgetCard("📅", $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}", "#202196F3", "#64B5F6",
                    () => ToggleCalendarWindow(cal), () => DeleteCalendar(cal),
                    cal);
                if (CalendarsStack != null) CalendarsStack.Children.Add(card);
            }
        }
        catch { }
    }

    void ToggleCalendarWindow(DesktopCalendar cal)
    {
        if (_openCalendarWindows.TryGetValue(cal.Id, out var w) && w is CalendarWidget cw)
        {
            // Check MainContent visibility instead of IsVisible to handle minimized state correctly
            if (cw.MainContent.Visibility == Visibility.Visible) cw.HideCalendar();
            else cw.ShowCalendar();
        }
        else
        {
            // Set IsVisible BEFORE creating the window to prevent constructor from calling ApplyHidden()
            cal.IsVisible = true;
            OpenCalendarWindow(cal);
        }
    }

    void DeleteCalendar(DesktopCalendar cal)
    {
        if (_openCalendarWindows.TryGetValue(cal.Id, out var w)) w.Close();
        _widgetService?.DeleteCalendar(cal.Id);
        RefreshCalendarsList();
    }

    // ── Reusable widget card builder ──

    Border CreateWidgetCard<T>(string icon, string name,
        string iconBgHex, string iconFgHex,
        Action onShowHide, Action onDelete,
        T widget) where T : class
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left: icon + name
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        Color iconBg;
        try { iconBg = (Color)ColorConverter.ConvertFromString(iconBgHex); } catch { iconBg = Color.FromRgb(0x20, 0x20, 0x20); }
        var iconBorder = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(0x12, iconBg.R, iconBg.G, iconBg.B)),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        iconBorder.Child = new TextBlock
        {
            Text = icon, FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        left.Children.Add(iconBorder);
        left.Children.Add(new TextBlock
        {
            Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // Right buttons (matching zone card style)
        var btns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        // Appearance button (🎨 paintbrush)
        var appBtn = new Button
        {
            Content = "🎨", Width = 36, Height = 26,
            Background = InactiveBg,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand, FontSize = 9,
            Tag = widget
        };
        appBtn.Click += (s, _) =>
        {
            if (s is Button b && b.Tag is DesktopClock c) ClockAppearance_Click(b, new RoutedEventArgs());
            else if (s is Button b2 && b2.Tag is DesktopCalendar ca) CalendarAppearance_Click(b2, new RoutedEventArgs());
        };
        btns.Children.Add(appBtn);

        // Toggle switch (matching zone card style: left=hide, right=show)
        var toggleBorder = new Border
        {
            Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            Cursor = Cursors.Hand,
            Margin = new Thickness(3, 0, 0, 0),
            Tag = widget
        };

        // Check actual window visibility via dictionaries
        bool widgetVisible = widget switch
        {
            DesktopClock c => _openClockWindows.TryGetValue(c.Id, out var cw) && cw.IsVisible,
            DesktopCalendar ca => _openCalendarWindows.TryGetValue(ca.Id, out var caw) && caw.IsVisible,
            StickyNote n => GetNoteWindow(n.Id) is StickyNoteWindow snw3 && snw3.MainContent.Visibility == Visibility.Visible,
            _ => false
        };

        // Determine if widget is active (considering EnableRestoreButton)
        bool isWidgetActive;
        bool enableRestoreButton = widget switch
        {
            DesktopClock c => c.EnableRestoreButton,
            DesktopCalendar ca => ca.EnableRestoreButton,
            StickyNote n => n.EnableRestoreButton,
            _ => true
        };
        if (enableRestoreButton)
        {
            // Enabled minimize mode: check if window is visible (not minimized)
            bool isMinimized = widgetVisible && widget switch
            {
                DesktopClock c2 => _openClockWindows.TryGetValue(c2.Id, out var w2) && w2 is ClockWidget cw2 && cw2.RestoreButton.Visibility == Visibility.Visible,
                DesktopCalendar ca2 => _openCalendarWindows.TryGetValue(ca2.Id, out var w3) && w3 is CalendarWidget cw3 && cw3.RestoreButton.Visibility == Visibility.Visible,
                StickyNote n2 => GetNoteWindow(n2.Id) is StickyNoteWindow sw2 && sw2.RestoreButton.Visibility == Visibility.Visible,
                _ => false
            };
            isWidgetActive = widgetVisible && !isMinimized;
        }
        else
        {
            // Disabled minimize mode: check if window is visible
            isWidgetActive = widgetVisible;
        }

        var toggleDot = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            HorizontalAlignment = isWidgetActive ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = isWidgetActive
                ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
                : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44))
        };
        toggleBorder.Child = toggleDot;
        if (widget is DesktopClock c3) _clockToggleDots[c3.Id] = toggleDot;
        else if (widget is DesktopCalendar ca3) _calendarToggleDots[ca3.Id] = toggleDot;
        else if (widget is StickyNote n3) _noteToggleDots[n3.Id] = toggleDot;
        toggleBorder.MouseLeftButtonDown += (_, _) =>
        {
            onShowHide();
            // Check actual window state after toggle and animate only this dot
            bool nowVisible = widget switch
            {
                DesktopClock c2 => _openClockWindows.TryGetValue(c2.Id, out var cw2) && cw2 is ClockWidget cw3 && cw3.MainContent.Visibility == Visibility.Visible,
                DesktopCalendar ca2 => _openCalendarWindows.TryGetValue(ca2.Id, out var caw2) && caw2 is CalendarWidget cw4 && cw4.MainContent.Visibility == Visibility.Visible,
                StickyNote n2 => GetNoteWindow(n2.Id) is StickyNoteWindow sw3 && sw3.MainContent.Visibility == Visibility.Visible,
                _ => false
            };
            AnimateToggleDot(toggleDot, nowVisible);
        };
        btns.Children.Add(toggleBorder);

        // Delete button (X — matching zone card)
        var delBtn = new Button
        {
            Content = "X", Width = 28, Height = 28,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x55, 0x55)),
            Cursor = Cursors.Hand, FontSize = 12
        };
        delBtn.Click += (_, _) => onDelete();
        btns.Children.Add(delBtn);
        Grid.SetColumn(btns, 1);
        grid.Children.Add(btns);

        card.Child = grid;
        return card;
    }

    /// <summary>
    /// Animate toggle dot sliding left/right with color change.
    /// </summary>
    private static void AnimateToggleDot(Border dot, bool toRight)
    {
        var targetAlign = toRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var targetColor = toRight
            ? Color.FromRgb(0x50, 0xFA, 0x7B)
            : Color.FromRgb(0xFF, 0x44, 0x44);

        // Use actual current margin as from (not HorizontalAlignment, which may be stale)
        var fromMargin = dot.Margin;
        var toMargin = toRight
            ? new Thickness(22, 0, 0, 0) : new Thickness(2, 0, 0, 0);

        var slideAnim = new System.Windows.Media.Animation.ThicknessAnimation(
            fromMargin, toMargin, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };

        var colorAnim = new System.Windows.Media.Animation.ColorAnimation(
            targetColor, TimeSpan.FromMilliseconds(150));

        dot.BeginAnimation(MarginProperty, slideAnim);
        dot.Background.BeginAnimation(SolidColorBrush.ColorProperty, colorAnim);
        // Defer HorizontalAlignment change to after animation completes to avoid jump
        slideAnim.Completed += (_, _) => dot.HorizontalAlignment = targetAlign;
    }

    // ── 3-state sync ──

    void RefreshAllStateButtons()
    {
        try
        {
            // Update toggle dots for all zones
            foreach (var zone in _zoneManager.Zones)
            {
                if (_zoneToggleDots.TryGetValue(zone.Id, out var toggleDot))
                {
                    bool isActive;
                    if (zone.EnableRestoreButton)
                    {
                        // Enabled minimize mode: check if window is visible (not minimized)
                        isActive = _zoneManager.IsZoneShown(zone.Id) && !_zoneManager.IsZoneMinimized(zone.Id);
                    }
                    else
                    {
                        // Disabled minimize mode: check if window is shown
                        isActive = _zoneManager.IsZoneShown(zone.Id);
                    }
                    AnimateToggleDot(toggleDot, isActive);
                }
            }

            // Update toggle dots for all clocks
            if (_widgetService != null)
            {
                foreach (var clock in _widgetService.Clocks)
                {
                    if (_clockToggleDots.TryGetValue(clock.Id, out var toggleDot))
                    {
                        bool isActive;
                        if (clock.EnableRestoreButton)
                        {
                            // Enabled minimize mode: check if window is visible (not minimized)
                            bool isWindowOpen = _openClockWindows.TryGetValue(clock.Id, out var win) && win.IsVisible;
                            bool isMinimized = isWindowOpen && win is ClockWidget cw && cw.RestoreButton.Visibility == Visibility.Visible;
                            isActive = isWindowOpen && !isMinimized;
                        }
                        else
                        {
                            // Disabled minimize mode: check if window is visible
                            isActive = _openClockWindows.TryGetValue(clock.Id, out var win) && win.IsVisible;
                        }
                        AnimateToggleDot(toggleDot, isActive);
                    }
                }
            }

            // Update toggle dots for all calendars
            if (_widgetService != null)
            {
                foreach (var calendar in _widgetService.Calendars)
                {
                    if (_calendarToggleDots.TryGetValue(calendar.Id, out var toggleDot))
                    {
                        bool isActive;
                        if (calendar.EnableRestoreButton)
                        {
                            // Enabled minimize mode: check if window is visible (not minimized)
                            bool isWindowOpen = _openCalendarWindows.TryGetValue(calendar.Id, out var win) && win.IsVisible;
                            bool isMinimized = isWindowOpen && win is CalendarWidget cw && cw.RestoreButton.Visibility == Visibility.Visible;
                            isActive = isWindowOpen && !isMinimized;
                        }
                        else
                        {
                            // Disabled minimize mode: check if window is visible
                            isActive = _openCalendarWindows.TryGetValue(calendar.Id, out var win) && win.IsVisible;
                        }
                        AnimateToggleDot(toggleDot, isActive);
                    }
                }
            }

            // Update toggle dots for all notes
            if (_notesService != null)
            {
                foreach (var note in _notesService.Notes)
                {
                    if (_noteToggleDots.TryGetValue(note.Id, out var toggleDot))
                    {
                        bool isActive;
                        if (note.EnableRestoreButton)
                        {
                            // Enabled minimize mode: check if window is visible (not minimized)
                            var noteWin = GetNoteWindow(note.Id);
                            bool isWindowOpen = noteWin != null && noteWin.IsVisible;
                            bool isMinimized = isWindowOpen && noteWin is StickyNoteWindow sw && sw.RestoreButton.Visibility == Visibility.Visible;
                            isActive = isWindowOpen && !isMinimized;
                        }
                        else
                        {
                            // Disabled minimize mode: check if window is visible
                            var noteWin = GetNoteWindow(note.Id);
                            isActive = noteWin != null && noteWin.IsVisible;
                        }
                        AnimateToggleDot(toggleDot, isActive);
                    }
                }
            }
        }
        catch { }
    }

    void ApplyCardColors(ContentPresenter cp, Zone? zone)
    {
        if (zone == null) return;
        if (VisualTreeHelper.GetChildrenCount(cp) < 1) return;
        var border = VisualTreeHelper.GetChild(cp, 0) as Border;
        if (border == null || VisualTreeHelper.GetChildrenCount(border) < 1) return;
        var grid = VisualTreeHelper.GetChild(border, 0) as Grid;
        if (grid == null || grid.Children.Count < 2) return;
        var leftPanel = grid.Children[0] as StackPanel;
        if (leftPanel == null || leftPanel.Children.Count < 1) return;
        if (leftPanel.Children[0] is not StackPanel nameRow || nameRow.Children.Count < 2) return;
        var iconBorder = nameRow.Children[0] as Border;
        var nameTb = nameRow.Children[1] as TextBlock;
        if (nameTb != null && !string.IsNullOrEmpty(zone.TitleTextColor))
        { try { nameTb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(zone.TitleTextColor)); } catch { } }
        if (iconBorder != null && iconBorder.Child is TextBlock iconTb && !string.IsNullOrEmpty(zone.IconColor))
        { try { iconTb.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(zone.IconColor)); } catch { } }

        // Merge status indicator in name row
        bool isMerged = zone.MergedGroupId.HasValue;
        // Check if there's already a merge label (index 2+ in nameRow)
        TextBlock? mergeLabel = null;
        for (int i = 2; i < nameRow.Children.Count; i++)
        {
            if (nameRow.Children[i] is TextBlock tb && (tb.Tag as string) == "mergeLabel")
            { mergeLabel = tb; break; }
        }
        if (isMerged)
        {
            var cn = _loc.CurrentLanguage == Services.Language.Chinese;
            if (mergeLabel == null)
            {
                mergeLabel = new TextBlock
                {
                    Tag = "mergeLabel",
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    FontStyle = FontStyles.Italic
                };
                nameRow.Children.Add(mergeLabel);
            }
            mergeLabel.Text = "[" + (cn ? "已合并" : "merged") + "]";
            try { mergeLabel.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#887CAFEF")); } catch { }
        }
        else if (mergeLabel != null)
        {
            nameRow.Children.Remove(mergeLabel);
        }
    }

    void UpdateStateButtons(ContentPresenter cp, Zone? zone)
    {
        if (zone == null) return;
        bool isVisible = zone.IsVisible;
        bool isMinimized = _zoneManager.IsZoneMinimized(zone.Id);

        // Find the toggle switch (first child is a Border with CornerRadius=10)
        if (VisualTreeHelper.GetChildrenCount(cp) >= 1)
        {
            var border = VisualTreeHelper.GetChild(cp, 0) as Border;
            if (border != null && VisualTreeHelper.GetChildrenCount(border) >= 1)
            {
                var grid = VisualTreeHelper.GetChild(border, 0) as Grid;
                if (grid != null && grid.Children.Count >= 2)
                {
                    var btnStack = grid.Children[1] as StackPanel;
                    if (btnStack != null && btnStack.Children.Count >= 1)
                    {
                        // First child is the toggle Border
                        if (btnStack.Children[0] is Border toggleBorder)
                        {
                            // Find the dot inside the toggle
                            if (VisualTreeHelper.GetChildrenCount(toggleBorder) >= 1)
                            {
                                var dot = VisualTreeHelper.GetChild(toggleBorder, 0) as Border;
                                if (dot != null)
                                {
                                    // Register for direct animation
                                    _zoneToggleDots[zone.Id] = dot;
                                    // Dot position: left=minimize (red), right=show (green)
                                    dot.HorizontalAlignment = isVisible && !isMinimized
                                        ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                                    dot.Background = isVisible && !isMinimized
                                        ? new SolidColorBrush(Color.FromRgb(0x50, 0xFA, 0x7B))
                                        : new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
                                }
                            }
                        }
                        // Third child is the full-hide button (index 2 after toggle + merge button)
                        if (btnStack.Children.Count >= 3 && btnStack.Children[2] is Button fullBtn)
                        {
                            fullBtn.Background = !isVisible ? ActiveBg : InactiveBg;
                        }
                    }
                }
            }
        }
    }

    static Button? FindButton(ContentPresenter cp, int index)
    {
        if (VisualTreeHelper.GetChildrenCount(cp) < 1) return null;
        var border = VisualTreeHelper.GetChild(cp, 0) as Border;
        if (border == null || VisualTreeHelper.GetChildrenCount(border) < 1) return null;
        var grid = VisualTreeHelper.GetChild(border, 0) as Grid;
        if (grid == null || grid.Children.Count < 2) return null;
        var btnStack = grid.Children[1] as StackPanel;
        if (btnStack == null || btnStack.Children.Count <= index) return null;
        return btnStack.Children[index] as Button;
    }

    // ── State button clicks ──

    void StateShow_Click(object s, RoutedEventArgs e)
    { if (s is Button b && b.Tag is Zone z) { _zoneManager.ShowZone(z); RefreshAllStateButtons(); } }
    void StateMin_Click(object s, RoutedEventArgs e)
    { if (s is Button b && b.Tag is Zone z) { _zoneManager.HideZone(z.Id); RefreshAllStateButtons(); } }
    void StateFullHide_Click(object s, RoutedEventArgs e)
    { if (s is Button b && b.Tag is Zone z) { _zoneManager.FullHideZone(z.Id); RefreshAllStateButtons(); } }
    void StateToggle_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border toggle && toggle.Tag is Zone z)
        {
            if (z.IsVisible) _zoneManager.HideZone(z.Id);
            else _zoneManager.ShowZone(z);
            // Animation handled by RefreshAllStateButtons via ZonesChanged event
        }
    }

    // ── Title bar ──

    void TitleBar_MouseLeftButtonDown(object s, MouseButtonEventArgs e) { if (e.ClickCount == 1) { try { DragMove(); } catch { } } }
    void MinimizeButton_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void CloseButton_Click(object s, RoutedEventArgs e) => Hide();

    /// <summary>Wrap a dialog window with a custom dark title bar (replaces ToolWindow white title bar).
    /// Exactly mirrors the liquid glass dialog pattern: dlgBg Border → rootGrid (titleBar + separator + content).</summary>
    static void WrapDialogWithDarkTitleBar(Window dlg, Border contentBorder, string title)
    {
        dlg.WindowStyle = WindowStyle.None;
        dlg.AllowsTransparency = true;
        dlg.Background = Brushes.Transparent;

        // Outer shell: dark background + rounded corners + border
        var dlgBg = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // separator
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content

        // Title bar
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

        // Separator
        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(12, 0, 12, 0)
        };

        // Strip original contentBorder's CornerRadius and Padding (handled by dlgBg)
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
    void LangToggle_Click(object s, RoutedEventArgs e)
    {
        _loc.ToggleLanguage();
        var config = _configService.Load();
        config.Language = _loc.CurrentLanguage == Services.Language.Chinese ? "zh" : "en";
        _configService.Save(config);
    }
    void NewZone_Click(object s, RoutedEventArgs e) => _viewModel.CreateZoneCommand.Execute(null);

    // ── Global buttons ──

    void BtnShowAll_Click(object s, RoutedEventArgs e) { _zoneManager.ShowAll(); ShowAllWidgets(); }
    void BtnHideAll_Click(object s, RoutedEventArgs e) { _zoneManager.HideAll(); HideAllWidgets(); }
    void BtnFullHideAll_Click(object s, RoutedEventArgs e) { _zoneManager.FullHideAll(); FullHideAllWidgets(); }

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
            // Notes managed by App — use ShowManagementWindow to show/hide
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

    void ZoneEdit_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is Zone zone)
        { var dlg = new ZoneSettingsDialog(zone, _zoneManager) { Owner = this }; dlg.ShowDialog(); }
    }

    void ZoneMerge_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is Zone zone)
            ShowMergeDialog(zone);
    }

    void ZoneDisband_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is Zone zone && zone.MergedGroupId.HasValue)
        {
            ShowMergedGroupContextMenu(zone, btn);
        }
    }

    void ShowMergedGroupContextMenu(Zone masterZone, Button placementBtn)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

        // Use custom Popup to avoid WPF ContextMenu + AllowsTransparency bug
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

        // Helper to create a menu item (Border + TextBlock, no Button template issues)
        UIElement MakeItem(string text, Action onClick)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = fgBrush,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            var itemBorder = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(2, 1, 2, 1),
                Cursor = Cursors.Hand,
                Child = tb
            };
            itemBorder.MouseEnter += (_, _) => itemBorder.Background = hoverBrush;
            itemBorder.MouseLeave += (_, _) => itemBorder.Background = Brushes.Transparent;
            itemBorder.MouseLeftButtonDown += (_, _) => { popup.IsOpen = false; onClick(); };
            return itemBorder;
        }

        // 1. Disband single zone
        if (masterZone.MergedSubZoneIds.Count > 0)
            stack.Children.Add(MakeItem(cn ? "分离单个分区" : "Disband Single Zone", () => DisbandSingleZone(masterZone)));

        // 2. Disband entire group
        stack.Children.Add(MakeItem(cn ? "解散组合分区" : "Disband Entire Group", () => DisbandEntireGroup(masterZone)));

        // Separator
        stack.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(6, 4, 6, 4) });

        // 3. Add zone to group
        stack.Children.Add(MakeItem(cn ? "添加分区到组合" : "Add Zone to Group", () => ShowMergeDialog(masterZone)));

        // 4. Merge with another group
        if (_zoneManager.Zones.Any(z => z.MergedSubZoneIds.Count > 0 && z.Id != masterZone.Id))
            stack.Children.Add(MakeItem(cn ? "与其他组合合并" : "Merge with Another Group", () => MergeWithAnotherGroup(masterZone)));

        menuBorder.Child = stack;
        popup.Child = menuBorder;
        popup.PlacementTarget = placementBtn;
        popup.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;

        // Close on click outside
        previewMouseDownHandler = (_, _) =>
        {
            if (popup.IsOpen) popup.IsOpen = false;
        };
        this.AddHandler(UIElement.PreviewMouseDownEvent, previewMouseDownHandler);
        popup.Closed += (_, _) => this.RemoveHandler(UIElement.PreviewMouseDownEvent, previewMouseDownHandler);

        popup.IsOpen = true;
    }

    private MouseButtonEventHandler? previewMouseDownHandler;

    void DisbandSingleZone(Zone masterZone)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

        // Show dialog to select which zone to disband
        var dialogTitle = cn ? "选择要分离的分区" : "Select Zone to Disband";
        var dialog = new Window
        {
            Title = dialogTitle,
            Width = 300,
            Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };

        var bgBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new Grid()
        };

        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = cn ? "选择要从组合中分离的分区：" : "Select zone to remove from group:",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
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
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };

        foreach (var subId in masterZone.MergedSubZoneIds)
        {
            var subZone = _zoneManager.Zones.FirstOrDefault(z => z.Id == subId);
            if (subZone != null)
            {
                var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
                if (!string.IsNullOrEmpty(subZone.IconChar))
                {
                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = subZone.IconChar,
                        FontSize = 14,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0)
                    });
                }
                itemPanel.Children.Add(new TextBlock
                {
                    Text = subZone.Name,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
                });
                listBox.Items.Add(new ListBoxItem
                {
                    Content = itemPanel,
                    Tag = subZone,
                    Padding = new Thickness(6, 4, 6, 4)
                });
            }
        }

        if (listBox.Items.Count > 0)
            listBox.SelectedIndex = 0;

        Grid.SetRow(listBox, 1);
        grid.Children.Add(listBox);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelBtn = new Button
        {
            Content = cn ? "取消" : "Cancel",
            Width = 70,
            Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        var disbandBtn = new Button
        {
            Content = cn ? "分离" : "Disband",
            Width = 80,
            Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Cursor = Cursors.Hand
        };
        disbandBtn.Click += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Tag is Zone selectedZone)
            {
                _zoneManager.RemoveFromMergedGroup(selectedZone.Id);
                dialog.Close();
                RefreshAll();
            }
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(disbandBtn);
        Grid.SetRow(btnRow, 2);
        grid.Children.Add(btnRow);

        WrapDialogWithDarkTitleBar(dialog, bgBorder, dialogTitle);
        dialog.ShowDialog();
    }

    void DisbandEntireGroup(Zone masterZone)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        var result = MessageBox.Show(
            cn ? $"确定要解散组合分区「{masterZone.MergedGroupName}」吗？\n所有分区将恢复为独立窗口。"
               : $"Disband merged group \"{masterZone.MergedGroupName}\"?\nAll zones will return to individual windows.",
            cn ? "解散组合" : "Disband Group",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _zoneManager.DisbandMergedGroup(masterZone.MergedGroupId.Value);
            RefreshAll();
        }
    }

    void MergeWithAnotherGroup(Zone sourceMaster)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

        // Find other merged groups
        var otherGroups = _zoneManager.Zones
            .Where(z => z.MergedSubZoneIds.Count > 0 && z.Id != sourceMaster.Id)
            .ToList();

        if (otherGroups.Count == 0)
        {
            MessageBox.Show(
                cn ? "没有其他组合分区可合并。" : "No other merged groups to merge with.",
                cn ? "合并" : "Merge",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var mergeTargetTitle = cn ? "选择要合并的目标组合" : "Select Target Group to Merge";
        var dialog = new Window
        {
            Title = mergeTargetTitle,
            Width = 360,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };

        var bgBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new Grid()
        };

        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new TextBlock
        {
            Text = cn ? "选择要合并的目标组合：" : "Select target group to merge with:",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
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
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };

        foreach (var targetGroup in otherGroups)
        {
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            if (!string.IsNullOrEmpty(targetGroup.MergedGroupIcon))
            {
                itemPanel.Children.Add(new TextBlock
                {
                    Text = targetGroup.MergedGroupIcon,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }
            itemPanel.Children.Add(new TextBlock
            {
                Text = targetGroup.MergedGroupName,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
            });
            listBox.Items.Add(new ListBoxItem
            {
                Content = itemPanel,
                Tag = targetGroup,
                Padding = new Thickness(6, 4, 6, 4)
            });
        }

        if (listBox.Items.Count > 0)
            listBox.SelectedIndex = 0;

        Grid.SetRow(listBox, 1);
        grid.Children.Add(listBox);

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelBtn = new Button
        {
            Content = cn ? "取消" : "Cancel",
            Width = 70,
            Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        var mergeBtn = new Button
        {
            Content = cn ? "合并" : "Merge",
            Width = 80,
            Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 11,
            Cursor = Cursors.Hand
        };
        mergeBtn.Click += (_, _) =>
        {
            if (listBox.SelectedItem is ListBoxItem item && item.Tag is Zone targetGroup)
            {
                // Merge all zones from source group into target group
                foreach (var subId in sourceMaster.MergedSubZoneIds.ToList())
                {
                    _zoneManager.RemoveFromMergedGroup(subId);
                    _zoneManager.MergeZones(targetGroup.Id, subId);
                }
                // Also merge the source master itself
                _zoneManager.RemoveFromMergedGroup(sourceMaster.Id);
                _zoneManager.MergeZones(targetGroup.Id, sourceMaster.Id);

                dialog.Close();
                RefreshAll();
            }
        };

        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(mergeBtn);
        Grid.SetRow(btnRow, 2);
        grid.Children.Add(btnRow);

        WrapDialogWithDarkTitleBar(dialog, bgBorder, mergeTargetTitle);
        dialog.ShowDialog();
    }

    void ShowMergeDialog(Zone sourceZone)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        // Determine which zones are eligible to merge
        var eligibleZones = _zoneManager.Zones
            .Where(z => z.Id != sourceZone.Id
                && (sourceZone.MergedGroupId == null
                    ? z.MergedGroupId == null || z.MergedGroupId != sourceZone.MergedGroupId
                    : z.MergedGroupId == null))
            .ToList();

        // If source is a merged master, exclude sub-zones that are already in this group
        if (sourceZone.MergedSubZoneIds.Count > 0)
        {
            eligibleZones = eligibleZones
                .Where(z => !sourceZone.MergedSubZoneIds.Contains(z.Id))
                .ToList();
        }

        if (eligibleZones.Count == 0)
        {
            MessageBox.Show(_loc["Merge.NoTargets"], _loc["Merge.Title"],
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Window
        {
            Title = _loc["Merge.Title"],
            Width = 360, Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };

        var bgBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x11, 0x1A)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = new Grid()
        };

        var grid = (Grid)bgBorder.Child;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new TextBlock
        {
            Text = cn ? "选择要合并的分区（可多选）：" : "Select zones to merge (multi-select):",
            FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // Select all checkbox
        var selectAllPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var selectAllCheckBox = new CheckBox
        {
            Content = cn ? "全选" : "Select All",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            FontSize = 12,
            IsChecked = false
        };
        selectAllPanel.Children.Add(selectAllCheckBox);
        Grid.SetRow(selectAllPanel, 1);
        grid.Children.Add(selectAllPanel);

        // Zone list with checkboxes
        var checkBoxes = new List<CheckBox>();
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 200
        };
        var zonesPanel = new StackPanel();

        foreach (var z in eligibleZones)
        {
            var itemPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
            if (!string.IsNullOrEmpty(z.IconChar))
            {
                itemPanel.Children.Add(new TextBlock
                {
                    Text = z.IconChar,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                });
            }
            itemPanel.Children.Add(new TextBlock
            {
                Text = z.Name,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
            });

            var checkBox = new CheckBox
            {
                Content = itemPanel,
                Tag = z,
                Margin = new Thickness(0, 2, 0, 2),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                FontSize = 12
            };
            checkBoxes.Add(checkBox);
            zonesPanel.Children.Add(checkBox);
        }

        scrollViewer.Content = zonesPanel;
        Grid.SetRow(scrollViewer, 2);
        grid.Children.Add(scrollViewer);

        // Select all logic
        selectAllCheckBox.Checked += (_, _) =>
        {
            foreach (var cb in checkBoxes) cb.IsChecked = true;
        };
        selectAllCheckBox.Unchecked += (_, _) =>
        {
            foreach (var cb in checkBoxes) cb.IsChecked = false;
        };

        // Buttons
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 0, 0)
        };
        var cancelBtn = new Button
        {
            Content = _loc["Rename.Cancel"],
            Width = 70, Height = 28,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            BorderThickness = new Thickness(0),
            FontSize = 11, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => dlg.Close();
        var mergeBtn = new Button
        {
            Content = _loc["Merge.MergeBtn"],
            Width = 80, Height = 28,
            Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            FontSize = 11, Cursor = Cursors.Hand
        };
        mergeBtn.Click += (_, _) =>
        {
            var selectedZones = checkBoxes
                .Where(cb => cb.IsChecked == true)
                .Select(cb => cb.Tag as Zone)
                .Where(z => z != null)
                .ToList();

            if (selectedZones.Count == 0)
            {
                MessageBox.Show(
                    cn ? "请至少选择一个分区" : "Please select at least one zone",
                    cn ? "提示" : "Info",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Merge all selected zones into source zone
            foreach (var targetZone in selectedZones)
            {
                _zoneManager.MergeZones(sourceZone.Id, targetZone.Id);
            }
            dlg.Close();
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(mergeBtn);
        Grid.SetRow(btnRow, 3);
        grid.Children.Add(btnRow);

        WrapDialogWithDarkTitleBar(dlg, bgBorder, _loc["Merge.Title"]);
        dlg.ShowDialog();
        RefreshAll();
        RefreshAllStateButtons();
    }

    void ZoneDelete_Click(object s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is Zone zone)
        {
            if (MessageBox.Show(_loc.Get("Dialog.DeleteZoneMsg", zone.Name),
                _loc["Dialog.DeleteZoneTitle"], MessageBoxButton.YesNo,
                MessageBoxImage.Warning) == MessageBoxResult.Yes)
                _zoneManager.DeleteZone(zone.Id);
        }
    }

    // ── Widget buttons ──

    // ── Panel ──

    void PanelSettingsBtn_Click(object s, MouseButtonEventArgs e)
    {
        try
        {
            var config = _zoneManager.GetConfig();
            var dlg = new WidgetSettingsDialog(WidgetSettingsTarget.Panel) { Owner = this };
            dlg.LoadFromConfig(config);
            if (dlg.ShowDialog() == true && dlg.DialogResultOk)
            {
                config.PanelWidth = dlg.ParsedWidth;
                config.PanelHeight = dlg.ParsedHeight;
                config.GlobalBorderThickness = dlg.ParsedBorderThickness;
                config.GlobalBorderColor = dlg.ParsedBorderColor;
                config.PanelFillColor = dlg.ParsedFillColor;
                config.PanelUseGlobalAppearance = dlg.ParsedUseGlobalAppearance;
                config.GlassBlurAmount = dlg.ParsedGlassBlur;
                config.GlassTintOpacity = dlg.ParsedGlassTintOpacity;
                config.GlassTintLuminosity = dlg.ParsedGlassLuminosity;
                config.GlassColorMode = dlg.ParsedGlassColorMode;
                config.EnableLiquidGlass = dlg.ParsedLiquidGlass;
                config.PanelTitleBarFillColor = dlg.ParsedTitleBarFill;
                config.PanelControlOpacity = dlg.ParsedButtonOpacity;

                // Panel background image
                config.PanelBackgroundImagePath = dlg.ParsedBgImagePath;
                config.PanelBgImageOffsetX = dlg.ParsedBgOffsetX;
                config.PanelBgImageOffsetY = dlg.ParsedBgOffsetY;
                config.PanelBgImageZoom = dlg.ParsedBgZoom;
                config.PanelBackgroundImageOpacity = dlg.ParsedBgOpacity;

                _configService.Save(config);

                // Refresh panel window if open
                _panelService?.RefreshAppearance();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.ToString(), "Panel Settings Error");
        }
    }

    void PanelToggle_Click(object s, MouseButtonEventArgs e)
    {
        TogglePanel();
    }

    public void TogglePanel()
    {
        try
        {
            if (_panelService == null) return;
            var config = _configService.Load();
            if (_panelService.IsOpen)
            {
                _panelService.CloseAndClear();
            }
            else
            {
                _panelService.Show(config);
            }
            RefreshPanelCard();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.ToString(), "PanelToggle Error");
        }
    }

    void PanelHotkeySet_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn) return;
        try
        {
            var cn = _loc.CurrentLanguage == Services.Language.Chinese;
            var config = _configService.Load();

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

            // Add preset hotkeys
            foreach (var preset in PanelHotkeyPresets)
            {
                var captured = preset;
                string label = captured.Enabled
                    ? GetHotkeyLabel(captured.Modifiers, captured.Key)
                    : (cn ? "无" : "None");
                bool isCurrent = config.PanelHotkeyEnabled == captured.Enabled
                    && config.PanelHotkeyModifiers == captured.Modifiers
                    && config.PanelHotkeyKey == captured.Key;

                var item = new Border
                {
                    Padding = new Thickness(8, 4, 8, 4),
                    CornerRadius = new CornerRadius(3),
                    Cursor = Cursors.Hand,
                    Background = isCurrent
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0x3A, 0xED))
                        : Brushes.Transparent
                };
                item.Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    Foreground = isCurrent
                        ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
                        : new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0))
                };
                item.MouseLeftButtonDown += (_, _) =>
                {
                    config.PanelHotkeyEnabled = captured.Enabled;
                    config.PanelHotkeyModifiers = captured.Modifiers;
                    config.PanelHotkeyKey = captured.Key;
                    _configService.Save(config);

                    // Update hotkey registration
                    if (Application.Current is App app)
                    {
                        if (config.PanelHotkeyEnabled)
                            app.RegisterPanelHotkey(config.PanelHotkeyModifiers, config.PanelHotkeyKey);
                        else
                            app.UnregisterPanelHotkey();
                    }
                    RefreshPanelCard();
                    popup.IsOpen = false;
                };
                item.MouseEnter += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0x63, 0xFF)); };
                item.MouseLeave += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = Brushes.Transparent; };
                stack.Children.Add(item);
            }

            // Add custom hotkeys
            if (config.PanelCustomHotkeys != null)
            {
                foreach (var customHotkey in config.PanelCustomHotkeys)
                {
                    var captured = customHotkey;
                    string label = GetHotkeyLabel(captured.Modifiers, captured.Key);
                    bool isCurrent = config.PanelHotkeyEnabled == true
                        && config.PanelHotkeyModifiers == captured.Modifiers
                        && config.PanelHotkeyKey == captured.Key;

                    // Container for hotkey item + delete button
                    var itemGrid = new Grid();
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    itemGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var item = new Border
                    {
                        Padding = new Thickness(8, 4, 8, 4),
                        CornerRadius = new CornerRadius(3),
                        Cursor = Cursors.Hand,
                        Background = isCurrent
                            ? new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0x3A, 0xED))
                            : Brushes.Transparent
                    };
                    item.Child = new TextBlock
                    {
                        Text = label,
                        FontSize = 11,
                        Foreground = isCurrent
                            ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
                            : new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0))
                    };
                    item.MouseLeftButtonDown += (_, _) =>
                    {
                        config.PanelHotkeyEnabled = true;
                        config.PanelHotkeyModifiers = captured.Modifiers;
                        config.PanelHotkeyKey = captured.Key;
                        _configService.Save(config);

                        // Update hotkey registration
                        if (Application.Current is App app)
                        {
                            app.RegisterPanelHotkey(config.PanelHotkeyModifiers, config.PanelHotkeyKey);
                        }
                        RefreshPanelCard();
                        popup.IsOpen = false;
                    };
                    item.MouseEnter += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = new SolidColorBrush(Color.FromArgb(0x30, 0x6C, 0x63, 0xFF)); };
                    item.MouseLeave += (s3, _) => { if (s3 is Border b3 && !isCurrent) b3.Background = Brushes.Transparent; };
                    Grid.SetColumn(item, 0);
                    itemGrid.Children.Add(item);

                    // Delete button (always visible)
                    var deleteBtn = new Border
                    {
                        Width = 20,
                        Height = 20,
                        CornerRadius = new CornerRadius(3),
                        Background = Brushes.Transparent,
                        Cursor = Cursors.Hand,
                        Visibility = Visibility.Visible,
                        Margin = new Thickness(2, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    deleteBtn.Child = new TextBlock
                    {
                        Text = "✕",
                        FontSize = 10,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    deleteBtn.MouseLeftButtonDown += (_, _) =>
                    {
                        // If this hotkey is currently bound, unbind it
                        if (isCurrent)
                        {
                            config.PanelHotkeyEnabled = false;
                            if (Application.Current is App app)
                            {
                                app.UnregisterPanelHotkey();
                            }
                        }
                        config.PanelCustomHotkeys.Remove(captured);
                        _configService.Save(config);
                        RefreshPanelCard();
                        popup.IsOpen = false;
                    };
                    deleteBtn.MouseEnter += (s3, _) => { if (s3 is Border b3) b3.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0x00, 0x00)); };
                    deleteBtn.MouseLeave += (s3, _) => { if (s3 is Border b3) b3.Background = Brushes.Transparent; };
                    Grid.SetColumn(deleteBtn, 1);
                    itemGrid.Children.Add(deleteBtn);

                    stack.Children.Add(itemGrid);
                }
            }

            // Add separator
            var separator = new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(4, 4, 4, 4)
            };
            stack.Children.Add(separator);

            // Add "New" option
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
                ShowHotkeyRecorderDialog(config);
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

    private void ShowHotkeyRecorderDialog(AppConfig config)
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;

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

        // Title bar
        var titleBar = new Border
        {
            Height = 30,
            Background = Brushes.Transparent,
            Cursor = Cursors.SizeAll,
            Margin = new Thickness(0, 0, 0, 12)
        };
        titleBar.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };
        var titleText = new TextBlock
        {
            Text = cn ? "录制快捷键" : "Record Hotkey",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            VerticalAlignment = VerticalAlignment.Center
        };
        titleBar.Child = titleText;
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        // Instruction
        var instruction = new TextBlock
        {
            Text = cn ? "请按下快捷键组合..." : "Press hotkey combination...",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instruction, 1);
        grid.Children.Add(instruction);

        // Hotkey display
        var hotkeyDisplay = new TextBox
        {
            Text = "",
            IsReadOnly = true,
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
            Background = new SolidColorBrush(Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(hotkeyDisplay, 2);
        grid.Children.Add(hotkeyDisplay);

        // Buttons
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var cancelButton = new Button
        {
            Content = cn ? "取消" : "Cancel",
            Width = 60,
            Height = 28,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelButton.Click += (_, _) => dlg.Close();
        buttonPanel.Children.Add(cancelButton);

        var saveButton = new Button
        {
            Content = cn ? "保存" : "Save",
            Width = 60,
            Height = 28,
            FontSize = 11,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x7C, 0x3A, 0xED)),
            Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            IsEnabled = false
        };
        buttonPanel.Children.Add(saveButton);

        Grid.SetRow(buttonPanel, 3);
        grid.Children.Add(buttonPanel);

        mainBorder.Child = grid;
        dlg.Content = mainBorder;

        // Hotkey recording logic
        int recordedModifiers = 0;
        int recordedKey = 0;
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
            if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
                recordedModifiers |= 0x0002; // MOD_CONTROL
            if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
                recordedModifiers |= 0x0001; // MOD_ALT
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                recordedModifiers |= 0x0004; // MOD_SHIFT
            if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
                recordedModifiers |= 0x0008; // MOD_WIN

            recordedKey = KeyInterop.VirtualKeyFromKey(key);

            hotkeyDisplay.Text = GetHotkeyLabel(recordedModifiers, recordedKey);
            saveButton.IsEnabled = true;
            saveButton.Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED));
            saveButton.Foreground = Brushes.White;
            isRecording = false;
        };

        saveButton.Click += (_, _) =>
        {
            if (config.PanelCustomHotkeys == null)
                config.PanelCustomHotkeys = new List<CustomHotkey>();

            config.PanelCustomHotkeys.Add(new CustomHotkey
            {
                Modifiers = recordedModifiers,
                Key = recordedKey
            });

            config.PanelHotkeyEnabled = true;
            config.PanelHotkeyModifiers = recordedModifiers;
            config.PanelHotkeyKey = recordedKey;
            _configService.Save(config);

            // Update hotkey registration
            if (Application.Current is App app)
            {
                app.RegisterPanelHotkey(config.PanelHotkeyModifiers, config.PanelHotkeyKey);
            }
            RefreshPanelCard();
            dlg.Close();
        };

        dlg.ShowDialog();
    }

    void NewPanel_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (_panelService == null) return;
            var config = _configService.Load();
            if (_panelService.IsOpen)
            {
                _panelService.CloseAndClear();
            }
            else
            {
                _panelService.Show(config);
            }
            RefreshPanelCard();
        }
        catch { }
    }

    void NewNote_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (_notesService == null) return;
            var wa = SystemParameters.WorkArea;
            var note = _notesService.CreateNote(
                wa.Left + (wa.Width - 260) / 2,
                wa.Top + (wa.Height - 200) / 3);
            OpenNoteWindow(note);
            // Delay to let window fully render, then sync toggle
            Dispatcher.BeginInvoke(new Action(() => RefreshNotesList()), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create note:\n{ex.Message}", "DeskOrder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void NewClock_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (_widgetService == null) return;
            var wa = SystemParameters.WorkArea;
            var clock = _widgetService.CreateClock(
                wa.Left + (wa.Width - 220) / 2 + 120,
                wa.Top + 60);
            OpenClockWindow(clock);
            Dispatcher.BeginInvoke(new Action(() => RefreshClocksList()), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create clock:\n{ex.Message}", "DeskOrder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void NewCalendar_Click(object s, RoutedEventArgs e)
    {
        try
        {
            if (_widgetService == null) return;
            var wa = SystemParameters.WorkArea;
            var cal = _widgetService.CreateCalendar(
                wa.Left + (wa.Width - 280) / 2 - 120,
                wa.Top + 40);
            OpenCalendarWindow(cal);
            Dispatcher.BeginInvoke(new Action(() => RefreshCalendarsList()), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to create calendar:\n{ex.Message}", "DeskOrder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void OpenNoteWindow(StickyNote note)
    {
        var app = (App)System.Windows.Application.Current;
        app.OpenNoteWindowFromManager(note);
        Dispatcher.BeginInvoke(new Action(() => RefreshNotesList()), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    void OpenClockWindow(DesktopClock clock)
    {
        if (_openClockWindows.ContainsKey(clock.Id)) return;
        var window = new ClockWidget(clock, _widgetService!);
        window.Closed += (_, _) =>
        {
            _openClockWindows.Remove(clock.Id);
            ((App)System.Windows.Application.Current)._clockWindows.Remove(clock.Id);
            Dispatcher.BeginInvoke(new Action(() => RefreshClocksList()), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        _openClockWindows[clock.Id] = window;
        ((App)System.Windows.Application.Current)._clockWindows[clock.Id] = window;
        window.Show();
        window.Activate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_clockToggleDots.TryGetValue(clock.Id, out var dot))
                AnimateToggleDot(dot, true);
            else
                RefreshClocksList();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    void OpenCalendarWindow(DesktopCalendar cal)
    {
        if (_openCalendarWindows.ContainsKey(cal.Id)) return;
        var window = new CalendarWidget(cal, _widgetService!);
        window.Closed += (_, _) =>
        {
            _openCalendarWindows.Remove(cal.Id);
            ((App)System.Windows.Application.Current)._calendarWindows.Remove(cal.Id);
            Dispatcher.BeginInvoke(new Action(() => RefreshCalendarsList()), System.Windows.Threading.DispatcherPriority.Loaded);
        };
        _openCalendarWindows[cal.Id] = window;
        ((App)System.Windows.Application.Current)._calendarWindows[cal.Id] = window;
        window.Show();
        window.Activate();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_calendarToggleDots.TryGetValue(cal.Id, out var dot))
                AnimateToggleDot(dot, true);
            else
                RefreshCalendarsList();
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// Called when ZoneManager reports a zone visibility change.
    /// Only animates when the state actually changed.
    /// </summary>
    private void OnZoneVisibilityChanged(Guid zoneId, bool isVisible)
    {
        if (_zoneToggleDots.TryGetValue(zoneId, out var dot))
        {
            double currentLeft = dot.Margin.Left;
            bool isCurrentlyRight = currentLeft > 10;
            if (isVisible != isCurrentlyRight)
                AnimateToggleDot(dot, isVisible);
        }
    }

    // ── Sync handlers ──

    void SyncNoteWindows()
    {
        // Note windows are managed by App.xaml.cs — no sync needed here
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

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; Hide(); }
}
