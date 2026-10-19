using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 作業フォルダの DB (gamedata_cache.db / trade_cache.db / translations.db) を WAL (journal_mode=WAL) で使うための道具。
// WAL では書き込み中でもほかの接続が読めるので、DB が書き込み中のときに画面の読み出しが待たない。
// journal_mode=WAL は DB のファイルに残る (次に開いた接続も WAL)。なので切り替えは、起動したとき (SqliteWal.Ensure) と、
// DB を新しく作った・作り直したとき (EnsureOn) に行う。
// WAL では DB の本体と横のファイル (-wal / -shm) がそろって 1 つの DB:
//   - 書き込みはまず -wal に入り、チェックポイントで本体に書き戻される。本体だけを写すと -wal の中身が抜ける
//     → 写しは VACUUM INTO (SnapshotTo) かバックアップ API で作る。
//   - DB の本体を消す・置き換えるときは、古い -wal / -shm も消す (古い -wal が別の DB に当たると壊れる) → DeleteSideFiles。
//   - 本体の更新時刻・長さは、チェックポイントまで変わらないことがある → 変わったかの印は FileStamp (-wal も見る)。
// ネットワークのドライブでは WAL を使えない (SQLite の仕様: 共有メモリ (-shm) を使うため) ので、DELETE 方式のままにする
internal static class SqliteWal
{
    // WAL にする DB (作業フォルダの名前)
    public static readonly string[] WalDbNames = ["gamedata_cache.db", "trade_cache.db", "translations.db"];
    // SQLite が DB の横に作るファイル
    public static readonly string[] SideFileSuffixes = ["-wal", "-shm", "-journal"];

    // 切り替えを試した DB (EnsureCore)
    private static readonly HashSet<string> Tried = new();

    // 切り替えに使う時間切れ (秒)。ほかの接続が読み書きしている最中は切り替えを待つので、長く待たずに今の方式のまま進む
    private const int SwitchTimeoutSec = 2;

