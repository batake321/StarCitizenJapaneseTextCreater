using System.Buffers.Binary;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// CrCh (版 0x746) の形のファイル (.cgf) の読み取り (2026-11-04)。
// socpak の中にある MergedMeshes/CompoundRenderNode_*.cgf (複数の部品をまとめた形) は、#ivo ではなく CrCh で、対の .cgfm がない。
// StarBreaker v0.2.2 はこの形式を読まない (parse_skin は CrCh なら UnsupportedFormat。そもそも p4k の目次に .cgfm が無いので
// 「file not found in P4k」で抜ける。orison_comm_000 の -v のログで 257 個を確かめた)。利用者が「建物が描かれない」を不具合として指摘したので読む。
// 読み方は実データ (orison_comm_000\mergedmeshes\compoundrendernode_1313789737.cgf) のバイトで確かめたもの:
//   ・チャンク 0x1000 (Mesh、版 0x801、260 バイト): flags, flags2, 頂点数, 索引数, submesh 数, MeshSubsets のチャンク番号, VertAnim の番号,
//     ストリームのチャンク番号 [16] (5 = 索引、15 = P3S_C4B_T2S、0 = 位置 (f32×3) ほか), 物理 [4], 箱 (min・max の f32×3)
//   ・チャンク 0x1016 (DataStream、版 0x800): flags, 種類, 数, 要素の大きさ (下の 16 ビット), 予備 ×2 の 24 バイトの後にデータ
//     P3S_C4B_T2S (16 バイト): 位置は i16×3 (+ w) の符号付き正規化を、箱で戻す (#ivo と同じ dequantize_position: 半分の幅は 1 以上にする)
//   ・チャンク 0x1017 (MeshSubsets、版 0x800): flags, 数, 予備 ×2 の後に、1 つ 36 バイト (最初の索引, 索引の数, 最初の頂点, 頂点の数, 材質の番号, 半径, 中心)
//   ・チャンク 0x1014 (MtlName): 先頭が 0 で終わる材質の名前
//   ・チャンク 0x100B (Node、版 0x824): 名前 64 バイト, 物の番号, 親の番号, 子の数, 材質の番号, 4 バイト, 4×4 の行列 (f32。4 列目は使わない。実データでは m[15] も 0)
// 形の位置は、ノードの行列が単位行列でなければ、その行列 (行ベクトル × 行列) を掛ける (MergedMeshes のノードは実データでは単位行列)。
internal static class CryCrChMesh
{
    public const ushort ChunkMesh = 0x1000, ChunkNode = 0x100B, ChunkMtlName = 0x1014, ChunkDataStream = 0x1016, ChunkMeshSubsets = 0x1017;
    private const int StreamPositions = 0, StreamIndices = 5, StreamP3sC4bT2s = 15;

    public static bool IsCrCh(byte[] data) => data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == CryChunkFile.CrChMagic;

    // 形を読む。読めなければ InvalidDataException
    public static CryMesh Parse(byte[] data)
    {
        var cf = CryChunkFile.Parse(data);
        if (cf.IsIvo) throw new InvalidDataException("CrCh ではありません");
        var byId = new Dictionary<int, CryChunkFile.Chunk>();
        foreach (var c in cf.Chunks) byId.TryAdd(c.Id, c);

        // 形を持つノード (物の番号が Mesh のチャンクを指すもの)。ノードが無ければ Mesh のチャンクをそのまま
        var targets = new List<(CryChunkFile.Chunk Mesh, float[]? M)>();
        foreach (var c in cf.Chunks)
        {
            if (c.Type != ChunkNode) continue;
            var d = cf.ChunkData(c);
            if (d.Length < 64 + 20 + 64) throw new InvalidDataException("CrCh の Node が短すぎます");
            int obj = BinaryPrimitives.ReadInt32LittleEndian(d[64..]);
            if (!byId.TryGetValue(obj, out var mc) || mc.Type != ChunkMesh) continue;
            var m = new float[16];
            for (int k = 0; k < 16; k++) m[k] = BinaryPrimitives.ReadSingleLittleEndian(d[(84 + k * 4)..]);
            targets.Add((mc, IsIdentity(m) ? null : m));
        }
        if (targets.Count == 0)
            foreach (var c in cf.Chunks) if (c.Type == ChunkMesh) targets.Add((c, null));
        if (targets.Count == 0) throw new InvalidDataException("CrCh に Mesh のチャンクがありません");

        CryMesh? res = null;
        foreach (var (mc, m) in targets)
        {
            var one = ParseMesh(cf, byId, mc);
            if (one == null) continue;
            if (m != null) Transform(one, m);
            if (res == null) res = one; else res.MergeFrom(one);
        }
        return res ?? throw new InvalidDataException("CrCh の Mesh に頂点がありません");
    }

