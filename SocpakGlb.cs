using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// socpak の GLB の書き出し (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」)。
// StarBreaker v0.2.2 の `socpak export <PATTERN> <out.glb> --materials none --no-lights [--lod N]` と同じ GLB を書く
// (ステーションの外の 3D (Station3DExports) が使う。ビューアが読むので、位置・法線・UV・索引・材質 (extras 付き)・ノードを starbreaker と同じにする)。
// 移植元: pipeline.rs (socpaks_to_glb・build_interiors_from_payloads・export_cgf_from_path・bake_nmc_into_mesh・query_tint_from_path)、
//   gltf/mod.rs・gltf/glb_builder.rs (attach_interiors・pack_mesh・build_materials・build_material・finalize・serialize_glb)、
//   types.rs (build_mesh_with_bbox)、dequant.rs、mtl.rs、ivo/skin.rs。JSON の書き方は gltf-json 1.4.1 (names・extras) と serde_json 1.0.149 (zmij)
internal static class SocpakGlb
{
    public const string GeneratorVersion = "0.2.2";

    // ===== 材質 (mtl.rs SubMaterial の全部) =====
    public sealed class Layer { public string Path = ""; public float[] Tint = [1, 1, 1]; public byte PaletteTint; public float UvTiling = 1; }
    public sealed class SubMat
    {
        public string Name = "", Shader = "", SurfaceType = "", StringGenMask = "";
        public float[] Diffuse = [1, 1, 1], Specular = [0.04f, 0.04f, 0.04f], Emissive = [0, 0, 0];
        public float Shininess = 128, Glow = 0, Opacity = 1, AlphaTest = 0;
        public bool IsNoDraw;
        public string? DiffuseTex, NormalTex;
        public List<Layer> Layers = new();
        public byte PaletteTint;
        public bool IsDecal => StringGenMask.Contains("%DECAL") || Shader == "MeshDecal";
        public bool IsGlass => Shader == "GlassPBR";
        public bool ShouldHide()
        {
            if (IsNoDraw) return true;
            var m = StringGenMask;
            if (m.Contains("%PARALLAX_OCCLUSION_MAPPING") && (IsDecal || m.Contains("%DECAL"))) return true;
            if (IsDecal && DiffuseTex == null && (m.Contains("STENCIL_MAP") || !m.Contains("%VERTCOLORS"))) return true;
            return false;
        }
        // alpha_config: 0 Opaque、1 Mask、2 Blend
        public (int Mode, float Cutoff) AlphaConfig()
        {
            if (IsNoDraw) return (2, 0);
            if (StringGenMask.Contains("%DECAL") || Shader == "MeshDecal") return (2, 0);
            if (AlphaTest > 0.0f) return (1, AlphaTest);
            if (Opacity < 1.0f) return (2, 0);
            return (0, 0);
        }
        public float Roughness()
        {
            float smooth = Math.Clamp(Shininess / 255.0f, 0.0f, 1.0f);
            float r = 1.0f - smooth;
            var s = Shader.ToLowerInvariant();
            bool perPixel = s.Contains("hardsurface") || s.Contains("layerblend") || s == "illum" || s == "glasspbr";
            return r == 0.0f && perPixel ? 0.5f : r;
        }
        public float Metallic()
        {
            var n = Name.ToLowerInvariant();
            return n.Contains("chrome") || n.Contains("raw_metal") || n.Contains("raw_iron") || n == "tile_metal_bare" ? 1.0f : 0.0f;
        }
        public float[]? MetalBaseColor() => Metallic() > 0.5f ? Specular : null;
        public float[] EmissiveFactor()
        {
            var e = Emissive;
            if (e[0] > 0.0f || e[1] > 0.0f || e[2] > 0.0f) { float g = Glow > 0.0f ? Glow : 1.0f; return [e[0] * g, e[1] * g, e[2] * g]; }
            return [0, 0, 0];
        }
        public bool DoubleSided => StringGenMask.Contains("%DECAL") || Shader == "MeshDecal";
    }
    public sealed class MtlFile { public List<SubMat> Materials = new(); public string? SourcePath; }

