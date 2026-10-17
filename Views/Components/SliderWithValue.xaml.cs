using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopZones.Views.Components;

/// <summary>
/// 「滑块 + 右侧数值」的复合控件（样式设置界面里所有进度类参数都用它）。
/// </summary>
/// <remarks>
/// ponytail 2026-10 (修用户报的「滑块右侧数字出现两位小数」):
/// 原来这里既没有 <c>IsSnapToTickEnabled</c>、数值显示又用 <c>0.##</c>。
/// 拖动滑块时 WPF 把像素位置换算回参数值，得到的本来就是 33.47 / 40.32 这种小数，
/// 于是右侧打出两位小数（实测 33.47 → "33.47"、40.317 → "40.32"）。模型里存的值
/// 也跟着变成小数。
///
/// 现在的口径：**整数类参数一律整数** —— 显示取整（0 位小数）、值吸附到 Tick
/// （默认 1，即整数），双击手输也会被四舍五入。<see cref="IsIntegerValue"/>
/// 保留给将来确实需要小数的参数，置 false 即恢复原来的 "0.##" 行为。
/// </remarks>
public partial class SliderWithValue : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SliderWithValue),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));
    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(
        nameof(Min), typeof(double), typeof(SliderWithValue), new PropertyMetadata(0.0));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(SliderWithValue), new PropertyMetadata(100.0));
    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(double), typeof(SliderWithValue), new PropertyMetadata(1.0));
    /// <summary>true（默认）= 只取 Tick 的整数倍、右侧只显示整数；false = 允许小数并按 "0.##" 显示。</summary>
    public static readonly DependencyProperty IsIntegerValueProperty = DependencyProperty.Register(
        nameof(IsIntegerValue), typeof(bool), typeof(SliderWithValue), new PropertyMetadata(true));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Min   { get => (double)GetValue(MinProperty);   set => SetValue(MinProperty, value); }
    public double Max   { get => (double)GetValue(MaxProperty);   set => SetValue(MaxProperty, value); }
    public double Tick  { get => (double)GetValue(TickProperty);  set => SetValue(TickProperty, value); }
    public bool IsIntegerValue { get => (bool)GetValue(IsIntegerValueProperty); set => SetValue(IsIntegerValueProperty, value); }

    public SliderWithValue()
    {
        InitializeComponent();
        // 值一变就刷新右侧文本（不再依赖 XAML 里的 StringFormat 绑定）。
        Loaded += (_, _) => UpdateValueText();
        PART_Slider.ValueChanged += (_, _) => UpdateValueText();
    }

    /// <summary>
    /// 整数模式(=1)下把任意来源的取值四舍五入到整数；小数模式原样返回。
    /// 注意这里**按 1 的步长**取整，不按 <see cref="Tick"/> —— Tick(5) 只用于画刻度，
    /// 若按它取整，滑块就只能选 5 的倍数（0/5/10…），精度会丢。
    /// </summary>
    static double Snap(double v, bool integer) => integer ? Math.Round(v, MidpointRounding.AwayFromZero) : v;

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (SliderWithValue)d;
        // 外部（预设 / 模型回填 / 双击手输 / 拖动落点）都可能给小数：这里统一取整，
        // 保证 Value 与其显示永远一致；已经整过的值不会再被改（无递归）。
        var snapped = Snap(self.Value, self.IsIntegerValue);
        if (snapped != self.Value) self.Value = snapped;
        self.UpdateValueText();
    }

    void UpdateValueText()
    {
        if (ValueText == null) return;
        var shown = Snap(Value, IsIntegerValue);
        ValueText.Text = shown.ToString(IsIntegerValue ? "0" : "0.##", CultureInfo.InvariantCulture);
    }

    void ValueText_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;

        var tb = (TextBlock)sender;
        var parent = (Border)tb.Parent;

        var box = new TextBox
        {
            Text = tb.Text,
            FontFamily = tb.FontFamily,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            BorderBrush = (Brush)FindResource("Brush.Accent"),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 1, 4, 1),
            MinWidth = tb.ActualWidth + 8
        };

        parent.Child = box;
        box.SelectAll();
        box.Focus();

        void Commit()
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                v = Math.Clamp(v, Min, Max);
                // 整数模式下把 33.6 也归成整数（OnValueChanged 会再兜一次）。
                v = Math.Clamp(Snap(v, IsIntegerValue), Min, Max);
                Value = v;
            }
            parent.Child = tb;
            UpdateValueText();
        }

        box.KeyDown += (_, ke) =>
        {
            if (ke.Key == Key.Enter)
            {
                Commit();
                ke.Handled = true;
            }
            else if (ke.Key == Key.Escape)
            {
                parent.Child = tb;
                ke.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit();
    }
}
