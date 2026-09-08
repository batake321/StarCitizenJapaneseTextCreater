using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class EquipmentService : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _cacheDbPath;

    // 購入先のオンメモリ索引。ツールチップは UI スレッドから同期で引かれるので、DB を読まずにここだけを見る
    // (プリフェッチの書き込みとロック競合させない)。索引は作り直して参照ごと差し替えるので、読み側にロックは要らない。
    // キーは item_name (UEX の英語名。大文字小文字は区別しない)。値は price 昇順
    private volatile Dictionary<string, List<(string Location, double Price)>> _priceIndex = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _pricePrefetched;      // uex_prefetch_state に記録があるか (「取得中…」と「登録がありません」の出し分け)

    // record_name (EntityClassDefinition. を除いた部分) から英語のアイテム名を引く索引。
    // 画面の表示名は日本語なので、UEX (英語名) と照合するときにこちらも試す
    private volatile Dictionary<string, string> _englishNameByRecord = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    // 装備カテゴリ。Key は items.item_type (船舶武器 WeaponGun のみ items に無く item_index から引く)。表示順はこの並び。
    // uexCategoryIds は UEX の購入場所検索用 (不明なカテゴリは空 → 既定カテゴリで検索)
    private const string WeaponGunType = "WeaponGun";
    private static readonly List<(string Key, string DisplayName, int[] UexCategoryIds)> ComponentCategories = new()
    {
        ("PowerPlant", "パワープラント", new[] { 21 }),
        ("Shield", "シールド", new[] { 23 }),
        ("QuantumDrive", "量子ドライブ", new[] { 22 }),
        ("JumpDrive", "ジャンプドライブ", Array.Empty<int>()),
        ("Cooler", "クーラー", new[] { 19 }),
        ("Radar", "レーダー", Array.Empty<int>()),
        ("MissileLauncher", "ミサイルラック", Array.Empty<int>()),
        ("FuelTank", "燃料タンク", Array.Empty<int>()),
        ("QuantumFuelTank", "量子燃料タンク", Array.Empty<int>()),
        ("FuelIntake", "燃料インテーク", Array.Empty<int>()),
        (WeaponGunType, "船舶武器", new[] { 32, 33, 34, 35 }),
    };

    private static readonly Dictionary<string, (string stat1Label, string stat2Label)> StatLabels = new()
    {
        ["SCItemShieldGeneratorParams"] = ("最大HP", "再生速度"),
        ["SCItemPowerPlantParams"] = ("出力", ""),
        ["SCItemCoolerParams"] = ("冷却速度", ""),
        ["SCItemQuantumDriveParams"] = ("スプール時間", "燃料消費"),
        ["SCItemWeaponComponentParams"] = ("発射速度", ""),
    };

    // item_index.size 列の有無 (再抽出前の旧 DB には無い。読み取り専用接続なので ALTER はここでは行わない)
    private readonly bool _hasItemIndexSize;

    public EquipmentService(string gamedataCacheDbPath)
    {
        _conn = new SqliteConnection($"Data Source={gamedataCacheDbPath};Mode=ReadOnly");
        _conn.Open();
        _hasItemIndexSize = HasColumn("item_index", "size");
        _cacheDbPath = Path.Combine(Path.GetDirectoryName(gamedataCacheDbPath) ?? ".", "equipment_cache.db");
        InitCacheDb();
        ReloadPriceIndex();
    }

    private bool HasColumn(string table, string column)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private void InitCacheDb()
    {
        using var db = new SqliteConnection($"Data Source={_cacheDbPath}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS uex_item_prices (
            item_name TEXT NOT NULL,
            location TEXT NOT NULL,
            price REAL NOT NULL,
            fetched_at TEXT NOT NULL,
            PRIMARY KEY (item_name, location)
        )";
        cmd.ExecuteNonQuery();

        // 一括取得の完了時刻。「未取得」と「購入先の登録が無い」を区別するために持つ
        cmd.CommandText = @"CREATE TABLE IF NOT EXISTS uex_prefetch_state (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            fetched_at TEXT NOT NULL
        )";
        cmd.ExecuteNonQuery();
    }

    // 装備カテゴリ (Key = items.item_type / "WeaponGun") と件数。件数 0 のカテゴリは返さない。
    // 件数は GetShipComponents と同じ除外 (PLACEHOLDER 名・空名) を適用した後のもの
    public List<EquipmentCategory> GetShipComponentCategories()
    {
        var categories = new List<EquipmentCategory>();
        foreach (var (key, displayName, _) in ComponentCategories)
        {
            var count = GetShipComponents(key).Count;
            if (count > 0)
                categories.Add(new EquipmentCategory { Key = key, DisplayName = displayName, Count = count });
        }
        return categories;
    }

    // 表示名が未ローカライズのプレースホルダ (item_index.name "<= PLACEHOLDER =>" / items.name "@LOC_PLACEHOLDER") または空
    private static bool IsPlaceholderName(string? name)
    {
        var n = (name ?? "").Trim();
        return n.Length == 0
            || n.Equals("<= PLACEHOLDER =>", StringComparison.Ordinal)
            || n.Equals("@LOC_PLACEHOLDER", StringComparison.Ordinal);
    }

    // item_index の武器 (WeaponGun) は Size 列が無いので record_name の "_S{n}" (末尾または "_" の前。大文字小文字無視) から補完する。
    // 例: BEHR_LaserCannon_S3 → 3、RSI_BallisticCannon_S5_Meteor_Bespoke → 5、POWR_AEGS_S01_Regulus → 1。表記が無ければ 0。
    // GameDataExtractor.InferSizeFromEntityName (EntitySizeRegex) と同一規則
    private static readonly Regex RecordSizeRegex = new(@"_S(\d{1,2})(?=_|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static int SizeFromRecordName(string? recordName)
    {
        if (string.IsNullOrEmpty(recordName)) return 0;
        var m = RecordSizeRegex.Match(recordName);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    // itemType (items.item_type) で絞った装備一覧。"WeaponGun" は item_index から (Grade 無し。Size は record_name から補完。sizeFilter は適用しない)。
    // 表示名は item_index.name を優先し、無ければ items.name (GetItemByRecord と同じ解決)。
    // 表示名がプレースホルダ ("<= PLACEHOLDER =>" / "@LOC_PLACEHOLDER") または空のものは候補から除外する
    public List<EquipmentItem> GetShipComponents(string itemType, string? search = null, int? sizeFilter = null)
    {
        if (itemType == WeaponGunType) return GetShipWeapons(search);

        using var cmd = _conn.CreateCommand();
        var conditions = new List<string> { "i.item_type = @ct" };
        cmd.Parameters.AddWithValue("@ct", itemType);

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(i.name LIKE @q OR i.manufacturer LIKE @q OR i.record_name LIKE @q OR COALESCE(x.name, '') LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{search}%");
        }
        if (sizeFilter.HasValue)
        {
            conditions.Add("i.size = @sz");
            cmd.Parameters.AddWithValue("@sz", sizeFilter.Value);
        }

        // item_index.record_name は一意でない可能性があるため相関サブクエリで 1 件に絞る
        cmd.CommandText = $"""
            SELECT i.record_name, i.name, i.item_type, i.size, i.grade, i.manufacturer, i.component_type, i.component_json, x.name
            FROM items i
            LEFT JOIN (SELECT record_name, MIN(name) AS name FROM item_index WHERE name IS NOT NULL AND name != '' GROUP BY record_name) x
                   ON x.record_name = i.record_name
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY i.size DESC, i.grade DESC, COALESCE(x.name, i.name)
            """;
        using var reader = cmd.ExecuteReader();

        var items = new List<EquipmentItem>();
        while (reader.Read())
        {
            var rawName = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1);
            var indexName = reader.IsDBNull(8) ? "" : reader.GetString(8);
            var item = new EquipmentItem
            {
                RecordName = reader.GetString(0),
                Name = indexName.Length > 0 ? indexName : rawName,
                ItemType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Size = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Grade = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Manufacturer = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ComponentType = reader.IsDBNull(6) ? "" : reader.GetString(6),
                ComponentJson = reader.IsDBNull(7) ? "" : reader.GetString(7),
            };
            if (IsPlaceholderName(item.Name)) continue;
            ParseStats(item);
            items.Add(item);
        }
        return items;
    }

    // 船舶武器 (item_index の item_type = 'WeaponGun')。Grade は無い。Size は record_name の "_S{n}" から補完 (無ければ 0)。
    // 表示名がプレースホルダ・空のものは除外
    private List<EquipmentItem> GetShipWeapons(string? search)
    {
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string> { "item_type = @ct" };
        cmd.Parameters.AddWithValue("@ct", WeaponGunType);
        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(name LIKE @q OR manufacturer LIKE @q OR record_name LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{search}%");
        }
        cmd.CommandText = $"SELECT record_name, name, item_type, sub_type, manufacturer FROM item_index WHERE {string.Join(" AND ", conditions)} ORDER BY name, record_name";
        using var reader = cmd.ExecuteReader();

        var items = new List<EquipmentItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var rec = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            // プレースホルダ名の行を先に落としてから重複排除する (同一 record に実名行があれば拾えるように)
            if (rec.Length == 0 || IsPlaceholderName(name)) continue;
            if (!seen.Add(rec)) continue;
            items.Add(new EquipmentItem
            {
                RecordName = rec,
                Name = name,
                ItemType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SubType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Manufacturer = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Size = SizeFromRecordName(rec),
            });
        }
        return items;
    }

    public List<EquipmentItem> GetPersonalWeapons(string? search = null, string? manufacturerFilter = null)
    {
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string> { "item_type = 'WeaponPersonal'" };

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(name LIKE @q OR manufacturer LIKE @q OR record_name LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{search}%");
        }
        if (!string.IsNullOrWhiteSpace(manufacturerFilter))
        {
            conditions.Add("manufacturer = @mfr");
            cmd.Parameters.AddWithValue("@mfr", manufacturerFilter);
        }

        cmd.CommandText = $"SELECT uuid, record_name, name, item_type, sub_type, manufacturer FROM item_index WHERE {string.Join(" AND ", conditions)} ORDER BY name";
        using var reader = cmd.ExecuteReader();

        var items = new List<EquipmentItem>();
        while (reader.Read())
        {
            items.Add(new EquipmentItem
            {
                RecordName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Name = reader.IsDBNull(2) ? (reader.IsDBNull(1) ? "" : reader.GetString(1)) : reader.GetString(2),
                ItemType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                SubType = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Manufacturer = reader.IsDBNull(5) ? "" : reader.GetString(5),
            });
        }
        return items;
    }

    public List<string> GetPersonalWeaponManufacturers()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT manufacturer FROM item_index WHERE item_type = 'WeaponPersonal' AND manufacturer IS NOT NULL AND manufacturer != '' ORDER BY manufacturer";
        using var reader = cmd.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            var mfr = reader.GetString(0);
            if (!mfr.StartsWith("file://"))
                list.Add(mfr);
        }
        return list;
    }

    // 船の全ポート行。子ポート行 (port_name が "{親port}/{子port}"。ジンバル配下の実武器・ミサイルラック配下のミサイル等) も含めて返す。
    // 親子関係は ShipPortInfo.IsChild / ParentPortName で判定する
    public List<ShipPortInfo> GetShipPorts(string shipRecordName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT port_name, item_type, size, equipped_item FROM ship_ports WHERE ship_record_name = @srn ORDER BY item_type, size DESC, port_name";
        cmd.Parameters.AddWithValue("@srn", shipRecordName);
        using var reader = cmd.ExecuteReader();

        var ports = new List<ShipPortInfo>();
        while (reader.Read())
        {
            ports.Add(new ShipPortInfo
            {
                PortName = reader.IsDBNull(0) ? "" : reader.GetString(0),
                ItemType = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Size = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                EquippedItem = reader.IsDBNull(3) ? "" : reader.GetString(3),
            });
        }
        return ports;
    }

    // record 名 1 件を items から引く (ツールチップ / 装備インベントリ用)。
    // ship_ports.equipped_item は "POWR_AEGS_S01_Regulus_SCItem" 形式、items.record_name / item_index.record_name は
    // "EntityClassDefinition.POWR_AEGS_S01_Regulus_SCItem" 形式なので、そのまま → "EntityClassDefinition." 前置 →
    // "_SCItem" の有無を反転したもの (+前置) の順に照合する。
    // 表示名は items.name がローカライズキー (@item_Name...) のため、同 record の item_index.name を優先し、無ければ items.name。
    // items に無く item_index にだけある record (船舶武器など) は名前・種別 (+ record_name の "_S{n}" から補完した Size。Grade = 0) で返す。
    // 表示名がプレースホルダ ("<= PLACEHOLDER =>" / "@LOC_PLACEHOLDER") または空のときは null にせず record 名を表示名にする。解決できなければ null
    private readonly Dictionary<string, EquipmentItem?> _itemByRecordCache = new(StringComparer.Ordinal);

    public EquipmentItem? GetItemByRecord(string record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return null;
        if (_itemByRecordCache.TryGetValue(key, out var cached)) return cached;

        EquipmentItem? result = null;
        foreach (var cand in RecordCandidates(key))
        {
            result = QueryItemByRecord(cand) ?? QueryItemIndexByRecord(cand);
            if (result != null) break;
        }
        if (result != null && IsPlaceholderName(result.Name)) result.Name = result.RecordName;
        _itemByRecordCache[key] = result;
        return result;
    }

    private static IEnumerable<string> RecordCandidates(string record)
    {
        const string prefix = "EntityClassDefinition.";
        const string suffix = "_SCItem";
        var bases = new List<string> { record };
        if (record.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            bases.Add(record[..^suffix.Length]);
        else
            bases.Add(record + suffix);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in bases)
        {
            if (seen.Add(b)) yield return b;
            if (!b.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var withPrefix = prefix + b;
                if (seen.Add(withPrefix)) yield return withPrefix;
            }
        }
    }

    // ship_ports.equipped_item は entityClassReference 由来の小文字 basename (klwe_laserrepeater_s3 等) のことがあり、
    // record_name (EntityClassDefinition.KLWE_LaserRepeater_S3) と大文字小文字が一致しないため COLLATE NOCASE で照合する
    private EquipmentItem? QueryItemByRecord(string recordName)
    {
        using var cmd = _conn.CreateCommand();
        var sizeExpr = _hasItemIndexSize ? "COALESCE(x.size, 0)" : "0";
        cmd.CommandText = $@"SELECT i.record_name, i.name, i.item_type, i.size, i.grade, i.manufacturer, i.component_type, i.component_json, x.name, {sizeExpr}
                            FROM items i LEFT JOIN item_index x ON x.record_name = i.record_name
                            WHERE i.record_name = @rn COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("@rn", recordName);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var rawName = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1);
        var indexName = reader.IsDBNull(8) ? "" : reader.GetString(8);
        var itemsSize = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        var indexSize = reader.IsDBNull(9) ? 0 : reader.GetInt32(9);
        var item = new EquipmentItem
        {
            RecordName = reader.GetString(0),
            Name = indexName.Length > 0 ? indexName : rawName,
            ItemType = reader.IsDBNull(2) ? "" : reader.GetString(2),
            // items.size を優先し、0 なら item_index.size、それも 0 なら record 名から補完
            Size = itemsSize > 0 ? itemsSize : indexSize > 0 ? indexSize : SizeFromRecordName(reader.GetString(0)),
            Grade = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            Manufacturer = reader.IsDBNull(5) ? "" : reader.GetString(5),
            ComponentType = reader.IsDBNull(6) ? "" : reader.GetString(6),
            ComponentJson = reader.IsDBNull(7) ? "" : reader.GetString(7),
        };
        ParseStats(item);
        return item;
    }

    // item_index から 1 件。Size は item_index.size (AttachDef.Size 由来。列が無い旧 DB / 0 のときは record 名の "_S{n}" から補完)。
    // record_name の照合は COLLATE NOCASE (QueryItemByRecord と同じ理由)
    private EquipmentItem? QueryItemIndexByRecord(string recordName)
    {
        using var cmd = _conn.CreateCommand();
        var sizeExpr = _hasItemIndexSize ? "COALESCE(size, 0)" : "0";
        cmd.CommandText = $"SELECT record_name, name, item_type, sub_type, manufacturer, {sizeExpr} FROM item_index WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("@rn", recordName);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var rec = reader.IsDBNull(0) ? recordName : reader.GetString(0);
        var indexSize = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
        return new EquipmentItem
        {
            RecordName = rec,
            Name = reader.IsDBNull(1) || reader.GetString(1).Length == 0 ? rec : reader.GetString(1),
            ItemType = reader.IsDBNull(2) ? "" : reader.GetString(2),
            SubType = reader.IsDBNull(3) ? "" : reader.GetString(3),
            Manufacturer = reader.IsDBNull(4) ? "" : reader.GetString(4),
            Size = indexSize > 0 ? indexSize : SizeFromRecordName(rec),
        };
    }

    public string? FindShipRecordName(string shipName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT record_name FROM ships WHERE name = @n LIMIT 1";
        cmd.Parameters.AddWithValue("@n", shipName);
        var result = cmd.ExecuteScalar();
        if (result != null) return result.ToString();

        cmd.CommandText = "SELECT record_name FROM ships WHERE name LIKE @q ORDER BY LENGTH(name) LIMIT 1";
        cmd.Parameters.Clear();
        cmd.Parameters.AddWithValue("@q", $"%{shipName}%");
        result = cmd.ExecuteScalar();
        return result?.ToString();
    }

    public HashSet<int> GetCompatibleSizes(IEnumerable<string> myShipNames, string componentType)
    {
        var sizes = new HashSet<int>();
        var itemType = ComponentTypeToPortType(componentType);
        if (string.IsNullOrEmpty(itemType)) return sizes;

        foreach (var shipName in myShipNames)
        {
            var recordName = FindShipRecordName(shipName);
            if (recordName == null) continue;

            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT size FROM ship_ports WHERE ship_record_name = @srn AND item_type = @it AND size > 0";
            cmd.Parameters.AddWithValue("@srn", recordName);
            cmd.Parameters.AddWithValue("@it", itemType);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                sizes.Add(reader.GetInt32(0));
        }
        return sizes;
    }

    // カテゴリ Key → ship_ports.item_type。Key は item_type そのもの (ComponentCategories にあるものはそのまま返す)。
    // 旧 Key (SCItem…Params) も互換のため受ける
    private static string ComponentTypeToPortType(string componentType)
    {
        if (ComponentCategories.Any(c => c.Key == componentType)) return componentType;
        return componentType switch
        {
            "SCItemShieldGeneratorParams" => "Shield",
            "SCItemPowerPlantParams" => "PowerPlant",
            "SCItemQuantumDriveParams" => "QuantumDrive",
            "SCItemCoolerParams" => "Cooler",
            "SCItemWeaponComponentParams" => "WeaponGun",
            _ => ""
        };
    }

    private static void ParseStats(EquipmentItem item)
    {
        if (string.IsNullOrEmpty(item.ComponentJson) || string.IsNullOrEmpty(item.ComponentType))
            return;

        if (StatLabels.TryGetValue(item.ComponentType, out var labels))
        {
            item.Stat1Label = labels.stat1Label;
            item.Stat2Label = labels.stat2Label;
        }

        try
        {
            using var doc = JsonDocument.Parse(item.ComponentJson);
            var el = doc.RootElement;

            switch (item.ComponentType)
            {
                case "SCItemShieldGeneratorParams":
                    if (el.TryGetProperty("MaxShieldHealth", out var mh))
                        item.Stat1Value = FormatNumber(mh);
                    if (el.TryGetProperty("MaxShieldRegen", out var mr))
                        item.Stat2Value = FormatNumber(mr);
                    break;
                case "SCItemPowerPlantParams":
                    if (el.TryGetProperty("PowerOutput", out var po))
                        item.Stat1Value = FormatNumber(po);
                    break;
                case "SCItemCoolerParams":
                    if (el.TryGetProperty("CoolingRate", out var cr))
                        item.Stat1Value = FormatNumber(cr);
                    break;
                case "SCItemQuantumDriveParams":
                    if (el.TryGetProperty("spoolUpTime", out var su))
                        item.Stat1Value = $"{FormatNumber(su)}s";
                    if (el.TryGetProperty("quantumFuelRequirement", out var qf))
                        item.Stat2Value = FormatNumber(qf);
                    break;
                case "SCItemWeaponComponentParams":
                    if (el.TryGetProperty("fireRate", out var fr))
                        item.Stat1Value = FormatNumber(fr);
                    break;
            }
        }
        catch { }
    }

    public string FormatDetail(EquipmentItem item)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"■ {item.Name}");
        sb.AppendLine($"  レコード名: {item.RecordName}");
        if (!string.IsNullOrEmpty(item.Manufacturer))
            sb.AppendLine($"  メーカー: {item.Manufacturer}");
        if (!string.IsNullOrEmpty(item.ItemType))
            sb.AppendLine($"  タイプ: {item.ItemType}");
        if (!string.IsNullOrEmpty(item.SubType))
            sb.AppendLine($"  サブタイプ: {item.SubType}");
        if (item.Size > 0)
            sb.AppendLine($"  サイズ: S{item.Size}");
        if (item.Grade > 0)
            sb.AppendLine($"  グレード: {item.Grade}");

        if (!string.IsNullOrEmpty(item.ComponentJson) && !string.IsNullOrEmpty(item.ComponentType))
        {
            sb.AppendLine();
            sb.AppendLine("■ 性能データ");
            try
            {
                using var doc = JsonDocument.Parse(item.ComponentJson);
                var el = doc.RootElement;
                foreach (var prop in el.EnumerateObject())
                {
                    var val = prop.Value.ValueKind switch
                    {
                        JsonValueKind.Number => prop.Value.TryGetDouble(out var d) ? d.ToString("0.##") : prop.Value.ToString(),
                        JsonValueKind.String => prop.Value.GetString() ?? "",
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => prop.Value.ToString()
                    };
                    sb.AppendLine($"  {prop.Name}: {val}");
                }
            }
            catch { }
        }

        return sb.ToString();
    }

    // 購入先の一括取得で対象にする UEX の section (装備に関係するものだけ)
    private static readonly string[] UexEquipmentSections =
        { "Systems", "Vehicle Weapons", "Avionics", "Propulsion", "Utility", "Personal Weapons" };

    // 購入先の一括取得。UEX のカテゴリ一覧を取り、装備に関係する section のカテゴリごとに
    // items_prices?id_category={id} を 1 回ずつ呼んで uex_item_prices を作り直す (per-item 取得より桁違いに少ない回数で済む)。
    // 完了したら uex_prefetch_state に時刻を記録する (「未取得」と「購入先の登録が無い」を区別するため)。
    // 戻り値は保存した item_name の数。失敗は例外を投げずに握りつぶし、途中まで保存した分は残す
    public async Task<int> PrefetchPurchaseLocationsAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var categoryIds = new List<int>();
        try
        {
            var catResp = await Http.GetStringAsync("https://api.uexcorp.space/2.0/categories", ct);
            using var catDoc = JsonDocument.Parse(catResp);
            var root = catDoc.RootElement;
            var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (!status.Equals("ok", StringComparison.OrdinalIgnoreCase)) return 0;
            if (!root.TryGetProperty("data", out var catData) || catData.ValueKind != JsonValueKind.Array) return 0;

            foreach (var c in catData.EnumerateArray())
            {
                var section = c.TryGetProperty("section", out var se) ? se.GetString() ?? "" : "";
                if (!UexEquipmentSections.Contains(section, StringComparer.Ordinal)) continue;
                if (!c.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                if (idEl.TryGetInt32(out var id)) categoryIds.Add(id);
            }
        }
        catch { return 0; }

        var savedNames = new HashSet<string>(StringComparer.Ordinal);
        var total = categoryIds.Count;
        var done = 0;
        var okCategories = 0;

        foreach (var catId in categoryIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items_prices?id_category={catId}", ct);
                using var doc = JsonDocument.Parse(resp);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    var rows = new List<(string itemName, string location, double price)>();
                    foreach (var p in data.EnumerateArray())
                    {
                        var itemName = p.TryGetProperty("item_name", out var inm) ? inm.GetString() ?? "" : "";
                        if (string.IsNullOrWhiteSpace(itemName)) continue;
                        if (!p.TryGetProperty("price_buy", out var pb) || pb.ValueKind != JsonValueKind.Number) continue;
                        if (!pb.TryGetDouble(out var buy) || buy <= 0) continue;

                        var terminal = p.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
                        var city = p.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
                        var planet = p.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
                        var star = p.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
                        var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(s => !string.IsNullOrEmpty(s)));
                        rows.Add((itemName, location, buy));
                    }

                    if (rows.Count > 0)
                    {
                        SavePrefetchedCategory(rows);
                        foreach (var r in rows) savedNames.Add(r.itemName);
                    }
                    okCategories++;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }

            done++;
            onStatus?.Invoke($"購入先を取得中… {done}/{total}");
        }

        if (okCategories > 0)
        {
            try
            {
                using var db = new SqliteConnection($"Data Source={_cacheDbPath}");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "INSERT OR REPLACE INTO uex_prefetch_state (id, fetched_at) VALUES (1, @t)";
                cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            catch { }
        }

        ReloadPriceIndex();
        return savedNames.Count;
    }

    // プリフェッチ 1 カテゴリ分をまとめて保存する。そのカテゴリに出てきた item_name の行だけ入れ替える
    private void SavePrefetchedCategory(List<(string itemName, string location, double price)> rows)
    {
        using var db = new SqliteConnection($"Data Source={_cacheDbPath}");
        db.Open();
        using var tx = db.BeginTransaction();

        using (var delCmd = db.CreateCommand())
        {
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM uex_item_prices WHERE item_name = @n";
            var delName = delCmd.Parameters.Add("@n", SqliteType.Text);
            foreach (var name in rows.Select(r => r.itemName).Distinct(StringComparer.Ordinal))
            {
                delName.Value = name;
                delCmd.ExecuteNonQuery();
            }
        }

        var now = DateTime.UtcNow.ToString("o");
        using (var insCmd = db.CreateCommand())
        {
            insCmd.Transaction = tx;
            insCmd.CommandText = "INSERT OR REPLACE INTO uex_item_prices (item_name, location, price, fetched_at) VALUES (@n, @l, @p, @t)";
            var insName = insCmd.Parameters.Add("@n", SqliteType.Text);
            var insLoc = insCmd.Parameters.Add("@l", SqliteType.Text);
            var insPrice = insCmd.Parameters.Add("@p", SqliteType.Real);
            insCmd.Parameters.AddWithValue("@t", now);
            foreach (var (itemName, location, price) in rows)
            {
                insName.Value = itemName;
                insLoc.Value = location;
                insPrice.Value = price;
                insCmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    // equipment_cache.db の購入先を丸ごと読んでオンメモリ索引を作り直す。
    // 呼ぶのは ctor (プリフェッチ開始前なので競合しない) と PrefetchPurchaseLocationsAsync の完了時 (バックグラウンドスレッド) だけ
    private void ReloadPriceIndex()
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={_cacheDbPath};Mode=ReadOnly");
            db.Open();

            var index = new Dictionary<string, List<(string Location, double Price)>>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = "SELECT item_name, location, price FROM uex_item_prices";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0)) continue;
                    var name = reader.GetString(0);
                    if (!index.TryGetValue(name, out var list)) index[name] = list = new List<(string Location, double Price)>();
                    list.Add((reader.IsDBNull(1) ? "" : reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetDouble(2)));
                }
            }
            foreach (var list in index.Values) list.Sort((a, b) => a.Price.CompareTo(b.Price));

            bool prefetched;
            using (var stateCmd = db.CreateCommand())
            {
                stateCmd.CommandText = "SELECT COUNT(*) FROM uex_prefetch_state WHERE id = 1";
                prefetched = Convert.ToInt64(stateCmd.ExecuteScalar() ?? 0L) > 0;
            }

            _priceIndex = index;
            _pricePrefetched = prefetched;
            _englishNameByRecord = LoadEnglishNameIndex();
        }
        catch { }
    }

    // translations.db から "item_Name<record>" のキーを読み、record → 英語名の索引を作る。
    // キーの ",P" は落とし、"_SCItem" の有無どちらでも引けるように両方の綴りで登録する
    private Dictionary<string, string> LoadEnglishNameIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var transPath = Path.Combine(Path.GetDirectoryName(_cacheDbPath) ?? ".", "translations.db");
            if (!File.Exists(transPath)) return map;

            using var db = new SqliteConnection($"Data Source={transPath};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT key, english FROM translations WHERE key LIKE 'item_Name%' AND english IS NOT NULL AND english <> ''";
            using var reader = cmd.ExecuteReader();
            const string prefix = "item_Name";
            const string scItem = "_SCItem";
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rec = key[prefix.Length..];
                if (rec.EndsWith(",P", StringComparison.Ordinal)) rec = rec[..^2];
                if (rec.Length == 0) continue;
                var en = reader.GetString(1);
                map.TryAdd(rec, en);
                var alt = rec.EndsWith(scItem, StringComparison.OrdinalIgnoreCase) ? rec[..^scItem.Length] : rec + scItem;
                map.TryAdd(alt, en);
            }
        }
        catch { }
        return map;
    }

    // record_name から英語のアイテム名を引く。引けなければ null
    private string? ResolveEnglishItemName(string? itemRecord)
    {
        var rec = (itemRecord ?? "").Trim();
        if (rec.Length == 0) return null;
        const string prefix = "EntityClassDefinition.";
        if (rec.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rec = rec[prefix.Length..];
        return _englishNameByRecord.TryGetValue(rec, out var en) ? en : null;
    }

    // ツールチップ用。オンメモリ索引だけを見る同期メソッド (DB もネットワークも触らないので UI スレッドから呼んでよい)。
    // 画面の表示名は日本語のことがあるので、見つからなければ record から引いた英語名でも探す
    public string GetPurchaseLocationText(string itemName, string? itemRecord = null)
    {
        var display = (itemName ?? "").Trim();
        var english = ResolveEnglishItemName(itemRecord);
        if (display.Length == 0 && string.IsNullOrEmpty(english)) return "";

        var lines = new List<string> { "■ 購入場所 (UEX)" };
        foreach (var name in new[] { display, english })
        {
            if (string.IsNullOrEmpty(name)) continue;
            if (!_priceIndex.TryGetValue(name, out var locations) || locations.Count == 0) continue;

            foreach (var (loc, price) in locations.Take(10))
                lines.Add($"  {loc} — {price:N0} aUEC");
            if (locations.Count > 10)
                lines.Add($"  他 {locations.Count - 10} 件");
            return string.Join("\n", lines);
        }

        lines.Add(_pricePrefetched ? "  購入先の登録がありません" : "  取得中…");
        return string.Join("\n", lines);
    }

    public (string? text, bool fromCache) GetCachedPurchaseLocations(string itemName)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={_cacheDbPath};Mode=ReadOnly");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT location, price, fetched_at FROM uex_item_prices WHERE item_name = @n ORDER BY price";
            cmd.Parameters.AddWithValue("@n", itemName);
            using var reader = cmd.ExecuteReader();

            var sb = new StringBuilder();
            sb.AppendLine("\n■ 購入場所 (UEX)");
            int count = 0;
            DateTime? fetchedAt = null;
            while (reader.Read())
            {
                var location = reader.GetString(0);
                var price = reader.GetDouble(1);
                sb.AppendLine($"  {location} — {price:N0} aUEC");
                count++;
                if (!fetchedAt.HasValue && !reader.IsDBNull(2))
                    fetchedAt = DateTime.TryParse(reader.GetString(2), out var dt) ? dt : null;
            }

            if (count == 0) return (null, false);

            var isStale = !fetchedAt.HasValue || DateTime.UtcNow - fetchedAt.Value > CacheTtl;
            if (isStale)
                sb.AppendLine("  (キャッシュ — バックグラウンドで更新中...)");
            else if (fetchedAt.HasValue)
                sb.AppendLine($"  (キャッシュ: {fetchedAt.Value.ToLocalTime():MM/dd HH:mm})");
            return (sb.ToString(), !isStale);
        }
        catch { return (null, false); }
    }

    public async Task<string?> FetchAndCacheUexPurchaseLocations(string itemName, string? componentType = null)
    {
        try
        {
            int[] categoryIds;
            var catInfo = componentType != null ? ComponentCategories.FirstOrDefault(c => c.Key == componentType) : default;
            if (catInfo.Key != null && catInfo.UexCategoryIds.Length > 0)
                categoryIds = catInfo.UexCategoryIds;
            else
                categoryIds = new[] { 18, 17 };

            int? matchedItemId = null;
            foreach (var catId in categoryIds)
            {
                var resp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items?id_category={catId}");
                using var doc = JsonDocument.Parse(resp);
                if (!doc.RootElement.TryGetProperty("data", out var data)) continue;
                foreach (var item in data.EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString() ?? "";
                    if (name.Equals(itemName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedItemId = item.GetProperty("id").GetInt32();
                        break;
                    }
                }
                if (matchedItemId.HasValue) break;
            }

            if (!matchedItemId.HasValue) return null;

            var priceResp = await Http.GetStringAsync($"https://api.uexcorp.space/2.0/items_prices?id_item={matchedItemId}");
            using var priceDoc = JsonDocument.Parse(priceResp);
            if (!priceDoc.RootElement.TryGetProperty("data", out var priceData)) return null;

            var locations = new List<(string location, double price)>();
            foreach (var p in priceData.EnumerateArray())
            {
                var terminal = p.TryGetProperty("terminal_name", out var tn) ? tn.GetString() ?? "" : "";
                var city = p.TryGetProperty("city_name", out var cn) ? cn.GetString() ?? "" : "";
                var planet = p.TryGetProperty("planet_name", out var pn) ? pn.GetString() ?? "" : "";
                var star = p.TryGetProperty("star_system_name", out var sn) ? sn.GetString() ?? "" : "";
                var buy = p.TryGetProperty("price_buy", out var pb) && pb.ValueKind == JsonValueKind.Number ? pb.GetDouble() : 0;

                if (buy > 0)
                {
                    var location = string.Join(" > ", new[] { star, planet, city, terminal }.Where(s => !string.IsNullOrEmpty(s)));
                    locations.Add((location, buy));
                }
            }

            if (locations.Count == 0) return null;

            SavePriceCache(itemName, locations);

            var sb = new StringBuilder();
            sb.AppendLine("\n■ 購入場所 (UEX)");
            foreach (var (loc, price) in locations.OrderBy(l => l.price))
                sb.AppendLine($"  {loc} — {price:N0} aUEC");
            sb.AppendLine($"  (取得: {DateTime.Now:MM/dd HH:mm})");
            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void SavePriceCache(string itemName, List<(string location, double price)> locations)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={_cacheDbPath}");
            db.Open();
            using var tx = db.BeginTransaction();

            using var delCmd = db.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = "DELETE FROM uex_item_prices WHERE item_name = @n";
            delCmd.Parameters.AddWithValue("@n", itemName);
            delCmd.ExecuteNonQuery();

            var now = DateTime.UtcNow.ToString("o");
            foreach (var (loc, price) in locations)
            {
                using var insCmd = db.CreateCommand();
                insCmd.Transaction = tx;
                insCmd.CommandText = "INSERT INTO uex_item_prices (item_name, location, price, fetched_at) VALUES (@n, @l, @p, @t)";
                insCmd.Parameters.AddWithValue("@n", itemName);
                insCmd.Parameters.AddWithValue("@l", loc);
                insCmd.Parameters.AddWithValue("@p", price);
                insCmd.Parameters.AddWithValue("@t", now);
                insCmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch { }
    }

    private static string FormatNumber(JsonElement el)
    {
        if (el.TryGetDouble(out var d))
            return d >= 1000 ? d.ToString("N0") : d.ToString("0.##");
        return el.ToString();
    }

    public void Dispose()
    {
        _conn?.Dispose();
    }
}

public class EquipmentCategory
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Count { get; set; }
    public override string ToString() => $"{DisplayName} ({Count})";
}

public class EquipmentItem
{
    public string RecordName { get; set; } = "";
    public string Name { get; set; } = "";
    public string ItemType { get; set; } = "";
    public string SubType { get; set; } = "";
    public int Size { get; set; }
    public int Grade { get; set; }
    public string Manufacturer { get; set; } = "";
    public string ComponentType { get; set; } = "";
    public string ComponentJson { get; set; } = "";
    public string Stat1Label { get; set; } = "";
    public string Stat1Value { get; set; } = "";
    public string Stat2Label { get; set; } = "";
    public string Stat2Value { get; set; } = "";
    public string SizeDisplay => Size > 0 ? $"S{Size}" : "";
    public string GradeDisplay => Grade > 0 ? $"{Grade}" : "";
    public string Stat1Display => !string.IsNullOrEmpty(Stat1Value) ? $"{Stat1Label}: {Stat1Value}" : "";
    public string Stat2Display => !string.IsNullOrEmpty(Stat2Value) ? $"{Stat2Label}: {Stat2Value}" : "";
}

public class ShipPortInfo
{
    // ship_ports.port_name。子ポート行は "{親port}/{子port}" (例 hardpoint_weapon_left_nose/hardpoint_class_2)
    public string PortName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int Size { get; set; }
    public string EquippedItem { get; set; } = "";
    // 子ポート行か (port_name に "/" を含む)
    public bool IsChild => PortName.Contains('/');
    // 直接の親ポートの port_name (最後の "/" より前)。最上位ポートは ""
    public string ParentPortName
    {
        get
        {
            var i = PortName.LastIndexOf('/');
            return i < 0 ? "" : PortName[..i];
        }
    }
    public string SizeDisplay => Size > 0 ? $"S{Size}" : "-";
    public string TypeDisplay => ItemType switch
    {
        "WeaponGun" => "武器",
        "WeaponMount" => "ジンバル/マウント",
        "Shield" => "シールド",
        "PowerPlant" => "パワープラント",
        "QuantumDrive" => "量子ドライブ",
        "JumpDrive" => "ジャンプドライブ",
        "Cooler" => "クーラー",
        "MissileLauncher" => "ミサイルラック",
        "Missile" => "ミサイル",
        "Turret" => "タレット",
        "WeaponRack" => "武器ラック",
        "Radar" => "レーダー",
        "Avionics" => "アビオニクス",
        "LifeSupport" => "ライフサポート",
        "CounterMeasure" => "カウンターメジャー",
        "FuelTank" => "燃料タンク",
        "QuantumFuelTank" => "量子燃料タンク",
        "FuelIntake" => "燃料インテーク",
        _ => ItemType
    };
}
