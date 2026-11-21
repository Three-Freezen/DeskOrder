using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskOrder.PluginApi;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.Views.Components;

/// <summary>
/// 「新建插件组件」菜单。市场页「已安装」行与「插件组件」管理页共用同一实现：
/// 每次点击现场构建（插件/描述符随时可能因加载、禁用、卸载变化），列出一个
/// 「插件显示名 · 描述符.DisplayName」条目，选中即调
/// <see cref="PluginService.CreateWidget"/> 建实例。
/// </summary>
public static class PluginWidgetCreateMenu
{
    /// <summary>当前有没有可新建的组件（有已加载插件注册了至少一种组件）。</summary>
    public static bool HasAvailable => PluginService.Instance.GetWidgetPlugins().Count > 0;

    /// <summary>
    /// 有已启用的 widget 插件、但没有任何已加载的描述符 —— 典型是「刚装上/刚启用，
    /// 还没重启」，此时按钮要禁用并提示重启，而不是显示「没有插件」。
    /// </summary>
    public static bool NeedsRestart
        => !HasAvailable
           && PluginService.Instance.Records.Any(r => r.Enabled && r.KindValues.Contains(PluginKind.Widget));

    /// <summary>
    /// 构建菜单。<paramref name="onCreated"/> 在创建成功后才回调（传创建的实例），
    /// 调用方据此刷新列表/选中新行；失败时这里自己弹错误提示。
    /// 菜单位置由调用方拿 <see cref="Open"/> 或自行设置 PlacementTarget/IsOpen。
    /// </summary>
    public static ContextMenu Build(Window? owner, Action<PluginWidget?>? onCreated)
    {
        var loc = LocalizationService.Instance;
        var menu = new ContextMenu();
        var plugins = PluginService.Instance.GetWidgetPlugins();
        if (plugins.Count == 0)
        {
            // 正常路径下调用方已经禁用了入口；这里兜底，避免点出空菜单。
            menu.Items.Add(new MenuItem
            {
                Header = loc[NeedsRestart ? "PluginWidget.NeedsRestart" : "PluginWidget.NoPlugins"],
                IsEnabled = false,
            });
            return menu;
        }

        foreach (var (record, descriptors) in plugins)
        {
            var pluginName = DescribePlugin(record);
            foreach (var descriptor in descriptors)
            {
                var item = new MenuItem { Header = pluginName + " · " + descriptor.DisplayName };
                var pluginId = record.Id;
                var widgetTypeId = descriptor.Id;
                var displayName = descriptor.DisplayName;
                item.Click += (_, _) =>
                {
                    var widget = PluginService.Instance.CreateWidget(pluginId, widgetTypeId);
                    if (widget == null)
                    {
                        MessageBox.Show(owner,
                            loc.Get("PluginWidget.CreateFailed", displayName),
                            loc["PluginWidget.Nav"],
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                    onCreated?.Invoke(widget);
                };
                menu.Items.Add(item);
            }
        }
        return menu;
    }

    /// <summary>在 <paramref name="anchor"/> 下方打开菜单（两个入口共用同一套定位）。</summary>
    public static void Open(FrameworkElement anchor, Window? owner, Action<PluginWidget?>? onCreated)
    {
        if (anchor == null) return;
        var menu = Build(owner, onCreated);
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    static string DescribePlugin(PluginRecord record)
    {
        var name = PluginService.Instance.GetLoaded(record.Id)?.Manifest.Name
            .Get(LocalizationService.Instance.CurrentLanguage);
        return string.IsNullOrWhiteSpace(name) ? record.Id : name!;
    }
}
