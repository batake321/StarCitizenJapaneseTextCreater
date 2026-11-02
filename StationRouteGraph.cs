using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの段階 4: 階の名前・階段・エレベーターとトラム (シャトル)・道順のグラフ、と段階 3・4 の結果の JSON。座標はルートの枠の局所 (メートル、z 上)。
//
// 実データで確かめたこと (2026-11-01、harness は scratchpad\stage34):
//   ・乗り場 (sctransitgateway) と行き先 (sctransitdestination) は、路線のタグ (Tags の TagId) でまとまる。路線のタグは sctransitmanager の PositiveTags にある
//     (Checkmate: 乗り場 89 個は全部タグあり、管理 11 個は全部 PositiveTags あり。Clinic の路線は 9108a202 (Locations/Reststop/ClinicSection))。
//     Orison は乗り場 829 個のうち 762 個にタグが無い (建物のエレベーター)。タグの無い乗り場のまとめ方は推測 (TransitLines の説明)。
//   ・同じ socpak のインスタンスが並ぶと、乗り場の CryGUID は同じになる (Clinic の 20 個の乗り場は全部 4a88c2a1)。節点はインスタンスの番号と組にする。
//   ・回る路線 (Orison のシャトル、タグ 4d1a3791 Locations/Transit/Looping) の向きは sctransitnavsplinedata で分かる:
//     道筋 (ControlPoints を Entity の PivotRelativePos・Rot の枠で置いたもの) の始まりと終わりが、それぞれ乗り場から 4〜5 m のところにある。
//     NavSpline_Looping_A_Spaceport-To-Commercial002 は 427f8012 (August Spaceport) → 41f4699c (Cloudview Center)。利用者が 00:34:57 に乗って降りた乗り場と一致。
//     A 回り: 427f8012 → 41f4699c → 4cdd6c07 (industrial) → 427f8012。B 回り: 4bb021eb (Cloudview Center) → 42af0d21 (spaceport) → 4e00620b → 4bb021eb。
//     向きの名前 (A・B) は道筋の名前の「Looping_<文字>_」から取る。
//   ・.rmxml の RoomMapping (未確認 5): SuperGUID は「Child の guid,…,Entity の CryGUID」の並び (Child の guid は .rmxml のある socpak から辿る子の xml の guid 属性)。
//     Clinic (rs_clinic_medium_no_view_001) で 173 種のうち 112 種がこの読み方で部屋・接続・乗り場に当たった。
//     1 段目 = 出発の部屋、2 段目 = 行き先の部屋、3 段目 = 出発の部屋の出口の接続 (roomconnector。どの行も 1 段目の部屋から 7.1 m)、
//     Distance = 接続から行き先までの道のり、NextRoomSuperGUID = 次の部屋、と読める (推測。Distance は 3 段目の接続と 2 段目の部屋の直線距離より 1〜7 m 短い。
//     部屋の位置が部屋の中心で、道のりが入口までのためと見られるが、確かめていない)。
//     グラフには「部屋 − 出口の接続」の辺 (費用は直線距離) だけを使う (RmxmlEdges)。

// 節点。Kind: room・connector・gateway・shop・stair・waypoint (床の上の経由点)。Floor は階の Id ("" は階に当たらない)、Section は区画のインスタンス (-1 は区画の外)
internal sealed record StationNode(string Id, string Kind, (double X, double Y, double Z) Pos, string Floor, int Section, string Name, int Instance, string Guid);

// 辺。Kind: walk (同じ区画・同じ階の直線)・door (区画をまたぐ近い節点)・floor (区画をまたぐ、床の上を通る直線)・rmxml・elevator・tram・stair。OneWay は A → B だけ (トラム)
internal sealed record StationEdge(int A, int B, double Cost, string Kind, string Line = "", string Dir = "", bool OneWay = false);

// 階段 (Bottom・Top は下と上の端、From・To は階の Id)。Source: mesh (階段のメッシュ)・ramp (スロープだけ)・room・module
// Outline (2026-11-02、利用者の指示「階段の形がわかるなら、階段アイコンでなく階段を枠で囲んでフロア同様に扱う」): 真上から見た階段の板 (メッシュの階段・スロープの板) の
// 凸包の環 (ステーションの局所の xy)。板から作れないもの (乗り場の縁 platform・部屋の組 room) は null (JSON に outline を書かない)
internal sealed record StationStair((double X, double Y, double Z) Bottom, (double X, double Y, double Z) Top, string From, string To, string Source, int Instance,
    IReadOnlyList<(double X, double Y)[]>? Outline = null, bool Landing = false);

// 乗り場 (Kind は elevator・tram) と行き先 (Kind は destination)。Line は路線のキー、Stop は乗り場のいちばん近い同じ路線の行き先の名前
internal sealed record StationTransit(string Kind, int Instance, string Guid, IReadOnlyList<string> Tags, (double X, double Y, double Z) Pos, string Floor,
    string Line, string Stop);

// 道順の 1 区切り。Kind: walk・elevator・tram・stair。To は着いた節点、Floor は着いた階の名前
internal sealed record StationRouteStep(string Kind, double Distance, int From, int To, string Line, string Dir, string Stop, string Floor);

internal sealed class StationGraph
{
    public List<StationNode> Nodes { get; } = new();
    public List<StationEdge> Edges { get; } = new();
    public Dictionary<string, int> ById { get; } = new(StringComparer.Ordinal);
    private List<List<(int To, int Edge)>>? _adj;

    public int Add(StationNode n)
    {
        if (ById.TryGetValue(n.Id, out var i)) return i;
        Nodes.Add(n);
        return ById[n.Id] = Nodes.Count - 1;
    }

    public void Link(StationEdge e)
    {
        if (e.A == e.B || !double.IsFinite(e.Cost)) return;
        Edges.Add(e);
        _adj = null;
    }

    private List<List<(int To, int Edge)>> Adj()
    {
        if (_adj != null) return _adj;
        var adj = Enumerable.Range(0, Nodes.Count).Select(_ => new List<(int, int)>()).ToList();
        for (int i = 0; i < Edges.Count; i++)
        {
            var e = Edges[i];
            adj[e.A].Add((e.B, i));
            if (!e.OneWay) adj[e.B].Add((e.A, i));
        }
        return _adj = adj;
    }

    // ダイクストラ。道が無ければ null。結果は辺の並び (向きつき: (from, to, 辺))
    public List<(int From, int To, StationEdge Edge)>? ShortestPath(int from, int to)
    {
        var adj = Adj();
        var dist = new double[Nodes.Count];
        var prev = new (int Node, int Edge)[Nodes.Count];
        Array.Fill(dist, double.PositiveInfinity);
        dist[from] = 0;
        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(from, 0);
        while (pq.TryDequeue(out var u, out var d))
        {
            if (d > dist[u]) continue;
            if (u == to) break;
            foreach (var (v, ei) in adj[u])
            {
                var nd = d + Edges[ei].Cost;
                if (nd < dist[v]) { dist[v] = nd; prev[v] = (u, ei); pq.Enqueue(v, nd); }
            }
        }
        if (double.IsPositiveInfinity(dist[to])) return null;
        var path = new List<(int, int, StationEdge)>();
        for (int v = to; v != from; v = prev[v].Node) path.Add((prev[v].Node, v, Edges[prev[v].Edge]));
        path.Reverse();
        return path;
    }

    public double Cost(List<(int From, int To, StationEdge Edge)> path) => path.Sum(p => p.Edge.Cost);

    // 節点ごとの、向きを無視してつながっている節点の数
    // (2026-11-01) 同じグラフ (節点・辺の数が同じ) で何度も呼ばれる (NearestNode が呼ぶたびに全体をたどっていて、Orison の取り込みが遅かった) ので、結果を覚えておく
    private int[]? _sizes;
    private (int Nodes, int Edges) _sizesKey;

    public int[] ComponentSizes()
    {
        if (_sizes != null && _sizesKey == (Nodes.Count, Edges.Count)) return (int[])_sizes.Clone();
        _sizes = ComputeComponentSizes();
        _sizesKey = (Nodes.Count, Edges.Count);
        return (int[])_sizes.Clone();
    }

    private int[] ComputeComponentSizes()
    {
        var adj = Adj();
        var comp = new int[Nodes.Count];
        Array.Fill(comp, -1);
        var sizes = new List<int>();
        var undirected = Enumerable.Range(0, Nodes.Count).Select(_ => new List<int>()).ToList();
        foreach (var e in Edges) { undirected[e.A].Add(e.B); undirected[e.B].Add(e.A); }
        for (int s = 0; s < Nodes.Count; s++)
        {
            if (comp[s] >= 0) continue;
            int id = sizes.Count, n = 0;
            var st = new Stack<int>(); st.Push(s); comp[s] = id;
            while (st.Count > 0) { var u = st.Pop(); n++; foreach (var v in undirected[u]) if (comp[v] < 0) { comp[v] = id; st.Push(v); } }
            sizes.Add(n);
        }
        return comp.Select(c => sizes[c]).ToArray();
    }
}

internal static class StationRouteGraph
{
    // 費用 (メートル相当)。エレベーター・トラムは乗る距離によらず一定 (計画「費用は一定」)。値は決めたもの
    public const double ElevatorCost = 30;
    public const double TramCost = 120;
    // 階段は端どうしの直線距離 × この倍率
    public const double StairFactor = 1.5;
    // 同じ区画・同じ階の徒歩の辺: いちばん近い K 個まで
    public const int WalkNeighbors = 6;
    // 区画をまたぐ徒歩の辺 (door): 同じ階でこの距離以内の節点どうし
    public const double DoorRadius = 3.0;
    public const double DoorBoxMargin = 1.0;
    public const int DoorNeighbors = 2;
    public const double DoorMaxLength = 15;
    public const double FloorLinkLength = 60;
    public const int FloorLinkNeighbors = 6;
    public const double FloorLinkRatio = 0.95;
    // 道筋の端と乗り場を同じとみなす距離
    public const double SplineSnap = 10;
    public const double ModuleEndRadius = 6;
    public const double PeripheralRadius = 1.5;
    // 乗り場と同じインスタンス (同じ socpak の置き場) の装置なら、この距離 (各軸) まで乗り場の装置とみなす (2026-11-01)。
    // New Babbage の路面電車の乗り場 ht_tran_plat_gateway (路線 c06371bb、8 か所) は、同じインスタンスに装置が 2 つあり、いちばん近いもので各軸 2.2〜2.3 m、
    // Lorville の路面電車の乗り場 gate_01〜06_int (路線 27d5acc0・ae116a07) は 1.7〜2.1 m、lorville_sp_int (7dd60a48・84e9ea1f) は 1.5〜2.5 m で、
    // PeripheralRadius (1.5 m) では外れていた。外れたままにするもの: Lorville の lorville_l19_int (いちばん近い装置 58 m)・lorville_cbd_int (281 m)
    // (走る道筋のある路線の乗り場だけ。TransitLines)
    public const double SameInstancePeripheralRadius = 3.0;

    // 利用者の情報で外す路線 (ステーションの record → 路線のタグ)。Orison: 「シャトルは同じ経路で向きが逆の 2 路線 (A 回り・B 回り) しか実装されていない」
    // (2026-11-01)。外すのは ae116a07 (DataCore のタグの名前 Locations/Transit/Lorville_WestLine。宇宙港 4037b0b5 ⇄ Vision Center 4b7f421a、
    // 道筋 TransitNavSpline_ConventionCenter-001・-002)。データには、この路線を A・B と見分けて外せる印 (管理・道筋・装置の有無、Child の class) は無かった
    // (2026-11-01) 決め打ちの外し方なのでやめた (利用者の指示)。空のまま残す (公開している名前を変えないため)
    public static readonly IReadOnlyDictionary<string, string[]> ExcludedLineTags = new Dictionary<string, string[]>(StringComparer.Ordinal);

    private static bool IsExcludedLine(StationTree tree, string tag) =>
        ExcludedLineTags.TryGetValue(tree.Root.Place.Record, out var l) && l.Contains(tag, StringComparer.OrdinalIgnoreCase);

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static readonly Regex FloorKey = new(@"_Floor_\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DestFloorKey = new(@"floor_\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 階の名前・区画の名前で使わない名前 (場所を表さないもの。@Maps_LocationUnknown の訳は「Location unknown」で、Checkmate の Contested Zone の区画に 6 つ付いていた)
    // 行き先の鍵が、階を表す名前 (floor_NN・ground_floor・lobby・foyer・roof) か、名前の無いもの (空・@LOC_UNINITIALIZED など) か
    public static bool IsLevelStop(string stop) => NoNameKeys.Contains(stop) || DestLevelKey.IsMatch(stop);

    private static readonly HashSet<string> NoNameKeys = new(StringComparer.OrdinalIgnoreCase) { "@LOC_UNINITIALIZED", "@generic_upper_floor", "@generic_lower_floor", "@Maps_LocationUnknown", "" };
    // エレベーターの行き先のうち、階を表す名前 (floor_NN・ground_floor・lobby・foyer・roof)
    private static readonly Regex DestLevelKey = new(@"(floor_\d+|ground_floor|_lobby$|_foyer$|elevator_roof)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex AreaKey = new(@"^@area_name_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // 大文字小文字だけ違う名前のうち、頭が大文字のもの (無ければ最初のもの)
    private static string PreferCapital(IEnumerable<string> names)
    {
        var l = names.ToList();
        return l.FirstOrDefault(n => n.Length > 0 && char.IsUpper(n[0])) ?? l[0];
    }

    // 鍵 (@…) の表示の名前: localize (global.ini の訳。鍵は @ を除いたもの) で引き、無ければ @ を除いた鍵のまま
    public static string Display(string key, Func<string, string?>? localize)
    {
        if (key.Length == 0) return "";
        var k = key.StartsWith('@') ? key[1..] : key;
        return Loc(key, localize) ?? k;
    }

    // 星図の場所の名前のうち、内部の名前を除いたもの (2026-11-01、利用者の決まり「内部名を出さない」)。内部の名前とみなすのは、
    // ① ルートの場所の record の名前 (StarMapObject. を除く) と同じ名前 (例 GlaciemRing_MissionLocation・KeegerBelt_MissionLocation・Stanton2c_BennyHenge)、
    // ② 空白が無く「_」を含む名前 (例 JumpPoint_Pyro_Castra・Stanton1_DistributionCentre)。どちらでもなければそのまま
    public static IReadOnlyDictionary<string, string>? UsablePlaceNames(StationTree tree, IReadOnlyDictionary<string, string>? placeNames)
    {
        if (placeNames == null) return null;
        var rec = tree.Root.Place.Record.StartsWith("StarMapObject.", StringComparison.Ordinal) ? tree.Root.Place.Record["StarMapObject.".Length..] : tree.Root.Place.Record;
        static bool Internal(string n, string rec) => string.Equals(n.Trim(), rec, StringComparison.OrdinalIgnoreCase) || !n.Contains(' ') && n.Contains('_');
        if (!placeNames.Values.Any(n => Internal(n, rec))) return placeNames;
        return placeNames.Where(kv => !Internal(kv.Value, rec)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    // 店の名前 (StationMapExports.LoadShopNames の表): 「<ルートの場所の record>|<GUID>」を先に、無ければ「<GUID>」(どの場所でも同じ名前の店だけある)。無ければ null
    public static string? ShopNameOf(StationTree tree, IReadOnlyDictionary<string, string> shopNames, string guid)
    {
        var g = guid.Trim().ToLowerInvariant();
        return shopNames.TryGetValue($"{tree.Root.Place.Record}|{g}", out var n) ? n : shopNames.TryGetValue(g, out n) ? n : null;
    }

    public static void NameFloors(StationTree tree, IReadOnlyList<StationFloor> floors) => NameFloors(tree, floors, null, null);

    // 階の名前 (計画 4 の 4 を、利用者の決定 (2026-11-01) で床のまとまりごとにし、階を表す名前だけにしたもの)。まとまり (StationFloor.Group) の箱の中の点だけを見る:
    //   ① interiormapsection の @*_Floor_* (箱の底 + 1 m の高さの階) → ② interiormaplabel の @*_Floor_* (名札の高さ − 1 m の階。名札は床 + 1.5 m)
    //   → ③ エレベーターの行き先のうち階を表すもの (floor_NN・ground_floor・lobby・foyer・roof。行き先の高さ − 1.5 m の階。行き先は床 + 2 m)
    //   → ④ 「<まとまりの名前> の <n> 階」(n はまとまりの中の下からの番号。まとまりの名前は NameGroups。名前の無いまとまりは、近くの名前の付いたまとまりの
    //      「<名前> 付近」) → どれも無ければ名前を付けず、一覧に出さない。
    // 名前は代表の 1 つ (つながない)。訳の引ける鍵だけを使い、Name・DisplayName とも訳の文字 (鍵は Labels)。店・施設の名前は階の名前にしない。
    // placeNames は星図の場所の uuid (小文字) → 名前 (starmap_locations の name_en。まとまりの名前に使う。無くてよい)
    // 名前として使える鍵か: localize があるときは、訳のある鍵だけ (Checkmate の @Maps_LocationUnknown・@area_name_hub などは global.ini に無く、鍵がそのまま出ていた)
    // localize が無ければ、鍵は名前に使わない (訳が引けないので。利用者の決定 2026-11-01: 鍵・socpak の名前を名前に使わない)
    private static bool Usable(string key, Func<string, string?>? localize) =>
        !NoNameKeys.Contains(key) && Loc(key, localize) is { Length: > 0 };

    // 訳を引く: 鍵 (@ を除く) で引き、無ければ「鍵,P」で引く (global.ini には ,P の付いた鍵が 13,504 件ある)
    private static string? Loc(string key, Func<string, string?>? localize)
    {
        if (localize == null || key.Length == 0) return null;
        var k = key.StartsWith('@') ? key[1..] : key;
        return localize(k) is { Length: > 0 } t ? t : localize(k + ",P") is { Length: > 0 } p ? p : null;
    }

    public static void NameFloors(StationTree tree, IReadOnlyList<StationFloor> floors, Func<string, string?>? localize, IReadOnlyDictionary<string, string>? placeNames) =>
        NameFloors(tree, floors, localize, placeNames, null);

    // shopNames は店の GUID (SCShop の EntityCryGUID、小文字) → ゲームの店の名前 (game_shops の name。shop_id の 3 番目)。無ければ店の名前は使わない
    public static void NameFloors(StationTree tree, IReadOnlyList<StationFloor> floors, Func<string, string?>? localize, IReadOnlyDictionary<string, string>? placeNames,
        IReadOnlyDictionary<string, string>? shopNames)
    {
        placeNames = UsablePlaceNames(tree, placeNames);
        var groupNames = NameGroups(tree, floors, localize, placeNames, shopNames);
        var nearOf = new Dictionary<string, string>(StringComparer.Ordinal);
        // (2026-11-01、利用者の指示「1 階 2 階などの表示は意味ない (勝手につけたものだから)」) 階の名前は、データにある名前だけで付ける。作った番号 (「<名前> の n 階」) は使わない。
        // 名前の候補 (規則の番号の小さいほうを先に使う):
        //   1 interiormapsection の @*_Floor_* (箱の底 + 1 m の高さの階)  2 interiormaplabel の @*_Floor_* (名札 − 1 m)
        //   3 エレベーターの行き先のうち階を表すもの (floor_NN・ground_floor・lobby・foyer・roof。行き先 − 1.5 m)
        //   4 エレベーターの行き先のうち階を表さないもの (施設・区域の名前。行き先 − 1.5 m)
        //   5 interiormaplabel の @area_name_* (名札 − 1 m) と、interiormapsection の名前 (@*_Floor_* でないもの。箱の底 + 1 m)
        //   6 その階の店の名前 (shopNames。店の位置 − 1 m)。階の名前にも候補 (Labels) にも使わない (2026-11-01)
        // どれも無い階は、まとまりの名前 (NameGroups)、無ければ近くの名前の付いたまとまりの「<名前> 付近」、無ければ場所 (ルート) の星図の名前。
        // 訳の引ける鍵だけを使う (Usable)。同じ規則の中では当たった数の多い名前、同じなら名前の順
        var hits = floors.ToDictionary(f => f.Id, _ => new List<(int Rule, string Name, string Line)>(), StringComparer.Ordinal);
        foreach (var p in tree.Points)
        {
            if (p.Pos is not { } pos || !Usable(p.Name, localize)) continue;
            double z; int rule;
            double SectionBottom() => pos.Z - (p.Attrs.TryGetValue("size", out var sz) && StationIndex.ParseVec(sz) is { } s ? s.Z : 0) / 2 + 1;
            if (p.Kind == "entitycomponentinteriormapsection" && FloorKey.IsMatch(p.Name)) { z = SectionBottom(); rule = 1; }
            else if (p.Kind == "entitycomponentinteriormaplabel" && FloorKey.IsMatch(p.Name)) { z = pos.Z - 1; rule = 2; }
            else if (p.Kind == "sctransitdestination" && !NoNameKeys.Contains(p.Name) && DestLevelKey.IsMatch(p.Name)) { z = pos.Z - 1.5; rule = 3; }
            else if (p.Kind == "sctransitdestination" && !NoNameKeys.Contains(p.Name)) { z = pos.Z - 1.5; rule = 4; }
            else if (p.Kind == "entitycomponentinteriormaplabel" && AreaKey.IsMatch(p.Name)) { z = pos.Z - 1; rule = 5; }
            else if (p.Kind == "entitycomponentinteriormapsection" && !NoNameKeys.Contains(p.Name)) { z = SectionBottom(); rule = 5; }
            else continue;
            // 行き先 (規則 3・4) は、行き先 − 1.5 m で階が無ければ行き先の高さでも探す (2026-11-01。Seraphim の居住区の行き先 @ui_interactor_floor_01〜05 は床の 0.7 m 上にあり、
            // − 1.5 m では床の 0.8 m 下になって (FloorAtXY の下の許し 0.6 m を超えて) 当たらなかった)
            // (2026-11-02) 行き先は DestFloor (点の真下 0〜1.5 m の床を先に探す)
            var hf = rule is 3 or 4 ? DestFloor(floors, pos) : StationFloorBuilder.FloorAtXY(floors, pos.X, pos.Y, z);
            if (hf is { } f) hits[f.Id].Add((rule, Display(p.Name, localize), rule == 3 ? p.Tags.FirstOrDefault() ?? "" : ""));
        }
        if (shopNames != null)
            foreach (var sh in tree.Shops)
                if (ShopNameOf(tree, shopNames, sh.Guid) is { Length: > 0 } sn && StationFloorBuilder.FloorAtXY(floors, sh.Pos.X, sh.Pos.Y, sh.Pos.Z - 1) is { } f)
                    hits[f.Id].Add((6, sn, ""));
        var rootName = placeNames != null && tree.Instances.Count > 0 && placeNames.TryGetValue(tree.Instances[0].StarMapRecord, out var rn) ? rn : "";
        // 規則 3 の行き先の路線 (Tags の最初) ごとの、行き先の当たった階の数。1 つの階に 2 本の路線の階の名前が当たるとき (Orison の Cloudview Center の塔は、
        // Floor 01〜17 の路線と、病院 (Orison General) の Floor 01〜07 の路線が同じ高さの階に止まる) は、止まる階の多い路線の名前を先にする
        var lineSize = hits.SelectMany(kv => kv.Value.Where(x => x.Rule == 3 && x.Line.Length > 0).Select(x => (x.Line, Floor: kv.Key))).Distinct()
            .GroupBy(x => x.Line).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        // 階ごとの候補の名前 (規則の順、同じ規則の中は止まる階の多い路線の名前、次に当たった数の多い順)
        List<string> Cands(StationFloor f) => hits[f.Id].GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Rule: g.Min(x => x.Rule), Line: g.Max(x => lineSize.GetValueOrDefault(x.Line)), Count: g.Count(), Name: PreferCapital(g.Select(x => x.Name))))
            .OrderBy(t => t.Rule).ThenByDescending(t => t.Line).ThenByDescending(t => t.Count).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(t => t.Name).ToList();
        foreach (var f in floors)
        {
            var h = hits[f.Id];
            // Labels はこの階の名前の候補を、使う順 (規則の順、同じ規則の中は当たった数の多い順) に並べたもの
            f.Labels.Clear();
            // (2026-11-01、検証の指摘 F) 店の名前 (規則 6) は候補にも入れない (同じ名前の階の括弧に「Galleria（Bar）」のように店の名前が出ていた)
            f.Labels.AddRange(Cands(f).Where(n => h.Any(x => x.Rule <= 5 && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))));
            var gname = groupNames.TryGetValue(f.Group, out var gn) ? gn : ("", "");
            f.GroupName = gname.Item2;
            if (gname.Item1.Length == 0 && NearGroupName(floors, f.Group, groupNames) is { } near) { gname = (near.Key, $"{near.Display} 付近"); nearOf[f.Group] = near.Group; f.GroupName = near.Display; }
            // (2026-11-01、利用者の指摘「Noodle Shop が階にある。これは区画のはず」) 店の名前 (規則 6) は階の名前にしない。店の名前しか当たらない階は、
            // まとまりの名前 (無ければ下の「付近」・場所の名前) にする。
            // Stanton Gateway (RR_JP_StantonMagnus) の Galleria (rs_comm_stan-magnus_jp1) は、−14〜−12 m・−7.75〜−4.82 m・−1〜1.16 m の 3 つの床の高さがあり、
            // 上の 2 つの階が「Bar」「Noodle Shop」になっていた
            if (h.Any(x => x.Rule <= 5))
            {
                var best = h.Where(x => x.Rule <= 5).Min(x => x.Rule);
                f.DisplayName = Cands(f).First(n => h.Any(x => x.Rule <= 5 && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)));
                f.NameSource = best switch { 1 => "section", 2 => "label", 3 => "elevator", 4 => "destination", _ => "area" };
            }
            else if (gname.Item1.Length > 0) { f.DisplayName = gname.Item2; f.NameSource = "group"; }
            else
            {
                // 近くの名前の付いたまとまり (距離・高さの上限なし) の「<名前> 付近」、無ければ場所の名前
                string prefix = "";
                if (f.Bounds is { } b0)
                {
                    double bd = double.MaxValue;
                    foreach (var g in floors.Where(x => x.Group != f.Group && x.Bounds != null && groupNames.ContainsKey(x.Group)).GroupBy(x => x.Group))
                    {
                        var o = g.First().Bounds!.Value;
                        double dx = Math.Max(0, Math.Max(o.MinX - b0.MaxX, b0.MinX - o.MaxX)), dy = Math.Max(0, Math.Max(o.MinY - b0.MaxY, b0.MinY - o.MaxY));
                        var d = Math.Sqrt(dx * dx + dy * dy);
                        if (d < bd) { bd = d; prefix = groupNames[g.Key].Display + " 付近"; }
                    }
                }
                // 場所の名前も無い (内部の名前を外した) ときは、場所の種類の言葉 (［詳細］の場所の種類と同じ訳。ShopTree.TypeLabel。2026-11-01、利用者の決定)
                var typeName = rootName.Length == 0 && tree.Root.Place.Type.Length > 0
                    ? StationPlace.ExtraTypeLabels.TryGetValue(tree.Root.Place.Type, out var xt) ? xt : ShopTree.TypeLabel(tree.Root.Place.Type) : "";
                f.DisplayName = prefix.Length > 0 ? prefix : rootName.Length > 0 ? rootName : typeName;
                f.NameSource = prefix.Length > 0 ? "near" : rootName.Length > 0 ? "place" : typeName.Length > 0 ? "type" : "none";
            }
            f.Name = f.DisplayName;
            // 一覧に出す階: 名前の付いた階。乗り場の高さだけの階は出さない (使える物の無い階・床の形の無い階は StationMapJson.Write で外す)
            // 名前を近くのまとまり・場所・場所の種類から借りた階 (near・place・type) は出さない (前と同じ)
            f.Listed = !f.FromTransit && f.DisplayName.Length > 0 && f.NameSource is not ("near" or "place" or "type" or "none");
        }
        // 同じ名前の階の括弧の添え書きは、一覧に出す階が決まってから StationMapJson.Write で付ける (一覧に出る階どうしだけで比べる)
        // 乗り場の行き先の名前 (同じ名前の階の括弧の材料。2026-11-01): 乗り場 (路線の Gateways) のいちばん近い同じ路線の行き先 (StopName) の訳のあるもの (階を表すものを除く)。
        // 乗り場の階は TransitList と同じ当て方 (FloorAtXY、無ければ GatewayFloorByBounds)
        foreach (var f in floors) f.StopLabels.Clear();
        {
            var destsS = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
            foreach (var l in TransitLines(tree))
                foreach (var (_, g) in l.Gateways)
                {
                    var stop = StopName(destsS, l.Key, g.Pos!.Value);
                    // 階を表す行き先 (DestLevelKey: floor_NN・ground_floor・lobby など) は入れない (Orison の Cloudview Center で、病院の路線の「Floor 01」が
                    // 本館の Floor 06 の括弧に入り「Floor 06（Floor 01）」になった)
                    if (!Usable(stop, localize) || DestLevelKey.IsMatch(stop)) continue;
                    var gf = StationFloorBuilder.FloorAtXY(floors, g.Pos!.Value.X, g.Pos!.Value.Y, g.Pos!.Value.Z) ?? GatewayFloorByBounds(floors, g.Pos!.Value);
                    var nm = Display(stop, localize);
                    if (gf != null && !gf.StopLabels.Contains(nm, StringComparer.OrdinalIgnoreCase)) gf.StopLabels.Add(nm);
                }
        }
        HideNoRideFloors(tree, floors);
        MergeUnnamedUnlistedFloors(tree, floors);
        // 「<名前> 付近」で名前を付けたまとまりは、名前を借りたまとまりの階が全部 ElevatorOnly なら同じく一覧から外す
        // (Checkmate のドッキングポートの横の小さなまとまり 4 つ。乗らないエレベーターの先にしかない)
        foreach (var (g, src) in nearOf)
            if (floors.Where(x => x.Group == src).All(x => x.ElevatorOnly))
                foreach (var x in floors.Where(x => x.Group == g)) { x.ElevatorOnly = true; x.Listed = false; }
        // ステーションの中で、大文字小文字だけ違う表示の名前は 1 つの書き方にそろえる (頭が大文字のもの)
        foreach (var g in floors.Where(f => f.DisplayName.Length > 0).GroupBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var pick = PreferCapital(g.Select(f => f.DisplayName));
            foreach (var f in g) f.DisplayName = pick;
        }
        // (2026-11-02、利用者の指示「名前での紐づけ」) 階の表示の名前 (データから付いたもの。括弧の添え書きの前) が、重ねる鍵 (StackKeys と StackKeyAliases) の訳と
        // 同じ (大文字小文字は区別しない) なら、その階にその鍵の番号を付ける。名前どうしを比べるだけで、名前の決め打ちはしない。
        // 訳の引けない鍵 (localize が無いときも) は比べない。使うのは StackOrder (その路線の行き先にその鍵があるときだけ)
        var keyNames = StackKeys.Select((k, i) => (Key: k, Index: i)).Concat(StackKeyAliases.Select(kv => (kv.Key, Index: kv.Value)))
            .Select(t => (Name: Loc(t.Key, localize), t.Index)).Where(t => t.Name is { Length: > 0 }).ToList();
        foreach (var f in floors)
        {
            f.NameStackKey = -1;
            if (f.DisplayName.Length == 0) continue;
            foreach (var (name, idx) in keyNames)
                if (string.Equals(name, f.DisplayName, StringComparison.OrdinalIgnoreCase)) { f.NameStackKey = f.NameStackKey < 0 ? idx : Math.Min(f.NameStackKey, idx); }
        }
    }

