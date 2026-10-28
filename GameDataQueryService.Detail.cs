using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// ［詳細］タブの検索の候補 1 件 (item_index の 1 品目。船・車両・武器・装備のすべて)
// Note は同じ名前・同じ種類の候補が複数あるときに添える見分け (record 名。追加で取り込む品目だけ。SearchDetailCandidates)
// DisplayText はリストの表示をそのまま決めるもの (星系・場所・お店の候補。ShopTreeCandidates)
public sealed record DetailCandidate(string Record, string Name, string ItemType, string SubType, string Manufacturer, string? Note = null, string? DisplayText = null)
{
    public string Display => DisplayText
                             ?? $"{Name}  [{GameDataQueryService.DetailTypeLabel(ItemType)}{(SubType.Length > 0 && SubType != "UNDEFINED" ? " / " + SubType : "")}]"
                             + (string.IsNullOrEmpty(Note) ? "" : $"  ({Note})");
    public override string ToString() => Display;
}

// ［詳細］タブの分類「船舶」の 1 行 (Kind = まとめた種類の名前、Size = ships.size)
public sealed record DetailShipRow(string Record, string Name, string Kind, int Size);

public partial class GameDataQueryService
{
    // ［詳細］タブの検索。item_index のあいまい検索 (FuzzySearch: 全文検索 (trigram)、無ければ LIKE) で候補を出し、
    // 少なければ綴りの揺れ用の検索 (EditDistanceSearch) も足す。名前がプレースホルダ・空のものは除き、同じ名前・同じ種類は 1 件にする。
    // ただし追加で取り込む品目 (IsExtraDetailType の種類。例 Mining_Laser_MPUV_Arm と Mining_Laser_GRIN_Arbor_S1 の「Arbor MH1 Mining Laser」) は
    // 別の品目なので record ごとに残し、同じ名前・同じ種類が複数あるときは record 名を添えて見分けられるようにする。
    // shopTree を渡すと、星系・場所・お店の候補 (ShopTreeCandidates) も、品目の候補とは別に作って先頭に足す。並びは 星系 → 場所 → お店 → 資源 → 品目
    public List<DetailCandidate> SearchDetailCandidates(string query, int limit = 40, ShopTree? shopTree = null)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new List<DetailCandidate>();
        var list = new List<DetailCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string rec, string name, string type, string sub, string mfr)
        {
            var n = (name ?? "").Trim();
            if (rec.Length == 0 || n.Length == 0 || n.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || n.Contains("プレースホルダ")) return;
            var key = n + "\u0001" + type + (IsExtraDetailType(type) ? "\u0001" + rec : "");
            if (!seen.Add(key)) return;
            list.Add(new DetailCandidate(rec, n, type, sub, mfr));
        }
        void AddNotes()
        {
            const string head = "EntityClassDefinition.";
            foreach (var g in list.Select((c, i) => (c, i)).GroupBy(x => x.c.Name + "\u0001" + x.c.ItemType, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var (c, i) in g)
                    list[i] = c with { Note = c.Record.StartsWith(head, StringComparison.Ordinal) ? c.Record[head.Length..] : c.Record };
        }
        try
        {
            foreach (var r in FuzzySearch(q, limit * 2)) Add(r.recordName, r.name, r.itemType, r.subType, r.manufacturer);
            if (list.Count < 5)
                foreach (var r in EditDistanceSearch(q, 10)) Add(r.recordName, r.name, r.itemType, r.subType, r.manufacturer);
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        AddNotes();
        // 資源 (resource_types。英語名・日本語名の部分一致。選ぶと資源の詳細: GetItemDetailText → ResourceDetailText) は、
        // 上の品目の候補 (件数・limit・EditDistanceSearch に進むかの判定) とは別に作って先頭に足す (品目の候補の数え方には入れない)
        var resources = SearchResourceCandidates(q).Where(c => c.Name.Trim().Length > 0)
            .DistinctBy(c => c.Record, StringComparer.OrdinalIgnoreCase).ToList();
        var places = shopTree != null ? ShopTreeCandidates(shopTree, q) : new List<DetailCandidate>();
        return places.Concat(resources).Concat(list.Take(limit)).ToList();
    }

    // 星系・場所・お店の候補の最大の件数 (場所・お店はそれぞれ)
    private const int ShopTreeCandidateLimit = 30;

    // 星系・場所・お店の候補: 名前に q を含むもの (大文字小文字無視)。星系 → 場所 (最大 30 件) → お店 (最大 30 件) の順。
    // それぞれ 名前が q と同じもの → q で始まるもの → ほか、同じ順位は名前の順。表示は分類のリストと同じ形に種類を添える:
    //   星系「Stanton  [星系]」、場所「名前 (親の名前)  [場所の種類の日本語名]」、お店「名前 (場所の名前)  [装備の店 など]」
    public static List<DetailCandidate> ShopTreeCandidates(ShopTree tree, string query)
    {
        var q = (query ?? "").Trim();
        var result = new List<DetailCandidate>();
        if (q.Length == 0 || tree.IsEmpty) return result;
        int Rank(string name) => name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        bool Hit(string name) => name.Contains(q, StringComparison.OrdinalIgnoreCase);
        foreach (var n in tree.Systems.Where(n => Hit(n.System)).OrderBy(n => Rank(n.System)).ThenBy(n => n.System, StringComparer.OrdinalIgnoreCase))
            result.Add(SystemCandidate(n));
        foreach (var n in tree.Places.Where(n => Hit(n.Name)).OrderBy(n => Rank(n.Name)).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(n => n.RecordName, StringComparer.Ordinal).Take(ShopTreeCandidateLimit))
            result.Add(PlaceCandidate(n));
        foreach (var s in tree.Shops.Where(s => Hit(s.Name)).OrderBy(s => Rank(s.Name)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.Location.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Record, StringComparer.Ordinal).Take(ShopTreeCandidateLimit))
            result.Add(ShopCandidate(s));
        return result;
    }

    private static DetailCandidate SystemCandidate(ShopTreeNode n) => new(n.Record, n.System, "", "", "", DisplayText: $"{n.System}  [星系]");
    private static DetailCandidate PlaceCandidate(ShopTreeNode n) =>
        new(n.Record, n.Name, "", "", "", DisplayText: (n.Parent != null ? $"{n.Name} ({n.Parent.Name})" : n.Name) + $"  [{n.TypeLabel}]");
    private static DetailCandidate ShopCandidate(ShopTreeShop s) => new(s.Record, s.Name, "", "", "", DisplayText: $"{s.Name} ({s.Location.Name})  [{s.Kind}]");

    // record 1 つの候補 (リンクで移ったときにリストの先頭に置く)。星系・場所・お店は shopTree から、資源は resource_types から、
    // 品目は item_index (無ければ items) の名前・種類から作る。見つからなければ null
    public DetailCandidate? CandidateOf(string record, ShopTree? shopTree)
    {
        var rec = (record ?? "").Trim();
        if (rec.Length == 0) return null;
        if (ShopTree.IsTreeRecord(rec))
        {
            if (shopTree == null) return null;
            if (shopTree.ShopsByRecord.TryGetValue(rec, out var shop)) return ShopCandidate(shop);
            if (shopTree.NodesByRecord.TryGetValue(rec, out var node)) return node.IsStar ? SystemCandidate(node) : PlaceCandidate(node);
            return null;
        }
        var isResource = rec.StartsWith("ResourceType.", StringComparison.OrdinalIgnoreCase);
        if (isResource || Guid.TryParse(rec, out _))
        {
            var res = OreService.LoadResources(DbPath, _translationsDbPath)
                .FirstOrDefault(x => isResource ? x.RecordName.Equals(rec, StringComparison.OrdinalIgnoreCase) : x.Uuid.Equals(rec, StringComparison.OrdinalIgnoreCase));
            if (res != null) return new DetailCandidate(res.RecordName, res.NameEn ?? OreService.ShortRecord(res.RecordName), ResourceDetailType, "", "");
        }
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT record_name, COALESCE(name, ''), COALESCE(item_type, ''), COALESCE(sub_type, ''), COALESCE(manufacturer, '') FROM item_index WHERE record_name = @rn COLLATE NOCASE LIMIT 1";
            cmd.Parameters.AddWithValue("@rn", rec);
            using var r = cmd.ExecuteReader();
            if (r.Read() && r.GetString(1).Trim().Length > 0) return new DetailCandidate(r.GetString(0), r.GetString(1).Trim(), r.GetString(2), r.GetString(3), r.GetString(4));
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        return null;
    }

    // ［詳細］タブの候補の種類「資源」(resource_types の資源。DetailCandidate.ItemType に入れる)
    internal const string ResourceDetailType = "Resource";

    // 資源の候補: resource_types の英語名 (OreService.LoadResources の NameEn。［鉱石］タブと同じ引き方) か日本語名に q を含むもの (大文字小文字無視)。
    // 名前は英語名 (無ければ record 名の "ResourceType." を除いたもの)。名前の無い資源 (英語名も日本語名も無い) は出さない。
    // 並びは 名前が q と同じもの → q で始まるもの → ほか、それぞれ名前の順
    private List<DetailCandidate> SearchResourceCandidates(string q)
    {
        var result = new List<DetailCandidate>();
        foreach (var r in OreService.LoadResources(DbPath, _translationsDbPath))
        {
            if (r.NameEn == null && r.NameJa == null) continue;
            var hit = (r.NameEn?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) || (r.NameJa?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
            if (!hit) continue;
            result.Add(new DetailCandidate(r.RecordName, r.NameEn ?? OreService.ShortRecord(r.RecordName), ResourceDetailType, "", ""));
        }
        int Rank(DetailCandidate c) => c.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ? 0 : c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        return result.OrderBy(Rank).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ［詳細］タブの分類「鉱石」の資源 (Record = resource_types の record 名, Name = 英語名 (無ければ record 名の "ResourceType." を除いたもの), Kind = 種類の日本語名)。
    // 入れるもの: 採掘で取れる資源 (ore_locations に行がある資源) とその精錬後 (refined_uuid の先) は group にかかわらず全部、
    // それに加えて OreCategoryWholeGroups の group の資源を全部。名前に PLACEHOLDER / プレースホルダを含むものは出さない。並びは名前の順。
    // 種類は group_record ごと (OreCategoryGroupLabels。表に無い group は group の record 名の後ろ)
    private static readonly string[] OreCategoryWholeGroups =
        ["ResourceTypeGroup.UnrefinedOres", "ResourceTypeGroup.Raw_Minerals", "ResourceTypeGroup.Mineral", "ResourceTypeGroup.Metal", "ResourceTypeGroup.Nonmetal"];
    private static readonly Dictionary<string, string> OreCategoryGroupLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ResourceTypeGroup.UnrefinedOres"] = "未精錬の鉱石",
        ["ResourceTypeGroup.Raw_Minerals"] = "未精錬の鉱物",
        ["ResourceTypeGroup.Mineral"] = "鉱物",
        ["ResourceTypeGroup.Metal"] = "金属",
        ["ResourceTypeGroup.Nonmetal"] = "非金属",
        ["ResourceTypeGroup.Halogen"] = "ハロゲン",
        ["ResourceTypeGroup.Organic"] = "有機物",
    };

    public List<(string Record, string Name, string Kind)> GetOreCategoryResources()
    {
        var mined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT resource_uuid FROM ore_locations";
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0)) mined.Add(r.GetString(0));
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        var include = new HashSet<string>(mined, StringComparer.OrdinalIgnoreCase);
        if (mined.Count > 0)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT uuid, refined_uuid FROM resource_types WHERE refined_uuid IS NOT NULL";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    if (!r.IsDBNull(0) && mined.Contains(r.GetString(0)) && !r.IsDBNull(1) && r.GetString(1).Length > 0) include.Add(r.GetString(1));
            }
            catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        }
        var result = new List<(string Record, string Name, string Kind)>();
        foreach (var x in OreService.LoadResources(DbPath, _translationsDbPath))
        {
            if (!include.Contains(x.Uuid) && !OreCategoryWholeGroups.Contains(x.GroupRecord, StringComparer.OrdinalIgnoreCase)) continue;
            var name = x.NameEn ?? OreService.ShortRecord(x.RecordName);
            if (name.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || name.Contains("プレースホルダ")) continue;
            var kind = OreCategoryGroupLabels.TryGetValue(x.GroupRecord, out var label) ? label : OreService.ShortRecord(x.GroupRecord);
            result.Add((x.RecordName, name, kind));
        }
        return result.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // 資源の詳細 (［詳細］タブ)。rec が "ResourceType." で始まる record 名か resource_types の uuid のときだけ文字を返す (それ以外・resource_types に無ければ null):
    //   【基本】名前 (英語名。無ければ record 名の "ResourceType." を除いたもの)・種類・record 名
    //   【採掘できる場所】ore_locations の表。精錬後の資源 (例 Copper) は、resource_types.refined_uuid でそれになる原石 (Ore_Copper 等) の行も合わせて出す。
    //     ore_locations の行 (1 場所 1 行) を (資源, preset, 掘り方, 岩の種類) で 1 行にまとめ、場所の列に場所名を ", " でつなぐ
    //     (番号の付いた小さな場所 (GameDataExtractor.OreSmallPlacePrefixes) は個別に並べず「Mining Claim (157 か所)」のように種類ごとの件数にする)。
    //     星系の列は preset の星系。並びは 掘り方 (船 → ROC → 手)、出やすさの大きい順、場所の列。行が無ければ「採掘できる場所のデータがありません。」
    private string? ResourceDetailText(string rec)
    {
        var isRecord = rec.StartsWith("ResourceType.", StringComparison.OrdinalIgnoreCase);
        if (!isRecord && !Guid.TryParse(rec, out _)) return null;
        var resources = OreService.LoadResources(DbPath, _translationsDbPath);
        var res = resources.FirstOrDefault(x => isRecord ? x.RecordName.Equals(rec, StringComparison.OrdinalIgnoreCase) : x.Uuid.Equals(rec, StringComparison.OrdinalIgnoreCase));
        if (res == null) return null;
        var name = res.NameEn ?? OreService.ShortRecord(res.RecordName);

        // この資源と、精錬後がこの資源になる原石の uuid
        var uuids = new List<string> { res.Uuid };
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT uuid FROM resource_types WHERE refined_uuid = @u COLLATE NOCASE";
            cmd.Parameters.AddWithValue("@u", res.Uuid);
            using var r = cmd.ExecuteReader();
            while (r.Read()) if (!r.IsDBNull(0) && !uuids.Contains(r.GetString(0), StringComparer.OrdinalIgnoreCase)) uuids.Add(r.GetString(0));
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }

        var rows = new List<(string Place, string System, string Method, string Rock, double Chance, double Min, double Max, double Prob, string Preset, string Uuid)>();
        try
        {
            using var cmd = _conn.CreateCommand();
            var ps = uuids.Select((_, i) => "@u" + i).ToList();
            cmd.CommandText = $"SELECT place, system, method, rock, chance, min_pct, max_pct, part_probability, preset, resource_uuid FROM ore_locations WHERE resource_uuid IN ({string.Join(",", ps)})";
            for (int i = 0; i < uuids.Count; i++) cmd.Parameters.AddWithValue(ps[i], uuids[i]);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6), r.GetDouble(7), r.GetString(8), r.GetString(9)));
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }

        var sb = new StringBuilder();
        sb.AppendLine(DetailTitle(name, DetailTypeLabel(ResourceDetailType)));
        sb.AppendLine("【基本】");
        sb.AppendLine($"  名前: {name}");
        sb.AppendLine($"  種類: {DetailTypeLabel(ResourceDetailType)}");
        sb.AppendLine($"  record 名: {OreService.ShortRecord(res.RecordName)}");
        sb.AppendLine();
        sb.AppendLine("【採掘できる場所】");
        if (rows.Count == 0)
        {
            sb.AppendLine("採掘できる場所のデータがありません。");
            return sb.ToString();
        }
        static int MethodOrder(string m) => m switch
        {
            GameDataExtractor.OreMethodShip => 0,
            GameDataExtractor.OreMethodRoc => 1,
            GameDataExtractor.OreMethodHand => 2,
            _ => 3,
        };
        static string Pct(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        static string Pct2(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        sb.AppendLine("| 場所 | 星系 | 掘り方 | 岩の種類 | 出やすさ | 岩の中の割合 | 出やすさ×割合 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        // 場所の列: 名前の順の場所名、続けて番号の付いた小さな場所の種類ごとの件数 (OreSmallPlacePrefixes の順)
        static string PlacesText(IEnumerable<string> places)
        {
            var named = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var small = new int[GameDataExtractor.OreSmallPlacePrefixes.Length];
            foreach (var p in places.Distinct(StringComparer.Ordinal))
            {
                var i = Array.FindIndex(GameDataExtractor.OreSmallPlacePrefixes, x => p.StartsWith(x.Prefix, StringComparison.Ordinal));
                if (i >= 0) small[i]++;
                else named.Add(p);
            }
            var parts = named.ToList();
            for (int i = 0; i < small.Length; i++)
                if (small[i] > 0) parts.Add($"{GameDataExtractor.OreSmallPlacePrefixes[i].Label} ({small[i]} か所)");
            return string.Join(", ", parts);
        }
        var groups = rows.GroupBy(x => (x.Uuid, x.Preset, x.Method, x.Rock))
            .Select(g => (First: g.First(), Places: PlacesText(g.Select(x => x.Place))))
            .OrderBy(x => MethodOrder(x.First.Method)).ThenByDescending(x => x.First.Chance).ThenBy(x => x.Places, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (x, places) in groups)
        {
            var chance = x.Chance.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "%";
            var range = $"{Pct(x.Min)}〜{Pct(x.Max)}%" + (x.Prob < 1 ? $"（入る確率 {Pct(x.Prob * 100)}%）" : "");
            // 出やすさ×割合: 出やすさ (%) × 岩の中の割合 (%) ÷ 100 × 入る確率 (part_probability)。割合の幅に合わせて幅で出す
            var product = $"{Pct2(x.Chance * x.Min / 100 * x.Prob)}〜{Pct2(x.Chance * x.Max / 100 * x.Prob)}%";
            var line = $"| {Cell(MdEscape(places))} | {Cell(MdEscape(x.System))} | {Cell(MdEscape(x.Method))} | {Cell(MdEscape(x.Rock))} | {Cell(chance)} | {Cell(range)} | {Cell(product)} |";
            if (seen.Add(line)) sb.AppendLine(line);
        }
        sb.AppendLine();
        sb.AppendLine("出やすさ＝その場所でその掘り方の岩のうち、この岩が出る割合（ゲームデータの relativeProbability から計算）。出やすさ×割合＝出やすさ × 岩の中の割合（入る確率が 1 未満ならそれも掛ける）。");
        return sb.ToString();
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
    //   【船・車両】船の情報 (SearchShips。船・車両のとき。「装備の取り付け口」の行は出さない) と既定の装備の表 (ShipDefaultLoadoutText)
    //   【性能】lookup と同じ (GetItemStatsText。色違いは基本品目の性能、武器は 1発・DPS・射撃モード・ダウンまでの発数など)
    //   【距離による減衰】【射程】武器の弾の damageDropParams・説明文の Effective Range・弾速 × 寿命 (WeaponFalloffText)
    //   【装着できるアタッチメント】個人武器の枠ごとに付けられるもの (WeaponAttachmentListText)
    //   【販売場所】保存データ (item_shop_prices) の購入・レンタル、無ければ uexItemPrices (星系 / 場所 / 店名 / 値段 を安い順)
    //   【ブループリント】その品をできる品とする BP (BlueprintDetail.ItemSectionText。BP が無ければ出さない)
    // shopTree を渡すと、【販売場所】の店の部分を、shop_price_links で店に結べた行だけお店の詳細へのリンク ([[record|表示名]]。DetailLink) にする
    // includePersonal が false なら【ブループリント】の節に個人のデータ (在庫・所持・手持ち) を出さない (データ API の LAN からの要求。BlueprintDetail.ItemSectionText)
    // 品目が無ければ null
    public string? GetItemDetailText(string recordName, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices = null, ShopTree? shopTree = null,
        bool includePersonal = true)
    {
        var rec = (recordName ?? "").Trim();
        if (rec.Length == 0) return null;
        // 資源 (ResourceType.… か resource_types の uuid) は item_index ではなく資源の詳細 (【採掘できる場所】)
        if (ResourceDetailText(rec) is { } resourceText) return resourceText;
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
        sb.Insert(0, DetailTitle(nameEn, typeJa) + Environment.NewLine);
        sb.AppendLine($"  種類: {typeJa}" + (subType.Length > 0 && subType != "UNDEFINED" ? $" / {subType}" : "") + (typeJa != itemType && !typeJa.Contains($"({itemType})") ? $" ({itemType})" : ""));
        var mfr = ManufacturerName(itemsMfr.Length > 0 ? itemsMfr : indexMfr, loc);
        if (mfr.Length == 0) mfr = indexMfr;
        if (mfr.Length > 0) sb.AppendLine($"  メーカー: {mfr}");
        if (size > 0) sb.AppendLine($"  サイズ: S{size}");
        // 船・車両の積載量 (【船・車両】と同じ共通の関数: ゲームデータ → UEX → Ship Matrix)。0・不明なら出さない
        if (itemType.StartsWith("NOITEM_Vehicle", StringComparison.OrdinalIgnoreCase)
            && ShipCargo.Resolve(nameEn, mfr, rec) is { } cargo)
            sb.AppendLine($"  積載量: {ShipCargo.Format(cargo.Scu)} SCU ({cargo.Source})");
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
            // 「装備の取り付け口」の行は出さず (AI チャットの SearchShips は従来どおり出す)、代わりに既定の装備の表 (ShipDefaultLoadoutText)
            var ship = SearchShips(nameEn, rec, includePortSummary: false);
            if (ship.Trim().Length > 0)
            {
                sb.AppendLine(); sb.AppendLine("【船・車両】"); sb.AppendLine(ship.TrimEnd());
                var loadout = ShipDefaultLoadoutText(rec, loc);
                if (loadout.Length > 0) { sb.AppendLine(); sb.Append(loadout); }
            }
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
        sb.Append(ShopText(uuids, nameEn, uexItemPrices, shopTree));

        // その品をできる品とする BP があれば、末尾に【ブループリント】の節 (［BP］タブと同じ読み方。BP の表が無い・BP が無ければ出さない)。
        // 読み込みが例外になったときは、この節だけを読み込めなかった旨の 1 行にする (【装着できるアタッチメント】と同じ)
        string bp;
        try { bp = BlueprintDetail.ItemSectionText(Path.GetDirectoryName(DbPath) ?? "", rec, includePersonal); }
        catch (Exception ex) { bp = "【ブループリント】" + Environment.NewLine + $"  ブループリントを読み込めなかった ({ex.Message})" + Environment.NewLine; }
        if (bp.Length > 0) { sb.AppendLine(); sb.Append(bp); }
        return sb.ToString();
    }

    // ［詳細］タブの【船・車両】の既定の装備の表 (「搭載枠 | 種類・サイズ | 既定の装備」の Markdown の表の行。DetailHtml が「|」の行をそのまま表にする)。
    // 中身は ships.raw_json の _RecordValue_.Components の SEntityComponentDefaultLoadoutParams.loadout.entries (入れ子の loadout も)。
    //   ・装備の record 名は entityClassName、空なら entityClassReference のファイル名 (GameDataExtractor.ResolveLoadoutEntityName と同じ)
    //   ・item_index と EquipmentService.RecordCandidates と同じ候補順・大文字小文字無視で照合し、引けたものだけ載せる (引けないもの・空のポートは載せない)。
    //     引けない装備の下の入れ子は、載せた一番近い祖先の下に置く (祖先が無ければ最上位)
    //   ・並びは raw_json の順。入れ子は搭載枠の頭に「└」(深さ 2 以降は前に全角空白を深さ - 1 個)
    //   ・同じ親の下で同じ装備 (record) が続き、下に載せる行が無いものは 1 行にまとめて「×数」。最上位の行はまとめない
    //   ・搭載枠: PortNameJa.TranslatePath。種類: LoadoutTypeLabel。サイズ: item_index.size (S2 の形。0 なら出さない)。名前: LoadoutItemName
    // 船の行が無ければ ""。既定のロードアウトを読めない (部品が無い・entries が空・JSON を読めない) ときは「既定の装備: 元データから読めません」の 1 行
    private string ShipDefaultLoadoutText(string shipRecord, LocReader loc)
    {
        // 船の行はまず大文字小文字の一致で引き (索引が効く)、無ければ大文字小文字無視で引く (全件をなめるので後回し)
        object? v = null;
        foreach (var sql in new[] { "SELECT raw_json FROM ships WHERE record_name = @rn LIMIT 1", "SELECT raw_json FROM ships WHERE record_name = @rn COLLATE NOCASE LIMIT 1" })
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@rn", shipRecord);
            v = cmd.ExecuteScalar();
            if (v != null) break;
        }
        if (v == null) return "";
        var raw = v as string;
        const string unreadable = "既定の装備: 元データから読めません";
        if (string.IsNullOrEmpty(raw)) return unreadable + Environment.NewLine;

        var rows = new List<LoadoutRow>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            JsonElement? entries = null;
            if (doc.RootElement.TryGetProperty("_RecordValue_", out var rv) && rv.TryGetProperty("Components", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("_Type_", out var t) || t.GetString() != "SEntityComponentDefaultLoadoutParams") continue;
                    if (c.TryGetProperty("loadout", out var lo) && lo.ValueKind == JsonValueKind.Object
                        && lo.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array && es.GetArrayLength() > 0)
                        entries = es;
                    break;
                }
            if (entries == null) return unreadable + Environment.NewLine;

            var (index, itemsNames) = LoadLoadoutTables();
            var lookupCache = new Dictionary<string, LoadoutItemInfo?>(StringComparer.OrdinalIgnoreCase);
            void Walk(JsonElement list, int depth, int parentRow)
            {
                foreach (var e in list.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var port = e.TryGetProperty("itemPortName", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() ?? "" : "";
                    var entity = LoadoutEntityName(e);
                    LoadoutItemInfo? info = null;
                    if (entity.Length > 0 && !lookupCache.TryGetValue(entity, out info))
                    {
                        info = LookupLoadoutItem(entity, index, itemsNames, loc);
                        lookupCache[entity] = info;
                    }
                    int childDepth = depth, childParent = parentRow;
                    if (info != null)
                    {
                        if (parentRow >= 0) rows[parentRow].HasChildren = true;
                        rows.Add(new LoadoutRow { Depth = depth, ParentRow = parentRow, PortLabel = PortNameJa.TranslatePath(port), Item = info });
                        childDepth = depth + 1;
                        childParent = rows.Count - 1;
                    }
                    if (e.TryGetProperty("loadout", out var sub) && sub.ValueKind == JsonValueKind.Object
                        && sub.TryGetProperty("entries", out var subEntries) && subEntries.ValueKind == JsonValueKind.Array)
                        Walk(subEntries, childDepth, childParent);
                }
            }
            Walk(entries.Value, 0, -1);
        }
        catch (JsonException) { return unreadable + Environment.NewLine; }
        // 装備はあるが名前の分かる装備が 1 つも無い (テンプレート用の船など): 空の表ではなく 1 行で出す
        if (rows.Count == 0) return "既定の装備: 名前の分かる装備がありません" + Environment.NewLine;

        var sb = new StringBuilder();
        sb.AppendLine("| 搭載枠 | 種類・サイズ | 既定の装備 |");
        sb.AppendLine("|---|---|---|");
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var ports = new List<string> { r.PortLabel };
            // 同じ親の下で同じ装備が続く行 (下に載せる行が無いもの) は 1 行にまとめる。最上位の行はまとめない
            if (r.Depth > 0 && !r.HasChildren)
                while (i + 1 < rows.Count && rows[i + 1].Depth == r.Depth && rows[i + 1].ParentRow == r.ParentRow && !rows[i + 1].HasChildren
                       && string.Equals(rows[i + 1].Item.Record, r.Item.Record, StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    ports.Add(rows[i].PortLabel);
                }
            var portCell = MergedPortLabel(ports);
            if (r.Depth > 0) portCell = new string('\u3000', r.Depth - 1) + "└ " + portCell;
            var typeCell = r.Item.TypeLabel + (r.Item.Size > 0 ? $" S{r.Item.Size}" : "");
            var nameCell = r.Item.Name + (ports.Count > 1 ? $" ×{ports.Count}" : "");
            sb.AppendLine($"| {Cell(MdEscape(portCell))} | {Cell(MdEscape(typeCell))} | {Cell(MdEscape(nameCell))} |");
        }
        return sb.ToString();
    }

    // 既定の装備の表のセルの Markdown の記号を打ち消す (名前の「*」などが斜体の記号として読まれて消えないように。例: Stor*All)
    private static string MdEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '~' or '#') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private sealed class LoadoutRow
    {
        public int Depth;
        public int ParentRow;          // 載せた親の行の番号 (rows の添字)。最上位は -1
        public string PortLabel = "";
        public LoadoutItemInfo Item = null!;
        public bool HasChildren;       // 下に載せる行があるか
    }

    private sealed record LoadoutItemInfo(string Record, string TypeLabel, int Size, string Name);

    // まとめた行の搭載枠: 1 つならそのまま。どれも「同じ語 + 空白 + 番号」で番号が 1 ずつ増えるなら「語 先頭〜末尾」、そうでなければ「、」でつなぐ
    private static string MergedPortLabel(List<string> labels)
    {
        if (labels.Count == 1) return labels[0];
        var m = labels.Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^(.*\S) (\d+)$")).ToList();
        if (m.All(x => x.Success) && m.All(x => x.Groups[1].Value == m[0].Groups[1].Value))
        {
            var nums = m.Select(x => int.Parse(x.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            if (nums.Zip(nums.Skip(1), (a, b) => b == a + 1).All(ok => ok))
                return $"{m[0].Groups[1].Value} {nums[0]}〜{nums[^1]}";
        }
        return string.Join("、", labels);
    }

    // ロードアウトの 1 項目の装備の record 名: entityClassName、空なら entityClassReference のファイル名 (.json を外す)。
    // GameDataExtractor.ResolveLoadoutEntityName と同じ
    private static string LoadoutEntityName(JsonElement entry)
    {
        var entityName = entry.TryGetProperty("entityClassName", out var en) && en.ValueKind == JsonValueKind.String ? en.GetString() ?? "" : "";
        if (!string.IsNullOrEmpty(entityName)) return entityName;
        var reference = entry.TryGetProperty("entityClassReference", out var er) && er.ValueKind == JsonValueKind.String ? er.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(reference)) return "";
        var slash = reference.LastIndexOf('/');
        var basename = slash >= 0 ? reference[(slash + 1)..] : reference;
        if (basename.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) basename = basename[..^5];
        return basename;
    }

    // record の照合の候補 (EquipmentService.RecordCandidates と同じ順: そのまま → EntityClassDefinition. 付き →
    // _SCItem を外した / 付けた形 → その EntityClassDefinition. 付き)
    private static IEnumerable<string> LoadoutRecordCandidates(string record)
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

    // item_index の 1 行 (既定の装備の照合用)
    private sealed record LoadoutIndexRow(string Record, string Name, string ItemType, int Size, string NameKey);

    // 既定の装備の照合に使う item_index (record_name → 行) と items (record_name → name) を 1 回ずつ全部読む (大文字小文字無視)。
    // record_name の索引は大文字小文字を区別する形なので、COLLATE NOCASE で 1 項目ずつ引くと毎回全件をなめて遅い (船 1 隻で数百回) ため。
    // 同じ record_name (大文字小文字無視) が複数あれば先に読んだ行 (1 項目ずつ LIMIT 1 で引いていたときの最初の行と同じ並び)
    private (Dictionary<string, LoadoutIndexRow> Index, Dictionary<string, string> ItemsName) LoadLoadoutTables()
    {
        var index = new Dictionary<string, LoadoutIndexRow>(StringComparer.OrdinalIgnoreCase);
        var itemsName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hasSize = ItemIndexHasSize();
        var hasNameKey = ItemIndexHasNameKey();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT record_name, COALESCE(name, ''), COALESCE(item_type, ''), {(hasSize ? "COALESCE(size, 0)" : "0")}, {(hasNameKey ? "COALESCE(name_key, '')" : "''")} FROM item_index";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0)) continue;
                var rn = r.GetString(0);
                index.TryAdd(rn, new LoadoutIndexRow(rn, r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4)));
            }
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "SELECT record_name, COALESCE(name, '') FROM items";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (!r.IsDBNull(0)) itemsName.TryAdd(r.GetString(0), r.GetString(1));
        }
        return (index, itemsName);
    }

    // ロードアウトの装備を item_index で引く (候補順・大文字小文字無視)。引けなければ null
    private static LoadoutItemInfo? LookupLoadoutItem(string entity, Dictionary<string, LoadoutIndexRow> index, Dictionary<string, string> itemsNames, LocReader loc)
    {
        foreach (var cand in LoadoutRecordCandidates(entity))
        {
            if (!index.TryGetValue(cand, out var x)) continue;
            var itemsName = itemsNames.TryGetValue(x.Record, out var s) ? s : "";
            return new LoadoutItemInfo(x.Record, LoadoutTypeLabel(x.ItemType, loc), x.Size, LoadoutItemName(x.Record, x.Name, itemsName, x.NameKey, loc));
        }
        return null;
    }

    // 既定の装備の名前: 日本語名 (item_index.name)。英語名が引けて日本語名と違えば「日本語 (英語)」(比べるときは DetailNameKey の形。等級の印は同じものとして扱う)。
    // 英語名は items.name の翻訳 (キーでなければ items.name)、無ければ item_index.name_key の翻訳の英語。
    // 日本語名が仮の文字 (<= PLACEHOLDER => など)・空なら record 名 (EntityClassDefinition. を外したもの)。英語名が仮の文字なら出さない
    private static string LoadoutItemName(string record, string indexName, string itemsName, string nameKey, LocReader loc)
    {
        static string? Usable(string? s)
        {
            var t = (s ?? "").Replace(' ', ' ').Trim();
            if (t.Length == 0 || t.StartsWith('@') || t.StartsWith("<=", StringComparison.Ordinal)
                || t.Contains("PLACEHOLDER", StringComparison.OrdinalIgnoreCase) || t.Contains("プレースホルダ")) return null;
            return t;
        }
        string? en = itemsName.StartsWith('@') ? Usable(loc.English(itemsName[1..])) : Usable(itemsName);
        if (en == null && nameKey.Length > 0) en = Usable(loc.English(nameKey));
        var ja = Usable(indexName) ?? (record.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? record["EntityClassDefinition.".Length..] : record);
        return en != null && !string.Equals(DetailNameKey(ja), DetailNameKey(en), StringComparison.OrdinalIgnoreCase) ? $"{ja} ({en})" : ja;
    }

    // 既定の装備の種類: ［詳細］の種類の名前 (DetailTypeLabel。ShipPortInfo.TypeDisplay ほか) に日本語があればそれ。
    // 無ければゲームの日本語訳の種類の名前 (翻訳 DB の item_Type + item_type のキーの日本語)。どちらも無ければ item_type (英語) のまま
    private static string LoadoutTypeLabel(string itemType, LocReader loc)
    {
        var label = DetailTypeLabel(itemType);
        if (label != itemType || itemType.Length == 0) return label;
        var key = "item_Type" + itemType;
        var text = loc.Text("@" + key);
        // Text は日本語が無ければ英語を返すので、英語と同じなら日本語は無いものとする
        return !text.StartsWith('@') && text != loc.English(key) ? text : itemType;
    }

    // item_index に name_key 列があるか (列を足す前の古い DB には無い)
    private bool ItemIndexHasNameKey()
    {
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA table_info(item_index)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), "name_key", StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        return false;
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
    // 購入は「星系 / 場所 / 店名 / 値段」を安い順に (ShopLine)、レンタルは「売り場 / 日数 / 値段」。
    // 店名・売り場は、shop_price_links で店に結べたものだけお店の詳細へのリンクにする (ShopTree.FindShopRecord)
    private string ShopText(List<string> uuids, string nameEn, IReadOnlyDictionary<string, List<(string Location, double Price)>>? uexItemPrices, ShopTree? tree)
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
                sb.AppendLine("  " + ShopLine(s, tree));
        }
        var rents = rows.Where(r => r.Kind == ShopPriceStore.KindRent && r.Price.HasValue).OrderBy(r => r.Price).ToList();
        if (rents.Count > 0)
        {
            sb.AppendLine("  レンタル: 売り場 / 日数 / 値段");
            foreach (var r in rents) sb.AppendLine($"  {ShopCell(r.Location, tree?.FindShopRecord(r.Source, r.Location))} / {r.RentDays}日 / {r.Price!.Value:N0} aUEC");
        }
        if (buys.Count == 0 && rents.Count == 0)
        {
            // 保存済みの「cstone: 販売なし (Not Sold directly or at all)」は、括弧の英文を外して出す (保存データは書き換えない)
            var note = ShopPriceStore.DisplayNote(rows.FirstOrDefault(r => r.Kind == ShopPriceStore.KindNone)?.Note);
            sb.AppendLine("  店の販売データ無し" + (!string.IsNullOrEmpty(note) ? $" ({note})" : ""));
        }
        return sb.ToString();
    }

    // 【販売場所】の購入の 1 行「星系 / 場所 / 店名 / 値段 aUEC」(ShopListing.Line と同じ形。店名はリンクにできればリンク)。
    // 分けられない形 (UEX の船の店。「Vantage HUR-L1」のように店名と場所が 1 つの名前) は、見出しの 4 列に合わせて星系・場所を「-」にし、店名の列に売り場の文字を入れる
    private static string ShopLine(ShopListing s, ShopTree? tree)
    {
        var price = $"{s.Price:N0} aUEC";
        var record = tree?.FindShopRecord(s.Source, s.Location);
        return s.Parts() is { } x
            ? $"{x.System} / {(x.Place.Length > 0 ? x.Place : "-")} / {ShopCell(x.Shop, record)} / {price}"
            : $"- / - / {ShopCell(s.Location, record)} / {price}";
    }

    // 「A / B / C」の行の店の列。record があればリンク (表示名の " / " は列の区切りと重ならないよう "/" にする)
    private static string ShopCell(string name, string? record) =>
        record == null ? name : DetailLink(record, name.Replace(" / ", "/"));

    // ［詳細］タブの詳細の文字のいちばん上の見出し「【【名前（種類）】】」(DetailHtml が大きめの見出しにする。文字の欄ではこのまま出る)
    public static string DetailTitle(string name, string kind) => $"【【{name}（{kind}）】】";

    // ［詳細］タブの詳細の文字の中のリンク「[[record|表示名]]」(record は URL エンコードする。DetailHtml がリンクに、文字の欄では表示名だけにする)。
    // 表示名の "|" は "/" に、改行は空白にする
    public static string DetailLink(string record, string display) =>
        $"[[{Uri.EscapeDataString(record)}|{(display ?? "").Replace("|", "/").Replace('\r', ' ').Replace('\n', ' ')}]]";

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
        // 追加で取り込む品目の種類。ゲームの global.ini の item_Type* の訳 (item_TypeContainer「コンテナ」・item_TypeDockingCollar「ドッキングカラー」・
        // item_TypeMisc「その他」・item_TypeCargo「貨物」・item_TypeDrink「ドリンク」・item_TypeFood「食べ物」・item_TypeBottle「ボトル」・
        // item_TypeRemovableChip「デバイス」・item_TypeBattery「バッテリー」・item_TypeMiningModifier「採掘モジュール」・item_TypeSalvageHead「サルベージビーム」)。
        // item_Type* の無い種類: Currency は mobiGlas_PlayerTrade_Currency_Title の「通貨」、InventoryContainer は
        // shop_ui_transactionResult_05_InventoryContainerRequestFail の「インベントリコンテナ」、SalvageModifier は品目名の訳
        // (「… スクレーパーモジュール」「… トラクターモジュール」) の「モジュール」と item_TypeSalvageHead の「サルベージ」から「サルベージモジュール」
        "Container" => "コンテナ",
        "DockingCollar" => "ドッキングカラー",
        "Misc" => "その他",
        "Cargo" => "貨物",
        "Drink" => "ドリンク",
        "Food" => "食べ物",
        "Bottle" => "ボトル",
        "RemovableChip" => "デバイス",
        "Battery" => "バッテリー",
        "MiningModifier" => "採掘モジュール",
        "SalvageHead" => "サルベージビーム",
        "Currency" => "通貨",
        "InventoryContainer" => "インベントリコンテナ",
        "SalvageModifier" => "サルベージモジュール",
        ResourceDetailType => "資源",
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

    // ===== お店・ステーション・星系 (［詳細］タブ) =====

    // 星図の場所 (starmap_locations)・ゲームの店 (game_shops)・店と DB の価格の紐づけ (shop_price_links) を読み、［詳細］タブで使う形 (ShopTree) にする。
    // 店の売っている物は、紐づけた価格の名前 (item_shop_prices の location・trade_cache.db の trade_prices の terminal) の DB の価格から作る。
    // 表が無い (インデックス構築前・前の版の DB) なら空の ShopTree (IsEmpty)
    public ShopTree BuildShopTree(string tradeDbPath)
    {
        var tree = new ShopTree();
        try
        {
            using (var cmd = _conn.CreateCommand())
            {
                // 座標の列 (x / y / z / radius) の無い前の形の表では、座標は無いものとして読む。自転の列 (spin_parent / lx / ly / lz / rot_hours) の無い表では、自転は無いものとして読む
                bool hasCoords, hasSpin, hasPlanetRadius, hasFrame;
                using (var info = _conn.CreateCommand())
                {
                    info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name IN ('x', 'y', 'z', 'radius')";
                    hasCoords = Convert.ToInt64(info.ExecuteScalar() ?? 0L) == 4;
                    info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name IN ('spin_parent', 'lx', 'ly', 'lz', 'rot_hours')";
                    hasSpin = Convert.ToInt64(info.ExecuteScalar() ?? 0L) == 5;
                    info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name = 'planet_radius'";
                    hasPlanetRadius = Convert.ToInt64(info.ExecuteScalar() ?? 0L) == 1;
                    info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name = 'frame_matrix'";
                    hasFrame = Convert.ToInt64(info.ExecuteScalar() ?? 0L) == 1;
                }
                cmd.CommandText = "SELECT record_name, name_en, type, parent_record, system, " +
                    (hasCoords ? "x, y, z, radius" : "NULL, NULL, NULL, NULL") + ", " +
                    (hasSpin ? "spin_parent, lx, ly, lz, rot_hours" : "'', NULL, NULL, NULL, NULL") + ", " +
                    (hasPlanetRadius ? "planet_radius" : "NULL") + ", " + (hasFrame ? "frame_matrix" : "NULL") + " FROM starmap_locations";
                using var r = cmd.ExecuteReader();
                static double? D(SqliteDataReader rd, int i) => rd.IsDBNull(i) ? null : rd.GetDouble(i);
                while (r.Read())
                {
                    var node = tree.AddLocation(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
                    if (D(r, 5) is { } x && D(r, 6) is { } y && D(r, 7) is { } z) node.Position = (x, y, z);
                    node.Radius = D(r, 8);
                    node.SpinParentRecord = r.IsDBNull(9) ? "" : r.GetString(9);
                    if (D(r, 10) is { } lx && D(r, 11) is { } ly && D(r, 12) is { } lz) node.Local = (lx, ly, lz);
                    // rot_hours はゲームのデータでは float の値 (例 5.0300002 = float の 5.03) なので、float の精度の最短の 10 進 (5.03) にして使う
                    // (自転の回数が 10 万回近くになるので、5.0300002 のままだと角度が 1.4° ほどずれる。実測は 5.03 で合った)
                    node.RotHours = D(r, 13) is { } rh ? double.Parse(((float)rh).ToString(System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) : null;
                    node.PlanetRadius = D(r, 14);
                    // 天体の枠の回転 (行の順の 3×3。9 つの数で読めたときだけ)
                    if (!r.IsDBNull(15) && r.GetString(15).Split(',') is { Length: 9 } fm
                        && fm.Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray() is { } fv
                        && fv.All(double.IsFinite))
                        node.Frame = fv;
                }
            }
            tree.LinkLocations();
            // 名前 → 星図の場所 (starmap_entities。量子ジャンプの目的地を引く。表が無い前の版の DB では空)
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'starmap_entities'";
                if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0)
                {
                    cmd.CommandText = "SELECT name, starmap_record FROM starmap_entities";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) tree.EntityToStarmapRecord[r.GetString(0)] = r.GetString(1);
                }
            }
            // 店。franchise_name (franchise の英語名) の列が無い前の形の表では、表の name をそのまま使う (franchise の名前があるものとして扱う)
            bool hasFranchiseName;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('game_shops') WHERE name = 'franchise_name'";
                hasFranchiseName = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = hasFranchiseName
                    ? "SELECT shop_id, name, inventory_type, location_record, entity_name, franchise_name FROM game_shops"
                    : "SELECT shop_id, name, inventory_type, location_record, entity_name, name FROM game_shops";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tree.AddShop(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5).Length > 0);
            }
            // 店の売り場の出どころ (場所だけ結んだ価格の店の名前を決めるため: cstone は location の最後の段、uex は名前そのもの)
            var itemSource = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT DISTINCT location, source FROM item_shop_prices WHERE location <> ''";
                using var r = cmd.ExecuteReader();
                while (r.Read()) itemSource.TryAdd(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
            }
            // 紐づけ。前の形の表 (location_record の列が無い) なら、店まで結んだものだけ読む (次の起動で作り直される)
            bool hasLocationColumn;
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('shop_price_links') WHERE name = 'location_record'";
                hasLocationColumn = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = hasLocationColumn
                    ? "SELECT shop_id, source, price_key, location_record FROM shop_price_links"
                    : "SELECT shop_id, source, price_key, '' FROM shop_price_links";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var source = r.GetString(1);
                    var key = r.GetString(2);
                    var dataName = source == "item"
                        ? (itemSource.TryGetValue(key, out var src) && src == "cstone" ? key.Split(" - ")[^1].Trim() : key.Trim())
                        : key.Trim();
                    tree.AddLink(r.GetString(0), source, key, r.GetString(3), dataName);
                }
            }
            tree.ApplyDataNames();
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { return new ShopTree(); }
        if (tree.IsEmpty) return tree;

        // 紐づけた店の売り場の価格 (購入・レンタル) と品目の名前・種類
        var itemKeys = tree.LinkedItemKeys;
        var rows = new List<(string Uuid, string Kind, string Location, double Price, int? RentDays)>();
        if (itemKeys.Count > 0)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT uuid, kind, location, price, rent_days FROM item_shop_prices WHERE kind IN (@b, @r) AND price IS NOT NULL";
                cmd.Parameters.AddWithValue("@b", ShopPriceStore.KindBuy);
                cmd.Parameters.AddWithValue("@r", ShopPriceStore.KindRent);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var location = r.IsDBNull(2) ? "" : r.GetString(2);
                    if (!itemKeys.Contains(location)) continue;
                    rows.Add((r.GetString(0), r.GetString(1), location, r.GetDouble(3), r.IsDBNull(4) ? null : r.GetInt32(4)));
                }
            }
            catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        }
        var itemInfo = new Dictionary<string, (string Record, string Name, string Type)>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count > 0)
        {
            var need = new HashSet<string>(rows.Select(x => x.Uuid), StringComparer.OrdinalIgnoreCase);
            using var loc = new LocReader(_translationsDbPath);
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    SELECT x.uuid, x.record_name, COALESCE(x.name, ''), COALESCE(x.item_type, ''), COALESCE(i.name, '')
                    FROM item_index x LEFT JOIN items i ON i.record_name = x.record_name
                    WHERE x.uuid IN (SELECT uuid FROM item_shop_prices)
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var uuid = r.GetString(0);
                    if (!need.Contains(uuid) || itemInfo.ContainsKey(uuid)) continue;
                    var rec = r.GetString(1);
                    var shortName = rec.StartsWith("EntityClassDefinition.", StringComparison.Ordinal) ? rec["EntityClassDefinition.".Length..] : rec;
                    itemInfo[uuid] = (rec, ResolveItemNameEn(r.GetString(4), r.GetString(2), loc) ?? shortName, DetailTypeLabel(r.GetString(3)));
                }
            }
            catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { }
        }
        foreach (var x in rows)
            tree.AddItemPrice(x.Location, x.Uuid, itemInfo.TryGetValue(x.Uuid, out var info) ? info : ("", x.Uuid, ""), x.Kind, x.Price, x.RentDays);

        // 紐づけたコモディティの端末の価格 (trade_cache.db は読み取り専用で開く)
        var terminals = tree.LinkedTradeKeys;
        if (terminals.Count > 0 && File.Exists(tradeDbPath))
        {
            var resourceByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in OreService.LoadResources(DbPath, _translationsDbPath))
                if (!string.IsNullOrWhiteSpace(x.NameEn)) resourceByName.TryAdd(x.NameEn.Trim(), x.RecordName);
            try
            {
                using var tc = new SqliteConnection($"Data Source={tradeDbPath};Mode=ReadOnly");
                tc.Open();
                using var cmd = tc.CreateCommand();
                cmd.CommandText = "SELECT terminal, COALESCE(commodity_name, ''), COALESCE(price_buy, 0), COALESCE(price_sell, 0) FROM trade_prices WHERE is_current = 1";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var terminal = r.GetString(0);
                    if (!terminals.Contains(terminal)) continue;
                    var name = r.GetString(1).Trim();
                    tree.AddTradePrice(terminal, name, resourceByName.TryGetValue(name, out var res) ? res : "", r.GetDouble(2), r.GetDouble(3));
                }
            }
            catch (SqliteException) { }
        }
        return tree;
    }

    // 木の中のお店・場所・星系の詳細の文字 (［詳細］タブ。GetItemDetailText と同じ形で、リンクは DetailLink)。record が木に無ければ null
    public static string? ShopTreeDetailText(ShopTree tree, string record)
    {
        if (tree.ShopsByRecord.TryGetValue(record, out var shop)) return ShopDetailText(shop);
        if (tree.NodesByRecord.TryGetValue(record, out var node)) return node.IsStar ? SystemDetailText(node) : PlaceDetailText(node);
        return null;
    }

    // 根の Star から node までの道筋「Stanton > ArcCorp > Area18」(各段はリンク)
    private static string PathLinks(ShopTreeNode node)
    {
        var chain = new List<ShopTreeNode>();
        for (var n = node; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        return string.Join(" > ", chain.Select(n => DetailLink(n.Record, n.Name)));
    }

    // お店の詳細: 【基本】名前・種類・星系、【場所】星系から店の場所までの道筋、【売っている物】紐づけた DB の価格の表 (品名の順)
    private static string ShopDetailText(ShopTreeShop shop)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DetailTitle(shop.Name, shop.Kind));
        sb.AppendLine("【基本】");
        sb.AppendLine($"  名前: {shop.Name}");
        sb.AppendLine($"  種類: {shop.Kind}");
        sb.AppendLine($"  星系: {shop.Location.System}");
        sb.AppendLine();
        sb.AppendLine("【場所】");
        sb.AppendLine(PathLinks(shop.Location));
        sb.AppendLine();
        sb.AppendLine("【売っている物】");
        if (shop.Items.Count == 0 && shop.Commodities.Count == 0)
        {
            sb.AppendLine("販売データが紐づいていません。");
            return sb.ToString();
        }
        if (shop.Items.Count > 0)
        {
            var items = shop.Items.Values.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Record, StringComparer.OrdinalIgnoreCase).ToList();
            var hasBuy = items.Any(i => i.Buy.HasValue);
            var hasRent = items.Any(i => i.Rents.Count > 0);
            // 購入とレンタルが両方ある店は列を分ける (購入 / レンタル)。どちらかだけなら、その列だけ
            sb.AppendLine("| 品名 | 種類 |" + (hasBuy ? (hasRent ? " 購入 |" : " 価格 |") : "") + (hasRent ? " レンタル |" : ""));
            sb.AppendLine("|---|---|" + (hasBuy ? "---|" : "") + (hasRent ? "---|" : ""));
            // 品名・種類・値段がすべて同じ行 (UEX の船の店では、同じ船の AI 用などの複製の record にも同じ値段の行がある) は 1 行にまとめ、
            // リンク先は record 名の短いもの (分類「船舶」の代表の選び方と同じ) にする
            var lines = items.Select(i => (i.Record, i.Name, i.Type,
                Buy: i.Buy is { } b ? $"{b:N0} aUEC" : "-",
                Rent: i.Rents.Count > 0
                    ? string.Join(" / ", i.Rents.Distinct().OrderBy(r => r.Days ?? int.MaxValue).ThenBy(r => r.Price).Select(r => $"{(r.Days is { } d ? $"{d}日 " : "")}{r.Price:N0} aUEC"))
                    : "-"));
            foreach (var g in lines.GroupBy(r => (r.Name, r.Type, r.Buy, r.Rent)))
            {
                var rec = g.Select(r => r.Record).Where(r => r.Length > 0).OrderBy(r => r.Length).ThenBy(r => r, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "";
                var line = $"| {NameCell(rec, g.Key.Name)} | {Cell(MdEscape(g.Key.Type))} |";
                if (hasBuy) line += $" {g.Key.Buy} |";
                if (hasRent) line += $" {g.Key.Rent} |";
                sb.AppendLine(line);
            }
        }
        if (shop.Commodities.Count > 0)
        {
            if (shop.Items.Count > 0) sb.AppendLine();
            static string P(double v) => v > 0 ? $"{v:N0} aUEC" : "-";
            sb.AppendLine("| 品名 | 買値 | 売値 |");
            sb.AppendLine("|---|---|---|");
            foreach (var c in shop.Commodities.Distinct().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"| {NameCell(c.ResourceRecord, c.Name)} | {P(c.Buy)} | {P(c.Sell)} |");
        }
        return sb.ToString();
    }

    // 表の品名のセル: record があれば詳細へのリンク、無ければ文字のまま
    private static string NameCell(string record, string name) =>
        record.Length > 0 ? DetailLink(record, name) : Cell(MdEscape(name));

    // 場所の詳細: 【基本】名前・種類・星系、【場所】親の道筋、【この中の場所】子 (種類ごと)、【お店】この場所の店
    private static string PlaceDetailText(ShopTreeNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DetailTitle(node.Name, node.TypeLabel));
        sb.AppendLine("【基本】");
        sb.AppendLine($"  名前: {node.Name}");
        sb.AppendLine($"  種類: {node.TypeLabel}");
        sb.AppendLine($"  星系: {node.System}");
        sb.AppendLine();
        sb.AppendLine("【場所】");
        sb.AppendLine(node.Parent != null ? PathLinks(node.Parent) : "(なし)");
        sb.AppendLine();
        sb.AppendLine("【この中の場所】");
        AppendChildPlaces(sb, node);
        sb.AppendLine();
        sb.AppendLine("【お店】");
        AppendShops(sb, node);
        sb.AppendLine();
        sb.AppendLine("【距離】");
        // 惑星・衛星の自転の角度は今の時刻 (開いた時刻) のもの (CurrentLocation.ThetasAt)
        var root = node.Root;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        var thetas = CurrentLocation.ThetasAt(Descendants(root), nowUnix);
        sb.AppendLine(SpinTimeNote(nowUnix));
        AppendDistances(sb, node, thetas);
        if (root.IsStar && SystemMapSvg(root, node, overlay: new MapOverlay(thetas, null, "")) is { } systemMap)
        {
            sb.AppendLine();
            sb.AppendLine("【星系図】");
            sb.AppendLine(SvgLine(systemMap));
        }
        if (NearMapSvg(node, thetas) is { } nearMap)
        {
            sb.AppendLine();
            sb.AppendLine("【近くの図】");
            sb.AppendLine(SvgLine(nearMap));
        }
        return sb.ToString();
    }

    // 星系の詳細: 【基本】名前、【場所】根の Star の直下の場所 (種類ごと)、【お店】根の Star の店
    private static string SystemDetailText(ShopTreeNode node)
    {
        var sb = new StringBuilder();
        sb.AppendLine(DetailTitle(node.System, "星系"));
        sb.AppendLine("【基本】");
        sb.AppendLine($"  名前: {node.System}");
        sb.AppendLine();
        sb.AppendLine("【場所】");
        AppendChildPlaces(sb, node);
        sb.AppendLine();
        sb.AppendLine("【お店】");
        AppendShops(sb, node);
        // 惑星・衛星の自転の角度は今の時刻 (開いた時刻) のもの
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        if (SystemMapSvg(node, null, overlay: new MapOverlay(CurrentLocation.ThetasAt(Descendants(node), nowUnix), null, "")) is { } systemMap)
        {
            sb.AppendLine();
            sb.AppendLine("【星系図】");
            sb.AppendLine(SpinTimeNote(nowUnix));
            sb.AppendLine(SvgLine(systemMap));
        }
        return sb.ToString();
    }

    // ===== 距離と図 =====

    // 距離の書き方 (【距離】の表と図のラベルで使う。ゲームの表記に合わせる。ゲームの QT の表示は「PYR1 L2 12.6Gm」で、G・M はメートルに付く):
    // 1 km 未満は「67 m」(整数)、1 万 km 未満は「2,630 Km」(km の整数・3 桁区切り)、10 億 m 未満は「42.3 Mm」(百万 m 単位・小数 1 桁)、
    // それ以上は「12.6 Gm」(十億 m 単位・小数 1 桁)
    public static string FormatDistance(double meters)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var km = meters / 1000;
        // 境目は丸めた後の値で決める (9,999.6 Km を「10,000 Km」と出さず「10.0 Mm」にする)
        if (Math.Round(meters) < 1000) return Math.Round(meters).ToString("0", inv) + " m";
        if (Math.Round(km) < 1e4) return Math.Round(km).ToString("#,0", inv) + " Km";
        if (Math.Round(meters / 1e6, 1) < 1000) return (meters / 1e6).ToString("0.0", inv) + " Mm";
        return (meters / 1e9).ToString("0.0", inv) + " Gm";
    }

    private static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    // 【距離】の相手の種類
    private static readonly HashSet<string> DistanceTypes = new(StringComparer.Ordinal) { "Planet", "Moon", "Manmade", "Manmade_VisibleOnInteraction", "LandingZone", "JumpPoint" };
    // 近くの図に描く子の種類
    private static readonly HashSet<string> NearMapTypes = new(StringComparer.Ordinal) { "Planet", "Moon", "Manmade", "Manmade_VisibleOnInteraction", "LandingZone" };
    // 星系図に描く種類 (その星系で座標のある場所のうち、この種類のもの全部)
    private static readonly HashSet<string> SystemPointTypes = new(StringComparer.Ordinal) { "Planet", "Moon", "Manmade", "Manmade_VisibleOnInteraction", "LandingZone", "JumpPoint" };

    private static IEnumerable<ShopTreeNode> Descendants(ShopTreeNode node)
    {
        foreach (var c in node.Children)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    // 詳細に書き添える自転の向きの時刻「（YYYY-MM-DD HH:mm 時点の自転の向き）」(手元の時刻)
    private static string SpinTimeNote(double unix) =>
        $"（{DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(unix * 1000)).LocalDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} 時点の自転の向き）";

    // 【距離】: 同じ星系の座標がある場所 (DistanceTypes。自分は除く) までの距離の表 (名前 (リンク) | 種類 | 距離)。近い順に全部。
    // 位置は惑星・衛星の自転の角度 thetas で回したもの (CurrentLocation.PlacePosition)
    private static void AppendDistances(StringBuilder sb, ShopTreeNode node, IReadOnlyDictionary<ShopTreeNode, double> thetas)
    {
        if (node.Position is null) { sb.AppendLine("位置のデータがありません"); return; }
        var me = CurrentLocation.PlacePosition(node, thetas);
        var list = Descendants(node.Root).Where(n => n != node && n.Position.HasValue && DistanceTypes.Contains(n.Type))
            .Select(n => (Node: n, D: Distance(me, CurrentLocation.PlacePosition(n, thetas)))).OrderBy(x => x.D).ThenBy(x => x.Node.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) { sb.AppendLine("(なし)"); return; }
        sb.AppendLine("| 名前 | 種類 | 距離 |");
        sb.AppendLine("|---|---|---|");
        foreach (var (n, d) in list) sb.AppendLine($"| {DetailLink(n.Record, n.Name)} | {Cell(MdEscape(n.TypeLabel))} | {FormatDistance(d)} |");
    }

    // 図の行「<<svg:BASE64>>」(DetailHtml が SVG にする。文字の欄では「（図は WebView2 が必要です）」にする)
    public static string SvgLine(string svg) => "<<svg:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)) + ">>";

    // 図の枠 (スクリプトが動かないときの大きさ。動けば、スクリプトが枠の実際の大きさ (幅は詳細の幅、高さは CSS の 500px) に合わせる)。
    // MapFit は「全体」の表示で、いちばん遠いもの (と中心) が収まる半径の、枠の短いほうの辺の半分に対する割合 (前の図の 260 / 300 と同じ)
    private const double MapW = 600, MapH = 500, MapFit = 260.0 / 300.0;

    private static string Xml(string s) => System.Security.SecurityElement.Escape(s) ?? "";
    private static string F(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    private static string Href(ShopTreeNode n) => "scjt://detail/" + Uri.EscapeDataString(n.Record);

    // 図の表示 (中心のゲームの座標と倍率 (画面の px / メートル))。DetailHtml のスクリプトと同じ式で、スクリプトが動かないときの最初の表示に使う。
    // 「全体」: (fcx, fcy) を中心に、半径 fr が収まる倍率。倍率の範囲は「全体」の 1/2 から、いちばん近い 2 点 (dmin) の間が枠の短いほうの辺の半分になるまで。
    // iw > 0 なら最初は (icx, icy) を中心に、幅 iw (メートル) が枠の幅に入る倍率 (範囲に収める)、iw が 0 なら「全体」
    private static (double Cx, double Cy, double S) InitialMapView(double fcx, double fcy, double fr, double icx, double icy, double iw, double dmin)
    {
        var fit = Math.Min(MapW, MapH) / 2 * MapFit / fr;
        var min = fit / 2;
        var max = dmin > 0 ? Math.Max(fit, Math.Min(MapW, MapH) / (2 * dmin)) : fit;
        double Clamp(double s) => Math.Max(min, Math.Min(max, s));
        return iw > 0 ? (icx, icy, Clamp(MapW / iw)) : (fcx, fcy, Clamp(fit));
    }

    // これより近い 2 点は同じ位置として扱う (メートル)。Stanton の Green (StarMapObject.Ellis3) と microTech は 0.013 m しか離れておらず、
    // それを「いちばん近い 2 点」にすると拡大の上限が桁外れになるため
    private const double MapSamePoint = 1;

    // 点どうしの xy の平面の距離のうち、MapSamePoint 以上のいちばん小さいもの (無ければ 0)
    private static double MinPairDistance(IReadOnlyList<(double X, double Y)> pts)
    {
        var best = 0.0;
        for (var i = 0; i < pts.Count; i++)
            for (var j = i + 1; j < pts.Count; j++)
            {
                var d = Math.Sqrt(Math.Pow(pts[i].X - pts[j].X, 2) + Math.Pow(pts[i].Y - pts[j].Y, 2));
                if (d >= MapSamePoint && (best == 0 || d < best)) best = d;
            }
        return best;
    }

    // 図の <svg> の開きと背景。data-* は DetailHtml のスクリプトが読む (「全体」の中心と半径、最初の表示の中心と幅、いちばん近い 2 点の距離。どれもメートル)。
    // saveKey があれば data-save (操作の後の表示をアプリへ送る印と、その名前。マップタブの図だけ)。［全体］［＋］［－］は DetailHtml.MapBlock が svg の外に付ける
    private static void AppendMapOpen(StringBuilder sb, double fcx, double fcy, double fr, double icx, double icy, double iw, double dmin, string? saveKey = null)
    {
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"zmap\" viewBox=\"0 0 {F(MapW)} {F(MapH)}\"" +
                  $" data-fcx=\"{F(fcx)}\" data-fcy=\"{F(fcy)}\" data-fr=\"{F(fr)}\" data-icx=\"{F(icx)}\" data-icy=\"{F(icy)}\" data-iw=\"{F(iw)}\" data-dmin=\"{F(dmin)}\"" +
                  (saveKey != null ? $" data-save=\"{Xml(saveKey)}\"" : "") + ">");
        sb.Append($"<rect class=\"bg\" x=\"0.5\" y=\"0.5\" width=\"{F(MapW - 1)}\" height=\"{F(MapH - 1)}\"/>");
    }

    // ===== 印のアイコン =====
    // ステーション (Manmade・Manmade_VisibleOnInteraction) = 六角形の輪と中心の点、ジャンプポイント = ひし形の輪、都市 (LandingZone) = 家の形 (三角の屋根と四角)、
    // 図に描く種類に入らない目立つ色の場所 (Outpost など) = 小さな三角。大きさは画面の px で固定 (拡大率で変えない)。
    // 外径はステーション・ジャンプポイント・都市 12px、小さな三角 8px。目立つ色の場所は +2px。色は CSS (.icon.mark = 印の色、.icon.hl = 目立つ色)

    private static string IconKind(ShopTreeNode n) => n.Type switch
    {
        "Manmade" or "Manmade_VisibleOnInteraction" => "station",
        "JumpPoint" => "jump",
        "LandingZone" => "city",
        _ => "other",
    };

    // アイコンの外径の半分 (px)
    private static double IconHalf(string kind, bool hl) => (kind == "other" ? 4 : 6) + (hl ? 1 : 0);

    // アイコンの形 (中心が (0,0)。h = 外径の半分)
    private static string IconShapes(string kind, double h)
    {
        switch (kind)
        {
            case "station":
                var hex = string.Join(" ", Enumerable.Range(0, 6).Select(i =>
                {
                    var a = Math.PI / 3 * i;
                    return $"{(i == 0 ? "M" : "L")}{F2(h * Math.Cos(a))},{F2(h * Math.Sin(a))}";
                })) + " Z";
                return $"<path class=\"ring\" d=\"{hex}\"/><circle class=\"solid\" r=\"1.5\"/>";
            case "jump":
                return $"<path class=\"ring\" d=\"M0,{F2(-h)} L{F2(h)},0 L0,{F2(h)} L{F2(-h)},0 Z\"/>";
            case "city":
                // 屋根 (三角) と壁 (四角)
                return $"<path class=\"solid\" d=\"M{F2(-h)},{F2(-h * 0.1)} L0,{F2(-h)} L{F2(h)},{F2(-h * 0.1)} Z M{F2(-h * 0.7)},{F2(-h * 0.1)} H{F2(h * 0.7)} V{F2(h)} H{F2(-h * 0.7)} Z\"/>";
            default:
                return $"<path class=\"solid\" d=\"M0,{F2(-h)} L{F2(h)},{F2(h * 0.8)} L{F2(-h)},{F2(h * 0.8)} Z\"/>";
        }
    }

    private static string F2(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    // 印のアイコン 1 つ (data-x / data-y のゲームの座標、data-pr = 点の大きさとして使う半径 (px)。位置は transform。スクリプトが置き直す)
    private static string IconSvg(string kind, bool hl, string at, double x, double y, string? cls = null) =>
        $"<g class=\"icon {cls ?? (hl ? "hl" : "mark")}\" {at} data-pr=\"{F2(IconHalf(kind, hl))}\" transform=\"translate({F(x)},{F(y)})\">{IconShapes(kind, IconHalf(kind, hl))}</g>";

    // ステーション・都市など (Manmade・Manmade_VisibleOnInteraction・LandingZone) で radius があるものの大きさの円 (縁なしの薄い塗り。半径を data-wr に持ち、拡大率に合わせる)。
    // 画面で半径 3px 未満なら隠す (data-hidebelow。スクリプトが拡大率に合わせて出し入れする)。押せない・ラベルの重なりの判定に入れない (<a> の外に置く)。
    // here (現在位置がその中にある場所) は赤の薄い塗り。当たらなければ ""
    private static readonly HashSet<string> AreaTypes = new(StringComparer.Ordinal) { "Manmade", "Manmade_VisibleOnInteraction", "LandingZone" };
    private static string AreaCircle(ShopTreeNode n, string at, double x, double y, double s, bool here)
    {
        if (!AreaTypes.Contains(n.Type) || n.Radius is not { } rad || !(rad > 0)) return "";
        var r = rad * s;
        return $"<circle class=\"area{(here ? " here" : "")}\" data-rec=\"{Xml(n.RecordName)}\" {at} data-wr=\"{F(rad)}\" data-hidebelow=\"3\" cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"{F(r)}\"" +
               (r < 3 ? " visibility=\"hidden\"" : "") + "/>";
    }

    // 図の左下の凡例 (HTML。DetailHtml.MapBlock が図の枠に重ねる。拡大・移動では動かない): ステーション・ジャンプポイント・都市・惑星・衛星の印と名前
    internal static string MapLegendHtml()
    {
        static string Item(string shapes, string name) =>
            $"<span class=\"lg\"><svg class=\"lgicon\" viewBox=\"-8 -8 16 16\" width=\"16\" height=\"16\">{shapes}</svg>{name}</span>";
        return "<div class=\"maplegend\">" +
               Item($"<g class=\"icon mark\">{IconShapes("station", 6)}</g>", "ステーション") +
               Item($"<g class=\"icon mark\">{IconShapes("jump", 6)}</g>", "ジャンプポイント") +
               Item($"<g class=\"icon mark\">{IconShapes("city", 6)}</g>", "都市") +
               Item("<circle class=\"planet\" r=\"6\"/>", "惑星") +
               Item("<circle class=\"planet\" r=\"5\"/>", "衛星") +
               Item("<g class=\"obj\"><path class=\"flag\" d=\"M-4,7 V-7 M-4,-7 L6,-4 L-4,-1 Z\"/></g>", "契約の目標") +
               "</div>";
    }

    // 星系図: 上から見た xy の平面。恒星と、その星系で座標のある場所のうち SystemPointTypes のもの全部 (星系の直下に限らない) を描く。
    // Planet・Moon は円 (radius があれば半径を data-wr に持ち、拡大率に合わせた大きさ。今の点の大きさが下限)、ほかは種類ごとの印のアイコン (IconSvg)。軌道の輪は星系の直下の Planet だけ。
    // 点・輪・ラベルはゲームの座標 (メートル) を data-x / data-y に持ち、表示の位置は DetailHtml のスクリプトが今の拡大率・中心から置く
    // (x / y・transform などの属性は最初の表示の位置。スクリプトが動かないときはこのまま見える)。縮尺は線形。y は上が正 (SVG では下向きなので符号を逆にする)。
    // 最初の表示: view があればそれ (中心と幅。マップタブで覚えた表示)。無く、highlight も無ければ全体 (恒星を真ん中に、いちばん遠いものが収まる)。
    // highlight の場所そのもの (座標が無ければ、親を辿って最初に座標のある場所。恒星まで辿ったら無し) は目立つ色にし (SystemPointTypes に無い種類でも描く)、
    // 最初の表示はそれを中心に、次の 3 つのいちばん大きい幅が枠の幅に入る倍率:
    //   いちばん近いほかの点 (MapSamePoint 未満は同じ位置として除く) までの距離 × 6、その場所の子孫で図に描く点 (SystemPointTypes で座標のあるもの) までのいちばん遠い距離 × 2.2、
    //   その場所の radius × 8
    // ラベルの data-pri は重なったときに残す順 (小さいほど優先。0 = 目立つ色の場所、1 = 恒星、2 = 惑星、3 = 衛星・LandingZone、4 = ステーション・ジャンプポイントなど)。
    // 同じ data-pri の中は、子の場所の数 (data-sub。Children の数 = starmap_locations で parent がその場所の行の数) の多い順、同じ数なら名前の順 (data-no) に置く。
    // 描く順は、惑星・衛星の円を全部先に (衛星・惑星の順)、ステーション・都市・ジャンプポイントなどの印とラベルを後 (上) に。その中は子の少ないほうを先 (多いほうが上)、同じ数なら名前の順。
    // saveKey があれば svg に data-save を付ける (スクリプトが操作の後の表示をアプリへ送る。マップタブの図だけ)。
    // overlay があれば (マップタブの現在位置): 場所を自転の角度で回した位置 (CurrentLocation.PlacePosition) に描き、現在位置の印 (赤い照準。ラベルの優先は 0) を描く。描くものが無ければ null
    internal static string? SystemMapSvg(ShopTreeNode star, ShopTreeNode? highlight, (double Cx, double Cy, double W)? view = null, string? saveKey = null,
        MapOverlay? overlay = null)
    {
        var origin = star.Position ?? (0, 0, 0);
        ShopTreeNode? hl = null;
        for (var n = highlight; n != null && n != star; n = n.Parent) if (n.Position.HasValue) { hl = n; break; }
        if (hl != null && hl.Root != star) hl = null;
        var here = overlay?.InsidePlace;
        var items = SystemMapNodes(star, hl, here);
        if (items.Count == 0) return null;
        double Dxy(ShopTreeNode n) => Math.Sqrt(Math.Pow(n.Position!.Value.X - origin.X, 2) + Math.Pow(n.Position!.Value.Y - origin.Y, 2));
        // 描く位置 (overlay があれば自転の角度で回したもの)
        (double X, double Y, double Z) Pos(ShopTreeNode n) => overlay != null ? CurrentLocation.PlacePosition(n, overlay.Thetas) : n.Position!.Value;
        var max = items.Max(Dxy);
        if (max <= 0) return null;
        // 点 (恒星と items) の xy
        var pts = new List<(double X, double Y)> { (origin.X, origin.Y) };
        pts.AddRange(items.Select(i => (Pos(i).X, Pos(i).Y)));
        var dmin = MinPairDistance(pts);
        double icx = origin.X, icy = origin.Y, iw = 0;
        if (view is { } v && v.W > 0) { icx = v.Cx; icy = v.Cy; iw = v.W; }
        else if (hl != null)
        {
            var h = (X: hl.Position!.Value.X, Y: hl.Position!.Value.Y);
            double D2(double x, double y) => Math.Sqrt(Math.Pow(x - h.X, 2) + Math.Pow(y - h.Y, 2));
            var dn = pts.Select(p => D2(p.X, p.Y)).Where(d => d >= MapSamePoint).DefaultIfEmpty(0).Min();
            var far = Descendants(hl).Where(d => d.Position.HasValue && SystemPointTypes.Contains(d.Type))
                .Select(d => D2(d.Position!.Value.X, d.Position!.Value.Y)).DefaultIfEmpty(0).Max();
            var w = Math.Max(dn * 6, Math.Max(far * 2.2, (hl.Radius ?? 0) * 8));
            if (w > 0) { icx = h.X; icy = h.Y; iw = w; }
        }
        var (vcx, vcy, s) = InitialMapView(origin.X, origin.Y, max, icx, icy, iw, dmin);
        double Sx(double x) => MapW / 2 + (x - vcx) * s;
        double Sy(double y) => MapH / 2 - (y - vcy) * s;
        var sb = new StringBuilder();
        AppendMapOpen(sb, origin.X, origin.Y, max, icx, icy, iw, dmin, saveKey);
        foreach (var p in star.Children.Where(i => i.Type == "Planet" && i.Position.HasValue))
            sb.Append($"<circle class=\"orbit\" data-x=\"{F(origin.X)}\" data-y=\"{F(origin.Y)}\" data-wr=\"{F(Dxy(p))}\" cx=\"{F(Sx(origin.X))}\" cy=\"{F(Sy(origin.Y))}\" r=\"{F(Dxy(p) * s)}\"/>");
        {
            var sx = Sx(origin.X); var sy = Sy(origin.Y);
            sb.Append($"<a href=\"{Href(star)}\"><title>{Xml(star.System)}</title>" +
                      $"<circle class=\"star\" data-x=\"{F(origin.X)}\" data-y=\"{F(origin.Y)}\" cx=\"{F(sx)}\" cy=\"{F(sy)}\" r=\"8\"/>" +
                      $"<text data-pri=\"1\" data-sub=\"{star.Children.Count}\" data-no=\"0\" data-x=\"{F(origin.X)}\" data-y=\"{F(origin.Y)}\" data-ox=\"10\" data-oy=\"-10\" x=\"{F(sx + 10)}\" y=\"{F(sy - 10)}\">{Xml(star.System)}</text></a>");
        }
        // 名前の順の番号 (data-no。同じ data-pri・同じ子の数のラベルの順)
        var nameNo = NameOrder(items);
        static int DrawGroup(ShopTreeNode n) => n.Type == "Moon" ? 0 : n.Type == "Planet" ? 1 : 2;
        foreach (var n in items.OrderBy(DrawGroup).ThenBy(i => i.Children.Count).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
        {
            var pp = Pos(n);
            var px = pp.X; var py = pp.Y;
            var x = Sx(px); var y = Sy(py);
            var at = $"data-x=\"{F(px)}\" data-y=\"{F(py)}\"";
            var isBody = n.Type is "Planet" or "Moon";
            var isHl = n == hl;
            var isHere = n == here;
            // 量子ジャンプの目的地は目立たせる (qtdest)
            var isDest = overlay?.QtDest == n;
            // ラベルの距離: overlay に距離の基準 (現在位置) があれば、そこからの距離 (DistanceSuffix を添える)。無ければ今どおり (惑星は恒星から)
            var label = MapPointLabel(n, overlay, pp, Dxy(n));
            // data-opri: 中にいる場所でないときの優先 (マップタブの書き換えで、中にいる場所が替わったときに戻す)。中にいる場所は優先 0 で curtext を足す
            var opri = isHl ? 0 : n.Type == "Planet" ? 2 : n.Type is "Moon" or "LandingZone" ? 3 : 4;
            var pri = isHere ? 0 : opri;
            var textCls = (isHl ? "hltext" : n.Type == "Planet" ? "" : "small") + (isHere ? " curtext" : "");
            var textAttr = $"class=\"{textCls.Trim()}\" data-pri=\"{pri}\" data-opri=\"{opri}\" data-sub=\"{n.Children.Count}\" data-no=\"{nameNo[n]}\" {at}";
            string shape, text;
            if (isBody)
            {
                var cls = (isHl ? "hl" : "planet") + (isHere ? " here" : "") + (isDest ? " qtdest" : "");
                // 点の大きさ (下限): 惑星 6px (目立つ色は 7px)、衛星 5px (目立つ色は 6px)
                double minR = (n.Type == "Planet" ? 6 : 5) + (isHl ? 1 : 0);
                if (n.Radius is { } rad && rad > 0)
                {
                    // 半径があれば拡大率に合わせた大きさ。ラベルは近くの図の自分の円と同じく円の右上へずらす
                    // (ずらす量 = 円の半径 (200px まで) × 0.7 + data-ox / data-oy。下限の大きさのときに今の位置 (+8, +8) になるよう data-ox / data-oy を決める)
                    var r = Math.Max(minR, rad * s);
                    var off = Math.Min(r, 200) * 0.7;
                    var ox = 8 - minR * 0.7; var oy = 8 + minR * 0.7;
                    shape = $"<circle class=\"{cls}\" {at} data-wr=\"{F(rad)}\" data-minr=\"{F(minR)}\" cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"{F(r)}\"/>";
                    text = $"<text {textAttr} data-wr=\"{F(rad)}\" data-minr=\"{F(minR)}\" data-ox=\"{F(ox)}\" data-oy=\"{F(oy)}\" x=\"{F(x + off + ox)}\" y=\"{F(y - off + oy)}\">{Xml(label)}</text>";
                }
                else
                {
                    shape = $"<circle class=\"{cls}\" {at} cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"{F(minR)}\"/>";
                    text = $"<text {textAttr} data-ox=\"8\" data-oy=\"8\" x=\"{F(x + 8)}\" y=\"{F(y + 8)}\">{Xml(label)}</text>";
                }
            }
            else
            {
                // 印のアイコン。ラベルはアイコンの右 (外径の半分 + 4px)
                var kind = IconKind(n);
                var lx = IconHalf(kind, isHl) + 4;
                shape = IconSvg(kind, isHl, at, x, y, (isHl ? "hl" : "mark") + (isHere ? " here" : "") + (isDest ? " qtdest" : ""));
                text = $"<text {textAttr} data-ox=\"{F2(lx)}\" data-oy=\"8\" x=\"{F(x + lx)}\" y=\"{F(y + 8)}\">{Xml(label)}</text>";
            }
            sb.Append(AreaCircle(n, at, x, y, s, isHere));
            // data-rec: 場所の record 名 (マップタブの書き換えで、どの場所かを見分ける)
            sb.Append($"<a data-rec=\"{Xml(n.RecordName)}\" href=\"{Href(n)}\"><title>{Xml(label)}</title>{shape}{text}</a>");
        }
        // 現在位置の印 (g.curmark): 取った座標の赤い照準 (g.cur)、推定位置の赤い白抜きの丸 (g.est)、量子ジャンプ中の出発地から目的地への矢印 (line.qt。先の三角は marker)、
        // そのラベル (text.mk。優先 0)。大きさ・太さは画面の px で固定。マップタブの図 (saveKey あり) は、使わない部分も隠して置いておく
        // (現在位置が変わったときに、ページを作り直さずにスクリプトで出し入れ・位置・ラベルを書き換えるため。使わないラベルは data-off="1")
        var mk = MarkerOf(overlay);
        if (saveKey != null || mk != null)
        {
            var (mkKind, mkPos, mkFrom, mkLabel) = mk ?? ("", (0d, 0d, 0d), null, "");
            var x = Sx(mkPos.X); var y = Sy(mkPos.Y);
            var from = mkFrom ?? mkPos;
            var at = $"data-x=\"{F(mkPos.X)}\" data-y=\"{F(mkPos.Y)}\"";
            static string Vis(bool on) => on ? "" : " visibility=\"hidden\"";
            sb.Append($"<g class=\"curmark\"><title>{Xml(mkLabel)}</title>" +
                      $"<g class=\"cur\" {at} data-pr=\"10\" transform=\"translate({F(x)},{F(y)})\"{Vis(mkKind == MapOverlay.KindTaken)}><circle r=\"7\"/><path d=\"M-10,0 H-3 M3,0 H10 M0,-10 V-3 M0,3 V10\"/></g>" +
                      $"<g class=\"est\" {at} data-pr=\"10\" transform=\"translate({F(x)},{F(y)})\"{Vis(mkKind == MapOverlay.KindEstimated)}><circle r=\"8\"/></g>" +
                      $"<text class=\"curtext mk\" data-pri=\"0\" data-sub=\"0\" data-no=\"-1\" {at} data-ox=\"14\" data-oy=\"8\" x=\"{F(x + 14)}\" y=\"{F(y + 8)}\"" +
                      (mk == null ? " data-off=\"1\" visibility=\"hidden\"" : "") + $">{Xml(mkLabel)}</text></g>");
        }
        // 量子ジャンプの線 (g.qtgroup): 出発地 (QtFrom) から目的地 (QtTo) への破線と、その真ん中の向きの三角 (marker-mid)、出発地の小さな丸 (g.qtfrom)。
        // 太さ・大きさは画面の px で固定。マップタブの図 (saveKey あり) は、使わないときも隠して置いておく (書き換えで出し入れするため)
        var qtOn = overlay is { QtFrom: not null, QtTo: not null };
        if (saveKey != null || qtOn)
        {
            var qf = overlay?.QtFrom ?? (0d, 0d, 0d);
            var qt = overlay?.QtTo ?? qf;
            double fx = Sx(qf.X), fy = Sy(qf.Y), tx = Sx(qt.X), ty = Sy(qt.Y);
            sb.Append("<defs><marker id=\"qtmid\" viewBox=\"0 0 10 10\" refX=\"5\" refY=\"5\" markerWidth=\"6\" markerHeight=\"6\" orient=\"auto\"><path class=\"qthead\" d=\"M0,0 L10,5 L0,10 Z\"/></marker></defs>");
            sb.Append($"<g class=\"qtgroup\"{(qtOn ? "" : " visibility=\"hidden\"")}>" +
                      $"<polyline class=\"qt\" data-x1=\"{F(qf.X)}\" data-y1=\"{F(qf.Y)}\" data-x2=\"{F(qt.X)}\" data-y2=\"{F(qt.Y)}\" " +
                      $"points=\"{F(fx)},{F(fy)} {F((fx + tx) / 2)},{F((fy + ty) / 2)} {F(tx)},{F(ty)}\" marker-mid=\"url(#qtmid)\"/>" +
                      $"<g class=\"qtfrom\" data-x=\"{F(qf.X)}\" data-y=\"{F(qf.Y)}\" data-pr=\"4\" transform=\"translate({F(fx)},{F(fy)})\"><circle r=\"4\"/></g>" +
                      // 線の先が契約の目標 (推定) のときの旗とラベル (優先 0。使わないときは隠し、ラベルは data-off)
                      $"<g class=\"qtgoalslot\"><g class=\"qtgoal\" data-x=\"{F(qt.X)}\" data-y=\"{F(qt.Y)}\" data-pr=\"9\" transform=\"translate({F(tx)},{F(ty)})\"" +
                      (overlay?.QtGoal == true ? "" : " visibility=\"hidden\"") + $"><path class=\"flag\" transform=\"scale(1.4)\" d=\"M-4,7 V-7 M-4,-7 L6,-4 L-4,-1 Z\"/></g>" +
                      $"<text class=\"curtext goaltext\" data-pri=\"0\" data-sub=\"0\" data-no=\"-1\" data-x=\"{F(qt.X)}\" data-y=\"{F(qt.Y)}\" data-ox=\"14\" data-oy=\"8\" x=\"{F(tx + 14)}\" y=\"{F(ty + 8)}\"" +
                      (overlay?.QtGoal == true ? "" : " data-off=\"1\" visibility=\"hidden\"") + ">契約の目標（推定）</text></g></g>");
        }
        // 最近の契約の目標 (橙の旗と「契約の目標」。固定の大きさ)。マップタブの図は ObjectiveSlots 個の入れ物を隠して置いておく (書き換えで出し入れするため)
        var objs = overlay?.Objectives ?? Array.Empty<(double X, double Y, double Z)>();
        if (saveKey != null || objs.Count > 0)
        {
            sb.Append("<g class=\"objmarks\">");
            for (int i = 0; i < Math.Max(objs.Count, saveKey != null ? ObjectiveSlots : 0); i++)
            {
                var on = i < objs.Count;
                (double X, double Y, double Z) op = on ? objs[i] : (0d, 0d, 0d);
                double ox = Sx(op.X), oy = Sy(op.Y);
                var oat = $"data-x=\"{F(op.X)}\" data-y=\"{F(op.Y)}\"";
                sb.Append($"<g class=\"objslot\"><title>契約の目標</title><g class=\"obj\" {oat} data-pr=\"7\" transform=\"translate({F(ox)},{F(oy)})\"{(on ? "" : " visibility=\"hidden\"")}>" +
                          $"<path class=\"flag\" d=\"M-4,7 V-7 M-4,-7 L6,-4 L-4,-1 Z\"/></g>" +
                          $"<text class=\"small objtext\" data-pri=\"1\" data-sub=\"0\" data-no=\"-1\" {oat} data-ox=\"10\" data-oy=\"8\" x=\"{F(ox + 10)}\" y=\"{F(oy + 8)}\"" +
                          (on ? "" : " data-off=\"1\" visibility=\"hidden\"") + ">契約の目標</text></g>");
            }
            sb.Append("</g>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // マップタブの図に置いておく最近の契約の目標の入れ物の数
    internal const int ObjectiveSlots = 5;

    // 星系図に描く場所: その星系で座標のある場所のうち SystemPointTypes のもの全部と、目立つ色の場所 (hl)・現在位置がその中にある場所 (here)
    private static List<ShopTreeNode> SystemMapNodes(ShopTreeNode star, ShopTreeNode? hl, ShopTreeNode? here) =>
        Descendants(star).Where(c => c.Position.HasValue && (SystemPointTypes.Contains(c.Type) || c == hl || c == here)).ToList();

    // 星系図の場所のラベル: 現在位置がその中にある場所は overlay.CurrentLabel、overlay に距離の基準 (現在位置) があれば「名前 (そこからの距離 + DistanceSuffix)」、
    // 無ければ今どおり (惑星は「名前 (恒星からの xy の距離 dxy)」、ほかは名前)。pp はその場所を描く位置
    private static string MapPointLabel(ShopTreeNode n, MapOverlay? overlay, (double X, double Y, double Z) pp, double dxy) =>
        overlay?.InsidePlace == n ? overlay.CurrentLabel
        : overlay?.DistanceFrom is { } df ? $"{n.Name} ({FormatDistance(Distance(df, pp))}{overlay.DistanceSuffix})"
        : n.Type == "Planet" ? $"{n.Name} ({FormatDistance(dxy)})" : n.Name;

    // 現在位置の印 (種類・位置・矢印の出発地・ラベル)。取った座標でその場所の中にいるときは無し (その場所を赤で描く)
    internal static (string Kind, (double X, double Y, double Z) Pos, (double X, double Y, double Z)? From, string Label)? MarkerOf(MapOverlay? overlay) => overlay switch
    {
        { Kind: MapOverlay.KindEstimated, Current: { } est } => (MapOverlay.KindEstimated, est, null, overlay.CurrentLabel),
        { Kind: MapOverlay.KindTaken, Current: { } cur, InsidePlace: null } => (MapOverlay.KindTaken, cur, null, overlay.CurrentLabel),
        _ => null,
    };

    // マップタブの書き換えのデータ (ページを作り直さずにスクリプトで書き換える): 星系図の各場所の record 名・描く位置 (自転を入れたもの)・ラベル。
    // 決まりは SystemMapSvg と同じ (目立つ色の場所は無し)
    internal static List<(string Record, double X, double Y, string Label)> SystemMapPointStates(ShopTreeNode star, MapOverlay overlay)
    {
        var origin = star.Position ?? (0, 0, 0);
        var list = new List<(string, double, double, string)>();
        foreach (var n in SystemMapNodes(star, null, overlay.InsidePlace))
        {
            var pp = CurrentLocation.PlacePosition(n, overlay.Thetas);
            var dxy = Math.Sqrt(Math.Pow(n.Position!.Value.X - origin.X, 2) + Math.Pow(n.Position!.Value.Y - origin.Y, 2));
            list.Add((n.RecordName, pp.X, pp.Y, MapPointLabel(n, overlay, pp, dxy)));
        }
        return list;
    }

    // 名前の順 (OrdinalIgnoreCase。同じ名前は record 名の順) の番号
    private static Dictionary<ShopTreeNode, int> NameOrder(IEnumerable<ShopTreeNode> nodes) =>
        nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.RecordName, StringComparer.Ordinal)
             .Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i);

    // 近くの図: その場所を真ん中に、座標がある子 (NearMapTypes) を xy の平面で描く。自分の半径があれば円の大きさで描く (拡大率に合わせて変わる。最小 4px)。
    // 座標の持ち方は星系図と同じ (data-x / data-y。自分の円とラベルは半径も data-wr)。最初の表示は全体 (いちばん遠い子と自分の半径が収まる線形)。
    // 子の Planet・Moon は 5px の円、ほかは星系図と同じ印のアイコン。
    // ラベルの data-pri: 0 = 自分、2 = 惑星、3 = 衛星・都市、4 = ステーションなど。同じ data-pri の中は星系図と同じく子の場所の数 (data-sub) の多い順、同じ数なら名前の順 (data-no)。
    // 描く順は、自分の円、子の惑星・衛星の円、ステーション・都市などの印 (上) の順。その中は子の少ないほうを先 (多いほうが上)、同じ数なら名前の順。描く子が無ければ null
    internal static string? NearMapSvg(ShopTreeNode center, IReadOnlyDictionary<ShopTreeNode, double> thetas)
    {
        if (center.Position is null) return null;
        // 位置は惑星・衛星の自転の角度 thetas で回したもの
        (double X, double Y, double Z) Pos(ShopTreeNode n) => CurrentLocation.PlacePosition(n, thetas);
        var c = Pos(center);
        var kids = center.Children.Where(k => k.Position.HasValue && NearMapTypes.Contains(k.Type)).ToList();
        if (kids.Count == 0) return null;
        double Dxy(ShopTreeNode n) => Math.Sqrt(Math.Pow(Pos(n).X - c.X, 2) + Math.Pow(Pos(n).Y - c.Y, 2));
        var max = Math.Max(kids.Max(Dxy), center.Radius ?? 0);
        if (max <= 0) return null;
        var pts = new List<(double X, double Y)> { (c.X, c.Y) };
        pts.AddRange(kids.Select(k => (Pos(k).X, Pos(k).Y)));
        var dmin = MinPairDistance(pts);
        var (vcx, vcy, s) = InitialMapView(c.X, c.Y, max, c.X, c.Y, 0, dmin);
        double Sx(double x) => MapW / 2 + (x - vcx) * s;
        double Sy(double y) => MapH / 2 - (y - vcy) * s;
        var sb = new StringBuilder();
        AppendMapOpen(sb, c.X, c.Y, max, c.X, c.Y, 0, dmin);
        var cx = Sx(c.X); var cy = Sy(c.Y);
        var cat = $"data-x=\"{F(c.X)}\" data-y=\"{F(c.Y)}\"";
        sb.Append($"<g><title>{Xml(center.Name)}</title>");
        if (center.Radius is { } rad && rad > 0)
        {
            // 半径があれば円の大きさ (最小 4px)。ラベルは円の右上 (円の半径 (200px まで) の 0.7 倍 + 6px ずらす)
            var selfR = Math.Max(4, rad * s);
            var off = Math.Min(selfR, 200) * 0.7;
            sb.Append($"<circle class=\"self\" {cat} data-wr=\"{F(rad)}\" data-minr=\"4\" cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(selfR)}\"/>");
            sb.Append($"<text class=\"hltext\" data-pri=\"0\" data-sub=\"{center.Children.Count}\" data-no=\"0\" {cat} data-wr=\"{F(rad)}\" data-minr=\"4\" data-ox=\"6\" data-oy=\"-6\" x=\"{F(cx + off + 6)}\" y=\"{F(cy - off - 6)}\">{Xml(center.Name)}</text>");
        }
        else
        {
            // 半径が無ければ 6px の円。ラベルは 6 × 0.7 + 6px ずらす
            sb.Append($"<circle class=\"self\" {cat} cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"6\"/>");
            sb.Append($"<text class=\"hltext\" data-pri=\"0\" data-sub=\"{center.Children.Count}\" data-no=\"0\" {cat} data-ox=\"10.2\" data-oy=\"-10.2\" x=\"{F(cx + 10.2)}\" y=\"{F(cy - 10.2)}\">{Xml(center.Name)}</text>");
        }
        sb.Append("</g>");
        var nameNo = NameOrder(kids);
        foreach (var n in kids.OrderBy(k => k.Type is "Planet" or "Moon" ? 0 : 1).ThenBy(k => k.Children.Count).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            var pp = Pos(n);
            var px = pp.X; var py = pp.Y;
            var x = Sx(px); var y = Sy(py);
            var at = $"data-x=\"{F(px)}\" data-y=\"{F(py)}\"";
            var label = $"{n.Name} ({FormatDistance(Distance(c, pp))})";
            var pri = n.Type == "Planet" ? 2 : n.Type is "Moon" or "LandingZone" ? 3 : 4;
            var textAttr = $"data-pri=\"{pri}\" data-sub=\"{n.Children.Count}\" data-no=\"{nameNo[n]}\" {at}";
            string shape, text;
            if (n.Type is "Planet" or "Moon")
            {
                shape = $"<circle class=\"planet\" {at} cx=\"{F(x)}\" cy=\"{F(y)}\" r=\"5\"/>";
                text = $"<text {textAttr} data-ox=\"8\" data-oy=\"8\" x=\"{F(x + 8)}\" y=\"{F(y + 8)}\">{Xml(label)}</text>";
            }
            else
            {
                var kind = IconKind(n);
                var lx = IconHalf(kind, false) + 4;
                shape = IconSvg(kind, false, at, x, y);
                text = $"<text {textAttr} data-ox=\"{F2(lx)}\" data-oy=\"8\" x=\"{F(x + lx)}\" y=\"{F(y + 8)}\">{Xml(label)}</text>";
            }
            sb.Append(AreaCircle(n, at, x, y, s, false));
            sb.Append($"<a href=\"{Href(n)}\"><title>{Xml(label)}</title>{shape}{text}</a>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // 子の場所の表 (種類 | 名前 (リンク))。種類の順 (ShopTree.TypeOrder)、同じ種類は名前の順。無ければ「(なし)」
    private static void AppendChildPlaces(StringBuilder sb, ShopTreeNode node)
    {
        if (node.Children.Count == 0) { sb.AppendLine("(なし)"); return; }
        sb.AppendLine("| 種類 | 名前 |");
        sb.AppendLine("|---|---|");
        foreach (var c in node.Children.OrderBy(c => ShopTree.TypeOrder(c.Type)).ThenBy(c => c.TypeLabel, StringComparer.Ordinal)
                     .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.RecordName, StringComparer.Ordinal))
            sb.AppendLine($"| {Cell(MdEscape(c.TypeLabel))} | {DetailLink(c.Record, c.Name)} |");
    }

    // この場所の店の表 (店名 (リンク) | 種類。名前の順)。無ければ「(なし)」
    private static void AppendShops(StringBuilder sb, ShopTreeNode node)
    {
        if (node.Shops.Count == 0) { sb.AppendLine("(なし)"); return; }
        sb.AppendLine("| 店名 | 種類 |");
        sb.AppendLine("|---|---|");
        foreach (var s in node.Shops.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal))
            sb.AppendLine($"| {DetailLink(s.Record, s.Name)} | {Cell(MdEscape(s.Kind))} |");
    }
}

