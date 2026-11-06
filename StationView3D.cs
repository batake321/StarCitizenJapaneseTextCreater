using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StarCitizenJapaneseTextCreater;

// ステーションの地図の新しい方式 (2026-11-04、利用者の指示「道案内をやめ、場所に今のような丸を作る方式なら、全部を 3D で出せば今すぐできる」)。
// 試験の頁 (scratchpad の orison_perf\view\test8.html) とデータ作り (view_build6.py・fuse.py・view_build_lib.py) の移植。
//   部品 (socpak の GLB の子のメッシュ) を全部そのまま 3D で描く。床の判定は「判定 2」(1 辺を共有し同じ平面の三角形をまとめた形の傾き:
//   10° 以内は床・46° 以内は坂。名前に stair のある部品はまるごと階段 (坂)。名前に ceiling・wall のある部品は床・坂にしない。
//   名前に counter のある部品の歩ける面はカウンター。unit_whammers_facade_interior_b は部品の床から 1 m 未満の上向きの面だけカウンター。
//   light_attachment は除外。筒の底 (同じ部品の真上の下向きの面まで 2 m 以上ある上向きの面) は床)。
//   (2026-11-04、利用者の決定) 除外の決まり (light・1 m 未満・ceiling・wall) の掛かる部品でも、上面の高さがまわりの床とほぼ同じものは除外しない (TopLevelParts)。
//   店の入口: 名前の語に door・doors・doorway・window・opening のある部品で、店の中心から 10 m 以内のもののうち、
//   店の中心の真下の床 + 1 m の高さの、店の中心から開口部への水平な直線が、どの三角形にも当たらないもの。
//
// 作り (2026-11-04、利用者の決定「Data.p4k から見る所の区域だけを、開いたときにその場で取り出して描く（前もってデータを作らない）」):
//   窓を開くと Session を作り (場所の木・地図の JSON・訳)、station.json (案内するもの・乗り場・前の地図の階の名前) を一時の置き場に書く。
//   行き先を選ぶと、まわり ±R m に掛かる区画 (socpak) だけをその場で取り出し (アプリの中の読み取り OwnExtractor)、判定を計算して region.json と g\<番号>.bin を書く。
//   ビューアが読み終えたら消す (StationView3DWindow)。保存して使い回すことはしない。
//   g\<番号>.bin: socpak 1 つ分 (部品の形 (socpak の枠の座標) と、地点のインスタンスごとの判定の色・まとめた形の輪郭)
internal static class StationView3D
{
    public const string Format = "scjt-view3d-1";
    private const string GeoMagic = "SCJT3DG2";

    // ===== 値 (試験の頁・view_build6.py と同じ) =====
    private static readonly double CosFlat = Math.Cos(10 * Math.PI / 180), CosWalk = Math.Cos(46 * Math.PI / 180);
    private const double TubeH = 2.0;            // 筒の底: 真上の同じ部品の下向きの面まで 2 m 以上
    private const double EntDist = 10.0;         // 店の入口: 店の中心から 10 m 以内の開口部
    private const int EntHit = 1;                // 直線が三角形に 1 つでも当たれば入口にしない
    private const double EntNode = 1.0;          // 入口の部品の箱から 1 m 以内の道の点
    private static readonly string[] EntWords = ["door", "doors", "doorway", "window", "opening"];
    private const double NTol = 1e-4, DTol = 1e-4;   // fuse.py の同じ平面の許し

    // 判定の色 (三角形ごとの 1 バイト)
    public const byte LabNone = 0, LabFloor = 1, LabSlope = 2, LabTube = 3, LabCounter = 4;
    private static readonly string[] LabNames = ["", "floor2", "slope2", "tube2", "counter2"];

    // ===== 取り出し (差し替え口) =====
    // 区画 (socpak) 1 つを渡すと、部品ごとの三角形が返る。アプリの中の読み取り (OwnExtractor) で取り出す
    // (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」(案 A)。前は StarBreaker で GLB に書き出して読んでいた)
    // SourcePart: 部品 1 つ (GLB の子のメッシュ)。Name はメッシュの名前、Mesh はメッシュの三角形 (float32 x9。同じメッシュの部品で共有してよい)、M は socpak の枠へのノードの行列 (4×4、行優先)
    // CgfPath: 部品の種類 (補う面の表の鍵。無ければ ""）。SizeRef: 大きさ (Small) の判定に使う形 (補う面の追加の部品が、元の部品の形で判定するため。null なら Mesh)。
    // Uv: 三角形の角ごとの UV (float32 x6。Mesh と同じ並び。形に UV が無ければ null)、TexKeys: この部品が使う画像の鍵 (StationView3DTextures.TexInfo.Key)、
    // Tex: 三角形ごとの TexKeys の番号 (画像の無い三角形は -1。画像を貼らない部品は null) (2026-11-05、利用者の指示「引いたときは色はビットマップ貼り付けにしてほしい」)
    public sealed record SourcePart(string Name, float[] Mesh, double[] M, string CgfPath = "", float[]? SizeRef = null, float[]? Uv = null, string[]? TexKeys = null, int[]? Tex = null);
    public delegate Task<IReadOnlyList<SourcePart>?> Extractor(string socpak, CancellationToken ct);