    // 星図に載っていない場所 (Place.IsUnlisted) で、どの階にもデータの名前が無い (名前が全部、種類の言葉 (NameSource「type」) か無し) ときは、
    // 全部の床を 1 つの階にまとめる (2026-11-01、利用者の決定「地上の前哨基地では、高さで階に分けず、1 枚の図にまとめる。Sublevel などの階の名前がデータにある場所は今のまま」)。
    // Bloodshot Ridge の廃村 (Drlct_Stmnt_006) は、離れた建物が地面の高さの違いで 18 の階に分かれ、どれも「前哨基地」になっていた。
    // まとめ方: 板 (PlateSet) のいちばん多い階に、ほかの階の板・升目の高さ (CellZ)・高さの範囲・箱を足し、ほかの階は板の無い階にする (一覧にも出ない)。
    // ほかの階のまとまり (Group) もその階のまとまりにする (PlateFloor が板のまとまりの階に無いときに全部の階から探すため)。ステーション・都市・星図の場所は変えない
    public static int LastMergedFloors;
    public static void MergeUnnamedUnlistedFloors(StationTree tree, IReadOnlyList<StationFloor> floors)
    {
        LastMergedFloors = 0;
        if (!tree.Root.Place.IsUnlisted || floors.Count < 2 || !floors.All(f => f.NameSource is "type" or "none")) return;
        var keep = floors.OrderByDescending(f => f.PlateSet?.Count ?? 0).ThenBy(f => f.Id, StringComparer.Ordinal).First();
        keep.PlateSet ??= new HashSet<StationPlate>();
        foreach (var f in floors)
        {
            if (f == keep) continue;
            if (f.PlateSet != null) keep.PlateSet.UnionWith(f.PlateSet);
            if (f.CellZ != null)
            {
                keep.CellZ ??= new Dictionary<(int, int), (double Min, double Max)>();
                foreach (var (c, r) in f.CellZ) keep.CellZ[c] = keep.CellZ.TryGetValue(c, out var o) ? (Math.Min(o.Min, r.Min), Math.Max(o.Max, r.Max)) : r;
            }
            keep.ZMin = Math.Min(keep.ZMin, f.ZMin);
            keep.ZMax = Math.Max(keep.ZMax, f.ZMax);
            if (f.Bounds is { } fb) keep.Bounds = keep.Bounds is { } kb ? (Math.Min(kb.MinX, fb.MinX), Math.Min(kb.MinY, fb.MinY), Math.Max(kb.MaxX, fb.MaxX), Math.Max(kb.MaxY, fb.MaxY)) : fb;
            f.PlateSet = new HashSet<StationPlate>();
            // 升目の無い (空の) 表にして、点の階の当て方 (FloorAtXY) でこの階に当たらないようにする (CellZ が null の階は高さと箱だけで当たるため)
            f.CellZ = new Dictionary<(int, int), (double Min, double Max)>();
            f.Group = keep.Group;
            // 箱を無くして、乗り場の階の当て方 (GatewayFloorByBounds。箱の中の階のうち箱のいちばん小さいもの) でもこの階が選ばれないようにする
            // (検証の指摘 2026-11-01: 箱が残っていると、FloorAtXY の外れた乗り場が一覧に出ない空の階に付くことがあった)
            f.Bounds = null;
            f.Listed = false;
            LastMergedFloors++;
        }
        StationFloorBuilder.ForgetPlateFloorMap(floors);
    }

