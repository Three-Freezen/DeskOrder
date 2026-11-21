using System;
using System.Collections.Generic;
using System.Windows;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones;

/// <summary>
/// 从右到左（RTL）排版接入：语言包是 ar / he / fa / ur 等 RTL 语言时，把界面整体镜像。
///
/// 做法：在启动早期给 <see cref="Window"/> 注册 <see cref="FrameworkElement.LoadedEvent"/> 的
/// 类处理器（每个窗口第一次显示时套用），并订阅 <see cref="LocalizationService.LanguageChanged"/> /
/// <see cref="LocalizationService.LanguageFallback"/>，语言切换或语言包安装/卸载导致回退时，
/// 遍历 <see cref="Application.Current"/> 的窗口逐个刷新。
///
/// 范围取舍：桌面层窗口（分区 / 分区坞 / 面板 / 时钟日历媒体组件 / 便签 / 插件组件）**不镜像**。
/// 它们的布局是绝对坐标 + 自绘视觉 + 拖拽命中区，整体镜像会把吸附边、芯片顺序和拖动落点
/// 全反过来，而 RTL 用户真正需要读的是管理界面和对话框里的文本。所以这里用「排除桌面层」
/// 的方式：管理窗口、属性面板和所有对话框都跟随语言，桌面层保持 LTR。
/// </summary>
public partial class App
{
    /// <summary>不套用 FlowDirection 的桌面层窗口（自己画布局的那一类）。</summary>
    static readonly HashSet<Type> RtlExcludedWindowTypes = new()
    {
        typeof(ZoneWindow),
        typeof(ZoneBayWindow),
        typeof(PanelWindow),
        typeof(ClockWidget),
        typeof(CalendarWidget),
        typeof(MediaPlayerWidget),
        typeof(StickyNoteWindow),
        typeof(PluginWidgetWindow)
    };

    static bool _rtlSupportInitialized;

    /// <summary>在创建任何窗口之前调用一次（见 <see cref="Application_Startup"/>）。</summary>
    void InitializeRtlSupport()
    {
        if (_rtlSupportInitialized) return;
        _rtlSupportInitialized = true;

        // 类处理器比在 XAML 里逐个窗口写 Loaded 更可靠：以后新加的对话框自动生效。
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnRtlWindowLoaded), handledEventsToo: true);

        _loc.LanguageChanged += _ => RefreshRtlWindows();
        // 语言包被卸载/停用导致当前语言回退时也刷新（LanguageChanged 一般也会来一次，
        // 但回退路径先发 LanguageFallback，这里订阅才能在同一次刷新里改好方向）。
        _loc.LanguageFallback += (_, _) => RefreshRtlWindows();
    }

    static void OnRtlWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window) ApplyRtlFlowDirection(window);
    }

    /// <summary>把已打开的窗口全部按当前语言刷新一遍；关闭期跳过，单个窗口异常不影响其它窗口。</summary>
    static void RefreshRtlWindows()
    {
        var app = Application.Current;
        if (app == null || Helpers.AppLifetime.IsShuttingDown) return;
        foreach (Window window in app.Windows)
        {
            try { ApplyRtlFlowDirection(window); }
            catch { /* 正在关闭的窗口：忽略 */ }
        }
    }

    static void ApplyRtlFlowDirection(Window? window)
    {
        if (window == null) return;
        if (Helpers.AppLifetime.IsShuttingDown) return;
        if (window.Dispatcher.HasShutdownStarted) return;
        if (RtlExcludedWindowTypes.Contains(window.GetType())) return;

        var target = LocalizationService.Instance.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
        if (window.FlowDirection != target) window.FlowDirection = target;
    }
}
