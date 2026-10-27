using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;

namespace StarCitizenJapaneseTextCreater;

// データ API (読み取り専用の GET)。ゲームのデータ (詳細タブ・交易・契約・BP・星図) と個人のデータ (パスが /api/my/ で始まるもの) を返す。
//   データは画面と同じ既存のメソッドで読む (画面と API の中身を一致させるため)。DB には書かない: 個人のデータは HangarService / OreService の
//   読み取り専用の版 (…ReadOnly。Mode=ReadOnly で開き、表を作る DDL・重複の行のまとめの書き込みを通らない) で読む。
//   出力: クエリ format=json (既定) か format=html。それ以外は 400。
//     JSON: 一覧は { "total": limit・offset の前の全体の件数, "count": 返した件数, "items": [...] }、詳細は { "record", "name", "text" }。
//           値は既存メソッドの戻り値の公開プロパティそのまま (名前付きのタプルは Item1・Item2… になる。列挙は名前の文字)。日本語は \uXXXX にしない。
//           生の JSON の項目 (RawJsonNames。契約の RawJson) は raw=1 を付けたときだけ出す
//     HTML: 一覧は表 (列 = JSON の項目)、詳細は DetailHtml.FromDetailText (scjt://detail/<record> のリンクは /api/detail?record=<record>&format=html に書き換える)
//   一覧には limit (1 以上の整数。大きすぎる値は int の最大として扱う)・offset (0 以上の整数) を付けられる。無ければ全件。不正な値は 400
//   (/api/search の limit は 1〜1000。範囲外は 400)
//   個人のデータ (/api/my/) は、要求元がこの PC (ループバック) で、Host ヘッダーのホスト名が localhost・127.0.0.1・[::1] のときだけ返す。それ以外は 403。
//   個人のデータの応答には CORS のヘッダーを付けない (ほかのサイトのページからブラウザー経由で読まれないように)。
//   ほかのルートの中の個人のデータ (BP の所持・在庫・手持ち。MixedPersonalPaths) も同じ条件のときだけ出し、そのときは CORS を付けない。
//   条件を満たさない要求 (LAN・Host 違い) には個人のデータを出さず、CORS を付けて返す
//   エラーは format にかかわらず JSON の { "error": "..." }。500 は固定の文 (例外の中身は app_log.txt に残す)
public partial class ChatWebServer
{
    // 交易 (価格・端末・所持船) は MainWindow がメモリに持つ TradeService を読む (MainWindow.StartWebServerAsync で渡す)。渡されていなければ交易の API は 503
    private TradeService? _tradeService;
    // 交易ルートの商品名の表示 (画面と同じ MainWindow.CommodityLabel。UI のスレッドで呼ぶもの)。英語名の並び → 表示名の並び
    private Func<IReadOnlyList<string>, List<string>>? _commodityLabeler;

    public void SetTradeService(TradeService tradeService)
    {
        _tradeService = tradeService;
    }

    public void SetCommodityLabeler(Func<IReadOnlyList<string>, List<string>> labeler)
    {
        _commodityLabeler = labeler;
    }

    // ===== ルートの一覧 (/api で出す。ルーティングもこの一覧のパスだけを受ける) =====

    private sealed record DataApiRoute(string Path, string Description, string Parameters, bool Personal);

    private static readonly DataApiRoute[] DataApiRoutes =
    [
        new("/api", "API の一覧", "", false),
        new("/api/search", "詳細タブの検索 (分類が (すべて) のときと同じ。星系・場所・お店・資源・品目)", "q=名前, limit=品目の候補の最大件数 (1〜1000。既定 40。星系・場所・お店・資源の候補はこの数に入らない), offset", false),
        new("/api/detail", "詳細タブの詳細 (品目・資源・お店・場所・星系)", "record=record 名 (必須)", false),
        new("/api/ships", "詳細タブの分類「船舶」", "kind=種類, size=サイズ (3 か S3), name=名前の一部, limit, offset", false),
        new("/api/ship-weapons", "詳細タブの分類「船舶武器」", "kind, size, name, limit, offset", false),
        new("/api/personal-weapons", "詳細タブの分類「個人武器」", "kind, name, limit, offset", false),
        new("/api/ship-gear", "詳細タブの分類「船舶装備」", "kind, size, name, limit, offset", false),
        new("/api/personal-gear", "詳細タブの分類「個人装備」(種類はアーマー・服は「種類 (部位)」)", "kind, name, limit, offset", false),
        new("/api/ores", "詳細タブの分類「鉱石」", "kind, name, limit, offset", false),
        new("/api/shops", "詳細タブの分類「お店」", "kind, name, limit, offset", false),
        new("/api/stations", "詳細タブの分類「ステーション」", "kind, name, limit, offset", false),
        new("/api/systems", "詳細タブの分類「星系」", "kind, name, limit, offset", false),
        new("/api/trade/commodities", "交易のコモディティ", "limit, offset", false),
        new("/api/trade/terminals", "交易の端末", "limit, offset", false),
        new("/api/trade/prices", "端末で買える物・売れる物", "terminal=端末名 (必須), limit, offset (買える物・売れる物それぞれに)", false),
        new("/api/trade/routes", "交易ルートの検索", "budget=予算 aUEC (必須。画面と同じ書き方: 1000000, 1M, 500k, 3.5m), scu=積載量 (必須), buySystem・sellSystem=星系 (既定 全て), excludeOutposts・loadingDockOnly・excludeLowStock=true/false, commodities=コモディティ名をカンマ区切り, top=件数 (既定 20), limit, offset", false),
        new("/api/missions/categories", "契約の分類", "limit, offset", false),
        new("/api/missions", "契約の一覧", "category=分類 か q=検索語 (どちらか一方。q が空なら全部), raw=1 で RawJson も出す, limit, offset", false),
        new("/api/missions/detail", "契約の詳細と納品物 (Wikelo の契約)", "record=契約の record 名 (必須)", false),
        new("/api/blueprints", "ブループリント (record 無しなら全部。BP タブの表と同じ)", "record=できる品の record 名 (省略可), limit, offset", false),
        new("/api/starmap", "星図の星系・場所", "limit, offset", false),
        new("/api/my/ships", "所持船", "limit, offset", true),
        new("/api/my/components", "所持コンポーネント", "limit, offset", true),
        new("/api/my/gear", "所持している個人装備", "limit, offset", true),
        new("/api/my/blueprints", "所持している BP の uuid と BP の名前", "limit, offset", true),
        new("/api/my/hangar/pledges", "Hangar の pledge", "limit, offset", true),
        new("/api/my/hangar/ships", "Hangar の保有船 (InstanceKey は /api/my/loadout の ship に使う)", "limit, offset", true),
        new("/api/my/hangar/ccus", "Hangar の CCU", "limit, offset", true),
        new("/api/my/loadout", "保有船の今の装備と付け替えの候補 (装備タブの表と同じ)", "ship=保有船の InstanceKey か my|所持船の Id (必須), limit, offset", true),
        new("/api/my/gamelog", "ゲームログの残してある行", "limit, offset", true),
        new("/api/my/mining", "採掘の記録", "limit, offset", true),
        new("/api/my/ore-stock", "鉱石の在庫", "limit, offset", true),
        new("/api/my/commodity-trades", "商品売買の記録", "limit, offset", true),
    ];

