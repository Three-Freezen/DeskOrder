using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DesktopZones.Helpers;

namespace DesktopZones.Services;

/// <summary>
/// 桌面窗口观察器 —— 快捷栏「运行状态指示」与「点图标切前台」的数据源。
///
/// 判据和 Windows 任务栏一致：某个条目对应的 exe **有可见的顶层窗口** = 正在运行；
/// 那个窗口正好是 <c>GetForegroundWindow()</c> = 在前台。所以「同一时刻只有一条蓝条」
/// 是天然成立的（前台窗口只有一个），不需要自己记账。
///
/// 匹配口径用 **exe 文件名**（如 <c>notepad</c>），不是完整路径：快捷方式/应用引用
/// 常常指向同一程序的另一个路径，按名匹配才不会漏；代价是两个同名进程会并成一条
/// （与任务栏按 app 归并的做法一致）。
///
/// 全部按需枚举（每次快照一次 EnumWindows），没有常驻钩子：轮询频率由调用方
/// （<see cref="Views.ZoneWindow"/> 的 700ms 定时器）控制，快捷栏关掉该功能就不轮询。
/// </summary>
public static class AppActivityService
{
    /// <summary>一个正在运行的应用：exe 名（小写）、优先窗口句柄、以及它是不是当前前台。</summary>
    public readonly record struct RunningApp(string ExeName, IntPtr Hwnd, bool IsForeground);

