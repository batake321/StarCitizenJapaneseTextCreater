using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace StarCitizenJapaneseTextCreater;

// 船の 3D モデル (GLB) の書き出し (取り込み) とキャッシュ。アプリ全体で 1 つ (Ship3DExports.Instance)。
//   書き出し: アプリの中の読み取り (ShipExport。StarBreaker v0.2.2 の entity export --materials textures と同じ GLB) を選択中チャンネルの Data.p4k から。
//     (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」(案 A)。前は starbreaker.exe を動かしていた)
//     ［読み込み］(ImportAsync)・設定タブの［まとめて取り込む］(RunBatchAsync) と、表示 (GetViewPathAsync):
//     (2026-11-04、利用者の決定「取り込み済みなら取り込みデータ、取り込みしてなければ直接」「設定で取り込み可能」) 表示しようとした船が取り込み済みならそれを出し、
//     取り込んでいなければ確認の窓を出さずに Data.p4k から直接読んで出す。設定「3D モデルを保存しない」(既定は保存する) が切れていれば、読んだものを取り込み (保存) にする。
//     入っていれば作業用のフォルダに表示の間だけ置き、表示をやめたら消す (次に見るときはまた直接読む)。書き出しは裏で並行に動く。同じ船の書き出しが動いていればそれに合流する (二重に書き出さない)。
//     始める前には毎回、CPU 使用率 (90% 以上なら待つ) と同時数の上限 (MaxParallelExports) を見る。待っている書き出しは並んだ順に始める (Pump)
//   キャッシュ: 置き場所 (設定の 3D モデルの置き場所。既定は TEMP の中) に「record の短い名前.glb」と、横に「同じ名前.json」(Sidecar)。
//     横の記録にアプリの印 (SidecarMark) があり中身が読めるものだけをこのアプリのキャッシュとして扱う (使用量・古い順の削除・鮮度の判定)。印の無いファイルには触れない。
//     書き出したときの Data.p4k の印 (パス・大きさ・更新日時) が今と同じなら最新。違う (パッチ) ときは、その船の元データを照合する (VerifyAsync):
//       元ファイル (参照ジオメトリ・material・テクスチャ) の p4k の中の大きさと Data.p4k に記録された CRC、船の構成 (--dump-hierarchy と同じもの。ShipExport.DumpHierarchy) の内容のハッシュが、書き出したときと同じなら
//       記録の Data.p4k の印を新しいものに書き換えて使い続ける。違う・照合できないときは、その場でキャッシュを消す (表示中・書き出し中は後で消す)
//     最大容量を超えたら、書き出しが終わるたびに古いもの (書き出した日時の古い順) から消す。表示中・書き出し中・今書き出したものは消さない
//   進み具合: 船ごと (Jobs) と全体 (Overall*)。書き出しの段階の行 (starbreaker -v と同じ形の行を ShipExport が出す。Ship3DStageTracker) と予想時間から出す (予想にもとづく)
public sealed class Ship3DExports : INotifyPropertyChanged
{
    private static Ship3DExports? _instance;
    // UI スレッドで最初に触ったときに作る
    public static Ship3DExports Instance => _instance ??= new Ship3DExports();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ボタンの有効・無効を決め直すきっかけ (CPU 使用率が 90% をまたいだ・書き出しが加わった / 終わった・照合を始めた / 終えた・
    // キャッシュを書いた / 消した・設定の置き場所が変わった)。UI スレッドで呼ぶ
    public event Action? StateChanged;
    private void RaiseStateChanged() => StateChanged?.Invoke();
    // 書き出しが終わった船 (短い名前)。その船を表示している画面がそのまま表示する
    public event Action<string>? Exported;

    // 動いている (待っている) 書き出し。船ごとのバーに出す
    public ObservableCollection<Ship3DJob> Jobs { get; } = new();

    // ===== 定数 =====

    // 予想の式の初めの係数 (この PC で親が実測。テクスチャ付き・船内込み):
    //   X1 = 参照ジオメトリ合計 35MB → 書き出し 5 秒・GLB 26MB / 600i = 135MB → 9 秒・147MB / Polaris = 491MB → 26 秒・648MB / Idris-P = 367MB → 26 秒・489MB。
    //   おおむね 時間 ≈ 3 秒 + 0.05 秒/MB、GLB の大きさ ≈ 参照合計 × 1.35。
    //   参照合計は、--dump-hierarchy の参照パス (重複は 1 つ) の p4k list の大きさに、同名の末尾 m のファイル (.cgam / .cgfm 等) を足したもの。MB は 1,000,000 バイト
    //   (この数え方で X1 35.4MB・600i 134.8MB・Polaris 489.4MB・Idris-P 366.3MB になり、上の実測の数と合うことを確かめた)
    public const double BaseSeconds = 3.0;
    public const double InitialSecondsPerMb = 0.05;
    public const double InitialSizeRatio = 1.35;
    // CPU 使用率がこれ以上なら書き出しを始めない
    public const double CpuLimitPercent = 90;
    // 1 本の書き出しに見込むメモリ (利用者の決定。親の実測: 1 本あたりの最大 600i 1.4GB、Polaris 2.9GB)
    public const long MemoryPerExport = 3L * 1024 * 1024 * 1024;
    private const long MB = 1024 * 1024;
    // 最大容量の初期値の上限 (2GB)
    public const long DefaultMaxCapMb = 2048;

    // ％と残り時間の出し方の説明 (進み具合のバーのツールチップ)
    public static string EstimateTip =>
        "進み具合は予想にもとづいて出しています: 書き出しのログの段階 (読み込み・部品 k/N・船内・仕上げ) と、" +
        "ゲームデータの大きさから見積もった時間 (この PC の実績で直したもの) から残り時間を求め、経過 ÷ (経過 + 残り) を％にしています。終わるまでは 99% で止まります。";

    // 仮想ホスト (WebView2 の SetVirtualHostNameToFolderMapping。Ship3DWeb)
    public const string CacheHost = "scjt-ship3d.example";

    // 横の記録 (<短い名前>.json) に書くアプリの印。印があり中身が読めるものだけをこのアプリのキャッシュとして扱う
    public const string SidecarMark = "StarCitizenJapaneseTextCreater.ship3d";

    private readonly DispatcherTimer _timer;
    private readonly CpuUsageMeter _cpu = new();
    private double _cpuPercent;
    private bool _cpuHigh;

    private Ship3DExports()
    {
        // 1 秒ごとに、CPU 使用率を測り、進み具合の表示を直し、待っている書き出しを始められるか見る
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        // アプリを終えるときは動いている書き出しを取り消す。残った一時ファイルは次の起動時の掃除 (CleanWorkFolder) で消す
        if (Application.Current != null) Application.Current.Exit += (_, _) =>
        {
            foreach (var j in Jobs.ToList()) j.Cancel();
        };
        CleanWorkFolder();
    }

    // ===== 置き場所・容量 =====

    public static string DefaultFolder => Path.Combine(Path.GetTempPath(), "StarCitizenJapaneseTextCreater", "ship3d");
    public static string Folder => string.IsNullOrWhiteSpace(App.Config.Ship3DFolder) ? DefaultFolder : App.Config.Ship3DFolder.Trim();
    // 作業用 (書きかけの一時ファイル) のフォルダ。置き場所に利用者の既存のフォルダを選ばれても重ならないよう、アプリ固有の名前にする
    internal static string WorkFolder => Path.Combine(Folder, "_scjt_ship3d_work");
    // 作業用のフォルダにアプリが作る一時ファイルの名前の形 (<短い名前>_<GUID 32 桁>.glb / .json / .hierarchy.json)。起動時の掃除はこの形のファイルだけ消す
    private static readonly Regex WorkFileRegex = new(@"^.+_[0-9a-f]{32}\.(glb|json|hierarchy\.json)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    internal static string NewWorkFile(string shortName, string ext) => Path.Combine(WorkFolder, $"{shortName}_{Guid.NewGuid():N}{ext}");

    // フォルダのドライブの容量 (全体・残り。バイト)。調べられなければ null
    public static (string Root, long Total, long Free)? DriveOf(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? "";
            if (root.Length == 0) return null;
            var d = new DriveInfo(root);
            return (root, d.TotalSize, d.AvailableFreeSpace);
        }
        catch { return null; }
    }

    // 最大容量の初期値 (MB): 置き場所のドライブの残り容量の 1/3、ただし最大 2GB。ドライブを調べられなければ 2GB
    public static long DefaultMaxMb(string folder)
    {
        var d = DriveOf(folder);
        if (d == null) return DefaultMaxCapMb;
        return Math.Max(1, Math.Min(DefaultMaxCapMb, d.Value.Free / 3 / MB));
    }

    // 未設定 (0) のときの初期値は、このアプリの起動中は最初に求めた値を使う (キャッシュが増えるたびに残り容量が減って上限が下がらないように)
    private static long? _defaultMaxMbOfSession;
    public static long MaxMb
    {
        get
        {
            if (App.Config.Ship3DMaxMB > 0) return App.Config.Ship3DMaxMB;
            return _defaultMaxMbOfSession ??= DefaultMaxMb(Folder);
        }
    }

    // キャッシュの GLB (置き場所の直下の *.glb のうち、横の記録にアプリの印があるもの) と合計の大きさ
    public static List<FileInfo> CachedFiles()
    {
        try
        {
            var dir = new DirectoryInfo(Folder);
            if (!dir.Exists) return new List<FileInfo>();
            return dir.GetFiles("*.glb", SearchOption.TopDirectoryOnly)
                .Where(f => ReadOurSidecar(Path.GetFileNameWithoutExtension(f.Name)) != null).ToList();
        }
        catch { return new List<FileInfo>(); }
    }

    // (2026-11-04、利用者の指示でステーションの 2D の地図・古い方式のステーションの 3D を消したので、船の分だけを数える)
    public static long CacheBytes() => CachedFiles().Sum(f => f.Length);

    public static string ShortName(string record) =>
        record.StartsWith("EntityClassDefinition.", StringComparison.OrdinalIgnoreCase) ? record["EntityClassDefinition.".Length..] : record;

    public static string GlbPath(string shortName) => Path.Combine(Folder, shortName + ".glb");
    private static string SidecarPath(string shortName) => Path.Combine(Folder, shortName + ".json");

    // 前の起動で途中で終わった書き出しの一時ファイルを消す (動いている書き出しが無いとき)。
    // フォルダごとは消さず、アプリが作る一時ファイルの名前の形 (WorkFileRegex) に合うファイルだけ消す
    private void CleanWorkFolder()
    {
        if (Jobs.Count > 0) return;
        try
        {
            if (!Directory.Exists(WorkFolder)) return;
            foreach (var f in Directory.GetFiles(WorkFolder))
            {
                if (!WorkFileRegex.IsMatch(Path.GetFileName(f))) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    // 設定タブで置き場所・最大容量を保存した後 (ボタンの有効・無効を決め直す)
    public void NotifySettingsChanged()
    {
        _defaultMaxMbOfSession = null;
        RaiseStateChanged();
    }

    // ===== Data.p4k =====

    // 書き出しに使う Data.p4k (選択中チャンネル) と、その大きさ・更新日時
    internal sealed record P4kStamp(string Path, long Size, long WriteUtcTicks)
    {
        public string Key => $"{Path}|{Size}|{WriteUtcTicks}";
    }

    internal static P4kStamp? CurrentStamp()
    {
        var p4k = new GameDataExtractor(DetailPanel.WorkDir()).FindDataP4k();
        if (p4k == null) return null;
        try
        {
            var fi = new FileInfo(p4k);
            return new P4kStamp(fi.FullName, fi.Length, fi.LastWriteTimeUtc.Ticks);
        }
        catch { return null; }
    }

    // 照合に使う元ファイル 1 つ (Path は p4k の中のパスを小文字・区切り / ・先頭の data/ を外した形。Size が -1 なら書き出したときに p4k に無かったもの)
    internal sealed class SourceFile
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public uint Crc { get; set; }
    }

    // 横に置く記録 (<短い名前>.json)
    private sealed class Sidecar
    {
        // アプリの印 (SidecarMark)
        public string App { get; set; } = "";
        public string Record { get; set; } = "";
        // 書き出したとき (照合で同じと分かったら、そのときのもの) の Data.p4k の印
        public string P4kPath { get; set; } = "";
        public long P4kSize { get; set; }
        public long P4kWriteUtcTicks { get; set; }
        public string ExportedUtc { get; set; } = "";
        public double ExportSeconds { get; set; }
        public double GeometryMb { get; set; }
        // --dump-hierarchy の内容のハッシュ (キーの並びをそろえた JSON の SHA-256。Ship3DEstimator.CanonicalHash)
        public string HierarchyHash { get; set; } = "";
        // 元ファイルの一覧 (p4k の中の大きさと Data.p4k に記録された CRC)
        public List<SourceFile> Sources { get; set; } = new();
    }

    // 横の記録を読む。読めて、アプリの印があり、record の短い名前がファイル名と同じときだけ返す (それ以外は null = このアプリのものではない)
    private static Sidecar? ReadOurSidecar(string shortName)
    {
        try
        {
            var side = SidecarPath(shortName);
            if (!File.Exists(side)) return null;
            var s = JsonSerializer.Deserialize<Sidecar>(File.ReadAllText(side));
            if (s == null || s.App != SidecarMark || s.Record.Length == 0) return null;
            return string.Equals(ShortName(s.Record), shortName, StringComparison.OrdinalIgnoreCase) ? s : null;
        }
        catch { return null; }
    }

    // 横の記録を書く (作業用のフォルダに書いてから名前を変える)
    private static void WriteSidecar(string shortName, Sidecar side)
    {
        Directory.CreateDirectory(WorkFolder);
        var tmp = NewWorkFile(shortName, ".json");
        File.WriteAllText(tmp, JsonSerializer.Serialize(side, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        File.Move(tmp, SidecarPath(shortName), overwrite: true);
    }

    // 置き場所に同じ名前の .glb か .json があり、それがこのアプリのもの (印のある記録) でなければ true (上書きしない)
    private static bool HasForeignFile(string shortName) =>
        (File.Exists(GlbPath(shortName)) || File.Exists(SidecarPath(shortName))) && ReadOurSidecar(shortName) == null;

    // ===== キャッシュの状態 =====

    // その船の今の状態
    public Ship3DCacheState StateOf(string record)
    {
        var shortName = ShortName(record);
        if (_verifying.ContainsKey(shortName)) return Ship3DCacheState.Verifying;
        // 照合で変わっていたが表示中・書き出し中なので後で消すもの: もう使わないので無いものとして扱う (［読み込み］が使える)
        if (_pendingDelete.Contains(shortName)) return Ship3DCacheState.None;
        var stamp = CurrentStamp();
        if (stamp == null) return Ship3DCacheState.NoGame;
        if (!File.Exists(GlbPath(shortName))) return Ship3DCacheState.None;
        var s = ReadOurSidecar(shortName);
        if (s == null) return Ship3DCacheState.None;
        return string.Equals(s.P4kPath, stamp.Path, StringComparison.OrdinalIgnoreCase) && s.P4kSize == stamp.Size && s.P4kWriteUtcTicks == stamp.WriteUtcTicks
            ? Ship3DCacheState.Fresh : Ship3DCacheState.NeedsVerify;
    }

    // StateOf のうちファイルだけで決まる部分: 最新 (Fresh) か。UI スレッドの外から呼べる (設定タブのまとめて取り込むのボタンの数え直しに使う)。
    // 照合中・後で消すもの (StateOf では最新にならない) はここでは見ないので、呼ぶ側で HeldBack を見る
    internal static bool IsFreshOnDisk(string record, P4kStamp stamp)
    {
        var shortName = ShortName(record);
        if (!File.Exists(GlbPath(shortName))) return false;
        var s = ReadOurSidecar(shortName);
        return s != null && string.Equals(s.P4kPath, stamp.Path, StringComparison.OrdinalIgnoreCase) && s.P4kSize == stamp.Size && s.P4kWriteUtcTicks == stamp.WriteUtcTicks;
    }

    // StateOf が照合中・後で消すもの (最新でない) とする船か。UI スレッドで呼ぶ
    internal bool HeldBack(string record)
    {
        var shortName = ShortName(record);
        return _verifying.ContainsKey(shortName) || _pendingDelete.Contains(shortName);
    }

    public bool IsExporting(string record) => _byShort.ContainsKey(ShortName(record));

    // 表示に使える GLB のパス。最新ならそのまま、Data.p4k が変わっていれば照合してから (変わっていなければ使う)。使えなければ null (書き出しはしない)
    public async Task<string?> GetCachedPathAsync(string record)
    {
        var state = StateOf(record);
        if (state == Ship3DCacheState.Fresh) return GlbPath(ShortName(record));
        if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying)
            return await VerifyAsync(record) ? GlbPath(ShortName(record)) : null;
        return null;
    }

    // 照合中の船 (短い名前 → 照合)。同じ船の照合は 1 つにまとめる
    private readonly Dictionary<string, Task<bool>> _verifying = new(StringComparer.OrdinalIgnoreCase);

    // Data.p4k が変わった船の元データを照合する。同じなら true (記録の Data.p4k の印を新しいものにする)。
    // 変わっていた・照合できなかったら false で、その場でキャッシュを消す (表示中・書き出し中なら、表示を閉じた後・書き出しの後に消す)
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
            var stamp = CurrentStamp() ?? throw new InvalidOperationException("ゲームの Data.p4k が見つかりません");
            var side = ReadOurSidecar(shortName) ?? throw new InvalidOperationException("横の記録が読めません");
            if (side.Sources.Count == 0 || side.HierarchyHash.Length == 0) reason = "照合の記録 (元ファイルの一覧) がありません";
            else
            {
                var sw = Stopwatch.StartNew();
                var index = await Ship3DP4kIndex.GetAsync(stamp);
                foreach (var src in side.Sources)
                {
                    var now = index.TryGetValue(Ship3DEstimator.Hash(src.Path), out var v) ? (v.Size, v.Crc) : (-1L, 0u);
                    if (now.Item1 != src.Size || now.Item2 != src.Crc) { reason = $"元データ {src.Path} が変わっていました"; break; }
                }
                if (reason == null)
                {
                    var dump = await Ship3DEstimator.DumpHierarchyAsync(stamp, shortName, CancellationToken.None);
                    if (dump.Hash != side.HierarchyHash) reason = "船の構成 (--dump-hierarchy の内容) が変わっていました";
                }
                if (reason == null)
                {
                    side.P4kPath = stamp.Path;
                    side.P4kSize = stamp.Size;
                    side.P4kWriteUtcTicks = stamp.WriteUtcTicks;
                    WriteSidecar(shortName, side);
                    Log($"「{shortName}」の元データを照合しました: 変わっていないので、そのまま使います ({side.Sources.Count} ファイル、{sw.Elapsed.TotalSeconds:F1} 秒)");
                    return true;
                }
            }
        }
        catch (Exception ex) { reason = $"照合できませんでした: {ex.Message}"; }
        finally
        {
            _verifying.Remove(shortName);
            RaiseStateChanged();
        }
        // (2026-11-02、利用者の決定「a」) 変わっていた船は消さずに、裏で自動で取り直す (QueueRefresh。書き出し中の船は今までどおり後で消す)
        QueueRefresh(shortName, reason!);
        return false;
    }

    // 照合で変わっていた (照合できなかった) 船のキャッシュを消す。表示中・書き出し中なら後で消す (_pendingDelete)
    private readonly HashSet<string> _pendingDelete = new(StringComparer.OrdinalIgnoreCase);

    private void DeleteChangedCache(string shortName, string reason)
    {
        if (_views.ContainsKey(shortName) || _byShort.ContainsKey(shortName))
        {
            _pendingDelete.Add(shortName);
            Notice = $"「{shortName}」はパッチで元データが変わったので、表示・書き出しが終わったらキャッシュを消します ({reason})";
            Log(Notice);
            // 後で消す船は「キャッシュ無し」になるので、ボタンの状態を決め直す
            RaiseStateChanged();
            return;
        }
        DeleteOurCache(shortName);
        Notice = $"「{shortName}」はパッチで元データが変わったので、キャッシュを消しました ({reason})。［読み込み］で取り込み直せます";
        Log(Notice);
        RaiseStateChanged();
    }

    // アプリの印のあるキャッシュ (.glb と横の .json) を消す。印が無ければ何もしない
    private static void DeleteOurCache(string shortName)
    {
        if (ReadOurSidecar(shortName) == null) return;
        try { File.Delete(GlbPath(shortName)); } catch (Exception ex) { Log($"{shortName}.glb を消せませんでした: {ex.Message}"); return; }
        try { File.Delete(SidecarPath(shortName)); } catch (Exception ex) { Log($"{shortName}.json を消せませんでした: {ex.Message}"); }
    }

    // 後で消すことにした船を、表示・書き出しが無くなっていれば消す
    private void DeletePendingIfIdle(string shortName)
    {
        if (!_pendingDelete.Contains(shortName) || _views.ContainsKey(shortName) || _byShort.ContainsKey(shortName)) return;
        _pendingDelete.Remove(shortName);
        DeleteOurCache(shortName);
        Notice = $"「{shortName}」はパッチで元データが変わったので、キャッシュを消しました。［読み込み］で取り込み直せます";
        Log(Notice);
        RaiseStateChanged();
    }

    // ===== 表示中 (消さないもの) =====

    private readonly Dictionary<string, int> _views = new(StringComparer.OrdinalIgnoreCase);
    public void AcquireView(string shortName) => _views[shortName] = _views.TryGetValue(shortName, out var n) ? n + 1 : 1;
    public void ReleaseView(string shortName)
    {
        if (!_views.TryGetValue(shortName, out var n)) return;
        if (n <= 1) _views.Remove(shortName); else _views[shortName] = n - 1;
        DeletePendingIfIdle(shortName);
        DeleteViewFileIfIdle(shortName);
    }

    // 保存しないで直接読んだ船の GLB (短い名前 → 作業用のフォルダの中のファイル)。表示をやめたら消す
    private readonly Dictionary<string, string> _viewFiles = new(StringComparer.OrdinalIgnoreCase);

    private void DeleteViewFileIfIdle(string shortName)
    {
        if (_views.ContainsKey(shortName) || _byShort.ContainsKey(shortName)) return;
        if (!_viewFiles.Remove(shortName, out var path)) return;
        try { File.Delete(path); } catch (Exception ex) { Log($"表示の間だけ置いた {Path.GetFileName(path)} を消せませんでした: {ex.Message}"); }
    }

    // 表示に使う GLB (置き場所の中のパス)。取り込み済みならそれ (Data.p4k が変わっていれば照合してから)、無ければ Data.p4k から直接読む
    // (確認の窓は出さない。CPU 使用率・同時数の上限は書き出しと同じく待つ。保存するかは設定)。読めなければ null (知らせは出し済み)
    public async Task<string?> GetViewPathAsync(string record, string name, Window? owner)
    {
        var shortName = ShortName(record);
        if (_viewFiles.TryGetValue(shortName, out var vf) && File.Exists(vf)) return vf;
        if (await GetCachedPathAsync(record) is { } cached) return cached;
        if (StateOf(record) == Ship3DCacheState.NoGame)
        {
            ShowError(owner, "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。");
            return null;
        }
        return await ImportAsync(record, name, owner, confirm: false, save: !App.Config.Ship3DNoSave);
    }

    // ===== 船以外の品 (武器・装備・船の部品など。2026-11-04、利用者の要望「武器の3D出ないけど？」) =====
    // 書き出し・保存・照合・表示は船と同じ流れ (record の短い名前でキャッシュ。ShipExport.Export は船以外の entity も同じに書く)。
    // 違うのは、書き出す前に形があるか調べること (形が無ければ書き出さない。NoShape が true になる) と、ジョブの種類 (Item)

    private static readonly Dictionary<string, bool> _isItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noShape = new(StringComparer.OrdinalIgnoreCase);

    // record が船・車両 (NOITEM_Vehicle) でなければ true (保存データの item_type で決める。引けなければ船として扱う)
    internal static bool IsItemRecord(string record)
    {
        lock (_isItem)
        {
            if (_isItem.TryGetValue(record, out var v)) return v;
        }
        var isItem = false;
        try
        {
            var db = DetailPanel.DbPath();
            if (File.Exists(db))
            {
                using var q = GameDataQueryService.OpenReadOnly(db);
                var t = q.ItemTypeOf(record);
                isItem = t.Length > 0 && !t.StartsWith("NOITEM_Vehicle", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) { Log($"{record} の種類を調べられませんでした (船として扱います): {ex.Message}"); return false; }
        lock (_isItem) _isItem[record] = isItem;
        return isItem;
    }

    // 直前に調べて「形が無い」とした品か (表示の側が「形がありません」と出すのに使う)
    public bool NoShape(string record) => _noShape.Contains(ShortName(record));

    // 保存しない設定か (［まとめて取り込む］［読み込み］を使えなくする)
    public static bool NoSave => App.Config.Ship3DNoSave;

    // ===== 知らせ =====

    private string _notice = "";
    // 進み具合の欄の下に出す最後の知らせ (容量で消した・照合で消した等)。次の書き出しが加わるまで出しておく
    public string Notice { get => _notice; private set { _notice = value; Changed(nameof(Notice)); Changed(nameof(HasNotice)); Changed(nameof(ShowPanel)); } }
    public bool HasNotice => _notice.Length > 0;

    private static void Log(string msg) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] {msg}");

    // 取り込み 1 件のログ (船は [3D モデル]、ステーションの地図は [ステーションの地図]、ステーションの 3D 表示は [ステーションの 3D] の頭。続けて「名前」(短い名前))
    internal static void LogJob(Ship3DJob job, string msg) =>
        App.AppendStartupLog(App.Config.WorkingDirectory, $"{(job.Kind switch { Ship3DJobKind.Station => "[ステーションの地図]", Ship3DJobKind.Station3D => "[ステーションの 3D]", _ => "[3D モデル]" })} 「{job.Name}」({job.Short}) {msg}");

    // ログに出す例外の中身: アプリが理由を書いて投げる種類 (InvalidOperationException・IOException) は型と文、それ以外は型・文・呼び出し履歴
    internal static string Describe(Exception ex) =>
        ex is InvalidOperationException or IOException ? $"{ex.GetType().FullName}: {ex.Message}" : ex.ToString();

    // logMsg: ログに残す文言が窓と違うとき (まとめて取り込むの窓はボタンの名前で出し、ログは前の文言のまま)
    private static void ShowError(Window? owner, string msg, string? logMsg = null)
    {
        Log(logMsg ?? msg);
        if (owner != null && owner.IsLoaded) MessageBox.Show(owner, msg, "3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(msg, "3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ===== CPU・メモリ・同時数 =====

    public double CpuPercent => _cpuPercent;
    // CPU 使用率が 90% 以上 (書き出しを始めない。［読み込み］・［まとめて取り込む］を使えなくする)
    public bool CpuHigh => _cpuHigh;
    // 今 CPU 使用率が 90% 以上か (Instance をまだ作っていなければ false。どのスレッドからも読める。ゲームのアイコンの取り出し GameIcons が見る)
    internal static bool CpuHighNow => _instance?._cpuHigh ?? false;

    // 空き物理メモリ (バイト。GlobalMemoryStatusEx の ullAvailPhys)。調べられなければ null
    public static long? AvailablePhysicalMemory()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? (long)Math.Min(m.ullAvailPhys, long.MaxValue) : null;
    }

    // 書き出しの同時数の上限 (個別とまとめての合計)。利用者の決定:
    //   min(論理プロセッサ数 ÷ 2, 空き物理メモリ ÷ 3GB)、最低 1。空きメモリは呼ぶたびに測り直す。
    //   (親の実測: この PC (NVMe SSD 3 台、論理プロセッサ 24) で 600i を 1/2/4 本同時 → 全体 9.3/9.3/10.9 秒、CPU 平均 10/19/30%。
    //    1 本あたりのメモリ最大は 600i 1.4GB、Polaris 2.9GB)
    //   同時数の決め方はここ 1 か所だけ
    public static (int Limit, int ByCpu, int? ByMemory, long? FreeMemory) MaxParallelExports()
    {
        var byCpu = Math.Max(1, Environment.ProcessorCount / 2);
        var free = AvailablePhysicalMemory();
        int? byMem = free is long f ? (int)Math.Min(int.MaxValue, f / MemoryPerExport) : null;
        var limit = Math.Max(1, byMem is int m ? Math.Min(byCpu, m) : byCpu);
        return (limit, byCpu, byMem, free);
    }

    // ===== 1 秒ごと =====

    private void Tick()
    {
        var cpu = _cpu.Sample();
        if (cpu is double c)
        {
            _cpuPercent = c;
            Changed(nameof(CpuPercent));
            var high = c >= CpuLimitPercent;
            if (high != _cpuHigh)
            {
                _cpuHigh = high;
                Changed(nameof(CpuHigh));
                RaiseStateChanged();
            }
        }
        Pump();
        RefreshProgress();
        // Data.p4k の印が変わった (パッチ・チャンネルの切り替え) かを見る (Data.p4k を探して大きさ・更新日時を見るだけ。開かない)
        var stampKey = CurrentStamp()?.Key;
        if (!_stampSeen)
        {
            _stampSeen = true;
            _lastStampKey = stampKey;
            // 自動の取り直し (StartAutoRefresh の説明): 起動の後 AutoRefreshDelay たってから 1 回 (アプリを閉じている間にパッチが入ったとき)。ステーションの 3D も同じ
            var t = new DispatcherTimer { Interval = AutoRefreshDelay };
            t.Tick += (_, _) => { t.Stop(); StartAutoRefresh("起動の後"); StationView3DLayout.Start("起動の後"); };
            t.Start();
        }
        else if (stampKey != _lastStampKey)
        {
            _lastStampKey = stampKey;
            P4kStampChanged?.Invoke();
            StartAutoRefresh("Data.p4k の印が変わった");
            StationView3DLayout.Start("Data.p4k の印が変わった");
        }
    }

    // Data.p4k の印 (CurrentStamp().Key) が変わったとき (1 秒ごとの Tick で気づく。UI スレッドで呼ぶ)。
    // 設定タブの［船舶・車両をまとめて取り込む］［マップをまとめて取り込む］の「全部取り込み済み」を出し直すのに使う
    public event Action? P4kStampChanged;
    private bool _stampSeen;
    private string? _lastStampKey;

    // ===== 待ち行列 =====

    // 始める順番を待っている書き出し (並んだ順)。Pump が先頭から、CPU 使用率と同時数の上限を見て始める
    private readonly List<(Ship3DJob Job, TaskCompletionSource Start)> _queue = new();
    // 今動いている書き出し (StarBreaker の entity export)
    private readonly List<Ship3DJob> _running = new();

    // 順番を待つ (CPU 使用率が 90% 未満で、動いている数が上限より少なくなったら始まる)。取り消されたら取り消しの例外
    internal async Task WaitTurnAsync(Ship3DJob job, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((job, tcs));
        job.LogStage = "順番待ち";
        job.QueueWatch.Restart();
        // まとめての中のものは 1 件ずつ出さない (並べた数はまとめての側で 1 行に出す)
        if (!job.InBatch)
            LogJob(job, $"待ち行列に入りました: {_queue.Count} 番目 (動いている {_running.Count} 本、同時数の上限 {MaxParallelExports().Limit} 本)");
        job.SetPhase(Ship3DJobPhase.Queued, "順番待ち");
        Pump();
        using (ct.Register(() => Application.Current?.Dispatcher.BeginInvoke(() =>
               {
                   _queue.RemoveAll(q => q.Job == job);
                   tcs.TrySetCanceled(ct);
               })))
        {
            await tcs.Task;
        }
    }

    // 待っている書き出しを、先頭から始められるだけ始める (始める前に毎回 CPU 使用率と同時数の上限を見る)。
    // 始められないときは、待っている書き出しにその理由を出す
    private void Pump()
    {
        while (_queue.Count > 0)
        {
            string? reason = null;
            string? waitKind = null;   // ログ用: 待つ理由の種類 (cpu・memory・limit)
            int limitNow = 0;
            if (_cpuHigh) { reason = $"CPU 使用率 {_cpuPercent:F0}% のため待っています (90% 未満になったら始めます)"; waitKind = "cpu"; }
            else
            {
                var (limit, byCpu, byMem, free) = MaxParallelExports();
                limitNow = limit;
                // ステーションの地図が足して動かしている starbreaker (_extraRuns) も同時数に数える
                if (_running.Count + _extraRuns >= limit)
                {
                    reason = byMem is int m && m < byCpu
                        ? $"メモリ待ち (空き {free / (double)(1024 * MB):F1} GB。1 本 3GB と見込み、同時 {limit} 本まで)"
                        : $"同時数の上限 ({limit} 本) のため待っています";
                    waitKind = byMem is int m2 && m2 < byCpu ? "memory" : "limit";
                }
            }
            if (reason != null)
            {
                // 理由の種類が変わったときだけ出す (CPU 使用率の数は毎秒変わるので、文ではなく種類で比べる)
                if (waitKind != _lastWaitKind)
                {
                    _lastWaitKind = waitKind;
                    Log($"待ち行列 (船・ステーションの地図 共通): {reason} (待ち {_queue.Count} 件、動いている {_running.Count} 本 + ステーションの地図が足した starbreaker {_extraRuns} 本)");
                }
                foreach (var (j, _) in _queue) j.SetPhase(Ship3DJobPhase.Queued, reason);
                return;
            }
            var (job, start) = _queue[0];
            _queue.RemoveAt(0);
            // 並行で動いた書き出しの秒数は遅くなるので、係数の実績には使わない
            if (_running.Count > 0)
            {
                job.Overlapped = true;
                foreach (var r in _running) r.Overlapped = true;
            }
            _running.Add(job);
            job.QueueWatch.Stop();
            job.LogStage = "順番が来た直後";
            LogJob(job, $"順番が来たので始めます ({job.LogTag}): 待ち {job.QueueWatch.Elapsed.TotalSeconds:F1} 秒 (動いている {_running.Count} 本 / 上限 {limitNow} 本、残りの待ち {_queue.Count} 件)");
            start.TrySetResult();
        }
        // 待ちが無くなった: 次に待ちが出たら、また理由を出す
        _lastWaitKind = null;
    }

    // ログ用: 最後に出した待つ理由の種類 (Pump)
    private string? _lastWaitKind;

    // ステーションの地図の取り込み (StationFloorBuilder.ExportAsync) が、自分の枠 (_running の 1 本) のほかに足して動かしている starbreaker の数。
    // starbreaker 1 本を 1 本として同時数の上限 (MaxParallelExports) に数える (Pump もこれを引く)
    private int _extraRuns;

    // starbreaker を 1 本足してよいか。よければ数に入れて true。UI スレッドで呼ぶ。
    //   足さないとき: CPU 使用率が 90% 以上 (Pump と同じ決まり)・順番を待っている取り込みがある (待っているものを先に始めるため)・
    //   動いている本数 (取り込みの枠 + 足した starbreaker) が同時数の上限に届いている
    internal bool TryAcquireExtraRun()
    {
        if (_cpuHigh || _queue.Count > 0) return false;
        if (_running.Count + _extraRuns >= MaxParallelExports().Limit) return false;
        _extraRuns++;
        return true;
    }

    // ステーションの地図の予想時間 (StationMapExports.EstimateSeconds) に使う、その取り込みで使える同時の本数 (UI スレッドで呼ぶ):
    //   同時数の上限 (MaxParallelExports) − ほかの取り込みの分 (動いている取り込み (self を除く) + 足した starbreaker)、最低 1
    internal int FreeRunSlots(Ship3DJob? self = null) =>
        Math.Max(1, MaxParallelExports().Limit - (_running.Count(j => j != self) + _extraRuns));

    // 足した starbreaker が終わった (UI スレッドで呼ぶ)。空いた枠で待っている取り込みを始める
    internal void ReleaseExtraRun()
    {
        if (_extraRuns > 0) _extraRuns--;
        Pump();
    }

    // ===== 船ごと・全体の進み具合 =====

    // 今回の書き出しのまとまり (動いている・待っている書き出しが無くなるまでに加わったもの全部。全体のバーの分母)
    private readonly List<Ship3DJob> _session = new();

    private int _overallPercent;
    private string _overallText = "";
    public int OverallPercent => _overallPercent;
    public string OverallText => _overallText;
    // 進み具合の欄を出すか (書き出し・まとめての見積もり・知らせがあるとき)
    public bool ShowPanel => Jobs.Count > 0 || BatchActive || HasNotice;

    internal void AddJob(Ship3DJob job)
    {
        if (HasNotice) Notice = "";
        Jobs.Add(job);
        _session.Add(job);
        _byShort[job.Short] = job;
        Changed(nameof(ShowPanel));
        RefreshProgress();
        RaiseStateChanged();
    }

    // 動いている書き出しの枠を空ける (ステーションの地図 (StationMapExports) の StarBreaker の部分が終わったとき。船の ExportAsync の _running.Remove と同じ)
    internal void EndRun(Ship3DJob j) => _running.Remove(j);

    internal void RemoveJob(Ship3DJob job)
    {
        Jobs.Remove(job);
        _running.Remove(job);
        _queue.RemoveAll(q => q.Job == job);
        if (_byShort.TryGetValue(job.Short, out var j) && j == job) _byShort.Remove(job.Short);
        // 取り消し・失敗は全体の分母から外す (終わったものは残して、全体の進みに数える)
        if (job.Phase != Ship3DJobPhase.Done) _session.Remove(job);
        if (Jobs.Count == 0) _session.Clear();
        Changed(nameof(ShowPanel));
        Pump();
        RefreshProgress();
        DeletePendingIfIdle(job.Short);
        RaiseStateChanged();
    }

    // 船ごとの％・残り時間と、全体の％・残り時間を出し直す (予想にもとづく)。
    //   全体の％ = 各船の (経過 (予想の総時間まで)) の合計 ÷ 各船の予想の総時間の合計 (終わった船は実際の時間、見積もり中の船は数えない)。
    //   全体の残り時間 = 終わっていない船の残り時間の合計 ÷ 動いている本数 (並行に動く分)。ただし動いている船のいちばん長い残り時間より短くはしない
    private void RefreshProgress()
    {
        foreach (var j in Jobs) j.Refresh();
        double sumTotal = 0, sumDone = 0, sumRemain = 0, maxRunningRemain = 0;
        foreach (var j in _session)
        {
            var (elapsed, remain, known) = j.Progress();
            if (!known) continue;
            sumTotal += elapsed + remain;
            sumDone += elapsed;
            sumRemain += remain;
            if (j.Phase == Ship3DJobPhase.Exporting) maxRunningRemain = Math.Max(maxRunningRemain, remain);
        }
        var running = Math.Max(1, _running.Count);
        var overallRemain = Math.Max(sumRemain / running, maxRunningRemain);
        int pct = sumTotal > 0 ? (int)Math.Min(99, Math.Floor(sumDone / sumTotal * 100)) : 0;
        // 数の単位: 全部船なら「隻」、ステーションの地図が混じれば「件」
        var unit = _session.All(j => j.Kind == Ship3DJobKind.Ship) ? "隻" : "件";
        var text = sumTotal > 0
            ? $"全体 {pct}%  残り 約 {FormatSeconds(overallRemain)}  ({_session.Count(j => j.Phase == Ship3DJobPhase.Done)} / {_session.Count} {unit})"
            : Jobs.Count > 0 ? "全体: 見積もり中" : "";
        if (pct != _overallPercent) { _overallPercent = pct; Changed(nameof(OverallPercent)); }
        if (text != _overallText) { _overallText = text; Changed(nameof(OverallText)); }
        // 順番待ちの船は 1 行にまとめる: 「待ち N 隻: <理由> (予想 合計 約 …)」。理由は先頭の待ちの船のもの (Pump が全部に同じ理由を入れる)
        var queued = Jobs.Where(j => j.Phase == Ship3DJobPhase.Queued).ToList();
        var queuedText = queued.Count == 0 ? ""
            : $"待ち {queued.Count} {(queued.All(j => j.Kind == Ship3DJobKind.Ship) ? "隻" : "件")}: {queued[0].Reason} (予想 合計 約 {FormatSeconds(queued.Sum(j => j.EstimatedSeconds))})";
        if (queuedText != _queuedText) { _queuedText = queuedText; Changed(nameof(QueuedText)); Changed(nameof(HasQueued)); }
    }

    private string _queuedText = "";
    public string QueuedText => _queuedText;
    public bool HasQueued => _queuedText.Length > 0;

    internal static string FormatSeconds(double s)
    {
        if (s < 1) return "まもなく";
        if (s < 60) return $"{Math.Ceiling(s):F0} 秒";
        var m = (int)(s / 60);
        var sec = (int)Math.Ceiling(s - m * 60);
        if (sec == 60) { m++; sec = 0; }
        return m < 60 ? $"{m} 分 {sec} 秒" : $"{m / 60} 時間 {m % 60} 分";
    }

    internal static string FormatBytes(double bytes) =>
        bytes >= 1024.0 * MB ? $"{bytes / (1024.0 * MB):F1} GB" : $"{bytes / MB:F0} MB";

    // ===== 個別の［読み込み］ =====

    // 船ごとの書き出し (短い名前 → 書き出し)。同じ船は合流する
    private readonly Dictionary<string, Ship3DJob> _byShort = new(StringComparer.OrdinalIgnoreCase);

    // ［読み込み］: その船をゲームデータから書き出す (書き出す前に予想時間・サイズを出して聞く)。終わったら GLB のパス。
    // すでに最新のキャッシュがあればそのパス (Data.p4k が変わっていれば先に照合する)。同じ船の書き出しが動いていれば合流する。
    // 取り消し・失敗・ゲームが見つからないときは null (知らせは出し済み)
    // confirm: 予想を出して確かめるか (表示からの直接の読み込みでは出さない)。save: 取り込み (置き場所に保存) にするか (false なら表示の間だけ作業用のフォルダに置く)
    public async Task<string?> ImportAsync(string record, string name, Window? owner, bool confirm = true, bool save = true)
    {
        var shortName = ShortName(record);
        var stamp = CurrentStamp();
        if (stamp == null)
        {
            ShowError(owner, "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。");
            return null;
        }
        if (_byShort.TryGetValue(shortName, out var running) && running.Task != null)
        {
            // 自動の取り直しの順番待ちなら、いちばん先にする (［読み込み］を押した船を待たせない)
            PromoteRefresh(running);
            Log($"「{name}」({shortName}) の［読み込み］: 同じ船の書き出しが動いているので、それに合流します ({running.LogTag})");
            try { return await running.Task; }
            catch { return null; }
        }
        if (await GetCachedPathAsync(record) is { } cached)
        {
            Log($"「{name}」({shortName}) の［読み込み］: 使えるキャッシュがあるので書き出しません ({cached})");
            return cached;
        }
        // 表示からの直接の読み込みは、CPU 使用率が高ければ下がるまで待つ (待ち行列 Pump の決まり)
        if (_cpuHigh && confirm)
        {
            ShowError(owner, $"CPU 使用率が {_cpuPercent:F0}% と高いため、書き出しを始めません。90% 未満に下がってからもう一度押してください。");
            return null;
        }
        if (save && HasForeignFile(shortName))
        {
            ShowError(owner, $"置き場所に同じ名前のファイル ({shortName}.glb / {shortName}.json) があり、このアプリが書いたものではないので書き出しません。置き場所を変えるか、そのファイルを移してください。");
            return null;
        }
        // 照合の間に別の［読み込み］が始めていれば合流する
        if (_byShort.TryGetValue(shortName, out running) && running.Task != null)
        {
            // 照合で変わっていて自動の取り直しに並んだ船なら、いちばん先にする
            PromoteRefresh(running);
            Log($"「{name}」({shortName}) の［読み込み］: 照合の間に同じ船の書き出しが始まったので、それに合流します ({running.LogTag})");
            try { return await running.Task; }
            catch { return null; }
        }
        var isItem = IsItemRecord(record);
        if (isItem)
        {
            // 形が無い品 (データだけの品) は書き出さない (Data.p4k に形のファイルが無いと Export は失敗する)
            bool has;
            try { has = await Task.Run(() => ShipExport.HasShape(P4kArchive.Shared(stamp.Path), DataCoreShared.Get(null, stamp.Path), shortName)); }
            catch (Exception ex)
            {
                Log($"「{name}」({shortName}) の形の有無を調べられませんでした: {Describe(ex)}");
                ShowError(owner, $"「{name}」の 3D モデルを調べられませんでした: {ex.Message}");
                return null;
            }
            if (!has)
            {
                _noShape.Add(shortName);
                Log($"「{name}」({shortName}) には形のデータが無いので、3D モデルは書き出しません");
                return null;
            }
            _noShape.Remove(shortName);
        }
        var job = new Ship3DJob(record, shortName, string.IsNullOrWhiteSpace(name) ? shortName : name, batch: null) { ViewOnly = !save, Kind = isItem ? Ship3DJobKind.Item : Ship3DJobKind.Ship };
        LogJob(job, $"取り込みを始めます (単体{(confirm ? "" : "、表示から直接")}{(save ? "" : "、保存しない")}、record {record}、Data.p4k {stamp.Path})");
        AddJob(job);
        job.Task = RunSingleAsync(job, stamp, owner, confirm);
        return await job.Task;
    }

    private async Task<string?> RunSingleAsync(Ship3DJob job, P4kStamp stamp, Window? owner, bool confirm = true)
    {
        var total = Stopwatch.StartNew();   // ログ用: 取り込み全体の経過
        try
        {
            job.SetPhase(Ship3DJobPhase.Estimating, "見積もり中 (ゲームデータを調べています)");
            job.LogStage = "見積もり (船の構成・参照ファイルの大きさ)";
            LogJob(job, "見積もり (船の構成・参照ファイルの大きさ) を始めます");
            var swEst = Stopwatch.StartNew();
            var est = await Ship3DEstimator.EstimateAsync(stamp, job.Record, job.Cts.Token);
            var c = Ship3DStats.Coefficients();
            job.Estimate = est;
            job.EstimatedSeconds = est.Seconds(c);
            LogJob(job, $"見積もりが終わりました: {swEst.Elapsed.TotalSeconds:F1} 秒。参照ジオメトリ {est.GeometryMb:F1} MB、照合用の参照 {est.SourcePaths.Count} 個 → " +
                        $"予想 {job.EstimatedSeconds:F0} 秒・約 {FormatBytes(est.GlbBytes(c))} (係数 {c.SecPerMb:F3} 秒/MB、大きさの倍率 {c.SizeRatio:F2})");
            if (confirm)
            {
                job.LogStage = "書き出すかの確認";
                job.SetPhase(Ship3DJobPhase.Estimating, "書き出すか確かめています");
                var msg = $"「{job.Name}」の 3D モデルをゲームデータから書き出します。\n\n" +
                          $"予想時間: 約 {FormatSeconds(job.EstimatedSeconds)}\n予想サイズ: 約 {FormatBytes(est.GlbBytes(c))}\n\n" +
                          "書き出しは裏で行います (その間もほかの操作・ほかの船の書き出しができます)。始めますか？";
                var ok = owner != null && owner.IsLoaded
                    ? MessageBox.Show(owner, msg, "3D 表示", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                    : MessageBox.Show(msg, "3D 表示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (ok != MessageBoxResult.OK)
                {
                    LogJob(job, "確認の窓で［キャンセル］が押されたので書き出しません");
                    job.SetPhase(Ship3DJobPhase.Cancelled, "");
                    return null;
                }
            }
            RefreshProgress();
            await WaitTurnAsync(job, job.Cts.Token);
            return await ExportAsync(job, stamp);
        }
        catch (OperationCanceledException)
        {
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            Log($"「{job.Name}」({job.Short}) の書き出しを取り消しました (段階: {job.LogStage}、経過 {total.Elapsed.TotalSeconds:F1} 秒)");
            return null;
        }
        catch (Exception ex)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            LogJob(job, $"失敗しました (段階: {job.LogStage}、経過 {total.Elapsed.TotalSeconds:F1} 秒): {Describe(ex)}");
            ShowError(owner, $"「{job.Name}」({job.Short}) の 3D モデルを書き出せませんでした: {ex.Message}");
            return null;
        }
        finally { RemoveJob(job); }
    }

    // ===== 自動の取り直し =====
    // (2026-11-02、利用者の決定「a だが b も」: 取り込み済みの船の 3D が古く (パッチなどで) なったら、消すのではなく、裏で自動で取り直す)
    //   いつ: 照合 (VerifyAsync) で「変わっていた」(照合できなかったを含む) になったとき。照合は今までどおり表示しようとしたとき・まとめて取り込むのほかに、
    //     起動の後 AutoRefreshDelay たってから 1 回と、Data.p4k の印が変わったときに、印の違う船を全部照合する (StartAutoRefresh。1 隻ずつ。CPU 使用率が 90% 以上の間は待つ)。
    //   取り直すまで: 古い GLB はファイルとしては残すが、表示には使わない (後で消すもの _pendingDelete に入れる。StateOf は「なし」、書き出し中の仕事があるので［読み込み］は押せず、
    //     ［詳細］の状態は仕事の文「自動の取り直しの順番待ち」・書き出し中の進み具合になる)。今開いている表示はそのまま (表示中のものは消さない今までの決まり)。
    //     取り直せたら新しい GLB で置き換える (ExportAsync が _pendingDelete から外す)。失敗したら今までどおり古いものを消す (仕事を外したとき DeletePendingIfIdle)。
    //     取り消し (アプリの終了など)・空きが足りずやめたときは古いものを消さずに残す (次の照合でまた取り直しに並ぶ)。
    //   控えめにする: 1 隻ずつ (前の船が終わってから次を始める)。書き出しは船と共通の待ち行列 (WaitTurnAsync・Pump) に乗るので、CPU 使用率 90% 以上・同時数の上限の間は待つ。
    //     順番待ちの船は進み具合の欄の「待ち N 隻」の 1 行にまとまる (仕事は並べたときに作る。［読み込み］を押した船はいちばん先にする PromoteRefresh)。
    //   変わった船が多いとき (全船 279 隻・25.9 GB の実績): 数の上限は設けず全部並べる。並べる順は GLB の更新日時の新しい順 (最近取り込んだ・見た船ほど先)。
    //     予想時間は前に書き出した時間 (横の記録の ExportSeconds)。容量は置き換えなので増えるのは新旧の差の分だけ。古いものから消す (EnforceLimit) は 1 隻ごとにはせず
    //     (まとめて取り込むと同じ。取り直し中の船を消さないため)、並べたものが全部終わったときに 1 回だけ。1 隻ごとに置き場所のドライブの空き (AutoRefreshMinFreeBytes) を見て、
    //     足りなければ残りをやめる (古いものは残す)。
    public static readonly TimeSpan AutoRefreshDelay = TimeSpan.FromSeconds(60);
    public const long AutoRefreshMinFreeBytes = 2L * 1024 * 1024 * 1024;
    private readonly List<Ship3DJob> _refreshWaiting = new();
    private readonly Dictionary<Ship3DJob, TaskCompletionSource<string?>> _refreshTcs = new();
    private readonly Dictionary<string, string> _refreshReason = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource _refreshCts = new();
    private bool _refreshRunning, _scanRunning, _scanAgain;
    // ログ用: 今回の取り直しの数 (並べたもの・取り直せたもの・失敗・取り消し)
    private int _refreshQueued, _refreshDone, _refreshFailed, _refreshCancelled;
    private readonly List<string> _refreshNames = new();
    // 今書き出している取り直し (無ければ null)。船とステーションの 3D の取り直しは合わせて 1 本ずつにする (相手のこれが null になるまで待つ)
    internal Ship3DJob? RefreshCurrent { get; private set; }

    // 照合で変わっていた船を取り直しに並べる (UI スレッド)。書き出し中 (個別・まとめて・取り直し) の船は、その書き出しが新しいものを書くので、今までどおり後で消すだけ
    private void QueueRefresh(string shortName, string reason)
    {
        var side = ReadOurSidecar(shortName);
        if (side == null || _byShort.ContainsKey(shortName)) { DeleteChangedCache(shortName, reason); return; }
        _pendingDelete.Add(shortName);
        var job = new Ship3DJob(side.Record, shortName, shortName, _refreshCts) { Kind = IsItemRecord(side.Record) ? Ship3DJobKind.Item : Ship3DJobKind.Ship, EstimatedSeconds = Math.Max(side.ExportSeconds, BaseSeconds), LogTag = "自動の取り直し" };
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Task = tcs.Task;   // 個別の［読み込み］が同じ船なら合流する
        _refreshTcs[job] = tcs;
        _refreshReason[shortName] = reason;
        AddJob(job);
        job.SetPhase(Ship3DJobPhase.Queued, "自動の取り直しの順番待ち");
        _refreshWaiting.Add(job);
        _refreshQueued++;
        _refreshNames.Add(shortName);
        LogJob(job, $"自動の取り直しに並べました ({reason})。待ち {_refreshWaiting.Count} 隻");
        RaiseStateChanged();
        if (!_refreshRunning) _ = ProcessRefreshAsync();
    }

    // 自動の取り直しの順番待ち (まだ見積もり・書き出しを始めていない) の船か。順番待ちの間は［読み込み］を押せる (押すと PromoteRefresh でいちばん先にする)
    public bool IsRefreshWaiting(string record) => _byShort.TryGetValue(ShortName(record), out var j) && _refreshWaiting.Contains(j);

    // 自動の取り直しの順番待ちの船を、いちばん先にする (［読み込み］で合流したとき)
    private void PromoteRefresh(Ship3DJob job)
    {
        var i = _refreshWaiting.IndexOf(job);
        if (i <= 0) return;
        _refreshWaiting.RemoveAt(i);
        _refreshWaiting.Insert(0, job);
        LogJob(job, "［読み込み］が押されたので、自動の取り直しの順番をいちばん先にしました");
    }

    // 並べた船を 1 隻ずつ取り直す (UI スレッド)
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
            Log($"自動の取り直し: {_refreshQueued} 隻 (取り直した {_refreshDone}、失敗して消した {_refreshFailed}、やめた (古いものを残した) {_refreshCancelled}。" +
                $"{sw.Elapsed.TotalSeconds:F1} 秒): {string.Join(", ", _refreshNames)}");
            _refreshQueued = _refreshDone = _refreshFailed = _refreshCancelled = 0;
            _refreshNames.Clear();
            // 古いものから消すのは、並べたものが全部終わったときに 1 回だけ (説明は上)
            if (lastDone != null) EnforceLimit(justExported: lastDone);
        }
    }

    // 取り直し 1 隻: 見積もり (確認の窓は出さない) → 順番を待つ → 書き出す。取り直せたら true
    private async Task<bool> RunRefreshJobAsync(Ship3DJob job)
    {
        var tcs = _refreshTcs[job];
        _refreshTcs.Remove(job);
        var keepOld = false;
        try
        {
            job.Cts.Token.ThrowIfCancellationRequested();
            if (DriveOf(Folder) is { } d && d.Free < AutoRefreshMinFreeBytes)
            {
                LogJob(job, $"置き場所のドライブ {d.Root} の空きが {FormatBytes(d.Free)} しかないので、自動の取り直しをやめます (古いものは残します。{FormatBytes(AutoRefreshMinFreeBytes)} 以上要ります)");
                // 残りも同じなのでやめる
                foreach (var j in _refreshWaiting.ToList()) j.Cancel();
                keepOld = true;
                job.SetPhase(Ship3DJobPhase.Cancelled, "");
                _refreshCancelled++;
                tcs.TrySetResult(null);
                return false;
            }
            var stamp = CurrentStamp() ?? throw new OperationCanceledException("ゲームの Data.p4k が見つかりません");
            job.SetPhase(Ship3DJobPhase.Estimating, "自動の取り直し: 見積もり中");
            job.LogStage = "見積もり (船の構成・参照ファイルの大きさ)";
            var est = await Ship3DEstimator.EstimateAsync(stamp, job.Record, job.Cts.Token);
            var c = Ship3DStats.Coefficients();
            job.Estimate = est;
            job.EstimatedSeconds = est.Seconds(c);
            LogJob(job, $"自動の取り直しを始めます ({(_refreshReason.TryGetValue(job.Short, out var why) ? why : "")})。予想 {job.EstimatedSeconds:F0} 秒・約 {FormatBytes(est.GlbBytes(c))}");
            RefreshCurrent = job;
            await WaitTurnAsync(job, job.Cts.Token);
            var path = await ExportAsync(job, stamp);
            _refreshDone++;
            tcs.TrySetResult(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            keepOld = true;
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            _refreshCancelled++;
            LogJob(job, $"自動の取り直しをやめました (段階: {(job.LogStage.Length > 0 ? job.LogStage : "順番待ち")})。古いものは残します (次の照合でまた並びます)");
            tcs.TrySetResult(null);
            return false;
        }
        catch (Exception ex)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            _refreshFailed++;
            LogJob(job, $"自動の取り直しが失敗しました (段階: {job.LogStage})。古いものは消します: {Describe(ex)}");
            tcs.TrySetResult(null);
            return false;
        }
        finally
        {
            // 取り消し・やめたときは古いものを残す (後で消すものから外す)。失敗のときは残したまま仕事を外す → DeletePendingIfIdle で消える (今までの照合の扱いと同じ)
            if (RefreshCurrent == job) RefreshCurrent = null;
            if (keepOld) _pendingDelete.Remove(job.Short);
            _refreshReason.Remove(job.Short);
            RemoveJob(job);
        }
    }

    // 印の違う船を全部照合する (UI スレッド)。変わっていた船は照合が取り直しに並べる (QueueRefresh)。動いている間に頼まれたら、終わってからもう 1 回
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
                var stamp = CurrentStamp();
                if (stamp == null) break;
                // 横の記録の Data.p4k の印が今と違う船 (書き出し中・照合中・後で消すものは除く)。GLB の更新日時の新しい順
                var cands = await Task.Run(() => CachedFiles()
                    .Select(f => (File: f, Short: Path.GetFileNameWithoutExtension(f.Name), Side: ReadOurSidecar(Path.GetFileNameWithoutExtension(f.Name))))
                    .Where(x => x.Side != null && !(string.Equals(x.Side.P4kPath, stamp.Path, StringComparison.OrdinalIgnoreCase)
                        && x.Side.P4kSize == stamp.Size && x.Side.P4kWriteUtcTicks == stamp.WriteUtcTicks))
                    .OrderByDescending(x => x.File.LastWriteTimeUtc).Select(x => (x.Short, Record: x.Side!.Record)).ToList());
                cands = cands.Where(x => !_byShort.ContainsKey(x.Short) && !_verifying.ContainsKey(x.Short) && !_pendingDelete.Contains(x.Short)).ToList();
                if (cands.Count == 0) continue;
                Log($"自動の取り直し ({why}): Data.p4k の印が違う船 {cands.Count} 隻を 1 隻ずつ照合します (変わっていた船は取り直しに並べます)");
                var sw = Stopwatch.StartNew();
                int same = 0, changed = 0;
                foreach (var (sn, rec) in cands)
                {
                    // CPU 使用率が 90% 以上の間は待つ (照合は船の構成を読み直すため)
                    while (_cpuHigh) await Task.Delay(1000);
                    if (StateOf(rec) != Ship3DCacheState.NeedsVerify) continue;   // 待つ間に照合・取り込みが始まったもの
                    if (await VerifyAsync(rec)) same++; else changed++;
                }
                Log($"自動の取り直し ({why}): 照合が終わりました: 変わっていなかった {same} 隻、変わっていた {changed} 隻 (取り直しに並べました)、{sw.Elapsed.TotalSeconds:F1} 秒");
            } while (_scanAgain);
        }
        catch (Exception e) { Log($"自動の取り直し ({why}) の照合が失敗しました: {Describe(e)}"); }
        finally { _scanRunning = false; }
    }


