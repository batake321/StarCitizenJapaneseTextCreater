using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの段階 3: 区画 (インスタンス) の GLB から床の板を取り出し、階ごとの多角形にする。座標はルートの枠の局所 (メートル、z 上)。
//
// StarBreaker (socpak export) について、実データで確かめたこと (starbreaker 0.2.2、2026-11-01):
//   ・PATTERN は p4k のパス (区切りは「\」) の部分一致で、大文字小文字を区別しない。正規表現・ワイルドカード・「|」・複数の引数は使えない
//     (「a|b」「?」は「no .socpak files matching」、引数 2 つは「unexpected argument」)。
//     そのため「複数の socpak を 1 回で」はフォルダのような共通の部分文字列で一致させるしかない。
//   ・一致した socpak は、GLB のルートのノード「CryEngine_Z_up」の下に、socpak ごとの子「interior_<socpak のファイル名 (拡張子なし)>」として並ぶ。
//     子の行列は単位行列 (socpak どうしの置き方は掛からない。どの子もそれぞれの socpak の枠のまま)。
//   ・socpak の Child (入れ子の socpak) は書き出されない (clinicfloors_10_001 は自分の形を持たず、604 バイトの空の GLB になった)。
//   ・ルートの行列は CryEngine (z 上) → glTF (y 上) の変換 [x, z, -y] (列優先 1,0,0,0, 0,0,-1,0, 0,1,0,0)。
//     その下のノードの行列は socpak の枠のままなので、ルートの行列は掛けない
//     (base_shipweapongeneric_001 の全メッシュの箱が socpak の minBounds・maxBounds (-6,-3.1,0)〜(6,18,5) と一致した)。
//   ・1 回の起動に約 1.9 秒かかる (p4k を開く分。中身が少ない socpak でも同じ)。9 個一致で 2.0 秒、34 個で 3.8 秒、57 個で 2.7 秒。
//     --materials none でも頂点は入るので GLB は大きい (34 個で 109 MB、57 個で 81 MB)。読むのは先頭の JSON の部分だけ。
// 床の板の決め方: メッシュ名 (CGF のファイル名) の規則 IsFloorMesh。床・階段の CGF の名前が .soc に無い socpak は書き出さない (ScanSocMeshes)。
// (2026-11-02) 床・階段は、部品の名前の表 (StationMeshKinds) → 形 (GLB の三角形の上向きの面。GuessKind) → 名前の語の決まり (最後の手段) の順で決め、板は実際の面の高さで作る (SurfacePlate)。
//   書き出す socpak は IsExportMesh(kinds, …) で決める。
// 多角形: 板の箱 (accessor の min/max) の 8 隅をノードの行列 → インスタンスの変換で局所に移し、xy の凸包 (箱なので平行四辺形) を
//   0.5 m の格子に塗る (セルの中心が入るもの)。上面 z = 移した 8 隅の z の最大。階ごと・区画ごとに marching squares で輪郭にし、Douglas–Peucker 0.25 m で間引く。

// GLB の中のメッシュ 1 つ (socpak の枠)。Corners は accessor の min/max の 8 隅をノードの行列で socpak の枠に移したもの。Shape は三角形から数えた形 (読んだときだけ)
internal sealed record StationGlbMesh(string Mesh, (double X, double Y, double Z)[] Corners, StationFloorBuilder.StationShape? Shape = null);

// 床・階段の板 1 つ (局所)。Hull は xy の凸包 (反時計回り)。Ramp は傾いた床の板 (スロープ)。スロープは Stair も true にし、階段と同じに扱う
// Group は床のまとまり (StationFloorBuilder.AssignGroups。-1 はまとまりに入らない)
// Link は踊り場でつないだ階段の鍵 (StationFloorBuilder.Landings。-1 はインスタンスごとにまとめる)。Landing は階段の一部にした踊り場の床の板
// Depth は板の歩ける水平な面の本当の奥行 (帯の幅。StationShape.LevelDepths。形の数の無い板は NaN で、凸包の短いほうの幅で見る。DeeperThanTread)
internal sealed record StationPlate(int Instance, string Mesh, bool Stair, (double X, double Y)[] Hull, double Top, double Bottom, bool Ramp = false, int Group = -1,
    int Link = -1, bool Landing = false, double Depth = double.NaN);

// 書き出しの 1 回分 (Pattern を starbreaker に渡し、Socpaks のうち出てきたものを使う)
internal sealed record StationExportJob(string Pattern, IReadOnlyList<string> Socpaks, int Matched);

// 書き出しの結果
internal sealed class StationMeshSet
{
    // socpak (位置表のキー) → 床・階段のメッシュ
    public Dictionary<string, List<StationGlbMesh>> Meshes { get; } = new(StringComparer.Ordinal);
    // GLB を読んだ直後の socpak ごとのメッシュ (表で選び分ける前。StationMeshCache に保存し、次の取り込みで starbreaker を動かさずに判定をやり直すのに使う)。
    // GLB に出た子ごとに 1 つの並び (1 つの GLB に同じ socpak の子が 2 回出たときは 2 つ。Meshes は後のもの、票は両方で数える)。GLB に出てこなかった socpak は並びが 0 個
    public Dictionary<string, List<List<StationGlbMesh>>> Raw { get; } = new(StringComparer.Ordinal);
    // 書き出しを頼んだのに GLB に出てこなかった socpak
    public List<string> Missing { get; } = new();
    public List<string> Errors { get; } = new();
    public int Runs { get; set; }
    public long TotalGlbBytes { get; set; }
    public long MaxGlbBytes { get; set; }
    public double Seconds { get; set; }
    // 部品の名前 → 種類の表 (StationFloorBuilder.ResolveKinds で決める)。null なら名前の語の決まりだけ (形の数の無い前のキャッシュ)
    public StationMeshKinds? Kinds { get; set; }
    // 形でだけ床・階段になった名前のうち、Plates で歩ける面につながって板にしたもの (FinalizeKinds で使う)
    public Dictionary<string, StationMeshKind> ShapeAccepted { get; } = new(StringComparer.Ordinal);
    // この取り込みで、形で決めた種類の部品として候補にした名前
    public HashSet<string> ShapeSeen { get; } = new(StringComparer.Ordinal);
    // この取り込みで、床と階段に分けた (Composite の) 部品の名前 (FinalizeKinds で表を composite にする)
    public HashSet<string> CompositeKeys { get; } = new(StringComparer.Ordinal);
    // この取り込みで表に足した名前
    public HashSet<string> AddedKeys { get; } = new(StringComparer.Ordinal);
    // 浮いたもの: 床・階段の種類なのに上向きの面が無く、板にならなかった部品 (Plates が入れる。StationFloorBuilder.FloatingItems)
    public List<StationFloating> NoUpFace { get; } = new();
    // 表に無かった名前の、形の判定の票 (部品 1 つで 1 票。床・階段・どれでもない)
    public Dictionary<string, int[]> Votes { get; } = new(StringComparer.Ordinal);
    // この取り込みで表に足した名前の数
    public int NewKinds { get; set; }
}

// Composite (2026-11-02、親の指示): 1 つの部品に、広い平らな面 (床) と段・坂 (階段) の両方があるもの。板は形で床と階段に分ける (StationFloorBuilder.SurfacePlates)
internal enum StationMeshKind { None, Floor, Stair, Composite }

// (2026-11-02、利用者の指示「浮いたものリストを取り込み時にログ出力と DB 格納しとくようにすると将来のデバッグに使える」) 浮いたもの 1 つ。
// Kind は種類と決め方 (floor:name・stair:shape・floor:bundled など。階は "floor:<階の id>")、X・Y・Z は局所の位置 (板・部品の中心と高さ)、Reason は浮いている理由
internal sealed record StationFloating(string Mesh, string Kind, double X, double Y, double Z, string Reason);

// 部品の名前 (StationFloorBuilder.MeshKey) → 種類 (床・通路 / 階段・坂 / どれでもない) の表 (2026-11-02、利用者の指示「判定したものを階段や通路に入れる処理をして、名前を登録するようにしろ」)。
//   同梱の表 (アプリのフォルダの station_mesh_kinds.json。今は空。無くてもよい) と、利用者の手元の表 (置き場所の stations\station_mesh_kinds_user.json。
//   取り込みのたびに、どちらの表にも無い名前を足す) の 2 つ。同梱の表に無い名前だけを手元の表に足す。
//   Source: "bundled" は同梱の表 (調べる担当がデータで確かめた種類。確定。「該当しない」(none) はここだけ)。"name" は名前の語の決まり (IsFloorMesh・IsStairMesh) にも当たる名前 (前と同じく、つながりを問わず板にする)。
//     "shape" は形でだけ床・階段になった名前 (板にするのは、名前の板などの歩ける面に接するものだけ。StationFloorBuilder.Plates)。
//   ファイルの形: {"format":1,"meshes":{"<名前>":"<floor|stair|none>:<name|shape>", ...}} (名前の順、改行は LF)
// 判定と登録はアプリが取り込みのときに行う (表に無い部品は形で判定して手元の表に足す。パッチで部品が増えても、同梱の表が空・無しでも動く)。同梱の表は、判定を省いて速くするためだけのもの。
internal sealed class StationMeshKinds
{
    public sealed record Entry(StationMeshKind Kind, string Source);
    public const string BundledFile = "station_mesh_kinds.json";
    public const string UserFile = "station_mesh_kinds_user.json";
    private readonly Dictionary<string, Entry> _bundled, _user;
    private readonly object _lock = new();
    public string? UserPath { get; private set; }
    public int Added { get; private set; }

    public StationMeshKinds(Dictionary<string, Entry> bundled, string? userPath)
    {
        _bundled = bundled;
        UserPath = userPath;
        // (2026-11-02、利用者の指示「明らかに該当しないと分かっているものをいれた該当しないリストの中身は増やさない。該当するリストにないものでチェックしてリストに入れるつくり」)
        // 手元の表は「該当する」(床・階段) だけ。前に書いた none は読むときに捨てる (次の取り込みで形で判定し直す)。「該当しない」(none) は同梱の表だけ
        _user = userPath != null ? ReadFile(userPath).Where(kv => kv.Value.Kind != StationMeshKind.None).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
    }

    private static StationMeshKinds? _current;
    private static readonly object CurrentLock = new();
    // アプリの表 (同梱の表と、userPath の手元の表)。userPath が前と違えば読み直す
    public static StationMeshKinds ForUser(string? userPath)
    {
        lock (CurrentLock)
        {
            if (_current == null || !string.Equals(_current.UserPath, userPath, StringComparison.OrdinalIgnoreCase))
                _current = new StationMeshKinds(ReadFile(Path.Combine(AppContext.BaseDirectory, BundledFile)), userPath);
            return _current;
        }
    }

    public Entry? Get(string key)
    {
        // (2026-11-02、親の指示) 同梱の表を手元の表より先に見る (同梱の表は確かめた種類で、確定)
        lock (_lock) return _bundled.TryGetValue(key, out var b) ? b : _user.TryGetValue(key, out var e) ? e : null;
    }

    public void Add(string key, Entry e)
    {
        // 手元の表には「該当する」(床・階段) だけを足す
        if (e.Kind == StationMeshKind.None) return;
        lock (_lock) { if (_user.ContainsKey(key) || _bundled.ContainsKey(key)) return; _user[key] = e; Added++; }
    }

    // 手元の表の名前の種類を書き換える (この取り込みで足した名前の直し。StationFloorBuilder.FinalizeKinds)
    // none にするときは手元の表から消す (「該当しない」は手元の表に書かない)
    public void Replace(string key, Entry e) { lock (_lock) if (_user.ContainsKey(key)) { if (e.Kind == StationMeshKind.None) _user.Remove(key); else _user[key] = e; } }

    public Dictionary<string, Entry> UserEntries() { lock (_lock) return new(_user, StringComparer.Ordinal); }

    // 手元の表を書く (作業用のファイルに書いてから名前を変える)。書けなければ例外
    public void SaveUser()
    {
        if (UserPath == null) return;
        Dictionary<string, Entry> copy;
        lock (_lock) copy = new(_user, StringComparer.Ordinal);
        WriteFile(UserPath, copy);
    }

    public static Dictionary<string, Entry> ReadFile(string path)
    {
        var res = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return res;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("meshes", out var ms) || ms.ValueKind != JsonValueKind.Object) return res;
            foreach (var kv in ms.EnumerateObject())
            {
                var v = kv.Value.GetString() ?? "";
                var c = v.IndexOf(':');
                var k = (c < 0 ? v : v[..c]) switch { "floor" => StationMeshKind.Floor, "stair" => StationMeshKind.Stair, "composite" => StationMeshKind.Composite, _ => StationMeshKind.None };
                res[kv.Name] = new Entry(k, c < 0 ? "shape" : v[(c + 1)..]);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        return res;
    }

    public static void WriteFile(string path, IReadOnlyDictionary<string, Entry> entries)
    {
        var sb = new StringBuilder("{\n  \"format\": 1,\n  \"meshes\": {\n");
        var keys = entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var e = entries[keys[i]];
            var kind = e.Kind switch { StationMeshKind.Floor => "floor", StationMeshKind.Stair => "stair", StationMeshKind.Composite => "composite", _ => "none" };
            sb.Append("    ").Append(JsonSerializer.Serialize(keys[i])).Append(": \"").Append(kind).Append(':').Append(e.Source).Append('"').Append(i + 1 < keys.Count ? ",\n" : "\n");
        }
        sb.Append("  }\n}\n");
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

// 階 1 つ。Z は板の上面の中央値、ZMin・ZMax は板の上面の範囲。Name・Labels は StationLevels.Name で付ける
internal sealed class StationFloor
{
    public required string Id { get; init; }
    public double Z { get; set; }
    public double ZMin { get; set; }
    public double ZMax { get; set; }
    public string Name { get; set; } = "";
    // 名前の出どころ (section・label・elevator・order)
    public string NameSource { get; set; } = "";
    public List<string> Labels { get; } = new();
    // この階にある乗り場 (エレベーター・トラム) の行き先の名前 (訳のあるもの。StationRouteGraph.NameFloors)。同じ名前の階の括弧の材料 (StationMapJson.Write)
    public List<string> StopLabels { get; } = new();
    // 名前で結び付いた重ねる鍵 (StationRouteGraph.StackKeys の番号。無ければ −1)。階の表示の名前 (データから付いたもの) が行き先の鍵の訳と同じ階に付ける
    // (StationRouteGraph.NameFloors。2026-11-02、利用者の指示「名前での紐づけ」)。StackOrder が重ねる順に使う
    public int NameStackKey { get; set; } = -1;
    // 階段でつながる、エレベーターの止まる階の Id (StationRouteGraph.NameByStairs。2026-11-02、利用者の決定「エレベーターとつながった名前優先」)。
    // この階はその階の名前を借り、重ねる順もその階の鍵を借りる (StackOrder)。無ければ ""
    public string StairPartner { get; set; } = "";
    // 板が無く、乗り場の高さから作った階か (洞窟・床の取れない区画)
    public bool FromTransit { get; set; }
    // 床のまとまり ("g<番号>"。乗り場の高さから作った階は "") と、その xy の箱 (局所。無ければ高さだけで階を決める)
    public string Group { get; set; } = "";
    public (double MinX, double MinY, double MaxX, double MaxY)? Bounds { get; set; }
    // 表示の名前 (StationRouteGraph.NameFloors で訳を引いたもの。訳が無ければ鍵から @ を除いたもの)
    public string DisplayName { get; set; } = "";
    // 地図の階の一覧に出すか (名前の付いたまとまりの階だけ。StationRouteGraph.NameFloors)
    public bool Listed { get; set; } = true;
    // まとまりの中の下からの番号 (1 から)
    public int Level { get; set; }
    // 乗らないエレベーター (病院・居住区・ハンガー) でしか行けない行き先の階 (StationRouteGraph.HideNoRideFloors。一覧に出さない)
    public bool ElevatorOnly { get; set; }
    // まとまりの名前 (表示の名前。「付近」の名前は借りた元のまとまりの名前。無ければ "")。地区の名前に使う (StationMapJson の districts)
    public string GroupName { get; set; } = "";
    // 乗り場 (トラム・エレベーター。乗らないエレベーターの乗り場も含む) から、歩き・階段・乗り物の辺でたどれる節点がこの階にあるか
    // (StationRouteGraph.MarkReachable。false の階は一覧に出さず、JSON の区画の多角形も書かない)
    public bool Reachable { get; set; } = true;
    // 行き先の階として実装されているか (StationRouteGraph.MarkImplemented。乗り場はあるが、実装されている乗り場も店も無い階は false)
    public bool Implemented { get; set; } = true;
    // 階の床の板が全部、都市の板 (IsCityPlate: habitation_*・plate_<大きさ>_*・乗り場の縁・橋のガラス) か (Levels で決める)
    public bool CityOnly { get; set; }
    // この階の板 (床の板と、階の中の道として合わせた階段・スロープの板。Levels で決める。JSON には書かない)
    public HashSet<StationPlate>? PlateSet { get; set; }
    // LayerCell (2 m) の升目ごとの、この階の床の高さの範囲 (Levels で決める。点の階を xy と高さで選ぶとき FloorAtXY が使う)
    public Dictionary<(int, int), (double Min, double Max)>? CellZ { get; set; }

    public const double GroupBoundsMargin = 2.0;

    // まとまりの板のある CoarseCell (8 m) の升目 (Levels で作る。JSON には書かない。無ければ箱だけで見る)
    public HashSet<(int, int)>? Coarse { get; set; }
    public const double CoarseCell = 8.0;

    // 点の xy がまとまりに入るか: 升目があれば、点の升目かその隣 (8 方向) に板があるか。無ければ箱 (Bounds) の margin 内か
    public bool Contains(double x, double y, double margin = GroupBoundsMargin)
    {
        if (Bounds is not { } b) return true;
        if (!(x >= b.MinX - margin && x <= b.MaxX + margin && y >= b.MinY - margin && y <= b.MaxY + margin)) return false;
        if (Coarse == null) return true;
        int i = (int)Math.Floor(x / CoarseCell), j = (int)Math.Floor(y / CoarseCell);
        // 升目の隣を見る範囲: 既定の margin (2 m) では 1 升、それより大きい margin ではその分だけ広げる
        int k = 1 + (margin > GroupBoundsMargin ? (int)Math.Ceiling((margin - GroupBoundsMargin) / CoarseCell) : 0);
        for (int di = -k; di <= k; di++) for (int dj = -k; dj <= k; dj++) if (Coarse.Contains((i + di, j + dj))) return true;
        return false;
    }
}

// 区画 1 つ (インスタンス)。Polygons は階の Id → 輪郭の環の並び (各環は [x,y] の並び。閉じ目は繰り返さない)
internal sealed class StationSection
{
    public required int Instance { get; init; }
    public required string Socpak { get; init; }
    public required string EntityName { get; init; }
    public string Label { get; set; } = "";
    // 箱 (インスタンスの minBounds・maxBounds を局所に置いたもの): 中心・半分の大きさ・z 回りの向き (度)
    public (double X, double Y, double Z) Center { get; set; }
    public (double X, double Y, double Z) Half { get; set; }
    public double Yaw { get; set; }
    // (2026-11-02、利用者の決定「A」: 区画の形が不定形で四角でない) 環は床の板ごとの凸包そのもの (四角い部品は四角のまま。環どうしは重なることがある)。
    // 板の無い区画は箱の 4 隅の 1 つの環
    public Dictionary<string, List<List<(double X, double Y)>>> Polygons { get; } = new(StringComparer.Ordinal);
    // 階の Id → 形の面積。板を 0.5 m の格子に塗った輪郭 (Outline。板の縁の PaintMargin の帯を含む) の環の面積の和 (形を凸包に替える前の形の面積と同じ値)。
    // 階を一覧に出すか (MinListedFloorArea)・階の名前の候補の順に使う (形を替えても一覧に出る階・名前が変わらないように)
    public Dictionary<string, double> PolygonArea { get; } = new(StringComparer.Ordinal);
    // 床の板が取れず、箱で代用した (推測の印)
    public bool BoxFallback { get; set; }
    public int PlateCount { get; set; }
    // ゲームの名前 (鍵か、星図の場所・店の名前)・表示の名前・名前の出どころ (place・section・destination・area・shop)・一覧に出すか (StationRouteGraph.NameSections)
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string NameSource { get; set; } = "";
    public bool Listed { get; set; }
}

internal static class StationFloorBuilder
{
    public const double Cell = 0.5;
    public const double Simplify = 0.25;
    public const double FloorGap = 2.5;
    public const double PlateThin = 0.6;

