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
                BuildZoneFields(z);
                break;
            // ponytail 2026-08-24: Clock/Calendar/Note/Panel targets now build
            // their own field trees. Previously they fell through to default and
            // showed "暂未实现". Each builder reuses MakeSection/MakeCheckRow/
            // MakeColorRow/MakeTextRow/MakeNumberSubBlock/MakeSliderRow from
            // BuildZoneFields above. Background-image crop picker stays Zone-only
            // (MakeBgImageRow is Zone-typed); each live widget already exposes its
            // own bg-image UI, so editing it from the right panel is optional.
            case DesktopClock c:
                InstanceName = $"Clock · {c.Mode}";
                BuildClockFields(c);
                break;
            case DesktopCalendar cal:
                InstanceName = $"Calendar · {cal.DisplayYear}-{cal.DisplayMonth:D2}";
                BuildCalendarFields(cal);
                break;
            case StickyNote note:
                InstanceName = string.IsNullOrEmpty(note.Title) ? "便签" : note.Title;
                BuildNoteFields(note);
                break;
            case PanelConfig p:
                InstanceName = "控制面板";
                BuildPanelFields(p);
                break;
            default:
                InstanceName = "";
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
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(z));
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
        bg.Children.Add(MakeBgImageRow(z));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Field tree for DesktopClock ──
    //
    // ponytail 2026-08-24: Clock field tree. Sections 开关 / 基本 / 显示 / 外观.
    // Mode is not editable here — the live ClockWidget has its own context menu
    // for Digital↔Analog switching; rebuilding the panel on Mode change would
    // require the widget to re-init its visual tree, which is overkill for a
    // property-panel convenience editor.
    void BuildClockFields(DesktopClock c)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关
        var switches = MakeSection("开关");
        switches.Children.Add(MakeCheckRow("24 小时制", c.Use24Hour,
            v => { c.Use24Hour = v; Save(c); }));
        switches.Children.Add(MakeCheckRow("显示秒", c.ShowSeconds,
            v => { c.ShowSeconds = v; Save(c); }));
        switches.Children.Add(MakeCheckRow("显示日期", c.ShowDate,
            v => { c.ShowDate = v; Save(c); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection("基本");
        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wGrid = MakeNumberSubBlock("宽度", c.Width, v => { c.Width = v; Save(c); });
        Grid.SetColumn(wGrid, 0); sizeGrid.Children.Add(wGrid);
        var hGrid = MakeNumberSubBlock("高度", c.Height, v => { c.Height = v; Save(c); });
        Grid.SetColumn(hGrid, 2); sizeGrid.Children.Add(hGrid);
        basic.Children.Add(sizeGrid);

        var xyGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var xGrid = MakeNumberSubBlock("X", c.X, v => { c.X = v; Save(c); });
        Grid.SetColumn(xGrid, 0); xyGrid.Children.Add(xGrid);
        var yGrid = MakeNumberSubBlock("Y", c.Y, v => { c.Y = v; Save(c); });
        Grid.SetColumn(yGrid, 2); xyGrid.Children.Add(yGrid);
        basic.Children.Add(xyGrid);
        root.Children.Add(basic);

        // 显示
        var display = MakeSection("显示");
        display.Children.Add(MakeTextRow("字体", c.FontFamily,
            v => { c.FontFamily = string.IsNullOrEmpty(v) ? "Segoe UI" : v; Save(c); }));
        display.Children.Add(MakeNumberSubBlock("字号", c.FontSize,
            v => { c.FontSize = v; Save(c); }, asInt: true));
        display.Children.Add(MakeColorRow("文字颜色", c.TextColor,
            v => { c.TextColor = v; Save(c); }));
        display.Children.Add(MakeColorRow("强调色", c.AccentColor,
            v => { c.AccentColor = v; Save(c); }));
        root.Children.Add(display);

        // 外观
        var appearance = MakeSection("外观");
        appearance.Children.Add(MakeCheckRow("使用全局外观", c.UseGlobalAppearance,
            v => { c.UseGlobalAppearance = v; Save(c); }));
        appearance.Children.Add(MakeTextRow("边框粗细", c.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { c.BorderThickness = d; Save(c); } }));
        appearance.Children.Add(MakeColorRow("数字模式填充", c.DigitalFillColor,
            v => { c.DigitalFillColor = v; Save(c); }));
        appearance.Children.Add(MakeColorRow("指针模式填充", c.AnalogFillColor,
            v => { c.AnalogFillColor = v; Save(c); }));
        appearance.Children.Add(MakeSliderRow("不透明度", 0, 100, 5,
            Math.Round(c.Opacity * 100),
            v => { c.Opacity = v / 100.0; Save(c); }));
        root.Children.Add(appearance);

        FieldScroller.Content = root;
    }

    // ── Field tree for DesktopCalendar ──
    void BuildCalendarFields(DesktopCalendar cal)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关
        var switches = MakeSection("开关");
        switches.Children.Add(MakeCheckRow("显示周数", cal.ShowWeekNumbers,
            v => { cal.ShowWeekNumbers = v; Save(cal); }));
        switches.Children.Add(MakeCheckRow("周一开头", cal.StartOnMonday,
            v => { cal.StartOnMonday = v; Save(cal); }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection("基本");
        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wGrid = MakeNumberSubBlock("宽度", cal.Width, v => { cal.Width = v; Save(cal); });
        Grid.SetColumn(wGrid, 0); sizeGrid.Children.Add(wGrid);
        var hGrid = MakeNumberSubBlock("高度", cal.Height, v => { cal.Height = v; Save(cal); });
        Grid.SetColumn(hGrid, 2); sizeGrid.Children.Add(hGrid);
        basic.Children.Add(sizeGrid);

        var xyGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var xGrid = MakeNumberSubBlock("X", cal.X, v => { cal.X = v; Save(cal); });
        Grid.SetColumn(xGrid, 0); xyGrid.Children.Add(xGrid);
        var yGrid = MakeNumberSubBlock("Y", cal.Y, v => { cal.Y = v; Save(cal); });
        Grid.SetColumn(yGrid, 2); xyGrid.Children.Add(yGrid);
        basic.Children.Add(xyGrid);
        root.Children.Add(basic);

        // 显示
        var display = MakeSection("显示");
        display.Children.Add(MakeNumberSubBlock("字号", cal.FontSize,
            v => { cal.FontSize = v; Save(cal); }, asInt: true));
        display.Children.Add(MakeColorRow("文字颜色", cal.TextColor,
            v => { cal.TextColor = v; Save(cal); }));
        display.Children.Add(MakeColorRow("今天高亮", cal.TodayColor,
            v => { cal.TodayColor = v; Save(cal); }));
        root.Children.Add(display);

        // 外观
        var appearance = MakeSection("外观");
        appearance.Children.Add(MakeCheckRow("使用全局外观", cal.UseGlobalAppearance,
            v => { cal.UseGlobalAppearance = v; Save(cal); }));
        appearance.Children.Add(MakeTextRow("边框粗细", cal.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { cal.BorderThickness = d; Save(cal); } }));
        appearance.Children.Add(MakeSliderRow("不透明度", 0, 100, 5,
            Math.Round(cal.Opacity * 100),
            v => { cal.Opacity = v / 100.0; Save(cal); }));
        root.Children.Add(appearance);

        FieldScroller.Content = root;
    }

    // ── Field tree for StickyNote ──
    void BuildNoteFields(StickyNote note)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 行为
        var behavior = MakeSection("行为");
        behavior.Children.Add(MakeCheckRow("置顶", note.PinnedTop,
            v => { note.PinnedTop = v; Save(note); }));
        behavior.Children.Add(MakeCheckRow("标题文字自适应", note.TitleBarTextColorAdaptive,
            v => { note.TitleBarTextColorAdaptive = v; Save(note); }));
        behavior.Children.Add(MakeCheckRow("正文文字自适应", note.TextColorAdaptive,
            v => { note.TextColorAdaptive = v; Save(note); }));
        root.Children.Add(behavior);

        // 基本
        var basic = MakeSection("基本");
        basic.Children.Add(MakeTextRow("标题", note.Title,
            v => { note.Title = v ?? ""; Save(note); }));
        basic.Children.Add(MakeNumberSubBlock("字号", note.FontSize,
            v => { note.FontSize = v; Save(note); }, asInt: true));
        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wGrid = MakeNumberSubBlock("宽度", note.Width, v => { note.Width = v; Save(note); });
        Grid.SetColumn(wGrid, 0); sizeGrid.Children.Add(wGrid);
        var hGrid = MakeNumberSubBlock("高度", note.Height, v => { note.Height = v; Save(note); });
        Grid.SetColumn(hGrid, 2); sizeGrid.Children.Add(hGrid);
        basic.Children.Add(sizeGrid);
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection("标题栏");
        tb.Children.Add(MakeColorRow("标题栏填充", note.TitleBarFillColor,
            v => { note.TitleBarFillColor = v; Save(note); }));
        tb.Children.Add(MakeColorRow("标题文字颜色", note.TitleTextColor,
            v => { note.TitleTextColor = v; Save(note); }));
        tb.Children.Add(MakeSliderRow("控件不透明度", 5, 100, 5,
            note.ControlOpacity,
            v => { note.ControlOpacity = v; Save(note); }));
        root.Children.Add(tb);

        // 外观
        var appearance = MakeSection("外观");
        appearance.Children.Add(MakeCheckRow("使用全局外观", note.UseGlobalAppearance,
            v => { note.UseGlobalAppearance = v; Save(note); }));
        appearance.Children.Add(MakeTextRow("边框粗细", note.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { note.BorderThickness = d; Save(note); } }));
        root.Children.Add(appearance);

        FieldScroller.Content = root;
    }

    // ── Field tree for PanelConfig ──
    void BuildPanelFields(PanelConfig p)
    {
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 基本
        var basic = MakeSection("基本");
        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wGrid = MakeNumberSubBlock("宽度", p.PanelWidth, v => { p.PanelWidth = v; Save(p); });
        Grid.SetColumn(wGrid, 0); sizeGrid.Children.Add(wGrid);
        var hGrid = MakeNumberSubBlock("高度", p.PanelHeight, v => { p.PanelHeight = v; Save(p); });
        Grid.SetColumn(hGrid, 2); sizeGrid.Children.Add(hGrid);
        basic.Children.Add(sizeGrid);

        var xyGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        xyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var xGrid = MakeNumberSubBlock("X", p.PanelX, v => { p.PanelX = v; Save(p); });
        Grid.SetColumn(xGrid, 0); xyGrid.Children.Add(xGrid);
        var yGrid = MakeNumberSubBlock("Y", p.PanelY, v => { p.PanelY = v; Save(p); });
        Grid.SetColumn(yGrid, 2); xyGrid.Children.Add(yGrid);
        basic.Children.Add(xyGrid);

        basic.Children.Add(MakeSliderRow("悬停展开速度", 0.1, 3.0, 0.1,
            p.PanelHoverExpandSpeed,
            v => { p.PanelHoverExpandSpeed = v; Save(p); }));
        root.Children.Add(basic);

        // 外观
        var appearance = MakeSection("外观");
        appearance.Children.Add(MakeCheckRow("使用全局外观", p.PanelUseGlobalAppearance,
            v => { p.PanelUseGlobalAppearance = v; Save(p); }));
        appearance.Children.Add(MakeCheckRow("标题文字自适应", p.PanelTitleBarTextColorAdaptive,
            v => { p.PanelTitleBarTextColorAdaptive = v; Save(p); }));
        appearance.Children.Add(MakeCheckRow("正文文字自适应", p.PanelTextColorAdaptive,
            v => { p.PanelTextColorAdaptive = v; Save(p); }));
        appearance.Children.Add(MakeColorRow("标题栏填充", p.PanelTitleBarFillColor,
            v => { p.PanelTitleBarFillColor = v; Save(p); }));
        appearance.Children.Add(MakeColorRow("填充颜色", p.PanelFillColor,
            v => { p.PanelFillColor = v; Save(p); }));
        appearance.Children.Add(MakeColorRow("边框颜色", p.PanelBorderColor,
            v => { p.PanelBorderColor = v; Save(p); }));
        appearance.Children.Add(MakeSliderRow("控件不透明度", 5, 100, 5,
            p.PanelControlOpacity,
            v => { p.PanelControlOpacity = v; Save(p); }));
        root.Children.Add(appearance);

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
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        int blur = z.GlassBlurAmount;
        int tint = z.GlassTintOpacity;
        int lum = z.GlassTintLuminosity;
        string mode = z.GlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, _loc["ZoneProp.Section.LiquidGlass"],
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