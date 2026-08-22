using System;
using System.Collections.Generic;
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
    public int GridSize { get; set; } = 80;
    public bool SnapToGrid { get; set; } = true;
    public string IconChar { get; set; } = "";
    public string TitleBarFillColor { get; set; } = "#10FFFFFF"; // title bar background
    public double ControlOpacity { get; set; } = 40;
    public double BackgroundImageOpacity { get; set; } = 40; // Zone-specific default (widgets use 30)
    public bool AutoArrange { get; set; } = true;
    public string IconColor { get; set; } = "";              // emoji tint color
    public string TitleTextColor { get; set; } = "#A0FFFFFF";
    public bool QuickBarMode { get; set; } = false;             // Title-bar-less compact mode
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

    // ── Title bar text color adaptive ──
    /// <summary>Auto-pick zone title bar text color based on <see cref="TitleBarFillColor"/>.</summary>
    public bool TitleBarTextColorAdaptive { get; set; } = true;

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
            QuickBarMode = QuickBarMode,
            MergedGroupMembership = new MergedGroupMembership
            {
                GroupId = MergedGroupMembership.GroupId,
                SubZoneIds = new List<Guid>(MergedGroupMembership.SubZoneIds),
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
                QuickBarMode = MergedGroupStyle.QuickBarMode,
                TitleBarTextColorAdaptive = MergedGroupStyle.TitleBarTextColorAdaptive,
                BackgroundImagePath = MergedGroupStyle.BackgroundImagePath,
                BgImageStretch = MergedGroupStyle.BgImageStretch,
                BgImageOffsetX = MergedGroupStyle.BgImageOffsetX,
                BgImageOffsetY = MergedGroupStyle.BgImageOffsetY,
                BgImageZoom = MergedGroupStyle.BgImageZoom,
                BackgroundImageOpacity = MergedGroupStyle.BackgroundImageOpacity,
            },
            TitleBarTextColorAdaptive = TitleBarTextColorAdaptive,
            Items = new List<ZoneItem>(Items.ConvertAll(i => i.Clone()))
        };
        // Ponytail: 14 AppearanceModel fields auto-copied via reflection so
        // adding a new one to the base class doesn't silently miss here.
        Helpers.CloneHelper.CopyBaseProperties<AppearanceModel>(this, copy);
        return copy;
    }
}