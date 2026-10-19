using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［船舶管理］の［電力管理］(PowerPanel) の計算に使うゲームデータの読み出しと計算。
// 値はどれも gamedata_cache.db の items / ships / ammo_params / blueprints / blueprint_modifiers から、実行時に JSON を読んで作る (表は足さない)。
// どこから読むか:
//   部品の電力: items.raw_json → _RecordValue_.Components[] の ItemResourceComponentParams の Online の状態
//     (deltas[] の consumption / generation、minimumConsumptionFraction、powerRanges の low / medium / high (start・modifier・registerRange)、defaultPriority)
//   シールド: SCItemShieldGeneratorParams (MaxShieldHealth・MaxShieldRegen・DamagedRegenDelay・DownedRegenDelay)
//   武器: items.component_json の weaponRegenConsumerParams (maxAmmoLoad・maxRegenPerSec・regenerationCooldown)、
//     connectionParams.simplifiedHeatParams (熱) と fireActions の heatPerShot、raw_json の SAmmoContainerComponentParams.maxAmmoCount (弾倉)、
//     弾のダメージは ReadWeaponAmmo (ammo_params)、連射は ReadFireRates (既存の［比べる］と同じ読み方)
//   船の電力プール: ships.raw_json の SItemPortContainerComponentParams.resourceNetworkPowerPools.itemPools[]
//   品質の増減: blueprints.output_record と blueprint_modifiers / blueprint_materials (GameDataExtractor が作る)
public enum PowerUnitKind { None, Segment, Standard }

// powerRanges の 1 段 (Name は low / medium / high)
public sealed record PowerRange(string Name, int Start, double Modifier, bool Registered);

// 熱 (connectionParams.simplifiedHeatParams と、最初の射撃モードの heatPerShot)
public sealed record PowerHeatData(double HeatPerShot, double MinTemperature, double OverheatTemperature, double CoolingPerSecond,
    double TimeTillCoolingStarts, double OverheatFixTime, double TemperatureAfterOverheatFix);

// 武器 1 本の電力・DPS の計算に使う値
public sealed class PowerWeaponData
{
    public string Kind = "";               // "Energy" (weaponRegenConsumerParams あり) / "Ballistic" (弾倉あり) / "Beam" / "" (分からない)
    public string Mode = "";               // 使った射撃モードの名前 (fireActions の順で最初の、第1の弾を撃つモード)
    public double DamagePerShot;           // 弾 1 個のダメージ (ammo_params の直撃 + 爆発の合計)
    public int Pellets = 1;                // 1 回に出る弾の数
    public double RatePerMinute;           // 連射速度 (発/分)
    public double MaxAmmoLoad;             // エネルギー武器の弾数 (weaponRegenConsumerParams.maxAmmoLoad)
    public double MaxRegenPerSec;          // 同 回復 (maxRegenPerSec。弾/秒)
    public double RegenCooldown;           // 同 撃った後に回復が始まるまでの待ち (regenerationCooldown 秒)
    public int MagazineCount;              // バリスティックの弾倉 (SAmmoContainerComponentParams.maxAmmoCount)
    public PowerHeatData? Heat;            // 熱 (simplifiedHeatParams が無ければ null)
    public double BeamDps;                 // ビームの毎秒のダメージ (最も大きいモード)
}

// 部品 1 つの電力のデータ
public sealed class PowerItemData
{
    public string Record = "";
    public string ItemType = "";
    public PowerUnitKind ConsumeUnit;      // 電力の消費の単位 (SPowerSegmentResourceUnit = ピップ、SStandardResourceUnit = 小数)
    public double ConsumeAmount;           // 電力の消費 (Online の consumption (Power) の量)
    public double MinFraction;             // minimumConsumptionFraction
    public List<PowerRange> Ranges = new();
    public double PowerGeneration;         // 電力の出力 (パワープラント。generation (Power) の units)
    public Dictionary<string, double> Generation = new(StringComparer.OrdinalIgnoreCase);   // 電力以外に作るもの (Coolant / Shield / LifeSupport など → 量)
    public int? Priority;                  // defaultPriority
    public double? ShieldHealth, ShieldRegen, DamagedRegenDelay, DownedRegenDelay;
    public PowerWeaponData? Weapon;
    public bool HasResourceComponent;      // ItemResourceComponentParams があるか

    // ピップの最大 (P)。ピップの部品でなければ 0
    public int MaxPips => ConsumeUnit == PowerUnitKind.Segment ? (int)Math.Round(ConsumeAmount) : 0;
}

// 船の電力プール (resourceNetworkPowerPools.itemPools[])
public sealed record PowerShipPools(bool Found, int? WeaponPoolSize, bool WeaponPoolDynamic, int? ShieldMaxItems);

// 品質の入力の枠 (blueprint_materials の枠。NameKey は訳のキー crafting_ui_slotname_…)
public sealed record QualitySlot(string DebugName, string? NameKey);

// 品質の増減の範囲 1 つ (blueprint_modifiers の 1 行)
public sealed record QualityRange(string Slot, string Property, int Index, string Kind, int StartQuality, int EndQuality, double ValueStart, double ValueEnd);

