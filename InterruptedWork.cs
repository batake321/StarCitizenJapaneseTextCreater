using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 途中で切れた処理 (インデックス構築・装備データの再抽出・バックアップの取り込み・PC 間の同期の取り込み) の「途中」の印と、次の起動での戻し。
// 控えのファイル・表は、処理が成功した後に消し損ねても残る (削除の失敗は知らせるだけで続ける) ので、控えがあるだけでは途中で切れたのか
// 見分けられない。そこで、DB を書き換え始める前に今の作業フォルダ (App.Config.WorkingDirectory) へ印 (interrupted_{kind}.json。
// 戻す DB と控えの場所を絶対パスで書く) を書き、成功したとき・控えから戻したとき (戻せなかったときも) に消す。
//   ・印を消せなければ「済み」(Completed) の中身で書き直す。起動のときは済みの印は戻さずに消すだけ
//   ・次の起動で済みでない印があれば、1 回だけ、DB ごとに控えから戻す (App.OnStartup。ほかの DB の接続を開く前)。
//     戻せた DB の控えは消し、戻せなかった DB の控えは「<控え>.failed-<日時>」に名前を変えて残す (次の処理で上書きされないように)。
//     どちらの場合も最後に印を消す (次の起動でまた戻し直さない)。印が読めないときは触らない
//   ・処理の途中 (取り消し・失敗) で控えから戻せなかったときも同じく、控えを .failed-<日時> にして残し、知らせ、印を消す
//   ・済みでない印が残っている間は、同じ種類の処理を始めない (PendingLabel)
internal static class InterruptedWork
{
    public const string KindBuild = "build";      // インデックス構築: gamedata_cache.db ← gamedata_cache.db.prebuild.bak
    public const string KindEquip = "equip";      // 装備データの再抽出: gamedata_cache.db の *_bak 表 (EquipmentTables)
    public const string KindImport = "import";    // バックアップの取り込み: 取り込み先の DB ← *.import.bak (全部)
    public const string KindSync = "sync";        // PC 間の同期の取り込み: trade_cache.db / translations.db ← *.sync.bak (全部)

    public sealed class Marker
    {
        public string Kind { get; set; } = "";
        public string Label { get; set; } = "";              // 知らせに出す処理の名前 (例 "インデックス構築")
        public Dictionary<string, string> Backups { get; set; } = new();   // 戻す先の DB → 控えのファイル (どちらも絶対パス。再抽出は空)
        public string? Db { get; set; }                       // 再抽出: *_bak 表のある DB (絶対パス)
        public string? NameSource { get; set; }               // 再抽出: 前の item_index.name の作り方 (gamedata_meta。null なら無かった)
        public string StartedAt { get; set; } = "";
        public bool Completed { get; set; }                   // 済み (成功・戻した後に印を消せなかったとき)。起動のときは消すだけ
    }

    // 印を置くフォルダ: 今の作業フォルダ。決まっていなければ fallback
    public static string MarkerDir(string fallback)
    {
        var w = App.Config?.WorkingDirectory;
        return !string.IsNullOrWhiteSpace(w) ? Path.GetFullPath(w) : Path.GetFullPath(fallback);
    }

    public static string MarkerPath(string dir, string kind) => Path.Combine(dir, $"interrupted_{kind}.json");

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    // 印を書く (一時ファイルに書いてから置き換える)。DB・控えの場所は絶対パスにする。書けなければ例外 (処理は始めない)。
    // 書いた印の場所を返す。処理はこの場所を覚えておき、書き直し (WriteAt)・消す (Clear)・控えを残す (KeepFailedBackup) はこの場所に対して行う
    // (処理の途中で作業フォルダの設定が変わっても、ずれないように)
    public static string Write(string fallbackDir, Marker marker)
    {
        var path = MarkerPath(MarkerDir(fallbackDir), marker.Kind);
        WriteAt(path, marker);
        return path;
    }

    public static void WriteAt(string markerPath, Marker marker)
    {
        marker.StartedAt = string.IsNullOrEmpty(marker.StartedAt) ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") : marker.StartedAt;
        marker.Backups = marker.Backups.ToDictionary(kv => Path.GetFullPath(kv.Key), kv => Path.GetFullPath(kv.Value), StringComparer.OrdinalIgnoreCase);
        if (marker.Db != null) marker.Db = Path.GetFullPath(marker.Db);
        WriteFile(markerPath, marker);
    }

