using System.Collections.Concurrent;
using System.IO;

namespace StarCitizenJapaneseTextCreater;

// 粗い形 (LOD1) で抜けた床の面を、細かい形 (LOD0) から求めて補う (2026-11-04、利用者の指示
// 「粗い形で抜けた床と当たりの面を、部品の種類ごとに DB に入れ、補って入れてしまえばいい」「裏作業でやって、DB に入れこめばいい」)。
//   部品の種類 = 形のパス (SocpakScene.Mesh.CgfPath)。表 part_fill(kind, tris): tris は部品の局所の座標の三角形 (float32 x9 の並び、リトルエンディアン)。
//   補う面 = LOD0 の上向きの面 (法線の z >= cos 46°。局所の +z を上とみなす) のうち、LOD1 の上向きの面 (法線の z >= 0.5) で覆われていない所。
//   覆われている = 点 (x, y) の上に LOD1 の上向きの三角形があり、その面の高さと点の高さの差が ZTol 以内。
//   覆われていない所だけを三角形で出す (三角形を 4 つに割って、全部覆われていれば捨て、1 つも覆われていなければそのまま、混ざっていれば MaxDepth まで割る)。
//   描くときは、元の部品と同じ名前・同じ置き方の追加の部品として足す (StationView3D.OwnExtractor)。
internal static class StationFill
{
    public const string TableName = "part_fill";
    // 値 (担当が決めたもの。利用者の指示に無い所)
    public const double ZTol = 0.05;        // 高さの差 (m)
    public const double CoverNz = 0.5;      // 覆う側 (LOD1) の上向きの法線の z の下限
    public const int MaxDepth = 4;          // 三角形を割る回数の上限 (辺の 1/16)
    public const double MinArea = 1e-6;     // これ未満の面積の三角形は捨てる (m²)
    private static readonly double CosWalk = Math.Cos(46 * Math.PI / 180);

    // ===== 求め方 =====
    // t0 = LOD0 の三角形 (x9)、t1 = LOD1 の三角形 (x9)。補う面の三角形 (x9) を返す。無ければ null
    // lab0・lab1: アプリの床の判定 (判定 2。StationView3D.WalkLabelsOf) の三角形ごとの結果。あれば、LOD0 の「歩ける面」(床・坂・筒の底) だけを対象にし、LOD1 の「歩ける面」だけで覆う。
    // 無ければ (試しの道具の古い比べ方) 法線だけで決める
    public static float[]? FillOf(float[] t0, float[] t1, byte[]? lab0 = null, byte[]? lab1 = null)
    {
        int n1 = t1.Length / 9;
        // LOD1 の上向きの三角形の升 (xy の 0.5 m)
        const double cell = 0.5;
        var up = new List<int>();

        for (int t = 0; t < n1; t++)
        {
            var nz = NormalZ(t1, t * 9);
            if (lab1 != null ? lab1[t] is >= 1 and <= 3 : nz >= CoverNz) up.Add(t);
        }
        var grid = new Dictionary<(int, int), List<int>>();
        var big = new List<int>();
        foreach (var t in up)
        {
            int b = t * 9;
            int i0 = (int)Math.Floor(Math.Min(t1[b], Math.Min(t1[b + 3], t1[b + 6])) / cell), i1 = (int)Math.Floor(Math.Max(t1[b], Math.Max(t1[b + 3], t1[b + 6])) / cell);
            int j0 = (int)Math.Floor(Math.Min(t1[b + 1], Math.Min(t1[b + 4], t1[b + 7])) / cell), j1 = (int)Math.Floor(Math.Max(t1[b + 1], Math.Max(t1[b + 4], t1[b + 7])) / cell);
            if ((long)(i1 - i0 + 1) * (j1 - j0 + 1) > 2048) { big.Add(t); continue; }
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    if (!grid.TryGetValue((i, j), out var l)) grid[(i, j)] = l = new List<int>();
                    l.Add(t);
                }
        }
        bool Covered(double x, double y, double z)
        {
            bool Hit(int t)
            {
                int b = t * 9;
                double ax = t1[b], ay = t1[b + 1], az = t1[b + 2];
                double v0x = t1[b + 3] - ax, v0y = t1[b + 4] - ay, v1x = t1[b + 6] - ax, v1y = t1[b + 7] - ay, wx = x - ax, wy = y - ay;
                double den = v0x * v1y - v1x * v0y;
                if (Math.Abs(den) < 1e-12) return false;
                double s = (wx * v1y - v1x * wy) / den, u = (v0x * wy - wx * v0y) / den;
                const double eps = 1e-6;
                if (s < -eps || u < -eps || s + u > 1 + eps) return false;
                double zp = az + s * (t1[b + 5] - az) + u * (t1[b + 8] - az);
                return Math.Abs(zp - z) <= ZTol;
            }
            if (grid.TryGetValue(((int)Math.Floor(x / cell), (int)Math.Floor(y / cell)), out var l))
                foreach (var t in l) if (Hit(t)) return true;
            foreach (var t in big) if (Hit(t)) return true;
            return false;
        }
        // 三角形の割り方・判定は元のまま。速くするためだけの直し: 配列を作らず (数値のまま持ち回る)、7 点の判定は「覆われている点と覆われていない点が両方出た」時点で打ち切る
        // (結果は 全部覆われている / 1 つも覆われていない / 混ざっている の 3 通りだけで決まるため同じ)。LOD0 の三角形は 256 個ずつの塊で並列に処理し、塊の順に並べ直す
        const int Chunk = 256;
        int n0 = t0.Length / 9;
        int nChunks = (n0 + Chunk - 1) / Chunk;
        var outs = new float[nChunks][];
        void DoChunk(int ci)
        {
            var res = new List<float>();
            bool Emit(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
            {
                double ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                if (Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2 < MinArea) return false;
                res.Add((float)ax); res.Add((float)ay); res.Add((float)az); res.Add((float)bx); res.Add((float)by); res.Add((float)bz); res.Add((float)cx); res.Add((float)cy); res.Add((float)cz);
                return true;
            }
            // 戻り: 0 = 何も出さなかった、1 = この三角形をそのまま 1 つ出した、2 = 一部を出した。4 つの子が全部「そのまま」なら、子を取り消して親をそのまま出す (三角形の数を減らす)
            int Walk(double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz, int depth)
            {
                double abx = (ax + bx) / 2, aby = (ay + by) / 2, abz = (az + bz) / 2;
                double bcx = (bx + cx) / 2, bcy = (by + cy) / 2, bcz = (bz + cz) / 2;
                double cax = (cx + ax) / 2, cay = (cy + ay) / 2, caz = (cz + az) / 2;
                double gx = (ax + bx + cx) / 3, gy = (ay + by + cy) / 3, gz = (az + bz + cz) / 3;
                bool any = false, none = false, mixed = false;
                void Chk(double x, double y, double z) { if (mixed) return; if (Covered(x, y, z)) any = true; else none = true; if (any && none) mixed = true; }
                Chk(ax, ay, az); Chk(bx, by, bz); Chk(cx, cy, cz); Chk(abx, aby, abz); Chk(bcx, bcy, bcz); Chk(cax, cay, caz); Chk(gx, gy, gz);
                if (!mixed)
                {
                    if (!none) return 0;   // 7 点とも覆われている
                    return Emit(ax, ay, az, bx, by, bz, cx, cy, cz) ? 1 : 0;   // 1 つも覆われていない
                }
                if (depth >= MaxDepth) return !Covered(gx, gy, gz) && Emit(ax, ay, az, bx, by, bz, cx, cy, cz) ? 1 : 0;
                int start = res.Count;
                int s1 = Walk(ax, ay, az, abx, aby, abz, cax, cay, caz, depth + 1), s2 = Walk(abx, aby, abz, bx, by, bz, bcx, bcy, bcz, depth + 1),
                    s3 = Walk(cax, cay, caz, bcx, bcy, bcz, cx, cy, cz, depth + 1), s4 = Walk(abx, aby, abz, bcx, bcy, bcz, cax, cay, caz, depth + 1);
                if (s1 == 1 && s2 == 1 && s3 == 1 && s4 == 1) { res.RemoveRange(start, res.Count - start); return Emit(ax, ay, az, bx, by, bz, cx, cy, cz) ? 1 : 0; }
                return s1 + s2 + s3 + s4 == 0 ? 0 : 2;
            }
            int end = Math.Min(n0, (ci + 1) * Chunk);
            for (int t = ci * Chunk; t < end; t++)
            {
                int b = t * 9;
                if (lab0 != null ? lab0[t] is < 1 or > 3 : NormalZ(t0, b) < CosWalk) continue;
                Walk(t0[b], t0[b + 1], t0[b + 2], t0[b + 3], t0[b + 4], t0[b + 5], t0[b + 6], t0[b + 7], t0[b + 8], 0);
            }
            outs[ci] = res.ToArray();
        }
        if (nChunks > 1) Parallel.For(0, nChunks, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, DoChunk);
        else if (nChunks == 1) DoChunk(0);
        int total = 0; foreach (var o in outs) total += o.Length;
        if (total == 0) return null;
        var all = new float[total]; int pos = 0;
        foreach (var o in outs) { Buffer.BlockCopy(o, 0, all, pos * 4, o.Length * 4); pos += o.Length; }
        return all;
    }

