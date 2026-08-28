using System;
using System.Windows;
using DesktopZones.Views;
using DesktopZones.Views.Components;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail: Static facade over PropertyWindowManager so any caller can open a
/// floating property window without holding a reference to ManagementWindow.
/// Initialized once by ManagementWindow's constructor. The optional requester
/// argument lets callers tell the manager "the gear button on THIS window was
/// the trigger" — used to anchor the popped-out window at the requester's
/// position (gear-button offset 24,24) so it visually pops from where the user
/// clicked instead of jumping to a remembered location.
/// </summary>
public static class PropertyWindowService
{
    static ManagementWindow? _main;

    public static void Init(ManagementWindow main)
    {
        // ponytail 2026-08-26: 窗口关闭后把静态引用清掉 — 否则关闭过管理界面后
        // _main 指向死窗口,EnsureMain 的 `_main != null` 短路导致永远不再重建,
        // 设置面板"静默打不开"(无异常可查)。
        if (!ReferenceEquals(_main, main))
        {
            if (_main != null) _main.Closed -= OnMainClosed;
            _main = main;
            main.Closed += OnMainClosed;
        }
    }

    static void OnMainClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _main)) _main = null;
    }

    /// <summary>Lazily create the ManagementWindow if it doesn't exist yet (StartMinimized
    /// startup keeps it null until first shown). Without this, opening a property editor
    /// from a zone/subfolder before the management UI was ever shown would no-op.</summary>
    static void EnsureMain()
    {
        if (_main != null) return;
        DzTrace.Log("[SubFlyout] PropertyWindowService: _main 为空,重建 ManagementWindow");
        (System.Windows.Application.Current as App)?.EnsureManagementWindow();
        if (_main == null)
            DzTrace.Log("[SubFlyout] PropertyWindowService: EnsureManagementWindow 后 _main 仍为空(Init 未生效)");
    }

    public static void OpenOrFocus(object target)
    {
        EnsureMain();
        _main?.OpenFloatingProperty(target);
    }

    public static void OpenOrFocus(object target, Window? requester)
    {
        EnsureMain();
        DzTrace.Log($"[SubFlyout] OpenOrFocus: main={(_main != null)} mainVisible={(_main?.IsVisible ?? false)} → OpenFloatingProperty");
        _main?.OpenFloatingProperty(target, requester);
    }

    /// <summary>ponytail 2026-08-28: gear 弹出 — anchorDip 为 ⚙ 点击点屏幕 DIP 坐标,
    /// 浮窗贴着它右下开(跳过历史 rect,避免窗口压住光标导致 ✕ 被下一次点击误关)。</summary>
    public static void OpenOrFocus(object target, Window? requester, Point anchorDip)
    {
        EnsureMain();
        DzTrace.Log($"[SubFlyout] OpenOrFocus(anchor): main={(_main != null)} mainVisible={(_main?.IsVisible ?? false)} anchor=({anchorDip.X:F0},{anchorDip.Y:F0}) target={PropertyWindowManager.TargetKey(target)}");
        _main?.OpenFloatingProperty(target, requester, anchorDip);
    }

    /// <summary>Close every property editor (floating window + docked tab/panel)
    /// currently showing <paramref name="target"/>. The delete funnels
    /// (zone / component / note / subfolder removal) call this right after the
    /// entity is removed so no stale editor lingers — a stale editor keeps the
    /// deleted instance alive and produces ghost components / crashes on further
    /// interaction. Deliberately does NOT call <see cref="EnsureMain"/>: if the
    /// management window is closed, the docked panel is gone with it and there
    /// is nothing to clear there.</summary>
    public static void CloseEditorsFor(object target)
    {
        if (target == null) return;
        PropertyWindowManager.Instance.CloseAllFor(target);
        _main?.CloseDockedEditorsFor(target);
    }
}