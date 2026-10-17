using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// PC 間の同期: 共有フォルダを介して、個人データ (所持船・所持部品・在庫・手動の訳・用語集・記憶など) を複数の PC の間で合わせる。
//
// 書き出し (Export): 作業フォルダの 3 つの DB から個人データだけを抜いた小さな ZIP を、共有フォルダに "<PC 名>_sync.zip" として置き、
//   横に "<PC 名>_sync.json" (PC 名・書き出した時刻 (UTC)・アプリの版) を置く。どちらも一時名に書いてから File.Move で置き換える (json は zip の後)。
//   書き出す前に trade_cache.db と translations.db の quick_check が ok でなければ書き出さない (gamedata_cache.db は knowledge を読むだけなので quick_check しない)。
// 取り込み (Import): 共有フォルダの自分以外の *_sync.json を読み、書き出した時刻が「自分の最後の書き出し」と「その PC を最後に取り込んだ時刻」の
//   両方より新しいものだけを、古い順に取り込む。zip は自分の Temp に写してから開く。
//   取り込みの決まり (通常のインポート (DatabaseBackupService.ImportCore) とは別。相手の書き出しのほうが新しいことが分かっているので置き換えが基本):
//     - 主キーの無い個人の表 (my_ships, my_components, hangar_items, hangar_nameable_ships, my_gear) と連動の game_log_imports / my_component_purchases / ore_stock:
//       相手の行で置き換え (DELETE → INSERT。両方にある列だけ)
//     - 主キーのある個人の表 (hangar_pledges, hangar_ccus, hangar_ship_marks, ship_loadouts, commodity_trades, mining_log、hangar_meta の個人のキー、glossary、
//       translations の個人行): INSERT OR REPLACE (主キーが同じなら相手の行。translations は key が同じとき japanese / source / translator / modified_at を
//       相手の値にする。自分の非個人行 (ゲーム由来の訳) は消さない。glossary も english が同じ行は値が違うときだけ相手の値にする)
//     - knowledge: 同じ本文 (content) の行が無ければ新しい id で足す (通常のインポートと同じ)
//   自分の trade_cache.db と translations.db の quick_check が ok でなければ取り込まない (gamedata_cache.db は quick_check せず、knowledge の INSERT が失敗したらその相手を失敗にする)。trade_cache.db と translations.db は VACUUM INTO で控えを作り、失敗したら戻す
//   (gamedata_cache.db は knowledge の 1 トランザクションだけなので控えは作らない)。
//   取り込んだ後、翻訳の個人行か用語集が 1 行でも変わっていたときだけ、品目名・日本語名を今の訳に合わせる (GameDataExtractor.RefreshItemNamesFromTranslations)。
// 状態: 作業フォルダの sync_state.json (lastExportUtc と、相手の PC ごとの lastImportedUtc)。
// 書き出しと取り込みは同時に走らせない (Gate)
internal static class SyncService
{
    internal const string StateFileName = "sync_state.json";
    private const string ZipSuffix = "_sync.zip";
    private const string JsonSuffix = "_sync.json";

    // 書き出しと取り込みを同時に走らせないための門
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // 共有フォルダに置く json の中身
    internal sealed class Manifest
    {
        public string Machine { get; set; } = "";
        public string ExportedAtUtc { get; set; } = "";   // "o" 形式
        public string AppVersion { get; set; } = "";
    }

    // 作業フォルダの sync_state.json
    internal sealed class State
    {
        public string? LastExportUtc { get; set; }
        public Dictionary<string, string> LastImportedUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // 取り込みの候補 (共有フォルダの、自分以外の新しい書き出し)
    internal sealed record Candidate(string Machine, DateTime ExportedAtUtc, string JsonPath, string ZipPath);

    internal sealed record ExportResult(string ZipPath, long ZipBytes, DateTime ExportedAtUtc, Dictionary<string, int> RowCounts);

    internal sealed record ImportedPeer(string Machine, DateTime ExportedAtUtc, Dictionary<string, int> RowCounts, int TranslationsChanged, int GlossaryChanged);

    internal sealed class ImportResult
    {
        public List<ImportedPeer> Imported { get; } = new();
        public List<string> Errors { get; } = new();
        // 取り込みで変わった (足した + 書き換えた) 翻訳の個人行と用語集の行の合計 (どちらも 0 なら日本語名の付け直しをしない)
        public int TranslationsChanged { get; set; }
        public int GlossaryChanged { get; set; }
        public bool ItemNamesRefreshed { get; set; }
    }

