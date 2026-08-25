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

    /// <summary>Lazy-creates the panel window and shows it. No-op if already open.
    /// Persists <c>Panel.PanelEnabled = true</c> in config so the panel reopens on next launch.</summary>
    public void Show(AppConfig cfg)
    {
        if (_window != null)
        {
            if (!_window.IsVisible) _window.Show();
            return;
        }
        cfg.Panel.PanelEnabled = true;
        _configService.Save(cfg);
        _window = new PanelWindow(_zoneManager, _configService);
        _window.Closed += (_, _) =>
        {
            _window = null;
            WindowClosed?.Invoke();
        };
        _window.Show();
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
    }
}