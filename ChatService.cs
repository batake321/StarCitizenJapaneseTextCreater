using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

public class ChatMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
}

public record StationFacility(
    string Name, string System, string Location, string Type,
    bool Refinery, bool CargoElevator, bool RepairResupply,
    bool Medical, bool ShipPurchase, bool Asop, string Notes)
{
    private static List<StationFacility>? _cache;

    public static List<StationFacility> LoadAll()
    {
        if (_cache != null) return _cache;
        _cache = new List<StationFacility>();
        var csvPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sc48_stations_facilities.csv");
        if (!File.Exists(csvPath)) return _cache;
        foreach (var line in File.ReadLines(csvPath).Skip(1))
        {
            var cols = line.Split(',');
            if (cols.Length < 11) continue;
            _cache.Add(new StationFacility(
                cols[0].Trim(), cols[1].Trim(), cols[2].Trim(), cols[3].Trim(),
                cols[4].Trim() == "●", cols[5].Trim() == "●", cols[6].Trim() == "●",
                cols[7].Trim() == "●", cols[8].Trim() == "●", cols[9].Trim() == "●",
                cols[10].Trim()));
        }
        return _cache;
    }

    public static void ClearCache() => _cache = null;

    public string ToSummary()
    {
        var parts = new List<string>();
        if (Refinery) parts.Add("精錬所");
        if (CargoElevator) parts.Add("カーゴ昇降機");
        if (RepairResupply) parts.Add("修理/補給");
        if (Medical) parts.Add("医療");
        if (ShipPurchase) parts.Add("艦船購入");
        if (Asop) parts.Add("ASOP");
        var sb = new StringBuilder();
        sb.AppendLine($"  タイプ: {Type}");
        sb.AppendLine($"  位置: {Location}");
        sb.AppendLine($"  施設: {string.Join(", ", parts)}");
        if (!string.IsNullOrEmpty(Notes)) sb.AppendLine($"  備考: {Notes}");
        return sb.ToString();
    }
}

