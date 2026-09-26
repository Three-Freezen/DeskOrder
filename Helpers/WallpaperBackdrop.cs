using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail 2026-09-26: 方案①「壁纸采样背板」—— DWM 的背板模糊在 Win11 上是固定强度
/// (实测 AccentFlags 半径 1=30=60),要让**模糊半径真正生效**只能自己画背板。
/// 本类把「按适配模式铺到虚拟桌面的壁纸」降采样后**预模糊**成一张缓存位图,再按窗口的
/// 屏幕矩形裁出对应区域给窗口当底层,于是:
///   * 半径真正可调(预模糊半径 = 屏幕半径 / 降采样倍数);
///   * 窗口移动/缩放只改 ImageBrush.Viewbox,不重解码、不重模糊;
///   * 背板是普通 WPF 元素 → 放进 MainContent 后自动跟随收起/展开动画。
/// 语义上等于 Windows 11 的「云母」: 采的是壁纸,不透出后方窗口。
///
/// 全部坐标用**物理像素**(虚拟桌面原点 = 主屏左上,可负),刻意避开 DPI 换算:
/// 虚拟桌面尺寸/窗口矩形都取 Win32 物理像素,壁纸布局也按物理像素算,
/// ImageBrush 的 Viewbox 是图像像素空间,与 DPI 无关。
///
/// 降级链: 壁纸读不到 → <see cref="TryCreate"/> 返回 null → 调用方继续走 DWM 玻璃。
/// </summary>
public static class WallpaperBackdrop
{
    /// <summary>降采样倍数 —— **直接引用 <see cref="WallpaperSource.Downscale"/>**。
    /// ponytail 2026-09-26(二期修订 3): 原来两处各写一个 2,改分辨率时极易只改一边
    /// (裁剪坐标立刻全体偏移)。现在只有一个真源。</summary>
    const int Downscale = WallpaperSource.Downscale;

    /// <summary>预模糊半径小于该值时不做模糊(避免无意义的 RTB 开销)。</summary>
    const double MinBlurPx = 0.4;

    /// <summary>预模糊缓存档位上限。全分辨率(1:1)下每档约 = 虚拟桌面像素 × 4 字节
    /// (本机 4480×1600 → 约 29 MB),所以从 12 降到 6:再高就是几百 MB 的位图常驻。
    /// 关掉渲染方案 / 重新采样时会整块清空。</summary>
    const int MaxBlurCacheEntries = 6;

    // ── 壁纸来源(二期改为 WallpaperSource:自动识别第三方动态壁纸软件) ──

    /// <summary>取「铺满虚拟桌面」的壁纸图。实际来源见 <see cref="WallpaperSource"/> ——
    /// 系统静态壁纸读文件、Wallpaper Engine 之类读壁纸本体文件或抓屏。</summary>
    static BitmapSource? LoadWallpaper() => WallpaperSource.GetDesktop();

    /// <summary>虚拟桌面在物理像素下的原点/尺寸(与壁纸图的像素空间配套)。</summary>
    static (int vx, int vy, int vw, int vh) Layout() => WallpaperSource.LayoutRect;

    // ── 预模糊缓存(按屏幕半径) ──

    static readonly Dictionary<int, BitmapSource> _blurCache = new();
    static readonly object _lock = new();
    static int _generation;

    /// <summary>壁纸图/模糊图的「世代号」,每次 <see cref="Invalidate"/> 自增。
    ///
    /// ponytail 2026-09-26(二期修订): **必须有这个号**。已经挂好的
    /// <see cref="WallpaperBackdropLayer"/> 原本只在「半径变了」时才去重取模糊图,于是
    /// 用户换了壁纸点「重新采样」时 —— 半径没变 → 图层继续用旧 ImageBrush → 屏幕上毫无
    /// 反应(用户实测反馈的 bug)。图层现在比对这个号,号变了就重取。</summary>
    public static int Generation => _generation;