    private static (double X, double Y, double Z) Sub((double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    // 床の板のメッシュ名の規則 (Checkmate の .soc の CGF の名前の一覧から決めた。harness の cm_cgfs.tsv):
    //   「_」で区切った語に floor・floorplate・walkway のどれかがあり、下の除く語が無いもの。または語に plane と metal の両方があるもの
    //   (col_a_ext_adn_plane_metal_1x2m_001_a・1x4m: Checkmate の BlackMarket (rstop_blackmarket_002_rund_001) の床に敷いた金属板。銃の店の前の床がこれだけだった)。
    //   addon は除く語に入れない (util_addon_floorplate_*・col_a_structural_addon_floor_* が床の板。Checkmate の .soc で計 300 か所以上)。
//   gantry は入れない (Checkmate では出入りの小部屋 (spawnc) の中の縦・斜めの部品と、細い飾り (gantry_straight_16x005x025) だった)。
//   Plates で、板の傾きで床・スロープ・縦の板 (除く) に分ける (Plates の説明)
    //   除く語: 床の上・床下の飾り (cable・cables・light・decal・vent・tarp・debris・rack・fixture・terminal・counter・garbage・rib)、
    //   壁・天井 (wall・ceiling・trim)、階段 (stair・stairs。階段は IsStairMesh で別に扱う)、手すり (railing・rail)
    private static readonly HashSet<string> FloorWords = new(StringComparer.Ordinal) { "floor", "floorplate", "walkway" };
    private static readonly HashSet<string> FloorNotWords = new(StringComparer.Ordinal)
    {
        "cable", "cables", "light", "decal", "vent", "tarp", "debris", "rack", "fixture", "terminal", "counter", "garbage", "rib",
        "wall", "ceiling", "trim", "stair", "stairs", "railing", "rail",
    };
    // 階段のメッシュ名の規則: 語に stair・stairs があり、手すり・柱・飾りの語 (rail・railing・decal・wall・structure・strut・divider・platform・cap・capplate・addon・sign・cover) が無いもの
    private static readonly HashSet<string> StairNotWords = new(StringComparer.Ordinal)
    {
        "rail", "railing", "decal", "wall", "structure", "strut", "divider", "platform", "cap", "capplate", "addon", "sign", "cover",
    };

    private static string[] Words(string mesh)
    {
        var s = mesh.ToLowerInvariant();
        var slash = s.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) s = s[(slash + 1)..];
        foreach (var ext in new[] { ".cgfm", ".cgam", ".cgf", ".cga" })
            if (s.EndsWith(ext, StringComparison.Ordinal)) { s = s[..^ext.Length]; break; }
        return s.Split('_', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool IsFloorMesh(string mesh)
    {
        var w = Words(mesh);
        if (IsCityPlate(w)) return true;
        // (2026-11-01) シャトルの駅の乗り場の床 ind_transit_dock_a・_b・ind_transit_dock_glass_* (Orison の Providence の駅。上面 741.2 m)。語に transit と dock の両方があるもの
        if (w.Contains("transit") && w.Contains("dock") && !w.Any(FloorNotWords.Contains)) return true;
        // (2026-11-02、利用者の決定「A」) 電車のプラットホーム platform_transit_* (1 語目 platform・2 語目 transit。ガラス platform_transit_*_glass_* は除く)。
        // Area18 の電車の乗り場の床 (platform_transit_a・_b)。90 か所の CGF の一覧 (cgfs_all) でこの名前があるのは Area18 だけ
        if (w.Length >= 2 && w[0] == "platform" && w[1] == "transit" && !w.Contains("glass")) return true;
        // (2026-11-01) 階段の踊り場の床 util_stairs_platform_* (語に stairs・stair と platform の両方があり、rail・railing の無いもの)。Checkmate の精錬所のエレベーター
        // (4bbd7b94) の前の床は、この踊り場の板 (-457.98・-457.48 m) だけで、床の規則で拾えず、乗り場がどの床ともつながっていなかった
        // canopy (屋根) は除く (Seraphim の貨物のエレベーターの util_stairs_platform_canopy_cap_4m_c (-753.09 m) が、22 m² の床の階になっていた)
        if (w.Contains("platform") && w.Any(x => x is "stair" or "stairs") && !w.Contains("rail") && !w.Contains("railing") && !w.Contains("canopy")) return true;
        if (IsCorridor(w)) return true;
        // (2026-11-02、親の決定) 足場の通路 structure_gantry_* (1 語目 structure・2 語目 gantry) を歩ける床にする。Orison の Cousin Crow's の店の階 (723.6 m の段) と
        // 727.17 m の床のあいだが structure_gantry_b_16x02x04_b だけで、道が切れていた。縦の部品は Plates の傾きの決まりで除かれる
        if (w.Length >= 2 && w[0] == "structure" && w[1] == "gantry" && !w.Any(FloorNotWords.Contains)) return true;
        return (w.Any(FloorWords.Contains) || w.Contains("plane") && w.Contains("metal")) && !w.Any(FloorNotWords.Contains);
    }

    // (2026-11-01、利用者の決定「A」) 通路の部品: 1 語目が corridor、2 語目が int か commons で、straight・custom・corner のどれかがあり、
    // 灯り・枠・天井・植木鉢・ガラス・飾り (light・frame・ceiling・planter・glass・addon) と、床の除く語 (FloorNotWords) の無いもの。
    // 下りの通路 corridor_int_straight_drop_* も同じ (利用者の言葉「通路扱いでいい」。坂・階段の別の規則は作らず、今の決まりのまま)。
    // New Babbage の Aspire Grand の乗り場の床 (y −353 m で終わる) と階段 stair_a_straight_04x08x04_a (y −347 m から) のあいだ 6 m (z 376 m) は、
    // corridor_commons_custom_b・corridor_int_straight_01x06x04・02x06x04・corridor_int_straight_drop_05x06x04_b だけで、床が途切れていた
    private static readonly HashSet<string> CorridorNotWords = new(StringComparer.Ordinal) { "light", "frame", "ceiling", "planter", "glass", "addon" };
    private static bool IsCorridor(string[] w) =>
        w.Length >= 3 && w[0] == "corridor" && w[1] is "int" or "commons" && w.Any(x => x is "straight" or "custom" or "corner")
        && !w.Any(CorridorNotWords.Contains) && !w.Any(FloorNotWords.Contains);

    // 段の灯り (light_landing_*): 段の縁・段の前の床に、床と同じ高さで埋め込まれた灯り。Orison の Providence の駅の階段は、階段のメッシュが無く、
    // この灯りが 0.75 m おきに並ぶ (737.95 → 741.21 m) だけだった。Seraphim の配送センターの 5 段も、下 (-9.72 m) と上 (-8.47 m) に並ぶ (StepLightPlates)
    public static bool IsStepLightMesh(string mesh)
    {
        var w = Words(mesh);
        return w.Length >= 2 && w[0] == "light" && w[1] == "landing";
    }

    // 床の灯り (light_floor_*): 床に埋め込まれた灯り。Providence の駅の下の乗り場の床 (737.92 m) は床のメッシュが無く、この灯りとベンチだけがあった (FloorLightPlates)
    public static bool IsFloorLightMesh(string mesh)
    {
        var w = Words(mesh);
        return w.Length >= 2 && w[0] == "light" && w[1] == "floor";
    }

    // 書き出しで読むメッシュ (床・階段・段の灯り・床の灯り)
    public static bool IsExportMesh(string mesh) => IsFloorMesh(mesh) || IsStairMesh(mesh) || IsStepLightMesh(mesh) || IsFloorLightMesh(mesh);

    // (2026-11-02) 書き出す socpak を決めるメッシュ: 表で床・階段のもの (形で判定した名前も含む)、表に無く名前の語の決まり (IsExportMesh) に当たるもの、灯り。
    // 表に無い名前だけの socpak は書き出さない (親の決定を待たずに決めたこと: 表に無い名前を全部書き出すと、取り込みのたびにほぼ全部の socpak を書き出すことになるため。
    // 書き出した socpak の中の表に無い部品は、形で判定して表に足す)
    public static bool IsExportMesh(StationMeshKinds? kinds, string mesh)
    {
        if (IsLightMesh(mesh)) return true;
        if (kinds?.Get(MeshKey(mesh)) is { } e) return e.Kind != StationMeshKind.None;
        return IsExportMesh(mesh);
    }

    // 都市の床 (Orison で実データから決めたもの): habitation_plate_*・habitation_addon_plate_* (浮かぶ足場の地面) と、
    // plate_trim_*_balcony_* (シャトルの駅の乗り場の縁の床。Cloudview Center の反対回りの乗り場 (-40.9, -129.8, -3.4) の床がこれだった)。
    // understructure (足場の裏の構造) は除く。plate_trim_*_balcony_* の歩ける高さは Plates で乗り場の高さに直す (IsBalcony)
    // (除く語の trim はここでは見ない)。thruster (足場の推進器 590 個)・window (窓) も除く
    // (2026-11-01 に足したもの) Cloudview Center の島の床の板 plate_<大きさ>_* (plate_64x32_a_thick・plate_64_cap_c_thick・plate_64x64_a_surface_a・plate_48_cap_b など。
    // 病院の島の南の床 (上面 -3.67 m) がこれだけだった) と、橋の床のガラス板 plate_trim_bridge_*_glass (厚さ 0.02 m。広場と Crusader Showroom のあいだの橋で、
    // 両端の橋の階段 base_08x06x02_bridge_stairs_b の下の端と同じ -3.42 m)。1 語目が plate で 2 語目が大きさ (数字か 数字x数字) か transition のものを床にし、
    // trim・addon の付くもの (縁・飾り) は除く。皿の plate_empty_1_a・plate_steak_piece_1_a・plate_meal_* (2 語目が数字でない) は入らない
    private static readonly Regex PlateSizeWord = new(@"^\d+(x\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static bool IsCityPlate(string[] w) =>
        w.Contains("plate") && (w.Contains("habitation") || w.Contains("trim") && w.Contains("balcony"))
        && !w.Contains("understructure") && !w.Contains("thruster") && !w.Contains("window")
        || w.Length >= 2 && w[0] == "plate" && (PlateSizeWord.IsMatch(w[1]) || w[1] == "transition") && !w.Contains("trim") && !w.Contains("addon")
        || w.Length >= 3 && w[0] == "plate" && w[1] == "trim" && w.Contains("bridge") && w[^1] == "glass";

    // シャトルの駅の乗り場の縁 (plate_trim_*_balcony_*)。箱の上面・底は歩ける高さではない
    // (Cloudview Center の乗り場の床は z -3.42 m (実測・乗り場の位置 -3.416 m) で、縁の箱は -9.57〜-2.33 m・-3.32〜-2.38 m)
    public static bool IsCityPlateMesh(string mesh) => IsCityPlate(Words(mesh));

    public static bool IsBalcony(string mesh)
    {
        var w = Words(mesh);
        return w.Contains("plate") && w.Contains("trim") && w.Contains("balcony");
    }

    public static bool IsStairMesh(string mesh)
    {
        var w = Words(mesh);
        return w.Any(x => x is "stair" or "stairs") && !w.Any(StairNotWords.Contains);
    }

    // 部品の名前の表 (StationMeshKinds) の鍵: 小文字・パスと拡張子を除いたもの (.soc の CGF の名前と、GLB のメッシュの名前を同じ鍵にする。
    // GLB の名前は多くが拡張子なし、一部は「Objects\…\shipping_container_yellow」「…/security_camera_2_a.cga」のようにパス付きだった (harness の shapes)
    public static string MeshKey(string mesh) => string.Join("_", Words(mesh));

    // 名前の語の決まりによる種類 (表にも形の判定にも当たらないときの最後の手段)
    public static StationMeshKind NameKind(string mesh) => IsStairMesh(mesh) ? StationMeshKind.Stair : IsFloorMesh(mesh) ? StationMeshKind.Floor : StationMeshKind.None;

    // 表の種類 (表が無い・表に無いときは名前の語の決まり。Source は "name")
    public static StationMeshKinds.Entry KindOf(StationMeshKinds? kinds, string mesh)
    {
        if (kinds?.Get(MeshKey(mesh)) is { } e) return e;
        return new StationMeshKinds.Entry(NameKind(mesh), "name");
    }

    // 書き出しで形を数えるメッシュ: 表で床・階段のもの、表に無いもの (形で判定して表に足す)
    public static bool NeedsShape(StationMeshKinds? kinds, string mesh) =>
        kinds?.Get(MeshKey(mesh)) is { } e ? e.Kind != StationMeshKind.None || !IsFixed(e) : true;

    // 確定の種類: 名前の語の決まり (name) と同梱の表 (bundled)。形で決めた種類 (shape) は確定にせず、取り込むたびに形で判定し直す (FinalizeKinds)
    public static bool IsFixed(StationMeshKinds.Entry e) => e.Source != "shape";

    // 書き出しで箱だけ読むメッシュ (段の灯り・床の灯り)
    public static bool IsLightMesh(string mesh) => IsStepLightMesh(mesh) || IsFloorLightMesh(mesh);

    private static readonly Regex CgfRegex = new(@"[A-Za-z0-9_/\\.\-]+\.(?:cgf|cga|cgfm|cgam)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // socpak の .soc にある CGF のファイル名 (小文字、パスなし)。開けなければ空
    public static HashSet<string> ScanSocMeshes(StationIndex index, string socpak)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var zip = index.OpenSocpak(socpak);
            if (zip == null) return res;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)) continue;
                var t = Encoding.Latin1.GetString(StationIndex.ReadAll(e));
                foreach (Match m in CgfRegex.Matches(t))
                {
                    var n = m.Value.Replace('\\', '/').ToLowerInvariant();
                    res.Add(n[(n.LastIndexOf('/') + 1)..]);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        return res;
    }

    // 書き出す socpak を決めて、starbreaker の起動の単位にまとめる。only があればそのインスタンスの socpak だけ (確かめ用)。
    // まとめ方: 同じフォルダの socpak が 2 つ以上あり、フォルダの部分文字列に一致する socpak (下のフォルダも含む) が
    //   必要な数の 3 倍と 8 個多いの小さいほう以下で、必要な socpak のファイル名がその中で重ならなければ、フォルダで 1 回。そうでなければ 1 つずつ
    public static List<StationExportJob> PlanExports(StationIndex index, StationTree tree, Func<StationInstance, bool>? only = null, StationMeshKinds? kinds = null)
    {
        var need = new List<string>();
        foreach (var sp in tree.Instances.Where(i => i.Found && i.Socpak.Length > 0 && (only == null || only(i))).Select(i => i.Socpak).Distinct(StringComparer.Ordinal))
        {
            var names = ScanSocMeshes(index, sp);
            if (names.Any(n => IsExportMesh(kinds, n))) need.Add(sp);
        }
        var jobs = new List<StationExportJob>();
        foreach (var g in need.GroupBy(k => k.Contains('/') ? k[..k.LastIndexOf('/')] : "", StringComparer.Ordinal))
        {
            var list = g.ToList();
            if (list.Count >= 2 && g.Key.Length > 0)
            {
                var pat = "objectcontainers\\" + g.Key.Replace('/', '\\') + "\\";
                var matched = index.Entries.Keys.Where(k => P4kPathOf(k).Contains(pat, StringComparison.OrdinalIgnoreCase)).ToList();
                var stems = matched.GroupBy(Stem, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
                if (matched.Count <= Math.Min(list.Count * 3, list.Count + 8) && list.All(k => stems.GetValueOrDefault(Stem(k)) == 1))
                {
                    jobs.Add(new StationExportJob(pat, list, matched.Count));
                    continue;
                }
            }
            foreach (var k in list) jobs.Add(new StationExportJob(P4kPathOf(k)[5..], new[] { k }, 1));
        }
        return jobs;
    }

    // 位置表のキー → p4k の中のパス (区切り「\」、小文字)
    private static string P4kPathOf(string key) => "data\\objectcontainers\\" + key.Replace('/', '\\');

    // socpak のファイル名 (拡張子なし)
    public static string Stem(string key)
    {
        var f = key[(key.LastIndexOf('/') + 1)..];
        return f.EndsWith(".socpak", StringComparison.OrdinalIgnoreCase) ? f[..^7] : f;
    }

    // Windows のコマンドラインの 1 つの引数にする (末尾・「"」の前の「\」は倍にする)
    public static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        int bs = 0;
        foreach (var c in s)
        {
            if (c == '\\') { bs++; continue; }
            if (c == '"') { sb.Append('\\', bs * 2 + 1).Append('"'); bs = 0; continue; }
            sb.Append('\\', bs).Append(c);
            bs = 0;
        }
        sb.Append('\\', bs * 2).Append('"');
        return sb.ToString();
    }

    // 書き出して読む。run は starbreaker を動かす関数 (アプリでは StarBreakerRunner.RunAsync(exe, args, null, null, ct) を渡す)。
    // GLB は workDir に作り、床・階段のメッシュだけを読んだらすぐ消す (GLB の名前は回の番号 station_<番号 4 桁>.glb なので、並行でもぶつからない)。
    // 並行: starbreaker は呼んだ側の枠で 1 本は必ず動かせる。2 本目からは 1 本ずつ tryExtra に聞き、true なら足して動かす
    //   (足した 1 本は、その starbreaker が終わったら releaseExtra で返す)。tryExtra が null なら 1 本ずつ (前と同じ)。
    //   tryExtra は、足せる本数が変わったことに気づけるよう、starbreaker が終わったときと 1 秒ごとに聞き直す。
    //   GLB の読み込みは starbreaker が終わった後、その回の枠を返してから行う (読んでいる間に次の回の starbreaker が動く)。
    // 結果 (Meshes・Missing・Errors・Runs・TotalGlbBytes・MaxGlbBytes) は、全部の回が終わってから回の番号の順に、1 本ずつのときと同じ足し方でまとめる。
    // progress は (終わった回の数, 全部の数)。
    // 失敗・取り消し: 取り消されたら動いている starbreaker を全部止め (run に渡す取り消し)、全部終わるのを待ってから取り消しの例外。
    //   ある回の読み込みが例外になったら、それより後の番号の回は始めず、動いていれば止め、前の番号の回は終わるのを待ってから、
    //   いちばん前の番号の回の例外を投げる (1 本ずつのとき、その回より後は動かなかったのと合わせる)
    public static async Task<StationMeshSet> ExportAsync(Func<string, CancellationToken, Task<(int ExitCode, string StderrTail)>> run,
        string p4kPath, IReadOnlyList<StationExportJob> jobs, string workDir, Action<int, int>? progress = null, CancellationToken ct = default,
        Func<Task<bool>>? tryExtra = null, Action? releaseExtra = null, StationMeshKinds? kinds = null)
    {
        var res = new StationMeshSet { Kinds = kinds };
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(workDir);
        var outcomes = new ExportOutcome?[jobs.Count];
        var runCts = new CancellationTokenSource?[jobs.Count];
        var own = new SemaphoreSlim(1, 1);       // 呼んだ側の枠 (1 本)
        var wake = new SemaphoreSlim(0, int.MaxValue);   // 回の starbreaker が終わった・回が終わったときの知らせ
        var tasks = new List<Task>();
        int done = 0, next = 0;
        int? failedAt = null;
        progress?.Invoke(0, jobs.Count);
        try
        {
            while (true)
            {
                // 始められるだけ始める (取り消し・失敗が出たら始めない)
                while (next < jobs.Count && failedAt == null && !ct.IsCancellationRequested)
                {
                    bool extra;
                    if (own.Wait(0)) extra = false;
                    else if (tryExtra != null && await tryExtra()) extra = true;
                    else break;
                    var n = next++;
                    runCts[n] = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tasks.Add(RunOne(n, extra, runCts[n]!.Token));
                }
                if (Volatile.Read(ref done) >= next && (next >= jobs.Count || failedAt != null || ct.IsCancellationRequested)) break;
                // 回の知らせか 1 秒 (足せる本数が増えたかを聞き直す)
                await wake.WaitAsync(TimeSpan.FromSeconds(1));
                for (int i = 0; i < next; i++)
                    if (outcomes[i] is { Error: { } e } && e is not OperationCanceledException && (failedAt == null || i < failedAt)) failedAt = i;
                if (failedAt is int f)
                    for (int i = f + 1; i < next; i++) try { runCts[i]?.Cancel(); } catch (ObjectDisposedException) { }
            }
            await Task.WhenAll(tasks);
        }
        catch
        {
            // 始める繰り返しそのものが例外になった (tryExtra が投げた等): 動いている回を全部止め、終わるのを待ってから投げる
            foreach (var c in runCts) try { c?.Cancel(); } catch (ObjectDisposedException) { }
            try { await Task.WhenAll(tasks); } catch { }
            throw;
        }
        finally
        {
            foreach (var c in runCts) c?.Dispose();
        }
        ct.ThrowIfCancellationRequested();
        if (failedAt is int fi) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(outcomes[fi]!.Error!).Throw();
        // 1 本ずつのときと同じ足し方を、回の番号の順に
        for (int n = 0; n < jobs.Count; n++)
        {
            var o = outcomes[n] ?? throw new InvalidOperationException($"{n + 1} 回目の書き出しの結果がありません");
            if (o.Error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(o.Error).Throw();
            var job = jobs[n];
            res.Runs++;
            if (o.Failed) { res.Errors.Add($"{job.Pattern}: 終了コード {o.Code} {o.Tail}"); res.Missing.AddRange(job.Socpaks); continue; }
            res.TotalGlbBytes += o.Length;
            res.MaxGlbBytes = Math.Max(res.MaxGlbBytes, o.Length);
            foreach (var (key, meshes) in o.Found)
            {
                (res.Raw.TryGetValue(key, out var chunks) ? chunks : res.Raw[key] = new()).Add(meshes);
                res.Meshes[key] = TakeMeshes(res, kinds, meshes);
            }
            // 形の無い socpak は GLB に子が出ない (空の GLB) ことがあるので、出てこなかったものは空として覚える
            foreach (var k in o.NotFound) { res.Missing.Add(k); res.Raw[k] = new(); res.Meshes[k] = new List<StationGlbMesh>(); }
        }
        if (kinds != null) res.NewKinds = ResolveKinds(res, kinds);
        progress?.Invoke(jobs.Count, jobs.Count);
        res.Seconds = sw.Elapsed.TotalSeconds;
        return res;

        // 1 回分: starbreaker を動かし、枠を返してから GLB を読んで消す。結果・例外は outcomes[n] に入れる (ここからは投げない)
        async Task RunOne(int n, bool extra, CancellationToken rct)
        {
            await Task.Yield();   // 呼んだ側の始める繰り返しを先に進める
            var job = jobs[n];
            var glb = Path.Combine(workDir, $"station_{n:D4}.glb");
            var o = new ExportOutcome();
            var slotHeld = true;
            void ReturnSlot()
            {
                if (!slotHeld) return;
                slotHeld = false;
                if (extra) releaseExtra?.Invoke(); else own.Release();
                wake.Release();
            }
            try
            {
                var args = $"socpak export {Quote(job.Pattern)} {Quote(glb)} --p4k {Quote(p4kPath)} --materials none --no-lights";
                (o.Code, o.Tail) = await run(args, rct);
                ReturnSlot();
                if (o.Code != 0 || !File.Exists(glb)) o.Failed = true;
                else
                {
                    o.Length = new FileInfo(glb).Length;
                    var byStem = job.Socpaks.ToDictionary(Stem, k => k, StringComparer.OrdinalIgnoreCase);
                    var found = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (container, meshes0) in kinds != null ? ReadMeshes(glb, IsLightMesh, n => NeedsShape(kinds, n)) : ReadMeshes(glb, IsExportMesh))
                    {
                        var stem = container.StartsWith("interior_", StringComparison.Ordinal) ? container[9..] : container;
                        if (!byStem.TryGetValue(stem, out var key)) continue;
                        found.Add(key);
                        // 表での選び分け (TakeMeshes) は、全部の回が終わった後に回の番号の順に行う (保存した結果から判定をやり直すとき (FromRaw) と同じ順にするため)
                        o.Found.Add((key, meshes0));
                    }
                    o.NotFound.AddRange(job.Socpaks.Where(k => !found.Contains(k)));
                }
            }
            catch (Exception e) { o.Error = e; }
            finally
            {
                ReturnSlot();
                try { if (File.Exists(glb)) File.Delete(glb); } catch (IOException) { }
                outcomes[n] = o;
                Interlocked.Increment(ref done);
                progress?.Invoke(Volatile.Read(ref done), jobs.Count);
                wake.Release();
            }
        }
    }

    // GLB を読んだ直後のメッシュ (meshes0) から、持つメッシュを選ぶ。表 (kinds) があれば、表に無い名前の票を数え (res.Votes)、
    // 板にならない部品 (表・形・名前のどれでも床・階段でない、灯りでもない) は持たない。表が無ければ全部持つ
    public static List<StationGlbMesh> TakeMeshes(StationMeshSet res, StationMeshKinds? kinds, List<StationGlbMesh> meshes0)
    {
        if (kinds == null) return meshes0;
        foreach (var m in meshes0)
        {
            if (m.Shape == null || kinds.Get(MeshKey(m.Mesh)) != null) continue;
            var k = MeshKey(m.Mesh);
            if (!res.Votes.TryGetValue(k, out var v)) res.Votes[k] = v = new int[3];
            v[(int)CandidateKind(m.Shape)]++;
        }
        return meshes0.Where(m => IsLightMesh(m.Mesh) || m.Shape is { } sh && (CandidateKind(sh) != StationMeshKind.None || KindOf(kinds, m.Mesh).Kind != StationMeshKind.None
            || NameKind(m.Mesh) != StationMeshKind.None)).ToList();
    }

    // 保存した GLB の読み取りの結果 (StationMeshCache) から、書き出し (ExportAsync) と同じメッシュの組を作る (starbreaker は動かさない)。
    // keys は要る socpak (全部が raw にあること。StationMeshCache.TryLoad が確かめる)。missing は保存したときに GLB に出てこなかった socpak
    public static StationMeshSet FromRaw(IReadOnlyDictionary<string, List<List<StationGlbMesh>>> raw, IReadOnlyCollection<string> missing, IEnumerable<string> keys, StationMeshKinds? kinds)
    {
        var res = new StationMeshSet { Kinds = kinds };
        // 並びは保存した並び (書き出しで Raw・Meshes に入れた順。BuildSources などが Meshes の並びで書くので、書き出しのときと同じにする)
        var want = keys.ToHashSet(StringComparer.Ordinal);
        foreach (var key in raw.Keys.Where(want.Contains))
        {
            var chunks = raw[key];
            res.Raw[key] = chunks;
            // 書き出しと同じく、子ごとに票を数え、Meshes は後の子のもの (子が無ければ空)
            res.Meshes[key] = new List<StationGlbMesh>();
            foreach (var meshes0 in chunks) res.Meshes[key] = TakeMeshes(res, kinds, meshes0);
            if (missing.Contains(key)) res.Missing.Add(key);
        }
        if (kinds != null) res.NewKinds = ResolveKinds(res, kinds);
        return res;
    }

    // 表に無かった名前の種類を、形の判定の票で決めて表に足す (足した数を返す)。票の多いほう (同じなら床・階段を先、階段を床より先)。
    // 形で床・階段にならなかった名前は、名前の語の決まり (NameKind) を使う (最後の手段)。名前の語の決まりにも当たる名前の Source は "name" (前と同じく、つながりを問わず板にする)
    public static int ResolveKinds(StationMeshSet set, StationMeshKinds kinds)
    {
        int n = 0;
        foreach (var (key, v) in set.Votes)
        {
            if (kinds.Get(key) != null) continue;
            // 仮の種類 (候補になるか)。決まるのは Plates のたどり (KeepTouching) の後 (FinalizeKinds)
            var shape = v[(int)StationMeshKind.Stair] == 0 && v[(int)StationMeshKind.Floor] == 0 ? StationMeshKind.None
                : v[(int)StationMeshKind.Stair] >= v[(int)StationMeshKind.Floor] ? StationMeshKind.Stair : StationMeshKind.Floor;
            var byName = NameKind(key);
            // 名前の語の決まりに当たる部品は、登録済みと同じく名前の種類のまま (手順 1。床の名前の部品は、傾けて置いてあれば SurfacePlate が坂の板にする)。
            // 当たらない部品は候補 (仮の種類は票の多いほう)。Checkmate の精錬所で、1 つの置き方が段に見えた util_stairs_platform_* が全部階段になり、床が消えていた
            var entry = byName != StationMeshKind.None ? new StationMeshKinds.Entry(byName, "name") : new StationMeshKinds.Entry(shape, "shape");
            if (entry.Kind == StationMeshKind.None) continue;   // 形で候補にもならない名前は登録しない (次の取り込みで判定し直す)
            kinds.Add(key, entry);
            set.AddedKeys.Add(key);
            n++;
        }
        return n;
    }

    // Plates の後に呼ぶ: この取り込みで形でだけ床・階段として表に足した名前のうち、どの部品も歩ける面につながらなかった (板にしなかった) ものを「どれでもない」にする
    // (表に床・階段として残すと、その名前のある socpak を毎回書き出すことになるため。RR_ARC_L1 で外壁の飾り・机・カーペットなどが形で床・階段になっていた)。直した数を返す
    public static int FinalizeKinds(StationMeshSet set)
    {
        if (set.Kinds == null) return 0;
        int n = 0;
        // この取り込みで足した名前: たどりの結果。床・階段にならなかったものは手元の表から消す (Replace に none)
        foreach (var key in set.AddedKeys)
            if (set.Kinds.Get(key) is { Source: "shape" } e)
            {
                var k = set.ShapeAccepted.GetValueOrDefault(key, StationMeshKind.None);
                if (k != e.Kind) { set.Kinds.Replace(key, new StationMeshKinds.Entry(k, "shape")); n++; }
            }
        // (2026-11-02、親の指示) 床と階段に分けた部品は、表で composite にする (同梱の表のものは変えない。Replace は手元の表だけ)。
        // 形で決めた名前は、たどりで残ったときだけ (ShapeAccepted)。名前の決まりの名前は Source を name のまま
        foreach (var key in set.CompositeKeys)
            if (set.Kinds.Get(key) is { } ce && ce.Kind != StationMeshKind.Composite && ce.Source != "bundled" && (ce.Source != "shape" || set.ShapeAccepted.ContainsKey(key)))
            { set.Kinds.Replace(key, new StationMeshKinds.Entry(StationMeshKind.Composite, ce.Source)); n++; }
        // (2026-11-02、親の指示「形で決まった種類は確定にしない。どこかの置き方で階段・床と判定されたら、そちらへ上書き」) 前から表にある形の名前は、
        // この取り込みで階段・床になったときだけ上書きする (none → 床・階段、床 → 階段)。none に戻すことはしない
        foreach (var key in set.ShapeSeen)
        {
            if (set.AddedKeys.Contains(key) || set.Kinds.Get(key) is not { Source: "shape" } e) continue;
            var k = set.ShapeAccepted.GetValueOrDefault(key, StationMeshKind.None);
            if (k == StationMeshKind.Stair && e.Kind != StationMeshKind.Stair || k == StationMeshKind.Floor && e.Kind == StationMeshKind.None)
            { set.Kinds.Replace(key, new StationMeshKinds.Entry(k, "shape")); n++; }
        }
        return n;
    }

    // 形の数のあるメッシュから、表に無い名前の票を数える (ハーネスでキャッシュから取り込みと同じことをするため。アプリでは ExportAsync が書き出しのときに数える)
    public static void VoteFromMeshes(StationMeshSet set)
    {
        if (set.Kinds == null) return;
        foreach (var m in set.Meshes.Values.SelectMany(x => x))
        {
            if (m.Shape == null || set.Kinds.Get(MeshKey(m.Mesh)) != null) continue;
            var k = MeshKey(m.Mesh);
            if (!set.Votes.TryGetValue(k, out var v)) set.Votes[k] = v = new int[3];
            v[(int)CandidateKind(m.Shape)]++;
        }
    }

    // ExportAsync の 1 回分の結果 (並行で動かした回を、後で番号の順にまとめるため)
    private sealed class ExportOutcome
    {
        public int Code;
        public string Tail = "";
        public bool Failed;
        public long Length;
        public List<(string Key, List<StationGlbMesh> Meshes)> Found { get; } = new();
        public List<string> NotFound { get; } = new();
        public Exception? Error;
    }

    // GLB の JSON の部分 (先頭の 12 バイトの次の最初のチャンク)
    public static JsonDocument ReadGlbJson(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[20];
        fs.ReadExactly(head);
        if (BitConverter.ToUInt32(head, 0) != 0x46546C67) throw new InvalidDataException($"GLB ではありません: {path}");
        var len = BitConverter.ToUInt32(head, 12);
        if (BitConverter.ToUInt32(head, 16) != 0x4E4F534A) throw new InvalidDataException($"GLB の最初のチャンクが JSON ではありません: {path}");
        var json = new byte[len];
        fs.ReadExactly(json);
        return JsonDocument.Parse(json);
    }

    // GLB の socpak ごとの子 (ルートの下の子の名前) → keep に合うメッシュ。ルート (CryEngine_Z_up) の行列は掛けない (上の説明)。
    // shape に合うメッシュは、三角形を読んで形の数 (StationGlbMesh.Shape) も付ける (形の判定。同じメッシュは 1 回だけ読む)。shape に合うメッシュは keep に合わなくても返す
    public static List<(string Container, List<StationGlbMesh> Meshes)> ReadMeshes(string path, Func<string, bool> keep, Func<string, bool>? shape = null)
    {
        using var doc = ReadGlbJson(path);
        var r = doc.RootElement;
        var res = new List<(string, List<StationGlbMesh>)>();
        if (!r.TryGetProperty("nodes", out var nodes) || !r.TryGetProperty("scenes", out var scenes)) return res;
        r.TryGetProperty("meshes", out var meshes);
        r.TryGetProperty("accessors", out var accessors);
        var sceneIdx = r.TryGetProperty("scene", out var si) ? si.GetInt32() : 0;
        // 形を数えるもの: (返す一覧, その中の位置, メッシュの番号, 行列)
        var shapeJobs = new List<(List<StationGlbMesh> List, int At, int Mesh, double[] W)>();
        foreach (var rootIdx in scenes[sceneIdx].GetProperty("nodes").EnumerateArray())
        {
            var root = nodes[rootIdx.GetInt32()];
            if (!root.TryGetProperty("children", out var kids)) continue;
            foreach (var c in kids.EnumerateArray())
            {
                var cn = nodes[c.GetInt32()];
                var name = cn.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var list = new List<StationGlbMesh>();
                Walk(c.GetInt32(), Identity());
                res.Add((name, list));

                void Walk(int ni, double[] parent)
                {
                    var n = nodes[ni];
                    var w = Mul(parent, LocalMatrix(n));
                    if (n.TryGetProperty("mesh", out var mi) && meshes.ValueKind == JsonValueKind.Array)
                    {
                        var mesh = meshes[mi.GetInt32()];
                        var mname = mesh.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                        var wantShape = shape != null && shape(mname);
                        if (keep(mname) || wantShape)
                        {
                            double[] mnv = [double.MaxValue, double.MaxValue, double.MaxValue], mxv = [double.MinValue, double.MinValue, double.MinValue];
                            foreach (var p in mesh.GetProperty("primitives").EnumerateArray())
                            {
                                if (!p.TryGetProperty("attributes", out var at) || !at.TryGetProperty("POSITION", out var pa)) continue;
                                var acc = accessors[pa.GetInt32()];
                                if (!acc.TryGetProperty("min", out var amin) || !acc.TryGetProperty("max", out var amax)) continue;
                                for (int k = 0; k < 3; k++) { mnv[k] = Math.Min(mnv[k], Num(amin[k])); mxv[k] = Math.Max(mxv[k], Num(amax[k])); }
                            }
                            if (mnv[0] <= mxv[0] && w.All(double.IsFinite) && mnv.Concat(mxv).All(double.IsFinite))
                            {
                                var cs = new (double, double, double)[8];
                                int q = 0;
                                foreach (var x in new[] { mnv[0], mxv[0] })
                                    foreach (var y in new[] { mnv[1], mxv[1] })
                                        foreach (var z in new[] { mnv[2], mxv[2] })
                                            cs[q++] = (w[0] * x + w[4] * y + w[8] * z + w[12], w[1] * x + w[5] * y + w[9] * z + w[13], w[2] * x + w[6] * y + w[10] * z + w[14]);
                                if (wantShape) shapeJobs.Add((list, list.Count, mi.GetInt32(), w));
                                list.Add(new StationGlbMesh(mname, cs));
                            }
                        }
                    }
                    if (n.TryGetProperty("children", out var ch))
                        foreach (var k in ch.EnumerateArray()) Walk(k.GetInt32(), w);
                }
            }
        }
        if (shapeJobs.Count > 0)
        {
            using var bin = new GlbBin(path, r);
            foreach (var g in shapeJobs.GroupBy(j => j.Mesh))
            {
                var tris = MeshTriangles(bin, meshes[g.Key]);
                foreach (var j in g) j.List[j.At] = j.List[j.At] with { Shape = ShapeOf(tris, j.W) };
            }
        }
        return res;
    }

    private static double[] Identity() => [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    // ノードの行列 (列優先)。matrix が無ければ translation・rotation (x,y,z,w)・scale
    private static double[] LocalMatrix(JsonElement n)
    {
        // 実データに null の成分の行列がある (Orison の socpak。NaN を書いたものと見られる)。数でない成分は NaN にし、その下のメッシュは使わない (ReadMeshes)
        if (n.TryGetProperty("matrix", out var m)) { var a = new double[16]; for (int i = 0; i < 16; i++) a[i] = Num(m[i]); return a; }
        double tx = 0, ty = 0, tz = 0, qx = 0, qy = 0, qz = 0, qw = 1, sx = 1, sy = 1, sz = 1;
        if (n.TryGetProperty("translation", out var t)) { tx = Num(t[0]); ty = Num(t[1]); tz = Num(t[2]); }
        if (n.TryGetProperty("rotation", out var r)) { qx = Num(r[0]); qy = Num(r[1]); qz = Num(r[2]); qw = Num(r[3]); }
        if (n.TryGetProperty("scale", out var s)) { sx = Num(s[0]); sy = Num(s[1]); sz = Num(s[2]); }
        return
        [
            (1 - 2 * (qy * qy + qz * qz)) * sx, 2 * (qx * qy + qz * qw) * sx, 2 * (qx * qz - qy * qw) * sx, 0,
            2 * (qx * qy - qz * qw) * sy, (1 - 2 * (qx * qx + qz * qz)) * sy, 2 * (qy * qz + qx * qw) * sy, 0,
            2 * (qx * qz + qy * qw) * sz, 2 * (qy * qz - qx * qw) * sz, (1 - 2 * (qx * qx + qy * qy)) * sz, 0,
            tx, ty, tz, 1,
        ];
    }

    private static double Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN;

    private static double[] Mul(double[] a, double[] b)
    {
        var c = new double[16];
        for (int col = 0; col < 4; col++)
            for (int row = 0; row < 4; row++)
            {
                double s = 0;
                for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[col * 4 + k];
                c[col * 4 + row] = s;
            }
        return c;
    }

    // ===== 形の判定 (2026-11-02、利用者の指示「通路か階段を判定する方法が名前だけに頼ってるのが間違い。形状、高さで判定できる」
    //   「判定したものを階段や通路に入れる処理をして、名前を登録するようにしろ」「ramp も通路 or 階段として扱う」) =====
    // GLB の三角形 (バイナリの部分) を読み、部品 (ノード) ごとに、上を向いた面を socpak の枠で数える。
    //   水平な面: 法線の傾きが FlatTiltDeg 以下。歩ける坂: FlatTiltDeg より大きく WalkTiltDeg 以下。下を向いた面: 法線の z が −cos(FlatTiltDeg) 以下。
    //   水平な面は高さ LevelBin (5 cm) の帯ごとの面積で持ち、帯を LevelJoin (7.5 cm) より離れたところで区切った「段」にまとめる。
    // 数の根拠は MeshKindOf の説明 (実データ)。
    public const double FlatTiltDeg = 10;
    // 46°: ARC_L2・HUR_L3 で歩く坂として使われている 45° 傾けた roomsm_floor_straight_04x02x0025_g (上面の法線の z 0.71) を入れるため (前は 40°)
    public const double WalkTiltDeg = 46;
    public const double LevelBin = 0.05;
    public const double LevelJoin = 0.075;

    // 部品 1 つの形の数 (socpak の枠。判定 MeshKindOf と、板 SurfacePlate に使う)
    internal sealed class StationShape
    {
        public double FlatArea, SlopeArea, DownArea, TotalArea;
        public double SlopeLo = double.MaxValue, SlopeHi = double.MinValue;
        // 坂の面の向きのそろい方 (面積 × 法線の水平の向き の和の長さ ÷ 面積 × 法線の水平の長さ の和。1 なら全部同じ向きの坂、パイプ・山のような丸い形は 0 に近い)
        public double SlopeCoherence;
        // 水平な面の段 (下から。高さ・xy は段の中の面積の重みの平均、面積)
        public List<(double Z, double Area, double X, double Y)> Levels = new();
        // (2026-11-02、利用者の指示「奥行で床か階段か決めろ」・親の決定 (A)) Levels の段ごとの本当の奥行 (水平な面の帯の幅。LevelDepth。面積が StepMinArea 未満の段は NaN)。
        // 凸包の幅では、L 字の段 (stairs_cornerin_04x04x0125_a の踏み面。凸包の短い幅 1.8〜2.7 m) の奥行が測れないため
        public List<double> LevelDepths = new();
        // 歩ける面 (水平な面と歩ける坂) の点: xy の凸包の頂点 (z 付き)
        public (double X, double Y, double Z)[] Hull = [];
        public double WalkLo = double.MaxValue, WalkHi = double.MinValue;
        // 箱の大きさ (三角形の点の xy の幅のうち大きいほう・z の幅)
        public double SizeXY, SizeZ;
        // 形から見た種類 (GuessKind) と、坂か (段でなく坂の面で上り下りするもの)
        public StationMeshKind Guess;
        public bool GuessRamp;
        // 主な水平な面 (面積のいちばん大きい段) の高さ・面積と、その段 ± MainBand の水平な面の xy の凸包 (z は MainZ)
        public double MainZ = double.NaN, MainArea;
        public (double X, double Y, double Z)[] MainHull = [];
        // 階段の下の端 (段の並びがあれば、いちばん下の段 − 段の高さの中央値 = 下の床の高さ。坂なら坂のいちばん低い所)
        public double StairLo = double.NaN;
        // 広い平らな面 (短いほうの幅・長いほうの長さが FloorSideMin 以上の段。高さ・面積・その段の水平な面の凸包) と、それ以外の歩ける面 (段・坂) の凸包。床と階段を 1 つに持つ部品を分けるのに使う
        public List<(double Z, double Area, (double X, double Y, double Z)[] Hull)> BigLevels = new();
        public (double X, double Y, double Z)[] StepHull = [];
        // 床と階段を 1 つに持つ部品か (段の並び・坂があり、広い平らな面もある)
        public bool IsComposite;
    }

    // GLB のバイナリの部分を読む (先頭の JSON の後の 2 つ目のチャンク)。accessor を必要なときに読む
    private sealed class GlbBin : IDisposable
    {
        private readonly FileStream _fs;
        private readonly long _bin;
        private readonly JsonElement _views, _accs;
        public GlbBin(string path, JsonElement root)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[20];
            _fs.ReadExactly(head);
            var jl = BitConverter.ToUInt32(head, 12);
            _bin = 20L + jl + 8;
            root.TryGetProperty("bufferViews", out _views);
            root.TryGetProperty("accessors", out _accs);
        }
        public void Dispose() => _fs.Dispose();

        private byte[] Raw(JsonElement acc, int elemSize, out int stride, out int count)
        {
            count = acc.GetProperty("count").GetInt32();
            var bv = _views[acc.GetProperty("bufferView").GetInt32()];
            stride = bv.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : elemSize;
            long off = (bv.TryGetProperty("byteOffset", out var bo) ? bo.GetInt64() : 0) + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt64() : 0);
            var len = count == 0 ? 0 : (long)stride * (count - 1) + elemSize;
            var buf = new byte[len];
            _fs.Position = _bin + off;
            _fs.ReadExactly(buf);
            return buf;
        }

        // 位置 (VEC3、float)。読めない形なら null
        public float[]? Positions(int ai)
        {
            var acc = _accs[ai];
            if (!acc.TryGetProperty("bufferView", out _) || acc.GetProperty("componentType").GetInt32() != 5126 || acc.GetProperty("type").GetString() != "VEC3") return null;
            var b = Raw(acc, 12, out var stride, out var n);
            var res = new float[n * 3];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < 3; k++) res[i * 3 + k] = BitConverter.ToSingle(b, i * stride + k * 4);
            return res;
        }

        public int[]? Indices(int ai)
        {
            var acc = _accs[ai];
            if (!acc.TryGetProperty("bufferView", out _)) return null;
            var ct = acc.GetProperty("componentType").GetInt32();
            int sz = ct switch { 5121 => 1, 5123 => 2, 5125 => 4, _ => 0 };
            if (sz == 0) return null;
            var b = Raw(acc, sz, out var stride, out var n);
            var res = new int[n];
            for (int i = 0; i < n; i++)
                res[i] = sz == 1 ? b[i * stride] : sz == 2 ? BitConverter.ToUInt16(b, i * stride) : (int)BitConverter.ToUInt32(b, i * stride);
            return res;
        }
    }

    // メッシュの三角形 (メッシュの枠。点 3 つずつ並べたもの)。三角形 (mode 4) でない primitive は使わない
    private static List<float[]> MeshTriangles(GlbBin bin, JsonElement mesh)
    {
        var res = new List<float[]>();
        foreach (var p in mesh.GetProperty("primitives").EnumerateArray())
        {
            if (p.TryGetProperty("mode", out var md) && md.GetInt32() != 4) continue;
            if (!p.TryGetProperty("attributes", out var at) || !at.TryGetProperty("POSITION", out var pa)) continue;
            var pos = bin.Positions(pa.GetInt32());
            if (pos == null) continue;
            var idx = p.TryGetProperty("indices", out var ii) ? bin.Indices(ii.GetInt32()) : Enumerable.Range(0, pos.Length / 3).ToArray();
            if (idx == null) continue;
            var t = new float[idx.Length / 3 * 9];
            for (int i = 0; i + 2 < idx.Length; i += 3)
                for (int v = 0; v < 3; v++)
                {
                    var q = idx[i + v];
                    if (q * 3 + 2 >= pos.Length) { t[i * 3 + v * 3] = float.NaN; continue; }
                    t[i * 3 + v * 3] = pos[q * 3]; t[i * 3 + v * 3 + 1] = pos[q * 3 + 1]; t[i * 3 + v * 3 + 2] = pos[q * 3 + 2];
                }
            res.Add(t);
        }
        return res;
    }

    // 三角形を行列 w (列優先) で移して、形の数を数える
    // (2026-11-02、親の決定 (A)) 水平な面 (三角形の集まり) の本当の奥行: 升目 (TreadTol 0.02 m。升目が LevelDepthMaxCells を超える大きな面は升目を広げる) に
    // 三角形を塗り、塗った升目から塗っていない升目までの距離 (8 方向の距離の近似。斜めは √2) のいちばん大きいもの d から、(2d − 1) × 升目 を帯の幅とする。
    // 幅 0.5 m の帯は 0.50 m、4 × 4 m の面は 4.0 m、幅 0.5 m の L 字の帯は 0.50 m (誤差は升目 1 つ分)。三角形が無ければ NaN
    public const int LevelDepthMaxCells = 4_000_000;
    public static double LevelDepth(List<((double X, double Y) A, (double X, double Y) B, (double X, double Y) C)> tris)
    {
        if (tris.Count == 0) return double.NaN;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var (a, b, c) in tris)
            foreach (var p in new[] { a, b, c }) { x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y); }
        if (!(x1 >= x0 && y1 >= y0) || !double.IsFinite(x1 - x0) || !double.IsFinite(y1 - y0)) return double.NaN;
        double cell = TreadTol;
        if ((x1 - x0) / cell * ((y1 - y0) / cell) > LevelDepthMaxCells) cell = Math.Sqrt((x1 - x0) * (y1 - y0) / LevelDepthMaxCells);
        int nx = (int)Math.Ceiling((x1 - x0) / cell) + 3, ny = (int)Math.Ceiling((y1 - y0) / cell) + 3;
        var d = new float[nx * ny];
        foreach (var (a, b, c) in tris)
        {
            int i0 = Math.Max(1, (int)Math.Floor((Math.Min(a.X, Math.Min(b.X, c.X)) - x0) / cell) + 1), i1 = Math.Min(nx - 2, (int)Math.Ceiling((Math.Max(a.X, Math.Max(b.X, c.X)) - x0) / cell) + 1);
            int j0 = Math.Max(1, (int)Math.Floor((Math.Min(a.Y, Math.Min(b.Y, c.Y)) - y0) / cell) + 1), j1 = Math.Min(ny - 2, (int)Math.Ceiling((Math.Max(a.Y, Math.Max(b.Y, c.Y)) - y0) / cell) + 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                {
                    double px = x0 + (i - 0.5) * cell, py = y0 + (j - 0.5) * cell;
                    double s1 = (b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X), s2 = (c.X - b.X) * (py - b.Y) - (c.Y - b.Y) * (px - b.X), s3 = (a.X - c.X) * (py - c.Y) - (a.Y - c.Y) * (px - c.X);
                    if ((s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0)) d[j * nx + i] = float.MaxValue;
                }
        }
        const float D1 = 1f, D2 = 1.41421356f;
        for (int j = 1; j < ny - 1; j++)
            for (int i = 1; i < nx - 1; i++)
            {
                int k = j * nx + i; if (d[k] == 0) continue;
                d[k] = Math.Min(d[k], Math.Min(Math.Min(d[k - 1] + D1, d[k - nx] + D1), Math.Min(d[k - nx - 1] + D2, d[k - nx + 1] + D2)));
            }
        float best = 0;
        for (int j = ny - 2; j >= 1; j--)
            for (int i = nx - 2; i >= 1; i--)
            {
                int k = j * nx + i; if (d[k] == 0) continue;
                d[k] = Math.Min(d[k], Math.Min(Math.Min(d[k + 1] + D1, d[k + nx] + D1), Math.Min(d[k + nx + 1] + D2, d[k + nx - 1] + D2)));
                best = Math.Max(best, d[k]);
            }
        return best <= 0 ? double.NaN : Math.Max(1, 2 * best - 1) * cell;
    }

