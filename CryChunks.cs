using System.Buffers.Binary;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// CryEngine のチャンクファイル (#ivo・CrCh) と、部品の形 (#ivo の IvoSkin2)・NMC・材質の名前・骨の読み取り。
// StarBreaker v0.2.2 の移植: crates/starbreaker-chunks (chunk_file.rs)、starbreaker-3d (ivo/skin.rs・ivo/material.rs・types.rs・dequant.rs・nmc.rs・skeleton.rs・mtl.rs)。
// 読み方 (足りない所を読もうとしたら失敗にするか、その所を飛ばすか) は starbreaker と同じにする。
internal sealed class CryChunkFile
{
    public const uint IvoMagic = 0x6F766923, CrChMagic = 0x68437243;
    public const uint IvoSkin2 = 0xB8757777, MtlNameIvo320 = 0x83353333, NodeMeshCombos = 0x70697FDA, CompiledBones = 0xC201973C, CompiledBonesIvo320 = 0xC2011111;
    public const ushort CrChCryXmlB = 0x0004, CrChIncludedObjects = 0x0010;

    public readonly record struct Chunk(uint Type, uint Version, int Id, long Offset, long Size);

    public byte[] Data { get; }
    public bool IsIvo { get; }
    public List<Chunk> Chunks { get; } = new();

    private CryChunkFile(byte[] data, bool ivo) { Data = data; IsIvo = ivo; }

    // ChunkFile::from_bytes。読めなければ例外 (InvalidDataException)
    public static CryChunkFile Parse(byte[] data)
    {
        if (data.Length < 4) throw new InvalidDataException("チャンクファイルが短すぎます");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic == IvoMagic)
        {
            Need(data, 16);
            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            if (ver != 0x900) throw new InvalidDataException($"#ivo の版 {ver:X} には対応していません");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            Need(data, 16 + (long)count * 16);
            var f = new CryChunkFile(data, true);
            var raw = new (uint Type, uint Ver, long Off)[count];
            for (int i = 0; i < count; i++)
            {
                var e = data.AsSpan(16 + i * 16);
                raw[i] = (BinaryPrimitives.ReadUInt32LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[4..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(e[8..]));
            }
            // 大きさは、位置の順に並べた次のチャンクの位置 (最後はファイルの終わり) までの差 (位置が同じものは安定な並べ替え)
            var order = Enumerable.Range(0, raw.Length).OrderBy(i => raw[i].Off).ToArray();
            var sizes = new long[raw.Length];
            for (int pos = 0; pos < order.Length; pos++)
            {
                long next = pos + 1 < order.Length ? raw[order[pos + 1]].Off : data.Length;
                sizes[order[pos]] = next - raw[order[pos]].Off;
            }
            for (int i = 0; i < raw.Length; i++) f.Chunks.Add(new Chunk(raw[i].Type, raw[i].Ver, 0, raw[i].Off, sizes[i]));
            return f;
        }
        if (magic == CrChMagic)
        {
            Need(data, 16);
            uint ver = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
            if (ver != 0x746) throw new InvalidDataException($"CrCh の版 {ver:X} には対応していません");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            Need(data, 16 + (long)count * 16);
            var f = new CryChunkFile(data, false);
            for (int i = 0; i < count; i++)
            {
                var e = data.AsSpan(16 + i * 16);
                f.Chunks.Add(new Chunk(BinaryPrimitives.ReadUInt16LittleEndian(e), (uint)(BinaryPrimitives.ReadUInt16LittleEndian(e[2..]) & 0x7FFF),
                    BinaryPrimitives.ReadInt32LittleEndian(e[4..]), BinaryPrimitives.ReadUInt32LittleEndian(e[12..]), BinaryPrimitives.ReadUInt32LittleEndian(e[8..])));
            }
            return f;
        }
        throw new InvalidDataException($"チャンクファイルの印 {magic:X8} が分かりません");
    }

    public static CryChunkFile? TryParse(byte[] data)
    {
        try { return Parse(data); } catch (InvalidDataException) { return null; }
    }

