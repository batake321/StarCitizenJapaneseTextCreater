using System.IO;
using System.IO.Compression;
using System.Text;

namespace StarCitizenJapaneseTextCreater;

// GLB の読み取りの結果 (StationMeshSet.Raw: socpak ごとの、表で選び分ける前のメッシュの名前・8 隅・形の数) の保存 (2026-11-02、利用者の指示
// 「孤立したフロアの判定を変えた場合でも全体の取り直し処理をすべて再試行しなくてもいい仕組み」)。
// 地図の JSON の横に <短い名前>.meshes.gz (gzip で圧縮したバイナリ) として置く (地図の JSON を書けた後に書く)。次の取り込みで、Data.p4k・木のハッシュ・
// この版 (Version)・同梱の部品の表 (station_mesh_kinds.json。どの部品を GLB から読むかを決める) が同じで、要る socpak が全部入っていれば、
// starbreaker を動かさずに、表での選び分け (StationFloorBuilder.FromRaw) から先 (床・階・道順・JSON) だけをやり直す。
// Version は、GLB の読み方 (ReadMeshes) や形の数 (StationShape・ShapeOf) の作り方を変えたときに上げる (上げると次の取り込みは全部取り直す)。
// 読めないファイル (壊れた・前の形) は消す
internal static class StationMeshCache
{
    public const int Version = 1;
    private const string Magic = "SCJTC-STATION-MESHES";

    public static string PathOf(string jsonPath) => Path.ChangeExtension(jsonPath, null) + ".meshes.gz";