    // Rust の str::parse::<f32>
    private static bool ParseF32(string s, out float v)
    {
        v = 0;
        if (!RustF64.TryParse(s, out _)) return false;
        var t = s.ToLowerInvariant().TrimStart('+', '-');
        if (t is "inf" or "infinity") { v = s.StartsWith('-') ? float.NegativeInfinity : float.PositiveInfinity; return true; }
        if (t == "nan") { v = float.NaN; return true; }
        return float.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out v);
    }
    private static bool ParseU8(string s, out byte v)
    {
        v = 0;
        var t = s.StartsWith('+') ? s[1..] : s;
        if (t.Length == 0 || !t.All(char.IsAsciiDigit)) return false;
        return byte.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out v);
    }
    private static float[] ParseRgb(string s)
    {
        var parts = new List<float>();
        foreach (var p in s.Split(',')) if (ParseF32(p.Trim(), out var f)) parts.Add(f);
        return parts.Count >= 3 ? [parts[0], parts[1], parts[2]] : [1, 1, 1];
    }

    public static MtlFile ParseMtl(byte[] data)
    {
        var x = CryXmlB.Parse(data);
        var res = new MtlFile();
        int sub = -1;
        foreach (var c in x.Children(x.Root)) if (x.Tag(c) == "SubMaterials") { sub = c; break; }
        if (sub >= 0) { foreach (var c in x.Children(sub)) if (x.Tag(c) == "Material") res.Materials.Add(ParseSub(x, c)); }
        else res.Materials.Add(ParseSub(x, x.Root));
        return res;
    }

    private static SubMat ParseSub(CryXmlB x, int node)
    {
        var m = new SubMat();
        foreach (var (k, v) in x.Attributes(node))
        {
            switch (k)
            {
                case "Name": m.Name = v; break;
                case "Shader": m.Shader = v; if (v == "NoDraw") m.IsNoDraw = true; break;
                case "Diffuse": m.Diffuse = ParseRgb(v); break;
                case "Specular": m.Specular = ParseRgb(v); break;
                case "Shininess": m.Shininess = ParseF32(v, out var sh) ? sh : 128.0f; break;
                case "Emissive": m.Emissive = ParseRgb(v); break;
                case "GlowAmount": m.Glow = ParseF32(v, out var gl) ? gl : 0.0f; break;
                case "SurfaceType": m.SurfaceType = v; break;
                case "Opacity": m.Opacity = ParseF32(v, out var op) ? op : 1.0f; break;
                case "AlphaTest": m.AlphaTest = ParseF32(v, out var at) ? at : 0.0f; break;
                case "StringGenMask": m.StringGenMask = v; break;
            }
        }
        foreach (var c in x.Children(node))
        {
            var tag = x.Tag(c);
            if (tag == "Textures")
            {
                foreach (var t in x.Children(c))
                {
                    if (x.Tag(t) != "Texture") continue;
                    string slot = ""; string? file = null;
                    foreach (var (k, v) in x.Attributes(t)) { if (k == "Map") slot = v; else if (k == "File") file = v; }
                    if (file == null || file.StartsWith('$')) continue;
                    if (slot == "TexSlot1") m.DiffuseTex = file;
                    else if (slot == "TexSlot2") m.NormalTex = file;
                }
            }
            else if (tag == "MatLayers")
            {
                foreach (var l in x.Children(c))
                {
                    var layer = new Layer();
                    byte lpal = 0;
                    foreach (var (k, v) in x.Attributes(l))
                    {
                        switch (k)
                        {
                            case "Path" when v.Length > 0: layer.Path = v; break;
                            case "TintColor": layer.Tint = ParseRgb(v); break;
                            case "PaletteTint": lpal = ParseU8(v, out var pt) ? pt : (byte)0; break;
                            case "UVTiling": layer.UvTiling = ParseF32(v, out var uv) ? uv : 1.0f; break;
                        }
                    }
                    layer.PaletteTint = lpal;
                    if (layer.Path.Length > 0) m.Layers.Add(layer);
                    if (lpal > 0 && m.PaletteTint == 0) m.PaletteTint = lpal;
                }
            }
        }
        return m;
    }

    // ===== 形 (build_mesh_with_bbox の全部: 位置・UV・法線・submesh) =====
    public sealed class FullMesh
    {
        public float[] Pos = [];
        public float[]? Uv;
        public float[] Nrm = [];
        public uint[] Idx = [];
        public List<(uint MatId, uint First, uint Num, ushort Node, string? MatName)> Subs = new();
        public float[] ModelMin = new float[3], ModelMax = new float[3], ScaleMin = new float[3], ScaleMax = new float[3];
        public int N => Pos.Length / 3;
    }

    public static FullMesh ParseSkinFull(byte[] data)
    {
        var cf = CryChunkFile.Parse(data);
        if (!cf.IsIvo) throw new InvalidDataException("CrCh の形には対応していません (UnsupportedFormat)");
        var sc = cf.Chunks.FirstOrDefault(c => c.Type == CryChunkFile.IvoSkin2);
        if (sc.Type != CryChunkFile.IvoSkin2) throw new InvalidDataException("IvoSkin2 のチャンクがありません");
        // MtlName のチャンク (並びの順。128 バイト、0 まで、1 バイト 1 文字)。submesh の material_name に使う
        var names = new List<string>();
        foreach (var c in cf.Chunks)
        {
            if (c.Type != CryChunkFile.MtlNameIvo320) continue;
            var d = cf.ChunkData(c);
            if (d.Length < 128) throw new InvalidDataException("MtlName のチャンクが短すぎます");
            var s = d[..128]; int z = s.IndexOf((byte)0);
            names.Add(Encoding.Latin1.GetString(z < 0 ? s : s[..z]));
        }
        var m = ReadSkinFull(cf.ChunkData(sc), names);
        return m;
    }

    private static FullMesh ReadSkinFull(ReadOnlySpan<byte> data, List<string> matNames)
    {
        var r = new SpanReader(data);
        r.U32();
        r.U32();
        uint numVerts = r.U32(), numIdx = r.U32(), numSub = r.U32();
        r.U32();
        var m = new FullMesh();
        for (int i = 0; i < 3; i++) m.ModelMin[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ModelMax[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ScaleMin[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ScaleMax[i] = r.F32();
        r.U32();
        uint extra = r.U32();
        r.Advance(88);
        r.Bytes((long)numSub * 48); r.Position -= (int)(numSub * 48);
        var subs = new (ushort Mat, ushort Node, uint First, uint Num, uint PageBase)[numSub];
        for (int i = 0; i < numSub; i++)
        {
            ushort mat = r.U16(), node = r.U16();
            uint first = r.U32(), num = r.U32(); r.U32(); uint pb = r.U32();
            r.Advance(28);
            subs[i] = (mat, node, first, num, pb);
        }
        r.Advance((long)extra * 4);
        long nv = numVerts, ni = numIdx;
        ushort[]? qpos = null; float[]? fpos = null; ushort[]? uvRaw = null; uint[]? idx = null;
        uint[]? packedN = null; float[]? floatN = null; ushort[]? tan = null; bool tanIsQ = false;
        while (r.Remaining >= 4)
        {
            uint st = r.U32();
            if (st == 0) continue;
            if (r.Remaining < 4) break;
            uint es = r.U32();
            int start = r.Position;
            switch (st)
            {
                case 0x91329AE9: case 0xB3A70D5E:
                    if (es == 16)
                    {
                        var b = r.Bytes(nv * 16);
                        qpos = new ushort[nv * 3]; uvRaw = new ushort[nv * 2]; fpos = null;
                        for (int v = 0; v < nv; v++)
                        {
                            for (int k = 0; k < 3; k++) qpos[v * 3 + k] = BinaryPrimitives.ReadUInt16LittleEndian(b[(v * 16 + k * 2)..]);
                            uvRaw[v * 2] = BinaryPrimitives.ReadUInt16LittleEndian(b[(v * 16 + 12)..]); uvRaw[v * 2 + 1] = BinaryPrimitives.ReadUInt16LittleEndian(b[(v * 16 + 14)..]);
                        }
                    }
                    else if (es == 20)
                    {
                        var b = r.Bytes(nv * 20);
                        fpos = new float[nv * 3]; uvRaw = new ushort[nv * 2]; qpos = null;
                        for (int v = 0; v < nv; v++)
                        {
                            for (int k = 0; k < 3; k++) fpos[v * 3 + k] = BinaryPrimitives.ReadSingleLittleEndian(b[(v * 20 + k * 4)..]);
                            uvRaw[v * 2] = BinaryPrimitives.ReadUInt16LittleEndian(b[(v * 20 + 16)..]); uvRaw[v * 2 + 1] = BinaryPrimitives.ReadUInt16LittleEndian(b[(v * 20 + 18)..]);
                        }
                    }
                    else throw new InvalidDataException($"頂点の要素の大きさ {es} には対応していません");
                    break;
                case 0xEECDC168:
                    if (es == 2) { var b = r.Bytes(ni * 2); idx = new uint[ni]; for (int i = 0; i < ni; i++) idx[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(i * 2)..]); }
                    else { var b = r.Bytes(ni * 4); idx = new uint[ni]; for (int i = 0; i < ni; i++) idx[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(i * 4)..]); }
                    break;
                case 0x9CF3F615:
                    { var b = r.Bytes(nv * 4); packedN = new uint[nv]; floatN = null; for (int i = 0; i < nv; i++) packedN[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(i * 4)..]); break; }
                case 0x38A581FE:
                    if (es == 12) { var b = r.Bytes(nv * 12); floatN = new float[nv * 3]; packedN = null; for (int i = 0; i < nv * 3; i++) floatN[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(i * 4)..]); }
                    else r.Advance((long)es * nv);
                    break;
                case 0xEE057252: case 0xB95E9A1B:
                    { var b = r.Bytes(nv * 8); tan = new ushort[nv * 4]; tanIsQ = st == 0xEE057252; for (int i = 0; i < nv * 4; i++) tan[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(i * 2)..]); break; }
                default:
                    long skip = (long)es * nv;
                    if (r.Remaining >= skip) r.Advance(skip); else goto done;
                    break;
            }
            int rem = (r.Position - start) % 8;
            if (rem != 0 && r.Remaining >= 8 - rem) r.Advance(8 - rem);
        }
    done:
        if (qpos == null && fpos == null) throw new InvalidDataException("頂点のデータがありません (IVOVERTSUVS)");
        if (idx == null) throw new InvalidDataException("索引のデータがありません (IVOINDICES)");
        if (qpos != null)
        {
            fpos = new float[nv * 3];
            for (int v = 0; v < nv; v++)
                for (int k = 0; k < 3; k++)
                {
                    float sn = (short)qpos[v * 3 + k] / 32767.0f;
                    float half = RustF32.Max((m.ScaleMax[k] - m.ScaleMin[k]) / 2.0f, 1.0f);
                    float center = (m.ScaleMax[k] + m.ScaleMin[k]) / 2.0f;
                    fpos[v * 3 + k] = sn * half + center;
                }
        }
        m.Pos = fpos!;
        if (uvRaw != null && uvRaw.Length > 0)
        {
            m.Uv = new float[uvRaw.Length];
            for (int i = 0; i < uvRaw.Length; i++) m.Uv[i] = (float)BitConverter.UInt16BitsToHalf(uvRaw[i]);
        }
        foreach (var s in subs)
        {
            if (s.PageBase == 0) continue;
            long st = s.First, en = st + s.Num;
            if (en > idx.Length) throw new InvalidDataException("submesh の索引の範囲が外れています");
            for (long i = st; i < en; i++) idx[i] += s.PageBase;
        }
        m.Idx = idx;
        foreach (var s in subs) m.Subs.Add((s.Mat, s.First, s.Num, s.Node, s.Mat < matNames.Count ? matNames[s.Mat] : null));
        // 法線: IVONORMALS2 → IVONORMALS → 接線の流れ → 面から計算
        if (floatN != null) m.Nrm = floatN;
        else if (packedN != null) { m.Nrm = new float[nv * 3]; for (int i = 0; i < nv; i++) { var u = PackedUnit(packedN[i]); m.Nrm[i * 3] = u.X; m.Nrm[i * 3 + 1] = u.Y; m.Nrm[i * 3 + 2] = u.Z; } }
        else if (tan != null)
        {
            m.Nrm = new float[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                var n = tanIsQ ? CompressedTangentNormal(tan[i * 4], tan[i * 4 + 1], tan[i * 4 + 2], tan[i * 4 + 3]) : QTangentNormal(tan[i * 4], tan[i * 4 + 1], tan[i * 4 + 2], tan[i * 4 + 3]);
                m.Nrm[i * 3] = n.X; m.Nrm[i * 3 + 1] = n.Y; m.Nrm[i * 3 + 2] = n.Z;
            }
        }
        else m.Nrm = SmoothNormals(m.Pos, m.Idx);
        return m;
    }

    private static (float X, float Y, float Z) PackedUnit(uint val)
    {
        const float Scale = 1.0f / 16383.0f;
        float x = ((int)(val & 0x7FFF) - 0x3FFF) * Scale;
        float y = ((int)((val >> 15) & 0x7FFF) - 0x3FFF) * Scale;
        float zsq = RustF32.Max(1.0f - x * x - y * y, 0.0f);
        float zu = MathF.Sqrt(zsq);
        float z = ((val >> 30) & 1) != 0 ? -zu : zu;
        float len = MathF.Sqrt(x * x + y * y + z * z);
        return len > 1e-8f ? (x / len, y / len, z / len) : (0, 0, 1);
    }

    private static (float X, float Y, float Z) CompressedTangentNormal(ushort a, ushort b, ushort c, ushort d)
    {
        uint v1 = a | ((uint)b << 16), v2 = c | ((uint)d << 16);
        var p = PackedUnit(v1); var q = PackedUnit(v2);
        float sign = (v1 & 0x80000000) != 0 ? -1.0f : 1.0f;
        float cx = p.Y * q.Z - p.Z * q.Y, cy = p.Z * q.X - p.X * q.Z, cz = p.X * q.Y - p.Y * q.X;
        float nx = cx * sign, ny = cy * sign, nz = cz * sign;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        return len > 1e-8f ? (nx / len, ny / len, nz / len) : (0, 0, 1);
    }

    private static (float X, float Y, float Z) QTangentNormal(ushort a, ushort b, ushort c, ushort d)
    {
        float qx = (short)a / 32767.0f, qy = (short)b / 32767.0f, qz = (short)c / 32767.0f, qw = (short)d / 32767.0f;
        float nx = 2.0f * (qx * qz + qw * qy), ny = 2.0f * (qy * qz - qw * qx), nz = 1.0f - 2.0f * (qx * qx + qy * qy);
        if (qw < 0.0f) { nx = -nx; ny = -ny; nz = -nz; }
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        return len > 0.0f ? (nx / len, ny / len, nz / len) : (0, 0, 1);
    }

    private static float[] SmoothNormals(float[] pos, uint[] idx)
    {
        int n = pos.Length / 3;
        var o = new float[n * 3];
        for (int t = 0; t + 2 < idx.Length || (t < idx.Length && false); t += 3)
        {
            long i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
            if (i0 >= n || i1 >= n || i2 >= n) continue;
            float e1x = pos[i1 * 3] - pos[i0 * 3], e1y = pos[i1 * 3 + 1] - pos[i0 * 3 + 1], e1z = pos[i1 * 3 + 2] - pos[i0 * 3 + 2];
            float e2x = pos[i2 * 3] - pos[i0 * 3], e2y = pos[i2 * 3 + 1] - pos[i0 * 3 + 1], e2z = pos[i2 * 3 + 2] - pos[i0 * 3 + 2];
            float fx = e1y * e2z - e1z * e2y, fy = e1z * e2x - e1x * e2z, fz = e1x * e2y - e1y * e2x;
            foreach (var vi in new[] { i0, i1, i2 }) { o[vi * 3] += fx; o[vi * 3 + 1] += fy; o[vi * 3 + 2] += fz; }
        }
        for (int i = 0; i < n; i++)
        {
            float x = o[i * 3], y = o[i * 3 + 1], z = o[i * 3 + 2];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (len > 1e-8f) { o[i * 3] = x / len; o[i * 3 + 1] = y / len; o[i * 3 + 2] = z / len; }
            else { o[i * 3] = 0; o[i * 3 + 1] = 0; o[i * 3 + 2] = 1; }
        }
        return o;
    }

    // merge_from (位置・UV・法線・索引・submesh・箱)
    private static void Merge(FullMesh a, FullMesh b)
    {
        uint vOff = (uint)a.N, iOff = (uint)a.Idx.Length;
        a.Pos = [.. a.Pos, .. b.Pos];
        a.Uv = a.Uv != null && b.Uv != null ? [.. a.Uv, .. b.Uv] : a.Uv;
        a.Nrm = [.. a.Nrm, .. b.Nrm];
        a.Idx = [.. a.Idx, .. b.Idx.Select(i => i + vOff)];
        foreach (var s in b.Subs) a.Subs.Add((s.MatId, s.First + iOff, s.Num, s.Node, s.MatName));
        for (int i = 0; i < 3; i++)
        {
            a.ModelMin[i] = RustF32.Min(a.ModelMin[i], b.ModelMin[i]); a.ModelMax[i] = RustF32.Max(a.ModelMax[i], b.ModelMax[i]);
            a.ScaleMin[i] = RustF32.Min(a.ScaleMin[i], b.ScaleMin[i]); a.ScaleMax[i] = RustF32.Max(a.ScaleMax[i], b.ScaleMax[i]);
        }
    }

    // transform_mesh_by_bone (位置・法線・model の箱)
    private static void ByBone(FullMesh mesh, CryIvo.Bone bone)
    {
        var cm = new CryMesh { Positions = mesh.Pos, ModelMin = mesh.ModelMin, ModelMax = mesh.ModelMax };
        CryIvo.TransformByBone(cm, bone);
        mesh.Pos = cm.Positions; mesh.ModelMin = cm.ModelMin; mesh.ModelMax = cm.ModelMax;
        var m3 = Glam.QuatToMat3(bone.Rotation[1], bone.Rotation[2], bone.Rotation[3], bone.Rotation[0]);
        for (int v = 0; v < mesh.N; v++)
        {
            float x = mesh.Nrm[v * 3], y = mesh.Nrm[v * 3 + 1], z = mesh.Nrm[v * 3 + 2];
            mesh.Nrm[v * 3] = m3[0] * x + m3[3] * y + m3[6] * z;
            mesh.Nrm[v * 3 + 1] = m3[1] * x + m3[4] * y + m3[7] * z;
            mesh.Nrm[v * 3 + 2] = m3[2] * x + m3[5] * y + m3[8] * z;
        }
    }

    // bake_nmc_into_mesh (use_root_inv = false)。法線は (xform の逆行列の転置) で移して正規化 (glam の SSE2 の inverse・transpose)
    private static void Bake(FullMesh mesh, List<CryIvo.NmcNode>? nmc)
    {
        if (nmc == null || nmc.Count == 0) return;
        var world = CryIvo.NmcWorld(nmc);
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
            var nm = nmat[node] ??= Transpose(Inverse(x));
            float nx = mesh.Nrm[v * 3], ny = mesh.Nrm[v * 3 + 1], nz = mesh.Nrm[v * 3 + 2];
            float rx = nm[0] * nx, ry = nm[1] * nx, rz = nm[2] * nx;
            rx = nm[4] * ny + rx; ry = nm[5] * ny + ry; rz = nm[6] * ny + rz;
            rx = nm[8] * nz + rx; ry = nm[9] * nz + ry; rz = nm[10] * nz + rz;
            float len = MathF.Sqrt(rx * rx + ry * ry + rz * rz);
            float rec = 1.0f / len;
            mesh.Nrm[v * 3] = rx * rec; mesh.Nrm[v * 3 + 1] = ry * rec; mesh.Nrm[v * 3 + 2] = rz * rec;
        }
    }

    private static Vector128<float> Col(float[] m, int c) => Vector128.Create(m[c * 4], m[c * 4 + 1], m[c * 4 + 2], m[c * 4 + 3]);

    // glam 0.29.3 f32/sse2/mat4.rs Mat4::inverse (glm の glm_mat4_inverse) と同じ命令の並び
    internal static float[] Inverse(float[] m)
    {
        if (!Sse.IsSupported) throw new PlatformNotSupportedException("SSE が使えません");
        var x = Col(m, 0); var y = Col(m, 1); var z = Col(m, 2); var w = Col(m, 3);
        // (2026-11-04) シャッフルの指定を定数で書く (CA1857 の警告をなくすため。計算の並びは前のローカル関数 Fac と同じ)
        static Vector128<float> Fac(Vector128<float> s0a, Vector128<float> s0b, Vector128<float> s00, Vector128<float> s03)
        {
            var s01 = Sse.Shuffle(s0a, s0a, 0b10_00_00_00); var s02 = Sse.Shuffle(s0b, s0b, 0b10_00_00_00);
            return Sse.Subtract(Sse.Multiply(s00, s01), Sse.Multiply(s02, s03));
        }
        var fac0 = Fac(Sse.Shuffle(w, z, 0b11_11_11_11), Sse.Shuffle(w, z, 0b10_10_10_10), Sse.Shuffle(z, y, 0b10_10_10_10), Sse.Shuffle(z, y, 0b11_11_11_11));
        var fac1 = Fac(Sse.Shuffle(w, z, 0b11_11_11_11), Sse.Shuffle(w, z, 0b01_01_01_01), Sse.Shuffle(z, y, 0b01_01_01_01), Sse.Shuffle(z, y, 0b11_11_11_11));
        var fac2 = Fac(Sse.Shuffle(w, z, 0b10_10_10_10), Sse.Shuffle(w, z, 0b01_01_01_01), Sse.Shuffle(z, y, 0b01_01_01_01), Sse.Shuffle(z, y, 0b10_10_10_10));
        var fac3 = Fac(Sse.Shuffle(w, z, 0b11_11_11_11), Sse.Shuffle(w, z, 0b00_00_00_00), Sse.Shuffle(z, y, 0b00_00_00_00), Sse.Shuffle(z, y, 0b11_11_11_11));
        var fac4 = Fac(Sse.Shuffle(w, z, 0b10_10_10_10), Sse.Shuffle(w, z, 0b00_00_00_00), Sse.Shuffle(z, y, 0b00_00_00_00), Sse.Shuffle(z, y, 0b10_10_10_10));
        var fac5 = Fac(Sse.Shuffle(w, z, 0b01_01_01_01), Sse.Shuffle(w, z, 0b00_00_00_00), Sse.Shuffle(z, y, 0b00_00_00_00), Sse.Shuffle(z, y, 0b01_01_01_01));
        var signA = Vector128.Create(-1.0f, 1.0f, -1.0f, 1.0f);   // _mm_set_ps(1, -1, 1, -1) は下位から -1, 1, -1, 1
        var signB = Vector128.Create(1.0f, -1.0f, 1.0f, -1.0f);
        var t0 = Sse.Shuffle(y, x, 0); var vec0 = Sse.Shuffle(t0, t0, 0b10_10_10_00);
        var t1 = Sse.Shuffle(y, x, 0b01_01_01_01); var vec1 = Sse.Shuffle(t1, t1, 0b10_10_10_00);
        var t2 = Sse.Shuffle(y, x, 0b10_10_10_10); var vec2 = Sse.Shuffle(t2, t2, 0b10_10_10_00);
        var t3 = Sse.Shuffle(y, x, 0b11_11_11_11); var vec3 = Sse.Shuffle(t3, t3, 0b10_10_10_00);
        var inv0 = Sse.Multiply(signB, Sse.Add(Sse.Subtract(Sse.Multiply(vec1, fac0), Sse.Multiply(vec2, fac1)), Sse.Multiply(vec3, fac2)));
        var inv1 = Sse.Multiply(signA, Sse.Add(Sse.Subtract(Sse.Multiply(vec0, fac0), Sse.Multiply(vec2, fac3)), Sse.Multiply(vec3, fac4)));
        var inv2 = Sse.Multiply(signB, Sse.Add(Sse.Subtract(Sse.Multiply(vec0, fac1), Sse.Multiply(vec1, fac3)), Sse.Multiply(vec3, fac5)));
        var inv3 = Sse.Multiply(signA, Sse.Add(Sse.Subtract(Sse.Multiply(vec0, fac2), Sse.Multiply(vec1, fac4)), Sse.Multiply(vec2, fac5)));
        var row0 = Sse.Shuffle(inv0, inv1, 0); var row1 = Sse.Shuffle(inv2, inv3, 0); var row2 = Sse.Shuffle(row0, row1, 0b10_00_10_00);
        // dot4: (x*x' + z*z') + (y*y' + w*w')
        var p = Sse.Multiply(x, row2);
        var hl = Sse.MoveHighToLow(p, p);
        var s = Sse.Add(p, hl);
        var s1 = Sse.Shuffle(s, s, 0b00_00_00_01);
        float dot0 = Sse.Add(s, s1).ToScalar();
        var rcp = Vector128.Create(1.0f / dot0);
        var r = new float[16];
        void Put(int c, Vector128<float> v) { for (int k = 0; k < 4; k++) r[c * 4 + k] = v.GetElement(k); }
        Put(0, Sse.Multiply(inv0, rcp)); Put(1, Sse.Multiply(inv1, rcp)); Put(2, Sse.Multiply(inv2, rcp)); Put(3, Sse.Multiply(inv3, rcp));
        return r;
    }

    internal static float[] Transpose(float[] m)
    {
        var r = new float[16];
        for (int c = 0; c < 4; c++) for (int k = 0; k < 4; k++) r[c * 4 + k] = m[k * 4 + c];
        return r;
    }

    // ===== 区画・部品の読み取り (SocpakExporter と同じ流れで、材質・パレットも持つ) =====
    private sealed record Cgf(string Path, string? Mtl, string Name);

    private sealed record Loaded(FullMesh Mesh, MtlFile? Mtl);

    private static Loaded LoadCgf(SocpakExporter.Context ctx, Cgf cgf)
    {
        var p4k = ctx.P4k;
        var clean = cgf.Path.Replace('\\', '/');
        var geometryPath = clean.StartsWith("data/", StringComparison.Ordinal) || clean.StartsWith("Data/", StringComparison.Ordinal) ? clean[5..] : clean;
        var materialPath = cgf.Mtl ?? "";
        var parts = new List<(string Path, string? Bone, string? MtlOverride)>();
        string? skeletonPath = null;
        if (!geometryPath.ToLowerInvariant().EndsWith(".cdf", StringComparison.Ordinal)) parts.Add((geometryPath, null, null));
        else
        {
            var cdfP = SocpakExporter.DataCorePathToP4k(geometryPath);
            var ce = p4k.Find(cdfP);
            if (ce < 0) throw new SocpakExporter.MeshLoadException($"file not found in P4k: {cdfP}");
            CryXmlB x;
            try { x = CryXmlB.Parse(p4k.Read(ce)); }
            catch (InvalidDataException e) { throw new SocpakExporter.MeshLoadException($"Failed to parse CDF {geometryPath}: {e.Message}"); }
            foreach (var c in x.Children(x.Root))
            {
                if (x.Tag(c) == "Model" && x.AttributeMap(c).TryGetValue("File", out var f) && f.Length > 0) skeletonPath = f;
                if (x.Tag(c) == "AttachmentList")
                    foreach (var a in x.Children(c))
                    {
                        if (x.Tag(a) != "Attachment") continue;
                        var am = x.AttributeMap(a);
                        if (am.TryGetValue("Binding", out var bind) && bind.Length > 0)
                            parts.Add((bind, am.GetValueOrDefault("BoneName"), am.TryGetValue("Material", out var mo) && mo.Length > 0 ? mo : null));
                    }
            }
            if (parts.Count == 0) throw new SocpakExporter.MeshLoadException($"CDF {geometryPath} has no attachments");
        }
        var bones = new List<CryIvo.Bone>();
        if (skeletonPath != null && p4k.Find(SocpakExporter.DataCorePathToP4k(skeletonPath)) is int se && se >= 0)
            try { bones = CryIvo.ParseSkeleton(p4k.Read(se)) ?? new(); } catch (InvalidDataException) { }
        var (mesh, mtl, nmc) = LoadSingle(ctx, parts[0].Path, parts[0].MtlOverride ?? materialPath);
        for (int i = 1; i < parts.Count; i++)
        {
            try
            {
                var (extra, _, _) = LoadSingle(ctx, parts[i].Path, materialPath);
                if (parts[i].Bone is string bn && bones.FirstOrDefault(b => string.Equals(b.Name, bn, StringComparison.OrdinalIgnoreCase)) is { } bone) ByBone(extra, bone);
                Merge(mesh, extra);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or SocpakExporter.MeshLoadException or ZstdSharp.ZstdException) { }
        }
        bool bake = false;
        for (int k = 0; k < 3; k++)
            if (MathF.Abs(mesh.ScaleMin[k] - mesh.ModelMin[k]) > 0.01f || MathF.Abs(mesh.ScaleMax[k] - mesh.ModelMax[k]) > 0.01f) bake = true;
        if (bake) Bake(mesh, nmc);
        return new Loaded(mesh, mtl);
    }

    private static (FullMesh, MtlFile?, List<CryIvo.NmcNode>?) LoadSingle(SocpakExporter.Context ctx, string geometryPath, string materialPath)
    {
        var p4k = ctx.P4k;
        var geomP4k = SocpakExporter.DataCorePathToP4k(geometryPath);
        var companion = geomP4k + "m";
        if (ctx.Lod > 0)
        {
            int dot = geomP4k.LastIndexOf('.');
            var lodGeom = dot >= 0 ? $"{geomP4k[..dot]}_lod{ctx.Lod}{geomP4k[dot..]}" : $"{geomP4k}_lod{ctx.Lod}";
            if (p4k.Find(lodGeom + "m") >= 0) companion = lodGeom + "m";
        }
        int ce = p4k.Find(companion);
        if (ce < 0) throw new SocpakExporter.MeshLoadException($"file not found in P4k: {companion}");
        var bytes = p4k.Read(ce);
        byte[]? meta = null;
        int me = p4k.Find(geomP4k);
        if (me >= 0) { try { meta = p4k.Read(me); } catch (InvalidDataException) { } }
        MtlFile? mtl = null;
        if (materialPath.Length > 0) mtl = TryMtl(p4k, SocpakExporter.DataCorePathToP4k(materialPath));
        if (mtl == null && meta != null && CryIvo.ExtractMtlName(meta) is string mn)
        {
            string path;
            if (mn.Contains('/') || mn.Contains('\\')) path = $"Data\\{mn.Replace('/', '\\')}.mtl";
            else { int i = geomP4k.LastIndexOf('\\'); path = $"{(i >= 0 ? geomP4k[..i] : geomP4k)}\\{mn}.mtl"; }
            mtl = TryMtl(p4k, path);
        }
        var nmc = meta != null ? CryIvo.ParseNmc(meta) : null;
        return (ParseSkinFull(bytes), mtl, nmc);
    }

    private static MtlFile? TryMtl(P4kArchive p4k, string path)
    {
        int e = p4k.Find(path);
        if (e < 0) return null;
        try { var m = ParseMtl(p4k.Read(e)); m.SourcePath = path; return m; }
        catch (InvalidDataException) { return null; }
    }

    // ===== パレット (TintPaletteTree の root.entryA… の tintColor) =====
    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static float[][]? Palette(DataCoreDb db, string paletteName)
    {
        var shortName = paletteName[(paletteName.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (db.StructIndexByName("TintPaletteTree") is not int tpt) return null;
        DataCoreDb.Record? rec = null;
        foreach (var r in db.Records) if (r.StructIndex == tpt && db.String2(r.NameOffset).ToLowerInvariant().EndsWith(shortName, StringComparison.Ordinal)) { rec = r; break; }
        if (rec is not DataCoreDb.Record pr) return null;
        byte? Q(string path)
        {
            try { return db.QueryFirstByte(db.CompilePath(pr.StructIndex, path, [DataCoreDb.DT.Byte]), pr); }
            catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException) { return null; }
        }
        try { db.CompilePath(pr.StructIndex, "root.entryA.tintColor.r", [DataCoreDb.DT.Byte]); }
        catch (DataCoreDb.QueryException) { return null; }
        if (Q("root.entryA.tintColor.r") == null) return null;
        float[] Rgb(string entry)
        {
            var c = new float[] { 0.5f, 0.5f, 0.5f };
            string[] ch = ["r", "g", "b"];
            for (int i = 0; i < 3; i++) if (Q($"root.{entry}.tintColor.{ch[i]}") is byte v) c[i] = SrgbToLinear(v / 255.0f);
            return c;
        }
        // glassColor は SRGB8 そのもの (r・g・b が欄)。前は「glassColor.tintColor.r」を引いていて引けず、いつも 0.5 だった (2026-11-05、利用者の指示で直した)
        float[] Glass()
        {
            var c = new float[] { 0.5f, 0.5f, 0.5f };
            string[] ch = ["r", "g", "b"];
            for (int i = 0; i < 3; i++) if (Q($"root.glassColor.{ch[i]}") is byte v) c[i] = SrgbToLinear(v / 255.0f);
            return c;
        }
        return [Rgb("entryA"), Rgb("entryB"), Rgb("entryC"), Glass()];
    }

    // ===== 書き出し (socpaks_to_glb → write_glb) =====
    public sealed class Result { public int Containers, Placements, Meshes; public List<(string Cgf, string Reason)> FailedMeshes = new(); public List<(string Socpak, string Reason)> FailedSocpaks = new(); public long Bytes; }

    public static Result Write(SocpakExporter.Context ctx, IReadOnlyList<string> socpakPaths, string outPath, Func<DataCoreDb> db, CancellationToken ct = default)
    {
        var res = new Result();
        // 区画の置き方とパレットの名前
        var payloads = new List<(string Name, List<(string Cgf, string? Mtl, float[] T, string? Guid)> Meshes, List<string> Palettes)>();
        foreach (var sp in socpakPaths)
        {
            ct.ThrowIfCancellationRequested();
            try { var (n, ms, pals) = SocpakExporter.LoadSocpakForGlb(ctx.P4k, sp); payloads.Add((n, ms, pals)); }
            catch (Exception e) when (e is InvalidDataException or IOException or SocpakExporter.SocpakException or ZstdSharp.ZstdException) { res.FailedSocpaks.Add((sp, e.Message)); }
        }
        var cgfIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var cgfs = new List<Cgf>();
        var containers = new List<(string Name, List<(int Cgf, float[] T)> Pl, float[][]? Palette)>();
        foreach (var (name, meshes, pals) in payloads)
        {
            var pl = new List<(int, float[])>();
            foreach (var im in meshes)
            {
                string cgf; string? mtl;
                if (im.Cgf.Length > 0) { cgf = im.Cgf; mtl = im.Mtl; }
                else if (im.Guid != null) { if (ctx.ResolveGuid(im.Guid) is not { } r) continue; cgf = r.Geom; mtl = r.Mtl.Length > 0 ? r.Mtl : null; }
                else continue;
                if (!cgfIndex.TryGetValue(cgf, out var ix))
                {
                    ix = cgfs.Count; cgfIndex[cgf] = ix;
                    var last = cgf[(cgf.LastIndexOf('/') + 1)..];
                    cgfs.Add(new Cgf(cgf, mtl ?? im.Mtl, last.EndsWith(".cgf", StringComparison.Ordinal) ? last[..^4] : cgf));
                }
                pl.Add((ix, im.T));
            }
            float[][]? palette = pals.Count > 0 ? Palette(db(), pals[0]) : null;
            containers.Add((name, pl, palette));
        }

        var b = new Builder();
        var meshCache = new Loaded?[cgfs.Count];
        var meshTried = new bool[cgfs.Count];
        var packed = new Dictionary<(int, string), int>();
        var sceneNodes = new List<int>();
        foreach (var (name, pl, palette) in containers)
        {
            ct.ThrowIfCancellationRequested();
            var pkey = palette == null ? "" : string.Join(",", palette.SelectMany(c => c).Select(f => BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture)));
            int cnode = b.Nodes.Count;
            b.Nodes.Add(new Node { Name = "interior_" + name, Matrix = Glam.Identity });
            var kids = new List<int>();
            foreach (var (ix, t) in pl)
            {
                var key = (ix, pkey);
                if (!packed.TryGetValue(key, out var mi))
                {
                    if (meshCache[ix] == null)
                    {
                        try { meshCache[ix] = LoadCgf(ctx, cgfs[ix]); }
                        catch (Exception e) when (e is InvalidDataException or IOException or SocpakExporter.MeshLoadException or ZstdSharp.ZstdException)
                        { if (!meshTried[ix]) res.FailedMeshes.Add((cgfs[ix].Path, e.Message)); }
                        meshTried[ix] = true;
                    }
                    if (meshCache[ix] is not { } ld) { packed[key] = -1; continue; }
                    mi = b.PackMesh(ld.Mesh, ld.Mtl, palette, cgfs[ix].Name);
                    packed[key] = mi;
                }
                if (mi < 0) continue;
                kids.Add(b.Nodes.Count);
                b.Nodes.Add(new Node { Mesh = mi, Matrix = t });
            }
            if (kids.Count > 0) b.Nodes[cnode].Children = kids;
            sceneNodes.Add(cnode);
        }
        var bytes = b.Finalize(sceneNodes, ctx.Lod);
        File.WriteAllBytes(outPath, bytes);
        res.Containers = containers.Count; res.Placements = b.Nodes.Count - containers.Count - 1; res.Meshes = b.Meshes.Count; res.Bytes = bytes.Length;
        return res;
    }

    private sealed class Node { public string? Name; public float[]? Matrix; public int? Mesh; public List<int>? Children; }

    private sealed class Builder
    {
        public readonly List<byte> Bin = new();
        public readonly List<string> Views = new(), Accessors = new(), Materials = new(), Meshes = new();
        public readonly List<Node> Nodes = new();
        private readonly Dictionary<string, int> _matDedup = new(StringComparer.Ordinal);

        private void Align() { while (Bin.Count % 4 != 0) Bin.Add(0); }
        private void F(float v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, v); foreach (var x in s) Bin.Add(x); }

        private int VertexAccessor(int off, int len, int count, string type, float[]? min, float[]? max)
        {
            int bv = Views.Count;
            Views.Add($"{{\"buffer\":0,\"byteLength\":{len},\"byteOffset\":{off},\"target\":34962}}");
            int ai = Accessors.Count;
            var sb = new StringBuilder($"{{\"bufferView\":{bv},\"byteOffset\":0,\"count\":{count},\"componentType\":5126,\"type\":\"{type}\"");
            if (min != null) sb.Append(",\"min\":[").Append(string.Join(",", min.Select(f => RustJson.F64(f)))).Append("],\"max\":[").Append(string.Join(",", max!.Select(f => RustJson.F64(f)))).Append(']');
            sb.Append('}');
            Accessors.Add(sb.ToString());
            return ai;
        }

        public int PackMesh(FullMesh mesh, MtlFile? mtl, float[][]? palette, string name)
        {
            Align();
            int posOff = Bin.Count;
            foreach (var f in mesh.Pos) F(f);
            int posLen = Bin.Count - posOff;
            int uvOff = Bin.Count, uvLen = 0;
            if (mesh.Uv != null) { foreach (var f in mesh.Uv) F(f); uvLen = Bin.Count - uvOff; }
            int nOff = Bin.Count;
            foreach (var f in mesh.Nrm) F(f);
            int nLen = Bin.Count - nOff;
            Align();
            int idxOff = Bin.Count;
            uint maxIdx = mesh.Idx.Length == 0 ? 0 : mesh.Idx.Max();
            bool u16 = maxIdx <= ushort.MaxValue;
            if (u16) { foreach (var i in mesh.Idx) { Bin.Add((byte)i); Bin.Add((byte)(i >> 8)); } Align(); }
            else foreach (var i in mesh.Idx) { Bin.Add((byte)i); Bin.Add((byte)(i >> 8)); Bin.Add((byte)(i >> 16)); Bin.Add((byte)(i >> 24)); }
            int idxLen = u16 ? mesh.Idx.Length * 2 : mesh.Idx.Length * 4;
            float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
            for (int v = 0; v < mesh.N; v++) for (int k = 0; k < 3; k++) { mn[k] = RustF32.Min(mn[k], mesh.Pos[v * 3 + k]); mx[k] = RustF32.Max(mx[k], mesh.Pos[v * 3 + k]); }
            int posAcc = posLen > 0 ? VertexAccessor(posOff, posLen, mesh.N, "VEC3", mn, mx) : 0;
            int? uvAcc = uvLen > 0 ? VertexAccessor(uvOff, uvLen, mesh.Uv!.Length / 2, "VEC2", null, null) : null;
            int? nAcc = nLen > 0 ? VertexAccessor(nOff, nLen, mesh.Nrm.Length / 3, "VEC3", null, null) : null;
            int idxBv = Views.Count;
            Views.Add($"{{\"buffer\":0,\"byteLength\":{idxLen},\"byteOffset\":{idxOff},\"target\":34963}}");
            var matIdx = mesh.Subs.Select(s => Material(s.MatId, s.MatName, mtl, palette)).ToList();
            var prims = new List<string>();
            for (int i = 0; i < mesh.Subs.Count; i++)
            {
                var s = mesh.Subs[i];
                var ms = mtl != null && s.MatId < mtl.Materials.Count ? mtl.Materials[(int)s.MatId] : null;
                if (ms != null && (ms.ShouldHide() || ms.IsDecal)) continue;
                int ai = Accessors.Count;
                Accessors.Add($"{{\"bufferView\":{idxBv},\"byteOffset\":{(u16 ? (long)s.First * 2 : (long)s.First * 4)},\"count\":{s.Num},\"componentType\":{(u16 ? 5123 : 5125)},\"type\":\"SCALAR\"}}");
                var at = new StringBuilder($"{{\"POSITION\":{posAcc}");
                if (nAcc is int na) at.Append($",\"NORMAL\":{na}");
                if (uvAcc is int ua) at.Append($",\"TEXCOORD_0\":{ua}");
                at.Append('}');
                prims.Add($"{{\"attributes\":{at},\"indices\":{ai},\"material\":{matIdx[i]}}}");
            }
            var msb = new StringBuilder("{\"name\":");
            RustJson.Str(msb, name);
            msb.Append(",\"primitives\":[").Append(string.Join(",", prims)).Append("]}");
            Meshes.Add(msb.ToString());
            return Meshes.Count - 1;
        }

        // build_material + build_materials の重ねない置き方
        private int Material(uint matId, string? subMatName, MtlFile? mtl, float[][]? palette)
        {
            var m = mtl != null && matId < mtl.Materials.Count ? mtl.Materials[(int)matId] : null;
            float[] baseColor; int alphaMode; float? cutoff; bool doubleSided; string? name;
            if (m != null)
            {
                var (am, ac) = m.AlphaConfig();
                alphaMode = am; cutoff = am == 1 ? ac : null;
                var baseName = m.Name.Length == 0 ? subMatName ?? "" : m.Name;
                string? stem = null;
                if (mtl!.SourcePath is string sp) { var f = sp[(sp.LastIndexOfAny(['\\', '/']) + 1)..]; stem = f.EndsWith(".mtl", StringComparison.Ordinal) ? f[..^4] : f; }
                name = stem != null ? $"{stem}_mtl_{baseName}_0{matId}" : baseName.Length == 0 ? null : baseName;
                float[]? pal = palette == null ? null : m.PaletteTint switch { 1 => palette[0], 2 => palette[1], 3 => palette[2], _ => m.IsGlass ? palette[3] : null };
                float[]? layerColor = m.Layers.Count > 0 && !(m.Layers[0].Tint[0] == 1 && m.Layers[0].Tint[1] == 1 && m.Layers[0].Tint[2] == 1) ? m.Layers[0].Tint : null;
                var c = pal ?? m.MetalBaseColor() ?? layerColor ?? m.Diffuse;
                // ガラス (GlassPBR) は、色の出どころ (パレットの A・B・C・glassColor、材質の色) によらず、決まった色が白なら着色していないガラスの色 (GlassTint) (2026-11-05、利用者の指示)
                if (m.IsGlass) c = GlassTint.OrUntinted(c);
                baseColor = [c[0], c[1], c[2], m.Opacity];
                doubleSided = m.DoubleSided;
            }
            else { baseColor = [0.8f, 0.8f, 0.8f, 1.0f]; alphaMode = 0; cutoff = null; doubleSided = false; name = subMatName; }
            float roughness = m?.Roughness() ?? 0.5f, metallic = m?.Metallic() ?? 0.0f;
            var emissive = m?.EmissiveFactor() ?? [0, 0, 0];
            bool glass = m?.IsGlass ?? false;
            string R(float f) => f.ToString("R", CultureInfo.InvariantCulture);
            var key = $"{name}|{string.Join(",", baseColor.Select(R))}|{R(metallic)}|{R(roughness)}|{doubleSided}|{alphaMode}|{(cutoff is float cf ? R(cf) : "-")}|-|-";
            if (_matDedup.TryGetValue(key, out var idx)) return idx;
            var sb = new StringBuilder("{");
            if (cutoff is float co) sb.Append("\"alphaCutoff\":").Append(J32(co)).Append(',');
            sb.Append("\"alphaMode\":\"").Append(alphaMode switch { 1 => "MASK", 2 => "BLEND", _ => "OPAQUE" }).Append("\",");
            sb.Append("\"doubleSided\":").Append(doubleSided ? "true" : "false").Append(',');
            if (name != null) { sb.Append("\"name\":"); RustJson.Str(sb, name); sb.Append(','); }
            sb.Append("\"pbrMetallicRoughness\":{\"baseColorFactor\":[").Append(string.Join(",", baseColor.Select(J32))).Append("],\"metallicFactor\":")
              .Append(J32(metallic)).Append(",\"roughnessFactor\":").Append(J32(roughness)).Append("},");
            sb.Append("\"emissiveFactor\":[").Append(string.Join(",", emissive.Select(J32))).Append(']');
            if (glass) sb.Append(",\"extensions\":{\"KHR_materials_transmission\":{\"transmissionFactor\":0.95},\"KHR_materials_ior\":{\"ior\":1.5}}");
            if (m != null && Extras(m) is string ex) sb.Append(",\"extras\":").Append(ex);
            sb.Append('}');
            idx = Materials.Count;
            Materials.Add(sb.ToString());
            _matDedup[key] = idx;
            return idx;
        }

        private static string J32(float f) => float.IsFinite(f) ? RustJson.F32(f) : "null";

        // 材質の extras (serde_json の Map: キーの順)
        private static string? Extras(SubMat m)
        {
            var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string S(string v) { var b = new StringBuilder(); RustJson.Str(b, v); return b.ToString(); }
            string A(float[] a) => "[" + string.Join(",", a.Select(f => float.IsFinite(f) ? RustJson.F64(f) : "null")) + "]";
            string N(float f) => float.IsFinite(f) ? RustJson.F64(f) : "null";
            if (m.Name.Length > 0) map["source_name"] = S(m.Name);
            if (m.Shader.Length > 0) map["shader"] = S(m.Shader);
            if (!(m.Diffuse[0] == 1 && m.Diffuse[1] == 1 && m.Diffuse[2] == 1)) map["diffuse"] = A(m.Diffuse);
            if (!(m.Specular[0] == 0.04f && m.Specular[1] == 0.04f && m.Specular[2] == 0.04f)) map["specular"] = A(m.Specular);
            if (MathF.Abs(m.Shininess - 128.0f) > 1.1920929E-07f) map["shininess"] = N(m.Shininess);
            if (!(m.Emissive[0] == 0 && m.Emissive[1] == 0 && m.Emissive[2] == 0)) map["emissive"] = A(m.Emissive);
            if (m.Glow != 0.0f) map["glow"] = N(m.Glow);
            if (MathF.Abs(m.Opacity - 1.0f) > 1.1920929E-07f) map["opacity"] = N(m.Opacity);
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
                    if (!(l.Tint[0] == 1 && l.Tint[1] == 1 && l.Tint[2] == 1)) lm["tint_color"] = A(l.Tint);
                    if (l.PaletteTint > 0) lm["palette_tint"] = l.PaletteTint.ToString(CultureInfo.InvariantCulture);
                    if (MathF.Abs(l.UvTiling - 1.0f) > 1.1920929E-07f) lm["uv_tiling"] = N(l.UvTiling);
                    return "{" + string.Join(",", lm.Select(kv => S(kv.Key) + ":" + kv.Value)) + "}";
                })) + "]";
            }
            if (map.Count == 0) return null;
            return "{" + string.Join(",", map.Select(kv => S(kv.Key) + ":" + kv.Value)) + "}";
        }

        public byte[] Finalize(List<int> sceneNodes, int lod)
        {
            int root = Nodes.Count;
            Nodes.Add(new Node { Name = "CryEngine_Z_up", Children = sceneNodes, Matrix = [1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1] });
            var sb = new StringBuilder("{");
            if (Accessors.Count > 0) sb.Append("\"accessors\":[").Append(string.Join(",", Accessors)).Append("],");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            sb.Append("\"asset\":{\"extras\":{\"export_options\":{\"format\":\"Glb\",\"include_attachments\":true,\"include_interior\":true,\"lod_level\":")
              .Append(lod).Append(",\"material_mode\":\"None\",\"texture_mip\":2},\"export_timestamp_unix\":").Append(ts)
              .Append(",\"generator_version\":\"").Append(GeneratorVersion).Append("\"},\"generator\":\"starbreaker-gltf\",\"version\":\"2.0\"},");
            sb.Append("\"buffers\":[{\"byteLength\":").Append(Bin.Count).Append("}],");
            if (Views.Count > 0) sb.Append("\"bufferViews\":[").Append(string.Join(",", Views)).Append("],");
            sb.Append("\"scene\":0,");
            if (Materials.Any(m => m.Contains("\"extensions\":{\"KHR_materials_transmission\"", StringComparison.Ordinal))) sb.Append("\"extensionsUsed\":[\"KHR_materials_transmission\",\"KHR_materials_ior\"],");
            if (Materials.Count > 0) sb.Append("\"materials\":[").Append(string.Join(",", Materials)).Append("],");
            if (Meshes.Count > 0) sb.Append("\"meshes\":[").Append(string.Join(",", Meshes)).Append("],");
            sb.Append("\"nodes\":[");
            for (int i = 0; i < Nodes.Count; i++)
            {
                var n = Nodes[i];
                if (i > 0) sb.Append(',');
                var parts = new List<string>();
                if (n.Children != null) parts.Add("\"children\":[" + string.Join(",", n.Children) + "]");
                if (n.Matrix != null) parts.Add("\"matrix\":[" + string.Join(",", n.Matrix.Select(f => float.IsFinite(f) ? RustJson.F32(f) : "null")) + "]");
                if (n.Mesh is int mi) parts.Add($"\"mesh\":{mi}");
                if (n.Name != null) { var nb = new StringBuilder("\"name\":"); RustJson.Str(nb, n.Name); parts.Add(nb.ToString()); }
                sb.Append('{').Append(string.Join(",", parts)).Append('}');
            }
            sb.Append("],\"scenes\":[{\"nodes\":[").Append(root).Append("]}]}");
            var json = Encoding.UTF8.GetBytes(sb.ToString());
            int jpad = (4 - json.Length % 4) % 4, bpad = (4 - Bin.Count % 4) % 4;
            int total = 12 + 8 + json.Length + jpad + 8 + Bin.Count + bpad;
            var o = new byte[total];
            var s = o.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(s, 0x46546C67); BinaryPrimitives.WriteUInt32LittleEndian(s[4..], 2); BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)total);
            BinaryPrimitives.WriteUInt32LittleEndian(s[12..], (uint)(json.Length + jpad)); BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0x4E4F534A);
            json.CopyTo(s[20..]);
            for (int i = 0; i < jpad; i++) s[20 + json.Length + i] = (byte)' ';
            int bo = 20 + json.Length + jpad;
            BinaryPrimitives.WriteUInt32LittleEndian(s[bo..], (uint)(Bin.Count + bpad)); BinaryPrimitives.WriteUInt32LittleEndian(s[(bo + 4)..], 0x004E4942);
            Bin.CopyTo(o, bo + 8);
            return o;
        }
    }
}

