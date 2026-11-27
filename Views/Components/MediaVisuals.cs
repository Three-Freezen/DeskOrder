using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;

namespace DesktopZones.Views.Components;

/// <summary>
/// 逐帧动画共用的高精度时钟（秒，双精度）。
/// 用 Stopwatch 而不是 Environment.TickCount64：后者只有 ~15.6ms 分辨率，
/// 0.55 秒的唱臂摆动会被量化成肉眼可见的台阶（这也是"唱针有点卡"的一部分原因）。
/// </summary>
internal static class FrameClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    /// <summary>自进程启动以来的秒数。</summary>
    public static double Now => Watch.Elapsed.TotalSeconds;
}

/// <summary>
/// 律动条自绘元素。
///
/// P0 的数据源是「模拟律动」（正弦 + 噪声 + 播放状态门控）；
/// P1 接 WASAPI 环回 + FFT 后只需把 <see cref="FeedLevels"/> 喂进来，
/// 渲染代码不用改。
///
/// 性能约定：整个条区**画进一张 WriteableBitmap**（一次 DrawImage 贴图），
/// 不建控件、不做逐条矢量重绘；帧回调按用户设定的 <see cref="TargetFps"/>（0–100，
/// 默认 30）限流，连续静音时再降到 ≤4fps，只在「播放中且未被暂停」时挂
/// <see cref="CompositionTarget.Rendering"/>，其余状态直接停帧并清空。
/// 帧率设成 0 = 停止逐帧刷新（条子静止贴地，不占 CPU 绘制开销）。
/// </summary>
public sealed class SpectrumBars : FrameworkElement
{
    // ── 外观参数（窗口/属性面板赋值后调用 Apply）──
    public int BarCount { get; set; } = 32;
    public double BarWidth { get; set; } = 4;
    public bool Rounded { get; set; } = true;
    public double Amplitude { get; set; } = 1.0;
    public double Sensitivity { get; set; } = 1.0;
    public bool Reflection { get; set; } = false;

    /// <summary>真实响度来源（0..1，系统混音电平）。为空或抛异常时退回纯合成律动。
    /// 有了它，条子跟着音乐本身的强弱喘气，而不是照着一条正弦匀速起伏。只在模拟算法下用。</summary>
    public Func<float>? EnergyProvider { get; set; }

    /// <summary>律动数据源："Spectrum" = 真频谱（P1，默认），"Simulated" = 合成律动（旧算法）。</summary>
    public string Source { get; set; } = "Spectrum";

    /// <summary>真频谱分频数据来源（长度必须是当前条数）。返回 null 时自动退回模拟。</summary>
    public Func<float[]?>? BandProvider { get; set; }

    /// <summary>颜色模式：Single / Gradient / Auto。</summary>
    public string ColorMode { get; set; } = "Gradient";
    public Color Color1 { get; set; } = Color.FromRgb(0x7C, 0xF5, 0xFF);
    public Color Color2 { get; set; } = Color.FromRgb(0xB5, 0x8C, 0xFF);
    public double AutoColorIntervalSec { get; set; } = 3;

    /// <summary>外部媒体是否在播放。false = 不画（与"没有媒体播放时一样"）。</summary>
    public bool IsPlaying { get; set; }

    /// <summary>属性面板的「暂停」勾选项：冻结本组件的可视化。</summary>
    public bool Frozen { get; set; }

    /// <summary>用户设定的渲染帧率上限（0–100，默认 30）。0 = 停止逐帧刷新，
    /// 条子静止贴地（同时摘掉 CompositionTarget.Rendering，不空转）；上限 100，
    /// 实际帧率仍受显示器刷新率限制。改完需要调用 <see cref="Apply"/> 生效。</summary>
    public int TargetFps { get; set; } = 30;

    /// <summary>自测计数：真正出帧（通过帧率门控、推进模拟并重绘）的次数。
    /// 用来在 --ui-bars-fps 自测里量实际帧率。</summary>
    public long PaintedFrames { get; private set; }

    private readonly Random _rnd = new(20261024);
    private float[] _levels = Array.Empty<float>();
    private float[] _targets = Array.Empty<float>();
    // 每条的随机脉冲（快速衰减）与邻域平滑用的暂存，长度跟随条数
    private float[] _transients = Array.Empty<float>();
    private float[] _scratch = Array.Empty<float>();
    private int _shapeCount = -1;
    private double _barSeed = 1.7;

    // 真实响度跟随（-1 = 没有真实数据）
    private float _energySmooth = -1f;
    private float _prevEnergy;
    private float _kick;
    // 自动增益用的参考峰值（快升慢降），让条子在任何系统音量下都能顶到满高
    private float _energyPeak = 0.12f;

    private bool _hooked;
    private double _lastTick = -1;   // <0 = 还没跑过帧
    private double _elapsed;
    // 自动换色的色相过渡：_hueBase 每帧朝 _hueTarget 渐变，不再瞬间跳色。
    private double _hueBase = 0.5;
    private double _hueFrom = 0.5;
    private double _hueTarget = 0.5;
    private double _fadeElapsed = double.MaxValue;
    private double _fadeDuration = 1.2;
    private double _lastAutoShift;

    // ── 位图渲染 + 帧率门控（Phase 1）──
    // 旧实现每帧在 OnRender 里用矢量画 32–100 根圆角条 + 倒影 DrawingGroup +
    // PushOpacityMask：分层窗口的软件渲染器每帧都要重新栅格化这些几何/遮罩，
    // 实测 60fps 下吃掉 44–50% 单核。改成「自己往一张 Pbgra32 位图里写像素 +
    // 一次 DrawImage 贴图」之后，WPF 每帧只需要做一次位图拷贝合成。
    private WriteableBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private int _pxW, _pxH;
    private double _dpiX = 1, _dpiY = 1;
    private Color[] _colors = Array.Empty<Color>();
    private bool _colorsDirty = true;
    private double _colorHue = double.NaN;

