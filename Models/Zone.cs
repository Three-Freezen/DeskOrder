using System;
using System.Collections.Generic;
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
    public int GridSize { get; set; } = 80;
    public bool SnapToGrid { get; set; } = true;
    public string IconChar { get; set; } = "";
    public string TitleBarFillColor { get; set; } = "#10FFFFFF"; // title bar background
    public double ControlOpacity { get; set; } = 40;
    public double BackgroundImageOpacity { get; set; } = 40; // Zone-specific default (widgets use 30)
    public bool AutoArrange { get; set; } = true;
    public string IconColor { get; set; } = "";              // emoji tint color
    public string TitleTextColor { get; set; } = "#A0FFFFFF";
    public ZoneType ZoneType { get; set; } = ZoneType.Normal;
    public bool QuickBarMode { get; set; } = false;             // Title-bar-less compact mode
    private List<ZoneItem> _items = new();
    public List<ZoneItem> Items
    {
        get => _items;
        set => _items = value ?? new();
    }

    // ── Merge / group ──
    public Guid? MergedGroupId { get; set; } = null;              // non-null = part of a merged group
    private List<Guid> _mergedSubZoneIds = new();
    public List<Guid> MergedSubZoneIds
    {
        get => _mergedSubZoneIds;
        set => _mergedSubZoneIds = value ?? new();
    }
    public string MergedGroupName { get; set; } = "";             // combined display name
    public string MergedGroupIcon { get; set; } = "";             // combined icon

    // ── Merged Group Style Settings ──
    public string MergedGroupBorderColor { get; set; } = "#40FFFFFF";
    public double MergedGroupBorderThickness { get; set; } = 1.5;
    public int MergedGroupCornerRadius { get; set; } = 8;
    public string MergedGroupFillColor { get; set; } = "#08000000";
    public string MergedGroupTitleBarFillColor { get; set; } = "#10FFFFFF";
    public string MergedGroupTitleTextColor { get; set; } = "#A0FFFFFF";
    public string MergedGroupIconColor { get; set; } = "";        // emoji tint color
    public double MergedGroupControlOpacity { get; set; } = 40;
    public double MergedGroupTitleBarOpacity { get; set; } = 6;   // title bar opacity
    public bool MergedGroupUseUnifiedFill { get; set; } = true;   // true=unified fill, false=keep original
    public bool MergedGroupQuickBarMode { get; set; } = false;    // title-bar-less compact mode
    public string MergedGroupBackgroundImagePath { get; set; } = "";
    public string MergedGroupBgImageStretch { get; set; } = "UniformToFill";
    public double MergedGroupBgImageOffsetX { get; set; } = 0;
    public double MergedGroupBgImageOffsetY { get; set; } = 0;
    public double MergedGroupBgImageZoom { get; set; } = 1.0;
    public double MergedGroupBackgroundImageOpacity { get; set; } = 40;

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
            GridSize = GridSize,
            SnapToGrid = SnapToGrid,
            IconChar = IconChar,
            TitleBarFillColor = TitleBarFillColor,
            ControlOpacity = ControlOpacity,
            BackgroundImageOpacity = BackgroundImageOpacity,
            AutoArrange = AutoArrange,
            IconColor = IconColor,
            TitleTextColor = TitleTextColor,
            ZoneType = ZoneType,
            QuickBarMode = QuickBarMode,
            MergedGroupId = MergedGroupId,
            MergedSubZoneIds = new List<Guid>(MergedSubZoneIds),
            MergedGroupName = MergedGroupName,
            MergedGroupIcon = MergedGroupIcon,
            MergedGroupBorderColor = MergedGroupBorderColor,
            MergedGroupBorderThickness = MergedGroupBorderThickness,
            MergedGroupCornerRadius = MergedGroupCornerRadius,
            MergedGroupFillColor = MergedGroupFillColor,
            MergedGroupTitleBarFillColor = MergedGroupTitleBarFillColor,
            MergedGroupTitleTextColor = MergedGroupTitleTextColor,
            MergedGroupIconColor = MergedGroupIconColor,
            MergedGroupControlOpacity = MergedGroupControlOpacity,
            MergedGroupTitleBarOpacity = MergedGroupTitleBarOpacity,
            MergedGroupUseUnifiedFill = MergedGroupUseUnifiedFill,
            MergedGroupQuickBarMode = MergedGroupQuickBarMode,
            MergedGroupBackgroundImagePath = MergedGroupBackgroundImagePath,
            MergedGroupBgImageStretch = MergedGroupBgImageStretch,
            MergedGroupBgImageOffsetX = MergedGroupBgImageOffsetX,
            MergedGroupBgImageOffsetY = MergedGroupBgImageOffsetY,
            MergedGroupBgImageZoom = MergedGroupBgImageZoom,
            MergedGroupBackgroundImageOpacity = MergedGroupBackgroundImageOpacity,
            Items = new List<ZoneItem>(Items.ConvertAll(i => i.Clone()))
        };
        // Ponytail: 14 AppearanceModel fields auto-copied via reflection so
        // adding a new one to the base class doesn't silently miss here.
        Helpers.CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
}
