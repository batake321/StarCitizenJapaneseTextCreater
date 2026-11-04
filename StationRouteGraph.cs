using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// 乗り場の路線 (2026-11-04、利用者の指示「2D 地図は消して」で、2D の地図の道順・階・階段の作りを消し、3D (新しい方式) が使う路線の作り方だけを残した)。
// 区画の .soc の metadata (sctransitgateway・sctransitdestination・sctransitmanager・sctransitnavsplinedata・sctransitperipheral) から、路線と乗り場を作る
// 乗り場 (Kind は elevator・elevator-noride・tram) と行き先 (Kind は destination)。Line は路線のキー、Stop は乗り場のいちばん近い同じ路線の行き先の名前。Floor は使わない (空)
internal sealed record StationTransit(string Kind, int Instance, string Guid, IReadOnlyList<string> Tags, (double X, double Y, double Z) Pos, string Floor,
    string Line, string Stop);

internal static class StationRouteGraph
{
    // 道筋の端と乗り場を同じとみなす距離
    public const double SplineSnap = 10;

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

    private static readonly HashSet<string> NoNameKeys = new(StringComparer.OrdinalIgnoreCase) { "@LOC_UNINITIALIZED", "@generic_upper_floor", "@generic_lower_floor", "@Maps_LocationUnknown", "" };

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
    // ハンガーの中のエレベーターの路線の名前の頭 (TransitLines。ビューアでステーションへ戻る一覧を出すのに使う)。路線は乗り場ごと (頭 + インスタンス + ":" + CryGUID)
    public const string HangarLiftLinePrefix = "hangarlift:";
    // (2026-11-03、親の決定「案 2」) ハンガーのエレベーターの乗り場のタグ。4 種類のハンガー (hangar_smlfrnt_001_rest_occu・hangar_xltop_001_orison・hangar_xltop_001_lorville・
    // hangar_medfrnt_002_rest_rund) の乗り場と、Lorville のステーション側のハンガーのエレベーターの路線で同じだった
    public const string HangarLiftTag = "37d8b7b6-58df-48d9-ad06-bcc0885a81b8";

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
        // (2026-11-03、親の決定) ハンガーの地図だけ: ハンガーのエレベーターの乗り場。案 2: タグ HangarLiftTag を持つ乗り場。
        // 1 つも無いハンガーだけ案 1: 路線の管理のタグを持たない乗り場 (sctransitgateway・新しい仕組みの sctransportgateway を直したもの)。
        // どちらも救急の降ろし場のエレベーター (socpak elev_util_dropoff の中) は外す (ステーションへ戻るエレベーターではないため)
        var hangarLift = new HashSet<StationPoint>(ReferenceEqualityComparer.Instance);
        if (tree.Root.Place.IsHangar)
        {
            bool Dropoff(StationPoint g) => tree.Instances[g.Instance].Socpak.Contains("elev_util_dropoff", StringComparison.OrdinalIgnoreCase);
            foreach (var g in allGw.Where(g => !Dropoff(g) && g.Tags.Contains(HangarLiftTag, StringComparer.OrdinalIgnoreCase))) hangarLift.Add(g);
            if (hangarLift.Count == 0)
                foreach (var g in allGw.Where(g => !Dropoff(g) && !g.Tags.Any(mgrTags.Contains))) hangarLift.Add(g);
        }
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
            // ハンガーのエレベーター (ハンガーの地図だけ) は乗り場ごとの路線 (行き先はハンガーの外 (ステーション) なので、地図の中の止まる所は 1 つ)。
            // 装置・路線の管理の決まりより先に決める (親の決定はタグ (無ければ管理の無い乗り場) だけで決める形のため)
            if (hangarLift.Contains(g))
            {
                var hk = HangarLiftLinePrefix + g.Instance + ":" + g.Guid.ToLowerInvariant();
                if (!lines.TryGetValue(hk, out var hl)) lines[hk] = hl = new Line { Key = hk };
                hl.Gateways.Add((g.Instance, g));
                continue;
            }
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

    // 乗り場・行き先の一覧 (2026-11-04、利用者の指示「2D 地図は消して」: 階 (2D の床の判定) は使わない。Floor は空)
    public static List<StationTransit> TransitList(StationTree tree, IReadOnlyList<Line> lines)
    {
        var res = new List<StationTransit>();
        var dests = tree.Points.Where(p => p.Kind == "sctransitdestination" && p.Pos != null).ToList();
        foreach (var l in lines)
            foreach (var (inst, g) in l.Gateways)
                res.Add(new StationTransit(l.Tram ? "tram" : l.NoRide ? "elevator-noride" : "elevator", inst, g.Guid, g.Tags, g.Pos!.Value, "", l.Key, StopName(dests, l.Key, g.Pos!.Value)));
        foreach (var d in dests)
            res.Add(new StationTransit("destination", d.Instance, d.Guid, d.Tags, d.Pos!.Value, "", d.Tags.FirstOrDefault()?.ToLowerInvariant() ?? "", d.Name));
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
}
