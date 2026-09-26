using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// ponytail 2026-09-26(二期): 「壁纸来源」自动识别与采集。
///
/// 一期只按 `HKCU\Control Panel\Desktop\Wallpaper` 读文件,遇到第三方动态壁纸软件
/// (Wallpaper Engine / Lively / 火萤…)**完全失效** —— 注册表里躺着的还是很久以前那张
/// 静态图,屏幕上显示的却是软件渲染的内容,于是玻璃背板采到一张八竿子打不着的图。
/// 实测(本机 Wallpaper Engine 2.6):
///   • 活壁纸渲染进 `WorkerW` 下的子窗口(类名 `WPEDesktopDX11Window`,进程 wallpaper32/64);
///   • 该窗口是 **DX11 flip-model**,`PrintWindow`(含 PW_RENDERFULLCONTENT)全黑,没有 GDI 内容;
///   • `Progman` 窗口 DC 同样全黑;
///   • `GetDC(NULL)` / `GetWindowDC(GetDesktopWindow())` + `CAPTUREBLT` 能拿到**合成后的整屏**,
///     但里面**包含本应用自己的窗口**(实测分区开着时该区域均值与分区自身渲染相差 47.66,
///     与壁纸层相差更大)—— 所以直接抓屏会把玻璃自己采进去,形成反馈。
/// 因此采集策略(优先级从高到低):
///   ① 屏幕壁纸就是静态图(或第三方软件在放**视频/图片**类壁纸)→ 直接读原文件,最清晰、零副作用;
///   ② 否则(场景类等无法取静帧的)→ **临时把本应用窗口移到 Z 序最底**再抓屏:分区都挂着
///      `WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW` 且本来就被 `PinToDesktop` 压在最底层,推到
///      `HWND_BOTTOM` + 桌面重绘后应用窗口物理上就在壁纸层之后,抓屏得到的就是壁纸本身;
///   ③ 兜底 `%APPDATA%\...\TranscodedWallpaper`;再兜底 `img19.jpg`。
/// 采集结果缓存;`Invalidate()` 让下次重采。
/// </summary>
public static class WallpaperSource
{
    /// <summary>降采样倍数。ponytail 2026-09-26(二期修订 3): **改成 1(不降采样)**。
    ///
    /// 原先取 2,代价是每个屏幕像素只有半个图像像素:窗口裁剪出来的是 1/2 分辨率的图,
    /// 再被拉回 1:1 显示 = 先砍掉一半清晰度再模糊一次。照片/视频类壁纸"太糊、根本没法看"
    /// 有一半是它造成的(用户实测反馈)。改 1 之后裁剪与屏幕 1:1。
    ///
    /// 代价(实测本机 4480×1600 双屏):每档半径的预模糊位图约 29 MB(原 7 MB)、
    /// 预模糊耗时约 4×(GPU 上是几毫秒级)。缓存档位上限已相应从 12 降到 6
    /// (见 <see cref="WallpaperBackdrop"/>),并且关掉开关时会整块丢掉。
    /// **改动这个值必须同步改 WallpaperBackdrop.Downscale** —— 后者现在直接引用本值。</summary>
    public const int Downscale = 1;

    public enum Kind
    {
        /// <summary>读不到任何壁纸。</summary>
        None,
        /// <summary>系统静态壁纸文件(注册表指定)。</summary>
        SystemFile,
        /// <summary>第三方壁纸软件,且其壁纸是可直接读的图片/视频文件。</summary>
        HostFile,
        /// <summary>第三方壁纸软件,内容只能靠抓屏(场景类)。</summary>
        HostScreen,
    }

    /// <summary>采集结果的文字摘要(设置页显示"当前来源")。</summary>
    public static string Describe { get; private set; } = "";
    /// <summary>本机是否检测到已知的第三方动态壁纸宿主。</summary>
    public static bool HostDetected { get; private set; }
    /// <summary>宿主进程名(wallpaper32 之类),用于摘要文字。</summary>
    public static string HostProcess { get; private set; } = "";

    sealed record Cache(string Origin, Kind Kind, DateTime Stamp, BitmapSource? Image, string Display);

    static Cache? _cache;
    static readonly object _lock = new();

