using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D 表示の画像 (テクスチャ) (2026-11-02、利用者の指示「入れてほしいが粗目でいい」)。
//   調べ (調査担当 F。scratchpad\tex の tex.html・scripts\resolve.py) に従う: starbreaker 0.2.2 のまま、
//     ・GLB (socpak export --materials none) の材質の extras に、層の .mtl (layers[].path)・色の画像 (diffuse_tex)・元の名前 (source_name)・shader がある
//     ・色の画像の選び方は starbreaker v0.3.2 の pipeline/textures.rs (715〜737 行) と同じ: 層があり shader が HardSurface・LayerBlend_V2・Layer・Illum なら、
//       層 1 の .mtl の TexSlot1、それ以外は diffuse_tex。繰り返し (UVTiling) は材質の名前の頭 (… _mtl_ の前) の .mtl (親) の、元の名前の材質の MatLayers の層 1。
//       色合いは GLB の baseColorFactor のまま (ビューアで画像 × 色)
//   取り出し: .mtl は `p4k extract --regex … --convert cryxml`、画像は `dds decode <dds> --mip 3` (粗め: 2048 px の画像なら 256 px) を 1 枚ずつ。
//     取り出せない細かさ (小さい画像) は、細かさを 1 段ずつ上げて試す (2・1・0)
//   置き場所: ステーションどうしで使い回す共通のフォルダ (置き場所の stations3d\_textures)。画像は JPEG (透明の無い画像) か PNG (透明のある画像)。
//     表 (_textures\index.json) に、元の画像 (.dds とその細かさの分けたファイル .dds.N) の Data.p4k の中の大きさと CRC を覚え、同じなら取り直さない
//   ゲームの画像は配布物に入れない (利用者の Data.p4k から取り出して置き場所に保存する)
internal static class Station3DTextures
{
    public const string FolderName = "_textures";
    public const string IndexFile = "index.json";
    public const string Mark = "StarCitizenJapaneseTextCreater.station3d.textures";
    // 取り出す細かさ (mip。0 がいちばん細かい)。3 は 1/8 の大きさ
    public const int Mip = 3;
    // JPEG の質
    public const int JpegQuality = 85;
    // p4k extract 1 回で取り出す画像の数 (正規表現がコマンドラインの長さの上限 (32,767 文字) に収まるように。1 枚 約 80 文字)
    public const int BatchTextures = 150;

