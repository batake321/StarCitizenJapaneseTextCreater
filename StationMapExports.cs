using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;

namespace StarCitizenJapaneseTextCreater;

// ステーションの階層マップの段階 5: 取り込み (地図の JSON を作って置く) とキャッシュ。アプリ全体で 1 つ (StationMapExports.Instance)。
//   船の 3D モデル (Ship3DExports) のものをそのまま使う: 置き場所 (Ship3DExports.Folder の下の stations\)・最大容量 (Ship3DExports.MaxMb)・
//   待ち行列・同時数・CPU 使用率 (Ship3DExports.WaitTurnAsync)・進み具合の欄 (Ship3DExports.Jobs。Ship3DJob の Kind が Station、Short は "station:<短い名前>")・
//   StarBreakerRunner・Ship3DP4kIndex・Ship3DExports.CurrentStamp。
//   取り込み: 1 か所 (ImportAsync) と、まとめて (RunBatchAsync)。どちらも
//     StationIndex.Build → FindRoots (星図の場所は gamedata_cache.db の starmap_locations) → StationTree.Load → 書き出しの計画 (StationFloorBuilder.PlanExports) →
//     順番を待つ → StarBreaker の socpak export (GLB は作業用のフォルダの下の station_<短い名前>_<GUID> に作り、読んだらすぐ消す) →
//     床・階・道順 (ComposeJson。段階 3・4 の呼び出しはここ 1 か所) → 照合用の元ファイルと木の形のハッシュを入れる (Finish) →
//     作業用のフォルダに書いてから名前を変える (<置き場所>\stations\<短い名前>.json)
//   キャッシュ: 置き場所の stations\ の *.json のうち、頭に印 ("app":"StarCitizenJapaneseTextCreater.stationmap") があり record の短い名前がファイル名と同じもの。
//     印の無いファイルには触れない。書き出したときの Data.p4k の印 (p4k の path・size・writeUtcTicks) が今と同じなら最新。
//     違う (パッチ) ときは照合する (VerifyAsync): 元ファイル (木の全 socpak と、床・階段に使った .cgf / .cgfm 等) の p4k の中の大きさと Data.p4k に記録された CRC、
//     木の形のハッシュ (TreeHash) が同じなら、JSON の p4k の印を新しいものに書き換えて使い続ける (更新日時は元のまま)。
//     違う・照合できないときはその場で消す (表示中・書き出し中は後で消す)。船の DeleteChangedCache と同じ考え。
//   容量: 船とステーションを合わせて最大容量で数える (計画の案 a)。1 か所の取り込みの後は Ship3DExports.EnforceLimit で古いものから消す
//     (表示中・書き出し中・照合中は消さない)。まとめて取り込むの中では消さない (船のまとめてと同じ)。
//   始める前に置き場所のドライブの空き (MinFreeBytes) を確かめる。
internal sealed class StationMapExports : INotifyPropertyChanged
{
    private static StationMapExports? _instance;
    // UI スレッドで最初に触ったときに作る
    public static StationMapExports Instance => _instance ??= new StationMapExports();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ボタンの有効・無効を決め直すきっかけ (取り込みが加わった / 終わった・照合を始めた / 終えた・地図を書いた / 消した)。UI スレッドで呼ぶ
    public event Action? StateChanged;
    private void RaiseStateChanged() => StateChanged?.Invoke();
    // 取り込みが終わったステーション (短い名前)。そのステーションを表示している画面がそのまま表示する
    public event Action<string>? Exported;

    // ===== 定数 =====

    // 横の記録の印 (段階 4 の StationMapJson.App と同じ)
    public const string Mark = StationMapJson.App;
    // 保存する JSON の形の版 (JSON の "format")。版の数字は StationMapJson.Format (StationRouteGraph.cs) の 1 か所だけで、Write がその値を書く。上げるのはそこだけ。
    //   これより古い版の JSON は「取り込みが要る」(StateOf が None) として扱い、取り込み直しで置き換える (このアプリのものなので、使用量・最大容量では数え、古い順に消す)
    public const int Format = StationMapJson.Format;
    // 進み具合の欄 (Ship3DExports.Jobs) の Short と、容量の数え方 (Ship3DExports.EnforceLimit) のキーの頭
    public const string KeyPrefix = "station:";
    // Checkmate (RR_P2_L4) の実測 (2026-11-01、LIVE、この PC。scratchpad\stage5 の harness。ほかのエージェントの starbreaker と一部重なった):
    //   起動をまとめた後 (PlanGrouped): インスタンス 1,333、starbreaker 60 回 (要る socpak 199、一致 737) 150.1 秒、GLB 合計 2.6 GB を 1 つずつ作って消した (最大 191 MB)、
    //   床・道順 1.5 秒、計画 2.6 秒、JSON 1.5 MB、全体 151.6 秒。まとめる前は 192 回 396.4 秒 (重なり無し)・JSON 6.7 MB (段階 4 の前の形)。
    // 始める前に要る置き場所のドライブの空き (1GB。途中の GLB は 1 つずつ作ってすぐ消す。1 回の GLB の上限の見込み 480 MB (MaxGeometryBytesPerRun の説明) に余裕を見たもの)
    public const long MinFreeBytes = 1024L * 1024 * 1024;
    // 予想時間の係数 (説明は EstimateSeconds)。2026-11-01 に、starbreaker を同時に動かす形・MaxGeometryBytesPerRun 1,200 MB に合わせて決め直した
    //   (前は SecondsPerRun 2.5 = 1 本ずつの 150.1 秒 ÷ 60 回で、同時の本数を入れていなかった)。
    //   実測 (2026-11-01、LIVE、この PC (論理プロセッサ 24)、scratchpad\par の harness で書き出しの部分だけ。Star Citizen は動いていない (計る前ごとに tasklist で確認)。各 1 回):
    //     Checkmate (36 回): 同時 1 本 99.7 秒・3 本 36.2 秒・6 本 21.1 秒・11 本 18.0 秒
    //     Seraphim (21 回): 1 本 46.6 秒・3 本 17.0 秒・11 本 7.1 秒
    //     Orison (32 回): 1 本 79.2 秒・3 本 27.7 秒・11 本 12.1 秒
    //   SecondsPerRun: 1 本ずつの 3 か所の合計 225.5 秒 ÷ 89 回 ≈ 2.53 秒。
    //   ParallelSlowdown: 同時に動かすと 1 回が遅くなる分 (1 本足すごとに 1 回の時間が × (1 + この値 × 足した本数))。上の 10 回の予想との外れの最大が
    //     いちばん小さくなる値として 0.074 にした (外れは −20% (Checkmate 11 本) 〜 +20% (Seraphim 3 本))
    //   (2026-11-01 の決め直し) 利用者が実機でまとめて取り込んだ 88 か所 (app_log.txt 20:13〜21:01。Star Citizen を動かしながら) の実測に合わせた:
    //     starbreaker 1 回あたり (「starbreaker の中 計」÷ 回数) は 85 か所で中央値 6.30 秒・平均 6.37 秒 (3.6〜10.2 秒)。
    //     そのときほかに動いていた場所の数 (「動いている N 本」) ごとの 1 回あたりの中央値: 1 本 8.20 秒 (3 か所)・2 本 6.39 秒 (11)・3 本 5.73 秒 (48)・
    //     4 本 6.40 秒 (17)・5〜9 本 8.1〜9.2 秒 (各 1〜2 か所)。本数が増えても 1 回あたりは増えておらず、本数の多いところは数が少なく決められない。
    //     どの場所も、その場所の starbreaker は 1 本ずつ動いていた (「中 計」÷ 実時間の中央値 0.99) ので、同時に動かしたときの遅れ (ParallelSlowdown) は
    //     この記録からは決め直せない。前の値のままにする。SecondsPerRun は中央値の 6.30 にした (前の 2.53 は Star Citizen を止めて計った値)。
    //   床・道順の時間は予想に入れない: 取り込みの前に分かる値 (インスタンスの数) と合わない。実測は Lorville 368.5 秒 (利用者の取り込み。今のソースのハーネスで 151 秒)・
    //     Orison 80.6 秒・New Babbage 12.2 秒、ほかの 85 か所は 5 秒以下。インスタンスは Lorville 3,699・Orison 5,175 で、多いほうが速い
    public const double SecondsPerRun = 6.30;
    public const double ParallelSlowdown = 0.074;
    public const double BaseSeconds = 3.0;
    public const double SecondsPerInstance = 0.002;
    // まとめて取り込むの予想 (書き出しの計画の前に、インスタンスの数だけで見積もる。説明は RunBatchAsync): 151.6 秒 ÷ 1,333 ≈ 0.12 秒、1.5 MB ÷ 1,333 ≈ 1,200 バイト。
    // Checkmate 1 か所だけの実測なので、ほかのステーション (都市など) で合うかは確かめていない
    public const double BatchSecondsPerInstance = 0.12;
    public const double BytesPerInstance = 1200;

    private StationMapExports()
    {
        CleanWorkFolders();
    }

    // ===== 置き場所 =====

