using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using DesktopZones.Helpers;

namespace DesktopZones.Models;

/// <summary>「播放器」组件的两种形态。创建时写入 <see cref="DesktopMediaPlayer.Mode"/>，
/// 之后只读 —— 管理页 / 属性面板都只展示、不提供切换入口。</summary>
public enum MediaDisplayMode { Bars, Vinyl }

/// <summary>
/// 桌面「播放器」组件（律动条 / 唱片机）。
///
/// 与时钟/日历/便签同构：继承 <see cref="AppearanceModel"/> 拿到
/// 边框/填充/液态玻璃/背景图/恢复按钮/动效等全部外观字段，
/// 再加两种形态各自的私有字段。
///
/// 注意与 C# 内置类型重名：类名不叫 MediaPlayer，避免与
/// System.Windows.Media.MediaPlayer 冲突。
/// </summary>
public class DesktopMediaPlayer : AppearanceModel
{
    // 新建窗口的默认尺寸（律动条 / 唱片机）。唱片机按分区默认大小（400×300）来，
    // 之前 500×320 在桌面上显得过大；改这里就够了，创建居中 / 窗口 ApplyMode 都读它。
    public const double DefaultBarsWidth = 380;
    public const double DefaultBarsHeight = 190;
    public const double DefaultVinylWidth = 400;
    public const double DefaultVinylHeight = 300;

    public Guid Id { get; set; } = Guid.NewGuid();
    public double X { get; set; } = 320;
    public double Y { get; set; } = 160;
    public double Width { get; set; } = 380;
    public double Height { get; set; } = 190;

    // 每模式独立记忆尺寸（同 DesktopClock 的 Digital/Analog）。
    // 字段名带 Mode 前缀，避免与「律动条宽度」BarsBarWidth 混淆。
    public double? BarsModeWidth { get; set; }
    public double? BarsModeHeight { get; set; }
    public double? VinylModeWidth { get; set; }
    public double? VinylModeHeight { get; set; }

    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;

    /// <summary>形态。创建后由 UI 层保证不再修改。</summary>
    public MediaDisplayMode Mode { get; set; } = MediaDisplayMode.Bars;

    /// <summary>磁贴模式：隐藏顶部两个按钮；律动条在磁贴模式下还会把填充/边框/液态玻璃/背景图全部不绘制。
    /// **默认关闭**（两种形态都是），用户想要"只留内容"再自己打开。</summary>
    public bool TileMode { get; set; } = false;

    public double Opacity { get; set; } = 1.0;
    public int CornerRadius { get; set; } = 12;
    public double BorderThickness { get; set; } = 1.0;
    public double ControlOpacity { get; set; } = 45;
    public string TextColor { get; set; } = "#EEFFFFFF";
    public string ButtonColor { get; set; } = "#B0FFFFFF";
    public double BackgroundImageOpacity { get; set; } = 30;

    // ── 律动条模式 ──
    public int BarsCount { get; set; } = 32;                 // 0–100
    public double BarsBarWidth { get; set; } = 4;            // 1–12 px
    public bool BarsRounded { get; set; } = true;            // 圆角/尖角

    /// <summary>跳动幅度档位 1–100（面板上的细调刻度）。真实倍率见
    /// <see cref="AmplitudeFromLevel"/>：50 = 1.0×，1 ≈ 0.33×，100 = 3.0×。</summary>
    public double BarsAmplitude { get; set; } = 50;

    /// <summary>灵敏度档位 1–100。真实倍率见 <see cref="SensitivityFromLevel"/>：
    /// 50 = 1.0×，1 ≈ 0.26×，100 = 4.0×。</summary>
    public double BarsSensitivity { get; set; } = 50;

    /// <summary>幅度/灵敏度是否已迁到 1–100 档位刻度。旧配置里这两个字段存的是
    /// 0.2–2.0 / 0.5–4.0 的倍率，加载时由 <see cref="MigrateBarsScale"/> 换算。</summary>
    public bool BarsScaleV2 { get; set; }

