using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public partial class GameDataQueryService : IDisposable
{
    private readonly SqliteConnection _conn;
    // 船の名前・職業・説明文などのローカライズキーを日本語にするための翻訳 DB (ゲームデータ DB と同じフォルダ)
    private readonly string _translationsDbPath;

    // ゲームデータ DB (gamedata_cache.db) の場所。売り場・価格 (item_shop_prices) を別の接続で読み書きするときに使う
    public string DbPath { get; }

    public GameDataQueryService(string gamedataCacheDbPath)
    {
        DbPath = gamedataCacheDbPath;
        _translationsDbPath = Path.Combine(Path.GetDirectoryName(gamedataCacheDbPath) ?? "", "translations.db");
        _conn = new SqliteConnection($"Data Source={gamedataCacheDbPath}");
        _conn.Open();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS knowledge (id INTEGER PRIMARY KEY AUTOINCREMENT, category TEXT NOT NULL DEFAULT 'general', content TEXT NOT NULL, created_at TEXT NOT NULL)";
        cmd.ExecuteNonQuery();
        // 売り場・価格の表 (インデックス構築前の古い DB には無いので、ここでも作っておく)
        cmd.CommandText = ShopPriceStore.TableDdl;
        cmd.ExecuteNonQuery();
        cmd.CommandText = ItemVariantsDdl;
        cmd.ExecuteNonQuery();
        cmd.CommandText = PlayerDamageParamsDdl;
        cmd.ExecuteNonQuery();
    }

    public bool HasData()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM missions";
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    // exactRecordName を渡すと、その record_name の船だけを返す (lookup で見つかった item_index の船を引くとき用)
    public string SearchShips(string query, string? exactRecordName = null)
    {
        using var cmd = _conn.CreateCommand();
        // name_en (英語 ini で解決した船名) がある DB ではそれも検索・表示する。列の無い古い DB では従来どおり
        var hasNameEn = ShipsHasNameEn();
        var cols = hasNameEn ? "record_name, name, manufacturer, career, role, crew_size, size, name_en" : "record_name, name, manufacturer, career, role, crew_size, size";
        if (!string.IsNullOrEmpty(exactRecordName))
        {
            cmd.CommandText = $"SELECT {cols} FROM ships WHERE record_name = @q LIMIT 1";
            cmd.Parameters.AddWithValue("@q", exactRecordName);
        }
        else
        {
            cmd.CommandText = hasNameEn
                ? $"SELECT {cols} FROM ships WHERE record_name LIKE @q OR name LIKE @q OR name_en LIKE @q ORDER BY name LIMIT 20"
                : $"SELECT {cols} FROM ships WHERE record_name LIKE @q OR name LIKE @q ORDER BY name LIMIT 20";
            cmd.Parameters.AddWithValue("@q", $"%{query}%");
        }
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var rn = reader.GetString(0);
            var name = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var mfr = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var career = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var role = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var crew = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
            var size = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            var nameEn = hasNameEn && !reader.IsDBNull(7) ? reader.GetString(7) : "";

            // ローカライズキー (@vehicle_…) は翻訳 DB で日本語にする。説明文はゲーム内の船の説明 (vehicle_Desc…)
            var nameJa = ResolveLocKey(name);
            var desc = name.StartsWith('@') ? LookupLoc(name[1..].Replace("vehicle_Name", "vehicle_Desc")) : null;
            // メーカーはゲームデータではファイルの場所しか入っていないので、説明文の「メーカー: …」の行から取る
            if (mfr.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                var mm = desc != null ? System.Text.RegularExpressions.Regex.Match(desc, @"メーカー[:：]\s*(.+?)(?:\\n|$)") : null;
                mfr = mm != null && mm.Success ? mm.Groups[1].Value.Trim() : "";
            }

            sb.Append($"- {(!string.IsNullOrEmpty(nameEn) ? nameEn : nameJa)}");
            if (!string.IsNullOrEmpty(nameJa) && nameJa != nameEn && !nameJa.StartsWith('@')) sb.Append($" | 日本語名: {nameJa}");
            if (!string.IsNullOrEmpty(mfr)) sb.Append($" | メーカー: {mfr}");
            if (!string.IsNullOrEmpty(career)) sb.Append($" | 職業: {ResolveLocKey(career)}");
            if (!string.IsNullOrEmpty(role)) sb.Append($" | 種別: {ResolveLocKey(role)}");
            if (crew > 0) sb.Append($" | 乗員: {crew}");
            if (size > 0) sb.Append($" | サイズ: {size}");
            // 積載量 (船の積載量の共通の関数: ゲームデータ → UEX → Ship Matrix)。0・不明なら出さない
            if (ShipCargo.Resolve(!string.IsNullOrEmpty(nameEn) ? nameEn : nameJa, mfr, rn) is { } cargo)
                sb.Append($" | 積載量: {ShipCargo.Format(cargo.Scu)} SCU ({cargo.Source})");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(desc))
                sb.AppendLine("    説明: " + desc.Replace("\\n", " ").Trim());

            // ハードポイントは種類とサイズごとの数にまとめる (一覧のままだと長く、肝心の情報が埋もれる)
            var ports = GetShipPortSummary(rn);
            if (!string.IsNullOrEmpty(ports)) sb.AppendLine("    装備の取り付け口: " + ports);
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): 船 ===\n{sb}" : "";
    }

    // 「@vehicle_…」のようなローカライズキーなら翻訳 DB で日本語 (無ければ英語) にする。キーでなければそのまま返す
    private string ResolveLocKey(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith('@')) return value;
        return LookupLoc(value[1..]) ?? value;
    }

    // 翻訳 DB からキーの訳を引く (",P" 付きのキーも試す)。日本語が無ければ英語、どちらも無ければ null
    private string? LookupLoc(string key)
    {
        try
        {
            if (!File.Exists(_translationsDbPath)) return null;
            using var conn = new SqliteConnection($"Data Source={_translationsDbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT japanese, english FROM translations WHERE key = @k OR key = @kp ORDER BY (key = @k) DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@k", key);
            cmd.Parameters.AddWithValue("@kp", key + ",P");
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var ja = r.IsDBNull(0) ? null : r.GetString(0);
            var en = r.IsDBNull(1) ? null : r.GetString(1);
            return !string.IsNullOrWhiteSpace(ja) ? ja : (!string.IsNullOrWhiteSpace(en) ? en : null);
        }
        catch { return null; }
    }

    // 船のハードポイントを「種類 (サイズ) ×数」にまとめる
    private string GetShipPortSummary(string shipRecordName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT item_type, size, COUNT(*) FROM ship_ports WHERE ship_record_name = @srn GROUP BY item_type, size ORDER BY size DESC, item_type";
        cmd.Parameters.AddWithValue("@srn", shipRecordName);
        using var reader = cmd.ExecuteReader();
        var parts = new List<string>();
        while (reader.Read())
        {
            var itemType = reader.IsDBNull(0) ? "" : reader.GetString(0);
            if (string.IsNullOrEmpty(itemType)) continue;
            var size = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var n = reader.GetInt64(2);
            parts.Add($"{itemType} (S{size}) ×{n}");
        }
        return string.Join(", ", parts);
    }

    // 英語名が「船名 + 空白 + …」で始まる船 (派生型。例: Drake Ironclad → Drake Ironclad Assault) の record_name を返す
    public List<string> FindShipVariants(string shipNameEn, string excludeRecordName)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(shipNameEn) || !ShipsHasNameEn()) return list;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT record_name FROM ships WHERE name_en LIKE @p AND record_name <> @ex ORDER BY name_en LIMIT 5";
        cmd.Parameters.AddWithValue("@p", shipNameEn + " %");
        cmd.Parameters.AddWithValue("@ex", excludeRecordName ?? "");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ships に name_en 列があるか (インデックス構築・マイグレーション前の古い DB には無い)
    private bool ShipsHasNameEn()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(ships)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), "name_en", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private string GetShipPorts(string shipRecordName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT port_name, item_type, size FROM ship_ports WHERE ship_record_name = @srn ORDER BY size DESC, item_type";
        cmd.Parameters.AddWithValue("@srn", shipRecordName);
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        while (reader.Read())
        {
            var portName = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var itemType = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var size = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            sb.AppendLine($"    ポート: {portName} | {itemType} (S{size})");
        }
        return sb.ToString();
    }

    public string SearchItems(string query, string? componentType = null)
    {
        using var cmd = _conn.CreateCommand();
        if (!string.IsNullOrEmpty(componentType))
        {
            cmd.CommandText = "SELECT record_name, name, item_type, size, grade, manufacturer, component_type, component_json FROM items WHERE (record_name LIKE @q OR name LIKE @q) AND component_type = @ct ORDER BY name LIMIT 30";
            cmd.Parameters.AddWithValue("@ct", componentType);
        }
        else
        {
            cmd.CommandText = "SELECT record_name, name, item_type, size, grade, manufacturer, component_type, component_json FROM items WHERE record_name LIKE @q OR name LIKE @q ORDER BY name LIMIT 30";
        }
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        LocReader? loc = null;
        while (reader.Read())
        {
            var recordName = reader.GetString(0);
            var name = reader.IsDBNull(1) ? reader.GetString(0) : reader.GetString(1);
            var itemType = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var size = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var grade = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var mfr = reader.IsDBNull(5) ? "" : reader.GetString(5);
            var compType = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var compJson = reader.IsDBNull(7) ? "" : reader.GetString(7);
            // 色違い (と同じ名前の複製) は性能を基本品目と共用して空にしているので、基本品目のものを使う
            if (string.IsNullOrEmpty(compJson))
            {
                loc ??= new LocReader(_translationsDbPath);
                compJson = BaseComponentJson(recordName, loc);
            }

            sb.Append($"- {name} | タイプ: {itemType} | S{size} | Grade {grade}");
            if (!string.IsNullOrEmpty(mfr)) sb.Append($" | {mfr}");

            if (!string.IsNullOrEmpty(compJson))
            {
                var extras = ParseComponentExtras(compType, compJson);
                if (!string.IsNullOrEmpty(extras)) sb.Append($" | {extras}");
            }
            sb.AppendLine();
            count++;
        }
        loc?.Dispose();

        return count > 0 ? $"=== ゲームデータ (DB): アイテム ===\n{sb}" : "";
    }

    // 武器 1 件のダメージの種類のキー (Physical / Energy など)。［船舶戦闘］の「ダメージの種類」と同じ判定 (WeaponDamageTypes:
    // 弾の直撃 + 爆発、弾が無ければビーム)。record 名は "EntityClassDefinition." の有無どちらでもよい。
    // 色違いで性能の JSON を空にしてある品目は、色違いの表 (item_variants) の基本品目の JSON で判定する。読めなければ空
    public List<string> WeaponDamageTypesOf(string recordName)
    {
        const string prefix = "EntityClassDefinition.";
        var rn = (recordName ?? "").Trim();
        if (rn.Length == 0) return new List<string>();
        if (!rn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rn = prefix + rn;
        (string Component, string Raw)? Read(string record)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(component_json, ''), COALESCE(raw_json, ''), record_name FROM items WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
                cmd.Parameters.AddWithValue("@rn", record);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                rn = r.GetString(2);
                return (r.GetString(0), r.GetString(1));
            }
            catch { return null; }
        }
        var row = Read(rn);
        if (row == null) return new List<string>();
        if (row.Value.Component.Length == 0 && row.Value.Raw.Length == 0)
        {
            var (baseRecord, _) = VariantRow(rn);
            if (baseRecord != null) row = Read(baseRecord) ?? row;
        }
        var (component, raw) = row.Value;
        if (component.Length == 0 && raw.Length == 0) return new List<string>();
        return WeaponDamageTypes(ReadWeaponAmmo(component, raw), component);
    }

    // 所持コンポーネントの表の「性能」の欄。種類 (itemType = my_components.item_type) ごとに 1 つの代表の値を「項目名 値」で返す。
    // 値は record の基本値 (品質による増減は入れない)。record 名は "EntityClassDefinition." の有無どちらでもよい。
    // 色違いで性能の JSON を空にしてある品目は、色違いの表 (item_variants) の基本品目の JSON で補う。値が無い・読めなければ ""
    //   PowerPlant:      出力 = ItemResourceComponentParams の Online の ItemResourceDeltaGeneration の generation (Power) の量 (［比べる］の「出力 (パワー)」と同じ)
    //   Cooler:          冷却 = ItemResourceComponentParams の Online の generation (Coolant) の量 (［比べる］の「冷却 (クーラント/秒)」と同じ)
    //   Shield:          HP = SCItemShieldGeneratorParams.MaxShieldHealth (［比べる］の「シールド容量」と同じ)
    //   QuantumDrive:    速度 = SCItemQuantumDriveParams.params.driveSpeed (m/秒) を km/秒 にしたもの (［比べる］の「速度 (km/秒)」と同じ)
    //   Radar:           感度 = SCItemRadarComponentParams.signatureDetection の 1 つ目の sensitivity
    //   WeaponGun:       Alpha = 1 回撃ったときの合計ダメージ = 1発のダメージ × 1回の弾数 (［船舶戦闘］の Damage/Alpha と同じ。WeaponAlpha)。
    //                    1回の弾数が 2 以上なら「(1発 X × N発)」を付ける。弾が無くビームなら毎秒のダメージ (ビーム DPS)
    //   MissileLauncher: 搭載 = SItemPortContainerComponentParams.Ports のうち Missile を受け付けるポートのサイズと数 (例 S2×4)
    //   Missile / Bomb:  ダメージ = SCItemMissileParams / SCItemBombParams の explosionParams.damage の種類の合計
    // タレット・ジンバル/マウントは EquipmentService.ComponentPerfLabel が item_ports から作る (ここでは "")
    public string ComponentPerfOf(string recordName, string itemType)
    {
        const string prefix = "EntityClassDefinition.";
        var rn = (recordName ?? "").Trim();
        if (rn.Length == 0) return "";
        if (!rn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rn = prefix + rn;
        (string Component, string Raw)? Read(string record)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(component_json, ''), COALESCE(raw_json, ''), record_name FROM items WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
                cmd.Parameters.AddWithValue("@rn", record);
                using var r = cmd.ExecuteReader();
                if (!r.Read()) return null;
                return (r.GetString(0), r.GetString(1));
            }
            catch { return null; }
        }
        var row = Read(rn);
        if (row == null) return "";
        var (component, raw) = row.Value;
        if (component.Length == 0 || raw.Length == 0)
        {
            // 色違いは基本品目の JSON で補う (空の方だけ)
            var (baseRecord, _) = VariantRow(rn);
            if (baseRecord != null && Read(baseRecord) is { } b)
            {
                if (component.Length == 0) component = b.Component;
                if (raw.Length == 0) raw = b.Raw;
            }
        }
        if (component.Length == 0 && raw.Length == 0) return "";

        JsonDocument? doc = null;
        try { if (raw.Length > 0) doc = JsonDocument.Parse(raw); } catch { }
        try
        {
            var comps = new List<JsonElement>();
            if (doc != null && doc.RootElement.TryGetProperty("_RecordValue_", out var rv)
                && rv.TryGetProperty("Components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                comps.AddRange(cs.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object));
            JsonElement? Comp(string type) => comps.Cast<JsonElement?>().FirstOrDefault(c => c!.Value.TryGetProperty("_Type_", out var t) && t.GetString() == type);

            switch (itemType)
            {
                case "PowerPlant":
                    if (Comp("ItemResourceComponentParams") is { } pr && FindResourceAmount(pr, "ItemResourceDeltaGeneration", "Power") is { } pw)
                        return $"出力 {pw:#,0.##}";
                    break;
                case "Cooler":
                    if (Comp("ItemResourceComponentParams") is { } cr && FindResourceAmount(cr, null, "Coolant") is { } cl)
                        return $"冷却 {cl:#,0.##}";
                    break;
                case "Shield":
                    if (Comp("SCItemShieldGeneratorParams") is { } sh && TryNum(sh, "MaxShieldHealth", out var hp))
                        return $"HP {hp:#,0}";
                    break;
                case "QuantumDrive":
                    if (Comp("SCItemQuantumDriveParams") is { } qd && qd.TryGetProperty("params", out var qp) && TryNum(qp, "driveSpeed", out var sp))
                        return $"速度 {sp / 1000:#,0} km/秒";
                    break;
                case "Radar":
                    if (Comp("SCItemRadarComponentParams") is { } rd && rd.TryGetProperty("signatureDetection", out var sd)
                        && sd.ValueKind == JsonValueKind.Array && sd.GetArrayLength() > 0 && TryNum(sd[0], "sensitivity", out var sens))
                        return $"感度 {sens:0.##}";
                    break;
                case "WeaponGun":
                {
                    var ammo = ReadWeaponAmmo(component, raw);
                    if (ammo != null && ammo.Total > 0)
                    {
                        var pellets = PrimaryPellets(ReadFireRates(component));
                        return $"Alpha {WeaponAlpha(ammo.Total, pellets):#,0.##}" + (pellets >= 2 ? $" (1発 {ammo.Total:#,0.##} × {pellets}発)" : "");
                    }
                    var beams = ReadBeamDamage(component);
                    if (beams.Count > 0) return $"ビーム DPS {beams.Max(x => x.Total):#,0.##}";
                    break;
                }
                case "MissileLauncher":
                    if (Comp("SItemPortContainerComponentParams") is { } pc && pc.TryGetProperty("Ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                    {
                        var sizes = new List<(int Min, int Max)>();
                        foreach (var port in ports.EnumerateArray())
                        {
                            if (port.ValueKind != JsonValueKind.Object || !port.TryGetProperty("Types", out var types) || types.ValueKind != JsonValueKind.Array) continue;
                            var missile = types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("Type", out var tt)
                                && string.Equals(tt.GetString(), "Missile", StringComparison.OrdinalIgnoreCase));
                            if (!missile) continue;
                            TryNum(port, "MinSize", out var mn);
                            TryNum(port, "MaxSize", out var mx);
                            sizes.Add(((int)mn, (int)mx));
                        }
                        if (sizes.Count > 0)
                            return "搭載 " + string.Join(" + ", sizes.GroupBy(x => x).OrderByDescending(g => g.Key.Max)
                                .Select(g => $"{PortSizeText(g.Key.Min, g.Key.Max)}×{g.Count()}"));
                    }
                    break;
                case "Missile":
                case "Bomb":
                    if ((Comp("SCItemMissileParams") ?? Comp("SCItemBombParams")) is { } ms && ms.TryGetProperty("explosionParams", out var ep)
                        && ep.TryGetProperty("damage", out var dmg) && dmg.ValueKind == JsonValueKind.Object)
                    {
                        double total = 0;
                        foreach (var d in dmg.EnumerateObject())
                            if (d.Value.ValueKind == JsonValueKind.Number && d.Value.TryGetDouble(out var v)) total += v;
                        if (total > 0) return $"ダメージ {total:#,0.##}";
                    }
                    break;
            }
        }
        catch { }
        finally { doc?.Dispose(); }
        return "";
    }

    // Damage/Alpha = 1 回撃ったときの合計ダメージ = 1発のダメージ × 1回の弾数 (散弾などで弾数が 2 以上のときだけ 1発と違う)
    internal static double WeaponAlpha(double perShot, int pellets) => perShot * Math.Max(1, pellets);

    // ポートのサイズの表記。最小と最大が同じ (または最小が無い) なら「S3」、違えば「S2-3」
    public static string PortSizeText(int min, int max) =>
        max <= 0 ? "" : min > 0 && min != max ? $"S{min}-{max}" : $"S{max}";

    // 色違い (と同じ名前の複製) の基本品目の component_json。基本品目が無い・空なら ""
    private string BaseComponentJson(string recordName, LocReader loc)
    {
        var (baseRecord, _) = VariantRow(recordName);
        if (baseRecord == null)
            foreach (var g in SameNameRecords(recordName, loc))
            {
                (baseRecord, _) = VariantRow(g);
                if (baseRecord != null) break;
            }
        if (baseRecord == null) return "";
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(component_json, '') FROM items WHERE record_name = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", baseRecord);
            return cmd.ExecuteScalar() as string ?? "";
        }
        catch { return ""; }
    }

    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer)> SearchItemIndex(string query)
    {
        var results = LikeSearch(query);
        if (results.Count > 0) return results;

        var segments = SplitOnParticles(query);
        foreach (var seg in segments)
        {
            if (seg.Length < 2) continue;
            results = LikeSearch(seg);
            if (results.Count > 0) return results;
        }

        var ascii = StripToAlphaNum(query);
        if (ascii.Length >= 2)
            return SearchItemIndexStripped(ascii);

        return results;
    }

    private List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer)> LikeSearch(string query)
    {
        var results = new List<(string, string, string, string, string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, record_name, name, item_type, sub_type, manufacturer FROM item_index WHERE name LIKE @q OR name_ja LIKE @q OR record_name LIKE @q ORDER BY name LIMIT 30";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5)
            ));
        }
        return results;
    }

    private static string[] SplitOnParticles(string query)
    {
        return System.Text.RegularExpressions.Regex.Split(query,
                "について|での|とは|では|から|まで|です|ます|って|いくら|[のはがをにでともか？?！!]")
            .Select(s => s.Trim())
            .Where(s => s.Length >= 2)
            .OrderByDescending(s => s.Length)
            .ToArray();
    }


    private List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer)> SearchItemIndexStripped(string strippedQuery)
    {
        var results = new List<(string, string, string, string, string, string)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, record_name, name, item_type, sub_type, manufacturer FROM item_index";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var recordName = reader.GetString(1);
            if (StripToAlphaNum(name).Contains(strippedQuery, StringComparison.OrdinalIgnoreCase)
                || StripToAlphaNum(recordName).Contains(strippedQuery, StringComparison.OrdinalIgnoreCase))
            {
                results.Add((
                    reader.GetString(0), recordName, name,
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5)
                ));
                if (results.Count >= 30) break;
            }
        }
        return results;
    }

    public string? GetUuidByName(string name)
    {
        var results = SearchItemIndex(name);
        return results.Count > 0 ? results[0].uuid : null;
    }

    public int GetItemIndexCount()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM item_index";
        try { return (int)(long)(cmd.ExecuteScalar() ?? 0L); }
        catch { return 0; }
    }

    public bool HasFts5()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='item_index_fts'";
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    public bool HasVectorIndex()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM item_vectors";
            return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score)> FuzzySearch(string query, int limit = 20)
    {
        var results = new List<(string, string, string, string, string, string, double)>();

        if (HasFts5())
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                var ftsQuery = EscapeFts5Query(query);
                cmd.CommandText = @"SELECT i.uuid, i.record_name, i.name, i.item_type, i.sub_type, i.manufacturer, rank
                    FROM item_index_fts f JOIN item_index i ON f.uuid = i.uuid
                    WHERE item_index_fts MATCH @q
                    ORDER BY rank LIMIT @lim";
                cmd.Parameters.AddWithValue("@q", ftsQuery);
                cmd.Parameters.AddWithValue("@lim", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    results.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3),
                        reader.IsDBNull(4) ? "" : reader.GetString(4),
                        reader.IsDBNull(5) ? "" : reader.GetString(5),
                        reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6)
                    ));
                }
                if (results.Count > 0) return results;
            }
            catch { }
        }

        var likeResults = SearchItemIndex(query);
        return likeResults.Select(r => (r.uuid, r.recordName, r.name, r.itemType, r.subType, r.manufacturer, 0.0)).ToList();
    }

    // 綴りの揺れ用のあいまい検索 (例 "ironcrad" → "Drake Ironclad")。
    // 検索語全体と、item_index の名前 (name と name_ja) の「全体」または「検索語と同じ語数の連続した語」を編集距離 (Damerau-Levenshtein の OSA 版。
    // 挿入・削除・置換・隣接 2 文字の入れ替えをそれぞれ 1 とする) で比べ、類似度 = 1 - 距離 / 長い方の長さ の最大値が minSimilarity 以上のものを
    // 類似度の高い順に返す。同点は名前全体との類似度 (WholeNameSimilarity。name と name_ja の高い方) の高い順、それも同点は名前順。検索語を断片に分けて照合することはしない (検索語の一部だけが名前に含まれても当たらない)
    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score)> EditDistanceSearch(string query, int limit = 10, double minSimilarity = FuzzyMinSimilarity)
    {
        var q = NormalizeForFuzzy(query);
        if (q.Length == 0) return [];
        var results = new List<((string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double score) item, double whole)>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT uuid, record_name, name, item_type, sub_type, manufacturer, name_ja FROM item_index WHERE name IS NOT NULL AND name != ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(2);
            var nameJa = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var sim = Math.Max(NameSimilarity(q, name, minSimilarity), NameSimilarity(q, nameJa, minSimilarity));
            if (sim < minSimilarity) continue;
            var whole = Math.Max(WholeNameSimilarity(q, name), WholeNameSimilarity(q, nameJa));
            results.Add(((reader.GetString(0), reader.GetString(1), name,
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? "" : reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5), sim), whole));
        }
        return results
            .OrderByDescending(r => r.item.score).ThenByDescending(r => r.whole).ThenBy(r => r.item.name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(r => r.item)
            .ToList();
    }

    // 検索語と名前全体 (区切りを空白 1 つにまとめたもの) の編集距離の類似度 = 1 - 距離 / 長い方の長さ。名前が空なら 0
    public static double WholeNameSimilarity(string normalizedQuery, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || normalizedQuery.Length == 0) return 0;
        var whole = NormalizeForFuzzy(name);
        if (whole.Length == 0) return 0;
        return 1.0 - (double)OsaDistance(normalizedQuery, whole) / Math.Max(whole.Length, normalizedQuery.Length);
    }

    // EditDistanceSearch の採用下限 (類似度)。0.8 では 5 文字の語に距離 1 までを拾い、3〜4 文字の語の距離 1 (類似度 0.67〜0.75) は拾わない
    public const double FuzzyMinSimilarity = 0.8;

    // 検索語と名前の類似度 (0〜1)。名前全体と、名前を区切り文字で分けた語のうち検索語と同じ語数の連続した語を候補にし、最も近いものを採る。
    // floor より明らかに届かない候補 (長さの差だけで下回るもの) は計算を省く。名前が空なら 0
    public static double NameSimilarity(string normalizedQuery, string name, double floor = 0)
    {
        if (string.IsNullOrWhiteSpace(name) || normalizedQuery.Length == 0) return 0;
        var whole = NormalizeForFuzzy(name);
        if (whole.Length == 0) return 0;
        var words = whole.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var qWords = normalizedQuery.Split(' ').Length;

        double best = 0;
        void Try(string candidate)
        {
            var maxLen = Math.Max(candidate.Length, normalizedQuery.Length);
            // 長さの差 = 距離の下限。これだけで floor と現在の最良を下回るなら計算しない
            var bound = 1.0 - (double)Math.Abs(candidate.Length - normalizedQuery.Length) / maxLen;
            if (bound < floor || bound <= best) return;
            var sim = 1.0 - (double)OsaDistance(normalizedQuery, candidate) / maxLen;
            if (sim > best) best = sim;
        }

        Try(whole);
        if (words.Length > qWords)
            for (int i = 0; i + qWords <= words.Length; i++)
                Try(string.Join(' ', words, i, qWords));
        return best;
    }

    // 区切り文字 (空白・括弧・ハイフン・スラッシュ・引用符・読点など)
    private static readonly char[] FuzzySeparators =
        " \t()（）[]［］{}「」『』【】<>＜＞\"“”'‘’-‐‑–—―－/／,，、。.:：;；!！?？~～・".ToCharArray();

    // 小文字化し、区切り文字を空白 1 つにまとめる
    public static string NormalizeForFuzzy(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return string.Join(' ', s.ToLowerInvariant().Split(FuzzySeparators, StringSplitOptions.RemoveEmptyEntries));
    }

    // Damerau-Levenshtein の OSA (optimal string alignment) 版の編集距離
    private static int OsaDistance(string a, string b)
    {
        int la = a.Length, lb = b.Length;
        if (la == 0) return lb;
        if (lb == 0) return la;
        var d = new int[la + 1, lb + 1];
        for (int i = 0; i <= la; i++) d[i, 0] = i;
        for (int j = 0; j <= lb; j++) d[0, j] = j;
        for (int i = 1; i <= la; i++)
        {
            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                d[i, j] = v;
            }
        }
        return d[la, lb];
    }

    // uuid → name_ja (カタカナ検索の候補の並べ替え用)
    public Dictionary<string, string> GetItemNamesJa(IEnumerable<string> uuids)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name_ja FROM item_index WHERE uuid = @u";
        var p = cmd.Parameters.Add("@u", SqliteType.Text);
        foreach (var u in uuids.Distinct())
        {
            p.Value = u;
            if (cmd.ExecuteScalar() is string ja && ja.Length > 0) result[u] = ja;
        }
        return result;
    }

    public List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double similarity)> SemanticSearch(float[] queryEmbedding, int limit = 10)
    {
        var results = new List<(string uuid, string recordName, string name, string itemType, string subType, string manufacturer, double similarity)>();
        if (!HasVectorIndex()) return results;

        var candidates = new List<(string uuid, float[] embedding)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT uuid, embedding FROM item_vectors";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var uuid = reader.GetString(0);
                var blob = (byte[])reader[1];
                candidates.Add((uuid, GameDataExtractor.BytesToFloats(blob)));
            }
        }

        var scored = candidates
            .Select(c => (c.uuid, similarity: CosineSimilarity(queryEmbedding, c.embedding)))
            .OrderByDescending(c => c.similarity)
            .Take(limit)
            .ToList();

        foreach (var (uuid, sim) in scored)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, name, item_type, sub_type, manufacturer FROM item_index WHERE uuid = @u";
            cmd.Parameters.AddWithValue("@u", uuid);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                results.Add((
                    uuid,
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    sim
                ));
            }
        }
        return results;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, magA = 0, magB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denom = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denom > 0 ? dot / denom : 0;
    }

    private static string StripToAlphaNum(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            var ch = c;
            if (ch >= '！' && ch <= '～') ch = (char)(ch - 0xFEE0);
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString();
    }

    private static string EscapeFts5Query(string query)
    {
        var escaped = query.Replace("\"", "\"\"");
        return $"\"{escaped}\"";
    }

    private static string ParseComponentExtras(string compType, string compJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(compJson);
            var el = doc.RootElement;
            var parts = new List<string>();

            switch (compType)
            {
                case "SCItemShieldGeneratorParams":
                    if (el.TryGetProperty("MaxShieldHealth", out var mh)) parts.Add($"最大HP: {mh}");
                    if (el.TryGetProperty("MaxShieldRegen", out var mr)) parts.Add($"再生: {mr}");
                    break;
                case "SCItemQuantumDriveParams":
                    if (el.TryGetProperty("quantumFuelRequirement", out var qf)) parts.Add($"燃料: {qf}");
                    if (el.TryGetProperty("jumpRange", out var jr)) parts.Add($"距離: {jr}");
                    if (el.TryGetProperty("spoolUpTime", out var su)) parts.Add($"スプール: {su}");
                    break;
                case "SCItemWeaponComponentParams":
                    if (el.TryGetProperty("fireRate", out var fr)) parts.Add($"発射速度: {fr}");
                    break;
                case "SCItemPowerPlantParams":
                    if (el.TryGetProperty("PowerOutput", out var po)) parts.Add($"出力: {po}");
                    break;
                case "SCItemCoolerParams":
                    if (el.TryGetProperty("CoolingRate", out var cr)) parts.Add($"冷却: {cr}");
                    break;
            }

            return string.Join(" | ", parts);
        }
        catch { return ""; }
    }

    public string SearchMissions(string? keyword = null, string? missionType = null)
    {
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string>();
        if (!string.IsNullOrEmpty(keyword))
        {
            // ContractGenerator 行は record_name が契約 GUID なので、raw_json の "_Name_" (契約 debugName → 生成器 debugName) でも当てる
            // (CASE で CG 行以外では json_extract を評価しない)
            conditions.Add($"(record_name LIKE @q OR title LIKE @q OR title_hud LIKE @q OR mission_type LIKE @q OR mission_giver LIKE @q OR description LIKE @q OR CASE WHEN record_name LIKE '{ContractRecordPrefix}%' THEN json_extract(raw_json, '$._Name_') END LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{keyword}%");
        }
        if (!string.IsNullOrEmpty(missionType))
        {
            conditions.Add("mission_type LIKE @mt");
            cmd.Parameters.AddWithValue("@mt", $"%{missionType}%");
        }

        var where = conditions.Count > 0 ? $"WHERE {string.Join(" AND ", conditions)}" : "";
        // 13 列目: ContractGenerator 行だけ raw_json (表示名に使う debugName を読む)。他の行は NULL
        cmd.CommandText = $"SELECT record_name, title, title_hud, mission_type, difficulty, mission_giver, location_label, description, reward_min, reward_max, required_reputation, lawfulness_type, CASE WHEN record_name LIKE '{ContractRecordPrefix}%' THEN raw_json END FROM missions {where} ORDER BY reward_max DESC LIMIT 30";
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var rn = reader.GetString(0);
            var title = CleanLocKey(reader.IsDBNull(1) ? "" : reader.GetString(1));
            var titleHud = CleanLocKey(reader.IsDBNull(2) ? "" : reader.GetString(2));
            var mType = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var diff = reader.IsDBNull(4) ? "" : reader.GetString(4);
            var giver = CleanLocKey(reader.IsDBNull(5) ? "" : reader.GetString(5));
            var loc = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var desc = CleanLocKey(reader.IsDBNull(7) ? "" : reader.GetString(7));
            var rMin = reader.IsDBNull(8) ? 0.0 : reader.GetDouble(8);
            var rMax = reader.IsDBNull(9) ? 0.0 : reader.GetDouble(9);
            var rep = reader.IsDBNull(10) ? "" : reader.GetString(10);
            var law = reader.IsDBNull(11) ? "" : reader.GetString(11);

            var displayTitle = IsLocKey(title) ? "" : title;
            string friendlyName;
            // 難易度・場所の推測に使う名前 (ContractGenerator 行は GUID ではなく debugName。抽出側と同じくタイトルキーでは推測しない)
            var inferName = rn;
            if (rn.StartsWith(ContractRecordPrefix))
            {
                // ContractGenerator 行 (record_name は契約 GUID): 表示名はタイトル、解決できなければ 契約 debugName → 生成器 debugName → タイトルキー
                var contractRaw = reader.IsDBNull(12) ? "" : reader.GetString(12);
                var (debugName, nameBase, _) = GameDataExtractor.ReadContractDisplayNames(contractRaw);
                friendlyName = !string.IsNullOrEmpty(displayTitle) ? displayTitle : nameBase;
                displayTitle = "";   // 表示名と同じタイトルを重ねて出さない
                inferName = debugName;
                // 合法性: lawfulness_type 列は空なので raw_json の _Resolved_.illegal から出す
                law = ReadContractLawfulness(contractRaw);
            }
            else
            {
                friendlyName = ExtractFriendlyName(rn);
            }
            sb.Append($"- {friendlyName}");
            if (!string.IsNullOrEmpty(displayTitle)) sb.Append($" | タイトル: {displayTitle}");
            if (!string.IsNullOrEmpty(mType)) sb.Append($" | 種別: {mType}");
            if (!string.IsNullOrEmpty(diff)) sb.Append($" | 難易度: {diff}");
            if (string.IsNullOrEmpty(diff))
            {
                var inferred = InferDifficultyFromName(inferName);
                if (!string.IsNullOrEmpty(inferred)) sb.Append($" | 難易度: {inferred}");
            }
            if (!string.IsNullOrEmpty(giver) && !IsLocKey(giver)) sb.Append($" | 依頼者: {giver}");
            if (rMin > 0 || rMax > 0)
            {
                if (rMin > 0 && rMax > 0 && rMin != rMax) sb.Append($" | 報酬: {rMin:0}-{rMax:0} aUEC");
                else sb.Append($" | 報酬: {Math.Max(rMin, rMax):0} aUEC");
            }
            if (!string.IsNullOrEmpty(rep)) sb.Append($" | 必要評判: {rep}");
            if (!string.IsNullOrEmpty(law)) sb.Append($" | 合法性: {law}");
            if (!string.IsNullOrEmpty(loc)) sb.Append($" | 場所: {loc}");
            var inferredLoc = InferLocationFromName(inferName);
            if (!string.IsNullOrEmpty(inferredLoc) && string.IsNullOrEmpty(loc)) sb.Append($" | 場所: {inferredLoc}");
            sb.AppendLine();

            if (!string.IsNullOrEmpty(desc) && !IsLocKey(desc))
            {
                var truncDesc = desc.Length > 120 ? desc[..120] + "..." : desc;
                sb.AppendLine($"  概要: {truncDesc}");
            }
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): ミッション/契約 ({count}件) ===\n{sb}" : "";
    }

    public string SearchCommodities(string? name = null)
    {
        using var cmd = _conn.CreateCommand();
        if (!string.IsNullOrEmpty(name))
        {
            cmd.CommandText = "SELECT record_name, name, symbol, volatility FROM commodities WHERE record_name LIKE @q OR name LIKE @q ORDER BY name LIMIT 30";
            cmd.Parameters.AddWithValue("@q", $"%{name}%");
        }
        else
        {
            cmd.CommandText = "SELECT record_name, name, symbol, volatility FROM commodities ORDER BY name LIMIT 50";
        }
        using var reader = cmd.ExecuteReader();

        var sb = new StringBuilder();
        int count = 0;
        while (reader.Read())
        {
            var rn = reader.GetString(0);
            var n = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var sym = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var vol = reader.IsDBNull(3) ? "" : reader.GetString(3);

            sb.Append($"- {(!string.IsNullOrEmpty(n) ? n : rn)}");
            if (!string.IsNullOrEmpty(sym)) sb.Append($" ({sym})");
            if (!string.IsNullOrEmpty(vol)) sb.Append($" | 変動性: {vol}");
            sb.AppendLine();
            count++;
        }

        return count > 0 ? $"=== ゲームデータ (DB): コモディティ ({count}件) ===\n{sb}" : "";
    }

    private static string CleanLocKey(string val)
    {
        if (string.IsNullOrEmpty(val)) return val;
        return val.StartsWith('@') ? val[1..] : val;
    }

    private static bool IsLocKey(string val)
    {
        if (string.IsNullOrEmpty(val)) return true;
        if (val.StartsWith('@')) return true;
        if (val.Contains('_') && !val.Contains(' ') && val.Length > 10) return true;
        return false;
    }

    // ContractGenerator 行の record_name の接頭辞 (GameDataExtractor.ContractRecordPrefix と同じ)
    private const string ContractRecordPrefix = GameDataExtractor.ContractRecordPrefix;

    // ContractGenerator 行の raw_json の _Resolved_.illegal (契約 → 生成器 → テンプレート) から 合法 / 非合法 / 不明 を返す
    private static string ReadContractLawfulness(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson)) return "不明";
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("_Resolved_", out var res) && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("illegal", out var il))
            {
                if (il.ValueKind == JsonValueKind.False) return "合法";
                if (il.ValueKind == JsonValueKind.True) return "非合法";
            }
        }
        catch { }
        return "不明";
    }

    private static string ExtractFriendlyName(string recordName)
    {
        var name = recordName.Replace("MissionBrokerEntry.", "");
        name = name.Replace("PU_", "");
        name = name.Replace("_", " ");
        return name;
    }

    private static string InferDifficultyFromName(string rn)
    {
        if (rn.Contains("_Intro", StringComparison.OrdinalIgnoreCase)) return "Intro";
        if (rn.Contains("_VeryEasy", StringComparison.OrdinalIgnoreCase)) return "Very Easy";
        if (rn.Contains("_Easy", StringComparison.OrdinalIgnoreCase)) return "Easy";
        if (rn.Contains("_Medium", StringComparison.OrdinalIgnoreCase)) return "Medium";
        if (rn.Contains("_Hard", StringComparison.OrdinalIgnoreCase)) return "Hard";
        if (rn.Contains("_VeryHard", StringComparison.OrdinalIgnoreCase)) return "Very Hard";
        return "";
    }

    private static string InferLocationFromName(string rn)
    {
        if (rn.Contains("Stanton1", StringComparison.OrdinalIgnoreCase)) return "Hurston";
        if (rn.Contains("Stanton2", StringComparison.OrdinalIgnoreCase)) return "Crusader";
        if (rn.Contains("Stanton3", StringComparison.OrdinalIgnoreCase)) return "ArcCorp";
        if (rn.Contains("Stanton4", StringComparison.OrdinalIgnoreCase)) return "microTech";
        if (rn.Contains("Pyro", StringComparison.OrdinalIgnoreCase)) return "Pyro";
        return "";
    }

    // === Knowledge (memory) ===

    /// <summary>Domain keywords for structured knowledge retrieval.</summary>
    private static readonly Dictionary<string, string[]> DomainKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ship"]      = new[] { "ship", "船", "艦", "vehicle", "fighter", "bomber", "hauler", "transport", "mining ship", "gunship",
                                "constellation", "cutlass", "freelancer", "carrack", "reclaimer", "caterpillar", "hammerhead", "retaliator",
                                "hornet", "gladius", "arrow", "sabre", "vanguard", "eclipse", "herald", "hull", "prospector", "mole",
                                "aurora", "mustang", "avenger", "pisces", "spirit", "corsair", "scorpius", "redeemer", "starfarer",
                                "merchantman", "perseus", "polaris", "idris", "javelin", "vulture", "expanse", "starlancer",
                                "raft", "nomad", "100i", "300i", "400i", "600i", "890", "railen", "san'tok", "khartu" },
        ["location"]  = new[] { "location", "station", "ステーション", "都市", "基地", "拠点", "ゲートウェイ", "gateway",
                                "orison", "lorville", "area18", "babbage", "grimhex", "levski", "pyro", "nyx", "stanton",
                                "hurston", "crusader", "arccorp", "microtech", "aberdeen", "daymar", "cellin", "yela",
                                "delamar", "port", "seraphim", "everus", "baijini", "cru-l", "arc-l", "hur-l", "mic-l",
                                "ruin", "checkmate", "bloom", "terra gate", "pyro gateway", "bloom" },
        ["mission"]   = new[] { "mission", "ミッション", "contract", "契約", "bounty", "賞金", "delivery", "配送", "salvage", "サルベージ",
                                "mining", "採掘", "cargo", "patrol", "bunker", "バンカー", "illegal", "smuggle", "rescue", "investigation" },
        ["commodity"] = new[] { "commodity", "商品", "コモディティ", "trade", "貿易", "取引", "cargo", "資源", "鉱石", "ore",
                                "laranite", "quantanium", "agricium", "titanium", "diamond", "corundum", "gold",
                                "stims", "distilled", "medical", "scrap", "waste", "hydrogen", "astatine" },
        ["combat"]    = new[] { "combat", "戦闘", "weapon", "武器", "gun", "銃", "missile", "ミサイル", "shield", "シールド",
                                "armor", "アーマー", "fps", "pvp", "pve", "turret", "タレット", "ammo", "弾薬" },
        ["equipment"] = new[] { "equipment", "装備", "component", "コンポーネント", "cooler", "クーラー", "quantum", "クォンタム",
                                "power plant", "パワープラント", "module", "モジュール", "undersuit", "アンダースーツ",
                                "backpack", "helmet", "ヘルメット", "thruster", "avionics", "radar", "scanner" },
        ["system"]    = new[] { "system", "星系", "stanton", "pyro", "nyx", "sol", "terra", "version", "バージョン", "alpha", "patch",
                                "wipe", "ワイプ", "update", "アップデート", "実装", "roadmap" },
    };

    /// <summary>Extract matching domain tags from user question.</summary>
    public static List<string> ExtractDomains(string question)
    {
        var q = question.ToLowerInvariant();
        var matched = new List<string>();
        foreach (var (domain, keywords) in DomainKeywords)
        {
            if (keywords.Any(kw => q.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                matched.Add(domain);
        }
        return matched;
    }

    /// <summary>Add knowledge with duplicate prevention. Returns existing ID if duplicate found.</summary>
    public (int id, bool isDuplicate) AddKnowledgeSafe(string content, string category = "general")
    {
        // Duplicate check: same category + content similarity (80%+ overlap)
        using var checkCmd = _conn.CreateCommand();
        checkCmd.CommandText = "SELECT id, content FROM knowledge WHERE category = @cat";
        checkCmd.Parameters.AddWithValue("@cat", category);
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            var existingContent = reader.GetString(1);
            if (IsSimilar(content, existingContent))
                return (reader.GetInt32(0), true);
        }
        reader.Close();

        var id = AddKnowledge(content, category);
        return (id, false);
    }

    private static bool IsSimilar(string a, string b)
    {
        if (a == b) return true;
        // Normalize and compare — if one contains the other, or >70% word overlap
        var aNorm = a.Trim().ToLowerInvariant();
        var bNorm = b.Trim().ToLowerInvariant();
        if (aNorm.Contains(bNorm) || bNorm.Contains(aNorm)) return true;

        var aWords = aNorm.Split(' ', '　', '、', '。', ',', '.', '/', '（', '）', '(', ')').Where(w => w.Length > 1).ToHashSet();
        var bWords = bNorm.Split(' ', '　', '、', '。', ',', '.', '/', '（', '）', '(', ')').Where(w => w.Length > 1).ToHashSet();
        if (aWords.Count == 0 || bWords.Count == 0) return false;
        var overlap = aWords.Intersect(bWords).Count();
        var maxLen = Math.Max(aWords.Count, bWords.Count);
        return (double)overlap / maxLen >= 0.7;
    }

    public int AddKnowledge(string content, string category = "general")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO knowledge(category, content, created_at) VALUES(@cat, @content, @at)";
        cmd.Parameters.AddWithValue("@cat", category);
        cmd.Parameters.AddWithValue("@content", content);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT last_insert_rowid()";
        return (int)(long)cmd.ExecuteScalar()!;
    }

    /// <summary>Search knowledge by keywords (OR match). Returns entries where content matches any keyword.</summary>
    public List<(int id, string category, string content, DateTime createdAt)> SearchKnowledge(IEnumerable<string> keywords)
    {
        var kwList = keywords.Where(k => k.Length >= 2).ToList();
        if (kwList.Count == 0) return GetAllKnowledge();

        var results = new List<(int, string, string, DateTime)>();
        using var cmd = _conn.CreateCommand();
        var conditions = new List<string>();
        for (int i = 0; i < kwList.Count; i++)
        {
            conditions.Add($"content LIKE @kw{i}");
            cmd.Parameters.AddWithValue($"@kw{i}", $"%{kwList[i]}%");
        }
        cmd.CommandText = $"SELECT id, category, content, created_at FROM knowledge WHERE {string.Join(" OR ", conditions)} ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime() : DateTime.Now;
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), dt));
        }
        return results;
    }

    /// <summary>Get knowledge filtered by category.</summary>
    public List<(int id, string category, string content, DateTime createdAt)> GetKnowledgeByCategory(string category)
    {
        var results = new List<(int, string, string, DateTime)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, category, content, created_at FROM knowledge WHERE category = @cat ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@cat", category);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime() : DateTime.Now;
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), dt));
        }
        return results;
    }

    public int DeleteKnowledge(string query)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM knowledge WHERE content LIKE @q";
        cmd.Parameters.AddWithValue("@q", $"%{query}%");
        return cmd.ExecuteNonQuery();
    }

    public List<(int id, string category, string content, DateTime createdAt)> GetAllKnowledge()
    {
        var results = new List<(int, string, string, DateTime)>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, category, content, created_at FROM knowledge ORDER BY created_at DESC";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dt = DateTime.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToLocalTime() : DateTime.Now;
            results.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2), dt));
        }
        return results;
    }

    public int DeleteKnowledgeByIds(IEnumerable<int> ids)
    {
        var idList = string.Join(",", ids);
        if (string.IsNullOrEmpty(idList)) return 0;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM knowledge WHERE id IN ({idList})";
        return cmd.ExecuteNonQuery();
    }

    // ===== 種類ごとの比較 (チャットの compare_items) =====

    // 比較の種類。性能の取り方と表の列を決める
    public enum CompareKind { PersonalWeapon, ShipWeapon, Shield, PowerPlant, Cooler, QuantumDrive, Radar, Armor, Generic, Ship }

    // 比較できる種類の定義。
    // Filters: item_index の絞り込み。"item_type" / "item_type/sub_type" / "前方一致*" のいずれか。
    // RecordToken: 個人武器の種類。record_name の 2 語目 (例 lbco_sniper_energy_01 → sniper)。
    // ShipRoleLike / ShipCareerLike: 船の ships.role / ships.career のキーに含まれる語 (例 @vehicle_class_mediumfreight → freight)。
    // GroupVariants: 基本モデルごとにまとめ、塗装違い・名前付き版を下に並べるか (個人装備のみ)。
    // UexCategoryIds: 店の販売データを UEX の items_prices から引くときのカテゴリ。
    // NoDataReason: DB に該当する品目が無い種類 (受け付けて「データ無し」と返す)
    // PartSummary: 部位を指定しない種類 (アーマー全体・服)。部位 (item_type) ごとの件数と上位だけの要約を返す。
    // (一覧が CompareTextLimit 文字を超えたときの要約は、すべての種類で行う)
    // Aliases のうち「=」で始まるものは、入力を語に区切ったときに語として一致したときだけ当てる (例 =hat。what / chat には当てない)
    public record CompareCategory(string Label, CompareKind Kind, string[] Aliases, string[] Filters,
        string? RecordToken = null, string? ShipRoleLike = null, string? ShipCareerLike = null,
        bool GroupVariants = false, int[]? UexCategoryIds = null, string? NoDataReason = null,
        bool PartSummary = false, string? ShipWeaponKind = null);
    // ShipWeaponKind: 船の武器の種類 (表示名と record 名の語から決める。ShipWeaponKindOf)。指定があればその種類だけにする

    private static readonly string[] PersonalAttachmentFilters =
        ["WeaponAttachment/IronSight", "WeaponAttachment/BottomAttachment", "WeaponAttachment/Magazine", "WeaponAttachment/Utility", "WeaponAttachment/Missile"];
    private static readonly string[] ArmorPieceFilters = ["Char_Armor_Helmet", "Char_Armor_Torso", "Char_Armor_Arms", "Char_Armor_Legs", "Char_Armor_Undersuit", "Char_Armor_Backpack"];
    private static readonly int[] ClothingUexCategoryIds = [8, 9, 10, 11, 12, 13, 14, 15];

    // 部位 (item_type) の表示名。部位ごとの要約の見出しと並び順に使う
    private static readonly (string ItemType, string Label)[] PartLabels =
    [
        ("Char_Armor_Helmet", "ヘルメット"), ("Char_Armor_Torso", "胴"), ("Char_Armor_Arms", "腕"), ("Char_Armor_Legs", "脚"),
        ("Char_Armor_Undersuit", "アンダースーツ"), ("Char_Armor_Backpack", "バックパック"),
        ("Char_Clothing_Torso_1", "上着"), ("Char_Clothing_Torso_0", "シャツ"), ("Char_Clothing_Torso_2", "ベルト・制服の腰回り"),
        ("Char_Clothing_Legs", "ズボン"), ("Char_Clothing_Feet", "靴"), ("Char_Clothing_Hands", "手袋"), ("Char_Clothing_Hat", "帽子"),
        ("Char_Clothing_Backpack", "バックパック (服)"),
    ];

    // 比べるツールの出力の上限の目安 (文字数)。一覧がこれを超えたら要約にする
    private const int CompareTextLimit = 10000;
    private const string SpaceshipFilter = "NOITEM_Vehicle/Vehicle_Spaceship";
    // 船の武器 (ロケットポッド = sub_type Rocket を除く)。"item_type/!sub_type" は「その sub_type ではない」
    internal const string ShipGunFilter = "WeaponGun/!Rocket";

    public static readonly List<CompareCategory> CompareCategories =
    [
        // 個人武器 (item_index.item_type = WeaponPersonal)
        new("スナイパーライフル", CompareKind.PersonalWeapon, ["sniper rifle", "sniper", "スナイパーライフル", "スナイパー", "狙撃銃"], ["WeaponPersonal"], RecordToken: "sniper", GroupVariants: true, UexCategoryIds: [18]),
        new("サブマシンガン (SMG)", CompareKind.PersonalWeapon, ["smg", "submachine gun", "サブマシンガン", "短機関銃"], ["WeaponPersonal"], RecordToken: "smg", GroupVariants: true, UexCategoryIds: [18]),
        new("ピストル", CompareKind.PersonalWeapon, ["pistol", "handgun", "ピストル", "拳銃", "ハンドガン"], ["WeaponPersonal"], RecordToken: "pistol", GroupVariants: true, UexCategoryIds: [18]),
        new("ショットガン", CompareKind.PersonalWeapon, ["shotgun", "ショットガン", "散弾銃"], ["WeaponPersonal"], RecordToken: "shotgun", GroupVariants: true, UexCategoryIds: [18]),
        new("アサルトライフル", CompareKind.PersonalWeapon, ["assault rifle", "rifle", "アサルトライフル", "ライフル", "突撃銃"], ["WeaponPersonal"], RecordToken: "rifle", GroupVariants: true, UexCategoryIds: [18]),
        new("軽機関銃 (LMG)", CompareKind.PersonalWeapon, ["lmg", "light machine gun", "ライトマシンガン", "軽機関銃", "機関銃"], ["WeaponPersonal"], RecordToken: "lmg", GroupVariants: true, UexCategoryIds: [18]),
        new("重機関銃 (HMG)", CompareKind.PersonalWeapon, ["hmg", "heavy machine gun", "ヘビーマシンガン", "重機関銃"], ["WeaponPersonal"], RecordToken: "hmg", GroupVariants: true, UexCategoryIds: [18]),
        new("グレネードランチャー", CompareKind.PersonalWeapon, ["grenade launcher", "グレネードランチャー"], ["WeaponPersonal"], RecordToken: "glauncher", GroupVariants: true, UexCategoryIds: [18]),
        new("特殊武器 (レールガン・ミサイルランチャー)", CompareKind.PersonalWeapon, ["railgun", "missile launcher", "rocket launcher", "レールガン", "ミサイルランチャー", "ロケットランチャー"], ["WeaponPersonal"], RecordToken: "special", GroupVariants: true, UexCategoryIds: [18]),
        new("個人武器 (全種類)", CompareKind.PersonalWeapon, ["personal weapon", "fps weapon", "個人武器", "銃", "武器"], ["WeaponPersonal/Small", "WeaponPersonal/Medium", "WeaponPersonal/Large"], GroupVariants: true, UexCategoryIds: [18]),
        new("ナイフ", CompareKind.Generic, ["knife", "ナイフ", "近接武器"], ["WeaponPersonal/Knife"], GroupVariants: true, UexCategoryIds: [18]),
        new("手榴弾", CompareKind.Generic, ["grenade", "手榴弾", "グレネード"], ["WeaponPersonal/Grenade", "Grenade"], GroupVariants: true, UexCategoryIds: [18]),
        new("設置型の爆発物", CompareKind.Generic, ["explosive", "mine", "爆発物", "爆薬", "地雷"], ["FPS_Deployable"], GroupVariants: true, UexCategoryIds: [18]),
        new("道具 (マルチツール・トラクタービーム等)", CompareKind.Generic, ["multitool", "multi-tool", "tractor beam", "gadget", "rangefinder", "salvage tool", "マルチツール", "トラクタービーム", "ガジェット", "道具", "ツール", "双眼鏡", "距離計"], ["WeaponPersonal/Gadget", "Gadget"], GroupVariants: true, UexCategoryIds: [18, 28]),
        new("医療ガン", CompareKind.PersonalWeapon, ["medgun", "medical gun", "paramed", "医療ガン", "メディカルガン", "パラメド"], ["WeaponPersonal"], RecordToken: "medgun", GroupVariants: true, UexCategoryIds: [18, 28]),
        new("医療用具 (メディペン等)", CompareKind.Generic, ["medpen", "medical", "consumable", "メディペン", "医療用具", "回復薬", "医療"], ["FPS_Consumable"], GroupVariants: true, UexCategoryIds: [16, 69]),
        // 武器の付属品 (WeaponAttachment のうち個人武器用。Barrel / FiringMechanism / PowerArray / Ventilation は船の武器の部品なので含めない)
        new("スコープ・照準器", CompareKind.Generic, ["scope", "optics", "sight", "スコープ", "サイト", "照準器"], ["WeaponAttachment/IronSight"], GroupVariants: true, UexCategoryIds: [17]),
        new("アンダーバレル (ライト・レーザー等)", CompareKind.Generic, ["underbarrel", "flashlight", "laser sight", "アンダーバレル", "フラッシュライト", "レーザーサイト"], ["WeaponAttachment/BottomAttachment"], GroupVariants: true, UexCategoryIds: [17]),
        new("マガジン", CompareKind.Generic, ["magazine", "マガジン", "弾倉"], ["WeaponAttachment/Magazine"], GroupVariants: true, UexCategoryIds: [17]),
        new("サプレッサー等のバレル付属品", CompareKind.Generic, ["suppressor", "silencer", "compensator", "サプレッサー", "サイレンサー", "消音器", "コンペンセイター"], [], NoDataReason: "ゲームデータ (item_index) に個人武器用のバレル付属品 (サプレッサー等) がありません"),
        new("武器の付属品 (全種類)", CompareKind.Generic, ["attachment", "アタッチメント", "付属品"], PersonalAttachmentFilters, GroupVariants: true, UexCategoryIds: [17]),
        // アーマー・服
        new("ヘルメット", CompareKind.Armor, ["helmet", "ヘルメット"], ["Char_Armor_Helmet"], GroupVariants: true, UexCategoryIds: [3]),
        new("アーマー (胴)", CompareKind.Armor, ["torso", "core", "chest", "胴", "胴体", "ボディアーマー", "胴アーマー", "コア"], ["Char_Armor_Torso"], GroupVariants: true, UexCategoryIds: [5]),
        new("アーマー (腕)", CompareKind.Armor, ["arms", "腕", "アーム", "腕アーマー"], ["Char_Armor_Arms"], GroupVariants: true, UexCategoryIds: [1]),
        new("アーマー (脚)", CompareKind.Armor, ["legs", "脚", "レッグ", "脚アーマー"], ["Char_Armor_Legs"], GroupVariants: true, UexCategoryIds: [4]),
        new("アンダースーツ", CompareKind.Armor, ["undersuit", "アンダースーツ"], ["Char_Armor_Undersuit"], GroupVariants: true, UexCategoryIds: [24]),
        new("バックパック", CompareKind.Armor, ["backpack", "バックパック"], ["Char_Armor_Backpack", "Char_Clothing_Backpack"], GroupVariants: true, UexCategoryIds: [2]),
        new("アーマー (全部位)", CompareKind.Armor, ["armor", "armour", "アーマー", "防具"], ArmorPieceFilters, GroupVariants: true, UexCategoryIds: [1, 2, 3, 4, 5, 7, 24], PartSummary: true),
        new("服 (全部位)", CompareKind.Armor, ["clothing", "clothes", "服", "衣服", "衣装"], ["Char_Clothing_*"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds, PartSummary: true),
        // 服の部位 (item_index.item_type。Torso_1 = 上着・ジャケット、Torso_0 = シャツ。
        // Torso_2 (制服のベルトなど) は全件の名前が未設定 (<= プレースホルダー =>) で比べられないので種類にしない)
        new("服 (上着)", CompareKind.Armor, ["jacket", "coat", "上着", "ジャケット", "コート"], ["Char_Clothing_Torso_1"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds),
        new("服 (シャツ)", CompareKind.Armor, ["shirt", "シャツ"], ["Char_Clothing_Torso_0"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds),
        new("服 (ズボン)", CompareKind.Armor, ["pants", "trousers", "ズボン", "パンツ"], ["Char_Clothing_Legs"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds),
        new("服 (靴)", CompareKind.Armor, ["shoes", "boots", "footwear", "靴", "ブーツ", "シューズ"], ["Char_Clothing_Feet"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds),
        new("服 (手袋)", CompareKind.Armor, ["gloves", "手袋", "グローブ"], ["Char_Clothing_Hands"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds),
        new("服 (帽子)", CompareKind.Armor, ["=hat", "=hats", "headwear", "帽子", "キャップ", "ハット"], ["Char_Clothing_Hat"], GroupVariants: true, UexCategoryIds: ClothingUexCategoryIds),
        // 船の部品
        new("シールド", CompareKind.Shield, ["shield generator", "shield", "シールドジェネレーター", "シールド"], ["Shield"], UexCategoryIds: [23]),
        new("動力装置 (パワープラント)", CompareKind.PowerPlant, ["power plant", "powerplant", "動力装置", "パワープラント", "発電機"], ["PowerPlant"], UexCategoryIds: [21]),
        new("冷却装置 (クーラー)", CompareKind.Cooler, ["cooler", "冷却装置", "クーラー"], ["Cooler"], UexCategoryIds: [19]),
        new("クアンタムドライブ", CompareKind.QuantumDrive, ["quantum drive", "quantum", "クアンタムドライブ", "量子ドライブ", "クォンタムドライブ", "クアンタム", "クォンタム"], ["QuantumDrive"], UexCategoryIds: [22]),
        new("レーダー", CompareKind.Radar, ["radar", "レーダー"], ["Radar"], UexCategoryIds: [83]),
        // 船の武器 = WeaponGun のうちロケットポッド (sub_type Rocket) 以外。ロケットポッドは「ミサイル・魚雷」に入れる
        new("船の武器", CompareKind.ShipWeapon, ["ship weapon", "ship gun", "vehicle weapon", "船の武器", "船舶武器", "艦載武器", "機銃"], [ShipGunFilter], UexCategoryIds: [32]),
        // 船の武器の種類 (レーザーキャノン・リピーターなど。record 名の語で分ける)
        .. ShipWeaponKindCategories(),
        // ロケットポッド (WeaponGun で sub_type が Rocket。ゲームデータの AttachDef.SubType) もミサイルの側に入れる。
        // ロケットポッドは前は船の武器 (UEX のカテゴリ 32) で値段を引いていたので、32 も引く
        new("ミサイル・魚雷", CompareKind.Generic, ["missile", "torpedo", "ミサイル", "魚雷"], ["Missile", "WeaponGun/Rocket"], UexCategoryIds: [34, 32]),
        new("ミサイルラック", CompareKind.Generic, ["missile rack", "ミサイルラック"], ["MissileLauncher"], UexCategoryIds: [33]),
        // 船・車両 (ships 表。種別は ships.role / ships.career のキーで判定)
        new("貨物船", CompareKind.Ship, ["cargo ship", "cargo", "freight", "hauler", "貨物船", "輸送船", "貨物", "カーゴ"], [SpaceshipFilter], ShipRoleLike: "freight"),
        new("戦闘機", CompareKind.Ship, ["fighter", "戦闘機", "ファイター"], [SpaceshipFilter], ShipRoleLike: "fighter"),
        new("爆撃機", CompareKind.Ship, ["bomber", "爆撃機"], [SpaceshipFilter], ShipRoleLike: "bomber"),
        new("採掘船", CompareKind.Ship, ["mining ship", "mining", "採掘船", "採掘", "マイニング"], [SpaceshipFilter], ShipRoleLike: "mining"),
        new("サルベージ船", CompareKind.Ship, ["salvage ship", "salvage", "サルベージ船", "サルベージ"], [SpaceshipFilter], ShipRoleLike: "salvage"),
        new("医療船", CompareKind.Ship, ["medical ship", "医療船"], [SpaceshipFilter], ShipRoleLike: "medical"),
        new("給油船", CompareKind.Ship, ["refuel", "給油船", "給油"], [SpaceshipFilter], ShipRoleLike: "refuel"),
        new("レース機", CompareKind.Ship, ["racing", "racer", "レース機", "レース"], [SpaceshipFilter], ShipRoleLike: "racing"),
        new("ガンシップ", CompareKind.Ship, ["gunship", "ガンシップ"], [SpaceshipFilter], ShipRoleLike: "gunship"),
        new("降下艇 (ドロップシップ)", CompareKind.Ship, ["dropship", "ドロップシップ", "降下艇"], [SpaceshipFilter], ShipRoleLike: "dropship"),
        new("旅客船", CompareKind.Ship, ["passenger", "旅客船", "客船"], [SpaceshipFilter], ShipRoleLike: "passenger"),
        new("探索船", CompareKind.Ship, ["exploration", "explorer", "探索船", "探索", "探検"], [SpaceshipFilter], ShipCareerLike: "exploration"),
        new("地上車両", CompareKind.Ship, ["ground vehicle", "rover", "地上車両", "車両", "ローバー", "バギー"], ["NOITEM_Vehicle/Vehicle_GroundVehicle"]),
        new("宇宙船 (全種類)", CompareKind.Ship, ["spaceship", "ship", "宇宙船", "船"], [SpaceshipFilter]),
    ];

    // 種類の文字列 (日本語・英語) から比較の種類を決める。種類の表示名 (例: 「アーマー (胴)」) と同じならそれ。
    // そうでなければ、別名が入力に含まれるもののうち、最も長い別名を持つ種類を選ぶ
    // (例: 「スナイパーライフル」は「ライフル」より長いのでスナイパーライフルになる)
    public static CompareCategory? ResolveCompareCategory(string text)
    {
        var norm = NormalizeCategoryText(text ?? "");
        if (norm.Length == 0) return null;
        var byLabel = CompareCategories.FirstOrDefault(c => NormalizeCategoryText(c.Label) == norm);
        if (byLabel != null) return byLabel;
        CompareCategory? best = null;
        int bestLen = 0;
        var words = System.Text.RegularExpressions.Regex.Split((text ?? "").ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToHashSet();
        foreach (var c in CompareCategories)
            foreach (var a in c.Aliases)
            {
                if (a.StartsWith('='))
                {
                    var word = a[1..].ToLowerInvariant();
                    if (word.Length > bestLen && words.Contains(word)) { best = c; bestLen = word.Length; }
                    continue;
                }
                var na = NormalizeCategoryText(a);
                if (na.Length > bestLen && norm.Contains(na, StringComparison.Ordinal)) { best = c; bestLen = na.Length; }
            }
        return best;
    }

    private static string NormalizeCategoryText(string s) =>
        new(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '_' && ch != '・').ToArray());

    // 比較表の 1 行 (基本モデル、または船 1 種類)
    private sealed class CompareEntry
    {
        public string Uuid = "", Record = "", Short = "", Name = "", ItemType = "";
        public string SubType = "";                  // item_index.sub_type (ゲームデータの AttachDef.SubType)
        public string ManufacturerRef = "";          // メーカーの参照 (items.manufacturer。scitemmanufacturer の record のファイル)
        public string? Weight;                       // アーマーの重さ (Light / Medium / Heavy)。無ければ null
        public int Size;
        public string ComponentJson = "", RawJson = "";
        public string? ShipWeaponKindKey;            // 船の武器の種類のキー (ShipWeaponKindKeyOf。読み込みのときに決める。未設定なら null)
        public List<string> Uuids = new();
        public Dictionary<string, string> Desc = new(StringComparer.OrdinalIgnoreCase);
        public CompareEntry? Base;
        public List<CompareEntry> Variants = new();
        public Dictionary<string, string> Cells = new();
        public Dictionary<string, double> Nums = new();
        public string PriceText = "";
        public double? MinPrice;                     // 店の最安値 (aUEC)。基本品目と色違いのうち最も安く買えるものを選ぶのに使う
        public bool Buyable;
        public string? PriceUuid;
        public List<CompareEntry> Dups = new();      // 同じ名前・同じ種類でまとめた複製 (代表以外)
        public bool DpsNoteCharge, DpsNoteMixed;     // バースト DPS の注 (注1 チャージ式 / 注2 段ごとに連射速度が違う連続動作)
        public string NameSuffix = "";               // 同じ名前で別の品目になったものの区別 (例 " (S7・レーザーキャノン)")
        public List<ShopListing> Shops = new();      // 店で買える売り場 (MinPrice を決めたデータの購入の行。色違いのほうが安ければその色違いのもの)
        public string ShopsNote = "";                // 売り場の一覧の注 (例 色違いの X の売り場)
        public string DisplayName => Name + NameSuffix;
        public string? NameJa;                       // 日本語の品目名 (ResolveItemNameJa。無ければ null)。照合には使わず、［表］の名前の欄にだけ使う
        public string DisplayNameJaEn => ItemNameJaEn(Name, NameJa) + NameSuffix;   // ［表］の名前の欄「日本語名 (英語名)」
    }

    // 種類全体を比べる表を作る (チャットの compare_items)。
    // 戻り値の UsedPriceUuids は、DB に保存済みの売り場・価格 (item_shop_prices) を使った品目の uuid (裏での取り直しの対象)。
    // uexItemPrices: UEX の items_prices (英語名 → 売り場と価格)。cstone の保存データが無い品目の店の販売データに使う。
    // 船の武器が弾を使うかの指定 (ammo) を 使う / 使わない にする。読めない値は null。
    // 使う / 弾を使う / yes / true → 使う、使わない / 弾を使わない / no / false → 使わない (前後の空白と英字の大小は無視)
    public static string? NormalizeShipWeaponAmmo(string? text) => (text ?? "").Trim().ToLowerInvariant() switch
    {
        "使う" or "弾を使う" or "yes" or "true" => "使う",
        "使わない" or "弾を使わない" or "no" or "false" => "使わない",
        _ => null,
    };

    // ammo が読めないときの案内の文 (ChatService と CompareItems で共用)
    public static string ShipWeaponAmmoUnknownText(string? text) =>
        $"弾を使うか (ammo) '{(text ?? "").Trim()}' がわかりません。使う / 使わない のどちらかを指定してください (ダメージの種類で絞る引数ではありません)。";

    // shipScuLookup: (ships.record_name, 船の英語名) → 貨物積載量 (SCU)。船の積載量の共通の関数 (ShipCargo.Resolve: ゲームデータ → UEX → Ship Matrix) で引く
    // weight: アーマーの重さ (Light / Medium / Heavy)。アーマーの種類で指定があればその重さだけを表にし、無ければ重さごとの要約を返す
    public (string Text, List<string> UsedPriceUuids) CompareItems(CompareCategory cat, int? sizeFilter,
        IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices, Func<string, string, ShipCargo.Info?>? shipScuLookup, string? weight = null,
        bool shopOnly = false, string? ammo = null)
    {
        var used = new List<string>();
        if (cat.NoDataReason != null)
            return ($"=== 比較: {cat.Label} ===\nデータ無し: {cat.NoDataReason}。", used);

        using var loc = new LocReader(_translationsDbPath);
        return cat.Kind == CompareKind.Ship
            ? CompareShips(cat, sizeFilter, shipScuLookup, loc, used)
            : CompareItemEntries(cat, sizeFilter, weight, shopOnly, uexItemPrices, loc, used, ammo);
    }

    // ===== アーマーの重さ =====

    public static readonly string[] ArmorWeights = ["Light", "Medium", "Heavy"];

    // 重さで分ける種類か (Char_Armor_* を含むアーマーの種類。服 Char_Clothing_* だけの種類は分けない)
    public static bool UsesArmorWeight(CompareCategory cat) =>
        cat.Kind == CompareKind.Armor && cat.Filters.Any(f => f.StartsWith("Char_Armor_", StringComparison.Ordinal));

    // 文字列から重さを読む。空白・記号で区切った語が light / medium / heavy、ライト / ミディアム / ヘビー (ヘヴィ)、軽 / 中 / 重 と
    // 一致したときだけ読む (部分一致はしない。「superlight」は読まない)。語の後ろに アーマー / armor / armour が付いたもの
    // (例 ヘビーアーマー) も読む。allowKanji が false なら漢字 1 文字 (軽 / 中 / 重) は読まない (種類の文字列から読むとき)
    public static string? ParseArmorWeight(string? text, bool allowKanji = true)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        foreach (var token in System.Text.RegularExpressions.Regex.Split(t, @"[\s\p{P}\p{S}]+"))
        {
            if (token.Length == 0) continue;
            foreach (var suffix in new[] { "", "アーマー", "armor", "armour" })
            {
                if (!token.EndsWith(suffix, StringComparison.Ordinal) || token.Length == suffix.Length) continue;
                var w = token[..^suffix.Length] switch
                {
                    "light" or "ライト" => "Light",
                    "medium" or "ミディアム" => "Medium",
                    "heavy" or "ヘビー" or "ヘヴィ" => "Heavy",
                    "軽" when allowKanji => "Light",
                    "中" when allowKanji => "Medium",
                    "重" when allowKanji => "Heavy",
                    _ => null,
                };
                if (w != null) return w;
            }
        }
        return null;
    }

    public static string ArmorWeightLabel(string? weight) => weight switch
    {
        "Light" => "ライト", "Medium" => "ミディアム", "Heavy" => "ヘビー", _ => "重さ無し",
    };

    // 品目の重さ。ゲームデータの AttachDef.SubType (item_index.sub_type) が Light / Medium / Heavy ならそれ
    // (バックパックの LightArmor は Light)。そうでなければ英語の説明文の「Item Type:」の行の語 (Light / Medium / Heavy。
    // 例 Heavy Utility → Heavy、Super Heavy Armor → Heavy)。どちらにも無ければ null (飛行用・レース用ヘルメットなど)
    private static string? ArmorWeightOf(string subType, IReadOnlyDictionary<string, string> desc)
    {
        foreach (var w in ArmorWeights)
            if (subType.Equals(w, StringComparison.OrdinalIgnoreCase)) return w;
        if (subType.Equals("LightArmor", StringComparison.OrdinalIgnoreCase)) return "Light";
        if (desc.TryGetValue("Item Type", out var it))
        {
            var m = System.Text.RegularExpressions.Regex.Match(it, @"\b(Light|Medium|Heavy)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) return ArmorWeights.First(w => w.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private (string, List<string>) CompareItemEntries(CompareCategory cat, int? sizeFilter, string? weight, bool shopOnly,
        IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices, LocReader loc, List<string> used, string? ammo = null)
    {
        var roots = LoadCompareRoots(cat, sizeFilter, loc, out var excluded, out var noEntries);
        var sizeLabel = sizeFilter.HasValue ? $" S{sizeFilter.Value}" : "";
        // 船の武器を弾を使うか (ammo: 使う / 使わない) で絞る (ゲームデータで判定できないものは外れる)。
        // 値は NormalizeShipWeaponAmmo で 使う / 使わない にし、読めない値は案内の文を返す
        if (!string.IsNullOrWhiteSpace(ammo) && cat.Kind == CompareKind.ShipWeapon)
        {
            var normalized = NormalizeShipWeaponAmmo(ammo);
            if (normalized == null) return (ShipWeaponAmmoUnknownText(ammo), used);
            ammo = normalized;
        }
        else ammo = null;
        if (ammo != null && !noEntries)
        {
            roots = roots.Where(e => e.Cells.TryGetValue("弾", out var a) && a == ammo).ToList();
            sizeLabel += $" 弾を{ammo}";
            if (roots.Count == 0)
                return ($"=== 比較: {cat.Label}{sizeLabel} ===\nデータ無し: 弾を{ammo}武器がありません。", used);
        }
        if (noEntries)
            return ($"=== 比較: {cat.Label}{sizeLabel} ===\nデータ無し: ゲームデータ (item_index) に該当する品目がありません。" +
                    (excluded > 0 ? $"\n(名前が未設定の品目 {excluded} 件は除外)" : ""), used);

        // アーマーの重さ。指定があればその重さの基本モデルだけにする
        var weighted = UsesArmorWeight(cat);
        if (!weighted) weight = null;
        if (weighted)
        {
            foreach (var e in roots) e.Weight = ArmorWeightOf(e.SubType, e.Desc);
            if (weight != null)
            {
                var counts = string.Join(" / ", ArmorWeights.Cast<string?>().Append(null)
                    .Select(w => (w, n: roots.Count(e => e.Weight == w))).Where(x => x.n > 0).Select(x => $"{ArmorWeightLabel(x.w)} {x.n} 種類"));
                roots = roots.Where(e => e.Weight == weight).ToList();
                if (roots.Count == 0)
                    return ($"=== 比較: {cat.Label}{sizeLabel} {ArmorWeightLabel(weight)} ===\nデータ無し: {ArmorWeightLabel(weight)} の品目がありません (重さごとの基本モデルの数: {counts})。", used);
            }
        }

        FillComparePrices(roots, uexItemPrices);

        // 店で買えるものだけ (shop_only)
        var shopLabel = shopOnly ? " (店で買えるものだけ)" : "";
        if (shopOnly)
        {
            var all0 = roots.Count;
            roots = roots.Where(e => e.Buyable).ToList();
            if (roots.Count == 0)
                return ($"=== 比較: {cat.Label}{sizeLabel}{(weight != null ? " " + ArmorWeightLabel(weight) : "")}{shopLabel} ===\nデータ無し: 店の販売データのある品目がありません (基本モデル {all0} 種類のうち 0 種類)。", used);
        }
        var weightLabel = weight != null ? $" {ArmorWeightLabel(weight)}" : "";

        if (cat.PartSummary)
        {
            var shownParts = new List<CompareEntry>();
            var partText = PartSummary(cat, sizeLabel + weightLabel + shopLabel, roots, weighted && weight == null, excluded, shownParts);
            AddRefreshTargets(used, shownParts);
            return (partText, used);
        }
        if (weighted && weight == null)
        {
            var shownWeights = new List<CompareEntry>();
            var weightText = ArmorWeightSummary(cat, sizeLabel + shopLabel, roots, excluded, shownWeights);
            AddRefreshTargets(used, shownWeights);
            return (weightText, used);
        }

        var allHealing = roots.Count > 0 && roots.All(IsHealingDevice);
        var cols = ItemColumns(cat.Kind, roots);
        var sb = new StringBuilder();
        sb.AppendLine($"=== 比較: {cat.Label}{sizeLabel}{weightLabel}{shopLabel} — 基本モデル {roots.Count} 種類" +
                      (cat.GroupVariants ? $" (色違いを含めると {roots.Count + roots.Sum(e => e.Variants.Count)} 品目)" : "") + " ===");
        sb.AppendLine("出典: 性能 = ゲームデータ (DB の items 表と、ゲームの英語の説明文)。店 = cstone の保存データ、無ければ UEX。");
        if (cat.GroupVariants)
            sb.AppendLine("※ 「色違い N 種」は性能が同じ色・柄違いの数。名前の一覧は lookup で品目名を聞くと返します。");
        if ((cat.Kind == CompareKind.PersonalWeapon || cat.Kind == CompareKind.ShipWeapon) && !allHealing)
        {
            sb.AppendLine("※ " + WeaponDamageNote);
            if (cat.Kind == CompareKind.PersonalWeapon && roots.Any(e => e.Cells.ContainsKey(TtkColumn)))
                sb.AppendLine("※ " + TtkColumnNote);
            foreach (var n in DpsNotes(roots)) sb.AppendLine(n);
            // 弾のデータの表 (ammo_params) そのものが空 (未構築) のときだけ案内する (弾を使わない品目だけの表では出さない)
            if (!roots.Any(e => e.Nums.ContainsKey("1発のダメージ")) && !HasAmmoParams())
                sb.AppendLine("※ 弾のデータ (ammo_params) がありません。設定タブの［インデックス構築］を実行すると入ります。");
        }
        sb.AppendLine("| " + (cat.GroupVariants ? "基本モデル" : "品目") + " | " + string.Join(" | ", cols) + " | 店 |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", cols.Count + 2)));
        var ordered = roots.OrderByDescending(x => x.Buyable).ThenBy(x => x.Size).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var e in ordered)
        {
            var cells = cols.Select(c => CellText(e, c));
            var name = e.DisplayName + (e.Variants.Count > 0 ? $" (色違い {e.Variants.Count} 種)" : "");
            sb.AppendLine($"| {Cell(name)} | {string.Join(" | ", cells.Select(Cell))} | {Cell(e.PriceText)} |");
        }

        sb.AppendLine();
        var bestBlock = BestBlock(cat.Kind, roots, allHealing);
        sb.Append(bestBlock);
        if (excluded > 0) sb.AppendLine($"(名前が未設定の品目 {excluded} 件は除外)");

        // 一覧が長すぎるときは (どの種類でも)、主な性能の上位と件数だけの要約にする
        if (sb.Length > CompareTextLimit)
        {
            var title = $"=== 比較: {cat.Label}{sizeLabel}{weightLabel}{shopLabel} — 要約 (基本モデル {roots.Count} 種類" +
                        (cat.GroupVariants ? $"、色違いを含めると {roots.Count + roots.Sum(e => e.Variants.Count)} 品目" : "") + ") ===";
            var notes = new List<string>
            {
                $"※ 全部の一覧は {CompareTextLimit:N0} 文字を超えるので、主な性能の上位 {SingleSummaryTop} 件だけを示します。" +
                (sizeFilter.HasValue ? "" : "サイズや種類を指定すれば一覧を出せます。") +
                (shopOnly ? "" : $"店で買えるものだけ (shop_only。店で買える {roots.Count(e => e.Buyable)} 種類) に絞ることもできます。"),
                RankingNoteFor(cat.Kind),
            };
            if ((cat.Kind == CompareKind.PersonalWeapon || cat.Kind == CompareKind.ShipWeapon) && !allHealing) notes.Add("※ " + WeaponDamageNote);
            if (cat.Kind == CompareKind.PersonalWeapon && roots.Any(e => e.Cells.ContainsKey(TtkColumn))) notes.Add("※ " + TtkColumnNote);
            var shownSingle = new List<CompareEntry>();
            var text = RankedSummary(cat.Kind, title, null, notes, [(cat.Label, roots, null)], SingleSummaryTop, false, excluded, shownSingle,
                sourceNote: cat.Kind == CompareKind.Armor ? SummarySourceNote : CompareSourceNote, variantNote: cat.GroupVariants, tail: bestBlock);
            AddRefreshTargets(used, shownSingle);
            return (text, used);
        }
        AddRefreshTargets(used, ordered);
        return (sb.ToString(), used);
    }

    // 比べるツールと武器の表 (WeaponTablePanel) で共用する前半: item_index から品目を読み、同じ名前の複製を 1 つにまとめ、
    // 基本品目と色違いに分けて (GroupVariants の種類)、基本品目の性能を入れる (FillItemStats)。
    // excluded は名前が未設定で外した数、noEntries は品目が 1 つも無かったか。
    // rootFilter を渡すと、それに当たる基本品目だけを返す (性能を入れる前に絞るので、要らない品目の性能は読まない)
    private List<CompareEntry> LoadCompareRoots(CompareCategory cat, int? sizeFilter, LocReader loc, out int excluded, out bool noEntries,
        Func<CompareEntry, bool>? rootFilter = null)
    {
        using var cmd = _conn.CreateCommand();
        var where = BuildFilterWhere(cmd, cat.Filters);
        var sizeExpr = ItemIndexHasSize() ? "COALESCE(x.size, 0)" : "0";
        cmd.CommandText = $"""
            SELECT x.uuid, x.record_name, x.name, x.item_type, {sizeExpr}, i.name, COALESCE(i.size, 0), i.component_json, i.raw_json, x.sub_type,
                   COALESCE(NULLIF(i.manufacturer, ''), x.manufacturer, '')
            FROM item_index x LEFT JOIN items i ON i.record_name = x.record_name
            WHERE {where}
            ORDER BY LENGTH(x.record_name), x.record_name
            """;
        var all = new List<CompareEntry>();
        excluded = 0;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var record = r.IsDBNull(1) ? "" : r.GetString(1);
                var shortName = record.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? record["EntityClassDefinition.".Length..] : record;
                if (cat.RecordToken != null)
                {
                    var parts = shortName.Split('_');
                    if (parts.Length < 2 || !parts[1].Equals(cat.RecordToken, StringComparison.OrdinalIgnoreCase)) continue;
                }
                var indexSize = r.GetInt32(4);
                var itemsSize = r.GetInt32(6);
                var size = indexSize > 0 ? indexSize : itemsSize > 0 ? itemsSize : SizeFromRecord(shortName);
                if (sizeFilter.HasValue && size != sizeFilter.Value) continue;
                var indexName = r.IsDBNull(2) ? "" : r.GetString(2);
                var itemsName = r.IsDBNull(5) ? "" : r.GetString(5);
                var name = ResolveItemNameEn(itemsName, indexName, loc);
                // 船の武器の種類は表示名でも決める。種類での絞り込みは、同じ名前をまとめた後の種類 (名前のキーの持ち主の種類) で行う
                // (名前が未設定の品目だけはここで絞り、その種類のときだけ除外に数える)
                string? shipKindKey = null;
                if (cat.Kind == CompareKind.ShipWeapon)
                {
                    shipKindKey = ShipWeaponKindKeyOf(shortName, name, r.IsDBNull(7) ? "" : r.GetString(7), r.IsDBNull(8) ? "" : r.GetString(8), loc);
                    if (name == null && cat.ShipWeaponKind != null && shipKindKey != cat.ShipWeaponKind) continue;
                }
                if (name == null) { excluded++; continue; }
                var e = new CompareEntry
                {
                    Uuid = r.GetString(0),
                    Record = record,
                    Short = shortName,
                    Name = name,
                    NameJa = ResolveItemNameJa(itemsName, loc),
                    ItemType = r.IsDBNull(3) ? "" : r.GetString(3),
                    Size = size,
                    ComponentJson = r.IsDBNull(7) ? "" : r.GetString(7),
                    RawJson = r.IsDBNull(8) ? "" : r.GetString(8),
                    SubType = r.IsDBNull(9) ? "" : r.GetString(9),
                    ManufacturerRef = r.IsDBNull(10) ? "" : r.GetString(10),
                    ShipWeaponKindKey = shipKindKey,
                };
                e.Uuids.Add(e.Uuid);
                all.Add(e);
            }
        }

        // 同じ名前の品目 (AI 用・砲塔用・テスト用など内部用の複製) は 1 つにまとめる。サイズや種類が違っても同じ武器として 1 行にする
        // (ゲームに同じ名前の別の武器は無い)。代表の選び方: 武器は PickWeaponRepresentative (実在の record を選ぶ)、ほかは PickRepresentative
        var sold = LoadSoldUuids();
        var isWeapon = cat.Kind is CompareKind.PersonalWeapon or CompareKind.ShipWeapon;
        var shipUse = isWeapon ? LoadShipPortUse() : null;
        var entries = new List<CompareEntry>();
        foreach (var g in all.GroupBy(e => (e.ItemType, e.Name.ToLowerInvariant())))
        {
            var rep = shipUse != null ? MergeSameName(g, PickWeaponRepresentative(g.ToList(), sold, shipUse)) : MergeSameName(g, sold);
            // 船の武器の種類は、名前のローカライズキー (@item_Name<record 名>) の持ち主の record のものにする。値 (1 発・DPS など) は代表のまま
            // (例 M9A Cannon: 代表は BEHR_JavelinBallisticCannon_S7 だが、名前のキーの持ち主は BEHR_LaserCannon_S7 なのでレーザーキャノン)。
            // 代表が持ち主か、持ち主が群に無ければ代表の種類のまま
            if (cat.Kind == CompareKind.ShipWeapon && rep.Dups.Count > 0 && !OwnsNameKey(rep))
            {
                var owner = rep.Dups.FirstOrDefault(OwnsNameKey);
                if (owner != null) rep.ShipWeaponKindKey = owner.ShipWeaponKindKey;
            }
            entries.Add(rep);
        }
        // 船の武器の種類で絞る (まとめた後の種類で決める。種類は名前のキーの持ち主のもの。上を参照)
        if (cat.ShipWeaponKind != null) entries = entries.Where(e => e.ShipWeaponKindKey == cat.ShipWeaponKind).ToList();

        noEntries = entries.Count == 0;
        if (noEntries) return new List<CompareEntry>();

        // 基本品目と色違い。色違いの表 (item_variants) があればそれで分ける (性能が基本品目と同じものだけが入っている)。
        // 表が無い古い DB では、その場で名前・record 名から判定する (性能は比べない)
        List<CompareEntry> roots;
        if (cat.GroupVariants)
        {
            var variantOf = LoadVariantBases(entries.SelectMany(e => e.Uuids));
            if (variantOf.Count > 0)
            {
                var byUuid = new Dictionary<string, CompareEntry>(StringComparer.Ordinal);
                foreach (var e in entries) foreach (var u in e.Uuids) byUuid.TryAdd(u, e);
                foreach (var e in entries)
                {
                    var baseUuid = e.Uuids.Select(u => variantOf.TryGetValue(u, out var b) ? b : null).FirstOrDefault(b => b != null);
                    if (baseUuid != null && byUuid.TryGetValue(baseUuid, out var be) && !ReferenceEquals(be, e)) e.Base = be;
                }
            }
            else AssignBaseModels(entries);
            roots = entries.Where(e => e.Base == null).ToList();
            foreach (var e in entries.Where(e => e.Base != null)) e.Base!.Variants.Add(e);
        }
        else roots = entries;
        if (rootFilter != null) roots = roots.Where(rootFilter).ToList();

        // 「ミサイル・魚雷」のロケットポッド (WeaponGun/Rocket) は船の武器と同じ計算 (1発のダメージ・連射速度・DPS・弾速)。［船舶戦闘］の表と同じ値
        // (説明文の「性能 (説明文)」の欄 (例 Rockets: 6xS1) も今までどおり付ける)
        foreach (var e in roots)
        {
            if (cat.Kind == CompareKind.Generic && string.Equals(e.ItemType, "WeaponGun", StringComparison.OrdinalIgnoreCase))
            {
                FillItemStats(CompareKind.ShipWeapon, e, loc);
                AddDescCell(e);
            }
            else FillItemStats(cat.Kind, e, loc);
        }
        return roots;
    }

    // 店の販売データ (cstone の保存データ → 無ければ UEX の items_prices) を基本品目と色違いに入れる (比べるツールと武器の表で共用)
    private void FillComparePrices(List<CompareEntry> roots, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices)
    {
        // 店で買えるか: cstone の保存データ (item_shop_prices) → 無ければ UEX の items_prices。
        // 色違いは売り場が違うので品目ごとに調べ、基本品目と色違いのうち最も安く買えるものをその行の店にする
        var shopRows = ShopPriceStore.LoadMany(_conn, roots.SelectMany(e => e.Uuids.Concat(e.Variants.SelectMany(v => v.Uuids))));
        foreach (var e in roots)
        {
            FillItemPrice(e, shopRows, uexItemPrices);
            CompareEntry? cheapest = e.Buyable && e.MinPrice.HasValue ? e : null;
            foreach (var v in e.Variants)
            {
                FillItemPrice(v, shopRows, uexItemPrices);
                if (v.Buyable && v.MinPrice.HasValue && (cheapest == null || v.MinPrice.Value < cheapest.MinPrice!.Value)) cheapest = v;
            }
            if (cheapest != null && !ReferenceEquals(cheapest, e))
            {
                e.PriceText = cheapest.PriceText + $" [最安は色違いの {cheapest.Name}]";
                e.MinPrice = cheapest.MinPrice;
                e.Buyable = true;
                e.Shops = cheapest.Shops;
                e.ShopsNote = $"色違いの {cheapest.Name} の売り場";
            }
        }
    }

    // 弾のデータの表 (ammo_params) に行があるか。表・列が無い古い DB だけ false にし、
    // それ以外の例外 (ロック・壊れた DB など) は呼び出し側へ出す (「未構築」と誤って案内しないため。HasItemIndex と同じ)
    private bool HasAmmoParams()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM ammo_params)";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return false; }
    }

    private const string CompareSourceNote = "出典: 性能 = ゲームデータ (DB の items 表と、ゲームの英語の説明文)。店 = cstone の保存データ、無ければ UEX。";

    // 1 回の質問で売り場・価格を取り直す品目の上限。1 回の質問の中の compare_items (表に出した行の基本品目を表の上から) と
    // search_price の分を合わせて、ChatService.NotePriceItemUsed が集めた順に数える (1 回の compare_items の結果もこの数まで)。
    // cstone には 1 件ごとに 0.75 秒の間隔を空けて取りに行くので、20 件で 15 秒ほど (答えの後に裏で取り直す)
    public const int MaxRefreshTargetsPerQuestion = 20;

    private static void AddRefreshTargets(List<string> used, IEnumerable<CompareEntry> shownInOrder)
    {
        foreach (var e in shownInOrder)
        {
            if (used.Count >= MaxRefreshTargetsPerQuestion) break;
            if (e.PriceUuid != null && !used.Contains(e.PriceUuid)) used.Add(e.PriceUuid);
        }
    }

    // 医療ガン (薬剤を注入するもの) か
    private static bool IsHealingDevice(CompareEntry e) => e.Cells.ContainsKey("薬剤の注入量 (mSCU/秒)");

    // 医療ガンにはダメージ系の値が無い (A13: 薬剤の注入量だけを出す) ので、混ざる表では「データ無し」ではなく「-」にする欄
    private static readonly HashSet<string> HealingBlankColumns = new(StringComparer.Ordinal)
        { "1発のダメージ", "ビームのダメージ (毎秒)", "DPS (バースト / 平均)", "弾速", TtkColumn };

    // 表の 1 欄の文字。値が無ければ「データ無し」(医療ガンのダメージ系の欄、ミサイル・魚雷の DPS の欄、ロケットポッドの追尾の欄は「-」。種類としてその値を持たないため)
    private static string CellText(CompareEntry e, string col) =>
        e.Cells.TryGetValue(col, out var v) && !string.IsNullOrWhiteSpace(v) ? v
        : IsHealingDevice(e) && HealingBlankColumns.Contains(col) ? "-"
        : col == "DPS (バースト / 平均)" && string.Equals(e.ItemType, "Missile", StringComparison.OrdinalIgnoreCase) ? "-"
        : col == "追尾" && string.Equals(e.ItemType, "WeaponGun", StringComparison.OrdinalIgnoreCase) ? "-"
        : "データ無し";

    // 「店で買えるものの中で、主な性能ごとに最も高いもの」の欄 (個人武器はヘビー相手に胴で最も少ない発数を足す)
    private static string BestBlock(CompareKind kind, List<CompareEntry> roots, bool allHealing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【店で買えるものの中で、主な性能ごとに最も高いもの】");
        var stats = BestStats(kind);
        if (allHealing) stats = stats.Where(s => s.Key == "装弾数").ToList();
        // 医療ガンが混ざる表では、比べる対象から医療ガンを外す (医療ガンだけの表では医療ガンどうしで比べる)
        sb.Append(BestLines(allHealing ? roots : roots.Where(e => !IsHealingDevice(e)).ToList(), stats));
        if (kind == CompareKind.PersonalWeapon && !allHealing)
        {
            var withTtk = roots.Where(e => e.Buyable && e.Nums.ContainsKey(TtkHeavyTorsoKey)).ToList();
            if (withTtk.Count > 0)
            {
                var min = withTtk.Min(e => e.Nums[TtkHeavyTorsoKey]);
                var names = withTtk.Where(e => e.Nums[TtkHeavyTorsoKey] == min).Select(e => e.DisplayName).ToList();
                sb.AppendLine($"- ヘビー相手に胴で最も少ない発数で倒せる (ダウンまで): {string.Join(", ", names)} ({min:0} 発)");
            }
        }
        return sb.ToString();
    }

    // 要約の上位の順の説明
    private static string RankingNoteFor(CompareKind kind)
    {
        if (kind == CompareKind.Armor) return RankingNote;
        var labels = RankKeys(kind).Select(k => k.Label).ToList();
        return labels.Count == 0
            ? "※ 上位の順: 店で買えるものを先に、名前順。"
            : $"※ 上位の順: 店で買えるものを先に、{string.Join("、同じなら ", labels)} の高い順。";
    }

    // 要約で並べる性能 (Nums のキー, 表示名)
    private static List<(string Key, string Label)> RankKeys(CompareKind kind) => kind switch
    {
        CompareKind.Armor => [("ダメージ軽減", "ダメージ軽減"), ("耐えられる温度の幅", "耐えられる温度の幅"), ("放射線防護", "放射線防護"), ("所持容量", "所持容量")],
        CompareKind.Ship => [("貨物積載量", "貨物積載量"), ("乗員", "乗員")],
        _ => BestStats(kind).Select(s => (s.Key, s.Label)).ToList(),
    };

    // 要約の上位の件数 (重さごと / 部位ごと / 1 つの部位)。
    // 部位ごとは部位が 6〜8 あるので 5 件、重さごと (3〜4 群) は 10 件、1 つの部位を要約にするときは 20 件 (どれも 1 万文字に収まる数)
    private const int ArmorWeightSummaryTop = 10;
    private const int PartSummaryTop = 5;
    private const int SingleSummaryTop = 20;

    private const string RankingNote = "※ 上位の順: 店で買えるものを先に、ダメージ軽減の高い順、同じなら耐えられる温度の幅の広い順、放射線防護の高い順、所持容量の多い順。";
    private const string VariantNote = "※ 「色違い N 種」は性能が同じ色・柄違いの数。名前の一覧は lookup で品目名を聞くと返します。";
    private const string SummarySourceNote = "出典: 性能 = ゲームデータ (DB の items 表と、ゲームの英語の説明文)。店 = cstone の保存データ、無ければ UEX。重さ = ゲームデータの種類 (SubType)、無ければ説明文の Item Type。";

    // アーマーの重さごとの要約 (重さの指定が無いとき)。重さごとの基本モデルの数と、重さごとに主な性能の上位 ArmorWeightSummaryTop 件の表。
    // 上位の順: 店で買えるものを先に、ダメージ軽減の高い順、同じなら耐えられる温度の幅 (上限 − 下限) の広い順、放射線防護の高い順、所持容量の多い順
    // (ダメージ軽減は同じ重さの中でほぼ同じ値なので、温度・放射線・所持容量で並べる)
    private static string ArmorWeightSummary(CompareCategory cat, string sizeLabel, List<CompareEntry> roots, int excluded, List<CompareEntry>? shown = null)
    {
        var groups = ArmorWeights.Cast<string?>().Append(null)
            .Select(w => (Label: ArmorWeightLabel(w), Items: roots.Where(e => e.Weight == w).ToList(),
                          MoreHint: w != null ? $"重さに「{ArmorWeightLabel(w)}」を指定すると全部を表にします" : "重さの無いものは全部の一覧を出せません"))
            .Where(g => g.Items.Count > 0).ToList();
        var title = $"=== 比較: {cat.Label}{sizeLabel} — 重さごとの要約 (基本モデル {roots.Count} 種類、色違いを含めると {roots.Count + roots.Sum(e => e.Variants.Count)} 品目) ===";
        var counts = "重さごとの数: " + string.Join(" / ", groups.Select(g => $"{g.Label} {g.Items.Count} 種類 (色違いを含めると {g.Items.Count + g.Items.Sum(e => e.Variants.Count)} 品目)"));
        var notes = new List<string>
        {
            $"※ ここでは重さごとに上位 {ArmorWeightSummaryTop} 件だけを示します。重さ (ライト / ミディアム / ヘビー) を指定すれば、その重さの全部の一覧を出せます。",
            RankingNote,
        };
        return RankedSummary(cat.Kind, title, counts, notes, groups.Select(g => (g.Label, g.Items, (string?)g.MoreHint)).ToList(), ArmorWeightSummaryTop, false, excluded, shown);
    }

    // 部位ごとの要約 (部位を指定しない種類: アーマー全体・服)。部位 (item_type) ごとの基本モデルの数と、部位ごとの上位 PartSummaryTop 件の表。
    // showWeight: アーマーで重さの指定が無いとき。件数に重さの内訳を付け、表に重さの列を足す
    private static string PartSummary(CompareCategory cat, string label, List<CompareEntry> roots, bool showWeight, int excluded, List<CompareEntry>? shown = null)
    {
        var order = PartLabels.Select((p, i) => (p.ItemType, i)).ToDictionary(x => x.ItemType, x => x.i, StringComparer.Ordinal);
        var groups = roots.GroupBy(e => e.ItemType)
            .OrderBy(g => order.TryGetValue(g.Key, out var i) ? i : int.MaxValue).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var partLabel = PartLabels.FirstOrDefault(p => p.ItemType == g.Key).Label ?? g.Key;
                return (Label: partLabel, Items: g.ToList(), MoreHint: (string?)$"種類に「{partLabel}」を指定するとその部位の一覧を出します");
            }).ToList();
        string WeightBreakdown(List<CompareEntry> items) =>
            string.Join(" / ", ArmorWeights.Cast<string?>().Append(null).Select(w => (w, n: items.Count(e => e.Weight == w)))
                .Where(x => x.n > 0).Select(x => $"{ArmorWeightLabel(x.w)} {x.n}"));
        var title = $"=== 比較: {cat.Label}{label} — 部位ごとの要約 (基本モデル {roots.Count} 種類、色違いを含めると {roots.Count + roots.Sum(e => e.Variants.Count)} 品目) ===";
        var counts = "部位ごとの数: " + string.Join(" / ", groups.Select(g =>
            $"{g.Label} {g.Items.Count} 種類" + (showWeight ? $" ({WeightBreakdown(g.Items)})" : "")));
        var isArmor = cat.Filters.Any(f => f.StartsWith("Char_Armor_", StringComparison.Ordinal));
        var notes = new List<string>
        {
            $"※ ここでは部位ごとに上位 {PartSummaryTop} 件だけを示します。部位 (" +
            (isArmor ? "ヘルメット・胴・腕・脚・アンダースーツ・バックパック" : "上着・シャツ・ズボン・靴・手袋・帽子など") +
            ") を指定すれば、その部位の一覧を出せます。" + (isArmor ? "重さ (ライト / ミディアム / ヘビー) も指定できます。" : "店で買えるものだけに絞ることもできます。"),
            RankingNote,
        };
        return RankedSummary(cat.Kind, title, counts, notes, groups, PartSummaryTop, showWeight, excluded, shown);
    }

    // 群ごとに上位 top 件の表を並べる要約 (重さごと・部位ごと・1 つの種類)。並び順は RankKeys (店で買えるものを先に、主な性能の高い順)。
    // shown: 表に出した品目を表の上から入れる (売り場・価格の取り直しの対象にする)。fixedCols: 表の列 (船など。無ければ ItemColumns)。
    // tail: 最後に付ける文 (「店で買えるものの中で…」の欄など)
    private static string RankedSummary(CompareKind kind, string title, string? counts, List<string> notes,
        List<(string Label, List<CompareEntry> Items, string? MoreHint)> groups, int top, bool weightColumn, int excluded,
        List<CompareEntry>? shown = null, string? sourceNote = null, bool variantNote = true, List<string>? fixedCols = null,
        string nameHeader = "基本モデル", string? tail = null)
    {
        double N(CompareEntry e, string key) => e.Nums.TryGetValue(key, out var v) ? v : -1;
        var keys = RankKeys(kind);
        var pickedAll = new List<CompareEntry>();
        var sb = new StringBuilder();
        sb.AppendLine(title);
        sb.AppendLine(sourceNote ?? SummarySourceNote);
        if (counts != null) sb.AppendLine(counts);
        foreach (var n in notes) sb.AppendLine(n);
        if (variantNote) sb.AppendLine(VariantNote);
        foreach (var (label, items, moreHint) in groups)
        {
            IOrderedEnumerable<CompareEntry> ordered = items.OrderByDescending(e => e.Buyable);
            foreach (var (key, _) in keys) { var k = key; ordered = ordered.ThenByDescending(e => N(e, k)); }
            var picked = ordered.ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(top).ToList();
            shown?.AddRange(picked);
            var cols = fixedCols ?? ItemColumns(kind, picked);
            sb.AppendLine();
            sb.AppendLine($"【{label} ({nameHeader} {items.Count} 種類、店で買える {items.Count(e => e.Buyable)} 種類) の上位 {picked.Count} 件】");
            sb.AppendLine($"| {nameHeader} | " + (weightColumn ? "重さ | " : "") + string.Join(" | ", cols) + " | 店 |");
            sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", cols.Count + 2 + (weightColumn ? 1 : 0))));
            foreach (var e in picked)
            {
                var cells = cols.Select(c => CellText(e, c));
                var name = e.DisplayName + (e.Variants.Count > 0 ? $" (色違い {e.Variants.Count} 種)" : "");
                sb.AppendLine($"| {Cell(name)} | " + (weightColumn ? $"{ArmorWeightLabel(e.Weight)} | " : "") + $"{string.Join(" | ", cells.Select(Cell))} | {Cell(e.PriceText)} |");
            }
            if (items.Count > picked.Count)
                sb.AppendLine($"(ほか {items.Count - picked.Count} 種類" + (moreHint != null ? $"。{moreHint})" : ")"));
            pickedAll.AddRange(picked);
        }
        foreach (var n in DpsNotes(pickedAll)) sb.AppendLine(n);
        if (tail != null) { sb.AppendLine(); sb.Append(tail); }
        if (excluded > 0) sb.AppendLine($"(名前が未設定の品目 {excluded} 件は除外)");
        return sb.ToString();
    }

    // ===== 色違いの表 (item_variants) =====

    // 色違いの表。性能が基本品目と同じ色・柄違い (塗装違い・名前付き版を含む) だけを入れる。
    // match_rule は判定の理由 (quote = 名前の "…" を除くと同じ / record = record 名が前方一致 / name = 名前が前方一致 + record の系統が同じ /
    // suffix = 基本名の後ろに語が付いた名前 / stem = 名前の最後の語を除くと同じ)。インデックス構築と［装備データを再抽出］のときに作り直す
    public const string ItemVariantsDdl = """
        CREATE TABLE IF NOT EXISTS item_variants (
            variant_uuid TEXT PRIMARY KEY,
            variant_record TEXT NOT NULL,
            base_uuid TEXT NOT NULL,
            base_record TEXT NOT NULL,
            variant_label TEXT NOT NULL DEFAULT '',
            match_rule TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_item_variants_base ON item_variants(base_uuid);
        """;

    // 色違いを判定する種類 (個人装備)。マガジンは武器から参照される (装弾数・弾のデータを読む) ので対象にしない
    private static bool IsVariantTargetType(string itemType, string subType) =>
        itemType switch
        {
            "WeaponPersonal" or "Grenade" or "FPS_Deployable" or "FPS_Consumable" or "Gadget" => true,
            "WeaponAttachment" => subType != "Magazine",
            _ => itemType.StartsWith("Char_Armor_", StringComparison.Ordinal) || itemType.StartsWith("Char_Clothing_", StringComparison.Ordinal),
        };

    private static CompareKind KindForItemType(string itemType) =>
        itemType == "WeaponPersonal" ? CompareKind.PersonalWeapon
        : itemType == "WeaponGun" ? CompareKind.ShipWeapon
        : itemType.StartsWith("Char_", StringComparison.Ordinal) ? CompareKind.Armor
        : itemType switch
        {
            "Shield" => CompareKind.Shield, "PowerPlant" => CompareKind.PowerPlant, "Cooler" => CompareKind.Cooler,
            "QuantumDrive" => CompareKind.QuantumDrive, "Radar" => CompareKind.Radar, _ => CompareKind.Generic,
        };

    // uuid → 基本品目の uuid (色違いの表。表が無ければ空)
    private Dictionary<string, string> LoadVariantBases(IEnumerable<string> uuids)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = uuids.Distinct().ToList();
        try
        {
            for (int start = 0; start < list.Count; start += 500)
            {
                var chunk = list.Skip(start).Take(500).ToList();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"SELECT variant_uuid, base_uuid FROM item_variants WHERE variant_uuid IN ({string.Join(",", chunk.Select((_, i) => $"@u{i}"))})";
                for (int i = 0; i < chunk.Count; i++) cmd.Parameters.AddWithValue($"@u{i}", chunk[i]);
                using var r = cmd.ExecuteReader();
                while (r.Read()) map[r.GetString(0)] = r.GetString(1);
            }
        }
        catch { }
        return map;
    }

    // 色違いの表を作り直す (インデックス構築・装備データの再抽出の後に呼ぶ)。
    // 判定: 同じ item_type の品目について、
    //   1. 今の基本モデルの判定 (AssignBaseModels: 名前の "…" / record 名の前方一致 / 名前の前方一致 + record の系統) で基本モデルを決め、
    //      性能 (FillItemStats の表示値とサイズ) が基本モデルと同じものだけを色違いにする (違うものは別の品目として残す)
    //   2. 残った品目のうち「名前の最後の語を除いた名前」が同じものが 2 件以上あれば、性能が同じものどうしをまとめる。
    //      基本モデルが無いので、record 名の短いもの (同じなら名前の辞書順で先) を代表 (基本) にする。色名の一覧は使わない
    // 性能を読めない品目 (再抽出の後に items の性能を空にしてあるもの) は、前の表のとおりの基本品目が今もあればそのまま残す。
    // blankVariantData が true なら、色違いの items.raw_json / component_json を空にする (性能は基本品目から読むので重複して持たない)。
    // 戻り値は (色違いの数, 空にした items の行数)
    public (int Variants, int Blanked) RebuildItemVariants(bool blankVariantData)
    {
        using (var ddl = _conn.CreateCommand()) { ddl.CommandText = ItemVariantsDdl; ddl.ExecuteNonQuery(); }
        using var loc = new LocReader(_translationsDbPath);

        // 前の表 (性能を読めない品目の引き継ぎ用)
        var oldBase = new Dictionary<string, (string BaseUuid, string Label, string Rule)>(StringComparer.Ordinal);
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT variant_uuid, base_uuid, variant_label, match_rule FROM item_variants";
            using var r = cmd.ExecuteReader();
            while (r.Read()) oldBase[r.GetString(0)] = (r.GetString(1), r.GetString(2), r.GetString(3));
        }

        var sizeExpr = ItemIndexHasSize() ? "COALESCE(x.size, 0)" : "0";
        var all = new List<(CompareEntry Entry, string SubType)>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT x.uuid, x.record_name, x.name, x.item_type, x.sub_type, {sizeExpr}, i.name, COALESCE(i.size, 0), i.component_json, i.raw_json
                FROM item_index x LEFT JOIN items i ON i.record_name = x.record_name
                ORDER BY LENGTH(x.record_name), x.record_name
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var itemType = r.IsDBNull(3) ? "" : r.GetString(3);
                var subType = r.IsDBNull(4) ? "" : r.GetString(4);
                if (!IsVariantTargetType(itemType, subType)) continue;
                var record = r.GetString(1);
                var shortName = record.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? record["EntityClassDefinition.".Length..] : record;
                var name = ResolveItemNameEn(r.IsDBNull(6) ? "" : r.GetString(6), r.IsDBNull(2) ? "" : r.GetString(2), loc);
                if (name == null) continue;
                var indexSize = r.GetInt32(5);
                var itemsSize = r.GetInt32(7);
                var e = new CompareEntry
                {
                    Uuid = r.GetString(0), Record = record, Short = shortName, Name = name, ItemType = itemType,
                    Size = indexSize > 0 ? indexSize : itemsSize > 0 ? itemsSize : SizeFromRecord(shortName),
                    ComponentJson = r.IsDBNull(8) ? "" : r.GetString(8),
                    RawJson = r.IsDBNull(9) ? "" : r.GetString(9),
                };
                e.Uuids.Add(e.Uuid);
                all.Add((e, subType));
            }
        }

        var sold = LoadSoldUuids();
        var rows = new List<(CompareEntry Variant, CompareEntry Base, string Label, string Rule, List<string> DupBlank)>();
        foreach (var typeGroup in all.Select(x => x.Entry).GroupBy(e => e.ItemType))
        {
            // 同じ名前の複製 (AI 用など) は 1 つにまとめて扱う (比べるツールと同じ。代表の選び方は PickRepresentative)
            var entries = new List<CompareEntry>();
            foreach (var g in typeGroup.GroupBy(e => e.Name.ToLowerInvariant()))
                entries.Add(MergeSameName(g, sold));
            var kind = KindForItemType(typeGroup.Key);
            // 性能の印 (表に出す性能の欄とサイズ)。性能を読めない (raw_json が空) なら null
            string? Sig(CompareEntry e)
            {
                if (e.RawJson.Length == 0) return null;
                FillItemStats(kind, e, loc);
                return e.Size + "\u0001" + string.Join("\u0001", e.Cells.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
            }
            var signature = new Dictionary<CompareEntry, string?>();
            foreach (var e in entries) signature[e] = Sig(e);
            // 色違い v と同じ名前の複製のうち、性能が基本品目 b と同じもの (性能を空にする対象)
            List<string> DupBlank(CompareEntry v, CompareEntry b)
            {
                var bs = signature.TryGetValue(b, out var s0) ? s0 : null;
                if (bs == null) return new List<string>();
                return v.Dups.Where(d => d.RawJson.Length > 0 && Sig(d) == bs).Select(d => d.Record).Distinct(StringComparer.Ordinal).ToList();
            }

            // 1. 今の判定で基本モデルを決め、性能が同じものだけを色違いにする
            AssignBaseModels(entries);
            var baseOf = new Dictionary<CompareEntry, (CompareEntry Base, string Rule)>();
            foreach (var e in entries.Where(x => x.Base != null))
            {
                var b = e.Base!;
                if (signature[e] != null && signature[e] == signature[b]) baseOf[e] = (b, MatchRule(e, b));
                e.Base = null;
            }

            // 1b. 基本名の後ろに語が付いた名前 (例 PAB-1 Core → PAB-1 Core Aqua / PAB-1 Core Dark Green) で性能が同じもの。
            //     2 語以上の色名も拾う。基本は、前に付く名前のうち最も短いもの (短い名前から順に決めるので、基本は色違いにならない)
            var candidates = entries.Where(x => !baseOf.ContainsKey(x) && signature[x] != null).OrderBy(x => x.Name.Length).ToList();
            foreach (var x in candidates)
            {
                if (baseOf.ContainsKey(x)) continue;
                var b = candidates.FirstOrDefault(c => !ReferenceEquals(c, x) && !baseOf.ContainsKey(c) && c.Name.Length < x.Name.Length
                    && x.Name.StartsWith(c.Name + " ", StringComparison.OrdinalIgnoreCase) && signature[c] == signature[x]);
                if (b == null) continue;
                baseOf[x] = (b, "suffix");
                foreach (var y in baseOf.Where(kv => ReferenceEquals(kv.Value.Base, x)).Select(kv => kv.Key).ToList())
                    baseOf[y] = (b, baseOf[y].Rule);
            }

            // 2. 名前の最後の語を除いた名前が同じで性能が同じもの
            var roots = entries.Where(x => !baseOf.ContainsKey(x) && signature[x] != null).ToList();
            foreach (var g in roots.GroupBy(x => NameStem(x.Name) ?? ""))
            {
                if (g.Key.Length == 0) continue;
                foreach (var sub in g.GroupBy(x => signature[x]!))
                {
                    var list = sub.OrderBy(x => x.Short.Length).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
                    if (list.Count < 2) continue;
                    var rep = list[0];
                    foreach (var x in list.Skip(1))
                    {
                        baseOf[x] = (rep, "stem");
                        // x の色違いだったものも代表に付け替える (表を 1 段にする)
                        foreach (var y in baseOf.Where(kv => ReferenceEquals(kv.Value.Base, x)).Select(kv => kv.Key).ToList())
                            baseOf[y] = (rep, baseOf[y].Rule);
                    }
                }
            }

            foreach (var (v, (b, rule)) in baseOf)
                rows.Add((v, b, VariantLabel(v, b, rule), rule, DupBlank(v, b)));

            // 性能を読めない品目は、前の表の基本品目が今もあれば引き継ぐ
            var byUuid = entries.SelectMany(e => e.Uuids.Select(u => (u, e))).GroupBy(x => x.u).ToDictionary(g => g.Key, g => g.First().e, StringComparer.Ordinal);
            foreach (var e in entries.Where(x => signature[x] == null && !baseOf.ContainsKey(x)))
                if (oldBase.TryGetValue(e.Uuid, out var ob) && byUuid.TryGetValue(ob.BaseUuid, out var b))
                {
                    // 前の基本品目が今回ほかの品目の色違いになったら、その基本品目に付け替える (表を 1 段にする)
                    if (baseOf.TryGetValue(b, out var bb)) b = bb.Base;
                    if (!ReferenceEquals(b, e)) rows.Add((e, b, ob.Label, ob.Rule, DupBlank(e, b)));
                }
        }

        int blanked = 0;
        using (var tx = _conn.BeginTransaction())
        {
            using (var del = _conn.CreateCommand()) { del.Transaction = tx; del.CommandText = "DELETE FROM item_variants"; del.ExecuteNonQuery(); }
            using var ins = _conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO item_variants(variant_uuid, variant_record, base_uuid, base_record, variant_label, match_rule) VALUES(@vu, @vr, @bu, @br, @lb, @mr)";
            var pVu = ins.Parameters.Add("@vu", SqliteType.Text);
            var pVr = ins.Parameters.Add("@vr", SqliteType.Text);
            var pBu = ins.Parameters.Add("@bu", SqliteType.Text);
            var pBr = ins.Parameters.Add("@br", SqliteType.Text);
            var pLb = ins.Parameters.Add("@lb", SqliteType.Text);
            var pMr = ins.Parameters.Add("@mr", SqliteType.Text);
            using var blank = _conn.CreateCommand();
            blank.Transaction = tx;
            blank.CommandText = "UPDATE items SET raw_json = '', component_json = '' WHERE record_name = @rn AND (raw_json <> '' OR COALESCE(component_json, '') <> '')";
            var pRn = blank.Parameters.Add("@rn", SqliteType.Text);
            foreach (var (v, b, label, rule, dupBlank) in rows)
            {
                // 同じ名前の複製 (v.Uuids) もまとめて同じ基本品目の色違いにする
                foreach (var u in v.Uuids)
                {
                    pVu.Value = u; pVr.Value = v.Record; pBu.Value = b.Uuid; pBr.Value = b.Record; pLb.Value = label; pMr.Value = rule;
                    ins.ExecuteNonQuery();
                }
                if (blankVariantData)
                {
                    // 色違い本体と、性能が基本品目と同じ複製 (例 behr_smg_ballistic_01_white02_tow) の性能を空にする
                    foreach (var rn in dupBlank.Prepend(v.Record).Distinct(StringComparer.Ordinal))
                    {
                        pRn.Value = rn;
                        blanked += blank.ExecuteNonQuery();
                    }
                }
            }
            tx.Commit();
        }
        return (rows.Count, blanked);
    }

    // ゲーム内部用の record (店で売られない複製・試験用) の名前の語 (record 名を _ で区切った語)。
    // 実データ (比べるツールの対象の種類) で、これらの語を含む record はどれも販売データが無く、
    // 同じ名前の群で record 名が最も短いものになっていたのは invisible_* 4 件・mannequin_undersuit・FPS_Consumable_TEMPLATE だった
    private static readonly HashSet<string> InternalRecordWords = new(StringComparer.Ordinal) { "test", "ai", "template", "placeholder" };
    private static readonly string[] InternalRecordPrefixes = ["invisible", "mannequin", "nodraw"];

    internal static bool IsInternalRecord(string shortName)
    {
        foreach (var w in shortName.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries))
            if (InternalRecordWords.Contains(w) || InternalRecordPrefixes.Any(p => w.StartsWith(p, StringComparison.Ordinal))) return true;
        return false;
    }

    // 店で買える (item_shop_prices に購入の行がある) 品目の uuid。表が無ければ空
    private HashSet<string> LoadSoldUuids()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT uuid FROM item_shop_prices WHERE kind = @k AND price IS NOT NULL";
            cmd.Parameters.AddWithValue("@k", ShopPriceStore.KindBuy);
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetString(0));
        }
        catch { }
        return set;
    }

    // 同じ名前・同じ種類の品目の代表。内部用の名前でないものを先に、次に店で売られているもの、次に record 名の短いもの (同じなら辞書順)
    private static CompareEntry PickRepresentative(IEnumerable<CompareEntry> group, HashSet<string> sold) =>
        group.OrderBy(e => IsInternalRecord(e.Short) ? 1 : 0)
             .ThenBy(e => sold.Contains(e.Uuid) ? 0 : 1)
             .ThenBy(e => e.Record.Length)
             .ThenBy(e => e.Record, StringComparer.Ordinal)
             .First();

    // 同じ名前の群を代表 1 件にまとめる (ほかの品目の uuid を代表の Uuids に足す)
    private static CompareEntry MergeSameName(IEnumerable<CompareEntry> group, HashSet<string> sold) =>
        MergeSameName(group, PickRepresentative(group.ToList(), sold));

    private static CompareEntry MergeSameName(IEnumerable<CompareEntry> group, CompareEntry rep)
    {
        foreach (var other in group.Where(x => !ReferenceEquals(x, rep))) { rep.Uuids.Add(other.Uuid); rep.Dups.Add(other); }
        return rep;
    }

    // 同じ名前の武器の代表 (ゲームに実在する record)。次の順に選ぶ:
    //   1. 内部用の record (IsInternalRecord) でない
    //   2. 店で買える (sold)
    //   3. 船の既定の装備 (ship_ports.equipped_item) に使われている船の数が多い
    //      (例 M9A Cannon: BEHR_JavelinBallisticCannon_S7 は Javelin など 7 隻、BEHR_LaserCannon_S7 は 2 隻、S8・S9・MassDriver_S12 は 0 隻)
    //   4. 名前のローカライズキー (AttachDef.Localization.Name の @item_Name<record 名>) がその record 自身のもの
    //      (例 GVSR Repeater: どれも既定の装備に無いが、名前のキーは BEHR_LaserRepeater_VNG_S2 のもの)
    //   5. 前の選び方 (record 名の短い順、同じなら文字の順)
    private static CompareEntry PickWeaponRepresentative(List<CompareEntry> group, HashSet<string> sold, IReadOnlyDictionary<string, int> shipUse) =>
        group.OrderBy(e => IsInternalRecord(e.Short) ? 1 : 0)
             .ThenBy(e => sold.Contains(e.Uuid) ? 0 : 1)
             .ThenByDescending(e => shipUse.TryGetValue(e.Short, out var n) ? n : 0)
             .ThenBy(e => OwnsNameKey(e) ? 0 : 1)
             .ThenBy(e => e.Record.Length)
             .ThenBy(e => e.Record, StringComparer.Ordinal)
             .First();

    // 名前のローカライズキーが品目自身の record 名のものか (@item_Name<record 名>。大小は無視)
    private static bool OwnsNameKey(CompareEntry e)
    {
        if (e.RawJson.Length == 0) return false;
        try
        {
            using var doc = JsonDocument.Parse(e.RawJson);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array) return false;
            foreach (var c in cs.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("_Type_", out var t) && t.GetString() == "SAttachableComponentParams"
                    && c.TryGetProperty("AttachDef", out var ad) && ad.TryGetProperty("Localization", out var lz)
                    && lz.TryGetProperty("Name", out var nk) && nk.ValueKind == JsonValueKind.String)
                    return string.Equals(nk.GetString(), "@item_Name" + e.Short, StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { }
        return false;
    }

    // 船の既定の装備 (ship_ports.equipped_item) の record 名 → それを使う船の数。表が無ければ空
    private Dictionary<string, int> LoadShipPortUse()
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT equipped_item, COUNT(DISTINCT ship_record_name) FROM ship_ports WHERE equipped_item IS NOT NULL AND equipped_item <> '' GROUP BY equipped_item";
            using var r = cmd.ExecuteReader();
            while (r.Read()) dict[r.GetString(0)] = r.GetInt32(1);
        }
        catch (SqliteException) { }
        return dict;
    }

    // 名前の最後の語を除いた名前 (1 語しか無ければ null)
    private static string? NameStem(string name)
    {
        var n = name.Trim();
        var idx = n.LastIndexOf(' ');
        return idx > 0 ? n[..idx].TrimEnd() : null;
    }

    // 今の基本モデルの判定のどれに当たったか
    private static string MatchRule(CompareEntry e, CompareEntry b)
    {
        if (StripQuoted(e.Name).Equals(b.Name, StringComparison.OrdinalIgnoreCase)) return "quote";
        if (e.Short.StartsWith(b.Short + "_", StringComparison.OrdinalIgnoreCase)) return "record";
        return "name";
    }

    // 色・柄の名前: 名前の "…" があればその中身、基本品目の名前で始まればその残り、名前の最後の語の判定なら最後の語、どれでもなければ名前全体
    private static string VariantLabel(CompareEntry v, CompareEntry b, string rule)
    {
        var m = System.Text.RegularExpressions.Regex.Match(v.Name, "\"([^\"]+)\"");
        if (m.Success) return m.Groups[1].Value;
        if (v.Name.StartsWith(b.Name + " ", StringComparison.OrdinalIgnoreCase)) return v.Name[(b.Name.Length + 1)..].Trim();
        if (rule == "stem") { var idx = v.Name.Trim().LastIndexOf(' '); if (idx > 0) return v.Name.Trim()[(idx + 1)..]; }
        return v.Name;
    }

    // lookup 用: 品目の性能 (ゲームデータ) と、色違いの関係。色違いなら基本品目の性能を返し、「この品目は ○○ の色違い」を付ける。
    // 基本品目に色違いがあれば、色違いの名前の一覧を付ける。items に無ければ ""。
    // japaneseNames: true なら色違いの品名を「日本語名 (英語名)」で書く (［詳細］タブ)。false (チャット) は英語名
    public string GetItemStatsText(string recordName, bool japaneseNames = false)
    {
        if (string.IsNullOrEmpty(recordName)) return "";
        using var loc = new LocReader(_translationsDbPath);
        var sb = new StringBuilder();

        // 色違いなら基本品目に置き換える。この record が表に無ければ、同じ名前・同じ種類の別の record (AI 用などの複製。
        // 色違いの表では代表の record だけが基本品目・色違いになる) で引く
        List<string>? group = null;
        List<string> Group() => group ??= SameNameRecords(recordName, loc);
        var (baseRecord, label) = VariantRow(recordName);
        if (baseRecord == null)
            foreach (var g in Group())
            {
                (baseRecord, label) = VariantRow(g);
                if (baseRecord != null) break;
            }

        // 色違いの一覧を引く基本品目
        var listBase = baseRecord ?? recordName;
        var variants = VariantNames(listBase, loc, japaneseNames);
        if (variants.Count == 0 && baseRecord == null)
            foreach (var g in Group())
            {
                var v = VariantNames(g, loc, japaneseNames);
                if (v.Count > 0) { listBase = g; variants = v; break; }
            }

        var baseName = japaneseNames ? ItemDisplayNameJaEn(listBase, loc) : ItemDisplayName(listBase, loc);
        if (baseRecord != null)
            sb.AppendLine($"【色違い】この品目は {baseName} の色違い (色・柄: {label}) です。性能は {baseName} と同じです。");
        if (variants.Count > 0)
            sb.AppendLine($"【{baseName} の色違い {variants.Count} 種 (性能は同じ)】 " + string.Join(", ", variants));

        // 性能: 色違いは基本品目のもの。そうでなければこの record のもの (無ければ同じ名前の基本品目のもの)
        var stats = BuildItemStatsLines(baseRecord ?? recordName, loc);
        if (stats.Length == 0 && baseRecord == null && listBase != recordName) stats = BuildItemStatsLines(listBase, loc);
        if (stats.Length > 0) sb.Append(stats);
        return sb.ToString();
    }

    // 色違いの表で record が色違いなら (基本品目の record, 色・柄の名前)。違えば (null, null)
    private (string? BaseRecord, string? Label) VariantRow(string recordName)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT base_record, variant_label FROM item_variants WHERE variant_record = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", recordName);
            using var r = cmd.ExecuteReader();
            if (r.Read()) return (r.GetString(0), r.GetString(1));
        }
        catch { }
        return (null, null);
    }

    // 基本品目の色違いの英語名の一覧 (重複を除いて名前順)。japaneseNames なら「日本語名 (英語名)」の一覧 (並びは英語名の順のまま)
    private List<string> VariantNames(string baseRecord, LocReader loc, bool japaneseNames = false)
    {
        var records = new List<string>();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT variant_record FROM item_variants WHERE base_record = @rn";
            cmd.Parameters.AddWithValue("@rn", baseRecord);
            using var r = cmd.ExecuteReader();
            while (r.Read()) records.Add(r.GetString(0));
        }
        catch { }
        return records.Select(x => (En: ItemDisplayName(x, loc), Name: japaneseNames ? ItemDisplayNameJaEn(x, loc) : ItemDisplayName(x, loc)))
            .Distinct().OrderBy(x => x.En, StringComparer.OrdinalIgnoreCase).Select(x => x.Name).ToList();
    }

    // 同じ名前 (英語名)・同じ種類の別の record (比べるツールで 1 つにまとめられる複製)。名前が未設定なら空
    private List<string> SameNameRecords(string recordName, LocReader loc)
    {
        var result = new List<string>();
        var name = ItemDisplayName(recordName, loc);
        string itemType = "", key = "", indexName = "";
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT x.item_type, COALESCE(i.name, ''), COALESCE(x.name, '') FROM item_index x LEFT JOIN items i ON i.record_name = x.record_name WHERE x.record_name = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", recordName);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return result;
            itemType = r.IsDBNull(0) ? "" : r.GetString(0);
            key = r.GetString(1);
            indexName = r.GetString(2);
        }
        if (ResolveItemNameEn(key, indexName, loc) == null) return result;
        var candidates = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT DISTINCT x.record_name FROM item_index x LEFT JOIN items i ON i.record_name = x.record_name
                WHERE x.item_type = @it AND x.record_name <> @rn AND ((@key <> '' AND i.name = @key) OR (@xn <> '' AND x.name = @xn))
                ORDER BY LENGTH(x.record_name), x.record_name
                """;
            cmd.Parameters.AddWithValue("@it", itemType);
            cmd.Parameters.AddWithValue("@rn", recordName);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@xn", indexName);
            using var r = cmd.ExecuteReader();
            while (r.Read()) candidates.Add(r.GetString(0));
        }
        foreach (var c in candidates)
            if (ItemDisplayName(c, loc).Equals(name, StringComparison.OrdinalIgnoreCase)) result.Add(c);
        return result;
    }

    // 品目の英語名 (items.name のローカライズキー → 翻訳 DB、無ければ item_index.name)。無ければ record 名
    private string ItemDisplayName(string recordName, LocReader loc)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT (SELECT name FROM items WHERE record_name = @rn), (SELECT name FROM item_index WHERE record_name = @rn LIMIT 1)";
        cmd.Parameters.AddWithValue("@rn", recordName);
        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            var n = ResolveItemNameEn(r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), loc);
            if (n != null) return n;
        }
        return recordName.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? recordName["EntityClassDefinition.".Length..] : recordName;
    }

    // 品目の画面用の名前「日本語名 (英語名)」(ItemDisplayName の英語名に、items.name のキーの日本語を添える。日本語が無ければ英語名だけ)
    private string ItemDisplayNameJaEn(string recordName, LocReader loc)
    {
        var en = ItemDisplayName(recordName, loc);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM items WHERE record_name = @rn";
        cmd.Parameters.AddWithValue("@rn", recordName);
        return ItemNameJaEn(en, cmd.ExecuteScalar() is string itemsName ? ResolveItemNameJa(itemsName, loc) : null);
    }

    // 品目 1 件の性能の行 (種類ごとの表の欄の順)。武器は計算式の注記も付ける
    private string BuildItemStatsLines(string recordName, LocReader loc)
    {
        string itemType, componentJson, rawJson;
        int size;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT item_type, COALESCE(size, 0), component_json, raw_json FROM items WHERE record_name = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", recordName);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return "";
            itemType = r.IsDBNull(0) ? "" : r.GetString(0);
            size = r.GetInt32(1);
            componentJson = r.IsDBNull(2) ? "" : r.GetString(2);
            rawJson = r.IsDBNull(3) ? "" : r.GetString(3);
        }
        var kind = KindForItemType(itemType);
        var shortName = recordName.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? recordName["EntityClassDefinition.".Length..] : recordName;
        var e = new CompareEntry { Record = recordName, Short = shortName, ItemType = itemType, ComponentJson = componentJson, RawJson = rawJson, Size = size > 0 ? size : SizeFromRecord(shortName) };
        FillItemStats(kind, e, loc);

        var isWeapon = kind is CompareKind.PersonalWeapon or CompareKind.ShipWeapon;
        var healing = IsHealingDevice(e);
        var sb = new StringBuilder();
        sb.AppendLine(isWeapon ? "【武器の性能 (ゲームデータ)】" : "【性能 (ゲームデータ)】");
        var keys = isWeapon
            ? new[] { "サイズ", "連射速度 (発/分)", "1回の弾数", "装弾数", "有効射程 (説明文)", "弾速", "1発のダメージ", "ビームのダメージ (毎秒)", "バースト DPS", "平均 DPS (推定)", "薬剤の注入量 (mSCU/秒)" }
            : ItemColumns(kind, new List<CompareEntry> { e }).Prepend("サイズ").Distinct().ToArray();
        int lines = 0;
        foreach (var key in keys)
        {
            if (e.Cells.TryGetValue(key, out var v))
            {
                if (key == "1回の弾数" && v == "1") continue;
                sb.AppendLine($"  {key}: {v}");
                lines++;
            }
            else if (isWeapon && !healing && key is "弾速" or "1発のダメージ" or "バースト DPS" or "平均 DPS (推定)") sb.AppendLine($"  {key}: データ無し");
        }
        if (isWeapon && !healing)
        {
            sb.AppendLine("  ※ " + WeaponDamageNote);
            foreach (var n in DpsNotes([e])) sb.AppendLine("  " + n);
        }
        else if (!isWeapon && lines == 0) return "";
        if (kind == CompareKind.PersonalWeapon && !healing) sb.Append(BuildTtkSection(componentJson, rawJson));
        return sb.ToString();
    }

    // ===== 何発で倒せるか (TTK) =====
    // プレイヤーの体力・部位の倍率・アーマーの倍率。インデックス構築で dcb query から取り込む (GameDataExtractor.ExtractPlayerDamageParamsAsync)。
    // kind: health (体力 HealthPool) / downed (ダウン後に死亡するまでのダメージ) / excess_instakill (意味未確認。注記用) /
    //       body_part (部位の倍率。name = head / torso / arm / leg) / armor (アーマーの倍率。name = DamageResistanceMacro の名前、damage_type = 種類)
    public const string PlayerDamageParamsDdl = """
        CREATE TABLE IF NOT EXISTS player_damage_params (
            kind TEXT NOT NULL,
            name TEXT NOT NULL DEFAULT '',
            damage_type TEXT NOT NULL DEFAULT '',
            value REAL NOT NULL,
            source TEXT NOT NULL DEFAULT '',
            PRIMARY KEY (kind, name, damage_type)
        );
        """;

    private sealed class TtkParams
    {
        public double Health, Downed, ExcessInstaKill;
        public string HealthSource = "", DownedSource = "", PartSource = "", ArmorSource = "";
        public Dictionary<string, double> Parts = new(StringComparer.Ordinal);
        public Dictionary<string, double> NakedParts = new(StringComparer.Ordinal);   // 素肌のときの部位の倍率 (nakedDamageMultiplier)
        public string NakedPartSource = "";
        public Dictionary<string, Dictionary<string, double>> Armor = new(StringComparer.Ordinal);
    }

    // 体力に効くダメージの種類 (ディストーション・スタンは数えない)
    private static readonly string[] HealthDamageTypes = ["Physical", "Energy", "Thermal", "Biochemical"];
    // lookup の表のアーマー (全身に同じアーマーを着ている前提)。素肌は部位の倍率に nakedDamageMultiplier を使い、アーマーの倍率は掛けない
    private static readonly (string Label, string Macro)[] TtkArmors =
        [("ライト", "LightArmor"), ("ミディアム", "MediumArmor"), ("ヘビー", "HeavyArmor")];
    private static readonly (string Label, string Part)[] TtkParts = [("頭", "head"), ("胴", "torso"), ("腕", "arm"), ("脚", "leg")];

    private const string TtkColumn = "ヘビー相手 胴 / 頭 (ダウンまで)";
    private const string TtkHeavyTorsoKey = "ヘビー胴ダウン発数";
    private const string TtkHeavyHeadKey = "ヘビー頭ダウン発数";
    // アーマー (TtkArmors のラベル) ごとの胴 / 頭でダウンまでの発数の Nums のキー。ヘビーは TtkHeavyTorsoKey / TtkHeavyHeadKey と同じ
    private static string TtkDownKey(string armorLabel, bool head) => $"{armorLabel}{(head ? "頭" : "胴")}ダウン発数";
    private const string TtkColumnNote = "「ヘビー相手 胴 / 頭 (ダウンまで)」= ヘビーアーマーの相手を胴 / 頭に当て続けてダウンさせるまでの発数 (体力に効く物理・エネルギー・熱・生化学だけで数える。距離による減衰は含まない。表は lookup で武器名を聞くと返します)。";

    private TtkParams? LoadTtkParams()
    {
        try
        {
            var p = new TtkParams();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT kind, name, damage_type, value, source FROM player_damage_params";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var kind = r.GetString(0); var name = r.GetString(1); var type = r.GetString(2); var v = r.GetDouble(3); var src = r.GetString(4);
                switch (kind)
                {
                    case "health": p.Health = v; p.HealthSource = src; break;
                    case "downed": p.Downed = v; p.DownedSource = src; break;
                    case "excess_instakill": p.ExcessInstaKill = v; break;
                    case "body_part": p.Parts[name] = v; p.PartSource = src; break;
                    case "body_part_naked": p.NakedParts[name] = v; p.NakedPartSource = src; break;
                    case "armor":
                        if (!p.Armor.TryGetValue(name, out var m)) p.Armor[name] = m = new Dictionary<string, double>(StringComparer.Ordinal);
                        m[type] = v; p.ArmorSource = src; break;
                }
            }
            if (p.Health <= 0 || TtkParts.Any(x => !p.Parts.ContainsKey(x.Part))) return null;
            return p;
        }
        catch { return null; }   // 表の無い古い DB
    }

    // 弾 1 個の種類ごとのダメージ (直撃 + 爆発)
    private static Dictionary<string, double> AmmoDamageByType(AmmoInfo a)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var src in new[] { a.Direct, a.Explosion })
            foreach (var (k, v) in src) d[k] = (d.TryGetValue(k, out var x) ? x : 0) + v;
        return d;
    }

    // 1 回の射撃で体力に入るダメージ (種類ごとにアーマーの倍率を掛け、部位の倍率・最大チャージの倍率・弾の数を掛ける)
    private static double TtkPerShot(Dictionary<string, double> dmg, Dictionary<string, double>? armor, double part, FireModeInfo m) =>
        HealthDamageTypes.Sum(t => (dmg.TryGetValue(t, out var v) ? v : 0) * (armor != null && armor.TryGetValue(t, out var am) ? am : 1))
        * part * m.ChargeDamageMult * m.Pellets;

    private static int TtkShots(double hp, double perShot) => perShot <= 0 ? 0 : (int)Math.Ceiling(hp / perShot - 1e-9);

    // n 発目を撃つ時刻 (秒)。最初の弾までの時間 (チャージ) + 前の周期の分 + 周期の中の弾の間隔 (60 ÷ 連射速度)。
    // 周期が求まらないときは連射速度の間隔だけ
    private static double TtkTime(int n, FireModeInfo m)
    {
        if (n <= 1) return m.LeadSeconds;
        var iv = m.Rate > 0 ? 60 / m.Rate : 0;
        var k = m.CycleShots > 0 ? m.CycleShots : 1;
        var cycle = m.CycleSeconds > 0 ? m.CycleSeconds : iv;
        return m.LeadSeconds + (n - 1) / k * cycle + (n - 1) % k * iv;
    }

    // 比べるツールの欄: ヘビー相手の胴 / 頭でダウンまでの発数 (射撃モードで違えばモードごと)
    // 射撃モードごとに、そのモードが撃つ弾 (第1 / 第2の弾。ModeAmmo) のダメージで数える。第2の弾が読めないモードは除く
    private static void FillTtkCell(CompareEntry e, AmmoInfo ammo, AmmoInfo? ammo2, List<FireModeInfo> rates, TtkParams p)
    {
        var dmg = AmmoDamageByType(ammo);
        if (HealthDamageTypes.All(t => !dmg.ContainsKey(t))) return;
        var dmg2 = ammo2 != null ? AmmoDamageByType(ammo2) : null;
        rates = rates.Where(m => !m.Secondary || dmg2 != null).ToList();
        Dictionary<string, double> D(FireModeInfo m) => m.Secondary ? dmg2! : dmg;
        // 武器の表 (［個人戦闘］) のライト・ミディアム相手の値: ヘビーと同じ計算 (射撃モードのうち最も少ない発数)。比べるツールの出力には使わない
        foreach (var (label, macro) in TtkArmors)
        {
            if (macro == "HeavyArmor" || !p.Armor.TryGetValue(macro, out var armor)) continue;
            var shots = rates.Select(m => (Torso: TtkShots(p.Health, TtkPerShot(D(m), armor, p.Parts["torso"], m)),
                Head: TtkShots(p.Health, TtkPerShot(D(m), armor, p.Parts["head"], m)))).ToList();
            if (shots.Count == 0 || shots.Any(x => x.Torso <= 0)) continue;
            e.Nums[TtkDownKey(label, false)] = shots.Min(x => x.Torso);
            e.Nums[TtkDownKey(label, true)] = shots.Min(x => x.Head);
        }
        p.Armor.TryGetValue("HeavyArmor", out var heavy);
        if (heavy == null) return;
        var perMode = rates.Select(m => (m.Mode,
            Torso: TtkShots(p.Health, TtkPerShot(D(m), heavy, p.Parts["torso"], m)),
            Head: TtkShots(p.Health, TtkPerShot(D(m), heavy, p.Parts["head"], m)))).ToList();
        if (perMode.Count == 0 || perMode.Any(x => x.Torso <= 0)) return;
        var distinct = perMode.Select(x => (x.Torso, x.Head)).Distinct().Count();
        e.Cells[TtkColumn] = distinct == 1
            ? $"{perMode[0].Torso} / {perMode[0].Head} 発"
            : string.Join("、", perMode.Select(x => $"{FireModeJa(x.Mode)} {x.Torso} / {x.Head}")) + " 発";
        e.Nums[TtkHeavyTorsoKey] = perMode.Min(x => x.Torso);
        e.Nums[TtkHeavyHeadKey] = perMode.Min(x => x.Head);
    }

    // lookup の武器の詳細に付ける表: アーマー (なし / アンダースーツ / ライト / ミディアム / ヘビー) × 部位 (頭・胴・腕・脚) の
    // ダウンまでの発数と時間、死亡までの発数 (射撃モードごと)
    private string BuildTtkSection(string componentJson, string rawJson)
    {
        var sb = new StringBuilder();
        var p = LoadTtkParams();
        var ammo = ReadWeaponAmmo(componentJson, rawJson);
        var rates = ReadFireRates(componentJson);
        // 第2の弾を撃つモードは、その弾のダメージで数える (第2の弾が読めないモードは出さない)
        var ammo2 = rates.Any(x => x.Secondary) ? ReadWeaponAmmo(componentJson, rawJson, secondary: true) : null;
        rates = rates.Where(m => !m.Secondary || ammo2 != null).ToList();
        var dmg2 = ammo2 != null ? AmmoDamageByType(ammo2) : null;
        if (ReadBeamDamage(componentJson).Count > 0 && ammo == null) return "";
        sb.AppendLine("【何発で倒せるか (ゲームデータから計算)】");
        if (p == null) { sb.AppendLine("  データ無し: 体力・部位・アーマーの倍率がありません。設定タブの［インデックス構築］を実行すると入ります。"); return sb.ToString(); }
        if (ammo == null || rates.Count == 0) { sb.AppendLine("  データ無し: 弾のデータか連射速度がありません。"); return sb.ToString(); }
        var dmg = AmmoDamageByType(ammo);
        var health = HealthDamageTypes.Where(t => dmg.ContainsKey(t)).ToList();
        if (health.Count == 0) { sb.AppendLine("  体力に効くダメージ (物理・エネルギー・熱・生化学) がない武器です。"); return sb.ToString(); }
        string Label(string key) => DamageTypeLabels.FirstOrDefault(x => x.Key == key).Label ?? key;
        var healthSum = health.Sum(t => dmg[t]);
        var others = dmg.Where(kv => !HealthDamageTypes.Contains(kv.Key)).ToList();
        sb.AppendLine($"  体力 {p.Health:0.##} ({p.HealthSource})。体力 0 でダウン、ダウン後さらに {p.Downed:0.##} のダメージで死亡 ({p.DownedSource})。");
        sb.AppendLine($"  1発で体力に効くダメージ: {healthSum:0.##} ({string.Join(" / ", health.Select(t => $"{Label(t)} {dmg[t]:0.##}"))})" +
                      (others.Count > 0 ? $"。ほかに {string.Join(" / ", others.Select(kv => $"{Label(kv.Key)} {kv.Value:0.##}"))} (体力には効かないので数えない)" : ""));
        var hasNaked = TtkParts.All(x => p.NakedParts.ContainsKey(x.Part));
        if (hasNaked)
            sb.AppendLine("  部位の倍率 素肌 (" + p.NakedPartSource + "): " + string.Join(" / ", TtkParts.Select(x => $"{x.Label} ×{p.NakedParts[x.Part]:0.##}")));
        sb.AppendLine("  部位の倍率 アーマーあり (" + p.PartSource + "): " + string.Join(" / ", TtkParts.Select(x => $"{x.Label} ×{p.Parts[x.Part]:0.##}")));
        string ArmorText(string macro)
        {
            if (!p.Armor.TryGetValue(macro, out var m)) return "データ無し";
            var vals = health.Select(t => m.TryGetValue(t, out var v) ? v : 1).Distinct().ToList();
            return vals.Count == 1 ? $"×{vals[0]:0.###}" : string.Join(" / ", health.Select(t => $"{Label(t)} ×{(m.TryGetValue(t, out var v) ? v : 1):0.###}"));
        }
        sb.AppendLine("  アーマーの倍率 (" + p.ArmorSource + "): " + string.Join(" / ", TtkArmors.Select(a => $"{a.Label} ({a.Macro}) {ArmorText(a.Macro)}")));
        foreach (var m in rates)
        {
            var md = m.Secondary ? dmg2! : dmg;
            var extra = (m.ChargeDamageMult != 1 ? $"、最大チャージ ×{m.ChargeDamageMult:0.##}" : "") + (m.Pellets > 1 ? $"、1 回 {m.Pellets} 発" : "")
                + (m.Secondary ? $"、別の弾 1 発 {string.Join(" / ", md.Select(kv => $"{Label(kv.Key)} {kv.Value:0.##}"))}" : "");
            sb.AppendLine($"  [{FireModeJa(m.Mode)} ({m.Rate:0.##} 発/分{extra})] 各欄 = ダウンまでの発数・その弾を撃つ時刻 / 死亡までの発数");
            sb.AppendLine("  | アーマー | " + string.Join(" | ", TtkParts.Select(x => x.Label)) + " |");
            sb.AppendLine("  |---|" + string.Concat(Enumerable.Repeat("---|", TtkParts.Length)));
            string Cell(Dictionary<string, double>? armor, double part)
            {
                var per = TtkPerShot(md, armor, part, m);
                var down = TtkShots(p.Health, per);
                var dead = TtkShots(p.Health + p.Downed, per);
                return down <= 0 ? "データ無し" : $"{down} 発 {TtkTime(down, m):0.00} 秒 / {dead} 発";
            }
            // 素肌 (nakedDamageMultiplier。無い古い DB では行を出さない)
            if (hasNaked)
                sb.AppendLine($"  | 素肌 | {string.Join(" | ", TtkParts.Select(x => Cell(null, p.NakedParts[x.Part])))} |");
            foreach (var (label, macro) in TtkArmors)
            {
                if (!p.Armor.TryGetValue(macro, out var armor)) continue;
                sb.AppendLine($"  | {label} | {string.Join(" | ", TtkParts.Select(x => Cell(armor, p.Parts[x.Part])))} |");
            }
        }
        var drop = ReadDamageDropMinDistance(ammo.RecordName);
        sb.AppendLine("  ※ 前提: 全身に同じアーマーを着ている (素肌は何も着ていない)。同じ部位に全弾命中" + (rates.Any(x => x.Pellets > 1) ? " (散弾は全ペレット命中)" : "") +
                      (rates.Any(x => x.ChargeDamageMult != 1) ? "、チャージ式は最大までチャージ" : "") +
                      "。時刻は 1 発目を 0 秒 (チャージ式はチャージ時間) とし、バースト・チャージは 1 周期 (撃った後の待ち・チャージを含む) で積み上げる。");
        sb.AppendLine("  ※ 死亡までの発数は、ダウン後のダメージにも同じ部位・アーマーの倍率が掛かる前提で、体力とダウン後のダメージの合計 " +
                      $"({p.Health:0.##} + {p.Downed:0.##}) を 1 発のダメージで割る (ダウンさせた弾の余りはダウン後のダメージに持ち越す: HealthPool の linkedStats の LinkedStatPassExcessBelowThreshold)。" +
                      (p.ExcessInstaKill > 0 ? $"excessDamageToInstaKill ({p.ExcessInstaKill:0.##}) は意味が未確認なので使っていない。" : ""));
        sb.AppendLine("  ※ 距離による減衰は含まない" + (drop.Count > 0 ? $" (ダメージが減り始める距離 damageDropMinDistance: {string.Join(" / ", drop.Select(kv => $"{Label(kv.Key)} {kv.Value:0.#} m"))})" : "") + "。");
        return sb.ToString();
    }

    // 弾のダメージが減り始める距離 (ammo_params.raw_json の projectileParams.damageDropParams.damageDropMinDistance。0 の種類は除く)
    private Dictionary<string, double> ReadDamageDropMinDistance(string ammoRecord)
    {
        var d = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT raw_json FROM ammo_params WHERE record_name = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", ammoRecord);
            if (cmd.ExecuteScalar() is not string raw || raw.Length == 0) return d;
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("projectileParams", out var pp) && pp.ValueKind == JsonValueKind.Object
                && pp.TryGetProperty("damageDropParams", out var dd) && dd.ValueKind == JsonValueKind.Object
                && dd.TryGetProperty("damageDropMinDistance", out var md) && md.ValueKind == JsonValueKind.Object)
                foreach (var (key, _) in DamageTypeLabels)
                    if (TryNum(md, "Damage" + key, out var v) && v > 0) d[key] = v;
        }
        catch { }
        return d;
    }

    // 船・車両の比較。ships 表を英語名 (name_en) ごとに 1 行にまとめる (AI 用などの複製の record は同じ名前なので 1 つになる)
    private (string, List<string>) CompareShips(CompareCategory cat, int? sizeFilter, Func<string, string, ShipCargo.Info?>? shipScuLookup, LocReader loc, List<string> used)
    {
        var sizeLabel = sizeFilter.HasValue ? $" サイズ{sizeFilter.Value}" : "";
        if (!ShipsHasNameEn())
            return ($"=== 比較: {cat.Label} ===\nデータ無し: 船の英語名 (ships.name_en) がありません。設定タブの［インデックス構築］を実行してください。", used);

        using var cmd = _conn.CreateCommand();
        var where = BuildFilterWhere(cmd, cat.Filters);
        if (cat.ShipRoleLike != null) { where += " AND s.role LIKE @role"; cmd.Parameters.AddWithValue("@role", $"%{cat.ShipRoleLike}%"); }
        if (cat.ShipCareerLike != null) { where += " AND s.career LIKE @career"; cmd.Parameters.AddWithValue("@career", $"%{cat.ShipCareerLike}%"); }
        cmd.CommandText = $"""
            SELECT s.record_name, s.name_en, s.career, s.role, COALESCE(s.crew_size, 0), COALESCE(s.size, 0), x.uuid
            FROM ships s JOIN item_index x ON x.record_name = s.record_name
            WHERE {where}
            ORDER BY LENGTH(s.record_name), s.record_name
            """;
        var groups = new Dictionary<string, (CompareEntry Entry, string Career, string Role, int Crew)>(StringComparer.OrdinalIgnoreCase);
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                var nameEn = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (nameEn.Length == 0 || nameEn.StartsWith("<=", StringComparison.Ordinal) || nameEn.StartsWith('@')) continue;
                var size = r.GetInt32(5);
                if (sizeFilter.HasValue && size != sizeFilter.Value) continue;
                var uuid = r.GetString(6);
                if (groups.TryGetValue(nameEn, out var g)) { g.Entry.Uuids.Add(uuid); continue; }
                var e = new CompareEntry { Uuid = uuid, Record = r.GetString(0), Name = nameEn, Size = size };
                e.Uuids.Add(uuid);
                groups[nameEn] = (e, r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3), r.GetInt32(4));
            }
        }
        if (groups.Count == 0)
            return ($"=== 比較: {cat.Label}{sizeLabel} ===\nデータ無し: ゲームデータ (ships) に該当する船・車両がありません。", used);

        var shopRows = ShopPriceStore.LoadMany(_conn, groups.Values.SelectMany(g => g.Entry.Uuids));
        foreach (var (e, career, role, crew) in groups.Values)
        {
            e.Cells["種別"] = loc.Text(role);
            e.Cells["職業"] = loc.Text(career);
            if (crew > 0) { e.Cells["乗員"] = crew.ToString(); e.Nums["乗員"] = crew; }
            if (e.Size > 0) e.Cells["サイズ"] = e.Size.ToString();
            var scu = shipScuLookup?.Invoke(e.Record, e.Name);
            if (scu is { } s && s.Scu > 0) { e.Cells["貨物積載量 (SCU)"] = ShipCargo.Format(s.Scu); e.Nums["貨物積載量"] = s.Scu; }
            FillItemPrice(e, shopRows, null);
        }

        var roots = groups.Values.Select(g => g.Entry).ToList();
        var cols = new List<string> { "種別", "職業", "乗員", "サイズ", "貨物積載量 (SCU)" };
        var sb = new StringBuilder();
        sb.AppendLine($"=== 比較: {cat.Label}{sizeLabel} — {roots.Count} 種類 ===");
        sb.AppendLine("出典: 種別・乗員・サイズ = ゲームデータ (DB の ships 表)。貨物積載量 = ゲームデータから計算した値 (無ければ UEX の船データ、それも無ければ RSI Ship Matrix)。店 = UEX の購入・レンタル価格 (保存データ)。");
        sb.AppendLine("| 船・車両 | " + string.Join(" | ", cols) + " | 店 |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", cols.Count + 2)));
        var orderedShips = roots.OrderByDescending(x => x.Buyable).ThenBy(x => x.Size).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var e in orderedShips)
        {
            var cells = cols.Select(c => e.Cells.TryGetValue(c, out var v) && !string.IsNullOrWhiteSpace(v) ? v : "データ無し");
            sb.AppendLine($"| {Cell(e.Name)} | {string.Join(" | ", cells.Select(Cell))} | {Cell(e.PriceText)} |");
        }
        sb.AppendLine();
        var best = "【店で買えるものの中で、主な性能ごとに最も高いもの】" + Environment.NewLine + BestLines(roots, [("貨物積載量", "貨物積載量", " SCU"), ("乗員", "乗員", " 人")]);
        sb.Append(best);

        // 一覧が長すぎるときは、主な性能の上位と件数だけの要約にする
        if (sb.Length > CompareTextLimit)
        {
            var title = $"=== 比較: {cat.Label}{sizeLabel} — 要約 ({roots.Count} 種類) ===";
            var notes = new List<string>
            {
                $"※ 全部の一覧は {CompareTextLimit:N0} 文字を超えるので、主な性能の上位 {SingleSummaryTop} 件だけを示します。" +
                (sizeFilter.HasValue ? "" : "サイズや種類 (例: 貨物船・戦闘機・採掘船) を指定すれば一覧を出せます。"),
                RankingNoteFor(CompareKind.Ship),
            };
            var shownShips = new List<CompareEntry>();
            var text = RankedSummary(CompareKind.Ship, title, null, notes, [(cat.Label, roots, null)], SingleSummaryTop, false, 0, shownShips,
                sourceNote: "出典: 種別・乗員・サイズ = ゲームデータ (DB の ships 表)。貨物積載量 = UEX の船データ。店 = UEX の購入・レンタル価格 (保存データ)。",
                variantNote: false, fixedCols: cols, nameHeader: "船・車両", tail: best);
            AddRefreshTargets(used, shownShips);
            return (text, used);
        }
        AddRefreshTargets(used, orderedShips);
        return (sb.ToString(), used);
    }

    // Filters を WHERE 句にする (別名 x = item_index)
    private static string BuildFilterWhere(SqliteCommand cmd, string[] filters)
    {
        var conds = new List<string>();
        for (int i = 0; i < filters.Length; i++)
        {
            var f = filters[i];
            if (f.EndsWith('*'))
            {
                conds.Add($"x.item_type LIKE @f{i}");
                cmd.Parameters.AddWithValue($"@f{i}", f[..^1] + "%");
            }
            else if (f.Contains('/'))
            {
                var parts = f.Split('/', 2);
                if (parts[1].StartsWith('!'))
                {
                    // "item_type/!sub_type": その item_type のうち、その sub_type ではないもの (sub_type が空も含む)
                    conds.Add($"(x.item_type = @f{i} AND COALESCE(x.sub_type, '') <> @s{i})");
                    cmd.Parameters.AddWithValue($"@f{i}", parts[0]);
                    cmd.Parameters.AddWithValue($"@s{i}", parts[1][1..]);
                }
                else
                {
                    conds.Add($"(x.item_type = @f{i} AND x.sub_type = @s{i})");
                    cmd.Parameters.AddWithValue($"@f{i}", parts[0]);
                    cmd.Parameters.AddWithValue($"@s{i}", parts[1]);
                }
            }
            else
            {
                conds.Add($"x.item_type = @f{i}");
                cmd.Parameters.AddWithValue($"@f{i}", f);
            }
        }
        return conds.Count > 0 ? "(" + string.Join(" OR ", conds) + ")" : "0";
    }

    private bool ItemIndexHasSize()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(item_index)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), "size", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex RecordSizeRegex = new(@"_S(\d{1,2})(?=_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static int SizeFromRecord(string shortName)
    {
        var m = RecordSizeRegex.Match(shortName);
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    // 英語の品目名。items.name がローカライズキー (@item_Name…) なら翻訳 DB の英語、無ければ item_index.name。
    // 名前が無い・プレースホルダ・未解決のキーなら null (比較から除外)
    private static string? ResolveItemNameEn(string itemsName, string indexName, LocReader loc)
    {
        string? name = null;
        if (itemsName.StartsWith('@')) name = loc.English(itemsName[1..]);
        else if (itemsName.Length > 0) name = itemsName;
        if (string.IsNullOrWhiteSpace(name)) name = indexName;
        name = (name ?? "").Replace(' ', ' ').Trim();
        if (name.Length == 0 || name.StartsWith('@') || name.StartsWith("<=", StringComparison.Ordinal)
            || name.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase)) return null;
        return name;
    }

    // 日本語の品目名。items.name がローカライズキー (@item_Name…) なら翻訳 DB の日本語 (LocReader.Text。"!" で始まる訳は英語として扱う)。
    // キーでない・訳が無い・プレースホルダなら null
    private static string? ResolveItemNameJa(string itemsName, LocReader loc)
    {
        if (!itemsName.StartsWith('@')) return null;
        var ja = loc.Text(itemsName);
        if (ja == itemsName) return null;
        ja = ja.Replace(' ', ' ').Trim();
        if (ja.Length == 0 || ja.StartsWith('@') || ja.StartsWith("<=", StringComparison.Ordinal)
            || ja.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || ja.Contains("プレースホルダ")) return null;
        return ja;
    }

    // 画面に出す品目名「日本語名 (英語名)」。日本語が無い・英語と同じなら英語名だけ
    private static string ItemNameJaEn(string nameEn, string? nameJa) =>
        !string.IsNullOrEmpty(nameJa) && !nameJa.Equals(nameEn, StringComparison.OrdinalIgnoreCase) ? $"{nameJa} ({nameEn})" : nameEn;

    // 基本モデルの判定。品目 e の基本モデルは、同じ item_type で名前に二重引用符 ("…") を含まない品目 b のうち、次のどれかに当たるもの:
    //   1. e の名前から "…" の部分を除いたものが b の名前と同じ (例: Atzkav "Venom" Sniper Rifle → Atzkav Sniper Rifle)
    //   2. e の record_name が b の record_name + "_" で始まる (例: gmni_sniper_ballistic_01_store02 → gmni_sniper_ballistic_01)
    //   3. e の名前が b の名前 + 空白で始まり、record_name の末尾 2 区切りを除いた部分が同じ
    //      (例: grin_utility_medium_helmet_01_01_02 "Aril Helmet Black Cherry" → grin_utility_medium_helmet_01_01_01 "Aril Helmet")
    // 複数当たれば名前の長いもの。基本モデルがさらに基本モデルを持つときはたどって一番上にする
    private static void AssignBaseModels(List<CompareEntry> entries)
    {
        var bases = entries.Where(e => !e.Name.Contains('"')).ToList();
        var direct = new Dictionary<CompareEntry, CompareEntry?>();
        foreach (var e in entries)
        {
            CompareEntry? best = null;
            var stripped = StripQuoted(e.Name);
            foreach (var b in bases)
            {
                if (ReferenceEquals(b, e) || b.ItemType != e.ItemType) continue;
                bool match = stripped.Equals(b.Name, StringComparison.OrdinalIgnoreCase)
                             || e.Short.StartsWith(b.Short + "_", StringComparison.OrdinalIgnoreCase)
                             || (e.Name.StartsWith(b.Name + " ", StringComparison.OrdinalIgnoreCase)
                                 && RecordFamily(e.Short).Equals(RecordFamily(b.Short), StringComparison.OrdinalIgnoreCase));
                if (match && (best == null || b.Name.Length > best.Name.Length)) best = b;
            }
            direct[e] = best;
        }
        // 一番上の基本モデルまでたどる。循環 (互いを基本モデルとしたもの) に当たったら、その品目自身を基本モデルとして扱う
        foreach (var e in entries)
        {
            var cur = e;
            var seen = new HashSet<CompareEntry> { e };
            bool cycle = false;
            while (direct[cur] is { } next)
            {
                if (!seen.Add(next)) { cycle = true; break; }
                cur = next;
            }
            e.Base = cycle || ReferenceEquals(cur, e) ? null : cur;
        }
    }

    private static string StripQuoted(string name) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(name, "\"[^\"]*\"", " "), @"\s+", " ").Trim();

    private static string RecordFamily(string shortName)
    {
        var parts = shortName.Split('_');
        return parts.Length >= 3 ? string.Join("_", parts.Take(parts.Length - 2)) : shortName;
    }

    // 種類ごとの性能を Cells (表示) と Nums (比べる数値) に入れる
    private void FillItemStats(CompareKind kind, CompareEntry e, LocReader loc)
    {
        JsonDocument? raw = null;
        try { if (e.RawJson.Length > 0) raw = JsonDocument.Parse(e.RawJson); } catch { }
        try
        {
            var comps = new List<JsonElement>();
            if (raw != null && raw.RootElement.TryGetProperty("_RecordValue_", out var rv)
                && rv.TryGetProperty("Components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                comps.AddRange(cs.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.Object));
            JsonElement? Comp(string type) => comps.Cast<JsonElement?>().FirstOrDefault(c => c!.Value.TryGetProperty("_Type_", out var t) && t.GetString() == type);

            // ゲームの英語の説明文の先頭の「項目: 値」の行 (例 Damage Reduction: 20% / Class: Military / Effective Range: 150m+)
            var attach = Comp("SAttachableComponentParams");
            if (attach is { } a && a.TryGetProperty("AttachDef", out var ad) && ad.TryGetProperty("Localization", out var lz)
                && lz.TryGetProperty("Description", out var dk) && dk.ValueKind == JsonValueKind.String && (dk.GetString() ?? "").StartsWith('@'))
            {
                var desc = loc.English(dk.GetString()![1..]);
                if (desc != null) e.Desc = ParseDescHeader(desc);
            }

            if (e.Size > 0) e.Cells["サイズ"] = $"S{e.Size}";
            string D(string key) => e.Desc.TryGetValue(key, out var v) ? v : "";

            switch (kind)
            {
                case CompareKind.PersonalWeapon:
                case CompareKind.ShipWeapon:
                {
                    var rates = ReadFireRates(e.ComponentJson);
                    if (rates.Count > 0)
                    {
                        e.Cells["連射速度 (発/分)"] = string.Join(" / ", rates.Select(x => $"{FireModeJa(x.Mode)} {x.Rate:0.##}"));
                        e.Nums["連射速度"] = rates.Max(x => x.Rate);
                    }
                    // 1回の弾数は、1発のダメージの弾 (第1の弾) を撃つモードのもの (PrimaryPellets。第2の弾のモードの弾数を第1の弾のダメージに掛けない)
                    var maxPellets = PrimaryPellets(rates);
                    if (rates.Count > 0) e.Cells["1回の弾数"] = maxPellets.ToString();
                    if (kind == CompareKind.PersonalWeapon)
                    {
                        var mag = ReadMagazineCapacity(e.ComponentJson);
                        if (mag > 0) { e.Cells["装弾数"] = mag.ToString(); e.Nums["装弾数"] = mag; }
                        if (D("Effective Range").Length > 0) e.Cells["有効射程 (説明文)"] = D("Effective Range");
                    }
                    // 弾のデータ (ammo_params): 弾速・1発のダメージ。DPS は射撃モードごとの 1 周期 (FireCycle) から計算する
                    var ammo = ReadWeaponAmmo(e.ComponentJson, e.RawJson);
                    // 第2の弾を撃つモードがあれば、その弾 (DPS・ダウンまでの発数はモードごとに撃つ弾のダメージで計算する)
                    var ammo2 = rates.Any(x => x.Secondary) ? ReadWeaponAmmo(e.ComponentJson, e.RawJson, secondary: true) : null;
                    if (ammo != null)
                    {
                        var chargeSpeed = rates.Select(x => x.ChargeSpeedMult).Where(m => m != 1).DefaultIfEmpty(1).Max();
                        if (ammo.Speed > 0)
                        {
                            e.Cells["弾速"] = $"{ammo.Speed:0.#} m/s" + (chargeSpeed != 1 ? $" (最大チャージで ×{chargeSpeed:0.##})" : "");
                            e.Nums["弾速"] = ammo.Speed;
                        }
                        var alpha = ammo.Total;
                        if (alpha > 0)
                        {
                            var chargeDamage = rates.Select(x => x.ChargeDamageMult).Where(m => m != 1).DefaultIfEmpty(1).Max();
                            e.Cells["1発のダメージ"] = FormatAmmoDamage(ammo) +
                                (chargeDamage != 1 ? $"、最大チャージで ×{chargeDamage:0.##} = {alpha * chargeDamage:#,0.##}" : "") +
                                (ammo2 != null ? "。" + string.Join("、", rates.Where(x => x.Secondary).Select(x => $"{FireModeJa(x.Mode)} は別の弾 {FormatAmmoDamage(ammo2)}" + (x.Pellets > 1 ? $" × {x.Pellets}発" : ""))) : "");
                            e.Nums["1発のダメージ"] = alpha;
                            if (rates.Count > 0)
                            {
                                // バースト DPS = 撃っている間の値 (待ち時間・チャージ倍率を入れない。cstone と同じ式)、
                                // 平均 DPS = 待ち時間・最大チャージの倍率を入れた推定 (WeaponDps)
                                // モードごとに、そのモードが撃つ弾のダメージで計算する (第2の弾が読めないモードは除く)
                                var dps = rates.Where(x => ModeAmmo(x, ammo, ammo2) is { Total: > 0 })
                                    .Select(x => (x.Mode, A: ModeAmmo(x, ammo, ammo2)!.Total, R: x))
                                    .Select(x => (x.Mode, Max: x.A * x.R.Pellets * x.R.Rate / 60, Avg: WeaponDps(x.A, x.R), NoCycle: x.R.CycleSeconds <= 0, Mark: DpsMark(x.R))).ToList();
                                e.Cells["DPS (バースト / 平均)"] = string.Join("、", dps.Select(x =>
                                    $"{FireModeJa(x.Mode)} {x.Max:#,0.##} / {x.Avg:#,0.##}" + (x.NoCycle ? " (平均は待ち時間を含まない)" : "") + x.Mark));
                                e.Cells["バースト DPS"] = string.Join(" / ", dps.Select(x => $"{FireModeJa(x.Mode)} {x.Max:#,0.##}" + x.Mark));
                                e.Cells["平均 DPS (推定)"] = string.Join(" / ", dps.Select(x => $"{FireModeJa(x.Mode)} {x.Avg:#,0.##}" + (x.NoCycle ? " (待ち時間を含まない)" : "") + x.Mark));
                                e.DpsNoteCharge = rates.Any(x => x.Charged);
                                e.DpsNoteMixed = rates.Any(x => x.MixedRates);
                                e.Nums["バーストDPS"] = dps.Max(x => x.Max);
                                e.Nums["平均DPS"] = dps.Max(x => x.Avg);
                            }
                        }
                    }
                    // ビーム (SWeaponActionFireBeamParams) は弾ではなく damagePerSecond (毎秒のダメージ) を持つ。
                    // バースト DPS = damagePerSecond の種類の合計。平均 DPS は待ち (熱など) を求めないので最大と同じ値で「待ち時間を含まない」
                    var beams = ReadBeamDamage(e.ComponentJson);
                    if (beams.Count > 0)
                    {
                        var beamText = string.Join("、", beams.Select(b => $"{BeamModeLabel(b.Mode)} {b.Total:#,0.##} / {b.Total:#,0.##} (平均は待ち時間を含まない)"));
                        e.Cells["DPS (バースト / 平均)"] = e.Cells.TryGetValue("DPS (バースト / 平均)", out var prev) ? prev + "、" + beamText : beamText;
                        var beamMax = string.Join(" / ", beams.Select(b => $"{BeamModeLabel(b.Mode)} {b.Total:#,0.##}"));
                        e.Cells["バースト DPS"] = e.Cells.TryGetValue("バースト DPS", out var pm) ? pm + " / " + beamMax : beamMax;
                        var beamAvg = string.Join(" / ", beams.Select(b => $"{BeamModeLabel(b.Mode)} {b.Total:#,0.##} (待ち時間を含まない)"));
                        e.Cells["平均 DPS (推定)"] = e.Cells.TryGetValue("平均 DPS (推定)", out var pa) ? pa + " / " + beamAvg : beamAvg;
                        e.Cells["ビームのダメージ (毎秒)"] = string.Join("、", beams.Select(b => $"{FireModeJa(b.Mode)}: {FormatDamageParts(b.Damage)}"));
                        var bm = beams.Max(b => b.Total);
                        e.Nums["バーストDPS"] = Math.Max(e.Nums.TryGetValue("バーストDPS", out var m1) ? m1 : 0, bm);
                        e.Nums["平均DPS"] = Math.Max(e.Nums.TryGetValue("平均DPS", out var m2) ? m2 : 0, bm);
                    }
                    // 医療ガン (SWeaponActionFireHealingBeamParams): 回復量 (HP) はゲームデータに無く、薬剤を注入する速さ (mSCUPerSec) だけがある。
                    // ダメージとは混ぜず、別の欄に出す
                    // 種類 (個人武器は比べるツールの種類、船の武器は ShipWeaponKindOf)、弾 (船)、ダメージの種類 (個人・船)
                    FillWeaponKindCells(kind, e, ammo, Comp("SAmmoContainerComponentParams"), loc);
                    var heals = ReadHealingBeams(e.ComponentJson);
                    if (heals.Count > 0)
                    {
                        e.Cells["薬剤の注入量 (mSCU/秒)"] = string.Join(" / ", heals.Select(h => $"{FireModeJa(h.Mode)} {h.MscuPerSec:0.##}" + (h.MaxDistance > 0 ? $" (届く距離 {h.MaxDistance:0.#} m)" : "")));
                        // 医療ガンは弾のデータ (1発のダメージ・DPS・弾速) を出さない (薬剤の注入量だけ)
                        foreach (var k in new[] { "1発のダメージ", "DPS (バースト / 平均)", "バースト DPS", "平均 DPS (推定)", "弾速" }) e.Cells.Remove(k);
                        foreach (var k in new[] { "1発のダメージ", "バーストDPS", "平均DPS", "弾速" }) e.Nums.Remove(k);
                    }
                    else if (kind == CompareKind.PersonalWeapon && ammo != null && rates.Count > 0)
                    {
                        // 何発で倒せるか (ヘビー相手の胴 / 頭、ダウンまで)
                        var ttk = LoadTtkParams();
                        if (ttk != null) FillTtkCell(e, ammo, ammo2, rates, ttk);
                    }
                    break;
                }
                case CompareKind.Shield:
                    AddGradeClass(e);
                    if (Comp("SCItemShieldGeneratorParams") is { } sh)
                    {
                        if (TryNum(sh, "MaxShieldHealth", out var hp)) { e.Cells["シールド容量"] = hp.ToString("N0"); e.Nums["シールド容量"] = hp; }
                        if (TryNum(sh, "MaxShieldRegen", out var rg)) { e.Cells["回復 (毎秒)"] = rg.ToString("N0"); e.Nums["回復"] = rg; }
                        if (TryNum(sh, "DamagedRegenDelay", out var d1) && TryNum(sh, "DownedRegenDelay", out var d2))
                            e.Cells["回復開始までの時間 (被弾後 / ダウン後)"] = $"{d1:0.##}秒 / {d2:0.##}秒";
                    }
                    break;
                case CompareKind.PowerPlant:
                    AddGradeClass(e);
                    if (Comp("ItemResourceComponentParams") is { } pr && FindResourceAmount(pr, "ItemResourceDeltaGeneration", "Power") is { } pw)
                    { e.Cells["出力 (パワー)"] = pw.ToString("0.##"); e.Nums["出力"] = pw; }
                    break;
                case CompareKind.Cooler:
                    AddGradeClass(e);
                    if (Comp("ItemResourceComponentParams") is { } cr && FindResourceAmount(cr, null, "Coolant") is { } cl)
                    { e.Cells["冷却 (クーラント/秒)"] = cl.ToString("0.##"); e.Nums["冷却"] = cl; }
                    break;
                case CompareKind.QuantumDrive:
                    AddGradeClass(e);
                    if (Comp("SCItemQuantumDriveParams") is { } qd)
                    {
                        if (qd.TryGetProperty("params", out var p))
                        {
                            if (TryNum(p, "driveSpeed", out var sp)) { e.Cells["速度 (km/秒)"] = (sp / 1000).ToString("N0"); e.Nums["速度"] = sp / 1000; }
                            if (TryNum(p, "spoolUpTime", out var su)) { e.Cells["スプール時間 (秒)"] = su.ToString("0.##"); e.Nums["スプール"] = su; }
                            if (TryNum(p, "cooldownTime", out var cd)) { e.Cells["クールダウン (秒)"] = cd.ToString("0.##"); e.Nums["クールダウン"] = cd; }
                        }
                        if (TryNum(qd, "quantumFuelRequirement", out var fu)) e.Cells["燃料消費"] = fu.ToString("0.#####");
                    }
                    break;
                case CompareKind.Radar:
                    AddGradeClass(e);
                    // 感度 (SCItemRadarComponentParams.signatureDetection の 1 つ目の sensitivity。所持コンポーネントの性能の欄と同じ値)。
                    // ［船舶装備］の表の列に使う (比べるツールの列には出さない)
                    if (Comp("SCItemRadarComponentParams") is { } rd && rd.TryGetProperty("signatureDetection", out var sd)
                        && sd.ValueKind == JsonValueKind.Array && sd.GetArrayLength() > 0 && TryNum(sd[0], "sensitivity", out var sens))
                        e.Nums["感度"] = sens;
                    break;
                case CompareKind.Armor:
                {
                    if (D("Damage Reduction").Length > 0)
                    {
                        e.Cells["ダメージ軽減 (説明文)"] = D("Damage Reduction");
                        var m = System.Text.RegularExpressions.Regex.Match(D("Damage Reduction"), @"([\d.]+)\s*%");
                        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dr))
                            e.Nums["ダメージ軽減"] = dr;
                    }
                    // 温度の耐性: 説明文に無ければゲームデータ (SCItemClothingParams.TemperatureResistance) から
                    if (D("Temp. Rating").Length > 0) e.Cells["耐えられる温度"] = D("Temp. Rating");
                    else if (Comp("SCItemClothingParams") is { } cp && cp.TryGetProperty("TemperatureResistance", out var tr)
                             && TryNum(tr, "MinResistance", out var tmin) && TryNum(tr, "MaxResistance", out var tmax))
                        e.Cells["耐えられる温度"] = $"{tmin:0.#} / {tmax:0.#} °C";
                    if (D("Radiation Protection").Length > 0) e.Cells["放射線防護"] = D("Radiation Protection");
                    if (D("Carrying Capacity").Length > 0) e.Cells["所持容量 (説明文)"] = D("Carrying Capacity");
                    // 重さごとの要約で並べるための数値 (表の表示は変えない)
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (e.Cells.TryGetValue("耐えられる温度", out var temp))
                    {
                        var tm = System.Text.RegularExpressions.Regex.Match(temp, @"(-?[\d.]+)\s*/\s*(-?[\d.]+)");
                        if (tm.Success && double.TryParse(tm.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var lo)
                            && double.TryParse(tm.Groups[2].Value, System.Globalization.NumberStyles.Float, inv, out var hi))
                            e.Nums["耐えられる温度の幅"] = hi - lo;
                    }
                    var rm = System.Text.RegularExpressions.Regex.Match(D("Radiation Protection"), @"([\d.]+)");
                    if (rm.Success && double.TryParse(rm.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var rad)) e.Nums["放射線防護"] = rad;
                    var cm = System.Text.RegularExpressions.Regex.Match(D("Carrying Capacity"), @"([\d.]+)\s*([kK])?");
                    if (cm.Success && double.TryParse(cm.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out var cap))
                        e.Nums["所持容量"] = cm.Groups[2].Success ? cap * 1000 : cap;
                    var compat = D("Backpacks").Length > 0 ? D("Backpacks") : D("Core Compatibility");
                    if (compat.Length > 0) e.Cells["対応 (バックパック/コア)"] = compat;
                    break;
                }
                default:
                {
                    AddDescCell(e);
                    if (string.Equals(e.ItemType, "Missile", StringComparison.OrdinalIgnoreCase)) FillMissileCells(e);
                    break;
                }
            }
        }
        finally { raw?.Dispose(); }
    }

    // ミサイル・魚雷・ロケット 1 件の値 (raw_json の SCItemMissileParams)。無ければ null。
    //   Damage = explosionParams.damage の 0 でない種類 (DamagePhysical → Physical)、Speed = GCSParams.linearSpeed、Tracking = targetingParams.trackingSignalType
    private sealed record MissileStats(Dictionary<string, double> Damage, double Speed, string Tracking)
    {
        public double Total => Damage.Values.Sum();
    }

    private static MissileStats? ReadMissileStats(string rawJson)
    {
        if (string.IsNullOrEmpty(rawJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array) return null;
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "SCItemMissileParams") continue;
                var dmg = new Dictionary<string, double>();
                if (c.TryGetProperty("explosionParams", out var ep) && ep.ValueKind == JsonValueKind.Object
                    && ep.TryGetProperty("damage", out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.EnumerateObject())
                        if (p.Name.StartsWith("Damage", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v) && v != 0)
                            dmg[p.Name["Damage".Length..]] = v;
                var speed = c.TryGetProperty("GCSParams", out var g) && TryNum(g, "linearSpeed", out var sp) ? sp : 0;
                var tracking = c.TryGetProperty("targetingParams", out var tp) && tp.ValueKind == JsonValueKind.Object
                    && tp.TryGetProperty("trackingSignalType", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() ?? "" : "";
                return new MissileStats(dmg, speed, tracking);
            }
        }
        catch { }
        return null;
    }

    // 追尾する信号の名前。表に無い値はそのまま
    internal static string TrackingSignalLabel(string signal) => signal switch
    {
        "Infrared" => "赤外線 (IR)",
        "Electromagnetic" => "電磁 (EM)",
        "CrossSection" => "断面積 (CS)",
        _ => signal,
    };

    // 比べるツール・［船舶戦闘］のミサイルの欄: 1発のダメージ (種類の合計。1 回に 1 発なので Damage/Alpha も同じ)、ダメージの種類、弾速、追尾。
    // 「性能 (説明文)」の欄: 説明文の先頭の「項目: 値」のうちメーカー・種類以外 (それ以外の種類と、ミサイル・魚雷のロケットポッドで使う)
    private static void AddDescCell(CompareEntry e)
    {
        var pairs = e.Desc.Where(kv => !kv.Key.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase)
                                       && !kv.Key.Equals("Item Type", StringComparison.OrdinalIgnoreCase))
                          .Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        if (pairs.Count > 0) e.Cells["性能 (説明文)"] = string.Join("; ", pairs);
    }

    private void FillMissileCells(CompareEntry e)
    {
        var m = ReadMissileStats(e.RawJson);
        if (m == null) return;
        if (m.Damage.Count > 0)
        {
            var parts = string.Join(" / ", DamageTypeLabels.Where(t => m.Damage.ContainsKey(t.Key)).Select(t => $"{t.Label} {m.Damage[t.Key]:#,0.##}"));
            e.Cells["1発のダメージ"] = $"{m.Total:#,0.##} ({parts})";
            e.Nums["1発のダメージ"] = m.Total;
            e.Cells["ダメージの種類"] = string.Join("・", DamageTypeLabels.Where(t => m.Damage.ContainsKey(t.Key)).Select(t => t.Label));
        }
        if (m.Speed > 0) { e.Cells["弾速"] = $"{m.Speed:0.#} m/s"; e.Nums["弾速"] = m.Speed; }
        if (m.Tracking.Length > 0) e.Cells["追尾"] = TrackingSignalLabel(m.Tracking);
    }

    private static void AddGradeClass(CompareEntry e)
    {
        if (e.Desc.TryGetValue("Grade", out var g)) e.Cells["グレード"] = g;
        if (e.Desc.TryGetValue("Class", out var c)) e.Cells["クラス"] = c;
    }

    private static bool TryNum(JsonElement el, string name, out double v)
    {
        v = 0;
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out v);
    }

    // ItemResourceComponentParams の Online 状態から、指定した資源 (Power / Coolant) を生み出す量 (毎秒) を返す。
    // 動力装置: ItemResourceDeltaGeneration の generation (Power, SPowerSegmentResourceUnit.units)
    // 冷却装置: ItemResourceDeltaConversion の generation (Coolant, SStandardResourceUnit.standardResourceUnits)
    private static double? FindResourceAmount(JsonElement comp, string? deltaType, string resource)
    {
        if (!comp.TryGetProperty("states", out var states) || states.ValueKind != JsonValueKind.Array) return null;
        foreach (var st in states.EnumerateArray())
        {
            if (st.ValueKind != JsonValueKind.Object) continue;
            if (st.TryGetProperty("name", out var n) && n.GetString() != "Online") continue;
            if (!st.TryGetProperty("deltas", out var deltas) || deltas.ValueKind != JsonValueKind.Array) continue;
            foreach (var d in deltas.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object) continue;
                if (deltaType != null && (!d.TryGetProperty("_Type_", out var t) || t.GetString() != deltaType)) continue;
                if (!d.TryGetProperty("generation", out var gen) || gen.ValueKind != JsonValueKind.Object) continue;
                if (!gen.TryGetProperty("resource", out var res) || res.GetString() != resource) continue;
                if (!gen.TryGetProperty("resourceAmountPerSecond", out var amt) || amt.ValueKind != JsonValueKind.Object) continue;
                if (TryNum(amt, "units", out var u)) return u;
                if (TryNum(amt, "standardResourceUnits", out var s)) return s;
            }
        }
        return null;
    }

    // 射撃モード 1 つ分。Rate は表に出す連射速度 (fireRate)、Pellets は 1 回に出る弾の数。
    // CycleShots / CycleSeconds は 1 周期 (FireCycle) に撃つ弾の数と秒数 (求まらなければ 0)。
    // ChargeDamageMult / ChargeSpeedMult は最大チャージ時のダメージ・弾速の倍率 (チャージ式でなければ 1)
    // LeadSeconds は最初の弾を撃つまでの時間 (チャージ式のチャージ時間。それ以外は 0)
    // Charged はチャージ式 (SWeaponActionFireChargedParams) を含むか、MixedRates は連続動作 (SWeaponActionSequenceParams) の段ごとに fireRate が違うか
    // Secondary はそのモードが第2の弾 (launchParams.projectileType = Secondary。マガジンの secondaryAmmoParamsRecord の弾) を撃つか
    // (例 Arlington Rifle: Slug は 1 発の 44mm 弾 (Primary)、もう 1 つのモードは 8 発の散弾 (Secondary))
    private sealed record FireModeInfo(string Mode, double Rate, int Pellets, int CycleShots, double CycleSeconds, double ChargeDamageMult, double ChargeSpeedMult, double LeadSeconds = 0,
        bool Charged = false, bool MixedRates = false, bool Secondary = false);

    // そのモードが撃つ弾 (Secondary なら第2の弾、それ以外は第1の弾)。無ければ null
    private static AmmoInfo? ModeAmmo(FireModeInfo m, AmmoInfo? primary, AmmoInfo? secondary) => m.Secondary ? secondary : primary;

    // 表の「1回の弾数」: 第1の弾 (1発のダメージの弾) を撃つモードのうち最も多い弾数。第1の弾を撃つモードが無ければ全モードのうち最も多い数。モードが無ければ 1
    private static int PrimaryPellets(List<FireModeInfo> rates)
    {
        var prim = rates.Where(x => !x.Secondary).ToList();
        var src = prim.Count > 0 ? prim : rates;
        return src.Count > 0 ? src.Max(x => x.Pellets) : 1;
    }

    // DPS (毎秒のダメージ)。1 周期が求まれば
    //   1発のダメージ × チャージ倍率 × 1回の弾数 × 1 周期に撃つ弾の数 ÷ 1 周期の秒数、
    // 求まらなければ 1発のダメージ × 1回の弾数 × 連射速度 ÷ 60 (待ち時間を含まない)
    private static double WeaponDps(double alpha, FireModeInfo m) =>
        m.CycleSeconds > 0 && m.CycleShots > 0
            ? alpha * m.ChargeDamageMult * m.Pellets * m.CycleShots / m.CycleSeconds
            : alpha * m.Pellets * m.Rate / 60;

    // 射撃モードの 1 周期 (撃つ弾の数, 秒数, 最大チャージ時のダメージ倍率, 弾速倍率)。component_json の fireActions の型ごとに:
    //   単発・連射 (SWeaponActionFireSingleParams / FireRapidParams): 1 発, 60 ÷ fireRate 秒
    //   バースト (SWeaponActionFireBurstParams): shotCount 発, shotCount × 60 ÷ fireRate + cooldownTime 秒 (撃った後の待ち)
    //   チャージ式 (SWeaponActionFireChargedParams): 中の weaponAction の周期に chargeTime (チャージにかかる秒) と cooldownTime を足す。
    //     最大チャージのダメージ・弾速の倍率は maxChargeModifier.damageMultiplier / projectileSpeedMultiplier (最大までチャージして撃つ前提)。
    //     maxChargeModifier.burstShots があれば、中のバーストの弾数に足す (例: Custodian SMG は 3 + 8 発)
    //   条件で切り替わる動作 (SWeaponActionDynamicConditionParams): 通常時の defaultWeaponAction の周期
    //   連続動作 (SWeaponActionSequenceParams): sequenceEntries の各段について、待ち (delay。unit が Seconds なら秒、RPM なら 60 ÷ delay 秒) と
    //     中の weaponAction の周期の長い方 × repetitions を足し、弾の数も足す
    // 求まらない型 (ビーム等) は null
    // 戻り値の Lead は 1 周期の始めから最初の弾までの秒数 (チャージ時間。中の動作の Lead も足す)
    private static (int Shots, double Seconds, double DamageMult, double SpeedMult, double Lead)? FireCycle(JsonElement a, int depth = 0, int extraBurstShots = 0)
    {
        if (depth > 6 || a.ValueKind != JsonValueKind.Object) return null;
        var type = a.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "SWeaponActionFireSingleParams":
            case "SWeaponActionFireRapidParams":
                return TryNum(a, "fireRate", out var r1) && r1 > 0 ? (1, 60 / r1, 1, 1, 0) : null;
            case "SWeaponActionFireBurstParams":
            {
                if (!TryNum(a, "fireRate", out var r2) || r2 <= 0) return null;
                var n = (TryNum(a, "shotCount", out var sc) && sc >= 1 ? (int)sc : 1) + Math.Max(0, extraBurstShots);
                var cool = TryNum(a, "cooldownTime", out var cd) ? cd : 0;
                return (n, n * 60 / r2 + cool, 1, 1, 0);
            }
            case "SWeaponActionFireChargedParams":
            {
                double dmg = 1, spd = 1;
                int burstShots = 0;
                if (a.TryGetProperty("maxChargeModifier", out var mm) && mm.ValueKind == JsonValueKind.Object)
                {
                    if (TryNum(mm, "damageMultiplier", out var dm) && dm > 0) dmg = dm;
                    if (TryNum(mm, "projectileSpeedMultiplier", out var sm) && sm > 0) spd = sm;
                    if (TryNum(mm, "burstShots", out var bs) && bs > 0) burstShots = (int)bs;
                }
                if (!a.TryGetProperty("weaponAction", out var inner) || FireCycle(inner, depth + 1, burstShots) is not { } ic) return null;
                var charge = TryNum(a, "chargeTime", out var ct) ? ct : 0;
                var cool = TryNum(a, "cooldownTime", out var cd) ? cd : 0;
                return (ic.Shots, charge + ic.Seconds + cool, ic.DamageMult * dmg, ic.SpeedMult * spd, charge + ic.Lead);
            }
            case "SWeaponActionSequenceParams":
            {
                if (!a.TryGetProperty("sequenceEntries", out var entries) || entries.ValueKind != JsonValueKind.Array) return null;
                int shots = 0;
                double seconds = 0, dmg = 1, spd = 1, lead = 0;
                bool first = true;
                foreach (var en in entries.EnumerateArray())
                {
                    if (en.ValueKind != JsonValueKind.Object || !en.TryGetProperty("weaponAction", out var wa) || FireCycle(wa, depth + 1) is not { } ic) continue;
                    var delay = TryNum(en, "delay", out var d) ? d : 0;
                    var unit = en.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
                    var delaySec = unit == "RPM" ? (delay > 0 ? 60 / delay : 0) : delay;
                    var reps = TryNum(en, "repetitions", out var rp) && rp >= 1 ? (int)rp : 1;
                    if (first) { lead = ic.Lead; first = false; }
                    shots += ic.Shots * reps;
                    seconds += Math.Max(delaySec, ic.Seconds) * reps;
                    dmg = ic.DamageMult;
                    spd = ic.SpeedMult;
                }
                return shots > 0 && seconds > 0 ? (shots, seconds, dmg, spd, lead) : null;
            }
            case "SWeaponActionDynamicConditionParams":
                // 条件で切り替わる動作は、通常時の動作 (defaultWeaponAction) の周期を使う
                return a.TryGetProperty("defaultWeaponAction", out var def) ? FireCycle(def, depth + 1, extraBurstShots) : null;
            default:
                return null;
        }
    }

    // 武器の射撃モードごとの連射速度 (発/分)・1 回に出る弾の数・1 周期。連射速度は component_json の fireActions の fireRate
    // (チャージ式・連続動作は中の weaponAction / sequenceEntries / weaponActions の fireRate)。
    // 弾の数は fireRate と同じ場所の launchParams.pelletCount (散弾銃は 2 以上。無ければ 1)。
    // 条件で切り替わる動作 (SWeaponActionDynamicConditionParams) は、通常時の動作 (defaultWeaponAction。モードの名前は外側の name) と、
    // 条件のときの動作 (conditionalWeaponActions の各 weaponAction。名前は中の name、無ければ「外側の name (条件)」) を別のモードとして数える
    // (例 Prism Laser Shotgun: 通常時は 8 発の散弾、熱が 0.4 以上のときは Slug)
    private static List<FireModeInfo> ReadFireRates(string componentJson)
    {
        var list = new List<FireModeInfo>();
        if (string.IsNullOrEmpty(componentJson)) return list;
        try
        {
            using var doc = JsonDocument.Parse(componentJson);
            if (!doc.RootElement.TryGetProperty("fireActions", out var fas) || fas.ValueKind != JsonValueKind.Array) return list;
            void Add(JsonElement act, string mode)
            {
                int pellets = 1;
                bool secondary = false;
                var rate = FindFireRate(act, 0, ref pellets, ref secondary);
                if (rate <= 0) return;
                var cycle = FireCycle(act);
                var seqRates = new List<double>();
                CollectSequenceRates(act, 0, seqRates);
                list.Add(new FireModeInfo(mode, rate, Math.Max(1, pellets),
                    cycle?.Shots ?? 0, cycle?.Seconds ?? 0, cycle?.DamageMult ?? 1, cycle?.SpeedMult ?? 1, cycle?.Lead ?? 0,
                    ContainsActionType(act, "SWeaponActionFireChargedParams", 0), seqRates.Distinct().Count() > 1, secondary));
            }
            static string? NameOf(JsonElement el) =>
                el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && (n.GetString() ?? "").Length > 0 ? n.GetString() : null;
            foreach (var fa in fas.EnumerateArray())
            {
                if (fa.ValueKind != JsonValueKind.Object) continue;
                var mode = NameOf(fa) ?? (fa.TryGetProperty("_Type_", out var t) ? t.GetString() ?? "" : "");
                Add(fa, mode);
                if (fa.TryGetProperty("_Type_", out var ft) && ft.GetString() == "SWeaponActionDynamicConditionParams"
                    && fa.TryGetProperty("conditionalWeaponActions", out var cwas) && cwas.ValueKind == JsonValueKind.Array)
                    foreach (var cwa in cwas.EnumerateArray())
                        if (cwa.ValueKind == JsonValueKind.Object && cwa.TryGetProperty("weaponAction", out var wa) && wa.ValueKind == JsonValueKind.Object)
                            Add(wa, NameOf(wa) ?? $"{mode} (条件)");
            }
        }
        catch { }
        return list;
    }

    // 動作の中に指定の型 (_Type_) があるか (weaponAction / defaultWeaponAction / sequenceEntries / weaponActions をたどる)
    private static bool ContainsActionType(JsonElement el, string type, int depth)
    {
        if (depth > 6) return false;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("_Type_", out var t) && t.GetString() == type) return true;
            foreach (var key in new[] { "weaponAction", "defaultWeaponAction", "sequenceEntries", "weaponActions" })
                if (el.TryGetProperty(key, out var child) && ContainsActionType(child, type, depth + 1)) return true;
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var c in el.EnumerateArray())
                if (ContainsActionType(c, type, depth + 1)) return true;
        return false;
    }

    // 連続動作 (SWeaponActionSequenceParams) の各段の fireRate を集める
    private static void CollectSequenceRates(JsonElement el, int depth, List<double> rates)
    {
        if (depth > 6) return;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("_Type_", out var t) && t.GetString() == "SWeaponActionSequenceParams"
                && el.TryGetProperty("sequenceEntries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var en in entries.EnumerateArray())
                {
                    if (en.ValueKind != JsonValueKind.Object || !en.TryGetProperty("weaponAction", out var wa)) continue;
                    int p = 1;
                    bool sec = false;
                    var r = FindFireRate(wa, 0, ref p, ref sec);
                    if (r > 0) rates.Add(r);
                }
                return;
            }
            foreach (var key in new[] { "weaponAction", "defaultWeaponAction", "weaponActions" })
                if (el.TryGetProperty(key, out var child)) CollectSequenceRates(child, depth + 1, rates);
        }
        else if (el.ValueKind == JsonValueKind.Array)
            foreach (var c in el.EnumerateArray()) CollectSequenceRates(c, depth + 1, rates);
    }

    // バースト DPS の注 (チャージ式・段ごとに連射速度が違う連続動作)。表・lookup では DPS の値の後ろに「(注1)」「(注2)」を付け、説明を一行出す
    private const string DpsNote1 = "注1 = チャージ式: バースト DPS にチャージの倍率を入れていない (平均 DPS は入れているので、バーストより大きくなることがある)。";
    private const string DpsNote2 = "注2 = 段ごとに連射速度が変わる連続動作: バースト DPS と連射速度は最初の段の値 (平均 DPS は全部の段を含む)。";
    private static string DpsMark(FireModeInfo m) => (m.Charged ? " (注1)" : "") + (m.MixedRates ? " (注2)" : "");

    private static IEnumerable<string> DpsNotes(IEnumerable<CompareEntry> shown)
    {
        var list = shown.ToList();
        if (list.Any(e => e.DpsNoteCharge)) yield return "※ " + DpsNote1;
        if (list.Any(e => e.DpsNoteMixed)) yield return "※ " + DpsNote2;
    }

    // fireRate と同じ場所の launchParams から、弾数 (pelletCount) と第2の弾を撃つか (projectileType が Secondary) も読む
    private static double FindFireRate(JsonElement el, int depth, ref int pellets, ref bool secondary)
    {
        if (depth > 6) return 0;
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (TryNum(el, "fireRate", out var fr) && fr > 0)
            {
                if (el.TryGetProperty("launchParams", out var lp) && lp.ValueKind == JsonValueKind.Object)
                {
                    if (TryNum(lp, "pelletCount", out var pc) && pc >= 1) pellets = (int)pc;
                    if (lp.TryGetProperty("projectileType", out var pt) && pt.ValueKind == JsonValueKind.String
                        && string.Equals(pt.GetString(), "Secondary", StringComparison.OrdinalIgnoreCase)) secondary = true;
                }
                return fr;
            }
            // 条件で切り替わる動作 (SWeaponActionDynamicConditionParams。例: Zenith の Parallax) は通常時の defaultWeaponAction を見る
            foreach (var key in new[] { "weaponAction", "defaultWeaponAction", "sequenceEntries", "weaponActions" })
                if (el.TryGetProperty(key, out var child))
                {
                    var r = FindFireRate(child, depth + 1, ref pellets, ref secondary);
                    if (r > 0) return r;
                }
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in el.EnumerateArray())
            {
                var r = FindFireRate(c, depth + 1, ref pellets, ref secondary);
                if (r > 0) return r;
            }
        }
        return 0;
    }

    // fireActions の中のビーム (SWeaponActionFireBeamParams。条件で切り替わる動作の中も含む) の毎秒のダメージ (damagePerSecond)
    private static List<(string Mode, Dictionary<string, double> Damage, double Total)> ReadBeamDamage(string componentJson)
    {
        var list = new List<(string, Dictionary<string, double>, double)>();
        foreach (var (mode, el) in FindActions(componentJson, "SWeaponActionFireBeamParams"))
        {
            if (!el.TryGetProperty("damagePerSecond", out var dps) || dps.ValueKind != JsonValueKind.Object) continue;
            var d = new Dictionary<string, double>();
            foreach (var (key, _) in DamageTypeLabels)
                if (TryNum(dps, "Damage" + key, out var v) && v != 0) d[key] = v;
            if (d.Count > 0) list.Add((mode, d, d.Values.Sum()));
        }
        return list;
    }

    // fireActions の中の医療ビーム (SWeaponActionFireHealingBeamParams) の薬剤の注入量 (mSCUPerSec) と届く距離 (maxDistance)
    private static List<(string Mode, double MscuPerSec, double MaxDistance)> ReadHealingBeams(string componentJson)
    {
        var list = new List<(string, double, double)>();
        foreach (var (mode, el) in FindActions(componentJson, "SWeaponActionFireHealingBeamParams"))
            if (TryNum(el, "mSCUPerSec", out var m) && m > 0)
                list.Add((mode, m, TryNum(el, "maxDistance", out var md) ? md : 0));
        return list;
    }

    // fireActions を (入れ子の動作も含めて) たどり、指定した型の動作を (名前, 要素) で返す。名前が無ければ外側の動作の名前
    private static List<(string Mode, JsonElement Element)> FindActions(string componentJson, string type)
    {
        var list = new List<(string, JsonElement)>();
        if (string.IsNullOrEmpty(componentJson)) return list;
        try
        {
            using var doc = JsonDocument.Parse(componentJson);
            if (!doc.RootElement.TryGetProperty("fireActions", out var fas) || fas.ValueKind != JsonValueKind.Array) return list;
            void Walk(JsonElement el, string outerName, int depth)
            {
                if (depth > 8) return;
                if (el.ValueKind == JsonValueKind.Array) { foreach (var c in el.EnumerateArray()) Walk(c, outerName, depth + 1); return; }
                if (el.ValueKind != JsonValueKind.Object) return;
                var name = el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && (n.GetString() ?? "").Length > 0 ? n.GetString()! : outerName;
                if (el.TryGetProperty("_Type_", out var t) && t.GetString() == type) list.Add((name, el.Clone()));
                foreach (var key in new[] { "weaponAction", "defaultWeaponAction", "conditionalWeaponActions", "sequenceEntries", "weaponActions" })
                    if (el.TryGetProperty(key, out var child)) Walk(child, name, depth + 1);
            }
            foreach (var fa in fas.EnumerateArray()) Walk(fa, "", 0);
        }
        catch { }
        return list;
    }

    private static string FormatDamageParts(Dictionary<string, double> d) =>
        $"{d.Values.Sum():#,0.##} (" + string.Join(" / ", DamageTypeLabels.Where(t => d.ContainsKey(t.Key)).Select(t => $"{t.Label} {d[t.Key]:#,0.##}")) + ")";

    // ビームの DPS の欄の「モード名 (ビーム)」。モード名がちょうど「ビーム」のときだけ「(ビーム)」を付けない (連射ビーム・採掘ビームなどには付ける)
    private static string BeamModeLabel(string mode)
    {
        var ja = FireModeJa(mode);
        return ja == "ビーム" ? ja : ja + " (ビーム)";
    }

    private static string FireModeJa(string mode) => mode switch
    {
        "Single" => "単発",
        "Rapid" => "連射",
        "Burst" => "バースト",
        "Charge" => "チャージ",
        "SWeaponActionSequenceParams" => "連続",   // 名前の無い連続動作 (船の武器のリピーター等)
        "Sequence" => "連続",   // 名前の付いた連続動作 (船の武器のリピーター等。同じ単発を順に撃つ。中身は上と同じ形)
        "Beam" or "Damage Beam" => "ビーム",   // ビームの射撃モード
        "RapidBeam" => "連射ビーム",
        "Repair" => "修理",
        "Heal" => "回復",
        "Salvage" => "サルベージ",
        "Mining beam" or "Mining beam and scanning" => "採掘ビーム",
        "TractorBeam" or "Detach" => "トラクタービーム",   // Detach は SWeaponActionFireTractorBeamParams の動作の名前 (マルチツール)
        "Shotgun" => "散弾",
        "Extinguisher" => "消火",
        "SelfHeal" => "自己回復",
        _ => mode
    };

    // 装弾数: component_json の ammoContainerRecord (…/magazines/xxx_mag.json) のマガジンの items 行の
    // SAmmoContainerComponentParams.maxAmmoCount。無ければ 0
    private int ReadMagazineCapacity(string componentJson)
    {
        var magRaw = LoadMagazineRaw(componentJson);
        return magRaw != null ? ReadAmmoContainer(magRaw).MaxAmmo : 0;
    }

    // 武器の component_json の ammoContainerRecord が指すマガジンの items.raw_json (個人武器)。無ければ null
    private string? LoadMagazineRaw(string componentJson)
    {
        if (string.IsNullOrEmpty(componentJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(componentJson);
            if (!doc.RootElement.TryGetProperty("ammoContainerRecord", out var ac) || ac.ValueKind != JsonValueKind.String) return null;
            var stem = Path.GetFileNameWithoutExtension(ac.GetString() ?? "");
            if (stem.Length == 0) return null;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT raw_json FROM items WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", "EntityClassDefinition." + stem);
            return cmd.ExecuteScalar() as string;
        }
        catch { return null; }
    }

    // raw_json の SAmmoContainerComponentParams の弾の参照 (ammoParamsRecord)、最大装弾数 (maxAmmoCount)、
    // 第2の弾の参照 (secondaryAmmoParamsRecord。射撃モードの launchParams.projectileType が Secondary のときに撃つ弾。無ければ null)
    private static (string? AmmoParamsRecord, int MaxAmmo, string? SecondaryAmmoParamsRecord) ReadAmmoContainer(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array)
                return (null, 0, null);
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "SAmmoContainerComponentParams") continue;
                var rec = c.TryGetProperty("ammoParamsRecord", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;
                var max = TryNum(c, "maxAmmoCount", out var m) ? (int)m : 0;
                var sec = c.TryGetProperty("secondaryAmmoParamsRecord", out var sp) && sp.ValueKind == JsonValueKind.String && (sp.GetString() ?? "").Length > 0 ? sp.GetString() : null;
                return (rec, max, sec);
            }
        }
        catch { }
        return (null, 0, null);
    }

    // 弾 1 種類のデータ (ammo_params の 1 行)。Direct は直撃、Explosion は爆発のダメージ (種類 → 値)
    private sealed record AmmoInfo(string RecordName, double Speed, Dictionary<string, double> Direct, Dictionary<string, double> Explosion)
    {
        public double Total => Direct.Values.Sum() + Explosion.Values.Sum();
    }

    // 武器の弾のデータ。武器 → 弾の参照のたどり方:
    //   個人武器: component_json の ammoContainerRecord (マガジン) → マガジンの SAmmoContainerComponentParams.ammoParamsRecord
    //   船の武器: 武器自身の raw_json の SAmmoContainerComponentParams.ammoParamsRecord
    // 参照はファイルの場所 (…/ammoparams/fps/xxx.json、…/ammoparams/vehicle/ammoparams.xxx.json) なので、ファイル名から
    // 先頭の "ammoparams." と拡張子を除いたものを AmmoParams のレコード名として ammo_params を引く。無ければ null
    // secondary = true なら第2の弾 (secondaryAmmoParamsRecord。たどり方は同じ)
    private AmmoInfo? ReadWeaponAmmo(string componentJson, string rawJson, bool secondary = false)
    {
        string? ammoPath = null;
        string? Pick(string raw) { var c = ReadAmmoContainer(raw); return secondary ? c.SecondaryAmmoParamsRecord : c.AmmoParamsRecord; }
        var magRaw = LoadMagazineRaw(componentJson);
        if (magRaw != null) ammoPath = Pick(magRaw);
        if (string.IsNullOrEmpty(ammoPath) && !string.IsNullOrEmpty(rawJson)) ammoPath = Pick(rawJson);
        if (string.IsNullOrEmpty(ammoPath)) return null;
        var stem = Path.GetFileNameWithoutExtension(ammoPath);
        if (stem.StartsWith("ammoparams.", StringComparison.OrdinalIgnoreCase)) stem = stem["ammoparams.".Length..];
        if (stem.Length == 0) return null;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, speed, damage_json, explosion_json FROM ammo_params WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", "AmmoParams." + stem);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            // ［ダメージ判定］のアタッチメントの補正 (ApplyAmmoMultipliers。補正を入れて計算している間だけ。それ以外はそのまま返す)
            return ApplyAmmoMultipliers(new AmmoInfo(r.GetString(0), r.IsDBNull(1) ? 0 : r.GetDouble(1),
                ParseDamageJson(r.IsDBNull(2) ? null : r.GetString(2)), ParseDamageJson(r.IsDBNull(3) ? null : r.GetString(3))));
        }
        catch { return null; }   // ammo_params の無い古い DB
    }

    private static Dictionary<string, double> ParseDamageJson(string? json)
    {
        var d = new Dictionary<string, double>();
        if (string.IsNullOrEmpty(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v) && v != 0) d[p.Name] = v;
        }
        catch { }
        return d;
    }

    private static readonly (string Key, string Label)[] DamageTypeLabels =
        [("Physical", "物理"), ("Energy", "エネルギー"), ("Distortion", "ディストーション"), ("Thermal", "熱"), ("Biochemical", "生化学"), ("Stun", "スタン")];

    // 「165 (エネルギー 120 / ディストーション 35 / スタン 10)」の形。爆発があれば「爆発: …」を付ける
    private static string FormatAmmoDamage(AmmoInfo a)
    {
        string Parts(Dictionary<string, double> d) => string.Join(" / ", DamageTypeLabels.Where(t => d.ContainsKey(t.Key)).Select(t => $"{t.Label} {d[t.Key]:#,0.##}"));
        var text = $"{a.Total:#,0.##}";
        var inner = new List<string>();
        if (a.Direct.Count > 0) inner.Add(Parts(a.Direct));
        if (a.Explosion.Count > 0) inner.Add("爆発: " + Parts(a.Explosion));
        return inner.Count > 0 ? $"{text} ({string.Join(" + ", inner)})" : text;
    }

    // 1発のダメージ・DPS の計算方法 (比較の表と lookup に付ける注記)
    private const string WeaponDamageNote =
        "1発のダメージ = 弾 1 個の直撃 (+爆発) ダメージの種類 (物理・エネルギー・ディストーション・熱・生化学・スタン) の合計。" +
        "バースト DPS = 撃っている間の値 = 1発のダメージ × 1回の弾数 × 連射速度 ÷ 60 (待ち時間・チャージの倍率は入れない。cstone と同じ式)。" +
        "平均 DPS = ゲームデータから計算した推定 = 1発のダメージ × 最大チャージの倍率 × 1回の弾数 × 1 周期に撃つ弾の数 ÷ 1 周期の秒数 " +
        "(1 周期にはチャージ時間・バースト後の待ち・連続動作の段ごとの待ちを含む。最大までチャージして撃ち続ける前提。周期を求められないモードは待ち時間を含まない)。" +
        "どちらも射撃モードごとで、距離による減衰は含まない。" +
        "別の弾を撃つ射撃モード (例 Arlington Rifle の散弾) は、そのモードの DPS をその弾のダメージで計算する (1回の弾数の欄は 1発のダメージの弾を撃つモードのもの)。";

    // 説明文の先頭の「項目: 値」の行を読む。空行で区切られた複数のまとまりも読み (例: 武器は Class の後に空行を挟んで Effective Range)、
    // 「項目: 値」でない行 (本文) に来たら終える。改行は翻訳 DB では "\n" の 2 文字で入っている
    private static Dictionary<string, string> ParseDescHeader(string desc)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in desc.Replace("\\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var idx = line.IndexOf(':');
            if (idx <= 0 || idx > 40 || line.Length > 150)
            {
                if (dict.Count > 0) break;
                continue;
            }
            var key = line[..idx].Trim();
            var val = line[(idx + 1)..].Trim();
            if (key.Length > 0 && val.Length > 0) dict.TryAdd(key, val);
        }
        return dict;
    }

    // 表の列 (種類ごと)。値のある品目が 1 つも無い列は出さない
    private static List<string> ItemColumns(CompareKind kind, List<CompareEntry> roots)
    {
        List<string> cols = kind switch
        {
            CompareKind.PersonalWeapon => ["種類", "サイズ", "連射速度 (発/分)", "装弾数", "1回の弾数", "有効射程 (説明文)", "ダメージの種類", "1発のダメージ", "ビームのダメージ (毎秒)", "DPS (バースト / 平均)", TtkColumn, "弾速", "薬剤の注入量 (mSCU/秒)"],
            CompareKind.ShipWeapon => ["種類", "ダメージの種類", "弾", "サイズ", "連射速度 (発/分)", "1回の弾数", "弾速", "1発のダメージ", "DPS (バースト / 平均)"],
            CompareKind.Shield => ["サイズ", "グレード", "クラス", "シールド容量", "回復 (毎秒)", "回復開始までの時間 (被弾後 / ダウン後)"],
            CompareKind.PowerPlant => ["サイズ", "グレード", "クラス", "出力 (パワー)"],
            CompareKind.Cooler => ["サイズ", "グレード", "クラス", "冷却 (クーラント/秒)"],
            CompareKind.QuantumDrive => ["サイズ", "グレード", "クラス", "速度 (km/秒)", "スプール時間 (秒)", "クールダウン (秒)", "燃料消費"],
            CompareKind.Radar => ["サイズ", "グレード", "クラス"],
            CompareKind.Armor => ["ダメージ軽減 (説明文)", "耐えられる温度", "放射線防護", "所持容量 (説明文)", "対応 (バックパック/コア)"],
            // それ以外の種類。ミサイル・魚雷は FillMissileCells の値 (ダメージの種類・1発のダメージ・弾速・追尾)、
            // ロケットポッドは船の武器の値 (DPS も) を出す (値のある品目があるときだけ)
            _ => ["サイズ", "ダメージの種類", "1発のダメージ", "DPS (バースト / 平均)", "弾速", "追尾", "性能 (説明文)"],
        };
        // 武器 (個人・船) の表では、1発のダメージ・DPS・弾速は、弾のデータが無い品目は「データ無し」と出すため常に残す (医療ガンだけの表では残さない)。
        // それ以外は値の無い列を落とす
        var always = kind is not (CompareKind.PersonalWeapon or CompareKind.ShipWeapon) || (roots.Count > 0 && roots.All(IsHealingDevice))
            ? new HashSet<string>() : new HashSet<string> { "1発のダメージ", "DPS (バースト / 平均)", "弾速" };
        // 1回の弾数は、2 以上 (散弾) の品目があるときだけ出す
        return cols.Where(c => always.Contains(c)
                               || (c == "1回の弾数" ? roots.Any(e => e.Cells.TryGetValue(c, out var p) && p != "1") : roots.Any(e => e.Cells.ContainsKey(c))))
                   .ToList();
    }

    // 「店で買えるものの中で最も高いもの」に使う性能 (Nums のキー, 表示名, 単位)
    private static List<(string Key, string Label, string Unit)> BestStats(CompareKind kind) => kind switch
    {
        CompareKind.PersonalWeapon => [("バーストDPS", "バースト DPS (撃っている間)", ""), ("平均DPS", "平均 DPS (待ち時間込みの推定)", ""), ("1発のダメージ", "1発のダメージ", ""), ("弾速", "弾速", " m/s"), ("連射速度", "連射速度", " 発/分"), ("装弾数", "装弾数", " 発")],
        CompareKind.ShipWeapon => [("バーストDPS", "バースト DPS (撃っている間)", ""), ("平均DPS", "平均 DPS (待ち時間込みの推定)", ""), ("1発のダメージ", "1発のダメージ", ""), ("連射速度", "連射速度", " 発/分")],
        CompareKind.Shield => [("シールド容量", "シールド容量", ""), ("回復", "回復 (毎秒)", "")],
        CompareKind.PowerPlant => [("出力", "出力", "")],
        CompareKind.Cooler => [("冷却", "冷却", " /秒")],
        CompareKind.QuantumDrive => [("速度", "速度", " km/秒")],
        CompareKind.Armor => [("ダメージ軽減", "ダメージ軽減", "%")],
        _ => [],
    };

    private static string BestLines(List<CompareEntry> roots, List<(string Key, string Label, string Unit)> stats)
    {
        var sb = new StringBuilder();
        var buyable = roots.Where(e => e.Buyable).ToList();
        if (buyable.Count == 0)
        {
            sb.AppendLine("- 店で買えるもの (販売データのあるもの) が見つかりませんでした。");
            return sb.ToString();
        }
        if (stats.Count == 0)
        {
            sb.AppendLine("- この種類は比べられる数値データがありません (性能は説明文のみ)。");
            return sb.ToString();
        }
        foreach (var (key, label, unit) in stats)
        {
            var withVal = buyable.Where(e => e.Nums.ContainsKey(key)).ToList();
            if (withVal.Count == 0) { sb.AppendLine($"- {label}: データ無し"); continue; }
            var max = withVal.Max(e => e.Nums[key]);
            var tops = withVal.Where(e => e.Nums[key] == max).Select(e => e.DisplayName).ToList();
            sb.AppendLine($"- {label}: {string.Join(", ", tops)} ({max:#,0.##}{unit})");
        }
        return sb.ToString();
    }

    // 店の販売データ: item_shop_prices (cstone / UEX の保存データ) に購入の行があればそれ、無ければ UEX の items_prices を英語名で引く
    // 店の販売データを行に入れる。保存データ (item_shop_prices) を使ったときは、その uuid を PriceUuid に入れる
    // (取り直しの対象は、表に出した行のものだけを AddRefreshTargets で数える)
    private static void FillItemPrice(CompareEntry e, Dictionary<string, List<ShopPriceRow>> shopRows,
        IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices)
    {
        List<ShopPriceRow>? rows = null;
        foreach (var u in e.Uuids)
            if (shopRows.TryGetValue(u, out var rs) && rs.Count > 0) { rows = rs; e.PriceUuid = u; break; }

        var buys = rows?.Where(r => r.Kind == ShopPriceStore.KindBuy && r.Price.HasValue).ToList() ?? new();
        var rents = rows?.Where(r => r.Kind == ShopPriceStore.KindRent && r.Price.HasValue).ToList() ?? new();
        if (buys.Count > 0)
        {
            var min = buys.MinBy(r => r.Price!.Value)!;
            var others = buys.Select(r => r.Location).Distinct().Count() - 1;
            e.PriceText = $"店で買える: 最安 {min.Price!.Value:N0} aUEC ({min.Location}{(others > 0 ? $" ほか {others} か所" : "")}) [{SourceLabel(min.Source)}]";
            e.MinPrice = min.Price!.Value;
            e.Buyable = true;
            e.Shops = buys.Select(r => new ShopListing(r.Location, r.Price!.Value, r.Source)).ToList();
        }
        else if (uexItemPrices != null && uexItemPrices.TryGetValue(NormalizeItemName(e.Name), out var uex) && uex.Count > 0)
        {
            var min = uex.MinBy(x => x.Price);
            var others = uex.Select(x => x.Location).Distinct().Count() - 1;
            e.PriceText = $"店で買える: 最安 {min.Price:N0} aUEC ({min.Location}{(others > 0 ? $" ほか {others} か所" : "")}) [UEX]";
            e.MinPrice = min.Price;
            e.Buyable = true;
            e.Shops = uex.Select(x => new ShopListing(x.Location, x.Price, ShopListing.SourceUexItemsPrices)).ToList();
        }
        else
        {
            var note = ShopPriceStore.DisplayNote(rows?.FirstOrDefault(r => r.Kind == ShopPriceStore.KindNone)?.Note);
            e.PriceText = "店の販売データ無し" + (!string.IsNullOrEmpty(note) ? $" ({note})" : "");
        }
        if (rents.Count > 0)
        {
            var oneDay = rents.Where(r => r.RentDays == 1).ToList();
            var pick = (oneDay.Count > 0 ? oneDay : rents).MinBy(r => r.Price!.Value)!;
            e.PriceText += $" / レンタル: {pick.RentDays}日 {pick.Price!.Value:N0} aUEC〜 ({pick.Location})";
        }
    }

    private static string SourceLabel(string source) => source switch { "cstone" => "cstone", "uex" => "UEX", _ => source };

    // UEX の item_name と照合するための正規化 (大文字小文字・連続空白・ノーブレークスペースの違いを吸収)
    public static string NormalizeItemName(string name) =>
        System.Text.RegularExpressions.Regex.Replace((name ?? "").Replace(' ', ' '), @"\s+", " ").Trim().ToLowerInvariant();

    private static string Cell(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ").Replace("\t", " ");

    // 翻訳 DB (translations.db) の読み取り。1 回の比較の間、接続を開いたままにして引いた結果を覚えておく
    private sealed class LocReader : IDisposable
    {
        private readonly SqliteConnection? _c;
        private readonly Dictionary<string, (string? En, string? Ja)> _cache = new(StringComparer.Ordinal);

        public LocReader(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    _c = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                    _c.Open();
                }
            }
            catch { _c = null; }
        }

        private (string? En, string? Ja) Get(string key)
        {
            if (_cache.TryGetValue(key, out var v)) return v;
            (string?, string?) result = (null, null);
            try
            {
                if (_c != null)
                {
                    using var cmd = _c.CreateCommand();
                    cmd.CommandText = "SELECT english, japanese FROM translations WHERE key = @k OR key = @kp ORDER BY (key = @k) DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("@k", key);
                    cmd.Parameters.AddWithValue("@kp", key + ",P");
                    using var r = cmd.ExecuteReader();
                    if (r.Read()) result = (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
                }
            }
            catch { }
            _cache[key] = result;
            return result;
        }

        // displayType の英語 (例 Crossbow) と同じ英語の訳がある item_displayType_ のキーの日本語。無ければ null
        public string? DisplayTypeJapanese(string english)
        {
            var cacheKey = "\u0001displayType\u0001" + english;
            if (_cache.TryGetValue(cacheKey, out var v)) return v.Ja;
            string? ja = null;
            try
            {
                if (_c != null)
                {
                    using var cmd = _c.CreateCommand();
                    cmd.CommandText = "SELECT japanese FROM translations WHERE key LIKE 'item_displayType%' AND english = @e COLLATE NOCASE " +
                                      "AND japanese IS NOT NULL AND japanese <> '' ORDER BY key LIMIT 1";
                    cmd.Parameters.AddWithValue("@e", english.Trim());
                    ja = cmd.ExecuteScalar() as string;
                }
            }
            catch { }
            if (ja != null && (ja.StartsWith('!') || string.IsNullOrWhiteSpace(ja))) ja = null;
            _cache[cacheKey] = (english, ja);
            return ja;
        }

        // 船の種別の英語 (例 Fighter) と同じ英語の訳がある船の分類のキー (vehicle_class_ / item_ShipFocus_ / vehicle_focus_) の日本語。無ければ null
        public string? ShipClassJapanese(string english)
        {
            var cacheKey = "\u0001shipClass\u0001" + english;
            if (_cache.TryGetValue(cacheKey, out var v)) return v.Ja;
            string? ja = null;
            try
            {
                if (_c != null)
                {
                    using var cmd = _c.CreateCommand();
                    cmd.CommandText = "SELECT japanese FROM translations WHERE (key LIKE 'vehicle_class%' OR key LIKE 'item_ShipFocus%' OR key LIKE 'vehicle_focus%') " +
                                      "AND english = @e COLLATE NOCASE AND japanese IS NOT NULL AND japanese <> '' ORDER BY key LIMIT 1";
                    cmd.Parameters.AddWithValue("@e", english.Trim());
                    ja = cmd.ExecuteScalar() as string;
                }
            }
            catch { }
            if (ja != null && (ja.StartsWith('!') || string.IsNullOrWhiteSpace(ja))) ja = null;
            _cache[cacheKey] = (english, ja);
            return ja;
        }

        public string? English(string key)
        {
            var en = Get(key).En;
            return string.IsNullOrWhiteSpace(en) ? null : en;
        }

        // 「@キー」を日本語 (無ければ英語) にする。キーでなければそのまま
        public string Text(string value)
        {
            if (string.IsNullOrEmpty(value) || !value.StartsWith('@')) return value ?? "";
            var (en, ja) = Get(value[1..]);
            if (!string.IsNullOrWhiteSpace(ja) && !ja.StartsWith('!')) return ja;
            return !string.IsNullOrWhiteSpace(en) ? en : value;
        }

        public void Dispose() => _c?.Dispose();
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}

// 売り場・価格 1 行 (item_shop_prices の 1 行)。
// Kind: 購入 / レンタル / なし (売り場が無かったことの記録)。Price は aUEC (なしは null)。RentDays はレンタルの日数。
// Verified は出どころの確認日 (cstone の VERIFIED、UEX の更新日)。Note は「なし」の理由。Source は cstone / uex
public record ShopPriceRow(string Kind, string Location, double? Price, int? RentDays, string Verified, string Note, string Source);

// item_shop_prices (gamedata_cache.db) の読み書き。武器・装備は cstone、船・車両は UEX から取った売り場・価格を入れる
// 店の売り場 1 か所 (購入)。Location は保存データ・UEX の文字のまま、Source は出どころ
//   "cstone": cstone の保存データ。「星系 - 場所 - … - 店名」(" - " 区切り。星系の位置がジャンプポイントなら「星系 > X Jump Point」)
//   "uex":    item_shop_prices の UEX の行 (船・車両の店。「Vantage HUR-L1」のように店名と場所が 1 つの名前)。分けずにそのまま
//   SourceUexItemsPrices: UEX の items_prices。「星系 > 天体 > 店名 - 場所」(天体が無いものもある)
public sealed record ShopListing(string Location, double Price, string Source)
{
    public const string SourceUexItemsPrices = "uex-items";

    // (星系, 場所, 店名)。分けられない形なら null
    public (string System, string Place, string Shop)? Parts()
    {
        var loc = (Location ?? "").Trim();
        if (loc.Length == 0) return null;
        if (Source == "cstone")
        {
            var t = loc.Split(" - ");
            if (t.Length < 2) return null;
            var head = t[0];
            var system = head;
            var place = new List<string>();
            var gt = head.IndexOf(" > ", StringComparison.Ordinal);
            if (gt >= 0) { system = head[..gt]; place.Add(head[(gt + 3)..]); }
            place.AddRange(t[1..^1]);
            return (system, string.Join(" - ", Dedup(place)), t[^1]);
        }
        if (Source == SourceUexItemsPrices)
        {
            var segs = loc.Split(" > ");
            if (segs.Length < 2) return null;
            var last = segs[^1];
            var dash = last.IndexOf(" - ", StringComparison.Ordinal);
            var shop = dash >= 0 ? last[..dash] : last;
            var place = segs[1..^1].ToList();
            if (dash >= 0) place.Add(last[(dash + 3)..]);
            return (segs[0], string.Join(" / ", Dedup(place)), shop);
        }
        return null;
    }

    // 場所の語が直前と同じ (大文字小文字無視) なら 1 つにする (例 Levski / Levski → Levski。見た目だけ)
    private static List<string> Dedup(List<string> place)
    {
        var r = new List<string>();
        foreach (var p in place.Select(x => x.Trim()).Where(x => x.Length > 0))
            if (r.Count == 0 || !r[^1].Equals(p, StringComparison.OrdinalIgnoreCase)) r.Add(p);
        return r;
    }

    // ツールチップの 1 行: 「星系 / 場所 / 店名 / 値段 aUEC」。分けられないときは「売り場の文字 / 値段 aUEC」
    public string Line()
    {
        var p = Parts();
        var price = $"{Price:N0} aUEC";
        return p is { } x ? $"{x.System} / {(x.Place.Length > 0 ? x.Place : "-")} / {x.Shop} / {price}" : $"{Location} / {price}";
    }

    // 「店の最安値」のセルのツールチップ。値段の安い順に 1 行ずつ (同じ売り場は 1 行)。売り場が無ければ null
    public static string? Tooltip(IReadOnlyList<ShopListing>? shops, string note = "")
    {
        if (shops == null || shops.Count == 0) return null;
        var lines = shops.GroupBy(s => s.Location).Select(g => g.MinBy(s => s.Price)!)
            .OrderBy(s => s.Price).ThenBy(s => s.Location, StringComparer.OrdinalIgnoreCase).Select(s => s.Line()).ToList();
        var head = "星系 / 場所 / 店名 / 値段" + (note.Length > 0 ? $" ({note})" : "");
        return head + "\n" + string.Join("\n", lines);
    }
}

public static class ShopPriceStore
{
    public const string KindBuy = "購入";
    public const string KindRent = "レンタル";
    public const string KindNone = "なし";

    // cstone に品目そのものが無かったことの記録 (「なし」の行の備考)。/search/{uuid} が検索のトップページへ転送されたときに入れる。
    // 裏の取得・チャットの取り直しでは取り直さず、次のインデックス構築・装備データの再抽出 (新しいパッチ) の後に消して取り直す
    public const string CstoneNotFoundNote = "cstone に品目無し";

    // 「なし」の行の備考を表示用にする。保存済みの「cstone: 販売なし (Not Sold directly or at all)」は、括弧の英文を外す (保存データは書き換えない)
    public static string? DisplayNote(string? note) => note?.Replace(" (Not Sold directly or at all)", "");

    // 1 品目の行が「cstone に品目無し」の記録だけか
    public static bool IsCstoneNotFound(IReadOnlyCollection<ShopPriceRow> rows) =>
        rows.Count > 0 && rows.All(r => r.Kind == KindNone && r.Source == "cstone" && r.Note == CstoneNotFoundNote);

    // 「cstone に品目無し」の記録を消す (次の裏の取得で取り直す)。消した行数を返す
    public static int DeleteCstoneNotFound(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM item_shop_prices WHERE kind = @k AND source = 'cstone' AND note = @n";
        cmd.Parameters.AddWithValue("@k", KindNone);
        cmd.Parameters.AddWithValue("@n", CstoneNotFoundNote);
        return cmd.ExecuteNonQuery();
    }

    public const string TableDdl = """
        CREATE TABLE IF NOT EXISTS item_shop_prices (
            uuid TEXT NOT NULL,
            kind TEXT NOT NULL,
            location TEXT NOT NULL DEFAULT '',
            price REAL,
            rent_days INTEGER,
            verified TEXT NOT NULL DEFAULT '',
            note TEXT NOT NULL DEFAULT '',
            source TEXT NOT NULL DEFAULT '',
            fetched_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_item_shop_prices_uuid ON item_shop_prices(uuid);
        DELETE FROM item_shop_prices
         WHERE NOT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'ux_item_shop_prices_key')
           AND rowid NOT IN (SELECT MAX(rowid) FROM item_shop_prices GROUP BY uuid, kind, location, COALESCE(rent_days, -1));
        CREATE UNIQUE INDEX IF NOT EXISTS ux_item_shop_prices_key ON item_shop_prices(uuid, kind, location, COALESCE(rent_days, -1));
        """;
    // 重複しないキー: 品目 (uuid)・種類 (購入 / レンタル / なし)・売り場・レンタルの日数 (購入・なしは NULL なので -1 として扱う)。
    // 実際の行 (cstone・UEX から取った 3,919 行) でこの組が重なる行は無かった。バックアップのインポート (INSERT OR REPLACE / IGNORE) で
    // 同じ行が積み上がらないようにするため。キーの無い古い DB は、最初に開いたときに重複を消してからキーを付ける (残すのは後に入った行)

    // 価格として採用する範囲 (aUEC)。UEX のデータ (2026-10 取得) で最も高い船 890 Jump が 65,356,200、
    // 装備で最も高いもの (Flight Blade) が 3,710,940、最も安いもの (おもちゃのマガジン) が 5 なので、
    // 下限 1・上限 10 億 (最も高い船の約 15 倍) とする。範囲外 (例 9,595,587,177,776,883) は価格として読まない
    public const double MinPrice = 1;
    public const double MaxPrice = 1_000_000_000;

    // "9 029" / "1,146,600" のような表記を数値にする。範囲外・数値でなければ false
    public static bool TryParsePrice(string? text, out double price)
    {
        price = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = new string(text.Where(ch => !char.IsWhiteSpace(ch) && ch != ',').ToArray());
        if (s.Length == 0 || !s.All(ch => char.IsAsciiDigit(ch) || ch == '.')) return false;
        if (!double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (double.IsNaN(v) || v < MinPrice || v > MaxPrice) return false;
        price = v;
        return true;
    }

    // 1 品目の行 (保存した順) と取得日時 (UTC)。表が無い・行が無ければ空
    public static (List<ShopPriceRow> Rows, DateTime? FetchedAtUtc) Load(SqliteConnection conn, string uuid)
    {
        var map = LoadMany(conn, [uuid], out var fetched);
        return (map.TryGetValue(uuid, out var rows) ? rows : new List<ShopPriceRow>(), fetched.TryGetValue(uuid, out var f) ? f : null);
    }

    public static Dictionary<string, List<ShopPriceRow>> LoadMany(SqliteConnection conn, IEnumerable<string> uuids) =>
        LoadMany(conn, uuids, out _);

    public static Dictionary<string, List<ShopPriceRow>> LoadMany(SqliteConnection conn, IEnumerable<string> uuids, out Dictionary<string, DateTime> fetchedAt)
    {
        var map = new Dictionary<string, List<ShopPriceRow>>(StringComparer.Ordinal);
        fetchedAt = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var list = uuids.Where(u => !string.IsNullOrEmpty(u)).Distinct().ToList();
        try
        {
            for (int start = 0; start < list.Count; start += 500)
            {
                var chunk = list.Skip(start).Take(500).ToList();
                using var cmd = conn.CreateCommand();
                var names = new List<string>();
                for (int i = 0; i < chunk.Count; i++)
                {
                    names.Add($"@u{i}");
                    cmd.Parameters.AddWithValue($"@u{i}", chunk[i]);
                }
                cmd.CommandText = $"SELECT uuid, kind, location, price, rent_days, verified, note, source, fetched_at FROM item_shop_prices WHERE uuid IN ({string.Join(",", names)}) ORDER BY rowid";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var uuid = r.GetString(0);
                    if (!map.TryGetValue(uuid, out var rows)) map[uuid] = rows = new List<ShopPriceRow>();
                    rows.Add(new ShopPriceRow(
                        r.GetString(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        r.IsDBNull(3) ? null : r.GetDouble(3),
                        r.IsDBNull(4) ? null : r.GetInt32(4),
                        r.IsDBNull(5) ? "" : r.GetString(5),
                        r.IsDBNull(6) ? "" : r.GetString(6),
                        r.IsDBNull(7) ? "" : r.GetString(7)));
                    if (!r.IsDBNull(8) && DateTime.TryParse(r.GetString(8), System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                    {
                        var utc = dt.ToUniversalTime();
                        if (!fetchedAt.TryGetValue(uuid, out var prev) || utc > prev) fetchedAt[uuid] = utc;
                    }
                }
            }
        }
        catch { }
        return map;
    }

    // 1 品目の行を入れ替える (rows が空なら何もしない。売り場が無いときは呼び出し側が「なし」の行を渡す)
    public static void Replace(SqliteConnection conn, string uuid, IReadOnlyList<ShopPriceRow> rows, DateTime fetchedAtUtc)
    {
        if (rows.Count == 0) return;
        using var tx = conn.BeginTransaction();
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM item_shop_prices WHERE uuid = @u";
            del.Parameters.AddWithValue("@u", uuid);
            del.ExecuteNonQuery();
        }
        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO item_shop_prices (uuid, kind, location, price, rent_days, verified, note, source, fetched_at) VALUES (@u, @k, @l, @p, @d, @v, @n, @s, @t)";
            var pU = ins.Parameters.Add("@u", SqliteType.Text);
            var pK = ins.Parameters.Add("@k", SqliteType.Text);
            var pL = ins.Parameters.Add("@l", SqliteType.Text);
            var pP = ins.Parameters.Add("@p", SqliteType.Real);
            var pD = ins.Parameters.Add("@d", SqliteType.Integer);
            var pV = ins.Parameters.Add("@v", SqliteType.Text);
            var pN = ins.Parameters.Add("@n", SqliteType.Text);
            var pS = ins.Parameters.Add("@s", SqliteType.Text);
            ins.Parameters.AddWithValue("@t", fetchedAtUtc.ToString("o"));
            foreach (var row in rows)
            {
                pU.Value = uuid;
                pK.Value = row.Kind;
                pL.Value = row.Location ?? "";
                pP.Value = (object?)row.Price ?? DBNull.Value;
                pD.Value = (object?)row.RentDays ?? DBNull.Value;
                pV.Value = row.Verified ?? "";
                pN.Value = row.Note ?? "";
                pS.Value = row.Source ?? "";
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    // 取り直して変わっていなかったとき、取得日時だけ更新する
    public static void Touch(SqliteConnection conn, string uuid, DateTime fetchedAtUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE item_shop_prices SET fetched_at = @t WHERE uuid = @u";
        cmd.Parameters.AddWithValue("@t", fetchedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("@u", uuid);
        cmd.ExecuteNonQuery();
    }

    // 売り場・価格が同じか (種類・売り場・価格・レンタル日数の組の集合で比べる。確認日・備考の違いは見ない)
    public static bool SameRows(IReadOnlyList<ShopPriceRow> a, IReadOnlyList<ShopPriceRow> b)
    {
        static List<string> Keys(IReadOnlyList<ShopPriceRow> rows) => rows
            .Select(r => $"{r.Kind}\u0001{(r.Location ?? "").Trim()}\u0001{(r.Price.HasValue ? Math.Round(r.Price.Value, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}\u0001{r.RentDays}")
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        return Keys(a).SequenceEqual(Keys(b));
    }
}
