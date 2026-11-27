using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Components;

namespace DesktopZones;

// -----------------------------------------------------------------------------
// 自测用：--ui-shot=<目录>
//
// 把「非分层窗口」逐个显示、截图后关闭，然后退出。用于 Phase 3 的
// AllowsTransparency=True -> WindowChrome 改造：改造前/后各跑一次，逐窗口对比
// 圆角、DWM 阴影、内容布局有没有退化。
//
// 截图用 GetDC(NULL)+BitBlt 直接抓屏幕（含 DWM 合成的圆角与阴影，这些不在
// WPF 视觉树里，RenderTargetBitmap 抓不到）；抓窗口矩形外扩 40px 边距以包含阴影。
//
// 这是纯自测开关（和已有的 --test-dialogs / --render-dock-preview 同类），
// 不影响正常启动路径：只有带该参数时才进这里。
//
// --ui-hold=<name> --ui-hold-ms=<ms>：只把一个窗口显示出来并保持，方便外部脚本
// 在这个状态下采样内存/CPU（改造前后对比用），到时后退出。
// -----------------------------------------------------------------------------
public partial class App
{
    const int ShotMargin = 40;

    /// <summary>要截图的窗口。返回 null 表示当前环境构造不了（记录到 manifest 后跳过）。</summary>
    IEnumerable<(string Name, Func<Window?> Make)> UiShotTargets()
    {
        yield return ("management", () => new ManagementWindow(
            _zoneManager!, _configService!, _notesService, _widgetService, _panelService));
        yield return ("property", () =>
        {
            var zone = _zoneManager!.Zones.FirstOrDefault();
            return zone == null ? null : new PropertyWindow(zone, _configService!);
        });

        // 播放器属性面板（律动条形态）：用于验证「律动条样式」段新增的「律动帧率」行。
        // 字段树比窗口高，RunUiShotsAsync 会把 Name=BarsFpsRow 的行滚进可视区再截图。
        yield return ("media-property", () => new PropertyWindow(
            new DesktopMediaPlayer { Mode = MediaDisplayMode.Bars }, _configService!)
        {
            Width = 420,
            Height = 820,
        });

        // ---- 二级弹窗（简单构造的） ----
        yield return ("rename", () => new RenameDialog("测试名称"));
        yield return ("overwrite", () => new OverwriteConfirmDialog("测试预设"));
        yield return ("save-preset", () => new SavePresetDialog(
            PresetService.For(PresetKind.Zone), new Zone { Name = "测试分区" }));
        yield return ("load-preset", () => new LoadPresetDialog(
            PresetService.For(PresetKind.Zone), _widgetService));
        yield return ("color-picker", () => new ColorPickerDialog("FF8800"));
        yield return ("emoji-picker", () => new EmojiPickerDialog());
        yield return ("icon-picker", () => new IconPickerDialog());
        yield return ("media-mode", () => new MediaModePickerDialog());
        yield return ("motion", () => new MotionSettingsDialog(
            HoverExpandAnimationKind.ScaleExpand, HoverExpandOrigin.ButtonCenter, 1.0));
        yield return ("panel-popup-motion", () => new PanelPopupMotionDialog(
            PanelPopupOrigin.TopLeft, HoverExpandAnimationKind.Fade, 1.0));
        yield return ("auto-organize", () => new AutoOrganizePickerWindow(
            new Zone { Name = "测试分区" }, AutoOrganizePickerKind.Extension));
        yield return ("shell-location", () => new ShellLocationPickerWindow());
        yield return ("image-crop", () =>
        {
            var img = Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "icon-256.png");
            if (!File.Exists(img)) return null;
            return new ImageCropPreviewWindow(img, 220, 140);
        });
    }

    /// <summary>窗口截图（屏幕像素）。窗口先抬到最上层，避免被分区分层窗口挡住。</summary>
    async Task RunUiShotsAsync(string dir, string? only = null)
    {
        Directory.CreateDirectory(dir);
        var manifest = new System.Text.StringBuilder();
        manifest.AppendLine($"ui-shot {DateTime.Now:yyyy-MM-dd HH:mm:ss}  dpiNote=screen-pixels  only={only ?? "*"}");
        var onlySet = (only ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, make) in UiShotTargets())
        {
            if (onlySet.Count > 0 && !onlySet.Contains(name)) continue;
            Window? w = null;
            try
            {
                w = make();
                if (w == null)
                {
                    manifest.AppendLine($"{name}: SKIP (null)");
                    continue;
                }
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.ShowInTaskbar = false;
                w.Left = 140;
                w.Top = 120;
                w.Topmost = true;          // 分区窗口是 Topmost，弹窗必须先到最上面才能截到
                var sw = System.Diagnostics.Stopwatch.StartNew();
                w.Show();
                w.Activate();
                await Task.Delay(700);     // 等主题资源 / 动画 / 内容生成
                w.UpdateLayout();

                // media-property：字段树比窗口高，要先把「律动帧率」行滚进可视区，
                // 截图里才能同时看到标题、滑块与默认值 30、hint。找不到行时也照常截图，
                // 并在 manifest 里标记（便于定位是布局问题还是命名问题）。
                string extra = "";
                if (name.Equals("media-property", StringComparison.OrdinalIgnoreCase))
                {
                    var row = FindElementByName(w, "BarsFpsRow");
                    extra = row == null ? " barsFpsRow=MISSING" : " barsFpsRow=found";
                    if (row != null)
                    {
                        row.BringIntoView();
                        await Task.Delay(350);
                        w.UpdateLayout();
                        // BringIntoView 只保证行本身可见；行下面还有一行 hint 小字，
                        // 再往下滚一点让它一起进画面（ScrollViewer 内还有更多字段，滚得动）。
                        if (w is PropertyWindow pw && pw.Body.FieldScroller != null)
                        {
                            pw.Body.FieldScroller.ScrollToVerticalOffset(
                                pw.Body.FieldScroller.VerticalOffset + 90);
                            extra += " scrolledHint=+90";
                        }
                        await Task.Delay(250);
                        w.UpdateLayout();
                        await Task.Delay(200);
                    }
                }

                await Task.Delay(120);
                sw.Stop();
                var path = Path.Combine(dir, name + ".png");
                var rect = CaptureWindow(w, path, ShotMargin);
                manifest.AppendLine($"{name}: {(rect == null ? "CAPTURE-FAILED" : rect)} openMs={sw.ElapsedMilliseconds}{extra}");
            }
            catch (Exception ex)
            {
                manifest.AppendLine($"{name}: ERROR {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { w?.Close(); } catch { }
                await Task.Delay(120);
            }
        }
        try { File.WriteAllText(Path.Combine(dir, "_manifest.txt"), manifest.ToString()); } catch { }
    }

    /// <summary>--ui-pages=&lt;csv&gt; --ui-pages-rounds=N：打开管理窗口，按固定顺序切页面 N 轮，
    /// 记录每页耗时（含同步布局）与轮次总耗时/内存 —— 用于验证页面缓存（Phase 4 P0）。</summary>
    async Task RunUiPagesAsync(int rounds, string outFile)
    {
        EnsureManagementWindow();
        var w = _managementWindow;
        if (w == null) return;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = 100; w.Top = 100;
        w.Show();
        w.Activate();
        await Task.Delay(1200);
        var sections = new[]
        {
            "zones", "docks", "merged", "panel", "calendar", "clock", "media",
            "sticky", "plugins", "settings", "zones", "settings"
        };
        // 预热一轮：让插件列表 / 缩略图等一次性初始化完成，之后的差异才是缓存效果
        foreach (var s in sections) { try { w.ShowSection(s); w.UpdateLayout(); } catch { } }
        await Task.Delay(300);
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        double mem0 = proc.PrivateMemorySize64;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("round,section,ms");
        var total = System.Diagnostics.Stopwatch.StartNew();
        for (int r = 1; r <= rounds; r++)
        {
            foreach (var s in sections)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { w.ShowSection(s); w.UpdateLayout(); } catch { }
                sw.Stop();
                sb.AppendLine($"{r},{s},{sw.Elapsed.TotalMilliseconds:0.0}");
            }
        }
        total.Stop();
        proc.Refresh();
        double mem1 = proc.PrivateMemorySize64;
        sb.AppendLine($"TOTAL,{rounds},{total.Elapsed.TotalMilliseconds:0.0}");
        sb.AppendLine($"MEM0_MB,{mem0 / 1048576.0:0.0}");
        sb.AppendLine($"MEM1_MB,{mem1 / 1048576.0:0.0}");
        try { File.WriteAllText(outFile, sb.ToString()); } catch { }
        try { w.Close(); } catch { }
    }

    async Task RunUiHoldAsync(string name, int holdMs)
    {
        var target = UiShotTargets().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Window? w = null;
        try
        {
            w = target.Make();
            if (w == null) return;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.ShowInTaskbar = false;
            w.Left = 140;
            w.Top = 120;
            w.Show();
            w.Activate();
            await Task.Delay(Math.Max(1000, holdMs));
        }
        catch (Exception ex)
        {
            try { DzTrace.Always("[ui-hold] " + ex); } catch { }
        }
        finally { try { w?.Close(); } catch { } }
    }

    /// <summary>--ui-cost=&lt;name&gt; --ui-cost-out=&lt;csv&gt;：同一进程内「开窗/关窗」反复采样私有内存，
    /// 用 (开窗 - 关窗) 的差值当这个窗口的实际内存成本 —— 避开跨进程/环境噪声。
    /// 同时记录构造+Show 的耗时与 idle CPU（两次采样之间的进程 CPU 增量）。</summary>
    async Task RunUiCostAsync(string name, int cycles, string outFile)
    {
        var target = UiShotTargets().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("cycle,windowMB,openMs,cpuMs");
        for (int i = 1; i <= cycles; i++)
        {
            Window? w = null;
            try
            {
                FullGc();
                await Task.Delay(300);
                proc.Refresh();
                double before = proc.PrivateMemorySize64;

                w = target.Make();
                if (w == null) { sb.AppendLine($"{i},null,0,0"); continue; }
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.ShowInTaskbar = false;
                w.Left = 140;
                w.Top = 120;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                w.Show();
                w.Activate();
                await Task.Delay(700);
                w.UpdateLayout();
                await Task.Delay(200);
                sw.Stop();

                FullGc();
                await Task.Delay(300);
                proc.Refresh();
                double open = proc.PrivateMemorySize64;
                double cpu0 = proc.TotalProcessorTime.TotalMilliseconds;
                await Task.Delay(1500);   // idle 采样
                proc.Refresh();
                double cpuMs = proc.TotalProcessorTime.TotalMilliseconds - cpu0;

                sb.AppendLine($"{i},{Math.Round((open - before) / 1048576.0, 1)},{sw.ElapsedMilliseconds},{Math.Round(cpuMs)}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{i},error:{ex.GetType().Name},0,0");
            }
            finally
            {
                try { w?.Close(); } catch { }
                await Task.Delay(400);
                try { FullGc(); } catch { }
            }
        }
        try { File.WriteAllText(outFile, sb.ToString()); } catch { }
    }

    /// <summary>按 <see cref="FrameworkElement.Name"/> 在视觉树里递归找元素
    /// （--ui-shot 定位/滚动到字段行用；代码里 new 出来的行没有 NameScope，FindName 找不到）。</summary>
    static FrameworkElement? FindElementByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return fe;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var found = FindElementByName(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
            if (found != null) return found;
        }
        return null;
    }

