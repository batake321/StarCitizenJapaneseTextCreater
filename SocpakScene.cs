using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// socpak (区画) の形の取り出し。StarBreaker v0.2.2 の `socpak export --materials none --no-lights [--lod N]` (socpaks_to_glb) の移植。
// GLB のファイルは作らず、GLB に書かれるのと同じもの (区画ごとの子・部品の置き方の行列・部品の形) をメモリで返す (SocpakScene)。
//   流れ: socpak (p4k の中の ZIP) → 中の .soc (CrCh) を全部 → IncludedObjects (0x0010) の置き方と、CryXmlB (0x0004) の Entity の置き方
//         (形のパスが無い Entity は EntityClassGUID を DataCore で引く) → 同じ CGF のパスは 1 回だけ読む (区画をまたいでも)
//         → .cgfm (_lod<N> があればそれ) の IvoSkin2 → 2 つの箱が 0.01 より違えば .cgf の NMC を焼き込む → .mtl の NoDraw・デカールの submesh を外す
//   socpak の中にある形 (MergedMeshes/CompoundRenderNode_*.cgf 等。p4k の目次には無い) は、p4k の目次に無いときだけ socpak の中から読む
//   (InnerFiles。CrCh の形は CryCrChMesh で読む)。StarBreaker v0.2.2 は読まずに「file not found in P4k」で抜かしていた (結果が starbreaker と変わる直し。
//   2026-11-04、Orison の建物の本体が描かれない不具合を直すため)。
//   GLB と同じにするため: 行列の数は f32 を最短の 10 進で書いて double で読んだ値 (JsonFloat)、箱 (accessor の min・max) は f32 をそのまま double にした値。
// 同じ部品の形は 1 回だけ読み、SocpakMeshCache (容量の上限つき) で区画・地点をまたいで使い回す。
internal sealed class SocpakScene
{
    // 部品の形 1 つ (GLB の mesh 1 つ)。Positions は x,y,z の並び (f32)、Primitives は面を出す submesh ごとの索引 (3 つずつ三角形)。
    // Uvs: u,v の並び (Positions と同じ頂点の順。形に UV が無ければ null)、SubMaterials: Primitives ごとの材質の番号 (submesh の MaterialId)、
    // Mtl: 材質のファイル (.mtl) の Data.p4k の中のパス (引けなければ "")。ゲームの画像を貼るため (2026-11-05、利用者の指示「引いたときは色はビットマップ貼り付けにしてほしい」)
    public sealed class Mesh
    {
        public string Name = ""; public string CgfPath = "";   // CgfPath: 部品の種類 (datacore の形のパス。補う面の表の鍵。2026-11-04)
        public float[] Positions = [];
        public float[]? Uvs;
        public List<uint[]> Primitives = new();
        public List<int> SubMaterials = new();
        public string Mtl = "";
        public double[] Min = new double[3], Max = new double[3];   // accessor の min・max (f32 を double に)
        public long Bytes => Positions.LongLength * 4 + (Uvs?.LongLength ?? 0) * 4 + Primitives.Sum(p => (long)p.Length * 4) + 64;
    }

    // 置き方 1 つ (GLB の子のノード)。Matrix は列優先 16 (GLB に書いた f32 を読んだ double)
    public readonly record struct Placement(Mesh Mesh, double[] Matrix);

    // 区画 1 つ (GLB のルートの下の子「interior_<名前>」)。Matrix は列優先 16 (socpak export では単位行列)
    public sealed class Container
    {
        public string Name = "";          // 「interior_」付き
        public string SocpakPath = "";    // p4k の中のパス (渡されたもの)
        public double[] Matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
        public List<Placement> Placements = new();
        // 重力の範囲 (GravityBox・GravityArea。形にはしない。2026-11-04、ステーションの 3D の担当が使う)
        public List<GravityVolume> Gravity = new();
    }

    // 重力の範囲 1 つ。座標は .soc の座標 (区画の中。Placements の行列と同じ座標)。
    //   Pos: Entity の Pos。Rot: Entity の Rotate (w, x, y, z。省けば 1, 0, 0, 0)。
    //   箱 (GravityBox): Size は GravityBoxParams の size をそのまま (箱が Pos を中心に Size の大きさか、角が Pos かは未確認。範囲の向きは Rot)。
    //   柱 (GravityArea): Polygon は GameVolumeComponent の Point の Pos (Entity の座標の中)、Height は volumeHeight (+z へ。Rot で回す)。
    //   重力: Magnitude (gravityMagnitude)・Direction (gravityDirection。Entity の座標の中)・DirectionWorld (Direction を Rot で回したもの)。
    //   省かれた欄は DataCore の EntityClassGUID の record (無ければ EntityClassDefinition.GravityBox・GravityArea) の
    //   GravityBoxParams・GravityAreaParams の値。DataCore で引けない欄は null
    public sealed record GravityVolume(
        string Class, string Name, double[] Pos, double[] Rot,
        double[]? Size, List<double[]>? Polygon, double? Height,
        bool? Active, bool? Uniform, bool? Filled, double? FallOffInner,
        double? Magnitude, double[]? Direction, double[]? DirectionWorld);

    public List<Container> Containers { get; } = new();
    // 読めなかった部品 (starbreaker の「failed to load CGF <パス>: <理由>」と同じ順・同じ中身)
    public List<(string CgfPath, string Reason)> FailedMeshes { get; } = new();
    public List<string> CgfPaths { get; } = new();   // 置き方に出てくる部品の形のパス (読んだかどうかにかかわらず全部。補う面の表の「その場所で使う形」の一覧用。2026-11-04)
    // 読めなかった socpak (「failed to load <socpak>: <理由>」)
    public List<(string Socpak, string Reason)> FailedSocpaks { get; } = new();

    // f32 を GLB (serde_json) の数の書き方 (最短の 10 進) で書いて double で読んだ値。NaN・無限は JSON では null (アプリの読み取りでは NaN)
    public static double JsonFloat(float f) => float.IsFinite(f) ? double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : double.NaN;
}

// 部品の形の置き場 (同じ Data.p4k・同じ LOD の間、CGF のパス (+ 材質のパス) ごとに 1 回だけ読む)。上限を超えたら長く使っていないものから捨てる
internal sealed class SocpakMeshCache
{
    public long CapBytes { get; }
    private readonly object _lock = new();
    private readonly Dictionary<string, (SocpakScene.Mesh? Mesh, string? Error, long Bytes, LinkedListNode<string> Node)> _map = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _lru = new();
    private long _bytes;
    public long Bytes { get { lock (_lock) return _bytes; } }
    public int Hits, Loads, Evicted;
    public SocpakMeshCache(long capBytes) { CapBytes = capBytes; }