    private static void Need(byte[] data, long n) { if (n > data.Length) throw new InvalidDataException("チャンクファイルが切れています"); }

    // チャンクの中身 (chunk_data。範囲がファイルの外なら starbreaker は止まる (panic) ので、ここは例外)
    public ReadOnlySpan<byte> ChunkData(Chunk c)
    {
        if (c.Offset < 0 || c.Size < 0 || c.Offset + c.Size > Data.Length) throw new InvalidDataException("チャンクの範囲がファイルの外です");
        return Data.AsSpan((int)c.Offset, (int)c.Size);
    }
    public byte[] ChunkBytes(Chunk c) => ChunkData(c).ToArray();
}

// SpanReader (starbreaker-common reader.rs) と同じ、足りなければ例外の読み取り
internal ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _d;
    public int Position;
    public SpanReader(ReadOnlySpan<byte> d) { _d = d; Position = 0; }
    public readonly int Remaining => _d.Length - Position;
    public readonly ReadOnlySpan<byte> RemainingBytes => _d[Position..];
    private ReadOnlySpan<byte> Take(long n)
    {
        if (n < 0 || n > Remaining) throw new InvalidDataException($"読み取りが範囲の外です (位置 {Position}、{n} バイト、残り {Remaining})");
        var s = _d.Slice(Position, (int)n);
        Position += (int)n;
        return s;
    }
    public void Advance(long n) => Take(n);
    public ReadOnlySpan<byte> Bytes(long n) => Take(n);
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public byte U8() => Take(1)[0];
    public sbyte I8() => (sbyte)Take(1)[0];
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
}

// 部品の形 (types.rs Mesh のうち、位置・索引・submesh・2 つの箱)。法線・UV・色は持たない (地図・3D の三角形には使わない)
internal sealed class CryMesh
{
    public float[] Positions = [];          // x,y,z の並び
    public uint[] Indices = [];
    public List<CrySubMesh> SubMeshes = new();
    public float[] ModelMin = new float[3], ModelMax = new float[3], ScalingMin = new float[3], ScalingMax = new float[3];
    public int VertexCount => Positions.Length / 3;

    // merge_from (位置・索引・submesh・箱)
    public void MergeFrom(CryMesh other)
    {
        uint vOff = (uint)VertexCount, iOff = (uint)Indices.Length;
        Positions = [.. Positions, .. other.Positions];
        var idx = new uint[Indices.Length + other.Indices.Length];
        Indices.CopyTo(idx, 0);
        for (int i = 0; i < other.Indices.Length; i++) idx[Indices.Length + i] = other.Indices[i] + vOff;
        Indices = idx;
        foreach (var s in other.SubMeshes) SubMeshes.Add(s with { FirstIndex = s.FirstIndex + iOff, FirstVertex = s.FirstVertex + vOff });
        for (int i = 0; i < 3; i++)
        {
            ModelMin[i] = RustF32.Min(ModelMin[i], other.ModelMin[i]); ModelMax[i] = RustF32.Max(ModelMax[i], other.ModelMax[i]);
            ScalingMin[i] = RustF32.Min(ScalingMin[i], other.ScalingMin[i]); ScalingMax[i] = RustF32.Max(ScalingMax[i], other.ScalingMax[i]);
        }
    }
}

internal readonly record struct CrySubMesh(uint MaterialId, uint FirstIndex, uint NumIndices, uint FirstVertex, uint NumVertices, ushort NodeParentIndex);

internal static class CryIvo
{
    // parse_skin_with_options (use_model_bbox = false: 2 つ目の箱で位置を戻す)。IvoSkin2 が無い・CrCh・読めなければ例外
    public static CryMesh ParseSkin(byte[] data)
    {
        var cf = CryChunkFile.Parse(data);
        if (!cf.IsIvo) throw new InvalidDataException("CrCh の形には対応していません (UnsupportedFormat)");
        var skinChunk = cf.Chunks.FirstOrDefault(c => c.Type == CryChunkFile.IvoSkin2);
        if (skinChunk.Type != CryChunkFile.IvoSkin2) throw new InvalidDataException("IvoSkin2 のチャンクがありません");
        var mesh = ReadSkin(cf.ChunkData(skinChunk));
        // MtlName のチャンクはすべて 128 バイト読めること (読めなければ部品の失敗。名前そのものは使わない)
        foreach (var c in cf.Chunks)
            if (c.Type == CryChunkFile.MtlNameIvo320 && cf.ChunkData(c).Length < 128) throw new InvalidDataException("MtlName のチャンクが短すぎます");
        return mesh;
    }