    /// <summary>取得「壁纸铺满虚拟桌面 + 已按半径预模糊」的位图(1/Downscale 分辨率)。
    /// screenRadiusPx = 期望的屏幕模糊半径(物理像素);返回 null = 壁纸不可用。
    /// ponytail 2026-09-26(二期): 来源图已由 <see cref="WallpaperSource"/> 统一铺满虚拟桌面
    /// (并已降采样),这里不再自己算适配,直接 1:1 画进模糊画布。</summary>
    public static BitmapSource? GetBlurredDesktop(int screenRadiusPx)
    {
        var img = LoadWallpaper();
        if (img == null)
        {
            AcrylicHelper.SelfDrawnAvailable = WallpaperSource.HostDetected;   // 宿主在跑 → 只是这一帧没采到,别永久关掉
            DzTrace.Log("[WallpaperBackdrop] 壁纸不可用 → 回退 DWM");
            return null;
        }
        var (_, _, vw, vh) = Layout();
        if (vw <= 0 || vh <= 0)
        {
            DzTrace.Log($"[WallpaperBackdrop] 虚拟桌面尺寸无效 {vw}x{vh} → 回退 DWM");
            return null;
        }
        AcrylicHelper.SelfDrawnAvailable = true;

        int key = Math.Max(0, screenRadiusPx);
        lock (_lock)
        {
            if (_blurCache.TryGetValue(key, out var cached)) return cached;
        }

        int dw = Math.Max(8, vw / Downscale);
        int dh = Math.Max(8, vh / Downscale);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, dw, dh));
            dc.DrawImage(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight));
        }
        double blur = key / (double)Downscale;
        if (blur > MinBlurPx)
        {
            var effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            effect.Freeze();
            visual.Effect = effect;
        }

        var rtb = new RenderTargetBitmap(dw, dh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        lock (_lock)
        {
            if (_blurCache.Count > MaxBlurCacheEntries) _blurCache.Clear(); // 半径档位很少,清空比 LRU 简单
            _blurCache[key] = rtb;
        }
        return rtb;
    }

    /// <summary>清缓存(壁纸变更/显示器拓扑变化/手动重采时调用)。
    /// 同时自增 <see cref="Generation"/> —— 已挂载的图层靠它察觉"图换了"。</summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _blurCache.Clear();
            _generation++;
        }
        WallpaperSource.Invalidate();
    }

    /// <summary>窗口在虚拟桌面里的裁剪 Viewbox(图像像素空间,已含降采样)。
    /// windowPx = 窗口的屏幕物理矩形(Win32 GetWindowRect 口径)。</summary>
    public static Rect GetCropViewbox(int screenRadiusPx, Rect windowPx)
    {
        var (vx, vy, _, _) = Layout();
        return new Rect(
            (windowPx.X - vx) / Downscale,
            (windowPx.Y - vy) / Downscale,
            Math.Max(1, windowPx.Width) / Downscale,
            Math.Max(1, windowPx.Height) / Downscale);
    }

    /// <summary>一次性静态背板画刷(裁剪图 + 着色 + 可选噪点) —— 给不移动的表面用(次级分区浮层),
    /// 避免为它维护"窗口矩形→Viewbox"的绑定。</summary>
    public static Brush? CreateStaticBrush(int screenRadiusPx, Rect windowPx, Color tint, double noiseOpacity)
    {
        var desk = GetBlurredDesktop(screenRadiusPx);
        if (desk == null) return null;
        var (vx, vy, _, _) = Layout();

        int x = (int)Math.Round((windowPx.X - vx) / Downscale);
        int y = (int)Math.Round((windowPx.Y - vy) / Downscale);
        int w = (int)Math.Max(1, Math.Round(Math.Max(1, windowPx.Width) / Downscale));
        int h = (int)Math.Max(1, Math.Round(Math.Max(1, windowPx.Height) / Downscale));
        // 夹到图内,避免越界(窗口有一半在虚拟桌面外时)
        x = Math.Clamp(x, 0, Math.Max(0, desk.PixelWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, desk.PixelHeight - 1));
        w = Math.Min(w, desk.PixelWidth - x);
        h = Math.Min(h, desk.PixelHeight - y);
        if (w <= 0 || h <= 0) return null;

        var crop = new CroppedBitmap(desk, new Int32Rect(x, y, w, h));
        crop.Freeze();

        var group = new DrawingGroup();
        group.Children.Add(new ImageDrawing(crop, new Rect(0, 0, w, h)));
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(tint), null, new RectangleGeometry(new Rect(0, 0, w, h))));
        if (noiseOpacity > 0.001)
        {
            // ponytail 2026-09-26(二期修订): **必须真的把 noiseOpacity 用上**。原先直接把
            // GeometryDrawing 塞进 group,强度参数完全没生效 —— NoiseBrush 的颗粒 alpha 是
            // 255(二值黑白),于是浮层被一层**满强度**噪点糊住,看上去就是"一张噪声图"
            // (用户实测反馈)。亚克力系材质的配方噪点只有 0.045~0.055,这里按它设整层透明度。
            // 对照:动态图层 WallpaperBackdropLayer.SetAppearance 里是 `_noise.Opacity = noiseOpacity`,
            // 那条路一直是对的,只有这个"一次性静态画刷"(浮层用)漏了。
            var noiseLayer = new DrawingGroup { Opacity = Math.Clamp(noiseOpacity, 0, 1) };
            noiseLayer.Children.Add(new GeometryDrawing(NoiseBrush, null, new RectangleGeometry(new Rect(0, 0, w, h))));
            noiseLayer.Freeze();
            group.Children.Add(noiseLayer);
        }
        group.Freeze();

        var brush = new DrawingBrush(group)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, w, h),
            ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewport = new Rect(0, 0, 1, 1),
        };
        brush.Freeze();
        return brush;
    }

    // ── 噪点纹理(亚克力系的"颗粒感"由我们自己造) ──

    static ImageBrush? _noiseBrush;

    /// <summary>128x128 程序生成噪点,平铺使用。用于给"亚克力系"材质补回 DWM 背板原本
    /// 自带的细颗粒(自绘背板没有系统噪点)。</summary>
    public static ImageBrush NoiseBrush
    {
        get
        {
            if (_noiseBrush != null) return _noiseBrush;
            const int S = 128;
            var bmp = new WriteableBitmap(S, S, 96, 96, PixelFormats.Bgra32, null);
            var px = new byte[S * S * 4];
            var rnd = new Random(20260926); // 固定种子:每次启动噪点一致,避免观感跳变
            for (int i = 0; i < S * S; i++)
            {
                byte v = (byte)rnd.Next(0, 256);
                byte a = (byte)(v > 128 ? 255 : 0); // 二值颗粒,叠在低透明度上最像亚克力
                px[i * 4 + 0] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = a;
            }
            bmp.WritePixels(new Int32Rect(0, 0, S, S), px, S * 4, 0);
            bmp.Freeze();
            var brush = new ImageBrush(bmp)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, S, S),
                Stretch = Stretch.None,
            };
            brush.Freeze();
            _noiseBrush = brush;
            return brush;
        }
    }
}

