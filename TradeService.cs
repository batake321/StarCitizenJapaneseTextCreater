using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class TradeService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string UexBase = "https://api.uexcorp.space/2.0";

    private Dictionary<int, CommodityInfo> _commodities = new();
    private List<CommodityPriceEntry> _allPrices = new();
    private List<ShipInfo> _ships = new();
    private Dictionary<string, TerminalInfo> _terminals = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPriceUpdate = DateTime.MinValue;
    private bool _isFetching;
    private string? _dbPath;
    private const int CacheHours = 24;

    public bool HasPriceData => _allPrices.Count > 0;
    public bool HasShipData => _ships.Count > 0;
    public bool IsFetching => _isFetching;
    public DateTime LastPriceUpdate => _lastPriceUpdate;
    public List<ShipInfo> Ships => _ships;
    public int PriceCount => _allPrices.Count;
    public int CommodityCount => _commodities.Count;
    public int MissingCount { get; private set; }

    public event Action<string>? OnProgress;

    public string GamePatch { get; set; } = "";

    public void SetCacheDir(string dir) => _dbPath = Path.Combine(dir, "trade_cache.db");

    // === Data Fetch ===

    public async Task FetchAllDataAsync(bool force = false)
    {
        if (_isFetching) return;

        if (!force && TryLoadCache())
        {
            OnProgress?.Invoke($"キャッシュ読込: 価格 {_allPrices.Count:N0} 件, 船 {_ships.Count} 件 (取得: {_lastPriceUpdate:yyyy/MM/dd HH:mm})");
            if (_ships.Count == 0)
            {
                OnProgress?.Invoke("船データがキャッシュにないためAPIから取得...");
                try { await FetchShipsAsync(); }
                catch (Exception ex) { OnProgress?.Invoke($"船データ取得エラー: {ex.Message}"); }
            }
            if (_terminals.Count == 0)
            {
                OnProgress?.Invoke("ターミナルデータがキャッシュにないためAPIから取得...");
                try { await FetchTerminalsAsync(); }
                catch (Exception ex) { OnProgress?.Invoke($"ターミナル取得エラー: {ex.Message}"); }
            }
            return;
        }

        _isFetching = true;
        try
        {
            OnProgress?.Invoke("船データ取得中...");
            try { await FetchShipsAsync(); }
            catch (Exception ex) { OnProgress?.Invoke($"船データ取得エラー(スキップ): {ex.Message}"); }

            OnProgress?.Invoke($"船 {_ships.Count} 件。ターミナル取得中...");
            try { await FetchTerminalsAsync(); }
            catch (Exception ex) { OnProgress?.Invoke($"ターミナル取得エラー(スキップ): {ex.Message}"); }

            OnProgress?.Invoke($"ターミナル {_terminals.Count} 件。コモディティ取得中...");
            await FetchCommoditiesAsync();

            OnProgress?.Invoke($"コモディティ {_commodities.Count} 件。価格データ取得中...");
            await FetchAllPricesAsync();

            SaveCache();
            OnProgress?.Invoke($"完了: 価格 {_allPrices.Count:N0} 件 (更新: {_lastPriceUpdate:HH:mm})");
        }
        finally
        {
            _isFetching = false;
        }
    }

    private async Task FetchShipsAsync()
    {
        OnProgress?.Invoke("UEX vehicles API 呼び出し中...");
        var resp = await Http.GetStringAsync($"{UexBase}/vehicles");
        OnProgress?.Invoke($"UEX vehicles 応答: {resp.Length} bytes");
        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            OnProgress?.Invoke("UEX vehicles: 'data' プロパティなし");
            return;
        }

        var ships = new List<ShipInfo>();
        int errors = 0;
        foreach (var v in data.EnumerateArray())
        {
            try
            {
                var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var mfr = v.TryGetProperty("manufacturer_name", out var m) ? m.GetString() ?? "" : "";
                var scu = 0;
                if (v.TryGetProperty("scu", out var s) && s.ValueKind == JsonValueKind.Number)
                    scu = (int)s.GetDouble();
                if (string.IsNullOrEmpty(name)) continue;
                ships.Add(new ShipInfo { Name = name, Manufacturer = mfr, Scu = scu });
            }
            catch { errors++; }
        }
        _ships = ships.OrderBy(s2 => s2.DisplayName).ToList();
        OnProgress?.Invoke($"UEX vehicles: {_ships.Count} 件パース完了" + (errors > 0 ? $" ({errors} 件スキップ)" : ""));
    }

    private async Task FetchTerminalsAsync()
    {
        try
        {
            var resp = await Http.GetStringAsync($"{UexBase}/terminals");
            using var doc = JsonDocument.Parse(resp);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return;

            var terminals = new Dictionary<string, TerminalInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in data.EnumerateArray())
            {
                var name = t.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(name)) continue;
                terminals[name] = new TerminalInfo
                {
                    Id = GetInt(t, "id"),
                    Name = name,
                    HasLoadingDock = GetBool(t, "has_loading_dock"),
                    HasDockingPort = GetBool(t, "has_docking_port"),
                    IsCargoCenter = GetBool(t, "is_cargo_center"),
                };
            }
            _terminals = terminals;
        }
        catch { }
    }

    private async Task FetchCommoditiesAsync()
    {
        var resp = await Http.GetStringAsync($"{UexBase}/commodities");
        using var doc = JsonDocument.Parse(resp);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return;

        var commodities = new Dictionary<int, CommodityInfo>();
        foreach (var c in data.EnumerateArray())
        {
            var id = GetInt(c, "id");
            var name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var kind = c.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "";
            var scu = GetInt(c, "scu");
            if (id > 0 && !string.IsNullOrEmpty(name))
                commodities[id] = new CommodityInfo { Id = id, Name = name, Kind = kind, Scu = scu };
        }
        _commodities = commodities;
    }

    private async Task FetchAllPricesAsync()
    {
        var allPrices = new List<CommodityPriceEntry>();
        int fetched = 0;

        foreach (var commodity in _commodities.Values)
        {
            try
            {
                var priceResp = await Http.GetStringAsync($"{UexBase}/commodities_prices?id_commodity={commodity.Id}");
                using var priceDoc = JsonDocument.Parse(priceResp);
                if (!priceDoc.RootElement.TryGetProperty("data", out var priceData)) continue;

                foreach (var item in priceData.EnumerateArray())
                {
                    var entry = ParsePriceEntry(item, commodity);
                    if (entry != null) allPrices.Add(entry);
                }

                fetched++;
                if (fetched % 10 == 0)
                    OnProgress?.Invoke($"価格取得中... {fetched}/{_commodities.Count}");

                await Task.Delay(50);
            }
            catch { }
        }

        _allPrices = allPrices;
        _lastPriceUpdate = DateTime.Now;
    }

    private static CommodityPriceEntry? ParsePriceEntry(JsonElement item, CommodityInfo commodity)
    {
        var priceBuy = GetDouble(item, "price_buy");
        var priceSell = GetDouble(item, "price_sell");
        if (priceBuy <= 0 && priceSell <= 0) return null;

        var terminal = GetStr(item, "terminal_name");
        var city = GetStr(item, "city_name");
        var outpost = GetStr(item, "outpost_name");
        var moon = GetStr(item, "moon_name");
        var planet = GetStr(item, "planet_name");
        var starSystem = GetStr(item, "star_system_name");

        var locationShort = BuildLocationShort(city, outpost, terminal, moon, planet);

        return new CommodityPriceEntry
        {
            CommodityId = commodity.Id,
            CommodityName = commodity.Name,
            CommodityKind = commodity.Kind,
            ContainerScu = commodity.Scu,
            Terminal = terminal,
            City = city,
            Outpost = outpost,
            Moon = moon,
            Planet = planet,
            StarSystem = starSystem,
            LocationShort = locationShort,
            PriceBuy = priceBuy,
            PriceSell = priceSell,
            ScuBuy = GetInt(item, "scu_buy"),
            ScuSell = GetInt(item, "scu_sell"),
            ScuBuyAvg = GetInt(item, "scu_buy_avg"),
            ScuSellAvg = GetInt(item, "scu_sell_avg"),
            PriceBuyAvg = GetDouble(item, "price_buy_avg"),
            PriceSellAvg = GetDouble(item, "price_sell_avg"),
            DateModified = GetStr(item, "date_modified"),
        };
    }

    // 場所名の組み立て。city / outpost / terminal に同じ名前が入ることがあるため重複を除去する
    // (例: outpost="HDMS-Woodruff", terminal="HDMS-Woodruff" → "HDMS-Woodruff > HDMS-Woodruff" になってしまう)
    private static string BuildLocationShort(string city, string outpost, string terminal, string moon, string planet)
    {
        var locationParts = new[] { city, outpost, terminal }
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.Ordinal);
        var locationShort = string.Join(" > ", locationParts);
        if (string.IsNullOrEmpty(locationShort)) locationShort = moon;
        if (string.IsNullOrEmpty(locationShort)) locationShort = planet;
        return locationShort;
    }

    // === Route Calculation ===

    public List<TradeRoute> CalculateBestRoutes(
        double budget, int cargoScu,
        string buySystem, string sellSystem,
        bool excludeOutposts, bool loadingDockOnly,
        bool excludeLowStock = false,
        HashSet<string>? commodityFilter = null,
        int topN = 10)
    {
        var buyEntries = _allPrices.Where(p => p.PriceBuy > 0).AsEnumerable();
        var sellEntries = _allPrices.Where(p => p.PriceSell > 0).AsEnumerable();

        if (buySystem != "全て")
            buyEntries = buyEntries.Where(p => p.StarSystem.Equals(buySystem, StringComparison.OrdinalIgnoreCase));
        if (sellSystem != "全て")
            sellEntries = sellEntries.Where(p => p.StarSystem.Equals(sellSystem, StringComparison.OrdinalIgnoreCase));

        if (commodityFilter != null && commodityFilter.Count > 0)
        {
            buyEntries = buyEntries.Where(p => commodityFilter.Contains(p.CommodityName));
            sellEntries = sellEntries.Where(p => commodityFilter.Contains(p.CommodityName));
        }

        if (excludeOutposts)
        {
            buyEntries = buyEntries.Where(p => !IsSmallGroundOutpost(p));
            sellEntries = sellEntries.Where(p => !IsSmallGroundOutpost(p));
        }

        if (loadingDockOnly)
        {
            buyEntries = buyEntries.Where(HasLoadingDock);
            sellEntries = sellEntries.Where(HasLoadingDock);
        }

        // Top buy candidates per commodity: up to 5, scored by price + stock fillability
        var buysByCommodity = buyEntries
            .GroupBy(p => p.CommodityId)
            .ToDictionary(g => g.Key, g =>
            {
                return g.OrderBy(p =>
                {
                    // Effective price: penalize locations that can't fill the cargo
                    var fillRatio = cargoScu > 0 && p.ScuBuy > 0
                        ? Math.Min(1.0, (double)p.ScuBuy / cargoScu)
                        : (p.ScuBuy > 0 ? 1.0 : 0.01);
                    return p.PriceBuy / fillRatio;
                }).Take(5).ToList();
            });

        // Top sell candidates per commodity: up to 3, prefer high price with stock
        var sellsByCommodity = sellEntries
            .GroupBy(p => p.CommodityId)
            .ToDictionary(g => g.Key, g =>
            {
                return g.OrderByDescending(p =>
                {
                    var fillRatio = cargoScu > 0 && p.ScuSell > 0
                        ? Math.Min(1.0, (double)p.ScuSell / cargoScu)
                        : (p.ScuSell > 0 ? 1.0 : 0.01);
                    return p.PriceSell * fillRatio;
                }).Take(3).ToList();
            });

        var routes = new List<TradeRoute>();

        foreach (var (commodityId, buys) in buysByCommodity)
        {
            if (!sellsByCommodity.TryGetValue(commodityId, out var sells)) continue;

            foreach (var buy in buys)
            {
                foreach (var sell in sells)
                {
                    // Skip same-terminal routes
                    if (buy.Terminal == sell.Terminal && buy.City == sell.City) continue;

                    var profitPerScu = sell.PriceSell - buy.PriceBuy;
                    if (profitPerScu <= 0) continue;

                    var maxByBudget = budget > 0 ? (int)Math.Floor(budget / buy.PriceBuy) : int.MaxValue;
                    var maxByCargo = cargoScu > 0 ? cargoScu : int.MaxValue;

                    // Factor in container SCU: round down to container boundary
                    var cs = buy.ContainerScu > 0 ? buy.ContainerScu : 1;
                    var actualScu = Math.Min(maxByBudget, maxByCargo);
                    actualScu = (actualScu / cs) * cs;
                    if (actualScu <= 0 || actualScu > 1_000_000) continue;

                    // Clamp to available buy stock
                    var fillableScu = buy.ScuBuy > 0 ? Math.Min(actualScu, buy.ScuBuy) : actualScu;
                    fillableScu = (fillableScu / cs) * cs;
                    if (fillableScu <= 0) fillableScu = actualScu;

                    var isLowBuyStock = buy.ScuBuy > 0 && buy.ScuBuy < actualScu;
                    var isNoBuyStock = buy.ScuBuy == 0;
                    var isNoSellStock = sell.ScuSell == 0;
                    var isNoRecord = isNoBuyStock || isNoSellStock;
                    
                    if (excludeLowStock && (isLowBuyStock || isNoRecord)) continue;

                    // Use fillable SCU for realistic profit calculation
                    var useScu = (isLowBuyStock && buy.ScuBuy > 0) ? fillableScu : actualScu;
                    var investment = buy.PriceBuy * useScu;
                    var revenue = sell.PriceSell * useScu;
                    var totalProfit = revenue - investment;
                    var roi = investment > 0 ? (totalProfit / investment) * 100 : 0;

                    routes.Add(new TradeRoute
                    {
                        CommodityName = buy.CommodityName,
                        CommodityKind = buy.CommodityKind,
                        ContainerScu = buy.ContainerScu,
                        BuyLocation = buy.LocationShort,
                        BuyTerminal = buy.Terminal,
                        BuySystem = buy.StarSystem,
                        BuyPlanet = buy.Planet,
                        BuyPrice = buy.PriceBuy,
                        BuyPriceAvg = buy.PriceBuyAvg,
                        SellLocation = sell.LocationShort,
                        SellTerminal = sell.Terminal,
                        SellSystem = sell.StarSystem,
                        SellPlanet = sell.Planet,
                        SellPrice = sell.PriceSell,
                        SellPriceAvg = sell.PriceSellAvg,
                        ProfitPerScu = profitPerScu,
                        ActualScu = useScu,
                        Investment = investment,
                        TotalProfit = totalProfit,
                        Roi = roi,
                        ScuBuyStock = buy.ScuBuy,
                        ScuSellStock = sell.ScuSell,
                        ScuBuyAvg = buy.ScuBuyAvg,
                        ScuSellAvg = sell.ScuSellAvg,
                        IsLowBuyStock = isLowBuyStock || isNoBuyStock,
                        IsNoRecord = isNoRecord,
                    });
                }
            }
        }

        // Deduplicate: keep best route per (commodity, buyTerminal) combination
        var deduped = routes
            .GroupBy(r => (r.CommodityName, r.BuyTerminal))
            .Select(g => g.OrderByDescending(r => r.TotalProfit).First())
            .ToList();

        var validRoutes = deduped.Where(r => !r.IsNoRecord).OrderByDescending(r => r.TotalProfit).ToList();
        var noRecordRoutes = deduped.Where(r => r.IsNoRecord).OrderByDescending(r => r.TotalProfit).ToList();

        var finalRoutes = validRoutes.Take(topN).ToList();
        finalRoutes.AddRange(noRecordRoutes.Take(topN)); // Add some no record routes at the bottom
        return finalRoutes;
    }

    // === ChatService API ===

    public string FormatTradeRouteSummary(
        double budget = 500000, int cargoScu = 100,
        string buySystem = "全て", string sellSystem = "全て",
        string? commodityFilter = null, int topN = 10)
    {
        if (!HasPriceData) return "交易価格データが未取得です。コモディティタブで [価格更新] を実行してください。";

        var routes = CalculateBestRoutes(budget, cargoScu, buySystem, sellSystem,
            excludeOutposts: true, loadingDockOnly: false, topN: topN);

        if (!string.IsNullOrEmpty(commodityFilter))
            routes = routes.Where(r => r.CommodityName.Contains(commodityFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (routes.Count == 0)
            return "条件に合う交易ルートが見つかりませんでした。";

        var sb = new StringBuilder();
        sb.AppendLine($"=== 交易ルート TOP {routes.Count} (データ: {_lastPriceUpdate:HH:mm}) ===");
        sb.AppendLine($"条件: 予算 {budget:N0} aUEC / 積載 {cargoScu} SCU / {buySystem} → {sellSystem}");
        sb.AppendLine();

        int rank = 1;
        foreach (var r in routes)
        {
            sb.AppendLine($"#{rank++} {r.CommodityName} ({r.CommodityKind}) CS:{r.ContainerScuDisplay}");
            sb.AppendLine($"  購入: {r.BuyDisplay} @ {r.BuyPrice:N1}/SCU [在庫: {r.BuyStockDisplay}]");
            sb.AppendLine($"  売却: {r.SellDisplay} @ {r.SellPrice:N1}/SCU [在庫: {r.SellStockDisplay}]");
            sb.AppendLine($"  利益: {r.ProfitDisplay}/SCU × {r.ActualScu} SCU = {r.TotalProfitDisplay} aUEC (ROI {r.RoiDisplay})");
            if (r.IsNoRecord)
                sb.AppendLine($"  ⚠ 実績なし (在庫データがありません)");
            else if (r.IsLowBuyStock)
                sb.AppendLine($"  ⚠ 在庫注意: 現在在庫 {r.ScuBuyStock} SCU < 必要量 {r.ActualScu} SCU");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // === DB Cache ===

    private void InitDb(SqliteConnection db)
    {
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS trade_prices (
                commodity_id INTEGER, commodity_name TEXT, commodity_kind TEXT, container_scu INTEGER,
                terminal TEXT, city TEXT, outpost TEXT, moon TEXT, planet TEXT, star_system TEXT, location_short TEXT,
                price_buy REAL, price_sell REAL, price_buy_avg REAL, price_sell_avg REAL,
                scu_buy INTEGER, scu_sell INTEGER, scu_buy_avg INTEGER, scu_sell_avg INTEGER,
                date_modified TEXT, fetched_at TEXT, patch TEXT, is_current INTEGER DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS trade_ships (name TEXT, manufacturer TEXT, scu INTEGER, fetched_at TEXT);
            CREATE TABLE IF NOT EXISTS trade_terminals (id INTEGER DEFAULT 0, name TEXT PRIMARY KEY, has_loading_dock INTEGER, has_docking_port INTEGER, is_cargo_center INTEGER);
            CREATE TABLE IF NOT EXISTS trade_meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS my_ships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                manufacturer TEXT DEFAULT '',
                scu INTEGER DEFAULT 0,
                notes TEXT DEFAULT '',
                added_at TEXT DEFAULT (datetime('now','localtime'))
            );
            """;
        cmd.ExecuteNonQuery();
        MigrateTerminalsSchema(db);
    }

    // 旧スキーマ(id 列なし)の trade_terminals だけを作り直す。
    // 無条件 DROP にすると InitDb が呼ばれるたびに取得済みターミナルが消えるため、id 列が無い場合に限定する。
    private static void MigrateTerminalsSchema(SqliteConnection db)
    {
        var hasId = false;
        using (var check = db.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(trade_terminals)";
            using var r = check.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), "id", StringComparison.OrdinalIgnoreCase))
                {
                    hasId = true;
                    break;
                }
            }
        }
        if (hasId) return;

        using var mig = db.CreateCommand();
        mig.CommandText = """
            DROP TABLE IF EXISTS trade_terminals;
            CREATE TABLE trade_terminals (id INTEGER DEFAULT 0, name TEXT PRIMARY KEY, has_loading_dock INTEGER, has_docking_port INTEGER, is_cargo_center INTEGER);
            """;
        mig.ExecuteNonQuery();
    }

    private bool TryLoadCache()
    {
        if (_dbPath == null || !File.Exists(_dbPath))
        {
            OnProgress?.Invoke($"キャッシュDB未検出: {_dbPath ?? "(null)"}");
            return false;
        }

        try
        {
            using var db = new SqliteConnection($"Data Source={_dbPath}");
            InitDb(db);

            var fetchedAt = GetMeta(db, "fetched_at");
            if (string.IsNullOrEmpty(fetchedAt))
            {
                OnProgress?.Invoke("キャッシュ: fetched_at が空のため再取得");
                return false;
            }
            var fetchTime = DateTime.Parse(fetchedAt);
            var ageHours = (DateTime.Now - fetchTime).TotalHours;
            if (ageHours > CacheHours)
            {
                OnProgress?.Invoke($"キャッシュ期限切れ: {ageHours:F1}時間経過 (上限{CacheHours}h)");
                return false;
            }

            _allPrices = LoadPricesFromDb(db, isCurrent: true);
            _ships = LoadShipsFromDb(db);
            _terminals = LoadTerminalsFromDb(db);
            _commodities = _allPrices.GroupBy(p => p.CommodityId)
                .ToDictionary(g => g.Key, g => new CommodityInfo { Id = g.Key, Name = g.First().CommodityName, Kind = g.First().CommodityKind, Scu = g.First().ContainerScu });
            _lastPriceUpdate = fetchTime;
            return _allPrices.Count > 0;
        }
        catch (Exception ex)
        {
            OnProgress?.Invoke($"キャッシュ読込エラー: {ex.Message}");
            return false;
        }
    }

    private void SaveCache()
    {
        if (_dbPath == null) return;

        try
        {
            using var db = new SqliteConnection($"Data Source={_dbPath}");
            InitDb(db);

            using var tx = db.BeginTransaction();

            // Mark previous current data as historical
            Exec(db, "UPDATE trade_prices SET is_current = 0 WHERE is_current = 1");
            // Insert new prices
            foreach (var p in _allPrices)
            {
                Exec(db, """
                    INSERT INTO trade_prices (commodity_id,commodity_name,commodity_kind,container_scu,
                        terminal,city,outpost,moon,planet,star_system,location_short,
                        price_buy,price_sell,price_buy_avg,price_sell_avg,
                        scu_buy,scu_sell,scu_buy_avg,scu_sell_avg,date_modified,fetched_at,patch,is_current)
                    VALUES (@cid,@cn,@ck,@cs,@t,@ci,@o,@m,@p,@ss,@ls,@pb,@ps,@pba,@psa,@sb,@sl,@sba,@ssa,@dm,@fa,@pa,1)
                    """,
                    ("@cid", p.CommodityId), ("@cn", p.CommodityName), ("@ck", p.CommodityKind), ("@cs", p.ContainerScu),
                    ("@t", p.Terminal), ("@ci", p.City), ("@o", p.Outpost), ("@m", p.Moon), ("@p", p.Planet),
                    ("@ss", p.StarSystem), ("@ls", p.LocationShort),
                    ("@pb", p.PriceBuy), ("@ps", p.PriceSell), ("@pba", p.PriceBuyAvg), ("@psa", p.PriceSellAvg),
                    ("@sb", p.ScuBuy), ("@sl", p.ScuSell), ("@sba", p.ScuBuyAvg), ("@ssa", p.ScuSellAvg),
                    ("@dm", p.DateModified), ("@fa", _lastPriceUpdate.ToString("o")), ("@pa", GamePatch));
            }

            // Clean old history (keep last 2 fetches only)
            Exec(db, """
                DELETE FROM trade_prices WHERE fetched_at NOT IN (
                    SELECT DISTINCT fetched_at FROM trade_prices ORDER BY fetched_at DESC LIMIT 2)
                """);

            // Ships
            Exec(db, "DELETE FROM trade_ships");
            foreach (var s in _ships)
                Exec(db, "INSERT INTO trade_ships VALUES (@n,@m,@s,@f)",
                    ("@n", s.Name), ("@m", s.Manufacturer), ("@s", s.Scu), ("@f", _lastPriceUpdate.ToString("o")));

            // Terminals
            Exec(db, "DELETE FROM trade_terminals");
            foreach (var t in _terminals.Values)
                Exec(db, "INSERT OR REPLACE INTO trade_terminals VALUES (@id,@n,@ld,@dp,@cc)",
                    ("@id", t.Id), ("@n", t.Name), ("@ld", t.HasLoadingDock ? 1 : 0), ("@dp", t.HasDockingPort ? 1 : 0), ("@cc", t.IsCargoCenter ? 1 : 0));

            SetMeta(db, "fetched_at", _lastPriceUpdate.ToString("o"));
            tx.Commit();
            OnProgress?.Invoke($"キャッシュ保存完了 ({_allPrices.Count:N0} 件)");

            // Detect missing routes
            DetectMissingRoutes(db);
        }
        catch (Exception ex)
        {
            OnProgress?.Invoke($"キャッシュ保存エラー: {ex.Message}");
        }
    }

    private void DetectMissingRoutes(SqliteConnection db)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT DISTINCT commodity_name, star_system, location_short FROM trade_prices
                WHERE is_current = 0 AND price_buy > 0
                AND commodity_id || '_' || terminal NOT IN (
                    SELECT commodity_id || '_' || terminal FROM trade_prices WHERE is_current = 1 AND price_buy > 0)
                """;
            using var r = cmd.ExecuteReader();
            int count = 0;
            while (r.Read()) count++;
            MissingCount = count;
        }
        catch { MissingCount = 0; }
    }

    private static List<CommodityPriceEntry> LoadPricesFromDb(SqliteConnection db, bool isCurrent)
    {
        var list = new List<CommodityPriceEntry>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT * FROM trade_prices WHERE is_current = {(isCurrent ? 1 : 0)}";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var terminal = r.GetString(r.GetOrdinal("terminal"));
            var city = r.GetString(r.GetOrdinal("city"));
            var outpost = r.GetString(r.GetOrdinal("outpost"));
            var moon = r.GetString(r.GetOrdinal("moon"));
            var planet = r.GetString(r.GetOrdinal("planet"));
            list.Add(new CommodityPriceEntry
            {
                CommodityId = r.GetInt32(r.GetOrdinal("commodity_id")),
                CommodityName = r.GetString(r.GetOrdinal("commodity_name")),
                CommodityKind = r.GetString(r.GetOrdinal("commodity_kind")),
                ContainerScu = r.GetInt32(r.GetOrdinal("container_scu")),
                Terminal = terminal,
                City = city,
                Outpost = outpost,
                Moon = moon,
                Planet = planet,
                StarSystem = r.GetString(r.GetOrdinal("star_system")),
                LocationShort = BuildLocationShort(city, outpost, terminal, moon, planet),
                PriceBuy = r.GetDouble(r.GetOrdinal("price_buy")),
                PriceSell = r.GetDouble(r.GetOrdinal("price_sell")),
                PriceBuyAvg = r.GetDouble(r.GetOrdinal("price_buy_avg")),
                PriceSellAvg = r.GetDouble(r.GetOrdinal("price_sell_avg")),
                ScuBuy = r.GetInt32(r.GetOrdinal("scu_buy")),
                ScuSell = r.GetInt32(r.GetOrdinal("scu_sell")),
                ScuBuyAvg = r.GetInt32(r.GetOrdinal("scu_buy_avg")),
                ScuSellAvg = r.GetInt32(r.GetOrdinal("scu_sell_avg")),
                DateModified = r.IsDBNull(r.GetOrdinal("date_modified")) ? "" : r.GetString(r.GetOrdinal("date_modified")),
            });
        }
        return list;
    }

    private static List<ShipInfo> LoadShipsFromDb(SqliteConnection db)
    {
        var list = new List<ShipInfo>();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, manufacturer, scu FROM trade_ships ORDER BY name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ShipInfo { Name = r.GetString(0), Manufacturer = r.GetString(1), Scu = r.GetInt32(2) });
        return list;
    }

    private static Dictionary<string, TerminalInfo> LoadTerminalsFromDb(SqliteConnection db)
    {
        var dict = new Dictionary<string, TerminalInfo>(StringComparer.OrdinalIgnoreCase);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name, has_loading_dock, has_docking_port, is_cargo_center, COALESCE(id, 0) FROM trade_terminals";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var name = r.GetString(0);
            dict[name] = new TerminalInfo
            {
                Name = name,
                HasLoadingDock = r.GetInt32(1) == 1,
                HasDockingPort = r.GetInt32(2) == 1,
                IsCargoCenter = r.GetInt32(3) == 1,
                Id = r.GetInt32(4),
            };
        }
        return dict;
    }

    private static string GetMeta(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM trade_meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static void SetMeta(SqliteConnection db, string key, string value)
    {
        Exec(db, "INSERT OR REPLACE INTO trade_meta VALUES (@k, @v)", ("@k", key), ("@v", value));
    }

    private static void Exec(SqliteConnection db, string sql, params (string name, object value)[] parms)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parms)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    // === Capture Accessors ===

    public Dictionary<string, int> GetTerminalNameToIdMap()
        => _terminals.ToDictionary(kv => kv.Key, kv => kv.Value.Id, StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> GetCommodityNameToIdMap()
        => _commodities.Values.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

    public Dictionary<int, CommodityInfo> GetCommodities() => _commodities;

    public Dictionary<string, TerminalInfo> GetTerminals() => _terminals;

    // === Commodity List & Detail ===

    public List<string> GetCommodityNames()
    {
        return _allPrices
            .Select(p => p.CommodityName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();
    }

    public (List<CommodityPriceEntry> buyLocations, List<CommodityPriceEntry> sellLocations) GetCommodityDetail(string commodityName)
    {
        var entries = _allPrices.Where(p => p.CommodityName.Equals(commodityName, StringComparison.OrdinalIgnoreCase)).ToList();
        var buy = entries.Where(p => p.PriceBuy > 0).OrderBy(p => p.PriceBuy).ToList();
        var sell = entries.Where(p => p.PriceSell > 0).OrderByDescending(p => p.PriceSell).ToList();
        return (buy, sell);
    }

    public List<CommodityPriceEntry> GetBuyableAtLocation(string terminal)
    {
        return _allPrices
            .Where(p => p.Terminal.Equals(terminal, StringComparison.OrdinalIgnoreCase) && p.PriceBuy > 0)
            .OrderBy(p => p.CommodityName)
            .ToList();
    }

    public List<CommodityPriceEntry> GetSellableAtLocation(string terminal)
    {
        return _allPrices
            .Where(p => p.Terminal.Equals(terminal, StringComparison.OrdinalIgnoreCase) && p.PriceSell > 0)
            .OrderBy(p => p.CommodityName)
            .ToList();
    }

    // === My Ships (所持船管理) ===

    private List<MyShipEntry> _myShips = new();
    public List<MyShipEntry> MyShips => _myShips;

    public void LoadMyShips()
    {
        if (_dbPath == null || !File.Exists(_dbPath)) { _myShips = new(); return; }
        try
        {
            using var db = new SqliteConnection($"Data Source={_dbPath}");
            InitDb(db);
            var list = new List<MyShipEntry>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, name, manufacturer, scu, notes, added_at FROM my_ships ORDER BY name";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new MyShipEntry
                {
                    Id = r.GetInt32(0),
                    Name = r.GetString(1),
                    Manufacturer = r.IsDBNull(2) ? "" : r.GetString(2),
                    Scu = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    Notes = r.IsDBNull(4) ? "" : r.GetString(4),
                    AddedAt = r.IsDBNull(5) ? "" : r.GetString(5),
                });
            _myShips = list;
        }
        catch { _myShips = new(); }
    }

    public void AddMyShip(string name, string manufacturer, int scu, string notes = "")
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        Exec(db, "INSERT INTO my_ships (name, manufacturer, scu, notes) VALUES (@n, @m, @s, @no)",
            ("@n", name), ("@m", manufacturer), ("@s", scu), ("@no", notes));
        LoadMyShips();
    }

    public void UpdateMyShip(int id, string name, string manufacturer, int scu, string notes)
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        Exec(db, "UPDATE my_ships SET name=@n, manufacturer=@m, scu=@s, notes=@no WHERE id=@id",
            ("@n", name), ("@m", manufacturer), ("@s", scu), ("@no", notes), ("@id", id));
        LoadMyShips();
    }

    public void DeleteMyShip(int id)
    {
        if (_dbPath == null) return;
        using var db = new SqliteConnection($"Data Source={_dbPath}");
        InitDb(db);
        Exec(db, "DELETE FROM my_ships WHERE id=@id", ("@id", id));
        LoadMyShips();
    }

    public ShipInfo? FindUexShip(string name)
    {
        return _ships.FirstOrDefault(s =>
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            s.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    public string ResolveVehicleName(string rawName, Dictionary<string, string>? localization = null)
    {
        if (string.IsNullOrEmpty(rawName)) return rawName;
        // Handle @vehicle_Name prefix
        if (rawName.StartsWith("@"))
        {
            var key = rawName.TrimStart('@');
            if (localization != null && localization.TryGetValue(key, out var locName))
                return locName;
            // Try stripping prefix and converting: vehicle_NameDRAK_Pitbull -> Pitbull
            if (key.StartsWith("vehicle_Name", StringComparison.OrdinalIgnoreCase))
            {
                var rest = key["vehicle_Name".Length..];
                var parts = rest.Split('_', 2);
                if (parts.Length >= 2) return parts[1].Replace("_", " ");
                return rest.Replace("_", " ");
            }
        }
        return rawName;
    }

    // === Helpers ===

    private bool IsSmallGroundOutpost(CommodityPriceEntry e) =>
        !string.IsNullOrEmpty(e.Outpost) && string.IsNullOrEmpty(e.City);

    private bool HasLoadingDock(CommodityPriceEntry e) =>
        _terminals.TryGetValue(e.Terminal, out var info)
            ? info.HasLoadingDock || info.HasDockingPort
            : !string.IsNullOrEmpty(e.City);

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int GetInt(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;

    private static double GetDouble(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool GetBool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;
}

// === Data Models ===

public class CommodityInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Scu { get; set; }
}

public class CommodityPriceEntry
{
    public int CommodityId { get; set; }
    public string CommodityName { get; set; } = "";
    public string CommodityKind { get; set; } = "";
    public int ContainerScu { get; set; }
    public string Terminal { get; set; } = "";
    public string City { get; set; } = "";
    public string Outpost { get; set; } = "";
    public string Moon { get; set; } = "";
    public string Planet { get; set; } = "";
    public string StarSystem { get; set; } = "";
    public string LocationShort { get; set; } = "";
    public double PriceBuy { get; set; }
    public double PriceSell { get; set; }
    public double PriceBuyAvg { get; set; }
    public double PriceSellAvg { get; set; }
    public int ScuBuy { get; set; }
    public int ScuSell { get; set; }
    public int ScuBuyAvg { get; set; }
    public int ScuSellAvg { get; set; }
    public string DateModified { get; set; } = "";
}

public class TerminalInfo
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool HasLoadingDock { get; set; }
    public bool HasDockingPort { get; set; }
    public bool IsCargoCenter { get; set; }
}

public class ShipInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public int Scu { get; set; }
    public string DisplayName => $"{Name} ({Scu} SCU)";
    public override string ToString() => DisplayName;
}