    // ivo/skin.rs SkinMesh::read と types.rs build_mesh_with_bbox (位置・索引・submesh だけ)
    private static CryMesh ReadSkin(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        r.U32();   // flags
        // MeshInfo (76 バイト)
        r.U32();
        uint numVerts = r.U32(), numIdx = r.U32(), numSub = r.U32();
        r.U32();
        var m = new CryMesh();
        for (int i = 0; i < 3; i++) m.ModelMin[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ModelMax[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ScalingMin[i] = r.F32();
        for (int i = 0; i < 3; i++) m.ScalingMax[i] = r.F32();
        r.U32();
        uint extra = r.U32();
        r.Advance(88);
        var subs = new (ushort Mat, ushort Node, uint First, uint Num, uint FirstVertex, uint PageBase)[numSub];
        r.Bytes((long)numSub * 48);   // 先に範囲を確かめる (read_slice)
        r.Position -= (int)(numSub * 48);
        for (int i = 0; i < numSub; i++)
        {
            ushort mat = r.U16(), node = r.U16();
            uint first = r.U32(), num = r.U32(), fv = r.U32(), pb = r.U32();
            r.Advance(48 - 20);
            subs[i] = (mat, node, first, num, fv, pb);
        }
        r.Advance((long)extra * 4);

        // データの流れ (read_streams)
        float[]? pos = null; bool quant = false; ushort[]? qpos = null; uint[]? idx = null;
        long nv = numVerts, ni = numIdx;
        while (r.Remaining >= 4)
        {
            uint st = r.U32();
            if (st == 0) continue;
            if (r.Remaining < 4) break;
            uint es = r.U32();
            int start = r.Position;
            switch (st)
            {
                case 0x91329AE9: case 0xB3A70D5E:   // IVOVERTSUVS・IVOVERTSUVS2
                    if (es == 16)
                    {
                        var b = r.Bytes(nv * 16);
                        qpos = new ushort[nv * 3];
                        for (int v = 0; v < nv; v++) for (int k = 0; k < 3; k++) qpos[v * 3 + k] = BinaryPrimitives.ReadUInt16LittleEndian(b[(v * 16 + k * 2)..]);
                        quant = true; pos = null;
                    }
                    else if (es == 20)
                    {
                        var b = r.Bytes(nv * 20);
                        pos = new float[nv * 3];
                        for (int v = 0; v < nv; v++) for (int k = 0; k < 3; k++) pos[v * 3 + k] = BinaryPrimitives.ReadSingleLittleEndian(b[(v * 20 + k * 4)..]);
                        quant = false; qpos = null;
                    }
                    else throw new InvalidDataException($"頂点の要素の大きさ {es} には対応していません");
                    break;
                case 0xEECDC168:   // IVOINDICES
                    if (es == 2)
                    {
                        var b = r.Bytes(ni * 2);
                        idx = new uint[ni];
                        for (int i = 0; i < ni; i++) idx[i] = BinaryPrimitives.ReadUInt16LittleEndian(b[(i * 2)..]);
                    }
                    else
                    {
                        var b = r.Bytes(ni * 4);
                        idx = new uint[ni];
                        for (int i = 0; i < ni; i++) idx[i] = BinaryPrimitives.ReadUInt32LittleEndian(b[(i * 4)..]);
                    }
                    break;
                case 0x9CF3F615: r.Bytes(nv * 4); break;                       // IVONORMALS
                case 0x38A581FE: if (es == 12) r.Bytes(nv * 12); else r.Advance((long)es * nv); break;   // IVONORMALS2
                case 0xEE057252: case 0xB95E9A1B: r.Bytes(nv * 8); break;      // IVOQTANGENTS・IVOTANGENTS
                default:
                    long skip = (long)es * nv;
                    if (r.Remaining >= skip) r.Advance(skip);
                    else goto done;
                    break;
            }
            int read = r.Position - start;
            int rem = read % 8;
            if (rem != 0 && r.Remaining >= 8 - rem) r.Advance(8 - rem);
        }
    done:
        if (pos == null && qpos == null) throw new InvalidDataException("頂点のデータがありません (IVOVERTSUVS)");
        if (idx == null) throw new InvalidDataException("索引のデータがありません (IVOINDICES)");
        if (quant)
        {
            // dequantize_position: i16 / 32767 × max(半分の幅, 1) + 中心 (f32 で)
            pos = new float[nv * 3];
            for (int v = 0; v < nv; v++)
                for (int k = 0; k < 3; k++)
                {
                    float snorm = (short)qpos![v * 3 + k] / 32767.0f;
                    float half = RustF32.Max((m.ScalingMax[k] - m.ScalingMin[k]) / 2.0f, 1.0f);
                    float center = (m.ScalingMax[k] + m.ScalingMin[k]) / 2.0f;
                    pos[v * 3 + k] = snorm * half + center;
                }
        }
        m.Positions = pos!;
        // 索引に page_base を足す
        foreach (var s in subs)
        {
            if (s.PageBase == 0) continue;
            long st = s.First, en = st + s.Num;
            if (en > idx.Length) throw new InvalidDataException("submesh の索引の範囲が外れています");
            for (long i = st; i < en; i++) idx[i] += s.PageBase;
        }
        m.Indices = idx;
        foreach (var s in subs) m.SubMeshes.Add(new CrySubMesh(s.Mat, s.First, s.Num, s.FirstVertex, s.PageBase, s.Node));
        return m;
    }

    // ===== NMC (nmc.rs parse_nmc_full。焼き込みに使う親と bone_to_world だけ) =====
    public sealed record NmcNode(ushort? Parent, float[] BoneToWorld /* 3×4 行優先 */);

    public static List<NmcNode>? ParseNmc(byte[] fileData)
    {
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
                r.Advance(48);   // world_to_bone
                var b2w = new float[12];
                for (int k = 0; k < 12; k++) b2w[k] = r.F32();
                r.Advance(12 + 8);
                ushort parent = r.U16();
                r.U16();
                r.Advance(56);
                nodes.Add(new NmcNode(parent == 0xFFFF ? null : parent, b2w));
            }
            r.Advance(32);
            if (numUnk < 0 || numMat < 0 || strSize < 0) return null;
            r.Advance((long)numUnk * 2);
            r.Advance((long)numMat * 2);
            r.Bytes(strSize);
            return nodes;
        }
        catch (InvalidDataException) { return null; }
    }