    /// <summary>律动数据源：<c>Spectrum</c> = 真频谱（WASAPI 环回 + FFT，默认），
    /// <c>Simulated</c> = 合成律动（旧算法，保留可切换）。</summary>
    public string BarsSource { get; set; } = "Spectrum";

    /// <summary>倒影</summary>
    public bool BarsReflection { get; set; } = false;
    public string BarsColorMode { get; set; } = "Gradient";  // Single / Gradient / Auto
    public string BarsColor { get; set; } = "#7CF5FF";
    public string BarsColor2 { get; set; } = "#B58CFF";
    public double BarsAutoIntervalSec { get; set; } = 3;     // 自动换色间隔
    public bool ShowDivider { get; set; } = true;            // 分割线

    /// <summary>分割线颜色（独立的 #AARRGGBB）。默认半透明白，跟主体文字脱钩，
    /// 这样调文字色不会连带改分割线。</summary>
    public string DividerColor { get; set; } = "#3CFFFFFF";
    public bool ShowTrackInfo { get; set; } = true;          // 下半部分
    public bool ShowControls { get; set; } = true;           // 三个控制按钮
    public bool ClickToToggle { get; set; } = true;          // 单击条区/唱片 = 播放暂停

    /// <summary>条款 7 的「暂停」勾选项：只冻结本组件的可视化（律动条消失、唱片停转），
    /// 不动外部播放器。状态区会显示「已暂停」。</summary>
    public bool Paused { get; set; } = false;

    // ── 唱片机模式 ──
    public bool ShowVinylTrackPanel { get; set; } = true;     // 右侧曲目信息
    public double VinylSpinSeconds { get; set; } = 6;         // 转一圈秒数

    /// <summary>没有系统媒体会话的播放器（网易云等）：允许从它自己的本地播放队列缓存里
    /// 找到专辑封面链接并下载封面图。关掉就只剩占位盘面。见 MediaArtworkFallback。</summary>
    public bool FetchCover { get; set; } = true;

    // ── P2：强调色 / 跟随会话 ──

    /// <summary>从当前封面提取主色做强调色：律动条颜色、分割线、唱片机中心标签（无封面时）都跟着专辑变色。
    /// 封面取不到 / 封面是灰白黑时，退回下面手设的 <see cref="BarsColor"/> 等颜色。</summary>
    public bool AccentFromCover { get; set; } = false;

    /// <summary>只跟随这个 SMTC 会话（AUMID）。空字符串 = 自动：
    /// 优先"正在出声的那一方"，否则系统当前会话。见 MediaSessionService.PickSession。</summary>
    public string FollowSession { get; set; } = "";

