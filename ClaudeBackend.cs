using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class ClaudeBackend : TranslationBackend
{
    private readonly string _apiKey;
    private readonly string _model;

    public ClaudeBackend(BackendConfig config) : base(config.Name, config.BatchSize)
    {
        _apiKey = config.ApiKey;
        _model = config.Model;
        Http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        Http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public override async Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch)
    {
        var userMsg = BuildUserMessage(batch);

        var body = new
        {
            model = _model,
            max_tokens = 16384,
            temperature = 0.2,
            system = SystemPrompt,
            messages = new[] { new { role = "user", content = userMsg } }
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await Http.PostAsync("https://api.anthropic.com/v1/messages", content);
        resp.EnsureSuccessStatusCode();

        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

        return ParseResponse(text);
    }
}
