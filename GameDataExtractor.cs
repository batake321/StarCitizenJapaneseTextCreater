using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class GameDataExtractor
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

    private void EnsureDb()
    {
        if (_db != null)
        {
            try
            {
                using var chk = _db.CreateCommand();
                chk.CommandText = "SELECT sql FROM sqlite_master WHERE name='ship_ports'";
                var s = chk.ExecuteScalar() as string ?? "";
                if (!s.Contains("equipped_item", StringComparison.OrdinalIgnoreCase))
                {
                    _db.Close(); _db.Dispose(); _db = null;
                    SqliteConnection.ClearAllPools();
                    File.Delete(DbPath);
                }
                else
                {
                    MigrateWikiColumns();
                    return;
                }
            }
            catch { _db = null; }
        }
        if (File.Exists(DbPath))
        {
            try
            {
                using var testConn = new SqliteConnection($"Data Source={DbPath};Mode=ReadOnly");
                testConn.Open();
                using var testCmd = testConn.CreateCommand();
                testCmd.CommandText = "SELECT sql FROM sqlite_master WHERE name='ship_ports'";
                var schema = testCmd.ExecuteScalar() as string ?? "";
                testConn.Close();
                if (schema.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                    !schema.Contains("equipped_item", StringComparison.OrdinalIgnoreCase))
                {
                    SqliteConnection.ClearAllPools();
                    File.Delete(DbPath);
                }
            }
            catch { try { File.Delete(DbPath); } catch { } }
        }
        _db = new SqliteConnection($"Data Source={DbPath}");
        _db.Open();

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
                extracted_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ships_name ON ships(name COLLATE NOCASE);

            CREATE TABLE IF NOT EXISTS ship_ports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ship_record_name TEXT NOT NULL,
                port_name TEXT,
                item_type TEXT,
                size INTEGER,
                equipped_item TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_ship_ports_ship ON ship_ports(ship_record_name);
            CREATE INDEX IF NOT EXISTS idx_ship_ports_type ON ship_ports(item_type);

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
                extracted_at TEXT NOT NULL
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
        """;
        cmd.ExecuteNonQuery();
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

    public async Task BuildIndexAsync(string dataP4kPath, CancellationToken ct = default)
    {
        await BuildStructuredIndexAsync(dataP4kPath, ct);
    }

    public async Task BuildStructuredIndexAsync(string dataP4kPath, CancellationToken ct = default)
    {
        var dbDrive = new DriveInfo(Path.GetPathRoot(DbPath)!);
        if (dbDrive.AvailableFreeSpace < 500 * 1024 * 1024)
            throw new IOException($"ディスク容量不足: {dbDrive.Name} の空き {dbDrive.AvailableFreeSpace / 1024 / 1024}MB (500MB以上必要)");

        await EnsureStarBreakerAsync();
        EnsureDb();

        StatusChanged?.Invoke("ゲームデータの構造化インデックスを構築中...");
        var sw = Stopwatch.StartNew();
        int step = 0;
        int totalSteps = 6;
        var now = DateTime.UtcNow.ToString("o");

        // クリア
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM item_index; DELETE FROM ship_ports; DELETE FROM ships; DELETE FROM items; DELETE FROM missions; DELETE FROM commodities; DELETE FROM gamedata_cache; DELETE FROM gamedata_meta;";
            cmd.ExecuteNonQuery();
        }

        // Step 1: アイテムインデックス（全メーカー＋船）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] アイテムインデックスを構築中...");
        int indexCount = await ExtractItemIndexAsync(dataP4kPath, now, ct);

        // Step 1b: FTS5 全文検索インデックス
        StatusChanged?.Invoke($"FTS5 全文検索インデックスを構築中...");
        RebuildFts5Index();

        // Step 2: 船・車両（詳細データ + ハードポイント）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 船・車両データを抽出中...");
        int shipCount = await ExtractShipsAsync(dataP4kPath, now, ct);

        // Step 3: アイテム詳細（装備コンポーネント）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 装備アイテムを抽出中...");
        int itemCount = 0;
        var componentQueries = new[]
        {
            ("SHLD_*", "SCItemShieldGeneratorParams"),
            ("POWR_*", "SCItemPowerPlantParams"),
            ("QDRV_*", "SCItemQuantumDriveParams"),
            ("COOL_*", "SCItemCoolerParams"),
        };
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

        // Step 4: ミッション・契約
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] ミッション・契約を抽出中...");
        int missionCount = await ExtractMissionsAsync(dataP4kPath, now, ct);

        // Step 5: コモディティ
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] コモディティを抽出中...");
        int commodityCount = await ExtractCommoditiesAsync(dataP4kPath, now, ct);

        // Step 6: メタ情報
        step++;
        var p4kModified = File.GetLastWriteTimeUtc(dataP4kPath);
        SetMeta("game_version", p4kModified.ToString("yyyy-MM-dd HH:mm"));
        SetMeta("p4k_last_modified", p4kModified.ToString("o"));
        SetMeta("indexed_at", now);
        SetMeta("p4k_path", dataP4kPath);

        sw.Stop();
        ProgressChanged?.Invoke(100, $"完了！ ({sw.Elapsed.TotalSeconds:F1}秒)");
        StatusChanged?.Invoke($"構造化インデックス完了 ({sw.Elapsed.TotalSeconds:F1}秒) - インデックス: {indexCount}, 船: {shipCount}, 装備: {itemCount}, 契約: {missionCount}, 商品: {commodityCount}");
    }

    public bool IsP4kUpdated()
    {
        EnsureDb();
        var stored = GetMeta("p4k_last_modified");
        if (string.IsNullOrEmpty(stored)) return true;
        var p4kPath = FindDataP4k();
        if (p4kPath == null) return false;
        var current = File.GetLastWriteTimeUtc(p4kPath).ToString("o");
        return current != stored;
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

    private static readonly string[] VehicleManufacturerPrefixes =
        ["AEGS", "ANVL", "ARGO", "BANU", "CNOU", "CRUS", "DRAK", "GAMA", "GATA", "GRIN", "KRIG", "MISC", "MRAI", "ORIG", "RSI", "TMBL", "VNCL", "XIAN", "ESPR"];

    private static readonly string[] ItemPrefixes =
        ["behr", "klwe", "ksar", "lbco", "gmni", "hdso", "jofl", "grin", "apar", "crus", "aegs", "anvl", "argo", "cnou", "drak", "krig", "misc", "mrai", "orig", "rsi", "tmbl", "xian", "espr",
         "POWR", "SHLD", "COOL", "QDRV", "MISL", "MRCK", "RADR", "COMP", "INTK", "HTNK", "QTNK", "ARMR", "LFSP", "RELAY",
         "powr", "shld", "cool", "qdrv", "misl", "mrck", "radr", "comp", "intk", "htnk", "qtnk", "armr", "lfsp", "relay",
         "GODI", "WETK", "TYDT", "GRNP", "FSKI", "godi", "wetk", "tydt", "grnp", "fski"];

    private async Task<int> ExtractItemIndexAsync(string p4kPath, string now, CancellationToken ct)
    {
        var gamePath = App.Config.GamePath;
        var enDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jaDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var enIniPath = Path.Combine(gamePath, "data", "Localization", "english", "global.ini");
        if (!File.Exists(enIniPath))
            enIniPath = Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "data", "Localization", "english", "global.ini");
        var jaIniPath = Path.Combine(gamePath, "data", "Localization", "japanese_(japan)", "global.ini");
        if (!File.Exists(jaIniPath))
            jaIniPath = Path.Combine(Path.GetDirectoryName(gamePath) ?? "", "data", "Localization", "japanese_(japan)", "global.ini");
        if (File.Exists(enIniPath))
        {
            StatusChanged?.Invoke("[1/4] ローカライズデータ（英語）を読み込み中...");
            enDict = GlobalIniParser.Parse(enIniPath);
        }
        if (File.Exists(jaIniPath))
        {
            StatusChanged?.Invoke("[1/4] ローカライズデータ（日本語）を読み込み中...");
            jaDict = GlobalIniParser.Parse(jaIniPath);
        }
        int resolved = 0;

        int count = 0;
        using var tx = _db!.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO item_index(uuid, record_name, name, name_ja, item_type, sub_type, manufacturer, extracted_at) VALUES(@uuid, @rn, @nm, @ja, @it, @st, @mf, @ea)";
        var pUuid = cmd.Parameters.Add("@uuid", SqliteType.Text);
        var pRn = cmd.Parameters.Add("@rn", SqliteType.Text);
        var pNm = cmd.Parameters.Add("@nm", SqliteType.Text);
        var pJa = cmd.Parameters.Add("@ja", SqliteType.Text);
        var pIt = cmd.Parameters.Add("@it", SqliteType.Text);
        var pSt = cmd.Parameters.Add("@st", SqliteType.Text);
        var pMf = cmd.Parameters.Add("@mf", SqliteType.Text);
        var pEa = cmd.Parameters.Add("@ea", SqliteType.Text);

        var allPrefixes = VehicleManufacturerPrefixes.Select(p => $"EntityClassDefinition.{p}_*")
            .Concat(ItemPrefixes.Select(p => $"EntityClassDefinition.{p}_*"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < allPrefixes.Count; i++)
        {
            var filter = allPrefixes[i];
            var prefix = filter.Split('.')[1].TrimEnd('*').TrimEnd('_');
            StatusChanged?.Invoke($"[1/{4}] インデックス構築 ({prefix}, {i + 1}/{allPrefixes.Count})");

            string rawJson;
            try { rawJson = await RunDcbQueryAsync(p4kPath, "EntityClassDefinition", filter, ct); }
            catch { continue; }
            if (string.IsNullOrEmpty(rawJson)) continue;

            foreach (var block in SplitJsonBlocks(rawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    var uuid = root.TryGetProperty("_RecordId_", out var rid) ? rid.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(recordName) || string.IsNullOrEmpty(uuid)) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)) continue;

                    string name = "", itemType = "", subType = "", manufacturer = "";
                    foreach (var comp in components.EnumerateArray())
                    {
                        var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                        if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                        {
                            itemType = ad.TryGetProperty("Type", out var it2) ? it2.GetString() ?? "" : "";
                            subType = ad.TryGetProperty("SubType", out var st2) ? st2.GetString() ?? "" : "";
                            manufacturer = ad.TryGetProperty("Manufacturer", out var mf2) ? mf2.GetString() ?? "" : "";
                            if (ad.TryGetProperty("Localization", out var loc))
                                name = loc.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(itemType) || itemType == "UNDEFINED") continue;

                    string nameJa = "";
                    if (name.StartsWith("@") && name.Length > 1)
                    {
                        var locKey = name[1..];
                        if (enDict.TryGetValue(locKey, out var enName) && !enName.StartsWith("!"))
                        {
                            name = enName;
                            resolved++;
                        }
                        else if (jaDict.TryGetValue(locKey, out var jaName) && !jaName.StartsWith("!"))
                        {
                            name = jaName;
                            resolved++;
                        }
                        if (jaDict.TryGetValue(locKey, out var jaVal) && !jaVal.StartsWith("!"))
                            nameJa = jaVal;
                    }

                    pUuid.Value = uuid; pRn.Value = recordName; pNm.Value = name;
                    pJa.Value = string.IsNullOrEmpty(nameJa) ? DBNull.Value : nameJa;
                    pIt.Value = itemType; pSt.Value = subType; pMf.Value = manufacturer; pEa.Value = now;
                    cmd.ExecuteNonQuery();
                    count++;
                }
                catch { }
            }
        }
        tx.Commit();
        StatusChanged?.Invoke($"[1/4] インデックス構築完了: {count} 件 (名前解決: {resolved} 件)");
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

        for (int pi = 0; pi < VehicleManufacturerPrefixes.Length; pi++)
        {
            var prefix = VehicleManufacturerPrefixes[pi];
            StatusChanged?.Invoke($"[1/6] 船・車両データ ({prefix}, {pi + 1}/{VehicleManufacturerPrefixes.Length})");
            var filter = $"EntityClassDefinition.{prefix}_*";
            var rawJson = await RunDcbQueryAsync(p4kPath, "EntityClassDefinition", filter, ct);
            if (string.IsNullOrEmpty(rawJson)) continue;

            count += ParseAndInsertShips(rawJson, now, shipCmd, pRn, pNm, pMf, pCa, pRo, pCr, pSz, pRj, pEa,
                portCmd, ppSrn, ppPn, ppIt, ppSz, ppEi);
        }

        tx.Commit();
        return count;
    }

    private static readonly HashSet<string> EquipmentPortPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "hardpoint_gun", "hardpoint_weapon", "hardpoint_turret",
        "hardpoint_shield", "hardpoint_power_plant", "hardpoint_quantum_drive",
        "hardpoint_cooler", "Hardpoint_cooler",
        "hardpoint_missilerack", "hardpoint_missile",
        "hardpoint_radar", "Hardpoint_Avionics", "Hardpoint_Life_Support",
        "hardpoint_countermeasure",
    };

    private static string InferPortType(string portName)
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

    private static int InferSizeFromEntityName(string entityName)
    {
        if (string.IsNullOrEmpty(entityName)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(entityName, @"_S(\d+)_");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static int ParseAndInsertShips(string rawJson, string now,
        SqliteCommand shipCmd, SqliteParameter pRn, SqliteParameter pNm, SqliteParameter pMf,
        SqliteParameter pCa, SqliteParameter pRo, SqliteParameter pCr, SqliteParameter pSz,
        SqliteParameter pRj, SqliteParameter pEa,
        SqliteCommand portCmd, SqliteParameter ppSrn, SqliteParameter ppPn, SqliteParameter ppIt, SqliteParameter ppSz, SqliteParameter ppEi)
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

                foreach (var comp in components.EnumerateArray())
                {
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

                var loadoutEntries = new List<(string portName, string entityName)>();
                CollectLoadoutEntries(components, loadoutEntries);

                foreach (var (portName, entityName) in loadoutEntries)
                {
                    var portType = InferPortType(portName);
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

    private static void CollectLoadoutEntries(JsonElement components, List<(string portName, string entityName)> results)
    {
        foreach (var comp in components.EnumerateArray())
        {
            if (!comp.TryGetProperty("_Type_", out var t)) continue;
            if (t.GetString() != "SEntityComponentDefaultLoadoutParams") continue;
            if (!comp.TryGetProperty("loadout", out var loadout)) continue;
            CollectEntriesFromLoadout(loadout, results);
            break;
        }
    }

    private static void CollectEntriesFromLoadout(JsonElement loadout, List<(string portName, string entityName)> results)
    {
        if (!loadout.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in entries.EnumerateArray())
        {
            var portName = entry.TryGetProperty("itemPortName", out var pn) ? pn.GetString() ?? "" : "";
            var entityName = entry.TryGetProperty("entityClassName", out var en) ? en.GetString() ?? "" : "";
            if (!string.IsNullOrEmpty(portName) &&
                EquipmentPortPrefixes.Any(p => portName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add((portName, entityName));
            }
            if (entry.TryGetProperty("loadout", out var subLoadout) && subLoadout.ValueKind == JsonValueKind.Object)
                CollectEntriesFromLoadout(subLoadout, results);
        }
    }

    private async Task<int> ExtractItemsAsync(string p4kPath, string filter, string componentType, string now, CancellationToken ct)
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
                if (!root.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var components)) continue;

                string name = "", itemType = "", subType = "", mfr = "";
                int size = 0, grade = 0;
                string compJson = "";

                foreach (var comp in components.EnumerateArray())
                {
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

                pRn.Value = recordName; pNm.Value = name; pIt.Value = itemType; pIst.Value = subType;
                pSz.Value = size; pGr.Value = grade; pMf.Value = mfr;
                pCt.Value = componentType; pCj.Value = compJson; pRj.Value = block; pEa.Value = now;
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

        var filters = new[] { "*PU_*", "*Tutorial*", "*Xenothreat*", "*ShipIncursion*", "*BlockadeRunner*", "*Kill*" };
        for (int fi = 0; fi < filters.Length; fi++)
        {
            ct.ThrowIfCancellationRequested();
            var filterIdx = fi;
            var filterLabel = filters[fi];

            var rawJson = await RunDcbQueryWithTimerAsync(p4kPath, "MissionBrokerEntry", filterLabel,
                $"[4/6] ミッション ({filterIdx + 1}/{filters.Length}): {filterLabel}", ct);
            if (string.IsNullOrEmpty(rawJson)) continue;

            StatusChanged?.Invoke($"[4/6] ミッション解析中 ({filterIdx + 1}/{filters.Length}): {filterLabel} — DB登録中...");
            int blockCount = 0;
            foreach (var block in SplitJsonBlocks(rawJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    if (!recordName.StartsWith("MissionBrokerEntry.")) continue;
                    if (!root.TryGetProperty("_RecordValue_", out var rv)) continue;

                    var title = ResolveLoc(GetStr(rv, "title"), locDict);
                    var difficulty = GetStr(rv, "difficulty");
                    var location = ResolveLoc(GetStr(rv, "locationLabel"), locDict);
                    if (string.IsNullOrEmpty(difficulty))
                        difficulty = InferDifficulty(recordName);
                    if (string.IsNullOrEmpty(location))
                        location = InferLocation(recordName);

                    pRn.Value = recordName;
                    pTi.Value = title;
                    pTh.Value = ResolveLoc(GetStr(rv, "titleHUD"), locDict);
                    pMt.Value = CleanMissionType(GetStr(rv, "type"));
                    pDi.Value = difficulty;
                    pMg.Value = ResolveLoc(GetStr(rv, "missionGiver"), locDict);
                    pLl.Value = location;
                    pDe.Value = ResolveLoc(GetStr(rv, "description"), locDict);
                    double rewardBase = 0;
                    if (rv.TryGetProperty("missionReward", out var mReward))
                        rewardBase = GetNum(mReward, "reward", GetNum(mReward, "max", 0));
                    pRmin.Value = GetNum(rv, "rewardMin", GetNum(rv, "payoutMin", rewardBase));
                    pRmax.Value = GetNum(rv, "rewardMax", GetNum(rv, "payoutMax", rewardBase));
                    pRr.Value = GetStr(rv, "requiredReputation", GetStr(rv, "minReputation"));
                    pLt.Value = GetStr(rv, "lawfulnessType");
                    pJu.Value = GetStr(rv, "jurisdiction");
                    pTl.Value = GetStr(rv, "timeLimit", GetStr(rv, "deadline"));
                    pRj.Value = block;
                    pEa.Value = now;
                    cmd.ExecuteNonQuery();
                    count++;
                }
                catch { }

                blockCount++;
                if (blockCount % 200 == 0)
                    StatusChanged?.Invoke($"[4/6] ミッション解析中 ({filterIdx + 1}/{filters.Length}): {filterLabel} — {count}件登録 ({blockCount}ブロック処理済み)");
            }
            ProgressChanged?.Invoke(66 + (filterIdx + 1) * 10 / filters.Length, $"[4/6] ミッション・契約: {count}件抽出済み");
        }

        tx.Commit();
        return count;
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

    private static string InferDifficulty(string recordName)
    {
        var parts = recordName.Split('_');
        foreach (var p in parts)
        {
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
                foreach (var comp in components.EnumerateArray())
                {
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

                foreach (var comp in components.EnumerateArray())
                {
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

                foreach (var comp in components.EnumerateArray())
                {
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
        var psi = new ProcessStartInfo
        {
            FileName = StarBreakerExe,
            Arguments = $"dcb query \"{recordType}\" --p4k \"{p4kPath}\" --filter \"{filter}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi)!;
        proc.BeginErrorReadLine();
        var sb = new StringBuilder();
        var buffer = new char[65536];
        int bytesRead;
        long totalChars = 0;
        while ((bytesRead = await proc.StandardOutput.ReadAsync(buffer, ct)) > 0)
        {
            sb.Append(buffer, 0, bytesRead);
            totalChars += bytesRead;
            onProgress?.Invoke(totalChars);
        }
        await proc.WaitForExitAsync(ct);
        return sb.ToString();
    }

    private async Task<string> RunDcbQueryAsync(string p4kPath, string path, string filter, CancellationToken ct = default)
    {
        return await RunDcbQueryRawAsync(p4kPath, path, filter, ct);
    }

    public void RebuildFts5Index()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "DROP TABLE IF EXISTS item_index_fts";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "CREATE VIRTUAL TABLE item_index_fts USING fts5(uuid UNINDEXED, name, name_ja, record_name, item_type, sub_type, manufacturer, tokenize='trigram')";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "INSERT INTO item_index_fts(uuid, name, name_ja, record_name, item_type, sub_type, manufacturer) SELECT uuid, name, COALESCE(name_ja,''), record_name, item_type, sub_type, manufacturer FROM item_index";
        cmd.ExecuteNonQuery();
    }

    public void PopulateJapaneseNames(string translationDbPath)
    {
        EnsureDb();
        if (!File.Exists(translationDbPath)) return;

        using var transConn = new SqliteConnection($"Data Source={translationDbPath};Mode=ReadOnly");
        transConn.Open();

        using var readCmd = transConn.CreateCommand();
        readCmd.CommandText = "SELECT key, japanese FROM translations WHERE key LIKE 'item_Name%' AND japanese IS NOT NULL AND japanese != ''";
        using var reader = readCmd.ExecuteReader();

        using var tx = _db!.BeginTransaction();
        using var updateCmd = _db.CreateCommand();
        updateCmd.CommandText = "UPDATE item_index SET name_ja = @ja WHERE name LIKE @en";
        var pJa = updateCmd.Parameters.Add("@ja", SqliteType.Text);
        var pEn = updateCmd.Parameters.Add("@en", SqliteType.Text);

        int updated = 0;
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var ja = reader.GetString(1);
            var enName = key.Replace("item_Name", "").Replace("item_name", "");

            using var enCmd = transConn.CreateCommand();
            enCmd.CommandText = "SELECT english FROM translations WHERE key = @k";
            enCmd.Parameters.AddWithValue("@k", key);
            var english = enCmd.ExecuteScalar() as string;
            if (string.IsNullOrEmpty(english)) continue;

            pJa.Value = ja;
            pEn.Value = $"%{english}%";
            updated += updateCmd.ExecuteNonQuery();
        }
        tx.Commit();
        transConn.Close();

        RebuildFts5Index();
        StatusChanged?.Invoke($"日本語名を {updated} 件更新し、FTS5 インデックスを再構築しました");
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
        var model = string.IsNullOrEmpty(backend.Model) ? "nomic-embed-text" : backend.Model;

        var results = new List<float[]>();
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            var body = JsonSerializer.Serialize(new { model, input = text });
            var resp = await Http.PostAsync($"{baseUrl}/api/embed",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            resp.EnsureSuccessStatusCode();
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
        var model = string.IsNullOrEmpty(backend.Model) ? "text-embedding-3-small" : backend.Model;
        var baseUrl = string.IsNullOrEmpty(backend.BaseUrl) ? "https://api.openai.com/v1" : backend.BaseUrl.TrimEnd('/');
        var body = JsonSerializer.Serialize(new { model, input = texts });

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings");
        req.Headers.Add("Authorization", $"Bearer {backend.ApiKey}");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
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
        var model = string.IsNullOrEmpty(backend.Model) ? "text-embedding-004" : backend.Model;
        var results = new List<float[]>();

        foreach (var batch in texts.Chunk(100))
        {
            ct.ThrowIfCancellationRequested();
            var requests = batch.Select(t => new { model = $"models/{model}", content = new { parts = new[] { new { text = t } } } }).ToArray();
            var body = JsonSerializer.Serialize(new { requests });

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:batchEmbedContents?key={backend.ApiKey}");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var resp = await Http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
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
        StatusChanged?.Invoke("Wiki API からミッションデータを取得中...");

        int page = 1, totalFetched = 0, linked = 0, added = 0;
        var now = DateTime.UtcNow.ToString("o");

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            StatusChanged?.Invoke($"Wiki API ミッション取得中... ページ {page} ({totalFetched} 件取得済み)");

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

                // Try to link to existing DCB mission by debug_name
                bool didLink = false;
                if (!string.IsNullOrEmpty(debugName))
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
        StatusChanged?.Invoke($"Wiki API ミッション取得完了: {totalFetched} 件取得, {linked} 件リンク, {added} 件新規追加");
        ProgressChanged?.Invoke(100, $"Wiki ミッション: {totalFetched} 件 (リンク: {linked}, 新規: {added})");
    }
}
