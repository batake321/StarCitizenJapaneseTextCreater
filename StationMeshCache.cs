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
// (2026-11-02、利用者の指示「ない場合は取り込み済みなら足りないものだけ取る形にしてほしい」) 保存の中身は socpak ごとの読み取りの結果 (GLB の子ごとのメッシュ・8 隅・形の数。
// socpak の枠の値で、木の中の置き方に依らない) なので、使える条件を「Data.p4k の印・この版・同梱の部品の表のハッシュが同じ」にし、木のハッシュでは捨てない。
// 要る socpak のうち保存に無いものだけを書き出して足す (StationMapExports.BuildJsonAsync)。木のハッシュは頭に書くが、確かめには使わない。
// 横に <短い名前>.meshes.keys (UTF-8 の文字。1 行目「SCJTC-STATION-MESH-KEYS」、2 行目 版、3 行目 Data.p4k の印、4 行目 同梱の表のハッシュ、
// 続けて「C<TAB>socpak」= 保存にある socpak、「T<TAB>socpak」= 保存したときの木にあった socpak (書き出しが要るかを調べたもの)) を置く。
// まとめて取り込むの確認の窓で、gz を開かずに「保存から作り直せるか・何個書き出すか」を数えるのに使う (StationMapExports.TopUpPlan)
// (2026-11-02、親の指示) 版 2: 頭に「保存したときの同梱の表の確定の none (読まなかった部品) の名前の一覧」を足した。同梱の表が変わっても、
// 保存したときの none が今の none に全部入っていれば (保存したときに読まなかった部品が、今は読む部品になっていなければ) 使う
// (none を足しただけなら、保存の中にはその部品が読んだ形で入っていて、表での選び分け (FromRaw) で外れる)。
// 版 1 (前の形。none の一覧が無い) の保存は、同梱の表のハッシュが同じとき、または過去に配った表のハッシュ (StationMeshKindsKnown。その表の none を保存したときの none とみなす) のときに使う。横の鍵の一覧にも同じ一覧 (N 行) を書き、同じ決まりで見る
internal static class StationMeshCache
{
    // 版 3 (2026-11-02、親の決定 (A)): 形の数に段ごとの本当の奥行 (StationShape.LevelDepths) を足し、広い平らな面 (BigLevels) をそれで決めるようにした。
    // 版 1・2 の保存は使わない (読めない版として消し、次の取り込みで全部書き出す)
    public const int Version = 3;
    // 読める前の版 (none の一覧が無い形)
    private const int VersionNoNone = 1;
    private const string Magic = "SCJTC-STATION-MESHES";

    public static string PathOf(string jsonPath) => Path.ChangeExtension(jsonPath, null) + ".meshes.gz";

