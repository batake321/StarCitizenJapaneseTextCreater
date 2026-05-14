using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public class ChatService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly List<string> _debugLog = new();
    public static IReadOnlyList<string> DebugLog => _debugLog;
    public static void ClearDebugLog() => _debugLog.Clear();
    public static string? LogDirectory { get; set; }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _debugLog.Add(line);
        try
        {
            var logDir = LogDirectory ?? _gameDataExtractor?.ToolsDir ?? System.IO.Path.GetTempPath();
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "chat_debug.log"), line + "\n");
        }
        catch { }
    }

    private static readonly string ChatSystemPrompt =
        "あなたはゲーム「Star Citizen」の情報アシスタントです。プレイヤーからの質問に日本語で回答してください。\n\n" +
        "【重要】以下のツール（スキル）を使って最新のゲームデータを取得できます。質問に答えるために必要なデータは必ずツールを使って取得してください。\n" +
        "- search_ship: 船・機体の検索（部分一致で複数候補がある場合は番号付きリストで提示し、ユーザーに選択させてください）\n" +
        "- search_commodity: 商品・資源の検索と場所別価格\n" +
        "- search_item: 武器・コンポーネントの検索\n" +
        "- search_mission: ミッション・契約の検索\n" +
        "- search_price: アイテムの販売場所・価格の検索\n" +
        "- search_wiki: Wiki からの詳細情報取得\n" +
        "- search_pledge: RSI プレッジ価格・Warbond 割引情報（販売状況は変動するため公式確認を案内）\n\n" +
        "【候補提示ルール】\n" +
        "- 検索結果が複数ある場合は、番号付きリストで候補を提示してください\n" +
        "- ユーザーが番号で選択したら、その候補の詳細を取得してください\n" +
        "- 「その他」で直接入力も可能にしてください\n\n" +
        "【価格データのルール】\n" +
        "- ツールから取得した価格は正確な数値です。絶対に独自の数値を作らず、ツールが返した数値をそのまま使ってください\n" +
        "- 「購入場所」はプレイヤーが商品を買える場所（price_buy）、「売却場所」はプレイヤーが商品を売れる場所（price_sell）です\n" +
        "- 購入場所を聞かれた場合は、どこで高く売れるかも併せて紹介してください\n" +
        "- 前の質問の続きでも、必ずツールを再呼出しして最新データを取得してください\n\n" +
        "回答は簡潔で分かりやすい日本語でお願いします。\n" +
        "憶測で回答しないでください。データに記載がなく、確信が持てない場合は「わかりません」と正直に答えてください。";

    // UEX カテゴリID: 武器・コンポーネント系
    private static readonly Dictionary<string, int[]> ItemCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        {"weapon",  new[] { 32, 33, 34, 35, 70, 79 }}, // Guns, Missile Racks, Missiles, Turrets, Bombs, Point Defense
        {"武器",    new[] { 32, 33, 34, 35, 70, 79 }},
        {"gun",     new[] { 32 }},
        {"missile", new[] { 33, 34 }},
        {"ミサイル", new[] { 33, 34 }},
        {"turret",  new[] { 35 }},
        {"タレット", new[] { 35 }},
        {"cooler",  new[] { 19 }},
        {"クーラー", new[] { 19 }},
        {"power",   new[] { 21 }},
        {"パワー",  new[] { 21 }},
        {"quantum",  new[] { 22 }},
        {"クォンタム", new[] { 22 }},
        {"shield",  new[] { 23 }},
        {"シールド", new[] { 23 }},
        {"module",  new[] { 74 }},
        {"モジュール", new[] { 74 }},
        {"component", new[] { 19, 21, 22, 23 }},
        {"コンポーネント", new[] { 19, 21, 22, 23 }},
    };

    private static GameDataExtractor? _gameDataExtractor;
    private static GameDataQueryService? _queryService;
    private static string? _translationDbPath;

    public static void SetGameDataExtractor(GameDataExtractor extractor)
    {
        _gameDataExtractor = extractor;
    }

    public static void SetGameDataQueryService(GameDataQueryService service)
    {
        _queryService = service;
    }

    public static void SetTranslationDbPath(string dbPath)
    {
        _translationDbPath = dbPath;
    }

    private static List<string> LookupEnglishKeywords(string japaneseQuery)
    {
        if (string.IsNullOrEmpty(_translationDbPath) || !File.Exists(_translationDbPath))
            return new List<string>();

        var results = new List<string>();
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_translationDbPath};Mode=ReadOnly");
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT english FROM translations WHERE japanese LIKE $q AND english != '' LIMIT 20";
            cmd.Parameters.AddWithValue("$q", $"%{japaneseQuery}%");
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                results.Add(reader.GetString(0));

            if (results.Count == 0)
            {
                using var gCmd = conn.CreateCommand();
                gCmd.CommandText = "SELECT english FROM glossary WHERE japanese LIKE $q LIMIT 10";
                gCmd.Parameters.AddWithValue("$q", $"%{japaneseQuery}%");
                using var gReader = gCmd.ExecuteReader();
                while (gReader.Read())
                    results.Add(gReader.GetString(0));
            }
        }
        catch { }
        return results;
    }

    private static string ExtractEnglishKeyword(string query)
    {
        // 1. ハードコードマップ（高速・確実）
        var jaKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"サルベージ", "salvage"}, {"賞金首", "bounty"}, {"賞金稼ぎ", "bounty"},
            {"救助", "rescue"}, {"配達", "delivery"}, {"調査", "investigation"},
            {"戦闘", "combat"}, {"採掘", "mining"}, {"偵察", "recon"},
            {"暗殺", "assassination"}, {"護衛", "escort"}, {"輸送", "cargo"},
            {"密輸", "smuggling"}, {"回収", "retrieval"}, {"哨戒", "patrol"},
            {"契約", "contract"}, {"ミッション", "mission"}, {"傭兵", "mercenary"},
            {"海賊", "pirate"}, {"探索", "exploration"}, {"清掃", "cleanup"},
        };

        foreach (var (ja, en) in jaKeywords)
            if (query.Contains(ja, StringComparison.OrdinalIgnoreCase))
                return en;

        // 2. 既にASCII英語ならそのまま返す
        if (query.All(c => c < 128)) return query;

        // 3. 翻訳DB逆引き（japanese→english）
        var dbResults = LookupEnglishKeywords(query);
        if (dbResults.Count > 0)
        {
            var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var eng in dbResults)
                foreach (var w in eng.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (w.Length > 3) words.Add(w);
            if (words.Count > 0) return words.First();
        }

        // 4. フォールバック
        return query;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<Dictionary<string, object>> GetToolDefinitions()
    {
        return new List<Dictionary<string, object>>
        {
            new()
            {
                ["name"] = "search_ship",
                ["description"] = "船・機体を名前で検索。部分一致で複数候補がある場合は候補リストを返す。ユーザーに番号で選択させること。",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "船名（部分一致可）" },
                        ["exact"] = new Dictionary<string, object> { ["type"] = "boolean", ["description"] = "完全一致検索するか" }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            new()
            {
                ["name"] = "search_commodity",
                ["description"] = "商品・資源を検索（場所別価格含む）",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "商品名" },
                        ["system"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "星系名（例: Stanton）" }
                    },
                    ["required"] = new[] { "name" }
                }
            },
            new()
            {
                ["name"] = "search_item",
                ["description"] = "武器・コンポーネント検索",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "アイテム名やキーワード" },
                        ["category"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "カテゴリ: weapon/shield/cooler/power/quantum" }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            new()
            {
                ["name"] = "search_mission",
                ["description"] = "ミッション・契約を検索。ゲーム内データは英語なので、queryには英語キーワードを使用してください（例: salvage, bounty, rescue, delivery, investigation）",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "英語のミッション名やキーワード（例: salvage, bounty, rescue）" },
                        ["system"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "星系名" }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            new()
            {
                ["name"] = "search_price",
                ["description"] = "アイテムの販売場所・価格",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["item_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "アイテム名" },
                        ["system"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "星系名" }
                    },
                    ["required"] = new[] { "item_name" }
                }
            },
            new()
            {
                ["name"] = "search_wiki",
                ["description"] = "Wiki から詳細情報取得",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["page_title"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "Wikiページタイトル（船名など）" }
                    },
                    ["required"] = new[] { "page_title" }
                }
            },
            new()
            {
                ["name"] = "search_pledge",
                ["description"] = "RSI プレッジ価格・Warbond 割引情報を検索。販売状況は時期により変動するため、結果には公式サイト確認の注意を含みます。",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["ship_name"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "船名" }
                    },
                    ["required"] = new[] { "ship_name" }
                }
            }
        };
    }

    private static List<Dictionary<string, object>> GetGeminiFunctionDeclarations()
    {
        var tools = GetToolDefinitions();
        var declarations = new List<Dictionary<string, object>>();
        foreach (var tool in tools)
        {
            var schema = (Dictionary<string, object>)tool["input_schema"];
            declarations.Add(new Dictionary<string, object>
            {
                ["name"] = tool["name"],
                ["description"] = (string)tool["description"],
                ["parameters"] = schema
            });
        }
        return declarations;
    }

    public static async Task<string> ExecuteToolAsync(string toolName, JsonElement args)
    {
        Log($"TOOL CALL: {toolName}({args})");
        try
        {
            var result = toolName switch
            {
                "search_ship" => await ExecuteSearchShipAsync(args),
                "search_commodity" => await ExecuteSearchCommodityAsync(args),
                "search_item" => await ExecuteSearchItemAsync(args),
                "search_mission" => await ExecuteSearchMissionAsync(args),
                "search_price" => await ExecuteSearchPriceAsync(args),
                "search_wiki" => await ExecuteSearchWikiAsync(args),
                "search_pledge" => await FetchPledgeInfoAsync(args),
                _ => $"[不明なツール: {toolName}]"
            };
            Log($"TOOL RESULT ({toolName}): {result[..Math.Min(500, result.Length)]}{(result.Length > 500 ? "..." : "")}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"TOOL ERROR ({toolName}): {ex.Message}");
            return $"[ツール実行エラー ({toolName}): {ex.Message}]";
        }
    }

    private static async Task<string> ExecuteSearchShipAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var exact = args.TryGetProperty("exact", out var e) && e.GetBoolean();

        var results = new List<string>();

        var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles");
        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return "[データ取得失敗]";

        var resolvedName = ExtractShipName(query);

        foreach (var v in data.EnumerateArray())
        {
            var name = v.GetProperty("name").GetString() ?? "";
            var manufacturer = v.TryGetProperty("manufacturer_name", out var mfr) ? mfr.GetString() ?? "" : "";
            var focus = v.TryGetProperty("focus", out var f) ? f.GetString() ?? "" : "";
            var crew = v.TryGetProperty("crew", out var c) ? c.ToString() : "";
            var cargo = v.TryGetProperty("scu", out var cg) ? cg.ToString() : "";
            var price = v.TryGetProperty("price", out var p) ? p.ToString() : "";
            var size = v.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";

            bool match;
            if (exact)
                match = name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(resolvedName) && name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));
            else
                match = name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(resolvedName) && name.Contains(resolvedName, StringComparison.OrdinalIgnoreCase));

            if (match)
                results.Add($"- {manufacturer} {name} | 役割: {focus} | 乗員: {crew} | カーゴ: {cargo} SCU | サイズ: {size} | 価格: {price} aUEC");
        }

        if (results.Count == 0) return $"'{query}' に該当する機体は見つかりませんでした。";
        if (results.Count > 10)
        {
            var sb = new StringBuilder($"検索結果: {results.Count}件\n");
            for (int i = 0; i < Math.Min(20, results.Count); i++)
                sb.AppendLine($"{i + 1}. {results[i]}");
            if (results.Count > 20) sb.AppendLine($"... 他{results.Count - 20}件");
            return sb.ToString();
        }

        return string.Join("\n", results);
    }

    private static string GetDistanceCategory(string buyPlanet, string buyMoon, string sellPlanet, string sellMoon)
    {
        if (!string.IsNullOrEmpty(buyMoon) && buyMoon == sellMoon)
            return "近距離（同じ衛星上）";
        if (buyPlanet == sellPlanet)
            return "短距離（同じ惑星圏 / QT数分）";
        return "長距離（別惑星間 / QT 5-10分+）";
    }

    private static async Task<string> ExecuteSearchCommodityAsync(JsonElement args)
    {
        var name = args.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var system = args.TryGetProperty("system", out var s) ? s.GetString() : null;

        var dcbCommodityResult = "";
        if (_gameDataExtractor?.IsReady == true)
        {
            try
            {
                var dcbData = await _gameDataExtractor.QueryCommoditiesAsync(name);
                if (!string.IsNullOrEmpty(dcbData)) dcbCommodityResult = dcbData;
            }
            catch { }
        }

        var commResp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/commodities");
        using var commDoc = JsonDocument.Parse(commResp);
        if (!commDoc.RootElement.TryGetProperty("data", out var commData))
            return string.IsNullOrEmpty(dcbCommodityResult) ? "[データ取得失敗]" : dcbCommodityResult;

        var resolvedName = name;
        if (CommodityNameMap.TryGetValue(name, out var mapped)) resolvedName = mapped;

        int? commodityId = null;
        string commodityName = "";
        foreach (var c in commData.EnumerateArray())
        {
            var cName = c.GetProperty("name").GetString() ?? "";
            if (cName.Contains(resolvedName, StringComparison.OrdinalIgnoreCase))
            {
                commodityId = c.GetProperty("id").GetInt32();
                commodityName = cName;
                break;
            }
        }
        if (commodityId == null) return $"'{name}' に該当する商品が見つかりませんでした。";

        var priceResp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/commodities_prices?id_commodity={commodityId}");
        using var priceDoc = JsonDocument.Parse(priceResp);
        if (!priceDoc.RootElement.TryGetProperty("data", out var priceData)) return $"'{commodityName}' の価格データが見つかりませんでした。";

        var buyLocations = new List<(string location, double price, string planet, string moon)>();
        var sellLocations = new List<(string location, double price, string planet, string moon)>();

        foreach (var item in priceData.EnumerateArray())
        {
            var terminal = item.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
            var city = item.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
            var outpost = item.TryGetProperty("outpost_name", out var on) ? on.GetString() ?? "" : "";
            var moon = item.TryGetProperty("moon_name", out var mn) ? mn.GetString() ?? "" : "";
            var planet = item.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
            var star = item.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
            var priceBuy = item.TryGetProperty("price_buy", out var pb) && pb.ValueKind == JsonValueKind.Number ? pb.GetDouble() : 0;
            var priceSell = item.TryGetProperty("price_sell", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetDouble() : 0;

            if (!string.IsNullOrEmpty(system) && !star.Contains(system, StringComparison.OrdinalIgnoreCase)) continue;

            var locParts = new[] { star, planet, moon, city, outpost, terminal }.Where(x => !string.IsNullOrEmpty(x)).Distinct();
            var location = string.Join(" > ", locParts);
            if (priceBuy > 0) buyLocations.Add((location, priceBuy, planet, moon));
            if (priceSell > 0) sellLocations.Add((location, priceSell, planet, moon));
        }

        var sb = new StringBuilder($"商品: {commodityName}");
        if (!string.IsNullOrEmpty(system)) sb.Append($" (フィルタ: {system}星系)");
        sb.AppendLine();

        if (buyLocations.Count > 0)
        {
            sb.AppendLine($"\n【購入場所】(安い順 — プレイヤーが買える場所)");
            foreach (var (loc, price, _, _) in buyLocations.OrderBy(x => x.price))
                sb.AppendLine($"- {loc} | {price:0} aUEC");
        }

        if (buyLocations.Count > 0 && sellLocations.Count > 0)
        {
            var bestBuy = buyLocations.OrderBy(x => x.price).First();
            var bestSell = sellLocations.OrderByDescending(x => x.price).First();
            var dist = GetDistanceCategory(bestBuy.planet, bestBuy.moon, bestSell.planet, bestSell.moon);
            var profit = bestSell.price - bestBuy.price;
            sb.AppendLine($"\n【おすすめルート】");
            sb.AppendLine($"  購入: {bestBuy.location} ({bestBuy.price:0} aUEC)");
            sb.AppendLine($"  売却: {bestSell.location} ({bestSell.price:0} aUEC)");
            sb.AppendLine($"  利益: +{profit:0} aUEC/単位 | 距離: {dist}");
        }

        if (sellLocations.Count > 0)
        {
            sb.AppendLine($"\n【売却場所】(高い順 — プレイヤーが売れる場所)");
            foreach (var (loc, price, _, _) in sellLocations.OrderByDescending(x => x.price))
                sb.AppendLine($"- {loc} | {price:0} aUEC");
        }

        int count = buyLocations.Count + sellLocations.Count;
        var uexResult = count > 0 ? sb.ToString() : $"'{commodityName}' の価格データが見つかりませんでした。";
        if (!string.IsNullOrEmpty(dcbCommodityResult))
            return dcbCommodityResult + "\n" + uexResult;
        return uexResult;
    }

    private static async Task<string> ExecuteSearchItemAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var category = args.TryGetProperty("category", out var c) ? c.GetString() : null;

        int[] categoryIds;
        if (!string.IsNullOrEmpty(category) && ItemCategoryMap.TryGetValue(category, out var ids))
            categoryIds = ids;
        else
            categoryIds = new[] { 32, 33, 34, 35, 19, 21, 22, 23, 70, 79 };

        var sb = new StringBuilder();
        int total = 0;
        foreach (var catId in categoryIds)
        {
            var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items?id_category={catId}");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
            foreach (var item in data.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var cat = item.TryGetProperty("category", out var catProp) ? catProp.GetString() ?? "" : "";
                var section = item.TryGetProperty("section", out var sec) ? sec.GetString() ?? "" : "";
                var company = item.TryGetProperty("company_name", out var co) ? co.GetString() ?? "" : "";
                var sz = item.TryGetProperty("size", out var szProp) ? szProp.GetString() ?? "" : "";
                var grade = item.TryGetProperty("quality", out var qProp) ? qProp.ToString() : "";
                sb.AppendLine($"- {name} | メーカー: {company} | カテゴリ: {section}/{cat} | サイズ: {sz} | グレード: {grade}");
                if (++total >= 30) break;
            }
            if (total >= 30) break;
        }

        if (_gameDataExtractor?.IsReady == true)
        {
            try
            {
                var compType = GameDataExtractor.DetectComponentType(query);
                if (compType == null && !string.IsNullOrEmpty(category))
                    compType = GameDataExtractor.DetectComponentType(category);
                var dcbData = await _gameDataExtractor.QueryGameDataAsync(query);
                if (!string.IsNullOrEmpty(dcbData)) sb.AppendLine(dcbData);
            }
            catch { }
        }

        if (total == 0 && sb.Length == 0)
        {
            var scResult = await FetchScTradeItemAsync(query);
            if (!string.IsNullOrEmpty(scResult)) return scResult;
            return $"'{query}' に該当するアイテムが見つかりませんでした。";
        }

        return sb.ToString();
    }

    private static async Task<string> ExecuteSearchMissionAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var system = args.TryGetProperty("system", out var s) ? s.GetString() : null;
        var englishQuery = ExtractEnglishKeyword(query);
        Log($"MISSION SEARCH: input='{query}' -> english='{englishQuery}'");

        // DB クエリ優先
        if (_queryService?.HasData() == true)
        {
            var dbResult = _queryService.SearchMissions(englishQuery);
            Log($"DB MISSION RESULT: {(string.IsNullOrEmpty(dbResult) ? "(empty)" : dbResult.Length + " chars")}");
            if (!string.IsNullOrEmpty(dbResult))
            {
                var uexResult = await FetchMissionsAsync(englishQuery, system);
                if (!string.IsNullOrEmpty(uexResult) && uexResult != "ミッション情報が見つかりませんでした。")
                    return dbResult + "\n" + uexResult;
                return dbResult;
            }
        }

        // DB にデータがない場合、インデックス未構築を通知して自動構築を試みる
        if (_queryService?.HasData() != true)
        {
            if (_gameDataExtractor != null && !_gameDataExtractor.IsStarBreakerInstalled)
            {
                Log("StarBreaker not installed, attempting auto-install...");
                try { await _gameDataExtractor.EnsureStarBreakerAsync(); } catch { }
            }
            if (_gameDataExtractor?.IsReady == true && !(_queryService?.HasData() == true))
            {
                Log("Auto-building structured index...");
                try
                {
                    var p4k = _gameDataExtractor.FindDataP4k();
                    if (p4k != null)
                    {
                        await _gameDataExtractor.BuildStructuredIndexAsync(p4k);
                        // QueryService を再接続
                        _queryService?.Dispose();
                        _queryService = new GameDataQueryService(_gameDataExtractor.DbPath);
                        var dbResult = _queryService.SearchMissions(englishQuery);
                        if (!string.IsNullOrEmpty(dbResult)) return dbResult;
                    }
                }
                catch (Exception ex) { Log($"Auto-index error: {ex.Message}"); }
            }

            if (_queryService?.HasData() != true)
                return $"ミッション情報が見つかりませんでした。\n⚠️ ゲームデータのインデックスが未構築です。設定タブの「インデックス構築」を実行すると、Data.p4k から契約・ミッションデータを検索できるようになります。";
        }

        return await FetchMissionsAsync(englishQuery, system);
    }

    private static async Task<string> ExecuteSearchPriceAsync(JsonElement args)
    {
        var itemName = args.TryGetProperty("item_name", out var n) ? n.GetString() ?? "" : "";
        var system = args.TryGetProperty("system", out var s) ? s.GetString() : null;

        int[] allCategoryIds = { 32, 33, 34, 35, 19, 21, 22, 23, 70, 79, 74 };
        var matchedItems = new List<(int id, string name)>();

        foreach (var catId in allCategoryIds)
        {
            var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items?id_category={catId}");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
            foreach (var item in data.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString() ?? "";
                var id = item.GetProperty("id").GetInt32();
                if (name.Contains(itemName, StringComparison.OrdinalIgnoreCase))
                    matchedItems.Add((id, name));
            }
        }

        if (matchedItems.Count == 0) return $"'{itemName}' に該当するアイテムが見つかりませんでした。";

        var sb = new StringBuilder();
        int count = 0;
        foreach (var (itemId, iName) in matchedItems.Take(5))
        {
            var priceResp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items_prices?id_item={itemId}");
            using var priceDoc = JsonDocument.Parse(priceResp);
            if (!priceDoc.RootElement.TryGetProperty("data", out var priceData)) continue;

            sb.AppendLine($"【{iName}】");
            foreach (var p in priceData.EnumerateArray())
            {
                var terminal = p.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
                var city = p.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
                var planet = p.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
                var star = p.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
                var buy = p.TryGetProperty("price_buy", out var pb) && pb.ValueKind == JsonValueKind.Number ? pb.GetDouble() : 0;

                if (!string.IsNullOrEmpty(system) && !star.Contains(system, StringComparison.OrdinalIgnoreCase)) continue;

                if (buy > 0)
                {
                    var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(x => !string.IsNullOrEmpty(x)));
                    sb.AppendLine($"  - {location} | 価格: {buy:0.##} aUEC");
                    count++;
                }
            }
        }

        return count > 0 ? sb.ToString() : $"'{itemName}' の販売場所が見つかりませんでした。";
    }

    private static async Task<string> ExecuteSearchWikiAsync(JsonElement args)
    {
        var pageTitle = args.TryGetProperty("page_title", out var p) ? p.GetString() ?? "" : "";
        var result = await FetchWikiShipDataAsync(pageTitle);
        return result ?? $"Wiki ページ '{pageTitle}' が見つからないか、データがありませんでした。";
    }

    private static async Task<string> FetchPledgeInfoAsync(JsonElement args)
    {
        var shipName = args.TryGetProperty("ship_name", out var s) ? s.GetString() ?? "" : "";
        return await FetchPledgeInfoAsync(shipName);
    }

    private static async Task<string> FetchPledgeInfoAsync(string shipName)
    {
        try
        {
            var sb = new StringBuilder();

            var listUrl = "https://starcitizen.tools/api.php?action=parse&page=List_of_pledge_vehicles&prop=text&format=json";
            var listResp = await Http.GetAsync(listUrl);
            if (listResp.IsSuccessStatusCode)
            {
                var listJson = await listResp.Content.ReadAsStringAsync();
                using var listDoc = JsonDocument.Parse(listJson);
                if (listDoc.RootElement.TryGetProperty("parse", out var parse))
                {
                    var html = parse.GetProperty("text").GetProperty("*").GetString() ?? "";
                    var rows = System.Text.RegularExpressions.Regex.Matches(html, @"<tr>(.*?)</tr>",
                        System.Text.RegularExpressions.RegexOptions.Singleline);
                    foreach (System.Text.RegularExpressions.Match row in rows)
                    {
                        var rowText = StripHtml(row.Groups[1].Value);
                        if (rowText.Contains(shipName, StringComparison.OrdinalIgnoreCase))
                        {
                            var cells = System.Text.RegularExpressions.Regex.Matches(row.Groups[1].Value, @"<td[^>]*>(.*?)</td>",
                                System.Text.RegularExpressions.RegexOptions.Singleline);
                            var cellValues = new List<string>();
                            foreach (System.Text.RegularExpressions.Match cell in cells)
                                cellValues.Add(StripHtml(cell.Groups[1].Value).Trim());
                            if (cellValues.Count >= 3)
                                sb.AppendLine($"プレッジ情報: {string.Join(" | ", cellValues)}");
                        }
                    }
                }
            }

            var wikiTitle = shipName.Replace(" ", "_");
            var escapedTitle = Uri.EscapeDataString(wikiTitle);
            var pageUrl = $"https://starcitizen.tools/api.php?action=parse&page={escapedTitle}&prop=text&format=json";
            var pageResp = await Http.GetAsync(pageUrl);
            if (pageResp.IsSuccessStatusCode)
            {
                var pageJson = await pageResp.Content.ReadAsStringAsync();
                using var pageDoc = JsonDocument.Parse(pageJson);
                if (pageDoc.RootElement.TryGetProperty("parse", out var parse))
                {
                    var html = parse.GetProperty("text").GetProperty("*").GetString() ?? "";
                    var plainText = StripHtml(html);
                    var pledgePatterns = new[] { "pledge", "warbond", "standalone", "price", "USD", "$" };
                    var lines = plainText.Split('\n');
                    foreach (var line in lines)
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length < 5) continue;
                        foreach (var pat in pledgePatterns)
                        {
                            if (trimmed.Contains(pat, StringComparison.OrdinalIgnoreCase))
                            {
                                sb.AppendLine(trimmed);
                                break;
                            }
                        }
                    }
                }
            }

            var disclaimer = "\n⚠️ Warbond の有無や船の販売状況は時期により変動します。最新の販売・割引情報は必ず RSI 公式サイト (https://robertsspaceindustries.com/pledge) でご確認ください。";
            if (sb.Length == 0) return $"'{shipName}' のプレッジ情報が見つかりませんでした。{disclaimer}";
            return $"=== {shipName} プレッジ情報 ===\n{sb}{disclaimer}";
        }
        catch (Exception ex)
        {
            return $"[プレッジ情報取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string> FetchMissionsAsync(string? query, string? system = null)
    {
        Log($"FetchMissionsAsync: query='{query}', system='{system}'");
        var sb = new StringBuilder();

        try
        {
            var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/contracts");
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                int count = 0;
                foreach (var item in data.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                    var payout = item.TryGetProperty("payout", out var p) ? p.ToString() : "";
                    var location = item.TryGetProperty("location", out var l) ? l.GetString() ?? "" : "";
                    var contact = item.TryGetProperty("contact", out var c) ? c.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(query) &&
                        !name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !desc.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;

                    if (!string.IsNullOrEmpty(system) &&
                        !location.Contains(system, StringComparison.OrdinalIgnoreCase)) continue;

                    var truncDesc = desc.Length > 100 ? desc[..100] + "..." : desc;
                    sb.AppendLine($"- {name} | 報酬: {payout} aUEC | 場所: {location} | 連絡先: {contact}");
                    if (!string.IsNullOrEmpty(truncDesc)) sb.AppendLine($"  概要: {truncDesc}");
                    if (++count >= 20) break;
                }
                Log($"UEX contracts matched: {count}");
            }
        }
        catch (Exception ex) { Log($"UEX contracts error: {ex.Message}"); }

        Log($"GameDataExtractor ready={_gameDataExtractor?.IsReady}, installed={_gameDataExtractor?.IsStarBreakerInstalled}");
        if (_gameDataExtractor?.IsReady == true)
        {
            try
            {
                var missionData = await _gameDataExtractor.QueryMissionsAsync(query ?? "");
                Log($"DCB mission result: {(string.IsNullOrEmpty(missionData) ? "(empty)" : missionData.Length + " chars")}");
                if (!string.IsNullOrEmpty(missionData)) sb.AppendLine(missionData);
            }
            catch (Exception ex) { Log($"DCB mission error: {ex.Message}"); }
        }

        if (sb.Length == 0) return "ミッション情報が見つかりませんでした。";
        return sb.ToString();
    }

    public static async Task<string> FetchScDataAsync(string query)
    {
        var sb = new StringBuilder();
        var lowerQuery = query.ToLowerInvariant();

        var tasks = new List<Task<string?>>();

        // ローカルゲームデータ（Data.p4k → StarBreaker オンデマンドクエリ）
        if (_gameDataExtractor?.IsReady == true)
            tasks.Add(_gameDataExtractor.QueryGameDataAsync(query));

        // 常に機体・商品データを取得
        tasks.Add(FetchUexVehiclesAsync(query));
        tasks.Add(FetchUexCommoditiesAsync(query));
        tasks.Add(FetchUexCommodityPricesAsync(query));

        // Wiki から機体詳細（ハードポイント・説明）を取得
        var shipName = ExtractShipName(query);
        if (!string.IsNullOrEmpty(shipName))
            tasks.Add(FetchWikiShipDataAsync(shipName));

        // UEX アイテム（武器・コンポーネント）データ
        var categoryIds = ExtractItemCategories(query);
        if (categoryIds.Length > 0)
        {
            tasks.Add(FetchUexItemsAsync(query, categoryIds));
            tasks.Add(FetchUexItemPricesAsync(query, categoryIds));
        }

        // SC Trade Tools アイテム検索
        var itemKeyword = ExtractItemKeyword(query);
        if (!string.IsNullOrEmpty(itemKeyword))
            tasks.Add(FetchScTradeItemAsync(itemKeyword));

        // SC Trade Tools 商品ショップ一覧（貿易・経済関連の質問時）
        if (ContainsAny(lowerQuery, "trade", "route", "profit", "貿易", "交易", "ルート", "利益", "稼",
            "安い", "高い", "最安", "最高", "cheapest", "expensive", "price", "値段", "価格", "いくら",
            "買える", "売れる", "どこで買", "どこで売"))
            tasks.Add(FetchScTradeCommodityShopsAsync());

        var scApiKey = App.Config.ScApiKey;
        if (!string.IsNullOrEmpty(scApiKey))
            tasks.Add(FetchScApiShipsAsync(scApiKey));

        if (ContainsAny(lowerQuery, "planet", "moon", "station", "location", "惑星", "月", "ステーション", "場所", "拠点",
            "stanton", "pyro", "星系"))
        {
            tasks.Add(FetchUexTerminalsAsync(query));
            if (!string.IsNullOrEmpty(scApiKey))
                tasks.Add(FetchScApiStarmapAsync(scApiKey));
        }

        try
        {
            var results = await Task.WhenAll(tasks);
            foreach (var r in results)
            {
                if (!string.IsNullOrEmpty(r))
                    sb.AppendLine(r);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[API取得エラー: {ex.Message}]");
        }

        return sb.ToString();
    }

    private static int[] ExtractItemCategories(string query)
    {
        var ids = new HashSet<int>();
        foreach (var (keyword, cats) in ItemCategoryMap)
        {
            if (query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                foreach (var id in cats) ids.Add(id);
        }
        if (ids.Count == 0)
        {
            if (ExtractShipName(query) != null ||
                ContainsAny(query, "武装", "装備", "ハードポイント", "hardpoint", "loadout", "デフォルト"))
                foreach (var id in new[] { 32, 33, 34, 35, 19, 21, 22, 23 }) ids.Add(id);
        }
        return ids.ToArray();
    }

    private static string ExtractItemKeyword(string query)
    {
        // 具体的なアイテム名を英語で抽出
        var itemPatterns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"リピーター", "Repeater"}, {"キャノン", "Cannon"}, {"ガトリング", "Gatling"},
            {"スキャッターガン", "Scattergun"}, {"レーザー", "Laser"}, {"バリスティック", "Ballistic"},
            {"ディストーション", "Distortion"}, {"ギンブル", "Gimbal"}, {"パワープラント", "Power Plant"},
            {"クーラー", "Cooler"}, {"シールドジェネレーター", "Shield Generator"},
            {"クォンタムドライブ", "Quantum Drive"},
        };
        foreach (var (ja, en) in itemPatterns)
        {
            if (query.Contains(ja, StringComparison.OrdinalIgnoreCase))
                return en;
        }
        // 英語アイテム名がそのまま入っている場合
        var words = query.Split(' ', '　', '?', '？', 'の', 'を', 'は', 'で');
        foreach (var w in words)
        {
            var trimmed = w.Trim();
            if (trimmed.Length >= 4 && trimmed.All(c => c < 128) &&
                !new[] { "what", "where", "which", "that", "this", "from", "with", "have", "does" }
                    .Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                return trimmed;
        }
        return "";
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly Dictionary<string, string> ShipAliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "F7C mk2", "F7C Hornet Mk II" },
        { "F7C mk 2", "F7C Hornet Mk II" },
        { "F7C mk1", "F7C Hornet Mk I" },
        { "F7C mk 1", "F7C Hornet Mk I" },
        { "F7A mk2", "F7A Hornet Mk II" },
        { "F7A mk 2", "F7A Hornet Mk II" },
        { "F7A mk1", "F7A Hornet Mk I" },
        { "F7A mk 1", "F7A Hornet Mk I" },
        { "Super Hornet mk2", "F7C-M Super Hornet Mk II" },
        { "Super Hornet mk 2", "F7C-M Super Hornet Mk II" },
        { "Super Hornet mk1", "F7C-M Super Hornet Mk I" },
        { "Hornet mk2", "F7C Hornet Mk II" },
        { "Hornet mk 2", "F7C Hornet Mk II" },
        { "Hornet mk1", "F7C Hornet Mk I" },
        { "ホーネット", "F7C Hornet Mk II" },
        { "スーパーホーネット", "F7C-M Super Hornet Mk II" },
        { "グラディウス", "Gladius" },
        { "カタピラー", "Caterpillar" },
        { "フリーランサー", "Freelancer" },
        { "オーロラ", "Aurora MR" },
        { "コンステレーション", "Constellation Andromeda" },
        { "コンステ", "Constellation Andromeda" },
        { "カットラス", "Cutlass Black" },
        { "アベンジャー", "Avenger Titan" },
        { "マーキュリー", "Mercury Star Runner" },
        { "スターランナー", "Mercury Star Runner" },
        { "コルセア", "Corsair" },
        { "ハリケーン", "Hurricane" },
        { "リクレイマー", "Reclaimer" },
        { "キャラック", "Carrack" },
        { "ハンマーヘッド", "Hammerhead" },
        { "バルキリー", "Valkyrie" },
        { "リディーマー", "Redeemer" },
        { "プロスペクター", "Prospector" },
        { "バルチャー", "Vulture" },
        { "セイバー", "Sabre" },
        { "スコーピアス", "Scorpius" },
        { "ヘラルド", "Herald" },
        { "マーチャントマン", "Merchantman" },
        { "BMM", "Merchantman" },
        { "リタリエーター", "Retaliator Bomber" },
        { "エクリプス", "Eclipse" },
        { "イクリプス", "Eclipse" },
        { "スターファーラー", "Starfarer" },
        { "ノマド", "Nomad" },
        { "ディフェンダー", "Defender" },
        { "プラウラー", "Prowler" },
        { "ドラゴンフライ", "Dragonfly" },
        { "サイクロン", "Cyclone" },
        { "バリスタ", "Ballista" },
        { "ギャラクシー", "Galaxy" },
        { "リベレーター", "Liberator" },
        { "アロー", "Arrow" },
        { "バッカニア", "Buccaneer" },
        { "レイザー", "Razor" },
        { "マスタング", "Mustang Alpha" },
        { "ピスケス", "Pisces" },
        { "モール", "Mole" },
        { "MSR", "Mercury Star Runner" },
        { "Connie", "Constellation Andromeda" },
        { "Cat", "Caterpillar" },
        { "Lancer", "Freelancer" },
        { "Tali", "Retaliator Bomber" },
        { "Vanguard", "Vanguard Warden" },
        { "Cutty Black", "Cutlass Black" },
        { "Cutty", "Cutlass Black" },
        { "MIS", "Freelancer MIS" },
    };

    public static string? ExtractShipNamePublic(string query) => ExtractShipName(query);

    private static string? ExtractShipName(string query)
    {
        foreach (var (alias, fullName) in ShipAliasMap.OrderByDescending(kv => kv.Key.Length))
        {
            if (query.Contains(alias, StringComparison.OrdinalIgnoreCase))
                return fullName;
        }

        var knownShips = new[] {
            "F7C Hornet Mk II", "F7C Hornet Mk I", "F7A Hornet Mk II", "F7A Hornet Mk I",
            "F7C-M Super Hornet Mk II", "F7C-M Super Hornet Mk I", "F7C-M Super Hornet",
            "F7C-R Hornet Tracker", "F7C-S Hornet Ghost",
            "RAFT", "Caterpillar", "Freelancer", "Freelancer MAX", "Freelancer DUR", "Freelancer MIS",
            "Aurora MR", "Aurora LN", "Aurora CL", "Aurora LX", "Aurora ES",
            "Constellation Andromeda", "Constellation Phoenix", "Constellation Aquila", "Constellation Taurus",
            "Gladius", "Arrow", "Sabre", "Vanguard Warden", "Vanguard Sentinel", "Vanguard Harbinger",
            "Retaliator Bomber", "Eclipse", "Hammerhead", "Reclaimer", "Carrack", "Merchantman",
            "Cutlass Black", "Cutlass Blue", "Cutlass Red", "Cutlass Steel",
            "Avenger Titan", "Avenger Stalker", "Avenger Warlock",
            "Prospector", "Mole", "Vulture", "Herald", "Mustang Alpha", "Mustang Delta",
            "Pisces", "Spirit A1", "Spirit C1", "Spirit E1",
            "Zeus Mk II ES", "Zeus Mk II MR", "Zeus Mk II CL",
            "Hull A", "Hull B", "Hull C", "Hull D", "Hull E",
            "Mercury Star Runner", "Corsair", "Scorpius", "Hurricane", "Inferno", "Ion",
            "Redeemer", "Valkyrie", "Liberator", "Starfarer", "Starfarer Gemini",
            "Nomad", "C8X Pisces", "100i", "125a", "135c", "300i", "315p", "325a", "350r",
            "400i", "600i", "890 Jump", "M50", "Razor", "Buccaneer", "Defender",
            "Prowler", "San'tok.yāi", "Blade", "Glaive", "Talon",
            "Reliant Kore", "Reliant Tana", "Reliant Sen", "Reliant Mako",
            "Titan Suit", "Dragonfly", "Nox", "X1", "HoverQuad", "PTV", "Cyclone",
            "Ballista", "Spartan", "Centurion", "Nova Tank", "Ursa Rover",
            "Starlancer MAX", "Starlancer TAC", "Galaxy",
        };

        foreach (var ship in knownShips.OrderByDescending(s => s.Length))
        {
            if (query.Contains(ship, StringComparison.OrdinalIgnoreCase))
                return ship;
        }

        return null;
    }

    private static async Task<string?> FetchWikiShipDataAsync(string shipName)
    {
        try
        {
            var wikiTitle = shipName.Replace(" ", "_");
            var escapedTitle = Uri.EscapeDataString(wikiTitle);

            var introUrl = $"https://starcitizen.tools/api.php?action=parse&page={escapedTitle}&prop=text&section=0&format=json";
            var introResp = await Http.GetAsync(introUrl);
            if (!introResp.IsSuccessStatusCode) return null;
            var introJson = await introResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(introJson);

            if (!doc.RootElement.TryGetProperty("parse", out var parse)) return null;
            var introHtml = parse.GetProperty("text").GetProperty("*").GetString() ?? "";
            var introText = StripHtml(introHtml);

            var specText = "";
            var sectionsUrl = $"https://starcitizen.tools/api.php?action=parse&page={escapedTitle}&prop=sections&format=json";
            var sectionsResp = await Http.GetAsync(sectionsUrl);
            if (sectionsResp.IsSuccessStatusCode)
            {
                var sectionsJson = await sectionsResp.Content.ReadAsStringAsync();
                using var sectionsDoc = JsonDocument.Parse(sectionsJson);
                var specSectionNumbers = new List<string>();
                if (sectionsDoc.RootElement.TryGetProperty("parse", out var secParse) &&
                    secParse.TryGetProperty("sections", out var sections))
                {
                    foreach (var sec in sections.EnumerateArray())
                    {
                        var title = sec.TryGetProperty("line", out var l) ? l.GetString() ?? "" : "";
                        if (title.Contains("Specifications", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Hardpoints", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Equipment", StringComparison.OrdinalIgnoreCase) ||
                            title.Contains("Weapons", StringComparison.OrdinalIgnoreCase))
                        {
                            var idx = sec.TryGetProperty("index", out var i) ? i.ToString() : "";
                            if (!string.IsNullOrEmpty(idx))
                                specSectionNumbers.Add(idx);
                        }
                    }
                }

                if (specSectionNumbers.Count > 0)
                {
                    var specParts = new StringBuilder();
                    foreach (var secNum in specSectionNumbers)
                    {
                        var secUrl = $"https://starcitizen.tools/api.php?action=parse&page={escapedTitle}&prop=text&section={secNum}&format=json";
                        var secResp = await Http.GetAsync(secUrl);
                        if (!secResp.IsSuccessStatusCode) continue;
                        var secJson = await secResp.Content.ReadAsStringAsync();
                        using var secDoc = JsonDocument.Parse(secJson);
                        if (secDoc.RootElement.TryGetProperty("parse", out var sp))
                        {
                            var html = sp.GetProperty("text").GetProperty("*").GetString() ?? "";
                            var text = StripHtml(html);
                            if (text.Length > 30)
                                specParts.AppendLine(text);
                        }
                    }
                    specText = specParts.ToString();
                }
                else
                {
                    var fullUrl = $"https://starcitizen.tools/api.php?action=parse&page={escapedTitle}&prop=text&format=json";
                    var fullResp = await Http.GetAsync(fullUrl);
                    if (fullResp.IsSuccessStatusCode)
                    {
                        var fullJson = await fullResp.Content.ReadAsStringAsync();
                        using var fullDoc = JsonDocument.Parse(fullJson);
                        if (fullDoc.RootElement.TryGetProperty("parse", out var fp))
                        {
                            var html = fp.GetProperty("text").GetProperty("*").GetString() ?? "";
                            specText = StripHtml(html);
                        }
                    }
                }
            }

            var sb = new StringBuilder($"=== starcitizen.tools Wiki: {shipName} ===\n");

            if (introText.Length > 100)
                sb.AppendLine($"概要: {introText[..Math.Min(1500, introText.Length)]}");

            if (specText.Length > 50)
                sb.AppendLine($"\nスペック・ハードポイント:\n{specText[..Math.Min(4000, specText.Length)]}");

            return sb.Length > 50 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StripHtml(string html)
    {
        var noStyle = System.Text.RegularExpressions.Regex.Replace(html, @"<style[^>]*>.*?</style>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var noTags = System.Text.RegularExpressions.Regex.Replace(noStyle, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(noTags).Trim();
    }

    private static async Task<string?> FetchUexVehiclesAsync(string query)
    {
        try
        {
            var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var shipName = ExtractShipName(query);
            var lowerQuery = query.ToLowerInvariant();
            var queryWords = lowerQuery.Split(' ', '　', '?', '？', 'の', 'を', 'は', 'で', 'が', 'に')
                .Where(w => w.Trim().Length >= 2).Select(w => w.Trim()).ToArray();

            var matched = new List<string>();
            var unmatched = new List<string>();

            foreach (var v in data.EnumerateArray())
            {
                var name = v.GetProperty("name").GetString() ?? "";
                var manufacturer = v.TryGetProperty("manufacturer_name", out var mfr) ? mfr.GetString() ?? "" : "";
                var focus = v.TryGetProperty("focus", out var f) ? f.GetString() ?? "" : "";
                var crew = v.TryGetProperty("crew", out var c) ? c.ToString() : "";
                var cargo = v.TryGetProperty("scu", out var cg) ? cg.ToString() : "";
                var price = v.TryGetProperty("price", out var p) ? p.ToString() : "";
                var size = v.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";

                var line = $"- {manufacturer} {name} | 役割: {focus} | 乗員: {crew} | カーゴ: {cargo} SCU | サイズ: {size} | 価格: {price} aUEC";

                bool isMatch = false;
                if (!string.IsNullOrEmpty(shipName))
                    isMatch = name.Contains(shipName, StringComparison.OrdinalIgnoreCase);
                else
                {
                    var fullEntry = $"{manufacturer} {name} {focus}".ToLowerInvariant();
                    foreach (var w in queryWords)
                    {
                        if (fullEntry.Contains(w))
                        {
                            isMatch = true;
                            break;
                        }
                    }
                }

                if (isMatch)
                    matched.Add(line);
                else
                    unmatched.Add(line);
            }

            var sb = new StringBuilder("=== UEX 機体データ ===\n");
            foreach (var line in matched)
                sb.AppendLine(line);
            if (matched.Count == 0)
            {
                foreach (var line in unmatched.Take(20))
                    sb.AppendLine(line);
            }
            int count = matched.Count > 0 ? matched.Count : Math.Min(unmatched.Count, 20);
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[UEX vehicles取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchUexCommoditiesAsync(string query)
    {
        try
        {
            var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/commodities");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var sb = new StringBuilder("=== UEX 商品・資源データ ===\n");
            int count = 0;
            foreach (var c in data.EnumerateArray())
            {
                var name = c.GetProperty("name").GetString() ?? "";
                var kind = c.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
                var buyPrice = c.TryGetProperty("price_buy", out var pb) ? pb.ToString() : "-";
                var sellPrice = c.TryGetProperty("price_sell", out var ps) ? ps.ToString() : "-";
                sb.AppendLine($"- {name} ({kind}) | 買値: {buyPrice} | 売値: {sellPrice} aUEC");
                count++;
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[UEX commodities取得エラー: {ex.Message}]";
        }
    }

    private static readonly Dictionary<string, string> CommodityNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        {"チタン", "titanium"}, {"チタニウム", "titanium"}, {"金", "gold"}, {"ダイヤモンド", "diamond"},
        {"銅", "copper"}, {"鉄", "iron"}, {"アルミニウム", "aluminum"}, {"タングステン", "tungsten"},
        {"クオンタニウム", "quantanium"}, {"ラナイト", "laranite"}, {"アグリシウム", "agricium"},
        {"コランダム", "corundum"}, {"ベリル", "beryl"}, {"水素", "hydrogen"}, {"医療", "medical"},
        {"スクラップ", "scrap"}, {"廃棄物", "waste"}, {"食料", "food"}, {"蒸留酒", "distilled"},
        {"アルミ", "aluminum"}, {"ダイヤ", "diamond"}, {"クオンタ", "quantanium"},
        {"ラナ", "laranite"}, {"アグリ", "agricium"}, {"タングス", "tungsten"},
        {"ベリリウム", "beryl"}, {"スクラップメタル", "scrap"}, {"ウィドウ", "widow"},
        {"スラム", "slam"}, {"ネオン", "neon"}, {"アスタロ", "astro"}, {"フルオリン", "fluorine"},
        {"クオーツ", "quartz"}, {"黒曜石", "obsidian"}, {"ハダナイト", "hadanite"},
        {"アパタイト", "aphorite"}, {"ドリヴァイン", "dolivine"}, {"タラナイト", "taranite"},
        {"リサイクル", "recycl"}, {"alum", "aluminum"},
    };

    private static string ExtractCommodityKeyword(string query)
    {
        foreach (var (ja, en) in CommodityNameMap)
        {
            if (query.Contains(ja, StringComparison.OrdinalIgnoreCase))
                return en;
        }
        var words = query.Split(' ', '　', 'で', 'の', 'を', 'は', 'が', 'に', 'と');
        foreach (var w in words)
        {
            var trimmed = w.Trim();
            if (trimmed.Length >= 3 && trimmed.All(c => c < 128))
                return trimmed;
        }
        return "";
    }

    private static async Task<string?> FetchUexCommodityPricesAsync(string query)
    {
        try
        {
            var keyword = ExtractCommodityKeyword(query);
            if (string.IsNullOrEmpty(keyword)) return null;

            var commResp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/commodities");
            using var commDoc = JsonDocument.Parse(commResp);
            if (!commDoc.RootElement.TryGetProperty("data", out var commData)) return null;

            int? commodityId = null;
            string commodityName = "";
            foreach (var c in commData.EnumerateArray())
            {
                var name = c.GetProperty("name").GetString() ?? "";
                if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    commodityId = c.GetProperty("id").GetInt32();
                    commodityName = name;
                    break;
                }
            }

            if (commodityId == null) return $"[UEX: '{keyword}' に該当する商品が見つかりませんでした]";

            var priceResp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/commodities_prices?id_commodity={commodityId}");
            using var priceDoc = JsonDocument.Parse(priceResp);
            if (!priceDoc.RootElement.TryGetProperty("data", out var priceData)) return null;

            string? systemFilter = null;
            var systemNames = new[] { "stanton", "pyro", "nyx", "terra", "sol" };
            var lowerQ = query.ToLowerInvariant();
            foreach (var sys in systemNames)
            {
                if (lowerQ.Contains(sys))
                {
                    systemFilter = sys;
                    break;
                }
            }

            var buyLocs = new List<(string loc, double price)>();
            var sellLocs = new List<(string loc, double price)>();

            foreach (var item in priceData.EnumerateArray())
            {
                var terminal = item.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
                var city = item.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
                var planet = item.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
                var star = item.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
                var priceBuy = item.TryGetProperty("price_buy", out var pb) && pb.ValueKind == JsonValueKind.Number ? pb.GetDouble() : 0;
                var priceSell = item.TryGetProperty("price_sell", out var ps) && ps.ValueKind == JsonValueKind.Number ? ps.GetDouble() : 0;

                if (systemFilter != null && !star.Contains(systemFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(s => !string.IsNullOrEmpty(s)));
                if (priceSell > 0) buyLocs.Add((location, priceSell));
                if (priceBuy > 0) sellLocs.Add((location, priceBuy));
            }

            var sb = new StringBuilder($"=== UEX 場所別価格: {commodityName}{(systemFilter != null ? $" ({systemFilter}星系のみ)" : "")} ===\n");
            if (buyLocs.Count > 0)
            {
                sb.AppendLine("\n【購入場所】(安い順 — プレイヤーが買える場所)");
                foreach (var (loc, price) in buyLocs.OrderBy(x => x.price))
                    sb.AppendLine($"- {loc} | {price:0} aUEC");
            }
            if (sellLocs.Count > 0)
            {
                sb.AppendLine("\n【売却場所】(高い順 — プレイヤーが売れる場所)");
                foreach (var (loc, price) in sellLocs.OrderByDescending(x => x.price))
                    sb.AppendLine($"- {loc} | {price:0} aUEC");
            }
            int count = buyLocs.Count + sellLocs.Count;
            return count > 0 ? sb.ToString() : $"[UEX: '{commodityName}' の場所別価格データは見つかりませんでした]";
        }
        catch (Exception ex)
        {
            return $"[UEX prices取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchUexTerminalsAsync(string query)
    {
        try
        {
            var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/star_systems");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var sb = new StringBuilder("=== UEX 星系データ ===\n");
            int count = 0;
            foreach (var s in data.EnumerateArray())
            {
                var name = s.GetProperty("name").GetString() ?? "";
                var status = s.TryGetProperty("is_available", out var av) ? (av.GetInt32() == 1 ? "利用可能" : "未実装") : "不明";
                sb.AppendLine($"- {name} ({status})");
                if (++count >= 20) break;
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[UEX terminals取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchUexItemsAsync(string query, int[] categoryIds)
    {
        try
        {
            var sb = new StringBuilder("=== UEX アイテムデータ（武器・コンポーネント） ===\n");
            int total = 0;
            foreach (var catId in categoryIds)
            {
                var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items?id_category={catId}");
                using var doc = JsonDocument.Parse(resp);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                foreach (var item in data.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    var category = item.TryGetProperty("category", out var cat) ? cat.GetString() ?? "" : "";
                    var section = item.TryGetProperty("section", out var sec) ? sec.GetString() ?? "" : "";
                    var company = item.TryGetProperty("company_name", out var co) ? co.GetString() ?? "" : "";
                    var size = item.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";
                    var grade = item.TryGetProperty("quality", out var q) ? q.ToString() : "";
                    sb.AppendLine($"- {name} | メーカー: {company} | カテゴリ: {section}/{category} | サイズ: {size} | グレード: {grade}");
                    total++;
                }
            }
            return total > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[UEX items取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchUexItemPricesAsync(string query, int[] categoryIds)
    {
        try
        {
            // まずアイテムリストを取得してクエリに関連するアイテムを見つける
            var matchedItems = new List<(int id, string name)>();
            foreach (var catId in categoryIds)
            {
                var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items?id_category={catId}");
                using var doc = JsonDocument.Parse(resp);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                foreach (var item in data.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    var id = item.GetProperty("id").GetInt32();
                    if (query.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains(ExtractItemKeyword(query), StringComparison.OrdinalIgnoreCase))
                        matchedItems.Add((id, name));
                }
            }

            if (matchedItems.Count == 0) return null;
            // 最大5アイテムまで価格取得
            var sb = new StringBuilder("=== UEX アイテム販売場所・価格 ===\n");
            int count = 0;
            foreach (var (itemId, itemName) in matchedItems.Take(5))
            {
                var priceResp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items_prices?id_item={itemId}");
                using var priceDoc = JsonDocument.Parse(priceResp);
                if (!priceDoc.RootElement.TryGetProperty("data", out var priceData)) continue;

                sb.AppendLine($"\n【{itemName}】");
                foreach (var p in priceData.EnumerateArray())
                {
                    var terminal = p.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
                    var city = p.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
                    var planet = p.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
                    var star = p.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
                    var buy = p.TryGetProperty("price_buy", out var pb) && pb.ValueKind == JsonValueKind.Number ? pb.GetDouble() : 0;

                    if (buy > 0)
                    {
                        var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(s => !string.IsNullOrEmpty(s)));
                        sb.AppendLine($"  - {location} | 価格: {buy:0.##} aUEC");
                        count++;
                    }
                }
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[UEX item prices取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchScTradeItemAsync(string keyword)
    {
        try
        {
            // SC Trade Tools: アイテム名で単品検索
            var encodedName = Uri.EscapeDataString(keyword);
            var resp = await Http.GetAsync($"https://sc-trade.tools/api/item/items/{encodedName}");
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(name))
                    return $"=== SC Trade Tools: {name} ===\nタイプ: {type}\n説明: {desc}";
            }

            // 単品で見つからない場合、ページネーション検索で部分一致
            var searchResp = await Http.GetStringAsync("https://sc-trade.tools/api/item/items?page=0&size=100");
            using var searchDoc = JsonDocument.Parse(searchResp);
            if (!searchDoc.RootElement.TryGetProperty("content", out var content)) return null;

            var sb = new StringBuilder("=== SC Trade Tools アイテム検索結果 ===\n");
            int count = 0;
            foreach (var item in content.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                var desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                sb.AppendLine($"- {name} | タイプ: {type} | 説明: {desc[..Math.Min(200, desc.Length)]}");
                if (++count >= 10) break;
            }

            // 1ページ目で見つからない場合、さらに数ページ検索
            if (count == 0)
            {
                for (int page = 1; page <= 5 && count == 0; page++)
                {
                    var pageResp = await Http.GetStringAsync($"https://sc-trade.tools/api/item/items?page={page}&size=100");
                    using var pageDoc = JsonDocument.Parse(pageResp);
                    if (!pageDoc.RootElement.TryGetProperty("content", out var pageContent)) break;
                    foreach (var item in pageContent.EnumerateArray())
                    {
                        var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (!name.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                        var desc = item.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                        sb.AppendLine($"- {name} | タイプ: {type} | 説明: {desc[..Math.Min(200, desc.Length)]}");
                        if (++count >= 10) break;
                    }
                }
            }

            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[SC Trade Tools取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchScTradeCommodityShopsAsync()
    {
        try
        {
            var resp = await Http.GetStringAsync("https://sc-trade.tools/api/commodity/shops");
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var sb = new StringBuilder("=== SC Trade Tools 商品取引ショップ一覧 ===\n");
            int count = 0;
            foreach (var shop in doc.RootElement.EnumerateArray())
            {
                var name = shop.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(name))
                {
                    sb.AppendLine($"- {name}");
                    if (++count >= 50) break;
                }
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[SC Trade Tools shops取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchScApiShipsAsync(string apiKey)
    {
        try
        {
            var resp = await Http.GetStringAsync($"https://starcitizen-api.com/{apiKey}/v1/cache/ships");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var sb = new StringBuilder("=== StarCitizen API 機体データ ===\n");
            int count = 0;
            foreach (var ship in data.EnumerateArray())
            {
                var name = ship.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var manufacturer = ship.TryGetProperty("manufacturer", out var m) ? m.GetString() ?? "" : "";
                var focus = ship.TryGetProperty("focus", out var f) ? f.GetString() ?? "" : "";
                var cargo = ship.TryGetProperty("cargocapacity", out var cg) ? cg.ToString() : "";
                var crew = ship.TryGetProperty("crew", out var cr) ? cr.ToString() : "";
                var size = ship.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";
                var price = ship.TryGetProperty("price", out var pr) ? pr.ToString() : "";
                sb.AppendLine($"- {manufacturer} {name} | 役割: {focus} | カーゴ: {cargo} SCU | 乗員: {crew} | サイズ: {size} | 価格: ${price}");
                count++;
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[SC API ships取得エラー: {ex.Message}]";
        }
    }

    private static async Task<string?> FetchScApiStarmapAsync(string apiKey)
    {
        try
        {
            var resp = await Http.GetStringAsync($"https://starcitizen-api.com/{apiKey}/v1/cache/starmap/systems");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var sb = new StringBuilder("=== StarCitizen API 星系データ ===\n");
            int count = 0;
            foreach (var sys in data.EnumerateArray())
            {
                var name = sys.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var type = sys.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                var affiliation = sys.TryGetProperty("affiliation", out var a) ? a.GetString() ?? "" : "";
                sb.AppendLine($"- {name} | タイプ: {type} | 所属: {affiliation}");
                if (++count >= 30) break;
            }
            return count > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            return $"[SC API starmap取得エラー: {ex.Message}]";
        }
    }

    public static async Task<string> SendChatAsync(BackendConfig backend, List<ChatMessage> history, string scData)
    {
        Log($"SendChatAsync: backend={backend.Type}, model={backend.Model}");
        return backend.Type.ToLowerInvariant() switch
        {
            "claude" => await SendClaudeChatAsync(backend, ChatSystemPrompt, history),
            "gemini" => await SendGeminiChatAsync(backend, ChatSystemPrompt, history),
            "ollama" => await SendOllamaChatAsync(backend, ChatSystemPrompt, history),
            _ => throw new ArgumentException($"Unknown backend: {backend.Type}")
        };
    }

    private static async Task<string> SendClaudeChatAsync(BackendConfig config, string system, List<ChatMessage> history)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var tools = GetToolDefinitions().Select(t => new Dictionary<string, object>
        {
            ["name"] = t["name"],
            ["description"] = t["description"],
            ["input_schema"] = t["input_schema"]
        }).ToList();

        var conversationMessages = new List<object>();
        foreach (var m in history)
            conversationMessages.Add(new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content });

        Log($"CLAUDE: model={config.Model}, messages={conversationMessages.Count}, tools={tools.Count}");
        for (int iteration = 0; iteration < 5; iteration++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["max_tokens"] = 4096,
                ["temperature"] = 0.7,
                ["system"] = system,
                ["messages"] = conversationMessages,
                ["tools"] = tools,
                ["tool_choice"] = new Dictionary<string, object> { ["type"] = iteration == 0 ? "any" : "auto" }
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            Log($"CLAUDE REQUEST (iter={iteration}): {json[..Math.Min(300, json.Length)]}...");
            var resp = await http.PostAsync("https://api.anthropic.com/v1/messages",
                new StringContent(json, Encoding.UTF8, "application/json"));

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                var code = (int)resp.StatusCode;
                if (code == 429 || code == 402 || code == 529 ||
                    err.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("credit", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
                {
                    return "⚠️ Claude API のクレジットが不足しているか、レート制限に達しました。Anthropic コンソール (console.anthropic.com) でプランと残高を確認してください。";
                }
                throw new HttpRequestException($"{code} - {err}");
            }

            var respJson = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;

            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() ?? "" : "";
            var contentArray = root.GetProperty("content");
            Log($"CLAUDE RESPONSE: stop_reason={stopReason}, content_blocks={contentArray.GetArrayLength()}");

            if (stopReason == "tool_use")
            {
                var assistantContentBlocks = new List<object>();
                var toolResults = new List<object>();

                foreach (var block in contentArray.EnumerateArray())
                {
                    var blockType = block.GetProperty("type").GetString() ?? "";
                    if (blockType == "text")
                    {
                        assistantContentBlocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "text",
                            ["text"] = block.GetProperty("text").GetString() ?? ""
                        });
                    }
                    else if (blockType == "tool_use")
                    {
                        var toolId = block.GetProperty("id").GetString() ?? "";
                        var toolName = block.GetProperty("name").GetString() ?? "";
                        var toolInput = block.GetProperty("input");

                        assistantContentBlocks.Add(new Dictionary<string, object>
                        {
                            ["type"] = "tool_use",
                            ["id"] = toolId,
                            ["name"] = toolName,
                            ["input"] = JsonSerializer.Deserialize<object>(toolInput.GetRawText())!
                        });

                        var toolResult = await ExecuteToolAsync(toolName, toolInput);
                        toolResults.Add(new Dictionary<string, object>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = toolId,
                            ["content"] = toolResult
                        });
                    }
                }

                conversationMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = assistantContentBlocks
                });
                conversationMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["content"] = toolResults
                });

                continue;
            }

            foreach (var block in contentArray.EnumerateArray())
            {
                if (block.GetProperty("type").GetString() == "text")
                    return block.GetProperty("text").GetString() ?? "";
            }

            return "";
        }

        return "[ツール呼び出し回数の上限に達しました]";
    }

    private static async Task<string> SendGeminiChatAsync(BackendConfig config, string system, List<ChatMessage> history)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.Model}:generateContent?key={config.ApiKey}";

        var functionDeclarations = GetGeminiFunctionDeclarations();

        var conversationContents = new List<object>();
        foreach (var m in history)
        {
            conversationContents.Add(new Dictionary<string, object>
            {
                ["role"] = m.Role == "assistant" ? "model" : "user",
                ["parts"] = new[] { new Dictionary<string, object> { ["text"] = m.Content } }
            });
        }

        Log($"GEMINI: model={config.Model}, messages={conversationContents.Count}");
        for (int iteration = 0; iteration < 5; iteration++)
        {
            var body = new Dictionary<string, object>
            {
                ["systemInstruction"] = new Dictionary<string, object>
                {
                    ["parts"] = new[] { new Dictionary<string, object> { ["text"] = system } }
                },
                ["contents"] = conversationContents,
                ["generationConfig"] = new Dictionary<string, object>
                {
                    ["temperature"] = 0.7,
                    ["maxOutputTokens"] = 4096
                },
                ["tools"] = new[] { new Dictionary<string, object> { ["function_declarations"] = functionDeclarations } },
                ["toolConfig"] = new Dictionary<string, object>
                {
                    ["functionCallingConfig"] = new Dictionary<string, object> { ["mode"] = iteration == 0 ? "ANY" : "AUTO" }
                }
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            Log($"GEMINI REQUEST (iter={iteration})");
            var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                var code = (int)resp.StatusCode;
                if (code == 429 || code == 403 ||
                    err.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("billing", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                {
                    return "⚠️ Gemini API のクレジットまたはクォータが不足しています。Google AI Studio (aistudio.google.com) でプランと使用量を確認してください。";
                }
                throw new HttpRequestException($"{code} - {err}");
            }

            var respJson = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respJson);
            var candidate = doc.RootElement.GetProperty("candidates")[0];
            var parts = candidate.GetProperty("content").GetProperty("parts");

            var hasFunctionCall = false;
            var modelParts = new List<object>();
            var functionResponseParts = new List<object>();

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("functionCall", out var fc))
                {
                    hasFunctionCall = true;
                    var funcName = fc.GetProperty("name").GetString() ?? "";
                    var funcArgs = fc.GetProperty("args");

                    modelParts.Add(new Dictionary<string, object>
                    {
                        ["functionCall"] = new Dictionary<string, object>
                        {
                            ["name"] = funcName,
                            ["args"] = JsonSerializer.Deserialize<object>(funcArgs.GetRawText())!
                        }
                    });

                    var result = await ExecuteToolAsync(funcName, funcArgs);
                    functionResponseParts.Add(new Dictionary<string, object>
                    {
                        ["functionResponse"] = new Dictionary<string, object>
                        {
                            ["name"] = funcName,
                            ["response"] = new Dictionary<string, object>
                            {
                                ["content"] = result
                            }
                        }
                    });
                }
                else if (part.TryGetProperty("text", out var textProp))
                {
                    modelParts.Add(new Dictionary<string, object> { ["text"] = textProp.GetString() ?? "" });
                }
            }

            if (hasFunctionCall)
            {
                conversationContents.Add(new Dictionary<string, object>
                {
                    ["role"] = "model",
                    ["parts"] = modelParts
                });
                conversationContents.Add(new Dictionary<string, object>
                {
                    ["role"] = "user",
                    ["parts"] = functionResponseParts
                });
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var textProp))
                    return textProp.GetString() ?? "";
            }

            return "";
        }

        return "[ツール呼び出し回数の上限に達しました]";
    }

    private static async Task<string> SendOllamaChatAsync(BackendConfig config, string system, List<ChatMessage> history)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var url = $"{config.BaseUrl.TrimEnd('/')}/api/chat";

        var ollamaTools = GetToolDefinitions().Select(t => new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = t["name"],
                ["description"] = t["description"],
                ["parameters"] = t["input_schema"]
            }
        }).ToList();

        var conversationMessages = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "system", ["content"] = system }
        };
        foreach (var m in history)
            conversationMessages.Add(new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content });

        Log($"OLLAMA: model={config.Model}, url={url}, messages={conversationMessages.Count}");

        for (int iteration = 0; iteration < 5; iteration++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["messages"] = conversationMessages,
                ["stream"] = false,
                ["options"] = new Dictionary<string, object> { ["temperature"] = 0.7, ["num_predict"] = 4096 },
                ["tools"] = ollamaTools
            };

            var json = JsonSerializer.Serialize(body, JsonOpts);
            Log($"OLLAMA REQUEST (iter={iteration})");
            var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                throw new HttpRequestException($"{(int)resp.StatusCode} - URL: {url} - {err}");
            }

            var respJson = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respJson);
            var message = doc.RootElement.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
            {
                Log($"OLLAMA: tool_calls detected, count={toolCalls.GetArrayLength()}");
                conversationMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = content,
                    ["tool_calls"] = JsonSerializer.Deserialize<object>(toolCalls.GetRawText())!
                });

                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var func = tc.GetProperty("function");
                    var funcName = func.GetProperty("name").GetString() ?? "";
                    var funcArgs = func.GetProperty("arguments");

                    var result = await ExecuteToolAsync(funcName, funcArgs);
                    conversationMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "tool",
                        ["content"] = result
                    });
                }
                continue;
            }

            Log($"OLLAMA: final response, content_len={content.Length}");
            return content;
        }

        return "[ツール呼び出し回数の上限に達しました]";
    }
}
