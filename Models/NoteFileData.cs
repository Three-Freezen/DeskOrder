using System.Collections.Generic;

namespace DesktopZones.Models;

// ── 便签富文本内容持久化(独立 JSON 文件) ──
// 每个便签一个 <id>.json,存段落/run 级别的格式(加粗/斜体/下划线/字号/颜色)。
// config.json 仍负责便签的元数据(位置/外观/标题等),这里只存会丢格式的正文内容。

public class NoteFileData
{
    public List<NoteParagraphData> Paragraphs { get; set; } = new();
}

public class NoteParagraphData
{
    public List<NoteRunData> Runs { get; set; } = new();
}

public class NoteRunData
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }
    public double Size { get; set; } = 14;
    /// <summary>#AARRGGBB 或 #RRGGBB。</summary>
    public string Color { get; set; } = "#FFE0E0E0";
}
