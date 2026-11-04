using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D (新しい方式) の、地点 (中心・半径) ごとの取り出し結果 (region.json と g\<番号>.bin) をディスクに保存して使い回す置き場
// (2026-11-04、利用者の指示「先読み、キャッシュ化 HDD 容量設定が空いていればファイル保存など早く動かす仕組みを入れて」)。
//   保存するもの: StationView3D.Session.BuildRegionAsync が書く地点のフォルダそのもの。ビューアはこのフォルダをそのまま読むので、保存したものから出した結果は、
//     その場で作った結果と同じファイル (作った後に中身を変えない)。解析の糸 (view3d-worker.js) の結果 (描く形・当たり判定の升目) は、ここではなく糸の側 (WC。ブラウザの OPFS) が保存する。
//     ここは、その鍵 (地点の保存の鍵) と使ってよい大きさ (WorkerBudget) を決め、合計 (WorkerBytes) を上限に数える。
//   鍵 (保存したものを使ってよい条件): 場所 (site)・Data.p4k の印・区画の置き方の一覧 (view3d_layout.db) の版と作った時刻・
//     このプログラムの版 (Version。モジュールの MVID を含むので、ビルドし直すと別の鍵になる)・中心・半径。どれかが違えば使わない。
//   置き場所: 3D の置き場所 (Ship3DExports.Folder) の stations3d_cache\<鍵の SHA-256 の頭 32 桁>\ (中身は region.json・g\*.bin・cache.json)。
//     作っている途中は <鍵>.tmp-<GUID> に書き、cache.json を最後に書いてから名前を替える (途中で止まったものは使わない)。
//   容量: 上限を超えたら、最後に使った時刻が古いものから消す (使われている最中のものは消さない)。ディスクの空きが足りなければ保存しない。
internal sealed class StationView3DCache
{
    // 保存の形の版。保存する中身が変わる変更 (BuildGeo・ComputeInst・WriteSocpakFile・融合・床の補い・region.json の項目) を入れたときは、ここを上げる
    // (Version には、ほかにモジュールの MVID も入るので、ビルドし直したときは必ず別の鍵になる)
    public const string FormatVersion = "region-cache-5";   // 2: 区画のファイルの parts にエレベーターの扉の前の向きを足した (2026-11-05)。3: region.json に店のカウンターの箱 counters を足した (2026-11-05)。4: parts に UV・画像の番号、region.json に textures を足した (2026-11-05)。5: counters を店と同じ階の箱だけにした (2026-11-05)
    public const string FolderName = "stations3d_cache";
    private const string MetaName = "cache.json";
    // 保存する前に、保存先のドライブに残しておく空き (これを割るなら保存しない)
    public const long FreeReserveBytes = 3L << 30;

    public static readonly string Version = FormatVersion + "|" + StationView3D.Format + "|" + typeof(StationView3DCache).Module.ModuleVersionId.ToString("N");

    private readonly string _root;
    private readonly Func<long> _maxBytes;
    private readonly Func<long?> _freeBytes;
    private readonly object _lock = new();
    private readonly Dictionary<string, int> _inUse = new(StringComparer.Ordinal);
    private bool _swept;

    // root: 保存先のフォルダ。maxBytes: 上限 (バイト。0 以下なら保存しない)。freeBytes: 保存先のドライブの空き (調べられなければ null)
    public StationView3DCache(string root, Func<long> maxBytes, Func<long?> freeBytes)
    { _root = root; _maxBytes = maxBytes; _freeBytes = freeBytes; }

    // ===== アプリの設定との結びつき =====
    // 上限の初期値: 置き場所のドライブの残りの 1/3、ただし最大 30 GB (地点の保存 + 解析の結果の保存の合計。Seraphim の解析の結果は 1 地点で 5 GB ほど) (設定が未設定 (負) のとき。アプリの起動中は最初に求めた値を使う)
    public const double DefaultMaxGbCap = 30;
    private static long? s_defaultMax;
    public static long MaxBytesSetting
    {
        get
        {
            var gb = App.Config.Station3DCacheMaxGB;
            if (gb >= 0) return (long)(gb * (1L << 30));
            return s_defaultMax ??= DefaultMaxBytes(Ship3DExports.Folder);
        }
    }
    public static long DefaultMaxBytes(string folder)
    {
        var d = Ship3DExports.DriveOf(folder);
        var cap = (long)(DefaultMaxGbCap * (1L << 30));
        return d == null ? cap : Math.Max(0, Math.Min(cap, d.Value.Free / 3));
    }
    public static void NotifySettingsChanged() { s_defaultMax = null; }