// 着色していないガラスの色 (2026-11-05、利用者の指示「ガラスは薄い水色がいいんじゃない？透明でなく着色しているものはその色を使った方がいいけど」)。
// 船・品の 3D (ShipGlb・SocpakGlb の Builder.Material) とステーションの 3D (StationView3DTextures.TextureOf) で使う。
// UntintedSrgb: sRGB (191, 227, 242) = #BFE3F2 (薄い水色。値は親が決めたもの。色を変えるときはここだけ)。パレットの色と同じく linear にして使う (UntintedLinear)。
// 着色していない = 色が白 (linear の r・g・b がどれも 1 以上。sRGB の 255,255,255 は linear で 1)
internal static class GlassTint
{
    public static readonly byte[] UntintedSrgb = [191, 227, 242];
    private static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);   // パレットの色と同じ式
    public static readonly float[] UntintedLinear = [SrgbToLinear(UntintedSrgb[0] / 255.0f), SrgbToLinear(UntintedSrgb[1] / 255.0f), SrgbToLinear(UntintedSrgb[2] / 255.0f)];
    public static bool IsWhite(float[] c) => c.Length >= 3 && c[0] >= 1f && c[1] >= 1f && c[2] >= 1f;
    // 白なら着色していないガラスの色 (新しい配列)、それ以外はそのまま
    public static float[] OrUntinted(float[] c) => IsWhite(c) ? (float[])UntintedLinear.Clone() : c;
}