    /// <summary>系统 shell 自己的窗口类名 —— 属于「桌面」，不是用户的应用。</summary>
    static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "Windows.UI.Input.InputSite.WindowClass", "MultitaskingViewFrame",
        "ForegroundStaging", "XamlExplorerHostIslandWindow", "SysListView32",
    };

    static readonly NativeMethods.EnumWindowsProc _enumProc = EnumWindow;

    // 一次枚举内的临时状态（避免每次都分配闭包）
    [ThreadStatic] static Dictionary<string, RunningApp>? _map;
    [ThreadStatic] static IntPtr _foreground;

    /// <summary>最近一次「别人的窗口」在前台的句柄（跳过本进程自己的窗口）。
    ///
    /// 为什么需要它：点快捷栏图标那一下，我们的窗口会先被激活，此时
    /// <c>GetForegroundWindow()</c> 已经是我们自己了 —— 想判断"刚才是从哪个程序切过来的"
    /// 只能靠提前记一笔。首选是 WM_MOUSEACTIVATE 里抓（激活之前，最准，见
    /// <c>ZoneWindow.WndProc</c>），这里作为兜底：轮询每次看到别人的前台窗口就记下来。</summary>
    public static IntPtr LastOtherForeground { get; private set; }

    /// <summary>
    /// 抓一份「正在运行的应用」快照：key = 小写 exe 名（不含 .exe）。
    /// 同名多个窗口时优先取前台窗口，其次取第一个未最小化的。
    /// </summary>
    public static Dictionary<string, RunningApp> Snapshot()
    {
        var map = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        _map = map;
        var ownPid = (uint)Environment.ProcessId;

        var fg = NativeMethods.GetForegroundWindow();
        fg = fg == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT);
        _foreground = fg;

        try { NativeMethods.EnumWindows(_enumProc, IntPtr.Zero); }
        catch { /* 枚举失败就当没有运行中的应用，不抛给 UI */ }
        finally { _map = null; }

        // 自己也要算「在运行」：快捷栏里放 DeskOrder 自己的图标是很自然的用法，
        // 而快捷栏的窗口没有标题（EnumWindow 会按"无标题宿主窗口"跳过），
        // 不显式补一条就永远看不到它自己的运行短线（用户反馈"看不到指示条"）。
        try
        {
            using var self = Process.GetCurrentProcess();
            var name = self.ProcessName.ToLowerInvariant();
            bool isForeground = false;
            if (fg != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
                isForeground = fgPid == ownPid;
            }
            map[name] = new RunningApp(name, fg, isForeground);
        }
        catch { }

        return map;
    }

    static bool EnumWindow(IntPtr hwnd, IntPtr lParam)
    {
        var map = _map;
        if (map == null) return true;
        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;

            // 工具窗口（浮动面板/托盘气泡等）不算「应用在运行」
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;

            // 无标题的窗口多半是隐藏宿主窗口，跳过（任务栏也是这个口径）
            if (NativeMethods.GetWindowTextLength(hwnd) <= 0) return true;

            var cls = new StringBuilder(96);
            if (NativeMethods.GetClassName(hwnd, cls, cls.Capacity) > 0
                && ShellClasses.Contains(cls.ToString())) return true;

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;

            var exe = ExeNameOf(pid);
            if (string.IsNullOrEmpty(exe)) return true;

            bool isForeground = hwnd == _foreground;
            if (isForeground && pid != (uint)Environment.ProcessId) LastOtherForeground = hwnd;
            if (map.TryGetValue(exe, out var existing))
            {
                // 已经有一条：前台优先；否则保留未最小化的那条
                if (existing.IsForeground) return true;
                if (isForeground || (!NativeMethods.IsIconic(hwnd) && NativeMethods.IsIconic(existing.Hwnd)))
                    map[exe] = new RunningApp(exe, hwnd, isForeground);
                return true;
            }
            map[exe] = new RunningApp(exe, hwnd, isForeground);
        }
        catch
        {
            // 单个窗口取信息失败（权限/竞态）不影响整轮
        }
        return true;
    }

    /// <summary>pid → 小写 exe 名（不含扩展名）。取不到就返回空串。</summary>
    static string ExeNameOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>条目目标路径 → 期望的 exe 名（小写、不含扩展名）。</summary>
    public static string ExeNameForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var name = System.IO.Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) return "";
            int dot = name.LastIndexOf('.');
            if (dot > 0) name = name[..dot];
            return name.ToLowerInvariant();
        }
        catch { return ""; }
    }

    /// <summary>
    /// 把某个 exe 的窗口拉到前台（最小化的先还原）。
    /// 直连 <c>SetForegroundWindow</c> 在别的进程有焦点时会被系统拦下，所以先
    /// AttachThreadInput 到前台线程再调 —— 桌面启动器的常规做法。
    /// </summary>
    public static bool TryActivate(string exeName, out bool found)
    {
        found = false;
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        var snapshot = Snapshot();
        if (!snapshot.TryGetValue(exeName, out var app)) return false;
        found = true;

        var hwnd = app.Hwnd;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);

            var fg = NativeMethods.GetForegroundWindow();
            uint fgThread = fg == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(fg, out _);
            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
            bool attached = false;
            if (fgThread != 0 && targetThread != 0 && fgThread != targetThread)
                attached = NativeMethods.AttachThreadInput(fgThread, targetThread, true);
            try
            {
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(fgThread, targetThread, false);
            }
            return true;
        }
        catch { return false; }
    }

    // ── 点图标：切前台 / 再点最小化（与 Windows 任务栏一致） ──

    [ThreadStatic] static List<IntPtr>? _collect;
    static string _collectExe = "";
    static readonly NativeMethods.EnumWindowsProc _collectProc = CollectWindow;

    static bool CollectWindow(IntPtr hwnd, IntPtr lParam)
    {
        var list = _collect;
        if (list == null) return true;
        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (NativeMethods.GetWindowTextLength(hwnd) <= 0) return true;
            var cls = new StringBuilder(96);
            if (NativeMethods.GetClassName(hwnd, cls, cls.Capacity) > 0
                && ShellClasses.Contains(cls.ToString())) return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return true;
            if (!string.Equals(ExeNameOf(pid), _collectExe, StringComparison.OrdinalIgnoreCase)) return true;
            list.Add(hwnd);
        }
        catch { }
        return true;
    }

    /// <summary>某个 exe 的所有可见顶层窗口（口径与 <see cref="Snapshot"/> 完全一致）。</summary>
    public static List<IntPtr> WindowsOf(string exeName)
    {
        var list = new List<IntPtr>(4);
        if (string.IsNullOrWhiteSpace(exeName)) return list;
        _collect = list;
        _collectExe = exeName;
        try { NativeMethods.EnumWindows(_collectProc, IntPtr.Zero); }
        catch { }
        finally { _collect = null; _collectExe = ""; }
        return list;
    }

    /// <summary>这个窗口属于该 exe 吗（句柄已失效 / 取不到进程 → false）。</summary>
    public static bool WindowBelongsTo(IntPtr hwnd, string exeName)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(exeName)) return false;
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return false;
            return string.Equals(ExeNameOf(pid), exeName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 点快捷栏图标：**它已经在最前面 → 最小化；否则切到前台**（Windows 任务栏同一套语义）。
    ///
    /// <paramref name="fgHint"/>= 点击之前的前台窗口：点击会先把快捷栏窗口激活，
    /// 那时候再问 <c>GetForegroundWindow()</c> 得到的已经是我们自己，判不出"是不是它"。
    /// 返回 false = 它根本没在跑，调用方按普通"启动"处理。
    /// </summary>
    public static bool TryToggle(string exeName, IntPtr fgHint, out bool minimized)
    {
        minimized = false;
        if (string.IsNullOrWhiteSpace(exeName)) return false;
        var wins = WindowsOf(exeName);
        if (wins.Count == 0) return false;

        if (WindowBelongsTo(fgHint, exeName))
        {
            // 它已经全部最小化了（比如刚"显示桌面"过）→ 这一下该还原，不能再"最小化一次"
            // 什么都不发生。窗口还有正常状态时才真的收起来。
            bool anyNormal = false;
            foreach (var h in wins)
            {
                try { if (!NativeMethods.IsIconic(h)) { anyNormal = true; break; } } catch { }
            }
            if (anyNormal)
            {
                foreach (var h in wins)
                {
                    try { NativeMethods.ShowWindowAsync(h, NativeMethods.SW_MINIMIZE); } catch { }
                }
                minimized = true;
                return true;
            }
        }
        return TryActivate(exeName, out _);
    }

    // ── 资源管理器打开着的文件夹（快捷栏里「文件夹快捷方式」的运行状态） ──

    static List<(string Path, IntPtr Hwnd)>? _explorerCache;
    static DateTime _explorerCacheAt = DateTime.MinValue;

    /// <summary>
    /// 资源管理器当前打开的文件夹窗口（规范化路径 + 窗口句柄）。
    ///
    /// 取法用 Shell.Application 的 Windows() 集合：窗口标题会受"在标题栏显示完整路径"
    /// 设置和同名目录影响，不可靠；而 <c>LocationURL</c> 就是那个窗口**真正在浏览的路径**
    /// （压缩包打开后给出 .zip 自身，正好对上快捷栏里的 zip 条目）。
    /// 缓存 400ms —— 快捷栏每 700ms 轮询一次，多个快捷栏同时问不该每次都起一个 COM 实例。
    /// </summary>
    /// <summary>最近一次 <see cref="ExplorerLocations"/> 的过程诊断（自测用）。</summary>
    public static string ExplorerDiag { get; private set; } = "";

    public static List<(string Path, IntPtr Hwnd)> ExplorerLocations()
    {
        var now = DateTime.UtcNow;
        if (_explorerCache != null && (now - _explorerCacheAt).TotalMilliseconds < 400) return _explorerCache;

        var list = new List<(string, IntPtr)>(8);
        var diag = new StringBuilder();
        try
        {
            var t = Type.GetTypeFromProgID("Shell.Application");
            diag.Append($"progid={(t == null ? "null" : t.FullName)}; ");
            if (t != null && Activator.CreateInstance(t) is { } shellObj)
            {
                dynamic shell = shellObj;
                dynamic windows = shell.Windows();
                int n = 0;
                try { n = Convert.ToInt32(windows.Count); } catch (Exception ex) { diag.Append("countEXC=" + ex.Message + "; "); }
                diag.Append($"n={n}; ");
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        // IShellWindows.Item 是 0 基
                        dynamic w = windows.Item(i);
                        string url = "";
                        try { url = Convert.ToString(w.LocationURL) ?? ""; } catch { }
                        if (!url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) { diag.Append($"[{i}]skip'{url}'; "); continue; }
                        string path;
                        try { path = new Uri(url).LocalPath; } catch { continue; }
                        path = NormalizeFolderPath(path);
                        if (path.Length == 0) continue;
                        IntPtr hwnd = IntPtr.Zero;
                        try { hwnd = new IntPtr(Convert.ToInt64(w.HWND)); } catch { }
                        list.Add((path, hwnd));
                        diag.Append($"[{i}]{path}; ");
                    }
                    catch (Exception ex) { diag.Append($"[{i}]EXC={ex.Message}; "); }
                }
            }
            else diag.Append("create failed; ");
        }
        catch (Exception ex) { diag.Append("OUTER EXC=" + ex.GetType().Name + ": " + ex.Message); }

        ExplorerDiag = diag.ToString();
        _explorerCache = list;
        _explorerCacheAt = now;
        return list;
    }

    /// <summary>路径规范化（比较用）：小写、统一下划线、去掉尾随分隔符。</summary>
    public static string NormalizeFolderPath(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        var s = p.Trim().Replace('/', '\\');
        while (s.Length > 1 && s[^1] == '\\') s = s[..^1];
        return s.ToLowerInvariant();
    }

    /// <summary>这个条目目标算不算「能打开来看的文件夹」：目录，或资源管理器能进去的压缩包。</summary>
    public static bool IsBrowsableFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            if (System.IO.Directory.Exists(path)) return true;
            if (!System.IO.File.Exists(path)) return false;
            var ext = System.IO.Path.GetExtension(path);
            return ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".cab", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>这个文件夹（或它的某个子目录、或它自己的压缩包视图）正在被浏览吗？
    /// 返回那个资源管理器窗口的句柄；0 = 没打开。
    ///
    /// 两条路：
    ///  ① COM（Shell.Application）：窗口**真正在浏览的路径**，精确、能识别子目录；
    ///  ② 兜底：按窗口标题匹配文件夹名 / 完整路径。COM 在某些安全策略下会直接
    ///     抛 E_ACCESSDENIED（本机沙箱里实测就是 0x80070005），没有兜底的话这个功能
    ///     在那种环境里就永远是"没打开"。代价是同名目录会误判成"打开着"。
    /// </summary>
    public static IntPtr FolderOpenWindow(string? path)
    {
        var want = NormalizeFolderPath(path);
        if (want.Length == 0) return IntPtr.Zero;

        foreach (var (loc, hwnd) in ExplorerLocations())
        {
            if (loc == want || loc.StartsWith(want + "\\", StringComparison.Ordinal)) return hwnd;
        }

        var leaf = LeafOfFolder(want);
        foreach (var (title, hwnd) in ExplorerWindowTitles())
        {
            var t = title.Trim();
            if (t.Length == 0) continue;
            // Win11 的窗口标题是「<文件夹名> - 文件资源管理器」（或 " - File Explorer"），
            // 不剥这个后缀的话标题永远对不上文件夹名。
            if (string.Equals(t, leaf, StringComparison.OrdinalIgnoreCase)) return hwnd;
            if (t.StartsWith(leaf + " - ", StringComparison.OrdinalIgnoreCase)) return hwnd;
            if (NormalizeFolderPath(t) == want) return hwnd;      // 标题栏显示完整路径时
        }
        return IntPtr.Zero;
    }

    /// <summary>路径最后一段（"d:\a\b" → "b"）—— 资源管理器默认就拿它当窗口标题。</summary>
    static string LeafOfFolder(string normalized)
    {
        int i = normalized.LastIndexOf('\\');
        return i >= 0 && i + 1 < normalized.Length ? normalized[(i + 1)..] : normalized;
    }

    // ── 资源管理器窗口标题（COM 不可用时的兜底判据） ──

    static readonly HashSet<string> ExplorerClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CabinetWClass", "ExploreWClass",
    };

    [ThreadStatic] static List<(string Title, IntPtr Hwnd)>? _titles;
    static readonly NativeMethods.EnumWindowsProc _titlesProc = CollectTitle;

    static bool CollectTitle(IntPtr hwnd, IntPtr lParam)
    {
        var list = _titles;
        if (list == null) return true;
        try
        {
            if (!NativeMethods.IsWindowVisible(hwnd)) return true;
            var cls = new StringBuilder(64);
            if (NativeMethods.GetClassName(hwnd, cls, cls.Capacity) <= 0) return true;
            if (!ExplorerClasses.Contains(cls.ToString())) return true;
            var sb = new StringBuilder(512);
            if (NativeMethods.GetWindowText(hwnd, sb, sb.Capacity) > 0)
                list.Add((sb.ToString(), hwnd));
        }
        catch { }
        return true;
    }

    /// <summary>资源管理器文件夹窗口的（标题, 句柄）列表。</summary>
    public static List<(string Title, IntPtr Hwnd)> ExplorerWindowTitles()
    {
        var list = new List<(string, IntPtr)>(8);
        _titles = list;
        try { NativeMethods.EnumWindows(_titlesProc, IntPtr.Zero); }
        catch { }
        finally { _titles = null; }
        return list;
    }

    /// <summary>点开始按钮：敲一下 Win 键（系统自己弹开始菜单 / 已弹出就收起）。</summary>
    public static void OpenStartMenu()
    {
        try
        {
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch { }
    }

    /// <summary>承载开始菜单 / 搜索面板的系统进程（Win11 25H2 上是 SearchHost）。</summary>
    static readonly HashSet<string> StartHostProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost",
    };

    static readonly Dictionary<uint, string> _pidNames = new();

    static string ProcNameOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            if (_pidNames.TryGetValue(pid, out var cached)) return cached;
            string name;
            using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName;
            if (_pidNames.Count > 256) _pidNames.Clear();
            _pidNames[pid] = name;
            return name;
        }
        catch { return ""; }
    }

    /// <summary>开始菜单（或它的搜索面板）当前是不是开着。</summary>
    public static bool IsStartMenuOpen()
        => StartHostProcs.Contains(ProcNameOfWindow(NativeMethods.GetForegroundWindow()));

    /// <summary>该屏幕坐标上的窗口是不是开始菜单自己的 —— 是的话那一下点击归它。</summary>
    public static bool IsStartMenuWindowAt(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { x = x, y = y });
        return StartHostProcs.Contains(ProcNameOfWindow(hwnd));
    }
}
