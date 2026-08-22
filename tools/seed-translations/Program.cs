using Seed.Models;

namespace Seed;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--help")) { PrintHelp(); return 0; }

        var targets = args.Where(a => !a.StartsWith("--")).ToArray();
        var allTargets = new[] { "ja", "ko", "es", "fr", "de", "ru", "pt" };
        if (targets.Length == 0) targets = allTargets;

        if (args.Contains("--validate-only"))
            return RunValidation(allTargets);

        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        { Console.Error.WriteLine("❌ ANTHROPIC_API_KEY not set"); return 2; }

        var force = args.Contains("--force");
        if (force && !Confirm("⚠️  --force 会覆盖所有现有翻译, 人类修正将丢失. 继续? [y/N]"))
            return 1;

        var source = LanguagePack.Load("i18n/source.zh.json");
        var glossary = Glossary.Load("i18n/glossary.json");
        var client = new ClaudeClient(apiKey);

        foreach (var lang in targets)
        {
            var targetPath = $"i18n/{lang}.json";
            var existing = force ? new LanguagePack() : LanguagePack.Load(targetPath);
            var missing = source.Strings.Keys.Where(k => !existing.Strings.ContainsKey(k)).ToList();

            if (missing.Count == 0)
            { Console.WriteLine($"[{lang}] already complete, skipping"); continue; }

            Console.WriteLine($"[{lang}] seeding {missing.Count} keys...");
            var batchSize = 30;
            for (int i = 0; i < missing.Count; i += batchSize)
            {
                var batch = missing.Skip(i).Take(batchSize).ToDictionary(k => k, k => source.Strings[k]);
                try
                {
                    var translated = await client.TranslateAsync(lang, glossary.GetTermSection(lang), batch);
                    foreach (var kv in translated) existing.Strings[kv.Key] = kv.Value;
                }
                catch (Exception ex)
                { Console.Error.WriteLine($"[{lang}] batch {i / batchSize + 1} failed: {ex.Message}"); }
            }
            existing.Info.LastSeeded = DateTime.UtcNow;
            existing.Save(targetPath);
            Console.WriteLine($"[{lang}] saved {existing.Strings.Count} keys to {targetPath}");
        }

        return RunValidation(targets);
    }

    public static int RunValidation(string[] langs)
    {
        var source = LanguagePack.Load("i18n/source.zh.json");
        var targets = langs.ToDictionary(l => l, l => LanguagePack.Load($"i18n/{l}.json"));
        var report = Validator.Validate(source, targets);

        var hasError = false;
        foreach (var (lang, result) in report.Results)
        {
            var icon = result.Status switch { "ok" => "✓", "warn" => "⚠", "error" => "✗", _ => "?" };
            Console.WriteLine($"[{icon} {lang}] missing={result.Missing.Count}, extra={result.Extra.Count}, warnings={result.Warnings.Count}");
            foreach (var w in result.Warnings.Take(5)) Console.WriteLine($"    warn: {w}");
            if (result.Status == "error") hasError = true;
        }
        var reportJson = System.Text.Json.JsonSerializer.Serialize(report,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory("i18n");
        File.WriteAllText("i18n/_validation.json", reportJson);
        return hasError ? 1 : 0;
    }

    static bool Confirm(string prompt)
    {
        Console.Write($"{prompt} ");
        var key = Console.ReadKey();
        Console.WriteLine();
        return key.KeyChar is 'y' or 'Y';
    }

    static void PrintHelp()
    {
        Console.WriteLine("Seed translations via Claude API");
        Console.WriteLine("Usage: dotnet run -- [lang1 lang2 ...] [--force] [--validate-only]");
        Console.WriteLine("  --force       overwrite all existing translations (loses human edits)");
        Console.WriteLine("  --validate-only  only check key consistency, don't call API");
    }
}