// ［詳細］タブのお店・ステーション・星系 (GameDataQueryService.BuildShopTree)。作った後は読むだけ (別のスレッドから読んでよい)
public sealed class ShopTree
{
    // 詳細の record の頭 (既存の record (EntityClassDefinition.… / ResourceType.… / uuid) と重ならない)。
    // Shop:<game_shops.shop_id>、Place:<starmap_locations.record_name>、System:<星系の名前 (根の Star の英語名)>、
    // PriceShop:<source>|<price_key> (場所だけ結べた価格の行だけで作った店。shop_price_links の shop_id が空の行)
    public const string ShopPrefix = "Shop:", PlacePrefix = "Place:", SystemPrefix = "System:", PriceShopPrefix = "PriceShop:";
    // 店の種類 (game_shops.inventory_type が ITEM / COMMODITY)
    public const string KindEquipment = "装備の店", KindTerminal = "コモディティの端末";

    public static bool IsTreeRecord(string? record) =>
        record != null && (record.StartsWith(ShopPrefix, StringComparison.Ordinal) || record.StartsWith(PlacePrefix, StringComparison.Ordinal)
                           || record.StartsWith(SystemPrefix, StringComparison.Ordinal) || record.StartsWith(PriceShopPrefix, StringComparison.Ordinal));