    private static void Station3DExportsLog(string msg) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D] {msg}");

    // 細かさで分けた DDS (元の .dds = ヘッダーといちばん粗い細かさたち、.dds.N = 細かさ (最大の N − N + …) 1 つずつ) から、細かさ skip を頭にした 1 つの DDS を組み直す。
    //   parts は .dds.(最大 − skip) から .dds.1 の順 (細かいほうから)。ヘッダーの高さ・幅・大きさ・細かさの数を直す。DX10 の拡張ヘッダーも見る。組み直せなければ null
    //   (DDS の中身の並びは、ヘッダーの後に細かさ 0・1・2… の順。分けたファイルは、いちばん大きい N が細かさ 0。写しで 3 枚、starbreaker の dds decode --mip 3 と同じ画像になることを確かめた)
    internal static byte[]? Rebuild(byte[] baseDds, List<byte[]> parts, int skip)
    {
        if (baseDds.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(baseDds) != 0x20534444) return null;   // "DDS "
        var dx10 = baseDds[84] == 'D' && baseDds[85] == 'X' && baseDds[86] == '1' && baseDds[87] == '0';
        var head = dx10 ? 148 : 128;
        if (baseDds.Length < head) return null;
        var h = BinaryPrimitives.ReadUInt32LittleEndian(baseDds.AsSpan(12));
        var w = BinaryPrimitives.ReadUInt32LittleEndian(baseDds.AsSpan(16));
        var lin = BinaryPrimitives.ReadUInt32LittleEndian(baseDds.AsSpan(20));
        var mips = BinaryPrimitives.ReadUInt32LittleEndian(baseDds.AsSpan(28));
        if (skip > 0 && mips <= skip) return null;
        var outHead = baseDds.AsSpan(0, head).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(outHead.AsSpan(12), Math.Max(1u, h >> skip));
        BinaryPrimitives.WriteUInt32LittleEndian(outHead.AsSpan(16), Math.Max(1u, w >> skip));
        BinaryPrimitives.WriteUInt32LittleEndian(outHead.AsSpan(20), Math.Max(1u, lin >> (2 * skip)));
        if (mips > 0) BinaryPrimitives.WriteUInt32LittleEndian(outHead.AsSpan(28), (uint)(mips - skip));
        using var ms = new MemoryStream();
        ms.Write(outHead);
        foreach (var p in parts) ms.Write(p);
        ms.Write(baseDds.AsSpan(head));
        return ms.ToArray();
    }
    // 層の材質を使う shader (小文字・_ なし)
    private static readonly HashSet<string> Layered = new(StringComparer.Ordinal) { "hardsurface", "layerblendv2", "layer", "illum" };
    // diffuse_tex が色の画像ではない shader (小文字・_ なし)
    private static readonly HashSet<string> NoDiffuseShaders = new(StringComparer.Ordinal) { "organic" };

    public static string Folder => Path.Combine(Station3DExports.Folder, FolderName);

    // 共通のフォルダの大きさ (容量の数えに入れる)
    public static long Bytes()
    {
        try
        {
            var d = new DirectoryInfo(Folder);
            return d.Exists ? d.GetFiles().Sum(f => f.Length) : 0;
        }
        catch { return 0; }
    }

    // 材質 1 つの画像の決め方の結果
    internal sealed record MatTex(string Dds, double Tiling);

    // GLB 1 つの材質 (名前・shader・層 1 の .mtl・diffuse_tex・元の名前)
    private sealed record GlbMat(string Name, string Shader, string Layer1, string Diffuse, string Source);

    internal sealed class Result
    {
        // GLB (files の番号) ごとの、材質の名前 → (画像の番号 (Files の), 繰り返し)
        public List<Dictionary<string, (int Tex, double Tiling)>> PerFile { get; } = new();
        // 画像のファイル名 (共通のフォルダの中)
        public List<string> Files { get; } = new();
        // 暗い画像 (DarkLuma・DarkShare) の Files の番号。ビューアは、透明を使わない材質にはこの画像を貼らない
        public List<int> Dark { get; } = new();
        // 画像ごとの平均の明るさ (0〜255。分からなければ -1)。Files と同じ並び
        public List<int> Luma { get; } = new();
        // 照合用の元ファイル (.mtl・.dds・.dds.N。Ship3DEstimator.Normalize の形)
        public List<string> Sources { get; } = new();
        public int Materials, MaterialsWithTex, Textures, Decoded, Reused, Missing, Fallback;
        public double Seconds;
    }

    // GLB の JSON の部分を読む
    private static JsonDocument ReadGlbJson(string path)
    {
        using var fs = File.OpenRead(path);
        var head = new byte[20];
        fs.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x46546C67) throw new InvalidDataException($"{Path.GetFileName(path)} は GLB ではありません");
        var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        var json = new byte[len];
        fs.ReadExactly(json);
        return JsonDocument.Parse(json);
    }

    private static List<GlbMat> ReadMaterials(string glb)
    {
        var res = new List<GlbMat>();
        using var doc = ReadGlbJson(glb);
        if (!doc.RootElement.TryGetProperty("materials", out var mats) || mats.ValueKind != JsonValueKind.Array) return res;
        foreach (var m in mats.EnumerateArray())
        {
            string S(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var name = S(m, "name");
            var ex = m.TryGetProperty("extras", out var x) ? x : default;
            var layer1 = "";
            if (ex.ValueKind == JsonValueKind.Object && ex.TryGetProperty("layers", out var ls) && ls.ValueKind == JsonValueKind.Array && ls.GetArrayLength() > 0)
                layer1 = S(ls[0], "path");
            res.Add(new GlbMat(name, ex.ValueKind == JsonValueKind.Object ? S(ex, "shader") : "", layer1,
                ex.ValueKind == JsonValueKind.Object ? S(ex, "diffuse_tex") : "", ex.ValueKind == JsonValueKind.Object ? S(ex, "source_name") : ""));
        }
        return res;
    }

    // 材質の名前の頭 (… _mtl_ の前) = 親の .mtl の名前
    private static string ParentOf(string name)
    {
        var k = name.IndexOf("_mtl_", StringComparison.Ordinal);
        return k > 0 ? name[..k] : "";
    }

    private static string DdsOf(string tex)
    {
        var t = tex.Replace('\\', '/');
        if (t.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) t = t[..^4] + ".dds";
        return "Data/" + t;
    }

    private static string RxOf(IEnumerable<string> paths) =>
        "(?i)^Data.(" + string.Join("|", paths.Select(p => Regex.Escape(p).Replace("/", "."))) + ")$";

    // 取り出した XML の材質を読む (読めなければ null)
    private static XElement? LoadXml(string path)
    {
        try { return XDocument.Load(path).Root; } catch { return null; }
    }

    private static string? TexSlot(XElement m, string slot) =>
        m.Element("Textures")?.Elements("Texture").FirstOrDefault(t => (string?)t.Attribute("Map") == slot)?.Attribute("File")?.Value;

    // 共通のフォルダの表 (画像の元 → ファイル名と元の印)
    private sealed class IndexEntry
    {
        public string File { get; set; } = "";
        public bool Dark { get; set; }
        public int Luma { get; set; } = -1;
        public List<Ship3DExports.SourceFile> Srcs { get; set; } = new();
    }
    private sealed class IndexContent
    {
        public string App { get; set; } = Mark;
        public Dictionary<string, IndexEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    // 表と共通のフォルダは、同時に動くステーションの書き出しで取り合わないよう、1 本ずつ触る
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    private static IndexContent ReadIndex()
    {
        try
        {
            var p = Path.Combine(Folder, IndexFile);
            if (!File.Exists(p)) return new IndexContent();
            var c = JsonSerializer.Deserialize<IndexContent>(File.ReadAllText(p));
            if (c == null || c.App != Mark) return new IndexContent();
            c.Entries = new Dictionary<string, IndexEntry>(c.Entries, StringComparer.OrdinalIgnoreCase);
            return c;
        }
        catch { return new IndexContent(); }
    }

    private static void WriteIndex(IndexContent c)
    {
        Directory.CreateDirectory(Folder);
        var p = Path.Combine(Folder, IndexFile);
        var tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c), new UTF8Encoding(false));
        File.Move(tmp, p, overwrite: true);
    }

    // 画像の元 (.dds と .dds.1〜.dds.9 のうち Data.p4k にあるもの) の大きさと CRC
    private static List<Ship3DExports.SourceFile> DdsSources(string dds, Dictionary<ulong, (long Size, uint Crc)> p4k)
    {
        var res = new List<Ship3DExports.SourceFile>();
        var basePath = Ship3DEstimator.Normalize(dds);
        foreach (var p in new[] { basePath }.Concat(Enumerable.Range(1, 9).Select(i => $"{basePath}.{i}")))
            if (p4k.TryGetValue(Ship3DEstimator.Hash(p), out var v)) res.Add(new Ship3DExports.SourceFile { Path = p, Size = v.Size, Crc = v.Crc });
        return res;
    }

    private static bool SameSrcs(List<Ship3DExports.SourceFile> a, List<Ship3DExports.SourceFile> b) =>
        a.Count == b.Count && a.Zip(b).All(t => t.First.Path == t.Second.Path && t.First.Size == t.Second.Size && t.First.Crc == t.Second.Crc);

    // 画像のファイル名 (元のパスから。英数字と _ - 以外は _)
    private static string FileStem(string dds)
    {
        var s = dds.Replace('\\', '/');
        if (s.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)) s = s[5..];
        if (s.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return Regex.Replace(s.Replace("/", "__"), @"[^A-Za-z0-9_\-]", "_").ToLowerInvariant();
    }

    // PNG を、透明の無い画像は JPEG に、透明のある画像は PNG のまま、共通のフォルダに書く。書いたファイル名を返す
    // 暗い画像の割合の決まり: 明るさ (0〜255) が DarkLuma 未満の画素が DarkShare 以上の画像を「暗い画像」とする
    // (光る所の形だけを持つ画像 light_a_diff (黒い画素 45%) など。色の画像として貼ると、黒地に白の四角が並んだ模様になり、パッドの縁の白黒の模様の原因だった。
    //  2026-11-02、写しの Checkmate のパッドで、模様の所の材質を調べて確かめた (lowtech_alpha_ext_a の glow)。写しの 580 枚のうち 0.4 以上は 35 枚)
    public const int DarkLuma = 24;
    public const double DarkShare = 0.4;

    private static string Convert(string png, string stem, out bool dark, out int luma)
    {
        BitmapSource src;
        using (var fs = File.OpenRead(png))
        {
            var dec = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            src = dec.Frames[0];
        }
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        var alpha = false;
        for (int i = 3; i < px.Length; i += 4) if (px[i] < 250) { alpha = true; break; }
        long darkPx = 0;
        for (int i = 0; i + 2 < px.Length; i += 4) if ((px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000 < DarkLuma) darkPx++;
        dark = darkPx >= DarkShare * (w * (long)h);
        // 平均の明るさ (0〜255)。ビューアが暗い画像を貼った材質を明るくするのに使う
        long sum = 0;
        for (int i = 0; i + 2 < px.Length; i += 4) sum += (px[i] * 114 + px[i + 1] * 587 + px[i + 2] * 299) / 1000;
        luma = (int)(sum / Math.Max(1L, w * (long)h));
        BitmapEncoder enc = alpha ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = JpegQuality };
        enc.Frames.Add(BitmapFrame.Create(alpha ? bgra : new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0)));
        var name = stem + (alpha ? ".png" : ".jpg");
        var final = Path.Combine(Folder, name);
        var tmp = final + ".tmp";
        using (var outFs = File.Create(tmp)) enc.Save(outFs);
        File.Move(tmp, final, overwrite: true);
        return name;
    }

    // 画像を決めて取り出す。glbs は書き出した GLB のフルパス (files の順)。work は作業用のフォルダ (.mtl・PNG の一時の置き場所)
    //   starbreaker は自分の枠 1 本のほかに、tryExtra が許すだけ並行に動かす (地図・3D の書き出しと同じ)
    internal static async Task<Result> BuildAsync(IReadOnlyList<string> glbs, string exe, Ship3DExports.P4kStamp stamp, string work, CancellationToken ct,
        Action<string>? onStage = null, Func<Task<bool>>? tryExtra = null, Action? releaseExtra = null)
    {
        var sw = Stopwatch.StartNew();
        var res = new Result();
        var tmp = Path.Combine(work, "_tex");
        Directory.CreateDirectory(tmp);
        // 1. 材質を読む
        var mats = glbs.Select(ReadMaterials).ToList();
        var layers = mats.SelectMany(l => l).Select(m => m.Layer1).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
        var parents = mats.SelectMany(l => l).Select(m => ParentOf(m.Name)).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal).ToList();
        // 2. .mtl を取り出す (層の .mtl はパスで、親の .mtl は名前で)
        onStage?.Invoke("材質 (.mtl) の取り出し");
        var mtlDir = Path.Combine(tmp, "mtl");
        var pmtlDir = Path.Combine(tmp, "pmtl");
        async Task Extract(string rx, string dir)
        {
            var args = $"p4k extract --p4k \"{stamp.Path}\" -o \"{dir}\" --regex \"{rx}\" --convert cryxml";
            var (code, tail) = await StarBreakerRunner.RunAsync(exe, args, null, null, ct);
            if (code != 0) throw new InvalidOperationException($"starbreaker p4k extract が失敗しました (終了コード {code}): {tail.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}");
        }
        if (layers.Count > 0) await Extract(RxOf(layers), mtlDir);
        if (parents.Count > 0) await Extract(@"(?i)[\\/](" + string.Join("|", parents.Select(Regex.Escape)) + @")\.mtl$", pmtlDir);
        var p4k = await Ship3DP4kIndex.GetAsync(stamp);
        var srcSet = new SortedSet<string>(StringComparer.Ordinal);
        // 取り出した .mtl (パスの小文字 → ファイル)
        Dictionary<string, string> Files(string dir) => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(dir, f).Replace('\\', '/').ToLowerInvariant(), f => f)
            : new();
        var lidx = Files(mtlDir);
        var pidx = Files(pmtlDir).GroupBy(kv => Path.GetFileNameWithoutExtension(kv.Key)).ToDictionary(g => g.Key, g => g.OrderBy(kv => kv.Key, StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);
        foreach (var l in layers) srcSet.Add(Ship3DEstimator.Normalize("data/" + l));
        foreach (var kv in pidx) srcSet.Add(Ship3DEstimator.Normalize(kv.Value.Key));
        // 3. 材質ごとに画像と繰り返しを決める
        var texIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ddsList = new List<string>();
        var xmlCache = new Dictionary<string, XElement?>(StringComparer.OrdinalIgnoreCase);
        XElement? Xml(string f) => xmlCache.TryGetValue(f, out var x) ? x : xmlCache[f] = LoadXml(f);
        foreach (var list in mats)
        {
            var map = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
            foreach (var m in list)
            {
                res.Materials++;
                string? tex = null;
                double tiling = 1;
                var shader = m.Shader.Replace("_", "").ToLowerInvariant();
                if (m.Layer1.Length > 0 && Layered.Contains(shader) && lidx.TryGetValue(("data/" + m.Layer1).ToLowerInvariant(), out var lf) && Xml(lf) is { } lroot)
                {
                    var lm = lroot.Name.LocalName == "Material" && lroot.Element("Textures") != null ? lroot : lroot.Descendants("Material").FirstOrDefault() ?? lroot;
                    tex = TexSlot(lm, "TexSlot1");
                    if (pidx.TryGetValue(ParentOf(m.Name), out var pf) && Xml(pf.Value) is { } proot)
                    {
                        var sub = proot.DescendantsAndSelf("Material").FirstOrDefault(e => string.Equals((string?)e.Attribute("Name"), m.Source, StringComparison.OrdinalIgnoreCase));
                        var layer = sub?.Element("MatLayers")?.Elements("Layer").FirstOrDefault();
                        if (layer != null && double.TryParse((string?)layer.Attribute("UVTiling"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) && t > 0) tiling = t;
                    }
                }
                // shader が Organic (岩・洞窟の材質) の diffuse_tex は色の画像ではなく、材質を混ぜる割合の画像 (…_blend。赤・緑・青の割合)。
                // 貼ると岩が赤と緑のまだらになった (2026-11-02、写しの GrimHEX で確かめた) ので、この shader では diffuse_tex を使わない (色合いのまま)
                if (string.IsNullOrEmpty(tex) && m.Diffuse.Length > 0 && !NoDiffuseShaders.Contains(shader)) tex = m.Diffuse;
                if (string.IsNullOrEmpty(tex) || tex.StartsWith('$')) continue;
                var dds = DdsOf(tex);
                if (!texIndex.TryGetValue(dds, out var ti)) { ti = ddsList.Count; texIndex[dds] = ti; ddsList.Add(dds); }
                map[m.Name] = (ti, tiling);
                res.MaterialsWithTex++;
            }
            res.PerFile.Add(map);
        }
        // 4. 画像を取り出す (共通のフォルダの表で同じ元のものは取り直さない)
        onStage?.Invoke($"画像の取り出し ({ddsList.Count} 枚)");
        var fileOf = new string?[ddsList.Count];
        var darkOf = new bool[ddsList.Count];
        var lumaOf = Enumerable.Repeat(-1, ddsList.Count).ToArray();
        var todo = new List<int>();
        IndexContent index;
        await Gate.WaitAsync(ct);
        try
        {
            index = ReadIndex();
            for (int i = 0; i < ddsList.Count; i++)
            {
                var srcs = DdsSources(ddsList[i], p4k);
                foreach (var s in srcs) srcSet.Add(s.Path);
                if (srcs.Count == 0) { res.Missing++; continue; }
                if (index.Entries.TryGetValue(ddsList[i], out var e) && SameSrcs(e.Srcs, srcs) && File.Exists(Path.Combine(Folder, e.File))) { fileOf[i] = e.File; darkOf[i] = e.Dark; lumaOf[i] = e.Luma; res.Reused++; }
                else todo.Add(i);
            }
        }
        finally { Gate.Release(); }
        // 4-1. まとめて取り出す: 画像ごとに、元の .dds と、細かさで分けたファイル (.dds.N。N が大きいほど細かい。いちばん大きい N が細かさ 0) のうち
        //   粗いほうの .dds.1〜.dds.(最大 − Mip) だけを、p4k extract 1 回 (BatchTextures 枚ずつ) で取り出し、細かさ Mip を頭にした .dds を組み直して (Rebuild)、
        //   dds to-png-all 1 回で PNG にする (1 枚ずつ dds decode を動かすと 1 枚 1.4 秒かかったため。2026-11-02 の実測は報告)。
        //   組み直せない・PNG にならなかった画像だけ、1 枚ずつ dds decode --mip で取り出す
        var decoded = new ConcurrentDictionary<int, string>();
        var errors = new ConcurrentBag<string>();
        var rawDir = Path.Combine(tmp, "raw");
        var ddsDir = Path.Combine(tmp, "dds");
        var pngDir = Path.Combine(tmp, "png");
        Directory.CreateDirectory(ddsDir);
        var keep = new Dictionary<int, int>();   // 画像の番号 → 取り出す .dds.N の最大の N (0 なら元の .dds だけ)
        foreach (var i in todo)
        {
            var maxN = 0;
            var basePath = Ship3DEstimator.Normalize(ddsList[i]);
            while (maxN < 9 && p4k.ContainsKey(Ship3DEstimator.Hash($"{basePath}.{maxN + 1}"))) maxN++;
            keep[i] = maxN - Math.Min(Mip, maxN);
        }
        foreach (var chunk in todo.Chunk(BatchTextures))
        {
            ct.ThrowIfCancellationRequested();
            var rx = "(?i)^Data.(" + string.Join("|", chunk.Select(i =>
            {
                var t = ddsList[i].Replace('\\', '/');
                t = t.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ? t[5..] : t;
                return Regex.Escape(t).Replace("/", ".") + (keep[i] > 0 ? $@"(\.[1-{keep[i]}])?" : "");
            })) + ")$";
            var (code, tail) = await StarBreakerRunner.RunAsync(exe, $"p4k extract --p4k \"{stamp.Path}\" -o \"{rawDir}\" --regex \"{rx}\"", null, null, ct);
            if (code != 0) Station3DExportsLog($"画像の取り出し (p4k extract) が失敗しました (終了コード {code})。1 枚ずつ取り出します: {tail.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}");
        }
        var raw = Directory.Exists(rawDir)
            ? Directory.EnumerateFiles(rawDir, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(rawDir, f).Replace('\\', '/').ToLowerInvariant(), f => f)
            : new Dictionary<string, string>();
        var built = new List<int>();
        foreach (var i in todo)
        {
            try
            {
                var key = ddsList[i].Replace('\\', '/').ToLowerInvariant();
                if (!raw.TryGetValue(key, out var baseFile)) continue;
                var parts = new List<byte[]>();
                for (int n = keep[i]; n >= 1; n--)
                {
                    if (!raw.TryGetValue($"{key}.{n}", out var f)) { parts = null!; break; }
                    parts.Add(File.ReadAllBytes(f));
                }
                if (parts == null) continue;
                var maxN = 0;
                var basePath = Ship3DEstimator.Normalize(ddsList[i]);
                while (maxN < 9 && p4k.ContainsKey(Ship3DEstimator.Hash($"{basePath}.{maxN + 1}"))) maxN++;
                if (Rebuild(File.ReadAllBytes(baseFile), parts, Math.Min(Mip, maxN)) is { } dds)
                {
                    File.WriteAllBytes(Path.Combine(ddsDir, $"t{i}.dds"), dds);
                    built.Add(i);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException) { }
        }
        if (built.Count > 0)
        {
            var (code, _) = await StarBreakerRunner.RunAsync(exe, $"dds to-png-all -i \"{ddsDir}\" -o \"{pngDir}\"", null, null, ct);
            if (code != 0) Station3DExportsLog($"画像の変換 (dds to-png-all) が終了コード {code} で終わりました。PNG にならなかった画像は 1 枚ずつ取り出します");
            foreach (var i in built)
            {
                var png = Path.Combine(pngDir, $"t{i}.png");
                if (File.Exists(png)) decoded[i] = png;
            }
        }
        // 4-2. まとめて取り出せなかった画像は 1 枚ずつ (細かさ Mip、取り出せなければ 1 段ずつ細かく)
        var queue = new ConcurrentQueue<int>(todo.Where(i => !decoded.ContainsKey(i)));
        res.Fallback = queue.Count;
        async Task Worker(bool extra)
        {
            try
            {
                while (!ct.IsCancellationRequested && queue.TryDequeue(out var i))
                {
                    var png = Path.Combine(tmp, $"t{i}.png");
                    var ok = false;
                    for (var mip = Mip; mip >= 0 && !ok; mip--)
                    {
                        var (code, _) = await StarBreakerRunner.RunAsync(exe, $"dds decode \"{ddsList[i]}\" \"{png}\" --mip {mip} --p4k \"{stamp.Path}\"", null, null, ct);
                        ok = code == 0 && File.Exists(png);
                    }
                    if (!ok) { errors.Add(ddsList[i]); continue; }
                    decoded[i] = png;
                }
            }
            finally { if (extra) releaseExtra?.Invoke(); }
        }
        var workers = new List<Task> { Worker(false) };
        while (!queue.IsEmpty && tryExtra != null && workers.Count < 8 && await tryExtra()) workers.Add(Worker(true));
        await Task.WhenAll(workers);
        ct.ThrowIfCancellationRequested();
        // 5. JPEG・PNG にして共通のフォルダに置き、表を書く
        onStage?.Invoke("画像の書き込み");
        await Gate.WaitAsync(ct);
        try
        {
            index = ReadIndex();
            Directory.CreateDirectory(Folder);
            foreach (var (i, png) in decoded)
            {
                try
                {
                    var isDark = false;
                    var luma = -1;
                    var name = await Task.Run(() => Convert(png, FileStem(ddsList[i]), out isDark, out luma), ct);
                    fileOf[i] = name;
                    darkOf[i] = isDark;
                    lumaOf[i] = luma;
                    index.Entries[ddsList[i]] = new IndexEntry { File = name, Dark = isDark, Luma = luma, Srcs = DdsSources(ddsList[i], p4k) };
                    res.Decoded++;
                }
                catch (Exception e) when (e is IOException or NotSupportedException or FileFormatException or InvalidOperationException) { errors.Add($"{ddsList[i]}: {e.Message}"); }
            }
            WriteIndex(index);
        }
        finally { Gate.Release(); }
        res.Missing += errors.Count;
        // 6. 結果 (取り出せなかった画像を使う材質は外す)
        var finalIndex = new Dictionary<int, int>();
        for (int i = 0; i < ddsList.Count; i++)
            if (fileOf[i] is { } f) { finalIndex[i] = res.Files.Count; if (darkOf[i]) res.Dark.Add(res.Files.Count); res.Luma.Add(lumaOf[i]); res.Files.Add(f); }
        for (int k = 0; k < res.PerFile.Count; k++)
            res.PerFile[k] = res.PerFile[k].Where(kv => finalIndex.ContainsKey(kv.Value.Tex))
                .ToDictionary(kv => kv.Key, kv => (finalIndex[kv.Value.Tex], kv.Value.Tiling), StringComparer.Ordinal);
        res.MaterialsWithTex = res.PerFile.Sum(d => d.Count);
        res.Textures = res.Files.Count;
        res.Sources.AddRange(srcSet);
        res.Seconds = sw.Elapsed.TotalSeconds;
        try { Directory.Delete(tmp, recursive: true); } catch { }
        return res;
    }

    // 共通のフォルダのうち、どの scene.json からも使われていない画像を消す (ステーションの 3D を消した後に呼ぶ。書き出し中は呼ばない)。消した大きさを返す
    internal static long Collect(IEnumerable<string> scenePaths)
    {
        long freed = 0;
        Gate.Wait();
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sp in scenePaths)
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(sp));
                    if (doc.RootElement.TryGetProperty("textures", out var t) && t.ValueKind == JsonValueKind.Array)
                        foreach (var e in t.EnumerateArray()) if (e.GetString() is { } s) used.Add(Path.GetFileName(s));
                }
                catch { return 0; }   // 読めない scene.json があれば消さない (使っているかもしれない)
            }
            var index = ReadIndex();
            foreach (var f in new DirectoryInfo(Folder).GetFiles())
            {
                if (f.Name == IndexFile || used.Contains(f.Name)) continue;
                if (!(f.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))) continue;
                try { var len = f.Length; f.Delete(); freed += len; } catch { }
            }
            foreach (var k in index.Entries.Where(kv => !used.Contains(kv.Value.File)).Select(kv => kv.Key).ToList()) index.Entries.Remove(k);
            WriteIndex(index);
        }
        finally { Gate.Release(); }
        return freed;
    }
}
