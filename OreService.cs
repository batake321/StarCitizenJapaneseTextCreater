using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// === ［鉱石］タブのモデル ===

// 資源の在庫 (ore_stock) の 1 行。「資源 × 品質 × 単位」ごとに量を持つ (同じ組は 1 行にまとめる)。
// 品質は 0〜1000 の整数 (NULL = 不明)。単位は積み荷 "SCU" か手掘りの鉱石 "個" (OreService.UnitScu / UnitPiece)
public sealed class OreStock
{
    public int Id { get; set; }
    public string ResourceUuid { get; set; } = "";     // resource_types.uuid (商品売買のログの resourceGUID と同じ)
    public string ResourceRecord { get; set; } = "";   // resource_types.record_name (ResourceType.…。名前を引けないときの表示用)
    public int? Quality { get; set; }
    public double Quantity { get; set; }
    public string Unit { get; set; } = OreService.UnitScu;
    public string Notes { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

// 商品売買の記録 (commodity_trades) の 1 行。主キーは (log_time, player_id, shop_id, kiosk_id, kind, resource_uuid, quantity_raw)。
// kind は "buy" | "sell"、result は '' が成功 (失敗はログの結果の文字)、source は "log" | "manual"。
// RemainingScu (買いの残り)・CostAuec・ProfitAuec・ProfitRate・MatchedScu (売り) は OreService.RecomputeProfits が書く
public sealed class CommodityTrade
{
    public string LogTime { get; set; } = "";          // 行頭の時刻の文字 (UTC。"2026-10-15T13:41:22.633")
    public string PlayerId { get; set; } = "";
    public string ShopId { get; set; } = "";
    public string KioskId { get; set; } = "";
    public string ShopName { get; set; } = "";
    public string Kind { get; set; } = "buy";
    public string ResourceUuid { get; set; } = "";
    public string ResourceRecord { get; set; } = "";
    public double? QuantityScu { get; set; }
    public string QuantityRaw { get; set; } = "";      // ログの quantity の文字そのまま (主キーの一部)
    public double? TotalAuec { get; set; }
    public double? UnitPrice { get; set; }             // 合計 ÷ SCU (入れるときに計算。SCU が無ければ NULL)
    public string TransactionMode { get; set; } = "";
    public string Result { get; set; } = "";
    public string Source { get; set; } = "log";
    public double? RemainingScu { get; set; }
    public double? CostAuec { get; set; }
    public double? ProfitAuec { get; set; }
    public double? ProfitRate { get; set; }
    public double? MatchedScu { get; set; }
    public bool AppliedToStock { get; set; }
    public string AddedAt { get; set; } = "";

    public bool IsBuy => Kind == "buy";
    public bool Succeeded => Result.Length == 0;
}

// 採掘の記録 (mining_log) の 1 行。主キーは (log_time, mineral_class, entity_id)。method は "hand" | "ship" | "manual"
public sealed class MiningLog
{
    public string LogTime { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string ResourceUuid { get; set; } = "";     // 引けなかったときは '' (在庫には入れない)
    public string ResourceRecord { get; set; } = "";
    public string MineralClass { get; set; } = "";     // 手掘りの鉱石のクラス名 (Harvestable_Mineral_1H_<X>)。手入力は ''
    public string EntityId { get; set; } = "";         // 個体の ID。手入力は ''
    public double Quantity { get; set; }
    public string Unit { get; set; } = OreService.UnitPiece;
    public int? Quality { get; set; }
    public string Method { get; set; } = "hand";
    public string Source { get; set; } = "log";
    public bool AppliedToStock { get; set; }
    public string AddedAt { get; set; } = "";
}

// 資源の種類 (resource_types) の 1 件と、その表示名・グループ名
public sealed class OreResource
{
    public string Uuid { get; set; } = "";
    public string RecordName { get; set; } = "";
    public string? NameKey { get; set; }
    public string? NameJa { get; set; }
    public string? NameEn { get; set; }
    public string GroupRecord { get; set; } = "";      // ResourceTypeGroup.… ('' = 不明。前の版の DB)
    public string GroupName { get; set; } = "";        // グループの日本語名 ('' = 不明)
    // ［鉱石］タブの資源の一覧に出すか (OreService.IsOreListed)
    public bool InOreList { get; set; }

    // 「日本語 (英語)」。片方しか無ければある方、両方無ければ record_name の "ResourceType." を除いた名前
    public string Display => OreService.Label(NameJa, NameEn) ?? OreService.ShortRecord(RecordName);
}

// ［鉱石］タブのデータ (trade_cache.db の ore_stock / commodity_trades / mining_log) と、資源の一覧 (gamedata_cache.db の resource_types)。
// HangarService / TradeService と同じく作業ディレクトリの trade_cache.db を SetCacheDir で指し、読むたびに開いて閉じる。
// 表の DDL (TradeTablesDdl) は HangarService.InitDb と DatabaseBackupService.EnsureTradeSchema からも使う (同じ形を 1 か所に置く)
public sealed class OreService
{
    public const string UnitScu = "SCU";    // 積み荷
    public const string UnitPiece = "個";   // 手掘りの鉱石

    // 3 つの表。ore_stock の「資源 × 品質 × 単位」の一意性は、入れるときに同じ組を探して合算すること (AddOrMergeStock / UpdateStock / Adjust) で守る
    // (NULL の品質は UNIQUE 制約では区別できないため)
    internal const string TradeTablesDdl = """

        CREATE TABLE IF NOT EXISTS ore_stock (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            resource_uuid TEXT NOT NULL,
            resource_record TEXT DEFAULT '',
            quality INTEGER,
            quantity REAL NOT NULL DEFAULT 0,
            unit TEXT NOT NULL DEFAULT 'SCU',
            notes TEXT DEFAULT '',
            updated_at TEXT DEFAULT (datetime('now','localtime'))
        );
        CREATE TABLE IF NOT EXISTS commodity_trades (
            log_time TEXT NOT NULL,
            player_id TEXT NOT NULL DEFAULT '',
            shop_id TEXT NOT NULL DEFAULT '',
            kiosk_id TEXT NOT NULL DEFAULT '',
            shop_name TEXT DEFAULT '',
            kind TEXT NOT NULL,
            resource_uuid TEXT NOT NULL,
            resource_record TEXT DEFAULT '',
            quantity_scu REAL,
            quantity_raw TEXT NOT NULL DEFAULT '',
            total_auec REAL,
            unit_price REAL,
            transaction_mode TEXT DEFAULT '',
            result TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL DEFAULT 'log',
            remaining_scu REAL,
            cost_auec REAL,
            profit_auec REAL,
            profit_rate REAL,
            matched_scu REAL,
            applied_to_stock INTEGER NOT NULL DEFAULT 0,
            added_at TEXT DEFAULT (datetime('now','localtime')),
            PRIMARY KEY (log_time, player_id, shop_id, kiosk_id, kind, resource_uuid, quantity_raw)
        );
        CREATE TABLE IF NOT EXISTS mining_log (
            log_time TEXT NOT NULL,
            player_name TEXT DEFAULT '',
            resource_uuid TEXT NOT NULL DEFAULT '',
            resource_record TEXT DEFAULT '',
            mineral_class TEXT NOT NULL DEFAULT '',
            entity_id TEXT NOT NULL DEFAULT '',
            quantity REAL NOT NULL DEFAULT 0,
            unit TEXT NOT NULL DEFAULT '個',
            quality INTEGER,
            method TEXT NOT NULL DEFAULT 'hand',
            source TEXT NOT NULL DEFAULT 'log',
            applied_to_stock INTEGER NOT NULL DEFAULT 0,
            added_at TEXT DEFAULT (datetime('now','localtime')),
            PRIMARY KEY (log_time, mineral_class, entity_id)
        );
        """;

    private string? _dbPath;

    public void SetCacheDir(string dir) => _dbPath = Path.Combine(dir, "trade_cache.db");

    private SqliteConnection Open()
    {
        if (_dbPath == null) throw new InvalidOperationException("作業ディレクトリが決まっていません (SetCacheDir)");
        var db = new SqliteConnection($"Data Source={_dbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        // 印の表 (game_log_imports) もここで書くので、HangarService の DDL も通す
        cmd.CommandText = TradeTablesDdl + HangarService.GearImportTablesDdl;
        cmd.ExecuteNonQuery();
        return db;
    }

    // 印 (game_log_imports) の kind・入れた先
    public const string MarkKindTrade = "trade";
    public const string MarkKindMined = "mined";
    public const string MarkTargetStock = "ore_stock";

    private bool DbExists => _dbPath != null && File.Exists(_dbPath);

    // 品質は 0〜1000 の整数か NULL
    public static bool IsValidQuality(int? quality) => quality is null or (>= 0 and <= 1000);

    private static void CheckQuality(int? quality)
    {
        if (!IsValidQuality(quality)) throw new ArgumentOutOfRangeException(nameof(quality), "品質は 0〜1000 の整数です");
    }

    private static void CheckUnit(string unit)
    {
        if (string.IsNullOrWhiteSpace(unit)) throw new ArgumentException("単位が空です", nameof(unit));
    }

    // 手入力の記録の log_time (ログの行頭の時刻と同じ形。UTC)
    public static string NowLogTime() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);

    // log_time ("2026-10-15T13:41:22.633"。UTC) → ローカル時刻の文字。読めなければそのまま
    public static string LogTimeDisplay(string logTime) =>
        TryParseLogTime(logTime, out var t) ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") : logTime;

    public static bool TryParseLogTime(string logTime, out DateTime utc) =>
        DateTime.TryParse(logTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);

    // === 在庫 (ore_stock) ===

    public List<OreStock> LoadStock()
    {
        var list = new List<OreStock>();
        if (!DbExists) return list;
        using var db = Open();
        // 量が 0 以下の行は在庫に出さない。前の版 (0 で止めて行を残していた) が残した 0 の行をここで消す
        Exec(db, "DELETE FROM ore_stock WHERE quantity <= 0");
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, resource_uuid, resource_record, quality, quantity, unit, notes, updated_at FROM ore_stock ORDER BY resource_record, unit, quality, id";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadStock(r));
        return list;
    }

    private static OreStock ReadStock(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(0),
        ResourceUuid = r.IsDBNull(1) ? "" : r.GetString(1),
        ResourceRecord = r.IsDBNull(2) ? "" : r.GetString(2),
        Quality = r.IsDBNull(3) ? null : r.GetInt32(3),
        Quantity = r.IsDBNull(4) ? 0 : r.GetDouble(4),
        Unit = r.IsDBNull(5) ? UnitScu : r.GetString(5),
        Notes = r.IsDBNull(6) ? "" : r.GetString(6),
        UpdatedAt = r.IsDBNull(7) ? "" : r.GetString(7),
    };

    // 同じ組 (資源・品質・単位) の行を探す。excludeId の行は除く。無ければ null
    private static (int Id, double Quantity, string Notes)? FindStock(SqliteConnection db, SqliteTransaction tx, string uuid, int? quality, string unit, int excludeId = 0)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT id, quantity, COALESCE(notes, '') FROM ore_stock
            WHERE resource_uuid = @u COLLATE NOCASE AND IFNULL(quality, -1) = IFNULL(@q, -1) AND unit = @unit AND id != @x
            ORDER BY id LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@u", uuid);
        cmd.Parameters.AddWithValue("@q", (object?)quality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@unit", unit);
        cmd.Parameters.AddWithValue("@x", excludeId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetDouble(1), r.GetString(2)) : null;
    }

    // 追加する。同じ組 (資源・品質・単位) の行が既にあれば、新しい行を作らずその行の量に足す (メモは HangarService.JoinNotes で連結)。
    // 追加した (または足した) 行の id を返し、s.Id にも入れる
    public int AddOrMergeStock(OreStock s)
    {
        CheckQuality(s.Quality);
        CheckUnit(s.Unit);
        using var db = Open();
        using var tx = db.BeginTransaction();
        var id = AddOrMergeStockCore(db, tx, s);
        tx.Commit();
        return id;
    }

    private static int AddOrMergeStockCore(SqliteConnection db, SqliteTransaction tx, OreStock s)
    {
        if (FindStock(db, tx, s.ResourceUuid, s.Quality, s.Unit) is { } found)
        {
            Exec(db, tx, "UPDATE ore_stock SET quantity = quantity + @q, notes = @o, updated_at = datetime('now','localtime') WHERE id = @id",
                ("@q", s.Quantity), ("@o", HangarService.JoinNotes([found.Notes, s.Notes ?? ""])), ("@id", found.Id));
            s.Id = found.Id;
            return found.Id;
        }
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO ore_stock (resource_uuid, resource_record, quality, quantity, unit, notes)
            VALUES (@u, @r, @q, @n, @unit, @o);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@u", s.ResourceUuid);
        cmd.Parameters.AddWithValue("@r", s.ResourceRecord ?? "");
        cmd.Parameters.AddWithValue("@q", (object?)s.Quality ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@n", s.Quantity);
        cmd.Parameters.AddWithValue("@unit", s.Unit);
        cmd.Parameters.AddWithValue("@o", s.Notes ?? "");
        var id = (int)(long)(cmd.ExecuteScalar() ?? 0L);
        s.Id = id;
        return id;
    }

    // 量・品質・単位・メモを書き換える。品質 (または単位) を変えた結果、同じ組の別の行ができたら、その行に量を足してこの行を消す
    // (メモは連結)。残った行の id を返す
    public int UpdateStock(OreStock s)
    {
        CheckQuality(s.Quality);
        CheckUnit(s.Unit);
        using var db = Open();
        using var tx = db.BeginTransaction();
        int id;
        if (FindStock(db, tx, s.ResourceUuid, s.Quality, s.Unit, excludeId: s.Id) is { } other)
        {
            Exec(db, tx, "UPDATE ore_stock SET quantity = quantity + @q, notes = @o, updated_at = datetime('now','localtime') WHERE id = @id",
                ("@q", s.Quantity), ("@o", HangarService.JoinNotes([other.Notes, s.Notes ?? ""])), ("@id", other.Id));
            Exec(db, tx, "DELETE FROM ore_stock WHERE id = @id", ("@id", s.Id));
            id = other.Id;
        }
        else
        {
            Exec(db, tx, "UPDATE ore_stock SET quality = @q, quantity = @n, unit = @unit, notes = @o, updated_at = datetime('now','localtime') WHERE id = @id",
                ("@q", (object?)s.Quality ?? DBNull.Value), ("@n", s.Quantity), ("@unit", s.Unit), ("@o", s.Notes ?? ""), ("@id", s.Id));
            id = s.Id;
        }
        tx.Commit();
        return id;
    }

    public void DeleteStock(int id)
    {
        if (!DbExists) return;
        using var db = Open();
        Exec(db, "DELETE FROM ore_stock WHERE id = @id", ("@id", id));
    }

    // 在庫の増減。delta が正なら足す (行が無ければ作る)。負なら引くが、0 より下には下げない (0 で止める)。
    // 引いた結果が 0 になった行は消す (0 の行を在庫に残さない)。戻り値は止めた量 (引ききれなかった量。全部引けたら 0)
    public double AdjustStock(string resourceUuid, string resourceRecord, int? quality, string unit, double delta)
    {
        CheckQuality(quality);
        CheckUnit(unit);
        using var db = Open();
        using var tx = db.BeginTransaction();
        var clamped = AdjustStockCore(db, tx, resourceUuid, resourceRecord, quality, unit, delta);
        tx.Commit();
        return clamped;
    }

    private static double AdjustStockCore(SqliteConnection db, SqliteTransaction tx, string resourceUuid, string resourceRecord, int? quality, string unit, double delta)
    {
        var found = FindStock(db, tx, resourceUuid, quality, unit);
        if (delta >= 0)
        {
            if (found is { } f)
                Exec(db, tx, "UPDATE ore_stock SET quantity = quantity + @q, updated_at = datetime('now','localtime') WHERE id = @id", ("@q", delta), ("@id", f.Id));
            else if (delta > 0)
                AddOrMergeStockCore(db, tx, new OreStock { ResourceUuid = resourceUuid, ResourceRecord = resourceRecord, Quality = quality, Quantity = delta, Unit = unit });
            return 0;
        }
        if (found is not { } row) return -delta;   // 行が無い: 何も引けない
        var next = row.Quantity + delta;
        var clamped = next < 0 ? -next : 0;
        if (next <= 0)
            Exec(db, tx, "DELETE FROM ore_stock WHERE id = @id", ("@id", row.Id));   // 0 になった行は残さない
        else
            Exec(db, tx, "UPDATE ore_stock SET quantity = @q, updated_at = datetime('now','localtime') WHERE id = @id", ("@q", next), ("@id", row.Id));
        return clamped;
    }

    // === 売買の記録 (commodity_trades) ===

    private const string TradeColumns = "log_time, player_id, shop_id, kiosk_id, shop_name, kind, resource_uuid, resource_record, quantity_scu, quantity_raw, total_auec, unit_price, transaction_mode, result, source, remaining_scu, cost_auec, profit_auec, profit_rate, matched_scu, applied_to_stock, added_at";

    public List<CommodityTrade> LoadTrades()
    {
        var list = new List<CommodityTrade>();
        if (!DbExists) return list;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {TradeColumns} FROM commodity_trades ORDER BY log_time DESC, rowid DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadTrade(r));
        return list;
    }

    private static CommodityTrade ReadTrade(SqliteDataReader r) => new()
    {
        LogTime = Str(r, 0), PlayerId = Str(r, 1), ShopId = Str(r, 2), KioskId = Str(r, 3), ShopName = Str(r, 4), Kind = Str(r, 5),
        ResourceUuid = Str(r, 6), ResourceRecord = Str(r, 7), QuantityScu = Dbl(r, 8), QuantityRaw = Str(r, 9), TotalAuec = Dbl(r, 10),
        UnitPrice = Dbl(r, 11), TransactionMode = Str(r, 12), Result = Str(r, 13), Source = Str(r, 14), RemainingScu = Dbl(r, 15),
        CostAuec = Dbl(r, 16), ProfitAuec = Dbl(r, 17), ProfitRate = Dbl(r, 18), MatchedScu = Dbl(r, 19),
        AppliedToStock = !r.IsDBNull(20) && r.GetInt64(20) != 0, AddedAt = Str(r, 21),
    };

    // 主キーで二重を防いで入れる (INSERT OR IGNORE)。入れた件数を返す。unit_price が無ければ合計 ÷ SCU で入れる
    public int InsertTradesIgnore(IEnumerable<CommodityTrade> trades)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO commodity_trades (log_time, player_id, shop_id, kiosk_id, shop_name, kind, resource_uuid, resource_record,
                quantity_scu, quantity_raw, total_auec, unit_price, transaction_mode, result, source)
            VALUES (@t, @p, @s, @k, @sn, @kind, @u, @r, @scu, @raw, @total, @unit, @mode, @res, @src)
            """;
        var pT = cmd.Parameters.Add("@t", SqliteType.Text);
        var pP = cmd.Parameters.Add("@p", SqliteType.Text);
        var pS = cmd.Parameters.Add("@s", SqliteType.Text);
        var pK = cmd.Parameters.Add("@k", SqliteType.Text);
        var pSn = cmd.Parameters.Add("@sn", SqliteType.Text);
        var pKind = cmd.Parameters.Add("@kind", SqliteType.Text);
        var pU = cmd.Parameters.Add("@u", SqliteType.Text);
        var pR = cmd.Parameters.Add("@r", SqliteType.Text);
        var pScu = cmd.Parameters.Add("@scu", SqliteType.Real);
        var pRaw = cmd.Parameters.Add("@raw", SqliteType.Text);
        var pTotal = cmd.Parameters.Add("@total", SqliteType.Real);
        var pUnit = cmd.Parameters.Add("@unit", SqliteType.Real);
        var pMode = cmd.Parameters.Add("@mode", SqliteType.Text);
        var pRes = cmd.Parameters.Add("@res", SqliteType.Text);
        var pSrc = cmd.Parameters.Add("@src", SqliteType.Text);
        int count = 0;
        foreach (var t in trades)
        {
            if (t.Kind != "buy" && t.Kind != "sell") throw new ArgumentException($"kind が buy / sell でありません: {t.Kind}");
            var unitPrice = t.UnitPrice ?? (t.TotalAuec is { } total && t.QuantityScu is > 0 and var scu ? total / scu : (double?)null);
            pT.Value = t.LogTime;
            pP.Value = t.PlayerId ?? "";
            pS.Value = t.ShopId ?? "";
            pK.Value = t.KioskId ?? "";
            pSn.Value = t.ShopName ?? "";
            pKind.Value = t.Kind;
            pU.Value = t.ResourceUuid ?? "";
            pR.Value = t.ResourceRecord ?? "";
            pScu.Value = (object?)t.QuantityScu ?? DBNull.Value;
            pRaw.Value = t.QuantityRaw ?? "";
            pTotal.Value = (object?)t.TotalAuec ?? DBNull.Value;
            pUnit.Value = (object?)unitPrice ?? DBNull.Value;
            pMode.Value = t.TransactionMode ?? "";
            pRes.Value = t.Result ?? "";
            pSrc.Value = string.IsNullOrEmpty(t.Source) ? "log" : t.Source;
            count += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return count;
    }

    // 利益の計算 (全件を計算し直す)。同じ資源 (resource_uuid。プレイヤー ID は束ねる) の成功した (result = '') 買いを log_time の順に
    // 先入れ先出しで売りに当て、買いには remaining_scu (まだ売っていない SCU)、売りには matched_scu (当てた SCU)・cost_auec (当てた買いの
    // 単価 × SCU の合計)・profit_auec (合計 − 仕入れ)・profit_rate (利益 ÷ 仕入れ。当てた買いが無い・仕入れが 0 なら NULL) を書く。
    // 売りより後の買いは当てない。買いが足りない分は仕入れ 0。失敗した行 (result != '') は計算に入れず 5 つの欄を NULL にする。
    // 戻り値は利益を書いた売りの件数
    public int RecomputeProfits()
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var rows = new List<(long RowId, string Kind, string Uuid, double? Scu, double? Total, double? UnitPrice, bool Ok)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT rowid, kind, resource_uuid, quantity_scu, total_auec, unit_price, result FROM commodity_trades ORDER BY log_time, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), Str(r, 1), Str(r, 2), Dbl(r, 3), Dbl(r, 4), Dbl(r, 5), Str(r, 6).Length == 0));
        }
        // 資源ごとの買いの待ち行列 (rowid, 残り SCU, 単価)
        var queues = new Dictionary<string, List<(long RowId, double Remaining, double UnitPrice)>>(StringComparer.OrdinalIgnoreCase);
        var remaining = new Dictionary<long, double?>();
        var sells = new Dictionary<long, (double Matched, double Cost, double? Profit, double? Rate)>();
        int sellCount = 0;
        foreach (var row in rows)
        {
            if (!row.Ok) continue;
            if (row.Kind == "buy")
            {
                var unit = row.UnitPrice ?? (row.Total is { } t && row.Scu is > 0 and var s ? t / s : (double?)null);
                if (row.Scu is not > 0 || unit is not { } price) { remaining[row.RowId] = row.Scu; continue; }   // 量・単価が無い買いは当てられない
                if (!queues.TryGetValue(row.Uuid, out var q)) queues[row.Uuid] = q = new();
                q.Add((row.RowId, row.Scu.Value, price));
                remaining[row.RowId] = row.Scu.Value;
            }
            else if (row.Kind == "sell")
            {
                double matched = 0, cost = 0;
                var need = row.Scu is > 0 ? row.Scu.Value : 0;
                if (need > 0 && queues.TryGetValue(row.Uuid, out var q))
                {
                    for (int i = 0; i < q.Count && need > 1e-9; i++)
                    {
                        var b = q[i];
                        if (b.Remaining <= 1e-9) continue;
                        var take = Math.Min(b.Remaining, need);
                        matched += take;
                        cost += take * b.UnitPrice;
                        need -= take;
                        q[i] = (b.RowId, b.Remaining - take, b.UnitPrice);
                        remaining[b.RowId] = q[i].Remaining;
                    }
                }
                double? profit = row.Total is { } total ? total - cost : null;
                double? rate = matched > 0 && cost > 0 && profit is { } p ? p / cost : null;
                sells[row.RowId] = (matched, cost, profit, rate);
                sellCount++;
            }
        }
        // 書き込み: まず全行を NULL に戻し (失敗した行・資源の無い行も)、計算した行を入れる
        Exec(db, tx, "UPDATE commodity_trades SET remaining_scu = NULL, cost_auec = NULL, profit_auec = NULL, profit_rate = NULL, matched_scu = NULL");
        using (var up = db.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE commodity_trades SET remaining_scu = @v WHERE rowid = @id";
            var pV = up.Parameters.Add("@v", SqliteType.Real);
            var pId = up.Parameters.Add("@id", SqliteType.Integer);
            foreach (var (id, rem) in remaining)
            {
                pV.Value = (object?)rem ?? DBNull.Value;
                pId.Value = id;
                up.ExecuteNonQuery();
            }
        }
        using (var up = db.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE commodity_trades SET matched_scu = @m, cost_auec = @c, profit_auec = @p, profit_rate = @r WHERE rowid = @id";
            var pM = up.Parameters.Add("@m", SqliteType.Real);
            var pC = up.Parameters.Add("@c", SqliteType.Real);
            var pP = up.Parameters.Add("@p", SqliteType.Real);
            var pR = up.Parameters.Add("@r", SqliteType.Real);
            var pId = up.Parameters.Add("@id", SqliteType.Integer);
            foreach (var (id, v) in sells)
            {
                pM.Value = v.Matched;
                pC.Value = v.Cost;
                pP.Value = (object?)v.Profit ?? DBNull.Value;
                pR.Value = (object?)v.Rate ?? DBNull.Value;
                pId.Value = id;
                up.ExecuteNonQuery();
            }
        }
        tx.Commit();
        return sellCount;
    }

    // === 採掘の記録 (mining_log) ===

    private const string MiningColumns = "log_time, player_name, resource_uuid, resource_record, mineral_class, entity_id, quantity, unit, quality, method, source, applied_to_stock, added_at";

    public List<MiningLog> LoadMining()
    {
        var list = new List<MiningLog>();
        if (!DbExists) return list;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"SELECT {MiningColumns} FROM mining_log ORDER BY log_time DESC, rowid DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMining(r));
        return list;
    }

    private static MiningLog ReadMining(SqliteDataReader r) => new()
    {
        LogTime = Str(r, 0), PlayerName = Str(r, 1), ResourceUuid = Str(r, 2), ResourceRecord = Str(r, 3), MineralClass = Str(r, 4),
        EntityId = Str(r, 5), Quantity = Dbl(r, 6) ?? 0, Unit = Str(r, 7) is { Length: > 0 } u ? u : UnitPiece,
        Quality = r.IsDBNull(8) ? null : r.GetInt32(8), Method = Str(r, 9), Source = Str(r, 10),
        AppliedToStock = !r.IsDBNull(11) && r.GetInt64(11) != 0, AddedAt = Str(r, 12),
    };

    // 主キー (log_time, mineral_class, entity_id) で二重を防いで入れる。入れた件数を返す
    public int InsertMiningIgnore(IEnumerable<MiningLog> entries)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO mining_log (log_time, player_name, resource_uuid, resource_record, mineral_class, entity_id, quantity, unit, quality, method, source)
            VALUES (@t, @pn, @u, @r, @cls, @eid, @q, @unit, @ql, @m, @src)
            """;
        var pT = cmd.Parameters.Add("@t", SqliteType.Text);
        var pPn = cmd.Parameters.Add("@pn", SqliteType.Text);
        var pU = cmd.Parameters.Add("@u", SqliteType.Text);
        var pR = cmd.Parameters.Add("@r", SqliteType.Text);
        var pCls = cmd.Parameters.Add("@cls", SqliteType.Text);
        var pEid = cmd.Parameters.Add("@eid", SqliteType.Text);
        var pQ = cmd.Parameters.Add("@q", SqliteType.Real);
        var pUnit = cmd.Parameters.Add("@unit", SqliteType.Text);
        var pQl = cmd.Parameters.Add("@ql", SqliteType.Integer);
        var pM = cmd.Parameters.Add("@m", SqliteType.Text);
        var pSrc = cmd.Parameters.Add("@src", SqliteType.Text);
        int count = 0;
        foreach (var e in entries)
        {
            CheckQuality(e.Quality);
            pT.Value = e.LogTime;
            pPn.Value = e.PlayerName ?? "";
            pU.Value = e.ResourceUuid ?? "";
            pR.Value = e.ResourceRecord ?? "";
            pCls.Value = e.MineralClass ?? "";
            pEid.Value = e.EntityId ?? "";
            pQ.Value = e.Quantity;
            pUnit.Value = string.IsNullOrEmpty(e.Unit) ? UnitPiece : e.Unit;
            pQl.Value = (object?)e.Quality ?? DBNull.Value;
            pM.Value = string.IsNullOrEmpty(e.Method) ? "hand" : e.Method;
            pSrc.Value = string.IsNullOrEmpty(e.Source) ? "log" : e.Source;
            count += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return count;
    }

    // === 記録の在庫への反映 ===

    // まだ反映していない (applied_to_stock = 0) 記録を log_time の順に在庫へ入れる:
    //   売買: 成功した (result = '') 行の 買い +quantity_scu / 売り −quantity_scu (品質 NULL、単位 SCU)。失敗した行・SCU の無い行は在庫を変えずに反映済みにする
    //   採掘: +quantity (単位は行の unit (既定 個)、品質は行の quality)。資源を引けていない行 (resource_uuid = '') は反映せず 0 のまま残す
    // stockCutoffUtc より前の行 (log_time で比べる) は在庫を変えずに反映済みにする (過去のログの記録は残すが、在庫は動かさない。null なら全部反映)。
    // 在庫を変えた行には印 (game_log_imports: kind trade / mined、入れた先 ore_stock、quantity は変えた量 (売りは負)) を書く
    // (［ゲームログ］の「取り消す」・取引の失敗の後付けで戻すため)。
    // 在庫は 0 より下に下げない (AdjustStockCore)。戻り値は (反映した売買の件数, 反映した採掘の件数, 0 で止めた量の合計)
    public (int Trades, int Mining, double Clamped) ApplyToStock(DateTime? stockCutoffUtc = null)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        int trades = 0, mining = 0;
        double clamped = 0;
        bool AfterCutoff(string logTime) => stockCutoffUtc is not { } c || (TryParseLogTime(logTime, out var t) && t >= c);

        var pending = new List<(long RowId, string LogTime, string PlayerId, string ShopId, string KioskId, string Kind, string Uuid, string Record, double? Scu, string Raw, bool Ok)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT rowid, log_time, player_id, shop_id, kiosk_id, kind, resource_uuid, resource_record, quantity_scu, quantity_raw, result FROM commodity_trades WHERE applied_to_stock = 0 ORDER BY log_time, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read()) pending.Add((r.GetInt64(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6), Str(r, 7), Dbl(r, 8), Str(r, 9), Str(r, 10).Length == 0));
        }
        foreach (var p in pending)
        {
            if (p.Ok && p.Uuid.Length > 0 && p.Scu is > 0 and var scu && AfterCutoff(p.LogTime))
            {
                var delta = p.Kind == "sell" ? -scu : scu;
                var key = GameLogTrade.MakeKey(p.LogTime, p.PlayerId, p.ShopId, p.KioskId, p.Kind, p.Uuid, p.Raw);
                if (HangarService.InsertMark(db, tx, MarkKindTrade, key, MarkTargetStock, p.Uuid, delta))
                    clamped += AdjustStockCore(db, tx, p.Uuid, p.Record, null, UnitScu, delta);
            }
            Exec(db, tx, "UPDATE commodity_trades SET applied_to_stock = 1 WHERE rowid = @id", ("@id", p.RowId));
            trades++;
        }

        var mined = new List<(long RowId, string LogTime, string Class, string EntityId, string Uuid, string Record, double Qty, string Unit, int? Quality)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT rowid, log_time, mineral_class, entity_id, resource_uuid, resource_record, quantity, unit, quality FROM mining_log WHERE applied_to_stock = 0 AND resource_uuid != '' ORDER BY log_time, rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                mined.Add((r.GetInt64(0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Dbl(r, 6) ?? 0, Str(r, 7) is { Length: > 0 } u ? u : UnitPiece, r.IsDBNull(8) ? null : r.GetInt32(8)));
        }
        foreach (var m in mined)
        {
            if (m.Qty > 0 && AfterCutoff(m.LogTime))
            {
                var key = GameLogMining.MakeKey(m.LogTime, m.Class, m.EntityId);
                if (HangarService.InsertMark(db, tx, MarkKindMined, key, MarkTargetStock, m.Uuid, m.Qty))
                    clamped += AdjustStockCore(db, tx, m.Uuid, m.Record, m.Quality, m.Unit, m.Qty);
            }
            Exec(db, tx, "UPDATE mining_log SET applied_to_stock = 1 WHERE rowid = @id", ("@id", m.RowId));
            mining++;
        }
        tx.Commit();
        return (trades, mining, clamped);
    }

