using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// ［マップ］の現在位置と、惑星・衛星の自転の角度。
//   座標は starmap_locations の x / y / z (自転の角度 0 のとき) と spin_parent / lx / ly / lz (自転の親からの相対位置) / rot_hours (1 回転にかかる時間)
//   (GameDataExtractor.ShopTables.BuildStarmap)。
//   自転の角度 θ(t) = データの式 360° × (t の UNIX 秒 ÷ (rot_hours × 3600)) mod 360 (+z の回り) + 天体の補正値 (現在位置で実測したときに覚えたもの。
//   無ければ 0)。rot_hours が 0 か無ければ回さない (補正もしない)。
//   場所の位置 (θ を当てたもの) = 自転の親の位置 + Rz(θ)·相対位置
internal static class CurrentLocation
{
    // /showlocation がクリップボードに入れる文字「Coordinates: x:10550836006.489370 y:-959305980.226979 z:2570470.808315」
    private static readonly Regex CoordRegex = new(
        @"Coordinates:\s*x:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s+y:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)\s+z:\s*(-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)",
        RegexOptions.CultureInvariant);

    // 座標の文字を読む。形が違えば null
    public static (double X, double Y, double Z)? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || CoordRegex.Match(text) is not { Success: true } m) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var st = System.Globalization.NumberStyles.Float;
        if (double.TryParse(m.Groups[1].Value, st, inv, out var x) && double.TryParse(m.Groups[2].Value, st, inv, out var y)
            && double.TryParse(m.Groups[3].Value, st, inv, out var z) && double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z))
            return (x, y, z);
        return null;
    }

    // ゲームログの行 (今の Game.log の追い読みの行) から今の着陸場所の名前: 到着・ログイン・復活・出発の行のうちいちばん新しいものが出発なら null (着陸場所の外)、
    // それ以外はその行の場所 (GameLogVoiceScenes.ArrivalPlace。名前待ちで分からなければ null)
    public static string? LandingPlaceFromEntries(IEnumerable<GameLogEntry> entries, GameLogPlaceNames? placeNames)
    {
        var last = entries.Where(e => e.Kind is "到着" or "ログイン" or "復活" or "出発")
            .OrderBy(e => e.TimeUtc).ThenBy(e => e.Seq).LastOrDefault();
        if (last == null || last.Kind == "出発") return null;
        var name = GameLogVoiceScenes.ArrivalPlace(last, placeNames);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    // 天体の自転の角度のデータの式 (ラジアン。0〜2π): θ(t) = 2π × (t ÷ (rot_hours × 3600)) mod 2π。rot_hours が 0 か無ければ null (回さない)
    public static double? DataThetaAt(ShopTreeNode body, double unix)
    {
        if (body.RotHours is not { } h || !(h > 0) || !double.IsFinite(h)) return null;
        var turns = unix / (h * 3600);
        var frac = turns - Math.Floor(turns);
        return frac * 2 * Math.PI;
    }

    // 天体の補正値 (無ければ null)。list は読んだ時点の App.Config.MapSpinCorrections (null ならここで 1 回だけ読む)。
    // 補正値のリストは書くときに丸ごと入れ替える (Locate) ので、読んだリストはその後も変わらない
    // 天体ごとの、最後に入れ替えられる前の補正値 (Locate が測り直して入れ替えたときの前の値。アプリを動かしている間だけ。キーは天体の record 名)
    public static readonly Dictionary<string, MapSpinCorrection> PreviousCorrections = new(StringComparer.Ordinal);

    public static MapSpinCorrection? CorrectionOf(ShopTreeNode body, IReadOnlyList<MapSpinCorrection>? list = null) =>
        (list ?? App.Config.MapSpinCorrections).LastOrDefault(c => c.Body == body.RecordName && double.IsFinite(c.OffsetDeg));

    // 天体の使う自転の角度 (ラジアン。0〜2π) = データの式 + 補正値 (補正値が無ければデータの式のまま)。回らない天体は null。list は CorrectionOf と同じ
    public static double? ThetaAt(ShopTreeNode body, double unix, IReadOnlyList<MapSpinCorrection>? list = null)
    {
        if (DataThetaAt(body, unix) is not { } th) return null;
        if (CorrectionOf(body, list) is { } c) th += c.OffsetDeg * Math.PI / 180;
        th %= 2 * Math.PI;
        return th < 0 ? th + 2 * Math.PI : th;
    }

    // nodes の中の Planet・Moon の、時刻 unix の使う自転の角度 (回るものだけ)
    public static Dictionary<ShopTreeNode, double> ThetasAt(IEnumerable<ShopTreeNode> nodes, double unix)
    {
        var d = new Dictionary<ShopTreeNode, double>();
        var list = App.Config.MapSpinCorrections;   // 補正値のリストはこの時点のものを 1 回だけ読んで使う
        foreach (var n in nodes)
            if (n.Type is "Planet" or "Moon" && ThetaAt(n, unix, list) is { } th) d[n] = th;
        return d;
    }

    // 場所の位置 (θ を当てたもの)。自転の親と相対位置があれば「自転の親の位置 + Rz(θ)·相対位置」(θ が無ければ回さない)、無ければ x / y / z
    public static (double X, double Y, double Z) PlacePosition(ShopTreeNode n, IReadOnlyDictionary<ShopTreeNode, double> thetas)
    {
        if (n.SpinParent is { Position: not null } sp && n.Local is { } l)
        {
            var p = PlacePosition(sp, thetas);
            if (!thetas.TryGetValue(sp, out var th)) return (p.X + l.X, p.Y + l.Y, p.Z + l.Z);
            double c = Math.Cos(th), s = Math.Sin(th);
            return (p.X + c * l.X - s * l.Y, p.Y + s * l.X + c * l.Y, p.Z + l.Z);
        }
        return n.Position!.Value;
    }

    // 近くの場所 1 つ (Distance: 現在位置からの距離 (θ を当てた位置で)、SpinUnknown: 自転の親の rot_hours が読めていない)
    public sealed record NearRow(ShopTreeNode Node, double Distance, bool SpinUnknown);

    // 実測の基準の種類 (MapSpinCorrection.Ref の値): Log = Game.log の居場所、Near = 中心からの距離と z の差が 5 Km 以内の子
    public const string RefLog = "log", RefNear = "near";

    public sealed class Result
    {
        public required ShopTreeNode System { get; init; }
        // 近くの Planet・Moon (無ければ null)
        public ShopTreeNode? Planet { get; init; }
        // 描くのに使う天体ごとの θ (ラジアン。データの式 + 補正値)
        public required Dictionary<ShopTreeNode, double> Thetas { get; init; }
        // 今回実測した θ (ラジアン。fresh で実測できたときだけ) と、その基準の種類・場所
        public double? MeasuredTheta { get; init; }
        // 今回の実測で条件に合った地表の子の数と、その θ の広がり (度。互いの差のいちばん大きいもの)
        public int SurfaceCandidates { get; init; }
        public double SurfaceSpreadDegrees { get; init; }
        public string? Ref { get; init; }
        public ShopTreeNode? RefNode { get; init; }
        // 近い順に 10 か所と、いちばん近い場所までの距離 (場所が無ければ 0)
        public required List<NearRow> Near { get; init; }
        public double NearestDistance { get; init; }
    }

    // 惑星の近くとみなす距離: その惑星の子 (自転の親がその惑星) のいちばん遠いものの距離 × この倍
    private const double NearPlanetFactor = 1.5;
    // 近くの子で θ を実測するときの決め方: 中心からの距離と z の差が両方これ以内 (メートル)
    private const double MatchTolerance = 5000;
    // Game.log の居場所で θ を実測するときの、現在位置とその場所の距離の上限 (メートル。超えたら居場所が古いとみなして使わない)
    private const double LogPlaceMaxDistance = 50000;
    // 一覧の数
    private const int NearCount = 10;
    // 地表の子から θ を求めるとき、合う子どうしの θ の差がこれ (度) 以上なら、どれか決められないとみなして使わない
    private const double SurfaceThetaAgreeDegrees = 1;
    // 地表の場所とみなす高さ: 自転の親の中心からの距離が、天体の半径 + これ以内 (メートル)
    private const double SurfaceMargin = 50000;

    // 地表の場所か (自転の親の Planet・Moon の中心からの距離が、その半径 (planet_radius、無ければ radius) + 50 Km 以内)。半径が分からなければ false
    public static bool IsSurfacePlace(ShopTreeNode n) =>
        n.SpinParent is { SurfaceRadius: { } r } && r > 0 && n.Local is { } l && Len(l) <= r + SurfaceMargin;

    // 現在位置の近くの Planet・Moon (その子のいちばん遠いものの距離 × 1.5 以内、または半径 × 3 以内。いちばん近いもの) と中心からの距離。無ければ null
    public static (ShopTreeNode Body, double Distance)? NearBody(ShopTree tree, ShopTreeNode system, (double X, double Y, double Z) pos)
    {
        var places = PlacesOf(tree, system).ToList();
        (ShopTreeNode, double)? best = null;
        foreach (var b in places.Where(p => p.Type is "Planet" or "Moon"))
        {
            var far = places.Where(p => p.SpinParent == b && p.Local.HasValue).Select(p => Len(p.Local!.Value)).DefaultIfEmpty(0).Max();
            var reach = Math.Max(far * NearPlanetFactor, (b.SurfaceRadius ?? 0) * 3);
            var d = Dist(pos, b.Position!.Value);
            if (d <= reach && (best == null || d < best.Value.Item2)) best = (b, d);
        }
        return best;
    }

    // 地表からの高さの書き方「Pyro I 上空 12.3 Km」(高さ = 中心からの距離 − 半径。0 未満なら「Pyro I 地表付近」)。近くの天体が無い・半径が分からなければ null
    public static string? AltitudeText(ShopTree tree, ShopTreeNode system, (double X, double Y, double Z) pos)
    {
        if (NearBody(tree, system, pos) is not { } nb || nb.Body.SurfaceRadius is not { } r || !(r > 0)) return null;
        var alt = nb.Distance - r;
        return alt < 0 ? $"{nb.Body.Name} 地表付近" : $"{nb.Body.Name} 上空 {GameDataQueryService.FormatDistance(alt)}";
    }

    private static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    private static double Len((double X, double Y, double Z) v) => Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);

    private static IEnumerable<ShopTreeNode> PlacesOf(ShopTree tree, ShopTreeNode star) =>
        tree.Places.Where(p => p.Position.HasValue && p.Root == star);

    // 角度の差 (度) を -180〜180 に回し込む
    public static double Wrap180(double deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }

    // 現在位置 cur (時刻 unix。UNIX 秒) から求める。systemNames の星系だけを見る。星系が 1 つも無ければ null。
    // fresh なら (今取れた位置なら) 近くの惑星の θ を実測し、補正値 (実測 − データの式。-180°〜180°) を取得時刻とともに App.Config.MapSpinCorrections に入れる
    // (天体ごとに最新の 1 つ。rot_hours が 0・無い天体は補正しない)。logPlace は Game.log の今の居場所 (starmap_locations で引けたもの。無ければ null)。
    // 実測の基準: Game.log の居場所が近くの惑星の子 (自転の親が同じ) なら、その場所を θ で回したときの現在位置との距離が 50 Km 以内のときに使う。
    //   無ければ、中心からの距離と z の差が両方 5 Km 以内の子のうち、差 (√(距離の差² + z の差²)) のいちばん小さいもの。
    //   どちらも、その場所の中心からの距離 (軌道の半径。地表の場所なら地表の高さ) と現在位置の中心からの距離・z を比べて、範囲内のときだけ使う
    // system を渡したら (量子ジャンプの途中に取った座標。線の星系)、いちばん近い場所で星系を選ばず、その星系を使う
    // (Pyro と Nyx は恒星がどちらも原点で座標が重なるため)。その星系が systemNames に無い・座標のある場所が無ければ null (選ぶときに候補にならないのと同じ)
    public static Result? Locate(ShopTree tree, (double X, double Y, double Z) cur, double unix, IEnumerable<string> systemNames, ShopTreeNode? logPlace, bool fresh,
        ShopTreeNode? system = null)
    {
        if (system != null)
        {
            var given = system;
            if (!systemNames.Any(n => given.System.Equals(n, StringComparison.OrdinalIgnoreCase)) || !PlacesOf(tree, given).Any()) return null;
        }
        else
        {
            // 星系: いちばん近い場所 (θ = 0 の座標で) までの距離がいちばん短いもの
            var best = double.MaxValue;
            foreach (var name in systemNames)
            {
                var star = tree.Systems.FirstOrDefault(s => s.System.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (star == null) continue;
                foreach (var p in PlacesOf(tree, star))
                {
                    var d = Dist(cur, p.Position!.Value);
                    if (d < best) { best = d; system = star; }
                }
            }
            if (system == null) return null;
        }

        var places = PlacesOf(tree, system).ToList();

        // 近くの惑星: 中心から「子のいちばん遠いものの距離 × 1.5」以内の Planet・Moon のうち、いちばん近いもの
        var bodies = places.Where(p => p.Type is "Planet" or "Moon").ToList();
        var kidsOf = places.Where(p => p.SpinParent != null && p.Local.HasValue).GroupBy(p => p.SpinParent!).ToDictionary(g => g.Key, g => g.ToList());
        ShopTreeNode? planet = null;
        var pd = double.MaxValue;
        foreach (var b in bodies)
        {
            if (!kidsOf.TryGetValue(b, out var kids)) continue;
            var reach = kids.Max(k => Len(k.Local!.Value)) * NearPlanetFactor;
            var d = Dist(cur, b.Position!.Value);
            if (d <= reach && d < pd) { pd = d; planet = b; }
        }

        double? measured = null;
        string? refKind = null;
        ShopTreeNode? refNode = null;
        int surfaceCount = 0;
        double surfaceSpread = 0;
        if (fresh && planet != null && DataThetaAt(planet, unix) is { } dataTheta)
        {
            var pc = planet.Position!.Value;
            var r = Dist(cur, pc);
            var rxy = Math.Sqrt((cur.X - pc.X) * (cur.X - pc.X) + (cur.Y - pc.Y) * (cur.Y - pc.Y));
            var dzCur = cur.Z - pc.Z;
            double ThetaFrom((double X, double Y, double Z) l) => Math.Atan2(cur.Y - pc.Y, cur.X - pc.X) - Math.Atan2(l.Y, l.X);
            if (logPlace is { Local: { } ll } && logPlace.SpinParent == planet)
            {
                // その場所を θ で回して現在位置の方角に合わせたときの距離 (中心からの水平の距離の差と z の差)
                var lxy = Math.Sqrt(ll.X * ll.X + ll.Y * ll.Y);
                var gap = Math.Sqrt((rxy - lxy) * (rxy - lxy) + (dzCur - ll.Z) * (dzCur - ll.Z));
                if (gap <= LogPlaceMaxDistance) { measured = ThetaFrom(ll); refKind = RefLog; refNode = logPlace; }
            }
            if (measured == null && kidsOf.TryGetValue(planet, out var kids))
            {
                // 条件 (中心からの距離と z の差が両方 5 Km 以内) に合う子。地表より十分上の子は、いちばん差の小さいものを今どおり使う。
                // 地表の子 (IsSurfacePlace) は、合う子が複数あって、それぞれから求めた θ が互いに 1° 以上違う (どれか決められない) ときは使わない
                var cands = new List<(ShopTreeNode Node, double Diff, double Theta)>();
                foreach (var k in kids)
                {
                    var l = k.Local!.Value;
                    var dr = Math.Abs(r - Len(l));
                    var dz = Math.Abs(dzCur - l.Z);
                    if (dr > MatchTolerance || dz > MatchTolerance) continue;
                    cands.Add((k, Math.Sqrt(dr * dr + dz * dz), ThetaFrom(l)));
                }
                surfaceCount = cands.Count(c => IsSurfacePlace(c.Node));
                var high = cands.Where(c => !IsSurfacePlace(c.Node)).OrderBy(c => c.Diff).ToList();
                var surface = cands.Where(c => IsSurfacePlace(c.Node)).OrderBy(c => c.Diff).ToList();
                if (high.Count > 0) { refNode = high[0].Node; measured = high[0].Theta; refKind = RefNear; }
                else if (surface.Count > 0)
                {
                    // θ の広がり (互いの差のいちばん大きいもの。-180〜180° に回し込んで比べる)
                    for (int i = 0; i < surface.Count; i++)
                        for (int j = i + 1; j < surface.Count; j++)
                            surfaceSpread = Math.Max(surfaceSpread, Math.Abs(Wrap180((surface[i].Theta - surface[j].Theta) * 180 / Math.PI)));
                    if (surfaceSpread < SurfaceThetaAgreeDegrees) { refNode = surface[0].Node; measured = surface[0].Theta; refKind = RefNear; }
                }
            }
            if (measured is { } m)
            {
                var offset = Wrap180((m - dataTheta) * 180 / Math.PI);
                // 今のリストは書き換えず、新しいリストを作って丸ごと入れ替える (読んでいる途中のリストが変わらないように)
                var list = App.Config.MapSpinCorrections.Where(c => c.Body != planet.RecordName).ToList();
                // 入れ替える前の補正値を覚える (ステーションの地図は、今回の点で測った補正値 (点 = 基準の場所の中心とみなすもの) でなく、前の補正値を使う。
                // アプリを動かしている間だけ。測り直しそのものは変えない)
                if (App.Config.MapSpinCorrections.LastOrDefault(c => c.Body == planet.RecordName) is { } before)
                    lock (PreviousCorrections) PreviousCorrections[planet.RecordName] = before;
                list.Add(new MapSpinCorrection { Body = planet.RecordName, OffsetDeg = offset, Utc = unix, Ref = refKind!, RefName = refNode!.Name });
                App.Config.MapSpinCorrections = list;
            }
        }

        // 使う θ (データの式 + 補正値。今回実測したなら、その補正値が入っている)
        var thetas = ThetasAt(places, unix);

        // 近くの場所 (θ を当てた位置で近い順)
        var rows = places.Select(p =>
        {
            var pos = PlacePosition(p, thetas);
            var unknown = p.SpinParent != null && p.Local.HasValue && p.SpinParent.RotHours == null;
            return new NearRow(p, Dist(cur, pos), unknown);
        }).OrderBy(x => x.Distance).ThenBy(x => x.Node.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new Result
        {
            System = system,
            Planet = planet,
            Thetas = thetas,
            MeasuredTheta = measured is { } mm ? (mm % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) : null,
            SurfaceCandidates = surfaceCount,
            SurfaceSpreadDegrees = surfaceSpread,
            Ref = refKind,
            RefNode = refNode,
            Near = rows.Take(NearCount).ToList(),
            NearestDistance = rows.Count > 0 ? rows[0].Distance : 0,
        };
    }

    // ===== 推定位置 (Game.log から) =====

    // 現在位置の種類: Taken = ［現在位置］で取った座標、EstLanding = 推定 (Game.log の着陸場所)、EstArrived = 推定 (量子ジャンプの到着地点付近)、Moving = 量子ジャンプ中、
    // MovingUnknown = 量子ジャンプ中で目的地が星図で引けない (地表の地点・契約のビーコンなど)
    // EstMemory = 推定 (今の Location の番号で覚えた居場所。Memory が "inside" / "near" / "space")
    public enum PositionKind { Taken, EstLanding, EstArrived, Moving, MovingUnknown, EstMemory }

    // 使う現在位置。Pos: 位置 (Moving なら目的地の位置、MovingUnknown なら出発地の位置)、Unix: 自転の角度に使う時刻、Place: 着陸場所・目的地の場所 (Taken・MovingUnknown なら null)、
    // From: 量子ジャンプの出発地の位置、FromName: その名前、TargetDesc: MovingUnknown の目的地の書き方 (「目的地不明: 地表の地点」など)、
    // NoPos: 位置が無い (MovingUnknown で出発地も引けない。Pos は使わない)、
    // System: 星系の名前 (Taken で、量子ジャンプの途中に取った座標のとき。MapCurrentState.System。それ以外は null)
    public sealed record EffectivePosition(PositionKind Kind, (double X, double Y, double Z) Pos, double Unix, ShopTreeNode? Place, (double X, double Y, double Z)? From,
        string? FromName, string? TargetDesc = null, bool NoPos = false, string? Memory = null, string? System = null);

    // 使う現在位置を決める。logRunning が偽 (［ゲームログ］の追い読みが動いていない・Game.log が無い) なら推定はせず、取った座標 (古くても) だけ。
    // 追い読みが動いていれば次の順 (引けない名前の段は飛ばす)。どれでもなければ null (推定位置なし。古い取った座標は使わない):
    //   1. 取った座標 (今の Game.log の始まりより後に取り、その後に死亡・復活も Location の変化も無ければ、何分たっても。TakenInThisLog)
    //   2. (量子ジャンプは現在位置にしない。Quantum が線と状態の行を別に出す)
    //   3. 着陸場所 (landingRaw。StarMapObject.<名前>) の位置 (今の時刻の自転を入れる)
    //   4. 今の Location の番号 (q.LocationId) で覚えた居場所 (App.Config.MapLocationMemories): 場所の中ならその場所の位置、天体の付近・宇宙なら位置無し (EstMemory)
    //   5. 最後の到着が最後の目的地の選択より後で、その後に Location が変わっていなければ、目的地の位置
    // 取った座標に星系の名前 (taken.System。量子ジャンプの途中に取ったとき) があれば、EffectivePosition.System に入れる
    public static EffectivePosition? Effective(ShopTree tree, MapCurrentState taken, double nowUnix, bool logRunning, string? landingRaw,
        GameLogQuantumState? q, EffectivePosition? previous)
    {
        var takenPos = taken.Utc > 0 && double.IsFinite(taken.X) && double.IsFinite(taken.Y) && double.IsFinite(taken.Z)
            ? new EffectivePosition(PositionKind.Taken, (taken.X, taken.Y, taken.Z), taken.Utc, null, null, null,
                System: string.IsNullOrEmpty(taken.System) ? null : taken.System) : null;
        if (!logRunning) return takenPos;
        static double Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;
        if (takenPos != null && TakenInThisLog(taken, q) && (q?.LastLocationChange is not { } changedAt || Unix(changedAt) <= taken.Utc)) return takenPos;
        (double X, double Y, double Z) At(ShopTreeNode n) => PlacePosition(n, ThetasAt(tree.Places.Where(p => p.Root == n.Root), nowUnix));
        // 量子ジャンプ (設定・移動中) は現在位置とは別に出す (Quantum)。ここでは現在位置を決めない
        var target = q?.Target is { } tn ? tree.NodeByEntityName(tn) : null;
        if (!string.IsNullOrWhiteSpace(landingRaw) && tree.NodesByRecord.TryGetValue("Place:StarMapObject." + landingRaw.Trim(), out var landing) && landing.Position.HasValue)
            return new EffectivePosition(PositionKind.EstLanding, At(landing), nowUnix, landing, null, null);
        if (q?.LocationId is { Length: > 0 } locId
            && App.Config.MapLocationMemories.LastOrDefault(m => m.LocationId == locId) is { } mem
            && tree.NodeByStarmapRecord(mem.Record) is { } memNode)
        {
            switch (mem.Kind)
            {
                case "inside" when memNode.Position.HasValue:
                    return new EffectivePosition(PositionKind.EstMemory, At(memNode), nowUnix, memNode, null, null, Memory: "inside");
                case "near":
                    // 天体の付近: 位置は出さず (中心に印を置かない)、現在地の行だけ
                    return new EffectivePosition(PositionKind.EstMemory, (0, 0, 0), nowUnix, memNode, null, null, NoPos: true, Memory: "near");
                case "space":
                    return new EffectivePosition(PositionKind.EstMemory, (0, 0, 0), nowUnix, memNode, null, null, NoPos: true, Memory: "space");
            }
        }
        if (target is { Position: not null } && q is { ArrivedTime: { } arrived, TargetTime: { } selected } && arrived > selected
            && (q.LastLocationChange is not { } changed || changed <= arrived))
            return new EffectivePosition(PositionKind.EstArrived, At(target), nowUnix, target, null, null);
        return null;
    }

    // 取った座標を、今の Game.log の回の現在位置として使ってよいか: 今の Game.log の始まり (最初の行の時刻) より後に取り、
    // 最後の死亡・復活より後に取ったものだけ (ゲームを起動し直した後・死んだ後は使わない)。ログの始まりが分からなければ使わない
    public static bool TakenInThisLog(MapCurrentState taken, GameLogQuantumState? q)
    {
        static double Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;
        if (q?.LogStart is not { } start || taken.Utc < Unix(start)) return false;
        return q.LastDeathOrRespawn is not { } dr || taken.Utc >= Unix(dr);
    }

    // 量子ジャンプの状態: Line = 状態の行 (「QT 目的地 Orbituary: 設定」「…: 移動中」「QT 目的地 不明（地表の地点）: 設定」)、Target = 目的地 (引けなければ null)、
    // From / To = 線の出発地と目的地 (目的地が決まらない・出発地が決まらなければ null)、System = 線を描く星系 (恒星)、
    // Goal = 線の先が契約の目標 (推定。目的地が契約のビーコンで、目標の印から位置を求めた)
    // Dest = 目的地の位置 (線の出発地が無くても入れる。到着予定に使う。目的地が決まらなければ null)
    // FromSource = 線の出発地の出どころ (「取った座標（22:34:42）」「推定（着陸場所 Orbituary）」「推定（覚えた居場所）」「Game.log の出発地の名前（Monox）」)
    public sealed record QtState(string Line, ShopTreeNode? Target, (double X, double Y, double Z)? From, (double X, double Y, double Z)? To,
        ShopTreeNode? System = null, bool Goal = false, string? FromSource = null, (double X, double Y, double Z)? Dest = null);

    // 量子ジャンプの状態 (目的地を選んでから到着するまで。到着の後・まだ選んでいなければ null)。
    //   状態: 途中で停止 (q.Stopped)、移動中 (q.Moving) か設定 (選んだが、まだ跳んでいない)。名前は星図の英語名。
    //   目的地を選んだ後にハンガー進入か、着陸場所に着いた (q.LandingSince と landingRaw) ら、状態を消す (null)。
    //   目的地が引けなければ「不明（地表の地点 / 契約のビーコン）」で、線は無し。ただし目的地と組の契約の目標の印 (q.ObjectiveMarkers の NavPoint が目的地の名前。
    //   名前の形・時刻は問わない。Bounty・Refuel・救難などのビーコンも) があるか、目的地が MISSION_QT_Quantum_Beacon で、目的地を選んだ時刻より前の 10 分以内に契約の目標の印があれば、
    //   その目標の位置 (ObjectiveOf) を線の先にし、行は「QT 目的地 契約のビーコン（目標: Monox 地表付近）: 設定」。
    //   線の出発地: ［現在位置］で取った座標が、目的地の選択より新しいか、取った後に目的地の選択まで Location が変わっていない (q.LocationChangeAtSelection) ならその座標。
    //   ただし移動中か途中で停止で q.MovingSince があり、取った座標がそれより後に取ったもの (量子ジャンプの途中に取った座標) なら、取った座標は使わない。
    //   無ければ推定 (着陸場所 → 今の Location の番号で覚えた居場所 (場所の中) → Game.log の出発地の名前 (星図の英語名。星系の名前は引かない))。どれも無ければ線は無し
    public static QtState? Quantum(ShopTree tree, GameLogQuantumState? q, MapCurrentState taken, double nowUnix, string? landingRaw, IEnumerable<string>? systemNames = null)
    {
        if (q?.Target is not { } tn || q.TargetTime is not { } selected) return null;
        if (q.ArrivedTime is { } arrived && arrived >= selected) return null;
        // 目的地を選んだ後にハンガー進入か、着陸場所に着いたら、量子ジャンプの状態を消す
        if (q.LastHangarEnter is { } he && he > selected) return null;
        if (q.LandingSince is { } ls && ls > selected && !string.IsNullOrWhiteSpace(landingRaw)) return null;
        static double Unix(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds() / 1000.0;
        (double X, double Y, double Z) At(ShopTreeNode n) => PlacePosition(n, ThetasAt(tree.Places.Where(p => p.Root == n.Root), nowUnix));
        var status = QtStatus(q);
        var startNode = q.StartName is { } sn0 ? NodeByEnglishName(tree, sn0, placesOnly: true) : null;
        (double X, double Y, double Z)? from = null;
        string? fromSource = null;
        var fromTaken = false;
        var selUnix = Unix(selected);
        // 量子ジャンプの途中 (移動中・途中で停止。跳び始めた時刻 q.MovingSince より後) に取った座標は、線の出発地に使わない
        var takenWhileMoving = (q.Moving || q.Stopped) && q.MovingSince is { } movingSince && taken.Utc > Unix(movingSince);
        if (taken.Utc > 0 && double.IsFinite(taken.X) && double.IsFinite(taken.Y) && double.IsFinite(taken.Z) && TakenInThisLog(taken, q) && !takenWhileMoving
            && (taken.Utc >= selUnix || q.LocationChangeAtSelection is not { } changed || Unix(changed) <= taken.Utc))
        {
            from = (taken.X, taken.Y, taken.Z);
            fromTaken = true;
            fromSource = $"取った座標（{DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(taken.Utc * 1000)).LocalDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}）";
        }
        else if (!string.IsNullOrWhiteSpace(landingRaw) && tree.NodesByRecord.TryGetValue("Place:StarMapObject." + landingRaw.Trim(), out var landing) && landing.Position.HasValue)
        {
            from = At(landing);
            fromSource = $"推定（着陸場所 {landing.Name}）";
        }
        else if (q.LocationId is { Length: > 0 } locId && App.Config.MapLocationMemories.LastOrDefault(m => m.LocationId == locId) is { Kind: "inside" } mem
                 && tree.NodeByStarmapRecord(mem.Record) is { Position: not null } memNode)
        {
            from = At(memNode);
            fromSource = "推定（覚えた居場所）";
        }
        else if (startNode is { Position: not null })
        {
            from = At(startNode);
            fromSource = $"Game.log の出発地の名前（{q.StartName}）";
        }
        var target = tree.NodeByEntityName(tn);
        if (target is not { Position: not null })
        {
            // 選んだ目的地と組の印 (PairWithMissionNavpoint。名前の形は問わない。時刻も問わない。いちばん新しいもの)
            var paired = q.ObjectiveMarkers?.Where(o => o.NavPoint == tn).OrderByDescending(o => o.Time).FirstOrDefault();
            var quantumBeacon = tn.StartsWith("MISSION_QT_Quantum_Beacon", StringComparison.Ordinal);
            var beacon = paired != null || quantumBeacon;
            // 目標の印: 組の印、無ければ (MISSION_QT_Quantum_Beacon のときだけ) 選んだ時刻より前の 10 分以内のいちばん新しい印
            if ((paired ?? (quantumBeacon && q.ObjectiveMarkers is { Count: > 0 } markers
                    ? markers.Where(o => o.Time <= selected && selected - o.Time <= ObjectiveBeforeSelection).OrderByDescending(o => o.Time).FirstOrDefault() : null)) is { } marker)
            {
                // 目標の星系: 出発地の名前の場所の星系、出発地の名前が星系の名前 (「Stanton System」「Stanton 星系」「Stanton」) ならその星系、無ければ線の出発地の星系
                // (線の出発地が取った座標で、取ったときの星系の名前 (taken.System) があれば、その星系で決める)
                var takenSystem = fromTaken && !string.IsNullOrEmpty(taken.System)
                    ? tree.Systems.FirstOrDefault(s => s.System.Equals(taken.System, StringComparison.OrdinalIgnoreCase)) : null;
                var system = startNode?.Root ?? (q.StartName is { } sn1 ? SystemByStartName(tree, sn1) : null) ?? (from is { } fp && systemNames != null ? Locate(tree, fp, nowUnix, systemNames, null, fresh: false, takenSystem)?.System : null);
                if (system != null && ObjectiveOf(tree, marker, system, BodyOf(startNode), from, nowUnix) is { } goal)
                    return new QtState($"QT 目的地 契約のビーコン（目標: {goal.Where}）: {status}", null, from, from != null ? goal.Pos : null, system, Goal: true, FromSource: fromSource, Dest: goal.Pos);
            }
            var desc = tn.StartsWith("NavPoint_Dynamic", StringComparison.Ordinal) && q.TargetIsSurface ? "（地表の地点）"
                : beacon ? "（契約のビーコン）" : "";
            return new QtState($"QT 目的地 不明{desc}: {status}", null, null, null);
        }
        return new QtState($"QT 目的地 {target.Name}: {status}", target, from, from != null ? At(target) : null, target.Root, FromSource: fromSource, Dest: At(target));
    }

    // Game.log の出発地の名前 (Projected Start Location is <名前>) が星系の名前のときの星系 (恒星)。全ログの形は「Stanton System」「Stanton 星系」「Stanton星系」「Stanton」。
    // 末尾の「 System」「星系」を除いた名前が星系の名前 (大文字小文字は区別しない) と同じものだけ。無ければ null
    public static ShopTreeNode? SystemByStartName(ShopTree tree, string name)
    {
        var n = name.Trim();
        if (n.EndsWith(" System", StringComparison.OrdinalIgnoreCase)) n = n[..^" System".Length];
        else if (n.EndsWith("星系", StringComparison.Ordinal)) n = n[..^"星系".Length];
        n = n.Trim();
        return n.Length == 0 ? null : tree.Systems.FirstOrDefault(s => s.System.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    // 量子ジャンプの状態の言葉 (状態の行の最後の「: 」より後ろ): 途中で停止・移動中・設定
    // 2 区間の経路の区間 1 の終わり (q.Leg1EndedAt。目的地を選んだ後で、まだ移動中でない) は「区間 1 の終わり（もう一度 QT が必要）」
    public static string QtStatus(GameLogQuantumState q) => q.Stopped ? "途中で停止（もう一度 QT が必要）" : q.Moving ? "移動中"
        : q.Leg1EndedAt is { } le && q.TargetTime is { } tt && le >= tt ? "区間 1 の終わり（もう一度 QT が必要）" : "設定";

    // 契約の目標の印を、目的地を選んだ時刻より前のどれだけまで見るか
    private static readonly TimeSpan ObjectiveBeforeSelection = TimeSpan.FromMinutes(10);
    // 目標の天体を決めるとき、|ZonePos| と天体の半径の差の許し (半径に対する割合)
    private const double ObjectiveRadiusTolerance = 0.03;

    // 場所の天体 (その場所が Planet・Moon ならそれ、ほかは自転の親)。無ければ null
    private static ShopTreeNode? BodyOf(ShopTreeNode? n) => n == null ? null : n.Type is "Planet" or "Moon" ? n : n.SpinParent;

    // 契約の目標の位置: Pos = 絶対位置、Body = その天体 (宇宙の目標なら null)、Where = 書き方 (「Monox 地表付近」「Monox 上空 12 Km」「RAB-TUNG 付近」)
    public sealed record ObjectivePoint(GameLogObjectiveMarker Marker, ShopTreeNode? Body, (double X, double Y, double Z) Pos, string Where);

    // 目標の印 (ZonePos は、どの天体かは書かれていないが、天体の中心からの、自転といっしょに回る天体の枠での位置) の天体と絶対位置。
    //   天体: system の Planet・Moon のうち、|ZonePos| と半径 (planet_radius、無ければ radius) の差が半径の 3% 以内のもの。複数なら preferBody (量子ジャンプの出発地の天体)、
    //   次に refPos (現在位置・線の出発地) に近いもの。決まらない・その天体の枠の回転 (frame_matrix) が無ければ null。
    //   絶対位置 = 天体の位置 + Rz(θ)·(枠の回転·ZonePos) (場所の位置 (PlacePosition) と同じ順。θ はその天体の時刻 unix の自転の角度 (データの式 + 補正値))
    //   当てはまる天体が無く、|ZonePos| が恒星の半径より大きければ、宇宙の目標とみなし ZonePos を星系の座標 (恒星が原点) のまま使う
    //   (2026-10-22 の小惑星基地の契約で、ZonePos が星図の RAB-TUNG の位置と一致した)。書き方は SpaceObjectiveWhere
    //   それ以外 (恒星の半径以下) は、ラグランジュ点の区域の目標 (LagrangeObjective) として決まればその位置 (書き方は SpaceObjectiveWhere)
    public static ObjectivePoint? ObjectiveOf(ShopTree tree, GameLogObjectiveMarker m, ShopTreeNode system, ShopTreeNode? preferBody,
        (double X, double Y, double Z)? refPos, double unix)
    {
        var len = Math.Sqrt(m.X * m.X + m.Y * m.Y + m.Z * m.Z);
        var cands = PlacesOf(tree, system).Where(b => b.Type is "Planet" or "Moon" && b.SurfaceRadius is { } r && r > 0
            && Math.Abs(len - r) <= r * ObjectiveRadiusTolerance && b.Frame != null).ToList();
        if (cands.Count == 0)
        {
            if (system.Radius is { } starRadius && len > starRadius)
            {
                var sp = (m.X, m.Y, m.Z);
                return new ObjectivePoint(m, null, sp, SpaceObjectiveWhere(tree, system, sp, unix));
            }
            if (LagrangeObjective(tree, system, m, len, unix) is { } lp) return new ObjectivePoint(m, null, lp, SpaceObjectiveWhere(tree, system, lp, unix));
            return null;
        }
        var body = cands.Count == 1 ? cands[0]
            : preferBody != null && cands.Contains(preferBody) ? preferBody
            : refPos is { } rp ? cands.OrderBy(b => Dist(rp, b.Position!.Value)).First() : null;
        if (body == null) return null;
        var f = body.Frame!;
        double lx = f[0] * m.X + f[1] * m.Y + f[2] * m.Z, ly = f[3] * m.X + f[4] * m.Y + f[5] * m.Z, lz = f[6] * m.X + f[7] * m.Y + f[8] * m.Z;
        var th = ThetaAt(body, unix) ?? 0;
        double c = Math.Cos(th), s = Math.Sin(th);
        var bp = body.Position!.Value;
        var pos = (bp.X + c * lx - s * ly, bp.Y + s * lx + c * ly, bp.Z + lz);
        var alt = len - body.SurfaceRadius!.Value;
        var where = Math.Abs(alt) <= ObjectiveSurfaceBand ? $"{body.Name} 地表付近" : $"{body.Name} 上空 {GameDataQueryService.FormatDistance(alt)}";
        return new ObjectivePoint(m, body, pos, where);
    }

    // ラグランジュ点の区域の目標: ZonePos をラグランジュ点 (record 名が _L1〜_L5 で終わり、親が恒星の場所。例 StarMapObject.Stanton2_L4 = CRU L4) からの
    // 星系の向きのままの位置 (回さない) とみなし、ラグランジュ点の位置 + ZonePos が、ほかの場所 (惑星・衛星以外) から LagrangeObjectiveMatch 以内になる
    // ラグランジュ点が 1 つだけなら、その位置を返す。無い・2 つ以上なら null。|ZonePos| が LagrangeObjectiveMinLength 以下なら使わない (原点そのものが場所の近くで決まらないため)。
    // 根拠: 全ログで目標の文が「CRU-L4 Shallow Fields Station 付近…」などのサルベージの契約 58 回で、ラグランジュ点 + ZonePos がその文のステーション・クリニックなどから
    // 9.8〜19.9 Km になり、ほかのラグランジュ点では 100 Km 以内の場所が無かった (ステーション − ラグランジュ点 と ZonePos がほぼ同じ向き・長さ)
    private static (double X, double Y, double Z)? LagrangeObjective(ShopTree tree, ShopTreeNode system, GameLogObjectiveMarker m, double len, double unix)
    {
        if (!(len > LagrangeObjectiveMinLength)) return null;
        var places = PlacesOf(tree, system).Where(p => p.Position.HasValue).ToList();
        var thetas = ThetasAt(places, unix);
        var others = places.Where(p => p.Type is not ("Planet" or "Moon")).Select(p => (Node: p, Pos: PlacePosition(p, thetas))).ToList();
        (double X, double Y, double Z)? found = null;
        foreach (var l in places.Where(p => p.Parent == system && LagrangeRecordRegex.IsMatch(p.RecordName)))
        {
            var o = PlacePosition(l, thetas);
            var pos = (o.X + m.X, o.Y + m.Y, o.Z + m.Z);
            if (!others.Any(x => x.Node != l && Dist(pos, x.Pos) <= LagrangeObjectiveMatch)) continue;
            if (found != null) return null;
            found = pos;
        }
        return found;
    }
    private static readonly System.Text.RegularExpressions.Regex LagrangeRecordRegex = new(@"_L[1-5]$", System.Text.RegularExpressions.RegexOptions.Compiled);
    // ラグランジュ点 + ZonePos とほかの場所の距離の許し (実測 9.8〜19.9 Km)、|ZonePos| の下限
    private const double LagrangeObjectiveMatch = 50000, LagrangeObjectiveMinLength = 100000;

    // 宇宙の目標の書き方: いちばん近い場所 (その時刻の自転を入れた位置) の中の距離 (InsideRange) 以内なら「RAB-TUNG 付近」、
    // それより遠ければ「宇宙（RAB-TUNG から 1.2 Gm）」、場所が無ければ「宇宙」
    private static string SpaceObjectiveWhere(ShopTree tree, ShopTreeNode system, (double X, double Y, double Z) pos, double unix)
    {
        var places = PlacesOf(tree, system).ToList();
        var thetas = ThetasAt(places, unix);
        var near = places.Select(p => (Node: p, Distance: Dist(pos, PlacePosition(p, thetas)))).OrderBy(x => x.Distance).FirstOrDefault();
        if (near.Node == null) return "宇宙";
        return near.Distance <= InsideRange(near.Node) ? $"{near.Node.Name} 付近" : $"宇宙（{near.Node.Name} から {GameDataQueryService.FormatDistance(near.Distance)}）";
    }

    // 目標の書き方で「地表付近」と言う高さ (メートル)
    private const double ObjectiveSurfaceBand = 1000;
    // 地図に出す最近の目標の印: この時間以内、この数まで
    private static readonly TimeSpan RecentObjectiveAge = TimeSpan.FromMinutes(30);
    private const int RecentObjectiveMax = 5;

    // 地図に出す最近の目標 (30 分以内、新しい 5 つまで。天体が決まったものだけ)
    public static List<ObjectivePoint> RecentObjectives(ShopTree tree, GameLogQuantumState? q, ShopTreeNode system, ShopTreeNode? preferBody,
        (double X, double Y, double Z)? refPos, double nowUnix)
    {
        var list = new List<ObjectivePoint>();
        if (q?.ObjectiveMarkers is not { Count: > 0 } markers) return list;
        var now = DateTimeOffset.FromUnixTimeMilliseconds((long)(nowUnix * 1000)).UtcDateTime;
        foreach (var m in markers.Where(o => now - o.Time <= RecentObjectiveAge).OrderByDescending(o => o.Time))
        {
            if (ObjectiveOf(tree, m, system, preferBody, refPos, nowUnix) is { } p) list.Add(p);
            if (list.Count >= RecentObjectiveMax) break;
        }
        return list;
    }

    // 覚える「どこにいたか」(［現在位置］で座標を取ったとき。Location の番号と組にして覚える): 場所の中 (WhereaboutsOf の Inside) ならその場所 ("inside")、
    // そうでなく、ある Planet・Moon の付近 (その子のいちばん遠いものの距離 × 1.5 以内、または radius × 3 以内。いちばん近いもの) ならその天体 ("near")、
    // どちらでもなければ星系 (恒星) の宇宙 ("space")。星系が分からなければ null。
    // system を渡したら (量子ジャンプの途中に取った座標。線の星系) その星系で決める (Locate に渡す。中・付近・宇宙の判定はその星系の中で同じ)
    public static (string Record, string Kind)? Classify(ShopTree tree, (double X, double Y, double Z) pos, double unix, IEnumerable<string> systemNames, ShopTreeNode? logPlace,
        ShopTreeNode? system = null)
    {
        if (Locate(tree, pos, unix, systemNames, null, fresh: false, system) is not { } loc) return null;
        var w = WhereaboutsOf(pos, loc, logPlace);
        if (w.Inside && w.Place != null) return (w.Place.RecordName, "inside");
        return NearBody(tree, loc.System, pos) is { } nb ? (nb.Body.RecordName, "near") : (loc.System.RecordName, "space");
    }

    // 星図の英語名 (name_en) で場所を引く (大文字小文字は区別しない。座標のある場所で 1 つに決まるときだけ)。placesOnly なら星系 (恒星) は引かない
    public static ShopTreeNode? NodeByEnglishName(ShopTree tree, string name, bool placesOnly = false)
    {
        var hits = (placesOnly ? tree.Places : tree.Places.Concat(tree.Systems)).Where(n => n.Position.HasValue && string.Equals(n.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    // 現在地 (現在位置がどこか)。Inside: その場所の中にいる (中心までが max(radius × 2, 2,000 m) 以内)。FromLog: Game.log の居場所から決めた。
    // Place が null なら宇宙 (Nearest: いちばん近い場所とその距離)
    public sealed record Whereabouts(ShopTreeNode? Place, double Distance, bool Inside, bool FromLog, ShopTreeNode? Nearest, double NearestDistance);

    // 「その場所の中」とみなす距離の下限 (メートル)
    private const double InsideMinDistance = 2000;
    // 「その場所の近く」とみなす距離: Game.log の居場所は 50 Km、いちばん近い場所は 5 Km
    private const double LogNearDistance = 50000, NearestNearDistance = 5000;

    public static double InsideRange(ShopTreeNode n) => Math.Max((n.Radius ?? 0) * 2, InsideMinDistance);

    // 現在位置 pos の現在地: Game.log の居場所 (logPlace) が中の距離以内ならその中、無ければいちばん近い場所 (loc.Near の先頭) が中の距離以内ならその中
    // (いちばん近い場所が地表の場所 (IsSurfacePlace) なら、中とは言わず付近にする。地表のアウトポストは中心からの距離がほぼ同じで取り違えるため)。
    // どちらでもなければ、Game.log の居場所が 50 Km 以内ならその場所、いちばん近い場所が 5 Km 以内ならその付近、それも無ければ宇宙
    public static Whereabouts WhereaboutsOf((double X, double Y, double Z) pos, Result loc, ShopTreeNode? logPlace)
    {
        // 惑星・衛星そのものは場所として扱わない (中・付近の決め方から外し、地表からの高さで言う: AltitudeText)
        if (logPlace is { Type: "Planet" or "Moon" }) logPlace = null;
        double? logDist = logPlace is { Position: not null } && logPlace.Root == loc.System ? Dist(pos, PlacePosition(logPlace, loc.Thetas)) : null;
        var nearest = loc.Near.FirstOrDefault(r => r.Node.Type is not ("Planet" or "Moon"));
        if (logDist is { } ld && ld <= InsideRange(logPlace!)) return new Whereabouts(logPlace, ld, true, true, nearest?.Node, nearest?.Distance ?? 0);
        if (nearest != null && nearest.Distance <= InsideRange(nearest.Node))
            return new Whereabouts(nearest.Node, nearest.Distance, !IsSurfacePlace(nearest.Node), false, nearest.Node, nearest.Distance);
        if (logDist is { } ld2 && ld2 <= LogNearDistance) return new Whereabouts(logPlace, ld2, false, true, nearest?.Node, nearest?.Distance ?? 0);
        if (nearest != null && nearest.Distance <= NearestNearDistance) return new Whereabouts(nearest.Node, nearest.Distance, false, false, nearest.Node, nearest.Distance);
        return new Whereabouts(null, 0, false, false, nearest?.Node, nearest?.Distance ?? 0);
    }

    // ===== 量子ジャンプの到着予定 =====

    // 量子ドライブの性能: Name = ドライブの名前、Speed = driveSpeed (巡航の速さ m/秒)、Accel = stageOneAccelRate (加速度 m/秒²)
    public sealed record QtDrive(string Name, double Speed, double Accel);

    // 到着予定の結果: Eta = 到着予定 (UNIX 秒。出せなければ null)、Speed = 今の速さ (m/秒。進んだ点が 2 つ以上なら最新の 2 点の実測、1 つなら式の上の速さ)、
    // Remain = 最新の点から目的地までの距離 (m)、Basis = 根拠 (「Pontes [S3] の性能から」「実測から」など)、Reason = 出せない理由 (出せたら null)
    public sealed record QtEtaEstimate(double? Eta, double Speed, double Remain, string Basis, string? Reason);

    // 目的地の方向へこれ未満しか進んでいない点は「止まっている」とみなし、速さの計算から外す (m)
    private const double QtProgressMin = 1e6;
    // 目的地を選んだときの点 (出発の位置にする点) とみなす、選んだ時刻からの時間 (秒)。自動の取得は選んだらすぐ取り、取れなければ 3 秒おきにあと 2 回。
    // 実測では選んでから 5.9〜12.0 秒で取れ、選んでから 51 秒・111 秒の最初の点は量子ジャンプの途中だった
    private const double QtStartPointWindow = 20;
    // 実測の巡航の速さが driveSpeed とこの割合以上違えば、ドライブを付け替えたとみなして実測の速さで計算し直す
    private const double QtDriveMismatch = 0.05;
    // ドライブが分からないとき、後の 2 点の速さが式の上の速さ (a × 経過時間) のこの割合より遅ければ、最高速が頭打ちになったとみなす
    private const double QtPlateauRatio = 0.9;

    // 等加速 a で速さ v まで加速 → v で巡航 → 同じ加速度で対称に減速、の式。距離 d が足りなければ三角形の速さの形 (最高速 √(a·d))。
    // Total = 所要時間、RemainAt(s) = 出発から s 進んだところからの残り時間、SpeedAt(s) = そこでの速さ
    private static (double Total, Func<double, double> RemainAt, Func<double, double> SpeedAt) QtProfile(double d, double v, double a)
    {
        double sa = v * v / (2 * a), ta, peak, total;
        if (2 * sa <= d) { ta = v / a; peak = v; total = 2 * ta + (d - 2 * sa) / v; }
        else { ta = Math.Sqrt(d / a); peak = a * ta; sa = d / 2; total = 2 * ta; }
        double TimeAt(double s)
        {
            s = Math.Clamp(s, 0, d);
            return s <= sa ? Math.Sqrt(2 * s / a) : s <= d - sa ? ta + (s - sa) / peak : total - Math.Sqrt(2 * Math.Max(d - s, 0) / a);
        }
        double SpeedAtS(double s)
        {
            s = Math.Clamp(s, 0, d);
            return s <= sa ? Math.Sqrt(2 * a * s) : s <= d - sa ? peak : Math.Sqrt(2 * a * Math.Max(d - s, 0));
        }
        return (total, s => total - TimeAt(s), SpeedAtS);
    }

    // 量子ジャンプの到着予定。points = 目的地を選んだ後に取った点 (座標と UNIX 秒)、sinceUnix = 目的地を選んだ時刻 (2 区間の経路で区間 1 が終わっていれば、その時刻)、
    // dest = 目的地の位置、drive = 乗っている船の初期の量子ドライブの性能 (分からなければ null)。
    //   出発の位置: sinceUnix から QtStartPointWindow 秒以内に取った最初の点 (目的地を選んだときの点)。無ければ出発の位置は分からない。
    //   進んだ点: 出発の位置が分かれば、出発の位置から目的地の方向へ QtProgressMin 以上進んだ点。分からなければ、最初の点より目的地に QtProgressMin 以上近づいた点と最初の点。
    //   進んだ点が無ければ null (まだ跳んでいない)。
    //   ドライブが分かるとき: 式 (QtProfile。driveSpeed・stageOneAccelRate) で、最新の進んだ点の「出発から進んだ距離」から式の上の残り時間を出し、到着予定 = その点の時刻 + 残り時間。
    //     出発の位置が分からなければ、最新の点は巡航か減速の途中とみなし、目的地までの距離から残り時間を出す。
    //     式の上で巡航の間に取った続きの 2 点の実測の速さが driveSpeed と QtDriveMismatch 以上違えば、ドライブを付け替えたとみなし、その速さで計算し直す。
    //   ドライブが分からないとき: 出発の位置と進んだ点 2 つが要る。最初の 2 点から s = ½a(t − t0)² で加速度 a と開始時刻 t0 を出し、同じ加速度で対称に減速するとみなす。
    //     その後の点の速さが a × 経過時間 の QtPlateauRatio より遅ければ、最高速が頭打ちになったとみなし、実測の最も速い速さで巡航させる。到着予定 = t0 + 所要時間。
    //     出せなければ Eta が null で、Reason に理由
    //   筋の通らない結果は返さず、Eta を null にして Reason に理由を入れる (QtEtaCheck):
    //     有限の値でない、最新の点 (sinceUnix 以後に取った点のいちばん新しいもの。到着予定を出した時点) より前、
    //     または最新の点 + QtEtaLimitFactor × (目的地までの距離 Remain ÷ 基準の速さ + 基準の速さに達する時間) より先。
    //     基準の速さは、ドライブが分かればその driveSpeed (達する時間 = driveSpeed ÷ stageOneAccelRate)、分からなければ sinceUnix 以後の続きの 2 点の実測の最も速い速さ
    //     (達する時間 = その 2 点の真ん中の時刻 − sinceUnix。止まった状態から加速してその速さになったので、それより短い時間で減速できる)。
    //     「距離 ÷ 速さの 1/10」だけでは、大きな船 (driveSpeed 9.7〜12.6 億 m/s、加速度 2.1〜2.6 Mm/s²) の減速の途中の正しい予定も外すので、達する時間を足す
    public static QtEtaEstimate? EstimateQtEta(IReadOnlyList<((double X, double Y, double Z) Pos, double Unix)> points, double sinceUnix,
        (double X, double Y, double Z) dest, QtDrive? drive)
    {
        var r = EstimateQtEtaCore(points, sinceUnix, dest, drive);
        return r is { Eta: { } eta } && QtEtaCheck(points, sinceUnix, drive, eta, r.Remain) is { } why ? r with { Eta = null, Reason = why } : r;
    }

    // 到着予定の上限の倍数 (EstimateQtEta の説明。基準の速さの 1/10 で進む時間)
    private const double QtEtaLimitFactor = 10;

    // 到着予定 eta が筋の通らないものなら、その理由 (EstimateQtEta の説明)。筋が通れば null
    private static string? QtEtaCheck(IReadOnlyList<((double X, double Y, double Z) Pos, double Unix)> points, double sinceUnix, QtDrive? drive, double eta, double remain)
    {
        if (!double.IsFinite(eta)) return "計算の結果が有限の値になりません";
        var pts = points.Where(p => p.Unix >= sinceUnix).OrderBy(p => p.Unix).ToList();
        if (pts.Count == 0) return null;
        var latest = pts[^1].Unix;
        if (eta < latest) return "計算の結果が最新の点を取った時刻より前です";
        double vRef, reachTime;
        string basis;
        if (drive != null) { vRef = drive.Speed; reachTime = drive.Speed / drive.Accel; basis = "量子ドライブの速さ"; }
        else
        {
            vRef = 0;
            reachTime = 0;
            for (int i = 1; i < pts.Count; i++)
            {
                var dt = pts[i].Unix - pts[i - 1].Unix;
                if (!(dt > 0)) continue;
                var v = Dist(pts[i - 1].Pos, pts[i].Pos) / dt;
                if (v > vRef) { vRef = v; reachTime = Math.Max((pts[i - 1].Unix + pts[i].Unix) / 2 - sinceUnix, 0); }
            }
            basis = "実測の最も速い速さ";
        }
        if (!(vRef > 0) || !double.IsFinite(vRef)) return "速さの基準が無いので、計算の結果を確かめられません";
        var limit = latest + QtEtaLimitFactor * (remain / vRef + reachTime);
        return eta > limit ? $"計算の結果が、残りの距離を{basis}の 1/{QtEtaLimitFactor:0} で進む時間（加速・減速の分を含む）より先です" : null;
    }

    private static QtEtaEstimate? EstimateQtEtaCore(IReadOnlyList<((double X, double Y, double Z) Pos, double Unix)> points, double sinceUnix,
        (double X, double Y, double Z) dest, QtDrive? drive)
    {
        var pts = points.Where(p => p.Unix >= sinceUnix).OrderBy(p => p.Unix).ToList();
        if (pts.Count == 0) return null;
        (double X, double Y, double Z)? start = pts[0].Unix - sinceUnix <= QtStartPointWindow ? pts[0].Pos : null;
        var moving = new List<(double S, double R, double Unix, (double X, double Y, double Z) Pos)>();
        double d = 0;
        if (start is { } st)
        {
            d = Dist(st, dest);
            if (!(d > 0)) return null;
            double ux = (dest.X - st.X) / d, uy = (dest.Y - st.Y) / d, uz = (dest.Z - st.Z) / d;
            foreach (var p in pts)
            {
                var along = (p.Pos.X - st.X) * ux + (p.Pos.Y - st.Y) * uy + (p.Pos.Z - st.Z) * uz;
                if (along >= QtProgressMin) moving.Add((Math.Min(along, d), Dist(p.Pos, dest), p.Unix, p.Pos));
            }
        }
        else
        {
            var r0 = Dist(pts[0].Pos, dest);
            if (pts.Skip(1).Any(p => Dist(p.Pos, dest) <= r0 - QtProgressMin))
                foreach (var p in pts.Where((p, i) => i == 0 || Dist(p.Pos, dest) <= r0 - QtProgressMin))
                    moving.Add((double.NaN, Dist(p.Pos, dest), p.Unix, p.Pos));
        }
        if (moving.Count == 0) return null;
        var last = moving[^1];
        // 今の速さ: 進んだ点が 2 つ以上なら最新の 2 点の実測
        double? measured = moving.Count >= 2 && last.Unix > moving[^2].Unix ? Dist(moving[^2].Pos, last.Pos) / (last.Unix - moving[^2].Unix) : null;
        if (drive != null)
        {
            var v = drive.Speed;
            var a = drive.Accel;
            var basis = $"{drive.Name} の性能から";
            if (start != null)
            {
                // 式の上で巡航の間に取った続きの 2 点の実測の速さ (いちばん新しい組)
                var sa = v * v / (2 * a);
                for (int i = moving.Count - 1; i >= 1; i--)
                {
                    var (p1, p2) = (moving[i - 1], moving[i]);
                    if (p1.S < sa || p2.S > d - sa || p2.Unix <= p1.Unix) continue;
                    var vm = (p2.S - p1.S) / (p2.Unix - p1.Unix);
                    if (vm > 0 && Math.Abs(vm - v) / v >= QtDriveMismatch)
                    {
                        v = vm;
                        basis = $"実測の巡航の速さ {vm / 1e6:0.0} Mm/s から（{drive.Name} から付け替えたとみなす）";
                    }
                    break;
                }
                var prof = QtProfile(d, v, a);
                return new QtEtaEstimate(last.Unix + prof.RemainAt(last.S), measured ?? prof.SpeedAt(last.S), last.R, basis, null);
            }
            // 出発の位置が分からない: 巡航か減速の途中とみなし、目的地までの距離から残り時間
            var sDec = v * v / (2 * a);
            var remain = last.R >= sDec ? (last.R - sDec) / v + v / a : Math.Sqrt(2 * last.R / a);
            var speedNow = last.R >= sDec ? v : Math.Sqrt(2 * a * last.R);
            return new QtEtaEstimate(last.Unix + remain, measured ?? speedNow, last.R, basis + "（出発の位置が分からないので、巡航か減速の途中とみなす）", null);
        }
        if (start == null) return new QtEtaEstimate(null, measured ?? 0, last.R, "", "船の量子ドライブが分からず、出発の位置も分かりません");
        if (moving.Count < 2) return new QtEtaEstimate(null, 0, last.R, "", "船の量子ドライブが分からず、移動中の点が 2 つありません");
        var (q1, q2) = (moving[0], moving[1]);
        var k = q2.Unix > q1.Unix ? (Math.Sqrt(q2.S) - Math.Sqrt(q1.S)) / (q2.Unix - q1.Unix) : 0;
        if (!(k > 0)) return new QtEtaEstimate(null, measured ?? 0, last.R, "", "船の量子ドライブが分からず、加速中の 2 点から加速度を出せません");
        var accel = 2 * k * k;
        var t0 = q1.Unix - Math.Sqrt(q1.S) / k;
        // 頭打ち: その後の続きの 2 点の速さが式の上の速さ a × 経過時間 の QtPlateauRatio より遅ければ、実測の最も速い速さで巡航
        var vmax = double.PositiveInfinity;
        double fastest = 0;
        var plateau = false;
        for (int i = 2; i < moving.Count; i++)
        {
            var (p1, p2) = (moving[i - 1], moving[i]);
            if (p2.Unix <= p1.Unix) continue;
            var vm = (p2.S - p1.S) / (p2.Unix - p1.Unix);
            fastest = Math.Max(fastest, vm);
            if (vm < QtPlateauRatio * accel * ((p1.Unix + p2.Unix) / 2 - t0)) plateau = true;
        }
        if (plateau && fastest > 0) vmax = fastest;
        var total = double.IsInfinity(vmax) ? 2 * Math.Sqrt(d / accel) : QtProfile(d, vmax, accel).Total;
        return new QtEtaEstimate(t0 + total, measured ?? 0, last.R, $"実測から（加速度 {accel / 1e6:0.00} Mm/s²{(plateau ? $"、最高速 {vmax / 1e6:0.0} Mm/s" : "")}）", null);
    }
}
