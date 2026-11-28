using System;
using System.Collections.Generic;

namespace DesktopZones.Models;

// ── 便签富文本内容持久化(独立 JSON 文件) ──
// 每个便签一个 <id>.json,只存「与便签文档样式不同的覆盖值」。
//
// v2(2026-11-28)：「正文格式跟着内容走」修复 —— 字号/颜色改为可空:
//   null = 继承 config.json 里 StickyNote 的 FontSize / TextColor。
//   这样设置面板里改一次字号,整张便签里没被单独设置过的文字全部跟随。
//
// v1(旧文件)是「每个 run 都写死格式」:加载时由 NormalizeLegacyFormat() 把
// 「等于旧默认值(14 号 / #FFE0E0E0)」的显式值降级为 null,真正被用户手动改过的
// 字号/颜色保留。迁移只在 Version < 2 时发生,写回后 Version = 2。

public class NoteFileData
{
    public const int CurrentVersion = 2;
    public const double LegacyDefaultFontSize = 14;
    public const string LegacyDefaultTextColor = "#FFE0E0E0";

    /// <summary>
    /// 格式版本。默认 1 = 旧文件(JSON 里没有这个字段)—— 不能默认成 CurrentVersion,
    /// 否则反序列化出来的旧文件会被当成新格式,迁移永远不触发。
    /// 写盘时由 StickyNoteWindow.BuildNoteFileData() 显式设成 CurrentVersion。
    /// </summary>
    public int Version { get; set; } = 1;
    public List<NoteParagraphData> Paragraphs { get; set; } = new();

    /// <summary>
    /// v1 → v2 一次性规范化:把「当年写死的旧默认格式」降级成「继承文档样式」。
    /// 返回 true 表示真的改动了(调用方应把新版写回文件,避免每次打开都迁移)。
    /// </summary>
    public bool NormalizeLegacyFormat()
    {
        if (Version >= CurrentVersion) return false;
        Version = CurrentVersion;
        foreach (var para in Paragraphs)
        {
            foreach (var run in para.Runs)
            {
                if (run.Size.HasValue && Math.Abs(run.Size.Value - LegacyDefaultFontSize) < 0.01)
                    run.Size = null;
                if (IsLegacyDefaultColor(run.Color))
                    run.Color = null;
                if (run.Bold == false) run.Bold = null;
                if (run.Italic == false) run.Italic = null;
                if (run.Underline == false) run.Underline = null;
            }
        }
        return true;
    }

    /// <summary>#RRGGBB / #AARRGGBB 都按 ARGB 比较,忽略大小写与 #。</summary>
    static bool IsLegacyDefaultColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        return s.Equals("FFE0E0E0", StringComparison.OrdinalIgnoreCase);
    }
}

public class NoteParagraphData
{
    public List<NoteRunData> Runs { get; set; } = new();
}

public class NoteRunData
{
    public string Text { get; set; } = "";
    // 以下全部可空:null = 继承便签的文档样式(粗/斜/下划线的默认是 Normal/None)。
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    public double? Size { get; set; }
    /// <summary>#AARRGGBB 或 #RRGGBB。</summary>
    public string? Color { get; set; }
}