    /// <summary>当前生效的目标帧率（由 <see cref="ResolveTargetFps"/> 按用户设置 + 静音状态算出），
    /// 仅作诊断/状态缓存；真正的上限是 <see cref="TargetFps"/>。</summary>
    private int _targetFps = 30;

    /// <summary>下一次允许出帧的 FrameClock 时间（秒）；<see cref="double.MinValue"/> = 尚未排程。
    /// 用「按目标间隔排程」而不是「距上次出帧 ≥ 1/帧率」的简单门控：
    /// Rendering 事件间隔由显示器刷新率决定，未必能整除目标间隔（94Hz 屏要 60fps、
    /// 60Hz 屏要 24fps…），简单门控会整档掉到 refresh/2 或 refresh/3；排程法允许
    /// 「这次隔 1 帧、下次隔 2 帧」交替，长期平均等于用户设定值。</summary>
    private double _nextPaint = double.MinValue;

    /// <summary>第一次判定为静音的时间；-1 = 当前有声音（或没有可用的采集源）。</summary>
    private double _silentSince = -1;

    public SpectrumBars()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
        Unloaded += (_, _) => Unhook();
        Loaded += (_, _) => SyncHook();
    }

    /// <summary>参数变化后调用：重建缓冲 + 颜色缓存 + 立即重绘。</summary>
    public void Apply()
    {
        int n = Math.Clamp(BarCount, 0, 100);
        if (_shapeCount != n)
        {
            _shapeCount = n;
            _levels = new float[n];
            _targets = new float[n];
            _transients = new float[n];
            _scratch = new float[n];
        }
        _barSeed = _rnd.NextDouble() * 6.28;   // 每次应用参数换一个相位底子，观感不呆板
        // 帧率设成 0：停止逐帧刷新，条子落在静息位置（全 0 → 贴地），
        // 而不是定格在上一帧悬空的高度。改回 >0 后再 Apply 会重新挂帧。
        if (TargetFps <= 0)
        {
            if (_levels.Length > 0) Array.Clear(_levels, 0, _levels.Length);
            if (_targets.Length > 0) Array.Clear(_targets, 0, _targets.Length);
            _silentSince = -1;
        }
        // 参数（含帧率）变了就重新排程：改完立刻出下一帧，不用等旧帧率的长间隔走完。
        _nextPaint = double.MinValue;
        // 换色相模式 / 改间隔时从下一个完整间隔重新计时，避免刚 Apply 就跳一次色。
        _lastAutoShift = _elapsed;
        if (ColorMode != "Auto")
        {
            // 离开自动换色时把过渡收干净，下次切回来从一个完整间隔开始。
            _hueFrom = _hueTarget;
            _hueBase = _hueTarget;
            _fadeElapsed = double.MaxValue;
        }
        _colorsDirty = true;
        SyncHook();
        InvalidateVisual();
    }

    /// <summary>P1 真实频谱入口：传入 0..1 的电平（长度可小于 BarCount）。</summary>
    public void FeedLevels(ReadOnlySpan<float> levels)    {
        int n = _levels.Length;
        for (int i = 0; i < n; i++)
            _targets[i] = i < levels.Length ? Math.Clamp(levels[i], 0f, 1f) : 0f;
    }

    /// <summary>调试/截图用：填一组静态电平（离屏窗口没有动画帧）。</summary>
    public void PrimePreviewLevels()
    {
        int n = _levels.Length;
        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0 : (double)i / (n - 1);
            double bell = Math.Exp(-Math.Pow((t - 0.42) / 0.30, 2));
            double wobble = 0.72 + 0.28 * Math.Sin(i * 0.9 + 1.1);
            _levels[i] = (float)Math.Clamp(0.14 + 0.86 * bell * wobble, 0, 1);
            _targets[i] = _levels[i];
        }
        if (_transients.Length == n) Array.Clear(_transients, 0, n);
        InvalidateVisual();
    }

    public void SetPlaying(bool playing, bool frozen)
    {
        bool wasPlaying = IsPlaying;
        IsPlaying = playing;
        Frozen = frozen;
        if (wasPlaying && !playing)
            Array.Clear(_levels, 0, _levels.Length);
        SyncHook();
        InvalidateVisual();
    }

    /// <summary>只换颜色（不重置相位/幅度）：换歌时封面主色变了，条子要跟着换色，
    /// 但不能像 <see cref="Apply"/> 那样把观感重置一遍。</summary>
    public void SetColors(Color c1, Color c2)
    {
        if (Color1 == c1 && Color2 == c2) return;
        Color1 = c1;
        Color2 = c2;
        _colorsDirty = true;    // 颜色缓存必然失配
        InvalidateVisual();
    }

    // ── 帧调度 ──

    private void SyncHook()
    {
        // TargetFps = 0：用户要求停止逐帧刷新 → 直接摘掉 Rendering 回调，不空转。
        bool shouldRun = TargetFps > 0 && IsLoaded && IsPlaying && !Frozen && BarCount > 0 && IsVisible;
        if (shouldRun == _hooked) return;
        if (shouldRun)
        {
            _lastTick = -1;
            _nextPaint = double.MinValue;
            _silentSince = -1;
            _targetFps = Math.Clamp(TargetFps, 0, 100);
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
        else
        {
            Unhook();
        }
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }

    /// <summary>目标帧率：以用户设定的 <see cref="TargetFps"/>（0–100）为上限；
    /// 0 = 不出帧；有信号时用用户帧率；连续静音 1.5s 以上降到 min(4, 用户帧率)
    /// （省电逻辑保留，用户本来就设得比 4 小就听用户的）；采集不可用 / 模拟律动
    /// 不降档。默认 <see cref="TargetFps"/> = 30 时与旧行为完全一致（静音 4fps）。</summary>
    private int ResolveTargetFps(double now)
    {
        int fps = Math.Clamp(TargetFps, 0, 100);
        if (fps <= 0) return 0;
        bool signal;
        try { signal = HasAudioSignal(); }
        catch { signal = true; }
        if (signal)
        {
            _silentSince = -1;
            _targetFps = fps;
            return fps;
        }
        if (_silentSince < 0) _silentSince = now;
        _targetFps = now - _silentSince >= 1.5 ? Math.Min(4, fps) : fps;
        return _targetFps;
    }

    /// <summary>当前有没有值得逐帧刷新的音频能量。
    /// 注意「采集不可用」和「静音」是两回事：没有设备/独占时频谱全零，条子本来就贴地，
    /// 可以降到 4fps；模拟律动则始终在动，必须保持 30fps。</summary>
    private bool HasAudioSignal()
    {
        if (Source != "Spectrum") return true;            // 模拟律动：动画本身在跑
        var bandProvider = BandProvider;
        if (bandProvider == null) return true;
        var bands = bandProvider();
        if (bands == null) return true;
        for (int i = 0; i < bands.Length; i++)
            if (bands[i] > 0.004f) return true;
        var energy = EnergyProvider;
        if (energy != null)
        {
            try { if (energy() > 0.002f) return true; } catch { }
        }
        return false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!IsPlaying || Frozen || !IsVisible) { SyncHook(); return; }
        double now = FrameClock.Now;
        if (_lastTick < 0) { _lastTick = now; _nextPaint = now; return; }

        int fps = ResolveTargetFps(now);
        if (fps <= 0) return;   // TargetFps=0：不出帧（正常情况下 SyncHook 已摘掉回调，这里兜底）

        // 帧率门控：CompositionTarget.Rendering 跟随显示器刷新率（60–144Hz），这里按
        // 「目标帧间隔排程」放行 —— 到点了才出帧，晚了就在下一帧补上（允许 1/2 帧交替），
        // 长期平均 = 目标帧率，不会像「距上次出帧 ≥ 1/fps」那样被刷新率量化掉一档。
        // 被跳过的帧不做模拟也不重绘（每帧整窗提交是主要成本）。
        if (_nextPaint == double.MinValue) _nextPaint = now;
        if (now < _nextPaint) return;

        double interval = 1.0 / fps;
        _nextPaint += interval;
        // 长时间断流（窗口被最小化 / 系统卡顿）后重新对齐排程，避免恢复瞬间连续补帧。
        if (_nextPaint < now - interval) _nextPaint = now + interval;

        double dt = Math.Clamp(now - _lastTick, 0.001, 0.1);
        _lastTick = now;
        _elapsed += dt;

        PaintedFrames++;
        AdvanceSimulation(dt);
        InvalidateVisual();
    }

    /// <summary>
    /// 律动推进。两种数据源：
    ///
    /// **真频谱（P1，默认）** —— <see cref="Source"/> = "Spectrum" 且 <see cref="BandProvider"/>
    /// 拿得到分频数据时，直接用每个条对应的真实频段电平。
    ///
    /// **模拟（保留）** —— 拿不到真频谱（独占模式 / 没设备 / 用户手选模拟）时，用合成律动：
    /// 1. 起得快、落得慢（上升 30ms、下落 200ms）；
    /// 2. 跟着真实响度走（<see cref="EnergyProvider"/>）+ 自动增益；
    /// 3. 随机脉冲模拟高频闪烁；鼓点检测让低频先抬；横向轻模糊让相邻条连贯。
    ///
    /// 两条路共用同一套「attack/release 平滑 + 横向模糊」，所以切换算法时观感是连续的。
    /// </summary>
    private void AdvanceSimulation(double dt)
    {
        int n = _levels.Length;
        if (n == 0) return;
        if (_scratch.Length != n) _scratch = new float[n];
        if (_targets.Length != n) _targets = new float[n];
        if (_transients.Length != n) _transients = new float[n];

        float[]? bands = TakeSpectrumBands(n);
        if (bands != null)
        {
            // 真频谱：频段电平已经做过 dB 归一化，这里只叠一个灵敏度增益
            float sens = 0.5f + (float)Sensitivity * 0.5f;
            for (int i = 0; i < n; i++)
                _targets[i] = Math.Clamp(bands[i] * sens, 0f, 1f);
        }
        else
        {
            SimulateTargets(n, dt);
        }

        // ── attack / release 平滑（两种数据源共用）──
        for (int i = 0; i < n; i++)
        {
            float target = _targets[i];
            float cur = _levels[i];
            double tau = target > cur ? 0.030 : 0.20;
            float k = (float)(1 - Math.Exp(-dt / tau));
            _scratch[i] = cur + (target - cur) * k;
        }

        // ── 横向轻模糊：0.70 自身 + 0.15 左右各一（太强会把起伏抹平）──
        for (int i = 0; i < n; i++)
        {
            float left = _scratch[i > 0 ? i - 1 : 0];
            float right = _scratch[i < n - 1 ? i + 1 : n - 1];
            _levels[i] = 0.70f * _scratch[i] + 0.15f * (left + right);
        }

        if (ColorMode != "Auto" || AutoColorIntervalSec <= 0) return;

        if (_elapsed - _lastAutoShift >= AutoColorIntervalSec)
        {
            _lastAutoShift = _elapsed;
            StartHueFade(_rnd.NextDouble());
        }

        if (_fadeElapsed < _fadeDuration)
        {
            _fadeElapsed += dt;
            double p = Math.Clamp(_fadeElapsed / _fadeDuration, 0, 1);
            p = 1 - Math.Pow(1 - p, 3);   // ease-out：起步快、收尾柔和
            _hueBase = LerpHue(_hueFrom, _hueTarget, p);
            _colorsDirty = true;          // 色相变了一个步进，颜色缓存要重建
        }
    }

    /// <summary>取一份真频谱分频数据；算法被设为模拟、或数据还没到 / 段数不匹配时返回 null。</summary>
    private float[]? TakeSpectrumBands(int n)
    {
        if (Source != "Spectrum") return null;
        var provider = BandProvider;
        if (provider == null) return null;
        try
        {
            var bands = provider();
            if (bands == null || bands.Length != n) return null;
            // 还没采到任何非零数据（刚启动 / 静音）→ 当作"没有真频谱"，让模拟先顶上
            for (int i = 0; i < bands.Length; i++)
                if (bands[i] > 0.001f) return bands;
            return bands;   // 全 0 也是有效数据（真的安静），直接显示贴地
        }
        catch
        {
            return null;
        }
    }

    /// <summary>模拟律动：算每条的合成目标电平（保留的旧算法，可在设置里切回来）。</summary>
    private void SimulateTargets(int n, double dt)
    {
        // ── 真实响度 ──
        float energy = -1f;
        var provider = EnergyProvider;
        if (provider != null)
        {
            try { energy = Math.Clamp(provider(), 0f, 1f); }
            catch { energy = -1f; }
        }
        bool hasEnergy = energy >= 0f;

        if (hasEnergy)
        {
            if (_energySmooth < 0) _energySmooth = energy;
            else
            {
                double tau = energy > _energySmooth ? 0.035 : 0.28;   // 快升慢降
                float k = (float)(1 - Math.Exp(-dt / tau));
                _energySmooth += (energy - _energySmooth) * k;
            }

            // 自动增益：以"最近的最大电平"为参照做归一化。
            // 不做这件事的话，音乐峰值常常只有 0.05~0.3，直接当增益用条子永远只有三成高 →
            // 就是"高低起伏太小、整体太平"的直接原因。
            float kPeak = (float)(1 - Math.Exp(-dt / (_energySmooth > _energyPeak ? 0.12 : 3.0)));
            _energyPeak += (_energySmooth - _energyPeak) * kPeak;
            if (_energyPeak < 0.03f) _energyPeak = 0.03f;

            // 鼓点：比上一帧明显更响 → 打一拍
            if (energy > _prevEnergy * 1.28f + 0.05f) _kick = Math.Min(1f, _kick + 0.85f);
            _prevEnergy = energy;
        }
        else
        {
            // 没有真实电平：按 2.4s 的合成节拍给一点律动，别让条子死平
            _energySmooth = -1f;
            _prevEnergy = 0f;
            _energyPeak = 0.12f;
            double beat = 0.5 + 0.5 * Math.Sin(_elapsed * Math.PI * 2 / 2.4);
            _kick = Math.Max(_kick, (float)(0.40 * beat * beat));
        }
        _kick = (float)(_kick * Math.Exp(-dt / 0.22));

        // ── 随机脉冲（模拟真实频谱的高频闪烁感）──
        if (_rnd.NextDouble() < 0.60)
        {
            int i = _rnd.Next(n);
            _transients[i] = Math.Max(_transients[i], (float)(0.25 + 0.55 * _rnd.NextDouble()));
        }
        float decay = (float)Math.Exp(-dt / 0.085);

        float gain;
        if (hasEnergy)
        {
            float norm = Math.Clamp(_energySmooth / _energyPeak, 0f, 1.1f);
            gain = 0.12f + 0.98f * (float)Math.Pow(norm, 0.75);
        }
        else
        {
            gain = 0.86f;
        }

        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0 : (double)i / (n - 1);
            // 频谱包络：低频高、高频矮
            double shape = Math.Exp(-Math.Pow((t - 0.32) / 0.42, 2));
            // 两条不同频率 / 相位的摆动叠在一起，相邻条之间差异更明显（原来一条摆动太平滑）
            double wobble = 0.55 + 0.45 * Math.Sin(_elapsed * (4.1 + 2.4 * t) + _barSeed + i * 0.9);
            double wobble2 = 0.5 + 0.5 * Math.Sin(_elapsed * (7.3 - 1.9 * t) + _barSeed * 1.7 + i * 1.7);
            double env = Math.Pow(Math.Clamp(shape * (0.58 * wobble + 0.42 * wobble2), 0, 1), 1.2);
            double low = Math.Max(0, 1 - t * 2.2);        // 只照顾左侧低频条

            _targets[i] = (float)Math.Clamp(
                (0.05 + 0.95 * env) * gain * Sensitivity
                + _transients[i] * (0.55 + 0.45 * t)
                + _kick * 0.45 * low,
                0, 1);

            _transients[i] *= decay;
        }
    }

    /// <summary>开始一次色相渐变。时长跟着换色间隔走（间隔 3s → 约 1.35s 渐完），
    /// 但夹在 0.35~2s，间隔调到极短时也不会变成瞬切。</summary>
    private void StartHueFade(double target)
    {
        _hueFrom = _hueBase;
        _hueTarget = ((target % 1) + 1) % 1;
        _fadeElapsed = 0;
        _fadeDuration = Math.Clamp(AutoColorIntervalSec * 0.45, 0.35, 2.0);
    }

    /// <summary>色相按最短路径插值（0.92 → 0.08 走 +0.16，而不是 -0.84）。</summary>
    private static double LerpHue(double from, double to, double t)
    {
        double d = to - from;
        if (d > 0.5) d -= 1;
        else if (d < -0.5) d += 1;
        return (((from + d * t) % 1) + 1) % 1;
    }

    // ── 绘制（写 Pbgra32 位图 + 一次 DrawImage 贴图）──
    //
    // 为什么不再用 DrawingContext 画几何：分层窗口（AllowsTransparency=True）强制 WPF
    // 走软件渲染，每帧都要把矢量几何 + 倒影的 DrawingGroup + PushOpacityMask 重新栅格化
    // 一遍——实测 60fps 下这一条占 44–50% 单核。改成自己往位图里写像素后，WPF 每帧
    // 只剩「一次 DrawImage 合成」，同样的观感，成本降到原来的零头。

    protected override void OnRender(DrawingContext dc)
    {
        int n = _levels.Length;
        double w = ActualWidth, h = ActualHeight;
        if (n == 0 || w <= 0 || h <= 0) return;
        if (!IsPlaying || Frozen) return;

        EnsureBitmap(w, h);
        RenderBarsToBitmap(w, h);
        if (_bitmap != null) dc.DrawImage(_bitmap, new Rect(0, 0, w, h));
    }

    /// <summary>按当前 DPI / 元素尺寸准备位图与像素缓冲；尺寸或 DPI 变了就重建。</summary>
    private void EnsureBitmap(double w, double h)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double dx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        double dy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;
        if (Math.Abs(dx - _dpiX) > 1e-6 || Math.Abs(dy - _dpiY) > 1e-6)
        {
            _dpiX = dx;
            _dpiY = dy;
            _bitmap = null;
        }
        int pw = Math.Max(1, (int)Math.Ceiling(w * _dpiX));
        int ph = Math.Max(1, (int)Math.Ceiling(h * _dpiY));
        if (_bitmap != null && _pxW == pw && _pxH == ph) return;
        _pxW = pw;
        _pxH = ph;
        _pixels = new int[pw * ph];
        _bitmap = new WriteableBitmap(pw, ph, 96 * _dpiX, 96 * _dpiY, PixelFormats.Pbgra32, null);
    }

    private void RenderBarsToBitmap(double w, double h)
    {
        var bitmap = _bitmap;
        int n = _levels.Length;
        int pw = _pxW, ph = _pxH;
        if (bitmap == null || n == 0 || pw <= 0 || ph <= 0) return;

        var pixels = _pixels;
        Array.Clear(pixels, 0, pixels.Length);

        double sx = pw / w;
        double sy = ph / h;
        double hDip = h;
        double baselineDip = Reflection ? hDip * 0.80 : hDip;
        double baseline = baselineDip * sy;
        double maxBar = baselineDip * 0.96 * sy;
        double slot = w / n;                                  // DIP
        double barW = Math.Clamp(BarWidth, 1, 12);
        barW = Math.Min(barW, slot * 0.86);                   // 条太多时自动收窄，不裁切
        double barWpx = barW * sx;
        double gap = slot - barW;
        double radius = (Rounded ? Math.Min(barW / 2, 5) : 0) * sx;
        float pulse = (float)Amplitude;

        // 倒影：旧实现是「DrawingGroup + PushOpacityMask（0x60 → 0 的竖向渐变）」。
        // 现在直接把同样的渐变乘进每行像素的 alpha，省掉整层遮罩的离屏合成。
        double reflectSpace = Reflection ? (hDip - baselineDip - 2) * sy : 0;
        double reflectH = Reflection && reflectSpace > 4 ? Math.Min(maxBar * 0.38, reflectSpace) : 0;

        EnsureColors(n);

        for (int i = 0; i < n; i++)
        {
            var color = _colors[Math.Min(i, _colors.Length - 1)];
            double level = Math.Clamp(_levels[i] * pulse, 0f, 1f);
            double bh = Math.Max(2 * sy, level * maxBar);
            double x = (i * slot + gap / 2) * sx;
            double y = baseline - bh;
            double rr = Math.Min(radius, Math.Min(barWpx, bh) / 2);
            FillRoundedRect(pixels, pw, ph, x, y, barWpx, bh, rr,
                color.R, color.G, color.B, color.A, 0, 0, 1f);

            if (reflectH > 4)
            {
                double rh = Math.Min(bh * 0.38, reflectH);
                double ry = baseline + 2 * sy;
                double rr2 = Math.Min(radius, Math.Min(barWpx, rh) / 2);
                FillRoundedRect(pixels, pw, ph, x, ry, barWpx, rh, rr2,
                    color.R, color.G, color.B, color.A, ry, reflectH, 0x60 / 255f);
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, pw, ph), pixels, pw * 4, 0);
    }

    /// <summary>每根条的颜色（颜色模式改 / 自动换色渐变 / 条数变时才重算）。</summary>
    private void EnsureColors(int n)
    {
        bool autoMoving = ColorMode == "Auto" && Math.Abs(_hueBase - _colorHue) > 0.0005;
        if (!_colorsDirty && !autoMoving && _colors.Length == n) return;
        _colorsDirty = false;
        _colorHue = _hueBase;
        if (_colors.Length != n) _colors = new Color[n];
        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0 : (double)i / (n - 1);
            _colors[i] = ColorMode switch
            {
                "Single" => Color1,
                "Auto" => AutoColorAt(t),
                _ => Lerp(Color1, Color2, t),
            };
        }
    }

    /// <summary>把一根圆角矩形的像素写进 Pbgra32 缓冲（几何单位 = 设备像素）。
    ///
    /// 覆盖率用「到圆角矩形的有符号距离」算（SDF）：边界 1px 内做线性抗锯齿，
    /// 和旧版矢量绘制的边缘观感一致；内部整像素直接按覆盖率 1 写。
    ///
    /// <paramref name="fadeOriginY"/> / <paramref name="fadeHeight"/> 非 0 时按行做
    /// 「<paramref name="fadeAlpha"/> → 0」线性淡出（倒影用），等价于旧版的竖向透明度遮罩。
    /// 条与条之间不重叠，所以直接写像素（不混合），省一次读改写。</summary>
    private static void FillRoundedRect(
        int[] pixels, int pw, int ph,
        double x0, double y0, double w, double h, double r,
        byte cr, byte cg, byte cb, byte ca,
        double fadeOriginY, double fadeHeight, float fadeAlpha)
    {
        if (w < 0.4 || h < 0.4) return;
        double x1 = x0 + w, y1 = y0 + h;
        int px0 = Math.Max(0, (int)Math.Floor(x0 - 1));
        int py0 = Math.Max(0, (int)Math.Floor(y0 - 1));
        int px1 = Math.Min(pw - 1, (int)Math.Ceiling(x1 + 1));
        int py1 = Math.Min(ph - 1, (int)Math.Ceiling(y1 + 1));
        if (px1 < px0 || py1 < py0) return;

        double halfW = w * 0.5, halfH = h * 0.5;
        double cx = x0 + halfW, cy = y0 + halfH;
        bool fading = fadeHeight > 0.01;

        for (int py = py0; py <= py1; py++)
        {
            double pyc = py + 0.5;
            double dy = pyc - cy;
            double qy = (dy < 0 ? -dy : dy) - halfH + r;
            double rowScale = 1.0;
            if (fading)
            {
                double t = (pyc - fadeOriginY) / fadeHeight;
                if (t >= 1) continue;
                if (t < 0) t = 0;
                rowScale = fadeAlpha * (1 - t);
                if (rowScale <= 0.002) continue;
            }

            int row = py * pw;
            for (int px = px0; px <= px1; px++)
            {
                double pxc = px + 0.5;
                double dx = pxc - cx;
                double qx = (dx < 0 ? -dx : dx) - halfW + r;
                double ax = qx > 0 ? qx : 0;
                double ay = qy > 0 ? qy : 0;
                double sd;
                if (ax > 0 && ay > 0)
                    sd = Math.Sqrt(ax * ax + ay * ay) + Math.Min(Math.Max(qx, qy), 0) - r;
                else
                    sd = Math.Max(qx, qy) - r;
                double cov = 0.5 - sd;
                if (cov <= 0) continue;
                if (cov > 1) cov = 1;

                int a = (int)(ca * cov * rowScale);
                if (a <= 0) continue;
                pixels[row + px] = (a << 24)
                    | ((cr * a / 255) << 16)
                    | ((cg * a / 255) << 8)
                    | (cb * a / 255);
            }
        }
    }

    private Color AutoColorAt(double t)
    {
        double hue = (_hueBase + t * 0.18) % 1.0;
        return FromHsv(hue, 0.74, 1.0);
    }

    private static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * t),
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    internal static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 1) + 1) % 1;
        int i = (int)(h * 6);
        double f = h * 6 - i;
        double p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (double r, double g, double b) = (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

/// <summary>
/// 唱片机自绘元素：黑胶盘（纹路 / 高光 / 圆形封面 / 中心孔）。
///
/// 旋转逐帧推进（CompositionTarget.Rendering），暂停立刻停帧；
/// 唱臂是独立的 <see cref="TonearmVisual"/>（叠在盘面上），
/// 两件事分开画：盘面每帧重绘不会连累唱臂，唱臂摆动也不重绘盘面。
/// </summary>
public sealed class VinylPlayerVisual : FrameworkElement
{
    private BitmapSource? _cover;
    private bool _playing;
    private double _spinSeconds = 6;
    private double _angle;
    private double _lastTick = -1;   // <0 = 还没跑过帧
    private double _lastPaint = double.MinValue;   // Phase 1：30fps 限流
    private bool _hooked;

    private DrawingGroup? _ringsCache;
    private double _ringsCacheSize = -1;
    private Brush? _discBrush;
    private double _discBrushSize = -1;
    private Pen? _sheenPen;
    private double _sheenSize = -1;

    /// <summary>没封面时的中心标签渐变（用强调色生成，颜色变了才重建）。</summary>
    private Brush? _labelBrush;
    private Color _labelColor = Color.FromArgb(0, 0, 0, 0);

    public event EventHandler? DiscClicked;

    public VinylPlayerVisual()
    {
        SnapsToDevicePixels = true;
        Unloaded += (_, _) => Unhook();
        Loaded += (_, _) => SyncHook();
        MouseLeftButtonUp += (_, _) => DiscClicked?.Invoke(this, EventArgs.Empty);
        Cursor = System.Windows.Input.Cursors.Hand;
    }

    public BitmapSource? Cover
    {
        get => _cover;
        set { _cover = value; InvalidateVisual(); }
    }

    // ── P2：中心标签用的强调色 ──

    private Color _accent = Color.FromRgb(0x7C, 0xF5, 0xFF);

    /// <summary>中心标签（无封面时）的颜色：封面主色或用户设的律动条颜色。</summary>
    public Color AccentColor
    {
        get => _accent;
        set
        {
            if (_accent == value) return;
            _accent = value;
            InvalidateVisual();
        }
    }

    public double SpinSeconds
    {
        get => _spinSeconds;
        set => _spinSeconds = Math.Clamp(value, 1.5, 60);
    }

    public void SetPlaying(bool playing, bool frozen)
    {
        bool running = playing && !frozen;
        if (_playing != running)
        {
            _playing = running;
            _lastTick = -1;
            _lastPaint = double.MinValue;
            SyncHook();
        }
        InvalidateVisual();
    }

    /// <summary>调试/截图用：把盘面角度摆到"正在播放"的姿态。
    /// 离屏窗口不会收到 CompositionTarget.Rendering，拿不到动画帧。</summary>
    public void PrimePreviewPose()
    {
        _angle = 24;
        InvalidateVisual();
    }

    private void SyncHook()
    {
        bool shouldRun = IsLoaded && _playing && IsVisible;
        if (shouldRun == _hooked) return;
        if (shouldRun)
        {
            _lastTick = -1;
            _lastPaint = double.MinValue;
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
        else
        {
            Unhook();
        }
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_playing || !IsVisible) { SyncHook(); return; }
        double now = FrameClock.Now;
        if (_lastTick < 0) { _lastTick = now; _lastPaint = now; return; }
        // Phase 1：盘面旋转限流到 30fps —— 慢速自转看不出差别，分层窗口的每帧成本减半。
        if (now - _lastPaint < 1.0 / 30.0) return;
        double dt = Math.Clamp(now - _lastTick, 0.001, 0.1);
        _lastTick = now;
        _lastPaint = now;

        _angle = (_angle + 360.0 / Math.Max(1.5, _spinSeconds) * dt) % 360;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double s = Math.Min(w, h);
        double cx = w / 2, cy = h / 2;
        double r = s / 2 - 2;
        if (r <= 6) return;

        EnsureBrushes(r);
        EnsureRings(r);

        // 盘面：平移到圆心 → 旋转 → 画盘/纹路/封面
        dc.PushTransform(new TranslateTransform(cx, cy));
        dc.PushTransform(new RotateTransform(_angle));
        dc.DrawEllipse(_discBrush, null, new Point(0, 0), r, r);
        if (_ringsCache != null) dc.DrawDrawing(_ringsCache);

        double cr = r * 0.52;
        if (_cover != null)
        {
            dc.PushClip(new EllipseGeometry(new Point(0, 0), cr, cr));
            dc.DrawImage(_cover, new Rect(-cr, -cr, cr * 2, cr * 2));
            dc.Pop();
        }
        else
        {
            // 没有封面：中心标签用强调色做渐变（封面取色时跟专辑走，否则是固定的灰紫）
            EnsureLabelBrush();
            dc.DrawEllipse(_labelBrush!, null, new Point(0, 0), cr, cr);
        }
        dc.Pop();
        dc.Pop();

        // 高光弧（固定光源，不随盘转）
        dc.DrawEllipse(null, _sheenPen!, new Point(cx, cy), r * 0.90, r * 0.90);

        // 中心孔
        var hole = new SolidColorBrush(Color.FromArgb(0xF0, 0x0C, 0x0C, 0x0E));
        hole.Freeze();
        dc.DrawEllipse(hole, null, new Point(cx, cy), Math.Max(1.5, r * 0.022), Math.Max(1.5, r * 0.022));
    }

    // ── 缓存 ──

    private void EnsureBrushes(double r)
    {
        if (_sheenPen == null || Math.Abs(_sheenSize - r) >= 0.5)
        {
            _sheenSize = r;
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)), Math.Max(2, r * 0.045));
            pen.Freeze();
            _sheenPen = pen;
        }
        if (_discBrush != null && Math.Abs(_discBrushSize - r) < 0.5) return;
        _discBrushSize = r;
        var g = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.40, 0.34),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.62,
            RadiusY = 0.62,
        };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x2C, 0x2C, 0x33), 0.0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x16, 0x16, 0x1B), 0.55));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x0B, 0x0B, 0x0E), 0.86));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x1C, 0x1C, 0x23), 1.0));
        g.Freeze();
        _discBrush = g;
    }

    /// <summary>没封面时的中心标签：从强调色生成一个"中间亮、边缘暗"的径向渐变。</summary>
    private void EnsureLabelBrush()
    {
        if (_labelBrush != null && _labelColor == _accent) return;
        _labelColor = _accent;
        Color Lighten(Color c, double amt) => Color.FromRgb(
            (byte)Math.Min(255, c.R + (255 - c.R) * amt),
            (byte)Math.Min(255, c.G + (255 - c.G) * amt),
            (byte)Math.Min(255, c.B + (255 - c.B) * amt));
        Color Darken(Color c, double amt) => Color.FromRgb(
            (byte)(c.R * (1 - amt)), (byte)(c.G * (1 - amt)), (byte)(c.B * (1 - amt)));

        var g = new RadialGradientBrush(Darken(_accent, 0.35), Darken(_accent, 0.78))
        {
            GradientOrigin = new Point(0.38, 0.33),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        // 中间一小块留亮，像唱片中心贴纸
        g.GradientStops.Insert(0, new GradientStop(Lighten(_accent, 0.25), 0.0));
        g.GradientStops.Insert(1, new GradientStop(_accent, 0.55));
        g.Freeze();
        _labelBrush = g;
    }

    private void EnsureRings(double r)    {
        if (_ringsCache != null && Math.Abs(_ringsCacheSize - r) < 0.5) return;
        _ringsCacheSize = r;
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            // 12 圈纹路足够看出黑胶质感，再密只是白烧绘制
            for (int i = 0; i < 12; i++)
            {
                double rr = r * (0.58 + 0.033 * i);
                byte a = (byte)(i % 2 == 0 ? 0x18 : 0x10);
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(a, 0xFF, 0xFF, 0xFF)), 0.9);
                pen.Freeze();
                dc.DrawEllipse(null, pen, new Point(0, 0), rr, rr);
            }
        }
        group.Freeze();
        _ringsCache = group;
    }
}

