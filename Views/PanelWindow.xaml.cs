using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using Microsoft.Win32;

namespace DesktopZones.Views;

public partial class PanelWindow : Window
{
    // Resize
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    const uint WM_NCLBUTTONDOWN = 0x00A1;
    const int HTTOPLEFT = 13, HTTOPRIGHT = 14, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    // ponytail: frozen hover brushes — same color on every mouse-over.
    private static readonly SolidColorBrush CtrlHoverBrush = Freeze(new(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush CtrlIdleBrush  = Freeze(new(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush ItemHoverBrush = Freeze(new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)));
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private readonly ZoneManager _zoneManager;
    private readonly ConfigService _configService;
    private readonly ShellIconService _iconService = ShellIconService.Instance;
    private readonly LocalizationService _loc = LocalizationService.Instance;
    private readonly System.Windows.Threading.DispatcherTimer _clockTimer;
    private readonly System.Windows.Threading.DispatcherTimer _recycleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _recycleStateInit;
    private bool _recycleFullLast;
    private bool _isGridView = true;
    private Zone? _selectedZone;
    private Action<string>? _langChanged;

    // ── 移植自分区:拖动选中 + 次级文件夹浮层 ──
    private readonly HashSet<Guid> _selectedItemIds = new();
    private readonly Dictionary<Guid, (Border Card, Border Highlight, ZoneItem Item, Zone Zone)> _panelCards = new();
    private bool _marqueeActive;
    private bool _marqueeMoved;
    private Point _marqueeStart;
    private HashSet<Guid>? _marqueeStartSel;
    private System.Windows.Shapes.Rectangle? _marqueeRect;
    private Popup? _subfolderPopup;
    private SubfolderFlyout? _subfolderFlyout;
    // ponytail 2026-08-26: 与分区 OpenSubfolderFlyout 一致 — 关闭动画进行中点开新
    // SubFolder 时,token 递增;老 close onComplete 检 token 失配直接 return,避免
    // 把刚开的新 flyout 误关。_flyoutClosing 防重入(re-guard 关闭请求)。
    private bool _flyoutClosing;
    private int _flyoutOpenToken;

    // ── 面板弹出动画(从桌面角落滑到屏幕中央 + 展开/收起,与其他窗口共用 HoverExpandAnimationKind) ──
    private HoverExpandAnimationKind _popupAnimation = HoverExpandAnimationKind.ScaleExpand;
    private PanelPopupOrigin _popupOrigin = PanelPopupOrigin.BottomRight;
    private double _popupSpeed = 1.0;
    private double _popupSlideStartX;
    private double _popupSlideStartY;
    private bool _popupOpenStarted;
    private bool _popupCloseAnimating;
    private bool _popupAllowClose;

    // ponytail: 面板主体文字色（PanelTextColor）— 供 SubfolderItemView 名称绑定
    // (RelativeSource AncestorType=Window → ItemTextBrush) 使用，与分区端 ItemTextBrush 对称。
    public static readonly DependencyProperty ItemTextBrushProperty =
        DependencyProperty.Register(nameof(ItemTextBrush), typeof(Brush), typeof(PanelWindow),
            new PropertyMetadata(Brushes.White));
    public Brush ItemTextBrush
    {
        get => (Brush)GetValue(ItemTextBrushProperty);
        set => SetValue(ItemTextBrushProperty, value);
    }

    public PanelWindow(ZoneManager zoneManager, ConfigService configService)
    {
        InitializeComponent();
        _zoneManager = zoneManager;
        _configService = configService;

        var config = configService.Load();
        if (config.Panel.PanelX > 0 || config.Panel.PanelY > 0)
        {
            Left = config.Panel.PanelX; Top = config.Panel.PanelY;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - 400;
            Top = wa.Top + 60;
        }
        Width = config.Panel.PanelWidth > 200 ? config.Panel.PanelWidth : 800;
        Height = config.Panel.PanelHeight > 200 ? config.Panel.PanelHeight : 450;

        // 面板弹出动画配置(打开/关闭都从这里读;若为 None 则无动效)。
        _popupAnimation = config.Panel.PanelPopupMotion;
        _popupOrigin = config.Panel.PanelPopupOrigin;
        _popupSpeed = Math.Clamp(config.Panel.PanelPopupSpeed, 0.25, 2.0);
        ConfigurePopupSlide();
        ApplyPopupClosedVisual();

        _zoneManager.ZonesChanged += RebuildDisplay;
        Loaded += OnLoad;
        LocationChanged += SavePosition;
        Activated += (_, _) => { Topmost = true; };
        SizeChanged += (_, _) => { SavePosition(null, EventArgs.Empty); NativeMethods.UpdateRoundedCorners(this, _zoneManager.GetConfig().Panel.PanelCornerRadius); };
        _langChanged = _ => ApplyLoc();
        _loc.LanguageChanged += _langChanged;

        // Clock timer
        _clockTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        // Recycle-bin icon state watcher
        _recycleTimer.Tick += RecycleTimer_Tick;
        _recycleTimer.Start();
    }

    void ApplyLoc()
    {
        TitleText.Text = _loc["Panel.Title"];
        HideBtn.ToolTip = _loc["Panel.Hide"];
        SearchPlaceholder.Text = _loc["Panel.Search"];
        PopulateZoneSelector();
    }

    void UpdateClock()
    {
        if (ClockText == null || DateText == null) return;
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.ToString(_loc["Panel.DateFormat"]);
    }

    void PopulateZoneSelector()
    {
        if (ZoneSelector == null) return;
        var prevSelection = ZoneSelector.SelectedIndex;
        ZoneSelector.Items.Clear();
        ZoneSelector.Items.Add(_loc["Panel.AllZones"]);
        foreach (var zone in _zoneManager.Zones)
        {
            ZoneSelector.Items.Add(zone.Name);
        }
        if (prevSelection >= 0 && prevSelection < ZoneSelector.Items.Count)
            ZoneSelector.SelectedIndex = prevSelection;
        else
            ZoneSelector.SelectedIndex = 0;
    }

    void ZoneSelector_Changed(object s, SelectionChangedEventArgs e)
    {
        if (ZoneSelector == null || ZoneSelector.SelectedIndex < 0) return;
        if (ZoneSelector.SelectedIndex == 0)
            _selectedZone = null;
        else if (ZoneSelector.SelectedIndex - 1 < _zoneManager.Zones.Count)
            _selectedZone = _zoneManager.Zones[ZoneSelector.SelectedIndex - 1];
        RebuildDisplay();
    }

    void SearchBox_TextChanged(object s, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        RebuildDisplay();
    }

    void GridToggle_Click(object s, MouseButtonEventArgs e)
    {
        _isGridView = true;
        if (GridToggleBtn != null) GridToggleBtn.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        if (ListToggleBtn != null) ListToggleBtn.Background = Brushes.Transparent;
        RebuildDisplay();
    }

    void ListToggle_Click(object s, MouseButtonEventArgs e)
    {
        _isGridView = false;
        if (ListToggleBtn != null) ListToggleBtn.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        if (GridToggleBtn != null) GridToggleBtn.Background = Brushes.Transparent;
        RebuildDisplay();
    }

    void SettingsBtn_Click(object s, MouseButtonEventArgs e)
    {
        // ponytail: pass `this` so the popped-out panel anchors at the panel's
        // window position (offset 24,24) instead of jumping somewhere else.
        // Target is the live PanelConfig POCO (not AppConfig) so the property
        // editor dispatches to the 面板设置 field tree.
        // ponytail 2026-08-28: 按钮改按下触发 — 原来挂 MouseLeftButtonUp,按下冒泡到
        // TopBar 的 TitleBar_Drag → DragMove() 模态循环吞掉抬起,Up 事件永远不来,
        // 设置界面"点了没反应"。e.Handled=true 阻断冒泡,DragMove 不再抢这次交互。
        // 锚点走 MonitorHelper(物理光标 + 所在显示器 DPI),多屏混合 DPI 下不依赖
        // WPF PointToScreen 的错误坐标,贴 ⚙ 点击点弹出(避免历史 rect 罩住 ⚙)。
        var anchor = MonitorHelper.CursorDip();
        if (anchor is { } a)
            PropertyWindowService.OpenOrFocus(_zoneManager.GetConfig().Panel, this, a);
        else
            PropertyWindowService.OpenOrFocus(_zoneManager.GetConfig().Panel, this);
        e.Handled = true;
    }

    void OnLoad(object s, RoutedEventArgs e)
    {
        // Panel should stay above other windows - don't call PinToDesktop
        NativeMethods.SetToolWindow(this);
        ApplyAcrylic();
        ApplyStyle();
        ApplyBackgroundImage();
        ApplyLoc();
        RebuildDisplay();
        // Set rounded corners LAST after all sizing
        NativeMethods.SetRoundedCorners(this, _zoneManager.GetConfig().Panel.PanelCornerRadius);
        NativeMethods.UpdateRoundedCorners(this, _zoneManager.GetConfig().Panel.PanelCornerRadius);
        PlayPopupOpenAnimation();
    }

    void SavePosition(object? _, EventArgs __)
    {
        var config = _configService.Load();
        config.Panel.PanelX = Left;
        config.Panel.PanelY = Top;
        config.Panel.PanelWidth = Width;
        config.Panel.PanelHeight = Height;
        _configService.Save(config);
        _zoneManager.GetConfig().Panel.PanelX = Left;
        _zoneManager.GetConfig().Panel.PanelY = Top;
        _zoneManager.GetConfig().Panel.PanelWidth = Width;
        _zoneManager.GetConfig().Panel.PanelHeight = Height;
    }

    // ── Acrylic ──

    /// <summary>Refresh all visual styles. Optional <paramref name="cfg"/> is applied
    /// to the underlying config first; mirrors ZoneWindow.RefreshZone's reference-update
    /// pattern (see ClockWidget.RefreshAppearance for rationale).</summary>
    public void RefreshAppearance(Models.PanelPresetConfig? cfg = null)
    {
        if (cfg != null) cfg.ApplyTo(_zoneManager.GetConfig());
        ApplyAcrylic();
        ApplyStyle();
        ApplyBackgroundImage();
    }

    public void ApplyAcrylic()
    {
        var config = _zoneManager.GetConfig();
        string fillColorStr = config.Panel.PanelFillColor;

        if (config.Panel.PanelEnableLiquidGlass)
        {
            var blurResult = AcrylicHelper.EnableBlur(this, config.Panel.PanelGlassBlurAmount,
                config.Panel.PanelGlassTintOpacity, config.Panel.PanelGlassTintLuminosity,
                config.Panel.PanelGlassColorMode);
            if (!blurResult.Success)
                System.Diagnostics.Debug.WriteLine($"[PanelWindow] EnableBlur failed: {blurResult.Error}");
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
        }
    }

    public void ApplyStyle()
    {
        var config = _zoneManager.GetConfig();
        string fillColorStr = config.Panel.PanelFillColor;
        // Use PanelBorderColor directly (panel no longer follows a global appearance toggle).
        string borderColorStr = config.Panel.PanelBorderColor;
        double borderThickness = config.Panel.PanelBorderThickness;

        // Fill
        try
        {
            var fill = (Color)ColorConverter.ConvertFromString(fillColorStr)!;
            FillRect.Fill = new SolidColorBrush(fill);
            FillRect.Opacity = 1.0; // Brush alpha from FillColor controls transparency
        }
        catch { }
        bool fillIndependent = config.Panel.PanelTitleBarFillIndependent;
        int r = config.Panel.PanelCornerRadius;
        FillRect.RadiusX = FillRect.RadiusY = fillIndependent ? 0 : r;
        FillRect.Margin = fillIndependent ? new Thickness(0, 44, 0, 0) : new Thickness(0);

        // Border
        try
        {
            PanelBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColorStr)!);
        }
        catch { }
        PanelBorder.BorderThickness = new Thickness(borderThickness);

        // ponytail 2026-08-26: 圆角/尖角 switch — border / top-bar / DWM lockstep.
        PanelBorder.CornerRadius = new CornerRadius(r);
        TopBar.CornerRadius = new CornerRadius(r, r, 0, 0);
        if (System.Windows.PresentationSource.FromVisual(this) != null)
            NativeMethods.SetRoundedCorners(this, r);

        // Title bar fill
        try
        {
            var tbColor = (Color)ColorConverter.ConvertFromString(config.Panel.PanelTitleBarFillColor);
            TopBar.Background = new SolidColorBrush(tbColor);
        }
        catch { }

        // Top-bar content color — fixed 按钮颜色 (replaces the old title-bar adaptive).
        // Panel top bar = ClockText / DateText / TitleText + the 5 glyph buttons
        // (▦/≡/+/⚙/─). The search box keeps its own XAML defaults.
        SolidColorBrush topBarBrush;
        try { topBarBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(config.Panel.PanelButtonColor)!); }
        catch { topBarBrush = Brushes.White; }
        if (TitleText != null) TitleText.Foreground = topBarBrush;
        if (ClockText != null) ClockText.Foreground = topBarBrush;
        if (DateText != null) DateText.Foreground = topBarBrush;
        ApplyButtonGlyphForeground(GridToggleBtn, topBarBrush);
        ApplyButtonGlyphForeground(ListToggleBtn, topBarBrush);
        ApplyButtonGlyphForeground(ImportBtn, topBarBrush);
        ApplyButtonGlyphForeground(SettingsBtn, topBarBrush);
        ApplyButtonGlyphForeground(HideBtn, topBarBrush);

