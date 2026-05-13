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
        await EnsureStarBreakerAsync();
        EnsureDb();

        // キャッシュクリア
        using (var cmd = _db!.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM gamedata_cache; DELETE FROM gamedata_meta;";
            cmd.ExecuteNonQuery();
        }

        StatusChanged?.Invoke("ゲームデータのインデックスを構築中...");
        var sw = Stopwatch.StartNew();
        int step = 0;
        int totalSteps = 4;

        // Step 1: 全船名を取得
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 船名リストを取得中...");
        var shipsJson = await RunDcbQueryAsync(dataP4kPath,
            "EntityClassDefinition.Components[SAttachableComponentParams].AttachDef.Type",
            "*Vehicle*", ct);
        // 船名のフィルタリングが必要なので、代わりにレコード名のリストを取得
        var shipNames = await RunDcbQueryNamesAsync(dataP4kPath, "EntityClassDefinition", "*_Vehicle", ct);
        SetCache("index:ships", JsonSerializer.Serialize(shipNames));

        // Step 2: 主要な武器タイプのリストをキャッシュ
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] 武器データをインデックス中...");
        var weaponNames = await RunDcbQueryNamesAsync(dataP4kPath, "EntityClassDefinition", "*WeaponGun*", ct);
        SetCache("index:weapons", JsonSerializer.Serialize(weaponNames));

        // Step 3: シールド・パワープラント等コンポーネント
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] コンポーネントデータをインデックス中...");
        var compNames = await RunDcbQueryNamesAsync(dataP4kPath, "EntityClassDefinition", "*SCItem_Cooler*", ct);
        var shieldNames = await RunDcbQueryNamesAsync(dataP4kPath, "EntityClassDefinition", "*SCItem_Shield*", ct);
        var powerNames = await RunDcbQueryNamesAsync(dataP4kPath, "EntityClassDefinition", "*SCItem_PowerPlant*", ct);
        var qdNames = await RunDcbQueryNamesAsync(dataP4kPath, "EntityClassDefinition", "*SCItem_QuantumDrive*", ct);
        var allComps = compNames.Concat(shieldNames).Concat(powerNames).Concat(qdNames).Distinct().ToList();
        SetCache("index:components", JsonSerializer.Serialize(allComps));

        // Step 4: ゲームバージョン取得
        step++;
        ProgressChanged?.Invoke(step * 100 / totalSteps, $"[{step}/{totalSteps}] バージョン情報を保存中...");
        var p4kModified = File.GetLastWriteTime(dataP4kPath);
        SetMeta("game_version", p4kModified.ToString("yyyy-MM-dd HH:mm"));
        SetMeta("indexed_at", DateTime.Now.ToString("o"));
        SetMeta("p4k_path", dataP4kPath);

        sw.Stop();
        ProgressChanged?.Invoke(100, $"完了！ ({sw.Elapsed.TotalSeconds:F1}秒)");
        StatusChanged?.Invoke($"インデックス構築完了 ({sw.Elapsed.TotalSeconds:F1}秒) - 船: {shipNames.Count}, 武器: {weaponNames.Count}, コンポーネント: {allComps.Count}");
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
            tasks.Add(QueryMissionsInternalAsync(p4kPath));

        if (ContainsCommodityKeyword(query))
            tasks.Add(QueryCommoditiesAsync(query));

        if (tasks.Count == 0) return null;

        var results = await Task.WhenAll(tasks);
        foreach (var r in results)
            if (!string.IsNullOrEmpty(r)) sb.AppendLine(r);

        return sb.Length > 0 ? sb.ToString() : null;
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
        return await QueryMissionsInternalAsync(p4kPath);
    }

    private async Task<string?> QueryMissionsInternalAsync(string p4kPath)
    {
        var cacheKey = "missions:all";
        var cached = GetCache(cacheKey);
        if (cached != null) return cached;

        var sb = new StringBuilder("=== ゲームファイル (Data.p4k): ミッション/契約 ===\n");
        int found = 0;

        var json = await RunDcbQueryRawAsync(p4kPath, "MissionBrokerEntry", "*");
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
                        var title = rv.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        var difficulty = rv.TryGetProperty("difficulty", out var d) ? d.GetString() ?? "" : "";
                        var mType = rv.TryGetProperty("type", out var mt) ? mt.GetString() ?? "" : "";
                        var giver = rv.TryGetProperty("missionGiver", out var mg) ? mg.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(title)) parts.Add($"タイトル: {title}");
                        if (!string.IsNullOrEmpty(difficulty)) parts.Add($"難易度: {difficulty}");
                        if (!string.IsNullOrEmpty(mType)) parts.Add($"種別: {mType}");
                        if (!string.IsNullOrEmpty(giver)) parts.Add($"依頼者: {giver}");
                    }
                    sb.AppendLine($"- {string.Join(" | ", parts)}");
                    if (++found >= 100) break;
                }
                catch { }
            }
        }

        var contractJson = await RunDcbQueryRawAsync(p4kPath, "ContractManager", "*");
        if (!string.IsNullOrEmpty(contractJson) && found < 150)
        {
            foreach (var block in SplitJsonBlocks(contractJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(block);
                    var root = doc.RootElement;
                    var recordName = root.TryGetProperty("_RecordName_", out var rn) ? rn.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(recordName))
                    {
                        sb.AppendLine($"- {recordName}");
                        if (++found >= 150) break;
                    }
                }
                catch { }
            }
        }

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

    private async Task<string> RunDcbQueryRawAsync(string p4kPath, string recordType, string filter, CancellationToken ct = default)
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
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return stdout;
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
