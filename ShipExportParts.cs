namespace StarCitizenJapaneseTextCreater;

// 船の 3D の自前の読み取りの部品 (2026-11-04)。ShipExport.cs の説明を参照。
// テクスチャ: pipeline.rs load_material_textures・textures_share_uv_space・cached_load・load_diffuse_texture・load_normal_texture・load_roughness_texture
//   (PNG は image 0.25.10 の write_to と同じバイト (RustPng))。
// 船内: socpak.rs (query_object_containers・build_container_transform・load_interior_from_socpak・parse_soc・灯り) と
//   pipeline.rs (load_interiors・build_interiors_from_payloads・resolve_guid_geometry)。
internal static class ShipTextures
{
    public static ShipGlb.Textures Load(ShipExport.Ctx ctx, SocpakGlb.MtlFile mtl)
    {
        var p4k = ctx.P4k;
        int mip = ShipExport.Mip;
        var diffuse = new List<ShipGlb.PngBox?>();
        foreach (var m in mtl.Materials)
        {
            ShipGlb.PngBox? d = null;
            bool done = false;
            if (m.DiffuseTex is string path && Cached(ctx, path, mip, Diffuse) is { } png) { d = png; done = true; }
            if (!done && m.Layers.Count > 0)
            {
                var lp = ShipExport.DataCorePathToP4k(m.Layers[0].Path);
                if (ShipExport.TryLoadMtl(p4k, lp) is { } lm && lm.Materials.Count > 0 && lm.Materials[0].DiffuseTex is string tp)
                    d = Cached(ctx, tp, mip, Diffuse);
            }
            diffuse.Add(d);
        }
        var normalPaths = mtl.Materials.Select(m =>
        {
            if (m.NormalTex is string p) return p;
            if (m.Layers.Count == 0) return null;
            var lp = ShipExport.DataCorePathToP4k(m.Layers[0].Path);
            return ShipExport.TryLoadMtl(p4k, lp) is { } lm && lm.Materials.Count > 0 ? lm.Materials[0].NormalTex : null;
        }).ToList();
        var normal = new List<ShipGlb.PngBox?>();
        var rough = new List<ShipGlb.PngBox?>();
        for (int i = 0; i < mtl.Materials.Count; i++)
        {
            var m = mtl.Materials[i];
            var np = normalPaths[i];
            ShipGlb.PngBox? n = null;
            if (np != null && !(m.DiffuseTex is string dt && !ShareUv(dt, np))) n = Cached(ctx, np, mip, Normal);
            normal.Add(n);
        }
        for (int i = 0; i < mtl.Materials.Count; i++)
        {
            var m = mtl.Materials[i];
            var np = normalPaths[i];
            ShipGlb.PngBox? r = null;
            if (np != null && np.Contains("_ddna", StringComparison.Ordinal) && !(m.DiffuseTex is string dt && !ShareUv(dt, np)))
            {
                var key = $"{np}@roughness_mip{mip}";
                lock (ctx.PngCache)
                    if (ctx.PngCache.TryGetValue(key, out var c)) { rough.Add(c); continue; }
                r = Roughness(p4k, np, mip) is { } rb ? new ShipGlb.PngBox { Data = rb } : null;
                lock (ctx.PngCache) ctx.PngCache[key] = r;
            }
            rough.Add(r);
        }
        return new ShipGlb.Textures(diffuse, normal, rough);
    }

    // textures_share_uv_space
    private static bool ShareUv(string diffusePath, string normalPath)
    {
        static string Stem(string p)
        {
            var f = p[(p.LastIndexOfAny(['/', '\\']) + 1)..];
            int dot = f.IndexOf('.');
            var b = dot >= 0 ? f[..dot] : f;
            if (b.EndsWith("_diff", StringComparison.Ordinal)) b = b[..^5];
            if (b.EndsWith("_ddna", StringComparison.Ordinal)) b = b[..^5];
            if (b.EndsWith("_ddn", StringComparison.Ordinal)) b = b[..^4];
            if (b.EndsWith("_spec", StringComparison.Ordinal)) b = b[..^5];
            return b;
        }
        var d = Stem(diffusePath); var n = Stem(normalPath);
        return d == n || d.StartsWith(n, StringComparison.Ordinal) || n.StartsWith(d, StringComparison.Ordinal);
    }

    private static ShipGlb.PngBox? Cached(ShipExport.Ctx ctx, string path, int mip, Func<P4kArchive, string, int, byte[]?> loader)
    {
        var key = $"{path}@mip{mip}";
        lock (ctx.PngCache)
            if (ctx.PngCache.TryGetValue(key, out var c)) return c;
        var r = loader(ctx.P4k, path, mip) is { } png ? new ShipGlb.PngBox { Data = png } : null;
        lock (ctx.PngCache) ctx.PngCache[key] = r;
        return r;
    }

    private static string DdsPath(string tif) => tif.EndsWith(".tif", StringComparison.Ordinal) ? tif[..^4] + ".dds" : tif;

