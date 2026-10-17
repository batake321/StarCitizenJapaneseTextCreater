using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
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
//     - 主キーのある個人の表 (hangar_pledges, hangar_ccus, hangar_ship_marks, ship_loadouts, commodity_trades, mining_log, game_log_entries、hangar_meta の個人のキー、glossary、
//       translations の個人行): INSERT OR REPLACE (主キーが同じなら相手の行。translations は key が同じとき japanese / source / translator / modified_at を
//       相手の値にする。自分の非個人行 (ゲーム由来の訳) は消さない。glossary も english が同じ行は値が違うときだけ相手の値にする。
//       ただし translations (modified_at) と glossary (created_at) は、自分の行のほうが新しければ相手の行で上書きしない (NewerThanMineSql))
//     - knowledge: 同じ本文 (content) の行が無ければ新しい id で足す (通常のインポートと同じ)
//   自分の trade_cache.db と translations.db の quick_check が ok でなければ取り込まない (gamedata_cache.db は quick_check せず、knowledge の INSERT が失敗したらその相手を失敗にする)。trade_cache.db と translations.db は VACUUM INTO で控えを作り、失敗したら戻す
//   (gamedata_cache.db は knowledge の 1 トランザクションだけなので控えは作らない)。
//   取り込んだ後、翻訳の個人行か用語集が 1 行でも変わっていたときだけ、品目名・日本語名を今の訳に合わせる (GameDataExtractor.RefreshItemNamesFromTranslations)。
//   両方で変更があるとき (相手の json の imported[自分] が自分の lastExportUtc より古い = 相手がこちらの最新の書き出しを取り込まずに書き出した。
//   自分が一度も書き出していなければ見ない) は自動では取り込まない (Candidate.BothChanged)。手動 (force) のときだけ取り込む
// 状態: 作業フォルダの sync_state.json (lastExportUtc、lastExportHash (最後に書き出した個人データの内容のハッシュ)、相手の PC ごとの lastImportedUtc)。
// 書き出しは個人データの内容のハッシュ (ContentHash) が前回の書き出しと同じなら行わない (ExportResult.Unchanged。force なら常に書き出す)
// 書き出しと取り込みは同時に走らせない (Gate。［ゲームログ］の行の保存と通常のインポートも同じ門を取る)
// 墓石 (sync_tombstones): 片方で消したもの (用語集の語・取り消した印・装備の構成の解除・記憶の削除など) が、同期でもう片方から戻ってこないようにする印。
//   3 つの DB のそれぞれに表を持ち (TombstonesDdl)、主キーのある個人の表 (Keyed) と knowledge の行を消す経路で (table_name, row_key, 消した時刻 (UTC), PC 名) を書く
//   (WriteTombstone。row_key は主キーの値を U+001F でつないだもの (TombstoneKey)。glossary は english の小文字、knowledge は content)。
//   書き出しには 3 つの DB の sync_tombstones を入れる (個人データ扱い。90 日より古い墓石は書き出す前に消す (PurgeOldTombstones))。
//   取り込み (ApplyTombstones): 相手の墓石のうち deleted_at_utc がその相手の lastImportedUtc より新しいもの (初めてなら全部) を自分の対応する行に当てて消す
//   (行に時刻の列があり (glossary.created_at / game_log_imports.added_at / knowledge.created_at / translations.modified_at)、それが消した時刻より新しければ消さない)。
//   相手の行を入れるときは、自分に同じ row_key の墓石があって deleted_at_utc が相手の書き出した時刻より新しければ入れない (自分の削除を守る。TombstoneGuardSql)。
//   machine が自分の墓石は当てない。行を書き直す経路 (装備の保存・印・CCU・用語・記憶・印の登録・個人訳の保存) ではその鍵の墓石を消す (ClearTombstone)。
//   translations の墓石 (「訳を消す」「原文に戻す」で個人行を非個人にしたもの) は行を消さず、自分の個人行を payload の値に UPDATE する。
//   相手の墓石は自分の sync_tombstones にも写す (同じ鍵なら新しい方)。通常のインポート (バックアップの復元) では墓石を当てない (sync_tombstones 自体は a 優先の和集合で取り込む)
internal static class SyncService
{
    internal const string StateFileName = "sync_state.json";
    private const string ZipSuffix = "_sync.zip";
    private const string JsonSuffix = "_sync.json";

    // 書き出し・取り込み・［ゲームログ］の行の保存 (MainWindow.SaveGameLogEntriesAsync)・通常のインポート (ImportDb_Click) を同時に走らせないための門
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ===== 墓石 (sync_tombstones) =====

    internal const string TombstonesTable = "sync_tombstones";

    // 3 つの DB に同じ形で持つ (HangarService.InitDb / OreService.Open / DatabaseBackupService.EnsureTradeSchema・EnsureIndexSchema /
    // TranslationDatabase.InitSchema / GameDataExtractor.EnsureDb / GameDataQueryService のコンストラクタから使う)
    // payload: translations の墓石 (個人行を「訳を消す」「原文に戻す」で非個人行にしたもの) だけに、操作後の {japanese, source, translator} を JSON で入れる
    // (取り込む側は行を消さず、この値に UPDATE する)。ほかの表の墓石は NULL
    internal const string TombstonesDdl = """

        CREATE TABLE IF NOT EXISTS sync_tombstones (
            table_name TEXT,
            row_key TEXT,
            deleted_at_utc TEXT,
            machine TEXT,
            payload TEXT,
            PRIMARY KEY (table_name, row_key)
        );
        """;