    // compute_nmc_world_transforms (glam の Mat4 の掛け算と同じ順の f32 の計算)
    public static float[][] NmcWorld(List<NmcNode> nodes)
    {
        int n = nodes.Count;
        var local = new float[n][];
        for (int i = 0; i < n; i++)
        {
            var m = nodes[i].BoneToWorld;   // 行優先 3×4: m[r*4+c]
            local[i] = [m[0], m[4], m[8], 0, m[1], m[5], m[9], 0, m[2], m[6], m[10], 0, m[3], m[7], m[11], 1];
        }
        var world = new float[n][];
        float[] Resolve(int i)
        {
            if (world[i] != null) return world[i];
            var p = nodes[i].Parent;
            var w = p is ushort pi && pi < n && pi != i ? Glam.Mul(Resolve(pi), local[i]) : local[i];
            return world[i] = w;
        }
        for (int i = 0; i < n; i++) Resolve(i);
        return world;
    }

    // bake_nmc_into_mesh (use_root_inv = false。位置だけ)
    public static void BakeNmc(CryMesh mesh, List<NmcNode>? nmc)
    {
        if (nmc == null || nmc.Count == 0) return;
        var world = NmcWorld(nmc);
        int nv = mesh.VertexCount;
        var vertNode = new int[nv];
        Array.Fill(vertNode, -1);
        foreach (var sub in mesh.SubMeshes)
        {
            int node = sub.NodeParentIndex;
            if (node >= world.Length) continue;
            long start = sub.FirstIndex, end = Math.Min(start + sub.NumIndices, mesh.Indices.Length);
            if (start > end) throw new InvalidDataException("submesh の索引の範囲が外れています");
            for (long i = start; i < end; i++)
            {
                long vi = mesh.Indices[i];
                if (vi < nv && vertNode[vi] < 0) vertNode[vi] = node;
            }
        }
        var xforms = new float[world.Length][];
        var isId = new bool[world.Length];
        for (int k = 0; k < world.Length; k++) { xforms[k] = Glam.Mul(Glam.Identity, world[k]); isId[k] = Glam.IsIdentity(xforms[k]); }
        for (int v = 0; v < nv; v++)
        {
            int node = vertNode[v];
            if (node < 0 || isId[node]) continue;
            var x = xforms[node];
            float px = mesh.Positions[v * 3], py = mesh.Positions[v * 3 + 1], pz = mesh.Positions[v * 3 + 2];
            Glam.TransformPoint3(x, px, py, pz, out mesh.Positions[v * 3], out mesh.Positions[v * 3 + 1], out mesh.Positions[v * 3 + 2]);
        }
    }