    // 乗らないエレベーター (Line.NoRide) の行き先の階を一覧から外す (ElevatorOnly = true、Listed = false)。乗り口は、乗り場のいちばん近い同じ路線の行き先が
    // 入口・ロビーを表すもの (lobby・entry・entrance・ground_floor) か、階を表す名前でも船の発着・貨物の行き先 (ShipStopKey) でもないもの。
    //   ① 乗り口でない乗り場のある階で、乗り口の乗り場もほかの (乗る) 路線の乗り場も無い階。
    //   ② まとまりの中の乗り場が全部 ① の乗り場なら、そのまとまりの階を全部 (病棟・パッド・ドッキングポートなど、乗り場の階のあいだの階も含む)
    private static readonly Regex EntranceStopKey = new(@"(lobby|entry|entrance|ground_floor)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // 船の発着・貨物の行き先 (ハンガーのエレベーター・Station_ATC の行き先)。これと階を表す名前のどちらでもない行き先も乗り口とみなす
    // (Orison のハンガーのエレベーターの乗り口の行き先は @ui_interactor_ST2_spaceport「August Dunlow Spaceport」で、lobby の語が無い)
    private static readonly Regex ShipStopKey = new(@"(dockingport|_pad_|pad_\d|hangar|cargodeck|freight|area_name_cargo|area_name_refinery|shipdealer|_tour)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void HideNoRideFloors(StationTree tree, IReadOnlyList<StationFloor> floors)
    {
        var lines = TransitLines(tree);
        var dests = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
        var info = new List<(StationFloor? F, bool NoRide, bool Entrance)>();
        foreach (var l in lines)
            foreach (var (_, g) in l.Gateways)
            {
                var f = StationFloorBuilder.FloorAtXY(floors, g.Pos!.Value.X, g.Pos!.Value.Y, g.Pos!.Value.Z);
                var stop = StopName(dests, l.Key, g.Pos!.Value);
                info.Add((f, l.NoRide, l.NoRide && (EntranceStopKey.IsMatch(stop) || stop.Length > 0 && !FloorKey.IsMatch(stop) && !DestLevelKey.IsMatch(stop) && !ShipStopKey.IsMatch(stop))));
            }
        var hide = new HashSet<StationFloor>();
        foreach (var grp in info.Where(t => t.F != null).GroupBy(t => t.F!))
            if (grp.Any(t => t.NoRide && !t.Entrance) && !grp.Any(t => !t.NoRide || t.Entrance)) hide.Add(grp.Key);
        foreach (var grp in info.Where(t => t.F != null && t.F.Group.Length > 0).GroupBy(t => t.F!.Group))
            if (grp.All(t => t.F != null && hide.Contains(t.F)))
                foreach (var f in floors.Where(f => f.Group == grp.Key)) hide.Add(f);
        foreach (var f in hide) { f.ElevatorOnly = true; f.Listed = false; }
    }

    // 名前の無いまとまりに近い (箱の間が NearGroupRadius m 以内、高さの範囲が 30 m 以内で重なる) 名前の付いたまとまりの名前。いちばん近いもの
    public const double NearGroupRadius = 60;

    private static (string Key, string Display, string Group)? NearGroupName(IReadOnlyList<StationFloor> floors, string group, Dictionary<string, (string Key, string Display)> names)
    {
        var own = floors.Where(f => f.Group == group && f.Bounds != null).ToList();
        if (own.Count == 0) return null;
        var b = own[0].Bounds!.Value;
        double zl = own.Min(f => f.ZMin) - 30, zh = own.Max(f => f.ZMax) + 30;
        (string, string, string)? best = null; double bd = double.MaxValue;
        foreach (var g in floors.Where(f => f.Group != group && f.Bounds != null && names.ContainsKey(f.Group)).GroupBy(f => f.Group))
        {
            if (g.Max(f => f.ZMax) < zl || g.Min(f => f.ZMin) > zh) continue;
            var o = g.First().Bounds!.Value;
            double dx = Math.Max(0, Math.Max(o.MinX - b.MaxX, b.MinX - o.MaxX)), dy = Math.Max(0, Math.Max(o.MinY - b.MaxY, b.MinY - o.MaxY));
            var d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= NearGroupRadius && d < bd) { bd = d; best = (names[g.Key].Key, names[g.Key].Display, g.Key); }
        }
        return best;
    }

    // 床のまとまりの名前 (鍵, 表示の名前)。まとまりの箱 (階の Bounds) の中の点・インスタンスから、
    //   ① interiormapsection の名前 (@*_Floor_* を除く。Checkmate の @RR_P2_L4「Checkmate」など、館内地図の範囲の名前)
    //   → ② 地区 (シャトルの駅の行き先 @ui_interactor_ST2_transit_<x> から、global.ini の ui_interactor_ST2_<x>_platform か ui_interactor_ST2_<x> の訳のあるもの。
    //        Orison: commercial → Cloudview Center、industrial → Providence Industrial Platform、spaceport → August Dunlow Spaceport。鍵の並びからの推測の規則)
    //   → ②' 星図の場所 (インスタンスの StarMapRecord が placeNames にあるもの。ルートは除く。インスタンスの位置がまとまりの箱に入るもの) → ③ 行き先の名前 (階を表すもの・@LOC_UNINITIALIZED・@generic_*_floor を除く)
    //   → ④ interiormaplabel の @area_name_* → ⑤ 店 (SCShop の Name)。同じ規則の中では、いちばん多く当たった名前 (同数なら名前の順)。
    // 星図の場所の名前は placeNames の文字のまま (訳を引かない)。店は Name のまま (表示の側で game_shops の名前に直す)
    public static Dictionary<string, (string Key, string Display)> NameGroups(StationTree tree, IReadOnlyList<StationFloor> floors,
        Func<string, string?>? localize, IReadOnlyDictionary<string, string>? placeNames) => NameGroups(tree, floors, localize, placeNames, null);

    public static Dictionary<string, (string Key, string Display)> NameGroups(StationTree tree, IReadOnlyList<StationFloor> floors,
        Func<string, string?>? localize, IReadOnlyDictionary<string, string>? placeNames, IReadOnlyDictionary<string, string>? shopNames)
    {
        placeNames = UsablePlaceNames(tree, placeNames);
        var res = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var g in floors.Where(f => f.Group.Length > 0 && f.Bounds != null).GroupBy(f => f.Group))
        {
            var b = g.First().Bounds!.Value;
            double zMin = g.Min(f => f.ZMin) - 3, zMax = g.Max(f => f.ZMax) + 6;
            bool In((double X, double Y, double Z) q) => q.X >= b.MinX && q.X <= b.MaxX && q.Y >= b.MinY && q.Y <= b.MaxY && q.Z >= zMin && q.Z <= zMax;
            var cands = new List<(int Rule, string Key, string Disp)>();
            if (placeNames != null)
                foreach (var inst in tree.Instances)
                    if (inst.Index > 0 && inst.StarMapRecord.Length > 0 && placeNames.TryGetValue(inst.StarMapRecord, out var pn) && In((inst.ToStation.X, inst.ToStation.Y, inst.ToStation.Z)))
                        cands.Add((3, pn, pn));
            foreach (var p in tree.Points)
            {
                if (p.Pos is not { } pos || !In(pos) || !Usable(p.Name, localize)) continue;
                if (p.Kind == "entitycomponentinteriormapsection" && !FloorKey.IsMatch(p.Name)) cands.Add((1, p.Name, Display(p.Name, localize)));
                else if (p.Kind == "sctransitdestination" && !DestLevelKey.IsMatch(p.Name))
                {
                    if (DistrictKey(p.Name, localize) is { } dk) cands.Add((2, dk, Display(dk, localize)));
                    cands.Add((4, p.Name, Display(p.Name, localize)));
                }
                else if (p.Kind == "entitycomponentinteriormaplabel" && AreaKey.IsMatch(p.Name)) cands.Add((5, p.Name, Display(p.Name, localize)));
            }
            if (shopNames != null)
                foreach (var s in tree.Shops)
                    if (In(s.Pos) && ShopNameOf(tree, shopNames, s.Guid) is { Length: > 0 } sn) cands.Add((6, sn, sn));
            if (cands.Count == 0) continue;
            var best = cands.Min(c => c.Rule);
            var pick = cands.Where(c => c.Rule == best).GroupBy(c => c.Disp, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First();
            res[g.Key] = (pick.First().Key, PreferCapital(pick.Select(c => c.Disp)));
        }
        return res;
    }

    // シャトルの駅の行き先の鍵から地区の名前の鍵 (訳のあるもの)。無ければ null
    private static readonly Regex TransitStationKey = new(@"^@?ui_interactor_ST2_transit_(\w+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? DistrictKey(string destKey, Func<string, string?>? localize)
    {
        if (localize == null || TransitStationKey.Match(destKey) is not { Success: true } m) return null;
        foreach (var k in new[] { $"ui_interactor_ST2_{m.Groups[1].Value}_platform", $"ui_interactor_ST2_{m.Groups[1].Value}" })
            if (localize(k) is { Length: > 0 }) return "@" + k;
        return null;
    }

    // 区画 (インスタンス) の名前 (利用者の決定、2026-11-01): 内部の名前 (ObjectContainer-…・ObjectContainerModifier-…・spawncloset…・static_elev… など、
    // Child の entityName) は使わず、ゲームの名前だけを付ける。候補 (上ほど先):
    //   ① 自分の星図の場所 (StarMapRecord が placeNames にある) → ② 区画の箱の中の店 (SCShop の Name。表示の側で game_shops の名前に直す)
    //   → ③ 区画の箱の中の interiormaplabel の @area_name_* → ④ 区画の箱の中の行き先 (階を表すもの・@LOC_UNINITIALIZED・@generic_*_floor を除く)
    //   → ⑤ 区画の箱の中の interiormapsection (@*_Floor_* を除く) → ⑥ 祖先のインスタンスの ① (ルートの場所 (都市・ステーションそのもの) は除く) と、祖先のインスタンスの ③〜⑤ (祖先に属する点。
    //   ルートのインスタンスの点は除く (2026-11-01、利用者の決定。ルートの点はステーション全体のもので、Seraphim の居住区の区画がいちばん近いルートの点 @cargodeck_01 の「Ext. Freight 01」になっていた))。
    // 箱の中は、区画のインスタンスの枠で minBounds・maxBounds の 0.5 m 内 (どのインスタンスの点でもよい)。同じ規則の中では箱の中心にいちばん近いもの。
    // 一覧 (Listed) には、名前の元 (点・店・場所) 1 つにつき、床の板のいちばん多い区画を 1 つだけ出す (base と rund のように重なる区画が同じ名前で並ばないように)。
    // どれも無い区画は名前が無く、一覧に出さない。地図の多角形と道順には使う
    public static void NameSections(StationTree tree, IReadOnlyList<StationSection> sections, Func<string, string?>? localize, IReadOnlyDictionary<string, string>? placeNames) =>
        NameSections(tree, sections, localize, placeNames, null);

    public static void NameSections(StationTree tree, IReadOnlyList<StationSection> sections, Func<string, string?>? localize, IReadOnlyDictionary<string, string>? placeNames,
        IReadOnlyDictionary<string, string>? shopNames)
    {
        placeNames = UsablePlaceNames(tree, placeNames);
        var named = tree.Points.Where(p => p.Pos != null && Usable(p.Name, localize) && (
            p.Kind == "entitycomponentinteriormaplabel" && AreaKey.IsMatch(p.Name)
            || p.Kind == "sctransitdestination" && !NoNameKeys.Contains(p.Name) && !DestLevelKey.IsMatch(p.Name)
            || p.Kind == "entitycomponentinteriormapsection" && !FloorKey.IsMatch(p.Name) && !NoNameKeys.Contains(p.Name))).ToList();
        int Rule(StationPoint p) => p.Kind switch { "entitycomponentinteriormaplabel" => 3, "sctransitdestination" => 4, _ => 5 };
        var byInst = named.GroupBy(p => p.Instance).ToDictionary(g => g.Key, g => g.ToList());
        var picks = new List<(StationSection Sec, string Origin)>();
        // 店・名札は、その点の持ち主の区画 (点のインスタンスか祖先のうちいちばん近い区画) が、この区画の子孫の区画なら候補にしない (2026-11-01)。
        // Orison の orison_comm_000 (Cloudview Center の島全体。箱は半径およそ 980 m) が、子孫の病院の中の店の名前 (Pharmacy) を取り、島全体が「Pharmacy」になっていた
        var secInst = sections.Select(x => x.Instance).ToHashSet();
        int Owner(int i) { for (; i >= 0; i = tree.Instances[i].Parent) if (secInst.Contains(i)) return i; return -1; }
        bool Allowed(int pointInst, (double X, double Y, double Z) q, int sec)
        {
            var o = Owner(pointInst);
            if (o < 0 || o == sec) return true;
            for (int i = tree.Instances[o].Parent; i >= 0; i = tree.Instances[i].Parent) if (i == sec) return false;
            return true;
        }
        foreach (var sec in sections)
        {
            var inst = tree.Instances[sec.Instance];
            var inv = inst.ToStation.Inverse();
            var a = inst.MinBounds!.Value; var b = inst.MaxBounds!.Value;
            bool InBox((double X, double Y, double Z) q)
            {
                var l = inv.Apply(q);
                return l.X >= a.X - 0.5 && l.X <= b.X + 0.5 && l.Y >= a.Y - 0.5 && l.Y <= b.Y + 0.5 && l.Z >= a.Z - 0.5 && l.Z <= b.Z + 0.5;
            }
            double D((double X, double Y, double Z) q) => Math.Pow(q.X - sec.Center.X, 2) + Math.Pow(q.Y - sec.Center.Y, 2) + Math.Pow(q.Z - sec.Center.Z, 2);
            string key = "", disp = "", src = "", origin = "";
            if (placeNames != null && inst.StarMapRecord.Length > 0 && placeNames.TryGetValue(inst.StarMapRecord, out var pn)) { key = disp = pn; src = "place"; origin = "place:" + sec.Instance; }
            else if (shopNames != null && tree.Shops.Where(x => InBox(x.Pos) && Allowed(x.Instance, x.Pos, sec.Instance) && ShopNameOf(tree, shopNames, x.Guid) is { Length: > 0 }).OrderBy(x => D(x.Pos)).FirstOrDefault() is { } sh)
            { key = disp = ShopNameOf(tree, shopNames, sh.Guid)!; src = "shop"; origin = $"shop:{sh.Instance}:{sh.Guid}"; }
            else if (named.Where(p => InBox(p.Pos!.Value) && Allowed(p.Instance, p.Pos!.Value, sec.Instance)).OrderBy(Rule).ThenBy(p => D(p.Pos!.Value)).FirstOrDefault() is { } np)
            { key = np.Name; src = Rule(np) switch { 3 => "area", 4 => "destination", _ => "section" }; origin = $"pt:{np.Instance}:{np.Guid}"; }
            else
            {
                for (int i = inst.Parent; i >= 0 && key.Length == 0; i = tree.Instances[i].Parent)
                {
                    var anc = tree.Instances[i];
                    if (placeNames != null && i > 0 && anc.StarMapRecord.Length > 0 && placeNames.TryGetValue(anc.StarMapRecord, out var apn)) { key = disp = apn; src = "parent"; origin = "place:" + i; }
                    else if (i > 0 && byInst.TryGetValue(i, out var al) && al.OrderBy(Rule).ThenBy(p => D(p.Pos!.Value)).FirstOrDefault() is { } ap) { key = ap.Name; src = "parent"; origin = $"pt:{ap.Instance}:{ap.Guid}"; }
                }
            }
            if (key.Length > 0 && disp.Length == 0) disp = Display(key, localize);
            sec.Name = key;
            sec.DisplayName = disp;
            // 名札 (Label) も、訳の引ける鍵だけを表示の名前にして入れる (鍵のまま出さない)
            sec.Label = sec.Label.Length > 0 && Usable(sec.Label, localize) ? Display(sec.Label, localize) : "";
            sec.NameSource = src;
            sec.Listed = false;
            if (key.Length > 0) picks.Add((sec, origin));
        }
        foreach (var g in picks.GroupBy(t => t.Origin))
            g.OrderByDescending(t => t.Sec.PlateCount).ThenBy(t => t.Sec.BoxFallback).ThenBy(t => t.Sec.Instance).First().Sec.Listed = true;
    }

    // 階段: ① メッシュ (IsStairMesh) をインスタンスごとに xy の箱が 1 m 以内で重なるものでまとめ、下の端と上の端の高さに近い (1.5 m 以内) 階が違えば階段。
    // ② 部屋 (entitycomponentroom) の名前 ent_rm_system_stair_*_bot・_top を同じインスタンスで組にする。③ socpak の名前に stair・shaft のあるインスタンスは、
    // 人が上り下りできると確かめられたものだけ箱の底と上の階を結ぶ: そのインスタンスか子孫に階段・スロープの板があり、かつ箱の底と上の端の両方の ModuleEndRadius (6 m) 以内に
    // 出入口の接続 (roomconnector) か乗り場がある。Checkmate の base_shaft_001 (28 個) は、中身が壁 (roomsm_lg_wall_*_12) と部屋 1 つだけで、
    // 階段・はしご・エレベーターのメッシュも entity も無く、両端の近くに接続も無いので、階段にしない (前は 11 本を階段にしていた)
    // 階段の端を床の板に寄せる範囲 (m)。寄せた端は床の縁にあるので、StairEndLink m 以内の経由点とは、線分が床の上かを見ずに結ぶ (AddWaypoints)。
    // Cloudview Center の橋の階段 (stair160) の上の端と Crusader Showroom の床のあいだは 2.7 m (あいだは橋 plate_trim_bridge_32m_c で、床の規則で拾えない)
    public const double StairEndSnap = 3.0;
    public const double StairEndLink = 4.0;
    // noEnd: 端の階が見つからず階段にしなかったまとまり (浮いたもの。StationFloorBuilder.FloatingItems) を入れる先 (null なら入れない)
    public static List<StationStair> Stairs(StationTree tree, IReadOnlyList<StationPlate> plates, IReadOnlyList<StationFloor> floors, List<StationFloating>? noEnd = null)
    {
        var res = new List<StationStair>();
        var floorPlates = plates.Where(p => !p.Stair).GroupBy(p => StationFloorBuilder.PlateFloor(p, floors)?.Id ?? "").Where(x => x.Key.Length > 0).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var g in plates.Where(p => p.Stair).GroupBy(StationFloorBuilder.StairKey))
        {
            var list = g.ToList();
            var parent = Enumerable.Range(0, list.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var boxes = list.Select(p => (MinX: p.Hull.Min(q => q.X), MaxX: p.Hull.Max(q => q.X), MinY: p.Hull.Min(q => q.Y), MaxY: p.Hull.Max(q => q.Y))).ToList();
            // (2026-11-01) まとめ方は StationFloorBuilder.StairClusters と同じ (xy の箱が JoinGap 以内で、高さの範囲が JoinRise 以内で続くもの)。
            // 前は xy だけでまとめていて、Checkmate の精錬所の階段 (util_stairs_a_start_b・util_stairs_a_end_c、-460.34〜-456.87 m) に、4 m 上の傾いた床の板
            // roomsm_floor_straight_04x02x0025_g (-454.59〜-453.95 m) が入り、上の端の階が f54 (-458 m) でなく -454.5 m の階になっていた
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                    if (boxes[i].MinX - StationFloorBuilder.JoinGap <= boxes[j].MaxX && boxes[j].MinX - StationFloorBuilder.JoinGap <= boxes[i].MaxX
                        && boxes[i].MinY - StationFloorBuilder.JoinGap <= boxes[j].MaxY && boxes[j].MinY - StationFloorBuilder.JoinGap <= boxes[i].MaxY
                        && list[i].Bottom <= list[j].Top + StationFloorBuilder.JoinRise && list[j].Bottom <= list[i].Top + StationFloorBuilder.JoinRise)
                        parent[Find(i)] = Find(j);
            foreach (var c in Enumerable.Range(0, list.Count).GroupBy(Find))
            {
                var ps = c.Select(i => list[i]).ToList();
                var lo = ps.MinBy(p => p.Bottom)!; var hi = ps.MaxBy(p => p.Top)!;
                IReadOnlyList<StationFloor> own = lo.Group >= 0 ? floors.Where(f => f.Group == $"g{lo.Group}").ToList() : floors;
                // 階の中の道として階に合わせた階段 (StationFloorBuilder.Levels の PlateSet) は印を作らない
                if (ps.All(p => own.Any(f => f.PlateSet != null && f.PlateSet.Contains(p)))) continue;
                // 端の階: 階の板の組 (PlateSet) のある階は、階段のそば (StairEndSnap 以内) に上面が端の高さ ± 1.5 m の床の板がある階のうち、高さのいちばん近いもの
                StationFloor? EndFloor(double z)
                {
                    if (!own.Any(f => f.PlateSet != null)) return StationFloorBuilder.NearestFloor(own, z, 1.5);
                    StationFloor? best = null; double bd = 1.5;
                    double bx0 = ps.Min(q => q.Hull.Min(h => h.X)) - StairEndSnap, bx1 = ps.Max(q => q.Hull.Max(h => h.X)) + StairEndSnap;
                    double by0 = ps.Min(q => q.Hull.Min(h => h.Y)) - StairEndSnap, by1 = ps.Max(q => q.Hull.Max(h => h.Y)) + StairEndSnap;
                    foreach (var f in own)
                        if (floorPlates.TryGetValue(f.Id, out var fl))
                            foreach (var fp in fl)
                            {
                                var d = Math.Abs(fp.Top - z);
                                if (d <= bd && fp.Hull.Max(h => h.X) >= bx0 && fp.Hull.Min(h => h.X) <= bx1 && fp.Hull.Max(h => h.Y) >= by0 && fp.Hull.Min(h => h.Y) <= by1 && ps.Any(sp => StationFloorBuilder.HullGap(sp.Hull, fp.Hull) <= StairEndSnap)) { bd = d; best = f; }
                            }
                    return best;
                }
                // (2026-11-02、利用者の原則「すき間の無い高低差が 0.5m 未満の続きの面」「"つながってる" を軽視してるんじゃないのか」)
                // 端の階は、先に、階段の板に接する床の板 (HullGap が JoinGap 以内で、上面が階段の板の Bottom − SurfaceRise 〜 Top + SurfaceRise) のうち、
                // いちばん低いものの階 (下の端) といちばん高いものの階 (上の端)。2 つの階が無ければ前と同じ探し方 (EndFloor。まとまりの下の端・上の端の高さ ± 1.5 m)。
                // 踊り場でつないだまとまりに、となりの階段の板が入り、まとまりの下の端が床の 2 m 下になって EndFloor で端の階が見つからず、階段ごと消えていた
                // (fin45 の Area18 の Arccorp Tower の屋上の階段 rooftop_bg_small_01: まとまり 371.72〜381.60 m、接する床 373.75・381.25 m)
                StationFloor? fb = null, ft = null;
                if (own.Any(f => f.PlateSet != null))
                {
                    double tlo = double.MaxValue, thi = double.MinValue;
                    foreach (var f in own)
                        if (floorPlates.TryGetValue(f.Id, out var fl))
                            foreach (var fp in fl)
                                if (ps.Any(sp => fp.Top >= sp.Bottom - StationFloorBuilder.SurfaceRise && fp.Top <= sp.Top + StationFloorBuilder.SurfaceRise
                                    && StationFloorBuilder.HullGap(sp.Hull, fp.Hull) <= StationFloorBuilder.JoinGap))
                                {
                                    if (fp.Top < tlo) { tlo = fp.Top; fb = f; }
                                    if (fp.Top > thi) { thi = fp.Top; ft = f; }
                                }
                }
                if (fb == null || ft == null || fb == ft) { fb = EndFloor(lo.Bottom); ft = EndFloor(hi.Top); }
                if (fb == null || ft == null || fb == ft)
                {
                    noEnd?.Add(new StationFloating(lo.Mesh, lo.Ramp ? "ramp" : "stair", lo.Hull.Average(q => q.X), lo.Hull.Average(q => q.Y), lo.Bottom,
                        StationFloorBuilder.StairNoEndReason + $" (下の端 {(fb == null ? "なし" : fb.Id)}・上の端 {(ft == null ? "なし" : ft.Id)}、{lo.Bottom:F2}〜{hi.Top:F2} m)"));
                    continue;
                }
                // 端の位置 (2026-11-01): 階段の凸包の頂点のうち、その階の床の板 (階段でない板) にいちばん近いもの (2 m 以内) を、階段の中心へ 0.5 m 寄せた点。
                // 無ければ前と同じく板の中心。Cloudview Center の広場と Crusader Showroom のあいだの橋の階段 (base_08x06x02_bridge_stairs_b) は、
                // 中心が床の外で、床の経由点と結べず、Showroom の側が切れていた
                var cx = ps.Average(p => p.Hull.Average(q => q.X)); var cy = ps.Average(p => p.Hull.Average(q => q.Y));
                (double X, double Y) End(StationPlate fallback, StationFloor f)
                {
                    var near = floorPlates.TryGetValue(f.Id, out var fl) ? fl : null;
                    var best = (X: fallback.Hull.Average(q => q.X), Y: fallback.Hull.Average(q => q.Y)); var bd = StairEndSnap;
                    if (near != null)
                        foreach (var v in ps.SelectMany(p => p.Hull))
                            foreach (var fp in near)
                            {
                                var d = StationFloorBuilder.DistToConvex(fp.Hull, v.X, v.Y);
                                if (d < bd) { bd = d; var len = Math.Sqrt((cx - v.X) * (cx - v.X) + (cy - v.Y) * (cy - v.Y)); var k = len > 0.5 ? 0.5 / len : 0; best = (v.X + (cx - v.X) * k, v.Y + (cy - v.Y) * k); }
                            }
                    return best;
                }
                var eb = End(lo, fb); var et = End(hi, ft);
                // 踊り場でつないだ階段 (StationFloorBuilder.Landings) は、いちばん下の板のインスタンスを使う (鍵は負の数)
                var stInst = g.Key >= 0 ? g.Key : lo.Instance;
                var landing = ps.Any(p => p.Landing);
                var src = ps.All(p => p.Ramp) ? "ramp" : "mesh";
                res.Add(new StationStair((eb.X, eb.Y, fb.Z), (et.X, et.Y, ft.Z),
                    fb.Id, ft.Id, src, stInst, ps.Where(p => p.Hull.Length >= 3).Select(p => p.Hull).ToList() is { Count: > 0 } ol ? ol : null, landing));
                // (2026-11-02) 端の階の床の板が、階段の別の所 (端の点から StairEndLink より離れた所) にも接していれば、そこへも結ぶ (形 outline は書かない)。
                // 同じ階の床が階段の両脇に分かれているとき、片方にしか端の点が無く、もう片方 (店のある側) が道でつながらなかった
                // (ARC_LEO の Galleria 77.8 m: 階段の上の端が x −1024 の側だけになり、Admin と Bar のある x −1015 の側が乗り場からたどれなかった)
                foreach (var (endF, endPt, other, otherF, isTop) in new[] { (ft, et, eb, fb, true), (fb, eb, et, ft, false) })
                {
                    if (!floorPlates.TryGetValue(endF.Id, out var efl)) continue;
                    var added = new List<(double X, double Y)> { endPt };
                    foreach (var fp in efl)
                    {
                        if (Math.Abs(fp.Top - endF.Z) > StationFloorBuilder.SurfaceRise && !(fp.Top >= endF.ZMin && fp.Top <= endF.ZMax)) continue;
                        var sp = ps.Where(q => fp.Top >= q.Bottom - StationFloorBuilder.SurfaceRise && fp.Top <= q.Top + StationFloorBuilder.SurfaceRise
                            && StationFloorBuilder.HullGap(q.Hull, fp.Hull) <= StationFloorBuilder.JoinGap).FirstOrDefault();
                        if (sp == null) continue;
                        var v = sp.Hull.MinBy(h => StationFloorBuilder.DistToConvex(fp.Hull, h.X, h.Y));
                        if (added.Any(a => Math.Sqrt((a.X - v.X) * (a.X - v.X) + (a.Y - v.Y) * (a.Y - v.Y)) <= StairEndLink)) continue;
                        added.Add(v);
                        res.Add(isTop ? new StationStair((other.X, other.Y, otherF.Z), (v.X, v.Y, endF.Z), otherF.Id, endF.Id, src, stInst, null, landing)
                                      : new StationStair((v.X, v.Y, endF.Z), (other.X, other.Y, otherF.Z), endF.Id, otherF.Id, src, stInst, null, landing));
                    }
                }
                // (2026-11-02、親の決定 d) 階段の途中に接する階も、その階段で結ぶ: 端の階でない階の床の板で、上面が階段の下の端 + JoinRise より上・上の端 − JoinRise より下にあり、
                // 階段の板 (上面か底が板の上面 ± JoinRise のもの) から JoinGap 以内のもの。その階と下の端・上の端を、それぞれ結ぶ (形 outline は書かない。形は上の 1 本に書く)。
                // Checkmate の Floor 13 (傾いた格子の通路の途中に接する) と、踊り場に接する ADMIN の部屋の床がこれでつながる
                foreach (var f in own)
                {
                    if (f == fb || f == ft || !floorPlates.TryGetValue(f.Id, out var fl)) continue;
                    StationPlate? bestSp = null; StationPlate? bestFp = null; double bg = StationFloorBuilder.JoinGap;
                    foreach (var fp in fl)
                    {
                        if (fp.Top <= lo.Bottom + StationFloorBuilder.JoinRise || fp.Top >= hi.Top - StationFloorBuilder.JoinRise) continue;
                        foreach (var sp in ps)
                        {
                            if (Math.Abs(sp.Top - fp.Top) > StationFloorBuilder.JoinRise && Math.Abs(sp.Bottom - fp.Top) > StationFloorBuilder.JoinRise) continue;
                            var d = StationFloorBuilder.HullGap(sp.Hull, fp.Hull);
                            if (d <= bg) { bg = d; bestSp = sp; bestFp = fp; }
                        }
                    }
                    if (bestSp == null || bestFp == null) continue;
                    // 途中の点: 階段の板の凸包の頂点のうち、その床の板にいちばん近いものを、板の中心へ 0.5 m 寄せた点
                    double sx = bestSp.Hull.Average(q => q.X), sy = bestSp.Hull.Average(q => q.Y);
                    var v = bestSp.Hull.MinBy(h => StationFloorBuilder.DistToConvex(bestFp.Hull, h.X, h.Y));
                    var vl = Math.Sqrt((sx - v.X) * (sx - v.X) + (sy - v.Y) * (sy - v.Y)); var vk = vl > 0.5 ? 0.5 / vl : 0;
                    var mid = (v.X + (sx - v.X) * vk, v.Y + (sy - v.Y) * vk, f.Z);
                    res.Add(new StationStair((eb.X, eb.Y, fb.Z), mid, fb.Id, f.Id, src, stInst, null, landing));
                    res.Add(new StationStair(mid, (et.X, et.Y, ft.Z), f.Id, ft.Id, src, stInst, null, landing));
                }
            }
        }
        // 乗り場の縁 (StationFloorBuilder.IsBalcony) と、同じまとまりのいちばん近い床の板 (PlatformLinkGap 以内) を結ぶ (source "platform")。
        // あいだの段・床がメッシュの名前の規則で拾えていないため (StationFloorBuilder.AssignGroups の説明)。端は縁の中心と、床の板の上の縁の中心にいちばん近い点
        foreach (var b in plates.Where(p => !p.Stair && StationFloorBuilder.IsBalcony(p.Mesh)))
        {
            var near = plates.Where(q => q.Group == b.Group && !q.Stair && !StationFloorBuilder.IsBalcony(q.Mesh) && Math.Abs(q.Top - b.Top) <= StationFloorBuilder.PlatformLinkRise)
                .Select(q => (q, d: StationFloorBuilder.HullGap(b.Hull, q.Hull))).Where(t => t.d <= StationFloorBuilder.PlatformLinkGap).OrderBy(t => t.d).Select(t => t.q).FirstOrDefault();
            if (near == null) continue;
            double cx = b.Hull.Average(q => q.X), cy = b.Hull.Average(q => q.Y);
            var cp = StationFloorBuilder.ClosestOnConvex(near.Hull, cx, cy);
            var fb = StationFloorBuilder.PlateFloor(b, floors); var ft = StationFloorBuilder.PlateFloor(near, floors);
            if (fb == null || ft == null) continue;
            res.Add(new StationStair((cx, cy, b.Top), (cp.X, cp.Y, near.Top), fb.Id, ft.Id, "platform", b.Instance));
        }
        var rx = new Regex(@"^(ent_rm_system_stair_.*)_(bot|top)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var g in tree.Points.Where(p => p.Kind == "entitycomponentroom" && p.Pos != null && rx.IsMatch(p.Name)).GroupBy(p => (p.Instance, rx.Match(p.Name).Groups[1].Value.ToLowerInvariant())))
        {
            var bot = g.FirstOrDefault(p => p.Name.EndsWith("_bot", StringComparison.OrdinalIgnoreCase));
            var top = g.FirstOrDefault(p => p.Name.EndsWith("_top", StringComparison.OrdinalIgnoreCase));
            if (bot == null || top == null) continue;
            var fb = StationFloorBuilder.FloorAtXY(floors, bot.Pos!.Value.X, bot.Pos!.Value.Y, bot.Pos!.Value.Z); var ft = StationFloorBuilder.FloorAtXY(floors, top.Pos!.Value.X, top.Pos!.Value.Y, top.Pos!.Value.Z);
            if (fb == null || ft == null || fb == ft) continue;
            res.Add(new StationStair(bot.Pos!.Value, top.Pos!.Value, fb.Id, ft.Id, "room", g.Key.Instance));
        }
        var stairInst = plates.Where(p => p.Stair).Select(p => p.Instance).ToHashSet();
        var doors = tree.Points.Where(p => p.Pos != null && p.Kind is "entitycomponentroomconnector" or "sctransitgateway").Select(p => p.Pos!.Value).ToList();
        foreach (var inst in tree.Instances)
        {
            var stem = StationFloorBuilder.Stem(inst.Socpak);
            if (!(stem.Contains("stair", StringComparison.OrdinalIgnoreCase) || stem.Contains("shaft", StringComparison.OrdinalIgnoreCase))) continue;
            if (inst.MinBounds is not { } a || inst.MaxBounds is not { } b) continue;
            var lo = inst.ToStation.Apply(((a.X + b.X) / 2, (a.Y + b.Y) / 2, a.Z));
            var hi = inst.ToStation.Apply(((a.X + b.X) / 2, (a.Y + b.Y) / 2, b.Z));
            if (lo.Z > hi.Z) (lo, hi) = (hi, lo);
            var fb = StationFloorBuilder.FloorAtXY(floors, lo.X, lo.Y, lo.Z + 0.5); var ft = StationFloorBuilder.FloorAtXY(floors, hi.X, hi.Y, hi.Z);
            if (fb == null || ft == null || fb == ft) continue;
            var sub = new HashSet<int> { inst.Index };
            for (bool grew = true; grew;) { grew = false; foreach (var c in tree.Instances) if (c.Parent >= 0 && sub.Contains(c.Parent) && sub.Add(c.Index)) grew = true; }
            if (!stairInst.Overlaps(sub)) continue;
            bool Door((double X, double Y, double Z) q) => doors.Any(d => Dist(d, q) <= ModuleEndRadius);
            if (!Door(lo) || !Door(hi)) continue;
            res.Add(new StationStair((lo.X, lo.Y, fb.Z), (hi.X, hi.Y, ft.Z), fb.Id, ft.Id, "module", inst.Index));
        }
        return res;
    }

    // 路線: 乗り場のタグのうち、sctransitmanager の PositiveTags にあるものを路線のキーにする。
    // 路線を管理する sctransitmanager の無い乗り場は道順に使わない (Excluded に入れる)。決めた理由: Orison の Spaceport_Tourism (30671a0a。乗り場 44c5b464
    // (orison_sp_transit) と 40cdbb86 (orison_comm_000)) は管理の PositiveTags に無く、道筋 (navspline) も無い。利用者の情報 (2026-11-01) で、Orison の
    // シャトルは A 回り・B 回りの 2 路線だけで、この路線は人が乗って移動できる路線ではないと決まった。Checkmate の乗り場 89 個は全部、管理のある路線に入る。
    // タグの無い乗り場は、祖先 (自分を含む) のうち PositiveTags の無い sctransitmanager を持ついちばん近いインスタンスごとにまとめる (推測。Orison の建物のエレベーター用。
    // 確かめていない)。そのような祖先も無ければ、管理の無い乗り場として使わない。
    // 回る路線: その路線のタグを持つ sctransitnavsplinedata の道筋の始まり・終わりが SplineSnap 以内の乗り場どうしを、向きつきで結ぶ
    public sealed class Line
    {
        public required string Key { get; init; }
        public List<(int Instance, StationPoint Gateway)> Gateways { get; } = new();
        public List<(int FromGw, int ToGw, string Dir, string Spline)> Legs { get; } = new();
        public bool Tram => Legs.Count > 0;
        // 乗らないエレベーター (利用者の決定 2026-11-01: 病院・居住区 (リスポン)・ハンガーのエレベーターは、乗り場の位置は出すが、行き先の階は作らず、道順で乗らない)
        public bool NoRide { get; set; }
    }

    // 点から折れ線 (道筋) までのいちばん近い距離
    private static double PolylineDist(IReadOnlyList<(double X, double Y, double Z)> path, (double X, double Y, double Z) q)
    {
        double best = double.MaxValue;
        for (int k = 0; k + 1 < path.Count; k++)
        {
            var p0 = path[k]; var p1 = path[k + 1];
            double vx = p1.X - p0.X, vy = p1.Y - p0.Y, vz = p1.Z - p0.Z, len2 = vx * vx + vy * vy + vz * vz;
            var u = len2 > 0 ? Math.Clamp(((q.X - p0.X) * vx + (q.Y - p0.Y) * vy + (q.Z - p0.Z) * vz) / len2, 0, 1) : 0;
            best = Math.Min(best, Dist((p0.X + u * vx, p0.Y + u * vy, p0.Z + u * vz), q));
        }
        return best;
    }

    public static List<Line> TransitLines(StationTree tree) => TransitLines(tree, out _);

    // excluded: 管理の無い乗り場 (キーは乗り場のタグ、タグが無ければ "inst:<インスタンス>")
    public static List<Line> TransitLines(StationTree tree, out List<Line> excludedLines)
    {
        var excluded = new Dictionary<string, Line>(StringComparer.Ordinal);
        var mgrTags = tree.Points.Where(p => p.Kind == "sctransitmanager").SelectMany(p => p.Tags).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untaggedMgr = tree.Points.Where(p => p.Kind == "sctransitmanager" && p.Tags.Count == 0).Select(p => p.Instance).ToHashSet();
        string? AncestorKey(int inst)
        {
            for (int i = inst; i >= 0; i = tree.Instances[i].Parent)
                if (untaggedMgr.Contains(i)) return "inst:" + i;
            return null;
        }
        var lines = new Dictionary<string, Line>(StringComparer.Ordinal);
        var periph = tree.Points.Where(p => p.Kind == "sctransitperipheral" && p.Pos != null).Select(p => p.Pos!.Value).ToList();
        bool HasPanel((double X, double Y, double Z) q) => periph.Any(p => Math.Abs(p.X - q.X) <= PeripheralRadius && Math.Abs(p.Y - q.Y) <= PeripheralRadius && Math.Abs(p.Z - q.Z) <= PeripheralRadius);
        var periphByInst = tree.Points.Where(p => p.Kind == "sctransitperipheral" && p.Pos != null).GroupBy(p => p.Instance).ToDictionary(g => g.Key, g => g.Select(p => p.Pos!.Value).ToList());
        // 同じインスタンスの装置で見るのは、3 つ以上の乗り場の近く (SplineSnap 以内) を通る道筋のある路線の乗り場だけ (駅を回る形の路面電車)。
        // New Babbage の TransitNavSpline-004・005 と Lorville の PERI_Line_East・West がこの形。駅から駅への道筋 (Orison の Looping_A_・B_、
        // Station_Internal (aea96340) の道筋) の路線は前のまま (Orison の voyager_bar_orison の乗り場 (aea96340) は同じインスタンスの装置まで 3 m 以内だが、外したまま)。
        // 道筋のタグのうち、路線の管理のタグ (mgrTags) になっているものだけを見る (どの道筋にも付く共通のタグ 00ead468… は路線ではない)
        var allGw = tree.Points.Where(p => p.Kind == "sctransitgateway" && p.Pos != null).ToList();
        var splineTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in tree.Points.Where(p => p.Kind == "sctransitnavsplinedata" && p.Path.Count >= 2))
            foreach (var t in s.Tags.Where(mgrTags.Contains))
            {
                var near = allGw.Count(g => g.Tags.Contains(t, StringComparer.OrdinalIgnoreCase) && PolylineDist(s.Path, g.Pos!.Value) <= SplineSnap);
                if (near >= 3) splineTags.Add(t);
            }
        bool HasOwnPanel(int inst, (double X, double Y, double Z) q) => periphByInst.TryGetValue(inst, out var own)
            && own.Any(p => Math.Abs(p.X - q.X) <= SameInstancePeripheralRadius && Math.Abs(p.Y - q.Y) <= SameInstancePeripheralRadius && Math.Abs(p.Z - q.Z) <= SameInstancePeripheralRadius);
        foreach (var g in tree.Points.Where(p => p.Kind == "sctransitgateway" && p.Pos != null))
        {
            // 乗り場の装置 (sctransitperipheral。扉・操作盤など) が PeripheralRadius (1.5 m) 以内に無い乗り場は使わない (Excluded に "noperipheral:<タグ>" で入れる)。
            // Checkmate の乗り場 89 個は全部ある。Orison で無いのは 26 個: orison_all の直下 24 個 (タグも路線の管理の PositiveTags も無く、全部が 1 つのエレベーターに
            // まとまっていた)・Station_Internal (aea96340) の orison_arcade_int_001 と voyager_bar_orison の 2 個
            // 新しい乗り物の仕組み (sctransport*) の乗り場は、データの結び付き (乗り場の DNF のタグを持つ同じインスタンスの操作盤。StationTree.LinkTransportSystem) で装置ありとみなす
            var linked = g.Attrs.TryGetValue("panelLinked", out var pl) && pl == "1";
            if (!linked && !HasPanel(g.Pos!.Value) && !(g.Tags.Any(splineTags.Contains) && HasOwnPanel(g.Instance, g.Pos!.Value)))
            {
                var nk = "noperipheral:" + (g.Tags.Count > 0 ? string.Join(",", g.Tags.Select(t => t.ToLowerInvariant())) : "inst:" + g.Instance);
                if (!excluded.TryGetValue(nk, out var nl)) excluded[nk] = nl = new Line { Key = nk };
                nl.Gateways.Add((g.Instance, g));
                continue;
            }
            var keys = g.Tags.Where(mgrTags.Contains).Where(t => !IsExcludedLine(tree, t)).ToList();
            if (keys.Count == 0 && g.Tags.Any(t => IsExcludedLine(tree, t)))
            {
                var xk = "excluded:" + string.Join(",", g.Tags.Select(t => t.ToLowerInvariant()));
                if (!excluded.TryGetValue(xk, out var xl)) excluded[xk] = xl = new Line { Key = xk };
                xl.Gateways.Add((g.Instance, g));
                continue;
            }
            if (keys.Count == 0 && g.Tags.Count == 0 && AncestorKey(g.Instance) is { } ak) keys.Add(ak);
            if (keys.Count == 0)
            {
                var ek = g.Tags.Count > 0 ? string.Join(",", g.Tags.Select(t => t.ToLowerInvariant())) : "inst:" + g.Instance;
                if (!excluded.TryGetValue(ek, out var el)) excluded[ek] = el = new Line { Key = ek };
                el.Gateways.Add((g.Instance, g));
                continue;
            }
            foreach (var k in keys.Select(k => k.ToLowerInvariant()))
            {
                if (!lines.TryGetValue(k, out var l)) lines[k] = l = new Line { Key = k };
                l.Gateways.Add((g.Instance, g));
            }
        }
        var dirRx = new Regex(@"Looping_([A-Za-z0-9]+)_", RegexOptions.CultureInvariant);
        foreach (var s in tree.Points.Where(p => p.Kind == "sctransitnavsplinedata" && p.Path.Count >= 2))
            foreach (var t in s.Tags.Select(t => t.ToLowerInvariant()))
            {
                if (!lines.TryGetValue(t, out var l)) continue;
                int Near((double X, double Y, double Z) q)
                {
                    int bi = -1; double bd = SplineSnap;
                    for (int i = 0; i < l.Gateways.Count; i++) { var d = Dist(l.Gateways[i].Gateway.Pos!.Value, q); if (d <= bd) { bd = d; bi = i; } }
                    return bi;
                }
                int a = Near(s.Path[0]), b = Near(s.Path[^1]);
                var m = dirRx.Match(s.Name);
                if (a >= 0 && b >= 0 && a != b) { l.Legs.Add((a, b, m.Success ? m.Groups[1].Value : "", s.Name)); continue; }
                // (2026-11-01) 道筋の両端が乗り場に無いときは、道筋が乗り場の近く (SplineSnap 以内) を通る順に、隣どうしの乗り場を結ぶ。
                // New Babbage の路面電車 (TransitNavSpline-004・005、点 183・198) と Lorville の環状線 (PERI_Line_East・West_TransitNavSpline_T1・T2、点 380〜440) は、
                // 1 本の道筋が全部の駅を回る形で、Orison の駅から駅への道筋 (Looping_A_・B_) と違い、端が乗り場に無かった。
                // 道筋の始めと終わりが SplineSnap 以内なら (閉じた環)、最後の駅から最初の駅へも結ぶ
                var along = new List<(double T, int Gw)>();
                for (int gi = 0; gi < l.Gateways.Count; gi++)
                {
                    var q = l.Gateways[gi].Gateway.Pos!.Value;
                    double bestD = SplineSnap, bestT = -1;
                    for (int k = 0; k + 1 < s.Path.Count; k++)
                    {
                        var p0 = s.Path[k]; var p1 = s.Path[k + 1];
                        double vx = p1.X - p0.X, vy = p1.Y - p0.Y, vz = p1.Z - p0.Z, len2 = vx * vx + vy * vy + vz * vz;
                        var u = len2 > 0 ? Math.Clamp(((q.X - p0.X) * vx + (q.Y - p0.Y) * vy + (q.Z - p0.Z) * vz) / len2, 0, 1) : 0;
                        var d = Dist((p0.X + u * vx, p0.Y + u * vy, p0.Z + u * vz), q);
                        if (d <= bestD) { bestD = d; bestT = k + u; }
                    }
                    if (bestT >= 0) along.Add((bestT, gi));
                }
                // 同じ場所の乗り場 (1 つの駅の両側など) は、道筋の上で続いて並ぶ。隣どうしで乗り場が違えば結ぶ
                var seq = along.OrderBy(x => x.T).ThenBy(x => x.Gw).Select(x => x.Gw).ToList();
                for (int k = 0; k + 1 < seq.Count; k++)
                    if (seq[k] != seq[k + 1]) l.Legs.Add((seq[k], seq[k + 1], m.Success ? m.Groups[1].Value : "", s.Name));
                if (seq.Count >= 2 && Dist(s.Path[0], s.Path[^1]) <= SplineSnap && seq[^1] != seq[0])
                    l.Legs.Add((seq[^1], seq[0], m.Success ? m.Groups[1].Value : "", s.Name));
            }
            // (2026-11-01 にやめたもの) socpak の名前 (hab・clinic・hangar など) で決め打ちして乗らない路線にしていた。
            // いまは、乗り場ごとに実装されているかをデータで判定する (Build の ImplementedGateways)
        excludedLines = excluded.Values.ToList();
        return lines.Values.ToList();
    }

    // 乗り場・行き先の一覧 (JSON の transit)
    public static List<StationTransit> TransitList(StationTree tree, IReadOnlyList<Line> lines, IReadOnlyList<StationFloor> floors)
    {
        var res = new List<StationTransit>();
        var dests = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
        // 行き先の点の階は DestFloor (NameFloors の規則 3・4、StackOrder と同じ)
        foreach (var l in lines)
            foreach (var (inst, g) in l.Gateways)
                res.Add(new StationTransit(l.Tram ? "tram" : l.NoRide ? "elevator-noride" : "elevator", inst, g.Guid, g.Tags, g.Pos!.Value,
                    (StationFloorBuilder.FloorAtXY(floors, g.Pos!.Value.X, g.Pos!.Value.Y, g.Pos!.Value.Z) ?? GatewayFloorByBounds(floors, g.Pos!.Value))?.Id ?? "", l.Key, StopName(dests, l.Key, g.Pos!.Value)));
        foreach (var d in dests)
            // 行き先 − 1.5 m で階が無ければ行き先の高さでも探す (NameFloors の規則 3・4 と同じ。2026-11-02: 床の灯りの直しの後、New Babbage の宿泊棟の行き先
            // @ui_interactor_floor_02〜10 (床の 0.5 m 上) が、灯りの偽の床 (行き先の 3.25 m 下) に乗っていたのが外れ、階が空になっていた)
            res.Add(new StationTransit("destination", d.Instance, d.Guid, d.Tags, d.Pos!.Value,
                DestFloor(floors, d.Pos!.Value)?.Id ?? "",
                d.Tags.FirstOrDefault()?.ToLowerInvariant() ?? "", d.Name));
        return res;
    }

    // 乗り場のいちばん近い (60 m 以内) 同じ路線のタグを持つ行き先の名前。無ければ ""
    private static string StopName(List<StationPoint> dests, string line, (double X, double Y, double Z) pos)
    {
        // タグの無い路線 ("inst:<番号>") は、タグの無い行き先 (@LOC_UNINITIALIZED・@generic_*_floor を除く) から引く
        // (Orison の Crusader Showroom の入口のエレベーター。行き先 @ui_interactor_ST2_CrusaderShowroom・@ui_interactor_generic_foyer・@ui_interactor_ST2_transit_SoO にタグが無い)
        var untagged = line.StartsWith("inst:", StringComparison.Ordinal);
        var d = dests.Where(x => (untagged ? x.Tags.Count == 0 && !NoNameKeys.Contains(x.Name) : x.Tags.Any(t => string.Equals(t, line, StringComparison.OrdinalIgnoreCase))) && x.Name.Length > 0)
            .Select(x => (x, d: Dist(x.Pos!.Value, pos))).Where(t => t.d <= 60).OrderBy(t => t.d).FirstOrDefault();
        return d.x?.Name ?? "";
    }

    // 道順のグラフを作る
    // floorCells (StationFloorBuilder.FloorCells) があれば、区画をまたぐ徒歩の辺を「同じ階で FloorLinkLength m 以内、線分の FloorLinkRatio 以上が床の上」でも作る
    // 直前の Build の段ごとの時間 (秒。確かめ用。結果には使わない)
    public static readonly Dictionary<string, double> LastBuildSeconds = new(StringComparer.Ordinal);
    public static StationGraph Build(StationTree tree, IReadOnlyList<StationFloor> floors, IReadOnlyList<StationSection> sections,
        IReadOnlyList<StationStair> stairs, IReadOnlyList<Line> lines, Dictionary<string, HashSet<(int I, int J)>>? floorCells = null,
        IReadOnlyList<StationPlate>? plates = null)
    {
        var g = new StationGraph();
        LastBuildSeconds.Clear(); var buildSw = System.Diagnostics.Stopwatch.StartNew(); double buildPrev = 0;
        void BuildLap(string name) { var t = buildSw.Elapsed.TotalSeconds; LastBuildSeconds[name] = t - buildPrev; buildPrev = t; }
        // ルート (0 番) と箱が 2 km を超える区画は、床の形を描くためだけの区画 (StationFloorBuilder.Sections) なので、道順のグラフでは前と同じく区画として使わない
        sections = sections.Where(s => s.Instance != 0 && s.Half.X <= 1000 && s.Half.Y <= 1000 && s.Half.Z <= 1000).ToList();
        var secInv = sections.ToDictionary(s => s.Instance, s => tree.Instances[s.Instance].ToStation.Inverse());
        var secByInst = sections.ToDictionary(s => s.Instance);
        var parentOf = tree.Instances.Select(i => i.Parent).ToArray();
        // 節点の区画: 自分か祖先のインスタンスが区画ならそれ。無ければ箱に入るいちばん小さい区画
        int SectionOf(int inst, (double X, double Y, double Z) p)
        {
            for (int i = inst; i >= 0; i = parentOf[i]) if (secByInst.ContainsKey(i)) return i;
            int best = -1; double bv = double.MaxValue;
            foreach (var s in sections)
            {
                var ins = tree.Instances[s.Instance];
                var q = secInv[s.Instance].Apply(p);
                var a = ins.MinBounds!.Value; var b = ins.MaxBounds!.Value;
                if (q.X < a.X || q.X > b.X || q.Y < a.Y || q.Y > b.Y || q.Z < a.Z || q.Z > b.Z) continue;
                var v = (b.X - a.X) * (b.Y - a.Y) * (b.Z - a.Z);
                if (v < bv) { bv = v; best = s.Instance; }
            }
            return best;
        }
        // エレベーター (トラムでない路線。乗らないものも含む) の乗り場 (インスタンス, CryGUID)
        var elevatorGw = lines.Where(l => !l.Tram).SelectMany(l => l.Gateways.Select(x => (x.Instance, x.Gateway.Guid))).ToHashSet();
        // floorId があればその階 (階段の端は階段の From・To の階)。無ければ NodeFloor
        int AddNode(string kind, int inst, string guid, string name, (double X, double Y, double Z) pos, string idSuffix = "", string? floorId = null)
        {
            var fid = floorId ?? (kind == "gateway" && elevatorGw.Contains((inst, guid)) ? ElevatorGatewayFloor(floors, floorCells, pos)?.Id : null)
                ?? NodeFloor(floors, floorCells, pos)?.Id ?? (kind == "gateway" ? GatewayFloor(floors, floorCells, pos)?.Id : null) ?? "";
            return g.Add(new StationNode($"{kind[0]}:{inst}:{guid}{idSuffix}", kind, pos, fid, SectionOf(inst, pos), name, inst, guid));
        }
        var pointNode = new Dictionary<(int, string), int>();
        foreach (var p in tree.Points)
        {
            if (p.Pos is not { } pos) continue;
            var kind = p.Kind switch { "entitycomponentroom" => "room", "entitycomponentroomconnector" => "connector", "sctransitgateway" => "gateway", _ => "" };
            if (kind.Length == 0) continue;
            pointNode[(p.Instance, p.Guid)] = AddNode(kind, p.Instance, p.Guid, p.Name, pos);
        }
        foreach (var s in tree.Shops) AddNode("shop", s.Instance, s.Guid, s.EntityName, s.Pos);
        for (int i = 0; i < stairs.Count; i++)
        {
            var st = stairs[i];
            int a = AddNode("stair", st.Instance, $"stair{i}", "bot", st.Bottom, ":b", st.From), b = AddNode("stair", st.Instance, $"stair{i}", "top", st.Top, ":t", st.To);
            g.Link(new StationEdge(a, b, Dist(st.Bottom, st.Top) * StairFactor, "stair"));
        }
        BuildLap("節点");
        // 徒歩 (同じ区画・同じ階): いちばん近い WalkNeighbors 個
        foreach (var grp in Enumerable.Range(0, g.Nodes.Count).Where(i => g.Nodes[i].Floor.Length > 0 && g.Nodes[i].Section >= 0)
                     .GroupBy(i => (g.Nodes[i].Section, g.Nodes[i].Floor)))
        {
            var ids = grp.ToList();
            var done = new HashSet<(int, int)>();
            foreach (var i in ids)
                foreach (var j in ids.Where(j => j != i).OrderBy(j => Dist(g.Nodes[i].Pos, g.Nodes[j].Pos)).Take(WalkNeighbors))
                    if (done.Add((Math.Min(i, j), Math.Max(i, j)))) g.Link(new StationEdge(i, j, Dist(g.Nodes[i].Pos, g.Nodes[j].Pos), "walk"));
        }
        BuildLap("徒歩");
        // 区画をまたぐ (door): 同じ階で DoorRadius 以内 (格子で探す)
        var cells = new Dictionary<(string, long, long), List<int>>();
        for (int i = 0; i < g.Nodes.Count; i++)
        {
            var n = g.Nodes[i];
            if (n.Floor.Length == 0) continue;
            var key = (n.Floor, (long)Math.Floor(n.Pos.X / DoorRadius), (long)Math.Floor(n.Pos.Y / DoorRadius));
            if (!cells.TryGetValue(key, out var l)) cells[key] = l = new List<int>();
            l.Add(i);
        }
        foreach (var ((f, cx, cy), list) in cells)
            foreach (var i in list)
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        if (cells.TryGetValue((f, cx + dx, cy + dy), out var other))
                            foreach (var j in other)
                                if (j > i && g.Nodes[i].Section != g.Nodes[j].Section && Dist(g.Nodes[i].Pos, g.Nodes[j].Pos) <= DoorRadius)
                                    g.Link(new StationEdge(i, j, Dist(g.Nodes[i].Pos, g.Nodes[j].Pos), "door"));
        BuildLap("扉 1");
        // 区画をまたぐ (door) その 2: 節点が別の区画の箱 (DoorBoxMargin m 広げたもの) にも入っていれば、その区画の同じ階のいちばん近い DoorNeighbors 個 (DoorMaxLength m 以内) と結ぶ
        // (区画の境の接続 (roomconnector) は片方の区画にしか属さないため。入れ子・重なりの区画もこれでつながる)
        var byGroup = Enumerable.Range(0, g.Nodes.Count).Where(i => g.Nodes[i].Floor.Length > 0 && g.Nodes[i].Section >= 0)
            .GroupBy(i => (g.Nodes[i].Section, g.Nodes[i].Floor)).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var s in sections)
        {
            var ins = tree.Instances[s.Instance];
            var a = ins.MinBounds!.Value; var b = ins.MaxBounds!.Value;
            var inv = secInv[s.Instance];
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var n = g.Nodes[i];
                if (n.Section == s.Instance || n.Floor.Length == 0 || !byGroup.TryGetValue((s.Instance, n.Floor), out var members)) continue;
                var q = inv.Apply(n.Pos);
                if (q.X < a.X - DoorBoxMargin || q.X > b.X + DoorBoxMargin || q.Y < a.Y - DoorBoxMargin || q.Y > b.Y + DoorBoxMargin || q.Z < a.Z - DoorBoxMargin || q.Z > b.Z + DoorBoxMargin) continue;
                foreach (var j in members.Where(j => j != i).Select(j => (j, d: Dist(n.Pos, g.Nodes[j].Pos))).Where(t => t.d <= DoorMaxLength).OrderBy(t => t.d).Take(DoorNeighbors))
                    g.Link(new StationEdge(i, j.j, j.d, "door"));
            }
        }
        BuildLap("扉 2");
        // 区画をまたぐ (floor): 床のセルの上を通る直線。節点ごとに別の区画の節点を近い順に FloorLinkNeighbors 個まで
        if (floorCells != null)
        {
            var grid = new Dictionary<(string, long, long), List<int>>();
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var n = g.Nodes[i];
                if (n.Floor.Length == 0) continue;
                var key = (n.Floor, (long)Math.Floor(n.Pos.X / FloorLinkLength), (long)Math.Floor(n.Pos.Y / FloorLinkLength));
                if (!grid.TryGetValue(key, out var l)) grid[key] = l = new List<int>();
                l.Add(i);
            }
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var n = g.Nodes[i];
                if (n.Floor.Length == 0 || !floorCells.TryGetValue(n.Floor, out var fc) || fc.Count == 0) continue;
                long cx = (long)Math.Floor(n.Pos.X / FloorLinkLength), cy = (long)Math.Floor(n.Pos.Y / FloorLinkLength);
                var cand = new List<(int J, double D)>();
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        if (grid.TryGetValue((n.Floor, cx + dx, cy + dy), out var other))
                            foreach (var j in other)
                                if (j != i && g.Nodes[j].Section != n.Section) { var d = Dist(n.Pos, g.Nodes[j].Pos); if (d <= FloorLinkLength) cand.Add((j, d)); }
                int added = 0;
                foreach (var (j, d) in cand.OrderBy(c => c.D))
                {
                    if (added >= FloorLinkNeighbors) break;
                    if (StationFloorBuilder.OnFloorRatio(fc, (n.Pos.X, n.Pos.Y), (g.Nodes[j].Pos.X, g.Nodes[j].Pos.Y)) < FloorLinkRatio) continue;
                    g.Link(new StationEdge(i, j, d, "floor"));
                    added++;
                }
            }
        }
        BuildLap("床の直線");
        // 経由点 (利用者の決定で足したもの): 床のセルの上に WaypointStep セル (3 m) おきに節点 (kind waypoint) を置き、隣どうし (8 方向) を
        // 線分の FloorLinkRatio 以上が床の上なら結ぶ。ほかの節点は、同じ階のいちばん近い経由点 WaypointLinks 個 (WaypointRadius m 以内、線分の 90 % 以上が床の上) と結ぶ
        if (floorCells != null) AddWaypoints(g, floors, floorCells);
        BuildLap("経由点");
        if (plates != null) { LastTouchEdges = AddTouchEdges(g, floors, plates); BuildLap("接する床"); }
        // .rmxml: 部屋 − 出口の接続
        foreach (var (a, b) in RmxmlEdges(tree))
            if (pointNode.TryGetValue(a, out var na) && pointNode.TryGetValue(b, out var nb))
                g.Link(new StationEdge(na, nb, Dist(g.Nodes[na].Pos, g.Nodes[nb].Pos), "rmxml"));
        BuildLap("部屋");
        // エレベーター・トラム: 実装されている乗り場 (ImplementedGateways) どうしだけを結ぶ。トラムは区間の両端が実装されている区間だけ
        var impl = ImplementedGateways(tree, floors, g, lines);
        foreach (var l in lines.Where(l => !l.NoRide))
        {
            var gw = l.Gateways.Select(x => pointNode[(x.Instance, x.Gateway.Guid)]).ToList();
            if (l.Tram)
            {
                foreach (var (a, b, dir, _) in l.Legs)
                    if (impl.Contains(gw[a]) && impl.Contains(gw[b])) g.Link(new StationEdge(gw[a], gw[b], TramCost, "tram", l.Key, dir, OneWay: true));
            }
            else
                for (int i = 0; i < gw.Count; i++)
                    for (int j = i + 1; j < gw.Count; j++)
                        if (impl.Contains(gw[i]) && impl.Contains(gw[j]) && (g.Nodes[gw[i]].Floor != g.Nodes[gw[j]].Floor || g.Nodes[gw[i]].Floor.Length == 0))
                            g.Link(new StationEdge(gw[i], gw[j], ElevatorCost, "elevator", l.Key));
        }
        BuildLap("乗り物");
        // 実装されていない行き先の階 (乗り場はあるが、実装されている乗り場も使える物も無い階) は一覧に出さない
        MarkImplemented(floors, g, impl);
        MarkReachable(floors, g);
        HideIsolatedShopFloors(floors, g);
        BuildLap("印");
        return g;
    }

    // 実装されているかの判定 (利用者の指示 2026-11-01: 行き先の階に何もなければ実装されていない):
    // 乗り場から、エレベーター・トラムを使わずにたどれる節点 (歩き・扉・床・階段・部屋の辺の成分) に、使える物が 1 つ以上あれば、その乗り場は実装されている。
    // 使える物 (UsableKinds): 店 (SCShop)、医療・リスポンの場所 (entitycomponentspawnpoint・entitycomponentslotspawnpoint)、医療などの動作の場所 (actionareacomponent)、
    // 任務の印 (subsumptionmissionlinkcomponent)、出入りの小部屋 (entitycomponentspawncloset)。これらの点は位置を持たないものが多いので、
    // 点のインスタンスの箱の中心 (底 + 1 m) をいちばん近い節点に当てる (インスタンスごとに 1 回)
    // 直前の ImplementedGateways で、場所の部分木の使える物で実装されているとみなした乗り場の数 (確かめ用)
    public static int LastImplementedByPlace;

    // (2026-11-01、親の決定 (b)) 船の発着の場所 (.soc の Entity EntityClass="LandingArea"。SocpakContents.LandingAreaKind) も使える物に数える。これは位置 (Pos) があるので、その位置をいちばん近い節点に当てる
    public static readonly string[] UsableKinds =["actionareacomponent", "entitycomponentspawnpoint", "entitycomponentslotspawnpoint", "subsumptionmissionlinkcomponent", "entitycomponentspawncloset", SocpakContents.LandingAreaKind];

    public static HashSet<int> ImplementedGateways(StationTree tree, IReadOnlyList<StationFloor> floors, StationGraph g, IReadOnlyList<Line>? lines = null)
    {
        var adj = new List<int>[g.Nodes.Count];
        for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
        foreach (var e in g.Edges) if (e.Kind is not ("elevator" or "tram")) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }
        var comp = Enumerable.Repeat(-1, g.Nodes.Count).ToArray();
        int nc = 0;
        for (int i = 0; i < comp.Length; i++)
        {
            if (comp[i] >= 0) continue;
            var q = new Queue<int>(); q.Enqueue(i); comp[i] = nc;
            while (q.Count > 0) { var u = q.Dequeue(); foreach (var v in adj[u]) if (comp[v] < 0) { comp[v] = nc; q.Enqueue(v); } }
            nc++;
        }
        var usable = new HashSet<int>();
        for (int i = 0; i < g.Nodes.Count; i++) if (g.Nodes[i].Kind == "shop") usable.Add(comp[i]);
        foreach (var la in tree.Points.Where(p => p.Kind == SocpakContents.LandingAreaKind && p.Pos != null))
        {
            var ni = NearestNode(g, floors, la.Pos!.Value);
            if (ni >= 0) usable.Add(comp[ni]);
        }
        foreach (var inst in tree.Points.Where(p => UsableKinds.Contains(p.Kind) && p.Kind != SocpakContents.LandingAreaKind).Select(p => p.Instance).Distinct())
        {
            var ins = tree.Instances[inst];
            var pos = ins.MinBounds is { } a && ins.MaxBounds is { } b ? ins.ToStation.Apply(((a.X + b.X) / 2, (a.Y + b.Y) / 2, a.Z + 1)) : ins.ToStation.Apply((0, 0, 0));
            var ni = NearestNode(g, floors, pos);
            if (ni >= 0) usable.Add(comp[ni]);
        }
        // 乗り継ぎ: 使える物の無い成分でも、別の路線の乗り場があり、その路線が使える成分に止まるなら、使える成分とみなす (増えなくなるまで)。
        // Orison の August Dunlow Spaceport のシャトルの駅 (店は無い) は、エレベーター 64da4937 で宇宙港のロビー (店がある) に上がる
        if (lines != null)
        {
            var gwIndex = Enumerable.Range(0, g.Nodes.Count).Where(k => g.Nodes[k].Kind == "gateway").GroupBy(k => (g.Nodes[k].Instance, g.Nodes[k].Guid)).ToDictionary(x => x.Key, x => x.First());
            var stops = lines.Select(l => l.Gateways.Select(x => gwIndex.TryGetValue((x.Instance, x.Gateway.Guid), out var k) ? comp[k] : -1).Where(c => c >= 0).Distinct().ToList()).ToList();
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int a = 0; a < stops.Count; a++)
                    foreach (var c in stops[a])
                    {
                        if (usable.Contains(c)) continue;
                        for (int b = 0; b < stops.Count && !usable.Contains(c); b++)
                            if (b != a && stops[b].Contains(c) && stops[b].Any(x => x != c && usable.Contains(x))) { usable.Add(c); changed = true; }
                    }
            }
        }
        // (2026-11-01、利用者の決定「データがあれば出す」) 歩いてたどれる範囲に使える物が無くても、乗り場の場所 (ルートの直下のインスタンス。都市・ステーションの中の 1 か所) の
        // 部分木に使える物 (店・UsableKinds の点) があれば、実装されているとみなす。Orison の Vision Center (orison_conv_platform) は店 (SCShop) 3 つがあるが、
        // 入口の階段の床がつながらず、乗り場から歩いてたどれなかった
        var topOf = new int[tree.Instances.Count];
        for (int i = 0; i < topOf.Length; i++) { int k = i; while (k >= 0 && tree.Instances[k].Parent > 0) k = tree.Instances[k].Parent; topOf[i] = k; }
        var usableTop = tree.Shops.Select(s => topOf[s.Instance]).Concat(tree.Points.Where(p => UsableKinds.Contains(p.Kind)).Select(p => topOf[p.Instance])).Where(k => k > 0).ToHashSet();
        LastImplementedByPlace = 0;
        var res = new HashSet<int>();
        for (int i = 0; i < g.Nodes.Count; i++)
        {
            if (g.Nodes[i].Kind != "gateway") continue;
            if (usable.Contains(comp[i])) res.Add(i);
            else if (g.Nodes[i].Instance >= 0 && g.Nodes[i].Instance < topOf.Length && usableTop.Contains(topOf[g.Nodes[i].Instance])) { res.Add(i); LastImplementedByPlace++; }
        }
        return res;
    }

    // 乗り場のある階で、実装されている乗り場が 1 つも無い階を Implemented = false・Listed = false にする
    public static void MarkImplemented(IReadOnlyList<StationFloor> floors, StationGraph g, HashSet<int> impl)
    {
        var byFloor = Enumerable.Range(0, g.Nodes.Count).Where(i => g.Nodes[i].Kind == "gateway" && g.Nodes[i].Floor.Length > 0).GroupBy(i => g.Nodes[i].Floor).ToDictionary(x => x.Key, x => x.ToList());
        var usableFloor = Enumerable.Range(0, g.Nodes.Count).Where(i => g.Nodes[i].Kind == "shop" && g.Nodes[i].Floor.Length > 0).Select(i => g.Nodes[i].Floor).ToHashSet();
        foreach (var f in floors)
        {
            f.ElevatorOnly = false;
            if (!byFloor.TryGetValue(f.Id, out var gws) || gws.Any(impl.Contains) || usableFloor.Contains(f.Id)) continue;
            f.Implemented = false;
            f.Listed = false;
        }
    }

    // 人の行ける階の印 (2026-11-01): 乗り場 (kind gateway。乗らないエレベーターの乗り場も含む) の節点から、全部の辺 (向きは見ない) でたどれる節点が 1 つも無く、
    // 部屋・接続 (roomconnector)・店・乗り場の節点も無い、都市の板だけの階 (StationFloor.CityOnly) を Reachable = false・Listed = false にする。Cloudview Center の島の土台の塊 (habitation_addon_plate_d、上面 -57 m など) の上面が、
    // 何ともつながらない階として地図に出ていた (利用者の確認でゲームには無い)
    // 直前の MarkReachable で外した階の数 (確かめ用)
    public static int LastUnreachable;
    public const double CeilingRatio = 0.5;
    public const double CeilingMaxArea = 300;
    public const double CeilingMaxRise = 6.5;

    public static void MarkReachable(IReadOnlyList<StationFloor> floors, StationGraph g)
    {
        var adj = new List<int>[g.Nodes.Count];
        for (int i = 0; i < adj.Length; i++) adj[i] = new List<int>();
        foreach (var e in g.Edges) { adj[e.A].Add(e.B); adj[e.B].Add(e.A); }
        var seen = new bool[g.Nodes.Count];
        var q = new Queue<int>();
        for (int i = 0; i < g.Nodes.Count; i++) if (g.Nodes[i].Kind == "gateway") { seen[i] = true; q.Enqueue(i); }
        while (q.Count > 0) { var u = q.Dequeue(); foreach (var v in adj[u]) if (!seen[v]) { seen[v] = true; q.Enqueue(v); } }
        // 部屋・接続・店・乗り場・行き先の節点のある階は、たどれなくても残す (Checkmate の Contested Zone などは、扉をグラフで結べていないだけで、たどれない階が 23 あった)
        var reached = Enumerable.Range(0, g.Nodes.Count).Where(i => g.Nodes[i].Floor.Length > 0 && (seen[i] || g.Nodes[i].Kind is "room" or "connector" or "shop" or "gateway"))
            .Select(i => g.Nodes[i].Floor).ToHashSet();
        // (2026-11-01、親の決定) 都市の板だけの階でなくても、乗り場からたどれる節点が無く、店・乗り場の節点も無い階は、人の行けない床として外す
        // (Seraphim のガレリアの上 6 m・12 m の床の板の小さなまとまり f08・f09。天井などの可能性 (推測))。部屋・接続 (roomconnector) の節点のある階は、前と同じく残す
        // (部屋・接続も外す理由にしたら、Checkmate で扉をグラフで結べていないだけの Contested Zone・精錬所などの 42 階が一覧から消えた)
        // (2026-11-01、親の決定の続き) ただし外しすぎないよう、都市の板だけの階でない階は、たどれないうえに「天井らしい小さな床」のときだけ外す:
        //   床の升目 (CellZ) の CeilingRatio (50 %) 以上が、同じまとまりの別の階の床の 2.5〜6.5 m 上にあり、床の板の面積が CeilingMaxArea (300 m²) 未満のもの。
        //   値は実データの比べから決めた: Seraphim のガレリアの f08 (66 %・260 m²)・f09 (100 %・188 m²) は外れ、Checkmate の Contested Zone のたどれない 7 階は
        //   (7〜67 %・400〜2208 m²) どれも残る。Contested Zone の階がたどれない理由は確かめきれていない
        LastUnreachable = 0;
        var byGroup = floors.Where(f => f.CellZ != null && f.Group.Length > 0).GroupBy(f => f.Group).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var f in floors)
        {
            var ok = reached.Contains(f.Id);
            if (!ok && !f.CityOnly)
            {
                var area = f.PlateSet?.Where(p => !p.Stair).Sum(p => StationFloorBuilder.HullArea(p.Hull)) ?? 0;
                var others = byGroup.TryGetValue(f.Group, out var l) ? l.Where(o => o != f).ToList() : new List<StationFloor>();
                int below = 0, all = f.CellZ?.Count ?? 0;
                if (f.CellZ != null)
                    foreach (var (c, z) in f.CellZ)
                        if (others.Any(o => o.CellZ!.TryGetValue(c, out var oz) && z.Min - oz.Max >= StationFloorBuilder.LayerGap && z.Min - oz.Max <= CeilingMaxRise)) below++;
                ok = !(all > 0 && below >= all * CeilingRatio && area < CeilingMaxArea);
            }
            f.Reachable = ok;
            if (!f.Reachable) { f.Listed = false; LastUnreachable++; }
        }
    }

    // (2026-11-02、利用者の決まり「店のフロアでどこともつながってないものは無視」(案 2)) 一覧に出る店の階 (店の節点がある) で、乗り場の節点 (乗らないエレベーターも含む) が無く、
    // ほかの階の節点への辺が 1 本も無いものを、一覧から外す (Listed = false。階・店・道は消さない)。Build の最後 (踊り場・道の直しの後) に当てる
    public static int LastIsolatedShopFloors;
    public static void HideIsolatedShopFloors(IReadOnlyList<StationFloor> floors, StationGraph g)
    {
        LastIsolatedShopFloors = 0;
        var shop = new HashSet<string>(StringComparer.Ordinal); var gw = new HashSet<string>(StringComparer.Ordinal); var linked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in g.Nodes)
        {
            if (n.Floor.Length == 0) continue;
            if (n.Kind == "shop") shop.Add(n.Floor);
            if (n.Kind == "gateway") gw.Add(n.Floor);
        }
        foreach (var e in g.Edges)
        {
            string fa = g.Nodes[e.A].Floor, fb = g.Nodes[e.B].Floor;
            if (fa.Length > 0 && fb.Length > 0 && fa != fb) { linked.Add(fa); linked.Add(fb); }
        }
        foreach (var f in floors)
            if (f.Listed && shop.Contains(f.Id) && !gw.Contains(f.Id) && !linked.Contains(f.Id)) { f.Listed = false; LastIsolatedShopFloors++; }
    }

    public const int WaypointStep = 6;
    public const double WaypointRadius = 6;
    public const int WaypointLinks = 3;

    // (2026-11-02、利用者の原則「すき間の無い高低差が 0.5m 未満の続きの面はつながっている」) 違う階の床の板どうしが接していれば (StationFloorBuilder.SurfacesTouch)、
    // 接する所のそばの両方の階の経由点 (TouchLinkRadius 以内でいちばん近いもの) を辺 (kind floor) で結ぶ。階の分け方は変えない。
    // Area18 の Dumper's Depot の入口 (x 22.5〜25、y −155〜−143) で、店の床 roomsm_floor_straight_08x08x0025_a (−8.0 m) と Plaza の床 (−8.0 m) が
    // 同じ高さで接しているのに、別の階 (f42・f43) になり、階をまたぐ道が無くて切れていた。同じ階の組と 8 m の升目ごとに 1 本だけ足す
    public const double TouchLinkRadius = 4.0;
    public static int LastTouchEdges;

    private static int AddTouchEdges(StationGraph g, IReadOnlyList<StationFloor> floors, IReadOnlyList<StationPlate> plates)
    {
        var fp = plates.Where(p => !p.Stair).Select(p => (P: p, F: StationFloorBuilder.PlateFloor(p, floors))).Where(t => t.F != null).ToList();
        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < fp.Count; i++)
        {
            var h = fp[i].P.Hull;
            for (int x = (int)Math.Floor((h.Min(q => q.X) - 1) / 8); x <= (int)Math.Floor((h.Max(q => q.X) + 1) / 8); x++)
                for (int y = (int)Math.Floor((h.Min(q => q.Y) - 1) / 8); y <= (int)Math.Floor((h.Max(q => q.Y) + 1) / 8); y++)
                    (grid.TryGetValue((x, y), out var l) ? l : grid[(x, y)] = new()).Add(i);
        }
        var wpByFloor = g.Nodes.Select((n, idx) => (n, idx)).Where(t => t.n.Kind == "waypoint").GroupBy(t => t.n.Floor).ToDictionary(x => x.Key, x => x.ToList(), StringComparer.Ordinal);
        var done = new HashSet<(string, string, int, int)>();
        int added = 0;
        foreach (var cellList in grid.Values)
            for (int a = 0; a < cellList.Count; a++)
                for (int b = a + 1; b < cellList.Count; b++)
                {
                    var (pa, fa) = fp[cellList[a]]; var (pb, fb) = fp[cellList[b]];
                    if (fa == fb || !StationFloorBuilder.SurfacesTouch(pa, pb)) continue;
                    var c0 = (X: pa.Hull.Average(q => q.X), Y: pa.Hull.Average(q => q.Y));
                    var cb = StationFloorBuilder.ClosestOnConvex(pb.Hull, c0.X, c0.Y);
                    var ca = StationFloorBuilder.ClosestOnConvex(pa.Hull, cb.X, cb.Y);
                    var mid = ((ca.X + cb.X) / 2, (ca.Y + cb.Y) / 2);
                    var (f1, f2) = string.CompareOrdinal(fa!.Id, fb!.Id) < 0 ? (fa, fb) : (fb, fa);
                    if (!done.Add((f1.Id, f2.Id, (int)Math.Floor(mid.Item1 / 8), (int)Math.Floor(mid.Item2 / 8)))) continue;
                    int Near(StationFloor f)
                    {
                        if (!wpByFloor.TryGetValue(f.Id, out var l)) return -1;
                        int best = -1; double bd = TouchLinkRadius;
                        foreach (var (n, idx) in l) { var d = Math.Sqrt((n.Pos.X - mid.Item1) * (n.Pos.X - mid.Item1) + (n.Pos.Y - mid.Item2) * (n.Pos.Y - mid.Item2)); if (d < bd) { bd = d; best = idx; } }
                        return best;
                    }
                    int na = Near(fa), nb = Near(fb);
                    if (na < 0 || nb < 0) continue;
                    g.Link(new StationEdge(na, nb, Dist(g.Nodes[na].Pos, g.Nodes[nb].Pos), "floor"));
                    added++;
                }
        return added;
    }

    private static void AddWaypoints(StationGraph g, IReadOnlyList<StationFloor> floors, Dictionary<string, HashSet<(int I, int J)>> cells)
    {
        int n0 = g.Nodes.Count;
        var zOf = floors.ToDictionary(f => f.Id, f => f.Z, StringComparer.Ordinal);
        var wp = new Dictionary<(string, int, int), int>();
        const double c = StationFloorBuilder.Cell;
        // 経由点は一覧に出す階 (Listed) にだけ置く (Orison は名前の無い建物・足場の床が多く、全部に置くと経由点が数百万になった)
        var listed = floors.Where(f => f.Listed).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        // (2026-11-01、速くする直し。結果は前と同じ) 区切りごとの経由点のセル選びと、経由点どうし・節点と経由点の辺の判定 (床の上の割合) は、
        // 階ごと・経由点ごと・節点ごとに並べて計算し、節点を足す・辺を結ぶのは前と同じ順に 1 本ずつ行う (節点の番号・辺の順は前と同じ)。
        // Lorville で、経由点の段が 53.7 秒かかっていた (ハーネス)
        var floorList = cells.Where(kv => zOf.ContainsKey(kv.Key) && listed.Contains(kv.Key)).ToList();
        var picked = new List<((int, int) Key, int I, int J)>[floorList.Count];
        Parallel.For(0, floorList.Count, k =>
        {
            var set = floorList[k].Value;
            var list = new List<((int, int), int, int)>();
            // WaypointStep × WaypointStep セルの区切りごとに 1 つ: 区切りの中の床のセルのうち区切りの中心にいちばん近いもの
            // (区切りの角のセルだけにすると、斜めに置かれた狭い出入口 (Checkmate の bulkh_004) で経由点が置かれず、床が途切れた)
            foreach (var blk in set.GroupBy(t => (FloorDiv(t.I, WaypointStep), FloorDiv(t.J, WaypointStep))).OrderBy(b => b.Key.Item1).ThenBy(b => b.Key.Item2))
            {
                double ci = blk.Key.Item1 * WaypointStep + (WaypointStep - 1) / 2.0, cj = blk.Key.Item2 * WaypointStep + (WaypointStep - 1) / 2.0;
                var (i, j) = blk.OrderBy(t => (t.I - ci) * (t.I - ci) + (t.J - cj) * (t.J - cj)).ThenBy(t => t.I).ThenBy(t => t.J).First();
                list.Add((blk.Key, i, j));
            }
            picked[k] = list;
        });
        for (int k = 0; k < floorList.Count; k++)
        {
            var fid = floorList[k].Key; var z = zOf[fid];
            foreach (var (key, i, j) in picked[k])
                wp[(fid, key.Item1, key.Item2)] = g.Add(new StationNode($"w:{fid}:{i}:{j}", "waypoint", ((i + 0.5) * c, (j + 0.5) * c, z), fid, -1, "", -1, ""));
        }
        var wpList = wp.ToList();
        var wpEdges = new List<StationEdge>[wpList.Count];
        Parallel.For(0, wpList.Count, k =>
        {
            var ((fid, i, j), a) = wpList[k];
            var list = new List<StationEdge>();
            foreach (var (di, dj) in new[] { (1, 0), (0, 1), (1, 1), (1, -1) })
                if (wp.TryGetValue((fid, i + di, j + dj), out var b))
                {
                    var pa = g.Nodes[a].Pos; var pb = g.Nodes[b].Pos;
                    if (StationFloorBuilder.OnFloorRatio(cells[fid], (pa.X, pa.Y), (pb.X, pb.Y)) >= FloorLinkRatio)
                        list.Add(new StationEdge(a, b, Math.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Y - pb.Y) * (pa.Y - pb.Y)), "floor"));
                }
            wpEdges[k] = list;
        });
        foreach (var list in wpEdges) foreach (var e in list) g.Link(e);
        int r = (int)Math.Ceiling(WaypointRadius / c / WaypointStep) + 1;
        int rg = (int)Math.Ceiling(ElevatorSameRadius / c / WaypointStep) + 1;
        static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
        var nodeEdges = new List<StationEdge>?[n0];
        Parallel.For(0, n0, n =>
        {
            var node = g.Nodes[n];
            if (node.Floor.Length == 0 || !cells.TryGetValue(node.Floor, out var fc)) return;
            int bi = (int)Math.Floor(node.Pos.X / c / WaypointStep), bj = (int)Math.Floor(node.Pos.Y / c / WaypointStep);
            var cand = new List<(int W, double D)>();
            // (2026-11-02、親の決定 a) 乗り場は、WaypointRadius 以内に経由点が無ければ ElevatorSameRadius (16 m) 以内のいちばん近いものと結ぶ
            var far = new List<(int W, double D)>();
            int rr = node.Kind == "gateway" ? rg : r;
            for (int a = -rr; a <= rr; a++)
                for (int b = -rr; b <= rr; b++)
                    if (wp.TryGetValue((node.Floor, bi + a, bj + b), out var w))
                    {
                        var d = Dist(g.Nodes[w].Pos, node.Pos);
                        // 乗り場 (gateway) は床の上の割合を見ない (シャトルの乗り場は線路の側で、乗り場の縁の床から 4〜5 m 外。Orison の 4bb021eb)。
                        // 階段の端は階段の板の上、出入口の接続 (roomconnector) は扉の下の床の途切れ (Checkmate の bulkh_004 の扉で約 1.5〜2 m) にあり、
                        // どちらも床のセルの外なので、床の上の割合を 0.5 まで緩める。接続は両側の経由点と結ばれ、扉を渡る道になる
                        if (node.Kind == "gateway" && d > WaypointRadius && d <= ElevatorSameRadius) far.Add((w, d));
                        if (d <= WaypointRadius && StationFloorBuilder.OnFloorRatio(fc, (node.Pos.X, node.Pos.Y), (g.Nodes[w].Pos.X, g.Nodes[w].Pos.Y)) >= (node.Kind == "gateway" ? 0 : node.Kind == "stair" && d <= StairEndLink ? 0 : node.Kind is "stair" or "connector" ? 0.5 : 0.9)) cand.Add((w, d));
                    }
            if (cand.Count == 0 && far.Count > 0) cand.Add(far.MinBy(t => t.D));
            // 接続は扉の両側に結ぶため多めに (WaypointLinks の 2 倍)
            nodeEdges[n] = cand.OrderBy(t => t.D).Take(node.Kind == "connector" ? WaypointLinks * 2 : WaypointLinks).Select(t => new StationEdge(n, t.W, t.D, "floor")).ToList();
        });
        foreach (var list in nodeEdges) if (list != null) foreach (var e in list) g.Link(e);
    }

    // .rmxml の行から「部屋 (1 段目) − 出口の接続 (3 段目)」の組 (インスタンス, CryGUID) を作る
    public static IEnumerable<((int, string) Room, (int, string) Exit)> RmxmlEdges(StationTree tree)
    {
        var kids = tree.Instances.ToLookup(i => i.Parent);
        var byInstGuid = tree.Points.GroupBy(p => (p.Instance, p.Guid)).ToDictionary(x => x.Key, x => x.First());
        var cache = new Dictionary<(int, string), (int, string)?>();
        (int, string)? Resolve(int inst, string super)
        {
            if (cache.TryGetValue((inst, super), out var r)) return r;
            var parts = super.Split(',');
            int cur = inst;
            for (int k = 0; k < parts.Length - 1 && cur >= 0; k++)
                cur = kids[cur].FirstOrDefault(x => string.Equals(x.Guid, parts[k].Trim(), StringComparison.OrdinalIgnoreCase))?.Index ?? -1;
            r = cur >= 0 && byInstGuid.ContainsKey((cur, parts[^1].Trim())) ? (cur, parts[^1].Trim()) : null;
            return cache[(inst, super)] = r;
        }
        var seen = new HashSet<((int, string), (int, string))>();
        foreach (var row in tree.RoomRoutes)
        {
            if (row.A.Length == 0 || row.C.Length == 0) continue;
            if (Resolve(row.Instance, row.A) is not { } a || Resolve(row.Instance, row.C) is not { } c) continue;
            if (byInstGuid[a].Kind != "entitycomponentroom") continue;
            if (seen.Add((a, c))) yield return (a, c);
        }
    }

    // 節点の階: 高さ (z + 0.6 m 以下に底があり、上面から 6 m 以内) で当たる階のうち、上から順に、節点の xy の 3 セル (1.5 m) 以内に床のセルがある最初の階。
    // 出入口の接続 (roomconnector) は床の板の途切れたところ (扉の下は socpak の形に床が無い) にあるので、1.5 m まで見る。
    // 床のセルが無ければ FloorAt。店・部屋の点は床より 1〜2 m 上にあることがあり (Checkmate の BlackMarket の店は床 129.8 m に対し 131.1 m)、
    // 高さだけでは上の階に当たっていた
    public static StationFloor? NodeFloor(IReadOnlyList<StationFloor> floors, Dictionary<string, HashSet<(int I, int J)>>? cells, (double X, double Y, double Z) pos)
    {
        if (cells != null)
        {
            int i = (int)Math.Floor(pos.X / StationFloorBuilder.Cell), j = (int)Math.Floor(pos.Y / StationFloorBuilder.Cell);
            foreach (var f in floors.Where(f => f.ZMin <= pos.Z + 0.6 && pos.Z - f.ZMax <= 6).OrderByDescending(f => f.Z))
            {
                if (!cells.TryGetValue(f.Id, out var set)) continue;
                for (int di = -3; di <= 3; di++)
                    for (int dj = -3; dj <= 3; dj++)
                        if (set.Contains((i + di, j + dj))) return f;
            }
        }
        return StationFloorBuilder.FloorAtXY(floors, pos.X, pos.Y, pos.Z);
    }

    // 局所の点にいちばん近い節点 (同じ階を優先。階が無ければ全部から)。kinds があればその種類だけ
    // 乗り場の階 (NodeFloor で当たらないとき): 乗り場の高さ ± 2 m の階のうち、xy の GatewayFloorRadius (6 m) 以内に床のセルがあるもので、いちばん近いもの。
    // シャトルの乗り場は線路の側にあり、乗り場の縁の床から 4〜5 m 離れている (Orison の 4bb021eb・4cdd6c07 は階が付いていなかった)
    public const double GatewayFloorRadius = 6;

    // 床のセルの無いところ (TransitList) での乗り場の階: 高さ ± 2 m (Providence の乗り場 4cdd6c07 は床より 1.1 m 低い) の階のうち、まとまりの箱から GatewayFloorRadius 以内で、箱のいちばん小さいもの
    public static StationFloor? GatewayFloorByBounds(IReadOnlyList<StationFloor> floors, (double X, double Y, double Z) pos) =>
        floors.Where(f => pos.Z >= f.ZMin - 2 && pos.Z <= f.ZMax + 2 && f.Bounds != null && f.Contains(pos.X, pos.Y, GatewayFloorRadius))
            .OrderBy(f => (f.Bounds!.Value.MaxX - f.Bounds!.Value.MinX) * (f.Bounds!.Value.MaxY - f.Bounds!.Value.MinY)).FirstOrDefault();

    // (2026-11-02、親の決定 a) エレベーターの乗り場の階: NodeFloor の階の高さの範囲が乗り場の高さ ± ElevatorSameRise (1 m) にかからず、乗り場と同じ高さ (± 1 m) の床の升目 (CellZ) が
    // xy で ElevatorSameRadius (16 m) 以内にある階があれば、そのうちいちばん近い階。無ければ null (NodeFloor に任せる)。
    // Orison の Crusader Showroom のエレベーター 4 つ (−19.67 m) は、Showroom の床の端 (x −16.3) から 4〜16 m 離れ (あいだは部屋のメッシュ showroom_elev_lobby_a で床の名前でない)、
    // NodeFloor では 5 m 下の階に当たっていた
    public const double ElevatorSameRise = 1.0;
    public const double ElevatorSameRadius = 16;
    public static StationFloor? ElevatorGatewayFloor(IReadOnlyList<StationFloor> floors, Dictionary<string, HashSet<(int I, int J)>>? cells, (double X, double Y, double Z) pos)
    {
        var nf = NodeFloor(floors, cells, pos);
        if (nf != null && nf.ZMin <= pos.Z + ElevatorSameRise && nf.ZMax >= pos.Z - ElevatorSameRise) return null;
        int r = (int)Math.Ceiling(ElevatorSameRadius / StationFloorBuilder.LayerCell);
        int ci = (int)Math.Floor(pos.X / StationFloorBuilder.LayerCell), cj = (int)Math.Floor(pos.Y / StationFloorBuilder.LayerCell);
        StationFloor? best = null; double bd = double.MaxValue;
        foreach (var f in floors)
        {
            if (f.CellZ == null || f.ZMin > pos.Z + ElevatorSameRise || f.ZMax < pos.Z - ElevatorSameRise) continue;
            for (int di = -r; di <= r; di++)
                for (int dj = -r; dj <= r; dj++)
                    if (f.CellZ.TryGetValue((ci + di, cj + dj), out var z) && z.Min <= pos.Z + ElevatorSameRise && z.Max >= pos.Z - ElevatorSameRise)
                    {
                        double dx = (ci + di + 0.5) * StationFloorBuilder.LayerCell - pos.X, dy = (cj + dj + 0.5) * StationFloorBuilder.LayerCell - pos.Y;
                        var d = Math.Sqrt(dx * dx + dy * dy);
                        if (d <= ElevatorSameRadius && d < bd) { bd = d; best = f; }
                    }
        }
        return best;
    }

    public static StationFloor? GatewayFloor(IReadOnlyList<StationFloor> floors, Dictionary<string, HashSet<(int I, int J)>>? cells, (double X, double Y, double Z) pos)
    {
        if (cells == null) return null;
        int r = (int)Math.Ceiling(GatewayFloorRadius / StationFloorBuilder.Cell);
        int ci = (int)Math.Floor(pos.X / StationFloorBuilder.Cell), cj = (int)Math.Floor(pos.Y / StationFloorBuilder.Cell);
        StationFloor? best = null; double bd = double.MaxValue;
        foreach (var f in floors.Where(f => pos.Z >= f.ZMin - 2 && pos.Z <= f.ZMax + 2))
        {
            if (!cells.TryGetValue(f.Id, out var set) || set.Count == 0) continue;
            for (int di = -r; di <= r; di++)
                for (int dj = -r; dj <= r; dj++)
                    if (set.Contains((ci + di, cj + dj)))
                    {
                        var d = Math.Sqrt(di * di + dj * dj) * StationFloorBuilder.Cell;
                        if (d <= GatewayFloorRadius && d < bd) { bd = d; best = f; }
                    }
        }
        return best;
    }

    // 地区 (利用者の決定 2026-11-01、都市の全体図のため)。輪の路線 (外していないトラム) の乗り場を、いちばん近い同じ路線の行き先 (駅) ごとにまとめ、駅ごとに 1 地区。
    // 地区に入る床のまとまり: 駅の乗り場の節点から、トラムの辺と乗らないエレベーター (NoRide。グラフに辺が無い) を使わずにつながる節点を、いちばん近い駅から
    // 順に集め (多点のダイクストラ)、節点の階のまとまりをその地区に入れる (1 つのまとまりは 1 つの地区だけ)。
    // 名前: 駅の乗り場の階のまとまりの名前 (StationFloor.GroupName。「付近」は元のまとまりの名前)。無ければ、またはほかの地区と名前が重なれば駅の行き先の名前 (鍵のまま。表示の側で訳す)。
    // 一覧に出る階の無い地区は作らず、その駅は乗り場の階のまとまりを持つ地区に入れる。
    // main: 輪の路線の駅の地区なので全部 true。都市 (LandingZone) の、輪になっている路線 (どの駅からもほかの全部の駅へ行ける) だけで作る
    public const double DistrictNearRadius = 300;

    public sealed record District(string Id, string Name, string NameKey, List<string> Groups, List<string> Stations,
        (double MinX, double MinY, double MaxX, double MaxY) Bounds, (double X, double Y) Center, bool Main)
    {
        // 乗り場の節点 (JSON には書かない)。New Babbage の乗り場は CryGUID がほかの区画の乗り場と同じなので、CryGUID でなく節点で地区を引く
        public IReadOnlyList<int> StationNodes { get; init; } = Array.Empty<int>();
    }

    public static List<District> Districts(StationTree tree, IReadOnlyList<StationFloor> floors, StationGraph g)
    {
        var res = new List<District>();
        // 地区を作るのは都市 (星図の種類 LandingZone) だけ (2026-11-01)。ステーション・施設の中のトラムは地区にしない。
        // Orbituary (RR_P3_LEO。星図の種類 Manmade) は、階の行き先 (@ui_interactor_floor_01・02) の 2 駅のトラムで、どちらも「Contested Access 04」の地区になり、
        // 片方は床のまとまりの無い点だけの地区だった。Onyx Facility (Stanton1d_ASD_Delve_Facility_009。Outpost) も、施設の中のトラムの駅で 4 地区になっていた
        if (tree.Root.Place.Type != "LandingZone") return res;
        var dests = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
        // 輪の路線だけ: 区間 (Legs) を駅 (乗り場のいちばん近い同じ路線の行き先) から駅への向きの辺として、どの駅からもほかの全部の駅へ行ける路線。
        // ContestedZone_RR_P3_LEO は星図の種類が LandingZone だが、トラムは Floor 01 から Floor 02 への片道だけで輪になっていない (地区が 2 つとも「Contested Access 04」になっていた)
        string StationOf(Line l, int gi) { var (_, gw) = l.Gateways[gi]; var st = StopName(dests, l.Key, gw.Pos!.Value); return st.Length > 0 ? st : gw.Guid; }
        bool IsLoop(Line l)
        {
            var next = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            for (int i = 0; i < l.Gateways.Count; i++) next.TryAdd(StationOf(l, i), new HashSet<string>(StringComparer.Ordinal));
            foreach (var (a, b, _, _) in l.Legs) { var sa = StationOf(l, a); var sb = StationOf(l, b); if (sa != sb) next[sa].Add(sb); }
            if (next.Count < 2) return false;
            foreach (var s0 in next.Keys)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { s0 }; var q = new Queue<string>(); q.Enqueue(s0);
                while (q.Count > 0) foreach (var v in next[q.Dequeue()]) if (seen.Add(v)) q.Enqueue(v);
                if (seen.Count != next.Count) return false;
            }
            return true;
        }
        var lines = TransitLines(tree).Where(l => l.Tram && IsLoop(l)).ToList();
        if (lines.Count == 0) return res;
        var stations = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var l in lines)
            foreach (var (inst, gw) in l.Gateways)
            {
                var stop = StopName(dests, l.Key, gw.Pos!.Value);
                if (stop.Length == 0) stop = gw.Guid;
                if (!g.ById.TryGetValue($"g:{inst}:{gw.Guid}", out var node)) continue;
                if (!stations.TryGetValue(stop, out var lst)) stations[stop] = lst = new List<int>();
                lst.Add(node);
            }
        // 多点のダイクストラ (トラムの辺を使わない)
        var owner = new int[g.Nodes.Count]; Array.Fill(owner, -1);
        var dist = new double[g.Nodes.Count]; Array.Fill(dist, double.PositiveInfinity);
        var adj = Enumerable.Range(0, g.Nodes.Count).Select(_ => new List<(int, double)>()).ToList();
        foreach (var e in g.Edges)
        {
            if (e.Kind == "tram") continue;
            adj[e.A].Add((e.B, e.Cost));
            if (!e.OneWay) adj[e.B].Add((e.A, e.Cost));
        }
        var keys = stations.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        var pq = new PriorityQueue<int, double>();
        for (int k = 0; k < keys.Count; k++)
            foreach (var n in stations[keys[k]]) { dist[n] = 0; owner[n] = k; pq.Enqueue(n, 0); }
        while (pq.TryDequeue(out var u, out var d))
        {
            if (d > dist[u]) continue;
            foreach (var (v, c) in adj[u])
                if (d + c < dist[v]) { dist[v] = d + c; owner[v] = owner[u]; pq.Enqueue(v, d + c); }
        }
        var floorById = floors.ToDictionary(f => f.Id, StringComparer.Ordinal);
        // まとまりごとに、節点のいちばん多い地区へ
        var groupOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var grp in Enumerable.Range(0, g.Nodes.Count).Where(i => owner[i] >= 0 && floorById.ContainsKey(g.Nodes[i].Floor) && floorById[g.Nodes[i].Floor].Group.Length > 0)
                     .GroupBy(i => floorById[g.Nodes[i].Floor].Group))
            groupOwner[grp.Key] = grp.GroupBy(i => owner[i]).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First().Key;
        // 歩いてつながらなかった、一覧に出すまとまりは、いちばん近い地区 (地区のまとまりの箱との xy の間が DistrictNearRadius 以内) に入れる
        // (Orison の Crusader Showroom・Inspiration Station は Cloudview Center のまとまりから歩きの辺でつながっていなかった)
        var boxes = Enumerable.Range(0, keys.Count).Select(k => floors.Where(f => f.Bounds != null && groupOwner.TryGetValue(f.Group, out var o) && o == k).Select(f => f.Bounds!.Value).ToList()).ToList();
        foreach (var grp in floors.Where(f => f.Listed && f.Group.Length > 0 && f.Bounds != null && !groupOwner.ContainsKey(f.Group)).GroupBy(f => f.Group))
        {
            var b0 = grp.First().Bounds!.Value;
            int bestK = -1; double bd = DistrictNearRadius;
            for (int k = 0; k < keys.Count; k++)
                foreach (var b1 in boxes[k])
                {
                    double dx = Math.Max(0, Math.Max(b1.MinX - b0.MaxX, b0.MinX - b1.MaxX)), dy = Math.Max(0, Math.Max(b1.MinY - b0.MaxY, b0.MinY - b1.MaxY));
                    var d = Math.Sqrt(dx * dx + dy * dy);
                    if (d <= bd) { bd = d; bestK = k; }
                }
            if (bestK >= 0) groupOwner[grp.Key] = bestK;
        }
        // 床のまとまりの無い地区は作らない (親の決定 (b) 2026-11-01。押すと空の地図になっていた)。
        // 床のまとまりの無い地区 = 入る床のまとまりに一覧に出る階が 1 つも無い地区。その駅は、駅の乗り場の階のまとまりを持つ別の地区 (一覧に出る階のある地区) に入れる。
        // 入れる先が無ければ地区にしない (その駅は地区に入らない)。
        // Lorville: City Gates 05 と 06、Metro Center と Leavsden Station は、乗り場の階が同じ床のまとまり (歩いてつながる) にあり、片方の地区が空だった。
        // Central Business District は乗り場が階の上に無かった。New Babbage の Tobin Center は、まとまりに一覧に出る階が無かった
        var groupsOf = Enumerable.Range(0, keys.Count).Select(k => groupOwner.Where(kv => kv.Value == k).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList()).ToList();
        var hasFloor = groupsOf.Select(gs => floors.Any(f => f.Listed && gs.Contains(f.Group))).ToList();
        var nameOf = Enumerable.Range(0, keys.Count).Select(k => stations[keys[k]].Select(n => g.Nodes[n].Floor).Where(floorById.ContainsKey).Select(id => floorById[id].GroupName)
            .Where(n => n.Length > 0).GroupBy(n => n).OrderByDescending(x => x.Count()).Select(x => x.Key).FirstOrDefault() ?? "").ToList();
        // 名前がほかの地区と重なる地区は、駅の行き先の名前 (鍵のまま。表示の側で訳す) にする (Lorville の City Gates 06 の地区が「City Gates 05」、
        // Leavsden Station と Metro Center の地区が両方「Maria Pure of Heart」になっていた)。重なりは、まとめる前の地区どうしで見る
        var clash = nameOf.Where(n => n.Length > 0).GroupBy(n => n, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var stNodesOf = Enumerable.Range(0, keys.Count).Select(k => new List<int>(stations[keys[k]])).ToList();
        for (int k = 0; k < keys.Count; k++)
        {
            if (hasFloor[k]) continue;
            var into = stations[keys[k]].Select(n => g.Nodes[n].Floor).Where(floorById.ContainsKey).Select(id => floorById[id].Group)
                .Where(gr => gr.Length > 0 && groupOwner.TryGetValue(gr, out var o) && o != k && hasFloor[o]).Select(gr => groupOwner[gr]).GroupBy(o => o)
                .OrderByDescending(x => x.Count()).ThenBy(x => x.Key).Select(x => (int?)x.Key).FirstOrDefault();
            if (into is int k2) stNodesOf[k2].AddRange(stations[keys[k]]);
        }
        for (int k = 0; k < keys.Count; k++)
        {
            if (!hasFloor[k]) continue;
            var groups = groupsOf[k];
            var gfl = floors.Where(f => groups.Contains(f.Group) && f.Bounds != null).ToList();
            var stNodes = stNodesOf[k];
            var name = clash.Contains(nameOf[k]) ? "" : nameOf[k];
            (double, double, double, double) b = gfl.Count > 0
                ? (gfl.Min(f => f.Bounds!.Value.MinX), gfl.Min(f => f.Bounds!.Value.MinY), gfl.Max(f => f.Bounds!.Value.MaxX), gfl.Max(f => f.Bounds!.Value.MaxY))
                : (stNodes.Min(n => g.Nodes[n].Pos.X), stNodes.Min(n => g.Nodes[n].Pos.Y), stNodes.Max(n => g.Nodes[n].Pos.X), stNodes.Max(n => g.Nodes[n].Pos.Y));
            res.Add(new District($"d{res.Count}", name.Length > 0 ? name : keys[k], keys[k], groups, stNodes.Select(n => g.Nodes[n].Guid).Distinct().ToList(),
                b, ((b.Item1 + b.Item3) / 2, (b.Item2 + b.Item4) / 2), true) { StationNodes = stNodes.Distinct().ToList() });
        }
        return res;
    }

    // 局所の点に近い節点 (同じ階を優先。階が無ければ全部から)。kinds があればその種類だけ。
    // いちばん近い節点から NearSlack (5 m) 以内の候補のうち、つながっている節点がいちばん多いもの (床の小さな切れ端の経由点を出発に選ばないため。
    // Orison の August Spaceport のロビーの点で、10 節点だけの切れ端の経由点がいちばん近かった)
    public const double NearSlack = 5;

    // 行き先の点 (sctransitdestination) の階 (2026-11-02、親の指示): 点の真下で、点の高さから DestFloorBelow (1.5 m) 下までに床のある階を先に探す。
    // 無ければ前と同じく、行き先 − 1.5 m の階、それも無ければ行き先の高さの階。
    // RR_CRU_L5 の Galleria 行きの行き先 (−512.29 m) は、止まる床 (−513.04 m、乗り場の階) の 0.75 m 上にある。前は − 1.5 m (−513.79 m) で
    // 探したので、止まる床が FloorAtXY の下の許し (0.6 m) から外れ、6 m 下の階 (−519.04 m) に当たっていた (RR_HUR_L3 も同じ。行き先 −105.0 m・止まる床 −105.75 m)
    public const double DestFloorBelow = 1.5;
    public static StationFloor? DestFloor(IReadOnlyList<StationFloor> floors, (double X, double Y, double Z) p) =>
        StationFloorBuilder.FloorAtXY(floors, p.X, p.Y, p.Z, tolBelow: 0, maxAbove: DestFloorBelow)
        ?? StationFloorBuilder.FloorAtXY(floors, p.X, p.Y, p.Z - 1.5) ?? StationFloorBuilder.FloorAtXY(floors, p.X, p.Y, p.Z);

    // 重ねる順番 (利用者の決定 2026-11-01): ステーションの階は、下から 精錬所 → ハンガー → ガレリア → 配送センター の順に重ねる (データの高さの上下は見ない)。
    // 見分けは、エレベーター (トラムでない路線) の乗り場のいちばん近い同じ路線の行き先の鍵 (StackKeys)。1 本の路線で 2 つ以上の鍵が当たったときだけ、
    // 鍵の順に、その乗り場の階の床のまとまりを重ね、まとまりの中は下からの番号 (Level) の順。返すのは階の Id → (重ねる組の Id、組の中の下からの番号 1〜)
    public static readonly string[] StackKeys = ["@ui_dest_refinery", "@ui_dest_entry_exit", "@ui_dest_commercial", "@ui_dest_cargo"];
    // Pyro のステーションの行き先の鍵で、StackKeys の鍵と訳で同じ意味のもの → StackKeys の番号 (2026-11-01。global.ini と translations.db で確かめた):
    //   @area_name_refinery「Refinery Deck／精製デッキ」= @ui_dest_refinery「Refinery／精製所」、@area_name_cargo「Cargo Deck／貨物デッキ」= @ui_dest_cargo「Cargo Center／貨物センター」。
    //   @ui_dest_entry_pyro「Entrance／入口」は @ui_dest_entry_exit「Hangars & Habs／ハンガー&居住区」と訳が違うので入れない
    public static readonly IReadOnlyDictionary<string, int> StackKeyAliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["@area_name_refinery"] = 0, ["@area_name_cargo"] = 3,
    };
    private static int StackIndex(string stop) =>
        Array.FindIndex(StackKeys, s => string.Equals(s, stop, StringComparison.OrdinalIgnoreCase)) is var k && k >= 0 ? k : StackKeyAliases.TryGetValue(stop, out var a) ? a : -1;

    // nameKeyOverrides (確かめ用。無くてよい): 名前で付いた鍵が、乗り場・行き先の点で付いた別の鍵を置き換えた階 (階の Id、前の鍵、名前の鍵) を足す
    public static Dictionary<string, (string Stack, int Level)> StackOrder(StationTree tree, IReadOnlyList<StationFloor> floors,
        List<(string Floor, int Before, int ByName)>? nameKeyOverrides = null)
    {
        var res = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        var dests = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
        int n = 0;
        foreach (var l in TransitLines(tree).Where(l => !l.Tram))
        {
            // 階ごとの鍵 (その階の乗り場の行き先の鍵。2 つ以上なら小さいほう)
            var floorKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (_, g) in l.Gateways)
            {
                var k = StackIndex(StopName(dests, l.Key, g.Pos!.Value));
                if (k < 0) continue;
                var f = StationFloorBuilder.FloorAtXY(floors, g.Pos!.Value.X, g.Pos!.Value.Y, g.Pos!.Value.Z) ?? GatewayFloorByBounds(floors, g.Pos!.Value);
                if (f == null || f.Group.Length == 0) continue;
                floorKey[f.Id] = floorKey.TryGetValue(f.Id, out var o) ? Math.Min(o, k) : k;
            }
            // (2026-11-02) その路線の行き先の点 (同じ路線のタグを持つ sctransitdestination。タグの無い路線は StopName と同じくタグの無い行き先) の階にも鍵を付ける。
            // 行き先の点の階は TransitList と同じ (DestFloor)。RR_CRU_L5 で、Galleria 行き (@ui_dest_commercial) の乗り場 (−513.04 m) は
            // 「Clinic」の階に当たり、行き先の点 (−512.29 m) の当たる「Galleria」の階 (−519 m) に鍵が無く、Lobby の鍵を借りて Lobby の下に重なっていた
            var untaggedLine = l.Key.StartsWith("inst:", StringComparison.Ordinal);
            foreach (var d in dests.Where(x => untaggedLine ? x.Tags.Count == 0 && !NoNameKeys.Contains(x.Name) : x.Tags.Any(t => string.Equals(t, l.Key, StringComparison.OrdinalIgnoreCase))))
            {
                var k = StackIndex(d.Name);
                if (k < 0) continue;
                var p = d.Pos!.Value;
                var f = DestFloor(floors, p);
                if (f == null || f.Group.Length == 0) continue;
                floorKey[f.Id] = floorKey.TryGetValue(f.Id, out var o) ? Math.Min(o, k) : k;
            }
            // (2026-11-02、利用者の指示「”Galleria の階は行き先が分からず” Galleria と繋げていいと思う。…名前での紐づけ」) この路線の行き先にある鍵 (乗り場・行き先の点で
            // 当たった鍵) について、名前で結び付いた階 (StationFloor.NameStackKey。NameFloors) にもその鍵を付ける。乗り場・行き先の点で別の鍵が付いていても、
            // 名前で付いた鍵にする (名前で付いた階をその行き先の階とみなす)。RR_CRU_L5 の「Galleria」の階 (−519 m) は乗り場が無く、行き先の点も当たらず、
            // Lobby の鍵を借りて Lobby の下に重なっていた
            var served = floorKey.Values.ToHashSet();
            foreach (var f in floors)
            {
                if (f.NameStackKey < 0 || !served.Contains(f.NameStackKey) || f.Group.Length == 0) continue;
                if (floorKey.TryGetValue(f.Id, out var o) && o != f.NameStackKey) nameKeyOverrides?.Add((f.Id, o, f.NameStackKey));
                floorKey[f.Id] = f.NameStackKey;
            }
            // (2026-11-02、利用者の決定「エレベーターとつながった名前優先」) 階段でつながるエレベーターの止まる階 (StairPartner。NameByStairs) の名前を借りた階は、
            // その階の鍵を借りる (上下は同じまとまりの中の下からの番号、つまりデータの高さで決まる)
            foreach (var f in floors)
                if (f.StairPartner.Length > 0 && !floorKey.ContainsKey(f.Id) && floorKey.TryGetValue(f.StairPartner, out var pk)) floorKey[f.Id] = pk;
            if (floorKey.Values.Distinct().Count() < 2) continue;
            // (2026-11-01、検証の指摘 B) 階の単位で鍵の順に重ねる。前はまとまりの単位で重ね、2 つの鍵の乗り場が同じまとまりにあると後の鍵を飛ばし、
            // まとまりの中は高さの順になっていた (RR_ARC_LEO で Cargo Center が Lobby の下、RR_CRU_L5・RR_HUR_L3 で Galleria が Lobby の下)。
            // 鍵の無い階 (乗り場の無い階) は、同じまとまりの下からの番号 (Level) でいちばん近い下の鍵のある階の鍵 (下に無ければ上の鍵のある階の鍵) にする。
            // 並び: 鍵 → まとまりのいちばん低い高さ → まとまりの中の下からの番号
            var groups = floors.Where(f => floorKey.ContainsKey(f.Id)).Select(f => f.Group).Distinct(StringComparer.Ordinal)
                .Where(grp => !floors.Where(f => f.Group == grp).Any(f => res.ContainsKey(f.Id))).ToList();
            var order = new List<(int Key, double GroupZ, int Level, StationFloor F)>();
            foreach (var grp in groups)
            {
                var gf = floors.Where(f => f.Group == grp).OrderBy(f => f.Level).ToList();
                var gz = gf.Min(f => f.ZMin);
                for (int i = 0; i < gf.Count; i++)
                {
                    int key = -1;
                    for (int j = i; j >= 0 && key < 0; j--) if (floorKey.TryGetValue(gf[j].Id, out var kb)) key = kb;
                    for (int j = i + 1; j < gf.Count && key < 0; j++) if (floorKey.TryGetValue(gf[j].Id, out var ka)) key = ka;
                    order.Add((key, gz, gf[i].Level, gf[i]));
                }
            }
            var id = $"k{n++}"; int lv = 0;
            foreach (var t in order.OrderBy(t => t.Key).ThenBy(t => t.GroupZ).ThenBy(t => t.Level)) res[t.F.Id] = (id, ++lv);
        }
        return res;
    }

    // (2026-11-02、利用者の決定「エレベーターとつながった名前優先」(案 A)) エレベーターが止まらず、行き先の名前も無い階 (名前がまとまりの名前・「付近」・場所の名前・
    // 種類の言葉・無し) で、階段 (stairs の from・to。層の決まりで分かれた階どうし) でエレベーターの止まる階 (名前がエレベーターの行き先から付いた階) とつながる階に、
    // その階の名前を付ける (NameSource "stairs"、StairPartner)。一覧に出ない階は通り道とみなし、その先の一覧に出る階を相手にする (画面の担当の決め方と同じ)。
    // 一覧に出る階で相手でないものの先へはたどらない。相手が 2 つ以上あるときは、たどった階段の数の少ないもの、次に高さ (Z) の差の小さいもの。
    // 一覧に出すか (Listed) は変えない。RR_CRU_L5・RR_HUR_L3 の下の Galleria の層 (Galleria の止まる階と階段でつながる) が、まとまりの名前
    // 「CRU-L5 Beautiful Glen Clinic」になり、重ねる順で Lobby の下に入っていた。
    // changes (確かめ用。無くてよい): (階の Id、前の名前、相手の階の Id、相手の名前、相手の候補の数) を足す
    public static void NameByStairs(StationTree tree, IReadOnlyList<StationFloor> floors, IReadOnlyList<StationStair> stairs,
        List<(string Floor, string Before, string Partner, string Name, int Candidates)>? changes = null)
    {
        var byId = floors.ToDictionary(f => f.Id, StringComparer.Ordinal);
        // エレベーター (トラムでない路線) の止まる階: 乗り場の階 (TransitList と同じ当て方) と、その路線の行き先の点の階 (DestFloor)
        var served = new HashSet<string>(StringComparer.Ordinal);
        var dests = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
        foreach (var l in TransitLines(tree).Where(l => !l.Tram && !l.NoRide))
        {
            foreach (var (_, g) in l.Gateways)
                if ((StationFloorBuilder.FloorAtXY(floors, g.Pos!.Value.X, g.Pos!.Value.Y, g.Pos!.Value.Z) ?? GatewayFloorByBounds(floors, g.Pos!.Value)) is { } f) served.Add(f.Id);
            var untagged = l.Key.StartsWith("inst:", StringComparison.Ordinal);
            foreach (var d in dests.Where(x => untagged ? x.Tags.Count == 0 && !NoNameKeys.Contains(x.Name) : x.Tags.Any(t => string.Equals(t, l.Key, StringComparison.OrdinalIgnoreCase))))
                if (DestFloor(floors, d.Pos!.Value) is { } f) served.Add(f.Id);
        }
        bool IsPartner(StationFloor f) => served.Contains(f.Id) && f.NameSource is "elevator" or "destination" && f.DisplayName.Length > 0;
        // (2026-11-02、利用者の決定「踊り場を含む階段でつながる階はサブネームの決まりが優先」) 踊り場を含む階段 (StationStair.Landing) の両端の階は対象にせず、その階段はたどらない
        var byLanding = stairs.Where(s => s.Landing).SelectMany(s => new[] { s.From, s.To }).ToHashSet(StringComparer.Ordinal);
        bool IsTarget(StationFloor f) => !served.Contains(f.Id) && !byLanding.Contains(f.Id) && f.Group.Length > 0 && f.NameSource is "group" or "near" or "place" or "type" or "none";
        var adj = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in stairs)
        {
            if (s.Landing) continue;
            if (s.From == s.To || !byId.ContainsKey(s.From) || !byId.ContainsKey(s.To)) continue;
            (adj.TryGetValue(s.From, out var a) ? a : adj[s.From] = new()).Add(s.To);
            (adj.TryGetValue(s.To, out var b) ? b : adj[s.To] = new()).Add(s.From);
        }
        var picks = new List<(StationFloor F, StationFloor P, int Cands)>();
        foreach (var f in floors.Where(IsTarget))
        {
            if (!adj.ContainsKey(f.Id)) continue;
            var dist = new Dictionary<string, int>(StringComparer.Ordinal) { [f.Id] = 0 };
            var q = new Queue<string>(); q.Enqueue(f.Id);
            var cands = new List<(StationFloor P, int D)>();
            while (q.Count > 0)
            {
                var cur = q.Dequeue();
                foreach (var nx in adj.GetValueOrDefault(cur) ?? new())
                {
                    if (dist.ContainsKey(nx)) continue;
                    dist[nx] = dist[cur] + 1;
                    var nf = byId[nx];
                    if (nf.Listed) { if (IsPartner(nf)) cands.Add((nf, dist[nx])); continue; }
                    q.Enqueue(nx);
                }
            }
            if (cands.Count == 0) continue;
            var pick = cands.OrderBy(c => c.D).ThenBy(c => Math.Abs(c.P.Z - f.Z)).ThenBy(c => c.P.Id, StringComparer.Ordinal).First().P;
            picks.Add((f, pick, cands.Select(c => c.P.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count()));
        }
        // 名前を変えるのは全部の相手を決めてから (変えた階を相手にしない)
        foreach (var (f, p, n) in picks)
        {
            changes?.Add((f.Id, f.DisplayName, p.Id, p.DisplayName, n));
            f.Name = f.DisplayName = p.DisplayName;
            f.NameSource = "stairs";
            f.StairPartner = p.Id;
            f.NameStackKey = p.NameStackKey;
        }
    }

    public static int NearestNode(StationGraph g, IReadOnlyList<StationFloor> floors, (double X, double Y, double Z) p, params string[] kinds)
    {
        var f = StationFloorBuilder.FloorAtXY(floors, p.X, p.Y, p.Z)?.Id;
        var cand = new List<(int I, double D)>();
        for (int pass = 0; pass < 2 && cand.Count == 0; pass++)
            for (int i = 0; i < g.Nodes.Count; i++)
            {
                var n = g.Nodes[i];
                if (kinds.Length > 0 && !kinds.Contains(n.Kind)) continue;
                if (pass == 0 && (f == null || n.Floor != f)) continue;
                cand.Add((i, Dist(n.Pos, p)));
            }
        if (cand.Count == 0) return -1;
        var bd = cand.Min(c => c.D);
        var size = g.ComponentSizes();
        return cand.Where(c => c.D <= bd + NearSlack).OrderByDescending(c => size[c.I]).ThenBy(c => c.D).First().I;
    }

    // 道順を区切りにまとめる: 続く徒歩 (walk・door・rmxml) は 1 つの「徒歩」、エレベーター・トラム・階段は乗った (上った) ところから降りたところまで
    public static List<StationRouteStep> Steps(StationGraph g, IReadOnlyList<StationFloor> floors, List<(int From, int To, StationEdge Edge)> path)
    {
        var res = new List<StationRouteStep>();
        string FloorName(int node) => floors.FirstOrDefault(f => f.Id == g.Nodes[node].Floor)?.Name ?? "";
        foreach (var (from, to, e) in path)
        {
            var kind = e.Kind is "walk" or "door" or "rmxml" or "floor" ? "walk" : e.Kind;
            var dist = kind is "walk" or "stair" ? Dist(g.Nodes[from].Pos, g.Nodes[to].Pos) : 0;
            if (res.Count > 0 && res[^1].Kind == kind && (kind is "walk" or "stair" || res[^1].Line == e.Line && res[^1].Dir == e.Dir))
            {
                var last = res[^1];
                res[^1] = last with { Distance = last.Distance + dist, To = to, Floor = FloorName(to), Stop = kind == "tram" ? StopOf(g, to) : last.Stop };
                continue;
            }
            res.Add(new StationRouteStep(kind, dist, from, to, e.Line, e.Dir, kind == "tram" ? StopOf(g, to) : "", FloorName(to)));
        }
        return res;
    }

    // トラムの乗り場の節点の行き先の名前 (TransitList と同じ引き方は Build に持たないので、節点の名前が空なら Guid の先頭)
    private static string StopOf(StationGraph g, int node) => g.Nodes[node].Name.Length > 0 ? g.Nodes[node].Name : g.Nodes[node].Guid;

    // 「徒歩 → シャトル（A回り）で ◯ → エレベーターで ◯ へ → 店」の文 (階の名前はデータの名前のまま。語を足さない。2026-11-01)。stopNames は乗り場の (インスタンス, CryGUID) → 行き先の名前
    public static string Describe(StationGraph g, IReadOnlyList<StationRouteStep> steps, string target,
        IReadOnlyDictionary<(int, string), string>? stopNames = null)
    {
        var parts = new List<string>();
        foreach (var s in steps)
        {
            var to = g.Nodes[s.To];
            var stop = stopNames != null && stopNames.TryGetValue((to.Instance, to.Guid), out var sn) && sn.Length > 0 ? sn : s.Stop;
            parts.Add(s.Kind switch
            {
                "walk" => $"徒歩 {s.Distance:F0} m",
                "elevator" => $"エレベーターで {s.Floor} へ",
                // 回りの向き (Orison の道筋の名前 Looping_A_・B_) が無い路線 (New Babbage・Lorville) は向きを書かない
                "tram" => s.Dir.Length > 0 ? $"シャトル（{s.Dir}回り）で {stop}" : $"シャトルで {stop}",
                "stair" => $"階段で {s.Floor} へ",
                _ => s.Kind,
            });
        }
        parts.Add(target);
        return string.Join(" → ", parts);
    }
}

