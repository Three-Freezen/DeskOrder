using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopZones.Helpers;

/// <summary>
/// 「没有 SMTC 会话的播放器」兜底识别（网易云音乐、QQ 音乐、酷狗等）。
///
/// 背景：这些播放器没有向系统注册媒体会话，<c>GlobalSystemMediaTransportControlsSessionManager</c>
/// 里根本看不到它们（实测网易云 3.1.27：CoreAudio 在出声，SMTC 会话数 0），
/// 所以只能换两条更底层的路子：
///
/// 1. 出声判断 —— CoreAudio 枚举默认输出设备上的音频会话，逐会话 QueryInterface
///    拿到 <c>IAudioMeterInformation</c> 读峰值：峰值 &gt; 0 才算"真的在响"。
///    （只靠状态不行：实测播放器暂停后会话状态有时还是 Active。）
/// 2. 曲目判断 —— 播放器惯例把当前曲目写进窗口标题（"歌名 - 歌手"，
///    实测网易云 OrpheusBrowserHost 窗口就是这样）。
///
/// 只在 SMTC 没有"正在播放"的会话时才走这条路，所以不会抢浏览器 / 系统播放器的活。
/// </summary>
public static class AudioNowPlayingProbe
{
    /// <summary>一个候选播放器进程的快照。</summary>
    public sealed class Snapshot
    {
        public int Pid;
        public string ProcessName = "";
        public string AppName = "";
        public string Title = "";
        public string Artist = "";
        public bool Audible;
        public bool HasSession;
        public IntPtr Window;

        /// <summary>窗口标题解析出了"歌名 - 歌手"才算可用。</summary>
        public bool HasTrack => Title.Length > 0;
    }

    // 峰值低于这个值就当没声音（约 -70dB，只挡住纯底噪）
    private const float AudibleThreshold = 0.0003f;

    /// <summary>上一次把"枚举会话失败"写进日志的时间（TickCount64），用于节流。</summary>
    private static long _lastFailureLogMs;

