using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();
    public static string ConfigPath { get; private set; } = "";
    // この起動中は個人の設定ファイルへ保存しない (壊れた設定ファイルの控えを作れなかったとき。AppConfigLoader)
    public static bool ConfigSaveDisabled { get; private set; }

    // 二重起動を防ぐための名前つき Mutex (ログオン中のユーザーの中で 1 つ)。アプリが終わるまで持っておく
    private const string SingleInstanceMutexName = @"Local\StarCitizenJapaneseTextCreater_SingleInstance";
    private static System.Threading.Mutex? _singleInstance;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    // 先に起動しているほうのウィンドウを前に出す (最小化されていれば元に戻す)。見つからなくても何もしない
    private static void ActivateRunningInstance()
    {
        try
        {
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
            {
                using (p)
                {
                    if (p.Id == current.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                    if (IsIconic(p.MainWindowHandle)) ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
            }
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--export-backup")
        {
            RunBackupExport(e.Args[1]);
            // Shutdown() は遅延実行なので、その前に App.xaml の StartupUri が MainWindow を作ってしまい、
            // 終了コードが 1 になる (呼び出し側のスクリプトが失敗と誤判定する)。ここで確実に終わらせる
            Environment.Exit(0);
            return;
        }

        // 二重起動を防ぐ: 2 つ起動すると、ゲームログの音・読み上げが 2 回ずれて重なり、同じ DB に 2 つから書き込む。
        // すでに起動していれば、そのウィンドウを前に出して、こちらは終わる
        _singleInstance = new System.Threading.Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            ActivateRunningInstance();
            Environment.Exit(0);
            return;
        }

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarCitizenJapaneseTextCreater");
        Directory.CreateDirectory(appDataDir);
        ConfigPath = Path.Combine(appDataDir, "appsettings.json");

        // Load: user config (AppData) > default (exe dir)。個人の設定がまだ無ければ既定を写す。
        // 個人の設定ファイルが壊れていたら別名で残して初期値で起動し、画面が出た後に 1 回だけ知らせる (AppConfigLoader)
        var loaded = AppConfigLoader.Load(AppContext.BaseDirectory, ConfigPath, DateTime.Now);
        Config = loaded.Config;
        ConfigSaveDisabled = loaded.SaveDisabled;
        if (loaded.Notice is { } notice)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                System.Windows.MessageBox.Show(notice, "設定ファイル",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning));
        }

        if (string.IsNullOrEmpty(Config.GamePath) || !Directory.Exists(Config.GamePath))
        {
            var detected = DetectFromLauncherLog();
            if (detected.Count > 0)
                Config.GamePath = detected[0];
        }

        if (string.IsNullOrEmpty(Config.WorkingDirectory))
            Config.WorkingDirectory = @"C:\temp";
        bool workDirReady = false;
        try
        {
            Directory.CreateDirectory(Config.WorkingDirectory);
            workDirReady = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Working Directory を作成できません:\n{Config.WorkingDirectory}\n\nエラー: {ex.Message}\n\n設定タブで別のパスを指定してください。",
                "起動エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        // フォルダはあっても書けない (Program Files の下など) と、同梱 DB を写すところで落ちるので、先に確かめて知らせる
        if (workDirReady && !CanWriteToDirectory(Config.WorkingDirectory, out var writeError))
        {
            workDirReady = false;
            System.Windows.MessageBox.Show(
                $"Working Directory に書き込めません:\n{Config.WorkingDirectory}\n\nエラー: {writeError}\n\n設定タブで別のパス (Program Files の外) を指定してください。",
                "起動エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        // 同梱 DB が替わった・作業フォルダの DB が無い・使えないときは、同梱 DB を土台に作り直す (今までの DB の個人データは取り込む)。
        // 時間がかかるので「準備中」の小さな窓を出し、裏で行う。失敗したら画面が出た後に知らせる
        if (workDirReady)
        {
            try
            {
                var jobs = PlanBundledDbSync(AppContext.BaseDirectory, Config.WorkingDirectory);
                if (jobs.Count > 0)
                    NotifyBundledDbResult(RunBundledDbSyncWithWindow(jobs, Config.WorkingDirectory));
            }
            catch (Exception ex)
            {
                AppendStartupLog(Config.WorkingDirectory, $"[同梱DB] 作り直しの準備に失敗: {ex}");
                var failed = new BundledDbSyncResult();
                failed.Failures.Add($"同梱 DB: {ex.Message}");
                NotifyBundledDbResult(failed);
            }
        }
    }

    public static List<string> DetectGameChannels()
    {
        var paths = DetectFromLauncherLog();
        if (paths.Count > 0) return paths;

        var gamePath = Config.GamePath;
        if (string.IsNullOrEmpty(gamePath) || !Directory.Exists(gamePath)) return paths;

        var parent = Path.GetDirectoryName(gamePath);
        if (parent == null || !Directory.Exists(parent)) return new List<string> { gamePath };

        return Directory.GetDirectories(parent)
            .Where(d => File.Exists(Path.Combine(d, "Data.p4k")))
            .ToList();
    }

    private void RunBackupExport(string outputDir)
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarCitizenJapaneseTextCreater");
        var configPath = Path.Combine(appDataDir, "appsettings.json");

        // 起動時と同じ読み込み (個人の設定ファイルが壊れていたら別名で残して既定で続ける)。画面が無いので知らせはコンソールに出す。
        // 個人の設定が無いときに既定を写さないのは今までどおり
        var loaded = AppConfigLoader.Load(AppContext.BaseDirectory, configPath, DateTime.Now, copyDefaultIfMissing: false);
        if (loaded.Notice is { } notice)
            Console.WriteLine(notice);
        var config = loaded.Config;

        var workDir = string.IsNullOrEmpty(config.WorkingDirectory) ? @"C:\temp" : config.WorkingDirectory;
        var translationDb = Path.Combine(workDir, "translations.db");
        var indexDb = Path.Combine(workDir, "gamedata_cache.db");

        Directory.CreateDirectory(outputDir);

        var outPath = Path.Combine(outputDir, "sc_japanese_backup.zip");
        // ExportAsync は内部の await に ConfigureAwait(false) が付いていないので、
        // WPF のディスパッチャ上で Wait() すると継続がブロック中のディスパッチャへ戻ろうとしてデッドロックする
        // (zip は書けるがプロセスが終わらず居座る)。同期コンテキストの無いスレッドプール上で待つ
        // 配布用なので PC 間の同期の墓石 (sync_tombstones) は入れない (includeTombstones: false)
        Task.Run(() => DatabaseBackupService.ExportAsync(translationDb, indexDb, outPath,
            s => Console.WriteLine($"  {s}"), includeTombstones: false)).GetAwaiter().GetResult();

        if (File.Exists(outPath))
            Console.WriteLine($"Backup: {new FileInfo(outPath).Length / 1024.0:N0} KB -> {outPath}");

        Console.WriteLine("Export complete.");
    }

    // ===== 同梱 DB (インストール先の translations.db / gamedata_cache.db / equipment_cache.db) を作業ディレクトリへ =====
    // 同梱 DB が替わった (記録と違う・記録が無い) とき、または作業ディレクトリの DB が無い・壊れている・(gamedata は) 中身が空のときは、
    // 同梱 DB を土台に作り直す。作業ディレクトリに今までの DB があれば、そこから個人データだけを取り込む
    // (translations の手動訳・CSV・用語集の置換の行、用語集、チャットの記憶 (knowledge)、この PC だけの印)。それ以外は同梱の内容が正
    internal static readonly string[] BundledDbNames = ["translations.db", "gamedata_cache.db", "equipment_cache.db"];

    // 同梱 DB で作り直したときの、同梱ファイルの長さと更新日時 (UTC) の記録 (作業ディレクトリに置く)。
    // 同梱ファイルの値がこの記録と違えば (アプリの更新で同梱 DB が替わった・記録がまだ無い) 作り直す。
    // 作り直しが失敗したら記録しない (次の起動でまた試す)
    internal const string BundledDbStateFileName = "bundled_db_state.json";

    // SQLite が DB の横に作るファイル (DB を動かすときは一緒に動かす)
    private static readonly string[] SqliteSideFileSuffixes = ["-journal", "-wal", "-shm"];

    // translations のうち個人データとして取り込む行の source (手動訳・CSV の取り込み・用語集の一括置換)
    internal static readonly string[] PersonalTranslationSources = ["manual", "csv", "glossary"];

    internal sealed class BundledDbStamp
    {
        public long Length { get; set; }
        public string LastWriteUtc { get; set; } = "";
    }

    // Reason: 作り直す理由 (ログ用)
    internal sealed record BundledDbJob(string DbName, string SrcPath, string DestPath, string Reason, BundledDbStamp Stamp);

    // 作り直しの結果。Failures: 作り直せなかった DB (今までの DB のまま)。Warnings: 作り直したが、今までの DB から個人データの一部を読めなかったもの。
    // StateNotSaved: 作り直して入れ替えは済んだが、作り直した記録 (bundled_db_state.json) を残せなかった DB (次の起動でもう一度作り直す)
    internal sealed class BundledDbSyncResult
    {
        public List<string> Failures { get; } = new();
        public List<string> Warnings { get; } = new();
        public List<string> StateNotSaved { get; } = new();
    }

    // 今までの DB から取り込んだ個人データの件数
    internal sealed class PersonalImportCounts
    {
        public int Translations { get; set; }
        public int Glossary { get; set; }
        public int Knowledge { get; set; }
        public int LocalMeta { get; set; }
        public int Tombstones { get; set; }   // PC 間の同期の墓石 (sync_tombstones)
        public int PersonNames { get; set; }   // AI 翻訳で貯めた人名 (person_names)

        public override string ToString() =>
            $"手動訳など {Translations} 行・用語集 {Glossary} 件・人名 {PersonNames} 件・記憶 {Knowledge} 件・この PC の印 {LocalMeta} 件・同期の墓石 {Tombstones} 件";
    }

    internal static BundledDbStamp GetBundledDbStamp(string path)
    {
        var fi = new FileInfo(path);
        return new BundledDbStamp { Length = fi.Length, LastWriteUtc = fi.LastWriteTimeUtc.ToString("o") };
    }

    internal static Dictionary<string, BundledDbStamp> LoadBundledDbState(string workDir)
    {
        var path = Path.Combine(workDir, BundledDbStateFileName);
        try
        {
            if (File.Exists(path))
            {
                var state = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, BundledDbStamp>>(File.ReadAllText(path));
                if (state != null) return new Dictionary<string, BundledDbStamp>(state, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            // 読めない記録は無いのと同じ扱い (作り直しをもう一度行う)
            AppendStartupLog(workDir, $"[同梱DB] {BundledDbStateFileName} を読めないので、無いものとして扱います: {ex.Message}");
        }
        return new Dictionary<string, BundledDbStamp>(StringComparer.OrdinalIgnoreCase);
    }

    internal static void SaveBundledDbState(string workDir, Dictionary<string, BundledDbStamp> state)
    {
        var path = Path.Combine(workDir, BundledDbStateFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    // 何を作り直すか決める。作業ディレクトリに無い・記録が無い・同梱ファイルが記録と違う・作業ディレクトリの DB が使えない (CheckWorkDbHealth) なら作り直す
    internal static List<BundledDbJob> PlanBundledDbSync(string baseDir, string workDir)
    {
        var state = LoadBundledDbState(workDir);
        var jobs = new List<BundledDbJob>();
        foreach (var dbName in BundledDbNames)
        {
            var src = Path.Combine(baseDir, dbName);
            if (!File.Exists(src)) continue;
            var dest = Path.Combine(workDir, dbName);
            // 作業ディレクトリがインストール先と同じ (同じファイル) なら何もしない (自分自身からは作り直さない)
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase)) continue;
            // 前回の入れ替えが途中で終わっていたら (".bak.new" が残っている)、先に片付ける
            RecoverInterruptedReplace(dest, workDir);
            var stamp = GetBundledDbStamp(src);
            string? reason;
            if (!File.Exists(dest)) reason = "作業フォルダに無い";
            else if (!state.TryGetValue(dbName, out var rec)) reason = "同梱 DB で作り直した記録が無い";
            else if (rec.Length != stamp.Length || rec.LastWriteUtc != stamp.LastWriteUtc) reason = "同梱 DB が替わった";
            else
            {
                reason = CheckWorkDbHealth(dbName, dest);
                // 使える (quick_check も ok) と分かった gamedata_cache.db は、この起動中の GameDataExtractor.EnsureDb の quick_check を 1 回省く
                // (パスと更新日時が同じときだけ)
                if (reason == null && dbName == "gamedata_cache.db") GameDataExtractor.NoteQuickCheckOk(dest);
            }
            if (reason != null) jobs.Add(new BundledDbJob(dbName, src, dest, reason, stamp));
        }
        return jobs;
    }

    // 作業ディレクトリの DB が使える状態か。使えなければその理由、使えれば null。
    // quick_check が "ok" でない・開けない、gamedata_cache.db なら item_index か items の表が無い・空なら使えないとする
    internal static string? CheckWorkDbHealth(string dbName, string path)
    {
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = "PRAGMA quick_check";
            var qc = cmd.ExecuteScalar()?.ToString();
            if (qc != "ok")
            {
                // quick_check の結果は何百行にもなることがあるので、ログには頭だけ残す
                var head = (qc ?? "").ReplaceLineEndings(" / ");
                if (head.Length > 200) head = head[..200] + "…";
                return $"作業フォルダの DB が壊れている (quick_check: {head})";
            }
            if (dbName == "gamedata_cache.db")
            {
                foreach (var table in new[] { "item_index", "items" })
                {
                    if (!TableExists(db, null, table)) return $"作業フォルダの DB に {table} の表が無い";
                    cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM {QuoteIdent(table)})";
                    if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return $"作業フォルダの DB の {table} が空";
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"作業フォルダの DB を開けない ({ex.Message})";
        }
    }

    // translations.db に個人訳 (source が manual / csv / glossary の行) があるか。ファイルが無い・読めなければ false
    internal static bool TranslationsHavePersonalRows(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"SELECT EXISTS (SELECT 1 FROM translations WHERE source IN ({string.Join(", ", PersonalTranslationSources.Select(s => $"'{s}'"))}))";
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
        }
        catch (Exception ex)
        {
            AppendStartupLog(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
                $"[同梱DB] {Path.GetFileName(path)} の個人訳の有無を読めないため、無いものとして扱います: {ex.Message}");
            return false;
        }
    }

    // 前回の入れ替え (ReplaceWithRebuilt) が途中で終わって "<名前>.bak.new" (か、その横のファイル) が残っていたら片付ける。
    // ReplaceWithRebuilt は今までの DB の横のファイルを先に、DB を最後に .bak.new へ動かすので、
    //   - .bak.new が無く横のファイルだけ (.bak.new-journal 等) があり、正式名の DB がある: 横のファイルを動かしている途中で終わった。
    //     横のファイルは正式名の DB のものなので、正式名の横へ戻す
    //   - .bak.new があり、正式名の DB が無い: 今までの DB を動かした後、新しい DB を正式名にする前に終わった。
    //     .bak.new (と横のファイル) が今までの DB なので正式名に戻す (取り込み元として使えるように)。
    //     .bak.new 側に横のファイルがある接尾辞について、正式名の横のファイルがあれば DB の無い残りなので消してから戻す
    //   - .bak.new があり、正式名の DB がある (新しい DB へは入れ替わっていた): .bak.new を .bak にする
    //     (FinishBak の途中で終わった場合もここ。FinishBak は .bak.new を最後に動かすので、呼び直せば続きができる)
    // FinishBak も横のファイルを先、DB を最後に動かすので、FinishBak の途中で落ちても
    // ".bak.new が無く横のファイルだけある" 状態にはならない (1 つ目の分岐は ReplaceWithRebuilt の途中だけ)
    internal static void RecoverInterruptedReplace(string dest, string workDir)
    {
        var bak = dest + ".bak";
        var bakNew = dest + ".bak.new";
        try
        {
            if (!File.Exists(bakNew))
            {
                if (!File.Exists(dest)) return;
                var back = new List<string>();
                foreach (var s in SqliteSideFileSuffixes)
                {
                    if (!File.Exists(bakNew + s)) continue;
                    if (File.Exists(dest + s))
                    {
                        AppendStartupLog(workDir, $"[同梱DB] 前回の入れ替えの残り {Path.GetFileName(bakNew + s)} がありますが、{Path.GetFileName(dest + s)} もあるため、そのままにしています");
                        continue;
                    }
                    File.Move(bakNew + s, dest + s);
                    back.Add(Path.GetFileName(bakNew + s));
                }
                if (back.Count > 0)
                    AppendStartupLog(workDir, $"[同梱DB] 前回の入れ替えが途中で終わっていたため、{string.Join(" / ", back)} を {Path.GetFileName(dest)} の横に戻しました");
                return;
            }
            if (!File.Exists(dest))
            {
                foreach (var s in SqliteSideFileSuffixes)
                    if (File.Exists(bakNew + s) && File.Exists(dest + s)) File.Delete(dest + s);   // DB の無い横のファイルは、戻す DB と組にならない
                File.Move(bakNew, dest);
                foreach (var s in SqliteSideFileSuffixes)
                    if (File.Exists(bakNew + s)) File.Move(bakNew + s, dest + s);
                AppendStartupLog(workDir, $"[同梱DB] 前回の入れ替えが途中で終わっていたため、{Path.GetFileName(bakNew)} を {Path.GetFileName(dest)} に戻しました");
            }
            else
            {
                FinishBak(bakNew, bak);
                AppendStartupLog(workDir, $"[同梱DB] 前回の入れ替えが途中で終わっていたため、{Path.GetFileName(bakNew)} を {Path.GetFileName(bak)} にしました");
            }
        }
        catch (Exception ex)
        {
            AppendStartupLog(workDir, $"[同梱DB] 前回の入れ替えの残り ({Path.GetFileName(bakNew)}) を片付けられませんでした: {ex.Message}");
        }
    }

    // "<名前>.bak.new" (と横のファイル) を "<名前>.bak" にする (前の .bak と横のファイルは置き換える)。
    // どこで落ちても RecoverInterruptedReplace から呼び直せば続きができるように、次の順で動かす:
    //   1. 前の .bak の横のファイルを消す → 2. 前の .bak を消す (.bak が無ければ、前の .bak の横のファイルはもう無い)
    //   3. .bak.new の横のファイルを .bak の横へ → 4. .bak.new を最後に .bak へ
    // DB を最後に動かすので、.bak.new が無くなっていれば横のファイルも全部動いている
    // (".bak.new が無く .bak.new の横のファイルだけある" 状態は ReplaceWithRebuilt の途中でしか起きない)。
    // .bak が無いときの .bak の横のファイルは 3 で動かした .bak.new のものなので消さない
    private static void FinishBak(string bakNew, string bak)
    {
        if (File.Exists(bak))
        {
            foreach (var s in SqliteSideFileSuffixes)
                if (File.Exists(bak + s)) File.Delete(bak + s);
            File.Delete(bak);
        }
        foreach (var s in SqliteSideFileSuffixes)
            if (File.Exists(bakNew + s)) File.Move(bakNew + s, bak + s, overwrite: true);
        if (File.Exists(bakNew)) File.Move(bakNew, bak, overwrite: true);
    }

    // 作り直しを順に行う (裏のスレッドで呼ぶ)。成功したものだけ記録する
    internal static BundledDbSyncResult RunBundledDbSync(List<BundledDbJob> jobs, string workDir, Action<int?, string>? report)
    {
        var result = new BundledDbSyncResult();
        var state = LoadBundledDbState(workDir);
        // translations の個人訳を取り込んだら、gamedata の item_index.name_ja を付け直す (同梱の name_ja は作者の訳から作ったものなので)。
        // gamedata を作り直すときは、作業フォルダの translations (今回作り直したものを含む) に個人訳があれば毎回付け直す
        bool personalTranslationsImported = false;
        // translations か gamedata を作り直したら、最後に gamedata の品目名 (item_index.name) を今の訳に合わせる
        bool namesMayDiffer = false;
        var translationsPath = Path.Combine(workDir, "translations.db");
        foreach (var job in jobs.OrderBy(j => Array.IndexOf(BundledDbNames, j.DbName)))
        {
            var jobWarnings = new List<string>();
            try
            {
                AppendStartupLog(workDir, $"[同梱DB] {job.DbName} を同梱 DB から作り直します (理由: {job.Reason})");
                var populate = job.DbName == "gamedata_cache.db" && TranslationsHavePersonalRows(translationsPath);
                var counts = RebuildBundledDb(job, workDir, populate, report, jobWarnings);
                if (job.DbName == "translations.db" && counts.Translations > 0) personalTranslationsImported = true;
                if (job.DbName is "translations.db" or "gamedata_cache.db") namesMayDiffer = true;
                AppendStartupLog(workDir, $"[同梱DB] {job.DbName} を作り直しました (今までの DB から取り込んだ個人データ: {counts}"
                    + (populate ? "。日本語名を付け直しました" : "") + ")");
                foreach (var w in jobWarnings) AppendStartupLog(workDir, $"[同梱DB] {w}");
                result.Warnings.AddRange(jobWarnings);
                jobWarnings.Clear();   // 下の記録の失敗で catch に入っても、もう一度書かないように
                // DB の入れ替えは済んでいる。記録を残せなかったときは、そのとおりに知らせる (次の起動でもう一度作り直す)
                state[job.DbName] = job.Stamp;
                try
                {
                    SaveBundledDbState(workDir, state);
                }
                catch (Exception sex)
                {
                    state.Remove(job.DbName);
                    AppendStartupLog(workDir, $"[同梱DB] {job.DbName} の入れ替えは済みましたが、作り直した記録 ({BundledDbStateFileName}) を残せませんでした (次の起動でもう一度作り直します): {sex}");
                    result.StateNotSaved.Add($"{job.DbName}: {sex.Message}");
                }
            }
            catch (Exception ex)
            {
                foreach (var w in jobWarnings) AppendStartupLog(workDir, $"[同梱DB] {w}");
                AppendStartupLog(workDir, $"[同梱DB] {job.DbName} の作り直しに失敗 (今までの DB はそのまま): {ex}");
                result.Failures.Add($"{job.DbName}: {ex.Message}");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        // translations の個人訳を取り込んだが gamedata は作り直さなかった (記録どおりで使える) とき、今の gamedata の日本語名を付け直す
        var gamedataPath = Path.Combine(workDir, "gamedata_cache.db");
        if (personalTranslationsImported && !jobs.Any(j => j.DbName == "gamedata_cache.db") && File.Exists(gamedataPath))
        {
            try
            {
                report?.Invoke(null, "gamedata_cache.db の日本語名を付け直しています...");
                using (var gx = new GameDataExtractor(workDir))
                    gx.PopulateJapaneseNames(translationsPath);
                AppendStartupLog(workDir, "[同梱DB] gamedata_cache.db の日本語名を付け直しました");
            }
            catch (Exception ex)
            {
                AppendStartupLog(workDir, $"[同梱DB] gamedata_cache.db の日本語名の付け直しに失敗: {ex}");
                result.Failures.Add($"gamedata_cache.db の日本語名の付け直し: {ex.Message}");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }

        // 品目名 (item_index.name) を今の訳に合わせる (作業フォルダに抽出済みの ini が無ければ合わせない。名前が変われば全文検索の表も作り直す)
        if (namesMayDiffer && File.Exists(gamedataPath))
        {
            try
            {
                report?.Invoke(null, "gamedata_cache.db の品目名を今の訳に合わせています...");
                GameDataExtractor.RefreshItemNamesFromTranslations(gamedataPath, workDir,
                    log: s => AppendStartupLog(workDir, $"[同梱DB] {s}"));
            }
            catch (Exception ex)
            {
                AppendStartupLog(workDir, $"[同梱DB] gamedata_cache.db の品目名を今の訳に合わせられませんでした: {ex}");
                result.Failures.Add($"gamedata_cache.db の品目名を今の訳に合わせる: {ex.Message}");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
            }
        }
        return result;
    }

    // 1 つの DB を作り直す: 同梱を "<名前>.tmp" に写す → 今までの DB から個人データを .tmp に取り込む →
    // (populateJapaneseNames なら) 日本語名の付け直しと全文検索の表の作り直し → 今までの DB を "<名前>.bak" に退避 → .tmp を正式名に。
    // どこで失敗しても今までの DB はそのまま残し、.tmp を消して例外を上げる
    internal static PersonalImportCounts RebuildBundledDb(BundledDbJob job, string workDir, bool populateJapaneseNames,
        Action<int?, string>? report, List<string> warnings)
    {
        var tmp = job.DestPath + ".tmp";
        DeleteDbFiles(tmp);   // 前回の残り
        EnsureFreeSpace(workDir, new FileInfo(job.SrcPath).Length);
        try
        {
            report?.Invoke(0, $"{job.DbName} を作業フォルダへ写しています...");
            CopyFileWithProgress(job.SrcPath, tmp, pct => report?.Invoke(pct, $"{job.DbName} を作業フォルダへ写しています... {pct}%"));

            PersonalImportCounts counts;
            using (var db = new SqliteConnection($"Data Source={tmp};Pooling=False"))
            {
                db.Open();
                report?.Invoke(null, $"{job.DbName} に今までの個人データを取り込んでいます...");
                counts = ImportPersonalData(job.DbName, job.DestPath, db, warnings);
            }

            if (populateJapaneseNames)
            {
                // PopulateJapaneseNames は最後に RebuildFts5Index で全文検索の表 (item_index_fts) も作り直す
                report?.Invoke(null, $"{job.DbName} の日本語名を付け直しています...");
                using var gx = new GameDataExtractor(workDir, tmp);
                gx.PopulateJapaneseNames(Path.Combine(workDir, "translations.db"));
            }
            SqliteConnection.ClearAllPools();

            if (ReplaceWithRebuilt(tmp, job.DestPath) is { } bakWarning) warnings.Add(bakWarning);
            return counts;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            try { DeleteDbFiles(tmp); } catch { }
            throw;
        }
    }

    // 作業フォルダのドライブの空きが、同梱 DB の大きさ + 余裕 (大きさの 1 割か 100MB の大きいほう) より少なければ例外を上げる。
    // 空きを調べられない (ネットワークのフォルダ等) ときは調べずに進む
    internal static void EnsureFreeSpace(string workDir, long srcLength)
    {
        long free;
        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(workDir)) ?? "";
            if (root.Length == 0) return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return; }
        const long MB = 1024 * 1024;
        var need = srcLength + Math.Max(srcLength / 10, 100 * MB);
        if (free < need)
            throw new IOException($"作業フォルダのドライブ ({root}) の空きが足りません (空き {free / MB:N0} MB、必要 {need / MB:N0} MB)");
    }

    // ファイルを写す (進み具合 % を返す)
    private static void CopyFileWithProgress(string srcPath, string destPath, Action<int>? progress)
    {
        using var input = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using var output = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        var total = input.Length;
        var buffer = new byte[4 << 20];
        long done = 0;
        int read, lastPct = -1;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            done += read;
            var pct = total > 0 ? (int)(done * 100 / total) : 100;
            if (pct != lastPct)
            {
                lastPct = pct;
                progress?.Invoke(pct);
            }
        }
        output.Flush(true);
    }

    // DB と横のファイル (-journal / -wal / -shm) を消す
    private static void DeleteDbFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        foreach (var s in SqliteSideFileSuffixes)
            if (File.Exists(path + s)) File.Delete(path + s);
    }

    // 作り直した .tmp を正式名にする。今までの DB (と横のファイル) は、まず "<名前>.bak.new" (と "<名前>.bak.new-journal" 等) へ動かし
    // (横のファイルが先、DB が最後)、
    // .tmp を正式名にできてから前の "<名前>.bak" と置き換える (入れ替えに失敗したら前の .bak はそのまま残る)。
    // .tmp を正式名にする前に失敗したら、動かしたものを元に戻して例外を上げる。
    // .bak への置き換えだけ失敗したときは、DB は入れ替わっているので例外にせず、知らせる文を返す (問題が無ければ null)
    internal static string? ReplaceWithRebuilt(string tmp, string dest)
    {
        foreach (var s in SqliteSideFileSuffixes)
            if (File.Exists(tmp + s)) throw new IOException($"{Path.GetFileName(tmp + s)} が残っているため、作り直した DB を使えません");

        var bak = dest + ".bak";
        var bakNew = dest + ".bak.new";
        var hadDest = File.Exists(dest);
        var moved = new List<(string From, string To)>();
        try
        {
            // 前回の残り (RecoverInterruptedReplace で片付けられなかったもの)
            DeleteDbFiles(bakNew);
            if (hadDest)
            {
                // 横のファイルを先に、DB を最後に動かす (途中で落ちても、正式名の DB が無いときは横のファイルも全部 .bak.new 側にある。
                // RecoverInterruptedReplace がこの順を前提に片付ける)
                foreach (var s in SqliteSideFileSuffixes)
                {
                    if (!File.Exists(dest + s)) continue;
                    File.Move(dest + s, bakNew + s);
                    moved.Add((dest + s, bakNew + s));
                }
                File.Move(dest, bakNew);
                moved.Add((dest, bakNew));
            }
            else
            {
                // DB の無い横のファイルは、新しい DB と組になると新しい DB を壊すので消す
                foreach (var s in SqliteSideFileSuffixes)
                    if (File.Exists(dest + s)) File.Delete(dest + s);
            }
            File.Move(tmp, dest);
        }
        catch
        {
            for (int i = moved.Count - 1; i >= 0; i--)
            {
                try { File.Move(moved[i].To, moved[i].From, overwrite: true); } catch { }
            }
            throw;
        }

        if (!hadDest) return null;
        try
        {
            FinishBak(bakNew, bak);
            return null;
        }
        catch (Exception ex)
        {
            return $"{Path.GetFileName(dest)}: 今までの DB を {Path.GetFileName(bak)} にできませんでした ({ex.Message})。{Path.GetFileName(bakNew)} として残しています";
        }
    }

    // 作り直し中の DB (.tmp) へ、今までの DB (oldPath) から個人データを取り込む (1 トランザクション)。
    // 列は両方にあるものだけを使う。今までの DB に表が無ければ飛ばす。今までの DB を開けない・表を読めないときは取り込まずに warnings へ書く。
    // 個人データは「今までの DB を残し、同梱 DB の分は今までの DB とぶつからないものだけ足す」(今までの DB 優先の和集合)。
    // ただし、この PC だけの印 (LocalOnlyMetaKeys) は今までの DB の値だけを使う。gamedata_cache.db では sqlite_sequence の knowledge の行を 1 行に直す
    internal static PersonalImportCounts ImportPersonalData(string dbName, string oldPath, SqliteConnection tmp, List<string> warnings)
    {
        var counts = new PersonalImportCounts();
        var isTranslations = dbName == "translations.db";
        var isGamedata = dbName == "gamedata_cache.db";
        using var tx = tmp.BeginTransaction();

        // この PC だけの印 (LocalOnlyMetaKeys) は利用者の入力ではないので、同梱 DB の値は使わない (今までの DB の値だけを下で入れ直す。
        // 今までの DB が無い・印が無ければ無いまま)
        if (isGamedata && TableExists(tmp, tx, "gamedata_meta"))
            ExecuteNonQuery(tmp, tx, $"DELETE FROM gamedata_meta WHERE key IN ({LocalOnlyMetaKeyList()})");
        // PC 間の同期の墓石 (sync_tombstones) も個人データ: 同梱側の墓石は捨て、今までの DB の墓石を下で写す (今までの DB が無ければ空のまま)
        if ((isTranslations || isGamedata) && TableExists(tmp, tx, SyncService.TombstonesTable))
            ExecuteNonQuery(tmp, tx, $"DELETE FROM {SyncService.TombstonesTable}");

        long? oldKnowledgeSeq = null;
        if ((isTranslations || isGamedata) && File.Exists(oldPath))
        {
            SqliteConnection? old = null;
            try
            {
                old = new SqliteConnection($"Data Source={oldPath};Mode=ReadWrite;Pooling=False");
                old.Open();
            }
            catch (Exception ex)
            {
                old?.Dispose();
                old = null;
                warnings.Add($"{dbName}: 今までの DB を開けないため、個人データを取り込めませんでした ({ex.Message})");
            }
            if (old != null)
            {
                using (old)
                {
                    if (isTranslations)
                    {
                        counts.Translations = CopyPersonalRows(old, tmp, tx, dbName, "translations", "手動訳・CSV・用語集の置換の訳",
                            requiredCols: ["key", "source"],
                            where: $"source IN ({string.Join(", ", PersonalTranslationSources.Select(s => $"'{s}'"))})",
                            conflictTarget: "key", updateCols: ["japanese", "source", "translator", "modified_at"], warnings,
                            // translator 列の無い古い版の DB の行は、translator を '' にする (同梱の AI の名前を残さない)
                            fillMissing: new Dictionary<string, object> { ["translator"] = "" });
                        counts.Glossary = CopyPersonalRows(old, tmp, tx, dbName, "glossary", "用語集",
                            requiredCols: ["english"], where: null,
                            conflictTarget: "english", updateCols: ["japanese", "created_at"], warnings);
                        // AI 翻訳で貯めた人名 (NameMasker.AddPersonNames)。同梱 DB に表が無ければ作ってから写す
                        ExecuteNonQuery(tmp, tx, TranslationDatabase.PersonNamesDdl);
                        counts.PersonNames = CopyPersonalRows(old, tmp, tx, dbName, "person_names", "AI 翻訳で貯めた人名",
                            requiredCols: ["name"], where: null,
                            conflictTarget: "name", updateCols: ["source_key", "created_at"], warnings);
                        counts.Tombstones = CopyTombstones(old, tmp, tx, dbName, warnings);
                    }
                    else
                    {
                        // 記憶: 今までの DB の行は id ごとそのまま。同梱 DB の行は、同じ本文 (content) が今までの DB に無いものだけ新しい id で足す
                        counts.Knowledge = MergeKnowledgeFromOld(old, tmp, tx, dbName, warnings);
                        // この PC だけの印: 今までの DB の値だけ (上で同梱 DB の値は消してある)
                        counts.LocalMeta = CopyPersonalRows(old, tmp, tx, dbName, "gamedata_meta", "この PC だけの印",
                            requiredCols: ["key", "value"], where: $"key IN ({LocalOnlyMetaKeyList()})",
                            conflictTarget: null, updateCols: [], warnings);
                        counts.Tombstones = CopyTombstones(old, tmp, tx, dbName, warnings);
                        try
                        {
                            if (TableExists(old, null, "sqlite_sequence"))
                            {
                                using var cmd = old.CreateCommand();
                                cmd.CommandText = "SELECT MAX(seq) FROM sqlite_sequence WHERE name = 'knowledge'";
                                var v = cmd.ExecuteScalar();
                                if (v != null && v != DBNull.Value) oldKnowledgeSeq = Convert.ToInt64(v);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 読めなければ同梱と移した行の id だけで決める (そうしたことを app_log に残す)
                            AppendStartupLog(Path.GetDirectoryName(Path.GetFullPath(oldPath)) ?? ".",
                                $"[同梱DB] {dbName}: 今までの DB の sqlite_sequence (knowledge の番号) を読めないため、同梱と取り込んだ行の id だけで決めます: {ex.Message}");
                        }
                    }
                }
            }
        }

        if (isGamedata) NormalizeKnowledgeSequence(tmp, tx, oldKnowledgeSeq);
        tx.Commit();
        return counts;
    }

    // PC 間の同期の墓石 (sync_tombstones) を今までの DB からそのまま写す (同梱側の墓石は ImportPersonalData の頭で捨ててある)。
    // 作り直し中の DB に表が無ければ作る (payload 列も)。今までの DB に表が無ければ 0
    private static int CopyTombstones(SqliteConnection old, SqliteConnection tmp, SqliteTransaction tx, string dbName, List<string> warnings)
    {
        SyncService.EnsureTombstonesTable(tmp, tx);
        return CopyPersonalRows(old, tmp, tx, dbName, SyncService.TombstonesTable, "同期の墓石",
            requiredCols: ["table_name", "row_key"], where: null, conflictTarget: null, updateCols: [], warnings);
    }

    // 記憶 (knowledge) を、今までの DB 優先で合わせる。今までの DB の行は id ごとそのまま入れ、作り直し中の DB (同梱) の行は、
    // 同じ本文 (content) が今までの DB に無いものだけ新しい id で足す。今までの DB に表が無ければ同梱のまま。取り込んだ今までの DB の行数を返す
    private static int MergeKnowledgeFromOld(SqliteConnection old, SqliteConnection tmp, SqliteTransaction tx, string dbName, List<string> warnings)
    {
        if (!TableExists(tmp, tx, "knowledge")) return 0;
        var tmpCols = TableColumns(tmp, tx, "knowledge");
        List<string> cols;
        var oldRows = new List<object[]>();
        try
        {
            if (!TableExists(old, null, "knowledge")) return 0;
            var oldCols = new HashSet<string>(TableColumns(old, null, "knowledge"), StringComparer.OrdinalIgnoreCase);
            if (!oldCols.Contains("content"))
            {
                warnings.Add($"{dbName}: 今までの DB の knowledge に content の列が無いため、チャットの記憶を取り込めませんでした");
                return 0;
            }
            cols = tmpCols.Where(oldCols.Contains).ToList();
            oldRows = ReadRows(old, null, "knowledge", cols, null);
        }
        catch (Exception ex)
        {
            warnings.Add($"{dbName}: 今までの DB の knowledge を読めないため、チャットの記憶を取り込めませんでした ({ex.Message})");
            return 0;
        }

        var contentIdx = cols.FindIndex(c => c.Equals("content", StringComparison.OrdinalIgnoreCase));
        var oldContents = new HashSet<string>(oldRows.Select(r => r[contentIdx]?.ToString() ?? ""), StringComparer.Ordinal);
        var bundledCols = tmpCols.Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
        var bundledRows = ReadRows(tmp, tx, "knowledge", bundledCols, null);
        var bundledContentIdx = bundledCols.FindIndex(c => c.Equals("content", StringComparison.OrdinalIgnoreCase));

        ExecuteNonQuery(tmp, tx, "DELETE FROM knowledge");
        InsertRows(tmp, tx, "knowledge", cols, oldRows, "INSERT OR REPLACE");
        if (bundledContentIdx >= 0)
            InsertRows(tmp, tx, "knowledge", bundledCols,
                bundledRows.Where(r => !oldContents.Contains(r[bundledContentIdx]?.ToString() ?? "")).ToList(), "INSERT");
        return oldRows.Count;
    }

    // 表の行を読む (列の順は cols)
    internal static List<object[]> ReadRows(SqliteConnection db, SqliteTransaction? tx, string table, IReadOnlyList<string> cols, string? where)
    {
        var rows = new List<object[]>();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(QuoteIdent))} FROM {QuoteIdent(table)}" + (where != null ? $" WHERE {where}" : "");
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var values = new object[cols.Count];
            r.GetValues(values);
            rows.Add(values);
        }
        return rows;
    }

    // 行を入れる (verb は "INSERT" / "INSERT OR REPLACE" / "INSERT OR IGNORE")。値の型 (整数・実数・BLOB・文字・NULL) のまま書く
    internal static void InsertRows(SqliteConnection db, SqliteTransaction? tx, string table, IReadOnlyList<string> cols, List<object[]> rows, string verb)
    {
        if (rows.Count == 0) return;
        using var ins = db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = $"{verb} INTO main.{QuoteIdent(table)} ({string.Join(", ", cols.Select(QuoteIdent))}) VALUES ({string.Join(", ", cols.Select((_, i) => $"@p{i}"))})";
        var ps = cols.Select((_, i) => ins.Parameters.Add($"@p{i}", SqliteType.Text)).ToArray();
        foreach (var values in rows)
        {
            for (int i = 0; i < ps.Length; i++)
            {
                ps[i].SqliteType = values[i] switch { long => SqliteType.Integer, double => SqliteType.Real, byte[] => SqliteType.Blob, _ => SqliteType.Text };
                ps[i].Value = values[i];
            }
            ins.ExecuteNonQuery();
        }
    }

    // 今までの DB の表 table の行 (where に合うもの) を、作り直し中の DB の同じ表へ入れる。入れた行数を返す。
    // conflictTarget があれば、その列がぶつかったときは updateCols (両方にある列だけ) を今までの DB の値で上書きする (無ければ行を足す)。
    // conflictTarget が無ければ INSERT OR REPLACE (ぶつかったら今までの DB の行にする)。
    // fillMissing: 作り直し中の DB にあって今までの DB に無い列に入れる値 (足す行・上書きする行の両方に入れる)。
    // 今までの DB に表が無い・requiredCols が無ければ 0。読めなければ warnings に書いて 0 (書き込みの失敗は例外のまま上げる)
    private static int CopyPersonalRows(SqliteConnection old, SqliteConnection tmp, SqliteTransaction tx, string dbName,
        string table, string label, string[] requiredCols, string? where, string? conflictTarget, string[] updateCols, List<string> warnings,
        Dictionary<string, object>? fillMissing = null)
    {
        if (!TableExists(tmp, tx, table)) return 0;
        var tmpCols = TableColumns(tmp, tx, table);

        List<string> cols;
        var rows = new List<object[]>();
        try
        {
            if (!TableExists(old, null, table)) return 0;
            var oldCols = new HashSet<string>(TableColumns(old, null, table), StringComparer.OrdinalIgnoreCase);
            if (requiredCols.Any(c => !oldCols.Contains(c)))
            {
                warnings.Add($"{dbName}: 今までの DB の {table} に {string.Join(" / ", requiredCols.Where(c => !oldCols.Contains(c)))} の列が無いため、{label}を取り込めませんでした");
                return 0;
            }
            cols = tmpCols.Where(oldCols.Contains).ToList();
            if (cols.Count == 0) return 0;
            using var cmd = old.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = $"SELECT {string.Join(", ", cols.Select(QuoteIdent))} FROM {QuoteIdent(table)}" + (where != null ? $" WHERE {where}" : "");
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var values = new object[cols.Count];
                r.GetValues(values);
                rows.Add(values);
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"{dbName}: 今までの DB の {table} を読めないため、{label}を取り込めませんでした ({ex.Message})");
            return 0;
        }
        if (rows.Count == 0) return 0;

        if (fillMissing != null)
        {
            var fill = fillMissing.Where(kv => tmpCols.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)
                                               && !cols.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            if (fill.Count > 0)
            {
                cols.AddRange(fill.Select(kv => kv.Key));
                rows = rows.Select(v => v.Concat(fill.Select(kv => kv.Value)).ToArray()).ToList();
            }
        }

        var colList = string.Join(", ", cols.Select(QuoteIdent));
        var paramList = string.Join(", ", cols.Select((_, i) => $"@p{i}"));
        string sql;
        if (conflictTarget != null)
        {
            var sets = updateCols.Where(c => cols.Contains(c, StringComparer.OrdinalIgnoreCase))
                .Select(c => $"{QuoteIdent(c)} = excluded.{QuoteIdent(c)}").ToList();
            sql = $"INSERT INTO main.{QuoteIdent(table)} ({colList}) VALUES ({paramList}) ON CONFLICT({QuoteIdent(conflictTarget)}) "
                + (sets.Count > 0 ? $"DO UPDATE SET {string.Join(", ", sets)}" : "DO NOTHING");
        }
        else
        {
            sql = $"INSERT OR REPLACE INTO main.{QuoteIdent(table)} ({colList}) VALUES ({paramList})";
        }

        using var ins = tmp.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = sql;
        var ps = cols.Select((_, i) => ins.Parameters.Add($"@p{i}", SqliteType.Text)).ToArray();
        foreach (var values in rows)
        {
            for (int i = 0; i < ps.Length; i++)
            {
                // 今までの DB の値の型 (整数・実数・BLOB・文字・NULL) のまま書く
                ps[i].SqliteType = values[i] switch
                {
                    long => SqliteType.Integer,
                    double => SqliteType.Real,
                    byte[] => SqliteType.Blob,
                    _ => SqliteType.Text,
                };
                ps[i].Value = values[i];
            }
            ins.ExecuteNonQuery();
        }
        return rows.Count;
    }

    // sqlite_sequence の knowledge の行を 1 行にする (同梱 DB に重複した行があったため)。値は knowledge の MAX(id)、
    // 同梱 DB の値、今までの DB の値の大きいほう (使ったことのある id を新しい記憶に使い回さないように)
    private static void NormalizeKnowledgeSequence(SqliteConnection tmp, SqliteTransaction tx, long? oldSeq)
    {
        if (!TableExists(tmp, tx, "knowledge") || !TableExists(tmp, tx, "sqlite_sequence")) return;
        using var cmd = tmp.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM knowledge";
        var maxId = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        cmd.CommandText = "SELECT COUNT(*), COALESCE(MAX(seq), 0) FROM sqlite_sequence WHERE name = 'knowledge'";
        long rowsBefore, seq;
        using (var r = cmd.ExecuteReader())
        {
            r.Read();
            rowsBefore = r.GetInt64(0);
            seq = r.GetInt64(1);
        }
        var value = Math.Max(maxId, Math.Max(seq, oldSeq ?? 0));
        cmd.CommandText = "DELETE FROM sqlite_sequence WHERE name = 'knowledge'";
        cmd.ExecuteNonQuery();
        if (rowsBefore > 0 || value > 0)
        {
            cmd.CommandText = "INSERT INTO sqlite_sequence(name, seq) VALUES('knowledge', @v)";
            cmd.Parameters.AddWithValue("@v", value);
            cmd.ExecuteNonQuery();
        }
    }

    // 「準備中」の小さな窓を出して、裏で作り直しを行う。終わったら窓を閉じて結果を返す
    private BundledDbSyncResult RunBundledDbSyncWithWindow(List<BundledDbJob> jobs, string workDir)
    {
        var text = new System.Windows.Controls.TextBlock { Text = "準備中...", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        var bar = new System.Windows.Controls.ProgressBar { Height = 16, Minimum = 0, Maximum = 100, IsIndeterminate = true };
        var note = new System.Windows.Controls.TextBlock
        {
            Text = "インストール・更新の後などに、同梱のデータベースを作業フォルダへ用意します。数分かかることがあります。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = System.Windows.Media.Brushes.Gray,
        };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(text);
        panel.Children.Add(bar);
        panel.Children.Add(note);
        var win = new Window
        {
            Title = "準備中",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            Content = panel,
        };

        var result = new BundledDbSyncResult();
        var finished = false;
        // 途中で閉じられると作り直しの途中で画面が出てしまうので、終わるまで閉じさせない
        win.Closing += (_, e) => { if (!finished) e.Cancel = true; };
        IProgress<(int? Pct, string Text)> progress = new Progress<(int? Pct, string Text)>(p =>
        {
            text.Text = p.Text;
            if (p.Pct is int v)
            {
                bar.IsIndeterminate = false;
                bar.Value = v;
            }
            else bar.IsIndeterminate = true;
        });
        win.ContentRendered += async (_, _) =>
        {
            try
            {
                result = await Task.Run(() => RunBundledDbSync(jobs, workDir, (pct, msg) => progress.Report((pct, msg))));
            }
            catch (Exception ex)
            {
                AppendStartupLog(workDir, $"[同梱DB] 作り直しに失敗: {ex}");
                result = new BundledDbSyncResult();
                result.Failures.Add($"同梱 DB: {ex.Message}");
            }
            finally
            {
                finished = true;
                win.Close();
            }
        };

        // この窓を閉じたときにアプリが終わらないように (最後の窓が閉じると終わる設定のため)、閉じるまで終わり方を変えておく
        var prevMode = ShutdownMode;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            win.ShowDialog();
        }
        finally
        {
            if (MainWindow == win) MainWindow = null;
            ShutdownMode = prevMode;
        }
        return result;
    }

    // 作り直しの失敗と、個人データを取り込めなかったことを、画面が出た後に知らせる
    private void NotifyBundledDbResult(BundledDbSyncResult result)
    {
        if (result.Failures.Count == 0 && result.Warnings.Count == 0 && result.StateNotSaved.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        if (result.Failures.Count > 0)
        {
            sb.AppendLine("同梱のデータベースを作業フォルダへ用意できませんでした。作業フォルダの今までの DB はそのままです (次の起動でもう一度試します)。");
            sb.AppendLine();
            foreach (var f in result.Failures) sb.AppendLine(f);
        }
        if (result.StateNotSaved.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"同梱のデータベースで作り直して入れ替えましたが、作り直した記録 ({BundledDbStateFileName}) を残せませんでした。次の起動でもう一度作り直します。");
            sb.AppendLine();
            foreach (var f in result.StateNotSaved) sb.AppendLine(f);
        }
        if (result.Warnings.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("同梱のデータベースで作り直しましたが、次のことがありました (今までの DB は作業フォルダに「<名前>.bak」として残しています)。");
            sb.AppendLine();
            foreach (var w in result.Warnings) sb.AppendLine(w);
        }
        sb.AppendLine();
        sb.Append("(詳しくは作業フォルダの app_log.txt)");
        var msg = sb.ToString();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            System.Windows.MessageBox.Show(msg, "同梱データベース",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning));
    }

    // 起動時の出来事を作業ディレクトリの app_log.txt に残す (MainWindow.Log と同じ書き先・書式)
    internal static void AppendStartupLog(string workDir, string msg)
    {
        try { File.AppendAllText(Path.Combine(workDir, "app_log.txt"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n", System.Text.Encoding.UTF8); } catch { }
    }

    private static bool CanWriteToDirectory(string dir, out string error)
    {
        try
        {
            var probe = Path.Combine(dir, $".write_test_{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string LocalOnlyMetaKeyList() => string.Join(", ", GameDataExtractor.LocalOnlyMetaKeys.Select(k => $"'{k}'"));

    private static void ExecuteNonQuery(SqliteConnection db, SqliteTransaction? tx, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection db, SqliteTransaction? tx, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = @t";
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static List<string> TableColumns(SqliteConnection db, SqliteTransaction? tx, string table)
    {
        var cols = new List<string>();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA main.table_info({QuoteIdent(table)})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }

    private static string QuoteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static List<string> DetectFromLauncherLog()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "rsilauncher", "logs", "log.log");

        if (!File.Exists(logPath)) return new List<string>();

        var channelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var regex = new Regex(@"\[Launcher::launch\] Launching Star Citizen \w+ from \((.+?)\)");

        try
        {
            foreach (var line in File.ReadLines(logPath))
            {
                var m = regex.Match(line);
                if (!m.Success) continue;
                var path = m.Groups[1].Value.Replace("\\\\", "\\");
                var channel = Path.GetFileName(path);
                channelPaths[channel] = path;
            }
        }
        catch { }

        if (channelPaths.Count == 0) return new List<string>();

        var ordered = new List<string>();
        foreach (var ch in new[] { "PTU", "EPTU", "TECH-PREVIEW", "HOTFIX", "LIVE" })
        {
            if (channelPaths.TryGetValue(ch, out var p))
            {
                ordered.Add(p);
                channelPaths.Remove(ch);
            }
        }
        ordered.AddRange(channelPaths.Values);

        return ordered.Where(Directory.Exists).ToList();
    }
}
