using System.Text;

namespace StarCitizenJapaneseTextCreater;

public abstract class TranslationBackend
{
    public string Name { get; }
    public string ModelName { get; }
    public int BatchSize { get; }
    public string TranslatorLabel => $"{Name}/{ModelName}";
    protected readonly HttpClient Http;

    private static readonly string BaseSystemPrompt =
        "あなたはゲーム「Star Citizen」のローカライズ翻訳者です。英語テキストを日本語に翻訳してください。\n\n" +
        "重要：ゲームの雰囲気を損なわないように、原文の英語のニュアンスを活かした自然な日本語に訳してください。\n" +
        "SFゲームにふさわしい語調を保ち、直訳ではなく、プレイヤーが没入できる翻訳を心がけてください。\n\n" +
        "ルール：\n" +
        "1. 地名（惑星名、星系名、都市名、ステーション名）は英語のまま\n" +
        "2. 船の名前・メーカー名は英語のまま\n" +
        "3. 人物名・企業名の固有名詞は英語のまま\n" +
        "4. UIラベル、説明文、ミッションテキストは自然な日本語に\n" +
        "5. %ls %s ~action(xxx) @xxx ~mission(xxx) <EM4> </EM4> 等のタグやプレースホルダーはそのまま保持\n" +
        "6. 空文字・数字のみ・記号のみはそのまま返す\n" +
        "7. \\nは改行として保持\n\n" +
        "入力: 1行1エントリ、タブ区切り「キー<TAB>英語テキスト」\n" +
        "出力: 1行1エントリ、タブ区切り「キー<TAB>日本語テキスト」\n" +
        "入力と同じ行数・同じキーで、タブ区切りの翻訳結果のみ出力。説明や装飾は不要。";

    private static List<(string English, string Japanese)>? _glossary;

    public static void SetGlossary(List<(string English, string Japanese)>? glossary)
    {
        _glossary = glossary;
    }

    protected static string SystemPrompt
    {
        get
        {
            if (_glossary == null || _glossary.Count == 0)
                return BaseSystemPrompt;

            var sb = new StringBuilder(BaseSystemPrompt);
            sb.Append("\n\n用語集（以下の英語は必ず指定の日本語に翻訳すること）：\n");
            foreach (var (en, ja) in _glossary)
                sb.Append($"・{en} → {ja}\n");
            return sb.ToString();
        }
    }

    protected TranslationBackend(string name, string modelName, int batchSize)
    {
        Name = name;
        ModelName = modelName;
        BatchSize = batchSize;
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
    }

    public abstract Task<Dictionary<string, string>?> TranslateAsync(List<TranslationEntry> batch);

    protected static Dictionary<string, string>? ParseResponse(string text, string backendName = "")
    {
        var result = new Dictionary<string, string>();

        // Strip markdown code fences
        text = StripCodeFences(text).Trim();

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var tabIdx = trimmed.IndexOf('\t');
            if (tabIdx <= 0) continue;

            var key = trimmed[..tabIdx].Trim();
            var ja = trimmed[(tabIdx + 1)..].Trim();
            if (key.Length > 0 && ja.Length > 0)
                result[key] = ja;
        }

        if (result.Count == 0)
        {
            var preview = text.Length > 300 ? text[..300] + "..." : text;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{backendName}] パース失敗 (0行): {preview}");
            return null;
        }

        return result;
    }

    private static string StripCodeFences(string text)
    {
        if (!text.TrimStart().StartsWith("```")) return text;

        var lines = text.Split('\n');
        var inner = new List<string>();
        bool started = false;
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```") && !started) { started = true; continue; }
            if (line.TrimStart().StartsWith("```") && started) break;
            if (started) inner.Add(line);
        }
        return string.Join("\n", inner);
    }

    protected static string BuildUserMessage(List<TranslationEntry> batch)
    {
        var sb = new StringBuilder();
        foreach (var e in batch)
            sb.AppendLine($"{e.Key}\t{e.English}");
        return sb.ToString();
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
