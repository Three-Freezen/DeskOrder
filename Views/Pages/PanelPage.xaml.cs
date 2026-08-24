using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>
/// Panel settings page. Spec §7.1 #9: Panel is a single instance (no list),
/// so the page renders one EditableListRow representing the AppConfig.PanelEnabled
/// toggle, with the active hotkey label in the subtitle.
/// ponytail: Panel has only one config entry; rendering it as a 1-row list keeps
/// the page visually consistent with Zones / MergedGroups / Clocks / Calendars / Notes.
/// </summary>
public partial class PanelPage : UserControl
{
    readonly ManagementWindow _main;
    readonly ConfigService _configService;
    readonly PanelService? _panelService;

    public PanelPage(ManagementWindow main, ConfigService configService, PanelService? panelService)
    {
        InitializeComponent();
        _main = main;
        _configService = configService;
        _panelService = panelService;
        // ponytail 2026-08-25: Persist is wired centrally in ManagementWindow
        // (WirePropertyPanelPersist) — one dispatcher for all target types,
        // docked and floating. Panel edits save the LIVE AppConfig and repaint
        // the live PanelWindow. Pages no longer overwrite it.
        Loaded += (_, _) => RefreshList();
    }

    public void ApplyLoc() => RefreshList();

    public void RefreshList()
    {
        // ponytail: row + editor must share the LIVE Panel instance (held by
        // ZoneManager's AppConfig) — a fresh Load() copy would desync the
        // property editor from what PanelService/PanelWindow actually read.
        var cfg = _main.LiveConfig;
        var hotkey = cfg.PanelHotkey.PanelHotkeyEnabled
            ? ManagementWindow.GetHotkeyLabel(cfg.PanelHotkey.PanelHotkeyModifiers, cfg.PanelHotkey.PanelHotkeyKey)
            : "未设置";
        CountLabel.Text = $"{hotkey} · {(cfg.Panel.PanelEnabled ? "已启用" : "未启用")}";
        ListHost.ItemsSource = new List<EditableListRow> { BuildRow(cfg) };
    }

    EditableListRow BuildRow(AppConfig cfg)
    {
        var row = new EditableListRow
        {
            Tag = cfg.Panel,
            Title = "控制面板",
            Subtitle = $"快捷键 {ManagementWindow.GetHotkeyLabel(cfg.PanelHotkey.PanelHotkeyModifiers, cfg.PanelHotkey.PanelHotkeyKey)}",
            IconKey = "Icon.Panel",
            IsLocked = false,
            IsVisible = cfg.Panel.PanelEnabled,
        };
        ApplyStatusBadge(row, cfg);
        row.LockCommand = new RelayCommand(_ => { /* panel lock not applicable */ });
        row.VisibilityCommand = new RelayCommand(_ =>
        {
            cfg.Panel.PanelEnabled = row.IsVisible;
            _configService.Save(cfg);
            if (cfg.Panel.PanelEnabled) _panelService?.Show(cfg); else _panelService?.CloseAndClear();
            RefreshList();
        });
        row.DeleteCommand = new RelayCommand(_ => { /* panel cannot be deleted */ });
        row.RenameCommand = new RelayCommand(_ => { /* panel has no name */ });
        // ponytail 2026-08-24: Panel row previously had no click handler — the
        // right-side PropertyPanel never opened for the singleton panel.
        // Mirror ClockPage/CalendarPage: route through DockTarget with the
        // live PanelConfig instance as the target so the existing PropertyPanel
        // dispatcher can build its field tree.
        row.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            Select();
        };
        return row;
    }

    void Select() =>
        PropertyWindowManager.Instance.DockTarget(_main.LiveConfig.Panel, _main);

    static void ApplyStatusBadge(EditableListRow row, AppConfig cfg)
    {
        row.HasStatusBadge = true;
        row.StatusBadge = cfg.Panel.PanelEnabled ? "已启用" : "未启用";
        row.StatusBadgeBrush = cfg.Panel.PanelEnabled
            ? new SolidColorBrush(Color.FromArgb(0x40, 0x4A, 0xC0, 0x4A))
            : new SolidColorBrush(Color.FromArgb(0x40, 0xA0, 0xA0, 0xC0));
    }

    void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _main.TogglePanel();
        RefreshList();
    }
}
