using System;
using System.Text.Json.Serialization;

namespace DesktopZones.Models;

public enum ItemType
{
    Shortcut,   // .lnk file
    Folder,     // folder path
    Application, // .exe file
    Document,   // .txt / .docx / .pptx etc.
    ShellLocation // virtual shell object, TargetPath is a "::{GUID}" spec (Recycle Bin, This PC, ...)
}

public class ZoneItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>
    /// Custom icon location ("file,index") of the source shortcut, when the shortcut's
    /// icon differs from its target's (e.g. a desktop shortcut with a rounded high-res
    /// .ico). Null = render the target's own icon.
    /// </summary>
    public string? IconPath { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ItemType Type { get; set; }

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
            Type = Type
        };
    }
}
