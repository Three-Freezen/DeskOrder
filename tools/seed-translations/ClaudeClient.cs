using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Seed;

public class ClaudeClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private const int MaxRetries = 3;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";

    public ClaudeClient(string apiKey, string model = "claude-sonnet-4-5")
    {
        _model = model;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<Dictionary<string, string>> TranslateAsync(
        string targetLang,
        string glossarySection,
        Dictionary<string, string> sourceEntries,
        CancellationToken ct = default)
    {
        var system = $@"你是 DesktopZones (桌面分区管理工具) 的本地化专家.
目标语言: {targetLang}

术语表 (强制, 不要改写):
{glossarySection}

规则:
1. 仅输出 JSON 对象, 不加注释/markdown
2. 保留 {{0}} {{1}} 等占位符不动
3. 保留 \n \t 等转义字符
4. 不要翻译 JSON key
5. 保持与 zh 源文本语气一致 (UI 短句, 不啰嗦)
6. 遇到术语表中列出的词, 必须用术语表规定的译法";

        var user = JsonSerializer.Serialize(sourceEntries, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        Exception? lastError = null;
        for (int attempt = 0; attempt < MaxRetries; attempt++)
        {
            try
            {
                var body = new
                {
                    model = _model,
                    max_tokens = 4096,
                    system,
                    messages = new[] { new { role = "user", content = user } }
                };
                var json = JsonSerializer.Serialize(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _http.PostAsync(ApiUrl, content, ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                var respJson = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(respJson);
                var rawText = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
                return ParseJsonResponse(rawText, sourceEntries.Keys.ToHashSet());
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries - 1)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (JsonException) when (attempt < MaxRetries - 1)
            {
                lastError = new JsonException("Claude returned non-JSON response");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        throw new InvalidOperationException($"Failed to translate after {MaxRetries} attempts", lastError);
    }

    private static Dictionary<string, string> ParseJsonResponse(string raw, HashSet<string> expectedKeys)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("No JSON object found in response");
        var json = raw[start..(end + 1)];
        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!expectedKeys.Contains(prop.Name))
                throw new JsonException($"Unexpected key in response: {prop.Name}");
            result[prop.Name] = prop.Value.GetString() ?? "";
        }
        foreach (var key in expectedKeys)
            if (!result.ContainsKey(key))
                throw new JsonException($"Missing key in response: {key}");
        return result;
    }
}
