using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Components;

namespace DesktopZones.Views;

/// <summary>
/// 桌面「播放器」组件窗口。两种形态共用同一个窗口，按
/// <see cref="DesktopMediaPlayer.Mode"/> 切换内容区：
/// 律动条（BarsPanel）/ 唱片机（VinylPanel）。
///
/// 骨架照抄 ClockWidget：桌面层驻留、四角缩放/吸附、锁定、
/// 最小化到恢复按钮、液态玻璃、背景图、右键菜单、位置防抖落盘。
/// 数据来自 <see cref="MediaSessionService"/>（系统 SMTC）。
/// </summary>
public partial class MediaPlayerWidget : Window
{
    private DesktopMediaPlayer _player;
    private readonly WidgetService _widgetService;
    private readonly MediaSessionService _media = MediaSessionService.Instance;
    private readonly LocalizationService _loc = LocalizationService.Instance;

    private HoverExpandBehavior? _hover;
    private SnapDrag? _snapDrag;
    private SnapResize? _snapResize;
    private Action<string>? _langChanged;

    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _savePending;

    private bool _restoreDragging;
    private Point _restoreDown;
    private Point _pressPoint;
    private bool _pressedInsideContent;

    // ── P2：跟随指定应用 / 强调色 ──

    /// <summary>当前生效的跟随会话（AUMID，空 = 自动跟随系统会话）。</summary>
    private string _follow = "";

    // 跟随指定应用时，这个组件的"当前曲目"由它自己的 1s 轮询维护，
    // 不碰 MediaSessionService.Now（那个是自动兜底判定的结果，多个播放器共用一份）。
    private MediaNowPlaying _pinnedNow = MediaNowPlaying.Empty;
    private System.Windows.Media.Imaging.BitmapSource? _pinnedArt;

    /// <summary>1s 心跳：只在"跟随某个应用"时跑（自动模式完全靠服务单例的 1s 轮询 + Changed）。</summary>
    private DispatcherTimer? _tick;
    private bool _ticking;

    /// <summary>从封面提取的强调色（null = 用用户手设的颜色）。</summary>
    private Color? _accent;
    private System.Windows.Media.Imaging.BitmapSource? _accentArt;

    public MediaPlayerWidget(DesktopMediaPlayer player, WidgetService widgetService)
    {
        InitializeComponent();
        Deactivated += (_, _) => { if (IsVisible) NativeMethods.PinBelowProgman(this); };

        _player = player;
        _widgetService = widgetService;

        Left = player.X;
        Top = player.Y;
        Opacity = player.Opacity;

        ApplyMode();
        ApplyStyle();

        Loaded += OnLoad;
        SizeChanged += OnSizeChanged;
        LocationChanged += (_, _) => { _player.X = Left; _player.Y = Top; ScheduleSave(); };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            if (_savePending) { _savePending = false; _widgetService.Save(); }
        };

        _langChanged = _ => UpdateLocalizedText();
        _loc.LanguageChanged += _langChanged;
        _widgetService.MediaPlayersChanged += OnPlayersChanged;
        _widgetService.LockChanged += OnServiceLockChanged;

        _hover = new HoverExpandBehavior(this, RestoreButton, MainContent, null,
            () => _player.HoverExpandAnimation,
            () => _player.HoverExpandSpeed,
            () => _player.HoverExpandOrigin,
            () => _player.HoverAutoExpand)
        { IsEnabled = _player.EnableRestoreButton };
        _player.HoverExpandSettingsChanged += OnHoverExpandSettingsChanged;
        _hover.Expanded += ReapplyAcrylic;
        _hover.Collapsed += OnHoverCollapsed;
        if (_player.IsVisible) _hover.SnapToExpanded();

        _snapDrag = new SnapDrag(this);
        _snapResize = new SnapResize(this);

