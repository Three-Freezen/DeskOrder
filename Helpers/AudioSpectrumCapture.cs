using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace DesktopZones.Helpers;

/// <summary>
/// P1 真频谱：WASAPI 环回采集 + 手写 FFT，输出 0..1 的分频电平给律动条。
///
/// 为什么用环回：shared 模式的 loopback 抓的是"默认渲染设备上正在放的所有声音"，
/// 所以**任何**播放器（网易云、游戏、浏览器、系统播放器）都能驱动条子，
/// 不依赖媒体会话、也不依赖我们那套窗口标题兜底。
///
/// 实现要点：
/// - 采集跑在独立 MTA 后台线程（`IAudioClient` loopback + 轮询 `GetNextPacketSize`）；
/// - 采样率/格式跟随系统混音格式（一般是 48kHz / 32bit float），16bit PCM 也能处理；
/// - FFT 4096 点、Hann 窗、hop 1024（≈21ms 一次更新），迭代 radix-2，不引第三方包；
/// - 分频按对数（40Hz–16kHz）映射到 `BandCount` 段，取段内峰值，做 dB 归一化 + 高频补偿；
/// - UI 线程每帧只读 `Bands`（不可变快照数组），不做任何等待；
/// - 独占模式 / 没有设备 / 权限问题 → `IsAvailable = false`，调用方自动退回模拟律动。
/// </summary>
public static class AudioSpectrumCapture
{
    private const int FftSize = 4096;                  // 86ms 窗（48kHz）
    private const int HopSize = 1024;                  // ≈21ms 更新一次
    private const float MinFreq = 40f;
    private const float MaxFreq = 16000f;
    private const float FloorDb = -72f;                // 低于这个当静音
    private const float CeilDb = -12f;                 // 到这个就是满格

    private static Thread? _thread;
    private static volatile bool _running;
    private static volatile bool _available;
    private static volatile float[] _bands = Array.Empty<float>();
    private static int _bandCount = 32;

    /// <summary>当前分频电平（0..1），长度 = <see cref="BandCount"/>。没数据时全 0。</summary>
    public static float[] Bands => _bands;

    /// <summary>期望的段数（= 律动条数量）。变化时重算频段边界。</summary>
    public static int BandCount => _bandCount;

    /// <summary>采集是否真的在跑（独占模式 / 无设备时为 false）。</summary>
    public static bool IsAvailable => _available;

    /// <summary>幂等启动；<paramref name="bandCount"/> 变化时只需再调一次。</summary>
    public static void EnsureStarted(int bandCount)
    {
        bandCount = Math.Clamp(bandCount, 1, 100);
        if (bandCount != _bandCount)
        {
            _bandCount = bandCount;
            _bands = new float[bandCount];
        }
        if (_running) return;
        _running = true;
        var thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "AudioSpectrumCapture",
        };
        try { thread.SetApartmentState(ApartmentState.MTA); } catch { }
        _thread = thread;
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            _running = false;
            _thread = null;
            DzTrace.Log($"[Spectrum] 采集线程启动失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        _running = false;
        _available = false;
        _thread = null;
    }

    // ── 采集线程 ──

