using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Cards;

namespace DesktopZones.Views;

public partial class LoadPresetDialog : Window, INotifyPropertyChanged
{
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly PresetService _service;
    private readonly Services.WidgetService? _widgetService;
    private readonly ObservableCollection<PresetRecord> _presets;
    /// <summary>Wrapper items fed to the ItemsControl. Backing collection of typed wrappers.</summary>
    private readonly ObservableCollection<Cards.PresetCardItem> _items = new();
    private readonly Action<PresetRecord>? _onCardPicked;
    private readonly DispatcherTimer? _clockModeTimer;

    /// <summary>The preset the user committed to (set when DialogResult is true).</summary>
    public PresetRecord? SelectedPreset { get; private set; }

    /// <summary>The typed payload out of <see cref="SelectedPreset"/> (Zone / DesktopClock / …).</summary>
    public object? SelectedPayload { get; private set; }

    private Border? _selectedCard;

    /// <summary>Live clock mode for ClockCard auto-switching (Digital ↔ Analog). Updates every 500ms.</summary>
    private ClockDisplayMode _liveClockMode = ClockDisplayMode.Digital;
    public ClockDisplayMode LiveClockMode
    {
        get => _liveClockMode;
        private set
        {
            if (_liveClockMode != value)
            {
                _liveClockMode = value;
                // Push the new live mode to every Clock card so its template re-evaluates
                // (ItemsControl re-runs the selector when the bound item raises PropertyChanged
                // for the property the selector reads).
                foreach (var item in _items)
                {
                    if (item.Kind == PresetKind.Clock) item.DisplayClockMode = value;
                }
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Cards fan out from the center: each index-step from center adds 4° of rotation.</summary>
    private const double AngleStep = 4.0;
    private const double HoverScaleX = 1.18;
    private const double HoverScaleY = 1.22;

    public LoadPresetDialog(
        PresetService service,
        Services.WidgetService? widgetService = null,
        Action<PresetRecord>? onCardPicked = null)
    {
        InitializeComponent();
        _service = service;
        _widgetService = widgetService;
        _onCardPicked = onCardPicked;
        _presets = new ObservableCollection<PresetRecord>(service.LoadAll());

        // 尖角裁切修复：去掉 DWM 给无边框分层窗口绘制的矩形阴影并请求 Win11 圆角，
        // 与主窗口(ZoneWindow 等)同一套处理，避免圆角内容四周残留直角阴影。
        Loaded += (_, _) =>
        {
            NativeMethods.DisableDwmFrameShadow(this);
            NativeMethods.SetRoundedCorners(this, 12);
        };

        // Card template is selected by PresetCardTemplateSelector (declared in XAML)
        // based on each item's Kind — no per-call template wiring needed.

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
            ApplyButton.IsEnabled = false;
            RebuildItems();
            PresetList.ItemsSource = _items;
        }

        // ClockCard live-mode polling: only active for Clock / MergedGroup / Panel
        // (Panel has its own clock readout too — see the live Panel top bar).
        if (_widgetService != null && (service.Kind == PresetKind.Clock || service.Kind == PresetKind.Panel))
        {
            LiveClockMode = _widgetService.GetActiveClock()?.Mode ?? ClockDisplayMode.Digital;
            _clockModeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _clockModeTimer.Tick += (_, _) =>
            {
                var m = _widgetService.GetActiveClock()?.Mode;
                if (m.HasValue) LiveClockMode = m.Value;
            };
            _clockModeTimer.Start();
            Closed += (_, _) => _clockModeTimer.Stop();

            // Auto-select the first preset whose stored mode matches the live clock.
            Dispatcher.BeginInvoke(new Action(AutoSelectMatchingMode), DispatcherPriority.Loaded);
        }
    }

    private void AutoSelectMatchingMode()
    {
        // Only auto-select when there's exactly one preset whose stored mode matches
        // the live clock mode. Multiple matches are ambiguous (e.g. several Digital
        // presets) — picking the first would silently overwrite the widget model via
        // onCardPicked without user action, so we leave selection to the user.
        PresetCardItem? match = null;
        foreach (var item in _items)
        {
            if (item.Payload is DesktopClock c && c.Mode == LiveClockMode)
            {
                if (match != null) return;
                match = item;
            }
        }
        if (match == null) return;

        if (PresetList.ItemContainerGenerator.ContainerFromItem(match) is DependencyObject container)
        {
            var card = FindVisualChild<Border>(container);
            if (card != null)
            {
                Card_MouseLeftButtonDown(card, new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, 0, MouseButton.Left));
            }
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var result = FindVisualChild<T>(child);
            if (result != null) return result;
        }
        return null;
    }

    private void ApplyLoc()
    {
        var cn = _loc.CurrentLanguage == "zh";
        // Window/Dialog title — derived per-kind. See SavePresetDialog for the matching key set.
        var titleKey = $"Preset.LoadTitle.{_service.Kind}";
        Title = _loc[titleKey];
        DialogTitle.Text = _loc[titleKey];
        EmptyHintText.Text = _loc["Preset.Empty"];
        ApplyButton.Content = _loc["Preset.Apply"];
        CancelButton.Content = _loc["Preset.Cancel"];
    }

    // ── Items wrapping ──

    private void RebuildItems()
    {
        _items.Clear();
        try {
            var log = new System.Text.StringBuilder();
            log.AppendLine($"=== RebuildItems kind={_service.Kind} _presets.Count={_presets.Count} ===");
            foreach (var p in _presets) {
                if (p is PanelPreset pp)
                    log.AppendLine($"  Name='{p.Name}' Fill={pp.Config.PanelFillColor} Glass={pp.Config.GlassColorMode} Ctl={pp.Config.PanelControlOpacity}");
                else
                    log.AppendLine($"  Name='{p.Name}' type={p.GetType().Name}");
            }
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preset_debug.log"), log.ToString());
        } catch (Exception ex) {
            System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preset_debug.log"), "EX: " + ex);
        }
        foreach (var p in _presets)
        {
            // Calendar needs precomputed day grid + weekday header that the base wrapper
            // can't carry. Other kinds (including MergedGroup, whose card is intentionally
            // hardcoded with a fixed 3-tab layout) use the plain wrapper.
            PresetCardItem item = p switch
            {
                CalendarPreset cp => new Cards.CalendarPresetCardItem(cp),
                _ => new Cards.PresetCardItem(p)
            };
            // Seed clock items with the current live mode so the first template evaluation
            // already uses it (instead of the per-preset stored Mode).
            if (item.Kind == PresetKind.Clock) item.DisplayClockMode = _liveClockMode;
            _items.Add(item);
        }
    }

    // ── Per-card setup ──

    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.DataContext is not Cards.PresetCardItem item) return;

