using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace DesktopZones.Helpers;

/// <summary>
/// ComboBox 下拉弹层的「延迟关闭」控制器。
///
/// 为什么需要它：<c>Resources/Controls/ComboBox.xaml</c> 与 <c>ComboBox.Menu.xaml</c>
/// 把展开动画写在 IsDropDownOpen=True 触发器的 EnterActions 里，收回动画写在
/// ExitActions 里。但 Popup.IsOpen 原来直接 TemplateBind 到 IsDropDownOpen ——
/// IsDropDownOpen 一翻 false，弹层在同一帧就被关掉，ExitActions 的收回动画播在
/// 一块已经不可见的画布上，用户看到的就是「展开有动画、收回瞬关」。
///
/// 做法：Popup.IsOpen 改绑到这里的 <see cref="IsPopupOpenProperty"/>（中转态）。
///   · 打开：IsDropDownOpen=True 的同一拍把中转态置 true，弹层照常弹出
///     （展开动画仍在 XAML 里，不受影响）；
///   · 关闭：中转态先不动，让 XAML 的 ExitActions 把收回动画播完（Motion.Fast），
///     再把中转态置 false 真正关层。
///
/// 为什么盯 IsDropDownOpen 的「镜像绑定」而不是 DropDownClosed 事件：
/// WPF 的 ComboBox 只在 Popup 真正关闭时才抛 DropDownClosed —— 而这里恰恰不让
/// Popup 当场关闭，于是事件永远等不到（实测 closedEvents=0，弹层只能越等越开）。
/// <see cref="IsDropDownOpenMirrorProperty"/> 在模板里绑到 IsDropDownOpen，
/// 属性一变就同步回调，不依赖 Popup 的开关事件，也不引入 DPD 订阅（DPD 会 root 住控件）。
///
/// 开关挂在 ComboBox 的 Style 上（<c>helpers:ComboBoxDropMotion.Enable="True"</c>，
/// 两套下拉模板都已置位；它只负责 Unloaded 清理）。镜像绑定本身在模板里，
/// 没套这两套模板的 ComboBox（例如 Theme.xaml 里遗留的 DarkCombo）仍是老行为。
/// </summary>
public static class ComboBoxDropMotion
{
    /// <summary>在 ComboBox 的 Style 里置 True：卸载时清掉待关闭计时器。</summary>
    public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
        "Enable", typeof(bool), typeof(ComboBoxDropMotion),
        new PropertyMetadata(false, OnEnableChanged));

    public static void SetEnable(DependencyObject element, bool value) => element.SetValue(EnableProperty, value);
    public static bool GetEnable(DependencyObject element) => (bool)element.GetValue(EnableProperty);

    /// <summary>Popup.IsOpen 的中转源：打开即时，关闭延迟到收回动画播完。</summary>
    public static readonly DependencyProperty IsPopupOpenProperty = DependencyProperty.RegisterAttached(
        "IsPopupOpen", typeof(bool), typeof(ComboBoxDropMotion),
        new PropertyMetadata(false));

    public static void SetIsPopupOpen(DependencyObject element, bool value) => element.SetValue(IsPopupOpenProperty, value);
    public static bool GetIsPopupOpen(DependencyObject element) => (bool)element.GetValue(IsPopupOpenProperty);

    /// <summary>
    /// 挂在 Popup 上、绑到 ComboBox.IsDropDownOpen 的镜像。值一变就同步回调 ——
    /// 这是「下拉逻辑已关」的唯一信号（Popup 的开关事件在这个方案里被故意错开了）。
    /// </summary>
    public static readonly DependencyProperty IsDropDownOpenMirrorProperty = DependencyProperty.RegisterAttached(
        "IsDropDownOpenMirror", typeof(bool), typeof(ComboBoxDropMotion),
        new PropertyMetadata(false, OnMirrorChanged));

    public static void SetIsDropDownOpenMirror(DependencyObject element, bool value) => element.SetValue(IsDropDownOpenMirrorProperty, value);
    public static bool GetIsDropDownOpenMirror(DependencyObject element) => (bool)element.GetValue(IsDropDownOpenMirrorProperty);

    /// <summary>每个 ComboBox 一份「待关闭」计时器（Weak 表：不 root 住控件）。</summary>
    static readonly ConditionalWeakTable<ComboBox, DispatcherTimer> PendingClose = new();

    static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;

        // 先退订再订阅：主题切换可能重挂 Style，重复 += 会叠多份处理器。
        combo.Unloaded -= OnComboUnloaded;

        if (e.NewValue is not true)
        {
            CancelPendingClose(combo);
            SetIsPopupOpen(combo, false);
            return;
        }

        combo.Unloaded += OnComboUnloaded;

        // Style 挂上时下拉已经是打开态（少见）：同步一次中转态，别让弹层不出现。
        SetIsPopupOpen(combo, combo.IsDropDownOpen);
    }

    static void OnMirrorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup) return;
        if (popup.TemplatedParent is not ComboBox combo) return;

        if (e.NewValue is true) OnLogicalOpened(combo);
        else OnLogicalClosed(combo);
    }

    static void OnLogicalOpened(ComboBox combo)
    {
        CancelPendingClose(combo);
        // 弹层还开着时再次打开（收回动画途中）→ 保持 true，让 XAML 的 EnterActions
        // 反向接管动画，不闪一下。
        if (!GetIsPopupOpen(combo)) SetIsPopupOpen(combo, true);
    }

    static void OnLogicalClosed(ComboBox combo)
    {
        if (!GetIsPopupOpen(combo)) return;

        CancelPendingClose(combo);

        // 系统「关闭动画」偏好：直接关，不再等收回动画。
        if (Motion.IsReducedMotion())
        {
            SetIsPopupOpen(combo, false);
            return;
        }

        var delay = CloseDelay(combo);
        if (delay <= TimeSpan.Zero)
        {
            SetIsPopupOpen(combo, false);
            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Input, combo.Dispatcher)
        {
            Interval = delay,
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            PendingClose.Remove(combo);
            SetIsPopupOpen(combo, false);
        };
        PendingClose.Remove(combo);
        PendingClose.Add(combo, timer);
        timer.Start();
    }

    static void OnComboUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        CancelPendingClose(combo);
        SetIsPopupOpen(combo, false);
    }

    static void CancelPendingClose(ComboBox combo)
    {
        if (!PendingClose.TryGetValue(combo, out var timer)) return;
        timer.Stop();
        PendingClose.Remove(combo);
    }

    /// <summary>收回动画时长 = 模板里 ExitActions 的 Duration（Motion.Fast）。</summary>
    static TimeSpan CloseDelay(FrameworkElement combo)
    {
        if (combo.TryFindResource("Motion.Fast") is Duration duration && duration.HasTimeSpan)
            return duration.TimeSpan;
        return TimeSpan.FromMilliseconds(100);
    }
}