    // mtl.rs extract_mtl_name: 最初の MtlName のチャンクの名前 (128 バイト、0 まで、1 バイト 1 文字)
    public static string? ExtractMtlName(byte[] data)
    {
        var cf = CryChunkFile.TryParse(data);
        if (cf == null || !cf.IsIvo) return null;
        foreach (var c in cf.Chunks)
        {
            if (c.Type != CryChunkFile.MtlNameIvo320) continue;
            var d = cf.ChunkData(c);
            if (d.Length < 128) return null;
            var s = d[..128];
            int z = s.IndexOf((byte)0);
            return Encoding.Latin1.GetString(z < 0 ? s : s[..z]);
        }
        return null;
    }

    // ===== 骨 (skeleton.rs parse_skeleton。名前・世界の位置・回転 (w,x,y,z)) =====
    public sealed record Bone(string Name, float[] Position, float[] Rotation);

    public static List<Bone>? ParseSkeleton(byte[] data)
    {
        var cf = CryChunkFile.TryParse(data);
        if (cf == null || !cf.IsIvo) return null;
        foreach (var c in cf.Chunks)
        {
            try
            {
                if (c.Type == CryChunkFile.CompiledBones) return BonesV901(cf.ChunkData(c));
                if (c.Type == CryChunkFile.CompiledBonesIvo320) return BonesV900(cf.ChunkData(c));
            }
            catch (InvalidDataException) { return null; }
        }
        return null;
    }

    private static List<string>? BoneNames(ReadOnlySpan<byte> data, int expected)
    {
        var names = new List<string>();
        int s = 0;
        for (int i = 0; i <= data.Length; i++)
        {
            if (i == data.Length || data[i] == 0)
            {
                if (i > s) names.Add(Encoding.UTF8.GetString(data[s..i]));
                s = i + 1;
            }
        }
        return names.Count < expected ? null : names;
    }

    private static Bone ToBone(ReadOnlySpan<byte> qt, string name)
    {
        var f = new float[7];
        for (int k = 0; k < 7; k++) f[k] = BinaryPrimitives.ReadSingleLittleEndian(qt[(k * 4)..]);
        return new Bone(name, [f[4], f[5], f[6]], [f[3], f[0], f[1], f[2]]);
    }

    private static List<Bone>? BonesV900(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        int n = (int)r.U32();
        if (n == 0 || n > 1000) return null;
        var entries = r.Bytes((long)n * 68);
        var names = BoneNames(r.RemainingBytes, n);
        if (names == null) return null;
        var res = new List<Bone>();
        for (int i = 0; i < n && i < names.Count; i++) res.Add(ToBone(entries[(i * 68 + 40)..], names[i]));
        return res;
    }

