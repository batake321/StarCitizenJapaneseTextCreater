using System.Globalization;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 現在位置のオーバーレイ (OreOverlayWindow) に出す「今いる場所に出る岩・資源」(2026-11-05、利用者の指示「ここにはこんなのが出ますよ」)。
// ゲームのプロセスには触らない。使うのは gamedata_cache.db の ore_locations / ore_signatures (［鉱石］タブ・［詳細］と同じ表) と、
// 品質の表 ore_quality / ore_rock_parts (GameDataExtractor.DeliverableSources.cs。無い DB では品質と倍率を省く)、名前の訳 (translations.db)。
//   場所: 取った座標から決める (Resolve)。
//     1. 天体でない場所: 現在位置の星系の星図の場所のうち種類が Asteroid / Asteroid_ValidQT のもので、その中か付近
//        (CurrentLocation.InsideOrNearRange = WhereaboutsOf の中・付近の距離) にあり、英語名が ore_locations.place に一致する (OreSignatureCalc.MatchPlace)、いちばん近いもの。
//        (2026-11-05 の gamedata_cache.db で、ore_locations の場所 398 のうち天体 (Planet / Moon) の 25 を除く 373 について、368 がこの 2 つの種類の
//         星図の場所の英語名と一致することを確かめた。星図の場所に同じ名前の無い Aaron Halo・Glaciem Ring・Keeger Belt・場所不明の 2 つは、この決め方では当たらない)
//     2. 無ければ［鉱石］タブと同じ: 近くの天体 (CurrentLocation.NearBody。Planet / Moon) の名前が一致するもの (OrePanel.PreselectSigPlaceAsync と同じ)
//     3. 無ければ、採掘できる場所の広がり (ore_areas。GameDataExtractor.DeliverableSources.cs) の中にいるもの。星図の場所に名前の無い小惑星帯
//        (Aaron Halo = 小惑星の輪の内側〜外側の半径と厚み、Glaciem Ring・Keeger Belt = ガス雲を収める箱) を、ゲームデータの広がりの値そのままで決める (Contains)。
//        表の無い DB では、この段は無い。見るのはアプリが決めた星系の広がり
//     4. 無ければ、どの場所の中・付近でもないときに限り、ほかの星系の広がりの中にいるもの (今の星系が分かっていればその星系だけ。Resolve の説明)
//     5. どれにも当たらなければ、場所の名前 (分かる範囲) と「ここで採掘できる物のデータはありません」
//   中身: 掘り方 (船 → ROC → 手) ごとに、岩 1 つを 1 行。並び・出やすさ・割合の書き方は［詳細］の【採掘できる場所】(GameDataQueryService.ResourceDetailText) と同じ、
//     出やすさは場所に preset が 2 つ以上あれば OreSignatureCalc.ChanceText と同じ「a% / b%」、格・シグネチャーは［鉱石］タブと同じ (OreSignatureCalc)。
//   品質 (2026-11-05、利用者の指示「品質も出るなら出してほしい」): ゲームデータの値そのまま (分布の min〜max と stddev、岩の中の行ごとの qualityScale)。
//     分布・qualityScale・丸め表をゲームがどう組み合わせるかの式はデータに無いので、掛けた数字などは作らない
public static class OreHere
{
    // 岩 1 つの行 (岩の名前 (英語)・格・出やすさ・シグネチャー・岩の中の資源と割合)
    public sealed record RockLine(string Rock, string Grade, string Chance, string Signature, string Resources);
    // 掘り方 1 つ分
    public sealed record Section(string Method, List<RockLine> Rocks);
    // 品質の 1 行 (範囲とばらつき・その値の資源)
    public sealed record QualityLine(string Range, string Resources);
    // オーバーレイの中身: 見出し (場所の名前（星系）)・掘り方ごとの行・知らせ (行が無いときの文。あれば "")・品質の行。
    // 説明・注意の行は出さない (2026-11-06、利用者の指示「注意を勝手に入れることを固く禁止する」「画面を狭くするだけ」。
    // 前は品質の下に「品質はゲームデータの範囲で、個々の岩の値ではありません」「倍率 = …」の 2 行を出していた)
    public sealed record Content(string Title, List<Section> Sections, string Message, List<QualityLine> Quality);