    private static double NormalZ(float[] v, int b)
    {
        double ux = v[b + 3] - v[b], uy = v[b + 4] - v[b + 1], uz = v[b + 5] - v[b + 2], vx = v[b + 6] - v[b], vy = v[b + 7] - v[b + 1], vz = v[b + 8] - v[b + 2];
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx, ln = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        return cz / Math.Max(ln, 1e-12);
    }

    // SocpakScene.Mesh → 三角形 (x9。StationView3D.PartsOf と同じ並べ方)
    public static float[] TrisOf(SocpakScene.Mesh m)
    {
        var outl = new List<float>();
        var P = m.Positions;
        foreach (var ix in m.Primitives)
        {
            int nt = ix.Length / 3 * 3;
            for (int i = 0; i < nt; i++) { long v = ix[i]; outl.Add(P[v * 3]); outl.Add(P[v * 3 + 1]); outl.Add(P[v * 3 + 2]); }
        }
        return outl.ToArray();
    }

    // LOD0 と LOD1 で形が別にあるか。p4k に形の _lod1 の対 (.cgfm) が無ければ LOD1 は LOD0 と同じ形 (補う面は無い)。
    // p4k に元の形 (.cgf) も無ければ socpak の中の形などで分からないので true (求めてみる)
    public static bool HasLodVariant(P4kArchive p4k, string cgfPath, int lod)
    {
        if (cgfPath.EndsWith(".cdf", StringComparison.OrdinalIgnoreCase)) return true;
        var geomP4k = SocpakExporter.DataCorePathToP4k(cgfPath.Replace('\\', '/'));
        if (p4k.Find(geomP4k) < 0 && p4k.Find(geomP4k + "m") < 0) return true;
        int dot = geomP4k.LastIndexOf('.');
        var lodGeom = dot >= 0 ? $"{geomP4k[..dot]}_lod{lod}{geomP4k[dot..]}" : $"{geomP4k}_lod{lod}";
        return p4k.Find(lodGeom + "m") >= 0;
    }

    // ===== 50 cm 未満の断片を外す =====
    // 利用者の原文「50cm で落下判定するなら 50cm 以上はやるべき」を、親が「穴の中に 50 cm の幅が入るか (穴の最小の幅が 50 cm 以上か)」と決めた (原文に無い測り方)。
    //   穴 = 補う三角形のうち、頂点がまったく同じ点を共有してつながるもの (z の違う別の階はつながらない)。
    //   穴の xy の射影を RasterRes の升に塗り、各升から、塗っていない升までの距離 (升の中心どうし) を求め、
    //   距離 - 升の半分 が MinHoleWidth/2 以上の升があれば、直径 50 cm の円が入る穴として残す。無ければ穴ごと捨てる。
    //   (2026-11-05、利用者「実際荒いのと詳細で違いがあって落ちるすき間になるものは小さいはず」「短辺が3m を切る」) 幅 (入る円の最大の直径) が MaxHoleWidth 以上の穴は、
    //   落ちるすき間ではなく、ドーム・外側の輪などの大きな面の差なので補わない。升の数が MaxRasterCells を超える穴 (局所の広がりが 500 m 級) も同じ扱いで捨てる (親の決定)。
    public const double MinHoleWidth = 0.5, MaxHoleWidth = 10.0;   // (2026-11-05、利用者「基準を10m 未満にするのは？」: 3 m では店の床 (TDD の中央の床 3.2 m・ダンスフロア 5.6 m) が落ちた)

