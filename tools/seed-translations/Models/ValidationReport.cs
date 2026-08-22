namespace Seed.Models;
public class ValidationReport
{
    public Dictionary<string, LangResult> Results { get; set; } = new();
    public class LangResult
    {
        public string Status { get; set; } = "ok";
        public List<string> Missing { get; set; } = new();
        public List<string> Extra { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
