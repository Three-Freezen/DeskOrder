using System;
using System.Collections.Generic;
using System.Windows;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// Owns the floating property editors keyed by their target (one floating window
/// per target). Plus the bidirectional dedup between the docked panel (in the
/// right column of ManagementWindow) and the floating layer:
///   * <see cref="PopOutTarget"/> — called from "outside" (gear button on a zone
///     widget, etc.). If a floating already shows this target, focus it; if the
///     docked panel shows it, clear the docked target and open a floating at the
///     requester's position; otherwise open a fresh floating.
///   * <see cref="DockTarget"/> — called from "inside" the workspace (list row
///     click, drag floating title back to main window's right column). If a
///     floating already shows this target, close it; then set the docked panel
///     target and ensure the right column is visible.
/// Both paths converge on the rule "only one editor per target".
///
/// ponytail: positions are persisted per-target-Id in
/// <see cref="AppConfig.PropertyWindowRects"/>. When opening with no requester
/// the manager picks a 24-px cascade slot off any existing floating so two
/// new windows don't perfectly stack on top of each other. OpenOrFocus also
/// routes to <see cref="RestoreAndActivate"/> when the target already has a
/// floating — used by tab-strip clicks.
/// </summary>
public class PropertyWindowManager
{
    static PropertyWindowManager? _instance;
    public static PropertyWindowManager Instance => _instance ??= new PropertyWindowManager();

    readonly Dictionary<object, PropertyWindow> _floating = new();

    /// <summary>True when a floating editor currently owns this target.</summary>
    public bool HasFloating(object target) => _floating.ContainsKey(target);

    /// <summary>Get the floating window for a target, or null. Used by tab-strip click routing.</summary>
    public PropertyWindow? GetFloating(object target)
        => _floating.TryGetValue(target, out var w) ? w : null;

    /// <summary>Pop-out flow — caller is a window outside the workspace (zone
    /// gear button, panel settings button, etc.). Honours the "no duplicate
    /// editor per target" rule by reusing an existing floating or by detaching
    /// the docked panel's same target before opening a new floating.</summary>
    public void PopOutTarget(object target, ConfigService configService, ManagementWindow main, Window? requester = null)
    {
        if (target == null) return;

        // 1. Already floating — just activate.
        if (_floating.TryGetValue(target, out var existing))
        {
            RestoreAndActivate(existing);
            return;
        }

        // 2. Docked shows the same target — clear it so we don't end up with two
        //    editors for the same target.
        if (main.DockedPanel != null && ReferenceEquals(main.DockedPanel.Target, target))
            main.DockedPanel.Target = null;

        // ponytail: also remove the tab from the docked strip so it doesn't
        // linger as a stale entry when the floating window is open.
        var key = TargetKey(target);
        if (!string.IsNullOrEmpty(key) && main.DockedTabs != null)
            main.DockedTabs.CloseTab(key);

        // 3. Open fresh floating at requester (or persisted / main window) position.
        var pos = ResolvePopPosition(target, requester, main, configService);
        OpenFloating(target, configService, main, pos);
    }

    /// <summary>Dock flow — caller is a workspace row click or a floating-title
    /// drag-back. If a floating already shows this target, close it; then set
    /// the docked tab strip's active tab to this target and ensure the right
    /// column is visible. The strip → panel.Target sync lives in ManagementWindow.</summary>
    public void DockTarget(object target, ManagementWindow main)
    {
        if (target == null) return;

        // Close any floating for this target (dock back).
        if (_floating.TryGetValue(target, out var existing))
        {
            try { existing.Close(); } catch { }
        }

        if (main.DockedTabs == null) return;
        main.EnsurePropertyPanelVisible();
        // Open as preview tab; PinTab is called later when the user edits the target.
        main.DockedTabs.OpenOrFocus(TargetKey(target), TitleOf(target), IconOf(target));
    }

    /// <summary>Backward-compat alias — used by callers that don't care about the
    /// direction. Defaults to pop-out.</summary>
    public void OpenOrFocus(object target, ConfigService configService, ManagementWindow main, Window? requester = null)
        => PopOutTarget(target, configService, main, requester);

