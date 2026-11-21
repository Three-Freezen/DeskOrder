using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using Microsoft.Win32;

namespace DesktopZones.Services;

public class ShellIconService
{
    /// <summary>共享实例 — ZoneWindow / PanelWindow / ZoneManager 都从这里取图标，
    /// 这样 ZoneManager 在导入时预热的图标缓存能被渲染端直接命中。</summary>
    public static ShellIconService Instance { get; } = new();

    /// <summary>SHIL_JUMBO — the 256px source resolution the desktop draws icons from.
    /// 只作为**解码尺寸上限**和 jumbo 兜底路径的源尺寸；实际按调用方给的显示尺寸
    /// 解码（<see cref="SnapDecodeSize"/> 的少数几档），不再一律按 256 常驻内存。</summary>
    private const int JumboSize = 256;

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly ConcurrentDictionary<string, ImageSource?> _iconCache = new();

    /// <summary>每个缓存条目的估算字节数（key 与 _iconCache 一一对应）。</summary>
    private readonly ConcurrentDictionary<string, long> _iconCacheBytes = new();

    /// <summary>图标缓存字节预算。
    ///
    /// ponytail 2026-09-26(审计修订): 这个缓存原本**没有任何上限**,而它是进程级单例的 ——
    /// 256px 图标(BGRA)约 100~260 KB/条,几百个条目就是几十 MB 常驻且永不回落;自动整理
    /// 每导入一批新文件名还会继续涨。之后补过 512 条上限，但「条数」和「内存」不成比例：
    /// 256px 一条 256 KB，48px 一条只有 9 KB，同样 512 条内存差 20 多倍。现在直接按
    /// **字节预算**管（24 MB），超预算时仍按**插入顺序**淘汰最老的（近似 LRU:
    /// 精确 LRU 要给每次命中记账,不值得）。
    /// 淘汰只是丢掉缓存条目 —— 还在界面上显示的 ImageSource 由 UI 元素自己持有,不受影响。</summary>
    public const long MaxIconCacheBytes = 24 * 1024 * 1024;

    /// <summary>插入顺序(超预算淘汰用)。一个 key 每次真正入缓存时入队一次。</summary>
    private readonly ConcurrentQueue<string> _iconCacheOrder = new();

    /// <summary>缓存条目 ↔ 字节账本的互斥锁。读路径（ConcurrentDictionary.TryGetValue）不加锁；
    /// 写路径（入缓存 / 淘汰 / 清空）串行，否则「先移除条目、再减字节」中间被另一个线程插入
    /// 同一个 key 时，账本会和实际条目对不上（两个线程各减一次 / 加错量）。</summary>
    private readonly object _cacheLock = new();

    /// <summary>当前缓存估算字节数（与 _iconCache 同锁维护）。</summary>
    private long _cachedBytes;

    /// <summary>当前缓存估算字节数（调试 / 测量用）。</summary>
    public long CachedBytes => Interlocked.Read(ref _cachedBytes);

    /// <summary>当前缓存条目数（调试 / 测量用）。</summary>
    public int CachedCount => _iconCache.Count;

    /// <summary>通用文件夹图标按解码尺寸懒加载。原本是 static 一次的 256px 图 —— 请求
    /// 48/96 的调用方也得吃整张 256（内存差 5~28 倍，且完全没必要）。</summary>
    private static readonly ConcurrentDictionary<int, ImageSource?> _folderIcons = new();

    // ── 按显示尺寸解码（Phase 2：降内存 / 降解码成本） ──

    /// <summary>解码尺寸档位。显示尺寸 × DPI 后向上取整到这几档，保证缓存 key 只有 6 种
    /// 尺寸 —— 否则每个分区的 GridSize 都不同，同一张图标会被拆成几十份缓存。</summary>
    private static readonly int[] DecodeSizeSteps = { 48, 64, 96, 128, 192, 256 };

    /// <summary>把「显示 DIP × DPI 缩放」向上取整到少数几档解码尺寸。
    /// 例：65 DIP × 1.5 = 97.5 → 128。</summary>
    public static int SnapDecodeSize(double displayDip, double dpiScale)
    {
        if (double.IsNaN(displayDip) || displayDip < 0) displayDip = 0;
        if (double.IsNaN(dpiScale) || dpiScale <= 0) dpiScale = 1.0;
        double px = displayDip * dpiScale;
        foreach (var step in DecodeSizeSteps)
        {
            if (px <= step) return step;
        }
        return JumboSize;
    }