    private static void WriteFile(string path, Marker marker)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    // 印 (Write が返した場所) を消す。消せなければ「済み」の中身で書き直す (起動のときに戻さないように)。どちらもできなければ log に出して false
    public static bool Clear(string markerPath, string kind, Action<string>? log = null) => ClearAt(markerPath, kind, log);

    private static bool ClearAt(string path, string kind, Action<string>? log)
    {
        Exception? last = null;
        for (int i = 0; i < 3; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return true; }
            catch (Exception ex) { last = ex; Thread.Sleep(200); }
        }
        try
        {
            WriteFile(path, new Marker { Kind = kind, Completed = true, StartedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") });
            log?.Invoke($"途中の印 {path} を消せなかったので ({last?.Message})、済みの印に書き直しました (次の起動で消します)");
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"途中の印 {path} を消せず、済みの印にも書き直せませんでした ({ex.Message})。次の起動で、済んだ処理を前の状態に戻してしまうおそれがあります。このファイルを手で消してください");
            return false;
        }
    }

    // 済みでない印が残っていれば、その処理の名前 (残っていなければ null)。同じ種類の処理を始める前に確かめる
    public static string? PendingLabel(string fallbackDir, string kind)
    {
        var path = MarkerPath(MarkerDir(fallbackDir), kind);
        if (!File.Exists(path)) return null;
        try
        {
            var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));
            if (m is { Completed: true }) return null;
            return string.IsNullOrEmpty(m?.Label) ? kind : m!.Label;
        }
        catch { return kind; }
    }

    // 始める前の確かめ: 済みでない印が残っている、または戻せなかった控えが元の名前のまま残っている (held_{kind}.json) ときは、
    // 始めずに例外 (知らせる)。次の処理がその控えを上書き・削除しないように
    public static void ThrowIfPending(string fallbackDir, string kind)
    {
        var dir = MarkerDir(fallbackDir);
        if (PendingLabel(fallbackDir, kind) is { } label)
            throw new InvalidOperationException($"前回の{label}の途中の印 ({MarkerPath(dir, kind)}) が残っているため、始めませんでした。アプリを起動し直すと前の状態に戻してから印を消します");
        var held = ReadHeld(dir, kind).Where(HeldExists).ToList();
        WriteHeld(dir, kind, held);
        if (held.Count > 0)
            throw new InvalidOperationException($"前回、元の状態に戻せなかった控えが元の名前のまま残っているため、始めませんでした (始めると控えが上書き・削除されます): {string.Join(", ", held)}。" +
                                                "控えを別の場所へ写すか、要らなければ消してから、もう一度始めてください");
    }

    // 戻せなかった控えを「<控え>.failed-<日時>」に名前を変えて残す。新しい場所を返す。変えられなければ元の名前のまま残し、
    // 印のフォルダの held_{kind}.json に書く (ThrowIfPending が次の同じ処理を止める)。呼び出し元は、戻せなかった控えを消さないこと
    public static string KeepFailedBackup(string backup, string markerPath, string kind, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(backup)) return backup;
            var dest = $"{backup}.failed-{Stamp()}";
            File.Move(backup, dest);
            return dest;
        }
        catch (Exception ex)
        {
            log?.Invoke($"控え {backup} の名前を変えられませんでした ({ex.Message})。元の名前のまま残します (この控えがある間は、同じ処理を始めません)");
            AddHeld(markerPath, kind, backup, log);
            return backup;
        }
    }

    // 元の名前のまま残した控えの一覧 (ファイルの場所か、"<DB の場所>::<表の名前>")
    private static string HeldPath(string dir, string kind) => Path.Combine(dir, $"held_{kind}.json");

    public static void AddHeld(string markerPath, string kind, string entry, Action<string>? log = null)
    {
        var dir = Path.GetDirectoryName(markerPath) ?? ".";
        try
        {
            var list = ReadHeld(dir, kind);
            if (!list.Contains(entry, StringComparer.OrdinalIgnoreCase)) list.Add(entry);
            File.WriteAllText(HeldPath(dir, kind), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { log?.Invoke($"元の名前のまま残した控えの一覧 ({HeldPath(dir, kind)}) を書けませんでした: {ex.Message}"); }
    }

    private static List<string> ReadHeld(string dir, string kind)
    {
        try
        {
            var path = HeldPath(dir, kind);
            return File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch { return new(); }
    }

    // 一覧を書き直す (もう無い控えを外す)。空なら一覧のファイルを消す
    private static void WriteHeld(string dir, string kind, List<string> list)
    {
        var path = HeldPath(dir, kind);
        try
        {
            if (list.Count == 0) { if (File.Exists(path)) File.Delete(path); return; }
            File.WriteAllText(path, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // 控えがまだあるか (ファイルか、DB の中の表)。確かめられなければ、あるものとする
    private static bool HeldExists(string entry)
    {
        var sep = entry.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return File.Exists(entry);
        var db = entry[..sep];
        var table = entry[(sep + 2)..];
        if (!File.Exists(db)) return false;
        try
        {
            using var c = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
            cmd.Parameters.AddWithValue("@n", table);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return true; }
    }

    // 起動のとき: 作業フォルダの印を見て、途中で切れた処理を 1 回だけ元に戻す。知らせる文 (起動の後に一度だけ出す) を返す。log は app_log.txt
    public static List<string> RecoverAll(string workDir, Action<string> log)
    {
        var notices = new List<string>();
        var dir = Path.GetFullPath(workDir);
        foreach (var kind in new[] { KindBuild, KindEquip, KindImport, KindSync })
        {
            var path = MarkerPath(dir, kind);
            if (!File.Exists(path)) continue;
            Marker? m = null;
            try { m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path)); }
            catch (Exception ex) { log($"途中の印 {path} を読めませんでした: {ex.Message}"); }
            if (m == null)
            {
                notices.Add($"前回、途中で終わった処理の印 ({Path.GetFileName(path)}) を読めなかったため、何もしていません。印は {path} に残しています。");
                continue;
            }
            if (m.Completed)
            {
                // 済んだ処理の印 (消し損ねたもの)。戻さずに消すだけ
                if (!ClearAt(path, kind, log)) notices.Add($"済んだ処理の印 {path} を消せませんでした。手で消してください。");
                else log($"済んだ処理の印 {Path.GetFileName(path)} を消しました");
                continue;
            }
            var label = string.IsNullOrEmpty(m.Label) ? kind : m.Label;
            log($"前回、{label}の途中 ({m.StartedAt} に開始) でアプリが終了していました。{label}の前の状態に戻します");
            var failures = new List<string>();
            try
            {
                if (kind == KindEquip)
                {
                    if (string.IsNullOrEmpty(m.Db) || !File.Exists(m.Db)) failures.Add($"DB が見つかりません ({m.Db})");
                    else
                    {
                        try { GameDataExtractor.RestoreEquipmentBackupTables(m.Db, m.NameSource); log($"{Path.GetFileName(m.Db)} の *_bak 表から戻しました"); }
                        catch (Exception ex)
                        {
                            var kept = GameDataExtractor.KeepFailedEquipmentBackupTables(m.Db, path, log);
                            failures.Add($"{Path.GetFileName(m.Db)} ({ex.Message}。控えの表は {m.Db} の中の {kept})");
                        }
                    }
                }
                else
                {
                    // DB ごとに戻す。戻せた DB の控えはその場で消し、戻せなかった DB の控えは .failed-<日時> にして残す
                    foreach (var (target, backup) in m.Backups)
                    {
                        if (!File.Exists(backup)) { failures.Add($"{Path.GetFileName(target)} (控え {backup} が見つかりません)"); continue; }
                        try
                        {
                            DatabaseBackupService.RestoreImportBackup(backup, target);
                            SqliteConnection.ClearAllPools();
                            DatabaseBackupService.TryDelete(backup);
                            log($"{target} を {Path.GetFileName(backup)} から戻しました");
                        }
                        catch (Exception ex)
                        {
                            SqliteConnection.ClearAllPools();
                            var kept = KeepFailedBackup(backup, path, kind, log);
                            failures.Add($"{target} ({ex.Message}。控えは {kept})");
                        }
                    }
                }
            }
            catch (Exception ex) { failures.Add(ex.Message); }
            SqliteConnection.ClearAllPools();
            // 1 回だけ試す: 戻せても戻せなくても印は消す (戻せなかった控えは .failed-<日時> で残してある)
            var cleared = ClearAt(path, kind, log);
            if (failures.Count == 0)
            {
                log($"{label}の前の状態に戻しました");
                notices.Add($"前回、{label}の途中でアプリが終了したため、{label}の前の状態に戻しました。");
            }
            else
            {
                log($"{label}の前の状態に戻せなかったものがあります: {string.Join(", ", failures)}");
                notices.Add($"前回、{label}の途中でアプリが終了しましたが、次のものは前の状態に戻せませんでした (控えを残しています):\n{string.Join("\n", failures)}");
            }
            if (!cleared) notices.Add($"途中の印 {path} を消せませんでした。手で消してください。");
        }
        return notices;
    }
}