#if DEBUG
    /// <summary>
    /// 自测：--ui-bars-fps=&lt;csv&gt; --ui-bars-out=&lt;文件&gt;
    ///
    /// 进程内建一个普通窗口（窗口内容就是 <see cref="SpectrumBars"/>），按 csv 里的帧率
    /// 逐个设置 TargetFps + Apply（与属性面板 → ApplyBarsSettings 同一条路径），每档采样
    /// 2 秒，记录 PaintedFrames 增量 / 实测 fps / 进程 CPU 时间增量；最后再做一次
    /// live 变更（60fps 1s → 10fps 1s），验证「拖滑块实时生效」那条路径。
    /// 结果写 <paramref name="outFile"/>（CSV）。
    /// </summary>
    async Task RunUiBarsFpsProbeAsync(string fpsCsv, string outFile)
    {
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# ui-bars-fps {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                      $"screen={SystemParameters.PrimaryScreenWidth:0}x{SystemParameters.PrimaryScreenHeight:0}");
        sb.AppendLine("step,targetFps,paintedFrames,seconds,actualFps,cpuMs");

        var values = new List<int>();
        foreach (var part in fpsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int v)) values.Add(v);
        if (values.Count == 0) values.AddRange(new[] { 0, 5, 24, 30, 60, 100 });

        Window? w = null;
        SpectrumBars? bars = null;
        try
        {
            bars = new SpectrumBars
            {
                BarCount = 32,
                BarWidth = 4,
                Source = "Simulated",     // 模拟律动：HasAudioSignal 恒 true，不会触发静音降帧
                TargetFps = values[0],
                ColorMode = "Gradient",
            };
            w = new Window
            {
                Title = "bars-fps-probe",
                Width = 640,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 120,
                Top = 120,
                ShowInTaskbar = false,
                Topmost = true,
                Background = System.Windows.Media.Brushes.Black,
                Content = bars,
            };
            w.Show();
            w.Activate();
            await Task.Delay(800);        // 等窗口加载 / 首帧
            w.UpdateLayout();
            bars.SetPlaying(true, false); // 开始播放；出帧速率由 TargetFps 决定

            // 单档采样：改 TargetFps + Apply → 200ms 让上一档尾部过去 → 采样 ms 毫秒。
            async Task<string> SampleAsync(int target, int ms, string tag)
            {
                bars!.TargetFps = target;
                bars.Apply();
                await Task.Delay(200);
                long f0 = bars.PaintedFrames;
                proc.Refresh();
                double cpu0 = proc.TotalProcessorTime.TotalMilliseconds;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                await Task.Delay(ms);
                sw.Stop();
                long frames = bars.PaintedFrames - f0;
                proc.Refresh();
                double cpuMs = proc.TotalProcessorTime.TotalMilliseconds - cpu0;
                double actual = frames / sw.Elapsed.TotalSeconds;
                return $"{tag},{target},{frames},{sw.Elapsed.TotalSeconds:0.000},{actual:0.0},{cpuMs:0}";
            }

            foreach (int fps in values)
                sb.AppendLine(await SampleAsync(fps, 2000, fps.ToString()));

            // live 变更验证：不重建窗口，只改 TargetFps + Apply（滑块实时生效走的就是这条路径）。
            sb.AppendLine(await SampleAsync(60, 1000, "live-60"));
            sb.AppendLine(await SampleAsync(10, 1000, "live-10"));
        }
        catch (Exception ex)
        {
            sb.AppendLine($"ERROR,{ex.GetType().Name},{ex.Message.Replace(',', ';').Replace('\n', ' ').Replace('\r', ' ')}");
        }
        finally
        {
            try { w?.Close(); } catch { }
            try { File.WriteAllText(outFile, sb.ToString()); } catch { }
        }
    }