/// <summary>
/// 一个窗口的自绘背板层:三层(壁纸裁剪 → 着色 → 噪点)插在填充层之下。
/// 用法(每个窗口):
/// <code>
/// _backdrop ??= WallpaperBackdropLayer.TryCreate();          // 壁纸不可用 → null,继续走 DWM
/// if (_backdrop != null) { _backdrop.Attach(FillRect); _backdrop.Update(this, radius, tint, noise); }
/// else _backdrop?.Detach();
/// </code>
/// </summary>
public sealed class WallpaperBackdropLayer
{
    readonly Grid _root = new() { IsHitTestVisible = false };
    readonly System.Windows.Shapes.Rectangle _wallpaper = new() { IsHitTestVisible = false };
    readonly System.Windows.Shapes.Rectangle _tint = new() { IsHitTestVisible = false };
    readonly System.Windows.Shapes.Rectangle _noise = new() { IsHitTestVisible = false };
    Panel? _host;
    int _attachedRadius = -1;
    int _attachedGeneration = -1;
    Color _attachedTint = Colors.Transparent;
    double _attachedNoise = -1;
    Rect _attachedWindow = Rect.Empty;

    WallpaperBackdropLayer()
    {
        _root.Children.Add(_wallpaper);
        _root.Children.Add(_tint);
        _root.Children.Add(_noise);
        _noise.Opacity = 0;
    }

