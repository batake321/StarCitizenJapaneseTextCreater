using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D の自前の読み取り (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」(案 A)・「B」(バイト単位まで同じにする))。
// StarBreaker v0.2.2 の `entity export <NAME> <out.glb> --p4k <Data.p4k> --materials textures` (既定: mip 2・lod 1・付属品・船内・灯りあり) と同じ GLB を書くための部品:
//   形 (ivo/skin.rs・types.rs build_mesh_with_bbox・dequant.rs)、NMC (nmc.rs parse_nmc_full)、テクスチャ (pipeline.rs load_material_textures 以下)。
// 書き出しの流れは ShipExport.cs、GLB の組み立ては ShipGlb.cs。
internal static class ShipMeshes
{
    // ===== 形 (types.rs Mesh) =====
    public sealed class Mesh
    {
        public float[] Pos = [];
        public float[]? Uv;
        public float[]? Nrm;
        public float[]? Tan;          // 4 つずつ
        public uint[] Idx = [];
        public List<Sub> Subs = new();
        public float[] ModelMin = new float[3], ModelMax = new float[3], ScaleMin = new float[3], ScaleMax = new float[3];
        public int N => Pos.Length / 3;
        public static Mesh Empty() => new() { Nrm = null };
    }

    public sealed record Sub(string? MatName, uint MatId, uint First, uint Num, uint FirstVertex, uint NumVertices, ushort Node);

    // merge_from (色は GLB に書かないので持たない)
    public static void Merge(Mesh a, Mesh b)
    {
        uint vOff = (uint)a.N, iOff = (uint)a.Idx.Length;
        a.Pos = [.. a.Pos, .. b.Pos];
        if (a.Uv != null && b.Uv != null) a.Uv = [.. a.Uv, .. b.Uv];
        if (a.Nrm != null && b.Nrm != null) a.Nrm = [.. a.Nrm, .. b.Nrm];
        if (a.Tan != null && b.Tan != null) a.Tan = [.. a.Tan, .. b.Tan];
        var idx = new uint[a.Idx.Length + b.Idx.Length];
        a.Idx.CopyTo(idx, 0);
        for (int i = 0; i < b.Idx.Length; i++) idx[a.Idx.Length + i] = b.Idx[i] + vOff;
        a.Idx = idx;
        foreach (var s in b.Subs) a.Subs.Add(s with { First = s.First + iOff, FirstVertex = s.FirstVertex + vOff });
        for (int i = 0; i < 3; i++)
        {
            a.ModelMin[i] = RustF32.Min(a.ModelMin[i], b.ModelMin[i]); a.ModelMax[i] = RustF32.Max(a.ModelMax[i], b.ModelMax[i]);
            a.ScaleMin[i] = RustF32.Min(a.ScaleMin[i], b.ScaleMin[i]); a.ScaleMax[i] = RustF32.Max(a.ScaleMax[i], b.ScaleMax[i]);
        }
    }

    // parse_skin_with_options (use_model_bbox は船の書き出しでは常に false)。読めなければ例外 (InvalidDataException)
    public static Mesh ParseSkin(byte[] data)
    {
        var cf = CryChunkFile.Parse(data);
        if (!cf.IsIvo) throw new InvalidDataException("UnsupportedFormat (CrCh)");
        var sc = cf.Chunks.FirstOrDefault(c => c.Type == CryChunkFile.IvoSkin2);
        if (sc.Type != CryChunkFile.IvoSkin2) throw new InvalidDataException("IvoSkin2 のチャンクがありません");
        var names = new List<string>();
        foreach (var c in cf.Chunks)
        {
            if (c.Type != CryChunkFile.MtlNameIvo320) continue;
            var d = cf.ChunkData(c);
            if (d.Length < 128) throw new InvalidDataException("MtlName のチャンクが短すぎます");
            var s = d[..128]; int z = s.IndexOf((byte)0);
            names.Add(Encoding.Latin1.GetString(z < 0 ? s : s[..z]));
        }
        return ReadSkin(cf.ChunkData(sc), names);
    }