    public bool TryGet(string key, out SocpakScene.Mesh? mesh, out string? error)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var e)) { _lru.Remove(e.Node); _lru.AddFirst(e.Node); mesh = e.Mesh; error = e.Error; Hits++; return true; }
        }
        mesh = null; error = null; return false;
    }

    public void Put(string key, SocpakScene.Mesh? mesh, string? error)
    {
        long b = (mesh?.Bytes ?? 0) + key.Length * 2 + 64;
        lock (_lock)
        {
            Loads++;
            if (_map.ContainsKey(key) || b > CapBytes) return;
            while (_bytes + b > CapBytes && _lru.Last is { } last)
            {
                _bytes -= _map[last.Value].Bytes; _map.Remove(last.Value); _lru.RemoveLast(); Evicted++;
            }
            _map[key] = (mesh, error, b, _lru.AddFirst(key));
            _bytes += b;
        }
    }

    public void Clear() { lock (_lock) { _map.Clear(); _lru.Clear(); _bytes = 0; } }
}

internal static class SocpakExporter
{
    // アプリで使い回す道具 (Data.p4k・LOD ごと)。Data.p4k が変わったら作り直す。部品の形の置き場は LOD ごとに MeshCacheBytes まで
    public const long MeshCacheBytes = 512L << 20;   // 512 MB (値は担当が決めたもの)
    private static readonly object SharedLock = new();
    private static readonly Dictionary<int, Context> SharedContexts = new();
    public static Context Shared(string p4kPath, int lod = 1)
    {
        var a = P4kArchive.Shared(p4kPath);
        lock (SharedLock)
        {
            if (SharedContexts.TryGetValue(lod, out var c) && ReferenceEquals(c.P4k, a)) return c;
            foreach (var old in SharedContexts.Values) old.ReleaseDataCore();
            if (SharedContexts.Values.Any(x => !ReferenceEquals(x.P4k, a))) SharedContexts.Clear();
            return SharedContexts[lod] = new Context(a, lod, MeshCacheBytes);
        }
    }

    // (2026-11-04、利用者の指示「３D 表示後のメモリ後始末」) ステーションの 3D がどこにも開いていなくなったら、部品の形の置き場ごと捨てる。
    // 取り出しの途中のものは自分の Context を持ったまま終わり、次に使うときに作り直す
    public static void ReleaseShared()
    {
        lock (SharedLock) SharedContexts.Clear();
    }

    // 部品の形の読み取りの道具 (Data.p4k の目次・DataCore・材質の置き場)。同じ Data.p4k の間は使い回す
    public sealed class Context
    {
        public P4kArchive P4k { get; }
        public int Lod { get; }
        public SocpakMeshCache Meshes { get; }
        // 読み込み中の部品の形 (同じ形を同時に頼まれたら、1 回だけ読んで分ける。2026-11-04、利用者の指示「マルチタスク対応にして」)
        internal readonly ConcurrentDictionary<string, Lazy<(SocpakScene.Mesh? Mesh, string? Error)>> InFlight = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, (string Geom, string Mtl)?> _guidGeom = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<int, DataCoreDb.CompiledPath?> _geomPath = new(), _mtlPath = new();
        public double DataCoreSeconds { get; private set; }

        public Context(P4kArchive p4k, int lod = 1, long meshCacheBytes = 1L << 30)
        {
            P4k = p4k; Lod = lod; Meshes = new SocpakMeshCache(meshCacheBytes);
        }

        // DataCore は GUID だけの Entity があったときに初めて読む (Game2.dcb の展開 約 0.3 秒・340 MB)。
        // (2026-11-04、利用者の指示「マルチタスク対応にして」) アプリで 1 つの DataCoreShared を使う (船の読み取り・ハンガーの表と同じもの。
        // 前は区画の道具 (LOD ごと) が自分で読んでいた)。使わないまま DataCoreShared.IdleSeconds 経ったら DataCoreShared が捨てる
        private DataCoreDb Db()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var db = DataCoreShared.Get(null, P4k.Path);
            lock (_dbSecondsLock) DataCoreSeconds += sw.Elapsed.TotalSeconds;
            return db;
        }
        private readonly object _dbSecondsLock = new();