    /// <summary>系统 DPI 缩放（不知道窗口 DPI 的调用方用：ZoneManager 导入预热、PanelWindow
    /// 建卡片时窗口还没进可视树）。per-monitor 差异由窗口侧把自己的 DpiScale 传进来覆盖
    /// （如 <see cref="ViewModels.ZoneViewModel.DpiScale"/>）。只查一次 —— GetDpiForSystem 在
    /// 进程 DPI awareness 固定后不会变；失败 / 返回 0 时按 100%。</summary>
    private static readonly Lazy<double> _systemDpiScale = new(() =>
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForSystem();
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }
        catch { return 1.0; } // Win10 1607 之前没有这个入口 —— 按 100% 处理
    });

    public static double SystemDpiScale => _systemDpiScale.Value;

    /// <summary>防御性归一化：把任意 decodeSize 钳到 [48, 256] 并对齐到 <see cref="DecodeSizeSteps"/>。
    /// 即使调用方漏了 SnapDecodeSize（或传了奇数），缓存 key 也不会被打散成上百种尺寸。
    /// 256 档只在 decodeSize ≥ 192 时才会取到（与「只在需要时付 256 的代价」一致）。</summary>
    private static int CoerceDecodeSize(int decodeSize)
    {
        if (decodeSize <= DecodeSizeSteps[0]) return DecodeSizeSteps[0];
        foreach (var step in DecodeSizeSteps)
        {
            if (decodeSize <= step) return step;
        }
        return JumboSize;
    }

    // ── Recycle Bin state-aware icon ──

    public const string RecycleBinSpec = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private const string RecycleBinDefaultIconKey = @"CLSID\{645FF040-5081-101B-9F08-00AA002F954E}\DefaultIcon";

    private static DateTime _recycleCheckedUtc = DateTime.MinValue;
    private static bool _recycleFull;

    public static bool IsRecycleBin(string target) =>
        string.Equals(target, RecycleBinSpec, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the Recycle Bin currently contains at least one item. Cached for ~2s.</summary>
    public static bool RecycleBinHasItems()
    {
        if (DateTime.UtcNow - _recycleCheckedUtc > TimeSpan.FromSeconds(2))
        {
            _recycleFull = QueryRecycleBinCount() > 0;
            _recycleCheckedUtc = DateTime.UtcNow;
        }
        return _recycleFull;
    }

    /// <summary>Force the next RecycleBinHasItems call to re-query the shell (used right after emptying the bin).</summary>
    public static void InvalidateRecycleBinState() => _recycleCheckedUtc = DateTime.MinValue;

    private static long QueryRecycleBinCount()
    {
        try
        {
            var info = new NativeMethods.SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>() };
            return NativeMethods.SHQueryRecycleBinW(null, ref info) == 0 ? info.i64NumItems : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Icon for a zone item. When the item carries a shortcut icon location
    /// ("file,index" — the exact icon the desktop shows for that shortcut), it is
    /// extracted first; otherwise the target's own icon is used.
    /// <paramref name="decodeSize"/> 是解码边长（像素档位，见 <see cref="SnapDecodeSize"/>）：
    /// 默认 128 给不知道显示尺寸的调用方；缓存 key 带尺寸，不同尺寸互不覆盖。
    /// </summary>
    public ImageSource? GetIcon(string path, Models.ItemType type, string? iconLocation = null, int decodeSize = 128)
    {
        decodeSize = CoerceDecodeSize(decodeSize);
        if (type == Models.ItemType.ShellLocation && IsRecycleBin(path))
            return GetRecycleBinStateIcon(decodeSize);

        string cacheKey = $"{decodeSize}|{iconLocation ?? path}";
        if (_iconCache.TryGetValue(cacheKey, out var cached))
            return cached;

        ImageSource? src = null;
        if (iconLocation != null)
            src = GetIconLocationImage(iconLocation, decodeSize);
        src ??= type switch
        {
            Models.ItemType.ShellLocation => GetShellLocationIcon(path, decodeSize) ?? GetFolderIcon(decodeSize),
            Models.ItemType.Folder => GetSystemIcon(path, decodeSize) ?? GetFolderIcon(decodeSize),
            _ => GetSystemIcon(path, decodeSize)
        };
        // 主路径带 SIIGBF_BIGGERSIZEOK：shell 没有该尺寸时可能返回更大的位图。先按请求尺寸缩掉，
        // 否则一张 256 的图会按 48/96 的 key 塞进缓存，解码尺寸的收益就白丢了。
        src = DownscaleIfLarger(FreezeIfPossible(CropLetterboxed(src)), decodeSize);
        // CroppedBitmap / TransformedBitmap 会强引用源位图：jumbo 兜底/BIGGERSIZEOK 路径下源可能是
        // 256px，缓存账本按 96px 记，实际却整张 256 留在内存。这里拷成独立缓冲区，彻底断掉引用链。
        src = DetachBitmap(FreezeIfPossible(src));

        // 解析失败（文件刚创建仍在写入/被占用）时不要缓存 null — 否则监听
        // 导入的图标会永久空白；等文件就绪后的重试会再次解析并成功缓存。
        if (src != null)
            CacheIcon(cacheKey, src);
        return src;
    }

    // ── Image thumbnail (分区图片预览) ──

    /// <summary>分区图片预览开关（运行时由 App 启动 / 设置页同步）。</summary>
    public static bool ImagePreviewEnabled { get; set; }

    static readonly HashSet<string> _imageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tiff", ".tif", ".ico", ".svg"
    };

    /// <summary>文件是否为支持预览的图片格式（按扩展名判断）。</summary>
    public static bool IsImageFile(string path)
    {
        try { return _imageExts.Contains(Path.GetExtension(path)); }
        catch { return false; }
    }

    /// <summary>
    /// 加载图片文件的内容缩略图（与资源管理器视图模式一致）。
    /// 使用 BitmapImage + DecodePixelWidth 按需解码，避免加载全分辨率。
    /// 缓存键加 "thumb:" 前缀与普通图标缓存隔离；**必须带 decodeWidth** ——
    /// 原来固定 "thumb:"+path，不同 decodeWidth 会互相顶掉（谁后写谁生效）。
    /// </summary>
    public ImageSource? GetImageThumbnail(string path, int decodeWidth = 128)
    {
        if (!IsImageFile(path) || !File.Exists(path)) return null;

        decodeWidth = CoerceDecodeSize(decodeWidth);
        string cacheKey = $"thumb:{decodeWidth}|{path}";
        if (_iconCache.TryGetValue(cacheKey, out var cached))
            return cached;

        ImageSource? src = null;
        try
        {
            // 用 FileStream 加载本地路径（new Uri("C:\...") 会把盘符解析成 scheme 而失败），
            // FileShare.Read 允许图片正被其它程序（资源管理器等）读取时也能打开。
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = fs;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.EndInit();
            bmp.Freeze(); // 跨线程安全
            src = bmp;
        }
        catch
        {
            // 文件损坏 / 格式不支持 / 被占用 → 回退到默认图标。
        }

        if (src != null)
            CacheIcon(cacheKey, src);
        return src;
    }

    /// <summary>
    /// Extract an icon from an explicit "file,index" icon location (a shortcut's custom
    /// icon). Index 0 .ico files are extracted via IShellItemImageFactory — this keeps
    /// alpha/rounded corners intact even for PNG-compressed frames, which
    /// SHDefExtractIconW cannot decode. Non-zero indices (dll/icl icons) go through
    /// SHDefExtractIconW first.
    /// </summary>
    private static ImageSource? GetIconLocationImage(string location, int decodeSize)
    {
        try
        {
            int sep = location.LastIndexOf(',');
            if (sep <= 0 || sep >= location.Length - 1) return null;
            string file = location[..sep].Trim().Trim('"');
            if (!int.TryParse(location[(sep + 1)..].Trim(), out var index)) return null;
            file = Environment.ExpandEnvironmentVariables(file);
            if (!File.Exists(file)) return null;

            if (index == 0)
            {
                var hbm = ShellOle.GetItemIconBitmap(file, decodeSize);
                if (hbm != null)
                {
                    try
                    {
                        return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            hbm.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                    }
                    finally { NativeMethods.DeleteObject(hbm.Value); }
                }
            }

            uint sz = (uint)(decodeSize | (decodeSize << 16));
            if (NativeMethods.SHDefExtractIconW(file, index, 0, out var hl, out var hs, sz) > 0 && hl != IntPtr.Zero)
            {
                try
                {
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        hl, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                }
                finally
                {
                    NativeMethods.DestroyIcon(hl);
                    if (hs != IntPtr.Zero) NativeMethods.DestroyIcon(hs);
                }
            }

            var hbm2 = ShellOle.GetItemIconBitmap(file, decodeSize);
            if (hbm2 != null)
            {
                try
                {
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hbm2.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally { NativeMethods.DeleteObject(hbm2.Value); }
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Safety net: whatever extraction path produced the bitmap, if the actual icon art
    /// only occupies a small region of a much larger canvas, crop to the art bounds so
    /// WPF fills the display box with the real art.
    /// IMPORTANT: the art bound is measured at <see cref="CropAlphaThreshold"/> — many
    /// icons (e.g. small 60px art inside a 256px canvas) carry a wide soft glow/shadow
    /// whose alpha spans the whole canvas (verified: BCUT ioc / Riffstation have glow
    /// pixels up to alpha ~47 reaching the canvas edges). Measuring at alpha > 0 there
    /// sees a full canvas and never crops — the "small icon with a glow ring" symptom.
    /// Threshold 96 lands just outside the glow band and captures the solid art.
    /// Normal icons are unaffected (cmd 97%×80%, DeskOrder 88% at the same threshold).
    /// </summary>
    private static ImageSource? CropLetterboxed(ImageSource? src)
    {
        try
        {
            if (src is not BitmapSource bmp) return src;
            if (bmp.Format != PixelFormats.Bgra32 && bmp.Format != PixelFormats.Pbgra32
                && bmp.Format != PixelFormats.Bgr32)
                return src;
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w < 16 || h < 16) return src;

            int stride = w * 4;
            // ponytail 2026-11-14(性能 P1-4): 256×256 图标每张都 new 256KB 再逐像素全扫，
            // 300 个图标 = 77MB 分配 + 2000 万次循环，全堵在 UI 线程的绑定求值里。
            // 现在: ① 扫描缓冲用 ArrayPool 复用（逐像素读取不可避免，但不再进 GC）；
            //       ② 大图标按 step 抽样找 art 边界，循环量最多降 4 倍；边界按步长向外扩，
            //          保证 crop 只会比真实 art 略大、不会裁掉边缘（与旧行为视觉等价）。
            int step = w >= 128 && h >= 128 ? 2 : 1;
            var px = System.Buffers.ArrayPool<byte>.Shared.Rent(stride * h);
            try
            {
                bmp.CopyPixels(px, stride, 0);

                int minX = w, minY = h, maxX = -1, maxY = -1;
                for (int y = 0; y < h; y += step)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += step)
                    {
                        if (px[row + x * 4 + 3] < CropAlphaThreshold) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < minX || maxY < minY) return src; // no solid art — return as-is
                if (step > 1)
                {
                    // 抽样点代表 [x, x+step-1] 区间：向外扩一格，绝不把 art 边缘切掉。
                    minX = Math.Max(0, minX - (step - 1));
                    minY = Math.Max(0, minY - (step - 1));
                    maxX = Math.Min(w - 1, maxX + (step - 1));
                    maxY = Math.Min(h - 1, maxY + (step - 1));
                }

                int cw = maxX - minX + 1, ch = maxY - minY + 1;
                if (cw >= w * 0.85 && ch >= h * 0.85) return src; // art fills at least 85% of the canvas
                return new CroppedBitmap(bmp, new System.Windows.Int32Rect(minX, minY, cw, ch));
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(px);
            }
        }
        catch { return src; }
    }

    /// <summary>Alpha threshold separating solid icon art from its soft glow/shadow band.</summary>
    private const byte CropAlphaThreshold = 96;

    /// <summary>能冻结就冻结 —— 缓存里的 ImageSource 会被多个线程读（导入线程预热、UI 线程绑定），
    /// 冻结后跨线程访问安全；个别无法冻结的源保持原样（与改动前一致，也不影响显示）。</summary>
    private static ImageSource? FreezeIfPossible(ImageSource? src)
    {
        if (src == null) return src;
        try
        {
            if (src.CanFreeze) src.Freeze();
        }
        catch { }
        return src;
    }

    /// <summary>把比请求尺寸大的图等比缩到请求尺寸内（只缩不放）。用于收掉
    /// IShellItemImageFactory + SIIGBF_BIGGERSIZEOK 可能返回的超大位图，保证缓存里的
    /// 实际像素不超过缓存 key 声称的档位。已经不大于请求尺寸时原样返回。</summary>
    private static ImageSource? DownscaleIfLarger(ImageSource? src, int decodeSize)
    {
        if (src is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return src;
        if (bmp.PixelWidth <= decodeSize && bmp.PixelHeight <= decodeSize) return src;
        try
        {
            double scale = Math.Min((double)decodeSize / bmp.PixelWidth, (double)decodeSize / bmp.PixelHeight);
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
            scaled.Freeze(); // 冻结后才能跨线程读，也才能被下面的 FreezeIfPossible 直接复用
            return scaled;
        }
        catch { return src; }
    }

    /// <summary>把「包装型」位图（CroppedBitmap / TransformedBitmap）脱壳成独立缓冲区。
    ///
    /// 这两类 BitmapSource 都**强引用源位图**：图标按 96px 解码时没事（源也只有 96px），
    /// 但 jumbo 兜底 / SIIGBF_BIGGERSIZEOK 的源可能是 256px —— 缓存按 96 记账、内存里却压着
    /// 整张 256（256KB vs 36KB，差 7 倍）。这里把最终像素 CopyPixels 出来重建成一张无父的
    /// BitmapSource，缓存条目从此只持有自己那份。主路径（shell 直接按尺寸返回）不会走到这里，
    /// 多出来的只有毛边路径的一次小拷贝。</summary>
    private static ImageSource? DetachBitmap(ImageSource? src)
    {
        try
        {
            if (src is not BitmapSource bmp) return src;
            // 只有我们主动包的两类才需要脱壳；BitmapImage / RenderTargetBitmap / BitmapSource.Create
            // 本身就是独立缓冲区，FormatConvertedBitmap 只包一次也在下面统一拷出。
            if (src is not CroppedBitmap && src is not TransformedBitmap && src is not FormatConvertedBitmap)
                return src;
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w <= 0 || h <= 0) return src;
            int stride = w * 4;
            var px = new byte[stride * h];
            BitmapSource conv = bmp.Format == PixelFormats.Pbgra32
                ? bmp
                : new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
            conv.CopyPixels(px, stride, 0);
            var detached = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
            detached.Freeze();
            return detached;
        }
        catch { return src; }   // 失败就保持原样（最坏是内存少省一点，不影响显示）
    }

    /// <summary>
    /// State-aware Recycle Bin icon: reads the shell's own Full/Empty icon locations from
    /// HKCR\CLSID\{645FF040-...}\DefaultIcon (exactly what the desktop switches on) and
    /// extracts the current system icon at the requested decode size.
    /// </summary>
    private ImageSource? GetRecycleBinStateIcon(int decodeSize)
    {
        bool full = RecycleBinHasItems();
        string key = $"{decodeSize}|{RecycleBinSpec}" + (full ? "|full" : "|empty");
        if (_iconCache.TryGetValue(key, out var cached)) return cached;
        var icon = DetachBitmap(FreezeIfPossible(BuildRecycleBinIcon(full, decodeSize)
            ?? GetShellLocationIcon(RecycleBinSpec, decodeSize)
            ?? GetFolderIcon(decodeSize)));
        if (icon != null) CacheIcon(key, icon);
        return icon;
    }

    private static ImageSource? BuildRecycleBinIcon(bool full, int decodeSize)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(RecycleBinDefaultIconKey);
            if (key == null) return null;
            string? raw = key.GetValue(full ? "Full" : "Empty") as string
                ?? key.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(raw)) return null;
            raw = Environment.ExpandEnvironmentVariables(raw.Trim());
            int sep = raw.LastIndexOf(',');
            if (sep <= 0 || sep >= raw.Length - 1) return null;
            var file = raw[..sep].Trim('"');
            if (!int.TryParse(raw[(sep + 1)..], out var index)) return null;
            if (!File.Exists(file)) return null;

            uint size = (uint)(decodeSize | (decodeSize << 16)); // MAKELONG(n, n)
            if (NativeMethods.SHDefExtractIconW(file, index, 0, out var hLarge, out var hSmall, size) <= 0
                || hLarge == IntPtr.Zero)
                return null;
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hLarge, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                NativeMethods.DestroyIcon(hLarge);
                if (hSmall != IntPtr.Zero) NativeMethods.DestroyIcon(hSmall);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Icon for a virtual shell object ("::{GUID}"): primary path asks
    /// IShellItemImageFactory at the requested decode size (preserves alpha/rounded corners);
    /// the jumbo system image list is the fallback — it can render legacy masked icons as
    /// opaque blocks, which breaks rounded corners. Further fallbacks: SHGetFileInfo, then
    /// the resolved known-folder path, then the generic folder icon.
    /// </summary>
    private static ImageSource? GetShellLocationIcon(string spec, int decodeSize)
    {
        // Primary: real system icon bitmap straight from the shell item itself
        // (IShellItemImageFactory — correct alpha, SCALEUP fills the canvas).
        var hbm = ShellOle.GetItemIconBitmap(spec, decodeSize);
        if (hbm != null)
        {
            try
            {
                // Convert the DIB section directly to a BitmapSource. Roundtripping
                // through Image.FromHbitmap/GetHbitmap drops the alpha channel (the
                // black frame around the icon) and loses pixel fidelity. This is the
                // same conversion Explorer-style icon samples use.
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hbm.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
            finally { NativeMethods.DeleteObject(hbm.Value); }
        }

        // Jumbo system image list（固定 256px，先缩到请求尺寸再返回，不把 256 的源放进缓存）。
        var hIcon = ShellOle.GetJumboIcon(spec);
        if (hIcon != null)
        {
            try
            {
                var scaled = JumboIconToBitmap(hIcon.Value, decodeSize);
                if (scaled != null) return scaled;
            }
            catch { }
            finally { NativeMethods.DestroyIcon(hIcon.Value); }
        }

        var direct = GetSystemIcon(spec, decodeSize);
        if (direct != null) return direct;

        try
        {
            var guidText = spec.StartsWith("::{", StringComparison.Ordinal)
                ? spec.Substring(3).TrimEnd('}') : string.Empty;
            if (Guid.TryParse(guidText, out var guid) &&
                NativeMethods.SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var pPath) == 0 && pPath != IntPtr.Zero)
            {
                string folderPath;
                try { folderPath = Marshal.PtrToStringUni(pPath) ?? string.Empty; }
                finally { NativeMethods.CoTaskMemFree(pPath); }
                if (!string.IsNullOrEmpty(folderPath))
                {
                    var icon = GetSystemIcon(folderPath, decodeSize);
                    if (icon != null) return icon;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Icon for a file/folder/shortcut target. Primary path asks IShellItemImageFactory
    /// at the requested decode size with SCALEUP — this preserves alpha (rounded corners
    /// stay rounded, verified against the jumbo list which renders legacy masked icons as
    /// opaque blocks). Letterboxing remnants are removed by <see cref="CropLetterboxed"/> in
    /// <see cref="GetIcon"/>. Falls back to the jumbo system image list (scaled down to the
    /// requested size), then the legacy 32px system icon (解不出更细的，保持原样).
    /// </summary>
    private static ImageSource? GetSystemIcon(string path, int decodeSize)
    {
        // Primary: IShellItemImageFactory at the requested pixel size.
        try
        {
            var hbm = ShellOle.GetItemIconBitmap(path, decodeSize);
            if (hbm != null)
            {
                try
                {
                    // Convert the DIB section directly — roundtrips through GetHbitmap
                    // drop the alpha channel (black corners).
                    return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                        hbm.Value, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                }
                finally { NativeMethods.DeleteObject(hbm.Value); }
            }
        }
        catch
        {
            // fall through to the jumbo list
        }

        // Fallback: jumbo system image list（固定 256px，缩到请求尺寸后再返回）。
        var hIcon = ShellOle.GetJumboIcon(path);
        if (hIcon != null)
        {
            try
            {
                var scaled = JumboIconToBitmap(hIcon.Value, decodeSize);
                if (scaled != null) return scaled;
            }
            catch { }
            finally { NativeMethods.DestroyIcon(hIcon.Value); }
        }

        try
        {
            // ponytail 2026-11-14: ExtractIconHandle 返回裸 HICON，转成 BitmapSource 后必须 DestroyIcon。
            // 变量名不能叫 hIcon —— 外层 jumbo 分支已在该方法作用域声明了同名局部（CS0136）。
            // 这是 32px 兜底路径（shell 没有更好来源时），decodeSize 再小也解不出更细的，保持原样。
            var legacyIcon = NativeMethods.ExtractIconHandle(path, true);
            if (legacyIcon == IntPtr.Zero) return null;
            try
            {
                return System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    legacyIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            }
            finally { NativeMethods.DestroyIcon(legacyIcon); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 jumbo 系统图像列表的固定 256px HICON 转成 BitmapSource 并缩到请求的解码尺寸。
    /// 缩放在这里做（而不是把 256 的源塞进缓存让 WPF 每次显示时缩）：否则 96px 档本来
    /// 只要 36 KB，缓存里却按 256×256 占 256 KB，字节预算会严重低估实际占用。
    /// 返回的 ImageSource 已冻结（跨线程安全）。失败返回 null，由调用方继续走后面的兜底。
    /// </summary>
    private static ImageSource? JumboIconToBitmap(IntPtr hIcon, int decodeSize)
    {
        try
        {
            var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            if (bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0) return bmp;
            bmp.Freeze();
            if (decodeSize >= bmp.PixelWidth) return bmp;
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(
                (double)decodeSize / bmp.PixelWidth, (double)decodeSize / bmp.PixelHeight));
            scaled.Freeze(); // 缓存里会跨线程读（导入线程预热 / UI 线程绑定），必须冻结
            return scaled;
        }
        catch { return null; }
    }

    /// <summary>通用文件夹图标（兜底）。按解码尺寸懒加载 —— 原来只有一张 static 的 256px 图，
    /// 请求 48/96 的调用方也得吃整张 256。注意不能反向调用 <see cref="GetIcon"/>（会递归）：
    /// 直接走 <see cref="GetSystemIcon"/>，它的 jumbo / 32px 兜底都不会再回到这里。</summary>
    private static ImageSource? GetFolderIcon(int decodeSize)
    {
        if (_folderIcons.TryGetValue(decodeSize, out var cached)) return cached;
        ImageSource? icon = null;
        try
        {
            icon = GetSystemIcon(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), decodeSize);
        }
        catch { }
        // 失败结果也缓存：连桌面路径都解析不出图标时，别让每张图标都重试一遍。
        _folderIcons.TryAdd(decodeSize, icon);
        return icon;
    }

    /// <summary>估算一条缓存占用的托管内存：像素宽 × 高 × 4 字节（BGRA）。
    /// CroppedBitmap / TransformedBitmap 都是 BitmapSource，PixelWidth/Height 是裁剪/缩放后的
    /// 实际尺寸。非位图（DrawingImage 等）按 64 KB 兜底 —— 这条路径实际不会出现，
    /// 兜底只为账本不记 0。</summary>
    private static long EstimateIconBytes(ImageSource src)
    {
        try
        {
            if (src is BitmapSource bmp && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                return (long)bmp.PixelWidth * bmp.PixelHeight * 4;
        }
        catch { }
        return 64 * 1024;
    }

    /// <summary>统一的入缓存口 —— 所有 add 点都走这里；超字节预算时按插入顺序淘汰。</summary>
    private void CacheIcon(string key, ImageSource src)
    {
        long bytes = EstimateIconBytes(src);
        lock (_cacheLock)
        {
            if (!_iconCache.TryAdd(key, src)) return;
            _iconCacheOrder.Enqueue(key);
            _iconCacheBytes[key] = bytes;
            _cachedBytes += bytes;

            // FIFO 淘汰。队列里可能残留已删除 / 重复入队的 key：TryDequeue 到它时
            // TryRemove 会失败，此时不减字节（那条的字节在真正被移除时已经减掉了）。
            while (_cachedBytes > MaxIconCacheBytes && _iconCacheOrder.TryDequeue(out var oldest))
            {
                if (_iconCache.TryRemove(oldest, out _) && _iconCacheBytes.TryRemove(oldest, out var freed))
                    _cachedBytes -= freed;
            }
        }
    }

    public void ClearCache()
    {
        // 与 CacheIcon 同一把锁：清空期间不能有入缓存插进来，否则字节账本会残留。
        lock (_cacheLock)
        {
            _iconCache.Clear();
            _iconCacheBytes.Clear();
            while (_iconCacheOrder.TryDequeue(out _)) { }
            _cachedBytes = 0;
        }
        _folderIcons.Clear(); // 文件夹图标不占预算，但清缓存语义上也要一起清
    }
}