    // ===== 高さのある立体構造を省く =====
    // (2026-11-05、利用者「床でない 高さのある立体構造は省くべき」「床なら厚みが薄いんじゃないの？」) 形 1 つの補う面の z の広がりが MaxFillHeight 以上なら、
    // ドーム・トラムのトンネル・外側のタンクやパイプ・背景の建物 (10 m〜数 km) なので補わない。床の部品は 0〜5 m (Orison のトラム乗り場は階段の下まで入れて 3.5 m)。
    // 線の値は全 2,752 種の実データの分布から親が決めた (5〜10 m はステーション外側の輪・腕・ハンガーの縁、10 m 以上はトンネル・外構・建物)
    public const double MaxFillHeight = 10.0;
    public static float[]? KeepThin(float[]? f)
    {
        if (f == null || f.Length == 0) return null;
        float z0 = float.MaxValue, z1 = float.MinValue;
        for (int i = 2; i < f.Length; i += 3) { if (f[i] < z0) z0 = f[i]; if (f[i] > z1) z1 = f[i]; }
        return z1 - z0 >= MaxFillHeight ? null : f;
    }
    private const double RasterRes = 0.125;
    private const long MaxRasterCells = 1L << 24;
    // report: 穴ごとの (x の幅, y の幅, 穴の幅 (入る円の最大の直径。測れないときは -1), 三角形の数, 残したか) を知らせる (試しの道具の表示用)
    public static float[]? KeepWide(float[] f, Action<double, double, double, int, bool>? report = null)
    {
        int n = f.Length / 9;
        if (n == 0) return null;
        var parent = new int[n]; for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        var vmap = new Dictionary<(int, int, int), int>();
        for (int t = 0; t < n; t++)
            for (int k = 0; k < 3; k++)
            {
                int b = t * 9 + k * 3;
                var key = (BitConverter.SingleToInt32Bits(f[b]), BitConverter.SingleToInt32Bits(f[b + 1]), BitConverter.SingleToInt32Bits(f[b + 2]));
                if (vmap.TryGetValue(key, out var o)) { int ra = Find(t), rb = Find(o); if (ra != rb) parent[ra] = rb; }
                else vmap[key] = t;
            }
        var groups = new Dictionary<int, List<int>>();
        for (int t = 0; t < n; t++) { int r = Find(t); if (!groups.TryGetValue(r, out var l)) groups[r] = l = new(); l.Add(t); }
        var keep = new bool[n];
        foreach (var g in groups.Values)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var t in g) for (int k = 0; k < 3; k++) { int b = t * 9 + k * 3; x0 = Math.Min(x0, f[b]); x1 = Math.Max(x1, f[b]); y0 = Math.Min(y0, f[b + 1]); y1 = Math.Max(y1, f[b + 1]); }
            if (x1 - x0 < MinHoleWidth || y1 - y0 < MinHoleWidth) { report?.Invoke(x1 - x0, y1 - y0, 0, g.Count, false); continue; }   // 外形の幅が 50 cm 未満なら直径 50 cm の円は入らない
            int W = (int)Math.Ceiling((x1 - x0) / RasterRes) + 3, H = (int)Math.Ceiling((y1 - y0) / RasterRes) + 3;
            bool ok; double width = -1;
            if ((long)W * H > MaxRasterCells) ok = false;
            else
            {
                var d = new float[W * H];
                const float Inf = 1e12f;
                for (int i = 0; i < d.Length; i++) d[i] = 0;
                foreach (var t in g)
                {
                    int b = t * 9;
                    double ax = f[b], ay = f[b + 1], bx = f[b + 3], by = f[b + 4], cx = f[b + 6], cy = f[b + 7];
                    double den = (by - ay) * (cx - ax) - (bx - ax) * (cy - ay);
                    if (Math.Abs(den) < 1e-12) continue;
                    int i0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) - x0) / RasterRes) + 1), i1 = Math.Min(W - 1, (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - x0) / RasterRes) + 1);
                    int j0 = Math.Max(0, (int)Math.Floor((Math.Min(ay, Math.Min(by, cy)) - y0) / RasterRes) + 1), j1 = Math.Min(H - 1, (int)Math.Floor((Math.Max(ay, Math.Max(by, cy)) - y0) / RasterRes) + 1);
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            double px = x0 + (i - 1 + 0.5) * RasterRes - ax, py = y0 + (j - 1 + 0.5) * RasterRes - ay;
                            // 重心座標 (a から b・c へ)
                            double s = (px * (cy - ay) - (cx - ax) * py) / ((bx - ax) * (cy - ay) - (cx - ax) * (by - ay));
                            double w = ((bx - ax) * py - px * (by - ay)) / ((bx - ax) * (cy - ay) - (cx - ax) * (by - ay));
                            if (s >= 0 && w >= 0 && s + w <= 1) d[j * W + i] = Inf;
                        }
                }
                // 距離の 2 乗 (Felzenszwalb。塗った升 = Inf、塗っていない升 = 0)
                int m = Math.Max(W, H);
                var line = new double[m]; var dd = new double[m]; var vv = new int[m]; var zz = new double[m + 1];
                void Pass(int len, Func<int, int> idx)
                {
                    for (int q = 0; q < len; q++) line[q] = d[idx(q)];
                    int k = 0; vv[0] = 0; zz[0] = double.NegativeInfinity; zz[1] = double.PositiveInfinity;
                    for (int q = 1; q < len; q++)
                    {
                        double sx;
                        while (true)
                        {
                            sx = ((line[q] + (double)q * q) - (line[vv[k]] + (double)vv[k] * vv[k])) / (2.0 * q - 2.0 * vv[k]);
                            if (sx <= zz[k]) k--; else break;
                            if (k < 0) { k = 0; break; }
                        }
                        k++; vv[k] = q; zz[k] = sx; zz[k + 1] = double.PositiveInfinity;
                    }
                    k = 0;
                    for (int q = 0; q < len; q++) { while (zz[k + 1] < q) k++; dd[q] = (double)(q - vv[k]) * (q - vv[k]) + line[vv[k]]; }
                    for (int q = 0; q < len; q++) d[idx(q)] = (float)Math.Min(dd[q], Inf);
                }
                for (int i = 0; i < W; i++) { int ii = i; Pass(H, q => q * W + ii); }
                for (int j = 0; j < H; j++) { int jj = j; Pass(W, q => jj * W + q); }
                // 穴の幅 = 入る円の最大の直径 (塗った升の中心から塗っていない升までの距離の最大 − 升の半分、の 2 倍)
                double maxR = 0;
                for (int i = 0; i < d.Length; i++) if (d[i] > 0) maxR = Math.Max(maxR, Math.Sqrt(d[i]) * RasterRes - RasterRes / 2);
                width = maxR * 2;
                ok = width >= MinHoleWidth && width < MaxHoleWidth;
            }
            report?.Invoke(x1 - x0, y1 - y0, width, g.Count, ok);
            if (ok) foreach (var t in g) keep[t] = true;
        }
        var res = new List<float>();
        for (int t = 0; t < n; t++) if (keep[t]) for (int k = 0; k < 9; k++) res.Add(f[t * 9 + k]);
        return res.Count > 0 ? res.ToArray() : null;
    }

    // ===== 比べる対象 =====
    // 建物の部品か (2026-11-04、利用者の指示「建物以外は意味ない」)。形のパス (先頭の data/ は無視。大文字小文字は区別しない) が
    // objects/buildingsets/ (建物の部品・町のセット) か objectcontainers/pu/loc/・objectcontainers/pu/shops/ (区画の中に入っている形) の下のもの。
    // 実データ (全 4787 socpak の形 48,534 種) のパスの上位で決めた。外れる: objects/spaceships・objects/planets・objects/space (小惑星など)・objects/characters・objectcontainers/pu/derelict など
    // 形のパスの正規化 (先頭の data/ を除き、小文字、区切りは /)。「data/objects/…」と「objects/…」を同じ形にする
    public static string Norm(string cgfPath)
    {
        var p = cgfPath.Replace('\\', '/').ToLowerInvariant();
        return p.StartsWith("data/", StringComparison.Ordinal) ? p[5..] : p;
    }

    public static bool IsBuildingKind(string cgfPath)
    {
        var p = cgfPath.Replace('\\', '/').ToLowerInvariant();
        if (p.StartsWith("data/", StringComparison.Ordinal)) p = p[5..];
        return p.StartsWith("objects/buildingsets/", StringComparison.Ordinal) || p.StartsWith("objectcontainers/pu/loc/", StringComparison.Ordinal) || p.StartsWith("objectcontainers/pu/shops/", StringComparison.Ordinal);
    }

    // 床に相当する形か (利用者の指示「”床” に相当する場所のみ。壁や天井は要らない。チューブは床相当部分」)。
    // 建物の部品で、名前の語に ceiling・wall が無いもの (StationView3D の除外の決まりのうち ceiling・wall だけ。counter の語があれば掛けない。light と 1 m 未満は掛けない)。
    // 上向きの面だけを補うので (FillOf)、床・坂・筒の底の面が対象になり、壁・天井の面は対象にならない
    public static bool IsFloorKind(string cgfPath)
    {
        if (!IsBuildingKind(cgfPath)) return false;
        var last = cgfPath[(cgfPath.LastIndexOfAny(['/', '\\']) + 1)..];
        if (last.EndsWith(".cgf", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        var w = StationFloorBuilder.MeshWords(last);
        return w.Contains("counter") || !(w.Contains("ceiling") || w.Contains("wall"));
    }

    // ===== 印 (パッチで変わったかの照会用。Data.p4k の目次の CRC32 と大きさ) =====
    // 形の印: LOD0 の形 (.cgf・.cgfm) と LOD1 の形 (_lod1 の .cgfm・.cgf) の CRC と大きさ。無いものは「-」。
    // (材質など形のファイル以外が変わった場合は、この印には出ない。未確認)
    public static string KindSig(P4kArchive p4k, string cgfPath)
    {
        var g = SocpakExporter.DataCorePathToP4k(cgfPath.Replace('\\', '/'));
        string S(string n) { int i = p4k.Find(n); return i < 0 ? "-" : $"{p4k.Crc(i):x8}.{p4k.UncompressedSize(i)}"; }
        int dot = g.LastIndexOf('.');
        var lod = dot >= 0 ? $"{g[..dot]}_lod1{g[dot..]}" : $"{g}_lod1";
        return $"{S(g)},{S(g + "m")}|{S(lod + "m")},{S(lod)}";
    }

    // 場所の印: その場所の socpak (ObjectContainers の下の相対パスの小文字) に当たる .socpak ファイルの名前・CRC・大きさをまとめたもの
    public static string SrcSig(P4kArchive p4k, IEnumerable<string> socpaks)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var sp in socpaks.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            sb.Append(sp).Append('=');
            foreach (var n in StationView3D.MatchSocpaksCached(p4k, sp.Replace('/', '\\')))
            {
                int i = p4k.Find(n);
                sb.Append(n).Append(':').Append(i < 0 ? "-" : $"{p4k.Crc(i):x8}.{p4k.UncompressedSize(i)}").Append(';');
            }
            sb.Append('\n');
        }
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // 補う面 1 つ (形ごと)。DB の詰めた形 (Pack) と三角形の並びの、要る方だけを、要るときに作る。
    // (2026-11-05) 前は DB を読むときに全部の形を Unpack (外形の三角形分け) していて、3D を開くとき・作り直すときに数分かかっていた。結果は同じ
    public sealed class FillData
    {
        private byte[]? _packed; private float[]? _tris;
        // 歩くモードの裏の補い (part_fill_walk。形式は PackRaw) を同じ形に足したもの (Table)。Tris は part_fill のものに続けて戻す。Packed は足したものがあれば詰め直す
        private List<byte[]>? _more;
        public FillData(float[] tris) { _tris = tris; }
        public FillData(byte[] packed) { _packed = packed; }
        public float[] Tris => _tris ??= UnpackAll();
        public byte[] Packed => _more == null ? _packed ??= Pack(_tris!) : Pack(Tris);
        public long? TriCount => _tris is { } t ? t.Length / 9 : null;   // 三角形に戻していなければ null (数えるためだけに戻さない)
        public void AddPackedRaw(byte[] packedRaw) { if (_packed == null && _tris is { Length: > 0 }) _packed = Pack(_tris); (_more ??= new()).Add(packedRaw); _tris = null; }
        private float[] UnpackAll()
        {
            var first = _packed != null ? Unpack(_packed) : [];
            if (_more == null) return first;
            var parts = new List<float[]> { first };
            // 形式の違う行 (古い形式など。UnpackRaw が InvalidDataException) は、その行だけ飛ばして続ける (2026-11-05、親の決定。1 行のために地点が作れなくならないように)
            foreach (var m in _more) { try { parts.Add(UnpackRaw(m)); } catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException or ArgumentException) { } }
            var all = new float[parts.Sum(p => p.Length)]; int pos = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, all, pos * 4, p.Length * 4); pos += p.Length; }
            return all;
        }
    }

    // ===== 前の DB (照会) =====
    public sealed class PrevData
    {
        public Dictionary<string, string> PlaceSrc = new(StringComparer.Ordinal);                       // 場所 → 場所の印
        public Dictionary<string, List<(string Kind, string Sig)>> PlaceKinds = new(StringComparer.Ordinal);   // 場所 → 使う形と形の印
        public Dictionary<string, string> KindSig = new(StringComparer.Ordinal);                         // 形 → 形の印
        public Dictionary<string, FillData> Fill = new(StringComparer.Ordinal);                          // 形 → 補う面 (DB の詰めた形のまま)
    }

    public static PrevData ReadPrev(string dbPath)
    {
        var d = new PrevData();
        try
        {
            if (!File.Exists(dbPath)) return d;
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            c.Open();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('place_fill_src','place_fill','part_fill','kind_sig')";
                using var r = q.ExecuteReader(); int n = 0; while (r.Read()) n++;
                if (n < 4) return d;
            }
            using (var q = c.CreateCommand()) { q.CommandText = "SELECT record, sig FROM place_fill_src"; using var r = q.ExecuteReader(); while (r.Read()) d.PlaceSrc[r.GetString(0)] = r.GetString(1); }
            var kinds = new Dictionary<long, (string Kind, string Sig)>();
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT id, kind, sig FROM kind_sig";
                using var r = q.ExecuteReader();
                while (r.Read()) { kinds[r.GetInt64(0)] = (r.GetString(1), r.GetString(2)); d.KindSig[r.GetString(1)] = r.GetString(2); }
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT record, ids FROM place_fill";
                using var r = q.ExecuteReader();
                while (r.Read())
                {
                    var bt = (byte[])r[1]; var l = new List<(string, string)>(bt.Length / 4);
                    for (int i = 0; i + 4 <= bt.Length; i += 4) l.Add(kinds[BitConverter.ToInt32(bt, i)]);
                    d.PlaceKinds[r.GetString(0)] = l;
                }
            }
            using (var q = c.CreateCommand())
            {
                q.CommandText = "SELECT kind, tris FROM part_fill";
                using var r = q.ExecuteReader();
                while (r.Read()) d.Fill[r.GetString(0)] = new FillData((byte[])r[1]);
            }
        }
        catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException or InvalidCastException) { return new PrevData(); }
        return d;
    }

    // ===== 一括 (BuildLayoutDb から。裏作業) =====
    public sealed class BuildStats
    {
        public int Socpaks, Kinds, KindsCompared, KindsWithFill, KindsExcluded, KindsReused, PlacesTotal, PlacesReused, PlacesChanged; public long Triangles, Bytes; public double Seconds;
    }
    public sealed class BuildResult
    {
        public Dictionary<string, FillData> Fill = new(StringComparer.Ordinal);                 // 形 → 補う面 (補う面のある形だけ)
        public Dictionary<string, string> KindSigs = new(StringComparer.Ordinal);               // 形 → 形の印 (この計算で出てきた形)
        public Dictionary<string, string[]> SocpakKinds = new(StringComparer.Ordinal);          // socpak → 使う形
        public BuildStats Stats = new();
    }

    // socpaks: 計算する socpak (ObjectContainers の下の相対パスの小文字)。部品の種類ごとに 1 回だけ求める。
    //   prev: 前の DB の中身。形の印が前と同じ形は、前の結果をそのまま使い (比べる計算も LOD0・LOD1 の形の読み込みもしない)、変わった形・新しい形だけを比べる。
    //   建物でない形・床に相当しない形は比べない (IsFloorKind)。
    //   log: 1 区画ごとに、始めた行と終えた行 (時刻・済んだ件数/全件数・秒・メモリ・socpak)、60 秒を超えている区画の名前と経過秒を 15 秒ごとに出す (打ち切りはしない)
    public static BuildResult Build(string p4kPath, IReadOnlyCollection<string> socpaks, int parallel, Action<int, int>? progress, CancellationToken ct, PrevData? prev = null, Action<string>? log = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ctx1 = SocpakExporter.Shared(p4kPath, 1);
        var ctx0 = SocpakExporter.Shared(p4kPath, 0);
        var claimed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var fill = new ConcurrentDictionary<string, FillData>(StringComparer.Ordinal);
        var kindSigs = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var socpakKinds = new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);
        var res = new BuildResult(); var st = res.Stats; st.Socpaks = socpaks.Count;
        int done = 0, compared = 0, excluded = 0, reused = 0;
        long tris = 0;
        var logLock = new object();
        void L(string m) { if (log != null) lock (logLock) log($"{DateTime.Now:HH:mm:ss} {m}"); }
        long Mem() => System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 / 1048576;
        var inflight = new ConcurrentDictionary<string, System.Diagnostics.Stopwatch>(StringComparer.Ordinal);
        using var watchdog = log == null ? null : new Timer(_ => { foreach (var kv in inflight) if (kv.Value.Elapsed.TotalSeconds > 60) L($"SLOW {kv.Value.Elapsed.TotalSeconds:F0}s {kv.Key} mem {Mem()}MB"); }, null, 15000, 15000);
        L($"build start socpaks {socpaks.Count} prevKinds {prev?.KindSig.Count ?? 0}");
        Parallel.ForEach(socpaks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallel), CancellationToken = ct }, sp =>
        {
            var swS = System.Diagnostics.Stopwatch.StartNew(); inflight[sp] = swS;
            L($"start {sp}");
            try
            {
                var list = StationView3D.MatchSocpaksCached(ctx1.P4k, sp.Replace('/', '\\'));
                if (list.Count > 0)
                {
                    bool Want1(string raw)
                    {
                        var p = Norm(raw);
                        if (claimed.ContainsKey(p)) return false;
                        if (!IsFloorKind(p)) { if (claimed.TryAdd(p, 0)) Interlocked.Increment(ref excluded); return false; }
                        var sig = KindSig(ctx1.P4k, p); kindSigs[p] = sig;
                        if (prev != null && prev.KindSig.TryGetValue(p, out var ps) && ps == sig)
                        {
                            if (claimed.TryAdd(p, 0)) { Interlocked.Increment(ref reused); if (prev.Fill.TryGetValue(p, out var pf)) { fill[p] = pf; Interlocked.Add(ref tris, pf.TriCount ?? 0); } }
                            return false;
                        }
                        return true;
                    }
                    var scene1 = SocpakExporter.Export(ctx1, list, ct: ct, wantCgf: Want1);   // 求め済み・前と同じ・対象外の種類の形は読まない
                    var kinds = scene1.CgfPaths.Select(Norm).Distinct(StringComparer.Ordinal).ToArray();
                    foreach (var k in kinds) kindSigs.GetOrAdd(k, x => KindSig(ctx1.P4k, x));
                    socpakKinds[sp] = kinds;
                    var m1 = new Dictionary<string, SocpakScene.Mesh>(StringComparer.Ordinal);
                    foreach (var c in scene1.Containers)
                        foreach (var pl in c.Placements)
                            if (pl.Mesh.CgfPath.Length > 0 && !m1.ContainsKey(Norm(pl.Mesh.CgfPath)) && claimed.TryAdd(Norm(pl.Mesh.CgfPath), 0)) m1[Norm(pl.Mesh.CgfPath)] = pl.Mesh;
                    var need = m1.Where(kv => HasLodVariant(ctx1.P4k, kv.Value.CgfPath, 1)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    if (need.Count > 0)
                    {
                        var names = new HashSet<string>(need.Values.Select(m => m.Name), StringComparer.Ordinal);
                        var scene0 = SocpakExporter.Export(ctx0, list, wantMesh: names.Contains, ct: ct, wantCgf: pth => need.ContainsKey(Norm(pth)));
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var c in scene0.Containers)
                            foreach (var pl in c.Placements)
                            {
                                var key = Norm(pl.Mesh.CgfPath);
                                if (!need.TryGetValue(key, out var mesh1) || !seen.Add(key)) continue;
                                Interlocked.Increment(ref compared);
                                var t0 = TrisOf(pl.Mesh); var t1 = TrisOf(mesh1);
                                var f = FillOf(t0, t1, StationView3D.WalkLabelsOf(pl.Mesh.Name, t0), StationView3D.WalkLabelsOf(mesh1.Name, t1));
                                f = KeepThin(f);
                                if (f != null) f = KeepWide(f);
                                if (f != null) { fill[key] = new FillData(f); Interlocked.Add(ref tris, f.Length / 9); }
                            }
                    }
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ZstdSharp.ZstdException) { L($"ERROR {sp} {e.Message}"); }
            inflight.TryRemove(sp, out _);
            int d = Interlocked.Increment(ref done);
            L($"done {d}/{socpaks.Count} {swS.Elapsed.TotalSeconds:F1}s mem {Mem()}MB {sp}");
            progress?.Invoke(d, socpaks.Count);
        });
        st.Kinds = claimed.Count; st.KindsCompared = compared; st.KindsExcluded = excluded; st.KindsReused = reused;
        st.KindsWithFill = fill.Count; st.Triangles = tris; st.Bytes = tris * 36; st.Seconds = sw.Elapsed.TotalSeconds;
        res.Fill = new Dictionary<string, FillData>(fill, StringComparer.Ordinal);
        res.KindSigs = new Dictionary<string, string>(kindSigs, StringComparer.Ordinal);
        res.SocpakKinds = new Dictionary<string, string[]>(socpakKinds, StringComparer.Ordinal);
        L($"build end compared {compared} reused {reused} excluded {excluded} kinds {claimed.Count} withFill {fill.Count} tris {tris} {sw.Elapsed.TotalSeconds:F1}s");
        return res;
    }

    // ===== DB =====
    //   part_fill(kind, tris)                  … 補う面 (形ごと。場所をまたいで共通)。tris は補う面を領域の外形の頂点にまとめて Brotli で圧縮したもの (Pack・Unpack)
    //   place_fill_src(record, sig)            … 場所の印 (その場所の socpak ファイルの CRC・大きさ)
    //   kind_sig(id, kind, sig)                … 形と形の印 (LOD0・LOD1 の CRC・大きさ)
    //   place_fill(record, ids)                … 場所ごとの、使う形 (対象でない形も含む) の kind_sig.id の並び (int32 のリトルエンディアン)
    public static void CreateTable(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {TableName}(kind TEXT PRIMARY KEY, tris BLOB NOT NULL) WITHOUT ROWID;"
            + "CREATE TABLE IF NOT EXISTS place_fill_src(record TEXT PRIMARY KEY, sig TEXT NOT NULL) WITHOUT ROWID;"
            + "CREATE TABLE IF NOT EXISTS kind_sig(id INTEGER PRIMARY KEY, kind TEXT NOT NULL UNIQUE, sig TEXT NOT NULL);"
            + "CREATE TABLE IF NOT EXISTS place_fill(record TEXT PRIMARY KEY, ids BLOB NOT NULL) WITHOUT ROWID";
        cmd.ExecuteNonQuery();
    }

    public static void Write(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, Dictionary<string, FillData> fill,
        Dictionary<string, string> placeSrc, Dictionary<string, List<(string Kind, string Sig)>> placeKinds)
    {
        foreach (var t in new[] { TableName, "place_fill_src", "place_fill", "kind_sig" })
        { using var del = c.CreateCommand(); del.Transaction = tx; del.CommandText = $"DELETE FROM {t}"; del.ExecuteNonQuery(); }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT INTO {TableName}(kind, tris) VALUES($k, $t)";
            var pk = cmd.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text); var pt = cmd.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Blob);
            // 新しく求めた形の詰め直しは並べて先に済ませる (前の DB から使い回す形は、詰めた形をそのまま書く)
            var rows = fill.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
            var packed = new byte[rows.Count][];
            Parallel.For(0, rows.Count, i => packed[i] = rows[i].Value.Packed);
            for (int i = 0; i < rows.Count; i++)
            {
                pk.Value = rows[i].Key; pt.Value = packed[i]; cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO place_fill_src(record, sig) VALUES($r, $s)";
            var pr = cmd.Parameters.Add("$r", Microsoft.Data.Sqlite.SqliteType.Text); var ps = cmd.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var kv in placeSrc.OrderBy(x => x.Key, StringComparer.Ordinal)) { pr.Value = kv.Key; ps.Value = kv.Value; cmd.ExecuteNonQuery(); }
        }
        var kindIds = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO kind_sig(id, kind, sig) VALUES($i, $k, $s)";
            var pi = cmd.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Integer); var pk = cmd.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text); var ps = cmd.Parameters.Add("$s", Microsoft.Data.Sqlite.SqliteType.Text);
            foreach (var x in placeKinds.Values.SelectMany(v => v).GroupBy(v => v.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                int id = kindIds.Count + 1; kindIds[x.Key] = id;
                pi.Value = id; pk.Value = x.Key; ps.Value = x.First().Sig; cmd.ExecuteNonQuery();
            }
        }
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx; cmd.CommandText = "INSERT INTO place_fill(record, ids) VALUES($r, $b)";
            var pr = cmd.Parameters.Add("$r", Microsoft.Data.Sqlite.SqliteType.Text); var pb = cmd.Parameters.Add("$b", Microsoft.Data.Sqlite.SqliteType.Blob);
            foreach (var kv in placeKinds.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                var ids = kv.Value.Select(x => kindIds[x.Kind]).OrderBy(x => x).ToArray();
                var bt = new byte[ids.Length * 4]; Buffer.BlockCopy(ids, 0, bt, 0, bt.Length);
                pr.Value = kv.Key; pb.Value = bt; cmd.ExecuteNonQuery();
            }
        }
    }

    // ===== 補う面の保存形式 =====
    // 利用者の指示「埋める部品を各頂点だけにすれば小さくなる。角度が同じもので 1 辺が一致するもの」。
    //   補う三角形のうち、同じ平面 (法線の各成分の差と平面の位置の差が 1e-4 以内。最初の三角形の平面と比べる) で、頂点がまったく同じ 2 点の辺を共有してつながるものを 1 つの領域にまとめ、
    //   領域の外形 (外の輪と穴の輪。一直線上の頂点は省く) の頂点だけを保存する。読むときは外形を三角形に分け直す (耳切り。穴は橋でつなぐ)。覆う xy の範囲は同じ (保存時に面積で確かめる)。
    //   まとめられない領域 (外形が 1 つの輪にならない・向きが上向きでない・頂点が MaxRegionVerts を超える・分け直した面積が合わない) は、三角形のまま保存する。
    //   全体を Brotli で圧縮する (可逆)。
    // MaxRegionVerts: 耳切りは頂点の数の 3 乗まで掛かるので (256 点でも 46 万三角形の形で 8 分以上。読むときも同じ計算)、これを超える領域は三角形のまま持つ (覆う範囲は同じ)
    private const int PackVersion = 1, MaxRegionVerts = 64;
    private const double PlaneTol = 1e-4;

    public static byte[] Pack(float[] tris)
    {
        int n = tris.Length / 9;
        // 頂点の番号 (座標の値のビットが同じもの)
        var vid = new Dictionary<(int, int, int), int>(); var vx = new List<float>(); var vy = new List<float>(); var vz = new List<float>();
        var tv = new int[n * 3];
        for (int t = 0; t < n; t++)
            for (int k = 0; k < 3; k++)
            {
                int b = t * 9 + k * 3;
                var key = (BitConverter.SingleToInt32Bits(tris[b]), BitConverter.SingleToInt32Bits(tris[b + 1]), BitConverter.SingleToInt32Bits(tris[b + 2]));
                if (!vid.TryGetValue(key, out var id)) { id = vx.Count; vid[key] = id; vx.Add(tris[b]); vy.Add(tris[b + 1]); vz.Add(tris[b + 2]); }
                tv[t * 3 + k] = id;
            }
        // 平面
        var pn = new double[n * 3]; var pd = new double[n]; var up = new bool[n];
        for (int t = 0; t < n; t++)
        {
            int b = t * 9;
            double ux = tris[b + 3] - tris[b], uy = tris[b + 4] - tris[b + 1], uz = tris[b + 5] - tris[b + 2], wx = tris[b + 6] - tris[b], wy = tris[b + 7] - tris[b + 1], wz = tris[b + 8] - tris[b + 2];
            double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx, ln = Math.Sqrt(cx * cx + cy * cy + cz * cz);
            if (ln < 1e-18) { up[t] = false; continue; }
            pn[t * 3] = cx / ln; pn[t * 3 + 1] = cy / ln; pn[t * 3 + 2] = cz / ln;
            pd[t] = pn[t * 3] * tris[b] + pn[t * 3 + 1] * tris[b + 1] + pn[t * 3 + 2] * tris[b + 2];
            up[t] = cz > 0;
        }
        // 辺 → 三角形
        var edge = new Dictionary<long, (int A, int B)>();
        static long EK(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        for (int t = 0; t < n; t++)
            for (int k = 0; k < 3; k++)
            {
                long ek = EK(tv[t * 3 + k], tv[t * 3 + (k + 1) % 3]);
                if (!edge.TryGetValue(ek, out var e)) edge[ek] = (t, -1);
                else edge[ek] = (e.A, e.B == -1 ? t : -2);   // -2: 3 枚以上が共有 (つなげない)
            }
        var seen = new bool[n];
        using var ms = new MemoryStream(); using var bw = new BinaryWriter(ms);
        bw.Write(PackVersion);
        var body = new MemoryStream(); var bb = new BinaryWriter(body);
        int regions = 0;
        for (int s = 0; s < n; s++)
        {
            if (seen[s]) continue;
            var reg = new List<int> { s }; seen[s] = true;
            if (up[s])
            {
                for (int qi = 0; qi < reg.Count; qi++)
                {
                    int t = reg[qi];
                    for (int k = 0; k < 3; k++)
                    {
                        var e = edge[EK(tv[t * 3 + k], tv[t * 3 + (k + 1) % 3])];
                        if (e.B < 0) continue;
                        int u = e.A == t ? e.B : e.A;
                        if (seen[u] || !up[u]) continue;
                        if (Math.Abs(pn[u * 3] - pn[s * 3]) > PlaneTol || Math.Abs(pn[u * 3 + 1] - pn[s * 3 + 1]) > PlaneTol || Math.Abs(pn[u * 3 + 2] - pn[s * 3 + 2]) > PlaneTol || Math.Abs(pd[u] - pd[s]) > PlaneTol) continue;
                        seen[u] = true; reg.Add(u);
                    }
                }
            }
            regions++;
            var loops = up[s] ? OutlineOf(reg, tv, vx, vy, vz, edge, tris) : null;
            if (loops != null)
            {
                // 分け直した面積が、元の三角形の xy の面積の合計と合うか
                double want = 0; foreach (var t in reg) want += Math.Abs(XyArea(tris, t * 9));
                var chk = new List<float>();
                if (Triangulate(loops, chk) && Math.Abs(XyAreaSum(chk) - want) <= 1e-5 * want + 1e-9) { bb.Write(1); bb.Write(loops.Count); foreach (var lp in loops) { bb.Write(lp.Length / 3); foreach (var v in lp) bb.Write(v); } continue; }
            }
            bb.Write(0); bb.Write(reg.Count);
            foreach (var t in reg) for (int k = 0; k < 9; k++) bb.Write(tris[t * 9 + k]);
        }
        bw.Write(regions); bb.Flush(); body.Position = 0; body.CopyTo(ms); bw.Flush();
        using var o = new MemoryStream();
        using (var z = new System.IO.Compression.BrotliStream(o, System.IO.Compression.CompressionLevel.Fastest, true)) { var raw = ms.GetBuffer(); z.Write(raw, 0, (int)ms.Length); }
        return o.ToArray();
    }

    private static double XyArea(float[] f, int b) => ((double)f[b + 3] - f[b]) * ((double)f[b + 7] - f[b + 1]) / 2 - ((double)f[b + 6] - f[b]) * ((double)f[b + 4] - f[b + 1]) / 2;
    private static double XyAreaSum(List<float> f) { double a = 0; for (int b = 0; b + 9 <= f.Count; b += 9) a += Math.Abs(((double)f[b + 3] - f[b]) * ((double)f[b + 7] - f[b + 1]) - ((double)f[b + 6] - f[b]) * ((double)f[b + 4] - f[b + 1])) / 2; return a; }

    // 領域の外形 (外の輪が先、穴の輪が後。各輪は x,y,z の並び)。作れなければ null
    private static List<float[]>? OutlineOf(List<int> reg, int[] tv, List<float> vx, List<float> vy, List<float> vz, Dictionary<long, (int A, int B)> edge, float[] tris)
    {
        static long EK(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        var inReg = new HashSet<int>(reg);
        var next = new Dictionary<int, int>();
        foreach (var t in reg)
        {
            if (XyArea(tris, t * 9) <= 0) return null;
            for (int k = 0; k < 3; k++)
            {
                int a = tv[t * 3 + k], b = tv[t * 3 + (k + 1) % 3];
                var e = edge[EK(a, b)];
                bool shared = e.B >= 0 && inReg.Contains(e.A) && inReg.Contains(e.B);
                if (shared) continue;
                if (next.ContainsKey(a)) return null;   // 1 点から 2 本出る (輪にならない)
                next[a] = b;
            }
        }
        if (next.Count == 0) return null;
        var used = new HashSet<int>(); var loops = new List<(List<int> V, double Area)>();
        int total = 0;
        foreach (var st in next.Keys.OrderBy(x => x))
        {
            if (used.Contains(st)) continue;
            var lp = new List<int>(); int cur = st;
            while (true)
            {
                if (!used.Add(cur)) return null;
                lp.Add(cur);
                if (!next.TryGetValue(cur, out var nx)) return null;
                cur = nx;
                if (cur == st) break;
            }
            // 一直線上の頂点を省く
            var cl = new List<int>();
            for (int i = 0; i < lp.Count; i++)
            {
                int a = lp[(i + lp.Count - 1) % lp.Count], b = lp[i], c = lp[(i + 1) % lp.Count];
                double ux = vx[b] - vx[a], uy = vy[b] - vy[a], uz = vz[b] - vz[a], wx = vx[c] - vx[b], wy = vy[c] - vy[b], wz = vz[c] - vz[b];
                double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                double cr = Math.Sqrt(cx * cx + cy * cy + cz * cz), lu = Math.Sqrt(ux * ux + uy * uy + uz * uz), lw = Math.Sqrt(wx * wx + wy * wy + wz * wz);
                bool straight = cr <= 1e-6 * lu * lw && ux * wx + uy * wy + uz * wz > 0;
                if (!straight) cl.Add(b);
            }
            if (cl.Count < 3) return null;
            double ar = 0;
            for (int i = 0; i < cl.Count; i++) { int a = cl[i], b = cl[(i + 1) % cl.Count]; ar += (double)vx[a] * vy[b] - (double)vx[b] * vy[a]; }
            loops.Add((cl, ar / 2)); total += cl.Count;
        }
        if (total > MaxRegionVerts) return null;
        var outer = loops.Where(l => l.Area > 0).ToList();
        if (outer.Count != 1) return null;
        var res = new List<float[]>();
        foreach (var l in new[] { outer[0] }.Concat(loops.Where(l => l.Area <= 0)))
        {
            var arr = new float[l.V.Count * 3];
            for (int i = 0; i < l.V.Count; i++) { arr[i * 3] = vx[l.V[i]]; arr[i * 3 + 1] = vy[l.V[i]]; arr[i * 3 + 2] = vz[l.V[i]]; }
            res.Add(arr);
        }
        return res;
    }

    public static float[] Unpack(byte[] b)
    {
        using var ms = new MemoryStream(b); using var z = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var o = new MemoryStream(); z.CopyTo(o); o.Position = 0;
        using var br = new BinaryReader(o);
        if (br.ReadInt32() != PackVersion) throw new InvalidDataException("part_fill の形式が違います");
        int regions = br.ReadInt32();
        var res = new List<float>();
        for (int r = 0; r < regions; r++)
        {
            int kind = br.ReadInt32();
            if (kind == 0) { int nt = br.ReadInt32(); for (int i = 0; i < nt * 9; i++) res.Add(br.ReadSingle()); }
            else
            {
                int nl = br.ReadInt32(); var loops = new List<float[]>();
                for (int l = 0; l < nl; l++) { int nv = br.ReadInt32(); var arr = new float[nv * 3]; for (int i = 0; i < arr.Length; i++) arr[i] = br.ReadSingle(); loops.Add(arr); }
                if (!Triangulate(loops, res)) throw new InvalidDataException("part_fill の外形を三角形に分けられません");
            }
        }
        return res.ToArray();
    }

    // 外の輪 (loops[0]、xy で反時計回り) と穴の輪 (時計回り) を三角形に分けて out に足す。穴は橋でつないで耳切りする。分けられなければ false
    private static bool Triangulate(List<float[]> loops, List<float> output)
    {
        var ring = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < loops[0].Length; i += 3) ring.Add((loops[0][i], loops[0][i + 1], loops[0][i + 2]));
        var holes = new List<List<(double X, double Y, double Z)>>();
        for (int h = 1; h < loops.Count; h++)
        {
            var l = new List<(double X, double Y, double Z)>();
            for (int i = 0; i < loops[h].Length; i += 3) l.Add((loops[h][i], loops[h][i + 1], loops[h][i + 2]));
            holes.Add(l);
        }
        foreach (var hole in holes.OrderByDescending(h => h.Max(v => v.X)))
        {
            int m = 0; for (int i = 1; i < hole.Count; i++) if (hole[i].X > hole[m].X) m = i;
            var M = hole[m];
            double bestX = double.MaxValue; int ea = -1;
            for (int i = 0; i < ring.Count; i++)
            {
                var A = ring[i]; var B = ring[(i + 1) % ring.Count];
                if (A.Y == B.Y) continue;
                if (!((A.Y <= M.Y && B.Y >= M.Y) || (B.Y <= M.Y && A.Y >= M.Y))) continue;
                double ix = A.X + (M.Y - A.Y) / (B.Y - A.Y) * (B.X - A.X);
                if (ix >= M.X - 1e-12 && ix < bestX) { bestX = ix; ea = i; }
            }
            if (ea < 0) return false;
            int p = ring[ea].X >= ring[(ea + 1) % ring.Count].X ? ea : (ea + 1) % ring.Count;
            var I = (X: bestX, Y: M.Y);
            var P = ring[p];
            // 三角形 M・I・P の中にある頂点のうち、M から見た角度が一番小さいものに替える
            double bestTan = double.MaxValue;
            for (int j = 0; j < ring.Count; j++)
            {
                if (j == p) continue;
                var V = ring[j];
                if (V.X < M.X) continue;
                if (!InTri(M.X, M.Y, I.X, I.Y, P.X, P.Y, V.X, V.Y)) continue;
                double tn = V.X == M.X ? double.MaxValue / 2 : Math.Abs(V.Y - M.Y) / (V.X - M.X);
                if (tn < bestTan) { bestTan = tn; p = j; }
            }
            var ins = new List<(double X, double Y, double Z)>();
            for (int k = 0; k < hole.Count; k++) ins.Add(hole[(m + k) % hole.Count]);
            ins.Add(hole[m]); ins.Add(ring[p]);
            ring.InsertRange(p + 1, ins);
        }
        return EarClip(ring, output);
    }

    private static bool InTri(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        double d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax), d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx), d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static bool EarClip(List<(double X, double Y, double Z)> r, List<float> output)
    {
        int n = r.Count;
        if (n < 3) return false;
        var prev = new int[n]; var next = new int[n];
        for (int i = 0; i < n; i++) { prev[i] = (i + n - 1) % n; next[i] = (i + 1) % n; }
        int remaining = n, cur = 0;
        static double Cross(in (double X, double Y, double Z) a, in (double X, double Y, double Z) b, in (double X, double Y, double Z) c) => (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
        void Emit(int a, int b, int c)
        {
            foreach (var i in new[] { a, b, c }) { output.Add((float)r[i].X); output.Add((float)r[i].Y); output.Add((float)r[i].Z); }
        }
        while (remaining > 3)
        {
            bool found = false; int start = cur;
            do
            {
                int p = prev[cur], nx = next[cur];
                if (Cross(r[p], r[cur], r[nx]) > 1e-12)
                {
                    bool ok = true;
                    for (int q = next[nx]; q != p; q = next[q])
                    {
                        var V = r[q];
                        if ((V.X == r[p].X && V.Y == r[p].Y) || (V.X == r[cur].X && V.Y == r[cur].Y) || (V.X == r[nx].X && V.Y == r[nx].Y)) continue;
                        if (InTri(r[p].X, r[p].Y, r[cur].X, r[cur].Y, r[nx].X, r[nx].Y, V.X, V.Y)) { ok = false; break; }
                    }
                    if (ok) { Emit(p, cur, nx); next[p] = nx; prev[nx] = p; remaining--; cur = nx; found = true; break; }
                }
                cur = next[cur];
            } while (cur != start);
            if (found) continue;
            // 面積 0 の頂点を捨てる
            do
            {
                int p = prev[cur], nx = next[cur];
                if (Math.Abs(Cross(r[p], r[cur], r[nx])) <= 1e-12) { next[p] = nx; prev[nx] = p; remaining--; cur = nx; found = true; break; }
                cur = next[cur];
            } while (cur != start);
            if (!found) return false;
        }
        if (Cross(r[prev[cur]], r[cur], r[next[cur]]) > 1e-12) Emit(prev[cur], cur, next[cur]);
        return true;
    }

    // 場所ごとの照会と計算 (BuildLayoutDb から)。places: 場所 → その場所の socpak。前の DB (prev) の印と今の Data.p4k の印が同じ場所は計算しない。
    // 返す: 書く中身 (補う面・場所の印・場所ごとの形と印) と数
    public static (Dictionary<string, FillData> Fill, Dictionary<string, string> PlaceSrc, Dictionary<string, List<(string Kind, string Sig)>> PlaceKinds, BuildStats Stats) Run(
        string p4kPath, IReadOnlyDictionary<string, List<string>> places, PrevData prev, int parallel, Action<int, int>? progress, CancellationToken ct, Action<string>? log = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p4k = SocpakExporter.Shared(p4kPath, 1).P4k;
        var placeSrc = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = new List<string>(); var reusedPlaces = new List<string>();
        foreach (var kv in places.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var src = SrcSig(p4k, kv.Value); placeSrc[kv.Key] = src;
            bool same = prev.PlaceSrc.TryGetValue(kv.Key, out var ps) && ps == src && (!prev.PlaceKinds.TryGetValue(kv.Key, out var pk) || pk.All(x => KindSig(p4k, x.Kind) == x.Sig));   // 形が 1 つも無い場所は pk 無し
            (same ? reusedPlaces : changed).Add(kv.Key);
        }
        log?.Invoke($"{DateTime.Now:HH:mm:ss} places {places.Count}: reuse {reusedPlaces.Count}, compute {changed.Count}");
        var sps = changed.SelectMany(r => places[r]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var br = Build(p4kPath, sps, parallel, progress, ct, prev, log);
        var fill = new Dictionary<string, FillData>(StringComparer.Ordinal);
        var placeKinds = new Dictionary<string, List<(string Kind, string Sig)>>(StringComparer.Ordinal);
        foreach (var r in reusedPlaces)
        {
            if (!prev.PlaceKinds.TryGetValue(r, out var pks)) continue;
            placeKinds[r] = pks;
            foreach (var (k, _) in pks) if (prev.Fill.TryGetValue(k, out var f)) fill[k] = f;
        }
        foreach (var r in changed)
        {
            var ks = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sp in places[r]) if (br.SocpakKinds.TryGetValue(sp, out var l)) foreach (var k in l) ks[k] = br.KindSigs.TryGetValue(k, out var sg) ? sg : "";
            placeKinds[r] = ks.Select(x => (x.Key, x.Value)).ToList();
            foreach (var k in ks.Keys) if (br.Fill.TryGetValue(k, out var f)) fill[k] = f;
        }
        var st = br.Stats; st.PlacesTotal = places.Count; st.PlacesReused = reusedPlaces.Count; st.PlacesChanged = changed.Count;
        // 三角形の数は、今回求めた形の分 (前の DB から使い回した形は、数えるためだけに三角形に戻さない)
        st.KindsWithFill = fill.Count; st.Triangles = fill.Values.Sum(f => f.TriCount ?? 0); st.Bytes = st.Triangles * 36; st.Seconds = sw.Elapsed.TotalSeconds;
        return (fill, placeSrc, placeKinds, st);
    }

    // ===== 歩くモードの裏の補い (2026-11-05、利用者の指示「歩くモードで近く … 裏で先に細かい形で調べる 見つけたら DB に入れる」) =====
    // 配る DB (view3d_layout.db) はアプリの更新とパッチの作り直しで丸ごと入れ替わるので、別のファイル view3d_fill_walk.db に入れる (親の決定)。
    //   part_fill_walk(kind, sig, tris) … kind = 形 (Norm)、sig = 形の印 (KindSig。パッチで形が変わった行は Table で使わない)、
    //   tris = 部品の局所座標の三角形 (float32 x9 の並び) をそのまま Brotli で圧縮したもの (PackRaw・UnpackRaw。配る part_fill の Pack (外形にまとめる) とは別の形式。
    //   親の決定: Pack は Unpack で三角形の分け方が変わるので、合わせるときに「頂点が同じ三角形は 1 つに」が効かず行が肥大した)
    // 同じ kind の行があれば、三角形を合わせて (頂点が同じ三角形は 1 つに) 書き直す。印が違う行 (パッチで形が変わった後の古い行) は合わせずに置き換える (担当が決めたもの)。
    // 書くときは Mode=ReadWriteCreate;Pooling=False、終わりに ClearAllPools
    public const string WalkTableName = "part_fill_walk";
    private const int RawVersion = 1;
    public static byte[] PackRaw(float[] tris)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        using (var bw = new BinaryWriter(z))
        {
            bw.Write(RawVersion); bw.Write(tris.Length / 9);
            for (int i = 0; i < tris.Length / 9 * 9; i++) bw.Write(tris[i]);
        }
        return ms.ToArray();
    }
    public static float[] UnpackRaw(byte[] b)
    {
        using var ms = new MemoryStream(b); using var z = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionMode.Decompress);
        using var o = new MemoryStream(); z.CopyTo(o); o.Position = 0;
        using var br = new BinaryReader(o);
        if (br.ReadInt32() != RawVersion) throw new InvalidDataException("part_fill_walk の形式が違います");
        int n = br.ReadInt32();
        var res = new float[n * 9];
        for (int i = 0; i < res.Length; i++) res[i] = br.ReadSingle();
        return res;
    }
    public const string WalkDbName = "view3d_fill_walk.db";
    public static string WalkDbPath => Path.Combine(StationPlaces.Folder, WalkDbName);
    private static readonly object WalkWriteLock = new();
    public static void WriteWalk(string dbPath, string kind, string sig, float[] localTris)
    {
        if (localTris.Length < 9) return;
        lock (WalkWriteLock)
        {
            try
            {
                if (Path.GetDirectoryName(dbPath) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate;Pooling=False");
                c.Open();
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = $"CREATE TABLE IF NOT EXISTS {WalkTableName}(kind TEXT PRIMARY KEY, sig TEXT NOT NULL, tris BLOB NOT NULL) WITHOUT ROWID";
                    cmd.ExecuteNonQuery();
                }
                float[] merged = localTris;
                using (var q = c.CreateCommand())
                {
                    q.CommandText = $"SELECT sig, tris FROM {WalkTableName} WHERE kind = $k";
                    q.Parameters.AddWithValue("$k", kind);
                    using var r = q.ExecuteReader();
                    if (r.Read() && r.GetString(0) == sig)
                    {
                        var old = UnpackRaw((byte[])r[1]);
                        // 頂点 (座標の値のビット) が同じ三角形は 1 つに
                        var seen = new HashSet<(int, int, int, int, int, int, int, int, int)>();
                        var outl = new List<float>(old.Length + localTris.Length);
                        foreach (var src in new[] { old, localTris })
                            for (int t = 0; t + 9 <= src.Length; t += 9)
                            {
                                int K(int i) => BitConverter.SingleToInt32Bits(src[t + i]);
                                if (!seen.Add((K(0), K(1), K(2), K(3), K(4), K(5), K(6), K(7), K(8)))) continue;
                                for (int i = 0; i < 9; i++) outl.Add(src[t + i]);
                            }
                        merged = outl.ToArray();
                    }
                }
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = $"INSERT OR REPLACE INTO {WalkTableName}(kind, sig, tris) VALUES($k, $s, $t)";
                    cmd.Parameters.AddWithValue("$k", kind);
                    cmd.Parameters.AddWithValue("$s", sig);
                    cmd.Parameters.AddWithValue("$t", PackRaw(merged));
                    cmd.ExecuteNonQuery();
                }
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        }
    }

    // ===== 読む (描くとき) =====
    // DB の表を、ファイルの更新時刻と大きさが変わるまでメモリに持つ。DB が無い・表が無い・読めなければ空。
    // walkDbPath・p4k があれば、歩くモードの裏の補い (view3d_fill_walk.db の part_fill_walk) のうち、印 (sig) が今の形の印 (KindSig) と同じ行も足す (同じ形は part_fill の後に続ける)
    private static readonly object TableLock = new();
    private static string _tableStamp = "";
    private static Dictionary<string, FillData> _table = new(StringComparer.Ordinal);
    // 形ごとの三角形への戻し (Unpack) は、Augment でその形が出てきたときに 1 回だけ行う (FillData)
    public static Dictionary<string, FillData> Table(string dbPath, string? walkDbPath = null, P4kArchive? p4k = null)
    {
        lock (TableLock)
        {
            try
            {
                var fi = new FileInfo(dbPath);
                var wi = walkDbPath != null && p4k != null ? new FileInfo(walkDbPath) : null;
                if (!fi.Exists && wi is not { Exists: true }) { _tableStamp = ""; return _table = new(StringComparer.Ordinal); }
                var stamp = dbPath + "|" + (fi.Exists ? fi.LastWriteTimeUtc.Ticks + "|" + fi.Length : "-") + "|" + (wi is { Exists: true } ? walkDbPath + "|" + wi.LastWriteTimeUtc.Ticks + "|" + wi.Length : "-");
                if (stamp == _tableStamp) return _table;
                var t = new Dictionary<string, FillData>(StringComparer.Ordinal);
                if (fi.Exists)
                    using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False"))
                    {
                        c.Open();
                        using var cmd = c.CreateCommand();
                        cmd.CommandText = $"SELECT kind, tris FROM {TableName}";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            t[r.GetString(0)] = new FillData((byte[])r[1]);
                        }
                    }
                bool walkFailed = false;
                if (wi is { Exists: true } && p4k != null)
                    try
                    {
                        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={walkDbPath};Mode=ReadOnly;Pooling=False");
                        c.Open();
                        bool has;
                        using (var chk = c.CreateCommand()) { chk.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$n"; chk.Parameters.AddWithValue("$n", WalkTableName); has = chk.ExecuteScalar() != null; }
                        if (has)   // 表がまだ無い (書く前) のは失敗ではない
                        {
                            using var cmd = c.CreateCommand();
                            cmd.CommandText = $"SELECT kind, sig, tris FROM {WalkTableName}";
                            using var r = cmd.ExecuteReader();
                            while (r.Read())
                            {
                                var kind = r.GetString(0);
                                if (r.GetString(1) != KindSig(p4k, kind)) continue;   // パッチで形が変わった行は使わない
                                var packed = (byte[])r[2];
                                if (t.TryGetValue(kind, out var f)) f.AddPackedRaw(packed); else { var nf = new FillData(Array.Empty<float>()); nf.AddPackedRaw(packed); t[kind] = nf; }
                            }
                        }
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException) { walkFailed = true; }   // 書き込みと重なった (locked) など。印を記録せず、次に読み直す
                _tableStamp = walkFailed ? "" : stamp;
                return _table = t;
            }
            catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            {
                _tableStamp = "";
                return _table = new(StringComparer.Ordinal);
            }
        }
    }

    // 部品の並びに、補う面の追加の部品を足す (元の部品の直後。同じ名前・同じ置き方。大きさの判定は元の部品の形で行う)
    public static List<StationView3D.SourcePart> Augment(List<StationView3D.SourcePart> parts, Dictionary<string, FillData> table)
    {
        if (table.Count == 0) return parts;
        var res = new List<StationView3D.SourcePart>(parts.Count + 16);
        foreach (var p in parts)
        {
            res.Add(p);
            if (p.CgfPath.Length > 0 && table.TryGetValue(Norm(p.CgfPath), out var f)) res.Add(new StationView3D.SourcePart(p.Name, f.Tris, p.M, "", p.Mesh));
        }
        return res;
    }
}