    private static readonly HashSet<string> DataApiPaths = new(DataApiRoutes.Select(r => r.Path), StringComparer.Ordinal);

    private const string PersonalPrefix = "/api/my/";

    // ゲームのデータのルートのうち、個人のデータ (BP の所持・在庫・手持ち) を混ぜて出すもの。混ぜるのは個人のデータを含めてよい要求 (local) のときだけで、
    // そのときは CORS を付けない。それ以外の要求には個人のデータを混ぜず、CORS を付ける
    private static readonly HashSet<string> MixedPersonalPaths = new(StringComparer.Ordinal) { "/api/blueprints", "/api/detail", "/api/missions/detail" };

    // データ API のパスか (小文字・完全一致)
    private static bool IsDataApiPath(string path) => DataApiPaths.Contains(path);

    // データ API より前からあるルートの /api/ のパス (HandleRequestAsync の switch。/api/voicevox/ で始まるものはその前で受ける)。
    // これらはメソッドが違うときも今までどおり (本文の無い 404)
    private static readonly HashSet<string> ExistingApiPaths = new(StringComparer.Ordinal)
    {
        "/api/backends", "/api/chat", "/api/voicevox/speakers", "/api/settings", "/api/knowledge",
    };

    // /api/ で始まり、既存のルートにもデータ API のルートにも当たらないパスか (404 と { "error": ... } を返す)
    private static bool IsUnknownApiPath(string path) =>
        path.StartsWith("/api/", StringComparison.Ordinal) && !ExistingApiPaths.Contains(path) && !IsDataApiPath(path);

    private static Task WriteUnknownApiAsync(HttpListenerResponse response, CancellationToken ct) =>
        WriteApiErrorAsync(response, 404, "そのパスの API はありません", cors: false, ct);

    // ===== JSON の書き出し =====

    // 日本語をそのまま出す。名前付きのタプル (フィールド) も出す。NaN・Infinity は文字で出す。列挙は名前で出す
    private static readonly JsonSerializerOptions ApiJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    private static JsonNode? ToNode(object? value) => JsonSerializer.SerializeToNode(value, ApiJsonOptions);

    // 生の JSON の項目 (既定では出さない。raw=1 のときだけ出す): MissionService.MissionEntry.RawJson (/api/missions)
    private static readonly HashSet<string> RawJsonNames = new(StringComparer.Ordinal) { "RawJson" };

    // 個人のデータを含めてよくない要求 (LAN・Host 違い) で除く BP の個人のデータの項目: BlueprintItem.StockText・Owned と、record 付きの行の stockText・haves
    private static readonly HashSet<string> BlueprintPersonalNames = new(StringComparer.Ordinal) { "StockText", "Owned", "stockText", "haves" };