// 段階 3・4 の結果の JSON (計画 3 の形)。保存の場所・照合は段階 5 (ここでは文字列を作るだけ)
internal static class StationMapJson
{
    public const string App = "StarCitizenJapaneseTextCreater.stationmap";
    // 一覧に出す階の、描く床の形の面積の下限 (m²)。値: Checkmate の Cargo Deck の 3 階 (9 m²)・Contested Zone の 8 階 (24 m²) などの小さな切れ端は出さず、
    // 貨物のエレベーターの階 (Ext. Freight の 1 階、47〜50 m²。乗り場と行き先がある) は出す値として決めた
    public const double MinListedFloorArea = 30;
    public const int Format = 18;   // 2: 道順のグラフの経由点を waypoints にまとめた形 (Write の graph の説明)。3: 階・区画の名前を訳と星図の場所の名前で付けるようにした (取り込みが NameFloors・NameSections に localize・placeNames を渡す。2026-11-01)。4: 区画の name を表示の名前にし、Child の entityName を entity に移した。階の名前を代表の 1 つ・訳のある名前だけにした (2026-11-01)。5: 地区 (districts) を足した (2026-11-01)。6: transit に lineInternal を足し、タグの無いエレベーターの行き先の名前を引くようにした (2026-11-01)。7: floors に reachable (乗り場からたどれない階は false。その階の区画の多角形は書かない)、transit のエレベーターに place (乗り場を含む区画の名前) を足した。床も店も無い箱だけの区画の多角形を書かないようにした (2026-11-01)。8: 階を真上から見た重なりで分けるようにした (Levels)。floors に implemented を足し、transit の kind (elevator・elevator-noride) を実装されているかの判定で決めるようにした (2026-11-01) 9: transit のトラムの乗り場に nextGateway・nextStop・nextDistrict (次の駅)、floors に stackGroup・stackLevel (行き先の鍵で決めた重ねる順番) を足した (2026-11-01) 10: 階を、数段の階段 (1.3 m 未満) でつながる床を高さの範囲 3 m 未満のあいだ足す作り方にした (利用者の規則、2026-11-01)。 11: 何も無い層を一覧から外し、階の名前をデータの名前だけにした。店の名前に area_label・販売データの名前を足した (2026-11-01)。 12: 頭に一覧に出る階の数 (listedFloors。StationMapExports.Finish が書く) を足した。 13: 乗り場の判定・地区の条件を直した (2026-11-01)。 14: 通路の部品 (corridor_*) を床にし、店の名前を階の名前・括弧の候補に使わないようにし、重ねる順を階の単位にし、新しい乗り物の仕組み (sctransport*) の乗り場を拾うようにした (2026-11-01)。 15: stairs に outline (階段の板の真上から見た形) を足し、明らかに階が違う階段の両端の床を別の層にし、エレベーターとつながった階の名前を階段でつながる階に付けるようにした (2026-11-02)。 16: 床・階段を部品の形 (GLB の三角形の上向きの面) で判定し、板を実際の面の高さで作り、部品の名前と種類を表に登録するようにした (2026-11-02)。 17: 区画の floors の環を、板を格子に塗った輪郭から板ごとの凸包そのものにした (四角い部品は四角のまま。環どうしは重なることがある。2026-11-02、利用者の決定「A」)。 18: 端末 (terminals)・外の設備 (exterior: pads・hangars・freight・decks) を足し、木から外す Child (HarvestableObjectContainer など) のうち部分木に床・階段の部品のあるものを外さないようにした (excludedChildren に外した Child を書く。2026-11-02)。