    private static void Loop()
    {
        bool comReady = CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/) >= 0;
        try
        {
            while (_running)
            {
                try
                {
                    CaptureOnce();
                }
                catch (Exception ex)
                {
                    _available = false;
                    _bands = new float[_bandCount];
                    DzTrace.Log($"[Spectrum] 采集中断，2 秒后重建: {ex.GetType().Name} {ex.Message}");
                    Thread.Sleep(2000);
                }
            }
        }
        finally
        {
            _bands = new float[_bandCount];
            _available = false;
            if (comReady) CoUninitialize();
        }
    }

    /// <summary>打开一次环回流并一直读到出错为止（设备切换 / 独占会抛，由外层重建）。</summary>
    private static void CaptureOnce()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? clientObj = null;
        IAudioClient? client = null;
        IntPtr formatPtr = IntPtr.Zero;
        try
        {
            enumerator = CoreAudioInterop.GetEnumerator<IMMDeviceEnumerator>();
            if (enumerator == null)
            {
                Thread.Sleep(1000);
                return;
            }
            if (enumerator.GetDefaultAudioEndpoint(0 /*eRender*/, 0 /*eConsole*/, out device) != 0
                || device == null)
            {
                Thread.Sleep(1000);
                return;
            }

            var iidClient = typeof(IAudioClient).GUID;
            if (device.Activate(ref iidClient, 1 /*CLSCTX_ALL*/, IntPtr.Zero, out clientObj) != 0
                || clientObj is not IAudioClient audioClient)
            {
                Thread.Sleep(1000);
                return;
            }
            client = audioClient;

            if (client.GetMixFormat(out formatPtr) != 0 || formatPtr == IntPtr.Zero)
            {
                Thread.Sleep(1000);
                return;
            }
            var fmt = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
            if (fmt.nChannels == 0 || fmt.nSamplesPerSec == 0 || fmt.wBitsPerSample == 0)
            {
                Thread.Sleep(1000);
                return;
            }

            // shared + loopback：抓"正在播放的混音"，不写回设备
            const uint shareShared = 0;
            const uint flagLoopback = 0x00020000;
            long bufferDuration = 1_000_000; // 100ms（100ns 单位）
            int hr = client.Initialize(shareShared, flagLoopback, bufferDuration, 0, formatPtr, IntPtr.Zero);
            if (hr != 0)
            {
                // 0x88890008 = AUDCLNT_E_UNSUPPORTED_FORMAT；独占占用时也会失败
                DzTrace.Log($"[Spectrum] IAudioClient.Initialize 失败 hr=0x{hr:X8}");
                Thread.Sleep(2000);
                return;
            }

            var iidCapture = typeof(IAudioCaptureClient).GUID;
            if (client.GetService(ref iidCapture, out var capturePtr) != 0 || capturePtr == IntPtr.Zero)
            {
                Thread.Sleep(1000);
                return;
            }
            var capture = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePtr);
            Marshal.Release(capturePtr);

            var bands = BuildBands(_bandCount, fmt.nSamplesPerSec);
            var bandCountAtStart = _bandCount;
            var analyser = new Analyser(fmt, bands, bandCountAtStart);

            if (client.Start() != 0)
            {
                Thread.Sleep(1000);
                return;
            }
            _available = true;

            while (_running && _bandCount == bandCountAtStart)
            {
                bool got = false;
                while (capture.GetNextPacketSize(out uint packetFrames) == 0 && packetFrames > 0)
                {
                    if (capture.GetBuffer(out IntPtr data, out uint frames, out uint flags,
                            out _, out _) != 0)
                        break;
                    try
                    {
                        got = true;
                        analyser.Feed(data, frames, (flags & 0x2) != 0 /*AUDCLNT_BUFFERFLAGS_SILENT*/);
                    }
                    finally
                    {
                        capture.ReleaseBuffer(frames);
                    }
                }

                if (!got) Thread.Sleep(8);
                if (analyser.TryProcess(out var snapshot)) _bands = snapshot;
            }

            // 条数变了 → 退出本流，外层会按新段数重建
            client.Stop();
            _available = false;
        }
        finally
        {
            if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } }
            if (clientObj != null) { try { Marshal.ReleaseComObject(clientObj); } catch { } }
            if (device != null) { try { Marshal.ReleaseComObject(device); } catch { } }
            // 枚举器是 CoreAudioInterop 里共享的那一份，不释放（会打断别的模块）。
            if (formatPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPtr);
        }
    }

    // ── 分析（喂样本 / 出频段）──

    private sealed class Analyser
    {
        private readonly WAVEFORMATEX _format;
        private readonly (int Lo, int Hi)[] _bands;
        private readonly int _bandCount;
        private readonly float _sampleRate;

        private readonly float[] _window = new float[FftSize];
        private readonly float[] _re = new float[FftSize];
        private readonly float[] _im = new float[FftSize];
        private readonly float[] _cos = new float[FftSize / 2];
        private readonly float[] _sin = new float[FftSize / 2];
        private readonly float[] _ring = new float[FftSize];
        private int _write;
        private int _sinceFft;
        private readonly float[] _smoothed;
        private float[] _bufferFloat = Array.Empty<float>();
        private short[] _bufferShort = Array.Empty<short>();

        public Analyser(WAVEFORMATEX format, (int Lo, int Hi)[] bands, int bandCount)
        {
            _format = format;
            _bands = bands;
            _bandCount = bandCount;
            _sampleRate = format.nSamplesPerSec;
            _smoothed = new float[bandCount];

            for (int i = 0; i < FftSize; i++)
                _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));   // Hann
            for (int i = 0; i < FftSize / 2; i++)
            {
                double a = -2 * Math.PI * i / FftSize;
                _cos[i] = (float)Math.Cos(a);
                _sin[i] = (float)Math.Sin(a);
            }
        }

        public void Feed(IntPtr data, uint frames, bool silent)
        {
            int channels = Math.Max(1, (int)_format.nChannels);
            int need = (int)frames * channels;
            if (need <= 0) return;
            bool isFloat = _format.wBitsPerSample == 32;

            // 批量拷贝再下混：逐样本 Marshal.Read* 会产生大量小对象，48kHz 下 GC 会拖慢 UI 帧
            if (isFloat)
            {
                if (_bufferFloat.Length < need) _bufferFloat = new float[need];
                if (!silent) Marshal.Copy(data, _bufferFloat, 0, need);
                for (int frame = 0; frame < frames; frame++)
                {
                    float sample = 0;
                    if (!silent)
                    {
                        int baseIdx = frame * channels;
                        for (int c = 0; c < channels; c++) sample += _bufferFloat[baseIdx + c];
                        sample /= channels;
                    }
                    Push(sample);
                }
            }
            else
            {
                if (_bufferShort.Length < need) _bufferShort = new short[need];
                if (!silent) Marshal.Copy(data, _bufferShort, 0, need);
                for (int frame = 0; frame < frames; frame++)
                {
                    float sample = 0;
                    if (!silent)
                    {
                        int baseIdx = frame * channels;
                        for (int c = 0; c < channels; c++) sample += _bufferShort[baseIdx + c] / 32768f;
                        sample /= channels;
                    }
                    Push(sample);
                }
            }
        }

        private void Push(float sample)
        {
            _ring[_write] = sample;
            _write = (_write + 1) % FftSize;
            if (++_sinceFft >= HopSize)
            {
                _sinceFft = 0;
                _pendingFft = true;
            }
        }

        private bool _pendingFft;

        /// <summary>攒够一个 hop 就算一次；返回 true 时 <paramref name="snapshot"/> 是新的频段数组。</summary>
        public bool TryProcess(out float[] snapshot)
        {
            snapshot = Array.Empty<float>();
            if (!_pendingFft) return false;
            _pendingFft = false;

            // 环形缓冲按时间顺序展开
            for (int i = 0; i < FftSize; i++)
            {
                int idx = (_write + i) % FftSize;
                _re[i] = _ring[idx] * _window[i];
                _im[i] = 0;
            }
            Fft(_re, _im, _cos, _sin);

            for (int b = 0; b < _bandCount; b++)
            {
                var (lo, hi) = _bands[b];
                float peak = 0;
                for (int k = lo; k <= hi && k < FftSize / 2; k++)
                {
                    float mag = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * 2f / FftSize;
                    if (mag > peak) peak = mag;
                }

                float db = 20f * MathF.Log10(peak + 1e-9f);
                float norm = (db - FloorDb) / (CeilDb - FloorDb);
                // 高频天然更弱：按位置补 0→6dB 的倾斜，画面才不会被低频全占
                norm += 0.10f * b / Math.Max(1, _bandCount - 1);
                norm = Math.Clamp(norm, 0f, 1f);

                // 频段间轻微平滑，避免相邻条乱跳（时间上的 attack/release 由 SpectrumBars 负责）
                _smoothed[b] = b == 0 ? norm : 0.75f * norm + 0.25f * _smoothed[b - 1];
            }

            snapshot = (float[])_smoothed.Clone();
            return true;
        }
    }

    /// <summary>对数分频：每段 = [lo, hi] 两个 FFT bin（段宽不足 1 bin 时按 1 bin 算）。</summary>
    private static (int Lo, int Hi)[] BuildBands(int bandCount, int sampleRate)
    {
        var result = new (int, int)[bandCount];
        double binHz = (double)sampleRate / FftSize;
        for (int i = 0; i < bandCount; i++)
        {
            double f1 = MinFreq * Math.Pow(MaxFreq / MinFreq, (double)i / bandCount);
            double f2 = MinFreq * Math.Pow(MaxFreq / MinFreq, (double)(i + 1) / bandCount);
            int lo = (int)Math.Round(f1 / binHz);
            int hi = (int)Math.Round(f2 / binHz);
            if (hi <= lo) hi = lo + 1;
            lo = Math.Clamp(lo, 1, FftSize / 2 - 1);
            hi = Math.Clamp(hi, lo, FftSize / 2 - 1);
            result[i] = (lo, hi);
        }
        return result;
    }

    /// <summary>原地迭代 radix-2 FFT（表已预算）。</summary>
    private static void Fft(float[] re, float[] im, float[] cos, float[] sin)
    {
        int n = re.Length;
        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            int step = n / len;
            for (int i = 0; i < n; i += len)
            {
                for (int k = 0; k < len / 2; k++)
                {
                    int idx = k * step;
                    float wr = cos[idx];
                    float wi = sin[idx];
                    int a = i + k;
                    int b = a + len / 2;
                    float xr = re[b] * wr - im[b] * wi;
                    float xi = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - xr;
                    im[b] = im[a] - xi;
                    re[a] += xr;
                    im[a] += xi;
                }
            }
        }
    }

    // ── COM 互通 ──

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    // 设备枚举器只在 CoreAudioInterop 里创建（三份同名 [ComImport] 类会让 RCW 提前
    // 定型，其它模块拿去转换时抛 InvalidCastException —— 详见那个文件的注释）。

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? iface);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity,
            IntPtr format, IntPtr sessionGuid);
        [PreserveSig]
        int GetBufferSize(out uint frames);
        [PreserveSig]
        int GetStreamLatency(out long latency);
        [PreserveSig]
        int GetCurrentPadding(out uint frames);
        [PreserveSig]
        int IsFormatSupported(uint shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig]
        int GetMixFormat(out IntPtr format);
        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig]
        int Start();
        [PreserveSig]
        int Stop();
        [PreserveSig]
        int Reset();
        [PreserveSig]
        int SetEventHandle(IntPtr handle);
        [PreserveSig]
        int GetService(ref Guid iid, out IntPtr service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig]
        int ReleaseBuffer(uint frames);
        [PreserveSig]
        int GetNextPacketSize(out uint frames);
    }
}
