using System.Text;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D の自前の読み取り (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」(案 A)・「B」(バイト単位まで同じにする))。
// StarBreaker v0.2.2 の `entity export <NAME> <out.glb> --p4k <Data.p4k> --materials textures` と、`--dump-hierarchy` の移植。
// 移植元: cli/src/entity.rs (find_candidates・export)、crates/starbreaker-datacore/src/loadout.rs (resolve_loadout_indexed)、
//   crates/starbreaker-3d/src/pipeline.rs (assemble_glb_with_loadout 以下・dump_hierarchy)、socpak.rs (船内の区画と灯り)。
// 既定の設定 (common.rs ExportOpts): 材質 textures・テクスチャの細かさ mip 2・lod 1・付属品あり・船内あり・灯りあり。
// GLB の組み立ては ShipGlb.cs、形・NMC は ShipExportMesh.cs。
internal static partial class ShipExport
{
    public const int Lod = 1, Mip = 2;

    public sealed class Ctx
    {
        public P4kArchive P4k = null!;
        public DataCoreDb Db = null!;
        public readonly Dictionary<string, ShipGlb.PngBox?> PngCache = new(StringComparer.Ordinal);
        public Action<string>? Log;
        public CancellationToken Ct;
    }

    // ===== 船の選び方 (entity.rs find_candidates): EntityClassDefinition のうち、名前の小文字に検索の小文字を含むもの。名前の短い順 (UTF-8 のバイト数。同じなら record の順) の最初 =====
    public static DataCoreDb.Record? FindRecord(DataCoreDb db, string name, out int candidates)
    {
        candidates = 0;
        var search = name.ToLowerInvariant();
        if (db.StructIndexByName("EntityClassDefinition") is not int si) return null;
        DataCoreDb.Record? best = null; int bestLen = int.MaxValue;
        foreach (var r in db.Records)
        {
            if (r.StructIndex != si) continue;
            var n = db.String2(r.NameOffset);
            if (!n.ToLowerInvariant().Contains(search, StringComparison.Ordinal)) continue;
            candidates++;
            int len = Encoding.UTF8.GetByteCount(n);
            if (len < bestLen) { best = r; bestLen = len; }
        }
        return best;
    }

    // ===== ロードアウト (loadout.rs) =====
    public sealed class LoadoutNode
    {
        public DataCoreDb.Record Record;
        public string EntityName = "", ItemPortName = "";
        public string? HelperBoneName;
        public bool NoRotation;
        public float[] OffPos = new float[3], OffRot = new float[3];
        public string PortTags = "";
        public string? GeometryPath, MaterialPath;
        public List<LoadoutNode> Children = new();
    }

    public sealed record SubGeomVariant(string Tag, string GeometryPath, string MaterialPath);

    // EntityIndex は DataCore 1 つにつき 1 回だけ作り、全部の書き出しで使う (2026-11-04、利用者の指示「マルチタスク対応にして」)
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataCoreDb, EntityIndex> Indexes = new();
    public static EntityIndex IndexOf(DataCoreDb db) => Indexes.GetValue(db, d => new EntityIndex(d));

    public sealed class EntityIndex
    {
        private readonly Dictionary<string, int> _names = new(StringComparer.Ordinal);
        public readonly DataCoreDb Db;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (DataCoreDb.CompiledPath G, DataCoreDb.CompiledPath M)?> _geom = new();
        public EntityIndex(DataCoreDb db)
        {
            Db = db;
            if (db.StructIndexByName("EntityClassDefinition") is int si)
                for (int i = 0; i < db.Records.Length; i++)
                {
                    var r = db.Records[i];
                    if (r.StructIndex != si) continue;
                    var n = db.String2(r.NameOffset);
                    _names[n[(n.LastIndexOf('.') + 1)..].ToLowerInvariant()] = i;
                }
        }
        public DataCoreDb.Record? Find(string name) => _names.TryGetValue(name.ToLowerInvariant(), out var i) ? Db.Records[i] : null;

        public (string? Geom, string? Mtl) GeometryPaths(DataCoreDb.Record r)
        {
            var c = _geom.GetOrAdd(r.StructIndex, si =>
            {
                var g = TryCompile(Db, si, "Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path", DataCoreDb.StringLeaf);
                var m = TryCompile(Db, si, "Components[SGeometryResourceParams].Geometry.Geometry.Material.path", DataCoreDb.StringLeaf);
                return g != null && m != null ? (g, m) : null;
            });
            if (c is not { } cc) return (null, null);
            var geom = TryString(Db, cc.G, r); var mtl = TryString(Db, cc.M, r);
            return (string.IsNullOrEmpty(geom) ? null : geom, string.IsNullOrEmpty(mtl) ? null : mtl);
        }