    // node の中 (入れ子も) の、names の名前の項目を除く
    private static void RemoveProperties(JsonNode? node, HashSet<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var k in o.Select(kv => kv.Key).Where(names.Contains).ToList()) o.Remove(k);
                foreach (var (_, v) in o) RemoveProperties(v, names);
                break;
            case JsonArray a:
                foreach (var e in a) RemoveProperties(e, names);
                break;
        }
    }

    // ===== 要求・結果 =====

    // 要求の引数 (format・limit・offset・raw は読み済み)。Local は個人のデータを混ぜてよい要求か (ループバックで、Host が localhost・127.0.0.1・[::1])
    private sealed class ApiQuery(NameValueCollection qs, bool html, int? limit, int offset, bool raw, bool local, string path)
    {
        public NameValueCollection Qs { get; } = qs;
        public bool Html { get; } = html;
        public int? Limit { get; } = limit;
        public int Offset { get; } = offset;
        public bool Raw { get; } = raw;
        public bool Local { get; } = local;
        public string Path { get; } = path;

        public string? Get(string key) => Qs[key];

        // 必須の文字の引数 (前後の空白は除く)。無い・空なら 400
        public string Required(string key)
        {
            var v = (Qs[key] ?? "").Trim();
            if (v.Length == 0) throw new ApiError(400, $"引数 {key} を指定してください");
            return v;
        }

        // 絞り込みの引数 (前後の空白は除く)。無い・空なら null (絞り込まない)
        public string? Filter(string key)
        {
            var v = (Qs[key] ?? "").Trim();
            return v.Length == 0 ? null : v;
        }

        // offset 件を飛ばし、limit 件まで (limit が無ければ残り全部)
        public List<T> Paginate<T>(IEnumerable<T> source)
        {
            var s = source.Skip(Offset);
            if (Limit is int l) s = s.Take(l);
            return s.ToList();
        }
    }

    // 状態コードつきのエラー (dispatcher が { "error": "..." } にする)
    private sealed class ApiError(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    // 結果: JSON と、HTML を作る関数 (null ならエラーなどで HTML を作らない)
    private sealed record ApiResult(JsonObject Json, Func<string>? Html);

    // ===== 入口 =====

    private async Task HandleDataApiAsync(HttpListenerContext ctx, string path, CancellationToken ct)
    {
        var personal = path.StartsWith(PersonalPrefix, StringComparison.Ordinal);
        // 個人のデータを含めてよい要求か: 要求元がこの PC (ループバック) で、Host のホスト名が localhost・127.0.0.1・[::1] (/api/my/ もほかのルートの所持・在庫も同じ判定)
        var local = ctx.Request.RemoteEndPoint?.Address is { } addr && IPAddress.IsLoopback(addr) && IsLocalHostHeader(ctx.Request.Headers["Host"]);
        // 個人のデータを含める応答 (/api/my/ と、local のときの MixedPersonalPaths) には CORS を付けない (ほかのサイトのページから読まれないように)
        var cors = !personal && !(local && MixedPersonalPaths.Contains(path));
        int status;
        string contentType = "application/json; charset=utf-8";
        string body;
        try
        {
            if (ctx.Request.HttpMethod != "GET") throw new ApiError(405, "GET だけを受け付けます");
            if (personal && !local)
                throw new ApiError(403, "個人のデータはこの PC からだけ読めます");

            var qs = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            var format = qs["format"];
            bool html = format switch
            {
                null or "json" => false,
                "html" => true,
                _ => throw new ApiError(400, "format は json か html を指定してください"),
            };
            var limit = path == "/api/search" ? ParseSearchLimit(qs) : ParseLimit(qs);
            var offset = ParseCount(qs, "offset", 0, "offset は 0 以上の整数を指定してください") ?? 0;
            var raw = ParseBool(qs["raw"], "raw");
            var q = new ApiQuery(qs, html, limit, offset, raw, local, path);

            var result = await DispatchDataApiAsync(q, ct);
            if (!raw) RemoveProperties(result.Json, RawJsonNames);
            status = 200;
            if (html && result.Html != null)
            {
                contentType = "text/html; charset=utf-8";
                body = result.Html();
            }
            else body = result.Json.ToJsonString(ApiJsonOptions);
        }
        catch (ApiError e)
        {
            status = e.Status;
            body = ErrorJson(e.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // サーバーを止めた (応答は返さずに切る)
            try { ctx.Response.Abort(); } catch { }
            return;
        }
        catch (Exception ex)
        {
            // 例外の中身は応答に出さず、アプリのログ (app_log.txt) に残す
            try { App.AppendStartupLog(DetailPanel.WorkDir(), $"[データ API] {path} で内部エラー: {ex}"); } catch { }
            status = 500;
            body = ErrorJson("内部エラー");
        }
        await WriteApiResponseAsync(ctx.Response, status, contentType, body, cors, ct);
    }

    // Host ヘッダーのホスト名 (ポートは問わない。大小文字は無視) が localhost・127.0.0.1・[::1] か (DNS rebinding 対策)
    private static bool IsLocalHostHeader(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return false;
        string name;
        if (h.StartsWith('['))
        {
            var end = h.IndexOf(']');
            if (end < 0) return false;
            name = h[..(end + 1)];
            var rest = h[(end + 1)..];
            if (rest.Length > 0 && !rest.StartsWith(':')) return false;
        }
        else
        {
            var colon = h.IndexOf(':');
            name = colon >= 0 ? h[..colon] : h;
        }
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
    }

    // 整数の引数 (0 以上の 10 進の数字だけ)。無ければ null。数字でなければ・min より小さければ 400。int に入らない大きな値は int の最大として扱う (桁あふれさせない)
    private static int? ParseCount(NameValueCollection qs, string key, int min, string error)
    {
        var v = qs[key];
        if (v == null) return null;
        var t = v.Trim();
        if (t.Length == 0 || !t.All(char.IsAsciiDigit)) throw new ApiError(400, error);
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) n = int.MaxValue;
        if (n < min) throw new ApiError(400, error);
        return n;
    }

    // 一覧の limit (1 以上。上限なし)
    private static int? ParseLimit(NameValueCollection qs) => ParseCount(qs, "limit", 1, "limit は 1 以上の整数を指定してください");

    // /api/search の limit (1〜1000)。無ければ null (既定 40)
    private static int? ParseSearchLimit(NameValueCollection qs)
    {
        const string error = "limit は 1〜1000 の整数を指定してください";
        var n = ParseCount(qs, "limit", 1, error);
        if (n > 1000) throw new ApiError(400, error);
        return n;
    }

    private static string ErrorJson(string message) => new JsonObject { ["error"] = message }.ToJsonString(ApiJsonOptions);

    // 応答を書いて閉じる。書いている間に相手が切断した・止めたなど、書けなかったときは Abort する (必ずどちらかで終える)
    private static async Task WriteApiResponseAsync(HttpListenerResponse response, int status, string contentType, string body, bool cors, CancellationToken ct)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.StatusCode = status;
            response.ContentType = contentType;
            response.ContentLength64 = bytes.Length;
            if (cors) AddCorsHeaders(response);
            await response.OutputStream.WriteAsync(bytes, ct);
            response.Close();
        }
        catch
        {
            try { response.Abort(); } catch { }
        }
    }

    private static Task WriteApiErrorAsync(HttpListenerResponse response, int status, string message, bool cors, CancellationToken ct) =>
        WriteApiResponseAsync(response, status, "application/json; charset=utf-8", ErrorJson(message), cors, ct);

    private Task<ApiResult> DispatchDataApiAsync(ApiQuery q, CancellationToken ct) => q.Path switch
    {
        "/api" => Task.FromResult(ApiIndex(q)),
        "/api/search" => Task.FromResult(ApiSearch(q)),
        "/api/detail" => ApiDetailAsync(q),
        "/api/ships" => Task.FromResult(ApiCategory(q, "船舶")),
        "/api/ship-weapons" => Task.FromResult(ApiCategory(q, "船舶武器")),
        "/api/personal-weapons" => Task.FromResult(ApiCategory(q, "個人武器")),
        "/api/ship-gear" => Task.FromResult(ApiCategory(q, "船舶装備")),
        "/api/personal-gear" => Task.FromResult(ApiCategory(q, "個人装備")),
        "/api/ores" => Task.FromResult(ApiCategory(q, "鉱石")),
        "/api/shops" => Task.FromResult(ApiTreeCategory(q, "お店")),
        "/api/stations" => Task.FromResult(ApiTreeCategory(q, "ステーション")),
        "/api/systems" => Task.FromResult(ApiTreeCategory(q, "星系")),
        "/api/trade/commodities" => Task.FromResult(ApiTradeCommodities(q)),
        "/api/trade/terminals" => Task.FromResult(ApiTradeTerminals(q)),
        "/api/trade/prices" => Task.FromResult(ApiTradePrices(q)),
        "/api/trade/routes" => Task.FromResult(ApiTradeRoutes(q)),
        "/api/missions/categories" => Task.FromResult(ApiMissionCategories(q)),
        "/api/missions" => Task.FromResult(ApiMissions(q)),
        "/api/missions/detail" => Task.FromResult(ApiMissionDetail(q)),
        "/api/blueprints" => Task.FromResult(ApiBlueprints(q)),
        "/api/starmap" => Task.FromResult(ApiStarmap(q)),
        "/api/my/ships" => Task.FromResult(ApiMyShips(q)),
        "/api/my/components" => Task.FromResult(ListResult(q, NewHangar().LoadMyComponentsReadOnly())),
        "/api/my/gear" => Task.FromResult(ListResult(q, NewHangar().LoadMyGearReadOnly())),
        "/api/my/blueprints" => Task.FromResult(ApiMyBlueprints(q)),
        "/api/my/hangar/pledges" => Task.FromResult(ListResult(q, NewHangar().LoadPledgesReadOnly())),
        "/api/my/hangar/ships" => Task.FromResult(ListResult(q, NewHangar().GetShipInstancesReadOnly())),
        "/api/my/hangar/ccus" => Task.FromResult(ListResult(q, NewHangar().LoadCcusReadOnly())),
        "/api/my/loadout" => ApiMyLoadoutAsync(q, ct),
        "/api/my/gamelog" => Task.FromResult(ApiMyGameLog(q)),
        "/api/my/mining" => Task.FromResult(ListResult(q, NewOre().LoadMiningReadOnly())),
        "/api/my/ore-stock" => Task.FromResult(ApiMyOreStock(q)),
        "/api/my/commodity-trades" => Task.FromResult(ListResult(q, NewOre().LoadTradesReadOnly())),
        _ => throw new ApiError(404, "そのパスの API はありません"),
    };

    // ===== 結果の組み立て =====

    // 一覧の JSON: { "total": 全体の件数, "count": 返した件数, "items": [...] }。items は limit・offset を通したもの。transform は items の JSON に通す
    private static JsonObject ListJson<T>(ApiQuery q, IEnumerable<T> rows, Action<JsonNode?>? transform = null)
    {
        var all = rows as IReadOnlyCollection<T> ?? rows.ToList();
        var items = q.Paginate(all);
        var node = ToNode(items);
        transform?.Invoke(node);
        return new JsonObject { ["total"] = all.Count, ["count"] = items.Count, ["items"] = node };
    }

    // 一覧: ListJson と追加の項目 (extra は先頭に置く)
    private static ApiResult ListResult<T>(ApiQuery q, IEnumerable<T> rows, JsonObject? extra = null, Action<JsonNode?>? transform = null)
    {
        var json = new JsonObject();
        if (extra != null)
            foreach (var (k, v) in extra.ToList()) { extra.Remove(k); json[k] = v; }
        var list = ListJson(q, rows, transform);
        foreach (var (k, v) in list.ToList()) { list.Remove(k); json[k] = v; }
        return new ApiResult(json, () => JsonPageHtml(q.Path, json));
    }

    // 詳細: { "record", "name", "text" } と追加の項目。HTML は DetailHtml.FromDetailText (リンクは /api/detail へ)。extraHtml は本文の後ろに足す HTML
    private static ApiResult DetailResult(string record, string name, string text, JsonObject? extra = null, Func<string>? extraHtml = null)
    {
        var json = new JsonObject { ["record"] = record, ["name"] = name, ["text"] = text };
        if (extra != null)
            foreach (var (k, v) in extra.ToList()) { extra.Remove(k); json[k] = v; }
        return new ApiResult(json, () =>
        {
            var page = RewriteDetailLinks(DetailHtml.FromDetailText(text));
            if (extraHtml == null) return page;
            // 本文の後ろ (MapScript の <script> の前) に足す
            var at = page.LastIndexOf("<script>", StringComparison.Ordinal);
            return at >= 0 ? page.Insert(at, extraHtml()) : page + extraHtml();
        });
    }

    // DetailHtml のリンク (href="scjt://detail/<URL エンコードした record>") を /api/detail?record=<record>&format=html にする
    private static readonly Regex DetailLinkHrefRegex = new("href=\"scjt://detail/([^\"]*)\"", RegexOptions.Compiled);

    private static string RewriteDetailLinks(string html) => DetailLinkHrefRegex.Replace(html, m =>
    {
        string record;
        try { record = Uri.UnescapeDataString(m.Groups[1].Value.TrimEnd('/')); }
        catch { record = m.Groups[1].Value; }
        return $"href=\"/api/detail?record={WebUtility.HtmlEncode(Uri.EscapeDataString(record))}&amp;format=html\"";
    });

    // JSON の結果を HTML のページにする: 文字・数の項目は「項目 | 内容」の表、配列は表 (列 = 要素の項目)、{ total, count, items } の項目は見出しと表
    private static string JsonPageHtml(string title, JsonObject json)
    {
        var sb = new StringBuilder();
        sb.Append("<h2>").Append(WebUtility.HtmlEncode(title)).Append("</h2>");
        var scalars = json.Where(kv => kv.Value is not JsonArray && !(kv.Value is JsonObject o && o["items"] is JsonArray)).ToList();
        if (scalars.Count > 0)
        {
            sb.Append("<table><tr><th>項目</th><th>内容</th></tr>");
            foreach (var (k, v) in scalars)
                sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(k)).Append("</td><td>").Append(WebUtility.HtmlEncode(CellText(v))).Append("</td></tr>");
            sb.Append("</table>");
        }
        foreach (var (k, v) in json)
        {
            if (v is JsonArray arr)
            {
                if (k != "items") sb.Append("<h3>").Append(WebUtility.HtmlEncode(k)).Append("</h3>");
                sb.Append(ArrayTableHtml(arr));
            }
            else if (v is JsonObject o && o["items"] is JsonArray items)
            {
                sb.Append("<h3>").Append(WebUtility.HtmlEncode($"{k} ({CellText(o["count"])} / {CellText(o["total"])} 件)")).Append("</h3>");
                sb.Append(ArrayTableHtml(items));
            }
        }
        return DetailHtml.Page(sb.ToString());
    }

    // 配列の表。要素がオブジェクトなら列は項目 (出てきた順)、そうでなければ 1 列 (値)。入れ子の値は JSON の文字のまま
    private static string ArrayTableHtml(JsonArray arr)
    {
        if (arr.Count == 0) return "<p>0 件</p>";
        var sb = new StringBuilder("<table>");
        if (arr.All(e => e is JsonObject))
        {
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in arr)
                foreach (var (k, _) in (JsonObject)e!)
                    if (seen.Add(k)) columns.Add(k);
            sb.Append("<tr>");
            foreach (var c in columns) sb.Append("<th>").Append(WebUtility.HtmlEncode(c)).Append("</th>");
            sb.Append("</tr>");
            foreach (var e in arr)
            {
                var o = (JsonObject)e!;
                sb.Append("<tr>");
                foreach (var c in columns)
                    sb.Append("<td>").Append(WebUtility.HtmlEncode(o.TryGetPropertyValue(c, out var v) ? CellText(v) : "")).Append("</td>");
                sb.Append("</tr>");
            }
        }
        else
        {
            sb.Append("<tr><th>値</th></tr>");
            foreach (var e in arr) sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(CellText(e))).Append("</td></tr>");
        }
        sb.Append("</table>");
        return sb.ToString();
    }

    // 表のセルの文字: 文字はそのまま、null は空、ほかは JSON の文字
    private static string CellText(JsonNode? v) => v switch
    {
        null => "",
        JsonValue jv when jv.GetValueKind() == JsonValueKind.String => jv.GetValue<string>(),
        _ => v.ToJsonString(ApiJsonOptions),
    };

    // ===== /api =====

    private static ApiResult ApiIndex(ApiQuery q) =>
        ListResult(q, DataApiRoutes.Select(r => new { path = r.Path, description = r.Description, parameters = r.Parameters, personal = r.Personal }),
            new JsonObject { ["formats"] = "format=json (既定) / format=html" });

    // ===== キャッシュ (DB ファイルの印 SqliteWal.FileStamp (-wal も見る) が変わらない間は使い回す) =====

    // お店・場所・星系 (gamedata_cache.db と trade_cache.db から作る)
    private readonly object _shopTreeLock = new();
    private (string Stamp, ShopTree? Tree)? _shopTreeCache;

    // 契約の読み口 (gamedata_cache.db と translations.db を読む)。使うのも _missionLock の中だけ (1 つの接続を複数のスレッドから同時に使わない)
    private readonly object _missionLock = new();
    private (string Stamp, MissionService Service)? _missionCache;

    private volatile bool _dataApiDisposed;

    // Dispose から呼ぶ。契約の読み口の接続を閉じる (使っている要求があれば終わってから。呼んだスレッド (UI) は待たない)。閉じた後は作り直さない
    private void DisposeDataApiCaches()
    {
        _dataApiDisposed = true;
        _ = Task.Run(() =>
        {
            lock (_missionLock)
            {
                try { _missionCache?.Service.Dispose(); } catch { }
                _missionCache = null;
            }
            lock (_shopTreeLock) _shopTreeCache = null;
        });
    }

    // ===== 詳細タブ =====

    // gamedata_cache.db を読み取り専用で開く。無ければ 503
    private static GameDataQueryService OpenGameData()
    {
        var db = DetailPanel.DbPath();
        if (!File.Exists(db)) throw new ApiError(503, "gamedata_cache.db がありません (設定タブの［インデックス構築］)");
        return GameDataQueryService.OpenReadOnly(db);
    }

    // お店・場所・星系 (DetailPanel.BuildShopTreeAsync と同じ作り方)。gamedata_cache.db が無ければ null。作れなければ例外 (覚えない)
    private ShopTree? BuildShopTree()
    {
        var db = DetailPanel.DbPath();
        var trade = DetailPanel.TradeDbPath();
        var stamp = SqliteWal.FileStamp(db) + "\n" + SqliteWal.FileStamp(trade);
        lock (_shopTreeLock)
        {
            if (_shopTreeCache is { } c && c.Stamp == stamp) return c.Tree;
            ShopTree? tree = null;
            if (File.Exists(db))
            {
                using var q = GameDataQueryService.OpenReadOnly(db);
                tree = q.BuildShopTree(trade);
            }
            _shopTreeCache = (stamp, tree);
            return tree;
        }
    }

    // 検索・リンクの候補に使う木 (作れなければ null。DetailPanel.GetShopTreeOrNullAsync と同じ)
    private ShopTree? BuildShopTreeOrNull()
    {
        try { return BuildShopTree(); }
        catch { return null; }
    }

    // 分類 (すべて) の検索 (DetailPanel.SearchAllAsync と同じ)。limit は SearchDetailCandidates の limit (既定 40)、offset は結果に通す
    private ApiResult ApiSearch(ApiQuery q)
    {
        var query = (q.Get("q") ?? "").Trim();
        var tree = BuildShopTreeOrNull();
        using var gd = OpenGameData();
        var found = query.Length == 0 ? new List<DetailCandidate>() : gd.SearchDetailCandidates(query, q.Limit ?? 40, tree);
        var items = found.Skip(q.Offset).ToList();
        var json = new JsonObject { ["total"] = found.Count, ["count"] = items.Count, ["items"] = ToNode(items) };
        return new ApiResult(json, () => JsonPageHtml(q.Path, json));
    }

    // 詳細 (DetailPanel.LoadDetailAsync と同じ)。保存データに販売場所が無ければ UEX の値段を取ってから出し直す。
    // 個人のデータを含めてよい要求 (q.Local) でなければ【ブループリント】の節の在庫・所持・手持ちを出さない
    private async Task<ApiResult> ApiDetailAsync(ApiQuery q)
    {
        var record = q.Required("record");
        var db = DetailPanel.DbPath();
        var isTreeRecord = ShopTree.IsTreeRecord(record);
        // お店・場所・星系 (その詳細と、販売場所のリンクに使う)。品目の詳細では、木を作れなくても詳細はリンク無しで出す
        ShopTree? tree;
        try { tree = BuildShopTree(); }
        catch when (!isTreeRecord) { tree = null; }
        if (!File.Exists(db)) throw new ApiError(503, "gamedata_cache.db がありません (設定タブの［インデックス構築］)");

        string? text;
        string itemType = "";
        string name;
        using (var gd = GameDataQueryService.OpenReadOnly(db))
        {
            if (isTreeRecord) text = tree != null ? GameDataQueryService.ShopTreeDetailText(tree, record) : null;
            else
            {
                text = gd.GetItemDetailText(record, shopTree: tree, includePersonal: q.Local);
                itemType = gd.ItemTypeOf(record);
            }
            name = gd.CandidateOf(record, tree)?.Name ?? "";
        }
        if (text == null) throw new ApiError(404, isTreeRecord ? "お店・場所が見つかりませんでした。" : "品目が見つかりませんでした。");

        // 保存データに購入の売り場が無ければ、UEX の値段 (チャット・表と同じ取得・キャッシュ) を取ってから出し直す
        if (text.Contains("店の販売データ無し"))
        {
            var ids = GameDataQueryService.UexCategoryIdsForItemType(itemType);
            if (ids.Length > 0)
            {
                try
                {
                    var uex = await ChatService.GetSharedUexItemPricesAsync(ids);
                    if (uex.Count > 0)
                    {
                        using var gd = GameDataQueryService.OpenReadOnly(db);
                        var again = gd.GetItemDetailText(record, uex, tree, includePersonal: q.Local);
                        if (again != null) text = again;
                    }
                }
                catch { }   // UEX が取れなくても保存データの詳細のまま
            }
        }
        return DetailResult(record, name, text);
    }

    // 分類の絞り込み (DetailPanel.RefreshListAsync と同じ): 種類は一致、サイズは S<n> と一致、名前は部分一致 (大文字小文字無視)。
    // 前後の空白は除き、空・「(すべて)」は絞り込まない。sizes が false の分類 (画面でサイズを選べない分類) で size を付けたら 400
    private static List<T> FilterCategory<T>(ApiQuery q, IEnumerable<T> rows, Func<T, string> kind, Func<T, int> size, Func<T, string> name, bool sizes)
    {
        const string any = "(すべて)";
        var k = q.Filter("kind");
        var s = q.Filter("size");
        var n = q.Filter("name") ?? "";
        if (k == any) k = null;
        if (s == any) s = null;
        string? sizeKey = null;
        if (s != null)
        {
            if (!sizes) throw new ApiError(400, "この分類はサイズで絞り込めません");
            var t = s;
            if (t.StartsWith('S') || t.StartsWith('s')) t = t[1..];
            if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var sz)) throw new ApiError(400, "size は 3 か S3 の形で指定してください");
            sizeKey = $"S{sz}";
        }
        return rows.Where(r =>
                (k == null || kind(r) == k)
                && (sizeKey == null || $"S{size(r)}" == sizeKey)
                && (n.Length == 0 || name(r).Contains(n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    // 船舶・船舶武器・個人武器・船舶装備・個人装備・鉱石 (DetailPanel.LoadCategoryAsync と同じ読み方)
    private static ApiResult ApiCategory(ApiQuery q, string category)
    {
        using var gd = OpenGameData();
        // 鉱石は resource_types から作る (item_index は使わない)
        if (category == "鉱石")
        {
            var ores = gd.GetOreCategoryResources().Select(r => new OreCategoryRow(r.Record, r.Name, r.Kind));
            return ListResult(q, FilterCategory(q, ores, r => r.Kind, _ => 0, r => r.Name, sizes: false));
        }
        if (!gd.HasItemIndex()) throw new ApiError(503, "インデックスが未構築です (設定タブの［インデックス構築］)");
        return category switch
        {
            "船舶" => ListResult(q, FilterCategory(q, gd.GetDetailShipList(), r => r.Kind, r => r.Size, r => r.Name, sizes: true)),
            "船舶武器" => ListResult(q, FilterCategory(q, gd.GetShipWeaponTable(), r => r.Kind, r => r.Size, r => r.Name, sizes: true)),
            "個人武器" => ListResult(q, FilterCategory(q, gd.GetPersonalWeaponTable(), r => r.Kind, r => r.Size, r => r.Name, sizes: false)),
            "船舶装備" => ListResult(q, FilterCategory(q, gd.GetShipGearTable(), r => r.Kind, r => r.Size, r => r.Name, sizes: true)),
            // 個人装備の種類は、アーマー・服は部位まで (例 アーマー (ヘルメット))、それ以外は表の種類 (画面と同じ)
            "個人装備" => ListResult(q, FilterCategory(q, gd.GetPersonalGearTable(), r => r.Part.Length > 0 ? $"{r.Kind} ({r.Part})" : r.Kind, _ => 0, r => r.Name, sizes: false)),
            _ => throw new ApiError(404, "分類がありません"),
        };
    }

    // GetOreCategoryResources の 1 行 (タプルの名前のまま出すため)
    private sealed record OreCategoryRow(string Record, string Name, string Kind);

    // お店・ステーション・星系の 1 行 (DetailPanel.TreeCategoryRows の行と同じ中身: record・名前・種類・表示)
    private sealed record TreeCategoryRow(string Record, string Name, string Kind, string Display);

    // お店・ステーション・星系 (DetailPanel.TreeCategoryRows と同じ規則・同じ並び)
    private ApiResult ApiTreeCategory(ApiQuery q, string category)
    {
        var tree = BuildShopTree();
        if (tree == null || tree.IsEmpty) throw new ApiError(503, "インデックスが未構築です (設定タブの［インデックス構築］)");
        IEnumerable<TreeCategoryRow> rows = category switch
        {
            "お店" => tree.Shops.Select(s => new TreeCategoryRow(s.Record, s.Name, s.Kind, $"{s.Name} ({s.Location.Name})")),
            "ステーション" => tree.Places.Where(p => ShopTree.StationTypes.Contains(p.Type) || p.Shops.Count > 0)
                .Select(p => new TreeCategoryRow(p.Record, p.Name, p.TypeLabel, p.Parent != null ? $"{p.Name} ({p.Parent.Name})" : p.Name)),
            "星系" => tree.Systems.Select(s => new TreeCategoryRow(s.Record, s.System, "", s.System)),
            _ => Enumerable.Empty<TreeCategoryRow>(),
        };
        var sorted = rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase).ToList();
        return ListResult(q, FilterCategory(q, sorted, r => r.Kind, _ => 0, r => r.Name, sizes: false));
    }

    // ===== 星図 =====

    // 星図の場所 1 つ (ShopTreeNode の親子の参照は循環するので、親は record と名前で出す。座標は X / Y / Z)
    private sealed record StarmapRow(string Record, string RecordName, string Name, string Type, string TypeLabel, string System, bool IsStar,
        string ParentRecord, string ParentName, int ShopCount, double? X, double? Y, double? Z, double? Radius);

    private ApiResult ApiStarmap(ApiQuery q)
    {
        var tree = BuildShopTree();
        if (tree == null || tree.IsEmpty) throw new ApiError(503, "インデックスが未構築です (設定タブの［インデックス構築］)");
        var rows = tree.Systems.OrderBy(n => n.System, StringComparer.OrdinalIgnoreCase)
            .Concat(tree.Places.OrderBy(n => n.System, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(n => n.RecordName, StringComparer.Ordinal))
            .Select(n => new StarmapRow(n.Record, n.RecordName, n.Name, n.Type, n.TypeLabel, n.System, n.IsStar,
                n.ParentRecord, n.Parent?.Name ?? "", n.Shops.Count, n.Position?.X, n.Position?.Y, n.Position?.Z, n.Radius));
        return ListResult(q, rows);
    }

    // ===== 交易 =====

    // 価格を読み込み済みの TradeService。渡されていない・まだ価格が無ければ 503
    private TradeService RequireTrade()
    {
        var t = _tradeService;
        if (t == null || !t.HasPriceData)
            throw new ApiError(503, t?.IsFetching == true ? "交易の価格を取得中です" : "交易の価格がまだ読み込まれていません (交易タブの［価格更新］)");
        return t;
    }

    private ApiResult ApiTradeCommodities(ApiQuery q) =>
        ListResult(q, RequireTrade().GetCommodities().Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));

    private ApiResult ApiTradeTerminals(ApiQuery q) =>
        ListResult(q, RequireTrade().GetTerminals().Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase));

    // 端末で買える物・売れる物 (GetBuyableAtLocation / GetSellableAtLocation)。limit・offset はそれぞれに通す
    private ApiResult ApiTradePrices(ApiQuery q)
    {
        var trade = RequireTrade();
        var terminal = q.Required("terminal");
        var json = new JsonObject
        {
            ["terminal"] = terminal,
            ["buy"] = ListJson(q, trade.GetBuyableAtLocation(terminal)),
            ["sell"] = ListJson(q, trade.GetSellableAtLocation(terminal)),
        };
        return new ApiResult(json, () => JsonPageHtml(q.Path, json));
    }

    // ルート検索 (CalculateBestRoutes)。画面と同じく予算は ParseSuffixedNumber で読み (1M・500k 等)、予算・積載量は 0 より大きい値が必要
    // (予算が Infinity・NaN・負の数・0 なら 400)。星系の既定は「全て」、件数の既定は画面と同じ 20。
    // 商品名の表示 (CommodityDisplayName) は画面と同じ CommodityLabel で付ける (MainWindow の画面のルート検索と同じ)
    private ApiResult ApiTradeRoutes(ApiQuery q)
    {
        var trade = RequireTrade();
        var budget = MainWindow.ParseSuffixedNumber(q.Required("budget"));
        if (double.IsNaN(budget) || double.IsInfinity(budget) || !(budget > 0))
            throw new ApiError(400, "budget は 0 より大きい数を指定してください (例: 1000000, 1M, 500k, 3.5m)");
        if (!int.TryParse(q.Required("scu"), NumberStyles.None, CultureInfo.InvariantCulture, out var scu) || scu <= 0)
            throw new ApiError(400, "scu は正の整数を指定してください");
        var buySystem = q.Filter("buySystem") ?? "全て";
        var sellSystem = q.Filter("sellSystem") ?? "全て";
        var excludeOutposts = ParseBool(q.Get("excludeOutposts"), "excludeOutposts");
        var loadingDockOnly = ParseBool(q.Get("loadingDockOnly"), "loadingDockOnly");
        var excludeLowStock = ParseBool(q.Get("excludeLowStock"), "excludeLowStock");
        HashSet<string>? commodities = null;
        var c = q.Get("commodities");
        if (!string.IsNullOrWhiteSpace(c))
            commodities = c.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToHashSet();
        var top = 20;
        if (q.Get("top") is { } topText && (!int.TryParse(topText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out top) || top <= 0))
            throw new ApiError(400, "top は正の整数を指定してください");
        var routes = trade.CalculateBestRoutes(budget, scu, buySystem, sellSystem, excludeOutposts, loadingDockOnly, excludeLowStock, commodities, topN: top);
        if (_commodityLabeler is { } labeler && routes.Count > 0)
        {
            var labels = labeler(routes.Select(r => r.CommodityName).ToList());
            for (int i = 0; i < routes.Count && i < labels.Count; i++)
                routes[i].CommodityDisplayName = labels[i];
        }
        return ListResult(q, routes);
    }

    // true/false (1/0)。無い・空なら false。それ以外は 400
    private static bool ParseBool(string? value, string key) => (value ?? "").Trim() switch
    {
        "" or "false" or "0" => false,
        "true" or "1" => true,
        _ => throw new ApiError(400, $"{key} は true か false を指定してください"),
    };

    // ===== 契約 =====

    // 契約の読み口 (画面と同じく gamedata_cache.db と translations.db) で work を行う。gamedata_cache.db が無ければ 503。
    // 読み口は 2 つの DB の印が変わらない間は使い回し、work は _missionLock の中で行う
    private T WithMissions<T>(Func<MissionService, T> work)
    {
        var db = DetailPanel.DbPath();
        if (!File.Exists(db)) throw new ApiError(503, "インデックス未構築 (gamedata_cache.db がありません)");
        var translations = Path.Combine(DetailPanel.WorkDir(), "translations.db");
        var stamp = SqliteWal.FileStamp(db) + "\n" + SqliteWal.FileStamp(translations);
        lock (_missionLock)
        {
            if (_dataApiDisposed) throw new OperationCanceledException();   // サーバーを止めた後は読み口を作り直さない
            if (_missionCache is not { } c || c.Stamp != stamp)
            {
                try { _missionCache?.Service.Dispose(); } catch { }
                _missionCache = null;
                _missionCache = (stamp, new MissionService(db, translations));
            }
            return work(_missionCache.Value.Service);
        }
    }

    private ApiResult ApiMissionCategories(ApiQuery q) => WithMissions(svc => ListResult(q, svc.GetCategories()));

    // category か q のどちらか一方 (q が空なら全部。画面の全件と同じ Search(""))。category は前後の空白を除き、空なら絞り込まない
    private ApiResult ApiMissions(ApiQuery q)
    {
        var categoryRaw = q.Get("category");
        var query = q.Get("q");
        if (categoryRaw == null && query == null) throw new ApiError(400, "category か q のどちらか一方を指定してください");
        var category = q.Filter("category");
        if (category != null && query != null) throw new ApiError(400, "category か q のどちらか一方を指定してください");
        return WithMissions(svc => ListResult(q, category != null ? svc.GetMissions(category) : svc.Search((query ?? "").Trim())));
    }

    // 契約の詳細 (FormatDetail) と、Wikelo の契約なら納品物 (GetDeliverables) とその入手場所 (FormatDeliverableSources。個人のデータを含めてよい要求でなければ BP の在庫・所持・手持ちを出さない)
    private ApiResult ApiMissionDetail(ApiQuery q)
    {
        var record = q.Required("record");
        return WithMissions(svc =>
        {
            var m = svc.FindByRecord([record]) ?? throw new ApiError(404, "契約が見つかりませんでした。");
            var text = svc.FormatDetail(m);
            var deliverables = MissionService.IsWikeloContract(m) ? svc.GetDeliverables(m) : new List<MissionService.DeliverableItem>();
            var withSources = deliverables.Select(d => new { item = d, sources = svc.FormatDeliverableSources(d, q.Local) }).ToList();
            var name = m.ListNameJa.Length > 0 ? m.ListNameJa : m.ListNameEn;
            var deliverableNode = ToNode(withSources);
            var extra = new JsonObject { ["deliverables"] = deliverableNode };
            return DetailResult(m.RecordName, name, text, extra, () =>
            {
                if (withSources.Count == 0) return "";
                var sb = new StringBuilder("<h3>納品物</h3><table><tr><th>名前</th><th>数</th><th>入手場所</th></tr>");
                foreach (var x in withSources)
                    sb.Append("<tr><td>").Append(WebUtility.HtmlEncode(x.item.Name)).Append("</td><td>").Append(WebUtility.HtmlEncode(x.item.Amount))
                      .Append("</td><td>").Append(WebUtility.HtmlEncode(x.sources).Replace("\r\n", "\n").Replace("\n", "<br>")).Append("</td></tr>");
                sb.Append("</table>");
                return sb.ToString();
            });
        });
    }

    // ===== BP =====

    // record 無し: ［BP］タブの表の全部 (BlueprintPanel.LoadData と同じ読み方。JSON は { total, count, items: [BlueprintItem] })。
    // record 付き: できる品の BP (BlueprintPanel.LoadForOutput。［詳細］の「ブループリント」の節と同じ読み方)。
    //   JSON は { record, total, count, items: [{ item, stockText, haves }], text }、HTML は節の文字 (BlueprintDetail.ItemSectionText の形) を DetailHtml で出す。
    //   その record の品目が無ければ 404 (品目はあるが BP が無ければ 0 件)
    // 個人のデータを含めてよい要求 (q.Local) でなければ在庫・所持・手持ち (BlueprintPersonalNames) を読まず、項目も出さない
    private static ApiResult ApiBlueprints(ApiQuery q)
    {
        var record = q.Filter("record");
        var workDir = DetailPanel.WorkDir();
        if (!File.Exists(DetailPanel.DbPath())) throw new ApiError(503, "gamedata_cache.db がありません (設定タブの［インデックス構築］)");
        Action<JsonNode?>? strip = q.Local ? null : node => RemoveProperties(node, BlueprintPersonalNames);
        if (record == null)
        {
            var res = BlueprintPanel.LoadData(workDir, includePersonal: q.Local);
            if (res.Message != null) throw new ApiError(503, res.Message);
            return ListResult(q, res.Items, transform: strip);
        }

        var rows = BlueprintPanel.LoadForOutput(workDir, record, q.Local);
        if (rows.Count == 0)
        {
            using var gd = OpenGameData();
            if (gd.ItemTypeOf(record).Length == 0 && gd.CandidateOf(record, null) == null)
                throw new ApiError(404, "品目が見つかりませんでした。");
        }
        var text = BlueprintDetail.ItemSectionText(rows);
        var list = ListJson(q, rows.Select(r => new { item = r.Item, stockText = r.StockText, haves = r.Haves }).ToList(), strip);
        var json = new JsonObject { ["record"] = record };
        foreach (var (k, v) in list.ToList()) { list.Remove(k); json[k] = v; }
        json["text"] = text;
        return new ApiResult(json, () => RewriteDetailLinks(DetailHtml.FromDetailText(text)));
    }

    // ===== 個人 =====

    // 個人のデータの読み口。UI のスレッドで使う MainWindow の HangarService とは別の口を作って読む
    // (MainWindow.LoadGameLogEntries と同じやり方。HangarService の覚えは UI のスレッド用なので、裏のスレッドから共有しない)。読むのは …ReadOnly のメソッドだけ
    private static HangarService NewHangar()
    {
        var h = new HangarService();
        h.SetCacheDir(DetailPanel.WorkDir());
        return h;
    }

    private static OreService NewOre()
    {
        var o = new OreService();
        o.SetCacheDir(DetailPanel.WorkDir());
        return o;
    }

    // 所持船 (TradeService.MyShips。MainWindow がメモリに持つもの)
    private ApiResult ApiMyShips(ApiQuery q)
    {
        var t = _tradeService ?? throw new ApiError(503, "所持船がまだ読み込まれていません");
        return ListResult(q, t.MyShips.ToList());
    }

    // 所持している BP の 1 件: uuid と BP の名前 (［BP］タブのできる品の名前と同じ出し方。BP の表に無ければ "")
    private sealed record MyBlueprintRow(string Uuid, string Name);

    private static ApiResult ApiMyBlueprints(ApiQuery q)
    {
        var workDir = DetailPanel.WorkDir();
        var set = HangarService.LoadMyBlueprintUuidsReadOnly(DetailPanel.TradeDbPath()) ?? throw new ApiError(503, "trade_cache.db がありません");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (set.Count > 0 && File.Exists(DetailPanel.DbPath()))
            foreach (var it in BlueprintPanel.LoadData(workDir, includePersonal: false).Items) names.TryAdd(it.Uuid, it.Name);
        return ListResult(q, set.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).Select(u => new MyBlueprintRow(u, names.TryGetValue(u, out var n) ? n : "")));
    }

    private static ApiResult ApiMyOreStock(ApiQuery q)
    {
        var stock = OreService.LoadStockReadOnly(DetailPanel.TradeDbPath()) ?? throw new ApiError(503, "trade_cache.db がありません");
        return ListResult(q, stock);
    }

    private static ApiResult ApiMyGameLog(ApiQuery q)
    {
        var rows = NewHangar().LoadGameLogEntriesReadOnly(out var broken);
        return ListResult(q, rows, new JsonObject { ["broken"] = broken });
    }

    // /api/my/loadout の読み込みの結果
    private sealed class LoadoutApiLoad
    {
        public string Name = "";
        public string Record = "";
        public List<LoadoutRow> Rows = new();
    }

    // 保有船の今の装備と付け替えの候補 (［装備］の表と同じ読み方: 船の record を ResolveShipRecordName で決め、GetShipLoadout (throwIfBusy: true) の行を
    // MakeLoadoutRow で表の行にする。候補は所持コンポーネント)。DB が使用中なら画面と同じ RunEquipBgAsync で間を置いて読み直す。
    // ship は保有船の InstanceKey (GetShipInstances) か「my|所持船の Id」(所持船だけの船。メーカーは Ship Matrix から)
    private async Task<ApiResult> ApiMyLoadoutAsync(ApiQuery q, CancellationToken ct)
    {
        var ship = q.Required("ship");
        var workDir = DetailPanel.WorkDir();
        var gamedata = DetailPanel.DbPath();
        if (!File.Exists(gamedata)) throw new ApiError(503, "gamedata_cache.db がありません");
        string? myName = null;
        if (ship.StartsWith("my|", StringComparison.Ordinal))
        {
            var t = _tradeService ?? throw new ApiError(503, "所持船がまだ読み込まれていません");
            if (!int.TryParse(ship[3..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) throw new ApiError(400, "ship の my| の後ろは所持船の Id です");
            var my = t.MyShips.FirstOrDefault(m => m.Id == id) ?? throw new ApiError(404, "所持船が見つかりませんでした。");
            myName = my.Name;
        }

        // 画面の ReadLoadoutRows / RunBgWithHangar と同じ: 船の record の解決 (ResolveShipRecordName) は失敗を中で受けて OnProgress に出すので、
        // その文が使用中 (SQLite Error 5 / 6) なら投げる (待って読み直す)。ほかの知らせはログに出す
        LoadoutApiLoad Work()
        {
            var h = new HangarService();
            h.SetCacheDir(workDir);
            string? busy = null;
            void OnProgress(string m)
            {
                if (m.Contains("SQLite Error 5:") || m.Contains("SQLite Error 6:")) busy = m;
                else App.AppendStartupLog(workDir, $"[Hangar] {m}");
            }
            h.OnProgress += OnProgress;
            try
            {
                string name;
                string? manufacturer;
                if (myName != null)
                {
                    name = myName;
                    manufacturer = h.ResolveShipReadOnly(name)?.ManufacturerName;
                }
                else
                {
                    var inst = h.GetShipInstancesReadOnly().FirstOrDefault(s => s.InstanceKey.Equals(ship, StringComparison.Ordinal))
                               ?? throw new ApiError(404, "保有船が見つかりませんでした。");
                    name = inst.Name;
                    manufacturer = inst.Matrix?.ManufacturerName;
                }
                var record = h.ResolveShipRecordNameReadOnly(name, manufacturer);
                if (busy != null) throw SqliteReadContext.NewBusyException(busy);   // 使用中で読めなかった DB を付ける
                if (record == null) throw new ApiError(404, $"「{name}」の装備データ (ships.record_name) を解決できません");
                var mine = h.LoadMyComponentsReadOnly();
                using var equip = EquipmentService.OpenReadOnly(gamedata, throwIfBusy: true);   // 画面の裏の口 (BgEquip) と同じ開き方
                ct.ThrowIfCancellationRequested();
                var rows = h.GetShipLoadoutReadOnly(ship, record, equip, throwIfBusy: true)
                    .Select(p => MainWindow.MakeLoadoutRow(p, record, mine, equip))
                    .ToList();
                if (busy != null) throw SqliteReadContext.NewBusyException(busy);
                return new LoadoutApiLoad { Name = name, Record = record, Rows = rows };
            }
            finally { h.OnProgress -= OnProgress; }
        }

        var res = await MainWindow.RunEquipBgAsync(Work, new SqliteReadContext(), () => !ct.IsCancellationRequested, _ => { }, ct)
                  ?? throw new OperationCanceledException(ct);
        return ListResult(q, res.Rows, new JsonObject { ["ship"] = ship, ["name"] = res.Name, ["record"] = res.Record });
    }
}