    // 材質の名前 (0x1014 の最初のもの)。無ければ null
    public static string? MtlName(byte[] data)
    {
        var cf = CryChunkFile.TryParse(data);
        if (cf == null || cf.IsIvo) return null;
        foreach (var c in cf.Chunks)
        {
            if (c.Type != ChunkMtlName) continue;
            var d = cf.ChunkData(c);
            int n = Math.Min(d.Length, 128);
            int z = d[..n].IndexOf((byte)0);
            var s = Encoding.UTF8.GetString(z < 0 ? d[..n] : d[..z]);
            return s.Length > 0 ? s : null;
        }
        return null;
    }

    private static CryMesh? ParseMesh(CryChunkFile cf, Dictionary<int, CryChunkFile.Chunk> byId, CryChunkFile.Chunk mc)
    {
        if (mc.Version != 0x801) throw new InvalidDataException($"CrCh の Mesh の版 {mc.Version:X} には対応していません");
        var r = new SpanReader(cf.ChunkData(mc));
        r.U32(); r.U32();
        int nVerts = r.I32(), nIdx = r.I32(), nSub = r.I32(), subId = r.I32();
        r.I32();
        var streams = new int[16];
        for (int i = 0; i < 16; i++) streams[i] = r.I32();
        for (int i = 0; i < 4; i++) r.I32();
        var mn = new float[3]; var mx = new float[3];
        for (int i = 0; i < 3; i++) mn[i] = r.F32();
        for (int i = 0; i < 3; i++) mx[i] = r.F32();
        if (nVerts <= 0) return null;

        var mesh = new CryMesh();
        mn.CopyTo(mesh.ModelMin, 0); mx.CopyTo(mesh.ModelMax, 0); mn.CopyTo(mesh.ScalingMin, 0); mx.CopyTo(mesh.ScalingMax, 0);

        // 位置
        var pos = new float[nVerts * 3];
        if (streams[StreamP3sC4bT2s] != 0)
        {
            var (dataM, count, size) = Stream(cf, byId, streams[StreamP3sC4bT2s]);
            var data = dataM.Span;
            if (count < nVerts || size < 8) throw new InvalidDataException("CrCh の P3S_C4B_T2S が足りません");
            for (int v = 0; v < nVerts; v++)
                for (int k = 0; k < 3; k++)
                {
                    float snorm = (short)BinaryPrimitives.ReadUInt16LittleEndian(data[(v * size + k * 2)..]) / 32767.0f;
                    float half = RustF32.Max((mx[k] - mn[k]) / 2.0f, 1.0f);
                    float center = (mx[k] + mn[k]) / 2.0f;
                    pos[v * 3 + k] = snorm * half + center;
                }
        }
        else if (streams[StreamPositions] != 0)
        {
            var (dataM, count, size) = Stream(cf, byId, streams[StreamPositions]);
            var data = dataM.Span;
            if (count < nVerts || size < 12) throw new InvalidDataException("CrCh の位置が足りません");
            for (int v = 0; v < nVerts; v++)
                for (int k = 0; k < 3; k++) pos[v * 3 + k] = BinaryPrimitives.ReadSingleLittleEndian(data[(v * size + k * 4)..]);
        }
        else throw new InvalidDataException("CrCh の Mesh に位置のストリームがありません");
        mesh.Positions = pos;

        // 索引
        if (streams[StreamIndices] == 0) throw new InvalidDataException("CrCh の Mesh に索引のストリームがありません");
        {
            var (dataM, count, size) = Stream(cf, byId, streams[StreamIndices]);
            var data = dataM.Span;
            if (count < nIdx) throw new InvalidDataException("CrCh の索引が足りません");
            var idx = new uint[nIdx];
            if (size == 2) for (int i = 0; i < nIdx; i++) idx[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            else if (size == 4) for (int i = 0; i < nIdx; i++) idx[i] = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            else throw new InvalidDataException($"CrCh の索引の大きさ {size} には対応していません");
            foreach (var i in idx) if (i >= nVerts) throw new InvalidDataException("CrCh の索引が頂点の数を超えています");
            mesh.Indices = idx;
        }

        // submesh
        if (nSub > 0 && byId.TryGetValue(subId, out var sc) && sc.Type == ChunkMeshSubsets)
        {
            var s = new SpanReader(cf.ChunkData(sc));
            s.U32();
            int cnt = s.I32();
            s.U32(); s.U32();
            for (int i = 0; i < cnt; i++)
            {
                int first = s.I32(), num = s.I32(), fv = s.I32(), nv = s.I32(), mat = s.I32();
                s.Advance(16);
                if (first < 0 || num < 0 || (long)first + num > nIdx) throw new InvalidDataException("CrCh の submesh の範囲が外れています");
                mesh.SubMeshes.Add(new CrySubMesh((uint)mat, (uint)first, (uint)num, (uint)fv, (uint)nv, 0));
            }
        }
        else mesh.SubMeshes.Add(new CrySubMesh(0, 0, (uint)nIdx, 0, (uint)nVerts, 0));
        return mesh;
    }

    // DataStream (版 0x800): (データ, 数, 要素の大きさ)
    private static (Memory<byte> Data, int Count, int Size) Stream(CryChunkFile cf, Dictionary<int, CryChunkFile.Chunk> byId, int id)
    {
        if (!byId.TryGetValue(id, out var c) || c.Type != ChunkDataStream) throw new InvalidDataException($"CrCh のストリーム {id} がありません");
        var d = cf.ChunkData(c);
        if (d.Length < 24) throw new InvalidDataException("CrCh のストリームが短すぎます");
        int count = BinaryPrimitives.ReadInt32LittleEndian(d[8..]);
        int size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(d[12..]) & 0xFFFF);
        if (count < 0 || size <= 0 || 24 + (long)count * size > d.Length) throw new InvalidDataException("CrCh のストリームの大きさが合いません");
        return (cf.Data.AsMemory((int)c.Offset + 24, count * size), count, size);
    }

    private static bool IsIdentity(float[] m)
    {
        // 4 列目 (m[3]・m[7]・m[11]・m[15]) は見ない (実データでは m[15] も 0。行列は 3×4 の部分だけを使う)
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 3; c++)
                if (m[r * 4 + c] != (r == c ? 1f : 0f)) return false;
        return true;
    }

    // 行ベクトル × 行列 (CryEngine の 4×4 は平行移動が 4 行目)
    private static void Transform(CryMesh mesh, float[] m)
    {
        var p = mesh.Positions;
        for (int v = 0; v < mesh.VertexCount; v++)
        {
            float x = p[v * 3], y = p[v * 3 + 1], z = p[v * 3 + 2];
            p[v * 3] = x * m[0] + y * m[4] + z * m[8] + m[12];
            p[v * 3 + 1] = x * m[1] + y * m[5] + z * m[9] + m[13];
            p[v * 3 + 2] = x * m[2] + y * m[6] + z * m[10] + m[14];
        }
    }
}
