using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

public class Zone : AppearanceModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New Zone";
    public double X { get; set; } = 100;
    public double Y { get; set; } = 100;
    public double Width { get; set; } = 400;
    public double Height { get; set; } = 300;
    public double BorderThickness { get; set; } = 1.5; // Zone-specific default (widgets use 1.0)
    public int CornerRadius { get; set; } = 8;
    public bool IsVisible { get; set; } = true;
    public bool IsLocked { get; set; } = false;
    public int GridSize { get; set; } = 56;
    public bool SnapToGrid { get; set; } = true;
    public string IconChar { get; set; } = "";
    public string TitleBarFillColor { get; set; } = "#10FFFFFF"; // title bar background
    public double ControlOpacity { get; set; } = 40;
    public double BackgroundImageOpacity { get; set; } = 40; // Zone-specific default (widgets use 30)
    public bool AutoArrange { get; set; } = true;
    public string IconColor { get; set; } = "";              // emoji tint color
    public string TitleTextColor { get; set; } = "#A0FFFFFF";
    /// <summary>磁贴模式 — 隐藏标题栏与底部 8px 分割条，主体作为一块完整窗口。</summary>
    public bool TileMode { get; set; } = false;
    /// <summary>隐藏应用名 — 磁贴模式下默认隐藏；非磁贴模式下也可手动勾选。</summary>
    public bool HideAppName { get; set; } = false;
    /// <summary>自定义图标 — 单图标模式，需 TileMode=true 且 Items.Count&lt;=1 才能启用。</summary>
    public bool CustomIcon { get; set; } = false;
    private List<ZoneItem> _items = new();
    public List<ZoneItem> Items
    {
        get => _items;
        set => _items = value ?? new();
    }

    // ── Merge / group (group identity + style overrides; lifted out of Zone
    //    to flatten the god class — see bug report item #14) ──
    public MergedGroupMembership MergedGroupMembership { get; set; } = new();
    public MergedGroupStyle MergedGroupStyle { get; set; } = new();

    /// <summary>
    /// Captures unknown JSON fields. Used by <c>PresetService</c> for one-time
    /// migration of legacy flat MergedGroup* fields into MergedGroupStyle /
    /// MergedGroupMembership. Cleared after migration.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>配置兼容 — 旧 config 用 "QuickBarMode" 字段名保存磁贴/极简模式，
    /// 重命名后通过扩展数据回填到 TileMode。</summary>
    [OnDeserialized]
    internal void OnDeserializedAfterRename(StreamingContext _)
    {
        if (ExtensionData == null) return;
        if (ExtensionData.TryGetValue("QuickBarMode", out var old)
            && old.ValueKind is JsonValueKind.True or JsonValueKind.False)
            TileMode = old.GetBoolean();
    }

    /// <summary>标题栏按钮颜色 — 替代原「标题栏文字颜色自适应」的固定色。默认匹配 XAML 按钮默认 #80FFFFFF。</summary>
    public string ButtonColor { get; set; } = "#80FFFFFF";
    /// <summary>主体内容颜色 — 替代原「主体内容颜色自适应」的固定色（分区图标/应用名）。默认匹配 XAML 项名 #E0FFFFFF。</summary>
    public string TextColor { get; set; } = "#E0FFFFFF";
    /// <summary>标题栏填充单独设置 — 勾选后主体填充(FillColor)不再铺到标题栏下方。</summary>
    public bool TitleBarFillIndependent { get; set; } = false;

    // ── Folder mapping ──
    /// <summary>文件夹映射 — 启用后分区内容区展示映射文件夹/磁盘的内容（双写 MergedGroupStyle，
    /// 组合分区解散后映射跟随保留）。</summary>
    public bool FolderMappingEnabled { get; set; } = false;
    public string FolderMappingPath { get; set; } = "";

    // ── Auto-organize ──
    /// <summary>自动整理扩展名集合（含预设 + 用户自定义，统一存这里）。小写、含前导点。</summary>
    [JsonPropertyName("autoOrganizeExtensions")]
    public List<string> AutoOrganizeExtensions { get; set; } = new();

    /// <summary>文件名要素集合（全用户自定义）。子串匹配，case-insensitive。</summary>
    [JsonPropertyName("autoOrganizeNameTokens")]
    public List<string> AutoOrganizeNameTokens { get; set; } = new();

    /// <summary>监听路径。空字符串 = 未配置；新建分区默认 %USERPROFILE%\Desktop。</summary>
    [JsonPropertyName("autoOrganizeWatchPath")]
    public string AutoOrganizeWatchPath { get; set; } = "";

    /// <summary>监听开关（用户显式控制）。勾选 = 启用 watcher，取消 = 暂停但保留规则。
    /// 首次添加规则时自动勾选。序列化持久化，方便下次快速开启。</summary>
    [JsonPropertyName("autoOrganizeWatching")]
    public bool AutoOrganizeWatching { get; set; } = false;

    /// <summary>扩展名规则子开关。取消 = 保留扩展名列表但不参与匹配。</summary>
    [JsonPropertyName("autoOrganizeExtEnabled")]
    public bool AutoOrganizeExtEnabled { get; set; } = true;

    /// <summary>文件名要素规则子开关。取消 = 保留要素列表但不参与匹配。</summary>
    [JsonPropertyName("autoOrganizeNameEnabled")]
    public bool AutoOrganizeNameEnabled { get; set; } = true;

    /// <summary>派生属性：是否已有规则（后缀或文件名要素非空）。序列化忽略。</summary>
    [JsonIgnore]
    public bool AutoOrganizeEnabled =>
        AutoOrganizeExtensions.Count > 0 || AutoOrganizeNameTokens.Count > 0;

    // Display state — not persisted
    [JsonIgnore]
    public bool IsEditing { get; set; }

    public Zone Clone()
    {
        var copy = new Zone
        {
            Id = Id,
            Name = Name,
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
            BorderThickness = BorderThickness,
            CornerRadius = CornerRadius,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            GridSize = GridSize,
            SnapToGrid = SnapToGrid,
            IconChar = IconChar,
            TitleBarFillColor = TitleBarFillColor,
            ControlOpacity = ControlOpacity,
            BackgroundImageOpacity = BackgroundImageOpacity,
            AutoArrange = AutoArrange,
            IconColor = IconColor,
            TitleTextColor = TitleTextColor,
            TileMode = TileMode,
            HideAppName = HideAppName,
            CustomIcon = CustomIcon,
            TitleBarFillIndependent = TitleBarFillIndependent,
            FolderMappingEnabled = FolderMappingEnabled,
            FolderMappingPath = FolderMappingPath,
            AutoOrganizeExtensions = new List<string>(AutoOrganizeExtensions),
            AutoOrganizeNameTokens = new List<string>(AutoOrganizeNameTokens),
            AutoOrganizeWatchPath = AutoOrganizeWatchPath,
            AutoOrganizeWatching = AutoOrganizeWatching,
            AutoOrganizeExtEnabled = AutoOrganizeExtEnabled,
            AutoOrganizeNameEnabled = AutoOrganizeNameEnabled,
            MergedGroupMembership = new MergedGroupMembership
            {
                GroupId = MergedGroupMembership.GroupId,
                SubZoneIds = new List<Guid>(MergedGroupMembership.SubZoneIds),
                TabOrder = new List<Guid>(MergedGroupMembership.TabOrder),
                DisplayName = MergedGroupMembership.DisplayName,
                Icon = MergedGroupMembership.Icon,
            },
            MergedGroupStyle = new MergedGroupStyle
            {
                BorderColor = MergedGroupStyle.BorderColor,
                BorderThickness = MergedGroupStyle.BorderThickness,
                CornerRadius = MergedGroupStyle.CornerRadius,
                FillColor = MergedGroupStyle.FillColor,
                TitleBarFillColor = MergedGroupStyle.TitleBarFillColor,
                TitleTextColor = MergedGroupStyle.TitleTextColor,
                IconColor = MergedGroupStyle.IconColor,
                ControlOpacity = MergedGroupStyle.ControlOpacity,
                TitleBarOpacity = MergedGroupStyle.TitleBarOpacity,
                UseUnifiedFill = MergedGroupStyle.UseUnifiedFill,
                TileMode = MergedGroupStyle.TileMode,
                ButtonColor = MergedGroupStyle.ButtonColor,
                TextColor = MergedGroupStyle.TextColor,
                TitleBarFillIndependent = MergedGroupStyle.TitleBarFillIndependent,
                BackgroundImagePath = MergedGroupStyle.BackgroundImagePath,
                BgImageStretch = MergedGroupStyle.BgImageStretch,
                BgImageOffsetX = MergedGroupStyle.BgImageOffsetX,
                BgImageOffsetY = MergedGroupStyle.BgImageOffsetY,
                BgImageZoom = MergedGroupStyle.BgImageZoom,
                BackgroundImageOpacity = MergedGroupStyle.BackgroundImageOpacity,
                FolderMappingEnabled = MergedGroupStyle.FolderMappingEnabled,
                FolderMappingPath = MergedGroupStyle.FolderMappingPath,
            },
            ButtonColor = ButtonColor,
            TextColor = TextColor,
            Items = new List<ZoneItem>(Items.ConvertAll(i => i.Clone()))
        };
        // Ponytail: 14 AppearanceModel fields auto-copied via reflection so
        // adding a new one to the base class doesn't silently miss here.
        Helpers.CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
}