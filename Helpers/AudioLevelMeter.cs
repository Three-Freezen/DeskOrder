using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopZones.Helpers;

/// <summary>
/// 系统混音电平（真实响度）采样器 —— 给律动条当"音量包络"用。
///
/// 只读默认输出设备的 <c>IAudioMeterInformation.GetPeakValue()</c>：不做频率分析（那是 P1 的
/// WASAPI 环回 + FFT），但已经足够让条子跟着音乐本身的强弱、鼓点喘气 ——
/// 纯合成正弦永远只是"匀速呼吸"，这是"少了点感觉"的主因。
///
/// 实现要点：
/// - 独立后台线程（MTA）跑 16ms 采样，COM 对象只在这一个线程上用，不碰 UI 线程；
/// - 每 10 秒重新取一次默认设备，用户换耳机/切声卡时能自动跟上（不释放旧对象直到新对象拿到）；
/// - 任何异常都只让电平归零并降频重试，绝不影响界面。
/// </summary>
public static class AudioLevelMeter
{
    private const int SampleIntervalMs = 16;
    private const int DeviceRefreshMs = 10_000;

    private static Thread? _thread;
    private static volatile bool _running;
    private static volatile float _level;

    /// <summary>最近一次峰值电平，0..1。没启动 / 采不到时是 0。</summary>
    public static float Level => _level;

    /// <summary>幂等启动。</summary>
    public static void EnsureStarted()
    {
        if (_running) return;
        _running = true;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AudioLevelMeter",
        };
        try { thread.SetApartmentState(ApartmentState.MTA); } catch { }
        _thread = thread;
        try { thread.Start(); }
        catch (Exception ex)
        {
            _running = false;
            _thread = null;
            DzTrace.Log($"[AudioLevelMeter] 线程启动失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>进程退出时停采样（不调也不会阻塞退出：线程是后台线程）。</summary>
    public static void Shutdown()
    {
        _running = false;
        _thread = null;
    }

    private static void Loop()
    {
        // 后台线程自己初始化 COM（MTA），避免依赖线程池的隐式初始化
        bool comReady = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded) >= 0;
        IMMDeviceEnumerator? enumerator = null;
        IAudioMeterInformation? meter = null;
        long lastAcquire = 0;

        try
        {
            while (_running)
            {
                long now = Environment.TickCount64;
                if (meter == null || now - lastAcquire > DeviceRefreshMs)
                {
                    var fresh = TryAcquireMeter(ref enumerator);
                    if (fresh != null)
                    {
                        if (!ReferenceEquals(fresh, meter) && meter != null)
                        {
                            try { Marshal.ReleaseComObject(meter); } catch { }
                        }
                        meter = fresh;
                        lastAcquire = now;
                    }
                    else if (meter == null)
                    {
                        _level = 0f;
                        Thread.Sleep(250);       // 还没有输出设备：降频重试
                        continue;
                    }
                }

                try
                {
                    int hr = meter!.GetPeakValue(out float peak);
                    _level = hr == 0 ? Math.Clamp(peak, 0f, 1f) : 0f;
                }
                catch
                {
                    _level = 0f;
                    meter = null;               // 设备失效 → 下一轮重新取
                }
                Thread.Sleep(SampleIntervalMs);
            }
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[AudioLevelMeter] 采样循环异常: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            _level = 0f;
            if (meter != null) { try { Marshal.ReleaseComObject(meter); } catch { } }
            // 枚举器是 CoreAudioInterop 里共享的那一份，不释放（会打断别的模块）。
            if (comReady) CoUninitialize();
        }
    }

    private static IAudioMeterInformation? TryAcquireMeter(ref IMMDeviceEnumerator? enumerator)
    {
        try
        {
            // 只能通过 CoreAudioInterop 拿：自己 new [ComImport] 类会撞 RCW 定型，
            // 让别的音频模块（兜底探针 / 频谱采集）从此永久抛 InvalidCastException。
            enumerator ??= CoreAudioInterop.GetEnumerator<IMMDeviceEnumerator>();
            if (enumerator == null) return null;

            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out var device) != 0
                || device == null)
                return null;

            try
            {
                var iid = typeof(IAudioMeterInformation).GUID;
                if (device.Activate(ref iid, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out var obj) != 0
                    || obj is not IAudioMeterInformation info)
                    return null;
                return info;
            }
            finally
            {
                try { Marshal.ReleaseComObject(device); } catch { }
            }
        }
        catch (Exception ex)
        {
            DzTrace.Log($"[AudioLevelMeter] 取电平表失败: {ex.GetType().Name} {ex.Message}");
            enumerator = null;
            return null;
        }
    }

    // ── 互通 ──

    private const uint CoInitMultithreaded = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    // 设备枚举器只在 CoreAudioInterop 里创建（见那里的文件头：三份同名 [ComImport] 类
    // 会让 RCW 提前定型，其它模块拿去转换时抛 InvalidCastException）。

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
