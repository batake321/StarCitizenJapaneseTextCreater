using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class OllamaBackend : TranslationBackend
{
    private readonly string _baseUrl;
    private readonly string _model;

    public OllamaBackend(BackendConfig config) : base(config.Name, config.Model, config.BatchSize)
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
                new { role = "user", content = "mission_desc_01\tDeliver the package to Hurston.\nmission_title_02\tBounty: Eliminate the target" },
                new { role = "assistant", content = "mission_desc_01\tパッケージを Hurston に届けよ。\nmission_title_02\t賞金首：ターゲットを排除せよ" },
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

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{Name}] POST {url} (model={_model}, batch={batch.Count})");
        var resp = await Http.PostAsync(url, content);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase} - URL: {url} - {errBody}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);

        var msg = doc.RootElement.GetProperty("message");
        var text = msg.GetProperty("content").GetString() ?? "";

        if (msg.TryGetProperty("thinking", out _) && string.IsNullOrWhiteSpace(text))
        {
            text = "[]";
        }

        return ParseResponse(text, Name);
    }
}
