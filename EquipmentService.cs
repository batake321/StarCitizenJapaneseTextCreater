using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class EquipmentService : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _cacheDbPath;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly Dictionary<string, (string displayName, int[] uexCategoryIds)> ComponentCategories = new()
    {
        ["SCItemShieldGeneratorParams"] = ("シールド", new[] { 23 }),
        ["SCItemPowerPlantParams"] = ("パワープラント", new[] { 21 }),
        ["SCItemQuantumDriveParams"] = ("量子ドライブ", new[] { 22 }),
        ["SCItemCoolerParams"] = ("クーラー", new[] { 19 }),
        ["SCItemWeaponComponentParams"] = ("船舶武器", new[] { 32, 33, 34, 35 }),
    };

    private static readonly Dictionary<string, (string stat1Label, string stat2Label)> StatLabels = new()
    {
        ["SCItemShieldGeneratorParams"] = ("最大HP", "再生速度"),
        ["SCItemPowerPlantParams"] = ("出力", ""),
        ["SCItemCoolerParams"] = ("冷却速度", ""),
        ["SCItemQuantumDriveParams"] = ("スプール時間", "燃料消費"),
        ["SCItemWeaponComponentParams"] = ("発射速度", ""),
    };

    public EquipmentService(string gamedataCacheDbPath)
    {
        _conn = new SqliteConnection($"Data Source={gamedataCacheDbPath};Mode=ReadOnly");
        _conn.Open();
        _cacheDbPath = Path.Combine(Path.GetDirectoryName(gamedataCacheDbPath) ?? ".", "equipment_cache.db");
        InitCacheDb();
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
    }

    public List<EquipmentCategory> GetShipComponentCategories()
    {
        var categories = new List<EquipmentCategory>();
        foreach (var (compType, (displayName, _)) in ComponentCategories)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM items WHERE component_type = @ct";
            cmd.Parameters.AddWithValue("@ct", compType);
            var count = (int)(long)(cmd.ExecuteScalar() ?? 0L);
            if (count > 0)
                categories.Add(new EquipmentCategory { Key = compType, DisplayName = displayName, Count = count });
        }
        return categories;
    }

    public List<EquipmentItem> GetShipComponents(string componentType, string? search = null, int? sizeFilter = null)
    {
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string> { "component_type = @ct" };
        cmd.Parameters.AddWithValue("@ct", componentType);

        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(name LIKE @q OR manufacturer LIKE @q OR record_name LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{search}%");
        }
        if (sizeFilter.HasValue)
        {
            conditions.Add("size = @sz");
            cmd.Parameters.AddWithValue("@sz", sizeFilter.Value);
        }

        cmd.CommandText = $"SELECT record_name, name, item_type, size, grade, manufacturer, component_type, component_json FROM items WHERE {string.Join(" AND ", conditions)} ORDER BY size DESC, grade DESC, name";
        using var reader = cmd.ExecuteReader();

        var items = new List<EquipmentItem>();
        while (reader.Read())
        {
            var item = new EquipmentItem
            {
                RecordName = reader.GetString(0),
                Name = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1),
                ItemType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Size = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                Grade = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Manufacturer = reader.IsDBNull(5) ? "" : reader.GetString(5),
                ComponentType = reader.IsDBNull(6) ? "" : reader.GetString(6),
                ComponentJson = reader.IsDBNull(7) ? "" : reader.GetString(7),
            };
            ParseStats(item);
            items.Add(item);
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

    private static string ComponentTypeToPortType(string componentType)
    {
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
            if (componentType != null && ComponentCategories.TryGetValue(componentType, out var catInfo))
                categoryIds = catInfo.uexCategoryIds;
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
            delCmd.CommandText = "DELETE FROM uex_item_prices WHERE item_name = @n";
            delCmd.Parameters.AddWithValue("@n", itemName);
            delCmd.ExecuteNonQuery();

            var now = DateTime.UtcNow.ToString("o");
            foreach (var (loc, price) in locations)
            {
                using var insCmd = db.CreateCommand();
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
    public string PortName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int Size { get; set; }
    public string EquippedItem { get; set; } = "";
    public string SizeDisplay => Size > 0 ? $"S{Size}" : "-";
    public string TypeDisplay => ItemType switch
    {
        "WeaponGun" => "武器",
        "Shield" => "シールド",
        "PowerPlant" => "パワープラント",
        "QuantumDrive" => "量子ドライブ",
        "Cooler" => "クーラー",
        "MissileLauncher" => "ミサイル",
        "Turret" => "タレット",
        "Radar" => "レーダー",
        "Avionics" => "アビオニクス",
        "LifeSupport" => "ライフサポート",
        "CounterMeasure" => "カウンターメジャー",
        _ => ItemType
    };
}