    // 表を作り (無ければ)、payload 列の無い既存の表には列を足す (墓石を読み書きする前に通す)
    internal static void EnsureTombstonesTable(SqliteConnection conn, SqliteTransaction? tx)
    {
        using var cmd = conn.CreateCommand();   // CreateCommand は接続の今のトランザクションを付ける (tx が null でも接続のトランザクションの中なら合う)
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = TombstonesDdl;
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA table_info(sync_tombstones)";
        var hasPayload = false;
        using (var r = cmd.ExecuteReader())
            while (r.Read()) if (r.GetString(1).Equals("payload", StringComparison.OrdinalIgnoreCase)) hasPayload = true;
        if (hasPayload) return;
        cmd.CommandText = "ALTER TABLE sync_tombstones ADD COLUMN payload TEXT";
        cmd.ExecuteNonQuery();
    }

    // 墓石を書いてから消すまでの保持 (これより古い墓石は書き出す前に消す)
    private static readonly TimeSpan TombstoneRetention = TimeSpan.FromDays(90);

    // row_key: 主キーの値 (複数なら主キーの列の順) を U+001F でつないだもの
    private const char TombstoneKeySeparator = '\u001f';
    internal static string TombstoneKey(params string[] parts) => string.Join(TombstoneKeySeparator, parts);
    // glossary の row_key (english の小文字。english は COLLATE NOCASE の主キー)
    internal static string GlossaryTombstoneKey(string english) => english.ToLowerInvariant();

    // 墓石を 1 つ書く (同じ鍵があれば今の時刻で書き直す)。表が無ければ作る。消した時刻は UTC の "o" 形式 (書き出しの時刻と同じ形)、machine はこの PC の名前
    internal static void WriteTombstone(SqliteConnection conn, SqliteTransaction? tx, string table, string rowKey) =>
        WriteTombstones(conn, tx, table, [rowKey]);

    internal static void WriteTombstones(SqliteConnection conn, SqliteTransaction? tx, string table, IEnumerable<string> rowKeys) =>
        WriteTombstones(conn, tx, table, rowKeys.Select(k => (k, (string?)null)));