    private static StationView3DCache? s_shared;
    private static readonly object s_sharedLock = new();
    // アプリで 1 つ (置き場所を変えたら作り直す。使われている最中の印は置き場所ごと)
    public static StationView3DCache Current
    {
        get
        {
            var root = Path.Combine(Ship3DExports.Folder, FolderName);
            lock (s_sharedLock)
            {
                if (s_shared == null || !string.Equals(s_shared._root, root, StringComparison.OrdinalIgnoreCase))
                    s_shared = new StationView3DCache(root, () => MaxBytesSetting, () => Ship3DExports.DriveOf(root)?.Free);
                return s_shared;
            }
        }
    }

    public string Root => _root;
    public bool Enabled => _maxBytes() > 0;

    // 鍵 → フォルダ名。double は R の形 (往復で同じ値) の文字にして鍵に入れる
    public static string KeyOf(string site, string p4kKey, string layoutId, double[] center, double r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var s = string.Join("|", Version, site, p4kKey, layoutId, string.Join(",", center.Select(v => v.ToString("R", inv))), r.ToString("R", inv));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();
    }

    public string DirOf(string key) => Path.Combine(_root, key);

    private sealed class Meta
    {
        public string Version { get; set; } = "";
        public string Site { get; set; } = "";
        public string P4k { get; set; } = "";
        public string Layout { get; set; } = "";
        public double[] Center { get; set; } = [];
        public double R { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
        public int Socpaks { get; set; }
        public int Missing { get; set; }
        public string BuiltUtc { get; set; } = "";
    }

    public sealed record Hit(string Dir, int Socpaks, int Missing);