    internal static string ZipFileName(string machine) => SafeName(machine) + ZipSuffix;
    internal static string JsonFileName(string machine) => SafeName(machine) + JsonSuffix;

    private static string SafeName(string machine)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = machine.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return s.Length == 0 ? "PC" : s;
    }

    private static string Fmt(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? ParseUtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return null;
        // "o" 形式は末尾 Z で Kind = Utc。種類の無い文字列は UTC と見る
        return d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
    }

    // ===== 状態 =====

    internal static State LoadState(string workDir, Action<string>? log = null)
    {
        var path = Path.Combine(workDir, StateFileName);
        try
        {
            if (File.Exists(path))
            {
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(path));
                if (state != null)
                {
                    state.LastImportedUtc = new Dictionary<string, string>(state.LastImportedUtc, StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            // 読めない記録は無いのと同じ扱い (全部を新しいものとして見る)
            log?.Invoke($"{StateFileName} を読めないので、無いものとして扱います: {ex.Message}");
        }
        return new State();
    }

    internal static void SaveState(string workDir, State state)
    {
        var path = Path.Combine(workDir, StateFileName);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    // ===== 書き出し =====

    public static Task<ExportResult> ExportAsync(string syncFolder, string workDir, string machine, string appVersion, Action<string>? log = null) =>
        Task.Run(() => Export(syncFolder, workDir, machine, appVersion, log));

    public static ExportResult Export(string syncFolder, string workDir, string machine, string appVersion, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(syncFolder)) throw new InvalidOperationException("共有フォルダが設定されていません");
        if (!Directory.Exists(syncFolder)) throw new DirectoryNotFoundException($"共有フォルダが見つかりません: {syncFolder}");
        Gate.Wait();
        try
        {
            var transPath = Path.Combine(workDir, "translations.db");
            var indexPath = Path.Combine(workDir, "gamedata_cache.db");
            var tradePath = Path.Combine(workDir, "trade_cache.db");
            // trade_cache.db と translations.db の quick_check (無い DB は書き出すものが無いので飛ばす)。
            // gamedata_cache.db (800MB) は quick_check せず、knowledge を読めなければそのまま失敗にする
            foreach (var p in new[] { transPath, tradePath })
                if (File.Exists(p)) RequireQuickCheckOk(p, "書き出しません");

            var exportedAt = DateTime.UtcNow;   // 中身を読み始める時刻 (取り込む側はこの時刻で新しさを見る)
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var tempDir = Path.Combine(Path.GetTempPath(), $"sc_sync_out_{Guid.NewGuid():N}");
            var tempZip = Path.Combine(Path.GetTempPath(), $"sc_sync_out_{Guid.NewGuid():N}.zip");
            try
            {
                Directory.CreateDirectory(tempDir);
                if (File.Exists(tradePath)) ExportTrade(tradePath, Path.Combine(tempDir, "trade_cache.db"), counts);
                var personal = string.Join(", ", App.PersonalTranslationSources.Select(s => $"'{s}'"));
                if (File.Exists(transPath))
                    ExportPersonalTables(transPath, Path.Combine(tempDir, "translations.db"), counts,
                        ("translations", $"COALESCE(source, '') IN ({personal})"), ("glossary", null));
                if (File.Exists(indexPath))
                    ExportPersonalTables(indexPath, Path.Combine(tempDir, "gamedata_cache.db"), counts, ("knowledge", null));
                SqliteConnection.ClearAllPools();
                if (Directory.GetFiles(tempDir).Length == 0) throw new InvalidOperationException("作業フォルダに書き出す DB がありません");

                ZipFile.CreateFromDirectory(tempDir, tempZip, CompressionLevel.Optimal, false);

                // 一時名に写してから置き換える (読んでいる途中の相手に欠けたファイルを見せないため)。json は zip の後
                var zipFinal = Path.Combine(syncFolder, ZipFileName(machine));
                var zipTmp = zipFinal + ".tmp";
                File.Copy(tempZip, zipTmp, overwrite: true);
                File.Move(zipTmp, zipFinal, overwrite: true);
                var jsonFinal = Path.Combine(syncFolder, JsonFileName(machine));
                var jsonTmp = jsonFinal + ".tmp";
                File.WriteAllText(jsonTmp, JsonSerializer.Serialize(new Manifest { Machine = machine, ExportedAtUtc = Fmt(exportedAt), AppVersion = appVersion }, JsonOptions));
                File.Move(jsonTmp, jsonFinal, overwrite: true);

                var state = LoadState(workDir, log);
                state.LastExportUtc = Fmt(exportedAt);
                SaveState(workDir, state);
                return new ExportResult(zipFinal, new FileInfo(zipFinal).Length, exportedAt, counts);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(tempDir, true); } catch { }
                DatabaseBackupService.TryDelete(tempZip);
            }
        }
        finally { Gate.Release(); }
    }

    // trade_cache.db: VACUUM INTO の写しから、ネット由来の表 (TradeNetTables) と hangar_meta のネット由来のキー (HangarMetaNetKeys) を消して VACUUM
    private static void ExportTrade(string srcPath, string destPath, Dictionary<string, int> counts)
    {
        using (var src = new SqliteConnection($"Data Source={srcPath};Pooling=False"))
        {
            src.Open();
            using var cmd = src.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = "VACUUM INTO @p";
            cmd.Parameters.AddWithValue("@p", destPath);
            cmd.ExecuteNonQuery();
        }
        using var conn = new SqliteConnection($"Data Source={destPath};Pooling=False");
        conn.Open();
        foreach (var t in DatabaseBackupService.TradeNetTables)
            if (DatabaseBackupService.TableExists(conn, t)) DatabaseBackupService.Exec(conn, null, $"DELETE FROM {DatabaseBackupService.Q(t)}");
        if (DatabaseBackupService.TableExists(conn, "hangar_meta"))
            DatabaseBackupService.Exec(conn, null, $"DELETE FROM hangar_meta WHERE key IN ({string.Join(", ", DatabaseBackupService.HangarMetaNetKeys.Select(k => $"'{k}'"))})");
        foreach (var t in DatabaseBackupService.TradeTables)
            if (!DatabaseBackupService.TradeNetTables.Contains(t) && DatabaseBackupService.TableExists(conn, t))
                counts[t] = (int)DatabaseBackupService.Scalar(conn, null, $"SELECT COUNT(*) FROM {DatabaseBackupService.Q(t)}");
        DatabaseBackupService.Exec(conn, null, "VACUUM");
    }

    // 空の DB (destPath) を作り、元の DB (srcPath) を ATTACH して、表ごとに元と同じ DDL で作って行を写す (where が null なら全行)
    private static void ExportPersonalTables(string srcPath, string destPath, Dictionary<string, int> counts, params (string Table, string? Where)[] specs)
    {
        using var conn = new SqliteConnection($"Data Source={destPath};Pooling=False");
        conn.Open();
        using (var attach = conn.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE @p AS src";
            attach.Parameters.AddWithValue("@p", srcPath);
            attach.ExecuteNonQuery();
        }
        foreach (var (table, where) in specs)
        {
            string? ddl;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM src.sqlite_master WHERE type = 'table' AND name = @t";
                cmd.Parameters.AddWithValue("@t", table);
                ddl = cmd.ExecuteScalar() as string;
            }
            if (string.IsNullOrEmpty(ddl)) continue;   // 元に表が無い
            DatabaseBackupService.Exec(conn, null, ddl);   // CREATE TABLE <名前> (...) は main に作られる
            var q = DatabaseBackupService.Q(table);
            counts[table] = DatabaseBackupService.Exec(conn, null, $"INSERT INTO main.{q} SELECT * FROM src.{q}" + (where == null ? "" : $" WHERE {where}"));
        }
        DatabaseBackupService.Exec(conn, null, "DETACH DATABASE src");
    }