    /// <summary>壁纸可用就返回一个层实例(实际元素延迟到 <see cref="Attach"/> 才插进树)。</summary>
    public static WallpaperBackdropLayer? TryCreate()
    {
        bool ok = WallpaperBackdrop.GetBlurredDesktop(24) != null;
        DzTrace.Log($"[WallpaperBackdrop] TryCreate → {(ok ? "可用" : "不可用")}");
        return ok ? new WallpaperBackdropLayer() : null;
    }

    /// <summary>把三层插到 <paramref name="mainContent"/> 内部的**最底层**。幂等。
    /// ponytail 2026-09-26: 刻意挂在 MainContent 的根面板上而不是填充层所在面板 ——
    /// ① 背板要像 DWM 玻璃那样铺满整窗(时钟/日历的填充层只占内容行,照抄会让玻璃只剩一条);
    /// ② 挂在 MainContent 内部,收起/展开的缩放动画会自动带着背板一起走(顺手修掉"玻璃不跟动画")。
    /// mainContent 为 null 时退回填充层所在面板。</summary>
    public void Attach(Border? mainContent, FrameworkElement? fillRect)
    {
        var panel = FindRootPanel(mainContent) ?? fillRect?.Parent as Panel;
        if (panel == null)
        {
            DzTrace.Log($"[WallpaperBackdrop] Attach 失败: 找不到宿主面板 (mainContent={mainContent?.GetType().Name ?? "null"} fillParent={fillRect?.Parent?.GetType().Name ?? "null"})");
            return;
        }
        if (ReferenceEquals(_host, panel)) return;

        Detach();
        _host = panel;
        _root.Margin = new Thickness(0);
        Grid.SetRow(_root, 0);
        Grid.SetRowSpan(_root, 64); // 超出实际行数会被 WPF 夹到可用行数
        panel.Children.Insert(0, _root);
        DzTrace.Log($"[WallpaperBackdrop] Attach ok: panel={panel.GetType().Name} children={panel.Children.Count} attached={ReferenceEquals(panel.Children[0], _root)}");
    }

    /// <summary>MainContent 往下最多 3 层的第一个 Panel(各窗口层级: MainContent > (Border) > Grid)。</summary>
    static Panel? FindRootPanel(DependencyObject? node, int depth = 0)
    {
        if (node == null || depth > 3) return null;
        if (node is Panel p && p.Children.Count > 0) return p;
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
        {
            var found = FindRootPanel(VisualTreeHelper.GetChild(node, i), depth + 1);
            if (found != null) return found;
        }
        if (node is ContentControl cc && cc.Content is DependencyObject cd) return FindRootPanel(cd, depth + 1);
        if (node is Decorator) return null;
        return null;
    }

    /// <summary>显隐(收起态/玻璃关闭时隐藏;它本来就在 MainContent 里,缩放动画会带着它走)。</summary>
    public void SetVisible(bool visible)
        => _root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public void Detach()
    {
        _host?.Children.Remove(_root);
        _host = null;
    }

    /// <summary>更新裁剪/半径/着色/噪点。window 为宿主窗口;参数与模型里的玻璃参数一一对应。</summary>
    public void Update(Window window, int blurRadiusPx, Color tint, double noiseOpacity)
    {
        BindWindow(window);
        SetAppearance(window, blurRadiusPx, tint, noiseOpacity);
    }

    Window? _bound;

    /// <summary>订阅窗口的移动/缩放 —— 背板只需要改 Viewbox,不重解码不重模糊。</summary>
    public void BindWindow(Window window)
    {
        if (ReferenceEquals(_bound, window)) return;
        if (_bound != null)
        {
            _bound.LocationChanged -= OnWindowGeometryChanged;
            _bound.SizeChanged -= OnWindowGeometryChanged;
        }
        _bound = window;
        window.LocationChanged += OnWindowGeometryChanged;
        window.SizeChanged += OnWindowGeometryChanged;
    }