    // ===== 書き出し本体 =====

    // 順番が来た書き出しを動かす (WaitTurnAsync の後。_running に入っている)。終わったら GLB のパス。
    //   一時ファイル (置き場所の作業用のフォルダ) に書いてから名前を変える。読み取りの選んだ record (starbreaker と同じ選び方) が求めるものと違えば失敗 (GLB は消す)。
    //   保存しない (job.ViewOnly) ときは、一時ファイルのまま表示に使い、横の記録・実績は書かない (表示をやめたら消す)。
    //   同じ名前でこのアプリのものではないファイルがあれば上書きしない。
    //   終わったら、横の記録 (印・Data.p4k の印・照合用の元ファイルの一覧と Data.p4k に記録された CRC・--dump-hierarchy のハッシュ) を書き、
    //   実績を記録し (並行で動いていなければ)、最大容量を超えていれば古いものから消す
    private async Task<string> ExportAsync(Ship3DJob job, P4kStamp stamp)
    {
        var ct = job.Cts.Token;
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(WorkFolder);
        var tmp = NewWorkFile(job.Short, ".glb");
        var keepTmp = false;
        job.Tracker = new Ship3DStageTracker();
        job.SetPhase(Ship3DJobPhase.Exporting, "");
        job.Watch.Restart();
        try
        {
            // 段階の行 (Assembling・children k/N・interiors done 等) は starbreaker -v と同じ形で出る (Ship3DStageTracker)
            job.LogStage = "書き出し (アプリの中の読み取り)";
            LogJob(job, $"書き出しを始めます (アプリの中の読み取り): 予想 {job.EstimatedSeconds:F0} 秒");
            var res = await Task.Run(() =>
            {
                var p4k = P4kArchive.Shared(stamp.Path);
                var db = DataCoreShared.Get(null, stamp.Path);
                return ShipExport.Export(p4k, db, job.Short, tmp, line => job.Tracker.OnLine(line, job.Watch.Elapsed.TotalSeconds), ct);
            }, ct);
            job.Watch.Stop();
            LogJob(job, $"書き出しが終わりました: {job.Watch.Elapsed.TotalSeconds:F1} 秒、最後の段階 [{job.Tracker.StageText}]、選ばれた record {res.ResolvedName}、GLB {FormatBytes(res.GlbBytes)}");
            // 求める record と違う船を選んだら失敗 (starbreaker と同じく、名前を含むもののうち名前の短いもの)
            if (!string.Equals(ShortName(res.ResolvedName), job.Short, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"読み取りが別の船 ({res.ResolvedName}) を選びました。求める record は {job.Record} です。書き出した GLB は使いません");
            if (!File.Exists(tmp)) throw new InvalidOperationException("書き出しは終わりましたが GLB ができていません");
            // (2026-11-04、利用者の決定「取り直しなら消す」) 自前の読み取りで書き出せたので、手元に残っている starbreaker.exe を消す
            RemoveOldStarBreaker();
            if (job.ViewOnly)
            {
                // 保存しない: 一時ファイルのまま表示に使う (表示をやめたら消す。DeleteViewFileIfIdle)
                keepTmp = true;
                if (_viewFiles.Remove(job.Short, out var old)) { try { File.Delete(old); } catch { } }
                _viewFiles[job.Short] = tmp;
                Log($"「{job.Name}」({job.Short}) を直接読みました (保存しない設定のため、表示の間だけ置きます): {job.Watch.Elapsed.TotalSeconds:F1} 秒、{FormatBytes(res.GlbBytes)}");
                job.SetPhase(Ship3DJobPhase.Done, "");
                _running.Remove(job);
                RaiseStateChanged();
                Exported?.Invoke(job.Short);
                return tmp;
            }

            // 照合用の元ファイルの一覧 (Data.p4k の中央ディレクトリの大きさと記録された CRC)
            job.SetPhase(Ship3DJobPhase.Exporting, "元データの記録");
            job.LogStage = "元データの記録";
            var swSrc = Stopwatch.StartNew();
            var est = job.Estimate ?? throw new InvalidOperationException("見積もり (船の構成) がありません");
            var index = await Ship3DP4kIndex.GetAsync(stamp);
            var sources = await Task.Run(() => Ship3DEstimator.BuildSources(est.SourcePaths, tmp, index), ct);
            var srcSeconds = swSrc.Elapsed.TotalSeconds;
            job.LogStage = "保存";
            var swSave = Stopwatch.StartNew();

            // 同じ名前でこのアプリのものではないファイルがあれば上書きしない (書き出しの間に置かれた場合も)
            if (HasForeignFile(job.Short))
                throw new InvalidOperationException($"置き場所に同じ名前のファイル ({job.Short}.glb / {job.Short}.json) があり、このアプリが書いたものではないので上書きしません。置き場所を変えるか、そのファイルを移してください");
            var final = GlbPath(job.Short);
            File.Move(tmp, final, overwrite: true);
            _pendingDelete.Remove(job.Short);   // 書き出し直したので、前のものを後で消す予定は取りやめ
            var glbBytes = new FileInfo(final).Length;
            WriteSidecar(job.Short, new Sidecar
            {
                App = SidecarMark,
                Record = job.Record,
                P4kPath = stamp.Path,
                P4kSize = stamp.Size,
                P4kWriteUtcTicks = stamp.WriteUtcTicks,
                ExportedUtc = DateTime.UtcNow.ToString("o"),
                ExportSeconds = Math.Round(job.Watch.Elapsed.TotalSeconds, 1),
                GeometryMb = Math.Round(est.GeometryMb, 1),
                HierarchyHash = est.HierarchyHash,
                Sources = sources,
            });
            Log($"「{job.Name}」({job.Short}) を書き出しました: {job.Watch.Elapsed.TotalSeconds:F1} 秒、{FormatBytes(glbBytes)}" +
                $" (予想 {job.EstimatedSeconds:F0} 秒、参照ジオメトリ {est.GeometryMb:F1} MB、照合用の元ファイル {sources.Count} 個" +
                $"{(job.Overlapped ? "、並行で動いたので係数の実績には使いません" : "")})" +
                $"。元データの記録 {srcSeconds:F1} 秒、保存 {swSave.Elapsed.TotalSeconds:F1} 秒 ({job.LogTag})");
            if (!job.Overlapped && job.Kind == Ship3DJobKind.Ship) Ship3DStats.Add(job.Short, est.GeometryMb, job.Watch.Elapsed.TotalSeconds, glbBytes);
            job.SetPhase(Ship3DJobPhase.Done, "");
            _running.Remove(job);
            // まとめて取り込むの中では古いものを消さない (利用者の決定。保有船を優先した意味が無くなるため)。
            // 予想より大きくて最大容量を少し超えることはある (次の個別の書き出しのときに古いものから消える)
            if (!job.InBatch) EnforceLimit(justExported: job.Short);
            RaiseStateChanged();
            Exported?.Invoke(job.Short);
            return final;
        }
        finally
        {
            job.Watch.Stop();
            _running.Remove(job);
            if (!keepTmp) try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    // 手元に残っている starbreaker.exe (前の版がダウンロードしたもの: 作業フォルダの tools\starbreaker と、アプリのフォルダの tools\starbreaker) を消す。
    // フォルダは空になったときだけ消す。1 回の起動で 1 回だけ試す
    private static bool _oldStarBreakerChecked;
    private static void RemoveOldStarBreaker()
    {
        if (_oldStarBreakerChecked) return;
        _oldStarBreakerChecked = true;
        foreach (var dir in new[] { Path.Combine(DetailPanel.WorkDir(), "tools", "starbreaker"), Path.Combine(AppContext.BaseDirectory, "tools", "starbreaker") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var exe = Path.Combine(dir, "starbreaker.exe");
                if (!File.Exists(exe)) continue;
                File.Delete(exe);
                Log($"使わなくなった starbreaker.exe を消しました ({exe})");
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) { Log($"使わなくなった starbreaker.exe を消せませんでした ({dir}): {ex.Message}"); }
        }
    }

    // 最大容量を超えていれば、書き出した日時 (GLB の更新日時) の古いものから消す (アプリの印のあるものだけ)。
    // 表示中・書き出し中 (今動いている・待っている船)・今書き出したものは消さない。消しても超えたままなら知らせる
    // (2026-11-04、利用者の指示でステーションの 2D の地図・古い方式のステーションの 3D を消したので、船の分だけを数えて消す)
    internal void EnforceLimit(string justExported)
    {
        var max = MaxMb * MB;
        var files = CachedFiles();
        long total = files.Sum(f => f.Length);
        if (total <= max) return;
        var protect = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { justExported };
        foreach (var v in _views.Keys) protect.Add(v);
        foreach (var j in Jobs) protect.Add(j.Short);
        var deleted = new List<string>();
        foreach (var (f, key) in files.Select(f => (File: f, Key: Path.GetFileNameWithoutExtension(f.Name))).Where(e => !protect.Contains(e.Key)).OrderBy(e => e.File.LastWriteTimeUtc))
        {
            if (total <= max) break;
            var shortName = key;
            try
            {
                if (ReadOurSidecar(shortName) == null) continue;
                // 大きさは消す前に取る (消した後の FileInfo.Length は FileNotFoundException になる)
                var length = f.Length;
                f.Delete();
                try { File.Delete(SidecarPath(shortName)); } catch { }
                total -= length;
                deleted.Add(shortName);
            }
            catch (Exception ex) { Log($"最大容量を超えたので {f.Name} を消そうとしましたが、消せませんでした: {ex.Message}"); }
        }
        if (deleted.Count > 0) Log($"最大容量 ({MaxMb:N0} MB) を超えたので、古いものから {deleted.Count} 件を消しました: {string.Join(", ", deleted)}");
        if (total > max)
        {
            var left = CachedFiles();
            var text = left.Count == 1
                ? $"「{Path.GetFileNameWithoutExtension(left[0].Name)}」1 件だけで最大容量 ({MaxMb:N0} MB) を超えています ({FormatBytes(left[0].Length)})。消さずに残します"
                : $"最大容量 ({MaxMb:N0} MB) を超えていますが、表示中・書き出し中のものを除くと消せるものがありません (今 {FormatBytes(total)})";
            Notice = text;
            Log(text);
        }
        else if (deleted.Count > 0) Notice = $"最大容量を超えたので、古いものから {deleted.Count} 件を消しました";
    }

    // ===== まとめて取り込む =====

    private bool _batchActive;
    private string _batchText = "";
    private CancellationTokenSource? _batchCts;
    // 進み具合の欄の［まとめて取り消し］の行を出すか: 船のまとめて取り込むが動いている間
    public bool BatchActive => _batchActive;
    // 船のまとめて取り込むが動いているか (船の［まとめて取り込む］のボタンに使う)
    internal bool ShipBatchActive => _batchActive;
    // まとめて取り込むの今の段階 (見積もり中 i / N 等)
    public string BatchText
    {
        get => _batchText;
        private set { _batchText = value; Changed(nameof(BatchText)); }
    }

    // ［まとめて取り消し］: 船のまとめて取り込むを取り消す
    public void CancelBatch()
    {
        _batchCts?.Cancel();
    }

    private void SetBatchActive(bool active)
    {
        _batchActive = active;
        Changed(nameof(BatchActive));
        Changed(nameof(ShowPanel));
    }

    // 事前取り込み: 保有船 (owned) を先に、その後に残りの船 (rest) を書き出す。
    //   始める前にゲームデータを見て各船の予想 (時間・サイズ) を出し、予想サイズの合計が「最大容量 − 今のキャッシュの大きさ」を超える手前で止める
    //   (まとめてでは古いものを消して入れ替えない)。すでに最新のキャッシュがある船 (Data.p4k が変わっていれば照合して同じだった船)・
    //   今書き出し中の船は飛ばす。何隻・約何分・約何 GB かを聞いてから、並行に書き出す (同時数は MaxParallelExports。始めるのは並んだ順)
    public async Task RunBatchAsync(IReadOnlyList<(string Record, string Name)> owned, IReadOnlyList<(string Record, string Name)> rest, Window? owner)
    {
        if (_batchActive) { ShowError(owner, "船舶・車両をまとめて取り込むはすでに動いています。", "まとめて取り込むはすでに動いています。"); return; }
        if (_cpuHigh)
        {
            ShowError(owner, $"CPU 使用率が {_cpuPercent:F0}% と高いため、書き出しを始めません。90% 未満に下がってからもう一度押してください。");
            return;
        }
        var stamp = CurrentStamp();
        if (stamp == null) { ShowError(owner, "ゲームの Data.p4k が見つかりません。設定タブのゲームのフォルダとチャンネルを確かめてください。"); return; }

        _batchCts = new CancellationTokenSource();
        var ct = _batchCts.Token;
        SetBatchActive(true);
        var failures = new List<string>();
        // ログ用: 全体の経過・今の段階・飛ばした船の名前 (理由ごと)
        var swBatch = Stopwatch.StartNew();
        var batchStage = "見積もりの用意";
        var skippedFresh = new List<string>();
        var skippedRunning = new List<string>();
        var skippedForeign = new List<string>();
        var notTakenNames = new List<string>();
        var batchJobs = new List<Ship3DJob>();
        // 対象の record の一覧 (BatchTargetRecord に書く。設定タブのボタンの「全部取り込み済み」の判定に使う)
        List<string>? batchTargets = null;
        Log($"まとめて取り込むを始めます: 保有船 {owned.Count} 隻、残りの船 {rest.Count} 隻 (Data.p4k {stamp.Path}、最大容量 {MaxMb:N0} MB、" +
            $"今の使用量 {FormatBytes(CacheBytes())})");
        try
        {
            // 並び: 保有船 → 残り (同じ record は最初の 1 つ)
            var list = new List<(string Record, string Short, string Name, bool Owned)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (r, n) in owned)
                if (!string.IsNullOrWhiteSpace(r) && seen.Add(ShortName(r))) list.Add((r, ShortName(r), n, true));
            foreach (var (r, n) in rest)
                if (!string.IsNullOrWhiteSpace(r) && seen.Add(ShortName(r))) list.Add((r, ShortName(r), n, false));
            batchTargets = list.Select(s => s.Record).ToList();
            BatchTargetRecord.Write(BatchTargetRecord.Ships, stamp, batchTargets, StateOf);

            var c = Ship3DStats.Coefficients();
            double sumBytes = 0, sumSeconds = 0;
            int skipFresh = 0, skipRunning = 0, skipForeign = 0, notTaken = 0, ownedCount = 0;
            var selected = new List<(string Record, string Short, string Name, Ship3DEstimate Est)>();
            // 1. 飛ばす船を決める (照合が要る船は順に照合する)
            batchStage = "飛ばす船を決める (照合)";
            var swSkip = Stopwatch.StartNew();
            var candidates = new List<(string Record, string Short, string Name, bool Owned)>();
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = list[i];
                if (_byShort.ContainsKey(s.Short)) { skipRunning++; skippedRunning.Add(s.Short); continue; }
                var state = StateOf(s.Record);
                if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying)
                {
                    BatchText = $"船舶・車両をまとめて取り込む: 照合中 {i + 1} / {list.Count} 隻目 ({s.Name})";
                    if (await VerifyAsync(s.Record)) { skipFresh++; skippedFresh.Add(s.Short); continue; }
                    // 照合で変わっていた船は自動の取り直しに並んだ (QueueRefresh)。その取り直しが書き出すので、見積もりの前に外す (書き出し中と同じ扱い)
                    if (_byShort.ContainsKey(s.Short)) { skipRunning++; skippedRunning.Add(s.Short); continue; }
                }
                else if (state == Ship3DCacheState.Fresh) { skipFresh++; skippedFresh.Add(s.Short); continue; }
                if (HasForeignFile(s.Short)) { skipForeign++; skippedForeign.Add(s.Short); continue; }
                candidates.Add(s);
            }
            Log($"まとめて取り込む: 飛ばす船を決めました ({swSkip.Elapsed.TotalSeconds:F1} 秒): 全 {list.Count} 隻のうち、見積もる船 {candidates.Count} 隻、" +
                $"最新のキャッシュがある {skipFresh} 隻、書き出し中 {skipRunning} 隻、このアプリのものではない同名のファイル {skipForeign} 隻");
            batchStage = "見積もり";
            var swEstAll = Stopwatch.StartNew();

            // 2. 見積もり (--dump-hierarchy。1 隻 約 2 秒) を同時数の上限 (MaxParallelExports) まで並行に、並んだ順に始める
            //    (1 隻ずつだと 278 隻で 9 分ほどかかり、その間は確認の窓も出なかった。利用者の指摘)。
            //    結果は並んだ順に見て、予想サイズの合計が最大容量を超える手前で止め、残りの見積もりは取り消す。
            //    続きの処理は UI スレッドの上で動く (await の後もこのスレッドに戻る) ので、_estimates などは同時に触られない
            var estLimit = MaxParallelExports().Limit;
            using var gate = new SemaphoreSlim(estLimit);
            using var estCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            async Task<Ship3DEstimate> EstimateGatedAsync(string record)
            {
                await gate.WaitAsync(estCts.Token);
                try { return await Ship3DEstimator.EstimateAsync(stamp, record, estCts.Token); }
                finally { gate.Release(); }
            }
            var estTasks = candidates.Select(s => EstimateGatedAsync(s.Record)).ToList();
            try
            {
                for (int k = 0; k < candidates.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = candidates[k];
                    BatchText = $"船舶・車両をまとめて取り込む: 見積もり中 {k + 1} / {candidates.Count} 隻目 (同時 {estLimit} 本。{s.Name})";
                    Ship3DEstimate est;
                    try { est = await estTasks[k]; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        failures.Add($"{s.Name} ({s.Short}): 見積もれませんでした: {ex.Message}");
                        Log($"まとめて取り込む: 「{s.Name}」({s.Short}) を見積もれませんでした: {ex.Message}");
                        continue;
                    }
                    var bytes = est.GlbBytes(c);
                    // 予想サイズの合計が最大容量を超える手前で止める (使用量は照合で消えた分を反映するため毎回測る)
                    if (sumBytes + bytes > MaxMb * MB - CacheBytes())
                    {
                        notTaken = candidates.Count - k;
                        notTakenNames.AddRange(candidates.Skip(k).Select(x => x.Short));
                        break;
                    }
                    sumBytes += bytes;
                    sumSeconds += est.Seconds(c);
                    if (s.Owned) ownedCount++;
                    selected.Add((s.Record, s.Short, s.Name, est));
                }
            }
            finally
            {
                // 使わなかった見積もりを取り消し、starbreaker が終わるのを待つ (失敗・取り消しの例外はここでは見ない)
                estCts.Cancel();
                try { await Task.WhenAll(estTasks); } catch { }
            }
            Log($"まとめて取り込む: 見積もりが終わりました ({swEstAll.Elapsed.TotalSeconds:F1} 秒、同時 {estLimit} 本): 書き出す船 {selected.Count} 隻 (うち保有船 {ownedCount} 隻)、" +
                $"予想 合計 {sumSeconds:F0} 秒・約 {FormatBytes(sumBytes)}、最大容量を超えるので取り込まない {notTaken} 隻、見積もれなかった {failures.Count} 隻");
            batchStage = "始めるかの確認";

            var parallel = MaxParallelExports().Limit;
            var head = $"船舶・車両をまとめて取り込む ({list.Count} 隻のうち)\n\n";
            var info = (skipFresh > 0 ? $"最新のキャッシュがあるので飛ばす: {skipFresh} 隻\n" : "") +
                       (skipRunning > 0 ? $"今書き出し中なので飛ばす: {skipRunning} 隻\n" : "") +
                       (skipForeign > 0 ? $"置き場所に同じ名前のこのアプリのものではないファイルがあるので飛ばす: {skipForeign} 隻\n" : "") +
                       (notTaken > 0 ? $"最大容量 ({MaxMb:N0} MB。今の使用量 {FormatBytes(CacheBytes())}、残り {FormatBytes(Math.Max(0, MaxMb * MB - CacheBytes()))}) を超えるので取り込まない: {notTaken} 隻\n" +
                                       "　→ 全部取り込むには、設定タブの「3D モデル」の最大容量を上げてください (全船 279 隻で約 25.9 GB。2026-10 の LIVE での作者の PC の実測)\n" : "") +
                       (failures.Count > 0 ? $"見積もれなかった: {failures.Count} 隻 (アプリのログに記録)\n" : "");
            if (selected.Count == 0)
            {
                Log("まとめて取り込む: 書き出す船がないので終わります。" + LogSkipLists(skippedFresh, skippedRunning, skippedForeign, notTakenNames, failures));
                MessageBox.Show(head + "書き出す船がありません。\n\n" + info, "3D モデル", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var minutes = sumSeconds / Math.Max(1, parallel) / 60;
            var ask = head +
                      $"書き出す船: {selected.Count} 隻 (うち保有船 {ownedCount} 隻)\n" +
                      $"予想時間: 約 {Math.Max(1, Math.Ceiling(minutes)):F0} 分 (1 隻ずつなら合計 約 {Math.Ceiling(sumSeconds / 60):F0} 分。同時 {parallel} 本までで見込んだもの)\n" +
                      $"予想サイズ: 約 {sumBytes / (1024.0 * MB):F1} GB\n\n" + info +
                      "\n始めますか？ (進み具合は［詳細］タブと設定タブの 3D モデルの欄に出ます)";
            var ok = owner != null && owner.IsLoaded
                ? MessageBox.Show(owner, ask, "3D モデル", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                : MessageBox.Show(ask, "3D モデル", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK)
            {
                Log("まとめて取り込む: 確認の窓で［キャンセル］が押されたので書き出しません");
                return;
            }

            // 船ごとの書き出しを全部並べ (全体のバーの分母に入る)、並んだ順に始める
            batchStage = "書き出し";
            BatchText = $"船舶・車両をまとめて取り込む: {selected.Count} 隻";
            var queuedBefore = _queue.Count;
            var tasks = new List<Task<bool>>();
            var no = 0;
            foreach (var s in selected)
            {
                no++;
                if (_byShort.ContainsKey(s.Short)) { skipRunning++; skippedRunning.Add(s.Short); continue; }   // 見積もりの間に個別で始めた船
                var job = new Ship3DJob(s.Record, s.Short, s.Name, _batchCts) { Estimate = s.Est, EstimatedSeconds = s.Est.Seconds(c), LogTag = $"まとめての {no} / {selected.Count} 隻目" };
                AddJob(job);
                batchJobs.Add(job);
                tasks.Add(RunBatchJobAsync(job, stamp, failures));
            }
            Log($"まとめて取り込む: {tasks.Count} 隻を待ち行列に並べました (前からの待ち {queuedBefore} 件の後ろ。動いている {_running.Count} 本、同時数の上限 {parallel} 本)");
            var results = await Task.WhenAll(tasks);
            var done = results.Count(r => r);
            var cancelled = ct.IsCancellationRequested;
            // 窓はボタンの名前 (［船舶・車両をまとめて取り込む］) で出し、ログは前のログと比べられるよう前の文言のまま
            var result = $"{(cancelled ? "取り消しました" : "終わりました")}。書き出した船 {done} / {tasks.Count} 隻" +
                         (failures.Count > 0 ? $"、失敗・見積もれなかった船 {failures.Count} 隻" : "");
            var summary = "船舶・車両をまとめて取り込む: " + result;
            Log("まとめて取り込む: " + result + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : ""));
            Log($"まとめて取り込む: 全体 {swBatch.Elapsed.TotalSeconds:F1} 秒。書き出した {done} 隻、失敗 {batchJobs.Count(j => j.Phase == Ship3DJobPhase.Failed)} 隻、" +
                $"取り消し {batchJobs.Count(j => j.Phase == Ship3DJobPhase.Cancelled)} 隻 (全 {list.Count} 隻のうち並べた {tasks.Count} 隻)。" +
                LogSkipLists(skippedFresh, skippedRunning, skippedForeign, notTakenNames, null));
            MessageBox.Show(summary + (failures.Count > 0 ? "\n\n" + string.Join("\n", failures.Take(15)) + (failures.Count > 15 ? "\n…(続きはアプリのログ)" : "") : ""),
                "3D モデル", MessageBoxButton.OK, failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log($"まとめて取り込むを取り消しました (段階: {batchStage}、経過 {swBatch.Elapsed.TotalSeconds:F1} 秒)");
            MessageBox.Show("船舶・車両をまとめて取り込むを取り消しました。", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"まとめて取り込むが失敗しました (段階: {batchStage}、経過 {swBatch.Elapsed.TotalSeconds:F1} 秒): {Describe(ex)}");
            ShowError(owner, $"船舶・車両をまとめて取り込むが失敗しました: {ex.Message}", $"まとめて取り込むが失敗しました: {ex.Message}");
        }
        finally
        {
            // 終わったときの取り込んだ数・取り込んでいない数で書き直す (ボタンの判定は SetBatchActive(false) の知らせで決め直す)
            if (batchTargets != null) BatchTargetRecord.Write(BatchTargetRecord.Ships, stamp, batchTargets, StateOf);
            BatchText = "";
            _batchCts?.Dispose();
            _batchCts = null;
            SetBatchActive(false);
        }
    }

    // まとめての中の 1 隻 (順番を待ってから書き出す)。書き出せたら true。失敗は failures に足す (知らせは最後にまとめて)
    private async Task<bool> RunBatchJobAsync(Ship3DJob job, P4kStamp stamp, List<string> failures)
    {
        var tcs = new TaskCompletionSource<string?>();
        job.Task = tcs.Task;   // 個別の［読み込み］が同じ船なら合流する
        try
        {
            await WaitTurnAsync(job, job.Cts.Token);
            var path = await ExportAsync(job, stamp);
            tcs.TrySetResult(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 順番待ちのまま取り消されたものは 1 件ずつ出さない (数はまとめての終わりの行に出す)
            if (job.LogStage != "順番待ち") LogJob(job, $"書き出しを取り消しました ({job.LogTag}、段階: {job.LogStage})");
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            tcs.TrySetResult(null);
            return false;
        }
        catch (Exception ex)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            failures.Add($"{job.Name} ({job.Short}): {ex.Message}");
            Log($"まとめて取り込む: 「{job.Name}」({job.Short}) を書き出せませんでした ({job.LogTag}、段階: {job.LogStage}): {Describe(ex)}");
            tcs.TrySetResult(null);
            return false;
        }
        finally { RemoveJob(job); }
    }

    // ログ用: 飛ばした・取り込まなかった・失敗したものの一覧 (理由ごとに 1 つの文。空の理由は出さない)
    internal static string LogSkipLists(IReadOnlyCollection<string> fresh, IReadOnlyCollection<string> running, IReadOnlyCollection<string> foreign,
        IReadOnlyCollection<string> notTaken, IReadOnlyCollection<string>? failures)
    {
        var parts = new List<string>();
        if (fresh.Count > 0) parts.Add($"最新なので飛ばした {fresh.Count} 件: {string.Join(", ", fresh)}");
        if (running.Count > 0) parts.Add($"取り込み中なので飛ばした {running.Count} 件: {string.Join(", ", running)}");
        if (foreign.Count > 0) parts.Add($"このアプリのものではない同名のファイルがあるので飛ばした {foreign.Count} 件: {string.Join(", ", foreign)}");
        if (notTaken.Count > 0) parts.Add($"最大容量を超えるので取り込まなかった {notTaken.Count} 件: {string.Join(", ", notTaken)}");
        if (failures is { Count: > 0 }) parts.Add($"失敗・見積もれなかった {failures.Count} 件: {string.Join(" / ", failures)}");
        return parts.Count == 0 ? "飛ばしたもの・失敗したものはありません" : string.Join("。", parts);
    }

    // ===== Win32 =====

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

// 船のキャッシュの状態: ゲーム (Data.p4k) が見つからない / キャッシュ無し / 最新 / Data.p4k が変わったので照合が要る / 照合中
public enum Ship3DCacheState { NoGame, None, Fresh, NeedsVerify, Verifying }

// まとめて取り込むの対象の記録 (設定タブの［船舶・車両をまとめて取り込む］［マップをまとめて取り込む］を、今の Data.p4k で全部取り込み済みなら使えなくするため。
// 利用者の決定 (2026-11-01): 対象の一覧はゲームデータ・DB から作り直すと重い (マップは p4k を開いて socpak を辿る) ので、まとめて取り込むが作った一覧を覚えておく)。
//   まとめて取り込むが対象の一覧を作ったときと終わったときに、置き場所 (Ship3DExports.Folder) の直下の batch_ships.json / batch_maps.json に書く:
//   Data.p4k の印 (P4kStamp.Key)・対象の record の一覧・書いたときの取り込んだ数 (StateOf が最新のもの) と取り込んでいない数。
//   ボタンの判定 (MainWindow.Ship3D.cs) は、これを読み、印が今の Data.p4k と同じなら、各 record の StateOf が最新 (Fresh) かを数え直す。
//   船のキャッシュ (*.glb と横の記録) にもステーションの地図 (stations\ の印のある *.json) にも数えられない名前・場所に置く
internal static class BatchTargetRecord
{
    public const string Ships = "batch_ships.json";
    public const string Maps = "batch_maps.json";

    internal sealed class Content
    {
        // 書いたときの Data.p4k の印 (P4kStamp.Key)
        public string P4k { get; set; } = "";
        public string WrittenUtc { get; set; } = "";
        // 書いたときの取り込んだ数・取り込んでいない数 (ボタンの判定では使わず、今の StateOf で数え直す)
        public int Done { get; set; }
        public int NotDone { get; set; }
        public List<string> Records { get; set; } = new();
    }

    private static string PathOf(string fileName) => Path.Combine(Ship3DExports.Folder, fileName);

    // 書く (UI スレッドで呼ぶ。stateOf は Ship3DExports の StateOf)。書けなくてもまとめて取り込むは止めない (ログに残すだけ)
    public static void Write(string fileName, Ship3DExports.P4kStamp stamp, IReadOnlyList<string> records, Func<string, Ship3DCacheState> stateOf)
    {
        try
        {
            var done = records.Count(r => stateOf(r) == Ship3DCacheState.Fresh);
            var c = new Content { P4k = stamp.Key, WrittenUtc = DateTime.UtcNow.ToString("o"), Done = done, NotDone = records.Count - done, Records = records.ToList() };
            Directory.CreateDirectory(Ship3DExports.Folder);
            var path = PathOf(fileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] まとめて取り込むの対象の記録 {fileName} を書けませんでした: {ex.Message}"); }
    }

    // 読む。無い・読めないときは null
    public static Content? Read(string fileName)
    {
        try
        {
            var path = PathOf(fileName);
            if (!File.Exists(path)) return null;
            var c = JsonSerializer.Deserialize<Content>(File.ReadAllText(path));
            return c == null || c.P4k.Length == 0 || c.Records == null ? null : c;
        }
        catch { return null; }
    }
}

// Data.p4k の中央ディレクトリ (全エントリの大きさと記録された CRC)。照合と元ファイルの記録に使う。Data.p4k ごとに 1 回読んで使い回す。
// ZIP の中央ディレクトリを自分で読む (System.IO.Compression.ZipArchive でも読めるが、約 138 万件のエントリを全部オブジェクトにして
// 作業メモリが約 1GB になるため)。キーは p4k の中のパスを正規化したもの (Ship3DEstimator.Normalize) のハッシュ。
// 実データで確かめたこと (LIVE の Data.p4k、2026-10-29): エントリ 1,379,308 件、中央ディレクトリ 447MB、記録された CRC が 0 のものは 63 件だけ。
// この読み方の大きさ・記録された CRC は ZipArchive (ZipArchiveEntry.Length / Crc32) と全件一致。読むのに約 0.9 秒 (パスの文字列を持つ版で作業メモリ最大約 340MB)
internal static class Ship3DP4kIndex
{
    private static readonly object Lock = new();
    private static string? _key;
    private static Task<Dictionary<ulong, (long Size, uint Crc)>>? _task;

    public static Task<Dictionary<ulong, (long Size, uint Crc)>> GetAsync(Ship3DExports.P4kStamp stamp)
    {
        lock (Lock)
        {
            if (_key == stamp.Key && _task != null && !_task.IsFaulted && !_task.IsCanceled) return _task;
            _key = stamp.Key;
            _task = Task.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                var d = Read(stamp.Path);
                App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] Data.p4k の中央ディレクトリを読みました: {d.Count:N0} 件、{sw.Elapsed.TotalSeconds:F1} 秒");
                return d;
            });
            return _task;
        }
    }

    private static Dictionary<ulong, (long Size, uint Crc)> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        long len = fs.Length;
        int tailLen = (int)Math.Min(len, 65536 + 22);
        var tail = new byte[tailLen];
        fs.Position = len - tailLen;
        fs.ReadExactly(tail);
        // 中央ディレクトリの終わりの記録 (EOCD。末尾から探す)
        int eocd = -1;
        for (int i = tailLen - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("Data.p4k の中央ディレクトリの終わりが見つかりません");
        long entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        long cdSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12));
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        if (entries == 0xFFFF || cdSize == 0xFFFFFFFF || cdOffset == 0xFFFFFFFF)
        {
            // ZIP64 (EOCD の直前の locator → ZIP64 の EOCD)
            int loc = eocd - 20;
            if (loc < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(loc)) != 0x07064b50)
                throw new InvalidDataException("Data.p4k の ZIP64 の記録が見つかりません");
            long z64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(loc + 8));
            var rec = new byte[56];
            fs.Position = z64;
            fs.ReadExactly(rec);
            if (BinaryPrimitives.ReadUInt32LittleEndian(rec) != 0x06064b50) throw new InvalidDataException("Data.p4k の ZIP64 の中央ディレクトリの終わりが読めません");
            entries = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
            cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
        }
        var dict = new Dictionary<ulong, (long, uint)>((int)Math.Min(entries, 4_000_000));
        fs.Position = cdOffset;
        using var br = new BufferedStream(fs, 1 << 20);
        var hdr = new byte[46];
        var buf = new byte[65536 * 3];
        for (long k = 0; k < entries; k++)
        {
            br.ReadExactly(hdr);
            if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x02014b50) throw new InvalidDataException($"Data.p4k の中央ディレクトリの {k + 1} 件目が読めません");
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(16));
            long usize = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(24));
            int nl = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(28));
            int xl = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(30));
            int cl = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(32));
            br.ReadExactly(buf.AsSpan(0, nl + xl + cl));
            var name = Encoding.UTF8.GetString(buf, 0, nl);
            if (usize == 0xFFFFFFFF)
            {
                // ZIP64 の追加の欄 (id 0x0001) の最初の 8 バイトが元の大きさ
                int p = nl, end = nl + xl;
                while (p + 4 <= end)
                {
                    int id = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p));
                    int sz = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(p + 2));
                    if (id == 0x0001 && sz >= 8) { usize = (long)BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(p + 4)); break; }
                    p += 4 + sz;
                }
            }
            dict[Ship3DEstimator.Hash(Ship3DEstimator.Normalize(name))] = (usize, crc);
        }
        return dict;
    }
}