    /// <summary>清空缓存,下次 <see cref="GetDesktop"/> 重新检测与采集。</summary>
    public static void Invalidate()
    {
        lock (_lock) { _cache = null; Describe = ""; }
    }

    // ── 对外主入口 ──

    /// <summary>设置页的「新渲染方案」开关。false 时只认系统注册表里的静态壁纸文件
    /// (不检测第三方宿主、不抓屏)—— 关掉开关的用户不该被偷偷抓一次屏。</summary>
    public static bool Enabled { get; set; }

    /// <summary>取「铺满虚拟桌面」的壁纸位图(1/Downscale 分辨率,Pbgra32)。
    /// 第一次调用会检测来源并可能抓屏(几十毫秒);之后走缓存。
    /// 返回 null = 壁纸不可用(调用方回退 DWM 玻璃)。</summary>
    public static BitmapSource? GetDesktop()
    {
        lock (_lock)
        {
            // 先用轻量指纹判断要不要重新检测 —— 检测本身可能包含一次抓屏(约 100ms),
            // 不能每帧都跑。指纹只读注册表/进程/文件时间,不做任何位图工作。
            var sig = QuickSignature();
            if (_cache != null && _cache.Origin == sig)
            {
                Describe = Summarize(_cache.Kind, _cache.Display, cached: true);
                return _cache.Image;
            }

            var now = Enabled ? Detect() : DetectSystemOnly();
            _cache = new Cache(now.Origin, now.Kind, now.Stamp, now.Image, now.Display);
            Describe = Summarize(now.Kind, now.Display, cached: false);
            DzTrace.Log($"[WallpaperSource] 来源={now.Kind} 图={(now.Image == null ? "NULL" : $"{now.Image.PixelWidth}x{now.Image.PixelHeight}")} 显示={now.Display}");
            return now.Image;
        }
    }

    /// <summary>轻量指纹:足以判断"壁纸换没换"的最小信息,不碰位图。
    /// 抓屏类来源返回固定串(内容随时变,但我们刻意只采一次,靠用户点「重新采样」刷新)。
    /// ponytail 2026-09-26(二期修订 3): 逐显示器比对(Monitor0..N 各自的文件 + 时间戳),
    /// 这样任一屏换了壁纸都会让缓存失效。</summary>
    static string QuickSignature()
    {
        if (!Enabled) return "sys:" + SystemSignature();
        var host = HostLocator.Find();
        if (host == null) return "sys:" + SystemSignature();
        var files = HostLocator.ResolveFiles(host);
        if (files.Count == 0) return HostScreenSig(host.Process);
        return HostSig(host.Process,
            string.Join("|", files.Select(f => f.MonitorIndex + "=" + f.Path)),
            files.Max(f => f.Stamp));
    }

    static string HostScreenSig(string proc) => "hostscreen:" + proc;
    static string HostSig(string proc, string path, DateTime stamp) => "hostfile:" + proc + ":" + path + ":" + stamp.Ticks;
    /// <summary>系统静态壁纸的指纹,带 sys: 前缀(Enabled=false 时 QuickSignature 也返回它)。</summary>
    static string SystemSig(string path, DateTime stamp) => "sys:" + path + ":" + stamp.Ticks;