    /// <summary>示例配置兼容：旧版本若曾用 QuickBarMode 表示磁贴模式。</summary>
    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }

    [System.Runtime.Serialization.OnDeserialized]
    internal void OnDeserializedCompat(System.Runtime.Serialization.StreamingContext _)
    {
        MigrateBarsScale();
        if (ExtensionData == null) return;
        if (ExtensionData.TryGetValue("QuickBarMode", out var old)
            && old.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
            TileMode = old.GetBoolean();
    }

    // ── 幅度 / 灵敏度：面板刻度 1–100 ↔ 渲染倍率 ──
    //
    // 旧配置（BarsScaleV2 = false）里这两个字段是倍率：幅度 0.2–2.0、灵敏度 0.5–4.0。
    // 现在面板统一用 1–100 的整数档位，倍率按指数曲线映射：50 档 = 1.0×（与旧默认一致），
    // 两头分别到 3.0×/4.0× 和 0.33×/0.26×，小档位也能拉开差距（线性映射时低位几乎没区别）。
    // 迁移只做一次，靠持久化的 BarsScaleV2 标记区分新旧。

    public const double MinLevel = 1;
    public const double MaxLevel = 100;

    /// <summary>档位 → 幅度倍率（50 档 = 1.0，100 档 = 3.0）。</summary>
    public static double AmplitudeFromLevel(double level)
        => Math.Pow(3.0, (Math.Clamp(level, MinLevel, MaxLevel) - 50) / 50.0);

    /// <summary>档位 → 灵敏度倍率（50 档 = 1.0，100 档 = 4.0）。</summary>
    public static double SensitivityFromLevel(double level)
        => Math.Pow(4.0, (Math.Clamp(level, MinLevel, MaxLevel) - 50) / 50.0);

    /// <summary>旧倍率 → 档位（<see cref="AmplitudeFromLevel"/> 的反函数；基数为 3 时同一条曲线
    /// 也适用于灵敏度，只是端点松一点）。</summary>
    private static double LevelFromAmplitude(double mul)
        => Math.Clamp(50 + 50 * Math.Log(Math.Max(0.01, mul)) / Math.Log(3.0), MinLevel, MaxLevel);

    private static double LevelFromSensitivity(double mul)
        => Math.Clamp(50 + 50 * Math.Log(Math.Max(0.01, mul)) / Math.Log(4.0), MinLevel, MaxLevel);

    /// <summary>把旧倍率配置换算成 1–100 档位。幂等：迁过一次就不再动。</summary>
    public void MigrateBarsScale()
    {
        if (BarsScaleV2) return;
        BarsScaleV2 = true;
        if (BarsAmplitude is > 0 and <= 5) BarsAmplitude = Math.Round(LevelFromAmplitude(BarsAmplitude));
        if (BarsSensitivity is > 0 and <= 5) BarsSensitivity = Math.Round(LevelFromSensitivity(BarsSensitivity));
        // 兜底：反序列化缺字段 / 0 / 负数时落到默认档位
        if (BarsAmplitude < MinLevel || BarsAmplitude > MaxLevel) BarsAmplitude = 50;
        if (BarsSensitivity < MinLevel || BarsSensitivity > MaxLevel) BarsSensitivity = 50;
    }

    public DesktopMediaPlayer Clone()
    {
        var copy = new DesktopMediaPlayer
        {
            Id = Id, X = X, Y = Y, Width = Width, Height = Height,
            BarsModeWidth = BarsModeWidth, BarsModeHeight = BarsModeHeight,
            VinylModeWidth = VinylModeWidth, VinylModeHeight = VinylModeHeight,
            IsVisible = IsVisible, IsLocked = IsLocked, Mode = Mode, TileMode = TileMode,
            Opacity = Opacity, CornerRadius = CornerRadius, BorderThickness = BorderThickness,
            ControlOpacity = ControlOpacity, TextColor = TextColor, ButtonColor = ButtonColor,
            BackgroundImageOpacity = BackgroundImageOpacity,
            BarsCount = BarsCount, BarsBarWidth = BarsBarWidth, BarsRounded = BarsRounded,
            BarsAmplitude = BarsAmplitude, BarsSensitivity = BarsSensitivity,
            BarsScaleV2 = BarsScaleV2,
            BarsSource = BarsSource,
            BarsReflection = BarsReflection, BarsColorMode = BarsColorMode,
            BarsColor = BarsColor, BarsColor2 = BarsColor2,
            BarsAutoIntervalSec = BarsAutoIntervalSec,
            ShowDivider = ShowDivider, ShowTrackInfo = ShowTrackInfo,
            DividerColor = DividerColor,
            ShowControls = ShowControls, ClickToToggle = ClickToToggle, Paused = Paused,
            ShowVinylTrackPanel = ShowVinylTrackPanel, VinylSpinSeconds = VinylSpinSeconds,
            FetchCover = FetchCover,
            AccentFromCover = AccentFromCover,
            FollowSession = FollowSession,
        };
        CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
}