    // 保存したものがあり、鍵が今と合い、中身がそろっていれば、使うフォルダを返す (使い終わるまで消されない。Release を呼ぶ)。無ければ null
    public Hit? TryGet(string key, string site, string p4kKey, string layoutId, double[] center, double r)
    {
        var dir = DirOf(key);
        try
        {
            var metaPath = Path.Combine(dir, MetaName);
            if (!File.Exists(metaPath)) return null;
            var m = JsonSerializer.Deserialize<Meta>(File.ReadAllBytes(metaPath));
            if (m == null || m.Version != Version || m.Site != site || m.P4k != p4kKey || m.Layout != layoutId || m.R != r || !m.Center.SequenceEqual(center)) return null;
            if (!File.Exists(Path.Combine(dir, "region.json")) || !Directory.Exists(Path.Combine(dir, "g"))) return null;
            // 中身がそろっているか (数とバイト数。一部だけ消えたり書きかけだったりするものは使わない)
            var (files, bytes) = Measure(dir, includeMeta: false);
            if (files != m.Files || bytes != m.Bytes) return null;
            lock (_lock) { _inUse[key] = _inUse.GetValueOrDefault(key) + 1; }
            try { File.SetLastWriteTimeUtc(metaPath, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return new Hit(dir, m.Socpaks, m.Missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // 保存する余地があるか (上限が 0 より大きく、空きが FreeReserveBytes 以上。estimate: これから足す大きさの見込み)
    public bool CanStore(long estimate = 0)
    {
        var max = _maxBytes();
        if (max <= 0) return false;
        if (estimate > max) return false;
        return _freeBytes() is not long free || free - estimate >= FreeReserveBytes;
    }

    // 解析の結果の保存 (ビューアの OPFS。view3d-worker.js の WC) の合計 (バイト)。ビューアが知らせてきた最後の値 (まだ知らせが無ければ 0)
    private static long s_workerBytes;
    public static long WorkerBytes { get => Interlocked.Read(ref s_workerBytes); set => Interlocked.Exchange(ref s_workerBytes, Math.Max(0, value)); }

    // 地点の保存の合計 (バイト)。cache.json のあるものだけ数える
    public long RegionBytes()
    {
        long sum = 0;
        foreach (var e in Entries()) sum += e.Bytes;
        return sum;
    }

    // 上限に数える今の保存の合計 (地点の保存 + 解析の結果の保存)
    public long UsedBytes() => RegionBytes() + WorkerBytes;

    // 解析の結果の保存に使ってよい大きさ (バイト) = 上限 − 地点の保存。保存しない設定 (上限 0) なら 0
    public long WorkerBudget()
    {
        var max = _maxBytes();
        return max <= 0 ? 0 : Math.Max(0, max - RegionBytes());
    }

    // 解析の結果を置く先のドライブの空き (バイト。調べられなければ null)
    public static long? WorkerFreeBytes() => Ship3DExports.DriveOf(Path.Combine(DetailPanel.WorkDir(), "webview2_ship3d"))?.Free;

    // 作る途中の置き場 (BuildRegionAsync の outDir にする)。終わったら Commit、失敗・取り消しなら Abort
    public string BeginStage(string key) => Path.Combine(_root, key + ".tmp-" + Guid.NewGuid().ToString("N"));

    // 作り終えたフォルダ (stage) を保存にする。保存できたら Hit (使い終わるまで消されない。Release を呼ぶ)。
    // 上限を超える・空きが足りない・同じ鍵のものが先にできていたときは保存せず null (stage はそのまま残るので、呼んだ側が使い終えてから消す)。
    // 同じ鍵のものが先にできていたときは、そちらを Hit で返し、stage は消す
    public Hit? Commit(string stage, string key, string site, string p4kKey, string layoutId, double[] center, double r, int socpaks, int missing)
    {
        try
        {
            var (files, bytes) = Measure(stage, includeMeta: false);
            if (!CanStore(bytes)) return null;
            var meta = new Meta
            {
                Version = Version, Site = site, P4k = p4kKey, Layout = layoutId, Center = center, R = r, Files = files, Bytes = bytes,
                Socpaks = socpaks, Missing = missing, BuiltUtc = DateTime.UtcNow.ToString("o"),
            };
            var final = DirOf(key);
            lock (_lock)
            {
                if (Directory.Exists(final))
                {
                    // 先にできたものが使えるなら、それを使う。使えない (古い・壊れている) なら消して置き換える
                    if (TryGet(key, site, p4kKey, layoutId, center, r) is { } existing) { TryDelete(stage); return existing; }
                    if (_inUse.GetValueOrDefault(key) > 0) return null;
                    TryDeleteSoon(final);
                }
                File.WriteAllBytes(Path.Combine(stage, MetaName), JsonSerializer.SerializeToUtf8Bytes(meta));
                Directory.Move(stage, final);
                _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
            }
            Evict(key);
            return new Hit(final, socpaks, missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // 使い終わった (ビューアが読み終えた・窓を閉じた)
    public void Release(string key)
    {
        lock (_lock)
        {
            if (!_inUse.TryGetValue(key, out var n)) return;
            if (n <= 1) _inUse.Remove(key); else _inUse[key] = n - 1;
        }
    }

    // 古い・使えないものと、途中で止まった書きかけを消す。最初の 1 回だけ (窓を開いたとき)。
    // 鍵の合わないもの (Data.p4k・区画の置き方の一覧・プログラムの版が今と違う) は、使えないので消す
    public void SweepStale(string p4kKey, string layoutId)
    {
        lock (_lock) { if (_swept) return; _swept = true; }
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var d in Directory.GetDirectories(_root))
            {
                var name = Path.GetFileName(d);
                if (name.Contains(".tmp-", StringComparison.Ordinal) || name.Contains(".del-", StringComparison.Ordinal))
                {
                    // 書きかけ・消しかけ: 1 時間以上動いていないものだけ (別のウィンドウが作っている最中のものを消さない)
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(1)) TryDelete(d);
                    continue;
                }
                var mp = Path.Combine(d, MetaName);
                Meta? m = null;
                try { if (File.Exists(mp)) m = JsonSerializer.Deserialize<Meta>(File.ReadAllBytes(mp)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
                if (m == null || m.Version != Version || m.P4k != p4kKey || m.Layout != layoutId)
                {
                    lock (_lock) { if (_inUse.GetValueOrDefault(name) > 0) continue; }
                    TryDeleteSoon(d);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // 上限を超えていたら、最後に使った時刻が古いものから消す (keep と、使われている最中のものは消さない)
    public void Evict(string? keep = null)
    {
        try
        {
            var max = _maxBytes();
            var list = Entries().OrderBy(e => e.LastUsedUtc).ToList();
            var total = list.Sum(e => e.Bytes) + WorkerBytes;
            foreach (var e in list)
            {
                if (total <= Math.Max(0, max)) break;
                if (e.Name == keep) continue;
                lock (_lock)
                {
                    if (_inUse.GetValueOrDefault(e.Name) > 0) continue;
                    TryDeleteSoon(e.Dir);
                }
                total -= e.Bytes;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Entry(string Name, string Dir, long Bytes, DateTime LastUsedUtc);

    private IEnumerable<Entry> Entries()
    {
        if (!Directory.Exists(_root)) yield break;
        foreach (var d in Directory.GetDirectories(_root))
        {
            var name = Path.GetFileName(d);
            if (name.Contains(".tmp-", StringComparison.Ordinal) || name.Contains(".del-", StringComparison.Ordinal)) continue;
            var mp = Path.Combine(d, MetaName);
            if (!File.Exists(mp)) continue;
            long bytes;
            try { bytes = Measure(d, includeMeta: true).Bytes; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            yield return new Entry(name, d, bytes, File.GetLastWriteTimeUtc(mp));
        }
    }

    private static (int Files, long Bytes) Measure(string dir, bool includeMeta)
    {
        int n = 0; long b = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (!includeMeta && f.Name == MetaName && Path.GetRelativePath(dir, f.FullName) == MetaName) continue;
            n++; b += f.Length;
        }
        return (n, b);
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // 名前を .del- に替えてから消す (消している途中のものが、使えるものに見えないように)
    private static void TryDeleteSoon(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var tomb = dir + ".del-" + Guid.NewGuid().ToString("N");
            Directory.Move(dir, tomb);
            TryDelete(tomb);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
