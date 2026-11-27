#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Components;

namespace DesktopZones;

/// <summary>
/// v1.2.3「插件市场 UX 收尾」的自测用例：插件组件 / 分区坞的预设卡（保存 / 加载外观预设）。
/// 与 <c>App.PluginLab.cs</c> 同属一个 partial 类：主文件只负责分派，用例实现放这里。
///
/// 覆盖：
/// ① 插件组件的 PropertyPanel「加载/保存预设」按钮可用，<c>BuildCurrentPayload</c> 产出
///    独立 <see cref="PresetKind.PluginWidget"/> + 外观快照；
/// ② 快照只含宽高/外观/玻璃/磁贴，落盘 JSON 不含位置/显隐/锁定/实例身份/插件私有 SettingsJson；
/// ③ 保存 → 改成不同外观 → 从磁盘读回 → 面板套用 → 字段恢复，且身份/位置/私有设置原样；
/// ④ 预设文件按 Kind 分目录，插件组件预设与 Zone 预设互不串台（Kind 数值仍是追加的 10）；
/// ⑤ 分区坞（<see cref="PresetKind.PluginZoneBar"/>）同样 roundtrip，且不动坞里的分区 Id；
/// ⑥ 截图：加载预设对话框里的插件组件预设卡（通用插件卡 + 类型标签）。
/// 所有断言都走生产代码（PropertyPanel.Debug* 只是把私有方法转到 internal），不在用例里重写规则。
/// </summary>
public partial class App
{
    async Task RunWidgetPresetLabCaseAsync(LabCase item, string dir)
    {
        var loc = LocalizationService.Instance;
        var pluginSvc = PluginService.Instance;
        var widgetPresets = PresetService.For(PresetKind.PluginWidget);
        var barPresets = PresetService.For(PresetKind.PluginZoneBar);
        var zonePresets = PresetService.For(PresetKind.Zone);

        // 已创建的预设统一在 finally 里删掉，反复跑同一个 data-root 也不会堆垃圾。
        var created = new List<(PresetService Service, Guid Id)>();
        LoadPresetDialog? loadDialog = null;
        PropertyWindow? floating = null;
        PluginWidget? widget = null;
        PluginWidgetAppearance? originalWidgetAppearance = null;
        try
        {
            // 清掉上一轮可能残留的本类型预设（服务目录就在隔离 data-root 里）。
            foreach (var p in widgetPresets.LoadAll()) widgetPresets.Delete(p.Id);
            foreach (var p in barPresets.LoadAll()) barPresets.Delete(p.Id);

            await EnsureEnabledAsync(dir);
            widget = EnsureWidget(pluginSvc);
            if (widget == null) { item.Check(false, "widget instance exists"); return; }
            originalWidgetAppearance = PluginWidgetAppearance.FromWidget(widget);

            // ── 1. 属性面板：这两种目标不再禁掉预设按钮 ──
            EnsureManagementWindow();
            PropertyWindowService.OpenOrFocus(widget);
            await Task.Delay(700);
            floating = PropertyWindowManager.Instance.GetFloating(widget);
            item.Check(floating != null, "the property panel opens for a plugin widget");
            if (floating == null) return;
            item.Check(floating.Body.DebugPresetButtonsEnabled(),
                "load/save preset buttons are enabled for a plugin widget");

            // 身份 / 位置 / 插件私有数据 —— 套预设绝不能动这些。
            var id = widget.Id;
            var pluginId = widget.PluginId;
            var typeId = widget.WidgetTypeId;
            var x = widget.X;
            var y = widget.Y;
            var visible = widget.IsVisible;
            var locked = widget.IsLocked;
            var settingsJson = widget.SettingsJson;

            // ── 2. 改成第一套外观，走面板生产代码拿 (Kind, 载荷) ──
            widget.Width = 420;
            widget.Height = 260;
            widget.CornerRadius = 24;
            widget.FillColor = "#33FF0000";
            widget.BorderColor = "#8040C0FF";
            widget.BorderThickness = 3;
            widget.Opacity = 0.8;
            widget.TileMode = true;
            widget.EnableAcrylic = false;
            widget.EnableLiquidGlass = false;
            widget.EnableRestoreButton = false;
            widget.GlassBlurAmount = 30;
            widget.GlassTintOpacity = 70;
            widget.GlassTintLuminosity = 120;
            widget.GlassColorMode = "OceanBlue";
            widget.GlassMaterial = "mica";

            var (kind, payload) = floating.Body.DebugBuildCurrentPayload();
            item.Check(kind == PresetKind.PluginWidget,
                $"the panel reports the PluginWidget preset kind (actual {kind})");
            item.Check(payload is PluginWidgetAppearance,
                "the panel payload is a plugin-widget appearance snapshot");
            if (payload is not PluginWidgetAppearance snapshot)
            {
                item.Check(false, "the snapshot can be inspected");
                return;
            }
            item.Check(Math.Abs(snapshot.Width - 420) < 0.01 && Math.Abs(snapshot.Height - 260) < 0.01,
                "the snapshot keeps the widget width / height");
            item.Check(snapshot.CornerRadius == 24 && snapshot.TileMode,
                "the snapshot keeps the corner radius / tile mode");
            item.Check(snapshot.GlassBlurAmount == 30 && snapshot.GlassTintOpacity == 70
                       && snapshot.GlassColorMode == "OceanBlue",
                "the snapshot keeps the liquid-glass parameters");

            // ── 3. 保存预设（服务写盘），检查目录 / Kind / JSON 不含敏感字段 ──
            var saved = widgetPresets.Save("Lab 插件组件预设", payload);
            created.Add((widgetPresets, saved.Id));
            item.Check(saved.Kind == PresetKind.PluginWidget && saved is PluginWidgetPreset,
                "the saved preset is a PluginWidget typed record");
            var presetFile = Path.Combine(widgetPresets.Folder, saved.Id + ".json");
            item.Check(File.Exists(presetFile), "the preset file lands in Presets/PluginWidgets");
            item.Check(widgetPresets.Folder.TrimEnd('\\', '/').EndsWith("PluginWidgets", StringComparison.Ordinal),
                $"the plugin-widget preset service uses its own folder ({widgetPresets.Folder})");
            var presetJson = File.ReadAllText(presetFile);
            item.Check(presetJson.Contains("\"Kind\": 10"),
                "the serialized kind stays numeric and appended (10) for PluginWidget");
            item.Check(!presetJson.Contains("SettingsJson") && !presetJson.Contains("PluginId")
                       && !presetJson.Contains("WidgetTypeId"),
                "the preset file never bakes the widget identity / plugin private settings");
            item.Check(!presetJson.Contains("\"X\"") && !presetJson.Contains("\"Y\"")
                       && !presetJson.Contains("\"IsVisible\"") && !presetJson.Contains("\"IsLocked\""),
                "the preset file never bakes position / visibility / lock state");

            // ── 4. 换成完全不同的外观 → 从磁盘读回 → 面板套用 ──
            widget.Width = 200;
            widget.Height = 120;
            widget.CornerRadius = 0;
            widget.FillColor = "#CC00FF00";
            widget.BorderColor = "#FF000000";
            widget.BorderThickness = 0;
            widget.Opacity = 0.5;
            widget.TileMode = false;
            widget.EnableAcrylic = true;
            widget.EnableLiquidGlass = true;
            widget.EnableRestoreButton = true;
            widget.GlassBlurAmount = 1;
            widget.GlassTintOpacity = 1;
            widget.GlassTintLuminosity = 1;
            widget.GlassColorMode = "RosePink";
            widget.GlassMaterial = "";

            var listed = widgetPresets.LoadAll();
            item.Check(listed.Count == 1,
                $"PresetService lists exactly the saved plugin-widget preset (actual {listed.Count})");
            var record = listed.FirstOrDefault();
            item.Check(record != null && record.Name == "Lab 插件组件预设",
                "the listed preset keeps its name");
            item.Check(record is PluginWidgetPreset
                       && record != null && PresetService.GetPayload(record) is PluginWidgetAppearance,
                "the listed preset deserializes into the typed PluginWidget preset / appearance payload");
            if (record == null) return;

            floating.Body.DebugApplyCardPicked(record);
            await Task.Delay(300);
            item.Check(Math.Abs(widget.Width - 420) < 0.01 && Math.Abs(widget.Height - 260) < 0.01,
                "applying the preset restores width / height");
            item.Check(widget.CornerRadius == 24, "applying the preset restores the corner radius");
            item.Check(widget.FillColor == "#33FF0000", "applying the preset restores the fill colour");
            item.Check(widget.BorderColor == "#8040C0FF" && Math.Abs(widget.BorderThickness - 3) < 0.01,
                "applying the preset restores the border colour / thickness");
            item.Check(Math.Abs(widget.Opacity - 0.8) < 0.001, "applying the preset restores the opacity");
            item.Check(widget.GlassBlurAmount == 30 && widget.GlassTintOpacity == 70
                       && widget.GlassTintLuminosity == 120 && widget.GlassColorMode == "OceanBlue"
                       && widget.GlassMaterial == "mica",
                "applying the preset restores the liquid-glass parameters");
            item.Check(widget.TileMode && !widget.EnableAcrylic && !widget.EnableLiquidGlass
                       && !widget.EnableRestoreButton,
                "applying the preset restores tile mode / glass switch / restore button");
            item.Check(widget.Id == id && widget.PluginId == pluginId && widget.WidgetTypeId == typeId,
                "applying a preset never changes the widget identity");
            item.Check(Math.Abs(widget.X - x) < 0.01 && Math.Abs(widget.Y - y) < 0.01,
                "applying a preset never moves the widget");
            item.Check(widget.IsVisible == visible && widget.IsLocked == locked,
                "applying a preset never changes visibility / lock state");
            item.Check(widget.SettingsJson == settingsJson,
                "applying a preset never touches the plugin's private settings");

            // ── 5. 实时预览：预设套用后活的插件组件窗口跟着变 ──
            var liveWindow = pluginSvc.GetWindow(widget.Id);
            if (liveWindow == null)
            {
                pluginSvc.ShowWidget(widget);
                await Task.Delay(400);
                liveWindow = pluginSvc.GetWindow(widget.Id);
            }
            item.Check(liveWindow != null, "the plugin widget window is alive for the live preview check");
            if (liveWindow != null)
            {
                item.Check(Math.Abs(liveWindow.Opacity - 0.8) < 0.02,
                    $"live preview applies the preset opacity (actual {liveWindow.Opacity:0.00})");
                item.Check(Math.Abs(liveWindow.Width - 420) < 2,
                    $"live preview applies the preset width (actual {liveWindow.Width:0})");
            }

            // ── 6. 预设文件隔离：插件组件预设与内置 Zone 预设互不串台 ──
            item.Check(!string.Equals(widgetPresets.Folder, zonePresets.Folder, StringComparison.OrdinalIgnoreCase),
                "plugin-widget presets and zone presets use different folders");
            var zonePreset = zonePresets.Save("Lab 分区隔离", new Zone { Name = "Lab 隔离分区", FillColor = "#11223344" });
            try
            {
                item.Check(zonePreset.Kind == PresetKind.Zone, "the built-in zone preset keeps its kind");
                item.Check(!zonePresets.LoadAll().Any(p => p.Id == saved.Id),
                    "a plugin-widget preset never shows up in zone presets");
                item.Check(!widgetPresets.LoadAll().Any(p => p.Id == zonePreset.Id),
                    "a zone preset never shows up in plugin-widget presets");
                item.Check(PresetRecord.SubFolderFor(PresetKind.PluginWidget) == "PluginWidgets"
                           && PresetRecord.SubFolderFor(PresetKind.PluginZoneBar) == "PluginZoneBars"
                           && PresetRecord.SubFolderFor(PresetKind.Zone) == "Zones",
                    "the new preset sub-folders are distinct from the built-in ones");
                var zoneJson = File.ReadAllText(Path.Combine(zonePresets.Folder, zonePreset.Id + ".json"));
                item.Check(zoneJson.Contains("\"Kind\": 0"),
                    "already-persisted zone presets keep kind 0 (appending new enum values is safe)");
            }
            finally
            {
                zonePresets.Delete(zonePreset.Id);
            }

            // ── 7. 分区坞：PropertyPanel 的 (Kind, 载荷) + 套用全链路 ──
            var zoneId = Guid.NewGuid();
            var bar = new PluginZoneBar
            {
                Name = "Bar-Raw-Name",
                Edge = ZoneBayEdges.Left,
                AutoHide = true,
                ExpandOnHover = false,
                ExpandMode = ZoneBayExpandModes.Original,
                EnableRestoreButton = false,
                FillColor = "#44FF8800",
                FillOpacity = 0.7,
                ChipFillColor = "#33FFFFFF",
                ChipOpacity = 0.6,
                IconColor = "#FF00FFFF",
                BorderColor = "#66FF0000",
                BorderThickness = 2,
                CornerRadius = 6,
                EnableLiquidGlass = true,
                GlassBlurAmount = 22,
                GlassTintOpacity = 40,
                GlassTintLuminosity = 90,
                GlassColorMode = "RosePink",
                GlassMaterial = "acrylic",
                IsCollapsed = true,
                MonitorIndex = 0,
                SettingsJson = "{\"lab\":true}",
                ZoneIds = { zoneId },
            };
            var barBefore = bar.Clone();
            var barPanel = new PropertyPanel { Target = bar };
            item.Check(barPanel.DebugPresetButtonsEnabled(),
                "load/save preset buttons are enabled for a zone bar");
            var (barKind, barPayload) = barPanel.DebugBuildCurrentPayload();
            item.Check(barKind == PresetKind.PluginZoneBar,
                $"the panel reports the PluginZoneBar preset kind (actual {barKind})");
            item.Check(barPayload is PluginZoneBarAppearance,
                "the panel payload is a zone-bar appearance snapshot");
            if (barPayload is not PluginZoneBarAppearance barSnapshot)
            {
                item.Check(false, "the zone-bar snapshot can be inspected");
                return;
            }
            item.Check(barSnapshot.Edge == ZoneBayEdges.Left && barSnapshot.AutoHide
                       && barSnapshot.GlassBlurAmount == 22 && barSnapshot.Edge == barBefore.Edge,
                "the zone-bar snapshot keeps edge / behaviour / glass parameters");
            var barSaved = barPresets.Save("Lab 分区坞预设", barPayload);
            created.Add((barPresets, barSaved.Id));
            item.Check(barSaved.Kind == PresetKind.PluginZoneBar && barSaved is PluginZoneBarPreset,
                "the saved preset is a PluginZoneBar typed record");
            item.Check(barPresets.Folder.TrimEnd('\\', '/').EndsWith("PluginZoneBars", StringComparison.Ordinal),
                $"the zone-bar preset service uses its own folder ({barPresets.Folder})");
            var barJson = File.ReadAllText(Path.Combine(barPresets.Folder, barSaved.Id + ".json"));
            item.Check(!barJson.Contains("ZoneIds") && !barJson.Contains("SettingsJson")
                       && !barJson.Contains("Bar-Raw-Name") && !barJson.Contains("MonitorIndex"),
                "a zone-bar preset never bakes held zone ids / private settings / bar name / monitor");
            item.Check(barJson.Contains("\"Kind\": 11"),
                "the serialized kind stays numeric and appended (11) for PluginZoneBar");

            // 改成另一套外观 → 套用 → 外观恢复、身份与坞内容原样。
            bar.Edge = ZoneBayEdges.Right;
            bar.AutoHide = false;
            bar.ExpandOnHover = true;
            bar.ExpandMode = ZoneBayExpandModes.Strip;
            bar.FillColor = "#00FFFFFF";
            bar.ChipFillColor = "#00000000";
            bar.BorderColor = "#00000000";
            bar.BorderThickness = 0;
            bar.CornerRadius = 0;
            bar.EnableLiquidGlass = false;
            bar.GlassBlurAmount = 1;
            bar.GlassColorMode = "Default";
            bar.GlassMaterial = "";
            bar.Name = "Lab 分区坞-改名";
            bar.IsVisible = false;
            bar.IsCollapsed = false;
            bar.ZoneIds.Clear();
            bar.SettingsJson = "{}";
            bar.MonitorIndex = 1;
            bar.SortOrder = 4;

            var barRecord = barPresets.LoadAll().FirstOrDefault();
            item.Check(barRecord != null && barRecord.Name == "Lab 分区坞预设",
                "the zone-bar preset lists with its saved name");
            if (barRecord == null) return;
            barPanel.DebugApplyCardPicked(barRecord);
            item.Check(bar.Edge == ZoneBayEdges.Left, "applying the zone-bar preset restores the snap edge");
            item.Check(bar.AutoHide && !bar.ExpandOnHover && bar.ExpandMode == ZoneBayExpandModes.Original
                       && !bar.EnableRestoreButton,
                "applying the zone-bar preset restores the behaviour switches");
            item.Check(bar.FillColor == "#44FF8800" && bar.ChipFillColor == "#33FFFFFF"
                       && bar.IconColor == "#FF00FFFF" && bar.CornerRadius == 6
                       && Math.Abs(bar.FillOpacity - 0.7) < 0.001,
                "applying the zone-bar preset restores fill / chip / icon / corner radius");
            item.Check(bar.EnableLiquidGlass && bar.GlassBlurAmount == 22 && bar.GlassColorMode == "RosePink"
                       && bar.GlassMaterial == "acrylic",
                "applying the zone-bar preset restores the liquid-glass parameters");
            item.Check(bar.Name == "Lab 分区坞-改名" && bar.IsVisible == false && bar.IsCollapsed == false,
                "applying a zone-bar preset keeps name / visibility / collapse state");
            item.Check(bar.ZoneIds.Count == 0
                       && bar.SettingsJson == "{}" && bar.MonitorIndex == 1 && bar.SortOrder == 4,
                "applying a zone-bar preset keeps held zone ids / private settings / monitor / sort order");
            item.Check(Guid.TryParse(barBefore.ZoneIds[0].ToString(), out _) && barBefore.ZoneIds.Count == 1,
                "the zone-bar snapshot itself was taken before the fields changed");

            // ── 8. 截图：加载预设对话框里的插件组件预设卡（通用插件卡 + 类型标签） ──
            var secondPayload = new PluginWidgetAppearance
            {
                Width = 360,
                Height = 200,
                CornerRadius = 18,
                FillColor = "#2233CCFF",
                BorderColor = "#88FFFFFF",
                EnableLiquidGlass = true,
                GlassBlurAmount = 26,
                GlassTintOpacity = 60,
                GlassColorMode = "AuroraCyan",
            };
            var second = widgetPresets.Save("Lab 插件组件预设 2", secondPayload);
            created.Add((widgetPresets, second.Id));
            // 第三张卡：不开玻璃、实心绿底 —— 截图里能同时看到「有玻璃色块 / 无玻璃」两种卡面。
            var thirdPayload = new PluginWidgetAppearance
            {
                Width = 300,
                Height = 180,
                CornerRadius = 4,
                FillColor = "#AA22AA55",
                BorderColor = "#33FFFFFF",
                BorderThickness = 1,
                EnableLiquidGlass = false,
                TileMode = true,
            };
            var third = widgetPresets.Save("Lab 插件组件预设 3", thirdPayload);
            created.Add((widgetPresets, third.Id));

            loadDialog = new LoadPresetDialog(widgetPresets)
            {
                Left = -6000,
                Top = -6000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            loadDialog.Show();
            await Task.Delay(700);
            loadDialog.UpdateLayout();
            item.Check(loadDialog.Title == loc["Preset.LoadTitle.PluginWidget"],
                "the load-preset dialog title is localized per kind");
            item.Check(loadDialog.DebugCardCount == 3,
                $"the load-preset dialog shows every plugin-widget preset card (actual {loadDialog.DebugCardCount})");
            item.Check(loadDialog.DebugCardUsesPluginTemplate(0),
                "plugin-widget preset cards use the shared plugin card template");
            item.Check(loadDialog.DebugCardKindLabel(0) == loc["Preset.Kind.PluginWidget"],
                "the shared card shows the localized plugin-widget type label");
            SaveScreenshot(loadDialog, Path.Combine(dir, "shot-ux-widget-presets.png"));
            item.Check(File.Exists(Path.Combine(dir, "shot-ux-widget-presets.png")),
                "plugin-widget preset card screenshot written");
        }
        finally
        {
            try { loadDialog?.Close(); } catch { }
            try
            {
                if (floating != null)
                    PropertyWindowManager.Instance.CloseWindow(floating.Target!);
            }
            catch { }
            // 还原组件外观（后面的用例 / 下次运行都从干净状态开始）。
            if (widget != null && originalWidgetAppearance != null)
            {
                try
                {
                    originalWidgetAppearance.ApplyTo(widget);
                    pluginSvc.GetWindow(widget.Id)?.RefreshAppearance();
                    pluginSvc.SaveWidgetsNow();
                }
                catch { }
            }
            foreach (var (service, presetId) in created)
            {
                try { service.Delete(presetId); } catch { }
            }
        }
    }
}
#endif
