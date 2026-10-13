using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［詳細］タブの検索の候補 1 件 (item_index の 1 品目。船・車両・武器・装備のすべて)
public sealed record DetailCandidate(string Record, string Name, string ItemType, string SubType, string Manufacturer)
{
    public string Display => $"{Name}  [{GameDataQueryService.DetailTypeLabel(ItemType)}{(SubType.Length > 0 && SubType != "UNDEFINED" ? " / " + SubType : "")}]";
    public override string ToString() => Display;
}

// ［詳細］タブの分類「船舶」の 1 行 (Kind = まとめた種類の名前、Size = ships.size)
public sealed record DetailShipRow(string Record, string Name, string Kind, int Size);

public partial class GameDataQueryService
{
    // ［詳細］タブの検索。item_index のあいまい検索 (FuzzySearch: 全文検索 (trigram)、無ければ LIKE) で候補を出し、
    // 少なければ綴りの揺れ用の検索 (EditDistanceSearch) も足す。名前がプレースホルダ・空のものは除き、同じ名前・同じ種類は 1 件にする
    public List<DetailCandidate> SearchDetailCandidates(string query, int limit = 40)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new List<DetailCandidate>();
        var list = new List<DetailCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string rec, string name, string type, string sub, string mfr)
        {
            var n = (name ?? "").Trim();
            if (rec.Length == 0 || n.Length == 0 || n.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || n.Contains("プレースホルダ")) return;
            if (!seen.Add(n + "\u0001" + type)) return;
            list.Add(new DetailCandidate(rec, n, type, sub, mfr));
        }
        try
        {
            foreach (var r in FuzzySearch(q, limit * 2)) Add(r.recordName, r.name, r.itemType, r.subType, r.manufacturer);
            if (list.Count < 5)
                foreach (var r in EditDistanceSearch(q, 10)) Add(r.recordName, r.name, r.itemType, r.subType, r.manufacturer);
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        return list.Take(limit).ToList();
    }

    // 名前の行で item_index.name を括弧で添えるかを決めるための比べる形。
    // 空白 (NBSP など char.IsWhiteSpace のもの) の並びを半角空白 1 つにして前後を除き、
    // 末尾の等級の印 (item_index.name の実データの形: "[" + 市・軍・産・競・隠 + 数字 1 桁 + A〜D + "]"、例 "CSR-RP [軍1C]") を外す
    private static readonly System.Text.RegularExpressions.Regex DetailGradeTagRegex = new(@"\s*\[[市軍産競隠]\d[A-D]\]$");
    private static string DetailNameKey(string name)
    {
        var s = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return DetailGradeTagRegex.Replace(s, "");
    }

    // ［詳細］タブの本文。record 1 件 (item_index / items) について:
    //   【基本】名前 (日本語名 (英語名)。日本語が無ければ英語名)・種類・メーカー・サイズ・record 名
    //   【説明文】翻訳 DB の日本語 (無ければ英語)
    //   【船・車両】船の情報 (SearchShips。船・車両のとき)
    //   【性能】lookup と同じ (GetItemStatsText。色違いは基本品目の性能、武器は 1発・DPS・射撃モード・ダウンまでの発数など)
    //   【距離による減衰】【射程】武器の弾の damageDropParams・説明文の Effective Range・弾速 × 寿命 (WeaponFalloffText)
    //   【装着できるアタッチメント】個人武器の枠ごとに付けられるもの (WeaponAttachmentListText)
    //   【販売場所】保存データ (item_shop_prices) の購入・レンタル、無ければ uexItemPrices (星系 / 場所 / 店名 / 値段 を安い順)
    // 品目が無ければ null
    public string? GetItemDetailText(string recordName, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null)
    {
        var rec = (recordName ?? "").Trim();
        if (rec.Length == 0) return null;
        using var loc = new LocReader(_translationsDbPath);
        string? uuid = null, indexName = null, itemType = "", subType = "", indexMfr = "";
        int size = 0;
        var uuids = new List<string>();
        using (var cmd = _conn.CreateCommand())
        {
            var sizeExpr = ItemIndexHasSize() ? "COALESCE(size, 0)" : "0";
            cmd.CommandText = $"SELECT uuid, record_name, name, item_type, sub_type, manufacturer, {sizeExpr} FROM item_index WHERE record_name = @rn COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@rn", rec);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                uuids.Add(r.GetString(0));
                if (uuid != null) continue;
                uuid = r.GetString(0);
                rec = r.GetString(1);
                indexName = r.IsDBNull(2) ? null : r.GetString(2);
                itemType = r.IsDBNull(3) ? "" : r.GetString(3);
                subType = r.IsDBNull(4) ? "" : r.GetString(4);
                indexMfr = r.IsDBNull(5) ? "" : r.GetString(5);
                size = r.GetInt32(6);
            }
        }
        string itemsName = "", itemsMfr = "", componentJson = "", rawJson = "";
        int itemsSize = 0;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(name, ''), COALESCE(manufacturer, ''), COALESCE(component_json, ''), COALESCE(raw_json, ''), COALESCE(size, 0), COALESCE(item_type, '') FROM items WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", rec);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                itemsName = r.GetString(0); itemsMfr = r.GetString(1); componentJson = r.GetString(2); rawJson = r.GetString(3); itemsSize = r.GetInt32(4);
                if (itemType.Length == 0) itemType = r.GetString(5);
            }
            else if (uuid == null) return null;
        }
        var shortName = rec.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? rec["EntityClassDefinition.".Length..] : rec;
        var nameEn = ResolveItemNameEn(itemsName, indexName ?? "", loc) ?? indexName ?? shortName;
        if (size <= 0) size = itemsSize > 0 ? itemsSize : SizeFromRecord(shortName);

        // 色違いで性能の JSON を空にしてある品目は基本品目の JSON (説明文・減衰に使う)
        var (baseRecord, _) = VariantRow(rec);
        if (baseRecord != null && (componentJson.Length == 0 || rawJson.Length == 0))
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(component_json, ''), COALESCE(raw_json, '') FROM items WHERE record_name = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", baseRecord);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                if (componentJson.Length == 0) componentJson = r.GetString(0);
                if (rawJson.Length == 0) rawJson = r.GetString(1);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("【基本】");
        // item_index.name が英語名とも日本語名とも中身が違うときだけ、後ろに括弧で添える。
        // 空白の違い (前後の空白・NBSP など) と末尾の等級の印 (例 " [軍1C]") は同じものとして比べる (DetailNameKey)
        var nameJa = ResolveItemNameJa(itemsName, loc);
        var indexKey = indexName != null ? DetailNameKey(indexName) : null;
        sb.AppendLine($"  名前: {ItemNameJaEn(nameEn, nameJa)}" + (indexName != null && indexKey != DetailNameKey(nameEn) && (nameJa == null || indexKey != DetailNameKey(nameJa)) ? $" ({indexName})" : ""));
        var typeJa = DetailTypeLabel(itemType);
        sb.AppendLine($"  種類: {typeJa}" + (subType.Length > 0 && subType != "UNDEFINED" ? $" / {subType}" : "") + (typeJa != itemType ? $" ({itemType})" : ""));
        var mfr = ManufacturerName(itemsMfr.Length > 0 ? itemsMfr : indexMfr, loc);
        if (mfr.Length == 0) mfr = indexMfr;
        if (mfr.Length > 0) sb.AppendLine($"  メーカー: {mfr}");
        if (size > 0) sb.AppendLine($"  サイズ: S{size}");
        sb.AppendLine($"  record 名: {shortName}");

        var desc = DescriptionText(rawJson, loc);
        if (desc.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("【説明文】");
            foreach (var line in desc.Split('\n')) sb.AppendLine("  " + line.TrimEnd());
        }

        if (itemType.StartsWith("NOITEM_Vehicle", StringComparison.OrdinalIgnoreCase))
        {
            var ship = SearchShips(nameEn, rec);
            if (ship.Trim().Length > 0) { sb.AppendLine(); sb.AppendLine("【船・車両】"); sb.AppendLine(ship.TrimEnd()); }
        }

        var stats = GetItemStatsText(rec, japaneseNames: true);
        if (stats.Trim().Length > 0) { sb.AppendLine(); sb.AppendLine(stats.TrimEnd()); }

        if (itemType is "WeaponPersonal" or "WeaponGun")
        {
            var fall = WeaponFalloffText(componentJson, rawJson, desc);
            if (fall.Length > 0) { sb.AppendLine(); sb.Append(fall); }
        }

        if (itemType == "WeaponPersonal")
        {
            // 欄に当たる枠が無い武器 (手榴弾・近接武器など) は "" なので、章も前の空行も出さない。
            // 一覧の読み込みが例外になったとき (DB を読めない・ロック中など) は、この章だけを読み込めなかった旨の 1 行にし、ほかの章は出す
            string attach;
            try { attach = WeaponAttachmentListText(rec); }
            catch (Exception ex)
            {
                attach = "【装着できるアタッチメント】" + Environment.NewLine + $"  アタッチメントの一覧を読み込めなかった ({ex.Message})" + Environment.NewLine;
            }
            if (attach.Length > 0) { sb.AppendLine(); sb.Append(attach); }
        }

        sb.AppendLine();
        sb.Append(ShopText(uuids, nameEn, uexItemPrices));
        return sb.ToString();
    }

    // 説明文 (raw_json の SAttachableComponentParams.AttachDef.Localization.Description のキーの日本語。無ければ英語)。
    // 翻訳 DB の改行 ("\n" の 2 文字) は改行にする。無ければ ""
    private static string DescriptionText(string rawJson, LocReader loc)
    {
        if (rawJson.Length == 0) return "";
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (!doc.RootElement.TryGetProperty("_RecordValue_", out var rv) || !rv.TryGetProperty("Components", out var cs) || cs.ValueKind != JsonValueKind.Array) return "";
            foreach (var c in cs.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "SAttachableComponentParams") continue;
                if (c.TryGetProperty("AttachDef", out var ad) && ad.TryGetProperty("Localization", out var lz)
                    && lz.TryGetProperty("Description", out var dk) && dk.ValueKind == JsonValueKind.String)
                {
                    var key = dk.GetString() ?? "";
                    if (!key.StartsWith('@') || key.Equals("@LOC_EMPTY", StringComparison.OrdinalIgnoreCase)) return "";
                    var text = loc.Text(key);
                    return text == key ? "" : text.Replace("\\n", "\n").Trim();
                }
            }
        }
        catch { }
        return "";
    }

    // 武器の距離による減衰と射程。
    //   減衰: 弾 (第1の弾) の ammo_params.raw_json の projectileParams.damageDropParams。ダメージの種類ごとに
    //     damageDropMinDistance (減り始める距離)、damageDropPerMeter (1 m ごとに減る量)、damageDropMinDamage (下限)。
    //     距離 d のダメージ = d ≤ 減り始める距離なら元の値、それより先は max(下限, 元の値 - (d - 減り始める距離) × 1 m ごとに減る量)
    //   射程: 説明文の Effective Range (有効射程) と、弾が届く距離 = 弾速 (ammo_params.speed) × 寿命 (raw_json の lifetime 秒)
    // どれも無ければ ""
    private string WeaponFalloffText(string componentJson, string rawJson, string desc)
    {
        var ammo = ReadWeaponAmmo(componentJson, rawJson);
        if (ammo == null) return "";
        var sb = new StringBuilder();
        Dictionary<string, double> start = new(), perMeter = new(), floor = new();
        double lifetime = 0;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT raw_json FROM ammo_params WHERE record_name = @rn LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", ammo.RecordName);
            if (cmd.ExecuteScalar() is string raw && raw.Length > 0)
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv))
                {
                    if (TryNum(rv, "lifetime", out var lt)) lifetime = lt;
                    if (rv.TryGetProperty("projectileParams", out var pp) && pp.ValueKind == JsonValueKind.Object
                        && pp.TryGetProperty("damageDropParams", out var dd) && dd.ValueKind == JsonValueKind.Object)
                    {
                        void Read(string name, Dictionary<string, double> into)
                        {
                            if (!dd.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Object) return;
                            foreach (var (key, _) in DamageTypeLabels)
                                if (TryNum(el, "Damage" + key, out var v)) into[key] = v;
                        }
                        Read("damageDropMinDistance", start);
                        Read("damageDropPerMeter", perMeter);
                        Read("damageDropMinDamage", floor);
                    }
                }
            }
        }
        catch { }

        var dmg = AmmoDamageByType(ammo);
        // 減衰する種類: 元のダメージがあり、1 m ごとに減る量が 0 より大きいもの
        var dropping = dmg.Keys.Where(k => perMeter.TryGetValue(k, out var p) && p > 0).ToList();
        string Label(string key) => DamageTypeLabels.FirstOrDefault(x => x.Key == key).Label ?? key;
        double At(string key, double d)
        {
            var b = dmg[key];
            if (!dropping.Contains(key)) return b;
            var s = start.TryGetValue(key, out var sv) ? sv : 0;
            if (d <= s) return b;
            var f = floor.TryGetValue(key, out var fv) ? fv : 0;
            return Math.Max(f, b - (d - s) * perMeter[key]);
        }
        var maxRange = ammo.Speed > 0 && lifetime > 0 ? ammo.Speed * lifetime : 0;
        if (dropping.Count > 0)
        {
            sb.AppendLine("【距離による減衰 (弾のデータ damageDropParams)】");
            var ends = new List<double>();
            foreach (var k in dropping)
            {
                var s = start.TryGetValue(k, out var sv) ? sv : 0;
                var f = floor.TryGetValue(k, out var fv) ? fv : 0;
                var end = s + Math.Max(0, dmg[k] - f) / perMeter[k];
                ends.Add(end);
                sb.AppendLine($"  {Label(k)}: {dmg[k]:#,0.##} ({s:#,0.#} m まで) → 1 m ごとに {perMeter[k]:0.###} 減る → {f:#,0.##} ({end:#,0.#} m から先)");
            }
            // 距離ごとの 1発のダメージ (種類の合計): 0 m、減り始める距離、下限になる距離、その間を 4 等分、弾が届く距離
            var points = new SortedSet<double> { 0 };
            foreach (var k in dropping) points.Add(start.TryGetValue(k, out var sv) ? sv : 0);
            foreach (var e in ends) points.Add(Math.Round(e, 1));
            var lo = points.Where(p => p > 0).DefaultIfEmpty(0).Min();
            var hi = ends.Max();
            for (var i = 1; i < 4; i++) points.Add(Math.Round(lo + (hi - lo) * i / 4, 0));
            if (maxRange > hi) points.Add(Math.Round(maxRange, 0));
            sb.AppendLine("  | 距離 | 1発のダメージ (種類の合計) |");
            sb.AppendLine("  |---|---|");
            foreach (var d in points.Where(p => maxRange <= 0 || p <= maxRange + 0.5))
                sb.AppendLine($"  | {d:#,0.#} m | {dmg.Keys.Sum(k => At(k, d)):#,0.##} |");
            sb.AppendLine();
        }
        var eff = System.Text.RegularExpressions.Regex.Match(desc, @"(?:Effective Range|有効射程)\s*[:：]\s*([^\n]+)");
        if (eff.Success || maxRange > 0)
        {
            sb.AppendLine("【射程】");
            if (eff.Success) sb.AppendLine($"  有効射程 (説明文): {eff.Groups[1].Value.Trim()}");
            if (maxRange > 0) sb.AppendLine($"  弾が届く距離 (弾速 {ammo.Speed:#,0.#} m/s × 寿命 {lifetime:0.##} 秒): {maxRange:#,0} m");
        }
        return sb.ToString();
    }

    // 販売場所。保存データ (item_shop_prices) の購入・レンタルがあればそれ、購入が無ければ UEX の items_prices (英語名で引く)。
    // 購入は「星系 / 場所 / 店名 / 値段」を安い順に (ShopListing)、レンタルは「売り場 / 日数 / 値段」
    private string ShopText(List<string> uuids, string nameEn, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【販売場所】");
        List<ShopPriceRow> rows = new();
        try
        {
            var map = ShopPriceStore.LoadMany(_conn, uuids);
            foreach (var u in uuids) if (map.TryGetValue(u, out var rs)) rows.AddRange(rs);
        }
        catch { }
        var buys = rows.Where(r => r.Kind == ShopPriceStore.KindBuy && r.Price.HasValue).Select(r => new ShopListing(r.Location, r.Price!.Value, r.Source)).ToList();
        var source = "cstone / UEX の保存データ";
        if (buys.Count == 0 && uexItemPrices != null && uexItemPrices.TryGetValue(NormalizeItemName(nameEn), out var uex) && uex.Count > 0)
        {
            buys = uex.Select(x => new ShopListing(x.Location, x.Price, ShopListing.SourceUexItemsPrices)).ToList();
            source = "UEX";
        }
        if (buys.Count > 0)
        {
            sb.AppendLine($"  購入 ({source}): 星系 / 場所 / 店名 / 値段");
            foreach (var s in buys.GroupBy(s => s.Location).Select(g => g.MinBy(s => s.Price)!).OrderBy(s => s.Price).ThenBy(s => s.Location, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine("  " + s.Line());
        }
        var rents = rows.Where(r => r.Kind == ShopPriceStore.KindRent && r.Price.HasValue).OrderBy(r => r.Price).ToList();
        if (rents.Count > 0)
        {
            sb.AppendLine("  レンタル: 売り場 / 日数 / 値段");
            foreach (var r in rents) sb.AppendLine($"  {r.Location} / {r.RentDays}日 / {r.Price!.Value:N0} aUEC");
        }
        if (buys.Count == 0 && rents.Count == 0)
        {
            // 保存済みの「cstone: 販売なし (Not Sold directly or at all)」は、括弧の英文を外して出す (保存データは書き換えない)
            var note = ShopPriceStore.DisplayNote(rows.FirstOrDefault(r => r.Kind == ShopPriceStore.KindNone)?.Note);
            sb.AppendLine("  店の販売データ無し" + (!string.IsNullOrEmpty(note) ? $" ({note})" : ""));
        }
        return sb.ToString();
    }

    // ［詳細］タブの分類「船舶」の品目。船・車両の一覧は比べるツール (CompareShips) と同じ規則で作る:
    //   ships と item_index にある record のうち、英語名 (name_en) が空・"@" で始まる・"<=" で始まるもの (テンプレート等) を除き、
    //   英語名ごとに 1 つにまとめる (AI 用などの複製は同じ名前なので 1 つになる。record 名の短いものを代表にする)
    // 種類は船の種別 (ships.role。ゲーム内の表示 @vehicle_class_… 等) から作る (ShipKindGroups)。サイズは ships.size。
    // 船の英語名が無い古い DB では空
    public List<DetailShipRow> GetDetailShipList()
    {
        var result = new List<DetailShipRow>();
        if (!ShipsHasNameEn()) return result;
        using var loc = new LocReader(_translationsDbPath);
        var ships = new Dictionary<string, (string Record, string Name, string Role, int Size)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT s.record_name, s.name_en, COALESCE(s.role, ''), COALESCE(s.size, 0)
                FROM ships s JOIN item_index x ON x.record_name = s.record_name
                ORDER BY LENGTH(s.record_name), s.record_name
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var nameEn = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (nameEn.Length == 0 || nameEn.StartsWith("<=", StringComparison.Ordinal) || nameEn.StartsWith('@')) continue;
                if (!ships.ContainsKey(nameEn)) ships[nameEn] = (r.GetString(0), nameEn, r.GetString(2), r.GetInt32(3));
            }
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return result; }

        var kinds = ShipKindGroups(ships.Values.Select(s => s.Role), loc);
        foreach (var s in ships.Values)
            result.Add(new DetailShipRow(s.Record, s.Name, kinds.TryGetValue(s.Role, out var k) ? k : "", s.Size));
        return result.OrderBy(r => r.Kind, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 船の種別の英語に入っている大きさの語 (実データの role の英語: Light / Medium / Heavy / Snub。Small / Large は今のデータに無いが同じ扱い)
    private static readonly HashSet<string> ShipSizeWords = new(StringComparer.OrdinalIgnoreCase) { "Light", "Medium", "Heavy", "Snub", "Small", "Large" };
    // 日本語名を大きさの語を除いて作るときに外す先頭の語 (実データの訳: 軽戦闘機・中型貨物船・重サルベージ・スナブ戦闘機・中量回収・
    // スターター / 小型採掘・ヘビー・ドロップシップ。大型は今のデータに無いが同じ扱い)。長いものから順に試す
    private static readonly string[] ShipSizePrefixesJa = ["ヘビー・", "スナブ", "中型", "中量", "小型", "大型", "軽", "重", "中"];

    // 船の種別の英語 (ゲームの訳の英語) → 種類の名前の決め打ち。利用者の決定 (大きさの語を除くとまとめ方・名前が不自然になるもの):
    //   Snub Carrier → 空母 (Snub は大きさではなく「スナブ戦闘機を積む」の意味なので、除くと「キャリア」になってしまう)
    //   Medium Data → 調査探索 (Expedition と同じ種類にする)、Light Science → 科学船
    //   Recovery → サルベージ、Transport → 貨物船、Touring と Luxury Touring → どちらもツーリング (同じ名前の種類に入る)
    // ここにある種別は、ほかの種別とまとめずにこの名前の種類にする (同じ名前の種類があればその種類に入る)
    private static readonly Dictionary<string, string> ShipKindOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Snub Carrier"] = "空母",
        ["Medium Data"] = "調査探索",
        ["Light Science"] = "科学船",
        ["Recovery"] = "サルベージ",
        ["Transport"] = "貨物船",
        ["Touring"] = "ツーリング",
        ["Luxury Touring"] = "ツーリング",
    };

    // 「/」で区切られた種別で外す区切り (利用者の決定: Starter / Light Freight → Light Freight と同じ扱い)
    private const string ShipKindStarterEn = "Starter";
    private const string ShipKindStarterJa = "スターター";

    // 船の種別 (role のキー) → 種類の名前。大きさで分かれている種別は 1 つにまとめる:
    //   英語 (ゲームの訳の英語。例 Light Fighter) から大きさの語を除いたもの (例 Fighter) が同じものを 1 つの種類にする。
    //   「/」で区切られた種別は、「Starter」の区切りを外して最初の区切りでまとめる (利用者の決定。
    //   例 Starter / Light Freight → Freight、Light Freight / Medium Fighter → Freight、Heavy Fighter / Bomber → Fighter)。
    //   ShipKindOverrides にある種別はまとめずにその名前にする (同じ名前の種類があればその種類に入る)。
    //   Stealth Fighter 等は大きさの語ではないので別の種類のまま。最後に名前がほかの種類に含まれるものを寄せる (MergeContainedKinds)
    //   名前は、(1) まとめた中に大きさの語の無い種別があればその訳 (例 Gunship → ガンシップ)、
    //   (2) 無ければ同じ英語の訳がある船の分類のキーの訳 (例 Fighter → 戦闘機)、
    //   (3) それも無ければ、まとめた中で一番多い訳 (「/」の無い種別を優先。「/」付きならスターターを外した最初の区切り) から
    //       先頭の大きさの語を除いたもの (例 軽貨物船 → 貨物船)
    private static Dictionary<string, string> ShipKindGroups(IEnumerable<string> roles, LocReader loc)
    {
        var members = new Dictionary<string, List<(string Role, string En, string Ja)>>(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in roles) counts[role] = counts.GetValueOrDefault(role) + 1;
        foreach (var role in counts.Keys)
        {
            if (role.Length == 0) continue;
            var en = (role.StartsWith('@') ? loc.English(role[1..]) : null) ?? role;
            if (ShipKindOverrides.TryGetValue(en.Trim(), out var fixedName)) { result[role] = fixedName; continue; }
            var ja = loc.Text(role);
            var key = ShipKindBase(en);
            if (!members.TryGetValue(key, out var list)) members[key] = list = new();
            list.Add((role, en, ja));
        }
        foreach (var (baseEn, list) in members)
        {
            string MostCommonJa(IEnumerable<(string Role, string En, string Ja)> xs) =>
                xs.GroupBy(x => x.Ja).OrderByDescending(g => g.Sum(x => counts[x.Role])).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key;
            var plain = list.Where(x => x.En.Trim().Equals(baseEn, StringComparison.OrdinalIgnoreCase)).ToList();
            var noSlash = list.Where(x => !x.En.Contains('/')).ToList();
            var name = plain.Count > 0 ? MostCommonJa(plain)
                : loc.ShipClassJapanese(baseEn) ?? StripShipSizeJa(FirstKindSegmentJa(MostCommonJa(noSlash.Count > 0 ? noSlash : list)));
            foreach (var x in list) result[x.Role] = name;
        }
        return MergeContainedKinds(result, counts);
    }

    // 利用者の決定: 種類の名前 (日本語) が、ほかの種類の名前に含まれるものは、それを含む大きな種類に寄せる (例 戦闘 → 戦闘機)。
    //   寄せる先は、名前が自分を含み、かつ船の数が自分より多い種類 (含む先が複数あれば船の数の多い方)。
    //   船の数が自分以下の種類には寄せない: 戦闘機 (多い) は ステルス戦闘機 (少ない) に含まれるが寄せない
    //   (ステルス戦闘機・ステルス爆撃機は利用者の決定で別の種類のまま。爆撃機 も ステルス爆撃機 より多いので寄せない)
    //   寄せた先がさらに寄せられるときはたどる (船の数は寄せるたびに増えるので回らない)
    private static Dictionary<string, string> MergeContainedKinds(Dictionary<string, string> roleToKind, Dictionary<string, int> roleCounts)
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (role, kind) in roleToKind) totals[kind] = totals.GetValueOrDefault(kind) + roleCounts.GetValueOrDefault(role);
        var target = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, n) in totals)
        {
            if (kind.Length == 0) continue;
            var to = totals.Where(t => t.Key != kind && t.Key.Contains(kind, StringComparison.Ordinal) && t.Value > n)
                .OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => t.Key).FirstOrDefault();
            if (to != null) target[kind] = to;
        }
        string Resolve(string k) { while (target.TryGetValue(k, out var next)) k = next; return k; }
        return roleToKind.ToDictionary(kv => kv.Key, kv => Resolve(kv.Value), StringComparer.Ordinal);
    }

    // 英語の種別 → まとめる鍵: 「/」の区切りのうち Starter を外した最初の区切り (全部 Starter なら最初の区切り) から大きさの語を除いたもの
    // (除いて空になるときは区切りのまま)
    private static string ShipKindBase(string en)
    {
        var segs = en.Split('/').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (segs.Count == 0) return en.Trim();
        var seg = segs.FirstOrDefault(x => !x.Equals(ShipKindStarterEn, StringComparison.OrdinalIgnoreCase)) ?? segs[0];
        var words = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !ShipSizeWords.Contains(w)).ToList();
        return words.Count > 0 ? string.Join(' ', words) : seg;
    }

    // 日本語の種別の「/」の区切りのうち、スターターを外した最初の区切り (全部スターターなら最初の区切り)
    private static string FirstKindSegmentJa(string ja)
    {
        var segs = System.Text.RegularExpressions.Regex.Split(ja, @"\s*/\s*").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (segs.Count == 0) return ja.Trim();
        return segs.FirstOrDefault(x => !x.StartsWith(ShipKindStarterJa, StringComparison.Ordinal)) ?? segs[0];
    }

    // 日本語の種別の先頭の大きさの語を除く
    private static string StripShipSizeJa(string ja)
    {
        foreach (var p in ShipSizePrefixesJa)
            if (ja.StartsWith(p, StringComparison.Ordinal) && ja.Length > p.Length) return ja[p.Length..];
        return ja;
    }

    // ［詳細］タブの種類の名前: 装備の表の種別 (ShipPortInfo.TypeDisplay) に、個人武器・船/車両・アーマー・服・個人装備の名前を足したもの。
    // どれでもなければ item_type のまま
    public static string DetailTypeLabel(string itemType) => itemType switch
    {
        "WeaponPersonal" => "個人武器",
        "NOITEM_Vehicle" => "船・車両",
        "WeaponAttachment" => "武器の付属品",
        "FPS_Consumable" => "医療用具・消耗品",
        "FPS_Deployable" => "設置型の爆発物",
        "Gadget" => "道具",
        "Grenade" => "手榴弾",
        _ when itemType.StartsWith("Char_Armor_", StringComparison.Ordinal) => "アーマー",
        _ when itemType.StartsWith("Char_Clothing_", StringComparison.Ordinal) => "服",
        _ => new ShipPortInfo { ItemType = itemType }.TypeDisplay,
    };

    // record の item_type (item_index、無ければ items)。無ければ ""
    public string ItemTypeOf(string recordName)
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT item_type FROM item_index WHERE record_name = @rn COLLATE NOCASE UNION ALL SELECT item_type FROM items WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", recordName ?? "");
            return cmd.ExecuteScalar() as string ?? "";
        }
        catch { return ""; }
    }

    // ［詳細］タブで UEX の items_prices を引くカテゴリ: その品目の item_type を条件に持つ比べるツールの種類のカテゴリ
    public static int[] UexCategoryIdsForItemType(string itemType) =>
        CompareCategories.Where(c => c.Filters.Any(f =>
            {
                var t = f.Split('/')[0];
                return t.EndsWith('*') ? itemType.StartsWith(t[..^1], StringComparison.Ordinal) : t.Equals(itemType, StringComparison.Ordinal);
            }))
            .SelectMany(c => c.UexCategoryIds ?? []).Distinct().ToArray();
}
