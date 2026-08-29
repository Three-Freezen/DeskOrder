using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.ViewModels;

/// <summary>
/// ponytail 2026-08-26: SubFolder 的"填充跟随主分区"解析结果 — 把 ZoneItem 的
/// FillFollowsZone / FillColorOverride / FillOpacityOverride / BackgroundImagePath /
/// BackgroundImageOpacity / EnableLiquidGlass(+玻璃四参数) 与主分区 ResolvedZoneStyle
/// 汇成一份可直接渲染的填充描述(颜色 + 背景图 + 液态玻璃),供次级文件夹图标格与
/// 打开的 Flyout 共用。边框不在其中 — 设计上 SubFolder 图标/打开面板的边框固定不同步。
/// 背景图用 ImageBrush(UniformToFill)输出:图片自动裁剪适应窗口,不参与布局测量 —
/// 直接放 Image 会把尺寸自动的 Popup 撑成图片原始大小。
/// </summary>
public sealed record SubfolderFill(
    string? FillHex,        // 填充色(#AARRGGBB);null = 完全透明(跟随模式图标格透出主分区)
    double FillOpacity,     // 0..100,乘到填充色 alpha 上
    string? BgPath,         // 背景图片路径(空 = 无)
    double BgOpacity,       // 0..100 背景图不透明度
    string? GlassMode,      // 液态玻璃渐变模式(空 = 无玻璃)
    int GlassBlur = 18,     // 玻璃模糊半径(真玻璃用,同 AppearanceModel 默认)
    int GlassTintOpacity = 50,
    int GlassTintLuminosity = 100)
{
    public bool HasGlass => !string.IsNullOrEmpty(GlassMode);

    /// <summary>不跟随主分区时,从 SubFolder 自身的 override 字段解析。</summary>
    public static SubfolderFill FromOverride(ZoneItem sub) => new(
        string.IsNullOrEmpty(sub.FillColorOverride) ? "#08000000" : sub.FillColorOverride,
        sub.FillOpacityOverride < 0 ? 100 : sub.FillOpacityOverride,
        sub.BackgroundImagePath,
        sub.BackgroundImageOpacity < 0 ? 30 : sub.BackgroundImageOpacity,
        sub.EnableLiquidGlass ? (string.IsNullOrEmpty(sub.GlassColorMode) ? "Default" : sub.GlassColorMode) : null,
        sub.GlassBlurAmount, sub.GlassTintOpacity, sub.GlassTintLuminosity);

    /// <summary>解析后的填充画刷(alpha 已乘透明度)。FillHex 为空时返回 null。</summary>
    public Brush? FillBrush
    {
        get
        {
            if (string.IsNullOrEmpty(FillHex)) return null;
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(FillHex)!;
                c.A = (byte)Math.Max(0, Math.Min(255, c.A * FillOpacity / 100.0));
                var b = new SolidColorBrush(c);
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }

    /// <summary>液态玻璃渐变画刷(可见预览渐变,与 LoadPresetDialog 预设卡同款)。
    /// Flyout 上的真玻璃(DWM)失败时用它兜底。</summary>
    public Brush? GlassBrush => string.IsNullOrEmpty(GlassMode) ? null : AcrylicHelper.MakePreviewGlassBrush(GlassMode);

    /// <summary>ponytail 2026-08-30: 一体化背景画刷 — 玻璃开时 = 填充 over 玻璃渐变
    /// (单画刷);无玻璃时 = 纯填充。放在填充层位置(背景图之下),与真玻璃(DWM accent
    /// 也在背景图之下)层序一致。FillHex 为空且无玻璃 → null(完全透明)。</summary>
    public Brush? UnifiedBackgroundBrush
    {
        get
        {
            if (string.IsNullOrEmpty(GlassMode))
                return FillBrush;
            var glass = AcrylicHelper.MakePreviewGlassBrush(GlassMode);
            if (string.IsNullOrEmpty(FillHex))
                return glass;
            return AcrylicHelper.CompositeFillOverBrush(FillHex, FillOpacity / 100.0, glass);
        }
    }

    /// <summary>背景图(文件不存在/路径为空时 null)。</summary>
    public ImageSource? BgImage
    {
        get
        {
            if (string.IsNullOrEmpty(BgPath) || !File.Exists(BgPath)) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(BgPath);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 512;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }
    }

    /// <summary>背景图 ImageBrush — UniformToFill 自动裁剪适应容器,且不参与布局
    /// 测量(修复"跟随图片填充把界面撑大")。</summary>
    public Brush? BgImageBrush
    {
        get
        {
            var img = BgImage;
            if (img == null) return null;
            var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
            b.Freeze();
            return b;
        }
    }

    /// <summary>背景图不透明度(0..1)。</summary>
    public double BgOpacity01 => Math.Max(0.0, Math.Min(1.0, BgOpacity / 100.0));
}
