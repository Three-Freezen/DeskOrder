using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones.Views.Components;

/// <summary>
/// Host panel for instance field UI. Three vertical sections:
/// header (instance name + undock/collapse), preview, field area.
/// Target DP drives the field-area rebuild: setting Target to a Zone
/// renders the Zone field tree; null clears it.
/// ponytail: field tree is built in code (not XAML) so per-target branching
/// stays a switch instead of dozens of UserControls with the same skeleton.
/// </summary>
public partial class PropertyPanel : UserControl
{
    public static readonly DependencyProperty InstanceNameProperty = DependencyProperty.Register(
        nameof(InstanceName), typeof(string), typeof(PropertyPanel), new PropertyMetadata(""));
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(object), typeof(PropertyPanel),
        new PropertyMetadata(null, (d, _) => ((PropertyPanel)d).OnTargetChanged()));

    public string InstanceName
    {
        get => (string)GetValue(InstanceNameProperty);
        set => SetValue(InstanceNameProperty, value);
    }
    public object? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>
    /// Optional sink for "save back to manager" calls. The page that owns
    /// the panel wires this up once; field controls call it after mutating
    /// the target. Null is fine — properties still mutate in-memory for
    /// preview, persistence just won't happen.
    /// </summary>
    public Action<object>? Persist { get; set; }

    public event EventHandler? UndockRequested;
    public event EventHandler? CollapseRequested;

    /// <summary>Cached host Window resolved once on Loaded. Dialogs read this before
    /// ShowDialog — falls back to a fresh Window.GetWindow lookup, then refuses to open
    /// if still null (avoids the InvalidOperationException that ShowDialog(owner:null)
    /// raises the moment the dialog tries to Activate).</summary>
    public Window? CachedOwner { get; private set; }

    public PropertyPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => CachedOwner = Window.GetWindow(this);
    }

    void UndockBtn_Click(object sender, RoutedEventArgs e) => UndockRequested?.Invoke(this, EventArgs.Empty);
    void CollapseBtn_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);

    void OnTargetChanged()
    {
        if (FieldScroller == null) return; // pre-InitializeComponent guard
        switch (Target)
        {
            case Zone z:
                InstanceName = z.Name;
                BuildZoneFields(z);
                break;
            default:
                InstanceName = "";
                FieldScroller.Content = new TextBlock
                {
                    Margin = new Thickness(16),
                    Text = Target == null ? "（未选中目标）" : "（此类型暂未实现字段）",
                    Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
                };
                break;
        }
        AnimateSwitch();
    }

    // ponytail: spec §4.3 — selection switch: Opacity 0→1 + TranslateX 12→0.
    // Runs on each Target change so clicks between rows replay the motion.
    void AnimateSwitch()
    {
        if (RootGrid == null) return;
        RootGrid.Opacity = 0;
        RootTranslate.X = 12;
        var fade = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1,
            Duration = (Duration)FindResource("Motion.Normal"),
        };
        var slide = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 12, To = 0,
            Duration = (Duration)FindResource("Motion.Normal"),
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
        RootTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    // ── Field tree for Zone ──
    //
    // Sections: 开关区 / 基本 / 标题栏 / 边框与填充 / 液态玻璃 / 背景图片
    // Each control wires back via the Save() closure — mutating the Zone
    // instance and then asking the host (Persist) to push it back to the
    // ZoneManager. Persist is set by the host page; if not wired, updates
    // are in-memory only.

    void BuildZoneFields(Zone z)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection("开关区");
        switches.Children.Add(MakeCheckRow("极简模式", z.QuickBarMode,
            v => { z.QuickBarMode = v; Save(z); }));
        var hoverRow = MakeCheckRowWithSideBtn("可恢复按钮", z.EnableRestoreButton,
            v => { z.EnableRestoreButton = v; Save(z); },
            "动效设置…", _ => OpenMotionDialog(z));
        switches.Children.Add(hoverRow);
        switches.Children.Add(MakeCheckRow("标题栏文字颜色自适应", z.TitleBarTextColorAdaptive,
            v => { z.TitleBarTextColorAdaptive = v; Save(z); }));
        switches.Children.Add(MakeCheckRow("主体内容颜色自适应", z.TextColorAdaptive,
            v => { z.TextColorAdaptive = v; Save(z); }));
        switches.Children.Add(MakeCheckRow("圆角", z.CornerRadius > 0,
            v => { z.CornerRadius = v ? (z.CornerRadius > 0 ? z.CornerRadius : 8) : 0; Save(z); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection("基本");
        basic.Children.Add(MakeTextRow("名称", z.Name,
            v => { z.Name = v ?? ""; Save(z); }));
        basic.Children.Add(MakeColorRow("名称颜色", z.TitleTextColor,
            v => { z.TitleTextColor = v; Save(z); }));
        basic.Children.Add(MakeTextRow("图标", z.IconChar,
            v => { z.IconChar = v ?? ""; Save(z); }, maxLen: 2));
        basic.Children.Add(MakeColorRow("图标颜色",
            string.IsNullOrEmpty(z.IconColor) ? "#FFFFFF" : z.IconColor,
            v => { z.IconColor = v; Save(z); }));

        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var widthGrid = MakeNumberSubBlock("宽度", z.Width, v => { z.Width = v; Save(z); });
        Grid.SetColumn(widthGrid, 0);
        sizeGrid.Children.Add(widthGrid);
        var heightGrid = MakeNumberSubBlock("高度", z.Height, v => { z.Height = v; Save(z); });
        Grid.SetColumn(heightGrid, 2);
        sizeGrid.Children.Add(heightGrid);
        basic.Children.Add(sizeGrid);

        var gridGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gridBlock = MakeNumberSubBlock("网格大小", z.GridSize,
            v => { z.GridSize = (int)v; Save(z); }, asInt: true);
        Grid.SetColumn(gridBlock, 0);
        gridGrid.Children.Add(gridBlock);
        var snapStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 18, 0, 4) };
        snapStack.Children.Add(MakeCheckRow("吸附到网格", z.SnapToGrid,
            v => { z.SnapToGrid = v; Save(z); }));
        snapStack.Children.Add(MakeCheckRow("尺寸变化时自动重排", z.AutoArrange,
            v => { z.AutoArrange = v; Save(z); }));
        Grid.SetColumn(snapStack, 2);
        gridGrid.Children.Add(snapStack);
        basic.Children.Add(gridGrid);

        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection("标题栏");
        tb.Children.Add(MakeColorRow("标题栏颜色", z.TitleBarFillColor,
            v => { z.TitleBarFillColor = v; Save(z); }));
        tb.Children.Add(MakeSliderRow("标题栏透明度", 0, 100, 5,
            ParsePercent(z.TitleBarFillColor, 6),
            p => { z.TitleBarFillColor = SetPercent(z.TitleBarFillColor, p, "FFFFFF"); Save(z); }));
        tb.Children.Add(MakeSliderRow("按钮透明度", 5, 100, 5,
            z.ControlOpacity,
            v => { z.ControlOpacity = v; Save(z); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection("边框与填充");
        bf.Children.Add(MakeTextRow("边框粗细", z.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { z.BorderThickness = d; Save(z); } }));
        bf.Children.Add(MakeColorRow("边框颜色", z.BorderColor,
            v => { z.BorderColor = v; Save(z); }));
        bf.Children.Add(MakeSliderRow("边框透明度", 0, 100, 5,
            ParsePercent(z.BorderColor, 25),
            p => { z.BorderColor = SetPercent(z.BorderColor, p, "FFFFFF"); Save(z); }));
        bf.Children.Add(MakeColorRow("内部填充颜色", z.FillColor,
            v => { z.FillColor = v; Save(z); }));
        bf.Children.Add(MakeSliderRow("填充透明度", 0, 100, 5,
            ParsePercent(z.FillColor, 8),
            p => { z.FillColor = SetPercent(z.FillColor, p, "000000"); Save(z); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection("液态玻璃");
        var lgRow = MakeCheckRowWithSideBtn("液态玻璃", z.EnableLiquidGlass,
            v => { z.EnableLiquidGlass = v; Save(z); },
            "液态玻璃设置…", _ => OpenLiquidGlassDialog(z));
        lg.Children.Add(lgRow);
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection("背景图片");
        bg.Children.Add(MakeBgImageRow(z));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Section + row builders ──

    StackPanel MakeSection(string title)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        section.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Brush.Border.Subtle"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        return section;
    }

    CheckBox MakeCheckRow(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            IsChecked = value,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
        };
        cb.SetValue(ContentProperty, label);
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    FrameworkElement MakeCheckRowWithSideBtn(string label, bool value,
        Action<bool> onChange, string btnText, Action<RoutedEventArgs> onBtnClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cb = new CheckBox
        {
            IsChecked = value,
            Margin = new Thickness(0, 2, 8, 2),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
        };
        cb.SetValue(ContentProperty, label);
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        Grid.SetColumn(cb, 0);
        grid.Children.Add(cb);
        var btn = new Button
        {
            Content = btnText,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
        };
        btn.Click += (_, e) => onBtnClick(e);
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    Grid MakeTextRow(string label, string value, Action<string?> onChange, int maxLen = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var tb = new TextBox
        {
            Text = value ?? "",
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
        };
        if (maxLen > 0) tb.MaxLength = maxLen;
        tb.LostFocus += (_, _) => onChange(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { onChange(tb.Text); Keyboard.ClearFocus(); } };
        Grid.SetRow(tb, 1);
        grid.Children.Add(tb);
        return grid;
    }

    Grid MakeColorRow(string label, string value, Action<string> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var swatch = new ColorSwatchButton { CurrentColor = value ?? "#00000000" };
        // ponytail: ColorSwatchButton doesn't expose a CLR change event; subscribe via
        // DependencyPropertyDescriptor so popup swatch clicks fire our callback.
        DependencyPropertyDescriptor.FromProperty(ColorSwatchButton.CurrentColorProperty, typeof(ColorSwatchButton))
            .AddValueChanged(swatch, (_, _) => onChange(swatch.CurrentColor));
        Grid.SetRow(swatch, 1);
        grid.Children.Add(swatch);
        return grid;
    }

    Grid MakeNumberSubBlock(string label, double value, Action<double> onChange, bool asInt = false)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var tb = new TextBox
        {
            Text = asInt
                ? ((int)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
        };
        tb.LostFocus += (_, _) => TryParse(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { TryParse(tb.Text); Keyboard.ClearFocus(); } };
        Grid.SetRow(tb, 1);
        grid.Children.Add(tb);
        return grid;

        void TryParse(string s)
        {
            if (asInt) { if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) onChange(i); }
            else { if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) onChange(d); }
        }
    }

    Grid MakeSliderRow(string label, double min, double max, double tick, double value, Action<double> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var slider = new SliderWithValue
        {
            Min = min,
            Max = max,
            Tick = tick,
            Value = value,
        };
        // ponytail: SliderWithValue has no ValueChanged event; subscribe via DP descriptor.
        DependencyPropertyDescriptor.FromProperty(SliderWithValue.ValueProperty, typeof(SliderWithValue))
            .AddValueChanged(slider, (_, _) => onChange(slider.Value));
        Grid.SetRow(slider, 1);
        grid.Children.Add(slider);
        return grid;
    }

    Grid MakeBgImageRow(Zone z)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new TextBox
        {
            Text = z.BackgroundImagePath ?? "",
            IsReadOnly = true,
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
        };
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);
        var browse = new Button
        {
            Content = "选图",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
        };
        browse.Click += (_, _) => OpenImagePicker(z, tb);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        var clear = new Button
        {
            Content = "清除",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
        };
        clear.Click += (_, _) => { z.BackgroundImagePath = ""; tb.Text = ""; Save(z); };
        Grid.SetColumn(clear, 2);
        grid.Children.Add(clear);
        return grid;
    }

    void Save(Zone z)
    {
        try { Persist?.Invoke(z); }
        catch (Exception ex)
        {
            // ponytail: surface to debug + show inline error dot in panel header (not a popup)
            System.Diagnostics.Debug.WriteLine($"[PropertyPanel] Persist failed: {ex}");
            ShowPersistError(true);
        }
    }

    void ShowPersistError(bool on)
    {
        if (PersistErrorDot != null) PersistErrorDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Color alpha helpers ──

    static double ParsePercent(string hex, double fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 3 || hex[0] != '#') return fallback;
        try
        {
            var a = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Math.Round(a / 255.0 * 100);
        }
        catch { return fallback; }
    }

    static string SetPercent(string hex, double percent, string fallbackRgb)
    {
        var rgb = (hex != null && hex.Length >= 7) ? hex.Substring(3) : fallbackRgb;
        var a = (int)Math.Clamp(Math.Round(percent / 100.0 * 255), 0, 255);
        return $"#{a:X2}{rgb}";
    }

    // ── Secondary window openers ──
    //
    // ponytail: each helper gets the host Window via Window.GetWindow(this) so it can
    // own the dialog (modal). Persist via Save(z) at the end so the property panel's
    // subscriber pushes the change back to ZoneManager and the live zone window
    // re-reads on ZonesChanged.

    void OpenMotionDialog(Zone z)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show("未找到宿主窗口"); return; }
        var dlg = new MotionSettingsDialog(z.HoverExpandAnimation, z.HoverExpandOrigin, z.HoverExpandSpeed)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true) return;
        z.HoverExpandOrigin = dlg.ResultHoverExpandOrigin;
        z.HoverExpandAnimation = dlg.ResultHoverExpandAnimation;
        z.HoverExpandSpeed = dlg.ResultHoverExpandSpeed;
        Save(z);
        // ponytail: 2026-08-21 — notify live HoverExpandBehavior instances so the
        // new kind/origin/speed take effect on the next expand/collapse. Without
        // this the live behaviour kept its ctor-time origin (ButtonCenter) and
        // ButtonCorner never took effect, and Scale=0 from the previous kind
        // leaked into the new one as a 36×36 ghost frame.
        z.RaiseHoverExpandSettingsChanged();
        // Re-build fields so the checkbox reflects any change to HoverAutoExpand.
        BuildZoneFields(z);
    }

    void OpenLiquidGlassDialog(Zone z)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show("未找到宿主窗口"); return; }
        int blur = z.GlassBlurAmount;
        int tint = z.GlassTintOpacity;
        int lum = z.GlassTintLuminosity;
        string mode = z.GlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, "液态玻璃设置",
            ref blur, ref tint, ref lum, ref mode, cn)) return;
        z.GlassBlurAmount = blur;
        z.GlassTintOpacity = tint;
        z.GlassTintLuminosity = lum;
        z.GlassColorMode = mode;
        Save(z);
    }

    void OpenImagePicker(Zone z, TextBox pathBox)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show("未找到宿主窗口"); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(owner) != true) return;

        // ponytail: ImageCropPreviewWindow owns drag / zoom / opacity. Write results
        // back to the AppearanceModel base fields; the live zone renders them.
        var crop = new ImageCropPreviewWindow(
            dlg.FileName,
            z.Width, z.Height,
            z.BgImageOffsetX, z.BgImageOffsetY,
            z.BgImageZoom, z.BackgroundImageOpacity)
        {
            Owner = owner
        };
        if (crop.ShowDialog() != true) return;

        z.BackgroundImagePath = dlg.FileName;
        if (crop.Result is { } r)
        {
            z.BgImageOffsetX = r.OffsetX;
            z.BgImageOffsetY = r.OffsetY;
            z.BgImageZoom = r.Zoom;
            z.BackgroundImageOpacity = r.Opacity;
        }
        if (pathBox != null) pathBox.Text = dlg.FileName;
        Save(z);
    }
}