    // payload つき (translations の墓石用)
    internal static void WriteTombstones(SqliteConnection conn, SqliteTransaction? tx, string table, IEnumerable<(string Key, string? Payload)> rows)
    {
        EnsureTombstonesTable(conn, tx);
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR REPLACE INTO sync_tombstones (table_name, row_key, deleted_at_utc, machine, payload) VALUES (@t, @k, @d, @m, @p)";
        cmd.Parameters.AddWithValue("@t", table);
        var pKey = cmd.Parameters.Add("@k", SqliteType.Text);
        cmd.Parameters.AddWithValue("@d", Fmt(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("@m", Environment.MachineName);
        var pPayload = cmd.Parameters.Add("@p", SqliteType.Text);
        foreach (var (key, payload) in rows)
        {
            pKey.Value = key;
            pPayload.Value = (object?)payload ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }

    // 行を書き直したとき (付け直し・再登録) に、その鍵の墓石を消す (消さないと、相手に写った自分の墓石が戻ってきて書き直した行が消える)。表が無ければ何もしない
    internal static void ClearTombstone(SqliteConnection conn, SqliteTransaction? tx, string table, string rowKey) =>
        ClearTombstones(conn, tx, table, [rowKey]);

    internal static void ClearTombstones(SqliteConnection conn, SqliteTransaction? tx, string table, IEnumerable<string> rowKeys)
    {
        using var cmd = conn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_tombstones'";
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return;
        cmd.CommandText = "DELETE FROM sync_tombstones WHERE table_name = @t AND row_key = @k";
        cmd.Parameters.AddWithValue("@t", table);
        var pKey = cmd.Parameters.Add("@k", SqliteType.Text);
        foreach (var key in rowKeys)
        {
            pKey.Value = key;
            cmd.ExecuteNonQuery();
        }
    }

    // translations の墓石の payload (操作後の値)。japanese は null あり
    internal static string TranslationTombstonePayload(string? japanese, string source, string translator) =>
        JsonSerializer.Serialize(new TranslationPayload { Japanese = japanese, Source = source, Translator = translator });

    internal sealed class TranslationPayload
    {
        public string? Japanese { get; set; }
        public string Source { get; set; } = "";
        public string Translator { get; set; } = "";
    }

    // 90 日より古い墓石を消す (書き出す前に、作業フォルダの DB で行う)。表が無い・古い墓石が無ければ DB に書かない
    // (gamedata_cache.db はインデックス構築などが書いている最中のことがあるので、要らない書き込みで待たされないように)。消した数を返す
    internal static int PurgeOldTombstones(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        if (!DatabaseBackupService.TableExists(conn, TombstonesTable)) return 0;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sync_tombstones WHERE deleted_at_utc < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", Fmt(DateTime.UtcNow - TombstoneRetention));
        if (Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) == 0) return 0;
        cmd.CommandText = "DELETE FROM sync_tombstones WHERE deleted_at_utc < @cutoff";
        return cmd.ExecuteNonQuery();
    }

    // 取り込みで墓石を当てる表 (同期で INSERT OR REPLACE する主キーのある個人の表と knowledge、印 (game_log_imports))。
    // KeyCols: row_key にする列 (主キーの列の順)、Lower: row_key を小文字にする (glossary)、TimeCol: 行の時刻の列 (これが消した時刻より新しければ消さない。無ければ null)
    private sealed record TombstoneSpec(string Table, string[] KeyCols, string? TimeCol, bool Lower = false);

    private static readonly TombstoneSpec[] TradeTombstoneSpecs =
    [
        new("hangar_pledges", ["id"], null),
        new("hangar_ccus", ["pledge_id"], null),
        new("hangar_ship_marks", ["pledge_id", "ship_name"], null),
        new("ship_loadouts", ["ship_key", "port_name"], null),
        new("commodity_trades", ["log_time", "player_id", "shop_id", "kiosk_id", "kind", "resource_uuid", "quantity_raw"], null),
        new("mining_log", ["log_time", "mineral_class", "entity_id"], null),
        new("game_log_entries", ["time_utc", "kind", "content"], null),
        new("hangar_meta", ["key"], null),
        new(DatabaseBackupService.GameLogImportsTable, ["kind", "log_key"], "added_at"),
    ];
    private static readonly TombstoneSpec[] TranslationTombstoneSpecs =
    [
        new("translations", ["key"], "modified_at"),
        new("glossary", ["english"], "created_at", Lower: true),
    ];
    private static readonly TombstoneSpec[] KnowledgeTombstoneSpecs =
    [
        new("knowledge", ["content"], "created_at"),
    ];

    // 表の行から row_key を作る SQL 式 (alias は行の表の別名。TombstoneKey と同じく U+001F (char(31)) でつなぐ。NULL は '' と見る)
    private static string TombstoneKeySql(TombstoneSpec spec, string alias)
    {
        var expr = string.Join(" || char(31) || ", spec.KeyCols.Select(c => $"COALESCE({alias}.{DatabaseBackupService.Q(c)}, '')"));
        return spec.Lower ? $"lower({expr})" : expr;
    }

    // 相手の行を入れる INSERT ... SELECT の WHERE に足す条件: 自分に同じ row_key の墓石があり、消した時刻が相手の書き出した時刻 (@exportedAt) より新しければ入れない
    private static string TombstoneGuardSql(string table, string alias)
    {
        var spec = TradeTombstoneSpecs.Concat(TranslationTombstoneSpecs).Concat(KnowledgeTombstoneSpecs).First(s => s.Table == table);
        return $"NOT EXISTS (SELECT 1 FROM main.sync_tombstones ts WHERE ts.table_name = '{table}' AND ts.row_key = {TombstoneKeySql(spec, alias)} AND ts.deleted_at_utc > @exportedAt)";
    }

    // 行の時刻の列の値を UTC にする。knowledge.created_at は UTC の "o" 形式、glossary.created_at / game_log_imports.added_at / translations.modified_at は
    // datetime('now', 'localtime') のローカル時刻 (種類の無い文字列はローカル時刻と見る。ParseUtc (json の時刻。種類が無ければ UTC) とは逆)。読めなければ null
    private static DateTime? ParseRowTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return null;
        return d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Local).ToUniversalTime() : d.ToUniversalTime();
    }

    // 相手 (src) の墓石を自分 (main) に当てる。since (その相手を最後に取り込んだ書き出しの時刻。初めてなら null) より新しい墓石だけを見て、
    // 対応する表の行を消す (TimeCol の値が消した時刻より新しい行は消さない)。machine が自分 (machine) の墓石は当てない
    // (自分で消したものは消したときに済んでいて、今その行があるなら消した後に書き直したもの。相手に写った自分の墓石が戻ってきても行を消さないように)。
    // translations の墓石は行を消さず、自分の行が個人行 (PersonalTranslationSources) で modified_at が消した時刻より新しくなければ payload の値に UPDATE する
    // (payload が無い translations の墓石は何もしない)。
    // 相手の墓石は全部を自分の sync_tombstones に写す (同じ鍵なら新しい方)。
    // counts に "墓石で消した行" (表ごと) と "sync_tombstones" (写した数) を入れる。相手に表が無ければ何もしない
    private static void ApplyTombstones(SqliteConnection conn, SqliteTransaction tx, TombstoneSpec[] specs, DateTime? since, string machine, Dictionary<string, int> counts)
    {
        if (!DatabaseBackupService.SourceTableExists(conn, tx, TombstonesTable)) return;
        EnsureTombstonesTable(conn, tx);   // main に無ければ作る (payload 列も)
        var srcCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "PRAGMA src.table_info(sync_tombstones)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) srcCols.Add(r.GetString(1));
        }
        var srcHasPayload = srcCols.Contains("payload");