    public void CloseWindow(object target)
    {
        if (target != null && _floating.TryGetValue(target, out var w))
        {
            try { w.Close(); } catch { }
        }
    }

    void OpenFloating(object target, ConfigService configService, ManagementWindow main, (double x, double y) pos)
    {
        var w = new PropertyWindow(target, configService) { Owner = main, Left = pos.x, Top = pos.y };
        var config = configService.Load();
        w.Width = config.PropertyWindowWidth > 0 ? config.PropertyWindowWidth : 360;
        w.Height = config.PropertyWindowHeight > 0 ? config.PropertyWindowHeight : 600;
        w.LocationChanged += (_, _) =>
        {
                PersistRect(target, w, configService);
            };
        w.SizeChanged += (_, _) =>
        {
                PersistRect(target, w, configService);
            };
        w.Closed += (_, _) => _floating.Remove(target);
        // ponytail: flip the panel into "floating mode" — swap toggle icon to
        // dock-back, fire the spin animation. Subscribe DockRequested so the
        // same button that pops out can also dock back. DockTarget closes this
        // window (via _floating.Remove on Closed) and sets the docked target.
        w.Body.IsFloating = true;
        w.Body.DockRequested += (_, _) => DockTarget(target, main);
        // ponytail: drag-out dock-back. When the user drags the title bar
        // toward the main window's right column, dock instead of repositioning.
        w.DockBackRequested += (_, args) =>
        {
            if (TryHandleDockBack(target, main, args.CursorScreen))
                args.Handled = true;
        };
        _floating[target] = w;
        w.Show();
    }

    void PersistRect(object target, PropertyWindow w, ConfigService configService)
    {
        try
        {
            var config = configService.Load();
            var key = TargetKey(target);
            if (string.IsNullOrEmpty(key)) return;
            config.PropertyWindowRects[key] = new RectLite
            {
                X = w.Left,
                Y = w.Top,
                Width = w.Width,
                Height = w.Height,
            };
            // ponytail: also keep the legacy global fields in sync for any
            // older reader still using them as a fallback.
            config.PropertyWindowX = w.Left;
            config.PropertyWindowY = w.Top;
            config.PropertyWindowWidth = w.Width;
            config.PropertyWindowHeight = w.Height;
            configService.Save(config);
        }
        catch { }
    }

    public static void RestoreAndActivate(PropertyWindow w)
    {
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        w.Activate();
    }

