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
        if (_db != null) return;
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
                if (schema.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase))
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
                size INTEGER
            );
            CREATE INDEX IF NOT EXISTS idx_ship_ports_ship ON ship_ports(ship_record_name);

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
                extracted_at TEXT NOT NULL
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
            cmd.CommandText = "DELETE FROM ship_ports; DELETE FROM ships; DELETE FROM items; DELETE FROM missions; DELETE FROM commodities; DELETE FROM gamedata_cache; DELETE FROM gamedata_meta;";
            cmd.ExecuteNonQuery();
        }

        // Step 1: 船・車両
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 船・車両データを抽出中...");
        int shipCount = await ExtractShipsAsync(dataP4kPath, now, ct);

        // Step 2: 武器
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 武器データを抽出中...");
        int weaponCount = await ExtractItemsAsync(dataP4kPath, "*WeaponGun*", "SCItemWeaponComponentParams", now, ct);

        // Step 3: シールド・QD・パワープラント・クーラー
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] コンポーネントデータを抽出中...");
        int compCount = 0;
        compCount += await ExtractItemsAsync(dataP4kPath, "*SCItem_Shield*", "SCItemShieldGeneratorParams", now, ct);
        compCount += await ExtractItemsAsync(dataP4kPath, "*SCItem_QuantumDrive*", "SCItemQuantumDriveParams", now, ct);
        compCount += await ExtractItemsAsync(dataP4kPath, "*SCItem_PowerPlant*", "SCItemPowerPlantParams", now, ct);
        compCount += await ExtractItemsAsync(dataP4kPath, "*SCItem_Cooler*", "SCItemCoolerParams", now, ct);

        // Step 4: ミッション・契約（最も時間がかかる）
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] ミッション・契約を抽出中...");
        int missionCount = await ExtractMissionsAsync(dataP4kPath, now, ct);

        // Step 5: コモディティ
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] コモディティを抽出中...");
        int commodityCount = await ExtractCommoditiesAsync(dataP4kPath, now, ct);

        // Step 6: メタ情報
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] メタ情報を保存中...");
        var p4kModified = File.GetLastWriteTimeUtc(dataP4kPath);
        SetMeta("game_version", p4kModified.ToString("yyyy-MM-dd HH:mm"));
        SetMeta("p4k_last_modified", p4kModified.ToString("o"));
        SetMeta("indexed_at", now);
        SetMeta("p4k_path", dataP4kPath);

        sw.Stop();
        ProgressChanged?.Invoke(100, $"完了！ ({sw.Elapsed.TotalSeconds:F1}秒)");
        StatusChanged?.Invoke($"構造化インデックス完了 ({sw.Elapsed.TotalSeconds:F1}秒) - 船: {shipCount}, 武器: {weaponCount}, コンポーネント: {compCount}, 契約: {missionCount}, 商品: {commodityCount}");
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

    private async Task<int> ExtractShipsAsync(string p4kPath, string now, CancellationToken ct)
    {
        var rawJson = await RunDcbQueryWithTimerAsync(p4kPath, "EntityClassDefinition", "*_Vehicle*", "[1/6] 船・車両データ", ct);
        if (string.IsNullOrEmpty(rawJson)) return 0;

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
        portCmd.CommandText = "INSERT INTO ship_ports(ship_record_name,port_name,item_type,size) VALUES(@srn,@pn,@it,@sz)";
        var ppSrn = portCmd.Parameters.Add("@srn", SqliteType.Text);
        var ppPn = portCmd.Parameters.Add("@pn", SqliteType.Text);
        var ppIt = portCmd.Parameters.Add("@it", SqliteType.Text);
        var ppSz = portCmd.Parameters.Add("@sz", SqliteType.Integer);

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
                    if (type == "SVehicleComponentParams")
                    {
                        career = comp.TryGetProperty("vehicleCareer", out var c) ? c.GetString() ?? "" : "";
                        role = comp.TryGetProperty("vehicleRole", out var r) ? r.GetString() ?? "" : "";
                        crewSize = comp.TryGetProperty("crewSize", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt32() : 0;
                    }
                }

                pRn.Value = recordName; pNm.Value = name; pMf.Value = manufacturer;
                pCa.Value = career; pRo.Value = role; pCr.Value = crewSize;
                pSz.Value = size; pRj.Value = block; pEa.Value = now;
                shipCmd.ExecuteNonQuery();

                foreach (var comp in components.EnumerateArray())
                {
                    var type = comp.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
                    if (type == "SAttachableComponentParams" && comp.TryGetProperty("AttachDef", out var ad))
                    {
                        var itemType = ad.TryGetProperty("Type", out var it) ? it.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(itemType) && itemType != "UNDEFINED" && itemType != "MainThruster")
                        {
                            var portSize = ad.TryGetProperty("Size", out var ps) ? ps.GetInt32() : 0;
                            var portName = ad.TryGetProperty("Localization", out var pl) && pl.TryGetProperty("Name", out var pln) ? pln.GetString() ?? "" : "";
                            ppSrn.Value = recordName; ppPn.Value = portName; ppIt.Value = itemType; ppSz.Value = portSize;
                            portCmd.ExecuteNonQuery();
                        }
                    }
                }
                count++;
            }
            catch { }
        }
        tx.Commit();
        return count;
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
                    pRmin.Value = GetNum(rv, "rewardMin", GetNum(rv, "payoutMin", GetNum(rv, "reward", 0)));
                    pRmax.Value = GetNum(rv, "rewardMax", GetNum(rv, "payoutMax", GetNum(rv, "payout", 0)));
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

                    if (type == "SVehicleComponentParams")
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
        };

        foreach (var (ja, en) in keywords)
            if (query.Contains(ja, StringComparison.OrdinalIgnoreCase))
                return en;

        return "";
    }
}
