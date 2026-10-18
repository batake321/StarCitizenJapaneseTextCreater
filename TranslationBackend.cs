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
        "3. 人物名・企業名の固有名詞は英語のまま。カタカナに音訳しない。愛称（あだ名・ニックネーム）も原文のままで訳さない\n" +
        "4. 船の装備・武器・部品の製品名・型名は英語のまま。カタカナに音訳しない（種類を表す一般名詞は訳してよい）\n" +
        "5. UIラベル、説明文、ミッションテキストは自然な日本語に\n" +
        "6. %ls %s ~action(xxx) @xxx ~mission(xxx) <EM4> </EM4> 等のタグやプレースホルダーはそのまま保持\n" +
        "7. 空文字・数字のみ・記号のみはそのまま返す\n" +
        "8. 原文中の \\n は実際の改行にせず「\\」と「n」の2文字のまま出力する（1エントリは必ず1行に収める）\n" +
        "9. {name1} {name2} 等の {nameN} は名前の置き換え記号。訳さず番号も変えずにそのまま保持\n" +
        "10. 英語テキストに人名（登場人物の名前）があるエントリは、そのエントリの訳の行の次に「人名<TAB>キー<TAB>人名1<TAB>人名2…」の行を1行出力する。" +
        "人名は英語テキストに書かれている綴りのまま（訳さない・音訳しない）。{nameN} は人名に含めない。人名が無いエントリはこの行を出さない\n\n" +
        "入力: 1行1エントリ、タブ区切り「キー<TAB>英語テキスト」\n" +
        "出力: 1行1エントリ、タブ区切り「キー<TAB>日本語テキスト」（と、ルール10の「人名<TAB>キー<TAB>人名…」の行）\n" +
        "訳の行は入力と同じ行数・同じキーで、タブ区切りの翻訳結果と人名の行のみ出力。説明や装飾は不要。";

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

    // 応答の人名の行の先頭の欄 (「人名<TAB>キー<TAB>人名1<TAB>人名2…」。BaseSystemPrompt のルール10)
    public const string PersonNamesLineLabel = "人名";

    // ParseResponse の結果。キー → 訳 (Dictionary そのもの) と、キー → AI が返した人名の一覧 (原語のまま。人名の行が無いキーは入らない)。
    // 各バックエンドは ParseResponse の結果をそのまま返すので、TranslateAsync の結果をこの型として読めば人名も取れる
    public class TranslationResult : Dictionary<string, string>
    {
        public Dictionary<string, List<string>> PersonNames { get; } = new(StringComparer.Ordinal);
    }

    // AI が訳に付ける「<0x0D>」のような文字列 (<0x と 16 進 2 桁と >)
    private static readonly System.Text.RegularExpressions.Regex ByteLiteralRegex = new(@"<0x[0-9A-Fa-f]{2}>");

    protected static Dictionary<string, string>? ParseResponse(string text, string backendName = "", List<TranslationEntry>? batch = null)
    {
        var result = new TranslationResult();
        var expectedKeys = batch?.Select(e => e.Key).ToHashSet();
        // 原文に「\n」がある (複数行の) 項目だけ、続きの行をつなげる。1 行の項目に余計な説明がくっつかないようにする
        var multiLineKeys = batch?.Where(e => e.English.Contains("\\n")).Select(e => e.Key).ToHashSet();

        // Strip markdown code fences
        text = StripCodeFences(text).Trim();

        // AI が訳文の途中で実際に改行してしまった場合、キーで始まらない行は直前の項目の続きとしてつなげる
        // (つながないと最初の行だけが保存され、訳が途中で切れる)
        string? lastKey = null;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r').Trim();
            // 空行は無視する。訳の中の改行は文字の「\n」で表すので、本当の空行が訳の中身になることはない
            // (つなぐと、空行ごとに余分な「\n」が付く)
            if (trimmed.Length == 0) continue;

            var tabIdx = trimmed.IndexOf('\t');
            var candidateKey = tabIdx > 0 ? trimmed[..tabIdx].Trim() : null;

            // 人名の行「人名<TAB>キー<TAB>人名1<TAB>人名2…」。キーが送ったキーでないもの (batch があるとき) は捨てる。
            // 直前の項目の続きの行としては扱わない (lastKey はそのまま。人名の行の後にその項目の続きの行が来てもつなぐ)
            if (candidateKey == PersonNamesLineLabel)
            {
                var cols = trimmed[(tabIdx + 1)..].Split('\t').Select(c => c.Trim()).ToList();
                var nameKey = cols[0];
                if (nameKey.Length > 0 && (expectedKeys == null || expectedKeys.Contains(nameKey)))
                {
                    if (!result.PersonNames.TryGetValue(nameKey, out var list)) result.PersonNames[nameKey] = list = [];
                    foreach (var n in cols.Skip(1))
                        if (n.Length > 0 && !list.Contains(n)) list.Add(n);
                }
                continue;
            }
            bool isNewEntry = candidateKey != null && (expectedKeys == null || expectedKeys.Contains(candidateKey));

            if (isNewEntry)
            {
                var ja = trimmed[(tabIdx + 1)..].Trim();
                if (ja.Length > 0)
                {
                    result[candidateKey!] = ja;
                    lastKey = candidateKey;
                }
                else lastKey = null;
            }
            else if (lastKey != null && multiLineKeys != null && multiLineKeys.Contains(lastKey))
            {
                // 続きの行。直前が「\」で終わっていたら「\n」の途中で改行されたものとして扱う
                var prev = result[lastKey];
                if (prev.EndsWith('\\'))
                    result[lastKey] = prev[..^1] + "\\n" + (trimmed.StartsWith('n') ? trimmed[1..] : trimmed);
                else
                    result[lastKey] = prev + "\\n" + trimmed;
            }
        }
        // 訳の中の「<0x0D>」のような <0x..> の文字列 (AI が付けるもの。本当の改行コードではない) のうち、原文に無いものを取り除き、
        // 取り除いた訳は前後の空白を除く (上で訳の前後の空白を除くのと同じ)。末尾の「\n」を整える前に行う
        var englishOf = batch?.GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.First().English);
        foreach (var k in result.Keys.ToList())
        {
            var en = englishOf != null && englishOf.TryGetValue(k, out var e0) ? e0 : "";
            var v = result[k];
            var cleaned = ByteLiteralRegex.Replace(v, m => en.Contains(m.Value, StringComparison.Ordinal) ? m.Value : "");
            if (cleaned != v) result[k] = cleaned.Trim();
        }
        // 続きの行をつないだ結果、末尾に付いた余分な「\n」を取り除く (原文が「\n」で終わっているものはそのまま)
        if (batch != null)
        {
            var english = englishOf!;
            foreach (var k in result.Keys.ToList())
            {
                if (english.TryGetValue(k, out var en) && en.EndsWith("\\n")) continue;
                var v = result[k];
                while (v.EndsWith("\\n")) v = v[..^2];
                result[k] = v;
            }
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
