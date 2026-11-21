using System;

namespace DesktopZones.Helpers;

/// <summary>
/// 复数类别（ICU / CLDR 的简化版，只够 DeskOrder 语言包用）。
///
/// 返回 <c>zero / one / two / few / many / other</c> 之一。这里刻意不做完整的
/// CLDR 数据表：桌面应用需要的计数文案集中在十来个语言，逐一写死规则比引入
/// 一份几十 KB 的规则表更好维护，也不用为「同一种语言不同方言」做额外分支。
/// 规则与 ICU 一致的部分：
/// <list type="bullet">
/// <item>中/日/韩/越/泰/印尼：只有其它形式（量词承担了复数的语义）。</item>
/// <item>英/德/荷/北欧/意/西/葡/希腊/保加利亚/土耳其：1 单数，其余复数。</item>
/// <item>法语：0 和 1 都算单数（<c>0 jour</c>）。</item>
/// <item>俄语/乌克兰语：个位 1 且不是 11 → one；个位 2..4 且不是 12..14 → few；其余 many（含 0）。</item>
/// <item>波兰语：1 → one；个位 2..4 且不是 12..14 → few；其余 many。</item>
/// <item>捷克语/斯洛伐克语：1 → one；2..4 → few；其余 other。</item>
/// <item>阿拉伯语：0/1/2 单独三档，3..10 与 11..99 各一档。</item>
/// <item>希伯来语：1 → one；2 → two；其余 other。</item>
/// <item>没有收录的语言一律回退 other（语言包只写 other 也能正常工作）。</item>
/// </list>
/// 语言 code 只取主语言段（<c>ru-RU</c> → <c>ru</c>），大小写不敏感。
/// </summary>
public static class PluralRules
{
    /// <summary>取 <paramref name="count"/> 在 <paramref name="language"/> 下的复数类别。</summary>
    public static string Category(string? language, int count)
    {
        var n = Math.Abs((long)count); // 负数按绝对值算（ICU 同样只看绝对值）
        switch (PrimaryLanguage(language))
        {
            // 无复数形态：中文、日语、韩语、越南语、泰语、印尼语。
            case "zh":
            case "ja":
            case "ko":
            case "vi":
            case "th":
            case "id":
                return "other";

            // 1 = one，其余 = other（土耳其语虽然语法上没有复数后缀，但 ICU 仍是 one/other）。
            case "en":
            case "de":
            case "nl":
            case "sv":
            case "da":
            case "no":
            case "nb":
            case "nn":
            case "fi":
            case "it":
            case "es":
            case "pt":
            case "el":
            case "bg":
            case "tr":
                return n == 1 ? "one" : "other";

            // 法语：0 和 1 都是单数（0 jour / 1 jour）。
            case "fr":
                return n <= 1 ? "one" : "other";

            // 俄语 / 乌克兰语：1, 21, 31... one；2..4, 22..24... few；0, 5, 11..14... many。
            case "ru":
            case "uk":
                if (n % 10 == 1 && n % 100 != 11) return "one";
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) return "few";
                return "many";

            // 波兰语：只有 1 是 one（21 也是 many），2..4 是 few。
            case "pl":
                if (n == 1) return "one";
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14)) return "few";
                return "many";

            // 捷克语 / 斯洛伐克语：1 one；2..4 few；其余 other（没有 many）。
            case "cs":
            case "sk":
                if (n == 1) return "one";
                if (n is >= 2 and <= 4) return "few";
                return "other";

            // 阿拉伯语：0 zero / 1 one / 2 two / 3..10 few / 11..99 many / 100..102 ... other。
            case "ar":
                if (n == 0) return "zero";
                if (n == 1) return "one";
                if (n == 2) return "two";
                var mod100 = n % 100;
                if (mod100 is >= 3 and <= 10) return "few";
                if (mod100 is >= 11 and <= 99) return "many";
                return "other";

            // 希伯来语：1 one / 2 two / 其余 other。
            case "he":
            case "iw":
                return n == 1 ? "one" : n == 2 ? "two" : "other";

            default:
                return "other";
        }
    }

    /// <summary>取 BCP-47 的主语言段：<c>zh-Hant-TW</c> → <c>zh</c>；空值返回空串。</summary>
    static string PrimaryLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "";
        var code = language.Trim();
        var cut = code.IndexOfAny(new[] { '-', '_' });
        if (cut >= 0) code = code[..cut];
        return code.ToLowerInvariant();
    }
}
