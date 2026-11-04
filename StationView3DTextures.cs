using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D (新しい方式) にゲームの画像 (テクスチャ) を貼るための、材質 → 色の画像の決め方と、画像 (PNG) の置き場 (2026-11-05、利用者の指示
// 「引いたときは色はビットマップ貼り付けにしてほしい」「端末は元々も色を貼ってほしい」「速度が落ちないなら」)。
//   色の画像の選び方は、前のステーションの 3D (v2.5.4 の Station3DTextures。starbreaker v0.3.2 の pipeline/textures.rs と同じ) の決まり:
//     材質 (部品の submesh の材質の番号) の shader (小文字・_ なし) が hardsurface・layerblendv2・layer・illum で層 (MatLayers) があれば、層 1 の .mtl の最初の材質の TexSlot1。
//     それで決まらなければ、その材質の TexSlot1 (diffuse_tex。shader が organic のものは色の画像ではないので使わない)。
//     繰り返し (UVTiling) は、その材質の MatLayers の層 1 のもの (無ければ 1)。.mtl の読み取りは船の SocpakGlb.ParseMtl。
//   画像: .tif → .dds (分けたファイル .dds.8〜.1 は大きい順の細かさ)。長い辺が上限 (設定「ステーションのテクスチャ (px)」。既定 512) 以下になる細かさ (mip) を選び、
//     その細かさの分けたファイルだけを読んで戻し (DdsImage.DecodeRgba。BC6H など戻せない形は画像なし = 単色)、PNG (RustPng) にして
//     3D の置き場所 (Ship3DExports.Folder) の stations3d_textures\<鍵>.png に置く。鍵 = .dds のパス + 細かさ + Data.p4k の目次の CRC (元と分けたファイル) の SHA-256 の頭 32 桁
//     (パッチで画像が変われば別の鍵。設定の上限を変えれば別の細かさ = 別の鍵)。ディスクにあれば戻さない (PNG の頭から大きさだけ読む)。1 枚は 1 回だけ (Lazy)。
//   ビューアは仮想ホスト (Ship3DExports.CacheHost) の /stations3d_textures/<鍵>.png で読む。置いた PNG は消さない (容量の数えには入れない。親の指示に無いため)
internal static class StationView3DTextures
{
    public const string FolderName = "stations3d_textures";
    public const int DefaultMaxPx = 512;

    // 画像 1 つ (材質の繰り返し・色合いごと)。Key = ファイル名の頭 + "|" + 繰り返し + "|" + 色合い (区画のファイルに書く画像の番号は、Session がこの Key に付ける)。
    // Tint: 材質の層 1 の TintColor (層が無ければ材質の Diffuse。どちらも無ければ白) (0〜1 の r,g,b)。hardsurface/layerblend 系は灰色の下地の画像に層の色を掛けて色を出すので、
    // ビューアは map × color で描く (2026-11-05、利用者の報告「かなり灰色になった」)
    public sealed record TexInfo(string Key, string File, int W, int H, float Tiling, float[] Tint);

    private static readonly object Lock = new();
    private static P4kArchive? s_p4k;
    private static ConcurrentDictionary<string, Lazy<TexInfo?>> s_resolved = new(StringComparer.Ordinal);   // "mtl|材質の番号|上限|置き場" → 結果
    private static ConcurrentDictionary<string, SocpakGlb.MtlFile?> s_mtls = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, TexInfo> s_registry = new(StringComparer.Ordinal);   // Key → 画像
    public static int RegistryCount => s_registry.Count;
    public static TexInfo? InfoOf(string key) => s_registry.TryGetValue(key, out var t) ? t : null;

    private static readonly HashSet<string> Layered = new(StringComparer.Ordinal) { "hardsurface", "layerblendv2", "layer", "illum" };
    private static readonly HashSet<string> NoDiffuseShaders = new(StringComparer.Ordinal) { "organic" };

    // 材質 (mtlPath = .mtl の p4k の中のパス、matId = submesh の材質の番号) の色の画像。無い・戻せない・上限 0 なら null
    public static TexInfo? Resolve(P4kArchive p4k, string folder, string mtlPath, int matId, int maxPx)
    {
        if (maxPx <= 0 || mtlPath.Length == 0 || matId < 0) return null;
        lock (Lock)
        {
            // Data.p4k が替わったら (パッチ) 覚えたものを捨てる
            if (!ReferenceEquals(s_p4k, p4k)) { s_p4k = p4k; s_resolved = new(StringComparer.Ordinal); s_mtls = new(StringComparer.OrdinalIgnoreCase); }
        }
        var ck = FormattableString.Invariant($"{mtlPath.ToLowerInvariant()}|{matId}|{maxPx}|{folder}");
        var lazy = s_resolved.GetOrAdd(ck, _ => new Lazy<TexInfo?>(() => ResolveCore(p4k, folder, mtlPath, matId, maxPx), LazyThreadSafetyMode.ExecutionAndPublication));
        return lazy.Value;
    }

    private static SocpakGlb.MtlFile? Mtl(P4kArchive p4k, string path) => s_mtls.GetOrAdd(path, p =>
    {
        int e = p4k.Find(p);
        if (e < 0) return null;
        try { return SocpakGlb.ParseMtl(p4k.Read(e)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }   // CryXmlB でない・壊れている .mtl は材質なし (単色)
    });

    // 材質 → .dds のパス・繰り返し・色合い (前のステーションの 3D の決まり + 層 1 の TintColor)。無ければ null。
    // 色合い: 層 (MatLayers) があれば層 1 の TintColor (ParseMtl は属性が無ければ白)、層が無ければ材質の Diffuse (無ければ白)。0〜1 に収める
    internal static (string Dds, float Tiling, float[] Tint)? TextureOf(P4kArchive p4k, string mtlPath, int matId)
    {
        var mtl = Mtl(p4k, mtlPath);
        if (mtl == null || matId >= mtl.Materials.Count) return null;
        var m = mtl.Materials[matId];
        var shader = m.Shader.ToLowerInvariant().Replace("_", "");
        string? tex = null; float tiling = 1;
        float[] tint = m.Layers.Count > 0 ? m.Layers[0].Tint : m.Diffuse;
        if (m.Layers.Count > 0)
        {
            if (Layered.Contains(shader))
            {
                var lm = Mtl(p4k, SocpakExporter.DataCorePathToP4k(m.Layers[0].Path));
                if (lm != null && lm.Materials.Count > 0) tex = lm.Materials[0].DiffuseTex;
            }
            if (m.Layers[0].UvTiling > 0 && float.IsFinite(m.Layers[0].UvTiling)) tiling = m.Layers[0].UvTiling;
        }
        if (string.IsNullOrEmpty(tex) && !string.IsNullOrEmpty(m.DiffuseTex) && !NoDiffuseShaders.Contains(shader)) tex = m.DiffuseTex;
        if (string.IsNullOrEmpty(tex) || tex.StartsWith('$')) return null;
        var dds = tex.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ? tex[..^4] + ".dds" : tex;
        var tn = new float[3];
        for (int k = 0; k < 3; k++) { var v = tint != null && tint.Length > k && float.IsFinite(tint[k]) ? tint[k] : 1f; tn[k] = Math.Clamp(v, 0f, 1f); }
        return (SocpakExporter.DataCorePathToP4k(dds), tiling, tn);
    }

    private static TexInfo? ResolveCore(P4kArchive p4k, string folder, string mtlPath, int matId, int maxPx)
    {
        if (TextureOf(p4k, mtlPath, matId) is not { } t) return null;
        var (ddsP4k, tiling, tint) = t;
        int e = p4k.Find(ddsP4k);
        if (e < 0) return null;
        // 分けたファイル (.dds.8〜.1。大きい番号が細かい)。並びは FromSplit と同じ (8 → 1 の順にあるもの)
        var sibs = new List<(int N, int Idx)>();
        for (int n = 8; n >= 1; n--) { int j = p4k.Find($"{ddsP4k}.{n}"); if (j >= 0) sibs.Add((n, j)); }
        byte[] baseData;
        try { baseData = p4k.Read(e); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ZstdSharp.ZstdException) { return null; }
        if (baseData.Length < 128 || baseData[0] != 'D' || baseData[1] != 'D' || baseData[2] != 'S' || baseData[3] != ' ') return null;
        uint h = BinaryPrimitives.ReadUInt32LittleEndian(baseData.AsSpan(12)), w = BinaryPrimitives.ReadUInt32LittleEndian(baseData.AsSpan(16));
        int total = (int)Math.Max(1, BinaryPrimitives.ReadUInt32LittleEndian(baseData.AsSpan(28)));
        // 長い辺が maxPx 以下になる細かさ (いちばん細かい側から数えて L 段目)。無ければいちばん粗いもの
        long side = Math.Max(w, h);
        int L = 0;
        while (L < total - 1 && (side >> L) > maxPx) L++;
        var sb = new StringBuilder();
        sb.Append(ddsP4k.ToLowerInvariant()).Append("|mip").Append(L).Append('|').Append(p4k.Crc(e).ToString("X8"));
        foreach (var (n, j) in sibs) sb.Append('|').Append(n).Append(':').Append(p4k.Crc(j).ToString("X8"));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32].ToLowerInvariant();
        var file = key + ".png";
        var path = Path.Combine(folder, file);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var rkey = key + "|" + tiling.ToString("R", inv) + "|" + string.Join(",", tint.Select(v => v.ToString("R", inv)));
        if (PngSize(path) is { } sz) return Register(new TexInfo(rkey, file, sz.W, sz.H, tiling, tint));
        // 戻す: 要る細かさの分けたファイルだけ読む (ほかの分けたファイルは空にして数だけ合わせる。alpha の分けたファイルは読まない)
        DdsImage img;
        byte[] rgba; int dw, dh;
        try
        {
            img = DdsImage.FromSplit(baseData, suf =>
            {
                if (suf.EndsWith('a')) return null;
                if (!int.TryParse(suf.AsSpan(1), out var n)) return null;
                int pos = sibs.FindIndex(x => x.N == n);
                if (pos < 0) return null;
                return pos == L ? p4k.Read(sibs[pos].Idx) : [];
            });
            (dw, dh) = img.Dimensions(L);
            rgba = img.DecodeRgba(L);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }   // BC6H・読めない・足りない → 画像なし (単色)
        byte[] png;
        try { png = RustPng.EncodeRgba8(dw, dh, rgba); }
        catch (ArgumentException) { return null; }
        try
        {
            Directory.CreateDirectory(folder);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, png);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        return Register(new TexInfo(rkey, file, dw, dh, tiling, tint));
    }

    private static TexInfo Register(TexInfo t) { s_registry[t.Key] = t; return t; }

    // PNG の頭 (IHDR) から大きさ。無い・PNG でなければ null
    private static (int W, int H)? PngSize(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = File.OpenRead(path);
            var head = new byte[24];
            if (fs.Read(head, 0, 24) < 24) return null;
            if (head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return null;
            int w = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(16)), h = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(20));
            return w > 0 && h > 0 ? (w, h) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