    // 星図の種類 (StarMapObjectType の name) の日本語名。ゲームの訳 (Markers_Subtext_… の日本語) があるものはそれ、無いものはアプリで付けた名前。表に無い種類は英語のまま
    private static readonly Dictionary<string, string> TypeLabels = new(StringComparer.Ordinal)
    {
        ["Star"] = "星",
        ["Planet"] = "惑星",
        ["Moon"] = "衛星",
        ["LandingZone"] = "居住地",
        ["Manmade"] = "人工",
        ["Manmade_VisibleOnInteraction"] = "人工 (近づくと表示)",
        ["JumpPoint"] = "ジャンプポイント",
        ["Outpost"] = "前哨基地",
        ["Outpost_InvalidQT"] = "前哨基地 (QT 不可)",
        ["PointOfInterest"] = "注目地点",
        ["Asteroid"] = "小惑星",
        ["Asteroid_ValidQT"] = "小惑星 (QT 可)",
        ["Anomaly"] = "異常域",
        ["NavPoint"] = "航行ポイント",
        ["QuantumTracePoint"] = "量子トレースポイント",
        ["CardinalPoint"] = "方位点",
        ["YouAreHere"] = "現在地",
        ["SolarSystem"] = "星系",
    };
    private static readonly string[] TypeOrderList =
        ["Planet", "Moon", "LandingZone", "Manmade", "Manmade_VisibleOnInteraction", "JumpPoint", "Outpost", "Outpost_InvalidQT", "PointOfInterest",
         "Asteroid", "Asteroid_ValidQT", "Anomaly", "NavPoint", "QuantumTracePoint", "CardinalPoint", "YouAreHere"];
    public static string TypeLabel(string type) => TypeLabels.TryGetValue(type, out var ja) ? ja : type;
    public static int TypeOrder(string type) { var i = Array.IndexOf(TypeOrderList, type); return i >= 0 ? i : TypeOrderList.Length; }
    // 分類「ステーション」に入れる種類 (このほかに、店が 1 つ以上ある場所も入れる)
    public static readonly HashSet<string> StationTypes = new(StringComparer.Ordinal) { "Manmade", "Manmade_VisibleOnInteraction", "LandingZone" };

