using System.Collections.Generic;
using System.Linq;

namespace DesktopZones.Models;

/// <summary>自动整理的预设扩展名（12 个常见桌面文件类型）。运行时不可变，
/// 用户自定义扩展名存在 Zone.AutoOrganizeExtensions 里，与预设合并渲染。</summary>
public static class AutoOrganizePresets
{
    public static readonly IReadOnlyList<string> Extensions = new[]
    {
        ".pdf", ".docx", ".xlsx", ".pptx", ".txt",
        ".jpg", ".png", ".mp4", ".zip", ".lnk",
        ".mp3", ".wav"
    };

    /// <summary>判断某个扩展名是否属于预设。</summary>
    public static bool IsPreset(string ext) =>
        Extensions.Contains(ext.ToLowerInvariant());
}
