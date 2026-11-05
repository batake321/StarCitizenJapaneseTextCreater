using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// 船の GLB の組み立て (2026-11-04、利用者の決定「B」(starbreaker とバイト単位で同じ GLB))。
// StarBreaker v0.2.2 の gltf/mod.rs (write_glb) と gltf/glb_builder.rs (GlbBuilder の全部: pack_mesh・build_nmc_hierarchy・attach_skeleton_bones・
// attach_child_entity・attach_interiors・finalize・build_material(s)・テクスチャの重複の除き方) の移植。
// JSON の書き方は gltf-json 1.4.1 (features: names・extras・KHR_lights_punctual・KHR_materials_transmission・KHR_materials_ior) の欄の並びと省き方、
// 数は serde_json 1.0.149 (f32 はそのまま、serde_json::Value に入れた f32 は f64 に広げて) と同じ (RustJson)。
// 違いは asset.extras.export_timestamp_unix (書き出した時刻) だけ。
internal static class ShipGlb
{
    // GLB の asset.extras.generator_version に書く版。書き出しの材質の決め方の版も兼ねる (材質の決め方を変えたら進める。2026-11-05、親の決定):
    //   "0.2.2" は移植元 (StarBreaker v0.2.2) と同じ決め方。"0.2.3" はガラス (GlassPBR) の色の決まり (白なら着色していないガラスの色・glassColor の読み方) を変えた版。
    //   取り込み済みの GLB の版が今の版と違えば、3D の画面は今の決め方の材質の表 (ShipPaints.IdStandardTable) を当てて描く。材質の表の鮮度にも使う (ShipPaints)。
    //   文字の長さは変えない (GLB のほかのバイトを変えないため)
    public const string GeneratorVersion = "0.2.3";

    // PNG 1 枚。GLB の塊に入れたら中身 (Data) を放し、入れた場所 (Region) だけを覚える (メモリを抑えるため。
    // 同じ中身は同じ場所になる (pack_texture_deduped) ので、2 回目からは Region を返しても結果は同じ)
    public sealed class PngBox { public byte[]? Data; public (int Off, int Len)? Region; }
    public sealed record Textures(List<PngBox?> Diffuse, List<PngBox?> Normal, List<PngBox?> Roughness);

    public sealed class Input
    {
        public ShipMeshes.Mesh? RootMesh;
        public SocpakGlb.MtlFile? RootMtl;
        public Textures? RootTex;
        public List<ShipMeshes.NmcNode>? RootNmc;
        public float[][]? RootPalette;
        public List<CryIvo.Bone> RootBones = new();
        public List<ShipExport.Payload> Children = new();
        public ShipInteriors.Loaded Interiors = new();
        public string? EntityName, GeometryPath, MaterialPath;
        // true ならパレットの色 (本体・子・船内のパレットの A・B・C と glass) を材質に当てない (色は材質そのものの色。ガラスの色の決まりは同じ)。
        // 3D の画面の色付けを切る設定の材質の表を作るときだけ (ShipExport.ExportJson。2026-11-05、利用者の指示「"船・装備・武器の 3D は常に色付け" これは設定で切れるようにしてください。」)
        public bool NoPalette;
        // 足す区画だけの GLB (ShipExport.ExportAddon) のとき: 取り付けられた子の乗り物の区画を、その子のノードの置き方 (船の座標。列優先 16) の下に入れる。
        // ふつうの船の書き出しでは空 (子の区画は Children の Payload.Interiors で、子のノードの下に入る)
        public List<(string Name, float[] Matrix, ShipInteriors.Loaded Interiors)> ExtraInteriors = new();
    }

    public static void Write(ShipExport.Ctx ctx, Input input, Stream output)
    {
        var b = new Builder { NoPalette = input.NoPalette };
        List<int> scene;
        if (input.RootMesh is { } rm)
        {
            var packed = b.PackMesh(rm, input.RootMtl, input.RootTex, input.RootPalette, null);
            input.RootTex = null;
            if (input.RootNmc is { Count: > 0 } nmc) scene = b.BuildNmc(packed, nmc, rm.Subs, true);
            else { b.Nodes.Add(new Node { Mesh = packed.MeshIdx }); scene = [0]; }
        }
        else scene = new();
        b.AttachBones(input.RootBones, scene);
        // 灯りの一覧は 1 つ (子の乗り物の区画の灯りが先、船の区画の灯りが後。子の区画の無い船は今までと同じ並び)
        var lights = new List<ShipInteriors.Light>();
        for (int i = 0; i < input.Children.Count; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            b.AttachChild(ctx, input.Children[i], scene, input.RootPalette, lights);
            b.MaybeCollect();
            if ((i + 1) % 20 == 0 || i + 1 == input.Children.Count) ctx.Log?.Invoke($"[mem-phase] children {i + 1}/{input.Children.Count}, bin={b.Bin.Len / 1_048_576}MB");
        }
        ctx.Log?.Invoke($"[mem-phase] children done, bin={b.Bin.Len / 1_048_576}MB");
        var iscene = b.AttachInteriors(ctx, input.Interiors, input.RootPalette, lights);
        ctx.Log?.Invoke($"[mem-phase] interiors done, bin={b.Bin.Len / 1_048_576}MB");
        scene.AddRange(iscene);
        foreach (var (ename, ematrix, eint) in input.ExtraInteriors)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            var kids = b.AttachInteriors(ctx, eint, input.RootPalette, lights);
            scene.Add(b.Nodes.Count);
            b.Nodes.Add(new Node { Name = ename, Matrix = ematrix, Children = kids.Count == 0 ? null : kids });
        }