    // (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」(案 A)) アプリの中の読み取り (SocpakExporter) で取り出す。
    // starbreaker で GLB に書き出して ReadGlb で読んだときと同じもの: socpak を PATTERN として渡したときに一致する socpak を同じ並びで読み、
    // GLB を ReadGlb で読んだときと同じ部品の名前・三角形・行列にする (行列は GLB の数 (SocpakScene の Matrix) を ReadGlb と同じ順で掛ける)。
    // Data.p4k の目次・DataCore・部品の形は、アプリの中で使い回す (SocpakExporter.Shared。同じ部品の形は 1 回だけ読む。上限 SocpakExporter.MeshCacheBytes)
    // texMaxPx・texFolder: ゲームの画像を貼る (StationView3DTextures。長い辺の上限 px と PNG の置き場。texMaxPx が 0 か texFolder が null なら画像なし = 単色)
    // palette: false なら場所のパレットの色を当てない (画像の色合いはパレットが無いときと同じ決め方。2026-11-05、利用者の指示「1 で。右のスイッチに入れて自動保存にして」)。
    //   材質の差し替え (looks の look_mtl) は false でも当てる (描く形と歩くモードの当たり・床の補いの形をそろえるため。親の決定)
    public static Extractor OwnExtractor(string p4kPath, Action<string>? log = null, int texMaxPx = 0, string? texFolder = null, bool palette = true) => (socpak, ct) => Task.Factory.StartNew(() =>
    {
        try
        {
            var ctx = SocpakExporter.Shared(p4kPath, 1);
            var pattern = socpak.Replace('/', '\\');
            var list = MatchSocpaksCached(ctx.P4k, pattern);
            if (list.Count == 0) { log?.Invoke($"取り出しが失敗しました ({socpak}): no .socpak files matching '{pattern}'"); return (IReadOnlyList<SourcePart>?)null; }
            // 場所のパレットの色と材質の差し替え (2026-11-05): 前もって view3d_layout.db の look_* 表に入れたものを引いて渡す (LooksOf)
            var scene = SocpakExporter.Export(ctx, list, ct: ct, looks: LooksOf(Path.Combine(StationPlaces.Folder, LayoutDbName), p4kPath));
            TexResolver? tex = texMaxPx > 0 && texFolder != null ? (mtl, mat, pal, image) => StationView3DTextures.Resolve(ctx.P4k, texFolder, mtl, mat, texMaxPx, palette ? pal : null, image) : null;
            // 粗い形で抜けた床の面を補う (StationFill)。歩くモードの裏の補い (view3d_fill_walk.db) のうち、形の印が今と同じ行も足す
            var parts = StationFill.Augment(PartsOf(scene, tex), StationFill.Table(Path.Combine(StationPlaces.Folder, LayoutDbName), StationFill.WalkDbPath, ctx.P4k));
            GravityCache[socpak] = GravityOf(scene);
            return parts.Count > 0 ? parts : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException)
        {
            log?.Invoke($"取り出しが失敗しました ({socpak}): {e.Message}");
            return null;
        }
    }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    // SocpakExporter.MatchSocpaks と同じ結果 (目次の順に、ファイル名が .socpak で終わり、小文字のパスにパターンの小文字を含むもの) を、
    // socpak の名前の一覧 (Data.p4k ごとに 1 回作る) から引く (2026-11-04、親の指示「取り出しの内訳」: 目次 138 万件を区画ごとに見直すのに 0.02〜0.09 秒かかっていた)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<P4kArchive, List<(string Low, string Name)>> SocpakNames = new();
    internal static List<string> MatchSocpaksCached(P4kArchive p4k, string pattern)
    {
        var names = SocpakNames.GetValue(p4k, a =>
        {
            var l = new List<(string, string)>();
            for (int i = 0; i < a.Count; i++)
                if (a.FileName(i).EndsWith(".socpak", StringComparison.Ordinal)) { var nm = a.Name(i); l.Add((nm.ToLowerInvariant(), nm)); }
            return l;
        });
        var low = pattern.ToLowerInvariant();
        return names.Where(x => x.Low.Contains(low, StringComparison.Ordinal)).Select(x => x.Name).ToList();
    }

    // ===== 重力の範囲 (2026-11-04、利用者の決定「床から 身長程度以内は重力ありで案 1 でいいんじゃない？」「但し床の上だけ」) =====
    // 区画の .soc の GravityBox・GravityArea (SocpakScene.Container.Gravity。船の担当が読む) を、socpak の枠の範囲にして覚える (取り出したとき。区画ごと)。
    //   Kind: zero = 無重力 (gravityMagnitude が 0)、up = 重力が上向き (向き (場所の枠) の z ≥ 0.5)、down = ふつうの重力 (それ以外。無重力の箱の中の重力のある通路のため)
    //   E: 範囲の枠 → socpak の枠 の行列 (4×4 行優先。Container.Matrix × Pos × Rot)。箱は E の原点を中心に Size (辺の長さ。中心か角かは未確認で、中心として扱う推測)。
    //   柱は Poly (E の枠の xy) を底に、E の +z へ Height。欄が引けない (null) 範囲は使わない
    public sealed record GravVol(string Kind, double[] E, double[]? Size, List<double[]>? Poly, double Height, string Name);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<GravVol>> GravityCache = new(StringComparer.Ordinal);
    internal static List<GravVol> GravityOf(SocpakScene scene)
    {
        var res = new List<GravVol>();
        static double[] LMat(double[] m) { var M = new double[16]; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) M[r * 4 + c] = m[c * 4 + r]; return M; }
        static double[] Mul(double[] a, double[] b)
        {
            var c = new double[16];
            for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { double s = 0; for (int j = 0; j < 4; j++) s += a[r * 4 + j] * b[j * 4 + k]; c[r * 4 + k] = s; }
            return c;
        }
        foreach (var c in scene.Containers)
        {
            var C = LMat(c.Matrix);
            foreach (var g in c.Gravity)
            {
                if (g.Magnitude is not { } mag || g.DirectionWorld is not { Length: 3 } dw || g.Pos is not { Length: 3 } pos || g.Rot is not { Length: 4 } q) continue;
                if (g.Active == false) continue;
                double w = q[0], x = q[1], y = q[2], z = q[3];
                double[] RT =
                [
                    1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y), pos[0],
                    2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x), pos[1],
                    2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y), pos[2],
                    0, 0, 0, 1,
                ];
                var E = Mul(C, RT);
                // 向きを socpak の枠に (Container の回転を掛ける)
                var dz = C[8] * dw[0] + C[9] * dw[1] + C[10] * dw[2];
                var kind = mag == 0 ? "zero" : dz >= 0.5 ? "up" : "down";
                if (g.Class == "GravityBox" && g.Size is { Length: 3 } size) res.Add(new GravVol(kind, E, size, null, 0, g.Name));
                else if (g.Class == "GravityArea" && g.Polygon is { Count: >= 3 } poly && g.Height is { } h) res.Add(new GravVol(kind, E, null, poly.Select(p => new[] { p[0], p[1] }).ToList(), h, g.Name));
            }
        }
        return res;
    }

    // 画像の引き当て: (材質の .mtl のパス, 材質の番号, 置き方のパレット (無ければ null), 画像を使うか (形に UV があるか)) → 画像 (StationView3DTextures.Resolve)
    public delegate StationView3DTextures.TexInfo? TexResolver(string mtl, int matId, SocpakScene.Tint? pal, bool image);
    // (形の参照, パレットの鍵) の比べ (形は参照で比べる)
    private sealed class MeshPalComparer : IEqualityComparer<(SocpakScene.Mesh, string)>
    {
        public bool Equals((SocpakScene.Mesh, string) a, (SocpakScene.Mesh, string) b) => ReferenceEquals(a.Item1, b.Item1) && string.Equals(a.Item2, b.Item2, StringComparison.Ordinal);
        public int GetHashCode((SocpakScene.Mesh, string) k) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Item1), StringComparer.Ordinal.GetHashCode(k.Item2));
    }

    // SocpakScene → 部品の並び (ReadGlb と同じ: 区画の子の順、三角形は primitive ごとに索引 3 つずつ、同じ形は同じ配列)。
    // tex: 材質 (.mtl のパス, 材質の番号) → 画像 (null なら画像も UV も付けない = 画像なしのときの出来るものは前と同じ)。画像の戻しは、場面に出てくる材質ごとに 1 回、並べて行う
    // (2026-11-05) 置き方に場所のパレット (Placement.Palette) があれば、材質・パレットの組ごとに引く (同じ形でもパレットが違えば画像の鍵の並びは別。三角形・UV の配列は形ごとに共有)。
    //   UV の無い形でも、画像なしの色だけの項目を引く (パレットの色を指す材質とガラスだけ。ほかは今までどおり画像なし。ガラスは 2026-11-05 から)
    public static List<SourcePart> PartsOf(SocpakScene scene, TexResolver? tex = null)
    {
        var res = new List<SourcePart>();
        var geom = new Dictionary<SocpakScene.Mesh, (float[] T, float[]? Uv)>(ReferenceEqualityComparer.Instance);
        var tris = new Dictionary<(SocpakScene.Mesh, string), (string[]? Keys, int[]? Tex)>(new MeshPalComparer());
        // 画像: 出てくる材質 (mtl, 番号, パレット, 画像を使うか) を先に集めて並べて戻す (StationView3DTextures.Resolve は 1 回だけ戻して覚える)
        var texOf = new Dictionary<(string, int, string, bool), StationView3DTextures.TexInfo?>();
        if (tex != null)
        {
            var pairs = new Dictionary<(string, int, string, bool), SocpakScene.Tint?>();
            foreach (var c in scene.Containers) foreach (var pl in c.Placements)
                if (pl.Mesh.Mtl.Length > 0) foreach (var mid in pl.Mesh.SubMaterials) pairs.TryAdd((pl.Mesh.Mtl, mid, pl.Palette?.Key ?? "", pl.Mesh.Uvs != null), pl.Palette);
            var arr = pairs.ToArray(); var got = new StationView3DTextures.TexInfo?[arr.Length];
            Parallel.For(0, arr.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, i => { got[i] = tex(arr[i].Key.Item1, arr[i].Key.Item2, arr[i].Value, arr[i].Key.Item4); });
            for (int i = 0; i < arr.Length; i++) texOf[arr[i].Key] = got[i];
        }
        double[] I = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        // 列優先 (GLB の matrix) → 行優先
        static double[] LMat(double[] m) { var M = new double[16]; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) M[r * 4 + c] = m[c * 4 + r]; return M; }
        static double[] Mul(double[] a, double[] b)
        {
            var c = new double[16];
            for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { double s = 0; for (int j = 0; j < 4; j++) s += a[r * 4 + j] * b[j * 4 + k]; c[r * 4 + k] = s; }
            return c;
        }
        foreach (var c in scene.Containers)
        {
            var M = Mul(I, LMat(c.Matrix));
            foreach (var pl in c.Placements)
            {
                var M2 = Mul(M, LMat(pl.Matrix));
                if (!geom.TryGetValue(pl.Mesh, out var G))
                {
                    var outl = new List<float>();
                    var P = pl.Mesh.Positions; var U = tex != null ? pl.Mesh.Uvs : null;
                    var uvl = U != null ? new List<float>() : null;
                    for (int pi = 0; pi < pl.Mesh.Primitives.Count; pi++)
                    {
                        var ix = pl.Mesh.Primitives[pi];
                        int nt = ix.Length / 3 * 3;
                        for (int i = 0; i < nt; i++)
                        {
                            long v = ix[i]; outl.Add(P[v * 3]); outl.Add(P[v * 3 + 1]); outl.Add(P[v * 3 + 2]);
                            if (uvl != null) { uvl.Add(U![v * 2]); uvl.Add(U[v * 2 + 1]); }
                        }
                    }
                    geom[pl.Mesh] = G = (outl.ToArray(), uvl?.ToArray());
                }
                var palKey = pl.Palette?.Key ?? "";
                if (!tris.TryGetValue((pl.Mesh, palKey), out var T))
                {
                    // 画像: primitive (submesh) ごとの材質の番号 → 画像の鍵 (この形で使う鍵の一覧の番号)。無い三角形は -1
                    var keys = new List<string>(); var keyIx = new Dictionary<string, int>(StringComparer.Ordinal);
                    var hasUv = pl.Mesh.Uvs != null;
                    var texl = tex != null ? new List<int>() : null;
                    if (texl != null)
                        for (int pi = 0; pi < pl.Mesh.Primitives.Count; pi++)
                        {
                            int nt = pl.Mesh.Primitives[pi].Length / 3 * 3;
                            int ti = -1;
                            if (pi < pl.Mesh.SubMaterials.Count && pl.Mesh.Mtl.Length > 0 && texOf.TryGetValue((pl.Mesh.Mtl, pl.Mesh.SubMaterials[pi], palKey, hasUv), out var info) && info != null)
                            {
                                if (!keyIx.TryGetValue(info.Key, out ti)) { ti = keys.Count; keys.Add(info.Key); keyIx[info.Key] = ti; }
                            }
                            for (int i = 0; i < nt / 3; i++) texl.Add(ti);
                        }
                    tris[(pl.Mesh, palKey)] = T = (keys.Count > 0 ? keys.ToArray() : null, keys.Count > 0 ? texl!.ToArray() : null);
                }
                if (G.T.Length > 0 && M2.All(double.IsFinite)) res.Add(new SourcePart(pl.Mesh.Name, G.T, M2, pl.Mesh.CgfPath, null, G.Uv, T.Keys, T.Tex));
            }
        }
        return res;
    }

    public sealed class Stats
    {
        public int Instances, Socpaks, Missing, Parts;
        public long Triangles, Bytes;
        public double ExportSeconds, ComputeSeconds, BuildSeconds;
        // 取り出したものの使い回し: 前の地点のものを使った数・取り出した数・捨てた後に取り出し直した数・上限で捨てた数・上限に当たったか・持っている大きさ
        public int CacheHits, Extracted, ReExtracted, Evicted; public bool CapHit; public long CacheBytes;
        // 置かれた所ごとの判定: 計算した数・使い回した数
        public int InstComputed, InstReused;
        // 部品の箱が地点に掛からず、形を作らなかった区画の数・判定を計算しなかった置かれた所の数
        public int SkippedNoHit, InstNoHit;
        // 区画ごとの時間 (socpak, 取り出し, 形を作る, 判定と書く, 始めてから終わるまで (地点の始めから), 三角形)。遅いものを調べるため
        public List<(string Socpak, double Extract, double Geo, double Calc, double EndAt, long Tris)> PerSocpak = new();   // 取り出しの時間の足し合わせ・判定の計算と書く時間の足し合わせ・全体の時間
    }

    // 地図の JSON から使うもの (StationMapJson が書いたもの)
    private sealed record Transit(string Kind, double[] Pos, string Floor, string Line, string Stop);
    private sealed record Stair(double[] Bottom, double[] Top, string From, string To, string Source);
    private sealed record OldFloor(string Id, string Name, double Z);
    private sealed record Shop(double[] Pos, string Name, string Guid, string Floor);
    private sealed record Terminal(string Kind, double[] Pos, string Entity, string Class, double? Yaw, string ShopEntity, string ShopGuid, string Floor);

    // 訳 (アプリのほかの所と同じ順: ゲームのフォルダの japanese_(japan)\global.ini → translations.db の日本語 → 英語 → 鍵のまま。
    // DB の引き方は GameDataQueryService.LookupLoc と同じ (鍵か「鍵,P」、鍵そのものを先))
    public sealed class Loc
    {
        private readonly Dictionary<string, string> _ja = new(StringComparer.Ordinal);
        private readonly string? _db;
        private readonly Dictionary<string, string> _memo = new(StringComparer.Ordinal);
        public Loc(string? gameJapaneseIni, string? translationsDb)
        {
            try
            {
                if (gameJapaneseIni != null && File.Exists(gameJapaneseIni))
                    foreach (var ln in File.ReadLines(gameJapaneseIni, new UTF8Encoding(false)))
                    {
                        var l = ln.TrimStart('﻿');
                        var eq = l.IndexOf('=');
                        if (eq < 0) continue;
                        _ja[l[..eq].Trim()] = l[(eq + 1)..];
                    }
            }
            catch (IOException) { }
            _db = translationsDb != null && File.Exists(translationsDb) ? translationsDb : null;
        }
        public bool InJa(string k) => _ja.ContainsKey(k);
        // 覚え (_memo) は複数の糸から引く (StationJson と、裏で作る外から見たときの印 ExteriorMarks) ので、鍵を引く間はロックする
        public string Get(string key) { lock (_memo) return GetCore(key); }
        private string GetCore(string key)
        {
            if (string.IsNullOrEmpty(key)) return key;
            if (_memo.TryGetValue(key, out var m)) return m;
            var k = key.StartsWith('@') ? key[1..] : key;
            string res = key;
            foreach (var kk in new[] { k, k + ",P" })
                if (_ja.TryGetValue(kk, out var v) && v.Trim().Length > 0) { res = v; goto done; }
            if (_db != null)
            {
                try
                {
                    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_db};Mode=ReadOnly");
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT japanese, english FROM translations WHERE key = @k OR key = @kp ORDER BY (key = @k) DESC LIMIT 1";
                    cmd.Parameters.AddWithValue("@k", k);
                    cmd.Parameters.AddWithValue("@kp", k + ",P");
                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        var ja = r.IsDBNull(0) ? null : r.GetString(0);
                        var en = r.IsDBNull(1) ? null : r.GetString(1);
                        if (!string.IsNullOrWhiteSpace(ja)) res = ja;
                        else if (!string.IsNullOrWhiteSpace(en)) res = en;
                    }
                }
                catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException) { }
            }
        done:
            _memo[key] = res;
            return res;
        }
        // 「鍵 付近」「鍵 / 鍵」の形の名前は、鍵の所だけ訳す (view_build6.py の loc_name)
        private static readonly Regex KeyRx = new(@"@?[A-Za-z][A-Za-z0-9_]*_[A-Za-z0-9_]+", RegexOptions.Compiled);
        public string Name(string s) => KeyRx.Replace(s, m => InJa(m.Value.TrimStart('@')) || m.Value.StartsWith('@') ? Get(m.Value) : m.Value);
    }

    // ===== 区画の置き方の一覧 (2026-11-04、利用者の指示「ファイル構造を調べておいて、表示する場所に合わせて展開する形にすればよい」) =====
    // 前もって作るのは、どの socpak がどこに (行列・箱) 置かれているかの一覧だけ (三角形は入れない)。地図の取り込みのときに地図の JSON の横に <短い名前>.layout.json として書く。
    // LayoutInst: インスタンスの番号・socpak・行列 (3 行 4 列、行優先: 回転の列 ex・ey・ez と位置)・箱 (socpak の頭の箱 (StationInstance.MinBounds・MaxBounds) の 8 隅を場所の座標にした箱。無ければ null)
    public sealed record LayoutInst(int Index, string Socpak, double[] A, double[]? Lo, double[]? Hi);
    // ハンガーの名前 (@ui_interactor_hangar_small など) の訳。訳は「%ls's\nSmall Hangar」の形 (%ls はプレイヤーの名前) なので、%ls のある行を外す
    // (アプリの地図の StationMapPanel.HangarName と同じ外し方。引く順は Loc の順)。鍵が空なら ""
    public static string HangarTitle(Loc loc, string key)
    {
        if (key.Length == 0) return "";
        var lines = loc.Get(key.StartsWith('@') ? key : "@" + key).Split("\\n").Where(l => !l.Contains("%ls", StringComparison.Ordinal)).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return string.Join(" ", lines);
    }
    // 一覧に入れる、地図の JSON に無い案内するもの (2026-11-04、利用者の指示「罰金の支払い端末もある」):
    //   罰金の支払い端末 = socpak の .soc の Entity で、EntityClass に「FineTerminal」のあるもの (Orison で SoftLock_Terminal_Standard_HighTech_FineTerminal を確かめた)。
    //   位置は Entity の Pos をインスタンスの置き方で場所の座標にしたもの (StationTree の端末と同じ)
    public sealed record LayoutTerminal(string Kind, double[] Pos, string Entity, string Class);
    public sealed record Layout(List<LayoutInst> Instances, List<LayoutTerminal> Terminals)
    {
        // 場所の中身 (乗り場・店・端末・ハンガーの入口)。2D の地図の JSON の代わり (PlaceOf)。中身を読まずに作った一覧 (contents: false の木) では null
        public PlaceData? Place { get; init; }
        // 屋根の扉の板を探す区画 (Instances の中の位置。旧方式の書き出す範囲 = 深さ 0・1 と、socpak のパスに /ext/ を含むもの。LayoutOf が木から入れる。DB から読んだ一覧では null)
        public List<int>? RoofRange { get; init; }
        // 屋根の扉の候補 (RoofDoorsOf。DB の place_roofdoors。無ければ null = 印を動かさない)
        public List<RoofDoor>? RoofDoors { get; init; }
    }
    public sealed record RoofDoor(double[] Origin, double[] Center);
    // 場所の中身 (2026-11-04、利用者の決定「名前を 2d からとるのをやめるだけの話」): 2D の地図の JSON から取っていたもののうち、Data.p4k (区画の .soc の Entity・metadata) から取れるもの。
    // 2D の地図と同じ作り方 (StationTree の中身と StationRouteGraph.TransitLines・TransitList。階は使わない) で作る:
    //   Transit = 乗り場と行き先 [種類 (elevator・elevator-noride・tram・destination), 位置, 路線, 行き先の鍵]、Shops = .soc の SCShop、
    //   Terms = 端末 (StationTree.Terminals。向きは 2D の地図の yaw と同じ求め方 (度))、Hangars = ハンガーの入口 (exterior の hangar。大きさは GatewaySize の区分)
    public sealed record PlaceTransit(string Kind, double[] Pos, string Line, string Stop);
    public sealed record PlaceShop(string Entity, string Guid, double[] Pos);
    public sealed record PlaceTerm(string Kind, double[] Pos, double? Yaw, string Entity, string Class, string ShopGuid, string ShopEntity);
    public sealed record PlaceHangar(string Class, string Size, double[] Pos);
    // 外の設備 (exterior の pad・dock・freight。旧方式 4bfc288:Station3DExports.WriteScene の markers の pads・docks・freight と同じもの)。Key は HUDDisplayName の鍵 (freight は "")
    public sealed record PlaceExt(string Kind, double[] Pos, string Key);
    public sealed record PlaceData(List<PlaceTransit> Transit, List<PlaceShop> Shops, List<PlaceTerm> Terms, List<PlaceHangar> Hangars)
    {
        // 外の貨物エレベーターの台の数 (exterior の freight。［詳細］・星系図の施設の手がかり AmenityMapHints の「積載ドック」)
        public int Freight { get; init; }
        // 外の設備 (着陸パッド pad・外部のドッキングポート dock・外部貨物エレベーター freight。木の Exteriors の順。DB の place_exts。版 8 から)
        public List<PlaceExt> Exts { get; init; } = new();
    }
    private static double[] V3((double X, double Y, double Z) p) => [p.X, p.Y, p.Z];
    internal static PlaceData PlaceOf(StationTree tree)
    {
        var transit = StationRouteGraph.TransitList(tree, StationRouteGraph.TransitLines(tree))
            .Select(t => new PlaceTransit(t.Kind, V3(t.Pos), t.Line, t.Stop)).ToList();
        var shops = tree.Shops.Select(s => new PlaceShop(s.EntityName, s.Guid, V3(s.Pos))).ToList();
        static double? Yaw((double W, double X, double Y, double Z)? q)
        {
            if (q is not { } r) return 0;
            var (x, y, _) = new StationXf(0, 0, 0, r.W, r.X, r.Y, r.Z).Rotate(1, 0, 0);
            return Math.Atan2(y, x) * 180 / Math.PI;
        }
        var terms = tree.Terminals.Select(t => new PlaceTerm(t.Kind, V3(t.Pos), Yaw(t.Rot), t.EntityName, t.EntityClass, t.ShopGuid, t.ShopEntity)).ToList();
        var hangars = tree.Exteriors.Where(x => x.Kind == "hangar").Select(x => new PlaceHangar(x.EntityClass, x.GatewaySize switch { 2 => "S", 4 => "M", 5 => "L", 6 => "XL", _ => "" }, V3(x.Pos))).ToList();
        // 外の設備: 旧方式の scene.json の markers と同じく、木の Exteriors のうち pad・dock・freight (AI 専用の着陸パッド・ドッキングポートは StationTree が外してある)
        var exts = tree.Exteriors.Where(x => x.Kind is "pad" or "dock" or "freight").Select(x => new PlaceExt(x.Kind, V3(x.Pos), x.NameKey)).ToList();
        return new PlaceData(transit, shops, terms, hangars) { Freight = tree.Exteriors.Count(x => x.Kind == "freight"), Exts = exts };
    }
    public const string LayoutFormat = "scjt-view3d-layout-2";
    public static string LayoutPathOf(string mapJsonPath) => Path.ChangeExtension(mapJsonPath, null) + ".layout.json";

    // fineCache: 罰金などの端末の Entity (FineEntities) を、場所どうしで使い回す置き場 (一括で作るとき。null なら場所ごとに読む)
    public static Layout LayoutOf(StationIndex index, StationTree tree, System.Collections.Concurrent.ConcurrentDictionary<string, List<(string, string, (double, double, double))>>? fineCache = null)
    {
        var res = new List<LayoutInst>();
        var roofRange = new List<int>();
        foreach (var inst in tree.Instances)
        {
            if (inst.Socpak.Length == 0) continue;
            // 旧方式の書き出す範囲 (4bfc288:Station3DExports.MakePlan。Depth > MaxPartDepth (1) で socpak に /ext/ を含まないものと、見つからない socpak (!Found) は入れない)
            if ((inst.Depth <= RoofMaxPartDepth || inst.Socpak.Contains(RoofExtPathPart, StringComparison.OrdinalIgnoreCase)) && inst.Found) roofRange.Add(res.Count);
            double[]? lo = null, hi = null;
            if (inst.MinBounds is { } a && inst.MaxBounds is { } b)
            {
                lo = [double.MaxValue, double.MaxValue, double.MaxValue]; hi = [double.MinValue, double.MinValue, double.MinValue];
                foreach (var x in new[] { a.X, b.X }) foreach (var y in new[] { a.Y, b.Y }) foreach (var z in new[] { a.Z, b.Z })
                {
                    var q = inst.ToStation.Apply((x, y, z));
                    lo[0] = Math.Min(lo[0], q.X); hi[0] = Math.Max(hi[0], q.X); lo[1] = Math.Min(lo[1], q.Y); hi[1] = Math.Max(hi[1], q.Y); lo[2] = Math.Min(lo[2], q.Z); hi[2] = Math.Max(hi[2], q.Z);
                }
            }
            res.Add(new LayoutInst(inst.Index, inst.Socpak, InstMatrix(inst), lo, hi));
        }
        // 罰金の支払い端末・クエストのもの (EntityKinds。socpak ごとに 1 回読み、置かれた所ごとに場所の座標にする)
        var fines = new List<LayoutTerminal>();
        var bySoc = new Dictionary<string, List<(string Cls, string Name, (double X, double Y, double Z) Pos)>>(StringComparer.Ordinal);
        foreach (var inst in tree.Instances)
        {
            if (inst.Socpak.Length == 0) continue;
            if (!bySoc.TryGetValue(inst.Socpak, out var ents)) bySoc[inst.Socpak] = ents = fineCache != null ? fineCache.GetOrAdd(inst.Socpak, k => FineEntities(index, k)) : FineEntities(index, inst.Socpak);
            foreach (var (cls, name, pos) in ents)
            {
                var q = inst.ToStation.Apply(pos);
                fines.Add(new LayoutTerminal(EntityKindOf(cls) ?? "", [q.X, q.Y, q.Z], name, cls));
            }
        }
        // 中身を読んだ木 (contents: true) なら、場所の中身も (PlaceOf)
        var loaded = tree.Points.Count > 0 || tree.Shops.Count > 0 || tree.Terminals.Count > 0 || tree.Exteriors.Count > 0;
        return new Layout(res, fines) { Place = loaded ? PlaceOf(tree) : null, RoofRange = roofRange };
    }

    // 案内するものにする Entity の見分け (EntityClass。2026-11-04、利用者の指示「行先で必要な場所 … クエストの扉、クエストの扉を開ける赤などのカードを取るとこ
    // クエストで使うパネル(カード差し込みやダウンロードなど)」。取り込み済みの 91 か所の .soc の EntityClass を数えて決めた):
    //   fine       = 罰金の支払い端末 (EntityClass に FineTerminal)
    //   questdoor  = クエストの扉 (Door_ChipReader_*・Door_Generic_ChipReader・Door_Relay_ContestedZone・Door_Default_Keypad_*)
    //   questitem  = カード・ヒューズを取る所 (GPI_ItemSpawner_PrintCooldown_Keycard_*・GPI_ItemSpawner_Fuse_*)
    //   questpanel = クエストで使うパネル (GPI_Relay_<n>slot_* (ヒューズを差す)・GPI_Generic_CardReader_*・Slot_Removable_Chip_Hackable_*・Terminal_Delving_Download_*・
    //                DisplayScreen_Delving_Download_*・GPI_ButtonTerminal_*・GPI_LaserTerminal・GPI_CatalystRackTerminal・GPI_ReflectorKeycodeScreen_*)
    private static readonly (string Kind, System.Text.RegularExpressions.Regex Rx)[] EntityKinds =
    [
        ("fine", new(@"FineTerminal", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
        ("questdoor", new(@"^Door_(ChipReader_|Generic_ChipReader|Relay_ContestedZone|Default_Keypad)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
        ("questitem", new(@"^GPI_ItemSpawner_(PrintCooldown_Keycard_|Fuse_)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
        ("questpanel", new(@"^(GPI_Relay_\d+slot|GPI_Generic_CardReader|Slot_Removable_Chip_Hackable|Terminal_Delving_Download|DisplayScreen_Delving_Download|GPI_ButtonTerminal|GPI_LaserTerminal|GPI_CatalystRackTerminal|GPI_ReflectorKeycodeScreen)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
    ];
    // .soc を XML にする前に、中にあるかを見る文字 (EntityKinds のどれかに当たる Entity の EntityClass に必ず入る文字)
    private static readonly string[] EntityMarks = ["FineTerminal", "Door_", "GPI_", "Slot_Removable_Chip_Hackable", "Delving_Download"];
    public static string? EntityKindOf(string cls) { foreach (var (k, rx) in EntityKinds) if (rx.IsMatch(cls)) return k; return null; }

    private static List<(string, string, (double, double, double))> FineEntities(StationIndex index, string socpak)
    {
        var res = new List<(string, string, (double, double, double))>();
        try
        {
            using var zip = index.OpenSocpak(socpak);
            if (zip == null) return res;
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = StationIndex.ReadAll(e);
                var latin1 = Encoding.Latin1.GetString(bytes);
                if (!EntityMarks.Any(m => latin1.Contains(m, StringComparison.OrdinalIgnoreCase))) continue;
                var starts = new List<int>();
                for (int at = latin1.IndexOf("CryXmlB", StringComparison.Ordinal); at >= 0; at = latin1.IndexOf("CryXmlB", at + 7, StringComparison.Ordinal)) starts.Add(at);
                for (int i = 0; i < starts.Count; i++)
                {
                    var a = starts[i]; var z = i + 1 < starts.Count ? starts[i + 1] : bytes.Length;
                    if (!EntityMarks.Any(m => latin1.IndexOf(m, a, z - a, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    System.Xml.Linq.XDocument doc;
                    try { doc = CryXmlParser.ParseBinaryBytes(bytes[a..z]); } catch { continue; }
                    foreach (var en in doc.Descendants("Entity"))
                    {
                        var cls = (string?)en.Attribute("EntityClass") ?? "";
                        if (EntityKindOf(cls) == null) continue;
                        // Pos の無い Entity は socpak の原点に置かれたものとする (Orison の terminal_fine_1_pristine_a.socpak の Entity は Pos が無かった)
                        var pos = StationIndex.ParseVec((string?)en.Attribute("Pos")) ?? (0, 0, 0);
                        res.Add((cls, (string?)en.Attribute("Name") ?? "", pos));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        return res;
    }

    // 一覧を書く (一時ファイルに書いてから置き換える)
    public static void WriteLayout(string path, Layout layout)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = LayoutFormat,
            instances = layout.Instances.Select(i => new object?[] { i.Index, i.Socpak, i.A, i.Lo, i.Hi }),
            terminals = layout.Terminals.Select(t => new object[] { t.Kind, t.Pos, t.Entity, t.Class }),
        }));
        File.Move(tmp, path, true);
    }

    // 一覧を読む。無い・形が違えば null
    public static Layout? ReadLayout(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("format", out var f) || f.GetString() != LayoutFormat) return null;
            double[]? Arr(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetDouble()).ToArray() : null;
            var insts = doc.RootElement.GetProperty("instances").EnumerateArray()
                .Select(e => new LayoutInst(e[0].GetInt32(), e[1].GetString() ?? "", Arr(e[2]) ?? [], Arr(e[3]), Arr(e[4]))).ToList();
            var terms = doc.RootElement.GetProperty("terminals").EnumerateArray()
                .Select(e => new LayoutTerminal(e[0].GetString() ?? "", Arr(e[1]) ?? [], e[2].GetString() ?? "", e[3].GetString() ?? "")).ToList();
            return new Layout(insts, terms);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    // ===== 区画の置き方の一覧の DB (2026-11-04、利用者の指示「DB などに持たせる。一括取得で一括で保存する。同じものの使いまわしもこの時格納」) =====
    // 置き場: 地図の置き場所 (StationPlaces.Folder) の view3d_layout.db (SQLite)。場所ごとのファイル (前の .layout.json) は使わない。
    // 表:
    //   meta(key, value)                     … format (LayoutDbFormat)・stamp (Data.p4k の印 P4kStamp.Key)・built_utc・places・seconds
    //   socpaks(id, path, places, uses)      … socpak (ObjectContainers の下の相対パスの小文字) ごとに 1 行。使い回しの情報: places = 置かれている場所の数、uses = 全部の場所で置かれた数
    //   places(record, socpaks, instances)   … 場所 (星図の場所の record、ハンガーは StationHangars.RecordOf) ごとに 1 行
    //   instances(record, idx, socpak, a, lo, hi) … 置かれた所 (LayoutInst)。socpak は socpaks.id、a は 3x4 の行列 (double 12 個)、lo・hi は箱 (double 3 個。無ければ NULL)
    //   terminals(record, n, kind, pos, entity, class) … 地図の JSON に無い案内するもの (LayoutTerminal。罰金の支払い端末)
    // 一括: BuildAll で、ゲームデータの索引・ルート探しを 1 回にして、見つかった場所を全部作り、1 つのトランザクションで新しいファイルに書いてから入れ替える。
    // Data.p4k の印が違う DB は古いものとして使わない (IsCurrent が false)。ハンガーの中は、開いたときに Put で 1 か所足す (socpaks の数え直しもする)
    public const string LayoutDbName = "view3d_layout.db";
    // 一括で作るときに、1 か所を始めてよいプロセスのメモリの上限 (バイト。BuildLayoutDb の説明)
    public const long LayoutMemGate = 2L << 30;
    // (版 7 では、ほかに店の端末の結び先 (place_terms の shop_guid・shop_entity) が、SCShop の ShopKioskGUIDs に載っている店で決まるようになった
    //  (載っていない端末は今までどおり同じ socpak のいちばん近い店。StationMapData の SocpakContents.ReadTerminals。2026-11-05))
    public const string LayoutDbFormat = "scjt-view3d-layoutdb-8";   // 2: クエストの扉・カードやヒューズを取る所・クエストのパネルを terminals に足した。3: 場所の中身 (place_*) を足した。4: 外の貨物エレベーターの数 (place_has.freight) を足した。5: 抜けた床の補い part_fill を足した。6: 屋根の扉の候補 place_roofdoors を足した。7: 場所のパレットの色と材質の差し替え look_* を足した。8: 外の設備 (着陸パッド・ドッキングポート・外部貨物エレベーター) place_exts を足した

    private static byte[] Doubles(double[] v) { var b = new byte[v.Length * 8]; Buffer.BlockCopy(v, 0, b, 0, b.Length); return b; }
    private static double[] DoublesOf(byte[] b) { var v = new double[b.Length / 8]; Buffer.BlockCopy(b, 0, v, 0, b.Length); return v; }
    private static Microsoft.Data.Sqlite.SqliteConnection OpenDb(string path, bool write)
    {
        var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode={(write ? "ReadWriteCreate" : "ReadOnly")};Pooling=False");
        c.Open();
        return c;
    }
    private static void Exec(Microsoft.Data.Sqlite.SqliteConnection c, string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }

    // DB が今の Data.p4k の印で作ったものか
    public static bool LayoutDbIsCurrent(string path, string stampKey)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var c = OpenDb(path, false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM meta WHERE key IN ('format', 'stamp')";
            using var r = cmd.ExecuteReader();
            string f = "", s = "";
            while (r.Read()) { if (r.GetString(0) == "format") f = r.GetString(1); else s = r.GetString(1); }
            return f == LayoutDbFormat && s == stampKey;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return false; }
    }

    // DB の meta の値 (無い・読めなければ null)
    public static string? LayoutDbMeta(string path, string key)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var c = OpenDb(path, false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    // DB にある場所の全部 (record → (精製所の端末があるか, 外の貨物エレベーターの数, 行き先があるか))。版の印は見ない (古い DB でも手がかりには使う)。DB が無い・読めなければ空
    // Targets (2026-11-05、親の決定。行き先の無い場所を開こうとしたときの差し替え StationView3DView.RedirectAsync に使う): 行き先の行
    //   (端末 place_terms・店 place_shops・乗り場 place_transit・ハンガーの入口 place_hangars・外の設備 place_exts・案内するもの terminals) が 1 つでもあるか。
    //   行き先の表が読めない DB (前の形) では、どの場所も「ある」のままにする (差し替えをしない)
    public static Dictionary<string, (bool Refinery, int Freight, bool Targets)> LayoutDbSummary(string path)
    {
        var res = new Dictionary<string, (bool Refinery, int Freight, bool Targets)>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return res;
            using var c = OpenDb(path, false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT p.record, h.freight, EXISTS(SELECT 1 FROM place_terms t WHERE t.record = p.record AND t.kind = 'refinery') FROM places p LEFT JOIN place_has h ON h.record = p.record";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) res[r.GetString(0)] = (r.GetInt64(2) != 0, r.IsDBNull(1) ? 0 : r.GetInt32(1), true);
            try
            {
                cmd.CommandText = "SELECT p.record FROM places p WHERE " + string.Join(" AND ",
                    new[] { "place_terms", "place_shops", "place_transit", "place_hangars", "place_exts", "terminals" }.Select(t => $"NOT EXISTS(SELECT 1 FROM {t} x WHERE x.record = p.record)"));
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (res.TryGetValue(r.GetString(0), out var v)) res[r.GetString(0)] = (v.Refinery, v.Freight, false);
            }
            catch (Microsoft.Data.Sqlite.SqliteException) { }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { }
        return res;
    }

    // 1 か所の区画の箱を全部合わせた範囲 (場所の座標。Session.LayoutCenter が真ん中を出すのと同じ範囲)。版の印は見ない。箱のある区画が無い・DB が無い・読めなければ null
    public static (double[] Lo, double[] Hi)? LayoutDbBox(string path, string record)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var c = OpenDb(path, false);
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT lo, hi FROM instances WHERE record = $r AND lo IS NOT NULL AND hi IS NOT NULL";
            cmd.Parameters.AddWithValue("$r", record);
            var lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; var hi = new[] { double.MinValue, double.MinValue, double.MinValue };
            var any = false;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var a = DoublesOf((byte[])r[0]); var b = DoublesOf((byte[])r[1]);
                if (a.Length < 3 || b.Length < 3) continue;
                for (int i = 0; i < 3; i++) { lo[i] = Math.Min(lo[i], a[i]); hi[i] = Math.Max(hi[i], b[i]); }
                any = true;
            }
            return any ? (lo, hi) : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    // 子の場所 child の区画が親の場所 parent の区画の中に入っているとき、親の 3D で子の場所へ飛ぶための行き先 (店の Entity 名。3D の窓の focusShop)。決まらなければ null。
    // 決まるのは次の両方のとき (2026-11-05、親の決定。StationView3DView.RedirectAsync):
    //   ・子の最初の区画 (instances の idx がいちばん小さいもの = 子のルートの socpak) と同じ socpak が、親の区画にちょうど 1 つある
    //   ・子の最初の店 (place_shops の n がいちばん小さいもの = 子の場所だけを開いたときに最初に出る行き先) と同じ Entity 名の店が、親の店にちょうど 1 つある
    // 版の印は見ない。DB が無い・読めなければ null
    public static string? LayoutDbChildFocus(string path, string child, string parent)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var c = OpenDb(path, false);
            using var cmd = c.CreateCommand();
            cmd.Parameters.AddWithValue("$c", child);
            cmd.Parameters.AddWithValue("$p", parent);
            cmd.CommandText = "SELECT socpak FROM instances WHERE record = $c ORDER BY idx LIMIT 1";
            if (cmd.ExecuteScalar() is not long soc) return null;
            cmd.CommandText = "SELECT COUNT(*) FROM instances WHERE record = $p AND socpak = $s";
            cmd.Parameters.AddWithValue("$s", soc);
            if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 1) return null;
            cmd.CommandText = "SELECT entity FROM place_shops WHERE record = $c ORDER BY n LIMIT 1";
            if (cmd.ExecuteScalar() is not string entity || entity.Length == 0) return null;
            cmd.CommandText = "SELECT COUNT(*) FROM place_shops WHERE record = $p AND entity = $e";
            cmd.Parameters.AddWithValue("$e", entity);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 1 ? entity : null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
    }

    // 1 か所の一覧を読む。無い・印が違えば null
    public static Layout? ReadLayoutDb(string path, string stampKey, string record)
    {
        if (!LayoutDbIsCurrent(path, stampKey)) return null;
        using var c = OpenDb(path, false);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM places WHERE record = $r";
        cmd.Parameters.AddWithValue("$r", record);
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return null;
        cmd.CommandText = "SELECT i.idx, s.path, i.a, i.lo, i.hi FROM instances i JOIN socpaks s ON s.id = i.socpak WHERE i.record = $r ORDER BY i.idx";
        var insts = new List<LayoutInst>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                insts.Add(new LayoutInst(r.GetInt32(0), r.GetString(1), DoublesOf((byte[])r[2]), r.IsDBNull(3) ? null : DoublesOf((byte[])r[3]), r.IsDBNull(4) ? null : DoublesOf((byte[])r[4])));
        cmd.CommandText = "SELECT kind, pos, entity, class FROM terminals WHERE record = $r ORDER BY n";
        var terms = new List<LayoutTerminal>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) terms.Add(new LayoutTerminal(r.GetString(0), DoublesOf((byte[])r[1]), r.GetString(2), r.GetString(3)));
        PlaceData? place = null;
        cmd.CommandText = "SELECT COUNT(*) FROM place_has WHERE record = $r";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0)
        {
            cmd.CommandText = "SELECT freight FROM place_has WHERE record = $r";
            var freight = Convert.ToInt32(cmd.ExecuteScalar() ?? 0L);
            place = new PlaceData(new(), new(), new(), new()) { Freight = freight };
            cmd.CommandText = "SELECT kind, pos, line, stop FROM place_transit WHERE record = $r ORDER BY n";
            using (var r = cmd.ExecuteReader()) while (r.Read()) place.Transit.Add(new PlaceTransit(r.GetString(0), DoublesOf((byte[])r[1]), r.GetString(2), r.GetString(3)));
            cmd.CommandText = "SELECT entity, guid, pos FROM place_shops WHERE record = $r ORDER BY n";
            using (var r = cmd.ExecuteReader()) while (r.Read()) place.Shops.Add(new PlaceShop(r.GetString(0), r.GetString(1), DoublesOf((byte[])r[2])));
            cmd.CommandText = "SELECT kind, pos, yaw, entity, class, shop_guid, shop_entity FROM place_terms WHERE record = $r ORDER BY n";
            using (var r = cmd.ExecuteReader()) while (r.Read()) place.Terms.Add(new PlaceTerm(r.GetString(0), DoublesOf((byte[])r[1]), r.IsDBNull(2) ? null : r.GetDouble(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
            cmd.CommandText = "SELECT class, size, pos FROM place_hangars WHERE record = $r ORDER BY n";
            using (var r = cmd.ExecuteReader()) while (r.Read()) place.Hangars.Add(new PlaceHangar(r.GetString(0), r.GetString(1), DoublesOf((byte[])r[2])));
            cmd.CommandText = "SELECT kind, pos, key FROM place_exts WHERE record = $r ORDER BY n";
            using (var r = cmd.ExecuteReader()) while (r.Read()) place.Exts.Add(new PlaceExt(r.GetString(0), DoublesOf((byte[])r[1]), r.GetString(2)));
        }
        // 屋根の扉の候補 (版 6 から。行が無ければ空 = 印を動かさない)
        var roof = new List<RoofDoor>();
        cmd.CommandText = "SELECT origin, center FROM place_roofdoors WHERE record = $r ORDER BY n";
        using (var r = cmd.ExecuteReader()) while (r.Read()) roof.Add(new RoofDoor(DoublesOf((byte[])r[0]), DoublesOf((byte[])r[1])));
        return new Layout(insts, terms) { Place = place, RoofDoors = roof };
    }

    private static void CreateTables(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        Exec(c, "CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)");
        Exec(c, "CREATE TABLE IF NOT EXISTS socpaks(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, places INTEGER NOT NULL DEFAULT 0, uses INTEGER NOT NULL DEFAULT 0)");
        Exec(c, "CREATE TABLE IF NOT EXISTS places(record TEXT PRIMARY KEY, socpaks INTEGER NOT NULL, instances INTEGER NOT NULL)");
        Exec(c, "CREATE TABLE IF NOT EXISTS instances(record TEXT NOT NULL, idx INTEGER NOT NULL, socpak INTEGER NOT NULL, a BLOB NOT NULL, lo BLOB, hi BLOB, PRIMARY KEY(record, idx)) WITHOUT ROWID");
        Exec(c, "CREATE TABLE IF NOT EXISTS terminals(record TEXT NOT NULL, n INTEGER NOT NULL, kind TEXT NOT NULL, pos BLOB NOT NULL, entity TEXT NOT NULL, class TEXT NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
        // 場所の中身 (PlaceData。2D の地図の JSON の代わり)。place_has に行のある場所だけ中身がある
        // 一括で作ったときの場所ごとの秒 (次の一括の重さ。BuildLayoutDb の WeightsOf。中身ではないので、DB の中身の比べからは外す)
        Exec(c, "CREATE TABLE IF NOT EXISTS place_secs(record TEXT PRIMARY KEY, seconds REAL NOT NULL)");
        Exec(c, "CREATE TABLE IF NOT EXISTS place_has(record TEXT PRIMARY KEY, freight INTEGER NOT NULL DEFAULT 0)");
        Exec(c, "CREATE TABLE IF NOT EXISTS place_transit(record TEXT NOT NULL, n INTEGER NOT NULL, kind TEXT NOT NULL, pos BLOB NOT NULL, line TEXT NOT NULL, stop TEXT NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
        Exec(c, "CREATE TABLE IF NOT EXISTS place_shops(record TEXT NOT NULL, n INTEGER NOT NULL, entity TEXT NOT NULL, guid TEXT NOT NULL, pos BLOB NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
        Exec(c, "CREATE TABLE IF NOT EXISTS place_terms(record TEXT NOT NULL, n INTEGER NOT NULL, kind TEXT NOT NULL, pos BLOB NOT NULL, yaw REAL, entity TEXT NOT NULL, class TEXT NOT NULL, shop_guid TEXT NOT NULL, shop_entity TEXT NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
        Exec(c, "CREATE TABLE IF NOT EXISTS place_hangars(record TEXT NOT NULL, n INTEGER NOT NULL, class TEXT NOT NULL, size TEXT NOT NULL, pos BLOB NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
        // 外の設備 (PlaceExt。kind = pad・dock・freight、pos は場所の座標の double 3 個、key は HUDDisplayName の鍵 (freight は ""))
        Exec(c, "CREATE TABLE IF NOT EXISTS place_exts(record TEXT NOT NULL, n INTEGER NOT NULL, kind TEXT NOT NULL, pos BLOB NOT NULL, key TEXT NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
        StationFill.CreateTable(c);   // part_fill: 粗い形で抜けた床の面（部品の種類ごと）
        CreateLookTables(c);          // look_*: 場所のパレットの色と材質の差し替え
        // 屋根の扉の候補 (RoofDoorsOf。origin・center は場所の座標の double 3 個)。ハンガーのある場所だけ行がある
        Exec(c, "CREATE TABLE IF NOT EXISTS place_roofdoors(record TEXT NOT NULL, n INTEGER NOT NULL, origin BLOB NOT NULL, center BLOB NOT NULL, PRIMARY KEY(record, n)) WITHOUT ROWID");
    }

    // 1 か所を書く (同じ record があれば消してから)。socpaks の使い回しの数は WriteCounts で数え直す
    private static void PutPlace(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, string record, Layout layout, Dictionary<string, long> ids)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$r", record);
        foreach (var t in new[] { "instances", "terminals", "places", "place_has", "place_transit", "place_shops", "place_terms", "place_hangars", "place_exts", "place_roofdoors" }) { cmd.CommandText = $"DELETE FROM {t} WHERE record = $r"; cmd.ExecuteNonQuery(); }
        using var sp = c.CreateCommand(); sp.Transaction = tx;
        sp.CommandText = "INSERT INTO socpaks(path) VALUES($p) ON CONFLICT(path) DO NOTHING; SELECT id FROM socpaks WHERE path = $p";
        var pp = sp.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
        using var ins = c.CreateCommand(); ins.Transaction = tx;
        ins.CommandText = "INSERT INTO instances(record, idx, socpak, a, lo, hi) VALUES($r, $i, $s, $a, $lo, $hi)";
        ins.Parameters.AddWithValue("$r", record);
        var pi = ins.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Integer); var ps = ins.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pa = ins.Parameters.Add("$a", Microsoft.Data.Sqlite.SqliteType.Blob); var plo = ins.Parameters.Add("$lo", Microsoft.Data.Sqlite.SqliteType.Blob); var phi = ins.Parameters.Add("$hi", Microsoft.Data.Sqlite.SqliteType.Blob);
        foreach (var i in layout.Instances)
        {
            if (!ids.TryGetValue(i.Socpak, out var id)) { pp.Value = i.Socpak; id = Convert.ToInt64(sp.ExecuteScalar()); ids[i.Socpak] = id; }
            pi.Value = i.Index; ps.Value = id; pa.Value = Doubles(i.A);
            plo.Value = i.Lo != null ? Doubles(i.Lo) : DBNull.Value; phi.Value = i.Hi != null ? Doubles(i.Hi) : DBNull.Value;
            ins.ExecuteNonQuery();
        }
        using var tm = c.CreateCommand(); tm.Transaction = tx;
        tm.CommandText = "INSERT INTO terminals(record, n, kind, pos, entity, class) VALUES($r, $n, $k, $p, $e, $c)";
        tm.Parameters.AddWithValue("$r", record);
        var tn = tm.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Integer); var tk = tm.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text); var tpos = tm.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Blob);
        var te = tm.Parameters.Add("$e", Microsoft.Data.Sqlite.SqliteType.Text); var tc = tm.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Text);
        for (int n = 0; n < layout.Terminals.Count; n++)
        {
            var t = layout.Terminals[n];
            tn.Value = n; tk.Value = t.Kind; tpos.Value = Doubles(t.Pos); te.Value = t.Entity; tc.Value = t.Class;
            tm.ExecuteNonQuery();
        }
        if (layout.Place is { } pl)
        {
            void Rows(string sql, int count, Action<Microsoft.Data.Sqlite.SqliteCommand, int> fill)
            {
                using var q = c.CreateCommand(); q.Transaction = tx; q.CommandText = sql;
                for (int n = 0; n < count; n++) { q.Parameters.Clear(); q.Parameters.AddWithValue("$r", record); q.Parameters.AddWithValue("$n", n); fill(q, n); q.ExecuteNonQuery(); }
            }
            Rows("INSERT INTO place_has(record, freight) VALUES($r, $f)", 1, (q, _) => q.Parameters.AddWithValue("$f", pl.Freight));
            Rows("INSERT INTO place_transit(record, n, kind, pos, line, stop) VALUES($r, $n, $k, $p, $l, $s)", pl.Transit.Count, (q, n) =>
            { var t = pl.Transit[n]; q.Parameters.AddWithValue("$k", t.Kind); q.Parameters.AddWithValue("$p", Doubles(t.Pos)); q.Parameters.AddWithValue("$l", t.Line); q.Parameters.AddWithValue("$s", t.Stop); });
            Rows("INSERT INTO place_shops(record, n, entity, guid, pos) VALUES($r, $n, $e, $g, $p)", pl.Shops.Count, (q, n) =>
            { var t = pl.Shops[n]; q.Parameters.AddWithValue("$e", t.Entity); q.Parameters.AddWithValue("$g", t.Guid); q.Parameters.AddWithValue("$p", Doubles(t.Pos)); });
            Rows("INSERT INTO place_terms(record, n, kind, pos, yaw, entity, class, shop_guid, shop_entity) VALUES($r, $n, $k, $p, $y, $e, $c, $sg, $se)", pl.Terms.Count, (q, n) =>
            {
                var t = pl.Terms[n]; q.Parameters.AddWithValue("$k", t.Kind); q.Parameters.AddWithValue("$p", Doubles(t.Pos)); q.Parameters.AddWithValue("$y", t.Yaw is { } y ? y : DBNull.Value);
                q.Parameters.AddWithValue("$e", t.Entity); q.Parameters.AddWithValue("$c", t.Class); q.Parameters.AddWithValue("$sg", t.ShopGuid); q.Parameters.AddWithValue("$se", t.ShopEntity);
            });
            Rows("INSERT INTO place_hangars(record, n, class, size, pos) VALUES($r, $n, $c, $s, $p)", pl.Hangars.Count, (q, n) =>
            { var t = pl.Hangars[n]; q.Parameters.AddWithValue("$c", t.Class); q.Parameters.AddWithValue("$s", t.Size); q.Parameters.AddWithValue("$p", Doubles(t.Pos)); });
            Rows("INSERT INTO place_exts(record, n, kind, pos, key) VALUES($r, $n, $k, $p, $y)", pl.Exts.Count, (q, n) =>
            { var t = pl.Exts[n]; q.Parameters.AddWithValue("$k", t.Kind); q.Parameters.AddWithValue("$p", Doubles(t.Pos)); q.Parameters.AddWithValue("$y", t.Key); });
        }
        if (layout.RoofDoors is { Count: > 0 } rd)
        {
            using var q = c.CreateCommand(); q.Transaction = tx;
            q.CommandText = "INSERT INTO place_roofdoors(record, n, origin, center) VALUES($r, $n, $o, $c)";
            q.Parameters.AddWithValue("$r", record);
            var qn = q.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Integer); var qo = q.Parameters.Add("$o", Microsoft.Data.Sqlite.SqliteType.Blob); var qc = q.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Blob);
            for (int n = 0; n < rd.Count; n++) { qn.Value = n; qo.Value = Doubles(rd[n].Origin); qc.Value = Doubles(rd[n].Center); q.ExecuteNonQuery(); }
        }
        cmd.CommandText = "INSERT INTO places(record, socpaks, instances) VALUES($r, $s, $n)";
        cmd.Parameters.AddWithValue("$s", layout.Instances.Select(i => i.Socpak).Distinct(StringComparer.Ordinal).Count());
        cmd.Parameters.AddWithValue("$n", layout.Instances.Count);
        cmd.ExecuteNonQuery();
    }
    // 使い回しの数 (socpaks.places・uses) を instances から数え直す
    private static void WriteCounts(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        // (2026-11-04、利用者の指摘「3225 / 3225 で 99% のまま止まる」) 前は socpak ごとに instances を全部読む相関の UPDATE (4,787 × 169,536 × 2) で、数分かかっていた。
        // instances を 1 回だけ socpak ごとにまとめてから入れる (結果は同じ)
        cmd.CommandText = "CREATE TEMP TABLE IF NOT EXISTS socpak_cnt(socpak INTEGER PRIMARY KEY, places INTEGER NOT NULL, uses INTEGER NOT NULL)";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM socpak_cnt";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO socpak_cnt(socpak, places, uses) SELECT socpak, COUNT(DISTINCT record), COUNT(*) FROM instances GROUP BY socpak";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "UPDATE socpaks SET places = COALESCE((SELECT places FROM socpak_cnt WHERE socpak_cnt.socpak = socpaks.id), 0), uses = COALESCE((SELECT uses FROM socpak_cnt WHERE socpak_cnt.socpak = socpaks.id), 0)";
        cmd.ExecuteNonQuery();
    }

    // 一括で作る: roots (場所の record → ルート) を全部、1 回の索引で作り、新しいファイルに書いてから path と入れ替える。
    // 1 か所で失敗したら、その場所は入れずに続け、failed に理由を返す。progress(済んだ数, 全部)
    // parallel: 場所ごとの読み取りと計算 (StationTree.Load・LayoutOf) を並べる数 (1 なら 1 か所ずつ)。並べても、DB へは場所の名前の順に 1 つのトランザクションで書く
    // (2026-11-04、利用者の指示「この取り込みも並列処理を入れて」)。wait: 1 か所を始める前に呼ぶ (CPU 使用率が高い間は待つ、など。呼ぶ側が決める)
    public static (int Places, int Instances, int Socpaks, long Bytes, double Seconds, List<string> Failed) BuildLayoutDb(string path, string stampKey, StationIndex index,
        IReadOnlyDictionary<string, StationRoot> roots, Action<int, int>? progress = null, CancellationToken ct = default, int parallel = 1, Action<CancellationToken>? wait = null,
        Action<double>? weightProgress = null, Action<int, int>? fillProgress = null, Action<string>? fillLog = null, Action<string>? roofLog = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        // 前の DB の補う面と印 (印が同じ場所は比べ直さない)。補う面を材質の差し替え (look_*) と同じ形で求めた DB (meta の fill_looks = 1) のものだけ使う
        // (2026-11-05: それより前の DB の補う面は、差し替える前の材質の形で求めたもので、形の印 (CRC) が同じでも中身が合わないため。1 回だけ全部求め直す)
        var prevFill = LayoutDbMeta(path, "fill_looks") == "1" ? StationFill.ReadPrev(path) : new StationFill.PrevData();
        var failed = new List<string>();
        int places = 0, insts = 0, socs;
        using (var c = OpenDb(tmp, true))
        {
            Exec(c, "PRAGMA journal_mode=OFF"); Exec(c, "PRAGMA synchronous=OFF");
            CreateTables(c);
            using var tx = c.BeginTransaction();
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            var list = roots.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            var made = new (Layout? Layout, string? Error)[list.Count];
            // 場所ごとの重さ (2026-11-04、利用者の指摘「予想が11分まで伸びた。予想とは？」): 前に作ったときのその場所の秒 (前の DB の place_secs)。
            // 前の記録の無い場所は、場所の木 (中身なし。速い) の置かれた所の数 × (前の DB の秒の合計 ÷ 置かれた所の合計。前の DB が無ければ SecPerInstance)。
            // 重い場所から始め (最後に長く残りにくくするため。DB へ書く順は名前の順のまま)、済んだ重さ ÷ 全部の重さを weightProgress に渡す
            var secs = new double[list.Count];
            var weight = WeightsOf(path, index, list, ct);
            var totalW = weight.Sum();
            double doneW = 0; var wLock = new object();
            var order = Enumerable.Range(0, list.Count).OrderByDescending(i => weight[i]).ThenBy(i => i).ToArray();
            // 同じ socpak の中身・端末の Entity は 1 回だけ読んで、場所どうしで使い回す (2026-11-04、親の指示)。終わったら捨てる
            var fineCache = new System.Collections.Concurrent.ConcurrentDictionary<string, List<(string, string, (double, double, double))>>(StringComparer.Ordinal);
            // 屋根の扉の候補を探す部品 (socpak ごと。RoofParts) も場所どうしで使い回し、終わったら捨てる
            var roofCache = new System.Collections.Concurrent.ConcurrentDictionary<string, List<(double[] M, double[] Min, double[] Max)>?>(StringComparer.Ordinal);
            var oldShared = index.SharedContents;
            index.SharedContents = new System.Collections.Concurrent.ConcurrentDictionary<string, SocpakContents?>(StringComparer.Ordinal);
            int k = 0;
            // メモリ (2026-11-04、親の決定「メモリの最大を 4 GB 以下」): 1 か所を始める前に、プロセスのメモリが LayoutMemGate を超えていれば GC をして、
            // 下がるまで (ほかの場所が終わるのを) 待つ。1 か所でも大きな場所は一時に 2 GB ほど使うので、門は 4 GB からその分を引いた値にする
            int running = 0;
            Parallel.For(0, order.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, oi =>
            {
                var i = order[oi];
                wait?.Invoke(ct);
                for (int tries = 0; ; tries++)
                {
                    if (System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 <= LayoutMemGate) break;
                    GC.Collect();
                    if (System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 <= LayoutMemGate || Volatile.Read(ref running) == 0) break;
                    Task.Delay(200, ct).Wait(ct);
                }
                Interlocked.Increment(ref running);
                try {
                var swP = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var lay = LayoutOf(index, StationTree.Load(index, list[i].Value, contents: true, ct: ct), fineCache);
                    // 屋根の扉の候補 (RoofDoorsOf。ハンガーのある場所だけ。無ければ動かす印が無いので形を読まない)
                    // 板を集める途中で失敗しても場所は落とさない: その場所の屋根の扉を空にして (印を動かさない = v2.6.2 と同じ見え方) 場所は入れ、場所の名前と例外の文を roofLog に残す
                    if (lay.Place is { Hangars.Count: > 0 })
                    {
                        try { lay = lay with { RoofDoors = RoofDoorsOf(SocpakExporter.Shared(index.P4kPath, 1), lay, roofCache, ct) }; }
                        catch (Exception rex) when (rex is InvalidOperationException or IOException or InvalidDataException or KeyNotFoundException)
                        {
                            lay = lay with { RoofDoors = new() };
                            roofLog?.Invoke($"{list[i].Key}: 屋根の扉の候補を集められませんでした ({rex.GetType().Name}: {rex.Message})。この場所はハンガーの印を動かしません");
                        }
                    }
                    made[i] = (lay, null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or KeyNotFoundException) { made[i] = (null, ex.Message); }
                secs[i] = swP.Elapsed.TotalSeconds;
                progress?.Invoke(Interlocked.Increment(ref k), list.Count);
                if (weightProgress != null) { double f; lock (wLock) { doneW += weight[i]; f = totalW > 0 ? doneW / totalW : 0; } weightProgress(f); }
                } finally { Interlocked.Decrement(ref running); }
            });
            index.SharedContents = oldShared;
            fineCache.Clear();
            roofCache.Clear();
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (made[i].Layout is { } layout) { PutPlace(c, tx, list[i].Key, layout, ids); places++; insts += layout.Instances.Count; }
                else failed.Add($"{list[i].Key}: {made[i].Error}");
            }
            WriteCounts(c, tx);
            // 場所のパレットの色と材質の差し替え (BuildLooks)。入れた場所の socpak の全部を、毎回全部求める (BuildLooks の説明)。
            // 補う面 (StationFill) を描く形と同じ材質の形で求めるため、補う面より先に求める
            LookData lookData;
            {
                var sps = new List<string>();
                for (int i = 0; i < list.Count; i++)
                    if (made[i].Layout is { } lay) sps.AddRange(lay.Instances.Select(x => x.Socpak));
                lookData = BuildLooks(index.P4kPath, sps, parallel, ct);
                WriteLooks(c, tx, lookData);
                LookStats = lookData.Stats;
            }
            // 粗い形 (LOD1) で抜けた床の面を細かい形 (LOD0) から求めて、場所ごとに入れる (StationFill)。前の DB に同じ印の場所があれば比べない
            {
                var fplaces = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                for (int i = 0; i < list.Count; i++)
                    if (made[i].Layout is { } lay) fplaces[list[i].Key] = lay.Instances.Select(x => x.Socpak).Distinct(StringComparer.Ordinal).ToList();
                var fr = StationFill.Run(index.P4kPath, fplaces, prevFill, parallel, (d, n) => fillProgress?.Invoke(d, n), ct, fillLog, LooksOf(lookData));
                StationFill.Write(c, tx, fr.Fill, fr.PlaceSrc, fr.PlaceKinds);
                FillStats = fr.Stats;
            }
            // 場所ごとにかかった秒 (次に作るときの重さ。WeightsOf)
            using (var q = c.CreateCommand())
            {
                q.Transaction = tx;
                q.CommandText = "INSERT OR REPLACE INTO place_secs(record, seconds) VALUES($r, $s)";
                var pr = q.Parameters.Add("$r", Microsoft.Data.Sqlite.SqliteType.Text); var ps = q.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Real);
                for (int i = 0; i < list.Count; i++) { pr.Value = list[i].Key; ps.Value = Math.Round(secs[i], 3); q.ExecuteNonQuery(); }
            }
            socs = ids.Count;
            using (var m = c.CreateCommand())
            {
                m.Transaction = tx;
                m.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES('format', $f), ('stamp', $s), ('built_utc', $u), ('places', $p), ('seconds', $t), ('failed', $x), ('fill_looks', '1')";
                m.Parameters.AddWithValue("$x", failed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                m.Parameters.AddWithValue("$f", LayoutDbFormat); m.Parameters.AddWithValue("$s", stampKey); m.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
                m.Parameters.AddWithValue("$p", places.ToString(System.Globalization.CultureInfo.InvariantCulture)); m.Parameters.AddWithValue("$t", sw.Elapsed.TotalSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                m.ExecuteNonQuery();
            }
            tx.Commit();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Move(tmp, path, true);
        return (places, insts, socs, new FileInfo(path).Length, sw.Elapsed.TotalSeconds, failed);
    }

    // 前の記録が無い場所の、置かれた所 1 つあたりの秒 (試しの道具で全部の場所を作ったとき: 347 秒 ÷ 169,536)
    private const double SecPerInstance = 0.002;
    public static StationFill.BuildStats? FillStats { get; private set; }   // 直近の一括で求めた補う面の数
    // 場所ごとの重さ (BuildLayoutDb の説明)。前の DB (path。版の印は見ない) が無い・読めなければ全部の場所を置かれた所の数で見る
    private static double[] WeightsOf(string path, StationIndex index, List<KeyValuePair<string, StationRoot>> list, CancellationToken ct)
    {
        var prevSec = new Dictionary<string, double>(StringComparer.Ordinal);
        var prevInst = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                using var c = OpenDb(path, false);
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT record, instances FROM places";
                using (var r = cmd.ExecuteReader()) while (r.Read()) prevInst[r.GetString(0)] = r.GetInt32(1);
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'place_secs'";
                if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0)
                {
                    cmd.CommandText = "SELECT record, seconds FROM place_secs";
                    using var r2 = cmd.ExecuteReader(); while (r2.Read()) prevSec[r2.GetString(0)] = r2.GetDouble(1);
                }
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { prevSec.Clear(); prevInst.Clear(); }
        // 置かれた所 1 つあたりの秒: 前の記録のある場所から (無ければ SecPerInstance)
        var both = prevSec.Keys.Where(prevInst.ContainsKey).ToList();
        var instSum = both.Sum(k => (double)prevInst[k]);
        var perInst = instSum > 0 ? both.Sum(k => prevSec[k]) / instSum : SecPerInstance;
        var w = new double[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var rec = list[i].Key;
            if (prevSec.TryGetValue(rec, out var sec)) { w[i] = Math.Max(sec, 0.001); continue; }
            int n;
            if (prevInst.TryGetValue(rec, out var pn)) n = pn;
            else { try { n = StationTree.Load(index, list[i].Value, contents: false, ct: ct).Instances.Count; } catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or KeyNotFoundException) { n = 1; } }
            w[i] = Math.Max(n, 1) * perInst;
        }
        return w;
    }

    // 1 か所を足す (ハンガーの中など、一括に入っていない場所)。DB が今の印でなければ何もしない (false)
    // p4kPath: あれば、場所のパレットの色・材質の差し替え (look_*) のうち、まだ行の無い socpak の分を足す (BuildLooks の existing。2026-11-05)
    public static bool PutLayoutDb(string path, string stampKey, string record, Layout layout, string? p4kPath = null)
    {
        if (!LayoutDbIsCurrent(path, stampKey)) return false;
        using (var c = OpenDb(path, true))
        {
            using var tx = c.BeginTransaction();
            var ids = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var q = c.CreateCommand()) { q.Transaction = tx; q.CommandText = "SELECT path, id FROM socpaks"; using var r = q.ExecuteReader(); while (r.Read()) ids[r.GetString(0)] = r.GetInt64(1); }
            PutPlace(c, tx, record, layout, ids);
            WriteCounts(c, tx);
            if (p4kPath != null)
            {
                // (表は版 7 の DB なら CreateTables で作ってある。LayoutDbIsCurrent で版を見ている)
                var looks = BuildLooks(p4kPath, layout.Instances.Select(i => i.Socpak), 1, default, ReadLookExisting(c, tx));
                WriteLooks(c, tx, looks, replace: false);
            }
            tx.Commit();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return true;
    }

    // ===== 場所のパレットの色と材質の差し替え (2026-11-05、利用者の指示「”色の決め方” これも事前調査して DB にリンクとしてでも入れとくのは？」
    //   「差し替えも同じように DB に予め入れておくのがいいんじゃない？」) =====
    // 表 (view3d_layout.db。一括で作るとき BuildLayoutDb が全部入れ直す。PutLayoutDb (ハンガーの中を 1 か所足す) は、まだ行の無い socpak の分だけ足す):
    //   look_pal(id, file, rgb) … パレットの鍵 (SocpakScene.PaletteKey: TintPaletteTree の record のファイル名の小文字、子を指すときは + "|" + 子の名前の小文字)。
    //                             rgb は [A, B, C, glass] の linear の r,g,b (float32 12 個。SocpakExporter.Context.PaletteColors)。record・子が無い・色が読めない名前は NULL (今の色のまま)
    //   look_mtl(id, path)      … 差し替えの材質 (.mtl の Data.p4k の中のパス)。Data.p4k に無い材質は入れない (形の読み取りは CGF の材質に落ちるので、入れても結果は同じ)
    //   look_soc(path, data)    … socpak (Data.p4k の中のパスの小文字) ごとの、置き方の通し番号 (SocpakExporter.Export の looks の番号) ごとの指定。
    //                             data = Brotli( int32 版 LookVersion, int32 数 n, int32 パレット[n] (look_pal.id。0 = 無し), int16 スウィズル[n] (0 = 無し), int32 材質[n] (look_mtl.id。0 = 無し) )。
    //                             指定が 1 つも無い socpak は行を作らない
    // 置き方ごとの決め方 (調査担当の調べ。+172・+170 の意味とスウィズルの向きは推測):
    //   区画の .soc の IncludedObjects のオブジェクト (184 バイト版): パレット = +172 の番号のパレットの名前、材質 = +170 の番号の材質の名前に .mtl を付けたもの。
    //   Entity: パレット = 子の RenderProxy の Palette、無ければ EntityClassGUID の record の Components[SGeometryResourceParams].Geometry.Geometry.Palette (RootRecord・ChildPath・SwizzleOverride)。
    //   名前の末尾の <xyz> はスウィズル (primary・secondary・tertiary が entry x・y・z を取る)、「|」の後ろは子のパレットの名前 (SocpakScene.PaletteName)
    public const int LookVersion = 1;
    public sealed class LookBuildStats
    {
        public int Socpaks, SocpaksFailed, SocpaksWithLooks, Placements, WithPalette, WithMtl, Palettes, PalettesMissing, Mtls, MtlsMissing; public long Bytes; public double Seconds;
    }
    public sealed class LookData
    {
        public List<(int Id, string File, float[]? Rgb)> Pals = new();   // look_pal に足す行
        public List<(int Id, string Path)> Mtls = new();                  // look_mtl に足す行
        public List<(string Path, byte[] Data)> Socs = new();             // look_soc に足す行
        public List<string> MissingMtls = new();                          // Data.p4k に無かった差し替えの材質 (入れていない)
        public LookBuildStats Stats = new();
    }
    // DB にすでにある行 (PutLayoutDb で足すとき。番号はこれに続ける)
    public sealed class LookExisting
    {
        public Dictionary<string, int> Pal = new(StringComparer.Ordinal);
        public Dictionary<string, int> Mtl = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Socs = new(StringComparer.Ordinal);
    }
    public static LookBuildStats? LookStats { get; private set; }   // 直近の一括で求めた数

    internal static void CreateLookTables(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        Exec(c, "CREATE TABLE IF NOT EXISTS look_pal(id INTEGER PRIMARY KEY, file TEXT NOT NULL UNIQUE, rgb BLOB)");
        Exec(c, "CREATE TABLE IF NOT EXISTS look_mtl(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE)");
        Exec(c, "CREATE TABLE IF NOT EXISTS look_soc(path TEXT PRIMARY KEY, data BLOB NOT NULL) WITHOUT ROWID");
    }

    // socpaks: 場所の socpak (ObjectContainers の下の相対パスの小文字。LayoutInst.Socpak)。それに当たる .socpak を全部読む。
    // 一括では前の DB は使わず、毎回全部求める (担当が決めたもの): パレットの色とエンティティの record のパレットは Game2.dcb で決まり、socpak のファイルが同じでもパッチで変わりうるため。
    // existing: DB にすでにある行 (null なら空の DB に全部書く)。あれば、行のある socpak は求めず、パレット・材質の番号はすでにあるものを使ってその後に続ける。
    // socpak の読み取りは並べる (parallel)。DataCore を引く所 (パレットの色・エンティティの record) は 1 本で順に
    internal static LookData BuildLooks(string p4kPath, IEnumerable<string> socpaks, int parallel, CancellationToken ct, LookExisting? existing = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ctx = SocpakExporter.Shared(p4kPath, 1);
        var p4k = ctx.P4k;
        var files = socpaks.Distinct(StringComparer.Ordinal).SelectMany(sp => MatchSocpaksCached(p4k, sp.Replace('/', '\\')))
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(f => existing == null || !existing.Socs.Contains(f.ToLowerInvariant()))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var raws = new List<SocpakExporter.RawLook>?[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, i =>
        {
            try { raws[i] = SocpakExporter.LoadRawLooks(p4k, files[i]); }
            catch (Exception e) when (e is InvalidDataException or IOException or SocpakExporter.SocpakException or ZstdSharp.ZstdException) { raws[i] = null; }
        });
        var d = new LookData(); var st = d.Stats; st.Socpaks = files.Count;
        var palId = new Dictionary<string, int>(existing?.Pal ?? new Dictionary<string, int>(), StringComparer.Ordinal);
        int nextPal = palId.Count > 0 ? palId.Values.Max() + 1 : 1;
        int Pal(string key)
        {
            if (palId.TryGetValue(key, out var id)) return id;
            float[]? flat = null;
            if (ctx.PaletteColors(key) is { Length: 4 } c4 && c4.All(x => x is { Length: 3 })) flat = c4.SelectMany(x => x).ToArray();
            else st.PalettesMissing++;
            id = nextPal++; d.Pals.Add((id, key, flat)); palId[key] = id;
            return id;
        }
        var mtlId = new Dictionary<string, int>(existing?.Mtl ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
        int nextMtl = mtlId.Count > 0 ? mtlId.Values.Max() + 1 : 1;
        var mtlRaw = new Dictionary<string, int>(StringComparer.Ordinal);
        int Mtl(string raw)
        {
            if (mtlRaw.TryGetValue(raw, out var id)) return id;
            var p = SocpakExporter.DataCorePathToP4k(raw);
            if (!p.EndsWith(".mtl", StringComparison.OrdinalIgnoreCase)) p += ".mtl";
            int e = p4k.Find(p);
            if (e < 0) { id = 0; st.MtlsMissing++; d.MissingMtls.Add(raw); }
            else
            {
                var name = p4k.Name(e);
                if (!mtlId.TryGetValue(name, out id)) { id = nextMtl++; d.Mtls.Add((id, name)); mtlId[name] = id; }
            }
            return mtlRaw[raw] = id;
        }
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (raws[i] is not { } r) { st.SocpaksFailed++; continue; }
            int n = r.Count;
            var pal = new int[n]; var swz = new short[n]; var mt = new int[n];
            bool any = false;
            for (int k = 0; k < n; k++)
            {
                var x = r[k];
                if (x.Palette is string pn) { var (f, s) = SocpakScene.PaletteName(pn); pal[k] = Pal(f); swz[k] = (short)s; }
                else if (x.EntityGuid is string g && ctx.GuidPalette(g) is { } gp) { pal[k] = Pal(gp.File); swz[k] = (short)gp.Swz; }
                if (x.Mtl is string mp) mt[k] = Mtl(mp);
                if (pal[k] != 0) st.WithPalette++;
                if (mt[k] != 0) st.WithMtl++;
                if (pal[k] != 0 || mt[k] != 0) any = true;
            }
            st.Placements += n;
            if (!any) continue;
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            using (var bw = new BinaryWriter(z))
            {
                bw.Write(LookVersion); bw.Write(n);
                foreach (var v in pal) bw.Write(v);
                foreach (var v in swz) bw.Write(v);
                foreach (var v in mt) bw.Write(v);
            }
            d.Socs.Add((files[i].ToLowerInvariant(), ms.ToArray()));
        }
        st.SocpaksWithLooks = d.Socs.Count; st.Palettes = d.Pals.Count; st.Mtls = d.Mtls.Count;
        st.Bytes = d.Socs.Sum(x => (long)x.Data.Length + x.Path.Length) + d.Pals.Sum(x => (long)x.File.Length + 48) + d.Mtls.Sum(x => (long)x.Path.Length);
        st.Seconds = sw.Elapsed.TotalSeconds;
        return d;
    }

    // replace: true なら look_* を全部消してから書く (一括)。false なら足すだけ (PutLayoutDb。同じ socpak の行は置き換える)
    internal static void WriteLooks(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, LookData d, bool replace = true)
    {
        if (replace)
            foreach (var t in new[] { "look_pal", "look_mtl", "look_soc" })
            { using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = $"DELETE FROM {t}"; del.ExecuteNonQuery(); }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO look_pal(id, file, rgb) VALUES($i, $f, $b)";
            var pi = cmd.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Integer); var pf = cmd.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Text); var pb = cmd.Parameters.Add("$b", Microsoft.Data.Sqlite.SqliteType.Blob);
            foreach (var (id, file, rgb) in d.Pals)
            {
                pi.Value = id; pf.Value = file;
                if (rgb != null) { var b = new byte[rgb.Length * 4]; Buffer.BlockCopy(rgb, 0, b, 0, b.Length); pb.Value = b; } else pb.Value = DBNull.Value;
                cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO look_mtl(id, path) VALUES($i, $p)";
            var pi = cmd.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Integer); var pp = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var (id, path) in d.Mtls) { pi.Value = id; pp.Value = path; cmd.ExecuteNonQuery(); }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT OR REPLACE INTO look_soc(path, data) VALUES($p, $d)";
            var pp = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text); var pd = cmd.Parameters.Add("$d", Microsoft.Data.Sqlite.SqliteType.Blob);
            foreach (var (p, b) in d.Socs) { pp.Value = p; pd.Value = b; cmd.ExecuteNonQuery(); }
        }
    }

    // DB にすでにある look_* の行 (PutLayoutDb で足すとき)
    internal static LookExisting ReadLookExisting(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        var e = new LookExisting();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT id, file FROM look_pal";
        using (var r = cmd.ExecuteReader()) while (r.Read()) e.Pal[r.GetString(1)] = r.GetInt32(0);
        cmd.CommandText = "SELECT id, path FROM look_mtl";
        using (var r = cmd.ExecuteReader()) while (r.Read()) e.Mtl[r.GetString(1)] = r.GetInt32(0);
        cmd.CommandText = "SELECT path FROM look_soc";
        using (var r = cmd.ExecuteReader()) while (r.Read()) e.Socs.Add(r.GetString(0));
        return e;
    }

    // look_* の中身 → socpak のパス (SocpakScene.Container.SocpakPath と同じ Data.p4k の中のパス) → 置き方の通し番号ごとの見た目 (無い socpak は null)
    private static Func<string, SocpakScene.Look[]?> LooksFrom(Dictionary<int, float[][]> pals, Dictionary<int, string> pkey, Dictionary<int, string> mtls, Dictionary<string, byte[]> socs)
    {
        var tints = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), SocpakScene.Tint?>();
        SocpakScene.Tint? TintOf(int id, int swz) => id == 0 ? null : tints.GetOrAdd((id, swz), k => pals.TryGetValue(k.Item1, out var c) ? SocpakScene.MakeTint(c, k.Item2, pkey[k.Item1]) : null);
        SocpakScene.Look[]? Decode(byte[] b)
        {
            try
            {
                using var ms = new MemoryStream(b); using var z = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var o = new MemoryStream(); z.CopyTo(o); o.Position = 0;
                using var br = new BinaryReader(o);
                if (br.ReadInt32() != LookVersion) return null;
                int n = br.ReadInt32();
                if (n < 0 || (long)n * 10 + 8 != o.Length) throw new LooksReadException($"look_soc の中身の長さが合いません ({o.Length} バイト・置き方 {n})", null);
                var pal = new int[n]; var swz = new short[n];
                for (int k = 0; k < n; k++) pal[k] = br.ReadInt32();
                for (int k = 0; k < n; k++) swz[k] = br.ReadInt16();
                var res = new SocpakScene.Look[n];
                for (int k = 0; k < n; k++) { int m = br.ReadInt32(); res[k] = new SocpakScene.Look(TintOf(pal[k], swz[k]), m != 0 && mtls.TryGetValue(m, out var mp) ? mp : null); }
                return res;
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException) { throw new LooksReadException($"look_soc を読めませんでした: {e.Message}", e); }
        }
        return sp => socs.TryGetValue(sp.ToLowerInvariant(), out var b) ? Decode(b) : null;
    }

    // 一括の途中 (DB に書く前) の look_* の中身から (補う面を求めるときに、描く形と同じ材質で形を作るため)
    internal static Func<string, SocpakScene.Look[]?> LooksOf(LookData d)
    {
        var pals = new Dictionary<int, float[][]>(); var pkey = new Dictionary<int, string>();
        foreach (var (id, file, rgb) in d.Pals) { pkey[id] = file; if (rgb is { Length: 12 } f) pals[id] = [f[0..3], f[3..6], f[6..9], f[9..12]]; }
        return LooksFrom(pals, pkey, d.Mtls.ToDictionary(x => x.Id, x => x.Path), d.Socs.ToDictionary(x => x.Path, x => x.Data, StringComparer.Ordinal));
    }

    // LooksOf が DB を読もうとして失敗した (2026-11-05、親の決定: 「表が無い・版が古い」(null = パレット・差し替えなし) と分け、その地点の取り出しを失敗にする。
    // OwnExtractor の catch (IOException など → 区画なし) に当たらない型なので、BuildRegionAsync から出て「地点を作れませんでした」になり、region-cache に保存されない)
    public sealed class LooksReadException(string message, Exception? inner) : Exception(message, inner);

    // Data.p4k (p4kPath) の今の印 (区画の置き方の一覧の DB の stamp と比べるもの。StationView3DLayout.GameKey と同じ。P4kStamp は Ship3DExports.CurrentStamp と同じ作り)。読めなければ null
    internal static string? LayoutKeyOf(string p4kPath)
    {
        try
        {
            var fi = new FileInfo(p4kPath);
            return fi.Exists ? StationView3DLayout.GameKey(new Ship3DExports.P4kStamp(fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // 描くとき: DB (dbPath) の look_* 表から (LooksFrom)。
    // DB が無い・表が無い (版 6 以前の DB)・今の版 (LayoutDbFormat) でない・今の Data.p4k (p4kPath) の印 (LayoutKeyOf) で作った DB でなければ null (パレット・差し替えなし = 今の色のまま)。
    // (2026-11-05、親の決定「DB が今の版・今のパッチのものかを見る」。LayoutDbIsCurrent と同じ確かめ。PutLayoutDb は今の印の DB にだけ足すので、足した行はそのまま使う)
    // 読もうとして例外が出たら LooksReadException (null にしない)。表は DB のファイルの更新時刻と大きさ・Data.p4k の印が変わるまでメモリに持つ (socpak ごとの中身は引くたびに戻す)
    private static readonly object LooksLock = new();
    private static string _looksStamp = "";
    private static Func<string, SocpakScene.Look[]?>? _looks;
    public static Func<string, SocpakScene.Look[]?>? LooksOf(string dbPath, string p4kPath)
    {
        lock (LooksLock)
        {
            try
            {
                var fi = new FileInfo(dbPath);
                if (!fi.Exists) { _looksStamp = ""; return _looks = null; }
                var key = LayoutKeyOf(p4kPath) ?? "";
                var stamp = dbPath + "|" + fi.LastWriteTimeUtc.Ticks + "|" + fi.Length + "|" + key;
                if (stamp == _looksStamp) return _looks;
                var pals = new Dictionary<int, float[][]>();
                var pfile = new Dictionary<int, string>();
                var mtls = new Dictionary<int, string>();
                var socs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                using (var c = OpenDb(dbPath, false))
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('look_pal', 'look_mtl', 'look_soc', 'meta')";
                    if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) < 4) { _looksStamp = stamp; return _looks = null; }
                    // 版と Data.p4k の印 (LayoutDbIsCurrent と同じ。meta の format・stamp)
                    cmd.CommandText = "SELECT key, value FROM meta WHERE key IN ('format', 'stamp')";
                    string fm = "", sm = "";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) { if (r.GetString(0) == "format") fm = r.GetString(1); else sm = r.GetString(1); }
                    if (fm != LayoutDbFormat || key.Length == 0 || sm != key) { _looksStamp = stamp; return _looks = null; }
                    cmd.CommandText = "SELECT id, file, rgb FROM look_pal";
                    using (var r = cmd.ExecuteReader())
                        while (r.Read())
                        {
                            pfile[r.GetInt32(0)] = r.GetString(1);
                            if (r.IsDBNull(2)) continue;
                            var b = (byte[])r[2];
                            if (b.Length != 48) continue;
                            var f = new float[12]; Buffer.BlockCopy(b, 0, f, 0, 48);
                            pals[r.GetInt32(0)] = [f[0..3], f[3..6], f[6..9], f[9..12]];
                        }
                    cmd.CommandText = "SELECT id, path FROM look_mtl";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) mtls[r.GetInt32(0)] = r.GetString(1);
                    cmd.CommandText = "SELECT path, data FROM look_soc";
                    using (var r = cmd.ExecuteReader()) while (r.Read()) socs[r.GetString(0)] = (byte[])r[1];
                }
                _looks = LooksFrom(pals, pfile, mtls, socs);
                _looksStamp = stamp;
                return _looks;
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidCastException)
            {
                _looksStamp = ""; _looks = null;
                throw new LooksReadException($"view3d_layout.db の場所のパレット・材質の差し替え (look_*) を読めませんでした: {e.Message}", e);
            }
        }
    }

    // ===== 作る (2026-11-04、利用者の決定「Data.p4k から見る所の区域だけを、開いたときにその場で取り出して描く（前もってデータを作らない）」) =====
    // 開いている間だけの 1 か所分 (窓ごとに 1 つ)。区画の置き方の一覧・地図の JSON・訳を持ち、地点ごとに BuildRegionAsync で区画を取り出して作る。三角形をファイルに保存して使い回すことはしない
    // (2026-11-04、利用者の指示「同じ区域は 1 回だけ取り出して、置かれた所ごとに使い回します… 容量制限を考えないと」)
    //   取り出したもの (socpak ごとの部品の三角形) は、窓を開いている間メモリに持ち、別の地点でも使い回す。持つのは CacheCapBytes まで。
    //   超えたら、いちばん長く使っていないものから捨てる。捨てた後にまた要れば取り出し直す。ディスクには持たない (GLB は読んだらすぐ消す)
    public const long CacheCapBytes = 1L << 30;   // 1 GB (値は担当が決めたもの)
    public sealed class Session
    {
        public List<LayoutInst> Layout { get; }
        private readonly List<LayoutTerminal> _extraTerminals;
        public Loc Loc { get; }
        private readonly MapData _map;
        private readonly long _cap;
        private readonly Func<string, string>? _terminalLabel;
        private readonly Func<string, string, string>? _shopLabel;
        private readonly List<double[]> _allShopPos;
        private readonly string _texUrlBase;
        // 画像の一覧 (この 1 か所分で出てきた画像。番号は増えるだけなので、使い回した区画の形 (GeoCalc) の番号も合う)。region.json の textures に全部書く
        private readonly object _texLock = new();
        private readonly Dictionary<string, int> _texIndex = new(StringComparer.Ordinal);
        private readonly List<string> _texKeys = new();
        private int TexIndex(string key) { lock (_texLock) { if (!_texIndex.TryGetValue(key, out var i)) { i = _texKeys.Count; _texKeys.Add(key); _texIndex[key] = i; } return i; } }
        // terminalLabel: 端末の種類 (fleet など) → 画面に出す名前 (アプリの地図の端末の名前 StationTerminalKinds.Label。ゲームの訳に合わせたもの)
        // shopLabel: 店 (CryGUID, Entity 名) → アプリの地図の店の名前 (StationMapPanel の店の名札と同じ「店名（種類）」。名前が無ければ "")
        // texUrlBase: 画像 (PNG) をビューアが読む URL の頭 (仮想ホストの /stations3d_textures/。region.json の textures の url = これ + ファイル名)
        public Session(Layout layout, string mapJson, Loc loc, long cacheCapBytes = CacheCapBytes, Func<string, string>? terminalLabel = null, Func<string, string, string>? shopLabel = null, string texUrlBase = "")
        {
            Layout = layout.Instances; _extraTerminals = layout.Terminals; Loc = loc; _map = mapJson.Length > 0 ? ReadMap(mapJson) : MapOf(layout); _cap = cacheCapBytes; _terminalLabel = terminalLabel; _shopLabel = shopLabel;
            _allShopPos = _map.Shops.Select(s => s.Pos).ToList();
            _texUrlBase = texUrlBase;
        }

        // 使い回しの置き場 (いちばん長く使っていないものから捨てる。全部で _cap まで):
        //   "g:<socpak>" = 区画の形 (取り出した部品・ファイルに書く点と番号)、"i:<インスタンス>" = 置かれた所ごとの判定 (色・輪郭・部品の箱・店のまわりの三角形)。
        //   (2026-11-04、親の決定「地点を変えても、同じ区画の判定を 1 GB の上限の中で使い回す」)
        private readonly object _cacheLock = new();
        private readonly Dictionary<string, (object Value, long Bytes, LinkedListNode<string> Node)> _cache = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _lru = new();
        private readonly HashSet<string> _evicted = new(StringComparer.Ordinal);
        private long _cacheBytes;
        public long CacheBytes { get { lock (_cacheLock) return _cacheBytes; } }
        private T? CacheGet<T>(string key) where T : class
        {
            lock (_cacheLock)
            {
                if (!_cache.TryGetValue(key, out var e)) return null;
                _lru.Remove(e.Node); _lru.AddFirst(e.Node);
                return (T)e.Value;
            }
        }
        // 置く。上限を超えたら古いものから捨てる。捨てた数を返す (1 つで上限を超えるものは置かない)
        private int CachePut(string key, object value, long bytes, out bool capHit)
        {
            int ev = 0; capHit = false;
            lock (_cacheLock)
            {
                if (_cache.ContainsKey(key)) return 0;
                if (bytes > _cap) { capHit = true; return 0; }
                while (_cacheBytes + bytes > _cap && _lru.Last is { } last)
                {
                    capHit = true;
                    var k = last.Value; _lru.RemoveLast();
                    _cacheBytes -= _cache[k].Bytes; _cache.Remove(k); _evicted.Add(k); ev++;
                }
                _cache[key] = (value, bytes, _lru.AddFirst(key));
                _cacheBytes += bytes;
            }
            return ev;
        }
        private bool WasEvicted(string key) { lock (_cacheLock) return _evicted.Contains(key); }

        // 場所の全体の情報 (区画の形は無い): 案内するもの・乗り場・前の地図の階の名前。ビューアの行き先の一覧と、地点のフロアの名前・道案内に使う
        public byte[] StationJson() => WriteStationJson(_map, Loc, _terminalLabel, _extraTerminals, _shopLabel, LayoutCenter());
        // 区画を置いた所 (一覧の箱) 全部の真ん中
        private double[] LayoutCenter()
        {
            if (!Layout.Any(i => i.Lo != null && i.Hi != null)) return [0, 0, 0];
            var lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; var hi = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (var i in Layout) if (i.Lo != null && i.Hi != null) for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], i.Lo[a]); hi[a] = Math.Max(hi[a], i.Hi[a]); }
            return [(lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, (lo[2] + hi[2]) / 2];
        }

        // 地点 (中心 center・±r m の立方体) の区画を取り出して outDir に書く (region.json と g\NNNN.bin)。
        // 区画の選び方: 置き方の一覧の箱 (socpak の頭の箱の 8 隅を場所の座標にした箱) が立方体に掛かるインスタンス。
        //   箱の無いインスタンスは、原点が立方体の中にあるもの。同じ socpak は 1 回だけ取り出し、置かれた所ごとに判定を計算する (利用者の指示「同じ socpak は 1 回」)。
        //   取り出したもの・計算した判定は、別の地点でも使い回す (上の置き場)。ビューアはその中から、部品の箱が立方体に掛かるインスタンス (試験の頁の選び方) の部品を全部描く。
        // extract: 区画の取り出し (OwnExtractor)。取り出せない・形が無ければ null。
        // acquire: 取り出しを 1 本始めてよいか (同時数の上限。返した IDisposable で返す)。progress: (済んだ socpak, 全部の socpak)
        public async Task<Stats> BuildRegionAsync(double[] center, double r, string outDir,
            Extractor extract, Func<CancellationToken, Task<IDisposable>> acquire, IProgress<(int Done, int Total)>? progress, CancellationToken ct, bool palette = true)
        {
            var st = new Stats();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool Near(double[] q) => Math.Abs(q[0] - center[0]) <= r && Math.Abs(q[1] - center[1]) <= r && Math.Abs(q[2] - center[2]) <= r;
            var insts = new List<LayoutInst>();
            foreach (var inst in Layout)
            {
                if (inst.Lo is { } lo && inst.Hi is { } hi)
                {
                    bool hit = true;
                    for (int k = 0; k < 3; k++) if (lo[k] > center[k] + r || hi[k] < center[k] - r) hit = false;
                    if (hit) insts.Add(inst);
                }
                else if (Near([inst.A[3], inst.A[7], inst.A[11]])) insts.Add(inst);
            }
            st.Instances = insts.Count;
            var bySoc = insts.GroupBy(i => i.Socpak, StringComparer.Ordinal).ToList();
            st.Socpaks = bySoc.Count;
            // 始める順: 置き方の一覧の箱 (socpak の頭の箱) の大きい区画から (2026-11-04、利用者の指摘「11/12 が長い」: 大きな区画が最後に残らないように)。
            // ファイルの番号 (geoIdx) は元の並びのままなので、出来るものは並びに依らない
            static double Vol(LayoutInst i) => i.Lo is { } a && i.Hi is { } b ? (b[0] - a[0]) * (b[1] - a[1]) * (b[2] - a[2]) : 0;
            var order = Enumerable.Range(0, bySoc.Count).OrderByDescending(gi => bySoc[gi].Max(Vol)).ToList();
            var tmp = outDir;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(Path.Combine(tmp, "g"));
            // 地点の中の店 (試験の頁の d['shops']: 店の印が立方体の中)。店のまわりの三角形は、場所の全部の店について持つ (使い回すため。店の入口の結果は同じ: 計算の説明)
            var shops = _map.Shops.Where(s => Near(s.Pos)).ToList();
            var pbox = new List<PartBox>();
            var nearTris = new List<NearPart>();
            var instRec = new Dictionary<int, (int Geo, double[] A, double[] Lo, double[] Hi)>();
            var missing = new List<string>();
            var lockObj = new object();
            int done = 0;
            double exportSec = 0, computeSec = 0;
            progress?.Report((0, bySoc.Count));
            var budget = new Budget();
            var tasks = new List<Task>();
            try
            {
                foreach (var gi in order)
                {
                    var g = bySoc[gi];
                    var geoIdx = gi;
                    // 区画の形 (画像の番号を含む) は、色付けを切った取り出し (palette: false。extract も色付けなしのもの) を別の鍵で持つ (形・箱・判定は同じなので b:・i: は共通)
                    var gkey = (palette ? "g:" : "g:nopal:") + g.Key;
                    var geo = CacheGet<GeoCalc>(gkey);
                    // 部品の箱しか持っていない区画 (前の地点で、どの部品も地点に掛からなかったもの) で、この地点でもどの部品も掛からなければ、取り出さない
                    var bkey = "b:" + g.Key;
                    if (geo == null && CacheGet<double[][]>(bkey) is { } pb0 && !g.Any(inst => PartsHit(pb0, inst.A, center, r)))
                    {
                        lock (lockObj) { st.SkippedNoHit++; done++; }
                        progress?.Report((done, bySoc.Count));
                        continue;
                    }
                    // 区画の形が置き場に無いときだけ、取り出しの枠を取って取り出す
                    var slot = geo == null ? await acquire(ct) : null;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            double tE = 0, tG = 0;
                            if (geo == null)
                            {
                                var swE = System.Diagnostics.Stopwatch.StartNew();
                                var src = await extract(g.Key, ct);
                                tE = swE.Elapsed.TotalSeconds;
                                lock (lockObj) { exportSec += tE; st.Extracted++; if (WasEvicted(gkey)) st.ReExtracted++; }
                                slot?.Dispose(); slot = null;
                                if (src is not { Count: > 0 }) { lock (lockObj) missing.Add(g.Key); return; }
                                var swG = System.Diagnostics.Stopwatch.StartNew();
                                // どの置かれた所も、部品の箱が地点に掛からなければ、形を作らない (ビューアは、部品の箱が地点に掛かる区画だけを描くので、描くものは同じ)。
                                // 部品の箱だけを置き場に持ち、次の地点で掛かるかを見る
                                var pb = PartBoxesLocal(src);
                                if (!g.Any(inst => PartsHit(pb, inst.A, center, r)))
                                {
                                    var evb = CachePut(bkey, pb, pb.Length * 48L + 64, out var capB);
                                    lock (lockObj) { computeSec += swG.Elapsed.TotalSeconds; st.SkippedNoHit++; st.Evicted += evb; st.CapHit |= capB; }
                                    return;
                                }
                                geo = BuildGeo(src, TexIndex);
                                tG = swG.Elapsed.TotalSeconds;
                                var ev = CachePut(gkey, geo, geo.Bytes, out var capHit);
                                lock (lockObj) { computeSec += tG; st.Evicted += ev; st.CapHit |= capHit; }
                            }
                            else lock (lockObj) st.CacheHits++;
                            var swC = System.Diagnostics.Stopwatch.StartNew();
                            var file = Path.Combine(tmp, "g", $"{geoIdx:D4}.bin");
                            var calcs = new List<(LayoutInst Inst, InstCalc Calc)>();
                            var gpb = geo.PartBoxes;
                            foreach (var inst in g)
                            {
                                if (!PartsHit(gpb, inst.A, center, r)) { lock (lockObj) st.InstNoHit++; continue; }
                                var ikey = "i:" + inst.Index.ToString(CultureInfo.InvariantCulture);
                                var ic = CacheGet<InstCalc>(ikey);
                                if (ic == null)
                                {
                                    var took = budget.Take(geo.Parts.Max(p => (long)p.N), ct);
                                    try { ic = ComputeInst(geo, inst, _allShopPos, ct); }
                                    finally { budget.Give(took); }
                                    var ev = CachePut(ikey, ic, ic.Bytes, out var capHit);
                                    lock (lockObj) { st.Evicted += ev; st.CapHit |= capHit; st.InstComputed++; }
                                }
                                else lock (lockObj) st.InstReused++;
                                calcs.Add((inst, ic));
                            }
                            if (calcs.Count == 0) return;
                            var bytes = WriteSocpakFile(file, geo, calcs);
                            lock (lockObj)
                            {
                                computeSec += swC.Elapsed.TotalSeconds;
                                st.PerSocpak.Add((g.Key, tE, tG, swC.Elapsed.TotalSeconds, sw.Elapsed.TotalSeconds, (long)geo.Parts.Sum(p => p.N) * g.Count()));
                                st.Bytes += bytes;
                                st.Parts += geo.Parts.Count * g.Count();
                                st.Triangles += (long)geo.Parts.Sum(p => p.N) * g.Count();
                                foreach (var (inst, ic) in calcs)
                                {
                                    pbox.AddRange(ic.Boxes);
                                    nearTris.AddRange(ic.Near);
                                    instRec[inst.Index] = (geoIdx, inst.A, ic.Lo, ic.Hi);
                                }
                            }
                        }
                        finally
                        {
                            slot?.Dispose();
                            var d = Interlocked.Increment(ref done);
                            progress?.Report((d, bySoc.Count));
                        }
                    }, ct));
                }
                await Task.WhenAll(tasks);
                ct.ThrowIfCancellationRequested();
                st.Missing = missing.Count;
                st.ExportSeconds = exportSec;
                st.ComputeSeconds = computeSec;
                st.CacheBytes = CacheBytes;
                var geos = Enumerable.Range(0, bySoc.Count).Select(gi => File.Exists(Path.Combine(tmp, "g", $"{gi:D4}.bin")) ? $"g/{gi:D4}.bin" : "").ToList();
                // 店の入口 (view_build6.py の entrances。地点の中の店と、地点の区画の部品で)
                var swN = System.Diagnostics.Stopwatch.StartNew();
                var entrances = Entrances(_map, shops, pbox, nearTris);
                var counters = Counters(shops, pbox);
                st.ComputeSeconds += swN.Elapsed.TotalSeconds;
                // 画像の一覧 (区画のファイルの parts の画像の番号 → ここの番号)。url は仮想ホストの PNG、w・h は画素、repeat は UV の繰り返し (材質の UVTiling。u・v 同じ)、
                // tint は材質の色合い (層 1 の TintColor。場所のパレットの色を指す材質はパレットの色。0〜1 の r,g,b。ビューアは map × color)。
                // color: true は画像なしの色だけの項目 (url は ""。パレットの色を指す、画像の無い面。ビューアは tint の色で描く) (2026-11-05)
                List<string> texKeys; lock (_texLock) texKeys = _texKeys.ToList();
                var textures = texKeys.Select(k => StationView3DTextures.InfoOf(k) is { } ti
                    ? new { url = ti.File.Length > 0 ? _texUrlBase + ti.File : "", w = ti.W, h = ti.H, repeat = new[] { ti.Tiling, ti.Tiling }, tint = ti.Tint, color = ti.File.Length == 0 }
                    : new { url = "", w = 0, h = 0, repeat = new[] { 1f, 1f }, tint = new[] { 1f, 1f, 1f }, color = false }).ToList();
                var json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    format = Format,
                    center,
                    r,
                    geos,
                    textures,
                    // [インスタンスの番号, socpak のファイルの番号, 行列 (3×4), 箱の下, 箱の上, socpak]
                    instances = insts.Where(i => instRec.ContainsKey(i.Index)).Select(i => { var x = instRec[i.Index]; return new object[] { i.Index, x.Geo, x.A, R3(x.Lo), R3(x.Hi), i.Socpak }; }).ToList(),
                    missing,
                    entrances,
                    // 店のカウンターの箱 (店の名前 → [[下 x,y,z], [上 x,y,z]], … 近い順)。ビューアの「カウンターの前」に立つ所
                    counters,
                    // 重力の範囲 [種類, 行列 (範囲の枠 → 場所の座標。3×4 行優先), 箱の大きさ | null, 柱の底の多角形 [[x, y], …] | null, 柱の高さ, 名前]。
                    // 地点に掛かる区画のうち、取り出した (形を使い回したものも含む) 区画のもの
                    gravity = insts.SelectMany(i => GravityCache.TryGetValue(i.Socpak, out var gl) ? gl.Select(g =>
                    {
                        var A = i.A; var E = g.E; var W = new double[12];
                        for (int rr = 0; rr < 3; rr++) for (int cc = 0; cc < 4; cc++) W[rr * 4 + cc] = A[rr * 4] * E[cc] + A[rr * 4 + 1] * E[4 + cc] + A[rr * 4 + 2] * E[8 + cc] + (cc == 3 ? A[rr * 4 + 3] : 0);
                        return new object?[] { g.Kind, W.Select(v => Math.Round(v, 4)).ToArray(), g.Size, g.Poly, g.Height, g.Name };
                    }) : []).ToList(),
                }, JsonOpts);
                File.WriteAllBytes(Path.Combine(tmp, "region.json"), json);
                st.Bytes += json.Length;
            }
            catch
            {
                // 途中で失敗・取り消し: 書きかけの所を消す (動いている回が終わるのを待ってから)
                try { await Task.WhenAll(tasks); } catch { }
                try { Directory.Delete(tmp, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
            st.BuildSeconds = sw.Elapsed.TotalSeconds;
            return st;
        }

        // ===== 場所の全部の大きな物体 (2026-11-06、利用者の指示「距離に関係なく大きなびったいは書いた方がいいのかも」「開けたとこでも150m 以上先は逆に建物など大まかなものが取れた方がいいし」。親の決定) =====
        // 場所の全部の区画 (置き方の一覧の全インスタンス) から、置かれた所の部品の箱 (部品の箱の 8 隅を場所の座標にした箱) のいちばん長い辺が threshold m 以上で、x の幅が 5000 m 未満の部品だけを、
        // 地点と同じ形 (region.json と g\NNNN.bin) で outDir に書く。ビューアの解析の糸が、地点の区画に無い置かれた所の分を、距離に関係なく出す (view3d-worker.js の loadBig)。
        //   区画のファイルの部品は、どこかの置かれた所で境以上になる部品だけ (BuildGeo をその部品だけで通す)。判定の色は付けない (0。ComputeInst を通さない。
        //   その区画に近づけば地点の取り出しで地点の区画に入り、判定の色つきの形に替わる)。textures は書いた時点のこの Session の画像の一覧 (ビューアは大きな物体の分を別に持つ)。
        //   区画は 1 つずつ順に取り出す (pause: 地点を作っている間は待つ。acquire: 取り出しの枠)。部品の箱は使い回しの置き場 "b:" に置き (地点の取り出しと共用)、形は置き場に置かない
        //   (地点の形を押し出さないため)。部品の箱が置き場にあって、どの置かれた所でも境以上の部品が無ければ取り出さない
        public async Task<(int Socpaks, int Kept, int Parts, long Bytes)> BuildBigAsync(double threshold, string outDir,
            Extractor extract, Func<CancellationToken, Task<IDisposable>> acquire, Func<CancellationToken, Task> pause, CancellationToken ct)
        {
            var bySoc = Layout.GroupBy(i => i.Socpak, StringComparer.Ordinal).ToList();
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(Path.Combine(outDir, "g"));
            // 置かれた所 A で、部品の箱 b (socpak の枠) が境以上か
            bool Big(double[] b, double[] A)
            {
                double[] bl = [double.MaxValue, double.MaxValue, double.MaxValue], bh = [double.MinValue, double.MinValue, double.MinValue];
                foreach (var x in new[] { b[0], b[3] }) foreach (var y in new[] { b[1], b[4] }) foreach (var z in new[] { b[2], b[5] })
                    for (int a = 0; a < 3; a++) { var v = A[a * 4] * x + A[a * 4 + 1] * y + A[a * 4 + 2] * z + A[a * 4 + 3]; if (v < bl[a]) bl[a] = v; if (v > bh[a]) bh[a] = v; }
                return Math.Max(bh[0] - bl[0], Math.Max(bh[1] - bl[1], bh[2] - bl[2])) >= threshold && bh[0] - bl[0] < 5000;
            }
            var instRows = new List<object>();
            var geos = new List<string>();
            int kept = 0, parts = 0; long bytes = 0;
            try
            {
                for (int gi = 0; gi < bySoc.Count; gi++)
                {
                    ct.ThrowIfCancellationRequested();
                    var g = bySoc[gi];
                    geos.Add("");
                    var bkey = "b:" + g.Key;
                    if (CacheGet<double[][]>(bkey) is { } pb0 && !g.Any(inst => pb0.Any(b => Big(b, inst.A)))) continue;
                    await pause(ct);
                    IReadOnlyList<SourcePart>? src;
                    using (await acquire(ct)) src = await extract(g.Key, ct);
                    if (src is not { Count: > 0 }) continue;
                    var pb = PartBoxesLocal(src);
                    CachePut(bkey, pb, pb.Length * 48L + 64, out _);
                    var keep = Enumerable.Range(0, src.Count).Where(pi => g.Any(inst => Big(pb[pi], inst.A))).ToList();
                    if (keep.Count == 0) continue;
                    var geo = BuildGeo(keep.Select(pi => src[pi]).ToList(), TexIndex);
                    var nTri = geo.Parts.Sum(p => p.N);
                    var calcs = new List<(LayoutInst Inst, InstCalc Calc)>();
                    foreach (var inst in g)
                    {
                        if (!keep.Any(pi => Big(pb[pi], inst.A))) continue;
                        // 置かれた所の箱 (書いた部品の箱の 8 隅を場所の座標にした箱)
                        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
                        foreach (var b in geo.PartBoxes)
                            foreach (var x in new[] { b[0], b[3] }) foreach (var y in new[] { b[1], b[4] }) foreach (var z in new[] { b[2], b[5] })
                                for (int a = 0; a < 3; a++) { var v = inst.A[a * 4] * x + inst.A[a * 4 + 1] * y + inst.A[a * 4 + 2] * z + inst.A[a * 4 + 3]; lo[a] = Math.Min(lo[a], v); hi[a] = Math.Max(hi[a], v); }
                        calcs.Add((inst, new InstCalc { Lab = new byte[nTri], Lo = lo, Hi = hi }));
                    }
                    if (calcs.Count == 0) continue;
                    geos[gi] = $"g/{gi:D4}.bin";
                    bytes += WriteSocpakFile(Path.Combine(outDir, "g", $"{gi:D4}.bin"), geo, calcs);
                    foreach (var (inst, ic) in calcs) instRows.Add(new object[] { inst.Index, gi, inst.A, R3(ic.Lo), R3(ic.Hi), inst.Socpak });
                    kept++; parts += keep.Count;
                }
                // 画像の一覧 (BuildRegionAsync の textures と同じ書き方)
                List<string> texKeys; lock (_texLock) texKeys = _texKeys.ToList();
                var textures = texKeys.Select(k => StationView3DTextures.InfoOf(k) is { } ti
                    ? new { url = ti.File.Length > 0 ? _texUrlBase + ti.File : "", w = ti.W, h = ti.H, repeat = new[] { ti.Tiling, ti.Tiling }, tint = ti.Tint, color = ti.File.Length == 0 }
                    : new { url = "", w = 0, h = 0, repeat = new[] { 1f, 1f }, tint = new[] { 1f, 1f, 1f }, color = false }).ToList();
                var json = JsonSerializer.SerializeToUtf8Bytes(new { format = Format, big = threshold, geos, textures, instances = instRows }, JsonOpts);
                File.WriteAllBytes(Path.Combine(outDir, "region.json"), json);
                bytes += json.Length;
            }
            catch
            {
                try { Directory.Delete(outDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                throw;
            }
            return (bySoc.Count, kept, parts, bytes);
        }

        // ===== 歩くモードの裏の補い (2026-11-05、利用者の指示「歩くモードで近く 5 m 以内の部品を裏で先に細かい形で調べる 見つけたら DB に入れる」
        // 「そのエリアに荒いので部品がない場合、細かいのでチェック」「床でなく物体で」「床代わりの何かに対応できる。照明とかパネルとかチューブとか」) =====
        // ビューアが知らせた「粗い形に物体 (面) が何も無い升」(cells: 升の中心の場所の座標 (x, y)。1 m の升) と足元の高さ pos[2] ± zTol について:
        //   1. 置き方の一覧の箱 (Lo/Hi) が、升と z 帯に掛かるインスタンスを選ぶ。
        //   2. そのインスタンスの部品の箱 (置き場 "g:<socpak>" の GeoCalc.PartBoxes。無ければ SocpakExporter.Export(ctx1) で部品と箱だけ出す。BuildGeo は通さない) で、升と z 帯に掛かる部品を選ぶ。
        //   3. 細かい形 (LOD0) と粗い形 (LOD1) の両方がある形 (StationFill.HasLodVariant) だけを対象にする。区画に直接置かれた形・補う面の追加の部品 (CgfPath 空) は対象外。
        //   4. 細かい形を読む (SocpakExporter.Shared(p4k, 0) で、その socpak のその部品だけ)。読んだ三角形は state に持ち、部品 (形) ごとに 1 回だけ読む。
        //   5. 細かい形の三角形を場所の座標 (A·M·点) にし、物体の無い升の xy の範囲と z 帯に (三角形の箱で) 掛かるものだけを取る (上向きに限らない。「床でなく物体で」)。
        //   6. 取れた三角形が 1 つでもあれば返す (部品の局所座標の三角形 = DB に入れるもの、場所の座標の三角形 = ビューアに送るもの)。
        //   同じ (インスタンス, 部品, 升) は state で 1 回の 3D の中で二度調べない (親の決定)。失敗 (読めない・形が無い) は log に残して続ける
        public sealed class WalkFillState
        {
            public readonly HashSet<string> Seen = new(StringComparer.Ordinal);                        // 調べた (インスタンス, 部品, 升)
            public readonly Dictionary<string, float[]?> Lod0 = new(StringComparer.Ordinal);           // 形 (Norm) → 細かい形の三角形 (局所。読めなければ null)
            public readonly Dictionary<string, string> Lod0Mtl = new(StringComparer.Ordinal);          // 形 (Norm) → その細かい形の材質 (Mesh.Mtl。補いの印 WalkSig に入れる。2026-11-05)
            public readonly Dictionary<string, bool> HasLod = new(StringComparer.Ordinal);             // 形 (Norm) → 細かい形と粗い形の両方があるか
            public readonly Dictionary<string, WalkPart[]?> Parts = new(StringComparer.Ordinal);       // socpak → 部品 (置き場に GeoCalc が無いときに出したもの。出せなければ null)
        }
        // 部品 1 つ (名前・形のパス・ノードの行列 (4×4 行優先)・socpak の枠の箱 [下 x,y,z, 上 x,y,z])
        public sealed record WalkPart(string Name, string CgfPath, double[] M, double[] Box);
        // 見つかったもの: インスタンスの番号・部品の名前・形 (StationFill.Norm)・補いの印 (StationFill.WalkSig = 形の印 + 細かい形の材質の印。2026-11-05 までは KindSig)・部品の局所座標の三角形 (x9)・場所の座標の三角形 (x9)
        public sealed record WalkFillItem(int Inst, string Name, string Kind, string Sig, float[] LocalTris, float[] WorldTris);

        public List<WalkFillItem> WalkCheck(string p4kPath, double[] pos, double zTol, IReadOnlyList<double[]> cells, WalkFillState state, Action<string>? log, CancellationToken ct)
        {
            var res = new List<WalkFillItem>();
            if (cells.Count == 0) return res;
            var ctx1 = SocpakExporter.Shared(p4kPath, 1);
            double z0 = pos[2] - zTol, z1 = pos[2] + zTol;
            // 升の全部を含む xy の範囲 (升は中心 ±0.5 m)
            double cx0 = cells.Min(c => c[0]) - 0.5, cx1 = cells.Max(c => c[0]) + 0.5, cy0 = cells.Min(c => c[1]) - 0.5, cy1 = cells.Max(c => c[1]) + 0.5;
            foreach (var inst in Layout)
            {
                ct.ThrowIfCancellationRequested();
                // 1. 一覧の箱が升と z 帯に掛かるインスタンス (箱の無いインスタンスは掛かるか分からないので対象外)
                if (inst.Lo is not { } lo || inst.Hi is not { } hi) continue;
                if (lo[0] > cx1 || hi[0] < cx0 || lo[1] > cy1 || hi[1] < cy0 || lo[2] > z1 || hi[2] < z0) continue;
                var parts = WalkPartsOf(inst.Socpak, ctx1, state, log, ct);
                if (parts == null) continue;
                var A = inst.A;
                for (int pi = 0; pi < parts.Length; pi++)
                {
                    var part = parts[pi];
                    if (part.CgfPath.Length == 0) continue;
                    // 2. 部品の箱 (8 隅を場所の座標に) が升と z 帯に掛かるか (PartsHit と同じ変換)
                    var b = part.Box;
                    double[] bl = [double.MaxValue, double.MaxValue, double.MaxValue], bh = [double.MinValue, double.MinValue, double.MinValue];
                    foreach (var x in new[] { b[0], b[3] }) foreach (var y in new[] { b[1], b[4] }) foreach (var z in new[] { b[2], b[5] })
                        for (int a = 0; a < 3; a++) { var v = A[a * 4] * x + A[a * 4 + 1] * y + A[a * 4 + 2] * z + A[a * 4 + 3]; if (v < bl[a]) bl[a] = v; if (v > bh[a]) bh[a] = v; }
                    if (bl[2] > z1 || bh[2] < z0 || bl[0] > cx1 || bh[0] < cx0 || bl[1] > cy1 || bh[1] < cy0) continue;
                    var hit = new List<double[]>();
                    foreach (var c in cells)
                    {
                        if (bl[0] > c[0] + 0.5 || bh[0] < c[0] - 0.5 || bl[1] > c[1] + 0.5 || bh[1] < c[1] - 0.5) continue;
                        // 鍵は (インスタンス, 部品, 升の xy, 足元の z を 1 m で切った数)。別の階に移ったら同じ升も調べ直す (親の決定。ビューアの sentKey と同じ切り方)
                        if (state.Seen.Add(FormattableString.Invariant($"{inst.Index}|{pi}|{c[0]:F1}|{c[1]:F1}|{Math.Floor(pos[2])}"))) hit.Add(c);
                    }
                    if (hit.Count == 0) continue;
                    // 3. 細かい形と粗い形の両方がある形だけ
                    var kind = StationFill.Norm(part.CgfPath);
                    if (!state.HasLod.TryGetValue(kind, out var has)) state.HasLod[kind] = has = StationFill.HasLodVariant(ctx1.P4k, part.CgfPath, 1);
                    if (!has) continue;
                    // 4. 細かい形 (形ごとに 1 回)
                    if (!state.Lod0.TryGetValue(kind, out var t0)) { state.Lod0[kind] = t0 = WalkLod0(p4kPath, inst.Socpak, part.Name, kind, ctx1, log, ct, out var lm); state.Lod0Mtl[kind] = lm; }
                    if (t0 == null) continue;
                    // 5. 場所の座標にして、物体の無い升と z 帯に掛かる三角形だけ
                    var M = part.M;
                    var local = new List<float>(); var world = new List<float>();
                    var wx = new double[3]; var wy = new double[3]; var wz = new double[3];
                    for (int t = 0; t + 9 <= t0.Length; t += 9)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            double x = t0[t + k * 3], y = t0[t + k * 3 + 1], z = t0[t + k * 3 + 2];
                            double sx = M[0] * x + M[1] * y + M[2] * z + M[3], sy = M[4] * x + M[5] * y + M[6] * z + M[7], sz = M[8] * x + M[9] * y + M[10] * z + M[11];
                            wx[k] = A[0] * sx + A[1] * sy + A[2] * sz + A[3]; wy[k] = A[4] * sx + A[5] * sy + A[6] * sz + A[7]; wz[k] = A[8] * sx + A[9] * sy + A[10] * sz + A[11];
                        }
                        double tz0 = Math.Min(wz[0], Math.Min(wz[1], wz[2])), tz1 = Math.Max(wz[0], Math.Max(wz[1], wz[2]));
                        if (tz0 > z1 || tz1 < z0) continue;
                        double tx0 = Math.Min(wx[0], Math.Min(wx[1], wx[2])), tx1 = Math.Max(wx[0], Math.Max(wx[1], wx[2])), ty0 = Math.Min(wy[0], Math.Min(wy[1], wy[2])), ty1 = Math.Max(wy[0], Math.Max(wy[1], wy[2]));
                        bool on = false;
                        foreach (var c in hit) if (tx0 <= c[0] + 0.5 && tx1 >= c[0] - 0.5 && ty0 <= c[1] + 0.5 && ty1 >= c[1] - 0.5) { on = true; break; }
                        if (!on) continue;
                        for (int k = 0; k < 9; k++) local.Add(t0[t + k]);
                        for (int k = 0; k < 3; k++) { world.Add((float)wx[k]); world.Add((float)wy[k]); world.Add((float)wz[k]); }
                    }
                    // 6. 1 つでもあれば返す
                    if (local.Count > 0) res.Add(new WalkFillItem(inst.Index, part.Name, kind, StationFill.WalkSig(ctx1.P4k, kind, state.Lod0Mtl.GetValueOrDefault(kind, "")), local.ToArray(), world.ToArray()));
                }
            }
            return res;
        }

        // インスタンスの socpak の部品 (名前・形のパス・行列・箱)。置き場 ("g:<socpak>") の GeoCalc があればその Parts と PartBoxes。無ければ粗い形を Export して部品と箱だけ出す (state に持つ)
        private WalkPart[]? WalkPartsOf(string socpak, SocpakExporter.Context ctx1, WalkFillState state, Action<string>? log, CancellationToken ct)
        {
            if ((CacheGet<GeoCalc>("g:" + socpak) ?? CacheGet<GeoCalc>("g:nopal:" + socpak)) is { } geo)   // 部品・行列・箱は色付けの有無で同じ
            {
                var arr = new WalkPart[geo.Parts.Count];
                for (int i = 0; i < arr.Length; i++) arr[i] = new WalkPart(geo.Parts[i].Name, geo.Parts[i].CgfPath, geo.Parts[i].M, geo.PartBoxes[i]);
                return arr;
            }
            if (state.Parts.TryGetValue(socpak, out var cached)) return cached;
            WalkPart[]? res = null;
            try
            {
                var list = MatchSocpaksCached(ctx1.P4k, socpak.Replace('/', '\\'));
                if (list.Count == 0) log?.Invoke($"区画が見つかりません ({socpak})");
                else
                {
                    // 材質の差し替えは、描く形 (OwnExtractor) と同じにする (差し替えた材質で隠す submesh が変わるため)
                    var src = PartsOf(SocpakExporter.Export(ctx1, list, ct: ct, looks: LooksOf(Path.Combine(StationPlaces.Folder, LayoutDbName), ctx1.P4k.Path)));
                    var boxes = PartBoxesLocal(src);
                    res = new WalkPart[src.Count];
                    for (int i = 0; i < res.Length; i++) res[i] = new WalkPart(src[i].Name, src[i].CgfPath, src[i].M, boxes[i]);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException)
            {
                log?.Invoke($"区画の部品を出せませんでした ({socpak}): {e.Message}");
            }
            state.Parts[socpak] = res;
            return res;
        }

        // 細かい形 (LOD0) の三角形 (部品の局所座標、x9)。その socpak のその部品 (名前と形のパス) だけを読む (StationFill.Build と同じやり方)。読めない・形が無ければ null
        private static float[]? WalkLod0(string p4kPath, string socpak, string name, string kind, SocpakExporter.Context ctx1, Action<string>? log, CancellationToken ct, out string mtl)
        {
            mtl = "";
            try
            {
                var list = MatchSocpaksCached(ctx1.P4k, socpak.Replace('/', '\\'));
                if (list.Count == 0) { log?.Invoke($"区画が見つかりません ({socpak})"); return null; }
                var ctx0 = SocpakExporter.Shared(p4kPath, 0);
                // 材質の差し替えは描く形と同じ (looks)
                var scene0 = SocpakExporter.Export(ctx0, list, wantMesh: n => n == name, ct: ct, wantCgf: p => StationFill.Norm(p) == kind, looks: LooksOf(Path.Combine(StationPlaces.Folder, LayoutDbName), p4kPath));
                foreach (var c in scene0.Containers)
                    foreach (var pl in c.Placements)
                        if (StationFill.Norm(pl.Mesh.CgfPath) == kind) { mtl = pl.Mesh.Mtl; return StationFill.TrisOf(pl.Mesh); }
                var fail = scene0.FailedMeshes.FirstOrDefault(f => StationFill.Norm(f.CgfPath) == kind);
                log?.Invoke($"細かい形が無い ({name} / {kind} / {socpak}){(fail.Reason is { Length: > 0 } rs ? ": " + rs : "")}");
                return null;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException)
            {
                log?.Invoke($"細かい形を読めませんでした ({name} / {kind} / {socpak}): {e.Message}");
                return null;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    // ===== 地図の JSON =====
    private sealed class MapData
    {
        public List<Transit> Transit = new();
        public List<Stair> Stairs = new();
        public List<OldFloor> Floors = new();
        public List<Shop> Shops = new();
        public List<Terminal> Terminals = new();
        // ハンガーの入口 (exterior.hangars) の class と大きさ (S・M・L・XL)
        public List<(string Class, string Size)> Hangars = new();
        public List<double[]> Nodes = new();
        public int[] Comp = [];
        public HashSet<int> GwComps = new();
        public int[] CompSize = [];
        public string Record = "";
        public Dictionary<string, int> ShopNode = new(StringComparer.OrdinalIgnoreCase);
        // 店が乗り場から行けるか (前の地図の道で、店の節点のまとまりに乗り場があるか。節点が無ければ null)
        public bool? ShopOk(Shop s) => ShopNode.TryGetValue(s.Guid, out var i) && i < Comp.Length ? GwComps.Contains(Comp[i]) : null;
    }

    // 一覧の場所の中身 (PlaceData) から、地図のデータを作る (2D の地図の JSON を使わない形。階・階段・道は無い)
    private static MapData MapOf(Layout layout)
    {
        var m = new MapData();
        if (layout.Place is not { } p) return m;
        foreach (var t in p.Transit) m.Transit.Add(new Transit(t.Kind, t.Pos, "", t.Line, t.Stop));
        foreach (var s in p.Shops) m.Shops.Add(new Shop(s.Pos, s.Entity, s.Guid, ""));
        foreach (var t in p.Terms) m.Terminals.Add(new Terminal(t.Kind, t.Pos, t.Entity, t.Class, t.Yaw, t.ShopEntity, t.ShopGuid, ""));
        foreach (var h in p.Hangars) m.Hangars.Add((h.Class, h.Size));
        return m;
    }

    private static MapData ReadMap(string path)
    {
        var m = new MapData();
        // 地図の無い所 (path が空。試しのサーバーで、地図を取り込んでいないハンガーを描くとき) は空の地図
        if (path.Length == 0) return m;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = doc.RootElement;
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        double[] V(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() >= 3
            ? [v[0].GetDouble(), v[1].GetDouble(), v[2].GetDouble()] : [double.NaN, double.NaN, double.NaN];
        IEnumerable<JsonElement> A(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];
        m.Record = S(r, "record");
        foreach (var t in A(r, "transit")) m.Transit.Add(new Transit(S(t, "kind"), V(t, "pos"), S(t, "floor"), S(t, "line"), S(t, "dest")));
        foreach (var t in A(r, "stairs")) m.Stairs.Add(new Stair(V(t, "bottom"), V(t, "top"), S(t, "from"), S(t, "to"), S(t, "source")));
        var floorZ = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var f in A(r, "floors"))
        {
            var z = f.TryGetProperty("z", out var zv) && zv.ValueKind == JsonValueKind.Number ? zv.GetDouble() : double.NaN;
            m.Floors.Add(new OldFloor(S(f, "id"), S(f, "name"), z));
            floorZ[S(f, "id")] = z;
        }
        if (r.TryGetProperty("exterior", out var exo) && exo.ValueKind == JsonValueKind.Object)
            foreach (var h in A(exo, "hangars")) m.Hangars.Add((S(h, "class"), S(h, "size")));
        foreach (var s in A(r, "shops")) m.Shops.Add(new Shop(V(s, "pos"), S(s, "entity"), S(s, "guid"), S(s, "floor")));
        foreach (var t in A(r, "terminals")) m.Terminals.Add(new Terminal(S(t, "kind"), V(t, "pos"), S(t, "entity"), S(t, "class"), t.TryGetProperty("yaw", out var yv) && yv.ValueKind == JsonValueKind.Number ? yv.GetDouble() : null, S(t, "shopEntity"), S(t, "shop"), S(t, "floor")));
        // 道の点 (StationMapModel.Parse と同じ並び: nodes と waypoints を at の番号で混ぜる) と辺 → まとまり
        if (r.TryGetProperty("graph", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            var objs = A(g, "nodes").ToList();
            var runs = A(g, "waypoints").Select(w => (At: w.GetProperty("at").GetInt32(), Floor: S(w, "floor"),
                Ij: w.GetProperty("ij").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                Z: w.TryGetProperty("z", out var za) && za.ValueKind == JsonValueKind.Array ? za.EnumerateArray().Select(x => x.GetInt32()).ToArray() : Array.Empty<int>()))
                .OrderBy(w => w.At).ToList();
            var total = objs.Count + runs.Sum(w => w.Ij.Length / 2);
            const double c = StationFloorBuilder.Cell;
            var gw = new List<bool>();
            int oi = 0, ri = 0;
            while (m.Nodes.Count < total)
            {
                if (ri < runs.Count && runs[ri].At == m.Nodes.Count)
                {
                    var run = runs[ri++];
                    var fz0 = floorZ.TryGetValue(run.Floor, out var fz) ? fz : 0;
                    for (int k = 0; k < run.Ij.Length / 2; k++)
                    {
                        m.Nodes.Add([(run.Ij[2 * k] + 0.5) * c, (run.Ij[2 * k + 1] + 0.5) * c, k < run.Z.Length ? run.Z[k] / 100.0 : fz0]);
                        gw.Add(false);
                    }
                    continue;
                }
                if (oi >= objs.Count) break;
                var n = objs[oi++];
                // 店の節点 (Id "s:<インスタンス>:<CryGUID>") は CryGUID で引けるようにする (店の玉の色: 乗り場から行けるか)
                if (S(n, "kind") == "shop" && S(n, "id").Split(':') is { Length: >= 3 } sid) m.ShopNode.TryAdd(sid[2], m.Nodes.Count);
                m.Nodes.Add(V(n, "pos"));
                gw.Add(S(n, "kind") == "gateway");
            }
            var par = Enumerable.Range(0, m.Nodes.Count).ToArray();
            int Find(int a) { while (par[a] != a) { par[a] = par[par[a]]; a = par[a]; } return a; }
            void Union(int a, int b) { if (a < 0 || b < 0 || a >= par.Length || b >= par.Length) return; a = Find(a); b = Find(b); if (a != b) par[a] = b; }
            foreach (var e in A(g, "edges"))
                if (e.TryGetProperty("a", out var ea) && e.TryGetProperty("b", out var eb)) Union(ea.GetInt32(), eb.GetInt32());
            if (g.TryGetProperty("wpEdges", out var wpe) && wpe.ValueKind == JsonValueKind.Array)
            {
                var arr = wpe.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                for (int k = 0; k + 2 < arr.Length; k += 3) Union(arr[k], arr[k + 1]);
            }
            var ids = new Dictionary<int, int>();
            m.Comp = new int[m.Nodes.Count];
            for (int i = 0; i < m.Nodes.Count; i++) { var root = Find(i); if (!ids.TryGetValue(root, out var ci)) ids[root] = ci = ids.Count; m.Comp[i] = ci; }
            m.CompSize = new int[ids.Count];
            foreach (var ci in m.Comp) m.CompSize[ci]++;
            for (int i = 0; i < m.Nodes.Count; i++) if (gw[i]) m.GwComps.Add(m.Comp[i]);
        }
        return m;
    }

    // ===== socpak 1 つ =====
    private sealed class PartBox
    {
        public int Inst; public string Key = ""; public string[] Words = []; public double[] Lo = new double[3], Hi = new double[3]; public int PartIdx;
        public string Kind = "other";   // 部品の種類 (Part.Kind。カウンターの部品は "counterpart")
    }
    private sealed class NearPart
    {
        public int Inst; public string Key = ""; public double[] V = []; public double[] Nz = [];
    }
    // 区画の形 (取り出した部品と、ファイルに書く点と番号)。Body はファイルの中身の頭の部分 (点・番号)、PartsJson の位置は Body の頭から
    private sealed class GeoCalc
    {
        public List<Part> Parts = new();
        public double[][] PartBoxes = [];   // 部品ごとの箱 [下 x,y,z, 上 x,y,z] (socpak の枠、0.001 m に丸めたもの。ファイルの parts の箱と同じ値)
        public byte[] Body = [];
        public List<object> PartsJson = new();
        public long Bytes;
    }
    // 置かれた所ごとの判定: 色 (三角形ごとに 1 バイト、部品の順)・輪郭 (色ごとの [部品, 点, 点])・部品の箱・店のまわりの三角形・インスタンスの箱
    private sealed class InstCalc
    {
        public byte[] Lab = [];
        public SortedDictionary<byte, uint[]> Lines = new();
        public List<PartBox> Boxes = new();
        public List<NearPart> Near = new();
        public double[] Lo = [], Hi = [];
        public long Bytes;
    }

    // 部品 1 つ (GLB の子のメッシュ)。三角形はメッシュの値 (float32。同じメッシュの部品で共有) と、socpak の枠へのノードの行列 M (view_build_lib.py と同じく、場所の座標は (A·M)·点)
    private sealed class Part
    {
        public string Name = "", Key = ""; public string[] Words = []; public string Kind = "other";
        public string CgfPath = "";  // 部品の種類 (形のパス。SourcePart.CgfPath。補う面の追加の部品・区画に直接置かれた形は ""。歩くモードの裏の補い (WalkCheck) で使う)
        public float[] Mesh = [];   // メッシュの三角形 (x9)
        public float[]? Uv;         // 三角形の角ごとの UV (x6。無ければ null)。画像を貼るためだけ (判定には使わない)
        public string[]? TexKeys; public int[]? Tex;   // 画像の鍵と、三角形ごとの鍵の番号 (-1 = 画像なし)。SourcePart のまま
        public double[] M = [];     // ノードの行列 (4×4、行優先)
        public int N => Mesh.Length / 9;
        public int[] Idx = [];      // 三角形の角ごとの、部品の点 (ファイルに書く点) の番号
        public bool Small;          // 局所の箱の x か y が 1 m 未満 (床・筒の底にしない)
    }

    // メモリの枠: 同時に計算する大きな部品の三角形の数の合計をこの数までにする (1 つで超えるものは 1 つだけで計算する)。
    // 値は、判定の計算のメモリ (三角形あたり約 300 バイト。場所の座標・まとめた形の作業・筒の底の升目) から、全部で 3〜4 GB ほどに収まるように決めた
    public const long TriBudget = 12_000_000;
    internal sealed class Budget
    {
        private long _avail = TriBudget;
        private readonly object _lock = new();
        public long Take(long want, CancellationToken ct)
        {
            want = Math.Min(Math.Max(want, 1), TriBudget);
            lock (_lock)
            {
                while (_avail < want) { ct.ThrowIfCancellationRequested(); Monitor.Wait(_lock, 500); }
                _avail -= want;
            }
            return want;
        }
        public void Give(long n) { lock (_lock) { _avail += n; Monitor.PulseAll(_lock); } }
    }

    // 区画の形を作る: 部品ごとに、点を float32 (socpak の枠 = M·メッシュの点) にして同じ点を 1 つにまとめ、点と番号 (点が 65536 未満なら 2 バイト) を Body に書く。
    // UV のある部品は、同じ点のまとめの鍵に UV も入れ (位置が同じで UV の違う角は別の点)、点ごとの UV (float32 x2) も書く。
    // texIndex: 画像の鍵 → 地点の一覧 (region.json の textures) の番号。null なら画像の列は書かない
    private static GeoCalc BuildGeo(IReadOnlyList<SourcePart> src, Func<string, int>? texIndex = null)
    {
        var geo = new GeoCalc { Parts = src.Select(ToPart).ToList() };
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        // 部品ごとの点のまとめは並べて動かす (2026-11-04、利用者の指摘「11/12 が長い」。部品どうしは関わらないので、部品の順に書いた結果は 1 本ずつと同じ)
        var pv = new (List<float> Verts, List<float>? Uvs, double[] Lo, double[] Hi)[geo.Parts.Count];
        Parallel.For(0, geo.Parts.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, pi =>
        {
            var p = geo.Parts[pi];
            var vmap = new Dictionary<(float, float, float, float, float), int>();
            var verts = new List<float>();
            var uvs = p.Uv != null ? new List<float>() : null;
            p.Idx = new int[p.N * 3];
            double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
            var M = p.M;
            for (int t = 0; t < p.N * 3; t++)
            {
                double x = p.Mesh[t * 3], y = p.Mesh[t * 3 + 1], z = p.Mesh[t * 3 + 2];
                double X = M[0] * x + M[1] * y + M[2] * z + M[3], Y = M[4] * x + M[5] * y + M[6] * z + M[7], Z = M[8] * x + M[9] * y + M[10] * z + M[11];
                float u = uvs != null ? p.Uv![t * 2] : 0f, v = uvs != null ? p.Uv![t * 2 + 1] : 0f;
                var k = ((float)X, (float)Y, (float)Z, u, v);
                if (!vmap.TryGetValue(k, out var vi)) { vi = vmap.Count; vmap[k] = vi; verts.Add(k.Item1); verts.Add(k.Item2); verts.Add(k.Item3); if (uvs != null) { uvs.Add(u); uvs.Add(v); } }
                p.Idx[t] = vi;
                lo[0] = Math.Min(lo[0], X); hi[0] = Math.Max(hi[0], X); lo[1] = Math.Min(lo[1], Y); hi[1] = Math.Max(hi[1], Y); lo[2] = Math.Min(lo[2], Z); hi[2] = Math.Max(hi[2], Z);
            }
            pv[pi] = (verts, uvs, lo, hi);
        });
        var boxes = new List<double[]>();
        for (int pi = 0; pi < geo.Parts.Count; pi++)
        {
            var p = geo.Parts[pi];
            var (verts, uvs, lo, hi) = pv[pi];
            pv[pi] = default;
            Align4(bw);
            var vOff = ms.Position;
            foreach (var f in verts) bw.Write(f);
            long uOff = -1;
            if (uvs != null) { uOff = ms.Position; foreach (var f in uvs) bw.Write(f); }
            var iOff = ms.Position;
            var i16 = verts.Count / 3 < 65536;
            if (i16) foreach (var i in p.Idx) bw.Write((ushort)i); else foreach (var i in p.Idx) bw.Write((uint)i);
            // 画像の番号 (三角形ごとに int32。地点の一覧の番号。画像の無い三角形は -1)。1 つも無い部品は書かない (-1)
            long tOff = -1;
            if (texIndex != null && p.Tex != null && p.TexKeys != null)
            {
                var map = new int[p.TexKeys.Length];
                for (int k = 0; k < map.Length; k++) map[k] = texIndex(p.TexKeys[k]);
                if (p.Tex.Any(t => t >= 0 && t < map.Length))
                {
                    Align4(bw);
                    tOff = ms.Position;
                    foreach (var t in p.Tex) bw.Write(t >= 0 && t < map.Length ? map[t] : -1);
                }
            }
            // parts の 1 行: [鍵, 種類, 点の位置, 点の数, 番号の位置, 三角形の数, 箱の下, 箱の上, 番号のバイト数, 扉の向き | null, UV の位置 | -1, 画像の番号の位置 | -1]。
            // 11・12 番目 (UV・画像の番号) は 2026-11-05 に足した (画像を貼る)。UV は点ごとの float32 x2、画像の番号は三角形ごとの int32 (地点の region.json の textures の番号)。
            // エレベーターの扉の部品だけ 10 番目に「扉の前 (ロビー側) の向き」= 部品の局所の +Y 軸を socpak の枠に回したもの (M の回転の 2 列目。場所の座標への回し (A) は糸の側)。扉でなければ null
            // (2026-11-05、利用者の指示「指示はドアの向きを見て」。実データで確かめた: Seraphim Station (RR_CRU_LEO) の貨物センターの扉 truck_shaft_door_double_a と
            //  Orison (Stanton2_Orison) の扉 elevator_door_double_a のどちらも、局所の +Y の側に乗り場の床 (hangar_walkway・elev_lobby_a_event_floor) と呼び出しの端末があり、−Y の側は何も無い (昇降路))
            // エレベーターの扉の見分け方 (2026-11-05、親の決定): 名前の語に elevator と door・doors がある、または 名前の語に door・doors があり部品の種類 (CgfPath。小文字・/ 区切り) に "/elev/" を含む
            // (Seraphim の truck_shaft_door_double_a は語に elevator が無く、cga のパス objects/buildingsets/human/lowtech/alpha/int/elev/ で見分ける)。糸 (view3d-worker.js) はこの 10 番目の有無で扉を選ぶ
            var doorWord = p.Words.Contains("door") || p.Words.Contains("doors");
            var door = doorWord && (p.Words.Contains("elevator") || p.CgfPath.Replace('\\', '/').ToLowerInvariant().Contains("/elev/", StringComparison.Ordinal));
            var M4 = p.M;
            geo.PartsJson.Add(new object?[] { p.Key, p.Kind, vOff, verts.Count / 3, iOff, p.N, Round3(lo), Round3(hi), i16 ? 2 : 4,
                door ? new[] { Math.Round(M4[1], 4), Math.Round(M4[5], 4), Math.Round(M4[9], 4) } : null, uOff, tOff });
            var rl = Round3(lo); var rh = Round3(hi);
            boxes.Add([rl[0], rl[1], rl[2], rh[0], rh[1], rh[2]]);
        }
        Align4(bw);
        bw.Flush();
        geo.Body = ms.ToArray();
        geo.PartBoxes = boxes.ToArray();
        geo.Bytes = geo.Body.Length + geo.Parts.Select(p => p.Mesh).Distinct(ReferenceEqualityComparer.Instance).Sum(m => (long)((float[])m!).Length * 4)
            + geo.Parts.Where(p => p.Uv != null).Select(p => p.Uv!).Distinct(ReferenceEqualityComparer.Instance).Sum(u => (long)((float[])u!).Length * 4)
            + geo.Parts.Sum(p => (long)p.Idx.Length * 4 + 16 * 8 + 128);
        return geo;
    }

    // 部品ごとの箱 (BuildGeo の parts の箱と同じ: socpak の枠 (M·メッシュの点) の箱を 0.001 m に丸めたもの)
    private static double[][] PartBoxesLocal(IReadOnlyList<SourcePart> src)
    {
        var res = new double[src.Count][];
        Parallel.For(0, src.Count, pi =>
        {
            var sp = src[pi]; var M = sp.M; var T = sp.Mesh;
            double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue], hi = [double.MinValue, double.MinValue, double.MinValue];
            for (int i = 0; i < T.Length; i += 3)
            {
                double x = T[i], y = T[i + 1], z = T[i + 2];
                double X = M[0] * x + M[1] * y + M[2] * z + M[3], Y = M[4] * x + M[5] * y + M[6] * z + M[7], Z = M[8] * x + M[9] * y + M[10] * z + M[11];
                lo[0] = Math.Min(lo[0], X); hi[0] = Math.Max(hi[0], X); lo[1] = Math.Min(lo[1], Y); hi[1] = Math.Max(hi[1], Y); lo[2] = Math.Min(lo[2], Z); hi[2] = Math.Max(hi[2], Z);
            }
            var rl = Round3(lo); var rh = Round3(hi);
            res[pi] = [rl[0], rl[1], rl[2], rh[0], rh[1], rh[2]];
        });
        return res;
    }

    // 置かれた所 (行列 A) の部品のどれかの箱 (8 隅を場所の座標にした箱) が、地点の立方体に掛かり、その箱の x の幅が 5000 m 未満か
    // (ビューアが描く区画を選ぶ決まりと同じ。試験の頁のデータの作り方 hh VIEW3D と同じ)
    private static bool PartsHit(double[][] boxes, double[] A, double[] c, double r)
    {
        foreach (var b in boxes)
        {
            double[] bl = [double.MaxValue, double.MaxValue, double.MaxValue], bh = [double.MinValue, double.MinValue, double.MinValue];
            foreach (var x in new[] { b[0], b[3] }) foreach (var y in new[] { b[1], b[4] }) foreach (var z in new[] { b[2], b[5] })
                for (int a = 0; a < 3; a++) { var v = A[a * 4] * x + A[a * 4 + 1] * y + A[a * 4 + 2] * z + A[a * 4 + 3]; if (v < bl[a]) bl[a] = v; if (v > bh[a]) bh[a] = v; }
            if (bl[0] <= c[0] + r && bh[0] >= c[0] - r && bl[1] <= c[1] + r && bh[1] >= c[1] - r && bl[2] <= c[2] + r && bh[2] >= c[2] - r && bh[0] - bl[0] < 5000) return true;
        }
        return false;
    }

    // 置かれた所 1 つの判定 (向きと位置で変わるので、view_build6.py と同じく場所の座標で計算する)。
    // 店のまわりの三角形は、場所の全部の店について持つ (地点の中の店だけについて持つのと、店の入口の結果は同じ: 店の入口の直線・床に掛かりうる三角形は、その店から xy で EntDist + 1 m 以内にあり、
    // その店について持つ三角形に入っているため。ほかの店のまわりの三角形は、その店の直線に当たらない)
    private static InstCalc ComputeInst(GeoCalc geo, LayoutInst inst, List<double[]> shopPos, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var A = inst.A;
        var ic = new InstCalc();
        double[] ilo = [double.MaxValue, double.MaxValue, double.MaxValue], ihi = [double.MinValue, double.MinValue, double.MinValue];
        var lab = new byte[geo.Parts.Sum(p => p.N)];
        var lines = new SortedDictionary<byte, List<uint>>();
        // 部品ごとの計算は並べて動かす (2026-11-04、親の指示「計算を縮める」。部品どうしは関わらないので、結果は 1 本ずつと同じ)。つなぐのは部品の順
        var per = new (byte[] Lab, Dictionary<byte, List<uint>> Lines, PartBox Box, NearPart? Near, double[] Floors)[geo.Parts.Count];
        Parallel.For(0, geo.Parts.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, pi =>
        {
            per[pi] = ComputePart(geo.Parts[pi], pi, inst, A, shopPos, false);
        });
        // (2026-11-04、利用者の決定「上面の高さが床とほぼ同じ物体は除去しない」) 除外の決まり (light・1 m 未満・ceiling・wall) が掛かった部品のうち、
        // 上面の高さ (部品の箱のいちばん高い所) が、まわりの床と TopTol 以内のものは、除外の決まりを当てずに判定し直す (床として描き、歩けるようにする)。
        // まわりの床: 同じ区画 (インスタンス) のほかの部品の、除外の決まりを当てた判定で床になった三角形のうち、xy の箱が部品の xy の箱から TopNear 以内のもの
        var redo = TopLevelParts(geo, per.Select(x => (x.Box, x.Floors)).ToArray());
        if (redo.Count > 0)
            Parallel.ForEach(redo, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, pi =>
            {
                per[pi] = ComputePart(geo.Parts[pi], pi, inst, A, shopPos, true);
            });
        int at = 0;
        for (int pi = 0; pi < geo.Parts.Count; pi++)
        {
            var (pl, pls, bx, np, _) = per[pi];
            Buffer.BlockCopy(pl, 0, lab, at, pl.Length); at += pl.Length;
            foreach (var (o, l) in pls) { if (!lines.TryGetValue(o, out var all)) lines[o] = all = new List<uint>(); all.AddRange(l); }
            for (int a = 0; a < 3; a++) { ilo[a] = Math.Min(ilo[a], bx.Lo[a]); ihi[a] = Math.Max(ihi[a], bx.Hi[a]); }
            ic.Boxes.Add(bx);
            if (np != null) ic.Near.Add(np);
        }
        ic.Lab = lab;
        foreach (var (o, l) in lines) ic.Lines[o] = l.ToArray();
        ic.Lo = ilo; ic.Hi = ihi;
        ic.Bytes = lab.Length + ic.Lines.Values.Sum(a => (long)a.Length * 4) + ic.Boxes.Count * 160L + ic.Near.Sum(n => (long)n.V.Length * 8 + n.Nz.Length * 8 + 64);
        return ic;
    }

    // 部品 1 つの判定 (ComputeInst から並べて呼ぶ)
    // 確かめ用: 1 部品の判定に SlowSeconds 秒以上かかったら知らせる (null なら計らない)
    public static Action<string>? SlowLog { get; set; }
    public const double SlowSeconds = 2.0;
    private static (byte[] Lab, Dictionary<byte, List<uint>> Lines, PartBox Box, NearPart? Near, double[] Floors) ComputePart(Part p, int pi, LayoutInst inst, double[] A, List<double[]> shopPos, bool noExclude)
    {
        var swP = SlowLog != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        try { return ComputePartCore(p, pi, inst, A, shopPos, noExclude); }
        finally { if (swP != null && swP.Elapsed.TotalSeconds >= SlowSeconds) SlowLog?.Invoke($"{p.Key} (inst {inst.Index}) 三角形 {p.N}: {swP.Elapsed.TotalSeconds:F1} 秒 {LastLabelsTimes}"); }
    }
    [ThreadStatic] private static string? LastLabelsTimes;

    // 上面の高さが床とほぼ同じ物体 (2026-11-04、利用者の決定「上場の高さが床とほぼ同じ物体は除去しない」。値は担当が決めたもの):
    //   TopTol: 「ほぼ同じ」の高さの差 (m)。TopNear: 「まわり」の xy の隔たり (m)
    private const double TopTol = 0.15, TopNear = 1.0;
    // 除外の決まり (light・1 m 未満・ceiling・wall) が掛かる部品か (Labels の除外と同じ決まり)
    private static bool HasExclusion(Part p) => p.Kind == "excluded" || p.Small || (!p.Words.Contains("counter") && (p.Words.Contains("ceiling") || p.Words.Contains("wall")));
    // 判定し直す部品の番号: 除外の決まりが掛かる部品のうち、上面の高さ (箱の Hi[2]) との差が TopTol 以内の床の三角形 (ほかの部品のもの) が、
    // 部品の xy の箱から TopNear 以内にあるもの。床の三角形は floors の 5 つ組 (x の下, y の下, x の上, y の上, 重心の z)
    private static List<int> TopLevelParts(GeoCalc geo, (PartBox Box, double[] Floors)[] per)
    {
        var res = new List<int>();
        var cand = Enumerable.Range(0, geo.Parts.Count).Where(pi => HasExclusion(geo.Parts[pi]) && per[pi].Box.Hi[2] >= per[pi].Box.Lo[2]).ToList();
        if (cand.Count == 0) return res;
        // 床の三角形を 1 m の升目に入れる (箱が掛かる升の全部)。大きな三角形 (升 4096 を超える) は別の一覧で全部と比べる
        const double cell = 1.0;
        var grid = new Dictionary<(long, long), List<(int Part, int At)>>();
        var big = new List<(int Part, int At)>();
        for (int pi = 0; pi < per.Length; pi++)
        {
            var f = per[pi].Floors;
            for (int k = 0; k + 4 < f.Length; k += 5)
            {
                long i0 = (long)Math.Floor(f[k] / cell), i1 = (long)Math.Floor(f[k + 2] / cell), j0 = (long)Math.Floor(f[k + 1] / cell), j1 = (long)Math.Floor(f[k + 3] / cell);
                if ((i1 - i0 + 1) * (j1 - j0 + 1) > 4096) { big.Add((pi, k)); continue; }
                for (long i = i0; i <= i1; i++)
                    for (long j = j0; j <= j1; j++)
                    {
                        if (!grid.TryGetValue((i, j), out var l)) grid[(i, j)] = l = new();
                        l.Add((pi, k));
                    }
            }
        }
        foreach (var pi in cand)
        {
            var bx = per[pi].Box;
            double x0 = bx.Lo[0] - TopNear, x1 = bx.Hi[0] + TopNear, y0 = bx.Lo[1] - TopNear, y1 = bx.Hi[1] + TopNear, top = bx.Hi[2];
            bool Hit((int Part, int At) e)
            {
                if (e.Part == pi) return false;
                var f = per[e.Part].Floors; int k = e.At;
                return f[k] <= x1 && f[k + 2] >= x0 && f[k + 1] <= y1 && f[k + 3] >= y0 && Math.Abs(f[k + 4] - top) <= TopTol;
            }
            bool found = big.Any(Hit);
            long i0 = (long)Math.Floor(x0 / cell), i1 = (long)Math.Floor(x1 / cell), j0 = (long)Math.Floor(y0 / cell), j1 = (long)Math.Floor(y1 / cell);
            if (!found)
            {
                if ((i1 - i0 + 1) * (j1 - j0 + 1) <= grid.Count)
                {
                    for (long i = i0; i <= i1 && !found; i++)
                        for (long j = j0; j <= j1 && !found; j++)
                            if (grid.TryGetValue((i, j), out var l)) found = l.Any(Hit);
                }
                else
                    found = grid.Any(kv => kv.Key.Item1 >= i0 && kv.Key.Item1 <= i1 && kv.Key.Item2 >= j0 && kv.Key.Item2 <= j1 && kv.Value.Any(Hit));
            }
            if (found) res.Add(pi);
        }
        if (TopLevelLog != null) foreach (var pi in res) TopLevelLog(geo.Parts[pi].Key, per[pi].Box.Lo, per[pi].Box.Hi);
        return res;
    }
    // 確かめ用: 判定し直した部品を知らせる (null なら知らせない)
    public static Action<string, double[], double[]>? TopLevelLog { get; set; }

    private static (byte[] Lab, Dictionary<byte, List<uint>> Lines, PartBox Box, NearPart? Near, double[] Floors) ComputePartCore(Part p, int pi, LayoutInst inst, double[] A, List<double[]> shopPos, bool noExclude)
    {
        var lines = new Dictionary<byte, List<uint>>();
        NearPart? near = null;
        {
            var V = Transform(p, A);
            var pl = Labels(p, V, out var nz, out var comp, out var bnd, noExclude);
            // 床の三角形の xy の箱と重心の z (TopLevelParts で「まわりの床」に使う。除外の決まりを当てた判定のときだけ)
            var floors = new List<double>();
            if (!noExclude)
                for (int t = 0; t < pl.Length; t++)
                {
                    if (pl[t] != LabFloor) continue;
                    int b9 = t * 9;
                    floors.Add(Math.Min(V[b9], Math.Min(V[b9 + 3], V[b9 + 6]))); floors.Add(Math.Min(V[b9 + 1], Math.Min(V[b9 + 4], V[b9 + 7])));
                    floors.Add(Math.Max(V[b9], Math.Max(V[b9 + 3], V[b9 + 6]))); floors.Add(Math.Max(V[b9 + 1], Math.Max(V[b9 + 4], V[b9 + 7])));
                    floors.Add((V[b9 + 2] + V[b9 + 5] + V[b9 + 8]) / 3);
                }
            // 輪郭: 判定の色ごとに、その色の三角形を含むまとめた形の、境界の辺 (部品の点の番号)
            if (bnd.Count > 0)
                for (byte o = 1; o <= 4; o++)
                {
                    var cs = new HashSet<int>();
                    for (int t = 0; t < pl.Length; t++) if (pl[t] == o && comp[t] >= 0) cs.Add(comp[t]);
                    if (cs.Count == 0) continue;
                    if (!lines.TryGetValue(o, out var l)) lines[o] = l = new List<uint>();
                    foreach (var (c, ca, cb) in bnd)
                    {
                        if (!cs.Contains(c)) continue;
                        l.Add((uint)pi); l.Add((uint)p.Idx[ca]); l.Add((uint)p.Idx[cb]);
                    }
                }
            var bx = new PartBox { Inst = inst.Index, Key = p.Key, Words = p.Words, PartIdx = pi, Kind = p.Kind };
            for (int a = 0; a < 3; a++) { bx.Lo[a] = double.MaxValue; bx.Hi[a] = double.MinValue; }
            for (int t = 0; t < V.Length / 3; t++)
                for (int a = 0; a < 3; a++) { bx.Lo[a] = Math.Min(bx.Lo[a], V[t * 3 + a]); bx.Hi[a] = Math.Max(bx.Hi[a], V[t * 3 + a]); }
            // 店のまわりの部品 (view_build6.py の nearTris: 部品の箱から店の印まで EntDist + 1 m 以内)。
            // 持つ三角形は、店の入口の直線・店の中心の床の計算に掛かりうるものだけ (xy で店の印から EntDist + 1 m 以内で、いちばん低い所が店の印 + 1 m 以下。
            // 直線は店の印の xy から 10 m 以内・高さは店の印の真下の床 + 1 m (店の印 + 1 m 以下)、床は店の印より下なので、ほかの三角形は結果に関わらない)
            var nearShops = shopPos.Where(sp => { double d2 = 0; for (int a = 0; a < 3; a++) { var gap = Math.Max(Math.Max(bx.Lo[a] - sp[a], sp[a] - bx.Hi[a]), 0); d2 += gap * gap; } return Math.Sqrt(d2) <= EntDist + 1; }).ToList();
            if (nearShops.Count > 0)
            {
                var keepV = new List<double>(); var keepN = new List<double>();
                for (int t = 0; t < V.Length / 9; t++)
                {
                    int b9 = t * 9;
                    double x0 = Math.Min(V[b9], Math.Min(V[b9 + 3], V[b9 + 6])), x1 = Math.Max(V[b9], Math.Max(V[b9 + 3], V[b9 + 6]));
                    double y0 = Math.Min(V[b9 + 1], Math.Min(V[b9 + 4], V[b9 + 7])), y1 = Math.Max(V[b9 + 1], Math.Max(V[b9 + 4], V[b9 + 7]));
                    double z0 = Math.Min(V[b9 + 2], Math.Min(V[b9 + 5], V[b9 + 8]));
                    bool need = false;
                    foreach (var sp in nearShops)
                    {
                        double gx = Math.Max(Math.Max(x0 - sp[0], sp[0] - x1), 0), gy = Math.Max(Math.Max(y0 - sp[1], sp[1] - y1), 0);
                        if (Math.Sqrt(gx * gx + gy * gy) <= EntDist + 1 && z0 <= sp[2] + 1) { need = true; break; }
                    }
                    if (!need) continue;
                    for (int k = 0; k < 9; k++) keepV.Add(V[b9 + k]);
                    keepN.Add(nz[t]);
                }
                if (keepN.Count > 0) near = new NearPart { Inst = inst.Index, Key = p.Key, V = keepV.ToArray(), Nz = keepN.ToArray() };
            }
            return (pl, lines, bx, near, floors.ToArray());
        }
    }

    // socpak 1 つのファイル (file) を書く。形: 頭 "SCJT3DG2"・表 (JSON) の位置 (int64)・表の長さ (int32)・4 バイトの空き (ここまで 24 バイト)、
    // 続けて中身 (位置は中身の頭 (24 バイト目) からのバイト数)、最後に表 (JSON: parts と inst)。
    //   parts: [名前, 種類, 点の位置, 点の数, 番号の位置, 三角形の数, 箱の下, 箱の上, 番号のバイト数 (2 か 4)]。点は float32 (socpak の枠)
    //   inst: [インスタンスの番号, 判定の色の位置 (三角形ごとに 1 バイト、部品の順につなげたもの), {色: [輪郭の位置, 辺の数]}]。
    //   輪郭の辺は uint32 の 3 つ組 [部品の番号, 点の番号, 点の番号]。中身がまったく同じインスタンス (同じ向き) は同じ所を指す (大きさを減らす)。書いた大きさを返す
    private static long WriteSocpakFile(string file, GeoCalc geo, List<(LayoutInst Inst, InstCalc Calc)> calcs)
    {
        const int Base = 24;
        using var fs = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
        using var bw = new BinaryWriter(fs);
        bw.Write(Encoding.ASCII.GetBytes(GeoMagic));
        bw.Write(0L); bw.Write(0); bw.Write(0);
        long Pos() { bw.Flush(); return fs.Position - Base; }
        bw.Write(geo.Body);
        var instJson = new List<object>();
        var blocks = new Dictionary<string, (long LabOff, Dictionary<string, long[]> Lines)>(StringComparer.Ordinal);
        foreach (var (inst, ic) in calcs)
        {
            string hash;
            using (var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                h.AppendData(ic.Lab);
                foreach (var (o, l) in ic.Lines)
                {
                    h.AppendData([o]);
                    var tmpb = new byte[l.Length * 4]; Buffer.BlockCopy(l, 0, tmpb, 0, tmpb.Length); h.AppendData(tmpb);
                }
                hash = Convert.ToHexString(h.GetHashAndReset());
            }
            if (!blocks.TryGetValue(hash, out var blk))
            {
                Align4(bw);
                var labOff = Pos();
                bw.Write(ic.Lab);
                var lineJson = new Dictionary<string, long[]>();
                foreach (var (o, l) in ic.Lines)
                {
                    Align4(bw);
                    var so = Pos(); foreach (var u in l) bw.Write(u);
                    lineJson[LabNames[o]] = [so, l.Length / 3];
                }
                blocks[hash] = blk = (labOff, lineJson);
            }
            instJson.Add(new object[] { inst.Index, blk.LabOff, blk.Lines });
        }
        var head = JsonSerializer.SerializeToUtf8Bytes(new { parts = geo.PartsJson, inst = instJson });
        bw.Flush();
        var headPos = fs.Position;
        bw.Write(head);
        bw.Flush();
        fs.Position = 8;
        bw.Write(headPos); bw.Write(head.Length);
        bw.Flush();
        return fs.Length;
    }

    private static void Align4(BinaryWriter bw) { bw.Flush(); while (bw.BaseStream.Position % 4 != 0) bw.Write((byte)0); }
    private static double[] Round3(double[] v) => v.Select(x => Math.Round(x, 3)).ToArray();

    // インスタンスの行列 (3 行 4 列、行優先: 回転の列 ex・ey・ez と位置。hh の VIEW3D の m と同じ作り)
    private static double[] InstMatrix(StationInstance inst)
    {
        var o = inst.ToStation.Apply((0, 0, 0)); var ex = inst.ToStation.Apply((1, 0, 0)); var ey = inst.ToStation.Apply((0, 1, 0)); var ez = inst.ToStation.Apply((0, 0, 1));
        return [ex.X - o.X, ey.X - o.X, ez.X - o.X, o.X, ex.Y - o.Y, ey.Y - o.Y, ez.Y - o.Y, o.Y, ex.Z - o.Z, ey.Z - o.Z, ez.Z - o.Z, o.Z];
    }

    // 部品の三角形を場所の座標にする (view_build_lib.py・view_build6.py と同じく S = A·M を作ってからメッシュの点に掛ける)
    private static double[] Transform(Part p, double[] A)
    {
        var M = p.M;
        var S = new double[12];
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                S[r * 4 + c] = A[r * 4] * M[c] + A[r * 4 + 1] * M[4 + c] + A[r * 4 + 2] * M[8 + c] + (c == 3 ? A[r * 4 + 3] : 0);
        var T = p.Mesh;
        var V = new double[T.Length];
        for (int i = 0; i < T.Length; i += 3)
        {
            double x = T[i], y = T[i + 1], z = T[i + 2];
            V[i] = S[0] * x + S[1] * y + S[2] * z + S[3];
            V[i + 1] = S[4] * x + S[5] * y + S[6] * z + S[7];
            V[i + 2] = S[8] * x + S[9] * y + S[10] * z + S[11];
        }
        return V;
    }

    // 判定 2 (view_build6.py の lab2 と、unit_whammers_facade_interior_b のカウンター)
    // noExclude: 除外の決まり (light・1 m 未満・ceiling・wall) を当てない (TopLevelParts で選んだ部品)
    private static byte[] Labels(Part p, double[] V, out double[] nz, out int[] comp, out List<(int C, int A, int B)> bnd, bool noExclude = false)
    {
        int n = V.Length / 9;
        nz = new double[n];
        var zc = new double[n];
        var area = new double[n];
        for (int t = 0; t < n; t++)
        {
            int b = t * 9;
            double ux = V[b + 3] - V[b], uy = V[b + 4] - V[b + 1], uz = V[b + 5] - V[b + 2], vx = V[b + 6] - V[b], vy = V[b + 7] - V[b + 1], vz = V[b + 8] - V[b + 2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, ln = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            nz[t] = cz / Math.Max(ln, 1e-12);
            area[t] = ln / 2;
            zc[t] = (V[b + 2] + V[b + 5] + V[b + 8]) / 3;
        }
        var up = new bool[n];
        for (int t = 0; t < n; t++) up[t] = nz[t] > 0;
        var swF = SlowLog != null ? System.Diagnostics.Stopwatch.StartNew() : null;
        (comp, bnd) = Fuse(V, up);
        var tFuse = swF?.Elapsed.TotalSeconds ?? 0;
        var lab = new byte[n];
        var w = p.Words;
        if (p.Kind != "excluded" || noExclude)
        {
            if (w.Contains("counter")) { for (int t = 0; t < n; t++) if (nz[t] >= CosWalk && comp[t] >= 0) lab[t] = LabCounter; }
            else if (!noExclude && (w.Contains("ceiling") || w.Contains("wall"))) { }
            else if (w.Any(x => x is "stair" or "stairs")) { for (int t = 0; t < n; t++) if (nz[t] >= CosWalk && comp[t] >= 0) lab[t] = LabSlope; }
            else
            {
                for (int t = 0; t < n; t++)
                {
                    if (comp[t] < 0) continue;
                    if (nz[t] >= CosFlat) { if (!p.Small || noExclude) lab[t] = LabFloor; }
                    else if (nz[t] >= CosWalk) lab[t] = LabSlope;
                }
                // 局所の箱の x か y が 1 m 未満の部品 (Small) は、床と同じく筒の底にもしない
                if (!p.Small || noExclude)
                {
                var swT = SlowLog != null ? System.Diagnostics.Stopwatch.StartNew() : null;
                    var tm = TubeMask(V, nz, comp);
                    if (swT != null) LastLabelsTimes = $"(まとめた形 {tFuse:F1} 秒・筒の底 {swT.Elapsed.TotalSeconds:F1} 秒・境界の辺 {bnd.Count})";
                    for (int t = 0; t < n; t++) if (tm[t] && lab[t] != LabFloor) lab[t] = LabTube;
                }
            }
        }
        if (p.Key == "unit_whammers_facade_interior_b")
        {
            var hz = new Dictionary<double, double>();
            for (int t = 0; t < n; t++)
                if (nz[t] >= CosFlat) { var k = Math.Round(zc[t], 2, MidpointRounding.ToEven); hz[k] = hz.GetValueOrDefault(k) + area[t]; }
            if (hz.Count > 0)
            {
                double baseZ = 0, best = double.MinValue;
                foreach (var (z, a) in hz) if (a > best) { best = a; baseZ = z; }
                for (int t = 0; t < n; t++) if (nz[t] >= CosWalk && zc[t] > baseZ && zc[t] < baseZ + 1.0) lab[t] = LabCounter;
            }
        }
        return lab;
    }

    // fuse.py: 上向きの三角形 (mask) を、1 辺を共有し同じ平面に乗るものどうしだけまとめる。辺の共有は点の座標がまったく同じもの (値のビットが同じ。fuse.py の np.unique と同じ)。
    // 同じ平面は、法線の向きと平面の位置の差が 1e-4 以内。並べた辺の隣どうしだけを比べる (fuse.py と同じ)。
    // 返す: comp (mask の外は -1)、境界の辺 (形ごとに 1 つの三角形にしか使われない辺) の (形, 点, 点)。点は、その座標の三角形の角の番号 (三角形の番号 × 3 + 角)。
    // メモリ: 辞書を使わず、並べ替えた配列だけで作る (三角形あたり約 130 バイト)
    private struct FEdge { public int A, B, Ord, Tri; }
    private static (int[] Comp, List<(int, int, int)> Bnd) Fuse(double[] V, bool[] mask)
    {
        int n = V.Length / 9;
        var comp = new int[n];
        Array.Fill(comp, -1);
        var bnd = new List<(int, int, int)>();
        int m = 0;
        for (int t = 0; t < n; t++) if (mask[t]) m++;
        if (m == 0) return (comp, bnd);
        var idx = new int[m];
        for (int t = 0, j = 0; t < n; t++) if (mask[t]) idx[j++] = t;
        // 角 (i = 三角形の並び × 3 + 角) を座標のビットで並べて、同じ座標に同じ番号を付ける
        int CornerBase(int i) => idx[i / 3] * 9 + (i % 3) * 3;
        var ord = new int[m * 3];
        for (int i = 0; i < ord.Length; i++) ord[i] = i;
        int Cmp(int a, int b)
        {
            int pa = CornerBase(a), pb = CornerBase(b);
            for (int k = 0; k < 3; k++)
            {
                long x = BitConverter.DoubleToInt64Bits(V[pa + k]), y = BitConverter.DoubleToInt64Bits(V[pb + k]);
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }
        Array.Sort(ord, Cmp);
        var vid = new int[m * 3];
        var cornerOf = new List<int>();
        for (int j = 0; j < ord.Length; j++)
        {
            if (j == 0 || Cmp(ord[j - 1], ord[j]) != 0) cornerOf.Add(idx[ord[j] / 3] * 3 + ord[j] % 3);
            vid[ord[j]] = cornerOf.Count - 1;
        }
        ord = null!;
        var nrm = new double[m * 3]; var dpl = new double[m];
        for (int i = 0; i < m; i++)
        {
            int b = idx[i] * 9;
            double ux = V[b + 3] - V[b], uy = V[b + 4] - V[b + 1], uz = V[b + 5] - V[b + 2], vx = V[b + 6] - V[b], vy = V[b + 7] - V[b + 1], vz = V[b + 8] - V[b + 2];
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, ln = Math.Max(Math.Sqrt(cx * cx + cy * cy + cz * cz), 1e-30);
            nrm[i * 3] = cx / ln; nrm[i * 3 + 1] = cy / ln; nrm[i * 3 + 2] = cz / ln;
            dpl[i] = nrm[i * 3] * V[b] + nrm[i * 3 + 1] * V[b + 1] + nrm[i * 3 + 2] * V[b + 2];
        }
        // 辺: [0,1] の組を全部、次に [1,2]、次に [2,0] (fuse.py と同じ並び)。(小さい番号, 大きい番号, 元の並び) で並べる
        var e = new FEdge[m * 3];
        for (int s = 0; s < 3; s++)
        {
            int c0 = s, c1 = (s + 1) % 3;
            for (int i = 0; i < m; i++)
            {
                int a = vid[i * 3 + c0], b = vid[i * 3 + c1];
                e[s * m + i] = new FEdge { A = Math.Min(a, b), B = Math.Max(a, b), Ord = s * m + i, Tri = i };
            }
        }
        vid = null!;
        Array.Sort(e, (p, q) => p.A != q.A ? p.A.CompareTo(q.A) : p.B != q.B ? p.B.CompareTo(q.B) : p.Ord.CompareTo(q.Ord));
        var parent = new int[m];
        for (int i = 0; i < m; i++) parent[i] = i;
        int Find(int a) { while (parent[a] != a) { parent[a] = parent[parent[a]]; a = parent[a]; } return a; }
        for (int k = 0; k + 1 < e.Length; k++)
        {
            if (e[k].A != e[k + 1].A || e[k].B != e[k + 1].B) continue;
            int a = e[k].Tri, b = e[k + 1].Tri;
            var dn = Math.Max(Math.Abs(nrm[a * 3] - nrm[b * 3]), Math.Max(Math.Abs(nrm[a * 3 + 1] - nrm[b * 3 + 1]), Math.Abs(nrm[a * 3 + 2] - nrm[b * 3 + 2])));
            if (dn <= NTol && Math.Abs(dpl[a] - dpl[b]) <= DTol)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) parent[ra] = rb;
            }
        }
        // 形の番号は根の番号の順 (fuse.py の np.unique(roots) と同じ)
        var rootId = new int[m];
        Array.Fill(rootId, -1);
        var cid = new int[m];
        for (int i = 0; i < m; i++) rootId[Find(i)] = 0;
        for (int r0 = 0, next = 0; r0 < m; r0++) if (rootId[r0] == 0) rootId[r0] = ++next;
        for (int i = 0; i < m; i++) { cid[i] = rootId[Find(i)] - 1; comp[idx[i]] = cid[i]; }
        // 境界の辺: 同じ形の中で 1 回しか出ない辺 (同じ点の組の辺の並びの中で、同じ形の数を数える)
        for (int k = 0; k < e.Length;)
        {
            int k2 = k + 1;
            while (k2 < e.Length && e[k2].A == e[k].A && e[k2].B == e[k].B) k2++;
            for (int x = k; x < k2; x++)
            {
                int c = cid[e[x].Tri], cnt = 0;
                for (int y = k; y < k2; y++) if (cid[e[y].Tri] == c) cnt++;
                if (cnt != 1) continue;
                bnd.Add((c, cornerOf[e[x].A], cornerOf[e[x].B]));
            }
            k = k2;
        }
        return (comp, bnd);
    }

    // 筒の底 (view_build6.py の tube_mask): 歩ける上向きの三角形 (まとめた形に入るもの) ごとに、中心から真上に、同じ部品の下向きの三角形 (法線 z < 0) までの高さを測り、
    // TubeH 以上ならその三角形を底面にする。
    // 結果は「中心の xy を含む (内側の判定 s ≥ 0・t ≥ 0・s + t ≤ 1 を満たす) 下向きの三角形のうち、中心より 1 cm 以上高いものまでの、いちばん小さい高さ」で決まる。
    // tube_mask は 2 m の升目で比べる組を絞るが、中心を含む三角形は、その三角形の箱の升に必ず入るので、升の大きさを変えても結果は同じ。
    // ここでは中心を CellTube の細かい升目に入れ、下向きの三角形ごとに、その箱に掛かる升の中心だけを見る
    // (2026-11-04、利用者の指摘「時間がかかる」: 細かい部品 (drak_kraken_holo_viewer、87 万三角形) で、2 m の升に中心と下向きの三角形が数千ずつ入り、39 秒かかっていた)
    private const double CellTube = 0.25;
    private static bool[] TubeMask(double[] V, double[] nz, int[] comp)
    {
        int n = nz.Length;
        var res = new bool[n];
        var up = Enumerable.Range(0, n).Where(t => nz[t] >= CosWalk && comp[t] >= 0).ToArray();
        if (up.Length == 0) return res;
        var dn = Enumerable.Range(0, n).Where(t => nz[t] < 0).ToArray();
        if (dn.Length == 0) return res;
        const double cell = CellTube;
        var px = new double[up.Length]; var py = new double[up.Length]; var pz = new double[up.Length]; var best = new double[up.Length];
        var groups = new Dictionary<(long, long), List<int>>();
        for (int k = 0; k < up.Length; k++)
        {
            int b = up[k] * 9;
            px[k] = (V[b] + V[b + 3] + V[b + 6]) / 3; py[k] = (V[b + 1] + V[b + 4] + V[b + 7]) / 3; pz[k] = (V[b + 2] + V[b + 5] + V[b + 8]) / 3;
            best[k] = double.PositiveInfinity;
            var key = ((long)Math.Floor(px[k] / cell), (long)Math.Floor(py[k] / cell));
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new();
            l.Add(k);
        }
        void Check(int t, List<int> ks)
        {
            int c = t * 9;
            double ax = V[c], ay = V[c + 1], az = V[c + 2];
            double v0x = V[c + 3] - ax, v0y = V[c + 4] - ay, v1x = V[c + 6] - ax, v1y = V[c + 7] - ay;
            double den = v0x * v1y - v1x * v0y;
            if (!(Math.Abs(den) > 1e-12)) return;
            foreach (var k in ks)
            {
                double wx = px[k] - ax, wy = py[k] - ay;
                double s = (wx * v1y - v1x * wy) / den, tt = (v0x * wy - wx * v0y) / den;
                if (!(s >= 0 && tt >= 0 && s + tt <= 1)) continue;
                double z = az + s * (V[c + 5] - az) + tt * (V[c + 8] - az);
                if (z > pz[k] + 0.01 && z - pz[k] < best[k]) best[k] = z - pz[k];
            }
        }
        foreach (var t in dn)
        {
            int b = t * 9;
            double x0 = Math.Min(V[b], Math.Min(V[b + 3], V[b + 6])), x1 = Math.Max(V[b], Math.Max(V[b + 3], V[b + 6]));
            double y0 = Math.Min(V[b + 1], Math.Min(V[b + 4], V[b + 7])), y1 = Math.Max(V[b + 1], Math.Max(V[b + 4], V[b + 7]));
            long i0 = (long)Math.Floor(x0 / cell), i1 = (long)Math.Floor(x1 / cell), j0 = (long)Math.Floor(y0 / cell), j1 = (long)Math.Floor(y1 / cell);
            if ((i1 - i0 + 1) * (j1 - j0 + 1) <= groups.Count)
            {
                for (long i = i0; i <= i1; i++) for (long j = j0; j <= j1; j++) if (groups.TryGetValue((i, j), out var ks)) Check(t, ks);
            }
            else
                foreach (var (key, ks) in groups)
                    if (key.Item1 >= i0 && key.Item1 <= i1 && key.Item2 >= j0 && key.Item2 <= j1) Check(t, ks);
        }
        for (int k = 0; k < up.Length; k++) if (double.IsFinite(best[k]) && best[k] >= TubeH) res[up[k]] = true;
        return res;
    }

    // ===== GLB (view_build_lib.py の load と同じたどり方: 場面のルートの子ごとに、ルートの行列を掛けずにたどる) =====
    // メッシュの三角形 (float32) は同じメッシュの部品で共有する (view_build_lib.py の meshcache と同じ)
    // 床にしない部品の大きさ (局所の箱の x か y がこれ未満。利用者の決定「縦か横のどちらかが 1 m 未満」)
    private const double SmallPart = 1.0;
    // 補う面の比べる面を決める (StationFill)。アプリの床の判定 (判定 2 の Labels) を、部品の形をそのまま (行列を掛けず、局所の +z を上として) 当てた結果を返す。
    // 判定の決まりは変えない。部品の名前の語 (ceiling・wall・counter・stair) は同じに当てる。light と 1 m 未満の除外は掛けない (利用者の指示)。
    // 返す: 三角形ごとの LabNone・LabFloor・LabSlope・LabTube・LabCounter
    internal static byte[] WalkLabelsOf(string meshName, float[] tris)
    {
        var words = StationFloorBuilder.MeshWords(meshName);
        var p = new Part { Name = meshName, Key = string.Join("_", words), Words = words, Kind = words.Contains("counter") ? "counterpart" : "other", Mesh = tris, Small = false };
        var V = new double[tris.Length];
        for (int i = 0; i < V.Length; i++) V[i] = tris[i];
        return Labels(p, V, out _, out _, out _);
    }

    private static Part ToPart(SourcePart sp)
    {
        var words = StationFloorBuilder.MeshWords(sp.Name);
        var key = string.Join("_", words);
        // 利用者の指示「light_attachment 除外」・(2026-11-04、利用者の決定「light ついてたら除外」) 名前の語に light のある部品も除外・名前に counter のある部品はカウンターの部品
        var kind = key.StartsWith("light_attachment", StringComparison.Ordinal) || words.Contains("light") ? "excluded" : words.Contains("counter") ? "counterpart" : "other";
        // (2026-11-04、利用者の決定「縦か横のどちらかが 1 m 未満の部品は、床にしない」) 局所の箱の x・y: メッシュの点の x・y の幅に、ノードの行列の x・y の列の長さを掛けたもの
        // (view_build6.py の壁の決まりの lx・ly と同じ作り)
        double mnx = double.MaxValue, mxx = double.MinValue, mny = double.MaxValue, mxy = double.MinValue;
        var sz = sp.SizeRef ?? sp.Mesh;
        for (int i = 0; i < sz.Length; i += 3) { mnx = Math.Min(mnx, sz[i]); mxx = Math.Max(mxx, sz[i]); mny = Math.Min(mny, sz[i + 1]); mxy = Math.Max(mxy, sz[i + 1]); }
        var M = sp.M;
        double lx = Math.Sqrt(M[0] * M[0] + M[4] * M[4] + M[8] * M[8]) * (mxx - mnx), ly = Math.Sqrt(M[1] * M[1] + M[5] * M[5] + M[9] * M[9]) * (mxy - mny);
        return new Part { Name = sp.Name, Key = key, Words = words, Kind = kind, Mesh = sp.Mesh, M = sp.M, Small = Math.Min(lx, ly) < SmallPart, CgfPath = sp.CgfPath, Uv = sp.Uv != null && sp.Uv.Length == sp.Mesh.Length / 9 * 6 ? sp.Uv : null, TexKeys = sp.TexKeys, Tex = sp.Tex != null && sp.Tex.Length == sp.Mesh.Length / 9 ? sp.Tex : null };
    }

    public static List<SourcePart> ReadGlb(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67) return new();
        int jl = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        using var doc = JsonDocument.Parse(bytes.AsMemory(20, jl));
        int bin0 = 20 + jl + 8;
        var js = doc.RootElement;
        var res = new List<SourcePart>();
        if (!js.TryGetProperty("nodes", out var nodes) || !js.TryGetProperty("scenes", out var scenes)) return res;
        js.TryGetProperty("meshes", out var meshes);
        var accs = js.GetProperty("accessors");
        var views = js.GetProperty("bufferViews");
        var meshCache = new Dictionary<int, float[]>();
        float[] MeshTris(int mi)
        {
            if (meshCache.TryGetValue(mi, out var c)) return c;
            var outl = new List<float>();
            foreach (var pr in meshes[mi].GetProperty("primitives").EnumerateArray())
            {
                if (pr.TryGetProperty("mode", out var mo) && mo.GetInt32() != 4) continue;
                var P = Acc(pr.GetProperty("attributes").GetProperty("POSITION").GetInt32());
                long[] ix;
                if (pr.TryGetProperty("indices", out var ie)) ix = AccIdx(ie.GetInt32());
                else { ix = new long[P.Length / 3]; for (int i = 0; i < ix.Length; i++) ix[i] = i; }
                int nt = ix.Length / 3 * 3;
                for (int i = 0; i < nt; i++) { var v = ix[i]; outl.Add(P[v * 3]); outl.Add(P[v * 3 + 1]); outl.Add(P[v * 3 + 2]); }
            }
            return meshCache[mi] = outl.ToArray();
        }
        (int Off, int Cnt, int Comp, int Ct, int Size, int Stride) AccInfo(int i)
        {
            var a = accs[i];
            var v = views[a.GetProperty("bufferView").GetInt32()];
            int off = bin0 + (v.TryGetProperty("byteOffset", out var bo) ? bo.GetInt32() : 0) + (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            int cnt = a.GetProperty("count").GetInt32();
            int comp = a.GetProperty("type").GetString() switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 1 };
            int ct = a.GetProperty("componentType").GetInt32();
            int size = ct switch { 5126 => 4, 5125 => 4, 5123 => 2, 5121 => 1, _ => 4 };
            int stride = v.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : comp * size;
            if (stride == 0) stride = comp * size;
            return (off, cnt, comp, ct, size, stride);
        }
        float[] Acc(int i)
        {
            var (off, cnt, comp, ct, size, stride) = AccInfo(i);
            var r = new float[cnt * comp];
            for (int k = 0; k < cnt; k++)
                for (int c2 = 0; c2 < comp; c2++)
                {
                    int p = off + k * stride + c2 * size;
                    r[k * comp + c2] = ct == 5126 ? BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(p)) : float.NaN;
                }
            return r;
        }
        long[] AccIdx(int i)
        {
            var (off, cnt, comp, ct, size, stride) = AccInfo(i);
            var r = new long[cnt * comp];
            for (int k = 0; k < cnt; k++)
                for (int c2 = 0; c2 < comp; c2++)
                {
                    int p = off + k * stride + c2 * size;
                    r[k * comp + c2] = ct switch
                    {
                        5125 => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p)),
                        5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p)),
                        5121 => bytes[p],
                        _ => 0,
                    };
                }
            return r;
        }
        double N(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN;
        double[] LMat(JsonElement nd)
        {
            var M = new double[16];
            if (nd.TryGetProperty("matrix", out var m))
            {
                // glTF は列優先。数学の行列 (行優先) に直す
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) M[r * 4 + c] = N(m[c * 4 + r]);
                return M;
            }
            double tx = 0, ty = 0, tz = 0, x = 0, y = 0, z = 0, w = 1, sx = 1, sy = 1, sz = 1;
            if (nd.TryGetProperty("translation", out var t)) { tx = N(t[0]); ty = N(t[1]); tz = N(t[2]); }
            if (nd.TryGetProperty("rotation", out var q)) { x = N(q[0]); y = N(q[1]); z = N(q[2]); w = N(q[3]); }
            if (nd.TryGetProperty("scale", out var s)) { sx = N(s[0]); sy = N(s[1]); sz = N(s[2]); }
            double[,] R = { { 1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w) }, { 2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w) }, { 2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y) } };
            double[] S = [sx, sy, sz];
            for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) M[r * 4 + c] = R[r, c] * S[c];
            M[3] = tx; M[7] = ty; M[11] = tz; M[15] = 1;
            return M;
        }
        double[] Mul(double[] a, double[] b)
        {
            var c = new double[16];
            for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { double s = 0; for (int j = 0; j < 4; j++) s += a[r * 4 + j] * b[j * 4 + k]; c[r * 4 + k] = s; }
            return c;
        }
        double[] I = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        void Walk(int i, double[] M)
        {
            var nd = nodes[i];
            var M2 = Mul(M, LMat(nd));
            if (nd.TryGetProperty("mesh", out var mi) && meshes.ValueKind == JsonValueKind.Array)
            {
                var mesh = meshes[mi.GetInt32()];
                var name = mesh.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                var T = MeshTris(mi.GetInt32());
                if (T.Length > 0 && M2.All(double.IsFinite)) res.Add(new SourcePart(name, T, M2));
            }
            if (nd.TryGetProperty("children", out var ch)) foreach (var c in ch.EnumerateArray()) Walk(c.GetInt32(), M2);
        }
        var si = js.TryGetProperty("scene", out var sc) ? sc.GetInt32() : 0;
        foreach (var r in scenes[si].GetProperty("nodes").EnumerateArray())
            if (nodes[r.GetInt32()].TryGetProperty("children", out var kids))
                foreach (var c in kids.EnumerateArray()) Walk(c.GetInt32(), I);
        return res;
    }

    // ===== 店の入口 (view_build6.py) =====
    private static List<object> Entrances(MapData map, List<Shop> shops, List<PartBox> pbox, List<NearPart> nearTris)
    {
        var res = new List<object>();
        (double Z, string Key, int Inst)? FloorUnder(double[] q)
        {
            (double, string, int)? best = null;
            foreach (var np in nearTris)
            {
                double bz = double.NegativeInfinity;
                for (int t = 0; t < np.Nz.Length; t++)
                {
                    if (np.Nz[t] < CosFlat) continue;
                    int b = t * 9; var V = np.V;
                    double ax = V[b], ay = V[b + 1], az = V[b + 2];
                    double v0x = V[b + 3] - ax, v0y = V[b + 4] - ay, v1x = V[b + 6] - ax, v1y = V[b + 7] - ay;
                    double den = v0x * v1y - v1x * v0y;
                    if (!(Math.Abs(den) > 1e-12)) continue;
                    double wx = q[0] - ax, wy = q[1] - ay;
                    double s = (wx * v1y - v1x * wy) / den, tt = (v0x * wy - wx * v0y) / den;
                    if (!(s >= 0 && tt >= 0 && s + tt <= 1)) continue;
                    double z = az + s * (V[b + 5] - az) + tt * (V[b + 8] - az);
                    if (z <= q[2] && z > bz) bz = z;
                }
                if (double.IsFinite(bz) && (best == null || bz > best.Value.Item1)) best = (bz, np.Key, np.Inst);
            }
            return best;
        }
        int SegHits(double[] a, double[] b, List<string> who)
        {
            int n = 0;
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            foreach (var np in nearTris)
            {
                int h = 0; var V = np.V;
                for (int t = 0; t < V.Length / 9; t++)
                {
                    int k = t * 9;
                    double e1x = V[k + 3] - V[k], e1y = V[k + 4] - V[k + 1], e1z = V[k + 5] - V[k + 2], e2x = V[k + 6] - V[k], e2y = V[k + 7] - V[k + 1], e2z = V[k + 8] - V[k + 2];
                    double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                    double det = e1x * px + e1y * py + e1z * pz;
                    if (!(Math.Abs(det) > 1e-12)) continue;
                    double inv = 1 / det, tx = a[0] - V[k], ty = a[1] - V[k + 1], tz = a[2] - V[k + 2];
                    double u = (tx * px + ty * py + tz * pz) * inv;
                    double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
                    double v = (dx * qx + dy * qy + dz * qz) * inv, tt = (e2x * qx + e2y * qy + e2z * qz) * inv;
                    if (u >= 0 && v >= 0 && u + v <= 1 && tt > 1e-6 && tt < 1 - 1e-6) h++;
                }
                if (h > 0) { who.Add($"{np.Key} (inst {np.Inst}) {h}"); n += h; }
            }
            return n;
        }
        foreach (var sh in shops)
        {
            var q = sh.Pos;
            var fu = FloorUnder(q);
            if (fu == null) { res.Add(new { shop = sh.Name, pos = sh.Pos, floor = (object?)null, list = Array.Empty<object>() }); continue; }
            var h = fu.Value.Z + 1.0;
            var lst = new List<(double Dist, object Row)>();
            foreach (var bx in pbox)
            {
                if (!bx.Words.Any(x => EntWords.Contains(x))) continue;
                double d2 = 0;
                for (int k = 0; k < 3; k++) { var gap = Math.Max(Math.Max(bx.Lo[k] - q[k], q[k] - bx.Hi[k]), 0); d2 += gap * gap; }
                var dist = Math.Sqrt(d2);
                if (dist > EntDist) continue;
                double[] a = [q[0], q[1], h];
                double[] b = [Math.Min(Math.Max(q[0], bx.Lo[0]), bx.Hi[0]), Math.Min(Math.Max(q[1], bx.Lo[1]), bx.Hi[1]), h];
                var who = new List<string>();
                var len = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
                var hits = len > 1e-6 ? SegHits(a, b, who) : 0;
                // 入口の部品の箱から EntNode m 以内 (軸ごと) の道の点と、そのまとまりが乗り場につながるか
                int nn = 0; var comps = new SortedSet<int>();
                for (int i = 0; i < map.Nodes.Count; i++)
                {
                    var p = map.Nodes[i]; bool ok = true;
                    for (int k = 0; k < 3; k++) if (Math.Max(Math.Max(bx.Lo[k] - p[k], p[k] - bx.Hi[k]), 0) > EntNode) { ok = false; break; }
                    if (!ok) continue;
                    nn++; comps.Add(map.Comp[i]);
                }
                var gw = comps.Any(map.GwComps.Contains);
                lst.Add((dist, new object[] { bx.Inst, bx.PartIdx, bx.Key, Math.Round(dist, 2), hits, hits < EntHit, nn, comps.ToArray(), gw, R3(b), R3(a), who }));
            }
            res.Add(new { shop = sh.Name, pos = sh.Pos, floor = new object[] { Math.Round(fu.Value.Z, 2), fu.Value.Key, fu.Value.Inst }, h = Math.Round(h, 2), list = lst.OrderBy(x => x.Dist).Select(x => x.Row).ToArray() });
        }
        return res;
    }
    private static double[] R3(double[] v) => v.Select(x => Math.Round(x, 3)).ToArray();

    // ===== 店のカウンター (2026-11-05、利用者の指示「行先でショップ選んだ時に…本来カウンターの前にすべき」) =====
    // 店ごとに、店の中心から EntDist m 以内 (箱までの距離) にあるカウンターの部品 (Kind = counterpart。名前の語に counter) の箱 (場所の座標) を近い順に。
    // 店の名前 → [[下 x,y,z], [上 x,y,z]], …。ビューアが「カウンターの前」に立つ所を決めるのに使う (既存の entrances は残す)。
    // 同じ名前の店が地点に 2 つ以上あれば、同じ名前の中に箱をまとめる (同じ箱は 1 つ)。ビューアは店の位置からの距離で選び直す。
    // 店と同じ階の箱だけ (2026-11-05、利用者の報告「ORISON の KEI-TO は一階なのに、行先て飛んだら 2 階だった」): 箱の z の範囲が店の印の CounterBelow m 下〜CounterAbove m 上に掛かるもの。
    //   10 m の距離だけでは上の階の別の店のカウンターも入っていた (Orison: Kel-To (印 z 1.45) に、真上の Whammers のカウンター (z 5.58〜6.71) と FTL の「counter」の部品 (z 5.33〜9.83) が入り、
    //   Whammers (印 z 7.46) に Kel-To のカウンター (z −0.17〜1.08) が入っていた)。実データの店の印とカウンターの箱: 箱の下は印の 1.5〜2.25 m 下、箱の上は印の 0.3〜0.85 m 下
    //   (Orison の Whammers・Kel-To・Makau・TDD)。値は丸の地点のそばに立つ決まり (ビューアの placeStart: 丸の地点の 2.5 m 下〜0.5 m 上の床) と同じにした
    private const double CounterBelow = 2.5, CounterAbove = 0.5;
    private static Dictionary<string, List<double[][]>> Counters(List<Shop> shops, List<PartBox> pbox)
    {
        var res = new Dictionary<string, List<double[][]>>(StringComparer.Ordinal);
        foreach (var sh in shops)
        {
            var q = sh.Pos;
            var lst = new List<(double Dist, PartBox Box)>();
            foreach (var bx in pbox)
            {
                if (bx.Kind != "counterpart") continue;
                if (bx.Hi[2] < q[2] - CounterBelow || bx.Lo[2] > q[2] + CounterAbove) continue;
                double d2 = 0;
                for (int k = 0; k < 3; k++) { var gap = Math.Max(Math.Max(bx.Lo[k] - q[k], q[k] - bx.Hi[k]), 0); d2 += gap * gap; }
                var dist = Math.Sqrt(d2);
                if (dist > EntDist) continue;
                lst.Add((dist, bx));
            }
            if (!res.TryGetValue(sh.Name, out var rows)) res[sh.Name] = rows = new List<double[][]>();
            foreach (var (_, bx) in lst.OrderBy(x => x.Dist))
            {
                var row = new[] { R3(bx.Lo), R3(bx.Hi) };
                if (rows.Any(r => r[0].SequenceEqual(row[0]) && r[1].SequenceEqual(row[1]))) continue;
                rows.Add(row);
            }
        }
        return res;
    }

    // ===== view.json =====
    private static readonly Dictionary<string, string> TK = new()
    {
        ["shop"] = "端末 (ショップ)", ["fleet"] = "端末 (艦隊マネージャー)", ["commodity"] = "端末 (コモディティ)", ["cargo"] = "端末 (貨物センター)", ["refinery"] = "端末 (精製所)",
        ["freight"] = "端末 (貨物エレベーター)", ["shipelev"] = "端末 (船のエレベーター)", ["clinic"] = "クリニック", ["pharmacy"] = "薬局", ["clone"] = "再生端末",
    };

    private const double SameFloorZ = 2.0;
    private static byte[] WriteStationJson(MapData map, Loc loc, Func<string, string>? terminalLabel, List<LayoutTerminal> extra, Func<string, string, string>? shopLabel, double[] center)
    {
        var hangarLines = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in map.Transit.Where(t => t.Line.Length > 0))
        {
            if (t.Line.StartsWith(StationRouteGraph.HangarLiftLinePrefix, StringComparison.Ordinal)) { hangarLines.Add(t.Line); continue; }
            if (t.Kind != "elevator-noride") continue;
            // 同じ階か: 地図の階があれば階で、無ければ (2D の地図を使わない形) 乗り場の高さの差 SameFloorZ m 以内で見る
            var stops = map.Transit.Where(x => x.Line == t.Line && x.Kind.StartsWith("elevator", StringComparison.Ordinal)).ToList();
            var floors = stops.All(x => x.Floor.Length > 0) ? stops.Select(x => x.Floor).Distinct(StringComparer.Ordinal).Count()
                : stops.Max(x => x.Pos[2]) - stops.Min(x => x.Pos[2]) <= SameFloorZ ? 1 : 2;
            var dests = map.Transit.Count(x => x.Kind == "destination" && x.Line == t.Line);
            if (floors == 1 && dests <= 1) hangarLines.Add(t.Line);
        }
        // 案内するもの (利用者の指示「shop terminal admin hanger_lobby clinic pharmacy 再生端末 エレベーター、階段など、利用するものだけ」)。
        // 地点の範囲の内か外かは、ビューアが選んだ地点のまわりの立方体で決める
        // ja: 行き先の一覧と案内の見出しに出す名前 (2026-11-04、利用者の指示「行先は、艦隊マネージャーとかも日本語になる？」)。
        //   店 = アプリの地図の店の名前 (shopLabel。無いときは Entity 名のまま)。端末 = 端末の名前 (text) と階の名前 (アプリの地図の階の名前の訳)。
        //   同じ名前と階の端末が 2 つ以上あれば、並びの番号 (1 から) を後ろに付ける
        var floorName = map.Floors.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => loc.Name(g.First().Name), StringComparer.Ordinal);
        string FloorOf(string id, double[] pos)
        {
            if (id.Length > 0 && floorName.TryGetValue(id, out var n) && n.Length > 0) return n;
            // 階の無いもの (罰金の支払い端末など): 位置の高さ + 0.5 m 以下で、いちばん高い階。無ければいちばん近い階
            var fs = map.Floors.Where(f => double.IsFinite(f.Z)).ToList();
            if (fs.Count == 0 || !double.IsFinite(pos[2])) return "";
            var f = fs.Where(f => f.Z <= pos[2] + 0.5).OrderByDescending(f => f.Z).FirstOrDefault() ?? fs.OrderBy(f => Math.Abs(f.Z - pos[2])).First();
            return loc.Name(f.Name);
        }
        var targets = new List<object>();
        foreach (var s in map.Shops)
        {
            var lower = s.Name.ToLowerInvariant();
            var kind = lower.Contains("admin") ? "admin" : lower.Contains("pharmacy") ? "pharmacy" : "shop";
            var ja = shopLabel?.Invoke(s.Guid, s.Name) is { Length: > 0 } sl ? sl : s.Name;
            targets.Add(new { kind, label = kind switch { "admin" => "admin (店の名前に admin)", "pharmacy" => "薬局 (店の名前に pharmacy)", _ => "店" }, name = s.Name, ja, guid = s.Guid, pos = s.Pos, ok = map.ShopOk(s) });
        }
        var terms = new List<(string Kind, string Label, string Name, double[] Pos, string Text, double? Yaw, string Shop, string ShopGuid, string Floor)>();
        foreach (var t in map.Terminals)
            // text: 画面に浮かせて出す端末の名前 (2026-11-04、利用者の指示「ターミナルには何のターミナルか画面に書くといい」)。yaw: 地図の JSON の端末の向き (度)
            terms.Add(("terminal_" + t.Kind, TK.TryGetValue(t.Kind, out var l) ? l : "端末 (" + t.Kind + ")", t.Entity, t.Pos, terminalLabel?.Invoke(t.Kind) ?? t.Kind, t.Yaw, t.ShopEntity, t.ShopGuid, FloorOf(t.Floor, t.Pos)));
        // 罰金の支払い端末 (名前はゲームの訳 fine_terminal_title_001「罰金・違反金支払いシステム」)
        foreach (var t in extra.Where(t => t.Kind == "fine"))
            terms.Add(("terminal_fine", "端末 (罰金の支払い)", t.Entity, t.Pos, loc.Get("@fine_terminal_title_001"), null, "", "", FloorOf("", t.Pos)));
        // クエストのもの (EntityKinds)。名前は種類の言葉と EntityClass (ゲームの訳の名前は引いていない)
        foreach (var t in extra.Where(t => t.Kind is "questdoor" or "questitem" or "questpanel"))
        {
            var (label, word) = t.Kind switch { "questdoor" => ("クエストの扉", "クエストの扉"), "questitem" => ("カード・ヒューズを取る所", "取る所"), _ => ("クエストのパネル", "パネル") };
            terms.Add(("terminal_" + t.Kind, label, t.Entity, t.Pos, $"{word}: {t.Class}", null, "", "", FloorOf("", t.Pos)));
        }
        foreach (var grp in terms.GroupBy(t => (t.Text, t.Floor)))
        {
            var list = grp.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i];
                var ja = (t.Floor.Length > 0 ? $"{t.Text}（{t.Floor}）" : t.Text) + (list.Count > 1 ? $" {i + 1}" : "");
                targets.Add(new { kind = t.Kind, label = t.Label, name = t.Name, ja, pos = t.Pos, text = t.Text, yaw = t.Yaw, shop = t.Shop, shopGuid = t.ShopGuid, floor = t.Floor });
            }
        }
        foreach (var t in map.Transit)
        {
            if (t.Kind is not ("elevator" or "tram")) continue;
            var hl = t.Stop.Contains("hangar", StringComparison.OrdinalIgnoreCase);
            targets.Add(new { kind = hl ? "hangar_lobby" : t.Kind, label = hl ? "hangar_lobby (エレベーターの行き先に hangar)" : t.Kind == "elevator" ? "エレベーター" : "トラム", name = loc.Get(t.Stop), pos = t.Pos });
        }
        // ハンガーロビー (2026-11-05、利用者の指示「飛ぶ先に ハンガーロビー がないのはダメだと思う」「ハンガーロビーは入れた方がいい。ハンガーのエレベーターの行き先と
        // 関連してるはず」。親の決定): ハンガーへ行くエレベーターの、ハンガーへ下りる前に人がいる側の乗り場。
        //   ハンガーのエレベーターの路線 = ビューア (view3d.js の elevOrder) と同じ見分け: 行き先の鍵が dockingport_NN・pad_NN で終わる乗り場のある路線、
        //     ハンガーのエレベーター (上の hangarLines)、ハンガー行きの路線のタグ (HangarLineTags。ハンガーの入口のある地図だけ)。
        //   その路線ごとに、エレベーターの乗り場を行き先の鍵でまとめ (dockingport_NN・pad_NN の乗り場は除く)、艦隊マネージャーの端末 (種類 fleet。船をハンガーに呼ぶ端末) に
        //   いちばん近い乗り場のあるまとまりを、ハンガーロビーとする。艦隊マネージャーの端末の無い地図では決めない (行を足さない)。
        //   (版 8 の一覧の全部の場所で確かめた値: 選ばれるまとまりは端末から 5.1〜16.7 m (Lorville の Teasa Spaceport だけ 43.4 m)、同じ路線のほかの行き先 (貨物デッキ・精製デッキ・
        //    シップディーラーなど) は 229.9 m 以上。鍵は場所で違う: レストストップは @ui_interactor_lobby「ロビー」、Pyro のステーションは @ui_dest_entry_pyro「入口」、
        //    Levski は「税関へ」、都市は「Teasa Spaceport」「NBIS ターミナル」など。鍵に lobby を含む乗り場で選んでいた前の決まりの 45 か所は、同じ行になる)
        //   選んだまとまりを、路線ごとに 1 つの行にする (行き先の点 (destination) が 2 つ以上あっても分けない)。
        //   位置は、まとめた乗り場のうち行き先の点にいちばん近い乗り場。名前は鍵の訳 (ゲームの訳に「ハンガーロビー」に当たる言葉は無い)、乗り場が 2 つ以上なら「×数」。
        //   もとの乗り場の行 (エレベーター: ロビー) はそのまま残す (道案内のいちばん近い乗り口・乗り口の玉は今までのまま)
        {
            var padDock = new System.Text.RegularExpressions.Regex(@"(dockingport|pad)_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var elev = map.Transit.Where(t => t.Kind == "elevator" && t.Line.Length > 0).ToList();
            var liftLines = elev.Where(t => padDock.IsMatch(t.Stop)).Select(t => t.Line).Concat(hangarLines)
                .Concat(map.Hangars.Count > 0 ? HangarLineTags : []).Distinct(StringComparer.Ordinal).OrderBy(l => l, StringComparer.Ordinal).ToList();
            var fleets = map.Terminals.Where(t => t.Kind == "fleet").Select(t => t.Pos).ToList();
            static double D(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            foreach (var line in fleets.Count > 0 ? liftLines : [])
            {
                var grp = elev.Where(t => t.Line == line && !padDock.IsMatch(t.Stop)).GroupBy(t => t.Stop).MinBy(g => g.Min(s => fleets.Min(f => D(f, s.Pos))));
                if (grp == null) continue;
                var stops = grp.ToList();
                var dests = map.Transit.Where(t => t.Kind == "destination" && t.Line == line && t.Stop == grp.Key).Select(t => t.Pos).ToList();
                if (dests.Count == 0) dests.Add([stops.Average(s => s.Pos[0]), stops.Average(s => s.Pos[1]), stops.Average(s => s.Pos[2])]);
                // 路線ごとに 1 つの行 (2026-11-05、利用者の指示「×8」: Checkmate・Orbituary・Ruin Station は、同じロビーの「入口」の行き先の点が 3.9〜4.4 m 離れて
                // 2 つあり、点ごとに行を分けると「入口」が 2 行 (×5 と ×3) になっていた。同じ路線・同じ鍵の乗り場は 1 つの行「入口 ×8」にまとめる。
                // 配る一覧の全部の場所で、行が分かれていたのはこの 3 か所だけ)。位置は、行き先の点にいちばん近い乗り場
                {
                    var at = stops.MinBy(s => dests.Min(d => D(d, s.Pos)))!;
                    var n = stops.Count;
                    targets.Add(new { kind = "hangar_lobby", label = "ハンガーロビー", name = loc.Get(grp.Key) + (n > 1 ? $" ×{n}" : ""), pos = at.Pos });
                }
            }
        }
        foreach (var s in map.Stairs)
            targets.Add(new { kind = "stair", label = "階段 (下の端)", name = $"{s.From}→{s.To} ({s.Source})", ja = $"{FloorOf(s.From, s.Bottom)}→{FloorOf(s.To, s.Top)} ({s.Source})", pos = s.Bottom, top = s.Top });
        var o = new
        {
            format = Format,
            station = map.Record.StartsWith("StarMapObject.", StringComparison.Ordinal) ? map.Record["StarMapObject.".Length..] : map.Record,
            record = map.Record,
            targets,
            // [種類, 位置, 階, 路線, 行き先 (訳), 行き先の鍵, ハンガーのエレベーターなら 1]
            // ハンガーのエレベーター: アプリの地図 (StationMapPanel.HangarLifts) と同じ決まり (乗らない (elevator-noride) エレベーターのうち、同じ路線のエレベーターの乗り場が
            // 全部同じ階で、その路線の行き先 (destination) が 1 つ以下のもの)。ハンガーの地図では、路線が StationRouteGraph.HangarLiftLinePrefix で始まるもの
            transit = map.Transit.Select(t => new object[] { t.Kind, t.Pos, t.Floor, t.Line, loc.Get(t.Stop), t.Stop, hangarLines.Contains(t.Line) ? 1 : 0 }).ToList(),
            // この地図のハンガーの入口の大きさ (S・M・L・XL の順。マイハンガーの行に使う。アプリの地図のビューアの openElevator と同じ)
            hangarSizes = new[] { "S", "M", "L", "XL" }.Where(z => map.Hangars.Any(h => h.Size == z)).ToList(),
            // ハンガー行きのエレベーターの路線のタグ (2026-11-05、利用者の指示「3D でのハンガーのエレベーターで移動先がないのは困る」。親の決定: 路線のタグで見分ける)。
            // 路線 (transit の 4 番目) がこのどれかの乗り場は、ビューアでハンガーのエレベーターとして「マイハンガー」の行を出す (hangarSizes が空の地図では出ない)
            hangarLineTags = HangarLineTags,
            // 区画を置いた所の真ん中 (地図の無いハンガーの始める所に使う)
            center,
            // 前の地図の階 [Id, 名前 (鍵の所を訳したもの), 高さ, 元の名前 (並べる順に使う)]
            floors = map.Floors.Select(f => new object[] { f.Id, loc.Name(f.Name), f.Z, f.Name }).ToList(),
        };
        return JsonSerializer.SerializeToUtf8Bytes(o, JsonOpts);
    }

    // ===== 外から見たときの印 (着陸パッド・ハンガー・ドッキングポート・外部貨物エレベーター) =====
    // (2026-11-05、利用者の指示「Seraphim なんか引いて行って外部視点になったときパッドなんかは引き出し線とか前にさんざんやったのを表示するように」「一度作ってんだから、今回入れてよ。外部になったときだけ出して」
    //  「外部の着陸パッド(番号必要)が引き出し線とキャプションが出てない。ドッキングポート(番号必要)や貨物エレベーターなど外部の施設の場所はわからないといけない」)
    // v2.5.3 の旧方式のステーションの 3D (4bfc288:Station3DExports.WriteScene の markers。pads・hangars・docks・freight) の移植。ビューア (view3d.js) が station.json を読んだ後に
    // {type:'exteriorGet'} で頼み、アプリが {type:'exterior', pads, hangars, docks, freight, roofDoors} で返す (StationView3DWindow)。pos は場所の座標 (z 上)。
    // 中身は、区画の置き方の一覧の DB を作るとき (BuildLayoutDb の PlaceOf) に木 (StationTree。contents: true) の Exteriors から入れたもの (place_exts・place_hangars・place_roofdoors)。
    // 表示のたびには .soc を読まない (2026-11-05、親の決定。利用者の「DB に予め入れておくのがいいんじゃない？」の考え方)。DB にその場所の行が無ければ空 (出さない)。
    //   pads  = 着陸パッド (exterior の pad = EntityClass「LandingArea」。LandingArea の canBeUsedBy="AI" は StationTree が外してある)。key は LandingArea の HUDDisplayName。
    //   docks = 外部のドッキングポート (exterior の dock = EntityClass「Station_DockingTube」。SCItemDockingTube の CanBeUsedBy="AI" は外してある)。key は SCItemDockingTube の HUDDisplayName。
    //   freight = 外部貨物エレベーター (exterior の freight = LoadingPlatformManager ごと。位置は platform の Entity の Pos、無ければ manager の Pos)。
    //     text は旧方式の Station3DWeb.Labels の ["freight"] = FreightWord「外部貨物EV」(2026-11-02、利用者の決定「外部貨物EV ×6」)。
    //   text = 鍵のゲームの訳 (Loc。無ければ鍵の「@」を除いたもの。旧方式 Station3DWeb.Labels と同じ)。鍵が空なら ""。
    //   hangars = ハンガーの入口 (place_hangars = exterior の hangar。size は GatewaySize の区分 S・M・L・XL、無ければ "")。
    //   roofDoors = 屋根の扉の候補 [{origin, center}] (DB の place_roofdoors。RoofDoorsOf。ハンガーの印を動かすのはビューア)。
    //   pos は旧方式の scene.json と同じく小数 3 桁。p4kPath・ct は使わない (前の形の呼び出しのまま)
    public const string FreightWord = "外部貨物EV";
    // ハンガー行きのエレベーターの路線のタグ (station.json の hangarLineTags)。DataCore の tagdatabase の名前:
    //   37d8b7b6-58df-48d9-ad06-bcc0885a81b8 = Spaceport_Hangar (StationRouteGraph.HangarLiftTag。Orison・Lorville・New Babbage・Levski などの路線の管理)
    //   3906bff7-aa71-4aa3-94b8-3164ece13ea2 = TransportManager_Generic_Hangar (Area18・GrimHEX の路線の管理)
    public static readonly string[] HangarLineTags = [StationRouteGraph.HangarLiftTag, "3906bff7-aa71-4aa3-94b8-3164ece13ea2"];
    public static object ExteriorMarks(string p4kPath, Layout layout, Loc loc, CancellationToken ct = default)
    {
        string Text(string key)
        {
            if (key.Length == 0) return "";
            var t = loc.Get(key);
            return t == key ? key.TrimStart('@') : t;
        }
        static double[] R3(double[] p) => [Math.Round(p[0], 3), Math.Round(p[1], 3), Math.Round(p[2], 3)];
        var exts = layout.Place?.Exts ?? new();
        var pads = exts.Where(x => x.Kind == "pad").Select(x => (object)new { pos = R3(x.Pos), key = x.Key, text = Text(x.Key) }).ToList();
        var docks = exts.Where(x => x.Kind == "dock").Select(x => (object)new { pos = R3(x.Pos), key = x.Key, text = Text(x.Key) }).ToList();
        var freight = exts.Where(x => x.Kind == "freight").Select(x => (object)new { pos = R3(x.Pos), text = FreightWord }).ToList();
        var hangars = (layout.Place?.Hangars ?? new()).Select(h => new { pos = R3(h.Pos), size = h.Size }).ToList();
        // 屋根の扉の候補 (区画の置き方の一覧の DB の place_roofdoors。RoofDoorsOf)。印を動かすのはビューア (view3d.js の onExterior。旧方式と同じく buildMarks の後) で、ここでは pos を動かさない。
        // DB にその場所の行が無ければ空 (印は動かさない)
        var roofDoors = (layout.RoofDoors ?? new()).Select(r => new { origin = r.Origin, center = r.Center }).ToList();
        return new { type = "exterior", pads, hangars, docks, freight, roofDoors };
    }

    // ===== 屋根の扉の候補 (旧方式 v2.5.4 wwwroot/station3d/viewer.html の roofDoors の移植。2026-11-05、利用者の指示「一度作ってんだから、今回入れてよ。外部になったときだけ出して」) =====
    // 旧方式はビューアが読み込んだステーション全体の形 (GLB) の全部の部品 × 置いた所から、水平の板 (高さが ROOF_DOOR_THIN 未満、横がどちらも ROOF_DOOR_WIDE より大きい) を集め、
    // その「置いた原点」と「箱の中心」を持っていた (viewer.html 1063〜1088 行)。今のビューアは地点の ±R m の形しか持たないので、同じ集め方を区画の置き方の一覧の DB を作るとき
    // (BuildLayoutDb。場所ごと) に行い、place_roofdoors に入れておく (表示のたびには探さない。2026-11-05、親の決定)。ExteriorMarks は DB の分 (Layout.RoofDoors) を返す。
    //   探す区画: 旧方式の GLB に入れていた範囲 (Layout.RoofRange = 深さ 0・1 と、socpak のパスに /ext/ を含むもの。4bfc288:Station3DExports の MaxPartDepth・ExtPathPart)。
    //   形: SocpakExporter.Export (OwnExtractor と同じ SocpakExporter.Shared(p4kPath, 1))。行列は PartsOf と同じ掛け方 M = LMat(区画の子の Matrix) × LMat(置き方の Matrix) に、
    //   インスタンスの行列 A (3 行 4 列・行優先) を掛けた W = A·M (場所の座標、z 上)。
    //   箱: 旧と同じく形の箱 (Mesh.Min/Max = accessor の min・max。three.js の geometry.boundingBox) の 8 隅を W で移した箱 (Box3.applyMatrix4)。
    //   旧の画面は y 上なので、旧の「高さ (y) < 10、横 (x・z) > 30」は、z 上の今の座標では「高さ (z) < 10、横 (x・y) > 30」。
    //   origin = W の平行移動 (旧の setFromMatrixPosition(m))、center = 箱の中心 (旧の tmp.getCenter)。どちらも場所の座標 (m)。
    // 旧の定数 (viewer.html 412〜413 行): 写しの扉の部品は、屋根の扉 XL が 202 × 320 × 4 m の板 (GrimHEX)、正面の扉は高さ 42〜66 m・厚さ 4 m の縦の板
    private const double RoofDoorThin = 10, RoofDoorWide = 30;   // 旧の ROOF_DOOR_THIN・ROOF_DOOR_WIDE (ROOF_DOOR_JOIN = 2 は動かす側の view3d.js)
    private const int RoofMaxPartDepth = 1;            // 旧の Station3DExports.MaxPartDepth
    private const string RoofExtPathPart = "/ext/";    // 旧の Station3DExports.ExtPathPart
    // 置き場 (cache) に持つ部品の下限: 箱の 8 隅を M で移した形のいちばん長い対角線 (m)。区画を置く行列 A の行の長さが RoofCacheRowMax 以下なら、
    // 場所の座標の箱の横の広がりは「対角線 × 行の長さ」以下なので、これより短い部品は横 30 m を超えられない (結果は変わらない。メモリを減らすため)
    private const double RoofCacheRowMax = 2, RoofCacheMinDiag = RoofDoorWide / RoofCacheRowMax;
    // socpak → 部品 (区画の子の順・置き方の順) の行列 M と形の箱 (minDiag 以下の部品は入れない)。読めなければ null
    private static List<(double[] M, double[] Min, double[] Max)>? RoofParts(SocpakExporter.Context ctx, string socpak, double minDiag, CancellationToken ct)
    {
        static double[] LMat(double[] m) { var M = new double[16]; for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) M[r * 4 + c] = m[c * 4 + r]; return M; }
        static double[] Mul(double[] a, double[] b)
        {
            var c = new double[16];
            for (int r = 0; r < 4; r++) for (int k = 0; k < 4; k++) { double s = 0; for (int j = 0; j < 4; j++) s += a[r * 4 + j] * b[j * 4 + k]; c[r * 4 + k] = s; }
            return c;
        }
        try
        {
            var list = MatchSocpaksCached(ctx.P4k, socpak.Replace('/', '\\'));
            if (list.Count == 0) return null;
            var scene = SocpakExporter.Export(ctx, list, ct: ct);
            var parts = new List<(double[] M, double[] Min, double[] Max)>();
            foreach (var c in scene.Containers)
            {
                var C = LMat(c.Matrix);
                foreach (var pl in c.Placements)
                {
                    // 形の無い部品は見ない。旧方式の GLB (StarBreaker の socpak export。移植の SocpakGlb.Builder.PackMesh) は、隠す submesh (ShouldHide・IsDecal) を primitives に書かず、
                    // primitives が空の mesh は three.js の GLTFLoader.loadMesh で中身の無い Group になり (isMesh でない)、旧のビューアは o.isMesh だけを集めていた (viewer.html 1037 行)。
                    // SocpakScene の Primitives は同じ条件 (CryMtl.HideWithoutTextures) で入れているので、Primitives が空 = 旧で Mesh にならない部品。頂点が 0 の部品も面を持たない (SocpakScene の LoadCgf)
                    if (pl.Mesh.Positions.Length == 0 || pl.Mesh.Primitives.Count == 0) continue;
                    var M = Mul(C, LMat(pl.Matrix));
                    double[] mn = pl.Mesh.Min, mx = pl.Mesh.Max;
                    double dx = mx[0] - mn[0], dy = mx[1] - mn[1], dz = mx[2] - mn[2], diag = 0;
                    foreach (var (sx, sy) in new[] { (1.0, 1.0), (1.0, -1.0), (-1.0, 1.0), (-1.0, -1.0) })
                    {
                        double ux = M[0] * dx + M[1] * sx * dy + M[2] * sy * dz, uy = M[4] * dx + M[5] * sx * dy + M[6] * sy * dz, uz = M[8] * dx + M[9] * sx * dy + M[10] * sy * dz;
                        diag = Math.Max(diag, Math.Sqrt(ux * ux + uy * uy + uz * uz));
                    }
                    if (diag <= minDiag) continue;
                    parts.Add((M, mn, mx));
                }
            }
            return parts;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException or SocpakExporter.SocpakException) { return null; }
    }
    // 1 か所の屋根の扉の候補 (Layout.RoofRange の区画。並びは区画の順 → 区画の子の順 → 置き方の順)。cache: socpak ごとの部品 (場所どうしで使い回す。RoofCacheMinDiag で絞ったもの)
    internal static List<RoofDoor> RoofDoorsOf(SocpakExporter.Context ctx, Layout layout, System.Collections.Concurrent.ConcurrentDictionary<string, List<(double[] M, double[] Min, double[] Max)>?> cache, CancellationToken ct = default)
    {
        var res = new List<RoofDoor>();
        foreach (var ii in layout.RoofRange ?? Enumerable.Range(0, layout.Instances.Count).ToList())
        {
            ct.ThrowIfCancellationRequested();
            var inst = layout.Instances[ii];
            if (inst.Socpak.Length == 0) continue;
            var A = inst.A;
            // 行の長さが RoofCacheRowMax を超える置き方 (未確認。ふつうは回転だけで 1) は、置き場の絞りが効かないので、絞らずに読み直す
            var rowMax = Math.Max(Math.Sqrt(A[0] * A[0] + A[1] * A[1] + A[2] * A[2]), Math.Sqrt(A[4] * A[4] + A[5] * A[5] + A[6] * A[6]));
            var parts = rowMax <= RoofCacheRowMax ? cache.GetOrAdd(inst.Socpak, s => RoofParts(ctx, s, RoofCacheMinDiag, ct)) : RoofParts(ctx, inst.Socpak, -1, ct);
            if (parts == null) continue;
            var W = new double[12];
            foreach (var (M, mn, mx) in parts)
            {
                // W = A (3×4) · M (4×4)
                for (int r = 0; r < 3; r++) for (int k = 0; k < 4; k++)
                    W[r * 4 + k] = A[r * 4] * M[k] + A[r * 4 + 1] * M[4 + k] + A[r * 4 + 2] * M[8 + k] + A[r * 4 + 3] * M[12 + k];
                double lx = double.MaxValue, ly = double.MaxValue, lz = double.MaxValue, hx = double.MinValue, hy = double.MinValue, hz = double.MinValue;
                for (int cn = 0; cn < 8; cn++)
                {
                    double x = (cn & 1) != 0 ? mx[0] : mn[0], y = (cn & 2) != 0 ? mx[1] : mn[1], z = (cn & 4) != 0 ? mx[2] : mn[2];
                    double vx = W[0] * x + W[1] * y + W[2] * z + W[3], vy = W[4] * x + W[5] * y + W[6] * z + W[7], vz = W[8] * x + W[9] * y + W[10] * z + W[11];
                    if (vx < lx) lx = vx; if (vx > hx) hx = vx;
                    if (vy < ly) ly = vy; if (vy > hy) hy = vy;
                    if (vz < lz) lz = vz; if (vz > hz) hz = vz;
                }
                // 屋根の扉の候補: 水平の板 (高さ (z) が RoofDoorThin 未満、横 (x・y) がどちらも RoofDoorWide より大きい) の部品の、置いた原点と箱
                if (hz - lz < RoofDoorThin && hx - lx > RoofDoorWide && hy - ly > RoofDoorWide)
                    res.Add(new RoofDoor([W[3], W[7], W[11]], [(lx + hx) / 2, (ly + hy) / 2, (lz + hz) / 2]));
            }
        }
        return res;
    }
}
