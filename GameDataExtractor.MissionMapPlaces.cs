using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 契約の行き先の地図 (契約の詳細の地図のリンク。mission_map_places)。インデックス構築の Step 6c (ExtractDeliverableSourcesAsync) の中で、
// 星図の場所の表 (starmap_locations) を作った後に作る。調べ方は調査 (Python のスクリプト scan4.py・scan5.py・hier.py・match3.py) を移したもの。
// 照合の決まりは調査で決めた推測:
//   契約: missions の raw_json の中の MissionProperty で、value が MissionPropertyValue_Location / MissionPropertyValue_Locations のもの
//     (変数は missionVariableName)。ContractGenerator の行は、上書きしなかった変数を ContractTemplate (_Resolved_.template) の中のものから足す。
//     条件は value.matchConditions の DataSetMatchCondition_TagSearch だけを見る (ExcludeProperty などほかの種類の条件は見ない)
//   場所: DataCore の MissionLocationTemplate (locationData の generalTags.tags / producesTags.positiveTags / consumesTags.positiveTags) を、
//     socpak の entdata の *.entxml の Entity が template="<uuid>" (missionLocationTemplate="<uuid>") で持つ。Entity の EntityCryGUID は、
//     同じ socpak の xml の Child の guid と同じ。Entity の additionalData のタグ (generalTags・producesTags・consumesTags) も足す
//   照合: 1 つの TagSearchTerm は positiveTags を全部含み negativeTags を 1 つも含まない。同じ条件の term はどれか 1 つを満たせばよく、
//     条件どうしは全部を満たす。tagType が Produces / Consumes なら生産・消費のタグ (その場所のものだけ)、ほかは一般のタグ (親の場所のものを継ぐ)。
//     タグの木の祖先も含めて比べる。親の場所は、Child の入れ子の親の Child、入れ子でなければその socpak を置く Child (どこに置かれたかは区別せず、
//     全部の置き場の親を継ぐ。調査の hier.py と同じ)
//   地図: 階層マップのルート (StationIndex.FindRoots。地図の窓と同じもの) のうち、その Child を中に持ついちばん内側のルート
//     (同じ socpak が何か所にも置かれていれば、置き場ごとのルート全部)。中に無ければ、Entity の starmapRecord・Child の starMapRecord が
//     ルートの星図の場所ならそのルート (同じ場所の規則 2 の置き場も)
public partial class GameDataExtractor
{
    // 契約の行き先の地図。3 つの表に分けて持つ (同じ条件の契約が多く、契約ごとに地図を並べると約 313 万行・DB が約 1GB 増えたため (2026-11-01 の写しでの実測)。
    // 同じ地図の組は 1 つにまとめる):
    //   mission_map_vars: 1 行 = 1 つの契約 (missions.record_name) の 1 つの変数 (missionVariableName)。place_count はその変数の条件に当たった場所の実体の数
    //     (地図の無いものも含む)、set_id はその変数の地図の組 (mission_map_sets)。場所が 1 つも当たらない変数・地図が 1 つも無い変数は入れない
    //   mission_map_sets: 1 行 = 地図の組 1 つの地図 1 つ
    //   mission_map_places: 1 行 = 地図 1 つ:
    //     map_record: 地図の鍵 (階層マップの窓に渡す record。星図の場所の record 名 (StarMapObject.…)、星図に載っていない場所は Unlisted.<Child の guid>・
    //       Unlisted.<元の record の StarMapObject. の後ろ>.<番号>・Unlisted.<socpak のファイル名>.<番号> (StationIndex.FindRoots))
    //     map_type: 場所の種類 (星図の種類の名前か、StationPlace.TypeCave などの星図に無い種類)。system: 星系 (starmap_locations.system)
    //     name: リンクの文字 (地図の窓の一覧の表示と同じ決まり。星図の場所は英語名 (同じ星系に同じ名前があれば親の名前を（）で添える)、
    //       星図に載っていない場所は種類の言葉か共有元の場所の名前に、乗っている天体の名前を（）で添えたもの)
    //     named: 名前がゲームの名前か (星図の名前が record 名のままの「名前の無い場所」は、名前を星図外の場所と同じ決まり (StationPlaceList.UnlistedNames。
    //       種類の言葉と、自転する天体の名前) で作り、1 にする (2026-11-02、親の決定「B」・名前は案 A)。0 の行は今は作らない)
    //     has_floor: 前は床のメッシュがあるかを調べていた。(2026-11-04、利用者の決定「やめる」) 調べをやめ、いつも 1 (列は前の版の DB と同じ形のために残す)
    //     content_key: 同じ地図の鍵 (StationContentKey.Of。地図の窓の一覧の ▼ のまとまりと同じ鍵)。画面は同じ鍵の場所を 1 つのリンクにまとめる
    // インデックス構築 (Step 6c) で作り直す (作れなかったときは前の表を残す)。書くときは 3 つとも消して今の形で作り直す
    internal static readonly string[] MissionMapTables = ["mission_map_vars", "mission_map_sets", "mission_map_places"];
    internal const string MissionMapDdl = """

        CREATE TABLE IF NOT EXISTS mission_map_vars (
            mission_record TEXT NOT NULL,
            variable TEXT NOT NULL,
            place_count INTEGER NOT NULL,
            set_id INTEGER NOT NULL,
            PRIMARY KEY (mission_record, variable)
        );
        CREATE TABLE IF NOT EXISTS mission_map_sets (
            set_id INTEGER NOT NULL,
            map_record TEXT NOT NULL,
            PRIMARY KEY (set_id, map_record)
        );
        CREATE TABLE IF NOT EXISTS mission_map_places (
            map_record TEXT PRIMARY KEY,
            map_type TEXT NOT NULL,
            system TEXT NOT NULL,
            name TEXT NOT NULL,
            named INTEGER NOT NULL,
            has_floor INTEGER NOT NULL,
            content_key TEXT NOT NULL
        );
        """;

