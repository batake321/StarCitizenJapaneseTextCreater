using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class ClaudeBackend : TranslationBackend
{
    private readonly string _apiKey;
    private readonly string _model;

    public ClaudeBackend(BackendConfig config) : base(config.Name, config.Model, config.BatchSize)
    {
        _apiKey = config.ApiKey;
        _model = config.Model;
        Http.DefaultRequestHeaders.Add("x-api-key", _apiKey);
        Http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public override async Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch)
    {
        var userMsg = BuildUserMessage(batch);

        var supportsTemperature = !_model.Contains("opus", StringComparison.OrdinalIgnoreCase);
        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["max_tokens"] = 16384,
            ["system"] = SystemPrompt,
            ["messages"] = new[] { new { role = "user", content = userMsg } }
        };
        if (supportsTemperature)
            bodyDict["temperature"] = 0.2;

        var json = JsonSerializer.Serialize(bodyDict, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{Name}] POST api.anthropic.com (model={_model}, batch={batch.Count})");
        var resp = await Http.PostAsync("https://api.anthropic.com/v1/messages", content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} - {errBody}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var text = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";

        return ParseResponse(text, Name);
    }
}
