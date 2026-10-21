using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public enum BackupCategory
{
    Translations,
    Glossary,
    Index,
    Knowledge,
    Trade
}

public static class DatabaseBackupService
{
    private static readonly string[] TranslationTables = ["translations"];
    private static readonly string[] GlossaryTables = ["glossary"];
    // item_shop_prices: 武器・装備 (cstone) と船・車両 (UEX) の売り場・価格。公開データなので配布のバックアップに入れてよい
    // ammo_params: 弾のデータ (Data.p4k の AmmoParams。武器の弾速・ダメージに使う)
    // item_variants: 色違いの表 (性能が基本品目と同じ色・柄違い。インデックス構築で作る)
    // player_damage_params: 何発で倒せるか (TTK) に使う体力・部位の倍率・アーマーの倍率 (インデックス構築で作る)
    // item_ports: タレット・ジンバル・船の武器自身の子ポートの定義 (インデックス構築で作る)
    // item_port_labels: 品目の中の枠の名前と表示名 (日本語化で itemPort_ キーを足すのに使う。インデックス構築で作る)
    // item_index: 品目の索引 (全文検索の表 item_index_fts は取り込まず、取り込んだ後に item_index から作り直す)。item_vectors: チャットのベクトル検索の埋め込み。
    // mission_reputation / faction_reputation / reputation_reward_amount: ミッションの派閥・貢献度 (日本語化で使う)
    // resource_types: 資源の種類 (商品売買のログの resourceGUID → 名前。インデックス構築で作る)
    // blueprints / blueprint_materials / blueprint_pools / blueprint_modifiers: BP (ブループリント。［BP］タブ。インデックス構築で作る)
    // starmap_names: 星図の場所のレコード名 → 表示名の翻訳キー (［ゲームログ］の場所名。インデックス構築で作る)
    // deliverable_sources: Wikelo の契約の納品物のゲームデータ上の入手場所 (［契約］タブの納品物の入手場所。インデックス構築で作る)
    // ore_locations: 資源の採掘できる場所 (［詳細］タブの資源の【採掘できる場所】。インデックス構築で作る)
    // starmap_locations / game_shops / shop_price_links: 星図の場所・ゲームの店・店と DB の価格の紐づけ (［詳細］タブのお店・ステーション・星系。インデックス構築で作る)
    private static readonly string[] IndexTables = ["ships", "ship_ports", "items", "missions", "commodities", "gamedata_meta", "gamedata_cache", "item_shop_prices", "ammo_params", "item_variants", "player_damage_params", "item_ports", "item_port_labels",
        "item_index", "item_vectors", "mission_reputation", "faction_reputation", "reputation_reward_amount", "resource_types", "blueprints", "blueprint_materials", "blueprint_pools", "blueprint_modifiers", "starmap_names", "deliverable_sources", "ore_locations",
        "starmap_locations", "game_shops", "shop_price_links"];
    private static readonly string[] KnowledgeTables = ["knowledge"];
    // ore_stock / commodity_trades / mining_log: ［鉱石］タブの在庫・売買の記録・採掘の記録 (個人データ。OreService)
    // my_gear: 所持個人装備、game_log_imports: ［ゲームログ］の行を所持・在庫に入れた印 (個人データ。HangarService)
    // game_log_entries: ［ゲームログ］の行そのもの (別の PC でも見るために残す。個人データ。HangarService / GameLogStore)
    // my_blueprints: 所持している BP (［BP］タブの「所持」。個人データ。HangarService)
    internal static readonly string[] TradeTables = ["trade_prices", "trade_ships", "trade_terminals", "trade_meta", "my_ships", "hangar_pledges", "hangar_items", "hangar_ccus", "hangar_meta", "hangar_nameable_ships", "hangar_ship_marks", "ship_matrix", "ship_loaners", "store_skus", "my_components", "ship_loadouts", "my_component_purchases",
        "ore_stock", "commodity_trades", "mining_log", "my_gear", "game_log_imports", "game_log_entries", "my_blueprints"];

    public static string[] GetTables(BackupCategory category) => category switch
    {
        BackupCategory.Translations => TranslationTables,
        BackupCategory.Glossary => GlossaryTables,
        BackupCategory.Index => IndexTables,
        BackupCategory.Knowledge => KnowledgeTables,
        BackupCategory.Trade => TradeTables,
        _ => []
    };

