using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// ［詳細］の性能の節: 追加で取り込む品目 (GameDataExtractor.ExtractExtraItemsAsync。採掘レーザー・採掘モジュール・サルベージのモジュール・
// サルベージヘッド・トラクタービーム・燃料ノズル・鉱石ポッド) の値。items.raw_json の部品 (GameDataExtractor.ExtraItemKeepComponents) から読む。
// 欄の名前はゲームの訳 (global.ini) の言葉: 説明文の「項目: 値」の日本語 (最適射程・最大射程・モジュールスロット・レーザー不安定性・抵抗値・
// 最適チャージウィンドウサイズ・不活性物質レベル・採掘レーザー出力・抽出レーザー出力・破砕ダメージ・持続時間・使用回数・抽出速度・有効半径・抽出効率・
// 最大出力距離・最大角度・クラスター修飾子・破局的チャージ速度・最適チャージウィンドウ速度・容量)、質量 (scan_data_mass)、耐久値 (engineering_ui_Item_Health)。
// 説明文に書かれていても、データの値と説明文の値の関係が分からないもの (採掘レーザーの採掘レーザー出力・抽出レーザー出力) は出さない
// (説明文の値は「性能 (説明文)」の欄に出る)
public partial class GameDataQueryService
{
    // 追加の品目の欄 (表示の順。ItemColumns の「それ以外の種類」の「性能 (説明文)」の前に入れる)
    internal static readonly string[] ExtraItemColumns =
    [
        "容量", "入れられる資源", "最適射程", "最大射程", "最大出力距離", "最大角度", "モジュールスロット",
        "採掘レーザー出力", "抽出レーザー出力", "レーザー不安定性", "抵抗値", "最適チャージウィンドウサイズ", "最適チャージウィンドウ速度",
        "破局的チャージ速度", "破砕ダメージ", "クラスター修飾子", "不活性物質レベル",
        "抽出速度", "有効半径", "抽出効率", "持続時間", "使用回数", "質量", "耐久値",
    ];

    // 追加の品目の値を出す種類 (今までの品目の表・比べるツールに欄が増えないよう、この種類だけ)
    internal static bool IsExtraDetailType(string itemType) =>
        GameDataExtractor.ExtraItemTypes.Contains(itemType) || itemType == "Container";

    private void FillExtraItemCells(CompareEntry e, Func<string, JsonElement?> comp, LocReader loc)
    {
        if (!IsExtraDetailType(e.ItemType)) return;

        // 容量・入れられる資源 (鉱石ポッドなど。ResourceContainer)
        if (comp("ResourceContainer") is { } rc)
        {
            if (rc.TryGetProperty("capacity", out var cap) && cap.ValueKind == JsonValueKind.Object)
            {
                if (TryNum(cap, "standardCargoUnits", out var scu) && scu > 0) e.Cells["容量"] = $"{scu:#,0.###} SCU";
                else if (TryNum(cap, "microSCU", out var micro) && micro > 0) e.Cells["容量"] = $"{micro / 1_000_000:#,0.######} SCU";
            }
            var res = ContainerResourceNames(rc, loc);
            if (res.Count > 0) e.Cells["入れられる資源"] = string.Join("、", res);
        }

        // 射程・モジュールスロット (採掘レーザー・トラクタービーム。SCItemWeaponComponentParams.fireActions)
        if (comp("SCItemWeaponComponentParams") is { } wc && wc.TryGetProperty("fireActions", out var fas) && fas.ValueKind == JsonValueKind.Array)
        {
            foreach (var fa in fas.EnumerateArray())
            {
                if (fa.ValueKind != JsonValueKind.Object || !fa.TryGetProperty("_Type_", out var ft)) continue;
                var type = ft.GetString();
                if (type == "SWeaponActionFireBeamParams" && e.ItemType == "WeaponMining")
                {
                    // 1 つめのビーム (採掘レーザー)。説明文の Optimal Range / Maximum Range と同じ値
                    if (TryNum(fa, "fullDamageRange", out var full) && full > 0) e.Cells["最適射程"] = $"{full:#,0.##} m";
                    if (TryNum(fa, "zeroDamageRange", out var zero) && zero > 0) e.Cells["最大射程"] = $"{zero:#,0.##} m";
                    break;
                }
                if (type == "SWeaponActionFireTractorBeamParams")
                {
                    if (TryNum(fa, "maxDistance", out var max) && max > 0) e.Cells["最大射程"] = $"{max:#,0.##} m";
                    if (TryNum(fa, "fullStrengthDistance", out var fsd) && fsd > 0) e.Cells["最大出力距離"] = $"{fsd:#,0.##} m";
                    if (TryNum(fa, "maxAngle", out var ang) && ang > 0) e.Cells["最大角度"] = $"{ang:#,0.##}°";
                    break;
                }
            }
        }
        if (comp("SItemPortContainerComponentParams") is { } pc && pc.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
        {
            // 採掘モジュール・サルベージのモジュールを付ける口の数 (説明文の Module Slots)
            int slots = 0;
            foreach (var p in ports.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("Types", out var types) || types.ValueKind != JsonValueKind.Array) continue;
                if (types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("Type", out var tt)
                                                    && tt.GetString() is "MiningModifier" or "SalvageModifier")) slots++;
            }
            if (slots > 0) e.Cells["モジュールスロット"] = slots.ToString();
        }