    public const string NoDataMessage = "ここで採掘できる物のデータはありません";
    public const string QualityHeader = "品質";
    // 列の見出し
    public static readonly string[] Columns = ["岩", "格", "出やすさ", "シグネチャー", "岩の中の資源と割合"];

    // 決めた場所: Place = ore_locations の場所 (当たらなければ null)、Name = 場所の名前 (分からなければ "")、System = 星系
    public sealed record Where(OreSignatureCalc.Place? Place, string Name, string System);

    // 天体でない場所として見る星図の種類 (実データで ore_locations.place と英語名が一致することを確かめた種類)
    private static bool IsAsteroidType(string type) => type is "Asteroid" or "Asteroid_ValidQT";

    // 採掘できる場所の広がり 1 つ (ore_areas の 1 行。列の意味は GameDataExtractor.OreAreasDdl の説明)
    public sealed record Area(string Place, string System, string Kind, double OX, double OY, double OZ, double[] Frame, double CX, double CY, double CZ,
        double Inner, double Outer, double Depth, double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ);

    // ore_areas を読む。DB・表・列が無ければ空 (読み取り専用)。枠の回転が 9 つの数で読めない行は入れない
    public static List<Area> LoadAreas(string gamedataDbPath)
    {
        var list = new List<Area>();
        if (!File.Exists(gamedataDbPath)) return list;
        try
        {
            using var c = new SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT place, system, kind, ox, oy, oz, frame, cx, cy, cz, inner_m, outer_m, depth_m, min_x, min_y, min_z, max_x, max_y, max_z FROM ore_areas";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var frame = r.GetString(6).Split(',').Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                if (frame.Length != 9 || !frame.All(double.IsFinite)) continue;
                list.Add(new Area(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetDouble(4), r.GetDouble(5), frame, r.GetDouble(7), r.GetDouble(8), r.GetDouble(9),
                    r.GetDouble(10), r.GetDouble(11), r.GetDouble(12), r.GetDouble(13), r.GetDouble(14), r.GetDouble(15), r.GetDouble(16), r.GetDouble(17), r.GetDouble(18)));
            }
        }
        catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { list.Clear(); }
        return list;
    }

    // 座標 pos (星系の座標) が広がりの中か。pos を置かれた socpak の枠の中の位置にして (枠の中の位置 = 枠の回転の転置 · (pos − 枠の原点)。
    // 取り込みの「星系の座標 = 枠の原点 + 枠の回転 · 枠の中の位置」の逆)、
    //   輪 (ring): 輪の中心からの、枠の x・y の面での距離が内側の半径以上・外側の半径以下で、z の差が厚みの半分以内
    //   箱 (box): 枠の中の位置が minBounds 以上・maxBounds 以下
    public static bool Contains(Area a, (double X, double Y, double Z) pos)
    {
        double dx = pos.X - a.OX, dy = pos.Y - a.OY, dz = pos.Z - a.OZ;
        var f = a.Frame;
        double lx = f[0] * dx + f[3] * dy + f[6] * dz, ly = f[1] * dx + f[4] * dy + f[7] * dz, lz = f[2] * dx + f[5] * dy + f[8] * dz;
        if (a.Kind == GameDataExtractor.OreAreaRing)
        {
            double qx = lx - a.CX, qy = ly - a.CY, qz = lz - a.CZ;
            var radial = Math.Sqrt(qx * qx + qy * qy);
            return radial >= a.Inner && radial <= a.Outer && Math.Abs(qz) <= a.Depth / 2;
        }
        if (a.Kind == GameDataExtractor.OreAreaBox)
            return lx >= a.MinX && lx <= a.MaxX && ly >= a.MinY && ly <= a.MaxY && lz >= a.MinZ && lz <= a.MaxZ;
        return false;
    }

    // 座標 pos (取った時刻 utc。takenSystem は量子ジャンプの途中に取ったときの星系名。無ければ空) から場所を決める。星系が分からなければ null。
    // areas: 採掘できる場所の広がり (LoadAreas。null・空なら広がりでは決めない)
    // knownSystem: 座標とは別の手がかりで分かっている今の星系 (MapPanel.KnownSystemForOverlay。分からなければ null・空)。4 の段でだけ使う
    public static Where? Resolve(ShopTree tree, IReadOnlyList<OreSignatureCalc.Place> places, (double X, double Y, double Z) pos, double utc, string? takenSystem,
        IReadOnlyList<Area>? areas = null, string? knownSystem = null)
    {
        var effSystem = takenSystem is { Length: > 0 } es ? tree.Systems.FirstOrDefault(s => s.System.Equals(es, StringComparison.OrdinalIgnoreCase)) : null;
        var loc = CurrentLocation.Locate(tree, pos, utc, MapPanel.SystemNames, null, fresh: false, effSystem);
        if (loc == null) return null;
        var system = loc.System.System;
        // 1. 天体でない場所 (近い順。中か付近にあって名前が一致する最初のもの)
        foreach (var row in loc.All)
        {
            if (!IsAsteroidType(row.Node.Type) || row.Distance > CurrentLocation.InsideOrNearRange(row.Node)) continue;
            if (OreSignatureCalc.MatchPlace(places, row.Node.Name, system) is { } hit) return new Where(hit, hit.Name, hit.System);
        }
        // 2. 近くの天体 (［鉱石］タブと同じ)
        var nb = CurrentLocation.NearBody(tree, loc.System, pos);
        if (nb != null && OreSignatureCalc.MatchPlace(places, nb.Value.Body.Name, system) is { } body) return new Where(body, body.Name, body.System);
        // 3. 採掘できる場所の広がりの中 (小惑星帯。その星系のもので、中にいて、場所の名前が ore_locations にあるもの)
        if (areas != null)
            foreach (var a in areas)
            {
                if (!a.System.Equals(system, StringComparison.OrdinalIgnoreCase) || !Contains(a, pos)) continue;
                if (OreSignatureCalc.MatchPlace(places, a.Place, system) is { } belt) return new Where(belt, belt.Name, belt.System);
            }
        // 4. ほかの星系の広がりの中 (2026-11-05)。アプリの星系の決め方 (いちばん近い場所の星系。3 つの星系の座標は恒星が原点で重なっている) は、
        //    名前のある場所から遠い小惑星帯の中では外れることが多い (写しの DB で、Aaron Halo の輪の真ん中の半径の 360 点のうち Stanton になるのは 73 点)。
        //    現在位置がどの場所の中・付近でもなく (WhereaboutsOf で場所が無い)、近くの天体も当たらなかった (2 で当たっていない) ときに限り、ほかの星系の広がりも見る。
        //    今の星系が分かっているとき (量子ジャンプの途中に取った座標の星系 takenSystem、無ければ knownSystem) は、その星系の広がりだけ
        //    (アプリが決めた星系と同じなら、3 で見たので何も足さない)。分からなければ、どの星系の広がりでも
        var w = CurrentLocation.WhereaboutsOf(pos, loc, null);
        if (areas != null && w.Place == null)
        {
            var known = takenSystem is { Length: > 0 } ? system : knownSystem;
            foreach (var a in areas)
            {
                if (a.System.Equals(system, StringComparison.OrdinalIgnoreCase)) continue;
                if (known is { Length: > 0 } && !a.System.Equals(known, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Contains(a, pos)) continue;
                if (OreSignatureCalc.MatchPlace(places, a.Place, a.System) is { } other && other.System.Equals(a.System, StringComparison.OrdinalIgnoreCase))
                    return new Where(other, other.Name, other.System);
            }
        }
        // 5. 当たらない: 分かる範囲の名前 (現在地の場所 (中・付近)、無ければ近くの天体)
        return new Where(null, w.Place?.Name ?? nb?.Body.Name ?? "", system);
    }

    // 座標から場所を決めて中身を作る (裏のスレッドで呼ぶ)。星系が分からなければ見出しは「現在位置」。knownSystem は Resolve の説明
    public static Content Compose(ShopTree tree, string gamedataDbPath, string translationsDbPath, (double X, double Y, double Z) pos, double utc, string? takenSystem,
        string? knownSystem = null)
    {
        var data = OreSignatureCalc.Load(gamedataDbPath);
        var where = Resolve(tree, data.Places, pos, utc, takenSystem, LoadAreas(gamedataDbPath), knownSystem) ?? new Where(null, "", "");
        return Build(gamedataDbPath, translationsDbPath, data, where);
    }

    // 見本の中身 (設定タブの［オーバーレイを表示して調整］で、現在位置が無い・その場所にデータが無いとき。裏のスレッドで呼ぶ):
    // 採掘の表 (ore_locations) の場所の一覧 (OreSignatureCalc.Load の Places の並び = 星系の順 → 名前の順) の中で、船・ROC・手の 3 つの掘り方の行が揃っている最初の場所。
    // 揃っている場所が無ければ一覧の最初の場所。場所が 1 つも無ければ null
    public static Content? Sample(string gamedataDbPath, string translationsDbPath)
    {
        var data = OreSignatureCalc.Load(gamedataDbPath);
        if (data.Places.Count == 0) return null;
        string[] methods = [GameDataExtractor.OreMethodShip, GameDataExtractor.OreMethodRoc, GameDataExtractor.OreMethodHand];
        var place = data.Places.FirstOrDefault(p => methods.All(m => data.Locations.Any(l => l.Place == p.Name && l.System == p.System && l.Method == m))) ?? data.Places[0];
        return Build(gamedataDbPath, translationsDbPath, data, new Where(place, place.Name, place.System));
    }

    // 掘り方の順 (GameDataQueryService.ResourceDetailText の MethodOrder と同じ: 船 → ROC → 手 → ほか)
    private static int MethodOrder(string m) => m switch
    {
        GameDataExtractor.OreMethodShip => 0,
        GameDataExtractor.OreMethodRoc => 1,
        GameDataExtractor.OreMethodHand => 2,
        _ => 3,
    };
    // 割合の数字 (ResourceDetailText の Pct と同じ)・シグネチャーの数字 (OrePanel.FormatNumber と同じ)
    private static string Pct(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string Number(double v) => v.ToString("#,0.##", CultureInfo.InvariantCulture);

    // ore_locations の 1 行 (資源つき。同じ資源の行をまとめたもの)
    private sealed record MergedRow(string Method, string Rock, string Uuid, string Record, double Min, double Max, double Prob);
    // ore_rock_parts の 1 行 (岩の中身の行そのまま)
    private sealed record PartRow(string Method, string Rock, string Composition, int Index, string Uuid, string Record, double Min, double Max, double Prob, double? Scale);
    // ore_quality の 1 行
    private sealed record QualityRow(string Uuid, string System, long Min, long Max, double Stddev);

    private static bool IsMissingTableOrColumn(SqliteException ex) =>
        ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase);

    // 場所の中身を作る。場所が当たっていない・行が無い (表・列の無い DB を含む) ときは「ここで採掘できる物のデータはありません」
    public static Content Build(string gamedataDbPath, string translationsDbPath, OreSignatureCalc.Data data, Where where)
    {
        var title = where.Name.Length > 0 ? (where.System.Length > 0 ? $"{where.Name}（{where.System}）" : where.Name)
            : where.System.Length > 0 ? where.System : "現在位置";
        Content None() => new(title, [], NoDataMessage, []);
        if (where.Place is not { } place || !File.Exists(gamedataDbPath)) return None();

        var merged = new List<MergedRow>();
        var parts = new List<PartRow>();
        var quality = new List<QualityRow>();
        using (var c = new SqliteConnection($"Data Source={gamedataDbPath};Mode=ReadOnly;Pooling=False"))
        {
            c.Open();
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT method, rock, resource_uuid, resource_record, min_pct, max_pct, part_probability FROM ore_locations WHERE place = @p AND system = @s";
                cmd.Parameters.AddWithValue("@p", place.Name);
                cmd.Parameters.AddWithValue("@s", place.System);
                using var r = cmd.ExecuteReader();
                while (r.Read()) merged.Add(new MergedRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetDouble(5), r.GetDouble(6)));
            }
            catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { merged.Clear(); }
            // 岩の中身の行 (その場所の preset・掘り方・岩のもの)。表・列が無ければ無いものとして、まとめた行 (ore_locations) で出す
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT p.method, p.rock, p.composition, p.part_index, p.resource_uuid, p.resource_record, p.min_pct, p.max_pct, p.probability, p.quality_scale "
                    + "FROM ore_rock_parts p WHERE EXISTS (SELECT 1 FROM ore_locations l WHERE l.place = @p AND l.system = @s AND l.preset = p.preset AND l.method = p.method AND l.rock = p.rock) "
                    + "ORDER BY p.method, p.rock, p.composition, p.part_index";
                cmd.Parameters.AddWithValue("@p", place.Name);
                cmd.Parameters.AddWithValue("@s", place.System);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    parts.Add(new PartRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetDouble(6), r.GetDouble(7), r.GetDouble(8),
                        r.IsDBNull(9) ? null : r.GetDouble(9)));
            }
            catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { parts.Clear(); }
            // 資源の品質の分布 (既定と星系の上書き)。表・列が無ければ品質を省く
            try
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT resource_uuid, system, q_min, q_max, stddev FROM ore_quality";
                using var r = cmd.ExecuteReader();
                while (r.Read()) quality.Add(new QualityRow(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetDouble(4)));
            }
            catch (SqliteException ex) when (IsMissingTableOrColumn(ex)) { quality.Clear(); }
        }
        if (merged.Count == 0) return None();

        // 名前: 資源は［鉱石］タブと同じ出し方 (OreResource.Display)、格はゲームの訳 (OreSignatureCalc.LoadGradeLabels。無ければ英語のまま)
        var byUuid = new Dictionary<string, OreResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in OreService.LoadResources(gamedataDbPath, translationsDbPath)) byUuid.TryAdd(x.Uuid, x);
        string ResourceName(string uuid, string record) => byUuid.TryGetValue(uuid, out var res) ? res.Display : OreService.ShortRecord(record);
        var gradeLabels = OreSignatureCalc.LoadGradeLabels(translationsDbPath);
        string Grade(string g) => g.Length == 0 ? "—" : gradeLabels.TryGetValue(g, out var l) ? l : g;

        // 資源の出てきた順 (品質の行の並びに使う) と、倍率を書いた岩があったか
        var resourceOrder = new List<(string Uuid, string Record)>();
        void Seen(string uuid, string record) { if (!resourceOrder.Any(x => x.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase))) resourceOrder.Add((uuid, record)); }

        // 岩ごとの行。並び: 掘り方の順、出やすさの大きい順 (preset が 2 つ以上なら大きいほう)、岩の名前
        var rocks = merged.GroupBy(x => (x.Method, x.Rock)).Select(g =>
        {
            var chances = data.Locations.Where(l => l.Place == place.Name && l.System == place.System && l.Method == g.Key.Method && l.Rock == g.Key.Rock)
                .Select(l => l.Chance).ToList();
            return (g.Key.Method, g.Key.Rock, MaxChance: chances.Count > 0 ? chances.Max() : 0, Rows: g.ToList());
        }).OrderBy(x => MethodOrder(x.Method)).ThenByDescending(x => x.MaxChance).ThenBy(x => x.Rock, StringComparer.OrdinalIgnoreCase).ToList();

        var sections = new List<Section>();
        foreach (var x in rocks)
        {
            var sigs = data.Rocks.Where(k => k.Method == x.Method && k.Name == x.Rock).ToList();
            var signature = sigs.Count == 0 ? "—" : string.Join(" / ", sigs.Select(k => k.Signature).Distinct().OrderBy(v => v).Select(Number));
            var grade = Grade(sigs.Select(k => k.Grade).FirstOrDefault(v => v.Length > 0) ?? "");
            var chance = OreSignatureCalc.ChanceText(data, place, x.Method, x.Rock);

            // 岩の中の資源と割合。岩の中身の行 (ore_rock_parts) があれば行のとおりに (同じ行は 1 つ)、無ければまとめた行 (ore_locations)
            string resources;
            var rockParts = parts.Where(p => p.Method == x.Method && p.Rock == x.Rock).ToList();
            if (rockParts.Count > 0)
            {
                // その岩の行が全部 1 (か項目が無い) なら倍率を書かない
                var showScale = rockParts.Any(p => p.Scale is { } s && s != 1);
                // 資源ごとに (出てきた順)、行の割合を並べる。同じ岩の中身の中に同じ値の行が 2 つ以上あれば「×2」と添える (行を重ねて並べない)。
                // 場所に preset が 2 つ以上あって岩の中身のレコードが違うときは、同じ値の行は 1 つにする (数は多いほう)
                var groups = new List<(string Uuid, string Record, List<(string Range, int Count)> Ranges)>();
                foreach (var comp in rockParts.GroupBy(p => p.Composition))
                {
                    foreach (var same in comp.GroupBy(p => (p.Uuid, Range: $"{Pct(p.Min)}〜{Pct(p.Max)}%" + (p.Prob < 1 ? $"（入る確率 {Pct(p.Prob * 100)}%）" : "")
                                 + (showScale && p.Scale is { } s ? $"（倍率 {s.ToString("0.###", CultureInfo.InvariantCulture)}）" : ""))))
                    {
                        var first = same.First();
                        Seen(first.Uuid, first.Record);
                        var gi = groups.FindIndex(k => k.Uuid.Equals(first.Uuid, StringComparison.OrdinalIgnoreCase));
                        if (gi < 0) { groups.Add((first.Uuid, first.Record, [])); gi = groups.Count - 1; }
                        var ri = groups[gi].Ranges.FindIndex(k => k.Range == same.Key.Range);
                        if (ri < 0) groups[gi].Ranges.Add((same.Key.Range, same.Count()));
                        else if (same.Count() > groups[gi].Ranges[ri].Count) groups[gi].Ranges[ri] = (same.Key.Range, same.Count());
                    }
                }
                resources = string.Join("、", groups.Select(k => $"{ResourceName(k.Uuid, k.Record)} {string.Join("・", k.Ranges.Select(r => r.Count > 1 ? $"{r.Range}×{r.Count}" : r.Range))}"));
            }
            else
            {
                var list = x.Rows.OrderByDescending(r => r.Max).ThenBy(r => ResourceName(r.Uuid, r.Record), StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var r in list) Seen(r.Uuid, r.Record);
                resources = string.Join("、", list.Select(r => $"{ResourceName(r.Uuid, r.Record)} {Pct(r.Min)}〜{Pct(r.Max)}%" + (r.Prob < 1 ? $"（入る確率 {Pct(r.Prob * 100)}%）" : ""))
                    .Distinct());
            }
            var line = new RockLine(x.Rock, grade, chance, signature, resources);
            if (sections.Count == 0 || sections[^1].Method != x.Method) sections.Add(new Section(x.Method, []));
            sections[^1].Rocks.Add(line);
        }

        // 品質: 資源ごとに、その星系の上書きがあればその値、無ければ既定の値。同じ値の資源は 1 行にまとめる (資源の出てきた順)
        var qualityLines = new List<QualityLine>();
        var byRange = new List<(string Range, List<string> Names)>();
        foreach (var (uuid, record) in resourceOrder)
        {
            var rows = quality.Where(q => q.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase)).ToList();
            var q = rows.FirstOrDefault(k => k.System.Length > 0 && k.System.Equals(place.System, StringComparison.OrdinalIgnoreCase)) ?? rows.FirstOrDefault(k => k.System.Length == 0);
            if (q == null) continue;
            var range = $"{q.Min.ToString(CultureInfo.InvariantCulture)}〜{q.Max.ToString(CultureInfo.InvariantCulture)}（ばらつき {q.Stddev.ToString("0.##", CultureInfo.InvariantCulture)}）";
            var name = ResourceName(uuid, record);
            var bi = byRange.FindIndex(k => k.Range == range);
            if (bi < 0) byRange.Add((range, [name]));
            else if (!byRange[bi].Names.Contains(name)) byRange[bi].Names.Add(name);
        }
        foreach (var (range, names) in byRange) qualityLines.Add(new QualityLine(range, string.Join("、", names)));
        return new Content(title, sections, "", qualityLines);
    }

    // 中身を文字の行にする (確かめ・ログ用。オーバーレイに出る文字を全部)
    public static List<string> ToLines(Content c)
    {
        var lines = new List<string> { c.Title };
        if (c.Message.Length > 0) lines.Add(c.Message);
        if (c.Sections.Count > 0) lines.Add("  " + string.Join(" | ", Columns));
        foreach (var s in c.Sections)
        {
            lines.Add($"■ {s.Method}");
            foreach (var r in s.Rocks) lines.Add($"  {r.Rock} | {r.Grade} | {r.Chance} | {r.Signature} | {r.Resources}");
        }
        if (c.Quality.Count > 0)
        {
            lines.Add($"■ {QualityHeader}");
            foreach (var q in c.Quality) lines.Add($"  {q.Range} | {q.Resources}");
        }
        return lines;
    }
}