        // 按钮透明度 — top-bar control chrome rides PanelControlOpacity, Zone-style.
        var controlOpacity = Math.Max(0.05, config.Panel.PanelControlOpacity / 100.0);
        if (GridToggleBtn != null) GridToggleBtn.Opacity = controlOpacity;
        if (ListToggleBtn != null) ListToggleBtn.Opacity = controlOpacity;
        if (ImportBtn != null) ImportBtn.Opacity = controlOpacity;
        if (SettingsBtn != null) SettingsBtn.Opacity = controlOpacity;
        if (HideBtn != null) HideBtn.Opacity = controlOpacity;
    }

    /// <summary>Set the foreground of a top-bar glyph button (a <see cref="Border"/> whose
    /// single child is the glyph <see cref="TextBlock"/>).</summary>
    static void ApplyButtonGlyphForeground(Border? btn, Brush brush)
    {
        if (btn?.Child is TextBlock tb) tb.Foreground = brush;
    }

    /// <summary>Re-apply full style: cards are rebuilt with the fixed content-color brushes
    /// at creation time and the top bar is re-brushed by ApplyStyle, so a single ApplyStyle()
    /// + RebuildDisplay() covers the live-preview case from the settings dialog.</summary>
    public void RefreshTextColorAdaptive()
    {
        ApplyStyle();
        RebuildDisplay();
    }

    public void ApplyBackgroundImage()
    {
        var config = _zoneManager.GetConfig();
        // 标题栏独立填充：背景图与 FillRect 一样不铺到顶栏下方（顶部裁剪）。
        double clipTop = config.Panel.PanelTitleBarFillIndependent ? 44 : 0;
        if (BgImageBorder != null)
        {
            BgImageBorder.Margin = new Thickness(0, clipTop, 0, 0);
            // 顶部裁剪后上边角取直角，仅保留底部圆角以贴合窗口。
            int r = config.Panel.PanelCornerRadius;
            BgImageBorder.CornerRadius = clipTop > 0
                ? new CornerRadius(0, 0, r, r)
                : new CornerRadius(r);
        }
        try
        {
            if (!string.IsNullOrEmpty(config.Panel.PanelBackgroundImagePath) && System.IO.File.Exists(config.Panel.PanelBackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(config.Panel.PanelBackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                BgImage.Source = bi;
                BgImage.Stretch = Stretch.UniformToFill;

                double bw = Width;
                double bh = Height;

                // UniformToFill — fill target area maintaining aspect ratio
                double imgW = bi.PixelWidth;
                double imgH = bi.PixelHeight;
                double utfScale = Math.Max((bw * config.Panel.PanelBgImageZoom) / imgW, (bh * config.Panel.PanelBgImageZoom) / imgH);
                double displayedW = imgW * utfScale;
                double displayedH = imgH * utfScale;

                BgImage.Width = displayedW;
                BgImage.Height = displayedH;

                // Position image: center at zone center + offset (matches ZoneWindow logic)
                double zoneCenterX = bw / 2;
                double zoneCenterY = bh / 2;
                double imgCenterX = displayedW / 2;
                double imgCenterY = displayedH / 2;
                double ox = config.Panel.PanelBgImageOffsetX;
                double oy = config.Panel.PanelBgImageOffsetY;

                BgImage.Margin = new Thickness(
                    zoneCenterX - imgCenterX + ox,
                    zoneCenterY - imgCenterY + oy - clipTop, 0, 0);
                BgImage.HorizontalAlignment = HorizontalAlignment.Left;
                BgImage.VerticalAlignment = VerticalAlignment.Top;
                BgImage.Opacity = Math.Max(0.01, config.Panel.PanelBackgroundImageOpacity / 100.0);
            }
            else
            {
                BgImage.Source = null;
                BgImage.Opacity = 0;
            }
        }
        catch { if (BgImage != null) { BgImage.Source = null; BgImage.Opacity = 0; } }
    }

    // ── Build display ──

    public void RebuildDisplay()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (ContentStack == null) return;
                ContentStack.Children.Clear();
                _panelCards.Clear();
                _selectedItemIds.Clear();
                CloseSubfolderFlyoutIfOrphaned();

                string search = SearchBox?.Text?.Trim() ?? "";
                bool hasSearch = !string.IsNullOrEmpty(search);

                var zonesToShow = _selectedZone != null
                    ? new[] { _selectedZone }
                    : _zoneManager.Zones.ToArray();

                // ponytail: resolve the fixed content colors ONCE before creating cards, so
                // each card is built with the correct brush at creation time.
                var cfg = _zoneManager.GetConfig();
                Brush? titleBarBrush = null;
                Brush? bodyBrush = null;
                try { titleBarBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cfg.Panel.PanelButtonColor)!); } catch { }
                try { bodyBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(cfg.Panel.PanelTextColor)!); } catch { }
                // 供 SubfolderItemView 名称绑定同步主体文字色（网格视图次级分区名称）。
                ItemTextBrush = bodyBrush ?? Brushes.White;

                if (_isGridView)
                {
                    // Grid view: wrap items in a WrapPanel
                    var wrapPanel = new WrapPanel { Margin = new Thickness(4) };
                    foreach (var zone in zonesToShow)
                    {
                        foreach (var item in zone.Items.ToList())
                        {
                            if (hasSearch && !FuzzySearchHelper.MatchFuzzy(item.Name, search))
                                continue;
                            var card = CreateItemCard(item, zone, isGrid: true, bodyBrush: bodyBrush);
                            wrapPanel.Children.Add(card);
                        }
                    }
                    ContentStack.Children.Add(wrapPanel);
                }
                else
                {
                    // List view: vertical stack
                    foreach (var zone in zonesToShow)
                    {
                        foreach (var item in zone.Items.ToList())
                        {
                            if (hasSearch && !FuzzySearchHelper.MatchFuzzy(item.Name, search))
                                continue;
                            var card = CreateItemCard(item, zone, isGrid: false, bodyBrush: bodyBrush);
                            ContentStack.Children.Add(card);
                        }
                    }
                }
            }
            catch { }
        }), System.Windows.Threading.DispatcherPriority.Normal);
    }

    Border CreateZoneHeader(Zone zone, Brush? titleBarBrush)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 4, 0, 4),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1)
        };

        var stack = new StackPanel { Orientation = Orientation.Horizontal };

        // Zone icon
        var iconBorder = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
        // ponytail: zone header icon + name ride the fixed title-bar content color.
        // 空图标回退到软件原生「田字」分区图标（不再用 "⊞"）。
        var icon = string.IsNullOrEmpty(zone.IconChar) ? Helpers.IconGlyph.Zones : zone.IconChar;
        var iconEl = Helpers.IconGlyph.CreateIcon(icon, titleBarBrush ?? Brushes.White, fontSize: 13, pathSize: 13);
        if (iconEl != null)
        {
            iconEl.Margin = new Thickness(0);
            iconEl.HorizontalAlignment = HorizontalAlignment.Center;
            iconEl.VerticalAlignment = VerticalAlignment.Center;
            iconBorder.Child = iconEl;
        }
        stack.Children.Add(iconBorder);

        // Zone name
        var nameTb = new TextBlock
        {
            Text = zone.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (titleBarBrush != null) nameTb.Foreground = titleBarBrush;
        stack.Children.Add(nameTb);

        card.Child = stack;
        return card;
    }

    Border CreateItemCard(ZoneItem item, Zone zone, bool isGrid = true, Brush? bodyBrush = null)
    {
        if (item.Type == ItemType.SubFolder)
            return CreateSubfolderCard(item, zone, isGrid, bodyBrush);

        var card = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Tag = (item, zone)
        };

        card.MouseLeftButtonDown += Item_Click;
        card.MouseRightButtonDown += Item_RightClick;
        card.MouseEnter += Item_Enter;
        card.MouseLeave += Item_Leave;
        card.ContextMenu = CreateItemContextMenu(item, zone);

        FrameworkElement content;
        if (isGrid)
        {
            // Grid view: icon centered, name below
            card.Width = 80; card.Height = 80;
            card.Margin = new Thickness(4);
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var iconImg = new Image
            {
                Width = 40, Height = 40, Stretch = Stretch.Uniform,
                Source = ResolveItemIcon(item),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 4)
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            stack.Children.Add(iconImg);
            // ponytail: item name rides the fixed body content color.
            var nameTb = new TextBlock
            {
                Text = item.Name, FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 72
            };
            if (bodyBrush != null) nameTb.Foreground = bodyBrush;
            stack.Children.Add(nameTb);
            content = stack;
        }
        else
        {
            // List view: icon + name horizontal
            card.Margin = new Thickness(0, 2, 0, 2);
            card.Padding = new Thickness(8, 4, 8, 4);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var iconImg = new Image
            {
                Width = 20, Height = 20, Stretch = Stretch.Uniform,
                Source = ResolveItemIcon(item),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(iconImg, BitmapScalingMode.HighQuality);
            Grid.SetColumn(iconImg, 0);
            grid.Children.Add(iconImg);
            var nameTb = new TextBlock
            {
                Text = item.Name, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (bodyBrush != null) nameTb.Foreground = bodyBrush;
            Grid.SetColumn(nameTb, 1);
            grid.Children.Add(nameTb);
            content = grid;
        }

        AttachSelectionOverlay(card, content, item, zone);
        return card;
    }

    /// <summary>次级文件夹卡片:网格视图复用分区同款 2×2 缩略图控件,列表视图用
    /// 小型 2×2 预览 + 名称 + 内部项数量。双击弹出内部图标浮层。</summary>
    Border CreateSubfolderCard(ZoneItem item, Zone zone, bool isGrid, Brush? bodyBrush)
    {
        var card = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Cursor = Cursors.Hand,
            Tag = (item, zone)
        };
        card.MouseLeftButtonDown += Item_Click;
        card.MouseRightButtonDown += Item_RightClick;
        card.MouseEnter += Item_Enter;
        card.MouseLeave += Item_Leave;
        card.ContextMenu = CreateItemContextMenu(item, zone);

        FrameworkElement content;
        if (isGrid)
        {
            card.Width = 80; card.Height = 80;
            card.Margin = new Thickness(4);
            var sv = new SubfolderItemView
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            sv.SetSource(item, _iconService);
            content = sv;
        }
        else
        {
            card.Margin = new Thickness(0, 2, 0, 2);
            card.Padding = new Thickness(8, 4, 8, 4);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var box = BuildSubfolderPreview(item, 28, 12);
            box.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);

            var nameTb = new TextBlock
            {
                Text = item.Name, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 6, 0)
            };
            if (bodyBrush != null) nameTb.Foreground = bodyBrush;
            Grid.SetColumn(nameTb, 1);
            grid.Children.Add(nameTb);

            var countTb = new TextBlock
            {
                Text = item.SubItems.Count.ToString(),
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(countTb, 2);
            grid.Children.Add(countTb);

            content = grid;
        }

        AttachSelectionOverlay(card, content, item, zone);
        return card;
    }

    /// <summary>分区图片预览:图片文件显示内容缩略图(与分区 ZoneItemViewModel.Icon 同逻辑)。</summary>
    ImageSource? ResolveItemIcon(ZoneItem item)
    {
        if (ShellIconService.IsImageFile(item.TargetPath) && ShellIconService.ImagePreviewEnabled)
            return _iconService.GetImageThumbnail(item.TargetPath)
                ?? _iconService.GetIcon(item.TargetPath, item.Type, item.IconPath);
        return _iconService.GetIcon(item.TargetPath, item.Type, item.IconPath);
    }

    /// <summary>次级文件夹列表视图的小型 2×2 缩略图预览。</summary>
    FrameworkElement BuildSubfolderPreview(ZoneItem sub, double boxSize, double thumbSize)
    {
        var box = new Border
        {
            Width = boxSize, Height = boxSize,
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            ClipToBounds = true
        };
        var grid = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(3) };
        var inners = sub.SubItems.Take(4).ToList();
        for (int i = 0; i < 4; i++)
        {
            var slot = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1)
            };
            if (i < inners.Count)
            {
                var img = new Image
                {
                    Source = ResolveItemIcon(inners[i]),
                    Width = thumbSize, Height = thumbSize,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                slot.Child = img;
            }
            grid.Children.Add(slot);
        }
        box.Child = grid;
        return box;
    }

    /// <summary>给卡片包一层 Grid 并叠加选中高亮框(与分区 SelBox 同款视觉)。</summary>
    void AttachSelectionOverlay(Border card, FrameworkElement content, ZoneItem item, Zone zone)
    {
        var highlight = new Border
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(4)
        };
        var wrapper = new Grid();
        wrapper.Children.Add(content);
        wrapper.Children.Add(highlight);
        card.Child = wrapper;
        _panelCards[item.Id] = (card, highlight, item, zone);
    }

    ContextMenu CreateItemContextMenu(ZoneItem item, Zone zone)
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = _loc["Item.Open"] };
        openItem.Click += (_, _) =>
        {
            if (item.Type == ItemType.SubFolder)
            {
                if (_panelCards.TryGetValue(item.Id, out var entry))
                    OpenSubfolderFlyout(item, zone, entry.Card);
                return;
            }
            try { ShellLocationResolver.Open(item.TargetPath, item.Type); }
            catch (Exception ex)
            {
                MessageBox.Show($"{_loc["Item.FailedToOpen"]}\n{ex.Message}", _loc["Item.FailedToOpen.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        menu.Items.Add(openItem);

        // 次级文件夹没有"打开所在位置"。
        if (item.Type != ItemType.SubFolder)
        {
            var openLocation = new MenuItem { Header = _loc["Item.OpenLocation"] };
            openLocation.Click += (_, _) =>
            {
                if (item.Type == ItemType.ShellLocation)
                {
                    ShellLocationResolver.Open(item.TargetPath, item.Type);
                    return;
                }
                if (item.Type is ItemType.Shortcut or ItemType.Application)
                {
                    var d = Path.GetDirectoryName(item.TargetPath);
                    if (!string.IsNullOrEmpty(d))
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.TargetPath}\"");
                }
                else System.Diagnostics.Process.Start("explorer.exe", item.TargetPath);
            };
            menu.Items.Add(openLocation);
        }

        // Recycle Bin item: offer "Empty Recycle Bin" right in the context menu.
        if (item.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(item.TargetPath))
        {
            var emptyRecycle = new MenuItem { Header = _loc["Item.EmptyRecycleBin"] };
            emptyRecycle.Click += (_, _) =>
            {
                try
                {
                    NativeMethods.SHEmptyRecycleBinW(new WindowInteropHelper(this).Handle, null,
                        NativeMethods.SHERB_NOCONFIRMATION | NativeMethods.SHERB_NOPROGRESSUI | NativeMethods.SHERB_NOSOUND);
                }
                catch { }
                ShellIconService.InvalidateRecycleBinState();
                _recycleStateInit = false;
                RebuildDisplay();
            };
            menu.Items.Add(emptyRecycle);
        }

        var renameItem = new MenuItem { Header = _loc["Item.Rename"] };
        renameItem.Click += (_, _) =>
        {
            var rn = new RenameDialog(item.Name) { Owner = this };
            if (rn.ShowDialog() == true)
            {
                item.Name = rn.NewName;
                _zoneManager.SaveConfig();
                _zoneManager.NotifyChanged();
            }
        };
        menu.Items.Add(renameItem);

        // 次级文件夹支持"解散":图标移除,内部图标散入所属分区。
        if (item.Type == ItemType.SubFolder)
        {
            var dissolveItem = new MenuItem { Header = _loc["Subfolder.Dissolve"] };
            dissolveItem.Click += (_, _) => DissolvePanelSubfolder(item, zone);
            menu.Items.Add(dissolveItem);
        }

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = _loc["Item.Delete"], Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)) };
        deleteItem.Click += (_, _) => DeleteSelectedPanelItems();
        menu.Items.Add(deleteItem);

        return menu;
    }

    void Item_Click(object s, MouseButtonEventArgs e)
    {
        if (s is not Border b || b.Tag is not (ZoneItem item, Zone zone)) return;

        // ponytail 2026-08-26: 与分区一致 — SubFolder 单击开 flyout(SubFolder 没有可启动
        // 的 TargetPath,无双击语义),普通项仍双击打开。panel 内图标是静态布局,没有
        // 拖拽歧义(分区有 Item_MouseUp 区分单击/拖拽),不需要消歧。
        if (item.Type == ItemType.SubFolder)
        {
            // 单击次级分区也选中并高亮(与普通图标一致)。
            if (!_selectedItemIds.Contains(item.Id))
            {
                ClearSelection();
                SelectItem(item);
            }
            OpenSubfolderFlyout(item, zone, b);
            e.Handled = true;
            return;
        }
        if (e.ClickCount == 2)
        {
            try { ShellLocationResolver.Open(item.TargetPath, item.Type); }
            catch (Exception ex)
            {
                MessageBox.Show($"{_loc["Item.FailedToOpen"]}\n{ex.Message}", _loc["Item.FailedToOpen.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            e.Handled = true;
            return;
        }

        // Ctrl+点选切换多选;普通单击选中该项(已选中保持多选,资源管理器行为)。
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ToggleItemSelected(item);
        }
        else if (!_selectedItemIds.Contains(item.Id))
        {
            ClearSelection();
            SelectItem(item);
        }
    }

    void Item_RightClick(object s, MouseButtonEventArgs e)
    {
        if (s is not Border b || b.Tag is not (ZoneItem item, _)) return;
        // 右键未选中项时先单独选中;已选中则保持多选,让右键菜单作用于整个选中集。
        if (!_selectedItemIds.Contains(item.Id))
        {
            ClearSelection();
            SelectItem(item);
        }
        if (b.ContextMenu != null)
        {
            b.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    void Item_Enter(object s, MouseEventArgs e)
    {
        if (s is Border b)
            b.Background = ItemHoverBrush;
    }

    void Item_Leave(object s, MouseEventArgs e)
    {
        if (s is Border b)
            b.Background = Brushes.Transparent;
    }

    // ── 拖动选中 / 批量操作(移植自分区) ──

    void SelectItem(ZoneItem item)
    {
        if (_selectedItemIds.Add(item.Id))
            SetItemHighlight(item.Id, true);
    }

    void ToggleItemSelected(ZoneItem item)
    {
        if (_selectedItemIds.Contains(item.Id))
        {
            _selectedItemIds.Remove(item.Id);
            SetItemHighlight(item.Id, false);
        }
        else
        {
            SelectItem(item);
        }
    }

    void ClearSelection()
    {
        foreach (var id in _selectedItemIds)
            SetItemHighlight(id, false);
        _selectedItemIds.Clear();
    }

    void SelectAllPanelItems()
    {
        foreach (var id in _panelCards.Keys)
        {
            _selectedItemIds.Add(id);
            _panelCards[id].Highlight.Visibility = Visibility.Visible;
        }
    }

    void SetItemHighlight(Guid id, bool selected)
    {
        if (_panelCards.TryGetValue(id, out var entry))
            entry.Highlight.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }

    List<(ZoneItem Item, Zone Zone)> SelectedPanelItems()
    {
        var list = new List<(ZoneItem, Zone)>();
        foreach (var id in _selectedItemIds)
            if (_panelCards.TryGetValue(id, out var entry))
                list.Add((entry.Item, entry.Zone));
        return list;
    }

    void DeleteSelectedPanelItems()
    {
        var sel = SelectedPanelItems();
        if (sel.Count == 0) return;
        if (sel.Count > 1
            && MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        foreach (var (item, zone) in sel)
            zone.Items.Remove(item);
        ClearSelection();
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
    }

    /// <summary>解散次级文件夹:图标本身移除,内部图标自动排列回所属分区(与分区一致)。</summary>
    void DissolvePanelSubfolder(ZoneItem sub, Zone zone)
    {
        if (sub.Type != ItemType.SubFolder) return;
        zone.Items.Remove(sub);
        foreach (var inner in sub.SubItems)
        {
            var (sx, sy) = FindFreeSpot(zone);
            inner.X = sx;
            inner.Y = sy;
            zone.Items.Add(inner);
        }
        sub.SubItems.Clear();
        ClearSelection();
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
    }

    static bool IsOnPanelCard(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is Border b && b.Tag is (ZoneItem, Zone)) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ── 窗口级事件:框选跟踪 + 浮层关闭 + 键盘 ──

    void Panel_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // 点面板任意位置关闭次级文件夹浮层(点击浮层内部不会冒泡到主窗口)。
        CloseSubfolderFlyout();
    }

    void ContentArea_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        // 卡片按下交给 Item_Click / Item_RightClick 处理。
        if (IsOnPanelCard(e.OriginalSource)) return;

        _marqueeActive = true;
        _marqueeMoved = false;
        _marqueeStart = e.GetPosition(MarqueeLayer);
        _marqueeStartSel = new HashSet<Guid>(_selectedItemIds);
        try { Mouse.Capture(this); } catch { }
        e.Handled = true;
    }

    void Panel_MouseMove(object s, MouseEventArgs e)
    {
        if (!_marqueeActive) return;
        var p = e.GetPosition(MarqueeLayer);
        if (!_marqueeMoved)
        {
            if (Math.Abs(p.X - _marqueeStart.X) < 4 && Math.Abs(p.Y - _marqueeStart.Y) < 4) return;
            _marqueeMoved = true;
        }
        UpdatePanelMarquee(p);
    }

    void Panel_MouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        if (!_marqueeActive) return;
        try { Mouse.Capture(null); } catch { }
        bool moved = _marqueeMoved;
        _marqueeActive = false;
        _marqueeMoved = false;
        _marqueeStartSel = null;
        HidePanelMarquee();
        if (moved)
        {
            e.Handled = true;
            return;
        }
        // 空白处普通点击 → 清空选择(资源管理器行为)。
        ClearSelection();
    }

    void UpdatePanelMarquee(Point current)
    {
        double x1 = Math.Min(_marqueeStart.X, current.X);
        double y1 = Math.Min(_marqueeStart.Y, current.Y);
        double w = Math.Abs(current.X - _marqueeStart.X);
        double h = Math.Abs(current.Y - _marqueeStart.Y);
        var rect = EnsurePanelMarquee();
        rect.Visibility = Visibility.Visible;
        Canvas.SetLeft(rect, x1);
        Canvas.SetTop(rect, y1);
        rect.Width = w;
        rect.Height = h;
        var r = new Rect(x1, y1, w, h);
        foreach (var (id, entry) in _panelCards)
        {
            var p0 = entry.Card.TranslatePoint(new Point(0, 0), MarqueeLayer);
            bool inRect = r.IntersectsWith(new Rect(p0.X, p0.Y, Math.Max(1, entry.Card.ActualWidth), Math.Max(1, entry.Card.ActualHeight)));
            bool sel = inRect || (_marqueeStartSel?.Contains(id) ?? false);
            if (sel) _selectedItemIds.Add(id); else _selectedItemIds.Remove(id);
            entry.Highlight.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    System.Windows.Shapes.Rectangle EnsurePanelMarquee()
    {
        if (_marqueeRect == null)
        {
            _marqueeRect = new System.Windows.Shapes.Rectangle
            {
                RadiusX = 3,
                RadiusY = 3,
                StrokeThickness = 1,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
                Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0x8A, 0xB4, 0xF8)),
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x8A, 0xB4, 0xF8))
            };
            MarqueeLayer.Children.Add(_marqueeRect);
        }
        return _marqueeRect;
    }

    void HidePanelMarquee()
    {
        if (_marqueeRect != null) _marqueeRect.Visibility = Visibility.Collapsed;
    }

    void Panel_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return; // 搜索框保留自身按键
        // 次级文件夹浮层打开时,面板快捷键让位(浮层内部自有一份 Ctrl+A/Delete)。
        if (_subfolderPopup?.IsOpen == true) return;
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SelectAllPanelItems();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete)
        {
            DeleteSelectedPanelItems();
            e.Handled = true;
        }
    }

    // ── 次级文件夹浮层(ponytail 2026-08-26: 与分区 SubfolderFlyout 对齐 — 单击打开
    // + flyout 自身的 ResetToClosed / SetAnchor / AnimateOpen / AnimateClose 路径,
    // popup 用 AbsolutePoint + 手算 offset,锚点=图标中心,与分区同配方) ──

    void OpenSubfolderFlyout(ZoneItem sub, Zone zone, Border card)
    {
        // 与分区同款:已开同一个 SubFolder 且不在关闭动画中 → 关闭之;同 SubFolder
        // 关闭动画期间被再次点击 → 不重播打开动画(否则会卡一帧)。
        if (_subfolderPopup?.IsOpen == true
            && _subfolderFlyout?.ViewModel?.HostSubItem.Id == sub.Id
            && !_flyoutClosing)
        {
            CloseSubfolderFlyout();
            return;
        }
        var token = ++_flyoutOpenToken;
        _flyoutClosing = false;

        var flyout = new SubfolderFlyout
        {
            ViewModel = new SubfolderFlyoutViewModel(sub, _iconService, ResolvePanelSubfolderFill(zone, sub))
        };
        flyout.ItemOpenRequested += vm => OpenPanelItem(vm);
        flyout.ItemOpenLocationRequested += vm => OpenPanelItemLocation(vm);
        flyout.ItemRenameRequested += vm =>
        {
            var rn = new RenameDialog(vm.Name) { Owner = this };
            if (rn.ShowDialog() == true && !string.IsNullOrWhiteSpace(rn.NewName))
            {
                vm.Name = rn.NewName;
                _zoneManager.SaveConfig();
                _zoneManager.NotifyChanged();
            }
        };
        flyout.ItemDeleteRequested += vm => DeleteFlyoutItems(flyout, vm);
        flyout.ItemsChanged += () =>
        {
            _zoneManager.SaveConfig();
            _zoneManager.NotifyChanged();
        };
        flyout.EditStyleRequested += f =>
        {
            if (f.ViewModel?.HostSubItem is { } host)
            {
                (Application.Current as App)?.EnsureManagementWindow();
                // ponytail 2026-08-28: 贴 ⚙ 点击点弹出(同分区,避免历史 rect 罩住 ⚙)。
                if (f.StyleBtnScreenDip is { } anchor)
                    PropertyWindowService.OpenOrFocus(host, this, anchor);
                else
                    PropertyWindowService.OpenOrFocus(host, this);
            }
        };
        flyout.PreviewKeyDown += (_, ke) =>
        {
            if (TryHandleFlyoutKeys(flyout, ke)) ke.Handled = true;
        };
        flyout.ClickOutsideRequested += _ => CloseSubfolderFlyout();

        var popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Placement = PlacementMode.AbsolutePoint,
            Child = flyout
        };
        popup.Opened += (_, _) =>
        {
            // 关闭动画进行中 token 已变 → 新的 SubFolder 已接管,这里别再给旧 flyout 装玻璃。
            if (_flyoutOpenToken != token) return;
            if (flyout.ViewModel?.Fill is { } fill && fill.HasGlass)
                flyout.ViewModel.ShowGlassFallback = !flyout.TryApplyRealGlass(fill);
        };
        popup.Closed += (_, _) =>
        {
            flyout.DisableGlass();
            flyout.UnhookClickOutside();
            if (_flyoutOpenToken == token)
            {
                _subfolderPopup = null;
                _subfolderFlyout = null;
                _flyoutClosing = false;
            }
        };
        _subfolderPopup = popup;
        _subfolderFlyout = flyout;

        // 先复位到关闭态(scale 0,opacity 0)再开 popup,避免上次关闭残留的中间态一闪而过。
        flyout.ResetToClosed();
        popup.IsOpen = true;

        // 等布局完成(ActualWidth/Height 就绪)再算位置 + 设锚点 + 跑动画。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_flyoutOpenToken != token) return;
            if (flyout.ActualWidth <= 0 || flyout.ActualHeight <= 0)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_flyoutOpenToken != token) return;
                    OpenFlyoutAnimated(popup, flyout, card);
                }), System.Windows.Threading.DispatcherPriority.ContextIdle);
                return;
            }
            OpenFlyoutAnimated(popup, flyout, card);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
        flyout.ViewModel.IsOpen = true;
    }

    void OpenFlyoutAnimated(Popup popup, SubfolderFlyout flyout, Border card)
    {
        var (pos, c) = SubfolderFlyout.ComputePosAndAnchor(card, new Size(flyout.ActualWidth, flyout.ActualHeight));
        popup.HorizontalOffset = pos.X;
        popup.VerticalOffset = pos.Y;
        flyout.SetAnchor(c);
        flyout.HookClickOutside();
        // ponytail 2026-08-29: 圆角偏好同步到 Popup HWND(与 ZoneWindow 打开路径一致)。
        flyout.ApplyCornerPref();
        flyout.AnimateOpen();
    }

    void CloseSubfolderFlyout()
    {
        if (_subfolderPopup == null || _flyoutClosing) return;
        var popup = _subfolderPopup;
        var flyout = _subfolderFlyout;
        if (popup == null || flyout == null || !popup.IsOpen) return;
        _flyoutClosing = true;
        var token = _flyoutOpenToken;
        flyout.AnimateClose(() =>
        {
            // 关闭动画期间又点开了另一个 SubFolder(token 已变)→ 不要误关新开的 Flyout。
            if (_flyoutOpenToken != token) { _flyoutClosing = false; return; }
            // popup.Closed 会清掉 _subfolderPopup / _flyoutClosing 等状态。
            popup.IsOpen = false;
        });
    }

    /// <summary>仅当浮层所属的次级文件夹已从所有分区消失时才关闭浮层;内部图标
    /// 改名/删除等操作会触发 RebuildDisplay,浮层应保持打开(与分区行为一致)。</summary>
    void CloseSubfolderFlyoutIfOrphaned()
    {
        var host = _subfolderFlyout?.ViewModel?.HostSubItem;
        if (host == null) return;
        if (!_zoneManager.Zones.Any(z => z.Items.Contains(host)))
            CloseSubfolderFlyout();
    }

    /// <summary>ponytail 2026-08-26: 与分区端 ZoneWindow.ResolveSubfolderFill 对齐 —
    /// "跟随主分区"取 SubFolder 所属 Zone 的主体填充(填充色/背景图/液态玻璃),不含边框;
    /// 不跟随时取 SubFolder 自身 override 字段。面板自身的样式(背景色等)完全不参与 —
    /// 这里 zone 必须是 SubFolder 所在分区(承载面板的分区或独立关联分区),而不是面板
    /// 自身的属性。Panel 不渲染 merged-group 视觉态,所以直接读 zone 字段(不调用
    /// ResolveStyle()),与 Zone 端 Shape 一致。</summary>
    SubfolderFill ResolvePanelSubfolderFill(Zone zone, ZoneItem sub)
    {
        if (!sub.FillFollowsZone)
        {
            var f = SubfolderFill.FromOverride(sub);
            // ponytail 2026-08-29: 未设置 override 填充色时沿用主分区填充,避免 3% 默认
            // 透明让浮层"隐形"(与 ZoneWindow.ResolveSubfolderFill 一致)。
            if (string.IsNullOrEmpty(sub.FillColorOverride) && !string.IsNullOrEmpty(zone.FillColor))
                return f with { FillHex = zone.FillColor };
            return f;
        }
        return new SubfolderFill(
            zone.FillColor, 100,
            zone.BackgroundImagePath, zone.BackgroundImageOpacity,
            zone.EnableLiquidGlass ? zone.GlassColorMode : null,
            zone.GlassBlurAmount, zone.GlassTintOpacity, zone.GlassTintLuminosity);
    }

    void OpenPanelItem(ZoneItemViewModel vm)
    {
        try { ShellLocationResolver.Open(vm.TargetPath, vm.Type); }
        catch (Exception ex)
        {
            MessageBox.Show($"{_loc["Item.FailedToOpen"]}\n{ex.Message}", _loc["Item.FailedToOpen.Title"], MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OpenPanelItemLocation(ZoneItemViewModel v)
    {
        if (v.Type == ItemType.ShellLocation)
        {
            ShellLocationResolver.Open(v.TargetPath, v.Type);
            return;
        }
        if (v.Type is ItemType.Shortcut or ItemType.Application)
        {
            var d = Path.GetDirectoryName(v.TargetPath);
            if (!string.IsNullOrEmpty(d))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{v.TargetPath}\"");
        }
        else
        {
            System.Diagnostics.Process.Start("explorer.exe", v.TargetPath);
        }
    }

    void DeleteFlyoutItems(SubfolderFlyout flyout, ZoneItemViewModel vm)
    {
        var fvm = flyout.ViewModel;
        if (fvm == null) return;
        var sel = fvm.ItemVms.Where(i => i.IsSelected).ToList();
        if (sel.Count > 1 && sel.Contains(vm))
        {
            if (MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                    _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            foreach (var it in sel) fvm.ItemVms.Remove(it);
        }
        else
        {
            fvm.ItemVms.Remove(vm);
        }
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
    }

    bool TryHandleFlyoutKeys(SubfolderFlyout flyout, KeyEventArgs e)
    {
        var fvm = flyout.ViewModel;
        if (fvm == null) return false;
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            foreach (var it in fvm.ItemVms) it.IsSelected = true;
            return true;
        }
        if (e.Key == Key.Delete)
        {
            var sel = fvm.ItemVms.Where(i => i.IsSelected).ToList();
            if (sel.Count > 0)
            {
                if (sel.Count == 1
                    || MessageBox.Show(string.Format(_loc["ZoneItem.DeleteMultiConfirm"], sel.Count),
                        _loc["Item.Delete"], MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    foreach (var it in sel) fvm.ItemVms.Remove(it);
                    _zoneManager.SaveConfig();
                    _zoneManager.NotifyChanged();
                }
                return true;
            }
        }
        return false;
    }

    // ── New file/folder helpers ──

    void CreateNewFolder(Zone zone)
    {
        var displayBuf = IntPtr.Zero;
        var pidl = IntPtr.Zero;
        try
        {
            var h = new WindowInteropHelper(this); h.EnsureHandle();
            displayBuf = Marshal.AllocHGlobal(520);
            var bi = new NativeMethods.BROWSEINFOW
            {
                hwndOwner = h.Handle,
                pszDisplayName = displayBuf,
                lpszTitle = _loc["Dialog.SelectFolder"],
                ulFlags = 0x40
            };
            pidl = NativeMethods.SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (NativeMethods.SHGetPathFromIDListW(pidl, sb))
                {
                    string parentPath = sb.ToString();
                    string folderName = Microsoft.VisualBasic.Interaction.InputBox(
                        _loc["Dialog.FolderName"], _loc["Dialog.NewFolder"], _loc["Dialog.NewFolder"]);
                    if (!string.IsNullOrWhiteSpace(folderName))
                    {
                        string fullPath = Path.Combine(parentPath, folderName);
                        Directory.CreateDirectory(fullPath);
                        AddItemToZone(zone, Path.GetFileName(fullPath), fullPath, ItemType.Folder);
                    }
                }
            }
        }
        catch (Exception ex) { MessageBox.Show(string.Format(_loc["Dialog.ImportFailed"], ex.Message)); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
        }
    }

    void CreateNewFile(string defaultExt, string filter, Zone zone)
    {
        var d = new SaveFileDialog
        {
            Title = _loc["Dialog.CreateFile"],
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = "NewDocument" + defaultExt
        };
        if (d.ShowDialog() == true)
        {
            try
            {
                File.Create(d.FileName).Dispose();
                string name = Path.GetFileNameWithoutExtension(d.FileName);
                ItemType type = Path.GetExtension(d.FileName).ToLowerInvariant() switch
                {
                    ".lnk" => ItemType.Shortcut,
                    ".exe" => ItemType.Application,
                    ".txt" => ItemType.Document,
                    ".docx" => ItemType.Document,
                    ".pptx" => ItemType.Document,
                    ".xlsx" => ItemType.Document,
                    _ => ItemType.Shortcut
                };
                AddItemToZone(zone, name, d.FileName, type);
            }
            catch (Exception ex) { MessageBox.Show(string.Format(_loc["Dialog.ImportFailed"], ex.Message)); }
        }
    }

    void AddItemToZone(Zone zone, string name, string path, ItemType type, double x = 10, double y = 10)
    {
        // Imported shortcuts re-associate to their real target and keep their custom
        // icon location (see ShortcutResolver).
        (string target, ItemType t, string? iconLoc) = ShortcutResolver.NormalizeItem(path, type);
        var item = new ZoneItem(name, target, t, x, y) { IconPath = iconLoc };
        zone.Items.Add(item);
        _zoneManager.SaveConfig();
        _zoneManager.NotifyChanged();
        // ZonesChanged fires → RebuildDisplay + ZoneWindow refreshes
    }

    // ── Title bar drag ──

    // ── Content area right-click ──

    void ContentArea_RightClick(object s, MouseButtonEventArgs e)
    {
        // ponytail 2026-08-27: 与分区右键同款 5 段结构 — 导入 ▶ / 新建次级分区 /
        // 新建 ▶ / 设置 / 隐藏。面板无 FolderMapping 和删除项,故省略。
        // 父项 Import/New 不挂 Click,防双触发(同分区修复)。
        var contextMenu = new ContextMenu();

        var importItem = new MenuItem { Header = _loc["Panel.Import"] };
        var importFilesItem = new MenuItem { Header = _loc["Panel.ImportFiles"] };
        importFilesItem.Click += ImportFile_Click;
        importItem.Items.Add(importFilesItem);
        var importFolderItem = new MenuItem { Header = _loc["Panel.ImportFolder"] };
        importFolderItem.Click += ImportFolder_Click;
        importItem.Items.Add(importFolderItem);
        var importShellItem = new MenuItem { Header = _loc["Panel.ImportShellItems"] };
        importShellItem.Click += ImportShellItems_Click;
        importItem.Items.Add(importShellItem);
        contextMenu.Items.Add(importItem);

        contextMenu.Items.Add(new Separator());

        var newSubZoneItem = new MenuItem { Header = _loc["SubZone.New"] };
        newSubZoneItem.Click += NewSubfolder_Click;
        contextMenu.Items.Add(newSubZoneItem);

        var newItem = new MenuItem { Header = _loc["Panel.New"] };
        var newFolderItem = new MenuItem { Header = _loc["Panel.NewFolder"] };
        newFolderItem.Click += NewFolder_Click;
        newItem.Items.Add(newFolderItem);
        newItem.Items.Add(new Separator());
        var newTxtItem = new MenuItem { Header = _loc["Panel.NewTxt"] };
        newTxtItem.Click += NewTextFile_Click;
        newItem.Items.Add(newTxtItem);
        var newDocxItem = new MenuItem { Header = _loc["Panel.NewDocx"] };
        newDocxItem.Click += NewWordFile_Click;
        newItem.Items.Add(newDocxItem);
        var newPptxItem = new MenuItem { Header = _loc["Panel.NewPptx"] };
        newPptxItem.Click += NewPptFile_Click;
        newItem.Items.Add(newPptxItem);
        var newXlsxItem = new MenuItem { Header = _loc["Panel.NewXlsx"] };
        newXlsxItem.Click += NewExcelFile_Click;
        newItem.Items.Add(newXlsxItem);
        contextMenu.Items.Add(newItem);

        contextMenu.Items.Add(new Separator());

        // 设置 — 与分区端"设置"对齐(无"面板"前缀)。
        var settingsItem = new MenuItem { Header = _loc["Panel.Settings"] };
        settingsItem.Click += (_, _) => SettingsBtn_Click(null!, null!);
        contextMenu.Items.Add(settingsItem);

        // 隐藏面板 — 与分区端"最小化"对齐。
        var hideItem = new MenuItem { Header = _loc["Panel.Hide"] };
        hideItem.Click += (_, _) => HideButton_Click(null!, null!);
        contextMenu.Items.Add(hideItem);

        ContextMenu = contextMenu;
        contextMenu.IsOpen = true;
        e.Handled = true;
    }

    void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    // ── Resize ──

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g) return;
        // Determine direction based on horizontal/vertical alignment
        bool isLeft = g.HorizontalAlignment == HorizontalAlignment.Left;
        bool isTop = g.VerticalAlignment == VerticalAlignment.Top;
        int d = (isLeft, isTop) switch
        {
            (true, true) => HTTOPLEFT,
            (false, true) => HTTOPRIGHT,
            (true, false) => HTBOTTOMLEFT,
            _ => HTBOTTOMRIGHT
        };
        SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, (IntPtr)d, IntPtr.Zero);
        e.Handled = true;
    }

    // ── Import/New ──

    void ImportBtn_Click(object s, MouseButtonEventArgs e)
    {
        if (s is Border b)
        {
            b.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    void ImportFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;

        var d = new OpenFileDialog
        {
            Title = _loc["Zone.ImportTitle"],
            Filter = $"{_loc["Filter.All"]}|*.lnk;*.exe;*.*|{_loc["Filter.Lnk"]}|*.lnk|{_loc["Filter.Exe"]}|*.exe",
            Multiselect = true
        };
        if (d.ShowDialog() == true)
        {
            ImportFilesToZone(targetZone, d.FileNames);
        }
    }

    void ImportFolder_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;

        var displayBuf = IntPtr.Zero;
        var pidl = IntPtr.Zero;
        try
        {
            var h = new WindowInteropHelper(this); h.EnsureHandle();
            displayBuf = Marshal.AllocHGlobal(520);
            var bi = new NativeMethods.BROWSEINFOW
            {
                hwndOwner = h.Handle,
                pszDisplayName = displayBuf,
                lpszTitle = _loc["Dialog.SelectFolder"],
                ulFlags = 0x40
            };
            pidl = NativeMethods.SHBrowseForFolderW(ref bi);
            if (pidl != IntPtr.Zero)
            {
                var sb = new System.Text.StringBuilder(260);
                if (NativeMethods.SHGetPathFromIDListW(pidl, sb) && Directory.Exists(sb.ToString()))
                {
                    ImportFilesToZone(targetZone, new[] { sb.ToString() });
                }
            }
        }
        catch (Exception ex) { MessageBox.Show(string.Format(_loc["Dialog.ImportFailed"], ex.Message)); }
        finally
        {
            if (displayBuf != IntPtr.Zero) Marshal.FreeHGlobal(displayBuf);
            if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
        }
    }

    void ImportShellItems_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;

        var dlg = new ShellLocationPickerWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.SelectedItems.Count == 0) return;

        foreach (var (name, spec) in dlg.SelectedItems)
        {
            var (sx, sy) = FindFreeSpot(targetZone);
            // 已知文件夹(文档/图片/音乐/视频等)直接关联真实路径 — "::{GUID}" 壳
            // 无法被 shell 解析(打不开/空壳),转成真实 Folder 项。
            var folderPath = ShellLocationResolver.ResolveKnownFolderPath(spec);
            AddItemToZone(targetZone, name, folderPath ?? spec,
                folderPath != null ? ItemType.Folder : ItemType.ShellLocation, sx, sy);
        }
    }

    /// <summary>新建次级文件夹(与分区同款:命名弹窗 + ZoneManager.CreateSubfolder)。</summary>
    void NewSubfolder_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;

        var rn = new RenameDialog(_loc["Subfolder.NewDefault"], _loc["Subfolder.NewTitle"]) { Owner = this };
        if (rn.ShowDialog() != true) return;
        string name = rn.NewName.Trim();
        if (string.IsNullOrWhiteSpace(name)) return;

        _zoneManager.CreateSubfolder(targetZone, name);
    }

    void NewFolder_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFolder(targetZone);
    }    void NewTextFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".txt", $"{_loc["Filter.Txt"]}|*.txt", targetZone);
    }

    void NewWordFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".docx", $"{_loc["Filter.Docx"]}|*.docx", targetZone);
    }

    void NewPptFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".pptx", $"{_loc["Filter.Pptx"]}|*.pptx", targetZone);
    }

    void NewExcelFile_Click(object s, RoutedEventArgs e)
    {
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        CreateNewFile(".xlsx", $"{_loc["Filter.Xlsx"]}|*.xlsx", targetZone);
    }

    private Zone? GetTargetZone()
    {
        if (_selectedZone != null)
            return _selectedZone;

        // If showing all zones, show zone selector dialog
        if (_zoneManager.Zones.Count == 1)
            return _zoneManager.Zones[0];

        if (_zoneManager.Zones.Count > 1)
        {
            // Show zone selection dialog
            var dlg = new Window
            {
                Title = _loc["Dialog.SelectZone"],
                Width = 300, Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };

            var mainBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10)
            };

            var grid = new Grid { Margin = new Thickness(18) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title
            var titleText = new TextBlock
            {
                Text = _loc["Dialog.SelectTargetZone"],
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            Grid.SetRow(titleText, 0);
            grid.Children.Add(titleText);

            // Title/content divider (与液态玻璃二级窗口同款,用本窗口自己的半透明白配色)
            var separator = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 0, 10)
            };
            separator.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Menu.Separator");
            Grid.SetRow(separator, 1);
            grid.Children.Add(separator);

            // Zone list
            var listBox = new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xF0))
            };
            foreach (var zone in _zoneManager.Zones)
            {
                listBox.Items.Add(zone.Name);
            }
            listBox.SelectedIndex = 0;
            Grid.SetRow(listBox, 2);
            grid.Children.Add(listBox);

            // Buttons
            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            Zone? selectedZone = null;

            var cancelButton = new Button
            {
                Content = _loc["Common.Cancel"],
                Width = 60, Height = 28, FontSize = 11,
                Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xC0)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x25, 0xFF, 0xFF, 0xFF)),
                Margin = new Thickness(0, 0, 8, 0)
            };
            cancelButton.Click += (_, _) => dlg.Close();
            buttonPanel.Children.Add(cancelButton);

            var selectButton = new Button
            {
                Content = _loc["Dialog.Select"],
                Width = 60, Height = 28, FontSize = 11,
                Background = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            selectButton.Click += (_, _) =>
            {
                if (listBox.SelectedIndex >= 0)
                {
                    selectedZone = _zoneManager.Zones[listBox.SelectedIndex];
                }
                dlg.Close();
            };
            buttonPanel.Children.Add(selectButton);

            Grid.SetRow(buttonPanel, 3);
            grid.Children.Add(buttonPanel);

            mainBorder.Child = grid;
            dlg.Content = mainBorder;
            dlg.ShowDialog();

            return selectedZone;
        }

        return null;
    }

    private void ImportFilesToZone(Zone zone, string[] paths)
    {
        foreach (var f in paths)
        {
            string name = Path.GetFileName(f);
            ItemType type = File.GetAttributes(f).HasFlag(FileAttributes.Directory)
                ? ItemType.Folder
                : Path.GetExtension(f).ToLowerInvariant() switch
                {
                    ".lnk" => ItemType.Shortcut,
                    ".exe" => ItemType.Application,
                    _ => ItemType.Shortcut
                };
            var (sx, sy) = FindFreeSpot(zone);
            AddItemToZone(zone, name, f, type, sx, sy);
        }
    }

    private (double, double) FindFreeSpot(Zone zone)
        => ZoneLayout.FindFreeSpot(zone.Items, zone.Width, zone.Height, zone.GridSize, zone.GridSize);

    // ── Drag-drop from Explorer (WPF AllowDrop — files only) ──

    void Panel_DragEnter(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Panel_DragOver(object s, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.Copy; e.Handled = true; }
    }

    void Panel_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        var targetZone = GetTargetZone();
        if (targetZone == null) return;
        ImportFilesToZone(targetZone, files);
        e.Handled = true;
    }

    // ── Recycle Bin icon state (empty ⇄ full) ──

    void RecycleTimer_Tick(object? s, EventArgs e)
    {
        try
        {
            bool hasRecycle = false;
            foreach (var z in _zoneManager.Zones)
            {
                foreach (var i in z.Items)
                {
                    if (i.Type == ItemType.ShellLocation && ShellIconService.IsRecycleBin(i.TargetPath))
                    { hasRecycle = true; break; }
                }
                if (hasRecycle) break;
            }
            if (!hasRecycle) { _recycleStateInit = false; return; }

            bool full = ShellIconService.RecycleBinHasItems();
            if (_recycleStateInit && full == _recycleFullLast) return;
            _recycleStateInit = true;
            _recycleFullLast = full;
            RebuildDisplay();
        }
        catch { }
    }

    // ── 面板弹出动画(从桌面角落滑到屏幕中央 + 展开/收起,关闭时逆向) ──

    /// <summary>当前焦点显示器工作区,换算成 DIP(与 Left/Top/Width/Height 同坐标系)。
    /// GetMonitorInfo 返回物理像素,按面板窗口 DPI 换算(与 SubfolderFlyout 的约定一致)。</summary>
    Rect GetFocusedWorkAreaDip()
    {
        var waPx = MonitorHelper.FocusedWorkArea();
        double sx = 1, sy = 1;
        try { var d = VisualTreeHelper.GetDpi(this); sx = d.DpiScaleX; sy = d.DpiScaleY; } catch { }
        return new Rect(waPx.Left / sx, waPx.Top / sy, waPx.Width / sx, waPx.Height / sy);
    }

    /// <summary>把面板定位到当前焦点屏幕正中央(忽略历史拖动位置)。</summary>
    void CenterPanelOnFocusedScreen()
    {
        var wa = GetFocusedWorkAreaDip();
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + (wa.Height - Height) / 2;
    }

    /// <summary>计算「桌面角落 → 面板中心」的滑动位移量(写入 _popupSlideStartX/Y)。
    /// 以面板当前 Left/Top 的中心为终点,所选桌面角落为起点。</summary>
    void ConfigurePopupSlide()
    {
        var wa = GetFocusedWorkAreaDip();
        double ox, oy;
        switch (_popupOrigin)
        {
            case PanelPopupOrigin.TopLeft:
                ox = wa.Left; oy = wa.Top; break;
            case PanelPopupOrigin.TopRight:
                ox = wa.Right; oy = wa.Top; break;
            case PanelPopupOrigin.BottomLeft:
                ox = wa.Left; oy = wa.Bottom; break;
            default: // BottomRight
                ox = wa.Right; oy = wa.Bottom; break;
        }
        double centerX = Left + Width / 2;
        double centerY = Top + Height / 2;
        _popupSlideStartX = ox - centerX;
        _popupSlideStartY = oy - centerY;
    }

    /// <summary>把窗口复位到「关闭态」(打开动画的 from 帧)。在 Show 前调用,避免整窗闪一帧。</summary>
    void ApplyPopupClosedVisual()
    {
        if (RootGrid == null) return;
        StopPopupAnimations();

        switch (_popupAnimation)
        {
            case HoverExpandAnimationKind.None:
                ApplyPopupFinalVisual();
                return;
            case HoverExpandAnimationKind.Fade:
                PopupScale.ScaleX = 1; PopupScale.ScaleY = 1;
                PopupSlide.X = _popupSlideStartX; PopupSlide.Y = _popupSlideStartY;
                RootGrid.Opacity = 0;
                break;
            case HoverExpandAnimationKind.VerticalExpand:
                PopupScale.ScaleX = 1; PopupScale.ScaleY = 0;
                PopupSlide.X = _popupSlideStartX; PopupSlide.Y = _popupSlideStartY;
                RootGrid.Opacity = 1;
                break;
            case HoverExpandAnimationKind.DirectionalExpand:
                PopupScale.ScaleX = 0; PopupScale.ScaleY = 1;
                PopupSlide.X = _popupSlideStartX; PopupSlide.Y = _popupSlideStartY;
                RootGrid.Opacity = 1;
                break;
            default: // ScaleExpand / BounceExpand
                PopupScale.ScaleX = 0; PopupScale.ScaleY = 0;
                PopupSlide.X = _popupSlideStartX; PopupSlide.Y = _popupSlideStartY;
                RootGrid.Opacity = 1;
                break;
        }
    }

    void ApplyPopupFinalVisual()
    {
        StopPopupAnimations();
        if (PopupScale != null) { PopupScale.ScaleX = 1; PopupScale.ScaleY = 1; }
        if (PopupSlide != null) { PopupSlide.X = 0; PopupSlide.Y = 0; }
        if (RootGrid != null) RootGrid.Opacity = 1;
    }

    void StopPopupAnimations()
    {
        if (PopupScale != null)
        {
            PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        if (PopupSlide != null)
        {
            PopupSlide.BeginAnimation(TranslateTransform.XProperty, null);
            PopupSlide.BeginAnimation(TranslateTransform.YProperty, null);
        }
        if (RootGrid != null) RootGrid.BeginAnimation(UIElement.OpacityProperty, null);
    }

    void PlayPopupOpenAnimation()
    {
        if (_popupOpenStarted) return;
        _popupOpenStarted = true;

        // 先居中,再按「角落 → 中央」计算滑动位移。
        CenterPanelOnFocusedScreen();
        ConfigurePopupSlide();

        if (_popupAnimation == HoverExpandAnimationKind.None || Motion.IsReducedMotion())
        {
            ApplyPopupFinalVisual();
            return;
        }

        ApplyPopupClosedVisual();
        var dur = new Duration(TimeSpan.FromMilliseconds(240.0 / _popupSpeed));
        switch (_popupAnimation)
        {
            case HoverExpandAnimationKind.Fade:
                AnimatePopupSlide(_popupSlideStartX, 0, _popupSlideStartY, 0, dur, EasingMode.EaseOut, null);
                AnimatePopupOpacity(RootGrid.Opacity, 1, dur, EasingMode.EaseOut, null);
                break;
            case HoverExpandAnimationKind.VerticalExpand:
                AnimatePopupScaleY(PopupScale.ScaleY, 1, dur, EasingMode.EaseOut, null);
                AnimatePopupSlide(_popupSlideStartX, 0, _popupSlideStartY, 0, dur, EasingMode.EaseOut, null);
                break;
            case HoverExpandAnimationKind.DirectionalExpand:
                AnimatePopupScaleX(PopupScale.ScaleX, 1, dur, EasingMode.EaseOut, null);
                AnimatePopupSlide(_popupSlideStartX, 0, _popupSlideStartY, 0, dur, EasingMode.EaseOut, null);
                break;
            case HoverExpandAnimationKind.BounceExpand:
                AnimatePopupBounce(isExpand: true, dur, null);
                AnimatePopupSlide(_popupSlideStartX, 0, _popupSlideStartY, 0, dur, EasingMode.EaseOut, null);
                break;
            default: // ScaleExpand
                AnimatePopupScaleXY(PopupScale.ScaleX, 1, dur, EasingMode.EaseOut, null);
                AnimatePopupSlide(_popupSlideStartX, 0, _popupSlideStartY, 0, dur, EasingMode.EaseOut, null);
                break;
        }
    }

    void PlayPopupCloseAnimation(Action completed)
    {
        if (_popupAnimation == HoverExpandAnimationKind.None || Motion.IsReducedMotion())
        {
            completed();
            return;
        }
        StopPopupAnimations();

        // 关闭时按面板当前位置重新算「角落」偏移,保证滑回正确角落。
        ConfigurePopupSlide();
        var dur = new Duration(TimeSpan.FromMilliseconds(200.0 / _popupSpeed));
        switch (_popupAnimation)
        {
            case HoverExpandAnimationKind.Fade:
            {
                var once = CombinePopup(2, completed);
                AnimatePopupSlide(PopupSlide.X, _popupSlideStartX, PopupSlide.Y, _popupSlideStartY, dur, EasingMode.EaseIn, once);
                AnimatePopupOpacity(RootGrid.Opacity, 0, dur, EasingMode.EaseIn, once);
                break;
            }
            case HoverExpandAnimationKind.VerticalExpand:
            {
                var once = CombinePopup(2, completed);
                AnimatePopupScaleY(PopupScale.ScaleY, 0, dur, EasingMode.EaseIn, once);
                AnimatePopupSlide(PopupSlide.X, _popupSlideStartX, PopupSlide.Y, _popupSlideStartY, dur, EasingMode.EaseIn, once);
                break;
            }
            case HoverExpandAnimationKind.DirectionalExpand:
            {
                var once = CombinePopup(2, completed);
                AnimatePopupScaleX(PopupScale.ScaleX, 0, dur, EasingMode.EaseIn, once);
                AnimatePopupSlide(PopupSlide.X, _popupSlideStartX, PopupSlide.Y, _popupSlideStartY, dur, EasingMode.EaseIn, once);
                break;
            }
            case HoverExpandAnimationKind.BounceExpand:
            {
                var once = CombinePopup(2, completed);
                AnimatePopupBounce(isExpand: false, dur, once);
                AnimatePopupSlide(PopupSlide.X, _popupSlideStartX, PopupSlide.Y, _popupSlideStartY, dur, EasingMode.EaseIn, once);
                break;
            }
            default: // ScaleExpand
            {
                var once = CombinePopup(2, completed);
                AnimatePopupScaleXY(PopupScale.ScaleX, 0, dur, EasingMode.EaseIn, once);
                AnimatePopupSlide(PopupSlide.X, _popupSlideStartX, PopupSlide.Y, _popupSlideStartY, dur, EasingMode.EaseIn, once);
                break;
            }
        }
    }

    void AnimatePopupOpacity(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var fire = onComplete ?? (() => { });
        if (Math.Abs(from - to) < 1e-9)
        {
            RootGrid.Opacity = to;
            fire();
            return;
        }
        var anim = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        anim.Completed += (_, _) => { RootGrid.Opacity = to; fire(); };
        RootGrid.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    void AnimatePopupSlide(double fromX, double toX, double fromY, double toY, Duration dur, EasingMode ease, Action? onComplete)
    {
        var once = CombinePopup(2, onComplete);
        AnimatePopupTransformDouble(PopupSlide, TranslateTransform.XProperty, fromX, toX, dur, ease, once);
        AnimatePopupTransformDouble(PopupSlide, TranslateTransform.YProperty, fromY, toY, dur, ease, once);
    }

    void AnimatePopupScaleXY(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var once = CombinePopup(2, onComplete);
        AnimatePopupTransformDouble(PopupScale, ScaleTransform.ScaleXProperty, from, to, dur, ease, once);
        AnimatePopupTransformDouble(PopupScale, ScaleTransform.ScaleYProperty, from, to, dur, ease, once);
    }

    void AnimatePopupScaleX(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
        => AnimatePopupTransformDouble(PopupScale, ScaleTransform.ScaleXProperty, from, to, dur, ease, onComplete);

    void AnimatePopupScaleY(double from, double to, Duration dur, EasingMode ease, Action? onComplete)
        => AnimatePopupTransformDouble(PopupScale, ScaleTransform.ScaleYProperty, from, to, dur, ease, onComplete);

    void AnimatePopupTransformDouble(Animatable target, DependencyProperty prop, double from, double to, Duration dur, EasingMode ease, Action? onComplete)
    {
        var fire = onComplete ?? (() => { });
        if (Math.Abs(from - to) < 1e-9)
        {
            target.SetValue(prop, to);
            fire();
            return;
        }
        var anim = new DoubleAnimation(from, to, dur) { EasingFunction = new CubicEase { EasingMode = ease } };
        anim.Completed += (_, _) => { target.SetValue(prop, to); fire(); };
        target.BeginAnimation(prop, anim);
    }

    /// <summary>把 N 组子动画的完成信号收敛成一次回调(滑动/缩放/淡入都完成后才触发)。</summary>
    static Action CombinePopup(int count, Action? onComplete)
    {
        int remaining = count;
        bool fired = false;
        return () =>
        {
            if (fired) return;
            if (--remaining > 0) return;
            fired = true;
            onComplete?.Invoke();
        };
    }

    /// <summary>弹性展开/收起 — 与 HoverExpandBehavior.AnimateBounce 同配方:
    /// 打开 0→1.08→1,关闭 1→0.85(弹)→0,速度由 _popupSpeed 统一控制。</summary>
    void AnimatePopupBounce(bool isExpand, Duration duration, Action? onComplete)
    {
        if (!isExpand && Math.Abs(PopupScale.ScaleX) < 1e-9)
        {
            PopupScale.ScaleX = 0; PopupScale.ScaleY = 0;
            onComplete?.Invoke();
            return;
        }
        var bounce = new DoubleAnimationUsingKeyFrames();
        var ease = new BounceEase { Bounces = 2, Bounciness = 2, EasingMode = EasingMode.EaseOut };
        if (isExpand)
        {
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(PopupScale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120.0 / _popupSpeed)), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(duration.TimeSpan)));
        }
        else
        {
            var squashTime = TimeSpan.FromMilliseconds(duration.TimeSpan.TotalMilliseconds * 0.45);
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(PopupScale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(squashTime), ease));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(duration.TimeSpan),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
        }
        double final = isExpand ? 1 : 0;
        bool done = false;
        Action fireOnce = () => { if (done) return; done = true; onComplete?.Invoke(); };
        bounce.Completed += (_, _) => { PopupScale.ScaleX = final; PopupScale.ScaleY = final; fireOnce(); };
        PopupScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        PopupScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    // ── Hide (minimize) ──

    /// <summary>
    /// Single minimize entry point for the panel — the top-right "─" button and every
    /// external entry (PanelService.Hide/Toggle, ManagementWindow.TogglePanel, the
    /// right-click "Hide Panel" item) all route through here. Persists the position,
    /// disables the panel in config so the next launch agrees, then closes the window
    /// (OnClosed re-runs the same save chain harmlessly).
    /// </summary>
    public void HidePanel()
    {
        SavePosition(null, EventArgs.Empty);
        var config = _configService.Load();
        config.Panel.PanelEnabled = false;
        _configService.Save(config);
        Close();
    }

    void HideButton_Click(object s, MouseButtonEventArgs e)
    {
        HidePanel();
        e?.Handled = true;
    }

    void Ctrl_Enter(object s, MouseEventArgs e)
    {
        if (s is Border b) b.Background = CtrlHoverBrush;
    }

    void Ctrl_Leave(object s, MouseEventArgs e)
    {
        if (s is Border b) b.Background = CtrlIdleBrush;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 用户触发关闭(隐藏按钮/托盘/热键/关闭面板设置)时先播逆向动画再真正关窗。
        // 应用退出(Dispatcher.HasShutdownStarted)直接放行;关闭动画进行中则忽略重复关闭请求。
        if (_popupAllowClose || Dispatcher.HasShutdownStarted)
        {
            base.OnClosing(e);
            return;
        }

        if (_popupCloseAnimating)
        {
            e.Cancel = true;
            return;
        }

        if (_popupAnimation == HoverExpandAnimationKind.None || Motion.IsReducedMotion())
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        _popupCloseAnimating = true;
        ConfigurePopupSlide();
        PlayPopupCloseAnimation(() =>
        {
            _popupAllowClose = true;
            // 不能在 OnClosing/WmClose 内同步再调 Close() — 面板快速连续开关时,
            // 关闭动画的 from==to 会同步触发 onComplete,此时窗口仍在关闭流程中,
            // 直接 Close() 抛 InvalidOperationException("在窗口关闭期间无法调用 Close")。
            // 推迟到下一个 Dispatcher 周期再真正关窗。
            Dispatcher.BeginInvoke(new Action(Close));
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer?.Stop();
        _recycleTimer.Stop();
        CloseSubfolderFlyout();
        if (_langChanged != null) { _loc.LanguageChanged -= _langChanged; _langChanged = null; }
        _zoneManager.ZonesChanged -= RebuildDisplay;
        SavePosition(null, EventArgs.Empty);
        // Clear the enabled flag so it doesn't auto-restore on next launch
        var config = _configService.Load();
        config.Panel.PanelEnabled = false;
        _configService.Save(config);
        base.OnClosed(e);
    }
}