        // 重力の範囲の既定値 (record の GravityBoxParams・GravityAreaParams)。引けなければ null の欄
        private readonly ConcurrentDictionary<string, DcValue.Obj?> _gravityDefaults = new(StringComparer.Ordinal);
        private DcValue.Obj? GravityDefaults(string? guid, string cls) => _gravityDefaults.GetOrAdd((guid ?? "") + "|" + cls, _ =>
        {
            var db = Db();
            DataCoreDb.Record? rec = guid != null && DataCoreDb.Guid128.Parse(guid) is { } id ? db.RecordById(id) : null;
            if (rec == null)
                foreach (var r in db.Records)
                    if (db.String2(r.NameOffset) == "EntityClassDefinition." + cls) { rec = r; break; }
            if (rec is not DataCoreDb.Record rr) return null;
            try
            {
                var cp = db.CompilePath(rr.StructIndex, $"Components[{cls}Params]", DataCoreDb.AnyLeaf);
                return db.QueryValues(cp, rr, true).OfType<DcValue.Obj>().FirstOrDefault();
            }
            catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException) { return null; }
        });

        internal SocpakScene.GravityVolume ResolveGravity(RawGravity g)
        {
            var def = GravityDefaults(g.Guid, g.Class);
            static bool? B(string? s) => s == null ? null : s.Trim() is "1" or "true" or "True" ? true : s.Trim() is "0" or "false" or "False" ? false : null;
            static double? D(string? s) => s != null && RustF64.TryParse(s.Trim(), out var v) ? v : null;
            bool? Bool(string k) => g.Over.TryGetValue(k, out var v) ? B(v) : def != null && DcValue.Field(def, k) is DcValue.Bool b ? b.V : null;
            double? Num(string k) => g.Over.TryGetValue(k, out var v) ? D(v) : def != null && DcValue.Field(def, k) is DcValue.Float f ? SocpakScene.JsonFloat(f.V) : null;
            double[]? Vec(string k, Dictionary<string, string> over)
            {
                var dv = def != null ? DcValue.Field(def, k) : null;
                var r = new double[3];
                string[] axes = ["x", "y", "z"];
                for (int i = 0; i < 3; i++)
                {
                    if (over.TryGetValue(axes[i], out var s)) { if (D(s) is double d) { r[i] = d; continue; } }
                    if (dv != null && DcValue.F32Field(dv, axes[i]) is float f) r[i] = SocpakScene.JsonFloat(f);
                    else if (over.Count == 0) return null;
                }
                return r;
            }
            var dir = Vec("gravityDirection", g.Dir);
            double[]? world = null;
            if (dir != null)
            {
                // 回転 (w, x, y, z) で dir を回す: v' = v + 2w(q×v) + 2 q×(q×v)
                double w = g.Rot[0], qx = g.Rot[1], qy = g.Rot[2], qz = g.Rot[3];
                double tx = 2 * (qy * dir[2] - qz * dir[1]), ty = 2 * (qz * dir[0] - qx * dir[2]), tz = 2 * (qx * dir[1] - qy * dir[0]);
                world = [dir[0] + w * tx + (qy * tz - qz * ty), dir[1] + w * ty + (qz * tx - qx * tz), dir[2] + w * tz + (qx * ty - qy * tx)];
            }
            return new SocpakScene.GravityVolume(g.Class, g.Name, g.Pos, g.Rot,
                g.Class == "GravityBox" ? Vec("size", g.SizeOver) : null, g.Polygon, g.Height,
                Bool("active"), Bool("uniform"), g.Class == "GravityBox" ? Bool("filled") : null, Num("fallOffInner"),
                Num("gravityMagnitude"), dir, world);
        }

        // DataCore を捨てる (メモリを返す。GUID の引き当ての結果は残す)
        public void ReleaseDataCore() { }   // (DataCoreShared が持つので、ここでは何もしない)

        // resolve_guid_geometry: EntityClassGUID → (形のパス, 材質のパス)。引けなければ null
        public (string Geom, string Mtl)? ResolveGuid(string guid) => _guidGeom.GetOrAdd(guid, g =>
        {
            if (DataCoreDb.Guid128.Parse(g) is not { } id) return null;
            var db = Db();
            if (db.RecordById(id) is not DataCoreDb.Record rec) return null;
            string? Q(ConcurrentDictionary<int, DataCoreDb.CompiledPath?> cache, string path)
            {
                var cp = cache.GetOrAdd(rec.StructIndex, si =>
                {
                    try { return db.CompilePath(si, path, DataCoreDb.StringLeaf); }
                    catch (DataCoreDb.QueryException) { return null; }
                });
                if (cp == null) return null;
                try { return db.QueryFirstString(cp, rec); }
                catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException) { return null; }
            }
            var geom = Q(_geomPath, "Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path");
            if (string.IsNullOrEmpty(geom)) return null;
            var mtl = Q(_mtlPath, "Components[SGeometryResourceParams].Geometry.Geometry.Material.path") ?? "";
            return (geom, mtl);
        });
    }

    // socpak の中の置き方 (InteriorMesh)。Transform は列優先 16 の f32
    private sealed record InteriorMesh(string CgfPath, string? MaterialPath, float[] Transform, string? Guid);

    // ===== 書き出し (socpaks_to_glb) =====
    // socpakPaths: p4k の中のパス (starbreaker に渡すのと同じ並び)。wantContainer: 部品の形を読む区画 (null なら全部)。
    //   読まない区画も置き方は読む (同じ CGF の材質は、並びで最初に出てきた区画のものになるため。starbreaker と同じ結果にする)。
    // wantMesh: 形を読む部品の名前 (null なら全部)。読まない部品は置き方にも入れない (呼ぶ側が名前で捨てるもの)
    public static SocpakScene Export(Context ctx, IReadOnlyList<string> socpakPaths, Func<string, bool>? wantContainer = null, Func<string, bool>? wantMesh = null, CancellationToken ct = default, Func<string, bool>? wantCgf = null)
    {
        var scene = new SocpakScene();
        // 1) 区画ごとの置き方 (load_interior_from_socpak)
        var payloads = new List<(string Name, string Path, List<InteriorMesh> Meshes)>();
        var gravity = new List<List<RawGravity>>();
        var inner = new InnerFiles();
        foreach (var sp in socpakPaths)
        {
            ct.ThrowIfCancellationRequested();
            var g = new List<RawGravity>();
            try { payloads.Add(LoadSocpak(ctx.P4k, sp, null, inner, g)); gravity.Add(g); }
            catch (Exception e) when (e is InvalidDataException or IOException or SocpakException or ZstdSharp.ZstdException) { scene.FailedSocpaks.Add((sp, e.Message)); }
        }
        // 2) 同じ CGF を 1 つにまとめる (build_interiors_from_payloads)。名前・材質は最初に出てきたもの
        var cgfIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var cgfs = new List<(string Path, string? Mtl, string Name)>();
        var containers = new List<(string Name, string Path, List<(int Cgf, float[] T)> Placements, List<RawGravity> Gravity)>();
        for (int pi = 0; pi < payloads.Count; pi++)
        {
            var (name, path, meshes) = payloads[pi];
            var placements = new List<(int, float[])>();
            foreach (var im in meshes)
            {
                string cgf; string? mtl;
                if (im.CgfPath.Length > 0) { cgf = im.CgfPath; mtl = im.MaterialPath; }
                else if (im.Guid != null)
                {
                    if (ctx.ResolveGuid(im.Guid) is not { } r) continue;
                    cgf = r.Geom; mtl = r.Mtl.Length > 0 ? r.Mtl : null;
                }
                else continue;
                if (!cgfIndex.TryGetValue(cgf, out var ix))
                {
                    ix = cgfs.Count; cgfIndex[cgf] = ix;
                    var last = cgf[(cgf.LastIndexOf('/') + 1)..];
                    var nm = last.EndsWith(".cgf", StringComparison.Ordinal) ? last[..^4] : cgf;
                    cgfs.Add((cgf, mtl ?? im.MaterialPath, nm));
                }
                placements.Add((ix, im.Transform));
            }
            containers.Add((name, path, placements, gravity[pi]));
        }
        foreach (var cg in cgfs) scene.CgfPaths.Add(cg.Path);
        // 3) 部品の形 (使う区画の、使う名前のものだけ)。読めなかったものは並びの順に FailedMeshes へ。
        //   (2026-11-04、利用者の指示「マルチタスク対応にして」) 形は並べて読む (読む順だけ先に決め、読めなかったものはその順で入れるので結果は 1 つずつ読んだときと同じ)
        var meshObjs = new SocpakScene.Mesh?[cgfs.Count];
        var tried = new bool[cgfs.Count];
        var order = new List<int>();
        foreach (var (name, _, placements, _) in containers)
        {
            if (wantContainer != null && !wantContainer("interior_" + name)) continue;
            foreach (var (ix, _) in placements)
                if ((wantMesh == null || wantMesh(cgfs[ix].Name)) && (wantCgf == null || wantCgf(cgfs[ix].Path)) && !tried[ix]) { tried[ix] = true; order.Add(ix); }
        }
        var errs = new string?[cgfs.Count];
        Parallel.ForEach(order, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, ix =>
        {
            meshObjs[ix] = LoadMesh(ctx, cgfs[ix].Path, cgfs[ix].Mtl, cgfs[ix].Name, out var err, inner);
            errs[ix] = err;
        });
        foreach (var ix in order) if (errs[ix] is string e1) scene.FailedMeshes.Add((cgfs[ix].Path, e1));
        foreach (var (name, path, placements, grav) in containers)
        {
            if (wantContainer != null && !wantContainer("interior_" + name)) continue;
            var c = new SocpakScene.Container { Name = "interior_" + name, SocpakPath = path };
            foreach (var rg in grav) c.Gravity.Add(ctx.ResolveGravity(rg));
            foreach (var (ix, t) in placements)
            {
                if (wantMesh != null && !wantMesh(cgfs[ix].Name)) continue;
                if (meshObjs[ix] is not { } m) continue;
                var mat = new double[16];
                for (int k = 0; k < 16; k++) mat[k] = SocpakScene.JsonFloat(t[k]);
                c.Placements.Add(new SocpakScene.Placement(m, mat));
            }
            scene.Containers.Add(c);
        }
        return scene;
    }

    public sealed class SocpakException(string message) : Exception(message);

    // socpak export の PATTERN: p4k のパスの小文字に、パターンの小文字が含まれ、名前が「.socpak」で終わるもの (目次の順)
    public static List<string> MatchSocpaks(P4kArchive p4k, string pattern)
    {
        var low = pattern.ToLowerInvariant();
        var res = new List<string>();
        for (int i = 0; i < p4k.Count; i++)
        {
            if (!p4k.FileName(i).EndsWith(".socpak", StringComparison.Ordinal)) continue;
            var n = p4k.Name(i);
            if (n.ToLowerInvariant().Contains(low, StringComparison.Ordinal)) res.Add(n);
        }
        return res;
    }

    // load_interior_from_socpak (置き方だけ。灯りは --no-lights なので読まない)
    // GLB の書き出し (SocpakGlb) 用: 区画の名前・置き方 (CGF のパス, 材質, 列優先 16, GUID)・パレットの名前 (最初に出てきた IncludedObjects のもの)
    internal static (string Name, List<(string Cgf, string? Mtl, float[] T, string? Guid)> Meshes, List<string> Palettes) LoadSocpakForGlb(P4kArchive p4k, string socpakPath)
    {
        var pals = new List<string>();
        var (name, _, meshes) = LoadSocpak(p4k, socpakPath, pals);
        return (name, meshes.Select(m => (m.CgfPath, m.MaterialPath, m.Transform, m.Guid)).ToList(), pals);
    }

    // socpak の中の形のファイル。置き方のパス (p4k の形) が、その socpak のフォルダの下 (socpak の中の名前) を指すときに引く
    internal sealed class InnerFiles
    {
        private readonly List<(string Dir, P4kArchive Arch)> _list = new();
        public void Add(string socpakP4kPath, P4kArchive arch)
        {
            int i = socpakP4kPath.LastIndexOf('\\');
            _list.Add((i >= 0 ? socpakP4kPath[..(i + 1)] : "", arch));
        }
        // p4k の形のパス (Data\...) → (socpak の中, socpak の中の名前)。無ければ null
        public (P4kArchive Arch, string Rel)? Locate(string p4kPath)
        {
            var norm = CollapseSeparators(p4kPath);
            foreach (var (dir, arch) in _list)
                if (dir.Length > 0 && norm.Length > dir.Length && norm.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    return (arch, norm[dir.Length..]);
            return null;
        }
    }

    // 「\」が続く所を 1 つにする (置き方のパスは「orison_comm_000//MergedMeshes/…」のように区切りが 2 つ続く)
    private static string CollapseSeparators(string p)
    {
        while (p.Contains(@"\\", StringComparison.Ordinal)) p = p.Replace(@"\\", @"\");
        return p;
    }

    // innerFiles が null でなければ、置き方が自分の中の形を指す socpak の中身を innerFiles に入れる (形の読み取りで使う)
    private static (string Name, string Path, List<InteriorMesh> Meshes) LoadSocpak(P4kArchive p4k, string socpakPath, List<string>? palettes = null, InnerFiles? innerFiles = null, List<RawGravity>? gravity = null)
    {
        var norm = socpakPath.Replace('/', '\\');
        var p4kPath = norm.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase) ? norm : @"Data\" + norm;
        int e = p4k.Find(p4kPath);
        if (e < 0) throw new SocpakException($"socpak not found: {p4kPath}");
        var data = p4k.Read(e);
        var inner = P4kArchive.FromBytes(data);
        var last = socpakPath[(socpakPath.LastIndexOfAny(['/', '\\']) + 1)..];
        var name = last.EndsWith(".socpak", StringComparison.Ordinal) ? last[..^7] : socpakPath;
        var socs = new List<int>();
        for (int i = 0; i < inner.Count; i++) if (inner.Name(i).ToLowerInvariant().EndsWith(".soc", StringComparison.Ordinal)) socs.Add(i);
        if (socs.Count == 0) throw new SocpakException($"No .soc in {p4kPath}");
        var meshes = new List<InteriorMesh>();
        foreach (var si in socs)
        {
            byte[] soc;
            try { soc = inner.Read(si); }
            catch (InvalidDataException) { continue; }
            try
            {
                var pn = new List<string>();
                meshes.AddRange(ParseSoc(soc, pn, gravity));
                if (palettes != null && palettes.Count == 0) palettes.AddRange(pn);
            }
            catch (InvalidDataException) { }
        }
        if (innerFiles != null)
        {
            // 置き方のパスが、この socpak のフォルダの下の「<名前>\」で始まるものがあれば、中身を残す
            int d = p4kPath.LastIndexOf('\\');
            var own = (d >= 0 ? p4kPath[..(d + 1)] : "") + name + @"\";
            foreach (var m in meshes)
            {
                if (m.CgfPath.Length == 0) continue;
                if (CollapseSeparators(DataCorePathToP4k(m.CgfPath)).StartsWith(own, StringComparison.OrdinalIgnoreCase)) { innerFiles.Add(p4kPath, inner); break; }
            }
        }
        return (name, socpakPath, meshes);
    }

    // parse_soc: CrCh のチャンクを順に。IncludedObjects・CryXmlB が読めなければそのチャンクだけ飛ばす
    private static List<InteriorMesh> ParseSoc(byte[] data, List<string>? paletteNames = null, List<RawGravity>? gravity = null)
    {
        var cf = CryChunkFile.Parse(data);
        if (cf.IsIvo) throw new InvalidDataException("expected CrCh, got IVO");
        var res = new List<InteriorMesh>();
        foreach (var c in cf.Chunks)
        {
            var d = cf.ChunkData(c);
            if (c.Type == CryChunkFile.CrChIncludedObjects)
            {
                List<InteriorMesh>? io = null;
                var pal = new List<string>();
                try { io = IncludedObjects(d.ToArray(), pal); } catch (InvalidDataException) { }
                if (io != null)
                {
                    res.AddRange(io);
                    if (paletteNames != null && paletteNames.Count == 0) paletteNames.AddRange(pal);
                }
            }
            else if (c.Type == CryChunkFile.CrChCryXmlB)
            {
                CryXmlB? x = null;
                try { x = CryXmlB.Parse(d.ToArray()); } catch (InvalidDataException) { }
                if (x != null) res.AddRange(Entities(x, gravity));
            }
        }
        return res;
    }

    // included_objects.rs IncludedObjects::from_bytes と included_objects_to_meshes
    private static List<InteriorMesh> IncludedObjects(byte[] data, List<string>? paletteNames = null)
    {
        int off = 4;
        uint U32() { if (off + 4 > data.Length) throw new InvalidDataException("IncludedObjects が切れています"); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off)); off += 4; return v; }
        ushort U16() { if (off + 2 > data.Length) throw new InvalidDataException("IncludedObjects が切れています"); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off)); off += 2; return v; }
        string Str(byte[] d)
        {
            if (off + 256 > d.Length) throw new InvalidDataException("IncludedObjects が切れています");
            var b = d.AsSpan(off, 256);
            int z = b.IndexOf((byte)0);
            off += 256;
            return Encoding.UTF8.GetString(z < 0 ? b : b[..z]);
        }
        int numCgf = (int)U32();
        var cgfPaths = new List<string>();
        for (int i = 0; i < numCgf; i++) cgfPaths.Add(Str(data));
        int numMat = U16(), numPal = U16();
        var matPaths = new List<string>();
        for (int i = 0; i < numMat; i++) matPaths.Add(Str(data));
        for (int i = 0; i < numPal; i++) { var ps = Str(data); paletteNames?.Add(ps); }
        if (off + 28 > data.Length) throw new InvalidDataException("IncludedObjects が切れています");
        off += 28;
        long objEnd = off + (long)U32();
        var res = new List<InteriorMesh>();
        var mtl = matPaths.Count > 0 ? matPaths[0] : null;
        double F64(int at) { if (at + 8 > data.Length) throw new InvalidDataException("IncludedObjects が切れています"); return BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at)); }
        while (off + 4 <= objEnd)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            if (type == 1)
            {
                if (off + 168 > data.Length) break;
                if (off + 64 > data.Length) throw new InvalidDataException("IncludedObjects が切れています");
                int id = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 60));
                // 3×4 の行優先の f64 → 列優先の 4×4 の f32
                var t = new float[16];
                int mo = off + 64;
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 4; col++)
                        t[col * 4 + row] = (float)F64(mo + (row * 4 + col) * 8);
                t[15] = 1;
                if (off + 168 > data.Length) throw new InvalidDataException("IncludedObjects が切れています");
                ulong u3 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 160));
                int size = u3 == 0 ? 184 : 168;
                long end = off + size;
                while (end + 4 <= objEnd && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)end)) == 0) end += 4;
                if (id < cgfPaths.Count) res.Add(new InteriorMesh(cgfPaths[id], mtl, t, null));
                off = (int)end;
            }
            else if (type == 7) off += 152;
            else if (type == 0x10) off += 136;
            else off += 4;
            if (off > objEnd) break;
        }
        return res;
    }

    // 灯りやゲームの決まりだけの Entity (形にしない)
    private static readonly HashSet<string> SkipClasses = new(StringComparer.Ordinal)
    {
        "ActionArea", "AudioAreaAmbience", "AudioEnvironmentFeedbackPoint", "AudioTriggerSpot", "AreaShape", "CameraSource", "ColorGradient", "CommentEntity",
        "EditorCamera", "EnvironmentLight", "FlographEntity", "FogVolume", "GravityArea", "GravityBox", "GreenZone", "Hazard", "Hint", "Ladder", "LandingArea",
        "LedgeObject", "LocationManager", "MusicArea", "NavigationArea", "ParticleEffect", "ParticleField", "PlanetAreaEntity", "ProceduralPointOfInterestProxy",
        "Room", "RoomConnector", "SafeTeleportPoint", "SCShop", "SequenceObjectItem", "ShadowRegionEntity", "SurfaceRaindropsTarget", "TagPoint",
        "TransitDestination", "TransitGateway", "TransitManager", "TransitNavSpline", "VibrationAudioPoint", "VehicleAudioPoint",
    };

    // extract_cryxml_entities・process_entity_children (灯りは --no-lights で GLB に入らないので読まない)
    private static List<InteriorMesh> Entities(CryXmlB x, List<RawGravity>? gravity = null)
    {
        var res = new List<InteriorMesh>();
        int root = x.Root;
        var rootTag = x.Tag(root);
        int container = -1;
        foreach (var c in x.Children(root)) { var t = x.Tag(c); if (t == "Entities" || t == "SCOC_Entities") { container = c; break; } }
        if (container < 0 && (rootTag == "Entities" || rootTag == "SCOC_Entities")) container = root;
        if (container < 0) return res;
        foreach (var ent in x.Children(container))
        {
            if (x.Tag(ent) != "Entity") continue;
            var attrs = x.AttributeMap(ent);
            var cls = attrs.GetValueOrDefault("EntityClass", "");
            // 重力の範囲は形にしないで、別の一覧に読む (形の置き方・GLB は変えない)
            if (gravity != null && cls is "GravityBox" or "GravityArea") gravity.Add(ReadGravity(x, ent, cls, attrs));
            if (SkipClasses.Contains(cls) || cls.StartsWith("Door_Ship_Sensor", StringComparison.Ordinal) || cls.StartsWith("ChipSet_Light", StringComparison.Ordinal)) continue;
            var pos = Csv(attrs.GetValueOrDefault("Pos", "0,0,0"));
            var rot = Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"));
            var scl = Csv(attrs.GetValueOrDefault("Scale", "1,1,1"));
            if (cls is "Light" or "LightBox" or "LightGroup" or "LightGroupPoweredItem") continue;
            double G(List<double> l, int i, double d) => i < l.Count ? l[i] : d;
            var t = Glam.FromScaleRotationTranslation((float)G(scl, 0, 1), (float)G(scl, 1, 1), (float)G(scl, 2, 1),
                (float)G(rot, 1, 0), (float)G(rot, 2, 0), (float)G(rot, 3, 0), (float)G(rot, 0, 1),
                (float)G(pos, 0, 0), (float)G(pos, 1, 0), (float)G(pos, 2, 0));
            attrs.TryGetValue("Material", out var mtl);
            var geom = EntityGeometry(x, ent);
            if (geom != null) res.Add(new InteriorMesh(geom, mtl, t, null));
            else if (attrs.TryGetValue("EntityClassGUID", out var guid)) res.Add(new InteriorMesh("", mtl, t, guid));
        }
        return res;
    }

    // ===== 重力の範囲 (GravityBox・GravityArea) =====
    // .soc の Entity から読んだまま (DataCore の既定値で埋める前)。Over は PropertiesDataCore の GravityBox・GravityArea の属性、
    // Dir・SizeOver は gravityDirection・size の属性 (x・y・z のうち書かれたものだけ)
    internal sealed record RawGravity(string Class, string Name, string? Guid, double[] Pos, double[] Rot,
        Dictionary<string, string> Over, Dictionary<string, string> Dir, Dictionary<string, string> SizeOver, List<double[]>? Polygon, double? Height);

    private static RawGravity ReadGravity(CryXmlB x, int ent, string cls, Dictionary<string, string> attrs)
    {
        var pos = Csv(attrs.GetValueOrDefault("Pos", "0,0,0"));
        var rot = Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"));
        double G(List<double> l, int i, double d) => i < l.Count ? l[i] : d;
        var over = new Dictionary<string, string>(StringComparer.Ordinal);
        var dir = new Dictionary<string, string>(StringComparer.Ordinal);
        var size = new Dictionary<string, string>(StringComparer.Ordinal);
        List<double[]>? poly = null; double? height = null;
        foreach (var c in x.Children(ent))
        {
            var tag = x.Tag(c);
            if (tag == "PropertiesDataCore")
                foreach (var p in x.Children(c))
                {
                    if (x.Tag(p) != cls) continue;
                    foreach (var (k, v) in x.Attributes(p)) if (k != "__type") over[k] = v;
                    foreach (var q in x.Children(p))
                    {
                        var target = x.Tag(q) switch { "gravityDirection" => dir, "size" => size, _ => null };
                        if (target == null) continue;
                        foreach (var (k, v) in x.Attributes(q)) if (k is "x" or "y" or "z") target[k] = v;
                    }
                }
            else if (tag == "GameVolumeComponent")
            {
                if (x.AttributeMap(c).TryGetValue("volumeHeight", out var h) && RustF64.TryParse(h.Trim(), out var hv)) height = hv;
                poly = new List<double[]>();
                foreach (var pt in x.Children(c))
                {
                    if (x.Tag(pt) != "Point") continue;
                    var pp = Csv(x.AttributeMap(pt).GetValueOrDefault("Pos", "0,0,0"));
                    poly.Add([G(pp, 0, 0), G(pp, 1, 0), G(pp, 2, 0)]);
                }
            }
        }
        return new RawGravity(cls, attrs.GetValueOrDefault("Name", ""), attrs.TryGetValue("EntityClassGUID", out var guid) ? guid : null,
            [G(pos, 0, 0), G(pos, 1, 0), G(pos, 2, 0)], [G(rot, 0, 1), G(rot, 1, 0), G(rot, 2, 0), G(rot, 3, 0)], over, dir, size, poly, height);
    }

    // PropertiesDataCore → EntityGeometryResource → Geometry → Geometry → Geometry の path (空でない最初のもの)
    private static string? EntityGeometry(CryXmlB x, int ent)
    {
        foreach (var c in x.Children(ent))
        {
            if (x.Tag(c) != "PropertiesDataCore") continue;
            foreach (var p in x.Children(c))
            {
                if (x.Tag(p) != "EntityGeometryResource") continue;
                foreach (var g1 in x.Children(p))
                {
                    if (x.Tag(g1) != "Geometry") continue;
                    foreach (var g2 in x.Children(g1))
                    {
                        if (x.Tag(g2) != "Geometry") continue;
                        foreach (var g3 in x.Children(g2))
                        {
                            if (x.Tag(g3) != "Geometry") continue;
                            if (x.AttributeMap(g3).TryGetValue("path", out var path) && path.Length > 0) return path;
                        }
                    }
                }
            }
        }
        return null;
    }

    // parse_csv_f64: 「,」で分け、前後の空白を除いて f64 として読めたものだけ (読めないものは詰める)
    private static List<double> Csv(string s)
    {
        var res = new List<double>();
        foreach (var part in s.Split(','))
            if (RustF64.TryParse(part.Trim(), out var v)) res.Add(v);
        return res;
    }

    // ===== 部品の形 (export_cgf_from_path・load_geometry_parts・load_single_mesh。--materials none の部品は材質 colors で読む) =====
    private static SocpakScene.Mesh? LoadMesh(Context ctx, string cgfPath, string? materialPath, string name, out string? error, InnerFiles? inner = null)
    {
        var key = ctx.Lod + "|" + cgfPath + "|" + (materialPath ?? "\0");
        if (ctx.Meshes.TryGet(key, out var cached, out error))
        {
            if (cached != null && cached.Name != name) cached = Renamed(cached, name);
            return cached;
        }
        // 同じ形を同時に頼まれたら、1 回だけ読む (ほかのタスクは読み終わるのを待って同じものを使う)
        var lazy = ctx.InFlight.GetOrAdd(key, _ => new Lazy<(SocpakScene.Mesh?, string?)>(() =>
        {
            SocpakScene.Mesh? r = null; string? err = null;
            try { r = LoadMeshCore(ctx, cgfPath, materialPath ?? "", name, inner); }
            catch (Exception e) when (e is InvalidDataException or IOException or MeshLoadException or ZstdSharp.ZstdException) { err = e.Message; }
            ctx.Meshes.Put(key, r, err);
            return (r, err);
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            var (res, e2) = lazy.Value;
            error = e2;
            if (res != null && res.Name != name) res = Renamed(res, name);
            return res;
        }
        finally { ctx.InFlight.TryRemove(new KeyValuePair<string, Lazy<(SocpakScene.Mesh?, string?)>>(key, lazy)); }
    }

    private static SocpakScene.Mesh Renamed(SocpakScene.Mesh m, string name) => new() { Name = name, CgfPath = m.CgfPath, Positions = m.Positions, Uvs = m.Uvs, Primitives = m.Primitives, SubMaterials = m.SubMaterials, Mtl = m.Mtl, Min = m.Min, Max = m.Max };

    public sealed class MeshLoadException(string message) : Exception(message);

    // datacore_path_to_p4k
    public static string DataCorePathToP4k(string path)
    {
        string clean = path;
        foreach (var pre in new[] { "Data/", "data/", "Data\\", "data\\" })
            if (path.StartsWith(pre, StringComparison.Ordinal)) { clean = path[pre.Length..]; break; }
        return @"Data\" + clean.Replace('/', '\\');
    }

    private static SocpakScene.Mesh LoadMeshCore(Context ctx, string cgfPath, string materialPath, string name, InnerFiles? inner = null)
    {
        var p4k = ctx.P4k;
        var clean = cgfPath.Replace('\\', '/');
        var geometryPath = clean.StartsWith("data/", StringComparison.Ordinal) ? clean[5..] : clean.StartsWith("Data/", StringComparison.Ordinal) ? clean[5..] : clean;

        // resolve_geometry_files (.cdf は CharacterDefinition の Attachment ごと)
        var parts = new List<(string Path, string? Bone, string? MtlOverride)>();
        string? skeletonPath = null;
        if (!geometryPath.ToLowerInvariant().EndsWith(".cdf", StringComparison.Ordinal)) parts.Add((geometryPath, null, null));
        else
        {
            var cdfP = DataCorePathToP4k(geometryPath);
            var ce = p4k.Find(cdfP);
            if (ce < 0) throw new MeshLoadException($"file not found in P4k: {cdfP}");
            CryXmlB x;
            try { x = CryXmlB.Parse(p4k.Read(ce)); }
            catch (InvalidDataException e) { throw new MeshLoadException($"Failed to parse CDF {geometryPath}: {e.Message}"); }
            foreach (var c in x.Children(x.Root))
            {
                if (x.Tag(c) == "Model" && x.AttributeMap(c).TryGetValue("File", out var file) && file.Length > 0) skeletonPath = file;
                if (x.Tag(c) == "AttachmentList")
                    foreach (var a in x.Children(c))
                    {
                        if (x.Tag(a) != "Attachment") continue;
                        var am = x.AttributeMap(a);
                        if (am.TryGetValue("Binding", out var bind) && bind.Length > 0)
                            parts.Add((bind, am.GetValueOrDefault("BoneName"), am.TryGetValue("Material", out var mo) && mo.Length > 0 ? mo : null));
                    }
            }
            if (parts.Count == 0) throw new MeshLoadException($"CDF {geometryPath} has no attachments");
        }
        List<CryIvo.Bone> bones = new();
        if (skeletonPath != null)
        {
            var se = p4k.Find(DataCorePathToP4k(skeletonPath));
            if (se >= 0) { try { bones = CryIvo.ParseSkeleton(p4k.Read(se)) ?? new(); } catch (InvalidDataException) { } }
        }
        var effMtl = parts[0].MtlOverride ?? materialPath;
        var (mesh, mtl, nmc, mtlPath) = LoadSingle(ctx, parts[0].Path, effMtl, inner);
        for (int i = 1; i < parts.Count; i++)
        {
            try
            {
                var (extra, _, _, _) = LoadSingle(ctx, parts[i].Path, materialPath, inner);
                if (parts[i].Bone is string bn && bones.FirstOrDefault(b => string.Equals(b.Name, bn, StringComparison.OrdinalIgnoreCase)) is { } bone)
                    CryIvo.TransformByBone(extra, bone);
                mesh.MergeFrom(extra);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or MeshLoadException or ZstdSharp.ZstdException) { }
        }
        // 2 つの箱が 0.01 より違えば NMC を焼き込む
        bool needsBake = false;
        for (int k = 0; k < 3; k++)
            if (MathF.Abs(mesh.ScalingMin[k] - mesh.ModelMin[k]) > 0.01f || MathF.Abs(mesh.ScalingMax[k] - mesh.ModelMax[k]) > 0.01f) needsBake = true;
        if (needsBake) CryIvo.BakeNmc(mesh, nmc);

        // pack_mesh: 箱は全部の頂点、面は隠さない submesh だけ。UV・材質の番号・材質のパスは画像を貼るために残す (面の選び方は変えない)
        var res = new SocpakScene.Mesh { Name = name, CgfPath = cgfPath, Positions = mesh.Positions, Uvs = mesh.Uvs != null && mesh.Uvs.Length == mesh.Positions.Length / 3 * 2 ? mesh.Uvs : null, Mtl = mtlPath ?? "" };
        float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
        for (int v = 0; v < mesh.VertexCount; v++)
            for (int k = 0; k < 3; k++) { var f = mesh.Positions[v * 3 + k]; mn[k] = RustF32.Min(mn[k], f); mx[k] = RustF32.Max(mx[k], f); }
        for (int k = 0; k < 3; k++) { res.Min[k] = mn[k]; res.Max[k] = mx[k]; }
        if (mesh.VertexCount == 0) return res;   // 位置の accessor が作られない (面は読めない) ので、面は持たない
        foreach (var sub in mesh.SubMeshes)
        {
            var m = mtl != null && sub.MaterialId < mtl.Materials.Count ? mtl.Materials[(int)sub.MaterialId] : null;
            if (m != null && CryMtl.HideWithoutTextures(m)) continue;
            long st = sub.FirstIndex, cnt = sub.NumIndices;
            if (st + cnt > mesh.Indices.Length) throw new MeshLoadException("submesh の索引の範囲が外れています");
            res.Primitives.Add(mesh.Indices.AsSpan((int)st, (int)cnt).ToArray());
            res.SubMaterials.Add((int)sub.MaterialId);
        }
        return res;
    }

    // load_single_mesh (形・NMC・材質)。MtlPath: 読めた材質のファイルの Data.p4k の中のパス (無ければ null)
    private static (CryMesh Mesh, CryMtl? Mtl, List<CryIvo.NmcNode>? Nmc, string? MtlPath) LoadSingle(Context ctx, string geometryPath, string materialPath, InnerFiles? inner = null)
    {
        var p4k = ctx.P4k;
        var geomP4k = DataCorePathToP4k(geometryPath);
        var companion = geomP4k + "m";
        if (ctx.Lod > 0)
        {
            int dot = geomP4k.LastIndexOf('.');
            var lodGeom = dot >= 0 ? $"{geomP4k[..dot]}_lod{ctx.Lod}{geomP4k[dot..]}" : $"{geomP4k}_lod{ctx.Lod}";
            if (p4k.Find(lodGeom + "m") >= 0) companion = lodGeom + "m";
        }
        int ce = p4k.Find(companion);
        if (ce < 0 && inner?.Locate(geomP4k) is { } loc) return LoadSingleInner(ctx, loc.Arch, loc.Rel, geomP4k, materialPath, companion);
        if (ce < 0) throw new MeshLoadException($"file not found in P4k: {companion}");
        var meshBytes = p4k.Read(ce);
        // load_nmc_and_material: 元の .cgf (無くてもよい)
        byte[]? meta = null;
        int me = p4k.Find(geomP4k);
        if (me >= 0) { try { meta = p4k.Read(me); } catch (InvalidDataException) { } }
        var mtl = ResolveMaterial(p4k, materialPath, geomP4k, meta, out var mtlPath);
        var nmc = meta != null ? CryIvo.ParseNmc(meta) : null;
        var mesh = CryIvo.ParseSkin(meshBytes);
        return (mesh, mtl, nmc, mtlPath);
    }

    // socpak の中の形: 対の .cgfm (_lod<N> があればそれ) があれば #ivo として、無ければ .cgf そのもの (CrCh なら CryCrChMesh、#ivo なら IvoSkin2) を読む。
    // 材質は p4k から (DataCore の材質のパス → だめなら .cgf の材質の名前。読み方は p4k の形と同じ)
    private static (CryMesh Mesh, CryMtl? Mtl, List<CryIvo.NmcNode>? Nmc, string? MtlPath) LoadSingleInner(Context ctx, P4kArchive arch, string rel, string geomP4k, string materialPath, string p4kCompanion)
    {
        var comp = rel + "m";
        if (ctx.Lod > 0)
        {
            int dot = rel.LastIndexOf('.');
            var lodRel = dot >= 0 ? $"{rel[..dot]}_lod{ctx.Lod}{rel[dot..]}" : $"{rel}_lod{ctx.Lod}";
            if (arch.Find(lodRel + "m") >= 0) comp = lodRel + "m";
        }
        int ge = arch.Find(rel);
        byte[]? meta = null;
        if (ge >= 0) { try { meta = arch.Read(ge); } catch (InvalidDataException) { } }
        int ce = arch.Find(comp);
        if (ce >= 0)
        {
            var mtl1 = ResolveMaterial(ctx.P4k, materialPath, geomP4k, meta, out var mp1);
            return (CryIvo.ParseSkin(arch.Read(ce)), mtl1, meta != null ? CryIvo.ParseNmc(meta) : null, mp1);
        }
        if (meta == null) throw new MeshLoadException($"file not found in P4k: {p4kCompanion}");
        if (CryCrChMesh.IsCrCh(meta))
        {
            var mesh = CryCrChMesh.Parse(meta);
            CryMtl? mtl = null; string? mp = null;
            if (materialPath.Length > 0) { mp = DataCorePathToP4k(materialPath); mtl = TryLoadMtl(ctx.P4k, mp); if (mtl == null) mp = null; }
            if (mtl == null && CryCrChMesh.MtlName(meta) is string mn) { mp = MtlPathFromName(mn, geomP4k); mtl = TryLoadMtl(ctx.P4k, mp); if (mtl == null) mp = null; }
            return (mesh, mtl, null, mp);
        }
        var mtl2 = ResolveMaterial(ctx.P4k, materialPath, geomP4k, meta, out var mp2);
        return (CryIvo.ParseSkin(meta), mtl2, CryIvo.ParseNmc(meta), mp2);
    }

    // 材質の名前 → p4k のパス (resolve_mtl_p4k_path。「/」「\」があれば Data\ から、無ければ形と同じフォルダ)
    private static string MtlPathFromName(string mn, string geomP4k)
    {
        if (mn.Contains('/') || mn.Contains('\\')) return $"Data\\{mn.Replace('/', '\\')}.mtl";
        int i = geomP4k.LastIndexOf('\\');
        return $"{(i >= 0 ? geomP4k[..i] : geomP4k)}\\{mn}.mtl";
    }

    // resolve_material: DataCore の材質のパス → だめなら .cgf の MtlName (パスに「/」「\」があれば Data\ から、無ければ .cgf と同じフォルダ)。
    // mtlPath: 読めた材質のファイルの p4k の中のパス (読めなければ null。画像を貼る側が .mtl を読み直すのに使う。選び方は変えない)
    private static CryMtl? ResolveMaterial(P4kArchive p4k, string materialPath, string geomP4k, byte[]? meta, out string? mtlPath)
    {
        mtlPath = null;
        if (materialPath.Length > 0)
        {
            var mp = DataCorePathToP4k(materialPath);
            if (TryLoadMtl(p4k, mp) is { } a) { mtlPath = mp; return a; }
        }
        if (meta == null || CryIvo.ExtractMtlName(meta) is not string mn) return null;
        string path;
        if (mn.Contains('/') || mn.Contains('\\')) path = $"Data\\{mn.Replace('/', '\\')}.mtl";
        else
        {
            int i = geomP4k.LastIndexOf('\\');
            path = $"{(i >= 0 ? geomP4k[..i] : geomP4k)}\\{mn}.mtl";
        }
        var r = TryLoadMtl(p4k, path);
        if (r != null) mtlPath = path;
        return r;
    }

    private static CryMtl? TryLoadMtl(P4kArchive p4k, string path)
    {
        int e = p4k.Find(path);
        if (e < 0) return null;
        try { return CryMtl.Parse(p4k.Read(e)); }
        catch (InvalidDataException) { return null; }
    }
}

// Rust の str::parse::<f64> と同じ書き方だけを数として読む (C# の double.Parse より狭い: 3 桁区切り・前後の空白・16 進などは読まない)
internal static class RustF64
{
    public static bool TryParse(string s, out double v)
    {
        v = 0;
        if (s.Length == 0) return false;
        var t = s;
        int i = 0;
        if (t[0] == '+' || t[0] == '-') i = 1;
        var body = t[i..];
        var lower = body.ToLowerInvariant();
        if (lower is "inf" or "infinity") { v = t[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity; return true; }
        if (lower == "nan") { v = double.NaN; return true; }
        // 数字・小数点・指数だけ
        bool digits = false, dot = false, exp = false;
        for (int k = 0; k < body.Length; k++)
        {
            char c = body[k];
            if (c >= '0' && c <= '9') { digits = true; continue; }
            if (c == '.' && !dot && !exp) { dot = true; continue; }
            if ((c == 'e' || c == 'E') && digits && !exp)
            {
                exp = true;
                if (k + 1 < body.Length && (body[k + 1] == '+' || body[k + 1] == '-')) k++;
                if (k + 1 >= body.Length) return false;
                continue;
            }
            return false;
        }
        if (!digits) return false;
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out v);
    }
}

// Rust の f32::min・max (片方が NaN ならもう片方)
internal static class RustF32
{
    public static float Min(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : a < b ? a : b;
    public static float Max(float a, float b) => float.IsNaN(a) ? b : float.IsNaN(b) ? a : a > b ? a : b;
}
