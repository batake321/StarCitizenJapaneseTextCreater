using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// === Models ===

public enum HangarInsurance { Unknown, Lti, Months120, Months6 }

// 保有船に対する 3 値マーク (仕様 §08)。None は未設定
public enum HangarShipMark { None, Keep, Drop, Hold }

public class HangarItem
{
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "";          // Ship / Insurance / Skin / Component / Credits / FPS Equipment / Hangar decoration / GamePackage / Unknown
    public string Manufacturer { get; set; } = "";
}

// script.js-pledge-nameable-ships の 1 要素
public class HangarNameableShip
{
    public long MembershipId { get; set; }
    public string DefaultName { get; set; } = "";
    public string? CustomName { get; set; }
}

public class HangarPledge
{
    public string Id { get; set; } = "";            // js-pledge-id
    public string Name { get; set; } = "";          // 表示用のみ。中身の判定に使わない
    public long ValueCents { get; set; }            // melt して得られる Store Credit
    public long ConfigValueCents { get; set; }      // js-pledge-configuration-value
    public string Currency { get; set; } = "";
    public bool Meltable { get; set; } = true;      // !js-pledge-not-buybackable
    public bool NeedsTicket => ValueCents > 100_000; // $1,000 超はサポートチケットが必要
    public bool Upgraded { get; set; }              // .availability に "upgraded"
    public HangarInsurance Insurance { get; set; } = HangarInsurance.Unknown;
    public string CreatedAt { get; set; } = "";
    public string FetchedAt { get; set; } = "";
    public List<HangarItem> Items { get; set; } = new();
    public List<HangarNameableShip> NameableShips { get; set; } = new();

    public List<string> ShipNames =>
        Items.Where(i => i.Kind.Equals("Ship", StringComparison.OrdinalIgnoreCase))
             .Select(i => i.Title).ToList();

    public string ValueDisplay => $"${ValueCents / 100.0:N2}";
    public string InsuranceDisplay => Insurance switch
    {
        HangarInsurance.Lti => "LTI",
        HangarInsurance.Months120 => "120ヶ月",
        HangarInsurance.Months6 => "6ヶ月",
        _ => "",
    };
}

public class HangarCcu
{
    public string PledgeId { get; set; } = "";
    public string FromShip { get; set; } = "";
    public string ToShip { get; set; } = "";
    public bool Warbond { get; set; }
    public long PriceCents { get; set; }            // 実際に支払った額
    public long StdPriceCents { get; set; }         // 標準CCU額 (値引き率の分母)。0 なら率を出さない
    public string Source { get; set; } = "sync";    // "sync" | "manual"
    public string Notes { get; set; } = "";

    public string PriceDisplay => $"${PriceCents / 100.0:N2}";
    public string RouteDisplay => $"{FromShip} → {ToShip}";
    // 値引き率。StdPriceCents が 0 以下、または割引が無い場合は空文字
    public string DiscountDisplay =>
        StdPriceCents > 0 && PriceCents < StdPriceCents
            ? $"-{(StdPriceCents - PriceCents) * 100.0 / StdPriceCents:N0}%"
            : "";
}

public class HangarShipInstance
{
    public string PledgeId { get; set; } = "";
    public string PledgeName { get; set; } = "";
    public string Name { get; set; } = "";
    public HangarInsurance Insurance { get; set; } = HangarInsurance.Unknown;
    public ShipMatrixEntry? Matrix { get; set; }    // Ship Matrix で名寄せできた場合のみ
    public bool IsUnresolved => Matrix == null;
    public HangarShipMark Mark { get; set; } = HangarShipMark.None;

    public string InsuranceDisplay => Insurance switch
    {
        HangarInsurance.Lti => "LTI",
        HangarInsurance.Months120 => "120ヶ月",
        HangarInsurance.Months6 => "6ヶ月",
        _ => "",
    };
}

// RSI Ship Matrix (/ship-matrix/index) の 1 件
public class ShipMatrixEntry
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string ManufacturerCode { get; set; } = "";
    public string ManufacturerName { get; set; } = "";
    public string Focus { get; set; } = "";
    public string Type { get; set; } = "";
    public string Size { get; set; } = "";          // API の size は "Small"/"Large" 等の文字列
    public string ProductionStatus { get; set; } = "";
    public string ProductionNote { get; set; } = "";
}

public class HangarTotals
{
    public int PledgeCount { get; set; }
    public int ShipCount { get; set; }
    public int CcuCount { get; set; }
    public long TotalMeltValueCents { get; set; }   // Σ hangar_pledges.value_cents
    public long MeltableValueCents { get; set; }    // meltable=1 のみ
    public int? BuybackTokens { get; set; }
    public long? StoreCreditCents { get; set; }
    public string? FetchedAt { get; set; }
    public string? ShipMatrixFetchedAt { get; set; }
}

// === CCU プランナー ===

public class CcuPlanApply
{
    public List<string> CcuIds { get; set; } = new();
    public string TargetPledgeId { get; set; } = "";
    public string FromShip { get; set; } = "";
    public string ToShip { get; set; } = "";
    public long TotalCostCents { get; set; }
    public HangarInsurance ResultInsurance { get; set; } = HangarInsurance.Unknown;
    public string Rationale { get; set; } = "";
}

public class CcuPlanMelt
{
    public string CcuId { get; set; } = "";
    public string Reason { get; set; } = "";
    public long ValueCents { get; set; }
}