    public static string Write(StationIndex index, StationTree tree, IReadOnlyList<StationFloor> floors, IReadOnlyList<StationSection> sections,
        IReadOnlyList<StationStair> stairs, IReadOnlyList<StationTransit> transit, StationGraph graph, StarRotRule rule = StationIndex.DefaultRule)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            var root = tree.Root;
            var gx = root.GlobalXf(rule);
            w.WriteStartObject();
            w.WriteString("app", App);
            w.WriteNumber("format", Format);
            w.WriteString("record", root.Place.Record);
            w.WriteString("system", root.Place.System);
            w.WriteString("name", root.Place.Record);
            w.WriteStartObject("root");
            w.WriteString("socpak", root.Socpak);
            w.WriteStartArray("pos"); w.WriteNumberValue(gx.X); w.WriteNumberValue(gx.Y); w.WriteNumberValue(gx.Z); w.WriteEndArray();
            w.WriteStartArray("rot"); w.WriteNumberValue(gx.Qw); w.WriteNumberValue(gx.Qx); w.WriteNumberValue(gx.Qy); w.WriteNumberValue(gx.Qz); w.WriteEndArray();
            w.WriteString("rotRule", rule.ToString());
            // 画面で StationRoot を組み直して ToLocal・CorrectedTheta をそのまま使うための値 (丸めない): 恒星の Child の位置と回転 (xml の成分のまま)、
            // PreSpin・PostSpin ([x,y,z,qw,qx,qy,qz])、自転する天体 (無ければ書かない。uuid は starMapRecord、rotHours は planetRotationSpeed)
            w.WriteStartArray("starPos"); w.WriteNumberValue(root.StarPos.X); w.WriteNumberValue(root.StarPos.Y); w.WriteNumberValue(root.StarPos.Z); w.WriteEndArray();
            w.WriteStartArray("starRot"); w.WriteNumberValue(root.StarRotRaw.W); w.WriteNumberValue(root.StarRotRaw.X); w.WriteNumberValue(root.StarRotRaw.Y); w.WriteNumberValue(root.StarRotRaw.Z); w.WriteEndArray();
            foreach (var (name, xf) in new[] { ("preSpin", root.PreSpin), ("postSpin", root.PostSpin) })
            {
                w.WriteStartArray(name);
                foreach (var v in new[] { xf.X, xf.Y, xf.Z, xf.Qw, xf.Qx, xf.Qy, xf.Qz }) w.WriteNumberValue(v);
                w.WriteEndArray();
            }
            if (root.Spins)
            {
                w.WriteStartObject("spin");
                w.WriteString("socpak", root.SpinSocpak);
                w.WriteString("uuid", root.SpinUuid);
                if (root.SpinRotHours is { } rh && double.IsFinite(rh)) w.WriteNumber("rotHours", rh); else w.WriteNull("rotHours");
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("p4k");
            w.WriteString("path", index.P4kPath);
            w.WriteNumber("size", index.P4kSize);
            w.WriteNumber("writeUtcTicks", index.P4kWriteUtcTicks);
            w.WriteEndObject();
            w.WriteString("exportedUtc", DateTime.UtcNow.ToString("o"));
            // 照合 (CRC・床の CGF) は段階 5。ここでは木の socpak と p4k の上の大きさだけ (crc は null)
            w.WriteStartArray("sources");
            foreach (var sp in tree.Instances.Select(i => i.Socpak).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("path", sp);
                if (index.Entries.TryGetValue(sp, out var e)) w.WriteNumber("size", e.Size); else w.WriteNull("size");
                w.WriteNull("crc");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // 外す決まり (StationChildFilter) に当たった Child (形 18。2026-11-02): socpak・entity (Child の entityName)・class・kept (部分木に床・階段の部品があるので外さなかったら true)
            w.WriteStartArray("excludedChildren");
            foreach (var (sp, en, cls, kept) in tree.CutChildren.Select(c => (c.Socpak, c.Entity, c.Class, false)).Concat(tree.KeptChildren.Select(c => (c.Socpak, c.Entity, c.Class, true))))
            {
                w.WriteStartObject();
                w.WriteString("socpak", sp); w.WriteString("entity", en); w.WriteString("class", cls); w.WriteBoolean("kept", kept);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("floors");
            var unreachable = floors.Where(f => !f.Reachable).Select(f => f.Id).ToHashSet();
            var shopOwners = new HashSet<int>();
            foreach (var sh in tree.Shops) for (int i = sh.Instance; i >= 0; i = tree.Instances[i].Parent) if (!shopOwners.Add(i)) break;
            var things = tree.Shops.Select(x => x.Pos).Concat(transit.Where(t => t.Kind != "destination").Select(t => t.Pos)).ToList();
            bool HasThing(StationSection sec)
            {
                if (shopOwners.Contains(sec.Instance)) return true;
                var ins = tree.Instances[sec.Instance]; var inv = ins.ToStation.Inverse(); var a = ins.MinBounds!.Value; var b = ins.MaxBounds!.Value;
                return things.Any(q => { var l = inv.Apply(q); return l.X >= a.X - 0.5 && l.X <= b.X + 0.5 && l.Y >= a.Y - 0.5 && l.Y <= b.Y + 0.5 && l.Z >= a.Z - 0.5 && l.Z <= b.Z + 0.5; });
            }
            // 階ごとの、描く床の形の面積 (区画の多角形のうち JSON に書くもの (下の sections と同じ条件) の面積 StationSection.PolygonArea の和)。
            // 一覧に出す階は、この面積が MinListedFloorArea 以上のものだけ (2026-11-01、利用者の指摘「枠の無い、意味のない階がリストにたくさんある」)
            var drawnArea = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var s in sections)
            {
                if (s.BoxFallback && !HasThing(s)) continue;
                foreach (var fid in s.Polygons.Keys)
                {
                    if (unreachable.Contains(fid)) continue;
                    drawnArea[fid] = drawnArea.GetValueOrDefault(fid) + s.PolygonArea.GetValueOrDefault(fid);
                }
            }
            // 使える物のある階 (2026-11-01、利用者の指摘「こういう何もない層は要らない」): 一覧に出す階は、次のどれかがある階だけ。道順の床としては残す (グラフは変えない)
            //   名前の候補 (StationRouteGraph.NameFloors の Labels: 区画・部屋の名札、エレベーターの行き先、店の名前) / 乗り場 (transit の destination 以外) /
            //   UsableKinds の点 (リスポン・医療・動作の場所・LandingArea など) / 店 (SCShop)
            var thingFloors = floors.Where(f => f.Labels.Count > 0).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var q in transit.Where(t => t.Kind != "destination").Select(t => t.Pos)
                .Concat(tree.Points.Where(p => p.Pos != null && StationRouteGraph.UsableKinds.Contains(p.Kind)).Select(p => p.Pos!.Value))
                .Concat(tree.Shops.Select(s => s.Pos)))
                if (StationFloorBuilder.FloorAtXY(floors, q.X, q.Y, q.Z) is { } tf) thingFloors.Add(tf.Id);
            bool IsListed(StationFloor f) => f.Listed && drawnArea.GetValueOrDefault(f.Id) >= MinListedFloorArea && thingFloors.Contains(f.Id);
            // (2026-11-01、利用者の決定) ステーション・都市でない場所 (Place.IsStation が false) で、上の規則で一覧に出る階が 1 つも無いときは、
            // 床の形のある階 (描く床の形の面積が MinListedFloorArea 以上。乗り場の高さだけの階は除く) を全部一覧に出す (残骸・イベントの台など、店・乗り場・名札の無い場所)
            // (2026-11-01、まとめて取り込んだ 88 か所のうち、一覧に出る階が 0 の場所の直し) ステーション・都市にも当てる。Security Post Kareah (9 階)・
            // Covalex Shipping Hub Gundo (6 階)・Wikelo Emporium (TheCollectorAsteroid_Stanton1/2/4) は、階の名前が全部場所の名前 (NameSource「place」) で
            // Listed が false になり、店・乗り場・名札の当たる階も無く、一覧が空だった。ContestedZone の RewardRoom 2 か所も同じく 0 だった
            // (2026-11-02、利用者の決定「A」) 一覧に出す階が 1 つも無いステーション (Kareah など) は、この決まりのまま形のある床を全部出す。
            // 同じ名前 (場所の名前) の階が並ぶので、画面の一覧 (StationMapPanel) で ▼ にまとまる
            var listAllDrawn = !floors.Any(IsListed);
            bool IsListedFinal(StationFloor f) => listAllDrawn ? !f.FromTransit && drawnArea.GetValueOrDefault(f.Id) >= MinListedFloorArea : IsListed(f);
            // 一覧に出す階の名前 (2026-11-01、利用者の指示): 一覧に出る階どうしで同じ名前 (大文字小文字は区別しない) があれば、データにある違いを括弧で添える。
            // 添えるのは、その階の候補のうち同じ名前のほかの階の候補に無いもの (候補の順で最初のもの)。候補は ① その階の名前の候補 (Labels。使う順)、
            // ② その階に描く区画の名前 (区画の名前の付いたもの。この階に描く面積の大きい順)。それも無ければ、まとまりの名前が同じ名前のほかの階と違えばそれ。
            // 高さ・番号・上下の言葉は使わない。どれも無い階はそのまま (同じ名前が残る)
            var shown = floors.ToDictionary(f => f.Id, f => f.DisplayName.Length > 0 ? f.DisplayName : f.Name, StringComparer.Ordinal);
            var secNames = new Dictionary<string, List<(string Name, double Area)>>(StringComparer.Ordinal);
            foreach (var s in sections)
            {
                if (s.DisplayName.Length == 0 || s.BoxFallback && !HasThing(s)) continue;
                // (2026-11-02) 店の名前から付けた区画の名前 (NameSource「shop」) は括弧に使わない (利用者の決定「店の名前を階の名前にも括弧の候補にも使わない」。
                // 規則 6 を外した後も、この ② から Stanton Gateway の「Galleria（Casaba Outlet）」・Area18 の「Lobby（Traveler Rentals）」のように店の名前が出ていた)
                if (s.NameSource == "shop") continue;
                foreach (var fid in s.Polygons.Keys)
                {
                    if (unreachable.Contains(fid)) continue;
                    if (!secNames.TryGetValue(fid, out var ls)) secNames[fid] = ls = new();
                    ls.Add((s.DisplayName, s.PolygonArea.GetValueOrDefault(fid)));
                }
            }
            foreach (var dup in floors.Where(IsListedFinal).GroupBy(f => shown[f.Id], StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList())
            {
                var members = dup.ToList();
                var cand = members.ToDictionary(f => f, f => f.Labels.Concat(f.StopLabels)
                    .Concat(secNames.TryGetValue(f.Id, out var ls) ? ls.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(x => x.Area)).Select(g => g.First().Name) : [])
                    .Where(c => c.Length > 0 && !string.Equals(c, shown[f.Id], StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
                var add = new Dictionary<StationFloor, string>();
                foreach (var f in members)
                {
                    var others = members.Where(o => o != f).SelectMany(o => cand[o]).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var pick = cand[f].FirstOrDefault(c => !others.Contains(c));
                    if (pick == null && f.GroupName.Length > 0 && !string.Equals(f.GroupName, shown[f.Id], StringComparison.OrdinalIgnoreCase)
                        && members.Count(o => string.Equals(o.GroupName, f.GroupName, StringComparison.OrdinalIgnoreCase)) == 1) pick = f.GroupName;
                    if (pick != null) add[f] = pick;
                }
                foreach (var (f, s) in add) shown[f.Id] = $"{shown[f.Id]}（{s}）";
            }
            var stack = StationRouteGraph.StackOrder(tree, floors);
            foreach (var f in floors)
            {
                w.WriteStartObject();
                w.WriteString("id", f.Id); w.WriteString("name", f.DisplayName.Length > 0 ? shown[f.Id] : f.Name); w.WriteString("nameSource", f.NameSource);
                Num(w, "z", f.Z); Num(w, "zMin", f.ZMin); Num(w, "zMax", f.ZMax);
                if (f.FromTransit) w.WriteBoolean("fromTransit", true);
                w.WriteStartArray("labels"); foreach (var l in f.Labels) w.WriteStringValue(l); w.WriteEndArray();
                // 床のまとまりごとの階 (2026-11-01 に足したもの): group・level (まとまりの中の下からの番号)・bounds (まとまりの xy の箱 [minX,minY,maxX,maxY])・
                // displayName (訳を引いた名前)・listed (地図の階の一覧に出すか)
                w.WriteString("group", f.Group); w.WriteNumber("level", f.Level);
                if (f.Bounds is { } fb) { w.WriteStartArray("bounds"); w.WriteNumberValue(Math.Round(fb.MinX, 2)); w.WriteNumberValue(Math.Round(fb.MinY, 2)); w.WriteNumberValue(Math.Round(fb.MaxX, 2)); w.WriteNumberValue(Math.Round(fb.MaxY, 2)); w.WriteEndArray(); }
                w.WriteString("displayName", shown[f.Id]);
                w.WriteBoolean("listed", IsListedFinal(f));
                if (f.ElevatorOnly) w.WriteBoolean("elevatorOnly", true);
                // 乗り場からたどれない階 (StationRouteGraph.MarkReachable。2026-11-01 に足したもの)。この階の区画の多角形は書かない
                if (!f.Reachable) w.WriteBoolean("reachable", false);
                // 実装されていない行き先の階 (StationRouteGraph.MarkImplemented。2026-11-01 に足したもの)
                if (!f.Implemented) w.WriteBoolean("implemented", false);
                // 重ねる順番 (形 9。StationRouteGraph.StackOrder。無い階は書かない)
                if (stack.TryGetValue(f.Id, out var sk)) { w.WriteString("stackGroup", sk.Stack); w.WriteNumber("stackLevel", sk.Level); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("sections");
            // 区画の多角形を書かないもの (2026-11-01): 乗り場からたどれない階 (StationFloor.Reachable)、
            // 板の無い区画の箱 (fallback) で、その区画か子孫に店 (SCShop) が無く、箱の中に店も乗り場 (トラム・エレベーター) も無いもの (床も店も無い箱だけの枠。利用者の指示で地図から消す)
            foreach (var s in sections)
            {
                var polys = s.BoxFallback && !HasThing(s) ? new List<KeyValuePair<string, List<List<(double X, double Y)>>>>()
                    : s.Polygons.Where(kv => !unreachable.Contains(kv.Key)).ToList();
                w.WriteStartObject();
                w.WriteString("id", "s" + s.Instance);
                // name は表示の名前 (2026-11-01 から。前は Child の entityName で、内部の名前が一覧に出ていた)。entity に Child の entityName
                w.WriteString("socpak", s.Socpak); w.WriteString("label", s.Label); w.WriteString("name", s.DisplayName); w.WriteString("entity", s.EntityName);
                // 区画のゲームの名前 (2026-11-01 に足したもの): nameKey (鍵・星図の場所・店の Name)・displayName (訳を引いた名前)・nameSource・listed (一覧に出すか)。
                // name は displayName と同じ。entity が Child の entityName (内部の名前。一覧には出さない)
                w.WriteString("nameKey", s.Name); w.WriteString("displayName", s.DisplayName); w.WriteString("nameSource", s.NameSource); w.WriteBoolean("listed", s.Listed && polys.Count > 0);
                w.WriteStartObject("obb");
                Vec(w, "center", s.Center); Vec(w, "half", s.Half); Num(w, "yaw", s.Yaw);
                w.WriteEndObject();
                if (s.BoxFallback) w.WriteBoolean("fallback", true);
                w.WriteStartObject("floors");
                foreach (var (fid, rings) in polys)
                {
                    w.WriteStartArray(fid);
                    foreach (var r in rings)
                    {
                        w.WriteStartArray();
                        foreach (var p in r) { w.WriteStartArray(); w.WriteNumberValue(Math.Round(p.X, 2)); w.WriteNumberValue(Math.Round(p.Y, 2)); w.WriteEndArray(); }
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("shops");
            foreach (var n in graph.Nodes.Where(n => n.Kind == "shop"))
            {
                w.WriteStartObject();
                w.WriteString("guid", n.Guid); w.WriteString("entity", n.Name); w.WriteString("socpak", tree.Instances[n.Instance].Socpak);
                Vec(w, "pos", n.Pos); w.WriteString("floor", n.Floor); w.WriteString("section", n.Section >= 0 ? "s" + n.Section : "");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // 端末 (形 18。2026-11-02): kind (StationTerminalKinds の鍵)・guid・class・entity (内部の名前。画面には出さない)・pos (局所)・yaw (度。z 回り)・
            // floor (StationFloorBuilder.FloorAtXY。無ければ "")・section (区画の Id。自分か祖先のインスタンスの区画、無ければ箱に入るいちばん小さい区画)・
            // node (道順のグラフの節点の番号 (graph の nodes と waypoints を合わせた並び)。StationRouteGraph.NearestNode。無ければ -1)・basis (判定の根拠)・
            // 店の端末は shop (SCShop の CryGUID)・shopEntity (SCShop の Name)
            var termSec = TerminalSections(tree, sections);
            w.WriteStartArray("terminals");
            foreach (var t in tree.Terminals)
            {
                w.WriteStartObject();
                w.WriteString("kind", t.Kind); w.WriteString("guid", t.Guid); w.WriteString("class", t.EntityClass); w.WriteString("entity", t.EntityName);
                Vec(w, "pos", t.Pos); Num(w, "yaw", Yaw(t.Rot));
                w.WriteString("floor", StationFloorBuilder.FloorAtXY(floors, t.Pos.X, t.Pos.Y, t.Pos.Z)?.Id ?? "");
                var sec = termSec(t.Instance, t.Pos);
                w.WriteString("section", sec >= 0 ? "s" + sec : "");
                w.WriteNumber("node", StationRouteGraph.NearestNode(graph, floors, t.Pos));
                w.WriteString("basis", t.Basis);
                if (t.ShopGuid.Length > 0) { w.WriteString("shop", t.ShopGuid); w.WriteString("shopEntity", t.ShopEntity); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // 外の設備 (形 18。2026-11-02。3D の表示に使う): pads (着陸パッド: key は HUDDisplayName (@ui_interactor_pad_01 など。訳はパッド01)、size は dimensions [x, y, z])、
            // hangars (ハンガーの入口: gatewaySize は metadata の GatewaySize、size はその区分 (2 Small・4 Medium・5 Large・6 XL。取り込んだ所の class 名の区分と全部一致)、door は EntityLinks の先)、
            // freight (外の貨物エレベーターの台: kiosk は端末の CryGUID、deck は外部貨物の区画の鍵 (@cargodeck_NN)、deckGuess は対応が推測 (近さ) であること、deckDistance は台と区画の中心の間 (m))、
            // decks (外部貨物の区画: key は HUDDisplayName、size は dimensions)。どれも pos は局所、rot は [w, x, y, z]
            w.WriteStartObject("exterior");
            void ExArr(string name, string kind, Action<StationExterior> extra)
            {
                w.WriteStartArray(name);
                foreach (var x in tree.Exteriors.Where(x => x.Kind == kind))
                {
                    w.WriteStartObject();
                    w.WriteString("guid", x.Guid); w.WriteString("class", x.EntityClass); w.WriteString("entity", x.EntityName);
                    Vec(w, "pos", x.Pos);
                    var q = x.Rot ?? (1, 0, 0, 0);
                    w.WriteStartArray("rot"); w.WriteNumberValue(Math.Round(q.W, 6)); w.WriteNumberValue(Math.Round(q.X, 6)); w.WriteNumberValue(Math.Round(q.Y, 6)); w.WriteNumberValue(Math.Round(q.Z, 6)); w.WriteEndArray();
                    if (x.Size is { } sz) Vec(w, "size", sz);
                    extra(x);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            ExArr("pads", "pad", x => w.WriteString("key", x.NameKey));
            ExArr("hangars", "hangar", x =>
            {
                w.WriteNumber("gatewaySize", x.GatewaySize);
                w.WriteString("size", x.GatewaySize switch { 2 => "S", 4 => "M", 5 => "L", 6 => "XL", _ => "" });
                if (x.Link.Length > 0) w.WriteString("door", x.Link);
            });
            ExArr("freight", "freight", x =>
            {
                w.WriteString("kiosk", x.Link);
                w.WriteString("deck", x.Deck);
                if (x.DeckGuess) { w.WriteBoolean("deckGuess", true); Num(w, "deckDistance", x.DeckDistance); }
            });
            ExArr("decks", "deck", x => w.WriteString("key", x.NameKey));
            w.WriteEndObject();
            w.WriteStartArray("transit");
            // エレベーターの印の名前 (2026-11-01 に足したもの): place は、行き先 (dest) が階の名前 (floor_NN・ground_floor・lobby・foyer・roof) か空の乗り場だけに書く、
            // 乗り場のインスタンスか祖先のうち、表示の名前の付いたいちばん近い区画の名前
            // (Cloudview Center のホテルのエレベーターは行き先が Floor 01〜16 で、乗り場は greencircle_int・greencircle_ext の下 → Green Circle Lobby)。無ければ書かない
            var secName = sections.Where(x => x.DisplayName.Length > 0).ToDictionary(x => x.Instance, x => x.DisplayName);
            string PlaceOf(int i) { for (; i >= 0; i = tree.Instances[i].Parent) if (secName.TryGetValue(i, out var n)) return n; return ""; }
            // 乗り場の kind (2026-11-01): グラフでエレベーターの辺のある乗り場は elevator、無い乗り場 (実装されていない) は elevator-noride
            var rideGw = graph.Edges.Where(e => e.Kind == "elevator").SelectMany(e => new[] { e.A, e.B }).Select(i => (graph.Nodes[i].Instance, graph.Nodes[i].Guid)).ToHashSet();
            // 次の駅 (2026-11-01、形 9): 回る路線 (トラム) の乗り場ごとに、道筋 (Legs) でその乗り場から出る区間の行き先の乗り場。
            // nextGateway はその乗り場の CryGUID、nextStop はその乗り場のいちばん近い同じ路線の行き先の名前 (鍵)、nextDistrict はその乗り場を含む地区の Id
            var districtList = StationRouteGraph.Districts(tree, floors, graph);
            var nextOf = new Dictionary<(int, string), (string Guid, string Stop, string District)>();
            foreach (var l in StationRouteGraph.TransitLines(tree).Where(l => l.Tram))
                foreach (var (fa, fb, _, _) in l.Legs)
                {
                    var from = l.Gateways[fa]; var to = l.Gateways[fb];
                    var stop = transit.FirstOrDefault(x => x.Kind != "destination" && x.Instance == to.Instance && x.Guid == to.Gateway.Guid)?.Stop ?? "";
                    // 節点のある乗り場は節点で引く (地区にしなかった駅 (New Babbage の Tobin Center) は、CryGUID の同じ別の地区に当てない)。節点の無い乗り場だけ CryGUID で引く
                    var dist = graph.ById.TryGetValue($"g:{to.Instance}:{to.Gateway.Guid}", out var toNode)
                        ? districtList.FirstOrDefault(d => d.StationNodes.Contains(toNode))
                        : districtList.FirstOrDefault(d => d.Stations.Contains(to.Gateway.Guid));
                    var distId = dist?.Id ?? "";
                    nextOf.TryAdd((from.Instance, from.Gateway.Guid), (to.Gateway.Guid, stop, distId));
                }
            foreach (var t in transit)
            {
                w.WriteStartObject();
                var kind = t.Kind.StartsWith("elevator", StringComparison.Ordinal) ? (rideGw.Contains((t.Instance, t.Guid)) ? "elevator" : "elevator-noride") : t.Kind;
                w.WriteString("kind", kind); w.WriteString("guid", t.Guid);
                w.WriteStartArray("tags"); foreach (var x in t.Tags) w.WriteStringValue(x); w.WriteEndArray();
                Vec(w, "pos", t.Pos); w.WriteString("floor", t.Floor); w.WriteString("dest", t.Stop); w.WriteString("line", t.Line);
                // line が内部の記号 (タグの無い路線の "inst:<番号>"・外した路線の印) なら true (画面に出さない)
                if (t.Line.StartsWith("inst:", StringComparison.Ordinal) || t.Line.Contains(':')) w.WriteBoolean("lineInternal", true);
                if (t.Kind == "tram" && nextOf.TryGetValue((t.Instance, t.Guid), out var nx))
                {
                    w.WriteString("nextGateway", nx.Guid); w.WriteString("nextStop", nx.Stop);
                    if (nx.District.Length > 0) w.WriteString("nextDistrict", nx.District);
                }
                if (t.Kind.StartsWith("elevator", StringComparison.Ordinal) && StationRouteGraph.IsLevelStop(t.Stop) && PlaceOf(t.Instance) is { Length: > 0 } pl)
                    w.WriteString("place", pl);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // トラム・シャトルの道筋 (2026-11-01 に足したもの): 路線ごとの区間 (from・to は乗り場の CryGUID、dir は向きの名前、points は道筋の点 (局所))
            // 地区 (2026-11-01 に足したもの。StationRouteGraph.Districts。地区の無いステーションは空)
            w.WriteStartArray("districts");
            foreach (var d in districtList)
            {
                w.WriteStartObject();
                w.WriteString("id", d.Id); w.WriteString("name", d.Name); w.WriteString("nameKey", d.NameKey);
                w.WriteStartArray("groups"); foreach (var x in d.Groups) w.WriteStringValue(x); w.WriteEndArray();
                w.WriteStartArray("stations"); foreach (var x in d.Stations) w.WriteStringValue(x); w.WriteEndArray();
                w.WriteStartArray("bounds"); w.WriteNumberValue(Math.Round(d.Bounds.MinX, 2)); w.WriteNumberValue(Math.Round(d.Bounds.MinY, 2)); w.WriteNumberValue(Math.Round(d.Bounds.MaxX, 2)); w.WriteNumberValue(Math.Round(d.Bounds.MaxY, 2)); w.WriteEndArray();
                w.WriteStartArray("center"); w.WriteNumberValue(Math.Round(d.Center.X, 2)); w.WriteNumberValue(Math.Round(d.Center.Y, 2)); w.WriteEndArray();
                w.WriteBoolean("main", d.Main);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("transitPaths");
            foreach (var l in StationRouteGraph.TransitLines(tree).Where(l => l.Tram))
                foreach (var (fa, fb, dir, name) in l.Legs)
                {
                    var spl = tree.Points.FirstOrDefault(p => p.Kind == "sctransitnavsplinedata" && p.Name == name && p.Path.Count >= 2);
                    if (spl == null) continue;
                    w.WriteStartObject();
                    w.WriteString("line", l.Key); w.WriteString("dir", dir); w.WriteString("spline", name);
                    w.WriteString("from", l.Gateways[fa].Gateway.Guid); w.WriteString("to", l.Gateways[fb].Gateway.Guid);
                    w.WriteStartArray("points");
                    foreach (var q in spl.Path) { w.WriteStartArray(); w.WriteNumberValue(Math.Round(q.X, 2)); w.WriteNumberValue(Math.Round(q.Y, 2)); w.WriteNumberValue(Math.Round(q.Z, 2)); w.WriteEndArray(); }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
            w.WriteEndArray();
            w.WriteStartArray("stairs");
            foreach (var s in stairs)
            {
                w.WriteStartObject();
                Vec(w, "pos", ((s.Bottom.X + s.Top.X) / 2, (s.Bottom.Y + s.Top.Y) / 2, (s.Bottom.Z + s.Top.Z) / 2));
                Vec(w, "bottom", s.Bottom); Vec(w, "top", s.Top);
                w.WriteString("from", s.From); w.WriteString("to", s.To); w.WriteString("source", s.Source);
                // 形 15: 階段の板の真上から見た形 (区画の floors の環と同じ書き方 [[[x, y], …], …])。形の無い階段は書かない (画面は印のまま)
                if (s.Outline is { Count: > 0 } ol)
                {
                    w.WriteStartArray("outline");
                    foreach (var r in ol)
                    {
                        w.WriteStartArray();
                        foreach (var p in r) { w.WriteStartArray(); w.WriteNumberValue(Math.Round(p.X, 2)); w.WriteNumberValue(Math.Round(p.Y, 2)); w.WriteEndArray(); }
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            // 道順のグラフ (形 2。経由点を軽くした形): 経由点 (kind waypoint。Id "w:<階>:<i>:<j>") は nodes に書かず waypoints に、
            //   続く同じ階の経由点ごとに {at: 最初の節点の番号, floor, ij: [i, j, i, j, …], links: 経由点ごとの 16 進 1 字} で書く
            //   (位置は ((i + 0.5)·Cell, (j + 0.5)·Cell, 階の z)。links はその経由点から区切り (WaypointStep) の隣 (+1,0)・(0,+1)・(+1,+1)・(+1,-1) への辺の有無の 4 ビット。
            //   その辺の費用は xy の距離 (AddWaypoints と同じ))。nodes は経由点以外を番号の順に、edges は経由点どうし以外の辺を書く
            //   (形 1 の Checkmate の 6 千節点・1.7 万辺はそのままの大きさ、Orison の全体 (経由点 24 万・辺 87 万) で 72 MB あったため)
            var wpIj = new Dictionary<int, (string F, int I, int J)>();
            for (int i = 0; i < graph.Nodes.Count; i++)
            {
                var n = graph.Nodes[i];
                if (n.Kind != "waypoint") continue;
                var p = n.Id.Split(':');
                if (p.Length == 4 && int.TryParse(p[2], out var wi) && int.TryParse(p[3], out var wj)) wpIj[i] = (n.Floor, wi, wj);
            }
            static int Blk(int v) => (int)Math.Floor((double)v / StationRouteGraph.WaypointStep);
            var dirs = new[] { (1, 0), (0, 1), (1, 1), (1, -1) };
            var masks = new Dictionary<int, int>();
            var packed = new HashSet<int>();   // links に入れた辺 (edges には書かない)
            for (int k = 0; k < graph.Edges.Count; k++)
            {
                var e = graph.Edges[k];
                if (e.Kind != "floor" || !wpIj.TryGetValue(e.A, out var a) || !wpIj.TryGetValue(e.B, out var b) || a.F != b.F) continue;
                int d = Array.IndexOf(dirs, (Blk(b.I) - Blk(a.I), Blk(b.J) - Blk(a.J)));
                if (d < 0 || (masks.GetValueOrDefault(e.A) & (1 << d)) != 0) continue;
                masks[e.A] = masks.GetValueOrDefault(e.A) | (1 << d);
                packed.Add(k);
            }
            w.WriteStartObject("graph");
            w.WriteStartArray("nodes");
            foreach (var n in graph.Nodes.Where((n, i) => !wpIj.ContainsKey(i)))
            {
                w.WriteStartObject();
                w.WriteString("id", n.Id); w.WriteString("kind", n.Kind); Vec(w, "pos", n.Pos); w.WriteString("floor", n.Floor);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("waypoints");
            for (int i = 0; i < graph.Nodes.Count;)
            {
                if (!wpIj.TryGetValue(i, out var first)) { i++; continue; }
                w.WriteStartObject();
                w.WriteNumber("at", i);
                w.WriteString("floor", first.F);
                var links = new StringBuilder();
                w.WriteStartArray("ij");
                for (; i < graph.Nodes.Count && wpIj.TryGetValue(i, out var c) && c.F == first.F; i++)
                {
                    w.WriteNumberValue(c.I); w.WriteNumberValue(c.J);
                    links.Append("0123456789abcdef"[masks.GetValueOrDefault(i)]);
                }
                w.WriteEndArray();
                w.WriteString("links", links.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("edges");
            for (int k = 0; k < graph.Edges.Count; k++)
            {
                if (packed.Contains(k)) continue;
                var e = graph.Edges[k];
                w.WriteStartObject();
                w.WriteNumber("a", e.A); w.WriteNumber("b", e.B); Num(w, "cost", e.Cost); w.WriteString("kind", e.Kind);
                if (e.Line.Length > 0) w.WriteString("line", e.Line);
                if (e.Dir.Length > 0) w.WriteString("dir", e.Dir);
                if (e.OneWay) w.WriteBoolean("oneWay", true);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void Num(Utf8JsonWriter w, string name, double v) => w.WriteNumber(name, Math.Round(v, 2));

    // z 回りの向き (度。回転で +x を回した向き。回転が無ければ 0)
    private static double Yaw((double W, double X, double Y, double Z)? q)
    {
        if (q is not { } r) return 0;
        var (x, y, _) = new StationXf(0, 0, 0, r.W, r.X, r.Y, r.Z).Rotate(1, 0, 0);
        return Math.Atan2(y, x) * 180 / Math.PI;
    }

    // 端末の区画 (StationRouteGraph.Build の節点の区画と同じ決め方): 自分か祖先のインスタンスが区画ならそれ、無ければ箱に入るいちばん小さい区画。
    // 区画にするのは Build と同じく、ルート (0 番) と箱が 2 km を超えるものを除いた区画。無ければ -1
    private static Func<int, (double X, double Y, double Z), int> TerminalSections(StationTree tree, IReadOnlyList<StationSection> sections)
    {
        var secs = sections.Where(s => s.Instance != 0 && s.Half.X <= 1000 && s.Half.Y <= 1000 && s.Half.Z <= 1000).ToList();
        var byInst = secs.ToDictionary(s => s.Instance);
        var inv = secs.ToDictionary(s => s.Instance, s => tree.Instances[s.Instance].ToStation.Inverse());
        return (inst, p) =>
        {
            for (int i = inst; i >= 0; i = tree.Instances[i].Parent) if (byInst.ContainsKey(i)) return i;
            int best = -1; double bv = double.MaxValue;
            foreach (var s in secs)
            {
                var ins = tree.Instances[s.Instance];
                if (ins.MinBounds is not { } a || ins.MaxBounds is not { } b) continue;
                var q = inv[s.Instance].Apply(p);
                if (q.X < a.X || q.X > b.X || q.Y < a.Y || q.Y > b.Y || q.Z < a.Z || q.Z > b.Z) continue;
                var v = (b.X - a.X) * (b.Y - a.Y) * (b.Z - a.Z);
                if (v < bv) { bv = v; best = s.Instance; }
            }
            return best;
        };
    }

    private static void Vec(Utf8JsonWriter w, string name, (double X, double Y, double Z) v)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(Math.Round(v.X, 2)); w.WriteNumberValue(Math.Round(v.Y, 2)); w.WriteNumberValue(Math.Round(v.Z, 2));
        w.WriteEndArray();
    }
}
