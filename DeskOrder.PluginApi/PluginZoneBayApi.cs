using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DeskOrder.PluginApi;

/// <summary>
/// 分区坞（Zone Bay）插件扩展。只在 <see cref="IDeskOrderPlugin.Initialize"/> 里注册。
/// 宿主负责窗口、吸附、自动隐藏、展开与分区安全；插件只画芯片条。
/// </summary>
public interface IPluginZoneBays
{
    void Register(PluginZoneBayDescriptor descriptor);
}

/// <summary>一种分区坞组件。</summary>
public sealed class PluginZoneBayDescriptor
{
    /// <summary>插件内唯一（建议小写 + 连字符）。</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>可选：包内相对路径的 16×16 / 32×32 图标。</summary>
    public string? IconPath { get; init; }

    public PluginZoneBayDefaults Defaults { get; init; } = new();

    /// <summary>
    /// 可选：自绘芯片条。null / 抛异常 → 宿主使用内置芯片条。
    /// 返回的元素会被放进坞的内容区，尺寸由宿主决定；插件自己处理溢出滚动。
    /// </summary>
    public Func<PluginZoneBayContext, FrameworkElement>? CreateContent { get; init; }
}

/// <summary>新建坞时的默认外观 / 行为。用户在属性面板改动后以持久化模型为准。</summary>
public sealed class PluginZoneBayDefaults
{
    public string Edge { get; init; } = "Bottom";       // Top / Bottom / Left / Right
    public bool AutoHide { get; init; }
    public bool ExpandOnHover { get; init; } = true;
    public string ExpandMode { get; init; } = "Strip";  // Strip / Original
    public bool EnableRestoreButton { get; init; } = true;

    // 外观（与宿主 AppearanceModel 同一套语义）
    public string FillColor { get; init; } = "#08000000";      // 背板填充
    public double FillOpacity { get; init; } = 1.0;
    public string ChipFillColor { get; init; } = "#00FFFFFF"; // 芯片（按钮）填充；透明 = 原生裸图标
    public double ChipOpacity { get; init; } = 1.0;
    public string IconColor { get; init; } = "";              // 空 = 跟随原分区图标色
    public string BorderColor { get; init; } = "#38FFFFFF";
    public double BorderThickness { get; init; } = 1.0;
    public int CornerRadius { get; init; } = 12;
    public bool EnableLiquidGlass { get; init; } = true;
    public int GlassBlurAmount { get; init; } = 18;
    public int GlassTintOpacity { get; init; } = 50;
    public int GlassTintLuminosity { get; init; } = 100;
    public string GlassColorMode { get; init; } = "Default";
    public string GlassMaterial { get; init; } = "";
}

/// <summary>单个坞实例的上下文。</summary>
public sealed class PluginZoneBayContext
{
    public required string BarId { get; init; }
    public required IPluginZoneBayHost Bar { get; init; }
    public required IPluginZones Zones { get; init; }
    public required IPluginStorage Storage { get; init; }
    public required IPluginUi Ui { get; init; }
    public required ILocalizer Loc { get; init; }
    public required IPluginLog Log { get; init; }

    /// <summary>芯片增删 / 顺序 / 组合成员变化。</summary>
    public event Action? ChipsChanged;

    /// <summary>边、颜色、玻璃、展开方式等宿主设置变化。</summary>
    public event Action? SettingsChanged;

    /// <summary>打开宿主的分区坞属性面板。</summary>
    public void OpenSettings() => OpenSettingsCallback?.Invoke();

    internal Action? OpenSettingsCallback { get; init; }

    internal void RaiseChipsChanged() => ChipsChanged?.Invoke();
    internal void RaiseSettingsChanged() => SettingsChanged?.Invoke();
}

public interface IPluginZoneBayHost
{
    PluginZoneBarSettings Settings { get; }
    bool IsVertical { get; }
}

/// <summary>宿主算好的外观值；插件不要在 UI 线程之外读。</summary>
public sealed class PluginZoneBarSettings
{
    public required string Edge { get; init; }
    public required bool AutoHide { get; init; }
    public required bool ExpandOnHover { get; init; }
    public required string ExpandMode { get; init; }
    public required bool EnableRestoreButton { get; init; }

    /// <summary>芯片击中区边长（宿主按原生快捷栏一格算出来，建议 42）。</summary>
    public double ChipSize { get; init; } = 42;

    /// <summary>芯片间距（原生快捷栏的 DockGap）。</summary>
    public double ChipGap { get; init; } = 4;

    /// <summary>芯片内图标的绘制边长；比 <see cref="ChipSize"/> 小一圈，四周留空。</summary>
    public double IconSize { get; init; } = 28;

    /// <summary>芯片底色（已乘 ChipOpacity）。</summary>
    public required Brush ChipFill { get; init; }

    public required Brush ChipHoverFill { get; init; }

    public required Brush ChipBorder { get; init; }

    public required double ChipBorderThickness { get; init; }

    public required int ChipCornerRadius { get; init; }

    /// <summary>非空时，矢量图标用这个颜色重绘；文件/应用图标保持原色。</summary>
    public required Brush? IconFill { get; init; }
}

public interface IPluginZones
{
    IReadOnlyList<PluginZoneChip> Chips { get; }

    /// <summary>点击芯片：展开；已展开则收起。</summary>
    void Toggle(string zoneId, FrameworkElement? anchor = null);

    void Expand(string zoneId, FrameworkElement? anchor = null);

    void Collapse(string zoneId);

    /// <summary>鼠标进入芯片；宿主 0.5s 后按设置展开。</summary>
    void HoverEnter(string zoneId, FrameworkElement? anchor = null);

    /// <summary>鼠标离开芯片；宿主 2s 后收起（若仍是临时展开）。</summary>
    void HoverLeave(string zoneId);

    /// <summary>芯片右键：在 <paramref name="anchor"/> 处打开坞菜单。</summary>
    void OpenMenu(string zoneId, FrameworkElement anchor);

    /// <summary>
    /// 芯片开始拖动（插件检测到按住后移动超过阈值时调用）。宿主接管跟随影子，
    /// 松手时按光标位置决定「坞内换位」或「拖出变回桌面 36×36 恢复按钮」。
    /// </summary>
    void BeginChipDrag(string zoneId, FrameworkElement? anchor = null);

    /// <summary>芯片拖动结束（鼠标松开时调用）。宿主自己读光标位置，插件不用传坐标。</summary>
    void EndChipDrag();
}

/// <summary>坞里的一颗芯片（只读快照，变化时由 ChipsChanged 通知）。</summary>
public sealed class PluginZoneChip
{
    public required string ZoneId { get; init; }
    public required string Name { get; init; }

    /// <summary>已按 2× DPI 渲染好的图标位图。</summary>
    public required ImageSource Icon { get; init; }

    public bool IsMerged { get; init; }
    public int MemberCount { get; init; }
}