    // 板の Depth: 形の段 (LevelDepths) のうち、高さが lo − LevelJoin 〜 hi + LevelJoin に入る段の本当の奥行のいちばん大きいもの (無ければ NaN)
    private static double PlateDepth(StationShape sh, double lo, double hi)
    {
        double best = double.NaN;
        for (int i = 0; i < sh.Levels.Count && i < sh.LevelDepths.Count; i++)
            if (sh.Levels[i].Z >= lo - LevelJoin && sh.Levels[i].Z <= hi + LevelJoin && !double.IsNaN(sh.LevelDepths[i]) && !(sh.LevelDepths[i] <= best)) best = sh.LevelDepths[i];
        return best;
    }

    private static StationShape ShapeOf(List<float[]> tris, double[] w)
    {
        var s = new StationShape();
        double cosFlat = Math.Cos(FlatTiltDeg * Math.PI / 180), cosWalk = Math.Cos(WalkTiltDeg * Math.PI / 180);
        var bins = new Dictionary<long, (double A, double Az, double Ax, double Ay)>();
        double snx = 0, sny = 0, snl = 0;
        var walk = new List<(double X, double Y, double Z)>();
        var flatPts = new List<(double X, double Y, double Z)>();   // 水平な面の点 (z は三角形の中心の高さ)
        var slopePts = new List<(double X, double Y, double Z)>();  // 歩ける坂の面の点
        var flatTris = new List<(double Z, (double X, double Y) A, (double X, double Y) B, (double X, double Y) C)>();   // 水平な面の三角形 (z は三角形の中心の高さ。段ごとの奥行に使う)
        double mnx = double.MaxValue, mxx = double.MinValue, mny = double.MaxValue, mxy = double.MinValue, mnz = double.MaxValue, mxz = double.MinValue;
        (double X, double Y, double Z) P(float[] t, int o) =>
            (w[0] * t[o] + w[4] * t[o + 1] + w[8] * t[o + 2] + w[12], w[1] * t[o] + w[5] * t[o + 1] + w[9] * t[o + 2] + w[13], w[2] * t[o] + w[6] * t[o + 1] + w[10] * t[o + 2] + w[14]);
        foreach (var t in tris)
            for (int i = 0; i + 8 < t.Length; i += 9)
            {
                if (float.IsNaN(t[i]) || float.IsNaN(t[i + 3]) || float.IsNaN(t[i + 6])) continue;
                var a = P(t, i); var b = P(t, i + 3); var c = P(t, i + 6);
                foreach (var v in new[] { a, b, c })
                {
                    mnx = Math.Min(mnx, v.X); mxx = Math.Max(mxx, v.X); mny = Math.Min(mny, v.Y); mxy = Math.Max(mxy, v.Y); mnz = Math.Min(mnz, v.Z); mxz = Math.Max(mxz, v.Z);
                }
                var u = Sub(b, a); var vv = Sub(c, a);
                var n = (X: u.Y * vv.Z - u.Z * vv.Y, Y: u.Z * vv.X - u.X * vv.Z, Z: u.X * vv.Y - u.Y * vv.X);
                var len = Len(n);
                if (!(len > 1e-9)) continue;
                var area = len / 2;
                s.TotalArea += area;
                var cz = n.Z / len;
                if (cz <= -cosFlat) { s.DownArea += area; continue; }
                if (cz < cosWalk) continue;
                walk.Add(a); walk.Add(b); walk.Add(c);
                var zc = (a.Z + b.Z + c.Z) / 3;
                if (cz >= cosFlat)
                {
                    flatPts.Add((a.X, a.Y, zc)); flatPts.Add((b.X, b.Y, zc)); flatPts.Add((c.X, c.Y, zc));
                    flatTris.Add((zc, (a.X, a.Y), (b.X, b.Y), (c.X, c.Y)));
                    s.FlatArea += area;
                    var key = (long)Math.Round(zc / LevelBin);
                    var o = bins.GetValueOrDefault(key);
                    bins[key] = (o.A + area, o.Az + area * zc, o.Ax + area * (a.X + b.X + c.X) / 3, o.Ay + area * (a.Y + b.Y + c.Y) / 3);
                }
                else
                {
                    s.SlopeArea += area;
                    slopePts.Add(a); slopePts.Add(b); slopePts.Add(c);
                    var hl = Math.Sqrt(n.X * n.X + n.Y * n.Y);
                    if (hl > 0) { snx += area * n.X / hl; sny += area * n.Y / hl; snl += area; }
                    s.SlopeLo = Math.Min(s.SlopeLo, Math.Min(a.Z, Math.Min(b.Z, c.Z))); s.SlopeHi = Math.Max(s.SlopeHi, Math.Max(a.Z, Math.Max(b.Z, c.Z)));
                }
            }
        if (mnx <= mxx) { s.SizeXY = Math.Max(mxx - mnx, mxy - mny); s.SizeZ = mxz - mnz; }
        // 段: 帯を高さの順に並べ、LevelJoin より離れたところで区切る
        if (snl > 0) s.SlopeCoherence = Math.Sqrt(snx * snx + sny * sny) / snl;
        double la = 0, laz = 0, lax = 0, lay = 0, last = double.NaN;
        foreach (var kv in bins.OrderBy(k => k.Key))
        {
            var z = kv.Value.Az / kv.Value.A;
            if (la > 0 && z - last > LevelJoin) { s.Levels.Add((laz / la, la, lax / la, lay / la)); la = 0; laz = 0; lax = 0; lay = 0; }
            la += kv.Value.A; laz += kv.Value.Az; lax += kv.Value.Ax; lay += kv.Value.Ay; last = z;
        }
        if (la > 0) s.Levels.Add((laz / la, la, lax / la, lay / la));
        if (walk.Count > 0)
        {
            s.WalkLo = walk.Min(p => p.Z); s.WalkHi = walk.Max(p => p.Z);
            s.Hull = ConvexHull3(walk);
        }
        if (s.Levels.Count > 0)
        {
            var main = s.Levels.MaxBy(l => l.Area);
            s.MainZ = main.Z; s.MainArea = main.Area;
            s.MainHull = ConvexHull3(flatPts.Where(p => Math.Abs(p.Z - main.Z) <= MainBand).Select(p => (p.X, p.Y, main.Z)).ToList());
        }
        GuessKind(s);
        foreach (var l in s.Levels)
            s.LevelDepths.Add(l.Area >= StepMinArea ? LevelDepth(flatTris.Where(t => Math.Abs(t.Z - l.Z) <= LevelJoin).Select(t => (t.A, t.B, t.C)).ToList()) : double.NaN);
        // (2026-11-02、親の指示) 床と階段を 1 つに持つ部品: 段の並び・坂 (Guess が階段) があり、縦横とも FloorSideMin 以上の平らな段もあるもの。
        // Orison の base_16x16x04_stairs_001_a は、下の平らな面 −25.62 m (60.85 m²)・段 15 (2.66 m²、0.25 m おき)・上の平らな面 −21.57 m (192.68 m²) を 1 つに持つ
        // (2026-11-02、利用者の指示「面積でなく、縦横の長さと形状がいい」) 広い平らな面は、段の水平な面の凸包の短いほうの幅と長いほうの長さが両方 FloorSideMin 以上のもの
        // (2026-11-02、親の決定 (A)) 短いほうの幅は、凸包の幅でなく本当の奥行 (LevelDepths) で見る (L 字の段の踏み面が広い平らな面になり、床の板になっていた。RR_ARC_L4)
        for (int li = 0; li < s.Levels.Count; li++)
        {
            var l = s.Levels[li];
            if (l.Area < StepMinArea) continue;
            var lh = ConvexHull3(flatPts.Where(p => Math.Abs(p.Z - l.Z) <= LevelJoin).Select(p => (p.X, p.Y, l.Z)).ToList());
            if (lh.Length >= 3 && s.LevelDepths[li] >= FloorSideMin && MaxLength(lh) >= FloorSideMin) s.BigLevels.Add((l.Z, l.Area, lh));
        }
        if (s.Guess == StationMeshKind.Stair && s.BigLevels.Count > 0)
        {
            var stepPts = slopePts.Concat(flatPts.Where(p => !s.BigLevels.Any(b => Math.Abs(p.Z - b.Z) <= LevelJoin))).ToList();
            s.StepHull = stepPts.Count >= 3 ? ConvexHull3(stepPts) : [];
            s.IsComposite = s.StepHull.Length >= 3;
        }
        return s;
    }