    private static DdsImage? LoadDds(P4kArchive p4k, string tifPath)
    {
        var path = ShipExport.DataCorePathToP4k(DdsPath(tifPath));
        int e = p4k.Find(path);
        if (e < 0) return null;
        try
        {
            var data = p4k.Read(e);
            return DdsImage.FromSplit(data, suf =>
            {
                int j = p4k.Find(path + suf);
                if (j < 0) return null;
                try { return p4k.Read(j); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or ZstdSharp.ZstdException) { return null; }
            });
        }
        catch (Exception ex) when (ShipExport.IsLoadError(ex)) { return null; }
    }

    private static byte[]? Png(int w, int h, byte[] rgba)
    {
        if (w <= 0 || h <= 0 || rgba.LongLength < (long)w * h * 4) return null;
        return RustPng.EncodeRgba8(w, h, rgba);
    }

    private static byte[]? Rgba(DdsImage dds, int mipLevel)
    {
        int mip = Math.Min(mipLevel, Math.Max(0, dds.Mips.Count - 1));
        var (w, h) = dds.Dimensions(mip);
        byte[] rgba;
        try { rgba = dds.DecodeRgba(mip); }
        catch (Exception ex) when (ShipExport.IsLoadError(ex)) { return null; }
        return Png(w, h, rgba);
    }

    // load_diffuse_texture
    private static byte[]? Diffuse(P4kArchive p4k, string tifPath, int mip)
    {
        if (tifPath.StartsWith('$')) return null;
        return LoadDds(p4k, tifPath) is { } d ? Rgba(d, mip) : null;
    }

    // load_normal_texture
    private static byte[]? Normal(P4kArchive p4k, string tifPath, int mip)
    {
        if (tifPath.StartsWith('$')) return null;
        var low = tifPath.ToLowerInvariant();
        if (!low.Contains("_ddna", StringComparison.Ordinal) && !low.Contains("_ddn.", StringComparison.Ordinal) && !low.Contains("_ddn_", StringComparison.Ordinal)) return null;
        return LoadDds(p4k, tifPath) is { } d ? Rgba(d, mip) : null;
    }

    // load_roughness_texture
    private static byte[]? Roughness(P4kArchive p4k, string tifPath, int mipLevel)
    {
        if (LoadDds(p4k, tifPath) is not { } dds) return null;
        if (dds.AlphaMips.Count == 0) return null;
        int mip = Math.Min(mipLevel, Math.Max(0, dds.AlphaMips.Count - 1));
        var (w, h) = dds.Dimensions(mip);
        byte[] sm;
        try { sm = dds.DecodeAlphaMip(mip); }
        catch (Exception ex) when (ShipExport.IsLoadError(ex)) { return null; }
        long n = (long)w * h;
        if (sm.LongLength != n) return null;
        var rgba = new byte[n * 4];
        for (long i = 0; i < n; i++) { rgba[i * 4 + 1] = (byte)(255 - sm[i]); rgba[i * 4 + 3] = 255; }
        return Png(w, h, rgba);
    }
}

internal static class ShipInteriors
{
    public sealed record Cgf(string Path, string? Mtl, string Name);
    public sealed record Light(string Name, double[] Pos, double[] Rot, float[] Color, float Intensity, float Radius, float? Inner, float? Outer);
    public sealed class Container
    {
        public string Name = "";
        public float[] Transform = new float[16];   // 列優先 (flatten_4x4 の並び)
        public List<(int Cgf, float[] T)> Placements = new();
        public List<Light> Lights = new();
        public float[][]? Palette;
    }
    public sealed class Loaded { public List<Cgf> Cgfs = new(); public List<Container> Containers = new(); }

    private sealed record Mesh(string CgfPath, string? Mtl, float[] T, string? Guid);