public class ChatService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private static readonly List<string> _debugLog = new();
    public static IReadOnlyList<string> DebugLog => _debugLog;
    public static void ClearDebugLog() => _debugLog.Clear();
    public static string? LogDirectory { get; set; }
    public static event Action<string>? OnLog;

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _debugLog.Add(line);
        OnLog?.Invoke(line);
        try
        {
            var logDir = LogDirectory ?? _gameDataExtractor?.ToolsDir ?? System.IO.Path.GetTempPath();
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, "chat_debug.log"), line + "\n");
        }
        catch { }
    }

    private static readonly string ChatSystemPrompt =
        "あなたはゲーム「Star Citizen」の情報アシスタントです。プレイヤーからの質問に日本語で回答してください。\n\n" +
        "【重要】以下のスキルを使って最新のゲームデータを取得できます。質問に答えるために必要なデータは必ずスキルを使って取得してください。\n\n" +
        "【最重要ルール】アイテム名・船名・武器名・商品名が含まれる質問には、まず lookup を呼んでください。\n" +
        "lookup はゲームデータベースからアイテムの種類を自動判別し、適切なデータ（価格・詳細・購入場所等）をまとめて返します。\n" +
        "lookup が返すデータだけで十分な場合は、他のスキルを呼ぶ必要はありません。\n\n" +
        "【検索のコツ】\n" +
        "- データベースの名称は英語です。日本語で見つからない場合は英語に翻訳して再検索してください\n" +
        "  例: 短機関銃→SMG、重機関銃→LMG、戦闘機→Fighter、シールド→Shield Generator、ピストル→Pistol、精錬所→Refinery\n" +
        "- カタカナの地名・固有名詞は英語の綴りに変換して検索してください\n" +
        "  例: オリソン→Orison、ハーストン→Hurston、マイクロテック→microTech、エリアエイティーン→Area18、スタントン→Stanton\n" +
        "  ロービル/ロウビル→Lorville、レブスキ→Levski、ニューバベッジ→New Babbage、グリムヘックス→GrimHEX、ポートオリサー→Port Olisar\n" +
        "- 見つからない場合は発音が似ている英単語で検索を試みてください\n" +
        "- ハイフンやスペースの有無で結果が変わることがあります（P8-SC, P8SC 等）\n\n" +
        "【スキル一覧】\n" +
        "- lookup: 統合検索（最初に呼ぶ）。アイテム名からDB検索し、種類を自動判別して価格・詳細を返す\n" +
        "- search_mission: ミッション・契約の検索\n" +
        "- search_commodity: 商品・資源の購入/売却場所と価格\n" +
        "- search_keybind: キーバインド検索\n" +
        "- search_location: ステーション・都市・拠点の施設検索。精錬所・カーゴ昇降機・修理/補給・医療・艦船購入等の有無を返す。スターシステム名(Stanton,Pyro,Nyx)で全ステーション一覧も可\n" +
        "- search_wiki: Wiki から詳細情報取得\n\n" +
        "【候補提示ルール】\n" +
        "- 検索結果が複数ある場合は、番号付きリストで候補を提示してください\n" +
        "- ユーザーが番号で選択したら、その候補の詳細を取得してください\n\n" +
        "【価格データのルール】\n" +
        "- スキルから取得した価格は正確な数値です。絶対に独自の数値を作らず、スキルが返した数値をそのまま使ってください\n" +
        "- 「購入場所」はプレイヤーが商品を買える場所、「売却場所」はプレイヤーが商品を売れる場所です\n" +
        "- 前の質問の続きでも、必ずスキルを再呼出しして最新データを取得してください\n\n" +
        "【記憶機能】\n" +
        "- ユーザーが「覚えて」「記憶して」と言ったら remember ツールで情報を保存してください\n" +
        "- ユーザーが「直った」「忘れて」「もう不要」と言ったら forget ツールで該当する記憶を削除してください\n" +
        "- ユーザーが「それは間違い」「訂正」「違う」と言ったら、直前に保存した記憶や回答中の誤情報について forget で削除してから、ユーザーに正しい情報を聞いて remember で保存し直してください\n" +
        "- 記憶データに誤りがあるとユーザーが指摘した場合、まず forget で誤った記憶を削除し、正しい情報を確認してから remember で保存してください\n" +
        "- バグ情報、用語の対応表、Tips、攻略情報などを記憶できます\n" +
        "- remember の category は bug/term/tip/general から適切なものを選んでください\n\n" +
        "【音声読み上げ】\n" +
        "回答の末尾に <tts>...</tts> タグで、1〜3文の読み上げ用要約を必ず含めてください。\n" +
        "リストや番号付き項目は含めず、要点だけを自然な話し言葉で書いてください。\n" +
        "例: <tts>セラフィムステーションには、FPS装備、船舶装備、飲食店など16件のショップや施設があります。</tts>\n\n" +
        "回答は簡潔で分かりやすい日本語でお願いします。\n" +
        "憶測で回答しないでください。データに記載がなく、確信が持てない場合は「わかりません」と正直に答えてください。";

    // UEX カテゴリID: 武器・コンポーネント系
    private static readonly Dictionary<string, int[]> ItemCategoryMap = new(StringComparer.OrdinalIgnoreCase)
    {
        {"weapon",  new[] { 18, 17, 32, 33, 34, 35, 70, 79 }}, // FPS + Vehicle weapons
        {"武器",    new[] { 18, 17, 32, 33, 34, 35, 70, 79 }},
        {"fps",     new[] { 18, 17 }}, // Personal Weapons + Attachments
        {"FPS武器", new[] { 18, 17 }},
        {"gun",     new[] { 18, 32 }}, // FPS + Vehicle guns
        {"銃",      new[] { 18 }},
        {"pistol",  new[] { 18 }},
        {"smg",     new[] { 18 }},
        {"rifle",   new[] { 18 }},
        {"shotgun", new[] { 18 }},
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
        {"armor",   new[] { 1, 2, 3, 4, 5, 7 }}, // Armor pieces
        {"アーマー", new[] { 1, 2, 3, 4, 5, 7 }},
        {"undersuit", new[] { 24 }},
        {"アンダースーツ", new[] { 24 }},
        {"component", new[] { 19, 21, 22, 23 }},
        {"コンポーネント", new[] { 19, 21, 22, 23 }},
    };

    private static GameDataExtractor? _gameDataExtractor;
    private static GameDataQueryService? _queryService;
    public static GameDataQueryService? QueryService => _queryService;
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

    private static ActionMapData? _keybindData;
    public static void SetKeybindData(ActionMapData data) => _keybindData = data;

    private static TradeService? _tradeService;
    public static void SetTradeService(TradeService service) => _tradeService = service;

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
                ["name"] = "lookup",
                ["description"] = "統合検索。船・武器・装備・アイテムの名前から種類を自動判別し、詳細情報・購入場所・価格をまとめて返す。アイテム名が含まれる質問には最初にこれを呼ぶこと。",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "アイテム名・船名・武器名（部分一致可、日本語・英語どちらもOK）" }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            new()
            {
                ["name"] = "search_commodity",
                ["description"] = "商品・資源の購入/売却場所と価格を検索（コモディティ交易向け）",
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
                ["name"] = "search_trade_routes",
                ["description"] = "キャッシュ済み価格データから最適交易ルートを検索。予算・積載量・星系で絞り込み。在庫・コンテナサイズ付き。search_commodityより高速",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["budget"] = new Dictionary<string, object> { ["type"] = "number", ["description"] = "予算 (aUEC)。省略で500000" },
                        ["cargo_scu"] = new Dictionary<string, object> { ["type"] = "integer", ["description"] = "積載量 (SCU)。省略で100" },
                        ["buy_system"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "購入星系 (Stanton/Pyro/Nyx)。省略で全星系" },
                        ["sell_system"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "売却星系。省略で全星系" },
                        ["commodity"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "コモディティ名で絞り込み（省略で全商品）" },
                    },
                    ["required"] = Array.Empty<string>()
                }
            },
            new()
            {
                ["name"] = "search_mission",
                ["description"] = "ミッション・契約を検索。queryには英語キーワードを使用（例: salvage, bounty, rescue, delivery）",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "英語のミッション名やキーワード" },
                        ["system"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "星系名" }
                    },
                    ["required"] = new[] { "query" }
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
                ["name"] = "search_keybind",
                ["description"] = "キーバインド検索。機能名・カテゴリ名・キー名で検索。mode: 'search'=キーワード検索, 'unbound'=未割当キー一覧, 'key'=特定キーの割り当て確認",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "検索キーワード（機能名、カテゴリ名、キー名など）" },
                        ["mode"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "検索モード: search(デフォルト), unbound(未割当キー), key(キーの割り当て)" }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            new()
            {
                ["name"] = "search_location",
                ["description"] = "ステーション・都市・拠点を名前またはスターシステム名で検索し、施設一覧(精錬所・カーゴ昇降機・修理/補給・医療・艦船購入・ASOP)を返す。例: Seraphim Station, CRU-L1, Stanton, Pyro, Nyx, Lorville, GrimHEX",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "ステーション名・都市名・拠点名・スターシステム名（部分一致可、英語）" }
                    },
                    ["required"] = new[] { "query" }
                }
            },
            new()
            {
                ["name"] = "remember",
                ["description"] = "ユーザーから「覚えて」「記憶して」と言われた情報を保存する。用語の対応表、バグ情報、Tips等。",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["content"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "記憶する内容" },
                        ["category"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "カテゴリ: term(用語), bug(バグ), tip(コツ), general(その他)" }
                    },
                    ["required"] = new[] { "content", "category" }
                }
            },
            new()
            {
                ["name"] = "forget",
                ["description"] = "ユーザーから「忘れて」「直った」「もう不要」と言われた記憶を削除する。",
                ["input_schema"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["query"] = new Dictionary<string, object> { ["type"] = "string", ["description"] = "削除する記憶の検索キーワード" }
                    },
                    ["required"] = new[] { "query" }
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"[Chat] スキル開始: {toolName}");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var resultTask = toolName switch
            {
                "lookup" => ExecuteLookupAsync(args),
                "search_ship" => ExecuteSearchShipAsync(args),
                "search_commodity" => ExecuteSearchCommodityAsync(args),
                "search_trade_routes" => Task.FromResult(ExecuteSearchTradeRoutes(args)),
                "search_item" => ExecuteSearchItemAsync(args),
                "search_mission" => ExecuteSearchMissionAsync(args),
                "search_price" => ExecuteSearchPriceAsync(args),
                "search_wiki" => ExecuteSearchWikiAsync(args),
                "search_pledge" => FetchPledgeInfoAsync(args),
                "search_keybind" => Task.FromResult(ExecuteSearchKeybind(args)),
                "search_location" => ExecuteSearchLocationAsync(args),
                "remember" => Task.FromResult(ExecuteRemember(args)),
                "forget" => Task.FromResult(ExecuteForget(args)),
                _ => Task.FromResult($"[不明なツール: {toolName}]")
            };
            var result = await resultTask.WaitAsync(cts.Token);
            Console.WriteLine($"[Chat] スキル完了: {toolName} ({sw.ElapsedMilliseconds}ms, {result.Length} chars)");
            Log($"SKILL RESULT ({toolName}): {result[..Math.Min(500, result.Length)]}{(result.Length > 500 ? "..." : "")}");
            return result;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[Chat] スキル TIMEOUT: {toolName} ({sw.ElapsedMilliseconds}ms)");
            Log($"SKILL TIMEOUT ({toolName}): 30秒タイムアウト");
            return $"[スキルタイムアウト ({toolName}): 30秒以内に応答がありませんでした]";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Chat] スキル ERROR: {toolName} - {ex.Message} ({sw.ElapsedMilliseconds}ms)");
            Log($"SKILL ERROR ({toolName}): {ex.Message}");
            return $"[スキル実行エラー ({toolName}): {ex.Message}]";
        }
    }

    private static readonly HashSet<string> ShipTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ship", "Vehicle", "Ground", "Gravlev"
    };
    private static readonly HashSet<string> WeaponPersonalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "WeaponPersonal", "WeaponAttachment", "Grenade"
    };
    private static readonly HashSet<string> ShipComponentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shield", "Cooler", "PowerPlant", "QuantumDrive", "WeaponGun", "Turret", "TurretBase",
        "MissileLauncher", "Missile", "WeaponMining", "Radar", "Avionics", "FlightController"
    };
    private static readonly HashSet<string> ArmorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Char_Armor_Helmet", "Char_Armor_Arms", "Char_Armor_Legs", "Char_Armor_Backpack",
        "Char_Armor_Core", "Char_Clothing_Undersuit", "Char_Clothing_Torso", "Char_Clothing_Legs",
        "Char_Clothing_Hat", "Char_Clothing_Hands", "Char_Clothing_Feet", "Light_Armor",
        "Medium_Armor", "Heavy_Armor"
    };

    private static string ClassifyItemType(string itemType, string subType)
    {
        if (ShipTypes.Contains(itemType) || ShipTypes.Contains(subType)) return "ship";
        if (WeaponPersonalTypes.Contains(itemType)) return "weapon";
        if (ShipComponentTypes.Contains(itemType)) return "component";
        if (ArmorTypes.Contains(itemType) || ArmorTypes.Contains(subType)
            || itemType.StartsWith("Char_Armor", StringComparison.OrdinalIgnoreCase)
            || itemType.StartsWith("Char_Clothing", StringComparison.OrdinalIgnoreCase)) return "armor";
        return "item";
    }

    private static async Task<string> ExecuteLookupAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query)) return "検索クエリが空です。";
        Log($"LOOKUP: query='{query}'");

        if (_queryService == null)
            return "⚠️ ゲームデータのインデックスが未構築です。設定タブの「インデックス構築」を実行してください。";

        var candidates = _queryService.FuzzySearch(query, 10);
        if (candidates.Count == 0)
        {
            var iniResult = SearchGlobalIniForItem(query);
            if (!string.IsNullOrEmpty(iniResult)) return iniResult;
            return $"'{query}' に該当するアイテムが見つかりませんでした。";
        }

        var best = candidates[0];
        var category = ClassifyItemType(best.itemType, best.subType);
        Log($"LOOKUP: found '{best.name}' type={best.itemType}/{best.subType} -> category={category}");

        var sb = new StringBuilder();

        if (candidates.Count > 1)
        {
            sb.AppendLine($"=== 検索結果: '{query}' ({candidates.Count}件) ===");
            foreach (var (uuid, rn, name, itemType, subType, mfr, _) in candidates.Take(10))
            {
                var cat = ClassifyItemType(itemType, subType);
                var catLabel = cat switch { "ship" => "船", "weapon" => "武器", "component" => "コンポーネント", "armor" => "アーマー/衣装", _ => "アイテム" };
                sb.Append($"- {(!string.IsNullOrEmpty(name) ? name : rn)} [{catLabel}]");
                if (!string.IsNullOrEmpty(mfr) && !mfr.StartsWith("file://")) sb.Append($" | {mfr}");
                sb.AppendLine();
            }
            sb.AppendLine($"\n最初の候補 '{best.name}' の詳細を表示します:");
        }

        switch (category)
        {
            case "ship":
                sb.AppendLine(await LookupShipAsync(query, best));
                break;
            case "weapon":
            case "armor":
            case "component":
            case "item":
                sb.AppendLine(await LookupItemAsync(query, best));
                break;
        }

        return sb.ToString();
    }

    private static async Task<string> LookupShipAsync(string query, (string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score) item)
    {
        var sb = new StringBuilder();

        if (_queryService != null)
        {
            try
            {
                var dcbResult = _queryService.SearchShips(query);
                if (!string.IsNullOrEmpty(dcbResult)) sb.AppendLine(dcbResult);
            }
            catch { }
        }

        try
        {
            var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles");
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
                foreach (var v in data.EnumerateArray())
                {
                    var name = v.GetProperty("name").GetString() ?? "";
                    if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(item.name, StringComparison.OrdinalIgnoreCase)) continue;
                    var mfr = v.TryGetProperty("manufacturer_name", out var m) ? m.GetString() ?? "" : "";
                    var focus = v.TryGetProperty("focus", out var f) ? f.GetString() ?? "" : "";
                    var crew = v.TryGetProperty("crew", out var c) ? c.ToString() : "";
                    var cargo = v.TryGetProperty("scu", out var cg) ? cg.ToString() : "";
                    var price = v.TryGetProperty("price", out var p) ? p.ToString() : "";
                    var size = v.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";
                    sb.AppendLine($"[UEX] {mfr} {name} | 役割: {focus} | 乗員: {crew} | カーゴ: {cargo} SCU | サイズ: {size} | 価格: {price} aUEC");
                }
            }
        }
        catch { }

        if (!string.IsNullOrEmpty(item.uuid))
        {
            try
            {
                var priceResult = await FetchCstonePricesAsync(item.uuid, item.name, null);
                if (!string.IsNullOrEmpty(priceResult) && !priceResult.Contains("価格情報が見つかりません"))
                    sb.AppendLine(priceResult);
            }
            catch { }
        }

        var pledgeArgs = JsonDocument.Parse($"{{\"ship_name\":\"{EscapeJsonString(query)}\"}}").RootElement;
        try
        {
            var pledgeResult = await FetchPledgeInfoAsync(pledgeArgs);
            if (!string.IsNullOrEmpty(pledgeResult) && !pledgeResult.Contains("見つかりませんでした"))
                sb.AppendLine(pledgeResult);
        }
        catch { }

        return sb.Length > 0 ? sb.ToString() : $"'{query}' の詳細情報が見つかりませんでした。";
    }

    private static async Task<string> LookupItemAsync(string query, (string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score) item)
    {
        var sb = new StringBuilder();
        var displayName = !string.IsNullOrEmpty(item.name) ? item.name : query;
        var category = ClassifyItemType(item.itemType, item.subType);
        var catLabel = category switch { "weapon" => "武器", "component" => "コンポーネント", "armor" => "アーマー/衣装", _ => "アイテム" };
        sb.AppendLine($"種別: {catLabel} | タイプ: {item.itemType}/{item.subType}");
        if (!string.IsNullOrEmpty(item.manufacturer) && !item.manufacturer.StartsWith("file://"))
            sb.AppendLine($"メーカー: {item.manufacturer}");

        if (_queryService?.HasData() == true)
        {
            try
            {
                var compType = GameDataExtractor.DetectComponentType(query);
                var dbResult = _queryService.SearchItems(query, compType);
                if (!string.IsNullOrEmpty(dbResult)) sb.AppendLine(dbResult);
            }
            catch { }
        }

        if (!string.IsNullOrEmpty(item.uuid))
        {
            try
            {
                var priceResult = await FetchCstonePricesAsync(item.uuid, displayName, null);
                if (!string.IsNullOrEmpty(priceResult) && !priceResult.Contains("価格情報が見つかりません"))
                {
                    sb.AppendLine("\n【ゲーム内購入場所 (aUEC)】");
                    sb.AppendLine(priceResult);
                }
            }
            catch { }
        }

        if (category is "weapon" or "armor" or "component")
        {
            var pledgeArgs = JsonDocument.Parse($"{{\"ship_name\":\"{EscapeJsonString(query)}\"}}").RootElement;
            try
            {
                var pledgeResult = await FetchPledgeInfoAsync(pledgeArgs);
                if (!string.IsNullOrEmpty(pledgeResult) && !pledgeResult.Contains("見つかりませんでした"))
                {
                    sb.AppendLine("\n【課金購入 (USD)】");
                    sb.AppendLine(pledgeResult);
                }
            }
            catch { }
        }

        return sb.Length > 0 ? sb.ToString() : $"'{query}' の詳細情報が見つかりませんでした。";
    }

    private static string EscapeJsonString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static async Task<string> ExecuteSearchShipAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var exact = args.TryGetProperty("exact", out var e) && e.GetBoolean();

        // 1. DCB (ローカルゲームデータ) を検索 — ハードポイント情報あり
        var dcbResult = "";
        if (_queryService != null)
        {
            try { dcbResult = _queryService.SearchShips(query); } catch { }
        }

        // 2. UEX API を検索
        var results = new List<string>();
        try
        {
            var resp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles");
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("data", out var data))
            {
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
            }
        }
        catch { }

        // 3. 結果をマージ
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(dcbResult))
            sb.AppendLine(dcbResult);

        if (results.Count > 0)
        {
            sb.AppendLine("=== UEX データ ===");
            if (results.Count > 20)
            {
                for (int i = 0; i < 20; i++) sb.AppendLine($"{i + 1}. {results[i]}");
                sb.AppendLine($"... 他{results.Count - 20}件");
            }
            else
                foreach (var r in results) sb.AppendLine(r);
        }

        if (sb.Length == 0)
            return $"'{query}' に該当する機体は見つかりませんでした。";

        return sb.ToString();
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

    private static string ExecuteSearchTradeRoutes(JsonElement args)
    {
        if (_tradeService == null || !_tradeService.HasPriceData)
            return "交易価格データが未取得です。コモディティタブで [価格更新] を実行するか、アプリ起動後しばらくお待ちください。";

        var budget = args.TryGetProperty("budget", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDouble() : 500000;
        var cargoScu = args.TryGetProperty("cargo_scu", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 100;
        var buySystem = args.TryGetProperty("buy_system", out var bs) ? bs.GetString() ?? "全て" : "全て";
        var sellSystem = args.TryGetProperty("sell_system", out var ss) ? ss.GetString() ?? "全て" : "全て";
        var commodity = args.TryGetProperty("commodity", out var com) ? com.GetString() : null;

        return _tradeService.FormatTradeRouteSummary(budget, cargoScu, buySystem, sellSystem, commodity, topN: 10);
    }

    private static async Task<string> ExecuteSearchItemAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var category = args.TryGetProperty("category", out var c) ? c.GetString() : null;

        var sb = new StringBuilder();

        // 1. item_index (FTS5 fuzzy / LIKE fallback)
        if (_queryService != null)
        {
            try
            {
                var indexResults = _queryService.FuzzySearch(query);
                if (indexResults.Count > 0)
                {
                    sb.AppendLine($"=== ゲームデータ (DB): アイテム検索 ({indexResults.Count}件) ===");
                    foreach (var (uuid, rn, name, itemType, subType, mfr, _) in indexResults)
                    {
                        sb.Append($"- {(!string.IsNullOrEmpty(name) ? name : rn)}");
                        if (!string.IsNullOrEmpty(itemType)) sb.Append($" | タイプ: {itemType}");
                        if (!string.IsNullOrEmpty(subType)) sb.Append($" | サブ: {subType}");
                        if (!string.IsNullOrEmpty(mfr)) sb.Append($" | メーカー: {mfr}");
                        sb.AppendLine();
                    }
                }
            }
            catch { }
        }

        // 2. DCB items table (detailed component data)
        if (_queryService?.HasData() == true)
        {
            try
            {
                var compType = GameDataExtractor.DetectComponentType(query);
                if (compType == null && !string.IsNullOrEmpty(category))
                    compType = GameDataExtractor.DetectComponentType(category);
                var dbResult = _queryService.SearchItems(query, compType);
                if (!string.IsNullOrEmpty(dbResult)) sb.AppendLine(dbResult);
            }
            catch { }
        }

        // 2. UEX API
        int[] categoryIds;
        if (!string.IsNullOrEmpty(category) && ItemCategoryMap.TryGetValue(category, out var ids))
            categoryIds = ids;
        else
            categoryIds = new[] { 18, 17, 32, 33, 34, 35, 19, 21, 22, 23, 70, 79 };

        int total = 0;
        foreach (var catId in categoryIds)
        {
            try
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
            }
            catch { }
            if (total >= 30) break;
        }

        // 3. DCB direct query (StarBreaker) — if index not available
        if (sb.Length == 0 && _gameDataExtractor?.IsReady == true)
        {
            try
            {
                var dcbData = await _gameDataExtractor.QueryGameDataAsync(query);
                if (!string.IsNullOrEmpty(dcbData)) sb.AppendLine(dcbData);
            }
            catch { }
        }

        // 4. global.ini fallback
        if (sb.Length == 0)
        {
            var iniResult = SearchGlobalIniForItem(query);
            if (!string.IsNullOrEmpty(iniResult)) sb.AppendLine(iniResult);
        }

        if (sb.Length == 0)
        {
            var scResult = await FetchScTradeItemAsync(query);
            if (!string.IsNullOrEmpty(scResult)) return scResult;
            return $"'{query}' に該当するアイテムが見つかりませんでした。";
        }

        return sb.ToString();
    }

    private static string? SearchGlobalIniForItem(string query)
    {
        if (string.IsNullOrEmpty(_translationDbPath) || !File.Exists(_translationDbPath))
            return null;

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_translationDbPath};Mode=ReadOnly");
            conn.Open();

            // Search item names
            using var nameCmd = conn.CreateCommand();
            nameCmd.CommandText = @"SELECT key, english, japanese FROM translations
                WHERE key LIKE 'item_Name%' AND english LIKE $q
                ORDER BY key LIMIT 20";
            nameCmd.Parameters.AddWithValue("$q", $"%{query}%");

            var sb = new StringBuilder();
            int count = 0;
            using (var reader = nameCmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    var key = reader.GetString(0);
                    var en = reader.GetString(1);
                    var ja = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    sb.AppendLine($"- {en}" + (ja.Length > 0 ? $" ({ja})" : "") + $" [key: {key}]");
                    count++;
                }
            }

            if (count > 0)
            {
                // Also find descriptions for the first few matches
                var descSb = new StringBuilder();
                using var descCmd = conn.CreateCommand();
                descCmd.CommandText = @"SELECT key, english FROM translations
                    WHERE key LIKE 'item_Desc%' AND english LIKE $q
                    ORDER BY key LIMIT 5";
                descCmd.Parameters.AddWithValue("$q", $"%{query}%");
                using var descReader = descCmd.ExecuteReader();
                while (descReader.Read())
                {
                    var descEn = descReader.GetString(1);
                    if (descEn.Length > 500) descEn = descEn[..500] + "...";
                    descSb.AppendLine($"\n説明: {descEn}");
                }

                return $"=== ゲームデータ: {query} ===\nアイテム名 ({count}件):\n{sb}{descSb}";
            }

            // Broader search if no item_Name match
            using var broadCmd = conn.CreateCommand();
            broadCmd.CommandText = @"SELECT key, english FROM translations
                WHERE (key LIKE 'item_Name%' OR key LIKE 'item_Desc%')
                AND english LIKE $q
                ORDER BY key LIMIT 15";
            broadCmd.Parameters.AddWithValue("$q", $"%{query}%");

            using var broadReader = broadCmd.ExecuteReader();
            while (broadReader.Read())
            {
                var key = broadReader.GetString(0);
                var en = broadReader.GetString(1);
                if (en.Length > 300) en = en[..300] + "...";
                sb.AppendLine($"- [{key}] {en}");
                count++;
            }

            if (count > 0)
                return $"=== ゲームデータ: {query} ===\n{sb}";
        }
        catch { }
        return null;
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

        if (_queryService == null)
            return $"⚠️ ゲームデータのインデックスが未構築です。設定タブの「インデックス構築」を実行してください。";

        var uuid = _queryService.GetUuidByName(itemName);
        string displayName = itemName;

        if (string.IsNullOrEmpty(uuid))
        {
            var candidates = _queryService.FuzzySearch(itemName);
            if (candidates.Count == 0)
                return $"'{itemName}' に該当するアイテムが見つかりませんでした。インデックスを再構築すると改善される場合があります。";

            if (candidates.Count == 1)
            {
                uuid = candidates[0].uuid;
                displayName = candidates[0].name;
            }
            else
            {
                var sb = new StringBuilder();
                sb.AppendLine($"'{itemName}' の候補が {candidates.Count} 件見つかりました:");
                foreach (var (cUuid, _, cName, cType, cSub, cMfr, _) in candidates.Take(10))
                {
                    sb.Append($"- {cName}");
                    if (!string.IsNullOrEmpty(cType)) sb.Append($" ({cType})");
                    if (!string.IsNullOrEmpty(cMfr)) sb.Append($" | {cMfr}");
                    sb.AppendLine();
                }
                uuid = candidates[0].uuid;
                displayName = candidates[0].name;
                sb.AppendLine($"\n最初の候補 '{displayName}' の価格を検索します...");
                var priceResult = await FetchCstonePricesAsync(uuid, displayName, system);
                sb.Append(priceResult);
                return sb.ToString();
            }
        }
        else
        {
            var indexResults = _queryService.FuzzySearch(itemName, 1);
            if (indexResults.Count > 0) displayName = indexResults[0].name;
        }

        return await FetchCstonePricesAsync(uuid, displayName, system);
    }

    private static async Task<string> FetchCstonePricesAsync(string uuid, string displayName, string? systemFilter)
    {
        Log($"[search_price] cstone.space fetch: {uuid} ({displayName})");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var resp = await Http.GetAsync($"https://finder.cstone.space/search/{uuid}", cts.Token);
            if (!resp.IsSuccessStatusCode)
                return $"'{displayName}' の価格情報を取得できませんでした (HTTP {(int)resp.StatusCode})。";

            var html = await resp.Content.ReadAsStringAsync(cts.Token);
            return ParseCstoneHtml(html, displayName, systemFilter);
        }
        catch (OperationCanceledException)
        {
            return $"'{displayName}' の価格取得がタイムアウトしました。";
        }
        catch (Exception ex)
        {
            Log($"[search_price] Error: {ex.Message}");
            return $"'{displayName}' の価格情報の取得中にエラーが発生しました: {ex.Message}";
        }
    }

    private static string ParseCstoneHtml(string html, string displayName, string? systemFilter)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【{displayName}】");

        var buyLocations = ParseCstoneShopEntries(html, "Buy", systemFilter);
        var rentLocations = ParseCstoneRentEntries(html, systemFilter);

        if (buyLocations.Count > 0)
        {
            sb.AppendLine("購入場所:");
            foreach (var (location, price, date) in buyLocations)
            {
                sb.Append($"  - {location} | 価格: {price} aUEC");
                if (!string.IsNullOrEmpty(date)) sb.Append($" (確認日: {date})");
                sb.AppendLine();
            }
        }

        if (rentLocations.Count > 0)
        {
            sb.AppendLine("レンタル場所:");
            foreach (var (location, prices, date) in rentLocations)
            {
                sb.Append($"  - {location}");
                if (!string.IsNullOrEmpty(prices)) sb.Append($" | {prices}");
                if (!string.IsNullOrEmpty(date)) sb.Append($" (確認日: {date})");
                sb.AppendLine();
            }
        }

        if (buyLocations.Count == 0 && rentLocations.Count == 0)
            sb.AppendLine("販売場所が見つかりませんでした。");

        return sb.ToString();
    }

    private static List<(string location, string price, string date)> ParseCstoneShopEntries(string html, string type, string? systemFilter)
    {
        var results = new List<(string, string, string)>();
        var rows = System.Text.RegularExpressions.Regex.Matches(html,
            @"<tr[^>]*>.*?</tr>", System.Text.RegularExpressions.RegexOptions.Singleline);

        foreach (System.Text.RegularExpressions.Match row in rows)
        {
            var cells = System.Text.RegularExpressions.Regex.Matches(row.Value,
                @"<td[^>]*>(.*?)</td>", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (cells.Count < 2) continue;

            var locationCell = StripHtml(cells[0].Groups[1].Value).Trim();
            var priceCell = StripHtml(cells.Count > 1 ? cells[1].Groups[1].Value : "").Trim();
            var dateCell = cells.Count > 2 ? StripHtml(cells[2].Groups[1].Value).Trim() : "";

            if (string.IsNullOrEmpty(locationCell) || string.IsNullOrEmpty(priceCell)) continue;
            if (!double.TryParse(priceCell.Replace(",", "").Replace(" ", ""), out _) && !priceCell.Any(char.IsDigit)) continue;

            if (!string.IsNullOrEmpty(systemFilter) &&
                !locationCell.Contains(systemFilter, StringComparison.OrdinalIgnoreCase)) continue;

            results.Add((locationCell, priceCell, dateCell));
        }

        if (results.Count == 0)
        {
            var lines = html.Split('\n');
            string? currentLocation = null;
            string? currentPrice = null;
            string? currentDate = null;

            foreach (var rawLine in lines)
            {
                var line = StripHtml(rawLine).Trim();
                if (string.IsNullOrEmpty(line)) continue;

                if (line.Contains(" - ") && (line.Contains("Stanton") || line.Contains("Nyx") || line.Contains("Pyro")))
                {
                    if (currentLocation != null && currentPrice != null)
                    {
                        if (string.IsNullOrEmpty(systemFilter) ||
                            currentLocation.Contains(systemFilter, StringComparison.OrdinalIgnoreCase))
                            results.Add((currentLocation, currentPrice, currentDate ?? ""));
                    }
                    currentLocation = line;
                    currentPrice = null;
                    currentDate = null;
                }
                else if (currentLocation != null && currentPrice == null &&
                         line.Any(char.IsDigit) && !line.Contains("day", StringComparison.OrdinalIgnoreCase))
                {
                    var numMatch = System.Text.RegularExpressions.Regex.Match(line, @"[\d,]+");
                    if (numMatch.Success) currentPrice = numMatch.Value;
                }
                else if (currentLocation != null && line.Contains("29"))
                {
                    var dateMatch = System.Text.RegularExpressions.Regex.Match(line, @"\d{4}-\d{2}-\d{2}");
                    if (dateMatch.Success) currentDate = dateMatch.Value;
                }
            }

            if (currentLocation != null && currentPrice != null)
            {
                if (string.IsNullOrEmpty(systemFilter) ||
                    currentLocation.Contains(systemFilter, StringComparison.OrdinalIgnoreCase))
                    results.Add((currentLocation, currentPrice, currentDate ?? ""));
            }
        }

        return results;
    }

    private static List<(string location, string prices, string date)> ParseCstoneRentEntries(string html, string? systemFilter)
    {
        var results = new List<(string, string, string)>();
        var lines = html.Split('\n');
        string? currentLocation = null;
        var rentPrices = new List<string>();
        string? currentDate = null;
        bool inRentalSection = false;

        foreach (var rawLine in lines)
        {
            var line = StripHtml(rawLine).Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Contains("Rent", StringComparison.OrdinalIgnoreCase) && line.Contains("Location", StringComparison.OrdinalIgnoreCase))
            {
                inRentalSection = true;
                continue;
            }

            if (!inRentalSection) continue;

            if (line.Contains(" - ") && (line.Contains("Stanton") || line.Contains("Nyx") || line.Contains("Pyro")))
            {
                if (currentLocation != null && rentPrices.Count > 0)
                {
                    if (string.IsNullOrEmpty(systemFilter) ||
                        currentLocation.Contains(systemFilter, StringComparison.OrdinalIgnoreCase))
                        results.Add((currentLocation, string.Join(" | ", rentPrices), currentDate ?? ""));
                }
                currentLocation = line;
                rentPrices.Clear();
                currentDate = null;
            }
            else if (currentLocation != null && System.Text.RegularExpressions.Regex.IsMatch(line, @"\d+\s*[Dd]ay"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*[Dd]ay[s]?\s*[:\s]*([\d,]+)");
                if (match.Success)
                    rentPrices.Add($"{match.Groups[1].Value}日: {match.Groups[2].Value} aUEC");
            }
            else if (currentLocation != null && line.Contains("29"))
            {
                var dateMatch = System.Text.RegularExpressions.Regex.Match(line, @"\d{4}-\d{2}-\d{2}");
                if (dateMatch.Success) currentDate = dateMatch.Value;
            }
        }

        if (currentLocation != null && rentPrices.Count > 0)
        {
            if (string.IsNullOrEmpty(systemFilter) ||
                currentLocation.Contains(systemFilter, StringComparison.OrdinalIgnoreCase))
                results.Add((currentLocation, string.Join(" | ", rentPrices), currentDate ?? ""));
        }

        return results;
    }

    private static string StripHtml(string html)
    {
        var noStyle = System.Text.RegularExpressions.Regex.Replace(html, @"<style[^>]*>.*?</style>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        var noTags = System.Text.RegularExpressions.Regex.Replace(noStyle, @"<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(noTags).Trim();
    }

    private static async Task<string> ExecuteSearchWikiAsync(JsonElement args)
    {
        var pageTitle = args.TryGetProperty("page_title", out var p) ? p.GetString() ?? "" : "";
        var result = await FetchWikiShipDataAsync(pageTitle);
        return result ?? $"Wiki ページ '{pageTitle}' が見つからないか、データがありませんでした。";
    }

    private static string ExecuteSearchKeybind(JsonElement args)
    {
        if (_keybindData == null || _keybindData.Categories.Count == 0)
            return "[キーバインドデータが読み込まれていません。キーバインドエディタを一度開いてください。]";

        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var mode = args.TryGetProperty("mode", out var m) ? m.GetString() ?? "search" : "search";

        var allActions = _keybindData.Categories.SelectMany(c => c.Actions).ToList();
        var sb = new StringBuilder();

        switch (mode.ToLower())
        {
            case "unbound":
                sb.AppendLine("=== 未割当キー一覧 ===");
                var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in allActions)
                {
                    if (!string.IsNullOrEmpty(a.Keyboard)) knownKeys.Add(NormalizeKeyForChat(a.Keyboard));
                }
                string[] commonKeys = [
                    "1","2","3","4","5","6","7","8","9","0",
                    "q","w","e","r","t","y","u","i","o","p",
                    "a","s","d","f","g","h","j","k","l",
                    "z","x","c","v","b","n","m",
                    "f1","f2","f3","f4","f5","f6","f7","f8","f9","f10","f11","f12",
                    "space","tab","enter","backspace","escape",
                    "up","down","left","right",
                    "insert","delete","home","end","pgup","pgdn",
                    "np_0","np_1","np_2","np_3","np_4","np_5","np_6","np_7","np_8","np_9",
                    "np_add","np_subtract","np_multiply","np_divide","np_enter","np_period"
                ];
                var unboundKeys = commonKeys.Where(k => !knownKeys.Contains(k)).ToList();
                if (unboundKeys.Count == 0)
                    sb.AppendLine("全ての主要キーに割り当てがあります。");
                else
                {
                    sb.AppendLine($"未割当キー ({unboundKeys.Count}個):");
                    sb.AppendLine(string.Join(", ", unboundKeys.Select(k => InputDisplayHelper.FormatInput(k))));
                }
                // Also check modifier combos if query hints at it
                if (!string.IsNullOrEmpty(query))
                {
                    sb.AppendLine($"\n修飾キー '{query}' との組み合わせは別途お問い合わせください。");
                }
                break;

            case "key":
                sb.AppendLine($"=== キー '{query}' の割り当て ===");
                var keyBindings = allActions.Where(a =>
                    !string.IsNullOrEmpty(a.Keyboard) &&
                    KeyMatchesQuery(a.Keyboard, query)).ToList();
                if (keyBindings.Count == 0)
                    sb.AppendLine($"キー '{query}' には割り当てがありません。");
                else
                {
                    foreach (var b in keyBindings)
                    {
                        var actMode = ActivationModeHelper.GetDisplayName(b.EffectiveKeyboardActivationMode);
                        var modeStr = string.IsNullOrEmpty(actMode) ? "" : $" [{actMode}]";
                        sb.AppendLine($"- {b.DisplayName} ({b.CategoryDisplayName}) キー: {b.KeyboardDisplay}{modeStr}");
                    }
                }
                break;

            default: // search
                sb.AppendLine($"=== キーバインド検索: '{query}' ===");
                var results = allActions.Where(a =>
                    a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.ActionName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.CategoryDisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.CategoryName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.KeyboardDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.MouseDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.GamepadDisplay.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Joystick1Display.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    a.Joystick2Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                ).ToList();

                if (results.Count == 0)
                    sb.AppendLine("該当するキーバインドが見つかりませんでした。");
                else
                {
                    sb.AppendLine($"{results.Count}件見つかりました:\n");
                    foreach (var r in results.Take(30))
                    {
                        var parts = new List<string>();
                        if (!string.IsNullOrEmpty(r.Keyboard))
                            parts.Add($"KB: {r.KeyboardDisplay}");
                        if (!string.IsNullOrEmpty(r.Mouse))
                            parts.Add($"Mouse: {r.MouseDisplay}");
                        if (!string.IsNullOrEmpty(r.Gamepad))
                            parts.Add($"GP: {r.GamepadDisplay}");
                        if (!string.IsNullOrEmpty(r.Joystick1))
                            parts.Add($"HOTAS R: {r.Joystick1Display}");
                        if (!string.IsNullOrEmpty(r.Joystick2))
                            parts.Add($"HOTAS L: {r.Joystick2Display}");
                        var bindStr = parts.Count > 0 ? string.Join(" / ", parts) : "(未割当)";

                        var actMode = ActivationModeHelper.GetDisplayName(r.EffectiveKeyboardActivationMode);
                        var modeStr = string.IsNullOrEmpty(actMode) ? "" : $" [{actMode}]";
                        sb.AppendLine($"- {r.DisplayName} [{r.CategoryDisplayName}] → {bindStr}{modeStr}");
                    }
                    if (results.Count > 30)
                        sb.AppendLine($"... 他 {results.Count - 30}件");
                }
                break;
        }

        return sb.ToString();
    }

    private static string NormalizeKeyForChat(string input)
    {
        var parts = input.Split('+');
        var last = parts[^1].Trim();
        if (last.StartsWith("kb1_", StringComparison.OrdinalIgnoreCase)) last = last[4..];
        return last.ToLowerInvariant();
    }

    private static bool KeyMatchesQuery(string binding, string query)
    {
        var normalized = NormalizeKeyForChat(binding);
        var q = query.Trim().ToLowerInvariant().Replace("kb1_", "").Replace(" ", "");
        if (normalized == q) return true;
        // Also match display name
        var display = InputDisplayHelper.FormatInput(binding).ToLowerInvariant();
        return display.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private static BackendConfig? _verifyBackend;
    private static List<BackendConfig> _verifyBackendCandidates = new();
    private static (string content, string category, List<BackendConfig> backends)? _pendingRemember;
    public static void SetVerifyBackend(BackendConfig? backend) => _verifyBackend = backend;
    public static void SetVerifyBackendCandidates(List<BackendConfig> candidates) => _verifyBackendCandidates = candidates;

    /// <summary>
    /// Checks if there's a pending remember waiting for backend selection.
    /// If user typed a number, verify and save. Returns null if not applicable.
    /// </summary>
    public static string? TryCompletePendingRemember(string userInput)
    {
        if (_pendingRemember == null) return null;
        var pending = _pendingRemember.Value;
        _pendingRemember = null;

        var trimmed = userInput.Trim();
        if (!int.TryParse(trimmed, out var idx) || idx < 1 || idx > pending.backends.Count)
            return "無効な番号です。記憶をキャンセルしました。";

        var chosen = pending.backends[idx - 1];
        Log($"REMEMBER: ユーザーが {chosen.Name}/{chosen.Model} を選択");

        try
        {
            var verifyResult = VerifyWithExternalAIAsync(
                "以下の Star Citizen に関する情報は正しいですか？", pending.content, chosen).GetAwaiter().GetResult();
            if (!verifyResult.Contains("検証OK"))
                return $"検証エージェント ({chosen.Name}/{chosen.Model}) が以下の指摘をしました。保存しません:\n{verifyResult}";
        }
        catch (Exception ex)
        {
            return $"検証エラー: {ex.Message}\n保存しません。";
        }

        if (_queryService == null) return "データベースが未初期化です。";
        var (id, isDup) = _queryService.AddKnowledgeSafe(pending.content, pending.category);
        if (isDup)
        {
            Log($"REMEMBER(pending): duplicate found id={id}");
            return $"類似する記憶が既にあります (ID:{id})。重複保存をスキップしました。";
        }
        Log($"REMEMBER(pending): id={id}, category={pending.category}");
        return $"{chosen.Name}/{chosen.Model} で検証済み — 記憶しました (ID:{id})。\n内容: {pending.content}";
    }

    private static List<BackendConfig> GetUsableBackends()
    {
        return App.Config.Translation.Backends
            .Where(b => b.Enabled && (!string.IsNullOrWhiteSpace(b.ApiKey) || b.Type.Equals("Ollama", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private static string ExecuteRemember(JsonElement args)
    {
        var content = args.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var category = args.TryGetProperty("category", out var cat) ? cat.GetString() ?? "general" : "general";
        if (string.IsNullOrWhiteSpace(content)) return "記憶する内容が空です。";
        if (_queryService == null) return "データベースが未初期化です。";

        // Determine which backend to verify with
        var vb = _verifyBackend;
        if (vb == null && _verifyBackendCandidates.Count > 0)
        {
            // Auto-select the first available candidate
            vb = _verifyBackendCandidates[0];
            Log($"REMEMBER: 検証エージェント未設定 → {vb.Name}/{vb.Model} で自動検証");
        }

        if (vb != null)
        {
            try
            {
                Log($"REMEMBER: {vb.Name}/{vb.Model} で検証して覚えます");
                var verifyResult = VerifyWithExternalAIAsync(
                    "以下の Star Citizen に関する情報は正しいですか？", content, vb).GetAwaiter().GetResult();
                if (verifyResult.Contains("検証OK"))
                {
                    Log($"REMEMBER: 検証OK");
                }
                else
                {
                    Log($"REMEMBER: 検証で指摘あり");
                    return $"検証エージェント ({vb.Name}/{vb.Model}) が以下の指摘をしました。保存しません:\n{verifyResult}\n\nユーザーに正しい情報を確認してから再度「覚えて」と依頼してください。";
                }
            }
            catch (Exception ex)
            {
                Log($"REMEMBER: 検証エラー: {ex.Message}");
                return $"検証中にエラーが発生しました: {ex.Message}\n検証できなかったため保存しません。";
            }
        }
        else
        {
            // No verify backend and no candidates — show numbered list
            var allBackends = GetUsableBackends();
            if (allBackends.Count == 0)
                return "検証に使える AI バックエンドがありません。AI 設定でバックエンドを追加してください。";

            _pendingRemember = (content, category, allBackends);
            var sb = new StringBuilder("どの AI を検証に使いますか？\n");
            for (int i = 0; i < allBackends.Count; i++)
                sb.AppendLine($"{i + 1}. {allBackends[i].Name} ({allBackends[i].Model})");
            return sb.ToString();
        }

        var (id, isDup) = _queryService.AddKnowledgeSafe(content, category);
        if (isDup)
        {
            Log($"REMEMBER: duplicate found id={id}");
            return $"類似する記憶が既にあります (ID:{id})。重複保存をスキップしました。";
        }
        Log($"REMEMBER: id={id}, category={category}, content={content[..Math.Min(100, content.Length)]}");
        var verifiedBy = vb != null ? $"{vb.Name}/{vb.Model} で検証済み — " : "";
        return $"{verifiedBy}記憶しました (ID:{id}, カテゴリ:{category})。\n内容: {content}";
    }

    private static string ExecuteForget(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query)) return "削除するキーワードが空です。";
        if (_queryService == null) return "データベースが未初期化です。";

        var deleted = _queryService.DeleteKnowledge(query);
        Log($"FORGET: query='{query}', deleted={deleted}");
        return deleted > 0 ? $"{deleted}件の記憶を削除しました。" : $"'{query}' に該当する記憶は見つかりませんでした。";
    }

    private static async Task<string> ExecuteSearchLocationAsync(JsonElement args)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(query))
            return "[検索クエリを指定してください]";

        try
        {
            var facilities = StationFacility.LoadAll();
            var matched = facilities.Where(f =>
                f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || f.System.Equals(query, StringComparison.OrdinalIgnoreCase)
                || f.Location.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matched.Count == 0)
            {
                var normalized = query.Replace(" ", "-").Replace("　", "-");
                matched = facilities.Where(f =>
                    f.Name.Replace(" ", "-").Contains(normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var sb = new StringBuilder();

            if (matched.Count > 0)
            {
                foreach (var f in matched)
                {
                    sb.AppendLine($"=== {f.Name} ({f.System}) ===");
                    sb.Append(f.ToSummary());
                }

                if (matched.Count <= 3)
                {
                    foreach (var f in matched)
                        await AppendUexTerminalsAsync(sb, f.Name);
                }
            }
            else
            {
                sb.AppendLine($"[施設データに '{query}' が見つかりません。UEX APIで検索します]");
                await SearchUexLocationsAsync(sb, query);
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result) ? $"['{query}' に一致するステーション・都市は見つかりませんでした]" : result;
        }
        catch (Exception ex)
        {
            return $"[ロケーション検索エラー: {ex.Message}]";
        }
    }

    private static async Task SearchUexLocationsAsync(StringBuilder sb, string query)
    {
        var stationsResp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/space_stations");
        using var stationsDoc = JsonDocument.Parse(stationsResp);
        var citiesResp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/cities");
        using var citiesDoc = JsonDocument.Parse(citiesResp);

        if (stationsDoc.RootElement.TryGetProperty("data", out var stationsData))
        {
            foreach (var s in stationsData.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var id = s.TryGetProperty("id", out var idv) ? idv.GetInt32() : 0;
                var planet = s.TryGetProperty("planet_name", out var p) ? p.GetString() ?? "" : "";
                var system = s.TryGetProperty("star_system_name", out var ss) ? ss.GetString() ?? "" : "";
                sb.AppendLine($"=== {name} ({planet}, {system}) ===");
                await AppendTerminalsAsync(sb, $"id_space_station={id}");
            }
        }

        if (citiesDoc.RootElement.TryGetProperty("data", out var citiesData))
        {
            foreach (var c in citiesData.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var id = c.TryGetProperty("id", out var idv) ? idv.GetInt32() : 0;
                var planet = c.TryGetProperty("planet_name", out var p) ? p.GetString() ?? "" : "";
                var system = c.TryGetProperty("star_system_name", out var ss) ? ss.GetString() ?? "" : "";
                sb.AppendLine($"=== {name} ({planet}, {system}) ===");
                await AppendTerminalsAsync(sb, $"id_city={id}");
            }
        }
    }

    private static async Task AppendUexTerminalsAsync(StringBuilder sb, string stationName)
    {
        try
        {
            var stationsResp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/space_stations");
            using var doc = JsonDocument.Parse(stationsResp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;
            foreach (var s in data.EnumerateArray())
            {
                var name = s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Equals(stationName, StringComparison.OrdinalIgnoreCase)) continue;
                var id = s.TryGetProperty("id", out var idv) ? idv.GetInt32() : 0;
                await AppendTerminalsAsync(sb, $"id_space_station={id}");
                return;
            }
        }
        catch { }
    }

    private static async Task AppendTerminalsAsync(StringBuilder sb, string filter)
    {
        try
        {
            var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/terminals?{filter}");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) { sb.AppendLine("  ターミナルなし"); return; }

            var shops = new List<string>();
            foreach (var t in data.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var type = t.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";

                var services = new List<string>();
                if (t.TryGetProperty("is_shop_fps", out var fps) && fps.GetInt32() == 1) services.Add("FPS装備");
                if (t.TryGetProperty("is_shop_vehicle", out var veh) && veh.GetInt32() == 1) services.Add("船舶装備");
                if (t.TryGetProperty("is_food", out var food) && food.GetInt32() == 1) services.Add("飲食");
                if (t.TryGetProperty("is_medical", out var med) && med.GetInt32() == 1) services.Add("医療");
                if (t.TryGetProperty("is_habitation", out var hab) && hab.GetInt32() == 1) services.Add("宿泊");
                if (t.TryGetProperty("is_refinery", out var ref1) && ref1.GetInt32() == 1) services.Add("精製");
                if (t.TryGetProperty("is_cargo_center", out var cargo) && cargo.GetInt32() == 1) services.Add("貨物");
                if (t.TryGetProperty("is_refuel", out var fuel) && fuel.GetInt32() == 1) services.Add("給油");
                if (t.TryGetProperty("is_repair", out var rep) && rep.GetInt32() == 1) services.Add("修理");
                if (t.TryGetProperty("has_loading_dock", out var dock) && dock.GetInt32() == 1) services.Add("ローディングドック");
                if (t.TryGetProperty("has_docking_port", out var dp) && dp.GetInt32() == 1) services.Add("ドッキングポート");

                var typeLabel = type switch
                {
                    "commodity" => "コモディティ取引",
                    "item" => "ショップ",
                    "fuel" => "燃料",
                    "vehicle_buy" => "船舶販売",
                    "vehicle_rent" => "船舶レンタル",
                    _ => type
                };
                var svcStr = services.Count > 0 ? $" [{string.Join(", ", services)}]" : "";
                shops.Add($"  - {name} ({typeLabel}){svcStr}");
            }

            if (shops.Count == 0)
                sb.AppendLine("  ターミナルなし");
            else
            {
                sb.AppendLine($"  ショップ・施設 ({shops.Count}件):");
                foreach (var s in shops) sb.AppendLine(s);
            }
        }
        catch { sb.AppendLine("  [ターミナル取得エラー]"); }
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

        var namedTasks = new List<(string name, Task<string?> task)>();

        void Add(string name, Task<string?> task) => namedTasks.Add((name, task));

        // ローカルゲームデータ（Data.p4k → StarBreaker オンデマンドクエリ）
        if (_gameDataExtractor?.IsReady == true)
            Add("GameData(DCB)", _gameDataExtractor.QueryGameDataAsync(query));

        // 常に機体・商品データを取得
        Add("UEX Vehicles", FetchUexVehiclesAsync(query));
        Add("UEX Commodities", FetchUexCommoditiesAsync(query));
        Add("UEX CommodityPrices", FetchUexCommodityPricesAsync(query));

        // Wiki から機体詳細（ハードポイント・説明）を取得
        var shipName = ExtractShipName(query);
        if (!string.IsNullOrEmpty(shipName))
            Add("Wiki Ship", FetchWikiShipDataAsync(shipName));

        // UEX アイテム（武器・コンポーネント）データ
        var categoryIds = ExtractItemCategories(query);
        if (categoryIds.Length > 0)
        {
            Add("UEX Items", FetchUexItemsAsync(query, categoryIds));
            Add("UEX ItemPrices", FetchUexItemPricesAsync(query, categoryIds));
        }

        // SC Trade Tools アイテム検索
        var itemKeyword = ExtractItemKeyword(query);
        if (!string.IsNullOrEmpty(itemKeyword))
            Add("SC-Trade Item", FetchScTradeItemAsync(itemKeyword));

        // SC Trade Tools 商品ショップ一覧（貿易・経済関連の質問時）
        if (ContainsAny(lowerQuery, "trade", "route", "profit", "貿易", "交易", "ルート", "利益", "稼",
            "安い", "高い", "最安", "最高", "cheapest", "expensive", "price", "値段", "価格", "いくら",
            "買える", "売れる", "どこで買", "どこで売"))
            Add("SC-Trade Shops", FetchScTradeCommodityShopsAsync());

        var scApiKey = App.Config.ScApiKey;
        if (!string.IsNullOrEmpty(scApiKey))
            Add("SC-API Ships", FetchScApiShipsAsync(scApiKey));

        if (ContainsAny(lowerQuery, "planet", "moon", "station", "location", "惑星", "月", "ステーション", "場所", "拠点",
            "stanton", "pyro", "星系"))
        {
            Add("UEX Terminals", FetchUexTerminalsAsync(query));
            if (!string.IsNullOrEmpty(scApiKey))
                Add("SC-API Starmap", FetchScApiStarmapAsync(scApiKey));
        }

        Console.WriteLine($"[Chat] FetchScData: {namedTasks.Count} タスク開始 ({string.Join(", ", namedTasks.Select(t => t.name))})");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var allTasks = namedTasks.Select(async nt =>
        {
            try
            {
                var result = await nt.task.WaitAsync(cts.Token);
                var elapsed = sw.ElapsedMilliseconds;
                if (!string.IsNullOrEmpty(result))
                    Console.WriteLine($"[Chat]   ✓ {nt.name}: {result.Length} chars ({elapsed}ms)");
                else
                    Console.WriteLine($"[Chat]   - {nt.name}: empty ({elapsed}ms)");
                return result;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[Chat]   ✗ {nt.name}: TIMEOUT ({sw.ElapsedMilliseconds}ms)");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Chat]   ✗ {nt.name}: ERROR {ex.Message} ({sw.ElapsedMilliseconds}ms)");
                return null;
            }
        }).ToArray();

        var results = await Task.WhenAll(allTasks);
        foreach (var r in results)
        {
            if (!string.IsNullOrEmpty(r))
                sb.AppendLine(r);
        }

        Console.WriteLine($"[Chat] FetchScData 完了: {sw.ElapsedMilliseconds}ms, {results.Count(r => !string.IsNullOrEmpty(r))}/{namedTasks.Count} 成功");
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

    public static async Task<string> SendChatAsync(BackendConfig backend, List<ChatMessage> history, bool useTools)
    {
        Log($"SendChatAsync: backend={backend.Type}, model={backend.Model}, tools={useTools}");
        var system = useTools ? ChatSystemPrompt : ChatSystemPrompt.Split("【重要】")[0] + "回答は簡潔で分かりやすい日本語でお願いします。";

        // Extract the latest user question for domain-targeted knowledge injection
        var latestQuestion = history.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var knowledgeSnippet = GetKnowledgeSnippet(latestQuestion);
        if (!string.IsNullOrEmpty(knowledgeSnippet))
        {
            system += knowledgeSnippet;
            Log($"Knowledge injected: {knowledgeSnippet.Split('\n').Length - 2} lines for question: {latestQuestion[..Math.Min(50, latestQuestion.Length)]}");
        }

        return backend.Type.ToLowerInvariant() switch
        {
            "claude" => await SendClaudeChatAsync(backend, system, history, useTools),
            "gemini" => await SendGeminiChatAsync(backend, system, history, useTools),
            "openai" => await SendOpenAiChatAsync(backend, system, history, useTools),
            "ollama" => await SendOllamaChatAsync(backend, system, history, useTools),
            _ => throw new ArgumentException($"Unknown backend: {backend.Type}")
        };
    }

    /// <summary>
    /// 外部 AI に相談して補足情報を取得する。
    /// 主 AI の回答を見せて、補足・修正があれば返してもらう。
    /// 複数バックエンドを並列で呼び出す。
    /// </summary>
    /// <summary>
    /// Build a domain-structured knowledge snippet relevant to the given question.
    /// If question is null/empty, returns all knowledge (for consult/verify contexts).
    /// </summary>
    private static string GetKnowledgeSnippet(string? question = null)
    {
        if (_queryService == null) return "";
        try
        {
            List<(int id, string category, string content, DateTime createdAt)> knowledge;

            if (!string.IsNullOrWhiteSpace(question))
            {
                // Extract domain keywords from the question and search
                var domains = GameDataQueryService.ExtractDomains(question);
                var searchTerms = new List<string>();

                // Add domain-specific keywords that matched
                foreach (var domain in domains)
                    searchTerms.Add(domain);

                // Also add raw question words (2+ chars) for direct content matching
                var qWords = question.Split(' ', '　', '、', '。', '？', '?', '！', '!', '「', '」', '『', '』')
                    .Where(w => w.Length >= 2).Take(8);
                searchTerms.AddRange(qWords);

                knowledge = searchTerms.Count > 0
                    ? _queryService.SearchKnowledge(searchTerms)
                    : _queryService.GetAllKnowledge();

                // If domain search returned few results, also include bug/term entries (always useful)
                if (knowledge.Count < 5)
                {
                    var bugTerms = _queryService.GetKnowledgeByCategory("bug")
                        .Concat(_queryService.GetKnowledgeByCategory("term"))
                        .Where(k => !knowledge.Any(e => e.id == k.id));
                    knowledge = knowledge.Concat(bugTerms).ToList();
                }
            }
            else
            {
                knowledge = _queryService.GetAllKnowledge();
            }

            if (knowledge.Count == 0) return "";

            // Cap at 30 entries to prevent prompt bloat
            if (knowledge.Count > 30)
                knowledge = knowledge.Take(30).ToList();

            // Group by category for structured output
            var grouped = knowledge.GroupBy(k => k.category).OrderBy(g => g.Key);
            var sb = new StringBuilder();
            sb.AppendLine("\n\n【ナレッジDB — 検証済み情報】");
            sb.AppendLine("以下は過去に検証済みの正確な情報です。回答時に該当するナレッジがあれば必ず参照・引用してください。");
            sb.AppendLine("ナレッジの内容と矛盾する回答をしないでください。ナレッジを参照した場合は回答内で明示してください。");

            foreach (var group in grouped)
            {
                var header = group.Key switch
                {
                    "bug" => "🐛 バグ情報",
                    "term" => "📖 用語",
                    "tip" => "💡 Tips",
                    _ => "📋 一般"
                };
                sb.AppendLine($"\n[{header}]");
                foreach (var (id, _, content, _) in group)
                    sb.AppendLine($"  #{id}: {content}");
            }

            return sb.ToString();
        }
        catch { return ""; }
    }

    public static async Task<List<(string name, string response)>> ConsultExternalAIsAsync(
        string userQuestion, string primaryAnswer, List<BackendConfig> consultBackends)
    {
        var consultSystem =
            "あなたは Star Citizen（スターシチズン）というオンラインゲームに詳しい補助 AI です。\n" +
            "ユーザーの質問と、別の AI が生成した回答が提示されます。\n" +
            "質問は Star Citizen に関するものである前提で、その回答に対して補足情報・修正・別の視点があれば簡潔に日本語で回答してください。\n" +
            "ゲーム内の用語・施設・ミッション・アイテム・船舶などについて、正確な情報を提供してください。\n" +
            "重要: 現在のゲームバージョンは LIVE Alpha 4.8 です。Stanton, Pyro, Nyx の3星系が実装済みです。「Nyx は未実装」「Pyro は未実装」等の古い情報を回答しないでください。\n" +
            "回答が既に十分であれば「補足はありません」と答えてください。\n" +
            "回答は簡潔に、箇条書きを使ってください。" +
            GetKnowledgeSnippet(userQuestion);

        var consultHistory = new List<ChatMessage>
        {
            new() { Role = "user", Content =
                $"【ユーザーの質問】\n{userQuestion}\n\n【別のAIの回答】\n{primaryAnswer}\n\n上記の回答に対して、補足・修正・追加情報があれば教えてください。" }
        };

        var tasks = consultBackends.Select(async backend =>
        {
            try
            {
                Log($"[Consult] {backend.Name}/{backend.Model} に相談開始");
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var response = await Task.Run(async () =>
                {
                    return backend.Type.ToLowerInvariant() switch
                    {
                        "claude" => await SendClaudeChatAsync(backend, consultSystem, consultHistory, useTools: false),
                        "gemini" => await SendGeminiChatAsync(backend, consultSystem, consultHistory, useTools: false),
                        "openai" => await SendOpenAiChatAsync(backend, consultSystem, consultHistory, useTools: false),
                        "ollama" => await SendOllamaChatAsync(backend, consultSystem, consultHistory, useTools: false),
                        _ => $"[未対応: {backend.Type}]"
                    };
                }, cts.Token);
                Log($"[Consult] {backend.Name}/{backend.Model} 完了: {response.Length} chars");
                return (name: $"{backend.Name} ({backend.Model})", response);
            }
            catch (Exception ex)
            {
                Log($"[Consult] {backend.Name}/{backend.Model} エラー: {ex.Message}");
                return (name: $"{backend.Name} ({backend.Model})", response: $"[エラー: {ex.Message}]");
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Extracts a concise knowledge summary from the user question and verification result.
    /// </summary>
    public static string ExtractKnowledgeSummary(string userQuestion, string verifyResult)
    {
        // Build a short, factual summary for knowledge storage
        // Remove common prefixes and keep the core information
        var lines = verifyResult.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var factLines = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimStart('-', '*', '•', ' ');
            // Skip meta lines (headers, evaluation comments, etc.)
            if (trimmed.StartsWith("検証結果") || trimmed.StartsWith("検証OK") ||
                trimmed.StartsWith("各回答") || trimmed.StartsWith("| ") ||
                trimmed.StartsWith("---") || trimmed.Length < 5)
                continue;
            // Keep factual content lines
            if (trimmed.Contains("は") || trimmed.Contains("です") || trimmed.Contains("である") ||
                trimmed.Contains("略称") || trimmed.Contains("施設") || trimmed.Contains("星系") ||
                trimmed.Contains("実装") || trimmed.Contains("という"))
            {
                factLines.Add(trimmed);
                if (factLines.Count >= 5) break; // limit to 5 key facts
            }
        }

        if (factLines.Count == 0) return "";

        // Prepend the topic from the user question
        var topic = userQuestion.Length > 50 ? userQuestion[..50] + "..." : userQuestion;
        return $"Q: {topic}\n" + string.Join("\n", factLines.Select(l => $"→ {l}"));
    }

    /// <summary>
    /// Checks whether the primary AI response indicates it couldn't answer the question.
    /// </summary>
    public static bool IsResponseInsufficient(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return true;
        var lower = response.ToLowerInvariant();
        var insufficientPatterns = new[]
        {
            "わかりません", "分かりません", "不明です", "情報が見つかりません",
            "見つかりませんでした", "見つけることができません",
            "確認できません", "答えられません", "お答えできません",
            "i don't know", "i'm not sure", "i cannot", "i can't",
            "情報がありません", "データがありません", "該当する情報",
            "申し訳ありません", "sorry, i",
            "もう少し詳しい情報", "詳しく教えていただけ",
            "特定できません", "判断できません"
        };
        // Check if the response is very short (likely unhelpful)
        if (response.Length < 30) return true;
        return insufficientPatterns.Any(p => lower.Contains(p));
    }

    /// <summary>
    /// Calls a single verification AI to cross-check the primary response.
    /// </summary>
    public static async Task<string> VerifyWithExternalAIAsync(
        string userQuestion, string primaryAnswer, BackendConfig verifyBackend)
    {
        var verifySystem =
            "あなたは Star Citizen（スターシチズン）というオンラインゲームに詳しい検証エージェントです。\n" +
            "ユーザーの質問と、別の AI が生成した回答が提示されます。\n" +
            "質問は Star Citizen に関するものである前提で、その回答の正確性を検証してください。\n" +
            "ゲーム内の用語・施設・ミッション・アイテム・船舶などについて正確な知識に基づいて判断してください。\n" +
            "重要: 現在のゲームバージョンは LIVE Alpha 4.8 です。Stanton, Pyro, Nyx の3星系が実装済みです。「Nyx は未実装」「Pyro は未実装」等の古い情報を回答しないでください。\n" +
            "正確であれば「検証OK: 回答は正確です」と答えてください。\n" +
            "誤りや不足があれば、正しい情報を簡潔に日本語で提供してください。" +
            GetKnowledgeSnippet(userQuestion);

        var verifyHistory = new List<ChatMessage>
        {
            new() { Role = "user", Content =
                $"【ユーザーの質問】\n{userQuestion}\n\n【検証対象の回答】\n{primaryAnswer}\n\n上記の回答を検証してください。正確ですか？誤りや不足があれば指摘してください。" }
        };

        try
        {
            Log($"[Verify] {verifyBackend.Name}/{verifyBackend.Model} に検証依頼");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var response = await Task.Run(async () =>
            {
                return verifyBackend.Type.ToLowerInvariant() switch
                {
                    "claude" => await SendClaudeChatAsync(verifyBackend, verifySystem, verifyHistory, useTools: false),
                    "gemini" => await SendGeminiChatAsync(verifyBackend, verifySystem, verifyHistory, useTools: false),
                    "openai" => await SendOpenAiChatAsync(verifyBackend, verifySystem, verifyHistory, useTools: false),
                    "ollama" => await SendOllamaChatAsync(verifyBackend, verifySystem, verifyHistory, useTools: false),
                    _ => $"[未対応: {verifyBackend.Type}]"
                };
            }, cts.Token);
            Log($"[Verify] {verifyBackend.Name}/{verifyBackend.Model} 完了: {response.Length} chars");
            return response;
        }
        catch (Exception ex)
        {
            Log($"[Verify] {verifyBackend.Name}/{verifyBackend.Model} エラー: {ex.Message}");
            return $"[検証エラー: {ex.Message}]";
        }
    }

    private static async Task<string> SendClaudeChatAsync(BackendConfig config, string system, List<ChatMessage> history, bool useTools = true)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var tools = useTools ? GetToolDefinitions().Select(t => new Dictionary<string, object>
        {
            ["name"] = t["name"],
            ["description"] = t["description"],
            ["input_schema"] = t["input_schema"]
        }).ToList() : new List<Dictionary<string, object>>();

        var conversationMessages = new List<object>();
        foreach (var m in history)
            conversationMessages.Add(new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content });

        Log($"CLAUDE: model={config.Model}, messages={conversationMessages.Count}, tools={tools.Count}");
        int maxIter = useTools ? 5 : 1;
        for (int iteration = 0; iteration < maxIter; iteration++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["max_tokens"] = 4096,
                ["system"] = system,
                ["messages"] = conversationMessages,
            };
            if (!config.Model.Contains("opus", StringComparison.OrdinalIgnoreCase))
                body["temperature"] = 0.7;
            if (useTools && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = new Dictionary<string, object> { ["type"] = iteration == 0 ? "any" : "auto" };
            }

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

    private static async Task<string> SendGeminiChatAsync(BackendConfig config, string system, List<ChatMessage> history, bool useTools = true)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.Model}:generateContent?key={config.ApiKey}";

        var functionDeclarations = useTools ? GetGeminiFunctionDeclarations() : new List<Dictionary<string, object>>();

        var conversationContents = new List<object>();
        foreach (var m in history)
        {
            conversationContents.Add(new Dictionary<string, object>
            {
                ["role"] = m.Role == "assistant" ? "model" : "user",
                ["parts"] = new[] { new Dictionary<string, object> { ["text"] = m.Content } }
            });
        }

        Log($"GEMINI: model={config.Model}, messages={conversationContents.Count}, tools={useTools}");
        int maxIter = useTools ? 5 : 1;
        for (int iteration = 0; iteration < maxIter; iteration++)
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
            };
            if (useTools && functionDeclarations.Count > 0)
            {
                body["tools"] = new[] { new Dictionary<string, object> { ["function_declarations"] = functionDeclarations } };
                body["toolConfig"] = new Dictionary<string, object>
                {
                    ["functionCallingConfig"] = new Dictionary<string, object> { ["mode"] = iteration == 0 ? "ANY" : "AUTO" }
                };
            }

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

    private static async Task<string> SendOpenAiChatAsync(BackendConfig config, string system, List<ChatMessage> history, bool useTools = true)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        http.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");

        var openAiTools = useTools ? GetToolDefinitions().Select(t => new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = t["name"],
                ["description"] = t["description"],
                ["parameters"] = t["input_schema"]
            }
        }).ToList() : new List<Dictionary<string, object>>();

        var conversationMessages = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "system", ["content"] = system }
        };
        foreach (var m in history)
            conversationMessages.Add(new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content });

        Log($"OPENAI: model={config.Model}, messages={conversationMessages.Count}, tools={useTools}");

        int maxIter = useTools ? 5 : 1;
        for (int iteration = 0; iteration < maxIter; iteration++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["messages"] = conversationMessages,
                ["temperature"] = 0.7,
                ["max_completion_tokens"] = 4096,
            };
            if (useTools && openAiTools.Count > 0)
            {
                body["tools"] = openAiTools;
                body["tool_choice"] = iteration == 0 ? "required" : "auto";
            }

            var json = JsonSerializer.Serialize(body, JsonOpts);
            Log($"OPENAI REQUEST (iter={iteration})");
            var resp = await http.PostAsync("https://api.openai.com/v1/chat/completions",
                new StringContent(json, Encoding.UTF8, "application/json"));

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                var code = (int)resp.StatusCode;
                if (code == 429 || code == 402 ||
                    err.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                    err.Contains("billing", StringComparison.OrdinalIgnoreCase))
                {
                    return "⚠️ OpenAI API のクレジットが不足しているか、レート制限に達しました。OpenAI Platform (platform.openai.com) で残高と使用量を確認してください。";
                }
                throw new HttpRequestException($"{code} - {err}");
            }

            var respJson = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(respJson);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() ?? "" : "";
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";

            if (finishReason == "tool_calls" && message.TryGetProperty("tool_calls", out var toolCalls))
            {
                Log($"OPENAI: tool_calls detected, count={toolCalls.GetArrayLength()}");
                conversationMessages.Add(new Dictionary<string, object>
                {
                    ["role"] = "assistant",
                    ["content"] = content,
                    ["tool_calls"] = JsonSerializer.Deserialize<object>(toolCalls.GetRawText())!
                });

                foreach (var tc in toolCalls.EnumerateArray())
                {
                    var toolCallId = tc.GetProperty("id").GetString() ?? "";
                    var func = tc.GetProperty("function");
                    var funcName = func.GetProperty("name").GetString() ?? "";
                    var funcArgsStr = func.GetProperty("arguments").GetString() ?? "{}";
                    using var argsDoc = JsonDocument.Parse(funcArgsStr);

                    var result = await ExecuteToolAsync(funcName, argsDoc.RootElement);
                    conversationMessages.Add(new Dictionary<string, object>
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = toolCallId,
                        ["content"] = result
                    });
                }
                continue;
            }

            Log($"OPENAI: final response, content_len={content.Length}");
            return content;
        }

        return "[ツール呼び出し回数の上限に達しました]";
    }

    private static async Task<string> SendOllamaChatAsync(BackendConfig config, string system, List<ChatMessage> history, bool useTools = true)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var url = $"{config.BaseUrl.TrimEnd('/')}/api/chat";

        var ollamaTools = useTools ? GetToolDefinitions().Select(t => new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = t["name"],
                ["description"] = t["description"],
                ["parameters"] = t["input_schema"]
            }
        }).ToList() : new List<Dictionary<string, object>>();

        var conversationMessages = new List<Dictionary<string, object>>
        {
            new() { ["role"] = "system", ["content"] = system }
        };
        foreach (var m in history)
            conversationMessages.Add(new Dictionary<string, object> { ["role"] = m.Role, ["content"] = m.Content });

        Log($"OLLAMA: model={config.Model}, url={url}, messages={conversationMessages.Count}, tools={useTools}");

        int maxIter = useTools ? 5 : 1;
        for (int iteration = 0; iteration < maxIter; iteration++)
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = config.Model,
                ["messages"] = conversationMessages,
                ["stream"] = false,
                ["options"] = new Dictionary<string, object> { ["temperature"] = 0.7, ["num_predict"] = 4096 },
            };
            if (useTools && ollamaTools.Count > 0)
                body["tools"] = ollamaTools;

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
