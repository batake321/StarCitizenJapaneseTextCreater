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

    private static readonly string ChatSystemPrompt =
        "あなたはゲーム「Star Citizen」の情報アシスタントです。プレイヤーからの質問に日本語で回答してください。\n\n" +
        "【最重要ルール】以下に「Star Citizen データ」が提供されている場合、そのデータは複数の情報源（UEX Corp API、SC Trade Tools、starcitizen.tools Wiki）からリアルタイムに取得した最新の正確なゲーム内データです。\n" +
        "このデータに含まれる商品・資源・機体・武器・装備・場所は実際にゲーム内に存在します。「存在しない」「データがない」と回答しないでください。\n" +
        "提供データを最優先で参照し、データに基づいて回答してください。\n" +
        "複数ソースのデータがある場合は相互に検証し、最も詳細で正確な情報を提供してください。\n" +
        "データがない項目についてのみ、あなたの知識に基づいて回答してください。\n" +
        "憶測で回答しないでください。データに記載がなく、確信が持てない場合は「わかりません」と正直に答えてください。\n\n" +
        "回答は簡潔で分かりやすい日本語でお願いします。";

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

    public static void SetGameDataExtractor(GameDataExtractor extractor)
    {
        _gameDataExtractor = extractor;
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
        if (ContainsAny(lowerQuery, "trade", "route", "profit", "貿易", "交易", "ルート", "利益", "稼"))
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
        // 機体名が含まれていたら武器+コンポーネント全部取得
        if (ids.Count == 0 && ExtractShipName(query) != null)
            foreach (var id in new[] { 32, 33, 34, 35, 19, 21, 22, 23 }) ids.Add(id);
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

    public static string? ExtractShipNamePublic(string query) => ExtractShipName(query);

    private static string? ExtractShipName(string query)
    {
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

        // UEX の機体名リストでも検索
        return null;
    }

    private static async Task<string?> FetchWikiShipDataAsync(string shipName)
    {
        try
        {
            var wikiTitle = shipName.Replace(" ", "_");
            var url = $"https://starcitizen.tools/api.php?action=parse&page={Uri.EscapeDataString(wikiTitle)}&prop=text&section=0&format=json";
            var resp = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(resp);

            if (!doc.RootElement.TryGetProperty("parse", out var parse)) return null;
            var introHtml = parse.GetProperty("text").GetProperty("*").GetString() ?? "";
            var introText = StripHtml(introHtml);

            // ハードポイント（Specifications セクション = section 2）
            var specUrl = $"https://starcitizen.tools/api.php?action=parse&page={Uri.EscapeDataString(wikiTitle)}&prop=text&section=2&format=json";
            var specResp = await Http.GetStringAsync(specUrl);
            using var specDoc = JsonDocument.Parse(specResp);
            var specText = "";
            if (specDoc.RootElement.TryGetProperty("parse", out var specParse))
            {
                var specHtml = specParse.GetProperty("text").GetProperty("*").GetString() ?? "";
                specText = StripHtml(specHtml);
            }

            var sb = new StringBuilder($"=== starcitizen.tools Wiki: {shipName} ===\n");

            if (introText.Length > 100)
                sb.AppendLine($"概要: {introText[..Math.Min(1500, introText.Length)]}");

            if (specText.Length > 50)
                sb.AppendLine($"\nスペック・ハードポイント:\n{specText[..Math.Min(3000, specText.Length)]}");

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

            var sb = new StringBuilder("=== UEX 機体データ ===\n");
            int count = 0;
            foreach (var v in data.EnumerateArray())
            {
                var name = v.GetProperty("name").GetString() ?? "";
                var manufacturer = v.TryGetProperty("manufacturer_name", out var mfr) ? mfr.GetString() ?? "" : "";
                var focus = v.TryGetProperty("focus", out var f) ? f.GetString() ?? "" : "";
                var crew = v.TryGetProperty("crew", out var c) ? c.ToString() : "";
                var cargo = v.TryGetProperty("scu", out var cg) ? cg.ToString() : "";
                var price = v.TryGetProperty("price", out var p) ? p.ToString() : "";
                var size = v.TryGetProperty("size", out var sz) ? sz.GetString() ?? "" : "";

                sb.AppendLine($"- {manufacturer} {name} | 役割: {focus} | 乗員: {crew} | カーゴ: {cargo} SCU | サイズ: {size} | 価格: {price} aUEC");
                count++;
            }
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

            var sb = new StringBuilder($"=== UEX 場所別価格: {commodityName} ===\n");
            int count = 0;
            foreach (var item in priceData.EnumerateArray())
            {
                var terminal = item.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
                var city = item.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
                var planet = item.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
                var star = item.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
                var buy = item.TryGetProperty("price_buy", out var pb) ? pb.GetInt32() : 0;
                var sell = item.TryGetProperty("price_sell", out var ps) ? ps.GetInt32() : 0;

                if (buy > 0 || sell > 0)
                {
                    var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(s => !string.IsNullOrEmpty(s)));
                    var priceInfo = new List<string>();
                    if (buy > 0) priceInfo.Add($"買値: {buy}");
                    if (sell > 0) priceInfo.Add($"売値: {sell}");
                    sb.AppendLine($"- {location} | {string.Join(" | ", priceInfo)} aUEC");
                    count++;
                }
            }

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
                    var buy = p.TryGetProperty("price_buy", out var pb) ? pb.GetInt32() : 0;

                    if (buy > 0)
                    {
                        var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(s => !string.IsNullOrEmpty(s)));
                        sb.AppendLine($"  - {location} | 価格: {buy} aUEC");
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
        var systemMsg = ChatSystemPrompt;
        if (!string.IsNullOrEmpty(scData))
            systemMsg += $"\n\n--- Star Citizen データ ---\n{scData}";

        return backend.Type.ToLowerInvariant() switch
        {
            "claude" => await SendClaudeChatAsync(backend, systemMsg, history),
            "gemini" => await SendGeminiChatAsync(backend, systemMsg, history),
            "ollama" => await SendOllamaChatAsync(backend, systemMsg, history),
            _ => throw new ArgumentException($"Unknown backend: {backend.Type}")
        };
    }

    private static async Task<string> SendClaudeChatAsync(BackendConfig config, string system, List<ChatMessage> history)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        http.DefaultRequestHeaders.Add("x-api-key", config.ApiKey);
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var messages = history.Select(m => new { role = m.Role, content = m.Content }).ToArray();

        var body = new
        {
            model = config.Model,
            max_tokens = 4096,
            temperature = 0.7,
            system,
            messages
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

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
        return doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> SendGeminiChatAsync(BackendConfig config, string system, List<ChatMessage> history)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{config.Model}:generateContent?key={config.ApiKey}";

        var contents = history.Select(m => new
        {
            role = m.Role == "assistant" ? "model" : "user",
            parts = new[] { new { text = m.Content } }
        }).ToArray();

        var body = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents,
            generationConfig = new { temperature = 0.7, maxOutputTokens = 4096 }
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

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
        return doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text").GetString() ?? "";
    }

    private static async Task<string> SendOllamaChatAsync(BackendConfig config, string system, List<ChatMessage> history)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(300) };
        var url = $"{config.BaseUrl.TrimEnd('/')}/api/chat";

        var messages = new List<object>
        {
            new { role = "system", content = system }
        };
        messages.AddRange(history.Select(m => (object)new { role = m.Role, content = m.Content }));

        var body = new
        {
            model = config.Model,
            messages,
            stream = false,
            options = new { temperature = 0.7, num_predict = 4096 }
        };

        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var resp = await http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"{(int)resp.StatusCode} - URL: {url} - {err}");
        }

        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        return doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
    }
}
