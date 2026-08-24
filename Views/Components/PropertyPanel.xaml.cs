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

// ponytail: only the new "鼠标悬停自动展开" row is i18n-fetched; the rest of the
// rows still use hardcoded Chinese strings (ROADMAP v1.1 will migrate them in
// bulk — adding one now diverges from the surrounding style but is what the
// user explicitly asked for).

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
    public static readonly DependencyProperty IsFloatingProperty = DependencyProperty.Register(
        nameof(IsFloating), typeof(bool), typeof(PropertyPanel),
        new PropertyMetadata(false, (d, _) => ((PropertyPanel)d).OnIsFloatingChanged()));

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

    /// <summary>True when the panel lives in a floating PropertyWindow; false when
    /// docked in ManagementWindow's right column. Drives the dock/undock toggle
    /// button: docked = undock icon + UndockRequested; floating = dock icon +
    /// DockRequested. Toggled by the host (PropertyWindow sets true on show;
    /// ManagementWindow leaves default false).</summary>
    public bool IsFloating
    {
        get => (bool)GetValue(IsFloatingProperty);
        set => SetValue(IsFloatingProperty, value);
    }

    /// <summary>
    /// Optional sink for "save back to manager" calls. The page that owns
    /// the panel wires this up once; field controls call it after mutating
    /// the target. Null is fine — properties still mutate in-memory for
    /// preview, persistence just won't happen.
    /// </summary>
    public Action<object>? Persist { get; set; }

    public event EventHandler? UndockRequested;
    public event EventHandler? DockRequested;
    public event EventHandler? CollapseRequested;
    /// <summary>Host wires this to its PropertyTabStrip.CloseActiveTab().</summary>
    public event EventHandler? CloseTabRequested;

    /// <summary>True when there's an active tab to close. Host (Mgmt/PropertyWindow)
    /// binds this to the X button's Visibility.</summary>
    public static readonly DependencyProperty IsCloseableProperty = DependencyProperty.Register(
        nameof(IsCloseable), typeof(bool), typeof(PropertyPanel),
        new PropertyMetadata(false, (d, _) => ((PropertyPanel)d).OnIsCloseableChanged()));
    public bool IsCloseable
    {
        get => (bool)GetValue(IsCloseableProperty);
        set => SetValue(IsCloseableProperty, value);
    }

    // ponytail: i18n for the one row that uses it. Read on every rebuild so a
    // user-driven language switch reflects on the next Target change without us
    // having to subscribe to LocalizationService.LangChanged.
    readonly LocalizationService _loc = LocalizationService.Instance;

    /// <summary>Cached host Window resolved once on Loaded. Dialogs read this before
    /// ShowDialog — falls back to a fresh Window.GetWindow lookup, then refuses to open
    /// if still null (avoids the InvalidOperationException that ShowDialog(owner:null)
    /// raises the moment the dialog tries to Activate).</summary>
    public Window? CachedOwner { get; private set; }

    public PropertyPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => CachedOwner = Window.GetWindow(this);
        Unloaded += (_, _) => _loc.LanguageChanged -= OnLanguageChanged;
        _loc.LanguageChanged += OnLanguageChanged;
    }

    void OnLanguageChanged(string _) => OnTargetChanged();

    void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: same button serves both directions — when docked it pops out,
        // when floating it docks back. Routing is by IsFloating so the host
        // doesn't need to swap event handlers.
        if (IsFloating) DockRequested?.Invoke(this, EventArgs.Empty);
        else UndockRequested?.Invoke(this, EventArgs.Empty);
    }

    void CollapseBtn_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);

    void CloseTabBtn_Click(object sender, RoutedEventArgs e) => CloseTabRequested?.Invoke(this, EventArgs.Empty);

    void OnIsCloseableChanged()
    {
        if (CloseTabBtn != null)
            CloseTabBtn.Visibility = IsCloseable ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnIsFloatingChanged()
    {
        // ponytail: do NOT swap the icon here — the phased flip below swaps it
        // while the button is invisible (ScaleX=0 at the midpoint) so the
        // transition reads as "the button flipped over and is now the new one".
        if (ToggleBtn == null || ToggleIcon == null) return;
        ToggleBtn.ToolTip = IsFloating ? _loc["Common.Dock"] : _loc["Common.Undock"];
        AnimateToggleFlip();
    }

    void AnimateToggleFlip()
    {
        if (ToggleIconScale == null) return;
        // ponytail: true horizontal mirror via ScaleX. Two phases:
        //   1) collapse to ScaleX=0  (button "closes" like a clamshell)
        //   2) swap icon while invisible, then expand to target ±1
        // Phase 1 uses DecelSpline (ends slow — meets the icon swap); phase 2
        // uses AccentSpline (starts fast — opens up from the invisible seam).
        // NOTE: Motion.DecelSpline / Motion.AccentSpline are KeySpline values
        // (consumed by SplineDoubleKeyFrame), NOT IEasingFunction — casting
        // them to IEasingFunction throws InvalidCastException at runtime.
        // Only Motion.StandardSpline (a CubicEase EaseOut) is an IEasingFunction;
        // reuse it for both phases.
        var toFloating = IsFloating;
        var easing = (System.Windows.Media.Animation.IEasingFunction)FindResource("Motion.StandardSpline");
        var phase1 = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = easing,
        };
        phase1.Completed += (_, _) =>
        {
            if (ToggleIconScale == null) return;
            // Swap icon while invisible (ScaleX=0).
            ToggleIcon.Data = (System.Windows.Media.Geometry)FindResource(
                toFloating ? "Icon.Dock" : "Icon.Undock");
            var phase2 = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = toFloating ? -1.0 : 1.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = easing,
            };
            ToggleIconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, phase2);
        };
        ToggleIconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, phase1);
    }

    void OnTargetChanged()
    {
        if (FieldScroller == null) return; // pre-InitializeComponent guard
        switch (Target)
        {
            case Zone z:
                InstanceName = z.Name;
                SetInstanceIcon("Icon.Zones");
                BuildZoneFields(z);
                break;
            // ponytail 2026-08-25: Clock/Calendar/Note/Panel targets build their
            // own field trees per the per-component settings spec. Each builder
            // reuses MakeSection/MakeCheckRow/MakeColorRow/MakeTextRow/
            // MakeNumberSubBlock/MakeSliderRow/MakeCheckRowWithSideBtn from
            // BuildZoneFields, plus the shared dialogs (动效设置 / 液态玻璃) and
            // the BgImageBinding-based background-image row.
            // Header = icon + window name, matching the management list rows
            // and the tab strip titles (no "某某设置" labels).
            case DesktopClock c:
                InstanceName = $"Clock ({(c.Mode == ClockDisplayMode.Digital ? "数字" : "钟表")})";
                SetInstanceIcon("Icon.Clock");
                BuildClockFields(c);
                break;
            case DesktopCalendar cal:
                InstanceName = $"Calendar {cal.DisplayYear}-{cal.DisplayMonth:D2}";
                SetInstanceIcon("Icon.Calendar");
                BuildCalendarFields(cal);
                break;
            case StickyNote note:
                InstanceName = string.IsNullOrEmpty(note.Title) ? "便签" : note.Title;
                SetInstanceIcon("Icon.Sticky");
                BuildNoteFields(note);
                break;
            case PanelConfig p:
                InstanceName = "控制面板";
                SetInstanceIcon("Icon.Panel");
                BuildPanelFields(p);
                break;
            default:
                InstanceName = "";
                SetInstanceIcon(null);
                FieldScroller.Content = new TextBlock
                {
                    Margin = new Thickness(16),
                    Text = Target == null ? _loc["PropertyPanel.NoTarget"] : _loc["PropertyPanel.NotImplemented"],
                    Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
                };
                break;
        }
        AnimateSwitch();
    }

    /// <summary>Assign the header icon for the active target; pass null/unknown
    /// keys to collapse it (NoTarget / NotImplemented placeholders).</summary>
    void SetInstanceIcon(string? resourceKey)
    {
        if (InstanceIcon == null) return;
        if (string.IsNullOrEmpty(resourceKey) ||
            FindResource(resourceKey) is not System.Windows.Media.Geometry geometry)
        {
            InstanceIcon.Visibility = Visibility.Collapsed;
            return;
        }
        InstanceIcon.Data = geometry;
        InstanceIcon.Visibility = Visibility.Visible;
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
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.MinimalMode"], z.QuickBarMode,
            v => { z.QuickBarMode = v; Save(z); }));
        var hoverRow = MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], z.EnableRestoreButton,
            v => { z.EnableRestoreButton = v; Save(z); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(z, () => BuildZoneFields(z)));
        switches.Children.Add(hoverRow);
        // ponytail: hover-to-expand sub-toggle. Hidden when EnableRestoreButton
        // is off would be a bigger UX change; we just leave it always editable.
        // When EnableRestoreButton=false, EnableRestoreButton's own gate hides
        // the RestoreButton so the toggle has no observable effect — the user
        // can flip it ahead of time without anything bad happening.
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], z.HoverAutoExpand,
            v => { z.HoverAutoExpand = v; Save(z); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TitleBarTextAdaptive"], z.TitleBarTextColorAdaptive,
            v => { z.TitleBarTextColorAdaptive = v; Save(z); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.BodyTextAdaptive"], z.TextColorAdaptive,
            v => { z.TextColorAdaptive = v; Save(z); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.RoundedCorners"], z.CornerRadius > 0,
            v => { z.CornerRadius = v ? (z.CornerRadius > 0 ? z.CornerRadius : 8) : 0; Save(z); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeTextRow(_loc["ZoneProp.Name"], z.Name,
            v => { z.Name = v ?? ""; Save(z); }));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.NameColor"], z.TitleTextColor,
            v => { z.TitleTextColor = v; Save(z); }));
        basic.Children.Add(MakeTextRow(_loc["ZoneProp.Icon"], z.IconChar,
            v => { z.IconChar = v ?? ""; Save(z); }, maxLen: 2));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.IconColor"],
            string.IsNullOrEmpty(z.IconColor) ? "#FFFFFF" : z.IconColor,
            v => { z.IconColor = v; Save(z); }));

        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var widthGrid = MakeNumberSubBlock(_loc["ZoneProp.Width"], z.Width, v => { z.Width = v; Save(z); });
        Grid.SetColumn(widthGrid, 0);
        sizeGrid.Children.Add(widthGrid);
        var heightGrid = MakeNumberSubBlock(_loc["ZoneProp.Height"], z.Height, v => { z.Height = v; Save(z); });
        Grid.SetColumn(heightGrid, 2);
        sizeGrid.Children.Add(heightGrid);
        basic.Children.Add(sizeGrid);

        var gridGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gridBlock = MakeNumberSubBlock(_loc["ZoneProp.GridSize"], z.GridSize,
            v => { z.GridSize = (int)v; Save(z); }, asInt: true);
        Grid.SetColumn(gridBlock, 0);
        gridGrid.Children.Add(gridBlock);
        var snapStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 18, 0, 4) };
        snapStack.Children.Add(MakeCheckRow(_loc["ZoneProp.SnapToGrid"], z.SnapToGrid,
            v => { z.SnapToGrid = v; Save(z); }));
        snapStack.Children.Add(MakeCheckRow(_loc["ZoneProp.AutoArrange"], z.AutoArrange,
            v => { z.AutoArrange = v; Save(z); }));
        Grid.SetColumn(snapStack, 2);
        gridGrid.Children.Add(snapStack);
        basic.Children.Add(gridGrid);

        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], z.TitleBarFillColor,
            v => { z.TitleBarFillColor = v; Save(z); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(z.TitleBarFillColor, 6),
            p => { z.TitleBarFillColor = SetPercent(z.TitleBarFillColor, p, "FFFFFF"); Save(z); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            z.ControlOpacity,
            v => { z.ControlOpacity = v; Save(z); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], z.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { z.BorderThickness = d; Save(z); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], z.BorderColor,
            v => { z.BorderColor = v; Save(z); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(z.BorderColor, 25),
            p => { z.BorderColor = SetPercent(z.BorderColor, p, "FFFFFF"); Save(z); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], z.FillColor,
            v => { z.FillColor = v; Save(z); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(z.FillColor, 8),
            p => { z.FillColor = SetPercent(z.FillColor, p, "000000"); Save(z); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        var lgRow = MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], z.EnableLiquidGlass,
            v => { z.EnableLiquidGlass = v; Save(z); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(z));
        lg.Children.Add(lgRow);
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => z.BackgroundImagePath,
            SetPath = v => z.BackgroundImagePath = v ?? "",
            GetOpacity = () => z.BackgroundImageOpacity,
            SetOpacity = v => z.BackgroundImageOpacity = v,
            GetZoom = () => z.BgImageZoom,
            SetZoom = v => z.BgImageZoom = v,
            GetOffsetX = () => z.BgImageOffsetX,
            SetOffsetX = v => z.BgImageOffsetX = v,
            GetOffsetY = () => z.BgImageOffsetY,
            SetOffsetY = v => z.BgImageOffsetY = v,
            Width = z.Width, Height = z.Height,
            OnSave = () => Save(z),
        }));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Field tree for DesktopClock ──
    //
    // ponytail 2026-08-25: rebuilt to the 时钟设置 spec (属性字段分类新.txt).
    // Sections mirror the Zone editor (same builders, same loc keys):
    // 开关区 / 基本 / 标题栏 / 边框与填充 / 液态玻璃 / 背景图片.
    void BuildClockFields(DesktopClock c)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow("极简模式", c.QuickBarMode,
            v => { c.QuickBarMode = v; Save(c); }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], c.EnableRestoreButton,
            v => { c.EnableRestoreButton = v; Save(c); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(c, () => BuildClockFields(c))));
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], c.HoverAutoExpand,
            v => { c.HoverAutoExpand = v; Save(c); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.BodyTextAdaptive"], c.TextColorAdaptive,
            v => { c.TextColorAdaptive = v; Save(c); }));
        switches.Children.Add(MakeCheckRow("24 小时制", c.Use24Hour,
            v => { c.Use24Hour = v; Save(c); }));
        switches.Children.Add(MakeCheckRow("显示秒", c.ShowSeconds,
            v => { c.ShowSeconds = v; Save(c); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeSizeGrid(
            c.Width, v => { c.Width = v; Save(c); },
            c.Height, v => { c.Height = v; Save(c); }));
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            c.ControlOpacity,
            v => { c.ControlOpacity = v; Save(c); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], c.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { c.BorderThickness = d; Save(c); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], c.BorderColor,
            v => { c.BorderColor = v; Save(c); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(c.BorderColor, 25),
            p => { c.BorderColor = SetPercent(c.BorderColor, p, "FFFFFF"); Save(c); }));
        bf.Children.Add(MakeColorRow("数字模式填充", c.DigitalFillColor,
            v => { c.DigitalFillColor = v; Save(c); }));
        bf.Children.Add(MakeSliderRow("数字模式填充透明度", 0, 100, 5,
            ParsePercent(c.DigitalFillColor, 8),
            p => { c.DigitalFillColor = SetPercent(c.DigitalFillColor, p, "000000"); Save(c); }));
        bf.Children.Add(MakeColorRow("钟表模式填充", c.AnalogFillColor,
            v => { c.AnalogFillColor = v; Save(c); }));
        bf.Children.Add(MakeSliderRow("钟表模式填充透明度", 0, 100, 5,
            ParsePercent(c.AnalogFillColor, 8),
            p => { c.AnalogFillColor = SetPercent(c.AnalogFillColor, p, "000000"); Save(c); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], c.EnableLiquidGlass,
            v => { c.EnableLiquidGlass = v; Save(c); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(c)));
        root.Children.Add(lg);

        // 背景图片 — 数字 / 钟表 two independent images
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("数字模式背景图片", new BgImageBinding
        {
            GetPath = () => c.DigitalBackgroundImagePath,
            SetPath = v => c.DigitalBackgroundImagePath = v ?? "",
            GetOpacity = () => c.DigitalBackgroundImageOpacity,
            SetOpacity = v => c.DigitalBackgroundImageOpacity = v,
            GetZoom = () => c.DigitalBgImageZoom,
            SetZoom = v => c.DigitalBgImageZoom = v,
            GetOffsetX = () => c.DigitalBgImageOffsetX,
            SetOffsetX = v => c.DigitalBgImageOffsetX = v,
            GetOffsetY = () => c.DigitalBgImageOffsetY,
            SetOffsetY = v => c.DigitalBgImageOffsetY = v,
            Width = c.Width, Height = c.Height,
            OnSave = () => Save(c),
        }));
        bg.Children.Add(MakeBgImageRow("钟表模式背景图片", new BgImageBinding
        {
            GetPath = () => c.BackgroundImagePath,
            SetPath = v => c.BackgroundImagePath = v ?? "",
            GetOpacity = () => c.BackgroundImageOpacity,
            SetOpacity = v => c.BackgroundImageOpacity = v,
            GetZoom = () => c.BgImageZoom,
            SetZoom = v => c.BgImageZoom = v,
            GetOffsetX = () => c.BgImageOffsetX,
            SetOffsetX = v => c.BgImageOffsetX = v,
            GetOffsetY = () => c.BgImageOffsetY,
            SetOffsetY = v => c.BgImageOffsetY = v,
            Width = c.Width, Height = c.Height,
            OnSave = () => Save(c),
        }));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Field tree for DesktopCalendar ──
    //
    // ponytail 2026-08-25: rebuilt to the 日历设置 spec (属性字段分类新.txt).
    // Same section structure as the Zone editor.
    void BuildCalendarFields(DesktopCalendar cal)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow("极简模式", cal.QuickBarMode,
            v => { cal.QuickBarMode = v; Save(cal); }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], cal.EnableRestoreButton,
            v => { cal.EnableRestoreButton = v; Save(cal); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(cal, () => BuildCalendarFields(cal))));
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], cal.HoverAutoExpand,
            v => { cal.HoverAutoExpand = v; Save(cal); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.BodyTextAdaptive"], cal.TextColorAdaptive,
            v => { cal.TextColorAdaptive = v; Save(cal); }));
        switches.Children.Add(MakeCheckRow("周一开头", cal.StartOnMonday,
            v => { cal.StartOnMonday = v; Save(cal); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeSizeGrid(
            cal.Width, v => { cal.Width = v; Save(cal); },
            cal.Height, v => { cal.Height = v; Save(cal); }));
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            cal.ControlOpacity,
            v => { cal.ControlOpacity = v; Save(cal); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], cal.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { cal.BorderThickness = d; Save(cal); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], cal.BorderColor,
            v => { cal.BorderColor = v; Save(cal); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(cal.BorderColor, 25),
            p => { cal.BorderColor = SetPercent(cal.BorderColor, p, "FFFFFF"); Save(cal); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], cal.FillColor,
            v => { cal.FillColor = v; Save(cal); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(cal.FillColor, 8),
            p => { cal.FillColor = SetPercent(cal.FillColor, p, "000000"); Save(cal); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], cal.EnableLiquidGlass,
            v => { cal.EnableLiquidGlass = v; Save(cal); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(cal)));
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => cal.BackgroundImagePath,
            SetPath = v => cal.BackgroundImagePath = v ?? "",
            GetOpacity = () => cal.BackgroundImageOpacity,
            SetOpacity = v => cal.BackgroundImageOpacity = v,
            GetZoom = () => cal.BgImageZoom,
            SetZoom = v => cal.BgImageZoom = v,
            GetOffsetX = () => cal.BgImageOffsetX,
            SetOffsetX = v => cal.BgImageOffsetX = v,
            GetOffsetY = () => cal.BgImageOffsetY,
            SetOffsetY = v => cal.BgImageOffsetY = v,
            Width = cal.Width, Height = cal.Height,
            OnSave = () => Save(cal),
        }));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Field tree for StickyNote ──
    //
    // ponytail 2026-08-25: rebuilt to the 便签设置 spec (属性字段分类新.txt).
    // Same section structure as the Zone editor.
    void BuildNoteFields(StickyNote note)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区（行为）
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow("置顶", note.PinnedTop,
            v => { note.PinnedTop = v; Save(note); }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], note.EnableRestoreButton,
            v => { note.EnableRestoreButton = v; Save(note); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(note, () => BuildNoteFields(note))));
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], note.HoverAutoExpand,
            v => { note.HoverAutoExpand = v; Save(note); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TitleBarTextAdaptive"], note.TitleBarTextColorAdaptive,
            v => { note.TitleBarTextColorAdaptive = v; Save(note); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.BodyTextAdaptive"], note.TextColorAdaptive,
            v => { note.TextColorAdaptive = v; Save(note); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeTextRow("便签名称", note.Title,
            v => { note.Title = v ?? ""; Save(note); }));
        basic.Children.Add(MakeColorRow("便签名称颜色", note.TitleTextColor,
            v => { note.TitleTextColor = v; Save(note); }));
        basic.Children.Add(MakeSizeGrid(
            note.Width, v => { note.Width = v; Save(note); },
            note.Height, v => { note.Height = v; Save(note); }));
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], note.TitleBarFillColor,
            v => { note.TitleBarFillColor = v; Save(note); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(note.TitleBarFillColor, 6),
            p => { note.TitleBarFillColor = SetPercent(note.TitleBarFillColor, p, "FFFFFF"); Save(note); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            note.ControlOpacity,
            v => { note.ControlOpacity = v; Save(note); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], note.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { note.BorderThickness = d; Save(note); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], note.BorderColor,
            v => { note.BorderColor = v; Save(note); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(note.BorderColor, 25),
            p => { note.BorderColor = SetPercent(note.BorderColor, p, "FFFFFF"); Save(note); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], note.FillColor,
            v => { note.FillColor = v; Save(note); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(note.FillColor, 8),
            p => { note.FillColor = SetPercent(note.FillColor, p, "000000"); Save(note); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], note.EnableLiquidGlass,
            v => { note.EnableLiquidGlass = v; Save(note); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(note)));
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => note.BackgroundImagePath,
            SetPath = v => note.BackgroundImagePath = v ?? "",
            GetOpacity = () => note.BackgroundImageOpacity,
            SetOpacity = v => note.BackgroundImageOpacity = v,
            GetZoom = () => note.BgImageZoom,
            SetZoom = v => note.BgImageZoom = v,
            GetOffsetX = () => note.BgImageOffsetX,
            SetOffsetX = v => note.BgImageOffsetX = v,
            GetOffsetY = () => note.BgImageOffsetY,
            SetOffsetY = v => note.BgImageOffsetY = v,
            Width = note.Width, Height = note.Height,
            OnSave = () => Save(note),
        }));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Field tree for PanelConfig ──
    //
    // ponytail 2026-08-25: rebuilt to the 面板设置 spec (属性字段分类新.txt).
    // Same section structure as the Zone editor.
    void BuildPanelFields(PanelConfig p)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TitleBarTextAdaptive"], p.PanelTitleBarTextColorAdaptive,
            v => { p.PanelTitleBarTextColorAdaptive = v; Save(p); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.BodyTextAdaptive"], p.PanelTextColorAdaptive,
            v => { p.PanelTextColorAdaptive = v; Save(p); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeSizeGrid(
            p.PanelWidth, v => { p.PanelWidth = v; Save(p); },
            p.PanelHeight, v => { p.PanelHeight = v; Save(p); }));
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], p.PanelTitleBarFillColor,
            v => { p.PanelTitleBarFillColor = v; Save(p); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(p.PanelTitleBarFillColor, 6),
            pct => { p.PanelTitleBarFillColor = SetPercent(p.PanelTitleBarFillColor, pct, "FFFFFF"); Save(p); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            p.PanelControlOpacity,
            v => { p.PanelControlOpacity = v; Save(p); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], p.PanelBorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { p.PanelBorderThickness = d; Save(p); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], p.PanelBorderColor,
            v => { p.PanelBorderColor = v; Save(p); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(p.PanelBorderColor, 25),
            pct => { p.PanelBorderColor = SetPercent(p.PanelBorderColor, pct, "FFFFFF"); Save(p); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], p.PanelFillColor,
            v => { p.PanelFillColor = v; Save(p); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(p.PanelFillColor, 8),
            pct => { p.PanelFillColor = SetPercent(p.PanelFillColor, pct, "000000"); Save(p); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], p.PanelEnableLiquidGlass,
            v => { p.PanelEnableLiquidGlass = v; Save(p); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenPanelGlassDialog(p)));
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => p.PanelBackgroundImagePath,
            SetPath = v => p.PanelBackgroundImagePath = v ?? "",
            GetOpacity = () => p.PanelBackgroundImageOpacity,
            SetOpacity = v => p.PanelBackgroundImageOpacity = v,
            GetZoom = () => p.PanelBgImageZoom,
            SetZoom = v => p.PanelBgImageZoom = v,
            GetOffsetX = () => p.PanelBgImageOffsetX,
            SetOffsetX = v => p.PanelBgImageOffsetX = v,
            GetOffsetY = () => p.PanelBgImageOffsetY,
            SetOffsetY = v => p.PanelBgImageOffsetY = v,
            Width = p.PanelWidth, Height = p.PanelHeight,
            OnSave = () => Save(p),
        }));
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

    Grid MakeSizeGrid(double width, Action<double> onWidth, double height, Action<double> onHeight)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wBlock = MakeNumberSubBlock(_loc["ZoneProp.Width"], width, onWidth);
        Grid.SetColumn(wBlock, 0);
        grid.Children.Add(wBlock);
        var hBlock = MakeNumberSubBlock(_loc["ZoneProp.Height"], height, onHeight);
        Grid.SetColumn(hBlock, 2);
        grid.Children.Add(hBlock);
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

    /// <summary>
    /// Model-agnostic background-image field binding. Get/set delegates let one
    /// row builder serve Zone / Clock (digital + analog) / Calendar / Note /
    /// Panel without per-model overloads. Width/Height feed the crop preview;
    /// OnSave pushes the edit back through the owning editor's Persist chain.
    /// </summary>
    sealed class BgImageBinding
    {
        public Func<string> GetPath = () => "";
        public Action<string> SetPath = _ => { };
        public Func<double> GetOpacity = () => 30;
        public Action<double> SetOpacity = _ => { };
        public Func<double> GetZoom = () => 1.0;
        public Action<double> SetZoom = _ => { };
        public Func<double> GetOffsetX = () => 0;
        public Action<double> SetOffsetX = _ => { };
        public Func<double> GetOffsetY = () => 0;
        public Action<double> SetOffsetY = _ => { };
        public double Width = 400;
        public double Height = 300;
        public Action OnSave = () => { };
    }

    Grid MakeBgImageRow(string label, BgImageBinding b)
    {
        var outer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = 0;
        if (!string.IsNullOrEmpty(label))
        {
            outer.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("Brush.Text.Secondary"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            row = 1;
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new TextBox
        {
            Text = b.GetPath() ?? "",
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
        browse.Click += (_, _) => OpenImagePicker(b, tb);
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
        clear.Click += (_, _) => { b.SetPath(""); tb.Text = ""; b.OnSave(); };
        Grid.SetColumn(clear, 2);
        grid.Children.Add(clear);
        Grid.SetRow(grid, row);
        outer.Children.Add(grid);
        return outer;
    }

    void Save(object target)
    {
        try { Persist?.Invoke(target); }
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

    void OpenMotionDialog(AppearanceModel m, Action rebuildFields)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show("未找到宿主窗口"); return; }
        var dlg = new MotionSettingsDialog(m.HoverExpandAnimation, m.HoverExpandOrigin, m.HoverExpandSpeed)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true) return;
        m.HoverExpandOrigin = dlg.ResultHoverExpandOrigin;
        m.HoverExpandAnimation = dlg.ResultHoverExpandAnimation;
        m.HoverExpandSpeed = dlg.ResultHoverExpandSpeed;
        Save(m);
        // ponytail: 2026-08-21 — notify live HoverExpandBehavior instances so the
        // new kind/origin/speed take effect on the next expand/collapse. Without
        // this the live behaviour kept its ctor-time origin (ButtonCenter) and
        // ButtonCorner never took effect, and Scale=0 from the previous kind
        // leaked into the new one as a 36×36 ghost frame.
        m.RaiseHoverExpandSettingsChanged();
        // Re-build fields so the checkbox reflects any change to HoverAutoExpand.
        rebuildFields();
    }

    void OpenLiquidGlassDialog(AppearanceModel m)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        int blur = m.GlassBlurAmount;
        int tint = m.GlassTintOpacity;
        int lum = m.GlassTintLuminosity;
        string mode = m.GlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, _loc["ZoneProp.Section.LiquidGlass"],
            ref blur, ref tint, ref lum, ref mode, cn)) return;
        m.GlassBlurAmount = blur;
        m.GlassTintOpacity = tint;
        m.GlassTintLuminosity = lum;
        m.GlassColorMode = mode;
        Save(m);
    }

    // ponytail 2026-08-25: PanelConfig is not an AppearanceModel — the panel
    // glass knobs live on the Panel POCO, so this opener mirrors the generic
    // one with Panel-prefixed fields.
    void OpenPanelGlassDialog(PanelConfig p)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        int blur = p.PanelGlassBlurAmount;
        int tint = p.PanelGlassTintOpacity;
        int lum = p.PanelGlassTintLuminosity;
        string mode = p.PanelGlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, _loc["ZoneProp.Section.LiquidGlass"],
            ref blur, ref tint, ref lum, ref mode, cn)) return;
        p.PanelGlassBlurAmount = blur;
        p.PanelGlassTintOpacity = tint;
        p.PanelGlassTintLuminosity = lum;
        p.PanelGlassColorMode = mode;
        Save(p);
    }

    void OpenImagePicker(BgImageBinding b, TextBox pathBox)
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
        // back through the binding; the owning editor's Persist pushes the change
        // to the live widget.
        var crop = new ImageCropPreviewWindow(
            dlg.FileName,
            b.Width, b.Height,
            b.GetOffsetX(), b.GetOffsetY(),
            b.GetZoom(), b.GetOpacity())
        {
            Owner = owner
        };
        if (crop.ShowDialog() != true) return;

        b.SetPath(dlg.FileName);
        if (crop.Result is { } r)
        {
            b.SetOffsetX(r.OffsetX);
            b.SetOffsetY(r.OffsetY);
            b.SetZoom(r.Zoom);
            b.SetOpacity(r.Opacity);
        }
        if (pathBox != null) pathBox.Text = dlg.FileName;
        b.OnSave();
    }
}