/// <summary>
/// 唱臂自绘元素（叠在 <see cref="VinylPlayerVisual"/> 上方，形态与 P0 完全一致：底座 + 折臂 + 唱头）。
///
/// 为什么独立成一个元素：唱臂只有「落针 / 抬起」两次摆动，其余时间完全静止。
/// 跟盘面画在同一个元素里时，盘面每帧重绘都会连累唱臂，反倒在 60fps 下看出抖动。
/// 拆开之后：盘面按帧重绘，唱臂只在摆动的那 0.55 秒里挂帧，静止时不产生任何渲染。
///
/// 运动方式也换掉了原来的「指数逼近」（起步猛、尾巴爬、永远差一点点，看着发顿）：
/// 改成固定时长的三次缓入缓出（ease-in-out）——起手慢、中间匀、落针前收住，像真的机械臂。
/// </summary>
public sealed class TonearmVisual : FrameworkElement
{
    private const double ArmPlayingDeg = -40;
    private const double ArmRestingDeg = -78;

    /// <summary>一次摆动的时长（秒）。落针 / 抬起一样长，观感统一。</summary>
    private const double SwingSeconds = 0.55;

    private bool _playing;
    private double _angle = ArmRestingDeg;
    private double _from = ArmRestingDeg;
    private double _to = ArmRestingDeg;
    private double _elapsed = SwingSeconds;   // >= 时长即「已完成」
    private bool _hooked;
    private double _lastTick = -1;   // <0 = 还没跑过帧