    /// <summary>Check whether the cursor is over the main window's right
    /// column (where the docked tab strip + panel live) and, if so, dock the
    /// target. Returns true if the host should close the floating window.</summary>
    bool TryHandleDockBack(object target, ManagementWindow main, Point cursorScreen)
    {
        if (main == null || !main.IsVisible) return false;
        if (main.WindowState != WindowState.Normal && main.WindowState != WindowState.Maximized) return false;
        // Convert cursor to main window client coords.
        try
        {
            var pt = main.PointFromScreen(cursorScreen);
            var bounds = new Rect(0, 0, main.ActualWidth, main.ActualHeight);
            if (!bounds.Contains(pt)) return false;
            // Right column is the last 360 px (RightCol Width = 360 when visible).
            // Allow docking when the cursor lands in the rightmost 200 px so
            // the gesture is forgiving — user doesn't need pixel precision.
            if (pt.X < main.ActualWidth - 200) return false;
            // Dock it.
            DockTarget(target, main);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Resolve the pop-out position: target's per-Id rect first, then
    /// the requester offset, then a 24-px cascade off the most recently opened
    /// floating (avoid stacking), then a sensible offset from the main
    /// management window, then the work-area corner.</summary>
    static (double x, double y) ResolvePopPosition(object target, Window? requester, ManagementWindow main, ConfigService configService)
    {
        // 1. Per-Id persisted rect.
        try
        {
            var config = configService.Load();
            var key = TargetKey(target);
            if (!string.IsNullOrEmpty(key) && config.PropertyWindowRects.TryGetValue(key, out var rect) && rect.IsValid)
                return CascadeIfColliding(rect.X, rect.Y, main);
        }
        catch { }

        // 2. Requester offset (button that triggered the pop-out).
        if (requester != null && requester.IsVisible && !double.IsNaN(requester.Left) && !double.IsNaN(requester.Top))
            return CascadeIfColliding(requester.Left + 24, requester.Top + 24, main);

        // 3. Main window right-edge offset.
        if (main.IsVisible && !double.IsNaN(main.Left))
            return CascadeIfColliding(main.Left + main.ActualWidth + 12, main.Top + 80, main);

        // 4. Work-area corner.
        var wa = SystemParameters.WorkArea;
        return (wa.Left + wa.Width - 380, wa.Top + 80);
    }

    /// <summary>If the candidate position collides with any currently-open
    /// floating, push it down-right by 24-px until clear. Keeps multiple
    /// windows legible when they pile up.</summary>
    static (double x, double y) CascadeIfColliding(double x, double y, ManagementWindow main)
    {
        const double step = 24;
        for (int i = 0; i < 16; i++)
        {
            bool collides = false;
            foreach (var kv in Instance._floating)
            {
                var w = kv.Value;
                if (!w.IsVisible) continue;
                // ponytail: 24-px hit-zone on each side so a window barely
                // off-screen counts as overlapping for cascade purposes.
                if (Math.Abs(w.Left - x) < step && Math.Abs(w.Top - y) < step)
                {
                    collides = true;
                    break;
                }
            }
            if (!collides) return (x, y);
            x += step;
            y += step;
        }
        return (x, y);
    }

    /// <summary>Stable string key for a target so positions survive across
    /// runs. Falls back to type+identity hashcode for types without a stable Id
    /// property — still better than losing all state on each save.</summary>
    public static string TargetKey(object target)
    {
        if (target == null) return "";
        // Models expose `Id` — try reflection so we don't need a hard dep.
        var prop = target.GetType().GetProperty("Id");
        if (prop?.GetValue(target) is { } idVal && idVal != null)
            return target.GetType().Name + ":" + idVal;
        return target.GetType().Name + ":" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target);
    }

    static string TitleOf(object target)
    {
        var prop = target.GetType().GetProperty("Name");
        if (prop?.GetValue(target) is string s && !string.IsNullOrEmpty(s)) return s;
        return target.GetType().Name;
    }

    static string IconOf(object target) => target switch
    {
        Zone => "Icon.Zones",
        DesktopClock => "Icon.Clock",
        DesktopCalendar => "Icon.Calendar",
        StickyNote => "Icon.Sticky",
        _ => "Icon.Settings",
    };

    /// <summary>Move a tab from one strip to another. After move, if the source
    /// strip is in a floating PropertyWindow with no tabs left, close that window
    /// (with its existing close animation).</summary>
    public void TransferTab(PropertyTabStrip fromStrip, PropertyTabStrip toStrip, string key)
    {
        if (fromStrip == null || toStrip == null || string.IsNullOrEmpty(key)) return;

        // ponytail: capture title/icon from the source tab BEFORE removing it,
        // so we can re-create it on the target strip with the same look.
        PropertyTab? sourceTab = null;
        for (int i = 0; i < fromStrip.Tabs.Count; i++)
            if (fromStrip.Tabs[i].Key == key) { sourceTab = fromStrip.Tabs[i]; break; }

        fromStrip.CloseTab(key);

        if (sourceTab != null)
            toStrip.OpenOrFocus(key, sourceTab.Title, sourceTab.IconKey);
        else
            toStrip.OpenOrFocus(key, key, "Icon.Settings");

        CheckEmptyFloatingAndClose(fromStrip);
    }

    /// <summary>If the given strip belongs to a visible floating PropertyWindow
    /// whose tab list is now empty, close that window (existing close animation).</summary>
    public void CheckEmptyFloatingAndClose(PropertyTabStrip strip)
    {
        if (strip == null) return;
        if (strip.Tabs.Count > 0) return;
        if (Window.GetWindow(strip) is PropertyWindow pw && pw.IsVisible)
        {
            try { pw.Close(); } catch { /* close animation already running */ }
        }
    }
}