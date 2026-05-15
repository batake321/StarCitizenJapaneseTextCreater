using System.Net.Http;
using System.Text;
using System.Text.Json;

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
        "1. 地名（惑星名、星系名、都市名、ステーション名）は英語のまま。カタカナに音訳しない（地名リストが別途付与されます）\n" +
        "2. 船の名前・メーカー名は英語のまま。カタカナに音訳しない\n" +
        "3. 人物名・企業名の固有名詞は英語のまま。カタカナに音訳しない\n" +
        "4. UIラベル、説明文、ミッションテキストは自然な日本語に\n" +
        "5. %ls %s ~action(xxx) @xxx ~mission(xxx) <EM4> </EM4> 等のタグやプレースホルダーはそのまま保持\n" +
        "6. 空文字・数字のみ・記号のみはそのまま返す\n" +
        "7. \\nは改行として保持\n\n" +
        "入力: 1行1エントリ、タブ区切り「キー<TAB>英語テキスト」\n" +
        "出力: 1行1エントリ、タブ区切り「キー<TAB>日本語テキスト」\n" +
        "入力と同じ行数・同じキーで、タブ区切りの翻訳結果のみ出力。説明や装飾は不要。";

    private static List<(string English, string Japanese)>? _glossary;
    private static List<string>? _locationNames;
    private static List<string>? _shipNames;

    public static void SetGlossary(List<(string English, string Japanese)>? glossary)
    {
        _glossary = glossary;
    }

    public static void SetLocationNames(List<string>? names) => _locationNames = names;
    public static void SetShipNames(List<string>? names) => _shipNames = names;

    private static string? _cacheDir;

    public static void SetCacheDir(string dir)
    {
        _cacheDir = string.IsNullOrWhiteSpace(dir) ? null : dir;
        Console.WriteLine($"  ProperNouns cache dir: {_cacheDir ?? "(temp)" + " → " + Path.GetTempPath()}");
    }

    private static string LocationCachePath => Path.Combine(_cacheDir ?? Path.GetTempPath(), "uex_location_names.txt");
    private static string ShipCachePath => Path.Combine(_cacheDir ?? Path.GetTempPath(), "uex_ship_names.txt");

    public static async Task FetchAndCacheProperNounsAsync()
    {
        Console.WriteLine($"  FetchAndCacheProperNouns: LocationCache={LocationCachePath}, ShipCache={ShipCachePath}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 地名
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] locEndpoints = [
                "https://api.uexcorp.space/2.0/star_systems",
                "https://api.uexcorp.space/2.0/planets",
                "https://api.uexcorp.space/2.0/moons",
                "https://api.uexcorp.space/2.0/space_stations",
                "https://api.uexcorp.space/2.0/cities",
                "https://api.uexcorp.space/2.0/outposts",
            ];
            foreach (var url in locEndpoints)
            {
                try
                {
                    var resp = await http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(resp);
                    if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                            names.Add(n.GetString()!);
                        if (item.TryGetProperty("nickname", out var nick) && !string.IsNullOrWhiteSpace(nick.GetString()))
                            names.Add(nick.GetString()!);
                    }
                }
                catch { }
            }
            if (names.Count > 0)
            {
                _locationNames = names.OrderBy(n => n).ToList();
                await File.WriteAllLinesAsync(LocationCachePath, _locationNames);
                Console.WriteLine($"  Location names cached: {_locationNames.Count} entries → {LocationCachePath}");
            }
            else
            {
                Console.WriteLine("  WARNING: No location names fetched from UEX API");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  Location fetch error: {ex.Message}"); }

        // 船名
        try
        {
            var ships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] shipEndpoints = [
                "https://api.uexcorp.space/2.0/vehicles",
                "https://api.uexcorp.space/2.0/vehicles_loaners",
            ];
            foreach (var url in shipEndpoints)
            {
                try
                {
                    var resp = await http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(resp);
                    if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                            ships.Add(n.GetString()!);
                        if (item.TryGetProperty("name_full", out var nf) && !string.IsNullOrWhiteSpace(nf.GetString()))
                            ships.Add(nf.GetString()!);
                    }
                }
                catch { }
            }
            // メーカー名も追加
            try
            {
                var resp = await http.GetStringAsync("https://api.uexcorp.space/2.0/companies");
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        if (item.TryGetProperty("name", out var n) && !string.IsNullOrWhiteSpace(n.GetString()))
                            ships.Add(n.GetString()!);
                    }
                }
            }
            catch { }

            if (ships.Count > 0)
            {
                _shipNames = ships.OrderBy(n => n).ToList();
                await File.WriteAllLinesAsync(ShipCachePath, _shipNames);
                Console.WriteLine($"  Ship names cached: {_shipNames.Count} entries → {ShipCachePath}");
            }
            else
            {
                Console.WriteLine("  WARNING: No ship names fetched from UEX API");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  Ship fetch error: {ex.Message}"); }
    }

    public static void LoadProperNounsFromCache()
    {
        try
        {
            Console.WriteLine($"  Loading proper nouns from cache: {LocationCachePath}");
            if (File.Exists(LocationCachePath))
            {
                _locationNames = File.ReadAllLines(LocationCachePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                Console.WriteLine($"  Location names loaded: {_locationNames.Count}");
            }
            else
            {
                Console.WriteLine($"  WARNING: Location cache not found at {LocationCachePath}");
            }
            if (File.Exists(ShipCachePath))
            {
                _shipNames = File.ReadAllLines(ShipCachePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                Console.WriteLine($"  Ship names loaded: {_shipNames.Count}");
            }
            else
            {
                Console.WriteLine($"  WARNING: Ship cache not found at {ShipCachePath}");
            }
        }
        catch (Exception ex) { Console.WriteLine($"  Cache load error: {ex.Message}"); }
    }

    public static bool HasCachedProperNouns()
    {
        var has = File.Exists(LocationCachePath) && File.Exists(ShipCachePath);
        Console.WriteLine($"  HasCachedProperNouns: {has} (loc={File.Exists(LocationCachePath)}, ship={File.Exists(ShipCachePath)})");
        Console.WriteLine($"    LocationCachePath={LocationCachePath}");
        Console.WriteLine($"    ShipCachePath={ShipCachePath}");
        return has;
    }

    protected static string SystemPrompt
    {
        get
        {
            var sb = new StringBuilder(BaseSystemPrompt);

            if (_locationNames != null && _locationNames.Count > 0)
            {
                sb.Append("\n\n地名リスト（以下は全て固有地名です。カタカナに音訳せず英語のまま出力すること）：\n");
                sb.Append(string.Join(", ", _locationNames));
                sb.Append('\n');
            }

            if (_shipNames != null && _shipNames.Count > 0)
            {
                sb.Append("\n\n船名・メーカー名リスト（以下は全て固有名詞です。カタカナに音訳せず英語のまま出力すること）：\n");
                sb.Append(string.Join(", ", _shipNames));
                sb.Append('\n');
            }

            if (_glossary != null && _glossary.Count > 0)
            {
                sb.Append("\n\n用語集（以下の英語は必ず指定の日本語に翻訳すること）：\n");
                foreach (var (en, ja) in _glossary)
                    sb.Append($"・{en} → {ja}\n");
            }

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
            "openai" => new OpenAiBackend(config),
            "ollama" => new OllamaBackend(config),
            _ => throw new ArgumentException($"Unknown backend type: {config.Type}")
        };
    }
}