// できる品 1 つの BP の品質の枠と増減
public sealed class BlueprintQualityInfo
{
    public string BlueprintUuid = "";
    public List<QualitySlot> Slots = new();
    public List<QualityRange> Ranges = new();
    public bool HasModifiers => Ranges.Count > 0;
    // 枠ごとに、その枠が変える性質 (property) の一覧
    public IEnumerable<string> PropertiesOf(string slot) => Ranges.Where(r => r.Slot == slot).Select(r => r.Property).Distinct();
}

public partial class GameDataQueryService
{
    // 品質を入れない枠の品質 (CraftingGlobalParams.defaultCompositionQuality = 500。p4k の craftingglobalparams.json で確認。
    // 品質 500 は Linear で倍率 1.0、LinearIntegerAdditive で 0 の範囲)
    public const int DefaultCompositionQuality = 500;

    // 部品の電力のデータ。items に無い・JSON が読めなければ null。色違いで JSON を空にしてある品目は基本品目の JSON で補う (ComponentPerfOf と同じ)
    public PowerItemData? GetPowerItem(string recordName)
    {
        const string prefix = "EntityClassDefinition.";
        var rn = (recordName ?? "").Trim();
        if (rn.Length == 0) return null;
        if (!rn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rn = prefix + rn;
        (string Component, string Raw, string Record, string ItemType)? Read(string record)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(component_json, ''), COALESCE(raw_json, ''), record_name, COALESCE(item_type, '') FROM items WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
                cmd.Parameters.AddWithValue("@rn", record);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { throw; }   // 使用中は呼ぶ側 (PowerPanel) が間を置いて読み直す
            catch { return null; }
        }
        var row = Read(rn);
        if (row == null) return null;
        var (component, raw, record, itemType) = row.Value;
        if (component.Length == 0 || raw.Length == 0)
        {
            var (baseRecord, _) = VariantRow(record);
            if (baseRecord != null && Read(baseRecord) is { } b)
            {
                if (component.Length == 0) component = b.Component;
                if (raw.Length == 0) raw = b.Raw;
            }
        }
        var data = new PowerItemData { Record = record, ItemType = itemType };
        if (raw.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("Components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in cs.EnumerateArray())
                    {
                        if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t)) continue;
                        switch (t.GetString())
                        {
                            case "ItemResourceComponentParams":
                                ReadResourceComponent(c, data);
                                break;
                            case "SCItemShieldGeneratorParams":
                                if (TryNum(c, "MaxShieldHealth", out var hp)) data.ShieldHealth = hp;
                                if (TryNum(c, "MaxShieldRegen", out var rg)) data.ShieldRegen = rg;
                                if (TryNum(c, "DamagedRegenDelay", out var dd)) data.DamagedRegenDelay = dd;
                                if (TryNum(c, "DownedRegenDelay", out var dn)) data.DownedRegenDelay = dn;
                                break;
                        }
                    }
                }
            }
            catch (JsonException) { }
        }
        if (itemType.Equals("WeaponGun", StringComparison.OrdinalIgnoreCase)) data.Weapon = ReadPowerWeapon(component, raw);
        return data;
    }

    // ItemResourceComponentParams の Online の状態を読む (状態の名前が Online のもの。無ければ最初の状態)
    private static void ReadResourceComponent(JsonElement comp, PowerItemData data)
    {
        data.HasResourceComponent = true;
        if (TryNum(comp, "defaultPriority", out var pr)) data.Priority = (int)pr;
        if (!comp.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array) return;
        JsonElement? online = null;
        foreach (var st in states.EnumerateArray())
        {
            if (st.ValueKind != JsonValueKind.Object) continue;
            if (st.TryGetProperty("name", out var n) && n.GetString() == "Online") { online = st; break; }
            online ??= st;
        }
        if (online is not { } s) return;
        if (s.TryGetProperty("deltas", out var deltas) && deltas.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in deltas.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                if (d.TryGetProperty("consumption", out var con) && con.ValueKind == JsonValueKind.Object
                    && con.TryGetProperty("resource", out var cr) && cr.GetString() == "Power"
                    && ReadAmount(con) is { } ca)
                {
                    data.ConsumeUnit = ca.Unit;
                    data.ConsumeAmount = ca.Amount;
                    if (TryNum(d, "minimumConsumptionFraction", out var mf)) data.MinFraction = mf;
                }
                if (d.TryGetProperty("generation", out var gen) && gen.ValueKind == JsonValueKind.Object
                    && gen.TryGetProperty("resource", out var gr) && gr.GetString() is { Length: > 0 } res
                    && ReadAmount(gen) is { } ga)
                {
                    if (res == "Power") data.PowerGeneration += ga.Amount;
                    else data.Generation[res] = (data.Generation.TryGetValue(res, out var prev) ? prev : 0) + ga.Amount;
                }
            }
        }
        if (s.TryGetProperty("powerRanges", out var ranges) && ranges.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "low", "medium", "high" })
            {
                if (!ranges.TryGetProperty(name, out var r) || r.ValueKind != JsonValueKind.Object) continue;
                TryNum(r, "start", out var start);
                var mod = TryNum(r, "modifier", out var m) ? m : 1;
                var reg = r.TryGetProperty("registerRange", out var rr) && rr.ValueKind == JsonValueKind.True;
                data.Ranges.Add(new PowerRange(name, (int)Math.Round(start), mod, reg));
            }
        }
    }

    // resourceAmountPerSecond の量と単位 (units = SPowerSegmentResourceUnit、standardResourceUnits = SStandardResourceUnit)
    private static (double Amount, PowerUnitKind Unit)? ReadAmount(JsonElement consumptionOrGeneration)
    {
        if (!consumptionOrGeneration.TryGetProperty("resourceAmountPerSecond", out var amt) || amt.ValueKind != JsonValueKind.Object) return null;
        if (TryNum(amt, "units", out var u)) return (u, PowerUnitKind.Segment);
        if (TryNum(amt, "standardResourceUnits", out var s)) return (s, PowerUnitKind.Standard);
        return null;
    }

    // 武器の DPS の計算に使う値 (弾・連射は［比べる］と同じ読み方。射撃モードは fireActions の順で最初の、第1の弾を撃つモード)
    private PowerWeaponData ReadPowerWeapon(string component, string raw)
    {
        var w = new PowerWeaponData();
        var rates = ReadFireRates(component);
        var mode = rates.FirstOrDefault(x => !x.Secondary) ?? rates.FirstOrDefault();
        if (mode != null)
        {
            w.Mode = mode.Mode;
            w.RatePerMinute = mode.Rate;
            w.Pellets = Math.Max(1, mode.Pellets);
        }
        var ammo = ReadWeaponAmmo(component, raw);
        if (ammo != null) w.DamagePerShot = ammo.Total;
        var beams = ReadBeamDamage(component);
        if (beams.Count > 0) w.BeamDps = beams.Max(b => b.Total);
        if (raw.Length > 0) w.MagazineCount = ReadAmmoContainer(raw).MaxAmmo;
        if (component.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(component);
                var root = doc.RootElement;
                if (root.TryGetProperty("weaponRegenConsumerParams", out var reg) && reg.ValueKind == JsonValueKind.Object)
                {
                    TryNum(reg, "maxAmmoLoad", out w.MaxAmmoLoad);
                    TryNum(reg, "maxRegenPerSec", out w.MaxRegenPerSec);
                    TryNum(reg, "regenerationCooldown", out w.RegenCooldown);
                }
                if (root.TryGetProperty("connectionParams", out var cp) && cp.ValueKind == JsonValueKind.Object
                    && cp.TryGetProperty("simplifiedHeatParams", out var hp) && hp.ValueKind == JsonValueKind.Object)
                {
                    double heatPerShot = 0;
                    // heatPerShot は射撃の動作 (fireActions) にある。使うモードと同じく最初の動作から探す
                    if (root.TryGetProperty("fireActions", out var fas) && fas.ValueKind == JsonValueKind.Array)
                        foreach (var fa in fas.EnumerateArray())
                            if (FindHeatPerShot(fa, 0) is { } h) { heatPerShot = h; break; }
                    TryNum(hp, "minTemperature", out var minT);
                    TryNum(hp, "overheatTemperature", out var overT);
                    TryNum(hp, "coolingPerSecond", out var cool);
                    TryNum(hp, "timeTillCoolingStarts", out var tcool);
                    TryNum(hp, "overheatFixTime", out var fix);
                    TryNum(hp, "temperatureAfterOverheatFix", out var after);
                    w.Heat = new PowerHeatData(heatPerShot, minT, overT, cool, tcool, fix, after);
                }
            }
            catch (JsonException) { }
        }
        if (w.MaxAmmoLoad > 0 && w.MaxRegenPerSec > 0) w.Kind = "Energy";
        else if (w.MagazineCount > 0) w.Kind = "Ballistic";
        else if (w.BeamDps > 0) w.Kind = "Beam";
        return w;
    }

    // 動作の中の heatPerShot (weaponAction / defaultWeaponAction / sequenceEntries / weaponActions をたどる)
    private static double? FindHeatPerShot(JsonElement el, int depth)
    {
        if (depth > 6) return null;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (TryNum(el, "heatPerShot", out var h)) return h;
            foreach (var key in new[] { "weaponAction", "defaultWeaponAction", "sequenceEntries", "weaponActions" })
                if (el.TryGetProperty(key, out var child) && FindHeatPerShot(child, depth + 1) is { } ch) return ch;
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var c in el.EnumerateArray())
                if (FindHeatPerShot(c, depth + 1) is { } ch) return ch;
        return null;
    }

    // 船の電力プール。船が無い・読めなければ Found = false
    public PowerShipPools GetShipPowerPools(string shipRecordName)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(raw_json, '') FROM ships WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", shipRecordName);
            var raw = cmd.ExecuteScalar() as string ?? "";
            if (raw.Length == 0) return new PowerShipPools(false, null, false, null);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array)
                return new PowerShipPools(false, null, false, null);
            int? weaponPool = null, shieldMax = null;
            bool weaponDynamic = false, found = false;
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "SItemPortContainerComponentParams") continue;
                if (!c.TryGetProperty("resourceNetworkPowerPools", out var pools) || pools.ValueKind != JsonValueKind.Object
                    || !pools.TryGetProperty("itemPools", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                found = true;
                foreach (var p in items.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var type = p.TryGetProperty("_Type_", out var pt) ? pt.GetString() : null;
                    var itemType = p.TryGetProperty("itemType", out var it) ? it.GetString() : null;
                    if (itemType == "WeaponGun")
                    {
                        if (type == "FixedPowerPool" && TryNum(p, "poolSize", out var ps)) weaponPool = (int)ps;
                        else if (type == "DynamicPowerPool") weaponDynamic = true;
                    }
                    else if (itemType == "Shield" && type == "DynamicPowerPool" && TryNum(p, "maxItemCount", out var mx) && mx >= 0)
                        shieldMax = (int)mx;
                }
            }
            return new PowerShipPools(found, weaponPool, weaponDynamic, shieldMax);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { throw; }   // 使用中は呼ぶ側 (PowerPanel) が間を置いて読み直す
        catch (Exception ex) when (ex is JsonException or SqliteException) { return new PowerShipPools(false, null, false, null); }
    }

    // できる品 (outputRecord) の BP の品質の枠と増減。BP が無い・表が無ければ null。
    // 同じできる品の BP が 2 つ以上あるときは record_name の順で最初のもの (今のデータでは 1 件 (KLWE_LaserRepeater_S3) だけで、増減は同じ)
    public BlueprintQualityInfo? GetBlueprintQuality(string outputRecord) => ReadBlueprintQuality(_conn, outputRecord);

    // 同じものを DB のファイルから読むだけの接続で読む (［装備］の所持コンポーネントの品質の入力から使う)。ファイルが無い・読めなければ null
    public static BlueprintQualityInfo? ReadBlueprintQuality(string gamedataDbPath, string outputRecord)
    {
        if (!File.Exists(gamedataDbPath)) return null;
        try
        {
            using var conn = new TrackedSqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            return ReadBlueprintQuality(conn, outputRecord);
        }
        catch (SqliteException) { return null; }
    }

    // 品質の増減のあるできる品の record 名 (大文字小文字を区別しない)。表が無い・読めなければ空
    public static HashSet<string> ReadOutputsWithQualityModifiers(string gamedataDbPath)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(gamedataDbPath)) return set;
        try
        {
            using var conn = new TrackedSqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT b.output_record FROM blueprints b JOIN blueprint_modifiers m ON m.bp_uuid = b.uuid WHERE b.output_record IS NOT NULL";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) set.Add(r.GetString(0));
        }
        catch (SqliteException ex) when (!IsBusy(ex)) { }   // 使用中は投げる (呼ぶのは所持コンポーネントの表の裏の読み込みだけ。待って読み直す)
        return set;
    }

    private static BlueprintQualityInfo? ReadBlueprintQuality(SqliteConnection conn, string outputRecord)
    {
        var _conn = conn;
        try
        {
            string? uuid;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT uuid FROM blueprints WHERE output_record = @o COLLATE NOCASE ORDER BY record_name LIMIT 1";
                cmd.Parameters.AddWithValue("@o", outputRecord.StartsWith("EntityClassDefinition.", StringComparison.OrdinalIgnoreCase) ? outputRecord : "EntityClassDefinition." + outputRecord);
                uuid = cmd.ExecuteScalar() as string;
            }
            if (string.IsNullOrEmpty(uuid)) return null;
            var info = new BlueprintQualityInfo { BlueprintUuid = uuid };
            using (var cmd = _conn.CreateCommand())
            {
                // 枠の並びは BP の材料の並び (rowid の順)。同じ枠に材料が 2 つ以上あっても 1 つ
                cmd.CommandText = "SELECT slot_debug_name, MIN(slot_name_key), MIN(rowid) AS o FROM blueprint_materials WHERE bp_uuid = @u GROUP BY slot_debug_name ORDER BY o";
                cmd.Parameters.AddWithValue("@u", uuid);
                using var r = cmd.ExecuteReader();
                while (r.Read()) info.Slots.Add(new QualitySlot(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT slot_debug_name, property, range_index, kind, start_quality, end_quality, value_start, value_end FROM blueprint_modifiers WHERE bp_uuid = @u ORDER BY slot_debug_name, property, range_index";
                cmd.Parameters.AddWithValue("@u", uuid);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    info.Ranges.Add(new QualityRange(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetDouble(6), r.GetDouble(7)));
            }
            // 材料の表に無い枠の増減があれば (今のデータには無い)、その枠も入力に出す
            foreach (var slot in info.Ranges.Select(x => x.Slot).Distinct())
                if (info.Slots.All(s => s.DebugName != slot)) info.Slots.Add(new QualitySlot(slot, null));
            return info;
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return null; }
    }
}