    /// <summary>这些进程常年"有音频会话"，但它们不是播放器（壁纸引擎会一直挂一条活跃会话）。
    /// 直接排除，免得"识别到壁纸引擎在播放"。
    /// 注意只排除确认不是播放器的系统组件；Wallpaper Engine 拖到托盘后没有可见窗口，
    /// 下面还有一道"应用名兜底必须带可见窗口"的关。</summary>
    private static readonly HashSet<string> NotPlayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper32", "wallpaper64", "explorer", "searchhost", "textinputhost",
        "startmenuexperiencehost", "shellexperiencehost", "applicationframehost",
        "lockapp", "dwm", "audiodg", "svchost", "runtimebroker", "taskhostw",
        "sihost", "ctfmon", "conhost", "wudfhost", "systemsettings",
    };

    // ── 对外 ──

    /// <summary>一轮扫描的结果。</summary>
    public sealed class ScanResult
    {
        /// <summary>能认出歌名（最好）或至少认出应用名的那个播放器；认不出就是 null。</summary>
        public Snapshot? Best;

        /// <summary>正在出声、且不在系统组件黑名单里的进程名（用来判断"有声音但这声音不是
        /// SMTC 那条会话发出来的"）。空 = 没有可归因的第三方声音。</summary>
        public readonly List<string> AudibleProcesses = new();

        /// <summary>整机可听度：所有活动输出设备主音量表的峰值（0..1）。
        ///
        /// 和 <see cref="AudibleProcesses"/> 的区别是**不需要把声音归因给某个进程**：
        /// 子进程混音、被排除的系统组件、拿不到会话的应用都可能让进程列表是空的，
        /// 但设备表照样有读数。上层用它兜住"确实有声音在放"这个事实 ——
        /// 认不出是谁的时候也要让律动条/唱片转起来。</summary>
        public float SystemPeak;

        /// <summary>这一轮扫描**根本没跑起来**（CoreAudio 不可用 / 枚举抛异常）。
        /// 上层要能区分"扫了，没人出声"和"没扫成" —— 否则一个哑掉的探针看起来
        /// 和"真没声音"一模一样，兜底失效也查不出来。</summary>
        public bool Failed;

        /// <summary>失败原因（<see cref="Failed"/> 时写给诊断日志用）。</summary>
        public string Failure = "";
    }

    /// <summary>扫描一轮：谁在出声 + 能认出谁。慢（几毫秒级）；调用方按 1s 轮询即可。
    /// <paramref name="trace"/> 非空时把每一步判断写进去，便于排查"为什么没识别到"。</summary>
    public static ScanResult Scan(ICollection<string>? trace = null)
    {
        var result = ScanInternal(preferredPid: 0, wantAudible: true, trace, out var sessions);
        return result;
    }

    /// <summary>找正在出声、且窗口标题能解析出曲目的播放器（认不出歌名时可能只有应用名）。</summary>
    public static Snapshot? FindPlaying(ICollection<string>? trace = null)
        => ScanInternal(preferredPid: 0, wantAudible: true, trace, out _).Best;

    /// <summary>找 <paramref name="pid"/> 这个进程（通常来自"上一次正在播放"），
    /// 即使此刻没声音也返回 —— 用来区分"暂停"和"没媒体"。</summary>
    public static Snapshot? FindByPid(int pid, ICollection<string>? trace = null)
        => pid <= 0 ? null : ScanInternal(pid, wantAudible: false, trace, out _).Best;

    // ── 主流程 ──

    private static ScanResult ScanInternal(
        int preferredPid, bool wantAudible, ICollection<string>? trace, out List<SessionInfo> sessions)
    {
        var result = new ScanResult();

        try { sessions = EnumerateSessions(trace, out result.SystemPeak); }
        catch (Exception ex)
        {
            // 这里每轮（1s）都会进来一次：日志做节流，避免刷屏。
            result.Failed = true;
            result.Failure = $"{ex.GetType().Name} {ex.Message}";
            long now = Environment.TickCount64;
            if (now - _lastFailureLogMs > 30_000)
            {
                _lastFailureLogMs = now;
                var hint = ProcessIntegrity.RestrictionHint;
                DzTrace.Always($"[AudioProbe] 枚举音频会话失败: {ex.GetType().Name} {ex.Message}"
                               + (hint.Length > 0 ? "；" + hint : ""));
            }
            trace?.Add("枚举音频会话失败: " + ex.Message);
            sessions = new List<SessionInfo>();
            return result;        }
        if (sessions.Count == 0)
        {
            trace?.Add("没有活动的输出设备会话");
            return result;
        }

        int self = Environment.ProcessId;
        var candidates = sessions
            .Where(s => s.Pid > 0 && s.Pid != self)
            .Where(s => preferredPid > 0 || !NotPlayers.Contains(SafeProcessName(s.Pid)))
            .Where(s => preferredPid > 0 ? s.Pid == preferredPid : !wantAudible || s.Audible)
            .OrderByDescending(s => s.Peak)
            .ToList();

        // 记下"到底谁在出声"（已排除壁纸引擎之类），供上层判断 SMTC 是否可信
        foreach (var s in candidates.Where(c => c.Audible))
        {
            var name = SafeProcessName(s.Pid);
            if (name.Length > 0 && !result.AudibleProcesses.Contains(name, StringComparer.OrdinalIgnoreCase))
                result.AudibleProcesses.Add(name);
        }
        trace?.Add($"在出声: [{string.Join(",", result.AudibleProcesses)}] 设备峰值={result.SystemPeak:0.0000}");

        if (candidates.Count == 0)
        {
            trace?.Add(wantAudible ? "没有进程在出声" : $"进程 {preferredPid} 没有音频会话");
            return result;
        }

        Snapshot? appOnly = null;
        foreach (var s in candidates)
        {
            var windows = DescribeProcess(s.Pid);
            if (windows == null)
            {
                trace?.Add($"  pid={s.Pid} 进程已退出");
                continue;
            }
            var (title, artist, hwnd) = ParseWindows(windows, s.Pid);
            if (title.Length == 0)
            {
                trace?.Add($"  pid={s.Pid} 窗口标题解析不出曲目（窗口 {windows.Count} 个）");
                var appName = DescribeApp(s.Pid, windows);
                if (appName.Length > 0)
                {
                    var snapshot = new Snapshot
                    {
                        Pid = s.Pid, ProcessName = SafeProcessName(s.Pid), AppName = appName,
                        Audible = s.Audible, HasSession = true, Window = hwnd,
                    };
                    // 调用方点名要看这个 pid（暂停态）→ 直接给；
                    // 只是想找"谁在播"→ 先记下来，后面若没有能解析出曲目的候选再兜这个。
                    // 只要它有个带标题的窗口（最小化到托盘也算）就认，否则后台音效 / 壁纸也会被当成播放器。
                    if (preferredPid > 0) { result.Best = snapshot; return result; }
                    if (HasNamedWindow(windows)) appOnly ??= snapshot;
                }
                continue;
            }
            trace?.Add($"  pid={s.Pid} 命中: '{title}' / '{artist}'");
            result.Best = new Snapshot
            {
                Pid = s.Pid,
                ProcessName = SafeProcessName(s.Pid),
                AppName = DescribeApp(s.Pid, windows),
                Title = title,
                Artist = artist,
                Audible = s.Audible,
                HasSession = true,
                Window = hwnd,
            };
            return result;
        }
        result.Best = appOnly;
        return result;
    }

    // ── CoreAudio ──

    private sealed class SessionInfo
    {
        public int Pid;
        public int State;
        public float Peak;
        public bool Audible => Peak >= AudibleThreshold;
    }

    /// <summary>所有**活动**渲染设备上的会话（含暂停的，带峰值；同一进程取最大峰值）。
    ///
    /// 不只枚举默认设备：用户把音乐切到耳机 / 第二块声卡 / 虚拟声卡时，默认设备上一条会话都没有，
    /// 只盯默认设备就会"彻底识别不出来"。
    ///
    /// 顺带读每个设备自己的主音量表峰值（<paramref name="systemPeak"/>）：这是"整机确实有声音"
    /// 的唯一可靠读数 —— 会话枚举失败 / 声音归因不到进程时，上层还能靠它让动画跑起来。</summary>
    private static List<SessionInfo> EnumerateSessions(
        ICollection<string>? trace, out float systemPeak)
    {
        systemPeak = 0;
        var merged = new Dictionary<int, SessionInfo>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            // 只能通过 CoreAudioInterop 拿枚举器：三个音频模块各自 new 自己的
            // [ComImport] 类会撞 RCW 定型（详见 CoreAudioInterop 文件头的事故说明）。
            enumerator = CoreAudioInterop.GetEnumerator<IMMDeviceEnumerator>();
            if (enumerator == null)
                throw new InvalidOperationException("CoreAudio 设备枚举器不可用");

            // eRender = 0, DEVICE_STATE_ACTIVE = 0x1
            int hr = enumerator.EnumAudioEndpoints(0, 0x1, out devices);
            if (hr != 0 || devices == null)
            {
                trace?.Add($"EnumAudioEndpoints 失败 hr=0x{hr:X8}");
                return new List<SessionInfo>();
            }

            hr = devices.GetCount(out int deviceCount);
            if (hr != 0)
            {
                trace?.Add($"设备数读取失败 hr=0x{hr:X8}");
                return new List<SessionInfo>();
            }
            trace?.Add($"活动输出设备 = {deviceCount}");

            for (int d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0 || device == null) continue;
                try
                {
                    ReadDevicePeak(device, ref systemPeak);
                    CollectForDevice(device, merged, trace);
                }
                finally
                {
                    try { Marshal.ReleaseComObject(device); } catch { }
                }
            }
        }
        finally
        {
            if (devices != null) Marshal.ReleaseComObject(devices);
            // 枚举器是进程内共享的那一份（CoreAudioInterop），**不能**释放：
            // ReleaseComObject 会把别的模块手里的接口一起打断。
        }
        return new List<SessionInfo>(merged.Values);
    }

    /// <summary>设备主音量表峰值（0..1）。失败静默 —— 这项只是"有声音"的旁证，不能因为它中断扫描。</summary>
    private static void ReadDevicePeak(IMMDevice device, ref float best)
    {
        object? meterObj = null;
        try
        {
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out meterObj) != 0
                || meterObj is not IAudioMeterInformation meter)
                return;
            if (meter.GetPeakValue(out float peak) != 0) return;
            if (peak < AudibleThreshold)
            {
                // 峰值是瞬时值，可能正好落在过零点上
                System.Threading.Thread.Sleep(8);
                if (meter.GetPeakValue(out float again) == 0) peak = again;
            }
            if (peak > best) best = peak;
        }
        catch
        {
            // 设备不支持电平表 / 独占模式 — 忽略
        }
        finally
        {
            if (meterObj != null) try { Marshal.ReleaseComObject(meterObj); } catch { }
        }
    }

    private static void CollectForDevice(
        IMMDevice device, Dictionary<int, SessionInfo> merged, ICollection<string>? trace)
    {
        object? managerObj = null;
        IAudioSessionEnumerator? sessionEnum = null;
        try
        {
            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out managerObj) != 0
                || managerObj is not IAudioSessionManager2 manager)
                return;
            if (manager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null) return;
            if (sessionEnum.GetCount(out int count) != 0) return;

            for (int i = 0; i < count; i++)
            {
                if (sessionEnum.GetSession(i, out var control) != 0 || control == null) continue;
                try
                {
                    if (control is not IAudioSessionControl2 c2) continue;
                    if (c2.GetProcessId(out int pid) != 0 || pid <= 0) continue;
                    if (c2.IsSystemSoundsSession() == 0) continue;   // 0 = 是系统提示音
                    control.GetState(out int state);

                    // 只给"活跃"的会话量峰值（0 = Inactive，1 = Active）：
                    // 每条会话都要读电平，别为暂停的会话白等。
                    float peak = 0;
                    if (state == 1 && control is IAudioMeterInformation meter)
                    {
                        meter.GetPeakValue(out peak);
                        if (peak < AudibleThreshold)
                        {
                            // 峰值是瞬时的，可能正好落在过零点上；隔一帧再确认一次
                            System.Threading.Thread.Sleep(10);
                            meter.GetPeakValue(out peak);
                        }
                    }

                    if (merged.TryGetValue(pid, out var existing))
                    {
                        // 同一进程可能同时在多个设备上有会话，取更响的那条
                        if (peak > existing.Peak) existing.Peak = peak;
                        if (state == 1) existing.State = 1;
                        continue;
                    }
                    merged[pid] = new SessionInfo { Pid = pid, State = state, Peak = peak };
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
        }
        finally
        {
            if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
            if (managerObj != null) Marshal.ReleaseComObject(managerObj);
        }
    }

    // ── 窗口标题 → 曲目 ──

    private sealed class WindowInfo
    {
        public IntPtr Hwnd;
        public string Class = "";
        public string Caption = "";
        public bool Visible;
    }

    private static List<WindowInfo>? DescribeProcess(int pid)
    {
        try { _ = Process.GetProcessById(pid); }
        catch { return null; }

        var list = new List<WindowInfo>();
        uint target = (uint)pid;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            DesktopHook.GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner != target) return true;
            int len = GetWindowTextLengthW(hwnd);
            var sb = new StringBuilder(Math.Max(4, len + 2));
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var cls = new StringBuilder(256);
            GetClassNameW(hwnd, cls, cls.Capacity);
            list.Add(new WindowInfo
            {
                Hwnd = hwnd,
                Class = cls.ToString(),
                Caption = sb.ToString().Trim(),
                Visible = DesktopHook.IsWindowVisible(hwnd),
            });
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>窗口列表 →（歌名, 歌手, 窗口句柄）。优先可见窗口、优先带 " - " 的标题。</summary>
    private static (string Title, string Artist, IntPtr Hwnd) ParseWindows(List<WindowInfo> windows, int pid)
    {
        var ordered = windows
            .Where(w => w.Caption.Length >= 3)
            .Where(w => !IsNoiseCaption(w.Caption))
            .OrderByDescending(w => w.Visible)
            .ThenByDescending(w => w.Caption.Contains(" - "))
            .ToList();

        var appDesc = SafeFileDescription(pid);
        foreach (var w in ordered)
        {
            var (t, a) = SplitTrack(w.Caption, appDesc);
            if (t.Length > 0) return (t, a, w.Hwnd);
        }
        return ("", "", ordered.Count > 0 ? ordered[0].Hwnd : IntPtr.Zero);
    }

    /// <summary>"歌名 - 歌手" 拆分。先去掉结尾的应用名（"歌名 - 歌手 - 网易云音乐"）。</summary>
    private static (string Title, string Artist) SplitTrack(string caption, string appDesc)
    {
        var parts = caption.Split(" - ", StringSplitOptions.None)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
        if (parts.Count >= 3 && appDesc.Length > 0 && parts[^1].Contains(appDesc, StringComparison.OrdinalIgnoreCase))
            parts.RemoveAt(parts.Count - 1);
        if (parts.Count < 2) return ("", "");          // 只有应用名 → 不是曲目

        string artist = parts[^1];
        string title = string.Join(" - ", parts.Take(parts.Count - 1));
        if (title.Length > 160) title = title[..160];
        if (artist.Length > 120) artist = artist[..120];
        return (title, artist);
    }

    private static bool IsNoiseCaption(string caption)
    {
        // IME / 输入法 / GDI+ 之类的隐藏辅助窗口标题
        if (caption.Equals("Default IME", StringComparison.OrdinalIgnoreCase)) return true;
        if (caption.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase)) return true;
        if (caption.StartsWith("GDI+ Window", StringComparison.OrdinalIgnoreCase)) return true;
        if (caption.Contains(" Hook Window Class", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>这个进程有没有带标题的窗口（最小化到托盘也算）—— 用来区分真播放器与后台音效。</summary>
    private static bool HasNamedWindow(List<WindowInfo> windows)
        => windows.Any(w => w.Caption.Length >= 3 && !IsNoiseCaption(w.Caption));

    private static string DescribeApp(int pid, List<WindowInfo>? windows)
    {
        var desc = SafeFileDescription(pid);
        if (desc.Length > 0) return desc;
        var name = SafeProcessName(pid);
        return name.Length > 0 ? name : "";
    }

    private static string SafeProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return ""; }
    }

    /// <summary>进程文件描述（"网易云音乐" / "QQ音乐" …），读不到就退回进程名。</summary>
    private static string SafeFileDescription(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var desc = p.MainModule?.FileVersionInfo?.FileDescription;
            return string.IsNullOrWhiteSpace(desc) ? "" : desc.Trim();
        }
        catch { return ""; }
    }

    // ── 媒体键（兜底控制）──
    //
    // 没有 SMTC 会话的播放器只能靠系统媒体键控制。这里用 keybd_event 注入而不是
    // WM_APPCOMMAND：前者走 Windows 全局媒体键通道，播放器自己的全局热键钩子能收到
    // （实测往网易云窗口发 WM_APPCOMMAND 没反应）。keybd_event 虽是老 API，
    // 但比手搓 INPUT 联合体的结构体布局稳妥得多。

    private const byte VkMediaNextTrack = 0xB0;
    private const byte VkMediaPrevTrack = 0xB1;
    private const byte VkMediaPlayPause = 0xB3;

    public static void TogglePlayPause() => SendMediaKey(VkMediaPlayPause);
    public static void SkipNext() => SendMediaKey(VkMediaNextTrack);
    public static void SkipPrevious() => SendMediaKey(VkMediaPrevTrack);

    private static void SendMediaKey(byte vk)
    {
        try
        {
            const uint extended = 0x0001;
            const uint keyUp = 0x0002;
            keybd_event(vk, 0, extended, UIntPtr.Zero);
            keybd_event(vk, 0, extended | keyUp, UIntPtr.Zero);
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[AudioProbe] 发送媒体键失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    // ── 互通 ──
    //
    // 设备枚举器（MMDeviceEnumerator）**不在这里创建**：见 CoreAudioInterop。
    // 曾经这里有一个 [ComImport] class MMDeviceEnumeratorComObject，与
    // AudioLevelMeter / AudioSpectrumCapture 里的同名类撞 RCW 定型，导致整条兜底链路
    // 每秒抛一次 InvalidCastException（发布版日志里只剩一行"枚举音频会话失败"）。

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int count);
        [PreserveSig]
        int Item(int index, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]        int GetState(out int state);
        [PreserveSig]        int GetDisplayName(out IntPtr name);
        [PreserveSig]        int SetDisplayName(string name, ref Guid eventContext);
        [PreserveSig]        int GetIconPath(out IntPtr path);
        [PreserveSig]        int SetIconPath(string path, ref Guid eventContext);
        [PreserveSig]        int GetGroupingParam(out Guid groupingParam);
        [PreserveSig]        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig]        int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig]        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]        int GetState(out int state);
        [PreserveSig]        int GetDisplayName(out IntPtr name);
        [PreserveSig]        int SetDisplayName(string name, ref Guid eventContext);
        [PreserveSig]        int GetIconPath(out IntPtr path);
        [PreserveSig]        int SetIconPath(string path, ref Guid eventContext);
        [PreserveSig]        int GetGroupingParam(out Guid groupingParam);
        [PreserveSig]        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        [PreserveSig]        int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig]        int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig]        int GetSessionIdentifier(out IntPtr id);
        [PreserveSig]        int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig]        int GetProcessId(out int pid);
        [PreserveSig]        int IsSystemSoundsSession();
        [PreserveSig]        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]        int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags, out IntPtr sessionControl);
        [PreserveSig]        int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags, out IntPtr audioVolume);
        [PreserveSig]        int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]        int GetCount(out int sessionCount);
        [PreserveSig]        int GetSession(int index, out IAudioSessionControl? session);
    }

    /// <summary>会话级电平表：对 <see cref="IAudioSessionControl"/> 做 QueryInterface 就能拿到。</summary>
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]        int GetPeakValue(out float peak);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}