        // 媒体会话：懒启动（没有播放器组件时不碰 WinRT）
        _media.Changed += OnMediaChanged;
        _media.EnsureStarted();
        RefreshMediaState();
        ApplyFollow();
    }

    // ── 生命周期 ──

    void OnLoad(object s, RoutedEventArgs e)
    {
        DesktopLayer.BringToFront(this);
        NativeMethods.SetToolWindow(this);
        NativeMethods.DisableDwmFrameShadow(this);
        NativeMethods.RemoveThickFrame(this);
        ApplyAcrylic();
        ApplyBackgroundImage();
        NativeMethods.SetRoundedCorners(this, _player.CornerRadius);
        ApplyLockState();
        UpdateLocalizedText();
        if (!_player.IsVisible) ApplyHidden();
    }

    void OnSizeChanged(object s, SizeChangedEventArgs e)
    {
        if (MainContent.Visibility != Visibility.Visible) return;
        _player.Width = Width;
        _player.Height = Height;
        SaveCurrentSizeForMode();
        NativeMethods.UpdateRoundedCorners(this, _player.CornerRadius);
        UpdateResponsiveLayout();
    }

    protected override void OnClosed(EventArgs e)
    {
        _saveDebounce.Stop();
        _tick?.Stop();
        _tick = null;
        if (_savePending) { _savePending = false; _widgetService.Save(); }
        if (_langChanged != null) _loc.LanguageChanged -= _langChanged;
        _langChanged = null;
        _widgetService.MediaPlayersChanged -= OnPlayersChanged;
        _widgetService.LockChanged -= OnServiceLockChanged;
        _player.HoverExpandSettingsChanged -= OnHoverExpandSettingsChanged;
        _media.Changed -= OnMediaChanged;
        _snapDrag?.Detach();
        _snapResize?.Detach();
        _hover?.Dispose();
        base.OnClosed(e);
    }

    // ── 模式与布局 ──

    void ApplyMode()
    {
        bool vinyl = _player.Mode == MediaDisplayMode.Vinyl;
        if (BarsPanel != null) BarsPanel.Visibility = vinyl ? Visibility.Collapsed : Visibility.Visible;
        if (VinylPanel != null) VinylPanel.Visibility = vinyl ? Visibility.Visible : Visibility.Collapsed;

        if (vinyl)
        {
            MinWidth = 260; MinHeight = 200;
            Width = Math.Max(MinWidth, _player.VinylModeWidth ?? DesktopMediaPlayer.DefaultVinylWidth);
            Height = Math.Max(MinHeight, _player.VinylModeHeight ?? DesktopMediaPlayer.DefaultVinylHeight);
        }
        else
        {
            MinWidth = 220; MinHeight = 120;
            Width = Math.Max(MinWidth, _player.BarsModeWidth ?? DesktopMediaPlayer.DefaultBarsWidth);
            Height = Math.Max(MinHeight, _player.BarsModeHeight ?? DesktopMediaPlayer.DefaultBarsHeight);
        }
        UpdateResponsiveLayout();
    }

    void UpdateResponsiveLayout()
    {
        if (BarsPanel != null && _player.Mode == MediaDisplayMode.Bars)
        {
            // 隐藏下半部分时用 Hidden 而不是 Collapsed：占位仍然保留 → 条区高度不变，
            // 下方留空（透明），不会被自动撑大。
            TrackPanel.Visibility = _player.ShowTrackInfo ? Visibility.Visible : Visibility.Hidden;
            BarsControls.Visibility = _player.ShowControls ? Visibility.Visible : Visibility.Collapsed;
            Divider.Visibility = _player.ShowDivider ? Visibility.Visible : Visibility.Collapsed;
        }
        if (VinylPanel != null && _player.Mode == MediaDisplayMode.Vinyl)
        {
            bool info = _player.ShowVinylTrackPanel;
            VinylInfo.Visibility = info ? Visibility.Visible : Visibility.Collapsed;
            VinylControls.Visibility = _player.ShowControls ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void SaveCurrentSizeForMode()
    {
        if (_player.Mode == MediaDisplayMode.Vinyl)
        {
            _player.VinylModeWidth = Width;
            _player.VinylModeHeight = Height;
        }
        else
        {
            _player.BarsModeWidth = Width;
            _player.BarsModeHeight = Height;
        }
    }

    // ── 外观 ──

    public void RefreshAppearance(DesktopMediaPlayer? player = null)
    {
        if (player != null) _player = player;
        ApplyStyle();
        ApplyFollow();
    }

    void ApplyStyle()
    {
        int r = _player.CornerRadius;
        bool barsTile = IsBarsTileMode();

        MainContent.CornerRadius = new CornerRadius(r);
        BorderRoot.CornerRadius = new CornerRadius(r);
        GlassBorder.CornerRadius = new CornerRadius(r);
        FillRect.RadiusX = FillRect.RadiusY = r;

        // 边框
        try
        {
            BorderRoot.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_player.BorderColor)!);
        }
        catch { }
        BorderRoot.BorderThickness = new Thickness(barsTile ? 0 : _player.BorderThickness);
        BorderRoot.InvalidateVisual();

        // 填充：磁贴模式的律动条整层透明
        if (barsTile)
        {
            FillRect.Fill = Brushes.Transparent;
        }
        else
        {
            bool glassCarriesFill = _player.EnableLiquidGlass && (_hover?.IsExpanded ?? false);
            try
            {
                FillRect.Fill = glassCarriesFill
                    ? AcrylicHelper.HitTestFill
                    : new SolidColorBrush((Color)ColorConverter.ConvertFromString(_player.FillColor)!);
            }
            catch { FillRect.Fill = Brushes.Transparent; }
        }
        FillRect.InvalidateVisual();

        ApplyBackgroundImage();
        ApplyAcrylic();
        ApplyTextColors();
        ApplyBarsSettings();
        ApplyVinylSettings();
        ApplyQuickBar();
        ApplyLockState();

        bool collapsed = RestoreButton.Visibility == Visibility.Visible || _hover is { IsExpanded: false };
        if (PresentationSource.FromVisual(this) != null && !collapsed && !barsTile)
            NativeMethods.SetRoundedCorners(this, r);
        else if (barsTile)
            NativeMethods.DisableRoundedCorners(this);

        // 强调色跟外观设置挂钩，统一在这里收口
        UpdateAccent();
    }

    bool IsBarsTileMode() => _player.TileMode && _player.Mode == MediaDisplayMode.Bars;

    void ApplyTextColors()
    {
        Brush text, buttons;
        try { text = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_player.TextColor)!); }
        catch { text = Brushes.White; }
        try { buttons = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_player.ButtonColor)!); }
        catch { buttons = Brushes.White; }

        TitleText.Foreground = text;
        ArtistText.Foreground = WithAlpha(text, 0x99);
        VinylTitle.Foreground = text;
        VinylArtist.Foreground = WithAlpha(text, 0xB0);
        VinylAlbum.Foreground = WithAlpha(text, 0x80);
        BarsHint.Foreground = WithAlpha(text, 0x70);
        VinylHint.Foreground = WithAlpha(text, 0x88);
        // 分割线有自己的颜色（默认 #3CFFFFFF）；旧配置没有这个字段时回落到文字色的低透明版本
        Divider.Fill = string.IsNullOrWhiteSpace(_player.DividerColor)
            ? WithAlpha(text, 0x3C)
            : new SolidColorBrush(ParseColor(_player.DividerColor, Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)));

        foreach (var path in new[]
                 {
                     BarsPlayIcon, VinylPlayIcon,
                 })
        {
            if (path != null) path.Stroke = buttons;
        }
        foreach (var btn in new[] { BarsPrevBtn, BarsNextBtn, VinylPrevBtn, VinylNextBtn })
        {
            if (btn?.Content is Path p) p.Stroke = WithAlpha(buttons, 0xE6);
        }
        LockBtn.Foreground = buttons;
        HideBtn.Foreground = buttons;
        ApplyRestoreIcon(buttons);
    }

    static SolidColorBrush WithAlpha(Brush source, byte alpha)
    {
        var c = source is SolidColorBrush scb ? scb.Color : Colors.White;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }

    void ApplyRestoreIcon(Brush fallback)
    {
        var color = !string.IsNullOrEmpty(_player.IconColor) ? _player.IconColor : _player.ButtonColor;
        Brush ic;
        try { ic = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!); }
        catch { ic = fallback; }
        var icon = string.IsNullOrEmpty(_player.IconChar) ? IconGlyph.Media : _player.IconChar;
        IconGlyph.Apply(RestoreIconChar, RestoreIconPath, icon, ic, 18);
    }

    void ApplyBarsSettings()
    {
        int n = Math.Clamp(_player.BarsCount, 0, 100);
        Bars.BarCount = n;
        Bars.BarWidth = _player.BarsBarWidth;
        Bars.Rounded = _player.BarsRounded;
        // 面板上是 1–100 档位，渲染侧要的是倍率
        Bars.Amplitude = DesktopMediaPlayer.AmplitudeFromLevel(_player.BarsAmplitude);
        Bars.Sensitivity = DesktopMediaPlayer.SensitivityFromLevel(_player.BarsSensitivity);
        // 真实响度：让条子跟着音乐本身的强弱走（不再只是合成正弦）
        AudioLevelMeter.EnsureStarted();
        Bars.EnergyProvider = static () => AudioLevelMeter.Level;

        // 律动数据源：真频谱（默认）/ 模拟。真频谱走 WASAPI 环回 + FFT，采不到会自动退回模拟。
        Bars.Source = string.IsNullOrEmpty(_player.BarsSource) ? "Spectrum" : _player.BarsSource;
        Bars.BandProvider = static () => AudioSpectrumCapture.Bands;
        AudioSpectrumCapture.EnsureStarted(Math.Max(1, n));
        Bars.Reflection = _player.BarsReflection;
        Bars.ColorMode = _player.BarsColorMode;
        Bars.Color1 = ParseColor(_player.BarsColor, Color.FromRgb(0x7C, 0xF5, 0xFF));
        Bars.Color2 = ParseColor(_player.BarsColor2, Color.FromRgb(0xB5, 0x8C, 0xFF));
        Bars.AutoColorIntervalSec = Math.Max(1, _player.BarsAutoIntervalSec);
        Bars.Apply();
        UpdateResponsiveLayout();
    }

    void ApplyVinylSettings()
    {
        Disc.SpinSeconds = _player.VinylSpinSeconds;
        // 兜底封面开关是"每个组件一份"，服务是单例 → 以最后应用外观的那个组件为准
        _media.FetchExternalArtwork = _player.FetchCover;
        UpdateResponsiveLayout();
    }

    static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex)!; }
        catch { return fallback; }
    }

    void ApplyQuickBar()
    {
        // 磁贴模式（两种形态都隐藏锁/最小化按钮）
        var vis = _player.TileMode ? Visibility.Collapsed : Visibility.Visible;
        LockBtn.Visibility = vis;
        HideBtn.Visibility = vis;
        HoverHighlight.ApplyControlOpacity(_player.ControlOpacity, new UIElement[] { LockBtn, HideBtn });
    }

    // ── 背景图 ──

    void ApplyBackgroundImage()
    {
        try
        {
            bool barsTile = IsBarsTileMode();
            if (!barsTile && !string.IsNullOrEmpty(_player.BackgroundImagePath)
                && System.IO.File.Exists(_player.BackgroundImagePath))
            {
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(_player.BackgroundImagePath);
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                BgImage.Source = bi;
                BgImageBorder.Visibility = Visibility.Visible;
                BgImage.Opacity = Math.Max(0.01, _player.BackgroundImageOpacity / 100.0);
            }
            else
            {
                BgImage.Source = null;
                BgImageBorder.Visibility = Visibility.Collapsed;
                BgImage.Opacity = 0;
            }
        }
        catch { BgImage.Source = null; BgImageBorder.Visibility = Visibility.Collapsed; }
    }

    // ── 玻璃 ──

    public void ApplyAcrylic()
    {
        bool expanded = _hover?.IsExpanded ?? false;
        if (_player.EnableLiquidGlass && expanded && !IsBarsTileMode())
        {
            var result = AcrylicHelper.EnableBlurComposite(this, _player.GlassBlurAmount,
                _player.FillColor, 1.0, _player.GlassColorMode,
                _player.GlassTintOpacity, _player.GlassTintLuminosity,
                AcrylicHelper.ResolveAccentState(_player.GlassMaterial),
                AcrylicHelper.ResolveUseClassicBlur(_player.GlassMaterial));
            if (!result.Success)
                System.Diagnostics.Debug.WriteLine($"[MediaPlayer] EnableBlur failed: {result.Error}");

            if (GlassBorder != null)
            {
                GlassBorder.BorderBrush = AcrylicHelper.CreateChromaticBorder();
                GlassBorder.BorderThickness = new Thickness(Math.Max(1.0, _player.BorderThickness));
                GlassBorder.CornerRadius = new CornerRadius(_player.CornerRadius);
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            if (GlassBorder != null) GlassBorder.BorderThickness = new Thickness(0);
        }
    }

    void ReapplyAcrylic()
    {
        NativeMethods.SetRoundedCorners(this, _player.CornerRadius);
        ApplyAcrylic();
    }

    void OnHoverCollapsed()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        NativeMethods.DisableDwmFrameShadow(this);
    }

    void OnHoverExpandSettingsChanged() => _hover?.SetEnabled(_player.EnableRestoreButton);

    // ── 媒体状态 ──

    void OnMediaChanged() => RefreshMediaState();

    void OnPlayersChanged()
    {
        if (!IsLoaded) return;
        var latest = _widgetService.MediaPlayers.FirstOrDefault(p => p.Id == _player.Id);
        if (latest != null) _player = latest;
        if (!_player.IsVisible && _hover != null && _hover.IsExpanded && !_hover.IsCollapsePending
            && MainContent.Visibility == Visibility.Visible)
            _hover.SnapToCollapsed();
        ApplyStyle();
        // ApplyFollow 内部会 RefreshMediaState（跟随对象/进度都可能在这次改动里换了）
        ApplyFollow();
    }

    void RefreshMediaState()
    {
        if (TitleText == null) return;
        var now = CurrentNow();
        bool pausedByUser = _player.Paused;
        bool playing = now.IsPlaying && !pausedByUser;
        bool noMedia = !now.HasSession;

        Bars.SetPlaying(now.IsPlaying, pausedByUser);
        Disc.SetPlaying(now.IsPlaying, pausedByUser);
        Arm.SetPlaying(now.IsPlaying && !pausedByUser);
        Disc.Cover = CurrentArt();

        string title = string.IsNullOrWhiteSpace(now.Title) ? "" : now.Title;
        string artist = now.ArtistText;
        string album = now.AlbumText;

        // 认不出曲目（只知道"有声音在放"）→ 不显示任何曲目内容，只保留播放状态；
        // 彻底没有媒体时才显示"未检测到正在播放的媒体"。
        bool unknownTrack = now.HasSession && title.Length == 0;
        TitleText.Text = noMedia ? _loc["Media.NoMedia"] : title;
        ArtistText.Text = noMedia || unknownTrack
            ? ""
            : string.IsNullOrEmpty(album) ? artist : $"{artist} · {album}";
        VinylTitle.Text = noMedia ? _loc["Media.NoMedia"] : title;
        VinylArtist.Text = unknownTrack ? "" : artist;
        VinylAlbum.Text = unknownTrack ? "" : album;

        // 无媒体 / 媒体暂停 / 用户勾了「暂停」：条区不画，给一行提示
        bool showHint = noMedia || pausedByUser || !now.IsPlaying;
        BarsHint.Text = noMedia ? _loc["Media.NoMediaHint"] : _loc["Media.Paused"];
        BarsHint.Visibility = showHint ? Visibility.Visible : Visibility.Collapsed;
        // 唱片机的曲目区已经会显示「未在播放」，这里只在用户勾了暂停时补一句，避免重复
        VinylHint.Text = _loc["Media.Paused"];
        VinylHint.Visibility = pausedByUser ? Visibility.Visible : Visibility.Collapsed;

        UpdatePlayIcons(now.IsPlaying);
        UpdateAccent();
    }

    // ── P2：当前数据源（自动 / 跟随指定应用）──

    /// <summary>自动模式读服务单例；跟随模式读本组件自己的会话快照。</summary>
    MediaNowPlaying CurrentNow() => _follow.Length > 0 ? _pinnedNow : _media.Now;

    System.Windows.Media.Imaging.BitmapSource? CurrentArt() => _follow.Length > 0 ? _pinnedArt : _media.Artwork;

    /// <summary>
    /// 「跟随的应用」的当前值。**从 WidgetService 的集合里读**，而不是只读缓存的
    /// <see cref="_player"/> —— 属性面板预览时改的是集合里的那个实例，组件缓存的那份
    /// 引用在某些路径上（面板克隆 / 重建）可能不是同一个对象，只认缓存就会出现
    /// "要关掉设置界面重开才同步"。
    /// </summary>
    string CurrentFollowSetting()
    {
        var live = _widgetService.MediaPlayers.FirstOrDefault(p => p.Id == _player.Id);
        return (live ?? _player).FollowSession ?? "";
    }

    /// <summary>把「跟随的应用」落到运行时：变了就重建本地快照 + 起停心跳。</summary>
    void ApplyFollow()
    {
        var want = CurrentFollowSetting();
        if (!string.Equals(want, _follow, StringComparison.OrdinalIgnoreCase))
        {
            _follow = want;
            _pinnedNow = MediaNowPlaying.Empty;
            _pinnedArt = null;
            if (_follow.Length > 0) _ = RefreshPinnedAsync();
        }
        EnsureTick();
        RefreshMediaState();
    }

    /// <summary>属性面板改完「跟随的应用」后直接叫一下：不等预览回调、也不等下一次心跳。</summary>
    public void RefreshFollow()
    {
        var live = _widgetService.MediaPlayers.FirstOrDefault(p => p.Id == _player.Id);
        if (live != null) _player = live;
        ApplyFollow();
    }

    /// <summary>只有"跟了某个应用"时才需要每秒心跳。</summary>
    void EnsureTick()
    {
        if (_follow.Length > 0)
        {
            if (_tick != null) return;
            _tick = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _tick.Tick += OnTick;
            _tick.Start();
        }
        else
        {
            _tick?.Stop();
            _tick = null;
        }
    }

    void OnTick(object? sender, EventArgs e)
    {
        if (_ticking || _follow.Length == 0) return;
        _ = RefreshPinnedAsync();
    }

    /// <summary>跟随模式下每秒读一次指定会话（曲目 / 封面）。</summary>
    async Task RefreshPinnedAsync()
    {
        if (_follow.Length == 0 || _ticking) return;
        _ticking = true;
        try
        {
            var snap = await _media.ReadSessionAsync(_follow);
            if (!string.Equals(_follow, CurrentFollowSetting(), StringComparison.OrdinalIgnoreCase))
                return;                                  // 期间用户改了跟随对象 → 丢弃
            if (snap == null)
            {
                _pinnedNow = MediaNowPlaying.Empty;
                _pinnedArt = null;
            }
            else
            {
                _pinnedNow = snap.Now;
                _pinnedArt = snap.Artwork;
            }
            RefreshMediaState();
        }
        finally
        {
            _ticking = false;
        }
    }

    // ── P2：封面主色 ──

    /// <summary>封面换了才重算主色（一首歌里 RefreshMediaState 会被调很多次）。</summary>
    void UpdateAccent()
    {
        var art = CurrentArt();
        if (_player.AccentFromCover)
        {
            if (ReferenceEquals(art, _accentArt)) return;
            _accentArt = art;
            _accent = MediaPalette.GetAccent(art);
            ApplyAccentColors();
        }
        else if (_accent != null)
        {
            _accent = null;
            _accentArt = null;
            ApplyAccentColors();
        }
    }

    /// <summary>把强调色铺到"有身份的地方"：律动条、分割线、唱片机中心标签。
    /// 没有强调色（没封面 / 封面没主色 / 用户没开）时全部回到手设颜色。</summary>
    void ApplyAccentColors()
    {
        var c1 = ParseColor(_player.BarsColor, Color.FromRgb(0x7C, 0xF5, 0xFF));
        var c2 = ParseColor(_player.BarsColor2, Color.FromRgb(0xB5, 0x8C, 0xFF));

        Color bars1 = c1, bars2 = c2;
        if (_accent is Color a && _player.BarsColorMode != "Auto")
        {
            bars1 = a;
            bars2 = _player.BarsColorMode == "Single" ? a : MediaPalette.Companion(a);
        }
        Bars.SetColors(bars1, bars2);

        // 分割线：保留用户设的透明度，只换色相
        var dividerBase = string.IsNullOrWhiteSpace(_player.DividerColor)
            ? Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)
            : ParseColor(_player.DividerColor, Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF));
        Divider.Fill = new SolidColorBrush(_accent is Color acc
            ? MediaPalette.WithAlpha(acc, dividerBase.A == 0 ? (byte)0x3C : dividerBase.A)
            : dividerBase);

        // 唱片机：中心标签（无封面时）与提示文字用强调色
        Disc.AccentColor = _accent ?? c1;
    }

    void UpdatePlayIcons(bool mediaPlaying)
    {
        var key = mediaPlaying ? "Icon.MediaPause" : "Icon.MediaPlay";
        if (Application.Current != null && Application.Current.TryFindResource(key) is Geometry geo)
        {
            if (BarsPlayIcon != null) BarsPlayIcon.Data = geo;
            if (VinylPlayIcon != null) VinylPlayIcon.Data = geo;
        }
    }

    void UpdateLocalizedText()
    {
        if (CtxSettings == null) return;
        CtxSettings.Header = _loc["Media.Settings"];
        CtxMinimize.Header = _loc["Media.Minimize"];
        CtxLock.Header = _loc[_player.IsLocked ? "Common.Unlock" : "Common.Lock"];
        CtxDelete.Header = _loc["Media.Delete"];
        RefreshMediaState();
    }

    // ── 控制按钮 ──

    /// <summary>跟随指定应用时，控制指令只发给那个会话；自动模式下传 null（服务自己挑）。</summary>
    string? FollowAumidOrNull() => _follow.Length > 0 ? _follow : null;

    async void PlayPause_Click(object s, RoutedEventArgs e)
    {
        await _media.TogglePlayPauseAsync(FollowAumidOrNull());
    }

    async void Prev_Click(object s, RoutedEventArgs e)
    {
        await _media.PreviousAsync(FollowAumidOrNull());
    }

    async void Next_Click(object s, RoutedEventArgs e)
    {
        await _media.NextAsync(FollowAumidOrNull());
    }

    // ── 点击切换播放 / 拖动 ──

    /// <summary>单击条区或唱片 = 播放/暂停（可关）。判定放在窗口层：
    /// SnapDrag 会 CaptureMouse，子元素的 MouseLeftButtonUp 收不到事件。</summary>
    void Window_PreviewMouseLeftButtonUp(object s, MouseButtonEventArgs e)
    {
        bool inside = _pressedInsideContent;
        _pressedInsideContent = false;
        if (!inside || e.ChangedButton != MouseButton.Left) return;
        if (!_player.ClickToToggle || _player.IsLocked) return;
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4) return;
        _ = _media.TogglePlayPauseAsync(FollowAumidOrNull());
    }

    void Window_PreviewMouseLeftButtonDown(object s, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressedInsideContent = false;
        var src = e.OriginalSource as DependencyObject;
        while (src != null && src != s)
        {
            if (src is Button) return;
            if (src is Border b && b.Tag is string tag
                && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR")) return;
            src = VisualTreeHelper.GetParent(src);
        }
        if (RestoreButton.Visibility == Visibility.Visible) return;
        if (_player.IsLocked) return;

        _pressedInsideContent = _player.Mode == MediaDisplayMode.Bars
            ? BarsRoot.IsMouseOver
            : DiscHost.IsMouseOver;

        _snapDrag?.Start(e, () =>
        {
            DesktopLayer.BringToFront(this);
            _player.X = Left; _player.Y = Top;
        });
    }

    void ResizeGrip_Down(object s, MouseButtonEventArgs e)
    {
        if (s is not Border g || g.Tag is not string tag) return;
        if (_player.IsLocked) { e.Handled = true; return; }
        bool left = tag == "TL" || tag == "BL";
        bool top = tag == "TL" || tag == "TR";
        _snapResize?.Start(e, left, top, !left, !top, 220, 120, () =>
        {
            _player.Width = Width;
            _player.Height = Height;
            SaveCurrentSizeForMode();
            _widgetService.UpdateMediaPlayer(_player);
        });
        e.Handled = true;
    }

    void HideBtn_Click(object s, RoutedEventArgs e) => HidePlayer();
    void HideBtn_MouseDown(object s, MouseButtonEventArgs e) => e.Handled = true;

    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void LockBtn_Click(object s, RoutedEventArgs e)
    {
        bool locked = !_player.IsLocked;
        _player.IsLocked = locked;
        _widgetService.SetLocked(_player.Id.ToString(), locked);
        ApplyLockState();
        _widgetService.UpdateMediaPlayer(_player);
    }

    void OnServiceLockChanged(string id, bool locked)
    {
        if (id != _player.Id.ToString()) return;
        if (_player.IsLocked == locked) return;
        _player.IsLocked = locked;
        ApplyLockState();
    }

    void ApplyLockState()
    {
        if (LockBtn == null) return;
        LockBtn.Content = _player.IsLocked ? "🔒" : "🔓";
        CtxLock.Header = _loc[_player.IsLocked ? "Common.Unlock" : "Common.Lock"];
        var gripVis = _player.IsLocked ? Visibility.Collapsed : Visibility.Visible;
        foreach (var grip in FindResizeGrips(this)) grip.Visibility = gripVis;
        if (_player.IsLocked && IsVisible) NativeMethods.PinBelowProgman(this);
    }

    void Settings_Click(object s, RoutedEventArgs e) => PropertyWindowService.OpenOrFocus(_player, this);

    void Delete_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(_loc["Media.DeleteConfirm"], _loc["Media.DeleteTitle"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        _widgetService.DeleteMediaPlayer(_player.Id);
        Close();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? 0.05 : -0.05;
        _player.Opacity = Math.Clamp(_player.Opacity + delta, 0.1, 1.0);
        Opacity = _player.Opacity;
        _widgetService.UpdateMediaPlayer(_player);
        e.Handled = true;
    }

    static IEnumerable<Border> FindResizeGrips(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.Tag is string tag
                && (tag == "TL" || tag == "TR" || tag == "BL" || tag == "BR"))
                yield return b;
            foreach (var nested in FindResizeGrips(child))
                yield return nested;
        }
    }

    // ── 显示/隐藏（与时钟同款） ──

    public void ShowPlayer(bool skipResync = false, double waveDelayMs = 0)
    {
        if (!IsVisible) Show();
        ApplyMode();
        Left = _player.X;
        Top = _player.Y;
        if (_player.Mode == MediaDisplayMode.Vinyl)
        {
            Width = Math.Max(MinWidth, _player.VinylModeWidth ?? 500);
            Height = Math.Max(MinHeight, _player.VinylModeHeight ?? 320);
        }
        else
        {
            Width = Math.Max(MinWidth, _player.BarsModeWidth ?? 380);
            Height = Math.Max(MinHeight, _player.BarsModeHeight ?? 190);
        }

        if (waveDelayMs > 0)
        {
            MainContent.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Collapsed;
            _hover?.SnapToCollapsed();
            RestoreButton.Visibility = Visibility.Collapsed;
            _hover?.ShowAfterDelay(waveDelayMs);
        }
        else
        {
            bool fromButton = RestoreButton.Visibility == Visibility.Visible;
            MainContent.Visibility = Visibility.Visible;
            RestoreButton.Visibility = Visibility.Collapsed;
            if (fromButton) _hover?.ExpandAnimated(permanent: true);
            else _hover?.SnapToExpanded();
        }

        if (!skipResync) ApplyAcrylic();
        DesktopLayer.BringToFront(this);
        NativeMethods.SetRoundedCorners(this, _player.CornerRadius);
        if (!skipResync)
        {
            _player.IsVisible = true;
            _widgetService.UpdateMediaPlayer(_player);
        }
        else
        {
            _player.IsVisible = true;
        }
        RefreshMediaState();
        Activate();
    }

    public void PlayEntranceAnimation(double waveDelayMs)
    {
        if (waveDelayMs <= 0) return;
        _hover?.SnapToCollapsed();
        RestoreButton.Visibility = Visibility.Collapsed;
        _hover?.ShowAfterDelay(waveDelayMs);
    }

    public void HidePlayer(double waveDelayMs = 0)
    {
        _player.X = Left; _player.Y = Top;
        _player.Width = Width; _player.Height = Height;
        SaveCurrentSizeForMode();
        NativeMethods.DisableRoundedCorners(this);

        if (!_player.EnableRestoreButton)
        {
            if (waveDelayMs > 0)
            {
                _hover?.CollapseAfterDelay(waveDelayMs, onComplete: () =>
                {
                    AcrylicHelper.DisableBlur(this);
                    _hover?.SnapToFullHidden();
                    MainContent.Visibility = Visibility.Collapsed;
                    MinWidth = 40; MinHeight = 40;
                    Width = 40; Height = 40;
                    Hide();
                });
            }
            else
            {
                AcrylicHelper.DisableBlur(this);
                _hover?.SnapToFullHidden();
                MainContent.Visibility = Visibility.Collapsed;
                MinWidth = 40; MinHeight = 40;
                Width = 40; Height = 40;
                Hide();
            }
        }
        else
        {
            AcrylicHelper.DisableBlur(this);
            DesktopLayer.BringToFront(this);
            if (waveDelayMs > 0) _hover?.CollapseAfterDelay(waveDelayMs, null);
            else _hover?.CollapseAnimated();
        }
        _player.IsVisible = false;
        _widgetService.UpdateMediaPlayer(_player);
    }

    void ApplyHidden()
    {
        AcrylicHelper.DisableBlur(this);
        NativeMethods.DisableRoundedCorners(this);
        if (!_player.EnableRestoreButton)
        {
            _hover?.SnapToFullHidden();
            MainContent.Visibility = Visibility.Collapsed;
            MinWidth = 40; MinHeight = 40;
            Width = 40; Height = 40;
            Hide();
        }
        else
        {
            if (_player.Mode == MediaDisplayMode.Vinyl)
            {
                MinWidth = 260; MinHeight = 200;
                Width = Math.Max(MinWidth, _player.VinylModeWidth ?? 500);
                Height = Math.Max(MinHeight, _player.VinylModeHeight ?? 320);
            }
            else
            {
                MinWidth = 220; MinHeight = 120;
                Width = Math.Max(MinWidth, _player.BarsModeWidth ?? 380);
                Height = Math.Max(MinHeight, _player.BarsModeHeight ?? 190);
            }
            _hover?.SnapToCollapsed();
        }
    }

    // ── 恢复按钮 ──

    void Restore_MouseDown(object s, MouseButtonEventArgs e)
    {
        _restoreDragging = false;
        _restoreDown = e.GetPosition(this);
        RestoreButton.CaptureMouse();
        e.Handled = true;
    }

    void Restore_MouseMove(object s, MouseEventArgs e)
    {
        if (!RestoreButton.IsMouseCaptured) return;
        var d = e.GetPosition(this) - _restoreDown;
        if (Math.Abs(d.X) > 3 || Math.Abs(d.Y) > 3)
        {
            _restoreDragging = true;
            RestoreButton.ReleaseMouseCapture();
            _snapDrag?.Start(e, () =>
            {
                DesktopLayer.BringToFront(this);
                _player.X = Left; _player.Y = Top;
            });
        }
    }

    void Restore_MouseUp(object s, MouseButtonEventArgs e)
    {
        RestoreButton.ReleaseMouseCapture();
        if (_restoreDragging) return;
        _player.IsVisible = true;
        _hover?.ExpandAnimated(permanent: true);
        _widgetService.UpdateMediaPlayer(_player);
    }

    void Restore_Enter(object s, MouseEventArgs e)
        => RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Hover");

    void Restore_Leave(object s, MouseEventArgs e)
        => RestoreButton.SetResourceReference(Border.BackgroundProperty, "Menu.Bg.Surface");

    // ── 防抖保存 ──

    void ScheduleSave() { _savePending = true; _saveDebounce.Stop(); _saveDebounce.Start(); }
}