#endif

    static void FullGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    // ---- 屏幕抓取 ---------------------------------------------------------------

    static string? CaptureWindow(Window w, string path, int margin)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return null;
        if (!ShotGetWindowRect(hwnd, out var r)) return null;

        int x = r.Left - margin, y = r.Top - margin;
        int cw = (r.Right - r.Left) + margin * 2;
        int ch = (r.Bottom - r.Top) + margin * 2;
        if (cw <= 0 || ch <= 0) return null;

        // 限制在虚拟桌面内（多屏时虚拟桌面原点可能是负数）
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int sx = Math.Max(x, vx), sy = Math.Max(y, vy);
        int ex = Math.Min(x + cw, vx + vw), ey = Math.Min(y + ch, vy + vh);
        if (ex - sx <= 0 || ey - sy <= 0) return null;
        cw = ex - sx; ch = ey - sy;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = CreateCompatibleBitmap(screenDc, cw, ch);
        IntPtr old = SelectObject(memDc, hBmp);
        try
        {
            if (!BitBlt(memDc, 0, 0, cw, ch, screenDc, sx, sy, SRCCOPY)) return null;
            var bs = Imaging.CreateBitmapSourceFromHBitmap(hBmp, IntPtr.Zero,
                Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bs));
            using var fs = File.Create(path);
            enc.Save(fs);
            return $"({sx},{sy}) {cw}x{ch}";
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBmp);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] static extern bool ShotGetWindowRect(IntPtr hWnd, out ShotRect lpRect);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr hdc, int x, int y, int w, int h,
        IntPtr hdcSrc, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    struct ShotRect { public int Left, Top, Right, Bottom; }
}
