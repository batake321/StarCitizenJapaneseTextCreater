using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class OllamaBackend : TranslationBackend
{
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaBackend(BackendConfig config) : base(config.Name, config.BatchSize)
    {
        _baseUrl = config.BaseUrl.TrimEnd('/');
        _model = config.Model;
    }

    public override async Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch)
    {
        var userMsg = BuildUserMessage(batch);
        var url = $"{_baseUrl}/api/chat";

        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userMsg }
            },
            stream = false,
            options = new { temperature = 0.2, num_predict = 16384 }
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
            .GetProperty("message")
            .GetProperty("content").GetString() ?? "";

        return ParseResponse(text);
    }
}