    // load_interiors (灯りあり)
    // carriages: 区画の中の TransitManager (エレベーター・リフトの管理) の carriageInterior が指すかごの区画も、かごの置かれる所に足す
    //   (2026-11-05、利用者の報告「床がない。船舶は表示しない部品があったらダメだよね」: かごの区画は船の区画の一覧 objectContainers に無く、台 (かごの床) が出ていなかった)。
    //   足す区画は一覧の区画の後ろに並べる (一覧の区画の並び・中身は変えない)。船の構成の書き出し (ShipExport.DumpHierarchy。照合のハッシュ) は false で今までどおり
    // carriagesOnly: 一覧の区画そのものは入れず、かごの区画だけを返す (前の版で取り込んだ船に足す区画だけの GLB 用。ShipExport.ExportAddon)
    public static Loaded Load(ShipExport.Ctx ctx, DataCoreDb.Record record, bool carriages = false, bool carriagesOnly = false)
    {
        var res = new Loaded();
        var db = ctx.Db;
        var cp = ShipExport.TryCompile(db, record.StructIndex, "Components[VehicleComponentParams].objectContainers", DataCoreDb.AnyLeaf);
        if (cp == null) return res;
        var vals = ShipExport.TryValues(db, cp, record, false);
        if (vals == null) return res;
        var refs = new List<(string File, float[] Pos, float[] Rot)>();
        foreach (var v in vals) if (ContainerRef(v) is { } r) refs.Add(r);
        if (refs.Count == 0) return res;
        var payloads = new List<(string Name, List<Mesh> Meshes, List<Light> Lights, float[] T, List<string> Pal)>();
        foreach (var (file, pos, rot) in refs)
        {
            if (carriagesOnly) break;
            ctx.Ct.ThrowIfCancellationRequested();
            var t = ContainerTransform(pos, rot);
            try { var (n, ms, ls, pal) = LoadSocpak(ctx.P4k, file); payloads.Add((n, ms, ls, t, pal)); }
            catch (Exception e) when (ShipExport.IsLoadError(e) || e is SocpakExporter.SocpakException) { ctx.Log?.Invoke($"failed to load {file}: {e.Message}"); }
        }
        if (carriages)
            foreach (var (file, pos, rot) in refs)
            {
                var t = ContainerTransform(pos, rot);
                foreach (var (cfile, ct) in CarriageRefs(ctx.P4k, file, ctx.Log))
                {
                    ctx.Ct.ThrowIfCancellationRequested();
                    try { var (n, ms, ls, pal) = LoadSocpak(ctx.P4k, cfile); payloads.Add((n, ms, ls, Glam.Mul(t, ct), pal)); }
                    catch (Exception e) when (ShipExport.IsLoadError(e) || e is SocpakExporter.SocpakException) { ctx.Log?.Invoke($"failed to load {cfile}: {e.Message}"); }
                }
            }
        // build_interiors_from_payloads
        var gGeom = TryRooted(db, "EntityClassDefinition.Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path");
        var gMtl = TryRooted(db, "EntityClassDefinition.Components[SGeometryResourceParams].Geometry.Geometry.Material.path");
        var cache = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, meshes, lights, t, pal) in payloads)
        {
            var c = new Container { Name = name, Transform = t, Lights = lights };
            foreach (var im in meshes)
            {
                string cgf; string? mtl;
                if (im.CgfPath.Length > 0) { cgf = im.CgfPath; mtl = im.Mtl; }
                else if (im.Guid != null)
                {
                    if (ResolveGuid(db, im.Guid, gGeom, gMtl) is not { } g) continue;
                    cgf = g.Geom; mtl = g.Mtl.Length > 0 ? g.Mtl : null;
                }
                else continue;
                if (!cache.TryGetValue(cgf, out var ix))
                {
                    ix = res.Cgfs.Count; cache[cgf] = ix;
                    var last = cgf[(cgf.LastIndexOf('/') + 1)..];
                    res.Cgfs.Add(new Cgf(cgf, mtl ?? im.Mtl, last.EndsWith(".cgf", StringComparison.Ordinal) ? last[..^4] : cgf));
                }
                c.Placements.Add((ix, im.T));
            }
            if (pal.Count > 0) c.Palette = PaletteByName(db, pal[0]);
            res.Containers.Add(c);
        }
        return res;
    }

    // record の objectContainers に区画が 1 つでもあるか (子の乗り物が自分の区画を持つかを手早く見る)
    internal static bool HasContainers(DataCoreDb db, DataCoreDb.Record record)
    {
        var cp = ShipExport.TryCompile(db, record.StructIndex, "Components[VehicleComponentParams].objectContainers", DataCoreDb.AnyLeaf);
        if (cp == null) return false;
        var vals = ShipExport.TryValues(db, cp, record, true);
        if (vals == null) return false;
        foreach (var v in vals) if (ShipLadders.ContainerRef(v) != null) return true;
        return false;
    }

    // 区画 (socpak) の中の TransitManager が指すかごの区画 (carriageInterior の path) と、かごを置く所 (区画の中の位置と向き。列優先 16)。
    //   実データで確かめた欄 (2026-11-05、DRAK_Ironclad の hold.socpak の TransitManager_Cargo_Lift_Left):
    //     <Entity EntityClass="TransitManager" Pos="-8.59375,37.7188,-3" …>
    //       <PropertiesDataCore><SCTransitManager …><carriageInterior path="ObjectContainers/Ships/DRAK/ironclad/cargo_lift.socpak">
    //       <SCTransitManager><TransitDestinations>
    //           <Destination nameIdentifier="@vehicle_deck_upper_cargo" destinationPos="-0.65625,-0.0313,2" destinationQuat="1,0,0,0"><Gateway …>
    //           <Destination nameIdentifier="@vehicle_deck_cargo" destinationPos="-0.65625,-0.0313,-3.1875" destinationQuat="1,0,0,0"><Gateway …>
    //         <CarriageSpawnLocations><SpawnLocation splineIndex="0" splinePointIndex="0" gatewayIndex="1">
    //   かごを置く階: CarriageSpawnLocations の最初の SpawnLocation の gatewayIndex (TransitDestinations の中の Gateway の通し番号) の Gateway を持つ行き先。
    //     その欄が無い・番号が外なら、行き先の並びの最初 (親の決定)。
    //   置く所: 管理のエンティティの Pos・Rotate に、その行き先の destinationPos・destinationQuat を重ねたもの (Ironclad の左のリフトで、管理 (−8.59, 37.72, −3) +
    //     (−0.66, −0.03, −3.19) = 乗り場のエンティティ TransitDestination_Ground_Floor_Left の Pos (−9.25, 37.69, −6.19) と同じになることを確かめた)
    internal static List<(string File, float[] T)> CarriageRefs(P4kArchive p4k, string socpakPath, Action<string>? log = null)
    {
        var res = new List<(string, float[])>();
        try
        {
            ShipLadders.ForEachEntity(p4k, socpakPath, (x, ent, attrs) =>
            {
                if (attrs.GetValueOrDefault("EntityClass", "") != "TransitManager") return;
                string? path = null; int? spawnGateway = null;
                var dests = new List<(double[] Pos, double[] Quat)>(); var gatewayDest = new List<int>();
                foreach (var c in x.Children(ent))
                {
                    var tag = x.Tag(c);
                    if (tag == "PropertiesDataCore")
                    {
                        foreach (var pc in x.Children(c))
                            if (x.Tag(pc) == "SCTransitManager")
                                foreach (var ci in x.Children(pc))
                                    if (x.Tag(ci) == "carriageInterior" && x.AttributeMap(ci).TryGetValue("path", out var cp) && cp.Length > 0) path = cp;
                    }
                    else if (tag == "SCTransitManager")
                    {
                        foreach (var s in x.Children(c))
                        {
                            var st = x.Tag(s);
                            if (st == "TransitDestinations")
                            {
                                foreach (var d in x.Children(s))
                                {
                                    if (x.Tag(d) != "Destination") continue;
                                    var da = x.AttributeMap(d);
                                    dests.Add((ShipLadders.Csv(da.GetValueOrDefault("destinationPos", "0,0,0"), [0, 0, 0]), ShipLadders.Csv(da.GetValueOrDefault("destinationQuat", "1,0,0,0"), [1, 0, 0, 0])));
                                    foreach (var gw in x.Children(d)) if (x.Tag(gw) == "Gateway") gatewayDest.Add(dests.Count - 1);
                                }
                            }
                            else if (st == "CarriageSpawnLocations" && spawnGateway == null)
                            {
                                foreach (var sl in x.Children(s))
                                    if (x.Tag(sl) == "SpawnLocation" && x.AttributeMap(sl).TryGetValue("gatewayIndex", out var gi) && int.TryParse(gi, out var g)) { spawnGateway = g; break; }
                            }
                        }
                    }
                }
                if (string.IsNullOrEmpty(path) || dests.Count == 0) return;
                int di = spawnGateway is int sg && sg >= 0 && sg < gatewayDest.Count ? gatewayDest[sg] : 0;
                var mp = ShipLadders.Csv(attrs.GetValueOrDefault("Pos", "0,0,0"), [0, 0, 0]);
                var mr = ShipLadders.Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"), [1, 0, 0, 0]);
                var (dp, dq) = dests[di];
                // 向きは w,x,y,z (Entities の Rotate と同じ読み方)
                var mt = Glam.FromScaleRotationTranslation(1, 1, 1, (float)mr[1], (float)mr[2], (float)mr[3], (float)mr[0], (float)mp[0], (float)mp[1], (float)mp[2]);
                var dt = Glam.FromScaleRotationTranslation(1, 1, 1, (float)dq[1], (float)dq[2], (float)dq[3], (float)dq[0], (float)dp[0], (float)dp[1], (float)dp[2]);
                res.Add((path, Glam.Mul(mt, dt)));
            });
        }
        catch (Exception e) when (ShipExport.IsLoadError(e)) { log?.Invoke($"failed to read transit managers of {socpakPath}: {e.Message}"); }
        return res;
    }

    private static DataCoreDb.CompiledPath? TryRooted(DataCoreDb db, string path)
    {
        try { return db.CompileRooted(path, DataCoreDb.StringLeaf); }
        catch (DataCoreDb.QueryException) { return null; }
    }

    private static float[][]? PaletteByName(DataCoreDb db, string paletteName)
    {
        var shortName = paletteName[(paletteName.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (db.StructIndexByName("TintPaletteTree") is not int tpt) return null;
        foreach (var r in db.Records)
            if (r.StructIndex == tpt && db.String2(r.NameOffset).ToLowerInvariant().EndsWith(shortName, StringComparison.Ordinal))
                return ShipExportPalette.FromRecord(db, r);
        return null;
    }

    // resolve_guid_geometry
    private static (string Geom, string Mtl)? ResolveGuid(DataCoreDb db, string guid, DataCoreDb.CompiledPath? gGeom, DataCoreDb.CompiledPath? gMtl)
    {
        if (DataCoreDb.Guid128.Parse(guid) is not { } id) return null;
        if (db.RecordById(id) is not DataCoreDb.Record rec) return null;
        string? Q(DataCoreDb.CompiledPath? rooted, string path)
        {
            if (rooted != null && ShipExport.TryString(db, rooted, rec) is string s) return s;
            var cp = ShipExport.TryCompile(db, rec.StructIndex, path, DataCoreDb.StringLeaf);
            return cp == null ? null : ShipExport.TryString(db, cp, rec);
        }
        var geom = Q(gGeom, "Components[SGeometryResourceParams].Geometry.Geometry.Geometry.path");
        if (geom == null || geom.Length == 0) return null;
        return (geom, Q(gMtl, "Components[SGeometryResourceParams].Geometry.Geometry.Material.path") ?? "");
    }

    // parse_container_ref・extract_offset
    private static (string, float[], float[])? ContainerRef(DcValue v)
    {
        if (v is not DcValue.Obj o) return null;
        var f = Map(o);
        if (!f.TryGetValue("fileName", out var fn) || fn is not DcValue.Str s) return null;
        float[] pos = [0, 0, 0], rot = [0, 0, 0];
        if (f.TryGetValue("Offset", out var off) && off is DcValue.Obj oo)
        {
            var om = Map(oo);
            if (om.TryGetValue("Position", out var p) && p is DcValue.Obj po)
            {
                var pm = Map(po);
                if (pm.TryGetValue("x", out var x) && x is DcValue.Float fx) pos[0] = fx.V;
                if (pm.TryGetValue("y", out var y) && y is DcValue.Float fy) pos[1] = fy.V;
                if (pm.TryGetValue("z", out var z) && z is DcValue.Float fz) pos[2] = fz.V;
            }
            if (om.TryGetValue("Rotation", out var r) && r is DcValue.Obj ro)
            {
                var rm = Map(ro);
                if (rm.TryGetValue("x", out var x) && x is DcValue.Float fx) rot[0] = fx.V;
                if (rm.TryGetValue("y", out var y) && y is DcValue.Float fy) rot[1] = fy.V;
                if (rm.TryGetValue("z", out var z) && z is DcValue.Float fz) rot[2] = fz.V;
            }
        }
        return (s.V, pos, rot);
    }

    private static Dictionary<string, DcValue> Map(DcValue.Obj o)
    {
        var d = new Dictionary<string, DcValue>(StringComparer.Ordinal);
        foreach (var (k, v) in o.Fields) d[k] = v;
        return d;
    }

    private const float RadsPerDeg = MathF.PI / 180.0f;
    public static float ToRadians(float deg) => deg * RadsPerDeg;

    // build_container_transform (flatten_4x4 の並び: 行 0〜3 をそのまま)
    private static float[] ContainerTransform(float[] pos, float[] rotDeg)
    {
        float px = ToRadians(rotDeg[0]), py = ToRadians(rotDeg[1]), pz = ToRadians(rotDeg[2]);
        float sx = MathF.Sin(px), cx = MathF.Cos(px), sy = MathF.Sin(py), cy = MathF.Cos(py), sz = MathF.Sin(pz), cz = MathF.Cos(pz);
        return [cy * cz, cy * sz, -sy, 0, sx * sy * cz - cx * sz, sx * sy * sz + cx * cz, sx * cy, 0, cx * sy * cz + sx * sz, cx * sy * sz - sx * cz, cx * cy, 0, pos[0], pos[1], pos[2], 1];
    }

    // load_interior_from_socpak (灯りあり)
    private static (string Name, List<Mesh> Meshes, List<Light> Lights, List<string> Palettes) LoadSocpak(P4kArchive p4k, string socpakPath)
    {
        var norm = socpakPath.Replace('/', '\\');
        var p4kPath = norm.ToLowerInvariant().StartsWith(@"data\", StringComparison.Ordinal) ? norm : @"Data\" + norm;
        int e = p4k.Find(p4kPath);
        if (e < 0) throw new SocpakExporter.SocpakException($"socpak not found: {p4kPath}");
        var inner = P4kArchive.FromBytes(p4k.Read(e));
        var last = socpakPath[(socpakPath.LastIndexOfAny(['/', '\\']) + 1)..];
        var name = last.EndsWith(".socpak", StringComparison.Ordinal) ? last[..^7] : socpakPath;
        var socs = new List<int>();
        for (int i = 0; i < inner.Count; i++) if (inner.Name(i).ToLowerInvariant().EndsWith(".soc", StringComparison.Ordinal)) socs.Add(i);
        if (socs.Count == 0) throw new SocpakExporter.SocpakException($"No .soc in {p4kPath}");
        var meshes = new List<Mesh>(); var lights = new List<Light>(); var pals = new List<string>();
        foreach (var si in socs)
        {
            byte[] soc;
            try { soc = inner.Read(si); }
            catch (Exception ex) when (ShipExport.IsLoadError(ex)) { continue; }
            try
            {
                var pn = new List<string>();
                ParseSoc(soc, meshes, lights, pn);
                if (pals.Count == 0) pals.AddRange(pn);
            }
            catch (InvalidDataException) { }
        }
        return (name, meshes, lights, pals);
    }

    // parse_soc (読めないチャンクは飛ばす。ファイルが CrCh でなければ失敗)
    private static void ParseSoc(byte[] data, List<Mesh> meshesOut, List<Light> lightsOut, List<string> palOut)
    {
        var cf = CryChunkFile.Parse(data);
        if (cf.IsIvo) throw new InvalidDataException("expected CrCh, got IVO");
        var meshes = new List<Mesh>(); var lights = new List<Light>(); var pals = new List<string>();
        foreach (var c in cf.Chunks)
        {
            var d = cf.ChunkData(c);
            if (c.Type == CryChunkFile.CrChIncludedObjects)
            {
                List<Mesh>? io = null; var pl = new List<string>();
                try { io = IncludedObjects(d.ToArray(), pl); } catch (InvalidDataException) { }
                if (io != null) { meshes.AddRange(io); if (pals.Count == 0) pals.AddRange(pl); }
            }
            else if (c.Type == CryChunkFile.CrChCryXmlB)
            {
                CryXmlB? x = null;
                try { x = CryXmlB.Parse(d.ToArray()); } catch (InvalidDataException) { }
                if (x != null) Entities(x, meshes, lights);
            }
        }
        meshesOut.AddRange(meshes); lightsOut.AddRange(lights); palOut.AddRange(pals);
    }

    // IncludedObjects::from_bytes と included_objects_to_meshes (SocpakScene と同じ読み方)
    private static List<Mesh> IncludedObjects(byte[] data, List<string> paletteNames)
    {
        int off = 4;
        uint U32() { if (off + 4 > data.Length) throw new InvalidDataException("IncludedObjects が切れています"); var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off)); off += 4; return v; }
        ushort U16() { if (off + 2 > data.Length) throw new InvalidDataException("IncludedObjects が切れています"); var v = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off)); off += 2; return v; }
        string Str()
        {
            if (off + 256 > data.Length) throw new InvalidDataException("IncludedObjects が切れています");
            var b = data.AsSpan(off, 256);
            int z = b.IndexOf((byte)0);
            off += 256;
            return System.Text.Encoding.UTF8.GetString(z < 0 ? b : b[..z]);
        }
        int numCgf = (int)U32();
        var cgfPaths = new List<string>();
        for (int i = 0; i < numCgf; i++) cgfPaths.Add(Str());
        int numMat = U16(), numPal = U16();
        var matPaths = new List<string>();
        for (int i = 0; i < numMat; i++) matPaths.Add(Str());
        for (int i = 0; i < numPal; i++) paletteNames.Add(Str());
        if (off + 28 > data.Length) throw new InvalidDataException("IncludedObjects が切れています");
        off += 28;
        long objEnd = off + (long)U32();
        var res = new List<Mesh>();
        var mtl = matPaths.Count > 0 ? matPaths[0] : null;
        double F64(int at) { if (at + 8 > data.Length) throw new InvalidDataException("IncludedObjects が切れています"); return System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(at)); }
        while (off + 4 <= objEnd)
        {
            uint type = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            if (type == 1)
            {
                if (off + 168 > data.Length) break;
                int id = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(off + 60));
                var t = new float[16];
                int mo = off + 64;
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 4; col++)
                        t[col * 4 + row] = (float)F64(mo + (row * 4 + col) * 8);
                t[15] = 1;
                ulong u3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 160));
                int size = u3 == 0 ? 184 : 168;
                long end = off + size;
                while (end + 4 <= objEnd && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)end)) == 0) end += 4;
                if (id < cgfPaths.Count) res.Add(new Mesh(cgfPaths[id], mtl, t, null));
                off = (int)end;
            }
            else if (type == 7) off += 152;
            else if (type == 0x10) off += 136;
            else off += 4;
            if (off > objEnd) break;
        }
        return res;
    }

    private static readonly HashSet<string> SkipClasses = new(StringComparer.Ordinal)
    {
        "ActionArea", "AudioAreaAmbience", "AudioEnvironmentFeedbackPoint", "AudioTriggerSpot", "AreaShape", "CameraSource", "ColorGradient", "CommentEntity",
        "EditorCamera", "EnvironmentLight", "FlographEntity", "FogVolume", "GravityArea", "GravityBox", "GreenZone", "Hazard", "Hint", "Ladder", "LandingArea",
        "LedgeObject", "LocationManager", "MusicArea", "NavigationArea", "ParticleEffect", "ParticleField", "PlanetAreaEntity", "ProceduralPointOfInterestProxy",
        "Room", "RoomConnector", "SafeTeleportPoint", "SCShop", "SequenceObjectItem", "ShadowRegionEntity", "SurfaceRaindropsTarget", "TagPoint",
        "TransitDestination", "TransitGateway", "TransitManager", "TransitNavSpline", "VibrationAudioPoint", "VehicleAudioPoint",
    };

    // extract_cryxml_entities・process_entity_children
    private static void Entities(CryXmlB x, List<Mesh> meshes, List<Light> lights)
    {
        int root = x.Root;
        var rootTag = x.Tag(root);
        int container = -1;
        foreach (var c in x.Children(root)) { var t = x.Tag(c); if (t == "Entities" || t == "SCOC_Entities") { container = c; break; } }
        if (container < 0 && (rootTag == "Entities" || rootTag == "SCOC_Entities")) container = root;
        if (container < 0) return;
        foreach (var ent in x.Children(container))
        {
            if (x.Tag(ent) != "Entity") continue;
            var attrs = x.AttributeMap(ent);
            var cls = attrs.GetValueOrDefault("EntityClass", "");
            if (SkipClasses.Contains(cls) || cls.StartsWith("Door_Ship_Sensor", StringComparison.Ordinal) || cls.StartsWith("ChipSet_Light", StringComparison.Ordinal)) continue;
            var pos = Csv(attrs.GetValueOrDefault("Pos", "0,0,0"));
            var rot = Csv(attrs.GetValueOrDefault("Rotate", "1,0,0,0"));
            var scl = Csv(attrs.GetValueOrDefault("Scale", "1,1,1"));
            if (cls is "Light" or "LightBox" or "LightGroup" or "LightGroupPoweredItem")
            {
                lights.AddRange(LightEntities(x, ent, attrs, pos, rot, cls));
                continue;
            }
            double G(List<double> l, int i, double d) => i < l.Count ? l[i] : d;
            var t = Glam.FromScaleRotationTranslation((float)G(scl, 0, 1), (float)G(scl, 1, 1), (float)G(scl, 2, 1),
                (float)G(rot, 1, 0), (float)G(rot, 2, 0), (float)G(rot, 3, 0), (float)G(rot, 0, 1),
                (float)G(pos, 0, 0), (float)G(pos, 1, 0), (float)G(pos, 2, 0));
            attrs.TryGetValue("Material", out var mtl);
            var geom = EntityGeometry(x, ent);
            if (geom != null) meshes.Add(new Mesh(geom, mtl, t, null));
            else if (attrs.TryGetValue("EntityClassGUID", out var guid)) meshes.Add(new Mesh("", mtl, t, guid));
        }
    }

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

    private static List<double> Csv(string s)
    {
        var res = new List<double>();
        foreach (var part in s.Split(',')) if (RustF64.TryParse(part.Trim(), out var v)) res.Add(v);
        return res;
    }

    // parse_light_entities
    private static List<Light> LightEntities(CryXmlB x, int ent, Dictionary<string, string> attrs, List<double> pos, List<double> rot, string cls)
    {
        var baseName = attrs.TryGetValue("Name", out var n) ? n : "Light";
        double[] bp = [pos.Count > 0 ? pos[0] : 0, pos.Count > 1 ? pos[1] : 0, pos.Count > 2 ? pos[2] : 0];
        double[] br = [rot.Count > 0 ? rot[0] : 1, rot.Count > 1 ? rot[1] : 0, rot.Count > 2 ? rot[2] : 0, rot.Count > 3 ? rot[3] : 0];
        if (cls == "LightGroup") return LightGroup(x, ent, baseName, bp, br);
        foreach (var c in x.Children(ent))
        {
            if (x.Tag(c) != "PropertiesDataCore") continue;
            int lc = -1;
            foreach (var p in x.Children(c)) if (x.Tag(p) == "EntityComponentLight") { lc = p; break; }
            if (lc >= 0) return [FromComponent(x, lc, baseName, bp, br)];
        }
        float radius = attrs.TryGetValue("Radius", out var rs) && ShipMeshes.ParseF32(rs, out var rv) ? rv : 5.0f;
        return [new Light(baseName, bp, br, [1.0f, 0.95f, 0.9f], 200.0f, radius, null, null)];
    }

    // parse_light_group
    private static List<Light> LightGroup(CryXmlB x, int ent, string baseName, double[] bp, double[] br)
    {
        var res = new List<Light>();
        foreach (var child in x.Children(ent))
        {
            var tag = x.Tag(child);
            int lg;
            if (tag == "EntityComponentLightGroup") lg = child;
            else if (tag == "PropertiesDataCore")
            {
                lg = -1;
                foreach (var c in x.Children(child)) if (x.Tag(c) == "EntityComponentLightGroup") { lg = c; break; }
                if (lg < 0) continue;
            }
            else continue;
            foreach (var baked in x.Children(lg))
            {
                if (x.Tag(baked) != "BakedInLights") continue;
                int idx = 0;
                foreach (var ln in x.Children(baked))
                {
                    if (x.Tag(ln) != "Light") continue;
                    var lname = $"{baseName}-{idx:D3}";
                    var (rt, rr) = RelativeXform(x, ln);
                    double[] lp = [bp[0] + rt[0], bp[1] + rt[1], bp[2] + rt[2]];
                    var lr = QuatMul(br, rr);
                    foreach (var lc in x.Children(ln))
                        if (x.Tag(lc) == "EntityComponentLight") { res.Add(FromComponent(x, lc, lname, lp, lr)); break; }
                    idx++;
                }
            }
        }
        if (res.Count == 0) res.Add(new Light(baseName, bp, br, [1.0f, 0.95f, 0.9f], 200.0f, 5.0f, null, null));
        return res;
    }

    private static (double[] T, double[] R) RelativeXform(CryXmlB x, int ln)
    {
        foreach (var c in x.Children(ln))
        {
            if (x.Tag(c) != "RelativeXForm") continue;
            var a = x.AttributeMap(c);
            var t = Csv(a.GetValueOrDefault("translation", "0,0,0"));
            var r = Csv(a.GetValueOrDefault("rotation", "1,0,0,0"));
            return ([t.Count > 0 ? t[0] : 0, t.Count > 1 ? t[1] : 0, t.Count > 2 ? t[2] : 0], [r.Count > 0 ? r[0] : 1, r.Count > 1 ? r[1] : 0, r.Count > 2 ? r[2] : 0, r.Count > 3 ? r[3] : 0]);
        }
        return ([0, 0, 0], [1, 0, 0, 0]);
    }

    private static double[] QuatMul(double[] a, double[] b)
    {
        double aw = a[0], ax = a[1], ay = a[2], az = a[3], bw = b[0], bx = b[1], by = b[2], bz = b[3];
        return [aw * bw - ax * bx - ay * by - az * bz, aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx, aw * bz + ax * by - ay * bx + az * bw];
    }

    // collect_all_attrs_recursive (後のものが上書き。__type は除く)
    private static void AllAttrs(CryXmlB x, int node, Dictionary<string, string> d)
    {
        foreach (var (k, v) in x.Attributes(node)) if (k != "__type") d[k] = v;
        foreach (var c in x.Children(node)) AllAttrs(x, c, d);
    }

    // build_light_info_from_component
    private static Light FromComponent(CryXmlB x, int comp, string name, double[] pos, double[] rot)
    {
        var all = new Dictionary<string, string>(StringComparer.Ordinal);
        AllAttrs(x, comp, all);
        float Af(string k) => all.TryGetValue(k, out var s) && ShipMeshes.ParseF32(s, out var v) ? v : 0.0f;
        float intensity = RustF32.Max(Af("intensity"), 0.001f);
        float r0 = Af("lightRadius");
        float radius = r0 > 0.0f ? r0 : 5.0f;
        bool useT = all.TryGetValue("useTemperature", out var ut) && ut == "1";
        float[] color;
        if (useT) color = Kelvin(RustF32.Min(RustF32.Max(Af("temperature"), 1000.0f), 40000.0f));
        else
        {
            float r = RustF32.Min(RustF32.Max(Af("r"), 0.0f), 1.0f), g = RustF32.Min(RustF32.Max(Af("g"), 0.0f), 1.0f), b = RustF32.Min(RustF32.Max(Af("b"), 0.0f), 1.0f);
            color = r == 0.0f && g == 0.0f && b == 0.0f ? [1.0f, 0.95f, 0.9f] : [r, g, b];
        }
        var type = all.TryGetValue("lightType", out var lt) ? lt : "Omni";
        float? inner = null, outer = null;
        if (type == "Projector")
        {
            float fov = RustF32.Max(Af("FOV"), 1.0f);
            outer = fov * 0.5f; inner = outer * 0.8f;
        }
        return new Light(name, pos, rot, color, intensity * 200.0f, radius, inner, outer);
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;   // f32::clamp (NaN はそのまま)

    // kelvin_to_rgb
    private static float[] Kelvin(float kelvin)
    {
        float temp = kelvin / 100.0f;
        float r = temp <= 66.0f ? 1.0f : Clamp(329.698727446f * MathF.Pow(temp - 60.0f, -0.1332047592f) / 255.0f, 0.0f, 1.0f);
        float g = temp <= 66.0f ? Clamp(99.4708025861f * MathF.Log(temp) - 161.1195681661f, 0.0f, 255.0f) / 255.0f
            : Clamp(288.1221695283f * MathF.Pow(temp - 60.0f, -0.0755148492f) / 255.0f, 0.0f, 1.0f);
        float b = temp >= 66.0f ? 1.0f : temp <= 19.0f ? 0.0f : Clamp(138.5177312231f * MathF.Log(temp - 10.0f) - 305.0447927307f, 0.0f, 255.0f) / 255.0f;
        return [r, g, b];
    }
}

// パレット (query_tint_from_record)
internal static class ShipExportPalette
{
    public static float[][]? FromRecord(DataCoreDb db, DataCoreDb.Record r) => ShipExport.TintFromRecord(db, r);
}
