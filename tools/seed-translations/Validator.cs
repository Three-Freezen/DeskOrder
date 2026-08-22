using System.Text.RegularExpressions;
using Seed.Models;

namespace Seed;

public static class Validator
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    public static ValidationReport Validate(LanguagePack source, Dictionary<string, LanguagePack> targets)
    {
        var report = new ValidationReport();
        var sourceKeys = source.Strings.Keys.ToHashSet();
        foreach (var (lang, pack) in targets)
        {
            var result = new ValidationReport.LangResult();
            var targetKeys = pack.Strings.Keys.ToHashSet();

            result.Missing = sourceKeys.Except(targetKeys).OrderBy(k => k).ToList();
            result.Extra = targetKeys.Except(sourceKeys).OrderBy(k => k).ToList();

            foreach (var key in sourceKeys.Intersect(targetKeys))
            {
                var src = source.Strings[key];
                var tgt = pack.Strings[key];
                var srcPh = ExtractPlaceholders(src);
                var tgtPh = ExtractPlaceholders(tgt);
                if (!srcPh.SetEquals(tgtPh))
                    result.Warnings.Add($"{key}: placeholder mismatch (src={string.Join(",", srcPh)}, tgt={string.Join(",", tgtPh)})");
                if (string.IsNullOrEmpty(tgt))
                    result.Warnings.Add($"{key}: empty value");
            }

            result.Status = (result.Missing.Count > 0 || result.Extra.Count > 0 || result.Warnings.Any(w => w.Contains("placeholder")))
                ? "error" : (result.Warnings.Count > 0 ? "warn" : "ok");
            report.Results[lang] = result;
        }
        return report;
    }

    private static HashSet<string> ExtractPlaceholders(string s) =>
        Placeholder.Matches(s).Select(m => m.Value).ToHashSet();
}