        var tombstones = new List<(string Table, string Key, DateTime DeletedAt, string? Payload)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT table_name, row_key, deleted_at_utc, machine, {(srcHasPayload ? "payload" : "NULL")} FROM src.sync_tombstones";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(0) || r.IsDBNull(1)) continue;
                var deletedAt = ParseUtc(r.IsDBNull(2) ? null : r.GetString(2));
                if (deletedAt == null) continue;
                if (since != null && deletedAt.Value <= since.Value) continue;
                if (!r.IsDBNull(3) && string.Equals(r.GetString(3), machine, StringComparison.OrdinalIgnoreCase)) continue;   // 自分の墓石
                tombstones.Add((r.GetString(0), r.GetString(1), deletedAt.Value, r.IsDBNull(4) ? null : r.GetString(4)));
            }
        }
        var personal = string.Join(", ", App.PersonalTranslationSources.Select(s => $"'{s}'"));
        foreach (var spec in specs)
        {
            var mine = tombstones.Where(t => t.Table == spec.Table).ToList();
            if (mine.Count == 0) continue;
            if (DatabaseBackupService.Scalar(conn, tx, $"SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = '{spec.Table}'") == 0) continue;
            var q = DatabaseBackupService.Q(spec.Table);
            var keySql = TombstoneKeySql(spec, q);
            var deleted = 0;
            foreach (var (_, key, deletedAt, payload) in mine)
            {
                if (spec.TimeCol != null)
                {
                    using var read = conn.CreateCommand();
                    read.Transaction = tx;
                    read.CommandText = $"SELECT {DatabaseBackupService.Q(spec.TimeCol)} FROM main.{q} WHERE {keySql} = @k";
                    read.Parameters.AddWithValue("@k", key);
                    var rowTime = ParseRowTime(read.ExecuteScalar()?.ToString());
                    if (rowTime != null && rowTime.Value > deletedAt) continue;   // 消した後に足し直した行は残す
                }
                if (spec.Table == "translations")
                {
                    // 行は消さず、個人行だけを操作後の値 (payload) にする
                    if (payload == null) continue;
                    TranslationPayload? p;
                    try { p = JsonSerializer.Deserialize<TranslationPayload>(payload); } catch { continue; }
                    if (p == null) continue;
                    using var up = conn.CreateCommand();
                    up.Transaction = tx;
                    up.CommandText = $"UPDATE main.translations SET japanese = @ja, source = @src, translator = @tr, modified_at = datetime('now', 'localtime') WHERE key = @k AND COALESCE(source, '') IN ({personal})";
                    up.Parameters.AddWithValue("@ja", (object?)p.Japanese ?? DBNull.Value);
                    up.Parameters.AddWithValue("@src", p.Source);
                    up.Parameters.AddWithValue("@tr", p.Translator);
                    up.Parameters.AddWithValue("@k", key);
                    deleted += up.ExecuteNonQuery();
                    continue;
                }
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM main.{q} WHERE {keySql} = @k";
                del.Parameters.AddWithValue("@k", key);
                deleted += del.ExecuteNonQuery();
            }
            if (deleted > 0) counts[spec.Table == "translations" ? "translations (墓石で非個人に)" : $"{spec.Table} (墓石で削除)"] = deleted;
        }
        var payloadSel = srcHasPayload ? "payload" : "NULL";
        counts[TombstonesTable] = DatabaseBackupService.Exec(conn, tx,
            $"INSERT INTO main.sync_tombstones (table_name, row_key, deleted_at_utc, machine, payload) SELECT table_name, row_key, deleted_at_utc, machine, {payloadSel} FROM src.sync_tombstones WHERE true "
            + "ON CONFLICT(table_name, row_key) DO UPDATE SET deleted_at_utc = excluded.deleted_at_utc, machine = excluded.machine, payload = excluded.payload WHERE excluded.deleted_at_utc > sync_tombstones.deleted_at_utc");
    }

    // @exportedAt (相手の書き出した時刻) を持つ SQL を実行する (TombstoneGuardSql を含む INSERT 用)
    private static int ExecWithExportedAt(SqliteConnection conn, SqliteTransaction tx, string sql, DateTime exportedAtUtc)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandTimeout = 0;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@exportedAt", Fmt(exportedAtUtc));
        return cmd.ExecuteNonQuery();
    }

    // 共有フォルダに置く json の中身
    internal sealed class Manifest
    {
        public string Machine { get; set; } = "";
        public string ExportedAtUtc { get; set; } = "";   // "o" 形式
        public string AppVersion { get; set; } = "";
        // 相手の PC 名 → その相手の書き出しのうち取り込み済みの最新の exportedAtUtc ("o" 形式)。古い版の書き出しには無い (null)
        public Dictionary<string, string>? Imported { get; set; }
    }

    // 作業フォルダの sync_state.json
    internal sealed class State
    {
        public string? LastExportUtc { get; set; }
        public string? LastExportHash { get; set; }
        public Dictionary<string, string> LastImportedUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // 取り込みの候補 (共有フォルダの、自分以外の新しい書き出し)。BothChanged: 相手がこちらの最新の書き出しを取り込まずに書き出したもの (自動では取り込まない)
    internal sealed record Candidate(string Machine, DateTime ExportedAtUtc, string JsonPath, string ZipPath, bool BothChanged);

    // Unchanged: 内容が前回の書き出しと同じだったので書き出さなかった (ZipPath は前回のパス、ZipBytes は 0)
    internal sealed record ExportResult(string ZipPath, long ZipBytes, DateTime ExportedAtUtc, Dictionary<string, int> RowCounts, bool Unchanged = false);

    internal sealed record ImportedPeer(string Machine, DateTime ExportedAtUtc, Dictionary<string, int> RowCounts, int TranslationsChanged, int GlossaryChanged);

    internal sealed class ImportResult
    {
        public List<ImportedPeer> Imported { get; } = new();
        public List<string> Errors { get; } = new();
        // 両方で変更があるので自動では取り込まなかった相手 (force でないとき)
        public List<string> SkippedBothChanged { get; } = new();
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

    // force: 内容が前回の書き出しと同じでも書き出す (手動のボタン)
    public static Task<ExportResult> ExportAsync(string syncFolder, string workDir, string machine, string appVersion, Action<string>? log = null, bool force = false) =>
        Task.Run(() => Export(syncFolder, workDir, machine, appVersion, log, force));

    public static ExportResult Export(string syncFolder, string workDir, string machine, string appVersion, Action<string>? log = null, bool force = false)
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

            // 90 日より古い墓石は書き出す前に消す (作業フォルダの 3 つの DB で)
            foreach (var p in new[] { tradePath, transPath, indexPath })
                if (File.Exists(p)) PurgeOldTombstones(p);
            SqliteConnection.ClearAllPools();

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
                        ("translations", $"COALESCE(source, '') IN ({personal})"), ("glossary", null), (TombstonesTable, null));
                if (File.Exists(indexPath))
                    ExportPersonalTables(indexPath, Path.Combine(tempDir, "gamedata_cache.db"), counts, ("knowledge", null), (TombstonesTable, null));
                SqliteConnection.ClearAllPools();
                if (Directory.GetFiles(tempDir).Length == 0) throw new InvalidOperationException("作業フォルダに書き出す DB がありません");

                // 個人データの内容のハッシュ。前回の書き出しと同じなら書き出さない (取り込むたびに書き出して 2 台で同じ内容が往復しないように)
                var hash = ContentHash(tempDir);
                SqliteConnection.ClearAllPools();
                var state = LoadState(workDir, log);
                var zipFinal = Path.Combine(syncFolder, ZipFileName(machine));
                if (!force && state.LastExportHash != null && string.Equals(state.LastExportHash, hash, StringComparison.Ordinal))
                    return new ExportResult(zipFinal, 0, ParseUtc(state.LastExportUtc) ?? exportedAt, counts, Unchanged: true);

                ZipFile.CreateFromDirectory(tempDir, tempZip, CompressionLevel.Optimal, false);

                // 一時名に写してから置き換える (読んでいる途中の相手に欠けたファイルを見せないため)。json は zip の後
                var zipTmp = zipFinal + ".tmp";
                File.Copy(tempZip, zipTmp, overwrite: true);
                File.Move(zipTmp, zipFinal, overwrite: true);
                var jsonFinal = Path.Combine(syncFolder, JsonFileName(machine));
                var jsonTmp = jsonFinal + ".tmp";
                var manifest = new Manifest
                {
                    Machine = machine, ExportedAtUtc = Fmt(exportedAt), AppVersion = appVersion,
                    Imported = new Dictionary<string, string>(state.LastImportedUtc, StringComparer.OrdinalIgnoreCase),
                };
                File.WriteAllText(jsonTmp, JsonSerializer.Serialize(manifest, JsonOptions));
                File.Move(jsonTmp, jsonFinal, overwrite: true);

                state.LastExportUtc = Fmt(exportedAt);
                state.LastExportHash = hash;
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
    // (墓石 (sync_tombstones) は写しにそのまま残る = 書き出しに入る)
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
        foreach (var t in DatabaseBackupService.TradeTables.Append(TombstonesTable))
            if (!DatabaseBackupService.TradeNetTables.Contains(t) && DatabaseBackupService.TableExists(conn, t))
                counts[t] = (int)DatabaseBackupService.Scalar(conn, null, $"SELECT COUNT(*) FROM {DatabaseBackupService.Q(t)}");
        DatabaseBackupService.Exec(conn, null, "VACUUM");
    }

    // 書き出す個人データ (tempDir の DB = 書き出す表だけになった写し) の内容のハッシュ (SHA-256 の 16 進)。
    // DB をファイル名順、表を名前順に、各表の全行を全列の値の順 (主キーの列を先に) に並べ、列の値を種類つきの文字に正規化して連結したものをハッシュにする。
    // rowid は使わない (取り込みの DELETE → INSERT で rowid が変わっても内容が同じなら同じハッシュになるように)
    internal static string ContentHash(string tempDir)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Feed(string s) => sha.AppendData(System.Text.Encoding.UTF8.GetBytes(s));
        foreach (var dbPath in Directory.GetFiles(tempDir, "*.db").OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            Feed("\u001d" + Path.GetFileName(dbPath) + "\n");
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.Open();
            var tables = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) tables.Add(r.GetString(0));
            }
            foreach (var table in tables)
            {
                // 列 (主キーの列を pk の順で先に、残りは表の定義の順)
                var cols = new List<(string Name, int Pk, int Cid)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"PRAGMA table_info({DatabaseBackupService.Q(table)})";
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) cols.Add((r.GetString(1), r.GetInt32(5), r.GetInt32(0)));
                }
                if (cols.Count == 0) continue;
                var ordered = cols.Where(c => c.Pk > 0).OrderBy(c => c.Pk).Concat(cols.Where(c => c.Pk == 0).OrderBy(c => c.Cid)).Select(c => DatabaseBackupService.Q(c.Name)).ToList();
                var colList = string.Join(", ", ordered);
                var orderBy = string.Join(", ", ordered.Select(c => c + " COLLATE BINARY"));
                Feed("\u001c" + table + "\n");
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT {colList} FROM {DatabaseBackupService.Q(table)} ORDER BY {orderBy}";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        for (int i = 0; i < r.FieldCount; i++)
                        {
                            if (i > 0) Feed("\u001f");
                            if (r.IsDBNull(i)) { Feed("N"); continue; }
                            switch (r.GetFieldType(i))
                            {
                                case var t when t == typeof(long): Feed("I" + r.GetInt64(i).ToString(CultureInfo.InvariantCulture)); break;
                                case var t when t == typeof(double): Feed("R" + r.GetDouble(i).ToString("R", CultureInfo.InvariantCulture)); break;
                                case var t when t == typeof(byte[]): Feed("B" + Convert.ToHexString((byte[])r.GetValue(i))); break;
                                default: Feed("T" + r.GetString(i)); break;
                            }
                        }
                        Feed("\u001e");
                    }
                }
            }
        }
        return Convert.ToHexString(sha.GetHashAndReset());
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

    // 共有フォルダの、自分以外の新しい書き出し (古い順)。読めない json・zip の無いものは log に残して飛ばす。
    // 両方で変更があるもの (相手の json の imported[自分] が自分の lastExportUtc より古い。自分が一度も書き出していなければ見ない) は BothChanged = true で返す
    // (自動の取り込みでは飛ばし、手動では取り込む。どちらにするかは呼ぶ側)
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
            // 両方で変更があるか: 自分が書き出したことがあり、相手がその最新の書き出しを取り込んでいない (imported に自分が無い・古い版で imported 自体が無いのも「取り込んでいない」)
            var bothChanged = false;
            if (lastExport != DateTime.MinValue)
            {
                DateTime? importedMine = null;
                if (m.Imported != null)
                    foreach (var kv in m.Imported)
                        if (string.Equals(kv.Key, machine, StringComparison.OrdinalIgnoreCase)) { importedMine = ParseUtc(kv.Value); break; }
                bothChanged = (importedMine ?? DateTime.MinValue) < lastExport;
            }
            result.Add(new Candidate(m.Machine, exportedAt.Value, jsonPath, zipPath, bothChanged));
        }
        return result.OrderBy(c => c.ExportedAtUtc).ToList();
    }

    // force: 両方で変更がある相手 (Candidate.BothChanged) も取り込む (手動のボタン)
    public static Task<ImportResult> ImportAsync(string syncFolder, string workDir, string machine, Action<string>? log = null,
        Action<string, string, Action<string>?>? refreshItemNames = null, bool force = false) =>
        Task.Run(() => Import(syncFolder, workDir, machine, log, refreshItemNames, force));

    // refreshItemNames: 翻訳の個人行が変わったときに呼ぶ (gamedata_cache.db のパス, 作業フォルダ, log)。null なら GameDataExtractor.RefreshItemNamesFromTranslations
    public static ImportResult Import(string syncFolder, string workDir, string machine, Action<string>? log = null,
        Action<string, string, Action<string>?>? refreshItemNames = null, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(syncFolder)) throw new InvalidOperationException("共有フォルダが設定されていません");
        if (!Directory.Exists(syncFolder)) throw new DirectoryNotFoundException($"共有フォルダが見つかりません: {syncFolder}");
        var result = new ImportResult();
        Gate.Wait();
        try
        {
            var candidates = ListCandidates(syncFolder, workDir, machine, log);
            if (!force)
            {
                foreach (var c in candidates.Where(c => c.BothChanged)) result.SkippedBothChanged.Add(c.Machine);
                candidates = candidates.Where(c => !c.BothChanged).ToList();
            }
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
                    // その相手を最後に取り込んだ書き出しの時刻 (墓石はこれより新しいものだけを当てる。初めてなら null = 全部)
                    var since = state.LastImportedUtc.TryGetValue(cand.Machine, out var lastStr) ? ParseUtc(lastStr) : null;
                    try
                    {
                        if (srcTrade != null) ImportTrade(tradePath, srcTrade, counts, since, cand.ExportedAtUtc, machine);
                        if (srcTrans != null) transChanged = ImportTranslations(transPath, srcTrans, counts, out glossaryChanged, since, cand.ExportedAtUtc, machine);
                        if (srcIndex != null && File.Exists(indexPath)) ImportKnowledge(indexPath, srcIndex, counts, since, cand.ExportedAtUtc, machine);
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
    // (先に相手の墓石を当て (ApplyTombstones)、INSERT OR REPLACE では自分の新しい墓石のある行を入れない (TombstoneGuardSql))
    private static void ImportTrade(string aPath, string bPath, Dictionary<string, int> counts, DateTime? since, DateTime exportedAtUtc, string machine)
    {
        using var conn = DatabaseBackupService.OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        EnsureTombstonesTable(conn, tx);   // main に無ければ作る (TombstoneGuardSql が参照する)
        ApplyTombstones(conn, tx, TradeTombstoneSpecs, since, machine, counts);
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
                // my_ships を相手で置き換えたら、所持船の装備 (ship_loadouts の "my|{my_ships.id}" の行) も自分のを消して相手のにする
                // (相手の my_ships の id と対応するため。ship_loadouts の相手の行はこの後の INSERT OR REPLACE で入る。"my|" 以外のキーは触らない)
                if (table == "my_ships" && DatabaseBackupService.Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = 'ship_loadouts'") > 0)
                    counts["ship_loadouts (my| 削除)"] = DatabaseBackupService.Exec(conn, tx, "DELETE FROM main.ship_loadouts WHERE substr(ship_key, 1, 3) = 'my|'");
            }
            else if (table == "hangar_meta")
            {
                DatabaseBackupService.EnsureMainTable(conn, tx, table);
                var netKeys = string.Join(", ", DatabaseBackupService.HangarMetaNetKeys.Select(k => $"'{k}'"));
                counts[table] = ExecWithExportedAt(conn, tx,
                    $"INSERT OR REPLACE INTO main.hangar_meta (key, value) SELECT s.key, s.value FROM src.hangar_meta s WHERE s.key NOT IN ({netKeys}) AND {TombstoneGuardSql(table, "s")}", exportedAtUtc);
            }
            else if (DatabaseBackupService.TradePersonalKeyedTables.Contains(table))
            {
                DatabaseBackupService.EnsureMainTable(conn, tx, table);
                var cols = string.Join(", ", DatabaseBackupService.CommonColumns(conn, tx, table).Select(c => "s." + DatabaseBackupService.Q(c)));
                var intoCols = string.Join(", ", DatabaseBackupService.CommonColumns(conn, tx, table).Select(DatabaseBackupService.Q));
                counts[table] = ExecWithExportedAt(conn, tx, $"INSERT OR REPLACE INTO main.{q} ({intoCols}) SELECT {cols} FROM src.{q} s WHERE {TombstoneGuardSql(table, "s")}", exportedAtUtc);
            }
        }
        DatabaseBackupService.NormalizeSequences(conn, tx);
        tx.Commit();
    }

    // translations.db: translations の個人行は key が同じとき japanese / source / translator / modified_at を相手の値にし、無ければ足す
    // (a の非個人行は消さない。値がすべて同じ行は変えない)。glossary も同じく english が同じ行は値が違うときだけ相手の値にし、無ければ足す。
    // 先に相手の墓石を当て (ApplyTombstones)、自分の新しい墓石のある行は入れない (TombstoneGuardSql)。
    // 返り値: 変わった (足した + 書き換えた) translations の行数。glossaryChanged: 同じく glossary の行数
    private static int ImportTranslations(string aPath, string bPath, Dictionary<string, int> counts, out int glossaryChanged, DateTime? since, DateTime exportedAtUtc, string machine)
    {
        glossaryChanged = 0;
        using var conn = DatabaseBackupService.OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        EnsureTombstonesTable(conn, tx);   // main に無ければ作る (TombstoneGuardSql が参照する)
        ApplyTombstones(conn, tx, TranslationTombstoneSpecs, since, machine, counts);
        var changed = 0;
        var personal = string.Join(", ", App.PersonalTranslationSources.Select(s => $"'{s}'"));
        if (DatabaseBackupService.SourceTableExists(conn, tx, "translations"))
        {
            var cols = DatabaseBackupService.CommonColumns(conn, tx, "translations");
            if (!cols.Contains("key", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("相手の translations に key の列がありません");
            var updateCols = new[] { "japanese", "source", "translator", "modified_at" }.Where(c => cols.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var colList = string.Join(", ", cols.Select(DatabaseBackupService.Q));
            var selList = string.Join(", ", cols.Select(c => "s." + DatabaseBackupService.Q(c)));
            // 値が違う行だけ書き換える (DO UPDATE の WHERE)。ExecuteNonQuery の件数 = 足した行 + 書き換えた行
            var set = string.Join(", ", updateCols.Select(c => $"{DatabaseBackupService.Q(c)} = excluded.{DatabaseBackupService.Q(c)}"));
            var differ = string.Join(" OR ", updateCols.Select(c => $"translations.{DatabaseBackupService.Q(c)} IS NOT excluded.{DatabaseBackupService.Q(c)}"));
            // 自分の行のほうが新しければ (modified_at の文字列比較。両 PC の時計が同じ前提) 相手の行で上書きしない
            var newer = updateCols.Contains("modified_at", StringComparer.OrdinalIgnoreCase) ? " AND " + NewerThanMineSql("translations", "modified_at") : "";
            var onConflict = updateCols.Count > 0 ? $"DO UPDATE SET {set} WHERE ({differ}){newer}" : "DO NOTHING";
            changed = ExecWithExportedAt(conn, tx,
                $"INSERT INTO main.translations ({colList}) SELECT {selList} FROM src.translations s WHERE COALESCE(s.source, '') IN ({personal}) AND {TombstoneGuardSql("translations", "s")} ON CONFLICT(key) {onConflict}",
                exportedAtUtc);
            counts["translations"] = changed;
        }
        if (DatabaseBackupService.SourceTableExists(conn, tx, "glossary"))
        {
            var cols = DatabaseBackupService.CommonColumns(conn, tx, "glossary");
            if (!cols.Contains("english", StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("相手の glossary に english の列がありません");
            var updateCols = cols.Where(c => !c.Equals("english", StringComparison.OrdinalIgnoreCase)).ToList();
            var colList = string.Join(", ", cols.Select(DatabaseBackupService.Q));
            var selList = string.Join(", ", cols.Select(c => "s." + DatabaseBackupService.Q(c)));
            // translations と同じく、値が違う行だけ書き換えて、足した + 書き換えた行数を数える (付け直しの要否に使う)
            var set = string.Join(", ", updateCols.Select(c => $"{DatabaseBackupService.Q(c)} = excluded.{DatabaseBackupService.Q(c)}"));
            var differ = string.Join(" OR ", updateCols.Select(c => $"glossary.{DatabaseBackupService.Q(c)} IS NOT excluded.{DatabaseBackupService.Q(c)}"));
            // translations と同じく、自分の行のほうが新しければ (created_at) 上書きしない
            var newer = updateCols.Contains("created_at", StringComparer.OrdinalIgnoreCase) ? " AND " + NewerThanMineSql("glossary", "created_at") : "";
            var onConflict = updateCols.Count > 0 ? $"DO UPDATE SET {set} WHERE ({differ}){newer}" : "DO NOTHING";
            glossaryChanged = ExecWithExportedAt(conn, tx,
                $"INSERT INTO main.glossary ({colList}) SELECT {selList} FROM src.glossary s WHERE {TombstoneGuardSql("glossary", "s")} ON CONFLICT(english) {onConflict}",
                exportedAtUtc);
            counts["glossary"] = glossaryChanged;
        }
        tx.Commit();
        return changed;
    }

    // ON CONFLICT ... DO UPDATE の WHERE に足す条件: 相手の行 (excluded) の時刻の列が自分の行より新しいときだけ上書きする。
    // 時刻は datetime('now', 'localtime') の "YYYY-MM-DD HH:MM:SS" の文字列比較 (両 PC の時計が同じ前提)。どちらかが NULL・この形でない (旧形式) なら今までどおり上書きする
    private static string NewerThanMineSql(string table, string timeCol)
    {
        const string pattern = "'[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9] [0-9][0-9]:[0-9][0-9]:[0-9][0-9]'";
        var mine = $"{table}.{DatabaseBackupService.Q(timeCol)}";
        var theirs = $"excluded.{DatabaseBackupService.Q(timeCol)}";
        return $"({mine} IS NULL OR {theirs} IS NULL OR {mine} NOT GLOB {pattern} OR {theirs} NOT GLOB {pattern} OR {theirs} > {mine})";
    }

    // gamedata_cache.db: knowledge は同じ本文 (content) の行が無ければ新しい id で足す (DatabaseBackupService.ImportIndexDb と同じ)。
    // 先に相手の墓石を当て (ApplyTombstones)、自分の新しい墓石のある本文は足さない (TombstoneGuardSql)
    private static void ImportKnowledge(string aPath, string bPath, Dictionary<string, int> counts, DateTime? since, DateTime exportedAtUtc, string machine)
    {
        using var conn = DatabaseBackupService.OpenWithSource(aPath, bPath);
        using var tx = conn.BeginTransaction();
        EnsureTombstonesTable(conn, tx);   // main に無ければ作る (TombstoneGuardSql が参照する)
        ApplyTombstones(conn, tx, KnowledgeTombstoneSpecs, since, machine, counts);
        if (DatabaseBackupService.SourceTableExists(conn, tx, "knowledge")
            && DatabaseBackupService.Scalar(conn, tx, "SELECT COUNT(*) FROM main.sqlite_master WHERE type = 'table' AND name = 'knowledge'") > 0)
        {
            var cols = DatabaseBackupService.CommonColumns(conn, tx, "knowledge").Where(c => !c.Equals("id", StringComparison.OrdinalIgnoreCase)).ToList();
            if (cols.Contains("content", StringComparer.OrdinalIgnoreCase))
            {
                var colList = string.Join(", ", cols.Select(DatabaseBackupService.Q));
                var selList = string.Join(", ", cols.Select(c => "s." + DatabaseBackupService.Q(c)));
                counts["knowledge"] = ExecWithExportedAt(conn, tx, $"INSERT INTO main.knowledge ({colList}) SELECT {selList} FROM src.knowledge s "
                    + $"WHERE NOT EXISTS (SELECT 1 FROM main.knowledge k WHERE k.content = s.content) AND {TombstoneGuardSql("knowledge", "s")}", exportedAtUtc);
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
