namespace Seed.Models;
public class LanguagePack
{
    public Dictionary<string, string> Strings { get; set; } = new();
    public PackInfo Info { get; set; } = new();
    public class PackInfo
    {
        public string? Version { get; set; }
        public DateTime? LastSeeded { get; set; }
        public DateTime? LastHumanReview { get; set; }
    }
    public static LanguagePack Load(string path)
    {
        if (!File.Exists(path)) return new();
        var json = File.ReadAllText(path);
        var pack = new LanguagePack();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Name == "_meta")
            {
                pack.Info.Version = prop.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                pack.Info.LastSeeded = prop.Value.TryGetProperty("last_seeded", out var s) && s.TryGetDateTime(out var d) ? d : null;
            }
            else
                pack.Strings[prop.Name] = prop.Value.GetString() ?? "";
        }
        return pack;
    }
    public void Save(string path)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"_meta\": {{ \"version\": \"{Info.Version ?? "1.1.0"}\", \"last_seeded\": \"{DateTime.UtcNow:O}\" }},");
        var first = true;
        foreach (var kv in Strings)
        {
            if (!first) sb.AppendLine(",");
            first = false;
            sb.Append("  ").Append(System.Text.Json.JsonSerializer.Serialize(kv.Key)).Append(": ")
              .Append(System.Text.Json.JsonSerializer.Serialize(kv.Value));
        }
        sb.AppendLine();
        sb.AppendLine("}");
        File.WriteAllText(path, sb.ToString());
    }
}
