using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// Converts a hex color string ("#RRGGBB" or "RRGGBB", or "Transparent") into a SolidColorBrush.
/// </summary>
public class HexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return Brushes.Transparent;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(s);
            return new SolidColorBrush(color);
        }
        catch
        {
            return Brushes.Transparent;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 24x24 color button that opens a popup palette. Bound via CurrentColor dependency property.
/// </summary>
public partial class ColorSwatchButton : UserControl
{
    readonly LocalizationService _loc = LocalizationService.Instance;

    public static readonly DependencyProperty CurrentColorProperty = DependencyProperty.Register(
        nameof(CurrentColor), typeof(string), typeof(ColorSwatchButton),
        new FrameworkPropertyMetadata("#00000000", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public string CurrentColor
    {
        get => (string)GetValue(CurrentColorProperty);
        set => SetValue(CurrentColorProperty, value);
    }

    static readonly string[] Presets =
    {
        "Transparent", "#000000", "#FFFFFF", "#808080",
        "#FF5252", "#FFA726", "#FFEB3B", "#66BB6A",
        "#42A5F5", "#26C6DA", "#AB47BC", "#8D6E63",
    };

    /// <summary>
    /// 「透明」预设要写进模型的字符串。**必须是合法颜色值** —— 以前这里直接把
    /// 字面量 "Transparent" 存进配置，后续 SetPercent 会把它切成
    /// "ransparent" 再拼成 "#4Cransparent" 这种垃圾串，导致透明度滑块永久失效
    /// （用户实测：分区配置里出现 "#00nsparent"）。#00FFFFFF = 全透明白。
    /// </summary>
    const string TransparentValue = "#00FFFFFF";

    // ── 色板弹层：展开/收回动效 + 点外关闭 ──
    //
    // 与 ComboBox 下拉同一套配方：透明度 0↔1 + ScaleY 0.92↔1（原点在上边中点）。
    // 弹层改成 StaysOpen=True 自己接管「点外关闭」——StaysOpen=False 时 WPF 会在
    // 点外的那一帧直接关掉弹层，收回动画同样没机会播（下拉框遇到的就是这个问题）。
    // 同一时刻只允许一块色板开着（_openInstance），避免快速点另一颗色板时两块同屏。

    static ColorSwatchButton? _openInstance;
    Window? _lightDismissHost;
    bool _closing;

    public ColorSwatchButton()
    {
        InitializeComponent();
        Loaded += (_, _) => BuildSwatches();
        // 字段树重建（切目标/重建属性面板）时控件会被卸载：弹层跟着收掉。
        Unloaded += (_, _) => CloseImmediately();
    }

    void BuildSwatches()
    {
        Swatches.Children.Clear();
        foreach (var hex in Presets)
        {
            Brush brush;
            bool isTransparent = false;
            if (hex == "Transparent")
            {
                // ponytail 2026-08-25: 透明预设就是 Brushes.Transparent —
                // 直接显示下层 popup 背景（Brush.Bg.Surface），周围 Border 仍然区分得出格子边界。
                isTransparent = true;
                brush = Brushes.Transparent;
            }
            else
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(hex);
                    brush = new SolidColorBrush(color);
                }
                catch
                {
                    brush = Brushes.Transparent;
                }
            }

            var btn = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2),
                Background = brush,
                BorderBrush = (Brush)FindResource("Brush.Border.Default"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            btn.Resources.Add(typeof(Border), new Style(typeof(Border))
            {
                Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(3)) },
            });
            // ponytail 2026-08-25: 透明预设鼠标悬浮显示「透明」提示，避免用户以为是空格/不可点。
            if (isTransparent)
                ToolTipService.SetToolTip(btn, _loc["Common.Transparent"]);
            var captured = hex;
            btn.Click += (_, _) =>
            {
                // ponytail 2026-10: 「透明」写入合法颜色值而不是字面量 "Transparent"，
                // 否则后续按 alpha 换算透明度时会拼出垃圾串，滑块从此失效。
                CurrentColor = hex == "Transparent" ? TransparentValue : captured;
                ClosePopup();
            };
            Swatches.Children.Add(btn);
        }
    }

    // ── 开合 ──

    void PART_Button_Click(object sender, RoutedEventArgs e)
    {
        // 收回途中再点 → 反向展开（否则这 100ms 里再点会「没反应」）。
        if (_closing) { OpenPopup(); return; }
        if (PART_Popup.IsOpen) ClosePopup();
        else OpenPopup();
    }

    void OpenPopup()
    {
        // 另一颗色板还开着：立刻收掉，别让两块色板同屏。
        if (_openInstance is { } previous && !ReferenceEquals(previous, this))
            previous.CloseImmediately();
        _openInstance = this;

        _closing = false;
        PopupRoot.IsHitTestVisible = true;
        if (!PART_Popup.IsOpen) PART_Popup.IsOpen = true;
        HookLightDismiss();

        if (Motion.IsReducedMotion())
        {
            ResetVisualState(opened: true);
            return;
        }

        // 从当前进度反向（收回途中再次打开时不会跳回 0）。
        var fromOpacity = PopupRoot.Opacity;
        var fromScale = PopupScale.ScaleY;
        var duration = MotionDuration("Motion.Fast", 100);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PopupRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(fromOpacity, 1, duration) { EasingFunction = ease });
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(fromScale, 1, duration) { EasingFunction = ease });
    }

    void ClosePopup()
    {
        if (!PART_Popup.IsOpen || _closing) return;
        _closing = true;
        // 淡出期间弹层还在屏上，但逻辑上已经关闭：别让它吃点击。
        PopupRoot.IsHitTestVisible = false;

        if (Motion.IsReducedMotion())
        {
            FinishClose();
            return;
        }

        var duration = MotionDuration("Motion.Fast", 100);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fireOnce = Once(2, FinishClose);

        var fade = new DoubleAnimation(PopupRoot.Opacity, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => fireOnce();
        PopupRoot.BeginAnimation(OpacityProperty, fade);

        var shrink = new DoubleAnimation(PopupScale.ScaleY, 0.92, duration) { EasingFunction = ease };
        shrink.Completed += (_, _) => fireOnce();
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    void FinishClose()
    {
        _closing = false;
        if (PART_Popup.IsOpen) PART_Popup.IsOpen = false;  // Closed 里统一复位
        else ResetVisualState(opened: false);
    }

    void CloseImmediately()
    {
        _closing = false;
        if (PART_Popup.IsOpen) PART_Popup.IsOpen = false;  // 触发 Closed → 复位 + 退订
        ResetVisualState(opened: false);
        UnhookLightDismiss();
        if (ReferenceEquals(_openInstance, this)) _openInstance = null;
    }

    void PART_Popup_Closed(object? sender, EventArgs e)
    {
        // 自己关的、被窗口关闭带走的、被系统收起的，都走这里复位，避免脏状态：
        // 下次打开会从 0/0.92 正常起步，不会被“上一条动画停住的透明度”卡住。
        _closing = false;
        ResetVisualState(opened: false);
        UnhookLightDismiss();
        if (ReferenceEquals(_openInstance, this)) _openInstance = null;
    }

    void ResetVisualState(bool opened)
    {
        PopupRoot.BeginAnimation(OpacityProperty, null);
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PopupRoot.Opacity = opened ? 1 : 0;
        PopupScale.ScaleY = opened ? 1 : 0.92;
    }

    Duration MotionDuration(string key, double fallbackMs)
        => TryFindResource(key) is Duration d && d.HasTimeSpan
            ? d
            : new Duration(TimeSpan.FromMilliseconds(fallbackMs));

    static Action Once(int count, Action done)
    {
        int remaining = count;
        bool fired = false;
        return () =>
        {
            if (fired) return;
            if (--remaining > 0) return;
            fired = true;
            done();
        };
    }

    // ── 点外关闭（StaysOpen=True 的配套） ──

    void HookLightDismiss()
    {
        if (_lightDismissHost != null) return;
        var window = Window.GetWindow(this);
        if (window == null) return;
        _lightDismissHost = window;
        window.PreviewMouseDown += OnHostPreviewMouseDown;
        window.Deactivated += OnHostDeactivated;
        window.PreviewKeyDown += OnHostPreviewKeyDown;
    }

    void UnhookLightDismiss()
    {
        if (_lightDismissHost is not { } window) return;
        window.PreviewMouseDown -= OnHostPreviewMouseDown;
        window.Deactivated -= OnHostDeactivated;
        window.PreviewKeyDown -= OnHostPreviewKeyDown;
        _lightDismissHost = null;
    }

    void OnHostPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!PART_Popup.IsOpen || _closing) return;
        // 点在自己的按钮上：交给 Click 去开合，这里先关会变成「关了又开」。
        if (IsWithin(e.OriginalSource as DependencyObject, this)) return;
        ClosePopup();
    }

    void OnHostDeactivated(object? sender, EventArgs e) => ClosePopup();

    void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !PART_Popup.IsOpen) return;
        ClosePopup();
        e.Handled = true;
    }

    void PopupRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ClosePopup();
        e.Handled = true;
    }

    static bool IsWithin(DependencyObject? node, DependencyObject root)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, root)) return true;
            node = node switch
            {
                Visual => VisualTreeHelper.GetParent(node),
                System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
                _ => LogicalTreeHelper.GetParent(node),
            };
        }
        return false;
    }

    void Custom_Click(object sender, RoutedEventArgs e)
    {
        // 立刻收掉再开模态框（收回动画会被对话框盖住，没必要播）。
        CloseImmediately();
        var dlg = new ColorPickerDialog(CurrentColor ?? "FFFFFF")
        {
            Owner = Window.GetWindow(this)
        };
        if (dlg.ShowDialog() == true)
            CurrentColor = dlg.SelectedColor;
    }
}
