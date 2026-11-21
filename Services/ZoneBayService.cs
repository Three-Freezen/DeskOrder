using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Views;

namespace DesktopZones.Services;

/// <summary>
/// 分区坞（Zone Bay）的宿主服务：读取坞记录、建窗、吸收/释放分区、
/// 展开/收起、拖入命中、插件生命周期对账。单例，UI 线程使用。
/// </summary>
public sealed class ZoneBayService
{
    public static ZoneBayService Instance { get; } = new();

    ZoneBayService() { }

    PluginService? _plugins;
    ZoneManager? _zones;
    ConfigService? _config;
    bool _seeded;

    readonly Dictionary<Guid, ZoneBayWindow> _windows = new();
    readonly Dictionary<Guid, Guid> _zoneToBar = new();
    readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.Ordinal);

    public ObservableCollection<PluginZoneBar> Bars { get; } = new();
    public event Action? BarsChanged;
    public event Action? SettingsChanged;
    public bool IsInitialized { get; private set; }

    // ── 初始化 / 对账 ──

    public void Initialize(PluginService plugins, ZoneManager zones, ConfigService config)
    {
        if (IsInitialized) return;
        _plugins = plugins;
        _zones = zones;
        _config = config;

        var cfg = config.Load();
        Bars.Clear();
        bool migrated = false;
        foreach (var bar in cfg.PluginZoneBars)
        {
            if (MigrateAppearance(bar)) migrated = true;
            Bars.Add(bar);
        }
        // 序号归一：历史数据里删过坞再建会出现重复 SortOrder，同边叠层会重合。
        for (int i = 0; i < Bars.Count; i++)
        {
            if (Bars[i].SortOrder != i) { Bars[i].SortOrder = i; migrated = true; }
        }
        _seeded = cfg.ZoneBaySeeded;
        RebuildMap();

        if (migrated) Save();
        if (!_seeded && TrySeedDefault())
        {
            _seeded = true;
            Save();
        }

        foreach (var bar in Bars.ToList()) CreateWindow(bar);

        zones.ZonesChanged += OnZonesChanged;
        IsInitialized = true;
        DzTrace.Always($"[ZoneBay] initialized: {Bars.Count} bar(s), {_zoneToBar.Count} zone(s)");
    }

    /// <summary>
    /// P0 的默认外观（52 高、实心深色、芯片也带底板、玻璃关）迁到原生快捷栏那套：
    /// 透明填充 + 开玻璃 + 裸图标。只认“完全还是 P0 默认值”的坞，用户动过任何一项就不碰。
    /// </summary>
    static bool MigrateAppearance(PluginZoneBar bar)
    {
        if (bar.AppearanceVersion >= 1) return false;
        bool untouched = bar.FillColor == "#E612171F"
                         && Math.Abs(bar.FillOpacity - 0.85) < 0.001
                         && bar.ChipFillColor == "#1E1E1E"
                         && Math.Abs(bar.ChipOpacity - 0.8) < 0.001
                         && bar.CornerRadius == 14
                         && !bar.EnableLiquidGlass
                         && string.IsNullOrWhiteSpace(bar.GlassMaterial);
        if (untouched)
        {
            bar.FillColor = "#08000000";
            bar.FillOpacity = 1.0;
            bar.ChipFillColor = "#00FFFFFF";
            bar.ChipOpacity = 1.0;
            bar.CornerRadius = 12;
            bar.EnableLiquidGlass = true;
            bar.GlassBlurAmount = 18;
            bar.GlassTintOpacity = 50;
            bar.GlassTintLuminosity = 100;
            bar.GlassColorMode = "Default";
            bar.GlassMaterial = "";
        }
        bar.AppearanceVersion = 1;
        return true;
    }

    bool TrySeedDefault()
    {
        var loaded = _plugins?.Loaded.FirstOrDefault(p =>
            p.Record.KindValues.Contains(PluginKind.ZoneBar) && p.Record.Enabled && p.BayRegistry.Descriptors.Count > 0);
        if (loaded == null) return false;
        var descriptor = loaded.BayRegistry.Descriptors.Values.First();
        var bar = CreateBar(loaded, descriptor);
        Bars.Add(bar);
        CreateWindow(bar);
        return true;
    }

    public PluginZoneBar CreateBar(LoadedPlugin plugin, PluginZoneBayDescriptor descriptor)
    {
        var d = descriptor.Defaults;
        var bar = new PluginZoneBar
        {
            PluginId = plugin.Record.Id,
            BarTypeId = descriptor.Id,
            Name = descriptor.DisplayName,
            Edge = ZoneBayEdges.Normalize(d.Edge),
            AutoHide = d.AutoHide,
            ExpandOnHover = d.ExpandOnHover,
            ExpandMode = ZoneBayExpandModes.Normalize(d.ExpandMode),
            EnableRestoreButton = d.EnableRestoreButton,
            FillColor = d.FillColor,
            FillOpacity = d.FillOpacity,
            ChipFillColor = d.ChipFillColor,
            ChipOpacity = d.ChipOpacity,
            IconColor = d.IconColor,
            BorderColor = d.BorderColor,
            BorderThickness = d.BorderThickness,
            CornerRadius = d.CornerRadius,
            EnableLiquidGlass = d.EnableLiquidGlass,
            GlassBlurAmount = d.GlassBlurAmount,
            GlassTintOpacity = d.GlassTintOpacity,
            GlassTintLuminosity = d.GlassTintLuminosity,
            GlassColorMode = d.GlassColorMode,
            GlassMaterial = d.GlassMaterial,
            AppearanceVersion = 1,
            // 同边叠层按 SortOrder 排：删过坞之后 Bars.Count 会复用，必须取「当前最大值 + 1」，
            // 否则新旧两条坞拿到同一个序号、两层叠在同一个位置。
            SortOrder = Bars.Count == 0 ? 0 : Bars.Max(b => b.SortOrder) + 1
        };
        return bar;
    }

    void CreateWindow(PluginZoneBar bar)
    {
        if (_windows.ContainsKey(bar.Id)) return;
        var descriptor = FindDescriptor(bar);
        if (descriptor == null)
        {
            DzTrace.Always($"[ZoneBay] descriptor missing for {bar.PluginId}/{bar.BarTypeId}; releasing zones");
            ReleaseAllFor(bar, showCollapsed: true);
            return;
        }

        var window = new ZoneBayWindow(bar, this, descriptor);
        window.Closed += (_, _) => _windows.Remove(bar.Id);
        _windows[bar.Id] = window;
        if (bar.IsVisible) window.ShowBar();
    }

    PluginZoneBayDescriptor? FindDescriptor(PluginZoneBar bar)
    {
        var loaded = _plugins?.GetLoaded(bar.PluginId);
        if (loaded == null) return null;
        return loaded.BayRegistry.Descriptors.TryGetValue(bar.BarTypeId, out var d) ? d : null;
    }

    public bool PluginHasBars(string pluginId) =>
        Bars.Any(b => string.Equals(b.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));

    public bool HasAnyZoneBayPlugin =>
        _plugins?.Loaded.Any(p => p.Record.KindValues.Contains(PluginKind.ZoneBar)
                                 && p.Record.Enabled
                                 && p.BayRegistry.Descriptors.Count > 0) == true;

    /// <summary>页面「新建」：用第一个可用的 zonebar 插件建一条默认坞。</summary>
    public PluginZoneBar? CreateBarForFirstPlugin()
    {
        var loaded = _plugins?.Loaded.FirstOrDefault(p =>
            p.Record.KindValues.Contains(PluginKind.ZoneBar) && p.Record.Enabled && p.BayRegistry.Descriptors.Count > 0);
        if (loaded == null) return null;
        var descriptor = loaded.BayRegistry.Descriptors.Values.First();
        var bar = CreateBar(loaded, descriptor);
        Bars.Add(bar);
        _seeded = true;
        CreateWindow(bar);
        // 同边多层：新坞会让同边已有坞各让一层，全部重排一次。
        RefreshLayoutAll();
        Save();
        BarsChanged?.Invoke();
        return bar;
    }

    /// <summary>同边/显示器变化、增删坞之后：让所有坞重新算一遍贴边几何（含叠层）。</summary>
    void RefreshLayoutAll(Guid? except = null)
    {
        foreach (var kv in _windows.ToList())
        {
            if (except.HasValue && kv.Key == except.Value) continue;
            try { kv.Value.ApplyEverything(); } catch (Exception ex) { DzTrace.Always("[ZoneBay] relayout: " + ex.Message); }
        }
    }

    void RebuildMap()
    {
        _zoneToBar.Clear();
        foreach (var bar in Bars)
            foreach (var id in bar.ZoneIds)
                if (!_zoneToBar.ContainsKey(id)) _zoneToBar[id] = bar.Id;
    }

    void OnZonesChanged()
    {
        if (!IsInitialized) return;
        bool changed = false;
        var zoneIds = _zones?.Zones.Select(z => z.Id).ToHashSet() ?? new HashSet<Guid>();
        foreach (var bar in Bars)
        {
            int removed = bar.ZoneIds.RemoveAll(id => !zoneIds.Contains(id));
            if (removed > 0) changed = true;
        }
        RebuildMap();
        if (changed) Save();
        foreach (var window in _windows.Values.ToList())
        {
            try { window.RefreshChips(); } catch (Exception ex) { DzTrace.Always("[ZoneBay] refresh chips: " + ex.Message); }
        }
        BarsChanged?.Invoke();
    }

    // ── 窗口 / 设置 ──

    public ZoneBayWindow? GetWindow(Guid barId) =>
        _windows.TryGetValue(barId, out var w) ? w : null;

    public void ApplySettings(PluginZoneBar bar)
    {
        ClearIconCache();
        if (_windows.TryGetValue(bar.Id, out var window))
        {
            window.Title = string.IsNullOrWhiteSpace(bar.Name) ? "Zone Bay" : bar.Name;
            window.ApplyEverything();
        }
        // 边/显示器/叠层变化会牵动同边的其它坞。
        RefreshLayoutAll(except: bar.Id);
        SettingsChanged?.Invoke();
        BarsChanged?.Invoke();
    }

    public void SetVisible(PluginZoneBar bar, bool visible)
    {
        bar.IsVisible = visible;
        if (_windows.TryGetValue(bar.Id, out var window))
        {
            if (visible) window.ShowBar();
            else window.HideBar();
        }
        Save();
        BarsChanged?.Invoke();
    }

    public void CollapseBar(PluginZoneBar bar)
    {
        bar.IsCollapsed = true;
        if (_windows.TryGetValue(bar.Id, out var window)) window.ApplyCollapsed();
        Save();
    }

    public void RestoreBar(PluginZoneBar bar)
    {
        bar.IsCollapsed = false;
        if (_windows.TryGetValue(bar.Id, out var window))
        {
            window.ApplyCollapsed();
            if (bar.IsVisible) window.ShowBar();
        }
        Save();
    }

    public void DeleteBar(PluginZoneBar bar)
    {
        foreach (var id in bar.ZoneIds.ToList()) Release(id, showCollapsed: true);
        if (_windows.Remove(bar.Id, out var window))
        {
            try { window.Close(); } catch { }
        }
        // 属性面板（浮动/停靠）里可能正开着这条坞的编辑器 —— 删了就别留着改死对象。
        try { PropertyWindowService.CloseEditorsFor(bar); } catch { }
        Bars.Remove(bar);
        RefreshLayoutAll();
        Save();
        BarsChanged?.Invoke();
    }

    // ── 吸收 / 释放 ──

    public bool IsAbsorbed(Guid zoneId) => _zoneToBar.ContainsKey(zoneId);

    public PluginZoneBar? BarOf(Guid zoneId) =>
        _zoneToBar.TryGetValue(zoneId, out var barId)
            ? Bars.FirstOrDefault(b => b.Id == barId)
            : null;

    public bool Absorb(Guid zoneId, Guid barId, ZoneBaySourceKind source, out string? error)
    {
        error = null;
        if (source != ZoneBaySourceKind.ZoneRestore)
        {
            error = "only a minimized zone restore button can be absorbed";
            return false;
        }
        var bar = Bars.FirstOrDefault(b => b.Id == barId);
        if (bar == null) { error = "zone bay not found"; return false; }
        var zone = _zones?.Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone == null) { error = "zone not found"; return false; }
        if (!zone.EnableRestoreButton) { error = "zone has no restore button"; return false; }
        if (_zoneToBar.ContainsKey(zoneId)) { error = "zone is already in a zone bay"; return false; }

        zone.IsVisible = false;
        bar.ZoneIds.Add(zoneId);
        _zoneToBar[zoneId] = bar.Id;

        if (_zones != null)
        {
            var win = _zones.GetZoneWindow(zoneId);
            if (win != null)
            {
                _zones.ForgetZoneWindow(zoneId);
                try { win.Close(); } catch { }
            }
            _zones.FireZoneVisibilityChanged(zoneId, false);
        }

        Save();
        RefreshWindow(bar);
        BarsChanged?.Invoke();
        try { _plugins?.Notify(bar.Name, LocalizationService.Instance.Get("ZoneBay.Toast.Absorbed", zone.Name)); } catch { }
        return true;
    }

    public bool TryAbsorb(Zone zone, Point screenPx, out string? error)
    {
        error = null;
        if (!HitTest(screenPx, out var bar) || bar == null) return false;
        return Absorb(zone.Id, bar.Id, ZoneBaySourceKind.ZoneRestore, out error);
    }

    public void Release(Guid zoneId, bool showCollapsed = true)
    {
        var bar = BarOf(zoneId);
        if (bar == null) return;
        bar.ZoneIds.Remove(zoneId);
        _zoneToBar.Remove(zoneId);

        var zone = _zones?.Zones.FirstOrDefault(z => z.Id == zoneId);
        if (zone != null)
        {
            zone.IsVisible = false;
            if (_zones != null)
            {
                if (_windows.TryGetValue(bar.Id, out var bw)) bw.Collapse(zoneId.ToString(), animate: false);
                var win = _zones.GetZoneWindow(zoneId);
                if (win != null)
                {
                    _zones.ForgetZoneWindow(zoneId);
                    try { win.Close(); } catch { }
                }

                if (showCollapsed && zone.EnableRestoreButton)
                    _zones.CreateCollapsedWindow(zone);
                else if (showCollapsed)
                    _zones.FullHideZone(zoneId);
                // showCollapsed=false：调用方（FullHideZone）接着关，避免递归。
                _zones.FireZoneVisibilityChanged(zoneId, false);
            }
        }

        Save();
        RefreshWindow(bar);
        BarsChanged?.Invoke();
        try { _plugins?.Notify(bar.Name, LocalizationService.Instance.Get("ZoneBay.Toast.Released", zone?.Name ?? "")); } catch { }
    }

    void ReleaseAllFor(PluginZoneBar bar, bool showCollapsed)
    {
        foreach (var id in bar.ZoneIds.ToList()) Release(id, showCollapsed);
    }

    void RefreshWindow(PluginZoneBar bar)
    {
        if (_windows.TryGetValue(bar.Id, out var window))
        {
            try { window.RefreshChips(); } catch (Exception ex) { DzTrace.Always("[ZoneBay] refresh: " + ex.Message); }
        }
    }

    // ── 展开 / 收起 ──

    public void ExpandInBay(Guid zoneId)
    {
        var bar = BarOf(zoneId);
        if (bar == null) return;
        bar.IsVisible = true;
        if (_windows.TryGetValue(bar.Id, out var window))
        {
            window.ShowBar();
            window.Expand(zoneId.ToString(), null);
        }
    }

    public void CollapseInBay(Guid zoneId)
    {
        var bar = BarOf(zoneId);
        if (bar == null) return;
        if (_windows.TryGetValue(bar.Id, out var window)) window.Collapse(zoneId.ToString());
    }

    public void CollapseAll()
    {
        foreach (var window in _windows.Values.ToList())
        {
            try { window.CollapseAll(); } catch { }
        }
    }

    public void ShowAll()
    {
        foreach (var bar in Bars.ToList())
        {
            if (!bar.IsVisible) SetVisible(bar, true);
            else if (_windows.TryGetValue(bar.Id, out var window)) window.ShowBar();
        }
    }

    public void HideAll()
    {
        foreach (var bar in Bars.ToList())
        {
            if (bar.IsVisible) SetVisible(bar, false);
        }
    }

    // ── 拖入命中 ──

    Guid? _dragTarget;
    public void DragMoved(Point screenPx)
    {
        if (!HitTest(screenPx, out var bar) || bar == null)
        {
            if (_dragTarget.HasValue && _windows.TryGetValue(_dragTarget.Value, out var old))
            {
                old.PeekForDrag(false);
                old.SetDropHighlight(false);
            }
            _dragTarget = null;
            return;
        }
        if (_dragTarget == bar.Id) return;
        if (_dragTarget.HasValue && _windows.TryGetValue(_dragTarget.Value, out var prev))
        {
            prev.PeekForDrag(false);
            prev.SetDropHighlight(false);
        }
        _dragTarget = bar.Id;
        if (_windows.TryGetValue(bar.Id, out var window))
        {
            window.PeekForDrag(true);
            window.SetDropHighlight(true);
        }
    }

    public void EndDrag()
    {
        if (_dragTarget.HasValue && _windows.TryGetValue(_dragTarget.Value, out var window))
        {
            window.PeekForDrag(false);
            window.SetDropHighlight(false);
        }
        _dragTarget = null;
    }

    public bool HitTest(Point screenPx, out PluginZoneBar? bar)
    {
        bar = null;
        foreach (var b in Bars)
        {
            if (!b.IsVisible) continue;
            if (_windows.TryGetValue(b.Id, out var window) && window.HitTestScreen(screenPx))
            {
                bar = b;
                return true;
            }
        }
        return false;
    }

    // ── 芯片数据 / 图标 ──

    public IReadOnlyList<PluginZoneChip> BuildChips(PluginZoneBar bar)
    {
        var list = new List<PluginZoneChip>();
        if (_zones == null) return list;
        foreach (var id in bar.ZoneIds)
        {
            var zone = _zones.Zones.FirstOrDefault(z => z.Id == id);
            if (zone == null) continue;
            list.Add(new PluginZoneChip
            {
                ZoneId = zone.Id.ToString(),
                Name = ChipName(zone),
                Icon = BuildIcon(bar, zone),
                IsMerged = zone.MergedGroupMembership.SubZoneIds.Count > 0,
                MemberCount = zone.MergedGroupMembership.SubZoneIds.Count + 1
            });
        }
        return list;
    }

    public static string ChipName(Zone zone)
    {
        if (zone.MergedGroupMembership.SubZoneIds.Count > 0
            && !string.IsNullOrWhiteSpace(zone.MergedGroupMembership.DisplayName))
            return zone.MergedGroupMembership.DisplayName;
        return zone.Name;
    }

    public Zone? FindZone(string zoneId) =>
        Guid.TryParse(zoneId, out var id) ? _zones?.Zones.FirstOrDefault(z => z.Id == id) : null;

    public ZoneWindow? GetOrCreateZoneWindow(Zone zone) => _zones?.GetOrCreateWindow(zone);

    // ── 芯片顺序 / 拖出恢复 / 右键菜单（P2）──

    /// <summary>坞内芯片换位（拖动排序）。<paramref name="to"/> 是最终下标。</summary>
    public void MoveChip(PluginZoneBar bar, int from, int to)
    {
        if (from < 0 || from >= bar.ZoneIds.Count) return;
        to = Math.Clamp(to, 0, bar.ZoneIds.Count - 1);
        if (from == to) return;
        var id = bar.ZoneIds[from];
        bar.ZoneIds.RemoveAt(from);
        bar.ZoneIds.Insert(to, id);
        Save();
        RefreshWindow(bar);
        BarsChanged?.Invoke();
    }

    /// <summary>把芯片拖到坞外：在落点处恢复成桌面的 36×36 恢复按钮（分区不会丢）。</summary>
    public void ReleaseToDesktop(string zoneId, Point screenDip)
    {
        if (!Guid.TryParse(zoneId, out var id)) return;
        var zone = FindZone(zoneId);
        if (zone == null) return;
        // 36×36 的按钮中心对准光标。
        zone.X = screenDip.X - 18;
        zone.Y = screenDip.Y - 18;
        Release(id, showCollapsed: true);
    }

    /// <summary>芯片右键：打开**这个分区自己的**原生右键菜单（不是坞菜单）。
    /// 分区被吸进坞时它的 ZoneWindow 通常已经关掉，这里按需借一个窗口当菜单宿主，
    /// 菜单关闭后如果那个窗口只是为菜单而建，就立刻回收。</summary>
    public bool OpenZoneMenu(string zoneId, FrameworkElement? anchor)
    {
        var zone = FindZone(zoneId);
        if (zone == null || _zones == null) return false;
        bool existed = _zones.GetZoneWindow(zone.Id) != null;
        var window = _zones.GetOrCreateWindow(zone);
        if (window == null) return false;
        // 芯片菜单的定位锚点默认用调用方给的那颗芯片；没给时用坞窗口本身 ——
        // 不能拿隐藏 ZoneWindow 里的元素当锚点（没有 PresentationSource，菜单弹不出来、
        // 也不会触发 Closed，宿主窗口就回收不掉）。
        var anchorElement = anchor;
        if (anchorElement == null)
        {
            var bar = BarOf(zone.Id);
            if (bar != null && _windows.TryGetValue(bar.Id, out var bayWindow))
                anchorElement = bayWindow.MenuAnchor;
        }
        window.OpenBayChipMenu(anchorElement, () =>
        {
            if (existed) return;
            // 只为菜单建的窗口：菜单一关就摘掉（否则会多出一个隐藏窗口 + 订阅）。
            if (_zones == null) return;
            _zones.ForgetZoneWindow(zone.Id);
            try { window.Close(); } catch { }
        });
        return true;
    }

    /// <summary>同边叠放：这条坞在第几层（0 = 贴边）。同边 + 同显示器、且排在它前面的可见坞各占一层。</summary>
    public int LaneOf(PluginZoneBar bar)
    {
        if (!bar.IsVisible || bar.IsCollapsed) return 0;
        int lane = 0;
        int monitor = NormalizeMonitor(bar);
        foreach (var other in Bars)
        {
            if (ReferenceEquals(other, bar) || other.Id == bar.Id) continue;
            if (!other.IsVisible || other.IsCollapsed) continue;
            if (other.NormalizedEdge != bar.NormalizedEdge) continue;
            if (NormalizeMonitor(other) != monitor) continue;
            if (other.SortOrder < bar.SortOrder) lane++;
        }
        return lane;
    }

    /// <summary>显示器序号规整：-1 / 越界 = 主显示器（0）。</summary>
    public static int NormalizeMonitor(PluginZoneBar bar)
    {
        int count = Math.Max(1, MonitorHelper.MonitorCount());
        return bar.MonitorIndex >= 0 && bar.MonitorIndex < count ? bar.MonitorIndex : 0;
    }

    public ImageSource BuildIcon(PluginZoneBar bar, Zone zone)
    {
        var color = ResolveIconColor(bar, zone);
        var icon = ResolveIconText(zone);
        var key = $"{zone.Id}|{icon}|{color}";
        if (_iconCache.TryGetValue(key, out var cached)) return cached;
        var image = RenderIcon(icon, color);
        _iconCache[key] = image;
        return image;
    }

    public void ClearIconCache() => _iconCache.Clear();

    static string ResolveIconText(Zone zone)
    {
        bool merged = zone.MergedGroupMembership.SubZoneIds.Count > 0;
        var icon = merged && !string.IsNullOrWhiteSpace(zone.MergedGroupMembership.Icon)
            ? zone.MergedGroupMembership.Icon
            : zone.IconChar;
        if (string.IsNullOrWhiteSpace(icon))
            icon = merged ? IconGlyph.Merged : IconGlyph.Zones;
        return icon!;
    }

    static string ResolveIconColor(PluginZoneBar bar, Zone zone)
    {
        if (!string.IsNullOrWhiteSpace(bar.IconColor)) return bar.IconColor;
        if (!string.IsNullOrWhiteSpace(zone.IconColor)) return zone.IconColor;
        if (!string.IsNullOrWhiteSpace(zone.TitleTextColor)) return zone.TitleTextColor;
        return "#FFFFFFFF";
    }

    static ImageSource RenderIcon(string icon, string colorHex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex)!;
            var brush = new SolidColorBrush(color);
            const double size = 32;
            var host = new Grid { Width = size, Height = size, Background = Brushes.Transparent };
            var tb = new TextBlock
            {
                FontSize = 26,
                FontFamily = new FontFamily("Segoe UI Emoji, Microsoft YaHei UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            var path = new Path
            {
                Width = 26,
                Height = 26,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.7,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            IconGlyph.Apply(tb, path, icon, brush, 26);
            host.Children.Add(tb);
            host.Children.Add(path);
            host.Measure(new Size(size, size));
            host.Arrange(new Rect(0, 0, size, size));
            host.UpdateLayout();

            var rtb = new RenderTargetBitmap(64, 64, 192, 192, PixelFormats.Pbgra32);
            rtb.Render(host);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            DzTrace.Always("[ZoneBay] icon render failed: " + ex.Message);
            var bmp = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null,
                new byte[] { 0, 0, 0, 0 }, 4);
            bmp.Freeze();
            return bmp;
        }
    }

    // ── 插件生命周期 ──

    public void OnPluginLoaded(LoadedPlugin plugin)
    {
        if (!IsInitialized) return;
        if (!plugin.Record.KindValues.Contains(PluginKind.ZoneBar)) return;
        foreach (var bar in Bars.Where(b => string.Equals(b.PluginId, plugin.Record.Id, StringComparison.OrdinalIgnoreCase)))
            CreateWindow(bar);
        BarsChanged?.Invoke();
    }

    public void OnPluginDisabling(string pluginId)
    {
        if (!IsInitialized) return;
        foreach (var bar in Bars.Where(b =>
                     string.Equals(b.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            CollapseAll();
            ReleaseAllFor(bar, showCollapsed: true);
            if (_windows.Remove(bar.Id, out var window))
            {
                try { window.Close(); } catch { }
            }
            try { PropertyWindowService.CloseEditorsFor(bar); } catch { }
        }
        Save();
        BarsChanged?.Invoke();
    }

    public void OnPluginsChanged()
    {
        if (!IsInitialized) return;
        var alive = _plugins?.Loaded.Select(p => p.Record.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var bar in Bars.Where(b => !alive.Contains(b.PluginId)).ToList())
            OnPluginDisabling(bar.PluginId);
        BarsChanged?.Invoke();
    }

    /// <summary>卸载插件后把坞记录也删掉（分区已在 OnPluginDisabling 里释放）。</summary>
    public void RemoveBarsForPlugin(string pluginId)
    {
        foreach (var bar in Bars.Where(b =>
                     string.Equals(b.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            DeleteBar(bar);
        }
        if (Bars.Count == 0) _seeded = false;
        Save();
    }

    public void Shutdown()
    {
        foreach (var window in _windows.Values.ToList())
        {
            try { window.CollapseAll(); window.Close(); } catch { }
        }
        _windows.Clear();
        if (_zones != null) _zones.ZonesChanged -= OnZonesChanged;
        IsInitialized = false;
    }

    // ── 设置 / 持久化 ──

    public void Save()
    {
        if (_config == null) return;
        try
        {
            ConfigSaver.SavePreservingPanelSettings(_config, cfg =>
            {
                cfg.PluginZoneBars = Bars.ToList();
                cfg.ZoneBaySeeded = _seeded;
            });
        }
        catch (Exception ex)
        {
            DzTrace.Always("[ZoneBay] save failed: " + ex.Message);
        }
    }

    /// <summary>坞右键「设置」→ 原生属性浮窗（与分区/挂件同一个 PropertyWindow）。
    /// 管理页的行点击走停靠面板（<see cref="Views.Components.PropertyWindowManager.DockTarget"/>）。</summary>
    public void OpenSettings(PluginZoneBar bar)
    {
        try { PropertyWindowService.OpenOrFocus(bar); }
        catch (Exception ex) { DzTrace.Always("[ZoneBay] settings: " + ex.Message); }
    }

    public void OpenManagement(PluginZoneBar bar)
    {
        try
        {
            if (Application.Current is App app)
            {
                app.EnsureManagementWindow();
                app.ManagementWindow?.ShowSection("zonebays");
            }
        }
        catch (Exception ex) { DzTrace.Always("[ZoneBay] management: " + ex.Message); }
    }
}
