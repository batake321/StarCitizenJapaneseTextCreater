using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［個人戦闘］の［ダメージ判定］: 銃にアタッチメント (スコープ・マズル・アンダーバレル・マガジン) を付けたときの値。
// 値の計算は武器の表と同じ (LoadCompareRoots → FillItemStats → ToWeaponTableRow) を使い、補正は次の 2 か所で入れる:
//   - 武器の component_json を書き換える (ApplyAttachmentsToComponentJson): 連射速度 (fireRate と連続動作の待ち delay・撃った後の待ち cooldownTime)・チャージ時間 (chargeTime)・
//     ビームの毎秒のダメージ (damagePerSecond)・マガジン (ammoContainerRecord)
//   - 弾のデータ (ReadWeaponAmmo の結果) に倍率を掛ける (ApplyAmmoMultipliers): 1発のダメージ (直撃・爆発)・弾速
// 補正を入れていないとき (_ammoMultipliers が null) は何も変えない (武器の表の値は変わらない)

// アタッチメントの補正の倍率 (SWeaponModifierComponentParams.modifier.weaponStats の *Multiplier のうち表の値に効くもの)。
// Damage = damageMultiplier、FireRate = fireRateMultiplier、ProjectileSpeed = projectileSpeedMultiplier、ChargeTime = chargeTimeMultiplier
public sealed record WeaponStatMultipliers(double Damage = 1, double FireRate = 1, double ProjectileSpeed = 1, double ChargeTime = 1)
{
    public static readonly WeaponStatMultipliers None = new();

    // 2 つの補正を合わせる (倍率の掛け算。ゲームデータから合成方法は確認できないので推定)
    public WeaponStatMultipliers Times(WeaponStatMultipliers o) =>
        new(Damage * o.Damage, FireRate * o.FireRate, ProjectileSpeed * o.ProjectileSpeed, ChargeTime * o.ChargeTime);

    public bool IsNone => Damage == 1 && FireRate == 1 && ProjectileSpeed == 1 && ChargeTime == 1;
}

// ［ダメージ判定］の 4 つの欄 (枠の AttachDef.SubType で決める: IronSight / Barrel / BottomAttachment / Magazine)
public enum WeaponAttachmentSlotKind { Scope, Muzzle, Underbarrel, Magazine }

// アタッチメントの色違い 1 つ (色違いの表 item_variants の variant_record)。Name = item_index.name (訳があれば日本語)
public sealed record WeaponAttachmentVariant(string Record, string Name);

// 武器に付けられるアタッチメント 1 つ (items.item_type = WeaponAttachment。色違いは基本品目の Variants にまとめ、別の項目にしない)。
// Record = record 名、Name = item_index.name (訳があれば日本語)、
// Type / SubType / Size / Tags / RequiredTags = SAttachableComponentParams.AttachDef の値、Multipliers = 補正の倍率 (補正が無ければ None)、
// Variants = 色違いの一覧 (item_variants で base_record がこの品目のもの。色違いの中身は空なので、装着の判定は基本品目で行い、色違いも同じとみなす)
public sealed record WeaponAttachmentCandidate(string Record, string Name, string Type, string SubType, int Size, string Tags, string RequiredTags,
    WeaponStatMultipliers Multipliers, IReadOnlyList<WeaponAttachmentVariant> Variants)
{
    // 選択肢の表記: 色違いがあれば「名前（色違い: A, B）」、無ければ名前だけ
    public string DisplayLabel => Variants.Count > 0 ? $"{Name}（色違い: {string.Join(", ", Variants.Select(v => v.Name))}）" : Name;
}

// 武器の枠 1 つと、その枠に付くアタッチメント
public sealed record WeaponAttachmentPort(WeaponAttachmentSlotKind Kind, string PortName, IReadOnlyList<WeaponAttachmentCandidate> Options);