        // Construct TransformGroup in code so the Freezable is NOT auto-frozen by the
        // XAML parser. Inline <Border.RenderTransform><TransformGroup>...</TransformGroup>
        // in XAML freezes the group during parse, making RotateTransform.Angle read-only.
        // A code-constructed TransformGroup stays mutable for SetCardAngle / AnimateScale.
        if (card.RenderTransform is not TransformGroup || card.RenderTransform.IsFrozen)
        {
            card.RenderTransform = new TransformGroup
            {
                Children = new TransformCollection
                {
                    new RotateTransform(),
                    new ScaleTransform()
                }
            };
        }

        var idx = _items.IndexOf(item);
        if (idx < 0) return;
        card.Tag = idx;
        SetCardAngle(card, DefaultAngleFor(idx));

        // Right-click delete: build the ContextMenu in code so the routed-event handler
        // isn't trapped inside a Style.Setter.Value (WPF BAML compiler picks a wrong
        // host type for AddHandler calls in that location, breaking the build).
        if (card.ContextMenu == null)
        {
            var mi = new MenuItem { Header = _loc["Preset.DeleteMenuItem"] };
            mi.Click += DeletePresetMenuItem_Click;
            card.ContextMenu = new ContextMenu { Items = { mi } };
        }
    }

    private double DefaultAngleFor(int idx) =>
        0; // TEMP DEBUG: disable fan rotation so we can see the true colors without visual overlap

    private static void SetCardAngle(Border card, double angle)
    {
        if (card.RenderTransform is TransformGroup tg &&
            tg.Children.Count > 0 &&
            tg.Children[0] is RotateTransform rt)
        {
            rt.Angle = angle;
        }
    }

    // ── Hover / Select ──

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border card) return;
        SetCardZIndex(card, 1);
        AnimateScale(card, HoverScaleX, HoverScaleY);
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border card) return;
        AnimateScale(card, 1.0, 1.0);
        SetCardZIndex(card, 0);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border card) return;
        if (card.DataContext is not Cards.PresetCardItem item) return;

        var prev = _selectedCard;
        _selectedCard = card;
        if (prev != null && prev != card)
        {
            SetCardSelectedStyle(prev, selected: false);
            SetCardZIndex(prev, 0);
        }

        SetCardSelectedStyle(card, selected: true);
        SetCardZIndex(card, 1);

        SelectedPreset = item.Record;
        SelectedPayload = item.Payload;
        ApplyButton.IsEnabled = true;

        try { _onCardPicked?.Invoke(item.Record); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[preset preview] {ex}"); }
    }

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
        if (card.TemplatedParent is UIElement container)
        {
            Panel.SetZIndex(container, z);
            return;
        }
        DependencyObject? current = card;
        while (current != null)
        {
            var parent = VisualTreeHelper.GetParent(current);
            if (parent == null) return;
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
        // ponytail: matches EditableListRow.IsSelected — accent border + DropShadowEffect
        // outer glow, no Storyboard (animated feel lives on the management list row;
        // preset card selection is instant). Color comes from app theme so dark/light/
        // high-contrast all keep their accent.
        if (selected)
        {
            card.BorderBrush = new SolidColorBrush(
                (Color)Application.Current.Resources["Color.Accent"]);
            card.BorderThickness = new Thickness(3);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = (Color)Application.Current.Resources["Color.Accent"],
                BlurRadius = 12,
                ShadowDepth = 0,
                Opacity = 0.55,
            };
        }
        else
        {
            card.BorderBrush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#15787878")!);
            card.BorderThickness = new Thickness(1);
            card.Effect = null;
        }
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
        if (sender is not MenuItem item) return;
        if (item.DataContext is not Cards.PresetCardItem ci) return;

        var preset = ci.Record;
        var confirm = MessageBox.Show(
            _loc.Get("Preset.DeleteConfirmMessage", preset.Name),
            _loc["Preset.DeleteConfirmTitle"],
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (!_service.Delete(preset.Id))
        {
            MessageBox.Show(
                _loc.Get("Preset.DeleteFailedMessage", preset.Name),
                _loc["Preset.DeleteFailedTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (_selectedCard?.DataContext == ci)
        {
            _selectedCard = null;
            SelectedPreset = null;
            SelectedPayload = null;
            ApplyButton.IsEnabled = false;
        }

        _presets.Remove(preset);
        _items.Remove(ci);

        if (_presets.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            CardScroller.Visibility = Visibility.Collapsed;
            ApplyButton.IsEnabled = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Parses "#AARRGGBB"/"#RRGGBB" hex strings into <see cref="Color"/>.</summary>
public class HexColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var s = value as string ?? "#FFFFFF";
            return (Color)ColorConverter.ConvertFromString(s)!;
        }
        catch { return Colors.White; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Converts a 0-100 percent value into a 0.0-1.0 opacity.</summary>
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
/// Maps a <see cref="ClockDisplayMode"/> value to Visibility. Parameter is the
/// mode name to match ("Digital" or "Analog"); mismatched → Collapsed.
/// </summary>
public class ModeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var want = (parameter as string) ?? "";
        var have = value?.ToString() ?? "";
        return string.Equals(want, have, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Maps a <see cref="ClockDisplayMode"/> value to a localized display name.</summary>
public class ModeToNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var mode = value?.ToString() ?? "";
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        return mode switch
        {
            "Digital" => cn ? "数字" : "Digital",
            "Analog" => cn ? "指针" : "Analog",
            _ => mode
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Maps <see cref="Models.Zone.GlassColorMode"/> strings (e.g. "OceanBlue", "RosePink")
/// to a 3-stop <see cref="LinearGradientBrush"/> tinted around that mode's base color.
/// KEEP IN SYNC with <c>Helpers/AcrylicHelper.cs : ResolveBaseColorARGB</c>.
/// </summary>
public class LiquidGlassBrushConverter : IValueConverter
{
    public static readonly Dictionary<string, Color> BaseColors = new()
    {
        // "Default" is the preset card preview's gradient identity (purple-blue) when a preset
        // has no explicit FillColor. Note: this differs from AcrylicHelper.ResolveBaseColorARGB
        // (which returns 0x00000000) — here the preview wants a visible gradient, while live
        // PanelWindow rendering uses transparent. Keep these in sync only if behavior should match.
        ["Default"]       = Color.FromArgb(0xFF, 0x70, 0x95, 0xC5),
        ["Accent"]        = Color.FromArgb(0xFF, 0x40, 0x90, 0xE2),
        ["GlassWhite"]    = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        ["MistGrey"]      = Color.FromArgb(0xFF, 0xC0, 0xC0, 0xC0),
        ["DeepBlack"]     = Color.FromArgb(0xFF, 0x10, 0x10, 0x10),
        ["OceanBlue"]     = Color.FromArgb(0xFF, 0x11, 0x85, 0xFF),
        ["AuroraCyan"]    = Color.FromArgb(0xFF, 0x00, 0xD4, 0xD4),
        ["RosePink"]      = Color.FromArgb(0xFF, 0xFF, 0x69, 0xB4),
        ["BordeauxRed"]   = Color.FromArgb(0xFF, 0x8B, 0x00, 0x00),
        ["ForestGreen"]   = Color.FromArgb(0xFF, 0x22, 0x8B, 0x22),
        ["RoyalPurple"]   = Color.FromArgb(0xFF, 0x6A, 0x0D, 0xAD),
        ["SunsetOrange"]  = Color.FromArgb(0xFF, 0xFF, 0x8C, 0x00),
        ["ChampagneGold"] = Color.FromArgb(0xFF, 0xDA, 0xA5, 0x20),
        ["MorandiSage"]   = Color.FromArgb(0xFF, 0x87, 0xA9, 0x6B),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string mode = (value as string) ?? "Default";
        if (!BaseColors.TryGetValue(mode, out var baseColor))
            mode = "Default";
        baseColor = BaseColors[mode];

        const byte stopAlpha = 0xC0;
        baseColor = Color.FromArgb(stopAlpha, baseColor.R, baseColor.G, baseColor.B);

        var brighter = Lighten(baseColor, 0.35);
        var darker   = Darken(baseColor,   0.30);

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(brighter, 0.0));
        brush.GradientStops.Add(new GradientStop(baseColor, 0.5));
        brush.GradientStops.Add(new GradientStop(darker,   1.0));
        return brush;
    }

    static Color Lighten(Color c, double amt) => Color.FromArgb(c.A,
        (byte)Math.Min(255, c.R + (255 - c.R) * amt),
        (byte)Math.Min(255, c.G + (255 - c.G) * amt),
        (byte)Math.Min(255, c.B + (255 - c.B) * amt));

    static Color Darken(Color c, double amt) => Color.FromArgb(c.A,
        (byte)(c.R * (1 - amt)),
        (byte)(c.G * (1 - amt)),
        (byte)(c.B * (1 - amt)));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Horizontal fan layout: lays children out in a single row with a small <see cref="StepWidth"/> per card.
/// Card rotation, Y offset, and scale are the caller's responsibility (applied per-card via RenderTransform).
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