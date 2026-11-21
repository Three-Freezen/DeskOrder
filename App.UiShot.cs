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

// 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
// 鑷祴鐢細--ui-shot=<鐩綍>
//
// 鎶娿€岄潪鍒嗗眰绐楀彛銆嶉€愪釜鏄剧ず銆佹埅鍥惧悗鍏抽棴锛岀劧鍚庨€€鍑恒€傜敤浜?Phase 3 鐨?// AllowsTransparency=True 鈫?WindowChrome 鏀归€狅細鏀归€犲墠/鍚庡悇璺戜竴娆★紝閫愮獥鍙ｅ姣?// 鍦嗚銆丏WM 闃村奖銆佸唴瀹瑰竷灞€鏈夋病鏈夐€€鍖栥€?//
// 鎴浘鐢?GetDC(NULL)+BitBlt 鐩存帴鎶撳睆骞曪紙鍚?DWM 鍚堟垚鐨勫渾瑙掍笌闃村奖锛岃繖浜涗笉鍦?// WPF 瑙嗚鏍戦噷锛孯enderTargetBitmap 鎶撲笉鍒帮級锛涙姄绐楀彛鐭╁舰澶栨墿 40px 杈硅窛浠ュ寘鍚槾褰便€?//
// 杩欐槸绾嚜娴嬪紑鍏筹紙鍜屽凡鏈夌殑 --test-dialogs / --render-dock-preview 鍚岀被锛夛紝
// 涓嶅奖鍝嶆甯稿惎鍔ㄨ矾寰勶細鍙湁甯﹁鍙傛暟鏃舵墠杩涜繖閲屻€?// 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€
public partial class App
{
    const int ShotMargin = 40;

    /// <summary>瑕佹埅鍥剧殑绐楀彛銆傝繑鍥?null 琛ㄧず褰撳墠鐜鏋勯€犱笉浜嗭紙璁板綍鍒?manifest 鍚庤烦杩囷級銆?/summary>
    IEnumerable<(string Name, Func<Window?> Make)> UiShotTargets()
    {
        yield return ("management", () => new ManagementWindow(
            _zoneManager!, _configService!, _notesService, _widgetService, _panelService));
        yield return ("property", () =>
        {
            var zone = _zoneManager!.Zones.FirstOrDefault();
            return zone == null ? null : new PropertyWindow(zone, _configService!);
        });

        // 鈹€鈹€ 浜岀骇寮圭獥锛堢畝鍗曟瀯閫犵殑锛?鈹€鈹€
        yield return ("rename", () => new RenameDialog("娴嬭瘯鍚嶇О"));
        yield return ("overwrite", () => new OverwriteConfirmDialog("娴嬭瘯棰勮"));
        yield return ("save-preset", () => new SavePresetDialog(
            PresetService.For(PresetKind.Zone), new Zone { Name = "娴嬭瘯鍒嗗尯" }));
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
            new Zone { Name = "娴嬭瘯鍒嗗尯" }, AutoOrganizePickerKind.Extension));
        yield return ("shell-location", () => new ShellLocationPickerWindow());
        yield return ("image-crop", () =>
        {
            var img = Path.Combine(AppContext.BaseDirectory, "Resources", "Icons", "icon-256.png");
            if (!File.Exists(img)) return null;
            return new ImageCropPreviewWindow(img, 220, 140);
        });
    }

    /// <summary>绐楀彛鎴浘锛堝睆骞曞儚绱狅級銆傜獥鍙ｅ厛鎶埌鏈€涓婂眰锛岄伩鍏嶈鍒嗗尯鍒嗗眰绐楀彛鎸′綇銆?/summary>
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
                w.Topmost = true;          // 鍒嗗尯绐楀彛鏄?Topmost锛屽脊绐楀繀椤诲厛鍒版渶涓婇潰鎵嶈兘鎴埌
                w.Show();
                w.Activate();
                await Task.Delay(700);     // 绛変富棰樿祫婧?/ 鍔ㄧ敾 / 鍐呭鐢熸垚
                w.UpdateLayout();
                await Task.Delay(120);
                var path = Path.Combine(dir, name + ".png");
                var rect = CaptureWindow(w, path, ShotMargin);
                manifest.AppendLine($"{name}: {(rect == null ? "CAPTURE-FAILED" : rect)}");
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

    // 鈹€鈹€ 灞忓箷鎶撳彇 鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€鈹€

    static string? CaptureWindow(Window w, string path, int margin)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return null;
        if (!ShotGetWindowRect(hwnd, out var r)) return null;

        int x = r.Left - margin, y = r.Top - margin;
        int cw = (r.Right - r.Left) + margin * 2;
        int ch = (r.Bottom - r.Top) + margin * 2;
        if (cw <= 0 || ch <= 0) return null;

        // 闄愬埗鍦ㄨ櫄鎷熸闈㈠唴锛堝灞忔椂铏氭嫙妗岄潰鍘熺偣鍙兘鏄礋鏁帮級
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

