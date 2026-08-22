using System.Text;

namespace Seed;

public class Glossary
{
    private readonly Dictionary<string, Dictionary<string, string>> _terms = new();

    public static Glossary Load(string path)
    {
        var g = new Glossary();
        if (!File.Exists(path)) return g;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var map = new Dictionary<string, string>();
            foreach (var langProp in prop.Value.EnumerateObject())
                map[langProp.Name] = langProp.Value.GetString() ?? "";
            g._terms[prop.Name] = map;
        }
        return g;
    }

    public string GetTermSection(string targetLang)
    {
        if (_terms.Count == 0) return "(术语表为空)";
        var sb = new StringBuilder();
        foreach (var kv in _terms)
        {
            var translation = kv.Value.TryGetValue(targetLang, out var t) ? t : kv.Key;
            sb.AppendLine($"  {kv.Key} → {translation}");
        }
        return sb.ToString();
    }

    public IReadOnlyList<string> SourceTerms => _terms.Keys.ToList();
}