// 電力管理の計算 (式は設計書 power_design.md の 2 と、利用者の決定 2026-10-19 のとおり)
public static class PowerMath
{
    // 品質の性質 (blueprint_modifiers.property) のうち、電力管理の計算に使うもの (名前から当てたもの。推定)
    public const string PropPowerGeneration = "gpp_itemresource_powergeneration";   // パワープラントの出力のピップに足す (LinearIntegerAdditive)
    public const string PropCoolantGeneration = "gpp_itemresource_coolantgeneration"; // クーラーの冷却剤生成量に掛ける
    public const string PropShieldMaxHealth = "gpp_shield_maxhealth";               // シールドの容量 (MaxShieldHealth) に掛ける
    public const string PropWeaponDamage = "gpp_weapon_damage";                     // 武器の 1 発のダメージに掛ける

    // 動かすのに要る最低のピップ (pmin = round(P × minimumConsumptionFraction)。1 未満なら 1)。ピップの部品でなければ 0
    public static int MinPips(PowerItemData d)
    {
        var p = d.MaxPips;
        if (p <= 0) return 0;
        return Math.Clamp((int)Math.Round(p * d.MinFraction, MidpointRounding.AwayFromZero), 1, p);
    }

    // 割り当てたピップ p の段階: 登録済み (registerRange) の段のうち start ≤ p で start がいちばん大きいもの。
    // 登録済みの段が無い部品 (パワープラント・武器など) は null (倍率 1.0)。登録済みの段があるのに start ≤ p の段が無いとき (今のデータには無い) は
    // start のいちばん小さい登録済みの段
    public static PowerRange? StageAt(PowerItemData d, int pips)
    {
        var regs = d.Ranges.Where(r => r.Registered).ToList();
        if (regs.Count == 0) return null;
        return regs.Where(r => r.Start <= pips).OrderByDescending(r => r.Start).FirstOrDefault() ?? regs.OrderBy(r => r.Start).First();
    }

