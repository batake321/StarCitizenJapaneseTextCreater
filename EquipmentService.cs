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

    // 装備カテゴリ。Key は items.item_type (船舶武器 WeaponGun・タレット Turret・ジンバル/マウント WeaponMount は items に無く item_index から引く)。
    // 表示順はこの並び。uexCategoryIds は UEX の購入場所検索用 (UEX の /2.0/categories で確かめた id。
    // 当たるカテゴリを確かめられないものは空 → 購入場所を検索しない。既定のカテゴリでは探さない)
    private const string WeaponGunType = "WeaponGun";
    internal const string TurretType = "Turret";
    internal const string WeaponMountType = "WeaponMount";
    private static readonly List<(string Key, string DisplayName, int[] UexCategoryIds)> ComponentCategories = new()
    {
        ("PowerPlant", "パワープラント", new[] { 21 }),
        ("Shield", "シールド", new[] { 23 }),
        ("QuantumDrive", "量子ドライブ", new[] { 22 }),
        ("JumpDrive", "ジャンプドライブ", Array.Empty<int>()),
        ("Cooler", "クーラー", new[] { 19 }),
        ("Radar", "レーダー", new[] { 83 }),
        ("MissileLauncher", "ミサイルラック", new[] { 33 }),
        ("Missile", "ミサイル", new[] { 34 }),
        ("FuelTank", "燃料タンク", Array.Empty<int>()),
        ("QuantumFuelTank", "量子燃料タンク", Array.Empty<int>()),
        ("FuelIntake", "燃料インテーク", Array.Empty<int>()),
        (WeaponGunType, "船舶武器", new[] { 32, 33, 34, 35 }),
        (TurretType, "タレット", new[] { 35 }),
        (WeaponMountType, "ジンバル/マウント", Array.Empty<int>()),
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
    // ship_ports の min_size / max_size / port_types 列、item_ports 表の有無 (インデックスの作り直し前の DB には無い)
    private readonly bool _hasShipPortSizes;
    private readonly bool _hasItemPorts;

    public EquipmentService(string gamedataCacheDbPath) : this(gamedataCacheDbPath, loadPriceCache: true) { }

    // loadPriceCache: false なら equipment_cache.db (購入場所の覚え) を開かない (入れられる品の索引を裏で作るときの、読むだけの口)
    private EquipmentService(string gamedataCacheDbPath, bool loadPriceCache)
    {
        _gamedataDbPath = gamedataCacheDbPath;
        _conn = new SqliteConnection($"Data Source={gamedataCacheDbPath};Mode=ReadOnly");
        _conn.Open();
        _hasItemIndexSize = HasColumn("item_index", "size");
        _hasShipPortSizes = HasColumn("ship_ports", "max_size") && HasColumn("ship_ports", "min_size") && HasColumn("ship_ports", "port_types");
        _hasItemPorts = HasColumn("item_ports", "max_size");
        _cacheDbPath = Path.Combine(Path.GetDirectoryName(gamedataCacheDbPath) ?? ".", "equipment_cache.db");
        if (!loadPriceCache) return;
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
        if (itemType == TurretType || itemType == WeaponMountType) return GetTurretsOrMounts(itemType, search);

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

    // ジンバル/マウントの record か。
    //   1. 船の既定の装備 (ship_ports.equipped_item) に使われていれば、そのポートの種別に合わせる
    //      (WeaponMount のポート → ジンバル/マウント、Turret のポート → タレット。例 MISC_Starlancer_TAC_Missile_Gimbal は Turret のポートなのでタレット)
    //   2. どちらのポートにも使われていなければ (使われていない・WeaponGun のポートだけ等)、IsMountRecordByName (record 名の規則)
    // item_index の item_type / sub_type ではジンバル (Mount_Gimbal_S3 = Turret / GunTurret) とリモートタレット
    // (AEGS_Reclaimer_SCItem_Remote_Turret = Turret / GunTurret) を見分けられないため、使われ方と record 名で分ける
    private bool IsMountRecord(string? record)
    {
        if (DefaultEquipPortTypes().TryGetValue(RecordBaseName(record), out var types))
        {
            if (types.Contains(WeaponMountType)) return true;
            if (types.Contains(TurretType)) return false;
        }
        return IsMountRecordByName(record);
    }

    // record 名の規則: record 名 (EntityClassDefinition. を除く) が mount_gimbal / weaponmount で始まるもの
    // (ship_ports.item_type を WeaponMount にする規則 GameDataExtractor.OverridePortTypeByEntity と同じ)、
    // または record 名に gimbal を含むもの (AEGS_Javelin_Manned_Turret_Gimbal_S5 等)。大文字小文字無視
    internal static bool IsMountRecordByName(string? record)
    {
        var b = RecordBaseName(record);
        return b.StartsWith("mount_gimbal", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith("weaponmount", StringComparison.OrdinalIgnoreCase)
            || b.Contains("gimbal", StringComparison.OrdinalIgnoreCase);
    }

    // record 名から "EntityClassDefinition." を除いた部分
    private static string RecordBaseName(string? record)
    {
        const string prefix = "EntityClassDefinition.";
        var r = record ?? "";
        return r.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? r[prefix.Length..] : r;
    }

    // タレット (Turret) / ジンバル/マウント (WeaponMount) の候補 (item_index から)。
    //   タレット         = item_index.item_type が Turret で、IsMountRecord (既定の装備のポートの種別、無ければ record 名) に当たらないもの
    //                     (TurretBase・UtilityTurret は含めない)
    //   ジンバル/マウント = item_index.item_type が Turret または WeaponMount で、IsMountRecord に当たるもの。ItemType は WeaponMount にする
    //                     (ship_ports のマウントのポートの item_type と同じ。現在の装備の候補はこの種別で照合する)
    // record ごとに 1 件 (同じ record の重複行だけをまとめる)。表示名とサイズが同じものが 2 件以上あるときは、見分けが付くよう
    // 名前の後ろに「(…)」を付ける: その record を既定の装備に使っている船の名前 (ships.name_en。使っている行数の多い順に 2 隻まで、
    // 3 隻以上なら「ほか」)。使っている船が無い (または船の名前が分からない) ときは record 名。
    // それでも同じ文字になるものは、さらに「/ record 名」を付ける。表示名がプレースホルダ・空のものは除く
    private List<EquipmentItem> GetTurretsOrMounts(string category, string? search)
    {
        var sizeExpr = _hasItemIndexSize ? "COALESCE(size, 0)" : "0";
        var rows = new List<EquipmentItem>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT record_name, name, item_type, sub_type, manufacturer, {sizeExpr} FROM item_index WHERE item_type IN ('Turret', 'WeaponMount')";
            using var reader = cmd.ExecuteReader();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var rec = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (rec.Length == 0 || IsPlaceholderName(name)) continue;
                var isMount = IsMountRecord(rec);
                if (category == WeaponMountType ? !isMount : (isMount || type != TurretType)) continue;
                if (!seen.Add(rec)) continue;
                var size = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                rows.Add(new EquipmentItem
                {
                    RecordName = rec,
                    Name = name,
                    ItemType = category,
                    SubType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    Manufacturer = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    Size = size > 0 ? size : SizeFromRecordName(rec),
                });
            }
        }

        // 表示名とサイズが同じものに区別を付ける
        var usage = DefaultEquipShips();
        string Note(EquipmentItem i)
        {
            if (usage.TryGetValue(RecordBaseName(i.RecordName), out var ships) && ships.Count > 0)
                return string.Join("・", ships.Take(2)) + (ships.Count > 2 ? " ほか" : "");
            return RecordBaseName(i.RecordName);
        }
        foreach (var g in rows.GroupBy(i => (i.Name, i.Size)).Where(g => g.Count() > 1))
        {
            var notes = g.Select(i => (Item: i, Note: Note(i))).ToList();
            foreach (var n in notes)
            {
                var dup = notes.Count(x => x.Note == n.Note) > 1;
                var baseName = RecordBaseName(n.Item.RecordName);
                var text = dup && n.Note != baseName ? $"{n.Note} / {baseName}" : n.Note;
                n.Item.Name = $"{n.Item.Name} ({text})";
            }
        }

        return rows
            .Where(i => string.IsNullOrWhiteSpace(search)
                || i.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || i.Manufacturer.Contains(search, StringComparison.OrdinalIgnoreCase)
                || i.RecordName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Size).ThenBy(i => i.RecordName, StringComparer.Ordinal)
            .ToList();
    }

    // record がどれかの船の既定の装備 (ship_ports.equipped_item。ポートの種別は問わない) に使われているか
    public bool IsDefaultEquipOfAnyShip(string? record) => DefaultEquipRecords(null).Contains(RecordBaseName(record));

    // record が船 shipRecordName (ships.record_name) の既定の装備 (ship_ports.equipped_item。ポートの種別は問わない) に使われているか
    public bool IsDefaultEquipOfShip(string? record, string shipRecordName) => DefaultEquipRecords(shipRecordName).Contains(RecordBaseName(record));

    // 既定の装備の record 名 (EntityClassDefinition. を除く。大文字小文字無視) の集合。shipRecordName が null なら全部の船、
    // それ以外はその船だけ。表が無ければ空
    private readonly Dictionary<string, HashSet<string>> _defaultEquipRecords = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> DefaultEquipRecords(string? shipRecordName)
    {
        var key = shipRecordName ?? "";
        if (_defaultEquipRecords.TryGetValue(key, out var cached)) return cached;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = shipRecordName == null
                ? "SELECT DISTINCT equipped_item FROM ship_ports WHERE equipped_item IS NOT NULL AND equipped_item <> ''"
                : "SELECT DISTINCT equipped_item FROM ship_ports WHERE ship_record_name = @srn AND equipped_item IS NOT NULL AND equipped_item <> ''";
            if (shipRecordName != null) cmd.Parameters.AddWithValue("@srn", shipRecordName);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) set.Add(RecordBaseName(reader.GetString(0)));
        }
        catch (SqliteException) { }
        return _defaultEquipRecords[key] = set;
    }

    // 船の既定の装備 (ship_ports.equipped_item。小文字の basename のことがあるので大文字小文字無視) → それが付いているポートの種別
    // (ship_ports.item_type のうち Turret / WeaponMount だけ)。表が無ければ空
    private Dictionary<string, HashSet<string>>? _defaultEquipPortTypes;

    private Dictionary<string, HashSet<string>> DefaultEquipPortTypes()
    {
        if (_defaultEquipPortTypes != null) return _defaultEquipPortTypes;
        var d = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT equipped_item, item_type FROM ship_ports WHERE equipped_item IS NOT NULL AND equipped_item <> '' AND item_type IN ('Turret', 'WeaponMount')";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var k = RecordBaseName(reader.GetString(0));
                if (!d.TryGetValue(k, out var set)) d[k] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(reader.GetString(1));
            }
        }
        catch (SqliteException) { }
        return _defaultEquipPortTypes = d;
    }

    // 船の既定の装備 (ship_ports.equipped_item。小文字の basename のことがあるので大文字小文字無視) → それを使っている船の名前
    // (ships.name_en。同じ名前の船 (AI 用などの複製) は 1 つにまとめ、使っている行数の多い順、同数なら名前の順)。
    // ships に name_en 列が無い古い DB・表が無いときは空
    private Dictionary<string, List<string>>? _defaultEquipShips;

    private Dictionary<string, List<string>> DefaultEquipShips()
    {
        if (_defaultEquipShips != null) return _defaultEquipShips;
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (HasColumn("ships", "name_en"))
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    SELECT p.equipped_item, s.name_en, COUNT(*)
                    FROM ship_ports p JOIN ships s ON s.record_name = p.ship_record_name
                    WHERE p.equipped_item IS NOT NULL AND p.equipped_item <> '' AND s.name_en IS NOT NULL AND s.name_en <> ''
                    GROUP BY p.equipped_item, s.name_en
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var k = RecordBaseName(reader.GetString(0));
                    if (!counts.TryGetValue(k, out var byShip)) counts[k] = byShip = new Dictionary<string, int>(StringComparer.Ordinal);
                    var ship = reader.GetString(1);
                    byShip[ship] = (byShip.TryGetValue(ship, out var n) ? n : 0) + reader.GetInt32(2);
                }
            }
            catch (SqliteException) { }
        }
        return _defaultEquipShips = counts.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    // record (ship_ports.equipped_item の basename / record_name のどちらの形でもよい) の item_index.sub_type
    // (タレットの形 CanardTurret / BallTurret 等)。record は GetItemByRecord と同じ順 (RecordCandidates) に照合する。見つからなければ ""
    private readonly Dictionary<string, string> _subTypeByRecordCache = new(StringComparer.Ordinal);

    public string GetItemIndexSubType(string? record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return "";
        if (_subTypeByRecordCache.TryGetValue(key, out var cached)) return cached;
        var result = "";
        foreach (var cand in RecordCandidates(key))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT sub_type FROM item_index WHERE record_name = @rn COLLATE NOCASE AND sub_type IS NOT NULL AND sub_type <> '' LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", cand);
            if (cmd.ExecuteScalar() is string st && st.Length > 0) { result = st; break; }
        }
        _subTypeByRecordCache[key] = result;
        return result;
    }

    // record の item_index の種類 (AttachDef.Type) と形 (SubType)。record は RecordCandidates の順に照合する。見つからなければ ("", "")
    private readonly Dictionary<string, (string Type, string SubType)> _typeByRecordCache = new(StringComparer.Ordinal);

    public (string Type, string SubType) GetItemIndexType(string? record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return ("", "");
        if (_typeByRecordCache.TryGetValue(key, out var cached)) return cached;
        (string, string) result = ("", "");
        foreach (var cand in RecordCandidates(key))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT item_type, sub_type FROM item_index WHERE record_name = @rn COLLATE NOCASE AND item_type IS NOT NULL AND item_type <> '' LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", cand);
            using var reader = cmd.ExecuteReader();
            if (reader.Read()) { result = (reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1)); break; }
        }
        return _typeByRecordCache[key] = result;
    }

    // ポートの受け付ける種類 ("Type:SubType,SubType|Type:...") に、record の品目 (item_index の種類と形) が入るか。
    // ポートの種類に形の指定が無ければ種類だけで照合する。受け付ける種類が空、または品目の種類が分からないときは true (絞れない)
    public bool PortAccepts(string? portTypes, string? record)
    {
        if (string.IsNullOrWhiteSpace(portTypes)) return true;
        var (type, sub) = GetItemIndexType(record);
        if (type.Length == 0) return true;
        foreach (var t in portTypes.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = t.Split(':', 2);
            if (!parts[0].Trim().Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Length < 2 || parts[1].Trim().Length == 0) return true;
            if (parts[1].Split(',').Any(x => x.Trim().Equals(sub, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
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
        var sizeCols = _hasShipPortSizes ? "min_size, max_size, port_types" : "NULL, NULL, NULL";
        cmd.CommandText = $"SELECT port_name, item_type, size, equipped_item, {sizeCols} FROM ship_ports WHERE ship_record_name = @srn ORDER BY item_type, size DESC, port_name";
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
                MinSize = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                MaxSize = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                PortTypes = reader.IsDBNull(6) ? "" : reader.GetString(6),
            });
        }
        return ports;
    }

    // 品目 (タレット・ジンバル・船の武器) 自身の子ポートの定義 (item_ports)。record は GetItemByRecord と同じ順 (RecordCandidates) に照合する。
    // 表が無い古い DB・定義が無い品目は空
    private readonly Dictionary<string, List<ItemPortDef>> _itemPortDefsCache = new(StringComparer.Ordinal);

    public List<ItemPortDef> GetItemPortDefs(string? record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0 || !_hasItemPorts) return new List<ItemPortDef>();
        if (_itemPortDefsCache.TryGetValue(key, out var cached)) return cached;
        var list = new List<ItemPortDef>();
        foreach (var cand in RecordCandidates(key))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT port_name, min_size, max_size, port_types FROM item_ports WHERE record_name = @rn COLLATE NOCASE ORDER BY rowid";
            cmd.Parameters.AddWithValue("@rn", cand);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                list.Add(new ItemPortDef
                {
                    Name = reader.GetString(0),
                    MinSize = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    MaxSize = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    Types = reader.IsDBNull(3) ? "" : reader.GetString(3),
                });
            if (list.Count > 0) break;
        }
        return _itemPortDefsCache[key] = list;
    }

    // record がジンバル/マウントか (IsMountRecord: 既定の装備のポートの種別、無ければ record 名の規則)
    public bool IsGimbalRecord(string? record) => !string.IsNullOrWhiteSpace(record) && IsMountRecord(record);

    // 所持コンポーネントに入れられる品 (装備カテゴリ ComponentCategories のどれかの GetShipComponents の候補に入るもの) の索引。
    // record_name (大文字小文字は区別しない) → 候補 (手入力の追加の候補と同じ名前・種別・サイズ・グレード)。
    // PrepareOwnableIndexAsync が裏で作る (作られる前に FindOwnableComponent が呼ばれたら、その場で作る)
    private volatile Dictionary<string, EquipmentItem>? _ownableByRecord;
    private readonly object _ownableLock = new();
    private Task? _ownableTask;
    // FindOwnableComponent の結果の覚え (record → 候補 / null)。UI のスレッドだけで使う
    private readonly Dictionary<string, EquipmentItem?> _ownableResults = new(StringComparer.OrdinalIgnoreCase);

    // 入れられる品の索引ができているか
    public bool IsOwnableIndexReady => _ownableByRecord != null;

    // 入れられる品の索引を裏で一度だけ作る (この口の接続 _conn は使わず、読むだけの口を別に開いて作る)。作り終わると完了する
    public Task PrepareOwnableIndexAsync()
    {
        lock (_ownableLock)
        {
            if (_ownableByRecord != null) return Task.CompletedTask;
            return _ownableTask ??= Task.Run(() =>
            {
                using var reader = new EquipmentService(_gamedataDbPath, loadPriceCache: false);
                var dict = reader.BuildOwnableIndex();
                lock (_ownableLock) _ownableByRecord ??= dict;
            });
        }
    }

    private Dictionary<string, EquipmentItem> BuildOwnableIndex()
    {
        var dict = new Dictionary<string, EquipmentItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var (catKey, _, _) in ComponentCategories)
            foreach (var item in GetShipComponents(catKey))
                dict.TryAdd(item.RecordName, item);
        return dict;
    }

    // record (EntityClassDefinition. 付き) が item_index にあり、所持コンポーネントに入れられる種類なら、その候補 (手入力の候補と同じもの)。
    // どちらかに当たらなければ null。結果は覚えておく (gamedata_cache.db が替わるときはこの口ごと作り直される)
    public EquipmentItem? FindOwnableComponent(string record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return null;
        if (_ownableResults.TryGetValue(key, out var cached)) return cached;
        var index = _ownableByRecord;
        if (index == null)
        {
            var dict = BuildOwnableIndex();
            lock (_ownableLock) index = _ownableByRecord ??= dict;
        }
        EquipmentItem? result = null;
        if (index.TryGetValue(key, out var found))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM item_index WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", key);
            if (cmd.ExecuteScalar() != null) result = found;
        }
        _ownableResults[key] = result;
        return result;
    }

    // === 所持個人装備 (my_gear) に入れられる品 ===

    // 個人装備の種類 (item_index.item_type)。Key は追加パネルの種類の選択肢。Types は当たる item_type (Char_Clothing は前方一致)。
    // 武器の付属品 (WeaponAttachment) は個人用の sub_type (Magazine / IronSight / BottomAttachment / Utility) だけ
    // (Barrel / FiringMechanism / PowerArray / Ventilation は船舶武器の部品。2026-10-17 の gamedata_cache.db の item_index で確認)
    public static readonly IReadOnlyList<(string Key, string DisplayName, string[] Types)> GearCategories =
    [
        ("WeaponPersonal", "個人武器", ["WeaponPersonal"]),
        ("WeaponAttachment", "武器の付属品", ["WeaponAttachment"]),
        ("Char_Armor_Helmet", "ヘルメット", ["Char_Armor_Helmet"]),
        ("Char_Armor_Torso", "胴アーマー", ["Char_Armor_Torso"]),
        ("Char_Armor_Arms", "腕アーマー", ["Char_Armor_Arms"]),
        ("Char_Armor_Legs", "脚アーマー", ["Char_Armor_Legs"]),
        ("Char_Armor_Undersuit", "アンダースーツ", ["Char_Armor_Undersuit"]),
        ("Char_Armor_Backpack", "バックパック", ["Char_Armor_Backpack", "Char_Clothing_Backpack"]),
        ("Char_Clothing", "服", ["Char_Clothing_Torso_0", "Char_Clothing_Torso_1", "Char_Clothing_Torso_2", "Char_Clothing_Legs", "Char_Clothing_Feet", "Char_Clothing_Hands", "Char_Clothing_Hat"]),
        ("FPS_Consumable", "消耗品", ["FPS_Consumable"]),
        ("Gadget", "ガジェット", ["Gadget"]),
        ("Grenade", "手榴弾", ["Grenade"]),
    ];
    private static readonly HashSet<string> PersonalAttachmentSubTypes = new(StringComparer.OrdinalIgnoreCase) { "Magazine", "IronSight", "BottomAttachment", "Utility" };

    // item_type (と WeaponAttachment の sub_type) が所持個人装備に入れられる種類か
    public static bool IsGearType(string? itemType, string? subType)
    {
        var t = itemType ?? "";
        if (t.Equals("WeaponAttachment", StringComparison.OrdinalIgnoreCase)) return PersonalAttachmentSubTypes.Contains(subType ?? "");
        foreach (var (_, _, types) in GearCategories)
            foreach (var x in types)
                if (x.Equals(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // 所持個人装備の種別の欄: 種類の名前 (GearCategories の DisplayName)。武器の付属品は sub_type も添える (弾倉 / 照準器 / 下部 / ユーティリティ)
    public static string GearTypeDisplay(string? itemType, string? subType)
    {
        var t = itemType ?? "";
        string? name = null;
        foreach (var (_, display, types) in GearCategories)
            if (types.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) { name = display; break; }
        if (name == null) return t;
        if (t.Equals("WeaponAttachment", StringComparison.OrdinalIgnoreCase))
        {
            var sub = (subType ?? "") switch
            {
                "Magazine" => "弾倉", "IronSight" => "照準器", "BottomAttachment" => "下部", "Utility" => "ユーティリティ", var s => s,
            };
            if (sub.Length > 0) name += $" ({sub})";
        }
        return name;
    }

    // record (EntityClassDefinition. 付き) が item_index にあり、所持個人装備に入れられる種類 (IsGearType) なら、その品 (item_index の行)。
    // そうでなければ null。結果は覚えておく。表示名がプレースホルダ・空なら record 名 (QueryItemIndexByRecord)
    private readonly Dictionary<string, EquipmentItem?> _ownableGearResults = new(StringComparer.OrdinalIgnoreCase);

    public EquipmentItem? FindOwnableGear(string record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return null;
        if (_ownableGearResults.TryGetValue(key, out var cached)) return cached;
        EquipmentItem? result = null;
        try
        {
            var item = QueryItemIndexByRecord(key);
            if (item != null && IsGearType(item.ItemType, item.SubType)) result = item;
        }
        catch (SqliteException) { return null; }   // item_index を読めない (覚えず、次にもう一度引く)
        _ownableGearResults[key] = result;
        return result;
    }

    // 種類 (GearCategories の Key) と検索語 (名前 / メーカー / レコード名の一部) で item_index を引く (追加パネルの候補)。
    // 表示名がプレースホルダ・空のものは除く。並びは名前
    public List<EquipmentItem> GetGearItems(string categoryKey, string? search = null)
    {
        var cat = GearCategories.FirstOrDefault(c => c.Key == categoryKey);
        if (cat.Types == null || cat.Types.Length == 0) return new List<EquipmentItem>();
        using var cmd = _conn.CreateCommand();
        var typeParams = new List<string>();
        for (int i = 0; i < cat.Types.Length; i++)
        {
            typeParams.Add($"@t{i}");
            cmd.Parameters.AddWithValue($"@t{i}", cat.Types[i]);
        }
        var conditions = new List<string> { $"item_type IN ({string.Join(", ", typeParams)})" };
        if (categoryKey == "WeaponAttachment")
        {
            var subs = PersonalAttachmentSubTypes.Select((s, i) => { cmd.Parameters.AddWithValue($"@s{i}", s); return $"@s{i}"; }).ToList();
            conditions.Add($"sub_type IN ({string.Join(", ", subs)})");
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            conditions.Add("(name LIKE @q OR COALESCE(manufacturer, '') LIKE @q OR record_name LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{search}%");
        }
        var sizeExpr = _hasItemIndexSize ? "COALESCE(size, 0)" : "0";
        cmd.CommandText = $"SELECT record_name, name, item_type, sub_type, manufacturer, {sizeExpr} FROM item_index WHERE {string.Join(" AND ", conditions)} ORDER BY name, record_name";
        var items = new List<EquipmentItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            if (IsPlaceholderName(name)) continue;
            var rec = reader.GetString(0);
            var indexSize = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            items.Add(new EquipmentItem
            {
                RecordName = rec, Name = name,
                ItemType = reader.IsDBNull(2) ? "" : reader.GetString(2),
                SubType = reader.IsDBNull(3) ? "" : reader.GetString(3),
                Manufacturer = reader.IsDBNull(4) ? "" : reader.GetString(4),
                Size = indexSize > 0 ? indexSize : SizeFromRecordName(rec),
            });
        }
        return items;
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

    // 所持リスト (my_components) の表示名: record の今の item_index.name (追加の候補 GetShipComponents と同じく、
    // その record の空でない名前のうち最小のもの)。record の照合は GetItemByRecord と同じ候補 (そのまま → "EntityClassDefinition." 前置 →
    // "_SCItem" の有無を反転したもの) の順で、大文字小文字は区別しない。引けない・プレースホルダなら null。結果は覚えておく
    // (item_index が替わったときはこの口ごと作り直される: MainWindow.ReopenHangarEquip)
    private readonly Dictionary<string, string?> _currentItemNameCache = new(StringComparer.Ordinal);

    public string? GetCurrentItemName(string? record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return null;
        if (_currentItemNameCache.TryGetValue(key, out var cached)) return cached;

        string? result = null;
        try
        {
            foreach (var cand in RecordCandidates(key))
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT MIN(name) FROM item_index WHERE record_name = @rn COLLATE NOCASE AND name IS NOT NULL AND name != ''";
                cmd.Parameters.AddWithValue("@rn", cand);
                if (cmd.ExecuteScalar() is string name && name.Length > 0)
                {
                    result = IsPlaceholderName(name) ? null : name;
                    break;
                }
            }
        }
        catch (SqliteException) { return null; }   // item_index を読めない (覚えず、次にもう一度引く)
        _currentItemNameCache[key] = result;
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
            if (catInfo.Key != null)
            {
                // 装備カテゴリの種別で、UEX のカテゴリを確かめられていないもの (id が空) は検索しない (別のカテゴリで探して誤った結果を出さない)
                if (catInfo.UexCategoryIds.Length == 0) return null;
                categoryIds = catInfo.UexCategoryIds;
            }
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

    // 武器のダメージの種類で分けた名前 (装備タブの表の種別の欄)。［船舶戦闘］の「ダメージの種類」と同じ判定
    // (GameDataQueryService.WeaponDamageTypesOf) で、物理 → 実弾武器、エネルギー → エネルギー武器、ディストーション → ディストーション武器。
    // 種類が複数なら「・」でつなぐ (例 エネルギー・ディストーション武器)。ダメージが無い・読めないときは ""
    private readonly string _gamedataDbPath;
    private GameDataQueryService? _damageQuery;
    private bool _damageQueryFailed;
    private readonly Dictionary<string, string> _weaponKindCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly (string Key, string Label)[] WeaponDamageLabels =
        [("Physical", "実弾"), ("Energy", "エネルギー"), ("Distortion", "ディストーション"), ("Thermal", "熱"), ("Biochemical", "生化学"), ("Stun", "スタン")];

    public string WeaponDamageKindLabel(string? record)
    {
        var key = (record ?? "").Trim();
        if (key.Length == 0) return "";
        if (_weaponKindCache.TryGetValue(key, out var cached)) return cached;
        var label = "";
        try
        {
            if (_damageQuery == null && !_damageQueryFailed)
            {
                try { _damageQuery = new GameDataQueryService(_gamedataDbPath); }
                catch { _damageQueryFailed = true; }
            }
            if (_damageQuery != null)
            {
                var types = _damageQuery.WeaponDamageTypesOf(key);
                var parts = WeaponDamageLabels.Where(t => types.Contains(t.Key)).Select(t => t.Label).ToList();
                if (parts.Count > 0) label = string.Join("・", parts) + "武器";
            }
        }
        catch { label = ""; }
        _weaponKindCache[key] = label;
        return label;
    }

    // 所持コンポーネントの表の「性能」の欄 (種類ごとに 1 つの代表の値を「項目名 値」で。record の基本値)。
    //   タレット・ジンバル/マウント: 下に付けられる物のサイズと数 (item_ports。例 搭載 S3×2)。武器 (WeaponGun) を受け付けるポートはサイズだけ、
    //     それ以外 (ミサイルラック等) は受け付ける種類の最初の名前を前に付ける (例 搭載 S3×2 + ミサイルラック S4×1)
    //   それ以外: GameDataQueryService.ComponentPerfOf (出力・冷却・HP・速度・感度・Alpha・ミサイルラックの搭載数など)
    // 値が無い・読めないときは ""
    private readonly Dictionary<string, string> _perfCache = new(StringComparer.OrdinalIgnoreCase);

    public string ComponentPerfLabel(string? record, string? itemType)
    {
        var key = (record ?? "").Trim();
        var type = (itemType ?? "").Trim();
        if (key.Length == 0) return "";
        var cacheKey = type + "|" + key;
        if (_perfCache.TryGetValue(cacheKey, out var cached)) return cached;
        var label = "";
        try
        {
            if (type == TurretType || type == WeaponMountType)
            {
                var parts = GetItemPortDefs(key)
                    .Select(d =>
                    {
                        var names = d.TypeNames;
                        var isGun = names.Any(n => n.Equals(WeaponGunType, StringComparison.OrdinalIgnoreCase));
                        var prefix = isGun || names.Count == 0 ? "" : new ShipPortInfo { ItemType = names[0] }.TypeDisplay + " ";
                        return (Order: isGun ? 0 : 1, Text: prefix + GameDataQueryService.PortSizeText(d.MinSize, d.MaxSize), d.MaxSize);
                    })
                    .Where(x => x.MaxSize > 0)
                    .GroupBy(x => (x.Order, x.Text))
                    .OrderBy(g => g.Key.Order).ThenByDescending(g => g.First().MaxSize)
                    .Select(g => $"{g.Key.Text}×{g.Count()}")
                    .ToList();
                if (parts.Count > 0) label = "搭載 " + string.Join(" + ", parts);
            }
            else
            {
                if (_damageQuery == null && !_damageQueryFailed)
                {
                    try { _damageQuery = new GameDataQueryService(_gamedataDbPath); }
                    catch { _damageQueryFailed = true; }
                }
                if (_damageQuery != null) label = _damageQuery.ComponentPerfOf(key, type);
            }
        }
        catch { label = ""; }
        _perfCache[cacheKey] = label;
        return label;
    }

    public void Dispose()
    {
        _damageQuery?.Dispose();
        _damageQuery = null;
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

// 品目自身の子ポートの定義 1 件 (item_ports)。Types は受け付ける種類 "Type:SubType,SubType|Type:..."
public class ItemPortDef
{
    public string Name { get; set; } = "";
    public int MinSize { get; set; }
    public int MaxSize { get; set; }
    public string Types { get; set; } = "";
    // 受け付ける種類の名前 (Type の部分) の一覧
    public List<string> TypeNames => ParseTypeNames(Types);

    public static List<string> ParseTypeNames(string types) =>
        (types ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Split(':')[0].Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public class ShipPortInfo
{
    // ship_ports.port_name。子ポート行は "{親port}/{子port}" (例 hardpoint_weapon_left_nose/hardpoint_class_2)
    public string PortName { get; set; } = "";
    public string ItemType { get; set; } = "";
    public int Size { get; set; }
    public string EquippedItem { get; set; } = "";
    // 船の XML のハードポイントの本当の大きさと受け付ける種類 (ship_ports.min_size / max_size / port_types。最上位のポートだけ。
    // 列が無い古い DB・値が無いときは 0 / "")
    public int MinSize { get; set; }
    public int MaxSize { get; set; }
    public string PortTypes { get; set; } = "";
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
        // 付いている物の種類で拾ったポートの種類 (ゲームの global.ini の item_Type* の語。ToolArm・UtilityTurret・SalvageModifier は語が無いので英語のまま)
        "BombLauncher" => "爆弾ランチャー",
        "Bomb" => "爆弾",
        "TractorBeam" => "トラクタービーム",
        "EMP" => "EMP",
        "QuantumInterdictionGenerator" => "量子妨害ジェネレーター",
        "WeaponMining" => "採掘レーザーヘッド",
        "MiningModifier" => "採掘モジュール (MiningModifier)",
        "SalvageHead" => "サルベージビーム (SalvageHead)",
        _ => ItemType
    };
}