public class TradeRoute
{
    public string CommodityName { get; set; } = "";
    public string CommodityKind { get; set; } = "";
    public int ContainerScu { get; set; }
    public string BuyLocation { get; set; } = "";
    public string BuyTerminal { get; set; } = "";
    public string BuySystem { get; set; } = "";
    public string BuyPlanet { get; set; } = "";
    public double BuyPrice { get; set; }
    public double BuyPriceAvg { get; set; }
    public string SellLocation { get; set; } = "";
    public string SellTerminal { get; set; } = "";
    public string SellSystem { get; set; } = "";
    public string SellPlanet { get; set; } = "";
    public double SellPrice { get; set; }
    public double SellPriceAvg { get; set; }
    public double ProfitPerScu { get; set; }
    public int ActualScu { get; set; }
    public double Investment { get; set; }
    public double TotalProfit { get; set; }
    public double Roi { get; set; }
    public int ScuBuyStock { get; set; }
    public int ScuSellStock { get; set; }
    public int ScuBuyAvg { get; set; }
    public int ScuSellAvg { get; set; }
    public bool IsLowBuyStock { get; set; }
    public bool IsNoRecord { get; set; }

    // Display properties
    public string ProfitDisplay => ProfitPerScu >= 0 ? $"+{ProfitPerScu:N1}" : $"{ProfitPerScu:N1}";
    public string TotalProfitDisplay => TotalProfit >= 0 ? $"+{TotalProfit:N0}" : $"{TotalProfit:N0}";
    public string RoiDisplay => $"{Roi:N1}%";
    public string BuyDisplay => BuyLocation.Contains($"({BuySystem})") ? BuyLocation : $"{BuyLocation} ({BuySystem})";
    public string SellDisplay => SellLocation.Contains($"({SellSystem})") ? SellLocation : $"{SellLocation} ({SellSystem})";
    public string BuyPriceDisplay => $"{BuyPrice:N1}";
    public string SellPriceDisplay => $"{SellPrice:N1}";
    public string InvestmentDisplay => $"{Investment:N0}";
    public string ActualScuDisplay => $"{ActualScu:N0}";
    public string ContainerScuDisplay => ContainerScu > 0 ? $"{ContainerScu}" : "-";
    public string BuyStockDisplay => ScuBuyStock > 0 ? $"{ScuBuyStock:N0}" : "-";
    public string SellStockDisplay => ScuSellStock > 0 ? $"{ScuSellStock:N0}" : "-";
    public string BuyStockAvgDisplay => ScuBuyAvg > 0 ? $"{ScuBuyAvg:N0}" : "-";
    public string StockWarning => IsNoRecord ? "実績なし" : (IsLowBuyStock ? "⚠在庫不足" : "");
}

public class MyShipEntry
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public int Scu { get; set; }
    public string Notes { get; set; } = "";
    public string AddedAt { get; set; } = "";
    public string DisplayName => Scu > 0 ? $"{Name} ({Scu} SCU)" : Name;
    public override string ToString() => DisplayName;
}