        public List<SubGeomVariant> SubGeometry(DataCoreDb.Record r)
        {
            var res = new List<SubGeomVariant>();
            var cp = TryCompile(Db, r.StructIndex, "Components[SGeometryResourceParams]", DataCoreDb.AnyLeaf);
            if (cp == null) return res;
            var comps = TryValues(Db, cp, r, false);
            if (comps == null) return res;
            foreach (var comp in comps)
            {
                if (DcValue.ObjField(comp, "Geometry") is not { } g) continue;
                if (DcValue.ArrField(g, "SubGeometry") is not { } arr) continue;
                foreach (var sub in arr)
                {
                    var tag = DcValue.StrField(sub, "Tags") ?? "";
                    var gd = DcValue.ObjField(sub, "Geometry");
                    var geom = gd == null ? null : DcValue.ObjField(gd, "Geometry") is { } gg ? NonEmpty(DcValue.StrField(gg, "path")) : null;
                    var mtl = gd == null ? null : DcValue.ObjField(gd, "Material") is { } mm ? NonEmpty(DcValue.StrField(mm, "path")) : null;
                    if (tag.Length == 0) continue;
                    if (geom != null) res.Add(new SubGeomVariant(tag, geom, mtl ?? ""));
                }
            }
            return res;
        }
    }

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    internal static DataCoreDb.CompiledPath? TryCompile(DataCoreDb db, int si, string path, DataCoreDb.DT[] leaf)
    {
        try { return db.CompilePath(si, path, leaf); }
        catch (DataCoreDb.QueryException) { return null; }
    }
    internal static string? TryString(DataCoreDb db, DataCoreDb.CompiledPath cp, DataCoreDb.Record r)
    {
        try { return db.QueryFirstString(cp, r); }
        catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
    }
    internal static List<DcValue>? TryValues(DataCoreDb db, DataCoreDb.CompiledPath cp, DataCoreDb.Record r, bool noRefs)
    {
        try { return db.QueryValues(cp, r, noRefs); }
        catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
    }
    private static byte? TryByte(DataCoreDb db, DataCoreDb.CompiledPath cp, DataCoreDb.Record r)
    {
        try { return db.QueryFirstByte(cp, r); }
        catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException) { return null; }
    }

    // resolve_loadout_indexed
    public static LoadoutNode ResolveLoadout(EntityIndex idx, DataCoreDb.Record record)
    {
        var db = idx.Db;
        var (g, m) = idx.GeometryPaths(record);
        var children = new List<LoadoutNode>();
        var cp = TryCompile(db, record.StructIndex, "Components[SEntityComponentDefaultLoadoutParams]", DataCoreDb.AnyLeaf);
        if (cp != null && TryValues(db, cp, record, true) is { } comps)
            foreach (var comp in comps)
                if (DcValue.ObjField(comp, "loadout") is { } lo) CollectEntries(idx, lo, children, 0);
        ApplyPortInfo(db, record, children);
        ResolveSubGeometry(idx, children);
        return new LoadoutNode { Record = record, EntityName = db.String2(record.NameOffset), GeometryPath = g, MaterialPath = m, Children = children };
    }

    private const int MaxLoadoutDepth = 4;

    private static void CollectEntries(EntityIndex idx, DcValue loadout, List<LoadoutNode> outList, int depth)
    {
        if (depth >= MaxLoadoutDepth) return;
        if (DcValue.ArrField(loadout, "entries") is not { } entries) return;
        var db = idx.Db;
        foreach (var entry in entries)
        {
            var port = DcValue.StrField(entry, "itemPortName") ?? "";
            var cls = DcValue.StrField(entry, "entityClassName") ?? "";
            var grand = new List<LoadoutNode>();
            if (DcValue.ObjField(entry, "loadout") is { } nested) CollectEntries(idx, nested, grand, depth + 1);
            if (cls.Length > 0)
            {
                if (idx.Find(cls) is not DataCoreDb.Record cr) continue;
                var (cg, cm) = idx.GeometryPaths(cr);
                outList.Add(new LoadoutNode { Record = cr, EntityName = cls, ItemPortName = port, GeometryPath = cg, MaterialPath = cm, Children = grand });
            }
            else if (DcValue.ObjField(entry, "entityClassReference") is { } refv)
            {
                var refGeom = ExtractGeometry(refv, false);
                var refMtl = ExtractGeometry(refv, true);
                if (ExtractLoadout(refv) is { } rl) CollectEntries(idx, rl, grand, depth + 1);
                DataCoreDb.Record? fromId = refv.RecordId is { } gid ? db.RecordById(gid) : null;
                string refName;
                if (fromId is DataCoreDb.Record fr) { var full = db.String2(fr.NameOffset); refName = full[(full.LastIndexOf('.') + 1)..]; }
                else
                {
                    var cn = DcValue.StrField(refv, "className");
                    if (!string.IsNullOrEmpty(cn)) refName = cn;
                    else if (refGeom != null)
                    {
                        var last = refGeom[(refGeom.LastIndexOfAny(['/', '\\']) + 1)..];
                        int dot = last.IndexOf('.');
                        refName = dot >= 0 ? last[..dot] : last;
                    }
                    else refName = $"<ref@{port}>";
                }
                var rec = fromId ?? idx.Find(refName);
                if (refGeom == null || refMtl == null)
                    if (rec is DataCoreDb.Record rr)
                    {
                        var (g2, m2) = idx.GeometryPaths(rr);
                        refGeom ??= g2; refMtl ??= m2;
                    }
                if (rec is DataCoreDb.Record r3)
                    outList.Add(new LoadoutNode { Record = r3, EntityName = refName, ItemPortName = port, GeometryPath = refGeom, MaterialPath = refMtl, Children = grand });
                else if (refGeom != null)
                {
                    if (db.Records.Length == 0) continue;
                    outList.Add(new LoadoutNode { Record = db.Records[0], EntityName = refName, ItemPortName = port, GeometryPath = refGeom, MaterialPath = refMtl, Children = grand });
                }
            }
            else if (DcValue.GuidField(entry, "entityClassReference") is { } guid)
            {
                if (db.RecordById(guid) is not DataCoreDb.Record rr) continue;
                var full = db.String2(rr.NameOffset);
                var shortName = full[(full.LastIndexOf('.') + 1)..];
                var (rg, rm) = idx.GeometryPaths(rr);
                var cp = TryCompile(db, rr.StructIndex, "Components[SEntityComponentDefaultLoadoutParams]", DataCoreDb.AnyLeaf);
                if (cp != null && TryValues(db, cp, rr, true) is { } comps)
                    foreach (var comp in comps)
                        if (DcValue.ObjField(comp, "loadout") is { } lo) CollectEntries(idx, lo, grand, depth + 1);
                outList.Add(new LoadoutNode { Record = rr, EntityName = shortName, ItemPortName = port, GeometryPath = rg, MaterialPath = rm, Children = grand });
            }
        }
    }

    // extract_geometry_from_value / extract_material_from_value (Components[] の SGeometryResourceParams の最初のもの)
    private static string? ExtractGeometry(DcValue entity, bool material)
    {
        if (DcValue.ArrField(entity, "Components") is not { } comps) return null;
        foreach (var comp in comps)
        {
            if (comp is not DcValue.Obj o || o.TypeName != "SGeometryResourceParams") continue;
            if (DcValue.ObjField(comp, "Geometry") is not { } g) return null;
            if (DcValue.ObjField(g, "Geometry") is not { } g2) return null;
            if (material)
            {
                if (DcValue.ObjField(g2, "Material") is not { } mm) return null;
                return NonEmpty(DcValue.StrField(mm, "path"));
            }
            if (DcValue.ObjField(g2, "Geometry") is not { } g3) return null;
            return NonEmpty(DcValue.StrField(g3, "path"));
        }
        return null;
    }

    private static DcValue? ExtractLoadout(DcValue entity)
    {
        if (DcValue.ArrField(entity, "Components") is not { } comps) return null;
        foreach (var comp in comps)
            if (comp is DcValue.Obj o && o.TypeName == "SEntityComponentDefaultLoadoutParams") return DcValue.ObjField(comp, "loadout");
        return null;
    }

    private sealed record PortInfo(string? Bone, bool NoRotation, float[] Pos, float[] Rot, string Tags);

    private static Dictionary<string, PortInfo> PortInfoMap(DataCoreDb db, DataCoreDb.Record record)
    {
        var map = new Dictionary<string, PortInfo>(StringComparer.Ordinal);
        var cp = TryCompile(db, record.StructIndex, "Components[SItemPortContainerComponentParams]", DataCoreDb.AnyLeaf);
        if (cp == null) return map;
        if (TryValues(db, cp, record, false) is not { } comps) return map;
        foreach (var comp in comps)
        {
            if (DcValue.ArrField(comp, "Ports") is not { } ports) continue;
            foreach (var port in ports)
            {
                var pn = DcValue.StrField(port, "Name");
                if (string.IsNullOrEmpty(pn)) continue;
                var ai = DcValue.ObjField(port, "AttachmentImplementation");
                var helper = ai == null ? null : DcValue.ObjField(ai, "Helper") is { } hn ? DcValue.ObjField(hn, "Helper") : null;
                var bone = helper == null ? null : NonEmpty(DcValue.StrField(helper, "Name"));
                var noRot = (ai == null ? null : DcValue.ObjField(ai, "constraintParams") is { } cpp ? DcValue.BoolField(cpp, "noRotation") : null) ?? false;
                var off = helper == null ? null : DcValue.ObjField(helper, "Offset");
                float[] pos = off != null && DcValue.ObjField(off, "Position") is { } p
                    ? [DcValue.F32Field(p, "x") ?? 0f, DcValue.F32Field(p, "y") ?? 0f, DcValue.F32Field(p, "z") ?? 0f] : [0, 0, 0];
                float[] rot = off != null && DcValue.ObjField(off, "Rotation") is { } ro
                    ? [DcValue.F32Field(ro, "x") ?? 0f, DcValue.F32Field(ro, "y") ?? 0f, DcValue.F32Field(ro, "z") ?? 0f] : [0, 0, 0];
                var tags = DcValue.StrField(port, "PortTags") ?? "";
                map[pn] = new PortInfo(bone, noRot, pos, rot, tags);
            }
        }
        return map;
    }

    private static void ApplyPortInfo(DataCoreDb db, DataCoreDb.Record parent, List<LoadoutNode> children)
    {
        var map = PortInfoMap(db, parent);
        foreach (var c in children)
        {
            if (map.TryGetValue(c.ItemPortName, out var info))
            {
                c.HelperBoneName = info.Bone; c.NoRotation = info.NoRotation; c.OffPos = info.Pos; c.OffRot = info.Rot; c.PortTags = info.Tags;
            }
            ApplyPortInfo(db, c.Record, c.Children);
        }
    }

    private static void ResolveSubGeometry(EntityIndex idx, List<LoadoutNode> children)
    {
        var unmatched = new List<int>();
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            if (c.GeometryPath == null) continue;
            var vars = idx.SubGeometry(c.Record);
            if (vars.Count == 0) continue;
            if (MatchVariant(vars, c.PortTags, c.HelperBoneName, c.ItemPortName) is { } m)
            {
                c.GeometryPath = m.GeometryPath;
                if (m.MaterialPath.Length > 0) c.MaterialPath = m.MaterialPath;
            }
            else if (vars.Count > 1) unmatched.Add(i);
        }
        foreach (var ui in unmatched)
        {
            var vars = idx.SubGeometry(children[ui].Record);
            var entity = children[ui].EntityName;
            var claimed = children.Where((c, j) => j != ui && c.EntityName == entity).Select(c => c.GeometryPath).Where(p => p != null).ToList();
            if (vars.FirstOrDefault(v => !claimed.Contains(v.GeometryPath)) is { } v)
            {
                children[ui].GeometryPath = v.GeometryPath;
                if (v.MaterialPath.Length > 0) children[ui].MaterialPath = v.MaterialPath;
            }
        }
        foreach (var c in children) ResolveSubGeometry(idx, c.Children);
    }

    private static SubGeomVariant? MatchVariant(List<SubGeomVariant> vars, string portTags, string? bone, string port)
    {
        if (portTags.Length > 0)
        {
            var tl = portTags.ToLowerInvariant();
            foreach (var v in vars) if (tl.Contains(v.Tag.ToLowerInvariant(), StringComparison.Ordinal)) return v;
        }
        if (bone != null)
        {
            var bl = bone.ToLowerInvariant();
            foreach (var v in vars) if (bl.Contains(v.Tag.ToLowerInvariant(), StringComparison.Ordinal)) return v;
        }
        var pl = port.ToLowerInvariant();
        foreach (var v in vars) if (pl.Contains(v.Tag.ToLowerInvariant(), StringComparison.Ordinal)) return v;
        return null;
    }

    // ===== 読み込みの道具 (pipeline.rs) =====
    public sealed class ExportException(string message) : Exception(message);

    // datacore_path_to_p4k
    public static string DataCorePathToP4k(string path) => SocpakExporter.DataCorePathToP4k(path);

    private static byte[]? ReadP4k(P4kArchive p4k, string path)
    {
        int e = p4k.Find(path);
        if (e < 0) return null;
        try { return p4k.Read(e); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ZstdSharp.ZstdException) { return null; }
    }

    // resolve_companion_path
    private static string CompanionPath(P4kArchive p4k, string geomP4k)
    {
        int dot = geomP4k.LastIndexOf('.');
        var lodGeom = dot >= 0 ? $"{geomP4k[..dot]}_lod{Lod}{geomP4k[dot..]}" : $"{geomP4k}_lod{Lod}";
        var lodComp = lodGeom + "m";
        return p4k.Find(lodComp) >= 0 ? lodComp : geomP4k + "m";
    }

    // try_load_mtl (source_path は渡したパス)
    public static SocpakGlb.MtlFile? TryLoadMtl(P4kArchive p4k, string path)
    {
        if (ReadP4k(p4k, path) is not { } d) return null;
        try { var m = SocpakGlb.ParseMtl(d); m.SourcePath = path; return m; }
        catch (InvalidDataException) { return null; }
    }

    // resolve_mtl_p4k_path
    private static string MtlP4kPath(string name, string geomP4k)
    {
        if (name.Contains('/') || name.Contains('\\')) return $"Data\\{name.Replace('/', '\\')}.mtl";
        int i = geomP4k.LastIndexOf('\\');
        return $"{(i >= 0 ? geomP4k[..i] : geomP4k)}\\{name}.mtl";
    }

    // resolve_material
    private static SocpakGlb.MtlFile? ResolveMaterial(P4kArchive p4k, string dcMtl, string geomP4k, byte[]? meta)
    {
        if (dcMtl.Length > 0 && TryLoadMtl(p4k, DataCorePathToP4k(dcMtl)) is { } a) return a;
        if (meta == null || CryIvo.ExtractMtlName(meta) is not string mn) return null;
        return TryLoadMtl(p4k, MtlP4kPath(mn, geomP4k));
    }

    // load_nmc_and_material
    private static (List<ShipMeshes.NmcNode>? Nmc, SocpakGlb.MtlFile? Mtl) NmcAndMaterial(P4kArchive p4k, string geometryPath, string materialPath)
    {
        var geomP4k = DataCorePathToP4k(geometryPath);
        var meta = ReadP4k(p4k, geomP4k);
        var mtl = ResolveMaterial(p4k, materialPath, geomP4k, meta);
        return (ShipMeshes.ParseNmc(meta), mtl);
    }

    private sealed record GeometryPart(string Path, string? Bone, string? MtlOverride);

    // resolve_geometry_files
    private static (List<GeometryPart> Parts, string? Skeleton) ResolveGeometryFiles(P4kArchive p4k, string geometryPath)
    {
        if (!geometryPath.ToLowerInvariant().EndsWith(".cdf", StringComparison.Ordinal)) return ([new GeometryPart(geometryPath, null, null)], null);
        var p = DataCorePathToP4k(geometryPath);
        int e = p4k.Find(p);
        if (e < 0) throw new ExportException($"file not found in P4k: {p}");
        var data = p4k.Read(e);
        CryXmlB x;
        try { x = CryXmlB.Parse(data); }
        catch (InvalidDataException ex) { throw new ExportException($"Failed to parse CDF {geometryPath}: {ex.Message}"); }
        var parts = new List<GeometryPart>();
        string? skel = null;
        foreach (var c in x.Children(x.Root))
        {
            if (x.Tag(c) == "Model" && x.AttributeMap(c).TryGetValue("File", out var f) && f.Length > 0) skel = f;
            if (x.Tag(c) == "AttachmentList")
                foreach (var a in x.Children(c))
                {
                    if (x.Tag(a) != "Attachment") continue;
                    var am = x.AttributeMap(a);
                    if (am.TryGetValue("Binding", out var b) && b.Length > 0)
                        parts.Add(new GeometryPart(b, am.TryGetValue("BoneName", out var bn) ? bn : null, am.TryGetValue("Material", out var mo) && mo.Length > 0 ? mo : null));
                }
        }
        if (parts.Count == 0) throw new ExportException($"CDF {geometryPath} has no attachments");
        return (parts, skel);
    }

    private static List<CryIvo.Bone> LoadSkeleton(P4kArchive p4k, string? path)
    {
        if (path == null) return new();
        if (ReadP4k(p4k, DataCorePathToP4k(path)) is not { } d) return new();
        try { return CryIvo.ParseSkeleton(d) ?? new(); } catch (InvalidDataException) { return new(); }
    }

    public sealed class Loaded
    {
        public ShipMeshes.Mesh Mesh = null!;
        public SocpakGlb.MtlFile? Mtl;
        public ShipGlb.Textures? Tex;
        public List<ShipMeshes.NmcNode>? Nmc;
        public float[][]? Palette;
        public string PrimaryPath = "", MaterialPath = "";
        public List<CryIvo.Bone> Bones = new();
    }

    // load_single_mesh (withTextures: material_mode が textures のとき)
    private static (ShipMeshes.Mesh Mesh, SocpakGlb.MtlFile? Mtl, ShipGlb.Textures? Tex, List<ShipMeshes.NmcNode>? Nmc) LoadSingleMesh(Ctx ctx, string geometryPath, string materialPath, bool withTextures)
    {
        var p4k = ctx.P4k;
        var geomP4k = DataCorePathToP4k(geometryPath);
        var comp = CompanionPath(p4k, geomP4k);
        int e = p4k.Find(comp);
        if (e < 0) throw new ExportException($"file not found in P4k: {comp}");
        var bytes = p4k.Read(e);
        var (nmc, mtl) = NmcAndMaterial(p4k, geometryPath, materialPath);
        var tex = withTextures && mtl != null ? ShipTextures.Load(ctx, mtl) : null;
        var mesh = ShipMeshes.ParseSkin(bytes);
        return (mesh, mtl, tex, nmc);
    }

    // load_geometry_parts
    private static Loaded LoadGeometryParts(Ctx ctx, string geometryPath, string materialPath, bool withTextures)
    {
        var (parts, skelPath) = ResolveGeometryFiles(ctx.P4k, geometryPath);
        var primary = parts[0].Path;
        var bones = LoadSkeleton(ctx.P4k, skelPath);
        var eff = parts[0].MtlOverride ?? materialPath;
        var (mesh, mtl, tex, nmc) = LoadSingleMesh(ctx, primary, eff, withTextures);
        for (int i = 1; i < parts.Count; i++)
        {
            try
            {
                var (extra, _, _, _) = LoadSingleMesh(ctx, parts[i].Path, materialPath, false);
                if (parts[i].Bone is string bn && bones.FirstOrDefault(b => string.Equals(b.Name, bn, StringComparison.OrdinalIgnoreCase)) is { } bone) ShipMeshes.ByBone(extra, bone);
                ShipMeshes.Merge(mesh, extra);
            }
            catch (Exception ex) when (IsLoadError(ex)) { ctx.Log?.Invoke($"  CDF part '{parts[i].Path}' failed: {ex.Message}"); }
        }
        return new Loaded { Mesh = mesh, Mtl = mtl, Tex = tex, Nmc = nmc, PrimaryPath = primary, MaterialPath = materialPath, Bones = bones };
    }

    internal static bool IsLoadError(Exception e) => e is InvalidDataException or IOException or ExportException or ZstdSharp.ZstdException or IndexOutOfRangeException or ArgumentOutOfRangeException or ArgumentException or OverflowException;

    // export_entity_payload (DataCore から形・材質のパス)
    private static Loaded EntityPayload(Ctx ctx, DataCoreDb.Record record, bool withTextures, bool withMaterials)
    {
        var db = ctx.Db;
        var name = db.String2(record.NameOffset);
        var gc = TryCompile(db, record.StructIndex, "Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path", DataCoreDb.StringLeaf)
            ?? throw new ExportException($"no geometry component: {name}");
        var mc = TryCompile(db, record.StructIndex, "Components[SGeometryResourceParams].Geometry.Geometry.Material.path", DataCoreDb.StringLeaf)
            ?? throw new ExportException($"no geometry component: {name}");
        string geom, mtl;
        try
        {
            geom = db.QueryFirstString(gc, record) ?? throw new ExportException($"no geometry component: {name}");
            mtl = db.QueryFirstString(mc, record) ?? "";
        }
        catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException) { throw new ExportException($"DataCore query: {e.Message}"); }
        var l = LoadGeometryParts(ctx, geom, mtl, withTextures);
        l.MaterialPath = mtl;
        if (!withMaterials) { l.Mtl = null; l.Tex = null; return l; }
        l.Palette = QueryTintPalette(db, record);
        return l;
    }

    // export_cgf_from_path (船内の部品。材質は colors (テクスチャ無し))
    public static Loaded LoadForInterior(Ctx ctx, string geometryPath, string materialPath) => FromPaths(ctx, geometryPath, materialPath);

    // export_entity_from_paths (DataCore を引かない。材質は colors (テクスチャ無し))
    private static Loaded FromPaths(Ctx ctx, string geometryPath, string materialPath)
    {
        var l = LoadGeometryParts(ctx, geometryPath, materialPath, false);
        l.Bones = new();
        return l;
    }

    // ===== パレット =====
    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float[][]? QueryTintFromPath(DataCoreDb db, DataCoreDb.Record record, string basePath)
    {
        var tc = TryCompile(db, record.StructIndex, $"{basePath}.entryA.tintColor.r", [DataCoreDb.DT.Byte]);
        if (tc == null) return null;
        if (TryByte(db, tc, record) == null) return null;
        float[] Rgb(string entry)
        {
            var rgb = new float[] { 0.5f, 0.5f, 0.5f };
            string[] ch = ["r", "g", "b"];
            for (int i = 0; i < 3; i++)
                if (TryCompile(db, record.StructIndex, $"{basePath}.{entry}.tintColor.{ch[i]}", [DataCoreDb.DT.Byte]) is { } c && TryByte(db, c, record) is byte v)
                    rgb[i] = SrgbToLinear(v / 255.0f);
            return rgb;
        }
        return [Rgb("entryA"), Rgb("entryB"), Rgb("entryC"), Rgb("glassColor")];
    }

    private static float[][]? QueryTintPalette(DataCoreDb db, DataCoreDb.Record record)
    {
        if (QueryTintFromPath(db, record, "Components[SGeometryResourceParams].Geometry.Geometry.Palette.RootRecord.root") is { } p) return p;
        var en = db.String2(record.NameOffset);
        var shortName = en[(en.LastIndexOf('.') + 1)..].ToLowerInvariant();
        if (db.StructIndexByName("TintPaletteTree") is not int tpt) return null;
        foreach (var r in db.Records)
        {
            if (r.StructIndex != tpt) continue;
            var n = db.String2(r.NameOffset).ToLowerInvariant();
            if (n[(n.LastIndexOf('.') + 1)..] == shortName) return QueryTintFromPath(db, r, "root");
        }
        return null;
    }

    // query_tint_from_record
    public static float[][]? TintFromRecord(DataCoreDb db, DataCoreDb.Record r) => QueryTintFromPath(db, r, "root");

    // resolve_paint_override
    private static (float[][]? Palette, SocpakGlb.MtlFile? Mtl) PaintOverride(Ctx ctx, DataCoreDb.Record entity, LoadoutNode root, float[][]? pal, SocpakGlb.MtlFile? mtl)
    {
        var db = ctx.Db;
        var paint = root.Children.FirstOrDefault(c => c.ItemPortName.ToLowerInvariant().Contains("paint", StringComparison.Ordinal));
        if (paint == null) return (pal, mtl);
        var tc = TryCompile(db, paint.Record.StructIndex, "Components[SAttachableComponentParams].AttachDef.Tags", DataCoreDb.StringLeaf);
        var tags = (tc == null ? null : TryString(db, tc, paint.Record)) ?? "";
        string? tag = null;
        foreach (var t in tags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) if (t.StartsWith('@')) { tag = t[1..]; break; }
        if (tag == null) return (pal, mtl);
        var cp = TryCompile(db, entity.StructIndex, "Components[SGeometryResourceParams]", DataCoreDb.AnyLeaf);
        if (cp == null) return (pal, mtl);
        var comps = TryValues(db, cp, entity, false) ?? new();
        foreach (var comp in comps)
        {
            if (DcValue.Field(comp, "Geometry") is not { } gn) continue;
            if (DcValue.ArrField(gn, "SubGeometry") is not { } arr) continue;
            foreach (var sub in arr)
            {
                var stag = DcValue.StrField(sub, "Tags") ?? "";
                if (!AsciiEqualsIgnoreCase(stag, tag)) continue;
                float[][]? p2 = null;
                var gd = DcValue.Field(sub, "Geometry");
                var pr = gd == null ? null : DcValue.Field(gd, "Palette");
                var rr = pr == null ? null : DcValue.Field(pr, "RootRecord");
                var rootv = rr == null ? null : DcValue.Field(rr, "root");
                if (rootv != null)
                {
                    float[] Read(string entry)
                    {
                        var e = DcValue.Field(rootv, entry);
                        var tint = e == null ? null : DcValue.Field(e, "tintColor");
                        byte r = (tint == null ? null : DcValue.U8Field(tint, "r")) ?? 128;
                        byte g = (tint == null ? null : DcValue.U8Field(tint, "g")) ?? 128;
                        byte b = (tint == null ? null : DcValue.U8Field(tint, "b")) ?? 128;
                        return [SrgbToLinear(r / 255.0f), SrgbToLinear(g / 255.0f), SrgbToLinear(b / 255.0f)];
                    }
                    p2 = [Read("entryA"), Read("entryB"), Read("entryC"), Read("glassColor")];
                }
                var mp = gd == null ? null : DcValue.Field(gd, "Material") is { } mm ? NonEmpty(DcValue.StrField(mm, "path")) : null;
                var m2 = mtl;
                if (mp != null) m2 = TryLoadMtl(ctx.P4k, DataCorePathToP4k(mp)) ?? mtl;
                return (p2 ?? pal, m2);
            }
        }
        return (pal, mtl);
    }

    private static bool AsciiEqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
        {
            char x = a[i], y = b[i];
            if (x is >= 'A' and <= 'Z') x = (char)(x + 32);
            if (y is >= 'A' and <= 'Z') y = (char)(y + 32);
            if (x != y) return false;
        }
        return true;
    }

    // query_landing_gear: (形のパス, 骨の名前)
    private static List<(string Path, string Bone)> LandingGear(DataCoreDb db, DataCoreDb.Record record)
    {
        var res = new List<(string, string)>();
        var cp = TryCompile(db, record.StructIndex, "Components[VehicleComponentParams].landingSystem", DataCoreDb.AnyLeaf);
        if (cp == null) return res;
        var vals = TryValues(db, cp, record, false);
        if (vals == null || vals.Count == 0) return res;
        if (vals[0] is not DcValue.Obj o) return res;
        // gears は名前が一致する最初の欄 (fields.iter().find)
        if (DcValue.Field(o, "gears") is not DcValue.Arr gears) return res;
        foreach (var gear in gears.Items)
        {
            if (gear is not DcValue.Obj go) continue;
            var gm = FieldMap(go);
            if (!gm.TryGetValue("bone", out var bv) || bv is not DcValue.Str bone) continue;
            string? path = null;
            if (gm.TryGetValue("geometry", out var gv) && gv is DcValue.Obj gobj)
            {
                var g1 = FieldMap(gobj);
                if (g1.TryGetValue("path", out var pv) && pv is DcValue.Str ps) path = ps.V;
                else if (g1.TryGetValue("Geometry", out var g2v) && g2v is DcValue.Obj g2o && FieldMap(g2o).TryGetValue("Geometry", out var g3v) && g3v is DcValue.Obj g3o
                    && FieldMap(g3o).TryGetValue("path", out var p3) && p3 is DcValue.Str p3s) path = p3s.V;
            }
            if (path != null && path.Length > 0) res.Add((path, bone.V));
        }
        return res;
    }

    // HashMap に入れたもの (同じ名前は後のもの)
    private static Dictionary<string, DcValue> FieldMap(DcValue.Obj o)
    {
        var d = new Dictionary<string, DcValue>(StringComparer.Ordinal);
        foreach (var (k, v) in o.Fields) d[k] = v;
        return d;
    }

    // ===== 船の XML (見えない取り付け口・Tread・Wheel) =====
    private static string? QueryVehicleString(DataCoreDb db, DataCoreDb.Record r, string field)
    {
        var cp = TryCompile(db, r.StructIndex, $"Components[VehicleComponentParams].{field}", DataCoreDb.StringLeaf);
        return cp == null ? null : TryString(db, cp, r);
    }

    public static HashSet<string> InvisiblePorts(Ctx ctx, DataCoreDb.Record record)
    {
        var inv = new HashSet<string>(StringComparer.Ordinal);
        var vd = QueryVehicleString(ctx.Db, record, "vehicleDefinition");
        if (string.IsNullOrEmpty(vd)) return inv;
        var mod = QueryVehicleString(ctx.Db, record, "modification") ?? "";
        if (ReadP4k(ctx.P4k, DataCorePathToP4k(vd)) is { } d && TryXml(d) is { } x) CollectInvisible(x, x.Root, inv, false);
        if (mod.Length > 0)
        {
            int slash = vd.LastIndexOf('/');
            if (slash < 0) slash = vd.LastIndexOf('\\');
            if (slash >= 0)
            {
                var dir = vd[..slash];
                var stem = TrimEndAll(TrimEndAll(vd[(slash + 1)..], ".xml"), ".XML");
                var mp = DataCorePathToP4k($"{dir}/Modifications/{stem}_{mod}.xml");
                if (ReadP4k(ctx.P4k, mp) is { } md && TryXml(md) is { } mx) CollectInvisible(mx, mx.Root, inv, true);
            }
        }
        return inv;
    }

    private static string TrimEndAll(string s, string suffix) { while (s.EndsWith(suffix, StringComparison.Ordinal)) s = s[..^suffix.Length]; return s; }
    private static CryXmlB? TryXml(byte[] d) { try { return CryXmlB.Parse(d); } catch (InvalidDataException) { return null; } }

    private static string? FirstAttr(CryXmlB x, int node, string key)
    {
        foreach (var (k, v) in x.Attributes(node)) if (k == key) return v;
        return null;
    }

    private static void CollectInvisible(CryXmlB x, int node, HashSet<string> inv, bool over)
    {
        if (x.Tag(node) == "Part")
        {
            var pn = FirstAttr(x, node, "name") ?? "";
            foreach (var c in x.Children(node))
            {
                if (x.Tag(c) != "ItemPort") continue;
                var flags = FirstAttr(x, c, "flags") ?? "";
                bool invis = flags.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("invisible");
                if (!over) { if (invis && pn.Length > 0) inv.Add(pn); }
                else if (pn.Length > 0) { if (invis) inv.Add(pn); else inv.Remove(pn); }
            }
        }
        foreach (var c in x.Children(node)) CollectInvisible(x, c, inv, over);
    }

    private sealed record VehiclePart(string Name, string GeometryPath, string MaterialPath, List<string> Children);

    private static List<VehiclePart> VehicleParts(Ctx ctx, DataCoreDb.Record record)
    {
        var res = new List<VehiclePart>();
        var vd = QueryVehicleString(ctx.Db, record, "vehicleDefinition");
        if (string.IsNullOrEmpty(vd)) return res;
        if (ReadP4k(ctx.P4k, DataCorePathToP4k(vd)) is not { } d || TryXml(d) is not { } x) return res;
        CollectParts(x, x.Root, res);
        return res;
    }

    private static void CollectParts(CryXmlB x, int node, List<VehiclePart> parts)
    {
        if (x.Tag(node) == "Part")
        {
            var am = x.AttributeMap(node);
            var cls = am.GetValueOrDefault("class", "");
            var pn = am.GetValueOrDefault("name", "");
            if (cls == "Tread")
            {
                foreach (var c in x.Children(node))
                {
                    if (x.Tag(c) != "Tread") continue;
                    var ta = x.AttributeMap(c);
                    var fn = ta.GetValueOrDefault("filename", "");
                    var mat = ta.GetValueOrDefault("materialName", "");
                    var wheels = new List<string>();
                    foreach (var tc in x.Children(c))
                        if (x.Tag(tc) == "Wheels")
                            foreach (var w in x.Children(tc))
                                if (x.Tag(w) == "Wheel" && FirstAttr(x, w, "partName") is { } wn) wheels.Add(wn);
                    if (fn.Length > 0 && pn.Length > 0) parts.Add(new VehiclePart(pn, fn, mat, wheels));
                }
            }
            else if (cls == "SubPartWheel")
            {
                foreach (var c in x.Children(node))
                {
                    if (x.Tag(c) != "SubPart") continue;
                    if (FirstAttr(x, c, "filename") is { } fn && fn.Length > 0 && pn.Length > 0) parts.Add(new VehiclePart(pn, fn, "", new()));
                }
            }
        }
        foreach (var c in x.Children(node)) CollectParts(x, c, parts);
    }

    // ===== 解決した木 (types.rs ResolvedNode) =====
    public sealed class ResolvedNode
    {
        public string EntityName = "", AttachmentName = "";
        public bool NoRotation;
        public float[] OffPos = new float[3], OffRot = new float[3];
        public List<ShipMeshes.NmcNode>? Nmc;
        public bool HasGeometry;
        public DataCoreDb.Record Record;
        public string? GeometryPath, MaterialPath;
        public List<ResolvedNode> Children = new();
    }

    private static bool HasGeom(P4kArchive p4k, string geometryPath)
    {
        var gp = DataCorePathToP4k(geometryPath);
        return geometryPath.ToLowerInvariant().EndsWith(".cdf", StringComparison.Ordinal) ? p4k.Find(gp) >= 0 : p4k.Find(CompanionPath(p4k, gp)) >= 0;
    }

    // resolve_loadout_meshes
    public static ResolvedNode ResolveLoadoutMeshes(Ctx ctx, DataCoreDb.Record record, LoadoutNode tree)
    {
        var db = ctx.Db; var p4k = ctx.P4k;
        DataCoreDb.CompiledPath gc;
        try { gc = db.CompilePath(record.StructIndex, "Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path", DataCoreDb.StringLeaf); }
        catch (DataCoreDb.QueryException e) { throw new ExportException($"DataCore query: {e.Message}"); }
        string geom;
        try { geom = db.QueryFirstString(gc, record) ?? throw new ExportException($"no geometry component: {db.String2(record.NameOffset)}"); }
        catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException) { throw new ExportException($"DataCore query: {e.Message}"); }
        var mc = TryCompile(db, record.StructIndex, "Components[SGeometryResourceParams].Geometry.Geometry.Material.path", DataCoreDb.StringLeaf);
        var mtl = (mc == null ? null : TryString(db, mc, record)) ?? "";
        var (nmc, _) = NmcAndMaterial(p4k, geom, mtl);
        ResolveGeometryFiles(p4k, geom);   // CDF が読めなければ失敗 (starbreaker と同じ)
        var hasGeom = HasGeom(p4k, geom);
        var invisible = InvisiblePorts(ctx, record);
        var children = ResolveChildren(ctx, tree.Children, invisible);
        var vparts = VehicleParts(ctx, record);
        if (vparts.Count > 0)
        {
            var existing = children.Select(c => c.AttachmentName.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var wheelLookup = new Dictionary<string, VehiclePart>(StringComparer.Ordinal);
            foreach (var p in vparts) if (p.Children.Count == 0) wheelLookup[p.Name.ToLowerInvariant()] = p;
            foreach (var part in vparts)
            {
                if (existing.Contains(part.Name.ToLowerInvariant())) continue;
                if (part.Children.Count == 0 && vparts.Any(p => p.Children.Any(c => AsciiEqualsIgnoreCase(c, part.Name)))) continue;
                var pc = new List<ResolvedNode>();
                foreach (var wn in part.Children)
                    if (wheelLookup.TryGetValue(wn.ToLowerInvariant(), out var w))
                        pc.Add(new ResolvedNode { EntityName = w.Name, AttachmentName = w.Name, HasGeometry = HasGeom(p4k, w.GeometryPath), Record = record, GeometryPath = w.GeometryPath, MaterialPath = w.MaterialPath.Length == 0 ? null : w.MaterialPath });
                children.Add(new ResolvedNode { EntityName = part.Name, AttachmentName = part.Name, HasGeometry = HasGeom(p4k, part.GeometryPath), Record = record, GeometryPath = part.GeometryPath, MaterialPath = part.MaterialPath.Length == 0 ? null : part.MaterialPath, Children = pc });
            }
        }
        return new ResolvedNode { EntityName = tree.EntityName, AttachmentName = "", Nmc = nmc, HasGeometry = hasGeom, Record = record, GeometryPath = geom, MaterialPath = mtl, Children = children };
    }

    private static List<ResolvedNode> ResolveChildren(Ctx ctx, List<LoadoutNode> nodes, HashSet<string> invisible)
    {
        var res = new ResolvedNode[nodes.Count];
        Parallel.For(0, nodes.Count, new ParallelOptions { CancellationToken = ctx.Ct }, i => res[i] = ResolveChild(ctx, nodes[i], invisible));
        return res.ToList();
    }

    private static ResolvedNode ResolveChild(Ctx ctx, LoadoutNode node, HashSet<string> invisible)
    {
        var att = node.HelperBoneName ?? node.ItemPortName;
        var rn = new ResolvedNode { EntityName = node.EntityName, AttachmentName = att, NoRotation = node.NoRotation, OffPos = node.OffPos, OffRot = node.OffRot, Record = node.Record, MaterialPath = node.MaterialPath };
        if (invisible.Contains(node.ItemPortName)) return rn;
        rn.Children = ResolveChildren(ctx, node.Children, invisible);
        if (node.GeometryPath is not string gp) return rn;
        (rn.Nmc, _) = NmcAndMaterial(ctx.P4k, gp, node.MaterialPath ?? "");
        rn.HasGeometry = HasGeom(ctx.P4k, gp);
        rn.GeometryPath = gp;
        return rn;
    }

    // ===== 子 (types.rs EntityPayload) =====
    public sealed class Payload
    {
        public ShipMeshes.Mesh Mesh = null!;
        public SocpakGlb.MtlFile? Mtl;
        public List<ShipMeshes.NmcNode>? Nmc;
        public float[][]? Palette;
        public List<CryIvo.Bone> Bones = new();
        public string EntityName = "", ParentNodeName = "", ParentEntityName = "";
        public bool NoRotation;
        public float[] OffPos = new float[3], OffRot = new float[3];
    }

    private static Loaded? LoadChildMesh(Ctx ctx, ResolvedNode child)
    {
        try
        {
            if (child.GeometryPath != null)
            {
                try { return FromPaths(ctx, child.GeometryPath, child.MaterialPath ?? ""); }
                catch (Exception e) when (IsLoadError(e)) { ctx.Log?.Invoke($"  {child.EntityName} -> load from paths failed: {e.Message}"); }
            }
            var l = EntityPayload(ctx, child.Record, false, true);
            l.Tex = null;
            return l;
        }
        catch (Exception e) when (IsLoadError(e)) { return null; }
    }

    private static void Flatten(Ctx ctx, List<ResolvedNode> children, string parentEntity, (string Name, bool NoRot)? over, List<Payload> outList)
    {
        foreach (var child in children)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var (attach, noRot) = over is { } o ? (o.Name, child.NoRotation || o.NoRot) : (child.AttachmentName, child.NoRotation);
            bool creates;
            if (child.HasGeometry)
            {
                if (LoadChildMesh(ctx, child) is { } l)
                {
                    outList.Add(new Payload { Mesh = l.Mesh, Mtl = l.Mtl, Nmc = l.Nmc, Palette = l.Palette, Bones = l.Bones, EntityName = child.EntityName, ParentNodeName = attach, ParentEntityName = parentEntity, NoRotation = noRot, OffPos = child.OffPos, OffRot = child.OffRot });
                    creates = true;
                }
                else creates = false;
            }
            else if (child.Nmc != null)
            {
                outList.Add(new Payload { Mesh = ShipMeshes.Mesh.Empty(), Nmc = child.Nmc, EntityName = child.EntityName, ParentNodeName = attach, ParentEntityName = parentEntity, NoRotation = noRot, OffPos = child.OffPos, OffRot = child.OffRot });
                creates = true;
            }
            else creates = false;
            if (creates) Flatten(ctx, child.Children, child.EntityName, null, outList);
            else Flatten(ctx, child.Children, parentEntity, (child.AttachmentName, child.NoRotation), outList);
        }
    }

    // ===== 書き出し (entity.rs export → assemble_glb_with_loadout) =====
    public sealed class Result
    {
        public long GlbBytes;
        public string ResolvedName = "", GeometryPath = "", MaterialPath = "";
        public int Candidates;
    }

    // outPath に GLB を書く (失敗したときの書きかけのファイルは呼ぶ側で消す)
    public static Result Export(P4kArchive p4k, DataCoreDb db, string name, string outPath, Action<string>? log = null, CancellationToken ct = default)
    {
        var ctx = new Ctx { P4k = p4k, Db = db, Log = log, Ct = ct };
        var record = FindRecord(db, name, out int cands) ?? throw new ExportException($"no EntityClassDefinition records matching '{name}'");
        var rname = db.String2(record.NameOffset);
        var idx = IndexOf(db);
        var tree = ResolveLoadout(idx, record);
        log?.Invoke($"Loadout tree for {tree.EntityName}: {tree.Children.Count} children");
        ct.ThrowIfCancellationRequested();

        var resolved = ResolveLoadoutMeshes(ctx, record, tree);
        var root = EntityPayload(ctx, record, true, true);
        var (rootPal, rootMtl) = PaintOverride(ctx, record, tree, root.Palette, root.Mtl);
        ct.ThrowIfCancellationRequested();

        var payloads = new List<Payload>();
        foreach (var (gp, bone) in LandingGear(db, record))
        {
            try
            {
                var g = FromPaths(ctx, gp, "");
                payloads.Add(new Payload { Mesh = g.Mesh, Mtl = g.Mtl, Nmc = g.Nmc, Palette = rootPal, EntityName = gp[(gp.LastIndexOf('/') + 1)..], ParentNodeName = bone, ParentEntityName = resolved.EntityName });
            }
            catch (Exception e) when (IsLoadError(e)) { log?.Invoke($"  landing gear '{gp}' failed: {e.Message}"); }
        }
        Flatten(ctx, resolved.Children, resolved.EntityName, null, payloads);
        ct.ThrowIfCancellationRequested();

        var interiors = ShipInteriors.Load(ctx, record);
        log?.Invoke($"Assembling: root + {payloads.Count} child meshes + {interiors.Containers.Sum(c => c.Placements.Count)} interior meshes ({interiors.Cgfs.Count} unique CGFs)");

        long len;
        using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            ShipGlb.Write(ctx, new ShipGlb.Input
            {
            RootMesh = root.Mesh, RootMtl = rootMtl, RootTex = root.Tex, RootNmc = resolved.Nmc, RootPalette = rootPal, RootBones = root.Bones,
                Children = payloads, Interiors = interiors, EntityName = resolved.EntityName, GeometryPath = root.PrimaryPath, MaterialPath = root.MaterialPath,
            }, fs);
            len = fs.Length;
        }
        return new Result { GlbBytes = len, ResolvedName = rname, GeometryPath = root.PrimaryPath, MaterialPath = root.MaterialPath, Candidates = cands };
    }
}