        // 最初の子に船の名前・パレットの extras (serde_json の Map はキーの順)
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (input.EntityName is string en)
        {
            map["entity_name"] = S(en);
            if (input.GeometryPath is string gp) map["geometry_path"] = S(gp);
            if (input.MaterialPath is string mp) map["material_path"] = S(mp);
        }
        if (input.RootPalette is { } pal)
            map["tint_palette"] = "{\"glass\":" + A64(pal[3]) + ",\"primary\":" + A64(pal[0]) + ",\"secondary\":" + A64(pal[1]) + ",\"tertiary\":" + A64(pal[2]) + "}";
        if (map.Count > 0 && scene.Count > 0 && scene[0] < b.Nodes.Count)
            b.Nodes[scene[0]].Extras = "{" + string.Join(",", map.Select(kv => S(kv.Key) + ":" + kv.Value)) + "}";
        // 大きい船では JSON (Idris-P で 35 MB。文字列にする間に 3 倍ほどの一時の量になる) を作る前に、溜まったごみを片付ける (出力には関係しない)
        if (b.Bin.Len >= 128 << 20) GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        b.Finalize(scene, lights, output);
    }

    internal static string S(string v) { var sb = new StringBuilder(); RustJson.Str(sb, v); return sb.ToString(); }
    internal static string J32(float f) => float.IsFinite(f) ? RustJson.F32(f) : "null";
    internal static string J64(float f) => float.IsFinite(f) ? RustJson.F64(f) : "null";
    private static string A32(float[] a) => "[" + string.Join(",", a.Select(J32)) + "]";
    private static string A64(float[] a) => "[" + string.Join(",", a.Select(J64)) + "]";

    private sealed class Node
    {
        public List<int>? Children;
        public string? Extensions, Extras;
        public float[]? Matrix;
        public int? Mesh;
        public string? Name;
        public float[]? Rotation, Translation;
    }

    private sealed class Packed
    {
        public int MeshIdx, PosAcc;
        public int? UvAcc, NrmAcc, TanAcc;
        public List<int> SubMat = new();
        public List<int?> SubIdxAcc = new();
    }

    // バイナリの塊 (大きい船は数百 MB になるので、16 MB ずつの塊に分けて持つ。伸ばすときの写しと倍の確保を避ける)
    private sealed class Buf
    {
        private const int Chunk = 16 << 20;
        private readonly List<byte[]> _chunks = new();
        private int _pos = Chunk;   // 今の塊の中の位置
        public int Len;
        private Span<byte> Take(int n)
        {
            if (_pos + n > Chunk) { _chunks.Add(new byte[Chunk]); _pos = 0; }
            var s = _chunks[^1].AsSpan(_pos, n);
            _pos += n; Len += n;
            return s;
        }
        public void Align() { while (Len % 4 != 0) Take(1)[0] = 0; }
        public void F(float v) => BinaryPrimitives.WriteSingleLittleEndian(Take(4), v);
        public void U16(ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), v);
        public void U32(uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), v);
        public void Bytes(byte[] b)
        {
            int off = 0;
            while (off < b.Length)
            {
                if (_pos == Chunk) { _chunks.Add(new byte[Chunk]); _pos = 0; }
                int n = Math.Min(b.Length - off, Chunk - _pos);
                b.AsSpan(off, n).CopyTo(_chunks[^1].AsSpan(_pos, n));
                _pos += n; Len += n; off += n;
            }
        }
        public void WriteTo(Stream o)
        {
            for (int i = 0; i < _chunks.Count; i++) o.Write(_chunks[i], 0, i == _chunks.Count - 1 ? _pos : Chunk);
        }
    }

    private sealed class Builder
    {
        public readonly Buf Bin = new();
        public readonly List<string> Views = new(), Accessors = new(), Materials = new(), Images = new(), TexturesJson = new(), Meshes = new();
        public readonly List<Node> Nodes = new();
        public bool HasSampler;
        // Input.NoPalette
        public bool NoPalette;
        private readonly Dictionary<string, int> _matDedup = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (int Off, int Len)> _texCache = new(StringComparer.Ordinal);

        // 大きい船では組み立ての間に一時の配列 (テクスチャの展開・形の読み取り) のごみが溜まり、最大の作業セットが生きているメモリの 2 倍ほどになる
        // (Idris-P の実測: 生きているもの 約 1.0 GB、最大の作業セット 2.2〜2.8 GB)。このプロセスで新しく確保した量が GcStepBytes を超えるごとに
        // GC を 1 回かけて抑える (出力には関係しない。2026-11-04)
        private const long GcStepBytes = 512L << 20;
        private long _lastGcAlloc = GC.GetTotalAllocatedBytes(false);
        public void MaybeCollect()
        {
            long now = GC.GetTotalAllocatedBytes(false);
            if (now - _lastGcAlloc < GcStepBytes) return;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            _lastGcAlloc = GC.GetTotalAllocatedBytes(false);
        }
        public readonly Dictionary<string, int> NameToIdx = new(StringComparer.Ordinal);
        public bool AnyMaterialExtension;

        private int? VertexAccessor(int off, int len, int count, string type, float[]? min, float[]? max)
        {
            if (len == 0) return null;
            int bv = Views.Count;
            Views.Add($"{{\"buffer\":0,\"byteLength\":{len},\"byteOffset\":{off},\"target\":34962}}");
            int ai = Accessors.Count;
            var sb = new StringBuilder($"{{\"bufferView\":{bv},\"byteOffset\":0,\"count\":{count},\"componentType\":5126,\"type\":\"{type}\"");
            if (min != null) sb.Append(",\"min\":").Append(A64(min)).Append(",\"max\":").Append(A64(max!));
            sb.Append('}');
            Accessors.Add(sb.ToString());
            return ai;
        }

        // pack_texture_deduped (鍵: 大きさ・先頭 64 バイト・末尾 64 バイト)
        private (int, int)? PackTexture(PngBox? box)
        {
            if (box == null) return null;
            if (box.Region is { } known) return known;
            var png = box.Data!;
            var head = Convert.ToHexString(png, 0, Math.Min(64, png.Length));
            var tail = png.Length > 64 ? Convert.ToHexString(png, png.Length - 64, 64) : "";
            var key = $"{png.Length}|{head}|{tail}";
            if (!_texCache.TryGetValue(key, out var reg))
            {
                Bin.Align();
                int off = Bin.Len;
                Bin.Bytes(png);
                reg = (off, png.Length);
                _texCache[key] = reg;
            }
            box.Region = reg; box.Data = null;
            return reg;
        }

        // regions_to_gltf_textures_deduped (重複の表は pack_mesh 1 回ごと)
        private List<int?> RegionsToTextures(List<(int Off, int Len)?> regions, Dictionary<(int, int), int> dedup)
        {
            var res = new List<int?>();
            foreach (var r in regions)
            {
                if (r is not { } reg) { res.Add(null); continue; }
                if (dedup.TryGetValue(reg, out var t)) { res.Add(t); continue; }
                int bv = Views.Count;
                Views.Add($"{{\"buffer\":0,\"byteLength\":{reg.Len},\"byteOffset\":{reg.Off}}}");
                int img = Images.Count;
                Images.Add($"{{\"bufferView\":{bv},\"mimeType\":\"image/png\"}}");
                int tex = TexturesJson.Count;
                TexturesJson.Add($"{{\"sampler\":0,\"source\":{img}}}");
                dedup[reg] = tex;
                res.Add(tex);
            }
            return res;
        }

        // pack_mesh (材質 textures: 接線も書く)
        public Packed PackMesh(ShipMeshes.Mesh mesh, SocpakGlb.MtlFile? mtl, Textures? tex, float[][]? palette, string? name)
        {
            Bin.Align();
            int posOff = Bin.Len;
            foreach (var f in mesh.Pos) Bin.F(f);
            int posLen = Bin.Len - posOff;
            int uvOff = Bin.Len, uvLen = 0;
            if (mesh.Uv != null) { foreach (var f in mesh.Uv) Bin.F(f); uvLen = Bin.Len - uvOff; }
            int nOff = Bin.Len, nLen = 0;
            if (mesh.Nrm != null) { foreach (var f in mesh.Nrm) Bin.F(f); nLen = Bin.Len - nOff; }
            int tOff = Bin.Len, tLen = 0;
            if (mesh.Tan != null) { foreach (var f in mesh.Tan) Bin.F(f); tLen = Bin.Len - tOff; }
            Bin.Align();
            int idxOff = Bin.Len;
            uint maxIdx = 0;
            foreach (var i in mesh.Idx) if (i > maxIdx) maxIdx = i;
            bool u16 = maxIdx <= ushort.MaxValue;
            if (u16) { foreach (var i in mesh.Idx) Bin.U16((ushort)i); Bin.Align(); }
            else foreach (var i in mesh.Idx) Bin.U32(i);
            int idxLen = u16 ? mesh.Idx.Length * 2 : mesh.Idx.Length * 4;

            var texR = new List<(int, int)?>(); var nrmR = new List<(int, int)?>(); var rouR = new List<(int, int)?>();
            if (tex != null)
            {
                foreach (var p in tex.Diffuse) texR.Add(PackTexture(p));
                foreach (var p in tex.Normal) nrmR.Add(PackTexture(p));
                foreach (var p in tex.Roughness) rouR.Add(PackTexture(p));
            }

            float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
            for (int v = 0; v < mesh.N; v++) for (int k = 0; k < 3; k++) { mn[k] = RustF32.Min(mn[k], mesh.Pos[v * 3 + k]); mx[k] = RustF32.Max(mx[k], mesh.Pos[v * 3 + k]); }
            int posAcc = VertexAccessor(posOff, posLen, mesh.N, "VEC3", mn, mx) ?? 0;
            int? uvAcc = VertexAccessor(uvOff, uvLen, mesh.Uv == null ? 0 : mesh.Uv.Length / 2, "VEC2", null, null);
            int? nAcc = VertexAccessor(nOff, nLen, mesh.Nrm == null ? 0 : mesh.Nrm.Length / 3, "VEC3", null, null);
            int? tAcc = VertexAccessor(tOff, tLen, mesh.Tan == null ? 0 : mesh.Tan.Length / 4, "VEC4", null, null);
            int idxBv = Views.Count;
            Views.Add($"{{\"buffer\":0,\"byteLength\":{idxLen},\"byteOffset\":{idxOff},\"target\":34963}}");

            bool any = texR.Concat(nrmR).Concat(rouR).Any(r => r != null);
            if (any) HasSampler = true;
            var dedup = new Dictionary<(int, int), int>();
            var texIdx = RegionsToTextures(texR, dedup);
            var nrmIdx = RegionsToTextures(nrmR, dedup);
            var rouIdx = RegionsToTextures(rouR, dedup);

            var res = new Packed { PosAcc = posAcc, UvAcc = uvAcc, NrmAcc = nAcc, TanAcc = tAcc };
            foreach (var s in mesh.Subs) res.SubMat.Add(Material(s, mtl, palette, texIdx, nrmIdx, rouIdx));
            var prims = new List<string>();
            for (int i = 0; i < mesh.Subs.Count; i++)
            {
                var s = mesh.Subs[i];
                var ms = mtl != null && s.MatId < mtl.Materials.Count ? mtl.Materials[(int)s.MatId] : null;
                if (ms != null && ms.ShouldHide()) { res.SubIdxAcc.Add(null); continue; }
                if (ms != null && ms.IsDecal && !(s.MatId < texIdx.Count && texIdx[(int)s.MatId] != null)) { res.SubIdxAcc.Add(null); continue; }
                int ai = Accessors.Count;
                res.SubIdxAcc.Add(ai);
                Accessors.Add($"{{\"bufferView\":{idxBv},\"byteOffset\":{(u16 ? (long)s.First * 2 : (long)s.First * 4)},\"count\":{s.Num},\"componentType\":{(u16 ? 5123 : 5125)},\"type\":\"SCALAR\"}}");
                var at = new StringBuilder($"{{\"POSITION\":{posAcc}");
                if (nAcc is int na) at.Append($",\"NORMAL\":{na}");
                if (tAcc is int ta) at.Append($",\"TANGENT\":{ta}");
                if (uvAcc is int ua) at.Append($",\"TEXCOORD_0\":{ua}");
                at.Append('}');
                prims.Add($"{{\"attributes\":{at},\"indices\":{ai},\"material\":{res.SubMat[i]}}}");
            }
            res.MeshIdx = Meshes.Count;
            Meshes.Add(MeshJson(name, prims));
            return res;
        }

        private static string MeshJson(string? name, List<string> prims)
        {
            var sb = new StringBuilder("{");
            if (name != null) { sb.Append("\"name\":"); RustJson.Str(sb, name); sb.Append(','); }
            sb.Append("\"primitives\":[").Append(string.Join(",", prims)).Append("]}");
            return sb.ToString();
        }

        // build_material + build_materials の重ね方
        private int Material(ShipMeshes.Sub sub, SocpakGlb.MtlFile? mtl, float[][]? palette, List<int?> texIdx, List<int?> nrmIdx, List<int?> rouIdx)
        {
            var m = mtl != null && sub.MatId < mtl.Materials.Count ? mtl.Materials[(int)sub.MatId] : null;
            float[] baseColor; int alphaMode; float? cutoff; bool doubleSided; string? name;
            if (m != null)
            {
                var (am, ac) = m.AlphaConfig();
                alphaMode = am; cutoff = am == 1 ? ac : null;
                var baseName = m.Name.Length == 0 ? sub.MatName ?? "" : m.Name;
                string? stem = null;
                if (mtl!.SourcePath is string sp) { var f = sp[(sp.LastIndexOfAny(['\\', '/']) + 1)..]; stem = f.EndsWith(".mtl", StringComparison.Ordinal) ? f[..^4] : f; }
                name = stem != null ? $"{stem}_mtl_{baseName}_0{sub.MatId}" : baseName.Length == 0 ? null : baseName;
                float[]? pal = palette == null || NoPalette ? null : m.PaletteTint switch { 1 => palette[0], 2 => palette[1], 3 => palette[2], _ => m.IsGlass ? palette[3] : null };
                float[]? layerColor = m.Layers.Count > 0 && !(m.Layers[0].Tint[0] == 1 && m.Layers[0].Tint[1] == 1 && m.Layers[0].Tint[2] == 1) ? m.Layers[0].Tint : null;
                var c = pal ?? m.MetalBaseColor() ?? layerColor ?? m.Diffuse;
                // ガラス (GlassPBR) は、色の出どころ (パレットの A・B・C・glassColor、材質の色) によらず、決まった色が白なら着色していないガラスの色 (GlassTint) (2026-11-05、利用者の指示)
                if (m.IsGlass) c = GlassTint.OrUntinted(c);
                baseColor = [c[0], c[1], c[2], m.Opacity];
                doubleSided = m.DoubleSided;
            }
            else { baseColor = [0.8f, 0.8f, 0.8f, 1.0f]; alphaMode = 0; cutoff = null; doubleSided = false; name = sub.MatName; }
            int? baseTex = sub.MatId < texIdx.Count ? texIdx[(int)sub.MatId] : null;
            bool allowDetail = m != null && m.DiffuseTex != null;
            int? nrmTex = allowDetail && sub.MatId < nrmIdx.Count ? nrmIdx[(int)sub.MatId] : null;
            float roughness = m?.Roughness() ?? 0.5f, metallic = m?.Metallic() ?? 0.0f;
            var emissive = m?.EmissiveFactor() ?? [0, 0, 0];
            bool glass = m?.IsGlass ?? false;
            string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            var key = $"{name}|{string.Join(",", baseColor.Select(R))}|{R(metallic)}|{R(roughness)}|{doubleSided}|{alphaMode}|{(cutoff is float cf ? R(cf) : "-")}|{baseTex?.ToString() ?? "-"}|{nrmTex?.ToString() ?? "-"}";
            if (_matDedup.TryGetValue(key, out var idx)) return idx;
            var sb = new StringBuilder("{");
            if (cutoff is float co) sb.Append("\"alphaCutoff\":").Append(J32(co)).Append(',');
            sb.Append("\"alphaMode\":\"").Append(alphaMode switch { 1 => "MASK", 2 => "BLEND", _ => "OPAQUE" }).Append("\",");
            sb.Append("\"doubleSided\":").Append(doubleSided ? "true" : "false").Append(',');
            if (name != null) { sb.Append("\"name\":"); RustJson.Str(sb, name); sb.Append(','); }
            sb.Append("\"pbrMetallicRoughness\":{\"baseColorFactor\":").Append(A32(baseColor));
            if (baseTex is int bt) sb.Append(",\"baseColorTexture\":{\"index\":").Append(bt).Append(",\"texCoord\":0}");
            sb.Append(",\"metallicFactor\":").Append(J32(metallic)).Append(",\"roughnessFactor\":").Append(J32(roughness)).Append("},");
            if (nrmTex is int nt) sb.Append("\"normalTexture\":{\"index\":").Append(nt).Append(",\"scale\":1.0,\"texCoord\":0},");
            sb.Append("\"emissiveFactor\":").Append(A32(emissive));
            if (glass) { sb.Append(",\"extensions\":{\"KHR_materials_transmission\":{\"transmissionFactor\":0.95},\"KHR_materials_ior\":{\"ior\":1.5}}"); AnyMaterialExtension = true; }
            if (m != null && Extras(m) is string ex) sb.Append(",\"extras\":").Append(ex);
            sb.Append('}');
            idx = Materials.Count;
            Materials.Add(sb.ToString());
            _matDedup[key] = idx;
            return idx;
        }

        // 材質の extras (serde_json の Map: キーの順)
        private static string? Extras(SocpakGlb.SubMat m)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (m.Name.Length > 0) map["source_name"] = S(m.Name);
            if (m.Shader.Length > 0) map["shader"] = S(m.Shader);
            if (!(m.Diffuse[0] == 1 && m.Diffuse[1] == 1 && m.Diffuse[2] == 1)) map["diffuse"] = A64(m.Diffuse);
            if (!(m.Specular[0] == 0.04f && m.Specular[1] == 0.04f && m.Specular[2] == 0.04f)) map["specular"] = A64(m.Specular);
            if (MathF.Abs(m.Shininess - 128.0f) > 1.1920929E-07f) map["shininess"] = J64(m.Shininess);
            if (!(m.Emissive[0] == 0 && m.Emissive[1] == 0 && m.Emissive[2] == 0)) map["emissive"] = A64(m.Emissive);
            if (m.Glow != 0.0f) map["glow"] = J64(m.Glow);
            if (MathF.Abs(m.Opacity - 1.0f) > 1.1920929E-07f) map["opacity"] = J64(m.Opacity);
            if (m.SurfaceType.Length > 0) map["surface_type"] = S(m.SurfaceType);
            if (m.StringGenMask.Length > 0) map["string_gen_mask"] = S(m.StringGenMask);
            if (m.DiffuseTex != null) map["diffuse_tex"] = S(m.DiffuseTex);
            if (m.NormalTex != null) map["normal_tex"] = S(m.NormalTex);
            if (m.PaletteTint > 0) map["palette_tint"] = m.PaletteTint.ToString(CultureInfo.InvariantCulture);
            if (m.Layers.Count > 0)
            {
                map["layers"] = "[" + string.Join(",", m.Layers.Select(l =>
                {
                    var lm = new SortedDictionary<string, string>(StringComparer.Ordinal);
                    if (l.Path.Length > 0) lm["path"] = S(l.Path);
                    if (!(l.Tint[0] == 1 && l.Tint[1] == 1 && l.Tint[2] == 1)) lm["tint_color"] = A64(l.Tint);
                    if (l.PaletteTint > 0) lm["palette_tint"] = l.PaletteTint.ToString(CultureInfo.InvariantCulture);
                    if (MathF.Abs(l.UvTiling - 1.0f) > 1.1920929E-07f) lm["uv_tiling"] = J64(l.UvTiling);
                    return "{" + string.Join(",", lm.Select(kv => S(kv.Key) + ":" + kv.Value)) + "}";
                })) + "]";
            }
            if (map.Count == 0) return null;
            return "{" + string.Join(",", map.Select(kv => S(kv.Key) + ":" + kv.Value)) + "}";
        }

        // is_identity_or_zero
        private static bool IdentityOrZero(float[] m)
        {
            bool zero = true;
            foreach (var v in m) if (v != 0.0f) { zero = false; break; }
            if (zero) return true;
            float[] id = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
            for (int i = 0; i < 12; i++) if (!(MathF.Abs(m[i] - id[i]) <= 1e-6f)) return false;
            return true;
        }

        // mat3x4_to_gltf (3×4 行優先 → 列優先 16)
        private static float[] Mat3x4ToGltf(float[] m) => [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1];

        // build_nmc_hierarchy。返すのは NMC の中の番号 (根のもの)
        public List<int> BuildNmc(Packed packed, List<ShipMeshes.NmcNode> nmc, List<ShipMeshes.Sub> subs, bool hasMesh)
        {
            if (hasMesh) Meshes.RemoveAt(Meshes.Count - 1);
            var nodeSubs = new List<int>[nmc.Count];
            for (int i = 0; i < nmc.Count; i++) nodeSubs[i] = new();
            for (int i = 0; i < subs.Count; i++) { int n = subs[i].Node; if (n < nmc.Count) nodeSubs[n].Add(i); }
            var nodeMesh = new int?[nmc.Count];
            if (hasMesh)
                for (int ni = 0; ni < nmc.Count; ni++)
                {
                    if (nodeSubs[ni].Count == 0) continue;
                    var prims = new List<string>();
                    foreach (var si in nodeSubs[ni])
                    {
                        if (packed.SubIdxAcc[si] is not int ia) continue;
                        var at = new StringBuilder($"{{\"POSITION\":{packed.PosAcc}");
                        if (packed.NrmAcc is int na) at.Append($",\"NORMAL\":{na}");
                        if (packed.UvAcc is int ua) at.Append($",\"TEXCOORD_0\":{ua}");
                        at.Append('}');
                        prims.Add($"{{\"attributes\":{at},\"indices\":{ia},\"material\":{packed.SubMat[si]}}}");
                    }
                    nodeMesh[ni] = Meshes.Count;
                    Meshes.Add(MeshJson(nmc[ni].Name, prims));
                }
            var kids = new List<int>[nmc.Count];
            for (int i = 0; i < nmc.Count; i++) kids[i] = new();
            var roots = new List<int>();
            for (int i = 0; i < nmc.Count; i++)
            {
                if (nmc[i].Parent is ushort p && p < nmc.Count) kids[p].Add(i);
                else roots.Add(i);
            }
            int baseIdx = Nodes.Count;
            for (int i = 0; i < nmc.Count; i++)
            {
                var n = nmc[i];
                int ni = Nodes.Count;
                if (n.Name.Length > 0) NameToIdx[n.Name.ToLowerInvariant()] = ni;
                Nodes.Add(new Node
                {
                    Name = n.Name,
                    Mesh = nodeMesh[i],
                    Matrix = IdentityOrZero(n.BoneToWorld) ? null : Mat3x4ToGltf(n.BoneToWorld),
                    Children = kids[i].Count == 0 ? null : kids[i].Select(c => baseIdx + c).ToList(),
                });
            }
            return roots;
        }

        public void AppendChild(int parent, int child)
        {
            if (parent == child) return;
            if (parent < 0 || parent >= Nodes.Count) return;
            (Nodes[parent].Children ??= new()).Add(child);
        }

        // glam Mat4::from_rotation_translation(Quat::from_xyzw(x,y,z,w), t).to_cols_array()
        private static float[] RotTrans(float qx, float qy, float qz, float qw, float tx, float ty, float tz)
        {
            var r = Glam.QuatToMat3(qx, qy, qz, qw);
            return [r[0], r[1], r[2], 0, r[3], r[4], r[5], 0, r[6], r[7], r[8], 0, tx, ty, tz, 1];
        }

        // attach_skeleton_bones
        public void AttachBones(List<CryIvo.Bone> bones, List<int> scene)
        {
            foreach (var bone in bones)
            {
                int bi = Nodes.Count;
                var cols = RotTrans(bone.Rotation[1], bone.Rotation[2], bone.Rotation[3], bone.Rotation[0], bone.Position[0], bone.Position[1], bone.Position[2]);
                float[] m3x4 = [cols[0], cols[4], cols[8], cols[12], cols[1], cols[5], cols[9], cols[13], cols[2], cols[6], cols[10], cols[14]];
                Nodes.Add(new Node { Name = bone.Name, Matrix = IdentityOrZero(m3x4) ? null : Mat3x4ToGltf(m3x4) });
                NameToIdx.TryAdd(bone.Name.ToLowerInvariant(), bi);
                if (scene.Count > 0) AppendChild(scene[0], bi);
            }
        }

        // offset_to_gltf_matrix
        private static float[]? OffsetMatrix(float[] pos, float[] rotDeg)
        {
            bool hasPos = pos[0] != 0.0f || pos[1] != 0.0f || pos[2] != 0.0f;
            bool hasRot = rotDeg[0] != 0.0f || rotDeg[1] != 0.0f || rotDeg[2] != 0.0f;
            if (!hasPos && !hasRot) return null;
            float rx = ShipInteriors.ToRadians(rotDeg[0]), ry = ShipInteriors.ToRadians(rotDeg[1]), rz = ShipInteriors.ToRadians(rotDeg[2]);
            // Quat::from_euler(EulerRot::XYZ, rx, ry, rz) (glam 0.29.3 euler.rs: 静的な XYZ、偶の並び → y の角の向きを逆に)
            float ti = rx * 0.5f, tj = -ry * 0.5f, th = rz * 0.5f;
            float si = MathF.Sin(ti), ci = MathF.Cos(ti), sj = MathF.Sin(tj), cj = MathF.Cos(tj), sh = MathF.Sin(th), ch = MathF.Cos(th);
            float cc = ci * ch, cs = ci * sh, sc = si * ch, ss = si * sh;
            float qx = cj * sc - sj * cs;
            float qy = (cj * ss + sj * cc) * -1.0f;
            float qz = cj * cs - sj * sc;
            float qw = cj * cc + sj * ss;
            return RotTrans(qx, qy, qz, qw, pos[0], pos[1], pos[2]);
        }

        // compute_node_world_translation
        private float[] WorldTranslation(int nodeIdx)
        {
            var parent = new int?[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
                if (Nodes[i].Children is { } ch)
                    foreach (var c in ch) if (c < parent.Length) parent[c] = i;
            float[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
            static float[] Mul(float[] a, float[] b)
            {
                var r = new float[16];
                for (int i = 0; i < 4; i++)
                    for (int j = 0; j < 4; j++)
                        for (int k = 0; k < 4; k++)
                            r[j * 4 + i] += a[k * 4 + i] * b[j * 4 + k];
                return r;
            }
            var chain = new List<int> { nodeIdx };
            int cur = nodeIdx;
            while (parent[cur] is int p)
            {
                if (chain.Count >= Nodes.Count) break;
                chain.Add(p); cur = p;
            }
            var world = identity;
            for (int i = chain.Count - 1; i >= 0; i--) world = Mul(world, Nodes[chain[i]].Matrix ?? identity);
            return [world[12], world[13], world[14]];
        }

        // attach_child_entity
        public void AttachChild(ShipExport.Ctx ctx, ShipExport.Payload child, List<int> scene, float[][]? fallbackPalette, List<ShipInteriors.Light> lights)
        {
            bool hasMesh = child.Mesh.Pos.Length > 0;
            Packed? packed = null;
            if (hasMesh)
            {
                var tex = child.Mtl != null ? ShipTextures.Load(ctx, child.Mtl) : null;
                packed = PackMesh(child.Mesh, child.Mtl, tex, child.Palette ?? fallbackPalette, child.EntityName);
                // starbreaker と同じく、詰めた後は形のデータを放す (submesh は NMC の木に使うので残す)
                child.Mesh.Pos = []; child.Mesh.Uv = null; child.Mesh.Nrm = null; child.Mesh.Tan = null; child.Mesh.Idx = [];
            }
            int childRoot;
            var lowerName = child.EntityName.ToLowerInvariant();
            if (child.Nmc is { } cn && cn.Count > 0)
            {
                int cbase = Nodes.Count;
                var roots = BuildNmc(packed ?? new Packed(), cn, child.Mesh.Subs, hasMesh);
                var boneIdx = new List<int>();
                foreach (var bone in child.Bones)
                {
                    if (bone.Name.Length == 0) continue;
                    var lb = bone.Name.ToLowerInvariant();
                    if (NameToIdx.ContainsKey(lb)) continue;
                    int bi = Nodes.Count;
                    Nodes.Add(new Node { Name = bone.Name, Matrix = RotTrans(bone.Rotation[1], bone.Rotation[2], bone.Rotation[3], bone.Rotation[0], bone.Position[0], bone.Position[1], bone.Position[2]) });
                    NameToIdx[lb] = bi;
                    boneIdx.Add(bi);
                }
                int wi = Nodes.Count;
                NameToIdx.TryAdd(lowerName, wi);
                var wk = roots.Select(r => cbase + r).Concat(boneIdx).ToList();
                var off = child.OffMatrix ?? OffsetMatrix(child.OffPos, child.OffRot);
                Nodes.Add(new Node { Name = child.EntityName, Children = wk.Count == 0 ? null : wk, Matrix = off });
                childRoot = wi;
            }
            else
            {
                int ni = Nodes.Count;
                NameToIdx.TryAdd(lowerName, ni);
                Nodes.Add(new Node { Name = child.EntityName, Mesh = packed?.MeshIdx, Matrix = child.OffMatrix ?? OffsetMatrix(child.OffPos, child.OffRot) });
                childRoot = ni;
            }
            int? parentIdx = NameToIdx.TryGetValue(child.ParentNodeName.ToLowerInvariant(), out var pi) ? pi
                : NameToIdx.TryGetValue(child.ParentEntityName.ToLowerInvariant(), out var pe) ? pe : null;
            int target = parentIdx ?? (scene.Count > 0 ? scene[0] : 0);
            if (child.NoRotation)
            {
                var wt = WorldTranslation(target);
                float[] pt = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, wt[0], wt[1], wt[2], 1];
                var fm = (child.OffMatrix ?? OffsetMatrix(child.OffPos, child.OffRot)) is { } om ? Glam.Mul(pt, om) : pt;
                Nodes[childRoot].Matrix = fm;
                AppendChild(scene.Count > 0 ? scene[0] : 0, childRoot);
            }
            else AppendChild(target, childRoot);
            // 子が自分で持つ区画: 子のノードの下に入れる (子を単体で書き出したときの、いちばん上の並びと同じ置き方)
            if (child.Interiors is { } ci)
                foreach (var n in AttachInteriors(ctx, ci, child.Palette ?? fallbackPalette, lights)) AppendChild(childRoot, n);
        }

        // attach_interiors
        // lights: 灯りの一覧 (ここで足す。ノードの灯りの番号は、この一覧の中の番号)
        public List<int> AttachInteriors(ShipExport.Ctx ctx, ShipInteriors.Loaded interiors, float[][]? fallbackPalette, List<ShipInteriors.Light> lights)
        {
            var meshCache = new (ShipMeshes.Mesh Mesh, SocpakGlb.MtlFile? Mtl)?[interiors.Cgfs.Count];
            var tried = new bool[interiors.Cgfs.Count];
            // 部品の形を最後に使う区画 (その区画が済んだら形を放す。後の区画では使わないので結果は同じ)
            var lastUse = new int[interiors.Cgfs.Count];
            for (int ci = 0; ci < interiors.Containers.Count; ci++)
                foreach (var (ix, _) in interiors.Containers[ci].Placements) lastUse[ix] = ci;
            int cIndex = -1;
            var packedCache = new Dictionary<(int, string), int>();
            var scene = new List<int>();
            foreach (var c in interiors.Containers)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                cIndex++;
                var palette = c.Palette ?? fallbackPalette;
                var pkey = palette == null ? "" : string.Join(",", palette.SelectMany(x => x).Select(f => BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture)));
                int cni = Nodes.Count;
                Nodes.Add(new Node { Name = "interior_" + c.Name, Matrix = c.Transform });
                var kids = new List<int>();
                foreach (var (ix, t) in c.Placements)
                {
                    var key = (ix, pkey);
                    if (!packedCache.TryGetValue(key, out var mi))
                    {
                        if (!tried[ix]) { tried[ix] = true; meshCache[ix] = LoadInteriorMesh(ctx, interiors.Cgfs[ix]); }
                        if (meshCache[ix] is not { } ld) { packedCache[key] = -1; continue; }
                        var tex = ld.Mtl != null ? ShipTextures.Load(ctx, ld.Mtl) : null;
                        mi = PackMesh(ld.Mesh, ld.Mtl, tex, palette, interiors.Cgfs[ix].Name).MeshIdx;
                        packedCache[key] = mi;
                        MaybeCollect();
                    }
                    if (mi < 0) continue;
                    kids.Add(Nodes.Count);
                    Nodes.Add(new Node { Mesh = mi, Matrix = t });
                }
                foreach (var l in c.Lights)
                {
                    int li = lights.Count;
                    lights.Add(l);
                    kids.Add(Nodes.Count);
                    Nodes.Add(new Node
                    {
                        Name = l.Name,
                        Translation = [(float)l.Pos[0], (float)l.Pos[1], (float)l.Pos[2]],
                        Rotation = [(float)l.Rot[1], (float)l.Rot[2], (float)l.Rot[3], (float)l.Rot[0]],
                        Extensions = $"{{\"KHR_lights_punctual\":{{\"light\":{li}}}}}",
                    });
                }
                if (kids.Count > 0) Nodes[cni].Children = kids;
                scene.Add(cni);
                MaybeCollect();
                foreach (var (ix, _) in c.Placements) if (lastUse[ix] == cIndex) meshCache[ix] = null;
            }
            return scene;
        }

        // 船内の部品の形 (export_cgf_from_path・材質 colors・2 つの箱が 0.01 より違えば NMC を焼き込む)。失敗は null
        private static (ShipMeshes.Mesh, SocpakGlb.MtlFile?)? LoadInteriorMesh(ShipExport.Ctx ctx, ShipInteriors.Cgf cgf)
        {
            try
            {
                var clean = cgf.Path.Replace('\\', '/');
                var gp = clean.StartsWith("data/", StringComparison.Ordinal) || clean.StartsWith("Data/", StringComparison.Ordinal) ? clean[5..] : clean;
                var l = ShipExport.LoadForInterior(ctx, gp, cgf.Mtl ?? "");
                var mesh = l.Mesh;
                bool bake = false;
                for (int k = 0; k < 3; k++)
                    if (MathF.Abs(mesh.ScaleMin[k] - mesh.ModelMin[k]) > 0.01f || MathF.Abs(mesh.ScaleMax[k] - mesh.ModelMax[k]) > 0.01f) bake = true;
                if (bake) Bake(mesh, l.Nmc);
                return (mesh, l.Mtl);
            }
            catch (Exception e) when (ShipExport.IsLoadError(e)) { ctx.Log?.Invoke($"failed to load CGF {cgf.Path}: {e.Message}"); return null; }
        }

        // bake_nmc_into_mesh (use_root_inv = false。位置と法線)
        private static void Bake(ShipMeshes.Mesh mesh, List<ShipMeshes.NmcNode>? nmc)
        {
            if (nmc == null || nmc.Count == 0) return;
            var world = ShipMeshes.NmcWorld(nmc);
            int nv = mesh.N;
            var vn = new int[nv]; Array.Fill(vn, -1);
            foreach (var s in mesh.Subs)
            {
                int node = s.Node;
                if (node >= world.Length) continue;
                long st = s.First, en = Math.Min(st + s.Num, mesh.Idx.Length);
                if (st > en) throw new InvalidDataException("submesh の索引の範囲が外れています");
                for (long i = st; i < en; i++) { long v = mesh.Idx[i]; if (v < nv && vn[v] < 0) vn[v] = node; }
            }
            var xf = world.Select(w => Glam.Mul(Glam.Identity, w)).ToArray();
            var nmat = new float[world.Length][];
            for (int v = 0; v < nv; v++)
            {
                int node = vn[v];
                if (node < 0) continue;
                var x = xf[node];
                if (Glam.IsIdentity(x)) continue;
                Glam.TransformPoint3(x, mesh.Pos[v * 3], mesh.Pos[v * 3 + 1], mesh.Pos[v * 3 + 2], out mesh.Pos[v * 3], out mesh.Pos[v * 3 + 1], out mesh.Pos[v * 3 + 2]);
                if (mesh.Nrm != null && v < mesh.Nrm.Length / 3)
                {
                    var nm = nmat[node] ??= SocpakGlb.Transpose(SocpakGlb.Inverse(x));
                    float nx = mesh.Nrm[v * 3], ny = mesh.Nrm[v * 3 + 1], nz = mesh.Nrm[v * 3 + 2];
                    float rx = nm[0] * nx, ry = nm[1] * nx, rz = nm[2] * nx;
                    rx = nm[4] * ny + rx; ry = nm[5] * ny + ry; rz = nm[6] * ny + rz;
                    rx = nm[8] * nz + rx; ry = nm[9] * nz + ry; rz = nm[10] * nz + rz;
                    float len = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
                    float rec = 1.0f / len;
                    mesh.Nrm[v * 3] = rx * rec; mesh.Nrm[v * 3 + 1] = ry * rec; mesh.Nrm[v * 3 + 2] = rz * rec;
                }
            }
        }

        // finalize・serialize_glb
        public void Finalize(List<int> scene, List<ShipInteriors.Light> lights, Stream output)
        {
            int root = Nodes.Count;
            Nodes.Add(new Node { Name = "CryEngine_Z_up", Children = scene, Matrix = [1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1] });
            var sb = new StringBuilder("{");
            if (Accessors.Count > 0) sb.Append("\"accessors\":[").AppendJoin(',', Accessors).Append("],");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            sb.Append("\"asset\":{\"extras\":{\"export_options\":{\"format\":\"Glb\",\"include_attachments\":true,\"include_interior\":true,\"lod_level\":")
              .Append(ShipExport.Lod).Append(",\"material_mode\":\"Textures\",\"texture_mip\":").Append(ShipExport.Mip).Append("},\"export_timestamp_unix\":").Append(ts)
              .Append(",\"generator_version\":\"").Append(GeneratorVersion).Append("\"},\"generator\":\"starbreaker-gltf\",\"version\":\"2.0\"},");
            sb.Append("\"buffers\":[{\"byteLength\":").Append(Bin.Len).Append("}],");
            if (Views.Count > 0) sb.Append("\"bufferViews\":[").AppendJoin(',', Views).Append("],");
            sb.Append("\"scene\":0,");
            var used = new List<string>();
            if (AnyMaterialExtension) { used.Add("KHR_materials_transmission"); used.Add("KHR_materials_ior"); }
            if (lights.Count > 0)
            {
                used.Add("KHR_lights_punctual");
                sb.Append("\"extensions\":{\"KHR_lights_punctual\":{\"lights\":[");
                for (int i = 0; i < lights.Count; i++)
                {
                    var l = lights[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"color\":").Append(A32(l.Color)).Append(",\"intensity\":").Append(J32(l.Intensity)).Append(",\"name\":");
                    RustJson.Str(sb, l.Name);
                    sb.Append(",\"range\":").Append(J32(l.Radius));
                    if (l.Inner is float inner && l.Outer is float outer)
                        sb.Append(",\"spot\":{\"innerConeAngle\":").Append(J32(ShipInteriors.ToRadians(inner))).Append(",\"outerConeAngle\":").Append(J32(ShipInteriors.ToRadians(outer))).Append("},\"type\":\"spot\"}");
                    else sb.Append(",\"type\":\"point\"}");
                }
                sb.Append("]}},");
            }
            if (used.Count > 0) sb.Append("\"extensionsUsed\":[").AppendJoin(',', used.Select(u => $"\"{u}\"")).Append("],");
            if (Images.Count > 0) sb.Append("\"images\":[").AppendJoin(',', Images).Append("],");
            if (Materials.Count > 0) sb.Append("\"materials\":[").AppendJoin(',', Materials).Append("],");
            if (Meshes.Count > 0) sb.Append("\"meshes\":[").AppendJoin(',', Meshes).Append("],");
            sb.Append("\"nodes\":[");
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                bool first = true;
                void Sep() { if (!first) sb.Append(','); first = false; }
                if (n.Children != null) { Sep(); sb.Append("\"children\":[").AppendJoin(',', n.Children).Append(']'); }
                if (n.Extensions != null) { Sep(); sb.Append("\"extensions\":").Append(n.Extensions); }
                if (n.Extras != null) { Sep(); sb.Append("\"extras\":").Append(n.Extras); }
                if (n.Matrix != null) { Sep(); sb.Append("\"matrix\":").Append(A32(n.Matrix)); }
                if (n.Mesh is int mi) { Sep(); sb.Append("\"mesh\":").Append(mi); }
                if (n.Name != null) { Sep(); sb.Append("\"name\":"); RustJson.Str(sb, n.Name); }
                if (n.Rotation != null) { Sep(); sb.Append("\"rotation\":").Append(A32(n.Rotation)); }
                if (n.Translation != null) { Sep(); sb.Append("\"translation\":").Append(A32(n.Translation)); }
                sb.Append('}');
            }
            sb.Append("],");
            if (HasSampler) sb.Append("\"samplers\":[{\"magFilter\":9729,\"minFilter\":9729,\"wrapS\":10497,\"wrapT\":10497}],");
            sb.Append("\"scenes\":[{\"nodes\":[").Append(root).Append("]}]");
            if (TexturesJson.Count > 0) sb.Append(",\"textures\":[").AppendJoin(',', TexturesJson).Append(']');
            sb.Append('}');
            var json = Encoding.UTF8.GetBytes(sb.ToString());
            int jpad = (4 - json.Length % 4) % 4, bpad = (4 - Bin.Len % 4) % 4;
            long total = 12L + 8 + json.Length + jpad + 8 + Bin.Len + bpad;
            if (total > uint.MaxValue) throw new InvalidDataException("GLB が大きすぎます (4 GB 以上)");
            Span<byte> h = stackalloc byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(h, 0x46546C67); BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 2); BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)(json.Length + jpad)); BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 0x4E4F534A);
            output.Write(h);
            output.Write(json);
            for (int i = 0; i < jpad; i++) output.WriteByte((byte)' ');
            Span<byte> bh = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(bh, (uint)(Bin.Len + bpad)); BinaryPrimitives.WriteUInt32LittleEndian(bh[4..], 0x004E4942);
            output.Write(bh);
            Bin.WriteTo(output);
            for (int i = 0; i < bpad; i++) output.WriteByte(0);
        }
    }
}
