using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class GeminiBackend : TranslationBackend
{
    private readonly string _apiKey;
    private readonly string _model;

    public GeminiBackend(BackendConfig config) : base(config.Name, config.Model, Math.Max(config.BatchSize, 50))
    {
        _apiKey = config.ApiKey;
        _model = config.Model;
    }

    public override async Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch)
    {
        var userMsg = BuildUserMessage(batch);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";

        var body = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = SystemPrompt } }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userMsg } }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                maxOutputTokens = 16384
            }
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await Http.PostAsync(url, content);
        resp.EnsureSuccessStatusCode();

        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text").GetString() ?? "";

        return ParseResponse(text, Name);
    }
}