    public static double StageModifier(PowerItemData d, int pips) => pips <= 0 ? 0 : StageAt(d, pips)?.Modifier ?? 1.0;

    // 品質 q の範囲 (start ≤ q ≤ end。無ければ start ≤ q の最後の範囲) の値。Linear は範囲の中を直線で結ぶ (推定)。
    // LinearIntegerAdditive も直線で結んで整数に丸める (今のデータは全部範囲の中で一定 (start = end) なので、範囲の始めの値と同じになる)
    public static double RangeValue(IEnumerable<QualityRange> ranges, int q)
    {
        var list = ranges.OrderBy(r => r.StartQuality).ToList();
        var hit = list.FirstOrDefault(r => r.StartQuality <= q && q <= r.EndQuality) ?? list.LastOrDefault(r => r.StartQuality <= q) ?? list.FirstOrDefault();
        if (hit == null) return double.NaN;
        var v = hit.EndQuality == hit.StartQuality ? hit.ValueStart
            : hit.ValueStart + (double)(Math.Clamp(q, hit.StartQuality, hit.EndQuality) - hit.StartQuality) / (hit.EndQuality - hit.StartQuality) * (hit.ValueEnd - hit.ValueStart);
        return hit.Kind == "LinearIntegerAdditive" ? Math.Round(v, MidpointRounding.AwayFromZero) : v;
    }