        // 採掘レーザーの補正 (SEntityComponentMiningLaserParams)
        if (comp("SEntityComponentMiningLaserParams") is { } ml)
        {
            if (ml.TryGetProperty("miningLaserModifiers", out var mods)) AddMiningModifierCells(e, mods);
            if (ml.TryGetProperty("filterParams", out var fp)) AddFilterCell(e, fp);
        }

        // 採掘モジュール・スクレーパーモジュールの補正 (EntityComponentAttachableModifierParams.modifiers)
        if (comp("EntityComponentAttachableModifierParams") is { } am)
        {
            if (am.TryGetProperty("modifiers", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                double? lifetime = null;
                foreach (var m in list.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("_Type_", out var mt)) continue;
                    if (m.TryGetProperty("modifierLifetime", out var life) && life.ValueKind == JsonValueKind.Object && TryNum(life, "lifetime", out var lt) && lt > 0)
                        lifetime = lt;
                    switch (mt.GetString())
                    {
                        case "ItemWeaponModifiersParams":
                        {
                            if (!m.TryGetProperty("weaponModifier", out var wm) || !wm.TryGetProperty("weaponStats", out var ws) || ws.ValueKind != JsonValueKind.Object) break;
                            // 採掘モジュール: 1 つめのビーム (fireActionIndex 0) が採掘レーザー、2 つめ (1) が抽出レーザーの出力の倍率
                            if (e.ItemType == "MiningModifier" && TryNum(ws, "damageMultiplier", out var dm) && Math.Abs(dm - 1) > 1e-9
                                && m.TryGetProperty("fireActionIndex", out var fi) && fi.TryGetInt32(out var idx) && idx is 0 or 1)
                                e.Cells[idx == 0 ? "採掘レーザー出力" : "抽出レーザー出力"] = Percent((dm - 1) * 100);
                            // スクレーパーモジュール: 抽出速度・有効半径・抽出効率 (salvageModifier)
                            if (e.ItemType == "SalvageModifier" && ws.TryGetProperty("salvageModifier", out var sm) && sm.ValueKind == JsonValueKind.Object)
                            {
                                if (TryNum(sm, "salvageSpeedMultiplier", out var sp)) e.Cells["抽出速度"] = $"{sp:0.###}";
                                if (TryNum(sm, "radiusMultiplier", out var rad)) e.Cells["有効半径"] = $"{rad:0.##} m";
                                if (TryNum(sm, "extractionEfficiency", out var eff)) e.Cells["抽出効率"] = $"{eff * 100:0.##}%";
                            }
                            break;
                        }
                        case "ItemMiningModifierParams":
                            if (m.TryGetProperty("MiningLaserModifier", out var mlm)) AddMiningModifierCells(e, mlm);
                            break;
                        case "MiningFilterItemModifierParams":
                            if (m.TryGetProperty("filterParams", out var fp2)) AddFilterCell(e, fp2);
                            break;
                    }
                }
                if (lifetime is { } l) e.Cells["持続時間"] = $"{l:#,0.##}秒";
            }
            // 使用回数は、使うときに作動させるもの (activationMethod = ActivateOnDemand。アクティブの採掘モジュール) だけ
            // (付けている間ずっと効くもの (ActivateOnAttach。パッシブ) の charges は 1 で、説明文にも使用回数は無い)
            if (am.TryGetProperty("activationMethod", out var act) && act.GetString() == "ActivateOnDemand"
                && am.TryGetProperty("charges", out var ch) && ch.ValueKind == JsonValueKind.Number && ch.TryGetInt32(out var charges) && charges > 0)
                e.Cells["使用回数"] = charges.ToString();
        }