    // 形の判定の数 (2026-11-02、実データで決めた。harness の shapes。socpak の枠に移した三角形で数えたもの):
    //   ・階段 (段): 面積 StepMinArea (0.3 m²) 以上の水平な段が StepMinCount (3) 段以上、続く段の高さの差が StepRiseMin〜StepRiseMax (0.1〜0.45 m)、
    //     段の中心の xy が StepShiftMin〜StepShiftMax (0.1〜1.0 m) ずつ同じ向き (前の向きとの内積が正) に進むもの。
    //     実例: Lorville の stair_straight_04x04x02_a (1.7 m² の段が 0.21〜0.29 m おき・0.5 m ずつ進む)、NB の stair_a_straight_04x08x04_a (2.09 m² が 0.25 m おき・0.5 m)、
    //     Checkmate の util_stairs_a_mid_04_a (1.67 m² が 0.25 m おき)。手すり util_stairs_a_rail_* は段が 0.3 m² 未満か 0.4〜0.8 m おきで当たらない。
    //     階段の下の端 (StairLo) は、いちばん下の段 − 段の高さの中央値 (Lorville の stair_straight_04x04x02_a: 箱の底は下の床の 0.96 m 下だった)。
    //   ・坂: 歩ける坂の面積 RampMinArea (2 m²) 以上・水平な面の面積以上、坂の高さの幅 RampMinRise (0.5 m) 以上、坂の面の向きのそろい方 (SlopeCoherence) RampCoherence (0.45) 以上。
    //     実例: Checkmate の Ship Dealer の util_ramp_a (坂 5.70 m²、2.88〜5.01 m、そろい方 0.51)、NB の corridor_int_straight_drop_05x06x04_b (22.99 m²、4.0〜9.6 m、0.97)。
    //     パイプ・山のような丸い形はそろい方が低いので当たらないようにした (どれだけ外れるかは数えていない。未確認)。
    //   ・床・通路: 主な水平な面の面積 FloorMinArea (1 m²) 以上で、次のどちらか。
    //     ① 主な面の面積が全体の面積の FloorShare (0.2) 以上で、下向きの面の面積が水平な面の面積の FloorDownRatio (1.1) 倍以下
    //       (板の形の床。表と裏のある板は下向き = 上向き: util_stairs_platform_04x04_a 21.63 / 全体 56.57 m²。片面の床は下向き 0: floor_08x04x0025_a 32 / 40 m²。
    //        コンテナ shipping_container_* は下向き > 上向き、天井 roomsm_ceiling_* は下向き ≫ 上向きで当たらない)
    //     ② 主な面が FloorRoomArea (20 m²) 以上で、全体の FloorRoomShare (0.05) 以上、主な面より MainBand (0.6 m) 以上低い所に、主な面の 5 % 以上の水平な面が無いもの
    //       (壁・天井ごと 1 つのメッシュになった部屋・通路の床。Orison の showroom_elev_lobby_a: 主な面 −22.25 m の 476 m² / 全体 5,852 m²、それより低い面は −22.74・−22.50 m だけ)
    //   ・どれでもない: 上のどれにも当たらないもの (壁・天井・飾り・灯りなど)
    //   ベッド・敷物・低い台など、床の上に置く平らな物も ① に当たる (cargo_hub の improvised_bed_1_a・rug_col_*)。そのため、形でだけ床・階段になった部品は、
    //   歩ける面に接するもの (SurfaceGap・SurfaceRise) だけ板にする (Plates)
    public const double StepMinArea = 0.3, StepRiseMin = 0.1, StepRiseMax = 0.45, StepShiftMin = 0.1, StepShiftMax = 1.0;
    public const int StepMinCount = 3;
    // 段のそろい方 (実例の階段は段の高さ 0.25 m・進み 0.5 m がほぼ同じ。Lorville の stair_straight_04x04x02_a は 0.21〜0.29 m。
    // 外壁の飾り (addon_greeble_*・addon_gen_*) が段の並びに当たっていた (RR_ARC_L1) ので足した)
    public const double StepRiseTol = 0.1, StepShiftTol = 0.3, StepDirCos = 0.9;
    public const double RampMinArea = 2.0, RampMinRise = 0.5, RampCoherence = 0.45;
    public const double FloorMinArea = 1.0, FloorShare = 0.2, FloorDownRatio = 1.1, FloorRoomArea = 20, FloorRoomShare = 0.05;
    public const double MainBand = 0.6;

    private static void GuessKind(StationShape s)
    {
        // 段の並び (いちばん長いもの)
        var lv = s.Levels.Where(l => l.Area >= StepMinArea).ToList();
        int best = 1, bestStart = 0;
        for (int i = 0; i < lv.Count; i++)
        {
            int n = 1; (double X, double Y, double Dz, double Sh)? first = null;
            for (int j = i + 1; j < lv.Count; j++)
            {
                double dz = lv[j].Z - lv[j - 1].Z, dx = lv[j].X - lv[j - 1].X, dy = lv[j].Y - lv[j - 1].Y, sh = Math.Sqrt(dx * dx + dy * dy);
                if (dz < StepRiseMin || dz > StepRiseMax || sh < StepShiftMin || sh > StepShiftMax) break;
                // 段がそろっていること: 最初の段との高さの差が StepRiseTol 以内、進む長さが ± StepShiftTol (割合) 以内、向きの cos が StepDirCos 以上
                if (first is { } f && (Math.Abs(dz - f.Dz) > StepRiseTol || Math.Abs(sh - f.Sh) > StepShiftTol * f.Sh || (f.X * dx + f.Y * dy) / (f.Sh * sh) < StepDirCos)) break;
                first ??= (dx, dy, dz, sh);
                n++;
            }
            if (n > best) { best = n; bestStart = i; }
        }
        if (best >= StepMinCount)
        {
            var steps = Enumerable.Range(bestStart + 1, best - 1).Select(j => lv[j].Z - lv[j - 1].Z).OrderBy(x => x).ToList();
            s.Guess = StationMeshKind.Stair;
            s.StairLo = lv[bestStart].Z - steps[steps.Count / 2];
            return;
        }
        if (s.SlopeArea >= RampMinArea && s.SlopeArea >= s.FlatArea && s.SlopeHi - s.SlopeLo >= RampMinRise && s.SlopeCoherence >= RampCoherence)
        {
            s.Guess = StationMeshKind.Stair; s.GuessRamp = true; s.StairLo = s.SlopeLo;
            return;
        }
        if (s.MainArea >= FloorMinArea && s.TotalArea > 0)
        {
            if (s.MainArea >= FloorShare * s.TotalArea && s.DownArea <= FloorDownRatio * s.FlatArea) { s.Guess = StationMeshKind.Floor; return; }
            if (s.MainArea >= FloorRoomArea && s.MainArea >= FloorRoomShare * s.TotalArea
                && !s.Levels.Any(l => l.Z < s.MainZ - MainBand && l.Area >= 0.05 * s.MainArea)) { s.Guess = StationMeshKind.Floor; return; }
        }
        s.Guess = StationMeshKind.None;
    }

    // xy の凸包 (頂点に z を付けたまま。反時計回り)
    private static (double X, double Y, double Z)[] ConvexHull3(List<(double X, double Y, double Z)> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p.ToArray();
        static double Cross((double X, double Y, double Z) o, (double X, double Y, double Z) a, (double X, double Y, double Z) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new (double X, double Y, double Z)[p.Count * 2];
        int k = 0;
        for (int i = 0; i < p.Count; i++) { while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 0) k--; h[k++] = p[i]; }
        for (int i = p.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 0) k--; h[k++] = p[i]; }
        return h.Take(k - 1).ToArray();
    }

    // インスタンスごとに、その socpak のメッシュを局所の板にする
    // 乗り場 (sctransitgateway) の真上の板を除く: 乗り場の xy が板の凸包の GatewayRoofMargin (2.5 m) 内にあり、板の上面が乗り場より 2.5〜5 m 上のもの。
    // Checkmate でエレベーターのかごの屋根の格子 (roomsm_floor_grate_frame_04x04x0025_a、clinic_elev_001_rund_001 の床 + 3.3 m) が床になっていた
    // (2026-11-02、利用者の決定「A」) 1 m → 2.5 m。Area18 の Central の電車の乗り場 2 つ (z −5.0 m) は、プラットホームの塊 platform_transit_a・_b
    // (−5.25〜−1.29 m) の凸包から 2.2 m 離れていて、① (塊の床の高さを乗り場の高さにする) に当たらず、床の高さが上面 −1.29 m になっていた。
    // 乗り場と塊のあいだ (半径 25 m、高さ −12〜6 m) に、つなぐ部品 (階段・段差・橋・通路) の板は無かった。値は 2.2 m に 0.3 m の余裕を足したもの
    public const double GatewayRoofMargin = 2.5;
    // かごの屋根とみなす板の面積の上限 (Checkmate のかごの屋根の格子は 4 m × 4 m = 16 m²。Orison の Cloudview Center の広場の床
    // floor_custom_plat000_plaza_a (87 m × 90 m) がシャトルの乗り場の 2.65 m 上にあって屋根として除かれ、H 型の島の間の通路が抜けていた)
    public const double GatewayRoofMaxArea = 64;

    public static List<StationPlate> Plates(StationTree tree, StationMeshSet set) => Plates(tree, set, out _);

    // roofRemoved: 乗り場の真上として除いた板の数
    // 灯り 1 つの位置 (局所): 箱の xy の中心と、箱の上面の高さ
    private static (double X, double Y, double Z) LightPoint(StationInstance inst, StationGlbMesh m)
    {
        var pts = m.Corners.Select(c => inst.ToStation.Apply(c)).ToArray();
        return (pts.Average(p => p.X), pts.Average(p => p.Y), pts.Max(p => p.Z));
    }

    // 段の灯りの階段: 同じインスタンスの段の灯りを、xy で StepLightLink (4 m) 以内・高さの差 StepLightRise (1.3 m) 以内のものどうしでつないでまとめ、
    // まとまりの高さの幅が StepLightMinRise (0.2 m) 以上なら、階段の板 (Stair) を 1 つ作る。凸包は灯りの位置を StepLightPad (0.75 m) 広げたもの、
    // 下の端・上の端はいちばん低い・高い灯りの上面。値は実データの並びから決めた:
    //   Providence の駅の階段は灯りが 1 段 0.75 m 上がり、xy で約 1.4 m 進む (737.95 → 741.21 m)。Seraphim の配送センターの 5 段は、下の灯り (-9.72 m) と
    //   上の灯り (-8.47 m) が 3.5 m 離れ (高さの差 1.25 m)、下の灯りは通路の床 hangar_walkway_str_24m_a の縁から 0.75 m のところにある
    public const double StepLightLink = 4.0;
    public const double StepLightRise = 1.3;
    public const double StepLightMinRise = 0.2;
    public const double StepLightPad = 0.75;