    // 品質の増減 (property ごと)。Linear は枠ごとの倍率の掛け算、LinearIntegerAdditive は足し算 (重ね方は推定。利用者の決定 5)。
    // qualities は 枠 (slot_debug_name) → 品質。null なら増減なし (自作でない・品質の入力なし)。入っていない枠は DefaultCompositionQuality (500)
    public static (double Multiplier, double Additive, bool Applied) QualityEffect(BlueprintQualityInfo? info, IReadOnlyDictionary<string, int>? qualities, string property)
    {
        if (info == null || qualities == null || qualities.Count == 0) return (1, 0, false);
        double mul = 1, add = 0;
        bool applied = false;
        foreach (var g in info.Ranges.Where(r => r.Property.Equals(property, StringComparison.OrdinalIgnoreCase)).GroupBy(r => r.Slot))
        {
            var q = qualities.TryGetValue(g.Key, out var v) ? v : GameDataQueryService.DefaultCompositionQuality;
            var val = RangeValue(g, q);
            if (double.IsNaN(val)) continue;
            applied = true;
            if (g.First().Kind == "LinearIntegerAdditive") add += val;
            else mul *= val;
        }
        return (mul, add, applied);
    }

    // quality_json ({"枠": 品質, ...}) → 辞書。'' や読めない値は null (品質なし)
    public static Dictionary<string, int>? ParseQualityJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v)) d[p.Name] = (int)Math.Round(Math.Clamp(v, 0, 1000));
            return d.Count > 0 ? d : null;
        }
        catch (JsonException) { return null; }
    }

    // 辞書 → quality_json (キーの並び・書き方は HangarService.TryNormalizeQualityJson が揃える)
    public static string ToQualityJson(IReadOnlyDictionary<string, int> qualities) =>
        qualities.Count == 0 ? "" : JsonSerializer.Serialize(qualities.OrderBy(k => k.Key, StringComparer.Ordinal).ToDictionary(k => k.Key, k => k.Value));

    // 品質の短い表示 (全部の枠が同じなら「Q950」、違えば「Q 枠ごと」。品質なしは "")
    public static string QualityLabel(IReadOnlyDictionary<string, int>? qualities)
    {
        if (qualities == null || qualities.Count == 0) return "";
        var values = qualities.Values.Distinct().ToList();
        return values.Count == 1 ? $"Q{values[0]}" : "Q 枠ごと";
    }

    // 武器 1 本の DPS。k は武器のプールの割り当ての割合 (割り当て ÷ プールの大きさ。プールの大きさが無ければ 1)、damageMult は品質の威力の倍率。
    //   電力が 0 (k ≤ 0) なら撃てない (connectionParams.noPowerStats の fireRateMultiplier・damageMultiplier が 0)
    //   エネルギー: 弾数 N = maxAmmoLoad × k、回復 R = maxRegenPerSec × k (どちらも比例。推定。利用者の決定 2)、
    //     1 周 T = N ÷ 連射 + regenerationCooldown + N ÷ R、サステイン = N × 1 回のダメージ ÷ T
    //   バリスティック: 電力が入っていれば動く (最小の割合 1.0。推定)。割合 k が 1 より小さくても 1 として計算する (電力の割合で変わるかはデータに無い。
    //     PowerAssumedFull を立て、バースト・サステイン・撃ち切るまでを推定にする)。熱 (simplifiedHeatParams) があれば、
    //     1 発ごとの熱 = heatPerShot − (撃つ間隔が timeTillCoolingStarts より長ければ、その差 × coolingPerSecond)、
    //     過熱までの発数 n = ⌈(overheatTemperature − temperatureAfterOverheatFix) ÷ 1 発ごとの熱⌉、
    //     サステイン = n × 1 回のダメージ ÷ (n ÷ 連射 + overheatFixTime) (推定。利用者の決定 3)。熱が無い・溜まらなければバーストと同じ
    //   ビーム: バーストだけ (サステインはデータなし)。バリスティックと同じく k が 1 より小さくても 1 として計算する (PowerAssumedFull)
    public static PowerWeaponResult WeaponDps(PowerWeaponData w, double k, double damageMult)
    {
        var res = new PowerWeaponResult { Kind = w.Kind };
        var shotsPerSec = w.RatePerMinute / 60;
        var perShot = w.DamagePerShot * w.Pellets * damageMult;
        if (k <= 0) { res.Unpowered = true; return res; }
        // バリスティック・ビームは割合を掛けない (k < 1 でも 1 として計算する。推定)
        res.PowerAssumedFull = k < 1 && w.Kind is "Ballistic" or "Beam";
        switch (w.Kind)
        {
            case "Energy":
            {
                if (shotsPerSec <= 0 || perShot <= 0) break;
                res.Burst = perShot * shotsPerSec;
                var n = w.MaxAmmoLoad * k;
                var regen = w.MaxRegenPerSec * k;
                var t = n / shotsPerSec + w.RegenCooldown + n / regen;
                res.Ammo = n;
                res.RegenPerSec = regen;
                res.Wait = w.RegenCooldown;
                res.CycleSeconds = t;
                res.Sustain = t > 0 ? n * perShot / t : 0;
                res.EmptySeconds = n / shotsPerSec;
                res.SustainEstimated = k < 1;   // 比例の部分は推定
                res.PowerScaled = k < 1;        // 弾薬容量・武器再生・1 周・撃ち切るまでも比例で減らした (推定)
                break;
            }
            case "Ballistic":
            {
                if (shotsPerSec <= 0 || perShot <= 0) break;
                res.Burst = perShot * shotsPerSec;
                res.Ammo = w.MagazineCount;
                res.Sustain = res.Burst;
                var interval = 1 / shotsPerSec;
                double emptySeconds = w.MagazineCount / shotsPerSec;
                if (w.Heat is { } h && h.HeatPerShot > 0 && h.OverheatTemperature > h.TemperatureAfterOverheatFix)
                {
                    var net = h.HeatPerShot - h.CoolingPerSecond * Math.Max(0, interval - h.TimeTillCoolingStarts);
                    if (net > 0)
                    {
                        var n = Math.Ceiling((h.OverheatTemperature - h.TemperatureAfterOverheatFix) / net);
                        var t = n / shotsPerSec + h.OverheatFixTime;
                        res.Sustain = n * perShot / t;
                        res.CycleSeconds = t;
                        res.ShotsToOverheat = n;
                        res.Wait = h.OverheatFixTime;
                        // 最初の 1 周は minTemperature から (今のデータでは temperatureAfterOverheatFix と同じ)。撃ち切るまでの過熱の回数は推定
                        var nFirst = Math.Ceiling((h.OverheatTemperature - h.MinTemperature) / net);
                        var overheats = w.MagazineCount > nFirst ? 1 + Math.Floor((w.MagazineCount - nFirst - 1) / n) : 0;
                        emptySeconds += Math.Max(0, overheats) * h.OverheatFixTime;
                        res.SustainEstimated = true;
                    }
                }
                res.EmptySeconds = emptySeconds;
                res.TotalDamage = w.MagazineCount * perShot;
                break;
            }
            case "Beam":
                res.Burst = w.BeamDps * damageMult;
                break;
        }
        return res;
    }
}

