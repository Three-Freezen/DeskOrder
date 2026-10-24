using System;

namespace DesktopZones.Helpers;

/// <summary>
/// CoreAudio `MMDeviceEnumerator` 的**唯一**创建入口。
///
/// 为什么必须有这个文件（2026-10-24 发布版实测事故，日志为证）：
///
/// `MMDeviceEnumerator`（CLSID BCDE0395-E52F-467C-8E3D-C4579291692E）原先在
/// <c>AudioLevelMeter</c> / <c>AudioSpectrumCapture</c> / <c>AudioNowPlayingProbe</c>
/// 三个文件里各自用一个 <c>[ComImport] class MMDeviceEnumeratorComObject</c> 去 new。
/// 三个声明是**三个不同的托管类型**，却指向同一个 COM 类；而 CLR 的 RCW 缓存按
/// COM 身份（IUnknown）而不是按托管类型缓存 —— 谁先创建，那份 RCW 就"定型"成谁的类型。
/// 之后另一处再拿自己的类类型去 new，得到的是**已经定型成别的类型的 RCW**，
/// 类类型转换失败，抛出：
/// <code>
/// InvalidCastException: Unable to cast object of type 'MMDeviceEnumeratorComObject'
///                       to type 'MMDeviceEnumeratorComObject'.
/// </code>
/// （两边类名一模一样，因为都是嵌套在别的类里的私有类型，<c>ToString()</c> 不带命名空间。）
///
/// 后果：<c>MediaPlayerWidget</c> 先启动 <see cref="AudioLevelMeter"/>，它占住 RCW；
/// 于是「兜底识别」探针每秒都抛这个异常 → 设备峰值恒为 0、在出声进程恒为空 →
/// 底层兜底（兜底应用 / 有声无曲目 / 暂停保持）**永远走不到**；
/// P1 真频谱（<see cref="AudioSpectrumCapture"/>）也一起哑掉，律动条退化成模拟。
/// 而 Release 构建里那两个模块只有 <c>DzTrace.Log</c>（DEBUG 才写），所以现场只剩一行
/// <c>[AudioProbe] 枚举音频会话失败: InvalidCastException</c>，看不出真正原因。
///
/// 修法：进程内只创建一个 <c>__ComObject</c>（持强引用、永不 Release），
/// 各调用点用 <c>as</c> 各自 QI 自己的接口类型（接口按 IID 匹配，多份声明互不冲突）。
/// </summary>
internal static class CoreAudioInterop
{
    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly object Gate = new();

    /// <summary>进程内唯一的设备枚举器 RCW。**不要**对它调 ReleaseComObject ——
    /// 多个调用点共享同一份 RCW，释放会把别人手里的接口一起打断。</summary>
    private static object? _core;

    /// <summary>失败日志节流（1s 轮询反复失败时别刷屏）。</summary>
    private static long _lastFailureLogMs;

    /// <summary>
    /// 取进程内唯一的设备枚举器，并 QI 成调用方自己的接口类型。
    /// 失败返回 null（调用方按"这次拿不到设备"处理，别抛）。
    /// </summary>
    public static T? GetEnumerator<T>() where T : class
    {
        try
        {
            object? core;
            lock (Gate)
            {
                if (_core == null)
                {
                    // 注意：不能用 [ComImport] 类 + new（见文件头）。CLSID 激活拿 __ComObject，
                    // 各调用点靠接口的 IID 做 QI，谁都不用"类类型"。
                    var type = Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid, throwOnError: false);
                    if (type == null)
                    {
                        LogFailure("取不到 MMDeviceEnumerator 的 COM 类型（CLSID 未注册？）");
                        return null;
                    }
                    _core = Activator.CreateInstance(type);
                }
                core = _core;
            }

            if (core == null) return null;

            if (core as T is { } typed) return typed;

            // QI 失败 = 这份 RCW 已经被别处 ReleaseComObject 打断了 → 丢掉重建。
            lock (Gate) { _core = null; }
            LogFailure("设备枚举器 RCW 已失效（被释放），下一轮重建");
            return null;
        }
        catch (Exception ex)
        {
            lock (Gate) { _core = null; }
            LogFailure($"{ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>把创建/激活失败写进日志（Release 也写，30 秒节流）。</summary>
    private static void LogFailure(string detail)
    {
        long now = Environment.TickCount64;
        if (now - _lastFailureLogMs < 30_000) return;
        _lastFailureLogMs = now;
        DzTrace.Always("[CoreAudio] 设备枚举器不可用: " + detail);
    }
}
