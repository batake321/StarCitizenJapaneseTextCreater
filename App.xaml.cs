using System.Text.RegularExpressions;
using System.Windows;

namespace StarCitizenJapaneseTextCreater;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();
    public static string ConfigPath { get; private set; } = "";
    // この起動中は個人の設定ファイルへ保存しない (壊れた設定ファイルの控えを作れなかったとき。AppConfigLoader)
    public static bool ConfigSaveDisabled { get; private set; }

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
        try
        {
            Directory.CreateDirectory(Config.WorkingDirectory);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Working Directory を作成できません:\n{Config.WorkingDirectory}\n\nエラー: {ex.Message}\n\n設定タブで別のパスを指定してください。",
                "起動エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        // 同梱DBをWorkDirにマージ（同梱版が新しければ差分インポート）
        var baseDir = AppContext.BaseDirectory;
        foreach (var dbName in new[] { "translations.db", "gamedata_cache.db", "equipment_cache.db" })
        {
            var src = Path.Combine(baseDir, dbName);
            var dest = Path.Combine(Config.WorkingDirectory, dbName);
            if (!File.Exists(src)) continue;
            if (!File.Exists(dest))
            {
                CopyBundledDb(src, dest);
                continue;
            }
            if (File.GetLastWriteTime(src) > File.GetLastWriteTime(dest))
                MergeBundledDb(src, dest);
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
        Task.Run(() => DatabaseBackupService.ExportAsync(translationDb, indexDb, outPath,
            s => Console.WriteLine($"  {s}"))).GetAwaiter().GetResult();

        if (File.Exists(outPath))
            Console.WriteLine($"Backup: {new FileInfo(outPath).Length / 1024.0:N0} KB -> {outPath}");

        Console.WriteLine("Export complete.");
    }

    // 同梱 DB を作業ディレクトリへ初めて写す。写した DB から、この PC だけの印 (売り場・価格の取得中・手動中止。
    // GameDataExtractor.LocalOnlyMetaKeys) を消す (配布の DB を受け取っただけで起動時の取得が始まらないように)
    internal static void CopyBundledDb(string srcPath, string destPath)
    {
        File.Copy(srcPath, destPath);
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destPath};Pooling=False");
            db.Open();
            using var chk = db.CreateCommand();
            chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='gamedata_meta'";
            if ((long)(chk.ExecuteScalar() ?? 0L) == 0) return;
            using var del = db.CreateCommand();
            del.CommandText = $"DELETE FROM gamedata_meta WHERE key IN ({LocalOnlyMetaKeyList()})";
            del.ExecuteNonQuery();
        }
        catch { }
    }

    private static string LocalOnlyMetaKeyList() => string.Join(", ", GameDataExtractor.LocalOnlyMetaKeys.Select(k => $"'{k}'"));

    private static void MergeBundledDb(string srcPath, string destPath)
    {
        try
        {
            using var src = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={srcPath};Mode=ReadOnly");
            src.Open();
            using var dest = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destPath}");
            dest.Open();

            // テーブル一覧を取得
            using var listCmd = src.CreateCommand();
            listCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            var tables = new List<string>();
            using (var r = listCmd.ExecuteReader())
                while (r.Read()) tables.Add(r.GetString(0));

            using var tx = dest.BeginTransaction();
            foreach (var table in tables)
            {
                // 宛先にテーブルがなければスキップ
                using var chk = dest.CreateCommand();
                chk.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
                chk.Parameters.AddWithValue("@n", table);
                if ((long)(chk.ExecuteScalar() ?? 0) == 0) continue;

                using var readCmd = src.CreateCommand();
                // gamedata_meta のこの PC だけの印 (取得中・手動中止) は取り込まない (利用者側の印はそのまま)
                readCmd.CommandText = table == "gamedata_meta"
                    ? $"SELECT * FROM {table} WHERE key NOT IN ({LocalOnlyMetaKeyList()})"
                    : $"SELECT * FROM {table}";
                using var reader = readCmd.ExecuteReader();
                var colCount = reader.FieldCount;
                var colNames = new string[colCount];
                for (int i = 0; i < colCount; i++) colNames[i] = reader.GetName(i);
                var colList = string.Join(", ", colNames);
                var paramList = string.Join(", ", colNames.Select((_, i) => $"@p{i}"));

                string sql;
                if (table == "translations" && colNames.Contains("japanese"))
                {
                    // 未翻訳レコードのみ上書き、手動翻訳は保護
                    sql = $"INSERT INTO {table} ({colList}) VALUES ({paramList}) ON CONFLICT(key) DO UPDATE SET japanese = excluded.japanese, source = excluded.source, translator = excluded.translator, modified_at = excluded.modified_at WHERE {table}.japanese IS NULL OR {table}.japanese = ''";
                }
                else
                {
                    sql = $"INSERT OR IGNORE INTO {table} ({colList}) VALUES ({paramList})";
                }

                while (reader.Read())
                {
                    using var ins = dest.CreateCommand();
                    ins.CommandText = sql;
                    ins.Transaction = tx;
                    for (int i = 0; i < colCount; i++)
                        ins.Parameters.AddWithValue($"@p{i}", reader.GetValue(i));
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
        catch { }
    }

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
