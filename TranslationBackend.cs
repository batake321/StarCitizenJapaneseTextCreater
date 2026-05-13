using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

public abstract class TranslationBackend
{
    public string Name { get; }
    public int BatchSize { get; }
    protected readonly HttpClient Http;

    protected static readonly string SystemPrompt =
        "あなたはゲーム「Star Citizen」のローカライズ翻訳者です。英語テキストを日本語に翻訳してください。\n\n" +
        "ルール：\n" +
        "1. 地名（惑星名、星系名、都市名、ステーション名）は英語のまま\n" +
        "2. 船の名前・メーカー名は英語のまま\n" +
        "3. 人物名・企業名の固有名詞は英語のまま\n" +
        "4. UIラベル、説明文、ミッションテキストは自然な日本語に\n" +
        "5. %ls %s ~action(xxx) @xxx ~mission(xxx) <EM4> </EM4> 等のタグやプレースホルダーはそのまま保持\n" +
        "6. 空文字・数字のみ・記号のみはそのまま返す\n" +
        "7. \\nは改行として保持\n\n" +
        "入力: JSON配列 [{\"k\":\"キー\",\"e\":\"英語\"},...]\n" +
        "出力: JSON配列 [{\"k\":\"キー\",\"j\":\"日本語\"},...]\n" +
        "必ずJSON配列のみ出力。説明不要。";

    protected TranslationBackend(string name, int batchSize)
    {
        Name = name;
        BatchSize = batchSize;
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
    }

    public abstract Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch);

    protected static Dictionary<string, string>? ParseResponse(string text)
    {
        var arr = ExtractJsonArray(text);
        if (arr == null) return null;

        var result = new Dictionary<string, string>();
        foreach (var item in arr)
        {
            if (item.Key.Length > 0 && item.Japanese.Length > 0)
                result[item.Key] = item.Japanese;
        }
        return result.Count > 0 ? result : null;
    }

    protected static List<BatchOutputItem>? ExtractJsonArray(string text)
    {
        text = text.Trim();

        // Strip markdown code fences
        if (text.StartsWith("```"))
        {
            var lines = text.Split('\n');
            var inner = new List<string>();
            bool started = false;
            foreach (var line in lines)
            {
                if (line.StartsWith("```") && !started) { started = true; continue; }
                if (line.StartsWith("```") && started) break;
                if (started) inner.Add(line);
            }
            text = string.Join("\n", inner);
        }

        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']') + 1;
        if (start < 0 || end <= start) return null;

        var candidate = text[start..end];

        // Try direct parse
        try { return JsonSerializer.Deserialize<List<BatchOutputItem>>(candidate); }
        catch (JsonException) { }

        // Fix trailing comma
        var fixed_ = Regex.Replace(candidate, @",\s*\]", "]");
        try { return JsonSerializer.Deserialize<List<BatchOutputItem>>(fixed_); }
        catch (JsonException) { }

        // Truncate to last complete object
        var lastBrace = candidate.LastIndexOf('}');
        if (lastBrace > 0)
        {
            var truncated = candidate[..(lastBrace + 1)] + "]";
            try { return JsonSerializer.Deserialize<List<BatchOutputItem>>(truncated); }
            catch (JsonException) { }
        }

        return null;
    }

    protected static string BuildUserMessage(List<TranslationEntry> batch)
    {
        var items = batch.Select(e => new BatchInputItem { Key = e.Key, English = e.English }).ToList();
        return JsonSerializer.Serialize(items, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static TranslationBackend Create(BackendConfig config)
    {
        return config.Type.ToLowerInvariant() switch
        {
            "claude" => new ClaudeBackend(config),
            "gemini" => new GeminiBackend(config),
            "ollama" => new OllamaBackend(config),
            _ => throw new ArgumentException($"Unknown backend type: {config.Type}")
        };
    }
}
