using System;
using System.ComponentModel;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Cards;

/// <summary>
/// Wrapper around a <see cref="PresetRecord"/> that exposes the typed payload
/// as a public <see cref="Payload"/> property. Card XAML templates bind via
/// <c>{Binding Payload.XXX}</c> and reflection finds the right field on the
/// runtime payload type (Zone, DesktopClock, …).
///
/// Why: each card is a <see cref="System.Windows.DataTemplate"/> with a static
/// binding path. The ItemsControl's items are PresetRecord (base), but each
/// kind's card template wants to bind to the typed payload's fields. A small
/// wrapper keeps XAML declarative and avoids a TemplateSelector or per-kind
/// converter.
/// </summary>
public class PresetCardItem : INotifyPropertyChanged
{
    public PresetRecord Record { get; }
    public object Payload { get; }

    public string Name => Record.Name;
    public DateTime CreatedAt => Record.CreatedAt;
    public PresetKind Kind => Record.Kind;

    /// <summary>Panel preset's primary fill color as a fully-formed <see cref="SolidColorBrush"/> (WPF
    /// can't always auto-convert a hex string to a brush when it's coming through an object cast —
    /// going through <see cref="ColorConverter"/> on the getter avoids silent fallback rendering).</summary>
    public System.Windows.Media.Brush? PanelFillBrush {
        get {
            try {
                var s = (Payload as PanelPresetConfig)?.PanelFillColor;
                if (string.IsNullOrEmpty(s)) return null;
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s)!;
                return new System.Windows.Media.SolidColorBrush(c);
            } catch { return null; }
        }
    }
    /// <summary>Panel preset's border color as a <see cref="SolidColorBrush"/>.</summary>
    public System.Windows.Media.Brush? PanelBorderBrush {
        get {
            try {
                var s = (Payload as PanelPresetConfig)?.PanelBorderColor;
                if (string.IsNullOrEmpty(s)) return null;
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s)!;
                return new System.Windows.Media.SolidColorBrush(c);
            } catch { return null; }
        }
    }
    /// <summary>Panel preset's primary fill color as hex string (kept for diagnostics / future string bindings).</summary>
    public string PanelFillColor => (Payload as PanelPresetConfig)?.PanelFillColor ?? "";
    /// <summary>Panel preset's border color as hex string.</summary>
    public string PanelBorderColor => (Payload as PanelPresetConfig)?.PanelBorderColor ?? "";

    // ── Subfolder preset card surface (Payload = the preset's ZoneItem) ──
    /// <summary>ponytail 2026-08-26: 次级文件夹预设卡只预览样式 — 填充色(无 override
    /// 时用默认暗色)、圆角、背景图、液态玻璃;名称/日期放在卡片下方信息栏,卡片上
    /// 不放额外内容。</summary>
    public string SubfolderFillColor
    {
        get
        {
            var s = (Payload as ZoneItem)?.FillColorOverride;
            return string.IsNullOrEmpty(s) ? "#08000000" : s;
        }
    }

    /// <summary>Subfolder preset fill as a <see cref="SolidColorBrush"/>.</summary>
    public System.Windows.Media.Brush? SubfolderFillBrush
    {
        get
        {
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(SubfolderFillColor)!;
                return new System.Windows.Media.SolidColorBrush(c);
            }
            catch { return null; }
        }
    }

    /// <summary>Subfolder preset glass preview brush — null when the preset has no glass.</summary>
    public System.Windows.Media.Brush? SubfolderGlassBrush
    {
        get
        {
            if ((Payload as ZoneItem)?.EnableLiquidGlass != true) return null;
            var mode = (Payload as ZoneItem)?.GlassColorMode;
            if (string.IsNullOrEmpty(mode)) mode = "Default";
            return (System.Windows.Media.Brush)new LiquidGlassBrushConverter().Convert(
                mode, typeof(System.Windows.Media.Brush), null,
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Clock-only: which display mode the preset stores on disk. Null for non-Clock kinds.</summary>
    public ClockDisplayMode? ClockMode => Payload is DesktopClock c ? c.Mode : null;

    // ── 通用插件卡（v1.2.3：插件组件 / 分区坞）──
    // 这两类的外观字段集跨类型不通用，不做各自的缩略图；卡面只画「填充/圆角/玻璃」
    // 色块 + 名称 + 类型标签（见 LoadPresetDialog 的 PluginPresetCardTemplate）。
    /// <summary>类型标签（本地化）；内置类型返回空串 —— 它们各自的卡片不用这行文字。</summary>
    public string KindLabel => Kind switch
    {
        PresetKind.PluginWidget => LocalizationService.Instance["Preset.Kind.PluginWidget"],
        PresetKind.PluginZoneBar => LocalizationService.Instance["Preset.Kind.PluginZoneBar"],
        _ => "",
    };

    /// <summary>通用卡片底色：插件组件/分区坞两款载荷都有 FillColor 字段（缺失回退默认暗色）。</summary>
    public string PluginCardFillColor => Payload switch
    {
        PluginWidgetAppearance w => w.FillColor,
        PluginZoneBarAppearance b => b.FillColor,
        _ => "#08000000",
    };

    /// <summary>通用卡片填充刷子（Brush 直接给，避免 Color→Brush 的绑定转换静默失败）。</summary>
    public System.Windows.Media.Brush? PluginCardFillBrush
    {
        get
        {
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(PluginCardFillColor)!;
                return new System.Windows.Media.SolidColorBrush(c);
            }
            catch { return null; }
        }
    }

    /// <summary>通用卡片边框色（两款载荷都有 BorderColor）。</summary>
    public string PluginCardBorderColor => Payload switch
    {
        PluginWidgetAppearance w => w.BorderColor,
        PluginZoneBarAppearance b => b.BorderColor,
        _ => "#40FFFFFF",
    };

    /// <summary>通用卡片边框刷子。</summary>
    public System.Windows.Media.Brush? PluginCardBorderBrush
    {
        get
        {
            try
            {
                var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
                    .ConvertFromString(PluginCardBorderColor)!;
                return new System.Windows.Media.SolidColorBrush(c);
            }
            catch { return null; }
        }
    }

    /// <summary>通用卡片圆角：两款载荷都有 CornerRadius。</summary>
    public int PluginCardCornerRadius => Payload switch
    {
        PluginWidgetAppearance w => w.CornerRadius,
        PluginZoneBarAppearance b => b.CornerRadius,
        _ => 12,
    };

    /// <summary>通用卡片边框粗细：两款载荷都有 BorderThickness（绑定不能直接转 Thickness，这里先包好）。</summary>
    public System.Windows.Thickness PluginCardBorderThickness => Payload switch
    {
        PluginWidgetAppearance w => new System.Windows.Thickness(Math.Max(0, w.BorderThickness)),
        PluginZoneBarAppearance b => new System.Windows.Thickness(Math.Max(0, b.BorderThickness)),
        _ => new System.Windows.Thickness(1),
    };

    /// <summary>通用卡片的玻璃预览刷子；未开液态玻璃时返回 null（卡面不叠玻璃层）。</summary>
    public System.Windows.Media.Brush? PluginCardGlassBrush
    {
        get
        {
            var (enabled, mode) = Payload switch
            {
                PluginWidgetAppearance w => (w.EnableLiquidGlass, w.GlassColorMode),
                PluginZoneBarAppearance b => (b.EnableLiquidGlass, b.GlassColorMode),
                _ => (false, ""),
            };
            if (!enabled) return null;
            if (string.IsNullOrEmpty(mode)) mode = "Default";
            return (System.Windows.Media.Brush)new LiquidGlassBrushConverter().Convert(
                mode, typeof(System.Windows.Media.Brush), null,
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private ClockDisplayMode? _displayMode;
    /// <summary>
    /// Clock-only: which template the selector should render this card with.
    /// Defaults to the preset's stored <see cref="ClockMode"/>, but the dialog can
    /// override this to the live widget's current mode so all clock cards re-render
    /// in the matching style when the user toggles Digital ↔ Analog on the live clock.
    /// </summary>
    public ClockDisplayMode? DisplayClockMode
    {
        get => _displayMode ?? ClockMode;
        set
        {
            if (_displayMode != value)
            {
                _displayMode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayClockMode)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PresetCardItem(PresetRecord record)
    {
        Record = record;
        Payload = PresetService.GetPayload(record);
    }
}