    // ===== 取り込み =====

    // 共有フォルダの、自分以外の新しい書き出し (古い順)。読めない json・zip の無いものは log に残して飛ばす
    public static List<Candidate> ListCandidates(string syncFolder, string workDir, string machine, Action<string>? log = null)
    {
        var result = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(syncFolder) || !Directory.Exists(syncFolder)) return result;
        var state = LoadState(workDir, log);
        var lastExport = ParseUtc(state.LastExportUtc) ?? DateTime.MinValue;
        var ownJson = JsonFileName(machine);
        foreach (var jsonPath in Directory.GetFiles(syncFolder, "*" + JsonSuffix))
        {
            var fileName = Path.GetFileName(jsonPath);
            if (string.Equals(fileName, ownJson, StringComparison.OrdinalIgnoreCase)) continue;
            Manifest? m;
            try { m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { log?.Invoke($"{fileName} を読めないので飛ばします: {ex.Message}"); continue; }
            if (m == null || string.IsNullOrWhiteSpace(m.Machine)) { log?.Invoke($"{fileName} に PC 名が無いので飛ばします"); continue; }
            if (string.Equals(m.Machine, machine, StringComparison.OrdinalIgnoreCase)) continue;   // 自分の書き出し
            var exportedAt = ParseUtc(m.ExportedAtUtc);
            if (exportedAt == null) { log?.Invoke($"{fileName} の書き出した時刻を読めないので飛ばします"); continue; }
            if (exportedAt.Value <= lastExport) continue;
            if (state.LastImportedUtc.TryGetValue(m.Machine, out var lastStr) && ParseUtc(lastStr) is { } last && exportedAt.Value <= last) continue;
            var zipPath = Path.Combine(syncFolder, fileName[..^JsonSuffix.Length] + ZipSuffix);
            if (!File.Exists(zipPath)) { log?.Invoke($"{fileName} の横に {Path.GetFileName(zipPath)} が無いので飛ばします"); continue; }
            result.Add(new Candidate(m.Machine, exportedAt.Value, jsonPath, zipPath));
        }
        return result.OrderBy(c => c.ExportedAtUtc).ToList();
    }