    private static List<Bone>? BonesV901(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        int n = (int)r.U32();
        int strSize = (int)r.U32();
        r.U32(); r.U32();
        r.Advance(32);
        if (n == 0 || n > 1000) return null;
        r.Bytes((long)n * 16);
        var names = BoneNames(r.Bytes(strSize), n);
        if (names == null) return null;
        r.Bytes((long)n * 28);
        var world = r.Bytes((long)n * 28);
        var res = new List<Bone>();
        for (int i = 0; i < n && i < names.Count; i++) res.Add(ToBone(world[(i * 28)..], names[i]));
        return res;
    }

    // transform_mesh_by_bone (位置と model の箱)
    public static void TransformByBone(CryMesh mesh, Bone bone)
    {
        float qw = bone.Rotation[0], qx = bone.Rotation[1], qy = bone.Rotation[2], qz = bone.Rotation[3];
        var m3 = Glam.QuatToMat3(qx, qy, qz, qw);
        float tx = bone.Position[0], ty = bone.Position[1], tz = bone.Position[2];
        void Tr(float x, float y, float z, out float ox, out float oy, out float oz)
        {
            // Affine3A::transform_point3: matrix3 * p + translation (Mat3A × Vec3A: x 列 * x + y 列 * y + z 列 * z)
            ox = m3[0] * x + m3[3] * y + m3[6] * z + tx;
            oy = m3[1] * x + m3[4] * y + m3[7] * z + ty;
            oz = m3[2] * x + m3[5] * y + m3[8] * z + tz;
        }
        for (int v = 0; v < mesh.VertexCount; v++)
            Tr(mesh.Positions[v * 3], mesh.Positions[v * 3 + 1], mesh.Positions[v * 3 + 2], out mesh.Positions[v * 3], out mesh.Positions[v * 3 + 1], out mesh.Positions[v * 3 + 2]);
        float[] mn = [float.MaxValue, float.MaxValue, float.MaxValue], mx = [float.MinValue, float.MinValue, float.MinValue];
        foreach (var x in new[] { mesh.ModelMin[0], mesh.ModelMax[0] })
            foreach (var y in new[] { mesh.ModelMin[1], mesh.ModelMax[1] })
                foreach (var z in new[] { mesh.ModelMin[2], mesh.ModelMax[2] })
                {
                    Tr(x, y, z, out var a, out var b, out var c);
                    mn[0] = RustF32.Min(mn[0], a); mn[1] = RustF32.Min(mn[1], b); mn[2] = RustF32.Min(mn[2], c);
                    mx[0] = RustF32.Max(mx[0], a); mx[1] = RustF32.Max(mx[1], b); mx[2] = RustF32.Max(mx[2], c);
                }
        mesh.ModelMin = mn; mesh.ModelMax = mx;
    }
}

// 材質 (mtl.rs のうち、部品の面を出さない判定に使うものだけ)
internal sealed class CryMtl
{
    public sealed record Sub(string Name, string Shader, string StringGenMask, bool IsNoDraw, string? DiffuseTex);
    public List<Sub> Materials { get; } = new();

    // parse_mtl。CryXmlB でなければ例外 (starbreaker は文字の XML の .mtl を読まない)
    public static CryMtl Parse(byte[] data)
    {
        var x = CryXmlB.Parse(data);
        var res = new CryMtl();
        int? sub = null;
        foreach (var c in x.Children(x.Root)) if (x.Tag(c) == "SubMaterials") { sub = c; break; }
        if (sub is int s) { foreach (var c in x.Children(s)) if (x.Tag(c) == "Material") res.Materials.Add(ParseSub(x, c)); }
        else res.Materials.Add(ParseSub(x, x.Root));
        return res;
    }

