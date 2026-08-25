using System;
using System.Windows;
using DesktopZones.Views;

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
        System.Diagnostics.Trace.WriteLine("[SubFlyout] PropertyWindowService: _main 为空,重建 ManagementWindow");
        (System.Windows.Application.Current as App)?.EnsureManagementWindow();
        if (_main == null)
            System.Diagnostics.Trace.WriteLine("[SubFlyout] PropertyWindowService: EnsureManagementWindow 后 _main 仍为空(Init 未生效)");
    }

    public static void OpenOrFocus(object target)
    {
        EnsureMain();
        _main?.OpenFloatingProperty(target);
    }

    public static void OpenOrFocus(object target, Window? requester)
    {
        EnsureMain();
        System.Diagnostics.Trace.WriteLine($"[SubFlyout] OpenOrFocus: main={(_main != null)} mainVisible={(_main?.IsVisible ?? false)} → OpenFloatingProperty");
        _main?.OpenFloatingProperty(target, requester);
    }
}