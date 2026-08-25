using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;

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
    public void PopOutTarget(object target, ConfigService configService, ManagementWindow main, Window? requester = null, Point? cursorScreen = null, Size? initialSize = null)
    {
        if (target == null) return;

        // 1. Already floating — just activate.
        if (_floating.TryGetValue(target, out var existing))
        {
            if (existing.IsVisible)
            {
                RestoreAndActivate(existing);
                return;
            }
            // ponytail 2026-08-26: 关闭动画中/不可见的残影窗口 — 激活它用户也看不到
            // (正淡出),直接丢弃走全新打开。Closed 回调按实例判等,旧窗口的 Closed
            // 不会误删新窗口的条目。
            _floating.Remove(target);
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
        var pos = ResolvePopPosition(target, requester, main, configService, cursorScreen);
        System.Diagnostics.Trace.WriteLine($"[SubFlyout] PopOutTarget: pos=({pos.x:F0},{pos.y:F0}) → OpenFloating");
        OpenFloating(target, configService, main, pos, initialSize);
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

    void OpenFloating(object target, ConfigService configService, ManagementWindow main, (double x, double y) pos, Size? initialSize = null)
    {
        var w = new PropertyWindow(target, configService) { Left = pos.x, Top = pos.y };
        // ponytail 2026-08-26: Owner 只能挂到"已显示过"的窗口上 — WPF 对从未 Show 的
        // 窗口 set_Owner 抛 InvalidOperationException("无法将 Owner 属性设置为之前未显示的
        // Window"),EnsureManagementWindow 创建的管理窗口可能从未显示。IsVisible 为 true 时
        // 才挂 Owner,并再包一层 try/catch 兜底:任何异常都不阻断设置面板打开(浮动窗口
        // 本身 Topmost,无 Owner 也能正常显示)。
        if (main.IsVisible)
        {
            try { w.Owner = main; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PropWin] Set Owner failed (non-fatal): {ex.Message}");
            }
        }
        // 把位置夹回工作区,避免历史持久化的屏幕外坐标(拔显示器等)让窗口"打不开"。
        var clamped = ClampToWorkArea(pos.x, pos.y);
        w.Left = clamped.x; w.Top = clamped.y;
        // ponytail: drag-out calls pass an explicit initialSize so the new
        // floating starts at a known width/height instead of inheriting the
        // persisted config size. Other callers leave it null and fall through
        // to the persisted-then-default chain below.
        if (initialSize.HasValue)
        {
            w.Width = initialSize.Value.Width;
            w.Height = initialSize.Value.Height;
        }
        else
        {
            var config = configService.Load();
            w.Width = config.PropertyWindowWidth > 0 ? config.PropertyWindowWidth : 360;
            w.Height = config.PropertyWindowHeight > 0 ? config.PropertyWindowHeight : 600;
        }
        w.LocationChanged += (_, _) => SchedulePersist(target, w, configService);
        w.SizeChanged += (_, _) => SchedulePersist(target, w, configService);
        w.Closed += (_, _) =>
        {
            FlushPendingPersist();
            // 按实例判等:只清理自己的条目,避免旧窗口(残影)的 Closed 误删新窗口。
            if (_floating.TryGetValue(target, out var cur) && ReferenceEquals(cur, w))
                _floating.Remove(target);
        };
        // ponytail: flip the panel into "floating mode" — swap toggle icon to
        // dock-back, fire the spin animation. Subscribe DockRequested so the
        // same button that pops out can also dock back. DockTarget closes this
        // window (via _floating.Remove on Closed) and sets the docked target.
        w.Body.IsFloating = true;
        // ponytail 2026-08-25: floating editors previously had no Persist —
        // edits vanished on close. Wire the central dispatcher so every field
        // change flows to the owning service (ZoneManager / WidgetService /
        // NotesService / panel live config).
        main.WirePropertyPanelPersist(w.Body);
        w.Body.DockRequested += (_, _) => DockTarget(target, main);
        // ponytail: drag-out dock-back. When the user drags the title bar
        // toward the main window's right column, dock instead of repositioning.
        w.DockBackRequested += (_, args) =>
        {
            if (TryHandleDockBack(target, main, args.CursorScreen))
                args.Handled = true;
        };
        _floating[target] = w;
        System.Diagnostics.Trace.WriteLine($"[SubFlyout] OpenFloating: 即将 Show() — Left={w.Left:F0} Top={w.Top:F0} {w.Width:F0}x{w.Height:F0} Topmost={w.Topmost} State={w.WindowState}");
        w.Show();
        // ponytail 2026-08-26: Show 后显式 Activate — 无 Owner 的浮动窗口也要抢到
        // 前台焦点,避免被分区窗口(桌面挂件)压住看不见。
        w.Activate();
        System.Diagnostics.Trace.WriteLine($"[SubFlyout] OpenFloating: Show 完成 — IsVisible={w.IsVisible} Left={w.Left:F0} Top={w.Top:F0} Opacity={w.Opacity}");
    }

    /// <summary>夹取窗口左上角到工作区,保证至少有一部分可见(兜底历史遗留的屏幕外坐标)。</summary>
    static (double x, double y) ClampToWorkArea(double x, double y)
    {
        var wa = SystemParameters.WorkArea;
        const double minVisible = 120;
        if (x + minVisible < wa.Left) x = wa.Left + 8;
        if (x > wa.Right - minVisible) x = Math.Max(wa.Left + 8, wa.Right - 400);
        if (y + minVisible < wa.Top) y = wa.Top + 8;
        if (y > wa.Bottom - 48) y = Math.Max(wa.Top + 8, wa.Bottom - 480);
        return (x, y);
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

    // ── Debounced rect persistence ──
    // ponytail 2026-08-26: LocationChanged / SizeChanged fire once per frame
    // while the floating window is dragged or resized. Persisting on every one
    // of them did a JSON Load+Save at 60Hz during drags — a major source of the
    // "拖动一卡一卡" stutter. Now the write happens once, 400ms after movement
    // settles (or immediately when the window closes).

    readonly Dictionary<object, (PropertyWindow w, ConfigService svc)> _pendingPersist = new();
    DispatcherTimer? _persistDebounce;

    void SchedulePersist(object target, PropertyWindow w, ConfigService configService)
    {
        _pendingPersist[target] = (w, configService);
        if (_persistDebounce == null)
        {
            _persistDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _persistDebounce.Tick += (_, _) => FlushPendingPersist();
        }
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    void FlushPendingPersist()
    {
        _persistDebounce?.Stop();
        if (_pendingPersist.Count == 0) return;
        var snapshot = new List<KeyValuePair<object, (PropertyWindow w, ConfigService svc)>>(_pendingPersist);
        _pendingPersist.Clear();
        foreach (var kv in snapshot)
            PersistRect(kv.Key, kv.Value.w, kv.Value.svc);
    }

    public static void RestoreAndActivate(PropertyWindow w)
    {
        if (w.WindowState == WindowState.Minimized)
            w.WindowState = WindowState.Normal;
        // 窗口完全在屏幕外(拔显示器等遗留位置)→ 拉回工作区,否则激活了也看不见。
        var wa = SystemParameters.WorkArea;
        if (w.Left + w.Width < wa.Left - 8 || w.Left > wa.Right + 8
            || w.Top + w.Height < wa.Top - 8 || w.Top > wa.Bottom + 8)
        {
            w.Left = wa.Left + 80;
            w.Top = wa.Top + 60;
        }
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
    static (double x, double y) ResolvePopPosition(object target, Window? requester, ManagementWindow main, ConfigService configService, Point? cursorScreen = null)
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

        // ponytail: drag-out from a tab — center the new floating PropertyWindow
        // on the cursor's drop position. Without this the fall-through lands at
        // step 3 (main-window right edge) which the user reads as "a stray small
        // floating window at the right edge of the screen".
        if (cursorScreen.HasValue)
            return CascadeIfColliding(cursorScreen.Value.X - 180, cursorScreen.Value.Y - 16, main);

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
    /// property — still better than losing all state on each save.
    /// ponytail 2026-08-25: PanelConfig is a singleton with NO Id property; the
    /// hashcode fallback produced a non-Guid key that ResolveTargetFromKey's
    /// Guid.TryParse rejected, so the docked panel resolved to null and showed
    /// nothing. Use the fixed literal "panel" so the key round-trips through
    /// every parse site.</summary>
    public static string TargetKey(object target)
    {
        if (target == null) return "";
        if (target is PanelConfig)
            return nameof(PanelConfig) + ":panel";
        // ponytail 2026-08-26: merged-group target keys by the stable GroupId
        // (survives master promotion on detach), not the master zone's Id.
        if (target is MergedGroupTarget g)
            return nameof(MergedGroupTarget) + ":" + g.GroupId;
        // Models expose `Id` — try reflection so we don't need a hard dep.
        var prop = target.GetType().GetProperty("Id");
        if (prop?.GetValue(target) is { } idVal && idVal != null)
            return target.GetType().Name + ":" + idVal;
        return target.GetType().Name + ":" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(target);
    }

    // ponytail 2026-08-25: window names mirror the management list rows and
    // the property-panel header (icon + name, Zone-style — no "某某设置"
    // labels). Notes keep their per-instance title when set.
    public static string TitleOf(object target)
    {
        if (target is StickyNote n && !string.IsNullOrEmpty(n.Title)) return n.Title;
        return target switch
        {
            Zone z => z.Name,
            MergedGroupTarget g => string.IsNullOrEmpty(g.Master.MergedGroupMembership.DisplayName)
                ? g.Master.Name : g.Master.MergedGroupMembership.DisplayName,
            DesktopClock c => c.Mode == ClockDisplayMode.Digital ? "Clock (数字)" : "Clock (钟表)",
            DesktopCalendar cal => $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}",
            StickyNote => "便签",
            PanelConfig => "控制面板",
            // ponytail 2026-08-26: SubFolder 编辑面板的标题直接取 ZoneItem.Name
            // (用户消息:右键重命名即可),不要回落到 "ZoneItem"。
            ZoneItem si => si.Name,
            _ => target.GetType().Name,
        };
    }

    public static string IconOf(object target) => target switch
    {
        Zone => "Icon.Zones",
        MergedGroupTarget => "Icon.Merged",
        DesktopClock => "Icon.Clock",
        DesktopCalendar => "Icon.Calendar",
        StickyNote => "Icon.Sticky",
        // ponytail 2026-08-24: Panel singleton gets its own tab icon instead
        // of the generic Settings gear so the docked/undocked panel tab is
        // visually distinct from the four property editors.
        PanelConfig => "Icon.Panel",
        // ponytail 2026-08-26: SubFolder 沿用 Icon.Folder 视觉一致性。
        ZoneItem si when si.Type == ItemType.SubFolder => "Icon.Folder",
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