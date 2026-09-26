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
    /// <summary>降采样倍数 —— 与 WallpaperBackdrop 的预模糊保持同一口径。</summary>
    public const int Downscale = 2;

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

    sealed record Cache(string Origin, Kind Kind, DateTime Stamp, BitmapSource? Image);

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
                Describe = Summarize(_cache.Kind, _cache.Origin, cached: true);
                return _cache.Image;
            }

            var now = Enabled ? Detect() : DetectSystemOnly();
            _cache = new Cache(now.Origin, now.Kind, now.Stamp, now.Image);
            Describe = Summarize(now.Kind, now.Origin, cached: false);
            DzTrace.Log($"[WallpaperSource] 来源={now.Kind} origin={now.Origin} 图={(now.Image == null ? "NULL" : $"{now.Image.PixelWidth}x{now.Image.PixelHeight}")}");
            return now.Image;
        }
    }

    /// <summary>轻量指纹:足以判断"壁纸换没换"的最小信息,不碰位图。
    /// 抓屏类来源返回固定串(内容随时变,但我们刻意只采一次,靠用户点「重新采样」刷新)。</summary>
    static string QuickSignature()
    {
        if (!Enabled) return "sys:" + SystemSignature();
        var host = HostLocator.Find();
        if (host == null) return "sys:" + SystemSignature();
        var f = HostLocator.ResolveFile(host);
        return f == null ? HostScreenSig(host.Process) : HostSig(host.Process, f.Path, f.Stamp);
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
            var img = TryLoadLayouted(cand.Path, cand.Style, cand.Tile);
            if (img != null)
                return new Detected(Kind.SystemFile, SystemSig(cand.Path, cand.Stamp), cand.Stamp, img);
        }
        return new Detected(Kind.None, "sys:", default, null);
    }

    static string Summarize(Kind kind, string origin, bool cached)
    {
        var loc = LocalizationService.Instance;
        string suffix = cached ? "" : " *";
        return kind switch
        {
            Kind.SystemFile => loc["Settings.Renderer.Source.System"] + ": " + ShortName(origin) + suffix,
            Kind.HostFile => loc["Settings.Renderer.Source.Host"] + $" ({HostProcess}): " + ShortName(origin) + suffix,
            Kind.HostScreen => loc["Settings.Renderer.Source.Screen"] + $" ({HostProcess})" + suffix,
            _ => loc["Settings.Renderer.Source.None"],
        };
    }

    static string ShortName(string p)
    {
        try { return Path.GetFileName(p); } catch { return p; }
    }

    // ── 来源检测 ──

    sealed record Detected(Kind Kind, string Origin, DateTime Stamp, BitmapSource? Image);

    /// <summary>检测来源并采集。<see cref="Detected.Origin"/> **必须**与
    /// <see cref="QuickSignature"/> 的返回值口径一致 —— 缓存命中就是比这两个串,
    /// 早期版本一边返回 "screen"、一边返回 "hostscreen:wallpaper32",缓存永远不命中,
    /// 结果是每次渲染都抓一次屏(实测踩过)。</summary>
    static Detected Detect()
    {
        // ① 第三方宿主?
        var host = HostLocator.Find();
        HostDetected = host != null;
        HostProcess = host?.Process ?? "";

        if (host != null)
        {
            // ①a 宿主的壁纸本身是图片/视频文件 → 直接读,最清晰
            var f = HostLocator.ResolveFile(host);
            if (f != null)
            {
                var img = TryLoadLayouted(f.Path, f.Style, "0");
                if (img != null)
                    return new Detected(Kind.HostFile, HostSig(host.Process, f.Path, f.Stamp), f.Stamp, img);
            }
            // ①b 只能抓屏
            var shot = CaptureDesktop();
            if (shot != null)
                return new Detected(Kind.HostScreen, HostScreenSig(host.Process), DateTime.UtcNow, shot);
        }

        // ② 系统静态壁纸
        foreach (var cand in SystemCandidates())
        {
            var img = TryLoadLayouted(cand.Path, cand.Style, cand.Tile);
            if (img != null)
                return new Detected(Kind.SystemFile, SystemSig(cand.Path, cand.Stamp), cand.Stamp, img);
        }

        return new Detected(Kind.None, Enabled ? "none" : "none:off", default, null);
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

    /// <summary>读图 → 按适配模式铺到虚拟桌面 → 降采样成 Pbgra32。失败返回 null。</summary>
    static BitmapSource? TryLoadLayouted(string path, string style, string tile)
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
            return Layout(bi, style, tile);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[WallpaperSource] 读图失败 {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>把一张图按 WallpaperStyle 铺到虚拟桌面并降采样。
    /// style: 0=居中/平铺 2=拉伸 6=适应 10=填充 22=跨屏(与桌面设置一致)。</summary>
    static BitmapSource? Layout(BitmapSource img, string style, string tile)
    {
        int vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        _layoutVx = vx; _layoutVy = vy; _layoutVw = vw; _layoutVh = vh;
        if (vw <= 0 || vh <= 0) return null;

        double iw = img.PixelWidth, ih = img.PixelHeight;
        bool tileMode = tile == "1" && style == "0";
        double sx, sy, ox, oy;
        switch (style)
        {
            case "2": case "22": sx = vw / iw; sy = vh / ih; ox = oy = 0; break;
            case "6": sx = sy = Math.Min(vw / iw, vh / ih); ox = (vw - iw * sx) / 2; oy = (vh - ih * sy) / 2; break;
            case "0": sx = sy = 1; ox = (vw - iw) / 2; oy = (vh - ih) / 2; break;
            default: sx = sy = Math.Max(vw / iw, vh / ih); ox = (vw - iw * sx) / 2; oy = (vh - ih * sy) / 2; break;   // 10 填充
        }

        int dw = Math.Max(8, vw / Downscale), dh = Math.Max(8, vh / Downscale);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(System.Windows.Media.Brushes.Black, null, new Rect(0, 0, dw, dh));
            if (tileMode)
            {
                var brush = new System.Windows.Media.ImageBrush(img)
                {
                    TileMode = System.Windows.Media.TileMode.Tile,
                    ViewportUnits = System.Windows.Media.BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, iw / Downscale, ih / Downscale),
                    Stretch = System.Windows.Media.Stretch.None,
                };
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(0, 0, dw, dh));
            }
            else
            {
                dc.DrawImage(img, new Rect(ox / Downscale, oy / Downscale,
                    iw * sx / Downscale, ih * sy / Downscale));
            }
        }
        var rtb = new RenderTargetBitmap(dw, dh, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    static int _layoutVx, _layoutVy, _layoutVw, _layoutVh;

    /// <summary>虚拟桌面原点/尺寸(物理像素)。<see cref="Layout"/> 跑过之后有效。</summary>
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

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;
    const int SW_HIDE = 0, SW_SHOWNA = 8;
    static readonly IntPtr HWND_BOTTOM = new(1);
    const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

    /// <summary>抓整屏当壁纸。**采样期间会把本应用的玻璃窗口临时隐藏约 100ms**(设置页的
    /// 「重新采样」按钮据此提示用户),否则窗口会连同壁纸一起被采进背板,形成"玻璃里套玻璃"。
    /// 先把窗口压到 Z 序最底(HWND_BOTTOM)再隐藏,是为了让它们在恢复时仍回到桌面层而不是
    /// 弹到普通窗口之上 —— 分区的 `PinToDesktop`/`PinBelowProgman` 会重新校准,但压底更稳。</summary>
    public static BitmapSource? CaptureDesktop()
    {
        var windows = Application.Current?.Windows;
        var hidden = new List<(IntPtr Hwnd, bool WasVisible)>();
        try
        {
            if (windows != null)
            {
                foreach (Window w in windows)
                {
                    var h = new WindowInteropHelper(w).Handle;
                    if (h == IntPtr.Zero || !IsWindowVisible(h)) continue;
                    // 只动真正的玻璃窗口;36×36 的恢复按钮态也算(它也有背板)
                    bool glass = w is Views.ZoneWindow or Views.StickyNoteWindow or Views.ClockWidget
                        or Views.CalendarWidget or Views.PanelWindow;
                    if (!glass) continue;
                    SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    ShowWindow(h, SW_HIDE);
                    hidden.Add((h, true));
                }
                // 让 DWM 重排 + 屏幕真正更新:同步渲染一次空操作,把时间让给合成器
                System.Windows.Media.CompositionTarget.Rendering += Noop;
                System.Threading.Thread.Sleep(110);
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
            DzTrace.Log($"[WallpaperSource] 抓屏成功 {vw}x{vh} → {dw}x{dh} (临时隐藏玻璃窗口 {hidden.Count} 个)");
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
            foreach (var (h, wasVisible) in hidden)
                if (wasVisible) ShowWindow(h, SW_SHOWNA);
        }
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

    public sealed record ResolvedFile(string Path, string Style, DateTime Stamp);

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

    /// <summary>解析 Wallpaper Engine 当前壁纸。只有「图片/视频文件」能直接读;
    /// `scene.pkg`(场景类)返回 null —— 那种只能抓屏。</summary>
    public static ResolvedFile? ResolveFile(Host host)
    {
        if (!File.Exists(host.ConfigPath)) return null;
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

                // 主屏优先:Monitor0 → 第一个
                var chosen = PickMonitor(sel);
                if (chosen == null) continue;
                var p = chosen.Replace('/', Path.DirectorySeparatorChar);
                if (!File.Exists(p)) continue;

                // 视频/图片才认;scene.pkg / .exe / .html 之类取不到静帧 → 交给抓屏
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif"
                    or ".mp4" or ".webm" or ".mkv" or ".avi" or ".mov"))
                {
                    DzTrace.Log($"[HostLocator] 壁纸 {ext} 无法直接取静帧 → 走抓屏");
                    return null;
                }
                if (ext is not (".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif"))
                {
                    // 视频:同目录的 preview.jpg/png 通常就是它的封面静帧,尺寸可能与壁纸不同但可用
                    var prev = FindPreview(Path.GetDirectoryName(p));
                    if (prev != null) return new ResolvedFile(prev, "10", File.GetLastWriteTimeUtc(prev));
                    DzTrace.Log("[HostLocator] 视频壁纸且同目录无 preview 静帧 → 走抓屏");
                    return null;
                }
                return new ResolvedFile(p, "10", File.GetLastWriteTimeUtc(p));   // 壁纸软件一律按"填充"铺满
            }
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[HostLocator] 解析 config.json 失败: {ex.Message}");
        }
        return null;
    }

    static string? PickMonitor(JsonElement sel)
    {
        foreach (var m in sel.EnumerateObject())
            if (m.Name.Equals("Monitor0", StringComparison.OrdinalIgnoreCase)
                && m.Value.TryGetProperty("file", out var f0) && f0.ValueKind == JsonValueKind.String)
                return f0.GetString();
        foreach (var m in sel.EnumerateObject())
            if (m.Value.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String)
                return f.GetString();
        return null;
    }

    static string? FindPreview(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        foreach (var n in new[] { "preview.jpg", "preview.png", "preview.jpeg", "preview.gif" })
        {
            var p = Path.Combine(dir!, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
