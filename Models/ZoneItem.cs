using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

public enum ItemType
{
    Shortcut,   // .lnk file
    Folder,     // folder path
    Application, // .exe file
    Document,   // .txt / .docx / .pptx etc.
    ShellLocation, // virtual shell object, TargetPath is a "::{GUID}" spec (Recycle Bin, This PC, ...)
    SubFolder,  // virtual container; SubItems holds inner ZoneItems. No nesting.
}

public class ZoneItem : INotifyPropertyChanged
{
    // ponytail: event backing field marked [field: JsonIgnore] so System.Text.Json
    // never tries to serialize it. Mirrors the [JsonIgnore] pattern used in Zone.cs
    // for non-persisted display state. Match: existing fields keep their JSON names.
    [field: JsonIgnore]
    public event PropertyChangedEventHandler? PropertyChanged;

    private Guid _id = Guid.NewGuid();
    public Guid Id
    {
        get => _id;
        set => SetField(ref _id, value);
    }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    private string _targetPath = string.Empty;
    public string TargetPath
    {
        get => _targetPath;
        set => SetField(ref _targetPath, value);
    }

    private double _x;
    public double X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    private double _y;
    public double Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }

    /// <summary>
    /// Custom icon location ("file,index") of the source shortcut, when the shortcut's
    /// icon differs from its target's (e.g. a desktop shortcut with a rounded high-res
    /// .ico). Null = render the target's own icon.
    /// </summary>
    private string? _iconPath;
    public string? IconPath
    {
        get => _iconPath;
        set => SetField(ref _iconPath, value);
    }

    private ItemType _type;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ItemType Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    // ── SubFolder fields (no-op for non-SubFolder items, all defaults preserved
    //    for backward compat with legacy config.json) ──
    private List<ZoneItem> _subItems = new();
    public List<ZoneItem> SubItems
    {
        get => _subItems;
        // ponytail: fire on replace only. Inner-list Add/Remove/RemoveAt
        // are not propagated through this event; consumers needing child
        // reactivity wrap each ZoneItem and subscribe to its INPC directly.
        set { _subItems = value ?? new(); OnPropertyChanged(nameof(SubItems)); }
    }

    private bool _iconSizeAutoGrow = true;
    public bool IconSizeAutoGrow
    {
        get => _iconSizeAutoGrow;
        set => SetField(ref _iconSizeAutoGrow, value);
    }

    private bool _cornerRounded = true;
    public bool CornerRounded
    {
        get => _cornerRounded;
        set => SetField(ref _cornerRounded, value);
    }

    private bool _fillFollowsZone = true;
    public bool FillFollowsZone
    {
        get => _fillFollowsZone;
        set => SetField(ref _fillFollowsZone, value);
    }

    private string _fillColorOverride = "";
    public string FillColorOverride
    {
        get => _fillColorOverride;
        set => SetField(ref _fillColorOverride, value);
    }

    private double _fillOpacityOverride = -1; // -1 = 跟随主分区
    public double FillOpacityOverride
    {
        get => _fillOpacityOverride;
        set => SetField(ref _fillOpacityOverride, value);
    }

    private string _backgroundImagePath = "";
    public string BackgroundImagePath
    {
        get => _backgroundImagePath;
        set => SetField(ref _backgroundImagePath, value);
    }

    private double _backgroundImageOpacity = -1;
    public double BackgroundImageOpacity
    {
        get => _backgroundImageOpacity;
        set => SetField(ref _backgroundImageOpacity, value);
    }

    private bool _enableLiquidGlass = false;
    public bool EnableLiquidGlass
    {
        get => _enableLiquidGlass;
        set => SetField(ref _enableLiquidGlass, value);
    }

    private int _gridSize = 56;
    public int GridSize
    {
        get => _gridSize;
        set => SetField(ref _gridSize, value);
    }

    private bool _snapToGrid = true;
    public bool SnapToGrid
    {
        get => _snapToGrid;
        set => SetField(ref _snapToGrid, value);
    }

    private bool _autoArrange = true;
    public bool AutoArrange
    {
        get => _autoArrange;
        set => SetField(ref _autoArrange, value);
    }

    private HoverExpandAnimationKind _hoverAnimation = HoverExpandAnimationKind.ScaleExpand;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HoverExpandAnimationKind HoverAnimation
    {
        get => _hoverAnimation;
        set => SetField(ref _hoverAnimation, value);
    }

    private double _hoverExpandSpeed = 1.0;
    public double HoverExpandSpeed
    {
        get => _hoverExpandSpeed;
        set => SetField(ref _hoverExpandSpeed, value);
    }

    private bool _hoverAutoExpand = false;
    public bool HoverAutoExpand
    {
        get => _hoverAutoExpand;
        set => SetField(ref _hoverAutoExpand, value);
    }

    public ZoneItem()
    {
    }

    public ZoneItem(string name, string targetPath, ItemType type, double x, double y)
    {
        Name = name;
        TargetPath = targetPath;
        Type = type;
        X = x;
        Y = y;
    }

    public ZoneItem Clone()
    {
        return new ZoneItem
        {
            Id = Id,
            Name = Name,
            TargetPath = TargetPath,
            X = X,
            Y = Y,
            IconPath = IconPath,
            Type = Type,
            SubItems = new List<ZoneItem>(SubItems.ConvertAll(i => i.Clone())),
            IconSizeAutoGrow = IconSizeAutoGrow,
            CornerRounded = CornerRounded,
            FillFollowsZone = FillFollowsZone,
            FillColorOverride = FillColorOverride,
            FillOpacityOverride = FillOpacityOverride,
            BackgroundImagePath = BackgroundImagePath,
            BackgroundImageOpacity = BackgroundImageOpacity,
            EnableLiquidGlass = EnableLiquidGlass,
            GridSize = GridSize,
            SnapToGrid = SnapToGrid,
            AutoArrange = AutoArrange,
            HoverAnimation = HoverAnimation,
            HoverExpandSpeed = HoverExpandSpeed,
            HoverAutoExpand = HoverAutoExpand,
        };
    }

    /// <summary>
    /// ponytail: SubFolder 内禁止嵌套 SubFolder。返回被剥离的嵌套项数量。
    /// 在加载 config.json 后跑一次;UI 创建路径不应产生嵌套项。
    /// </summary>
    public static int ValidateSubItems(ZoneItem item)
    {
        if (item.Type != ItemType.SubFolder) return 0;
        int stripped = 0;
        for (int i = item.SubItems.Count - 1; i >= 0; i--)
        {
            if (item.SubItems[i].Type == ItemType.SubFolder)
            {
                item.SubItems.RemoveAt(i);
                stripped++;
            }
        }
        return stripped;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}