public enum Ship3DJobPhase { Estimating, Queued, Exporting, Done, Failed, Cancelled }

// 書き出しの種類: 船の 3D モデル / ステーションの地図 (StationMapExports) / ステーションの 3D 表示 (Station3DExports)
// Item: 船以外の品 (武器・装備・船の部品など。2026-11-04)
public enum Ship3DJobKind { Ship, Station, Station3D, Item }

// 書き出し 1 隻 (進み具合の欄の 1 行)
public sealed class Ship3DJob : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Record { get; }
    // ステーションの地図は "station:" を頭に付けた短い名前 (StationMapExports.KeyPrefix。船の短い名前と重ならないように)
    public string Short { get; }
    public string Name { get; }
    // 船かステーションの地図か (既定は船)
    public Ship3DJobKind Kind { get; init; } = Ship3DJobKind.Ship;
    // まとめて取り込むの中の船か
    public bool InBatch { get; }
    internal CancellationTokenSource Cts { get; }
    internal Task<string?>? Task { get; set; }
    internal Ship3DEstimate? Estimate { get; set; }
    // 見積もった時間 (秒。書き出しを始める前の予想)
    internal double EstimatedSeconds { get; set; }
    internal Stopwatch Watch { get; } = new();
    internal Ship3DStageTracker? Tracker { get; set; }
    // ほかの書き出しと並行で動いた (係数の実績に使わない)
    internal bool Overlapped { get; set; }
    // ログ用: 今の段階 (失敗・取り消しのときにどこで止まったかを出す)、単体かまとめての何件目か、待ち行列に入ってからの時間
    internal string LogStage { get; set; } = "";
    internal string LogTag { get; set; } = "単体";
    // 保存しない設定で、表示のために直接読むもの (置き場所に保存しない)
    internal bool ViewOnly { get; init; }
    internal Stopwatch QueueWatch { get; } = new();

    internal Ship3DJob(string record, string shortName, string name, CancellationTokenSource? batch)
    {
        Record = record;
        Short = shortName;
        Name = name;
        InBatch = batch != null;
        Cts = batch != null ? CancellationTokenSource.CreateLinkedTokenSource(batch.Token) : new CancellationTokenSource();
    }

    public void Cancel() { try { Cts.Cancel(); } catch (ObjectDisposedException) { } }

    private Ship3DJobPhase _phase = Ship3DJobPhase.Estimating;
    private string _reason = "";
    public Ship3DJobPhase Phase => _phase;

    internal void SetPhase(Ship3DJobPhase phase, string reason)
    {
        _phase = phase;
        _reason = reason;
        Refresh();
    }

    // 今の経過・残り (秒) と、予想があるか (見積もり中は無い)
    internal (double Elapsed, double Remaining, bool Known) Progress()
    {
        switch (_phase)
        {
            case Ship3DJobPhase.Estimating: return (0, 0, false);
            case Ship3DJobPhase.Queued: return (0, EstimatedSeconds, EstimatedSeconds > 0);
            case Ship3DJobPhase.Done: return (Watch.Elapsed.TotalSeconds, 0, true);
            case Ship3DJobPhase.Exporting:
                var e = Watch.Elapsed.TotalSeconds;
                var r = Tracker?.Remaining(e, EstimatedSeconds) ?? Math.Max(0, EstimatedSeconds - e);
                return (e, r, true);
            default: return (0, 0, false);
        }
    }

    private int _percent;
    private string _text = "";
    public int Percent => _percent;
    public string Text => _text;
    public bool IsIndeterminate => _phase == Ship3DJobPhase.Estimating;
    // 進み具合の欄に 1 行で出すか (順番待ちの船は出さず、まとめて「待ち N 隻」の 1 行にする。利用者の要望: まとめて取り込むで待ちの行がものすごく並んだ)
    public bool IsListed => _phase != Ship3DJobPhase.Queued;

    // ％と残り時間を出し直す (書き出し中は「経過 ÷ (経過 + 残り)」。終わるまでは 99% で止める)
    internal void Refresh()
    {
        int pct = 0;
        string text;
        switch (_phase)
        {
            case Ship3DJobPhase.Estimating: text = _reason.Length > 0 ? _reason : "見積もり中"; break;
            case Ship3DJobPhase.Queued: text = $"{_reason} (予想 約 {Ship3DExports.FormatSeconds(EstimatedSeconds)})"; break;
            case Ship3DJobPhase.Exporting:
                var (e, r, _) = Progress();
                pct = e + r > 0 ? (int)Math.Min(99, Math.Floor(e / (e + r) * 100)) : 0;
                var stage = Tracker?.StageText ?? "";
                // 経過時間 / 予想時間 (予想時間は今の見込みの総時間 = 経過 + 残り)
                var times = $"経過 {Ship3DExports.FormatSeconds(e)} / 予想 {Ship3DExports.FormatSeconds(e + r)}";
                text = r >= 1
                    ? $"{pct}%  残り 約 {Ship3DExports.FormatSeconds(r)}  ({times}){(stage.Length > 0 ? $"  [{stage}]" : "")}"
                    : $"{pct}%  まもなく終わる予定 (予想を超えています。経過 {Ship3DExports.FormatSeconds(e)}){(stage.Length > 0 ? $"  [{stage}]" : "")}";
                break;
            case Ship3DJobPhase.Done: pct = 100; text = "終わりました"; break;
            case Ship3DJobPhase.Failed: text = "失敗しました"; break;
            default: text = "取り消しました"; break;
        }
        if (pct != _percent) { _percent = pct; Changed(nameof(Percent)); }
        if (text != _text) { _text = text; Changed(nameof(Text)); }
        Changed(nameof(IsIndeterminate));
        Changed(nameof(IsListed));
    }

    // 待っている理由 (順番待ちのとき。「待ち N 隻」の行に出す)
    internal string Reason => _reason;
}

