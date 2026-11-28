using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace DesktopZones.Helpers;

/// <summary>
/// 便签正文的「当前笔触」(Word 式,2026-11-28):无选区时用户刚设的格式,只存在于内存,
/// 打字时按 TextChanged 的 change 范围精确落到新文字上。null = 这一项沿用原格式。
///
/// 单独抽成文件是为了能脱离窗口/服务直接测(见 _scratch/noteprobe9)。
/// </summary>
public sealed record TypingFormat(
    FontFamily? Family,
    double? Size,
    FontWeight? Weight,
    FontStyle? Style,
    bool? Underline,
    SolidColorBrush? Color)
{
    public static readonly TypingFormat Empty = new(null, null, null, null, null, null);
}

public static class NoteFormatting
{
    public static bool IsBold(object? v) => v is FontWeight fw && fw.ToOpenTypeWeight() > 400;
    public static bool IsItalic(object? v) => v is FontStyle fs && fs == FontStyles.Italic;
    public static bool IsUnderlined(object? v) => v is TextDecorationCollection td && td.Count > 0;

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// 打某处的格式快照。fromLeftChar = true: 取光标左侧那个字符(打字会继承的格式);
    /// false: 取右侧那个字符(选区第一个字)。取不到时各项为 null = 沿用文档样式。
    /// </summary>
    public static TypingFormat Snapshot(TextPointer? pos, bool fromLeftChar = true)
    {
        if (pos == null) return TypingFormat.Empty;
        TextRange range;
        try
        {
            // 首选方向取不到(文档/段落的开头结尾)时退到另一侧 —— 只要「最近的那个字符」。
            var other = fromLeftChar
                ? pos.GetPositionAtOffset(-1, LogicalDirection.Backward)
                : pos.GetPositionAtOffset(1, LogicalDirection.Forward);
            if (other == null)
                other = fromLeftChar
                    ? pos.GetPositionAtOffset(1, LogicalDirection.Forward)
                    : pos.GetPositionAtOffset(-1, LogicalDirection.Backward);
            range = other == null
                ? new TextRange(pos, pos)
                : (other.CompareTo(pos) < 0 ? new TextRange(other, pos) : new TextRange(pos, other));
        }
        catch { range = new TextRange(pos, pos); }

        object? Get(DependencyProperty p)
        {
            try
            {
                var v = range.GetPropertyValue(p);
                return v == DependencyProperty.UnsetValue ? null : v;
            }
            catch { return null; }
        }

        return new TypingFormat(
            Get(TextElement.FontFamilyProperty) as FontFamily,
            Get(TextElement.FontSizeProperty) as double?,
            Get(TextElement.FontWeightProperty) as FontWeight?,
            Get(TextElement.FontStyleProperty) as FontStyle?,
            Get(Inline.TextDecorationsProperty) is TextDecorationCollection td ? IsUnderlined(td) : null,
            Get(TextElement.ForegroundProperty) as SolidColorBrush);
    }

    /// <summary>把「本次修改」并入笔触;其它项保持原样。</summary>
    public static TypingFormat Merge(TypingFormat f, DependencyProperty prop, object? value)
    {
        if (prop == TextElement.FontFamilyProperty) return f with { Family = value as FontFamily };
        if (prop == TextElement.FontSizeProperty) return f with { Size = value is double d ? d : null };
        if (prop == TextElement.FontWeightProperty) return f with { Weight = value is FontWeight w ? w : null };
        if (prop == TextElement.FontStyleProperty) return f with { Style = value is FontStyle st ? st : null };
        if (prop == Inline.TextDecorationsProperty) return f with { Underline = value is TextDecorationCollection };
        if (prop == TextElement.ForegroundProperty) return f with { Color = value as SolidColorBrush };
        return f;
    }

    /// <summary>笔触里某个属性的值(null = 这一项不变)。</summary>
    public static object? Read(TypingFormat? f, DependencyProperty prop)
    {
        if (f == null) return null;
        if (prop == TextElement.FontFamilyProperty) return f.Family;
        if (prop == TextElement.FontSizeProperty) return f.Size;
        if (prop == TextElement.FontWeightProperty) return f.Weight;
        if (prop == TextElement.FontStyleProperty) return f.Style;
        if (prop == Inline.TextDecorationsProperty)
            return f.Underline == true ? TextDecorations.Underline : null;
        if (prop == TextElement.ForegroundProperty) return f.Color;
        return null;
    }

    /// <summary>
    /// 把笔触写到一段文字上(只写非 null 的项)。单属性各自 try:文档在每次写入后
    /// 会重排,一项失败不该丢其它项。
    /// </summary>
    public static void ApplyToRange(TextRange range, TypingFormat fmt)
    {
        if (range == null || string.IsNullOrEmpty(range.Text)) return;
        if (fmt.Family != null) TryApply(range, TextElement.FontFamilyProperty, fmt.Family);
        if (fmt.Size is double sz && sz > 0) TryApply(range, TextElement.FontSizeProperty, sz);
        if (fmt.Weight is FontWeight w) TryApply(range, TextElement.FontWeightProperty, w);
        if (fmt.Style is FontStyle st) TryApply(range, TextElement.FontStyleProperty, st);
        if (fmt.Underline is bool u) TryApply(range, Inline.TextDecorationsProperty, u ? TextDecorations.Underline : null);
        if (fmt.Color is SolidColorBrush c) TryApply(range, TextElement.ForegroundProperty, c);
    }

    static void TryApply(TextRange range, DependencyProperty prop, object? value)
    {
        try { range.ApplyPropertyValue(prop, value); } catch { }
    }
}
