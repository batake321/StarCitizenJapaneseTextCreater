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
//   書き出し: StarBreaker の entity export (テクスチャ付き・船内込み) を選択中チャンネルの Data.p4k から。
//     ［読み込み］(［詳細］の船の欄・別ウィンドウ。ImportAsync) と、設定タブの［まとめて取り込む］(RunBatchAsync) からだけ始まる
//     (表示のボタンでは書き出さない)。書き出しは裏で並行に動く。同じ船の書き出しが動いていればそれに合流する (二重に書き出さない)。
//     始める前には毎回、CPU 使用率 (90% 以上なら待つ) と同時数の上限 (MaxParallelExports) を見る。待っている書き出しは並んだ順に始める (Pump)
//   キャッシュ: 置き場所 (設定の 3D モデルの置き場所。既定は TEMP の中) に「record の短い名前.glb」と、横に「同じ名前.json」(Sidecar)。
//     横の記録にアプリの印 (SidecarMark) があり中身が読めるものだけをこのアプリのキャッシュとして扱う (使用量・古い順の削除・鮮度の判定)。印の無いファイルには触れない。
//     書き出したときの Data.p4k の印 (パス・大きさ・更新日時) が今と同じなら最新。違う (パッチ) ときは、その船の元データを照合する (VerifyAsync):
//       元ファイル (参照ジオメトリ・material・テクスチャ) の p4k の中の大きさと CRC32、--dump-hierarchy の内容のハッシュが、書き出したときと同じなら
//       記録の Data.p4k の印を新しいものに書き換えて使い続ける。違う・照合できないときは、その場でキャッシュを消す (表示中・書き出し中は後で消す)
//     最大容量を超えたら、書き出しが終わるたびに古いもの (書き出した日時の古い順) から消す。表示中・書き出し中・今書き出したものは消さない
//   進み具合: 船ごと (Jobs) と全体 (Overall*)。StarBreaker は進み具合を出さないので、-v のログの段階の行 (Ship3DStageTracker) と予想時間から出す (予想にもとづく)
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
        "StarBreaker は進み具合を出さないため、予想にもとづいて出しています: 書き出しのログの段階 (読み込み・部品 k/N・船内・仕上げ) と、" +
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
        // アプリを終えるときは動いている書き出しを取り消し、動いている starbreaker (書き出し・見積もり・照合) は同期的に止める
        // (取り消しの後始末は UI スレッドの継続なので、終了後は動かないことがあるため)。残った一時ファイルは次の起動時の掃除 (CleanWorkFolder) で消す
        if (Application.Current != null) Application.Current.Exit += (_, _) =>
        {
            foreach (var j in Jobs.ToList()) j.Cancel();
            StarBreakerRunner.KillAll();
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
        // 元ファイルの一覧 (p4k の中の大きさと CRC32)
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
                    var extractor = new GameDataExtractor(DetailPanel.WorkDir());
                    await extractor.EnsureStarBreakerAsync();
                    var dump = await Ship3DEstimator.DumpHierarchyAsync(extractor.StarBreakerExe, stamp, shortName, CancellationToken.None);
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
        DeleteChangedCache(shortName, reason!);
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
    }

    // ===== 知らせ =====

    private string _notice = "";
    // 進み具合の欄の下に出す最後の知らせ (容量で消した・照合で消した等)。次の書き出しが加わるまで出しておく
    public string Notice { get => _notice; private set { _notice = value; Changed(nameof(Notice)); Changed(nameof(HasNotice)); Changed(nameof(ShowPanel)); } }
    public bool HasNotice => _notice.Length > 0;

    private static void Log(string msg) => App.AppendStartupLog(App.Config.WorkingDirectory, $"[3D モデル] {msg}");

    private static void ShowError(Window? owner, string msg)
    {
        Log(msg);
        if (owner != null && owner.IsLoaded) MessageBox.Show(owner, msg, "3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
        else MessageBox.Show(msg, "3D 表示", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ===== CPU・メモリ・同時数 =====

    public double CpuPercent => _cpuPercent;
    // CPU 使用率が 90% 以上 (書き出しを始めない。［読み込み］・［まとめて取り込む］を使えなくする)
    public bool CpuHigh => _cpuHigh;

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
    }

    // ===== 待ち行列 =====

    // 始める順番を待っている書き出し (並んだ順)。Pump が先頭から、CPU 使用率と同時数の上限を見て始める
    private readonly List<(Ship3DJob Job, TaskCompletionSource Start)> _queue = new();
    // 今動いている書き出し (StarBreaker の entity export)
    private readonly List<Ship3DJob> _running = new();

    // 順番を待つ (CPU 使用率が 90% 未満で、動いている数が上限より少なくなったら始まる)。取り消されたら取り消しの例外
    private async Task WaitTurnAsync(Ship3DJob job, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((job, tcs));
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
            if (_cpuHigh) reason = $"CPU 使用率 {_cpuPercent:F0}% のため待っています (90% 未満になったら始めます)";
            else
            {
                var (limit, byCpu, byMem, free) = MaxParallelExports();
                if (_running.Count >= limit)
                {
                    reason = byMem is int m && m < byCpu
                        ? $"メモリ待ち (空き {free / (double)(1024 * MB):F1} GB。1 本 3GB と見込み、同時 {limit} 本まで)"
                        : $"同時数の上限 ({limit} 本) のため待っています";
                }
            }
            if (reason != null)
            {
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
            start.TrySetResult();
        }
    }

    // ===== 船ごと・全体の進み具合 =====

    // 今回の書き出しのまとまり (動いている・待っている書き出しが無くなるまでに加わったもの全部。全体のバーの分母)
    private readonly List<Ship3DJob> _session = new();

    private int _overallPercent;
    private string _overallText = "";
    public int OverallPercent => _overallPercent;
    public string OverallText => _overallText;
    // 進み具合の欄を出すか (書き出し・まとめての見積もり・知らせがあるとき)
    public bool ShowPanel => Jobs.Count > 0 || _batchActive || HasNotice;

    private void AddJob(Ship3DJob job)
    {
        if (HasNotice) Notice = "";
        Jobs.Add(job);
        _session.Add(job);
        _byShort[job.Short] = job;
        Changed(nameof(ShowPanel));
        RefreshProgress();
        RaiseStateChanged();
    }

    private void RemoveJob(Ship3DJob job)
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
        var text = sumTotal > 0
            ? $"全体 {pct}%  残り 約 {FormatSeconds(overallRemain)}  ({_session.Count(j => j.Phase == Ship3DJobPhase.Done)} / {_session.Count} 隻)"
            : Jobs.Count > 0 ? "全体: 見積もり中" : "";
        if (pct != _overallPercent) { _overallPercent = pct; Changed(nameof(OverallPercent)); }
        if (text != _overallText) { _overallText = text; Changed(nameof(OverallText)); }
    }

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
    public async Task<string?> ImportAsync(string record, string name, Window? owner)
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
            try { return await running.Task; }
            catch { return null; }
        }
        if (await GetCachedPathAsync(record) is { } cached) return cached;
        if (_cpuHigh)
        {
            ShowError(owner, $"CPU 使用率が {_cpuPercent:F0}% と高いため、書き出しを始めません。90% 未満に下がってからもう一度押してください。");
            return null;
        }
        if (HasForeignFile(shortName))
        {
            ShowError(owner, $"置き場所に同じ名前のファイル ({shortName}.glb / {shortName}.json) があり、このアプリが書いたものではないので書き出しません。置き場所を変えるか、そのファイルを移してください。");
            return null;
        }
        // 照合の間に別の［読み込み］が始めていれば合流する
        if (_byShort.TryGetValue(shortName, out running) && running.Task != null)
        {
            try { return await running.Task; }
            catch { return null; }
        }
        var job = new Ship3DJob(record, shortName, string.IsNullOrWhiteSpace(name) ? shortName : name, batch: null);
        AddJob(job);
        job.Task = RunSingleAsync(job, stamp, owner);
        return await job.Task;
    }

    private async Task<string?> RunSingleAsync(Ship3DJob job, P4kStamp stamp, Window? owner)
    {
        try
        {
            var extractor = new GameDataExtractor(DetailPanel.WorkDir());
            try { await extractor.EnsureStarBreakerAsync(); }
            catch (Exception ex)
            {
                ShowError(owner, $"StarBreaker を用意できませんでした: {ex.Message}");
                job.SetPhase(Ship3DJobPhase.Failed, "");
                return null;
            }
            job.SetPhase(Ship3DJobPhase.Estimating, "見積もり中 (ゲームデータを調べています)");
            var est = await Ship3DEstimator.EstimateAsync(extractor.StarBreakerExe, stamp, job.Record, job.Cts.Token);
            var c = Ship3DStats.Coefficients();
            job.Estimate = est;
            job.EstimatedSeconds = est.Seconds(c);
            job.SetPhase(Ship3DJobPhase.Estimating, "書き出すか確かめています");
            var msg = $"「{job.Name}」の 3D モデルをゲームデータから書き出します。\n\n" +
                      $"予想時間: 約 {FormatSeconds(job.EstimatedSeconds)}\n予想サイズ: 約 {FormatBytes(est.GlbBytes(c))}\n\n" +
                      "書き出しは裏で行います (その間もほかの操作・ほかの船の書き出しができます)。始めますか？";
            var ok = owner != null && owner.IsLoaded
                ? MessageBox.Show(owner, msg, "3D 表示", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                : MessageBox.Show(msg, "3D 表示", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) { job.SetPhase(Ship3DJobPhase.Cancelled, ""); return null; }
            RefreshProgress();
            await WaitTurnAsync(job, job.Cts.Token);
            return await ExportAsync(job, extractor.StarBreakerExe, stamp);
        }
        catch (OperationCanceledException)
        {
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            Log($"「{job.Name}」({job.Short}) の書き出しを取り消しました");
            return null;
        }
        catch (Exception ex)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            ShowError(owner, $"「{job.Name}」({job.Short}) の 3D モデルを書き出せませんでした: {ex.Message}");
            return null;
        }
        finally { RemoveJob(job); }
    }

    // ===== 書き出し本体 =====

    // StarBreaker の解決した record (標準エラーの「using shortest match: X」、無ければ「Loadout tree for X:」)
    private static readonly Regex ShortestMatchRegex = new(@"using shortest match:\s*(\S+)", RegexOptions.Compiled);
    private static readonly Regex LoadoutTreeRegex = new(@"Loadout tree for\s+(\S+?):", RegexOptions.Compiled);

    // 順番が来た書き出しを動かす (WaitTurnAsync の後。_running に入っている)。終わったら GLB のパス。
    //   一時ファイル (置き場所の作業用のフォルダ) に書いてから名前を変える。StarBreaker の選んだ record が求めるものと違えば失敗 (GLB は消す)。
    //   同じ名前でこのアプリのものではないファイルがあれば上書きしない。
    //   終わったら、横の記録 (印・Data.p4k の印・照合用の元ファイルの一覧と CRC32・--dump-hierarchy のハッシュ) を書き、
    //   実績を記録し (並行で動いていなければ)、最大容量を超えていれば古いものから消す
    private async Task<string> ExportAsync(Ship3DJob job, string exe, P4kStamp stamp)
    {
        var ct = job.Cts.Token;
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(WorkFolder);
        var tmp = NewWorkFile(job.Short, ".glb");
        string? resolvedShortest = null, resolvedTree = null;
        job.Tracker = new Ship3DStageTracker();
        job.SetPhase(Ship3DJobPhase.Exporting, "");
        job.Watch.Restart();
        try
        {
            // -v を付けると段階の行 (Assembling・children k/N・interiors done 等) が標準エラーに出る (Ship3DStageTracker)
            var args = $"-v entity export \"{job.Short}\" \"{tmp}\" --p4k \"{stamp.Path}\" --materials textures";
            var (exit, tail) = await StarBreakerRunner.RunAsync(exe, args, line =>
            {
                job.Tracker.OnLine(line, job.Watch.Elapsed.TotalSeconds);
                if (resolvedShortest == null && ShortestMatchRegex.Match(line) is { Success: true } m1) resolvedShortest = m1.Groups[1].Value;
                if (resolvedTree == null && LoadoutTreeRegex.Match(line) is { Success: true } m2) resolvedTree = m2.Groups[1].Value;
            }, null, ct);
            job.Watch.Stop();
            if (exit != 0)
            {
                Log($"「{job.Name}」({job.Short}) の書き出しが失敗しました (終了コード {exit})。標準エラーの末尾:\n{tail}");
                throw new InvalidOperationException($"starbreaker が失敗しました (終了コード {exit}): {LastLines(tail, 3)}");
            }
            // 求める record と違う船を選んだら失敗 (候補が 1 つだけで「using shortest match」が出ないときは「Loadout tree for」で見る。
            // どちらも出なければ、短い名前そのものが唯一の候補なので求める record)
            var resolved = resolvedShortest ?? resolvedTree;
            if (resolved != null && !string.Equals(ShortName(resolved), job.Short, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"StarBreaker が別の船 ({resolved}) を選びました。求める record は {job.Record} です。書き出した GLB は使いません");
            if (!File.Exists(tmp)) throw new InvalidOperationException("starbreaker は終わりましたが GLB ができていません");

            // 照合用の元ファイルの一覧 (Data.p4k の中央ディレクトリの大きさと CRC32)
            job.SetPhase(Ship3DJobPhase.Exporting, "元データの記録");
            var est = job.Estimate ?? throw new InvalidOperationException("見積もり (--dump-hierarchy) がありません");
            var index = await Ship3DP4kIndex.GetAsync(stamp);
            var sources = await Task.Run(() => Ship3DEstimator.BuildSources(est.SourcePaths, tmp, index), ct);

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
                $"{(job.Overlapped ? "、並行で動いたので係数の実績には使いません" : "")})");
            if (!job.Overlapped) Ship3DStats.Add(job.Short, est.GeometryMb, job.Watch.Elapsed.TotalSeconds, glbBytes);
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
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static string LastLines(string text, int n)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - n)));
    }

    // 最大容量を超えていれば、書き出した日時 (GLB の更新日時) の古いものから消す (アプリの印のあるものだけ)。
    // 表示中・書き出し中 (今動いている・待っている船)・今書き出したものは消さない。消しても超えたままなら知らせる
    private void EnforceLimit(string justExported)
    {
        var max = MaxMb * MB;
        var files = CachedFiles();
        long total = files.Sum(f => f.Length);
        if (total <= max) return;
        var protect = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { justExported };
        foreach (var v in _views.Keys) protect.Add(v);
        foreach (var j in Jobs) protect.Add(j.Short);
        var deleted = new List<string>();
        foreach (var f in files.Where(f => !protect.Contains(Path.GetFileNameWithoutExtension(f.Name))).OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= max) break;
            var shortName = Path.GetFileNameWithoutExtension(f.Name);
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
        if (deleted.Count > 0) Log($"最大容量 ({MaxMb:N0} MB) を超えたので、古いものから {deleted.Count} 隻を消しました: {string.Join(", ", deleted)}");
        if (total > max)
        {
            var left = CachedFiles();
            var text = left.Count == 1
                ? $"「{Path.GetFileNameWithoutExtension(left[0].Name)}」1 隻だけで最大容量 ({MaxMb:N0} MB) を超えています ({FormatBytes(left[0].Length)})。消さずに残します"
                : $"最大容量 ({MaxMb:N0} MB) を超えていますが、表示中・書き出し中のものを除くと消せるものがありません (今 {FormatBytes(total)})";
            Notice = text;
            Log(text);
        }
        else if (deleted.Count > 0) Notice = $"最大容量を超えたので、古いものから {deleted.Count} 隻を消しました";
    }

    // ===== まとめて取り込む =====

    private bool _batchActive;
    private string _batchText = "";
    private CancellationTokenSource? _batchCts;
    public bool BatchActive => _batchActive;
    // まとめて取り込むの今の段階 (見積もり中 i / N 等)
    public string BatchText { get => _batchText; private set { _batchText = value; Changed(nameof(BatchText)); } }

    public void CancelBatch() => _batchCts?.Cancel();

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
        if (_batchActive) { ShowError(owner, "まとめて取り込むはすでに動いています。"); return; }
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
        try
        {
            var extractor = new GameDataExtractor(DetailPanel.WorkDir());
            BatchText = "StarBreaker を用意しています...";
            try { await extractor.EnsureStarBreakerAsync(); }
            catch (Exception ex) { ShowError(owner, $"StarBreaker を用意できませんでした: {ex.Message}"); return; }

            // 並び: 保有船 → 残り (同じ record は最初の 1 つ)
            var list = new List<(string Record, string Short, string Name, bool Owned)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (r, n) in owned)
                if (!string.IsNullOrWhiteSpace(r) && seen.Add(ShortName(r))) list.Add((r, ShortName(r), n, true));
            foreach (var (r, n) in rest)
                if (!string.IsNullOrWhiteSpace(r) && seen.Add(ShortName(r))) list.Add((r, ShortName(r), n, false));

            var c = Ship3DStats.Coefficients();
            double sumBytes = 0, sumSeconds = 0;
            int skipFresh = 0, skipRunning = 0, skipForeign = 0, notTaken = 0, ownedCount = 0;
            var selected = new List<(string Record, string Short, string Name, Ship3DEstimate Est)>();
            // 1. 飛ばす船を決める (照合が要る船は順に照合する)
            var candidates = new List<(string Record, string Short, string Name, bool Owned)>();
            for (int i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = list[i];
                if (_byShort.ContainsKey(s.Short)) { skipRunning++; continue; }
                var state = StateOf(s.Record);
                if (state is Ship3DCacheState.NeedsVerify or Ship3DCacheState.Verifying)
                {
                    BatchText = $"まとめて取り込む: 照合中 {i + 1} / {list.Count} 隻目 ({s.Name})";
                    if (await VerifyAsync(s.Record)) { skipFresh++; continue; }
                }
                else if (state == Ship3DCacheState.Fresh) { skipFresh++; continue; }
                if (HasForeignFile(s.Short)) { skipForeign++; continue; }
                candidates.Add(s);
            }

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
                try { return await Ship3DEstimator.EstimateAsync(extractor.StarBreakerExe, stamp, record, estCts.Token); }
                finally { gate.Release(); }
            }
            var estTasks = candidates.Select(s => EstimateGatedAsync(s.Record)).ToList();
            try
            {
                for (int k = 0; k < candidates.Count; k++)
                {
                    ct.ThrowIfCancellationRequested();
                    var s = candidates[k];
                    BatchText = $"まとめて取り込む: 見積もり中 {k + 1} / {candidates.Count} 隻目 (同時 {estLimit} 本。{s.Name})";
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

            var parallel = MaxParallelExports().Limit;
            var head = $"まとめて取り込む ({list.Count} 隻のうち)\n\n";
            var info = (skipFresh > 0 ? $"最新のキャッシュがあるので飛ばす: {skipFresh} 隻\n" : "") +
                       (skipRunning > 0 ? $"今書き出し中なので飛ばす: {skipRunning} 隻\n" : "") +
                       (skipForeign > 0 ? $"置き場所に同じ名前のこのアプリのものではないファイルがあるので飛ばす: {skipForeign} 隻\n" : "") +
                       (notTaken > 0 ? $"最大容量 ({MaxMb:N0} MB。今の使用量 {FormatBytes(CacheBytes())}、残り {FormatBytes(Math.Max(0, MaxMb * MB - CacheBytes()))}) を超えるので取り込まない: {notTaken} 隻\n" +
                                       "　→ 全部取り込むには、設定タブの「3D モデル」の最大容量を上げてください (全船で約 30〜35 GB。2026-10 の LIVE での見積もり)\n" : "") +
                       (failures.Count > 0 ? $"見積もれなかった: {failures.Count} 隻 (アプリのログに記録)\n" : "");
            if (selected.Count == 0)
            {
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
            if (ok != MessageBoxResult.OK) return;

            // 船ごとの書き出しを全部並べ (全体のバーの分母に入る)、並んだ順に始める
            BatchText = $"まとめて取り込む: {selected.Count} 隻";
            var tasks = new List<Task<bool>>();
            foreach (var s in selected)
            {
                if (_byShort.ContainsKey(s.Short)) { skipRunning++; continue; }   // 見積もりの間に個別で始めた船
                var job = new Ship3DJob(s.Record, s.Short, s.Name, _batchCts) { Estimate = s.Est, EstimatedSeconds = s.Est.Seconds(c) };
                AddJob(job);
                tasks.Add(RunBatchJobAsync(job, extractor.StarBreakerExe, stamp, failures));
            }
            var results = await Task.WhenAll(tasks);
            var done = results.Count(r => r);
            var cancelled = ct.IsCancellationRequested;
            var summary = $"まとめて取り込む: {(cancelled ? "取り消しました" : "終わりました")}。書き出した船 {done} / {tasks.Count} 隻" +
                          (failures.Count > 0 ? $"、失敗・見積もれなかった船 {failures.Count} 隻" : "");
            Log(summary + (failures.Count > 0 ? "\n" + string.Join("\n", failures) : ""));
            MessageBox.Show(summary + (failures.Count > 0 ? "\n\n" + string.Join("\n", failures.Take(15)) + (failures.Count > 15 ? "\n…(続きはアプリのログ)" : "") : ""),
                "3D モデル", MessageBoxButton.OK, failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            Log("まとめて取り込むを取り消しました");
            MessageBox.Show("まとめて取り込むを取り消しました。", "3D モデル", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(owner, $"まとめて取り込むが失敗しました: {ex.Message}");
        }
        finally
        {
            BatchText = "";
            _batchCts?.Dispose();
            _batchCts = null;
            SetBatchActive(false);
        }
    }

    // まとめての中の 1 隻 (順番を待ってから書き出す)。書き出せたら true。失敗は failures に足す (知らせは最後にまとめて)
    private async Task<bool> RunBatchJobAsync(Ship3DJob job, string exe, P4kStamp stamp, List<string> failures)
    {
        var tcs = new TaskCompletionSource<string?>();
        job.Task = tcs.Task;   // 個別の［読み込み］が同じ船なら合流する
        try
        {
            await WaitTurnAsync(job, job.Cts.Token);
            var path = await ExportAsync(job, exe, stamp);
            tcs.TrySetResult(path);
            return true;
        }
        catch (OperationCanceledException)
        {
            job.SetPhase(Ship3DJobPhase.Cancelled, "");
            tcs.TrySetResult(null);
            return false;
        }
        catch (Exception ex)
        {
            job.SetPhase(Ship3DJobPhase.Failed, "");
            failures.Add($"{job.Name} ({job.Short}): {ex.Message}");
            Log($"まとめて取り込む: 「{job.Name}」({job.Short}) を書き出せませんでした: {ex.Message}");
            tcs.TrySetResult(null);
            return false;
        }
        finally { RemoveJob(job); }
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

// Data.p4k の中央ディレクトリ (全エントリの大きさと CRC32)。照合と元ファイルの記録に使う。Data.p4k ごとに 1 回読んで使い回す。
// ZIP の中央ディレクトリを自分で読む (System.IO.Compression.ZipArchive でも読めるが、約 138 万件のエントリを全部オブジェクトにして
// 作業メモリが約 1GB になるため)。キーは p4k の中のパスを正規化したもの (Ship3DEstimator.Normalize) のハッシュ。
// 実データで確かめたこと (LIVE の Data.p4k、2026-10-29): エントリ 1,379,308 件、中央ディレクトリ 447MB、CRC32 が 0 のものは 63 件だけ。
// この読み方の大きさ・CRC32 は ZipArchive (ZipArchiveEntry.Length / Crc32) と全件一致。読むのに約 0.9 秒 (パスの文字列を持つ版で作業メモリ最大約 340MB)
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

// 書き出し 1 隻 (進み具合の欄の 1 行)
public sealed class Ship3DJob : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Record { get; }
    public string Short { get; }
    public string Name { get; }
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
    }
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

// 予想: その船の --dump-hierarchy の参照ジオメトリ (.cga/.cgf/.skin/.chr/.cdf/.socpak。重複は 1 つ) の大きさを、
// p4k list (Data/Objects/**) の大きさ (同名に m を付けたファイルも足す) で合計する。p4k list は Data.p4k ごとに使い回す。
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

    // p4k list の大きさ (正規化したパスのハッシュ → 大きさ。.cgam 等の末尾 m のファイルは、m を除いたパスに足す)
    private static Task<Dictionary<ulong, long>> GetSizesAsync(string exe, Ship3DExports.P4kStamp stamp)
    {
        lock (ListLock)
        {
            if (_listKey == stamp.Key && _listTask != null && !_listTask.IsFaulted && !_listTask.IsCanceled) return _listTask;
            _listKey = stamp.Key;
            _listTask = Task.Run(async () =>
            {
                var sizes = new Dictionary<ulong, long>();
                var (exit, tail) = await StarBreakerRunner.RunAsync(exe, $"p4k list --p4k \"{stamp.Path}\" --filter \"Data/Objects/**\"", null, line =>
                {
                    var tab = line.LastIndexOf('\t');
                    if (tab <= 0 || !long.TryParse(line.AsSpan(tab + 1), out var size)) return;
                    var path = Normalize(line[..tab]);
                    string? key = null;
                    foreach (var ext in BaseExts)
                    {
                        if (path.EndsWith(ext, StringComparison.Ordinal)) { key = path; break; }
                        if (path.EndsWith(ext + "m", StringComparison.Ordinal)) { key = path[..^1]; break; }
                    }
                    if (key == null) return;
                    var h = Hash(key);
                    sizes[h] = sizes.TryGetValue(h, out var s) ? s + size : size;
                }, CancellationToken.None);
                if (exit != 0) throw new InvalidOperationException($"starbreaker p4k list が失敗しました (終了コード {exit}): {tail}");
                return sizes;
            });
            return _listTask;
        }
    }

    // --dump-hierarchy を動かして、選ばれた船 (root.entity)・内容のハッシュ・中身の文字を返す。
    // 選ばれた船が求める短い名前と違えば失敗 (書き出しても別の船になるため)
    public static async Task<(string Hash, string Text)> DumpHierarchyAsync(string exe, Ship3DExports.P4kStamp stamp, string shortName, CancellationToken ct)
    {
        Directory.CreateDirectory(Ship3DExports.WorkFolder);
        var json = Ship3DExports.NewWorkFile(shortName, ".hierarchy.json");
        try
        {
            var (exit, tail) = await StarBreakerRunner.RunAsync(exe, $"entity export \"{shortName}\" \"{json}\" --p4k \"{stamp.Path}\" --dump-hierarchy", null, null, ct);
            if (exit != 0) throw new InvalidOperationException($"starbreaker の --dump-hierarchy が失敗しました (終了コード {exit}): {tail}");
            if (!File.Exists(json)) throw new InvalidOperationException("starbreaker の --dump-hierarchy の結果のファイルができていません");
            var text = await File.ReadAllTextAsync(json, ct);
            using var doc = JsonDocument.Parse(text, LenientJson);
            if (doc.RootElement.TryGetProperty("root", out var root) && root.TryGetProperty("entity", out var ent) && ent.GetString() is { } entity
                && !string.Equals(Ship3DExports.ShortName(entity), shortName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"StarBreaker が別の船 ({entity}) を選びました。求める record は {shortName} です");
            return (CanonicalHash(doc.RootElement), text);
        }
        finally
        {
            try { if (File.Exists(json)) File.Delete(json); } catch { }
        }
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

    public static async Task<Ship3DEstimate> EstimateAsync(string exe, Ship3DExports.P4kStamp stamp, string record, CancellationToken ct)
    {
        var shortName = Ship3DExports.ShortName(record);
        var cacheKey = $"{stamp.Key}|{shortName}";
        if (_estimates.TryGetValue(cacheKey, out var cached)) return cached;

        var sizesTask = GetSizesAsync(exe, stamp);
        var (hash, text) = await DumpHierarchyAsync(exe, stamp, shortName, ct);
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

    // 照合用の元ファイルの一覧 (p4k の中の大きさと CRC32)。
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

// starbreaker を動かす (標準出力・標準エラーは 1 行ずつ渡すか読み捨てる。パイプが詰まらないよう、両方とも必ず読む)。
// 取り消されたらプロセス (子プロセスも) を止め、終わるのを待ってから (最大 5 秒) 取り消しの例外を上げる (GameDataExtractor.WaitStarBreakerExitAsync と同じ)。
// 動いているプロセスは覚えておき、アプリを終えるときに KillAll で同期的に止める
internal static class StarBreakerRunner
{
    private const int TailLines = 40;
    private static readonly HashSet<Process> Live = new();

    // 動いている starbreaker (書き出し・見積もり・照合・p4k list) を全部止める (子プロセスも)。UI スレッドの継続に頼らない
    public static void KillAll()
    {
        Process[] procs;
        lock (Live) procs = Live.ToArray();
        foreach (var p in procs)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        }
    }

    public static async Task<(int ExitCode, string StderrTail)> RunAsync(string exe, string args, Action<string>? onStderr, Action<string>? onStdout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        ct.ThrowIfCancellationRequested();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("starbreaker を起動できませんでした");
        lock (Live) Live.Add(proc);
        try
        {
            return await WaitAsync(proc, onStderr, onStdout, ct);
        }
        finally
        {
            lock (Live) Live.Remove(proc);
        }
    }

    private static async Task<(int ExitCode, string StderrTail)> WaitAsync(Process proc, Action<string>? onStderr, Action<string>? onStdout, CancellationToken ct)
    {
        var tail = new Queue<string>();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                while (tail.Count > TailLines) tail.Dequeue();
            }
            try { onStderr?.Invoke(e.Data); } catch { }
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            try { onStdout?.Invoke(e.Data); } catch { }
        };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch { }
            throw;
        }
        // 非同期の読み取りの残り (最後の行) を受け取り終えるまで待つ
        proc.WaitForExit();
        string text;
        lock (tail) text = string.Join("\n", tail);
        return (proc.ExitCode, text.Length == 0 ? "(標準エラーの出力なし)" : text);
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