public sealed class PowerWeaponResult
{
    public string Kind = "";
    public bool Unpowered;
    public double Burst;
    public double? Sustain;
    public bool SustainEstimated;
    public bool PowerScaled;          // エネルギー武器の弾薬容量・武器再生を、武器のプールの割合で比例で減らした (割合が 100% より少ないとき。推定)
    public bool PowerAssumedFull;     // バリスティック・ビームを、武器のプールの割合が 100% より少ないのに 100% として計算した (推定)
    public double Ammo;
    public double RegenPerSec;
    public double Wait;
    public double CycleSeconds;
    public double ShotsToOverheat;
    public double EmptySeconds;
    public double TotalDamage;
}

// 電力管理の部品 1 行の入力 (画面の行が持つ)
public sealed class PowerSlotState
{
    public string PortKey = "";
    public PowerItemData? Item;
    public BlueprintQualityInfo? Quality;
    public Dictionary<string, int>? Qualities;   // 枠 → 品質 (null = 品質なし)
    public int Pips;                             // ピップの部品の割り当て (0 = オフ)
    public bool On = true;                       // ピップでない部品 (量子ドライブ) の入・切

    public bool IsPowerPlant => Item != null && Item.PowerGeneration > 0 && Item.ItemType.Equals("PowerPlant", StringComparison.OrdinalIgnoreCase);
    public bool IsWeapon => Item?.Weapon != null;
    public bool IsSegmentConsumer => Item != null && !IsPowerPlant && !IsWeapon && Item.ConsumeUnit == PowerUnitKind.Segment && Item.MaxPips > 0;
    public bool IsShield => Item != null && Item.ItemType.Equals("Shield", StringComparison.OrdinalIgnoreCase);
}

