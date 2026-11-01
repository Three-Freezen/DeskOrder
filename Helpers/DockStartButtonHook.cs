using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using DesktopZones.Services;

namespace DesktopZones.Helpers;

/// <summary>
/// 快捷栏「开始菜单按钮」的鼠标兜底 —— 低层鼠标钩子（WH_MOUSE_LL）。
///
/// 为什么需要它：开始菜单一弹出来就以 <c>Modality</c>-ish 的方式抓输入，落在
/// 「桌面层」窗口上的那一下**经常根本不会派发给我们**（实测：连点 5 下，窗口一次都没
/// 收到，菜单也没关）—— 用户看到的就是「点个几下就失效了」。钩子能看见每一次真实点击，
/// 所以这里兜住那种被吞掉的点击。
///
/// 三条纪律（这是全局钩子，出问题会拖慢整个系统的鼠标）：
/// ① 回调里只做坐标比较 + <c>Dispatcher.BeginInvoke</c>，绝不做 IO / 枚举 / 加锁；
/// ② 命中判定用**缓存的屏幕矩形**（由各快捷栏窗口自己提供），不查可视树；
/// ③ 点击点上的窗口如果本来就属于开始菜单（SearchHost 等），一律放行 —— 那一下是
///    开始菜单自己的，我们抢过来反而会「菜单关了又开」。
/// 另外：正常路径（WPF 的 PreviewMouseLeftButtonDown）仍然保留，两边共用
/// <see cref="TryConsume"/> 去重（120ms 内只算一次）；钩子万一被系统摘掉，按钮照旧能用。
/// </summary>
public static class DockStartButtonHook
{
    sealed class Entry
    {
        public IntPtr Hwnd;
        public Func<Rect> ScreenRect = () => Rect.Empty;
        public Action Toggle = () => { };
    }

    static IntPtr _hook;
    static NativeMethods.LowLevelMouseProc? _proc;      // 必须留引用，否则委托被回收 → 钩子失效
    static readonly List<Entry> _entries = new();

    static long _lastConsumedTick = Environment.TickCount64 - 1000;
    /// <summary>同一次物理点击最多触发一次（钩子 + WPF 两条路都走这里）。</summary>
    const long DebounceMs = 120;

    /// <summary>登记一条开始按钮（hwnd 为它所属的快捷栏窗口）。重复登记会覆盖旧的。</summary>
    public static void Register(IntPtr hwnd, Func<Rect> screenRect, Action toggle)
    {
        if (hwnd == IntPtr.Zero) return;
        _entries.RemoveAll(e => e.Hwnd == hwnd);
        _entries.Add(new Entry { Hwnd = hwnd, ScreenRect = screenRect, Toggle = toggle });
        EnsureInstalled();
    }

    public static void Unregister(IntPtr hwnd)
    {
        _entries.RemoveAll(e => e.Hwnd == hwnd);
        if (_entries.Count == 0) Uninstall();
    }

    /// <summary>
    /// 去重闸门：返回 true = 这一下归你处理。钩子和 WPF 点击路径都调它，
    /// 同一击的第二次调用会返回 false，所以「开→关」不会被连点两下抵消。
    /// </summary>
    public static bool TryConsume()
    {
        long now = Environment.TickCount64;
        if (now - _lastConsumedTick < DebounceMs) return false;
        _lastConsumedTick = now;
        return true;
    }

    static void EnsureInstalled()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookProc;
        try
        {
            // WH_MOUSE_LL 不需要模块句柄（传当前模块即可），回调在本线程（UI 线程）上跑。
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc,
                NativeMethods.GetModuleHandle(null), 0);
            DzTrace.Log($"[DockStartButtonHook] install ok={_hook != IntPtr.Zero} err={Marshal.GetLastWin32Error()}");
            if (_hook == IntPtr.Zero)
                DzTrace.Log("[DockStartButtonHook] SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            _hook = IntPtr.Zero;
            DzTrace.Log("[DockStartButtonHook] install failed: " + ex.Message);
        }
    }

    static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        try { NativeMethods.UnhookWindowsHookEx(_hook); } catch { }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _entries.Count > 0 &&
                (wParam == (IntPtr)NativeMethods.WM_LBUTTONDOWN || wParam == (IntPtr)NativeMethods.WM_LBUTTONDBLCLK))
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.pt.x, info.pt.y);

                List<Entry>? hits = null;
                for (int i = 0; i < _entries.Count; i++)
                {
                    Rect r;
                    try { r = _entries[i].ScreenRect(); }
                    catch { continue; }
                    if (r.IsEmpty || r.Width <= 0 || !r.Contains(pt)) continue;
                    (hits ??= new List<Entry>()).Add(_entries[i]);
                }
                if (hits == null) return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
                DzTrace.Log($"[DockStartButtonHook] hit {hits.Count} at ({info.pt.x},{info.pt.y})");

                // 这一点本来就被开始菜单自己占着（菜单盖在快捷栏上）→ 让给菜单处理。
                if (AppActivityService.IsStartMenuWindowAt(info.pt.x, info.pt.y))
                    return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);

                // 注意：这里**不要**再去重。去重闸门只有 DockStartClick 里那一处 ——
                // 钩子先吞一次，等 BeginInvoke 里真正要敲键时又会被闸门挡掉（实测过一次：
                // 日志里 hit 之后紧跟两个 pass=False，菜单永远不开）。
                foreach (var h in hits)
                {
                    var toggle = h.Toggle;
                    // 别在钩子回调里直接注入按键：扔回消息队列，回调立刻返回。
                    var app = Application.Current;
                    if (app != null) app.Dispatcher.BeginInvoke(new Action(() => { try { toggle(); } catch { } }));
                    else try { toggle(); } catch { }
                }
            }
        }
        catch { /* 钩子里绝不抛 */ }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
