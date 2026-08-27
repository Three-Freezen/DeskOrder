using System;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

/// <summary>
/// Owns the singleton PanelWindow. Replaces the previous scattered
/// _panelWindow fields in App.xaml.cs and ManagementWindow.xaml.cs.
/// Single source of truth for panel lifetime + show/hide/toggle/refresh.
///
/// WindowClosed fires whenever the user closes the panel via the X button
/// (or any other UI-driven close). Subscribers (typically ManagementWindow)
/// use it to refresh their "Panel is open" UI state.
/// </summary>
public class PanelService
{
    private readonly ZoneManager _zoneManager;
    private readonly ConfigService _configService;
    private PanelWindow? _window;

    public PanelService(ZoneManager zoneManager, ConfigService configService)
    {
        _zoneManager = zoneManager;
        _configService = configService;
    }

    public PanelWindow? Window => _window;

    public bool IsOpen => _window is { IsVisible: true };

    /// <summary>Fires when the panel window is closed by the user (X button).</summary>
    public event Action? WindowClosed;

    /// <summary>面板启用状态变化时触发(打开=true,关闭=false)。PropertyPanel 状态区
    /// 订阅它做实时同步 — 无论面板是经热键/托盘/面板自身"─"按钮还是状态栏开关开关。</summary>
    public event Action? PanelEnabledChanged;

    /// <summary>Lazy-creates the panel window and shows it. No-op if already open.
    /// Persists <c>Panel.PanelEnabled = true</c> in config so the panel reopens on next launch.</summary>
    public void Show(AppConfig cfg)
    {
        if (_window != null)
        {
            if (!_window.IsVisible) _window.Show();
            return;
        }

        // 状态写两份:传入 cfg 走落盘(与旧行为一致),live AppConfig 的 Panel 写
        // 一份给 PropertyPanel 的 Target(它就是 live PanelConfig 实例)实时读取。
        // 参考分区/便签等组件 — 状态写 live model,再发 Changed 事件通知刷新。
        cfg.Panel.PanelEnabled = true;
        _zoneManager.GetConfig().Panel.PanelEnabled = true;
        _configService.Save(cfg);

        _window = new PanelWindow(_zoneManager, _configService);
        _window.Closed += (_, _) =>
        {
            _window = null;
            // 关窗时 OnClosed/HidePanel 已把 PanelEnabled=false 落盘,这里只需同步
            // live 实例并广播,让状态区开关立即跟随(无需再次写盘)。
            _zoneManager.GetConfig().Panel.PanelEnabled = false;
            WindowClosed?.Invoke();
            PanelEnabledChanged?.Invoke();
        };
        _window.Show();
        PanelEnabledChanged?.Invoke();
    }

    /// <summary>
    /// Minimizes the panel through the SAME code path as the panel window's own
    /// top-right "─" button (PanelWindow.HidePanel: persist position, disable
    /// PanelEnabled, close). The Closed → WindowClosed chain releases the singleton,
    /// so a later Show() recreates the window.
    /// </summary>
    public void Hide()
    {
        // Route through the window's own minimize-button logic, never raw Window.Hide().
        _window?.HidePanel();
    }

    /// <summary>Closes the panel window (firing WindowClosed) and clears the singleton.
    /// Use when the user disables Panel globally; a subsequent Show will recreate.</summary>
    public void CloseAndClear()
    {
        if (_window == null) return;
        _window.Close(); // triggers Closed → WindowClosed → _window = null
    }

    public void Toggle(AppConfig cfg)
    {
        if (IsOpen) Hide();
        else Show(cfg);
    }

    /// <summary>Apply the live panel window's appearance from a preset config.</summary>
    public void RefreshAppearance()
    {
        if (_window == null) return;
        _window.ApplyAcrylic();
        _window.ApplyStyle();
        _window.ApplyBackgroundImage();
        // ponytail: cards carry the body content color (PanelTextColor) and are built in
        // RebuildDisplay — ApplyStyle only re-brushes the top bar, so without this the
        // 主体内容颜色 edit looks like a no-op until some other RebuildDisplay trigger.
        _window.RebuildDisplay();
    }
}