    void OnWindowGeometryChanged(object? sender, EventArgs e) => RefreshViewbox();

    /// <summary>窗口移动/缩放后只刷新裁剪框(裁剪图/模糊图都不动)。</summary>
    public void RefreshViewbox()
    {
        if (_host == null || _bound == null) return;
        if (_wallpaper.Fill is not ImageBrush ib) return;
        var rect = WindowPx(_bound);
        if (rect.IsEmpty || rect == _attachedWindow) return;
        ib.Viewbox = WallpaperBackdrop.GetCropViewbox(_attachedRadius, rect);
        _attachedWindow = rect;
    }

    /// <summary>设置半径/着色/噪点(半径变化才重取模糊图;窗口移动只刷新 Viewbox)。</summary>
    public void SetAppearance(Window window, int blurRadiusPx, Color tint, double noiseOpacity)
    {
        if (_host == null)
        {
            DzTrace.Log("[WallpaperBackdrop] SetAppearance 跳过: 未挂载");
            return;
        }
        int radius = Math.Max(0, blurRadiusPx);
        var rect = WindowPx(window);
        if (rect.IsEmpty)
        {
            DzTrace.Log("[WallpaperBackdrop] SetAppearance 跳过: 窗口矩形为空(句柄未就绪?)");
            return;
        }

        // ponytail 2026-09-26(二期修订): 除了"半径变了",**世代号变了也要重取** ——
        // 否则「重新采样」在一模一样的半径下拿不到新图(用户实测反馈的 bug:换壁纸后点
        // 重新采样毫无反应)。
        int gen = WallpaperBackdrop.Generation;
        if (radius != _attachedRadius || gen != _attachedGeneration || _wallpaper.Fill == null)
        {
            var desk = WallpaperBackdrop.GetBlurredDesktop(radius);
            DzTrace.Log($"[WallpaperBackdrop] SetAppearance r={radius} gen={gen} desk={(desk == null ? "NULL" : $"{desk.PixelWidth}x{desk.PixelHeight}")} rect={rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0} tint=#{tint.A:X2}{tint.R:X2}{tint.G:X2}{tint.B:X2} noise={noiseOpacity:F3}");
            if (desk == null) return;
            var brush = new ImageBrush(desk)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.Absolute,
                ViewportUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewport = new Rect(0, 0, 1, 1),
            };
            _wallpaper.Fill = brush;
            _attachedRadius = radius;
            _attachedGeneration = gen;
            _attachedWindow = Rect.Empty; // 新图 → Viewbox 必须重设
        }

        if (_wallpaper.Fill is ImageBrush ib && rect != _attachedWindow)
        {
            ib.Viewbox = WallpaperBackdrop.GetCropViewbox(radius, rect);
            _attachedWindow = rect;
        }

        if (tint != _attachedTint)
        {
            var b = new SolidColorBrush(tint);
            b.Freeze();
            _tint.Fill = b;
            _attachedTint = tint;
        }

        if (Math.Abs(noiseOpacity - _attachedNoise) > 0.001)
        {
            _noise.Fill = WallpaperBackdrop.NoiseBrush;
            _noise.Opacity = Math.Max(0, Math.Min(1, noiseOpacity));
            // 0 = 完全关掉(别让 WPF 每帧去栅格化一张平铺噪点)
            _noise.Visibility = _noise.Opacity > 0.001 ? Visibility.Visible : Visibility.Collapsed;
            _attachedNoise = noiseOpacity;
        }
    }

    /// <summary>窗口的物理像素矩形(Win32 口径,与虚拟桌面同坐标系)。</summary>
    static Rect WindowPx(Window window)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return Rect.Empty;
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return Rect.Empty;
            return new Rect(r.Left, r.Top, Math.Max(1, r.Right - r.Left), Math.Max(1, r.Bottom - r.Top));
        }
        catch { return Rect.Empty; }
    }
}