public class CcuPlanUnusable
{
    public string CcuId { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class CcuPlan
{
    public List<CcuPlanApply> Applies { get; set; } = new();
    public List<CcuPlanMelt> Melts { get; set; } = new();
    public List<CcuPlanUnusable> Unusable { get; set; } = new();
    public bool TimedOut { get; set; }
}

// === melt シミュレーション ===

public class MeltSimInput
{
    public List<string> MeltPledgeIds { get; set; } = new();
    public List<string> MeltCcuIds { get; set; } = new();
    public long TargetPriceCents { get; set; }
    public double TaxRate { get; set; } = 0.10;
}

public class MeltSimResult
{
    public long CreditCents { get; set; }
    public long CashCents { get; set; }
    public List<string> LostShips { get; set; } = new();
    public bool LosesLti { get; set; }
    public List<string> NeedsTicketPledges { get; set; } = new();
    public List<string> BlockedCcuIds { get; set; } = new();
    public List<string> NotMeltablePledges { get; set; } = new();
}

// === Selectors ===

// DOM セレクタ定義。実行ファイルと同じディレクトリの hangar_selectors.json で上書きできる。
// 既定値はファイルが無くても動くように JSON と同じ内容を持たせてある。
public class HangarSelectors
{
    public string PledgeRoot { get; set; } = ".list-items li";
    public string PledgeId { get; set; } = ".js-pledge-id";
    public string PledgeName { get; set; } = ".js-pledge-name";
    public string PledgeValue { get; set; } = ".js-pledge-value";
    public string PledgeCurrency { get; set; } = ".js-pledge-currency";
    public string PledgeConfigValue { get; set; } = ".js-pledge-configuration-value";
    public string PledgeNotBuybackable { get; set; } = ".js-pledge-not-buybackable";
    public string Availability { get; set; } = ".availability";
    public string DateCol { get; set; } = ".date-col";
    public string NameableShips { get; set; } = "script.js-pledge-nameable-ships";
    public string ItemRoot { get; set; } = ".item";
    public string ItemTitle { get; set; } = ".title";
    public string ItemKind { get; set; } = ".kind";
    public string ItemLiner { get; set; } = ".liner";
    // 以下 2 つは未確定 (空文字)。空なら innerText への正規表現フォールバックを使う
    public string BuybackTokens { get; set; } = "";
    public string StoreCredit { get; set; } = "";

    // RSI ストア (GraphQL) の取得先と、取得対象カテゴリ (ストアのカテゴリ id → 表示名)
    public string StoreGraphqlUrl { get; set; } = "https://robertsspaceindustries.com/graphql";
    public Dictionary<string, string> StoreCategories { get; set; } = new()
    {
        ["72"] = "Standalone Ships",
        ["45"] = "Packages",
        ["241"] = "Upgrades",
        ["270"] = "Packs",
    };
}

// RSI ストアの販売中 SKU 1 件 (GetBrowseListingQuery の TySku)
public class StoreSku
{
    public string Id { get; set; } = "";
    public string ProductId { get; set; } = "";       // ストアのカテゴリ id ("72" 等)
    public string Category { get; set; } = "";        // カテゴリ表示名 ("Standalone Ships" 等)
    public string Name { get; set; } = "";            // 船名 (例 "Avenger Titan")
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string SkuType { get; set; } = "";
    public bool IsWarbond { get; set; }               // isWarbond || url が "-Warbond" で終わる
    public long NativePriceCents { get; set; }        // 税抜 USD セント (表示・計算の正)
    public long PriceCents { get; set; }              // 税込 (参考)
    public string TaxDescription { get; set; } = "";
    public string StockLevel { get; set; } = "";
    public bool Available { get; set; }
    public string Thumbnail { get; set; } = "";
    public string FetchedAt { get; set; } = "";

    public string NativePriceDisplay => $"${NativePriceCents / 100.0:N2}";
}

public class HangarService
{
    private string? _dbPath;

    public event Action<string>? OnProgress;

    public void SetCacheDir(string dir) => _dbPath = Path.Combine(dir, "trade_cache.db");

    // === Selector Loading ===

    private const string SelectorsFileName = "hangar_selectors.json";
    private const string AliasesFileName = "hangar_ship_aliases.json";
    private const string PricesFileName = "hangar_ship_prices.json";

    // hangar_selectors.json を読む。無い/壊れている場合は既定値を返す。
    // static のため OnProgress は使えないので Console.WriteLine で警告する。
    public static HangarSelectors LoadSelectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, SelectorsFileName);
        try
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Hangar] {SelectorsFileName} が見つかりません。既定のセレクタを使用します: {path}");
                return new HangarSelectors();
            }
            var json = File.ReadAllText(path);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var sel = JsonSerializer.Deserialize<HangarSelectors>(json, opts);
            if (sel == null)
            {
                Console.WriteLine($"[Hangar] {SelectorsFileName} の内容が空です。既定のセレクタを使用します。");
                return new HangarSelectors();
            }
            return sel;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Hangar] {SelectorsFileName} の読込に失敗しました({ex.Message})。既定のセレクタを使用します。");
            return new HangarSelectors();
        }
    }

    // === Extraction JSON -> Model ===

    // 必須セレクタ。1 つでも空なら pledge 単位で失敗扱いにする
    private static readonly string[] RequiredPledgeFields =
        ["id", "name", "value", "currency", "notBuybackable", "availability", "date"];

    // 抽出 JSON をパースして pledge のリストにする。
    // 失敗時は例外を投げる (呼び出し側でエラー表示する)
    public List<HangarPledge> ParseExtractionJson(string json, string fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("pledges", out var pledgesEl) || pledgesEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("pledge が 0 件でした。ページ構造が変わった可能性があります。");

        var result = new List<HangarPledge>();
        foreach (var p in pledgesEl.EnumerateArray())
        {
            var name = GetStr(p, "name");
            var id = GetStr(p, "id").Trim();

            var missing = RequiredPledgeFields.Where(f => string.IsNullOrEmpty(GetStr(p, f).Trim())).ToList();
            if (missing.Count > 0)
            {
                var label = $"id={(string.IsNullOrEmpty(id) ? "(不明)" : id)}, name={(string.IsNullOrEmpty(name) ? "(不明)" : name)}";
                throw new InvalidOperationException(
                    $"pledge の必須項目が取得できませんでした ({label}): {string.Join(", ", missing)}。ページ構造が変わった可能性があります。");
            }

            var pledge = new HangarPledge
            {
                Id = id,
                Name = name,
                ValueCents = ParseMoneyCents(GetStr(p, "value")),
                ConfigValueCents = ParseMoneyCents(GetStr(p, "configValue")),
                Currency = GetStr(p, "currency"),
                Meltable = GetStr(p, "notBuybackable").Trim() != "1",
                Upgraded = GetStr(p, "availability").Contains("upgraded", StringComparison.OrdinalIgnoreCase),
                CreatedAt = GetStr(p, "date"),
                FetchedAt = fetchedAt,
            };

            if (p.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in itemsEl.EnumerateArray())
                {
                    var title = GetStr(it, "title").Trim();
                    var kind = GetStr(it, "kind").Trim();
                    if (string.IsNullOrEmpty(kind))
                        kind = title.Equals("Star Citizen Digital Download", StringComparison.OrdinalIgnoreCase)
                            ? "GamePackage"
                            : "Unknown";

                    pledge.Items.Add(new HangarItem
                    {
                        Title = title,
                        Kind = kind,
                        Manufacturer = GetStr(it, "liner").Trim(),
                    });
                }
            }

            pledge.NameableShips = ParseNameableShips(GetStr(p, "nameableShips"), pledge);
            pledge.Insurance = DetermineInsurance(pledge);
            result.Add(pledge);
        }

        // 0 件は「船が無い」ではなくページ構造の変化とみなす
        if (result.Count == 0)
            throw new InvalidOperationException("pledge が 0 件でした。ページ構造が変わった可能性があります。");

        return result;
    }

    // script.js-pledge-nameable-ships の textContent (JSON 配列文字列) を解釈する。
    // 空なら空リスト。壊れていれば警告を出して空リストにする (pledge 自体は失敗にしない)
    private List<HangarNameableShip> ParseNameableShips(string raw, HangarPledge pledge)
    {
        var list = new List<HangarNameableShip>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            // 船を含まない pledge (ペイント・装備・CCU 等) では配列以外 (null / {} など) が入る。
            // 実アカウント 82 pledge で確認済みの正常系なので、警告は出さず空リストにする
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                var idStr = GetStr(el, "membership_id").Trim();
                long.TryParse(idStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mid);
                string? custom = null;
                if (el.TryGetProperty("custom_name", out var cn) && cn.ValueKind == JsonValueKind.String)
                    custom = cn.GetString();
                list.Add(new HangarNameableShip
                {
                    MembershipId = mid,
                    DefaultName = GetStr(el, "default_name"),
                    CustomName = custom,
                });
            }
        }
        catch (Exception ex)
        {
            OnProgress?.Invoke($"nameable-ships の解析に失敗しました (pledge: {pledge.Name}): {ex.Message} → 無視します");
            list.Clear();
        }
        return list;
    }

    private HangarInsurance DetermineInsurance(HangarPledge pledge)
    {
        foreach (var item in pledge.Items)
        {
            if (!item.Kind.Equals("Insurance", StringComparison.OrdinalIgnoreCase)) continue;

            switch (item.Title.Trim())
            {
                case "Lifetime Insurance": return HangarInsurance.Lti;
                case "120 Month Insurance": return HangarInsurance.Months120;
                case "6 Month Insurance": return HangarInsurance.Months6;
                default:
                    OnProgress?.Invoke($"未知の保険種別: \"{item.Title}\" (pledge: {pledge.Name}) → Unknown として扱います");
                    return HangarInsurance.Unknown;
            }
        }
        return HangarInsurance.Unknown;
    }

    // "$1,600.00 USD" → 160000 (セント)。パースできない場合は 0
    public static long ParseMoneyCents(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var m = Regex.Match(raw, @"-?\d[\d,]*(?:\.\d+)?");
        if (!m.Success) return 0;
        var num = m.Value.Replace(",", "");
        if (!decimal.TryParse(num, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return 0;
        return (long)Math.Round(value * 100m, MidpointRounding.AwayFromZero);
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.ToString(),
        };
    }

    // === CCU ===

    private static readonly Regex CcuNameRegex =
        new(@"^Upgrade - (.+?) to (.+?) (Warbond|Standard) Edition$", RegexOptions.IgnoreCase);

    // pledge 名から CCU を判定する。CCU でなければ null
    public static HangarCcu? TryParseCcu(HangarPledge pledge)
    {
        var m = CcuNameRegex.Match(pledge.Name ?? "");
        if (!m.Success) return null;

        return new HangarCcu
        {
            PledgeId = pledge.Id,
            FromShip = m.Groups[1].Value.Trim(),
            ToShip = m.Groups[2].Value.Trim(),
            Warbond = m.Groups[3].Value.Equals("Warbond", StringComparison.OrdinalIgnoreCase),
            PriceCents = pledge.ValueCents,
            Source = "sync",
        };
    }

    // === DB ===

    private void InitDb(SqliteConnection db)
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS hangar_pledges (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                value_cents INTEGER DEFAULT 0,
                currency TEXT DEFAULT '',
                meltable INTEGER DEFAULT 1,
                upgraded INTEGER DEFAULT 0,
                insurance TEXT DEFAULT 'Unknown',
                created_at TEXT DEFAULT '',
                fetched_at TEXT DEFAULT '',
                config_value_cents INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS hangar_items (
                pledge_id TEXT NOT NULL,
                title TEXT NOT NULL,
                kind TEXT DEFAULT '',
                manufacturer TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS hangar_ccus (
                pledge_id TEXT PRIMARY KEY,
                from_ship TEXT NOT NULL,
                to_ship TEXT NOT NULL,
                warbond INTEGER DEFAULT 0,
                price_cents INTEGER DEFAULT 0,
                std_price_cents INTEGER DEFAULT 0,
                source TEXT DEFAULT 'sync',
                notes TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS hangar_meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS hangar_nameable_ships (
                pledge_id TEXT,
                membership_id INTEGER,
                default_name TEXT,
                custom_name TEXT
            );
            CREATE TABLE IF NOT EXISTS hangar_ship_marks (
                pledge_id TEXT,
                ship_name TEXT,
                mark TEXT,
                PRIMARY KEY(pledge_id, ship_name)
            );
            CREATE TABLE IF NOT EXISTS ship_matrix (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                manufacturer_code TEXT,
                manufacturer_name TEXT,
                focus TEXT,
                type TEXT,
                size TEXT,
                production_status TEXT,
                production_note TEXT,
                fetched_at TEXT
            );
            CREATE TABLE IF NOT EXISTS store_skus (
                id TEXT PRIMARY KEY,
                product_id TEXT,
                category TEXT,
                name TEXT,
                title TEXT,
                url TEXT,
                sku_type TEXT,
                is_warbond INTEGER,
                native_price_cents INTEGER,
                price_cents INTEGER,
                tax_description TEXT,
                stock_level TEXT,
                available INTEGER,
                thumbnail TEXT,
                fetched_at TEXT
            );
            """;
        cmd.ExecuteNonQuery();
        MigratePledgesSchema(db);
    }

    // 旧スキーマ (config_value_cents 列なし) の hangar_pledges に列を追加する。DROP はしない
    private static void MigratePledgesSchema(SqliteConnection db)
    {
        var hasCol = false;
        using (var check = db.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(hangar_pledges)";
            using var r = check.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), "config_value_cents", StringComparison.OrdinalIgnoreCase))
                {
                    hasCol = true;
                    break;
                }
            }
        }
        if (hasCol) return;

        Exec(db, "ALTER TABLE hangar_pledges ADD COLUMN config_value_cents INTEGER DEFAULT 0");
    }

    // 取得したスナップショットで hangar_pledges / hangar_items / hangar_nameable_ships / hangar_ccus を置き換える。
    // ただし手入力 (source='manual') の CCU と、std_price_cents / notes は保持する。hangar_ship_marks は触らない
    public void SaveSnapshot(List<HangarPledge> pledges, List<HangarCcu> ccus, string fetchedAt)
    {
        if (_dbPath == null) return;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);

        // 既存 CCU の手入力値 (標準額・メモ) と手入力行を退避しておく
        var keptStdPrice = new Dictionary<string, long>(StringComparer.Ordinal);
        var keptNotes = new Dictionary<string, string>(StringComparer.Ordinal);
        var manualIds = new HashSet<string>(StringComparer.Ordinal);
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT pledge_id, std_price_cents, notes, source FROM hangar_ccus";
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var pid = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(pid)) continue;
                keptStdPrice[pid] = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                keptNotes[pid] = r.IsDBNull(2) ? "" : r.GetString(2);
                var src = r.IsDBNull(3) ? "" : r.GetString(3);
                if (src.Equals("manual", StringComparison.OrdinalIgnoreCase)) manualIds.Add(pid);
            }
        }

        using var tx = db.BeginTransaction();

        Exec(db, "DELETE FROM hangar_pledges");
        Exec(db, "DELETE FROM hangar_items");
        Exec(db, "DELETE FROM hangar_nameable_ships");
        // 手入力 CCU は消さない
        Exec(db, "DELETE FROM hangar_ccus WHERE source = 'sync'");

        foreach (var p in pledges)
        {
            Exec(db, """
                INSERT OR REPLACE INTO hangar_pledges
                    (id, name, value_cents, currency, meltable, upgraded, insurance, created_at, fetched_at, config_value_cents)
                VALUES (@id, @n, @v, @c, @m, @u, @i, @ca, @fa, @cv)
                """,
                ("@id", p.Id), ("@n", p.Name), ("@v", p.ValueCents), ("@c", p.Currency),
                ("@m", p.Meltable ? 1 : 0), ("@u", p.Upgraded ? 1 : 0), ("@i", p.Insurance.ToString()),
                ("@ca", p.CreatedAt), ("@fa", fetchedAt), ("@cv", p.ConfigValueCents));

            foreach (var it in p.Items)
                Exec(db, "INSERT INTO hangar_items (pledge_id, title, kind, manufacturer) VALUES (@p, @t, @k, @m)",
                    ("@p", p.Id), ("@t", it.Title), ("@k", it.Kind), ("@m", it.Manufacturer));

            foreach (var ns in p.NameableShips)
                Exec(db, "INSERT INTO hangar_nameable_ships (pledge_id, membership_id, default_name, custom_name) VALUES (@p, @mi, @dn, @cn)",
                    ("@p", p.Id), ("@mi", ns.MembershipId), ("@dn", ns.DefaultName),
                    ("@cn", (object?)ns.CustomName ?? DBNull.Value));
        }

        int ccuSaved = 0;
        foreach (var c in ccus)
        {
            if (string.IsNullOrEmpty(c.PledgeId)) continue;
            // 同じ pledge_id の手入力行がある場合は上書きしない
            if (manualIds.Contains(c.PledgeId)) continue;

            var std = c.StdPriceCents;
            if (std <= 0 && keptStdPrice.TryGetValue(c.PledgeId, out var oldStd)) std = oldStd;
            var notes = c.Notes;
            if (string.IsNullOrEmpty(notes) && keptNotes.TryGetValue(c.PledgeId, out var oldNotes)) notes = oldNotes;

            Exec(db, """
                INSERT OR REPLACE INTO hangar_ccus
                    (pledge_id, from_ship, to_ship, warbond, price_cents, std_price_cents, source, notes)
                VALUES (@p, @f, @t, @w, @pr, @sp, @s, @no)
                """,
                ("@p", c.PledgeId), ("@f", c.FromShip), ("@t", c.ToShip), ("@w", c.Warbond ? 1 : 0),
                ("@pr", c.PriceCents), ("@sp", std), ("@s", string.IsNullOrEmpty(c.Source) ? "sync" : c.Source),
                ("@no", notes ?? ""));
            ccuSaved++;
        }

        SetMeta(db, "fetched_at", fetchedAt);
        tx.Commit();

        OnProgress?.Invoke($"Hangar 保存完了: pledge {pledges.Count:N0} 件 / CCU {ccuSaved:N0} 件 (取得: {fetchedAt})");
    }

    // Buy Back トークン残数 / Store Credit 残高を hangar_meta に保存する。null の項目は触らない
    public void SaveAccountInfo(int? buybackTokens, long? storeCreditCents)
    {
        if (_dbPath == null) return;
        if (buybackTokens == null && storeCreditCents == null) return;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        if (buybackTokens != null)
            SetMeta(db, "buyback_tokens", buybackTokens.Value.ToString(CultureInfo.InvariantCulture));
        if (storeCreditCents != null)
            SetMeta(db, "store_credit_cents", storeCreditCents.Value.ToString(CultureInfo.InvariantCulture));
    }

    public List<HangarPledge> LoadPledges()
    {
        var list = new List<HangarPledge>();
        if (_dbPath == null || !File.Exists(_dbPath)) return list;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);

        var byId = new Dictionary<string, HangarPledge>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, value_cents, currency, meltable, upgraded, insurance, created_at, fetched_at, config_value_cents FROM hangar_pledges ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var p = new HangarPledge
                {
                    Id = r.IsDBNull(0) ? "" : r.GetString(0),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1),
                    ValueCents = r.IsDBNull(2) ? 0 : r.GetInt64(2),
                    Currency = r.IsDBNull(3) ? "" : r.GetString(3),
                    Meltable = r.IsDBNull(4) || r.GetInt32(4) != 0,
                    Upgraded = !r.IsDBNull(5) && r.GetInt32(5) != 0,
                    Insurance = ParseInsurance(r.IsDBNull(6) ? "" : r.GetString(6)),
                    CreatedAt = r.IsDBNull(7) ? "" : r.GetString(7),
                    FetchedAt = r.IsDBNull(8) ? "" : r.GetString(8),
                    ConfigValueCents = r.IsDBNull(9) ? 0 : r.GetInt64(9),
                };
                list.Add(p);
                if (!string.IsNullOrEmpty(p.Id)) byId[p.Id] = p;
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT pledge_id, title, kind, manufacturer FROM hangar_items";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pid = r.IsDBNull(0) ? "" : r.GetString(0);
                if (!byId.TryGetValue(pid, out var p)) continue;
                p.Items.Add(new HangarItem
                {
                    Title = r.IsDBNull(1) ? "" : r.GetString(1),
                    Kind = r.IsDBNull(2) ? "" : r.GetString(2),
                    Manufacturer = r.IsDBNull(3) ? "" : r.GetString(3),
                });
            }
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT pledge_id, membership_id, default_name, custom_name FROM hangar_nameable_ships";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var pid = r.IsDBNull(0) ? "" : r.GetString(0);
                if (!byId.TryGetValue(pid, out var p)) continue;
                p.NameableShips.Add(new HangarNameableShip
                {
                    MembershipId = r.IsDBNull(1) ? 0 : r.GetInt64(1),
                    DefaultName = r.IsDBNull(2) ? "" : r.GetString(2),
                    CustomName = r.IsDBNull(3) ? null : r.GetString(3),
                });
            }
        }

        return list;
    }

    // 保有機体を1機ずつのインスタンスとして返す。
    // pledge 名からの推測はせず、items の kind=="Ship" を正とする。
    // Ship Matrix での名寄せ結果 (Matrix) と 3 値マーク (Mark) も埋める
    public List<HangarShipInstance> GetShipInstances()
    {
        var list = new List<HangarShipInstance>();
        var marks = LoadMarks();
        foreach (var p in LoadPledges())
        {
            foreach (var name in p.ShipNames)
            {
                marks.TryGetValue((p.Id, name), out var mark);
                list.Add(new HangarShipInstance
                {
                    PledgeId = p.Id,
                    PledgeName = p.Name,
                    Name = name,
                    Insurance = p.Insurance,
                    Matrix = ResolveShip(name),
                    Mark = mark,
                });
            }
        }
        return list;
    }

    // 保有船名と全 CCU の from/to のうち、Ship Matrix で解決できない名前を重複排除して返す (UI が警告表示する)
    public List<string> GetUnresolvedNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        void Check(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0) return;
            if (ResolveShip(n) != null) return;
            if (seen.Add(n)) result.Add(n);
        }

        foreach (var p in LoadPledges())
            foreach (var s in p.ShipNames) Check(s);

        foreach (var c in LoadCcus())
        {
            Check(c.FromShip);
            Check(c.ToShip);
        }

        return result;
    }

    // DB の CCU を返す。std_price_cents が 0 の行は価格テーブル (hangar_ship_prices.json) から
    // 標準 CCU 額をメモリ上で補完する (DB は書き換えない。手入力値が優先)
    public List<HangarCcu> LoadCcus()
    {
        var list = new List<HangarCcu>();
        if (_dbPath == null || !File.Exists(_dbPath)) return list;

        using (var db = new SqliteConnection($"Data Source={_dbPath}"))
        {
            InitDb(db);

            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT pledge_id, from_ship, to_ship, warbond, price_cents, std_price_cents, source, notes FROM hangar_ccus ORDER BY from_ship, to_ship";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new HangarCcu
                {
                    PledgeId = r.IsDBNull(0) ? "" : r.GetString(0),
                    FromShip = r.IsDBNull(1) ? "" : r.GetString(1),
                    ToShip = r.IsDBNull(2) ? "" : r.GetString(2),
                    Warbond = !r.IsDBNull(3) && r.GetInt32(3) != 0,
                    PriceCents = r.IsDBNull(4) ? 0 : r.GetInt64(4),
                    StdPriceCents = r.IsDBNull(5) ? 0 : r.GetInt64(5),
                    Source = r.IsDBNull(6) ? "sync" : r.GetString(6),
                    Notes = r.IsDBNull(7) ? "" : r.GetString(7),
                });
            }
        }

        foreach (var c in list)
        {
            if (c.StdPriceCents != 0) continue;
            var std = StandardCcuPriceCents(c);
            if (std.HasValue) c.StdPriceCents = std.Value;
        }
        return list;
    }

    // 手入力・修正用
    public void UpsertCcu(HangarCcu ccu)
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        Exec(db, """
            INSERT OR REPLACE INTO hangar_ccus
                (pledge_id, from_ship, to_ship, warbond, price_cents, std_price_cents, source, notes)
            VALUES (@p, @f, @t, @w, @pr, @sp, @s, @no)
            """,
            ("@p", ccu.PledgeId), ("@f", ccu.FromShip), ("@t", ccu.ToShip), ("@w", ccu.Warbond ? 1 : 0),
            ("@pr", ccu.PriceCents), ("@sp", ccu.StdPriceCents),
            ("@s", string.IsNullOrEmpty(ccu.Source) ? "sync" : ccu.Source), ("@no", ccu.Notes ?? ""));
    }

    public void DeleteCcu(string pledgeId)
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        Exec(db, "DELETE FROM hangar_ccus WHERE pledge_id = @p", ("@p", pledgeId));
    }

    // 値引き率の分母だけ更新
    public void SetCcuStdPrice(string pledgeId, long stdPriceCents)
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        Exec(db, "UPDATE hangar_ccus SET std_price_cents = @sp WHERE pledge_id = @p",
            ("@sp", stdPriceCents), ("@p", pledgeId));
    }

    // === 3 値マーク ===

    public HangarShipMark GetMark(string pledgeId, string shipName)
    {
        if (_dbPath == null || !File.Exists(_dbPath)) return HangarShipMark.None;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT mark FROM hangar_ship_marks WHERE pledge_id = @p AND ship_name = @s";
        cmd.Parameters.AddWithValue("@p", pledgeId);
        cmd.Parameters.AddWithValue("@s", shipName);
        var raw = cmd.ExecuteScalar()?.ToString() ?? "";
        return ParseMark(raw);
    }

    // None を指定した場合は行を削除する
    public void SetMark(string pledgeId, string shipName, HangarShipMark mark)
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        if (mark == HangarShipMark.None)
        {
            Exec(db, "DELETE FROM hangar_ship_marks WHERE pledge_id = @p AND ship_name = @s",
                ("@p", pledgeId), ("@s", shipName));
            return;
        }
        Exec(db, "INSERT OR REPLACE INTO hangar_ship_marks (pledge_id, ship_name, mark) VALUES (@p, @s, @m)",
            ("@p", pledgeId), ("@s", shipName), ("@m", mark.ToString()));
    }

    public Dictionary<(string PledgeId, string ShipName), HangarShipMark> LoadMarks()
    {
        var dict = new Dictionary<(string, string), HangarShipMark>();
        if (_dbPath == null || !File.Exists(_dbPath)) return dict;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT pledge_id, ship_name, mark FROM hangar_ship_marks";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var pid = r.IsDBNull(0) ? "" : r.GetString(0);
            var name = r.IsDBNull(1) ? "" : r.GetString(1);
            dict[(pid, name)] = ParseMark(r.IsDBNull(2) ? "" : r.GetString(2));
        }
        return dict;
    }

    private static HangarShipMark ParseMark(string raw)
        => Enum.TryParse<HangarShipMark>(raw, ignoreCase: true, out var v) ? v : HangarShipMark.None;

    // === 合計・アカウント情報 ===

    public HangarTotals GetTotals()
    {
        var t = new HangarTotals();
        if (_dbPath == null || !File.Exists(_dbPath)) return t;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);

        t.PledgeCount = (int)ScalarLong(db, "SELECT COUNT(*) FROM hangar_pledges");
        t.ShipCount = (int)ScalarLong(db, "SELECT COUNT(*) FROM hangar_items WHERE LOWER(kind) = 'ship'");
        t.CcuCount = (int)ScalarLong(db, "SELECT COUNT(*) FROM hangar_ccus");
        t.TotalMeltValueCents = ScalarLong(db, "SELECT COALESCE(SUM(value_cents), 0) FROM hangar_pledges");
        t.MeltableValueCents = ScalarLong(db, "SELECT COALESCE(SUM(value_cents), 0) FROM hangar_pledges WHERE meltable = 1");

        var tokens = GetMeta(db, "buyback_tokens");
        if (int.TryParse(tokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tk)) t.BuybackTokens = tk;
        var credit = GetMeta(db, "store_credit_cents");
        if (long.TryParse(credit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var cr)) t.StoreCreditCents = cr;
        t.FetchedAt = GetMeta(db, "fetched_at");
        t.ShipMatrixFetchedAt = GetMeta(db, "ship_matrix_fetched_at");
        return t;
    }

    // === Ship Matrix ===

    private const string ShipMatrixUrl = "https://robertsspaceindustries.com/ship-matrix/index";
    private static readonly TimeSpan ShipMatrixTtl = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "StarCitizenJapaneseTextCreater/1.16 (+WPF; .NET 8)");
        return http;
    }

    // インスタンス内キャッシュ (Matrix / エイリアス / 価格表)
    private List<ShipMatrixEntry>? _matrixCache;
    private Dictionary<string, ShipMatrixEntry>? _matrixByName;
    private Dictionary<string, string>? _aliasCache;
    private Dictionary<string, long>? _priceCache;
    private List<StoreSku>? _storeCache;

    // Matrix / エイリアス / 価格表 / ストアのキャッシュを破棄する (別インスタンスで Matrix を更新した後などに呼ぶ)
    public void InvalidateCaches()
    {
        _matrixCache = null;
        _matrixByName = null;
        _aliasCache = null;
        _priceCache = null;
        _storeCache = null;
    }

    // Ship Matrix を取得して ship_matrix テーブルを全置換する。
    // 24 時間以内に取得済みで force=false なら何もせず件数を返す。戻り値は保存済み件数
    public async Task<int> RefreshShipMatrixAsync(bool force)
    {
        if (_dbPath == null) return 0;

        using (var db = new SqliteConnection($"Data Source={_dbPath}"))
        {
            InitDb(db);
            var fetchedAtRaw = GetMeta(db, "ship_matrix_fetched_at");
            if (!force
                && DateTime.TryParse(fetchedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetchedAt)
                && DateTime.Now - fetchedAt.ToLocalTime() < ShipMatrixTtl)
            {
                var cached = (int)ScalarLong(db, "SELECT COUNT(*) FROM ship_matrix");
                OnProgress?.Invoke($"Ship Matrix はキャッシュを使用します: {cached:N0} 件 (取得: {fetchedAtRaw})");
                return cached;
            }
        }

        var json = await Http.GetStringAsync(ShipMatrixUrl);

        var entries = new List<ShipMatrixEntry>();
        using (var doc = JsonDocument.Parse(json))
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var okEl) || !IsTruthy(okEl))
            {
                var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.ToString() : "";
                throw new InvalidOperationException($"Ship Matrix の取得に失敗しました (success != 1): {msg}");
            }
            if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Ship Matrix の data が配列ではありません。");

            foreach (var el in dataEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) continue;
                if (!long.TryParse(GetStr(el, "id").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
                var name = GetStr(el, "name").Trim();
                if (name.Length == 0) continue;

                var mCode = "";
                var mName = "";
                if (el.TryGetProperty("manufacturer", out var man) && man.ValueKind == JsonValueKind.Object)
                {
                    mCode = GetStr(man, "code").Trim();
                    mName = GetStr(man, "name").Trim();
                }

                var size = GetStr(el, "size").Trim();

                entries.Add(new ShipMatrixEntry
                {
                    Id = id,
                    Name = name,
                    ManufacturerCode = mCode,
                    ManufacturerName = mName,
                    Focus = GetStr(el, "focus").Trim(),
                    Type = GetStr(el, "type").Trim(),
                    Size = size,
                    ProductionStatus = GetStr(el, "production_status").Trim(),
                    ProductionNote = GetStr(el, "production_note").Trim(),
                });
            }
        }

        if (entries.Count == 0)
            throw new InvalidOperationException("Ship Matrix が 0 件でした。API の構造が変わった可能性があります。");

        var now = DateTime.Now.ToString("o");
        using (var db = new SqliteConnection($"Data Source={_dbPath}"))
        {
            InitDb(db);
            using var tx = db.BeginTransaction();
            Exec(db, "DELETE FROM ship_matrix");
            foreach (var e in entries)
            {
                Exec(db, """
                    INSERT OR REPLACE INTO ship_matrix
                        (id, name, manufacturer_code, manufacturer_name, focus, type, size, production_status, production_note, fetched_at)
                    VALUES (@id, @n, @mc, @mn, @f, @t, @s, @ps, @pn, @fa)
                    """,
                    ("@id", e.Id), ("@n", e.Name), ("@mc", e.ManufacturerCode), ("@mn", e.ManufacturerName),
                    ("@f", e.Focus), ("@t", e.Type), ("@s", e.Size),
                    ("@ps", e.ProductionStatus), ("@pn", e.ProductionNote), ("@fa", now));
            }
            SetMeta(db, "ship_matrix_fetched_at", now);
            tx.Commit();
        }

        _matrixCache = null;
        _matrixByName = null;
        OnProgress?.Invoke($"Ship Matrix を取得しました: {entries.Count:N0} 件");
        return entries.Count;
    }

    private static bool IsTruthy(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => el.TryGetInt64(out var n) && n != 0,
        JsonValueKind.String => el.GetString() is "1" or "true" or "True",
        _ => false,
    };

    public List<ShipMatrixEntry> LoadShipMatrix()
    {
        var list = new List<ShipMatrixEntry>();
        if (_dbPath == null || !File.Exists(_dbPath)) return list;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, manufacturer_code, manufacturer_name, focus, type, size, production_status, production_note FROM ship_matrix ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ShipMatrixEntry
            {
                Id = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                Name = r.IsDBNull(1) ? "" : r.GetString(1),
                ManufacturerCode = r.IsDBNull(2) ? "" : r.GetString(2),
                ManufacturerName = r.IsDBNull(3) ? "" : r.GetString(3),
                Focus = r.IsDBNull(4) ? "" : r.GetString(4),
                Type = r.IsDBNull(5) ? "" : r.GetString(5),
                Size = r.IsDBNull(6) ? "" : r.GetString(6),
                ProductionStatus = r.IsDBNull(7) ? "" : r.GetString(7),
                ProductionNote = r.IsDBNull(8) ? "" : r.GetString(8),
            });
        }
        return list;
    }

    // hangar_ship_aliases.json (Hangar 上の表記 → Matrix 名)。無い/壊れていれば空
    public Dictionary<string, string> LoadAliases()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, AliasesFileName);
        try
        {
            if (!File.Exists(path))
            {
                OnProgress?.Invoke($"{AliasesFileName} が見つかりません。エイリアスなしで名寄せします: {path}");
                return dict;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var v = prop.Value.GetString();
                if (string.IsNullOrWhiteSpace(v)) continue;
                dict[prop.Name.Trim()] = v.Trim();
            }
        }
        catch (Exception ex)
        {
            OnProgress?.Invoke($"{AliasesFileName} の読込に失敗しました({ex.Message})。エイリアスなしで名寄せします。");
        }
        return dict;
    }

    private void EnsureShipCaches()
    {
        if (_matrixCache == null || _matrixByName == null)
        {
            _matrixCache = LoadShipMatrix();
            var byName = new Dictionary<string, ShipMatrixEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _matrixCache)
                if (!byName.ContainsKey(e.Name)) byName[e.Name] = e;   // 同名は先勝ち (ORDER BY name で決定的)
            _matrixByName = byName;
        }
        _aliasCache ??= LoadAliases();
    }

    // Hangar 上の船名を Ship Matrix のエントリへ解決する。
    // ① Matrix 名と大文字小文字無視で完全一致 → ② エイリアス変換後に一致 → ③ null
    public ShipMatrixEntry? ResolveShip(string hangarName)
    {
        var name = (hangarName ?? "").Trim();
        if (name.Length == 0) return null;
        EnsureShipCaches();

        if (_matrixByName!.TryGetValue(name, out var direct)) return direct;
        if (_aliasCache!.TryGetValue(name, out var alias) && _matrixByName.TryGetValue(alias, out var viaAlias)) return viaAlias;
        return null;
    }

    // 名寄せ後の正式名。解決できなければ入力をそのまま返す。CCU の from/to と保有船名の比較はこれで行う
    public string NormalizeShipName(string name) => ResolveShip(name)?.Name ?? name;

    // === 価格テーブル ===

    // hangar_ship_prices.json (船名 → 店頭価格 USD) をセント単位で返す。"_" 始まりのキーは無視
    public Dictionary<string, long> LoadShipPrices()
    {
        var dict = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, PricesFileName);
        try
        {
            if (!File.Exists(path)) return dict;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return dict;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.StartsWith('_')) continue;
                decimal usd;
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    if (!prop.Value.TryGetDecimal(out usd)) continue;
                }
                else if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var s = (prop.Value.GetString() ?? "").Replace(",", "").Replace("$", "").Trim();
                    if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out usd)) continue;
                }
                else continue;
                dict[prop.Name.Trim()] = (long)Math.Round(usd * 100m, MidpointRounding.AwayFromZero);
            }
        }
        catch (Exception ex)
        {
            OnProgress?.Invoke($"{PricesFileName} の読込に失敗しました({ex.Message})。価格表なしで続行します。");
        }
        return dict;
    }

    // from/to 双方の店頭価格が分かる場合の標準 CCU 額 (to − from)。
    // ① hangar_ship_prices.json に両方あればその差 (負なら null)
    // ② 価格表に無ければ RSI ストア (store_skus) の税抜価格の差 (両方あり・正のときのみ)
    // どちらも不明なら null
    public long? StandardCcuPriceCents(HangarCcu ccu)
    {
        _priceCache ??= LoadShipPrices();

        var from = NormalizeShipName(ccu.FromShip ?? "");
        var to = NormalizeShipName(ccu.ToShip ?? "");

        if (_priceCache.TryGetValue(from, out var fromCents) && _priceCache.TryGetValue(to, out var toCents))
        {
            var diff = toCents - fromCents;
            return diff < 0 ? null : diff;
        }

        var fromSku = FindStoreShip(from);
        var toSku = FindStoreShip(to);
        if (fromSku == null || toSku == null) return null;
        var storeDiff = toSku.NativePriceCents - fromSku.NativePriceCents;
        return storeDiff > 0 ? storeDiff : null;
    }

    // === RSI ストア (GraphQL) ===

    private static readonly TimeSpan StoreTtl = TimeSpan.FromHours(12);
    private const int StorePageSize = 100;
    private const int StoreMaxPages = 50;   // 暴走防止 (1 カテゴリ 100 件超は無いが対応する)

    // 船名の名寄せ対象カテゴリ (Standalone Ships / Upgrades)
    private const string StoreCategoryStandaloneShips = "72";
    private const string StoreCategoryUpgrades = "241";

    private const string StoreBrowseQuery =
        "query GetBrowseListingQuery($query: SearchQuery) { store(browse: true) { listing: search(query: $query) { count resources { __typename ... on TySku { id title name url type isWarbond isDirectCheckout productId price { amount taxDescription } nativePrice { amount discounted } stock { unlimited show available level } media { thumbnail { storeSmall } } } } } } }";

    // ストア用 HttpClient (1 個。Timeout 60s, User-Agent: Mozilla/5.0)
    private static readonly HttpClient StoreHttp = CreateStoreHttpClient();

    private static HttpClient CreateStoreHttpClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        return http;
    }

    // RSI ストアの販売中 SKU を取得して store_skus をカテゴリ単位で置き換える。
    // 12 時間以内に取得済みで force=false なら何もせず件数を返す。
    // 失敗したカテゴリは警告して続行し、そのカテゴリの旧データは残す。戻り値は保存済み総件数
    public async Task<int> RefreshStoreAsync(bool force, CancellationToken ct = default)
    {
        if (_dbPath == null) return 0;

        using (var db = new SqliteConnection($"Data Source={_dbPath}"))
        {
            InitDb(db);
            var fetchedAtRaw = GetMeta(db, "store_fetched_at");
            if (!force
                && DateTime.TryParse(fetchedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fetchedAt)
                && DateTime.Now - fetchedAt.ToLocalTime() < StoreTtl)
            {
                var cached = (int)ScalarLong(db, "SELECT COUNT(*) FROM store_skus");
                OnProgress?.Invoke($"ストア情報はキャッシュを使用します: {cached:N0} 件 (取得: {fetchedAtRaw})");
                return cached;
            }
        }

        var selectors = LoadSelectors();
        var url = string.IsNullOrWhiteSpace(selectors.StoreGraphqlUrl)
            ? new HangarSelectors().StoreGraphqlUrl
            : selectors.StoreGraphqlUrl;
        var categories = selectors.StoreCategories is { Count: > 0 }
            ? selectors.StoreCategories
            : new HangarSelectors().StoreCategories;

        var now = DateTime.Now.ToString("o");
        var succeeded = new Dictionary<string, List<StoreSku>>(StringComparer.Ordinal);   // product_id → SKU
        var failed = new List<string>();

        foreach (var (productId, categoryName) in categories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var skus = await FetchStoreCategoryAsync(url, productId, categoryName, now, ct);
                succeeded[productId] = skus;
                OnProgress?.Invoke($"ストア取得: {categoryName} {skus.Count:N0} 件");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed.Add(categoryName);
                OnProgress?.Invoke($"⚠ ストア取得に失敗しました ({categoryName}): {ex.Message} → このカテゴリは前回のデータを残します");
            }
        }

        if (succeeded.Count == 0)
            throw new InvalidOperationException($"ストア取得が全カテゴリで失敗しました: {string.Join(", ", failed)}");

        int total;
        using (var db = new SqliteConnection($"Data Source={_dbPath}"))
        {
            InitDb(db);
            using var tx = db.BeginTransaction();
            foreach (var (productId, skus) in succeeded)
            {
                Exec(db, "DELETE FROM store_skus WHERE product_id = @p", ("@p", productId));
                foreach (var s in skus)
                {
                    Exec(db, """
                        INSERT OR REPLACE INTO store_skus
                            (id, product_id, category, name, title, url, sku_type, is_warbond, native_price_cents, price_cents,
                             tax_description, stock_level, available, thumbnail, fetched_at)
                        VALUES (@id, @pid, @cat, @n, @t, @u, @st, @wb, @np, @pr, @tax, @sl, @av, @th, @fa)
                        """,
                        ("@id", s.Id), ("@pid", s.ProductId), ("@cat", s.Category), ("@n", s.Name), ("@t", s.Title),
                        ("@u", s.Url), ("@st", s.SkuType), ("@wb", s.IsWarbond ? 1 : 0),
                        ("@np", s.NativePriceCents), ("@pr", s.PriceCents), ("@tax", s.TaxDescription),
                        ("@sl", s.StockLevel), ("@av", s.Available ? 1 : 0), ("@th", s.Thumbnail), ("@fa", s.FetchedAt));
                }
            }
            SetMeta(db, "store_fetched_at", now);
            tx.Commit();
            total = (int)ScalarLong(db, "SELECT COUNT(*) FROM store_skus");
        }

        _storeCache = null;
        var summary = $"ストア情報を取得しました: {total:N0} 件 ({succeeded.Count}/{categories.Count} カテゴリ)";
        if (failed.Count > 0) summary += $" / 失敗: {string.Join(", ", failed)}";
        OnProgress?.Invoke(summary);
        return total;
    }

    // 1 カテゴリ (products = [productId]) を count 件になるまでページを進めて取得する
    private static async Task<List<StoreSku>> FetchStoreCategoryAsync(string url, string productId, string categoryName, string fetchedAt, CancellationToken ct)
    {
        var result = new List<StoreSku>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long count = -1;

        for (var page = 1; page <= StoreMaxPages; page++)
        {
            var body = JsonSerializer.Serialize(new[]
            {
                new
                {
                    operationName = "GetBrowseListingQuery",
                    variables = new
                    {
                        query = new
                        {
                            skus = new { products = new[] { productId } },
                            limit = StorePageSize,
                            page,
                            sort = new { field = "weight", direction = "desc" },
                        },
                    },
                    query = StoreBrowseQuery,
                },
            });

            using var content = new StringContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var resp = await StoreHttp.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);

            int pageItems = 0;
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
                    throw new InvalidOperationException("応答が配列ではありません。API の構造が変わった可能性があります。");
                var first = root[0];

                if (first.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
                {
                    var msg = errs[0].TryGetProperty("message", out var m) ? m.ToString() : errs[0].ToString();
                    throw new InvalidOperationException($"GraphQL エラー: {msg}");
                }

                if (!first.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                    || !data.TryGetProperty("store", out var store) || store.ValueKind != JsonValueKind.Object
                    || !store.TryGetProperty("listing", out var listing) || listing.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("data.store.listing がありません。API の構造が変わった可能性があります。");

                if (listing.TryGetProperty("count", out var countEl) && countEl.ValueKind == JsonValueKind.Number && countEl.TryGetInt64(out var c))
                    count = c;

                if (!listing.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException("listing.resources が配列ではありません。");

                foreach (var el in resources.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var typeName = GetStr(el, "__typename");
                    if (typeName.Length > 0 && typeName != "TySku") continue;

                    var id = GetStr(el, "id").Trim();
                    if (id.Length == 0) continue;
                    pageItems++;
                    if (!seen.Add(id)) continue;

                    var skuUrl = GetStr(el, "url").Trim();
                    var isWarbond = false;
                    if (el.TryGetProperty("isWarbond", out var wbEl)) isWarbond = IsTruthy(wbEl);
                    if (skuUrl.EndsWith("-Warbond", StringComparison.OrdinalIgnoreCase)) isWarbond = true;

                    long nativeCents = 0, priceCents = 0;
                    var taxDesc = "";
                    if (el.TryGetProperty("nativePrice", out var np) && np.ValueKind == JsonValueKind.Object
                        && np.TryGetProperty("amount", out var npAmt))
                        nativeCents = ReadCents(npAmt);
                    if (el.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Object)
                    {
                        if (pr.TryGetProperty("amount", out var prAmt)) priceCents = ReadCents(prAmt);
                        if (pr.TryGetProperty("taxDescription", out var td))
                        {
                            taxDesc = td.ValueKind == JsonValueKind.Array
                                ? string.Join(", ", td.EnumerateArray().Select(x => x.ToString()))
                                : td.ValueKind == JsonValueKind.Null ? "" : td.ToString();
                        }
                    }

                    var stockLevel = "";
                    var available = false;
                    if (el.TryGetProperty("stock", out var stock) && stock.ValueKind == JsonValueKind.Object)
                    {
                        stockLevel = GetStr(stock, "level").Trim();
                        if (stock.TryGetProperty("available", out var avEl)) available = IsTruthy(avEl);
                    }

                    var thumb = "";
                    if (el.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object
                        && media.TryGetProperty("thumbnail", out var th) && th.ValueKind == JsonValueKind.Object)
                        thumb = GetStr(th, "storeSmall").Trim();

                    var name = GetStr(el, "name").Trim();
                    var title = GetStr(el, "title").Trim();
                    if (name.Length == 0) name = title;

                    result.Add(new StoreSku
                    {
                        Id = id,
                        ProductId = productId,   // 問い合わせに使ったカテゴリ id (SKU 自身の productId ではない)
                        Category = categoryName,
                        Name = name,
                        Title = title,
                        Url = skuUrl,
                        SkuType = GetStr(el, "type").Trim(),
                        IsWarbond = isWarbond,
                        NativePriceCents = nativeCents,
                        PriceCents = priceCents,
                        TaxDescription = taxDesc,
                        StockLevel = stockLevel,
                        Available = available,
                        Thumbnail = thumb,
                        FetchedAt = fetchedAt,
                    });
                }
            }

            if (pageItems == 0) break;                       // 空ページ → 終了
            if (count >= 0 && result.Count >= count) break;  // count 件に達した
        }

        return result;
    }

    // nativePrice.amount / price.amount はセント単位の整数 (例 6000 = $60.00)。文字列で来ても受ける
    private static long ReadCents(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Number => el.TryGetInt64(out var n) ? n : (long)Math.Round(el.GetDouble(), MidpointRounding.AwayFromZero),
        JsonValueKind.String => long.TryParse(el.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0,
        _ => 0,
    };

    public List<StoreSku> LoadStoreSkus()
    {
        var list = new List<StoreSku>();
        if (_dbPath == null || !File.Exists(_dbPath)) return list;

        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, product_id, category, name, title, url, sku_type, is_warbond, native_price_cents, price_cents,
                   tax_description, stock_level, available, thumbnail, fetched_at
            FROM store_skus ORDER BY category, name, id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new StoreSku
            {
                Id = r.IsDBNull(0) ? "" : r.GetString(0),
                ProductId = r.IsDBNull(1) ? "" : r.GetString(1),
                Category = r.IsDBNull(2) ? "" : r.GetString(2),
                Name = r.IsDBNull(3) ? "" : r.GetString(3),
                Title = r.IsDBNull(4) ? "" : r.GetString(4),
                Url = r.IsDBNull(5) ? "" : r.GetString(5),
                SkuType = r.IsDBNull(6) ? "" : r.GetString(6),
                IsWarbond = !r.IsDBNull(7) && r.GetInt32(7) != 0,
                NativePriceCents = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                PriceCents = r.IsDBNull(9) ? 0 : r.GetInt64(9),
                TaxDescription = r.IsDBNull(10) ? "" : r.GetString(10),
                StockLevel = r.IsDBNull(11) ? "" : r.GetString(11),
                Available = !r.IsDBNull(12) && r.GetInt32(12) != 0,
                Thumbnail = r.IsDBNull(13) ? "" : r.GetString(13),
                FetchedAt = r.IsDBNull(14) ? "" : r.GetString(14),
            });
        }
        return list;
    }

    // hangar_meta.store_fetched_at (ISO 8601)。未取得なら null
    public string? StoreFetchedAt()
    {
        if (_dbPath == null || !File.Exists(_dbPath)) return null;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        return GetMeta(db, "store_fetched_at");
    }

    // 船名 (名寄せ後) が一致する Standalone Ships / Upgrades の SKU を列挙する
    private IEnumerable<StoreSku> MatchStoreShips(string shipName)
    {
        var target = NormalizeShipName((shipName ?? "").Trim());
        if (target.Length == 0) return Enumerable.Empty<StoreSku>();
        var cache = _storeCache ??= LoadStoreSkus();   // 別スレッドの InvalidateCaches に備えローカルへ取る
        return cache.Where(s =>
            (s.ProductId == StoreCategoryStandaloneShips || s.ProductId == StoreCategoryUpgrades)
            && NormalizeShipName(s.Name).Equals(target, StringComparison.OrdinalIgnoreCase));
    }

    // 同名の販売中 SKU。通常版を優先し、無ければ Warbond 版。カテゴリは Standalone Ships を Upgrades より優先。無ければ null
    public StoreSku? FindStoreShip(string shipName)
        => MatchStoreShips(shipName)
            .OrderBy(s => s.IsWarbond ? 1 : 0)
            .ThenBy(s => s.ProductId == StoreCategoryStandaloneShips ? 0 : 1)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    // 同名の Warbond SKU。無ければ null
    public StoreSku? FindStoreShipWarbond(string shipName)
        => MatchStoreShips(shipName)
            .Where(s => s.IsWarbond)
            .OrderBy(s => s.ProductId == StoreCategoryStandaloneShips ? 0 : 1)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();

    // === CCU プランナー (DFS) ===

    // 仕様 §08 の判定 1〜5:
    //  1. from に一致する保有船が無い (連鎖でも到達できない) CCU は Unusable
    //  2. to が既に保有している型なら Rationale に「重複: 既に保有」を含める (除外はしない)
    //  3. Mark == Drop の船を起点に持つ CCU を優先
    //  4. 各起点船から DFS で連鎖を列挙し、終端が Drop でない型に到達する経路のみ残す。
    //     「Drop 起点」＞「深い連鎖」＞「合計額が小さい」で 1 つ選ぶ
    //  5. 使われなかった CCU は Melts
    public CcuPlan BuildPlan(List<HangarShipInstance> ships, List<HangarCcu> ccus, int maxDepth = 5, int timeoutMs = 500)
    {
        var plan = new CcuPlan();
        var sw = Stopwatch.StartNew();

        // 正規化済みの型名で扱う
        var heldTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dropTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // Drop マークされた保有船の型
        foreach (var s in ships)
        {
            var t = NormalizeShipName(s.Name);
            heldTypes.Add(t);
            if (s.Mark == HangarShipMark.Drop) dropTypes.Add(t);
        }

        var ccuFrom = new Dictionary<string, string>(StringComparer.Ordinal);   // CcuId → from (正規化)
        var ccuTo = new Dictionary<string, string>(StringComparer.Ordinal);     // CcuId → to (正規化)
        var byFrom = new Dictionary<string, List<HangarCcu>>(StringComparer.OrdinalIgnoreCase);
        var validCcus = new List<HangarCcu>();
        foreach (var c in ccus)
        {
            if (string.IsNullOrEmpty(c.PledgeId)) continue;
            if (ccuFrom.ContainsKey(c.PledgeId)) continue;   // 同一 id は 1 回だけ
            var f = NormalizeShipName(c.FromShip ?? "");
            var t = NormalizeShipName(c.ToShip ?? "");
            ccuFrom[c.PledgeId] = f;
            ccuTo[c.PledgeId] = t;
            validCcus.Add(c);
            if (!byFrom.TryGetValue(f, out var lst)) byFrom[f] = lst = new List<HangarCcu>();
            lst.Add(c);
        }
        // 決定性のため各リストを id でソート
        foreach (var lst in byFrom.Values)
            lst.Sort((a, b) => string.CompareOrdinal(a.PledgeId, b.PledgeId));

        // 1. 到達可能な型 (保有型 + CCU で辿れる型) を求め、from が到達不能な CCU は Unusable
        var reachable = new HashSet<string>(heldTypes, StringComparer.OrdinalIgnoreCase);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var c in validCcus)
                if (reachable.Contains(ccuFrom[c.PledgeId]) && reachable.Add(ccuTo[c.PledgeId])) grew = true;
        }
        var unusableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in validCcus)
        {
            if (reachable.Contains(ccuFrom[c.PledgeId])) continue;
            unusableIds.Add(c.PledgeId);
            plan.Unusable.Add(new CcuPlanUnusable { CcuId = c.PledgeId, Reason = "元船を保有していない" });
        }

        // 3. 起点船の順序: Drop 起点を先に。その後は pledge id / 船名で決定的に
        var origins = ships
            .OrderBy(s => s.Mark == HangarShipMark.Drop ? 0 : 1)
            .ThenBy(s => s.PledgeId, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var origin in origins)
        {
            if (sw.ElapsedMilliseconds > timeoutMs) { plan.TimedOut = true; break; }

            var start = NormalizeShipName(origin.Name);
            if (!byFrom.ContainsKey(start)) continue;

            // 4. DFS で経路を列挙
            var best = default(List<HangarCcu>);
            long bestCost = 0;
            var path = new List<HangarCcu>();
            var visitedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };

            void Dfs(string current, int depth)
            {
                if (sw.ElapsedMilliseconds > timeoutMs) { plan.TimedOut = true; return; }
                if (depth >= maxDepth) return;
                if (!byFrom.TryGetValue(current, out var nexts)) return;

                foreach (var c in nexts)
                {
                    if (plan.TimedOut) return;
                    if (used.Contains(c.PledgeId) || unusableIds.Contains(c.PledgeId)) continue;
                    if (path.Contains(c)) continue;
                    var to = ccuTo[c.PledgeId];
                    if (visitedTypes.Contains(to)) continue;   // 循環防止

                    path.Add(c);
                    visitedTypes.Add(to);

                    // 終端候補: Drop でない型に到達
                    if (!dropTypes.Contains(to))
                    {
                        long cost = path.Sum(x => x.PriceCents);
                        if (best == null
                            || path.Count > best.Count
                            || (path.Count == best.Count && cost < bestCost)
                            || (path.Count == best.Count && cost == bestCost
                                && string.CompareOrdinal(JoinIds(path), JoinIds(best)) < 0))
                        {
                            best = new List<HangarCcu>(path);
                            bestCost = cost;
                        }
                    }

                    Dfs(to, depth + 1);

                    visitedTypes.Remove(to);
                    path.RemoveAt(path.Count - 1);
                }
            }

            Dfs(start, 0);

            if (best == null) continue;

            foreach (var c in best) used.Add(c.PledgeId);
            var finalType = ccuTo[best[^1].PledgeId];
            var rationale = new List<string>();
            rationale.Add(origin.Mark == HangarShipMark.Drop ? "Drop 起点" : "起点");
            rationale.Add($"連鎖 {best.Count} 段");
            rationale.Add($"合計 ${bestCost / 100.0:N2}");
            if (heldTypes.Contains(finalType)) rationale.Add("重複: 既に保有");   // 2.

            plan.Applies.Add(new CcuPlanApply
            {
                CcuIds = best.Select(x => x.PledgeId).ToList(),
                TargetPledgeId = origin.PledgeId,
                FromShip = origin.Name,
                ToShip = finalType,
                TotalCostCents = bestCost,
                ResultInsurance = origin.Insurance,
                Rationale = string.Join(" / ", rationale),
            });

            if (plan.TimedOut) break;
        }

        // 5. 使われなかった CCU は Melts
        foreach (var c in validCcus)
        {
            if (used.Contains(c.PledgeId) || unusableIds.Contains(c.PledgeId)) continue;
            plan.Melts.Add(new CcuPlanMelt
            {
                CcuId = c.PledgeId,
                Reason = "適用先が無い／終端が不要",
                ValueCents = c.PriceCents,
            });
        }

        return plan;
    }

    private static string JoinIds(List<HangarCcu> path) => string.Join("|", path.Select(x => x.PledgeId));

    // === melt シミュレーション ===

    // R-01: Credit = Σ melt pledge 額 + Σ melt CCU 額
    // R-03: $1,000 超の pledge はチケット必要
    // R-05: パックを melt すると同梱機を全て失う
    // R-06: 元船を melt すると、それを from とする CCU は使用不能
    // R-08: 不足分 × (1 + 税率) を現金で支払う (decimal で計算)
    // R-09: meltable=false の pledge は列挙する (Credit 等の集計からは除外)
    public MeltSimResult SimulateMelt(MeltSimInput input)
    {
        var result = new MeltSimResult();
        var pledges = LoadPledges();
        var ccus = LoadCcus();
        var pledgeById = pledges.Where(p => !string.IsNullOrEmpty(p.Id))
            .GroupBy(p => p.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var ccuById = ccus.Where(c => !string.IsNullOrEmpty(c.PledgeId))
            .GroupBy(c => c.PledgeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        long credit = 0;
        var counted = new HashSet<string>(StringComparer.Ordinal);
        var meltedShipTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in input.MeltPledgeIds ?? new List<string>())
        {
            if (string.IsNullOrEmpty(id) || !counted.Add(id)) continue;
            if (!pledgeById.TryGetValue(id, out var p)) continue;

            if (!p.Meltable)
            {
                result.NotMeltablePledges.Add(id);   // R-09
                continue;
            }

            credit += p.ValueCents;                  // R-01
            if (p.NeedsTicket) result.NeedsTicketPledges.Add(id);   // R-03
            if (p.Insurance == HangarInsurance.Lti) result.LosesLti = true;
            foreach (var s in p.ShipNames)           // R-05
            {
                result.LostShips.Add(s);
                meltedShipTypes.Add(NormalizeShipName(s));
            }
        }

        foreach (var id in input.MeltCcuIds ?? new List<string>())
        {
            if (string.IsNullOrEmpty(id) || !counted.Add(id)) continue;   // pledge 側で数えた id は二重計上しない
            if (!ccuById.TryGetValue(id, out var c)) continue;
            credit += c.PriceCents;                  // R-01
        }

        result.CreditCents = credit;

        // R-08
        var shortfall = Math.Max(0L, input.TargetPriceCents - credit);
        var cash = (decimal)shortfall * (1m + (decimal)input.TaxRate);
        result.CashCents = (long)Math.Round(cash, MidpointRounding.AwayFromZero);

        // R-06
        foreach (var c in ccus)
        {
            if (string.IsNullOrEmpty(c.PledgeId)) continue;
            if (meltedShipTypes.Contains(NormalizeShipName(c.FromShip ?? "")))
                result.BlockedCcuIds.Add(c.PledgeId);
        }

        return result;
    }

    // === ヘルパ ===

    private static HangarInsurance ParseInsurance(string raw)
        => Enum.TryParse<HangarInsurance>(raw, ignoreCase: true, out var v) ? v : HangarInsurance.Unknown;

    private static void SetMeta(SqliteConnection db, string key, string value)
    {
        Exec(db, "INSERT OR REPLACE INTO hangar_meta VALUES (@k, @v)", ("@k", key), ("@v", value));
    }

    private static string? GetMeta(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM hangar_meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? null : v.ToString();
    }

    private static long ScalarLong(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is DBNull ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection db, string sql, params (string name, object value)[] parms)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parms)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