    /// <summary>注册表里那张静态壁纸的指纹(开关关闭时也用它,口径与 DetectSystemOnly 一致)。</summary>
    static string SystemSignature()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            var p = (k?.GetValue("Wallpaper") as string) ?? "";
            var stamp = File.Exists(p) ? File.GetLastWriteTimeUtc(p) : default;
            return SystemSig(p, stamp);
        }
        catch { return "sys:?"; }
    }

    /// <summary>强制重新采集(设置页的「重新采样」按钮)。</summary>
    public static BitmapSource? Resample()
    {
        Invalidate();
        return GetDesktop();
    }

    /// <summary>只认系统静态壁纸(开关关闭 / 没检测到宿主时的路径)。
    /// Origin 与 <see cref="QuickSignature"/> 在 Enabled=false 时的返回值同口径。</summary>
    static Detected DetectSystemOnly()
    {
        HostDetected = false;
        HostProcess = "";
        foreach (var cand in SystemCandidates())
        {
            var img = TryComposeSystem(cand.Path, cand.Style, cand.Tile);
            if (img != null)
                return new Detected(Kind.SystemFile, SystemSig(cand.Path, cand.Stamp), cand.Stamp, img,
                    ShortName(cand.Path));
        }
        return new Detected(Kind.None, "sys:", default, null, "");
    }

    /// <summary><paramref name="display"/> = 给人看的来源说明(不含 origin 这种机器指纹)。</summary>
    static string Summarize(Kind kind, string display, bool cached)
    {
        var loc = LocalizationService.Instance;
        string suffix = cached ? "" : " *";
        return kind switch
        {
            Kind.SystemFile => loc["Settings.Renderer.Source.System"] + ": " + display + suffix,
            Kind.HostFile => loc["Settings.Renderer.Source.Host"] + $" ({HostProcess}): " + display + suffix,
            Kind.HostScreen => loc["Settings.Renderer.Source.Screen"] + $" ({HostProcess})" + suffix,
            _ => loc["Settings.Renderer.Source.None"],
        };
    }

    static string ShortName(string p)
    {
        try { return Path.GetFileName(p); } catch { return p; }
    }

    // ── 来源检测 ──

    sealed record Detected(Kind Kind, string Origin, DateTime Stamp, BitmapSource? Image, string Display);

    /// <summary>检测来源并采集。<see cref="Detected.Origin"/> **必须**与
    /// <see cref="QuickSignature"/> 的返回值口径一致 —— 缓存命中就是比这两个串,
    /// 早期版本一边返回 "screen"、一边返回 "hostscreen:wallpaper32",缓存永远不命中,
    /// 结果是每次渲染都抓一次屏(实测踩过)。
    ///
    /// ponytail 2026-09-26(二期修订 3): 优先级重排 —— **文件类壁纸(图片)优先,读不到就抓屏**,
    /// 抓屏之后再用能读到的那些显示器的文件图**覆盖对应区域**(抓屏给的是合成后的整屏,
    /// 会带上别的程序窗口;能读原文件的显示器就没必要用它)。视频不再用同目录的
    /// `preview.jpg`(通常只有 1024×1024,拉满屏幕后"根本没法看"—— 用户实测反馈),
    /// 改为走抓屏拿实时帧。布局也不再拉满整块虚拟桌面,改成**按每个显示器各自适配**。</summary>
    static Detected Detect()
    {
        // ① 第三方宿主?
        var host = HostLocator.Find();
        HostDetected = host != null;
        HostProcess = host?.Process ?? "";

        if (host != null)
        {
            // ①a 宿主的壁纸本身是图片文件 → 直接读原文件,最清晰、零副作用
            var files = HostLocator.ResolveFiles(host);          // MonitorN → 图片路径(场景/视频不在内)
            var parts = BuildHostParts(files);
            var names = string.Join(", ", parts.Select(p => ShortName(p.Path)));
            if (parts.Count > 0 && parts.Count == Monitors().Count)
            {
                var composed = ComposeDesktop(null, parts);
                if (composed != null)
                    return new Detected(Kind.HostFile,
                        HostSig(host.Process, string.Join("|", parts.Select(p => p.Path)), parts.Max(p => p.Stamp)),
                        parts.Max(p => p.Stamp), composed, names);
            }

            // ①b 有显示器读不到(场景/视频)→ 抓屏,再用可读的那几个显示器的文件图盖掉对应区域
            var shot = CaptureDesktop();
            if (shot != null)
            {
                var overlaid = parts.Count > 0 ? ComposeDesktop(shot, parts) : shot;
                return new Detected(Kind.HostScreen, HostScreenSig(host.Process), DateTime.UtcNow,
                    overlaid ?? shot, parts.Count > 0 ? names + " + " + LocalizationService.Instance["Settings.Renderer.Source.Screen"] : "");
            }
        }

        // ② 系统静态壁纸(一张图 → 每个显示器各自适配)
        foreach (var cand in SystemCandidates())
        {
            var img = TryComposeSystem(cand.Path, cand.Style, cand.Tile);
            if (img != null)
                return new Detected(Kind.SystemFile, SystemSig(cand.Path, cand.Stamp), cand.Stamp, img,
                    ShortName(cand.Path));
        }

        return new Detected(Kind.None, Enabled ? "none" : "none:off", default, null, "");
    }

    sealed record Candidate(string Path, string Style, string Tile, DateTime Stamp);

    static IEnumerable<Candidate> SystemCandidates()
    {
        string style = "10", tile = "0", path = "";
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            path = (k?.GetValue("Wallpaper") as string) ?? "";
            style = (k?.GetValue("WallpaperStyle") as string) ?? "10";
            tile = (k?.GetValue("TileWallpaper") as string) ?? "0";
        }
        catch { }

        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(path)) list.Add(path);
        var transcoded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
        list.Add(transcoded);
        list.Add(transcoded + ".jpg");
        list.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "web", "wallpaper", "Windows", "img19.jpg"));

        foreach (var p in list)
        {
            if (!File.Exists(p)) continue;
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(p); } catch { continue; }
            yield return new Candidate(p, style, tile, stamp);
        }
    }

    /// <summary>读一张图(不铺、不缩放)。失败返回 null。</summary>
    static BitmapSource? TryLoad(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);   // 先读进内存:TranscodedWallpaper 常被系统占用且无扩展名
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.StreamSource = new MemoryStream(bytes);
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;   // 与桌面观感一致
            bi.EndInit();
            bi.Freeze();
            if (bi.PixelWidth < 16 || bi.PixelHeight < 16) return null;
            return bi;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[WallpaperSource] 读图失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>一块「贴在某个显示器上的壁纸」:显示器物理矩形 + 图 + 适配模式 + 来源路径/时间戳
    /// (后两个只用于生成缓存指纹与摘要文字)。</summary>
    sealed record Part(Rect Monitor, BitmapSource Image, string Style, string Tile,
        string Path, DateTime Stamp);

    /// <summary>系统静态壁纸:一张图 → **每个显示器各自适配** → 拼成整块虚拟桌面。
    /// style 22(跨屏)例外:那种模式本来就是一张图横跨所有显示器。
    /// style: 0=居中/平铺 2=拉伸 6=适应 10=填充 22=跨屏。</summary>
    static BitmapSource? TryComposeSystem(string path, string style, string tile)
    {
        var img = TryLoad(path);
        if (img == null) return null;
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); } catch { stamp = default; }

        var monitors = Monitors();
        if (monitors.Count == 0) return null;
        var parts = new List<Part>();
        if (style == "22")
            parts.Add(new Part(VirtualDesktop(), img, style, tile, path, stamp));   // 跨屏:整块铺一次
        else
            foreach (var m in monitors)
                parts.Add(new Part(m, img, style, tile, path, stamp));
        return ComposeDesktop(null, parts);
    }

    /// <summary>把「每块显示器 → 各自的图」画成整块虚拟桌面的位图(**物理像素 1:1**,不再降采样)。
    ///
    /// ponytail 2026-09-26(二期修订 3) 三处改动都在这里:
    ///   ① **按显示器各自适配**(而不是把一张图拉满虚拟桌面)—— 修"采样区域有很大偏移";
    ///   ② **1:1 全分辨率**(Downscale 2 → 1)—— 修"太糊"(原先每 2 个屏幕像素才 1 个图像像素,
    ///      再被拉伸回 1:1,等于先砍一半清晰度再模糊);
    ///   ③ <paramref name="baseImage"/> 非空 = 以它(抓屏结果)为底,再盖上有原文件的显示器 ——
    ///      抓屏带别的程序窗口,能读原文件的区域就别用它。</summary>
    static BitmapSource? ComposeDesktop(BitmapSource? baseImage, IReadOnlyList<Part> parts)
    {
        var (vx, vy, vw, vh) = LayoutRect;
        if (vw <= 0 || vh <= 0) return null;
        int dw = Math.Max(8, vw / Downscale), dh = Math.Max(8, vh / Downscale);

        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, dw, dh));
            if (baseImage != null)
                dc.DrawImage(baseImage, new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight));

            foreach (var part in parts)
            {
                // 该图的可用区域 = 它所属显示器的矩形;跨屏模式则是整块虚拟桌面
                var area = part.Style == "22" ? new Rect(vx, vy, vw, vh) : part.Monitor;
                if (area.Width <= 0 || area.Height <= 0) continue;
                double iw = part.Image.PixelWidth, ih = part.Image.PixelHeight;
                double sx, sy, ox, oy;
                bool tileMode = part.Tile == "1" && part.Style == "0";
                switch (part.Style)
                {
                    case "2": case "22": sx = area.Width / iw; sy = area.Height / ih; ox = oy = 0; break;
                    case "6": sx = sy = Math.Min(area.Width / iw, area.Height / ih); ox = (area.Width - iw * sx) / 2; oy = (area.Height - ih * sy) / 2; break;
                    case "0": sx = sy = 1; ox = (area.Width - iw) / 2; oy = (area.Height - ih) / 2; break;
                    default: sx = sy = Math.Max(area.Width / iw, area.Height / ih); ox = (area.Width - iw * sx) / 2; oy = (area.Height - ih * sy) / 2; break;   // 10 填充
                }
                // 图像像素 → 画布像素(虚拟桌面原点归零)
                double cx = (area.X - vx) / Downscale, cy = (area.Y - vy) / Downscale;
                if (tileMode)
                {
                    // 平铺:以该显示器左上角为锚点,用 Viewport 平移实现(ImageBrush 的 Viewbox 不含偏移)
                    int tw = (int)Math.Round(area.Width / Downscale), th = (int)Math.Round(area.Height / Downscale);
                    var brush = new System.Windows.Media.ImageBrush(part.Image)
                    {
                        TileMode = System.Windows.Media.TileMode.Tile,
                        ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                        Viewport = new Rect(0, 0, iw / Downscale, ih / Downscale),
                        Stretch = System.Windows.Media.Stretch.None,
                    };
                    brush.Freeze();
                    var dv = new System.Windows.Media.DrawingVisual();
                    using (var cdc = dv.RenderOpen())
                        cdc.DrawRectangle(brush, null, new Rect(0, 0, tw, th));
                    var tb = new RenderTargetBitmap(Math.Max(1, tw), Math.Max(1, th), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    tb.Render(dv);
                    tb.Freeze();
                    dc.DrawImage(tb, new Rect(cx, cy, area.Width / Downscale, area.Height / Downscale));
                }
                else
                {
                    dc.DrawImage(part.Image, new Rect(
                        cx + ox / Downscale, cy + oy / Downscale,
                        iw * sx / Downscale, ih * sy / Downscale));
                }
            }
        }
        var rtb = new RenderTargetBitmap(dw, dh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>所有显示器(物理像素矩形),主显示器排第一。</summary>
    static IReadOnlyList<Rect> Monitors() => MonitorHelper.Monitors();

    static Rect VirtualDesktop()
    {
        var (vx, vy, vw, vh) = LayoutRect;
        return new Rect(vx, vy, Math.Max(1, vw), Math.Max(1, vh));
    }

    /// <summary>宿主的「MonitorN → 图片文件」映射 → 每块显示器一块 part。
    /// WPE 的 MonitorN 编号习惯是 Monitor0 = 主屏(本机实测),所以按 Monitors() 的顺序对位;
    /// 只配了一个壁纸时,所有显示器都用它(单图双屏的常见情形)。</summary>
    static List<Part> BuildHostParts(IReadOnlyList<HostLocator.PickedFile> files)
    {
        var parts = new List<Part>();
        var monitors = Monitors();
        if (files.Count == 0 || monitors.Count == 0) return parts;

        for (int i = 0; i < monitors.Count; i++)
        {
            var f = files.FirstOrDefault(x => x.MonitorIndex == i) ?? (files.Count == 1 ? files[0] : null);
            if (f == null) continue;
            var img = TryLoad(f.Path);
            if (img == null) continue;
            parts.Add(new Part(monitors[i], img, "10", "0", f.Path, f.Stamp));   // 壁纸软件一律按"填充"铺
        }
        return parts;
    }

    static int _layoutVx, _layoutVy, _layoutVw, _layoutVh;

    /// <summary>虚拟桌面原点/尺寸(物理像素)。</summary>
    public static (int X, int Y, int W, int H) LayoutRect
    {
        get
        {
            if (_layoutVw <= 0)
            {
                _layoutVx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
                _layoutVy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
                _layoutVw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
                _layoutVh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
            }
            return (_layoutVx, _layoutVy, _layoutVw, _layoutVh);
        }
    }

    // ── 抓屏(场景类壁纸唯一可行路径) ──

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
        IntPtr hdcSrc, int xSrc, int ySrc, int rop);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    const int SW_HIDE = 0, SW_SHOWNA = 8;
    static readonly IntPtr HWND_BOTTOM = new(1);
    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>DWMWA_CLOAK —— 把窗口从 DWM 合成里摘掉,但**不动窗口管理器的可见性**。
    /// ponytail 2026-09-26(二期修订 3): 管理窗口这类普通窗口用 cloak 而不是 SW_HIDE,
    /// 因为隐藏前台窗口会让系统把焦点转给别的程序(用户正在用的浏览器会被顶到前面),而且
    /// 整窗消失 0.1 秒非常显眼。cloak 只是"不画它",焦点/Z 序/激活状态全不动。</summary>
    const int DWMWA_CLOAK = 13;

    /// <summary>抓整屏当壁纸。采样期间本应用的窗口会被临时摘出画面,否则会被采进背板
    /// (抓屏拿到的是**合成后的整屏**,形成"玻璃里套玻璃")。
    ///
    /// ponytail 2026-09-26(二期修订 3): 分成两段,目的是让**管理窗口几乎看不见地让开**——
    /// 用户实测反馈"点重新采样后管理窗口整个消失/闪一下再回来"(上一版把本应用所有可见窗口
    /// 一起隐藏了 0.1 秒)。现在:
    ///   ① 玻璃窗口(分区/便签/时钟/日历/面板)先压到 Z 序最底再 `SW_HIDE` —— 它们本来就在
    ///      桌面层、多半被别的窗口盖着,消失 0.1 秒基本无感;
    ///   ② 等一个 `DwmFlush()` + 短睡,让 DWM 重排出"干净的壁纸";
    ///   ③ 抓屏**前一刻**才 cloak 掉其余窗口(管理窗口等),抓完**立刻**解除 —— 它们在画面上
    ///      只缺席 1~2 帧(约 30ms),而且不动焦点。
    /// 最小化的窗口跳过(不在画面上,不参与合成)。</summary>
    public static BitmapSource? CaptureDesktop()
    {
        var windows = Application.Current?.Windows;
        var hidden = new List<(IntPtr Hwnd, bool WasVisible)>();
        var cloaked = new List<IntPtr>();
        // 恢复所有窗口(幂等:抓屏前调用一次,finally 再兜一次)
        void RestoreAll()
        {
            foreach (var h in cloaked) Cloak(h, false);
            cloaked.Clear();
            foreach (var (h, wasVisible) in hidden)
                if (wasVisible) ShowWindow(h, SW_SHOWNA);
            hidden.Clear();
        }
        try
        {
            if (windows != null)
            {
                foreach (Window w in windows)
                {
                    try
                    {
                        if (w.WindowState == WindowState.Minimized) continue;
                        var h = new WindowInteropHelper(w).Handle;
                        if (h == IntPtr.Zero || !IsWindowVisible(h)) continue;
                        // 玻璃窗口(含 36×36 的恢复按钮态)→ 压底 + 隐藏(它们本来就在桌面层)
                        bool glass = w is Views.ZoneWindow or Views.StickyNoteWindow or Views.ClockWidget
                            or Views.CalendarWidget or Views.PanelWindow;
                        if (!glass) continue;
                        SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                        ShowWindow(h, SW_HIDE);
                        hidden.Add((h, true));
                    }
                    catch { }
                }
            }
            if (hidden.Count > 0)
            {
                // 让 DWM 重排 + 屏幕真正更新:同步渲染一次空操作 + 等合成完成
                // (DwmFlush 会阻塞到这一帧真正合成完毕,所以不需要原来那种 110ms 的死等)
                System.Windows.Media.CompositionTarget.Rendering += Noop;
                DwmFlush();
                System.Threading.Thread.Sleep(25);
                DwmFlush();
            }

            // ③ 其余窗口(管理窗口等)只在抓屏这一瞬间让开
            if (windows != null)
            {
                foreach (Window w in windows)
                {
                    try
                    {
                        if (w.WindowState == WindowState.Minimized) continue;
                        var h = new WindowInteropHelper(w).Handle;
                        if (h == IntPtr.Zero || !IsWindowVisible(h)) continue;
                        if (w is Views.ZoneWindow or Views.StickyNoteWindow or Views.ClockWidget
                            or Views.CalendarWidget or Views.PanelWindow) continue;
                        if (Cloak(h, true)) cloaked.Add(h);
                    }
                    catch { }
                }
                if (cloaked.Count > 0)
                {
                    DwmFlush();
                    System.Threading.Thread.Sleep(20);
                    DwmFlush();
                }
            }

            var (vx, vy, vw, vh) = LayoutRect;
            if (vw <= 0 || vh <= 0) return null;
            int dw = Math.Max(8, vw / Downscale), dh = Math.Max(8, vh / Downscale);

            using var full = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full))
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = GetDC(IntPtr.Zero);
                BitBlt(dst, 0, 0, vw, vh, src, vx, vy, SRCCOPY | CAPTUREBLT);
                ReleaseDC(IntPtr.Zero, src);
                g.ReleaseHdc(dst);
            }
            // ponytail 2026-09-26(二期修订 3): 画面已经拿到手,**立刻**把所有窗口放回去,
            // 后面的缩放/拷贝(全分辨率下要走几十毫秒)不该再让用户盯着空屏。
            RestoreAll();
            using var small = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
            using (var g2 = Graphics.FromImage(small))
            {
                g2.DrawImage(full, new Rectangle(0, 0, dw, dh), new Rectangle(0, 0, vw, vh), GraphicsUnit.Pixel);
            }

            var wb = new WriteableBitmap(dw, dh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
            wb.Lock();
            try
            {
                var d = small.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    var row = new byte[dw * 4];
                    for (int y = 0; y < dh; y++)
                    {
                        Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                        Marshal.Copy(row, 0, wb.BackBuffer + y * wb.BackBufferStride, row.Length);
                    }
                }
                finally { small.UnlockBits(d); }
            }
            finally { wb.Unlock(); }
            wb.Freeze();
            DzTrace.Log($"[WallpaperSource] 抓屏成功 {vw}x{vh} → {dw}x{dh} (隐藏玻璃窗口 {hidden.Count} 个 / 临时 cloak 其余窗口 {cloaked.Count} 个)");
            return wb;
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[WallpaperSource] 抓屏失败: {ex.Message}");
            return null;
        }
        finally
        {
            System.Windows.Media.CompositionTarget.Rendering -= Noop;
            RestoreAll();
        }
    }

    /// <summary>cloak / uncloak(DWMWA_CLOAK)。失败返回 false(调用方会跳过恢复)。</summary>
    static bool Cloak(IntPtr hwnd, bool on)
    {
        try
        {
            int v = on ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    static void Noop(object? s, EventArgs e) { }
}

/// <summary>
/// 第三方动态壁纸软件的定位与「当前壁纸 → 可读文件」解析。
/// 目前只做 Wallpaper Engine(装机量最大,且其 config.json 是明文 JSON);
/// 其余软件(火萤/UPUPOO/Lively…)先只做「检测到 + 名字」,采集走抓屏兜底。
/// </summary>
static class HostLocator
{
    public sealed record Host(string Process, string InstallDir, string ConfigPath);

    /// <summary>一台显示器上「能直接读成图」的壁纸。MonitorIndex = WPE 的 MonitorN 里的 N
    /// (0 = 主屏,见 <see cref="MonitorHelper.Monitors"/> 的排序约定)。</summary>
    public sealed record PickedFile(int MonitorIndex, string Path, DateTime Stamp);

    /// <summary>检测正在运行的第三方壁纸宿主。找不到返回 null。</summary>
    public static Host? Find()
    {
        foreach (var name in new[] { "wallpaper32", "wallpaper64" })
        {
            try
            {
                var procs = Process.GetProcessesByName(name);
                if (procs.Length == 0) continue;
                string dir = "";
                try { dir = Path.GetDirectoryName(procs[0].MainModule?.FileName ?? "") ?? ""; } catch { }
                if (string.IsNullOrEmpty(dir))
                {
                    // 主模块读不到(权限)→ 退回注册表里的 installPath
                    try
                    {
                        using var k = Registry.CurrentUser.OpenSubKey(@"Software\WallpaperEngine");
                        var ip = k?.GetValue("installPath") as string;
                        if (!string.IsNullOrEmpty(ip)) dir = Path.GetDirectoryName(ip!) ?? "";
                    }
                    catch { }
                }
                if (string.IsNullOrEmpty(dir)) continue;
                var cfg = Path.Combine(dir, "config.json");
                DzTrace.Log($"[HostLocator] 检测到 {name} pid={procs[0].Id} dir={dir} cfg={File.Exists(cfg)}");
                return new Host(name, dir, cfg);
            }
            catch { }
        }
        return null;
    }

    /// <summary>解析 Wallpaper Engine 当前壁纸 —— **逐显示器**返回「能直接读成图」的那些。
    ///
    /// ponytail 2026-09-26(二期修订 3) 两处改动:
    ///   ① 原来只取 Monitor0 一张、还把它拉满整个虚拟桌面 → 双屏各自设了不同壁纸时,
    ///      右屏等于显示左屏那张的拉伸版,内容与真实壁纸差出几百像素("采样区域有很大偏移");
    ///      现在逐显示器返回,交给 <see cref="WallpaperSource.ComposeDesktop"/> 各铺各的;
    ///   ② **视频不再用同目录的 `preview.jpg`** —— 那玩意实测只有 1024×1024,拉满 2560×1600 的屏
    ///      之后"根本没法看"(用户实测反馈)。现在视频与场景类一样走抓屏,拿的是**实时帧 + 原生分辨率**。
    /// 所以只有图片类(.jpg/.jpeg/.png/.bmp/.webp/.gif)会出现在返回值里。</summary>
    public static IReadOnlyList<PickedFile> ResolveFiles(Host host)
    {
        var list = new List<PickedFile>();
        if (!File.Exists(host.ConfigPath)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(host.ConfigPath));
            var root = doc.RootElement;
            // 顶层是 { "?installdirectory": ..., "<用户名>": { general: { wallpaperconfig: { selectedwallpapers: { Monitor0: { file } } } } } }
            foreach (var user in root.EnumerateObject())
            {
                if (user.Name.StartsWith("?")) continue;
                if (!user.Value.TryGetProperty("general", out var general)) continue;
                if (!general.TryGetProperty("wallpaperconfig", out var wc)) continue;
                if (!wc.TryGetProperty("selectedwallpapers", out var sel)) continue;

                foreach (var m in sel.EnumerateObject())
                {
                    if (!m.Value.TryGetProperty("file", out var f) || f.ValueKind != JsonValueKind.String) continue;
                    var raw = f.GetString();
                    if (string.IsNullOrEmpty(raw)) continue;
                    var p = raw.Replace('/', Path.DirectorySeparatorChar);
                    if (!File.Exists(p)) continue;

                    string ext = Path.GetExtension(p).ToLowerInvariant();
                    if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif"))
                    {
                        DzTrace.Log($"[HostLocator] {m.Name} 壁纸 {ext} 无法直接取静帧 → 该屏走抓屏");
                        continue;
                    }
                    list.Add(new PickedFile(MonitorIndexOf(m.Name), p, File.GetLastWriteTimeUtc(p)));
                }
                break;   // 第一个非 "?" 用户就是当前用户
            }
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[HostLocator] 解析 config.json 失败: {ex.Message}");
        }
        list.Sort((a, b) => a.MonitorIndex.CompareTo(b.MonitorIndex));
        return list;
    }

    /// <summary>"Monitor3" → 3;解析不出来时按 0。</summary>
    static int MonitorIndexOf(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    // ponytail 2026-09-26(二期修订 3): 原 FindPreview() 已删 —— 视频壁纸不再用同目录的
    // preview.jpg(实测只有 1024×1024,拉满屏幕后没法看),改为抓屏取实时帧。
}
