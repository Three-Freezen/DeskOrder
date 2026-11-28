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

    /// <summary>预模糊缓存的**字节预算**(估算值,Pbgra32 = 4 字节/像素)。
    ///
    /// ponytail 2026-11-21(Phase 2 第 4 项): 上限从「条数」(原来 6 档)改成字节预算。
    /// 旧方案只按条数限,而一档的大小随分辨率差很多(1:1 下 1080p ≈ 8.3MB、4480×1600 ≈ 28.7MB
    /// → 6 档分别约 50MB / 172MB,是常驻内存的大头)。现在所有档位至少 1/2 采样
    /// (见 <see cref="BlurScale"/>),单档降到 1/4:1080p ≈ 2.1MB、2560×1440 ≈ 3.7MB、
    /// 4480×1600 ≈ 7.2MB。48MB 在 4480×1600 下约等于 6~7 档(43~50MB);1080p 下字节预算碰不到,
    /// 由下面的条数兜底先卡住(8 档 ≈ 16.6MB)。峰值/常驻都被压在几十 MB。
    /// 关掉渲染方案 / 重新采样 / 显示器变化时整块清空。</summary>
    const long BlurCacheBudgetBytes = 48L * 1024 * 1024;

    /// <summary>条数兜底。分辨率很小时 48MB 能塞几十张位图,而每张位图对象在 WPF 侧还有
    /// 额外开销,所以再压一个条数上限。预模糊档位总共 20 个(0..8 逐像素 + 9 以上按 5 归档),
    /// 8 档足够覆盖常用材质预设的半径,又不会让缓存字典无限长。
    ///
    /// ponytail 2026-09-26(审计修订): 淘汰策略从"满了就 Clear()"改成 **LRU 淘汰一条**。
    /// 实测每次未命中要重算一次全桌面预模糊(半径 35 ≈ 690ms、60 ≈ 1136ms),Clear() 会让
    /// **所有**窗口在下次刷新时同时重算(5 个窗口 ≈ 3 秒顿卡);LRU 只牺牲最久没用过的那一档。</summary>
    const int MaxBlurCacheEntries = 8;

    /// <summary>半径量化步长 —— 只在 <see cref="QuantizeBelow"/> 之上生效。
    /// ponytail 2026-09-26(审计修订): 每遇到一个没缓存过的半径就要重算整块桌面的预模糊
    /// (实测 509ms@24px、1136ms@60px),而拖一次模糊滑块会**逐个整数**走过去(实测连拖
    /// 40 档合计 23.7 秒的 UI 线程阻塞)。把大半径归到 5px 一档:档位数从 61 降到 20,
    /// 拖动时同一档直接命中缓存;而背板本来就是糊的,32px 与 35px 肉眼无法分辨。</summary>
    const int RadiusStep = 5;

    /// <summary>小于等于此半径**不量化**。小半径是肉眼能分辨的区间(0 → 5px 的差别看得
    /// 出来),所以 0..8 逐像素精确,9 以上才按 <see cref="RadiusStep"/> 归档。</summary>
    const int QuantizeBelow = 8;

    /// <summary>**大于等于**此档位的半径改用「四分之一分辨率预模糊」。
    ///
    /// ponytail 2026-09-26(用户反馈"拖模糊滑块会卡一下" + 实测):
    /// 用户拖滑块时真正卡的不是对话框(实测真对话框逐格拖 26 步只花 0.6ms),而是**每跨过
    /// 一个量化档位就要同步重算一次全桌面预模糊**;而材质预设的半径(25/30/35/45/60)恰好都
    /// 落在 5px 档位边界上,所以感觉是"拖到材质预设那里就卡一下"。实测(4960×1600):
    ///   1:1 → 363~1193ms;1/2 → 80~172ms;**1/4 → 20~47ms**
    /// 保真度(同一窗口区域逐像素差,对比 1:1):1/2 平均 0.22~0.31/255、最大 ≤6/255(r≥5);
    /// 1/4 在 r=10~15 最大差 19~31/255(**高对比边缘看得出来**),r≥20 才降到 ≤10/255。
    /// 所以:0..19 用 1/2,≥20 用 1/4。
    ///
    /// Phase 2 第 4 项: 0..4 档从「1:1」也改成 1/2。旧注释记的实测是 r=3 时 1/2 与 1:1
    /// 最大差 22/255(高对比边缘看得出),但高斯核尺度无关 —— 在 1/2 画布上 blur=半径/2、
    /// 再放大回显示尺寸,模糊半径本身没变,观感只是略软一点点;而背板上面还叠着着色/噪点。
    /// 换来的是内存大头消失:小半径档单张从 8.3MB 降到 2.1MB(@1080p),见 <see cref="BlurScale"/>。</summary>
    const int QuarterScaleFrom = 20;

    /// <summary>位图相对虚拟桌面的采样倍率:2(半分辨率)或 4(四分之一)。
    ///
    /// 高斯核尺度无关:在 1/N 的画布上用 radius/N 模糊、再放大回原尺寸,与全分辨率模糊
    /// 等价(只差一次重采样)。而这块背板本来就是糊的 —— 半径越大越没细节可丢,
    /// 所以倍率可以随半径放大,阈值由上面的实测差值定。
    ///
    /// Phase 2 第 4 项: **最小倍率从 1 改到 2**。1:1 档只服务 0..4 这几个小半径,却是缓存里
    /// 最占内存的一组(单张是半分辨率档的 4 倍)。取舍:放大回显示尺寸时多一次线性重采样,
    /// 极小半径(1~2px)在 200% 缩放的屏幕上看会比 1:1 略软一点点 —— 但背板不是正文,
    /// 换来峰值/常驻降 3/4(1080p 六档 50MB → 12MB,4480×1600 172MB → 43MB)。</summary>
    static int BlurScale(int quantizedRadius)
        => quantizedRadius < QuarterScaleFrom ? 2 : 4;

    /// <summary>位图相对虚拟桌面的采样倍率(1/2/4)—— **从位图自身推导**,不依赖全局常量。
    /// ponytail 2026-09-26(审计修订 B): 这是"两处必须一致"那个隐患的根治办法 ——
    /// 裁剪 Viewbox 直接用拿到的这张图算缩放,而不是去问一个可能已经变了的常量。</summary>
    public static double ScaleOf(BitmapSource desk)
    {
        var (_, _, vw, _) = Layout();
        if (vw <= 0 || desk.PixelWidth <= 0) return Downscale;
        return Math.Max(1, Math.Round(vw / (double)desk.PixelWidth));
    }

    /// <summary>模型半径(0-60)→ 预模糊档位。0..8 原样,9 以上取最近的 5 的倍数。</summary>
    public static int QuantizeRadius(int screenRadiusPx)
    {
        int r = Math.Max(0, screenRadiusPx);
        return r <= QuantizeBelow ? r : (int)Math.Round(r / (double)RadiusStep) * RadiusStep;
    }

    // ── 壁纸来源(二期改为 WallpaperSource:自动识别第三方动态壁纸软件) ──

    /// <summary>取「铺满虚拟桌面」的壁纸图。实际来源见 <see cref="WallpaperSource"/> ——
    /// 系统静态壁纸读文件、Wallpaper Engine 之类读壁纸本体文件或抓屏。</summary>
    static BitmapSource? LoadWallpaper() => WallpaperSource.GetDesktop();

    /// <summary>虚拟桌面在物理像素下的原点/尺寸(与壁纸图的像素空间配套)。</summary>
    static (int vx, int vy, int vw, int vh) Layout() => WallpaperSource.LayoutRect;

    // ── 预模糊缓存(按屏幕半径 → LRU,按字节预算淘汰) ──

    /// <summary>档位 → (位图, 估算字节, 最近使用时间)。字节数在插入时算一次,淘汰时直接减,
    /// 不用每次遍历位图问大小。</summary>
    static readonly Dictionary<int, (BitmapSource Bitmap, long Bytes, long Used)> _blurCache = new();
    static readonly object _lock = new();

    /// <summary>当前缓存里位图的估算总字节(受 <see cref="_lock"/> 保护)。</summary>
    static long _blurCacheBytes;

    static long _useTick;
    static int _generation;

    // ponytail 2026-11-14(审计修订 P0-2): 后台档位计算。
    // 原来 GetBlurredDesktop 在 UI 线程同步 RenderTargetBitmap(作者实测 1:1 档 363~1193ms、
    // 1/2 档 80~172ms),拖模糊滑块每跨一档 UI 就冻一次。现在:冷启动第一张仍同步(保持首帧
    // 行为不变),之后缺档全部丢后台算;算完抛 TierReady,已挂载的背板图层自己回填。
    static readonly HashSet<int> _pendingTiers = new();

    /// <summary>某个模糊档位在后台算好了。**可能在后台线程触发**,订阅方自行切 Dispatcher。
    /// 已挂载的 <see cref="WallpaperBackdropLayer"/> 收到后按上次的半径/着色重取一次。</summary>
    public static event Action? TierReady;

    /// <summary>壁纸图/模糊图的「世代号」,每次 <see cref="Invalidate"/> 自增。
    ///
    /// ponytail 2026-09-26(二期修订): **必须有这个号**。已经挂好的
    /// <see cref="WallpaperBackdropLayer"/> 原本只在「半径变了」时才去重取模糊图,于是
    /// 用户换了壁纸点「重新采样」时 —— 半径没变 → 图层继续用旧 ImageBrush → 屏幕上毫无
    /// 反应(用户实测反馈的 bug)。图层现在比对这个号,号变了就重取。</summary>
    public static int Generation => _generation;

    /// <summary>当前预模糊缓存占用的估算字节数(PixelWidth*PixelHeight*4,不含 WPF 内部开销)。
    /// ponytail 2026-11-21(Phase 2 第 4 项): 缓存上限从"条数"改成"字节预算"后,靠这个读数
    /// 验证收口是否生效(调试/内存自检用,不参与渲染决策)。</summary>
    public static long BlurCacheBytes
    {
        get { lock (_lock) return _blurCacheBytes; }
    }

    /// <summary>壁纸当前是否可用(**不渲染、不模糊、不占缓存档位**)。
    /// ponytail 2026-09-26(审计修订): `WallpaperBackdropLayer.TryCreate` 原来用
    /// `GetBlurredDesktop(24)` 探测可用性 —— 冷启动实测 675ms,还往缓存里塞一张半径 24 的
    /// 位图,而那个窗口自己的半径可能根本不是 24(白占一档,把 LRU 挤掉)。探测只需要
    /// "壁纸读得到 + 虚拟桌面尺寸正常"这两个条件。</summary>
    public static bool IsAvailable()
    {
        if (LoadWallpaper() == null)
        {
            // 宿主在跑 → 只是这一帧没采到,别永久关掉
            AcrylicHelper.SelfDrawnAvailable = WallpaperSource.HostDetected;
            DzTrace.Log("[WallpaperBackdrop] 壁纸不可用 → 回退 DWM");
            return false;
        }
        var (_, _, vw, vh) = Layout();
        if (vw <= 0 || vh <= 0)
        {
            DzTrace.Log($"[WallpaperBackdrop] 虚拟桌面尺寸无效 {vw}x{vh} → 回退 DWM");
            return false;
        }
        AcrylicHelper.SelfDrawnAvailable = true;
        return true;
    }

    /// <summary>取得「壁纸铺满虚拟桌面 + 已按半径预模糊」的位图。
    /// screenRadiusPx = 期望的屏幕模糊半径(物理像素);返回 null = 壁纸不可用。
    /// 半径先过 <see cref="QuantizeRadius"/> 归档再缓存;采样倍率由 <see cref="BlurScale"/>
    /// 决定(0..19 半分辨率,≥20 四分之一),**调用方必须用 <see cref="ScaleOf"/> 反推缩放**,
    /// 不要假设这张图一定是 1:1。
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

        int key = QuantizeRadius(screenRadiusPx);

        // ponytail 2026-09-26(审计修订 B): 半径 0 = 完全不做模糊 → 这张"模糊图"就是壁纸本身。
        // 原来还要为此把整块桌面重画进一张 RenderTargetBitmap(实测 116ms、再常驻 27MB),
        // 纯浪费;直接把来源图交出去(它**已经**是铺满虚拟桌面、按显示器适配好的 1:1 图)。
        if (key <= 0) return img;

        lock (_lock)
        {
            if (_blurCache.TryGetValue(key, out var hit))
            {
                _blurCache[key] = (hit.Bitmap, hit.Bytes, ++_useTick);   // 命中 → 只刷新 LRU 时间戳,字节数不变
                return hit.Bitmap;
            }
        }

        // ponytail 2026-11-14(审计修订 P0-2): 缓存里**没有这一档**时不再在 UI 线程上渲。
        // 冷启动(缓存完全空,含换壁纸后 Invalidate 的第一张)仍同步算一次 —— 保持"第一帧就有
        // 玻璃"的历史行为;之后拖滑块跨档、切材质预设都走后台,先返回 null 让调用方保留旧档位图,
        // 算完由 TierReady 回填。作者实测同步渲染的代价:1:1 档 363~1193ms / 1:2 档 80~172ms。
        bool coldStart;
        lock (_lock) coldStart = _blurCache.Count == 0;
        if (coldStart) return ComputeBlurred(key, vw, vh, img, _generation);

        ScheduleTierCompute(key, vw, vh, img, _generation);
        return null;
    }

    /// <summary>真正的渲染+模糊。调用线程 = 执行线程（同步路径是 UI 线程，后台路径是线程池）。
    /// `img` 必须是已 Freeze 的位图（WallpaperSource/CaptureDesktop 都 Freeze 过）。
    /// <paramref name="generationAtRequest"/> = 发起这次计算时的世代号:渲染期间若被
    /// <see cref="Invalidate"/> 作废(换壁纸/显示器变化/关方案),结果**仍返回给调用方**但不再入缓存。</summary>
    static BitmapSource ComputeBlurred(int key, int vw, int vh, BitmapSource img, int generationAtRequest)
    {
        int scale = BlurScale(key);
        int dw = Math.Max(8, vw / scale);
        int dh = Math.Max(8, vh / scale);

        var visual = new DrawingVisual();
        // 降采样用高质量滤波:实测与默认线性同价(38 vs 41 ms@1/4),但消掉降采样的锯齿。
        // Phase 2 第 4 项: 现在所有档位都至少 1/2 采样(scale ≥ 2),所以无条件开高质量。
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, dw, dh));
            dc.DrawImage(img, new Rect(0, 0, dw, dh));   // 降采样档位:这里顺带完成缩放
        }
        double blur = key / (double)scale;
        if (blur > MinBlurPx)
        {
            var effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
            effect.Freeze();
            visual.Effect = effect;
        }

        var rtb = new RenderTargetBitmap(dw, dh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        // Pbgra32 = 4 字节/像素。这个估算不含 WPF 内部的额外开销(对 48MB 级预算来说可忽略)。
        long bytes = (long)rtb.PixelWidth * rtb.PixelHeight * 4;
        lock (_lock)
        {
            // ponytail 2026-11-21(Phase 2 第 4 项): 渲染这段时间里缓存可能已被 Invalidate 过
            // (关方案/换壁纸/显示器变化)。旧实现只在任务**开始**时查世代号,算出结果的最后一步
            // 仍会把旧世代的位图塞回刚清空的缓存 —— 关掉方案后还以为"缓存已经空了",其实又常驻
            // 了一档(最多 7MB)。世代号不一致 → 结果照常返回给当前调用方,但**不入缓存**。
            if (generationAtRequest != _generation) return rtb;
            // Phase 2 第 4 项: 插入前先按**字节预算**淘汰(新条目本身也计入),再按条数兜底。
            // 旧实现只在"条数满了"时淘汰一条,分辨率一高 6 档就是上百 MB。
            // 这里把两条上限都收敛到"插入后仍满足",所以循环式淘汰。
            while (_blurCacheBytes + bytes > BlurCacheBudgetBytes && EvictLeastRecentlyUsed()) { }
            while (_blurCache.Count >= MaxBlurCacheEntries && EvictLeastRecentlyUsed()) { }
            _blurCache[key] = (rtb, bytes, ++_useTick);
            _blurCacheBytes += bytes;
        }
        return rtb;
    }

    /// <summary>把「缺这一档模糊图」丢到后台算；算完抛 <see cref="TierReady"/>。
    /// 幂等：同一档位只会有一个计算在跑（_pendingTiers）。</summary>
    static void ScheduleTierCompute(int key, int vw, int vh, BitmapSource img, int generationAtRequest)
    {
        lock (_lock)
        {
            if (_blurCache.ContainsKey(key) || !_pendingTiers.Add(key)) return;
        }
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            bool ok = false;
            try
            {
                // 期间换了壁纸 / 显示器拓扑变化 → 结果作废（世代号变了）
                if (generationAtRequest != _generation) return;
                if (vw <= 0 || vh <= 0) return;
                ComputeBlurred(key, vw, vh, img, generationAtRequest);
                ok = true;
            }
            catch (Exception ex)
            {
                DzTrace.Log($"[WallpaperBackdrop] 后台档位计算失败: {ex.GetType().Name} {ex.Message}");
            }
            finally
            {
                lock (_lock) _pendingTiers.Remove(key);
            }
            // 只有成功才通知回填 —— 失败也通知会形成「回填 → 缺档 → 再调度 → 再失败」的忙循环。
            if (ok) { try { TierReady?.Invoke(); } catch { } }
        });
    }

    /// <summary>淘汰最久未使用的一档(调用方必须持有 <see cref="_lock"/>)。
    /// 档位最多 8 个,线性找最小即可,不值得上 OrderedDictionary。
    /// 返回 true = 真的淘汰了一条(顺带从 <see cref="_blurCacheBytes"/> 里扣掉它的估算字节);
    /// 返回 false = 缓存已空,调用方的预算/条数循环应当停下。</summary>
    static bool EvictLeastRecentlyUsed()
    {
        int victim = -1;
        long oldest = long.MaxValue;
        foreach (var kv in _blurCache)
            if (kv.Value.Used < oldest) { oldest = kv.Value.Used; victim = kv.Key; }
        if (victim < 0) return false;
        _blurCacheBytes -= _blurCache[victim].Bytes;
        _blurCache.Remove(victim);
        return true;
    }

    /// <summary>清缓存(壁纸变更/显示器拓扑变化/手动重采时调用)。
    /// 同时自增 <see cref="Generation"/> —— 已挂载的图层靠它察觉"图换了"。
    /// 线程安全:`SystemEvents.DisplaySettingsChanged` 会在别的线程上回调到这里。
    /// ponytail 2026-11-01(第 7 轮 A): 顺带抛 <see cref="Changed"/> —— 已挂载的图层自己
    /// 重新取图,不必等到"窗口被拖动/重开"才更新(这就是"换壁纸后玻璃一直是旧图"的根治)。</summary>
    public static void Invalidate()
    {
        lock (_lock)
        {
            _blurCache.Clear();
            _blurCacheBytes = 0;     // Phase 2 第 4 项: 字节计数必须跟着清,否则预算会永久虚占
            _pendingTiers.Clear();   // ponytail 2026-11-14: 旧世代的在算任务作废（结果按世代号丢弃）
            _generation++;
        }
        WallpaperSource.Invalidate();
        try { Changed?.Invoke(); } catch { }
    }

    /// <summary>壁纸来源/缓存被作废了(换壁纸、点「重新采样」、显示器拓扑变化)。
    /// 已挂载的图层订阅它自行重取；可能在**非 UI 线程**触发，订阅方要自己切 Dispatcher。</summary>
    public static event Action? Changed;

    /// <summary>窗口在虚拟桌面里的裁剪 Viewbox(图像像素空间)。
    /// windowPx = 窗口的屏幕物理矩形(Win32 GetWindowRect 口径)。
    ///
    /// ponytail 2026-09-26(审计修订 B): 缩放倍数**从这张位图自身推导**(见
    /// <see cref="ScaleOf"/>),不再读全局常量 —— 大半径档位给的是半分辨率图,
    /// 拿常量算裁剪会整体偏一倍。</summary>
    public static Rect GetCropViewbox(BitmapSource desk, Rect windowPx)
    {
        var (vx, vy, _, _) = Layout();
        double scale = ScaleOf(desk);
        return new Rect(
            (windowPx.X - vx) / scale,
            (windowPx.Y - vy) / scale,
            Math.Max(1, windowPx.Width) / scale,
            Math.Max(1, windowPx.Height) / scale);
    }

    /// <summary>一次性静态背板画刷(裁剪图 + 着色 + 可选噪点) —— 给不移动的表面用(次级分区浮层),
    /// 避免为它维护"窗口矩形→Viewbox"的绑定。
    /// 裁剪图可能是半分辨率的(大半径),由 DrawingBrush 的 Stretch 放大回浮层尺寸 ——
    /// 背板本来就是糊的,看不出差别;唯一的副作用是**噪点颗粒**会跟着放大一倍
    /// (噪点层和裁剪图在同一个坐标系里),在 0.045~0.055 的强度下不可见。</summary>
    public static Brush? CreateStaticBrush(int screenRadiusPx, Rect windowPx, Color tint, double noiseOpacity)
    {
        var desk = GetBlurredDesktop(screenRadiusPx);
        if (desk == null) return null;
        var (vx, vy, _, _) = Layout();
        double scale = ScaleOf(desk);

        int x = (int)Math.Round((windowPx.X - vx) / scale);
        int y = (int)Math.Round((windowPx.Y - vy) / scale);
        int w = (int)Math.Max(1, Math.Round(Math.Max(1, windowPx.Width) / scale));
        int h = (int)Math.Max(1, Math.Round(Math.Max(1, windowPx.Height) / scale));
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

    /// <summary>最近一次**请求**的半径（物理像素）。某档模糊图还在后台算时
    /// <see cref="_attachedRadius"/> 不会前进，回填时要用这个值重试（见 OnTierReady）。</summary>
    int _wantedRadius = -1;

    WallpaperBackdropLayer()
    {
        _root.Children.Add(_wallpaper);
        _root.Children.Add(_tint);
        _root.Children.Add(_noise);
        _noise.Opacity = 0;
        // 壁纸换了就自己重取（换壁纸/重新采样/显示器拓扑变化都走 WallpaperBackdrop.Invalidate）。
        // 生命周期：窗口关掉时可视树 Unloaded → 退订，避免静态事件把整棵树钉住。
        WallpaperBackdrop.Changed += OnWallpaperChanged;
        // ponytail 2026-11-14(审计修订 P0-2): 后台算好某个模糊档位 → 回来重挂一次。
        WallpaperBackdrop.TierReady += OnTierReady;
        _root.Unloaded += OnRootUnloaded;
    }

    void OnRootUnloaded(object sender, RoutedEventArgs e)
    {
        _root.Unloaded -= OnRootUnloaded;
        WallpaperBackdrop.Changed -= OnWallpaperChanged;
        WallpaperBackdrop.TierReady -= OnTierReady;
    }

    /// <summary>壁纸换了：按上次的半径/着色/噪点重取（世代号一变 SetAppearance 就会重裁）。
    /// 事件可能在非 UI 线程触发，这里切回宿主线程。</summary>
    void OnWallpaperChanged()
    {
        var dispatcher = _root.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                // ponytail 2026-11-21(Phase 2 第 4 项): 自绘方案已切回 DWM 时**不要再重采样**。
                // 关开关时窗口只是 SetVisible(false) 把图层藏起来、仍然订阅着 Changed;不挡的话
                // 刚被 Enabled=false 清掉的采集/模糊缓存会被这些"看不见的图层"立刻重新填满
                // (整块桌面图 + 一档模糊图,几十 MB),关开关等于没关。重新打开方案时窗口会自己
                // ApplyStyle → SetAppearance 主动取图,不需要这里保活。
                if (!WallpaperSource.Enabled) return;
                if (_host == null || _bound == null || _attachedRadius < 0) return;
                SetAppearance(_bound, _attachedRadius, _attachedTint, Math.Max(0, _attachedNoise));
            }));
        }
        catch { }
    }

    /// <summary>后台模糊档位算好了：用「期望半径」重试一次（命中的话走 SetAppearance 的缓存分支）。
    /// ponytail 2026-11-14(P0-2): 可能在后台线程触发 → 切回宿主 Dispatcher。</summary>
    void OnTierReady()
    {
        var dispatcher = _root.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        try
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!WallpaperSource.Enabled) return;   // 同 OnWallpaperChanged:关掉方案后不再回填缓存
                if (_host == null || _bound == null || _wantedRadius < 0) return;
                SetAppearance(_bound, _wantedRadius, _attachedTint, Math.Max(0, _attachedNoise));
            }));
        }
        catch { }
    }

    /// <summary>壁纸可用就返回一个层实例(实际元素延迟到 <see cref="Attach"/> 才插进树)。</summary>
    public static WallpaperBackdropLayer? TryCreate()
    {
        bool ok = WallpaperBackdrop.IsAvailable();
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

    /// <summary>把一个**指定的**面板当宿主(快捷栏用:胶囊那块 Grid)。
    /// 与 <see cref="Attach"/> 的区别是不去猜根面板 —— 快捷栏的窗口里还有放大带/倒影带,
    /// 猜错一格图层就铺到胶囊外面去了。幂等。</summary>
    public void AttachTo(Panel host)
    {
        if (host == null) { DzTrace.Log("[WallpaperBackdrop] AttachTo 跳过: host=null"); return; }
        if (ReferenceEquals(_host, host)) return;
        Detach();
        _host = host;
        _root.Margin = new Thickness(0);
        host.Children.Insert(0, _root);
        DzTrace.Log($"[WallpaperBackdrop] AttachTo ok: panel={host.GetType().Name} children={host.Children.Count}");
    }

    /// <summary>自测用：图层内部状态摘录（半径/世代/着色/取到的位图/裁剪框）。</summary>
    public string DebugState()
    {
        string desk = _wallpaper.Fill is ImageBrush ib && ib.ImageSource is BitmapSource b
            ? $"{b.PixelWidth}x{b.PixelHeight}" : "none";
        string viewbox = _wallpaper.Fill is ImageBrush ib2
            ? $"{ib2.Viewbox.X:F0},{ib2.Viewbox.Y:F0} {ib2.Viewbox.Width:F0}x{ib2.Viewbox.Height:F0}" : "-";
        return $"hosted={_host != null} bound={(_bound != null)} radius={_attachedRadius} gen={_attachedGeneration} " +
               $"tint=#{_attachedTint.A:X2}{_attachedTint.R:X2}{_attachedTint.G:X2}{_attachedTint.B:X2} " +
               $"noise={_attachedNoise:F3} desk={desk} viewbox={viewbox} " +
               $"inset={(_sourceInset is { } i ? $"{i.X:F0},{i.Y:F0} {i.Width:F0}x{i.Height:F0}" : "window")} " +
               $"visible={_root.Visibility}";
    }

    /// <summary>显隐(收起态/玻璃关闭时隐藏;它本来就在 MainContent 里,缩放动画会带着它走)。</summary>
    public void SetVisible(bool visible)
        => _root.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>只显示图层的一块(宿主局部坐标,DIP);null = 恢复整窗。
    /// 磁贴圆形用:壁纸 Viewbox 仍按整窗裁剪,所以圆内纹理对齐不受影响,只是矩形角被裁掉。</summary>
    public void SetClip(Geometry? clip)
    {
        if (clip != null && clip.CanFreeze && !clip.IsFrozen) clip.Freeze();
        if (ReferenceEquals(_root.Clip, clip)) return;
        _root.Clip = clip;
    }

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

    /// <summary>「壁纸裁剪源」在窗口内偏移/尺寸(物理像素)。null = 整窗(分区/时钟/日历/便签/面板)。
    /// ponytail 2026-11-01(第 7 轮 A)：快捷栏的图层只铺在**可见胶囊**里，而壁纸必须按胶囊的
    /// 屏幕矩形裁 —— 否则玻璃里透出的是整窗那一块(含上方放大带/下方倒影带)，位置整体偏上。
    /// 存的是"窗口内偏移"而不是屏幕矩形：这样窗口一移动，源矩形跟着走，不用重新计算。</summary>
    Rect? _sourceInset;

    /// <summary>设定「壁纸裁剪源 = 窗口内这一块」。null = 恢复整窗。</summary>
    public void SetSourceInset(Rect? windowLocalPx)
    {
        if (Nullable.Equals(_sourceInset, windowLocalPx)) return;
        _sourceInset = windowLocalPx;
        _attachedWindow = Rect.Empty;   // 源变了 → 下一次一定要重设 Viewbox
        RefreshViewbox();
    }

    /// <summary>当前的裁剪源(屏幕物理像素)：整窗矩形按 <see cref="_sourceInset"/> 收窄。</summary>
    Rect SourcePx(Window window)
    {
        var r = WindowPx(window);
        if (r.IsEmpty || _sourceInset is not { } ins) return r;
        return new Rect(r.X + ins.X, r.Y + ins.Y,
                        Math.Max(1, ins.Width), Math.Max(1, ins.Height));
    }

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

    /// <summary>窗口移动/缩放后只刷新裁剪框(裁剪图/模糊图都不动)。
    /// ponytail 2026-11-01(第 7 轮 A)：顺带比对壁纸世代号 —— 换过壁纸(或点过「重新采样」)
    /// 之后哪怕只是挪了一下窗口，也按新图重裁；分区与快捷栏共用这一段，行为一致。</summary>
    public void RefreshViewbox()
    {
        if (_host == null || _bound == null) return;
        if (WallpaperBackdrop.Generation != _attachedGeneration)
        {
            if (_attachedRadius >= 0)
                SetAppearance(_bound, _attachedRadius, _attachedTint, Math.Max(0, _attachedNoise));
            return;
        }
        if (_wallpaper.Fill is not ImageBrush ib || ib.ImageSource is not BitmapSource desk) return;
        var rect = SourcePx(_bound);
        if (rect.IsEmpty || rect == _attachedWindow) return;
        ib.Viewbox = WallpaperBackdrop.GetCropViewbox(desk, rect);
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
        int radius = WallpaperBackdrop.QuantizeRadius(blurRadiusPx);
        _wantedRadius = blurRadiusPx;   // ponytail 2026-11-14: 供后台档位算好后回填
        var rect = SourcePx(window);
        if (rect.IsEmpty)
        {
            DzTrace.Log("[WallpaperBackdrop] SetAppearance 跳过: 窗口矩形为空(句柄未就绪?)");
            return;
        }

        // ponytail 2026-09-26(二期修订): 除了"半径变了",**世代号变了也要重取** ——
        // 否则「重新采样」在一模一样的半径下拿不到新图(用户实测反馈的 bug:换壁纸后点
        // 重新采样毫无反应)。
        // ponytail 2026-09-26(审计修订): 比的是**归档后**的半径,所以拖滑块在同一档内
        // 来回时连取图/建 ImageBrush 都省掉了(量化见 QuantizeRadius)。
        int gen = WallpaperBackdrop.Generation;
        if (radius != _attachedRadius || gen != _attachedGeneration || _wallpaper.Fill == null)
        {
            var desk = WallpaperBackdrop.GetBlurredDesktop(radius);
            DzTrace.Log($"[WallpaperBackdrop] SetAppearance r={blurRadiusPx}→{radius} gen={gen} desk={(desk == null ? "NULL" : $"{desk.PixelWidth}x{desk.PixelHeight}")} rect={rect.X:F0},{rect.Y:F0} {rect.Width:F0}x{rect.Height:F0} tint=#{tint.A:X2}{tint.R:X2}{tint.G:X2}{tint.B:X2} noise={noiseOpacity:F3}");
            if (desk == null)
            {
                // ponytail 2026-11-14(P0-2): 这一档还在后台算（GetBlurredDesktop 已负责调度）。
                // 保持当前已挂的旧档位图不动（不闪、不塌成透明），算完由 OnTierReady 用
                // _wantedRadius 重试；_attachedRadius 不前移，所以重试一定会重新进这个分支。
                return;
            }
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

        if (_wallpaper.Fill is ImageBrush ib && ib.ImageSource is BitmapSource deskBmp && rect != _attachedWindow)
        {
            ib.Viewbox = WallpaperBackdrop.GetCropViewbox(deskBmp, rect);
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