    // 表の形と作り方の番号。表・列・決まりを変えたら上げる (作った印の値。印と違えば起動後に作り直す: NeedsMissionMapPlaces)
    // 3: mission_map_places に content_key (同じ地図の鍵) を足した。4: 星図の場所の床も調べる (has_floor)・名前の無い場所に種類の言葉の名前を付ける
    private const string MissionMapPlacesFormat = "4";

    // 契約の地図の表を作れた印 (gamedata_meta。値は MissionMapPlacesFormat)。作れたときだけ書く。インデックス構築の最初に消す (RebuiltDataMetaKeys) ので、
    // 構築の中で作れなければ印が無いまま残り、次の起動で作り直す (星図の場所・店の表と同じく Step 6c を走らせる: RebuildOreLocationsAsync)
    internal const string MissionMapPlacesMetaKey = "mission_map_places_format";

    // 契約の地図の表を (起動後に) 作り直すか: インデックスを構築した DB で、表が無いか、作れた印が今の形の番号でない
    public bool NeedsMissionMapPlaces()
    {
        EnsureDb();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM item_index)";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return false;
        cmd.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ({string.Join(", ", MissionMapTables.Select(t => $"'{t}'"))})";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) < MissionMapTables.Length) return true;
        return ReadMetaValue(MissionMapPlacesMetaKey) != MissionMapPlacesFormat;
    }

    // 場所のテンプレート (MissionLocationTemplate) 1 つ: record 名と、一般・生産・消費のタグ (uuid の小文字)
    private sealed record MissionLocTemplate(string Record, List<string> General, List<string> Produces, List<string> Consumes);

    // 場所の実体 1 つ (socpak の entdata の entxml の Entity。Socpak は ObjectContainers の下の相対パスの小文字、Guid は EntityCryGUID の小文字)
    private sealed class MissionLocInstance
    {
        public required string Socpak { get; init; }
        public required string Guid { get; init; }
        public required string Name { get; init; }
        public List<string> Templates { get; } = new();
        public HashSet<string> Extra { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExtraProduces { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExtraConsumes { get; } = new(StringComparer.Ordinal);
        public List<string> StarmapRecords { get; } = new();
        // タグの木の祖先まで広げたもの (一般は親の場所のものを継いだもの)
        public HashSet<string> General { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Produces { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> Consumes { get; set; } = new(StringComparer.Ordinal);
    }

    // 場所の条件 1 つ (DataSetMatchCondition_TagSearch): tagType と、TagSearchTerm の (positiveTags, negativeTags) の並び
    private sealed record MissionLocCond(string TagType, List<(string[] Pos, string[] Neg)> Terms);

    // 書き込むもの: 変数の行 (地図の組の番号)、地図の組 (番号 → 地図の record)、地図
    private readonly record struct MissionMapVar(string Mission, string Variable, int PlaceCount, int SetId);
    private readonly record struct MissionMapPlace(string MapRecord, string MapType, string System, string Name, bool Named, bool HasFloor, string ContentKey);
    private sealed record MissionMapData(List<MissionMapVar> Vars, List<List<string>> Sets, List<MissionMapPlace> Places);

    private static readonly Regex MissionMapUuidRegex = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MissionMapRecordsPathRegex = new("libs/foundry/records/(.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 契約の地図を作って mission_map_places を入れ替える。作れた行が 0 行なら前の行を残す (表が無ければ空の表だけ作る) で、作れた印は書かない。
    // 途中の失敗は例外 (呼び出し側で知らせて構築は続ける。印が無いので次の起動でまた作る)。各段の件数と時間を StatusChanged に出す。
    // skipWriteIfIndexBuildStarts: 起動後の裏の作り直しで、作っている間に構築・再抽出が始まっていたら、表も印も書かずに抜ける
    // (構築が最初に印を消して作り直すので、古いデータに作れた印を付けないため)
    private void WriteMissionMapPlaces(DeliverableDcb dcb, DeliverableSocpaks soc, string p4kPath, bool skipWriteIfIndexBuildStarts, CancellationToken ct)
    {
        const string P = "[契約の地図]";
        var total = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();

        // 1. 場所のテンプレート (DCB の MissionLocationTemplate.* の全部)
        var templates = ReadMissionLocTemplates(dcb, ct);
        if (templates.Count == 0) throw new InvalidOperationException("MissionLocationTemplate のレコードが 1 件もありません");
        var tTemplates = sw.Elapsed.TotalSeconds;
        StatusChanged?.Invoke($"{P} 場所のテンプレート {templates.Count} 件 ({tTemplates:F1}秒)");

        // 2. 場所の実体 (テンプレートの uuid が出てくる socpak の entxml)
        sw.Restart();
        using var idx = StationIndex.Build(p4kPath, ct);
        var instSocpaks = templates.Values
            .SelectMany(t => soc.RecordToSocpaks.TryGetValue(t.Record, out var s) ? s : Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var (insts, badSocpaks, badFiles) = ReadMissionLocInstances(idx, instSocpaks, templates, ct);
        var tInst = sw.Elapsed.TotalSeconds;
        StatusChanged?.Invoke($"{P} 場所の実体 {insts.Count} 個 (socpak {instSocpaks.Count} 件のうち {insts.Select(i => i.Socpak).Distinct().Count()} 件、"
            + $"テンプレート {insts.SelectMany(i => i.Templates).Distinct().Count()} 件"
            + (badSocpaks > 0 ? $"、開けない socpak {badSocpaks} 件" : "") + (badFiles > 0 ? $"、読めない entxml {badFiles} 件" : "") + $"。{tInst:F1}秒)");
        if (insts.Count == 0) throw new InvalidOperationException("場所の実体 (テンプレートを持つ entxml の Entity) が 1 つもありません");

        // 3. Child の木 (場所の実体のある socpak と、それを置く親の socpak を上へ辿ったもの) と、親の場所のタグを継いだタグ
        sw.Restart();
        var (nodes, bySoc, graphSocpaks) = ReadMissionLocChildGraph(idx, soc, instSocpaks, ct);
        ExpandMissionLocTags(insts, templates, nodes, bySoc, dcb.TagParent);
        var tTags = sw.Elapsed.TotalSeconds;
        StatusChanged?.Invoke($"{P} Child の木 socpak {graphSocpaks} 件・Child {nodes.Count} 個、タグを継ぐ ({tTags:F1}秒)");

        // 4. 契約の照合
        sw.Restart();
        var missions = ReadMissionLocProperties(dcb, ct);
        var candCache = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        // 契約 → 変数 → 当たった場所の実体 (insts の番号)
        var matched = new List<(string Mission, List<(string Variable, HashSet<int> Cands)> Vars)>();
        int withProp = 0, withPlace = 0;
        var withPlaceByKind = new Dictionary<string, (int Total, int Prop, int Place)>(StringComparer.Ordinal);
        foreach (var (mission, props) in missions)
        {
            ct.ThrowIfCancellationRequested();
            var kind = mission.Split('.')[0];
            var k = withPlaceByKind.GetValueOrDefault(kind);
            k.Total++;
            var vars = new List<(string Variable, HashSet<int> Cands)>();
            foreach (var (variable, conds) in props)
            {
                var key = MissionLocCondKey(conds);
                if (!candCache.TryGetValue(key, out var c)) candCache[key] = c = MatchMissionLocations(conds, insts);
                var slot = vars.FindIndex(v => v.Variable == variable);
                if (slot < 0) vars.Add((variable, new HashSet<int>(c)));
                else vars[slot].Cands.UnionWith(c);
            }
            if (props.Count > 0) { withProp++; k.Prop++; }
            if (vars.Any(v => v.Cands.Count > 0)) { withPlace++; k.Place++; }
            withPlaceByKind[kind] = k;
            matched.Add((mission, vars));
        }
        var tMatch = sw.Elapsed.TotalSeconds;
        StatusChanged?.Invoke($"{P} 契約 {missions.Count} 件のうち、場所の条件のあるもの {withProp} 件、場所が 1 か所以上見つかったもの {withPlace} 件 ("
            + string.Join("・", withPlaceByKind.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value.Place}/{x.Value.Total}"))
            + $"。条件の種類 {candCache.Count}。{tMatch:F1}秒)");

        // 5. 地図のルート (地図の窓と同じ FindRoots) と、場所の実体 → ルート
        sw.Restart();
        var starmap = ReadMissionMapStarmapRows();
        var search = idx.FindRoots(starmap.Select(r => r.Place).ToList(), ct: ct);
        var (instRoots, byContain, byStarmap, none) = MapMissionLocInstancesToRoots(idx, search, insts, nodes, ct);
        var tRoots = sw.Elapsed.TotalSeconds;
        StatusChanged?.Invoke($"{P} 地図のルート {search.Roots.Count} か所。場所の実体のうち、ルートの中にあるもの {byContain} 個・星図の場所でルートに当たるもの {byStarmap} 個・"
            + $"地図の無いもの {none} 個 ({tRoots:F1}秒)");

        // 6. 行 (リンクの文字と、一覧に出す場所かを決める値)
        sw.Restart();
        var data = BuildMissionMapRows(matched, instRoots, search, starmap, idx, ct);
        var tRows = sw.Elapsed.TotalSeconds;

        // 7. 書き込み (入れ替え。3 つの表を消して今の形で作り直す)
        ct.ThrowIfCancellationRequested();
        if (skipWriteIfIndexBuildStarts && IsIndexBuildRunning)
        {
            StatusChanged?.Invoke($"{P} インデックス構築・装備データの再抽出が始まったので、表も作れた印も書き込みませんでした");
            return;
        }
        if (data.Vars.Count == 0)
        {
            using (var ddl = _db!.CreateCommand())
            {
                ddl.CommandText = MissionMapDdl;
                ddl.ExecuteNonQuery();
            }
            StatusChanged?.Invoke($"{P} 1 行も作れませんでした (前のデータを残します。作れた印は書かないので、次の起動でまた作ります)");
            return;
        }
        sw.Restart();
        using (var tx = _db!.BeginTransaction())
        {
            using (var ddl = _db.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = string.Concat(MissionMapTables.Select(t => $"DROP TABLE IF EXISTS {t};")) + MissionMapDdl;
                ddl.ExecuteNonQuery();
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO mission_map_vars(mission_record,variable,place_count,set_id) VALUES(@m,@v,@c,@s)";
                var pM = ins.Parameters.Add("@m", SqliteType.Text);
                var pV = ins.Parameters.Add("@v", SqliteType.Text);
                var pC = ins.Parameters.Add("@c", SqliteType.Integer);
                var pS = ins.Parameters.Add("@s", SqliteType.Integer);
                foreach (var v in data.Vars)
                {
                    pM.Value = v.Mission; pV.Value = v.Variable; pC.Value = v.PlaceCount; pS.Value = v.SetId;
                    ins.ExecuteNonQuery();
                }
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO mission_map_sets(set_id,map_record) VALUES(@s,@r)";
                var pS = ins.Parameters.Add("@s", SqliteType.Integer);
                var pR = ins.Parameters.Add("@r", SqliteType.Text);
                for (int i = 0; i < data.Sets.Count; i++)
                    foreach (var rec in data.Sets[i])
                    {
                        pS.Value = i; pR.Value = rec;
                        ins.ExecuteNonQuery();
                    }
            }
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO mission_map_places(map_record,map_type,system,name,named,has_floor,content_key) VALUES(@r,@t,@s,@n,@nd,@f,@k)";
                var pR = ins.Parameters.Add("@r", SqliteType.Text);
                var pT = ins.Parameters.Add("@t", SqliteType.Text);
                var pS = ins.Parameters.Add("@s", SqliteType.Text);
                var pN = ins.Parameters.Add("@n", SqliteType.Text);
                var pNd = ins.Parameters.Add("@nd", SqliteType.Integer);
                var pF = ins.Parameters.Add("@f", SqliteType.Integer);
                var pK = ins.Parameters.Add("@k", SqliteType.Text);
                foreach (var pl in data.Places)
                {
                    pR.Value = pl.MapRecord; pT.Value = pl.MapType; pS.Value = pl.System; pN.Value = pl.Name; pNd.Value = pl.Named ? 1 : 0; pF.Value = pl.HasFloor ? 1 : 0;
                    pK.Value = pl.ContentKey;
                    ins.ExecuteNonQuery();
                }
            }
            SetMeta(_db, MissionMapPlacesMetaKey, MissionMapPlacesFormat);
            tx.Commit();
        }
        var tWrite = sw.Elapsed.TotalSeconds;

        // 画面に出る数 (StationPlaceList.IsListedPlace と名前のある場所だけ。契約ごとの地図の数。同じ地図の鍵の場所は 1 つと数える (画面と同じ))
        var placeBy = data.Places.ToDictionary(x => x.MapRecord, StringComparer.Ordinal);
        int show1to10 = 0, showMany = 0, hiddenOnly = 0;
        foreach (var g in data.Vars.GroupBy(v => v.Mission))
        {
            var shown = g.SelectMany(v => data.Sets[v.SetId]).Distinct(StringComparer.Ordinal)
                .Where(rec => placeBy[rec] is var pl && pl.Named && StationPlaceList.IsListedPlace(pl.MapType))
                .Select(rec => placeBy[rec].ContentKey).Distinct(StringComparer.Ordinal).Count();
            if (shown == 0) hiddenOnly++;
            else if (shown <= 10) show1to10++;
            else showMany++;
        }
        StatusChanged?.Invoke($"{P} 変数 {data.Vars.Count} 行・地図の組 {data.Sets.Count} 組 ({data.Sets.Sum(x => x.Count)} 行)・地図 {data.Places.Count} か所 "
            + $"(地図のある契約 {data.Vars.Select(v => v.Mission).Distinct().Count()} 件)。"
            + $"画面にリンクを出す契約 {show1to10} 件・候補の数だけ出す契約 (11 か所以上) {showMany} 件・地図の一覧に出ない場所だけでリンクを出さない契約 {hiddenOnly} 件。"
            + $"時間: テンプレート {tTemplates:F1}秒・場所の実体 {tInst:F1}秒・タグ {tTags:F1}秒・照合 {tMatch:F1}秒・ルート {tRoots:F1}秒・行 {tRows:F1}秒・書き込み {tWrite:F1}秒・"
            + $"合計 {total.Elapsed.TotalSeconds:F1}秒");
    }

    // DCB の MissionLocationTemplate.* のレコード → uuid (小文字) ごとのテンプレート
    private static Dictionary<string, MissionLocTemplate> ReadMissionLocTemplates(DeliverableDcb dcb, CancellationToken ct)
    {
        var res = new Dictionary<string, MissionLocTemplate>(StringComparer.Ordinal);
        static List<string> Ids(JsonElement parent, string listName)
        {
            var list = new List<string>();
            if (parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(listName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var t in arr.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.Object && t.TryGetProperty("_RecordId_", out var id) && id.ValueKind == JsonValueKind.String)
                        list.Add(id.GetString()!.ToLowerInvariant());
            return list;
        }
        static JsonElement Prop(JsonElement o, string name) =>
            o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : default;
        foreach (var rel in dcb.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!dcb.Rec.TryGetValue(rel, out var rn) || rn == null || !rn.StartsWith("MissionLocationTemplate.", StringComparison.Ordinal)) continue;
            var text = dcb.ReadText(rel);
            if (text == null) continue;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("_RecordId_", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
            var ld = Prop(Prop(root, "_RecordValue_"), "locationData");
            res[idEl.GetString()!.ToLowerInvariant()] = new MissionLocTemplate(rn,
                Ids(Prop(ld, "generalTags"), "tags"), Ids(Prop(ld, "producesTags"), "positiveTags"), Ids(Prop(ld, "consumesTags"), "positiveTags"));
        }
        return res;
    }

    // socpak の entdata の entxml のうち、テンプレートの uuid が出てくるものを読んで場所の実体にする (調査の scan4.py・scan5.py)。
    // 並びは socpak・entxml の名前の順。開けない socpak と読めない entxml の数も返す
    private static (List<MissionLocInstance> Insts, int BadSocpaks, int BadFiles) ReadMissionLocInstances(StationIndex idx, List<string> socpaks,
        Dictionary<string, MissionLocTemplate> templates, CancellationToken ct)
    {
        var bag = new ConcurrentBag<(string Socpak, string File, MissionLocInstance Inst)>();
        int badSocpaks = 0, badFiles = 0;
        Parallel.ForEach(socpaks, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, rel =>
        {
            System.IO.Compression.ZipArchive? zip;
            try { zip = idx.OpenSocpak(rel); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { zip = null; }
            if (zip == null) { Interlocked.Increment(ref badSocpaks); return; }
            using (zip)
            {
                foreach (var e in zip.Entries)
                {
                    var file = e.FullName.Replace('\\', '/');
                    if (!file.EndsWith(".entxml", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var bytes = StationIndex.ReadAll(e);
                        var text = Encoding.Latin1.GetString(bytes);
                        var hit = false;
                        foreach (Match m in MissionMapUuidRegex.Matches(text))
                            if (templates.ContainsKey(m.Value.ToLowerInvariant())) { hit = true; break; }
                        if (!hit) continue;
                        var root = bytes.Length >= 7 && Encoding.ASCII.GetString(bytes, 0, 7) == "CryXmlB"
                            ? CryXmlParser.ParseBinaryBytes(bytes).Root : XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root;
                        if (root == null) { Interlocked.Increment(ref badFiles); continue; }
                        var inst = new MissionLocInstance
                        {
                            Socpak = rel, Guid = ((string?)root.Attribute("EntityCryGUID") ?? "").ToLowerInvariant(), Name = (string?)root.Attribute("Name") ?? "",
                        };
                        foreach (var el in root.DescendantsAndSelf())
                        {
                            foreach (var a in el.Attributes())
                            {
                                var an = a.Name.LocalName;
                                if ((an == "template" || an == "missionLocationTemplate") && templates.ContainsKey(a.Value.ToLowerInvariant())
                                    && !inst.Templates.Contains(a.Value.ToLowerInvariant()))
                                    inst.Templates.Add(a.Value.ToLowerInvariant());
                                if (an == "starmapRecord" && a.Value.Length > 0) inst.StarmapRecords.Add(a.Value.ToLowerInvariant());
                            }
                        }
                        inst.Templates.Sort(StringComparer.Ordinal);
                        // その場所だけのタグ (additionalData の generalTags の record@value、producesTags / consumesTags の positiveTags の record@value)
                        foreach (var ad in root.DescendantsAndSelf("additionalData"))
                        {
                            foreach (var r in ad.DescendantsAndSelf("generalTags").SelectMany(g => g.DescendantsAndSelf("record")))
                                if ((string?)r.Attribute("value") is { Length: > 0 } v) inst.Extra.Add(v.ToLowerInvariant());
                            foreach (var r in ad.DescendantsAndSelf("producesTags").SelectMany(x => x.DescendantsAndSelf("positiveTags")).SelectMany(x => x.DescendantsAndSelf("record")))
                                if ((string?)r.Attribute("value") is { Length: > 0 } v) inst.ExtraProduces.Add(v.ToLowerInvariant());
                            foreach (var r in ad.DescendantsAndSelf("consumesTags").SelectMany(x => x.DescendantsAndSelf("positiveTags")).SelectMany(x => x.DescendantsAndSelf("record")))
                                if ((string?)r.Attribute("value") is { Length: > 0 } v) inst.ExtraConsumes.Add(v.ToLowerInvariant());
                        }
                        bag.Add((rel, file, inst));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException or ArgumentException or IndexOutOfRangeException)
                    {
                        Interlocked.Increment(ref badFiles);
                    }
                }
            }
        });
        var list = bag.OrderBy(x => x.Socpak, StringComparer.Ordinal).ThenBy(x => x.File, StringComparer.Ordinal).Select(x => x.Inst).ToList();
        return (list, badSocpaks, badFiles);
    }

    // socpak の直下の xml の Child の木 (調査の childs.json の形): (socpak, Child の guid) → (入れ子の親の Child の guid (無ければ "")、子の socpak、starMapRecord)、
    // 子の socpak → それを置く (socpak, Child の guid)。場所の実体のある socpak から、それを置く親の socpak (DeliverableSocpaks.Parents) を上へ辿って読む
    private static (Dictionary<(string Socpak, string Guid), (string Under, string Child, string Smr)> Nodes, Dictionary<string, List<(string Socpak, string Guid)>> BySoc, int Socpaks)
        ReadMissionLocChildGraph(StationIndex idx, DeliverableSocpaks soc, List<string> start, CancellationToken ct)
    {
        var nodes = new Dictionary<(string, string), (string, string, string)>();
        var bySoc = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(start, StringComparer.Ordinal);
        var queue = new Queue<string>(start);
        int read = 0;
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var s = queue.Dequeue();
            if (idx.Head(s)?.Root is { } root)
            {
                read++;
                void Walk(XElement el, string under)
                {
                    foreach (var c in el.Elements("ChildObjectContainers").Elements("Child"))
                    {
                        var g = ((string?)c.Attribute("guid") ?? "").ToLowerInvariant();
                        var name = ((string?)c.Attribute("name") ?? "").Replace('\\', '/').ToLowerInvariant();
                        if (name.StartsWith("data/objectcontainers/", StringComparison.Ordinal)) name = name["data/objectcontainers/".Length..];
                        var child = name.Length > 0 ? idx.Resolve(name) : "";
                        nodes[(s, g)] = (under, child, ((string?)c.Attribute("starMapRecord") ?? "").Trim().ToLowerInvariant());
                        if (child.Length > 0)
                        {
                            if (!bySoc.TryGetValue(child, out var l)) bySoc[child] = l = new List<(string, string)>();
                            l.Add((s, g));
                        }
                        Walk(c, g);
                    }
                }
                Walk(root, "");
            }
            if (soc.Parents.TryGetValue(s, out var parents))
                foreach (var p in parents.OrderBy(x => x, StringComparer.Ordinal))
                    if (seen.Add(p)) queue.Enqueue(p);
        }
        return (nodes, bySoc, read);
    }

    // 場所の実体のタグを決める (調査の hier.py・match3.py): 一般 = 自分のテンプレートと additionalData のタグに、親の場所 (Child の入れ子の親、
    // 入れ子でなければその socpak を置く Child。上へ全部。辿るのは 5000 個まで) の場所の実体の同じものを足し、タグの木の祖先まで広げたもの。
    // 生産・消費 = 自分のテンプレートと additionalData のもの (継がない) を祖先まで広げたもの
    private static void ExpandMissionLocTags(List<MissionLocInstance> insts, Dictionary<string, MissionLocTemplate> templates,
        Dictionary<(string Socpak, string Guid), (string Under, string Child, string Smr)> nodes, Dictionary<string, List<(string Socpak, string Guid)>> bySoc,
        Dictionary<string, string?> tagParent)
    {
        var ancCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        string[] Anc(string g)
        {
            if (ancCache.TryGetValue(g, out var a)) return a;
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (string? t = g; t != null && seen.Add(t); t = tagParent.TryGetValue(t, out var p) ? p : null) list.Add(t);
            return ancCache[g] = list.ToArray();
        }
        HashSet<string> Expand(IEnumerable<string> tags)
        {
            var r = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in tags) r.UnionWith(Anc(t));
            return r;
        }
        var own = insts.Select(x => x.Templates.SelectMany(t => templates[t].General).Concat(x.Extra).ToHashSet(StringComparer.Ordinal)).ToList();
        var byKey = new Dictionary<(string, string), int>();
        for (int i = 0; i < insts.Count; i++) byKey[(insts[i].Socpak, insts[i].Guid)] = i;
        IEnumerable<(string Socpak, string Guid)> Parents((string Socpak, string Guid) n) =>
            nodes.TryGetValue(n, out var nd) && nd.Under.Length > 0 ? [(n.Socpak, nd.Under)]
            : bySoc.TryGetValue(n.Socpak, out var l) ? l : [];
        for (int i = 0; i < insts.Count; i++)
        {
            var x = insts[i];
            var tags = new HashSet<string>(own[i], StringComparer.Ordinal);
            var seen = new HashSet<(string, string)>();
            var stack = new Stack<(string, string)>();
            stack.Push((x.Socpak, x.Guid));
            while (stack.Count > 0 && seen.Count < 5000)
            {
                var n = stack.Pop();
                foreach (var p in Parents(n))
                {
                    if (!seen.Add(p)) continue;
                    if (byKey.TryGetValue(p, out var pi)) tags.UnionWith(own[pi]);
                    stack.Push(p);
                }
            }
            x.General = Expand(tags);
            x.Produces = Expand(x.Templates.SelectMany(t => templates[t].Produces).Concat(x.ExtraProduces));
            x.Consumes = Expand(x.Templates.SelectMany(t => templates[t].Consumes).Concat(x.ExtraConsumes));
        }
    }

    // missions の行 (wiki の行を除く) ごとの場所の条件: 変数 (missionVariableName) と条件の並び。並びは record 名の順、変数は出てきた順
    // (ContractGenerator の行は、契約に無い変数を ContractTemplate から後ろに足す。調査の match3.py)
    private List<(string Mission, List<(string Variable, List<MissionLocCond> Conds)> Props)> ReadMissionLocProperties(DeliverableDcb dcb, CancellationToken ct)
    {
        var res = new List<(string, List<(string, List<MissionLocCond>)>)>();
        var templateCache = new Dictionary<string, List<(string, List<MissionLocCond>)>>(StringComparer.Ordinal);
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT record_name, raw_json FROM missions WHERE record_name NOT LIKE 'WikiMission.%' ORDER BY record_name";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var rn = r.GetString(0);
            var props = new List<(string, List<MissionLocCond>)>();
            using (var doc = JsonDocument.Parse(r.GetString(1)))
            {
                CollectMissionLocProperties(doc.RootElement, props);
                if (rn.Split('.')[0] == "ContractGenerator"
                    && doc.RootElement.TryGetProperty("_Resolved_", out var resolved) && resolved.ValueKind == JsonValueKind.Object
                    && resolved.TryGetProperty("template", out var tp) && tp.ValueKind == JsonValueKind.String
                    && MissionMapRecordsPathRegex.Match(tp.GetString()!) is { Success: true } m)
                {
                    var rel = m.Groups[1].Value.ToLowerInvariant();
                    if (!templateCache.TryGetValue(rel, out var tprops))
                    {
                        tprops = new List<(string, List<MissionLocCond>)>();
                        if (dcb.RelByLower.TryGetValue(rel, out var realRel) && dcb.ReadText(realRel) is { } text)
                        {
                            using var tdoc = JsonDocument.Parse(text);
                            CollectMissionLocProperties(tdoc.RootElement, tprops);
                        }
                        templateCache[rel] = tprops;
                    }
                    var have = props.Select(p => p.Item1).ToHashSet(StringComparer.Ordinal);
                    foreach (var p in tprops) if (!have.Contains(p.Item1)) props.Add(p);
                }
            }
            res.Add((rn, props));
        }
        return res;
    }

    // JSON の中の MissionProperty (value が MissionPropertyValue_Location / MissionPropertyValue_Locations) を出てきた順に集める (中も続けて探す)
    private static void CollectMissionLocProperties(JsonElement o, List<(string, List<MissionLocCond>)> outList)
    {
        if (o.ValueKind == JsonValueKind.Object)
        {
            if (o.TryGetProperty("_Type_", out var ty) && ty.ValueKind == JsonValueKind.String && ty.GetString() == "MissionProperty"
                && o.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("_Type_", out var vt) && vt.ValueKind == JsonValueKind.String
                && vt.GetString() is "MissionPropertyValue_Location" or "MissionPropertyValue_Locations")
            {
                var name = o.TryGetProperty("missionVariableName", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? "" : "";
                var conds = new List<MissionLocCond>();
                if (v.TryGetProperty("matchConditions", out var mcs) && mcs.ValueKind == JsonValueKind.Array)
                    foreach (var mc in mcs.EnumerateArray())
                    {
                        if (mc.ValueKind != JsonValueKind.Object || !mc.TryGetProperty("_Type_", out var mt) || mt.ValueKind != JsonValueKind.String
                            || mt.GetString() != "DataSetMatchCondition_TagSearch") continue;
                        var tagType = mc.TryGetProperty("tagType", out var tt) && tt.ValueKind == JsonValueKind.String ? tt.GetString() ?? "General" : "General";
                        var terms = new List<(string[], string[])>();
                        if (mc.TryGetProperty("tagSearch", out var ts) && ts.ValueKind == JsonValueKind.Array)
                            foreach (var term in ts.EnumerateArray())
                                if (term.ValueKind == JsonValueKind.Object) terms.Add((TagIds(term, "positiveTags"), TagIds(term, "negativeTags")));
                        conds.Add(new MissionLocCond(tagType, terms));
                    }
                outList.Add((name, conds));
            }
            foreach (var p in o.EnumerateObject()) CollectMissionLocProperties(p.Value, outList);
        }
        else if (o.ValueKind == JsonValueKind.Array)
            foreach (var x in o.EnumerateArray()) CollectMissionLocProperties(x, outList);

        static string[] TagIds(JsonElement term, string name) =>
            term.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.Object && t.TryGetProperty("_RecordId_", out var id) && id.ValueKind == JsonValueKind.String)
                    .Select(t => t.GetProperty("_RecordId_").GetString()!.ToLowerInvariant()).ToArray()
                : [];
    }

    // 条件の並びの鍵 (同じ条件の照合を 1 回にする)
    private static string MissionLocCondKey(List<MissionLocCond> conds) =>
        string.Join(";", conds.Select(c => c.TagType + ":" + string.Join("|", c.Terms.Select(t => string.Join(",", t.Pos) + "/" + string.Join(",", t.Neg)))));

    // 条件に当たる場所の実体の番号 (調査の match3.py の cands)
    private static List<int> MatchMissionLocations(List<MissionLocCond> conds, List<MissionLocInstance> insts)
    {
        var res = new List<int>();
        for (int k = 0; k < insts.Count; k++)
        {
            var ok = true;
            foreach (var c in conds)
            {
                var tags = c.TagType == "Produces" ? insts[k].Produces : c.TagType == "Consumes" ? insts[k].Consumes : insts[k].General;
                if (c.Terms.Count > 0 && !c.Terms.Any(t => t.Pos.All(tags.Contains) && !t.Neg.Any(tags.Contains))) { ok = false; break; }
            }
            if (ok) res.Add(k);
        }
        return res;
    }

    // 星図の場所 (starmap_locations。地図の窓の LoadPlaces と同じ列に、親の record を足したもの)
    private List<(StationPlace Place, string Name, string Parent)> ReadMissionMapStarmapRows()
    {
        var list = new List<(StationPlace, string, string)>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT record_name, uuid, type, system, name_en, parent_record FROM starmap_locations";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((new StationPlace(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), r.GetString(4), r.GetString(5)));
        return list;
    }

    // 場所の実体 → 地図のルート (record 名)。ルートの Child (StationRoot.Node) から、入れ子の Child と子の socpak の Child を辿り (ほかのルートの Child の
    // 下へは入らない。そこはそのルートのもの)、辿った Child の (xml の socpak, guid) にそのルートを付ける。場所の実体はその Child のルート。
    // 無ければ、Entity の starmapRecord・Child の starMapRecord が星図の場所のルート (同じ場所の規則 2 の置き場も) ならそれ
    private static (List<List<string>> InstRoots, int ByContain, int ByStarmap, int None) MapMissionLocInstancesToRoots(StationIndex idx, StationRootSearch search,
        List<MissionLocInstance> insts, Dictionary<(string Socpak, string Guid), (string Under, string Child, string Smr)> nodes, CancellationToken ct)
    {
        // socpak の xml の Child 要素の guid (SocChild → guid。SocpakHead の Children は要素の順に作られるので、同じ順に辿る)
        var guidCache = new Dictionary<string, Dictionary<SocChild, string>>(StringComparer.Ordinal);
        Dictionary<SocChild, string> GuidsOf(string xmlSocpak)
        {
            var key = idx.Resolve(xmlSocpak);
            if (guidCache.TryGetValue(key, out var d)) return d;
            d = new Dictionary<SocChild, string>(ReferenceEqualityComparer.Instance);
            var head = idx.Head(key);
            if (head?.Root != null)
            {
                void Walk(XElement el, List<SocChild> kids)
                {
                    int i = 0;
                    foreach (var c in el.Elements("ChildObjectContainers").Elements("Child"))
                    {
                        if (i >= kids.Count) return;
                        var sc = kids[i++];
                        d[sc] = ((string?)c.Attribute("guid") ?? "").ToLowerInvariant();
                        Walk(c, sc.Kids);
                    }
                }
                Walk(head.Root, head.Children);
            }
            return guidCache[key] = d;
        }
        var rootNodes = new HashSet<SocChild>(search.Roots.Values.Where(r => r.Node != null).Select(r => r.Node!), ReferenceEqualityComparer.Instance);
        var childRoots = new Dictionary<(string, string), List<string>>();
        foreach (var root in search.Roots.Values.OrderBy(r => r.Place.Record, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (root.Node == null) continue;
            var xml = root.Path.Count > 0 ? root.Path[^1] : "";
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<(SocChild N, string Xml, bool Start)>();
            stack.Push((root.Node, xml, true));
            while (stack.Count > 0)
            {
                var (n, x, start) = stack.Pop();
                if (!start && rootNodes.Contains(n)) continue;
                if (x.Length > 0 && GuidsOf(x).TryGetValue(n, out var g) && g.Length > 0)
                {
                    var key = (idx.Resolve(x), g);
                    if (!childRoots.TryGetValue(key, out var l)) childRoots[key] = l = new List<string>();
                    if (!l.Contains(root.Place.Record)) l.Add(root.Place.Record);
                }
                foreach (var k in n.Kids) stack.Push((k, x, false));
                if (n.Socpak.Length > 0)
                {
                    var s = idx.Resolve(n.Socpak);
                    if (visited.Add(s) && idx.Head(s) is { } h)
                        foreach (var k in h.Children) stack.Push((k, s, false));
                }
            }
        }
        var rootsByUuid = search.Roots.Values.Where(r => r.Place.Uuid.Length > 0).GroupBy(r => r.Place.Uuid.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Select(r => r.Place.Record).OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var res = new List<List<string>>();
        int byContain = 0, byStarmap = 0, none = 0;
        foreach (var x in insts)
        {
            if (childRoots.TryGetValue((x.Socpak, x.Guid), out var l)) { res.Add(l); byContain++; continue; }
            var smrs = x.StarmapRecords.ToList();
            if (nodes.TryGetValue((x.Socpak, x.Guid), out var nd) && nd.Smr.Length > 0) smrs.Add(nd.Smr);
            var hits = smrs.SelectMany(u => rootsByUuid.TryGetValue(u, out var rs) ? rs : Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal).ToList();
            res.Add(hits);
            if (hits.Count > 0) byStarmap++; else none++;
        }
        return (res, byContain, byStarmap, none);
    }

    // 契約ごとの行。リンクの文字は地図の窓の一覧と同じ決まり: 星図の場所は英語名で、同じ星系に地図を作る種類の同じ名前 (大文字小文字を区別しない) の場所が
    // 2 つ以上あれば、親の場所の名前 (ゲームの名前のあるとき) を（）で添える (StationMapWindow.RefreshStations)。星図に載っていない場所は
    // StationMapWindow.UnlistedNames の表示 (星図の場所で名前の無いものも同じ決まり)。床のメッシュは全部の場所で調べる (StationMapWindow.HasFloorMesh。
    // 地図の窓の一覧の床なしの判定 (s_noFloor・UnlistedPlace.NoFloor) と同じ調べ方)
    private static MissionMapData BuildMissionMapRows(List<(string Mission, List<(string Variable, HashSet<int> Cands)> Vars)> matched,
        List<List<string>> instRoots, StationRootSearch search, List<(StationPlace Place, string Name, string Parent)> starmap, StationIndex idx, CancellationToken ct)
    {
        var byRecord = starmap.GroupBy(r => r.Place.Record).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byUuid = starmap.Where(r => r.Place.Uuid.Length > 0).GroupBy(r => r.Place.Uuid.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        static bool HasGameName(string record, string name)
        {
            var dot = record.IndexOf('.');
            return name.Length > 0 && !string.Equals(name, dot >= 0 ? record[(dot + 1)..] : record, StringComparison.Ordinal);
        }
        // 地図を作る種類 (星図の場所では、一覧の種類のチェックのある種類と同じ。恒星は入らない) の、星系ごとの同じ名前
        var dupNames = starmap.Where(r => StationPlaceList.IsListedPlace(r.Place.Type))
            .GroupBy(r => (r.Place.System.ToLowerInvariant(), r.Name.ToLowerInvariant())).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var info = new Dictionary<string, (string Type, string System, string Name, bool Named, bool HasFloor)>(StringComparer.Ordinal);
        var floorCache = new Dictionary<string, bool>(StringComparer.Ordinal);
        (string Type, string System, string Name, bool Named, bool HasFloor) Info(string record)
        {
            if (info.TryGetValue(record, out var v)) return v;
            var root = search.Roots[record];
            if (root.Place.IsUnlisted)
            {
                var (_, label) = StationPlaceList.UnlistedNames(root, rec => byRecord.TryGetValue(rec, out var s) ? s.Name : null,
                    uuid => byUuid.TryGetValue(uuid, out var b) ? b.Name : null);
                v = (root.Place.Type, root.Place.System, label, true, true);
            }
            else
            {
                var row = byRecord[record];
                var name = row.Name;
                if (dupNames.Contains((row.Place.System.ToLowerInvariant(), row.Name.ToLowerInvariant()))
                    && byRecord.TryGetValue(row.Parent, out var parent) && HasGameName(parent.Place.Record, parent.Name))
                    name = $"{row.Name}（{parent.Name}）";
                // 名前の無い場所 (星図の名前が record 名のまま) は、内部の名前を出さないよう、星図外の場所と同じ決まりの名前にする
                if (!HasGameName(record, row.Name))
                    name = StationPlaceList.UnlistedNames(root, rec => byRecord.TryGetValue(rec, out var s) ? s.Name : null,
                        uuid => byUuid.TryGetValue(uuid, out var b) ? b.Name : null).Label;
                v = (row.Place.Type, row.Place.System, name, true, true);
            }
            return info[record] = v;
        }
        var vars = new List<MissionMapVar>();
        var sets = new List<List<string>>();
        var setIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var places = new List<MissionMapPlace>();
        foreach (var (mission, mvars) in matched)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (variable, cands) in mvars)
            {
                if (cands.Count == 0) continue;
                var roots = cands.SelectMany(c => instRoots[c]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (roots.Count == 0) continue;
                var key = string.Join("\n", roots);
                if (!setIds.TryGetValue(key, out var id))
                {
                    setIds[key] = id = sets.Count;
                    sets.Add(roots);
                    foreach (var rec in roots)
                    {
                        if (info.ContainsKey(rec)) continue;
                        var (type, system, name, named, hasFloor) = Info(rec);
                        // 同じ地図の鍵は地図の窓の一覧 (StationMapWindow.BuildContentKeys) と同じく、FindRoots のルートから StationContentKey.Of で作る
                        places.Add(new MissionMapPlace(rec, type, system, name, named, hasFloor, StationContentKey.Of(search.Roots[rec])));
                    }
                }
                vars.Add(new MissionMapVar(mission, variable, cands.Count, id));
            }
        }
        return new MissionMapData(vars, sets, places);
    }
}