    private readonly Dictionary<string, ShopTreeNode> _byStarmapRecord = new(StringComparer.Ordinal);
    public Dictionary<string, ShopTreeShop> ShopsByRecord { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ShopTreeNode> NodesByRecord { get; } = new(StringComparer.Ordinal);
    // 名前 (socpak の Child の entityName・子の socpak のファイル名) → 星図の場所の record 名 (starmap_entities。大文字小文字は区別しない)
    public Dictionary<string, string> EntityToStarmapRecord { get; } = new(StringComparer.OrdinalIgnoreCase);

    // 星図の場所の record 名 (StarMapObject.…) の場所 (無ければ null)
    public ShopTreeNode? NodeByStarmapRecord(string record) => _byStarmapRecord.TryGetValue(record, out var n) ? n : null;

    // 名前 (量子ジャンプの目的地など) の場所 (starmap_entities で引く。無ければ null)
    public ShopTreeNode? NodeByEntityName(string name) =>
        EntityToStarmapRecord.TryGetValue(name.Trim(), out var rec) ? NodeByStarmapRecord(rec) : null;
    // 価格の名前 → 店の record (shop_price_links)。item: item_shop_prices.location、trade: trade_prices.terminal
    private readonly Dictionary<string, string> _itemLinks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tradeLinks = new(StringComparer.Ordinal);

    public bool IsEmpty => _byStarmapRecord.Count == 0;
    // 根の Star (星系)
    public IEnumerable<ShopTreeNode> Systems => _byStarmapRecord.Values.Where(n => n.IsStar);
    // 根の Star 以外の場所すべて
    public IEnumerable<ShopTreeNode> Places => _byStarmapRecord.Values.Where(n => !n.IsStar);
    public IEnumerable<ShopTreeShop> Shops => ShopsByRecord.Values;
    public HashSet<string> LinkedItemKeys => new(_itemLinks.Keys, StringComparer.Ordinal);
    public HashSet<string> LinkedTradeKeys => new(_tradeLinks.Keys, StringComparer.Ordinal);

    public ShopTreeNode AddLocation(string record, string name, string type, string parent, string system)
    {
        var isStar = parent.Length == 0 && type == "Star";
        var node = new ShopTreeNode(isStar ? SystemPrefix + system : PlacePrefix + record, record, name, type, TypeLabel(type), parent, system, isStar);
        _byStarmapRecord[record] = node;
        NodesByRecord[node.Record] = node;
        return node;
    }

    // 親子と自転の親をつなぐ (AddLocation を全部終えてから呼ぶ)
    public void LinkLocations()
    {
        foreach (var n in _byStarmapRecord.Values)
        {
            if (n.ParentRecord.Length > 0 && _byStarmapRecord.TryGetValue(n.ParentRecord, out var p)) { n.Parent = p; p.Children.Add(n); }
            if (n.SpinParentRecord.Length > 0 && _byStarmapRecord.TryGetValue(n.SpinParentRecord, out var sp)) n.SpinParent = sp;
        }
    }

    // hasFranchiseName: 名前が franchise の英語名か (false なら Entity 名から作った名前。ApplyDataNames で販売データの店名に替えることがある)
    public void AddShop(string shopId, string name, string inventoryType, string location, string entityName, bool hasFranchiseName)
    {
        if (!_byStarmapRecord.TryGetValue(location, out var node)) return;
        // ITEM / COMMODITY 以外 (ゲームデータに INVALID・空のものがある) は「不明 (値)」
        var kind = inventoryType.Equals("COMMODITY", StringComparison.OrdinalIgnoreCase) ? KindTerminal
            : inventoryType.Equals("ITEM", StringComparison.OrdinalIgnoreCase) ? KindEquipment
            : inventoryType.Length > 0 ? $"不明 ({inventoryType})" : "不明";
        var s = new ShopTreeShop(shopId, name, kind, node, entityName, ShopPrefix) { HasFranchiseName = hasFranchiseName };
        ShopsByRecord[s.Record] = s;
        node.Shops.Add(s);
    }

    // 紐づけ 1 行。shopId があればそのゲームの店に結ぶ。shopId が空 (場所だけ結べた価格) なら、その価格の行だけの店
    // (record は PriceShop:<source>|<price_key>、名前は「販売データの店名（販売データ）」、種類は item なら装備の店・trade ならコモディティの端末) を
    // 結んだ場所 (locationRecord) に作って結ぶ
    public void AddLink(string shopId, string source, string priceKey, string locationRecord, string dataShopName)
    {
        if (source is not ("item" or "trade")) return;
        string record;
        if (shopId.Length > 0)
        {
            record = ShopPrefix + shopId;
            if (!ShopsByRecord.ContainsKey(record)) return;
        }
        else
        {
            if (!_byStarmapRecord.TryGetValue(locationRecord, out var node)) return;
            var s = new ShopTreeShop($"{source}|{priceKey}", $"{dataShopName}（販売データ）", source == "item" ? KindEquipment : KindTerminal, node, "", PriceShopPrefix);
            record = s.Record;
            if (!ShopsByRecord.TryAdd(record, s)) return;
            node.Shops.Add(s);
        }
        if (source == "item") { if (!_itemLinks.TryAdd(priceKey, record)) return; }
        else if (!_tradeLinks.TryAdd(priceKey, record)) return;
        if (shopId.Length > 0) ShopsByRecord[record].LinkedDataNames.Add(dataShopName);
    }

    // 店の表示名: franchise の英語名があればそれ。無い店で、店まで結べた価格のキーが 1 つだけなら、その販売データの店名
    // (cstone は location の最後の段、trade は端末名、uex は名前)。それ以外は Entity 名から作った名前のまま。AddLink を全部終えてから呼ぶ
    public void ApplyDataNames()
    {
        foreach (var s in ShopsByRecord.Values)
            if (!s.HasFranchiseName && s.LinkedDataNames.Count == 1 && s.LinkedDataNames[0].Length > 0) s.Name = s.LinkedDataNames[0];
    }

    public void AddItemPrice(string location, string uuid, (string Record, string Name, string Type) info, string kind, double price, int? rentDays)
    {
        if (_itemLinks.TryGetValue(location, out var rec) && ShopsByRecord.TryGetValue(rec, out var shop)) shop.AddItem(uuid, info, kind, price, rentDays);
    }

    public void AddTradePrice(string terminal, string name, string resourceRecord, double buy, double sell)
    {
        if (_tradeLinks.TryGetValue(terminal, out var rec) && ShopsByRecord.TryGetValue(rec, out var shop))
            shop.Commodities.Add(new ShopTreeShop.Commodity(name, resourceRecord, buy, sell));
    }

    // 品目の詳細の販売場所の行の店の record (shop_price_links で結べたものだけ。結べなければ null)。
    //   cstone / uex: 売り場の文字 (item_shop_prices.location) で引く
    //   UEX の items_prices (「星系 > … > 端末名」): 最後の段 (端末名) をコモディティの端末の名前 (trade_prices.terminal) として引く
    public string? FindShopRecord(string source, string location)
    {
        if (source is "cstone" or "uex") return _itemLinks.TryGetValue(location ?? "", out var r) ? r : null;
        if (source != ShopListing.SourceUexItemsPrices) return null;
        var segs = (location ?? "").Split(" > ");
        var terminal = segs[^1].Trim();
        return terminal.Length > 0 && _tradeLinks.TryGetValue(terminal, out var t) ? t : null;
    }
}

// 星系図に重ねるもの (マップタブの現在位置): Thetas = Planet・Moon ごとの自転の角度 (ラジアン。分かるものだけ)、Current = 現在位置 (量子ジャンプ中は目的地。無ければ null)、
// CurrentLabel = その印のラベル、Kind = 印の形 (taken = 取った座標の照準、est = 推定位置の白抜きの丸、moving = 量子ジャンプ中の矢印)、ArrowFrom = 矢印の出発地、
// DistanceFrom / DistanceSuffix = 各場所のラベルの距離の基準と、距離に添える文字 (無ければ今どおり)、InsidePlace = 現在位置がその中にある場所 (赤で描き、ラベルを CurrentLabel にする)
// QtFrom / QtTo = 量子ジャンプの線の出発地と目的地 (無ければ線は無し)、QtDest = 量子ジャンプの目的地 (目立たせる)、
// QtGoal = 線の先が契約の目標 (推定。旗と「契約の目標（推定）」を出す)、Objectives = 地図に出す最近の契約の目標の位置 (橙の旗と「契約の目標」)
internal sealed record MapOverlay(IReadOnlyDictionary<ShopTreeNode, double> Thetas, (double X, double Y, double Z)? Current, string CurrentLabel,
    string Kind = MapOverlay.KindTaken, (double X, double Y, double Z)? ArrowFrom = null, (double X, double Y, double Z)? DistanceFrom = null,
    string DistanceSuffix = "", ShopTreeNode? InsidePlace = null, (double X, double Y, double Z)? QtFrom = null, (double X, double Y, double Z)? QtTo = null,
    ShopTreeNode? QtDest = null, bool QtGoal = false, IReadOnlyList<(double X, double Y, double Z)>? Objectives = null)
{
    public const string KindTaken = "taken", KindEstimated = "est", KindMoving = "moving";
}

// 星図の場所 1 つ (starmap_locations の 1 行)。Record は詳細の record (根の Star は System:<星系>、ほかは Place:<record 名>)
public sealed class ShopTreeNode(string record, string recordName, string name, string type, string typeLabel, string parentRecord, string system, bool isStar)
{
    public string Record { get; } = record;
    public string RecordName { get; } = recordName;
    public string Name { get; } = name;
    public string Type { get; } = type;
    public string TypeLabel { get; } = typeLabel;
    public string ParentRecord { get; } = parentRecord;
    public string System { get; } = system;
    public bool IsStar { get; } = isStar;
    public ShopTreeNode? Parent { get; set; }
    public List<ShopTreeNode> Children { get; } = new();
    public List<ShopTreeShop> Shops { get; } = new();
    // 星系の恒星からの絶対座標 (メートル。starmap_locations の x / y / z。無ければ null) と半径 (StarMapObject の size。無ければ null)
    public (double X, double Y, double Z)? Position { get; set; }
    public double? Radius { get; set; }
    // 自転の親 (祖先で最初の Planet・Moon。starmap_locations.spin_parent。無ければ空 / null) と、その座標からの相対位置 (自転の角度 0 のとき。lx / ly / lz。無ければ null)
    public string SpinParentRecord { get; set; } = "";
    public ShopTreeNode? SpinParent { get; set; }
    public (double X, double Y, double Z)? Local { get; set; }
    // Planet・Moon の 1 回転にかかる時間 (時間。starmap_locations.rot_hours。読めなければ null。0 は回らない)
    public double? RotHours { get; set; }
    // Planet・Moon の半径 (メートル。starmap_locations.planet_radius = socpak の planetRadius。読めなければ null)。地表からの高さに使う (無ければ Radius)
    public double? PlanetRadius { get; set; }
    // Planet・Moon の枠の回転 (恒星の回転 × 天体の回転。行の順の 3×3。starmap_locations.frame_matrix。無ければ null)
    public double[]? Frame { get; set; }
    // 地表からの高さに使う半径 (PlanetRadius、無ければ Radius)
    public double? SurfaceRadius => PlanetRadius ?? Radius;
    // この場所の星系の恒星 (根の Star)
    public ShopTreeNode Root { get { var n = this; while (n.Parent != null) n = n.Parent; return n; } }
}

// ゲームの店 1 つ (game_shops の 1 行)、または場所だけ結べた価格の行だけで作った店 (recordPrefix が PriceShop:)。Items / Commodities は紐づけた DB の価格
public sealed class ShopTreeShop(string id, string name, string kind, ShopTreeNode location, string entityName, string recordPrefix)
{
    public string Id { get; } = id;
    // 表示名 (ShopTree.ApplyDataNames で販売データの店名に替えることがある)
    public string Name { get; set; } = name;
    // 名前が franchise の英語名か / 店まで結べた価格のキーの販売データの店名 (キーごとに 1 つ)
    public bool HasFranchiseName { get; init; } = true;
    public List<string> LinkedDataNames { get; } = new();
    public string Kind { get; } = kind;
    public ShopTreeNode Location { get; } = location;
    public string EntityName { get; } = entityName;
    public string Record => recordPrefix + Id;

    // 店の売り場の品目 (uuid ごと)。Buy は購入の値段 (同じ品目に複数あれば安いほう)、Rents はレンタルの (日数, 値段)
    public sealed class Item(string record, string name, string type)
    {
        public string Record { get; } = record;
        public string Name { get; } = name;
        public string Type { get; } = type;
        public double? Buy { get; set; }
        public List<(int? Days, double Price)> Rents { get; } = new();
    }
    public Dictionary<string, Item> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    // コモディティの端末の品目。ResourceRecord は品名が resource_types の英語名と同じ資源の record 名 (無ければ "")
    public sealed record Commodity(string Name, string ResourceRecord, double Buy, double Sell);
    public List<Commodity> Commodities { get; } = new();

    public void AddItem(string uuid, (string Record, string Name, string Type) info, string kind, double price, int? rentDays)
    {
        if (!Items.TryGetValue(uuid, out var item)) Items[uuid] = item = new Item(info.Record, info.Name, info.Type);
        if (kind == ShopPriceStore.KindBuy) item.Buy = item.Buy is { } b ? Math.Min(b, price) : price;
        else if (kind == ShopPriceStore.KindRent) item.Rents.Add((rentDays, price));
    }
}
