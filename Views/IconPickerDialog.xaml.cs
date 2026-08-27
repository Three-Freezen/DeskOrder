using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class IconPickerDialog : Window
{
    public string? SelectedIcon { get; private set; }
    private readonly LocalizationService _loc = LocalizationService.Instance;

    public IconPickerDialog()
    {
        InitializeComponent();
        BuildGrid();
        ApplyLoc();
    }

    void BuildGrid()
    {
        var fg = (Brush)FindResource("Brush.Text.Primary");
        foreach (var icon in IconGlyph.PresetIcons)
        {
            var btn = new Button
            {
                Width = 34, Height = 34,
                Background = Brushes.Transparent,
                Foreground = fg,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = icon,
            };

            var geo = IconGlyph.GetGeometry(icon);
            if (geo != null)
            {
                btn.Content = new Path
                {
                    Data = geo,
                    Width = 18, Height = 18,
                    Stretch = Stretch.Uniform,
                    Stroke = fg,
                    StrokeThickness = 1.5,
                    StrokeLineJoin = PenLineJoin.Round,
                };
            }
            else
            {
                btn.Content = new TextBlock
                {
                    Text = icon,
                    FontSize = 18,
                    Foreground = fg,
                };
            }

            btn.Click += (s, _) =>
            {
                SelectedIcon = ((Button)s!).Tag as string;
                DialogResult = true;
                Close();
            };
            IconGrid.Children.Add(btn);
        }
    }

    void ApplyLoc()
    {
        TitleLabel.Text = _loc["IconPicker.Title"];
        CancelBtn.Content = _loc["EmojiPicker.Cancel"];
    }

    void Cancel_Click(object s, RoutedEventArgs e) { DialogResult = false; Close(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    { base.OnMouseLeftButtonDown(e); try { DragMove(); } catch { } }
}