    // includeTombstones: false なら PC 間の同期の墓石 (sync_tombstones) を 3 つの DB から消す (配布用。includeMyShips が false のときも消す)
    public static async Task ExportAsync(string translationDbPath, string indexDbPath, string outputPath,
        Action<string>? onStatus = null, string? tradeDbPath = null, bool includeMyShips = true, bool includeTombstones = true)
    {
        var dropTombstones = !includeMyShips || !includeTombstones;
        var tempDir = Path.Combine(Path.GetTempPath(), $"sc_export_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await Task.Run(() =>
            {
                if (File.Exists(translationDbPath))
                {
                    onStatus?.Invoke("translations.db をコピー中...");
                    var tempTransPath = Path.Combine(tempDir, "translations.db");
                    // WAL の DB は本体だけを写すと -wal の中身が抜けるので、VACUUM INTO で一貫した写しを作る (写しは DELETE 方式の 1 つのファイル)。
                    // VACUUM INTO が失敗したら (DB が壊れているときなど) 本体と -wal・-shm を組で写す
                    CopyForExport(translationDbPath, tempTransPath, onStatus);
                    if (dropTombstones)
                    {
                        using (var conn = new SqliteConnection($"Data Source={tempTransPath}"))
                        {
                            conn.Open();
                            DropTombstones(conn);
                        }
                        SqliteConnection.ClearAllPools();
                    }
                }
                if (File.Exists(indexDbPath))
                {
                    onStatus?.Invoke("gamedata_cache.db をコピー中...");
                    var tempIndexPath = Path.Combine(tempDir, "gamedata_cache.db");
                    CopyForExport(indexDbPath, tempIndexPath, onStatus);
                    // この PC だけの印 (売り場・価格の取得中・手動中止) はバックアップに入れない
                    using (var conn = new SqliteConnection($"Data Source={tempIndexPath}"))
                    {
                        conn.Open();
                        if (TableExists(conn, "gamedata_meta"))
                        {
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = $"DELETE FROM gamedata_meta WHERE key IN ({LocalOnlyMetaKeyList})";
                            cmd.ExecuteNonQuery();
                        }
                        if (dropTombstones) DropTombstones(conn);
                    }
                    SqliteConnection.ClearAllPools();
                }
                // 個人データを除いた書き出し (includeMyShips が false。配布用) には trade_cache.db を入れない
                if (includeMyShips && !string.IsNullOrEmpty(tradeDbPath) && File.Exists(tradeDbPath))
                {
                    onStatus?.Invoke("trade_cache.db をコピー中...");
                    var tempTradePath = Path.Combine(tempDir, "trade_cache.db");
                    CopyForExport(tradeDbPath, tempTradePath, onStatus);

                    if (!includeMyShips)
                    {
                        onStatus?.Invoke("所持船舶・アップグレード権利データを除外中...");
                        using (var conn = new SqliteConnection($"Data Source={tempTradePath}"))
                        {
                            conn.Open();
                            // hangar_* を導入する前に作られた DB にはテーブルが無いため、DELETE の前に作っておく
                            EnsureTradeSchema(conn);
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = """
                                    DELETE FROM my_ships;
                                    DELETE FROM hangar_pledges;
                                    DELETE FROM hangar_items;
                                    DELETE FROM hangar_ccus;
                                    DELETE FROM hangar_meta;
                                    DELETE FROM hangar_nameable_ships;
                                    DELETE FROM hangar_ship_marks;
                                    DELETE FROM my_components;
                                    DELETE FROM ship_loadouts;
                                    DELETE FROM my_component_purchases;
                                    DELETE FROM ore_stock;
                                    DELETE FROM commodity_trades;
                                    DELETE FROM mining_log;
                                    DELETE FROM my_gear;
                                    DELETE FROM game_log_imports;
                                    DELETE FROM game_log_entries;
                                    DELETE FROM my_blueprints;
                                    DELETE FROM sync_tombstones;
                                    """;
                                cmd.ExecuteNonQuery();
                            }
                            using (var vacCmd = conn.CreateCommand())
                            {
                                vacCmd.CommandText = "VACUUM";
                                vacCmd.ExecuteNonQuery();
                            }
                        }
                        SqliteConnection.ClearAllPools();
                    }
                }

                onStatus?.Invoke("ZIP を作成中...");
                if (File.Exists(outputPath)) File.Delete(outputPath);
                ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, false);
            });
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }

        onStatus?.Invoke("エクスポート完了");
    }

    // 書き出しの写しを作る (SqliteWal.SnapshotOrCopyTo)。VACUUM INTO で写せずに組で写したときは、DB が壊れている可能性を添えて知らせる
    private static void CopyForExport(string src, string dest, Action<string>? onStatus)
    {
        if (SqliteWal.SnapshotOrCopyTo(src, dest) is { } why)
            onStatus?.Invoke($"{Path.GetFileName(src)} は VACUUM INTO で写せなかったため、ファイル (本体と -wal・-shm) をそのまま写しました。DB が壊れている可能性があります ({why})");
    }

    // 書き出しの写しから PC 間の同期の墓石 (sync_tombstones) を消す (表が無ければ何もしない)
    private static void DropTombstones(SqliteConnection conn)
    {
        if (!TableExists(conn, SyncService.TombstonesTable)) return;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {SyncService.TombstonesTable}";
        cmd.ExecuteNonQuery();
    }

    public static async Task<Dictionary<BackupCategory, int>> InspectAsync(string backupPath)
    {
        var sql = await ReadCompressedSqlAsync(backupPath);
        var result = new Dictionary<BackupCategory, int>();

        foreach (BackupCategory cat in Enum.GetValues<BackupCategory>())
        {
            int count = 0;
            foreach (var table in GetTables(cat))
            {
                var marker = $"-- TABLE: {table}";
                if (sql.Contains(marker))
                    count += CountInserts(sql, table);
            }
            if (count > 0)
                result[cat] = count;
        }
        return result;
    }

    public static async Task<Dictionary<BackupCategory, int>> InspectDbFileAsync(string dbPath)
    {
        var result = new Dictionary<BackupCategory, int>();
        await Task.Run(() =>
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            foreach (BackupCategory cat in Enum.GetValues<BackupCategory>())
            {
                int count = 0;
                foreach (var table in GetTables(cat))
                {
                    if (!TableExists(conn, table)) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                    count += (int)(long)(cmd.ExecuteScalar() ?? 0L);
                }
                if (count > 0) result[cat] = count;
            }
        });
        return result;
    }

    public static async Task<Dictionary<BackupCategory, int>> InspectZipAsync(string zipPath)
    {
        var result = new Dictionary<BackupCategory, int>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"sc_backup_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempDir));
            foreach (var dbFile in Directory.GetFiles(tempDir, "*.db"))
            {
                var partial = await InspectDbFileAsync(dbFile);
                foreach (var kv in partial)
                {
                    if (result.ContainsKey(kv.Key))
                        result[kv.Key] += kv.Value;
                    else
                        result[kv.Key] = kv.Value;
                }
            }
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
        return result;
    }

    // ===== インポート (復元) =====
    // 決まり: 利用者が入力したもの (個人データ) は今の DB (a) を残し、バックアップ (b) の分は a とぶつからないものだけ足す (a 優先の和集合)。
    // ゲームやネットから取得したデータは、選んだカテゴリについてバックアップ (b) の内容で置き換える。
    // 3 つの DB をまとめて扱い、どこかで失敗したら、取り込みの前に取った控えから全部を元に戻す

    // 交易 DB (trade_cache.db) の表の扱い
    // ネット由来 (b で置き換え): UEX の価格・船・端末・取得時刻、RSI の Ship Matrix・ローナー・ストア
    internal static readonly string[] TradeNetTables = ["trade_prices", "trade_ships", "trade_terminals", "trade_meta", "ship_matrix", "ship_loaners", "store_skus"];
    // 個人で、行を見分けるキー (主キー) があるもの: キーで a 優先の和集合
    // (commodity_trades / mining_log: ［鉱石］タブの売買・採掘の記録。主キーはログの行の組。game_log_entries: ［ゲームログ］の行。主キーは (time_utc, kind, content)。
    //  my_blueprints: 所持している BP。主キーは bp_uuid)
    internal static readonly string[] TradePersonalKeyedTables = ["hangar_pledges", "hangar_ccus", "hangar_ship_marks", "ship_loadouts", "commodity_trades", "mining_log", "game_log_entries", "my_blueprints"];
    // 個人で、行を見分ける主キーが無いもの (主キーが無い、または中身と関係の無い連番の id だけのもの): 自然な鍵で a 優先の和集合
    //   my_ships: 正規化した船名 (HangarService.NormalizeShipName。所持船の名前一致の流儀) が a に無い b の船を足す。a にある船はそのまま (数量の概念なし)
    //   my_components / my_gear: item_record と使用可 (usable) が同じ (my_components は品質 (quality_json。揃えた書き方で比べる) も同じ) なら
    //     a の行を残して quantity は max(a, b)、notes は JoinNotes。無い品は b から足す
    //   hangar_items / hangar_nameable_ships: 全列で重複を除いた和集合
    internal static readonly string[] TradePersonalUnkeyedTables = ["my_ships", "my_components", "hangar_items", "hangar_nameable_ships", "my_gear"];
    // ［ゲームログ］の行を所持・在庫に入れた印 (game_log_imports と、旧の my_component_purchases): 主キーで a 優先の和集合 (Keyed と同じ)。
    // ［鉱石］タブの在庫 (ore_stock): 資源 (resource_uuid) × 品質 (quality) × 単位 (unit) が同じなら a の行を残して quantity は max、無ければ b から足す
    internal const string ComponentPurchasesTable = "my_component_purchases";
    internal const string GameLogImportsTable = "game_log_imports";
    internal const string OreStockTable = "ore_stock";
    // hangar_meta のうちネット由来のデータ (ship_matrix / ship_loaners / store_skus) の取得時刻のキー (b を使う)。ほかのキー (同期の時刻・残高) は個人
    internal static readonly string[] HangarMetaNetKeys = ["ship_matrix_fetched_at", "loaners_fetched_at", "store_fetched_at"];

    public static async Task ImportFromZipAsync(string zipPath, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, Action<string>? onStatus = null, string? tradeDbPath = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sc_backup_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            onStatus?.Invoke("ZIP を展開中...");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempDir));
            await ImportFromDbFilesAsync(Directory.GetFiles(tempDir, "*.db"), translationDbPath, indexDbPath, categories, onStatus, tradeDbPath);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    // 展開したバックアップの DB ファイル (translations.db / gamedata_cache.db / trade_cache.db。どれかだけでもよい) から取り込む。
    // どの DB かはファイルの名前ではなく、中の表で見分ける
    public static Task ImportFromDbFilesAsync(IEnumerable<string> sourceDbPaths, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, Action<string>? onStatus = null, string? tradeDbPath = null)
    {
        var sources = sourceDbPaths.ToList();
        var cats = categories.ToHashSet();
        return Task.Run(() => ImportCore(sources, translationDbPath, indexDbPath, cats, onStatus, tradeDbPath));
    }

    // アプリを閉じるとき (AppExit) は、控えを作る前なら何もせずに止め、控えを作った後なら段階の間で止めて取り込み前の控えから戻す
    // (閉じる処理は戻し終わるまで待つ)
    private static void ImportCore(List<string> sources, string translationDbPath, string indexDbPath,
        HashSet<BackupCategory> cats, Action<string>? onStatus, string? tradeDbPath)
    {
        using var exitWork = AppExit.BeginWork("バックアップの取り込み");
        var exitToken = AppExit.Token;
        exitToken.ThrowIfCancellationRequested();
        // 前回の取り込みの途中の印が残っていれば始めない (InterruptedWork。起動のときに戻して消すので、ふつうは残らない)
        InterruptedWork.ThrowIfPending(Path.GetDirectoryName(Path.GetFullPath(indexDbPath)) ?? ".", InterruptedWork.KindImport);
        string? srcTrans = null, srcIndex = null, srcTrade = null;
        foreach (var p in sources)
        {
            using var conn = new SqliteConnection($"Data Source={p};Mode=ReadOnly;Pooling=False");
            conn.Open();
            if (srcTrans == null && (TableExists(conn, "translations") || TableExists(conn, "glossary"))) srcTrans = p;
            if (srcIndex == null && (TableExists(conn, "items") || TableExists(conn, "item_index") || TableExists(conn, "knowledge") || TableExists(conn, "gamedata_meta"))) srcIndex = p;
            if (srcTrade == null && (TableExists(conn, "trade_prices") || TableExists(conn, "my_ships") || TableExists(conn, "hangar_pledges")
                                     || TableExists(conn, "ore_stock") || TableExists(conn, "my_gear"))) srcTrade = p;
        }

        var doTrans = srcTrans != null && (cats.Contains(BackupCategory.Translations) || cats.Contains(BackupCategory.Glossary));
        var doIndex = srcIndex != null && (cats.Contains(BackupCategory.Index) || cats.Contains(BackupCategory.Knowledge));
        var doTrade = srcTrade != null && cats.Contains(BackupCategory.Trade) && !string.IsNullOrEmpty(tradeDbPath);
        // 翻訳かゲームデータを置き換えたら、ゲームデータの日本語名 (item_index.name_ja) を今の個人訳で付け直し、全文検索の表を作り直す
        var repopulate = (doTrans && cats.Contains(BackupCategory.Translations)) || (doIndex && cats.Contains(BackupCategory.Index));
        if (!doTrans && !doIndex && !doTrade)
        {
            onStatus?.Invoke("取り込むデータがありません");
            return;
        }

        // 取り込み先 (控えを作る DB)
        var targets = new List<string>();
        if (doTrans) targets.Add(translationDbPath);
        var doIndexSchema = doIndex || (repopulate && File.Exists(indexDbPath));
        if (doIndexSchema) targets.Add(indexDbPath);
        if (doTrade) targets.Add(tradeDbPath!);

        // 控え (取り込みの前の状態)。空きが足りなければ何もしない。取り込みで増える分 (取り込むバックアップの DB の大きさの合計) も見込む
        var importBytes = new[] { doTrans ? srcTrans : null, doIndex ? srcIndex : null, doTrade ? srcTrade : null }
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Sum(p => new FileInfo(p).Length);
        EnsureFreeSpaceForBackups(targets, importBytes);

        // 今ある取り込み先の控えは、DB の形を揃える (EnsureSchema は開けない・壊れた DB を .corrupt- へ退避して作り直すことがある) より前に作る。
        // 控えを作れなければ (壊れた DB など) 何も取り込まずに知らせる
        var backups = new Dictionary<string, string>();
        string? current = null;
        try
        {
            foreach (var t in targets.Where(File.Exists))
            {
                exitToken.ThrowIfCancellationRequested();
                current = t;
                onStatus?.Invoke($"{Path.GetFileName(t)} の取り込み前の控えを作っています...");
                backups[t] = CreateImportBackup(t);
            }
        }
        catch (OperationCanceledException)
        {
            SqliteConnection.ClearAllPools();
            foreach (var b in backups.Values) TryDelete(b);
            throw;
        }
        catch (Exception ex)
        {
            SqliteConnection.ClearAllPools();
            foreach (var b in backups.Values) TryDelete(b);
            throw new IOException($"{Path.GetFileName(current)} の取り込み前の控えを作れなかったため、何も取り込んでいません: {ex.Message}", ex);
        }

        // 途中の印 (InterruptedWork)。取り込み先を書き換え始める (形を揃える) 前に書き、控えを足すたびに書き直す。成功したとき・
        // 控えから全部戻し終えたときに消す (アプリが途中で終わったら、次の起動で印を見て全部の DB を控えから戻す)。書けなければ何も取り込まない
        var markerDir = Path.GetDirectoryName(Path.GetFullPath(indexDbPath)) ?? ".";   // 印は今の作業フォルダに書く (InterruptedWork.MarkerDir。決まっていなければここ)
        // 最初に決めた印の場所を覚え、書き直し・消すのはその場所に対して行う (途中で作業フォルダの設定が変わっても、ずれないように)
        var markerPath = InterruptedWork.MarkerPath(InterruptedWork.MarkerDir(markerDir), InterruptedWork.KindImport);
        void WriteMarker() => InterruptedWork.WriteAt(markerPath, new InterruptedWork.Marker
        {
            Kind = InterruptedWork.KindImport, Label = "バックアップの取り込み", Backups = new Dictionary<string, string>(backups),
        });
        try { WriteMarker(); }
        catch (Exception ex)
        {
            foreach (var b in backups.Values) TryDelete(b);
            throw new IOException($"途中の印を書けなかったため、何も取り込んでいません: {ex.Message}", ex);
        }

        try
        {
            // 取り込み先の DB の形を今のものに揃える (無ければ作る)
            onStatus?.Invoke("取り込み先の DB を確かめています...");
            if (doTrans)
            {
                using (new TranslationDatabase(translationDbPath)) { }
            }
            if (doIndexSchema)
            {
                using (var gx = new GameDataExtractor(Path.GetDirectoryName(indexDbPath) ?? ".", indexDbPath)) gx.EnsureSchema();
                using (var conn = new SqliteConnection($"Data Source={indexDbPath};Pooling=False"))
                {
                    conn.Open();
                    EnsureIndexSchema(conn);
                }
            }
            if (doTrade)
            {
                using (var conn = new SqliteConnection($"Data Source={tradeDbPath};Pooling=False"))
                {
                    conn.Open();
                    EnsureTradeSchema(conn);
                    // id 列の無い古い trade_terminals は、TradeService が開いたときに作り直される (取り込んだ端末が消える) ので、
                    // 取り込む前に同じ移行をしておく
                    TradeService.MigrateTerminalsSchema(conn);
                    // 所持船の入手の種類・期限・印の鍵の列 (origin / expires_at / source_key) の無い古い my_ships にも、取り込む前に列を足す
                    // (列の無いバックアップの行は CommonColumns (共通の列) で入る)
                    TradeService.MigrateMyShipsSchema(conn);
                }
            }
            SqliteConnection.ClearAllPools();

            // 取り込みの前には無かった DB (上で作ったもの) の控えは、今までどおり形を揃えた後に作る
            foreach (var t in targets.Where(t => !backups.ContainsKey(t)))
            {
                onStatus?.Invoke($"{Path.GetFileName(t)} の取り込み前の控えを作っています...");
                backups[t] = CreateImportBackup(t);
                WriteMarker();
            }

            exitToken.ThrowIfCancellationRequested();
            if (doTrans)
            {
                onStatus?.Invoke("翻訳DBにインポート中...");
                ImportTranslationsDb(translationDbPath, srcTrans!, cats, onStatus);
            }
            exitToken.ThrowIfCancellationRequested();
            if (doIndex)
            {
                onStatus?.Invoke("インデックスDBにインポート中...");
                ImportIndexDb(indexDbPath, srcIndex!, cats, onStatus);
            }
            exitToken.ThrowIfCancellationRequested();
            if (doTrade)
            {
                onStatus?.Invoke("交易DBにインポート中...");
                ImportTradeDb(tradeDbPath!, srcTrade!, onStatus);
            }
            exitToken.ThrowIfCancellationRequested();
            if (repopulate && File.Exists(indexDbPath))
            {
                onStatus?.Invoke("日本語名を付け直し、全文検索の表を作り直しています...");
                using (var gx = new GameDataExtractor(Path.GetDirectoryName(indexDbPath) ?? ".", indexDbPath))
                {
                    if (File.Exists(translationDbPath)) gx.PopulateJapaneseNames(translationDbPath);
                    else gx.RebuildFts5Index();
                }
            }
            // 翻訳・ゲームデータ・用語集 (用語集だけでも。ゲーム用 ini の値が用語集の置き換えで変わるため) を取り込んだら、
            // 品目名 (item_index.name / name_ja) も今の訳に合わせる (作業フォルダの抽出済みの ini と translations.db から。変われば全文検索の表も作り直す)。
            // 合わせられなくても取り込みは失敗にしない
            if ((repopulate || (doTrans && cats.Contains(BackupCategory.Glossary))) && File.Exists(indexDbPath))
            {
                onStatus?.Invoke("品目名を今の訳に合わせています...");
                try
                {
                    GameDataExtractor.RefreshItemNamesFromTranslations(indexDbPath, Path.GetDirectoryName(translationDbPath) ?? ".",
                        log: s => onStatus?.Invoke(s));
                }
                catch (Exception ex) { onStatus?.Invoke($"品目名を今の訳に合わせられませんでした: {ex.Message}"); }
            }
            SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            SqliteConnection.ClearAllPools();
            onStatus?.Invoke(ex is OperationCanceledException ? "アプリを閉じるため、取り込み前の状態に戻しています..." : "インポートに失敗したため、取り込み前の状態に戻しています...");
            // DB ごとに戻す。戻せた DB の控えは消し、戻せなかった DB の控えは .failed-<日時> にして残す (次の取り込みで上書きされないように)。
            // どちらでも印は消す (起動のときに黙って戻すことはしない)
            var notRestored = new List<string>();
            foreach (var (target, backup) in backups)
            {
                try
                {
                    RestoreImportBackup(backup, target);
                    SqliteConnection.ClearAllPools();
                    TryDelete(backup);
                }
                catch (Exception rex)
                {
                    SqliteConnection.ClearAllPools();
                    var kept = InterruptedWork.KeepFailedBackup(backup, markerPath, InterruptedWork.KindImport, onStatus);
                    notRestored.Add($"{target} ({rex.Message}。取り込み前の控えは {kept})");
                }
            }
            SqliteConnection.ClearAllPools();
            InterruptedWork.Clear(markerPath, InterruptedWork.KindImport, onStatus);
            if (notRestored.Count > 0)
                throw new IOException($"インポートに失敗し、次の DB を取り込み前の状態に戻せませんでした (控えを残しています): {string.Join(", ", notRestored)}。元の失敗: {ex.Message}", ex);
            throw new InvalidOperationException($"インポートに失敗したため、取り込み前の状態に戻しました: {ex.Message}", ex);
        }

        // 成功: 印を先に消してから控えを消す (控えを消し損ねても、印が無いので次の起動で戻さない)
        InterruptedWork.Clear(markerPath, InterruptedWork.KindImport, onStatus);
        foreach (var b in backups.Values) TryDelete(b);
        onStatus?.Invoke("インポート完了");
    }

    // 控えの置き場所 (取り込み先と同じフォルダ)
    internal static string CreateImportBackup(string dbPath, string suffix = ".import.bak")
    {
        var bak = dbPath + suffix;
        TryDelete(bak);
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "VACUUM INTO @p";
        cmd.Parameters.AddWithValue("@p", bak);
        cmd.ExecuteNonQuery();
        return bak;
    }

    // 控えから戻す (SQLite のバックアップで書き戻すので、ほかで開いている接続があっても戻せる)
    internal static void RestoreImportBackup(string backupPath, string dbPath)
    {
        using var bak = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        bak.Open();
        using var dest = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        dest.Open();
        bak.BackupDatabase(dest);
        // バックアップ API で戻した中身はいったん -wal に入る (DB と同じくらいの大きさ) ので、本体に書き戻して -wal を空にする
        // (ほかの接続が読んでいて途中で止まったら、少し後に試し直す。RestorePreBuildBackup と同じ)
        SqliteWal.CheckpointOrRetryLater(dest);
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // 控えを作る空き (取り込み先の DB の大きさの合計 + 取り込みで増える分の見込み (importBytes: 取り込むバックアップの DB の大きさの合計) + 100MB) があるか
    private static void EnsureFreeSpaceForBackups(List<string> targets, long importBytes)
    {
        long need = 100L * 1024 * 1024 + targets.Where(File.Exists).Sum(t => new FileInfo(t).Length) + importBytes;
        long free;
        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(targets[0])) ?? "";
            if (root.Length == 0) return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return; }   // 空きを調べられない (ネットワークのフォルダ等) ときは調べずに進む
        if (free < need)
            throw new IOException($"取り込み前の控えを作る空きが足りません ({root} の空き {free / 1024 / 1024:N0} MB、必要 {need / 1024 / 1024:N0} MB)。何も取り込んでいません");
    }

    // translations.db: translations は、b の行 (ゲーム由来の訳) で置き換え、a の個人行 (manual / csv / glossary) は残す
    // (同じ key に b の行があれば english だけ b の値にする)。b の個人行は、a に同じ key の個人行が無ければ b のまま入る。
    // glossary は a 優先の和集合 (同じ english は a のまま)
    private static void ImportTranslationsDb(string aPath, string bPath, HashSet<BackupCategory> cats, Action<string>? onStatus)
    {
        using var conn = OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        var personal = string.Join(", ", App.PersonalTranslationSources.Select(s => $"'{s}'"));
        if (cats.Contains(BackupCategory.Translations) && SourceTableExists(conn, tx, "translations"))
        {
            var cols = CommonColumns(conn, tx, "translations");
            if (!cols.Contains("key", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("バックアップの translations に key の列がありません");
            Exec(conn, tx, $"DELETE FROM main.translations WHERE COALESCE(source, '') NOT IN ({personal})");
            var colList = string.Join(", ", cols.Select(Q));
            var onConflict = cols.Contains("english", StringComparer.OrdinalIgnoreCase)
                ? "DO UPDATE SET english = excluded.english" : "DO NOTHING";
            // INSERT ... SELECT に ON CONFLICT を付けるときは、構文のあいまいさを避けるため SELECT に WHERE が要る
            var n = Exec(conn, tx, $"INSERT INTO main.translations ({colList}) SELECT {colList} FROM src.translations WHERE true ON CONFLICT(key) {onConflict}");
            onStatus?.Invoke($"translations: {n} 件完了");
        }
        if (cats.Contains(BackupCategory.Glossary) && SourceTableExists(conn, tx, "glossary"))
        {
            var cols = CommonColumns(conn, tx, "glossary");
            var colList = string.Join(", ", cols.Select(Q));
            var n = Exec(conn, tx, $"INSERT OR IGNORE INTO main.glossary ({colList}) SELECT {colList} FROM src.glossary");
            onStatus?.Invoke($"glossary: {n} 件追加");
        }
        // AI 翻訳で貯めた人名 (person_names。NameMasker.AddPersonNames)。訳 (Translations) を選んだときに、glossary と同じく a 優先の和集合にする。
        // 古いバックアップに表が無ければ何もしない
        if (cats.Contains(BackupCategory.Translations) && SourceTableExists(conn, tx, "person_names"))
        {
            Exec(conn, tx, TranslationDatabase.PersonNamesDdl);   // 名前を付けない表は main に作られる
            var cols = CommonColumns(conn, tx, "person_names");
            if (cols.Contains("name", StringComparer.OrdinalIgnoreCase))
            {
                var colList = string.Join(", ", cols.Select(Q));
                var n = Exec(conn, tx, $"INSERT OR IGNORE INTO main.person_names ({colList}) SELECT {colList} FROM src.person_names");
                onStatus?.Invoke($"person_names: {n} 件追加");
            }
        }
        MergeTombstones(conn, tx, onStatus);
        tx.Commit();
    }

    // PC 間の同期の墓石 (sync_tombstones): バックアップは復元なので墓石は当てず、表そのものを主キーで a 優先の和集合にする (b に表が無ければ何もしない)
    private static void MergeTombstones(SqliteConnection conn, SqliteTransaction tx, Action<string>? onStatus)
    {
        if (!SourceTableExists(conn, tx, SyncService.TombstonesTable)) return;
        SyncService.EnsureTombstonesTable(conn, tx);
        var colList = string.Join(", ", CommonColumns(conn, tx, SyncService.TombstonesTable).Select(Q));
        var n = Exec(conn, tx, $"INSERT OR IGNORE INTO main.{Q(SyncService.TombstonesTable)} ({colList}) SELECT {colList} FROM src.{Q(SyncService.TombstonesTable)}");
        onStatus?.Invoke($"{SyncService.TombstonesTable}: {n} 件追加");
    }

    // gamedata_cache.db: ゲーム・ネット由来の表 (IndexTables) は b で置き換え。gamedata_meta は、この PC だけの印 (LocalOnlyMetaKeys) を
    // a の値のままにし (b からは取らない。a に無ければ無いまま)、ほかのキーは b で置き換え。knowledge は a の行を残し、b の行は同じ本文 (content) が a に無いものだけ新しい id で足す
    private static void ImportIndexDb(string aPath, string bPath, HashSet<BackupCategory> cats, Action<string>? onStatus)
    {
        using var conn = OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        if (cats.Contains(BackupCategory.Index))
        {
            foreach (var table in IndexTables)
            {
                if (table == "gamedata_meta" || !SourceTableExists(conn, tx, table)) continue;
                var n = ReplaceTable(conn, tx, table);
                onStatus?.Invoke($"{table}: {n} 件完了");
            }
            // name_key 列の無い版 (v1.x) のバックアップの item_index を入れたときは、GameDataExtractor.MigrateItemIndexNameKeyColumn が
            // 列を足すときと同じく、チャットのベクトル検索の埋め込み (item_vectors) の行をすべて消す (name_key は NULL のまま。
            // 次のインデックス構築・装備データの再抽出で埋まり、次のベクトル検索の構築で埋め込みを全件作り直す)。
            // 今の DB には name_key 列がある (EnsureSchema で足してある) ので、取り込んだ後に移行が走らないため、ここで行う
            if (SourceTableExists(conn, tx, "item_index") && !SourceColumnExists(conn, tx, "item_index", "name_key")
                && Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type='table' AND name='item_vectors'") > 0)
            {
                var n = Exec(conn, tx, "DELETE FROM main.item_vectors");
                onStatus?.Invoke($"item_vectors: バックアップの item_index に name_key の列が無いため {n} 件消しました (次のベクトル検索の構築で作り直します)");
            }
            if (SourceTableExists(conn, tx, "gamedata_meta"))
            {
                Exec(conn, tx, $"DELETE FROM main.gamedata_meta WHERE key NOT IN ({LocalOnlyMetaKeyList})");
                Exec(conn, tx, $"INSERT OR REPLACE INTO main.gamedata_meta (key, value) SELECT key, value FROM src.gamedata_meta WHERE key NOT IN ({LocalOnlyMetaKeyList})");
            }
            // 追加の品目 (GameDataExtractor.ExtractExtraItemsAsync) の取り込みの記録は消す (取り込んだ item_index に無い版のバックアップでも、
            // 次の起動で NeedsExtraItems が真になって足りない分だけを足す。足りていれば何も足さずに記録だけ書く)
            if (Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type='table' AND name='gamedata_meta'") > 0)
                Exec(conn, tx, $"DELETE FROM main.gamedata_meta WHERE key = '{GameDataExtractor.ExtraItemsMetaKey}'");
            // BP の品質の増減 (blueprint_modifiers) の無い版のバックアップの blueprints を入れたときは、今の増減を空にし、取り出した記録
            // (GameDataExtractor.BlueprintModifiersMetaKey) も消す (入れた BP と組にならない増減を残さず、次の起動で NeedsBlueprints が真になって取り直すように)
            if (SourceTableExists(conn, tx, "blueprints") && !SourceTableExists(conn, tx, "blueprint_modifiers"))
            {
                if (Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type='table' AND name='blueprint_modifiers'") > 0)
                {
                    var n = Exec(conn, tx, "DELETE FROM main.blueprint_modifiers");
                    onStatus?.Invoke($"blueprint_modifiers: バックアップに無いため {n} 件消しました (次の起動で取り直します)");
                }
                if (Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type='table' AND name='gamedata_meta'") > 0)
                    Exec(conn, tx, $"DELETE FROM main.gamedata_meta WHERE key = '{GameDataExtractor.BlueprintModifiersMetaKey}'");
            }
        }
        if (cats.Contains(BackupCategory.Knowledge) && SourceTableExists(conn, tx, "knowledge"))
        {
            var cols = CommonColumns(conn, tx, "knowledge").Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cols.Contains("content", StringComparer.OrdinalIgnoreCase))
            {
                var colList = string.Join(", ", cols.Select(Q));
                var n = Exec(conn, tx, $"INSERT INTO main.knowledge ({colList}) SELECT {colList} FROM src.knowledge s "
                    + "WHERE NOT EXISTS (SELECT 1 FROM main.knowledge k WHERE k.content = s.content)");
                onStatus?.Invoke($"knowledge: {n} 件追加");
            }
        }
        if (cats.Contains(BackupCategory.Knowledge)) MergeTombstones(conn, tx, onStatus);   // gamedata_cache.db の墓石は knowledge のもの
        NormalizeSequences(conn, tx);
        tx.Commit();
    }

    // trade_cache.db: ネット由来の表は b で置き換え、個人の表は a 優先の和集合 (TradePersonalKeyedTables / TradePersonalUnkeyedTables の説明のとおり)。
    // hangar_meta はキーで分ける (HangarMetaNetKeys は b、ほかは a 優先の和集合)。
    // 所持船 (my_ships) で b から足した船の id が a の id とぶつかって変わったときは、その船の装備 (ship_loadouts の "my|{id}") も新しい id で入れる
    private static void ImportTradeDb(string aPath, string bPath, Action<string>? onStatus)
    {
        using var conn = OpenWithSource(aPath, bPath);
        // 所持船の名寄せ (NormalizeShipName) は a の Ship Matrix と同梱の別名表で行う (HangarService は a と同じフォルダの trade_cache.db を開くので、
        // トランザクションを始める前に、a と b の船名をまとめて正規化しておく)
        var shipNames = NormalizeMyShipNames(conn, aPath);
        using var tx = conn.BeginTransaction();
        Dictionary<long, long>? myShipIdMap = null;   // b の my_ships.id → a での id (足した船は入れた id、a にあった船は a の同じ船の id)
        foreach (var table in TradeTables)
        {
            if (table == ComponentPurchasesTable || table == GameLogImportsTable)
            {
                // 印は主キーで a 優先の和集合。a に表が無ければ作る
                EnsureTradeSchemaTable(conn, tx);
                if (SourceTableExists(conn, tx, table))
                {
                    var cols = string.Join(", ", CommonColumns(conn, tx, table).Select(Q));
                    var n = Exec(conn, tx, $"INSERT OR IGNORE INTO main.{Q(table)} ({cols}) SELECT {cols} FROM src.{Q(table)}");
                    onStatus?.Invoke($"{table}: {n} 件追加");
                }
                // 旧の印 (my_component_purchases) しか無いバックアップ (前の版) からも印が残るよう、旧表の行を game_log_imports に写す
                // (game_log_imports のあるバックアップでは行わない。取り消した印が旧表から戻らないように)
                else if (table == GameLogImportsTable && SourceTableExists(conn, tx, ComponentPurchasesTable))
                    Exec(conn, tx, HangarService.MigrateComponentPurchasesSql);
                continue;
            }
            if (!SourceTableExists(conn, tx, table)) continue;
            if (TradeNetTables.Contains(table))
            {
                var n = ReplaceTable(conn, tx, table);
                onStatus?.Invoke($"{table}: {n} 件完了");
            }
            else if (table == "hangar_meta")
            {
                var netKeys = string.Join(", ", HangarMetaNetKeys.Select(k => $"'{k}'"));
                EnsureMainTable(conn, tx, table);
                Exec(conn, tx, $"DELETE FROM main.hangar_meta WHERE key IN ({netKeys})");
                Exec(conn, tx, $"INSERT OR REPLACE INTO main.hangar_meta (key, value) SELECT key, value FROM src.hangar_meta WHERE key IN ({netKeys})");
                Exec(conn, tx, $"INSERT OR IGNORE INTO main.hangar_meta (key, value) SELECT key, value FROM src.hangar_meta WHERE key NOT IN ({netKeys})");
            }
            else if (table == OreStockTable)
            {
                Exec(conn, tx, OreService.TradeTablesDdl);   // a に表が無ければ作る (OreService の DDL)
                var n = MergeOreStock(conn, tx);
                onStatus?.Invoke($"{table}: {n} 件追加");
            }
            else if (table == "ship_loadouts" && myShipIdMap is { Count: > 0 })
            {
                EnsureMainTable(conn, tx, table);
                var n = MergeShipLoadouts(conn, tx, myShipIdMap);
                onStatus?.Invoke($"{table}: {n} 件追加");
            }
            else if (TradePersonalKeyedTables.Contains(table))
            {
                EnsureMainTable(conn, tx, table);
                var colList = string.Join(", ", CommonColumns(conn, tx, table).Select(Q));
                var n = Exec(conn, tx, $"INSERT OR IGNORE INTO main.{Q(table)} ({colList}) SELECT {colList} FROM src.{Q(table)}");
                onStatus?.Invoke($"{table}: {n} 件追加");
            }
            else if (TradePersonalUnkeyedTables.Contains(table))
            {
                EnsureMainTable(conn, tx, table);
                int n;
                if (table == "my_ships") n = MergeMyShips(conn, tx, shipNames, out myShipIdMap);
                else if (table == "my_components" || table == "my_gear") n = MergeQuantityRows(conn, tx, table);
                else n = MergeDistinctRows(conn, tx, table);
                onStatus?.Invoke($"{table}: {n} 件追加");
            }
        }
        MergeTombstones(conn, tx, onStatus);   // PC 間の同期の墓石 (sync_tombstones) も主キーで a 優先の和集合
        NormalizeSequences(conn, tx);
        tx.Commit();
    }

    // a (main) と b (src) の所持船の名前 → 正規化名 (HangarService.NormalizeShipName: a の Ship Matrix と別名表で名寄せ。解決できなければそのまま)。
    // HangarService は a と同じフォルダの trade_cache.db を開く (a は作業フォルダの trade_cache.db)。表が無ければ空
    private static Dictionary<string, string> NormalizeMyShipNames(SqliteConnection conn, string aPath)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in new[] { "main", "src" })
        {
            if (Scalar(conn, null, $"SELECT COUNT(*) FROM {schema}.sqlite_master WHERE type = 'table' AND name = 'my_ships'") == 0) continue;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT DISTINCT name FROM {schema}.my_ships";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) names.Add(r.GetString(0));
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (names.Count == 0) return result;
        var hangar = new HangarService();
        hangar.SetCacheDir(Path.GetDirectoryName(Path.GetFullPath(aPath)) ?? ".");
        foreach (var n in names) result[n] = hangar.NormalizeShipName(n.Trim());
        return result;
    }

    // my_ships: 正規化した船名が a に無い b の船を足す (a にある船はそのまま。b に同じ船が 2 隻あれば 2 隻とも足す)。
    // b の id が a で空いていればその id のまま、ぶつかれば新しい id で入れる。idMap に b の id → a での id を入れる (足さなかった船は a の同じ船の最初の id)
    private static int MergeMyShips(SqliteConnection conn, SqliteTransaction tx, Dictionary<string, string> shipNames, out Dictionary<long, long> idMap)
    {
        idMap = new Dictionary<long, long>();
        string Norm(string name) => shipNames.TryGetValue(name, out var n) ? n : name.Trim();
        var cols = CommonColumns(conn, tx, "my_ships");
        if (!cols.Contains("name", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("my_ships に name の列がありません");
        var colsNoId = cols.Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
        var hasId = cols.Count != colsNoId.Count;

        var aByName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var aIds = new HashSet<long>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT rowid, name FROM main.my_ships ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                aIds.Add(id);
                var key = Norm(r.IsDBNull(1) ? "" : r.GetString(1));
                if (!aByName.ContainsKey(key)) aByName[key] = id;
            }
        }
        var bRows = new List<(long Id, string Name)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT rowid, name FROM src.my_ships ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read()) bRows.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }

        var added = 0;
        var withId = string.Join(", ", cols.Select(Q));
        var withoutId = string.Join(", ", colsNoId.Select(Q));
        foreach (var (bId, bName) in bRows)
        {
            if (aByName.TryGetValue(Norm(bName), out var aId)) { idMap[bId] = aId; continue; }
            var keepId = hasId && !aIds.Contains(bId);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = keepId
                ? $"INSERT INTO main.my_ships ({withId}) SELECT {withId} FROM src.my_ships WHERE rowid = @id; SELECT last_insert_rowid();"
                : $"INSERT INTO main.my_ships ({withoutId}) SELECT {withoutId} FROM src.my_ships WHERE rowid = @id; SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@id", bId);
            var newId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            aIds.Add(newId);
            idMap[bId] = newId;
            added++;
        }
        return added;
    }

    // ship_loadouts: 主キーで a 優先の和集合。所持船のキー ("my|{my_ships.id}") は MergeMyShips で決めた a での id に付け替える
    // (b の my_ships に無い id のキーは入れない)。ほかのキーはそのまま
    private static int MergeShipLoadouts(SqliteConnection conn, SqliteTransaction tx, Dictionary<long, long> idMap)
    {
        var cols = CommonColumns(conn, tx, "ship_loadouts");
        if (!cols.Contains("ship_key", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("ship_loadouts に ship_key の列がありません");
        var colList = string.Join(", ", cols.Select(Q));
        var n = Exec(conn, tx, $"INSERT OR IGNORE INTO main.ship_loadouts ({colList}) SELECT {colList} FROM src.ship_loadouts WHERE ship_key NOT LIKE 'my|%'");
        var selectList = string.Join(", ", cols.Select(c => c.Equals("ship_key", StringComparison.OrdinalIgnoreCase) ? "@newKey" : Q(c)));
        foreach (var (oldId, newId) in idMap)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT OR IGNORE INTO main.ship_loadouts ({colList}) SELECT {selectList} FROM src.ship_loadouts WHERE ship_key = @oldKey";
            cmd.Parameters.AddWithValue("@newKey", $"my|{newId}");
            cmd.Parameters.AddWithValue("@oldKey", $"my|{oldId}");
            n += cmd.ExecuteNonQuery();
        }
        return n;
    }

    // my_components / my_gear: 同じ品 (item_record と使用可 (usable) が同じ。my_components は品質 (quality_json) も同じ) の行が a にあれば、
    // その行 (id のいちばん小さい行) を残して quantity を max(a, b)、notes を JoinNotes にする。無ければ b の行を新しい id で足す。足した行数を返す。
    // 品質は a に列があるときだけ鍵に入れる (b に列が無ければ b の行は '' = 品質なしとして扱う)。品質は a・b とも揃えた書き方
    // (HangarService.TryNormalizeQualityJson) にして比べる (a の揃っていない値は揃えて書き直し、b から足した行も揃えて入れる。揃えられない値はそのまま)
    private static int MergeQuantityRows(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        var cols = CommonColumns(conn, tx, table);
        if (!cols.Contains("item_record", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{table} に item_record の列がありません");
        var colsNoId = cols.Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
        var colList = string.Join(", ", colsNoId.Select(Q));
        var srcHasUsable = SourceColumnExists(conn, tx, table, "usable");
        var srcHasQty = SourceColumnExists(conn, tx, table, "quantity");
        var srcHasNotes = SourceColumnExists(conn, tx, table, "notes");
        var mainHasQuality = MainColumnExists(conn, tx, table, "quality_json");
        var srcHasQuality = SourceColumnExists(conn, tx, table, "quality_json");

        var bRows = new List<(long Rowid, string Record, bool Usable, long Qty, string Notes, string Quality)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT rowid, item_record, {(srcHasUsable ? "(COALESCE(usable, 1) != 0)" : "1")}, {(srcHasQty ? "COALESCE(quantity, 1)" : "1")}, {(srcHasNotes ? "COALESCE(notes, '')" : "''")}, "
                + $"{(srcHasQuality ? "COALESCE(quality_json, '')" : "''")} FROM src.{Q(table)} ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var quality = r.GetString(5);
                if (HangarService.TryNormalizeQualityJson(quality, out var normalized)) quality = normalized;
                bRows.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.GetInt64(3), r.GetString(4), quality));
            }
        }
        if (mainHasQuality)
        {
            var rewrites = new List<(long Id, string Quality)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"SELECT id, COALESCE(quality_json, '') FROM main.{Q(table)}";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (HangarService.TryNormalizeQualityJson(r.GetString(1), out var normalized) && normalized != r.GetString(1)) rewrites.Add((r.GetInt64(0), normalized));
            }
            foreach (var (rid, quality) in rewrites)
            {
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE main.{Q(table)} SET quality_json = @qj WHERE id = @id";
                upd.Parameters.AddWithValue("@qj", quality);
                upd.Parameters.AddWithValue("@id", rid);
                upd.ExecuteNonQuery();
            }
        }

        var added = 0;
        foreach (var b in bRows)
        {
            long? aId = null; long aQty = 1; var aNotes = "";
            using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = $"SELECT id, COALESCE(quantity, 1), COALESCE(notes, '') FROM main.{Q(table)} WHERE item_record = @r AND (COALESCE(usable, 1) != 0) = @u"
                    + (mainHasQuality ? " AND COALESCE(quality_json, '') = @qj" : "") + " ORDER BY id LIMIT 1";
                find.Parameters.AddWithValue("@r", b.Record);
                find.Parameters.AddWithValue("@u", b.Usable ? 1 : 0);
                if (mainHasQuality) find.Parameters.AddWithValue("@qj", b.Quality);
                using var r = find.ExecuteReader();
                if (r.Read()) { aId = r.GetInt64(0); aQty = r.GetInt64(1); aNotes = r.GetString(2); }
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (aId is { } id)
            {
                var qty = Math.Max(aQty, b.Qty);
                var notes = JoinNotesIdempotent(aNotes, b.Notes);
                if (qty == aQty && notes == aNotes) continue;
                cmd.CommandText = $"UPDATE main.{Q(table)} SET quantity = @q, notes = @o WHERE id = @id";
                cmd.Parameters.AddWithValue("@q", qty);
                cmd.Parameters.AddWithValue("@o", notes);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
                continue;
            }
            cmd.CommandText = $"INSERT INTO main.{Q(table)} ({colList}) SELECT {colList} FROM src.{Q(table)} WHERE rowid = @rid";
            cmd.Parameters.AddWithValue("@rid", b.Rowid);
            cmd.ExecuteNonQuery();
            if (mainHasQuality && srcHasQuality)
            {
                // 足した行の品質を揃えた書き方にする (b の値が揃っていなかったとき。揃えられない値は b のまま)
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = $"UPDATE main.{Q(table)} SET quality_json = @qj WHERE rowid = last_insert_rowid()";
                upd.Parameters.AddWithValue("@qj", b.Quality);
                upd.ExecuteNonQuery();
            }
            added++;
        }
        return added;
    }

    // ore_stock: 同じ組 (resource_uuid × quality (NULL も同じ扱い) × unit) の行が a にあれば、その行を残して quantity を max(a, b)、notes を JoinNotes にする
    // (変わったときだけ updated_at も今にする。OreService の更新と同じ)。無ければ b の行を新しい id で足す。足した行数を返す
    private static int MergeOreStock(SqliteConnection conn, SqliteTransaction tx)
    {
        if (!SourceTableExists(conn, tx, OreStockTable)) return 0;
        var cols = CommonColumns(conn, tx, OreStockTable);
        foreach (var need in new[] { "resource_uuid", "quality", "unit", "quantity" })
            if (!cols.Contains(need, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException($"{OreStockTable} に {need} の列がありません");
        var colsNoId = cols.Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
        var colList = string.Join(", ", colsNoId.Select(Q));
        var srcHasNotes = SourceColumnExists(conn, tx, OreStockTable, "notes");

        var bRows = new List<(long Rowid, string Uuid, long? Quality, string Unit, double Qty, string Notes)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT rowid, resource_uuid, quality, unit, COALESCE(quantity, 0), {(srcHasNotes ? "COALESCE(notes, '')" : "''")} FROM src.ore_stock ORDER BY rowid";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                bRows.Add((r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : Convert.ToInt64(r.GetValue(2)), r.GetString(3), r.GetDouble(4), r.GetString(5)));
        }

        var added = 0;
        foreach (var b in bRows)
        {
            long? aId = null; double aQty = 0; var aNotes = "";
            using (var find = conn.CreateCommand())
            {
                find.Transaction = tx;
                find.CommandText = "SELECT id, COALESCE(quantity, 0), COALESCE(notes, '') FROM main.ore_stock WHERE resource_uuid = @u AND quality IS @q AND unit = @unit ORDER BY id LIMIT 1";
                find.Parameters.AddWithValue("@u", b.Uuid);
                find.Parameters.AddWithValue("@q", (object?)b.Quality ?? DBNull.Value);
                find.Parameters.AddWithValue("@unit", b.Unit);
                using var r = find.ExecuteReader();
                if (r.Read()) { aId = r.GetInt64(0); aQty = r.GetDouble(1); aNotes = r.GetString(2); }
            }
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (aId is { } id)
            {
                var qty = Math.Max(aQty, b.Qty);
                var notes = JoinNotesIdempotent(aNotes, b.Notes);
                if (qty == aQty && notes == aNotes) continue;
                cmd.CommandText = "UPDATE main.ore_stock SET quantity = @n, notes = @o, updated_at = datetime('now','localtime') WHERE id = @id";
                cmd.Parameters.AddWithValue("@n", qty);
                cmd.Parameters.AddWithValue("@o", notes);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
                continue;
            }
            cmd.CommandText = $"INSERT INTO main.ore_stock ({colList}) SELECT {colList} FROM src.ore_stock WHERE rowid = @rid";
            cmd.Parameters.AddWithValue("@rid", b.Rowid);
            cmd.ExecuteNonQuery();
            added++;
        }
        return added;
    }

    // a と b のメモを HangarService.JoinNotes でつなぐ。同じバックアップを 2 回取り込んでも増えないよう、つないだ形 (" / " 区切り) をほどいてから
    // 重複を除いてつなぎ直す (JoinNotes は文の単位で重複を除く)
    private static string JoinNotesIdempotent(string aNotes, string bNotes) =>
        HangarService.JoinNotes(aNotes.Split(" / ").Concat(bNotes.Split(" / ")));

    // hangar_items / hangar_nameable_ships (主キー無し): 全列 (両方にある列) で重複を除いた和集合。a に同じ行 (全列が同じ。NULL 同士も同じ) が無い b の行を足す
    private static int MergeDistinctRows(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        var cols = CommonColumns(conn, tx, table);
        var colList = string.Join(", ", cols.Select(Q));
        var same = string.Join(" AND ", cols.Select(c => $"m.{Q(c)} IS s.{Q(c)}"));
        return Exec(conn, tx, $"INSERT INTO main.{Q(table)} ({colList}) SELECT DISTINCT {colList} FROM src.{Q(table)} s "
            + $"WHERE NOT EXISTS (SELECT 1 FROM main.{Q(table)} m WHERE {same})");
    }

    // a に印の表 (ComponentPurchasesTable / GameLogImportsTable。game_log_imports と一緒に my_gear も) が無ければ作る
    // (b に表が無いときも空の表を用意するため。形は EnsureTradeSchema と同じ)
    internal static void EnsureTradeSchemaTable(SqliteConnection conn, SqliteTransaction tx) =>
        Exec(conn, tx, """
            CREATE TABLE IF NOT EXISTS main.my_component_purchases (
                log_time TEXT NOT NULL,
                player_id TEXT NOT NULL DEFAULT '',
                shop_id TEXT NOT NULL DEFAULT '',
                kiosk_id TEXT NOT NULL DEFAULT '',
                item_name TEXT NOT NULL,
                quantity INTEGER NOT NULL,
                item_record TEXT DEFAULT '',
                added_at TEXT DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (log_time, player_id, shop_id, kiosk_id, item_name, quantity)
            );
            """ + HangarService.GearImportTablesDdl);

    // a の DB を開き、b を "src" として読む
    internal static SqliteConnection OpenWithSource(string aPath, string bPath)
    {
        var conn = new SqliteConnection($"Data Source={aPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "ATTACH DATABASE @p AS src";
        cmd.Parameters.AddWithValue("@p", bPath);
        cmd.ExecuteNonQuery();
        return conn;
    }

    // a の表を b の行で置き換える (列は両方にあるものだけ。a に無い表は b の形で作る)。入れた行数を返す。
    // b の中で、a にだけある一意の索引 (例 ux_item_shop_prices_key) にぶつかる行は、後の行を残す
    private static int ReplaceTable(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        EnsureMainTable(conn, tx, table);
        var colList = string.Join(", ", CommonColumns(conn, tx, table).Select(Q));
        Exec(conn, tx, $"DELETE FROM main.{Q(table)}");
        return Exec(conn, tx, $"INSERT OR REPLACE INTO main.{Q(table)} ({colList}) SELECT {colList} FROM src.{Q(table)}");
    }

    // a に表が無ければ b の CREATE TABLE で作る
    internal static void EnsureMainTable(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = @t";
        cmd.Parameters.AddWithValue("@t", table);
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0) return;
        cmd.CommandText = "SELECT sql FROM src.sqlite_master WHERE type = 'table' AND name = @t";
        var sql = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(sql)) throw new InvalidOperationException($"{table} の表の形が分かりません");
        Exec(conn, tx, sql);
    }

    internal static bool SourceTableExists(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM src.sqlite_master WHERE type = 'table' AND name = @t";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    // b の表に列があるか
    internal static bool SourceColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA src.table_info({Q(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // a (main) の表にその列があるか (SourceColumnExists の main 側)
    internal static bool MainColumnExists(SqliteConnection conn, SqliteTransaction tx, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA main.table_info({Q(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // a と b の両方にある列 (a の順)
    internal static List<string> CommonColumns(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        List<string> Cols(string schema)
        {
            var list = new List<string>();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA {schema}.table_info({Q(table)})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(1));
            return list;
        }
        var src = new HashSet<string>(Cols("src"), StringComparer.OrdinalIgnoreCase);
        var cols = Cols("main").Where(src.Contains).ToList();
        if (cols.Count == 0) throw new InvalidOperationException($"{table} に、今の DB とバックアップの両方にある列がありません");
        return cols;
    }

    // sqlite_sequence (AUTOINCREMENT の表の最後の番号) を、表ごとに 1 行・使った番号の最大以上にする (b の id で入れた表があるため)
    internal static void NormalizeSequences(SqliteConnection conn, SqliteTransaction tx)
    {
        if (Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = 'sqlite_sequence'") == 0) return;
        var names = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT DISTINCT name FROM main.sqlite_sequence";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names.Add(r.GetString(0));
        }
        foreach (var name in names)
        {
            long maxId = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = @t";
                cmd.Parameters.AddWithValue("@t", name);
                if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0)
                    maxId = Scalar(conn, tx, $"SELECT COALESCE(MAX(rowid), 0) FROM main.{Q(name)}");
            }
            using var up = conn.CreateCommand();
            up.Transaction = tx;
            up.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM main.sqlite_sequence WHERE name = @n";
            up.Parameters.AddWithValue("@n", name);
            var seq = Math.Max(maxId, Convert.ToInt64(up.ExecuteScalar() ?? 0L));
            up.CommandText = "DELETE FROM main.sqlite_sequence WHERE name = @n";
            up.ExecuteNonQuery();
            up.CommandText = "INSERT INTO main.sqlite_sequence (name, seq) VALUES (@n, @s)";
            up.Parameters.AddWithValue("@s", seq);
            up.ExecuteNonQuery();
        }
    }

    internal static int Exec(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    internal static long Scalar(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    internal static string Q(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    internal static void EnsureTradeSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
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
                name TEXT NOT NULL, manufacturer TEXT DEFAULT '', scu INTEGER DEFAULT 0,
                notes TEXT DEFAULT '', added_at TEXT DEFAULT (datetime('now','localtime')),
                origin TEXT DEFAULT '', expires_at TEXT, source_key TEXT DEFAULT ''
            );
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
                config_value_cents INTEGER DEFAULT 0,
                availability TEXT DEFAULT ''
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
                fetched_at TEXT,
                cargocapacity REAL
            );
            CREATE TABLE IF NOT EXISTS ship_loaners (
                ship_name TEXT NOT NULL,
                loaner_name TEXT NOT NULL,
                PRIMARY KEY (ship_name, loaner_name)
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
                fetched_at TEXT,
                native_discounted_cents INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS my_components (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                item_record TEXT NOT NULL,
                item_name TEXT DEFAULT '',
                item_type TEXT DEFAULT '',
                size INTEGER DEFAULT 0,
                grade INTEGER DEFAULT 0,
                quantity INTEGER DEFAULT 1,
                usable INTEGER DEFAULT 1,
                notes TEXT DEFAULT '',
                added_at TEXT DEFAULT (datetime('now','localtime')),
                quality_json TEXT DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS ship_loadouts (
                ship_key TEXT NOT NULL,
                port_name TEXT NOT NULL,
                item_record TEXT NOT NULL,
                PRIMARY KEY (ship_key, port_name)
            );
            CREATE TABLE IF NOT EXISTS my_component_purchases (
                log_time TEXT NOT NULL,
                player_id TEXT NOT NULL DEFAULT '',
                shop_id TEXT NOT NULL DEFAULT '',
                kiosk_id TEXT NOT NULL DEFAULT '',
                item_name TEXT NOT NULL,
                quantity INTEGER NOT NULL,
                item_record TEXT DEFAULT '',
                added_at TEXT DEFAULT (datetime('now','localtime')),
                PRIMARY KEY (log_time, player_id, shop_id, kiosk_id, item_name, quantity)
            );
            """ + OreService.TradeTablesDdl   // ［鉱石］タブの 3 つの表 (HangarService.InitDb と同じ DDL)
            + HangarService.GearImportTablesDdl   // 所持個人装備 (my_gear) と［ゲームログ］の印 (game_log_imports)
            + HangarService.GameLogEntriesDdl     // ［ゲームログ］の行 (game_log_entries)
            + HangarService.MyBlueprintsDdl       // 所持している BP (my_blueprints)
            + SyncService.TombstonesDdl;          // PC 間の同期の墓石 (sync_tombstones)
        cmd.ExecuteNonQuery();

        // 旧スキーマ (config_value_cents / availability 列なし) の hangar_pledges に列を追加する (HangarService.MigratePledgesSchema と同等)。
        // 新形式バックアップを旧 DB に取り込む際の列不足を防ぐ
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(hangar_pledges)";
            using var r = check.ExecuteReader();
            while (r.Read()) cols.Add(r.GetString(1));
        }
        if (!cols.Contains("config_value_cents"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE hangar_pledges ADD COLUMN config_value_cents INTEGER DEFAULT 0";
            alter.ExecuteNonQuery();
        }
        if (!cols.Contains("availability"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE hangar_pledges ADD COLUMN availability TEXT DEFAULT ''";
            alter.ExecuteNonQuery();
        }

        // 旧スキーマ (native_discounted_cents 列なし) の store_skus に列を追加する (HangarService.MigrateStoreSchema と同等)
        var storeCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(store_skus)";
            using var r = check.ExecuteReader();
            while (r.Read()) storeCols.Add(r.GetString(1));
        }
        if (!storeCols.Contains("native_discounted_cents"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE store_skus ADD COLUMN native_discounted_cents INTEGER DEFAULT 0";
            alter.ExecuteNonQuery();
        }

        // 旧スキーマ (cargocapacity 列なし) の ship_matrix に列を追加する (HangarService.MigrateShipMatrixSchema と同じ)。
        // バックアップ側にある Ship Matrix の積載量を取り込めるようにするため
        HangarService.MigrateShipMatrixSchema(conn);

        // 旧スキーマ (quality_json 列なし) の my_components に列を追加する (HangarService.MigrateMyComponentsSchema と同じ)。
        // バックアップ・同期の相手の自作品の品質を取り込めるようにするため
        HangarService.MigrateMyComponentsSchema(conn);
    }

    public static async Task ImportAsync(string backupPath, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, ImportMode mode, Action<string>? onStatus = null)
    {
        var cats = categories.ToHashSet();
        var sql = await ReadCompressedSqlAsync(backupPath);
        var statements = ParseStatements(sql);

        var translationNeeded = cats.Contains(BackupCategory.Translations) || cats.Contains(BackupCategory.Glossary);
        var indexNeeded = cats.Contains(BackupCategory.Index) || cats.Contains(BackupCategory.Knowledge);

        var allowedTables = new HashSet<string>();
        foreach (var cat in cats)
            foreach (var t in GetTables(cat))
                allowedTables.Add(t);

        if (translationNeeded)
        {
            onStatus?.Invoke("翻訳DBにインポート中...");
            using var db = new TranslationDatabase(translationDbPath);
            ExecuteFiltered(db.Connection, statements, allowedTables, mode, onStatus);
        }

        if (indexNeeded)
        {
            onStatus?.Invoke("インデックスDBにインポート中...");
            using var conn = new SqliteConnection($"Data Source={indexDbPath}");
            conn.Open();
            EnsureIndexSchema(conn);
            ExecuteFiltered(conn, statements, allowedTables, mode, onStatus);
        }

        onStatus?.Invoke("インポート完了");
    }

    private static void DumpTables(SqliteConnection conn, string[] tables, StringBuilder sb)
    {
        foreach (var table in tables)
        {
            if (!TableExists(conn, table)) continue;

            sb.AppendLine($"-- TABLE: {table}");

            var createSql = GetCreateTableSql(conn, table);
            if (!string.IsNullOrEmpty(createSql))
            {
                sb.AppendLine($"CREATE TABLE IF NOT EXISTS {table} ({ExtractColumnDefs(createSql)});");
            }

            sb.AppendLine($"DELETE FROM {table};");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table}";
            using var reader = cmd.ExecuteReader();

            var colCount = reader.FieldCount;
            var colNames = new string[colCount];
            for (int i = 0; i < colCount; i++)
                colNames[i] = reader.GetName(i);

            var colList = string.Join(", ", colNames);

            while (reader.Read())
            {
                var values = new string[colCount];
                for (int i = 0; i < colCount; i++)
                {
                    if (reader.IsDBNull(i))
                        values[i] = "NULL";
                    else
                    {
                        var fieldType = reader.GetFieldType(i);
                        if (fieldType == typeof(long) || fieldType == typeof(int) || fieldType == typeof(double) || fieldType == typeof(float))
                            values[i] = reader.GetValue(i).ToString()!;
                        else
                            values[i] = $"'{EscapeSql(reader.GetString(i))}'";
                    }
                }
                sb.AppendLine($"INSERT OR REPLACE INTO {table} ({colList}) VALUES ({string.Join(", ", values)});");
            }
            sb.AppendLine();
        }
    }

    private static long CountRows(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    internal static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static string GetCreateTableSql(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string ExtractColumnDefs(string createSql)
    {
        var start = createSql.IndexOf('(');
        var end = createSql.LastIndexOf(')');
        if (start < 0 || end < 0) return "";
        return createSql[(start + 1)..end].Trim();
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static async Task<string> ReadCompressedSqlAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static int CountInserts(string sql, string table)
    {
        int count = 0;
        var pattern = $"INSERT OR REPLACE INTO {table} ";
        int idx = 0;
        while ((idx = sql.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private static List<string> ParseStatements(string sql)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inString = false;

        for (int i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            if (c == '\'' && !inString)
            {
                inString = true;
                sb.Append(c);
            }
            else if (c == '\'' && inString)
            {
                sb.Append(c);
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                }
                else
                {
                    inString = false;
                }
            }
            else if (c == ';' && !inString)
            {
                var stmt = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(stmt) && !stmt.StartsWith("--"))
                    result.Add(stmt);
                sb.Clear();
            }
            else if (c == '\n' && !inString)
            {
                var current = sb.ToString().TrimStart();
                if (current.StartsWith("--"))
                    sb.Clear();
                else
                    sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        var last = sb.ToString().Trim();
        if (!string.IsNullOrEmpty(last) && !last.StartsWith("--"))
            result.Add(last);

        return result;
    }

    private static void ExecuteFiltered(SqliteConnection conn, List<string> statements,
        HashSet<string> allowedTables, ImportMode mode, Action<string>? onStatus)
    {
        using var tx = conn.BeginTransaction();
        var deletedTables = new HashSet<string>();
        int executed = 0;

        foreach (var stmt in statements)
        {
            var tableName = ExtractTableName(stmt);
            if (tableName == null || !allowedTables.Contains(tableName))
                continue;

            var upper = stmt.TrimStart().ToUpperInvariant();

            // gamedata_meta のこの PC だけの印は、消さず・取り込まない (受け取った側の印をそのまま残す)
            if (tableName == "gamedata_meta" && upper.StartsWith("INSERT")
                && GameDataExtractor.LocalOnlyMetaKeys.Any(k => stmt.Contains($"'{k}'", StringComparison.Ordinal)))
                continue;

            if (upper.StartsWith("DELETE FROM"))
            {
                if (mode == ImportMode.Drop)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = tableName == "gamedata_meta" ? $"DELETE FROM gamedata_meta WHERE key NOT IN ({LocalOnlyMetaKeyList})" : stmt;
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                    deletedTables.Add(tableName);
                }
                continue;
            }

            if (upper.StartsWith("INSERT OR REPLACE"))
            {
                if (mode == ImportMode.Add)
                {
                    var adjusted = stmt.Replace("INSERT OR REPLACE INTO", "INSERT OR IGNORE INTO");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = adjusted;
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt;
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                executed++;
                if (executed % 5000 == 0)
                    onStatus?.Invoke($"{executed} 件処理中...");
                continue;
            }

            using var other = conn.CreateCommand();
            other.CommandText = stmt;
            other.Transaction = tx;
            other.ExecuteNonQuery();
        }
        tx.Commit();
    }

    // バックアップに入れない gamedata_meta のキーの SQL 用の一覧 ('a', 'b')
    private static string LocalOnlyMetaKeyList => string.Join(", ", GameDataExtractor.LocalOnlyMetaKeys.Select(k => $"'{k}'"));

    private static string? ExtractTableName(string stmt)
    {
        var upper = stmt.ToUpperInvariant();
        string[] patterns = ["INSERT OR REPLACE INTO ", "DELETE FROM ", "CREATE TABLE IF NOT EXISTS "];
        foreach (var p in patterns)
        {
            var idx = upper.IndexOf(p, StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = stmt[(idx + p.Length)..].TrimStart();
            var end = rest.IndexOfAny([' ', '(', ';']);
            return end > 0 ? rest[..end] : rest.TrimEnd(';');
        }
        return null;
    }

    private static void EnsureIndexSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gamedata_cache (
                query_key TEXT PRIMARY KEY,
                result_json TEXT NOT NULL,
                cached_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS gamedata_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ships (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                manufacturer TEXT,
                career TEXT,
                role TEXT,
                crew_size INTEGER,
                size INTEGER,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL,
                name_en TEXT,
                cargo_scu REAL
            );
            CREATE TABLE IF NOT EXISTS ship_ports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ship_record_name TEXT NOT NULL,
                port_name TEXT,
                item_type TEXT,
                size INTEGER,
                equipped_item TEXT,
                min_size INTEGER,
                max_size INTEGER,
                port_types TEXT
            );
            CREATE TABLE IF NOT EXISTS items (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                item_type TEXT,
                item_sub_type TEXT,
                size INTEGER,
                grade INTEGER,
                manufacturer TEXT,
                component_type TEXT,
                component_json TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS missions (
                record_name TEXT PRIMARY KEY,
                title TEXT,
                title_hud TEXT,
                mission_type TEXT,
                difficulty TEXT,
                mission_giver TEXT,
                location_label TEXT,
                description TEXT,
                reward_min REAL,
                reward_max REAL,
                required_reputation TEXT,
                lawfulness_type TEXT,
                jurisdiction TEXT,
                time_limit TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL,
                wiki_title TEXT,
                wiki_faction TEXT,
                wiki_reward REAL,
                wiki_legality TEXT,
                wiki_enemy_min INTEGER,
                wiki_enemy_max INTEGER,
                wiki_duration_min REAL,
                wiki_uuid TEXT
            );
            CREATE TABLE IF NOT EXISTS commodities (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                symbol TEXT,
                volatility TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS knowledge (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL DEFAULT 'general',
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
        """ + ShopPriceStore.TableDdl + GameDataExtractor.AmmoParamsDdl + GameDataQueryService.ItemVariantsDdl + GameDataQueryService.PlayerDamageParamsDdl
          + GameDataExtractor.ItemPortsDdl + GameDataExtractor.ItemPortLabelsDdl + GameDataExtractor.ResourceTypesDdl
          + SyncService.TombstonesDdl;   // PC 間の同期の墓石 (sync_tombstones)
        cmd.ExecuteNonQuery();

        // 既存の ship_ports に min_size / max_size / port_types 列が無ければ足す (バックアップ側にあると INSERT が列不足で失敗するため)
        GameDataExtractor.AddMissingShipPortsColumns(conn);
        // 既存の resource_types に group_record 列 (資源グループ) が無ければ足す (同じ理由)
        GameDataExtractor.AddMissingResourceTypesColumns(conn);

        // 既存の ships に name_en 列 (船の英語名) が無ければ足す。バックアップ側に name_en があると INSERT が列不足で失敗するため
        var shipCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(ships)";
            using var r = info.ExecuteReader();
            while (r.Read()) shipCols.Add(r.GetString(1));
        }
        if (shipCols.Count > 0 && !shipCols.Contains("name_en"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE ships ADD COLUMN name_en TEXT";
            alter.ExecuteNonQuery();
        }
        // 同じく cargo_scu 列 (ゲームデータから計算した船の積載量) が無ければ足す (バックアップ側の積載量を取り込めるようにするため)
        if (shipCols.Count > 0 && !shipCols.Contains("cargo_scu"))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE ships ADD COLUMN cargo_scu REAL";
            alter.ExecuteNonQuery();
        }
    }
}
