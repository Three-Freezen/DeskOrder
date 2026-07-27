using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views;

public partial class LoadPresetDialog : Window
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly PresetService _service;
    private readonly ObservableCollection<ZonePreset> _presets;
    private readonly Action<ZonePreset>? _onCardPicked;

    /// <summary>The preset the user committed to (set when DialogResult is true).</summary>
    public ZonePreset? SelectedPreset { get; private set; }

    private Border? _selectedCard; // the card that stays highlighted & scaled after the cursor leaves

    /// <summary>Cards fan out from the center: each index-step from center adds 4° of rotation.</summary>
    private const double AngleStep = 4.0;

    /// <summary>Scale factors applied on hover. Always reverts to 1.0 on MouseLeave;
    /// the selected card also reverts but keeps the Acc border + ZIndex.</summary>
    private const double HoverScaleX = 1.18;
    private const double HoverScaleY = 1.22;

    public LoadPresetDialog(PresetService service, Action<ZonePreset>? onCardPicked)
    {
        InitializeComponent();
        _service = service;
        _onCardPicked = onCardPicked;
        _presets = new ObservableCollection<ZonePreset>(service.LoadAll());
        ApplyLoc();

        if (_presets.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            CardScroller.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = false;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            CardScroller.Visibility = Visibility.Visible;
            ApplyButton.IsEnabled = false;  // enabled only after a card click
            PresetList.ItemsSource = _presets;
        }
    }

    private void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == Services.Language.Chinese;
        Title = _loc["Preset.LoadTitle"];
        DialogTitle.Text = _loc["Preset.LoadTitle"];
        EmptyHintText.Text = _loc["Preset.Empty"];
        ApplyButton.Content = _loc["Preset.Apply"];
        CancelButton.Content = _loc["Preset.Cancel"];
    }

    // ── Per-card setup ──

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.DataContext is not ZonePreset preset) return;

        var idx = _presets.IndexOf(preset);
        if (idx < 0) return;
        card.Tag = idx;   // remember index for default rotation
        SetCardAngle(card, DefaultAngleFor(idx));

        // Localize the right-click "Delete Preset" menu item (ContextMenu lives in a
        // separate namescope so we walk the Items collection rather than FindName).
        if (card.ContextMenu is { } menu && menu.Items.Count > 0 && menu.Items[0] is MenuItem item)
        {
            item.Header = _loc["Preset.DeleteMenuItem"];
        }
    }

    private double DefaultAngleFor(int idx) =>
        (idx - (_presets.Count - 1) / 2.0) * AngleStep;

    private static void SetCardAngle(Border card, double angle)
    {
        // TransformGroup: [0] = RotateTransform (set in code), [1] = ScaleTransform (animated in code).
        if (card.RenderTransform is TransformGroup tg &&
            tg.Children.Count > 0 &&
            tg.Children[0] is RotateTransform rt)
        {
            rt.Angle = angle;
        }
    }

    // ── Hover / Select: scale animation + ZIndex lift ──
    // The card itself scales up on hover; Panel.SetZIndex raises it above its neighbours
    // without any Panel.GetVisualChild override, so ItemsPanelTemplate is left untouched.

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border card) return;
        // Lift the hovered card to the front and scale it up.
        SetCardZIndex(card, 1);
        AnimateScale(card, HoverScaleX, HoverScaleY);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border card) return;
        // Always return to normal size and back in the fan stack on MouseLeave.
        // The selected card's Acc border is preserved (only ZIndex/scale revert).
        AnimateScale(card, 1.0, 1.0);
        SetCardZIndex(card, 0);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.DataContext is not ZonePreset preset) return;

        // Demote the previously selected card (if any) back to default scale + Z.
        var prev = _selectedCard;
        _selectedCard = card;
        if (prev != null && prev != card)
        {
            SetCardSelectedStyle(prev, selected: false);
            SetCardZIndex(prev, 0);
            AnimateScale(prev, 1.0, 1.0);
        }

        // Promote the newly clicked card.
        SetCardSelectedStyle(card, selected: true);
        SetCardZIndex(card, 1);
        AnimateScale(card, HoverScaleX, HoverScaleY);

        SelectedPreset = preset;
        ApplyButton.IsEnabled = true;

        // Push preview to the live zone window.
        try { _onCardPicked?.Invoke(preset); }
        catch { /* swallow preview errors so dialog stays usable */ }
    }

    /// <summary>Animate the card's ScaleTransform (index 1 in its TransformGroup) to (sx, sy).</summary>
    private static void AnimateScale(Border card, double sx, double sy)
    {
        if (card.RenderTransform is not TransformGroup tg) return;
        if (tg.Children.Count < 2) return;
        if (tg.Children[1] is not ScaleTransform st) return;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation(sx, TimeSpan.FromMilliseconds(200)) { EasingFunction = easing };
        var animY = new DoubleAnimation(sy, TimeSpan.FromMilliseconds(200)) { EasingFunction = easing };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, animX);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, animY);
    }

    /// <summary>
    /// Set Panel.ZIndex on the card's actual Panel child. The Border lives inside an
    /// auto-generated ContentPresenter (the ItemsControl's item container), and
    /// Panel.SetZIndex on the Border itself is a no-op — Panel only consults ZIndex
    /// on its direct children, so the value never reaches the render sort.
    /// </summary>
    private static void SetCardZIndex(Border card, int z)
    {
        // Fast path: in a DataTemplate the root's TemplatedParent is the ContentPresenter
        // that applied the template, which is exactly the FanPanel's direct child.
        if (card.TemplatedParent is UIElement container)
        {
            Panel.SetZIndex(container, z);
            return;
        }
        // Fallback: walk the visual tree until we find the node whose parent is a Panel.
        DependencyObject? current = card;
        while (current != null)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent == null) return;     // disconnected, give up
            if (parent is Panel && current is UIElement ui)
            {
                Panel.SetZIndex(ui, z);
                return;
            }
            current = parent;
        }
    }

    private static void SetCardSelectedStyle(Border card, bool selected)
    {
        card.BorderBrush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(selected ? "#FF7C3AED" : "#15787878")!);
        card.BorderThickness = new Thickness(selected ? 2 : 1);
    }

    // ── Buttons ──

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPreset == null) return;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    // ── Right-click menu ──

    private void DeletePresetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        // The MenuItem's DataContext is inherited from its placement target (the Card Border),
        // so it carries the ZonePreset we want to delete.
        if (sender is not MenuItem item) return;
        if (item.DataContext is not ZonePreset preset) return;

        var confirm = MessageBox.Show(
            _loc.Get("Preset.DeleteConfirmMessage", preset.Name),
            _loc["Preset.DeleteConfirmTitle"],
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        // Persist deletion to disk before mutating the UI list.
        _service.Delete(preset.Id);

        // If the deleted preset was the currently selected one, clear the selection state
        // so the Apply button disables and the live zone preview is no longer "stale".
        if (_selectedCard?.DataContext == preset)
        {
            _selectedCard = null;
            SelectedPreset = null;
            ApplyButton.IsEnabled = false;
        }

        // ObservableCollection raises CollectionChanged, so the ItemsControl drops the card
        // automatically — no need to re-assign ItemsSource.
        _presets.Remove(preset);

        // Last preset removed: switch the body to the empty-hint view.
        if (_presets.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            CardScroller.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = false;
        }
    }
}