    // (2026-11-02、利用者の指示「足りないものが見つかったら、設定の "マップを纏めて取り込む" が無効でも有効にして、足りないものだけ前部拾うようにしてほしい」)
    // 横の鍵の一覧の版がこの版 (Version) と違う (前の版の保存) か。鍵の一覧が無い・読めないときは false (保存の有無は見ない)。
    // StationMapExports.IsFreshOnDisk が、前の版の保存の地図を取り込み済みに数えないのに使う
    public static bool OldVersionOnDisk(string meshCachePath)
    {
        try
        {
            var kp = KeysPathOf(meshCachePath);
            if (!File.Exists(kp)) return false;
            using var r = new StreamReader(kp, Encoding.UTF8);
            if (r.ReadLine() != KeysMagic) return false;
            return r.ReadLine() != Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    // 横の鍵の一覧 (<短い名前>.meshes.keys)。meshCachePath は PathOf の値
    public static string KeysPathOf(string meshCachePath) => meshCachePath.EndsWith(".meshes.gz", StringComparison.OrdinalIgnoreCase) ? meshCachePath[..^3] + ".keys" : meshCachePath + ".keys";
    private const string KeysMagic = "SCJTC-STATION-MESH-KEYS";

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

    // 同梱の部品の表の確定の none (読まない部品) の名前 (表が無ければ空)
    public static HashSet<string> BundledNoneNames()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, StationMeshKinds.BundledFile);
            return File.Exists(p) ? StationMeshKinds.ReadFile(p).Where(kv => kv.Value.Kind == StationMeshKind.None).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (IOException) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    // 保存の部品の表の条件が今と合うか: none の一覧があれば「保存したときの none ⊆ 今の none」、無ければ (版 1) 同梱の表のハッシュが同じ
    // 版 1 の保存で、ハッシュが今の表と違っても、過去に配った表のハッシュ (StationMeshKindsKnown) なら、その表の none を保存したときの none とみなす
    private static bool KindsUsable(HashSet<string>? savedNone, string savedHash)
    {
        if (savedNone != null) return savedNone.IsSubsetOf(BundledNoneNames());
        if (savedHash == BundledKindsHash()) return true;
        return StationMeshKindsKnown.NoneByHash.TryGetValue(savedHash, out var known) && BundledNoneNames().IsSupersetOf(known);
    }

    // 書く (一時ファイルに書いてから置き換える)。横の鍵の一覧も書く (調べたもの (T) は tree の socpak。無ければ保存にある socpak)。
    // none の一覧は今の同梱の表のもの (保存の中身は今の表で読んだ結果なので)
    public static void Save(string path, string stampKey, string treeHash, string kindsHash, StationMeshSet set, StationTree? tree = null)
    {
        var none = BundledNoneNames();
        Write(path, stampKey, treeHash, kindsHash, none, set.Raw, set.Missing.ToHashSet(StringComparer.Ordinal));
        WriteKeys(path, stampKey, kindsHash, none, set.Raw.Keys, tree?.Instances.Where(i => i.Found && i.Socpak.Length > 0).Select(i => i.Socpak) ?? set.Raw.Keys);
    }

    // none: 保存したときの確定の none の名前 (版 1 の保存なら null。N 行を書かず、読むときは同梱の表のハッシュで見る)
    private static void WriteKeys(string path, string stampKey, string kindsHash, IEnumerable<string>? none, IEnumerable<string> cached, IEnumerable<string> checkedSocpaks)
    {
        var kp = KeysPathOf(path);
        var sb = new StringBuilder();
        sb.Append(KeysMagic).Append('\n').Append(Version).Append('\n').Append(stampKey.Replace('\n', ' ')).Append('\n').Append(kindsHash).Append('\n');
        // (2026-11-02、検証の指摘「中 5」) 保存 (.gz) の大きさと更新日時。読むときに合わなければ (公開版のアプリが .gz だけ書き直したなど) この一覧は使わない
        sb.Append(GzLine(path)).Append('\n');
        // 保存したときの none の一覧 (「N<TAB>-」は一覧の無い版 1 の保存)
        if (none == null) sb.Append("N\t-\n");
        else foreach (var k in none.OrderBy(k => k, StringComparer.Ordinal)) sb.Append("N\t").Append(k).Append('\n');
        foreach (var k in cached.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)) sb.Append("C\t").Append(k).Append('\n');
        foreach (var k in checkedSocpaks.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)) sb.Append("T\t").Append(k).Append('\n');
        var tmp = kp + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, kp, true);
    }

    // 横の鍵の一覧を読む。無い・版・Data.p4k の印・同梱の表が違えば null (gz は開かない)
    public static (HashSet<string> Cached, HashSet<string> Checked)? ReadKeys(string path, string stampKey)
    {
        try
        {
            var kp = KeysPathOf(path);
            if (!File.Exists(kp) || !File.Exists(path)) return null;
            var lines = File.ReadAllLines(kp, Encoding.UTF8);
            if (lines.Length < 5 || lines[0] != KeysMagic || lines[1] != Version.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || lines[2] != stampKey.Replace('\n', ' ') || lines[4] != GzLine(path)) return null;
            var c = new HashSet<string>(StringComparer.Ordinal); var t = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string>? none = null; var noNone = false;
            foreach (var l in lines.Skip(4))
            {
                if (l.StartsWith("C\t", StringComparison.Ordinal)) c.Add(l[2..]);
                else if (l.StartsWith("T\t", StringComparison.Ordinal)) t.Add(l[2..]);
                else if (l == "N\t-") noNone = true;
                else if (l.StartsWith("N\t", StringComparison.Ordinal)) (none ??= new HashSet<string>(StringComparer.Ordinal)).Add(l[2..]);
            }
            // none の一覧の行が無い鍵の一覧 (この決まりの前に書いたもの) は使わない (.gz を読み直す)
            if (!noNone && none == null) return null;
            if (!KindsUsable(noNone ? null : none, lines[3])) return null;
            return (c, t);
        }
        catch (IOException) { return null; }
    }

    // 保存にある socpak (Cached) と、保存したときに書き出しが要るかを調べた socpak (Checked) (まとめて取り込むの確認の窓の見積もり。StationMapExports.TopUpPlan)。
    // 横の鍵の一覧があればそれ。無ければ (2026-11-02 より前の保存)、保存を全部読んで、保存にある socpak を両方にする。使えない保存 (Data.p4k・版・同梱の表が違う) は null
    public static (HashSet<string> Cached, HashSet<string> Checked)? KeysOf(string path, string stampKey)
    {
        if (ReadKeys(path, stampKey) is { } k) return k;
        if (!File.Exists(path) || Read(path, out _) is not { } l || l.StampKey != stampKey || !KindsUsable(l.NoneNames, l.KindsHash)) return null;
        var c = l.Raw.Keys.ToHashSet(StringComparer.Ordinal);
        // (2026-11-02、検証の指摘) 読んだら横の鍵の一覧を書いておき、次からは .gz を読まない (調べたもの (T) は保存にある socpak だけ)
        try { WriteKeys(path, stampKey, l.KindsHash, l.NoneNames, c, c); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (c, new HashSet<string>(c, StringComparer.Ordinal));
    }

    // 横の鍵の一覧に書く保存 (.gz) の印: 「G<TAB>大きさ<TAB>更新日時 (UTC の ticks)」。.gz が無ければ「G<TAB>-」
    private static string GzLine(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? $"G\t{f.Length}\t{f.LastWriteTimeUtc.Ticks}" : "G\t-";
    }

    // 横の鍵の一覧が無いか、木の socpak に一覧の「調べたもの」に入っていないものがあるか (書き直したほうがよいか)
    public static bool KeysOutdated(string path, StationTree tree)
    {
        try
        {
            var kp = KeysPathOf(path);
            if (!File.Exists(kp)) return true;
            var t = File.ReadAllLines(kp, Encoding.UTF8).Where(l => l.StartsWith("T\t", StringComparison.Ordinal)).Select(l => l[2..]).ToHashSet(StringComparer.Ordinal);
            return tree.Instances.Any(i => i.Found && i.Socpak.Length > 0 && !t.Contains(i.Socpak));
        }
        catch (IOException) { return true; }
    }

    // 保存と横の鍵の一覧を消す
    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var kp = KeysPathOf(path);
        if (File.Exists(kp)) File.Delete(kp);
    }

    // none が null なら版 1 の形 (none の一覧なし) で書く (版 1 の保存の印だけを書き直すとき。Restamp)
    private static void Write(string path, string stampKey, string treeHash, string kindsHash, IEnumerable<string>? none, IReadOnlyDictionary<string, List<List<StationGlbMesh>>> raw, HashSet<string> missing)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            using (var w = new BinaryWriter(gz, Encoding.UTF8))
            {
                w.Write(Magic); w.Write(none == null ? VersionNoNone : Version); w.Write(stampKey); w.Write(treeHash); w.Write(kindsHash);
                if (none != null)
                {
                    var list = none.OrderBy(k => k, StringComparer.Ordinal).ToList();
                    w.Write(list.Count);
                    foreach (var k in list) w.Write(k);
                }
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

    // NoneNames: 保存したときの確定の none (版 1 の保存は null)
    private sealed record Loaded(string StampKey, string TreeHash, string KindsHash, Dictionary<string, List<List<StationGlbMesh>>> Raw, HashSet<string> Missing, HashSet<string>? NoneNames = null);

    // 読む。無い・壊れている・版・Data.p4k・同梱の表が違うときは null と理由 (木のハッシュは見ない。要る socpak が足りるかは呼ぶ側で見る)。
    // Raw は socpak → GLB に出た子ごとのメッシュの並び (1 つの GLB に同じ socpak の子が 2 回出たときは 2 つ。StationMeshSet.Raw と同じ)
    public static (Dictionary<string, List<List<StationGlbMesh>>> Raw, HashSet<string> Missing)? TryLoad(string path, string stampKey, out string reason)
    {
        if (Read(path, out reason) is not { } l) return null;
        if (l.StampKey != stampKey) { reason = "Data.p4k が保存したときと違う"; return null; }
        if (!KindsUsable(l.NoneNames, l.KindsHash))
        {
            reason = l.NoneNames == null ? "同梱の部品の表が保存したときと違う (保存は前の版 1 の形で、表のハッシュが過去に配った表のどれとも違うか、その表の none が今は読む部品になっている)" : "保存したときに読まなかった部品 (同梱の表の none) が、今は読む部品になっている";
            return null;
        }
        return (l.Raw, l.Missing);
    }

    // 照合で Data.p4k の印だけを書き直したとき (StationMapExports.RewriteStamp): 保存の印が前の印 (oldKey) なら新しい印 (newKey) にする。書き直したら true
    public static bool Restamp(string path, string oldKey, string newKey)
    {
        if (Read(path, out _) is not { } l || l.StampKey != oldKey) return false;
        Write(path, newKey, l.TreeHash, l.KindsHash, l.NoneNames, l.Raw, l.Missing);
        // 横の鍵の一覧も印を書き直す (調べたもの (T) は前の一覧のまま。無ければ保存にある socpak だけ)
        var kp = KeysPathOf(path);
        var checkedOld = File.Exists(kp) ? File.ReadAllLines(kp, Encoding.UTF8).Where(x => x.StartsWith("T\t", StringComparison.Ordinal)).Select(x => x[2..]).ToList() : l.Raw.Keys.ToList();
        WriteKeys(path, newKey, l.KindsHash, l.NoneNames, l.Raw.Keys, checkedOld);
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
                HashSet<string>? noneNames = null;
                if (ver == Version)
                {
                    noneNames = new HashSet<string>(StringComparer.Ordinal);
                    var nn = Count(r);
                    for (int i = 0; i < nn; i++) noneNames.Add(r.ReadString());
                }
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
                return new Loaded(stampKey, treeHash, kindsHash, raw, missing, noneNames);
            }
        }
        catch (Exception ex) { reason = $"保存した読み取りの結果を読めない ({ex.GetType().Name}: {ex.Message})"; }
        // 読めない・形や版が違う: 使えないので消す (次の取り込みで作り直す)
        try { Delete(path); } catch { }
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
        w.Write(s.LevelDepths.Count);
        foreach (var dpt in s.LevelDepths) w.Write(dpt);
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
        var nd = Count(r);
        for (int i = 0; i < nd; i++) s.LevelDepths.Add(r.ReadDouble());
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
