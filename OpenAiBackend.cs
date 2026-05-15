using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class OpenAiBackend : TranslationBackend
{
    private readonly string _apiKey;
    private readonly string _model;

    public OpenAiBackend(BackendConfig config) : base(config.Name, config.Model, config.BatchSize)
    {
        _apiKey = config.ApiKey;
        _model = config.Model;
        Http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
    }

    public override async Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch)
    {
        var userMsg = BuildUserMessage(batch);

        var body = new
        {
            model = _model,
            max_completion_tokens = 16384,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userMsg }
            }
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{Name}] POST api.openai.com (model={_model}, batch={batch.Count})");
        var resp = await Http.PostAsync("https://api.openai.com/v1/chat/completions", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} - {errBody}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString() ?? "";

        return ParseResponse(text, Name);
    }
}