    public static string Folder => Path.Combine(Ship3DExports.Folder, "stations");
    // 部品の名前 → 種類の表 (同梱の表と、置き場所の stations\station_mesh_kinds_user.json。StationMeshKinds の説明)
    internal static StationMeshKinds MeshKinds() => StationMeshKinds.ForUser(Path.Combine(Folder, StationMeshKinds.UserFile));
    public static string ShortName(string record) =>
        record.StartsWith("StarMapObject.", StringComparison.OrdinalIgnoreCase) ? record["StarMapObject.".Length..] : record;
    public static string JsonPath(string shortName) => Path.Combine(Folder, shortName + ".json");
    public static string KeyOf(string shortName) => KeyPrefix + shortName;
    public static string KeyOf(FileInfo f) => KeyPrefix + Path.GetFileNameWithoutExtension(f.Name);
    public static bool IsKey(string key) => key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase);
    private static string ShortOfKey(string key) => IsKey(key) ? key[KeyPrefix.Length..] : key;
    private static void Log(string msg) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[ステーションの地図] {msg}");

    // 作業用のフォルダ (Ship3DExports.WorkFolder) の下に作る、1 回の取り込みの GLB の置き場所の名前の形 (station_<短い名前>_<GUID 32 桁>)
    private static readonly Regex WorkDirRegex = new(@"^station_.+_[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 前の起動で途中で終わった取り込みの GLB の置き場所を消す (この形の名前のフォルダだけ。JSON の一時ファイルは船の掃除 (CleanWorkFolder) の形に合うのでそちらで消える)
    private void CleanWorkFolders()
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

    // JSON の頭 (印・record・Data.p4k の印・木の形のハッシュ)
    // ListedFloors: 一覧に出る階の数 (Finish が書く "listedFloors"。形 12 から。無ければ -1)
    internal sealed record Header(string Record, string P4kPath, long P4kSize, long P4kWriteUtcTicks, string TreeHash, int Format, int ListedFloors = -1)
    {
        // 今の版 (Format) より古い形か
        public bool Outdated => Format < StationMapExports.Format;
    }

    // JSON の頭を読む (先頭の 64KB だけ。頭の欄は sources より前にある)。読めて、印があり、record の短い名前がファイル名と同じときだけ返す
    internal static Header? ReadOurHeader(string shortName) => ReadHeaderOf(JsonPath(shortName), shortName);

    private static Header? ReadHeaderOf(string path, string shortName)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                buf = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
                fs.ReadExactly(buf);
            }
            var final = buf.Length < 64 * 1024;
            var r = new Utf8JsonReader(buf, isFinalBlock: final, default);
            string app = "", record = "", p4kPath = "", hash = "";
            long size = 0, ticks = 0;
            int format = 0, listedFloors = -1;
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject) break;
                if (r.TokenType != JsonTokenType.PropertyName) return null;
                var name = r.GetString();
                if (!r.Read()) break;
                switch (name)
                {
                    case "format": if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var fv)) format = fv; break;
                    case "app": app = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "record": record = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "treeHash": hash = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "listedFloors": if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var lf)) listedFloors = lf; break;
                    case "p4k":
                        if (r.TokenType != JsonTokenType.StartObject) { if (!r.TrySkip()) goto done; break; }
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            var pn = r.GetString();
                            if (!r.Read()) goto done;
                            if (pn == "path" && r.TokenType == JsonTokenType.String) p4kPath = r.GetString() ?? "";
                            else if (pn == "size" && r.TokenType == JsonTokenType.Number) size = r.GetInt64();
                            else if (pn == "writeUtcTicks" && r.TokenType == JsonTokenType.Number) ticks = r.GetInt64();
                            else if (!r.TrySkip()) goto done;
                        }
                        break;
                    // 頭の後ろ (sources から先) は読まない
                    case "sources": goto done;
                    default: if (!r.TrySkip()) goto done; break;
                }
            }
        done:
            if (app != Mark || record.Length == 0 || p4kPath.Length == 0) return null;
            if (!string.Equals(ShortName(record), shortName, StringComparison.OrdinalIgnoreCase)) return null;
            return new Header(record, p4kPath, size, ticks, hash, format, listedFloors);
        }
        catch { return null; }
    }

    // キャッシュの地図 (置き場所の stations\ の直下の *.json のうち、頭に印があるもの)
    public static List<FileInfo> CachedFiles()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return new List<FileInfo>();
            return dir.GetFiles("*.json", SearchOption.TopDirectoryOnly)
                .Where(f => ReadHeaderOf(f.FullName, Path.GetFileNameWithoutExtension(f.Name)) != null).ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    public static long CacheBytes() => CachedFiles().Sum(f => f.Length);

    // 置き場所に同じ名前の .json があり、それがこのアプリのもの (頭に印) でなければ true (上書きしない)
    private static bool HasForeignFile(string shortName) => File.Exists(JsonPath(shortName)) && ReadOurHeader(shortName) == null;

    // ===== 状態 =====

    private static bool SameStamp(Header h, Ship3DExports.P4kStamp s) =>
        string.Equals(h.P4kPath, s.Path, StringComparison.OrdinalIgnoreCase) && h.P4kSize == s.Size && h.P4kWriteUtcTicks == s.WriteUtcTicks;

    // そのステーションの今の状態 (船と同じ Ship3DCacheState)
    public Ship3DCacheState StateOf(string record)
    {
        var shortName = ShortName(record);
        if (_verifying.ContainsKey(shortName)) return Ship3DCacheState.Verifying;
        if (_pendingDelete.Contains(shortName)) return Ship3DCacheState.None;
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) return Ship3DCacheState.NoGame;
        var h = ReadOurHeader(shortName);
        // 無い・今の版より古い形 (取り込み直しが要る) は None
        if (h == null || h.Outdated) return Ship3DCacheState.None;
        return SameStamp(h, stamp) ? Ship3DCacheState.Fresh : Ship3DCacheState.NeedsVerify;
    }

    // StateOf のうちファイルだけで決まる部分: 最新 (Fresh) か。UI スレッドの外から呼べる (設定タブのまとめて取り込むのボタンの数え直しに使う)。
    // 照合中・後で消すもの (StateOf では最新にならない) はここでは見ないので、呼ぶ側で HeldBack を見る
    internal static bool IsFreshOnDisk(string record, Ship3DExports.P4kStamp stamp)
    {
        var h = ReadOurHeader(ShortName(record));
        return h != null && !h.Outdated && SameStamp(h, stamp);
    }

    // StateOf が照合中・後で消すもの (最新でない) とするステーションか。UI スレッドで呼ぶ
    internal bool HeldBack(string record)
    {
        var shortName = ShortName(record);
        return _verifying.ContainsKey(shortName) || _pendingDelete.Contains(shortName);
    }

    public bool IsExporting(string record) => _byShort.ContainsKey(ShortName(record));

    // 表示に使える地図の JSON のパス。最新ならそのまま、Data.p4k が変わっていれば照合してから。使えなければ null (取り込みはしない)
    public async Task<string?> GetCachedPathAsync(string record)
    {
        var state = StateOf(record);
        if (state == Ship3DCacheState.Fresh) return JsonPath(ShortName(record));
        if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying)
            return await VerifyAsync(record) ? JsonPath(ShortName(record)) : null;
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

    // 最大容量で消さないもの (表示中・照合中。キーは "station:<短い名前>")。取り込み中は Ship3DExports.Jobs の Short で守られる
    internal IEnumerable<string> ProtectedKeys() => _views.Keys.Concat(_verifying.Keys).Select(KeyOf).ToList();

    // ===== 知らせ =====

    private string _notice = "";
    // 最後の知らせ (照合で消した等)。画面 (段階 6) が出す
    public string Notice { get => _notice; private set { _notice = value; Changed(nameof(Notice)); } }

    // logMsg: ログに残す文言が窓と違うとき (まとめて取り込むの窓はボタンの名前で出し、ログは前の文言のまま)
    private static void ShowError(Window? owner, string msg, string? logMsg = null)
    {
        Log(logMsg ?? msg);
        if (owner != null && owner.IsLoaded) MessageBox.Show(owner, msg, "ステーションの地図", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(msg, "ステーションの地図", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ===== 照合 =====

    private readonly Dictionary<string, Task<bool>> _verifying = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pendingDelete = new(StringComparer.OrdinalIgnoreCase);

    // Data.p4k が変わったステーションの元データを照合する。同じなら true (JSON の p4k の印を新しいものにする)。
    // 変わっていた・照合できなかったら false で、その場で消す (表示中・取り込み中なら後で消す)
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
            var head = ReadOurHeader(shortName) ?? throw new InvalidOperationException("地図の JSON が読めません");
            var sw = Stopwatch.StartNew();
            var dbPath = DetailPanel.DbPath();
            var (r, count) = await Task.Run(async () => await VerifyFilesAsync(JsonPath(shortName), head, stamp, dbPath, CancellationToken.None));
            reason = r;
            if (reason == null)
            {
                await Task.Run(() => RewriteStamp(JsonPath(shortName), stamp));
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
        DeleteChangedCache(shortName, reason!);
        return false;
    }

    // 照合の本体 (UI に触れない)。変わっていなければ (null, 元ファイルの数)、変わっていれば (理由, 数)
    internal static async Task<(string? Reason, int Count)> VerifyFilesAsync(string jsonPath, Header head, Ship3DExports.P4kStamp stamp, string dbPath, CancellationToken ct)
    {
        if (head.TreeHash.Length == 0) return ("照合の記録 (木の形のハッシュ) がありません", 0);
        var sources = ReadSources(jsonPath);
        if (sources.Count == 0) return ("照合の記録 (元ファイルの一覧) がありません", 0);
        var index = await Ship3DP4kIndex.GetAsync(stamp);
        foreach (var src in sources)
        {
            var now = index.TryGetValue(Ship3DEstimator.Hash(src.Path), out var v) ? (v.Size, v.Crc) : (-1L, 0u);
            if (now.Item1 != src.Size || now.Item2 != src.Crc) return ($"元データ {src.Path} が変わっていました", sources.Count);
        }
        var places = LoadPlaces(dbPath).Select(p => p.Place).ToList();
        using var sidx = StationIndex.Build(stamp.Path, ct);
        var search = sidx.FindRoots(places, ct: ct);
        if (!search.Roots.TryGetValue(head.Record, out var root)) return ("ゲームデータの中にこのステーションが見つかりませんでした", sources.Count);
        var tree = StationTree.Load(sidx, root, contents: false, ct: ct);
        if (TreeHash(tree) != head.TreeHash) return ("ステーションの構成 (木の形) が変わっていました", sources.Count);
        return (null, sources.Count);
    }

    // JSON の sources (照合用の元ファイル)
    private static List<Ship3DExports.SourceFile> ReadSources(string jsonPath)
    {
        var res = new List<Ship3DExports.SourceFile>();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
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

    // JSON の p4k の印を新しいものに書き換える (作業用のフォルダに書いてから名前を変える。最大容量の古い順が変わらないよう、更新日時は元のまま)
    private static void RewriteStamp(string jsonPath, Ship3DExports.P4kStamp stamp)
    {
        var written = File.GetLastWriteTimeUtc(jsonPath);
        var node = JsonNode.Parse(File.ReadAllBytes(jsonPath))!.AsObject();
        node["p4k"] = new JsonObject { ["path"] = stamp.Path, ["size"] = stamp.Size, ["writeUtcTicks"] = stamp.WriteUtcTicks };
        var shortName = Path.GetFileNameWithoutExtension(jsonPath);
        Directory.CreateDirectory(Ship3DExports.WorkFolder);
        var tmp = Ship3DExports.NewWorkFile("station_" + shortName, ".json");
        try
        {
            File.WriteAllText(tmp, node.ToJsonString(JsonOut), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(tmp, written);
            File.Move(tmp, jsonPath, overwrite: true);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
    }

    private void DeleteChangedCache(string shortName, string reason)
    {
        if (_views.ContainsKey(shortName) || _byShort.ContainsKey(shortName))
        {
            _pendingDelete.Add(shortName);
            Notice = $"「{shortName}」はパッチで元データが変わったので、表示・取り込みが終わったら地図を消します ({reason})";
            Log(Notice);
            RaiseStateChanged();
            return;
        }
        DeleteOurCache(shortName);
        Notice = $"「{shortName}」はパッチで元データが変わったので、地図を消しました ({reason})。［読み込み］で取り込み直せます";
        Log(Notice);
        RaiseStateChanged();
    }

    // 頭に印のある地図を消す。印が無ければ何もしない (false)
    private static bool DeleteOurCache(string shortName)
    {
        if (ReadOurHeader(shortName) == null) return false;
        try { File.Delete(JsonPath(shortName)); return true; }
        catch (Exception ex) { Log($"{shortName}.json を消せませんでした: {ex.Message}"); return false; }
    }

    private void DeletePendingIfIdle(string shortName)
    {
        if (!_pendingDelete.Contains(shortName) || _views.ContainsKey(shortName) || _byShort.ContainsKey(shortName)) return;
        _pendingDelete.Remove(shortName);
        DeleteOurCache(shortName);
        Notice = $"「{shortName}」はパッチで元データが変わったので、地図を消しました。［読み込み］で取り込み直せます";
        Log(Notice);
        RaiseStateChanged();
    }

    // 最大容量を超えたので消す (Ship3DExports.EnforceLimit から。キーは "station:<短い名前>")。消せたら true
    internal bool DeleteForLimit(string key)
    {
        var shortName = ShortOfKey(key);
        if (_views.ContainsKey(shortName) || _byShort.ContainsKey(shortName) || _verifying.ContainsKey(shortName)) return false;
        if (!DeleteOurCache(shortName)) return false;
        RaiseStateChanged();
        return true;
    }

    // ===== 星図の場所 =====

    // gamedata_cache.db の starmap_locations (record_name・uuid・type・system と英語名)。恒星 (Star) も含めて全部 (StationIndex.FindRoots が恒星から辿るため)。
    // 表・uuid の列が無ければ (インデックス構築前) 例外
    internal static List<(StationPlace Place, string Name)> LoadPlaces(string dbPath)
    {
        if (!File.Exists(dbPath)) throw new InvalidOperationException("ゲームデータの DB がありません (設定タブの［インデックス構築］が要ります)");
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name IN ('record_name', 'uuid', 'type', 'system', 'name_en')";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 5) throw new InvalidOperationException("星図の場所の表がありません (設定タブの［インデックス構築］が要ります)");
        cmd.CommandText = "SELECT record_name, uuid, type, system, name_en FROM starmap_locations";
        var list = new List<(StationPlace, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((new StationPlace(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), r.GetString(4)));
        return list;
    }

    // 階・区画の名前に使うもの: Localize は英語の global.ini の鍵 (@ なし) → 訳、PlaceNames は星図の場所の uuid (小文字) → 英語名 (name_en)
    // ShopNames は店の名前の表 (LoadShopNames): 鍵は「<場所の record>|<GUID>」(GUID は shop_id の 3 番目、小文字) と、どの場所でも同じ名前の店だけ「<GUID>」。
    // 名前は［詳細］の店の名前と同じ順 (franchise_name → shop_name_* → area_label → 販売データの店名) で決まったものだけ (Entity 名から作った内部の名前は入れない)
    internal sealed record MapNames(Func<string, string?>? Localize, IReadOnlyDictionary<string, string> PlaceNames, IReadOnlyDictionary<string, string> ShopNames);

    // 作業ディレクトリの english\global.ini (［新しいパッチを読み取る］が作るもの。GlobalIniParser.Parse で読む) と、星図の場所の名前。
    // global.ini が無ければ Localize は null (階・区画の名前は鍵のまま。StationRouteGraph の説明)。鍵は大文字小文字を区別しない
    internal static MapNames LoadNames(IEnumerable<(StationPlace Place, string Name)> places, string workDir, string dbPath)
    {
        Func<string, string?>? localize = null;
        var ini = Path.Combine(workDir, "english", "global.ini");
        if (File.Exists(ini))
        {
            var d = new Dictionary<string, string>(GlobalIniParser.Parse(ini), StringComparer.OrdinalIgnoreCase);
            localize = k => d.TryGetValue(k, out var v) ? v : null;
        }
        else Log($"英語の global.ini ({ini}) が無いので、階・区画の名前は鍵のままになります");
        var pn = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, n) in places)
            if (p.Uuid.Length > 0 && n.Length > 0) pn.TryAdd(p.Uuid.ToLowerInvariant(), n);
        return new MapNames(localize, pn, LoadShopNames(dbPath));
    }

    // 店の GUID (shop_id の 3 番目、小文字) → 店の名前。名前の決め方はアプリの［詳細］の店の名前 (GameDataQueryService.BuildShopTree) と同じ順で、ShopTree の同じ処理を使う
    // (2026-11-01、利用者の決定。前は franchise_name のある店の name だけ):
    //   1. franchise の英語名 (game_shops.franchise_name のある店の name)。ただし Entity 名に店ごとの鍵が当たれば 2 を先にする (ShopTree.ApplyGameNames)
    //   2. ゲームの店の名前: translations.db の shop_name_<語> の英語 (ShopTree.ApplyGameNames)
    //   3. 店の案内図の名札: game_shops.area_label の @area_name_* の英語 (ShopTree.ApplyAreaLabels。例「Ellroy's Coffee」)
    //   4. 店まで結べた価格のキーが 1 つだけなら、その販売データの店名 (ShopTree.ApplyDataNames。例「Admin - Seraphim」)
    // どれでも決まらない店 (アプリでは売っている品目の種類「店（…）」・店の種類を出す店) と、Entity 名から作った内部の名前は入れない。
    // 訳は dbPath と同じフォルダの translations.db (アプリと同じ)。表・列が無い (前の版の DB) ときは、その段を飛ばす
    internal static Dictionary<string, string> LoadShopNames(string dbPath)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(dbPath)) return res;
        var tree = new ShopTree();
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            long Count(string sql) { cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L); }
            if (Count("SELECT COUNT(*) FROM pragma_table_info('game_shops') WHERE name IN ('shop_id', 'name', 'franchise_name', 'inventory_type', 'location_record', 'entity_name')") != 6)
            { Log("ゲームの店の表 (franchise_name の列) が無いので、店の名前は階・区画の名前に使いません"); return res; }
            if (Count("SELECT COUNT(*) FROM pragma_table_info('starmap_locations') WHERE name IN ('record_name', 'name_en', 'type', 'parent_record', 'system')") != 5)
            { Log("星図の場所の表が無いので、店の名前は階・区画の名前に使いません"); return res; }
            var hasAreaLabel = Count("SELECT COUNT(*) FROM pragma_table_info('game_shops') WHERE name = 'area_label'") > 0;
            cmd.CommandText = "SELECT record_name, name_en, type, parent_record, system FROM starmap_locations";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) tree.AddLocation(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4));
            cmd.CommandText = "SELECT shop_id, name, inventory_type, location_record, entity_name, franchise_name, " + (hasAreaLabel ? "area_label" : "''") + " FROM game_shops";
            using (var r = cmd.ExecuteReader())
                while (r.Read()) tree.AddShop(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5).Length > 0, r.GetString(6));
            // 2・3 の訳 (translations.db。アプリの LocReader と同じ引き方: 鍵か「鍵,P」の english)
            var trPath = Path.Combine(Path.GetDirectoryName(dbPath) ?? "", "translations.db");
            if (File.Exists(trPath))
            {
                try
                {
                    using var tc = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={trPath};Mode=ReadOnly");
                    tc.Open();
                    using var tcmd = tc.CreateCommand();
                    var shopNameKeys = new Dictionary<string, string>(StringComparer.Ordinal);
                    tcmd.CommandText = "SELECT key, english FROM translations WHERE substr(key, 1, length(@p)) = @p ORDER BY key";
                    tcmd.Parameters.AddWithValue("@p", "shop_name_");
                    using (var r = tcmd.ExecuteReader())
                        while (r.Read())
                        {
                            if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
                            var key = r.GetString(0)["shop_name_".Length..];
                            if (key.EndsWith(",P", StringComparison.OrdinalIgnoreCase)) key = key[..^2];
                            var en = r.GetString(1).Trim();
                            if (key.Length > 0 && en.Length > 0) shopNameKeys.TryAdd(key.ToLowerInvariant(), en);
                        }
                    tree.ApplyGameNames(shopNameKeys);
                    string? English(string key)
                    {
                        using var q = tc.CreateCommand();
                        q.CommandText = "SELECT english FROM translations WHERE key = @k OR key = @kp ORDER BY (key = @k) DESC LIMIT 1";
                        q.Parameters.AddWithValue("@k", key);
                        q.Parameters.AddWithValue("@kp", key + ",P");
                        var v = q.ExecuteScalar() as string;
                        return string.IsNullOrWhiteSpace(v) ? null : v;
                    }
                    tree.ApplyAreaLabels(key => English(key.TrimStart('@')));
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) { Log($"訳の表 ({trPath}) を読めなかったので、ゲームの店の名前・案内図の名札は使いません: {ex.Message}"); }
            }
            else Log($"訳の表 ({trPath}) が無いので、ゲームの店の名前・案内図の名札は使いません");
            // 4. 販売データの店名 (アプリと同じ: item は cstone なら location の最後の段、ほかはキーそのまま)
            if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('shop_price_links', 'item_shop_prices')") == 2)
            {
                var itemSource = new Dictionary<string, string>(StringComparer.Ordinal);
                cmd.CommandText = "SELECT DISTINCT location, source FROM item_shop_prices WHERE location <> ''";
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) itemSource.TryAdd(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
                var hasLocationColumn = Count("SELECT COUNT(*) FROM pragma_table_info('shop_price_links') WHERE name = 'location_record'") > 0;
                cmd.CommandText = hasLocationColumn
                    ? "SELECT shop_id, source, price_key, location_record FROM shop_price_links"
                    : "SELECT shop_id, source, price_key, '' FROM shop_price_links";
                using (var r = cmd.ExecuteReader())
                    while (r.Read())
                    {
                        var source = r.GetString(1);
                        var key = r.GetString(2);
                        var dataName = source == "item"
                            ? (itemSource.TryGetValue(key, out var src) && src == "cstone" ? key.Split(" - ")[^1].Trim() : key.Trim())
                            : key.Trim();
                        tree.AddLink(r.GetString(0), source, key, r.GetString(3), dataName);
                    }
                tree.ApplyDataNames();
            }
        }
        // 鍵は「<場所の record>|<GUID>」(店ごと。販売データの店名は場所ごとに違う。例 同じ GUID の ADMIN が「Admin - Seraphim」「Admin - CRU-L1」) と、
        // 同じ GUID の店の名前がどの場所でも同じときだけ「<GUID>」も (StationRouteGraph はルートの record の鍵を先に引く)
        var byGuid = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var s in tree.ShopsByRecord.Values)
        {
            if (!s.Record.StartsWith(ShopTree.ShopPrefix, StringComparison.Ordinal)) continue;
            if (!s.HasFranchiseName && s.NameSource is not (ShopTreeShop.NameFromGame or ShopTreeShop.NameFromLabel or ShopTreeShop.NameFromData)) continue;
            var parts = s.Id.Split('|');
            if (parts.Length < 3 || parts[2].Length == 0 || s.Name.Trim().Length == 0) continue;
            var guid = parts[2].Trim().ToLowerInvariant();
            res.TryAdd($"{parts[0].Trim()}|{guid}", s.Name.Trim());
            if (!byGuid.TryGetValue(guid, out var set)) byGuid[guid] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(s.Name.Trim());
        }
        // 名前の決まらない店のある GUID は、場所によって名前が無いので GUID だけの鍵は作らない
        var unnamed = tree.ShopsByRecord.Values.Where(s => s.Record.StartsWith(ShopTree.ShopPrefix, StringComparison.Ordinal)
                && !s.HasFranchiseName && s.NameSource is not (ShopTreeShop.NameFromGame or ShopTreeShop.NameFromLabel or ShopTreeShop.NameFromData))
            .Select(s => s.Id.Split('|')).Where(p => p.Length >= 3).Select(p => p[2].Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var (guid, set) in byGuid)
            if (set.Count == 1 && !unnamed.Contains(guid)) res.TryAdd(guid, set.First());
        return res;
    }

    // ===== 書き出しの計画 =====

    // 取り込み 1 か所の準備 (StationIndex は呼んだ側の持ち物。ここでは閉じない)
    internal sealed class Prep
    {
        public required StationIndex Index { get; init; }
        public required StationRoot Root { get; init; }
        public required StationTree Tree { get; init; }
        public required List<StationExportJob> Jobs { get; init; }
        public required string TreeHash { get; init; }
        public double Seconds { get; init; }
        // 階・区画の名前に使う訳と星図の場所の名前 (LoadNames)
        public MapNames? Names { get; init; }
    }

    // 木を辿って書き出しの計画を作る (UI に触れない)。要る socpak は StationFloorBuilder.PlanExports で決め、起動の単位は PlanGrouped でまとめ直す
    internal static Prep Prepare(StationIndex index, StationRoot root, Dictionary<ulong, (long Size, uint Crc)> p4k, MapNames? names, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tree = StationTree.Load(index, root, ct: ct);
        var tTree = sw.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();
        var need = StationFloorBuilder.PlanExports(index, tree, kinds: MeshKinds()).SelectMany(j => j.Socpaks).Distinct(StringComparer.Ordinal).ToList();
        var tNeed = sw.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();
        var jobs = PlanGrouped(index, need, p4k, MaxSocpaksPerRun, MaxGeometryBytesPerRun, ct);
        var prep = new Prep { Index = index, Root = root, Tree = tree, Jobs = jobs, TreeHash = TreeHash(tree), Seconds = sw.Elapsed.TotalSeconds, Names = names };
        Log($"「{ShortName(root.Place.Record)}」の書き出しの計画: {prep.Seconds:F1} 秒 (木 {tTree:F1} 秒、要る socpak {tNeed - tTree:F1} 秒、起動のまとめ {prep.Seconds - tNeed:F1} 秒)。" +
            $"インスタンス {tree.Instances.Count}、要る socpak {need.Count} → starbreaker {jobs.Count} 回 (一致 合計 {jobs.Sum(j => j.Matched)}、1 回の一致 最大 {(jobs.Count > 0 ? jobs.Max(j => j.Matched) : 0)})、" +
            $"名前の表 {(names == null ? "なし" : names.Localize == null ? "global.ini なし" : "あり")}");
        return prep;
    }

    // ===== 起動のまとめ方 =====
    // starbreaker の socpak export は 1 回の起動に約 1.9 秒かかる (p4k を開く分。段階 3 の実測) ので、要る socpak を共通の部分文字列 (パスの頭) でまとめて 1 回で書き出す。
    // PATTERN は p4k のパスの部分一致 (大文字小文字を区別しない) なので、パスの頭 "objectcontainers\<…>" を渡すと、その頭で始まる socpak が全部書き出される。
    // 要らない socpak が混ざってもよい (GLB の子 interior_<ファイル名> で分けて、要るものだけ使う。StationFloorBuilder.ExportAsync)。
    // ただし、要る socpak のファイル名 (拡張子なし) が、一致する socpak の中で 1 つだけのときに限る (子の名前で区別できないため)。
    // 1 回の大きさの上限 (両方を満たすようにまとめる。1 つだけで超える socpak は 1 つで書き出す):
    //   MaxSocpaksPerRun: 一致する socpak の数
    //   MaxGeometryBytesPerRun: 一致する socpak の .soc にある CGF (同名に m を付けたものも) の p4k の中の大きさの合計 (重複は 1 つ)。GLB の大きさ・starbreaker のメモリの目安
    // 決め方 (Checkmate の実測。2026-11-01、LIVE、この PC、scratchpad\stage5 の harness の group):
    //   1 つずつ (199 回): GLB 最大 64 MB、starbreaker のメモリ (commit の最大) 1,448 MB。
    //   CGF の大きさの上限 1,000 MB でまとめた (51 回): GLB 最大 301 MB (CGF 996 MB の回) で commit 2,126 MB。
    //   GLB ÷ CGF の大きさは、CGF 10 MB 以上の回で最大 0.68、全部の回で最大 0.80 (小さい回で 1.73 があるが、その回の GLB は数 MB)。
    //   commit は GLB 1 MB あたり約 2.9 MB 増える ((2,126 − 1,448) ÷ (301 − 64))。
    //   船の同時数の決め方 (Ship3DExports.MemoryPerExport) は 1 本 3 GB と見込むので、それを超えないように、
    //   CGF 600 MB (GLB は多くて 0.8 倍の 480 MB → commit 約 1,448 + 2.9 × (480 − 64) ≈ 2,650 MB) を上限にした。
    //   この GLB の大きさは、始める前に確かめる空き (MinFreeBytes 1 GB) にも収まる。
    //   数の上限 (400) は、CGF の無い小さな socpak が大量に一致するときの歯止め (Checkmate の一致の最大は 67)
    // 600 MB → 1,200 MB に変えた (2026-11-01、利用者の決定)。Checkmate を同時 3 本 (inline) で書き出した実測
    //   (scratchpad\par の harness。計ったとき Star Citizen が動いていたので、秒はぶれている。各 1 回):
    //   600 MB: 61 回、全体 179.9 秒、starbreaker の中の時間の合計 534.9 秒、1 本の commit 最大 1,596 MB、GLB 最大 191 MB。
    //   1,200 MB: 36 回、全体 114.7 秒、中の時間の合計 317.9 秒、1 本の commit 最大 2,064 MB、GLB 最大 327 MB。
    //   (参考: 2,400 MB は 32 回・commit 最大 2,177 MB、5,000 MB は 29 回・commit 最大 3,583 MB で 1 本 3 GB を超えるので使わない)
    //   starbreaker は 1 回ごとに Data.p4k を開き DataCore を読み直す (何も一致しないパターンで動かした実測 5.1〜5.7 秒が準備の分。StarBreaker v0.2.2 の cli/src/socpak.rs) ので、回を減らすと中の時間の合計が減る。
    //   1 本の commit の最大が 1 本 3 GB (Ship3DExports.MemoryPerExport) に収まる中で回を減らせる値として決めた。
    //   床・階段のメッシュの結果は 600 MB のときと同じ (socpak を入れる順・回の数・GLB の大きさを除いて比べた)
    public const int MaxSocpaksPerRun = 400;
    public const long MaxGeometryBytesPerRun = 1200L * 1000 * 1000;

    private static readonly ConcurrentDictionaryCache GeometryCache = new();

    // socpak の .soc にある CGF・CGA (と同名に m を付けたもの) の p4k の中の大きさの合計 (重複は 1 つ。p4k に無いものは数えない)。開けなければ 0
    internal static long SocGeometryBytes(StationIndex index, string socpak, Dictionary<ulong, (long Size, uint Crc)> p4k)
    {
        var key = index.P4kPath + "|" + index.P4kWriteUtcTicks + "|" + socpak;
        if (GeometryCache.TryGet(key, out var cached)) return cached;
        long total = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var zip = index.OpenSocpak(socpak);
            if (zip != null)
                foreach (var e in zip.Entries)
                {
                    if (!e.FullName.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)) continue;
                    var t = Encoding.Latin1.GetString(StationIndex.ReadAll(e));
                    foreach (Match m in CgfPathRegex.Matches(t))
                    {
                        var p = Ship3DEstimator.Normalize(m.Value);
                        foreach (var q in new[] { p, p.EndsWith('m') ? p : p + "m" })
                            if (seen.Add(q) && p4k.TryGetValue(Ship3DEstimator.Hash(q), out var v)) total += v.Size;
                    }
                }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { }
        GeometryCache.Set(key, total);
        return total;
    }

    // 書き出しの計画のまとめ直し (貪欲法): 残っている要る socpak のパスの頭 (フォルダの区切り「/」と、名前の区切り「_」の後ろまで) を候補にし、
    //   一致する socpak の数が maxSocpaks 以下のもののうち、残っている要る socpak (ファイル名がその中で 1 つだけのもの) をいちばん多く含むもの
    //   (同じ数なら一致する数の少ないもの) から順に、CGF の大きさの合計が maxGeometry 以下のものを選ぶ。選んだ頭に一致する残りの要る socpak を、その 1 回で書き出す。
    //   どの頭も上限を超える socpak は、それ 1 つで書き出す。並びは決まった順 (同じ入力なら同じ計画)
    internal static List<StationExportJob> PlanGrouped(StationIndex index, IReadOnlyList<string> need, Dictionary<ulong, (long Size, uint Crc)> p4k,
        int maxSocpaks, long maxGeometry, CancellationToken ct = default)
    {
        var keys = index.Entries.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        (int Lo, int Hi) Range(string prefix)
        {
            // キーは小文字なので、頭 + char.MaxValue (U+FFFF) より前までが頭で始まるもの
            return (LowerBound(keys, prefix), LowerBound(keys, prefix + char.MaxValue));
        }
        var uncovered = new SortedSet<string>(need, StringComparer.Ordinal);
        var jobs = new List<StationExportJob>();
        while (uncovered.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var prefixes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in uncovered)
                for (int i = 0; i < k.Length; i++)
                    if (k[i] is '/' or '_') prefixes.Add(k[..(i + 1)]);
            var cands = new List<(string Prefix, List<string> Covered, int Matched)>();
            foreach (var p in prefixes)
            {
                var (lo, hi) = Range(p);
                if (hi - lo > maxSocpaks || hi - lo < 2) continue;
                var stems = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = lo; i < hi; i++)
                {
                    var s = StationFloorBuilder.Stem(keys[i]);
                    stems[s] = stems.GetValueOrDefault(s) + 1;
                }
                // ファイル名が一致するものの中で 1 つだけの要る socpak だけを、この回で書き出す (重なるものは残して、ほかの回で書き出す)
                var covered = new List<string>();
                for (int i = lo; i < hi; i++)
                    if (uncovered.Contains(keys[i]) && stems[StationFloorBuilder.Stem(keys[i])] == 1) covered.Add(keys[i]);
                if (covered.Count < 2) continue;
                cands.Add((p, covered, hi - lo));
            }
            StationExportJob? pick = null;
            foreach (var c in cands.OrderByDescending(c => c.Covered.Count).ThenBy(c => c.Matched).ThenBy(c => c.Prefix, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var (lo, hi) = Range(c.Prefix);
                long geo = 0;
                for (int i = lo; i < hi && geo <= maxGeometry; i++) geo += SocGeometryBytes(index, keys[i], p4k);
                if (geo > maxGeometry) continue;
                pick = new StationExportJob("objectcontainers\\" + c.Prefix.Replace('/', '\\'), c.Covered, c.Matched);
                break;
            }
            if (pick == null)
            {
                // まとめられるものが無い: 残りの最初の 1 つを、それだけで書き出す (StationFloorBuilder.PlanExports の 1 つずつの形と同じ)
                var k = uncovered.Min!;
                pick = new StationExportJob("objectcontainers\\" + k.Replace('/', '\\'), new[] { k }, 1);
            }
            foreach (var s in pick.Socpaks) uncovered.Remove(s);
            jobs.Add(pick);
        }
        return jobs;
    }

    private static int LowerBound(string[] a, string x)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (string.CompareOrdinal(a[mid], x) < 0) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // socpak ごとの CGF の大きさの覚え (並行の取り込みから使う)
    private sealed class ConcurrentDictionaryCache
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _d = new(StringComparer.Ordinal);
        public bool TryGet(string k, out long v) => _d.TryGetValue(k, out v);
        public void Set(string k, long v) { if (_d.Count > 200_000) _d.Clear(); _d[k] = v; }
    }

    // 予想時間 (秒): BaseSeconds + 書き出し + インスタンスの数 × SecondsPerInstance。
    //   書き出し = 起動の回数 × SecondsPerRun × (1 + ParallelSlowdown × (n − 1)) ÷ n。n は同時の本数 = min(parallel, 起動の回数)。
    //   parallel は、その取り込みで使える同時の本数 (始めるときの Ship3DExports.FreeRunSlots: 同時数の上限から、ほかの取り込みの分を引いたもの)。
    // 係数の説明は SecondsPerRun。予想にもとづく
    internal static double EstimateSeconds(Prep p, int parallel)
    {
        var runs = p.Jobs.Count;
        var n = Math.Max(1, Math.Min(parallel, runs));
        var export = runs * SecondsPerRun * (1 + ParallelSlowdown * (n - 1)) / n;
        return BaseSeconds + export + p.Tree.Instances.Count * SecondsPerInstance;
    }

    // 木の形のハッシュ: ルートの全体の変換 (StationIndex.DefaultRule) と、インスタンスごとの番号・親・socpak・entityName・ルートの枠への変換・p4k にあったか、の SHA-256 (16 進)
    internal static string TreeHash(StationTree tree)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        static string X(StationXf x, IFormatProvider f) => string.Join(",", new[] { x.X, x.Y, x.Z, x.Qw, x.Qx, x.Qy, x.Qz }.Select(v => v.ToString("R", f)));
        sb.Append(tree.Root.Place.Record).Append('|').Append(tree.Root.Socpak).Append('|').Append(X(tree.Root.GlobalXf(), inv)).Append('\n');
        foreach (var i in tree.Instances)
            sb.Append(i.Index).Append('|').Append(i.Parent).Append('|').Append(i.Socpak).Append('|').Append(i.EntityName).Append('|')
              .Append(X(i.ToStation, inv)).Append('|').Append(i.Found ? '1' : '0').Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // ===== 床・階・道順 (段階 3・4) =====

    // 段階 3・4 (StationFloorBuilder・StationRouteGraph・StationMapJson) の呼び出しはここ 1 か所だけ (段階 3・4 の関数の形が変わったらここを直す)。
    // 並びは段階 3・4 の確かめ (scratchpad\stage34 の harness の route) と同じ。階の候補に乗り場の高さは足さない (harness の既定)
    //   階の名前・区画の名前は names (英語の global.ini の訳と、星図の場所の名前) で付ける (StationRouteGraph.NameFloors・NameSections。NameSections は Sections の後)
    //   ログ: 段階ごとの秒と、返ってきたものの数 (公開の型からそのまま数えたもの) を 1 行に出す
    // floating: 浮いたもの (StationFloorBuilder.FloatingItems) を入れる先 (null なら作らない)
    internal static string ComposeJson(StationIndex index, StationTree tree, StationMeshSet set, MapNames? names, List<StationFloating>? floating = null)
    {
        var sw = Stopwatch.StartNew();
        var t = new List<string>();
        void Mark(string step) { t.Add($"{step} {sw.Elapsed.TotalSeconds:F1}"); sw.Restart(); }
        var plates = StationFloorBuilder.DropOverheadPlates(StationFloorBuilder.Plates(tree, set), out var droppedPlates);
        // (2026-11-02) 形で判定して表に足した部品の名前を決め直し (歩ける面につながらなかったものは「どれでもない」)、手元の表 (置き場所の stations\station_mesh_kinds_user.json) に書く。
        // 書けなくても取り込みは続ける
        // (2026-11-02) 前から表にある形の名前も、この取り込みで階段・床になれば上書きするので、足した名前が無くても決め直す
        var fixedN = StationFloorBuilder.FinalizeKinds(set);
        if (set.NewKinds > 0 || fixedN > 0)
        {
            try { set.Kinds?.SaveUser(); Log($"部品の名前の表に {set.NewKinds} 件を足し、{fixedN} 件の種類を決め直しました ({set.Kinds?.UserPath})"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log($"部品の名前の表を書けませんでした: {ex.Message}"); }
        }
        // (2026-11-02、利用者の決定) 踊り場の板を階段の一部にする (階を作る前)
        plates = StationFloorBuilder.Landings(tree, plates);
        Mark("板");
        var floors = StationFloorBuilder.Levels(plates);
        Mark("階");
        StationRouteGraph.NameFloors(tree, floors, names?.Localize, names?.PlaceNames, names?.ShopNames);
        Mark("階の名前");
        var sections = StationFloorBuilder.Sections(tree, plates, floors);
        Mark("区画");
        StationRouteGraph.NameSections(tree, sections, names?.Localize, names?.PlaceNames, names?.ShopNames);
        Mark("区画の名前");
        var stairNoEnd = new List<StationFloating>();
        var stairs = StationRouteGraph.Stairs(tree, plates, floors, stairNoEnd);
        // (2026-11-02、利用者の決定「エレベーターとつながった名前優先」) 階段でエレベーターの止まる階とつながる階に、その階の名前を付ける (階段が決まってから)
        StationRouteGraph.NameByStairs(tree, floors, stairs);
        Mark("階段");
        var lines = StationRouteGraph.TransitLines(tree);
        var transit = StationRouteGraph.TransitList(tree, lines, floors);
        Mark("乗り場");
        var cells = StationFloorBuilder.FloorCells(plates, floors);
        Mark("床の升目");
        var graph = StationRouteGraph.Build(tree, floors, sections, stairs, lines, cells);
        Mark("道順の網");
        var json = StationMapJson.Write(index, tree, floors, sections, stairs, transit, graph);
        Mark("JSON");
        if (floating != null) { floating.AddRange(StationFloorBuilder.FloatingItems(set, plates, floors, stairNoEnd)); Mark("浮いたもの"); }
        // 一覧に出す数は、書いた JSON の "listed" を数える (StationMapJson.Write が Listed に面積・使える物の条件を足して決めるため、Listed の数とは違う)
        var (listedFloors, listedSections) = CountListed(json);
        Log($"「{ShortName(tree.Root.Place.Record)}」の床・階・道順の結果: " +
            $"板 {plates.Count} 枚 (乗り場の上の板として除いた {droppedPlates})、" +
            $"階 {floors.Count} (地図の一覧に出す {listedFloors}、名前が付き一覧の候補 (Listed) {floors.Count(f => f.Listed)}、乗り場の高さから作った {floors.Count(f => f.FromTransit)})、" +
            $"区画 {sections.Count} (地図の一覧に出す {listedSections}、名前が店から付いた {sections.Count(s => s.NameSource == "shop")}、箱で代用 {sections.Count(s => s.BoxFallback)})、" +
            $"階段 {stairs.Count}、路線 {lines.Count}、乗り場・行き先 {transit.Count} (" +
            string.Join("・", transit.GroupBy(x => x.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")) + ")、" +
            $"道順の節点 {graph.Nodes.Count}・辺 {graph.Edges.Count}。秒: {string.Join("、", t)}");
        return json;
    }

    // ===== 浮いたもの (2026-11-02、利用者の指示「浮いたものリストを取り込み時にログ出力と DB 格納しとくようにすると将来のデバッグに使える」) =====
    // ログ: 理由ごとの数と、はじめの FloatingLogMax (20) 件。DB: gamedata_cache.db の station_map_floating に、その場所 (record) の行を消してから全部入れる。
    // DB に書けなくても取り込みは続ける (ログに出す)
    public const int FloatingLogMax = 20;
    public const string FloatingTable = "station_map_floating";

    private static void LogAndStoreFloating(string record, List<StationFloating> items)
    {
        var who = $"「{ShortName(record)}」";
        var byReason = items.GroupBy(i => i.Reason.Split(' ')[0]).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
        Log($"{who} 浮いたもの {items.Count} 件 ({string.Join("・", byReason)})" + (items.Count > 0 ? "。はじめの " + Math.Min(items.Count, FloatingLogMax) + " 件:\n" +
            string.Join("\n", items.Take(FloatingLogMax).Select(i => $"  {i.Reason}: {i.Mesh} [{i.Kind}] ({i.X:F1}, {i.Y:F1}, {i.Z:F2})")) : ""));
        try
        {
            using var db = new TrackedSqliteConnection($"Data Source={DetailPanel.DbPath()}");
            db.Open();
            using var tx = db.BeginTransaction();
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $@"CREATE TABLE IF NOT EXISTS {FloatingTable} (
                    record TEXT NOT NULL,
                    mesh TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    x REAL, y REAL, z REAL,
                    reason TEXT NOT NULL,
                    imported_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_{FloatingTable}_record ON {FloatingTable} (record);
                DELETE FROM {FloatingTable} WHERE record = @r;";
                cmd.Parameters.AddWithValue("@r", record);
                cmd.ExecuteNonQuery();
            }
            using (var ins = db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = $"INSERT INTO {FloatingTable} (record, mesh, kind, x, y, z, reason, imported_at) VALUES (@r, @m, @k, @x, @y, @z, @why, @t)";
                var pr = ins.Parameters.Add("@r", Microsoft.Data.Sqlite.SqliteType.Text); var pm = ins.Parameters.Add("@m", Microsoft.Data.Sqlite.SqliteType.Text);
                var pk = ins.Parameters.Add("@k", Microsoft.Data.Sqlite.SqliteType.Text); var px = ins.Parameters.Add("@x", Microsoft.Data.Sqlite.SqliteType.Real);
                var py = ins.Parameters.Add("@y", Microsoft.Data.Sqlite.SqliteType.Real); var pz = ins.Parameters.Add("@z", Microsoft.Data.Sqlite.SqliteType.Real);
                var pw = ins.Parameters.Add("@why", Microsoft.Data.Sqlite.SqliteType.Text); var pt = ins.Parameters.Add("@t", Microsoft.Data.Sqlite.SqliteType.Text);
                var now = DateTime.UtcNow.ToString("o");
                foreach (var i in items)
                {
                    pr.Value = record; pm.Value = i.Mesh; pk.Value = i.Kind;
                    px.Value = double.IsFinite(i.X) ? i.X : DBNull.Value; py.Value = double.IsFinite(i.Y) ? i.Y : DBNull.Value; pz.Value = double.IsFinite(i.Z) ? i.Z : DBNull.Value;
                    pw.Value = i.Reason; pt.Value = now;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
        {
            Log($"{who} 浮いたものを DB ({FloatingTable}) に書けませんでした: {ex.Message}");
        }
    }

    // ログ用: 地図の JSON の floors・sections のうち "listed" が true のものの数 (読めなければ -1)
    private static (int Floors, int Sections) CountListed(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            int Count(string name) => doc.RootElement.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Count(e => e.TryGetProperty("listed", out var l) && l.ValueKind == JsonValueKind.True) : -1;
            return (Count("floors"), Count("sections"));
        }
        catch (JsonException) { return (-1, -1); }
    }

    // .soc の中の CGF のパス (StationFloorBuilder.ScanSocMeshes と同じ形の文字。こちらはパスのまま取る)
    private static readonly Regex CgfPathRegex = new(@"[A-Za-z0-9_/\\.\-]+\.(?:cgf|cga|cgfm|cgam)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // メッシュ名・パスの拡張子とフォルダを外した小文字の名前
    private static string MeshStem(string s)
    {
        s = s.Replace('\\', '/').ToLowerInvariant();
        s = s[(s.LastIndexOf('/') + 1)..];
        foreach (var ext in new[] { ".cgfm", ".cgam", ".cgf", ".cga" })
            if (s.EndsWith(ext, StringComparison.Ordinal)) return s[..^ext.Length];
        return s;
    }

    // 照合用の元ファイル (p4k の中の大きさと記録された CRC。パスは Ship3DEstimator.Normalize の形):
    //   木の全 socpak (objectcontainers/…。p4k に無いものは大きさ -1)、
    //   床・階段に使ったメッシュ (GLB から読んだもの) の CGF: その socpak の .soc にあるパスのうち、名前がメッシュ名と同じもの (p4k に無ければ大きさ -1) と、
    //   同名に m を付けたもの (.cgfm・.cgam。p4k にあれば)。
    //   unmatched: .soc にパスが見つからなかったメッシュ名の数
    internal static List<Ship3DExports.SourceFile> BuildSources(StationIndex index, StationTree tree, StationMeshSet set,
        Dictionary<ulong, (long Size, uint Crc)> p4k, out int unmatched)
    {
        var result = new List<Ship3DExports.SourceFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path, bool required)
        {
            if (!seen.Add(path)) return;
            if (p4k.TryGetValue(Ship3DEstimator.Hash(path), out var v)) result.Add(new Ship3DExports.SourceFile { Path = path, Size = v.Size, Crc = v.Crc });
            else if (required) result.Add(new Ship3DExports.SourceFile { Path = path, Size = -1, Crc = 0 });
        }
        foreach (var sp in tree.Instances.Select(i => i.Socpak).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            Add(Ship3DEstimator.Normalize("data/objectcontainers/" + sp), required: true);
        unmatched = 0;
        foreach (var (socpak, meshes) in set.Meshes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (meshes.Count == 0) continue;
            var names = meshes.Select(m => MeshStem(m.Mesh)).ToHashSet(StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            var paths = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                using var zip = index.OpenSocpak(socpak);
                if (zip != null)
                    foreach (var e in zip.Entries)
                    {
                        if (!e.FullName.EndsWith(".soc", StringComparison.OrdinalIgnoreCase)) continue;
                        var t = Encoding.Latin1.GetString(StationIndex.ReadAll(e));
                        foreach (Match m in CgfPathRegex.Matches(t))
                        {
                            var stem = MeshStem(m.Value);
                            if (!names.Contains(stem)) continue;
                            found.Add(stem);
                            paths.Add(Ship3DEstimator.Normalize(m.Value));
                        }
                    }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { }
            foreach (var p in paths)
            {
                Add(p, required: true);
                if (p.EndsWith(".cgf", StringComparison.Ordinal) || p.EndsWith(".cga", StringComparison.Ordinal)) Add(p + "m", required: false);
            }
            unmatched += names.Count(n => !found.Contains(n));
        }
        return result;
    }

    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // 段階 4 の JSON (StationMapJson.Write。sources の crc は null) の sources を、照合用の元ファイル (crc を埋めたもの) に置き換え、
    // その前に木の形のハッシュ (treeHash) を足す。Write は変えずに、書いた後にここで埋める
    internal static string Finish(string json, IReadOnlyList<Ship3DExports.SourceFile> sources, string treeHash)
    {
        var src = JsonNode.Parse(json)!.AsObject();
        var arr = new JsonArray();
        foreach (var s in sources) arr.Add(new JsonObject { ["path"] = s.Path, ["size"] = s.Size, ["crc"] = s.Crc });
        var res = new JsonObject();
        var added = false;
        foreach (var (k, v) in src.ToList())
        {
            src.Remove(k);
            if (k == "sources")
            {
                res["listedFloors"] = ListedFloorsOf(src);
                res["treeHash"] = treeHash;
                res["sources"] = arr;
                added = true;
            }
            else res[k] = v;
        }
        if (!added) { res["listedFloors"] = ListedFloorsOf(src); res["treeHash"] = treeHash; res["sources"] = arr; }
        return res.ToJsonString(JsonOut);
    }

    // 一覧に出る階の数 (floors のうち listed が true のもの)。頭の欄 "listedFloors" に書き、ReadHeaderOf で読む (形 12。2026-11-01、利用者の決定
    // 「床ができない場所は出さない」: 取り込む前には分からない、書き出した後で床の板が 0 になる場所 (通信アレイ・Rockcracker_Sandbox) を一覧から外すため)
    private static int ListedFloorsOf(JsonObject src) =>
        src["floors"] is JsonArray fl ? fl.Count(f => f is JsonObject o && o["listed"] is JsonValue v && v.TryGetValue<bool>(out var b) && b) : 0;

    // 取り込んだ地図が、一覧に出る階の無い空の地図か (頭の listedFloors が 0。古い形で欄が無ければ false)
    internal static bool IsEmptyMap(string record) => ReadOurHeader(ShortName(record)) is { ListedFloors: 0 };

    // 書き出しの記録 (ログ・確かめに使う)
    internal sealed record ExportStats(int Runs, int Socpaks, int Missing, long TotalGlbBytes, long MaxGlbBytes, double ExportSeconds, double ComposeSeconds,
        int Sources, int UnmatchedMeshes, long JsonBytes);

    // 書き出して JSON を作る (UI に触れない)。GLB は workDir に作り、読んだらすぐ消す。starbreaker の失敗が 1 回でもあれば失敗 (半端な地図を残さない)。
    // afterExport は StarBreaker の部分が終わったとき (同時数の枠を空ける)。progress は (済んだ起動の数, 全部の数)
    // onStage はログ用 (今の段階の名前を呼んだ側に知らせる。失敗・取り消しのときにどこで止まったかを出す)
    // tryExtra・releaseExtra は starbreaker を並行に足すときの問い合わせと返し (StationFloorBuilder.ExportAsync の説明)。null なら 1 本ずつ
    internal static async Task<(string Json, ExportStats Stats)> BuildJsonAsync(Prep prep, string exe, Ship3DExports.P4kStamp stamp, string workDir,
        Action<int, int>? progress, Action? afterExport, CancellationToken ct, Action<string>? onStage = null,
        Func<Task<bool>>? tryExtra = null, Action? releaseExtra = null)
    {
        var who = $"「{ShortName(prep.Root.Place.Record)}」";
        // ログ用: starbreaker の回ごとの記録。1 回ずつは出さず (Checkmate で 60 回ある)、終わった回の数で RunLogBlock 回ごとに 1 行にまとめる。失敗した回は 1 回ずつ出す。
        //   回は並行に動くので、終わる順は番号の順とは限らない。回の番号は GLB の名前 (ExportAsync が付ける station_<番号 4 桁>.glb。番号は prep.Jobs の添え字) から取る。
        //   GLB の大きさは、run が返った直後 (ExportAsync が読んで消す前) に見る。記録は並行の回から触るので logLock で守る
        var totalRuns = prep.Jobs.Count;
        var logLock = new object();
        int completed = 0, blockFrom = 1, blockFails = 0, maxActive = 0, blockMaxActive = 0;
        double blockRun = 0, sumRun = 0, slowSec = -1;
        long blockMaxGlb = -1;
        string slowText = "";
        var active = new SortedSet<int>();
        async Task<(int ExitCode, string StderrTail)> Run(string args, CancellationToken c)
        {
            var m = GlbNoRegex.Match(args);
            var no = m.Success ? int.Parse(m.Groups[1].Value) + 1 : 0;
            var j = no >= 1 && no <= prep.Jobs.Count ? prep.Jobs[no - 1] : null;
            var what = j != null ? $"{j.Pattern} (要る socpak {j.Socpaks.Count}、一致 {j.Matched})" : "";
            lock (logLock)
            {
                active.Add(no);
                maxActive = Math.Max(maxActive, active.Count);
                blockMaxActive = Math.Max(blockMaxActive, active.Count);
                onStage?.Invoke($"starbreaker {string.Join("・", active)} 回目 / 全 {totalRuns} 回 (同時 {active.Count} 本)");
            }
            var sw1 = Stopwatch.StartNew();
            (int Code, string Tail) r;
            try { r = await StarBreakerRunner.RunAsync(exe, args, null, null, c); }
            finally
            {
                lock (logLock)
                {
                    active.Remove(no);
                    if (active.Count > 0) onStage?.Invoke($"starbreaker {string.Join("・", active)} 回目 / 全 {totalRuns} 回 (同時 {active.Count} 本)");
                    else onStage?.Invoke($"starbreaker {no} 回目の GLB の読み込み");
                }
            }
            var sec = sw1.Elapsed.TotalSeconds;
            long len = -1;
            try { var glb = Path.Combine(workDir, $"station_{no - 1:D4}.glb"); if (File.Exists(glb)) len = new FileInfo(glb).Length; } catch { }
            lock (logLock)
            {
                completed++;
                blockRun += sec;
                sumRun += sec;
                blockMaxGlb = Math.Max(blockMaxGlb, len);
                if (sec > slowSec) { slowSec = sec; slowText = $"{no} 回目 {sec:F1} 秒 {what}"; }
                if (r.Code != 0 || len < 0)
                {
                    blockFails++;
                    Log($"{who} starbreaker {no} / {totalRuns} 回目が失敗しました: 終了コード {r.Code}、{sec:F1} 秒、GLB {(len < 0 ? "なし" : Ship3DExports.FormatBytes(len))}、{what}。" +
                        $"標準エラーの末尾: {TailLines(r.Tail, 5)}");
                }
                if (completed % RunLogBlock == 0 || completed == totalRuns)
                {
                    Log($"{who} starbreaker が {blockFrom}〜{completed} / {totalRuns} 回終わりました (終わった数): starbreaker の時間の足し合わせ {blockRun:F1} 秒、" +
                        $"同時に動いた最大 {blockMaxActive} 本、GLB 最大 {(blockMaxGlb < 0 ? "なし" : Ship3DExports.FormatBytes(blockMaxGlb))}、失敗 {blockFails} 回。ここまででいちばん長い回: {slowText}");
                    blockFrom = completed + 1;
                    blockRun = 0;
                    blockMaxGlb = -1;
                    blockFails = 0;
                    blockMaxActive = active.Count;
                }
            }
            return r;
        }
        Log($"{who} starbreaker の書き出しを始めます: 全 {totalRuns} 回 (終わった数で {RunLogBlock} 回ごとに 1 行で出します。{(tryExtra != null ? "同時数の上限まで並行に動かします" : "1 本ずつ")})");
        onStage?.Invoke("starbreaker の書き出し");
        var set = await StationFloorBuilder.ExportAsync(Run, stamp.Path, prep.Jobs, workDir, progress, ct, tryExtra, releaseExtra, MeshKinds());
        Log($"{who} starbreaker の書き出しが終わりました: {set.Runs} / {totalRuns} 回、{set.Seconds:F1} 秒 (starbreaker の時間の足し合わせ {sumRun:F1} 秒、同時に動いた最大 {maxActive} 本)、" +
            $"GLB 合計 {Ship3DExports.FormatBytes(set.TotalGlbBytes)}・最大 {Ship3DExports.FormatBytes(set.MaxGlbBytes)}、床・階段のメッシュのある socpak {set.Meshes.Count(m => m.Value.Count > 0)} / {set.Meshes.Count}、" +
            $"GLB に出てこなかった socpak {set.Missing.Count}、失敗 {set.Errors.Count} 回");
        if (set.Errors.Count > 0)
        {
            Log($"「{prep.Root.Place.Record}」の床の書き出しで starbreaker が {set.Errors.Count} 回失敗しました:\n{string.Join("\n", set.Errors)}");
            throw new InvalidOperationException($"starbreaker が {set.Errors.Count} 回失敗しました: {set.Errors[0]}");
        }
        afterExport?.Invoke();
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        onStage?.Invoke("Data.p4k の中央ディレクトリ");
        var p4k = await Ship3DP4kIndex.GetAsync(stamp);
        onStage?.Invoke("床・階・道順の作成 (ComposeJson)");
        Log($"{who} 床・階・道順の作成を始めます");
        var floating = new List<StationFloating>();
        var json = ComposeJson(prep.Index, prep.Tree, set, prep.Names, floating);
        LogAndStoreFloating(prep.Root.Place.Record, floating);
        var tCompose = sw.Elapsed.TotalSeconds;
        ct.ThrowIfCancellationRequested();
        onStage?.Invoke("照合用の元ファイル (BuildSources)");
        var sources = BuildSources(prep.Index, prep.Tree, set, p4k, out var unmatched);
        var tSources = sw.Elapsed.TotalSeconds;
        onStage?.Invoke("JSON の仕上げ (Finish)");
        var final = Finish(json, sources, prep.TreeHash);
        Log($"{who} 床・階・道順の作成が終わりました: 中央ディレクトリと床・道順 {tCompose:F1} 秒、照合用の元ファイル {sources.Count} 個 {tSources - tCompose:F1} 秒、" +
            $"仕上げ {sw.Elapsed.TotalSeconds - tSources:F1} 秒 (JSON {Ship3DExports.FormatBytes(Encoding.UTF8.GetByteCount(final))})");
        return (final, new ExportStats(set.Runs, prep.Jobs.Sum(j => j.Socpaks.Count), set.Missing.Count, set.TotalGlbBytes, set.MaxGlbBytes, set.Seconds,
            sw.Elapsed.TotalSeconds, sources.Count, unmatched, Encoding.UTF8.GetByteCount(final)));
    }

    // ログ用: starbreaker の回を何回ごとに 1 行にまとめるか (BuildJsonAsync)
    private const int RunLogBlock = 10;
    // ログ用: starbreaker の引数の GLB の名前 (station_<番号 4 桁>.glb) から回の番号を取る
    private static readonly Regex GlbNoRegex = new(@"station_(\d{4})\.glb", RegexOptions.Compiled);

    // ログ用: 標準エラーの末尾 n 行を 1 行にしたもの
    private static string TailLines(string text, int n)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - n)));
    }

    // 置き場所のドライブの空きが MinFreeBytes 以上か。足りなければ理由
    private static string? FreeSpaceProblem()
    {
        var d = Ship3DExports.DriveOf(Ship3DExports.Folder);
        if (d is { } x && x.Free < MinFreeBytes)
            return $"置き場所のドライブ {x.Root} の空きが {Ship3DExports.FormatBytes(x.Free)} しかありません (途中の作業に {Ship3DExports.FormatBytes(MinFreeBytes)} 以上要ります)";
        return null;
    }

    // ===== 1 か所の［読み込み］ =====

    // ステーションごとの取り込み (短い名前 → 取り込み)。同じステーションは合流する
    private readonly Dictionary<string, Ship3DJob> _byShort = new(StringComparer.OrdinalIgnoreCase);

    // ［読み込み］: そのステーションの地図をゲームデータから作る (始める前に予想時間を出して聞く)。終わったら JSON のパス。
    // すでに最新の地図があればそのパス (Data.p4k が変わっていれば先に照合する)。同じステーションの取り込みが動いていれば合流する。
    // 取り消し・失敗・ゲームが見つからないときは null (知らせは出し済み)
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
            Log($"「{name}」({shortName}) の［読み込み］: 同じステーションの取り込みが動いているので、それに合流します ({running.LogTag})");
            try { return await running.Task; }
            catch { return null; }
        }
        if (await GetCachedPathAsync(record) is { } cached)
        {
            Log($"「{name}」({shortName}) の［読み込み］: 使える地図があるので取り込みません ({cached})");
            return cached;
        }
        if (ex.CpuHigh)
        {
            ShowError(owner, $"CPU 使用率が {ex.CpuPercent:F0}% と高いため、取り込みを始めません。90% 未満に下がってからもう一度押してください。");
            return null;
        }
        if (HasForeignFile(shortName))
        {
            ShowError(owner, $"置き場所に同じ名前のファイル (stations\\{shortName}.json) があり、このアプリが書いたものではないので取り込みません。置き場所を変えるか、そのファイルを移してください。");
            return null;
        }
        if (FreeSpaceProblem() is { } free)
        {
            ShowError(owner, free + "。");
            return null;
        }
        if (_byShort.TryGetValue(shortName, out running) && running.Task != null)
        {
            Log($"「{name}」({shortName}) の［読み込み］: 照合の間に同じステーションの取り込みが始まったので、それに合流します ({running.LogTag})");
            try { return await running.Task; }
            catch { return null; }
        }
        var job = new Ship3DJob(record, KeyOf(shortName), string.IsNullOrWhiteSpace(name) ? shortName : name, batch: null) { Kind = Ship3DJobKind.Station };
        Ship3DExports.LogJob(job, $"取り込みを始めます (単体、record {record}、Data.p4k {stamp.Path})");
        AddJob(job, shortName);
        job.Task = RunSingleAsync(job, shortName, stamp, owner);
        return await job.Task;
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
        var total = Stopwatch.StartNew();   // ログ用: 取り込み全体の経過
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
            var workDir = DetailPanel.WorkDir();
            var record = job.Record;
            Ship3DExports.LogJob(job, "計画を始めます (星図の場所の表 → ゲームデータの索引 → ルート探し → 木・書き出しの計画)");
            var prep = await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                job.LogStage = "計画: 星図の場所の表を読む";
                var placeRows = LoadPlaces(dbPath);
                var places = placeRows.Select(p => p.Place).ToList();
                var tPlaces = sw.Elapsed.TotalSeconds;
                job.LogStage = "計画: ゲームデータの索引 (StationIndex.Build)";
                index = StationIndex.Build(stamp.Path, ct);
                var tIndex = sw.Elapsed.TotalSeconds;
                job.LogStage = "計画: ルート探し (FindRoots)";
                var search = index.FindRoots(places, ct: ct);
                var tRoots = sw.Elapsed.TotalSeconds;
                Ship3DExports.LogJob(job, $"計画: 星図の場所 {places.Count} 件 {tPlaces:F1} 秒、索引 {tIndex - tPlaces:F1} 秒、ルート探し {tRoots - tIndex:F1} 秒 (見つかったルート {search.Roots.Count} 件)");
                if (!search.Roots.TryGetValue(record, out var root))
                    throw new InvalidOperationException("ゲームデータの中にこのステーションが見つかりませんでした");
                job.LogStage = "計画: Data.p4k の中央ディレクトリ";
                var p4k = await Ship3DP4kIndex.GetAsync(stamp);
                job.LogStage = "計画: 名前の表 (global.ini・店)";
                var names = LoadNames(placeRows, workDir, dbPath);
                job.LogStage = "計画: 木・書き出しの計画 (Prepare)";
                return Prepare(index, root, p4k, names, ct);
            }, ct);
            // 確認の窓に出す予想: 今使える同時の本数で見込む (始めるときに ExportAsync で見込み直す)
            var slotsNow = Ship3DExports.Instance.FreeRunSlots();
            job.EstimatedSeconds = EstimateSeconds(prep, slotsNow);
            Ship3DExports.LogJob(job, $"計画ができました: 全体 {total.Elapsed.TotalSeconds:F1} 秒。starbreaker {prep.Jobs.Count} 回の予定、予想 {job.EstimatedSeconds:F0} 秒 (使える同時の本数 {slotsNow} 本で見込んだもの)");
            job.LogStage = "取り込むかの確認";
            job.SetPhase(Ship3DJobPhase.Estimating, "取り込むか確かめています");
            var msg = $"「{job.Name}」の地図 (階ごとの床・道順) をゲームデータから作ります。\n\n" +
                      $"予想時間: 約 {Ship3DExports.FormatSeconds(job.EstimatedSeconds)} (StarBreaker を {prep.Jobs.Count} 回動かします)\n\n" +
                      "取り込みは裏で行います (その間もほかの操作・ほかの取り込みができます)。始めますか？";
            var ok = owner != null && owner.IsLoaded
                ? MessageBox.Show(owner, msg, "ステーションの地図", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                : MessageBox.Show(msg, "ステーションの地図", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK)
            {
                Ship3DExports.LogJob(job, "確認の窓で［キャンセル］が押されたので取り込みません");
                job.SetPhase(Ship3DJobPhase.Cancelled, "");
                return null;
            }
            await Ship3DExports.Instance.WaitTurnAsync(job, ct);
            return await ExportAsync(job, shortName, extractor.StarBreakerExe, stamp, prep);
        }
        catch (OperationCanceledException)
        {
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            Log($"「{job.Name}」({shortName}) の取り込みを取り消しました (段階: {job.LogStage}、経過 {total.Elapsed.TotalSeconds:F1} 秒)");
            return null;
        }
        catch (Exception e)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            Ship3DExports.LogJob(job, $"失敗しました (段階: {job.LogStage}、経過 {total.Elapsed.TotalSeconds:F1} 秒): {Ship3DExports.Describe(e)}");
            ShowError(owner, $"「{job.Name}」({shortName}) の地図を作れませんでした: {e.Message}");
            return null;
        }
        finally
        {
            index?.Dispose();
            RemoveJob(job, shortName);
        }
    }

    // 順番が来た取り込みを動かす (WaitTurnAsync の後。同時数の枠に入っている)。終わったら JSON のパス。
    //   作業用のフォルダに書いてから名前を変える。同じ名前でこのアプリのものではないファイルがあれば上書きしない。
    //   StarBreaker の部分が終わったら同時数の枠を空ける (EndRun)。まとめての中でなければ、最大容量を超えていれば古いものから消す
    private async Task<string> ExportAsync(Ship3DJob job, string shortName, string exe, Ship3DExports.P4kStamp stamp, Prep prep)
    {
        var ex = Ship3DExports.Instance;
        var ct = job.Cts.Token;
        var work = Path.Combine(Ship3DExports.WorkFolder, $"station_{shortName}_{Guid.NewGuid():N}");
        string? tmp = null;
        job.SetPhase(Ship3DJobPhase.Exporting, "");
        job.Watch.Restart();
        try
        {
            job.LogStage = "書き出しの前の空きの確認";
            if (FreeSpaceProblem() is { } free) throw new InvalidOperationException(free);
            // 予想時間を、始めるときに使える同時の本数 (同時数の上限から、ほかの取り込みの分を引いたもの) で見込み直す
            var slots = ex.FreeRunSlots(job);
            var estimate = EstimateSeconds(prep, slots);
            job.EstimatedSeconds = estimate;
            Ship3DExports.LogJob(job, $"書き出しを始めます ({job.LogTag}): starbreaker {prep.Jobs.Count} 回 (要る socpak {prep.Jobs.Sum(j => j.Socpaks.Count)}、一致 合計 {prep.Jobs.Sum(j => j.Matched)})、" +
                $"インスタンス {prep.Tree.Instances.Count}、予想 {estimate:F0} 秒 (使える同時の本数 {slots} 本で見込んだもの)。作業用のフォルダ {work}");
            Directory.CreateDirectory(Folder);
            Directory.CreateDirectory(Ship3DExports.WorkFolder);
            var dispatcher = Application.Current?.Dispatcher;
            var post = BaseSeconds + prep.Tree.Instances.Count * SecondsPerInstance;
            // 起動ごとの進み具合で予想時間を見直す (済んだ起動の 1 回あたりの実際の秒で残りを見込む)
            void Progress(int done, int total)
            {
                if (done <= 0 || total <= 0) return;
                var e = job.Watch.Elapsed.TotalSeconds;
                var est = Math.Max(e / done * total + post, e);
                if (dispatcher != null) dispatcher.BeginInvoke(() => job.EstimatedSeconds = est);
                else job.EstimatedSeconds = est;
            }
            // starbreaker の並行: この取り込みの枠 1 本のほかに、同時数の上限 (Ship3DExports.MaxParallelExports。ほかの取り込みの分を引いたもの) まで足す。
            //   足すかは Ship3DExports.TryAcquireExtraRun (UI スレッド。CPU 使用率 90% 以上・待っている取り込みがあるときは足さない) で決め、
            //   その前に置き場所のドライブの空きが MinFreeBytes 以上あるかを見る (並行の分だけ途中の GLB が同時に置かれるため)
            Func<Task<bool>>? tryExtra = dispatcher == null ? null : async () =>
            {
                if (FreeSpaceProblem() != null) return false;
                return await dispatcher.InvokeAsync(() => ex.TryAcquireExtraRun());
            };
            Action? releaseExtra = dispatcher == null ? null : () => dispatcher.BeginInvoke(() => ex.ReleaseExtraRun());
            var (json, stats) = await Task.Run(() => BuildJsonAsync(prep, exe, stamp, work, Progress,
                () => { if (dispatcher != null) dispatcher.BeginInvoke(() => ex.EndRun(job)); else ex.EndRun(job); }, ct, s => job.LogStage = s,
                tryExtra, releaseExtra), ct);

            job.LogStage = "保存";
            var swSave = Stopwatch.StartNew();
            tmp = Ship3DExports.NewWorkFile("station_" + shortName, ".json");
            await File.WriteAllTextAsync(tmp, json, new UTF8Encoding(false), ct);
            if (HasForeignFile(shortName))
                throw new InvalidOperationException($"置き場所に同じ名前のファイル (stations\\{shortName}.json) があり、このアプリが書いたものではないので上書きしません。置き場所を変えるか、そのファイルを移してください");
            var final = JsonPath(shortName);
            File.Move(tmp, final, overwrite: true);
            tmp = null;
            _pendingDelete.Remove(shortName);   // 取り込み直したので、前のものを後で消す予定は取りやめ
            job.Watch.Stop();
            Ship3DExports.LogJob(job, $"保存しました: {swSave.Elapsed.TotalSeconds:F1} 秒、{final}");
            Log($"「{job.Name}」({shortName}) の地図を作りました: {job.Watch.Elapsed.TotalSeconds:F1} 秒 (予想 {estimate:F0} 秒 (同時 {slots} 本で見込んだもの)、計画 {prep.Seconds:F1} 秒)、" +
                $"{Ship3DExports.FormatBytes(stats.JsonBytes)}。インスタンス {prep.Tree.Instances.Count}、starbreaker {stats.Runs} 回 {stats.ExportSeconds:F1} 秒 " +
                $"(socpak {stats.Socpaks}、GLB 合計 {Ship3DExports.FormatBytes(stats.TotalGlbBytes)}、最大 {Ship3DExports.FormatBytes(stats.MaxGlbBytes)})、" +
                $"床・道順 {stats.ComposeSeconds:F1} 秒、照合用の元ファイル {stats.Sources} 個" + (stats.UnmatchedMeshes > 0 ? $" (パスの見つからないメッシュ名 {stats.UnmatchedMeshes})" : "") +
                $"。GLB に出てこなかった socpak {stats.Missing} ({job.LogTag})");
            job.SetPhase(Ship3DJobPhase.Done, "");
            ex.EndRun(job);
            // まとめて取り込むの中では古いものを消さない (船のまとめてと同じ)
            if (!job.InBatch) ex.EnforceLimit(justExported: job.Short);
            RaiseStateChanged();
            Exported?.Invoke(shortName);
            return final;
        }
        finally
        {
            job.Watch.Stop();
            ex.EndRun(job);
            if (tmp != null) try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { }
        }
    }

    // ===== まとめて取り込む =====

    private bool _batchActive;
    private string _batchText = "";
    private CancellationTokenSource? _batchCts;
    public bool BatchActive => _batchActive;
    // まとめて取り込むの今の段階 (進み具合の欄では船のまとめての段階と「 / 」でつないで出る。取り消しは欄の［まとめて取り消し］(Ship3DExports.CancelBatch))
    public string BatchText { get => _batchText; private set { _batchText = value; Changed(nameof(BatchText)); Ship3DExports.Instance.NotifyStationBatchChanged(); } }

    public void CancelBatch() { try { _batchCts?.Cancel(); } catch (ObjectDisposedException) { } }

    private void SetBatchActive(bool active)
    {
        _batchActive = active;
        Changed(nameof(BatchActive));
        // 進み具合の欄 (Ship3DProgressPanel) の［まとめて取り消し］の行は Ship3DExports.BatchActive・BatchText に結んであるので知らせる
        Ship3DExports.Instance.NotifyStationBatchChanged();
        RaiseStateChanged();
    }

    // まとめて取り込む: 星図の場所のうちステーション・都市 (Manmade 系・LandingZone) で、ゲームデータの中にルートが見つかったものを、星系・名前の順に。
    //   すでに最新の地図があるもの (Data.p4k が変わっていれば照合して同じだったもの)・今取り込み中のもの・同じ名前のこのアプリのものではないファイルがあるものは飛ばす。
    //   予想 (インスタンスの数 × BatchSecondsPerInstance 秒・× BytesPerInstance バイト。Checkmate の実測から) の大きさの合計が
    //   「最大容量 − 今の使用量 (船とステーションの合計)」を超える手前で止める (古いものを消して入れ替えない)。何か所・約何分・約何 MB かを聞いてから、
    //   並べて順に始める (同時数は Ship3DExports の待ち行列)。書き出しの計画は順番が来てから作る
    public async Task RunBatchAsync(Window? owner)
    {
        var ex = Ship3DExports.Instance;
        if (_batchActive) { ShowError(owner, "マップをまとめて取り込むはすでに動いています。", "ステーションのまとめて取り込むはすでに動いています。"); return; }
        if (ex.CpuHigh)
        {
            ShowError(owner, $"CPU 使用率が {ex.CpuPercent:F0}% と高いため、取り込みを始めません。90% 未満に下がってからもう一度押してください。");
            return;
        }
        var stamp = Ship3DExports.CurrentStamp();
        if (stamp == null) { ShowError(owner, "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"); return; }
        if (FreeSpaceProblem() is { } free) { ShowError(owner, free + "。"); return; }

        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        SetBatchActive(true);
        var failures = new List<string>();
        StationIndex? index = null;
        // ログ用: 全体の経過・今の段階・飛ばしたものの名前 (理由ごと)
        var swBatch = Stopwatch.StartNew();
        var batchStage = "StarBreaker の用意";
        var skippedFresh = new List<string>();
        var skippedRunning = new List<string>();
        var skippedForeign = new List<string>();
        var notTakenNames = new List<string>();
        var batchJobs = new List<Ship3DJob>();
        // 対象の record の一覧 (BatchTargetRecord に書く。設定タブのボタンの「全部取り込み済み」の判定に使う)
        List<string>? batchTargets = null;
        Log($"ステーションのまとめて取り込むを始めます (Data.p4k {stamp.Path}、最大容量 {Ship3DExports.MaxMb:N0} MB、" +
            $"今の使用量 (船とステーションの合計) {Ship3DExports.FormatBytes(Ship3DExports.CacheBytes() + CacheBytes())})");
        try
        {
            var extractor = new GameDataExtractor(DetailPanel.WorkDir());
            BatchText = "StarBreaker を用意しています...";
            try { await extractor.EnsureStarBreakerAsync(); }
            catch (Exception e) { Log($"ステーションのまとめて取り込む: StarBreaker を用意できませんでした: {Ship3DExports.Describe(e)}"); ShowError(owner, $"StarBreaker を用意できませんでした: {e.Message}"); return; }

            BatchText = "マップをまとめて取り込む: ゲームデータを調べています...";
            batchStage = "ゲームデータを調べる (場所の表・索引・ルート探し)";
            var swScan = Stopwatch.StartNew();
            var dbPath = DetailPanel.DbPath();
            var workDir = DetailPanel.WorkDir();
            MapNames? mapNames = null;
            var (names, roots) = await Task.Run(() =>
            {
                var places = LoadPlaces(dbPath);
                mapNames = LoadNames(places, workDir, dbPath);
                index = StationIndex.Build(stamp.Path, ct);
                var search = index.FindRoots(places.Select(p => p.Place).ToList(), ct: ct);
                var nm = places.GroupBy(p => p.Place.Record).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
                // まとめての取り込みは星図の場所のうちステーション・都市 (Manmade 系・LandingZone) だけ (FindRoots は前哨基地・小惑星のルートも返すので絞る。
                // 星図に載っていない場所 (IsUnlisted) も外す)
                // 床のメッシュの無い場所 (取り込んでも空の地図になる。StationMapWindow.HasFloorMesh。ハーネスで、ステーション・都市では TransitPoint_Glaciem_Alpha・Bravo・Charlie の 3 か所) も外す (2026-11-01、利用者の決定。HideNoFloorPlaces を false にすれば入る)
                var floorCache = new Dictionary<string, bool>(StringComparer.Ordinal);
                return (nm, search.Roots.Values.Where(r => r.Place.IsStation && !r.Place.IsUnlisted && (!StationMapWindow.HideNoFloorPlaces || StationMapWindow.HasFloorMesh(index, r, floorCache))).OrderBy(r => r.Place.System, StringComparer.Ordinal).ThenBy(r => r.Place.Record, StringComparer.Ordinal).ToList());
            }, ct);
            string NameOf(StationRoot r) => names.TryGetValue(r.Place.Record, out var n) && n.Length > 0 ? n : ShortName(r.Place.Record);
            Log($"ステーションのまとめて取り込む: ゲームデータを調べました ({swScan.Elapsed.TotalSeconds:F1} 秒): 星図の場所 (record ごと) {names.Count} 件、ルートが見つかったもの {roots.Count} か所");
            batchTargets = roots.Select(r => r.Place.Record).ToList();
            BatchTargetRecord.Write(BatchTargetRecord.Maps, stamp, batchTargets, StateOf);

            // 1. 飛ばすものを決める (照合が要るものは順に照合する)
            batchStage = "飛ばすものを決める (照合)";
            var swSkip = Stopwatch.StartNew();
            int skipFresh = 0, skipRunning = 0, skipForeign = 0, notTaken = 0;
            var candidates = new List<StationRoot>();
            for (int i = 0; i < roots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var r = roots[i];
                var sn = ShortName(r.Place.Record);
                if (_byShort.ContainsKey(sn)) { skipRunning++; skippedRunning.Add(sn); continue; }
                var state = StateOf(r.Place.Record);
                if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying)
                {
                    BatchText = $"マップをまとめて取り込む: 照合中 {i + 1} / {roots.Count} か所目 ({NameOf(r)})";
                    if (await VerifyAsync(r.Place.Record)) { skipFresh++; skippedFresh.Add(sn); continue; }
                }
                else if (state == Ship3DCacheState.Fresh) { skipFresh++; skippedFresh.Add(sn); continue; }
                if (HasForeignFile(sn)) { skipForeign++; skippedForeign.Add(sn); continue; }
                candidates.Add(r);
            }
            Log($"ステーションのまとめて取り込む: 飛ばすものを決めました ({swSkip.Elapsed.TotalSeconds:F1} 秒): 全 {roots.Count} か所のうち、見積もるもの {candidates.Count} か所、" +
                $"最新の地図がある {skipFresh} か所、取り込み中 {skipRunning} か所、このアプリのものではない同名のファイル {skipForeign} か所");

            // 2. 見積もり: 木の形だけ辿ってインスタンスの数を数える
            batchStage = "見積もり";
            var swEst = Stopwatch.StartNew();
            var selected = new List<(StationRoot Root, double Seconds, double Bytes)>();
            double sumBytes = 0, sumSeconds = 0;
            for (int k = 0; k < candidates.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                var r = candidates[k];
                BatchText = $"マップをまとめて取り込む: 見積もり中 {k + 1} / {candidates.Count} か所目 ({NameOf(r)})";
                int count;
                try { count = await Task.Run(() => StationTree.Load(index!, r, contents: false, ct: ct).Instances.Count, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    failures.Add($"{NameOf(r)} ({ShortName(r.Place.Record)}): 見積もれませんでした: {e.Message}");
                    Log($"ステーションのまとめて取り込む: 「{NameOf(r)}」を見積もれませんでした: {Ship3DExports.Describe(e)}");
                    continue;
                }
                var bytes = count * BytesPerInstance;
                if (sumBytes + bytes > Ship3DExports.MaxMb * 1024.0 * 1024 - Ship3DExports.CacheBytes() - CacheBytes())
                {
                    notTaken = candidates.Count - k;
                    notTakenNames.AddRange(candidates.Skip(k).Select(x => ShortName(x.Place.Record)));
                    break;
                }
                var sec = BaseSeconds + count * BatchSecondsPerInstance;
                sumBytes += bytes;
                sumSeconds += sec;
                selected.Add((r, sec, bytes));
            }
            Log($"ステーションのまとめて取り込む: 見積もりが終わりました ({swEst.Elapsed.TotalSeconds:F1} 秒): 取り込むもの {selected.Count} か所、" +
                $"予想 合計 {sumSeconds:F0} 秒・約 {Ship3DExports.FormatBytes(sumBytes)}、最大容量を超えるので取り込まない {notTaken} か所、見積もれなかった {failures.Count} か所");
            batchStage = "始めるかの確認";

            var used = Ship3DExports.CacheBytes() + CacheBytes();
            var parallel = Ship3DExports.MaxParallelExports().Limit;
            var head = $"マップをまとめて取り込む ({roots.Count} か所のうち)\n\n";
            var info = (skipFresh > 0 ? $"最新の地図があるので飛ばす: {skipFresh} か所\n" : "") +
                       (skipRunning > 0 ? $"今取り込み中なので飛ばす: {skipRunning} か所\n" : "") +
                       (skipForeign > 0 ? $"置き場所に同じ名前のこのアプリのものではないファイルがあるので飛ばす: {skipForeign} か所\n" : "") +
                       (notTaken > 0 ? $"最大容量 ({Ship3DExports.MaxMb:N0} MB。今の使用量 (船とステーションの合計) {Ship3DExports.FormatBytes(used)}、残り {Ship3DExports.FormatBytes(Math.Max(0, Ship3DExports.MaxMb * 1024.0 * 1024 - used))}) を超えるので取り込まない: {notTaken} か所\n" +
                                       "　→ 全部取り込むには、設定タブの「3D モデル」の最大容量を上げてください\n" : "") +
                       (failures.Count > 0 ? $"見積もれなかった: {failures.Count} か所 (アプリのログに記録)\n" : "");
            if (selected.Count == 0)
            {
                Log("ステーションのまとめて取り込む: 取り込むものがないので終わります。" +
                    Ship3DExports.LogSkipLists(skippedFresh, skippedRunning, skippedForeign, notTakenNames, failures));
                MessageBox.Show(head + "取り込むステーションがありません。\n\n" + info, "ステーションの地図", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var minutes = sumSeconds / Math.Max(1, parallel) / 60;
            var ask = head +
                      $"取り込むステーション・都市: {selected.Count} か所\n" +
                      $"予想時間: 約 {Math.Max(1, Math.Ceiling(minutes)):F0} 分 (1 か所ずつなら合計 約 {Math.Ceiling(sumSeconds / 60):F0} 分。同時 {parallel} 本までで見込んだもの)\n" +
                      $"予想サイズ: 約 {Math.Max(1, Math.Ceiling(sumBytes / (1024.0 * 1024))):F0} MB\n\n" + info +
                      "\n始めますか？ (進み具合は［詳細］タブと設定タブの 3D モデルの欄に出ます)";
            var ok = owner != null && owner.IsLoaded
                ? MessageBox.Show(owner, ask, "ステーションの地図", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                : MessageBox.Show(ask, "ステーションの地図", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK)
            {
                Log("ステーションのまとめて取り込む: 確認の窓で［キャンセル］が押されたので取り込みません");
                return;
            }

            batchStage = "取り込み";
            BatchText = $"マップをまとめて取り込む: {selected.Count} か所";
            var tasks = new List<Task<bool>>();
            var no = 0;
            foreach (var s in selected)
            {
                no++;
                var sn = ShortName(s.Root.Place.Record);
                if (_byShort.ContainsKey(sn)) { skipRunning++; skippedRunning.Add(sn); continue; }   // 見積もりの間に個別で始めたもの
                var job = new Ship3DJob(s.Root.Place.Record, KeyOf(sn), NameOf(s.Root), _batchCts)
                    { Kind = Ship3DJobKind.Station, EstimatedSeconds = s.Seconds, LogTag = $"まとめての {no} / {selected.Count} か所目" };
                AddJob(job, sn);
                batchJobs.Add(job);
                tasks.Add(RunBatchJobAsync(job, sn, s.Root, index!, mapNames, extractor.StarBreakerExe, stamp, failures));
            }
            Log($"ステーションのまとめて取り込む: {tasks.Count} か所を待ち行列に並べました (同時数の上限 {parallel} 本。船と共通の待ち行列)");
            var results = await Task.WhenAll(tasks);
            var done = results.Count(r => r);
            var cancelled = ct.IsCancellationRequested;
            // 窓はボタンの名前 (［マップをまとめて取り込む］) で出し、ログは前のログと比べられるよう前の文言のまま
            var result = $"{(cancelled ? "取り消しました" : "終わりました")}。作った地図 {done} / {tasks.Count} か所" +
                         (failures.Count > 0 ? $"、失敗・見積もれなかったもの {failures.Count} か所" : "");
            var summary = "マップをまとめて取り込む: " + result;
            Log("ステーションのまとめて取り込む: " + result + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : ""));
            Log($"ステーションのまとめて取り込む: 全体 {swBatch.Elapsed.TotalSeconds:F1} 秒。作った {done} か所、失敗 {batchJobs.Count(j => j.Phase == Ship3DJobPhase.Failed)} か所、" +
                $"取り消し {batchJobs.Count(j => j.Phase == Ship3DJobPhase.Cancelled)} か所 (全 {roots.Count} か所のうち並べた {tasks.Count} か所)。" +
                Ship3DExports.LogSkipLists(skippedFresh, skippedRunning, skippedForeign, notTakenNames, null));
            MessageBox.Show(summary + (failures.Count > 0 ? "\n\n" + string.Join("\n", failures.Take(15)) + (failures.Count > 15 ? "\n…(続きはアプリのログ)" : "") : ""),
                "ステーションの地図", MessageBoxButton.OK, failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log($"ステーションのまとめて取り込むを取り消しました (段階: {batchStage}、経過 {swBatch.Elapsed.TotalSeconds:F1} 秒)");
            MessageBox.Show("マップをまとめて取り込むを取り消しました。", "ステーションの地図", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception e)
        {
            Log($"ステーションのまとめて取り込むが失敗しました (段階: {batchStage}、経過 {swBatch.Elapsed.TotalSeconds:F1} 秒): {Ship3DExports.Describe(e)}");
            ShowError(owner, $"マップをまとめて取り込むが失敗しました: {e.Message}", $"ステーションのまとめて取り込むが失敗しました: {e.Message}");
        }
        finally
        {
            index?.Dispose();
            // 終わったときの取り込んだ数・取り込んでいない数で書き直す (ボタンの判定は SetBatchActive(false) の知らせで決め直す)
            if (batchTargets != null) BatchTargetRecord.Write(BatchTargetRecord.Maps, stamp, batchTargets, StateOf);
            BatchText = "";
            _batchCts?.Dispose();
            _batchCts = null;
            SetBatchActive(false);
        }
    }

    // まとめての中の 1 か所 (順番を待ってから計画を作って取り込む)。作れたら true。失敗は failures に足す (知らせは最後にまとめて)
    private async Task<bool> RunBatchJobAsync(Ship3DJob job, string shortName, StationRoot root, StationIndex index, MapNames? names, string exe,
        Ship3DExports.P4kStamp stamp, List<string> failures)
    {
        var tcs = new TaskCompletionSource<string?>();
        job.Task = tcs.Task;   // 個別の［読み込み］が同じステーションなら合流する
        try
        {
            var ct = job.Cts.Token;
            await Ship3DExports.Instance.WaitTurnAsync(job, ct);
            job.SetPhase(Ship3DJobPhase.Estimating, "書き出しの計画を作っています");
            job.LogStage = "計画: 木・書き出しの計画 (Prepare)";
            var prep = await Task.Run(async () => Prepare(index, root, await Ship3DP4kIndex.GetAsync(stamp), names, ct), ct);
            var path = await ExportAsync(job, shortName, exe, stamp, prep);
            tcs.TrySetResult(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 順番待ちのまま取り消されたものは 1 件ずつ出さない (数はまとめての終わりの行に出す)
            if (job.LogStage != "順番待ち") Ship3DExports.LogJob(job, $"取り込みを取り消しました ({job.LogTag}、段階: {job.LogStage})");
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            tcs.TrySetResult(null);
            return false;
        }
        catch (Exception e)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            failures.Add($"{job.Name} ({shortName}): {e.Message}");
            Log($"ステーションのまとめて取り込む: 「{job.Name}」({shortName}) を作れませんでした ({job.LogTag}、段階: {job.LogStage}): {Ship3DExports.Describe(e)}");
            tcs.TrySetResult(null);
            return false;
        }
        finally { RemoveJob(job, shortName); }
    }
}