    private static Mesh ReadSkin(ReadOnlySpan<byte> data, List<string> matNames)
    {
        var r = new SpanReader(data);
        r.U32();
        r.U32();
        uint numVerts = r.U32(), numIdx = r.U32(), numSub = r.U32();
        r.U32();
        var m = new Mesh();
        for (int i = 0; i < 3; i++) m.ModelMin[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ModelMax[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ScaleMin[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ScaleMax[i] = r.F32();
        r.U32();
        uint extra = r.U32();
        r.Advance(88);
        r.Bytes((long)numSub * 48); r.Position -= (int)(numSub * 48);
        var subs = new (ushort Mat, ushort Node, uint First, uint Num, uint FirstVertex, uint PageBase)[numSub];
        for (int i = 0; i < numSub; i++)
        {
            ushort mat = r.U16(), node = r.U16();
            uint first = r.U32(), num = r.U32(), fv = r.U32(), pb = r.U32();
            r.Advance(28);
            subs[i] = (mat, node, first, num, fv, pb);
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
        foreach (var s in subs) m.Subs.Add(new Sub(s.Mat < matNames.Count ? matNames[s.Mat] : null, s.Mat, s.First, s.Num, s.FirstVertex, s.PageBase, s.Node));
        // 法線: IVONORMALS2 → IVONORMALS → 接線の流れ → 面から計算。接線は接線の流れから
        float[]? tanOut = null, tanNrm = null;
        if (tan != null)
        {
            tanOut = new float[nv * 4]; tanNrm = new float[nv * 3];
            for (int i = 0; i < nv; i++)
            {
                if (tanIsQ) CompressedTangent(tan[i * 4], tan[i * 4 + 1], tan[i * 4 + 2], tan[i * 4 + 3], tanNrm.AsSpan(i * 3, 3), tanOut.AsSpan(i * 4, 4));
                else QTangent(tan[i * 4], tan[i * 4 + 1], tan[i * 4 + 2], tan[i * 4 + 3], tanNrm.AsSpan(i * 3, 3), tanOut.AsSpan(i * 4, 4));
            }
        }
        if (floatN != null) m.Nrm = floatN;
        else if (packedN != null) { m.Nrm = new float[nv * 3]; for (int i = 0; i < nv; i++) { var u = PackedUnit(packedN[i]); m.Nrm[i * 3] = u.X; m.Nrm[i * 3 + 1] = u.Y; m.Nrm[i * 3 + 2] = u.Z; } }
        else if (tanNrm != null) m.Nrm = tanNrm;
        else m.Nrm = SmoothNormals(m.Pos, m.Idx);
        m.Tan = tanOut;
        return m;
    }

    // decode_packed_unit_vector
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

    // decode_compressed_tangent
    private static void CompressedTangent(ushort a, ushort b, ushort c, ushort d, Span<float> n, Span<float> t)
    {
        uint v1 = a | ((uint)b << 16), v2 = c | ((uint)d << 16);
        var p = PackedUnit(v1); var q = PackedUnit(v2);
        float sign = (v1 & 0x80000000) != 0 ? -1.0f : 1.0f;
        float cx = p.Y * q.Z - p.Z * q.Y, cy = p.Z * q.X - p.X * q.Z, cz = p.X * q.Y - p.Y * q.X;
        float nx = cx * sign, ny = cy * sign, nz = cz * sign;
        float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len > 1e-8f) { n[0] = nx / len; n[1] = ny / len; n[2] = nz / len; } else { n[0] = 0; n[1] = 0; n[2] = 1; }
        t[0] = p.X; t[1] = p.Y; t[2] = p.Z; t[3] = sign;
    }

    // decode_qtangent_snorm
    private static void QTangent(ushort a, ushort b, ushort c, ushort d, Span<float> n, Span<float> t)
    {
        float qx = (short)a / 32767.0f, qy = (short)b / 32767.0f, qz = (short)c / 32767.0f, qw = (short)d / 32767.0f;
        float nx = 2.0f * (qx * qz + qw * qy), ny = 2.0f * (qy * qz - qw * qx), nz = 1.0f - 2.0f * (qx * qx + qy * qy);
        if (qw < 0.0f) { nx = -nx; ny = -ny; nz = -nz; }
        float tx = 1.0f - 2.0f * (qy * qy + qz * qz), ty = 2.0f * (qx * qy + qw * qz), tz = 2.0f * (qx * qz - qw * qy);
        float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz), tl = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
        float hand = qw >= 0.0f ? 1.0f : -1.0f;
        if (nl > 0.0f) { n[0] = nx / nl; n[1] = ny / nl; n[2] = nz / nl; } else { n[0] = 0; n[1] = 0; n[2] = 1; }
        if (tl > 0.0f) { t[0] = tx / tl; t[1] = ty / tl; t[2] = tz / tl; } else { t[0] = 1; t[1] = 0; t[2] = 0; }
        t[3] = hand;
    }

    // compute_smooth_normals
    private static float[] SmoothNormals(float[] pos, uint[] idx)
    {
        int n = pos.Length / 3;
        var o = new float[n * 3];
        for (int t = 0; t + 2 < idx.Length; t += 3)
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

    // transform_mesh_by_bone (位置・法線・model の箱。接線は動かさない)
    public static void ByBone(Mesh mesh, CryIvo.Bone bone)
    {
        var cm = new CryMesh { Positions = mesh.Pos, ModelMin = mesh.ModelMin, ModelMax = mesh.ModelMax };
        CryIvo.TransformByBone(cm, bone);
        mesh.Pos = cm.Positions; mesh.ModelMin = cm.ModelMin; mesh.ModelMax = cm.ModelMax;
        if (mesh.Nrm == null) return;
        var m3 = Glam.QuatToMat3(bone.Rotation[1], bone.Rotation[2], bone.Rotation[3], bone.Rotation[0]);
        for (int v = 0; v < mesh.Nrm.Length / 3; v++)
        {
            float x = mesh.Nrm[v * 3], y = mesh.Nrm[v * 3 + 1], z = mesh.Nrm[v * 3 + 2];
            mesh.Nrm[v * 3] = m3[0] * x + m3[3] * y + m3[6] * z;
            mesh.Nrm[v * 3 + 1] = m3[1] * x + m3[4] * y + m3[7] * z;
            mesh.Nrm[v * 3 + 2] = m3[2] * x + m3[5] * y + m3[8] * z;
        }
    }

    // ===== NMC (nmc.rs parse_nmc_full の全部: 名前・親・行列・種類・性質) =====
    public sealed class NmcNode
    {
        public string Name = "";
        public ushort? Parent;
        public float[] BoneToWorld = new float[12];   // 3×4 行優先
        public ushort GeometryType;
        public List<(string Key, string Value)> Props = new();   // 同じキーは後のもので置き換え (HashMap の insert)
    }

    public static List<NmcNode>? ParseNmc(byte[]? fileData)
    {
        if (fileData == null) return null;
        var cf = CryChunkFile.TryParse(fileData);
        if (cf == null || !cf.IsIvo) return null;
        var c = cf.Chunks.FirstOrDefault(x => x.Type == CryChunkFile.NodeMeshCombos);
        if (c.Type != CryChunkFile.NodeMeshCombos) return null;
        try
        {
            var r = new SpanReader(cf.ChunkData(c));
            r.I32();
            int numNodes = r.I32();
            r.I32();
            int numUnk = r.I32(), numMat = r.I32(), strSize = r.I32();
            r.I32(); r.I32();
            if (numNodes < 0) return null;
            var nodes = new List<NmcNode>(Math.Min(numNodes, 100000));
            for (int i = 0; i < numNodes; i++)
            {
                r.Advance(32);
                r.Advance(48);
                var n = new NmcNode();
                for (int k = 0; k < 12; k++) n.BoneToWorld[k] = r.F32();
                r.Advance(12 + 8);
                ushort parent = r.U16();
                n.GeometryType = r.U16();
                r.Advance(56);
                n.Parent = parent == 0xFFFF ? null : parent;
                nodes.Add(n);
            }
            r.Advance(32);
            if (numUnk < 0 || numMat < 0 || strSize < 0) return null;
            r.Advance((long)numUnk * 2);
            r.Advance((long)numMat * 2);
            var str = r.Bytes(strSize);
            int pos = 0;
            for (int i = 0; i < numNodes; i++)
            {
                if (pos >= str.Length) { nodes[i].Name = ""; continue; }
                int z = str[pos..].IndexOf((byte)0);
                int end = z < 0 ? str.Length : pos + z;
                var full = Utf8OrEmpty(str[pos..end]);
                nodes[i].Name = FirstLine(full);
                pos = end + 1;
            }
            var meta = r.RemainingBytes;
            int mp = 0;
            for (int i = 0; i < numNodes; i++)
            {
                if (mp < meta.Length)
                {
                    int z = meta[mp..].IndexOf((byte)0);
                    int end = z < 0 ? meta.Length : mp + z;
                    if (Utf8OrNull(meta[mp..end]) is string s)
                        foreach (var line0 in Lines(s))
                        {
                            var line = line0.Trim();
                            int eq = line.IndexOf('=');
                            if (eq < 0) continue;
                            var k = line[..eq]; var v = line[(eq + 1)..];
                            int at = nodes[i].Props.FindIndex(p => p.Key == k);
                            if (at >= 0) nodes[i].Props[at] = (k, v); else nodes[i].Props.Add((k, v));
                        }
                    mp = end + 1;
                }
            }
            return nodes;
        }
        catch (InvalidDataException) { return null; }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static string? Utf8OrNull(ReadOnlySpan<byte> b) { try { return StrictUtf8.GetString(b); } catch (DecoderFallbackException) { return null; } }
    private static string Utf8OrEmpty(ReadOnlySpan<byte> b) => Utf8OrNull(b) ?? "";

    // str::lines: 「\n」で分け、「\r\n」の「\r」を外す (「\n」の付かない最後の行の「\r」は残す)。最後が空なら数えない
    public static List<string> Lines(string s)
    {
        var res = new List<string>();
        int st = 0;
        while (st < s.Length)
        {
            int nl = s.IndexOf('\n', st);
            string line = nl < 0 ? s[st..] : s[st..nl];
            if (nl >= 0 && line.EndsWith('\r')) line = line[..^1];
            res.Add(line);
            if (nl < 0) break;
            st = nl + 1;
        }
        return res;
    }
    private static string FirstLine(string s) { var l = Lines(s); return l.Count > 0 ? l[0] : ""; }

    // Rust の str::trim (Unicode の空白)
    public static string RustTrim(string s) => s.Trim();

    // compute_nmc_world_transforms と bake_nmc_into_mesh (use_root_inv = false。位置と法線)
    public static float[][] NmcWorld(List<NmcNode> nodes) => CryIvo.NmcWorld(nodes.Select(n => new CryIvo.NmcNode(n.Parent, n.BoneToWorld)).ToList());

    // ===== Rust の str::parse::<f32> =====
    public static bool ParseF32(string s, out float v)
    {
        v = 0;
        if (!RustF64.TryParse(s, out _)) return false;
        var t = s.ToLowerInvariant().TrimStart('+', '-');
        if (t is "inf" or "infinity") { v = s.StartsWith('-') ? float.NegativeInfinity : float.PositiveInfinity; return true; }
        if (t == "nan") { v = float.NaN; return true; }
        return float.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out v);
    }
}