// 書き出しの段階 (starbreaker -v の標準エラーの行。親の実測、600i の例):
//   [mem] start → [mem] after p4k+dcb load (読み込み) → Loadout: … / resolving loadout meshes / resolved: 95 children
//   → Assembling: root + 87 child meshes + 656 interior meshes (264 unique CGFs)   ← ここで量が分かる
//   → [mem-phase] start write_glb → root packed → children 20/87, bin=38MB … children 87/87 → children done, bin=68MB
//   → interiors done, bin=135MB (船内の間は数を数える行が無い) → GLB size: N bytes / Written to ...
//   時刻: 読み込み〜Assembling 約 2 秒、子 87 個 約 3 秒、船内 656 個 約 3 秒 → 子 1 個は船内 1 個の約 7.5 倍の時間と見込む。
// 残り時間の出し方:
//   Assembling の行が出るまで: 予想の総時間 − 経過 (従来の「経過 ÷ 予想」と同じ)。行の形が違う (版が変わった等) ときもこのまま
//   Assembling の後: そこまでの経過を除いた残りの予想 (最低 1 秒) を、子 (重み 7.5)・船内 (重み 1)・仕上げ (5%) に分けて見込む。
//     子の数を数える行 (children k/N) が出たら、実際の 1 個あたりの時間で子と船内の残りを見直す。
//     船内の段階は数が出ないので、船内の量に比例した見込みから経過を引いた分 (下限 0。％は 99% で止まる)
internal sealed class Ship3DStageTracker
{
    private const double ChildWeight = 7.5;
    private const double FinishShare = 0.05;
    private static readonly Regex AssemblingRegex = new(@"Assembling:", RegexOptions.Compiled);
    private static readonly Regex ChildMeshesRegex = new(@"(\d+)\s+child meshes", RegexOptions.Compiled);
    private static readonly Regex InteriorMeshesRegex = new(@"(\d+)\s+interior meshes", RegexOptions.Compiled);
    private static readonly Regex ChildrenProgressRegex = new(@"children\s+(\d+)\s*/\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex ChildrenDoneRegex = new(@"children done", RegexOptions.Compiled);
    private static readonly Regex InteriorsDoneRegex = new(@"interiors done", RegexOptions.Compiled);

    private readonly object _lock = new();
    private double? _assembleAt, _childrenDoneAt, _interiorsDoneAt;
    private int _children, _interiors, _childDone;
    private double _lastChildAt;
    private double _assembleRemain;   // Assembling の時点の残りの予想 (秒)

    public string StageText
    {
        get
        {
            lock (_lock)
            {
                if (_interiorsDoneAt != null) return "仕上げ";
                if (_childrenDoneAt != null) return _interiors > 0 ? $"船内 {_interiors} 個" : "船内";
                if (_assembleAt != null) return _children > 0 ? $"部品 {_childDone}/{_children}" : "組み立て";
                return "読み込み";
            }
        }
    }

    // 標準エラーの 1 行 (elapsed は書き出しを始めてからの秒)
    public void OnLine(string line, double elapsed)
    {
        lock (_lock)
        {
            if (_assembleAt == null && AssemblingRegex.IsMatch(line))
            {
                _assembleAt = elapsed;
                if (ChildMeshesRegex.Match(line) is { Success: true } c) _children = int.Parse(c.Groups[1].Value);
                if (InteriorMeshesRegex.Match(line) is { Success: true } i) _interiors = int.Parse(i.Groups[1].Value);
                return;
            }
            if (_assembleAt == null) return;
            if (ChildrenProgressRegex.Match(line) is { Success: true } p)
            {
                _childDone = int.Parse(p.Groups[1].Value);
                if (_children == 0) _children = int.Parse(p.Groups[2].Value);
                _lastChildAt = elapsed;
            }
            else if (_childrenDoneAt == null && ChildrenDoneRegex.IsMatch(line)) { _childrenDoneAt = elapsed; _childDone = _children; }
            else if (_interiorsDoneAt == null && InteriorsDoneRegex.IsMatch(line)) _interiorsDoneAt = elapsed;
        }
    }

    // 残り時間 (秒)。estimated は書き出し前の予想の総時間
    public double Remaining(double elapsed, double estimated)
    {
        lock (_lock)
        {
            if (_assembleAt is not double at) return Math.Max(0, estimated - elapsed);
            if (_assembleRemain <= 0) _assembleRemain = Math.Max(1, estimated - at);
            var finish = _assembleRemain * FinishShare;
            if (_interiorsDoneAt is double idone) return Math.Max(0, finish - (elapsed - idone));
            var units = _children * ChildWeight + _interiors;
            if (units <= 0) return Math.Max(0, _assembleRemain - (elapsed - at));
            // 1 単位 (船内 1 個) あたりの秒: 子の数を数える行が出ていれば実際の値、無ければ予想を割り振ったもの
            var perUnit = _assembleRemain * (1 - FinishShare) / units;
            if (_childDone > 0 && _children > 0)
            {
                var childEnd = _childrenDoneAt ?? _lastChildAt;
                perUnit = (childEnd - at) / _childDone / ChildWeight;
            }
            double childRemain, interiorRemain;
            if (_childrenDoneAt is double cdone)
            {
                childRemain = 0;
                interiorRemain = Math.Max(0, _interiors * perUnit - (elapsed - cdone));
            }
            else
            {
                // 最後の子の行からの経過は、今の子の分として引く
                var sinceLast = _childDone > 0 ? elapsed - _lastChildAt : elapsed - at;
                childRemain = Math.Max(0, (_children - _childDone) * ChildWeight * perUnit - sinceLast);
                interiorRemain = _interiors * perUnit;
            }
            return childRemain + interiorRemain + finish;
        }
    }
}

// 予想 1 隻ぶん (参照ジオメトリの合計) と、照合に使うもの (--dump-hierarchy の内容のハッシュ、参照ファイル (ジオメトリ・material) のパス)
internal sealed record Ship3DEstimate(double GeometryMb, string HierarchyHash, IReadOnlyList<string> SourcePaths)
{
    public double Seconds((double SecPerMb, double SizeRatio) c) => Ship3DExports.BaseSeconds + c.SecPerMb * GeometryMb;
    public double GlbBytes((double SecPerMb, double SizeRatio) c) => GeometryMb * 1_000_000 * c.SizeRatio;
}

// 予想: その船の構成 (--dump-hierarchy と同じもの。ShipExport.DumpHierarchy) の参照ジオメトリ (.cga/.cgf/.skin/.chr/.cdf/.socpak。重複は 1 つ) の大きさを、
// Data.p4k の目次 (Data/Objects/** の元の大きさ。同名に m を付けたファイルも足す) で合計する。大きさの表は Data.p4k ごとに使い回す。
// (2026-11-04 から starbreaker の p4k list と --dump-hierarchy ではなく、アプリの中の読み取り。中身は同じ (船 3 隻でハッシュが同じことを確かめた))
// あわせて、照合 (Ship3DExports.VerifyAsync) に使う --dump-hierarchy の内容のハッシュと参照ファイルのパスを持つ
internal static class Ship3DEstimator
{
    private static readonly Regex GeometryPathRegex = new(@"""([^""]+\.(?:cga|cgf|skin|chr|cdf|socpak))""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaterialPathRegex = new(@"""([^""]+\.mtl)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] BaseExts = [".cga", ".cgf", ".skin", ".chr", ".cdf", ".socpak"];
    // --dump-hierarchy の JSON は配列の終わりの前にカンマが残ることがある (実データの h_ORIG_X1.json で確認) ので許す
    private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true };