    // まだ在庫に反映していない記録 (ApplyToStock が処理する行: 売買は applied_to_stock = 0 の全部、採掘は applied_to_stock = 0 で資源を引けている行) が
    // stockCutoffUtc 以降 (log_time の文字の比較。null なら全部) に残っているか。前の ApplyToStock が途中で失敗した (database is locked 等) ときに
    // 新しい行が無くても反映をやり直すための判定。DB が無ければ false
    public bool HasUnappliedRecords(DateTime? stockCutoffUtc = null)
    {
        if (!DbExists) return false;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS (SELECT 1 FROM commodity_trades WHERE applied_to_stock = 0 AND (@c IS NULL OR log_time >= @c))
                OR EXISTS (SELECT 1 FROM mining_log WHERE applied_to_stock = 0 AND resource_uuid != '' AND (@c IS NULL OR log_time >= @c))
            """;
        cmd.Parameters.AddWithValue("@c", stockCutoffUtc is { } c ? c.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) : DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // ［ゲームログ］の「入れる」「減らす」(在庫へ): 印 (kind, logKey → ore_stock) を書いて在庫を quantity だけ変える (負なら引く。0 より下には下げない)。
    // 印の quantity には実際に変えた量 (引ききれなかった分を除く) を書く (「取り消す」で戻す量)。印が既にあれば何もせず (false, 0)。
    // 戻り値は (変えたか, 引ききれなかった量)
    public (bool Applied, double Shortage) ImportToStock(string kind, string logKey, string resourceUuid, string resourceRecord, int? quality, string unit, double quantity)
    {
        CheckQuality(quality);
        CheckUnit(unit);
        using var db = Open();
        using var tx = db.BeginTransaction();
        var result = ImportToStockCore(db, tx, kind, logKey, resourceUuid, resourceRecord, quality, unit, quantity);
        tx.Commit();
        return result;
    }

    // markRecord: 印の item_record に書く値 (null なら資源の uuid。納品は uuid と単位 (MakeStockMarkRecord))
    private static (bool Applied, double Shortage) ImportToStockCore(SqliteConnection db, SqliteTransaction tx, string kind, string logKey, string resourceUuid, string resourceRecord, int? quality, string unit, double quantity, string? markRecord = null)
    {
        if (!HangarService.InsertMark(db, tx, kind, logKey, MarkTargetStock, markRecord ?? resourceUuid, quantity)) return (false, 0);
        var clamped = AdjustStockCore(db, tx, resourceUuid, resourceRecord, quality, unit, quantity);
        if (clamped > 0)
            Exec(db, tx, "UPDATE game_log_imports SET quantity = @q WHERE kind = @k AND log_key = @l", ("@q", quantity + clamped), ("@k", kind), ("@l", logKey));
        return (true, clamped);
    }

    // 納品の減算の印 (kind delivered) の item_record: 資源の uuid と引いた行の単位を U+001F でつないだもの (取り消しで同じ単位の行へ戻すため)。
    // 前の版の印 (uuid だけ) は単位なし
    private const char StockMarkSeparator = '\u001F';
    private static string MakeStockMarkRecord(string resourceUuid, string unit) => resourceUuid + StockMarkSeparator + unit;
    private static (string Uuid, string? Unit) SplitStockMarkRecord(string itemRecord)
    {
        var at = itemRecord.IndexOf(StockMarkSeparator);
        if (at < 0) return (itemRecord, null);
        var unit = itemRecord[(at + 1)..];
        return (itemRecord[..at], unit.Length > 0 ? unit : null);
    }

    // 納品で引く在庫の行の単位: 同じ資源 (品質なし) の行を 個 → SCU の順に見て、ある方。どちらも無ければ 個 (引けず 0 止め)
    private static string DeliveryStockUnit(SqliteConnection db, SqliteTransaction tx, string resourceUuid)
    {
        if (FindStock(db, tx, resourceUuid, null, UnitPiece) != null) return UnitPiece;
        if (FindStock(db, tx, resourceUuid, null, UnitScu) != null) return UnitScu;
        return UnitPiece;
    }

    // ［ゲームログ］の納品の「減らす」(手押し): 印 (kind, logKey → ore_stock。item_record は uuid と単位) を書いて、在庫から quantity を引く
    // (行の単位は DeliveryStockUnit。品質なし。0 より下には下げない)。印が既にあれば (false, 0)。戻り値は ImportToStock と同じ
    public (bool Applied, double Shortage) ImportDeliveryToStock(string kind, string logKey, string resourceUuid, string resourceRecord, double quantity)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var unit = DeliveryStockUnit(db, tx, resourceUuid);
        var result = ImportToStockCore(db, tx, kind, logKey, resourceUuid, resourceRecord, null, unit, -quantity, MakeStockMarkRecord(resourceUuid, unit));
        tx.Commit();
        return result;
    }

    // 納品の自動の減算: 同じ納品について一度だけ (印 kind "delivered_auto" を済の印として残し、取り消しても消さない) 在庫から quantity を引く
    // (印 kind delivered。行の単位は DeliveryStockUnit (個 → SCU の順にある方)、品質なし)。二度目以降・既に delivered の印があるときは (false, 0)。
    // 戻り値は ImportToStock と同じ
    public const string MarkKindDelivered = "delivered";
    public const string MarkKindDeliveredAuto = "delivered_auto";

    public (bool Applied, double Shortage) ApplyDeliveryOnce(string logKey, string resourceUuid, string resourceRecord, double quantity)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (!HangarService.InsertMark(db, tx, MarkKindDeliveredAuto, logKey, MarkTargetStock, resourceUuid, -quantity)) return (false, 0);
        var unit = DeliveryStockUnit(db, tx, resourceUuid);
        var result = ImportToStockCore(db, tx, MarkKindDelivered, logKey, resourceUuid, resourceRecord, null, unit, -quantity, MakeStockMarkRecord(resourceUuid, unit));
        tx.Commit();
        return result;
    }

    // ［ゲームログ］の「取り消す」(在庫から): 印 (入れた先が ore_stock) を消し、入れた量を在庫から引く (0 より下には下げない)。
    // 単位は印の item_record に単位があればそれ (納品。引いた行と同じ行へ戻す)、無ければ 売買 (trade) なら SCU、ほか (mined・前の版の納品) は 個。
    // 品質は無し (入れたときと同じ)。戻り値は (印があったか, 引ききれなかった量)
    public (bool Found, double Shortage) CancelStockImport(string kind, string logKey)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        GameLogImportMark? mark = null;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT target, item_record, quantity FROM game_log_imports WHERE kind = @k AND log_key = @l";
            cmd.Parameters.AddWithValue("@k", kind);
            cmd.Parameters.AddWithValue("@l", logKey);
            using var r = cmd.ExecuteReader();
            if (r.Read()) mark = new GameLogImportMark(kind, logKey, Str(r, 0), Str(r, 1), Dbl(r, 2) ?? 0);
        }
        if (mark == null || mark.Target != MarkTargetStock) return (false, 0);
        var (uuid, markUnit) = SplitStockMarkRecord(mark.ItemRecord);
        var unit = markUnit ?? (kind == MarkKindTrade ? UnitScu : UnitPiece);
        var clamped = uuid.Length > 0 && mark.Quantity != 0 ? AdjustStockCore(db, tx, uuid, "", null, unit, -mark.Quantity) : 0;
        HangarService.DeleteMark(db, tx, kind, logKey);
        tx.Commit();
        return (true, clamped);
    }

    // 取引の結果が後から分かった (失敗が結び付いた) とき: 同じ鍵 (主キー) の行の result を書き換える。
    // 成功として既に在庫へ反映してあれば (印 kind trade がある) 反映を戻して印を消す。行が無ければ false。
    // 利益の計算し直し (RecomputeProfits) は呼ぶ側で行う
    public bool UpdateTradeResult(string logTime, string playerId, string shopId, string kioskId, string kind, string resourceUuid, string quantityRaw, string result)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        int n;
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE commodity_trades SET result = @res
                WHERE log_time = @t AND player_id = @p AND shop_id = @s AND kiosk_id = @k AND kind = @kind AND resource_uuid = @u AND quantity_raw = @raw AND result != @res
                """;
            cmd.Parameters.AddWithValue("@res", result ?? "");
            cmd.Parameters.AddWithValue("@t", logTime);
            cmd.Parameters.AddWithValue("@p", playerId ?? "");
            cmd.Parameters.AddWithValue("@s", shopId ?? "");
            cmd.Parameters.AddWithValue("@k", kioskId ?? "");
            cmd.Parameters.AddWithValue("@kind", kind);
            cmd.Parameters.AddWithValue("@u", resourceUuid ?? "");
            cmd.Parameters.AddWithValue("@raw", quantityRaw ?? "");
            n = cmd.ExecuteNonQuery();
        }
        if (n == 0) { tx.Commit(); return false; }
        if (!string.IsNullOrEmpty(result))
        {
            var key = GameLogTrade.MakeKey(logTime, playerId ?? "", shopId ?? "", kioskId ?? "", kind, resourceUuid ?? "", quantityRaw ?? "");
            double? applied = null;
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT quantity FROM game_log_imports WHERE kind = @k AND log_key = @l AND target = @tg";
                cmd.Parameters.AddWithValue("@k", MarkKindTrade);
                cmd.Parameters.AddWithValue("@l", key);
                cmd.Parameters.AddWithValue("@tg", MarkTargetStock);
                applied = cmd.ExecuteScalar() is double d ? d : null;
            }
            if (applied is { } delta && delta != 0 && (resourceUuid ?? "").Length > 0)
            {
                AdjustStockCore(db, tx, resourceUuid!, "", null, UnitScu, -delta);
                HangarService.DeleteMark(db, tx, MarkKindTrade, key);
            }
        }
        tx.Commit();
        return true;
    }

    // 売りの行の利益 (鍵 → (利益, 利益率))。計算前 (NULL) の行は入れない。［ゲームログ］の売却の行に添えるのに使う
    public Dictionary<string, (double Profit, double? Rate)> LoadSellProfits()
    {
        var dict = new Dictionary<string, (double, double?)>(StringComparer.Ordinal);
        if (!DbExists) return dict;
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT log_time, player_id, shop_id, kiosk_id, kind, resource_uuid, quantity_raw, profit_auec, profit_rate FROM commodity_trades WHERE kind = 'sell' AND profit_auec IS NOT NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            dict[GameLogTrade.MakeKey(Str(r, 0), Str(r, 1), Str(r, 2), Str(r, 3), Str(r, 4), Str(r, 5), Str(r, 6))] = (Dbl(r, 7) ?? 0, Dbl(r, 8));
        return dict;
    }

    // === 資源の一覧 (gamedata_cache.db の resource_types + translations.db) ===

    // ［鉱石］タブの一覧に入れる資源グループ (ResourceTypeDatabase の ResourceTypeGroup.<名前>)。
    // Bulk_Supplies (弾薬・対抗手段) と PowerPlaceholder / HeatPlaceholder / LifeSupportPlaceholder / CleanAir は入れない
    private static readonly HashSet<string> OreGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "ResourceTypeGroup.UnrefinedOres", "ResourceTypeGroup.Raw_Minerals", "ResourceTypeGroup.Mineral", "ResourceTypeGroup.Metal",
        "ResourceTypeGroup.Alloy", "ResourceTypeGroup.Nonmetal", "ResourceTypeGroup.Halogen", "ResourceTypeGroup.Gas",
        "ResourceTypeGroup.Organic", "ResourceTypeGroup.Food", "ResourceTypeGroup.ProcessedGoods", "ResourceTypeGroup.Vice",
        "ResourceTypeGroup.SyntheticMaterials", "ResourceTypeGroup.Waste",
    };

    // グループの名前: ResourceTypeDatabase のグループ record の displayName (@items_commodities_type_…) を翻訳 DB で引く
    // (2026-10-17 の Data.p4k で確認した対応)。displayName が @LOC_PLACEHOLDER のグループ (UnrefinedOres / Raw_Minerals / Halogen) は
    // 固定の日本語 (Key = null)。表に無いグループは record の名前のまま
    private static readonly Dictionary<string, (string? Key, string Fallback)> GroupLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ResourceTypeGroup.Gas"] = ("@items_commodities_type_gas", "Gas"),
        ["ResourceTypeGroup.Metal"] = ("@items_commodities_type_metal", "Metal"),
        ["ResourceTypeGroup.Alloy"] = ("@items_commodities_type_alloy", "Alloy"),
        ["ResourceTypeGroup.UnrefinedOres"] = (null, "原石 (金属)"),
        ["ResourceTypeGroup.Mineral"] = ("@items_commodities_type_Mineral", "Mineral"),
        ["ResourceTypeGroup.Raw_Minerals"] = (null, "原石 (鉱物)"),
        ["ResourceTypeGroup.Organic"] = ("@items_commodities_type_natural", "Natural Materials"),
        ["ResourceTypeGroup.Food"] = ("@items_commodities_type_food", "Food"),
        ["ResourceTypeGroup.Nonmetal"] = ("@items_commodities_type_nonmetals", "Nonmetals"),
        ["ResourceTypeGroup.Halogen"] = (null, "ハロゲン"),
        ["ResourceTypeGroup.ProcessedGoods"] = ("@items_commodities_type_processedGoods", "Processed Goods"),
        ["ResourceTypeGroup.Vice"] = ("@items_commodities_type_vice", "Vice"),
        ["ResourceTypeGroup.SyntheticMaterials"] = ("@items_commodities_type_manmade", "Man-made"),
        ["ResourceTypeGroup.Bulk_Supplies"] = ("@items_commodities_type_manmade", "Man-made"),
        ["ResourceTypeGroup.Waste"] = ("@items_commodities_type_waste", "Waste"),
    };

    // 一覧に入れるか: グループが OreGroups で、名前のキーが @items_commodities_ で始まり (CO2・ShipAmmunition・AceInterceptorHelmet・Armillaria 等の
    // 商品でないものを除く)、プレースホルダ (@LOC_PLACEHOLDER) でも special_ (季節の品) でもないもの
    private static bool IsOreListed(string groupRecord, string? nameKey) =>
        OreGroups.Contains(groupRecord) && nameKey is { Length: > 1 }
        && nameKey.StartsWith("@items_commodities_", StringComparison.OrdinalIgnoreCase)
        && !nameKey.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
        && !nameKey.Contains("_special_", StringComparison.OrdinalIgnoreCase);

    // resource_types の全件に名前 (日本語は translations の name_key。大文字小文字を区別しない) とグループ名を付けて返す。
    // 表が無い (インデックス構築前の DB)・読めない (開けない・壊れている) ときは空 (例外は投げない。呼ぶ側はそのまま「資源を引けない」扱い)。
    // group_record 列の無い DB (前の版) ではグループは '' (一覧には何も出ない。起動後の裏の取り出しで入る)
    public static List<OreResource> LoadResources(string gamedataCacheDbPath, string translationsDbPath)
    {
        var list = new List<OreResource>();
        if (!File.Exists(gamedataCacheDbPath)) return list;
        try
        {
            using var game = new SqliteConnection($"Data Source={gamedataCacheDbPath};Mode=ReadOnly;Pooling=False");
            game.Open();
            using (var exists = game.CreateCommand())
            {
                exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'resource_types'";
                if (Convert.ToInt64(exists.ExecuteScalar() ?? 0L) == 0) return list;
            }
            bool hasGroup = false;
            using (var info = game.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(resource_types)";
                using var r = info.ExecuteReader();
                while (r.Read()) if (string.Equals(r.GetString(1), "group_record", StringComparison.OrdinalIgnoreCase)) hasGroup = true;
            }
            using var cmd = game.CreateCommand();
            cmd.CommandText = hasGroup
                ? "SELECT uuid, record_name, name_key, name_en, group_record FROM resource_types"
                : "SELECT uuid, record_name, name_key, name_en, NULL FROM resource_types";
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    list.Add(new OreResource
                    {
                        Uuid = Str(r, 0), RecordName = Str(r, 1),
                        NameKey = r.IsDBNull(2) ? null : r.GetString(2),
                        NameEn = r.IsDBNull(3) ? null : Usable(r.GetString(3)),
                        GroupRecord = Str(r, 4),
                    });
                }
            }
        }
        catch (SqliteException)
        {
            list.Clear();
            return list;
        }
        // 翻訳: 必要なキー (資源の name_key とグループの displayName) だけを translations から引く (キーの大文字小文字は区別しない。",P" の行も同じキー)
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in list) if (x.NameKey is { Length: > 1 } k && k[0] == '@') keys.Add(k[1..]);
        foreach (var (k, _) in GroupLabels.Values) if (k != null) keys.Add(k[1..]);
        var loc = LoadTranslations(translationsDbPath, keys);
        foreach (var x in list)
        {
            if (x.NameKey is { Length: > 1 } k && k[0] == '@' && loc.TryGetValue(k[1..], out var t))
            {
                x.NameJa = JapaneseText(t.Ja);
                x.NameEn ??= EnglishText(t.En);
            }
            x.GroupName = GroupLabel(x.GroupRecord, loc);
            x.InOreList = IsOreListed(x.GroupRecord, x.NameKey);
        }
        return list;
    }

    // ［鉱石］タブの資源の一覧 (IsOreListed のもの。グループ → 名前の順)
    public static List<OreResource> GetOreResourceList(string gamedataCacheDbPath, string translationsDbPath) =>
        LoadResources(gamedataCacheDbPath, translationsDbPath).Where(x => x.InOreList)
            .OrderBy(x => x.GroupName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToList();

    private static string GroupLabel(string groupRecord, Dictionary<string, (string? En, string? Ja)> loc)
    {
        if (groupRecord.Length == 0) return "";
        if (!GroupLabels.TryGetValue(groupRecord, out var g)) return ShortRecord(groupRecord);
        if (g.Key != null && loc.TryGetValue(g.Key[1..], out var t))
            return JapaneseText(t.Ja) ?? EnglishText(t.En) ?? g.Fallback;
        return g.Fallback;
    }

    // translations から keys (先頭の @ 無し) の行の (english, japanese) を引く (GameLogNames.Lookup と同じ見方)。
    // key の行と key + ",P" の行の両方を見て key の行を優先し、大文字小文字がそのまま同じ行を、違う行より優先する。
    // 大文字小文字を区別しないため全行を読む (写しの 91,062 行で数十 ms)。DB が無い・読めないときは空
    private static Dictionary<string, (string? En, string? Ja)> LoadTranslations(string translationsDbPath, HashSet<string> keys)
    {
        var dict = new Dictionary<string, (string? En, string? Ja)>(StringComparer.OrdinalIgnoreCase);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // 入れた行の優先度 (大きいほど優先)
        if (keys.Count == 0 || !File.Exists(translationsDbPath)) return dict;
        try
        {
            using var c = new SqliteConnection($"Data Source={translationsDbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, english, japanese FROM translations";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                var isPlural = key.EndsWith(",P", StringComparison.Ordinal);
                var bare = isPlural ? key[..^2] : key;
                if (!keys.TryGetValue(bare, out var wanted)) continue;
                var exact = string.Equals(bare, wanted, StringComparison.Ordinal);
                var score = (exact ? 2 : 0) + (isPlural ? 0 : 1);
                if (rank.TryGetValue(bare, out var have) && have >= score) continue;
                dict[bare] = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
                rank[bare] = score;
            }
        }
        catch { }
        return dict;
    }

    // 手掘りの鉱石のクラス名 Harvestable_Mineral_1H_<X> → resource_types の ResourceType.<X> (大文字小文字を区別しない)。無ければ null
    public static (string Uuid, string Record)? ResolveMineralResource(string gamedataCacheDbPath, string mineralClass)
    {
        const string prefix = "Harvestable_Mineral_1H_";
        if (string.IsNullOrEmpty(mineralClass) || !File.Exists(gamedataCacheDbPath)) return null;
        var x = mineralClass.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? mineralClass[prefix.Length..] : mineralClass;
        try
        {
            using var game = new SqliteConnection($"Data Source={gamedataCacheDbPath};Mode=ReadOnly;Pooling=False");
            game.Open();
            using var cmd = game.CreateCommand();
            cmd.CommandText = "SELECT uuid, record_name FROM resource_types WHERE record_name = @r COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@r", "ResourceType." + x);
            using var r = cmd.ExecuteReader();
            return r.Read() ? (r.GetString(0), r.GetString(1)) : null;
        }
        catch { return null; }
    }

    // === 表示の補助 (GameLogPanel の GameLogNames と同じ見方) ===

    // 「日本語 (英語)」の形。日本語と英語が同じ (大文字小文字は区別しない) か片方が無ければある方だけ。両方無ければ null
    public static string? Label(string? ja, string? en) =>
        ja != null && en != null && !string.Equals(ja, en, StringComparison.OrdinalIgnoreCase) ? $"{ja} ({en})" : ja ?? en;

    // "ResourceType.Ore_Stileron" → "Ore_Stileron"
    public static string ShortRecord(string record)
    {
        var i = record.IndexOf('.');
        return i >= 0 ? record[(i + 1)..] : record;
    }

    private static string? JapaneseText(string? ja) => !string.IsNullOrWhiteSpace(ja) && !ja.StartsWith('!') ? Usable(ja) : null;
    private static string? EnglishText(string? en) => !string.IsNullOrWhiteSpace(en) ? Usable(en) : null;

    private static string? Usable(string text)
    {
        text = text.Replace(' ', ' ').Trim();
        if (text.Length == 0 || text.StartsWith('@') || text.StartsWith("<=", StringComparison.Ordinal)
            || text.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || text.Contains("プレースホルダ")) return null;
        return text;
    }

    // === 共通 ===

    private static string Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i);
    private static double? Dbl(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    private static void Exec(SqliteConnection db, SqliteTransaction tx, string sql, params (string name, object value)[] parms)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parms) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection db, string sql, params (string name, object value)[] parms)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parms) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