// 武器の枠の一覧。PortsFound = 枠のデータ (raw_json) を取れたか (色違いで基本品目にたどれないと false)、
// DefaultMagazineRecord / DefaultMagazineName = 武器の既定のマガジン (component_json の ammoContainerRecord。無ければ null)
public sealed record WeaponAttachmentSetup(bool PortsFound, IReadOnlyList<WeaponAttachmentPort> Ports, string? DefaultMagazineRecord, string? DefaultMagazineName);

public partial class GameDataQueryService
{
    // 選択肢に出さないアタッチメント (record 名の EntityClassDefinition. を除いた部分。大文字小文字は区別しない)。
    // 枠の規則には合うが、実物のアタッチメントではない (偽物) として利用者の指定で除外するもの
    private static readonly HashSet<string> ExcludedAttachmentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "behr_binoculars_01_FakeOptic",
        "behr_binoculars_01_FakeOptic_Collector",
        "volt_ubarrel_flsh_s1_Sniperglint",
    };

    // 同じく偽物として除外するマガジンの record 名の頭 (vlk_spewgun_ballistic_01_mag と、その派生の _adult・_juvi など。利用者の指定)
    private const string ExcludedMagazinePrefix = "vlk_spewgun_";

    private static bool IsExcludedAttachment(string shortName, string subType) =>
        ExcludedAttachmentNames.Contains(shortName)
        || (subType == "Magazine" && shortName.StartsWith(ExcludedMagazinePrefix, StringComparison.OrdinalIgnoreCase));

    private static string ShortRecordName(string record) =>
        record.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? record["EntityClassDefinition.".Length..] : record;

    // 補正を入れて計算している間だけ設定する倍率 (ReadWeaponAmmo の結果に掛ける)。null なら何も変えない
    private WeaponStatMultipliers? _ammoMultipliers;

    // ReadWeaponAmmo が返す弾のデータに倍率を掛ける (1発のダメージ = 直撃・爆発の各種類に damageMultiplier、弾速に projectileSpeedMultiplier)
    private AmmoInfo ApplyAmmoMultipliers(AmmoInfo a)
    {
        if (_ammoMultipliers is not { } m || (m.Damage == 1 && m.ProjectileSpeed == 1)) return a;
        return a with
        {
            Speed = a.Speed * m.ProjectileSpeed,
            Direct = a.Direct.ToDictionary(kv => kv.Key, kv => kv.Value * m.Damage),
            Explosion = a.Explosion.ToDictionary(kv => kv.Key, kv => kv.Value * m.Damage),
        };
    }

    // 武器に付けられるアタッチメントの全件 (除外するものと、raw_json が空で AttachDef の無いものは除く。色違いは基本品目の Variants にまとめる)
    public List<WeaponAttachmentCandidate> LoadWeaponAttachmentCandidates()
    {
        var list = new List<WeaponAttachmentCandidate>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT x.record_name, COALESCE(x.name, ''), COALESCE(i.raw_json, '')
                FROM item_index x LEFT JOIN items i ON i.record_name = x.record_name
                WHERE x.item_type = 'WeaponAttachment'
                ORDER BY x.record_name
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var record = r.GetString(0);
                var raw = r.GetString(2);
                if (raw.Length == 0) continue;
                var c = ParseAttachmentCandidate(record, r.GetString(1), raw);
                if (c != null && !IsExcludedAttachment(ShortRecordName(record), c.SubType)) list.Add(c);
            }
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }

        // 色違い (item_variants) は基本品目にまとめる: 色違いは別の項目にせず、基本品目の Variants に名前を入れる。
        // (マガジンは色違いの表の対象外 (IsVariantTargetType) なので、マガジンは 1 つずつの項目のまま)
        var variants = LoadAttachmentVariants();
        var variantRecords = variants.Values.SelectMany(v => v).Select(v => v.Record).ToHashSet(StringComparer.Ordinal);
        return list.Where(c => !variantRecords.Contains(c.Record))
            .Select(c => variants.TryGetValue(c.Record, out var vs) ? c with { Variants = vs } : c)
            .ToList();
    }

    // 色違いの表 (item_variants) の WeaponAttachment の行: 基本品目の record → 色違いの一覧 (名前順)。表が無い古い DB では空
    private Dictionary<string, List<WeaponAttachmentVariant>> LoadAttachmentVariants()
    {
        var map = new Dictionary<string, List<WeaponAttachmentVariant>>(StringComparer.Ordinal);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT v.base_record, v.variant_record, COALESCE(x.name, '')
                FROM item_variants v JOIN item_index x ON x.record_name = v.variant_record
                WHERE x.item_type = 'WeaponAttachment' AND v.variant_record <> v.base_record
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var record = r.GetString(1);
                var name = r.GetString(2).Trim();
                if (name.Length == 0 || name.StartsWith('@')) name = ShortRecordName(record);
                if (!map.TryGetValue(r.GetString(0), out var vs)) map[r.GetString(0)] = vs = new List<WeaponAttachmentVariant>();
                if (vs.All(v => v.Record != record)) vs.Add(new WeaponAttachmentVariant(record, name));
            }
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        foreach (var vs in map.Values) vs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return map;
    }

    // 武器の枠と、それぞれに付くアタッチメント (アタッチメントの一覧も読む。詳細パネル・チャットなど 1 件だけ引くとき用)
    public WeaponAttachmentSetup GetWeaponAttachmentSetup(string weaponRecord) =>
        GetWeaponAttachmentSetup(weaponRecord, LoadWeaponAttachmentCandidates());

    private static WeaponAttachmentCandidate? ParseAttachmentCandidate(string record, string indexName, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array)
                return null;
            JsonElement? attachDef = null;
            var mult = WeaponStatMultipliers.None;
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t)) continue;
                switch (t.GetString())
                {
                    case "SAttachableComponentParams" when c.TryGetProperty("AttachDef", out var ad) && ad.ValueKind == JsonValueKind.Object:
                        attachDef = ad;
                        break;
                    case "SWeaponModifierComponentParams" when c.TryGetProperty("modifier", out var mo) && mo.ValueKind == JsonValueKind.Object
                                                                && mo.TryGetProperty("weaponStats", out var ws) && ws.ValueKind == JsonValueKind.Object:
                        static double M(JsonElement el, string name) => TryNum(el, name, out var v) ? v : 1;
                        mult = new WeaponStatMultipliers(M(ws, "damageMultiplier"), M(ws, "fireRateMultiplier"), M(ws, "projectileSpeedMultiplier"), M(ws, "chargeTimeMultiplier"));
                        break;
                }
            }
            if (attachDef is not { } a) return null;
            static string S(JsonElement el, string name) => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            var size = TryNum(a, "Size", out var sz) ? (int)sz : 0;
            var name = indexName.Trim();
            if (name.Length == 0 || name.StartsWith('@')) name = ShortRecordName(record);
            return new WeaponAttachmentCandidate(record, name, S(a, "Type"), S(a, "SubType"), size, S(a, "Tags"), S(a, "RequiredTags"), mult, []);
        }
        catch (JsonException) { return null; }
    }

    // 枠 (SItemPortDef) にアタッチメントが付くか:
    //   (1) AttachDef.Type が枠の Types[].Type と同じで、AttachDef.SubType がその SubTypes に含まれる
    //   (2) MinSize <= Size <= MaxSize
    //   (3) 枠の RequiredPortTags の語 (空白区切り) がすべて AttachDef.Tags に含まれる
    //   (4) AttachDef.RequiredTags の語がすべて枠の PortTags に含まれる
    private static bool AttachmentFits(PortDef port, WeaponAttachmentCandidate c)
    {
        if (!port.Types.Any(t => t.Type == c.Type && t.SubTypes.Contains(c.SubType))) return false;
        if (c.Size < port.MinSize || c.Size > port.MaxSize) return false;
        var tags = Words(c.Tags);
        if (Words(port.RequiredPortTags).Any(w => !tags.Contains(w))) return false;
        var portTags = Words(port.PortTags);
        return Words(c.RequiredTags).All(portTags.Contains);
    }

    private static HashSet<string> Words(string s) =>
        new(s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

    private sealed record PortDef(string Name, int MinSize, int MaxSize, List<(string Type, List<string> SubTypes)> Types, string RequiredPortTags, string PortTags);

    // raw_json の SItemPortContainerComponentParams.Ports。コンポーネントが無ければ空
    private static List<PortDef> ReadPortDefs(string raw)
    {
        var list = new List<PortDef>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array)
                return list;
            static string S(JsonElement el, string name) => el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "SItemPortContainerComponentParams") continue;
                if (!c.TryGetProperty("Ports", out var ports) || ports.ValueKind != JsonValueKind.Array) continue;
                foreach (var p in ports.EnumerateArray())
                {
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    var types = new List<(string, List<string>)>();
                    if (p.TryGetProperty("Types", out var ts) && ts.ValueKind == JsonValueKind.Array)
                        foreach (var ty in ts.EnumerateArray())
                        {
                            if (ty.ValueKind != JsonValueKind.Object) continue;
                            var subs = ty.TryGetProperty("SubTypes", out var st) && st.ValueKind == JsonValueKind.Array
                                ? st.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
                                : new List<string>();
                            types.Add((S(ty, "Type"), subs));
                        }
                    list.Add(new PortDef(S(p, "Name"), TryNum(p, "MinSize", out var mn) ? (int)mn : 0, TryNum(p, "MaxSize", out var mx) ? (int)mx : 0,
                        types, S(p, "RequiredPortTags"), S(p, "PortTags")));
                }
            }
        }
        catch (JsonException) { }
        return list;
    }

    // 枠の SubTypes から［ダメージ判定］の欄を決める (どれにも当たらない枠は null。例 module_attach (SubTypes = Utility)。
    // canister_attach (マルチツール grin_multitool_01 など。SubTypes = Magazine) はマガジンの欄になる)
    private static WeaponAttachmentSlotKind? SlotKindOf(PortDef port)
    {
        var subs = port.Types.SelectMany(t => t.SubTypes).ToHashSet(StringComparer.Ordinal);
        if (subs.Contains("IronSight")) return WeaponAttachmentSlotKind.Scope;
        if (subs.Contains("Barrel")) return WeaponAttachmentSlotKind.Muzzle;
        if (subs.Contains("BottomAttachment")) return WeaponAttachmentSlotKind.Underbarrel;
        if (subs.Contains("Magazine")) return WeaponAttachmentSlotKind.Magazine;
        return null;
    }

    private (string Raw, string Component) ReadItemJson(string record)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(raw_json, ''), COALESCE(component_json, '') FROM items WHERE record_name = @rn LIMIT 1";
        cmd.Parameters.AddWithValue("@rn", record);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetString(0), r.GetString(1)) : ("", "");
    }

    // 武器の枠と、それぞれに付くアタッチメント (candidates は LoadWeaponAttachmentCandidates の結果)。
    // 色違い (raw_json が空) は基本品目の枠を使う (色違いの表 item_variants、無ければ同じ名前の複製から基本品目を引く。BaseComponentJson と同じたどり方)。
    // 同じ欄の枠が 2 つ以上あれば最初の枠だけを使う
    public WeaponAttachmentSetup GetWeaponAttachmentSetup(string weaponRecord, IReadOnlyList<WeaponAttachmentCandidate> candidates)
    {
        var (raw, component) = ReadItemJson(weaponRecord);
        if (raw.Length == 0 || component.Length == 0)
        {
            using var loc = new LocReader(_translationsDbPath);
            var (baseRecord, _) = VariantRow(weaponRecord);
            if (baseRecord == null)
                foreach (var g in SameNameRecords(weaponRecord, loc))
                {
                    (baseRecord, _) = VariantRow(g);
                    if (baseRecord != null) break;
                }
            if (baseRecord != null)
            {
                var b = ReadItemJson(baseRecord);
                if (raw.Length == 0) raw = b.Raw;
                if (component.Length == 0) component = b.Component;
            }
        }

        // 既定のマガジン (component_json の ammoContainerRecord のファイル名。LoadMagazineRaw と同じ引き方)
        string? defaultMag = null, defaultMagName = null;
        try
        {
            if (component.Length > 0)
            {
                using var doc = JsonDocument.Parse(component);
                if (doc.RootElement.TryGetProperty("ammoContainerRecord", out var ac) && ac.ValueKind == JsonValueKind.String
                    && Path.GetFileNameWithoutExtension(ac.GetString() ?? "") is { Length: > 0 } stem)
                {
                    using var cmd = _conn.CreateCommand();
                    cmd.CommandText = "SELECT record_name, COALESCE(name, '') FROM item_index WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
                    cmd.Parameters.AddWithValue("@rn", "EntityClassDefinition." + stem);
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        defaultMag = r.GetString(0);
                        defaultMagName = r.GetString(1).Trim() is { Length: > 0 } n && !n.StartsWith('@') ? n : stem;
                    }
                    else { defaultMag = "EntityClassDefinition." + stem; defaultMagName = stem; }
                }
            }
        }
        catch (JsonException) { }

        if (raw.Length == 0) return new WeaponAttachmentSetup(false, [], defaultMag, defaultMagName);
        var ports = new List<WeaponAttachmentPort>();
        foreach (var p in ReadPortDefs(raw))
        {
            if (SlotKindOf(p) is not { } kind || ports.Any(x => x.Kind == kind)) continue;
            ports.Add(new WeaponAttachmentPort(kind, p.Name, candidates.Where(c => AttachmentFits(p, c)).ToList()));
        }
        return new WeaponAttachmentSetup(true, ports, defaultMag, defaultMagName);
    }

    // ［個人戦闘］の表の 1 行 (record・種類で指定) を、補正とマガジンを入れて計算し直す。値の計算は表と同じ (GetWeaponTable / GetOtherPersonalWeaponRows)。
    // magazineRecord: null = 既定のまま、"" = マガジンなし (ammoContainerRecord を外す)、それ以外 = そのマガジンの record (弾と装弾数をそのマガジンから取る)。
    // 表の行が見つからなければ null
    public WeaponTableRow? GetPersonalWeaponRowWithAttachments(string record, string kind, WeaponStatMultipliers multipliers, string? magazineRecord,
        IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null)
    {
        var allCat = CompareCategories.First(c => c.Kind == CompareKind.PersonalWeapon && c.RecordToken == null);
        // 表と同じ種類の読み込み (種類の名前が同じ種類 → どの種類にも当たらない武器の読み込み) で、その record を代表とする行を探す
        var cats = PersonalWeaponTableCategories.Where(c => c.Label == kind).Append(allCat).ToList();
        using var loc = new LocReader(_translationsDbPath);
        _ammoMultipliers = multipliers;
        try
        {
            foreach (var cat in cats)
            {
                var isOther = ReferenceEquals(cat, allCat);
                List<CompareEntry> roots;
                try
                {
                    // 性能を入れる前 (rootFilter) に、その行の component_json を補正・マガジンを入れたものに差し替える
                    roots = LoadCompareRoots(cat, null, loc, out _, out var noEntries, e =>
                    {
                        if (!string.Equals(e.Record, record, StringComparison.Ordinal)) return false;
                        if (isOther && PersonalWeaponCategoryOf(e) != null) return false;
                        e.ComponentJson = ApplyAttachmentsToComponentJson(e.ComponentJson, multipliers, magazineRecord);
                        return true;
                    });
                    if (noEntries) continue;
                }
                catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return null; }
                roots = roots.Where(e => !IsHealingDevice(e)).ToList();
                if (roots.Count == 0) continue;
                FillComparePrices(roots, uexItemPrices);
                return ToWeaponTableRow(roots[0], loc);
            }
            return null;
        }
        finally { _ammoMultipliers = null; }
    }

    // 武器の component_json に補正とマガジンを入れる (fireActions の中だけ書き換える。何も変えなければ元の文字列のまま):
    //   連射速度: SWeaponActionFire* の fireRate × fireRateMultiplier。
    //     連続動作 (SWeaponActionSequenceParams) の sequenceEntries の待ち (delay) にも掛ける (銃全体の連射が倍率どおりになるように):
    //     unit が Seconds (秒) なら delay ÷ fireRateMultiplier、RPM なら delay × fireRateMultiplier (FireCycle の単位の読み方と同じ)。
    //     ほかの unit は単位が分からないので変えない。
    //     バースト (SWeaponActionFireBurstParams)・チャージ式 (SWeaponActionFireChargedParams) の撃った後の待ち (cooldownTime。秒) も ÷ fireRateMultiplier
    //     (FireCycle で 1 周期の秒数に足す待ち。chargeTime には fireRateMultiplier を掛けない)
    //   チャージ時間: SWeaponActionFireChargedParams の chargeTime × chargeTimeMultiplier
    //   ビーム: SWeaponActionFireBeamParams の damagePerSecond の各値 × damageMultiplier
    //   マガジン: ammoContainerRecord をそのマガジンのファイル名にする ("" なら外す。null なら変えない)
    private static string ApplyAttachmentsToComponentJson(string componentJson, WeaponStatMultipliers m, string? magazineRecord)
    {
        if (string.IsNullOrEmpty(componentJson) || (m.IsNone && magazineRecord == null)) return componentJson;
        JsonObject? root;
        try { root = JsonNode.Parse(componentJson) as JsonObject; }
        catch (JsonException) { return componentJson; }
        if (root == null) return componentJson;
        var changed = false;

        static bool TryDouble(JsonNode? n, out double v)
        {
            v = 0;
            return n is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && jv.TryGetValue(out v);
        }
        void Scale(JsonObject o, string key, double mult)
        {
            if (mult == 1 || !TryDouble(o[key], out var v)) return;
            o[key] = v * mult;
            changed = true;
        }
        void Walk(JsonNode? node, int depth)
        {
            if (depth > 12 || node == null) return;
            if (node is JsonArray arr) { foreach (var c in arr) Walk(c, depth + 1); return; }
            if (node is not JsonObject o) return;
            var type = o["_Type_"] is JsonValue tv && tv.GetValueKind() == JsonValueKind.String ? tv.GetValue<string>() : "";
            if (type.StartsWith("SWeaponActionFire", StringComparison.Ordinal)) Scale(o, "fireRate", m.FireRate);
            if ((type == "SWeaponActionFireBurstParams" || type == "SWeaponActionFireChargedParams") && m.FireRate > 0)
                Scale(o, "cooldownTime", 1 / m.FireRate);
            if (type == "SWeaponActionSequenceParams" && m.FireRate > 0 && o["sequenceEntries"] is JsonArray entries)
                foreach (var en in entries.OfType<JsonObject>())
                {
                    var unit = en["unit"] is JsonValue uv && uv.GetValueKind() == JsonValueKind.String ? uv.GetValue<string>() : "";
                    if (unit == "Seconds") Scale(en, "delay", 1 / m.FireRate);
                    else if (unit == "RPM") Scale(en, "delay", m.FireRate);
                }
            if (type == "SWeaponActionFireChargedParams") Scale(o, "chargeTime", m.ChargeTime);
            if (type == "SWeaponActionFireBeamParams" && o["damagePerSecond"] is JsonObject dps)
                foreach (var key in dps.Select(kv => kv.Key).ToList())
                    if (key != "_Type_") Scale(dps, key, m.Damage);
            foreach (var key in o.Select(kv => kv.Key).ToList()) Walk(o[key], depth + 1);
        }
        if (!m.IsNone) Walk(root["fireActions"], 0);

        if (magazineRecord != null)
        {
            if (magazineRecord.Length == 0) changed |= root.Remove("ammoContainerRecord");
            else { root["ammoContainerRecord"] = ShortRecordName(magazineRecord) + ".json"; changed = true; }
        }
        return changed ? root.ToJsonString() : componentJson;
    }

    // 個人武器に付けられるアタッチメントの一覧 (詳細パネルの【装着できるアタッチメント】と、チャットで AI に渡す個人武器の情報に使う)。
    // Markdown の表「パーツ | サイズ | アタッチメント | 色違い」で、付けられるもの 1 つにつき 1 行 (枠 (GetWeaponAttachmentSetup) の順)。
    // パーツ (欄の名前)・サイズ (枠のサイズ) は枠の最初の行だけに書く。色違いは基本品目の行の「色違い」の列に「、」でつないで書く。
    // 付けられるものが無い枠は、アタッチメントの列に「付けられるもの無し」と書いた 1 行。
    // 一覧を書くだけで補正の値は書かない。枠のデータが取れなければその旨の 1 行。
    // 欄に当たる枠が 1 つも無い武器 (手榴弾・近接武器など) は章ごと出さない ("" を返す)。それ以外は末尾に改行が付く
    public string WeaponAttachmentListText(string weaponRecord)
    {
        var setup = GetWeaponAttachmentSetup(weaponRecord);
        if (setup.PortsFound && setup.Ports.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine("【装着できるアタッチメント】");
        if (!setup.PortsFound)
        {
            sb.AppendLine("  枠のデータが取れないため、付けられるアタッチメントの一覧を出せない");
            return sb.ToString();
        }
        // 枠のサイズ (SItemPortDef の MinSize / MaxSize。枠の名前で引く)
        var sizes = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal);
        foreach (var p in ReadPortDefs(ResolveWeaponRawJson(weaponRecord)))
            sizes.TryAdd(p.Name, (p.MinSize, p.MaxSize));
        // 表の書き方は【何発で倒せるか】の表と同じ (行頭に空白 2 つ、「|」で始めて「|」で終える)
        sb.AppendLine("  | パーツ | サイズ | アタッチメント | 色違い |");
        sb.AppendLine("  |---|---|---|---|");
        foreach (var port in setup.Ports.OrderBy(p => p.Kind))
        {
            var label = port.Kind switch
            {
                WeaponAttachmentSlotKind.Scope => "スコープ",
                WeaponAttachmentSlotKind.Muzzle => "マズル",
                WeaponAttachmentSlotKind.Underbarrel => "アンダーバレル",
                _ => "マガジン",
            };
            var size = "";
            if (sizes.TryGetValue(port.PortName, out var sz) && sz.Max > 0)
                size = sz.Min == sz.Max ? $"S{sz.Max}" : $"S{sz.Min}〜S{sz.Max}";
            if (port.Options.Count == 0)
            {
                sb.AppendLine($"  | {label} | {size} | 付けられるもの無し |  |");
                continue;
            }
            var first = true;
            foreach (var o in port.Options)
            {
                var variants = string.Join("、", o.Variants.Select(v => v.Name));
                sb.AppendLine(first ? $"  | {label} | {size} | {o.Name} | {variants} |" : $"  |  |  | {o.Name} | {variants} |");
                first = false;
            }
        }
        return sb.ToString();
    }

    // 武器の raw_json (色違いで空なら基本品目のもの。GetWeaponAttachmentSetup と同じたどり方)。無ければ ""
    private string ResolveWeaponRawJson(string weaponRecord)
    {
        var (raw, _) = ReadItemJson(weaponRecord);
        if (raw.Length > 0) return raw;
        using var loc = new LocReader(_translationsDbPath);
        var (baseRecord, _) = VariantRow(weaponRecord);
        if (baseRecord == null)
            foreach (var g in SameNameRecords(weaponRecord, loc))
            {
                (baseRecord, _) = VariantRow(g);
                if (baseRecord != null) break;
            }
        return baseRecord != null ? ReadItemJson(baseRecord).Raw : "";
    }
}