    public static Task<ImportResult> ImportAsync(string syncFolder, string workDir, string machine, Action<string>? log = null,
        Action<string, string, Action<string>?>? refreshItemNames = null) =>
        Task.Run(() => Import(syncFolder, workDir, machine, log, refreshItemNames));

    // refreshItemNames: 翻訳の個人行が変わったときに呼ぶ (gamedata_cache.db のパス, 作業フォルダ, log)。null なら GameDataExtractor.RefreshItemNamesFromTranslations
    public static ImportResult Import(string syncFolder, string workDir, string machine, Action<string>? log = null,
        Action<string, string, Action<string>?>? refreshItemNames = null)
    {
        if (string.IsNullOrWhiteSpace(syncFolder)) throw new InvalidOperationException("共有フォルダが設定されていません");
        if (!Directory.Exists(syncFolder)) throw new DirectoryNotFoundException($"共有フォルダが見つかりません: {syncFolder}");
        var result = new ImportResult();
        Gate.Wait();
        try
        {
            var candidates = ListCandidates(syncFolder, workDir, machine, log);
            if (candidates.Count == 0) return result;

            var transPath = Path.Combine(workDir, "translations.db");
            var indexPath = Path.Combine(workDir, "gamedata_cache.db");
            var tradePath = Path.Combine(workDir, "trade_cache.db");
            // 取り込み先の DB の形を今のものに揃える (無ければ作る。通常のインポートと同じ)。gamedata_cache.db は無ければ knowledge を取り込まない
            using (new TranslationDatabase(transPath)) { }
            using (var conn = new SqliteConnection($"Data Source={tradePath};Pooling=False"))
            {
                conn.Open();
                DatabaseBackupService.EnsureTradeSchema(conn);
                TradeService.MigrateTerminalsSchema(conn);
                TradeService.MigrateMyShipsSchema(conn);
            }
            SqliteConnection.ClearAllPools();
            // gamedata_cache.db は quick_check せず、knowledge への INSERT が失敗したらその相手を失敗にする
            foreach (var p in new[] { transPath, tradePath })
                if (File.Exists(p)) RequireQuickCheckOk(p, "取り込みません");

            var state = LoadState(workDir, log);
            foreach (var cand in candidates)
            {
                var tempDir = Path.Combine(Path.GetTempPath(), $"sc_sync_in_{Guid.NewGuid():N}");
                try
                {
                    log?.Invoke($"{cand.Machine} の書き出し ({cand.ExportedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}) を取り込みます");
                    Directory.CreateDirectory(tempDir);
                    var localZip = Path.Combine(tempDir, "sync.zip");
                    File.Copy(cand.ZipPath, localZip, overwrite: true);   // 共有フォルダの zip は自分の Temp に写してから開く
                    var dbDir = Path.Combine(tempDir, "db");
                    ZipFile.ExtractToDirectory(localZip, dbDir);

                    // どの DB かは中の表で見分ける
                    string? srcTrans = null, srcIndex = null, srcTrade = null;
                    foreach (var p in Directory.GetFiles(dbDir, "*.db"))
                    {
                        using var conn = new SqliteConnection($"Data Source={p};Mode=ReadOnly;Pooling=False");
                        conn.Open();
                        if (srcTrans == null && (DatabaseBackupService.TableExists(conn, "translations") || DatabaseBackupService.TableExists(conn, "glossary"))) srcTrans = p;
                        if (srcIndex == null && DatabaseBackupService.TableExists(conn, "knowledge")) srcIndex = p;
                        if (srcTrade == null && DatabaseBackupService.TradeTables.Any(t => DatabaseBackupService.TableExists(conn, t))) srcTrade = p;
                    }
                    SqliteConnection.ClearAllPools();

                    // 控え (trade と translations)。失敗したら戻す
                    var backups = new Dictionary<string, string>();
                    if (srcTrade != null) backups[tradePath] = DatabaseBackupService.CreateImportBackup(tradePath, ".sync.bak");
                    if (srcTrans != null) backups[transPath] = DatabaseBackupService.CreateImportBackup(transPath, ".sync.bak");
                    var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                    var transChanged = 0;
                    var glossaryChanged = 0;
                    try
                    {
                        if (srcTrade != null) ImportTrade(tradePath, srcTrade, counts);
                        if (srcTrans != null) transChanged = ImportTranslations(transPath, srcTrans, counts, out glossaryChanged);
                        if (srcIndex != null && File.Exists(indexPath)) ImportKnowledge(indexPath, srcIndex, counts);
                        SqliteConnection.ClearAllPools();
                    }
                    catch
                    {
                        SqliteConnection.ClearAllPools();
                        foreach (var (target, backup) in backups)
                        {
                            try { DatabaseBackupService.RestoreImportBackup(backup, target); }
                            catch (Exception rex) { log?.Invoke($"{Path.GetFileName(target)} を取り込み前の状態に戻せませんでした: {rex.Message} (控えは {backup})"); }
                        }
                        SqliteConnection.ClearAllPools();
                        throw;
                    }
                    finally
                    {
                        foreach (var b in backups.Values) DatabaseBackupService.TryDelete(b);
                    }

                    state.LastImportedUtc[cand.Machine] = Fmt(cand.ExportedAtUtc);
                    SaveState(workDir, state);
                    result.Imported.Add(new ImportedPeer(cand.Machine, cand.ExportedAtUtc, counts, transChanged, glossaryChanged));
                    result.TranslationsChanged += transChanged;
                    result.GlossaryChanged += glossaryChanged;
                    log?.Invoke($"{cand.Machine} から取り込みました: {string.Join(", ", counts.Where(kv => kv.Value > 0).Select(kv => $"{kv.Key} {kv.Value}"))}"
                        + (transChanged > 0 ? $" (翻訳の個人行 {transChanged} 行が変わりました)" : "")
                        + (glossaryChanged > 0 ? $" (用語集 {glossaryChanged} 行が変わりました)" : ""));
                }
                catch (Exception ex)
                {
                    // 古い順に取り込むので、失敗したらそこで止める (記録は進めないので次回もう一度)
                    log?.Invoke($"{cand.Machine} の書き出しの取り込みに失敗: {ex.Message}");
                    result.Errors.Add($"{cand.Machine}: {ex.Message}");
                    break;
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }

            // 翻訳の個人行か用語集が 1 行でも変わっていたときだけ、品目名・日本語名を今の訳に合わせる (ImportDb_Click と同じ扱い。合わせられなくても取り込みは失敗にしない)
            if ((result.TranslationsChanged > 0 || result.GlossaryChanged > 0) && File.Exists(indexPath))
            {
                try
                {
                    (refreshItemNames ?? DefaultRefreshItemNames)(indexPath, workDir, log);
                    result.ItemNamesRefreshed = true;
                }
                catch (Exception ex) { log?.Invoke($"品目名を今の訳に合わせられませんでした: {ex.Message}"); }
                SqliteConnection.ClearAllPools();
            }
            return result;
        }
        finally { Gate.Release(); }
    }

    private static void DefaultRefreshItemNames(string indexPath, string workDir, Action<string>? log) =>
        GameDataExtractor.RefreshItemNamesFromTranslations(indexPath, workDir, log: log);

    // trade_cache.db: ネット由来の表は触らない。主キーの無い個人の表と連動の印・在庫は相手で置き換え、主キーのある個人の表は INSERT OR REPLACE
    private static void ImportTrade(string aPath, string bPath, Dictionary<string, int> counts)
    {
        using var conn = DatabaseBackupService.OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        var replaceTables = DatabaseBackupService.TradePersonalUnkeyedTables
            .Concat([DatabaseBackupService.GameLogImportsTable, DatabaseBackupService.ComponentPurchasesTable, DatabaseBackupService.OreStockTable]).ToArray();
        foreach (var table in DatabaseBackupService.TradeTables)
        {
            if (DatabaseBackupService.TradeNetTables.Contains(table)) continue;
            if (!DatabaseBackupService.SourceTableExists(conn, tx, table)) continue;
            var q = DatabaseBackupService.Q(table);
            if (replaceTables.Contains(table))
            {
                // a に表が無ければ作る (印は EnsureTradeSchema の形、在庫は OreService の DDL、ほかは b の形)
                if (table == DatabaseBackupService.OreStockTable) DatabaseBackupService.Exec(conn, tx, OreService.TradeTablesDdl);
                else if (table == DatabaseBackupService.GameLogImportsTable || table == DatabaseBackupService.ComponentPurchasesTable) DatabaseBackupService.EnsureTradeSchemaTable(conn, tx);
                else DatabaseBackupService.EnsureMainTable(conn, tx, table);
                var cols = string.Join(", ", DatabaseBackupService.CommonColumns(conn, tx, table).Select(DatabaseBackupService.Q));
                DatabaseBackupService.Exec(conn, tx, $"DELETE FROM main.{q}");
                counts[table] = DatabaseBackupService.Exec(conn, tx, $"INSERT INTO main.{q} ({cols}) SELECT {cols} FROM src.{q}");
            }
            else if (table == "hangar_meta")
            {
                DatabaseBackupService.EnsureMainTable(conn, tx, table);
                var netKeys = string.Join(", ", DatabaseBackupService.HangarMetaNetKeys.Select(k => $"'{k}'"));
                counts[table] = DatabaseBackupService.Exec(conn, tx, $"INSERT OR REPLACE INTO main.hangar_meta (key, value) SELECT key, value FROM src.hangar_meta WHERE key NOT IN ({netKeys})");
            }
            else if (DatabaseBackupService.TradePersonalKeyedTables.Contains(table))
            {
                DatabaseBackupService.EnsureMainTable(conn, tx, table);
                var cols = string.Join(", ", DatabaseBackupService.CommonColumns(conn, tx, table).Select(DatabaseBackupService.Q));
                counts[table] = DatabaseBackupService.Exec(conn, tx, $"INSERT OR REPLACE INTO main.{q} ({cols}) SELECT {cols} FROM src.{q}");
            }
        }
        DatabaseBackupService.NormalizeSequences(conn, tx);
        tx.Commit();
    }

    // translations.db: translations の個人行は key が同じとき japanese / source / translator / modified_at を相手の値にし、無ければ足す
    // (a の非個人行は消さない。値がすべて同じ行は変えない)。glossary も同じく english が同じ行は値が違うときだけ相手の値にし、無ければ足す。
    // 返り値: 変わった (足した + 書き換えた) translations の行数。glossaryChanged: 同じく glossary の行数
    private static int ImportTranslations(string aPath, string bPath, Dictionary<string, int> counts, out int glossaryChanged)
    {
        glossaryChanged = 0;
        using var conn = DatabaseBackupService.OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        var changed = 0;
        var personal = string.Join(", ", App.PersonalTranslationSources.Select(s => $"'{s}'"));
        if (DatabaseBackupService.SourceTableExists(conn, tx, "translations"))
        {
            var cols = DatabaseBackupService.CommonColumns(conn, tx, "translations");
            if (!cols.Contains("key", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("相手の translations に key の列がありません");
            var updateCols = new[] { "japanese", "source", "translator", "modified_at" }.Where(c => cols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var colList = string.Join(", ", cols.Select(DatabaseBackupService.Q));
            // 値が違う行だけ書き換える (DO UPDATE の WHERE)。ExecuteNonQuery の件数 = 足した行 + 書き換えた行
            var set = string.Join(", ", updateCols.Select(c => $"{DatabaseBackupService.Q(c)} = excluded.{DatabaseBackupService.Q(c)}"));
            var differ = string.Join(" OR ", updateCols.Select(c => $"translations.{DatabaseBackupService.Q(c)} IS NOT excluded.{DatabaseBackupService.Q(c)}"));
            var onConflict = updateCols.Count > 0 ? $"DO UPDATE SET {set} WHERE {differ}" : "DO NOTHING";
            changed = DatabaseBackupService.Exec(conn, tx,
                $"INSERT INTO main.translations ({colList}) SELECT {colList} FROM src.translations WHERE COALESCE(source, '') IN ({personal}) ON CONFLICT(key) {onConflict}");
            counts["translations"] = changed;
        }
        if (DatabaseBackupService.SourceTableExists(conn, tx, "glossary"))
        {
            var cols = DatabaseBackupService.CommonColumns(conn, tx, "glossary");
            if (!cols.Contains("english", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("相手の glossary に english の列がありません");
            var updateCols = cols.Where(c => !c.Equals("english", StringComparison.OrdinalIgnoreCase)).ToList();
            var colList = string.Join(", ", cols.Select(DatabaseBackupService.Q));
            // translations と同じく、値が違う行だけ書き換えて、足した + 書き換えた行数を数える (付け直しの要否に使う)
            var set = string.Join(", ", updateCols.Select(c => $"{DatabaseBackupService.Q(c)} = excluded.{DatabaseBackupService.Q(c)}"));
            var differ = string.Join(" OR ", updateCols.Select(c => $"glossary.{DatabaseBackupService.Q(c)} IS NOT excluded.{DatabaseBackupService.Q(c)}"));
            var onConflict = updateCols.Count > 0 ? $"DO UPDATE SET {set} WHERE {differ}" : "DO NOTHING";
            glossaryChanged = DatabaseBackupService.Exec(conn, tx,
                $"INSERT INTO main.glossary ({colList}) SELECT {colList} FROM src.glossary WHERE true ON CONFLICT(english) {onConflict}");
            counts["glossary"] = glossaryChanged;
        }
        tx.Commit();
        return changed;
    }

    // gamedata_cache.db: knowledge は同じ本文 (content) の行が無ければ新しい id で足す (DatabaseBackupService.ImportIndexDb と同じ)
    private static void ImportKnowledge(string aPath, string bPath, Dictionary<string, int> counts)
    {
        using var conn = DatabaseBackupService.OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        if (DatabaseBackupService.SourceTableExists(conn, tx, "knowledge")
            && DatabaseBackupService.Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = 'knowledge'") > 0)
        {
            var cols = DatabaseBackupService.CommonColumns(conn, tx, "knowledge").Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cols.Contains("content", StringComparer.OrdinalIgnoreCase))
            {
                var colList = string.Join(", ", cols.Select(DatabaseBackupService.Q));
                counts["knowledge"] = DatabaseBackupService.Exec(conn, tx, $"INSERT INTO main.knowledge ({colList}) SELECT {colList} FROM src.knowledge s "
                    + "WHERE NOT EXISTS (SELECT 1 FROM main.knowledge k WHERE k.content = s.content)");
            }
        }
        DatabaseBackupService.NormalizeSequences(conn, tx);
        tx.Commit();
    }

    // ===== 共通 =====

    // quick_check が ok でなければ例外 (App.CheckWorkDbHealth と同じ見方。結果の頭だけを文に入れる)
    private static void RequireQuickCheckOk(string path, string action)
    {
        string? qc;
        using (var db = new SqliteConnection($"Data Source={path};Mode=ReadWrite;Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandTimeout = 0;
            cmd.CommandText = "PRAGMA quick_check";
            qc = cmd.ExecuteScalar()?.ToString();
        }
        if (qc == "ok") return;
        var head = (qc ?? "").ReplaceLineEndings(" / ");
        if (head.Length > 200) head = head[..200] + "…";
        throw new InvalidOperationException($"{Path.GetFileName(path)} の quick_check が ok でないため{action} ({head})");
    }
}