    private static IEnumerable<StationPlate> StepLightPlates(int inst, List<(double X, double Y, double Z)> lights)
    {
        foreach (var cl in Chain(lights, (a, b) => Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y)) <= StepLightLink && Math.Abs(a.Z - b.Z) <= StepLightRise))
        {
            double lo = cl.Min(p => p.Z), hi = cl.Max(p => p.Z);
            if (hi - lo < StepLightMinRise) continue;
            var hull = PadHull(cl, StepLightPad);
            if (hull.Length >= 3) yield return new StationPlate(inst, "light_landing (段の灯り)", true, hull, hi, lo);
        }
    }

    // 床の灯りの床: 同じインスタンスの床の灯りを、xy で FloorLightLink (8 m) 以内・高さの差 0.1 m 以内でつないでまとめ、灯りが FloorLightMin (3) 個以上なら、
    // 床の板を 1 つ作る (凸包は灯りの位置を FloorLightPad (2 m) 広げたもの、上面は灯りの上面の中央の値)。
    // 値: Providence の駅の下の乗り場の床の灯り light_floor_01_a 8 個 (737.92 m) は、隣どうしが 3〜6 m 離れていた。広げる幅はその半分ほど (推測で決めた値)
    public const double FloorLightLink = 8.0;
    public const int FloorLightMin = 3;
    public const double FloorLightPad = 2.0;

    private static IEnumerable<StationPlate> FloorLightPlates(int inst, List<(double X, double Y, double Z)> lights)
    {
        foreach (var cl in Chain(lights, (a, b) => Math.Sqrt(Sq(a.X - b.X) + Sq(a.Y - b.Y)) <= FloorLightLink && Math.Abs(a.Z - b.Z) <= 0.1))
        {
            if (cl.Count < FloorLightMin) continue;
            var hull = PadHull(cl, FloorLightPad);
            var zs = cl.Select(p => p.Z).OrderBy(z => z).ToList();
            if (hull.Length >= 3) yield return new StationPlate(inst, "light_floor (床の灯り)", false, hull, zs[zs.Count / 2], zs[zs.Count / 2]);
        }
    }

    private static List<List<(double X, double Y, double Z)>> Chain(List<(double X, double Y, double Z)> pts, Func<(double X, double Y, double Z), (double X, double Y, double Z), bool> link)
    {
        var parent = Enumerable.Range(0, pts.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
                if (link(pts[i], pts[j])) parent[Find(i)] = Find(j);
        return Enumerable.Range(0, pts.Count).GroupBy(Find).Select(g => g.Select(i => pts[i]).ToList()).ToList();
    }

    // 点の並びを pad だけ広げた凸包 (各点のまわりの 8 方向の点の凸包)
    private static (double X, double Y)[] PadHull(List<(double X, double Y, double Z)> pts, double pad)
    {
        var l = new List<(double X, double Y)>();
        foreach (var p in pts)
            for (int k = 0; k < 8; k++) l.Add((p.X + pad * Math.Cos(k * Math.PI / 4), p.Y + pad * Math.Sin(k * Math.PI / 4)));
        return ConvexHull(l);
    }

    // 直前の Plates で、段の灯りから作った階段・床の灯りから作った床の数 (確かめ用)
    public static int LastStepLightStairs, LastFloorLightPlates;
    // 直前の Plates で、真下に本当の床があるので床にしなかった床の灯りの数 (確かめ用)
    public static int LastFloorLightsDropped;
    // 床の灯りの真下の本当の床の高さの幅 (灯りの上面から下へ。利用者の決定「灯りの下 2〜5 m に本当の床があれば灯りを床にしない」)
    public const double FloorLightBelowMin = 2.0, FloorLightBelowMax = 5.0;

    public static List<StationPlate> Plates(StationTree tree, StationMeshSet set, out int roofRemoved)
    {
        var res = new List<StationPlate>();
        LastStepLightStairs = 0; LastFloorLightPlates = 0;
        var gws = tree.Points.Where(p => p.Kind == "sctransitgateway" && p.Pos != null).Select(p => p.Pos!.Value).ToList();
        int removed = 0;
        var blocks = new List<int>();
        var floorLightAt = new List<(int At, int Inst, List<(double X, double Y, double Z)> Lights)>();
        var shapeOnly = new HashSet<StationPlate>(ReferenceEqualityComparer.Instance);
        LastShapePlates = 0; LastShapeOnlyKept = 0; LastShapeOnlyDropped = 0;
        LastBadPlates = 0; LastBadPlateNames.Clear();
        foreach (var inst in tree.Instances)
        {
            if (!set.Meshes.TryGetValue(inst.Socpak, out var meshes)) continue;
            foreach (var m in meshes)
            {
                // (2026-11-02) 種類は表 (形で判定して登録したもの) → 名前の語の決まり の順 (KindOf)。形の数があれば、板は実際の上向きの面から作る (SurfacePlate)
                var kind = set.Kinds != null ? KindOf(set.Kinds, m.Mesh) : new StationMeshKinds.Entry(NameKind(m.Mesh), "name");
                // 形で決めた種類 (shape) の部品は、表の種類によらず、この置き方の形で候補にする (取り込むたびに判定し直す)
                if (kind.Source == "shape" && m.Shape != null) kind = kind with { Kind = CandidateKind(m.Shape) };
                if (kind.Kind == StationMeshKind.None) continue;
                if (m.Shape is { } shp)
                {
                    var sps = SurfacePlates(inst, m.Mesh, shp, kind.Kind);
                    if (shp.IsComposite && sps.Count > 0) set.CompositeKeys.Add(MeshKey(m.Mesh));
                    if (sps.Count == 0)
                    {
                        // 候補 (形で決めた種類) でない、床・階段の種類の部品だけを浮いたものにする (候補は形で決まるので、上向きの面が無ければもともと候補にならない)
                        if (kind.Source != "shape")
                        {
                            var c = inst.ToStation.Apply((m.Corners.Average(q => q.X), m.Corners.Average(q => q.Y), m.Corners.Max(q => q.Z)));
                            set.NoUpFace.Add(new StationFloating(m.Mesh, KindLabel(kind), c.X, c.Y, c.Z, NoUpFaceReason));
                        }
                        continue;
                    }
                    foreach (var sp in sps)
                    {
                        // (2026-11-02) 座標が壊れた板 (有限でない・PlateCoordLimit を超える) は使わない (BadPlate の説明)
                        if (BadPlate(sp)) { LastBadPlates++; LastBadPlateNames.Add(m.Mesh); continue; }
                        if (!sp.Stair && HullArea(sp.Hull) <= GatewayRoofMaxArea && gws.Any(g => sp.Top - g.Z >= 2.5 && sp.Top - g.Z <= 5 && DistToConvex(sp.Hull, g.X, g.Y) <= GatewayRoofMargin))
                        {
                            removed++;
                            continue;
                        }
                        // 1. 表に登録済みの部品 (前の取り込みで決めたもの・名前の決まり) は、そのままつなぐ。この取り込みで初めて見た部品は候補 (KeepTouching でたどる)
                        if (kind.Source == "shape") { shapeOnly.Add(sp); set.ShapeSeen.Add(MeshKey(m.Mesh)); }
                        res.Add(sp);
                        LastShapePlates++;
                    }
                    continue;
                }
                var stair = kind.Kind is StationMeshKind.Stair or StationMeshKind.Composite;
                var pts = m.Corners.Select(c => inst.ToStation.Apply(c)).ToArray();
                var hull = ConvexHull(pts.Select(p => (p.X, p.Y)).ToList());
                if (hull.Length < 3) continue;
                double top = pts.Max(p => p.Z), bottom = pts.Min(p => p.Z);
                var ramp = false;
                if (!stair)
                {
                    // 箱の 3 辺 (Corners の並び: [0]=(min,min,min)、[1] は z、[2] は y、[4] は x の辺の先)。いちばん短い辺が PlateThin 以下なら板で、
                    // その辺 (板の法線) の傾きが 10° 以下なら床、45° 以下ならスロープ、それより大きければ縦の板として除く。
                    // 短い辺の無い塊 (Orison の床の造形など。Checkmate では bulkhead の roomsm_blkhead_*_mid_floor_* 2 個) は、下の後処理で床の高さを決める
                    // (Checkmate で、z の幅で一律に除いていたため roomsm_floor_straight_04x02x0025_g などの傾いた床 (2〜4 m) が抜け、BlackMarket の入口の床が途切れていた)
                    var ex = Sub(pts[4], pts[0]); var ey = Sub(pts[2], pts[0]); var ez = Sub(pts[1], pts[0]);
                    var thin = new[] { ex, ey, ez }.OrderBy(Len).First();
                    if (Len(thin) <= PlateThin)
                    {
                        var tilt = Math.Acos(Math.Min(1, Math.Abs(thin.Z) / Math.Max(1e-9, Len(thin)))) * 180 / Math.PI;
                        if (tilt > 45) continue;
                        if (tilt > 10) { ramp = true; stair = true; }
                    }
                    else blocks.Add(res.Count);
                }
                if (!stair && HullArea(hull) <= GatewayRoofMaxArea && gws.Any(g => top - g.Z >= 2.5 && top - g.Z <= 5 && DistToConvex(hull, g.X, g.Y) <= GatewayRoofMargin))
                {
                    removed++;
                    if (blocks.Count > 0 && blocks[^1] == res.Count) blocks.RemoveAt(blocks.Count - 1);
                    continue;
                }
                var plate = new StationPlate(inst.Index, m.Mesh, stair, hull, top, bottom, ramp);
                if (BadPlate(plate)) { LastBadPlates++; LastBadPlateNames.Add(m.Mesh); if (blocks.Count > 0 && blocks[^1] == res.Count) blocks.RemoveAt(blocks.Count - 1); continue; }
                res.Add(plate);
            }
            // 段の灯り・床の灯りから作る板 (2026-11-01、親の決定「案 b」)
            var stepLights = meshes.Where(m => IsStepLightMesh(m.Mesh)).Select(m => LightPoint(inst, m)).ToList();
            var floorLights = meshes.Where(m => IsFloorLightMesh(m.Mesh)).Select(m => LightPoint(inst, m)).ToList();
            foreach (var p in StepLightPlates(inst.Index, stepLights)) { res.Add(p); LastStepLightStairs++; }
            // 床の灯りの床は、全部のインスタンスの床の板ができてから作る (下の本当の床を見るため)。並びは前と同じ所に入れる
            if (floorLights.Count > 0) floorLightAt.Add((res.Count, inst.Index, floorLights));
        }
        // (2026-11-02、利用者の決定「A」) 床の灯りの真下 FloorLightBelowMin〜FloorLightBelowMax (2〜5 m) に本当の床 (床のメッシュの板。階段・スロープ・灯りから作った板は除く) が
        // あれば、その灯りは床にしない (天井の灯り。New Babbage の Admin Office・Whammers の偽の床)。真下 = 灯りの xy が板の凸包の中。
        // 塊 (blocks。高さがまだ決まっていない板) は、上面と底のどちらかが範囲にあれば床とみなす。
        // 残った灯りから前と同じ決まり (FloorLightPlates) で床を作り、インスタンスの並びの元の位置に入れる (塊の番号もずらす)
        LastFloorLightsDropped = 0;
        var realFloors = res.Where(p => !p.Stair).ToList();
        for (int k = floorLightAt.Count - 1; k >= 0; k--)
        {
            var (at, instIndex, lights) = floorLightAt[k];
            var kept = lights.Where(l => !realFloors.Any(p =>
                (p.Top <= l.Z - FloorLightBelowMin && p.Top >= l.Z - FloorLightBelowMax || p.Bottom <= l.Z - FloorLightBelowMin && p.Bottom >= l.Z - FloorLightBelowMax)
                && InConvex(p.Hull, l.X, l.Y))).ToList();
            LastFloorLightsDropped += lights.Count - kept.Count;
            var add = FloorLightPlates(instIndex, kept).ToList();
            if (add.Count == 0) continue;
            res.InsertRange(at, add);
            LastFloorLightPlates += add.Count;
            for (int b = 0; b < blocks.Count; b++) if (blocks[b] >= at) blocks[b] += add.Count;
        }
        // 乗り場の縁 (IsBalcony): 乗り場 (sctransitgateway) が凸包の BalconyGatewayRadius (5 m) 以内にあり、その高さが箱の z の範囲 (±0.5 m) にあれば、
        // 上面・底をその乗り場の高さにする (乗り場の高さ = 人の立つ高さ。Cloudview Center で実測と一致)。乗り場の無い縁は床にしない。塊の後処理より先に決める
        var dropBalcony = new HashSet<int>();
        for (int i = 0; i < res.Count; i++)
        {
            var p = res[i];
            if (p.Stair || !IsBalcony(p.Mesh)) continue;
            var g = gws.Where(q => q.Z >= p.Bottom - 0.5 && q.Z <= p.Top + 0.5 && DistToConvex(p.Hull, q.X, q.Y) <= BalconyGatewayRadius)
                .OrderBy(q => DistToConvex(p.Hull, q.X, q.Y)).Select(q => (double?)q.Z).FirstOrDefault();
            if (g is { } gz) res[i] = p with { Top = gz, Bottom = gz };
            else dropBalcony.Add(i);
            blocks.Remove(i);
        }
        // 塊の床の高さ: 塊の xy の 3 m 以内に (凸包どうしの近さ)、塊の底から 0.6 m 以内の高さの (塊でない) 平らな床の板があれば、底を床とみなす
        // (Orison の Kel-To の床 kel_to_int_floor_001 (底 -0.79 m・上面 3.59 m) は、まわりの arcade の床 (-0.79 m) と続く。板の中心で比べていたときは大きな床の中心が遠く当たらなかった)
        // (Checkmate の roomsm_blkhead_12m_mid_floor_12x04x04_a は高さ 4 m の出入口の枠で、底 128.53 m が通路の床 128.75 m と続く。上面 132.47 m は床ではない)。
        // そうでなければ上面を床とみなす (推測。Orison の床の造形 orison_sp_st_floor_a (4 m) などはどちらか確かめていない)
        // 底を床にした塊も、次の回では平らな床として数える (Orison の arcade の床は塊どうしで続く。変わらなくなるまで繰り返す)
        var pending = blocks.ToHashSet();
        for (bool changed = true; changed;)
        {
            changed = false;
            var flats = res.Where((p, i) => !p.Stair && !pending.Contains(i)).ToList();
            foreach (var i in pending.ToList())
            {
                var b = res[i];
                if (!flats.Any(f => Math.Abs(f.Top - b.Bottom) <= 0.6 && HullGap(b.Hull, f.Hull) <= 3)) continue;
                res[i] = b with { Top = b.Bottom };
                pending.Remove(i);
                changed = true;
            }
        }
        // (2026-11-01) 上の規則で底にならなかった塊は、塊の中の人の立つ高さがデータにあれば、それを床の高さにする:
        //   ① 塊の凸包の GatewayRoofMargin (2.5 m) 以内にある乗り場 (sctransitgateway) の高さ (箱の z の範囲 ± 0.5 m のもの。乗り場の高さ = 人の立つ高さ。乗り場の縁 IsBalcony と同じ考え)
        //   ② ①が無ければ、塊に接する (JoinGap 1 m 以内) 階段の板 (スロープを除く) の端 (下の端・上の端) のうち、箱の z の範囲 (± 0.1 m) にあるもの
        //   候補の高さの幅が JoinRise (0.6 m) 以内なら、その中央の値。幅がそれより大きい (どの高さか決められない) か、候補が無ければ、前と同じく上面
        // Orison の Crusader Showroom の入口のロビーの床 showroom_ent_lobby_floor_a (箱 0.16〜13.79 m) は、中のエレベーターの乗り場が 2.6 m、
        // Vision Center の入口の下の床 conv_building_ent_floor_lower_a (箱 -1358.81〜-1354.78 m) は、接する階段 stair_a_straight_04x08x04_a の下の端が -1357.78 m で、
        // どちらも上面を床にしていた
        LastBlockByGateway = 0; LastBlockByStair = 0; LastBlockSlope = 0;
        var stairPl = res.Where(p => p.Stair && !p.Ramp).ToList();
        foreach (var i in pending.ToList())
        {
            var b = res[i];
            var cand = gws.Where(g => g.Z >= b.Bottom - 0.5 && g.Z <= b.Top + 0.5 && DistToConvex(b.Hull, g.X, g.Y) <= GatewayRoofMargin).Select(g => g.Z).ToList();
            var byGw = cand.Count > 0;
            if (!byGw)
                foreach (var s in stairPl.Where(s => HullGap(s.Hull, b.Hull) <= JoinGap))
                    foreach (var z in new[] { s.Bottom, s.Top })
                        if (z >= b.Bottom - 0.1 && z <= b.Top + 0.1) cand.Add(z);
            // 名前に slope のある塊で、候補の高さの幅が JoinRise より大きいものは、スロープ (下の端 = いちばん低い候補、上の端 = いちばん高い候補) にする
            // (Vision Center の入口の conv_building_ent_floor_slope_a。箱 -1358.75〜-1350.27 m に、接する階段の端が -1357.78〜-1351.0 m で、下の床と上の床をつないでいた)
            if (cand.Count >= 2 && cand.Max() - cand.Min() > JoinRise && Words(b.Mesh).Contains("slope"))
            {
                res[i] = b with { Stair = true, Ramp = true, Bottom = cand.Min(), Top = cand.Max() };
                pending.Remove(i);
                LastBlockSlope++;
                continue;
            }
            if (cand.Count == 0 || cand.Max() - cand.Min() > JoinRise) continue;
            var zs = cand.OrderBy(z => z).ToList();
            res[i] = b with { Top = zs[zs.Count / 2] };
            pending.Remove(i);
            if (byGw) LastBlockByGateway++; else LastBlockByStair++;
        }
        roofRemoved = removed;
        var outPlates = res.Where((p, i) => !dropBalcony.Contains(i)).ToList();
        if (shapeOnly.Count > 0) outPlates = KeepTouching(outPlates, shapeOnly, set.ShapeAccepted);
        return AssignGroups(outPlates);
    }

    // (2026-11-02、利用者の原則「店のフロア、通路、階段、すき間はない」「すき間の無い高低差が 0.5m 未満の続きの面になる」)
    // 歩ける面どうしのつながり: 真上から見た形のすき間が SurfaceGap 以内で、高さ (板の Bottom〜Top の範囲どうし) の差が SurfaceRise 未満。
    // SurfaceRise 0.5 m は利用者の決めた値。SurfaceGap 0.3 m は利用者の言う「数 cm〜数十 cm」の上のほう (実データでの詰めはしていない。未確認)
    public const double SurfaceGap = 0.3;
    public const double SurfaceRise = 0.5;
    public static bool SurfacesTouch(StationPlate a, StationPlate b) =>
        Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top) < SurfaceRise && HullGap(a.Hull, b.Hull) <= SurfaceGap;

    // (2026-11-02) 座標が壊れた板: 凸包の点・上面・底が有限でないか、絶対値が PlateCoordLimit を超えるもの。
    // Nyx_Levski の町 (levski_v2_ext。外す決まりの Child を外さなくした後) の teachsshipshop.socpak の部品 teachsshipshop_a の形の数から作った板は、
    // 凸包の幅が 9.0e15 m × 3.5e31 m などになっていて、板の升目 (BoxCells。8 m ごと) を作るところで取り込みのメモリが 30 GB を超えた (2026-11-02 の取り込みで確かめた)。
    // 形の数が壊れる理由は未確認 (GLB の読み取り・形の数の作り方のどちらかは調べていない)。値: ステーションの局所の座標は大きいものでも数十 km
    // (Orison の Vision Center が (11819, -48544)) なので、その 20 倍の 1,000 km
    public const double PlateCoordLimit = 1_000_000;
    // 直前の Plates で使わなかった板の数と部品の名前 (同じスレッドの直前の呼び出しのもの。並行の取り込みで混ざらないようにスレッドごと)
    [ThreadStatic] public static int LastBadPlates;
    [ThreadStatic] private static List<string>? _lastBadPlateNames;
    public static List<string> LastBadPlateNames => _lastBadPlateNames ??= new();
    private static bool BadPlate(StationPlate p) =>
        !double.IsFinite(p.Top) || !double.IsFinite(p.Bottom) || Math.Abs(p.Top) > PlateCoordLimit || Math.Abs(p.Bottom) > PlateCoordLimit
        || p.Hull.Any(q => !double.IsFinite(q.X) || !double.IsFinite(q.Y) || Math.Abs(q.X) > PlateCoordLimit || Math.Abs(q.Y) > PlateCoordLimit);

    // 直前の Plates で、形の数から作った板の数・形でだけ床・階段になった板のうち、歩ける面につながって残した数・つながらず除いた数 (確かめ用)
    public static int LastShapePlates, LastShapeOnlyKept, LastShapeOnlyDropped;

    // (2026-11-02、利用者の指示の手順) 表に無い部品 (cand) を、登録済みの部品の板 (seeds) からたどって、階段・床に決める:
    //   1. 登録済みの部品の板でつなぐ (seeds)。
    //   2. 登録の無い部品のうち、上面が平らで人が乗れる大きさ (CandidateKind: 主な水平面 StandArea 以上。段の並び・坂の形のものも) の板で、
    //      今の歩ける面に接し (SurfacesTouch: すき間 SurfaceGap 以内、高さの差 SurfaceRise 0.5 m 未満 = 階段の一段くらい) ているものをたどる (繰り返す)。
    //   3. たどった板のうち、短いほうの幅か長いほうの長さが FloorSideMin 未満のもの (段) と段の形・坂の形のものは、つながった段のまとまりごとに、まとまりの下の端と上の端の両方に
    //      床 (登録済みの板・広い板) が接する (2 つの高さの床をつなぐ) ときだけ階段にする。つながる先の無い段 (床と同じ高さの敷物・行き止まりの台) は除いて、たどり直す。
    //   4. 広い板は床にする。その広い面の先に段が続き、店などが無ければ踊り場として階段の一部にするのは、前と同じ Landings (利用者の決定 C・板ごと) が行う。
    //   5. 決めた種類を result (名前 → 階段・床) に入れる (FinalizeKinds で表に登録する)。
    // StandArea 0.8 m²: 実例の階段の段の面積 (util_stairs_a_mid_04_a 1.67 m²、stair_straight_04x04x02_a 1.7 m²) の半分ほど。
    public const double StandArea = 0.8;
    // FloorSideMin 1.0 m: 床とみなす平らな面の短いほうの幅・長いほうの長さの下限。Seraphim の階段の 1 段の奥行き 0.5 m (TreadMin) の 2 倍
    // (段は短いほうの幅が 1 段の奥行きくらい。Checkmate のいちばん小さい踊り場 util_stairs_platform_01x02_a は 1 m × 2 m で、床に入る)
    public const double FloorSideMin = 1.0;

    // 候補の仮の種類: 段の並び・坂の形なら階段、主な水平面が StandArea 以上なら床、どちらでもなければ候補にしない
    public static StationMeshKind CandidateKind(StationShape sh)
    {
        var k = sh.Guess == StationMeshKind.Stair ? StationMeshKind.Stair : sh.MainArea >= StandArea || sh.Guess == StationMeshKind.Floor ? StationMeshKind.Floor : StationMeshKind.None;
        return k != StationMeshKind.None && !WideEnough(sh, k) ? StationMeshKind.None : k;
    }

    // (2026-11-02、利用者の決まり「既存の階段の一段の奥行(Seraphim で私が位置を記録したもの) より短い幅や奥行きのものは歩けない部品として除外」)
    // TreadMin 0.5 m: Seraphim (RR_CRU_LEO) のガレリア 1→2 層の stairs_04x04x08_a と上の stairs_04x02x01_a の 1 段の進み (段の中心の xy の差。どの段も 0.5 m)。
    // 形で判定する部品 (候補) だけに当てる (名前の決まり・同梱の表で決まった部品には当てない)。測りの誤差に TreadTol (0.02 m) を見る
    public const double TreadMin = 0.5, TreadTol = 0.02;

    private static bool WideEnough(StationShape sh, StationMeshKind k)
    {
        double min = TreadMin - TreadTol;
        if (k == StationMeshKind.Stair && !sh.GuessRamp)
        {
            // 段の並びの 1 段ずつ: 奥行き (段の中心の進み) と幅 (段の面積 ÷ 奥行き) の両方が基準以上の段が、StepMinCount 段以上続くこと
            var lv = sh.Levels.Where(l => l.Area >= StepMinArea).ToList();
            int run = 1, best = 1;
            for (int j = 1; j < lv.Count; j++)
            {
                double dz = lv[j].Z - lv[j - 1].Z, d = Math.Sqrt(Sq(lv[j].X - lv[j - 1].X) + Sq(lv[j].Y - lv[j - 1].Y));
                bool ok = dz >= StepRiseMin && dz <= StepRiseMax && d >= min && lv[j].Area / d >= min;
                run = ok ? run + 1 : 1; best = Math.Max(best, run);
            }
            return best >= StepMinCount;
        }
        // 床・坂: 上向きの面の凸包の短いほうの幅
        var h = k == StationMeshKind.Floor && sh.MainHull.Length >= 3 ? sh.MainHull : sh.Hull;
        return h.Length >= 3 && MinWidth(h) >= min;
    }

    // (2026-11-02、利用者の決定「「案 1 に加え、上下の差 0.3 m 未満の平らな坂・階段の板も床に戻す」 奥行で床か階段か決めろ。階段の奥行は場所では大きくは変わらない」。
    //  親の答え: 比べる長さは板の短い辺 (凸包の短いほうの幅)。一段の奥行 0.50 m (TreadMin) より長ければ床、以下なら階段。測りの誤差は TreadTol (0.02 m))
    // 床か階段かの奥行の境: 短い辺がこれより長い板を床にする。TreadMin は Seraphim の階段の一段の進み 0.5 m (段の中心の xy の差。2026-11-02 に
    // stairs_04x02x01_a・stairs_04x04x08_a・stairs_04x06x03_a・stairs_06x02x01_a・stairs_02x02x01_a・util_stairs_a_mid_a・util_stairs_a_mid_04_a・stair_straight_08x02x01_a で
    // どれも 0.50 m と測り直した。上面の幅は段の鼻の重なりで 0.44〜0.48 m と短く出る)。その測りの誤差 TreadTol を足し、0.52 m より長いものを床にする
    public const double FlatStairRise = 0.3;
    public static double FloorDepthMin => TreadMin + TreadTol;
    // (2026-11-02、親の決定 (A)) 板の本当の奥行 (Depth) で見る。形の数の無い板 (Depth が NaN) だけ凸包の短いほうの幅で見る
    static bool DeeperThanTread(StationPlate p) => (double.IsNaN(p.Depth) ? MinWidth2(p.Hull) : p.Depth) > FloorDepthMin;

    // 1. 平らな坂・階段の板: 階段・坂の板 (Stair) のうち、踊り場 (Landings が踊り場にした板) でなく、上下の差 (Top − Bottom) が FlatStairRise m 未満で、
    //    短い辺が一段の奥行より長いもの (DeeperThanTread) を床の板にする。床と接するかは見ない
    // 2. 案 1 (調べの担当 D の表の案 1「踊り場の板が床の板と xy 0.3 m 以内・高さの差 0.5 m 未満で接し、短い辺 1.0 m 以上なら床に戻す」。
    //    短い辺の決まりは、親の答えにより 1 と同じ奥行の決まりに置き換えた): 踊り場の板 (Landing) のうち、床の板 (階段でない板) と
    //    xy のすき間 SurfaceGap (0.3 m) 以内・上面の高さの差 SurfaceRise (0.5 m) 未満で接し、短い辺が一段の奥行より長いものを床に戻す。
    //    戻した板も床として、変わらなくなるまで繰り返す (D のハーネス scratchpad\isle\h\Landing.cs の Apply と同じ。接するかの見方 (Touch) も同じ)。
    // 床にした板は、坂・踊り場・階段のつながり (Link) を外し、底を上面にする (D のハーネスと同じ)。Landings の後に呼ぶ (StationMapExports.ComposeJson)
    public static List<StationPlate> FlatStairsToFloor(List<StationPlate> plates, out int flatChanged, out int landingChanged)
    {
        static StationPlate ToFloor(StationPlate p) => p with { Stair = false, Ramp = false, Landing = false, Link = -1, Bottom = p.Top };
        int n = 0;
        var cur = plates.Select(p =>
        {
            if (!p.Stair || p.Landing || p.Top - p.Bottom >= FlatStairRise) return p;
            if (!DeeperThanTread(p)) return p;
            n++;
            return ToFloor(p);
        }).ToList();
        flatChanged = n;
        int total = 0;
        while (true)
        {
            var floorsP = cur.Where(p => !p.Stair).ToList();
            var change = new Dictionary<StationPlate, StationPlate>(ReferenceEqualityComparer.Instance);
            foreach (var l in cur.Where(p => p.Landing))
            {
                if (!DeeperThanTread(l)) continue;
                double x0 = l.Hull.Min(q => q.X) - SurfaceGap, x1 = l.Hull.Max(q => q.X) + SurfaceGap, y0 = l.Hull.Min(q => q.Y) - SurfaceGap, y1 = l.Hull.Max(q => q.Y) + SurfaceGap;
                if (floorsP.Any(f => !ReferenceEquals(f, l) && Math.Abs(f.Top - l.Top) < SurfaceRise
                        && f.Hull.Max(q => q.X) >= x0 && f.Hull.Min(q => q.X) <= x1 && f.Hull.Max(q => q.Y) >= y0 && f.Hull.Min(q => q.Y) <= y1
                        && HullGap(l.Hull, f.Hull) <= SurfaceGap))
                    change[l] = ToFloor(l);
            }
            if (change.Count == 0) break;
            total += change.Count;
            cur = cur.Select(p => change.TryGetValue(p, out var q) ? q : p).ToList();
        }
        landingChanged = total;
        return cur;
    }

    private static double MinWidth2((double X, double Y)[] h) => MinWidth(h.Select(p => (p.X, p.Y, 0.0)).ToArray());
    private static double MaxLength2((double X, double Y)[] h) => MaxLength(h.Select(p => (p.X, p.Y, 0.0)).ToArray());

    // 凸包の長いほうの長さ (頂点どうしのいちばん遠い距離)
    private static double MaxLength((double X, double Y, double Z)[] h)
    {
        double best = 0;
        for (int i = 0; i < h.Length; i++) for (int j = i + 1; j < h.Length; j++) best = Math.Max(best, Math.Sqrt(Sq(h[i].X - h[j].X) + Sq(h[i].Y - h[j].Y)));
        return best;
    }

    // 凸包の短いほうの幅 (辺ごとに、その辺の直線から最も遠い点までの距離を取り、その最小)
    private static double MinWidth((double X, double Y, double Z)[] h)
    {
        double best = double.MaxValue;
        for (int i = 0; i < h.Length; i++)
        {
            var a = h[i]; var b = h[(i + 1) % h.Length];
            double ex = b.X - a.X, ey = b.Y - a.Y, len = Math.Sqrt(ex * ex + ey * ey);
            if (len < 1e-9) continue;
            double far = 0;
            foreach (var p in h) far = Math.Max(far, Math.Abs((p.X - a.X) * ey - (p.Y - a.Y) * ex) / len);
            best = Math.Min(best, far);
        }
        return best == double.MaxValue ? 0 : best;
    }

    private static List<StationPlate> KeepTouching(List<StationPlate> plates, HashSet<StationPlate> cand, Dictionary<string, StationMeshKind> result)
    {
        const double cell = 8;
        var seeds = plates.Where(p => !cand.Contains(p)).ToList();
        var excluded = new HashSet<StationPlate>(ReferenceEqualityComparer.Instance);
        // 段か: 階段の板か、床の板で短いほうの幅か長いほうの長さが FloorSideMin 未満のもの (面積でなく縦横の長さで見る。利用者の指示)
        bool IsStep(StationPlate p) => p.Stair || MinWidth2(p.Hull) < FloorSideMin || MaxLength2(p.Hull) < FloorSideMin;
        PlateGrid Grid(IEnumerable<StationPlate> ps) => new PlateGrid(ps, cell);
        IEnumerable<StationPlate> Near(PlateGrid g, StationPlate a) => g.Near(a);
        var ok = new HashSet<StationPlate>(ReferenceEqualityComparer.Instance);
        var seedGrid = Grid(seeds);
        for (int pass = 0; pass < 4; pass++)
        {
            // 2. たどる
            var cg = Grid(cand.Where(p => !excluded.Contains(p)));
            ok = new HashSet<StationPlate>(ReferenceEqualityComparer.Instance);
            var queue = new Queue<StationPlate>(seeds);
            while (queue.Count > 0)
            {
                var a = queue.Dequeue();
                foreach (var b in Near(cg, a))
                    if (!ok.Contains(b) && SurfacesTouch(a, b)) { ok.Add(b); queue.Enqueue(b); }
            }
            // 3. 段のまとまりごとに、2 つの高さの面のあいだをつなぐかを見る
            var steps = ok.Where(IsStep).ToList();
            var wide = ok.Where(p => !IsStep(p)).ToList();
            var sg = Grid(steps); var wg = Grid(wide);
            var comp = new Dictionary<StationPlate, int>(ReferenceEqualityComparer.Instance);
            int nc = 0, newly = 0;
            foreach (var s0 in steps)
            {
                if (comp.ContainsKey(s0)) continue;
                var members = new List<StationPlate> { s0 }; comp[s0] = nc;
                for (int i = 0; i < members.Count; i++)
                    foreach (var b in Near(sg, members[i]))
                        if (!comp.ContainsKey(b) && SurfacesTouch(members[i], b)) { comp[b] = nc; members.Add(b); }
                // 段の並びの下の端と上の端の両方が床 (登録済みの板・広い板) に接するときだけ階段 (親の決定「2 つの高さの床をつなぐ段の並び」)。
                // 下の端 = まとまりのいちばん低い面、上の端 = いちばん高い面。そこから SurfaceRise 未満の高さの床が接していること。
                // 木箱・棚・コンテナを積んだもの (QV の Cargo Deck A: 212.52〜220.48 m の 34 枚) は、上の端に床が接しない
                double cLo = members.Min(m => m.Bottom), cHi = members.Max(m => m.Top);
                bool atLo = false, atHi = false;
                foreach (var m in members)
                    foreach (var an in Near(seedGrid, m).Concat(Near(wg, m)))
                        if (SurfacesTouch(m, an))
                        {
                            if (Math.Abs(an.Top - cLo) < SurfaceRise) atLo = true;
                            if (Math.Abs(an.Top - cHi) < SurfaceRise) atHi = true;
                        }
                if (!(atLo && atHi && cHi - cLo >= StepRiseMin)) { foreach (var m in members) if (excluded.Add(m)) newly++; }
                nc++;
            }
            if (newly == 0) break;
        }
        ok.ExceptWith(excluded);
        // 段は階段の板に、広い板は床の板にする。名前の種類は、どこかで段になれば階段、そうでなく広い板になれば床
        var conv = new Dictionary<StationPlate, StationPlate>(ReferenceEqualityComparer.Instance);
        foreach (var p in ok)
        {
            var key = MeshKey(p.Mesh);
            if (IsStep(p))
            {
                conv[p] = p.Stair ? p : p with { Stair = true };
                result[key] = StationMeshKind.Stair;
            }
            else
            {
                conv[p] = p;
                if (!result.ContainsKey(key)) result[key] = StationMeshKind.Floor;
            }
        }
        LastShapeOnlyKept = ok.Count;
        LastShapeOnlyDropped = cand.Count - ok.Count;
        return plates.Where(p => !cand.Contains(p) || ok.Contains(p)).Select(p => conv.TryGetValue(p, out var q) ? q : p).ToList();
    }

    // 板の升目 (近くの板を探すための下調べ。2026-11-02 に KeepTouching・FloatingItems から切り出した)。
    // 升目の数が LargeCells を超える大きな板 (Rockcracker_002 の木にある Keeger の小惑星 angular_smooth_01_03400_01_lp は 1 枚 5 km 四方で、8 m の升目では 1 枚 40 万升。
    // 小惑星が並ぶと取り込みのメモリが 8 GB を超えた) は升目に入れず、別の並び (_large) に置き、どの板の近くの候補にも入れる。
    // 大きな板の近くの候補は、全部の板。近くの候補は SurfacesTouch で確かめる前の下調べなので、候補が増えても結果 (つながる板) は前と同じ
    private sealed class PlateGrid
    {
        public const long LargeCells = 4096;
        private readonly double _cell;
        private readonly Dictionary<(int, int), List<StationPlate>> _g = new();
        private readonly List<StationPlate> _large = new();
        private readonly List<StationPlate> _all = new();
        public PlateGrid(IEnumerable<StationPlate> ps, double cell)
        {
            _cell = cell;
            foreach (var p in ps)
            {
                _all.Add(p);
                if (CellCount(p, cell) > LargeCells) { _large.Add(p); continue; }
                foreach (var c in BoxCells(p, cell)) { if (!_g.TryGetValue(c, out var l)) _g[c] = l = new(); l.Add(p); }
            }
        }
        public IEnumerable<StationPlate> Near(StationPlate a)
        {
            if (CellCount(a, _cell) > LargeCells)
            {
                foreach (var b in _all) if (!ReferenceEquals(a, b)) yield return b;
                yield break;
            }
            var seen = new HashSet<StationPlate>(ReferenceEqualityComparer.Instance);
            foreach (var c in BoxCells(a, _cell)) if (_g.TryGetValue(c, out var l)) foreach (var b in l) if (!ReferenceEquals(a, b) && seen.Add(b)) yield return b;
            foreach (var b in _large) if (!ReferenceEquals(a, b) && seen.Add(b)) yield return b;
        }
    }

    private static long CellCount(StationPlate p, double cell)
    {
        long i0 = (long)Math.Floor((p.Hull.Min(q => q.X) - SurfaceGap) / cell), i1 = (long)Math.Floor((p.Hull.Max(q => q.X) + SurfaceGap) / cell);
        long j0 = (long)Math.Floor((p.Hull.Min(q => q.Y) - SurfaceGap) / cell), j1 = (long)Math.Floor((p.Hull.Max(q => q.Y) + SurfaceGap) / cell);
        return (i1 - i0 + 1) * (j1 - j0 + 1);
    }

    private static IEnumerable<(int, int)> BoxCells(StationPlate p, double cell)
    {
        int i0 = (int)Math.Floor((p.Hull.Min(q => q.X) - SurfaceGap) / cell), i1 = (int)Math.Floor((p.Hull.Max(q => q.X) + SurfaceGap) / cell);
        int j0 = (int)Math.Floor((p.Hull.Min(q => q.Y) - SurfaceGap) / cell), j1 = (int)Math.Floor((p.Hull.Max(q => q.Y) + SurfaceGap) / cell);
        for (int i = i0; i <= i1; i++) for (int j = j0; j <= j1; j++) yield return (i, j);
    }

    // ===== 浮いたもの (2026-11-02、利用者の指示「浮いたものリストを取り込み時にログ出力と DB 格納しとくようにすると将来のデバッグに使える」) =====
    //   ① 上向きの面なし: 床・階段の種類 (表・名前) なのに、歩ける上向きの面が無く板にならなかった部品 (Plates)
    //   ② 接する面なし: どの板とも接しない (SurfacesTouch) 床・階段・坂の板
    //   ③ 階段の端に床なし: 端の階が見つからず、階段にならなかった階段・坂のまとまり (StationRouteGraph.Stairs)
    //   ④ 乗り場からたどれない: 乗り場からたどれない階 (StationFloor.Reachable)
    public const string NoUpFaceReason = "上向きの面なし", NoTouchReason = "接する面なし", StairNoEndReason = "階段の端に床なし", UnreachableReason = "乗り場からたどれない",
        ShapeOnlyFloorReason = "形だけの階で道なし";

    // (2026-11-02、親の指示) 形でだけ床・階段になった部品 (表の Source が shape、または表に無い) の板だけでできていて、道順の節点 (経由点を含む) が 1 つも無い階は、
    // 階の一覧から外し (Listed・Reachable を false)、浮いたもの (⑤ 形だけの階で道なし) にする。Checkmate の砂の山 sand_pile_single_straight_fs が、どこにもつながらない階
    // (−476.76 m・−109.01 m) になっていた。外した階を返す (graphFloors は道順の節点のある階の Id)
    public static List<StationFloating> DropShapeOnlyFloors(StationMeshSet set, IReadOnlyList<StationFloor> floors, ISet<string> graphFloors)
    {
        var res = new List<StationFloating>();
        foreach (var f in floors)
        {
            if (f.PlateSet == null || f.PlateSet.Count == 0 || graphFloors.Contains(f.Id)) continue;
            if (set.Kinds == null || !f.PlateSet.All(p => KindOf(set.Kinds, p.Mesh).Source == "shape")) continue;
            f.Listed = false; f.Reachable = false;
            var big = f.PlateSet.MaxBy(p => HullArea(p.Hull))!;
            res.Add(new StationFloating(big.Mesh, "floor:" + f.Id, big.Hull.Average(q => q.X), big.Hull.Average(q => q.Y), f.Z, ShapeOnlyFloorReason));
        }
        return res;
    }

    public static string KindLabel(StationMeshKinds.Entry e) => (e.Kind switch { StationMeshKind.Floor => "floor", StationMeshKind.Stair => "stair", StationMeshKind.Composite => "composite", _ => "none" }) + ":" + e.Source;

    public static List<StationFloating> FloatingItems(StationMeshSet set, IReadOnlyList<StationPlate> plates, IReadOnlyList<StationFloor> floors, IEnumerable<StationFloating> stairNoEnd)
    {
        var res = new List<StationFloating>(set.NoUpFace);
        // ② 接する面なし
        const double cell = 8;
        var grid = new PlateGrid(plates, cell);
        foreach (var p in plates)
        {
            if (grid.Near(p).Any(q => SurfacesTouch(p, q))) continue;
            var k = set.Kinds != null ? KindOf(set.Kinds, p.Mesh) : new StationMeshKinds.Entry(NameKind(p.Mesh), "name");
            res.Add(new StationFloating(p.Mesh, KindLabel(k), p.Hull.Average(q => q.X), p.Hull.Average(q => q.Y), p.Top, NoTouchReason));
        }
        // ③ 階段の端に床なし
        res.AddRange(stairNoEnd);
        // ④ 乗り場からたどれない階 (部品の名前は、階の板のうち面積のいちばん大きいもの)
        foreach (var f in floors.Where(f => !f.Reachable))
        {
            var big = f.PlateSet?.Where(p => !p.Stair).MaxBy(p => HullArea(p.Hull));
            var (x, y) = big != null ? (big.Hull.Average(q => q.X), big.Hull.Average(q => q.Y)) : f.Bounds is { } b ? ((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2) : (double.NaN, double.NaN);
            res.Add(new StationFloating(big?.Mesh ?? "", "floor:" + f.Id, x, y, f.Z, UnreachableReason));
        }
        return res;
    }

    // 形の数から板を作る (箱の上面ではなく、実際の上向きの面の高さ)。
    //   床・通路: 主な水平な面 (MainZ) の高さで、その段 ± MainBand の水平な面の凸包。主な面より坂の面が広い (傾けて置いた床) ときは坂の板にする。
    //   階段・坂: 歩ける面の凸包。下の端は StairLo (段の並びが無ければ、面積 StepMinArea 以上の段のいちばん低いもの、それも無ければ歩ける面のいちばん低い所)、
    //   上の端は面積 StepMinArea 以上の段のいちばん高いもの (坂のほうが高ければ坂の上の端。どちらも無ければ歩ける面のいちばん高い所。手すりの上面を上の端にしないため)
    // 床と階段を 1 つに持つ部品 (IsComposite) は、形で分ける: 広い平らな面 (BigLevels) はそれぞれ床の板、残りの段・坂は階段 (坂) の板 1 つ。
    // 階段の板の下の端は、いちばん下の段 − 段の高さ (坂なら坂のいちばん低い所)、上の端はいちばん高い段 (坂の上の端)。
    // 端から SurfaceRise 以内に広い平らな面があれば、端をその高さにする (床と階段がつながるように)
    private static List<StationPlate> SurfacePlates(StationInstance inst, string mesh, StationShape sh, StationMeshKind kind)
    {
        var res = new List<StationPlate>();
        if (kind != StationMeshKind.None && sh.IsComposite)
        {
            foreach (var b in sh.BigLevels)
            {
                if (b.Hull.Length < 3) continue;
                var bp = b.Hull.Select(c => inst.ToStation.Apply(c)).ToList();
                var bh = ConvexHull(bp.Select(p => (p.X, p.Y)).ToList());
                if (bh.Length < 3) continue;
                var bz = bp.Average(p => p.Z);
                res.Add(new StationPlate(inst.Index, mesh, false, bh, bz, bz, Depth: PlateDepth(sh, b.Z, b.Z)));
            }
            var rem = sh.Levels.Where(l => l.Area >= StepMinArea && !sh.BigLevels.Any(b => Math.Abs(b.Z - l.Z) <= LevelJoin)).OrderBy(l => l.Z).ToList();
            double lo, hi;
            if (rem.Count > 0)
            {
                var dz = Enumerable.Range(1, rem.Count - 1).Select(i => rem[i].Z - rem[i - 1].Z).Where(d => d >= StepRiseMin && d <= StepRiseMax).OrderBy(d => d).ToList();
                var step = dz.Count > 0 ? dz[dz.Count / 2] : 0.25;
                lo = rem[0].Z - step; hi = rem[^1].Z;
                if (sh.SlopeArea > 0) { lo = Math.Min(lo, sh.SlopeLo); hi = Math.Max(hi, sh.SlopeHi); }
            }
            else { lo = sh.SlopeLo; hi = sh.SlopeHi; }
            foreach (var b in sh.BigLevels)
            {
                if (Math.Abs(b.Z - lo) < SurfaceRise) lo = b.Z;
                if (b.Z > hi && b.Z - hi < SurfaceRise) hi = b.Z;
            }
            var sp = sh.StepHull.Select(c => inst.ToStation.Apply(c)).ToList();
            var shl = ConvexHull(sp.Select(p => (p.X, p.Y)).ToList());
            if (shl.Length >= 3 && hi > lo)
            {
                double cx = sh.StepHull.Average(c => c.X), cy = sh.StepHull.Average(c => c.Y);
                double top = inst.ToStation.Apply((cx, cy, hi)).Z, bottom = inst.ToStation.Apply((cx, cy, lo)).Z;
                res.Add(new StationPlate(inst.Index, mesh, true, shl, Math.Max(top, bottom), Math.Min(top, bottom), sh.GuessRamp,
                    Depth: rem.Count > 0 ? rem.Select(l => PlateDepth(sh, l.Z, l.Z)).Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max() : double.NaN));
            }
            return res;
        }
        if (SurfacePlate(inst, mesh, sh, kind == StationMeshKind.Composite ? (sh.Guess == StationMeshKind.Stair ? StationMeshKind.Stair : StationMeshKind.Floor) : kind) is { } one) res.Add(one);
        return res;
    }

    private static StationPlate? SurfacePlate(StationInstance inst, string mesh, StationShape sh, StationMeshKind kind)
    {
        if (kind == StationMeshKind.Floor && sh.MainArea > 0 && sh.MainArea >= sh.SlopeArea && sh.MainHull.Length >= 3)
        {
            var mp = sh.MainHull.Select(c => inst.ToStation.Apply(c)).ToList();
            var mh = ConvexHull(mp.Select(p => (p.X, p.Y)).ToList());
            if (mh.Length < 3) return null;
            var z = mp.Average(p => p.Z);
            return new StationPlate(inst.Index, mesh, false, mh, z, z, Depth: PlateDepth(sh, sh.MainZ, sh.MainZ));
        }
        if (sh.Hull.Length < 3 || sh.FlatArea + sh.SlopeArea <= 0) return null;
        var big = sh.Levels.Where(l => l.Area >= StepMinArea).ToList();
        double lo = !double.IsNaN(sh.StairLo) ? sh.StairLo : big.Count > 0 ? big.Min(l => l.Z) : sh.WalkLo;
        double hi = big.Count > 0 ? big.Max(l => l.Z) : sh.WalkHi;
        if (sh.SlopeArea > 0 && sh.SlopeHi > hi) hi = sh.SlopeHi;
        if (hi < lo) (lo, hi) = (hi, lo);
        var wp = sh.Hull.Select(c => inst.ToStation.Apply(c)).ToList();
        var wh = ConvexHull(wp.Select(p => (p.X, p.Y)).ToList());
        if (wh.Length < 3) return null;
        double cx = sh.Hull.Average(c => c.X), cy = sh.Hull.Average(c => c.Y);
        double top = inst.ToStation.Apply((cx, cy, hi)).Z, bottom = inst.ToStation.Apply((cx, cy, lo)).Z;
        var ramp = sh.GuessRamp || kind == StationMeshKind.Floor;
        return new StationPlate(inst.Index, mesh, true, wh, Math.Max(top, bottom), Math.Min(top, bottom), ramp, Depth: PlateDepth(sh, lo, hi));
    }

    // 床のまとまり: 板 (床・階段・スロープ) どうしを、xy の箱の間が GroupGap (3 m) 以内で、上面の高さの差が GroupMaxRise (60 m) 以内なら同じまとまりにする (つながりで広げる)。
    // 階はまとまりごとに作る (Levels)。Orison は高さの違う浮かぶプラットフォームがいくつもあり、都市全体の床の高さを 1 本に並べると階が数百になった
    // (利用者が取り込んだ Stanton2_Orison.json で 338 階)。Checkmate の Clinic の病棟 (16 m おき) は入口と xy が重なるので同じまとまりになる
    public const double GroupGap = 3.0;
    public const double BalconyGatewayRadius = 5.0;
    public const double GroupMaxRise = 60.0;
    public const double PlatformLinkGap = 10.0;
    public const double PlatformLinkRise = 3.0;

    // 凸多角形の上で点にいちばん近い点 (中なら点のまま)
    public static (double X, double Y) ClosestOnConvex((double X, double Y)[] h, double x, double y)
    {
        if (InConvex(h, x, y)) return (x, y);
        (double X, double Y) best = h[0]; double md = double.MaxValue;
        for (int i = 0; i < h.Length; i++)
        {
            var a = h[i]; var b = h[(i + 1) % h.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double tt = l2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1) : 0;
            var p = (a.X + tt * dx, a.Y + tt * dy);
            var d = (p.Item1 - x) * (p.Item1 - x) + (p.Item2 - y) * (p.Item2 - y);
            if (d < md) { md = d; best = p; }
        }
        return best;
    }

    public static List<StationPlate> AssignGroups(List<StationPlate> plates)
    {
        var box = plates.Select(p => (MinX: p.Hull.Min(q => q.X), MaxX: p.Hull.Max(q => q.X), MinY: p.Hull.Min(q => q.Y), MaxY: p.Hull.Max(q => q.Y))).ToList();
        var parent = Enumerable.Range(0, plates.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var order = Enumerable.Range(0, plates.Count).OrderBy(i => box[i].MinX).ToList();
        for (int oi = 0; oi < order.Count; oi++)
            for (int oj = oi + 1; oj < order.Count && box[order[oj]].MinX <= box[order[oi]].MaxX + GroupGap; oj++)
            {
                int i = order[oi], j = order[oj];
                if (box[i].MinY - GroupGap <= box[j].MaxY && box[j].MinY - GroupGap <= box[i].MaxY
                    && Math.Abs(plates[i].Top - plates[j].Top) <= GroupMaxRise) parent[Find(i)] = Find(j);
            }
        // 乗り場の縁 (IsBalcony) は、高さの差 PlatformLinkRise (3 m) 以内でいちばん近い乗り場の縁でない床の板 (PlatformLinkGap 10 m 以内) と同じまとまりにする
        // (Orison の Cloudview Center の乗り場の縁 (-3.42 m) は、島の床 floor_custom_plat000_front_a・plat001_bridges_a (-1.41 m) から 4.9〜8 m 離れていて、
        // 別のまとまり「Cloudview Center 付近」になり、2 つの乗り場が島から歩いてつながらなかった。あいだの段・床はメッシュの名前の規則で拾えていない)
        for (int i = 0; i < plates.Count; i++)
        {
            if (!IsBalcony(plates[i].Mesh)) continue;
            int bj = -1; double bd = PlatformLinkGap;
            for (int j = 0; j < plates.Count; j++)
            {
                if (j == i || IsBalcony(plates[j].Mesh) || plates[j].Stair || Math.Abs(plates[j].Top - plates[i].Top) > PlatformLinkRise) continue;
                var d = HullGap(plates[i].Hull, plates[j].Hull);
                if (d < bd) { bd = d; bj = j; }
            }
            if (bj >= 0) parent[Find(i)] = Find(bj);
        }
        // まとまりの番号は、板の面積の大きい順
        var ids = new Dictionary<int, int>();
        int n = 0;
        foreach (var g in Enumerable.Range(0, plates.Count).GroupBy(Find).OrderByDescending(g => g.Sum(i => HullArea(plates[i].Hull))).ThenBy(g => g.Min()))
        {
            foreach (var i in g) ids[i] = n;
            n++;
        }
        return plates.Select((p, i) => p with { Group = ids[i] }).ToList();
    }

    // 階 (計画 4 の 3 に、利用者の決定 (2026-11-01 の差し戻しの答え) を足したもの):
    //   ① 床の板の上面 z を並べ、FloorGap (2.5 m) を超える差で区切る。
    //   ② 厚さ (zMax − zMin) が MaxFloorSpan (4 m) を超える階は、その中のいちばん大きい差で 2 つに割る (割れなくなるまで繰り返す)。
    //      Checkmate の入口〜病棟 1・2 階は、床の板が 1 m おきに続くので ① だけでは z 122.6〜147.75 が 1 つの階になった。
    // 偽の床 (天井の格子など) は、先に DropOverheadPlates で板から除いておく。
    // extraHeights は板の無い区画の乗り場から足す高さ (階の候補)
    public const double MaxFloorSpan = 4.0;
    public const double FakeFloorArea = 100;
    public const double FakeFloorOverlap = 0.9;
    public const double FakeFloorMinRise = 2.5;
    public const double MinFloorArea = 10;

    // 階はまとまり (StationPlate.Group) ごとに作る。Id は f<番号> (まとまりの番号順、まとまりの中は下から)。extraHeights の階はまとまりに入らない (Group "")
    // 階の分け方 (利用者の決定 2026-11-01): 真上から見て床どうしが上下に重なるときだけ階を分ける。重ならない所は、長い階段・スロープ・小さな段差でつながっていても
    // 同じ階にする。手順:
    //   ① まとまりごとに、前と同じ高さの帯 (LevelGroups: FloorGap・MaxFloorSpan) を作る。同じ帯の板どうしは xy で GroupGap (3 m) 以内なら結ぶ。
    //   ② 別の帯の板どうしは、接していて (JoinGap 1 m 以内) 上面の差が SmallStepRise (1.625 m) 以下なら結ぶ (階段のメッシュの無い段差)。
    //      階段・スロープの板のまとまり (StairClusters) は、接する床の板 (JoinGap 以内、高さはまとまりの範囲 ± JoinRise) を高さの順に結ぶ。
    //   ③ 結びを上面の差の小さい順に見て、2 つの組を合わせても上下の重なり (Overlaps) が無ければ合わせる (Kruskal の順)。
    //      上下の重なり: LayerCell (2 m) の升目で、両方の床の板がかかり、高さの差が LayerGap (2 m) 以上の升目が LayerMinCells (2) 個以上。
    //   ④ 最後に、同じまとまりの組どうしを、高さ (中央値) の差の小さい順に、上下に重ならなければ合わせる (つながっていなくても、重ならない所は同じ階)。
    // 合わせた階段・スロープの板は、その階の板 (PlateSet。地図の床・道の床) になり、階段の印 (StationRouteGraph.Stairs) は作らない
    public const double LayerGap = 2.5;
    public const double LayerCell = 2.0;
    public const int LayerMinCells = 2;
    public const double JoinGap = 1.0;
    public const double JoinRise = 0.6;
    public const double SmallStepRise = 1.625;
    public const double SameLevelRise = 1.0;
    // 利用者の規則 (2026-11-01): 数段の階段・同じ高さの床とみなす上面の差と、1 つの階層の高さの範囲の幅の上限。値はゲームの事実から決めた:
    //   SmallStairRise 1.3 m: 利用者が同じ階と確かめた Seraphim の 4 段の階段 (ガレリアの stairs_04x02x01_a、つなぐ床 -9.7 m と -8.7 m、差 1.0 m) と
    //     配送センターの 5 段 (段の灯り light_landing、つなぐ床 -9.72 m と -8.47 m、差 1.26 m) が、数段の階段に入る値
    //   LevelRangeMax 3.0 m: 利用者が 1 層と確かめた Seraphim のガレリアの下の層 (装備の店の層) の床の上面の範囲 -10.75〜-7.90 m (幅 2.85 m) を 1 つに収める値
    public const double SmallStairRise = 1.3;
    // (2026-11-02、利用者の規則「小さな段差は装飾。床の高さに範囲を加え、これでひとくくりにする。但し、50cm 以上の隣接する床を同一視しないように。
    //  間に床と見なした定義されたものがあることが条件。」と、その答え「直は 30cm 程度だろう？」「ひとつとみなすなら、1.6m 程度だし、一段だったら 30cm 程度」
    //  「(1 つの階の高さの幅は) Seraphim の5段以下の高さに合わせてわずかに幅を持たせる」「繋がっていなくても高低差がわずかなら一緒にしないと見にくい」)
    //   DirectStepRise 0.3 m: 間に階段の無い、直に接する床どうしを同じ階にする上面の差 (一段)。利用者の「一段だったら 30cm 程度」と、前からの利用者の想定
    //     「階段の一段の高さが30cm以内」(Seraphim の実測は一段 0.25 m)。Checkmate の Admin の脇の床の板 (137.40・138.40・139.15 m、差 1.0 m と 0.75 m) はまとめない
    //   StairJoinRise・LevelRangeMax 1.625 m (SmallStepRise と同じ値): 複数の段のある階段を 1 つとみなして上下の床を結ぶ高さの差の上限と、1 つの階の高さの幅の上限。
    //     利用者の「ひとつとみなすなら、1.6m 程度」「Seraphim の5段以下の高さに合わせてわずかに幅を持たせる」。一段 0.30 m (利用者の想定の上限) の 5 段 1.5 m に、
    //     一段の半分ほど (0.125 m) を足した値で、一段 0.25 m (Seraphim の実測) なら 6.5 段。前の LevelRangeMax 3.0 m (Seraphim のガレリアの下の層 -10.75〜-7.90 m を
    //     1 つに収める値) から変えた
    public const double DirectStepRise = 0.3;
    public const double StairJoinRise = SmallStepRise;
    public const double LevelRangeMax = SmallStepRise;
    // 明らかに階が違う階段の境目 (2026-11-02、親の決定): 階段のまとまりの高さ (板の Bottom〜Top) と、両端に接する床の上面の差の、両方がこの値以上のときだけ、
    // 両端の床を別の層にする (Sheets の Ends)。数段の階段の判定 (SmallStairRise 1.3 m) とは分けた。
    // 値の根拠: 4 段の部品 stairs_04x02x01_a は、利用者が数段と見ている Seraphim のガレリアの 4 段 (「ガレリアの踊り場以外の 4・5 段の階段」) と同じ部品で、
    // メッシュの箱の高さは 1.302 m (Lorville のメッシュのキャッシュ mesh_Stanton1_Lorville_0_v10.json の角の z −2.984〜−1.682、
    // Seraphim の mesh_RR_CRU_LEO_0.json の板 −15.98〜−14.68)。Lorville の Metro Center では、この部品が床 270.77 m と 272.07 m (差 1.30 m) をつなぎ、
    // 1.3 m 以上で「明らかに階が違う」に入って、トラムの乗り場の床が別の層に分かれていた。4 段の部品を数段に入れるため、1.302 m の少し上の 1.35 m にした
    public const double BigStairRise = 1.35;
    // 直前の Levels で、数段の階段・明らかに階が違う階段に分けた階段のまとまりと、つなぐ床の上面の差 (確かめ用。接する床が 2 つ未満なら NaN)
    public static readonly List<(List<StationPlate> Cluster, double Rise, List<double> Touch)> LastSmallStairs = new(), LastBigStairs = new();

    public static List<StationFloor> Levels(IEnumerable<StationPlate> plates, IEnumerable<double>? extraHeights = null)
    {
        var res = new List<StationFloor>();
        var all = plates.ToList();
        LastMergedPairs = 0; LastRefusedPairs = 0; LastClashRefused = 0; LastBigStairRefused = 0; LastSmallStairs.Clear(); LastBigStairs.Clear();
        foreach (var grp in all.GroupBy(p => p.Group).OrderBy(g => g.Key))
        {
            var gp = grp.Where(p => !p.Stair).ToList();
            if (gp.Count == 0) continue;
            var gs = grp.Where(p => p.Stair).ToList();
            (double, double, double, double) bounds = (gp.Min(p => p.Hull.Min(q => q.X)), gp.Min(p => p.Hull.Min(q => q.Y)), gp.Max(p => p.Hull.Max(q => q.X)), gp.Max(p => p.Hull.Max(q => q.Y)));
            var sheets = Sheets(gp, gs);
            int lv = 0;
            foreach (var sh in sheets.OrderBy(x => x.Z))
            {
                var tops = sh.Floor.Select(p => p.Top).OrderBy(z => z).ToList();
                var coarse = new HashSet<(int, int)>();
                foreach (var p in sh.Floor.Concat(sh.Stairs))
                    for (int i = (int)Math.Floor(p.Hull.Min(q => q.X) / StationFloor.CoarseCell); i <= (int)Math.Floor(p.Hull.Max(q => q.X) / StationFloor.CoarseCell); i++)
                        for (int j = (int)Math.Floor(p.Hull.Min(q => q.Y) / StationFloor.CoarseCell); j <= (int)Math.Floor(p.Hull.Max(q => q.Y) / StationFloor.CoarseCell); j++)
                            coarse.Add((i, j));
                var set = new HashSet<StationPlate>(sh.Floor.Concat(sh.Stairs), ReferenceEqualityComparer.Instance);
                res.Add(new StationFloor
                {
                    Id = $"f{res.Count:D2}", Z = Math.Round(tops[tops.Count / 2], 3), ZMin = Math.Round(tops[0], 3), ZMax = Math.Round(tops[^1], 3),
                    Group = grp.Key >= 0 ? $"g{grp.Key}" : "", Bounds = grp.Key >= 0 ? bounds : null, Coarse = grp.Key >= 0 ? coarse : null, Level = ++lv,
                    CityOnly = sh.Floor.All(p => IsCityPlateMesh(p.Mesh)), PlateSet = set, CellZ = StairCells(sh),
                });
            }
        }
        if (extraHeights != null && extraHeights.Any())
            foreach (var g in LevelGroups(new List<StationPlate>(), extraHeights))
            {
                var sorted = g.Select(t => t.Z).OrderBy(z => z).ToList();
                res.Add(new StationFloor { Id = $"f{res.Count:D2}", Z = Math.Round(sorted[sorted.Count / 2], 3), ZMin = Math.Round(sorted[0], 3), ZMax = Math.Round(sorted[^1], 3), FromTransit = true });
            }
        return res;
    }

    // 直前の Levels で、合わせた組の数・上下の重なりで合わせなかった結びの数 (確かめ用)
    public static int LastMergedPairs, LastRefusedPairs, LastClashRefused;
    // 直前の Plates で、塊の床の高さを乗り場・階段の端で決めた数 (確かめ用)
    public static int LastBlockByGateway, LastBlockByStair, LastBlockSlope;

    private sealed class Sheet
    {
        public List<StationPlate> Floor = new();
        public List<StationPlate> Stairs = new();
        public Dictionary<(int, int), (double Min, double Max)> Cells = new();
        public double Z => Floor.Select(p => p.Top).OrderBy(z => z).ElementAt(Floor.Count / 2);
        public double Lo = double.MaxValue, Hi = double.MinValue;
        // 明らかに階が違う階段の端の印 (階段の番号 × 2 + 0 下の端・1 上の端)。同じ番号の下の端と上の端を同じ組に入れない
        public HashSet<int> Ends = new();
    }

    private static List<(int I, int J)> CellsOf(StationPlate p)
    {
        var res = new List<(int, int)>();
        int i0 = (int)Math.Floor(p.Hull.Min(q => q.X) / LayerCell), i1 = (int)Math.Floor(p.Hull.Max(q => q.X) / LayerCell);
        int j0 = (int)Math.Floor(p.Hull.Min(q => q.Y) / LayerCell), j1 = (int)Math.Floor(p.Hull.Max(q => q.Y) / LayerCell);
        for (int i = i0; i <= i1; i++)
            for (int j = j0; j <= j1; j++)
                if (InConvex(p.Hull, (i + 0.5) * LayerCell, (j + 0.5) * LayerCell)) res.Add((i, j));
        if (res.Count == 0) res.Add(((int)Math.Floor(p.Hull.Average(q => q.X) / LayerCell), (int)Math.Floor(p.Hull.Average(q => q.Y) / LayerCell)));
        return res;
    }

    // 階の升目の高さ (床の板の升目に、階の中の道にした階段・スロープの板の升目 (高さは下の端〜上の端) を足したもの)
    private static Dictionary<(int, int), (double Min, double Max)> StairCells(Sheet sh)
    {
        var d = new Dictionary<(int, int), (double Min, double Max)>(sh.Cells);
        foreach (var p in sh.Stairs)
            foreach (var c in CellsOf(p))
                d[c] = d.TryGetValue(c, out var o) ? (Math.Min(o.Min, p.Bottom), Math.Max(o.Max, p.Top)) : (p.Bottom, p.Top);
        return d;
    }

    // 升目の高さ (CellZ) で点の階を選ぶ: 点の升目と隣の 8 升目に床があり、床の高さの範囲が [z − maxAbove, z + tolBelow] にかかる階のうち、
    // 点の高さとの差 (範囲の中なら 0、下なら z − 上端、上なら下端 − z) のいちばん小さいもの。どの階にも CellZ が無ければ null を返さず、前のやり方に任せる
    private static bool CellPick(IReadOnlyList<StationFloor> floors, double x, double y, double z, double tolBelow, double maxAbove, out StationFloor? best)
    {
        best = null;
        if (!floors.Any(f => f.CellZ != null)) return false;
        int ci = (int)Math.Floor(x / LayerCell), cj = (int)Math.Floor(y / LayerCell);
        double bd = double.MaxValue;
        foreach (var f in floors)
        {
            if (f.CellZ == null) continue;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int di = -1; di <= 1; di++) for (int dj = -1; dj <= 1; dj++)
                    if (f.CellZ.TryGetValue((ci + di, cj + dj), out var r)) { lo = Math.Min(lo, r.Min); hi = Math.Max(hi, r.Max); }
            if (lo > hi || lo > z + tolBelow || z - hi > maxAbove) continue;
            var d = z > hi ? z - hi : z < lo ? lo - z : 0;
            if (d < bd) { bd = d; best = f; }
        }
        return true;
    }

    private static bool Overlaps(Sheet a, Sheet b)
    {
        var (s, l) = a.Cells.Count <= b.Cells.Count ? (a, b) : (b, a);
        int n = 0;
        foreach (var (c, za) in s.Cells)
            if (l.Cells.TryGetValue(c, out var zb) && (za.Min - zb.Max >= LayerGap || zb.Min - za.Max >= LayerGap) && ++n >= LayerMinCells) return true;
        return false;
    }

    // a が b の上に重なれば +1、下に重なれば −1、重ならなければ 0 (上下の重なりの升目が LayerMinCells 個以上で、全部が同じ向きのとき)
    private static int OverlapDir(Sheet a, Sheet b)
    {
        var (s, l, sign) = a.Cells.Count <= b.Cells.Count ? (a, b, 1) : (b, a, -1);
        int up = 0, down = 0;
        foreach (var (c, zs) in s.Cells)
            if (l.Cells.TryGetValue(c, out var zl))
            {
                if (zs.Min - zl.Max >= LayerGap) up++;
                else if (zl.Min - zs.Max >= LayerGap) down++;
            }
        // 上と下の両方がある (高さの違う床をまとめた組どうし) ときは、どちらとも決めない (0)
        if (up < LayerMinCells && down < LayerMinCells || up > 0 && down > 0) return 0;
        return (up > 0 ? 1 : -1) * sign;
    }

    // 2 つの組を合わせると、明らかに階が違う階段の下の端と上の端が同じ組に入るか
    private static bool EndsClash(Sheet a, Sheet b) => a.Ends.Count > 0 && b.Ends.Count > 0 && a.Ends.Any(e => b.Ends.Contains(e ^ 1));
    // 直前の Levels で、明らかに階が違う階段の両端が同じ層に入るため合わせなかった数 (確かめ用)
    public static int LastBigStairRefused;

    private static void Absorb(Sheet into, Sheet from)
    {
        into.Floor.AddRange(from.Floor); into.Stairs.AddRange(from.Stairs);
        into.Lo = Math.Min(into.Lo, from.Lo); into.Hi = Math.Max(into.Hi, from.Hi);
        into.Ends.UnionWith(from.Ends);
        foreach (var (c, z) in from.Cells)
            into.Cells[c] = into.Cells.TryGetValue(c, out var o) ? (Math.Min(o.Min, z.Min), Math.Max(o.Max, z.Max)) : z;
    }

    // 階段・スロープの板のまとまり (インスタンスごとに、xy の箱が JoinGap 以内で重なり、高さの範囲が JoinRise 以内で続くもの)
    public static List<List<StationPlate>> StairClusters(IReadOnlyList<StationPlate> stairs)
    {
        var parent = Enumerable.Range(0, stairs.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var box = stairs.Select(p => (MinX: p.Hull.Min(q => q.X), MaxX: p.Hull.Max(q => q.X), MinY: p.Hull.Min(q => q.Y), MaxY: p.Hull.Max(q => q.Y))).ToList();
        foreach (var g in Enumerable.Range(0, stairs.Count).GroupBy(i => StairKey(stairs[i])))
        {
            var l = g.ToList();
            for (int a = 0; a < l.Count; a++)
                for (int b = a + 1; b < l.Count; b++)
                {
                    int i = l[a], j = l[b];
                    if (box[i].MinX - JoinGap <= box[j].MaxX && box[j].MinX - JoinGap <= box[i].MaxX && box[i].MinY - JoinGap <= box[j].MaxY && box[j].MinY - JoinGap <= box[i].MaxY
                        && stairs[i].Bottom <= stairs[j].Top + JoinRise && stairs[j].Bottom <= stairs[i].Top + JoinRise) parent[Find(i)] = Find(j);
                }
        }
        return Enumerable.Range(0, stairs.Count).GroupBy(Find).Select(g => g.Select(i => stairs[i]).ToList()).ToList();
    }

    // (2026-11-02、利用者の指示「偽でいいわけないよね」・親の決定 (a)) 階段の板どうしが、実際に続いている段か: 凸包のすき間が JoinGap (1 m) 以内で、
    // 高さの範囲が一段 (DirectStepRise 0.3 m) 以内で続くもの (前は xy の箱が JoinGap 以内で、高さが JoinRise 0.6 m 以内)。
    // 地図の階段の線 (StationRouteGraph.Stairs) のまとめ方に使う。床どうしをつなぐ数段の階段の見方 (StairClusters。Levels) は前のまま
    public static bool StairsContinue(StationPlate a, StationPlate b) =>
        a.Bottom <= b.Top + DirectStepRise && b.Bottom <= a.Top + DirectStepRise && HullGap(a.Hull, b.Hull) <= JoinGap;

    // 階段の板をまとめる鍵: 踊り場でつないだ階段 (Link) はその鍵、ほかはインスタンス (StairClusters・StationRouteGraph.Stairs)
    public static int StairKey(StationPlate p) => p.Link >= 0 ? -2 - p.Link : p.Instance;

    // 直前の Landings で踊り場にした板の数・踊り場でつないだ階段の数 (確かめ用)
    public static int LastLandingPlates, LastLandingLinks;
    public const double LandingSameRise = 0.3;
    public const double LandingThingAbove = 2.5;

    // (2026-11-02、利用者の決定「踊り場に相当する小さな床は階段とみなす」「見分け方は C」、Checkmate の 131.75 m の床は「B」(板ごと)。原文「踊り場には何もない。admin と clinic のフロアは違ったと思う」)
    // 踊り場の板をどの階にも入れず、階段の一部にする (Stair = true・Landing = true。Levels の前に呼ぶ)。見分け方:
    //   塊: 同じまとまり (Group) の床の板を、上面の差 LandingSameRise (0.3 m) 以内で接する (HullGap が JoinGap 1 m 以内) ものでまとめたもの。
    //   塊の条件: 階段のまとまり (StairClusters) で、塊より低い床 (上面 − JoinRise より下) と高い床 (上面 + JoinRise より上) の両方とつながる (中間の高さ・上下と階段でつながる)。
    //     階段以外の段差 (層の決まり ① の近い床: xy の箱が GroupGap 3 m 以内で、上面の差が LandingSameRise より大きく SmallStairRise 1.3 m 未満) で、塊の外の床の板とつながらない。
    //   板の条件 (板ごと): 階段の板から JoinGap (1 m) 以内。店・名札・乗り場・行き先 (板の凸包から 0.5 m 以内、上面 −0.6〜+2.5 m) が上に無い。
    //   残りの板 (Checkmate の ADMIN の部屋など) は床のまま。
    // 踊り場の板と、それに接する階段のまとまり (高さが範囲 ± JoinRise) は、同じ鍵 (Link) でまとめ、別のインスタンスの階段も 1 本の階段にする (StairKey)
    // (2026-11-02、親の決定) 踊り場の見分けで「階段の板に接する」は、xy (JoinGap 以内) に加えて、床の板の上面が、その階段の板の高さ (Bottom〜Top) から JoinRise 以内のときだけ。
    // QV Logistics Station (Nyx_Kaboos) の Cargo Deck A で、219.71 m の util_stairs_platform_04x04_a が、xy だけで 6 m 下の木箱の段 (形で段にした板) に接するとされ、踊り場になって階段が消えていた
    private static bool TouchesAt(StationPlate st, StationPlate floor) =>
        floor.Top >= st.Bottom - JoinRise && floor.Top <= st.Top + JoinRise && HullGap(st.Hull, floor.Hull) <= JoinGap;

    public static List<StationPlate> Landings(StationTree tree, List<StationPlate> plates)
    {
        LastLandingPlates = 0; LastLandingLinks = 0;
        var things = tree.Shops.Select(s => s.Pos).Concat(tree.Points.Where(p => p.Pos != null && p.Kind is "entitycomponentinteriormaplabel" or "sctransitgateway" or "sctransitdestination").Select(p => p.Pos!.Value)).ToList();
        var replace = new Dictionary<StationPlate, StationPlate>(ReferenceEqualityComparer.Instance);
        int links = 0;
        foreach (var grp in plates.GroupBy(p => p.Group))
        {
            var gp = grp.Where(p => !p.Stair).ToList(); var gs = grp.Where(p => p.Stair).ToList();
            if (gp.Count == 0 || gs.Count == 0) continue;
            var box = gp.Select(p => (MinX: p.Hull.Min(q => q.X), MaxX: p.Hull.Max(q => q.X), MinY: p.Hull.Min(q => q.Y), MaxY: p.Hull.Max(q => q.Y))).ToList();
            var parent = Enumerable.Range(0, gp.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var order = Enumerable.Range(0, gp.Count).OrderBy(i => box[i].MinX).ToList();
            for (int a = 0; a < order.Count; a++)
                for (int b = a + 1; b < order.Count && box[order[b]].MinX <= box[order[a]].MaxX + JoinGap; b++)
                {
                    int i = order[a], j = order[b];
                    if (box[i].MinY - JoinGap > box[j].MaxY || box[j].MinY - JoinGap > box[i].MaxY || Math.Abs(gp[i].Top - gp[j].Top) > LandingSameRise) continue;
                    if (HullGap(gp[i].Hull, gp[j].Hull) <= JoinGap) parent[Find(i)] = Find(j);
                }
            var cls = StairClusters(gs);
            var touchOf = new List<(double Lo, double Hi, List<int> T)>();
            foreach (var cl in cls)
            {
                double lo = cl.Min(p => p.Bottom), hi = cl.Max(p => p.Top);
                double cx0 = cl.Min(p => p.Hull.Min(q => q.X)) - JoinGap, cx1 = cl.Max(p => p.Hull.Max(q => q.X)) + JoinGap;
                double cy0 = cl.Min(p => p.Hull.Min(q => q.Y)) - JoinGap, cy1 = cl.Max(p => p.Hull.Max(q => q.Y)) + JoinGap;
                touchOf.Add((lo, hi, Enumerable.Range(0, gp.Count).Where(i => gp[i].Top >= lo - JoinRise && gp[i].Top <= hi + JoinRise
                    && box[i].MinX <= cx1 && box[i].MaxX >= cx0 && box[i].MinY <= cy1 && box[i].MaxY >= cy0
                    && cl.Any(st => TouchesAt(st, gp[i]))).ToList()));
            }
            var landing = new List<int>();
            foreach (var members in Enumerable.Range(0, gp.Count).GroupBy(Find).Select(g => g.ToList()))
            {
                var mset = members.ToHashSet();
                double top = members.Average(i => gp[i].Top);
                bool down = false, up = false;
                foreach (var (_, _, t) in touchOf)
                {
                    if (!t.Any(mset.Contains)) continue;
                    if (t.Any(i => !mset.Contains(i) && gp[i].Top < top - JoinRise)) down = true;
                    if (t.Any(i => !mset.Contains(i) && gp[i].Top > top + JoinRise)) up = true;
                }
                if (!(down && up)) continue;
                bool step = false;
                foreach (var i in members)
                {
                    for (int j = 0; j < gp.Count && !step; j++)
                    {
                        if (mset.Contains(j)) continue;
                        var dz = Math.Abs(gp[i].Top - gp[j].Top);
                        if (dz <= LandingSameRise || dz >= SmallStairRise) continue;
                        if (box[i].MinX - GroupGap <= box[j].MaxX && box[j].MinX - GroupGap <= box[i].MaxX && box[i].MinY - GroupGap <= box[j].MaxY && box[j].MinY - GroupGap <= box[i].MaxY) step = true;
                    }
                    if (step) break;
                }
                if (step) continue;
                foreach (var i in members)
                {
                    var p = gp[i];
                    if (!gs.Any(st => TouchesAt(st, p))) continue;
                    if (things.Any(th => th.Z >= p.Top - JoinRise && th.Z <= p.Top + LandingThingAbove && DistToConvex(p.Hull, th.X, th.Y) <= 0.5)) continue;
                    landing.Add(i);
                }
            }
            if (landing.Count == 0) continue;
            // 踊り場の板と階段のまとまりを鍵でまとめる (踊り場どうしは接するもの、踊り場と階段のまとまりは接して高さが範囲 ± JoinRise のもの)
            int n = landing.Count + cls.Count;
            var lp = Enumerable.Range(0, n).ToArray();
            int F2(int x) { while (lp[x] != x) x = lp[x] = lp[lp[x]]; return x; }
            for (int a = 0; a < landing.Count; a++)
            {
                var pa = gp[landing[a]];
                for (int b = a + 1; b < landing.Count; b++)
                    if (Math.Abs(pa.Top - gp[landing[b]].Top) <= LandingSameRise && HullGap(pa.Hull, gp[landing[b]].Hull) <= JoinGap) lp[F2(a)] = F2(b);
                for (int c = 0; c < cls.Count; c++)
                    if (pa.Top >= touchOf[c].Lo - JoinRise && pa.Top <= touchOf[c].Hi + JoinRise && cls[c].Any(st => TouchesAt(st, pa))) lp[F2(a)] = F2(landing.Count + c);
            }
            var keyOf = new Dictionary<int, int>();
            for (int a = 0; a < landing.Count; a++)
            {
                var r = F2(a);
                if (!keyOf.TryGetValue(r, out var k)) keyOf[r] = k = links++;
                var p = gp[landing[a]];
                replace[p] = p with { Stair = true, Landing = true, Link = k };
                LastLandingPlates++;
            }
            for (int c = 0; c < cls.Count; c++)
                if (keyOf.TryGetValue(F2(landing.Count + c), out var k))
                    foreach (var st in cls[c]) replace[st] = st with { Link = k };
        }
        LastLandingLinks = links;
        return replace.Count == 0 ? plates : plates.Select(p => replace.TryGetValue(p, out var q) ? q : p).ToList();
    }

    private static List<Sheet> Sheets(List<StationPlate> gp, List<StationPlate> gs)
    {
        var sheets = new List<Sheet>();
        var idx = new Dictionary<StationPlate, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < gp.Count; i++)
        {
            idx[gp[i]] = i;
            var sh = new Sheet { Lo = gp[i].Top, Hi = gp[i].Top }; sh.Floor.Add(gp[i]);
            foreach (var c in CellsOf(gp[i])) sh.Cells[c] = sh.Cells.TryGetValue(c, out var o) ? (Math.Min(o.Min, gp[i].Top), Math.Max(o.Max, gp[i].Top)) : (gp[i].Top, gp[i].Top);
            sheets.Add(sh);
        }
        // (2026-11-01、利用者の規則) 「1 明らかに階が違う階段と、数段の階段を分ける。2 数段の階段でつながっていると思しきフロアは同じ階層とする。
        // 高さの違いが 0.5 m 未満とか? その階層の高さの範囲を作成し、その範囲が 2 m 未満である限り、加えたエリアを高さの範囲に加える」
        //   ① 同じ高さでつながる床: xy の箱が GroupGap (3 m) 以内で、上面の差が DirectStepRise (0.3 m。一段) 未満の床の板どうし
        //      (2026-11-02 に SmallStairRise 1.3 m から変えた。DirectStepRise の説明)。
        //   ② 階段・スロープのまとまり (StairClusters) は、接する床の板 (JoinGap 以内、高さはまとまりの範囲 ± JoinRise) の上面の差 (いちばん高い − いちばん低い) が
        //      StairJoinRise (1.625 m。2026-11-02 に SmallStairRise 1.3 m から変えた) 未満なら「数段の階段」としてその床どうしを結び、
        //      それ以上なら「明らかに階が違う階段」として結ばない (StationRouteGraph.Stairs が階段にする)。
        //   ③ 結びを上面の差の小さい順に見て、2 つの組を合わせた高さの範囲 (床の板の上面のいちばん低い〜いちばん高い) の幅が LevelRangeMax 未満のときだけ合わせる。
        //   ④ 結ばれていない組どうしも、高さ (中央値) の差の小さい順に、合わせた範囲の幅が LevelRangeMax 未満で、上下に重ならず、上下の並びが食い違わなければ合わせる
        //      (前の「重ならない所は同じ階」を、③ の範囲の上限の中に限ったもの)。
        // 前の決まり (高さの帯 LevelGroups・SmallStepRise・上下の重なりで合わせない) は使わない
        var edges = new List<(int A, int B, double Dz)>();
        var box = gp.Select(p => (MinX: p.Hull.Min(q => q.X), MaxX: p.Hull.Max(q => q.X), MinY: p.Hull.Min(q => q.Y), MaxY: p.Hull.Max(q => q.Y))).ToList();
        var order = Enumerable.Range(0, gp.Count).OrderBy(i => box[i].MinX).ToList();
        for (int oi = 0; oi < order.Count; oi++)
            for (int oj = oi + 1; oj < order.Count && box[order[oj]].MinX <= box[order[oi]].MaxX + GroupGap; oj++)
            {
                int i = order[oi], j = order[oj];
                if (!(box[i].MinY - GroupGap <= box[j].MaxY && box[j].MinY - GroupGap <= box[i].MaxY)) continue;
                var dz = Math.Abs(gp[i].Top - gp[j].Top);
                if (dz < DirectStepRise) edges.Add((i, j, dz));
            }
        var clusterTouch = new List<(List<StationPlate> Cl, List<int> Touch)>();
        int bigEnds = 0;
        foreach (var cl in StairClusters(gs))
        {
            double lo = cl.Min(p => p.Bottom), hi = cl.Max(p => p.Top);
            double cx0 = cl.Min(p => p.Hull.Min(q => q.X)) - JoinGap, cx1 = cl.Max(p => p.Hull.Max(q => q.X)) + JoinGap;
            double cy0 = cl.Min(p => p.Hull.Min(q => q.Y)) - JoinGap, cy1 = cl.Max(p => p.Hull.Max(q => q.Y)) + JoinGap;
            var touch = Enumerable.Range(0, gp.Count).Where(i => gp[i].Top >= lo - JoinRise && gp[i].Top <= hi + JoinRise
                && box[i].MinX <= cx1 && box[i].MaxX >= cx0 && box[i].MinY <= cy1 && box[i].MaxY >= cy0
                && cl.Any(st => HullGap(st.Hull, gp[i].Hull) <= JoinGap)).OrderBy(i => gp[i].Top).ToList();
            var rise = touch.Count >= 2 ? gp[touch[^1]].Top - gp[touch[0]].Top : double.NaN;
            // 数段の階段は、下の端と上の端の両方 (± JoinRise) に接する床があるものだけ (片方の端にしか床の無い階段は、つなぐ床どうしの差が測れないため、
            // 階が違う側に入れる。Checkmate の精錬所の util_stairs_a_mid_a (-457.83〜-453.38 m) は、下の端の床だけに接していて、差 0.01 m で数段に入っていた)
            if (touch.Count >= 2 && rise < StairJoinRise && touch.Any(i => Math.Abs(gp[i].Top - lo) <= JoinRise) && touch.Any(i => Math.Abs(gp[i].Top - hi) <= JoinRise))
            {
                for (int k = 1; k < touch.Count; k++) edges.Add((touch[k - 1], touch[k], Math.Abs(gp[touch[k]].Top - gp[touch[k - 1]].Top)));
                LastSmallStairs.Add((cl, rise, touch.Select(i => gp[i].Top).ToList()));
            }
            else
            {
                LastBigStairs.Add((cl, rise, touch.Select(i => gp[i].Top).ToList()));
                // (2026-11-02、利用者の最初の指示「1 明らかに階が違う階段と、数段の階段を分ける」「踊り場を仲介して 2 層構造部分があるので注意」)
                // 明らかに階が違う階段: 階段のまとまり (StairClusters。踊り場をはさんで続く段もインスタンスの中で高さが続けば 1 つのまとまり) の高さ (下の端〜上の端) が
                // BigStairRise (1.35 m。2026-11-02 に SmallStairRise から分けた) 以上で、下の端 (± JoinRise) に接する床と上の端 (± JoinRise) に接する床の上面の差
                // (上の端の床のいちばん低いもの − 下の端の床のいちばん高いもの) も BigStairRise 以上のもの。その下の端の床と上の端の床は、同じ組 (層) に入れない (③ ④ で合わせない)。
                // 接する床の上面のいちばん高い − いちばん低い (rise) だけで見ないのは、Checkmate で、端の近くの小さな板 (col_a_structural_addon_floor_1m_a 2.2 m² の 128.62 m、
                // 壊れた通路の板 walkway_straight_broken_part_04x02x0025_a の 53.88 m) で rise が 1.30〜1.73 m になり、1.0 m の段 (stairs_04x02x01_a。床 128.75 → 129.75 m) や
                // 傾いた格子 roomsm_floor_grate_01x02x0025_a (高さ 0.59 m) まで分けていたため。
                // あいだの板 (踊り場) は、③ の段差の小さい順で先に結ばれたほうの層に入る
                if (touch.Count >= 2 && hi - lo >= BigStairRise)
                {
                    var lowEnd = touch.Where(i => Math.Abs(gp[i].Top - lo) <= JoinRise).ToList();
                    var highEnd = touch.Where(i => Math.Abs(gp[i].Top - hi) <= JoinRise).ToList();
                    if (lowEnd.Count > 0 && highEnd.Count > 0 && highEnd.Min(i => gp[i].Top) - lowEnd.Max(i => gp[i].Top) >= BigStairRise)
                    {
                        int k = bigEnds++;
                        foreach (var i in lowEnd) sheets[i].Ends.Add(k * 2);
                        foreach (var i in highEnd) sheets[i].Ends.Add(k * 2 + 1);
                    }
                }
            }
            clusterTouch.Add((cl, touch));
        }
        var parent = Enumerable.Range(0, gp.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        int merged = 0, refused = 0;
        foreach (var e in edges.OrderBy(e => e.Dz))
        {
            int ra = Find(e.A), rb = Find(e.B);
            if (ra == rb) continue;
            if (Math.Max(sheets[ra].Hi, sheets[rb].Hi) - Math.Min(sheets[ra].Lo, sheets[rb].Lo) >= LevelRangeMax) { refused++; continue; }
            if (EndsClash(sheets[ra], sheets[rb])) { LastBigStairRefused++; continue; }
            if (sheets[ra].Cells.Count < sheets[rb].Cells.Count) (ra, rb) = (rb, ra);
            Absorb(sheets[ra], sheets[rb]); parent[rb] = ra; merged++;
        }
        var roots0 = Enumerable.Range(0, gp.Count).Where(i => Find(i) == i).Select(i => sheets[i]).ToList();
        var zs = roots0.Select(r => r.Z).ToList();
        var p2 = Enumerable.Range(0, roots0.Count).ToArray();
        int Find2(int x) { while (p2[x] != x) x = p2[x] = p2[p2[x]]; return x; }
        var pairs = new List<(int A, int B, double Dz)>();
        for (int a = 0; a < roots0.Count; a++)
            for (int b = a + 1; b < roots0.Count; b++)
                if (Math.Abs(zs[a] - zs[b]) < LevelRangeMax) pairs.Add((a, b, Math.Abs(zs[a] - zs[b])));
        foreach (var (a, b, _) in pairs.OrderBy(t => t.Dz))
        {
            int ra = Find2(a), rb = Find2(b);
            if (ra == rb) continue;
            if (Math.Max(roots0[ra].Hi, roots0[rb].Hi) - Math.Min(roots0[ra].Lo, roots0[rb].Lo) >= LevelRangeMax || Overlaps(roots0[ra], roots0[rb])) continue;
            if (EndsClash(roots0[ra], roots0[rb])) { LastBigStairRefused++; continue; }
            // 上下の並びが食い違う組は合わせない: ほかの組 Z が、片方の上に重なり、もう片方の下に重なるなら合わせない
            bool clash = false;
            for (int z = 0; z < roots0.Count && !clash; z++)
            {
                if (Find2(z) != z || z == ra || z == rb) continue;
                int da = OverlapDir(roots0[z], roots0[ra]); if (da == 0) continue;
                int db = OverlapDir(roots0[z], roots0[rb]);
                if (db != 0 && db != da) clash = true;
            }
            if (clash) { LastClashRefused++; continue; }
            if (roots0[ra].Cells.Count < roots0[rb].Cells.Count) (ra, rb) = (rb, ra);
            Absorb(roots0[ra], roots0[rb]); p2[rb] = ra; merged++;
        }
        var roots = Enumerable.Range(0, roots0.Count).Where(i => Find2(i) == i).Select(i => roots0[i]).OrderBy(r => r.Z).ToList();
        // 階段・スロープのまとまりは、接する床の板が全部同じ階で、下の端と上の端の両方の高さ (± JoinRise) に接する板があるときだけ、その階の中の道にする。
        // そうでなければ階段のまま (StationRouteGraph.Stairs が別の階どうしを結ぶ)
        var sheetOf = new Dictionary<StationPlate, Sheet>(ReferenceEqualityComparer.Instance);
        foreach (var r in roots) foreach (var p in r.Floor) sheetOf[p] = r;
        foreach (var (cl, touch) in clusterTouch)
        {
            if (touch.Count == 0) continue;
            double lo = cl.Min(p => p.Bottom), hi = cl.Max(p => p.Top);
            var ss = touch.Select(i => sheetOf[gp[i]]).Distinct().ToList();
            if (ss.Count == 1 && touch.Any(i => Math.Abs(gp[i].Top - lo) <= JoinRise) && touch.Any(i => Math.Abs(gp[i].Top - hi) <= JoinRise)) ss[0].Stairs.AddRange(cl);
        }
        LastMergedPairs += merged; LastRefusedPairs += refused;
        return roots;
    }

    private static List<List<(double Z, StationPlate? Plate)>> LevelGroups(List<StationPlate> floorPlates, IEnumerable<double>? extraHeights)
    {
        var zs = floorPlates.Select(p => (Z: p.Top, Plate: (StationPlate?)p))
            .Concat((extraHeights ?? Enumerable.Empty<double>()).Select(z => (Z: z, Plate: (StationPlate?)null))).OrderBy(t => t.Z).ToList();
        var groups = new List<List<(double Z, StationPlate? Plate)>>();
        var cur = new List<(double Z, StationPlate? Plate)>();
        foreach (var t in zs)
        {
            if (cur.Count > 0 && t.Z - cur[^1].Z > FloorGap) { groups.Add(cur); cur = new(); }
            cur.Add(t);
        }
        if (cur.Count > 0) groups.Add(cur);
        var done = new List<List<(double Z, StationPlate? Plate)>>();
        var stack = new Stack<List<(double Z, StationPlate? Plate)>>(groups);
        while (stack.Count > 0)
        {
            var g = stack.Pop();
            if (g.Count < 2 || g[^1].Z - g[0].Z <= MaxFloorSpan) { done.Add(g); continue; }
            int bi = 1; double bd = -1;
            for (int i = 1; i < g.Count; i++) if (g[i].Z - g[i - 1].Z > bd) { bd = g[i].Z - g[i - 1].Z; bi = i; }
            stack.Push(g.Take(bi).ToList());
            stack.Push(g.Skip(bi).ToList());
        }
        return done.OrderBy(g => g[0].Z).ToList();
    }

    // 偽の床の板を除く (利用者の決定で足したもの): ①② で仮の階を作り、階ごとに板を「xy の箱が 0.5 m 以内で重なるもの」でまとめ、
    // まとまりの面積の合計が FakeFloorArea (100 m²) 未満で、その面積の FakeFloorOverlap (90 %) 以上が 2.5〜5 m 下の床の板の真上
    // (板の中心が下の板の凸包から 0.5 m 以内) にあるまとまりを除く。
    // Checkmate の Clinic の病棟の廊下 (clinic_uni_005_rund_001) の天井の格子 roomsm_floor_grate_frame_04x04x0025_a 3 枚 (48 m²、床 + 3.37 m) が
    // 病棟の各階の上に偽の階を作っていた (harness の levels)。階段の板はそのまま返す
    public static List<StationPlate> DropOverheadPlates(IReadOnlyList<StationPlate> plates, out int dropped)
    {
        var fp = plates.Where(p => !p.Stair).ToList();
        var stairs = plates.Where(p => p.Stair).ToList();
        var drop = new HashSet<StationPlate>(ReferenceEqualityComparer.Instance);
        foreach (var g in fp.GroupBy(p => p.Group).SelectMany(grp => LevelGroups(grp.ToList(), null)))
        {
            var ps = g.Select(t => t.Plate!).ToList();
            // 階まるごとの面積が MinFloorArea 未満なら、その階の板を全部除く (Checkmate: rstop_unilg_008 の floorplate 1 枚 4 m² (136.68 m)・
            // 店の跡 rstop_stall_sml_001_aban_001 の col_a_structural_addon_floor_2m_c 1 枚 0.8 m² (55.25 m) が、それぞれ 1 枚だけの階になっていた)
            if (ps.Sum(p => HullArea(p.Hull)) < MinFloorArea) { foreach (var p in ps) drop.Add(p); continue; }
            var box = ps.Select(p => (MinX: p.Hull.Min(q => q.X), MaxX: p.Hull.Max(q => q.X), MinY: p.Hull.Min(q => q.Y), MaxY: p.Hull.Max(q => q.Y))).ToList();
            var parent = Enumerable.Range(0, ps.Count).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            var order = Enumerable.Range(0, ps.Count).OrderBy(i => box[i].MinX).ToList();
            for (int oi = 0; oi < order.Count; oi++)
                for (int oj = oi + 1; oj < order.Count && box[order[oj]].MinX <= box[order[oi]].MaxX + 0.5; oj++)
                {
                    int i = order[oi], j = order[oj];
                    if (box[i].MinY - 0.5 <= box[j].MaxY && box[j].MinY - 0.5 <= box[i].MaxY) parent[Find(i)] = Find(j);
                }
            foreach (var comp in Enumerable.Range(0, ps.Count).GroupBy(Find))
            {
                var cs = comp.Select(i => ps[i]).ToList();
                var area = cs.Sum(p => HullArea(p.Hull));
                if (area <= 0 || area >= FakeFloorArea) continue;
                double over = cs.Where(p =>
                {
                    double cx = p.Hull.Average(q => q.X), cy = p.Hull.Average(q => q.Y);
                    return fp.Any(q => q.Group == p.Group && p.Top - q.Top >= FakeFloorMinRise && p.Top - q.Top <= 5 && DistToConvex(q.Hull, cx, cy) <= 0.5);
                }).Sum(p => HullArea(p.Hull));
                // (2026-11-02) 階段・坂の板に接する (SurfacesTouch) 板のあるまとまりは、偽の床にしない (階段の踊り場。利用者の原則「すき間の無い高低差が 0.5m 未満の続きの面」)。
                // 形で床にした屋上 roof_base_small (Area18 の Arccorp Tower、368.21 m) の 3.8 m 上の踊り場 landing_walkway_straight_04x04x0025 (372.00 m) が、
                // 偽の床として除かれ、上下の階段がつながらなくなっていた
                if (over / area >= FakeFloorOverlap && !cs.Any(p => stairs.Any(s => SurfacesTouch(p, s)))) foreach (var p in cs) drop.Add(p);
            }
        }
        dropped = drop.Count;
        return plates.Where(p => !drop.Contains(p)).ToList();
    }

    // 凸包どうしの xy の近さ (重なれば 0)。互いの頂点から相手の凸包までの距離の小さいほう
    public static double HullGap((double X, double Y)[] a, (double X, double Y)[] b) =>
        Math.Min(a.Min(p => DistToConvex(b, p.X, p.Y)), b.Min(p => DistToConvex(a, p.X, p.Y)));

    public static double HullArea((double X, double Y)[] h)
    {
        double a = 0;
        for (int i = 0; i < h.Length; i++) { var p = h[i]; var q = h[(i + 1) % h.Length]; a += p.X * q.Y - q.X * p.Y; }
        return Math.Abs(a) / 2;
    }

    // 点から凸多角形までの距離 (中なら 0)
    public static double DistToConvex((double X, double Y)[] h, double x, double y)
    {
        if (InConvex(h, x, y)) return 0;
        double md = double.MaxValue;
        for (int i = 0; i < h.Length; i++)
        {
            var a = h[i]; var b = h[(i + 1) % h.Length];
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / l2, 0, 1) : 0;
            md = Math.Min(md, Math.Sqrt(Sq(a.X + t * dx - x) + Sq(a.Y + t * dy - y)));
        }
        return md;
    }

    // 局所の高さ z の階 (z の下にあるいちばん高い階。下 tolBelow m までの階は z の上にあっても含める)。maxAbove より上に離れていれば null
    public static StationFloor? FloorAt(IReadOnlyList<StationFloor> floors, double z, double tolBelow = 0.6, double maxAbove = 6)
    {
        StationFloor? best = null;
        foreach (var f in floors)
            if (f.ZMin <= z + tolBelow && (best == null || f.Z > best.Z)) best = f;
        if (best != null && z - best.ZMax > maxAbove) return null;
        return best;
    }

    // xy つきの階: 点の xy を箱 (Bounds) に含む階のまとまりのうち、高さで当たる階のあるもので、箱のいちばん小さいまとまりの階。
    // Bounds の無い階 (前の版の JSON・乗り場の高さの階) は、どの点も含むものとして扱う
    // (2026-11-01) 階に升目の高さ (CellZ) があれば、それで選ぶ (CellPick)。1 つの階が高さの違う床を持つため (Levels の説明)
    public static StationFloor? FloorAtXY(IReadOnlyList<StationFloor> floors, double x, double y, double z, double tolBelow = 0.6, double maxAbove = 6) =>
        CellPick(floors, x, y, z, tolBelow, maxAbove, out var c) && c != null ? c : InSmallestGroup(floors.Where(f => f.CellZ == null).ToList(), x, y, g => FloorAt(g, z: z, tolBelow: tolBelow, maxAbove: maxAbove), z);

    public static StationFloor? NearestFloorXY(IReadOnlyList<StationFloor> floors, double x, double y, double z, double tol) =>
        CellPick(floors, x, y, z, tol, tol, out var c) && c != null ? c : InSmallestGroup(floors.Where(f => f.CellZ == null).ToList(), x, y, g => NearestFloor(g, z: z, tol: tol), z);

    // まとまりが 2 つ以上当たるときは、点の高さに近い階 (点 − 階の範囲、中なら 0。次に点 − 階の中央値) のまとまりを先に、同じなら箱の小さいほう
    // (Checkmate で、Clinic の病棟の乗り場 (63.8 m) が、箱の小さい貨物のまとまりの 64.0 m の階に当たっていた)
    private static StationFloor? InSmallestGroup(IReadOnlyList<StationFloor> floors, double x, double y, Func<IReadOnlyList<StationFloor>, StationFloor?> pick, double z = double.NaN)
    {
        StationFloor? best = null; (double Gap, double Mid, double Area) bestKey = (double.MaxValue, double.MaxValue, double.MaxValue);
        foreach (var g in floors.Where(f => f.Contains(x, y)).GroupBy(f => f.Group))
        {
            var f = pick(g.ToList());
            if (f == null) continue;
            var area = f.Bounds is { } b ? (b.MaxX - b.MinX) * (b.MaxY - b.MinY) : double.MaxValue / 2;
            // 点 − 階の範囲 (中なら 0)、次に点 − 階の中央値 (Z)、次に箱の面積の小さい順
            var key = double.IsNaN(z) ? (0, 0, area) : (Math.Abs(z < f.ZMin ? f.ZMin - z : z > f.ZMax ? z - f.ZMax : 0), Math.Abs(z - f.Z), area);
            if (key.CompareTo(bestKey) < 0) { bestKey = key; best = f; }
        }
        return best;
    }

    // 階の高さの板の上面に最も近い階 (階段の上下の端を階に当てるとき)。tol より離れていれば null
    public static StationFloor? NearestFloor(IReadOnlyList<StationFloor> floors, double z, double tol)
    {
        StationFloor? best = null;
        double bd = double.MaxValue;
        foreach (var f in floors)
        {
            var d = z < f.ZMin ? f.ZMin - z : z > f.ZMax ? z - f.ZMax : 0;
            if (d < bd) { bd = d; best = f; }
        }
        return bd <= tol ? best : null;
    }

    // 区画: 床の板のあるインスタンス (ルートを除く) と、板は無いが部屋 (entitycomponentroom) のあるインスタンス (箱で代用)。
    // 箱の大きさが 2 km を超えるもの (外側の飾り・砲台などの箱) は区画にしない
    public static List<StationSection> Sections(StationTree tree, IReadOnlyList<StationPlate> plates, IReadOnlyList<StationFloor> floors)
    {
        var byInst = plates.Where(p => PlateFloor(p, floors) != null).GroupBy(p => p.Instance).ToDictionary(g => g.Key, g => g.ToList());
        var roomInst = tree.Points.Where(p => p.Kind == "entitycomponentroom").Select(p => p.Instance).ToHashSet();
        var res = new List<StationSection>();
        // (2026-11-01) 速さのため: 名札の点はインスタンスごとに引けるようにしておき、板からの多角形は区画ごとに並べて作る (結果は前と同じ)
        var labelsByInst = tree.Points.Where(p => p.Kind == "entitycomponentinteriormaplabel" && p.Pos != null).ToLookup(p => p.Instance);
        var outlineJobs = new List<(StationSection Sec, List<StationPlate> Plates)>();
        foreach (var inst in tree.Instances)
        {
            if (inst.MinBounds is not { } a || inst.MaxBounds is not { } b) continue;
            var has = byInst.TryGetValue(inst.Index, out var ps);
            // (2026-11-01) 床の板のあるインスタンスは、ルート (0 番) でも箱が 2 km を超えていても区画にする (床の形を描くため)。
            // Seraphim の Pad 01〜04 の床 (ext_landingpad_floor_* など、ルート rs_ext_cru-leo1 の板) は、ルートを区画にしていなかったため、階はあるのに形が描かれなかった。
            // 板の無いルート・2 km を超える箱は前と同じく区画にしない
            if (!has && (inst.Index == 0 || b.X - a.X > 2000 || b.Y - a.Y > 2000 || b.Z - a.Z > 2000)) continue;
            if (!has && !roomInst.Contains(inst.Index)) continue;
            var c = inst.ToStation.Apply(((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2));
            // z 回りの向き: インスタンスの回転で x 軸を移した向き (傾いたインスタンスでは近似)
            var ax = inst.ToStation.Rotate(1, 0, 0);
            var sec = new StationSection
            {
                Instance = inst.Index, Socpak = inst.Socpak, EntityName = inst.EntityName,
                Center = c, Half = ((b.X - a.X) / 2, (b.Y - a.Y) / 2, (b.Z - a.Z) / 2), Yaw = Math.Atan2(ax.Y, ax.X) * 180 / Math.PI,
                BoxFallback = !has, PlateCount = ps?.Count ?? 0,
            };
            if (has) outlineJobs.Add((sec, ps!));
            else
            {
                // 箱で代用: 箱の 4 隅 (局所の xy) を、箱の底の高さの階に置く
                var bottom = inst.ToStation.Apply(((a.X + b.X) / 2, (a.Y + b.Y) / 2, a.Z)).Z;
                var bc = inst.ToStation.Apply(((a.X + b.X) / 2, (a.Y + b.Y) / 2, a.Z));
                var f = FloorAtXY(floors, bc.X, bc.Y, bottom + 0.5);
                if (f != null)
                {
                    var corners = new[] { (a.X, a.Y), (b.X, a.Y), (b.X, b.Y), (a.X, b.Y) }
                        .Select(q => { var p = inst.ToStation.Apply((q.Item1, q.Item2, a.Z)); return (Math.Round(p.X, 2), Math.Round(p.Y, 2)); }).ToList();
                    sec.Polygons[f.Id] = new List<List<(double X, double Y)>> { corners };
                    sec.PolygonArea[f.Id] = HullArea(corners.ToArray());
                }
            }
            // 名札: このインスタンスの interiormaplabel のうち箱の中心にいちばん近いもの
            sec.Label = labelsByInst[inst.Index]
                .OrderBy(p => Math.Pow(p.Pos!.Value.X - c.X, 2) + Math.Pow(p.Pos!.Value.Y - c.Y, 2) + Math.Pow(p.Pos!.Value.Z - c.Z, 2)).FirstOrDefault()?.Name ?? "";
            res.Add(sec);
        }
        var groupsBySec = outlineJobs.Select(j => (j.Sec, Groups: j.Plates.GroupBy(p => PlateFloor(p, floors)?.Id ?? "").Where(g => g.Key.Length > 0).Select(g => (g.Key, Hulls: g.Select(p => p.Hull).ToList())).ToList())).ToList();
        var outlines = new List<(string Key, List<List<(double X, double Y)>> Rings)>[groupsBySec.Count];
        Parallel.For(0, groupsBySec.Count, k => outlines[k] = groupsBySec[k].Groups.Select(g => (g.Key, Outline(g.Hulls))).ToList());
        // 形は板ごとの凸包 (Polygons)。格子の輪郭 (Outline) は面積 (PolygonArea) にだけ使う
        for (int k = 0; k < groupsBySec.Count; k++)
            foreach (var (key, rings) in outlines[k])
            {
                if (rings.Count == 0) continue;
                var sec = groupsBySec[k].Sec;
                sec.PolygonArea[key] = rings.Sum(r => HullArea(r.ToArray()));
                sec.Polygons[key] = groupsBySec[k].Groups.First(g => g.Key == key).Hulls.Where(h => h.Length >= 3)
                    .Select(h => h.Select(p => (Math.Round(p.X, 2), Math.Round(p.Y, 2))).ToList()).ToList();
            }
        return res;
    }

    // 板の階: 床の板は上面の高さの階。階段の板は、下の端と上の端 (1.5 m 以内で近い階) が同じ階のときだけその階 (段差の小さい階段。
    // Checkmate のロビー (base_view_004) の段差 util_stairs_straight_04x02x01_a (133.75〜134.75 m) が床に入っておらず、床が途切れていた)。違う階をつなぐ階段は null
    private sealed class PlateFloorMap
    {
        public Dictionary<string, List<StationFloor>> ByGroup = new(StringComparer.Ordinal);
        public Dictionary<StationPlate, List<StationFloor>> ByPlate = new(ReferenceEqualityComparer.Instance);
        public bool AnyPlateSet;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<StationFloor>, PlateFloorMap> PlateFloorMaps = new();
    // 階の並びの中身 (PlateSet・Group) を変えたときに、覚えた表を捨てる (StationRouteGraph.MergeUnnamedUnlistedFloors)
    internal static void ForgetPlateFloorMap(IReadOnlyList<StationFloor> floors) => PlateFloorMaps.Remove(floors);

    private static PlateFloorMap BuildPlateFloorMap(IReadOnlyList<StationFloor> floors)
    {
        var m = new PlateFloorMap();
        foreach (var f in floors)
        {
            if (!m.ByGroup.TryGetValue(f.Group, out var l)) m.ByGroup[f.Group] = l = new List<StationFloor>();
            l.Add(f);
            if (f.PlateSet == null) continue;
            m.AnyPlateSet = true;
            foreach (var p in f.PlateSet)
            {
                if (!m.ByPlate.TryGetValue(p, out var pl)) m.ByPlate[p] = pl = new List<StationFloor>();
                pl.Add(f);
            }
        }
        return m;
    }

    public static StationFloor? PlateFloor(StationPlate p, IReadOnlyList<StationFloor> floors)
    {
        // (2026-11-01) 階の並び (floors) ごとに、まとまり → 階・板 → 階 の表を作って覚えておく (板ごとに全部の階をたどっていて、Orison で遅かった)。結果は前と同じ
        var map = PlateFloorMaps.GetValue(floors, BuildPlateFloorMap);
        var gkey = p.Group >= 0 ? $"g{p.Group}" : null;
        IReadOnlyList<StationFloor> own = gkey != null ? (map.ByGroup.TryGetValue(gkey, out var gl) ? gl : Array.Empty<StationFloor>()) : floors;
        if (own.Count == 0) own = floors;
        // Levels で作った階は、板の組 (PlateSet) で決める。階段の板で、どの階にも合わせなかったものは null (別の階をつなぐ階段)
        if (ReferenceEquals(own, floors) ? map.AnyPlateSet : own.Any(f => f.PlateSet != null))
        {
            // 板の入る階 (PlateSet に入る、own の中で並びの最初のもの)
            if (map.ByPlate.TryGetValue(p, out var hits)) foreach (var f in hits) if (own.Contains(f)) return f;
            return null;
        }
        if (!p.Stair) return FloorAt(own, p.Top, 0.01, 0.01);
        var a = NearestFloor(own, p.Bottom, 1.5);
        return a != null && a == NearestFloor(own, p.Top, 1.5) ? a : null;
    }

    // 階ごとの床のセル (0.5 m の格子、原点 0。キーは (i, j))。道順の「区画をまたぐ徒歩」の辺で、線分が床の上を通るかを見るのに使う
    public static Dictionary<string, HashSet<(int I, int J)>> FloorCells(IEnumerable<StationPlate> plates, IReadOnlyList<StationFloor> floors)
    {
        var res = floors.ToDictionary(f => f.Id, _ => new HashSet<(int, int)>(), StringComparer.Ordinal);
        // (2026-11-01) 階ごとに別の HashSet なので、階ごとに並べて塗る (板の順は前と同じ。結果は前と同じ)。Orison で 11 秒かかっていた
        var byFloor = plates.Select(p => (p, f: PlateFloor(p, floors))).Where(t => t.f != null).GroupBy(t => t.f!.Id).Select(g => (Id: g.Key, Plates: g.Select(t => t.p).ToList())).ToList();
        Parallel.ForEach(byFloor, g => { foreach (var p in g.Plates) PaintFloorCells(res[g.Id], p); });
        return res;
    }

    private static void PaintFloorCells(HashSet<(int I, int J)> set, StationPlate p)
    {
        {
            int i0 = (int)Math.Floor(p.Hull.Min(q => q.X) / Cell) - 2, i1 = (int)Math.Ceiling(p.Hull.Max(q => q.X) / Cell) + 2;
            int j0 = (int)Math.Floor(p.Hull.Min(q => q.Y) / Cell) - 2, j1 = (int)Math.Ceiling(p.Hull.Max(q => q.Y) / Cell) + 2;
            if ((long)(i1 - i0) * (j1 - j0) > 4_000_000) return;
            // 塗る範囲 (凸包と、そのまわり PaintMargin の帯) は凸なので、行ごとに左右の端だけを確かめて、あいだを全部塗る (結果は 1 セルずつ確かめたときと同じ)
            for (int j = j0; j <= j1; j++)
            {
                double y = (j + 0.5) * Cell;
                int l = i0; while (l <= i1 && !Paint(p.Hull, (l + 0.5) * Cell, y)) l++;
                if (l > i1) continue;
                int r = i1; while (r > l && !Paint(p.Hull, (r + 0.5) * Cell, y)) r--;
                for (int i = l; i <= r; i++) set.Add((i, j));
            }
        }
    }

    // 線分 (xy) が床のセルの上を通る割合 (0.25 m ごとに見て、その点のセルか隣の 8 セルに床があれば床の上とみなす)
    public static double OnFloorRatio(HashSet<(int I, int J)> cells, (double X, double Y) a, (double X, double Y) b)
    {
        var len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
        int n = Math.Max(1, (int)Math.Ceiling(len / 0.25));
        int on = 0;
        for (int k = 0; k <= n; k++)
        {
            double x = a.X + (b.X - a.X) * k / n, y = a.Y + (b.Y - a.Y) * k / n;
            int i = (int)Math.Floor(x / Cell), j = (int)Math.Floor(y / Cell);
            bool hit = false;
            for (int di = -1; di <= 1 && !hit; di++)
                for (int dj = -1; dj <= 1 && !hit; dj++)
                    hit = cells.Contains((i + di, j + dj));
            if (hit) on++;
        }
        return (double)on / (n + 1);
    }

    // 点 (局所) が入る区画: 箱 (インスタンスの枠) に入るもののうち、その階の多角形に xy が入るもの。無ければ箱に入るいちばん小さいもの
    public static StationSection? SectionAt(StationTree tree, IReadOnlyList<StationSection> sections, StationFloor? floor, (double X, double Y, double Z) p)
    {
        StationSection? best = null, bestPoly = null;
        double bv = double.MaxValue, bpv = double.MaxValue;
        foreach (var s in sections)
        {
            var inst = tree.Instances[s.Instance];
            var q = inst.ToStation.Inverse().Apply(p);
            var a = inst.MinBounds!.Value; var b = inst.MaxBounds!.Value;
            if (q.X < a.X - 0.5 || q.X > b.X + 0.5 || q.Y < a.Y - 0.5 || q.Y > b.Y + 0.5 || q.Z < a.Z - 1 || q.Z > b.Z + 1) continue;
            var vol = (b.X - a.X) * (b.Y - a.Y) * (b.Z - a.Z);
            if (vol < bv) { bv = vol; best = s; }
            if (floor != null && s.Polygons.TryGetValue(floor.Id, out var rings) && OnRings(rings, p.X, p.Y) && vol < bpv) { bpv = vol; bestPoly = s; }
        }
        return bestPoly ?? best;
    }

    // 区画の形 (凸の環の並び。Polygons) のどれかの中か、その縁から PaintMargin 以内か (形を格子の輪郭にしていたときの、縁の帯を含む判定と同じ幅)
    // (箱の 4 隅の環は回りの向きが決まらないので、中かどうかは向きによらない数え方で確かめる)
    public static bool OnRings(IEnumerable<List<(double X, double Y)>> rings, double x, double y)
    {
        foreach (var r in rings)
        {
            if (r.Count < 3) continue;
            bool inside = false;
            double md = double.MaxValue;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            {
                if ((r[i].Y > y) != (r[j].Y > y) && x < (r[j].X - r[i].X) * (y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X) inside = !inside;
                double dx = r[i].X - r[j].X, dy = r[i].Y - r[j].Y, l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Clamp(((x - r[j].X) * dx + (y - r[j].Y) * dy) / l2, 0, 1) : 0;
                md = Math.Min(md, Math.Sqrt(Sq(r[j].X + t * dx - x) + Sq(r[j].Y + t * dy - y)));
            }
            if (inside || md <= PaintMargin) return true;
        }
        return false;
    }

    // 凸包 (Andrew の方法。反時計回り)
    public static (double X, double Y)[] ConvexHull(List<(double X, double Y)> pts)
    {
        var p = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p.ToArray();
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double X, double Y)>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 1e-9) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (h.Count >= lower && Cross(h[^2], h[^1], q) <= 1e-9) h.RemoveAt(h.Count - 1); h.Add(q); }
        h.RemoveAt(h.Count - 1);
        return h.ToArray();
    }

    // 床に塗るか: セルの中心が板の凸包の PaintMargin (0.5 m) 以内 (板の縁・扉の下の細い途切れを埋めるため。
    // Orison の宇宙港のエレベーター前の実測点は、床の板 sp_int_garage_lobby_floor_a の 0.2 m 外だった)
    public const double PaintMargin = 0.5;

    private static bool Paint((double X, double Y)[] hull, double x, double y) => InConvex(hull, x, y) || DistToConvex(hull, x, y) <= PaintMargin;

    private static bool InConvex((double X, double Y)[] hull, double x, double y)
    {
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Length];
            if ((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X) < -1e-9) return false;
        }
        return true;
    }

    // 凸多角形の並びを 0.5 m の格子に塗り (セルの中心が入るもの)、marching squares で輪郭の環にして Douglas–Peucker で間引く
    public static List<List<(double X, double Y)>> Outline(IEnumerable<(double X, double Y)[]> hulls)
    {
        var hs = hulls.ToList();
        if (hs.Count == 0) return new();
        double minX = hs.Min(h => h.Min(p => p.X)), minY = hs.Min(h => h.Min(p => p.Y));
        double maxX = hs.Max(h => h.Max(p => p.X)), maxY = hs.Max(h => h.Max(p => p.Y));
        // 格子の原点を 0.5 m の倍数にそろえる (区画どうしの輪郭が同じ格子に乗るように)
        double ox = Math.Floor(minX / Cell) * Cell - 3 * Cell, oy = Math.Floor(minY / Cell) * Cell - 3 * Cell;
        int nx = (int)Math.Ceiling((maxX - ox) / Cell) + 4, ny = (int)Math.Ceiling((maxY - oy) / Cell) + 4;
        if ((long)nx * ny > 50_000_000) return new();
        var g = new bool[nx, ny];
        foreach (var h in hs)
        {
            int i0 = Math.Max(1, (int)Math.Floor((h.Min(p => p.X) - ox) / Cell) - 2), i1 = Math.Min(nx - 2, (int)Math.Ceiling((h.Max(p => p.X) - ox) / Cell) + 2);
            int j0 = Math.Max(1, (int)Math.Floor((h.Min(p => p.Y) - oy) / Cell) - 2), j1 = Math.Min(ny - 2, (int)Math.Ceiling((h.Max(p => p.Y) - oy) / Cell) + 2);
            // 行ごとに左右の端だけを確かめる (FloorCells と同じ。塗る範囲は凸)
            for (int j = j0; j <= j1; j++)
            {
                double y = oy + (j + 0.5) * Cell;
                int l = i0; while (l <= i1 && !Paint(h, ox + (l + 0.5) * Cell, y)) l++;
                if (l > i1) continue;
                int r = i1; while (r > l && !Paint(h, ox + (r + 0.5) * Cell, y)) r--;
                for (int i = l; i <= r; i++) g[i, j] = true;
            }
        }
        return Contours(g, nx, ny, ox, oy).Select(r => DouglasPeuckerRing(r, Simplify)).Where(r => r.Count >= 3)
            .Select(r => r.Select(p => (Math.Round(p.X, 2), Math.Round(p.Y, 2))).ToList()).ToList();
    }

    // marching squares (セルの中心の値 0/1 を標本点にし、0.5 の等値線。標本点の間の中点を結ぶ)。
    // 鞍点 (対角だけが 1) は 1 のセルをつながないほうにする。端は 0 で囲む (塗る範囲の外に 1 セル以上の余白を取っている)
    private static List<List<(double X, double Y)>> Contours(bool[,] g, int nx, int ny, double ox, double oy)
    {
        // 標本点 (i, j) の座標 = セルの中心。辺の中点を整数の 2 倍の座標で表して、線分をつなぐ
        var next = new Dictionary<(int, int), (int, int)>();
        bool V(int i, int j) => i >= 0 && j >= 0 && i < nx && j < ny && g[i, j];
        void Seg((int, int) a, (int, int) b) => next[a] = b;
        for (int i = -1; i < nx; i++)
            for (int j = -1; j < ny; j++)
            {
                // 四角の 4 隅: 左下 (i,j)、右下 (i+1,j)、右上 (i+1,j+1)、左上 (i,j+1)
                int c = (V(i, j) ? 1 : 0) | (V(i + 1, j) ? 2 : 0) | (V(i + 1, j + 1) ? 4 : 0) | (V(i, j + 1) ? 8 : 0);
                if (c == 0 || c == 15) continue;
                // 辺の中点 (2 倍の座標): 下 B、右 R、上 T、左 L
                var B = (2 * i + 1, 2 * j); var R = (2 * i + 2, 2 * j + 1); var T = (2 * i + 1, 2 * j + 2); var L = (2 * i, 2 * j + 1);
                // 1 の側を右に見る向き (外周は時計回り、穴は反時計回り)
                switch (c)
                {
                    case 1: Seg(L, B); break;
                    case 2: Seg(B, R); break;
                    case 3: Seg(L, R); break;
                    case 4: Seg(R, T); break;
                    case 5: Seg(L, B); Seg(R, T); break; // 鞍点: 1 のセルどうしをつながない
                    case 6: Seg(B, T); break;
                    case 7: Seg(L, T); break;
                    case 8: Seg(T, L); break;
                    case 9: Seg(T, B); break;
                    case 10: Seg(B, R); Seg(T, L); break; // 鞍点: 1 のセルどうしをつながない
                    case 11: Seg(T, R); break;
                    case 12: Seg(R, L); break;
                    case 13: Seg(R, B); break;
                    case 14: Seg(B, L); break;
                }
            }
        var rings = new List<List<(double X, double Y)>>();
        var used = new HashSet<(int, int)>();
        foreach (var start in next.Keys)
        {
            if (used.Contains(start)) continue;
            var ring = new List<(double X, double Y)>();
            var cur = start;
            while (used.Add(cur))
            {
                ring.Add((ox + (cur.Item1 / 2.0 + 0.5) * Cell, oy + (cur.Item2 / 2.0 + 0.5) * Cell));
                if (!next.TryGetValue(cur, out cur)) break;
            }
            if (ring.Count >= 3) rings.Add(ring);
        }
        return rings;
    }

    // 閉じた環の Douglas–Peucker (環をいちばん離れた 2 点で 2 つに分けて、それぞれを間引く)
    public static List<(double X, double Y)> DouglasPeuckerRing(List<(double X, double Y)> ring, double eps)
    {
        if (ring.Count < 4) return ring;
        int far = 0; double fd = -1;
        for (int i = 1; i < ring.Count; i++) { var d = Sq(ring[i].X - ring[0].X) + Sq(ring[i].Y - ring[0].Y); if (d > fd) { fd = d; far = i; } }
        var a = ring.Take(far + 1).ToList();
        var b = ring.Skip(far).Append(ring[0]).ToList();
        var ra = DouglasPeucker(a, eps); var rb = DouglasPeucker(b, eps);
        var res = ra.Take(ra.Count - 1).Concat(rb.Take(rb.Count - 1)).ToList();
        return res;
    }

    private static double Sq(double v) => v * v;

    public static List<(double X, double Y)> DouglasPeucker(List<(double X, double Y)> pts, double eps)
    {
        if (pts.Count < 3) return pts.ToList();
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        while (stack.Count > 0)
        {
            var (s, e) = stack.Pop();
            double md = -1; int mi = -1;
            var (ax, ay) = pts[s]; var (bx, by) = pts[e];
            double dx = bx - ax, dy = by - ay, len = Math.Sqrt(dx * dx + dy * dy);
            for (int i = s + 1; i < e; i++)
            {
                var d = len < 1e-12 ? Math.Sqrt(Sq(pts[i].X - ax) + Sq(pts[i].Y - ay)) : Math.Abs(dy * (pts[i].X - ax) - dx * (pts[i].Y - ay)) / len;
                if (d > md) { md = d; mi = i; }
            }
            if (mi >= 0 && md > eps) { keep[mi] = true; stack.Push((s, mi)); stack.Push((mi, e)); }
        }
        return pts.Where((p, i) => keep[i]).ToList();
    }
}
