using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public partial class GameDataExtractor : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string StarBreakerVersion = "v0.2.2";
    private const string StarBreakerUrl =
        $"https://github.com/diogotr7/StarBreaker/releases/download/{StarBreakerVersion}/starbreaker-cli-{StarBreakerVersion}-windows-x86_64.zip";

    public string ToolsDir { get; }
    public string DbPath { get; }
    public string StarBreakerExe { get; }

    public event Action<int, string>? ProgressChanged;
    public event Action<string>? StatusChanged;

    private SqliteConnection? _db;

    public GameDataExtractor(string workingDirectory)
    {
        ToolsDir = Path.Combine(workingDirectory, "tools", "starbreaker");
        DbPath = Path.Combine(workingDirectory, "gamedata_cache.db");
        StarBreakerExe = Path.Combine(ToolsDir, "starbreaker.exe");
    }

    // DB のファイルを指定して開く (起動時に同梱 DB を作り直すとき、正式名にする前の "gamedata_cache.db.tmp" に対して
    // 日本語名の付け直し (PopulateJapaneseNames) をするために App から使う)
    internal GameDataExtractor(string workingDirectory, string dbPath) : this(workingDirectory)
    {
        DbPath = dbPath;
    }

    public bool IsStarBreakerInstalled => File.Exists(StarBreakerExe);
    public bool IsReady => IsStarBreakerInstalled && FindDataP4k() != null;

    public string? FindDataP4k()
    {
        var gamePath = App.Config.GamePath;
        if (string.IsNullOrEmpty(gamePath)) return null;

        var candidates = new[]
        {
            Path.Combine(gamePath, "Data.p4k"),
            Path.Combine(gamePath, "data", "Data.p4k"),
            Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "Data.p4k"),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    // ship_ports の現行 DDL (EnsureDb の初回作成と MigrateShipPortsTable の再作成で共用)
    private const string ShipPortsDdl = """
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
            CREATE INDEX IF NOT EXISTS idx_ship_ports_ship ON ship_ports(ship_record_name);
            CREATE INDEX IF NOT EXISTS idx_ship_ports_type ON ship_ports(item_type);
        """;

    // 旧スキーマの ship_ports (FOREIGN KEY 付き / equipped_item 列なし) を既存行を保持したまま現行 DDL に移行する。
    // 手順: ship_ports → ship_ports_old にリネーム → 現行 DDL で ship_ports 作成 → 行をコピー → ship_ports_old を DROP (1 トランザクション)。
    // 他テーブル (missions / items / knowledge / item_vectors 等) には触れない。DB ファイルは削除しない。id 列は再採番される
    private void MigrateShipPortsTable()
    {
        if (_db == null) return;
        string schema;
        using (var chk = _db.CreateCommand())
        {
            chk.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='ship_ports'";
            schema = chk.ExecuteScalar() as string ?? "";
        }
        if (schema.Length == 0) return;   // 未作成 (呼び出し側の CREATE TABLE IF NOT EXISTS に任せる)
        if (!schema.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)
            && schema.Contains("equipped_item", StringComparison.OrdinalIgnoreCase)) return;

        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;

        // 前回異常終了で ship_ports_old が残っている場合は先に片付ける
        cmd.CommandText = "DROP TABLE IF EXISTS ship_ports_old";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "ALTER TABLE ship_ports RENAME TO ship_ports_old";
        cmd.ExecuteNonQuery();

        // RENAME でもインデックス名は旧テーブル側に残るため、先に落とさないと新テーブルの CREATE INDEX IF NOT EXISTS が no-op になる
        cmd.CommandText = "DROP INDEX IF EXISTS idx_ship_ports_ship; DROP INDEX IF EXISTS idx_ship_ports_type;";
        cmd.ExecuteNonQuery();

        cmd.CommandText = ShipPortsDdl;
        cmd.ExecuteNonQuery();

        // 旧テーブルに equipped_item 列があるかを PRAGMA table_info で判定
        bool hasEquippedItem = false;
        cmd.CommandText = "PRAGMA table_info(ship_ports_old)";
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                if (string.Equals(r.GetString(1), "equipped_item", StringComparison.OrdinalIgnoreCase))
                {
                    hasEquippedItem = true;
                    break;
                }
            }
        }

        var equippedExpr = hasEquippedItem ? "equipped_item" : "NULL AS equipped_item";
        cmd.CommandText = "INSERT INTO ship_ports (ship_record_name, port_name, item_type, size, equipped_item) "
                        + $"SELECT ship_record_name, port_name, item_type, size, {equippedExpr} FROM ship_ports_old";
        var copied = cmd.ExecuteNonQuery();

        cmd.CommandText = "DROP TABLE ship_ports_old";
        cmd.ExecuteNonQuery();

        tx.Commit();
        StatusChanged?.Invoke($"ship_ports テーブルを現行スキーマへ移行しました ({copied} 件を保持)");
    }

    // DB を開き、表・列を今の形に揃える (起動時に同梱 DB を取り込む前に、古い版の DB に無い列・表を足すために App から呼ぶ)
    internal void EnsureSchema() => EnsureDb();

    // 起動時に App (PlanBundledDbSync → CheckWorkDbHealth) が quick_check を済ませて ok だった DB (パスと、本体と -wal の印: SqliteWal.FileStamp)。
    // EnsureDb で同じパス・同じ印のファイルを開くときに 1 回だけ quick_check を省く (使ったら消す)。WAL では書き込みが -wal に入り、本体の更新日時が
    // 変わらないことがあるので、-wal の印も比べる
    private static readonly object QuickCheckOkLock = new();
    private static (string Path, string Stamp)? _quickCheckOk;

    internal static void NoteQuickCheckOk(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var stamp = SqliteWal.FileStamp(full);
            lock (QuickCheckOkLock) _quickCheckOk = (full, stamp);
        }
        catch { lock (QuickCheckOkLock) _quickCheckOk = null; }   // 調べられなければ省かない
    }

    private static bool TakeQuickCheckOk(string path)
    {
        lock (QuickCheckOkLock)
        {
            if (_quickCheckOk is not { } ok) return false;
            try
            {
                var full = Path.GetFullPath(path);
                if (!string.Equals(ok.Path, full, StringComparison.OrdinalIgnoreCase)) return false;
                _quickCheckOk = null;   // 同じパスなら、省くかどうかに関わらず 1 回で使い切る
                return File.Exists(full) && SqliteWal.FileStamp(full) == ok.Stamp;
            }
            catch { return false; }
        }
    }

    private void EnsureDb()
    {
        if (_db != null)
        {
            try
            {
                MigrateShipPortsTable();
                MigrateWikiColumns();
                MigrateItemIndexSizeColumn();
                MigrateItemIndexNameKeyColumn();
                MigrateShipsNameEnColumn();
                MigrateShipsCargoScuColumn();
                MigrateShipPortsSizeColumns();
                EnsureItemPortsTable();
                return;
            }
            catch { try { _db?.Dispose(); } catch { } _db = null; }
        }
        var existedBefore = File.Exists(DbPath);
        _db = new SqliteConnection($"Data Source={DbPath}");
        string? corruptReason = null;
        SqliteException? corruptEx = null;   // 退避に失敗したとき再スローする元の例外 (quick_check 不一致の場合は null)
        // 起動時に App が同じファイル (パスと更新日時が同じ) の quick_check を済ませて ok だったら、ここでの quick_check を 1 回省く
        var skipQuickCheck = TakeQuickCheckOk(DbPath);
        try
        {
            _db.Open();
            if (!skipQuickCheck)
            {
                using var qc = _db.CreateCommand();
                qc.CommandText = "PRAGMA quick_check";
                // quick_check は例外を投げずに "ok" 以外の文字列 (例 "*** in database main ***") を返すことがあるので、
                // その場合も破損とみなして退避経路に入れる
                var qcResult = qc.ExecuteScalar()?.ToString();
                if (qcResult != "ok") corruptReason = $"quick_check: {qcResult}";
            }
        }
        catch (SqliteException ex)
        {
            // 退避対象のファイルが無ければそのまま例外を上げる
            if (!File.Exists(DbPath)) throw;
            corruptReason = ex.Message;
            corruptEx = ex;
        }
        if (corruptReason != null)
        {
            // 開けない/破損した DB は削除せず .corrupt-yyyyMMdd_HHmmss へリネーム退避してから新規作成する
            // (-journal / -wal / -shm も一緒に退避。名前は "<退避した名前>-wal" 等で、退避した DB と組になる)
            _db.Dispose();
            _db = null;
            SqliteConnection.ClearAllPools();
            var suffix = $".corrupt-{DateTime.Now:yyyyMMdd_HHmmss}";
            var quarantined = DbPath + suffix;
            try
            {
                // 横のファイルは「退避した DB の名前」+ "-wal" 等にする (退避した DB と組のまま開けるように)
                File.Move(DbPath, quarantined);
                foreach (var ext in new[] { "-journal", "-wal", "-shm" })
                {
                    var side = DbPath + ext;
                    if (File.Exists(side)) File.Move(side, quarantined + ext);
                }
            }
            catch (Exception moveEx)
            {
                // 退避できなかった (他プロセスが開いている等) 場合は無言で新規作成へ進まず、通知してから元の破損理由で例外を上げる
                StatusChanged?.Invoke($"gamedata_cache.db を開けなかったため ({corruptReason}) {Path.GetFileName(quarantined)} へ退避しようとしましたが、退避に失敗しました (ファイルが使用中の可能性): {moveEx.Message}");
                if (corruptEx != null) throw corruptEx;
                throw new IOException($"gamedata_cache.db ({corruptReason}) の退避に失敗しました (ファイルが使用中の可能性): {moveEx.Message}", moveEx);
            }
            StatusChanged?.Invoke($"gamedata_cache.db を開けなかったため ({corruptReason}) {Path.GetFileName(quarantined)} へ退避し、新規作成します");
            _db = new SqliteConnection($"Data Source={DbPath}");
            _db.Open();
            existedBefore = false;
        }
        // 新しく作った DB は WAL にする (前からある DB は起動したときに App が切り替えている)
        if (!existedBefore) SqliteWal.EnsureOn(_db);

        using var cmd = _db.CreateCommand();
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
            CREATE INDEX IF NOT EXISTS idx_ships_name ON ships(name COLLATE NOCASE);
            """ + ShipPortsDdl + """

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
            CREATE INDEX IF NOT EXISTS idx_items_name ON items(name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_items_type ON items(item_type);

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
            CREATE INDEX IF NOT EXISTS idx_missions_type ON missions(mission_type COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_missions_title ON missions(title COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS commodities (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                symbol TEXT,
                volatility TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_commodities_name ON commodities(name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS item_index (
                uuid TEXT PRIMARY KEY,
                record_name TEXT NOT NULL,
                name TEXT,
                item_type TEXT,
                sub_type TEXT,
                manufacturer TEXT,
                name_ja TEXT,
                extracted_at TEXT NOT NULL,
                size INTEGER DEFAULT 0,
                name_key TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_item_index_name ON item_index(name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS idx_item_index_type ON item_index(item_type);
            CREATE INDEX IF NOT EXISTS idx_item_index_record ON item_index(record_name);

            CREATE TABLE IF NOT EXISTS item_vectors (
                uuid TEXT PRIMARY KEY,
                embedding BLOB NOT NULL
            );

            CREATE TABLE IF NOT EXISTS knowledge (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL DEFAULT 'general',
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS faction_reputation (
                record_name TEXT PRIMARY KEY,
                display_key TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS reputation_reward_amount (
                record_name TEXT PRIMARY KEY,
                amount INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS mission_reputation (
                mission_record TEXT NOT NULL,
                title_key TEXT NOT NULL,
                faction_record TEXT NOT NULL,
                amount_record TEXT NOT NULL,
                scope_record TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_mission_reputation_title ON mission_reputation(title_key);
        """ + ShopPriceStore.TableDdl + AmmoParamsDdl + GameDataQueryService.ItemVariantsDdl + GameDataQueryService.PlayerDamageParamsDdl
          + SyncService.TombstonesDdl;   // PC 間の同期の墓石 (sync_tombstones。消した記憶 (knowledge) の印)
        cmd.ExecuteNonQuery();
        MigrateShipPortsTable();   // 旧スキーマの ship_ports (CREATE TABLE IF NOT EXISTS では作り直されない) を現行 DDL に移行
        // missions の wiki の列も、初めて開いたときに足す (今までは 2 回目の EnsureDb でだけ足していたので、
        // 1 回だけ開いて取り込むインポート (DatabaseBackupService) で古い DB に列が無いまま wiki の情報が入らなかった)
        MigrateWikiColumns();
        MigrateItemIndexSizeColumn();
        MigrateItemIndexNameKeyColumn();
        MigrateShipsNameEnColumn();
        MigrateShipsCargoScuColumn();
        MigrateShipPortsSizeColumns();
        EnsureItemPortsTable();
    }

    // 既存 DB の ship_ports に min_size / max_size / port_types 列 (船の XML のハードポイントの本当の大きさと受け付ける種類) が無ければ足す
    private void MigrateShipPortsSizeColumns()
    {
        if (_db == null) return;
        try { AddMissingShipPortsColumns(_db); }
        catch { }
    }

    // ship_ports に min_size / max_size / port_types 列が無ければ足す (DatabaseBackupService の復元先でも使う)。表が無ければ何もしない
    internal static void AddMissingShipPortsColumns(SqliteConnection db)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var chk = db.CreateCommand())
        {
            chk.CommandText = "PRAGMA table_info(ship_ports)";
            using var reader = chk.ExecuteReader();
            while (reader.Read()) cols.Add(reader.GetString(1));
        }
        if (cols.Count == 0) return;
        foreach (var (col, type) in new[] { ("min_size", "INTEGER"), ("max_size", "INTEGER"), ("port_types", "TEXT") })
        {
            if (cols.Contains(col)) continue;
            using var alt = db.CreateCommand();
            alt.CommandText = $"ALTER TABLE ship_ports ADD COLUMN {col} {type}";
            alt.ExecuteNonQuery();
        }
    }

    private void EnsureItemPortsTable()
    {
        if (_db == null) return;
        try
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = ItemPortsDdl + ItemPortLabelsDdl + ResourceTypesDdl + BlueprintsDdl + StarmapNamesDdl;
            cmd.ExecuteNonQuery();
            AddMissingResourceTypesColumns(_db);
        }
        catch { }
    }

    // 前の版の resource_types (group_record 列なし・refined_uuid 列なし) に列を足す (DatabaseBackupService の復元先でも使う)。表が無ければ何もしない。
    // 足したばかりの列は NULL のまま (group_record は NeedsResourceTypes が真になり、起動後の裏の取り出し (RebuildResourceTypesAsync) で入れ直される。
    // refined_uuid は NeedsBlueprints が真になり、RebuildBlueprintsAsync で埋められる)
    internal static void AddMissingResourceTypesColumns(SqliteConnection db)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var chk = db.CreateCommand())
        {
            chk.CommandText = "PRAGMA table_info(resource_types)";
            using var reader = chk.ExecuteReader();
            while (reader.Read()) cols.Add(reader.GetString(1));
        }
        if (cols.Count == 0) return;
        foreach (var col in new[] { "group_record", "refined_uuid" })
        {
            if (cols.Contains(col)) continue;
            using var alt = db.CreateCommand();
            alt.CommandText = $"ALTER TABLE resource_types ADD COLUMN {col} TEXT";
            alt.ExecuteNonQuery();
        }
    }

    // 資源の種類 (ResourceTypeDatabase の record の中の ResourceType)。uuid は record の _RecordId_ (商品売買のログの resourceGUID と同じ)、
    // name_key は displayName ("@items_commodities_…")、name_en は英語ローカライズで引いた英語名 (無ければ NULL)、
    // group_record はその資源を含む直近のグループ record の名前 ("ResourceTypeGroup.Metal" 等。［鉱石］タブの一覧の絞り込みに使う)、
    // refined_uuid は原石の精錬後の資源 (refinedVersion._RecordId_。原石でなければ NULL。［BP］タブで原石の在庫を精錬後の資源に寄せるのに使う)。
    // ［ゲームログ］の商品売買の行で資源名を出すのに使う。インデックス構築で作る (無い・空・group_record が無いなら起動後に裏で一度だけ取り出す: RebuildResourceTypesAsync)
    internal const string ResourceTypesDdl = """

        CREATE TABLE IF NOT EXISTS resource_types (
            uuid TEXT PRIMARY KEY,
            record_name TEXT NOT NULL,
            name_key TEXT,
            name_en TEXT,
            extracted_at TEXT NOT NULL,
            group_record TEXT,
            refined_uuid TEXT
        );
        """;

    // 星図の場所 (StarMapObject) のレコード名 → 表示名の翻訳キー。record_name は "StarMapObject." を除いた名前 (ゲームログの
    // <RequestLocationInventory> の Location[…] と同じ綴り。RR_CRU_LEO 等)、name_key は name の値 ("@Stanton2_Transfer_Seraphim" 等。先頭の @ 付き)。
    // name が @LOC_UNINITIALIZED・空のものは入れない。［ゲームログ］の場所名 (GameLogPanel の Place) で、今までの引き方で引けなかったときに使う。
    // インデックス構築で作る (無い・空なら起動後に裏で一度だけ取り出す: RebuildStarmapNamesAsync。NeedsStarmapNames)
    internal const string StarmapNamesDdl = """

        CREATE TABLE IF NOT EXISTS starmap_names (
            record_name TEXT PRIMARY KEY,
            name_key TEXT
        );
        """;

    // BP (ブループリント)。CraftingBlueprintRecord (できる品・分類・時間・材料) と BlueprintPoolRecord (ミッション報酬の抽選プール)、
    // CraftingGlobalParams の初期所持の白リスト (starter) から作る。インデックス構築で作り直す (ExtractBlueprintsAsync)。
    // 前の版で構築した DB には無い (表は EnsureDb が空で作る) ので、起動後に裏で一度だけ取り出す (RebuildBlueprintsAsync。NeedsBlueprints)。
    //   blueprints: uuid = record の _RecordId_、category は BlueprintCategoryRecord の名前 ("BlueprintCategoryRecord." を除く)、
    //     output_record はできる品の record 名 ("EntityClassDefinition." + entityClass のファイル名。item_index にあればその綴り)、
    //     output_name_key / output_name_en は item_index (無ければ EntityClassDefinition を問い合わせた Name キー) から、
    //     craft_time_sec は craftTime の合計秒、process は processSpecificData._Type_、choose_count は枠のうち選ぶ数 (全部使うなら NULL)、
    //     starter は初期所持の白リストにあれば 1
    //   blueprint_materials: 枠ごとの材料。resource_uuid (ResourceType の _RecordId_) か item_record (CraftingCost_Item の record 名) のどちらかが '' (NULL ではない。主キーのため)。
    //     quantity は SCU (資源) か個数 (品目)。QuantityMultiplier の context があれば掛けた値。min_quality は品質の下限 (0〜1000)
    //   blueprint_pools: 抽選プール → BP。pool_file はプールの record 名を小文字にしたもの (missions.raw_json の blueprintPool のファイル名と結合する用)
    //   blueprint_modifiers: 材料の品質によるできる品の増減。枠 (CraftingCost_Select) の context[] の CraftingCostContext_ResultGameplayPropertyModifiers の
    //     gameplayPropertyModifiers.gameplayPropertyModifiers[] (CraftingGameplayPropertyModifierCommon) の valueRanges[] を 1 行ずつ。
    //     slot_debug_name は blueprint_materials.slot_debug_name と同じ (枠の nameInfo.debugName)、property は gameplayPropertyRecord のファイル名 (.json を除く。
    //     例 gpp_itemresource_coolantgeneration)、range_index は valueRanges の中の位置 (null の要素は飛ばすが位置は数える)、
    //     kind は 'Linear' (value_start/end = modifierAtStart/End。倍率) か 'LinearIntegerAdditive' (value_start/end = additiveModifierAtStart/End。足し算)。
    //     前の版で構築した DB には無い (表は EnsureDb が空で作る) ので、NeedsBlueprints が真になり起動後に一度だけ取り出す
    internal const string BlueprintsDdl = """

        CREATE TABLE IF NOT EXISTS blueprints (
            uuid TEXT PRIMARY KEY,
            record_name TEXT,
            category TEXT,
            output_record TEXT,
            output_name_key TEXT,
            output_name_en TEXT,
            craft_time_sec INTEGER,
            process TEXT,
            choose_count INTEGER,
            starter INTEGER,
            extracted_at TEXT
        );
        CREATE TABLE IF NOT EXISTS blueprint_materials (
            bp_uuid TEXT,
            slot_debug_name TEXT,
            slot_name_key TEXT,
            resource_uuid TEXT,
            item_record TEXT,
            quantity REAL,
            min_quality INTEGER,
            PRIMARY KEY(bp_uuid, slot_debug_name, resource_uuid, item_record)
        );
        CREATE TABLE IF NOT EXISTS blueprint_pools (
            pool_record TEXT,
            pool_file TEXT,
            bp_uuid TEXT,
            PRIMARY KEY(pool_record, bp_uuid)
        );
        CREATE INDEX IF NOT EXISTS idx_blueprint_pools_file ON blueprint_pools(pool_file);
        CREATE TABLE IF NOT EXISTS blueprint_modifiers (
            bp_uuid TEXT NOT NULL,
            slot_debug_name TEXT NOT NULL,
            property TEXT NOT NULL,
            range_index INTEGER NOT NULL,
            kind TEXT NOT NULL,
            start_quality INTEGER NOT NULL,
            end_quality INTEGER NOT NULL,
            value_start REAL NOT NULL,
            value_end REAL NOT NULL,
            PRIMARY KEY (bp_uuid, slot_debug_name, property, range_index)
        );
        """;

    // 品目 (タレット・ジンバル・船の武器と、着る物 CharacterPortRecordTypes) 自身の子ポートの定義 (record の SItemPortContainerComponentParams.Ports)。
    // port_types は受け付ける種類 "Type:SubType,SubType|Type:..." (例 "WeaponGun:Gun", "Char_Armor_Backpack")。インデックス構築で作る。
    // 着る物の分が無い DB (前の版で構築したもの) は、起動後に裏で一度だけ足す (RebuildCharacterPortsAsync)
    internal const string ItemPortsDdl = """

        CREATE TABLE IF NOT EXISTS item_ports (
            record_name TEXT NOT NULL,
            port_name TEXT NOT NULL,
            min_size INTEGER,
            max_size INTEGER,
            port_types TEXT,
            flags TEXT,
            PRIMARY KEY (record_name, port_name)
        );
        """;

    // 品目 (種類を問わない) の中の枠 (SItemPortContainerComponentParams.Ports) の名前と表示名 (DisplayName。"@port_NameJDrive" 等のキー)。
    // item_type は品目の AttachDef.Type。日本語化 (IniMerger) で、英語 ini に無い "itemPort_<枠の名前>" を足すのに使う。
    // 機体ロードアウトマネージャーは枠の名前を "@itemPort_<枠の名前>" で引き、キーが無いと生のキーが出る (利用者がゲームで確認)。インデックス構築で作る
    internal const string ItemPortLabelsDdl = """

        CREATE TABLE IF NOT EXISTS item_port_labels (
            record_name TEXT NOT NULL,
            port_name TEXT NOT NULL,
            display_name TEXT,
            item_type TEXT,
            PRIMARY KEY (record_name, port_name)
        );
        """;

    // dcb query (EntityClassDefinition) の出力から、品目の中の枠の名前と表示名を item_port_labels に入れる (INSERT OR REPLACE)。
    // 品目の種類で絞らない (item_ports と違う)。入れた行数を返す
    internal static int InsertItemPortLabelsFromJson(SqliteConnection db, SqliteTransaction? tx, string rawJson, HashSet<string>? onlyRecords = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO item_port_labels(record_name, port_name, display_name, item_type) VALUES(@rn, @pn, @dn, @it)";
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pPn = cmd.Parameters.Add("@pn", SqliteType.Text);
        var pDn = cmd.Parameters.Add("@dn", SqliteType.Text);
        var pIt = cmd.Parameters.Add("@it", SqliteType.Text);
        int count = 0;
        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (recordName.Length == 0) continue;
                if (onlyRecords != null && !onlyRecords.Contains(recordName)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)
                    || components.ValueKind != JsonValueKind.Array) continue;
                string itemType = "";
                JsonElement? container = null;
                foreach (var comp in components.EnumerateArray())
                {
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                        itemType = ad.TryGetProperty("Type", out var it) ? it.GetString() ?? "" : "";
                    else if (type == "SItemPortContainerComponentParams")
                        container = comp;
                }
                if (container == null || !container.Value.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in ports.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var name = p.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    if (name.Length == 0) continue;
                    var display = p.TryGetProperty("DisplayName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                    pRn.Value = recordName; pPn.Value = name; pDn.Value = display; pIt.Value = itemType;
                    cmd.ExecuteNonQuery();
                    count++;
                }
            }
            catch { }
        }
        return count;
    }

    // 着る物のうち子ポートの定義 (item_ports) を保存する種類 (AttachDef.Type)。チャットが装備の互換性 (バックパックの口・胴アーマーの口) を
    // 口のデータで答えるのに使う。前の版で構築した DB には無いので、起動後に裏で一度だけ Data.p4k から取り出す (RebuildCharacterPortsAsync)。
    // ItemPortRecordTypes より前に置く (静的な初期化の順)
    internal static readonly string[] CharacterPortRecordTypes =
        ["Char_Armor_Undersuit", "Char_Armor_Torso", "Char_Clothing_Torso_1", "Char_Armor_Arms", "Char_Armor_Legs", "Char_Armor_Helmet", "Char_Armor_Backpack"];

    // SQL の IN (...) に並べた CharacterPortRecordTypes
    internal static readonly string CharacterPortTypeList = string.Join(", ", CharacterPortRecordTypes.Select(t => $"'{t}'"));

    // 着る物 (CharacterPortRecordTypes) の子ポートの定義が item_ports に 1 行でもあるか (0 / 1)。
    // GameDataQueryService (口のデータが取り出し済みか) と NeedsCharacterPorts で使う
    internal static readonly string CharacterPortsExistSql =
        $"SELECT EXISTS (SELECT 1 FROM item_ports p WHERE EXISTS (SELECT 1 FROM item_index x WHERE x.record_name = p.record_name AND x.item_type IN ({CharacterPortTypeList})))";

    // 子ポートの定義を保存する品目の種類 (AttachDef.Type): 船の武器類と着る物 (CharacterPortRecordTypes)
    internal static readonly HashSet<string> ItemPortRecordTypes = new(
        new[] { "Turret", "TurretBase", "UtilityTurret", "WeaponMount", "WeaponGun" }.Concat(CharacterPortRecordTypes), StringComparer.Ordinal);

    // SItemPortContainerComponentParams の Ports を (名前, 最小, 最大, 受け付ける種類, flags) にする
    internal static List<(string Name, int Min, int Max, string Types, string Flags)> ReadItemPortDefs(JsonElement portContainer)
    {
        var list = new List<(string, int, int, string, string)>();
        if (!portContainer.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in ports.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object) continue;
            var name = p.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            if (name.Length == 0) continue;
            int Int(string key) => p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
            var types = new List<string>();
            if (p.TryGetProperty("Types", out var ts) && ts.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in ts.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Object) continue;
                    var type = t.TryGetProperty("Type", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() ?? "" : "";
                    if (type.Length == 0) continue;
                    var subs = t.TryGetProperty("SubTypes", out var sv) && sv.ValueKind == JsonValueKind.Array
                        ? string.Join(",", sv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()))
                        : "";
                    types.Add(subs.Length > 0 ? $"{type}:{subs}" : type);
                }
            }
            var flags = p.TryGetProperty("Flags", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() ?? "" : "";
            list.Add((name, Int("MinSize"), Int("MaxSize"), string.Join("|", types.Distinct(StringComparer.Ordinal)), flags));
        }
        return list;
    }

    // dcb query (EntityClassDefinition) の出力から、ItemPortRecordTypes の品目の子ポートの定義を item_ports に入れる (INSERT OR REPLACE)。
    // 入れた行数を返す。インデックス構築とハーネスの両方から使う
    internal static int InsertItemPortsFromJson(SqliteConnection db, SqliteTransaction? tx, string rawJson, HashSet<string>? onlyRecords = null)
        => InsertItemPortRows(db, tx, ReadItemPortRows(rawJson, onlyRecords));

    // item_ports の 1 行 (record 名, 口の名前, 最小, 最大, 受け付ける種類, flags)
    internal readonly record struct ItemPortRow(string Record, string Name, int Min, int Max, string Types, string Flags);

    // dcb query (EntityClassDefinition) の出力から、ItemPortRecordTypes の品目の子ポートの定義を item_ports の行にする (読むだけで書かない)。
    // onlyRecords を渡すと、その record だけ。読めない record は飛ばす
    internal static List<ItemPortRow> ReadItemPortRows(string rawJson, HashSet<string>? onlyRecords = null)
    {
        var rows = new List<ItemPortRow>();
        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (recordName.Length == 0) continue;
                if (onlyRecords != null && !onlyRecords.Contains(recordName)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)
                    || components.ValueKind != JsonValueKind.Array) continue;
                string itemType = "";
                JsonElement? container = null;
                foreach (var comp in components.EnumerateArray())
                {
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                        itemType = ad.TryGetProperty("Type", out var it) ? it.GetString() ?? "" : "";
                    else if (type == "SItemPortContainerComponentParams")
                        container = comp;
                }
                if (container == null || !ItemPortRecordTypes.Contains(itemType)) continue;
                foreach (var (name, min, max, types, flags) in ReadItemPortDefs(container.Value))
                    rows.Add(new ItemPortRow(recordName, name, min, max, types, flags));
            }
            catch { }
        }
        return rows;
    }

    // item_ports に行を入れる (INSERT OR REPLACE)。入れた行数を返す
    internal static int InsertItemPortRows(SqliteConnection db, SqliteTransaction? tx, IEnumerable<ItemPortRow> rows)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO item_ports(record_name, port_name, min_size, max_size, port_types, flags) VALUES(@rn, @pn, @mn, @mx, @pt, @fl)";
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pPn = cmd.Parameters.Add("@pn", SqliteType.Text);
        var pMn = cmd.Parameters.Add("@mn", SqliteType.Integer);
        var pMx = cmd.Parameters.Add("@mx", SqliteType.Integer);
        var pPt = cmd.Parameters.Add("@pt", SqliteType.Text);
        var pFl = cmd.Parameters.Add("@fl", SqliteType.Text);
        int count = 0;
        foreach (var r in rows)
        {
            pRn.Value = r.Record; pPn.Value = r.Name; pMn.Value = r.Min; pMx.Value = r.Max; pPt.Value = r.Types; pFl.Value = r.Flags;
            cmd.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    // 船の XML (Scripts/Entities/Vehicles/Implementations/Xml。CryXmlB を XML に直したもの) のハードポイントの本当の大きさと
    // 受け付ける種類を、ship_ports の最上位のポート (port_name に "/" が無い行) に入れる。
    //   船 (ships.raw_json の VehicleComponentParams) の vehicleDefinition が XML、modification が改造 (同じ XML の <Modification name>)。
    //   改造は patchFile (Modifications/ の XML。中の id の付いた要素で同じ id の要素を置き換える) と
    //   <Elems><Elem idRef name value> (id の要素の属性を書き換える) を当てる。
    //   ハードポイントは <Part class="ItemPort"> の <ItemPort minSize maxSize> と <Types><Type type subtypes>。
    // convertedXmlDir は Xml フォルダを XML に直したもの (Modifications/ を含む)。更新した行数を返す
    internal static int FillShipPortSizesFromXml(SqliteConnection db, string convertedXmlDir, Action<string>? status = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.EnumerateFiles(convertedXmlDir, "*.xml", SearchOption.AllDirectories))
            files[Path.GetRelativePath(convertedXmlDir, f).Replace(Path.DirectorySeparatorChar, '/')] = f;
        var parsed = new Dictionary<string, XElement?>(StringComparer.OrdinalIgnoreCase);
        XElement? Load(string rel)
        {
            if (parsed.TryGetValue(rel, out var e)) return e;
            XElement? root = null;
            try { if (files.TryGetValue(rel, out var path)) root = XElement.Load(path); } catch { }
            return parsed[rel] = root;
        }

        var ships = new List<(string Record, string Definition, string Modification)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT record_name, raw_json FROM ships";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                try
                {
                    using var doc = JsonDocument.Parse(reader.GetString(1));
                    if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var comps)
                        || comps.ValueKind != JsonValueKind.Array) continue;
                    foreach (var c in comps.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "VehicleComponentParams") continue;
                        var def = c.TryGetProperty("vehicleDefinition", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
                        var mod = c.TryGetProperty("modification", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                        if (def.Length > 0) ships.Add((reader.GetString(0), def, mod));
                        break;
                    }
                }
                catch { }
            }
        }

        int updated = 0, noFile = 0;
        using var tx = db.BeginTransaction();
        using var up = db.CreateCommand();
        up.Transaction = tx;
        up.CommandText = "UPDATE ship_ports SET min_size = @mn, max_size = @mx, port_types = @pt WHERE ship_record_name = @s AND port_name = @p COLLATE NOCASE AND instr(port_name, '/') = 0";
        var uMn = up.Parameters.Add("@mn", SqliteType.Integer);
        var uMx = up.Parameters.Add("@mx", SqliteType.Integer);
        var uPt = up.Parameters.Add("@pt", SqliteType.Text);
        var uS = up.Parameters.Add("@s", SqliteType.Text);
        var uP = up.Parameters.Add("@p", SqliteType.Text);
        foreach (var (record, definition, modification) in ships)
        {
            var baseRoot = Load(definition.Replace("\\", "/").Split('/').Last());
            if (baseRoot == null) { noFile++; continue; }
            var root = new XElement(baseRoot);   // 改造を当てるので複製する
            if (modification.Length > 0) ApplyVehicleModification(root, modification, Load);
            foreach (var (port, min, max, types) in ReadVehicleItemPorts(root))
            {
                uMn.Value = min; uMx.Value = max; uPt.Value = types; uS.Value = record; uP.Value = port;
                updated += up.ExecuteNonQuery();
            }
        }
        tx.Commit();
        status?.Invoke($"船のハードポイントの大きさ: {updated} 行 (XML の無い船 {noFile} 隻)");
        return updated;
    }

    // 船の XML に改造 (<Modifications><Modification name>) を当てる
    private static void ApplyVehicleModification(XElement root, string modification, Func<string, XElement?> load)
    {
        var mod = root.Elements("Modifications").Elements("Modification")
            .FirstOrDefault(m => string.Equals((string?)m.Attribute("name"), modification, StringComparison.OrdinalIgnoreCase));
        if (mod == null) return;
        // 同じ id の要素が複数あるときは全部に当てる
        Dictionary<string, List<XElement>> ById() => root.Descendants()
            .Where(e => e.Attribute("id") != null && !e.Ancestors("Modifications").Any())
            .GroupBy(e => (string)e.Attribute("id")!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var patchFile = (string?)mod.Attribute("patchFile");
        if (!string.IsNullOrEmpty(patchFile))
        {
            var patch = load(patchFile.Replace("\\", "/") + ".xml");
            if (patch != null)
            {
                var byId = ById();
                foreach (var el in patch.Elements())
                {
                    var id = (string?)el.Attribute("id");
                    if (id == null || !byId.TryGetValue(id, out var targets)) continue;
                    foreach (var target in targets)
                        if (target.Parent != null) target.ReplaceWith(new XElement(el));
                }
            }
        }
        var ids = ById();
        foreach (var elem in mod.Descendants("Elem"))
        {
            var idRef = (string?)elem.Attribute("idRef");
            var name = (string?)elem.Attribute("name");
            if (idRef == null || string.IsNullOrEmpty(name) || !ids.TryGetValue(idRef, out var targets)) continue;
            foreach (var target in targets)
            {
                var attr = target.Attributes().FirstOrDefault(a => string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase));
                if (attr != null) attr.Value = (string?)elem.Attribute("value") ?? "";
                else target.SetAttributeValue(name, (string?)elem.Attribute("value") ?? "");
            }
        }
    }

    // 船の XML のハードポイント (<Part class="ItemPort"> の <ItemPort>)。同じ名前が複数あれば最初のもの
    private static List<(string Port, int Min, int Max, string Types)> ReadVehicleItemPorts(XElement root)
    {
        var list = new List<(string, int, int, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in root.Descendants("Part"))
        {
            if (!string.Equals((string?)part.Attribute("class"), "ItemPort", StringComparison.OrdinalIgnoreCase)) continue;
            if (part.Ancestors("Modifications").Any()) continue;
            var name = (string?)part.Attribute("name");
            var ip = part.Element("ItemPort");
            if (string.IsNullOrEmpty(name) || ip == null || !seen.Add(name)) continue;
            int Int(string key)
            {
                var a = ip.Attributes().FirstOrDefault(x => string.Equals(x.Name.LocalName, key, StringComparison.OrdinalIgnoreCase));
                return a != null && double.TryParse(a.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? (int)v : 0;
            }
            var types = ip.Elements("Types").Elements("Type")
                .Select(t => ((string?)t.Attribute("type") ?? "", (string?)t.Attribute("subtypes") ?? ""))
                .Where(t => t.Item1.Length > 0)
                .Select(t => t.Item2.Length > 0 ? $"{t.Item1}:{t.Item2}" : t.Item1)
                .Distinct(StringComparer.Ordinal);
            list.Add((name, Int("minSize"), Int("maxSize"), string.Join("|", types)));
        }
        return list;
    }

    // 船の XML を Data.p4k から取り出して XML に直し、ship_ports にハードポイントの本当の大きさを入れる。
    // 失敗しても例外にしない (大きさが無いときは装備タブが既定の装備の大きさで代わりにする)
    private async Task UpdateShipPortSizesAsync(string p4kPath, CancellationToken ct)
    {
        var workDir = Path.Combine(Path.GetDirectoryName(DbPath) ?? ".", "vehicle_xml_tmp");
        try
        {
            StatusChanged?.Invoke("船のハードポイントの大きさを読み込み中...");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            var rawDir = Path.Combine(workDir, "raw");
            var convDir = Path.Combine(workDir, "xml");
            Directory.CreateDirectory(rawDir);
            await RunStarBreakerAsync($"p4k extract --p4k \"{p4kPath}\" -o \"{rawDir}\" --filter \"Data/Scripts/Entities/Vehicles/Implementations/Xml/**\"", ct);
            var xmlDir = Path.Combine(rawDir, "Data", "Scripts", "Entities", "Vehicles", "Implementations", "Xml");
            if (!Directory.Exists(xmlDir)) { StatusChanged?.Invoke("船の XML を取り出せませんでした (ハードポイントの大きさは既定の装備の大きさで代わりにします)"); return; }
            await RunStarBreakerAsync($"cryxml convert-all -i \"{xmlDir}\" -o \"{convDir}\"", ct);
            if (!Directory.Exists(convDir)) { StatusChanged?.Invoke("船の XML を変換できませんでした (ハードポイントの大きさは既定の装備の大きさで代わりにします)"); return; }
            FillShipPortSizesFromXml(_db!, convDir, m => StatusChanged?.Invoke(m));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"船のハードポイントの大きさの読み込みに失敗しました (既定の装備の大きさで代わりにします): {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }
    }

    // ===== 船の積載量 (SCU) =====
    // 船の既定のロードアウト (SEntityComponentDefaultLoadoutParams) をたどり、付いているエンティティの貨物の置き場
    // (SCItemInventoryContainerComponentParams.containerParams が指す InventoryContainer のうち、種類が InventoryOpenContainerType のもの) の
    // 内寸 interiorDimensions (m) の x*y*z を 1 SCU (1.25m 角 = 1.953125m³) で割って足す。
    // たどるのは、entry に手書きで入れ子にしたロードアウト (SItemPortLoadoutManualParams) と、付いたエンティティ自身の既定のロードアウトの両方
    // (例: Constellation の扉 → 扉の既定のロードアウトの貨物の格子)。船自身に付いている置き場は数えない。
    // XML の別ファイルを指すロードアウト (SItemPortLoadoutXMLParams。loadoutPath は Data.p4k の Data/ の下の CryXML) は、そのファイルの
    // <Item portName itemName> (付く先 = itemName) と、Item の下の <Items> (手書きの入れ子と同じ扱い)・<Loadout loadout="別の XML"> を同じ規則でたどる。
    // 付く先は entityClassReference (ファイルの場所) があればそのファイル名、無ければ entityClassName (record 名は大文字小文字を区別せずに引く)
    internal const double CargoUnitVolume = 1.953125;
    private const string CargoOpenContainerType = "InventoryOpenContainerType";
    // 入れ子が循環していたときに止めるための、エンティティをたどる段数の上限
    private const int CargoMaxDepth = 12;

    // 既定のロードアウトの 1 entry (付く先のエンティティの record 名と、entry に手書きで入れ子にしたロードアウト)。
    // XML のロードアウトは、付く先の無い entry (Target が空) の Children にそのファイルの entry を入れて表す (同じファイルは同じ List を共有する)
    internal sealed class CargoEntry
    {
        public string Target = "";
        public List<CargoEntry> Children = new();
    }

    // 船の積載量を計算して ships.cargo_scu に入れる (インデックス構築・装備データの再抽出の中で使う)。
    // 失敗しても構築は続ける (cargo_scu は NULL のまま。表示は UEX・Ship Matrix の値になる)
    private async Task UpdateShipCargoAsync(string p4kPath, CancellationToken ct)
    {
        try
        {
            await UpdateShipCargoCoreAsync(p4kPath, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"船の積載量 (SCU) の計算に失敗しました (UEX・Ship Matrix の値で代わりにします): {ex.Message}");
        }
    }

    // 積載量だけを計算し直す (インデックス構築・装備データの再抽出をせずに ships.cargo_scu を埋める)。計算した船の数を返す。
    // ships の行は今あるものを使う (ships が空なら何もしない)。起動後に裏で一度だけ呼ばれる (MainWindow: ships に cargo_scu が NULL の行があるとき)。
    // 構築・再抽出と同時に走っても作業フォルダがぶつからないよう、取り出し先は別の名前 (dcb_tmp_cargo / cargo_loadout_tmp_cargo) にする。
    // 途中で構築・再抽出が始まったら (IsIndexBuildRunning)、ships が作り直されるので書き込まずに 0 を返す
    public async Task<int> RebuildShipCargoAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        var prevSuffix = _tmpSuffix;
        if (_dcbScopeDepth == 0) _tmpSuffix = "_cargo";
        try
        {
            using var dcbScope = await UseExtractedDcbAsync(p4kPath, ct);
            return await UpdateShipCargoCoreAsync(p4kPath, ct, skipWriteIfIndexBuildStarts: true);
        }
        finally
        {
            _tmpSuffix = prevSuffix;
            if (relay != null) StatusChanged -= relay;
        }
    }

    // 作業フォルダの取り出し先の名前に付ける接尾辞 (RebuildShipCargoAsync の間だけ "_cargo")
    private string _tmpSuffix = "";

    // ships に積載量 (cargo_scu) をまだ計算していない行 (NULL) があるか (起動後の RebuildShipCargoAsync を走らせるか決めるため)
    public bool NeedsShipCargo()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM ships WHERE cargo_scu IS NULL)";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // 着る物 (CharacterPortRecordTypes) の子ポートの定義が item_ports にまだ 1 行も無いか (起動後の RebuildCharacterPortsAsync を走らせるか決めるため)。
    // item_index にその種類の品目が無ければ (構築前の DB) false。表は EnsureDb (EnsureItemPortsTable) が作る
    public bool NeedsCharacterPorts()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM item_index WHERE item_type IN ({CharacterPortTypeList})) AND NOT ({CharacterPortsExistSql})";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // 着る物 (CharacterPortRecordTypes) の子ポートの定義だけを Data.p4k から取り出して item_ports に入れる (インデックス構築・装備データの再抽出をしない)。
    // 入れた行数を返す。対象の record は item_index の今ある行から決める (その種類の品目が無ければ何もしない)。
    // 起動後に裏で一度だけ呼ばれる (MainWindow: NeedsCharacterPorts のとき。前の版で構築した DB・同梱 DB には無いため)。
    // 構築・再抽出と同時に走っても作業フォルダがぶつからないよう、取り出し先は別の名前 (dcb_tmp_charports) にする。
    // 途中で構築・再抽出が始まったら (IsIndexBuildRunning)、item_ports が作り直されるので書き込まずに 0 を返す
    public async Task<int> RebuildCharacterPortsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        var prevSuffix = _tmpSuffix;
        if (_dcbScopeDepth == 0) _tmpSuffix = "_charports";
        try
        {
            using var dcbScope = await UseExtractedDcbAsync(p4kPath, ct);
            return await RebuildCharacterPortsCoreAsync(p4kPath, ct);
        }
        finally
        {
            _tmpSuffix = prevSuffix;
            if (relay != null) StatusChanged -= relay;
        }
    }

    private async Task<int> RebuildCharacterPortsCoreAsync(string p4kPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // 対象の record 名 → 種類 (item_index)。インデックス構築と同じく、record 名を並べた filter にまとめて問い合わせる (BuildTypedExtraFilters)
        var typeMap = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = $"SELECT DISTINCT record_name, item_type FROM item_index WHERE item_type IN ({CharacterPortTypeList})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var rn = r.GetString(0);
                if (rn.StartsWith("EntityClassDefinition.", StringComparison.Ordinal)) typeMap[rn] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
        }
        if (typeMap.Count == 0)
        {
            StatusChanged?.Invoke("装備の口の定義: 対象の品目 (アーマー・アンダースーツ・上着) が item_index に無いので何もしませんでした");
            return 0;
        }
        var filters = BuildTypedExtraFilters(typeMap, Array.Empty<string>());
        StatusChanged?.Invoke($"装備の口の定義 (アーマー・アンダースーツ・上着) を取得中... ({typeMap.Count} 件を {filters.Count} 回に分けて読み込み)");

        // 書き込みは最後にまとめて行う (途中まで入った状態だと NeedsCharacterPorts が false になり、残りが取り出されないため)
        var rows = new List<ItemPortRow>();
        for (int i = 0; i < filters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (filter, only) = filters[i];
            var rawJson = await RunDcbQueryAsync(p4kPath, "EntityClassDefinition", filter, ct);
            if (!string.IsNullOrEmpty(rawJson)) rows.AddRange(ReadItemPortRows(rawJson, only));
            StatusChanged?.Invoke($"装備の口の定義: {i + 1}/{filters.Count} 回目を読みました ({rows.Count} 行, {sw.Elapsed.TotalSeconds:F0}秒)");
        }

        if (IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("装備の口の定義: インデックス構築・装備データの再抽出が始まったので、取り出した定義は書き込みませんでした");
            return 0;
        }
        // 起動後の裏の取り出しは、構築・再抽出・インポートが始まるときに取り消される (MainWindow が取り消して終わるまで待つ)。取り消されたら書き込まない
        ct.ThrowIfCancellationRequested();
        int count;
        using (var tx = _db.BeginTransaction())
        {
            count = InsertItemPortRows(_db, tx, rows);
            tx.Commit();
        }
        var records = rows.Select(x => x.Record).Distinct(StringComparer.Ordinal).Count();
        StatusChanged?.Invoke($"装備の口の定義: {records} 件の品目の口 {count} 行を入れました (対象 {typeMap.Count} 件。口の無い品目は行なし, {sw.Elapsed.TotalSeconds:F1}秒)");
        return count;
    }

    // 資源の種類 (resource_types) が空か、グループ (group_record) が 1 行も入っていないか (前の版で取り出した DB・group_record の無い
    // バックアップを取り込んだ DB)。起動後の RebuildResourceTypesAsync を走らせるか決めるため。表と列は EnsureDb が作る
    public bool NeedsResourceTypes()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM resource_types WHERE group_record IS NOT NULL)";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0;
    }

    // 資源の種類だけを取り出し直す (インデックス構築をせずに resource_types を埋める)。入れた行数を返す。
    // 起動後に裏で一度だけ呼ばれる (MainWindow: resource_types が無い・空のとき)。dcb query 1 回なので Game2.dcb は取り出さず Data.p4k から直接読む。
    // 途中で構築が始まったら (IsIndexBuildRunning)、構築が作り直すので書き込まずに 0 を返す
    public async Task<int> RebuildResourceTypesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        try
        {
            return await ExtractResourceTypesAsync(p4kPath, DateTime.UtcNow.ToString("o"), ct, skipWriteIfIndexBuildStarts: true);
        }
        finally
        {
            if (relay != null) StatusChanged -= relay;
        }
    }

    // 資源の種類を resource_types に入れる。ResourceTypeDatabase の record (1 件) の groups[] (入れ子) の resources[] が ResourceType。
    // 取得できなければ前の行を残して 0 を返す。入れた行数を返す
    private async Task<int> ExtractResourceTypesAsync(string p4kPath, string now, CancellationToken ct, bool skipWriteIfIndexBuildStarts = false)
    {
        StatusChanged?.Invoke("資源の種類 (ResourceType) を取り出し中...");
        var output = await RunDcbQueryRawAsync(p4kPath, "ResourceTypeDatabase", "*", ct);
        var rows = ParseResourceTypeDatabase(output);
        if (rows.Count == 0)
        {
            StatusChanged?.Invoke("資源の種類 (ResourceType) を取得できませんでした (前のデータを残します)");
            return 0;
        }
        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("資源の種類 (ResourceType): インデックス構築・装備データの再抽出が始まったので、取り出した値は書き込みませんでした");
            return 0;
        }
        ct.ThrowIfCancellationRequested();
        // 英語名: 英語ローカライズ (LoadShipNameLocalization と同じ探し方)。無ければ NULL のまま
        var loc = LoadShipNameLocalization();
        using var tx = _db!.BeginTransaction();
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM resource_types";
            del.ExecuteNonQuery();
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO resource_types(uuid,record_name,name_key,name_en,extracted_at,group_record,refined_uuid) VALUES(@u,@rn,@k,@en,@t,@g,@rf)";
            var pU = ins.Parameters.Add("@u", SqliteType.Text);
            var pRn = ins.Parameters.Add("@rn", SqliteType.Text);
            var pK = ins.Parameters.Add("@k", SqliteType.Text);
            var pEn = ins.Parameters.Add("@en", SqliteType.Text);
            var pT = ins.Parameters.Add("@t", SqliteType.Text);
            var pG = ins.Parameters.Add("@g", SqliteType.Text);
            var pRf = ins.Parameters.Add("@rf", SqliteType.Text);
            foreach (var (uuid, recordName, nameKey, groupRecord, refinedUuid) in rows)
            {
                string? en = null;
                if (loc != null && nameKey is { Length: > 1 } && nameKey[0] == '@' && loc.TryGetValue(nameKey[1..], out var text) && !string.IsNullOrWhiteSpace(text))
                    en = text.Trim();
                pU.Value = uuid;
                pRn.Value = recordName;
                pK.Value = (object?)nameKey ?? DBNull.Value;
                pEn.Value = (object?)en ?? DBNull.Value;
                pT.Value = now;
                pG.Value = (object?)groupRecord ?? DBNull.Value;
                pRf.Value = (object?)refinedUuid ?? DBNull.Value;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        StatusChanged?.Invoke($"資源の種類 (ResourceType): {rows.Count} 件" + (loc == null ? " (英語ローカライズが見つからないので英語名は入れていません)" : ""));
        return rows.Count;
    }

    // dcb query "ResourceTypeDatabase" の出力から ResourceType を集める: (uuid = _RecordId_, record_name = _RecordName_, name_key = displayName,
    // group_record = その resources[] を持つ直近のグループ record の _RecordName_ ("ResourceTypeGroup.Metal" 等。無ければ null),
    // refined_uuid = refinedVersion._RecordId_ (原石の精錬後の資源。無ければ null))。
    // _RecordValue_.groups[] の中の resources[] と、入れ子の groups[] をたどる。_Type_ が ResourceType のものだけ
    internal static List<(string Uuid, string RecordName, string? NameKey, string? GroupRecord, string? RefinedUuid)> ParseResourceTypeDatabase(string output)
    {
        var rows = new List<(string, string, string?, string?, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                if (!doc.RootElement.TryGetProperty("_RecordValue_", out var value)) continue;
                WalkResourceGroups(value, null, rows, seen);
            }
            catch { }
        }
        return rows;
    }

    // groupRecord は group 自身の _RecordName_ (ResourceTypeGroup.…)。無ければ (いちばん外の _RecordValue_) 親のものを引き継ぐ
    private static void WalkResourceGroups(JsonElement group, string? groupRecord, List<(string, string, string?, string?, string?)> rows, HashSet<string> seen)
    {
        if (group.ValueKind != JsonValueKind.Object) return;
        if (group.TryGetProperty("_RecordName_", out var grEl) && grEl.ValueKind == JsonValueKind.String && grEl.GetString() is { Length: > 0 } gr)
            groupRecord = gr;
        if (group.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in resources.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (r.TryGetProperty("_Type_", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() != "ResourceType") continue;
                var id = r.TryGetProperty("_RecordId_", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                var name = r.TryGetProperty("_RecordName_", out var nmEl) && nmEl.ValueKind == JsonValueKind.String ? nmEl.GetString() : null;
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !seen.Add(id)) continue;
                var key = r.TryGetProperty("displayName", out var dnEl) && dnEl.ValueKind == JsonValueKind.String ? dnEl.GetString() : null;
                // 原石の精錬後の資源 (refinedVersion は ResourceType の record がそのまま入れ子で入っている。原石でなければ null)
                string? refined = null;
                if (r.TryGetProperty("refinedVersion", out var rvEl) && rvEl.ValueKind == JsonValueKind.Object
                    && rvEl.TryGetProperty("_RecordId_", out var rvId) && rvId.ValueKind == JsonValueKind.String && rvId.GetString() is { Length: > 0 } rid)
                    refined = rid;
                rows.Add((id, name, string.IsNullOrWhiteSpace(key) ? null : key, groupRecord, refined));
            }
        }
        if (group.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Array)
            foreach (var g in groups.EnumerateArray()) WalkResourceGroups(g, groupRecord, rows, seen);
    }

    // === 星図の場所の名前 (starmap_names) ===

    // starmap_names が空か (前の版で構築した DB・前の版のバックアップを取り込んだ DB)。起動後の RebuildStarmapNamesAsync を走らせるか決めるため。表は EnsureDb が作る
    public bool NeedsStarmapNames()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT NOT EXISTS (SELECT 1 FROM starmap_names)";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // 星図の場所の名前だけを取り出し直す (インデックス構築をせずに starmap_names を埋める)。入れた行数を返す。
    // 起動後に裏で一度だけ呼ばれる (MainWindow: NeedsStarmapNames のとき)。RebuildResourceTypesAsync と同じく dcb query 1 回なので
    // Game2.dcb は取り出さず Data.p4k から直接読む。途中で構築が始まったら (IsIndexBuildRunning)、構築が作り直すので書き込まずに 0 を返す
    public async Task<int> RebuildStarmapNamesAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        try
        {
            return await ExtractStarmapNamesAsync(p4kPath, ct, skipWriteIfIndexBuildStarts: true);
        }
        finally
        {
            if (relay != null) StatusChanged -= relay;
        }
    }

    // 星図の場所 (StarMapObject) の name を starmap_names に入れる。取得できなければ前の行を残して 0 を返す。入れた行数を返す
    private async Task<int> ExtractStarmapNamesAsync(string p4kPath, CancellationToken ct, bool skipWriteIfIndexBuildStarts = false)
    {
        StatusChanged?.Invoke("星図の場所の名前 (StarMapObject) を取り出し中...");
        var output = await RunDcbQueryRawAsync(p4kPath, "StarMapObject.name", "*", ct);
        var rows = ParseStarmapNames(output);
        if (rows.Count == 0)
        {
            StatusChanged?.Invoke("星図の場所の名前 (StarMapObject) を取得できませんでした (前のデータを残します)");
            return 0;
        }
        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("星図の場所の名前 (StarMapObject): インデックス構築・装備データの再抽出が始まったので、取り出した値は書き込みませんでした");
            return 0;
        }
        ct.ThrowIfCancellationRequested();
        using var tx = _db!.BeginTransaction();
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM starmap_names";
            del.ExecuteNonQuery();
        }
        using (var ins = _db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO starmap_names(record_name,name_key) VALUES(@r,@k)";
            var pR = ins.Parameters.Add("@r", SqliteType.Text);
            var pK = ins.Parameters.Add("@k", SqliteType.Text);
            foreach (var (recordName, nameKey) in rows)
            {
                pR.Value = recordName;
                pK.Value = nameKey;
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        StatusChanged?.Invoke($"星図の場所の名前 (StarMapObject): {rows.Count} 件");
        return rows.Count;
    }

    // dcb query "StarMapObject.name" の出力 (1 行が「StarMapObject.レコード名<TAB>値」) から (レコード名 ("StarMapObject." を除く), 値) を集める。
    // 値は翻訳キーのまま ("@…")。値が空・@LOC_UNINITIALIZED の行、"StarMapObject." で始まらない行は入れない。同じレコード名は最初のもの
    internal static List<(string RecordName, string NameKey)> ParseStarmapNames(string output)
    {
        const string prefix = "StarMapObject.";
        var rows = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0 || !line.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var record = line[prefix.Length..tab].Trim();
            var key = line[(tab + 1)..].Trim();
            if (record.Length == 0 || key.Length == 0 || string.Equals(key, "@LOC_UNINITIALIZED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(record)) continue;
            rows.Add((record, key));
        }
        return rows;
    }

    // === BP (ブループリント) ===

    // BP の表 (blueprints) が無い・空か、resource_types に原石の精錬後 (refined_uuid) が 1 行も入っていないか、品質の増減 (blueprint_modifiers) が
    // 1 行も無く、取り出した記録 (gamedata_meta の BlueprintModifiersMetaKey) も無いか (前の版で構築した DB・前の版のバックアップを取り込んだ DB)。
    // 起動後の RebuildBlueprintsAsync を走らせるか決めるため。表と列は EnsureDb が作る。
    // 増減は、取り出した結果が 0 行でも記録があれば取り直さない (起動のたびに取り直さないため)
    public bool NeedsBlueprints()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT (NOT EXISTS (SELECT 1 FROM blueprints)) OR (NOT EXISTS (SELECT 1 FROM resource_types WHERE refined_uuid IS NOT NULL))"
            + " OR ((NOT EXISTS (SELECT 1 FROM blueprint_modifiers)) AND (NOT EXISTS (SELECT 1 FROM gamedata_meta WHERE key = @k)))";
        cmd.Parameters.AddWithValue("@k", BlueprintModifiersMetaKey);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
    }

    // BP の品質の増減 (blueprint_modifiers) を取り出した時刻 (UTC, ISO 8601) の gamedata_meta の記録。ExtractBlueprintsAsync が増減を書くのと同じトランザクションで書く。
    // 増減が 0 行でも取り出しは済んだことを NeedsBlueprints に知らせる。インデックス構築の最初に消す (RebuiltDataMetaKeys)。
    // 増減の無い古いバックアップを取り込んだときは DatabaseBackupService.ImportIndexDb が消す (次の起動で取り直す)
    internal const string BlueprintModifiersMetaKey = "blueprint_modifiers_extracted_at";

    // BP だけを取り出し直す (インデックス構築をせずに blueprints / blueprint_materials / blueprint_pools / blueprint_modifiers を作り、resource_types の refined_uuid を埋める)。
    // 入れた BP の件数を返す。起動後に裏で一度だけ呼ばれる (MainWindow: NeedsBlueprints のとき)。
    // RebuildResourceTypesAsync と同じく dcb query が数回なので Game2.dcb は取り出さず Data.p4k から直接読む。
    // 途中で構築が始まったら (IsIndexBuildRunning)、構築が作り直すので書き込まずに 0 を返す
    public async Task<int> RebuildBlueprintsAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        try
        {
            // 原石 → 精錬後 (refined_uuid)。resource_types の行はそのまま、refined_uuid だけ入れる (ResourceTypeDatabase を 1 回問い合わせる)
            StatusChanged?.Invoke("原石の精錬後の資源 (refinedVersion) を取り出し中...");
            var output = await RunDcbQueryRawAsync(p4kPath, "ResourceTypeDatabase", "*", ct);
            var rows = ParseResourceTypeDatabase(output);
            if (rows.Count == 0)
                StatusChanged?.Invoke("原石の精錬後の資源 (refinedVersion) を取得できませんでした (前のデータを残します)");
            else if (IsIndexBuildRunning)
            {
                StatusChanged?.Invoke("BP: インデックス構築・装備データの再抽出が始まったので、取り出した値は書き込みませんでした");
                return 0;
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                int updated = 0;
                using (var tx = _db!.BeginTransaction())
                {
                    using var upd = _db.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE resource_types SET refined_uuid = @rf WHERE uuid = @u";
                    var pRf = upd.Parameters.Add("@rf", SqliteType.Text);
                    var pU = upd.Parameters.Add("@u", SqliteType.Text);
                    foreach (var (uuid, _, _, _, refined) in rows)
                    {
                        if (refined == null) continue;
                        pRf.Value = refined;
                        pU.Value = uuid;
                        updated += upd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                StatusChanged?.Invoke($"原石の精錬後の資源 (refinedVersion): {rows.Count(r => r.RefinedUuid != null)} 件のうち resource_types にある {updated} 件に入れました");
            }
            return await ExtractBlueprintsAsync(p4kPath, DateTime.UtcNow.ToString("o"), ct, skipWriteIfIndexBuildStarts: true);
        }
        finally
        {
            if (relay != null) StatusChanged -= relay;
        }
    }

    private sealed class BlueprintRow
    {
        public string Uuid = "";
        public string RecordName = "";       // "CraftingBlueprintRecord." を除いた record 名
        public string? Category;
        public string? OutputShort;          // entityClass のファイル名 (拡張子なし。元の綴り)
        public string? OutputRecord;         // "EntityClassDefinition." + OutputShort (item_index にあればその綴り)
        public string? OutputNameKey;
        public string? OutputNameEn;
        public long CraftTimeSec;
        public string? Process;
        public int? ChooseCount;
        public bool Starter;
        public List<BlueprintMaterial> Materials = new();
        public List<BlueprintModifier> Modifiers = new();
        public BlueprintRangeSkips RangeSkips = new();   // 品質の増減の範囲のうち飛ばしたもの (進み具合の表示に出す)
    }

    // 品質の増減の範囲のうち、行にせずに飛ばしたものの件数
    private sealed class BlueprintRangeSkips
    {
        public Dictionary<string, int> UnknownTypes = new(StringComparer.Ordinal);   // 種類の分からない範囲の _Type_ (無ければ "(なし)") → 件数
        public int MissingNumbers;                                                   // 種類は分かるが数 (startQuality・endQuality・始めと終わりの値) の欠けた範囲の件数
    }

    // 品質の増減の範囲 1 つ (blueprint_modifiers の 1 行)
    private sealed class BlueprintModifier
    {
        public string SlotDebugName = "";
        public string Property = "";          // gameplayPropertyRecord のファイル名 (.json を除く)
        public int RangeIndex;                // valueRanges の中の位置
        public string Kind = "";              // "Linear" / "LinearIntegerAdditive"
        public int StartQuality;
        public int EndQuality;
        public double ValueStart;
        public double ValueEnd;
    }

    private sealed class BlueprintMaterial
    {
        public string SlotDebugName = "";
        public string? SlotNameKey;
        public string ResourceUuid = "";     // どちらかが '' (主キーのため NULL にしない)
        public string ItemRecord = "";
        public double Quantity;
        public int MinQuality;
    }

    // BP を 4 表 (blueprints / blueprint_materials / blueprint_pools / blueprint_modifiers) に入れ直す。CraftingBlueprintRecord・BlueprintPoolRecord・CraftingGlobalParams を問い合わせ、
    // item_index に無いできる品の名前は EntityClassDefinition を record 名で問い合わせて Name キーを取る (filter は大文字小文字を区別するので、
    // 小文字のファイル名で当たらなければ未解決のまま = 名前はファイル名)。取得できなければ前の行を残して 0 を返す。入れた BP の件数を返す
    private async Task<int> ExtractBlueprintsAsync(string p4kPath, string now, CancellationToken ct, bool skipWriteIfIndexBuildStarts = false)
    {
        var sw = Stopwatch.StartNew();
        StatusChanged?.Invoke("BP (CraftingBlueprintRecord) を取り出し中...");
        var bpJson = await RunDcbQueryRawAsync(p4kPath, "CraftingBlueprintRecord", "*", ct);
        var bps = ParseBlueprints(bpJson);
        if (bps.Count == 0)
        {
            StatusChanged?.Invoke("BP (CraftingBlueprintRecord) を取得できませんでした (前のデータを残します)");
            return 0;
        }
        // 品質の増減の範囲のうち、種類 (Linear・LinearIntegerAdditive) の分からないものは入れずに飛ばしたので、種類ごとの件数を知らせる
        var unknownRanges = bps.SelectMany(b => b.RangeSkips.UnknownTypes).GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(g => (Type: g.Key, Count: g.Sum(kv => kv.Value))).OrderBy(t => t.Type, StringComparer.Ordinal).ToList();
        var missingNumberRanges = bps.Sum(b => b.RangeSkips.MissingNumbers);
        if (unknownRanges.Count > 0 || missingNumberRanges > 0)
        {
            var parts = new List<string>();
            if (unknownRanges.Count > 0)
                parts.Add($"種類の分からない {unknownRanges.Sum(t => t.Count)} 件 (" + string.Join("、", unknownRanges.Select(t => $"{t.Type} {t.Count} 件")) + ")");
            if (missingNumberRanges > 0) parts.Add($"数の欠けた {missingNumberRanges} 件");
            StatusChanged?.Invoke("BP: 品質の増減の範囲のうち、" + string.Join("、", parts) + "を飛ばしました");
        }
        StatusChanged?.Invoke($"BP: {bps.Count} 件 ({sw.Elapsed.TotalSeconds:F1}秒)。抽選プール (BlueprintPoolRecord) を取り出し中...");
        ct.ThrowIfCancellationRequested();
        var poolJson = await RunDcbQueryRawAsync(p4kPath, "BlueprintPoolRecord", "*", ct);
        var pools = ParseBlueprintPools(poolJson);
        ct.ThrowIfCancellationRequested();
        var paramsJson = await RunDcbQueryRawAsync(p4kPath, "CraftingGlobalParams", "*", ct);
        var starters = ParseBlueprintWhitelist(paramsJson);
        StatusChanged?.Invoke($"BP: 抽選プール {pools.Count} 件、初期所持 {starters.Count} 件");

        // できる品の名前: item_index (大文字小文字を区別しない record 名で引く。あればその綴りに揃える)
        var index = new Dictionary<string, (string Record, string? NameKey, string? NameEn)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "SELECT record_name, name_key, name FROM item_index WHERE record_name LIKE 'EntityClassDefinition.%'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var rec = r.GetString(0);
                var shortName = rec["EntityClassDefinition.".Length..];
                if (!index.ContainsKey(shortName))
                    index[shortName] = (rec, r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
            }
        }
        var bpByShort = new Dictionary<string, BlueprintRow>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        foreach (var bp in bps)
        {
            bpByShort.TryAdd(bp.RecordName, bp);
            bp.Starter = starters.Contains(bp.RecordName);
            if (bp.OutputShort == null) continue;
            if (index.TryGetValue(bp.OutputShort, out var hit))
            {
                bp.OutputRecord = hit.Record;
                bp.OutputNameKey = hit.NameKey;
                bp.OutputNameEn = hit.NameEn;
            }
            else
            {
                bp.OutputRecord = "EntityClassDefinition." + bp.OutputShort;
                unresolved.Add(bp.OutputShort);
            }
            // 材料の品目 (手掘りの鉱物など) の record 名も item_index の綴りに揃える
            foreach (var m in bp.Materials)
                if (m.ItemRecord.Length > 0 && index.TryGetValue(m.ItemRecord["EntityClassDefinition.".Length..], out var ih)) m.ItemRecord = ih.Record;
        }

        // item_index に無いできる品 (採掘レーザー・燃料ノズル・ミッション品など) は EntityClassDefinition を問い合わせて Name キーを取る。
        // entityClass のパスのファイル名は小文字で、dcb query の filter は record 名の大文字小文字を区別する (小文字のままでは 1 件も当たらない) ので、
        // 全エンティティの種類の一覧 (QueryEntityTypesAsync。record 名が元の綴りで取れる) から小文字一致で正しい綴りを引き、その綴りで問い合わせる。
        // output_record もその綴りに揃える (一覧に無いものはファイル名のまま)
        if (unresolved.Count > 0)
        {
            var typeMap = await QueryEntityTypesAsync(p4kPath, ct);
            var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // 短縮名 (大文字小文字を区別しない) → 元の綴りの短縮名
            foreach (var rec in typeMap.Keys) spelling.TryAdd(rec["EntityClassDefinition.".Length..], rec["EntityClassDefinition.".Length..]);
            var unresolvedSet = new HashSet<string>(unresolved, StringComparer.OrdinalIgnoreCase);
            foreach (var bp in bps)
                if (bp.OutputShort != null && unresolvedSet.Contains(bp.OutputShort) && spelling.TryGetValue(bp.OutputShort, out var sp))
                    bp.OutputRecord = "EntityClassDefinition." + sp;
            var queryNames = unresolved.Select(u => spelling.TryGetValue(u, out var sp) ? sp : u).ToList();
            var filters = BuildRecordNameFilters(queryNames);
            int unresolvedCount = unresolvedSet.Count;
            StatusChanged?.Invoke($"BP: item_index に無いできる品 {unresolvedCount} 件の名前を取得中... ({filters.Count} 回に分けて読み込み)");
            var loc = LoadShipNameLocalization();
            var names = new Dictionary<string, (string Record, string? NameKey)>(StringComparer.OrdinalIgnoreCase);
            foreach (var filter in filters)
            {
                ct.ThrowIfCancellationRequested();
                var text = await RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition", filter, ct);
                foreach (var (record, nameKey) in ReadEntityNameKeys(text))
                    names.TryAdd(record["EntityClassDefinition.".Length..], (record, nameKey));
            }
            var resolvedShorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var bp in bps)
            {
                if (bp.OutputShort == null || bp.OutputNameKey != null || bp.OutputNameEn != null || !names.TryGetValue(bp.OutputShort, out var n)) continue;
                bp.OutputRecord = n.Record;
                bp.OutputNameKey = n.NameKey;
                if (n.NameKey != null && loc != null && loc.TryGetValue(n.NameKey, out var en) && !string.IsNullOrWhiteSpace(en) && !en.StartsWith('!'))
                    bp.OutputNameEn = en.Trim();
                if (n.NameKey != null) resolvedShorts.Add(bp.OutputShort);
            }
            StatusChanged?.Invoke($"BP: item_index に無いできる品の名前を {resolvedShorts.Count} 件解決しました ({unresolvedCount - resolvedShorts.Count} 件はファイル名のまま)");
        }

        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("BP: インデックス構築・装備データの再抽出が始まったので、取り出した値は書き込みませんでした");
            return 0;
        }
        ct.ThrowIfCancellationRequested();

        int poolRows = 0, materialRows = 0, modifierRows = 0;
        using (var tx = _db.BeginTransaction())
        {
            using (var del = _db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM blueprint_pools; DELETE FROM blueprint_materials; DELETE FROM blueprint_modifiers; DELETE FROM blueprints";
                del.ExecuteNonQuery();
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR REPLACE INTO blueprints(uuid,record_name,category,output_record,output_name_key,output_name_en,craft_time_sec,process,choose_count,starter,extracted_at) "
                    + "VALUES(@u,@rn,@c,@or,@nk,@ne,@ct,@p,@cc,@st,@t)";
                var pU = ins.Parameters.Add("@u", SqliteType.Text);
                var pRn = ins.Parameters.Add("@rn", SqliteType.Text);
                var pC = ins.Parameters.Add("@c", SqliteType.Text);
                var pOr = ins.Parameters.Add("@or", SqliteType.Text);
                var pNk = ins.Parameters.Add("@nk", SqliteType.Text);
                var pNe = ins.Parameters.Add("@ne", SqliteType.Text);
                var pCt = ins.Parameters.Add("@ct", SqliteType.Integer);
                var pP = ins.Parameters.Add("@p", SqliteType.Text);
                var pCc = ins.Parameters.Add("@cc", SqliteType.Integer);
                var pSt = ins.Parameters.Add("@st", SqliteType.Integer);
                var pT = ins.Parameters.Add("@t", SqliteType.Text);
                foreach (var bp in bps)
                {
                    pU.Value = bp.Uuid;
                    pRn.Value = bp.RecordName;
                    pC.Value = (object?)bp.Category ?? DBNull.Value;
                    pOr.Value = (object?)bp.OutputRecord ?? DBNull.Value;
                    pNk.Value = (object?)bp.OutputNameKey ?? DBNull.Value;
                    pNe.Value = (object?)bp.OutputNameEn ?? DBNull.Value;
                    pCt.Value = bp.CraftTimeSec;
                    pP.Value = (object?)bp.Process ?? DBNull.Value;
                    pCc.Value = (object?)bp.ChooseCount ?? DBNull.Value;
                    pSt.Value = bp.Starter ? 1 : 0;
                    pT.Value = now;
                    ins.ExecuteNonQuery();
                }
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR REPLACE INTO blueprint_materials(bp_uuid,slot_debug_name,slot_name_key,resource_uuid,item_record,quantity,min_quality) VALUES(@b,@s,@k,@r,@i,@q,@m)";
                var pB = ins.Parameters.Add("@b", SqliteType.Text);
                var pS = ins.Parameters.Add("@s", SqliteType.Text);
                var pK = ins.Parameters.Add("@k", SqliteType.Text);
                var pR = ins.Parameters.Add("@r", SqliteType.Text);
                var pI = ins.Parameters.Add("@i", SqliteType.Text);
                var pQ = ins.Parameters.Add("@q", SqliteType.Real);
                var pM = ins.Parameters.Add("@m", SqliteType.Integer);
                foreach (var bp in bps)
                    foreach (var m in bp.Materials)
                    {
                        pB.Value = bp.Uuid;
                        pS.Value = m.SlotDebugName;
                        pK.Value = (object?)m.SlotNameKey ?? DBNull.Value;
                        pR.Value = m.ResourceUuid;
                        pI.Value = m.ItemRecord;
                        pQ.Value = m.Quantity;
                        pM.Value = m.MinQuality;
                        materialRows += ins.ExecuteNonQuery();
                    }
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR REPLACE INTO blueprint_modifiers(bp_uuid,slot_debug_name,property,range_index,kind,start_quality,end_quality,value_start,value_end) "
                    + "VALUES(@b,@s,@p,@i,@k,@sq,@eq,@vs,@ve)";
                var pB = ins.Parameters.Add("@b", SqliteType.Text);
                var pS = ins.Parameters.Add("@s", SqliteType.Text);
                var pP = ins.Parameters.Add("@p", SqliteType.Text);
                var pI = ins.Parameters.Add("@i", SqliteType.Integer);
                var pK = ins.Parameters.Add("@k", SqliteType.Text);
                var pSq = ins.Parameters.Add("@sq", SqliteType.Integer);
                var pEq = ins.Parameters.Add("@eq", SqliteType.Integer);
                var pVs = ins.Parameters.Add("@vs", SqliteType.Real);
                var pVe = ins.Parameters.Add("@ve", SqliteType.Real);
                foreach (var bp in bps)
                    foreach (var m in bp.Modifiers)
                    {
                        pB.Value = bp.Uuid;
                        pS.Value = m.SlotDebugName;
                        pP.Value = m.Property;
                        pI.Value = m.RangeIndex;
                        pK.Value = m.Kind;
                        pSq.Value = m.StartQuality;
                        pEq.Value = m.EndQuality;
                        pVs.Value = m.ValueStart;
                        pVe.Value = m.ValueEnd;
                        modifierRows += ins.ExecuteNonQuery();
                    }
            }
            using (var meta = _db.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "INSERT OR REPLACE INTO gamedata_meta(key, value) VALUES(@k, @v)";
                meta.Parameters.AddWithValue("@k", BlueprintModifiersMetaKey);
                meta.Parameters.AddWithValue("@v", now);
                meta.ExecuteNonQuery();
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR REPLACE INTO blueprint_pools(pool_record,pool_file,bp_uuid) VALUES(@p,@f,@b)";
                var pP = ins.Parameters.Add("@p", SqliteType.Text);
                var pF = ins.Parameters.Add("@f", SqliteType.Text);
                var pB = ins.Parameters.Add("@b", SqliteType.Text);
                foreach (var (poolRecord, bpShortNames) in pools)
                {
                    var shortName = poolRecord.StartsWith("BlueprintPoolRecord.", StringComparison.Ordinal) ? poolRecord["BlueprintPoolRecord.".Length..] : poolRecord;
                    foreach (var bpShort in bpShortNames)
                    {
                        if (!bpByShort.TryGetValue(bpShort, out var bp)) continue;
                        pP.Value = poolRecord;
                        pF.Value = shortName.ToLowerInvariant();
                        pB.Value = bp.Uuid;
                        poolRows += ins.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }
        StatusChanged?.Invoke($"BP: {bps.Count} 件、材料 {materialRows} 行、品質の増減 {modifierRows} 行、抽選プール {poolRows} 行を入れました ({sw.Elapsed.TotalSeconds:F1}秒)");
        return bps.Count;
    }

    // dcb query "CraftingBlueprintRecord" の出力を読む。_RecordValue_.blueprint の category (BlueprintCategoryRecord.xxx)、
    // processSpecificData (_Type_ と entityClass のパス)、tiers[0].recipe.costs (craftTime、mandatoryCost = 枠を並べた CraftingCost_Select)。
    // mandatoryCost: 外の CraftingCost_Select (debugName "ASPECTS") の options[] が枠 (CraftingCost_Select。nameInfo.debugName / displayName)、
    // 枠の options[] が CraftingCost_Resource (resource._RecordId_、quantity.standardCargoUnits、minQuality) か CraftingCost_Item (entityClass のパス、quantity、minQuality)。
    // context[] の CraftingCostContext_QuantityMultiplier.multiplier は quantity に掛ける。外の count が枠の数より少なければ choose_count
    private static List<BlueprintRow> ParseBlueprints(string output)
    {
        var list = new List<BlueprintRow>();
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = Str(root, "_RecordName_");
                var uuid = Str(root, "_RecordId_");
                if (recordName == null || uuid == null || !recordName.StartsWith("CraftingBlueprintRecord.", StringComparison.Ordinal)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("blueprint", out var bp) || bp.ValueKind != JsonValueKind.Object) continue;
                var row = new BlueprintRow { Uuid = uuid, RecordName = recordName["CraftingBlueprintRecord.".Length..] };
                if (bp.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.Object && Str(cat, "_RecordName_") is { } catName)
                    row.Category = catName.StartsWith("BlueprintCategoryRecord.", StringComparison.Ordinal) ? catName["BlueprintCategoryRecord.".Length..] : catName;
                if (bp.TryGetProperty("processSpecificData", out var psd) && psd.ValueKind == JsonValueKind.Object)
                {
                    row.Process = Str(psd, "_Type_");
                    row.OutputShort = FileStem(Str(psd, "entityClass"));
                }
                if (bp.TryGetProperty("tiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tier in tiers.EnumerateArray())
                    {
                        if (tier.ValueKind != JsonValueKind.Object || !tier.TryGetProperty("recipe", out var recipe) || recipe.ValueKind != JsonValueKind.Object
                            || !recipe.TryGetProperty("costs", out var costs) || costs.ValueKind != JsonValueKind.Object) continue;
                        if (costs.TryGetProperty("craftTime", out var time) && time.ValueKind == JsonValueKind.Object)
                            row.CraftTimeSec = (long)Math.Round(Num(time, "days") * 86400 + Num(time, "hours") * 3600 + Num(time, "minutes") * 60 + Num(time, "seconds"));
                        if (costs.TryGetProperty("mandatoryCost", out var mc) && mc.ValueKind == JsonValueKind.Object)
                        {
                            if (Str(mc, "_Type_") == "CraftingCost_Select" && mc.TryGetProperty("options", out var slots) && slots.ValueKind == JsonValueKind.Array)
                            {
                                int slotCount = 0;
                                foreach (var slot in slots.EnumerateArray())
                                {
                                    if (slot.ValueKind != JsonValueKind.Object) continue;
                                    slotCount++;
                                    ReadBlueprintSlot(slot, row.Materials, row.Modifiers, row.RangeSkips);
                                }
                                if (mc.TryGetProperty("count", out var cnt) && cnt.ValueKind == JsonValueKind.Number && cnt.TryGetInt32(out var count) && count > 0 && count < slotCount)
                                    row.ChooseCount = count;
                            }
                            else
                                ReadBlueprintSlot(mc, row.Materials, row.Modifiers, row.RangeSkips);   // 枠を並べた形でなければ、それ自体を 1 枠として読む (今のデータには無い)
                        }
                        break;   // tiers は 1 つ (今のデータ)。複数あっても最初のものを使う
                    }
                }
                list.Add(row);
            }
            catch { }
        }
        return list;
    }

    // 枠 (CraftingCost_Select) の options[] の材料と、枠の context[] の品質の増減 (CraftingCostContext_ResultGameplayPropertyModifiers) を読む。
    // 枠でなく材料そのものなら、その 1 件を枠名なしで読む (品質の増減は読まない。材料の側の増減は今のデータでは valueRanges が null だけ)
    private static void ReadBlueprintSlot(JsonElement slot, List<BlueprintMaterial> materials, List<BlueprintModifier> modifiers, BlueprintRangeSkips skips)
    {
        var type = Str(slot, "_Type_");
        if (type == "CraftingCost_Select")
        {
            string slotName = "", slotKeyRaw = "";
            if (slot.TryGetProperty("nameInfo", out var ni) && ni.ValueKind == JsonValueKind.Object)
            {
                slotName = Str(ni, "debugName") ?? "";
                slotKeyRaw = Str(ni, "displayName") ?? "";
            }
            var slotKey = slotKeyRaw.StartsWith('@') && slotKeyRaw.Length > 1 ? slotKeyRaw[1..] : (slotKeyRaw.Length > 0 ? slotKeyRaw : null);
            if (slot.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                foreach (var opt in opts.EnumerateArray())
                    if (opt.ValueKind == JsonValueKind.Object && ReadBlueprintCost(opt, slotName, slotKey) is { } m) materials.Add(m);
            ReadBlueprintSlotModifiers(slot, slotName, modifiers, skips);
            return;
        }
        if (ReadBlueprintCost(slot, "", null) is { } single) materials.Add(single);
    }

    // 枠の context[] → CraftingCostContext_ResultGameplayPropertyModifiers → gameplayPropertyModifiers.gameplayPropertyModifiers[] の valueRanges[] を読む。
    // null の要素・種類の分からない範囲・数の欠けた範囲は飛ばす (range_index は valueRanges の中の位置のまま)。
    // 種類の分からない範囲は skips.UnknownTypes に _Type_ (無ければ "(なし)") ごとの件数を、数の欠けた範囲は skips.MissingNumbers に件数を足す
    // (ExtractBlueprintsAsync が進み具合の表示に出す)
    private static void ReadBlueprintSlotModifiers(JsonElement slot, string slotName, List<BlueprintModifier> modifiers, BlueprintRangeSkips skips)
    {
        if (!slot.TryGetProperty("context", out var ctx) || ctx.ValueKind != JsonValueKind.Array) return;
        foreach (var c in ctx.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || Str(c, "_Type_") != "CraftingCostContext_ResultGameplayPropertyModifiers") continue;
            if (!c.TryGetProperty("gameplayPropertyModifiers", out var list) || list.ValueKind != JsonValueKind.Object
                || !list.TryGetProperty("gameplayPropertyModifiers", out var mods) || mods.ValueKind != JsonValueKind.Array) continue;
            foreach (var mod in mods.EnumerateArray())
            {
                if (mod.ValueKind != JsonValueKind.Object || !mod.TryGetProperty("valueRanges", out var ranges) || ranges.ValueKind != JsonValueKind.Array) continue;
                if (!mod.TryGetProperty("gameplayPropertyRecord", out var gpr)) continue;
                // ファイルのパスの文字列 (dcb の出力の形)。参照の object で来たときは _RecordPath_ のファイル名を使う
                var property = gpr.ValueKind == JsonValueKind.String ? FileStem(gpr.GetString())
                    : gpr.ValueKind == JsonValueKind.Object ? FileStem(Str(gpr, "_RecordPath_")) : null;
                if (property == null) continue;
                int index = 0;
                foreach (var r in ranges.EnumerateArray())
                {
                    int i = index++;
                    if (r.ValueKind != JsonValueKind.Object) continue;
                    var rangeType = Str(r, "_Type_");
                    string kind, startKey, endKey;
                    if (rangeType == "CraftingGameplayPropertyModifierValueRange_Linear") { kind = "Linear"; startKey = "modifierAtStart"; endKey = "modifierAtEnd"; }
                    else if (rangeType == "CraftingGameplayPropertyModifierValueRange_LinearIntegerAdditive") { kind = "LinearIntegerAdditive"; startKey = "additiveModifierAtStart"; endKey = "additiveModifierAtEnd"; }
                    else
                    {
                        var name = rangeType ?? "(なし)";
                        skips.UnknownTypes[name] = skips.UnknownTypes.GetValueOrDefault(name) + 1;
                        continue;
                    }
                    if (!TryNum(r, "startQuality", out var sq) || !TryNum(r, "endQuality", out var eq)
                        || !TryNum(r, startKey, out var vs) || !TryNum(r, endKey, out var ve))
                    {
                        skips.MissingNumbers++;
                        continue;
                    }
                    modifiers.Add(new BlueprintModifier
                    {
                        SlotDebugName = slotName, Property = property, RangeIndex = i, Kind = kind,
                        StartQuality = (int)Math.Round(sq), EndQuality = (int)Math.Round(eq), ValueStart = vs, ValueEnd = ve,
                    });
                }
            }
        }

        static bool TryNum(JsonElement e, string name, out double value)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) { value = v.GetDouble(); return true; }
            value = 0;
            return false;
        }
    }

    // CraftingCost_Resource / CraftingCost_Item を 1 件の材料にする。どちらでもなければ null
    private static BlueprintMaterial? ReadBlueprintCost(JsonElement cost, string slotName, string? slotKey)
    {
        var type = Str(cost, "_Type_");
        double multiplier = 1;
        if (cost.TryGetProperty("context", out var ctx) && ctx.ValueKind == JsonValueKind.Array)
            foreach (var c in ctx.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && Str(c, "_Type_") == "CraftingCostContext_QuantityMultiplier")
                    multiplier *= Num(c, "multiplier") is var mul && mul > 0 ? mul : 1;
        var minQuality = cost.TryGetProperty("minQuality", out var mq) && mq.ValueKind == JsonValueKind.Number && mq.TryGetInt32(out var q) ? q : 0;
        if (type == "CraftingCost_Resource")
        {
            if (!cost.TryGetProperty("resource", out var res) || res.ValueKind != JsonValueKind.Object || Str(res, "_RecordId_") is not { Length: > 0 } rid) return null;
            double qty = cost.TryGetProperty("quantity", out var qEl) && qEl.ValueKind == JsonValueKind.Object ? Num(qEl, "standardCargoUnits") : 0;
            return new BlueprintMaterial { SlotDebugName = slotName, SlotNameKey = slotKey, ResourceUuid = rid, ItemRecord = "", Quantity = qty * multiplier, MinQuality = minQuality };
        }
        if (type == "CraftingCost_Item")
        {
            var stem = FileStem(Str(cost, "entityClass"));
            if (stem == null) return null;
            double qty = cost.TryGetProperty("quantity", out var qEl) && qEl.ValueKind == JsonValueKind.Number ? qEl.GetDouble() : 0;
            return new BlueprintMaterial { SlotDebugName = slotName, SlotNameKey = slotKey, ResourceUuid = "", ItemRecord = "EntityClassDefinition." + stem, Quantity = qty * multiplier, MinQuality = minQuality };
        }
        return null;
    }

    // dcb query "BlueprintPoolRecord" の出力: プールの record 名 → blueprintRewards[].blueprintRecord のファイル名 (拡張子なし = BP の record 名の小文字)
    private static List<(string PoolRecord, List<string> BlueprintShortNames)> ParseBlueprintPools(string output)
    {
        var list = new List<(string, List<string>)>();
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = Str(root, "_RecordName_");
                if (recordName == null || !recordName.StartsWith("BlueprintPoolRecord.", StringComparison.Ordinal)) continue;
                var names = new List<string>();
                if (root.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("blueprintRewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array)
                    foreach (var rw in rewards.EnumerateArray())
                        if (rw.ValueKind == JsonValueKind.Object && FileStem(Str(rw, "blueprintRecord")) is { } stem) names.Add(stem);
                list.Add((recordName, names));
            }
            catch { }
        }
        return list;
    }

    // dcb query "CraftingGlobalParams" の出力: defaultBlueprintSelection.blueprintRecords[] のファイル名 (拡張子なし = BP の record 名の小文字)
    private static HashSet<string> ParseBlueprintWhitelist(string output)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("defaultBlueprintSelection", out var sel) || sel.ValueKind != JsonValueKind.Object) continue;
                if (sel.TryGetProperty("blueprintRecords", out var recs) && recs.ValueKind == JsonValueKind.Array)
                    foreach (var r in recs.EnumerateArray())
                        if (r.ValueKind == JsonValueKind.String && FileStem(r.GetString()) is { } stem) set.Add(stem);
            }
            catch { }
        }
        return set;
    }

    // dcb query "EntityClassDefinition" の出力から (record 名, 名前のローカライズキー ("@" を除く。無ければ null))。
    // 名前は item_index と同じく Components[] の SAttachableComponentParams.AttachDef.Localization.Name
    private static List<(string Record, string? NameKey)> ReadEntityNameKeys(string output)
    {
        var list = new List<(string, string?)>();
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var record = Str(root, "_RecordName_");
                if (record == null || !record.StartsWith("EntityClassDefinition.", StringComparison.Ordinal)) continue;
                string? nameKey = null;
                if (root.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("Components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                    foreach (var comp in comps.EnumerateArray())
                    {
                        if (comp.ValueKind != JsonValueKind.Object || Str(comp, "_Type_") != "SAttachableComponentParams") continue;
                        if (comp.TryGetProperty("AttachDef", out var ad) && ad.ValueKind == JsonValueKind.Object
                            && ad.TryGetProperty("Localization", out var loc) && loc.ValueKind == JsonValueKind.Object
                            && Str(loc, "Name") is { Length: > 1 } nm && nm[0] == '@')
                            nameKey = nm[1..];
                        break;
                    }
                list.Add((record, nameKey));
            }
            catch { }
        }
        return list;
    }

    // "file://…/xxx.json" → "xxx" (拡張子なし。パスでなければそのまま)。空・null なら null
    private static string? FileStem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Replace('\\', '/');
        var i = p.LastIndexOf('/');
        var name = i >= 0 ? p[(i + 1)..] : p;
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^5];
        return name.Length > 0 ? name : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private async Task<int> UpdateShipCargoCoreAsync(string p4kPath, CancellationToken ct, bool skipWriteIfIndexBuildStarts = false)
    {
        var sw = Stopwatch.StartNew();
        StatusChanged?.Invoke("船の積載量 (SCU) を計算中...");

        // XML のロードアウト (Data/Scripts/Loadouts/**) を作業ディレクトリへ取り出す。取り出せなければ XML のロードアウトはたどらずに続ける
        var xmlDir = Path.Combine(Path.GetDirectoryName(DbPath) ?? ".", "cargo_loadout_tmp" + _tmpSuffix);
        try
        {
            Func<string, List<CargoEntry>>? xmlLoader = null;
            int xmlFiles = 0, xmlMissing = 0;
            try
            {
                if (Directory.Exists(xmlDir)) Directory.Delete(xmlDir, true);
                Directory.CreateDirectory(xmlDir);
                await RunStarBreakerAsync($"p4k extract --p4k \"{p4kPath}\" -o \"{xmlDir}\" --filter \"Data/Scripts/Loadouts/**\"", ct);
                var dataDir = Path.Combine(xmlDir, "Data");
                if (Directory.Exists(dataDir))
                    xmlLoader = CreateCargoXmlLoader(dataDir, () => xmlFiles++, () => xmlMissing++);
                else
                    StatusChanged?.Invoke("船の積載量 (SCU): XML のロードアウトを取り出せませんでした (XML のロードアウトはたどらずに計算します)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"船の積載量 (SCU): XML のロードアウトを取り出せませんでした (XML のロードアウトはたどらずに計算します): {ex.Message}");
            }
            var count = await UpdateShipCargoFromLoadoutsAsync(p4kPath, xmlLoader, sw, ct, skipWriteIfIndexBuildStarts);
            if (xmlLoader != null)
                StatusChanged?.Invoke($"船の積載量 (SCU): XML のロードアウト {xmlFiles} 件を読みました" + (xmlMissing > 0 ? $" (見つからない・読めないもの {xmlMissing} 件)" : ""));
            return count;
        }
        finally
        {
            try { if (Directory.Exists(xmlDir)) Directory.Delete(xmlDir, true); } catch { }
        }
    }

    // XML のロードアウトの読み込み口 (loadoutPath → entry の並び)。dataDir は取り出した Data フォルダ。同じファイルは 1 回だけ読み、
    // 入れ子の <Loadout loadout> が循環していたら、循環した所は空にする。onRead / onMissing は読んだ数・見つからない数を数えるため
    internal static Func<string, List<CargoEntry>> CreateCargoXmlLoader(string dataDir, Action? onRead = null, Action? onMissing = null)
    {
        var cache = new Dictionary<string, List<CargoEntry>>(StringComparer.OrdinalIgnoreCase);
        var loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<CargoEntry> Load(string loadoutPath)
        {
            var key = (loadoutPath ?? "").Replace('\\', '/').Trim().TrimStart('/');
            if (key.Length == 0) return new List<CargoEntry>();
            if (cache.TryGetValue(key, out var hit)) return hit;
            if (!loading.Add(key)) return new List<CargoEntry>();
            var result = new List<CargoEntry>();
            try
            {
                var file = Path.Combine(dataDir, key.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file))
                {
                    var root = CryXmlParser.Parse(file).Root;
                    if (root != null) result = ReadCargoXmlItems(root, Load);
                    onRead?.Invoke();
                }
                else onMissing?.Invoke();
            }
            catch { onMissing?.Invoke(); }
            finally { loading.Remove(key); }
            cache[key] = result;
            return result;
        }
        return Load;
    }

    // XML のロードアウトの要素 (Loadout / Item) の下の <Items><Item portName itemName> を entry にする。
    // Item の下の <Items> は Children に、<Loadout loadout="別の XML"> は付く先の無い entry (Children = その XML) にする
    internal static List<CargoEntry> ReadCargoXmlItems(XElement parent, Func<string, List<CargoEntry>> xmlLoader)
    {
        var result = new List<CargoEntry>();
        foreach (var items in parent.Elements("Items"))
            foreach (var item in items.Elements("Item"))
            {
                var entry = new CargoEntry { Target = ((string?)item.Attribute("itemName") ?? "").Trim() };
                entry.Children.AddRange(ReadCargoXmlItems(item, xmlLoader));
                foreach (var sub in item.Elements("Loadout"))
                {
                    var path = (string?)sub.Attribute("loadout");
                    if (!string.IsNullOrWhiteSpace(path)) entry.Children.Add(new CargoEntry { Children = xmlLoader(path) });
                }
                result.Add(entry);
            }
        return result;
    }

    private async Task<int> UpdateShipCargoFromLoadoutsAsync(string p4kPath, Func<string, List<CargoEntry>>? xmlLoader, Stopwatch sw, CancellationToken ct,
        bool skipWriteIfIndexBuildStarts)
    {
        // 船の record 名と既定のロードアウト (ships.raw_json)
        var loadouts = new Dictionary<string, List<CargoEntry>>(StringComparer.OrdinalIgnoreCase);
        var ships = new List<(string RecordName, string ShortName)>();
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "SELECT record_name, raw_json FROM ships";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var rn = r.GetString(0);
                var shortName = RecordShortName(rn);
                ships.Add((rn, shortName));
                try
                {
                    using var doc = JsonDocument.Parse(r.GetString(1));
                    loadouts[shortName] = ReadCargoLoadout(doc.RootElement, xmlLoader);
                }
                catch { loadouts[shortName] = new List<CargoEntry>(); }
            }
        }
        if (ships.Count == 0)
        {
            StatusChanged?.Invoke("船の積載量 (SCU): 船のデータ (ships) が無いので計算しませんでした");
            return 0;
        }

        // 全エンティティの record 名 (大文字小文字はこの名前で filter に渡す。starbreaker の --filter は大文字小文字を区別する)
        var namesText = await RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition.Category", "*", ct);
        var names = ParseEntityRecordNames(namesText);
        if (names.Count == 0) throw new InvalidOperationException("エンティティの一覧を取得できませんでした");

        // 貨物の置き場を持つエンティティと、その置き場 (種類と SCU)
        ct.ThrowIfCancellationRequested();
        var containerText = await RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition.Components[SCItemInventoryContainerComponentParams].containerParams", "*", ct);
        var containers = ParseCargoContainers(containerText);

        // 船に付いているエンティティの既定のロードアウトを、付く先が無くなるまで段ごとに読み込む
        var attempted = new HashSet<string>(loadouts.Keys, StringComparer.OrdinalIgnoreCase);
        var pending = NewCargoTargets(loadouts.Values, names, attempted);
        int round = 0, fetched = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            round++;
            StatusChanged?.Invoke($"船の積載量 (SCU): 付いている部品を読み込み中 ({round} 段目, {pending.Count} 件)");
            attempted.UnionWith(pending);
            var filters = BuildRecordNameFilters(pending.Select(t => names[t]));
            var added = new List<List<CargoEntry>>();
            using (var prefetch = new DcbPrefetcher(filters.Count, (i, token) => RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition", filters[i], token), ct))
            {
                for (int i = 0; i < filters.Count; i++)
                {
                    var text = await prefetch.GetAsync(i);
                    foreach (var block in SplitJsonBlocks(text))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(block);
                            var root = doc.RootElement;
                            var rn = root.TryGetProperty("_RecordName_", out var rnEl) ? rnEl.GetString() ?? "" : "";
                            var shortName = RecordShortName(rn);
                            if (!pending.Contains(shortName) || loadouts.ContainsKey(shortName)) continue;
                            var entries = ReadCargoLoadout(root, xmlLoader);
                            loadouts[shortName] = entries;
                            added.Add(entries);
                            fetched++;
                        }
                        catch { }
                    }
                }
            }
            pending = NewCargoTargets(added, names, attempted);
        }

        // 船ごとに計算して書き込む
        var results = ships.Select(s => (s.RecordName, Scu: ComputeCargoScu(s.ShortName, loadouts, containers, names.ContainsKey))).ToList();
        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke("船の積載量 (SCU): インデックス構築・装備データの再抽出が始まったので、計算した値は書き込みませんでした");
            return 0;
        }
        // 起動後の裏の計算 (RebuildShipCargoAsync) は、構築・再抽出・インポートが始まるときに取り消される (MainWindow が取り消して終わるまで待つ)。
        // 取り消されたら書き込まない
        ct.ThrowIfCancellationRequested();
        using (var tx = _db.BeginTransaction())
        using (var up = _db.CreateCommand())
        {
            up.Transaction = tx;
            up.CommandText = "UPDATE ships SET cargo_scu = @v WHERE record_name = @rn";
            var pV = up.Parameters.Add("@v", SqliteType.Real);
            var pRn = up.Parameters.Add("@rn", SqliteType.Text);
            foreach (var (rn, scu) in results)
            {
                pV.Value = scu;
                pRn.Value = rn;
                up.ExecuteNonQuery();
            }
            tx.Commit();
        }
        var zero = results.Count(x => x.Scu <= 0);
        StatusChanged?.Invoke($"船の積載量 (SCU): {results.Count} 隻を計算しました (貨物の置き場が見つからない船 {zero} 隻。部品 {fetched} 件を {round} 段で読み込み, {sw.Elapsed.TotalSeconds:F1}秒)");
        return results.Count;
    }

    // "EntityClassDefinition.X" → "X" (接頭辞が無ければそのまま)
    private static string RecordShortName(string recordName)
    {
        const string head = "EntityClassDefinition.";
        return recordName.StartsWith(head, StringComparison.Ordinal) ? recordName[head.Length..] : recordName;
    }

    // "EntityClassDefinition.X\t値" の行の並び (dcb query の性質の取り出し) から、record 名 (接頭辞を除く) を集める。
    // キーは大文字小文字を区別しない、値は元の大文字小文字の名前
    internal static Dictionary<string, string> ParseEntityRecordNames(string text)
    {
        const string head = "EntityClassDefinition.";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var rec = line[..tab].Trim();
            if (!rec.StartsWith(head, StringComparison.Ordinal)) continue;
            var shortName = rec[head.Length..];
            if (shortName.Length > 0) map.TryAdd(shortName, shortName);
        }
        return map;
    }

    // "EntityClassDefinition.X\t{InventoryContainer の JSON}" の行の並び (containerParams の取り出し。参照先の record が展開されて出る) から、
    // エンティティごとの置き場 (種類 = inventoryType の __type、SCU = interiorDimensions の x*y*z / 1.953125) を集める
    internal static Dictionary<string, List<(string Type, double Scu)>> ParseCargoContainers(string text)
    {
        const string head = "EntityClassDefinition.";
        var map = new Dictionary<string, List<(string Type, double Scu)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var rec = line[..tab].Trim();
            if (!rec.StartsWith(head, StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line[(tab + 1)..]);
                var c = doc.RootElement;
                if (c.ValueKind != JsonValueKind.Object) continue;
                if (!c.TryGetProperty("interiorDimensions", out var d) || d.ValueKind != JsonValueKind.Object) continue;
                if (!c.TryGetProperty("inventoryType", out var it) || it.ValueKind != JsonValueKind.Object) continue;
                var type = it.TryGetProperty("__type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                var x = GetNum(d, "x"); var y = GetNum(d, "y"); var z = GetNum(d, "z");
                var shortName = rec[head.Length..];
                if (!map.TryGetValue(shortName, out var list)) map[shortName] = list = new List<(string, double)>();
                list.Add((type, x * y * z / CargoUnitVolume));
            }
            catch { }
        }
        return map;
    }

    // record (dcb query の 1 件の JSON) の既定のロードアウトの entry を読む。SItemPortLoadoutXMLParams は xmlLoader で読む (null ならたどらない)
    internal static List<CargoEntry> ReadCargoLoadout(JsonElement root, Func<string, List<CargoEntry>>? xmlLoader = null)
    {
        var result = new List<CargoEntry>();
        if (!root.TryGetProperty("_RecordValue_", out var rv) || rv.ValueKind != JsonValueKind.Object) return result;
        if (!rv.TryGetProperty("Components", out var comps) || comps.ValueKind != JsonValueKind.Array) return result;
        foreach (var c in comps.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object) continue;
            if (!c.TryGetProperty("_Type_", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "SEntityComponentDefaultLoadoutParams") continue;
            if (c.TryGetProperty("loadout", out var lo)) ReadCargoEntries(lo, result, xmlLoader);
        }
        return result;
    }

    private static void ReadCargoEntries(JsonElement loadout, List<CargoEntry> into, Func<string, List<CargoEntry>>? xmlLoader)
    {
        if (loadout.ValueKind != JsonValueKind.Object) return;
        if (!loadout.TryGetProperty("_Type_", out var lt) || lt.ValueKind != JsonValueKind.String) return;
        if (lt.GetString() == "SItemPortLoadoutXMLParams")
        {
            // XML のロードアウト: 付く先の無い entry の Children にそのファイルの entry を入れる
            var path = loadout.TryGetProperty("loadoutPath", out var lp) && lp.ValueKind == JsonValueKind.String ? lp.GetString() ?? "" : "";
            if (xmlLoader != null && path.Length > 0) into.Add(new CargoEntry { Children = xmlLoader(path) });
            return;
        }
        if (lt.GetString() != "SItemPortLoadoutManualParams") return;
        if (!loadout.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (!e.TryGetProperty("_Type_", out var et) || et.ValueKind != JsonValueKind.String || et.GetString() != "SItemPortLoadoutEntryParams") continue;
            var entry = new CargoEntry { Target = CargoEntryTarget(e) };
            if (e.TryGetProperty("loadout", out var sub)) ReadCargoEntries(sub, entry.Children, xmlLoader);
            into.Add(entry);
        }
    }

    // entry の付く先: entityClassReference があればそのファイル名 (拡張子 .json を除く)、無ければ entityClassName
    private static string CargoEntryTarget(JsonElement e)
    {
        var reference = e.TryGetProperty("entityClassReference", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() ?? "" : "";
        if (reference.Length > 0)
        {
            var slash = reference.LastIndexOf('/');
            var basename = slash >= 0 ? reference[(slash + 1)..] : reference;
            if (basename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) basename = basename[..^5];
            return basename;
        }
        return e.TryGetProperty("entityClassName", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() ?? "" : "";
    }

    // まだ読み込んでいない付く先 (エンティティの一覧にあるもの) を集める
    private static HashSet<string> NewCargoTargets(IEnumerable<List<CargoEntry>> loadouts, Dictionary<string, string> names, HashSet<string> attempted)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(List<CargoEntry> entries)
        {
            foreach (var e in entries)
            {
                if (e.Target.Length > 0 && !attempted.Contains(e.Target) && names.ContainsKey(e.Target)) result.Add(e.Target);
                Walk(e.Children);
            }
        }
        foreach (var l in loadouts) Walk(l);
        return result;
    }

    // record 名 (接頭辞を除く、大文字小文字は元のまま) を、dcb query の filter "EntityClassDefinition.{名前1,名前2,…}" にまとめる
    // (1 回の filter は TypedFilterMaxChars 文字・TypedFilterMaxRecords 件まで。名前に glob の記号を含むものは 1 件ずつ。BuildTypedExtraFilters と同じ決まり)
    internal static List<string> BuildRecordNameFilters(IEnumerable<string> shortNames)
    {
        const string head = "EntityClassDefinition.";
        var names = shortNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var result = new List<string>();
        var batch = new List<string>();
        int batchChars = 0;
        void Flush()
        {
            if (batch.Count == 0) return;
            result.Add(batch.Count == 1 ? head + batch[0] : head + "{" + string.Join(",", batch) + "}");
            batch.Clear();
            batchChars = 0;
        }
        foreach (var n in names)
        {
            if (n.IndexOfAny(['{', '}', '[', ']', '*', '?', ',', '\\']) >= 0)
            {
                result.Add(head + n);
                continue;
            }
            if (batch.Count >= TypedFilterMaxRecords || batchChars + n.Length + 1 > TypedFilterMaxChars) Flush();
            batch.Add(n);
            batchChars += n.Length + 1;
        }
        Flush();
        return result;
    }

    // 船 1 隻の積載量 (SCU)。exists はエンティティの一覧にある名前か (無い名前の付く先はたどらない)
    internal static double ComputeCargoScu(string shipShortName, IReadOnlyDictionary<string, List<CargoEntry>> loadouts,
        IReadOnlyDictionary<string, List<(string Type, double Scu)>> containers, Func<string, bool> exists)
    {
        double total = 0;
        void Visit(string entity, int depth)
        {
            if (depth > CargoMaxDepth) return;
            if (depth > 0 && containers.TryGetValue(entity, out var list))
                foreach (var (type, scu) in list)
                    if (type == CargoOpenContainerType) total += scu;
            if (loadouts.TryGetValue(entity, out var entries)) Walk(entries, depth);
        }
        void Walk(List<CargoEntry> entries, int depth)
        {
            foreach (var e in entries)
            {
                if (e.Target.Length > 0 && (loadouts.ContainsKey(e.Target) || exists(e.Target))) Visit(e.Target, depth + 1);
                Walk(e.Children, depth);
            }
        }
        Visit(shipShortName, 0);
        return total;
    }

    // 構築の間だけ使う、Data.p4k から取り出した Game2.dcb (null なら dcb query は --p4k で読む)
    private string? _dcbPath;
    private int _dcbScopeDepth;
    // 構築の後の段階で同じ問い合わせ (種類, フィルタ) をし直すもの。最初の問い合わせの出力を dcb_tmp/cache に置き、2 回目はそれを読む。
    // starbreaker の出力は同じ問い合わせなら毎回同じ (実測でバイト単位で一致) なので、結果は問い合わせ直したときと同じ。
    // Game2.dcb を取り出せていて、空きが十分なときだけ使う
    private readonly HashSet<(string Type, string Filter)> _reuseQueries = new();
    private string? _queryCacheDir;
    private int _queryCacheSeq;
    private readonly Dictionary<(string Type, string Filter), string> _queryCacheFiles = new();
    private readonly object _queryCacheLock = new();

    private string DcbTmpDir => Path.Combine(Path.GetDirectoryName(DbPath) ?? ".", "dcb_tmp" + _tmpSuffix);

    // Data.p4k から Game2.dcb を作業ディレクトリの dcb_tmp に 1 回だけ取り出し、返したものを破棄するまで dcb query をそこから読ませる。
    // starbreaker は --p4k だと呼ぶたびに p4k から dcb を開き直す (1 回あたり約 3.6 秒。--dcb なら約 0.4 秒。出力は同じ。実測)。
    // 入れ子で呼ばれたら (装備データの再抽出の中の評判報酬の取り込み等) 外側の取り出しをそのまま使う。
    // 取り出せなかったら知らせて、今までどおり --p4k で続ける
    private async Task<IDisposable> UseExtractedDcbAsync(string p4kPath, CancellationToken ct)
    {
        var scope = new DcbScope(this);
        if (_dcbScopeDepth++ > 0) return scope;
        var dir = DcbTmpDir;
        try
        {
            var sw = Stopwatch.StartNew();
            StatusChanged?.Invoke("Data.p4k から Game2.dcb を取り出し中...");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            await RunStarBreakerAsync($"p4k extract --p4k \"{p4kPath}\" -o \"{dir}\" --filter \"Data/Game2.dcb\"", ct);
            var path = Path.Combine(dir, "Data", "Game2.dcb");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                _dcbPath = path;
                StatusChanged?.Invoke($"Game2.dcb を取り出しました ({new FileInfo(path).Length / 1024 / 1024}MB, {sw.Elapsed.TotalSeconds:F1}秒)");
                // 問い合わせの出力の使い回しは、空きが 4GB 以上あるときだけ (置くのは 1GB 前後の見込み。推定)
                try
                {
                    if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace >= 4L * 1024 * 1024 * 1024)
                    {
                        _queryCacheDir = Path.Combine(dir, "cache");
                        Directory.CreateDirectory(_queryCacheDir);
                    }
                }
                catch { _queryCacheDir = null; }
            }
            else
                StatusChanged?.Invoke("Game2.dcb を取り出せませんでした (Data.p4k から直接読みます)");
        }
        catch (OperationCanceledException) { scope.Dispose(); throw; }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Game2.dcb を取り出せませんでした (Data.p4k から直接読みます): {ex.Message}");
        }
        return scope;
    }

    private void EndDcbScope()
    {
        if (--_dcbScopeDepth > 0) return;
        _dcbScopeDepth = 0;
        _dcbPath = null;
        lock (_queryCacheLock)
        {
            _queryCacheDir = null;
            _queryCacheFiles.Clear();
            _reuseQueries.Clear();
        }
        try
        {
            if (Directory.Exists(DcbTmpDir)) Directory.Delete(DcbTmpDir, true);
        }
        catch { }   // 消せなくても次の取り出しの前に消す
    }

    private sealed class DcbScope(GameDataExtractor owner) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.EndDcbScope();
        }
    }

    // 後の段階で同じ問い合わせをし直すものを登録する (最初の問い合わせの出力を置いておき、2 回目はそれを読む)
    private void RegisterReuseQueries(IEnumerable<string> entityClassFilters)
    {
        lock (_queryCacheLock)
            foreach (var f in entityClassFilters) _reuseQueries.Add(("EntityClassDefinition", f));
    }

    // 置いておいた出力があれば読む (無ければ null)
    private string? TryReadCachedQuery(string recordType, string filter)
    {
        string? file;
        lock (_queryCacheLock)
        {
            if (_queryCacheDir == null || !_queryCacheFiles.TryGetValue((recordType, filter), out file)) return null;
        }
        try { return File.ReadAllText(file, new UTF8Encoding(false)); }
        catch { return null; }
    }

    // 登録された問い合わせなら、出力を置いておく (置けなくても構築は続ける)
    private void TryWriteCachedQuery(string recordType, string filter, string output)
    {
        string? file;
        lock (_queryCacheLock)
        {
            if (_queryCacheDir == null || !_reuseQueries.Contains((recordType, filter)) || _queryCacheFiles.ContainsKey((recordType, filter))) return;
            file = Path.Combine(_queryCacheDir, $"{++_queryCacheSeq}.json");
        }
        try
        {
            File.WriteAllText(file, output, new UTF8Encoding(false));
            lock (_queryCacheLock)
                if (_queryCacheDir != null) _queryCacheFiles[(recordType, filter)] = file;
        }
        catch { try { File.Delete(file); } catch { } }
    }

    // 問い合わせを先に (同時に maxParallel 個まで) 動かしておき、結果を元の順番で受け取る。
    // DB への書き込みは呼び出し側が今までどおり順番に行うので、入る順番 (rowid) は変わらない
    private sealed class DcbPrefetcher : IDisposable
    {
        private readonly Func<int, CancellationToken, Task<string>> _run;
        private readonly int _count;
        private readonly int _maxParallel;
        private readonly Task<string>?[] _tasks;
        private readonly CancellationTokenSource _cts;

        public DcbPrefetcher(int count, Func<int, CancellationToken, Task<string>> run, CancellationToken ct, int maxParallel = 4)
        {
            _count = count;
            _run = run;
            _maxParallel = maxParallel;
            _tasks = new Task<string>?[count];
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        // i 番目の結果を待つ。待つ前に i から maxParallel 個先までを動かし始める
        public Task<string> GetAsync(int i)
        {
            for (int k = i; k < Math.Min(_count, i + _maxParallel); k++)
                _tasks[k] ??= _run(k, _cts.Token);
            var t = _tasks[i]!;
            _tasks[i] = null;   // 受け取った出力を早く手放す
            return t;
        }

        public void Dispose()
        {
            // 途中で抜けたら (取り消し・例外)、先に動かしていた分を止め、例外を拾っておく
            _cts.Cancel();
            foreach (var t in _tasks)
                t?.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
            _cts.Dispose();
        }
    }

    // starbreaker を引数 args で動かし、終わるまで待つ (標準出力は読み捨てる)。
    // 終了コードが 0 でなければ、標準エラーの末尾を添えて例外にする。取り消されたら starbreaker を止めてから取り消しの例外を上げる
    private async Task RunStarBreakerAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = StarBreakerExe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await WaitStarBreakerExitAsync(proc, ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"starbreaker が失敗しました (終了コード {proc.ExitCode}): {StderrTail(stderr)}");
    }

    // starbreaker の終わりを待つ。取り消されたらプロセス (子プロセスも) を止め、終わるのを待ってから (最大 5 秒。書きかけのファイルを
    // 握ったままだと、呼ぶ側の後始末で作業フォルダを消せないため) 取り消しの例外を上げる
    private static async Task WaitStarBreakerExitAsync(Process proc, CancellationToken ct)
    {
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch { }
            throw;
        }
    }

    // 標準エラーの末尾 (長すぎる場合は最後の 500 文字)
    private static string StderrTail(StringBuilder stderr)
    {
        string text;
        lock (stderr) text = stderr.ToString().Trim();
        if (text.Length == 0) return "(標準エラーの出力なし)";
        return text.Length > 500 ? "…" + text[^500..] : text;
    }

    public void Dispose()
    {
        _db?.Dispose();
        _db = null;
    }

    // 既存 DB の item_index に size 列が無ければ追加する (CREATE TABLE IF NOT EXISTS では既存テーブルに列が足されないため)
    private void MigrateItemIndexSizeColumn()
    {
        if (_db == null) return;
        try
        {
            using var chk = _db.CreateCommand();
            chk.CommandText = "PRAGMA table_info(item_index)";
            using var reader = chk.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "size", StringComparison.OrdinalIgnoreCase)) return;
            }
            reader.Close();
            using var alt = _db.CreateCommand();
            alt.CommandText = "ALTER TABLE item_index ADD COLUMN size INTEGER DEFAULT 0";
            alt.ExecuteNonQuery();
        }
        catch { }
    }

    // 既存 DB の item_index に name_key 列 (名前のローカライズキー。"@" を除いたもの、例 item_Namevolt_smg_energy_01) が無ければ追加する。
    // 足しただけの列は NULL のままで、次のインデックス構築・装備データの再抽出 (ExtractItemIndexAsync) で埋まる
    private void MigrateItemIndexNameKeyColumn()
    {
        if (_db == null) return;
        try
        {
            using var chk = _db.CreateCommand();
            chk.CommandText = "PRAGMA table_info(item_index)";
            using var reader = chk.ExecuteReader();
            bool hasTable = false;
            while (reader.Read())
            {
                hasTable = true;
                if (string.Equals(reader.GetString(1), "name_key", StringComparison.OrdinalIgnoreCase)) return;
            }
            reader.Close();
            if (!hasTable) return;
            // 列を足すのと同時に (同じトランザクションで)、チャットのベクトル検索の埋め込み (item_vectors) の行をすべて消す。
            // name_key 列が無かった版の name_ja (英語名の一致で取り違えた訳を含む) で作った埋め込みが残らないよう、
            // 次のベクトル検索の構築 (BuildVectorIndexAsync は item_vectors に無い uuid の分だけ作る) で全件を作り直させるため。
            // 列を足したときだけ行うので、作り直しは一度だけ
            using var tx = _db.BeginTransaction();
            using var alt = _db.CreateCommand();
            alt.Transaction = tx;
            alt.CommandText = "ALTER TABLE item_index ADD COLUMN name_key TEXT";
            alt.ExecuteNonQuery();
            alt.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='item_vectors'";
            if ((long)(alt.ExecuteScalar() ?? 0L) > 0)
            {
                alt.CommandText = "DELETE FROM item_vectors";
                alt.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    // 既存 DB の ships に name_en 列 (英語 ini で解決した船名) が無ければ追加する
    private void MigrateShipsNameEnColumn()
    {
        if (_db == null) return;
        try
        {
            using var chk = _db.CreateCommand();
            chk.CommandText = "PRAGMA table_info(ships)";
            using var reader = chk.ExecuteReader();
            bool hasTable = false;
            while (reader.Read())
            {
                hasTable = true;
                if (string.Equals(reader.GetString(1), "name_en", StringComparison.OrdinalIgnoreCase)) return;
            }
            reader.Close();
            if (!hasTable) return;
            using var alt = _db.CreateCommand();
            alt.CommandText = "ALTER TABLE ships ADD COLUMN name_en TEXT";
            alt.ExecuteNonQuery();
        }
        catch { }
    }

    // 既存 DB の ships に cargo_scu 列 (ゲームデータから計算した船の積載量 SCU。UpdateShipCargoAsync が埋める) が無ければ追加する。
    // 足したばかりの列は NULL (まだ計算していない)。0 は「計算したが貨物の置き場が見つからなかった」
    private void MigrateShipsCargoScuColumn()
    {
        if (_db == null) return;
        try
        {
            using var chk = _db.CreateCommand();
            chk.CommandText = "PRAGMA table_info(ships)";
            using var reader = chk.ExecuteReader();
            bool hasTable = false;
            while (reader.Read())
            {
                hasTable = true;
                if (string.Equals(reader.GetString(1), "cargo_scu", StringComparison.OrdinalIgnoreCase)) return;
            }
            reader.Close();
            if (!hasTable) return;
            using var alt = _db.CreateCommand();
            alt.CommandText = "ALTER TABLE ships ADD COLUMN cargo_scu REAL";
            alt.ExecuteNonQuery();
        }
        catch { }
    }

    // ships.name_en を埋める英語ローカライズ。<GamePath>\data\Localization\english\global.ini (ExtractMissionsAsync と同じ 2 か所) に無ければ、
    // [新しいパッチを読み取る] が作業ディレクトリに作る english\global.ini を使う。どれも無ければ null。
    // ※ ミッションの title 等の解決 (ExtractMissionsAsync の辞書) はこれを使わない (IniMerger の印が title_key = キーであることに依存しているため)
    private Dictionary<string, string>? LoadShipNameLocalization()
    {
        var gamePath = App.Config.GamePath ?? "";
        var candidates = new[]
        {
            Path.Combine(gamePath, "data", "Localization", "english", "global.ini"),
            Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "data", "Localization", "english", "global.ini"),
            Path.Combine(Path.GetDirectoryName(DbPath) ?? "", "english", "global.ini"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path == null) return null;
        return new Dictionary<string, string>(GlobalIniParser.Parse(path), StringComparer.OrdinalIgnoreCase);
    }

    // ships 全行の name_en を英語ローカライズで埋める。戻り値は (解決できた行数, 解決できなかった行数)
    private (int Resolved, int Unresolved) UpdateShipEnglishNames()
    {
        var loc = LoadShipNameLocalization();
        if (loc == null)
        {
            StatusChanged?.Invoke("船の英語名: 英語の global.ini が見つからないため name_en を埋められません");
            return (0, 0);
        }
        var result = FillShipEnglishNames(_db!, loc);
        StatusChanged?.Invoke($"船の英語名: {result.Resolved} 件解決 (未解決 {result.Unresolved} 件)");
        return result;
    }

    // ships.name ("@vehicle_NameDRAK_Ironclad" のようなキー) を英語ローカライズで解決して name_en に入れる。
    // "@" で始まらない name はそのまま英語名として入れる。解決できなければ NULL
    internal static (int Resolved, int Unresolved) FillShipEnglishNames(SqliteConnection db, Dictionary<string, string> loc)
    {
        var rows = new List<(string Record, string Name)>();
        using (var sel = db.CreateCommand())
        {
            sel.CommandText = "SELECT record_name, name FROM ships";
            using var r = sel.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1)));
        }

        int resolved = 0, unresolved = 0;
        using var tx = db.BeginTransaction();
        using var upd = db.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE ships SET name_en = @ne WHERE record_name = @rn";
        var pNe = upd.Parameters.Add("@ne", SqliteType.Text);
        var pRn = upd.Parameters.Add("@rn", SqliteType.Text);
        foreach (var (record, name) in rows)
        {
            var en = ResolveShipEnglishName(name, loc);
            if (en != null) resolved++; else unresolved++;
            pNe.Value = (object?)en ?? DBNull.Value;
            pRn.Value = record;
            upd.ExecuteNonQuery();
        }
        tx.Commit();
        return (resolved, unresolved);
    }

    internal static string? ResolveShipEnglishName(string name, Dictionary<string, string> loc)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (!name.StartsWith('@')) return name;
        return loc.TryGetValue(name[1..], out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    public string? GetCachedVersion()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT value FROM gamedata_meta WHERE key='game_version'";
        return cmd.ExecuteScalar() as string;
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO gamedata_meta(key,value) VALUES(@k,@v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private string? GetCache(string queryKey)
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT result_json FROM gamedata_cache WHERE query_key=@k";
        cmd.Parameters.AddWithValue("@k", queryKey);
        return cmd.ExecuteScalar() as string;
    }

    private void SetCache(string queryKey, string json)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO gamedata_cache(query_key,result_json,cached_at) VALUES(@k,@v,@t)";
        cmd.Parameters.AddWithValue("@k", queryKey);
        cmd.Parameters.AddWithValue("@v", json);
        cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public async Task EnsureStarBreakerAsync()
    {
        if (IsStarBreakerInstalled) return;

        StatusChanged?.Invoke("StarBreaker CLI をダウンロード中...");
        ProgressChanged?.Invoke(0, "ダウンロード中...");

        Directory.CreateDirectory(ToolsDir);
        var zipPath = Path.Combine(ToolsDir, "starbreaker-cli.zip");

        using (var response = await Http.GetAsync(StarBreakerUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var totalBytes = response.Content.Headers.ContentLength ?? 0;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);

            var buffer = new byte[81920];
            long downloaded = 0;
            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                downloaded += bytesRead;
                if (totalBytes > 0)
                {
                    var pct = (int)(downloaded * 100 / totalBytes);
                    ProgressChanged?.Invoke(pct, $"ダウンロード中... {downloaded / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB");
                }
            }
        }

        StatusChanged?.Invoke("展開中...");
        ZipFile.ExtractToDirectory(zipPath, ToolsDir, overwriteFiles: true);
        File.Delete(zipPath);

        ProgressChanged?.Invoke(100, "StarBreaker CLI 準備完了");
    }

    // requestShopPriceFetch: 構築が終わったら売り場・価格の裏の取得を始めてもらうか。設定タブの構築は、構築の後の処理
    // (日本語名・固有名詞キャッシュ) を済ませてから MainWindow が RequestShopPriceFetch を呼ぶので false にする
    public async Task BuildIndexAsync(string dataP4kPath, CancellationToken ct = default, bool requestShopPriceFetch = true)
    {
        await BuildStructuredIndexAsync(dataP4kPath, ct, requestShopPriceFetch);
    }

    // 売り場・価格の裏の取得を始めてもらう (MainWindow が受けて FetchNewShopPricesAsync を走らせる)
    public static void RequestShopPriceFetch(string dbPath) => ShopPriceFetchRequested?.Invoke(dbPath);

    // 構築の前に売り場・価格の裏の取得を止める (構築中・VACUUM 中に取得が DB に書かないように)。
    // 成功すれば最後の ShopPriceFetchRequested で、失敗すれば ShopPriceFetchResumeRequested で続きを裏で再開する
    // 売り場・価格の取得の印 (手動中止・途中) は構築では消さないが、失敗したときは構築前の控えから戻すのに加えて、先に退避した印を入れ直してから再開を頼む
    // (成功したときは、ShopPriceFetchRequested の受け手が手動中止の印を消して取得を始める)
    public async Task BuildStructuredIndexAsync(string dataP4kPath, CancellationToken ct = default, bool requestShopPriceFetch = true)
    {
        // アプリを閉じるとき (AppExit) も取り消す。取り消すと構築前の控えから戻してから終わる (閉じる処理はそれを待つ)
        using var exitWork = AppExit.BeginWork("インデックス構築");
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, AppExit.Token);
        ct = linkedCts.Token;
        // 構築中かどうか (IsIndexBuildRunning) は、取得を止める前から、再開を頼んだ後まで立てておく
        Interlocked.Increment(ref s_indexBuildsRunning);
        try
        {
            await PauseShopPriceFetchAsync();
            Dictionary<string, string>? savedMarks = null;
            try
            {
                EnsureDb();
                savedMarks = ReadShopPriceMarks(_db!);
                await BuildStructuredIndexCoreAsync(dataP4kPath, ct, requestShopPriceFetch);
            }
            catch
            {
                // 止めた取得 (とチャットの取り直し) は、どこで失敗しても必ず再開を頼む
                try { if (savedMarks != null && _db != null) RestoreShopPriceMarks(_db, savedMarks); } catch { }
                ShopPriceFetchResumeRequested?.Invoke(DbPath);
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref s_indexBuildsRunning);
        }
    }

    // BuildStructuredIndexAsync (設定タブの構築・チャットの自動構築) と RebuildEquipmentDataAsync (装備データの再抽出) が走っている数
    private static int s_indexBuildsRunning;

    // BuildStructuredIndexAsync・RebuildEquipmentDataAsync が走っている間 true (バックアップのインポートの終わりに、構築が止めた取得を再開しないために MainWindow が見る)
    internal static bool IsIndexBuildRunning => Volatile.Read(ref s_indexBuildsRunning) > 0;

    // VACUUM の直前の空きの確かめ。WAL では VACUUM の間に -wal が DB とほぼ同じ大きさまで大きくなる (804MB の写しで 808MB) ので、
    // 今の gamedata_cache.db の 1.1 倍 (VacuumWalFactor) の空きが要る。
    // 加えて VACUUM は作業用の一時 DB (DB 以下の大きさ。実測で DB の約 0.8 倍) を一時フォルダに作る (同梱の SQLite は TEMP_STORE=1 で、
    // temp_store を変えていないので一時ファイルはディスクに作る)。一時フォルダは、temp_store_directory を設定していないので Windows では
    // GetTempPath() が返すフォルダ (SQLite の「Temporary Files」の説明。SQLITE_TMPDIR などの環境変数は Windows では SQLite 自身は見ない)。
    // DB の 1.0 倍で見込み、一時フォルダが DB と同じドライブならその空きに足し、違うドライブならそのドライブの空きを別に確かめる。
    // 足りなければ知らせの文 (構築の前の確かめと同じ書き方)、足りれば null。空きを調べられなければ null (今までどおり VACUUM する)
    private const double VacuumWalFactor = 1.1;
    private static long VacuumWalBytes(long dbBytes) => (long)Math.Ceiling(dbBytes * VacuumWalFactor);

    // VACUUM の一時 DB を置くドライブ (Path.GetTempPath の根)。DB と同じドライブなら null。調べられなければ例外
    private static DriveInfo? VacuumTempDrive(DriveInfo dbDrive)
    {
        var tempDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!);
        return string.Equals(tempDrive.Name, dbDrive.Name, StringComparison.OrdinalIgnoreCase) ? null : tempDrive;
    }

    // 空きの知らせの文の一部: "<ドライブ> の空き ◯MB (◯MB以上必要。<内訳1> に ◯MB と <内訳2> に ◯MB ...)"。
    // 「以上必要」の数は、内訳それぞれを MB に切り捨てた数の合計 (内訳の和と合うように)。MB に切り捨てて 0 の内訳は文に出さない。
    // 空きが足りれば null (足りるかは切り捨てる前のバイトで比べる)
    private static long ToMB(long bytes) => bytes / 1024 / 1024;
    private static string? SpaceShortageText(DriveInfo drive, string prefix, params (string Label, long Bytes)[] parts)
    {
        long need = parts.Sum(x => x.Bytes);
        if (drive.AvailableFreeSpace >= need) return null;
        var shown = parts.Where(x => ToMB(x.Bytes) > 0).ToList();
        return $"{prefix}{drive.Name} の空き {ToMB(drive.AvailableFreeSpace)}MB ({parts.Sum(x => ToMB(x.Bytes))}MB以上必要"
            + (shown.Count > 0 ? "。" + string.Join(" と", shown.Select(x => $"{x.Label}に {ToMB(x.Bytes)}MB")) : "") + ")";
    }

    // 一時フォルダが違うドライブのとき、そのドライブの空きが tempBytes に足りなければ知らせの文の一部、足りれば null
    private static string? VacuumTempShortage(DriveInfo tempDrive, long tempBytes)
        => SpaceShortageText(tempDrive, $"一時フォルダ ({Path.GetTempPath()}) の ", ("最適化 (VACUUM) の一時 DB ", tempBytes));

    private string? VacuumSpaceShortage()
    {
        try
        {
            var dbDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DbPath))!);
            long dbBytes = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
            long walBytes = VacuumWalBytes(dbBytes);
            long tempBytes = dbBytes;
            // 一時フォルダのドライブを調べられなければ、一時 DB の分の確かめだけを飛ばす (DB のドライブの -wal の分は確かめる。構築の前の確かめと同じ扱い)
            bool tempKnown;
            DriveInfo? tempDrive = null;
            try { tempDrive = VacuumTempDrive(dbDrive); tempKnown = true; }
            catch { tempKnown = false; }
            var dbParts = new List<(string, long)> { ("最適化 (VACUUM) の -wal ", walBytes) };
            if (tempKnown && tempDrive == null) dbParts.Add(("一時 DB ", tempBytes));
            var dbShort = SpaceShortageText(dbDrive, "", dbParts.ToArray());
            string? tempShort = null;
            if (tempDrive != null)
            {
                try { tempShort = VacuumTempShortage(tempDrive, tempBytes); }
                catch { tempShort = null; }   // 一時フォルダのドライブの空きを調べられなければ、その確かめだけを飛ばす
            }
            if (dbShort == null && tempShort == null) return null;
            return "ディスク容量不足のため最適化 (VACUUM) を飛ばしました: " + string.Join("、", new[] { dbShort, tempShort }.Where(x => x != null));
        }
        catch { return null; }
    }

    private async Task BuildStructuredIndexCoreAsync(string dataP4kPath, CancellationToken ct, bool requestShopPriceFetch)
    {
        // 構築の作業に 500MB、加えて構築前の控え (BuildIndexAsync が今の gamedata_cache.db を丸ごと控える) の分と、
        // 最後の最適化 (VACUUM) の -wal の分 (WAL では VACUUM の間に -wal が DB とほぼ同じ大きさになる。今の gamedata_cache.db の 1.1 倍で見込む) と、
        // VACUUM の一時 DB の分 (一時フォルダに作る。DB の 1.0 倍で見込む。VacuumSpaceShortage の説明) が要る
        // (一時フォルダが DB と同じドライブなら合わせて DB の 3.1 倍 + 500MB。違うドライブなら DB のドライブに 2.1 倍 + 500MB、一時フォルダのドライブに 1.0 倍)
        var dbDrive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(DbPath))!);
        long backupBytes = File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0;
        long walBytes = VacuumWalBytes(backupBytes);
        long tempBytes = backupBytes;
        // 一時フォルダのドライブを調べられなければ、一時 DB の分の確かめだけを飛ばす (構築は止めない。VACUUM の直前の確かめと同じ扱い)
        bool tempKnown;
        DriveInfo? tempDrive = null;
        try { tempDrive = VacuumTempDrive(dbDrive); tempKnown = true; }
        catch { tempKnown = false; }
        var dbParts = new List<(string, long)> { ("構築の作業", 500L * 1024 * 1024), ("構築前の控え", backupBytes), ("最適化 (VACUUM) の -wal ", walBytes) };
        if (tempKnown && tempDrive == null) dbParts.Add(("一時 DB ", tempBytes));
        var dbShort = SpaceShortageText(dbDrive, "", dbParts.ToArray());
        string? tempShort = null;
        if (tempDrive != null)
        {
            try { tempShort = VacuumTempShortage(tempDrive, tempBytes); }
            catch { tempShort = null; }   // 一時フォルダのドライブの空きを調べられなければ、その確かめだけを飛ばす
        }
        // DB のドライブと一時フォルダのドライブが両方足りなければ、両方を「、」でつないで 1 回で知らせる
        if (dbShort != null || tempShort != null)
            throw new IOException("ディスク容量不足: " + string.Join("、", new[] { dbShort, tempShort }.Where(x => x != null)));

        await EnsureStarBreakerAsync();
        EnsureDb();
        using var dcbScope = await UseExtractedDcbAsync(dataP4kPath, ct);

        // 構築は最初に表を消してから段階ごとに入れ直す。各段階が自分のトランザクションを使い、最後に VACUUM (トランザクションの中では
        // 実行できない) をするので、全体を 1 つのトランザクションでは包めない。消す前に DB を丸ごと控え (VACUUM INTO)、
        // 途中で失敗・中止したら控えから元に戻す (名前だけ並んで詳細が空、のまま残さない)。控えを作れなければ何も消さずに止める
        // 前回の構築の途中の印が残っていれば始めない (InterruptedWork。起動のときに戻して消すので、ふつうは残らない)
        var workDir = Path.GetDirectoryName(Path.GetFullPath(DbPath)) ?? ".";
        InterruptedWork.ThrowIfPending(workDir, InterruptedWork.KindBuild);
        var preBuildBackup = CreatePreBuildBackup();
        // 控えを作っている間に取り消された (アプリを閉じる等) ときは、まだ何も消していないので戻さずに止める
        if (ct.IsCancellationRequested)
        {
            DeletePreBuildBackup(preBuildBackup);
            ct.ThrowIfCancellationRequested();
        }
        // 途中の印 (InterruptedWork)。書き換え始める前に書き、成功したとき・控えから戻し終えたときに消す
        // (アプリが途中で終わったら、次の起動で印を見て控えから戻す)。書けなければ何も消さずに止める
        string markerPath;
        try
        {
            markerPath = InterruptedWork.Write(workDir, new InterruptedWork.Marker
            {
                Kind = InterruptedWork.KindBuild, Label = "インデックス構築",
                Backups = new Dictionary<string, string> { [DbPath] = preBuildBackup },
            });
        }
        catch (Exception ex)
        {
            DeletePreBuildBackup(preBuildBackup);
            throw new IOException($"途中の印を書けなかったため、インデックス構築を中止しました (データは消していません): {ex.Message}", ex);
        }
        try
        {
            await BuildStructuredIndexStepsAsync(dataP4kPath, ct, requestShopPriceFetch);
        }
        catch (Exception ex)
        {
            // 戻せなかったら控えを .failed-<日時> にして残し (次の構築で上書きされないように)、場所を知らせる。どちらでも印は消す
            // (起動のときに黙って戻すことはしない)
            if (!RestorePreBuildBackup(preBuildBackup, ex))
            {
                var kept = InterruptedWork.KeepFailedBackup(preBuildBackup, markerPath, InterruptedWork.KindBuild, s => StatusChanged?.Invoke(s));
                StatusChanged?.Invoke($"構築前の控えは {kept} に残しています (gamedata_cache.db を構築前に戻すには、アプリを閉じてこのファイルで置き換えてください)");
            }
            InterruptedWork.Clear(markerPath, InterruptedWork.KindBuild, s => StatusChanged?.Invoke(s));
            throw;
        }
        // 成功: 印を先に消してから控えを消す (控えを消し損ねても、印が無いので次の起動で戻さない)
        InterruptedWork.Clear(markerPath, InterruptedWork.KindBuild, s => StatusChanged?.Invoke(s));
        DeletePreBuildBackup(preBuildBackup);
    }

    // インデックス構築の前の控えの置き場所 (gamedata_cache.db と同じフォルダ)
    private string PreBuildBackupPath => DbPath + ".prebuild.bak";

    private string CreatePreBuildBackup()
    {
        var path = PreBuildBackupPath;
        StatusChanged?.Invoke("構築前のデータを控えています...");
        try
        {
            // 前回の控えが残っていたら消す (VACUUM INTO は既にあるファイルへは書けない)
            if (File.Exists(path)) File.Delete(path);
            using var cmd = _db!.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = "VACUUM INTO @p";
            cmd.Parameters.AddWithValue("@p", path);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw new IOException($"構築前のデータを控えられなかったため、インデックス構築を中止しました (データは消していません): {ex.Message}", ex);
        }
        return path;
    }

    // 控えから構築前の状態に戻す (同じ接続へ SQLite のバックアップで書き戻すので、他で開いている接続があっても戻せる)。
    // 戻せなかったら控えは消さずに残し、場所を知らせる。戻せたら true
    private bool RestorePreBuildBackup(string backupPath, Exception cause)
    {
        StatusChanged?.Invoke($"インデックス構築が途中で終わったため ({cause.Message})、構築前のデータへ戻しています...");
        try
        {
            EnsureDb();
            using (var bak = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False"))
            {
                bak.Open();
                bak.BackupDatabase(_db!);
            }
            // バックアップ API で戻した中身はいったん -wal に入る (DB と同じくらいの大きさ) ので、本体に書き戻して -wal を空にする
            SqliteWal.CheckpointOrRetryLater(_db!);
            StatusChanged?.Invoke("構築前のデータへ戻しました");
            DeletePreBuildBackup(backupPath);
            return true;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"構築前のデータへ戻せませんでした: {ex.Message}");
            return false;
        }
    }

    private void DeletePreBuildBackup(string backupPath)
    {
        try { if (File.Exists(backupPath)) File.Delete(backupPath); }
        catch (Exception ex) { StatusChanged?.Invoke($"構築前の控え {backupPath} を消せませんでした: {ex.Message}"); }
    }

    private async Task BuildStructuredIndexStepsAsync(string dataP4kPath, CancellationToken ct, bool requestShopPriceFetch)
    {
        StatusChanged?.Invoke("ゲームデータの構造化インデックスを構築中...");
        var sw = Stopwatch.StartNew();
        int step = 0;
        int totalSteps = 7;
        var now = DateTime.UtcNow.ToString("o");

        // wiki から取って missions に入れた分 (wiki_* 列) は、構築が missions を作り直すと消えるので、消す前に控え、
        // ミッションを抽出した後に同じ record_name の行へ戻す (構築後に無くなったミッションの分は捨てる)
        SaveWikiColumns(_db!);

        // クリア
        using (var cmd = _db!.CreateCommand())
        {
            // gamedata_meta は、この構築が作り直すデータについての記録 (RebuiltDataMetaKeys) だけ消す。作り直さないデータ
            // (ベクトル検索の埋め込み・派閥と貢献度) の記録と、この PC だけの印 (LocalOnlyMetaKeys) は残す
            cmd.CommandText = "DELETE FROM item_index; DELETE FROM ship_ports; DELETE FROM ships; DELETE FROM items; DELETE FROM missions; DELETE FROM commodities; DELETE FROM gamedata_cache; "
                + $"DELETE FROM gamedata_meta WHERE key IN ({string.Join(", ", RebuiltDataMetaKeys.Select(k => $"'{k}'"))});";
            cmd.ExecuteNonQuery();
        }

        // Step 1: アイテムインデックス（全メーカー＋船）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] アイテムインデックスを構築中...");
        // 全エンティティの種類 (AttachDef.Type) の一覧。決め打ちの接頭辞で漏れる武器 (新しい製造元など) を種類で拾うために使う
        var typeMap = await QueryEntityTypesAsync(dataP4kPath, ct);
        var componentQueries = new[]
        {
            ("SHLD_*", "SCItemShieldGeneratorParams"),
            ("POWR_*", "SCItemPowerPlantParams"),
            ("QDRV_*", "SCItemQuantumDriveParams"),
            ("COOL_*", "SCItemCoolerParams"),
        };
        // 船・車両 (Step 2) と装備アイテム (Step 3) は、アイテムインデックスと同じ問い合わせ (種類・フィルタが全く同じもの) をし直すので、
        // 最初の出力を置いておいて使い回す (starbreaker の出力は同じ問い合わせなら毎回同じ)
        RegisterReuseQueries(VehicleManufacturerPrefixes.Select(p => $"EntityClassDefinition.{p}_*")
            .Concat(componentQueries.Select(q => $"EntityClassDefinition.{q.Item1}"))
            .Concat(ItemPrefixes.Where(p => p.Length <= 4).Select(p => $"EntityClassDefinition.{p}_*"))
            .Concat(BuildTypedExtraFilters(typeMap, ItemPrefixes.Where(p => p.Length <= 4)).Select(x => x.Filter)));
        int indexCount = await ExtractItemIndexAsync(dataP4kPath, now, ct, typeMap);

        // Step 1b: FTS5 全文検索インデックス
        StatusChanged?.Invoke($"FTS5 全文検索インデックスを構築中...");
        RebuildFts5Index();

        // Step 2: 船・車両（詳細データ + ハードポイント）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 船・車両データを抽出中...");
        int shipCount = await ExtractShipsAsync(dataP4kPath, now, ct);
        UpdateShipEnglishNames();
        await UpdateShipPortSizesAsync(dataP4kPath, ct);
        await UpdateShipCargoAsync(dataP4kPath, ct);

        // Step 3: アイテム詳細（装備コンポーネント）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 装備アイテムを抽出中...");
        int itemCount = 0;
        foreach (var (filter, compType) in componentQueries)
        {
            StatusChanged?.Invoke($"[{step}/{totalSteps}] 装備抽出: {filter}");
            itemCount += await ExtractItemsAsync(dataP4kPath, $"EntityClassDefinition.{filter}", compType, now, ct);
        }
        foreach (var prefix in ItemPrefixes.Where(p => p.Length <= 4))
        {
            var filter = $"EntityClassDefinition.{prefix}_*";
            StatusChanged?.Invoke($"[{step}/{totalSteps}] 武器抽出: {prefix}");
            itemCount += await ExtractItemsAsync(dataP4kPath, filter, "SCItemWeaponComponentParams", now, ct);
        }
        // 上の接頭辞で取れない武器類 (新しい製造元・大文字の製造元コードの船の武器など) を、種類の一覧から拾って取り込む
        foreach (var (filter, records) in BuildTypedExtraFilters(typeMap, ItemPrefixes.Where(p => p.Length <= 4)))
        {
            StatusChanged?.Invoke($"[{step}/{totalSteps}] 武器抽出 (種類で追加): {filter}");
            itemCount += await ExtractItemsAsync(dataP4kPath, filter, "SCItemWeaponComponentParams", now, ct, records);
        }
        // 弾のデータ (AmmoParams): 武器の弾速・1発のダメージに使う
        StatusChanged?.Invoke($"[{step}/{totalSteps}] 弾のデータ (AmmoParams) を抽出中...");
        int ammoCount = await ExtractAmmoParamsAsync(dataP4kPath, now, ct);
        StatusChanged?.Invoke($"[{step}/{totalSteps}] 弾のデータ: {ammoCount} 件");
        // 何発で倒せるか (TTK) に使う、プレイヤーの体力・部位の倍率・アーマーの倍率
        var damageParams = await ExtractPlayerDamageParamsAsync(dataP4kPath, ct);
        StatusChanged?.Invoke($"[{step}/{totalSteps}] 体力・部位・アーマーの倍率: {damageParams} 件" + (damageParams == 0 ? " (取り込めなかったので前のデータを残します)" : ""));
        RebuildItemVariantsAndReport($"[{step}/{totalSteps}] ");

        // Step 4: ミッション・契約
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 契約を抽出中...");
        int missionCount = await ExtractMissionsAsync(dataP4kPath, now, ct);
        var wikiRestored = RestoreWikiColumns(_db!);
        StatusChanged?.Invoke($"[{step}/{totalSteps}] wiki の契約情報を {wikiRestored} 件引き継ぎました");

        // Step 5: コモディティ
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] コモディティを抽出中...");
        int commodityCount = await ExtractCommoditiesAsync(dataP4kPath, now, ct);
        // 資源の種類 (ResourceType。［ゲームログ］の商品売買の資源名に使う)。失敗しても構築は続ける (resource_types は前のまま。無い・空なら起動後に裏で取り出す)
        int resourceCount = 0;
        try { resourceCount = await ExtractResourceTypesAsync(dataP4kPath, now, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { StatusChanged?.Invoke($"資源の種類 (ResourceType) の取り出しに失敗しました: {ex.Message}"); }
        // 星図の場所の名前 (StarMapObject。［ゲームログ］の場所名に使う)。失敗しても構築は続ける (starmap_names は前のまま。無い・空なら起動後に裏で取り出す)
        int starmapCount = 0;
        try { starmapCount = await ExtractStarmapNamesAsync(dataP4kPath, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { StatusChanged?.Invoke($"星図の場所の名前 (StarMapObject) の取り出しに失敗しました: {ex.Message}"); }

        // Step 6: BP (ブループリント。［BP］タブ)。失敗しても構築は続ける (3 表は前のまま。無い・空なら起動後に裏で取り出す: RebuildBlueprintsAsync)
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] BP (ブループリント) を抽出中...");
        int blueprintCount = 0;
        try { blueprintCount = await ExtractBlueprintsAsync(dataP4kPath, now, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { StatusChanged?.Invoke($"BP (ブループリント) の取り出しに失敗しました: {ex.Message}"); }
        // Step 6b: 決め打ちの接頭辞・種類では取れない品目 (採掘レーザー・採掘モジュール・鉱石ポッド・BP や契約から参照される品目など。ExtractExtraItemsAsync)。
        // BP の表と契約を使うので BP の後、船の口 (ship_ports) の判定を変えないよう船の後に行う。失敗しても構築は続ける (起動後に裏で取り直す)
        int extraCount = 0;
        try { extraCount = await ExtractExtraItemsAsync(dataP4kPath, now, ct, typeMap); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { StatusChanged?.Invoke($"[品目の追加] 取り込みに失敗しました (起動後に裏で取り直します): {ex.Message}"); }
        // Step 6c: Wikelo の契約の納品物のゲームデータ上の入手場所 (契約タブの入手場所の欄。ExtractDeliverableSourcesAsync)。対象を契約 (Step 4) の行から決めるので契約の後に行う。
        // 失敗しても構築は続ける (deliverable_sources は前のまま)。各段の時間は StatusChanged に出る
        try { await ExtractDeliverableSourcesAsync(dataP4kPath, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { StatusChanged?.Invoke($"[納品物の入手場所] 取り込みに失敗しました (前のデータを残します): {ex.Message}"); }

        // Step 7: メタ情報
        step++;
        var p4kModified = File.GetLastWriteTimeUtc(dataP4kPath);
        SetMeta("game_version", p4kModified.ToString("yyyy-MM-dd HH:mm"));
        SetMeta("p4k_last_modified", p4kModified.ToString("o"));
        SetMeta("indexed_at", now);
        SetMeta("p4k_path", dataP4kPath);
        // ゲームの版 (Data.p4k と同じフォルダの build_manifest.id)。パッチ更新ありの判定 (IsP4kUpdated) で日時より優先して比べる
        var (buildVersion, buildChangeNum) = ReadBuildManifest(dataP4kPath);
        if (!string.IsNullOrEmpty(buildVersion)) SetMeta(BuildVersionMetaKey, buildVersion);
        if (!string.IsNullOrEmpty(buildChangeNum)) SetMeta(BuildChangeNumMetaKey, buildChangeNum);

        // 中身を消して入れ直したページが空きとして残り、ファイルが大きいままになるので VACUUM で詰める。
        // 売り場・価格の裏の取得 (ShopPriceFetchRequested) はこの後に始まる。失敗しても構築の結果は使えるので、知らせて続ける
        ClearCstoneNotFoundMarks();
        // 構築の途中で空きが減ったときのため、VACUUM の直前にも -wal の分 (DB の 1.1 倍) の空きを確かめる。足りなければ VACUUM だけを飛ばす (構築の結果は使える)
        var vacuumShortage = VacuumSpaceShortage();
        if (vacuumShortage != null) StatusChanged?.Invoke(vacuumShortage);
        else
        {
            StatusChanged?.Invoke("DB を最適化中 (VACUUM)...");
            var vacuumSw = Stopwatch.StartNew();
            try
            {
                using var vac = _db!.CreateCommand();
                vac.CommandText = "VACUUM";
                vac.ExecuteNonQuery();
                // WAL では VACUUM の中身がいったん -wal に入る (DB と同じくらいの大きさ) ので、本体に書き戻して -wal を空にする
                // (ほかの接続が読んでいて途中で止まったら、少し後に試し直す)
                SqliteWal.CheckpointOrRetryLater(_db!);
                StatusChanged?.Invoke($"DB の最適化完了 ({vacuumSw.Elapsed.TotalSeconds:F1}秒)");
            }
            catch (Exception ex) { StatusChanged?.Invoke($"DB の最適化 (VACUUM) に失敗しました: {ex.Message}"); }
        }

        sw.Stop();
        ProgressChanged?.Invoke(100, $"完了！ ({sw.Elapsed.TotalSeconds:F1}秒)");
        StatusChanged?.Invoke($"構造化インデックス完了 ({sw.Elapsed.TotalSeconds:F1}秒) - インデックス: {indexCount}, 船: {shipCount}, 装備: {itemCount}, 弾: {ammoCount}, 契約: {missionCount}, 商品: {commodityCount}, 資源: {resourceCount}, 場所名: {starmapCount}, BP: {blueprintCount}, 追加の品目: {extraCount}");

        // 新しい品目の売り場・価格は、構築とは別に裏で取得する (MainWindow が受けて FetchNewShopPricesAsync を走らせる)
        if (requestShopPriceFetch) ShopPriceFetchRequested?.Invoke(DbPath);
    }

    // インデックス構築・装備データの再抽出が成功した後に、「cstone に品目無し」の記録を消す (新しいパッチで cstone に載ったかもしれないので、
    // この後の裏の取得で取り直す)。消せなくても構築の結果は使えるので、知らせて続ける
    private void ClearCstoneNotFoundMarks()
    {
        try
        {
            using (var init = _db!.CreateCommand())
            {
                init.CommandText = ShopPriceStore.TableDdl;
                init.ExecuteNonQuery();
            }
            var n = ShopPriceStore.DeleteCstoneNotFound(_db);
            if (n > 0) StatusChanged?.Invoke($"[売り場・価格] cstone に品目無しの記録 {n} 件を消しました (この後の取得で取り直します)");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"[売り場・価格] cstone に品目無しの記録を消せませんでした: {ex.Message}"); }
    }

    // インデックス構築・装備データの再抽出が終わり、新しい品目の売り場・価格を裏で取りに行ってほしいときに起きる (引数は gamedata_cache.db の場所)
    public static event Action<string>? ShopPriceFetchRequested;

    // インデックス構築・装備データの再抽出を始める前に、売り場・価格の裏の取得を止めてほしいときに起きる (止まるまで待つ。MainWindow が受ける)
    public static event Func<Task>? ShopPriceFetchPauseRequested;

    // 構築・再抽出が失敗したとき、止めた取得の続きを裏で再開してほしいときに起きる (引数は gamedata_cache.db の場所)
    public static event Action<string>? ShopPriceFetchResumeRequested;

    // (バックアップのインポートの前にも MainWindow から呼ぶ)
    internal static async Task PauseShopPriceFetchAsync()
    {
        if (ShopPriceFetchPauseRequested is not { } handlers) return;
        foreach (var h in handlers.GetInvocationList().Cast<Func<Task>>())
        {
            try { await h(); }
            catch (Exception ex) { ChatService.LogExternal($"[売り場・価格] 構築前の取得の停止に失敗: {ex.Message}"); }
        }
    }

    // 弾のデータ (AmmoParams) の表。record_name は "AmmoParams.xxx"。damage_json は直撃の DamageInfo、explosion_json は爆発の DamageInfo
    // (種類 → 値の JSON。爆発しない弾は NULL)
    internal const string AmmoParamsDdl = """

        CREATE TABLE IF NOT EXISTS ammo_params (
            record_name TEXT PRIMARY KEY,
            speed REAL,
            lifetime REAL,
            damage_json TEXT,
            explosion_json TEXT,
            raw_json TEXT NOT NULL,
            extracted_at TEXT NOT NULL
        );
        """;

    // 何発で倒せるか (TTK) に使う値を dcb query で取り、player_damage_params を作り直す (足りなければ前のデータを残して 0 を返す)。
    //   体力: ActorStatusComponent.PlayerStatusPU の ActorStatData (statType = HealthPool) の maximumStatValue
    //   ダウン後に死亡するまでのダメージ: 同じ record の downedParams.damageToInstaKillFromDowned (excessDamageToInstaKill も注記用に残す)
    //   部位の倍率: BodyHealthConfig.HealthConfig_Player の bodyPartConfigs の damageMultiplier (bodyParts のファイル名で head / torso / arm / leg)
    //   アーマーの倍率: DamageResistanceMacro の各 record の damageResistance.{種類}Resistance.Multiplier
    private async Task<int> ExtractPlayerDamageParamsAsync(string p4kPath, CancellationToken ct)
    {
        try
        {
            var status = await RunDcbQueryWithTimerAsync(p4kPath, "ActorStatusComponent", "*PlayerStatusPU", "体力 (PlayerStatusPU)", ct);
            var body = await RunDcbQueryWithTimerAsync(p4kPath, "BodyHealthConfig", "*HealthConfig_Player", "部位の倍率 (HealthConfig_Player)", ct);
            var macros = await RunDcbQueryWithTimerAsync(p4kPath, "DamageResistanceMacro", "*", "アーマーの倍率 (DamageResistanceMacro)", ct);
            var rows = ParsePlayerDamageParams(status, body, macros);
            if (rows.Count == 0) return 0;
            using var tx = _db!.BeginTransaction();
            using (var del = _db.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM player_damage_params"; del.ExecuteNonQuery(); }
            using var ins = _db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO player_damage_params(kind, name, damage_type, value, source) VALUES(@k, @n, @t, @v, @s)";
            var pK = ins.Parameters.Add("@k", SqliteType.Text);
            var pN = ins.Parameters.Add("@n", SqliteType.Text);
            var pT = ins.Parameters.Add("@t", SqliteType.Text);
            var pV = ins.Parameters.Add("@v", SqliteType.Real);
            var pS = ins.Parameters.Add("@s", SqliteType.Text);
            foreach (var r in rows)
            {
                pK.Value = r.Kind; pN.Value = r.Name; pT.Value = r.DamageType; pV.Value = r.Value; pS.Value = r.Source;
                ins.ExecuteNonQuery();
            }
            tx.Commit();
            return rows.Count;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"体力・部位・アーマーの倍率の取り込みに失敗しました: {ex.Message}");
            return 0;
        }
    }

    // dcb query の出力 3 つから player_damage_params の行を作る。体力・4 部位・アーマーの倍率のどれかが無ければ空
    internal static List<(string Kind, string Name, string DamageType, double Value, string Source)> ParsePlayerDamageParams(string statusJson, string bodyJson, string macroJson)
    {
        var rows = new List<(string, string, string, double, string)>();
        double health = 0, downed = 0, excess = 0;
        foreach (var block in SplitJsonBlocks(statusJson ?? ""))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                if ((root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() : null) != "ActorStatusComponent.PlayerStatusPU") continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;
                health = FindHealthPoolMax(rv, 0);
                if (rv.TryGetProperty("downedParams", out var dp) && dp.ValueKind == JsonValueKind.Object)
                {
                    if (dp.TryGetProperty("damageToInstaKillFromDowned", out var d1) && d1.ValueKind == JsonValueKind.Number) downed = d1.GetDouble();
                    if (dp.TryGetProperty("excessDamageToInstaKill", out var d2) && d2.ValueKind == JsonValueKind.Number) excess = d2.GetDouble();
                }
            }
            catch { }
        }
        var parts = new Dictionary<string, double>(StringComparer.Ordinal);
        var nakedParts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var block in SplitJsonBlocks(bodyJson ?? ""))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                if ((root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() : null) != "BodyHealthConfig.HealthConfig_Player") continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("bodyPartConfigs", out var cfgs) || cfgs.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in cfgs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("damageMultiplier", out var dm) || dm.ValueKind != JsonValueKind.Number) continue;
                    if (!c.TryGetProperty("bodyParts", out var bps) || bps.ValueKind != JsonValueKind.Array) continue;
                    foreach (var bp in bps.EnumerateArray())
                    {
                        var file = Path.GetFileNameWithoutExtension(bp.GetString() ?? "").ToLowerInvariant();
                        var part = file switch
                        {
                            "head" => "head", "torso" => "torso",
                            "rightarm" or "leftarm" => "arm", "rightleg" or "leftleg" => "leg",
                            _ => null,
                        };
                        if (part == null) continue;
                        parts.TryAdd(part, dm.GetDouble());
                        // 素肌 (アーマーを着ていない) ときの倍率。無い record では入れない
                        if (c.TryGetProperty("nakedDamageMultiplier", out var nm) && nm.ValueKind == JsonValueKind.Number) nakedParts.TryAdd(part, nm.GetDouble());
                    }
                }
            }
            catch { }
        }
        var armorRows = new List<(string, string, string, double, string)>();
        foreach (var block in SplitJsonBlocks(macroJson ?? ""))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var name = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (!name.StartsWith("DamageResistanceMacro.", StringComparison.Ordinal)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("damageResistance", out var dr) || dr.ValueKind != JsonValueKind.Object) continue;
                foreach (var type in new[] { "Physical", "Energy", "Distortion", "Thermal", "Biochemical", "Stun" })
                    if (dr.TryGetProperty(type + "Resistance", out var entry) && entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("Multiplier", out var m) && m.ValueKind == JsonValueKind.Number)
                        armorRows.Add(("armor", name["DamageResistanceMacro.".Length..], type, m.GetDouble(), "DamageResistanceMacro"));
            }
            catch { }
        }
        if (health <= 0 || new[] { "head", "torso", "arm", "leg" }.Any(p => !parts.ContainsKey(p)) || armorRows.Count == 0) return rows;
        rows.Add(("health", "", "", health, "ActorStatusComponent.PlayerStatusPU の HealthPool"));
        if (downed > 0) rows.Add(("downed", "", "", downed, "ActorStatusComponent.PlayerStatusPU の downedParams.damageToInstaKillFromDowned"));
        if (excess > 0) rows.Add(("excess_instakill", "", "", excess, "ActorStatusComponent.PlayerStatusPU の downedParams.excessDamageToInstaKill"));
        foreach (var (part, v) in parts) rows.Add(("body_part", part, "", v, "BodyHealthConfig.HealthConfig_Player の bodyPartConfigs.damageMultiplier"));
        foreach (var (part, v) in nakedParts) rows.Add(("body_part_naked", part, "", v, "BodyHealthConfig.HealthConfig_Player の bodyPartConfigs.nakedDamageMultiplier"));
        rows.AddRange(armorRows);
        return rows;
    }

    // ActorStatData (statType = HealthPool) の maximumStatValue を探す (見つからなければ 0)
    private static double FindHealthPoolMax(JsonElement el, int depth)
    {
        if (depth > 8) return 0;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("_Type_", out var t) && t.GetString() == "ActorStatData"
                && el.TryGetProperty("statType", out var st) && st.GetString() == "HealthPool"
                && el.TryGetProperty("maximumStatValue", out var mx) && mx.ValueKind == JsonValueKind.Number)
                return mx.GetDouble();
            foreach (var p in el.EnumerateObject())
            {
                var v = FindHealthPoolMax(p.Value, depth + 1);
                if (v > 0) return v;
            }
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var c in el.EnumerateArray())
            {
                var v = FindHealthPoolMax(c, depth + 1);
                if (v > 0) return v;
            }
        return 0;
    }

    // dcb query "AmmoParams" で弾のデータを全件取り、ammo_params を作り直す (取れなかったときは前のデータを残す)
    private async Task<int> ExtractAmmoParamsAsync(string p4kPath, string now, CancellationToken ct)
    {
        var rawJson = await RunDcbQueryWithTimerAsync(p4kPath, "AmmoParams", "*", "弾のデータ (AmmoParams)", ct);
        var rows = ParseAmmoParams(rawJson);
        if (rows.Count == 0) return 0;

        using var tx = _db!.BeginTransaction();
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM ammo_params";
            del.ExecuteNonQuery();
        }
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO ammo_params(record_name, speed, lifetime, damage_json, explosion_json, raw_json, extracted_at) VALUES(@rn, @sp, @lt, @dj, @ej, @rj, @ea)";
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pSp = cmd.Parameters.Add("@sp", SqliteType.Real);
        var pLt = cmd.Parameters.Add("@lt", SqliteType.Real);
        var pDj = cmd.Parameters.Add("@dj", SqliteType.Text);
        var pEj = cmd.Parameters.Add("@ej", SqliteType.Text);
        var pRj = cmd.Parameters.Add("@rj", SqliteType.Text);
        cmd.Parameters.AddWithValue("@ea", now);
        foreach (var r in rows)
        {
            pRn.Value = r.RecordName;
            pSp.Value = r.Speed;
            pLt.Value = r.Lifetime;
            pDj.Value = (object?)r.DamageJson ?? DBNull.Value;
            pEj.Value = (object?)r.ExplosionJson ?? DBNull.Value;
            pRj.Value = r.RawJson;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return rows.Count;
    }

    // dcb query "AmmoParams" の出力を 1 弾 1 行にする。
    // 弾速 = _RecordValue_.speed (m/s)、寿命 = lifetime (秒)、直撃のダメージ = projectileParams.damage (DamageInfo)、
    // 爆発のダメージ = projectileParams.detonationParams.explosionParams.damage (DamageInfo)。
    // DamageInfo は DamagePhysical / DamageEnergy / DamageDistortion / DamageThermal / DamageBiochemical / DamageStun を
    // {"Physical": 値, …} の JSON にする (すべて 0 なら NULL)
    internal static List<(string RecordName, double Speed, double Lifetime, string? DamageJson, string? ExplosionJson, string RawJson)> ParseAmmoParams(string rawJson)
    {
        var rows = new List<(string, double, double, string?, string?, string)>();
        if (string.IsNullOrEmpty(rawJson)) return rows;
        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (!recordName.StartsWith("AmmoParams.", StringComparison.Ordinal)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || rv.ValueKind != JsonValueKind.Object) continue;
                double speed = rv.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetDouble() : 0;
                double lifetime = rv.TryGetProperty("lifetime", out var lt) && lt.ValueKind == JsonValueKind.Number ? lt.GetDouble() : 0;
                string? direct = null, explosion = null;
                if (rv.TryGetProperty("projectileParams", out var pp) && pp.ValueKind == JsonValueKind.Object)
                {
                    if (pp.TryGetProperty("damage", out var dmg)) direct = DamageInfoJson(dmg);
                    if (pp.TryGetProperty("detonationParams", out var det) && det.ValueKind == JsonValueKind.Object
                        && det.TryGetProperty("explosionParams", out var ex) && ex.ValueKind == JsonValueKind.Object
                        && ex.TryGetProperty("damage", out var exd))
                        explosion = DamageInfoJson(exd);
                }
                rows.Add((recordName, speed, lifetime, direct, explosion, block));
            }
            catch { }
        }
        return rows;
    }

    private static string? DamageInfoJson(JsonElement dmg)
    {
        if (dmg.ValueKind != JsonValueKind.Object) return null;
        var d = new Dictionary<string, double>();
        foreach (var key in new[] { "Physical", "Energy", "Distortion", "Thermal", "Biochemical", "Stun" })
            if (dmg.TryGetProperty("Damage" + key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var x) && x != 0) d[key] = x;
        return d.Count > 0 ? JsonSerializer.Serialize(d) : null;
    }

    // 売り場・価格を取る武器・装備 (item_index)。個人武器・道具 (WeaponPersonal)、手榴弾・設置型の爆発物・医療用具 (FPS_Consumable)、
    // 武器の付属品のうち個人武器用 (照準器・アンダーバレル・マガジン・マルチツール用・ランチャー用と、マズル。Barrel 等は船の武器の部品なので除く)、
    // マズル (個人武器のバレルの付属品 arma_barrel_* など) は SubType が船の武器のバレルと同じ Barrel なので、items.raw_json の
    // SAttachableComponentParams の AttachDef.Tags (空白区切り) に FPS_Barrel があるものだけを入れる
    // (色違いで raw_json を空にした行は json_each に渡すと malformed JSON で問い合わせ全体が失敗するので、json_valid で NULL にして飛ばす)
    // アーマー・アンダースーツ・バックパック・服、船の部品 (シールド・動力装置・クアンタムドライブ・冷却装置・レーダー)、
    // 船の武器・ミサイル・ミサイルラック・爆弾、船のトラクタービーム・サルベージヘッド。
    // 追加で取り込む品目 (ExtractExtraItemsAsync) のうち、採掘レーザー・採掘モジュール・サルベージのモジュール・燃料ノズル (DockingCollar の Fuel)・
    // 採掘の鉱石ポッド (Container で record 名が Cargo_ShipMining_Pod_* / Cargo_GroundVehicleMining_Pod_*)。どれも cstone の品目のページに
    // 売り場がある (finder.cstone.space/search/{uuid} で確かめた。サルベージヘッドは売り場なし)。名前の無いもの・プレースホルダは除く。
    // プレースホルダは、名前のローカライズキーが @LOC_PLACEHOLDER のもの。item_index.name はキーを訳した文 (英語の global.ini に
    // 日本語が入っていれば「<= プレースホルダー =>」) なので、キーが残っている items.name で判定し、items に行が無いときのために
    // 訳した文 (英語・日本語) でも外す
    internal const string ShopPriceTargetWhere = """
        (item_type IN ('WeaponPersonal', 'Grenade', 'FPS_Deployable', 'FPS_Consumable', 'Gadget',
                       'Shield', 'PowerPlant', 'QuantumDrive', 'Cooler', 'Radar',
                       'WeaponGun', 'Missile', 'MissileLauncher', 'Bomb', 'BombLauncher',
                       'TractorBeam', 'SalvageHead', 'TowingBeam',
                       'WeaponMining', 'MiningModifier', 'SalvageModifier')
         OR (item_type = 'DockingCollar' AND sub_type = 'Fuel')
         OR (item_type = 'Container' AND (record_name LIKE 'EntityClassDefinition.Cargo\_ShipMining\_Pod\_%' ESCAPE '\'
                                          OR record_name LIKE 'EntityClassDefinition.Cargo\_GroundVehicleMining\_Pod\_%' ESCAPE '\'))
         OR item_type LIKE 'Char_Armor_%' OR item_type LIKE 'Char_Clothing_%'
         OR (item_type = 'WeaponAttachment' AND sub_type IN ('IronSight', 'BottomAttachment', 'Magazine', 'Utility', 'Missile'))
         OR (item_type = 'WeaponAttachment' AND sub_type = 'Barrel'
             AND EXISTS (SELECT 1 FROM items,
                                json_each(CASE WHEN json_valid(items.raw_json) THEN items.raw_json END, '$._RecordValue_.Components') AS comp
                         WHERE items.record_name = item_index.record_name
                           AND json_extract(comp.value, '$._Type_') = 'SAttachableComponentParams'
                           AND ' ' || json_extract(comp.value, '$.AttachDef.Tags') || ' ' LIKE '% FPS_Barrel %')))
        AND name IS NOT NULL AND TRIM(name) <> '' AND name NOT IN ('<= PLACEHOLDER =>', '<= プレースホルダー =>', '@LOC_PLACEHOLDER')
        AND NOT EXISTS (SELECT 1 FROM items WHERE items.record_name = item_index.record_name AND items.name = '@LOC_PLACEHOLDER')
        """;

    // 裏の取得の途中の件数をログ (onStatus) に出す間隔。武器・装備は 25 件ごとか 30 秒ごと、船・車両は 30 秒ごと
    private const int ShopPriceLogEveryItems = 25;
    private static readonly TimeSpan ShopPriceLogInterval = TimeSpan.FromSeconds(30);

    // 売り場・価格がまだ保存されていない品目だけを取得して item_shop_prices に保存する。インデックス構築・再抽出の後や起動時に、
    // MainWindow が裏で呼ぶ (自分の DB 接続を使うので、アプリの他の処理と同時に動いてよい)。
    // 武器・装備は cstone (finder.cstone.space/search/{uuid}) を 1 件ずつ、船・車両は UEX の一括 API を使う。
    // 1 件の失敗では止めない (失敗した品目は保存しないので、次回に取り直す)。キャンセルされたら、そこまでの分を残して終える。
    // onStatus は節目の知らせ (途中の件数も一定の間隔で出す)、onProgress は (割合, 進み具合の文) で、どちらも呼び出し側のスレッドとは別のスレッドから呼ばれる
    public static async Task FetchNewShopPricesAsync(string dbPath, Action<string>? onStatus, Action<int, string>? onProgress, CancellationToken ct)
    {
        // 今の段階 (武器・装備 / 船・車両) のそこまでの件数の文。中止したときの知らせに付ける
        Func<string>? currentCounts = null;
        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath}");
            db.Open();
            using (var init = db.CreateCommand())
            {
                init.CommandText = ShopPriceStore.TableDdl;
                init.ExecuteNonQuery();
            }
            // 取得中の印 (最後まで終わったら消す)。アプリを閉じる等で途中で終わったときだけ、起動時に続きを取る
            SetMeta(db, ShopPriceInProgressMetaKey, DateTime.UtcNow.ToString("o"));
            var targets = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = $"SELECT uuid FROM item_index WHERE {ShopPriceTargetWhere} AND uuid NOT IN (SELECT uuid FROM item_shop_prices) ORDER BY item_type, record_name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) targets.Add(r.GetString(0));
            }

            if (targets.Count > 0)
            {
                onStatus?.Invoke($"[売り場・価格] 未取得の武器・装備 {targets.Count} 件を cstone から取得します");
                var sw = Stopwatch.StartNew();
                int withShop = 0, noShop = 0, notOnCstone = 0, failed = 0, done = 0;
                string Counts() => $"[売り場・価格] 武器・装備 {done:N0} / {targets.Count:N0} 件 (売り場あり {withShop:N0}, 売り場なし {noShop:N0}, cstone に品目無し {notOnCstone:N0}, 失敗 {failed:N0})";
                currentCounts = Counts;
                var lastLog = Stopwatch.StartNew();
                int lastLoggedDone = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    // cstone への間隔 (0.75 秒) と同時に 1 つの順番待ちは FetchCstoneShopRowsAsync の中で行う (チャットの取り直しと共用)
                    var rows = await ChatService.FetchCstoneShopRowsAsync(targets[i], ct);
                    if (rows == null) failed++;
                    else if (!await TryReplaceShopRowsAsync(db, targets[i], rows, onStatus, ct)) failed++;
                    else if (rows.Any(x => x.Kind != ShopPriceStore.KindNone)) withShop++;
                    else if (ShopPriceStore.IsCstoneNotFound(rows)) notOnCstone++;
                    else noShop++;
                    done = i + 1;
                    // ログ (節目の知らせ) には 25 件ごとか 30 秒ごとに件数を出す。最後の 1 件の分は下のまとめで出す
                    if (done < targets.Count && (done - lastLoggedDone >= ShopPriceLogEveryItems || lastLog.Elapsed >= ShopPriceLogInterval))
                    {
                        onStatus?.Invoke(Counts());
                        lastLoggedDone = done;
                        lastLog.Restart();
                    }
                    var remainMin = sw.Elapsed.TotalSeconds / done * (targets.Count - done) / 60;
                    onProgress?.Invoke(done * 100 / targets.Count, $"{Counts()} 残り約 {remainMin:F0} 分");
                }
                currentCounts = null;
                onStatus?.Invoke($"[売り場・価格] 武器・装備: 売り場あり {withShop} 件, 売り場なし {noShop} 件, cstone に品目無し {notOnCstone} 件, 取得失敗 {failed} 件 ({sw.Elapsed.TotalMinutes:F1} 分)" +
                                      (failed > 0 ? "。失敗した品目は次回に取り直します" : "") +
                                      (notOnCstone > 0 ? "。cstone に品目無しの品目は、次のインデックス構築・装備データの再抽出の後に取り直します" : ""));
            }

            var shipTargets = new List<string>();
            using (var cmd = db.CreateCommand())
            {
                // プレースホルダ (名前のキーが @LOC_PLACEHOLDER) は除く。キーが残っている ships.name・items.name で判定し、
                // どちらにも行が無いときのために訳した文 (英語・日本語) でも外す (ShopPriceTargetWhere と同じ考え方)
                cmd.CommandText = "SELECT uuid FROM item_index WHERE item_type = 'NOITEM_Vehicle' AND name IS NOT NULL AND TRIM(name) <> '' " +
                                  "AND name NOT IN ('<= PLACEHOLDER =>', '<= プレースホルダー =>', '@LOC_PLACEHOLDER') " +
                                  "AND NOT EXISTS (SELECT 1 FROM ships WHERE ships.record_name = item_index.record_name AND ships.name = '@LOC_PLACEHOLDER') " +
                                  "AND NOT EXISTS (SELECT 1 FROM items WHERE items.record_name = item_index.record_name AND items.name = '@LOC_PLACEHOLDER') " +
                                  "AND uuid NOT IN (SELECT uuid FROM item_shop_prices)";
                using var r = cmd.ExecuteReader();
                while (r.Read()) shipTargets.Add(r.GetString(0));
            }
            if (shipTargets.Count > 0)
            {
                onStatus?.Invoke($"[売り場・価格] 未取得の船・車両 {shipTargets.Count} 件の購入・レンタル価格を UEX から取得中...");
                var map = await FetchUexShipPriceRowsAsync(db, ct);
                if (map == null)
                {
                    onStatus?.Invoke("[売り場・価格] UEX から船・車両の価格を取得できませんでした (次回に取り直します)");
                }
                else
                {
                    int withShop = 0, done = 0;
                    // 「なし」は終わりのまとめと同じ数え方 (処理した件数 - 登録あり)
                    string Counts() => $"[売り場・価格] 船・車両 {done:N0} / {shipTargets.Count:N0} 件 (購入・レンタルの登録あり {withShop:N0}, なし {done - withShop:N0})";
                    currentCounts = Counts;
                    var lastLog = Stopwatch.StartNew();
                    // UEX からは一括で取ってあり、ここは DB への保存だけで速いので、ログには 30 秒ごとにだけ件数を出す
                    foreach (var uuid in shipTargets)
                    {
                        if (map.TryGetValue(uuid, out var rows)) { if (await TryReplaceShopRowsAsync(db, uuid, rows, onStatus, ct)) withShop++; }
                        else await TryReplaceShopRowsAsync(db, uuid, [UexNoShopRow()], onStatus, ct);
                        done++;
                        if (done < shipTargets.Count && lastLog.Elapsed >= ShopPriceLogInterval)
                        {
                            onStatus?.Invoke(Counts());
                            lastLog.Restart();
                        }
                        onProgress?.Invoke(done * 100 / shipTargets.Count, Counts());
                    }
                    currentCounts = null;
                    onStatus?.Invoke($"[売り場・価格] 船・車両: 購入・レンタルの登録あり {withShop} 件, なし {shipTargets.Count - withShop} 件");
                }
            }
            // 価格を取り終えたので、ゲームの店と DB の価格の紐づけ (shop_price_links。［詳細］タブのお店) を作り直す。
            // 店の表 (game_shops) が無い・空なら作らない。失敗しても取得の結果は使えるので、知らせて続ける
            try
            {
                var tradeDb = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath)) ?? ".", "trade_cache.db");
                onStatus?.Invoke(ShopTables.RebuildLinks(db, tradeDb).Summary);
            }
            catch (Exception ex) { onStatus?.Invoke($"[お店] 価格の紐づけを作り直せませんでした: {ex.Message}"); }
            // 最後まで終わったので取得中の印を消す (取れなかった品目は次のインデックス構築・再抽出の後に取り直す)
            DeleteMeta(db, ShopPriceInProgressMetaKey);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var counts = currentCounts?.Invoke();
            onStatus?.Invoke("[売り場・価格] 中止しました (取得済みの分は保存しています。残りは次回、まだ取っていない品目から続けます)" +
                             (counts != null ? $"。中止までの件数: {counts.Replace("[売り場・価格] ", "")}" : ""));
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"[売り場・価格] 取得中にエラーが発生しました: {ex.Message}");
        }
    }

    // 1 品目の行を保存する。database is locked などで失敗したら少し待って取り直し (3 回まで)、だめならその品目を飛ばす
    // (保存しないので次回取り直す)。保存できたら true
    private static async Task<bool> TryReplaceShopRowsAsync(SqliteConnection db, string uuid, IReadOnlyList<ShopPriceRow> rows, Action<string>? onStatus, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ShopPriceStore.Replace(db, uuid, rows, DateTime.UtcNow);
                return true;
            }
            catch (SqliteException ex)
            {
                if (attempt >= 3)
                {
                    onStatus?.Invoke($"[売り場・価格] {uuid}: DB に保存できませんでした ({ex.Message})。この品目は次回取り直します");
                    return false;
                }
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    private static void SetMeta(SqliteConnection db, string key, string value)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS gamedata_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT OR REPLACE INTO gamedata_meta(key, value) VALUES(@k, @v)";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    private static void DeleteMeta(SqliteConnection db, string key)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM gamedata_meta WHERE key = @k";
        cmd.Parameters.AddWithValue("@k", key);
        cmd.ExecuteNonQuery();
    }

    // 売り場・価格の取得の印 (手動中止・取得中) を読む / 戻す (インデックス構築が失敗したときに戻すため)
    private static Dictionary<string, string> ReadShopPriceMarks(SqliteConnection db)
    {
        var marks = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM gamedata_meta WHERE key IN (@a, @b)";
            cmd.Parameters.AddWithValue("@a", ShopPriceStoppedMetaKey);
            cmd.Parameters.AddWithValue("@b", ShopPriceInProgressMetaKey);
            using var r = cmd.ExecuteReader();
            while (r.Read()) marks[r.GetString(0)] = r.GetString(1);
        }
        catch { }
        return marks;
    }

    private static void RestoreShopPriceMarks(SqliteConnection db, Dictionary<string, string> marks)
    {
        foreach (var (k, v) in marks) SetMeta(db, k, v);
    }

    // 売り場・価格の取得が途中で終わった (取得中の印が残っている) か。印があるときだけ起動時に続きを取る
    private const string ShopPriceInProgressMetaKey = "shop_price_fetch_in_progress";

    // この PC だけの印 (売り場・価格の取得中・手動中止)。バックアップに入れず、インポートでも受け取った側の印を上書きしない
    internal static readonly string[] LocalOnlyMetaKeys = [ShopPriceInProgressMetaKey, ShopPriceStoppedMetaKey];

    // 取得中の印を立てる (アプリを閉じる途中に取得の依頼が来たとき。次の起動で、まだ取っていない品目から取る: MainWindow の起動時の続き取得)
    public static void MarkShopPriceFetchInProgress(string dbPath)
    {
        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        SetMeta(db, ShopPriceInProgressMetaKey, DateTime.UtcNow.ToString("o"));
    }

    public static bool IsShopPriceFetchInterrupted(string dbPath)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM gamedata_meta WHERE key = @k";
            cmd.Parameters.AddWithValue("@k", ShopPriceInProgressMetaKey);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    // ［価格の取得を中止］で利用者が止めたことの印。gamedata_meta の shop_price_fetch_stopped に止めた時刻を入れる
    // (インデックス構築は gamedata_meta を消して作り直すが、構築の前に退避して、失敗したときは戻す)。印があれば起動時の続き取得をしない
    private const string ShopPriceStoppedMetaKey = "shop_price_fetch_stopped";

    public static void SetShopPriceFetchStopped(string dbPath, bool stopped)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath}");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS gamedata_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = stopped
                ? "INSERT OR REPLACE INTO gamedata_meta(key, value) VALUES(@k, @v)"
                : "DELETE FROM gamedata_meta WHERE key = @k";
            cmd.Parameters.AddWithValue("@k", ShopPriceStoppedMetaKey);
            if (stopped) cmd.Parameters.AddWithValue("@v", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    public static bool IsShopPriceFetchStopped(string dbPath)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM gamedata_meta WHERE key = @k";
            cmd.Parameters.AddWithValue("@k", ShopPriceStoppedMetaKey);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    // UEX に船・車両の購入・レンタルの登録が無かったことの記録
    internal static ShopPriceRow UexNoShopRow() =>
        new(ShopPriceStore.KindNone, "", null, null, "", "UEX に購入・レンタルの登録無し", "uex");

    // UEX の船・車両の購入価格 (vehicles_purchases_prices_all) とレンタル価格 (vehicles_rentals_prices_all。1 日分) を取り、
    // item_index の船・車両 (NOITEM_Vehicle) の uuid ごとの行にする (API キー不要の GET 3 回)。
    // 対応付け: UEX の uuid が item_index の uuid と一致すればそれ、一致しなければ UEX の name_full と ships.name_en の一致。
    // 同じ name_en の record (AI 用などの複製) の uuid すべてに同じ行を付ける。購入・レンタルの登録が無い船は含めない。取れなければ null
    internal static async Task<Dictionary<string, List<ShopPriceRow>>?> FetchUexShipPriceRowsAsync(SqliteConnection db, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            var vehiclesJson = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles", cts.Token);
            var buyJson = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles_purchases_prices_all", cts.Token);
            var rentJson = await Http.GetStringAsync("https://api.uexcorp.space/2.0/vehicles_rentals_prices_all", cts.Token);

            // item_index の船・車両: uuid → name_en、name_en → uuid 一覧
            var uuidToName = new Dictionary<string, string>(StringComparer.Ordinal);
            var nameToUuids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT x.uuid, s.name_en FROM item_index x LEFT JOIN ships s ON s.record_name = x.record_name WHERE x.item_type = 'NOITEM_Vehicle'";
                try
                {
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var uuid = r.GetString(0);
                        var nameEn = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                        uuidToName[uuid] = nameEn;
                        if (nameEn.Length == 0) continue;
                        if (!nameToUuids.TryGetValue(nameEn, out var list)) nameToUuids[nameEn] = list = new List<string>();
                        list.Add(uuid);
                    }
                }
                catch (SqliteException)
                {
                    // name_en 列の無い古い DB: uuid の一致だけで対応付ける
                    cmd.CommandText = "SELECT uuid FROM item_index WHERE item_type = 'NOITEM_Vehicle'";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) uuidToName[r.GetString(0)] = "";
                }
            }

            static string Verified(JsonElement row) =>
                row.TryGetProperty("date_modified", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt64(out var sec) && sec > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime.ToString("yyyy-MM-dd") : "";
            static string Str(JsonElement row, string key) =>
                row.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            // UEX の車両 id ごとの行
            var rowsByVehicle = new Dictionary<int, List<ShopPriceRow>>();
            void AddRows(string json, string priceKey, string kind, int? rentDays)
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return;
                foreach (var row in data.EnumerateArray())
                {
                    if (!row.TryGetProperty("id_vehicle", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                    if (!row.TryGetProperty(priceKey, out var pe) || pe.ValueKind != JsonValueKind.Number || !pe.TryGetDouble(out var price)) continue;
                    if (price < ShopPriceStore.MinPrice || price > ShopPriceStore.MaxPrice) continue;
                    if (!rowsByVehicle.TryGetValue(id, out var list)) rowsByVehicle[id] = list = new List<ShopPriceRow>();
                    list.Add(new ShopPriceRow(kind, Str(row, "terminal_name"), price, rentDays, Verified(row), "", "uex"));
                }
            }
            AddRows(buyJson, "price_buy", ShopPriceStore.KindBuy, null);
            // UEX のレンタル価格は 1 日分 (100i の 28,665 aUEC が cstone の 1 DAY と一致することで確認)
            AddRows(rentJson, "price_rent", ShopPriceStore.KindRent, 1);

            var result = new Dictionary<string, List<ShopPriceRow>>(StringComparer.Ordinal);
            using var vdoc = JsonDocument.Parse(vehiclesJson);
            if (!vdoc.RootElement.TryGetProperty("data", out var vdata) || vdata.ValueKind != JsonValueKind.Array) return null;
            var byName = new List<(string NameFull, List<ShopPriceRow> Rows)>();
            foreach (var v in vdata.EnumerateArray())
            {
                if (!v.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                if (!rowsByVehicle.TryGetValue(id, out var rows) || rows.Count == 0) continue;
                var uexUuid = Str(v, "uuid");
                if (uexUuid.Length > 0 && uuidToName.TryGetValue(uexUuid, out var nameEn))
                {
                    // uuid が一致: その船と、同じ name_en の record すべて
                    var uuids = nameEn.Length > 0 && nameToUuids.TryGetValue(nameEn, out var same) ? same : new List<string> { uexUuid };
                    foreach (var u in uuids.Append(uexUuid).Distinct()) result.TryAdd(u, rows);
                }
                else byName.Add((Str(v, "name_full"), rows));
            }
            // uuid で対応付かなかった UEX の船は name_full = ships.name_en で対応付ける (uuid で付いたものは上書きしない)
            foreach (var (nameFull, rows) in byName)
                if (nameFull.Length > 0 && nameToUuids.TryGetValue(nameFull, out var uuids))
                    foreach (var u in uuids) result.TryAdd(u, rows);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ChatService.LogExternal($"[UEX] 船・車両の価格の取得に失敗: {ex.Message}");
            return null;
        }
    }

    public bool IsP4kUpdated()
    {
        EnsureDb();
        var stored = GetMeta("p4k_last_modified");
        if (string.IsNullOrEmpty(stored)) return true;
        var p4kPath = FindDataP4k();
        if (p4kPath == null) return false;

        // 構築したときのゲームの版と今の版が両方分かれば、版で比べる (配布の DB を受け取っただけでは
        // Data.p4k の更新日時が作者の PC と違うので、日時では「パッチ更新あり」と出てしまうため)
        var (curVersion, curChangeNum) = ReadBuildManifest(p4kPath);
        var storedVersion = GetMeta(BuildVersionMetaKey);
        var storedChangeNum = GetMeta(BuildChangeNumMetaKey);
        bool comparable = false, differs = false;
        if (!string.IsNullOrEmpty(storedVersion) && !string.IsNullOrEmpty(curVersion))
        {
            comparable = true;
            differs |= storedVersion != curVersion;
        }
        if (!string.IsNullOrEmpty(storedChangeNum) && !string.IsNullOrEmpty(curChangeNum))
        {
            comparable = true;
            differs |= storedChangeNum != curChangeNum;
        }
        if (comparable) return differs;

        // 版が分からないときは今までどおり Data.p4k の更新日時で比べる
        var current = File.GetLastWriteTimeUtc(p4kPath).ToString("o");
        return current != stored;
    }

    // missions の wiki の列 (FetchWikiMissionsAsync が入れるもの。MigrateWikiColumns で足す列)
    private static readonly string[] WikiMissionColumns =
        ["wiki_title", "wiki_faction", "wiki_reward", "wiki_legality", "wiki_enemy_min", "wiki_enemy_max", "wiki_duration_min", "wiki_uuid"];

    // 構築の前に、missions の wiki の列が入っている行を一時表 (temp.wiki_keep) に控える
    internal static void SaveWikiColumns(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        var cols = string.Join(", ", WikiMissionColumns);
        cmd.CommandText = "DROP TABLE IF EXISTS temp.wiki_keep; "
            + $"CREATE TEMP TABLE wiki_keep AS SELECT record_name, {cols} FROM main.missions WHERE "
            + string.Join(" OR ", WikiMissionColumns.Select(c => $"{c} IS NOT NULL"));
        cmd.ExecuteNonQuery();
    }

    // 控えた wiki の列を、構築後の missions の同じ record_name の行へ戻す (無くなったミッションの分は捨てる)。戻した行数を返す
    internal static int RestoreWikiColumns(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM temp.sqlite_master WHERE type = 'table' AND name = 'wiki_keep'";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return 0;
        cmd.CommandText = $"UPDATE main.missions SET {string.Join(", ", WikiMissionColumns.Select(c => $"{c} = k.{c}"))} "
            + "FROM temp.wiki_keep AS k WHERE main.missions.record_name = k.record_name";
        var n = cmd.ExecuteNonQuery();
        cmd.CommandText = "DROP TABLE IF EXISTS temp.wiki_keep";
        cmd.ExecuteNonQuery();
        return n;
    }

    // インデックス構築 (BuildStructuredIndexStepsAsync) が作り直すデータについての gamedata_meta の記録。構築の最初に消す。
    // game_version / p4k_last_modified / indexed_at / p4k_path / 版: 構築の手順6で書き直す。
    // equipment_rebuilt_at: 構築が作り直す装備データ (item_index / ships / ship_ports) を前に再抽出した時刻。
    // BlueprintModifiersMetaKey: BP の品質の増減を取り出した時刻 (構築の手順6の BP の取り出しで書き直す。失敗したら書かれない。
    // 起動後に取り直すのは、増減の行が 0 行で、この記録も無いとき (NeedsBlueprints))。
    // ExtraItemsMetaKey: 追加の品目を取り込んだ時刻 (構築の手順6b で書き直す。失敗したら書かれず、起動後に裏で取り込む: NeedsExtraItems)。
    internal static readonly string[] RebuiltDataMetaKeys =
    [
        "game_version", "p4k_last_modified", "indexed_at", "p4k_path", BuildVersionMetaKey, BuildChangeNumMetaKey,
        "equipment_rebuilt_at", BlueprintModifiersMetaKey, ExtraItemsMetaKey,
    ];

    // gamedata_meta に残すゲームの版 (build_manifest.id の Data.Version と Data.RequestedP4ChangeNum)
    internal const string BuildVersionMetaKey = "build_version";
    internal const string BuildChangeNumMetaKey = "build_p4_change_num";

    // Data.p4k と同じフォルダの build_manifest.id から、版 (Data.Version 例 "4.10.197.2878") と
    // RequestedP4ChangeNum (例 "12913470") を読む。ファイルが無い・読めない・値が無ければ null
    internal static (string? Version, string? ChangeNum) ReadBuildManifest(string p4kPath)
    {
        var dir = Path.GetDirectoryName(p4kPath);
        if (string.IsNullOrEmpty(dir)) return (null, null);
        var (version, changeNum, _) = ReadBuildManifestFile(Path.Combine(dir, "build_manifest.id"));
        return (version, changeNum);
    }

    // build_manifest.id (JSON。{"Data": {"Branch": "sc-alpha-4.10.2", "Version": "4.10.197.2878", "RequestedP4ChangeNum": "12913470", ...}}) の
    // Data.Version・Data.RequestedP4ChangeNum・Data.Branch を読む。ファイルが無い・読めない・値が無ければ null
    internal static (string? Version, string? ChangeNum, string? Branch) ReadBuildManifestFile(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return (null, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Object) return (null, null, null);
            static string? Str(JsonElement e, string key) =>
                e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                    ? v.GetString()!.Trim() : null;
            return (Str(data, "Version"), Str(data, "RequestedP4ChangeNum"), Str(data, "Branch"));
        }
        catch { return (null, null, null); }
    }

    // 装備データ (item_index / ships / ship_ports) だけを再抽出する。全体インデックス (BuildStructuredIndexAsync) とは別扱いで、
    // missions / commodities / items / knowledge / item_vectors / gamedata_cache 等は触らず、gamedata_meta の indexed_at も更新しない
    // (代わりに equipment_rebuilt_at を保存する)。item_index_fts は item_index から作り直す。
    // 進捗は progress と StatusChanged の両方へ流す
    public async Task RebuildEquipmentDataAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // アプリを閉じるとき (AppExit) も取り消す。取り消すと *_bak 表から戻してから終わる (閉じる処理はそれを待つ)
        using var exitWork = AppExit.BeginWork("装備データの再抽出");
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, AppExit.Token);
        ct = linkedCts.Token;
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        // 構築と同じく、取得を止める前から再開を頼んだ後まで IsIndexBuildRunning を立てておく (下の finally で下ろす)
        Interlocked.Increment(ref s_indexBuildsRunning);
        try
        {
            // 売り場・価格の裏の取得を止めてから再抽出する (成功すれば最後の ShopPriceFetchRequested、失敗すれば下の catch で再開)
            await PauseShopPriceFetchAsync();
            using var dcbScope = await UseExtractedDcbAsync(p4kPath, ct);
            // 船・車両の段階は、アイテムインデックスと同じ問い合わせ (製造元の接頭辞) をし直すので、出力を使い回す
            RegisterReuseQueries(VehicleManufacturerPrefixes.Select(p => $"EntityClassDefinition.{p}_*"));
            var sw = Stopwatch.StartNew();
            var now = DateTime.UtcNow.ToString("o");
            StatusChanged?.Invoke("装備データ (item_index / ships / ship_ports) を再構築中...");

            // 抽出が途中で失敗しても 3 表が空のまま残らないよう、DELETE の前に同 DB 内へバックアップ表を作る
            // (成功・失敗いずれでも finally で DROP する)。前回の再抽出の途中の印が残っていれば始めない (InterruptedWork)
            var workDir = Path.GetDirectoryName(Path.GetFullPath(DbPath)) ?? ".";
            InterruptedWork.ThrowIfPending(workDir, InterruptedWork.KindEquip);
            using (var cmd = _db!.CreateCommand())
            {
                cmd.CommandText = string.Join(" ", EquipmentTables.Select(t =>
                    $"DROP TABLE IF EXISTS {t}_bak; CREATE TABLE {t}_bak AS SELECT * FROM {t};"));
                cmd.ExecuteNonQuery();
            }
            // item_index.name をどこから作ったかの記録も、失敗して戻すときに一緒に戻す (ExtractItemIndexAsync が書き換える)
            var prevNameSource = ReadMetaValue(_db, ItemNameSourceMetaKey);
            // 途中の印 (InterruptedWork)。*_bak 表を作った後、消し始める前に書き、成功したとき・*_bak 表から戻し終えたときに消す
            // (アプリが途中で終わったら、次の起動で印を見て *_bak 表から戻す)。書けなければ何も消さずに止める
            string markerPath;
            try
            {
                markerPath = InterruptedWork.Write(workDir, new InterruptedWork.Marker
                {
                    Kind = InterruptedWork.KindEquip, Label = "装備データの再抽出", Db = DbPath, NameSource = prevNameSource,
                });
            }
            catch (Exception ex)
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = string.Join(" ", EquipmentTables.Select(t => $"DROP TABLE IF EXISTS {t}_bak;"));
                    cmd.ExecuteNonQuery();
                }
                throw new IOException($"途中の印を書けなかったため、装備データの再抽出を中止しました (データは消していません): {ex.Message}", ex);
            }
            bool succeeded = false, restored = false, failedKept = false;
            var heldTables = new List<string>();   // 戻せず、名前も変えられなかった *_bak 表 (消さない)

            try
            {
                using (var cmd = _db.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM item_index; DELETE FROM ship_ports; DELETE FROM ships;";
                    cmd.ExecuteNonQuery();
                }

                int indexCount = await ExtractItemIndexAsync(p4kPath, now, ct);

                // 全体インデックス構築 (MainWindow の BuildIndexAsync 後) と同じく translations.db から item_index.name_ja を埋める
                // (translations.db は gamedata_cache.db と同じ作業ディレクトリ)。PopulateJapaneseNames が内部で RebuildFts5Index を呼ぶので
                // item_index_fts もここで item_index から作り直される
                StatusChanged?.Invoke("日本語名をマッピング中...");
                PopulateJapaneseNames(Path.Combine(Path.GetDirectoryName(DbPath) ?? "", "translations.db"));
                RebuildItemVariantsAndReport("");

                int shipCount = await ExtractShipsAsync(p4kPath, now, ct);
                UpdateShipEnglishNames();
                await UpdateShipPortSizesAsync(p4kPath, ct);
                await UpdateShipCargoAsync(p4kPath, ct);
                // 追加の品目 (item_index を消して作り直したので入れ直す。船の後に行う)。失敗しても再抽出は続ける (印が消えて起動後に裏で取り直す)
                try
                {
                    DeleteMeta(_db, ExtraItemsMetaKey);
                    await ExtractExtraItemsAsync(p4kPath, now, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { StatusChanged?.Invoke($"[品目の追加] 取り込みに失敗しました (起動後に裏で取り直します): {ex.Message}"); }

                SetMeta("equipment_rebuilt_at", now);
                try { await RebuildMissionReputationAsync(progress, ct); }
                catch (Exception ex) { StatusChanged?.Invoke($"評判報酬の取り込みに失敗しました: {ex.Message}"); }
                sw.Stop();
                StatusChanged?.Invoke($"装備データ再構築完了 ({sw.Elapsed.TotalSeconds:F1}秒) - インデックス: {indexCount}, 船: {shipCount}");
                succeeded = true;
            }
            catch
            {
                // 失敗時はバックアップから表を復元し、item_index_fts も復元後の item_index に合わせる。
                // 戻せなかったら *_bak 表を *_failed_<日時> に名前を変えて残し (次の再抽出で消されないように)、知らせる。どちらでも印は消す
                StatusChanged?.Invoke("装備データの再抽出に失敗したため、再抽出前のデータへ復元中...");
                try
                {
                    RestoreEquipmentBackupTables(_db, prevNameSource);
                    restored = true;
                }
                catch (Exception rex)
                {
                    var kept = KeepFailedEquipmentBackupTables(_db, markerPath, s => StatusChanged?.Invoke(s), heldTables);
                    failedKept = true;
                    StatusChanged?.Invoke($"再抽出前のデータへ戻せませんでした: {rex.Message} (再抽出前の表は {DbPath} の中の {kept} に残しています)");
                }
                throw;
            }
            finally
            {
                if (succeeded || restored || failedKept)
                {
                    // 印を先に消してから *_bak 表を消す (表を消し損ねても、印が無いので次の起動で戻さない)
                    InterruptedWork.Clear(markerPath, InterruptedWork.KindEquip, s => StatusChanged?.Invoke(s));
                    // 戻せず名前も変えられなかった表 (heldTables) は消さない
                    var drop = EquipmentTables.Select(t => t + "_bak").Where(t => !heldTables.Contains(t)).ToList();
                    if (drop.Count > 0)
                    {
                        using var cmd = _db.CreateCommand();
                        cmd.CommandText = string.Join(" ", drop.Select(t => $"DROP TABLE IF EXISTS {t};"));
                        cmd.ExecuteNonQuery();
                    }
                }
            }

            // ここに到達するのは成功時のみ (失敗時は上の catch で再スロー済み)。
            // DELETE した旧 3 表とバックアップ表 (ships.raw_json の複製約 315MB を含む) のページが freelist に残って DB が肥大するため VACUUM で回収する。
            // VACUUM はトランザクション内では実行できないので、トランザクション外 (各抽出メソッドの tx は Commit 済み) で実行する
            ClearCstoneNotFoundMarks();
            // VACUUM の直前に -wal の分 (DB の 1.1 倍) の空きを確かめる。足りなければ VACUUM だけを飛ばし、再抽出の結果はそのまま使う
            if (VacuumSpaceShortage() is { } shortage)
            {
                StatusChanged?.Invoke(shortage);
            }
            else
            {
                // インデックスの構築と同じく、VACUUM が失敗しても再抽出の結果は使えるので、知らせて続ける (下の売り場・価格の取得の頼みも出す)
                StatusChanged?.Invoke("DB を最適化中...");
                try
                {
                    using (var cmd = _db.CreateCommand())
                    {
                        cmd.CommandText = "VACUUM";
                        cmd.ExecuteNonQuery();
                    }
                    // WAL では VACUUM の中身がいったん -wal に入るので、本体に書き戻して -wal を空にする (止まったら少し後に試し直す)
                    SqliteWal.CheckpointOrRetryLater(_db);
                }
                catch (Exception ex) { StatusChanged?.Invoke($"DB の最適化 (VACUUM) に失敗しました: {ex.Message}"); }
            }

            // 新しい品目の売り場・価格を裏で取得してもらう
            ShopPriceFetchRequested?.Invoke(DbPath);
        }
        catch
        {
            ShopPriceFetchResumeRequested?.Invoke(DbPath);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref s_indexBuildsRunning);
            if (relay != null) StatusChanged -= relay;
        }
    }

    // 装備データの再抽出の *_bak 表から戻す (取り消し・失敗したときと、次の起動で途中の印があったとき: InterruptedWork)。
    // 1 つのトランザクションで全部の表を戻し、item_index.name の作り方の記録を戻し、item_index_fts を作り直す。*_bak 表は消さない (呼び出し元が消す)
    internal static void RestoreEquipmentBackupTables(SqliteConnection db, string? prevNameSource)
    {
        foreach (var t in EquipmentTables)
        {
            using var chk = db.CreateCommand();
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
            chk.Parameters.AddWithValue("@n", t + "_bak");
            if (Convert.ToInt64(chk.ExecuteScalar() ?? 0L) == 0) throw new InvalidOperationException($"控えの表 {t}_bak がありません");
        }
        using (var tx = db.BeginTransaction())
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandTimeout = 0;
            cmd.CommandText = string.Join(" ", EquipmentTables.Select(t => $"DELETE FROM {t}; INSERT INTO {t} SELECT * FROM {t}_bak;"));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        if (prevNameSource == null) DeleteMeta(db, ItemNameSourceMetaKey);
        else SetMeta(db, ItemNameSourceMetaKey, prevNameSource);
        RebuildFts5Index(db);
    }

    // 戻せなかった *_bak 表を *_failed_<日時> に名前を変えて残す (次の再抽出の DROP TABLE IF EXISTS *_bak で消されないように)。残した表の名前を返す。
    // 名前を変えられなかった表は *_bak のまま残し、held (渡されたとき) と印のフォルダの held_equip.json に書く (次の再抽出を ThrowIfPending が止める)
    internal static string KeepFailedEquipmentBackupTables(SqliteConnection db, string markerPath, Action<string>? log = null, List<string>? held = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var kept = new List<string>();
        foreach (var t in EquipmentTables)
        {
            try
            {
                using var chk = db.CreateCommand();
                chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
                chk.Parameters.AddWithValue("@n", t + "_bak");
                if (Convert.ToInt64(chk.ExecuteScalar() ?? 0L) == 0) continue;
                using var cmd = db.CreateCommand();
                cmd.CommandText = $"ALTER TABLE {t}_bak RENAME TO {t}_failed_{stamp}";
                cmd.ExecuteNonQuery();
                kept.Add($"{t}_failed_{stamp}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"控えの表 {t}_bak の名前を変えられませんでした ({ex.Message})。元の名前のまま残します (この表がある間は、装備データの再抽出を始めません)");
                kept.Add($"{t}_bak");
                held?.Add($"{t}_bak");
                InterruptedWork.AddHeld(markerPath, InterruptedWork.KindEquip, $"{Path.GetFullPath(db.DataSource)}::{t}_bak", log);
            }
        }
        return string.Join("・", kept);
    }

    internal static string KeepFailedEquipmentBackupTables(string dbPath, string markerPath, Action<string>? log = null)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={dbPath};Pooling=False");
            db.Open();
            return KeepFailedEquipmentBackupTables(db, markerPath, log);
        }
        catch (Exception ex)
        {
            // DB を開けなければ表は何も変わっていない。*_bak のまま残るので、次の再抽出を止める
            log?.Invoke($"控えの表の名前を変えられませんでした: {ex.Message}");
            foreach (var t in EquipmentTables) InterruptedWork.AddHeld(markerPath, InterruptedWork.KindEquip, $"{Path.GetFullPath(dbPath)}::{t}_bak", log);
            return "*_bak";
        }
    }

    // 次の起動で: DB を開いて *_bak 表から戻し、*_bak 表を消す
    internal static void RestoreEquipmentBackupTables(string dbPath, string? prevNameSource)
    {
        using var db = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        db.Open();
        RestoreEquipmentBackupTables(db, prevNameSource);
        using var drop = db.CreateCommand();
        drop.CommandText = string.Join(" ", EquipmentTables.Select(t => $"DROP TABLE IF EXISTS {t}_bak;"));
        drop.ExecuteNonQuery();
    }

    // RebuildEquipmentDataAsync が作り直す 5 表 (バックアップ表 "{name}_bak" の対象)
    private static readonly string[] EquipmentTables = ["item_index", "ship_ports", "ships", "item_ports", "item_port_labels"];

    // ミッションの評判報酬 (派閥・貢献度) を DCB から取り込む。
    // 全件インデックス構築とは独立していて、starbreaker の 3 クエリだけで済む (実測で合計 30 秒程度)。
    // faction_reputation / reputation_reward_amount / mission_reputation を作り直し、mission_reputation の行数を返す
    public async Task<int> RebuildMissionReputationAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var p4kPath = FindDataP4k()
            ?? throw new FileNotFoundException("Data.p4k が見つかりません。設定のゲームパスを確認してください。");

        await EnsureStarBreakerAsync();
        EnsureDb();

        Action<string>? relay = progress != null ? msg => progress.Report(msg) : null;
        if (relay != null) StatusChanged += relay;
        try
        {
            using var dcbScope = await UseExtractedDcbAsync(p4kPath, ct);
            var sw = Stopwatch.StartNew();

            // 派閥 (FactionReputation)
            StatusChanged?.Invoke("派閥データを取得中...");
            var factionJson = await RunDcbQueryRawAsync(p4kPath, "FactionReputation", "*", ct);
            var factions = new List<(string Record, string DisplayKey)>();
            foreach (var block in SplitJsonBlocks(factionJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(recordName)) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;

                    var displayName = GetStr(rv, "displayName");
                    if (string.IsNullOrEmpty(displayName)) continue;
                    var displayKey = displayName.StartsWith('@') ? displayName[1..] : displayName;
                    if (string.IsNullOrEmpty(displayKey)) continue;
                    if (displayKey.Equals("LOC_PLACEHOLDER", StringComparison.OrdinalIgnoreCase)) continue;

                    factions.Add((RecordBaseName(recordName), displayKey));
                }
                catch { }
            }

            // 貢献度の値 (SReputationRewardAmount)
            ct.ThrowIfCancellationRequested();
            StatusChanged?.Invoke("貢献度の値を取得中...");
            var amountJson = await RunDcbQueryRawAsync(p4kPath, "SReputationRewardAmount", "*", ct);
            var amounts = new List<(string Record, int Amount)>();
            foreach (var block in SplitJsonBlocks(amountJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(recordName)) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;
                    if (!rv.TryGetProperty("reputationAmount", out var amt) || amt.ValueKind != JsonValueKind.Number) continue;
                    if (!amt.TryGetInt32(out var amountValue)) continue;

                    amounts.Add((RecordBaseName(recordName), amountValue));
                }
                catch { }
            }

            // ミッションの評判報酬 (MissionBrokerEntry)
            ct.ThrowIfCancellationRequested();
            StatusChanged?.Invoke("契約の評判報酬を取得中...");

            // title は ExtractMissionsAsync が missions.title に入れるのと同じ取り方 (英語ロケールで解決、無ければ @ を外したキー) にする
            var locDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var gamePath = App.Config.GamePath;
            var enIniPath = Path.Combine(gamePath, "data", "Localization", "english", "global.ini");
            if (!File.Exists(enIniPath))
                enIniPath = Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "data", "Localization", "english", "global.ini");
            if (File.Exists(enIniPath))
                locDict = GlobalIniParser.Parse(enIniPath);

            var missionJson = await RunDcbQueryRawAsync(p4kPath, "MissionBrokerEntry", "*", ct);
            var rows = new List<(string MissionRecord, string TitleKey, string Faction, string Amount, string Scope)>();
            var missionRecords = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in SplitJsonBlocks(missionJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(recordName)) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;

                    var titleKey = ResolveLoc(GetStr(rv, "title"), locDict);
                    if (string.IsNullOrEmpty(titleKey)) continue;
                    if (titleKey.Equals("LOC_UNINITIALIZED", StringComparison.OrdinalIgnoreCase)
                        || titleKey.Equals("LOC_PLACEHOLDER", StringComparison.OrdinalIgnoreCase)
                        || titleKey.Equals("loc_testName", StringComparison.OrdinalIgnoreCase)) continue;

                    if (!rv.TryGetProperty("missionResultReputationRewards", out var rewards)
                        || rewards.ValueKind != JsonValueKind.Array) continue;

                    foreach (var reward in rewards.EnumerateArray())
                    {
                        if (reward.ValueKind != JsonValueKind.Object) continue;
                        if (!reward.TryGetProperty("reputationAmounts", out var repAmounts)
                            || repAmounts.ValueKind != JsonValueKind.Array) continue;

                        foreach (var entry in repAmounts.EnumerateArray())
                        {
                            if (entry.ValueKind != JsonValueKind.Object) continue;
                            var faction = RefBaseName(GetStr(entry, "factionReputation"));
                            var amount = RefBaseName(GetStr(entry, "reward"));
                            var scope = RefBaseName(GetStr(entry, "reputationScope"));
                            if (string.IsNullOrEmpty(faction) || string.IsNullOrEmpty(amount) || string.IsNullOrEmpty(scope)) continue;

                            rows.Add((recordName, titleKey, faction, amount, scope));
                            missionRecords.Add(recordName);
                        }
                    }
                }
                catch { }
            }

            // 3 表をまとめて 1 トランザクションで作り直す
            ct.ThrowIfCancellationRequested();
            using (var tx = _db!.BeginTransaction())
            {
                using (var del = _db.CreateCommand())
                {
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM mission_reputation; DELETE FROM faction_reputation; DELETE FROM reputation_reward_amount;";
                    del.ExecuteNonQuery();
                }

                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO faction_reputation(record_name,display_key) VALUES(@rn,@dk)";
                    var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
                    var pDk = cmd.Parameters.Add("@dk", SqliteType.Text);
                    foreach (var (record, displayKey) in factions)
                    {
                        pRn.Value = record;
                        pDk.Value = displayKey;
                        cmd.ExecuteNonQuery();
                    }
                }

                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT OR REPLACE INTO reputation_reward_amount(record_name,amount) VALUES(@rn,@am)";
                    var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
                    var pAm = cmd.Parameters.Add("@am", SqliteType.Integer);
                    foreach (var (record, amount) in amounts)
                    {
                        pRn.Value = record;
                        pAm.Value = amount;
                        cmd.ExecuteNonQuery();
                    }
                }

                using (var cmd = _db.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO mission_reputation(mission_record,title_key,faction_record,amount_record,scope_record) VALUES(@mr,@tk,@fr,@ar,@sr)";
                    var pMr = cmd.Parameters.Add("@mr", SqliteType.Text);
                    var pTk = cmd.Parameters.Add("@tk", SqliteType.Text);
                    var pFr = cmd.Parameters.Add("@fr", SqliteType.Text);
                    var pAr = cmd.Parameters.Add("@ar", SqliteType.Text);
                    var pSr = cmd.Parameters.Add("@sr", SqliteType.Text);
                    foreach (var (missionRecord, titleKey, faction, amount, scope) in rows)
                    {
                        pMr.Value = missionRecord;
                        pTk.Value = titleKey;
                        pFr.Value = faction;
                        pAr.Value = amount;
                        pSr.Value = scope;
                        cmd.ExecuteNonQuery();
                    }
                }

                tx.Commit();
            }

            SetMeta("mission_reputation_rebuilt_at", DateTime.UtcNow.ToString("o"));
            // 取り込み時点の Data.p4k の更新日時を残す。次のパッチで p4k が変わったら取り込み直す判定に使う
            SetMeta("mission_reputation_p4k", File.GetLastWriteTimeUtc(p4kPath).ToString("o"));

            sw.Stop();
            int factionCount = factions.Count, amountCount = amounts.Count, missionCount = missionRecords.Count, rowCount = rows.Count;
            StatusChanged?.Invoke($"評判報酬を取り込みました - 派閥: {factionCount}, 段階: {amountCount}, 契約: {missionCount} (行 {rowCount})");
            return rowCount;
        }
        finally
        {
            if (relay != null) StatusChanged -= relay;
        }
    }

    // "FactionReputation.FactionReputation_Unlawful_BitZeros" -> "factionreputation_unlawful_bitzeros"
    private static string RecordBaseName(string recordName)
    {
        var idx = recordName.LastIndexOf('.');
        var name = idx >= 0 ? recordName[(idx + 1)..] : recordName;
        return name.ToLowerInvariant();
    }

    // "file://.../reputationrewardamount_positive_s.json" -> "reputationrewardamount_positive_s"
    private static string RefBaseName(string reference)
    {
        if (string.IsNullOrEmpty(reference)) return "";
        var idx = reference.LastIndexOf('/');
        var name = idx >= 0 ? reference[(idx + 1)..] : reference;
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^".json".Length];
        return name.ToLowerInvariant();
    }

    public bool HasStructuredData()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM missions";
        try { return (long)(cmd.ExecuteScalar() ?? 0L) > 0; }
        catch { return false; }
    }

    private string? GetMeta(string key)
    {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT value FROM gamedata_meta WHERE key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        return cmd.ExecuteScalar() as string;
    }

    // 種類で拾う武器・装備 (AttachDef.Type)。個人武器・道具 (マルチツール・医療ガン等)・武器の付属品 (マガジン・照準器等)・手榴弾・設置型の爆発物・
    // 医療用具 (メディペン等。FPS_Consumable)・船の武器・ミサイル・ミサイルラック・爆弾と、アーマー・服 (Char_Armor_* / Char_Clothing_*。
    // バックパック・アンダースーツを含む)。決め打ちの接頭辞 (ItemPrefixes 等) に無い製造元の品目もこれで取り込む
    internal static readonly HashSet<string> TypedDiscoveryTypes = new(StringComparer.Ordinal)
    {
        "WeaponPersonal", "Gadget", "WeaponAttachment", "Grenade", "FPS_Deployable", "FPS_Consumable",
        "WeaponGun", "Missile", "MissileLauncher", "Bomb", "BombLauncher",
    };

    internal static bool IsTypedDiscoveryType(string type) =>
        TypedDiscoveryTypes.Contains(type)
        || type.StartsWith("Char_Armor_", StringComparison.Ordinal)
        || type.StartsWith("Char_Clothing_", StringComparison.Ordinal);

    // 全エンティティの record 名 → 種類 (AttachDef.Type)。dcb query の値の取り出し (1 行 "record名<TAB>種類") で、全件でも数秒で済む。
    // 取れなければ空 (従来の接頭辞だけで取り込む)
    private async Task<Dictionary<string, string>> QueryEntityTypesAsync(string p4kPath, CancellationToken ct)
    {
        try
        {
            StatusChanged?.Invoke("エンティティの種類の一覧を取得中...");
            var text = await RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition.Components[SAttachableComponentParams].AttachDef.Type", "*", ct);
            return ParseEntityTypes(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"エンティティの種類の一覧を取得できませんでした (従来の接頭辞だけで取り込みます): {ex.Message}");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    internal static Dictionary<string, string> ParseEntityTypes(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var rec = line[..tab].Trim();
            var type = line[(tab + 1)..].Trim();
            if (rec.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) && type.Length > 0) map[rec] = type;
        }
        return map;
    }

    // 1 回の dcb query の filter に並べる record 名の文字数・件数の上限 (コマンドラインの長さ 32,767 文字の制限と、1 回の出力の大きさを抑えるため)
    private const int TypedFilterMaxChars = 20000;
    private const int TypedFilterMaxRecords = 400;

    // 種類の一覧のうち IsTypedDiscoveryType に当たり、coveredPrefixes ("{接頭辞}_*" の filter。大文字小文字を区別) で取れない record を、
    // record 名そのものを並べた filter "EntityClassDefinition.{名前1,名前2,…}" にまとめる (starbreaker の --filter は {a,b} の選択に対応)。
    // 1 回の filter は TypedFilterMaxChars 文字・TypedFilterMaxRecords 件まで。名前に glob の記号 ({}[]*?,\) を含むものは 1 件ずつの filter にする。
    // 戻り値の Records は取り込む record (EntityClassDefinition. 付き)
    internal static List<(string Filter, HashSet<string> Records)> BuildTypedExtraFilters(Dictionary<string, string> typeMap, IEnumerable<string> coveredPrefixes)
    {
        const string head = "EntityClassDefinition.";
        var covered = coveredPrefixes.Select(p => p + "_").ToList();
        var names = new List<string>();
        foreach (var (rec, type) in typeMap)
        {
            if (!IsTypedDiscoveryType(type)) continue;
            var shortName = rec[head.Length..];
            if (covered.Any(c => shortName.StartsWith(c, StringComparison.Ordinal))) continue;
            names.Add(shortName);
        }
        names.Sort(StringComparer.Ordinal);
        return BuildRecordFilters(names);
    }

    // record の短い名前 (EntityClassDefinition. を除いたもの) を並べた filter にまとめる (BuildTypedExtraFilters と追加の品目 ExtractExtraItemsAsync で共用)。
    // 1 回の filter は TypedFilterMaxChars 文字・TypedFilterMaxRecords 件まで。名前に glob の記号を含むものは 1 件ずつの filter にする
    internal static List<(string Filter, HashSet<string> Records)> BuildRecordFilters(IEnumerable<string> names)
    {
        const string head = "EntityClassDefinition.";
        var result = new List<(string, HashSet<string>)>();
        var batch = new List<string>();
        int batchChars = 0;
        void Flush()
        {
            if (batch.Count == 0) return;
            var filter = batch.Count == 1 ? head + batch[0] : head + "{" + string.Join(",", batch) + "}";
            result.Add((filter, new HashSet<string>(batch.Select(n => head + n), StringComparer.Ordinal)));
            batch.Clear();
            batchChars = 0;
        }
        foreach (var n in names)
        {
            if (n.IndexOfAny(['{', '}', '[', ']', '*', '?', ',', '\\']) >= 0)
            {
                result.Add((head + n, new HashSet<string>(StringComparer.Ordinal) { head + n }));
                continue;
            }
            if (batch.Count >= TypedFilterMaxRecords || batchChars + n.Length + 1 > TypedFilterMaxChars) Flush();
            batch.Add(n);
            batchChars += n.Length + 1;
        }
        Flush();
        return result;
    }

    private static readonly string[] VehicleManufacturerPrefixes =
        ["AEGS", "ANVL", "ARGO", "BANU", "CNOU", "CRUS", "DRAK", "GAMA", "GATA", "GRIN", "KRIG", "MISC", "MRAI", "ORIG", "RSI", "TMBL", "VNCL", "XIAN", "ESPR"];

    // starbreaker の --filter は大文字小文字を区別するため、船舶武器・ジンバル系は大文字メーカーコードも列挙する
    // (BEHR_LaserCannon_S3 / Mount_Gimbal_S3 / WeaponMount_* 等)
    private static readonly string[] ItemPrefixes =
        ["behr", "klwe", "ksar", "lbco", "gmni", "hdso", "jofl", "grin", "apar", "crus", "aegs", "anvl", "argo", "cnou", "drak", "krig", "misc", "mrai", "orig", "rsi", "tmbl", "xian", "espr",
         "BEHR", "KLWE", "GMNI", "HDSO", "JOFL", "APAR", "LBCO", "KSAR", "GATS", "AMRS", "JOKR", "Mount_Gimbal", "WeaponMount",
         "POWR", "SHLD", "COOL", "QDRV", "JDRV", "MISL", "MRCK", "RADR", "COMP", "INTK", "HTNK", "QTNK", "ARMR", "LFSP", "RELAY",
         "powr", "shld", "cool", "qdrv", "jdrv", "misl", "mrck", "radr", "comp", "intk", "htnk", "qtnk", "armr", "lfsp", "relay",
         "GODI", "WETK", "TYDT", "GRNP", "FSKI", "godi", "wetk", "tydt", "grnp", "fski",
         // ship_ports.equipped_item に出るが item_index で未解決だったプレフィックス (武器ラック / メーカー / マウント / 爆弾系)
         "Weapon_Rack", "MXOX", "KRON", "GLSN", "HRST", "UMNT", "TOAG", "KBAR", "ASAD", "RPOD", "GMNT", "BMBRCK", "BOMB"];

    // typeMap は QueryEntityTypesAsync の結果 (null なら中で取る)。決め打ちの接頭辞で取れない武器類を、種類で拾って追加する
    // item_index.name を作る辞書 (ExtractItemIndexAsync と、追加で取り込む品目 ExtractExtraItemsAsync で共用)。
    // english の ini → (english の ini が無い環境では) 今の訳から作った値 (Merged) → japanese の ini の順に引く
    private sealed class ItemNameDicts
    {
        public Dictionary<string, string> En = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Ja = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string>? Merged;
        public string Source => En.Count > 0 ? ItemNameSourceEnglish : Merged != null ? ItemNameSourceTranslations : ItemNameSourceJapaneseIni;

        // name は AttachDef.Localization.Name ("@キー" か文字)。返り値: 表示名 (解決できなければ name のまま)、日本語名 (japanese の ini。無ければ "")、
        // キー ("@" を除いたもの。"@" で始まらなければ null)、名前を解決できたか
        public (string Name, string NameJa, string? Key, bool Resolved) Resolve(string name)
        {
            if (!name.StartsWith("@") || name.Length <= 1) return (name, "", null, false);
            var locKey = name[1..];
            var resolved = false;
            if (En.TryGetValue(locKey, out var enName) && !enName.StartsWith("!"))
            {
                name = enName;
                resolved = true;
            }
            else if (Merged != null && IniMerger.TryGetIniValue(Merged, locKey, out var mergedName))
            {
                name = mergedName;
                resolved = true;
            }
            else if (Ja.TryGetValue(locKey, out var jaName) && !jaName.StartsWith("!"))
            {
                name = jaName;
                resolved = true;
            }
            var nameJa = Ja.TryGetValue(locKey, out var jaVal) && !jaVal.StartsWith("!") ? jaVal : "";
            return (name, nameJa, locKey, resolved);
        }
    }

    // statusPrefix は進み具合の文の頭 (例 "[1/4] ")
    private async Task<ItemNameDicts> LoadItemNameDictsAsync(string statusPrefix, CancellationToken ct)
    {
        var gamePath = App.Config.GamePath;
        var d = new ItemNameDicts();
        var enIniPath = GameLocalizationIniPath(gamePath, "english");
        var jaIniPath = GameLocalizationIniPath(gamePath, "japanese_(japan)");
        if (File.Exists(enIniPath))
        {
            StatusChanged?.Invoke($"{statusPrefix}ローカライズデータ（英語）を読み込み中...");
            d.En = GlobalIniParser.Parse(enIniPath);
        }
        if (File.Exists(jaIniPath))
        {
            StatusChanged?.Invoke($"{statusPrefix}ローカライズデータ（日本語）を読み込み中...");
            d.Ja = GlobalIniParser.Parse(jaIniPath);
        }
        // english の ini が無い (ふつうの環境) ときは、名前を今の訳から作る: ［日本語化］の［実行］がゲーム用 ini に書き出す値
        // (IniMerger.MergeForItemNames。RefreshItemNamesFromTranslations と同じ)。作れない (抽出済みの ini が無い等) ときは今までどおり japanese の ini
        if (d.En.Count == 0)
        {
            StatusChanged?.Invoke($"{statusPrefix}今の訳から品目名を作成中...");
            var workDir = Path.GetDirectoryName(DbPath) ?? "";
            try { d.Merged = await Task.Run(() => IniMerger.MergeForItemNames(workDir), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { StatusChanged?.Invoke($"{statusPrefix}今の訳から品目名を作れませんでした (japanese の ini の値を使います): {ex.Message}"); }
        }
        return d;
    }

    private async Task<int> ExtractItemIndexAsync(string p4kPath, string now, CancellationToken ct, Dictionary<string, string>? typeMap = null)
    {
        var names = await LoadItemNameDictsAsync("[1/4] ", ct);
        var nameSource = names.Source;
        int resolved = 0;

        int count = 0;
        using var tx = _db!.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO item_index(uuid, record_name, name, name_ja, item_type, sub_type, manufacturer, size, extracted_at, name_key) VALUES(@uuid, @rn, @nm, @ja, @it, @st, @mf, @sz, @ea, @nk)";
        var pUuid = cmd.Parameters.Add("@uuid", SqliteType.Text);
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pNm = cmd.Parameters.Add("@nm", SqliteType.Text);
        var pJa = cmd.Parameters.Add("@ja", SqliteType.Text);
        var pIt = cmd.Parameters.Add("@it", SqliteType.Text);
        var pSt = cmd.Parameters.Add("@st", SqliteType.Text);
        var pMf = cmd.Parameters.Add("@mf", SqliteType.Text);
        var pSz = cmd.Parameters.Add("@sz", SqliteType.Integer);
        var pEa = cmd.Parameters.Add("@ea", SqliteType.Text);
        var pNk = cmd.Parameters.Add("@nk", SqliteType.Text);

        // 品目自身の子ポートの定義 (item_ports) も同じ出力から作り直す
        using (var del = _db.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM item_ports; DELETE FROM item_port_labels;";
            del.ExecuteNonQuery();
        }
        int portRows = 0, labelRows = 0;

        // --filter は大文字小文字を区別するので、重複排除も大小区別 (Ordinal) で行う ("BEHR_*" と "behr_*" は別クエリ)
        var basePrefixes = VehicleManufacturerPrefixes.Concat(ItemPrefixes).Distinct(StringComparer.Ordinal).ToList();
        var allPrefixes = basePrefixes.Select(p => (Filter: $"EntityClassDefinition.{p}_*", Only: (HashSet<string>?)null)).ToList();
        // 上の接頭辞で取れない武器類 (新しい製造元など) を種類の一覧から拾って追加する (その record だけを入れる)
        typeMap ??= await QueryEntityTypesAsync(p4kPath, ct);
        allPrefixes.AddRange(BuildTypedExtraFilters(typeMap, basePrefixes).Select(x => (x.Filter, (HashSet<string>?)x.Records)));

        // 問い合わせは先に同時に 4 つまで動かしておく (DcbPrefetcher)。DB への書き込みは下で今までどおりこの順番に行う
        using var prefetch = new DcbPrefetcher(allPrefixes.Count,
            (k, token) => RunDcbQueryAsync(p4kPath, "EntityClassDefinition", allPrefixes[k].Filter, token), ct);
        for (int i = 0; i < allPrefixes.Count; i++)
        {
            var (filter, only) = allPrefixes[i];
            var prefix = filter.Split('.')[1].TrimEnd('*').TrimEnd('_');
            StatusChanged?.Invoke($"[1/{4}] インデックス構築 ({prefix}, {i + 1}/{allPrefixes.Count})");

            string rawJson;
            try { rawJson = await prefetch.GetAsync(i); }
            catch { continue; }
            if (string.IsNullOrEmpty(rawJson)) continue;
            // 種類で拾った追加分 (only) は、下の item_index と同じく only の record だけを入れる
            portRows += InsertItemPortsFromJson(_db, tx, rawJson, only);
            labelRows += InsertItemPortLabelsFromJson(_db, tx, rawJson, only);

            foreach (var block in SplitJsonBlocks(rawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    var uuid = root.TryGetProperty("_RecordId_", out var rid) ? rid.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(recordName) || string.IsNullOrEmpty(uuid)) continue;
                    if (only != null && !only.Contains(recordName)) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)) continue;

                    string name = "", itemType = "", subType = "", manufacturer = "";
                    int size = 0;
                    if (components.ValueKind != JsonValueKind.Array) continue;
                    foreach (var comp in components.EnumerateArray())
                    {
                        // Components には null の要素が入っている record がある (例 grin_multitool_01_cutter)。読まずに飛ばす
                        if (comp.ValueKind != JsonValueKind.Object) continue;
                        var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                        if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                        {
                            itemType = ad.TryGetProperty("Type", out var it2) ? it2.GetString() ?? "" : "";
                            subType = ad.TryGetProperty("SubType", out var st2) ? st2.GetString() ?? "" : "";
                            manufacturer = ad.TryGetProperty("Manufacturer", out var mf2) ? mf2.GetString() ?? "" : "";
                            size = ad.TryGetProperty("Size", out var sz2) && sz2.ValueKind == JsonValueKind.Number && sz2.TryGetInt32(out var szv) ? szv : 0;
                            if (ad.TryGetProperty("Localization", out var loc))
                                name = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(itemType) || itemType == "UNDEFINED") continue;
                    // AttachDef.Size が無い (0) ときは record 名の "_S{n}" (末尾または "_" の前) から補完
                    if (size <= 0) size = InferSizeFromEntityName(recordName);

                    // 名前のローカライズキー (nameKey。"@" を除いたもの)。PopulateJapaneseNames が translations.db のキーと照合する
                    var (resolvedName, nameJa, nameKey, ok) = names.Resolve(name);
                    name = resolvedName;
                    if (ok) resolved++;

                    pUuid.Value = uuid; pRn.Value = recordName; pNm.Value = name;
                    pJa.Value = string.IsNullOrEmpty(nameJa) ? DBNull.Value : nameJa;
                    pIt.Value = itemType; pSt.Value = subType; pMf.Value = manufacturer; pSz.Value = size; pEa.Value = now;
                    pNk.Value = nameKey is null ? DBNull.Value : nameKey;
                    cmd.ExecuteNonQuery();
                    count++;
                }
                catch { }
            }
        }
        tx.Commit();
        SetMeta(_db, ItemNameSourceMetaKey, nameSource);
        StatusChanged?.Invoke($"[1/4] インデックス構築完了: {count} 件 (名前解決: {resolved} 件、子ポートの定義: {portRows} 行、枠の表示名: {labelRows} 行)");
        return count;
    }

    private async Task<int> ExtractShipsAsync(string p4kPath, string now, CancellationToken ct)
    {
        int count = 0;
        using var tx = _db!.BeginTransaction();
        using var shipCmd = _db.CreateCommand();
        shipCmd.CommandText = "INSERT OR REPLACE INTO ships(record_name,name,manufacturer,career,role,crew_size,size,raw_json,extracted_at) VALUES(@rn,@nm,@mf,@ca,@ro,@cr,@sz,@rj,@ea)";
        var pRn = shipCmd.Parameters.Add("@rn", SqliteType.Text);
        var pNm = shipCmd.Parameters.Add("@nm", SqliteType.Text);
        var pMf = shipCmd.Parameters.Add("@mf", SqliteType.Text);
        var pCa = shipCmd.Parameters.Add("@ca", SqliteType.Text);
        var pRo = shipCmd.Parameters.Add("@ro", SqliteType.Text);
        var pCr = shipCmd.Parameters.Add("@cr", SqliteType.Integer);
        var pSz = shipCmd.Parameters.Add("@sz", SqliteType.Integer);
        var pRj = shipCmd.Parameters.Add("@rj", SqliteType.Text);
        var pEa = shipCmd.Parameters.Add("@ea", SqliteType.Text);

        using var portCmd = _db.CreateCommand();
        portCmd.CommandText = "INSERT INTO ship_ports(ship_record_name,port_name,item_type,size,equipped_item) VALUES(@srn,@pn,@it,@sz,@ei)";
        var ppSrn = portCmd.Parameters.Add("@srn", SqliteType.Text);
        var ppPn = portCmd.Parameters.Add("@pn", SqliteType.Text);
        var ppIt = portCmd.Parameters.Add("@it", SqliteType.Text);
        var ppSz = portCmd.Parameters.Add("@sz", SqliteType.Integer);
        var ppEi = portCmd.Parameters.Add("@ei", SqliteType.Text);

        // 名前で種類が決まらない最上位ポートを、付いている物の種類で装備と判定するための表 (呼び出し元で item_index を先に作っている)
        var itemTypeByRecord = LoadItemTypeByRecord(_db);

        for (int pi = 0; pi < VehicleManufacturerPrefixes.Length; pi++)
        {
            var prefix = VehicleManufacturerPrefixes[pi];
            StatusChanged?.Invoke($"[1/6] 船・車両データ ({prefix}, {pi + 1}/{VehicleManufacturerPrefixes.Length})");
            var filter = $"EntityClassDefinition.{prefix}_*";
            var rawJson = await RunDcbQueryAsync(p4kPath, "EntityClassDefinition", filter, ct);
            if (string.IsNullOrEmpty(rawJson)) continue;

            count += ParseAndInsertShips(rawJson, now, shipCmd, pRn, pNm, pMf, pCa, pRo, pCr, pSz, pRj, pEa,
                portCmd, ppSrn, ppPn, ppIt, ppSz, ppEi, itemTypeByRecord);
        }

        tx.Commit();
        return count;
    }

    // 前からある名前の規則 (この規則と InferLegacyPortType で拾う行は、種類に関係なく今までどおり保存する)
    private static readonly HashSet<string> LegacyEquipmentPortPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hardpoint_gun", "hardpoint_weapon", "hardpoint_turret",
        "hardpoint_shield", "hardpoint_power_plant", "hardpoint_quantum_drive",
        "hardpoint_cooler", "Hardpoint_cooler",
        "hardpoint_missilerack", "hardpoint_missile",
        "hardpoint_radar", "Hardpoint_Avionics", "Hardpoint_Life_Support",
        "hardpoint_countermeasure",
    };

    // 名前の規則 = 前からある規則 + 綴り違い (hardpoint_powerplant = Vanguard 等、hardpoint_lifesupport、hardpoint_cm_launcher / hardpoint_CML_* = 対抗手段の発射機)
    private static readonly HashSet<string> EquipmentPortPrefixes = new(
        LegacyEquipmentPortPrefixes.Concat(["hardpoint_powerplant", "hardpoint_lifesupport", "hardpoint_cm_launcher", "hardpoint_cml"]),
        StringComparer.OrdinalIgnoreCase);

    private static string InferPortType(string portName)
    {
        var legacy = InferLegacyPortType(portName);
        if (legacy.Length > 0) return legacy;
        var lower = portName.ToLowerInvariant();
        // 綴り違い (前からある語に当たらなかったものだけ。今まで種類が決まっていた名前の結果は変わらない)
        if (lower.Contains("powerplant")) return "PowerPlant";
        if (lower.Contains("lifesupport")) return "LifeSupport";
        if (lower.Contains("cm_launcher") || lower.StartsWith("hardpoint_cml", StringComparison.Ordinal)) return "CounterMeasure";
        // ミサイルラックの綴り違い (hardpoint_missile_left / hardpoint_missiles_wing_left / hardpoint_missilelauncher_* 等)
        if (lower.Contains("missile")) return "MissileLauncher";
        return "";
    }

    // 前からある名前からの種類の推定
    private static string InferLegacyPortType(string portName)
    {
        var lower = portName.ToLowerInvariant();
        if (lower.Contains("gun") || (lower.Contains("weapon") && !lower.Contains("rack"))) return "WeaponGun";
        if (lower.Contains("turret")) return "Turret";
        if (lower.Contains("shield")) return "Shield";
        if (lower.Contains("power_plant")) return "PowerPlant";
        if (lower.Contains("quantum_drive")) return "QuantumDrive";
        if (lower.Contains("cooler")) return "Cooler";
        if (lower.Contains("missilerack") || lower.Contains("missile_rack")) return "MissileLauncher";
        if (lower.Contains("radar")) return "Radar";
        if (lower.Contains("avionics")) return "Avionics";
        if (lower.Contains("life_support")) return "LifeSupport";
        if (lower.Contains("countermeasure")) return "CounterMeasure";
        return "";
    }

    // 名前で種類が決まらない最上位ポートを、付いている物の item_index の種類 (item_type) で装備と判定するときの装備の種類。
    // LifeSupportGenerator → LifeSupport、WeaponDefensive → CounterMeasure、TurretBase → Turret
    // (名前で決めたときの種類にそろえる。hardpoint_turret_* に付いた TurretBase は Turret で入っている)。それ以外はそのまま
    private static readonly HashSet<string> ShipEquipmentItemTypes = new(StringComparer.Ordinal)
    {
        "WeaponGun", "Turret", "TurretBase", "MissileLauncher", "Missile", "PowerPlant", "Cooler", "Shield", "QuantumDrive",
        "JumpDrive", "Radar", "LifeSupportGenerator", "WeaponDefensive", "WeaponMining", "TractorBeam", "SalvageHead",
        "UtilityTurret", "WeaponMount", "BombLauncher", "Bomb", "EMP", "QuantumInterdictionGenerator", "ToolArm",
        "MiningModifier", "SalvageModifier",
    };

    // 付いている物の種類 (item_index.item_type) が装備なら ship_ports の種類、装備でない・item_index に無い・空なら null。
    // HangarService.IsLoadoutCandidate も、所持コンポーネントの種類 (items.item_type) と口の種類を比べるのに使う
    internal static string? ShipEquipmentPortType(string? itemType)
    {
        if (string.IsNullOrEmpty(itemType) || !ShipEquipmentItemTypes.Contains(itemType)) return null;
        return itemType switch
        {
            "LifeSupportGenerator" => "LifeSupport",
            "WeaponDefensive" => "CounterMeasure",
            "TurretBase" => "Turret",
            _ => itemType,
        };
    }

    // item_index に無い物 (地上車両のミサイルラック GMRCK_* 等) の種類を、直下に付いている装備から決める。
    // Missile → MissileLauncher、Bomb → BombLauncher、WeaponGun → Turret。直下に該当が無い、または食い違うときは null
    private static string? PortTypeFromChildren(JsonElement entry, IReadOnlyDictionary<string, string> itemTypeByRecord)
    {
        if (!entry.TryGetProperty("loadout", out var sub) || sub.ValueKind != JsonValueKind.Object
            || !sub.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
        string? result = null;
        foreach (var child in entries.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object) continue;
            var t = LookupItemType(itemTypeByRecord, ResolveLoadoutEntityName(child)) switch
            {
                "Missile" => "MissileLauncher",
                "Bomb" => "BombLauncher",
                "WeaponGun" => "Turret",
                _ => null,
            };
            if (t == null) continue;
            if (result != null && result != t) return null;
            result = t;
        }
        return result;
    }

    // item_index の record_name ("EntityClassDefinition." を除く) → item_type。大文字小文字は区別しない
    private static Dictionary<string, string> LoadItemTypeByRecord(SqliteConnection db)
    {
        const string prefix = "EntityClassDefinition.";
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT record_name, item_type FROM item_index WHERE item_type IS NOT NULL AND item_type <> ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(0)) continue;
            var record = reader.GetString(0);
            if (record.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) record = record[prefix.Length..];
            map.TryAdd(record, reader.GetString(1));
        }
        return map;
    }

    // ロードアウトのエンティティ名 (basename) の item_type。そのまま → "_SCItem" の有無を反転したもの の順に引く
    // (EquipmentService.RecordCandidates と同じ照合)。引けなければ null
    private static string? LookupItemType(IReadOnlyDictionary<string, string> itemTypeByRecord, string entityName)
    {
        if (string.IsNullOrEmpty(entityName)) return null;
        if (itemTypeByRecord.TryGetValue(entityName, out var t)) return t;
        const string suffix = "_SCItem";
        var toggled = entityName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? entityName[..^suffix.Length] : entityName + suffix;
        return itemTypeByRecord.TryGetValue(toggled, out t) ? t : null;
    }

    // エンティティ名の "_S{n}" (末尾または "_" の前。大文字小文字無視) から Size を推定する。
    // 例: POWR_AEGS_S01_Regulus_SCItem → 1、klwe_laserrepeater_s3 → 3、mount_gimbal_s3 → 3。表記が無ければ 0
    private static readonly System.Text.RegularExpressions.Regex EntitySizeRegex =
        new(@"_S(\d{1,2})(?=_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int InferSizeFromEntityName(string entityName)
    {
        if (string.IsNullOrEmpty(entityName)) return 0;
        var match = EntitySizeRegex.Match(entityName);
        return match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 0;
    }

    // ロードアウト entry のエンティティ名。entityClassName が空なら entityClassReference
    // ("file://./../../libs/foundry/records/entities/scitem/.../klwe_laserrepeater_s3.json") の basename (拡張子 .json 除く) を採用する。
    // DB には "EntityClassDefinition." を付けず basename のまま保存する (既存データと同じ形式)
    private static string ResolveLoadoutEntityName(JsonElement entry)
    {
        var entityName = entry.TryGetProperty("entityClassName", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(entityName)) return entityName;
        var reference = entry.TryGetProperty("entityClassReference", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(reference)) return "";
        var slash = reference.LastIndexOf('/');
        var basename = slash >= 0 ? reference[(slash + 1)..] : reference;
        if (basename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) basename = basename[..^5];
        return basename;
    }

    // 装備エンティティ名による item_type の上書き。ジンバル/マウント → WeaponMount、ミサイル → Missile、
    // ジャンプドライブ (jdrv_*) → JumpDrive、武器ラック (weapon_rack_*) → WeaponRack、タレット → Turret。
    // 該当しなければ inheritedType (最上位祖先ポート名から推定した型) をそのまま返す
    private static string OverridePortTypeByEntity(string entityName, string inheritedType)
    {
        if (string.IsNullOrEmpty(entityName)) return inheritedType;
        var lower = entityName.ToLowerInvariant();
        if (lower.StartsWith("mount_gimbal") || lower.StartsWith("weaponmount")) return "WeaponMount";
        if (lower.StartsWith("missile") || lower.StartsWith("misl_")) return "Missile";
        if (lower.StartsWith("jdrv_")) return "JumpDrive";
        if (lower.StartsWith("weapon_rack_")) return "WeaponRack";
        if (lower.Contains("_turret")) return "Turret";
        return inheritedType;
    }

    // 画面・HUD・コントローラ・座席アクセスは装備ではないので ship_ports に保存しない (最上位ポート・子行の両方に適用)。
    // エンティティ名 (basename、小文字化) が screen / display / _hud / controller_weapon / radar_display / seataccess / _access_ のいずれかを含むもの
    // (Carrack の anvl_carrack_seataccess_turret_* = 座席アクセス 等)、
    // またはエンティティ名が空で port 名が Screen_* / Display_* のもの (Display_HUD 等)
    private static readonly string[] NonEquipmentEntityFragments = ["screen", "display", "_hud", "controller_weapon", "radar_display", "seataccess", "_access_", "_interior"];

    private static bool IsNonEquipmentEntry(string portName, string entityName)
    {
        if (!string.IsNullOrEmpty(entityName))
        {
            var lower = entityName.ToLowerInvariant();
            return NonEquipmentEntityFragments.Any(f => lower.Contains(f));
        }
        return portName.StartsWith("Screen_", StringComparison.OrdinalIgnoreCase)
            || portName.StartsWith("Display_", StringComparison.OrdinalIgnoreCase)
            // 武器のリジェネプール (hardpoint_weapon_regen_pool / _turrets) は装備を挿すポートではない (ゲームデータ上も常に空)
            || portName.Contains("regen_pool", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseAndInsertShips(string rawJson, string now,
        SqliteCommand shipCmd, SqliteParameter pRn, SqliteParameter pNm, SqliteParameter pMf,
        SqliteParameter pCa, SqliteParameter pRo, SqliteParameter pCr, SqliteParameter pSz,
        SqliteParameter pRj, SqliteParameter pEa,
        SqliteCommand portCmd, SqliteParameter ppSrn, SqliteParameter ppPn, SqliteParameter ppIt, SqliteParameter ppSz, SqliteParameter ppEi,
        IReadOnlyDictionary<string, string> itemTypeByRecord)
    {
        int count = 0;
        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(recordName)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)) continue;

                string name = "", manufacturer = "", career = "", role = "";
                int crewSize = 0, size = 0;
                bool hasVehicleComponent = false;
                if (components.ValueKind != JsonValueKind.Array) continue;

                foreach (var comp in components.EnumerateArray())
                {
                    // Components には null やオブジェクトでない要素が入っている record がある。読まずに飛ばす
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                    {
                        size = ad.TryGetProperty("Size", out var sz) ? sz.GetInt32() : 0;
                        if (ad.TryGetProperty("Localization", out var loc))
                            name = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                        manufacturer = ad.TryGetProperty("Manufacturer", out var mf) ? mf.GetString() ?? "" : "";
                    }
                    if (type == "VehicleComponentParams")
                    {
                        hasVehicleComponent = true;
                        career = comp.TryGetProperty("vehicleCareer", out var c) ? c.GetString() ?? "" : "";
                        role = comp.TryGetProperty("vehicleRole", out var r) ? r.GetString() ?? "" : "";
                        crewSize = comp.TryGetProperty("crewSize", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt32() : 0;
                    }
                }

                if (!hasVehicleComponent) continue;

                pRn.Value = recordName; pNm.Value = name; pMf.Value = manufacturer;
                pCa.Value = career; pRo.Value = role; pCr.Value = crewSize;
                pSz.Value = size; pRj.Value = block; pEa.Value = now;
                shipCmd.ExecuteNonQuery();

                var loadoutEntries = new List<(string portName, string itemType, string entityName)>();
                CollectLoadoutEntries(components, itemTypeByRecord, loadoutEntries);

                foreach (var (portName, portType, entityName) in loadoutEntries)
                {
                    if (string.IsNullOrEmpty(portType)) continue;
                    var portSize = InferSizeFromEntityName(entityName);
                    ppSrn.Value = recordName; ppPn.Value = portName; ppIt.Value = portType; ppSz.Value = portSize;
                    ppEi.Value = string.IsNullOrEmpty(entityName) ? DBNull.Value : entityName;
                    portCmd.ExecuteNonQuery();
                }
                count++;
            }
            catch { }
        }
        return count;
    }

    private static void CollectLoadoutEntries(JsonElement components, IReadOnlyDictionary<string, string> itemTypeByRecord,
        List<(string portName, string itemType, string entityName)> results)
    {
        foreach (var comp in components.EnumerateArray())
        {
            if (!comp.TryGetProperty("_Type_", out var t)) continue;
            if (t.GetString() != "SEntityComponentDefaultLoadoutParams") continue;
            if (!comp.TryGetProperty("loadout", out var loadout)) continue;
            CollectEntriesFromLoadout(loadout, "", "", null, false, itemTypeByRecord, results);
            break;
        }
    }

    // デフォルトロードアウトの entries を再帰的に集める。
    // - 最上位ポートは EquipmentPortPrefixes に一致するものだけ対象。祖先が対象なら子孫 (ジンバル配下の実武器、ミサイルラック配下のミサイル等) も全て保存する。
    // - 子孫行の port_name は "{親port}/{子port}" (例 hardpoint_weapon_left_nose/hardpoint_class_2)。
    // - item_type は最上位祖先ポート名から InferPortType で決め、子孫に継承する。ただしエンティティ名が
    //   mount_gimbal/weaponmount → WeaponMount、missile/misl_ → Missile、jdrv_ → JumpDrive、weapon_rack_ → WeaponRack、
    //   "_turret" を含む → Turret に上書き (OverridePortTypeByEntity)。
    // - 画面・HUD・コントローラ・座席アクセス (IsNonEquipmentEntry) は装備ではないので最上位・子行とも保存しない。
    // - 名前が EquipmentPortPrefixes に一致しない、または一致しても InferPortType で種類が決まらない最上位ポートは、
    //   付いている物の item_index の種類が装備 (ShipEquipmentItemTypes) なら対象にし、その種類 (ShipEquipmentPortType) を子孫に継承する。
    //   付いている物がジンバル (OverridePortTypeByEntity が WeaponMount) なら、下の武器の行は hardpoint_weapon_* の下と同じ WeaponGun にする。
    // - 前からある規則 (LegacyEquipmentPortPrefixes + InferLegacyPortType) で保存していた行は、種類に関係なく今までどおり保存する。
    //   それ以外の行 (綴り違い・付いている物の種類で増えた行) は、付いている物の item_index の種類が装備でない (Misc・Room・AttachedPart・Door 等) なら
    //   保存しない (前からある規則の最上位の下なら、前からの行を残すため配下は辿る。それ以外は配下も辿らない)。空のポートは保存する。
    // - 名前が一致しない最上位ポートを付いている物の種類で拾ったときは、その下で item_index に無い物 (GMRCK_* 等) は
    //   直下の装備から種類を決めて (PortTypeFromChildren) 保存し配下も辿る。決まらなければ保存せず配下も辿らない。
    //   名前の規則で拾った最上位の下の item_index に無い物は、名前から決めた種類で保存する。
    // parentPath = 親までの port_name (最上位は "")、ancestorType = 最上位祖先から決めた型 (最上位は "")、
    // legacyType = 前からある規則で最上位祖先ポート名から推定した型 (前からある規則に当たらない最上位の下は null)、
    // equipmentOnly = 名前が一致せず付いている物の種類で拾った最上位の下か (最上位は false)
    private static void CollectEntriesFromLoadout(JsonElement loadout, string parentPath, string ancestorType, string? ancestorLegacyType,
        bool equipmentOnly, IReadOnlyDictionary<string, string> itemTypeByRecord, List<(string portName, string itemType, string entityName)> results)
    {
        if (!loadout.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in entries.EnumerateArray())
        {
            var portName = entry.TryGetProperty("itemPortName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(portName)) continue;

            var entityName = ResolveLoadoutEntityName(entry);
            string path, baseType, childType;
            string? legacyType;
            bool onlyEquipment;
            if (parentPath.Length == 0)
            {
                var byName = EquipmentPortPrefixes.Any(p => portName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                legacyType = LegacyEquipmentPortPrefixes.Any(p => portName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    ? InferLegacyPortType(portName) : null;
                baseType = byName ? InferPortType(portName) : "";
                var byItemType = false;
                if (baseType.Length == 0)
                {
                    var equipType = ShipEquipmentPortType(LookupItemType(itemTypeByRecord, entityName));
                    if (equipType != null) { baseType = equipType; byItemType = true; }
                    else if (!byName) continue;
                }
                path = portName;
                onlyEquipment = byItemType && !byName;
                childType = byItemType && OverridePortTypeByEntity(entityName, baseType) == "WeaponMount" ? "WeaponGun" : baseType;
            }
            else
            {
                path = parentPath + "/" + portName;
                baseType = ancestorType;
                childType = ancestorType;
                legacyType = ancestorLegacyType;
                onlyEquipment = equipmentOnly;
            }

            // 画面・HUD・コントローラ・座席アクセス (装備ではない) は最上位・子行とも保存しない (配下の loadout も辿らない)
            if (IsNonEquipmentEntry(portName, entityName)) continue;

            var itemType = OverridePortTypeByEntity(entityName, baseType);
            // 前からある規則で保存していた行か
            var legacyKept = legacyType != null && OverridePortTypeByEntity(entityName, legacyType).Length > 0;
            if (!legacyKept && entityName.Length > 0)
            {
                var entityItemType = LookupItemType(itemTypeByRecord, entityName);
                if (entityItemType != null && ShipEquipmentPortType(entityItemType) == null)
                {
                    // 増えた行で付いている物が装備でない: 保存しない (前からある規則の最上位の下なら、前からの行を残すため配下は辿る)
                    if (legacyType != null && entry.TryGetProperty("loadout", out var keepLoadout) && keepLoadout.ValueKind == JsonValueKind.Object)
                        CollectEntriesFromLoadout(keepLoadout, path, childType, legacyType, onlyEquipment, itemTypeByRecord, results);
                    continue;
                }
                if (entityItemType == null && onlyEquipment)
                {
                    // 付いている物の種類で拾った最上位の下の item_index に無い物: 直下の装備から種類を決める。決まらなければ保存せず配下も辿らない
                    var fromChildren = PortTypeFromChildren(entry, itemTypeByRecord);
                    if (fromChildren == null) continue;
                    itemType = fromChildren;
                }
                // 増えた子の行: 付いている物がミサイル・爆弾なら、受け継いだ種類 (Turret / BombLauncher / MissileLauncher 等) より
                // 付いている物の種類を優先する (地上車両のミサイル GMISL_* や爆弾は OverridePortTypeByEntity の名前の判定に当たらない)。
                // 受け継いだ種類がミサイルラック・爆弾ランチャーで付いている物が武器なら武器にする (タレット・ジンバルの下の武器は今の決まりのまま)
                if (parentPath.Length > 0)
                {
                    if (entityItemType is "Missile" or "Bomb") itemType = entityItemType;
                    else if (entityItemType == "WeaponGun" && (baseType is "MissileLauncher" or "BombLauncher")) itemType = "WeaponGun";
                    // タレットの下のミサイルラック (MRCK_* 等。下のミサイルは上の決まりで Missile)
                    else if (entityItemType == "MissileLauncher" && baseType == "Turret") itemType = "MissileLauncher";
                }
                // 名前の規則 (綴り違いの "missile") でミサイルラックとした増えた最上位の行に、EMP が付いている (ANVL_Hawk の hardpoint_missile_emp)
                else if (entityItemType == "EMP" && itemType == "MissileLauncher") itemType = "EMP";
            }
            results.Add((path, itemType, entityName));

            if (entry.TryGetProperty("loadout", out var subLoadout) && subLoadout.ValueKind == JsonValueKind.Object)
                CollectEntriesFromLoadout(subLoadout, path, childType, legacyType, onlyEquipment, itemTypeByRecord, results);
        }
    }

    // 色違いの表 (item_variants) を作り直し、色違いの items の性能 (raw_json / component_json) を空にする (基本品目から読むので重複して持たない)
    private void RebuildItemVariantsAndReport(string statusPrefix)
    {
        try
        {
            StatusChanged?.Invoke($"{statusPrefix}色違いの表を作成中...");
            using var q = new GameDataQueryService(DbPath);
            var (variants, blanked) = q.RebuildItemVariants(blankVariantData: true);
            StatusChanged?.Invoke($"{statusPrefix}色違い: {variants} 件 (性能を基本品目と共用: {blanked} 件)");
        }
        catch (Exception ex) { StatusChanged?.Invoke($"{statusPrefix}色違いの表の作成に失敗しました: {ex.Message}"); }
    }

    // raw_json を小さくする種類 (アーマー・服・医療用具)
    private static bool IsSlimRawJsonType(string itemType) =>
        itemType.StartsWith("Char_Armor_", StringComparison.Ordinal)
        || itemType.StartsWith("Char_Clothing_", StringComparison.Ordinal)
        || itemType == "FPS_Consumable";

    // 小さくした raw_json に残す部品 (比べるツール・lookup の GameDataQueryService.FillItemStats / 装弾数・弾の参照が読むもの)。
    // 説明文の参照と大きさ (SAttachableComponentParams)、温度・放射線 (SCItemClothingParams)、防御の参照 (SCItemSuitArmorParams)、
    // 収納 (SCItemInventoryContainerComponentParams)、装弾数と弾の参照 (SAmmoContainerComponentParams)、
    // シールド・動力装置・冷却装置・クアンタムドライブの性能 (SCItemShieldGeneratorParams / ItemResourceComponentParams / SCItemQuantumDriveParams)、
    // ミサイル・ロケットのダメージ・速度・追尾 (SCItemMissileParams。決め打ちの接頭辞に無い NOVP_Rocket_* や地上車両用の GMISL_* など)
    private static readonly HashSet<string> SlimRawJsonComponents = new(StringComparer.Ordinal)
    {
        "SAttachableComponentParams", "SCItemClothingParams", "SCItemSuitArmorParams", "SCItemInventoryContainerComponentParams",
        "SAmmoContainerComponentParams", "SCItemShieldGeneratorParams", "ItemResourceComponentParams", "SCItemQuantumDriveParams",
        "SCItemMissileParams",
    };

    // 種類 (AttachDef.Type) ごとに、小さくした raw_json にさらに残す部品。アーマー・服も持っていて大きいので、読む種類だけに残す。
    //   MissileLauncher: 搭載するミサイルのポート (SItemPortContainerComponentParams。GameDataQueryService.FillItemStats の「搭載」)
    //   WeaponGun: 種類の語の displayType (SCItemPurchasableParams。GameDataQueryService.ShipWeaponTypeWords)
    //   WeaponPersonal / WeaponAttachment: アタッチメントの差し口 (SItemPortContainerComponentParams の Ports) と
    //     アタッチメントによる武器の性能の補正 (SWeaponModifierComponentParams の modifier.weaponStats)。種類で拾った追加分の個人武器・付属品で使う
    private static readonly Dictionary<string, string[]> SlimRawJsonComponentsByType = new(StringComparer.Ordinal)
    {
        ["MissileLauncher"] = ["SItemPortContainerComponentParams"],
        ["WeaponGun"] = ["SCItemPurchasableParams"],
        ["WeaponPersonal"] = ["SItemPortContainerComponentParams", "SWeaponModifierComponentParams"],
        ["WeaponAttachment"] = ["SItemPortContainerComponentParams", "SWeaponModifierComponentParams"],
    };

    // record の JSON を、形 ({"_RecordName_", "_RecordId_", "_RecordValue_": {"_Type_", "Components": [...]}}) はそのままに、
    // Components を SlimRawJsonComponents と、種類 itemType の SlimRawJsonComponentsByType だけにしたものにする
    // alsoKeep を渡すと、その部品も残す (追加で取り込む品目の性能に使う部品。ExtraItemKeepComponents)
    internal static string SlimItemRawJson(JsonElement root, string itemType = "", IReadOnlySet<string>? alsoKeep = null)
    {
        SlimRawJsonComponentsByType.TryGetValue(itemType ?? "", out var extra);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name != "_RecordValue_" || prop.Value.ValueKind != JsonValueKind.Object)
                {
                    if (prop.Name is "_RecordName_" or "_RecordId_" or "_RecordTag_") prop.WriteTo(w);
                    continue;
                }
                w.WritePropertyName("_RecordValue_");
                w.WriteStartObject();
                foreach (var p2 in prop.Value.EnumerateObject())
                {
                    if (p2.Name == "_Type_") { p2.WriteTo(w); continue; }
                    if (p2.Name != "Components" || p2.Value.ValueKind != JsonValueKind.Array) continue;
                    w.WritePropertyName("Components");
                    w.WriteStartArray();
                    foreach (var comp in p2.Value.EnumerateArray())
                    {
                        if (comp.ValueKind != JsonValueKind.Object || !comp.TryGetProperty("_Type_", out var t)) continue;
                        var type = t.GetString() ?? "";
                        if (SlimRawJsonComponents.Contains(type) || (extra != null && extra.Contains(type)) || (alsoKeep != null && alsoKeep.Contains(type))) comp.WriteTo(w);
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    // onlyRecords を渡すと、その record だけを取り込む (種類で拾った追加分。同じ接頭辞の関係ない record を入れないため)
    private async Task<int> ExtractItemsAsync(string p4kPath, string filter, string componentType, string now, CancellationToken ct, HashSet<string>? onlyRecords = null)
    {
        var rawJson = await RunDcbQueryWithTimerAsync(p4kPath, "EntityClassDefinition", filter, $"アイテム ({filter})", ct);
        if (string.IsNullOrEmpty(rawJson)) return 0;

        int count = 0;
        using var tx = _db!.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO items(record_name,name,item_type,item_sub_type,size,grade,manufacturer,component_type,component_json,raw_json,extracted_at) VALUES(@rn,@nm,@it,@ist,@sz,@gr,@mf,@ct,@cj,@rj,@ea)";
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pNm = cmd.Parameters.Add("@nm", SqliteType.Text);
        var pIt = cmd.Parameters.Add("@it", SqliteType.Text);
        var pIst = cmd.Parameters.Add("@ist", SqliteType.Text);
        var pSz = cmd.Parameters.Add("@sz", SqliteType.Integer);
        var pGr = cmd.Parameters.Add("@gr", SqliteType.Integer);
        var pMf = cmd.Parameters.Add("@mf", SqliteType.Text);
        var pCt = cmd.Parameters.Add("@ct", SqliteType.Text);
        var pCj = cmd.Parameters.Add("@cj", SqliteType.Text);
        var pRj = cmd.Parameters.Add("@rj", SqliteType.Text);
        var pEa = cmd.Parameters.Add("@ea", SqliteType.Text);

        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(recordName)) continue;
                if (onlyRecords != null && !onlyRecords.Contains(recordName)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)) continue;

                string name = "", itemType = "", subType = "", mfr = "";
                int size = 0, grade = 0;
                string compJson = "";

                foreach (var comp in components.EnumerateArray())
                {
                    // Components には null の要素が入っている record がある (例 grin_multitool_01_cutter)。読まずに飛ばす
                    // (飛ばさないと TryGetProperty が例外になり、その record ごと取り込めなかった)
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                    {
                        itemType = ad.TryGetProperty("Type", out var it2) ? it2.GetString() ?? "" : "";
                        subType = ad.TryGetProperty("SubType", out var st) ? st.GetString() ?? "" : "";
                        size = ad.TryGetProperty("Size", out var sz) ? sz.GetInt32() : 0;
                        grade = ad.TryGetProperty("Grade", out var g) ? g.GetInt32() : 0;
                        mfr = ad.TryGetProperty("Manufacturer", out var mf) ? mf.GetString() ?? "" : "";
                        if (ad.TryGetProperty("Localization", out var loc))
                            name = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    }
                    if (type == componentType)
                        compJson = comp.ToString();
                }

                if (string.IsNullOrEmpty(itemType) || itemType == "UNDEFINED") continue;

                // アーマー・服・医療用具と、種類で拾った追加分は raw_json が大きいので、比べるツールと lookup が使う部品だけを残す
                var slim = onlyRecords != null || IsSlimRawJsonType(itemType);
                pRn.Value = recordName; pNm.Value = name; pIt.Value = itemType; pIst.Value = subType;
                pSz.Value = size; pGr.Value = grade; pMf.Value = mfr;
                pCt.Value = componentType; pCj.Value = compJson; pRj.Value = slim ? SlimItemRawJson(root, itemType) : block; pEa.Value = now;
                cmd.ExecuteNonQuery();
                count++;
            }
            catch { }
        }
        tx.Commit();
        return count;
    }

    private async Task<int> ExtractMissionsAsync(string p4kPath, string now, CancellationToken ct)
    {
        // Load English localization for resolving @key references
        var locDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var gamePath = App.Config.GamePath;
        var enIniPath = Path.Combine(gamePath, "data", "Localization", "english", "global.ini");
        if (!File.Exists(enIniPath))
            enIniPath = Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "data", "Localization", "english", "global.ini");
        if (File.Exists(enIniPath))
        {
            StatusChanged?.Invoke("[4/6] ローカライズデータを読み込み中...");
            locDict = GlobalIniParser.Parse(enIniPath);
        }

        int count = 0;
        using var tx = _db!.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO missions(record_name,title,title_hud,mission_type,difficulty,mission_giver,location_label,description,reward_min,reward_max,required_reputation,lawfulness_type,jurisdiction,time_limit,raw_json,extracted_at) VALUES(@rn,@ti,@th,@mt,@di,@mg,@ll,@de,@rmin,@rmax,@rr,@lt,@ju,@tl,@rj,@ea)";
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pTi = cmd.Parameters.Add("@ti", SqliteType.Text);
        var pTh = cmd.Parameters.Add("@th", SqliteType.Text);
        var pMt = cmd.Parameters.Add("@mt", SqliteType.Text);
        var pDi = cmd.Parameters.Add("@di", SqliteType.Text);
        var pMg = cmd.Parameters.Add("@mg", SqliteType.Text);
        var pLl = cmd.Parameters.Add("@ll", SqliteType.Text);
        var pDe = cmd.Parameters.Add("@de", SqliteType.Text);
        var pRmin = cmd.Parameters.Add("@rmin", SqliteType.Real);
        var pRmax = cmd.Parameters.Add("@rmax", SqliteType.Real);
        var pRr = cmd.Parameters.Add("@rr", SqliteType.Text);
        var pLt = cmd.Parameters.Add("@lt", SqliteType.Text);
        var pJu = cmd.Parameters.Add("@ju", SqliteType.Text);
        var pTl = cmd.Parameters.Add("@tl", SqliteType.Text);
        var pRj = cmd.Parameters.Add("@rj", SqliteType.Text);
        var pEa = cmd.Parameters.Add("@ea", SqliteType.Text);

        // 1 行分を missions 表に INSERT OR REPLACE する (MissionBrokerEntry 行・ContractGenerator 行で共用)
        void InsertRow(MissionRow row)
        {
            pRn.Value = row.RecordName;
            pTi.Value = row.Title;
            pTh.Value = row.TitleHud;
            pMt.Value = row.MissionType;
            pDi.Value = row.Difficulty;
            pMg.Value = row.MissionGiver;
            pLl.Value = row.Location;
            pDe.Value = row.Description;
            pRmin.Value = row.RewardMin;
            pRmax.Value = row.RewardMax;
            pRr.Value = row.RequiredReputation;
            pLt.Value = row.LawfulnessType;
            pJu.Value = row.Jurisdiction;
            pTl.Value = row.TimeLimit;
            pRj.Value = row.RawJson;
            pEa.Value = now;
            cmd.ExecuteNonQuery();
        }

        // 6 フィルタで登録した MissionBrokerEntry の record_name (レガシー契約の参照先を重複登録しないため)
        var insertedMbe = new HashSet<string>(StringComparer.Ordinal);

        var filters = new[] { "*PU_*", "*Tutorial*", "*Xenothreat*", "*ShipIncursion*", "*BlockadeRunner*", "*Kill*" };
        for (int fi = 0; fi < filters.Length; fi++)
        {
            ct.ThrowIfCancellationRequested();
            var filterIdx = fi;
            var filterLabel = filters[fi];

            var rawJson = await RunDcbQueryWithTimerAsync(p4kPath, "MissionBrokerEntry", filterLabel,
                $"[4/6] 契約 ({filterIdx + 1}/{filters.Length}): {filterLabel}", ct);
            if (string.IsNullOrEmpty(rawJson)) continue;

            StatusChanged?.Invoke($"[4/6] 契約解析中 ({filterIdx + 1}/{filters.Length}): {filterLabel} — DB登録中...");
            int blockCount = 0;
            foreach (var block in SplitJsonBlocks(rawJson))
            {
                try
                {
                    var row = BuildMissionBrokerRow(block, locDict);
                    if (row == null) continue;
                    InsertRow(row);
                    insertedMbe.Add(row.RecordName);
                    count++;
                }
                catch { }

                blockCount++;
                if (blockCount % 200 == 0)
                    StatusChanged?.Invoke($"[4/6] 契約解析中 ({filterIdx + 1}/{filters.Length}): {filterLabel} — {count}件登録 ({blockCount}ブロック処理済み)");
            }
            ProgressChanged?.Invoke(66 + (filterIdx + 1) * 10 / filters.Length, $"[4/6] 契約: {count}件抽出済み");
        }

        // ContractGenerator の親契約 (contracts / introContracts / serviceBeaconContracts / PVPBountyContract) を 1 契約 1 行で登録する。
        // 表示用の値の補完に ContractTemplate も読む
        ct.ThrowIfCancellationRequested();
        var generatorJson = await RunDcbQueryWithTimerAsync(p4kPath, "ContractGenerator", "*", "[4/6] 契約 (ContractGenerator)", ct);
        if (!string.IsNullOrEmpty(generatorJson))
        {
            ct.ThrowIfCancellationRequested();
            var templateJson = await RunDcbQueryWithTimerAsync(p4kPath, "ContractTemplate", "*", "[4/6] 契約テンプレート (ContractTemplate)", ct);

            StatusChanged?.Invoke("[4/6] 契約解析中 (ContractGenerator) — DB登録中...");
            int contractCount = 0;
            foreach (var row in BuildContractGeneratorRows(generatorJson, templateJson, locDict))
            {
                try
                {
                    InsertRow(row);
                    count++;
                    contractCount++;
                }
                catch { }
            }
            StatusChanged?.Invoke($"[4/6] 契約解析中 (ContractGenerator) — {contractCount}件登録");
            ProgressChanged?.Invoke(79, $"[4/6] 契約: {count}件抽出済み");

            // legacyContracts[] が参照する MissionBrokerEntry のうち、6 フィルタで入らなかったものを同じ処理で登録する。
            // 参照先だけをフィルタで指定できないため、MissionBrokerEntry を全件 ("*") 取得して参照名で拾う
            // (RebuildMissionReputationAsync と同じ取り方)
            var legacyRefs = CollectLegacyMissionBrokerRefs(generatorJson);
            if (legacyRefs.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var mbeAllJson = await RunDcbQueryWithTimerAsync(p4kPath, "MissionBrokerEntry", "*",
                    "[4/6] 契約 (レガシー契約の参照先): *", ct);
                if (!string.IsNullOrEmpty(mbeAllJson))
                {
                    StatusChanged?.Invoke("[4/6] 契約解析中 (レガシー契約の参照先) — DB登録中...");
                    int legacyCount = 0;
                    foreach (var block in SplitJsonBlocks(mbeAllJson))
                    {
                        try
                        {
                            var row = BuildMissionBrokerRow(block, locDict);
                            if (row == null) continue;
                            if (!legacyRefs.Contains(row.RecordName["MissionBrokerEntry.".Length..].ToLowerInvariant())) continue;
                            if (insertedMbe.Contains(row.RecordName)) continue;
                            InsertRow(row);
                            insertedMbe.Add(row.RecordName);
                            count++;
                            legacyCount++;
                        }
                        catch { }
                    }
                    StatusChanged?.Invoke($"[4/6] 契約解析中 (レガシー契約の参照先) — {legacyCount}件登録");
                }
            }
            ProgressChanged?.Invoke(82, $"[4/6] 契約: {count}件抽出済み");
        }

        tx.Commit();
        return count;
    }

    // missions 表の 1 行分 (raw_json 以外は表示・検索用に解決済みの値)
    internal sealed class MissionRow
    {
        public string RecordName { get; init; } = "";
        public string Title { get; init; } = "";
        public string TitleHud { get; init; } = "";
        public string MissionType { get; init; } = "";
        public string Difficulty { get; init; } = "";
        public string MissionGiver { get; init; } = "";
        public string Location { get; init; } = "";
        public string Description { get; init; } = "";
        public double RewardMin { get; init; }
        public double RewardMax { get; init; }
        public string RequiredReputation { get; init; } = "";
        public string LawfulnessType { get; init; } = "";
        public string Jurisdiction { get; init; } = "";
        public string TimeLimit { get; init; } = "";
        public string RawJson { get; init; } = "";
    }

    // MissionBrokerEntry 1 レコード (dcb query の 1 ブロック) を missions 表の 1 行にする。
    // MissionBrokerEntry 以外のレコード・_RecordValue_ が無いものは null。JSON が壊れていれば例外 (呼び出し側で握りつぶす)
    internal static MissionRow? BuildMissionBrokerRow(string block, Dictionary<string, string> locDict)
    {
        using var doc = JsonDocument.Parse(block);
        var root = doc.RootElement;
        var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
        if (!recordName.StartsWith("MissionBrokerEntry.")) return null;
        if (!root.TryGetProperty("_RecordValue_", out var rv)) return null;

        var title = ResolveLoc(GetStr(rv, "title"), locDict);
        var difficulty = GetStr(rv, "difficulty");
        var location = ResolveLoc(GetStr(rv, "locationLabel"), locDict);
        if (string.IsNullOrEmpty(difficulty))
            difficulty = InferDifficulty(recordName);
        if (string.IsNullOrEmpty(location))
            location = InferLocation(recordName);

        double rewardBase = 0;
        if (rv.TryGetProperty("missionReward", out var mReward))
            rewardBase = GetNum(mReward, "reward", GetNum(mReward, "max", 0));

        return new MissionRow
        {
            RecordName = recordName,
            Title = title,
            TitleHud = ResolveLoc(GetStr(rv, "titleHUD"), locDict),
            MissionType = CleanMissionType(GetStr(rv, "type")),
            Difficulty = difficulty,
            MissionGiver = ResolveLoc(GetStr(rv, "missionGiver"), locDict),
            Location = location,
            Description = ResolveLoc(GetStr(rv, "description"), locDict),
            RewardMin = GetNum(rv, "rewardMin", GetNum(rv, "payoutMin", rewardBase)),
            RewardMax = GetNum(rv, "rewardMax", GetNum(rv, "payoutMax", rewardBase)),
            RequiredReputation = GetStr(rv, "requiredReputation", GetStr(rv, "minReputation")),
            LawfulnessType = GetStr(rv, "lawfulnessType"),
            Jurisdiction = GetStr(rv, "jurisdiction"),
            TimeLimit = GetStr(rv, "timeLimit", GetStr(rv, "deadline")),
            RawJson = block,
        };
    }

    // ContractGenerator 行の record_name の接頭辞。後ろに契約 id (GUID, 小文字) を付ける。
    // debugName を含めないのは、FetchWikiMissionsAsync の debug_name による LIKE 紐付けが CG 行に当たらないようにするため
    internal const string ContractRecordPrefix = "ContractGenerator.";

    // ContractGenerator 行の raw_json から表示用の名前を読む (record_name の GUID を画面に出さないため。MissionService / GameDataQueryService で共用)
    //   NameBase         : 契約 debugName → 生成器 debugName ("_Name_") → タイトルキー (@ を外す)。分類・難易度・場所の推測と検索に使う
    //   DisplayRecordName: "ContractGenerator.<生成器レコード名の末尾>.<NameBase>" (NameBase が空なら "ContractGenerator.<末尾>")
    //   DebugName        : "_Name_" そのもの (契約 debugName → 生成器 debugName)。テスト用レコードの判定に使う
    // 読めなければ ("", "", "")
    internal static (string DebugName, string NameBase, string DisplayRecordName) ReadContractDisplayNames(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson)) return ("", "", "");
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var debugName = GetStr(root, "_Name_");
            var name = debugName;
            if (string.IsNullOrEmpty(name) && root.TryGetProperty("_Resolved_", out var res) && res.ValueKind == JsonValueKind.Object)
                name = GetStr(res, "title").TrimStart('@');
            var generatorRecord = GetStr(root, "_RecordName_");
            var generatorTail = generatorRecord.StartsWith(ContractRecordPrefix) ? generatorRecord[ContractRecordPrefix.Length..] : generatorRecord;
            var display = ContractRecordPrefix + generatorTail + (string.IsNullOrEmpty(name) ? "" : "." + name);
            return (debugName, name, display);
        }
        catch { return ("", "", ""); }
    }

    // 親契約を持つ配列 (subContracts は行にしない。legacyContracts は参照先の MissionBrokerEntry を別途登録する)
    private static readonly string[] ContractArrayNames = ["contracts", "introContracts", "serviceBeaconContracts", "PVPBountyContract"];

    // ContractGenerator 全件の dcb query 出力から、親契約 1 件ごとに missions 表の 1 行を作る。
    // 値の取り方 (前にあるものを優先):
    //   title       : 契約 Title → 生成器 contractParams の Title → テンプレート contractDisplayInfo.displayString[0]
    //   description : 契約 Description → 生成器 Description → displayString[2]
    //   依頼主      : 契約 Contractor → 生成器 Contractor → displayString[3] (LOC_UNINITIALIZED はどの段でも無視)
    //   mission_type: 契約 missionTypeOverride → 生成器 contractParams.missionTypeOverride → テンプレート contractDisplayInfo.type
    //   title_hud   : 契約 TitleHUD → 生成器 TitleHUD → displayString[1] (LOC_UNINITIALIZED はどの段でも無視)
    //   Illegal     : 契約 boolParamOverrides の Illegal → 生成器 contractParams の Illegal → テンプレート contractDisplayInfo.illegal
    //   パーティ共有: 契約 boolParamOverrides の CanBeShared → 生成器の CanBeShared (無ければ表示しない)
    //   未リリース  : 契約または生成器の notForRelease が true
    // raw_json は契約の JSON をそのまま "_Contract_" に入れ、補った値 (ローカライズキーとその出どころ) を "_Resolved_" に同梱する。
    // Wikelo のテンプレート (record 名に TheCollector を含む ContractTemplate) を使う契約は、テンプレートの納品物 (BuildTemplateHaulingJson) を
    // "_TemplateHauling_" に入れる (ほかの契約には入れない)
    internal static List<MissionRow> BuildContractGeneratorRows(string generatorJson, string? templateJson, Dictionary<string, string> locDict)
    {
        // ContractTemplate.<名前> の <名前> 小文字 → contractDisplayInfo (契約の template 参照のファイル名で引く)
        var templates = new Dictionary<string, string>(StringComparer.Ordinal);
        // ContractTemplate.<名前> の <名前> 小文字 → テンプレートの納品物の JSON (Wikelo のテンプレートで haulingOrders があるものだけ)
        var templateHauling = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(templateJson))
        {
            foreach (var block in SplitJsonBlocks(templateJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    if (!recordName.StartsWith("ContractTemplate.")) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;
                    var key = recordName["ContractTemplate.".Length..].ToLowerInvariant();
                    if (recordName.Contains(WikeloTemplateMark, StringComparison.OrdinalIgnoreCase)
                        && BuildTemplateHaulingJson(recordName, rv) is { } hauling)
                        templateHauling[key] = hauling;
                    if (!rv.TryGetProperty("contractDisplayInfo", out var di) || di.ValueKind != JsonValueKind.Object) continue;
                    templates[key] = di.GetRawText();
                }
                catch { }
            }
        }

        var rows = new List<MissionRow>();
        foreach (var block in SplitJsonBlocks(generatorJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var generatorRecord = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (!generatorRecord.StartsWith("ContractGenerator.")) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;
                if (!rv.TryGetProperty("generators", out var generators) || generators.ValueKind != JsonValueKind.Array) continue;

                foreach (var gen in generators.EnumerateArray())
                {
                    if (gen.ValueKind != JsonValueKind.Object) continue;
                    var genParams = gen.TryGetProperty("contractParams", out var gp) && gp.ValueKind == JsonValueKind.Object ? gp : default;

                    foreach (var arrayName in ContractArrayNames)
                    {
                        if (!gen.TryGetProperty(arrayName, out var contracts) || contracts.ValueKind != JsonValueKind.Array) continue;
                        foreach (var contract in contracts.EnumerateArray())
                        {
                            try
                            {
                                var row = BuildContractRow(generatorRecord, gen, genParams, arrayName, contract, templates, templateHauling, locDict);
                                if (row != null) rows.Add(row);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
        }
        return rows;
    }

    // Wikelo の契約のテンプレートの record 名に入っている語 (ContractTemplate.TheCollector_* / ItemResourceGathering_TheCollector など)
    private const string WikeloTemplateMark = "TheCollector";

    // テンプレートの納品物: objectiveTokens[].objectiveHandler が ObjectiveHandler_Hauling のものの haulingOrders[] を、元の JSON のまま
    // 出てきた順に "orders" に並べる。HaulingOrder_Property の haulingOrdersProperty ("_PointsTo_:ptr:N") が指す目標の property
    // (objectiveTokens[].properties[] の "_Pointer_" が ptr:N のもの) の missionVariableName を "propertyVariables" (ptr:N → 名前) に入れる
    // (契約側の propertyOverrides の missionVariableName と突き合わせるため)。haulingOrders が 1 件も無ければ null
    //   {"template": record 名, "orders": [...], "propertyVariables": {"ptr:6": "HaulingOverride"}}
    internal static string? BuildTemplateHaulingJson(string recordName, JsonElement recordValue)
    {
        if (!recordValue.TryGetProperty("objectiveTokens", out var tokens) || tokens.ValueKind != JsonValueKind.Array) return null;
        var orders = new List<JsonElement>();
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens.EnumerateArray())
        {
            if (token.ValueKind != JsonValueKind.Object) continue;
            if (token.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in props.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var ptr = GetStr(p, "_Pointer_");
                    var name = GetStr(p, "missionVariableName");
                    if (ptr.Length > 0 && name.Length > 0) variables[ptr] = name;
                }
            }
            if (!token.TryGetProperty("objectiveHandler", out var handler) || handler.ValueKind != JsonValueKind.Object) continue;
            if (GetStr(handler, "_Type_") != "ObjectiveHandler_Hauling") continue;
            if (!handler.TryGetProperty("haulingOrders", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            foreach (var o in list.EnumerateArray())
                if (o.ValueKind == JsonValueKind.Object) orders.Add(o);
        }
        if (orders.Count == 0) return null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("template", recordName);
            w.WriteStartArray("orders");
            foreach (var o in orders) o.WriteTo(w);
            w.WriteEndArray();
            w.WriteStartObject("propertyVariables");
            foreach (var (ptr, name) in variables) w.WriteString(ptr, name);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static MissionRow? BuildContractRow(string generatorRecord, JsonElement gen, JsonElement genParams, string arrayName,
        JsonElement contract, Dictionary<string, string> templates, Dictionary<string, string> templateHauling, Dictionary<string, string> locDict)
    {
        if (contract.ValueKind != JsonValueKind.Object) return null;
        var id = GetStr(contract, "id").ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return null;

        var contractParams = contract.TryGetProperty("paramOverrides", out var po) && po.ValueKind == JsonValueKind.Object ? po : default;

        // テンプレート: "file://.../contracttemplates/xxx.json" の xxx で引く
        JsonDocument? templateDoc = null;
        string? haulingJson = null;
        var templateRef = GetStr(contract, "template");
        if (!string.IsNullOrEmpty(templateRef))
        {
            var name = templateRef[(templateRef.LastIndexOf('/') + 1)..];
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^".json".Length];
            if (templates.TryGetValue(name.ToLowerInvariant(), out var displayInfoJson))
                templateDoc = JsonDocument.Parse(displayInfoJson);
            templateHauling.TryGetValue(name.ToLowerInvariant(), out haulingJson);
        }
        using (templateDoc)
        {
            var displayInfo = templateDoc?.RootElement ?? default;
            string DisplayString(int index)
            {
                if (displayInfo.ValueKind != JsonValueKind.Object) return "";
                if (!displayInfo.TryGetProperty("displayString", out var ds) || ds.ValueKind != JsonValueKind.Array) return "";
                if (index >= ds.GetArrayLength()) return "";
                var v = ds[index];
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            }

            var (titleKey, titleSource) = FirstValue(
                ("contract", GetStringParam(contractParams, "Title")),
                ("generator", GetStringParam(genParams, "Title")),
                ("template", DisplayString(0)));
            var (descKey, descSource) = FirstValue(
                ("contract", GetStringParam(contractParams, "Description")),
                ("generator", GetStringParam(genParams, "Description")),
                ("template", DisplayString(2)));
            var (contractorKey, contractorSource) = FirstValue(
                ("contract", SkipUninitialized(GetStringParam(contractParams, "Contractor"))),
                ("generator", SkipUninitialized(GetStringParam(genParams, "Contractor"))),
                ("template", SkipUninitialized(DisplayString(3))));
            var (missionTypeRef, missionTypeSource) = FirstValue(
                ("contract", contractParams.ValueKind == JsonValueKind.Object ? GetStr(contractParams, "missionTypeOverride") : ""),
                ("generator", genParams.ValueKind == JsonValueKind.Object ? GetStr(genParams, "missionTypeOverride") : ""),
                ("template", displayInfo.ValueKind == JsonValueKind.Object ? GetStr(displayInfo, "type") : ""));

            var (titleHudKey, titleHudSource) = FirstValue(
                ("contract", SkipUninitialized(GetStringParam(contractParams, "TitleHUD"))),
                ("generator", SkipUninitialized(GetStringParam(genParams, "TitleHUD"))),
                ("template", SkipUninitialized(DisplayString(1))));

            bool? illegal = GetBoolParam(contractParams, "Illegal");
            var illegalSource = illegal.HasValue ? "contract" : "";
            if (!illegal.HasValue)
            {
                illegal = GetBoolParam(genParams, "Illegal");
                if (illegal.HasValue) illegalSource = "generator";
            }
            if (!illegal.HasValue && displayInfo.ValueKind == JsonValueKind.Object && displayInfo.TryGetProperty("illegal", out var il)
                && (il.ValueKind == JsonValueKind.True || il.ValueKind == JsonValueKind.False))
            {
                illegal = il.ValueKind == JsonValueKind.True;
                illegalSource = "template";
            }

            // パーティ共有: 契約 → 生成器の boolParamOverrides の CanBeShared (どちらにも無ければ null)
            bool? canBeShared = GetBoolParam(contractParams, "CanBeShared");
            var canBeSharedSource = canBeShared.HasValue ? "contract" : "";
            if (!canBeShared.HasValue)
            {
                canBeShared = GetBoolParam(genParams, "CanBeShared");
                if (canBeShared.HasValue) canBeSharedSource = "generator";
            }

            // 一覧の表示名・難易度・場所・分類の推測に使う名前 (契約 debugName、空なら生成器 debugName)
            var nameBase = GetStr(contract, "debugName");
            if (string.IsNullOrEmpty(nameBase)) nameBase = GetStr(gen, "debugName");

            // raw_json: 補った値を "_Resolved_"、契約の JSON をそのまま "_Contract_" に入れる
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("_Name_", nameBase);
                w.WriteString("_RecordName_", generatorRecord);
                w.WriteString("_ContractArray_", arrayName);
                w.WriteStartObject("_Generator_");
                w.WriteString("_Type_", GetStr(gen, "_Type_"));
                w.WriteString("debugName", GetStr(gen, "debugName"));
                w.WriteBoolean("notForRelease", GetBoolOrFalse(gen, "notForRelease"));
                w.WriteBoolean("workInProgress", GetBoolOrFalse(gen, "workInProgress"));
                w.WriteEndObject();
                w.WriteStartObject("_Resolved_");
                w.WriteString("title", titleKey);
                w.WriteString("titleSource", titleSource);
                w.WriteString("description", descKey);
                w.WriteString("descriptionSource", descSource);
                w.WriteString("contractor", contractorKey);
                w.WriteString("contractorSource", contractorSource);
                w.WriteString("missionType", missionTypeRef);
                w.WriteString("missionTypeSource", missionTypeSource);
                if (illegal.HasValue) w.WriteBoolean("illegal", illegal.Value);
                else w.WriteNull("illegal");
                w.WriteString("illegalSource", illegalSource);
                w.WriteString("titleHud", titleHudKey);
                w.WriteString("titleHudSource", titleHudSource);
                if (canBeShared.HasValue) w.WriteBoolean("canBeShared", canBeShared.Value);
                else w.WriteNull("canBeShared");
                w.WriteString("canBeSharedSource", canBeSharedSource);
                w.WriteString("template", templateRef);
                w.WriteBoolean("templateFound", templateDoc != null);
                w.WriteEndObject();
                // テンプレートの納品物 (Wikelo のテンプレートで haulingOrders があるときだけ)
                if (haulingJson != null)
                {
                    using var hd = JsonDocument.Parse(haulingJson);
                    w.WritePropertyName("_TemplateHauling_");
                    hd.RootElement.WriteTo(w);
                }
                w.WritePropertyName("_Contract_");
                contract.WriteTo(w);
                w.WriteEndObject();
            }

            return new MissionRow
            {
                RecordName = ContractRecordPrefix + id,
                Title = ResolveLocWithPluralKey(titleKey, locDict),
                TitleHud = ResolveLocWithPluralKey(titleHudKey, locDict),
                MissionType = CleanMissionType(missionTypeRef),
                Difficulty = InferDifficulty(nameBase),
                MissionGiver = ResolveLocWithPluralKey(contractorKey, locDict),
                Location = InferLocation(nameBase),
                Description = ResolveLocWithPluralKey(descKey, locDict),
                RawJson = Encoding.UTF8.GetString(ms.ToArray()),
            };
        }
    }

    // 候補を前から見て、最初の空でない値とその出どころを返す (どれも空なら ("", ""))
    private static (string Value, string Source) FirstValue(params (string Source, string Value)[] candidates)
    {
        foreach (var (source, value) in candidates)
            if (!string.IsNullOrEmpty(value)) return (value, source);
        return ("", "");
    }

    // bool のプロパティを読む (true 以外は false)
    private static bool GetBoolOrFalse(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    // "@LOC_UNINITIALIZED" / "LOC_UNINITIALIZED" は値なしとして扱う
    private static string SkipUninitialized(string value) =>
        value.TrimStart('@').Equals("LOC_UNINITIALIZED", StringComparison.OrdinalIgnoreCase) ? "" : value;

    // ContractParamOverrides.stringParamOverrides から param が一致する最初の value を返す (無ければ "")
    private static string GetStringParam(JsonElement paramOverrides, string param)
    {
        if (paramOverrides.ValueKind != JsonValueKind.Object) return "";
        if (!paramOverrides.TryGetProperty("stringParamOverrides", out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        foreach (var p in arr.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Object && GetStr(p, "param") == param)
                return GetStr(p, "value");
        return "";
    }

    // ContractParamOverrides.boolParamOverrides から param が一致する最初の value を返す (無ければ null)
    private static bool? GetBoolParam(JsonElement paramOverrides, string param)
    {
        if (paramOverrides.ValueKind != JsonValueKind.Object) return null;
        if (!paramOverrides.TryGetProperty("boolParamOverrides", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in arr.EnumerateArray())
        {
            if (p.ValueKind != JsonValueKind.Object || GetStr(p, "param") != param) continue;
            if (!p.TryGetProperty("value", out var v)) return null;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            return null;
        }
        return null;
    }

    // ContractGenerator の legacyContracts[].missionBrokerEntry が参照する MissionBrokerEntry 名 (小文字) の集合。
    // 参照は "file://.../missionbroker/.../haulcargo_atob_stanton1_intro.json" の形で、ファイル名が "MissionBrokerEntry.<名前>" の <名前> 小文字に当たる
    internal static HashSet<string> CollectLegacyMissionBrokerRefs(string generatorJson)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var block in SplitJsonBlocks(generatorJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;
                if (!rv.TryGetProperty("generators", out var generators) || generators.ValueKind != JsonValueKind.Array) continue;
                foreach (var gen in generators.EnumerateArray())
                {
                    if (gen.ValueKind != JsonValueKind.Object) continue;
                    if (!gen.TryGetProperty("legacyContracts", out var legacy) || legacy.ValueKind != JsonValueKind.Array) continue;
                    foreach (var c in legacy.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object) continue;
                        var path = GetStr(c, "missionBrokerEntry");
                        if (string.IsNullOrEmpty(path)) continue;
                        var name = path[(path.LastIndexOf('/') + 1)..];
                        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name[..^".json".Length];
                        if (name.Length > 0) refs.Add(name.ToLowerInvariant());
                    }
                }
            }
            catch { }
        }
        return refs;
    }

    private async Task<int> ExtractCommoditiesAsync(string p4kPath, string now, CancellationToken ct)
    {
        var rawJson = await RunDcbQueryWithTimerAsync(p4kPath, "CommoditySubtype", "*", "[5/6] コモディティ", ct);
        if (string.IsNullOrEmpty(rawJson)) return 0;

        int count = 0;
        using var tx = _db!.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO commodities(record_name,name,symbol,volatility,raw_json,extracted_at) VALUES(@rn,@nm,@sy,@vo,@rj,@ea)";
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pNm = cmd.Parameters.Add("@nm", SqliteType.Text);
        var pSy = cmd.Parameters.Add("@sy", SqliteType.Text);
        var pVo = cmd.Parameters.Add("@vo", SqliteType.Text);
        var pRj = cmd.Parameters.Add("@rj", SqliteType.Text);
        var pEa = cmd.Parameters.Add("@ea", SqliteType.Text);

        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(recordName)) continue;
                if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;

                pRn.Value = recordName;
                pNm.Value = GetStr(rv, "name");
                pSy.Value = GetStr(rv, "symbol");
                pVo.Value = GetStr(rv, "volatility");
                pRj.Value = block;
                pEa.Value = now;
                cmd.ExecuteNonQuery();
                count++;
            }
            catch { }
        }
        tx.Commit();
        return count;
    }

    private static string ResolveLoc(string raw, Dictionary<string, string> locDict)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (!raw.StartsWith('@')) return raw;
        var key = raw[1..];
        if (locDict.TryGetValue(key, out var resolved) && !string.IsNullOrEmpty(resolved))
            return resolved;
        return key;
    }

    // ResolveLoc と同じだが、キーが見つからなければ末尾に ",P" を付けたキーも試す
    // (ContractGenerator のローカライズキーには ini に ",P" 付きでしか無いものがあるため。ContractGenerator 行だけで使う)
    private static string ResolveLocWithPluralKey(string raw, Dictionary<string, string> locDict)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (!raw.StartsWith('@')) return raw;
        var key = raw[1..];
        if (locDict.TryGetValue(key, out var resolved) && !string.IsNullOrEmpty(resolved))
            return resolved;
        if (locDict.TryGetValue(key + ",P", out var resolvedP) && !string.IsNullOrEmpty(resolvedP))
            return resolvedP;
        return key;
    }

    private static string InferDifficulty(string recordName)
    {
        var parts = recordName.Split('_');
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            // Vehicle の直後の語 (TheCollector_Vehicle_Medium_Firebird など) は船の大きさの区分なので難易度にしない (MissionService.InferDifficulty と同じ)
            if (i > 0 && parts[i - 1].Equals("Vehicle", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Equals("Intro", StringComparison.OrdinalIgnoreCase)) return "Intro";
            if (p.Equals("VeryEasy", StringComparison.OrdinalIgnoreCase)) return "Very Easy";
            if (p.Equals("Easy", StringComparison.OrdinalIgnoreCase)) return "Easy";
            if (p.Equals("Medium", StringComparison.OrdinalIgnoreCase)) return "Medium";
            if (p.Equals("Hard", StringComparison.OrdinalIgnoreCase)) return "Hard";
            if (p.Equals("VeryHard", StringComparison.OrdinalIgnoreCase)) return "Very Hard";
        }
        return "";
    }

    private static string InferLocation(string recordName)
    {
        if (recordName.Contains("Stanton1", StringComparison.OrdinalIgnoreCase)) return "Stanton (Hurston)";
        if (recordName.Contains("Stanton2", StringComparison.OrdinalIgnoreCase)) return "Stanton (Crusader)";
        if (recordName.Contains("Stanton3", StringComparison.OrdinalIgnoreCase)) return "Stanton (ArcCorp)";
        if (recordName.Contains("Stanton4", StringComparison.OrdinalIgnoreCase)) return "Stanton (microTech)";
        if (recordName.Contains("Pyro", StringComparison.OrdinalIgnoreCase)) return "Pyro";
        return "";
    }

    private static string CleanMissionType(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (!raw.Contains('/')) return raw;
        var fileName = raw.Split('/').Last();
        if (fileName.EndsWith(".json")) fileName = fileName[..^5];
        if (fileName.StartsWith("missiontype.")) fileName = fileName[12..];
        return fileName;
    }

    private static string GetStr(JsonElement el, string prop, string fallback = "")
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString() ?? fallback;
        return fallback;
    }

    private static double GetNum(JsonElement el, string prop, double fallback = 0)
    {
        if (el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number)
            return v.GetDouble();
        return fallback;
    }

    public async Task<string?> QueryGameDataAsync(string query)
    {
        var p4kPath = FindDataP4k();
        if (p4kPath == null || !IsStarBreakerInstalled) return null;

        var sb = new StringBuilder();
        var tasks = new List<Task<string?>>();

        var shipName = ChatService.ExtractShipNamePublic(query);
        if (!string.IsNullOrEmpty(shipName))
            tasks.Add(QueryShipAsync(p4kPath, shipName));

        var itemKw = ExtractGameDataKeyword(query);
        if (!string.IsNullOrEmpty(itemKw))
        {
            var compType = DetectComponentType(query);
            tasks.Add(QueryItemAsync(p4kPath, itemKw, compType));
        }

        if (ContainsMissionKeyword(query))
            tasks.Add(QueryMissionsInternalAsync(p4kPath, query));

        if (ContainsCommodityKeyword(query))
            tasks.Add(QueryCommoditiesAsync(query));

        if (tasks.Count == 0) return null;

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
            if (!string.IsNullOrEmpty(r)) sb.AppendLine(r);

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void AddField(List<string> parts, JsonElement rv, string fieldName, string label)
    {
        if (rv.TryGetProperty(fieldName, out var val) && val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString() ?? "";
            if (!string.IsNullOrEmpty(s)) parts.Add($"{label}: {s}");
        }
    }

    private static void AddNumericField(List<string> parts, JsonElement rv, string fieldName, string label)
    {
        if (rv.TryGetProperty(fieldName, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number)
            {
                var n = val.GetDouble();
                if (n > 0) parts.Add($"{label}: {n:0} aUEC");
            }
            else if (val.ValueKind == JsonValueKind.String)
            {
                var s = val.GetString() ?? "";
                if (!string.IsNullOrEmpty(s) && s != "0") parts.Add($"{label}: {s}");
            }
        }
    }

    private static bool ContainsMissionKeyword(string query)
    {
        var keywords = new[] { "ミッション", "契約", "mission", "contract", "bounty" };
        foreach (var kw in keywords)
            if (query.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool ContainsCommodityKeyword(string query)
    {
        var keywords = new[] { "コモディティ", "商品", "資源", "commodity", "resource", "cargo", "貿易", "交易", "trade" };
        foreach (var kw in keywords)
            if (query.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static string? DetectComponentType(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"シールド", "SCItemShieldGeneratorParams"}, {"shield", "SCItemShieldGeneratorParams"},
            {"クォンタムドライブ", "SCItemQuantumDriveParams"}, {"quantum_drive", "SCItemQuantumDriveParams"},
            {"quantum drive", "SCItemQuantumDriveParams"}, {"QD", "SCItemQuantumDriveParams"},
            {"パワープラント", "SCItemPowerPlantParams"}, {"power_plant", "SCItemPowerPlantParams"},
            {"power plant", "SCItemPowerPlantParams"}, {"powerplant", "SCItemPowerPlantParams"},
            {"クーラー", "SCItemCoolerParams"}, {"cooler", "SCItemCoolerParams"},
            {"武器", "SCItemWeaponComponentParams"}, {"weapon", "SCItemWeaponComponentParams"},
            {"gun", "SCItemWeaponComponentParams"}, {"cannon", "SCItemWeaponComponentParams"},
            {"リピーター", "SCItemWeaponComponentParams"}, {"ガトリング", "SCItemWeaponComponentParams"},
            {"ミサイル", "SAmmoContainerComponentParams"}, {"missile", "SAmmoContainerComponentParams"},
        };
        foreach (var (kw, comp) in map)
            if (query.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return comp;
        return null;
    }

    public async Task<string?> QueryMissionsAsync(string query)
    {
        var p4kPath = FindDataP4k();
        if (p4kPath == null || !IsStarBreakerInstalled) return null;
        return await QueryMissionsInternalAsync(p4kPath, query);
    }

    private async Task<string?> QueryMissionsInternalAsync(string p4kPath, string query)
    {
        var filter = string.IsNullOrWhiteSpace(query) ? "*" : $"*{query.Replace(" ", "*")}*";
        var cacheKey = $"missions:{filter}";
        var cached = GetCache(cacheKey);
        if (cached != null) return cached;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sb = new StringBuilder("=== ゲームファイル (Data.p4k): ミッション/契約 ===\n");
        int found = 0;

        try
        {
            var json = await RunDcbQueryRawAsync(p4kPath, "MissionBrokerEntry", filter, cts.Token);
            if (!string.IsNullOrEmpty(json))
            {
                foreach (var block in SplitJsonBlocks(json))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(block);
                        var root = doc.RootElement;
                        var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(recordName)) continue;

                        var parts = new List<string> { recordName };
                        if (root.TryGetProperty("_RecordValue_", out var rv))
                        {
                            AddField(parts, rv, "title", "タイトル");
                            AddField(parts, rv, "titleHUD", "HUDタイトル");
                            AddField(parts, rv, "type", "種別");
                            AddField(parts, rv, "difficulty", "難易度");
                            AddField(parts, rv, "missionGiver", "依頼者");
                            AddField(parts, rv, "commsChannelName", "通信チャンネル");
                            AddField(parts, rv, "locationLabel", "場所");
                            AddField(parts, rv, "description", "説明");
                            AddNumericField(parts, rv, "reward", "報酬");
                            AddNumericField(parts, rv, "rewardMin", "最低報酬");
                            AddNumericField(parts, rv, "rewardMax", "最高報酬");
                            AddNumericField(parts, rv, "payout", "報酬");
                            AddNumericField(parts, rv, "payoutMin", "最低報酬");
                            AddNumericField(parts, rv, "payoutMax", "最高報酬");
                            AddField(parts, rv, "requiredReputation", "必要評判");
                            AddField(parts, rv, "minReputation", "最低評判");
                            AddField(parts, rv, "lawfulnessType", "合法性");
                            AddField(parts, rv, "jurisdiction", "管轄");
                            AddField(parts, rv, "deadline", "期限");
                            AddField(parts, rv, "timeLimit", "制限時間");
                        }
                        sb.AppendLine($"- {string.Join(" | ", parts)}");
                        if (++found >= 50) break;
                    }
                    catch { }
                }
            }
        }
        catch (OperationCanceledException) { sb.AppendLine("(タイムアウト: 検索キーワードを絞ってください)"); }

        var result = found > 0 ? sb.ToString() : null;
        if (result != null) SetCache(cacheKey, result);
        return result;
    }

    private async Task<string?> QueryShipAsync(string p4kPath, string shipName)
    {
        var cacheKey = $"ship:{shipName}";
        var cached = GetCache(cacheKey);
        if (cached != null) return cached;

        var filter = "*" + shipName.Replace(" ", "_").Replace("-", "_") + "*";
        var rawJson = await RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition", filter);
        if (string.IsNullOrEmpty(rawJson)) return null;

        var result = ParseShipRecords(shipName, rawJson);
        if (!string.IsNullOrEmpty(result))
            SetCache(cacheKey, result);

        return result;
    }

    private async Task<string?> QueryItemAsync(string p4kPath, string keyword, string? componentType = null)
    {
        var cacheKey = $"item:{keyword}:{componentType ?? "any"}";
        var cached = GetCache(cacheKey);
        if (cached != null) return cached;

        var filter = "*" + keyword + "*";
        var rawJson = await RunDcbQueryRawAsync(p4kPath, "EntityClassDefinition", filter);

        if (!string.IsNullOrEmpty(rawJson) && !string.IsNullOrEmpty(componentType))
        {
            var result = ParseItemRecordsWithComponent(keyword, rawJson, componentType);
            if (!string.IsNullOrEmpty(result))
                SetCache(cacheKey, result);
            return result;
        }

        if (string.IsNullOrEmpty(rawJson)) return null;

        var genericResult = ParseItemRecords(keyword, rawJson);
        if (!string.IsNullOrEmpty(genericResult))
            SetCache(cacheKey, genericResult);

        return genericResult;
    }

    public async Task<string?> QueryCommoditiesAsync(string query)
    {
        var p4kPath = FindDataP4k();
        if (p4kPath == null || !IsStarBreakerInstalled) return null;

        var cacheKey = $"commodity:{query}";
        var cached = GetCache(cacheKey);
        if (cached != null) return cached;

        var filter = string.IsNullOrEmpty(query) ? "*" : $"*{query}*";
        var rawJson = await RunDcbQueryRawAsync(p4kPath, "CommoditySubtype", filter);
        if (string.IsNullOrEmpty(rawJson)) return null;

        var sb = new StringBuilder("=== ゲームファイル (Data.p4k): コモディティ ===\n");
        int found = 0;
        foreach (var block in SplitJsonBlocks(rawJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(recordName)) continue;

                var parts = new List<string> { recordName };
                if (root.TryGetProperty("_RecordValue_", out var rv))
                {
                    var name = rv.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var symbol = rv.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                    var volatility = rv.TryGetProperty("volatility", out var v) ? v.ToString() : "";
                    if (!string.IsNullOrEmpty(name)) parts.Add($"名前: {name}");
                    if (!string.IsNullOrEmpty(symbol)) parts.Add($"シンボル: {symbol}");
                    if (!string.IsNullOrEmpty(volatility)) parts.Add($"変動性: {volatility}");
                }
                sb.AppendLine($"- {string.Join(" | ", parts)}");
                if (++found >= 50) break;
            }
            catch { }
        }

        var result = found > 0 ? sb.ToString() : null;
        if (result != null) SetCache(cacheKey, result);
        return result;
    }

    private string? ParseShipRecords(string shipName, string jsonOutput)
    {
        var sb = new StringBuilder($"=== ゲームファイル (Data.p4k): {shipName} ===\n");
        int found = 0;

        foreach (var block in SplitJsonBlocks(jsonOutput))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";

                if (!root.TryGetProperty("_RecordValue_", out var rv) ||
                    !rv.TryGetProperty("Components", out var components)) continue;

                var parts = new List<string>();
                if (components.ValueKind != JsonValueKind.Array) continue;
                foreach (var comp in components.EnumerateArray())
                {
                    // Components には null やオブジェクトでない要素が入っている record がある。読まずに飛ばす
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";

                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                    {
                        var itemType = ad.TryGetProperty("Type", out var it) ? it.GetString() ?? "" : "";
                        var size = ad.TryGetProperty("Size", out var sz) ? sz.GetInt32() : 0;
                        var locName = "";
                        if (ad.TryGetProperty("Localization", out var loc))
                            locName = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";

                        if (!string.IsNullOrEmpty(itemType) && itemType != "UNDEFINED")
                            parts.Add($"  - {itemType} (Size {size}): {locName}");
                    }

                    if (type == "VehicleComponentParams")
                    {
                        var career = comp.TryGetProperty("vehicleCareer", out var c) ? c.GetString() : "";
                        var role = comp.TryGetProperty("vehicleRole", out var r) ? r.GetString() : "";
                        var crew = comp.TryGetProperty("crewSize", out var cr) ? cr.ToString() : "";
                        if (!string.IsNullOrEmpty(career)) parts.Add($"  職業: {career}");
                        if (!string.IsNullOrEmpty(role)) parts.Add($"  役割: {role}");
                        if (!string.IsNullOrEmpty(crew)) parts.Add($"  乗員: {crew}");
                    }
                }

                if (parts.Count > 0)
                {
                    sb.AppendLine($"\n【{recordName}】");
                    foreach (var p in parts) sb.AppendLine(p);
                    found++;
                }
            }
            catch { }
        }

        return found > 0 ? sb.ToString() : null;
    }

    private string? ParseItemRecords(string keyword, string jsonOutput)
    {
        var sb = new StringBuilder($"=== ゲームファイル アイテム: {keyword} ===\n");
        int found = 0;

        foreach (var block in SplitJsonBlocks(jsonOutput))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;

                if (!root.TryGetProperty("_RecordValue_", out var rv) ||
                    !rv.TryGetProperty("Components", out var components)) continue;

                string itemName = "", itemType = "";
                int size = 0, grade = 0;
                if (components.ValueKind != JsonValueKind.Array) continue;

                foreach (var comp in components.EnumerateArray())
                {
                    // Components には null やオブジェクトでない要素が入っている record がある。読まずに飛ばす
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                    {
                        itemType = ad.TryGetProperty("Type", out var it) ? it.GetString() ?? "" : "";
                        size = ad.TryGetProperty("Size", out var sz) ? sz.GetInt32() : 0;
                        grade = ad.TryGetProperty("Grade", out var g) ? g.GetInt32() : 0;
                        if (ad.TryGetProperty("Localization", out var loc))
                            itemName = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    }
                }

                if (!string.IsNullOrEmpty(itemType) && itemType != "UNDEFINED")
                {
                    sb.AppendLine($"- {itemName} | タイプ: {itemType} | サイズ: {size} | グレード: {grade}");
                    if (++found >= 20) break;
                }
            }
            catch { }
        }

        return found > 0 ? sb.ToString() : null;
    }

    private string? ParseItemRecordsWithComponent(string keyword, string jsonOutput, string componentType)
    {
        var sb = new StringBuilder($"=== ゲームファイル アイテム: {keyword} ({componentType}) ===\n");
        int found = 0;

        foreach (var block in SplitJsonBlocks(jsonOutput))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                var root = doc.RootElement;
                var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";

                if (!root.TryGetProperty("_RecordValue_", out var rv) ||
                    !rv.TryGetProperty("Components", out var components)) continue;

                bool hasTargetComponent = false;
                string itemName = "", itemType = "";
                int size = 0, grade = 0;
                var extraProps = new List<string>();
                if (components.ValueKind != JsonValueKind.Array) continue;

                foreach (var comp in components.EnumerateArray())
                {
                    // Components には null やオブジェクトでない要素が入っている record がある。読まずに飛ばす
                    if (comp.ValueKind != JsonValueKind.Object) continue;
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";

                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                    {
                        itemType = ad.TryGetProperty("Type", out var it) ? it.GetString() ?? "" : "";
                        size = ad.TryGetProperty("Size", out var sz) ? sz.GetInt32() : 0;
                        grade = ad.TryGetProperty("Grade", out var g) ? g.GetInt32() : 0;
                        if (ad.TryGetProperty("Localization", out var loc))
                            itemName = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                    }

                    if (type == componentType)
                    {
                        hasTargetComponent = true;
                        if (type == "SCItemShieldGeneratorParams")
                        {
                            var maxHp = comp.TryGetProperty("MaxShieldHealth", out var mh) ? mh.ToString() : "";
                            var regen = comp.TryGetProperty("MaxShieldRegen", out var mr) ? mr.ToString() : "";
                            if (!string.IsNullOrEmpty(maxHp)) extraProps.Add($"最大HP: {maxHp}");
                            if (!string.IsNullOrEmpty(regen)) extraProps.Add($"再生: {regen}");
                        }
                        else if (type == "SCItemQuantumDriveParams")
                        {
                            var fuel = comp.TryGetProperty("quantumFuelRequirement", out var f) ? f.ToString() : "";
                            var range = comp.TryGetProperty("jumpRange", out var r) ? r.ToString() : "";
                            var spool = comp.TryGetProperty("spoolUpTime", out var s) ? s.ToString() : "";
                            if (!string.IsNullOrEmpty(fuel)) extraProps.Add($"燃料: {fuel}");
                            if (!string.IsNullOrEmpty(range)) extraProps.Add($"距離: {range}");
                            if (!string.IsNullOrEmpty(spool)) extraProps.Add($"スプール: {spool}");
                        }
                        else if (type == "SCItemWeaponComponentParams")
                        {
                            var fireRate = comp.TryGetProperty("fireRate", out var fr) ? fr.ToString() : "";
                            if (!string.IsNullOrEmpty(fireRate)) extraProps.Add($"発射速度: {fireRate}");
                        }
                        else if (type == "SAmmoContainerComponentParams")
                        {
                            var maxAmmo = comp.TryGetProperty("maxAmmoCount", out var ma) ? ma.ToString() : "";
                            if (!string.IsNullOrEmpty(maxAmmo)) extraProps.Add($"弾数: {maxAmmo}");
                        }
                    }
                }

                if (!hasTargetComponent) continue;
                if (string.IsNullOrEmpty(itemType) || itemType == "UNDEFINED") continue;

                var line = $"- {itemName} | タイプ: {itemType} | サイズ: {size} | グレード: {grade}";
                if (extraProps.Count > 0) line += " | " + string.Join(" | ", extraProps);
                sb.AppendLine(line);
                if (++found >= 30) break;
            }
            catch { }
        }

        return found > 0 ? sb.ToString() : null;
    }

    private static IEnumerable<string> FindNamedBlocks(string output, string recordPrefix)
    {
        var marker = $"\"_RecordName_\": \"{recordPrefix}";
        int searchPos = 0;
        while ((searchPos = output.IndexOf(marker, searchPos, StringComparison.Ordinal)) >= 0)
        {
            int start = searchPos;
            while (start > 0 && output[start] != '{') start--;

            int depth = 0;
            int end = -1;
            for (int i = start; i < output.Length; i++)
            {
                if (output[i] == '{') depth++;
                else if (output[i] == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
            }

            if (end > start)
            {
                yield return output[start..(end + 1)];
                searchPos = end + 1;
            }
            else
            {
                searchPos++;
            }
        }
    }

    private static IEnumerable<string> SplitJsonBlocks(string output)
    {
        int depth = 0;
        int start = -1;
        for (int i = 0; i < output.Length; i++)
        {
            if (output[i] == '{')
            {
                if (depth == 0) start = i;
                depth++;
            }
            else if (output[i] == '}')
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    yield return output[start..(i + 1)];
                    start = -1;
                }
            }
        }
    }

    private async Task<List<string>> RunDcbQueryNamesAsync(string p4kPath, string recordType, string filter, CancellationToken ct = default)
    {
        var output = await RunDcbQueryRawAsync(p4kPath, recordType, filter, ct);
        var names = new List<string>();
        foreach (var block in SplitJsonBlocks(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(block);
                if (doc.RootElement.TryGetProperty("_RecordName_", out var rn))
                    names.Add(rn.GetString() ?? "");
            }
            catch { }
        }
        return names;
    }

    private async Task<string> RunDcbQueryWithTimerAsync(string p4kPath, string recordType, string filter, string statusPrefix, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        long readChars = 0;
        using var timerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var timerTask = Task.Run(async () =>
        {
            while (!timerCts.Token.IsCancellationRequested)
            {
                var mb = Volatile.Read(ref readChars) * 2 / 1024 / 1024;
                StatusChanged?.Invoke(mb > 0
                    ? $"{statusPrefix} — {mb}MB読み込み中 ({sw.Elapsed.TotalSeconds:F0}秒経過)"
                    : $"{statusPrefix} — 処理中... ({sw.Elapsed.TotalSeconds:F0}秒経過)");
                try { await Task.Delay(1000, timerCts.Token); } catch { break; }
            }
        }, timerCts.Token);

        var result = await RunDcbQueryRawAsync(p4kPath, recordType, filter, ct, totalChars =>
        {
            Volatile.Write(ref readChars, totalChars);
        });

        timerCts.Cancel();
        try { await timerTask; } catch (OperationCanceledException) { }

        if (result.Length > 0)
            StatusChanged?.Invoke($"{statusPrefix} — {result.Length * 2 / 1024 / 1024}MB取得完了 ({sw.Elapsed.TotalSeconds:F1}秒)");

        return result;
    }

    private async Task<string> RunDcbQueryRawAsync(string p4kPath, string recordType, string filter, CancellationToken ct = default, Action<long>? onProgress = null)
    {
        // 構築の前の段階で同じ問い合わせをしていれば、置いておいた出力を使う (RegisterReuseQueries)
        if (TryReadCachedQuery(recordType, filter) is { } cached)
        {
            onProgress?.Invoke(cached.Length);
            return cached;
        }
        var psi = new ProcessStartInfo
        {
            FileName = StarBreakerExe,
            // 構築中は最初に取り出した Game2.dcb を読む (UseExtractedDcbAsync)
            Arguments = _dcbPath is { } dcb
                ? $"dcb query \"{recordType}\" --dcb \"{dcb}\" --filter \"{filter}\""
                : $"dcb query \"{recordType}\" --p4k \"{p4kPath}\" --filter \"{filter}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)!;
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };
        proc.BeginErrorReadLine();
        var sb = new StringBuilder();
        var buffer = new char[65536];
        int bytesRead;
        long totalChars = 0;
        try
        {
            while ((bytesRead = await proc.StandardOutput.ReadAsync(buffer, ct)) > 0)
            {
                sb.Append(buffer, 0, bytesRead);
                totalChars += bytesRead;
                onProgress?.Invoke(totalChars);
            }
        }
        catch (OperationCanceledException)
        {
            // 取り消されたら starbreaker を止めてから取り消しの例外を上げる
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await WaitStarBreakerExitAsync(proc, ct);
        // 終了コードが 0 でなければ知らせる。ただし当てはまる record が無いとき (starbreaker は終了コード 1 で
        // "error: no ... records matching filter" を出す) は失敗ではないので知らせない。
        // 呼び出し側は今までどおり取れた出力 (失敗なら空) で続ける (例外にはしない)
        if (proc.ExitCode != 0)
        {
            string err;
            lock (stderr) err = stderr.ToString();
            if (!err.Contains("records matching filter", StringComparison.Ordinal))
                StatusChanged?.Invoke($"starbreaker dcb query が失敗しました (終了コード {proc.ExitCode}, {recordType} / {filter}): {StderrTail(stderr)}");
        }
        var output = sb.ToString();
        // 失敗したもの (終了コードが 0 でない) は置いておかない (後の段階では今までどおり問い合わせ直す)
        if (proc.ExitCode == 0 && output.Length > 0) TryWriteCachedQuery(recordType, filter, output);
        return output;
    }

    private async Task<string> RunDcbQueryAsync(string p4kPath, string path, string filter, CancellationToken ct = default)
    {
        return await RunDcbQueryRawAsync(p4kPath, path, filter, ct);
    }

    // DROP → CREATE → INSERT を 1 つのトランザクションで行う (途中で失敗しても前の item_index_fts が残り、全文検索の表が欠けない)
    public void RebuildFts5Index()
    {
        EnsureDb();
        RebuildFts5Index(_db!);
    }

    private static void RebuildFts5Index(SqliteConnection db)
    {
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DROP TABLE IF EXISTS item_index_fts";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE VIRTUAL TABLE item_index_fts USING fts5(uuid UNINDEXED, name, name_ja, record_name, item_type, sub_type, manufacturer, tokenize='trigram')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO item_index_fts(uuid, name, name_ja, record_name, item_type, sub_type, manufacturer) SELECT uuid, name, COALESCE(name_ja,''), record_name, item_type, sub_type, manufacturer FROM item_index";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    // ゲームのフォルダの data/Localization/<言語>/global.ini の場所 (ExtractItemIndexAsync が読む ini)。
    // ゲームのフォルダの下に無ければ、その 1 つ上のフォルダの下 (今までの ExtractItemIndexAsync と同じ順)
    internal static string GameLocalizationIniPath(string gamePath, string language)
    {
        var path = Path.Combine(gamePath, "data", "Localization", language, "global.ini");
        if (!File.Exists(path))
            path = Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "data", "Localization", language, "global.ini");
        return path;
    }

    // item_index.name をどこから作ったか (gamedata_meta。ExtractItemIndexAsync と RefreshItemNamesFromTranslations が書く)。
    //   english: ゲームのフォルダの english の ini (ある環境では name は英語のまま。訳を直しても作り直さない)
    //   translations: 今の訳から作ったゲーム用 ini の値 (IniMerger.MergeForItemNames)
    //   japanese_ini: ゲームのフォルダの japanese の ini (前回このアプリが書き出したもの。訳から作れなかったとき)
    // 記録の無い DB (この記録を書く前に構築したもの) は、今ゲームのフォルダに english の ini があるかで見分ける (構築と同じ場所を見る)
    internal const string ItemNameSourceMetaKey = "item_index_name_source";
    internal const string ItemNameSourceEnglish = "english";
    internal const string ItemNameSourceTranslations = "translations";
    internal const string ItemNameSourceJapaneseIni = "japanese_ini";

    private static string? ReadMetaValue(SqliteConnection db, string key)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM gamedata_meta WHERE key = @k";
            cmd.Parameters.AddWithValue("@k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (SqliteException) { return null; }   // gamedata_meta が無い DB
    }

    // item_index の名前を今の訳に合わせる。
    //   name: 名前のローカライズキー (name_key) を持つ品目の name を、［日本語化］の［実行］がゲーム用 ini に書き出す値
    //     (訳 → 用語集の置き換え → 廃墟と橙の置き換え → 分類の印。IniMerger.Merge) にする (ExtractItemIndexAsync が english の ini の無い環境で作る名前と同じ)。
    //     merged は Merge の結果 (null なら作業フォルダの材料で作る)。ゲーム用 ini の値が引けない品目の name は変えない。
    //     english の ini から作った name (ItemNameSourceMetaKey) と、材料 (抽出済みの ini) が無いときは変えない。
    //   name_ja: 作業フォルダの translations.db で PopulateJapaneseNames と同じ付け直しをする (name を変えなかったときも行う)
    // どちらかが 1 件でも変わったら item_index_fts を作り直す。
    // 返り値: 変わった件数 (name と name_ja の合計)。どちらも行えなかった (表が無い・translations.db も材料も無い) ときは -1
    public static int RefreshItemNamesFromTranslations(string gamedataDbPath, string workDir,
        Dictionary<string, string>? merged = null, Action<string>? log = null)
    {
        if (!File.Exists(gamedataDbPath)) return -1;
        using var db = new SqliteConnection($"Data Source={gamedataDbPath};Pooling=False");
        db.Open();

        int nameChanged = RefreshItemIndexNames(db, workDir, merged, log);

        int jaChanged = -1;
        var translationDbPath = Path.Combine(workDir, "translations.db");
        if (File.Exists(translationDbPath))
        {
            try
            {
                var (updated, changed, _, _, _) = PopulateJapaneseNamesCore(db, translationDbPath);
                jaChanged = changed;
                log?.Invoke($"日本語名 (name_ja) を今の訳で付け直しました (当たった {updated} 件、変わった {changed} 件)");
            }
            catch (SqliteException ex) { log?.Invoke($"日本語名 (name_ja) を付け直せませんでした ({ex.Message})"); }
        }

        if (nameChanged < 0 && jaChanged < 0) return -1;
        var total = Math.Max(nameChanged, 0) + Math.Max(jaChanged, 0);
        if (total > 0)
        {
            RebuildFts5Index(db);
            log?.Invoke("全文検索の表を作り直しました");
        }
        return total;
    }

    // RefreshItemNamesFromTranslations の name の部分。返り値: 変えた件数。行わなかったときは -1
    private static int RefreshItemIndexNames(SqliteConnection db, string workDir, Dictionary<string, string>? merged, Action<string>? log)
    {
        var source = ReadMetaValue(db, ItemNameSourceMetaKey);
        if (source == ItemNameSourceEnglish
            || (source == null && File.Exists(GameLocalizationIniPath(App.Config.GamePath, "english"))))
        {
            log?.Invoke("品目名は english の ini から作ったものなので、作り直しません");
            return -1;
        }

        merged ??= IniMerger.MergeForItemNames(workDir);
        if (merged == null)
        {
            log?.Invoke("作業フォルダに抽出済みの global.ini が無いので、品目名を作り直せません");
            return -1;
        }

        var updates = new List<(string Uuid, string Name)>();
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT uuid, name_key, name FROM item_index WHERE name_key IS NOT NULL AND name_key != ''";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var uuid = r.GetString(0);
                var key = r.GetString(1);
                var name = r.IsDBNull(2) ? "" : r.GetString(2);
                if (IniMerger.TryGetIniValue(merged, key, out var newName) && newName != name)
                    updates.Add((uuid, newName));
            }
        }
        catch (SqliteException ex)
        {
            // item_index (か name_key の列) が無い DB
            log?.Invoke($"item_index を読めないので、品目名を作り直せません ({ex.Message})");
            return -1;
        }

        using (var tx = db.BeginTransaction())
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE item_index SET name = @n WHERE uuid = @u";
            var pN = cmd.Parameters.Add("@n", SqliteType.Text);
            var pU = cmd.Parameters.Add("@u", SqliteType.Text);
            foreach (var (uuid, name) in updates)
            {
                pN.Value = name;
                pU.Value = uuid;
                cmd.ExecuteNonQuery();
            }
            using var meta = db.CreateCommand();
            meta.Transaction = tx;
            meta.CommandText = "CREATE TABLE IF NOT EXISTS gamedata_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL); INSERT OR REPLACE INTO gamedata_meta(key, value) VALUES(@k, @v)";
            meta.Parameters.AddWithValue("@k", ItemNameSourceMetaKey);
            meta.Parameters.AddWithValue("@v", ItemNameSourceTranslations);
            meta.ExecuteNonQuery();
            tx.Commit();
        }
        log?.Invoke(updates.Count > 0 ? $"品目名を今の訳に合わせて {updates.Count} 件直しました" : "品目名は今の訳と同じでした");
        return updates.Count;
    }

    // 品目名 (item_index.name / name_ja) を作り直したことの知らせ。品目名を覚えている画面 (［詳細］・武器と装備の表など) が受け、
    // 次に表示するときに引き直す。MainWindow が UI のスレッドで出す
    public static event Action? ItemNamesChanged;
    public static void NotifyItemNamesChanged() => ItemNamesChanged?.Invoke();

    // item_index.name_ja を translations.db の item_Name 行の訳で埋める。
    //   1. 名前のローカライズキー (item_index.name_key) と translations.key の一致。キーそのもの、無ければ "キー,P" (GameDataQueryService.LookupLoc と同じ順)。
    //      それぞれ大文字小文字まで同じものを先に、無ければ大文字小文字を区別せずに引く (ExtractItemIndexAsync の ini の辞書が区別しないのに合わせる)
    //   2. キーで見つからない品目だけ、英語名 (item_index.name) と translations.english の完全一致 (大文字小文字は区別しない)。
    //      同じ英語名に違う訳が複数あるときは、読み出し順で後の行の訳 (以前の UPDATE を行ごとに繰り返す書き方で後の行が上書きしていたのと同じ)
    //   どちらでも見つからない品目の name_ja は変えない (ExtractItemIndexAsync が公式の日本語 ini から入れた値のまま)
    public void PopulateJapaneseNames(string translationDbPath)
    {
        EnsureDb();
        if (!File.Exists(translationDbPath)) return;
        var (updated, _, byKeyCount, byEnglishCount, notFound) = PopulateJapaneseNamesCore(_db!, translationDbPath);
        RebuildFts5Index();
        StatusChanged?.Invoke($"日本語名を {updated} 件更新し (キーで {byKeyCount} 件、英語名で {byEnglishCount} 件。見つからない {notFound} 件は変更なし)、FTS5 インデックスを再構築しました");
    }

    // PopulateJapaneseNames の中身 (全文検索の表は作り直さない)。返り値: 当たった件数、そのうち値が変わった件数、キーで・英語名で当たった件数、見つからない件数
    private static (int Updated, int Changed, int ByKey, int ByEnglish, int NotFound) PopulateJapaneseNamesCore(SqliteConnection db, string translationDbPath)
    {
        var byKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var byKeyIgnoreCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var byEnglish = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var transConn = new SqliteConnection($"Data Source={translationDbPath};Mode=ReadOnly"))
        {
            transConn.Open();
            using var readCmd = transConn.CreateCommand();
            readCmd.CommandText = "SELECT key, english, japanese FROM translations WHERE key LIKE 'item_Name%' AND japanese IS NOT NULL AND japanese != ''";
            using var reader = readCmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var english = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var ja = reader.GetString(2);
                // 公式の日本語ファイルの「訳が無い」印 ("! JAPANESE_(JAPAN) TRANSLATION NOT FOUND FOR LOCID: ... !") は name_ja に入れない
                // (ExtractItemIndexAsync の StartsWith("!") と IniMerger の TranslationDatabase.IsMissingTranslationMarker の判定に合わせる)
                if (ja.StartsWith("!") || TranslationDatabase.IsMissingTranslationMarker(ja)) continue;
                byKey[key] = ja;
                byKeyIgnoreCase[key] = ja;
                if (!string.IsNullOrEmpty(english)) byEnglish[english] = ja;
            }
        }

        string? LookupKey(string k)
            => byKey.TryGetValue(k, out var v) ? v : byKeyIgnoreCase.TryGetValue(k, out var vi) ? vi : null;

        var updates = new List<(string Uuid, string Ja)>();
        int byKeyCount = 0, byEnglishCount = 0, notFound = 0, changed = 0;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT uuid, name_key, name, name_ja FROM item_index";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var uuid = reader.GetString(0);
                var nameKey = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var name = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var currentJa = reader.IsDBNull(3) ? null : reader.GetString(3);
                string? ja = null;
                if (nameKey.Length > 0)
                    ja = LookupKey(nameKey) ?? LookupKey(nameKey + ",P");
                if (ja == null && name.Length > 0 && byEnglish.TryGetValue(name, out var jaEn)) { byEnglishCount++; ja = jaEn; }
                else if (ja != null) byKeyCount++;
                else { notFound++; continue; }
                updates.Add((uuid, ja));
                if (ja != currentJa) changed++;
            }
        }

        using (var tx = db.BeginTransaction())
        using (var updateCmd = db.CreateCommand())
        {
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE item_index SET name_ja = @ja WHERE uuid = @u";
            var pJa = updateCmd.Parameters.Add("@ja", SqliteType.Text);
            var pU = updateCmd.Parameters.Add("@u", SqliteType.Text);
            foreach (var (uuid, ja) in updates)
            {
                pJa.Value = ja;
                pU.Value = uuid;
                updateCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return (updates.Count, changed, byKeyCount, byEnglishCount, notFound);
    }

    public int GetItemIndexCount()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_index";
        try { return (int)(long)(cmd.ExecuteScalar() ?? 0L); }
        catch { return 0; }
    }

    public bool HasVectorIndex()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_vectors";
        try { return (long)(cmd.ExecuteScalar() ?? 0L) > 0; }
        catch { return false; }
    }

    public async Task BuildVectorIndexAsync(BackendConfig backend, CancellationToken ct = default)
    {
        EnsureDb();
        var totalItems = GetItemIndexCount();
        if (totalItems == 0) throw new InvalidOperationException("item_index が空です。先にインデックス構築を実行してください。");

        StatusChanged?.Invoke("ベクトルインデックス構築中...");
        ProgressChanged?.Invoke(0, "アイテム名を読み込み中...");

        // Collect existing UUIDs to skip (resume support)
        var existingUuids = new HashSet<string>();
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "SELECT uuid FROM item_vectors";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                existingUuids.Add(reader.GetString(0));
        }

        var items = new List<(string uuid, string text)>();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = "SELECT uuid, COALESCE(name,'') || ' ' || COALESCE(name_ja,'') || ' ' || COALESCE(item_type,'') || ' ' || COALESCE(manufacturer,'') FROM item_index";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var uuid = reader.GetString(0);
                if (!existingUuids.Contains(uuid))
                    items.Add((uuid, reader.GetString(1).Trim()));
            }
        }

        if (items.Count == 0)
        {
            StatusChanged?.Invoke($"ベクトルインデックスは既に完了しています ({existingUuids.Count}件)");
            ProgressChanged?.Invoke(100, "完了済み");
            return;
        }

        int skipped = existingUuids.Count;
        int total = skipped + items.Count;
        if (skipped > 0)
            StatusChanged?.Invoke($"既存 {skipped}件をスキップ、残り {items.Count}件を処理");

        int batchSize = backend.Type.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ? 50 : 200;
        int processed = 0;
        int errors = 0;
        const int maxRetries = 3;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < items.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = items.Skip(i).Take(batchSize).ToList();
            var texts = batch.Select(b => b.text).ToList();

            List<float[]>? embeddings = null;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                try
                {
                    embeddings = await GetEmbeddingsAsync(backend, texts, ct);
                    if (embeddings != null && embeddings.Count == texts.Count) break;
                }
                catch (HttpRequestException) when (retry < maxRetries - 1)
                {
                    StatusChanged?.Invoke($"リトライ {retry + 1}/{maxRetries} (batch {i / batchSize + 1})");
                    await Task.Delay(2000 * (retry + 1), ct);
                }
            }

            if (embeddings == null || embeddings.Count != texts.Count)
            {
                errors += batch.Count;
                StatusChanged?.Invoke($"エンベディング取得エラー (batch {i / batchSize + 1}), スキップして続行");
                continue;
            }

            // Commit per batch so progress is saved even if later batches fail
            using var tx = _db.BeginTransaction();
            using var insertCmd = _db.CreateCommand();
            insertCmd.CommandText = "INSERT OR REPLACE INTO item_vectors(uuid, embedding) VALUES(@uuid, @emb)";
            var pUuid = insertCmd.Parameters.Add("@uuid", SqliteType.Text);
            var pEmb = insertCmd.Parameters.Add("@emb", SqliteType.Blob);

            for (int j = 0; j < batch.Count; j++)
            {
                pUuid.Value = batch[j].uuid;
                pEmb.Value = FloatsToBytes(embeddings[j]);
                insertCmd.ExecuteNonQuery();
            }
            tx.Commit();

            processed += batch.Count;
            var pct = (skipped + processed) * 100 / total;
            var eta = sw.Elapsed.TotalSeconds / processed * (items.Count - processed - errors);
            ProgressChanged?.Invoke(pct, $"ベクトル化中... {skipped + processed}/{total} (残り約{eta:F0}秒)");
        }

        SetMeta("vector_model", $"{backend.Type}:{backend.Model}");
        SetMeta("vector_count", (skipped + processed).ToString());
        SetMeta("vector_built_at", DateTime.UtcNow.ToString("o"));

        sw.Stop();
        var msg = $"ベクトルインデックス完了 ({skipped + processed}/{total}件, {sw.Elapsed.TotalSeconds:F1}秒)";
        if (errors > 0) msg += $" ※{errors}件エラー";
        ProgressChanged?.Invoke(100, msg);
        StatusChanged?.Invoke(msg);
    }

    // 埋め込み専用のモデル名 (チャット用の Model は埋め込みに使えないため、種類ごとにここで固定する)
    // Gemini: https://ai.google.dev/gemini-api/docs/embeddings ・ text-embedding-004 は 2026-01-14 に提供終了 (https://ai.google.dev/gemini-api/docs/deprecations)
    // OpenAI: https://developers.openai.com/api/docs/guides/embeddings
    public const string OllamaEmbeddingModel = "nomic-embed-text";
    public const string OpenAiEmbeddingModel = "text-embedding-3-small";
    public const string GeminiEmbeddingModel = "gemini-embedding-001";

    // ベクトル構築 (MainWindow) とチャットの検索語の埋め込み (ChatService) の両方がこれでモデルを決める。
    // 構築と検索でモデルが違うとベクトルの次元が合わないため、必ずここを通す。対応外の種類は null
    public static string? GetEmbeddingModelName(string backendType)
    {
        if (backendType.Equals("Ollama", StringComparison.OrdinalIgnoreCase)) return OllamaEmbeddingModel;
        if (backendType.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)) return OpenAiEmbeddingModel;
        if (backendType.Equals("Gemini", StringComparison.OrdinalIgnoreCase)) return GeminiEmbeddingModel;
        return null;
    }

    // 埋め込みの API がエラーを返したとき、返事の本文をチャットのログに出してから、今までどおり EnsureSuccessStatusCode で例外にする。
    // URL の key= と本文中のキーの値は伏せる
    private static async Task EnsureEmbeddingSuccessAsync(HttpResponseMessage resp, string url, string? apiKey, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string body;
        try { body = await resp.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) { body = $"(本文の読み取りに失敗: {ex.Message})"; }
        if (body.Length > 2000) body = body[..2000] + "...";
        ChatService.LogExternal($"[埋め込み] HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} {MaskApiKey(url, apiKey)}: {MaskApiKey(body, apiKey)}");
        resp.EnsureSuccessStatusCode();
    }

    internal static string MaskApiKey(string text, string? apiKey)
    {
        var masked = System.Text.RegularExpressions.Regex.Replace(text, @"([?&]key=)[^&\s]*", "$1***");
        if (!string.IsNullOrEmpty(apiKey)) masked = masked.Replace(apiKey, "***");
        return masked;
    }

    // チャットの検索語の埋め込み用 (ベクトル構築と同じ API・同じモデル指定で作る)
    public static Task<List<float[]>?> GetQueryEmbeddingsAsync(BackendConfig backend, List<string> texts, CancellationToken ct) =>
        GetEmbeddingsAsync(backend, texts, ct);

    private static async Task<List<float[]>?> GetEmbeddingsAsync(BackendConfig backend, List<string> texts, CancellationToken ct)
    {
        var type = backend.Type.ToLowerInvariant();
        if (type == "ollama") return await GetOllamaEmbeddingsAsync(backend, texts, ct);
        if (type == "openai") return await GetOpenAiEmbeddingsAsync(backend, texts, ct);
        if (type == "gemini") return await GetGeminiEmbeddingsAsync(backend, texts, ct);
        return null;
    }

    private static async Task<List<float[]>?> GetOllamaEmbeddingsAsync(BackendConfig backend, List<string> texts, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrEmpty(backend.BaseUrl) ? "http://localhost:11434" : backend.BaseUrl.TrimEnd('/');
        var model = string.IsNullOrEmpty(backend.Model) ? OllamaEmbeddingModel : backend.Model;

        var results = new List<float[]>();
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            var body = JsonSerializer.Serialize(new { model, input = text });
            var url = $"{baseUrl}/api/embed";
            var resp = await Http.PostAsync(url,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            await EnsureEmbeddingSuccessAsync(resp, url, backend.ApiKey, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("embeddings", out var embs) && embs.GetArrayLength() > 0)
            {
                var arr = embs[0].EnumerateArray().Select(e => e.GetSingle()).ToArray();
                results.Add(arr);
            }
            else return null;
        }
        return results;
    }

    private static async Task<List<float[]>?> GetOpenAiEmbeddingsAsync(BackendConfig backend, List<string> texts, CancellationToken ct)
    {
        var model = string.IsNullOrEmpty(backend.Model) ? OpenAiEmbeddingModel : backend.Model;
        var baseUrl = string.IsNullOrEmpty(backend.BaseUrl) ? "https://api.openai.com/v1" : backend.BaseUrl.TrimEnd('/');
        var body = JsonSerializer.Serialize(new { model, input = texts });

        var url = $"{baseUrl}/embeddings";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Authorization", $"Bearer {backend.ApiKey}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await Http.SendAsync(req, ct);
        await EnsureEmbeddingSuccessAsync(resp, url, backend.ApiKey, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

        return data.EnumerateArray()
            .OrderBy(e => e.GetProperty("index").GetInt32())
            .Select(e => e.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray())
            .ToList();
    }

    private static async Task<List<float[]>?> GetGeminiEmbeddingsAsync(BackendConfig backend, List<string> texts, CancellationToken ct)
    {
        var model = string.IsNullOrEmpty(backend.Model) ? GeminiEmbeddingModel : backend.Model;
        var results = new List<float[]>();

        foreach (var batch in texts.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var requests = batch.Select(t => new { model = $"models/{model}", content = new { parts = new[] { new { text = t } } } }).ToArray();
            var body = JsonSerializer.Serialize(new { requests });

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:batchEmbedContents?key={backend.ApiKey}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var resp = await Http.SendAsync(req, ct);
            await EnsureEmbeddingSuccessAsync(resp, url, backend.ApiKey, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("embeddings", out var embs)) return null;
            foreach (var emb in embs.EnumerateArray())
            {
                if (emb.TryGetProperty("values", out var vals))
                    results.Add(vals.EnumerateArray().Select(v => v.GetSingle()).ToArray());
            }
        }
        return results;
    }

    private static byte[] FloatsToBytes(float[] floats)
    {
        var bytes = new byte[floats.Length * 4];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] BytesToFloats(byte[] bytes)
    {
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static string ExtractGameDataKeyword(string query)
    {
        var keywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"リピーター", "repeater"}, {"キャノン", "cannon"}, {"ガトリング", "gatling"},
            {"スキャッターガン", "scattergun"}, {"レーザー", "laser"},
            {"バリスティック", "ballistic"}, {"ディストーション", "distortion"},
            {"パワープラント", "powerplant"}, {"クーラー", "cooler"},
            {"シールドジェネレーター", "shield_generator"}, {"クォンタムドライブ", "quantum_drive"},
            {"ミサイル", "missile"}, {"タレット", "turret"},
            {"ピストル", "pistol"}, {"ライフル", "rifle"}, {"ショットガン", "shotgun"},
            {"スナイパー", "sniper"}, {"グレネード", "grenade"},
        };

        foreach (var (ja, en) in keywords)
            if (query.Contains(ja, StringComparison.OrdinalIgnoreCase))
                return en;

        if (query.Any(c => c < 128 && char.IsLetter(c)))
            return query.Trim();

        return "";
    }

    private void MigrateWikiColumns()
    {
        if (_db == null) return;
        try
        {
            using var chk = _db.CreateCommand();
            chk.CommandText = "SELECT sql FROM sqlite_master WHERE name='missions'";
            var schema = chk.ExecuteScalar() as string ?? "";
            if (schema.Contains("wiki_title", StringComparison.OrdinalIgnoreCase)) return;

            var cols = new[] { "wiki_title TEXT", "wiki_faction TEXT", "wiki_reward REAL",
                "wiki_legality TEXT", "wiki_enemy_min INTEGER", "wiki_enemy_max INTEGER",
                "wiki_duration_min REAL", "wiki_uuid TEXT" };
            foreach (var col in cols)
            {
                using var alt = _db.CreateCommand();
                alt.CommandText = $"ALTER TABLE missions ADD COLUMN {col}";
                try { alt.ExecuteNonQuery(); } catch { }
            }
            using var idx = _db.CreateCommand();
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_missions_wiki ON missions(wiki_title COLLATE NOCASE)";
            try { idx.ExecuteNonQuery(); } catch { }
        }
        catch { }
    }

    public async Task FetchWikiMissionsAsync(CancellationToken ct = default)
    {
        EnsureDb();
        MigrateWikiColumns();
        StatusChanged?.Invoke("Wiki API から契約データを取得中...");

        int page = 1, totalFetched = 0, linked = 0, added = 0;
        var now = DateTime.UtcNow.ToString("o");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            StatusChanged?.Invoke($"Wiki API 契約取得中... ページ {page} ({totalFetched} 件取得済み)");

            string json;
            try
            {
                var url = $"https://api.star-citizen.wiki/api/missions?page[number]={page}&page[size]=200";
                json = await Http.GetStringAsync(url, ct);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"Wiki API エラー (page {page}): {ex.Message}");
                break;
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0)
                break;

            using var tx = _db!.BeginTransaction();
            foreach (var m in data.EnumerateArray())
            {
                var uuid = m.TryGetProperty("uuid", out var u) ? u.GetString() ?? "" : "";
                var title = m.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var debugName = m.TryGetProperty("debug_name", out var dn) ? dn.GetString() ?? "" : "";
                var faction = "";
                if (m.TryGetProperty("faction", out var f) && f.ValueKind == JsonValueKind.Object)
                    faction = f.TryGetProperty("name", out var fn) ? fn.GetString() ?? "" : "";
                var rewardMin = m.TryGetProperty("reward_min", out var rm) && rm.ValueKind == JsonValueKind.Number ? rm.GetDouble() : 0;
                var legality = m.TryGetProperty("legality_label", out var ll) ? ll.GetString() ?? "" : "";
                var enemyMin = m.TryGetProperty("enemy_count_min", out var en1) && en1.ValueKind == JsonValueKind.Number ? en1.GetInt32() : 0;
                var enemyMax = m.TryGetProperty("enemy_count_max", out var en2) && en2.ValueKind == JsonValueKind.Number ? en2.GetInt32() : 0;
                var duration = m.TryGetProperty("time_to_complete_minutes", out var dur) && dur.ValueKind == JsonValueKind.Number ? dur.GetDouble() : 0;
                var missionGiver = m.TryGetProperty("mission_giver", out var mg) ? mg.GetString() ?? "" : "";
                var description = m.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
                var hasCombat = m.TryGetProperty("has_combat", out var hc) && hc.ValueKind == JsonValueKind.True;
                var shareable = m.TryGetProperty("shareable", out var sh) && sh.ValueKind == JsonValueKind.True;
                var maxPlayers = m.TryGetProperty("max_players_per_instance", out var mp) && mp.ValueKind == JsonValueKind.Number ? mp.GetInt32() : 0;
                var rankIndex = m.TryGetProperty("rank_index", out var ri) && ri.ValueKind == JsonValueKind.Number ? ri.GetInt32() : -1;
                var released = m.TryGetProperty("released", out var rel) && rel.ValueKind == JsonValueKind.True;

                if (string.IsNullOrEmpty(title) || title == "LOC_UNINITIALIZED") { totalFetched++; continue; }

                // Wiki の uuid が ContractGenerator 行の契約 id と一致すれば、その行に書く (record_name = "ContractGenerator.{契約 id 小文字}")
                bool didLink = false;
                if (!string.IsNullOrEmpty(uuid))
                {
                    using var cgCmd = _db.CreateCommand();
                    cgCmd.Transaction = tx;
                    cgCmd.CommandText = "UPDATE missions SET wiki_title=@wt, wiki_faction=@wf, wiki_reward=@wr, wiki_legality=@wl, wiki_enemy_min=@we1, wiki_enemy_max=@we2, wiki_duration_min=@wd, wiki_uuid=@wu WHERE record_name = @rn";
                    cgCmd.Parameters.AddWithValue("@wt", title);
                    cgCmd.Parameters.AddWithValue("@wf", faction);
                    cgCmd.Parameters.AddWithValue("@wr", rewardMin);
                    cgCmd.Parameters.AddWithValue("@wl", legality);
                    cgCmd.Parameters.AddWithValue("@we1", enemyMin);
                    cgCmd.Parameters.AddWithValue("@we2", enemyMax);
                    cgCmd.Parameters.AddWithValue("@wd", duration);
                    cgCmd.Parameters.AddWithValue("@wu", uuid);
                    cgCmd.Parameters.AddWithValue("@rn", ContractRecordPrefix + uuid.ToLowerInvariant());
                    if (cgCmd.ExecuteNonQuery() > 0) { linked++; didLink = true; }
                }

                // Try to link to existing DCB mission by debug_name
                if (!didLink && !string.IsNullOrEmpty(debugName))
                {
                    using var linkCmd = _db.CreateCommand();
                    linkCmd.Transaction = tx;
                    linkCmd.CommandText = "UPDATE missions SET wiki_title=@wt, wiki_faction=@wf, wiki_reward=@wr, wiki_legality=@wl, wiki_enemy_min=@we1, wiki_enemy_max=@we2, wiki_duration_min=@wd, wiki_uuid=@wu WHERE record_name LIKE @dn";
                    linkCmd.Parameters.AddWithValue("@wt", title);
                    linkCmd.Parameters.AddWithValue("@wf", faction);
                    linkCmd.Parameters.AddWithValue("@wr", rewardMin);
                    linkCmd.Parameters.AddWithValue("@wl", legality);
                    linkCmd.Parameters.AddWithValue("@we1", enemyMin);
                    linkCmd.Parameters.AddWithValue("@we2", enemyMax);
                    linkCmd.Parameters.AddWithValue("@wd", duration);
                    linkCmd.Parameters.AddWithValue("@wu", uuid);
                    linkCmd.Parameters.AddWithValue("@dn", $"%{debugName}%");
                    if (linkCmd.ExecuteNonQuery() > 0) { linked++; didLink = true; }
                }

                // If not linked, insert as new mission
                if (!didLink)
                {
                    var recordName = $"WikiMission.{uuid}";
                    var missionType = m.TryGetProperty("reward_scope", out var rs) ? rs.GetString() ?? "" : "";
                    var difficulty = rankIndex switch { 0 => "Intro", 1 => "Easy", 2 => "Medium", 3 => "Hard", 4 => "Very Hard", 5 => "Super", _ => "" };
                    var location = "";
                    if (m.TryGetProperty("star_systems", out var ss) && ss.ValueKind == JsonValueKind.Array)
                    {
                        var systems = new List<string>();
                        foreach (var s in ss.EnumerateArray()) systems.Add(s.GetString() ?? "");
                        location = string.Join(", ", systems);
                    }

                    using var insCmd = _db.CreateCommand();
                    insCmd.Transaction = tx;
                    insCmd.CommandText = @"INSERT OR IGNORE INTO missions(record_name, title, title_hud, mission_type, difficulty,
                        mission_giver, location_label, description, reward_min, reward_max, required_reputation,
                        lawfulness_type, jurisdiction, time_limit, raw_json, extracted_at,
                        wiki_title, wiki_faction, wiki_reward, wiki_legality, wiki_enemy_min, wiki_enemy_max, wiki_duration_min, wiki_uuid)
                        VALUES(@rn, @ti, '', @mt, @di, @mg, @ll, @de, @rmin, 0, '', @law, '', @tl, '{}', @ea,
                        @wt, @wf, @wr, @wl, @we1, @we2, @wd, @wu)";
                    insCmd.Parameters.AddWithValue("@rn", recordName);
                    insCmd.Parameters.AddWithValue("@ti", title);
                    insCmd.Parameters.AddWithValue("@mt", missionType.ToLowerInvariant());
                    insCmd.Parameters.AddWithValue("@di", difficulty);
                    insCmd.Parameters.AddWithValue("@mg", missionGiver);
                    insCmd.Parameters.AddWithValue("@ll", location);
                    insCmd.Parameters.AddWithValue("@de", description);
                    insCmd.Parameters.AddWithValue("@rmin", rewardMin);
                    insCmd.Parameters.AddWithValue("@law", legality);
                    insCmd.Parameters.AddWithValue("@tl", duration > 0 ? $"{duration}" : "");
                    insCmd.Parameters.AddWithValue("@ea", now);
                    insCmd.Parameters.AddWithValue("@wt", title);
                    insCmd.Parameters.AddWithValue("@wf", faction);
                    insCmd.Parameters.AddWithValue("@wr", rewardMin);
                    insCmd.Parameters.AddWithValue("@wl", legality);
                    insCmd.Parameters.AddWithValue("@we1", enemyMin);
                    insCmd.Parameters.AddWithValue("@we2", enemyMax);
                    insCmd.Parameters.AddWithValue("@wd", duration);
                    insCmd.Parameters.AddWithValue("@wu", uuid);
                    if (insCmd.ExecuteNonQuery() > 0) added++;
                }
                totalFetched++;
            }
            tx.Commit();

            var meta = doc.RootElement.TryGetProperty("meta", out var metaEl) ? metaEl : default;
            var lastPage = meta.TryGetProperty("last_page", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : 1;
            if (page >= lastPage) break;
            page++;
            await Task.Delay(100, ct);
        }

        SetMeta("wiki_missions_fetched_at", DateTime.UtcNow.ToString("o"));
        SetMeta("wiki_missions_count", totalFetched.ToString());
        StatusChanged?.Invoke($"Wiki API 契約取得完了: {totalFetched} 件取得, {linked} 件リンク, {added} 件新規追加");
        ProgressChanged?.Invoke(100, $"Wiki 契約: {totalFetched} 件 (リンク: {linked}, 新規: {added})");
    }
}
