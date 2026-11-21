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
                await Task.Delay(120);
                sw.Stop();
                var path = Path.Combine(dir, name + ".png");
                var rect = CaptureWindow(w, path, ShotMargin);
                manifest.AppendLine($"{name}: {(rect == null ? "CAPTURE-FAILED" : rect)} openMs={sw.ElapsedMilliseconds}");
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

    /// <summary>--ui-hold：只把一个窗口显示出来并保持，供外部采样内存/CPU。</summary>
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