// 電力管理の部品 1 行の結果
// 推定の印: どの値に推定が入ったかを値ごとに持つ (推定の入らない値には付けない)
//   段階の倍率 (powerRanges の modifier ≠ 1) → Value (冷却剤・シールド再生量・生命維持)
//   品質の増減 (BP の増減で値が実際に変わったときだけ。QualityApplied) → 掛かった値 (出力・冷却剤・最大シールド強度・武器の 1 発のダメージ)
//   武器のサステイン → PowerWeaponResult.SustainEstimated (比例・熱) か品質
//   バリスティック・ビームを割合 100% として計算した (PowerWeaponResult.PowerAssumedFull) → バースト・サステイン
public sealed class PowerSlotResult
{
    public PowerRange? Stage;
    public double Modifier = 1;
    public bool NoPower;              // 電力を受けられない (シールドの数の上限を超えた。推定)
    public bool Off;                  // 割り当て 0 (オフ)
    public double QualityMultiplier = 1;
    public double QualityAdditive;
    public bool QualityApplied;       // 品質の増減で値が実際に変わった (倍率 ≠ 1 または足す数 ≠ 0)
    public double Value;              // 主な結果 (冷却剤 /秒・シールドの回復 /秒・出力のピップ・生命維持)
    public double ShieldCapacity;
    public PowerWeaponResult? Weapon;
    public bool Estimated;            // Value に推定が入った (武器はバーストかサステインのどちらか)
    public bool CapacityEstimated;    // ShieldCapacity に推定が入った (品質)
    public bool BurstEstimated;       // 武器のバーストに推定が入った (品質)
    public bool SustainEstimated;     // 武器のサステインに推定が入った (比例・熱・品質)
}

public sealed class PowerSummary
{
    public double Output;             // パワープラントの出力の合計 (ピップ)
    public int Allocated;             // 割り当て済み (ピップの部品 + 武器のプール)
    public double Remaining => Output - Allocated;
    public double CoolantSupply;
    public double ShieldCapacity;
    public double ShieldRegen;
    public double ShieldDamagedDelay;
    public double ShieldDownedDelay;
    public int PoweredShields;
    public double BurstTotal;
    public double SustainTotal;
    public bool OutputEstimated;          // 出力に品質の増減が入った
    public bool CoolantEstimated;         // 冷却剤生成量に段階の倍率・品質が入った
    public bool ShieldRegenEstimated;     // シールド再生量に段階の倍率が入った、またはシールドの数の上限で外したものがある
    public bool ShieldCapacityEstimated;  // 最大シールド強度に品質が入った、またはシールドの数の上限で外したものがある
    public bool BurstEstimated;
    public bool SustainEstimated;
    public double QuantumDrivePower;  // 量子ドライブの電力 (SStandardResourceUnit。単位が違うので割り当て済みに入れない)
    public double WeaponFraction;     // 武器のプールの割合 k
    public Dictionary<PowerSlotState, PowerSlotResult> Results = new();
}