    private static readonly object ListLock = new();
    private static string? _listKey;
    private static Task<Dictionary<ulong, long>>? _listTask;
    private static readonly Dictionary<string, Ship3DEstimate> _estimates = new(StringComparer.OrdinalIgnoreCase);

    // パスを比べる形にする: 小文字、区切りは /、先頭の data/ は外す
    internal static string Normalize(string path)
    {
        var p = path.Replace('\\', '/').Trim().ToLowerInvariant();
        if (p.StartsWith("data/", StringComparison.Ordinal)) p = p[5..];
        return p;
    }

    // パスの 64 ビットのハッシュ (FNV-1a)。数十万〜百数十万件を文字列のまま持つと重いので、ハッシュで持つ
    internal static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
        return h;
    }

    // Data.p4k の目次の大きさ (正規化したパスのハッシュ → 大きさ。.cgam 等の末尾 m のファイルは、m を除いたパスに足す)。
    // starbreaker の p4k list --filter "Data/Objects/**" と同じ項目 (glob の一致 GlobMatch) と大きさ (元の大きさ)
    private static Task<Dictionary<ulong, long>> GetSizesAsync(Ship3DExports.P4kStamp stamp)
    {
        lock (ListLock)
        {
            if (_listKey == stamp.Key && _listTask != null && !_listTask.IsFaulted && !_listTask.IsCanceled) return _listTask;
            _listKey = stamp.Key;
            _listTask = Task.Run(() =>
            {
                var sizes = new Dictionary<ulong, long>();
                var p4k = P4kArchive.Shared(stamp.Path);
                for (int i = 0; i < p4k.Count; i++)
                {
                    var name = p4k.Name(i);
                    if (!GlobMatch.MatchesFilter(name, "Data/Objects/**")) continue;
                    var path = Normalize(name);
                    string? key = null;
                    foreach (var ext in BaseExts)
                    {
                        if (path.EndsWith(ext, StringComparison.Ordinal)) { key = path; break; }
                        if (path.EndsWith(ext + "m", StringComparison.Ordinal)) { key = path[..^1]; break; }
                    }
                    if (key == null) continue;
                    var h = Hash(key);
                    long size = p4k.UncompressedSize(i);
                    sizes[h] = sizes.TryGetValue(h, out var s) ? s + size : size;
                }
                return sizes;
            });
            return _listTask;
        }
    }

    // 船の構成 (--dump-hierarchy と同じもの) を作って、選ばれた船 (root.entity)・内容のハッシュ・中身の文字を返す。
    // 選ばれた船が求める短い名前と違えば失敗 (書き出しても別の船になるため)
    public static async Task<(string Hash, string Text)> DumpHierarchyAsync(Ship3DExports.P4kStamp stamp, string shortName, CancellationToken ct)
    {
        var (text, resolved) = await Task.Run(() => ShipExport.DumpHierarchy(P4kArchive.Shared(stamp.Path), DataCoreShared.Get(null, stamp.Path), shortName, ct), ct);
        if (!string.Equals(Ship3DExports.ShortName(resolved), shortName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"読み取りが別の船 ({resolved}) を選びました。求める record は {shortName} です");
        using var doc = JsonDocument.Parse(text, LenientJson);
        return (CanonicalHash(doc.RootElement), text);
    }

    // JSON の内容のハッシュ (SHA-256 の 16 進)。オブジェクトのキーの並びは出すたびに変わる (実データで同じ船を 2 回出して確認。"props" の中の並びが違った) ので、
    // キーを並べ替えた形にしてから求める。数はそのままの文字で入れる (小数の書き方を変えない)
    public static string CanonicalHash(JsonElement root)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
            WriteCanonical(w, root);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Value.GetRawText(), StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    WriteCanonical(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) WriteCanonical(w, x);
                w.WriteEndArray();
                break;
            case JsonValueKind.Number: w.WriteRawValue(e.GetRawText()); break;
            case JsonValueKind.String: w.WriteStringValue(e.GetString()); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteNullValue(); break;
        }
    }

    public static async Task<Ship3DEstimate> EstimateAsync(Ship3DExports.P4kStamp stamp, string record, CancellationToken ct)
    {
        var shortName = Ship3DExports.ShortName(record);
        var cacheKey = $"{stamp.Key}|{shortName}";
        if (_estimates.TryGetValue(cacheKey, out var cached)) return cached;

        var sizesTask = GetSizesAsync(stamp);
        var (hash, text) = await DumpHierarchyAsync(stamp, shortName, ct);
        var flat = text.Replace("\\\\", "/");
        var geometry = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in GeometryPathRegex.Matches(flat)) geometry.Add(Normalize(m.Groups[1].Value));
        var materials = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in MaterialPathRegex.Matches(flat)) materials.Add(Normalize(m.Groups[1].Value));
        var sizes = await sizesTask.WaitAsync(ct);
        long total = 0;
        foreach (var p in geometry) if (sizes.TryGetValue(Hash(p), out var s)) total += s;
        var est = new Ship3DEstimate(total / 1_000_000.0, hash, geometry.Concat(materials).ToList());
        _estimates[cacheKey] = est;
        return est;
    }

    // テクスチャの番号付きの分割ファイルの末尾 (実データ: 例 aegs_internal_girder_b_diff.dds の横に .dds.1〜.dds.7、
    // damage_internals_ddna.dds の横に .dds.1〜.dds.7・.dds.1a〜.dds.3a・.dds.a がある)。9 まで見る
    private static readonly string[] DdsSuffixes =
        new[] { ".a" }.Concat(Enumerable.Range(1, 9).Select(i => $".{i}")).Concat(Enumerable.Range(1, 9).Select(i => $".{i}a")).ToArray();

    // 照合用の元ファイルの一覧 (p4k の中の大きさと Data.p4k に記録された CRC)。
    //   参照ジオメトリ (--dump-hierarchy の .cga 等。同名に m を付けたものもあれば足す)・material (.mtl)
    //   GLB の materials の extras にあるテクスチャ (diffuse_tex・normal_tex 等の .tif は p4k では .dds。実データで確認) と、その番号付きの分割ファイル、
    //   extras の中の .mtl (layers の path)
    //   書き出したときに p4k に無かった参照は大きさ -1 で記録する (後で現れたら変わったとみなす)
    public static List<Ship3DExports.SourceFile> BuildSources(IReadOnlyList<string> hierarchyPaths, string glbPath, Dictionary<ulong, (long Size, uint Crc)> index)
    {
        var result = new List<Ship3DExports.SourceFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path, bool required)
        {
            if (!seen.Add(path)) return;
            if (index.TryGetValue(Hash(path), out var v)) result.Add(new Ship3DExports.SourceFile { Path = path, Size = v.Size, Crc = v.Crc });
            else if (required) result.Add(new Ship3DExports.SourceFile { Path = path, Size = -1, Crc = 0 });
        }
        foreach (var p in hierarchyPaths)
        {
            Add(p, required: true);
            if (BaseExts.Any(e => p.EndsWith(e, StringComparison.Ordinal))) Add(p + "m", required: false);
        }
        foreach (var raw in GlbMaterialFiles(glbPath))
        {
            var p = Normalize(raw);
            if (p.EndsWith(".tif", StringComparison.Ordinal) || p.EndsWith(".tga", StringComparison.Ordinal) || p.EndsWith(".png", StringComparison.Ordinal))
                p = p[..^4] + ".dds";
            Add(p, required: true);
            if (p.EndsWith(".dds", StringComparison.Ordinal))
                foreach (var s in DdsSuffixes) Add(p + s, required: false);
        }
        return result;
    }

    // GLB の JSON (最初のかたまり) の materials の extras にあるファイルのパス (.tif / .dds / .tga / .png / .mtl で終わる文字。入れ子の中も)
    private static IEnumerable<string> GlbMaterialFiles(string glbPath)
    {
        using var fs = new FileStream(glbPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var head = new byte[20];
        fs.ReadExactly(head);
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != 0x46546C67) throw new InvalidDataException("GLB の頭が違います");   // "glTF"
        var jsonLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(16)) != 0x4E4F534A) throw new InvalidDataException("GLB の最初のかたまりが JSON ではありません");   // "JSON"
        var json = new byte[jsonLen];
        fs.ReadExactly(json);
        using var doc = JsonDocument.Parse(json);
        var list = new List<string>();
        if (doc.RootElement.TryGetProperty("materials", out var mats) && mats.ValueKind == JsonValueKind.Array)
            foreach (var m in mats.EnumerateArray())
                if (m.TryGetProperty("extras", out var ex)) Collect(ex, list);
        return list;

        static void Collect(JsonElement e, List<string> list)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Collect(p.Value, list); break;
                case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Collect(x, list); break;
                case JsonValueKind.String:
                    var s = e.GetString() ?? "";
                    if (Regex.IsMatch(s, @"\.(tif|dds|tga|png|mtl)$", RegexOptions.IgnoreCase)) list.Add(s);
                    break;
            }
        }
    }
}