/// <summary>Parses "#AARRGGBB"/"#RRGGBB" hex strings into <see cref="Color"/>.</summary>
public class HexColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var s = value as string ?? "#FFFFFF";
            // ConvertFromString supports #RRGGBB and #AARRGGBB; missing alpha defaults to FF.
            var color = (Color)ColorConverter.ConvertFromString(s)!;
            return color;
        }
        catch
        {
            return Colors.White;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a 0-100 percent value into a 0.0-1.0 opacity (used for
/// <c>GlassTintOpacity</c> and <c>BackgroundImageOpacity</c>).</summary>
public class PercentToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double d = value switch
        {
            double dv => dv,
            int iv => iv,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => 100.0
        };
        return Math.Clamp(d / 100.0, 0.0, 1.0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Horizontal fan layout: lays children out in a single row with a small <see cref="StepWidth"/> per card,
/// so neighbouring cards overlap heavily. Card rotation, Y offset, and scale are the caller's responsibility
/// (applied per-card via <c>RenderTransform</c> in the DataTemplate).
/// </summary>
public class FanPanel : Panel
{
    public static readonly DependencyProperty CardWidthProperty =
        DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(FanPanel),
            new FrameworkPropertyMetadata(180.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty CardHeightProperty =
        DependencyProperty.Register(nameof(CardHeight), typeof(double), typeof(FanPanel),
            new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty StepWidthProperty =
        DependencyProperty.Register(nameof(StepWidth), typeof(double), typeof(FanPanel),
            new FrameworkPropertyMetadata(45.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty VerticalPaddingProperty =
        DependencyProperty.Register(nameof(VerticalPadding), typeof(double), typeof(FanPanel),
            new FrameworkPropertyMetadata(40.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }
    public double CardHeight { get => (double)GetValue(CardHeightProperty); set => SetValue(CardHeightProperty, value); }
    public double StepWidth { get => (double)GetValue(StepWidthProperty); set => SetValue(StepWidthProperty, value); }
    /// <summary>Extra room above/below the row so rotated cards aren't clipped by the parent.</summary>
    public double VerticalPadding { get => (double)GetValue(VerticalPaddingProperty); set => SetValue(VerticalPaddingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = new Size(CardWidth, CardHeight);
        foreach (UIElement child in Children) child.Measure(size);

        int count = Children.Count;
        double totalW = count == 0 ? 0 : (count - 1) * StepWidth + CardWidth;
        double totalH = CardHeight + 2 * VerticalPadding;
        return new Size(totalW, totalH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        for (int i = 0; i < count; i++)
        {
            double x = i * StepWidth;
            double y = (finalSize.Height - CardHeight) / 2.0;
            Children[i].Arrange(new Rect(x, y, CardWidth, CardHeight));
        }
        return finalSize;
    }
}