public static partial class PowerMathShip
{
    // 船 1 隻分の計算。weaponPips は武器のプールの割り当て (0〜poolSize)。
    // 武器のプールのピップはパワープラントの出力から引く (利用者の決定 4。ゲームの画面での確かめは未確認)。
    // プールの大きさ (FixedPowerPool WeaponGun の poolSize) が無い船は、武器を k = 1 で計算し、割り当て済みには入れない。
    // シールドは DynamicPowerPool Shield の maxItemCount 基まで電力を受けられる (行の順で、割り当てが 1 以上のもの。推定)
    public static PowerSummary Compute(IReadOnlyList<PowerSlotState> slots, PowerShipPools pools, int weaponPips)
    {
        var sum = new PowerSummary();
        var poolSize = pools.WeaponPoolSize ?? 0;
        var k = poolSize > 0 ? Math.Clamp((double)weaponPips / poolSize, 0, 1) : 1;
        sum.WeaponFraction = k;
        if (poolSize > 0) sum.Allocated += Math.Clamp(weaponPips, 0, poolSize);
        int shieldsPowered = 0;
        foreach (var s in slots)
        {
            var r = new PowerSlotResult();
            sum.Results[s] = r;
            var d = s.Item;
            if (d == null) continue;
            if (s.IsPowerPlant)
            {
                var q = PowerMath.QualityEffect(s.Quality, s.Qualities, PowerMath.PropPowerGeneration);
                r.QualityAdditive = q.Additive;
                r.QualityApplied = q.Applied && q.Additive != 0;   // BP の増減が出力を実際に変えたときだけ推定
                r.Value = d.PowerGeneration + q.Additive;
                r.Estimated = r.QualityApplied;
                if (r.Estimated) sum.OutputEstimated = true;
                sum.Output += r.Value;
                continue;
            }
            if (s.IsWeapon)
            {
                var q = PowerMath.QualityEffect(s.Quality, s.Qualities, PowerMath.PropWeaponDamage);
                r.QualityMultiplier = q.Multiplier;
                r.QualityApplied = q.Applied && q.Multiplier != 1;
                r.Weapon = PowerMath.WeaponDps(d.Weapon!, k, q.Multiplier);
                r.Off = r.Weapon.Unpowered;
                r.BurstEstimated = r.Weapon.Burst > 0 && (r.QualityApplied || r.Weapon.PowerAssumedFull);
                r.SustainEstimated = r.Weapon.Sustain != null && (r.QualityApplied || r.Weapon.SustainEstimated || r.Weapon.PowerAssumedFull);
                r.Estimated = r.BurstEstimated || r.SustainEstimated;
                sum.BurstTotal += r.Weapon.Burst;
                if (r.Weapon.Sustain is { } sus) sum.SustainTotal += sus;
                if (r.BurstEstimated) sum.BurstEstimated = true;
                if (r.SustainEstimated) sum.SustainEstimated = true;
                continue;
            }
            if (s.IsSegmentConsumer)
            {
                var pips = Math.Clamp(s.Pips, 0, d.MaxPips);
                if (pips > 0 && pips < PowerMath.MinPips(d)) pips = 0;   // 最低のピップ未満は動かない (推定)
                if (pips == 0) { r.Off = true; r.Modifier = 0; continue; }
                if (s.IsShield && pools.ShieldMaxItems is { } max && shieldsPowered >= max)
                {
                    // シールドの数の上限は推定なので、外したシールドがあればシールドの合計は推定
                    r.NoPower = true; r.Modifier = 0;
                    sum.ShieldRegenEstimated = sum.ShieldCapacityEstimated = true;
                    continue;
                }
                sum.Allocated += pips;
                r.Stage = PowerMath.StageAt(d, pips);
                r.Modifier = PowerMath.StageModifier(d, pips);
                r.Estimated = r.Stage != null && r.Modifier != 1;
                if (d.ItemType.Equals("Cooler", StringComparison.OrdinalIgnoreCase) && d.Generation.TryGetValue("Coolant", out var cool))
                {
                    var q = PowerMath.QualityEffect(s.Quality, s.Qualities, PowerMath.PropCoolantGeneration);
                    r.QualityMultiplier = q.Multiplier;
                    r.QualityApplied = q.Applied && q.Multiplier != 1;
                    r.Value = cool * r.Modifier * q.Multiplier;
                    r.Estimated |= r.QualityApplied;
                    if (r.Estimated) sum.CoolantEstimated = true;
                    sum.CoolantSupply += r.Value;
                }
                else if (s.IsShield)
                {
                    shieldsPowered++;
                    var q = PowerMath.QualityEffect(s.Quality, s.Qualities, PowerMath.PropShieldMaxHealth);
                    r.QualityMultiplier = q.Multiplier;
                    r.QualityApplied = q.Applied && q.Multiplier != 1;
                    r.Value = (d.ShieldRegen ?? (d.Generation.TryGetValue("Shield", out var sg) ? sg : 0)) * r.Modifier;
                    r.ShieldCapacity = (d.ShieldHealth ?? 0) * q.Multiplier;
                    // 再生量は段階の倍率、最大シールド強度は品質で推定になる (別々に持つ)
                    r.CapacityEstimated = r.QualityApplied;
                    if (r.Estimated) sum.ShieldRegenEstimated = true;
                    if (r.CapacityEstimated) sum.ShieldCapacityEstimated = true;
                    sum.ShieldRegen += r.Value;
                    sum.ShieldCapacity += r.ShieldCapacity;
                    sum.ShieldDamagedDelay = Math.Max(sum.ShieldDamagedDelay, d.DamagedRegenDelay ?? 0);
                    sum.ShieldDownedDelay = Math.Max(sum.ShieldDownedDelay, d.DownedRegenDelay ?? 0);
                }
                else if (d.Generation.TryGetValue("LifeSupport", out var ls))
                    r.Value = ls * r.Modifier;
                continue;
            }
            if (d.ConsumeUnit == PowerUnitKind.Standard && d.ConsumeAmount > 0)
            {
                // 量子ドライブなど (SStandardResourceUnit)。入・切だけ。割り当て済みには入れない (単位の関係が未確認)
                r.Off = !s.On;
                if (s.On) sum.QuantumDrivePower += d.ConsumeAmount;
            }
        }
        sum.PoweredShields = shieldsPowered;
        return sum;
    }
}