        // 質量 (SEntityPhysicsControllerParams.PhysType.Mass)・耐久値 (SHealthComponentParams.Health)
        if (comp("SEntityPhysicsControllerParams") is { } phys && phys.TryGetProperty("PhysType", out var pt) && pt.ValueKind == JsonValueKind.Object
            && TryNum(pt, "Mass", out var mass) && mass > 0)
            e.Cells["質量"] = $"{mass:#,0.##} kg";
        if (comp("SHealthComponentParams") is { } hp && TryNum(hp, "Health", out var health) && health > 0)
            e.Cells["耐久値"] = $"{health:#,0.##}";
    }

    private static string Percent(double v) => $"{v:+0.##;-0.##;0}%";

    // MiningLaserModifiers の各値 (FloatModifierMultiplicative.value。% の増減。null・0 の値は出さない)
    private static void AddMiningModifierCells(CompareEntry e, JsonElement mods)
    {
        if (mods.ValueKind != JsonValueKind.Object) return;
        foreach (var (key, label) in new[]
                 {
                     ("laserInstability", "レーザー不安定性"), ("resistanceModifier", "抵抗値"), ("optimalChargeWindowSizeModifier", "最適チャージウィンドウサイズ"),
                     ("optimalChargeWindowRateModifier", "最適チャージウィンドウ速度"), ("catastrophicChargeWindowRateModifier", "破局的チャージ速度"),
                     ("shatterdamageModifier", "破砕ダメージ"), ("clusterFactorModifier", "クラスター修飾子"),
                 })
        {
            if (mods.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.Object && TryNum(m, "value", out var v) && Math.Abs(v) > 1e-9)
                e.Cells[label] = Percent(v);
        }
    }

    // 不活性物質レベル (MiningFilterParams.filterModifier.value)。値は不活性物質を除く割合で、説明文の Inert Material Level は符号を逆にした値
    // (例 Arbor MH1: value 30 → Inert Material Level: -30%。採掘レーザー・採掘モジュールの説明文のある品目はすべてこの関係)
    private static void AddFilterCell(CompareEntry e, JsonElement fp)
    {
        if (fp.ValueKind == JsonValueKind.Object && fp.TryGetProperty("filterModifier", out var fm) && fm.ValueKind == JsonValueKind.Object
            && TryNum(fm, "value", out var v) && Math.Abs(v) > 1e-9)
            e.Cells["不活性物質レベル"] = Percent(-v);
    }

    // 入れられる資源の名前 (英語。inclusiveResources と、inclusiveGroups の資源の種類のグループに直接入る資源 (resource_types.group_record)。
    // exclusiveResources は除く)。名前の無い資源は出さない。資源の種類のグループの名前はゲームの訳が仮の文字 (@LOC_PLACEHOLDER) のものがあるので、
    // グループではなく資源の名前を並べる
    private List<string> ContainerResourceNames(JsonElement rc, LocReader loc)
    {
        static IEnumerable<string> Records(JsonElement rc, string prop)
        {
            if (!rc.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
            foreach (var x in arr.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && x.TryGetProperty("_RecordName_", out var rn) && rn.GetString() is { Length: > 0 } s) yield return s;
        }
        var resources = new List<string>(Records(rc, "inclusiveResources"));
        var groups = Records(rc, "inclusiveGroups").ToList();
        var excluded = new HashSet<string>(Records(rc, "exclusiveResources"), StringComparer.Ordinal);
        var excludedGroups = new HashSet<string>(Records(rc, "exclusiveGroups"), StringComparer.Ordinal);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, name_key, name_en, group_record FROM resource_types";
            using var r = cmd.ExecuteReader();
            var rows = new List<(string Rec, string? Key, string? En, string? Group)>();
            while (r.Read())
                rows.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            string? NameOf((string Rec, string? Key, string? En, string? Group) row)
            {
                if (!string.IsNullOrWhiteSpace(row.En)) return row.En.Trim();
                if (row.Key is { Length: > 1 } k && k.StartsWith('@')) return loc.English(k[1..])?.Trim();
                return null;
            }
            var byRec = rows.GroupBy(x => x.Rec, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var picked = new List<(string Rec, string? Key, string? En, string? Group)>();
            foreach (var g in groups)
                if (!excludedGroups.Contains(g)) picked.AddRange(rows.Where(x => x.Group == g));
            foreach (var rec in resources)
                if (byRec.TryGetValue(rec, out var row)) picked.Add(row);
            foreach (var row in picked)
            {
                if (excluded.Contains(row.Rec) || (row.Group != null && excludedGroups.Contains(row.Group))) continue;
                var n = NameOf(row);
                if (string.IsNullOrEmpty(n) || n.StartsWith("<=") || n.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)) continue;
                if (seen.Add(n)) names.Add(n);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }
}
