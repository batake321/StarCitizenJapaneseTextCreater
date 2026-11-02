using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D 表示 (2026-11-02、利用者の指示「詳細に、3D で船のようにStation を立体で見たい。そこに引き出し線とキャプションで着陸パッド(番号付き)、
// 外部貨物エレベーター、ハンガーの場所を示してほしい。回転、拡大縮小もしたい。内部のマップボタンを付けて、マップに自動切換え表示の機能も欲しい」) の書き出しとキャッシュ。
// アプリ全体で 1 つ (Station3DExports.Instance)。
//   船の 3D モデル (Ship3DExports) とステーションの地図 (StationMapExports) の仕組みに乗せる:
//     置き場所 (Ship3DExports.Folder の下の stations3d\<短い名前>\)・最大容量 (Ship3DExports.MaxMb。船・地図と合わせて数え、Ship3DExports.EnforceLimit が古い順に消す。
//     キーは "station3d:<短い名前>")・待ち行列・同時数・CPU 使用率 (Ship3DExports.WaitTurnAsync・TryAcquireExtraRun)・進み具合の欄 (Ship3DExports.Jobs。Ship3DJob の Kind は Station3D)・
//     StarBreakerRunner・Ship3DP4kIndex・Ship3DExports.CurrentStamp。照合は地図と同じ考え (元ファイルの大きさと CRC、木の形のハッシュ StationMapExports.TreeHash)。
//   書き出す範囲 (親の決定): ルートとその直下の Child (木 StationTree の深さ 0 と 1 のインスタンスのうち socpak のあるもの)。名前で決め打ちしない。
//     starbreaker の socpak export は Child を書き出さないので、socpak ごとに 1 つの GLB に書き出し、置き方 (インスタンスの ToStation) は scene.json に書いてビューアが置く。
//     同じ socpak を何か所にも置くとき (武器の台・貨物エレベーターなど) は GLB は 1 つ。
//     書き出しの引数: --materials none --no-lights --lod 3 (調査担当 C の下調べ: 1 か所 2〜3 秒・5〜22 MB。LOD 0 だと大きく重い)
//   印 (着陸パッド・外の貨物エレベーター・ハンガー): 木の全体 (深さの制限なし) の外の設備 (StationTree.Exteriors。地図の JSON の exterior と同じもの) を scene.json に書く。
//     見出しの言葉は表示のときに訳で引く (Station3DWeb)。
//   scene.json の形 (Format): app・format・record・p4k・treeHash・exportedUtc・exportSeconds・lod・files (GLB と socpak)・parts (置き方)・markers・sources (照合用の元ファイル)。
//     sources は最後に置く (頭を読むのは sources の前まで)。
internal sealed class Station3DExports : INotifyPropertyChanged
{
    private static Station3DExports? _instance;
    // UI スレッドで最初に触ったときに作る
    public static Station3DExports Instance => _instance ??= new Station3DExports();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ボタンの有効・無効を決め直すきっかけ (書き出しが加わった / 終わった・照合を始めた / 終えた・消した)。UI スレッドで呼ぶ
    public event Action? StateChanged;
    private void RaiseStateChanged() => StateChanged?.Invoke();
    // 書き出しが終わった場所 (短い名前)
    public event Action<string>? Exported;

    // ===== 定数 =====

    public const string Mark = "StarCitizenJapaneseTextCreater.station3d";
    // scene.json の形の版。これより古いものは「書き出しが要る」(StateOf が None) として扱う
    public const int Format = 1;
    public const string KeyPrefix = "station3d:";
    public const string SceneFile = "scene.json";
    // 書き出す LOD (調査担当 C の下調べで決めたもの)
    public const int Lod = 3;
    // 書き出す深さ (ルート 0 とその直下の Child 1。親の決定)
    public const int MaxPartDepth = 1;
    // 始める前に要る置き場所のドライブの空き (地図の取り込みと同じ値。1 回の GLB は 22 MB 程度だが、同時に動かす分と書き終えるまでの前の版の分に余裕を見る)
    public const long MinFreeBytes = StationMapExports.MinFreeBytes;
    // 予想時間の係数 (2026-11-02、この PC、LIVE、scratchpad\agentC の harness で 5 か所を書き出した実測で決めた。説明は報告と EstimateSeconds)
    public const double BaseSeconds = 3.0;
    public const double SecondsPerRun = 2.1;
    public const double ParallelSlowdown = StationMapExports.ParallelSlowdown;
    // 予想の大きさ: socpak の .soc にある CGF の p4k の中の大きさの合計 (StationMapExports.SocGeometryBytes) × この値 (実測。報告)
    public const double GlbPerGeometryByte = 0.14;

    private Station3DExports()
    {
        CleanWorkFolders();
    }

    // ===== 置き場所 =====

    public static string Folder => Path.Combine(Ship3DExports.Folder, "stations3d");
    public static string ShortName(string record) => StationMapExports.ShortName(record);
    public static string PlaceFolder(string shortName) => Path.Combine(Folder, shortName);
    public static string ScenePath(string shortName) => Path.Combine(PlaceFolder(shortName), SceneFile);
    public static string KeyOf(string shortName) => KeyPrefix + shortName;
    public static bool IsKey(string key) => key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);
    private static string ShortOfKey(string key) => IsKey(key) ? key[KeyPrefix.Length..] : key;
    private static void Log(string msg) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの 3D] {msg}");

    // 作業用のフォルダ (Ship3DExports.WorkFolder) の下に作る、1 回の書き出しの置き場所の名前の形 (station3d_<短い名前>_<GUID 32 桁>)
    private static readonly Regex WorkDirRegex = new(@"^station3d_.+_[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 前の起動で途中で終わった書き出しの置き場所を消す (この形の名前のフォルダだけ)
    private static void CleanWorkFolders()
    {
        try
        {
            if (!Directory.Exists(Ship3DExports.WorkFolder)) return;
            foreach (var d in Directory.GetDirectories(Ship3DExports.WorkFolder))
            {
                if (!WorkDirRegex.IsMatch(Path.GetFileName(d))) continue;
                try { Directory.Delete(d, recursive: true); } catch { }
            }
        }
        catch { }
    }

    // scene.json の頭 (印・record・場所の名前 (無い形では "")・Data.p4k の印・木の形のハッシュ・形の版)
    internal sealed record Header(string Record, string P4kPath, long P4kSize, long P4kWriteUtcTicks, string TreeHash, int Format, string Name = "")
    {
        public bool Outdated => Format < Station3DExports.Format;
    }

    internal static Header? ReadOurHeader(string shortName) => ReadHeaderOf(ScenePath(shortName), shortName);

    // 読んだ頭とフォルダの大きさの覚え (scene.json のパス → scene.json の大きさ・更新日時と、そのときの頭・フォルダの中の全部の大きさ (まだ数えていなければ -1))。
    // StateOf は［詳細］のボタンの決め直し (書き出し中はほぼ毎秒) から呼ばれるので、scene.json (数百 KB) を毎回読まない。
    // scene.json の大きさか更新日時が変わったら読み直す。印を書き換える (RewriteStamp。更新日時は元のまま)・消す・書き出すときは覚えを捨てる (Forget)
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Len, long Ticks, Header? Head, long Bytes)> HeadCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static void Forget(string scenePath) => HeadCache.TryRemove(scenePath, out _);

    // 頭を読む (sources の前まで)。読めて、印があり、record の短い名前がフォルダの名前と同じときだけ返す
    private static Header? ReadHeaderOf(string path, string shortName)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) { Forget(path); return null; }
            if (HeadCache.TryGetValue(path, out var c) && c.Len == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) return c.Head;
            var head = ParseHeader(File.ReadAllBytes(path), shortName);
            HeadCache[path] = (fi.Length, fi.LastWriteTimeUtc.Ticks, head, -1);
            return head;
        }
        catch { return null; }
    }

    private static Header? ParseHeader(byte[] bytes, string shortName)
    {
        try
        {
            var r = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = false });
            string app = "", record = "", p4kPath = "", hash = "", placeName = "";
            long size = 0, ticks = 0;
            int format = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == 0) break;
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 1) continue;
                var name = r.GetString();
                if (name == "sources") break;
                if (!r.Read()) return null;
                switch (name)
                {
                    case "app": app = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "record": record = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "name": placeName = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "treeHash": hash = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "format": format = r.TokenType == JsonTokenType.Number ? r.GetInt32() : 0; break;
                    case "p4k":
                        if (r.TokenType != JsonTokenType.StartObject) return null;
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) continue;
                            var k = r.GetString();
                            r.Read();
                            if (k == "path" && r.TokenType == JsonTokenType.String) p4kPath = r.GetString() ?? "";
                            else if (k == "size" && r.TokenType == JsonTokenType.Number) size = r.GetInt64();
                            else if (k == "writeUtcTicks" && r.TokenType == JsonTokenType.Number) ticks = r.GetInt64();
                        }
                        break;
                    default:
                        if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
                        break;
                }
            }
            if (app != Mark || record.Length == 0) return null;
            if (!string.Equals(ShortName(record), shortName, StringComparison.OrdinalIgnoreCase)) return null;
            return new Header(record, p4kPath, size, ticks, hash, format, placeName);
        }
        catch { return null; }
    }

    // 画面・ログに出す場所の名前 (scene.json の name。無ければ短い名前。内部のキー "station3d:…" は出さない)
    internal static string DisplayName(string shortName) => ReadOurHeader(shortName) is { Name.Length: > 0 } h ? h.Name : shortName;
    internal static string DisplayNameOfKey(string key) => DisplayName(ShortOfKey(key));

    // キャッシュの 3D (置き場所の stations3d\ の直下のフォルダのうち、scene.json の頭に印があるもの)。Bytes はフォルダの中の全部の大きさ
    // (scene.json が同じ間は覚えたものを使う。フォルダを数えるのは書き出し・取り直しの後の初めの 1 回だけ)
    public static List<(string Key, FileInfo Scene, long Bytes)> CachedEntries()
    {
        var res = new List<(string, FileInfo, long)>();
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return res;
            foreach (var d in dir.GetDirectories())
            {
                var scene = new FileInfo(Path.Combine(d.FullName, SceneFile));
                if (ReadHeaderOf(scene.FullName, d.Name) == null) continue;
                long bytes = -1;
                if (HeadCache.TryGetValue(scene.FullName, out var c) && c.Bytes >= 0 && c.Len == scene.Length && c.Ticks == scene.LastWriteTimeUtc.Ticks) bytes = c.Bytes;
                if (bytes < 0)
                {
                    try { bytes = d.GetFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { bytes = 0; }
                    if (HeadCache.TryGetValue(scene.FullName, out var c2) && c2.Len == scene.Length && c2.Ticks == scene.LastWriteTimeUtc.Ticks)
                        HeadCache[scene.FullName] = c2 with { Bytes = bytes };
                }
                res.Add((KeyOf(d.Name), scene, bytes));
            }
        }
        catch { }
        return res;
    }

    public static long CacheBytes() => CachedEntries().Sum(e => e.Bytes);

    // 置き場所に同じ名前のフォルダがあり、それがこのアプリのもの (scene.json の頭に印) でなければ true (上書きしない)
    private static bool HasForeignFolder(string shortName) => Directory.Exists(PlaceFolder(shortName)) && ReadOurHeader(shortName) == null
        && Directory.EnumerateFileSystemEntries(PlaceFolder(shortName)).Any();

    // ===== 状態 =====

    private static bool SameStamp(Header h, Ship3DExports.P4kStamp s) =>
        string.Equals(h.P4kPath, s.Path, StringComparison.OrdinalIgnoreCase) && h.P4kSize == s.Size && h.P4kWriteUtcTicks == s.WriteUtcTicks;

    public Ship3DCacheState StateOf(string record)
    {
        var shortName = ShortName(record);
        if (_verifying.ContainsKey(shortName)) return Ship3DCacheState.Verifying;
        if (_pendingDelete.Contains(shortName)) return Ship3DCacheState.None;
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) return Ship3DCacheState.NoGame;
        var h = ReadOurHeader(shortName);
        if (h == null || h.Outdated) return Ship3DCacheState.None;
        return SameStamp(h, stamp) ? Ship3DCacheState.Fresh : Ship3DCacheState.NeedsVerify;
    }

    public bool IsExporting(string record) => _byShort.ContainsKey(ShortName(record));

    // 表示に使える scene.json のパス。最新ならそのまま、Data.p4k が変わっていれば照合してから。使えなければ null (書き出しはしない)
    public async Task<string?> GetCachedPathAsync(string record)
    {
        var state = StateOf(record);
        if (state == Ship3DCacheState.Fresh) return ScenePath(ShortName(record));
        if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying)
            return await VerifyAsync(record) ? ScenePath(ShortName(record)) : null;
        return null;
    }

    // ===== 表示中 (消さないもの) =====

    private readonly Dictionary<string, int> _views = new(StringComparer.OrdinalIgnoreCase);
    public void AcquireView(string shortName) => _views[shortName] = _views.TryGetValue(shortName, out var n) ? n + 1 : 1;
    public void ReleaseView(string shortName)
    {
        if (!_views.TryGetValue(shortName, out var n)) return;
        if (n <= 1) _views.Remove(shortName); else _views[shortName] = n - 1;
        DeletePendingIfIdle(shortName);
    }

    // 容量の上限で消さないもの (表示中・照合中・書き出し中) のキー
    internal IEnumerable<string> ProtectedKeys() => _views.Keys.Concat(_verifying.Keys).Concat(_byShort.Keys).Select(KeyOf).ToList();

    // ===== 知らせ =====

    private string _notice = "";
    public string Notice { get => _notice; private set { _notice = value; Changed(nameof(Notice)); } }

    private static void ShowError(Window? owner, string msg)
    {
        Log(msg);
        if (owner != null && owner.IsLoaded) MessageBox.Show(owner, msg, "ステーションの 3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(msg, "ステーションの 3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ===== 照合 =====

    private readonly Dictionary<string, Task<bool>> _verifying = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDelete = new(StringComparer.OrdinalIgnoreCase);

    // Data.p4k が変わった場所の元データを照合する。同じなら true (scene.json の p4k の印を新しいものにする)。
    // 変わっていた・照合できなかったら false で、その場で消す (表示中・書き出し中なら後で消す)
    public Task<bool> VerifyAsync(string record)
    {
        var shortName = ShortName(record);
        if (_verifying.TryGetValue(shortName, out var running)) return running;
        if (StateOf(record) != Ship3DCacheState.NeedsVerify) return Task.FromResult(StateOf(record) == Ship3DCacheState.Fresh);
        var task = VerifyCoreAsync(shortName);
        if (!task.IsCompleted)
        {
            _verifying[shortName] = task;
            RaiseStateChanged();
        }
        return task;
    }

    private async Task<bool> VerifyCoreAsync(string shortName)
    {
        string? reason = null;
        try
        {
            await Task.Yield();   // 先に _verifying に入れてから始める
            var stamp = Ship3DExports.CurrentStamp() ?? throw new InvalidOperationException("ゲームの Data.p4k が見つかりません");
            var head = ReadOurHeader(shortName) ?? throw new InvalidOperationException("scene.json が読めません");
            var sw = Stopwatch.StartNew();
            var dbPath = DetailPanel.DbPath();
            var (r, count) = await Task.Run(async () => await VerifyFilesAsync(ScenePath(shortName), head, stamp, dbPath, CancellationToken.None));
            reason = r;
            if (reason == null)
            {
                await Task.Run(() => RewriteStamp(ScenePath(shortName), stamp));
                Log($"「{shortName}」の元データを照合しました: 変わっていないので、そのまま使います ({count} ファイル、{sw.Elapsed.TotalSeconds:F1} 秒)");
                return true;
            }
        }
        catch (Exception ex) { reason = $"照合できませんでした: {ex.Message}"; }
        finally
        {
            _verifying.Remove(shortName);
            RaiseStateChanged();
        }
        // (2026-11-02、利用者の決定「a」) 変わっていた場所は消さずに、裏で自動で取り直す (QueueRefresh。書き出し中の場所は今までどおり後で消す)
        QueueRefresh(shortName, reason!);
        return false;
    }

    // 照合の本体 (UI に触れない): 元ファイル (木の全 socpak と、書き出した socpak の .soc にある CGF) の大きさと CRC、木の形のハッシュ
    internal static async Task<(string? Reason, int Count)> VerifyFilesAsync(string scenePath, Header head, Ship3DExports.P4kStamp stamp, string dbPath, CancellationToken ct)
    {
        if (head.TreeHash.Length == 0) return ("照合の記録 (木の形のハッシュ) がありません", 0);
        var sources = ReadSources(scenePath);
        if (sources.Count == 0) return ("照合の記録 (元ファイルの一覧) がありません", 0);
        var index = await Ship3DP4kIndex.GetAsync(stamp);
        foreach (var src in sources)
        {
            var now = index.TryGetValue(Ship3DEstimator.Hash(src.Path), out var v) ? (v.Size, v.Crc) : (-1L, 0u);
            if (now.Item1 != src.Size || now.Item2 != src.Crc) return ($"元データ {src.Path} が変わっていました", sources.Count);
        }
        var places = StationMapExports.LoadPlaces(dbPath).Select(p => p.Place).ToList();
        using var sidx = StationIndex.Build(stamp.Path, ct);
        var search = sidx.FindRoots(places, ct: ct);
        if (!search.Roots.TryGetValue(head.Record, out var root)) return ("ゲームデータの中にこのステーションが見つかりませんでした", sources.Count);
        var tree = StationTree.Load(sidx, root, contents: false, ct: ct);
        if (StationMapExports.TreeHash(tree) != head.TreeHash) return ("ステーションの構成 (木の形) が変わっていました", sources.Count);
        return (null, sources.Count);
    }

    private static List<Ship3DExports.SourceFile> ReadSources(string scenePath)
    {
        var res = new List<Ship3DExports.SourceFile>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(scenePath));
        if (!doc.RootElement.TryGetProperty("sources", out var arr) || arr.ValueKind != JsonValueKind.Array) return res;
        foreach (var s in arr.EnumerateArray())
        {
            if (!s.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) return new();
            if (!s.TryGetProperty("size", out var z) || z.ValueKind != JsonValueKind.Number) return new();
            if (!s.TryGetProperty("crc", out var c) || c.ValueKind != JsonValueKind.Number) return new();
            res.Add(new Ship3DExports.SourceFile { Path = p.GetString() ?? "", Size = z.GetInt64(), Crc = c.GetUInt32() });
        }
        return res;
    }

    // scene.json の p4k の印を新しいものに書き換える (作業用のフォルダに書いてから名前を変える。最大容量の古い順が変わらないよう、更新日時は元のまま)
    private static void RewriteStamp(string scenePath, Ship3DExports.P4kStamp stamp)
    {
        var written = File.GetLastWriteTimeUtc(scenePath);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(scenePath))!.AsObject();
        node["p4k"] = new System.Text.Json.Nodes.JsonObject { ["path"] = stamp.Path, ["size"] = stamp.Size, ["writeUtcTicks"] = stamp.WriteUtcTicks };
        Directory.CreateDirectory(Ship3DExports.WorkFolder);
        var tmp = Ship3DExports.NewWorkFile("station3d_" + Path.GetFileName(Path.GetDirectoryName(scenePath)), ".json");
        try
        {
            File.WriteAllText(tmp, node.ToJsonString(JsonOut), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(tmp, written);
            File.Move(tmp, scenePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            Forget(scenePath);   // 更新日時を元のままにしたので、覚えた頭 (前の印) を捨てる
        }
    }

    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private void DeleteChangedCache(string shortName, string reason)
    {
        if (_views.ContainsKey(shortName) || _byShort.ContainsKey(shortName))
        {
            _pendingDelete.Add(shortName);
            Notice = $"「{shortName}」はパッチで元データが変わったので、表示・書き出しが終わったら 3D を消します ({reason})";
            Log(Notice);
            RaiseStateChanged();
            return;
        }
        DeleteOurCache(shortName);
        Notice = $"「{shortName}」はパッチで元データが変わったので、3D を消しました ({reason})。［読み込み］で書き出し直せます";
        Log(Notice);
        RaiseStateChanged();
    }

    // scene.json の頭に印のある場所のフォルダを消す。印が無ければ何もしない (false)
    private static bool DeleteOurCache(string shortName)
    {
        if (ReadOurHeader(shortName) == null) return false;
        Forget(ScenePath(shortName));
        try { Directory.Delete(PlaceFolder(shortName), recursive: true); return true; }
        catch (Exception ex) { Log($"stations3d\\{shortName} を消せませんでした: {ex.Message}"); return false; }
    }

    private void DeletePendingIfIdle(string shortName)
    {
        if (!_pendingDelete.Contains(shortName) || _views.ContainsKey(shortName) || _byShort.ContainsKey(shortName)) return;
        _pendingDelete.Remove(shortName);
        DeleteOurCache(shortName);
        Notice = $"「{shortName}」はパッチで元データが変わったので、3D を消しました。［読み込み］で書き出し直せます";
        Log(Notice);
        RaiseStateChanged();
    }

    // 最大容量を超えたので消す (Ship3DExports.EnforceLimit から。キーは "station3d:<短い名前>")。消せたら true
    internal bool DeleteForLimit(string key)
    {
        var shortName = ShortOfKey(key);
        if (_views.ContainsKey(shortName) || _byShort.ContainsKey(shortName) || _verifying.ContainsKey(shortName)) return false;
        if (!DeleteOurCache(shortName)) return false;
        RaiseStateChanged();
        return true;
    }

    // ===== 自動の取り直し =====
    // (2026-11-02、利用者の決定「a だが b も」) 船の 3D (Ship3DExports の「自動の取り直し」) と同じ: 照合 (VerifyAsync) で「変わっていた」になった場所は、消さずに裏で自動で取り直す。
    //   取り直すまで: 古いものはフォルダとして残すが表示には使わない (_pendingDelete。StateOf は「なし」。書き出しの仕事があるので［読み込み］は押せず、状態は仕事の文)。
    //     今開いている表示はそのまま。取り直せたら置き換える。失敗したら今までどおり消す。取り消し・空きが足りずやめたときは残す (次の照合でまた並ぶ)。
    //   控えめにする: 1 か所ずつ、starbreaker も 1 本ずつ (ExportAsync の allowExtra を使わない)。待ち行列 (Ship3DExports.WaitTurnAsync) で CPU 使用率・同時数の上限を待つ。
    //   いつ照合するか: 表示しようとしたとき (今まで) のほかに、起動の後と Data.p4k の印が変わったとき (Ship3DExports.Tick が StartAutoRefresh を呼ぶ) に、印の違う場所を全部 1 か所ずつ。
    //   古いものから消す (EnforceLimit) は、並べたものが全部終わったときに 1 回だけ。空きは 1 か所ごとに MinFreeBytes を見る
    private readonly List<Ship3DJob> _refreshWaiting = new();
    private readonly Dictionary<Ship3DJob, TaskCompletionSource<string?>> _refreshTcs = new();
    private readonly Dictionary<string, string> _refreshReason = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _refreshCts = new();
    private bool _refreshRunning, _scanRunning, _scanAgain;
    private int _refreshQueued, _refreshDone, _refreshFailed, _refreshCancelled;
    private readonly List<string> _refreshNames = new();
    // 今書き出している取り直し (無ければ null)。船の 3D の取り直し (Ship3DExports.RefreshCurrent) と合わせて 1 本ずつにする
    internal Ship3DJob? RefreshCurrent { get; private set; }

    private void QueueRefresh(string shortName, string reason)
    {
        var head = ReadOurHeader(shortName);
        if (head == null || _byShort.ContainsKey(shortName)) { DeleteChangedCache(shortName, reason); return; }
        _pendingDelete.Add(shortName);
        var job = new Ship3DJob(head.Record, KeyOf(shortName), head.Name.Length > 0 ? head.Name : shortName, _refreshCts)
            { Kind = Ship3DJobKind.Station3D, EstimatedSeconds = BaseSeconds + SecondsPerRun * Math.Max(1, ReadFileCount(shortName)), LogTag = "自動の取り直し" };
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Task = tcs.Task;
        _refreshTcs[job] = tcs;
        _refreshReason[shortName] = reason;
        AddJob(job, shortName);
        job.SetPhase(Ship3DJobPhase.Queued, "自動の取り直しの順番待ち");
        _refreshWaiting.Add(job);
        _refreshQueued++;
        _refreshNames.Add(shortName);
        Ship3DExports.LogJob(job, $"自動の取り直しに並べました ({reason})。待ち {_refreshWaiting.Count} か所");
        if (!_refreshRunning) _ = ProcessRefreshAsync();
    }

    // scene.json の files の数 (予想時間に使う。読めなければ 0)
    private static int ReadFileCount(string shortName)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(ScenePath(shortName)));
            return doc.RootElement.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array ? f.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    // 自動の取り直しの順番待ち (まだ計画・書き出しを始めていない) の場所か。順番待ちの間は［読み込み］を押せる (押すと PromoteRefresh でいちばん先にする)
    public bool IsRefreshWaiting(string record) => _byShort.TryGetValue(ShortName(record), out var j) && _refreshWaiting.Contains(j);

    private void PromoteRefresh(Ship3DJob job)
    {
        var i = _refreshWaiting.IndexOf(job);
        if (i <= 0) return;
        _refreshWaiting.RemoveAt(i);
        _refreshWaiting.Insert(0, job);
        Ship3DExports.LogJob(job, "［読み込み］が押されたので、自動の取り直しの順番をいちばん先にしました");
    }

    private async Task ProcessRefreshAsync()
    {
        _refreshRunning = true;
        var sw = Stopwatch.StartNew();
        string? lastDone = null;
        try
        {
            while (_refreshWaiting.Count > 0)
            {
                var job = _refreshWaiting[0];
                _refreshWaiting.RemoveAt(0);
                if (await RunRefreshJobAsync(job)) lastDone = job.Short;
            }
        }
        finally
        {
            _refreshRunning = false;
            Log($"自動の取り直し: {_refreshQueued} か所 (取り直した {_refreshDone}、失敗して消した {_refreshFailed}、やめた (古いものを残した) {_refreshCancelled}。" +
                $"{sw.Elapsed.TotalSeconds:F1} 秒): {string.Join(", ", _refreshNames)}");
            _refreshQueued = _refreshDone = _refreshFailed = _refreshCancelled = 0;
            _refreshNames.Clear();
            if (lastDone != null) Ship3DExports.Instance.EnforceLimit(justExported: lastDone);
        }
    }

    // 取り直し 1 か所: 計画 (確認の窓は出さない) → 順番を待つ → 書き出す (starbreaker は 1 本ずつ)。取り直せたら true
    private async Task<bool> RunRefreshJobAsync(Ship3DJob job)
    {
        var tcs = _refreshTcs[job];
        _refreshTcs.Remove(job);
        var shortName = ShortOfKey(job.Short);
        var keepOld = false;
        StationIndex? index = null;
        try
        {
            job.Cts.Token.ThrowIfCancellationRequested();
            if (FreeSpaceProblem() is { } free)
            {
                Ship3DExports.LogJob(job, $"{free}。自動の取り直しをやめます (古いものは残します)");
                foreach (var j in _refreshWaiting.ToList()) j.Cancel();
                throw new OperationCanceledException(free);
            }
            var stamp = Ship3DExports.CurrentStamp() ?? throw new OperationCanceledException("ゲームの Data.p4k が見つかりません");
            var ct = job.Cts.Token;
            job.LogStage = "StarBreaker の用意";
            var extractor = new GameDataExtractor(DetailPanel.WorkDir());
            await extractor.EnsureStarBreakerAsync();
            job.SetPhase(Ship3DJobPhase.Estimating, "自動の取り直し: 見積もり中");
            var dbPath = DetailPanel.DbPath();
            var record = job.Record;
            var plan = await Task.Run(async () =>
            {
                job.LogStage = "計画: 星図の場所の表・索引・ルート探し";
                var places = StationMapExports.LoadPlaces(dbPath).Select(p => p.Place).ToList();
                index = StationIndex.Build(stamp.Path, ct);
                var search = index.FindRoots(places, ct: ct);
                if (!search.Roots.TryGetValue(record, out var root))
                    throw new InvalidOperationException("ゲームデータの中にこの場所が見つかりませんでした");
                var p4k = await Ship3DP4kIndex.GetAsync(stamp);
                job.LogStage = "計画: 木・書き出す部品 (MakePlan)";
                return MakePlan(index, root, p4k, ct);
            }, ct);
            plan.Name = job.Name;
            if (plan.Socpaks.Count == 0) throw new InvalidOperationException("書き出す部品 (ルートとその直下の Child の socpak) がありません");
            job.EstimatedSeconds = EstimateSeconds(plan.Socpaks.Count, 1);
            Ship3DExports.LogJob(job, $"自動の取り直しを始めます ({(_refreshReason.TryGetValue(shortName, out var why) ? why : "")})。" +
                $"starbreaker {plan.Socpaks.Count} 回 (1 本ずつ)、予想 {job.EstimatedSeconds:F0} 秒・約 {Ship3DExports.FormatBytes(EstimateBytes(plan))}");
            // 船の 3D の取り直しが書き出している間は待つ (合わせて 1 本ずつ)
            if (Ship3DExports.Instance.RefreshCurrent != null) job.SetPhase(Ship3DJobPhase.Estimating, "自動の取り直し: 船の 3D の取り直しが終わるのを待っています");
            while (Ship3DExports.Instance.RefreshCurrent != null) await Task.Delay(1000, ct);
            RefreshCurrent = job;
            await Ship3DExports.Instance.WaitTurnAsync(job, ct);
            var path = await ExportAsync(job, shortName, extractor.StarBreakerExe, stamp, index!, plan, allowExtra: false);
            _refreshDone++;
            tcs.TrySetResult(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            keepOld = true;
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            _refreshCancelled++;
            Ship3DExports.LogJob(job, $"自動の取り直しをやめました (段階: {(job.LogStage.Length > 0 ? job.LogStage : "順番待ち")})。古いものは残します (次の照合でまた並びます)");
            tcs.TrySetResult(null);
            return false;
        }
        catch (Exception e)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            _refreshFailed++;
            Ship3DExports.LogJob(job, $"自動の取り直しが失敗しました (段階: {job.LogStage})。古いものは消します: {Ship3DExports.Describe(e)}");
            tcs.TrySetResult(null);
            return false;
        }
        finally
        {
            index?.Dispose();
            if (RefreshCurrent == job) RefreshCurrent = null;
            if (keepOld) _pendingDelete.Remove(shortName);
            _refreshReason.Remove(shortName);
            RemoveJob(job, shortName);
        }
    }

    // 印の違う場所を全部照合する (UI スレッド)。変わっていた場所は照合が取り直しに並べる。動いている間に頼まれたら、終わってからもう 1 回
    public void StartAutoRefresh(string why)
    {
        if (_scanRunning) { _scanAgain = true; return; }
        _ = ScanAsync(why);
    }

    private async Task ScanAsync(string why)
    {
        _scanRunning = true;
        try
        {
            do
            {
                _scanAgain = false;
                var stamp = Ship3DExports.CurrentStamp();
                if (stamp == null) break;
                var cands = await Task.Run(() => CachedEntries()
                    .Select(e => (e.Scene, Short: ShortOfKey(e.Key), Head: ReadOurHeader(ShortOfKey(e.Key))))
                    .Where(x => x.Head != null && !x.Head.Outdated && !SameStamp(x.Head, stamp))
                    .OrderByDescending(x => x.Scene.LastWriteTimeUtc).Select(x => (x.Short, Record: x.Head!.Record)).ToList());
                cands = cands.Where(x => !_byShort.ContainsKey(x.Short) && !_verifying.ContainsKey(x.Short) && !_pendingDelete.Contains(x.Short)).ToList();
                if (cands.Count == 0) continue;
                Log($"自動の取り直し ({why}): Data.p4k の印が違う場所 {cands.Count} か所を 1 か所ずつ照合します (変わっていた場所は取り直しに並べます)");
                var sw = Stopwatch.StartNew();
                int same = 0, changed = 0;
                foreach (var (sn, rec) in cands)
                {
                    while (Ship3DExports.Instance.CpuHigh) await Task.Delay(1000);
                    if (StateOf(rec) != Ship3DCacheState.NeedsVerify) continue;
                    if (await VerifyAsync(rec)) same++; else changed++;
                }
                Log($"自動の取り直し ({why}): 照合が終わりました: 変わっていなかった {same} か所、変わっていた {changed} か所 (取り直しに並べました)、{sw.Elapsed.TotalSeconds:F1} 秒");
            } while (_scanAgain);
        }
        catch (Exception e) { Log($"自動の取り直し ({why}) の照合が失敗しました: {Ship3DExports.Describe(e)}"); }
        finally { _scanRunning = false; }
    }


    // ===== 書き出しの計画 =====

    // 置く部品 1 つ: socpak と、その枠 → ルートの枠 (局所) の変換、木の深さ (ルート 0)
    internal sealed record Part(string Socpak, StationXf Xf, int Depth);

    internal sealed class Plan
    {
        public required StationTree Tree { get; init; }
        public required List<Part> Parts { get; init; }
        // 書き出す socpak (重なりなし。並びは木の順)
        public required List<string> Socpaks { get; init; }
        public required string TreeHash { get; init; }
        // .soc の CGF の大きさの合計 (予想の大きさに使う)
        public long GeometryBytes { get; init; }
        // 場所の名前 (scene.json の name。画面・ログに出す。決めるのは呼ぶ側)
        public string Name { get; set; } = "";
        // 木の深さ 1 までで socpak が p4k に無かったインスタンスの数 (置かない)
        public int NotFound { get; init; }
        public double Seconds { get; init; }
    }

    // 書き出しの計画: 木を辿り (中身も読む。印に使う外の設備のため)、深さ MaxPartDepth までのインスタンスのうち socpak のあるものを部品にする
    internal static Plan MakePlan(StationIndex index, StationRoot root, Dictionary<ulong, (long Size, uint Crc)> p4k, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tree = StationTree.Load(index, root, ct: ct);
        var parts = new List<Part>();
        var notFound = 0;
        foreach (var i in tree.Instances)
        {
            if (i.Depth > MaxPartDepth || i.Socpak.Length == 0) continue;
            if (!i.Found) { notFound++; continue; }
            parts.Add(new Part(i.Socpak, i.ToStation, i.Depth));
        }
        var socpaks = parts.Select(p => p.Socpak).Distinct(StringComparer.Ordinal).ToList();
        long geo = 0;
        foreach (var s in socpaks) geo += StationMapExports.SocGeometryBytes(index, s, p4k);
        return new Plan
        {
            Tree = tree, Parts = parts, Socpaks = socpaks, TreeHash = StationMapExports.TreeHash(tree), GeometryBytes = geo, NotFound = notFound,
            Seconds = sw.Elapsed.TotalSeconds,
        };
    }

    // 予想時間 (秒): BaseSeconds + 起動の数 × SecondsPerRun ÷ 同時の本数 × (1 + ParallelSlowdown × (同時の本数 − 1))。同時の本数は起動の数を超えない
    internal static double EstimateSeconds(int runs, int parallel)
    {
        var p = Math.Max(1, Math.Min(parallel, runs));
        return BaseSeconds + runs * SecondsPerRun / p * (1 + ParallelSlowdown * (p - 1));
    }

    internal static long EstimateBytes(Plan plan) => (long)(plan.GeometryBytes * GlbPerGeometryByte);

    // ===== 1 か所の［読み込み］ =====

    private readonly Dictionary<string, Ship3DJob> _byShort = new(StringComparer.OrdinalIgnoreCase);

    // ［読み込み］: その場所の 3D をゲームデータから書き出す (始める前に予想を出して聞く)。終わったら scene.json のパス。
    // すでに最新のものがあればそのパス。同じ場所の書き出しが動いていれば合流する。取り消し・失敗は null (知らせは出し済み)
    public async Task<string?> ImportAsync(string record, string name, Window? owner)
    {
        var ex = Ship3DExports.Instance;
        var shortName = ShortName(record);
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null)
        {
            ShowError(owner, "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。");
            return null;
        }
        if (_byShort.TryGetValue(shortName, out var running) && running.Task != null)
        {
            // 自動の取り直しの順番待ちなら、いちばん先にする
            PromoteRefresh(running);
            Log($"「{name}」({shortName}) の［読み込み］: 同じ場所の書き出しが動いているので、それに合流します");
            try { return await running.Task; }
            catch { return null; }
        }
        if (await GetCachedPathAsync(record) is { } cached)
        {
            Log($"「{name}」({shortName}) の［読み込み］: 使える 3D があるので書き出しません ({cached})");
            return cached;
        }
        if (ex.CpuHigh)
        {
            ShowError(owner, $"CPU 使用率が {ex.CpuPercent:F0}% と高いため、書き出しを始めません。90% 未満に下がってからもう一度押してください。");
            return null;
        }
        if (HasForeignFolder(shortName))
        {
            ShowError(owner, $"置き場所に同じ名前のフォルダ (stations3d\\{shortName}) があり、このアプリが書いたものではないので書き出しません。置き場所を変えるか、そのフォルダを移してください。");
            return null;
        }
        if (FreeSpaceProblem() is { } free)
        {
            ShowError(owner, free + "。");
            return null;
        }
        if (_byShort.TryGetValue(shortName, out running) && running.Task != null)
        {
            // 照合で変わっていて自動の取り直しに並んだ場所なら、いちばん先にする
            PromoteRefresh(running);
            try { return await running.Task; }
            catch { return null; }
        }
        var job = new Ship3DJob(record, KeyOf(shortName), string.IsNullOrWhiteSpace(name) ? shortName : name, batch: null) { Kind = Ship3DJobKind.Station3D };
        Ship3DExports.LogJob(job, $"書き出しを始めます (単体、record {record}、Data.p4k {stamp.Path})");
        AddJob(job, shortName);
        job.Task = RunSingleAsync(job, shortName, stamp, owner);
        return await job.Task;
    }

    private static string? FreeSpaceProblem()
    {
        var d = Ship3DExports.DriveOf(Ship3DExports.Folder);
        if (d is { } x && x.Free < MinFreeBytes)
            return $"置き場所のドライブ {x.Root} の空きが {Ship3DExports.FormatBytes(x.Free)} しかありません (途中の作業に {Ship3DExports.FormatBytes(MinFreeBytes)} 以上要ります)";
        return null;
    }

    private void AddJob(Ship3DJob job, string shortName)
    {
        _byShort[shortName] = job;
        Ship3DExports.Instance.AddJob(job);
        RaiseStateChanged();
    }

    private void RemoveJob(Ship3DJob job, string shortName)
    {
        if (_byShort.TryGetValue(shortName, out var j) && j == job) _byShort.Remove(shortName);
        Ship3DExports.Instance.RemoveJob(job);
        DeletePendingIfIdle(shortName);
        RaiseStateChanged();
    }

    private async Task<string?> RunSingleAsync(Ship3DJob job, string shortName, Ship3DExports.P4kStamp stamp, Window? owner)
    {
        StationIndex? index = null;
        var total = Stopwatch.StartNew();
        try
        {
            job.LogStage = "StarBreaker の用意";
            var extractor = new GameDataExtractor(DetailPanel.WorkDir());
            try { await extractor.EnsureStarBreakerAsync(); }
            catch (Exception e)
            {
                Ship3DExports.LogJob(job, $"StarBreaker を用意できませんでした: {Ship3DExports.Describe(e)}");
                ShowError(owner, $"StarBreaker を用意できませんでした: {e.Message}");
                job.SetPhase(Ship3DJobPhase.Failed, "");
                return null;
            }
            job.SetPhase(Ship3DJobPhase.Estimating, "見積もり中 (ゲームデータを調べています)");
            var ct = job.Cts.Token;
            var dbPath = DetailPanel.DbPath();
            var record = job.Record;
            var plan = await Task.Run(async () =>
            {
                job.LogStage = "計画: 星図の場所の表を読む";
                var places = StationMapExports.LoadPlaces(dbPath).Select(p => p.Place).ToList();
                job.LogStage = "計画: ゲームデータの索引 (StationIndex.Build)";
                index = StationIndex.Build(stamp.Path, ct);
                job.LogStage = "計画: ルート探し (FindRoots)";
                var search = index.FindRoots(places, ct: ct);
                if (!search.Roots.TryGetValue(record, out var root))
                    throw new InvalidOperationException("ゲームデータの中にこの場所が見つかりませんでした");
                job.LogStage = "計画: Data.p4k の中央ディレクトリ";
                var p4k = await Ship3DP4kIndex.GetAsync(stamp);
                job.LogStage = "計画: 木・書き出す部品 (MakePlan)";
                return MakePlan(index, root, p4k, ct);
            }, ct);
            plan.Name = job.Name;
            if (plan.Socpaks.Count == 0) throw new InvalidOperationException("書き出す部品 (ルートとその直下の Child の socpak) がありません");
            var slotsNow = Ship3DExports.Instance.FreeRunSlots();
            job.EstimatedSeconds = EstimateSeconds(plan.Socpaks.Count, slotsNow);
            var bytes = EstimateBytes(plan);
            Ship3DExports.LogJob(job, $"計画ができました: 全体 {total.Elapsed.TotalSeconds:F1} 秒 (木 {plan.Seconds:F1} 秒、インスタンス {plan.Tree.Instances.Count})。" +
                $"部品 {plan.Parts.Count} (socpak {plan.Socpaks.Count} 種類、p4k に無い {plan.NotFound})、starbreaker {plan.Socpaks.Count} 回の予定、" +
                $"予想 {job.EstimatedSeconds:F0} 秒 (同時 {slotsNow} 本で見込んだもの)・約 {Ship3DExports.FormatBytes(bytes)}");
            job.LogStage = "書き出すかの確認";
            job.SetPhase(Ship3DJobPhase.Estimating, "書き出すか確かめています");
            var msg = $"「{job.Name}」の 3D をゲームデータから書き出します。\n\n" +
                      $"予想時間: 約 {Ship3DExports.FormatSeconds(job.EstimatedSeconds)} (StarBreaker を {plan.Socpaks.Count} 回動かします)\n" +
                      $"予想の大きさ: 約 {Ship3DExports.FormatBytes(bytes)}\n\n" +
                      "書き出しは裏で行います (その間もほかの操作・ほかの書き出しができます)。始めますか？";
            var ok = owner != null && owner.IsLoaded
                ? MessageBox.Show(owner, msg, "ステーションの 3D 表示", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                : MessageBox.Show(msg, "ステーションの 3D 表示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK)
            {
                Ship3DExports.LogJob(job, "確認の窓で［キャンセル］が押されたので書き出しません");
                job.SetPhase(Ship3DJobPhase.Cancelled, "");
                return null;
            }
            await Ship3DExports.Instance.WaitTurnAsync(job, ct);
            return await ExportAsync(job, shortName, extractor.StarBreakerExe, stamp, index!, plan);
        }
        catch (OperationCanceledException)
        {
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            Log($"「{job.Name}」({shortName}) の書き出しを取り消しました (段階: {job.LogStage}、経過 {total.Elapsed.TotalSeconds:F1} 秒)");
            return null;
        }
        catch (Exception e)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            Ship3DExports.LogJob(job, $"失敗しました (段階: {job.LogStage}、経過 {total.Elapsed.TotalSeconds:F1} 秒): {Ship3DExports.Describe(e)}");
            ShowError(owner, $"「{job.Name}」({shortName}) の 3D を書き出せませんでした: {e.Message}");
            return null;
        }
        finally
        {
            index?.Dispose();
            RemoveJob(job, shortName);
        }
    }

    // 順番が来た書き出しを動かす (WaitTurnAsync の後)。終わったら scene.json のパス。
    //   作業用のフォルダの下に GLB と scene.json を作り、できたら置き場所のフォルダと入れ替える。まとめての中ではないので、最大容量を超えていれば古いものから消す
    // allowExtra: starbreaker を並行に足すか (自動の取り直しでは足さない。1 本ずつ)
    private async Task<string> ExportAsync(Ship3DJob job, string shortName, string exe, Ship3DExports.P4kStamp stamp, StationIndex index, Plan plan, bool allowExtra = true)
    {
        var ex = Ship3DExports.Instance;
        var ct = job.Cts.Token;
        var work = Path.Combine(Ship3DExports.WorkFolder, $"station3d_{shortName}_{Guid.NewGuid():N}");
        job.SetPhase(Ship3DJobPhase.Exporting, "");
        job.Watch.Restart();
        try
        {
            job.LogStage = "書き出しの前の空きの確認";
            if (FreeSpaceProblem() is { } free) throw new InvalidOperationException(free);
            var slots = allowExtra ? ex.FreeRunSlots(job) : 1;
            job.EstimatedSeconds = EstimateSeconds(plan.Socpaks.Count, slots);
            Directory.CreateDirectory(work);
            var dispatcher = Application.Current?.Dispatcher;
            void Progress(int done, int all)
            {
                if (done <= 0 || all <= 0) return;
                var e = job.Watch.Elapsed.TotalSeconds;
                var est = Math.Max(e / done * all + BaseSeconds, e);
                if (dispatcher != null) dispatcher.BeginInvoke(() => job.EstimatedSeconds = est);
                else job.EstimatedSeconds = est;
            }
            Func<Task<bool>>? tryExtra = dispatcher == null || !allowExtra ? null : async () =>
            {
                if (FreeSpaceProblem() != null) return false;
                return await dispatcher.InvokeAsync(() => ex.TryAcquireExtraRun());
            };
            Action? releaseExtra = dispatcher == null ? null : () => dispatcher.BeginInvoke(() => ex.ReleaseExtraRun());
            Ship3DExports.LogJob(job, $"書き出しを始めます: starbreaker {plan.Socpaks.Count} 回、予想 {job.EstimatedSeconds:F0} 秒 (同時 {slots} 本で見込んだもの)。作業用のフォルダ {work}");
            var result = await Task.Run(() => BuildAsync(plan, index, exe, stamp, work, job.Record, Progress, ct, s => job.LogStage = s, tryExtra, releaseExtra), ct);
            ex.EndRun(job);

            job.LogStage = "保存";
            if (HasForeignFolder(shortName))
                throw new InvalidOperationException($"置き場所に同じ名前のフォルダ (stations3d\\{shortName}) があり、このアプリが書いたものではないので上書きしません");
            Directory.CreateDirectory(Folder);
            var final = PlaceFolder(shortName);
            if (Directory.Exists(final))
            {
                // 前の版 (このアプリのもの。HasForeignFolder で確かめ済み) を消してから入れ替える
                Directory.Delete(final, recursive: true);
            }
            Directory.Move(work, final);
            Forget(ScenePath(shortName));
            _pendingDelete.Remove(shortName);
            job.Watch.Stop();
            Log($"「{job.Name}」({shortName}) の 3D を書き出しました: {job.Watch.Elapsed.TotalSeconds:F1} 秒、starbreaker {plan.Socpaks.Count} 回 " +
                $"(時間の足し合わせ {result.RunSeconds:F1} 秒、同時に動いた最大 {result.MaxActive} 本)、GLB 合計 {Ship3DExports.FormatBytes(result.GlbBytes)} " +
                $"(予想 {Ship3DExports.FormatBytes(EstimateBytes(plan))})、部品 {plan.Parts.Count}、印 {result.Markers}、照合用の元ファイル {result.Sources} 個");
            job.SetPhase(Ship3DJobPhase.Done, "");
            // 自動の取り直し (InBatch) の中では古いものを消さない (並べたものが全部終わったときに 1 回だけ。ProcessRefreshAsync)
            if (!job.InBatch) ex.EnforceLimit(justExported: job.Short);
            RaiseStateChanged();
            Exported?.Invoke(shortName);
            return ScenePath(shortName);
        }
        finally
        {
            job.Watch.Stop();
            ex.EndRun(job);
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    internal sealed record BuildResult(long GlbBytes, double RunSeconds, int MaxActive, int Markers, int Sources);

    // 書き出しの本体 (UI に触れない。画面なしの確かめからも呼ぶ): socpak ごとに starbreaker で GLB を作り、scene.json を書く。
    //   starbreaker は自分の枠 1 本のほかに、tryExtra が許すだけ並行に動かす (地図の取り込み StationFloorBuilder.ExportAsync と同じ考え)。
    //   1 回でも失敗したら例外 (書きかけは呼ぶ側が消す)
    internal static async Task<BuildResult> BuildAsync(Plan plan, StationIndex index, string exe, Ship3DExports.P4kStamp stamp, string work, string record,
        Action<int, int>? progress, CancellationToken ct, Action<string>? onStage = null, Func<Task<bool>>? tryExtra = null, Action? releaseExtra = null)
    {
        var n = plan.Socpaks.Count;
        var files = plan.Socpaks.Select((s, i) => $"{i:D3}_{SafeStem(s)}.glb").ToList();
        var queue = new ConcurrentQueue<int>(Enumerable.Range(0, n));
        var errors = new ConcurrentBag<string>();
        var wake = new SemaphoreSlim(0);
        int done = 0, active = 0, maxActive = 0;
        double runSeconds = 0;
        var lockObj = new object();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Worker(bool extra)
        {
            try
            {
                while (!stop.IsCancellationRequested && queue.TryDequeue(out var i))
                {
                    lock (lockObj) { active++; maxActive = Math.Max(maxActive, active); }
                    onStage?.Invoke($"starbreaker {done + 1}〜 / 全 {n} 回");
                    var glb = Path.Combine(work, files[i]);
                    var pattern = "objectcontainers\\" + plan.Socpaks[i].Replace('/', '\\');
                    var args = $"socpak export {Quote(pattern)} {Quote(glb)} --p4k {Quote(stamp.Path)} --materials none --no-lights --lod {Lod}";
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var (code, tail) = await StarBreakerRunner.RunAsync(exe, args, null, null, stop.Token);
                        if (code != 0 || !File.Exists(glb))
                        {
                            errors.Add($"{plan.Socpaks[i]}: 終了コード {code}、GLB {(File.Exists(glb) ? "あり" : "なし")}。{LastLine(tail)}");
                            stop.Cancel();
                        }
                    }
                    finally
                    {
                        lock (lockObj) { active--; runSeconds += sw.Elapsed.TotalSeconds; done++; }
                        progress?.Invoke(done, n);
                        try { wake.Release(); } catch (ObjectDisposedException) { }
                    }
                }
            }
            finally { if (extra) releaseExtra?.Invoke(); }
        }
        var workers = new List<Task> { Worker(false) };
        while (true)
        {
            var all = Task.WhenAll(workers);
            if (all.IsCompleted) break;
            // 失敗・取り消しで止めた後は、動いている回が終わるのを待つだけ (下の待ちは止めた後すぐ返るので、回り続けないように)
            if (stop.IsCancellationRequested)
            {
                try { await all; } catch { }
                break;
            }
            if (!queue.IsEmpty && !stop.IsCancellationRequested && tryExtra != null && workers.Count < n && await tryExtra())
            {
                workers.Add(Worker(true));
                continue;
            }
            await Task.WhenAny(all, wake.WaitAsync(stop.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        }
        try { await Task.WhenAll(workers); }
        catch (OperationCanceledException) when (!errors.IsEmpty) { }
        ct.ThrowIfCancellationRequested();
        if (!errors.IsEmpty)
        {
            var list = errors.ToList();
            Log($"「{ShortName(record)}」の書き出しで starbreaker が {list.Count} 回失敗しました:\n{string.Join("\n", list)}");
            throw new InvalidOperationException($"starbreaker が失敗しました: {list[0]}");
        }
        onStage?.Invoke("照合用の元ファイル");
        var p4k = await Ship3DP4kIndex.GetAsync(stamp);
        var sources = BuildSources(index, plan, p4k);
        onStage?.Invoke("scene.json を書く");
        var glbBytes = files.Sum(f => new FileInfo(Path.Combine(work, f)).Length);
        var (json, markers) = WriteScene(plan, files, record, stamp, sources, work);
        await File.WriteAllTextAsync(Path.Combine(work, SceneFile), json, new UTF8Encoding(false), ct);
        return new BuildResult(glbBytes, runSeconds, maxActive, markers, sources.Count);
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    // 標準エラーの最後の行 (Rust の「note: run with `RUST_BACKTRACE=1` …」の行は除く)
    private static string LastLine(string text) =>
        text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && !l.StartsWith("note:", StringComparison.Ordinal)) ?? "";

    // GLB のファイル名に使う socpak の名前 (拡張子なし。英数字と _ - . 以外は _)
    private static string SafeStem(string socpak)
    {
        var stem = Path.GetFileNameWithoutExtension(socpak.Replace('\\', '/').Split('/').Last());
        return Regex.Replace(stem, @"[^A-Za-z0-9_\-.]", "_");
    }

    // .soc の中の CGF のパス (StationMapExports の CgfPathRegex と同じ形)
    private static readonly Regex CgfPathRegex = new(@"[A-Za-z0-9_/\\.\-]+\.(?:cgf|cga|cgfm|cgam)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 照合用の元ファイル: 木の全 socpak (印に使う外の設備は木の全体から取るため。p4k に無いものは大きさ -1) と、
    // 書き出した socpak の .soc にある CGF・CGA (p4k に無いものは大きさ -1) と同名に m を付けたもの (p4k にあれば)
    internal static List<Ship3DExports.SourceFile> BuildSources(StationIndex index, Plan plan, Dictionary<ulong, (long Size, uint Crc)> p4k)
    {
        var result = new List<Ship3DExports.SourceFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path, bool required)
        {
            if (!seen.Add(path)) return;
            if (p4k.TryGetValue(Ship3DEstimator.Hash(path), out var v)) result.Add(new Ship3DExports.SourceFile { Path = path, Size = v.Size, Crc = v.Crc });
            else if (required) result.Add(new Ship3DExports.SourceFile { Path = path, Size = -1, Crc = 0 });
        }
        foreach (var sp in plan.Tree.Instances.Select(i => i.Socpak).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            Add(Ship3DEstimator.Normalize("data/objectcontainers/" + sp), required: true);
        foreach (var socpak in plan.Socpaks.OrderBy(s => s, StringComparer.Ordinal))
        {
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                using var zip = index.OpenSocpak(socpak);
                if (zip != null)
                    foreach (var e in zip.Entries)
                    {
                        if (!e.FullName.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)) continue;
                        var t = Encoding.Latin1.GetString(StationIndex.ReadAll(e));
                        foreach (Match m in CgfPathRegex.Matches(t)) paths.Add(Ship3DEstimator.Normalize(m.Value));
                    }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { }
            foreach (var p in paths)
            {
                Add(p, required: true);
                if (p.EndsWith(".cgf", StringComparison.Ordinal) || p.EndsWith(".cga", StringComparison.Ordinal)) Add(p + "m", required: false);
            }
        }
        return result;
    }

    // scene.json を作る。戻り値は JSON と印の数
    //   files: [{file, socpak, bytes}]、parts: [{f: files の番号, d: 木の深さ (ルート 0), xf: [x, y, z, qw, qx, qy, qz]}] (局所。z 上)
    //   markers: pads [{pos, key, size}] (key は HUDDisplayName。無ければ "")、hangars [{pos, size (S・M・L・XL。無ければ ""), gatewaySize}]、
    //            freight [{pos, deck (外部貨物の区画の鍵 @cargodeck_NN。無ければ ""), deckGuess}]。pos は局所 (z 上)
    private static (string Json, int Markers) WriteScene(Plan plan, List<string> files, string record, Ship3DExports.P4kStamp stamp,
        List<Ship3DExports.SourceFile> sources, string work)
    {
        using var ms = new MemoryStream();
        int markers = 0;
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            static double R(double v, int d = 3) => Math.Round(v, d);
            void Vec(string name, (double X, double Y, double Z) v) { w.WriteStartArray(name); w.WriteNumberValue(R(v.X)); w.WriteNumberValue(R(v.Y)); w.WriteNumberValue(R(v.Z)); w.WriteEndArray(); }
            w.WriteStartObject();
            w.WriteString("app", Mark);
            w.WriteNumber("format", Format);
            w.WriteString("record", record);
            // 場所の名前 (画面・ログに出す。内部のキーを出さないため)
            w.WriteString("name", plan.Name);
            w.WriteStartObject("p4k");
            w.WriteString("path", stamp.Path); w.WriteNumber("size", stamp.Size); w.WriteNumber("writeUtcTicks", stamp.WriteUtcTicks);
            w.WriteEndObject();
            w.WriteString("treeHash", plan.TreeHash);
            w.WriteString("exportedUtc", DateTime.UtcNow.ToString("o"));
            w.WriteNumber("lod", Lod);
            w.WriteNumber("maxPartDepth", MaxPartDepth);
            w.WriteStartArray("files");
            for (int i = 0; i < files.Count; i++)
            {
                w.WriteStartObject();
                w.WriteString("file", files[i]);
                w.WriteString("socpak", plan.Socpaks[i]);
                w.WriteNumber("bytes", new FileInfo(Path.Combine(work, files[i])).Length);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            var fileOf = plan.Socpaks.Select((s, i) => (s, i)).ToDictionary(t => t.s, t => t.i, StringComparer.Ordinal);
            w.WriteStartArray("parts");
            foreach (var p in plan.Parts)
            {
                w.WriteStartObject();
                w.WriteNumber("f", fileOf[p.Socpak]);
                w.WriteNumber("d", p.Depth);
                w.WriteStartArray("xf");
                foreach (var v in new[] { R(p.Xf.X), R(p.Xf.Y), R(p.Xf.Z), R(p.Xf.Qw, 7), R(p.Xf.Qx, 7), R(p.Xf.Qy, 7), R(p.Xf.Qz, 7) }) w.WriteNumberValue(v);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("markers");
            w.WriteStartArray("pads");
            foreach (var x in plan.Tree.Exteriors.Where(x => x.Kind == "pad"))
            {
                w.WriteStartObject(); Vec("pos", x.Pos); w.WriteString("key", x.NameKey);
                if (x.Size is { } sz) Vec("size", sz);
                w.WriteEndObject(); markers++;
            }
            w.WriteEndArray();
            w.WriteStartArray("hangars");
            foreach (var x in plan.Tree.Exteriors.Where(x => x.Kind == "hangar"))
            {
                w.WriteStartObject(); Vec("pos", x.Pos);
                // 区分は地図の JSON (StationMapJson の exterior.hangars) と同じ (2 Small・4 Medium・5 Large・6 XL)
                w.WriteString("size", x.GatewaySize switch { 2 => "S", 4 => "M", 5 => "L", 6 => "XL", _ => "" });
                w.WriteNumber("gatewaySize", x.GatewaySize);
                w.WriteEndObject(); markers++;
            }
            w.WriteEndArray();
            w.WriteStartArray("freight");
            foreach (var x in plan.Tree.Exteriors.Where(x => x.Kind == "freight"))
            {
                w.WriteStartObject(); Vec("pos", x.Pos); w.WriteString("deck", x.Deck);
                if (x.DeckGuess) w.WriteBoolean("deckGuess", true);
                w.WriteEndObject(); markers++;
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartArray("sources");
            foreach (var s in sources)
            {
                w.WriteStartObject(); w.WriteString("path", s.Path); w.WriteNumber("size", s.Size); w.WriteNumber("crc", s.Crc); w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return (Encoding.UTF8.GetString(ms.ToArray()), markers);
    }
}