    public TonearmVisual()
    {
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;   // 点击穿透到盘面（单击播放/暂停仍由盘面接收）
        Loaded += (_, _) =>
        {
            // 首帧把角度对齐到当前状态，避免刚显示时先画在错误位置再摆过去
            _angle = _to;
            InvalidateVisual();
            SyncHook();
        };
        Unloaded += (_, _) => Unhook();
    }

    /// <summary>正在播放 → 落针；暂停 / 无媒体 → 抬起。重复调用同一状态不做任何事。</summary>
    public void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        _playing = playing;
        StartSwing(playing ? ArmPlayingDeg : ArmRestingDeg);
    }

    /// <summary>调试/截图用：直接摆到落针姿态（离屏窗口没有动画帧）。</summary>
    public void PrimePreviewPose()
    {
        _playing = true;
        _from = _to = _angle = ArmPlayingDeg;
        _elapsed = SwingSeconds;
        InvalidateVisual();
    }

    private void StartSwing(double target)
    {
        _from = _angle;
        _to = target;
        if (Math.Abs(_to - _from) < 0.05)
        {
            _angle = _to;
            _elapsed = SwingSeconds;
            Unhook();
            InvalidateVisual();
            return;
        }
        _elapsed = 0;
        _lastTick = -1;
        SyncHook();
    }

    private void SyncHook()
    {
        bool shouldRun = IsLoaded && _elapsed < SwingSeconds;
        if (shouldRun == _hooked) return;
        if (shouldRun)
        {
            CompositionTarget.Rendering += OnFrame;
            _hooked = true;
        }
        else
        {
            Unhook();
        }
    }

    private void Unhook()
    {
        if (!_hooked) return;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double now = FrameClock.Now;
        if (_lastTick < 0) { _lastTick = now; return; }
        double dt = Math.Clamp(now - _lastTick, 0, 0.1);
        _lastTick = now;

        _elapsed += dt;
        double p = Math.Clamp(_elapsed / SwingSeconds, 0, 1);
        _angle = _from + (_to - _from) * CubicInOut(p);
        InvalidateVisual();

        if (p >= 1)
        {
            _angle = _to;
            SyncHook();      // 摆动结束 → 停帧，之后不再有任何渲染开销
        }
    }

    /// <summary>三次缓入缓出：两端速度为 0、中间匀，机械臂的手感。</summary>
    private static double CubicInOut(double p)
        => p < 0.5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double s = Math.Min(w, h);
        double cx = w / 2, cy = h / 2;
        double r = s / 2 - 2;
        if (r <= 6) return;

        // 0 = 落针（-40°），1 = 抬起（-78°）。唱头到盘心的距离随比例平滑过渡：
        // 原来按「角度是否接近 -40°」一刀切 0.80r / 1.06r，摆动途中唱头会"跳"一下。
        double lift = Math.Clamp((_angle - ArmPlayingDeg) / (ArmRestingDeg - ArmPlayingDeg), 0, 1);
        double reach = (0.80 + 0.26 * lift) * r;

        double angRad = _angle * Math.PI / 180.0;
        var tip = new Point(cx + reach * Math.Cos(angRad), cy + reach * Math.Sin(angRad));
        var basePt = new Point(cx + r * 1.02, cy - r * 0.95);
        var elbow = new Point((basePt.X + tip.X) / 2 + r * 0.06, (basePt.Y + tip.Y) / 2 - r * 0.05);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(basePt, false, false);
            g.LineTo(elbow, true, false);
            g.LineTo(tip, true, false);
        }
        geo.Freeze();

        var shadowPen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)), Math.Max(3, r * 0.075))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        shadowPen.Freeze();
        dc.PushTransform(new TranslateTransform(1.5, 2.5));
        dc.DrawGeometry(null, shadowPen, geo);
        dc.Pop();

        var armPen = new Pen(new SolidColorBrush(Color.FromArgb(0xF2, 0xEC, 0xEE, 0xF2)), Math.Max(2, r * 0.032))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        armPen.Freeze();
        dc.DrawGeometry(null, armPen, geo);

        var baseBrush = new SolidColorBrush(Color.FromArgb(0xF5, 0xE6, 0xE8, 0xEE));
        baseBrush.Freeze();
        dc.DrawEllipse(baseBrush, null, basePt, Math.Max(3, r * 0.072), Math.Max(3, r * 0.072));
        var pivotDot = new SolidColorBrush(Color.FromArgb(0xFF, 0x6E, 0x74, 0x80));
        pivotDot.Freeze();
        dc.DrawEllipse(pivotDot, null, basePt, Math.Max(1.2, r * 0.028), Math.Max(1.2, r * 0.028));

        // 唱头：与臂垂直的一小段
        double dirAng = Math.Atan2(tip.Y - elbow.Y, tip.X - elbow.X) + Math.PI / 2;
        double hw = Math.Max(3, r * 0.055);
        var p1 = new Point(tip.X + Math.Cos(dirAng) * hw, tip.Y + Math.Sin(dirAng) * hw);
        var p2 = new Point(tip.X - Math.Cos(dirAng) * hw, tip.Y - Math.Sin(dirAng) * hw);
        var headPen = new Pen(baseBrush, Math.Max(2.5, r * 0.065))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        headPen.Freeze();
        dc.DrawLine(headPen, p1, p2);
    }
}