// 書き出しの実績 (作業フォルダの ship3d_stats.json) と、そこから求めるこの PC の係数 (秒/MB・大きさの倍率)。
// 並行で動いた書き出しは記録しない (Ship3DExports が呼ばない)。実績が無ければ初めの定数
internal static class Ship3DStats
{
    private const int MaxRecords = 100;

    private sealed class Entry
    {
        public string Short { get; set; } = "";
        public double GeometryMb { get; set; }
        public double Seconds { get; set; }
        public long GlbBytes { get; set; }
        public string Utc { get; set; } = "";
    }

    private sealed class StatsFile { public List<Entry> Records { get; set; } = new(); }

    private static string FilePath => Path.Combine(DetailPanel.WorkDir(), "ship3d_stats.json");
    private static readonly object Lock = new();

    private static StatsFile Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<StatsFile>(File.ReadAllText(FilePath)) ?? new StatsFile();
        }
        catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 書き出しの実績 (ship3d_stats.json) を読めませんでした: {ex.Message}"); }
        return new StatsFile();
    }

    public static void Add(string shortName, double geometryMb, double seconds, long glbBytes)
    {
        lock (Lock)
        {
            try
            {
                var f = Load();
                f.Records.Add(new Entry { Short = shortName, GeometryMb = Math.Round(geometryMb, 2), Seconds = Math.Round(seconds, 2), GlbBytes = glbBytes, Utc = DateTime.UtcNow.ToString("o") });
                if (f.Records.Count > MaxRecords) f.Records.RemoveRange(0, f.Records.Count - MaxRecords);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] 書き出しの実績を記録できませんでした: {ex.Message}"); }
        }
    }

    // 係数: 秒/MB = Σ(実際の秒 − 3 秒) ÷ Σ参照 MB、大きさの倍率 = ΣGLB のバイト ÷ Σ(参照 MB × 1,000,000)。
    // 参照 1MB 未満の記録は使わない。秒/MB は 0.005 より小さくしない
    public static (double SecPerMb, double SizeRatio) Coefficients()
    {
        lock (Lock)
        {
            var recs = Load().Records.Where(r => r.GeometryMb >= 1 && r.Seconds > 0 && r.GlbBytes > 0).ToList();
            if (recs.Count == 0) return (Ship3DExports.InitialSecondsPerMb, Ship3DExports.InitialSizeRatio);
            var mb = recs.Sum(r => r.GeometryMb);
            var secPerMb = Math.Max(0.005, recs.Sum(r => Math.Max(0, r.Seconds - Ship3DExports.BaseSeconds)) / mb);
            var ratio = recs.Sum(r => (double)r.GlbBytes) / (mb * 1_000_000);
            return (secPerMb, ratio);
        }
    }
}