    // 同梱の部品の表のハッシュ (無ければ "")
    public static string BundledKindsHash()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, StationMeshKinds.BundledFile);
            return File.Exists(p) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))) : "";
        }
        catch (IOException) { return ""; }
    }

    // 書く (一時ファイルに書いてから置き換える)
    public static void Save(string path, string stampKey, string treeHash, string kindsHash, StationMeshSet set) =>
        Write(path, stampKey, treeHash, kindsHash, set.Raw, set.Missing.ToHashSet(StringComparer.Ordinal));

    private static void Write(string path, string stampKey, string treeHash, string kindsHash, IReadOnlyDictionary<string, List<List<StationGlbMesh>>> raw, HashSet<string> missing)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz, Encoding.UTF8))
            {
                w.Write(Magic); w.Write(Version); w.Write(stampKey); w.Write(treeHash); w.Write(kindsHash);
                w.Write(raw.Count);
                foreach (var (key, chunks) in raw)
                {
                    w.Write(key); w.Write(missing.Contains(key));
                    w.Write(chunks.Count);
                    foreach (var meshes in chunks)
                    {
                        w.Write(meshes.Count);
                        foreach (var m in meshes) WriteMesh(w, m);
                    }
                }
            }
            File.Move(tmp, path, true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private sealed record Loaded(string StampKey, string TreeHash, string KindsHash, Dictionary<string, List<List<StationGlbMesh>>> Raw, HashSet<string> Missing);

    // 読む。無い・壊れている・版・Data.p4k・木のハッシュ・同梱の表が違う・要る socpak (keys) が入っていないときは null と理由。
    // Raw は socpak → GLB に出た子ごとのメッシュの並び (1 つの GLB に同じ socpak の子が 2 回出たときは 2 つ。StationMeshSet.Raw と同じ)
    public static (Dictionary<string, List<List<StationGlbMesh>>> Raw, HashSet<string> Missing)? TryLoad(string path, string stampKey, string treeHash, IEnumerable<string> keys, out string reason)
    {
        if (Read(path, out reason) is not { } l) return null;
        if (l.StampKey != stampKey) { reason = "Data.p4k が保存したときと違う"; return null; }
        if (l.TreeHash != treeHash) { reason = "ステーションの木が保存したときと違う"; return null; }
        if (l.KindsHash != BundledKindsHash()) { reason = "同梱の部品の表が保存したときと違う"; return null; }
        var lack = keys.Where(k => !l.Raw.ContainsKey(k)).ToList();
        if (lack.Count > 0) { reason = $"要る socpak が保存した読み取りの結果に無い ({lack.Count} 個。例: {lack[0]})"; return null; }
        return (l.Raw, l.Missing);
    }

    // 照合で Data.p4k の印だけを書き直したとき (StationMapExports.RewriteStamp): 保存の印が前の印 (oldKey) なら新しい印 (newKey) にする。書き直したら true
    public static bool Restamp(string path, string oldKey, string newKey)
    {
        if (Read(path, out _) is not { } l || l.StampKey != oldKey) return false;
        Write(path, newKey, l.TreeHash, l.KindsHash, l.Raw, l.Missing);
        return true;
    }

    // 頭と中身を読む。無い・読めない・形や版が違うときは null と理由 (読めない・形や版が違うファイルは消す)
    private static Loaded? Read(string path, out string reason)
    {
        reason = "";
        if (!File.Exists(path)) { reason = "保存した読み取りの結果が無い"; return null; }
        try
        {
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(gz, Encoding.UTF8);
            if (r.ReadString() != Magic) reason = "保存した読み取りの結果の形が違う";
            else if (r.ReadInt32() is var ver && ver != Version) reason = $"保存した読み取りの結果の版が違う ({ver}、今は {Version})";
            else
            {
                var stampKey = r.ReadString(); var treeHash = r.ReadString(); var kindsHash = r.ReadString();
                var raw = new Dictionary<string, List<List<StationGlbMesh>>>(StringComparer.Ordinal);
                var missing = new HashSet<string>(StringComparer.Ordinal);
                var n = Count(r);
                for (int i = 0; i < n; i++)
                {
                    var key = r.ReadString();
                    if (r.ReadBoolean()) missing.Add(key);
                    var nc = Count(r);
                    var chunks = new List<List<StationGlbMesh>>(nc);
                    for (int k = 0; k < nc; k++)
                    {
                        var c = Count(r);
                        var list = new List<StationGlbMesh>(c);
                        for (int j = 0; j < c; j++) list.Add(ReadMesh(r));
                        chunks.Add(list);
                    }
                    raw[key] = chunks;
                }
                return new Loaded(stampKey, treeHash, kindsHash, raw, missing);
            }
        }
        catch (Exception ex) { reason = $"保存した読み取りの結果を読めない ({ex.GetType().Name}: {ex.Message})"; }
        // 読めない・形や版が違う: 使えないので消す (次の取り込みで作り直す)
        try { File.Delete(path); } catch { }
        return null;
    }

    // 数の欄 (負・大きすぎるものは壊れたファイル)
    private static int Count(BinaryReader r)
    {
        var c = r.ReadInt32();
        if (c < 0 || c > 50_000_000) throw new InvalidDataException($"数の欄が壊れています ({c})");
        return c;
    }

    private static void WriteMesh(BinaryWriter w, StationGlbMesh m)
    {
        w.Write(m.Mesh);
        WritePts(w, m.Corners);
        var s = m.Shape;
        w.Write(s != null);
        if (s == null) return;
        w.Write(s.FlatArea); w.Write(s.SlopeArea); w.Write(s.DownArea); w.Write(s.TotalArea);
        w.Write(s.SlopeLo); w.Write(s.SlopeHi); w.Write(s.SlopeCoherence);
        w.Write(s.Levels.Count);
        foreach (var l in s.Levels) { w.Write(l.Z); w.Write(l.Area); w.Write(l.X); w.Write(l.Y); }
        WritePts(w, s.Hull);
        w.Write(s.WalkLo); w.Write(s.WalkHi); w.Write(s.SizeXY); w.Write(s.SizeZ);
        w.Write((int)s.Guess); w.Write(s.GuessRamp);
        w.Write(s.MainZ); w.Write(s.MainArea);
        WritePts(w, s.MainHull);
        w.Write(s.StairLo);
        w.Write(s.BigLevels.Count);
        foreach (var b in s.BigLevels) { w.Write(b.Z); w.Write(b.Area); WritePts(w, b.Hull); }
        WritePts(w, s.StepHull);
        w.Write(s.IsComposite);
    }

    private static StationGlbMesh ReadMesh(BinaryReader r)
    {
        var name = r.ReadString();
        var corners = ReadPts(r);
        if (!r.ReadBoolean()) return new StationGlbMesh(name, corners);
        var s = new StationFloorBuilder.StationShape
        {
            FlatArea = r.ReadDouble(), SlopeArea = r.ReadDouble(), DownArea = r.ReadDouble(), TotalArea = r.ReadDouble(),
            SlopeLo = r.ReadDouble(), SlopeHi = r.ReadDouble(), SlopeCoherence = r.ReadDouble(),
        };
        var nl = Count(r);
        for (int i = 0; i < nl; i++) s.Levels.Add((r.ReadDouble(), r.ReadDouble(), r.ReadDouble(), r.ReadDouble()));
        s.Hull = ReadPts(r);
        s.WalkLo = r.ReadDouble(); s.WalkHi = r.ReadDouble(); s.SizeXY = r.ReadDouble(); s.SizeZ = r.ReadDouble();
        s.Guess = (StationMeshKind)r.ReadInt32(); s.GuessRamp = r.ReadBoolean();
        s.MainZ = r.ReadDouble(); s.MainArea = r.ReadDouble();
        s.MainHull = ReadPts(r);
        s.StairLo = r.ReadDouble();
        var nb = Count(r);
        for (int i = 0; i < nb; i++) s.BigLevels.Add((r.ReadDouble(), r.ReadDouble(), ReadPts(r)));
        s.StepHull = ReadPts(r);
        s.IsComposite = r.ReadBoolean();
        return new StationGlbMesh(name, corners, s);
    }

    private static void WritePts(BinaryWriter w, (double X, double Y, double Z)[] pts)
    {
        w.Write(pts.Length);
        foreach (var p in pts) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
    }

    private static (double X, double Y, double Z)[] ReadPts(BinaryReader r)
    {
        var a = new (double X, double Y, double Z)[Count(r)];
        for (int i = 0; i < a.Length; i++) a[i] = (r.ReadDouble(), r.ReadDouble(), r.ReadDouble());
        return a;
    }
}
