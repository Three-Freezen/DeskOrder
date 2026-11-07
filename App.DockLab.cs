using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using DesktopZones.Models;
using DesktopZones.Views;
using DBitmap = System.Drawing.Bitmap;
using DColor = System.Drawing.Color;
using DGraphics = System.Drawing.Graphics;
using DGraphicsUnit = System.Drawing.GraphicsUnit;
using DCopyOp = System.Drawing.CopyPixelOperation;
using DImageFormat = System.Drawing.Imaging.ImageFormat;
using DPixelFormat = System.Drawing.Imaging.PixelFormat;
using DRect = System.Drawing.Rectangle;
using DSize = System.Drawing.Size;

#if DEBUG

namespace DesktopZones;

/// <summary>
/// 自测台（<c>--dock-lab=&lt;dir&gt;</c>，仅 DEBUG）。快捷栏这几个问题的**现场复现器**：
/// 不靠推理，直接把窗口摆到真实桌面上、用真实鼠标输入（SetCursorPos/mouse_event）触发，
/// 再把屏幕像素抓下来做差分、把内部状态打成文本。
///
/// 场景（<c>--dock-lab-case=ring|ring2|hover|drag|zorder|glass|glasswin|all</c>）：
///   · ring  —— 最小化→展开后「周围一圈鬼影/透明边框」：HideZone()/ShowZone() 路径。
///   · ring2 —— 同上，但展开走**真实的恢复按钮点击**（用户的日常路径）。
///   · hover —— 「显示名称」时悬停图标、缩放动画是否一直抽搐。
///   · drag  —— 「打开软件的同时拖动图标 → 图标卡在动画里」。
///   · zorder —— 玻璃层（独立 HWND）会不会被内容层的 PinBelowProgman / BringToFront
///               顶到上面去（用户报的「两层玻璃、点一下边缘下面那层就盖上来」）。
///               含一段「故意翻过来」的强制复现 + 守卫自愈验证。
///   · glassfit —— 玻璃层和内容层到底差几个像素（用户报的「WPF 层明显大一圈」）：
///               A 段在**纯色背景**上逐个试 `SetWindowRgn` / 圆角偏好，量出「accent 背板
///               不认窗口区域、只认系统圆角」；B 段量真实胶囊的每侧内缩与四角半径。
/// </summary>
public partial class App
{
    [StructLayout(LayoutKind.Sequential)]
    struct LabPoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct LabRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out LabPoint p);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(LabPoint p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hWnd, ref LabPoint p);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out LabRect r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)] static extern bool SetWindowPosEx(IntPtr hWnd, IntPtr after,
        int x, int y, int cx, int cy, uint flags);
    const uint SWP_NOSIZE_ = 0x0001, SWP_NOMOVE_ = 0x0002, SWP_NOACTIVATE_ = 0x0010, SWP_SHOWWINDOW_ = 0x0040;
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int val, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, ref LabRect val, int size);

    const uint WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202;

    /// <summary>GetWindow 的 z 序遍历命令：PREV = 上面一个，NEXT = 下面一个。</summary>
    const uint GW_HWNDPREV = 3, GW_HWNDNEXT = 2;

    const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    static string? ParseDockLabArg(string[] args) => ParseArgValue(args, "--dock-lab=");
    static string ParseDockLabCase(string[] args)
        => ParseArgValue(args, "--dock-lab-case=") ?? "all";

    async Task RunDockLabAsync(string dir, string which)
    {
        Directory.CreateDirectory(dir);
        var log = new StringBuilder();
        void Say(string s) { log.AppendLine(s); System.Diagnostics.Debug.WriteLine("[lab] " + s); }
        Say($"# dock lab {DateTime.Now:yyyy-MM-dd HH:mm:ss} case={which} workarea={SystemParameters.WorkArea}");
        try
        {
            if (which is "cursor") { await LabCursorProbeAsync(Say); return; }
            if (which is "glass") { await LabGlassAsync(dir, Say); return; }
            if (which is "dwmscope") { await LabDwmScopeAsync(dir, Say); return; }
            if (which is "layerglass") { await LabLayerGlassAsync(dir, Say); return; }
            if (which is "glasswin") { await LabGlassWindowAsync(dir, Say); return; }
            if (which is "glasscmp") { await LabGlassCompareAsync(dir, Say); return; }
            if (which is "glassab") { await LabGlassAbAsync(dir, Say); return; }
            if (which is "glassdiff") { await LabGlassDiffAsync(dir, Say); return; }
            if (which is "glassshape") { await LabGlassShapeAsync(dir, Say); return; }
            if (which is "glassfit") { await LabGlassFitAsync(dir, Say); return; }
            if (which is "widgetfit") { await LabWidgetFitAsync(dir, Say); return; }
            if (which is "zorder") { await LabZOrderAsync(dir, Say); return; }
            if (which is "state" or "all") await LabStateAsync(dir, Say);
            if (which is "ring" or "all") await LabRingAsync(dir, Say);
            if (which is "ring2" or "all") await LabRingClickAsync(dir, Say);
            if (which is "hover" or "all") await LabHoverAsync(dir, Say);
            if (which is "drag" or "all") await LabDragAsync(dir, Say);
        }
        catch (Exception ex) { Say("LAB EXC " + ex); }
        finally { try { File.WriteAllText(Path.Combine(dir, "lab.txt"), log.ToString()); } catch { } }
    }

    // ── 探针：这台机器上能不能用 SetCursorPos / 合成鼠标消息 ──────────────

    async Task LabCursorProbeAsync(Action<string> Say)
    {
        Say($"integrity={DesktopZones.Helpers.ProcessIntegrity.Describe} restricted={DesktopZones.Helpers.ProcessIntegrity.IsRestricted}");
        var before = CursorPos();
        Say($"cursor before={before}");
        bool ok = SetCursorPos(1000, 500);
        await Task.Delay(150);
        Say($"SetCursorPos(1000,500) ok={ok} now={CursorPos()}");
        await Task.Delay(400);
        Say($"cursor 400ms later={CursorPos()}");
        SetCursorPos(before.X, before.Y);
        await Task.Delay(100);
        Say($"cursor restored={CursorPos()}");
        // 自己的窗口在不在前台/能不能收到合成消息
        var w = new Window
        {
            Width = 240, Height = 120, Left = 1200, Top = 600,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x20, 0x40, 0x80)),
        };
        w.Show();
        await Task.Delay(500);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        Say($"probe window hwnd=0x{hwnd.ToInt64():X} rect=({w.Left},{w.Top})");
        var p = CursorPos();
        bool moved = SetCursorPos(1300, 650);
        await Task.Delay(200);
        Say($"cursor over probe ok={moved} now={CursorPos()} directlyOver={System.Windows.Input.Mouse.DirectlyOver?.GetType().Name ?? "null"} " +
            $"wIsMouseOver={w.IsMouseOver} wActive={w.IsActive}");
        PostMouse(hwnd, WM_MOUSEMOVE, (1300, 650), Say, "synthetic move");
        await Task.Delay(200);
        Say($"after synthetic: directlyOver={System.Windows.Input.Mouse.DirectlyOver?.GetType().Name ?? "null"} wIsMouseOver={w.IsMouseOver}");
        PostMouse(hwnd, WM_LBUTTONDOWN, (1300, 650), Say, "synthetic down");
        PostMouse(hwnd, WM_LBUTTONUP, (1300, 650), Say, "synthetic up");
        await Task.Delay(200);
        SetCursorPos(p.X, p.Y);
        w.Close();
    }

    // ── 场景 0：状态机自测（拖动收尾 / 悬停幂等 / 锁定三道闸 / 玻璃） ─────

    async Task LabStateAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== state: 拖动收尾 / 悬停幂等 / 锁定的闸 ==");

        var z = MakeLabDock(false, "lab state",
            new[] { @"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\mspaint.exe", @"C:\Windows\System32\cmd.exe" });
        var win = new ZoneWindow(z, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
        { ShowInTaskbar = false };
        win.Show();
        await Task.Delay(1400);
        win.UpdateLayout();
        try
        {
            Say("dragWiring: " + win.PreviewDragWiring());
            Say("hoverIdem: " + win.PreviewHoverIdempotence());
            Say("lockGuards: " + win.PreviewLockGuards());
            Say("hover log: " + win.DebugHoverLog());
        }
        catch (Exception ex) { Say("state EXC " + ex); }
        finally { win.Close(); }
    }

    // ── 场景 1a：最小化 → 展开（HideZone/ShowZone）────────────────────────

    async Task LabRingAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== ring: 最小化 → 展开 的残影 ==");
        var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
        if (dock == null)
        {
            Say("配置里没有快捷栏 → 新建一条测试用的");
            dock = _zoneManager.CreateDock("lab dock");
            AddLabItems(dock, new[] { @"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\mspaint.exe", @"C:\Windows\System32\cmd.exe" });
        }
        _zoneManager.ShowZone(dock);
        var win = _zoneManager.GetZoneWindow(dock.Id);
        if (win == null) { Say("拿不到窗口 → 跳过本场景"); return; }

        Say($"dock='{dock.Name}' model={dock.Width}x{dock.Height} win=({win.Left},{win.Top}) {win.ActualWidth}x{win.ActualHeight} " +
            $"tile={dock.TileMode} glass={dock.EnableLiquidGlass} anim={dock.DockAnimate}/{dock.DockMagnify} refl={dock.DockReflection}");
        MoveToFreeSpot(win, Say);
        await Task.Delay(1200);
        win.UpdateLayout();
        Say("dwm fresh: " + DwmState(win));

        var region = CaptureRegion(win, 40);
        Say($"capture region={region}");

        win.Hide();
        await Task.Delay(600);
        using var reference = Grab(region);
        SavePng(reference, Path.Combine(dir, "ring-ref-desktop.png"));
        win.Show();
        await Task.Delay(1300);

        using (var fresh = Grab(region))
        {
            SavePng(fresh, Path.Combine(dir, "ring-1-fresh.png"));
            ReportDiff(fresh, reference, win, dock, region, "fresh", dir, Say);
        }

        win.HideZone();
        await Task.Delay(2200);
        Say("dwm minimized: " + DwmState(win) + " | " + win.DebugContentState());
        using (var min = Grab(region))
        {
            SavePng(min, Path.Combine(dir, "ring-2-minimized.png"));
            ReportDiff(min, reference, win, dock, region, "minimized", dir, Say);
        }

        win.ShowZone();
        await Task.Delay(2200);
        Say("dwm expanded: " + DwmState(win) + " | " + win.DebugContentState());
        using (var exp = Grab(region))
        {
            SavePng(exp, Path.Combine(dir, "ring-3-expanded.png"));
            ReportDiff(exp, reference, win, dock, region, "expanded", dir, Say);
        }
    }

    // ── 场景 4：玻璃（磁贴 + 玻璃 / 渲染方案开关）────────────────────────

    async Task LabGlassAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glass: 磁贴 + 玻璃，渲染方案跟着设置走 ==");
        var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
        if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }
        _zoneManager.ShowZone(dock);
        var win = _zoneManager.GetZoneWindow(dock.Id);
        if (win == null) { Say("拿不到窗口 → 跳过"); return; }
        MoveToFreeSpot(win, Say);
        await Task.Delay(1400);
        win.UpdateLayout();

        var region = CaptureRegion(win, 30);
        void Shot(string name)
        {
            win.PreviewResetHover();
            SaveCrop(Grab(region), Path.Combine(dir, name));
        }

        try
        {
            Say($"config: tile={dock.TileMode} glass={dock.EnableLiquidGlass} mat={dock.GlassMaterial} " +
                $"blur={dock.GlassBlurAmount} tint={dock.GlassTintOpacity} mode={dock.GlassColorMode} fill={dock.FillColor}");
            Say("A 原样（config 里磁贴+玻璃）: " + win.DebugDockGlass());
            await Task.Delay(250);
            Shot("glass-1-config-tile-on.png");

            dock.TileMode = false;
            win.RefreshZone(dock);
            await Task.Delay(900);
            Say("B 关掉磁贴: " + win.DebugDockGlass());
            Shot("glass-2-tile-off.png");

            DesktopZones.Helpers.WallpaperSource.Enabled = true;   // 自绘背板方案
            win.RefreshZone(dock);
            await Task.Delay(900);
            Say("C 渲染方案=自绘背板: " + win.DebugDockGlass());
            await Task.Delay(250);
            Shot("glass-3-selfdrawn.png");

            DesktopZones.Helpers.WallpaperSource.Enabled = false;  // DWM 方案
            win.RefreshZone(dock);
            await Task.Delay(900);
            Say("D 渲染方案=DWM: " + win.DebugDockGlass());
            Shot("glass-4-dwm.png");

            dock.EnableLiquidGlass = false;
            win.RefreshZone(dock);
            await Task.Delay(900);
            Say("E 关掉玻璃(对照): " + win.DebugDockGlass());
            await Task.Delay(250);
            Shot("glass-5-off.png");
        }
        finally
        {
            DesktopZones.Helpers.WallpaperSource.Enabled =
                _configService?.Load().UseWallpaperRenderer ?? false;
        }
    }

    // ── 场景 4b：DWM 的模糊到底盖多大一块？（第 7 轮）─────────────────────
    //
    // 第 4~6 轮一直假定「DWM 只能整窗 → 快捷栏窗口比胶囊高，一开就把两条透明带糊上」，
    // 但这个结论**从来没有实测过**（当年看到的失败其实是磁贴模式把玻璃整个吞了）。
    // 这里直接把分区/律动条那套 EnableBlurComposite 硬打到快捷栏窗口上，用像素差分分别数
    // 「胶囊内」「胶囊外窗口内（= 两条带）」「窗口外」三块各变了多少像素。

    async Task LabDwmScopeAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== dwmscope: DWM 模糊是整窗，还是只盖胶囊？ ==");
        var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
        if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }

        dock.TileMode = false;              // 磁贴会把胶囊填充整个撤掉，看不出玻璃
        dock.EnableLiquidGlass = true;
        _zoneManager.ShowZone(dock);
        var win = _zoneManager.GetZoneWindow(dock.Id);
        if (win == null) { Say("拿不到窗口 → 跳过"); return; }
        win.RefreshZone(dock);
        MoveToFreeSpot(win, Say);
        await Task.Delay(1400);
        win.UpdateLayout();

        Say($"geom win={win.ActualWidth:F0}x{win.ActualHeight:F0} " +
            $"pill={DesktopZones.Helpers.ZoneLayout.DockPillWidth(dock, dock.Items.Count):F0}" +
            $"x{DesktopZones.Helpers.ZoneLayout.DockBarHeight(dock):F0} " +
            $"top={DesktopZones.Helpers.ZoneLayout.DockOverflowTop(dock):F0} " +
            $"side={DesktopZones.Helpers.ZoneLayout.DockSideOverflow(dock):F0} " +
            $"refl={DesktopZones.Helpers.ZoneLayout.DockReflectBand(dock):F0} " +
            $"anim={dock.DockAnimate}/{dock.DockMagnify} reflOn={dock.DockReflection}");
        var region = CaptureRegion(win, 40);

        // 参照帧：藏起窗口抓「纯桌面」。抓两次 → 顺带量出「壁纸自己会动」的假阳性基线。
        win.Hide();
        await Task.Delay(700);
        using var refA = Grab(region);
        await Task.Delay(700);
        using var refB = Grab(region);
        win.Show();
        await Task.Delay(1500);
        ReportDiff(refB, refA, win, dock, region, "noise(两帧空桌面)", dir, Say);
        SavePng(refA, Path.Combine(dir, "dwmscope-ref.png"));

        async Task Probe(string tag)
        {
            await Task.Delay(900);
            using var shot = Grab(region);
            SavePng(shot, Path.Combine(dir, $"dwmscope-{tag}.png"));
            ReportDiff(shot, refA, win, dock, region, tag, dir, Say);
        }

        try
        {
            Say("0 现状（自壁画刷）: " + win.DebugDockGlass());
            await Probe("0-current");

            Say("1 DWM 整窗: " + win.DebugForceDwmGlass(1));
            await Probe("1-dwm-full");

            Say("2 DWM + 窗口裁成胶囊: " + win.DebugForceDwmGlass(2));
            await Probe("2-dwm-region");
        }
        finally
        {
            Say("3 复原: " + win.DebugForceDwmGlass(0));
            dock.TileMode = true;           // 这条 lab 快捷栏本来就是磁贴 + 玻璃
            win.RefreshZone(dock);
        }
    }

    // ── 场景 4c：胶囊玻璃走「统一图层」后，静止时改模糊/着色到底生效不生效 ──
    //
    // 老写法（CreateStaticBrush + "rect 没变就 return"）在窗口静止时**什么都改不动**：
    // 换壁纸不更新、拖模糊滑块不更新、改玻璃着色也不更新。现在换成分区的
    // WallpaperBackdropLayer（认 Generation / 半径 / 着色），这三个都应该立刻生效。
    // 用「胶囊区的相邻像素对比度（锐度）」量模糊：半径越大越平滑。这套指标对壁纸自己
    // 在动不敏感（动的是内容不是纹理）。

    async Task LabLayerGlassAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== layerglass: 静止状态下 半径/着色/壁纸世代 是否生效 ==");
        var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
        if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }

        int oldBlur = dock.GlassBlurAmount, oldTint = dock.GlassTintOpacity;
        bool oldTile = dock.TileMode, oldGlass = dock.EnableLiquidGlass;
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        try
        {
            dock.TileMode = false;                 // 磁贴会把填充撤掉，看不出玻璃
            dock.EnableLiquidGlass = true;
            DesktopZones.Helpers.WallpaperSource.Enabled = true;   // 自绘（统一图层）方案
            _zoneManager.ShowZone(dock);
            var win = _zoneManager.GetZoneWindow(dock.Id);
            if (win == null) { Say("拿不到窗口 → 跳过"); return; }
            win.RefreshZone(dock);
            MoveToFreeSpot(win, Say);
            await Task.Delay(1500);
            win.UpdateLayout();

            var region = CaptureRegion(win, 30);
            var pill = PillRectInRegion(win, dock, region);
            Say($"region={region} pillInRegion={pill}");
            Say("state: " + win.DebugDockGlass());

            async Task<(double pillSharp, double bandSharp, double outSharp, double mean)> Probe(string tag)
            {
                await Task.Delay(900);
                using var shot = Grab(region);
                SavePng(shot, Path.Combine(dir, $"lg-{tag}.png"));
                var bandTop = new DRect(pill.X, pill.Y - 20, pill.Width, 16);
                var outside = new DRect(region.Width - 60, region.Height - 40, 40, 24);
                double ps = Sharpness(shot, pill);
                double bs = Sharpness(shot, bandTop);
                double os = Sharpness(shot, outside);
                double mean = MeanLuma(shot, pill);
                Say($"  [{tag}] pillSharp={ps:F1} bandSharp={bs:F1} outsideSharp={os:F1} pillMeanLuma={mean:F1} " +
                    win.DebugDockGlass());
                return (ps, bs, os, mean);
            }

            dock.GlassBlurAmount = 8;
            win.RefreshZone(dock);
            var a = await Probe("1-blur8");

            // 窗口一动不动，只改模糊半径 → 老写法这里不会变
            dock.GlassBlurAmount = 50;
            win.RefreshZone(dock);
            var b = await Probe("2-blur50");

            // 再只改玻璃着色不透明度 → 老写法同样不会变
            dock.GlassBlurAmount = 8;
            dock.GlassTintOpacity = 95;
            win.RefreshZone(dock);
            var c = await Probe("3-tint95");

            // 世代号：模拟「换壁纸/重新采样」（缓存作废、世代 +1），图层必须重取
            int genBefore = DesktopZones.Helpers.WallpaperBackdrop.Generation;
            DesktopZones.Helpers.WallpaperBackdrop.Invalidate();
            int genAfter = DesktopZones.Helpers.WallpaperBackdrop.Generation;
            win.RefreshZone(dock);
            var d = await Probe("4-generation");
            Say($"  generation {genBefore} → {genAfter}（图层按新世代重取）");

            // 关键：**不调用任何刷新**，只作废壁纸 —— 图层必须靠 WallpaperBackdrop.Changed 自己重取
            Say("  layer before invalidate: " + win.DebugDockGlassLayer());
            DesktopZones.Helpers.WallpaperBackdrop.Invalidate();
            await Task.Delay(1500);
            Say("  layer after  invalidate(无任何 RefreshZone): " + win.DebugDockGlassLayer());

            Say($"结论：模糊 8→50 锐度 {a.pillSharp:F1}→{b.pillSharp:F1}（应下降）；" +
                $"着色 30→95 亮度 {b.mean:F1}→{c.mean:F1}（应变化）；" +
                $"世代重取后锐度 {d.pillSharp:F1} 亮度 {d.mean:F1}");
        }
        finally
        {
            dock.GlassBlurAmount = oldBlur;
            dock.GlassTintOpacity = oldTint;
            dock.TileMode = oldTile;
            dock.EnableLiquidGlass = oldGlass;
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
            var w2 = _zoneManager?.GetZoneWindow(dock.Id);
            if (w2 != null) w2.RefreshZone(dock);
        }
    }

    /// <summary>胶囊在「抓屏坐标系」里的矩形。</summary>
    static DRect PillRectInRegion(ZoneWindow win, Zone dock, DRect region)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
        var tl = win.PointToScreen(new System.Windows.Point(0, 0));
        double side = DesktopZones.Helpers.ZoneLayout.DockSideOverflow(dock);
        double top = DesktopZones.Helpers.ZoneLayout.DockBarTop(dock);
        double w = DesktopZones.Helpers.ZoneLayout.DockPillWidth(dock, dock.Items.Count);
        double h = DesktopZones.Helpers.ZoneLayout.DockBarHeight(dock);
        return new DRect(
            (int)Math.Floor(tl.X) - region.X + (int)Math.Round(side * dpi.DpiScaleX),
            (int)Math.Floor(tl.Y) - region.Y + (int)Math.Round(top * dpi.DpiScaleY),
            Math.Max(4, (int)Math.Round(w * dpi.DpiScaleX)),
            Math.Max(4, (int)Math.Round(h * dpi.DpiScaleY)));
    }

    /// <summary>两张图在区域内的平均绝对差（判断"这块画面有没有在动"）。</summary>
    static double MeanAbsDiff(DBitmap a, DBitmap b, DRect r)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(Math.Min(a.Width, b.Width), r.Right);
        int y1 = Math.Min(Math.Min(a.Height, b.Height), r.Bottom);
        double sum = 0; int n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
                sum += (Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B)) / 3.0;
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>区域「锐度」= 相邻像素的水平亮度差均值。模糊越重越小。</summary>
    static double Sharpness(DBitmap bmp, DRect r)
    {
        int x0 = Math.Max(1, r.X), y0 = Math.Max(1, r.Y);
        int x1 = Math.Min(bmp.Width - 2, r.Right - 2), y1 = Math.Min(bmp.Height - 2, r.Bottom - 2);
        double sum = 0; int n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                var p = bmp.GetPixel(x, y); var q = bmp.GetPixel(x + 1, y);
                sum += Math.Abs((p.R + p.G + p.B) - (q.R + q.G + q.B)) / 3.0;
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>区域平均亮度。</summary>
    static double MeanLuma(DBitmap bmp, DRect r)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(bmp.Width, r.Right), y1 = Math.Min(bmp.Height, r.Bottom);
        double sum = 0; int n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                var p = bmp.GetPixel(x, y);
                sum += (p.R + p.G + p.B) / 3.0; n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    // ── 场景 5：B —— 胶囊大小的独立「玻璃窗」吃真 DWM（实景模糊） ──────────
    //
    // 要验证四件事：
    //   ① 玻璃窗存在、正好胶囊大小、在内容窗口正下方；
    //   ② 胶囊里是玻璃（比周围壁纸模糊），而两条透明带**没有**被糊；
    //   ③ 点击仍然落在内容窗口（玻璃窗 WS_EX_TRANSPARENT 不抢命中）；
    //   ④ 切回「壁纸采样」方案时玻璃窗藏起来、图层接管。

    async Task LabGlassWindowAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glasswin: 双窗（内容层 + 胶囊玻璃层） ==");
        var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
        if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }

        bool oldTile = dock.TileMode, oldGlass = dock.EnableLiquidGlass;
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        try
        {
            dock.TileMode = false;
            dock.EnableLiquidGlass = true;
            DesktopZones.Helpers.WallpaperSource.Enabled = false;   // 渲染方案 = DWM
            _zoneManager.ShowZone(dock);
            var win = _zoneManager.GetZoneWindow(dock.Id);
            if (win == null) { Say("拿不到窗口 → 跳过"); return; }
            win.RefreshZone(dock);
            MoveToFreeSpot(win, Say);
            await Task.Delay(1600);
            win.UpdateLayout();

            var region = CaptureRegion(win, 30);
            var pill = PillRectInRegion(win, dock, region);
            var contentHwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            var glassHwnd = win.DebugDockGlassHwnd();
            Say($"content=0x{contentHwnd.ToInt64():X} glass=0x{glassHwnd.ToInt64():X}");
            Say("state: " + win.DebugDockGlass());
            Say("layer: " + win.DebugDockGlassLayer());

            // ①② 画面：胶囊锐度应明显低于周围壁纸；顶部带上沿不应有玻璃
            using (var shot = Grab(region))
            {
                SavePng(shot, Path.Combine(dir, "gw-1-dwm.png"));
                var bandTop = new DRect(pill.X, pill.Y - 22, pill.Width, 16);
                var bandBottom = new DRect(pill.X, pill.Bottom + 6, pill.Width, 16);
                var outside = new DRect(region.Width - 70, region.Height - 40, 40, 24);
                Say($"  sharp pill={Sharpness(shot, pill):F1} bandTop={Sharpness(shot, bandTop):F1} " +
                    $"bandBottom={Sharpness(shot, bandBottom):F1} outside={Sharpness(shot, outside):F1} " +
                    $"pillLuma={MeanLuma(shot, pill):F1}");
            }

            // ③ 命中：胶囊中心应该还是内容窗口
            var cx = region.X + pill.X + pill.Width / 2;
            var cy = region.Y + pill.Y + pill.Height / 2;
            var hit = WindowAt((cx, cy));
            var hitRoot = GetAncestor(hit, 2 /*GA_ROOT*/);
            Say($"  WindowFromPoint(pill 中心 {cx},{cy}) = 0x{hit.ToInt64():X} root=0x{hitRoot.ToInt64():X} " +
                $"isContent={hitRoot == contentHwnd} isGlass={hitRoot == glassHwnd}");
            var by = region.Y + pill.Y - 12;      // 顶部带里的一点
            var hitBand = WindowAt((cx, by));
            Say($"  WindowFromPoint(顶部带 {cx},{by}) = 0x{hitBand.ToInt64():X} " +
                $"isContent={hitBand == contentHwnd} isGlass={hitBand == glassHwnd}");

            // 参照帧：两个窗口都收走（glass 是独立窗口，Hide 内容窗口不会带走它）
            win.DebugSetDockGlassVisible(false);
            win.Hide();
            await Task.Delay(800);
            using (var reference = Grab(region))
            {
                SavePng(reference, Path.Combine(dir, "gw-ref-desktop.png"));
                win.Show();
                await Task.Delay(1400);
                using var back = Grab(region);
                SavePng(back, Path.Combine(dir, "gw-2-back.png"));
                ReportDiff(back, reference, win, dock, region, "dwm-window", dir, Say);
            }

            // ④ 切回采样方案
            DesktopZones.Helpers.WallpaperSource.Enabled = true;
            win.RefreshZone(dock);
            await Task.Delay(1200);
            Say("采样方案下: " + win.DebugDockGlass());
            Say("采样方案下 layer: " + win.DebugDockGlassLayer());
            using (var shot2 = Grab(region)) SavePng(shot2, Path.Combine(dir, "gw-3-sample.png"));

            // ⑤ 真玻璃 vs 采样：动态壁纸下胶囊里的像素会不会跟着变（采样 = 冻结的一帧）
            using (var d1 = Grab(region))
            {
                await Task.Delay(1000);
                using var d2 = Grab(region);
                Say($"  采样方案 pill 逐帧变化={MeanAbsDiff(d1, d2, pill):F2} band={MeanAbsDiff(d1, d2, new DRect(pill.X, pill.Y - 22, pill.Width, 16)):F2}");
            }
            DesktopZones.Helpers.WallpaperSource.Enabled = false;
            win.RefreshZone(dock);
            await Task.Delay(1200);
            using (var e1 = Grab(region))
            {
                await Task.Delay(1000);
                using var e2 = Grab(region);
                Say($"  DWM 方案 pill 逐帧变化={MeanAbsDiff(e1, e2, pill):F2} band={MeanAbsDiff(e1, e2, new DRect(pill.X, pill.Y - 22, pill.Width, 16)):F2}");
            }

            // ⑥ 最小化 → 展开：玻璃窗要跟着藏/显、并且回到胶囊原位
            win.HideZone();
            await Task.Delay(2200);
            Say("  最小化后: " + win.DebugDockGlass());
            win.ShowZone();
            await Task.Delay(2200);
            Say("  展开后  : " + win.DebugDockGlass());
            using (var back2 = Grab(region)) SavePng(back2, Path.Combine(dir, "gw-4-expanded.png"));
        }
        finally
        {
            dock.TileMode = oldTile;
            dock.EnableLiquidGlass = oldGlass;
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
            var w2 = _zoneManager?.GetZoneWindow(dock.Id);
            if (w2 != null) w2.RefreshZone(dock);
        }
    }

    // ── 场景 6：DWM 玻璃「快捷栏 vs 分区」到底一不一样（同级比较） ──────────
    //
    // 用户反馈「快捷栏 DWM 默认玻璃的参数不对，调成和分区一样的」。这里把**分区**和
    // **快捷栏**都用 DWM 方案开玻璃摆到桌面上，各取一块"纯玻璃"的区域算平均色 ——
    // 两个数接近才算真的同一套观感。

    async Task LabGlassCompareAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glasscmp: 同一个位置、同一块背景 —— 分区 vs 快捷栏 的 DWM 玻璃 ==");
        Say("（DWM 是实景模糊：不在同一位置的绝对不能用亮度对比，那样量的只是背景不同）");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        ZoneWindow? zoneWin = null;
        try
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = false;   // DWM
            var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
            if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }
            bool oldTile = dock.TileMode, oldGlass = dock.EnableLiquidGlass;
            string oldFill = dock.FillColor, oldMode = dock.GlassColorMode;
            int oldBlur = dock.GlassBlurAmount, oldTint = dock.GlassTintOpacity;
            try
            {
                dock.TileMode = false;
                dock.EnableLiquidGlass = true;
                dock.GlassColorMode = "Default";
                dock.GlassTintOpacity = 50;
                dock.GlassBlurAmount = 18;
                dock.FillColor = "#08000000";
                _zoneManager.ShowZone(dock);
                var dwin = _zoneManager.GetZoneWindow(dock.Id);
                if (dwin == null) { Say("拿不到快捷栏窗口"); return; }
                dwin.RefreshZone(dock);
                MoveToFreeSpot(dwin, Say);
                await Task.Delay(1600);
                dwin.PreviewResetHover();
                await Task.Delay(250);

                // 探针 = 胶囊内侧靠底边的一条（没有图标、没有描边、没有倒影）
                var pill = DockPillScreenPx(dock, dwin);
                var patch = new DRect(pill.X + 40, pill.Bottom - 8, 140, 5);
                var region = new DRect(patch.X - 20, patch.Y - 20, patch.Width + 40, patch.Height + 40);
                Say($"pill={pill} patch={patch}");
                Say("dock: " + dwin.DebugDockGlass());

                double dockLuma, dockSharp;
                using (var shot = Grab(region))
                {
                    SavePng(shot, Path.Combine(dir, "cmp-dock.png"));
                    var local = new DRect(20, 20, patch.Width, patch.Height);
                    dockLuma = MeanLuma(shot, local);
                    dockSharp = Sharpness(shot, local);
                    Say($"  dock  patch luma={dockLuma:F1} sharp={dockSharp:F1} " +
                        $"rgb={MeanRgb(shot, local)}");
                }

                // 同一个屏幕矩形上摆一个**分区**（尺寸 = 胶囊），玻璃参数完全相同
                dwin.Hide();
                await Task.Delay(500);
                var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(dwin);
                var zone = new Zone
                {
                    Name = "lab glass zone",
                    DockMode = false,
                    GridSize = 48,
                    CornerRadius = 12,
                    BorderThickness = 1.0,
                    IsVisible = true,
                    EnableLiquidGlass = true,
                    FillColor = "#08000000",
                    BorderColor = "#66FFFFFF",
                    GlassBlurAmount = 18,
                    GlassTintOpacity = 50,
                    GlassColorMode = "Default",
                    Width = Math.Round(pill.Width / dpi.DpiScaleX),
                    Height = Math.Round(pill.Height / dpi.DpiScaleY),
                    X = Math.Round(pill.X / dpi.DpiScaleX),
                    Y = Math.Round(pill.Y / dpi.DpiScaleY),
                };
                zoneWin = new ZoneWindow(zone, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
                { ShowInTaskbar = false };
                zoneWin.Show();
                await Task.Delay(1600);
                zoneWin.UpdateLayout();
                using (var shot = Grab(region))
                {
                    SavePng(shot, Path.Combine(dir, "cmp-zone.png"));
                    var local = new DRect(20, 20, patch.Width, patch.Height);
                    double zl = MeanLuma(shot, local), zs = Sharpness(shot, local);
                    Say($"  zone  patch luma={zl:F1} sharp={zs:F1} rgb={MeanRgb(shot, local)}");
                    Say($"  Δluma={Math.Abs(zl - dockLuma):F1}（<=8 视为同一套观感）");
                }
                zoneWin.Close();
                zoneWin = null;
                dwin.Show();
                await Task.Delay(1200);
                using (var shot = Grab(region))
                {
                    SavePng(shot, Path.Combine(dir, "cmp-dock2.png"));
                    var local = new DRect(20, 20, patch.Width, patch.Height);
                    Say($"  dock2 patch luma={MeanLuma(shot, local):F1} rgb={MeanRgb(shot, local)}（回到原位复测）");
                }
            }
            finally
            {
                dock.TileMode = oldTile;
                dock.EnableLiquidGlass = oldGlass;
                dock.FillColor = oldFill;
                dock.GlassColorMode = oldMode;
                dock.GlassBlurAmount = oldBlur;
                dock.GlassTintOpacity = oldTint;
                var w2 = _zoneManager?.GetZoneWindow(dock.Id);
                if (w2 != null) w2.RefreshZone(dock);
            }
        }
        finally
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
            try { zoneWin?.Close(); } catch { }
        }
    }

    // ── 场景 6b：同一个位置，两种 DWM 玻璃通道 A/B ──────────────────────
    //
    // A = 窗口 == 胶囊（内容窗口自己吃 accent —— 和分区/时钟/便签**同一条代码路径**）
    // B = 双窗（胶囊大小的裸 HWND 吃 accent，第 7 轮 B）
    // 两条通道在同一位置、同一块背景、同一套参数下取同一块像素，数字接近才算"和分区一样"。

    async Task LabGlassAbAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glassab: A 窗口==胶囊（=分区那条路） vs B 双窗 ==");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        try
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = false;   // DWM
            var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
            if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }
            bool oldTile = dock.TileMode, oldGlass = dock.EnableLiquidGlass;
            bool oldAnim = dock.DockAnimate, oldRefl = dock.DockReflection;
            string oldFill = dock.FillColor, oldMode = dock.GlassColorMode;
            int oldBlur = dock.GlassBlurAmount, oldTint = dock.GlassTintOpacity;
            try
            {
                dock.TileMode = false;
                dock.EnableLiquidGlass = true;
                dock.GlassColorMode = "Default";
                dock.GlassTintOpacity = 50;
                dock.GlassBlurAmount = 18;
                dock.FillColor = "#08000000";
                _zoneManager.ShowZone(dock);
                var dwin = _zoneManager.GetZoneWindow(dock.Id);
                if (dwin == null) { Say("拿不到快捷栏窗口"); return; }

                async Task Shot(ZoneWindow w, Zone d, string tag)
                {
                    w.PreviewResetHover();
                    await Task.Delay(1200);
                    var pill = DockPillScreenPx(d, w);
                    var patch = new DRect(pill.X + 40, pill.Bottom - 8, 160, 5);
                    var region = new DRect(patch.X - 16, patch.Y - 16, patch.Width + 32, patch.Height + 32);
                    using var shot = Grab(region);
                    SavePng(shot, Path.Combine(dir, $"ab-{tag}.png"));
                    var local = new DRect(16, 16, patch.Width, patch.Height);
                    Say($"  [{tag}] pill={pill.Width}x{pill.Height} luma={MeanLuma(shot, local):F1} " +
                        $"sharp={Sharpness(shot, local):F1} rgb={MeanRgb(shot, local)} | " + w.DebugDockGlass());
                }

                // 两条通道都摆到**同一个胶囊屏幕矩形**上（DWM 是实景模糊，位置不同就没法比）
                const double targetLeft = 820, targetTop = 620;
                void PlaceDock(ZoneWindow w, Zone d)
                {
                    double side = DesktopZones.Helpers.ZoneLayout.DockSideOverflow(d);
                    double top = DesktopZones.Helpers.ZoneLayout.DockBarTop(d);
                    // 让**胶囊**左上角落在 (targetLeft, targetTop)：窗口原点要往左上让开侧向/上方余量
                    w.Left = targetLeft - side;
                    w.Top = targetTop - top;
                }

                // A：窗口 == 胶囊（动效/倒影都关）→ 走内容窗口自己的 accent
                dock.DockAnimate = false;
                dock.DockReflection = false;
                dwin.RefreshZone(dock);
                PlaceDock(dwin, dock);
                await Task.Delay(700);
                await Shot(dwin, dock, "A-inwindow");

                // B：双窗（胶囊屏幕矩形不变，只是打开溢出带）
                dock.DockAnimate = true;
                dock.DockReflection = true;
                dwin.RefreshZone(dock);
                PlaceDock(dwin, dock);
                await Task.Delay(700);
                await Shot(dwin, dock, "B-twowindow");
            }
            finally
            {
                dock.TileMode = oldTile;
                dock.EnableLiquidGlass = oldGlass;
                dock.DockAnimate = oldAnim;
                dock.DockReflection = oldRefl;
                dock.FillColor = oldFill;
                dock.GlassColorMode = oldMode;
                dock.GlassBlurAmount = oldBlur;
                dock.GlassTintOpacity = oldTint;
                var w2 = _zoneManager?.GetZoneWindow(dock.Id);
                if (w2 != null) w2.RefreshZone(dock);
            }
        }
        finally { DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme; }
    }

    // ── 场景 8：玻璃窗的形状到底被什么裁（glassshape）──────────────────────
    //
    // 用户报「圆角的裁切还是有点问题」。玻璃层是**非分层裸 HWND**，DWM 的 accent
    // （亚克力背板）到底认不认 `SetWindowRgn` / `DWMWA_WINDOW_CORNER_PREFERENCE`，
    // 直接做实验：正常 → 椭圆区域 → 圆角偏好 → 关 accent，各拍一张。

    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateEllipticRgn(int l, int t, int r, int b);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    async Task LabGlassShapeAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glassshape: 玻璃窗的形状被什么裁 ==");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        ZoneWindow? w = null;
        try
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = false;
            var z = MakeLabDock(false, "lab shape", Array.Empty<string>());
            z.EnableLiquidGlass = true;
            z.FillColor = "#08000000";
            z.GlassColorMode = "Default";
            z.GlassMaterial = "";
            z.GlassBlurAmount = 18;
            z.GlassTintOpacity = 50;
            z.CornerRadius = 12;
            z.BorderThickness = 0;          // 去掉 WPF 描边，免得它遮住玻璃自己的边
            z.DockAnimate = true;           // 留溢出带，逼它走独立玻璃窗
            z.DockMagnify = 1.8;
            z.DockReflection = true;
            DesktopZones.Helpers.ZoneLayout.ApplyDockSize(z);

            w = new ZoneWindow(z, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };
            w.Show();
            await Task.Delay(1400);
            foreach (Window ow in Application.Current.Windows)
                if (ow is ZoneWindow oz && !ReferenceEquals(oz, w) && oz.IsVisible)
                { oz.DebugSetDockGlassVisible(false); oz.Hide(); }
            w.Left = 1500;
            w.Top = 980;
            await Task.Delay(1300);
            w.UpdateLayout();

            var g = w.DebugDockGlassHwnd();
            if (g == IntPtr.Zero) { Say("没有玻璃窗 → 跳过"); return; }
            GetWindowRect(g, out var gr);
            int gw = gr.Right - gr.Left, gh = gr.Bottom - gr.Top;
            Say($"glass hwnd=0x{g.ToInt64():X} rect=({gr.Left},{gr.Top}) {gw}x{gh} state={w.DebugDockGlass()}");
            var region = CaptureRegion(w, 26);
            void Shot(string tag, string note)
            {
                using var bmp = Grab(region);
                SavePng(bmp, Path.Combine(dir, $"gs-{tag}.png"));
                Say($"  [{tag}] {note}");
            }
            Shot("a-normal", "现状（SetWindowRgn 圆角 + accent）");

            // 1) 直接把玻璃窗裁成椭圆：accent 若认窗口区域，胶囊就该变椭圆
            SetWindowRgn(g, CreateEllipticRgn(0, 0, gw, gh), true);
            await Task.Delay(600);
            Shot("b-ellipse", "SetWindowRgn(椭圆) —— accent 认区域的话这里会变椭圆");

            // 2) Win11 圆角偏好（半径固定 ~8px）
            int round = 2;   // DWMWCP_ROUND
            int hr = DwmSetWindowAttribute(g, 33, ref round, sizeof(int));
            await Task.Delay(600);
            Shot("c-corner", $"DWMWA_WINDOW_CORNER_PREFERENCE=ROUND hr={hr}");

            // 3) 关掉 accent：裸窗口还剩什么
            DesktopZones.Helpers.AcrylicHelper.DisableBlur(g);
            await Task.Delay(500);
            Shot("d-accentoff", "DisableBlur(玻璃窗) —— 没有 accent 的裸窗口");
        }
        catch (Exception ex) { Say("glassshape EXC " + ex); }
        finally
        {
            try { w?.Close(); } catch { }
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
        }
    }

    // ── 场景 9：玻璃层和内容层「契合」到什么程度（glassfit）──────────────────
    //
    // 用户报「玻璃层和内容层没有完美契合，WPF 层明显要大一圈」。这句话里其实有
    // **两个**可以分开量的量：
    //   A. 玻璃窗的形状由谁决定 —— 把一只裸玻璃窗放到**纯色**背景上，逐个试
    //      SetWindowRgn（圆角区域 / 半窗区域）与 DWMWA_WINDOW_CORNER_PREFERENCE，
    //      看玻璃边界到底听谁的。纯色背景 = 差分干净，不受动态壁纸干扰。
    //   B. 真实快捷栏的玻璃边界 vs 胶囊矩形差几像素、四角半径是多少 —— 这就是
    //      「大一圈」的量化定义：每一侧的内缩（应为 0）+ 四角半径（应与描边一致）。
    //
    // 边界一律用「玻璃开 / 玻璃关」两张图求差得到，而不是看绝对颜色。

    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    async Task LabGlassFitAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glassfit: 玻璃层和内容层差几像素 ==");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        Window? bgw = null;
        ZoneWindow? dwin = null;
        DockGlassWindow? glass = null;
        try
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = false;

            // ── A. 纯色背景上的裸玻璃窗：形状听谁的 ──
            var bgRect = new DRect(1180, 200, 460, 300);
            bgw = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                AllowsTransparency = false,
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A)),
                Left = bgRect.X, Top = bgRect.Y, Width = bgRect.Width, Height = bgRect.Height,
            };
            // WPF 的 Left/Top/Width/Height 是 **DIP**：在 125% 缩放下直接写物理像素会让窗口
            // 整体偏到别处（第一版就是这么错的：玻璃一大半落在壁纸上，右边缘的"没变"其实是
            // 灰色背景窗的边）。先 Show 拿到窗口，再按 DIP 摆。
            bgw.Show();
            var bgDpi = System.Windows.Media.VisualTreeHelper.GetDpi(bgw);
            bgw.Left = bgRect.X / bgDpi.DpiScaleX;
            bgw.Top = bgRect.Y / bgDpi.DpiScaleY;
            bgw.Width = bgRect.Width / bgDpi.DpiScaleX;
            bgw.Height = bgRect.Height / bgDpi.DpiScaleY;
            await Task.Delay(600);

            var gr = new DRect(bgRect.X + 60, bgRect.Y + 80, 260, 72);   // 玻璃窗矩形（物理像素）
            glass = new DockGlassWindow();
            glass.ShowNoActivate();
            if (glass.Handle == IntPtr.Zero)
            {
                Say("A: 玻璃窗创建失败 → 跳过 A");
            }
            else
            {
                IntPtr topmost = new IntPtr(-1);
                void Place() => SetWindowPosEx(glass!.Handle, topmost,
                    gr.X, gr.Y, gr.Width, gr.Height, SWP_NOACTIVATE_ | SWP_SHOWWINDOW_);
                Place();
                var ar = glass.ApplyGlass(18, "#CC101418", 1.0, "Default", 100, 0,
                    DesktopZones.Helpers.AcrylicHelper.ResolveAccentState(""), useClassicBlur: false);
                Say($"A: ApplyGlass {(ar.Success ? "ok" : ar.Error)} rect={gr} " +
                    $"dpi={System.Windows.Media.VisualTreeHelper.GetDpi(bgw).DpiScaleX:F2}");
                await Task.Delay(500);

                var cap = new DRect(gr.X - 10, gr.Y - 10, gr.Width + 20, gr.Height + 20);
                glass.HideGlass();
                await Task.Delay(350);
                using var bgShot = Grab(cap);
                SavePng(bgShot, Path.Combine(dir, "gf-a-ref.png"));
                glass.ShowNoActivate();
                Place();
                await Task.Delay(500);

                async Task Variant(string tag, bool round, IntPtr region, string note)
                {
                    glass!.SetCornerRounding(round);
                    int hr = SetWindowRgn(glass.Handle, region, true);
                    Place();                       // 改区域后重摆一次，保证 z 序仍在最上
                    await Task.Delay(600);
                    using var shot = Grab(cap);
                    SavePng(shot, Path.Combine(dir, $"gf-a-{tag}.png"));
                    Say($"  [{tag}] {note} round={round} regionSet={hr}");
                    ReportGlassMask(shot, bgShot, cap, gr, Say, tag, dir);
                    SaveZoom(shot, new DRect(10, 10, 26, 26), 8, Path.Combine(dir, $"gf-a-{tag}-tl8x.png"));
                }

                await Variant("rect-noround", false, IntPtr.Zero, "无区域 + DONOTROUND");
                await Variant("round", true, IntPtr.Zero, "无区域 + ROUND");
                await Variant("rect+rgn16", false, CreateRoundRectRgn(0, 0, gr.Width + 1, gr.Height + 1, 32, 32),
                    "圆角区域 r=16 + DONOTROUND");
                await Variant("rect+rgn-half", false, CreateRoundRectRgn(0, 0, gr.Width / 2, gr.Height, 0, 0),
                    "半窗矩形区域 + DONOTROUND（accent 认区域就该只剩半边）");
                await Variant("round+rgn16", true, CreateRoundRectRgn(0, 0, gr.Width + 1, gr.Height + 1, 32, 32),
                    "圆角区域 r=16 + ROUND");

                // 7) ROUND + **饱和着色**：只为把「玻璃」和「投影」在像素上分开 —— 玻璃的差
                //    会被拉到 150 以上，投影只有 30 上下，这样四个角（尤其下面两个）的圆角
                //    半径才量得准：下边那一圈投影正是上一轮把 BL/BR 量成 0 的原因。
                glass!.ApplyGlass(18, "#FFFF0000", 1.0, "#FFFF0000", 100, 100,
                    DesktopZones.Helpers.AcrylicHelper.ResolveAccentState(""), useClassicBlur: false);
                await Variant("round-red", true, IntPtr.Zero, "ROUND + 强红着色（分离投影）");
            }

            try { glass.Dispose(); } catch { }
            glass = null;
            bgw.Close();
            bgw = null;
            await Task.Delay(400);

            // 参数按用户那条「快捷栏 1」逐项抄：0 个图标（空胶囊 181×60 DIP，只有一个开始按钮）、
            // magnify 1.4、倒影关、corner=12、border=1、玻璃开。这样：
            //   · 四个角上只有"描边 + 玻璃"两样东西，差半个像素都能看出来；
            //   · 上边距 11 DIP × 1.25 = 13.75px、胶囊宽 181 × 1.25 = 226.25px —— 都是**带小数**
            //     的，正是"玻璃层和描边差一个亚像素"的最坏情况。
            var dock = MakeLabDock(false, "lab fit", Array.Empty<string>());
            dock.EnableLiquidGlass = true;
            dock.TileMode = false;
            dock.FillColor = "#CC101418";
            dock.GlassColorMode = "Default";
            dock.GlassMaterial = "";
            dock.GlassBlurAmount = 18;
            dock.GlassTintOpacity = 100;      // 只为把玻璃和背景拉开：形状跟不透明度无关
            dock.GlassTintLuminosity = 100;
            dock.CornerRadius = 12;
            dock.BorderThickness = 1.0;
            dock.BorderColor = "#40FFFFFF";
            dock.DockAnimate = true;
            dock.DockMagnify = 1.4;
            dock.DockReflection = false;
            dock.DockStartButton = true;
            DesktopZones.Helpers.ZoneLayout.ApplyDockSize(dock);

            dwin = new ZoneWindow(dock, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };
            foreach (Window ow in Application.Current.Windows)
                if (ow is ZoneWindow oz && !ReferenceEquals(oz, dwin) && oz.IsVisible)
                { oz.DebugSetDockGlassVisible(false); oz.Hide(); }
            dwin.Show();
            await Task.Delay(900);
            double side = DesktopZones.Helpers.ZoneLayout.DockSideOverflow(dock);
            double top = DesktopZones.Helpers.ZoneLayout.DockBarTop(dock);
            var contentHwnd = new System.Windows.Interop.WindowInteropHelper(dwin).Handle;
            foreach (var (cx, cy) in new (double, double)[] { (1500, 980), (700, 1050), (300, 980), (1500, 260) })
            {
                dwin.Left = cx - side;
                dwin.Top = cy - top;
                await Task.Delay(320);
                var probe = DockPillScreenPx(dock, dwin);
                if (WindowAt((probe.X + probe.Width / 2, probe.Y + probe.Height / 2)) == contentHwnd) break;
            }
            await Task.Delay(1300);
            dwin.UpdateLayout();

            var pill = DockPillScreenPx(dock, dwin);
            var pad = new DRect(pill.X - 10, pill.Y - 10, pill.Width + 20, pill.Height + 20);
            Say($"B: dock='{dock.Name}' pill={pill} corner={dock.CornerRadius} border={dock.BorderThickness} " +
                $"mode={dwin.DebugDockGlass()}");
            Say("B geom: " + dwin.DebugDockPillGeom());

            dwin.DebugSetDockGlassVisible(false);
            await Task.Delay(700);
            using var refShot = Grab(pad);
            SavePng(refShot, Path.Combine(dir, "gf-b-ref.png"));
            dwin.DebugSetDockGlassVisible(true);
            await Task.Delay(900);
            using (var shot = Grab(pad))
            {
                SavePng(shot, Path.Combine(dir, "gf-b-shot.png"));
                ReportGlassMask(shot, refShot, pad, pill, Say, "dock", dir);
                // 四角各拍一张 14 倍特写：**左下角**是用户报的那一处，四个角放一起才比得出来
                const int zs = 14, box = 30;
                SaveZoom(shot, new DRect(0, 0, box, box), zs, Path.Combine(dir, "corner-tl.png"));
                SaveZoom(shot, new DRect(pad.Width - box, 0, box, box), zs, Path.Combine(dir, "corner-tr.png"));
                SaveZoom(shot, new DRect(0, pad.Height - box, box, box), zs, Path.Combine(dir, "corner-bl.png"));
                SaveZoom(shot, new DRect(pad.Width - box, pad.Height - box, box, box), zs,
                    Path.Combine(dir, "corner-br.png"));
            }
            // 再拍一张"玻璃关"：动态壁纸两张参照帧求交集，把"壁纸自己动了"的像素剔掉
            dwin.DebugSetDockGlassVisible(false);
            await Task.Delay(700);
            using (var ref2 = Grab(pad)) SavePng(ref2, Path.Combine(dir, "gf-b-ref2.png"));
            Say("B: " + dwin.DebugDockGlass());
        }
        catch (Exception ex) { Say("glassfit EXC " + ex); }
        finally
        {
            try { glass?.Dispose(); } catch { }
            try { bgw?.Close(); } catch { }
            try { dwin?.Close(); } catch { }
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
        }
    }

    /// <summary>
    /// 玻璃相对同一块背景的**可见范围**：每一侧相对 <paramref name="target"/> 的内缩
    /// （0 = 齐边）与四角沿对角线的圆角半径（r = t / 0.2929）。边界一律取"变了"的像素，
    /// 不依赖颜色绝对值。参数都是屏幕坐标，<paramref name="area"/> 是抓屏区域。
    /// </summary>
    static void ReportGlassMask(DBitmap shot, DBitmap bg, DRect area, DRect target,
                                Action<string> Say, string tag, string dir)
    {
        int W = Math.Min(shot.Width, bg.Width), H = Math.Min(shot.Height, bg.Height);
        bool In(int x, int y) => x >= 0 && y >= 0 && x < W && y < H;
        bool Changed(int x, int y)
        {
            if (!In(x, y)) return false;
            var a = shot.GetPixel(x, y); var b = bg.GetPixel(x, y);
            return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 12;
        }

        int tx = target.X - area.X, ty = target.Y - area.Y, tw = target.Width, th = target.Height;

        static int Med(List<int> list)
        {
            var c = list.Where(v => v != int.MinValue).ToList();
            if (c.Count == 0) return int.MinValue;
            c.Sort();
            return c[c.Count / 2];
        }

        var leftVals = new List<int>(); var rightVals = new List<int>();
        var topVals = new List<int>(); var botVals = new List<int>();
        for (int i = th / 3; i < th * 2 / 3; i += 2)
        {
            int rowY = ty + i;
            for (int x = tx - 6; x < tx + tw; x++) if (Changed(x, rowY)) { leftVals.Add(x - tx); break; }
            for (int x = tx + tw + 5; x > tx; x--) if (Changed(x, rowY)) { rightVals.Add(x - (tx + tw - 1)); break; }
        }
        for (int i = tw / 3; i < tw * 2 / 3; i += 2)
        {
            int colX = tx + i;
            for (int y = ty - 6; y < ty + th; y++) if (Changed(colX, y)) { topVals.Add(y - ty); break; }
            for (int y = ty + th + 5; y > ty; y--) if (Changed(colX, y)) { botVals.Add(y - (ty + th - 1)); break; }
        }

        double Corner(int sx, int sy, int dx, int dy, int limit)
        {
            for (int t = 0; t <= limit; t++)
                if (Changed(sx + dx * t, sy + dy * t)) return t / 0.2929;
            return -1;
        }
        int lim = Math.Min(tw, th) / 2;
        double rTL = Corner(tx, ty, 1, 1, lim);
        double rTR = Corner(tx + tw - 1, ty, -1, 1, lim);
        double rBL = Corner(tx, ty + th - 1, 1, -1, lim);
        double rBR = Corner(tx + tw - 1, ty + th - 1, -1, -1, lim);

        Say($"  [{tag}] edgeInset L={Med(leftVals)} T={Med(topVals)} R={Med(rightVals)} B={Med(botVals)} " +
            $"(0 = 与目标矩形齐边) | cornerR TL={rTL:F1} TR={rTR:F1} BL={rBL:F1} BR={rBR:F1}");

        using (var mask = new DBitmap(W, H, DPixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    mask.SetPixel(x, y, Changed(x, y)
                        ? DColor.FromArgb(255, 255, 255, 255) : DColor.FromArgb(255, 0, 0, 0));
            foreach (var yy in new[] { ty, ty + th - 1 })            // 目标矩形画成红框，方便对着看
                for (int x = tx; x < tx + tw; x++) if (In(x, yy)) mask.SetPixel(x, yy, DColor.FromArgb(255, 255, 0, 0));
            foreach (var xx in new[] { tx, tx + tw - 1 })
                for (int y = ty; y < ty + th; y++) if (In(xx, y)) mask.SetPixel(xx, y, DColor.FromArgb(255, 255, 0, 0));
            SavePng(mask, Path.Combine(dir, $"gf-mask-{tag}.png"));
        }
    }

    /// <summary>把一小块放大存成 PNG（看圆角/接缝用，最近邻放大，不模糊）。</summary>
    static void SaveZoom(DBitmap src, DRect r, int scale, string path)
    {
        try
        {
            int w = Math.Max(1, Math.Min(r.Width, src.Width - r.X)) * scale;
            int h = Math.Max(1, Math.Min(r.Height, src.Height - r.Y)) * scale;
            using var dst = new DBitmap(w, h, DPixelFormat.Format32bppArgb);
            using var g = DGraphics.FromImage(dst);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(src, new DRect(0, 0, w, h), r, DGraphicsUnit.Pixel);
            SavePng(dst, path);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("zoom " + ex.Message); }
    }

    // ── 场景 10：四个小组件（时钟/日历/播放器/便签）的边框对齐（widgetfit）──────
    //
    // 用户报「四个小组件的边框也有类似的问题」。它们和快捷栏是同一套结构：
    //   · 窗口 == 可见内容（不像快捷栏那样多出两条带），圆角靠 DWM 的
    //     `DWMWA_WINDOW_CORNER_PREFERENCE = ROUND` 切 —— 半径是**固定 8 DIP**；
    //   · 而 WPF 自己画的边框 / 填充用的是模型里的 CornerRadius（10 / 12）。
    // 只要 10/12 > 8，四个角上「DWM 切出来的玻璃」就从「WPF 画的描边弧」**外面**
    // 探出一小条 —— 与快捷栏第 10 轮同一个几何错误。
    //
    // 这里把每个小组件摆到空位、玻璃开的情况下拍「有/无」两张图求差，再存四角 14 倍特写。
    async Task LabWidgetFitAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== widgetfit: 小组件边框 vs DWM 圆角 ==");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        DesktopZones.Helpers.WallpaperSource.Enabled = false;   // 渲染方案 = DWM
        var hidden = new List<Window>();
        try
        {
            // 自测台在 `App.OnStartup` 里提前 return 了，`RestoreVisibleWidgets()` 没跑过 ——
            // 这里按配置自己建四个（每类一个），跑完就关掉。
            var created = new List<Window>();
            var cfg = _configService!.Load();
            if (cfg.Clocks.Count > 0) created.Add(new DesktopZones.Views.ClockWidget(cfg.Clocks[0], _widgetService!));
            if (cfg.Calendars.Count > 0) created.Add(new DesktopZones.Views.CalendarWidget(cfg.Calendars[0], _widgetService!));
            if (cfg.MediaPlayers.Count > 0) created.Add(new DesktopZones.Views.MediaPlayerWidget(cfg.MediaPlayers[0], _widgetService!));
            if (cfg.Notes.Count > 0) created.Add(new DesktopZones.Views.StickyNoteWindow(cfg.Notes[0], _notesService!));
            foreach (var w in created) { try { w.Show(); } catch { } }
            await Task.Delay(1800);
            Say($"按配置建了 {created.Count} 个小组件窗口");

            var widgets = created;
            Say($"找到 {widgets.Count} 个小组件窗口（含隐藏的）");
            if (widgets.Count == 0) { Say("没有小组件 → 跳过"); return; }

            var spots = new (double X, double Y)[] { (240, 200), (1000, 200), (240, 760), (1000, 760), (1560, 200) };
            int si = 0;
            foreach (var w in widgets)
            {
                string cls = w.GetType().Name;
                if (!w.IsVisible) { try { w.Show(); } catch { } await Task.Delay(500); }
                var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                // 挪到一个"没被别人盖住"的位置（桌面层的分区/快捷栏永远在应用窗口下面，
                // 被盖住就拍不到自己）
                bool visible = false;
                for (int k = 0; k < spots.Length; k++)
                {
                    var (sx, sy) = spots[(si + k) % spots.Length];
                    w.Left = sx; w.Top = sy;
                    await Task.Delay(260);
                    var c = w.PointToScreen(new Point(w.ActualWidth / 2, w.ActualHeight / 2));
                    var at = WindowAt(((int)c.X, (int)c.Y));
                    if (at == hwnd) { visible = true; si = (si + k + 1) % spots.Length; break; }
                }
                await Task.Delay(500);
                w.UpdateLayout();

                var tl = w.PointToScreen(new Point(0, 0));
                int x0 = (int)Math.Round(tl.X), y0 = (int)Math.Round(tl.Y);
                int cw = (int)Math.Round(w.ActualWidth * System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX);
                int ch = (int)Math.Round(w.ActualHeight * System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleY);
                Say($"{cls}: visible={visible} win=({x0},{y0}) {cw}x{ch} dip={w.ActualWidth:F1}x{w.ActualHeight:F1} " +
                    $"dpi={System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX:F2} | {DwmState(w)}");
                if (!visible) { hidden.Add(w); continue; }

                var pad = new DRect(x0 - 10, y0 - 10, cw + 20, ch + 20);
                using var shot = Grab(pad);
                SavePng(shot, Path.Combine(dir, $"w-{cls}-shot.png"));
                // 不藏窗口取参照帧：小组件的 Hide/Show 会触发它们自己的收起/展开状态机
                // （留恢复按钮、关 DWM 圆角），拿来做参照会把状态搞乱。四角特写足够判断。
                const int zs = 14, box = 30;
                SaveZoom(shot, new DRect(10, 10, box, box), zs, Path.Combine(dir, $"w-{cls}-tl.png"));
                SaveZoom(shot, new DRect(pad.Width - 10 - box, 10, box, box), zs, Path.Combine(dir, $"w-{cls}-tr.png"));
                SaveZoom(shot, new DRect(10, pad.Height - 10 - box, box, box), zs,
                    Path.Combine(dir, $"w-{cls}-bl.png"));
                SaveZoom(shot, new DRect(pad.Width - 10 - box, pad.Height - 10 - box, box, box), zs,
                    Path.Combine(dir, $"w-{cls}-br.png"));
            }
        }
        catch (Exception ex) { Say("widgetfit EXC " + ex); }
        finally
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
            try
            {
                foreach (Window w in Application.Current.Windows)
                    if (w is DesktopZones.Views.ClockWidget or DesktopZones.Views.CalendarWidget
                        or DesktopZones.Views.MediaPlayerWidget or DesktopZones.Views.StickyNoteWindow)
                    {
                        try { w.Hide(); } catch { }
                        try { w.Close(); } catch { }
                    }
            }
            catch { }
        }
    }

    // ── 场景 7：快捷栏玻璃 vs 分区玻璃（同一位置、同一背景，参考帧做差） ──────
    //
    // 用户报「快捷栏的玻璃默认比分区深不少」。这里把两者摆到**同一个胶囊屏幕矩形**上，
    // 每次都先拍一张"没有玻璃的同一块背景"再拍玻璃，用差值（而不是绝对值）比较 ——
    // 这台机的壁纸是动态的，绝对值没有可比性（见 20.2 的假阳性基线）。

    async Task LabGlassDiffAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== glassdiff: 快捷栏 DWM 玻璃 vs 分区 DWM 玻璃 ==");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        ZoneWindow? dwin = null;
        ZoneWindow? zw = null;
        try
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = false;   // 渲染方案 = DWM
            // 用一条**空**快捷栏：胶囊里没有图标/倒影，整块都是纯玻璃，才好和空分区比。
            var dock = MakeLabDock(false, "lab glassdiff", Array.Empty<string>());
            dock.EnableLiquidGlass = true;
            dock.TileMode = false;
            dock.FillColor = "#08000000";
            dock.GlassColorMode = "Default";
            dock.GlassMaterial = "";
            dock.GlassBlurAmount = 18;
            dock.GlassTintOpacity = 50;
            dock.GlassTintLuminosity = 100;
            dock.CornerRadius = 12;
            dock.BorderThickness = 1;
            dock.BorderColor = "#66FFFFFF";
            dock.DockAnimate = true;
            dock.DockMagnify = 1.8;
            dock.DockReflection = true;
            DesktopZones.Helpers.ZoneLayout.ApplyDockSize(dock);

            dwin = new ZoneWindow(dock, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };

            // 其它窗口全收（玻璃层是独立 HWND，也要一起收）
            foreach (Window w in Application.Current.Windows)
                if (w is ZoneWindow z2 && !ReferenceEquals(z2, dwin) && z2.IsVisible)
                { z2.DebugSetDockGlassVisible(false); z2.Hide(); }

            // 固定胶囊左上角（DIP）。窗口位置 = 胶囊 - 侧/上余量。
            // 位置要挑一个"没有被别的窗口盖住"的：桌面层窗口永远在应用窗口下面，被盖住就拍不到。
            dwin.Show();
            await Task.Delay(900);
            var candidates = new (double X, double Y)[] { (1500, 980), (700, 1050), (300, 980), (1500, 200) };
            var contentHwnd = new System.Windows.Interop.WindowInteropHelper(dwin).Handle;
            double side = DesktopZones.Helpers.ZoneLayout.DockSideOverflow(dock);
            double top = DesktopZones.Helpers.ZoneLayout.DockBarTop(dock);
            foreach (var (cx, cy) in candidates)
            {
                dwin.Left = cx - side;
                dwin.Top = cy - top;
                await Task.Delay(300);
                var probe = DockPillScreenPx(dock, dwin);
                var hit = WindowAt((probe.X + probe.Width / 2, probe.Y + probe.Height / 2));
                Say($"  候选 pill@({cx},{cy}) hit=0x{hit.ToInt64():X} content=0x{contentHwnd.ToInt64():X} ok={hit == contentHwnd}");
                if (hit == contentHwnd) break;
            }
            await Task.Delay(1200);
            dwin.UpdateLayout();

            var pill = DockPillScreenPx(dock, dwin);
            // 空胶囊：取**底部中间**一条（快捷栏这里没有图标/倒影，分区这里在标题栏之下），
            // 两边都不含标题栏/描边，才是"同一块纯玻璃"。
            var patch = new DRect(pill.X + 15, pill.Bottom - 24,
                Math.Max(20, pill.Width - 30), Math.Max(8, 14));
            var region = new DRect(patch.X - 40, patch.Y - 40, patch.Width + 80, patch.Height + 80);
            var local = new DRect(40, 40, patch.Width, patch.Height);
            var corner = new DRect(pill.X - 24, pill.Y - 24, 80, 80);   // 左上角特写
            Say($"pill={pill} patch={patch}");
            Say($"appearance: mode={dock.GlassColorMode} fill={dock.FillColor} blur={dock.GlassBlurAmount} " +
                $"tint={dock.GlassTintOpacity}/{dock.GlassTintLuminosity} mat='{dock.GlassMaterial}' " +
                $"corner={dock.CornerRadius} border={dock.BorderThickness}");
            Say("dock state: " + dwin.DebugDockGlass());

            // A：快捷栏 —— 先拍"有玻璃"，再把**玻璃层**收掉拍同一块背景（内容层不参与取色）
            using (var shotA = Grab(region))
            {
                SavePng(shotA, Path.Combine(dir, "gd-dock.png"));
                using (var c = Grab(corner)) SavePng(c, Path.Combine(dir, "gd-corner-dock.png"));

                // A2：只把经典 blurbehind 关掉（accent 不动），看"深一截"是不是它干的。
                DesktopZones.Helpers.AcrylicHelper.DebugSetClassicBlur(dwin.DebugDockGlassHwnd(), false);
                await Task.Delay(500);
                using var shotA2 = Grab(region);
                SavePng(shotA2, Path.Combine(dir, "gd-dock-noclassic.png"));
                using (var c2 = Grab(corner)) SavePng(c2, Path.Combine(dir, "gd-corner-dock-noclassic.png"));

                dwin.DebugSetDockGlassVisible(false);
                await Task.Delay(600);
                using var refA = Grab(region);
                SavePng(refA, Path.Combine(dir, "gd-ref-dock.png"));
                Say($"  [dock      ] luma={MeanLuma(shotA, local):F1} {DeltaRgb(shotA, refA, local)} " +
                    $"shotRGB={MeanRgb(shotA, local)} bgRGB={MeanRgb(refA, local)}");
                Say($"  [dock-nocl ] luma={MeanLuma(shotA2, local):F1} {DeltaRgb(shotA2, refA, local)} " +
                    $"shotRGB={MeanRgb(shotA2, local)}");
            }
            dwin.Hide();
            await Task.Delay(600);
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(dwin);
            var zone = new Zone
            {
                Name = "lab glass zone",
                DockMode = false,
                GridSize = 48,
                IsVisible = true,
                EnableLiquidGlass = true,
                TileMode = false,
                CornerRadius = dock.CornerRadius,
                BorderThickness = dock.BorderThickness,
                BorderColor = dock.BorderColor,
                FillColor = dock.FillColor,
                GlassMaterial = dock.GlassMaterial,
                GlassColorMode = dock.GlassColorMode,
                GlassBlurAmount = dock.GlassBlurAmount,
                GlassTintOpacity = dock.GlassTintOpacity,
                GlassTintLuminosity = dock.GlassTintLuminosity,
                Width = Math.Round(pill.Width / dpi.DpiScaleX),
                Height = Math.Round(pill.Height / dpi.DpiScaleY),
                X = Math.Round(pill.X / dpi.DpiScaleX),
                Y = Math.Round(pill.Y / dpi.DpiScaleY),
            };

            // B：分区 —— 同一个屏幕矩形、同一套外观字段
            zw = new ZoneWindow(zone, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };
            zw.Show();
            await Task.Delay(1600);
            zw.UpdateLayout();
            using (var shotB = Grab(region))
            {
                SavePng(shotB, Path.Combine(dir, "gd-zone.png"));
                using (var c = Grab(corner)) SavePng(c, Path.Combine(dir, "gd-corner-zone.png"));
                zw.Hide();
                await Task.Delay(600);
                using var refB = Grab(region);
                SavePng(refB, Path.Combine(dir, "gd-ref-zone.png"));
                Say($"  [zone] luma={MeanLuma(shotB, local):F1} {DeltaRgb(shotB, refB, local)} " +
                    $"shotRGB={MeanRgb(shotB, local)} bgRGB={MeanRgb(refB, local)}");
                Say("  zone dwm: " + DwmState(zw));
            }
        }
        catch (Exception ex) { Say("glassdiff EXC " + ex); }
        finally
        {
            try { zw?.Close(); } catch { }
            try { dwin?.Close(); } catch { }
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
        }
    }

    /// <summary>胶囊的屏幕物理矩形（不加抓屏 pad）。</summary>
    static DRect DockPillScreenPx(Zone dock, ZoneWindow win)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
        var tl = win.PointToScreen(new System.Windows.Point(0, 0));
        double side = DesktopZones.Helpers.ZoneLayout.DockSideOverflow(dock);
        double top = DesktopZones.Helpers.ZoneLayout.DockBarTop(dock);
        double w = DesktopZones.Helpers.ZoneLayout.DockPillWidth(dock, dock.Items.Count);
        double h = DesktopZones.Helpers.ZoneLayout.DockBarHeight(dock);
        return new DRect(
            (int)Math.Round(tl.X + side * dpi.DpiScaleX),
            (int)Math.Round(tl.Y + top * dpi.DpiScaleY),
            Math.Max(4, (int)Math.Round(w * dpi.DpiScaleX)),
            Math.Max(4, (int)Math.Round(h * dpi.DpiScaleY)));
    }

    /// <summary>区域平均 RGB（快速判断"颜色一不一样"）。</summary>
    static string MeanRgb(DBitmap bmp, DRect r)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(bmp.Width, r.Right), y1 = Math.Min(bmp.Height, r.Bottom);
        double sr = 0, sg = 0, sb = 0; int n = 0;
        for (int y = y0; y < y1; y += 2)
            for (int x = x0; x < x1; x += 2)
            {
                var p = bmp.GetPixel(x, y);
                sr += p.R; sg += p.G; sb += p.B; n++;
            }
        if (n == 0) return "-";
        return $"({sr / n:F0},{sg / n:F0},{sb / n:F0})";
    }

    /// <summary>玻璃相对同一块背景的 **差值**：Δrgb 每通道均值 + Δluma。
    /// 这台机的壁纸是动态的，只有差值能横向比较"谁更深"。</summary>
    static string DeltaRgb(DBitmap shot, DBitmap bg, DRect r)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(Math.Min(shot.Width, bg.Width), r.Right);
        int y1 = Math.Min(Math.Min(shot.Height, bg.Height), r.Bottom);
        double dr = 0, dg = 0, db = 0, dl = 0; int n = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x += 2)
            {
                var a = shot.GetPixel(x, y); var b = bg.GetPixel(x, y);
                dr += a.R - b.R; dg += a.G - b.G; db += a.B - b.B;
                dl += ((a.R + a.G + a.B) - (b.R + b.G + b.B)) / 3.0;
                n++;
            }
        if (n == 0) return "Δ=-";
        return $"Δrgb=({dr / n:+0.0;-0.0;0.0},{dg / n:+0.0;-0.0;0.0},{db / n:+0.0;-0.0;0.0}) Δluma={dl / n:+0.0;-0.0;0.0}";
    }

    // ── 场景 1b：最小化 → 点 36×36 恢复按钮展开（用户的真实路径） ─────────

    async Task LabRingClickAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== ring2: 最小化 → 点恢复按钮展开 ==");
        var dock = _zoneManager!.Zones.FirstOrDefault(z => z.DockMode);
        if (dock == null) { Say("配置里没有快捷栏 → 跳过"); return; }
        _zoneManager.ShowZone(dock);
        var win = _zoneManager.GetZoneWindow(dock.Id);
        if (win == null) { Say("拿不到窗口 → 跳过"); return; }
        Say($"dock='{dock.Name}' {dock.Width}x{dock.Height} tile={dock.TileMode} glass={dock.EnableLiquidGlass} " +
            $"anim={dock.DockAnimate}/{dock.DockMagnify} refl={dock.DockReflection} origin={dock.HoverExpandOrigin}");
        MoveToFreeSpot(win, Say);
        await Task.Delay(1200);
        win.UpdateLayout();
        Say($"at ({win.Left},{win.Top}) {win.ActualWidth}x{win.ActualHeight} dwm: {DwmState(win)}");
        win.PreviewResetHover();
        await Task.Delay(600);

        var region = CaptureRegion(win, 44);
        Say($"region={region}");

        win.Hide();
        await Task.Delay(500);
        using var reference = Grab(region);
        SavePng(reference, Path.Combine(dir, "ring2-ref.png"));
        win.Show();
        await Task.Delay(1300);
        using (var fresh = Grab(region))
        {
            SavePng(fresh, Path.Combine(dir, "ring2-1-fresh.png"));
            ReportDiff(fresh, reference, win, dock, region, "r2-fresh", dir, Say);
        }

        win.HideZone();
        await Task.Delay(2200);
        Say("dwm minimized: " + DwmState(win) + " | " + win.DebugContentState());
        using (var min = Grab(region))
        {
            SavePng(min, Path.Combine(dir, "ring2-2-minimized.png"));
            ReportDiff(min, reference, win, dock, region, "r2-minimized", dir, Say);
        }

        var (bx, by) = win.RestoreButtonCenterDevice();
        Say($"restore button center device=({bx},{by})");
        Say("probe: " + win.DebugProbeAt(bx, by));
        bool clicked = SafeClick(win, (bx, by), Say);
        await Task.Delay(1500);
        Say($"real click delivered? clicked={clicked} expanded={win.DebugHoverExpanded()}");
        if (!win.DebugHoverExpanded())
        {
            Say("真实输入在本沙箱里没派发到窗口 → 用 PreviewRestoreClick() 复现同一状态");
            win.PreviewRestoreClick();
            clicked = true;
        }
        await Task.Delay(900);
        // 把真实光标挪开（顺便验证"离开后放大/提示要收干净"），再清一次残留命中缓存
        SetCursorPos(60, 60);
        await Task.Delay(900);
        win.PreviewResetHover();
        await Task.Delay(900);
        Say($"clicked={clicked} dwm after click: {DwmState(win)} | {win.DebugContentState()}");
        Say("probe after: " + win.DebugProbeAt(bx, by));
        using (var exp = Grab(region))
        {
            SavePng(exp, Path.Combine(dir, "ring2-3-after-click.png"));
            ReportDiff(exp, reference, win, dock, region, "r2-after-click", dir, Say);
        }
        await Task.Delay(2000);
        using (var settle = Grab(region))
        {
            SavePng(settle, Path.Combine(dir, "ring2-4-settled.png"));
            ReportDiff(settle, reference, win, dock, region, "r2-settled", dir, Say);
            Say("dwm settled: " + DwmState(win));
        }
    }

    // ── 场景 2：显示名称时悬停，缩放抽搐 ────────────────────────────────

    async Task LabHoverAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== hover: 显示名称 + 悬停图标，缩放是否抽搐 ==");
        foreach (var showName in new[] { true, false })
        {
            var z = MakeLabDock(showName, showName ? "lab hover names" : "lab hover nonames");
            var win = new ZoneWindow(z, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };
            win.Show();
            await Task.Delay(1300);
            win.UpdateLayout();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            Say($"--- showName={showName} model={z.Width}x{z.Height} win={win.ActualWidth}x{win.ActualHeight} " +
                $"icon={DesktopZones.Helpers.ZoneLayout.DockIconSize(z)} hwnd=0x{hwnd.ToInt64():X}");
            try
            {
                var (tx, ty) = win.ItemIconCenterDevice(1);
                Say($"item1 icon-center device=({tx},{ty}) " + win.DebugDockScales());
                // 从图标上方 140px 处"移进来"，先让 WPF 建立命中
                await PostMoveAsync(hwnd, (tx, ty - 140), 6, 0, 140, 30, Say, "hover-in");
                await Task.Delay(150);
                Say("probe: " + win.DebugProbeAt(tx, ty));

                var samples = new List<string>();
                var frames = new List<string>();
                for (int i = 0; i < 26; i++)
                {
                    await Task.Delay(120);
                    samples.Add($"{i * 120,4}ms {win.DebugDockScales()}");
                    if (showName && i % 4 == 0)
                    {
                        var p = Path.Combine(dir, $"hover-names-f{i / 4:00}.png");
                        SaveCrop(GrabDevice(tx, ty, 200, 220), p);
                        frames.Add(p);
                    }
                }
                Say("scale timeline:");
                foreach (var s in samples) Say("  " + s);
                Say("hover events: " + win.DebugHoverLog());
                int changed = 0;
                for (int i = 1; i < frames.Count; i++)
                    if (FileDiffCount(frames[i - 1], frames[i]) > 40) changed++;
                Say($"frames={frames.Count} consecutive-changed={changed}");

                await PostMoveAsync(hwnd, (tx, ty), 4, 0, -260, 40, Say, "hover-out");
                await Task.Delay(500);
                Say("after leave: " + win.DebugDockScales());
            }
            finally { win.Close(); }
        }
    }

    // ── 场景 3：打开软件的同时拖动图标 ──────────────────────────────────

    async Task LabDragAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== drag: 拖动图标 + 拖动途中丢捕获 ==");
        var z = MakeLabDock(false, "lab drag",
            new[] { @"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\mspaint.exe", @"C:\Windows\System32\cmd.exe" });
        var win = new ZoneWindow(z, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
        { ShowInTaskbar = false };
        win.Show();
        await Task.Delay(1300);
        win.UpdateLayout();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        try
        {
            var (x0, y0) = win.ItemIconCenterDevice(0);
            Say($"win=({win.Left},{win.Top}) {win.ActualWidth}x{win.ActualHeight} item0=({x0},{y0}) " + win.DebugDockInteraction());

            // ① 基线：普通拖动应当干净收尾
            PostMouse(hwnd, WM_MOUSEMOVE, (x0, y0), Say, "move0");
            await Task.Delay(120);
            PostMouse(hwnd, WM_LBUTTONDOWN, (x0, y0), Say, "down");
            await Task.Delay(120);
            for (int i = 1; i <= 6; i++)
            {
                PostMouse(hwnd, WM_MOUSEMOVE, (x0 + i * 12, y0), Say, i == 6 ? "move+72" : "");
                await Task.Delay(60);
            }
            PostMouse(hwnd, WM_LBUTTONUP, (x0 + 72, y0), Say, "up");
            await Task.Delay(700);
            Say("[A] 普通拖动后: " + win.DebugDockInteraction());

            // ② 拖动途中把捕获夺走（= 前台窗口被激活时的真实系统行为），再松手
            var (x1, y1) = win.ItemIconCenterDevice(0);
            PostMouse(hwnd, WM_MOUSEMOVE, (x1, y1), Say, "move1");
            await Task.Delay(120);
            PostMouse(hwnd, WM_LBUTTONDOWN, (x1, y1), Say, "down2");
            await Task.Delay(120);
            for (int i = 1; i <= 5; i++)
            {
                PostMouse(hwnd, WM_MOUSEMOVE, (x1 + i * 12, y1), Say, i == 5 ? "move+60" : "");
                await Task.Delay(60);
            }
            System.Windows.Input.Mouse.Capture(null);      // ← OS 把 capture 收走
            await Task.Delay(120);
            PostMouse(hwnd, WM_LBUTTONUP, (x1 + 60, y1), Say, "up2");
            await Task.Delay(700);
            Say("[B] 拖动中丢捕获再松手后: " + win.DebugDockInteraction());

            // ③ 不按键、只移动 —— 拖动状态没被收尾的话图标会跟着跑
            for (int i = 1; i <= 6; i++)
            {
                PostMouse(hwnd, WM_MOUSEMOVE, (x1 + 60 + i * 20, y1), Say, i == 6 ? "move-only" : "");
                await Task.Delay(70);
            }
            await Task.Delay(300);
            Say("[C] 之后只移动光标: " + win.DebugDockInteraction());

            // ④ 再点一次（用户说的「再拖一次就恢复」）
            PostMouse(hwnd, WM_MOUSEMOVE, (x1, y1), Say, "move2");
            await Task.Delay(120);
            PostMouse(hwnd, WM_LBUTTONDOWN, (x1, y1), Say, "down3");
            await Task.Delay(80);
            PostMouse(hwnd, WM_LBUTTONUP, (x1, y1), Say, "up3");
            await Task.Delay(800);
            Say("[D] 再点一次后: " + win.DebugDockInteraction());
        }
        catch (Exception ex) { Say("drag case EXC " + ex); }
        finally { win.Close(); }
    }

    // ── 场景 7：玻璃层跟不跟得上内容层的层级变化（zorder）──────────────────
    //
    // 用户报「DWM 玻璃明显有两层，点一下边缘下面那层就盖到上面」。
    // 怀疑点：快捷栏玻璃是**独立窗口**（第 7 轮 B），而内容窗口失焦时会
    // `PinBelowProgman`（回落到桌面层最底部）—— 玻璃层不跟着动，就从「内容层下面」
    // 翻到「上面」。沙箱里动不了光标，这里不模拟鼠标，直接调那两条层级路径，
    // 用 `GetWindow(GW_HWNDPREV)` 看两者到底谁紧挨着谁，并抓图对照。

    async Task LabZOrderAsync(string dir, Action<string> Say)
    {
        Say("");
        Say("== zorder: 玻璃层 vs 内容层 的层级归属 ==");
        bool oldScheme = DesktopZones.Helpers.WallpaperSource.Enabled;
        ZoneWindow? win = null;
        ZoneWindow? sibling = null;
        try
        {
            DesktopZones.Helpers.WallpaperSource.Enabled = false;   // 渲染方案 = DWM
            var z = MakeLabDock(true, "lab zorder");
            z.EnableLiquidGlass = true;
            z.TileMode = false;
            z.DockAnimate = true;
            z.DockMagnify = 1.8;
            z.DockReflection = true;
            DesktopZones.Helpers.ZoneLayout.ApplyDockSize(z);

            win = new ZoneWindow(z, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };
            win.Show();
            await Task.Delay(1200);
            // 其它分区/快捷栏收起来：既免得盖住抓屏区域，也免得干扰层级比较。
            // **收玻璃层也要收**（它们是独立 HWND：只 Hide 内容窗口会留下一个悬空的胶囊）。
            foreach (Window w in Application.Current.Windows)
                if (w is ZoneWindow zw && !ReferenceEquals(zw, win) && zw.IsVisible)
                {
                    zw.DebugSetDockGlassVisible(false);
                    zw.Hide();
                }
            MoveToFreeSpot(win, Say);
            await Task.Delay(1200);
            win.UpdateLayout();

            // 造一个「兄弟桌面层窗口」压在下面。没有兄弟时 PinBelowProgman 是空操作
            // （内容窗口本来就在最底部），翻不过来；生产里快捷栏上面永远有别的分区/挂件。
            var zsib = MakeLabDock(false, "lab sibling");
            sibling = new ZoneWindow(zsib, _zoneManager!, DesktopZones.Services.ShellIconService.Instance)
            { ShowInTaskbar = false };
            sibling.Show();
            sibling.Left = 40;
            sibling.Top = 40;
            await Task.Delay(700);
            Say($"sibling=0x{new System.Windows.Interop.WindowInteropHelper(sibling).Handle.ToInt64():X}");

            var content = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            var glass = win.DebugDockGlassHwnd();
            Say($"content=0x{content.ToInt64():X} glass=0x{glass.ToInt64():X}");
            Say("state: " + win.DebugDockGlass());
            Say("own windows:\n" + DumpOwnWindows());
            var region = CaptureRegion(win, 44);

            void Shot(string tag)
            {
                using var bmp = Grab(region);
                SavePng(bmp, Path.Combine(dir, $"z-{tag}.png"));
            }
            void Log(string tag)
            {
                var g = win.DebugDockGlassHwnd();
                var s = sibling == null ? IntPtr.Zero : new System.Windows.Interop.WindowInteropHelper(sibling).Handle;
                Say($"  [{tag}] {ZRelation(content, g)}");
                Say("    z order (top→bottom):\n" + ZChainDump(content, g, s));
                Say($"         state: " + win.DebugDockGlass());
            }

            // ① 点胶囊（内容层被提到桌面层最顶）——生产代码在这之后会 SyncDockGlassWindow()
            DesktopZones.Helpers.DesktopLayer.BringToFront(win);
            win.RefreshZone(z);
            await Task.Delay(900);
            Shot("1-raised"); Log("1-raised");

            // ② 失焦路径 = 点胶囊外面的透明处 → 桌面拿到点击 → 内容窗口 Deactivated
            //    → PinBelowProgman（这一条**不动玻璃层**，怀疑就是它把两层翻过来的）
            DesktopZones.Helpers.NativeMethods.PinBelowProgman(win);
            await Task.Delay(900);
            Shot("2-pinned"); Log("2-pinned");

            // ③ 再点回胶囊 → 又提到最顶（**故意不刷新**：层级守卫自己应该在下一轮修回来）
            DesktopZones.Helpers.DesktopLayer.BringToFront(win);
            await Task.Delay(600);
            Shot("3-brought-front"); Log("3-brought-front");
            win.RefreshZone(z);
            await Task.Delay(700);
            Shot("4-refreshed"); Log("4-refreshed");

            // ⑤ 故意把层级翻成"玻璃在内容上面"（= 用户报的现象），看守卫能不能自己修回来。
            {
                var g = win.DebugDockGlassHwnd();
                bool ok = SetWindowPosEx(content, g, 0, 0, 0, 0,
                    SWP_NOMOVE_ | SWP_NOSIZE_ | SWP_NOACTIVATE_ | SWP_SHOWWINDOW_);
                // 立刻量（不等消息泵）：这才是"破掉"的瞬间。
                Say($"  forced-flip SetWindowPos(content→glass) ok={ok} → {ZRelation(content, g)}");
                // 阻塞 UI 线程 120ms 抓一张"坏掉的画面"：守卫还排在队列里没跑，DWM 已经按
                // 新层级合成完了 —— 这张就是用户看到的"玻璃盖在图标上"。
                System.Threading.Thread.Sleep(120);
                using (var broken = Grab(region))
                {
                    SavePng(broken, Path.Combine(dir, "z-5-flipped.png"));
                    var pill = PillRectInRegion(win, z, region);
                    Say($"  flipped 画面: pillLuma={MeanLuma(broken, pill):F1} sharp={Sharpness(broken, pill):F1}");
                }
                await Task.Delay(1000);
                Shot("5-after-flip"); Log("5-after-flip（守卫应在这一帧之前修回）");
            }

            // ④ 环境探针（放在最后，别污染上面的层级测量）
            //    先确认这台机（低完整性沙箱）到底能不能改 z 序：不能的话结论要打折看。
            {
                var prog = DesktopZones.Helpers.NativeMethods.GetProgmanHandle();
                bool ok = SetWindowPosEx(content, prog, 0, 0, 0, 0,
                    SWP_NOMOVE_ | SWP_NOSIZE_ | SWP_NOACTIVATE_ | SWP_SHOWWINDOW_);
                Say($"  probe: direct SetWindowPos(content, progman=0x{prog.ToInt64():X}) " +
                    $"ok={ok} err={Marshal.GetLastWin32Error()}（err=5 就是 UIPI 挡了，沙箱限制）");
            }
            // 同一进程里的 z 序方向探针：先 SetWindowPos(content, sibling)，再反过来，
            // 用来看「insertAfter 到底是上面还是下面」。
            if (sibling != null)
            {
                var sibHwnd = new System.Windows.Interop.WindowInteropHelper(sibling).Handle;
                bool okA = SetWindowPosEx(content, sibHwnd, 0, 0, 0, 0,
                    SWP_NOMOVE_ | SWP_NOSIZE_ | SWP_NOACTIVATE_ | SWP_SHOWWINDOW_);
                await Task.Delay(250);
                Say($"  probe A SetWindowPos(content→sibling) ok={okA} err={Marshal.GetLastWin32Error()}");
                Say(ZChainDump(content, win.DebugDockGlassHwnd(), sibHwnd, 3));
                bool okB = SetWindowPosEx(sibHwnd, content, 0, 0, 0, 0,
                    SWP_NOMOVE_ | SWP_NOSIZE_ | SWP_NOACTIVATE_ | SWP_SHOWWINDOW_);
                await Task.Delay(250);
                Say($"  probe B SetWindowPos(sibling→content) ok={okB} err={Marshal.GetLastWin32Error()}");
                Say(ZChainDump(content, win.DebugDockGlassHwnd(), sibHwnd, 3));
            }
        }
        catch (Exception ex) { Say("zorder EXC " + ex); }
        finally
        {
            try { sibling?.Close(); } catch { }
            try { win?.Close(); } catch { }
            DesktopZones.Helpers.WallpaperSource.Enabled = oldScheme;
        }
    }

    /// <summary>从 content 往上/往下各走几个窗口，标出 content / glass / sibling。
    /// 只靠 PREV/NEXT 判断关系容易把方向弄反，把两边的邻居都打出来最省事。</summary>
    static string ZChainDump(IntPtr content, IntPtr glass, IntPtr sibling, int n = 5)
    {
        if (content == IntPtr.Zero) return "      (no content hwnd)";
        string Name(IntPtr h)
        {
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            GetWindowRect(h, out var r);
            string tag = h == content ? " <=CONTENT" : h == glass ? " <=GLASS" : h == sibling ? " <=SIBLING" : "";
            return $"0x{h.ToInt64():X} {cls} ({r.Left},{r.Top})-({r.Right},{r.Bottom}){tag}";
        }
        var sb = new StringBuilder();
        var up = new List<string>();
        var cur = GetWindow(content, GW_HWNDPREV);
        for (int i = 0; i < n && cur != IntPtr.Zero; i++) { up.Add(Name(cur)); cur = GetWindow(cur, GW_HWNDPREV); }
        for (int i = up.Count - 1; i >= 0; i--) sb.AppendLine("        ↑ " + up[i]);
        sb.AppendLine("        = " + Name(content));
        cur = GetWindow(content, GW_HWNDNEXT);
        for (int i = 0; i < n && cur != IntPtr.Zero; i++) { sb.AppendLine("        ↓ " + Name(cur)); cur = GetWindow(cur, GW_HWNDNEXT); }
        return sb.ToString().TrimEnd();
    }

    /// <summary>本进程所有**可见**顶层窗口（类名 + 屏幕矩形）。用来发现「多出来的胶囊」。</summary>
    static string DumpOwnWindows()
    {
        var sb = new StringBuilder();
        uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pid != me || !IsWindowVisible(h)) return true;
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            GetWindowRect(h, out var r);
            sb.AppendLine($"    hwnd=0x{h.ToInt64():X} cls={cls} rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}");
            return true;
        }, IntPtr.Zero);
        return sb.ToString().TrimEnd();
    }

    /// <summary>两个窗口在 z 序里的相对位置。除了"紧挨着"还给一个明确结论：
    /// **玻璃到底在内容上面还是下面**（上面=用户报的那种盖住图标的状态）。</summary>
    static string ZRelation(IntPtr content, IntPtr glass)
    {
        if (content == IntPtr.Zero) return "content=0";
        if (glass == IntPtr.Zero) return "glass=0（这一轮没有独立玻璃窗）";
        var aboveGlass = GetWindow(glass, GW_HWNDPREV);
        var aboveContent = GetWindow(content, GW_HWNDPREV);
        bool glassAbove = IsAbove(glass, content);      // 玻璃在内容上面？
        bool glassBelow = IsAbove(content, glass);      // 内容在玻璃上面（= 正解）
        return $"above(glass)=0x{aboveGlass.ToInt64():X} above(content)=0x{aboveContent.ToInt64():X} | " +
               $"contentDirectlyAboveGlass={aboveGlass == content} " +
               $"glassDirectlyAboveContent={aboveContent == glass} | " +
               $"VERDICT glassAboveContent={glassAbove} glassBelowContent={glassBelow}" +
               (glassAbove ? "  ← 玻璃盖住内容（BUG）" : glassBelow ? "  ← 正确" : "  ← 不同 z 序带？");
    }

    /// <summary><paramref name="a"/> 是否在 <paramref name="b"/> 的**上面**（顺着往下走能找到 b）。</summary>
    static bool IsAbove(IntPtr a, IntPtr b)
    {
        if (a == IntPtr.Zero || b == IntPtr.Zero) return false;
        var cur = GetWindow(a, GW_HWNDNEXT);
        for (int i = 0; i < 96 && cur != IntPtr.Zero; i++)
        {
            if (cur == b) return true;
            cur = GetWindow(cur, GW_HWNDNEXT);
        }
        return false;
    }

    // ── 造/摆放测试用快捷栏 ──────────────────────────────────────────────

    static void MoveToFreeSpot(ZoneWindow win, Action<string> Say)
    {
        // 桌面层窗口在所有应用窗口之下：挪到壁纸的右下空白区，点击才落得到它身上。
        var wa = SystemParameters.WorkArea;
        win.Left = wa.Right - win.Width - 260;
        win.Top = wa.Bottom - win.Height - 150;
        Say($"moved to free spot ({win.Left:F0},{win.Top:F0})");
    }

    Zone MakeLabDock(bool showName, string name, string[]? items = null)
    {
        var z = new Zone
        {
            Name = name,
            DockMode = true,
            HideAppName = !showName,
            GridSize = 48,
            CornerRadius = 12,
            BorderThickness = 1.0,
            IsVisible = true,
            EnableLiquidGlass = false,
            FillColor = "#CC101418",
            BorderColor = "#66FFFFFF",
            DockAnimate = true,
            DockMagnify = 1.8,
            DockAnimMs = 160,
            DockReflection = true,
            DockReflectionOpacity = 74,
            EnableRestoreButton = true,
            DockHoverRestore = true,
            DockClickOnce = true,
            DockRunIndicator = false,
            DockStartButton = false,
            TileMode = false,
        };
        AddLabItems(z, items ?? new[]
        {
            @"C:\Windows\System32\notepad.exe",
            @"C:\Windows\System32\mspaint.exe",
            @"C:\Windows\System32\cmd.exe",
            @"C:\Windows\System32\control.exe",
        });
        DesktopZones.Helpers.ZoneLayout.ApplyDockSize(z);
        var wa = SystemParameters.WorkArea;
        z.X = wa.Right - z.Width - 320;
        z.Y = wa.Bottom - z.Height - 200;
        return z;
    }

    static void AddLabItems(Zone z, string[] paths)
    {
        foreach (var path in paths)
        {
            if (!File.Exists(path)) continue;
            var (target, type, iconLoc) = DesktopZones.Helpers.ShortcutResolver.NormalizeItem(path, ItemType.Application);
            var item = new ZoneItem(Path.GetFileNameWithoutExtension(path), target, type, 0, 0) { IconPath = iconLoc };
            DesktopZones.Services.ShellIconService.Instance.GetIcon(item.TargetPath, item.Type, item.IconPath);
            z.Items.Add(item);
        }
    }

    // ── 工具 ──────────────────────────────────────────────────────────────

    static string DwmState(Window win)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
            int border = -1, corner = -1, thick = -1;
            DwmGetWindowAttribute(hwnd, 34, out border, 4);   // DWMWA_BORDER_COLOR
            DwmGetWindowAttribute(hwnd, 33, out corner, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE
            DwmGetWindowAttribute(hwnd, 37, out thick, 4);    // DWMWA_VISIBLE_FRAME_BORDER_THICKNESS
            var r = new LabRect();
            int hr = DwmGetWindowAttribute(hwnd, 9, ref r, Marshal.SizeOf<LabRect>());  // EXTENDED_FRAME_BOUNDS
            return $"border=0x{border:X} cornerPref={corner} frameThick={thick} " +
                   (hr == 0 ? $"extFrame=({r.Left},{r.Top},{r.Right},{r.Bottom})" : "extFrame=n/a");
        }
        catch (Exception ex) { return "dwm-exc " + ex.Message; }
    }

    static (int X, int Y) CursorPos() { GetCursorPos(out var p); return (p.X, p.Y); }

    /// <summary>屏幕点上最顶层的窗口（顶层 HWND）。桌面层的分区/快捷栏在所有应用窗口之下，
    /// 被盖住时点击会被别人吃掉 —— 点之前必须确认这一点。</summary>
    static IntPtr WindowAt((int X, int Y) p)
    {
        var h = WindowFromPoint(new LabPoint { X = p.X, Y = p.Y });
        return h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, 2);
    }

    /// <summary>安全点击：确认该点最顶层就是目标窗口，否则只记录、不点（绝不误点用户窗口）。</summary>
    static bool SafeClick(ZoneWindow win, (int X, int Y) p, Action<string> Say)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        var at = WindowAt(p);
        if (at != hwnd)
        {
            Say($"  ! click skipped at ({p.X},{p.Y}): top window is 0x{at.ToInt64():X}, not dock 0x{hwnd.ToInt64():X}");
            return false;
        }
        // 沙箱里 SetCursorPos 返回 false（低完整性进程不让动系统光标）→ 注入的点击会落在
        // 真实光标位置（可能点到用户窗口），所以这里**只探测、不注入**。
        Say($"  (real input skipped: SetCursorPos in sandbox is blocked)");
        return false;
    }

    static async Task MouseDragAsync((int X, int Y) from, int dx, int steps)
    {
        SetCursorPos(from.X, from.Y);
        await Task.Delay(80);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(80);
        for (int i = 1; i <= steps; i++)
        {
            SetCursorPos(from.X + dx * i / steps, from.Y);
            await Task.Delay(50);
        }
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }

    // ── 合成鼠标消息 ─────────────────────────────────────────────────────
    // 沙箱里（低完整性）SetCursorPos/mouse_event 不生效（实测光标根本没动、窗口收不到
    // 任何输入）。改成直接往自家窗口投 WM_MOUSEMOVE/WM_LBUTTONDOWN/WM_LBUTTONUP：
    // WPF 的 HwndMouseInputProvider 会照常做命中测试并派发 MouseEnter/MouseLeave/点击，
    // 命中行为与真实鼠标完全一致（只是不用动系统光标、也不打扰用户）。

    static (int X, int Y) ToClientPx(IntPtr hwnd, (int X, int Y) screen)
    {
        var p = new LabPoint { X = screen.X, Y = screen.Y };
        ScreenToClient(hwnd, ref p);
        return (p.X, p.Y);
    }

    static void PostMouse(IntPtr hwnd, uint msg, (int X, int Y) screen, Action<string> Say, string tag)
    {
        var c = ToClientPx(hwnd, screen);
        var lp = (IntPtr)(((c.Y & 0xFFFF) << 16) | (c.X & 0xFFFF));
        PostMessage(hwnd, msg, IntPtr.Zero, lp);
        Say($"  post {tag} client=({c.X},{c.Y})");
    }

    static async Task PostMoveAsync(IntPtr hwnd, (int X, int Y) screen, int steps, int dx, int dy, int stepMs, Action<string> Say, string tag)
    {
        for (int i = 0; i <= steps; i++)
        {
            var p = (screen.X + dx * i / Math.Max(1, steps), screen.Y + dy * i / Math.Max(1, steps));
            PostMouse(hwnd, WM_MOUSEMOVE, p, Say, i == steps ? tag : "");
            await Task.Delay(stepMs);
        }
    }

    static DRect CaptureRegion(ZoneWindow win, int pad)
    {
        try
        {
            var tl = win.PointToScreen(new Point(0, 0));
            var br = win.PointToScreen(new Point(win.ActualWidth, win.ActualHeight));
            int x = (int)Math.Floor(tl.X) - pad, y = (int)Math.Floor(tl.Y) - pad;
            int w = (int)Math.Ceiling(br.X - tl.X) + pad * 2, h = (int)Math.Ceiling(br.Y - tl.Y) + pad * 2;
            return new DRect(x, y, Math.Max(1, w), Math.Max(1, h));
        }
        catch { return new DRect(0, 0, 10, 10); }
    }

    static DBitmap Grab(DRect r)
    {
        var bmp = new DBitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), DPixelFormat.Format32bppArgb);
        using var g = DGraphics.FromImage(bmp);
        g.CopyFromScreen(r.X, r.Y, 0, 0, new DSize(bmp.Width, bmp.Height), DCopyOp.SourceCopy);
        return bmp;
    }

    static DBitmap GrabDevice(int cx, int cy, int w, int h)
        => Grab(new DRect(cx - w / 2, cy - h / 2, w, h));

    static void SavePng(DBitmap bmp, string path)
    {
        try { bmp.Save(path, DImageFormat.Png); } catch { }
    }

    static void SaveCrop(DBitmap bmp, string path)
    {
        SavePng(bmp, path);
        bmp.Dispose();
    }

    /// <summary>与参照图求差，按「胶囊 / 两条透明带 / 窗口外」分区统计 + 存差异图。</summary>
    static void ReportDiff(DBitmap shot, DBitmap reference, ZoneWindow win, Zone zone,
                           DRect region, string tag, string dir, Action<string> Say)
    {
        double side = DesktopZones.Helpers.ZoneLayout.DockSideOverflow(zone);
        double barTop = DesktopZones.Helpers.ZoneLayout.DockBarTop(zone);
        double barH = DesktopZones.Helpers.ZoneLayout.DockBarHeight(zone);
        double pillW = DesktopZones.Helpers.ZoneLayout.DockPillWidth(zone, zone.Items.Count);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win);
        var tl = win.PointToScreen(new Point(0, 0));
        int winX = (int)Math.Floor(tl.X) - region.X;
        int winY = (int)Math.Floor(tl.Y) - region.Y;
        int pillL = winX + (int)Math.Round(side * dpi.DpiScaleX);
        int pillT = winY + (int)Math.Round(barTop * dpi.DpiScaleY);
        int pillR = pillL + (int)Math.Round(pillW * dpi.DpiScaleX);
        int pillB = pillT + (int)Math.Round(barH * dpi.DpiScaleY);
        int winR = winX + (int)Math.Round(win.ActualWidth * dpi.DpiScaleX);
        int winB = winY + (int)Math.Round(win.ActualHeight * dpi.DpiScaleY);

        int w = Math.Min(shot.Width, reference.Width), h = Math.Min(shot.Height, reference.Height);
        var outBmp = new DBitmap(w, h, DPixelFormat.Format32bppArgb);
        int inPill = 0, inBand = 0, outside = 0, maxOut = 0;
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = 0, by1 = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var pa = shot.GetPixel(x, y); var pb = reference.GetPixel(x, y);
                int d = Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                bool inPillRect = x >= pillL && x < pillR && y >= pillT && y < pillB;
                bool inWin = x >= winX && y >= winY && x < winR && y < winB;
                if (d > 18)
                {
                    if (inPillRect) inPill++;
                    else if (inWin) inBand++;
                    else { outside++; maxOut = Math.Max(maxOut, d); }
                    if (x < bx0) bx0 = x;
                    if (y < by0) by0 = y;
                    if (x + 1 > bx1) bx1 = x + 1;
                    if (y + 1 > by1) by1 = y + 1;
                    outBmp.SetPixel(x, y, DColor.FromArgb(255, Math.Min(255, 80 + d), 0, 0));
                }
                else outBmp.SetPixel(x, y, DColor.FromArgb(255, 0, 36, 0));
            }
        }
        SavePng(outBmp, Path.Combine(dir, $"ring-diff-{tag}.png"));
        outBmp.Dispose();
        string bbox = bx0 == int.MaxValue ? "none" : $"({bx0},{by0})-({bx1},{by1})";
        Say($"  [{tag}] diff pill={inPill} band={inBand} outside={outside}(maxD={maxOut}) bbox={bbox} " +
            $"pillRect=({pillL},{pillT})-({pillR},{pillB}) winRect=({winX},{winY})-({winR},{winB})");
    }

    static int FileDiffCount(string p1, string p2)
    {
        try
        {
            using var a = new DBitmap(p1);
            using var b = new DBitmap(p2);
            int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height), n = 0;
            for (int y = 0; y < h; y += 2)
                for (int x = 0; x < w; x += 2)
                {
                    var pa = a.GetPixel(x, y); var pb = b.GetPixel(x, y);
                    if (Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B) > 24) n++;
                }
            return n;
        }
        catch { return -1; }
    }
}
#endif