    // ネットワークのドライブ (UNC のパス・割り当てたネットワークのドライブ) か。調べられなければ false
    public static bool IsNetworkPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            // 長い形 (\\?\ / \\.\) のパス: \\?\UNC\ はネットワーク、ほか (\\?\C:\... など) は前置きを除いたローカルのパスで見る
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || full.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) full = full[4..];
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    // path の DB を WAL にする (ネットワークのドライブなら DELETE にする)。切り替えた後の journal_mode を返す (ファイルが無い・失敗したら null)。
    // 切り替えられなかったとき (ほかの接続が読み書き中・壊れている等) は今の方式のまま使う (log に出して例外にはしない)
    public static string? Ensure(string path, Action<string>? log = null)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var db = new SqliteConnection($"Data Source={path};Pooling=False");
            db.Open();
            return EnsureCore(db, path, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"{Path.GetFileName(path)} の journal_mode を切り替えられませんでした (今の方式のまま使います): {ex.Message}");
            return null;
        }
    }

    // 開いた接続 (読み書きできる接続。トランザクションの外) の DB を WAL にする (DB を作った・開いたところで呼ぶ)。もう WAL なら問い合わせ 1 回だけ。
    // 失敗しても例外にしない
    public static void EnsureOn(SqliteConnection db, Action<string>? log = null)
    {
        try { EnsureCore(db, db.DataSource, log); }
        catch (Exception ex) { log?.Invoke($"{Path.GetFileName(db.DataSource)} の journal_mode を切り替えられませんでした (今の方式のまま使います): {ex.Message}"); }
    }

    private static string? EnsureCore(SqliteConnection db, string path, Action<string>? log)
    {
        var want = IsNetworkPath(path) ? "delete" : "wal";
        using var cmd = db.CreateCommand();
        cmd.CommandTimeout = SwitchTimeoutSec;
        cmd.CommandText = "PRAGMA journal_mode";
        var now = cmd.ExecuteScalar()?.ToString()?.ToLowerInvariant();
        if (now == want) return now;
        // メモリ上・一時の DB (DataSource が空) は切り替えない
        if (string.IsNullOrEmpty(path)) return now;
        // 同じファイル (パスと作った時刻が同じ) の切り替えは 1 回だけ試す (切り替えられなかった DB を開くたびに待たないように。
        // DB を作り直したら作った時刻が変わるので、また試す)
        string key;
        try { key = Path.GetFullPath(path).ToLowerInvariant() + "|" + File.GetCreationTimeUtc(path).Ticks; }
        catch { key = path; }
        lock (Tried)
            if (!Tried.Add(key)) return now;
        cmd.CommandText = $"PRAGMA journal_mode={want.ToUpperInvariant()}";
        var after = cmd.ExecuteScalar()?.ToString()?.ToLowerInvariant();
        log?.Invoke(after == want
            ? $"{Path.GetFileName(path)} の journal_mode を {now} から {after} にしました" + (want == "delete" ? " (ネットワークのドライブでは WAL を使えないため)" : "")
            : $"{Path.GetFileName(path)} の journal_mode を {want} にできませんでした (今は {after})");
        return after;
    }

    // WAL の中身を本体に書き戻して -wal を空にする (PRAGMA wal_checkpoint(TRUNCATE))。ほかの接続が読んでいると最後までできない。
    // 最後まで書き戻せたら true (WAL でない DB・ファイルが無いときも true)。失敗しても例外にしない
    // timeoutSec: ほかの接続が書いているときに待つ秒数 (CommandTimeout。1 以上)
    public static bool Checkpoint(string path, Action<string>? log = null, int timeoutSec = SwitchTimeoutSec)
    {
        if (!File.Exists(path)) return true;
        try
        {
            // Mode=ReadWrite: 無ければ作らない (File.Exists の後に退避・削除されても、空の DB を作らない。開けなければ下で受け止める)
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False");
            db.Open();
            return CheckpointOn(db, log, timeoutSec);
        }
        catch (Exception ex)
        {
            log?.Invoke($"{Path.GetFileName(path)} のチェックポイントに失敗しました: {ex.Message}");
            return false;
        }
    }

    // 開いた接続の DB のチェックポイント (TRUNCATE)。最後まで書き戻せたら true
    public static bool CheckpointOn(SqliteConnection db, Action<string>? log = null, int timeoutSec = SwitchTimeoutSec)
    {
        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandTimeout = Math.Max(1, timeoutSec);
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using var r = cmd.ExecuteReader();
            // 結果は (busy, log, checkpointed)。WAL でない DB は (0, -1, -1)
            if (!r.Read()) return true;
            var busy = r.GetInt64(0);
            if (busy != 0) log?.Invoke($"{Path.GetFileName(db.DataSource)} のチェックポイントは、ほかの接続が読み書きしているため最後までできませんでした");
            return busy == 0;
        }
        catch (Exception ex)
        {
            log?.Invoke($"{Path.GetFileName(db.DataSource)} のチェックポイントに失敗しました: {ex.Message}");
            return false;
        }
    }

    // 開いた接続の DB のチェックポイント (TRUNCATE)。ほかの接続が読んでいて途中で止まったら、裏で少し後 (RetryDelayMs ごと、RetryCount 回) に
    // 別の接続で試し直す (それでも止まったら、アプリを閉じるときのチェックポイント (App.CheckpointWorkDbs) で空にする)
    private const int RetryDelayMs = 5000;
    private const int RetryCount = 3;
    public static void CheckpointOrRetryLater(SqliteConnection db, Action<string>? log = null)
    {
        if (CheckpointOn(db, log)) return;
        var path = db.DataSource;
        if (string.IsNullOrEmpty(path)) return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < RetryCount; i++)
            {
                await Task.Delay(RetryDelayMs);
                if (Checkpoint(path, log)) return;
            }
        });
    }

    // アプリを閉じるときのチェックポイント。paths の DB (無い DB は飛ばす) を、空振り (ほかの接続が読み書きしている) したら
    // retryDelayMs を置いて、budget の間やり直す。DB ごとの試しの前に残りの時間を見て、1 回の試しで書き込みを待つ時間 (CommandTimeout。秒で、1 秒まで) と
    // やり直しの間の待ちも残りの時間を超えないようにする (CommandTimeout は秒なので、残りが 1 秒を切ったら試さない)。
    // DB ごとの結果 (成功か、空振りして -wal が何 KB 残ったか) を log に出す
    // 全部の DB のチェックポイントが成功したら true (無い DB は数えない)
    public static bool CheckpointForExit(IEnumerable<string> paths, Action<string> log, TimeSpan budget, int retryDelayMs = 250)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pending = paths.Where(File.Exists).ToList();
        var tries = pending.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
        var lastWhy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TimeSpan Remaining() => budget - sw.Elapsed;
        var outOfTime = false;
        while (pending.Count > 0 && !outOfTime)
        {
            foreach (var path in pending.ToList())
            {
                if (Remaining() < TimeSpan.FromSeconds(1)) { outOfTime = true; break; }
                tries[path]++;
                string? why = null;
                if (Checkpoint(path, m => why = m, 1))
                {
                    pending.Remove(path);
                    log($"閉じるときのチェックポイント: {Path.GetFileName(path)} は成功しました (-wal {WalKb(path)}、{tries[path]} 回目)");
                }
                else if (why != null) lastWhy[path] = why;
            }
            if (pending.Count == 0 || outOfTime) break;
            var wait = Remaining() - TimeSpan.FromSeconds(1);   // 待った後にもう 1 回試せる (1 秒) だけ残す
            if (wait <= TimeSpan.Zero) break;
            Thread.Sleep(wait < TimeSpan.FromMilliseconds(retryDelayMs) ? wait : TimeSpan.FromMilliseconds(retryDelayMs));
        }
        foreach (var path in pending.Where(p => tries[p] == 0))
            log($"閉じるときのチェックポイント: {Path.GetFileName(path)} は持ち時間 ({budget.TotalSeconds:F1} 秒) が尽きたので試していません (-wal {WalKb(path)})。次に開いたときに -wal から読みます");
        foreach (var path in pending.Where(p => tries[p] > 0))
            log($"閉じるときのチェックポイント: {Path.GetFileName(path)} は空振りしました (-wal {WalKb(path)} が残っています。{tries[path]} 回試しました" +
                (lastWhy.TryGetValue(path, out var w) ? $"。{w}" : "") + ")。次に開いたときに -wal から読みます");
        return pending.Count == 0;
    }

    private static string WalKb(string path)
    {
        try
        {
            var fi = new FileInfo(path + "-wal");
            return fi.Exists ? $"{(fi.Length + 1023) / 1024:N0} KB" : "なし";
        }
        catch { return "不明"; }
    }

    // 同梱する DB (release.ps1 が publish に写したもの) を DELETE 方式の 1 つのファイルにして確かめる (アプリの --prepare-bundled-db)。
    // -wal に中身があれば止める (写す前のアプリが閉じていない)。DELETE にした後、見出しが 1/1 (DELETE 方式)・quick_check が ok・横のファイルが無いことを確かめる。
    // 問題が無ければ null、あればその訳
    public static string? PrepareForBundle(string path)
    {
        if (!File.Exists(path)) return $"{path} がありません";
        var wal = path + "-wal";
        if (File.Exists(wal) && new FileInfo(wal).Length > 0) return $"{wal} が 0 バイトではありません (DB の中身がまだ -wal にあります)";
        try
        {
            using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandTimeout = 0;
                cmd.CommandText = "PRAGMA journal_mode=DELETE";
                var mode = cmd.ExecuteScalar()?.ToString()?.ToLowerInvariant();
                if (mode != "delete") return $"{path} を DELETE 方式にできませんでした (今は {mode})";
                cmd.CommandText = "PRAGMA quick_check";
                var qc = cmd.ExecuteScalar()?.ToString();
                if (qc != "ok") return $"{path} の quick_check が ok ではありません: {qc}";
            }
            SqliteConnection.ClearAllPools();
            var header = new byte[100];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                fs.ReadExactly(header);
            // 見出しの 18・19 バイト目: 1 = DELETE などの方式 (rollback journal)、2 = WAL
            if (header[18] != 1 || header[19] != 1) return $"{path} の見出しが DELETE 方式 (1/1) ではありません ({header[18]}/{header[19]})";
            foreach (var sfx in SideFileSuffixes)
                if (File.Exists(path + sfx)) return $"{path + sfx} が残っています";
            return null;
        }
        catch (Exception ex) { return $"{path} を確かめられませんでした: {ex.Message}"; }
    }

    // src の DB の一貫した写しを dest に作る (VACUUM INTO。-wal の中身も入る。写しは DELETE 方式の 1 つのファイルで、横のファイルは無い)。
    // dest に前のファイルがあれば消してから作る。ほかの接続が書き込み中でも、その時点の中身で写せる
    public static void SnapshotTo(string src, string dest)
    {
        DeleteDbFiles(dest);
        using var conn = new SqliteConnection($"Data Source={src};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = 0;
        cmd.CommandText = "VACUUM INTO @p";
        cmd.Parameters.AddWithValue("@p", dest);
        cmd.ExecuteNonQuery();
    }

    // SnapshotTo で写す。失敗したら (壊れた DB は VACUUM INTO が失敗する)、今までどおり本体と横のファイル (-wal / -shm) を組で File.Copy する
    // (dest の横に同じ名前で置くので、写しを開くと -wal の中身も読まれる)。組で写したら、その訳 (VACUUM INTO の失敗) を返す。写せたら null
    public static string? SnapshotOrCopyTo(string src, string dest)
    {
        try
        {
            SnapshotTo(src, dest);
            return null;
        }
        catch (Exception ex)
        {
            DeleteDbFiles(dest);
            File.Copy(src, dest, true);
            foreach (var sfx in new[] { "-wal", "-shm" })
                if (File.Exists(src + sfx)) File.Copy(src + sfx, dest + sfx, true);
            return ex.Message;
        }
    }

    // DB の横のファイル (-wal / -shm / -journal) を消す。DB の接続を全部閉じてから呼ぶ (開いている接続があると消せずに例外)
    public static void DeleteSideFiles(string path)
    {
        foreach (var s in SideFileSuffixes)
            if (File.Exists(path + s)) File.Delete(path + s);
    }

    // DB の本体と横のファイルを消す
    public static void DeleteDbFiles(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        DeleteSideFiles(path);
    }

    // DB が変わったかの印 (本体と -wal の、パス・最終更新時刻・長さ)。無ければ「無し」。
    // WAL では書き込みはまず -wal に入り、本体の更新時刻・長さはチェックポイントまで変わらないことがあるので、-wal も見る。
    // 0 バイトの -wal (中身が無い。チェックポイントの後・読むだけの接続が作ったもの) は、無いのと同じに扱う (できる・消えるだけで印を変えない)
    public static string FileStamp(string path)
    {
        static string One(string p, bool emptyIsNone)
        {
            var fi = new FileInfo(p);
            if (!fi.Exists || (emptyIsNone && fi.Length == 0)) return "無し";
            return $"{fi.LastWriteTimeUtc.Ticks}|{fi.Length}";
        }
        try { return $"{Path.GetFullPath(path)}|{One(path, false)}|wal:{One(path + "-wal", true)}"; }
        catch { return $"{path}|?"; }
    }
}