    private static Sub ParseSub(CryXmlB x, int node)
    {
        string name = "", shader = "", mask = "";
        bool nodraw = false;
        foreach (var (k, v) in x.Attributes(node))
        {
            switch (k)
            {
                case "Name": name = v; break;
                case "Shader": shader = v; if (v == "NoDraw") nodraw = true; break;
                case "StringGenMask": mask = v; break;
            }
        }
        string? diffuse = null;
        foreach (var c in x.Children(node))
        {
            if (x.Tag(c) != "Textures") continue;
            foreach (var t in x.Children(c))
            {
                if (x.Tag(t) != "Texture") continue;
                string slot = ""; string? file = null;
                foreach (var (k, v) in x.Attributes(t)) { if (k == "Map") slot = v; else if (k == "File") file = v; }
                if (file == null || file.StartsWith('$')) continue;
                if (slot == "TexSlot1") diffuse = file;
            }
        }
        return new Sub(name, shader, mask, nodraw, diffuse);
    }

    public static bool IsDecal(Sub m) => m.StringGenMask.Contains("%DECAL") || m.Shader == "MeshDecal";

    // pack_mesh で面を出さない submesh: should_hide、または (テクスチャを使わないので) デカール
    public static bool HideWithoutTextures(Sub m)
    {
        if (m.IsNoDraw) return true;
        var mask = m.StringGenMask;
        if (mask.Contains("%PARALLAX_OCCLUSION_MAPPING") && (IsDecal(m) || mask.Contains("%DECAL"))) return true;
        if (IsDecal(m) && m.DiffuseTex == null && (mask.Contains("STENCIL_MAP") || !mask.Contains("%VERTCOLORS"))) return true;
        return IsDecal(m);
    }
}

// glam (f32) の計算を同じ順で行う
internal static class Glam
{
    public static readonly float[] Identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];

    // 列優先の 4×4 の掛け算 a × b (mul_vec4: ((a.x*v.x + a.y*v.y) + a.z*v.z) + a.w*v.w)
    public static float[] Mul(float[] a, float[] b)
    {
        var c = new float[16];
        for (int col = 0; col < 4; col++)
        {
            float vx = b[col * 4], vy = b[col * 4 + 1], vz = b[col * 4 + 2], vw = b[col * 4 + 3];
            for (int row = 0; row < 4; row++)
            {
                float s = a[row] * vx;
                s = s + a[4 + row] * vy;
                s = s + a[8 + row] * vz;
                s = s + a[12 + row] * vw;
                c[col * 4 + row] = s;
            }
        }
        return c;
    }

    public static bool IsIdentity(float[] m)
    {
        for (int i = 0; i < 16; i++) if (m[i] != Identity[i]) return false;
        return true;
    }

    // Mat4::transform_point3: ((x 列 * px + y 列 * py) + z 列 * pz) + w 列
    public static void TransformPoint3(float[] m, float px, float py, float pz, out float ox, out float oy, out float oz)
    {
        float rx = m[0] * px, ry = m[1] * px, rz = m[2] * px;
        rx = m[4] * py + rx; ry = m[5] * py + ry; rz = m[6] * py + rz;
        rx = m[8] * pz + rx; ry = m[9] * pz + ry; rz = m[10] * pz + rz;
        ox = m[12] + rx; oy = m[13] + ry; oz = m[14] + rz;
    }

    // quat_to_axes (glam): 列優先の 3×3
    public static float[] QuatToMat3(float x, float y, float z, float w)
    {
        float x2 = x + x, y2 = y + y, z2 = z + z;
        float xx = x * x2, xy = x * y2, xz = x * z2, yy = y * y2, yz = y * z2, zz = z * z2, wx = w * x2, wy = w * y2, wz = w * z2;
        return [1.0f - (yy + zz), xy + wz, xz - wy, xy - wz, 1.0f - (xx + zz), yz + wx, xz + wy, yz - wx, 1.0f - (xx + yy)];
    }

    // Mat4::from_scale_rotation_translation (列優先 16)
    public static float[] FromScaleRotationTranslation(float sx, float sy, float sz, float qx, float qy, float qz, float qw, float tx, float ty, float tz)
    {
        var r = QuatToMat3(qx, qy, qz, qw);
        return [r[0] * sx, r[1] * sx, r[2] * sx, 0, r[3] * sy, r[4] * sy, r[5] * sy, 0, r[6] * sz, r[7] * sz, r[8] * sz, 0, tx, ty, tz, 1];
    }
}
