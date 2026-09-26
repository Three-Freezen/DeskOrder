using System;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

/// <summary>
/// Shared visual appearance fields for Zone / StickyNote / DesktopClock /
/// DesktopCalendar. Inherited (NOT composed) so the JSON shape stays flat
/// and existing preset files keep loading without migration.
///
/// Field defaults match the historical values each model declared before
/// the refactor, so a freshly-loaded preset reads identically.
///
/// Excluded fields (kept per-model because their defaults diverge):
/// - BorderThickness (Zone=1.5, others=1.0)
/// - BackgroundImageOpacity (Zone=40, others=30)
///
/// Excluded fields (model-specific):
/// - TitleBarFillColor / TitleBarOpacity / ControlOpacity / TitleTextColor
///   (only StickyNote)
/// - AnalogFillColor / DigitalFillColor / DigitalBackgroundImage*
///   (only DesktopClock)
/// </summary>
public abstract class AppearanceModel
{
    public bool EnableAcrylic { get; set; } = true;
    public string BorderColor { get; set; } = "#40FFFFFF";
    public string FillColor { get; set; } = "#08000000";
    /// <summary>分区/组件图标。emoji 原样存储；"@zones" 等 token 为软件原生矢量图标（见 Helpers.IconGlyph）。</summary>
    public string IconChar { get; set; } = "";
    /// <summary>图标颜色（独立于标题/按钮颜色）。空 = 回退到各组件的默认图标色。</summary>
    public string IconColor { get; set; } = "";
    public int GlassBlurAmount { get; set; } = 18;
    public int GlassTintOpacity { get; set; } = 50;
    public int GlassTintLuminosity { get; set; } = 100;
    public string GlassColorMode { get; set; } = "Default";
    /// <summary>ponytail 2026-09-26: 材质预设 key(Acrylic / AcrylicThin / Matte / Smoke /
    /// Frosted / Clear / Liquid / DarkGlass)。决定 DWM 背板配方 = AccentState(4 亚克力带噪点 /
    /// 3 平滑模糊) + 是否叠加经典 blurbehind。空串 = 自定义 = 历史亚克力背板 —— 老配置没有
    /// 该字段,反序列化后即空串,视觉效果与升级前完全一致。模糊/着色仍由上面四个字段决定。</summary>
    public string GlassMaterial { get; set; } = "";
    /// <summary>ponytail 2026-08-28: 新建分区/时钟/日历/便签默认开启液态玻璃。
    /// JSON 反序列化时旧数据里显式的 false 仍会覆盖此默认,已存在对象不受影响。</summary>
    public bool EnableLiquidGlass { get; set; } = true;
    public string BackgroundImagePath { get; set; } = "";
    public string BgImageStretch { get; set; } = "UniformToFill";
    public double BgImageZoom { get; set; } = 1.0;
    public double BgImageOffsetX { get; set; } = 0;
    public double BgImageOffsetY { get; set; } = 0;
    public bool EnableRestoreButton { get; set; } = true;
    /// <summary>
    /// ponytail: true = restore button hover auto-expands after a short delay (existing behaviour);
    /// false = hover does nothing, only direct click on the RestoreButton expands.
    /// Always read live by HoverExpandBehavior via a getter so a PropertyPanel toggle takes
    /// effect on the next hover without restarting the behavior.
    /// Default false: opt-in, otherwise first-time users see the zone spring open unexpectedly.
    /// </summary>
    public bool HoverAutoExpand { get; set; } = false;

    /// <summary>ponytail 2026-09-26(二期): 每个对象自己记住「本对象是否走新渲染方案
    /// (壁纸采样自绘背板)」。全局开关在「设置 → 外观」,这里是它写入各对象的快照 ——
    /// 渲染侧只读这个字段,所以新旧方案可以同时存在于桌面上(切开关不会动老对象)。
    /// 默认 false = 使用 DWM 方案(用户明确要求默认 DWM)。**注意**:一期已选过材质的
    /// 对象在迁移时会被写成 true(否则切到全局默认后会突然换观感)。</summary>
    public bool UseWallpaperRenderer { get; set; } = false;

    // ── Hover restore animation (per-instance, spec §7.1 #2) ──
    // ponytail: EnableRestoreButton (above) gates the entire feature — when off
    // the RestoreButton is hidden and no hover/click animation runs. When on,
    // HoverAutoExpand (above) further gates the HOVER trigger; direct clicks
    // on the RestoreButton always expand regardless of HoverAutoExpand.
    // The behavior decides on collapse: cursor outside for 2 s collapses a
    // hover-expanded window, click-expanded windows stay open until next Hide.
    // PanelPresetConfig declares only HoverExpandSpeed (see spec §7.2).
    public HoverExpandAnimationKind HoverExpandAnimation { get; set; } = HoverExpandAnimationKind.ScaleExpand;
    public double HoverExpandSpeed { get; set; } = 1.0;
    /// <summary>
    /// ponytail: Anchor point + RestoreButton position for the restore animation — see
    /// <see cref="HoverExpandOrigin"/>. Default ButtonCenter parks the button at the
    /// window's center and expands from the middle.
    /// </summary>
    public HoverExpandOrigin HoverExpandOrigin { get; set; } = HoverExpandOrigin.ButtonCenter;

    // ponytail: 2026-08-21 — Live notification for hover-expand settings changes.
    // PropertyPanel.OpenMotionDialog mutates HoverExpand{Animation,Origin,Speed} on
    // the model and raises this event after Save(). Live HoverExpandBehavior
    // instances subscribe in their widget's ctor and call SetEnabled(...) which
    // re-runs ApplyOrigin + NormalizeFor so the next expand uses the new values.
    // Without this event the live behaviour kept its ctor-time origin/kind and
    // silently ignored dialog changes (BUG: ButtonCorner never took effect;
    // switching kind left stale Scale=0 → 36×36 ghost).
    [field: JsonIgnore]
    public event Action? HoverExpandSettingsChanged;

    /// <summary>Internal raise so external mutation sites (PropertyPanel) don't need
    /// to know the event exists. Call after Save() to notify live behaviours.</summary>
    internal void RaiseHoverExpandSettingsChanged()
        => HoverExpandSettingsChanged?.Invoke();
}