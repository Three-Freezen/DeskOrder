using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopZones.Views.Components;

public partial class SliderWithValue : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(SliderWithValue),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty MinProperty = DependencyProperty.Register(
        nameof(Min), typeof(double), typeof(SliderWithValue), new PropertyMetadata(0.0));
    public static readonly DependencyProperty MaxProperty = DependencyProperty.Register(
        nameof(Max), typeof(double), typeof(SliderWithValue), new PropertyMetadata(100.0));
    public static readonly DependencyProperty TickProperty = DependencyProperty.Register(
        nameof(Tick), typeof(double), typeof(SliderWithValue), new PropertyMetadata(1.0));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Min   { get => (double)GetValue(MinProperty);   set => SetValue(MinProperty, value); }
    public double Max   { get => (double)GetValue(MaxProperty);   set => SetValue(MaxProperty, value); }
    public double Tick  { get => (double)GetValue(TickProperty);  set => SetValue(TickProperty, value); }

    public SliderWithValue()
    {
        InitializeComponent();
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
                Value = v;
            }
            parent.Child = tb;
            tb.Text = Value.ToString("0.##", CultureInfo.InvariantCulture);
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