// PC 全体の CPU 使用率 (GetSystemTimes の前回との差: 1 − 空きの時間 ÷ (カーネル + ユーザーの時間)。カーネルの時間は空きの時間を含む)。
// 追加のパッケージ無しで使える Windows の API (kernel32)
internal sealed class CpuUsageMeter
{
    private ulong _idle, _kernel, _user;
    private bool _has;

    // 前回の呼び出しからの使用率 (%)。初回・取れないときは null
    public double? Sample()
    {
        if (!GetSystemTimes(out var idleFt, out var kernelFt, out var userFt)) return null;
        ulong idle = ToUlong(idleFt), kernel = ToUlong(kernelFt), user = ToUlong(userFt);
        double? result = null;
        if (_has)
        {
            var di = idle - _idle;
            var total = (kernel - _kernel) + (user - _user);
            if (total > 0) result = Math.Clamp((1.0 - (double)di / total) * 100.0, 0, 100);
        }
        _idle = idle; _kernel = kernel; _user = user; _has = true;
        return result;
    }

    private static ulong ToUlong(System.Runtime.InteropServices.ComTypes.FILETIME ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out System.Runtime.InteropServices.ComTypes.FILETIME idleTime,
        out System.Runtime.InteropServices.ComTypes.FILETIME kernelTime, out System.Runtime.InteropServices.ComTypes.FILETIME userTime);
}
