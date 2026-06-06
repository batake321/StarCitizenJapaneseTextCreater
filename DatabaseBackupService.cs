using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public enum BackupCategory
{
    Translations,
    Glossary,
    Index,
    Knowledge,
    Trade
}

public static class DatabaseBackupService
{
    private static readonly string[] TranslationTables = ["translations"];
    private static readonly string[] GlossaryTables = ["glossary"];
    private static readonly string[] IndexTables = ["ships", "ship_ports", "items", "missions", "commodities", "gamedata_meta", "gamedata_cache"];
    private static readonly string[] KnowledgeTables = ["knowledge"];
    private static readonly string[] TradeTables = ["trade_prices", "trade_ships", "trade_terminals", "trade_meta", "my_ships"];

    public static string[] GetTables(BackupCategory category) => category switch
    {
        BackupCategory.Translations => TranslationTables,
        BackupCategory.Glossary => GlossaryTables,
        BackupCategory.Index => IndexTables,
        BackupCategory.Knowledge => KnowledgeTables,
        BackupCategory.Trade => TradeTables,
        _ => []
    };

    public static async Task ExportAsync(string translationDbPath, string indexDbPath, string outputPath,
        Action<string>? onStatus = null, string? tradeDbPath = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sc_export_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await Task.Run(() =>
            {
                if (File.Exists(translationDbPath))
                {
                    onStatus?.Invoke("translations.db をコピー中...");
                    File.Copy(translationDbPath, Path.Combine(tempDir, "translations.db"), true);
                }
                if (File.Exists(indexDbPath))
                {
                    onStatus?.Invoke("gamedata_cache.db をコピー中...");
                    File.Copy(indexDbPath, Path.Combine(tempDir, "gamedata_cache.db"), true);
                }
                if (!string.IsNullOrEmpty(tradeDbPath) && File.Exists(tradeDbPath))
                {
                    onStatus?.Invoke("trade_cache.db をコピー中...");
                    File.Copy(tradeDbPath, Path.Combine(tempDir, "trade_cache.db"), true);
                }

                onStatus?.Invoke("ZIP を作成中...");
                if (File.Exists(outputPath)) File.Delete(outputPath);
                ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, false);
            });
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }

        onStatus?.Invoke("エクスポート完了");
    }

    public static async Task<Dictionary<BackupCategory, int>> InspectAsync(string backupPath)
    {
        var sql = await ReadCompressedSqlAsync(backupPath);
        var result = new Dictionary<BackupCategory, int>();

        foreach (BackupCategory cat in Enum.GetValues<BackupCategory>())
        {
            int count = 0;
            foreach (var table in GetTables(cat))
            {
                var marker = $"-- TABLE: {table}";
                if (sql.Contains(marker))
                    count += CountInserts(sql, table);
            }
            if (count > 0)
                result[cat] = count;
        }
        return result;
    }

    public static async Task<Dictionary<BackupCategory, int>> InspectDbFileAsync(string dbPath)
    {
        var result = new Dictionary<BackupCategory, int>();
        await Task.Run(() =>
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            foreach (BackupCategory cat in Enum.GetValues<BackupCategory>())
            {
                int count = 0;
                foreach (var table in GetTables(cat))
                {
                    if (!TableExists(conn, table)) continue;
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
                    count += (int)(long)(cmd.ExecuteScalar() ?? 0L);
                }
                if (count > 0) result[cat] = count;
            }
        });
        return result;
    }

    public static async Task<Dictionary<BackupCategory, int>> InspectZipAsync(string zipPath)
    {
        var result = new Dictionary<BackupCategory, int>();
        var tempDir = Path.Combine(Path.GetTempPath(), $"sc_backup_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempDir));
            foreach (var dbFile in Directory.GetFiles(tempDir, "*.db"))
            {
                var partial = await InspectDbFileAsync(dbFile);
                foreach (var kv in partial)
                {
                    if (result.ContainsKey(kv.Key))
                        result[kv.Key] += kv.Value;
                    else
                        result[kv.Key] = kv.Value;
                }
            }
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
        return result;
    }

    public static async Task ImportFromZipAsync(string zipPath, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, ImportMode mode, Action<string>? onStatus = null, string? tradeDbPath = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"sc_backup_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            onStatus?.Invoke("ZIP を展開中...");
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, tempDir));

            foreach (var dbFile in Directory.GetFiles(tempDir, "*.db"))
            {
                onStatus?.Invoke($"{Path.GetFileName(dbFile)} をインポート中...");
                await ImportFromDbAsync(dbFile, translationDbPath, indexDbPath, categories, mode, onStatus, tradeDbPath);
            }
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }

        onStatus?.Invoke("インポート完了");
    }

    public static async Task ImportFromDbAsync(string sourceDbPath, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, ImportMode mode, Action<string>? onStatus = null, string? tradeDbPath = null)
    {
        var cats = categories.ToHashSet();
        var allowedTables = new HashSet<string>();
        foreach (var cat in cats)
            foreach (var t in GetTables(cat))
                allowedTables.Add(t);

        await Task.Run(() =>
        {
            using var srcConn = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly");
            srcConn.Open();

            var translationTables = allowedTables.Intersect(TranslationTables.Concat(GlossaryTables)).ToHashSet();
            var indexTables = allowedTables.Intersect(IndexTables.Concat(KnowledgeTables)).ToHashSet();
            var tradeTables = allowedTables.Intersect(TradeTables).ToHashSet();

            if (translationTables.Count > 0)
            {
                onStatus?.Invoke("翻訳DBにインポート中...");
                using var db = new TranslationDatabase(translationDbPath);
                CopyTables(srcConn, db.Connection, translationTables, mode, onStatus);
            }

            if (indexTables.Count > 0)
            {
                onStatus?.Invoke("インデックスDBにインポート中...");
                using var destConn = new SqliteConnection($"Data Source={indexDbPath}");
                destConn.Open();
                EnsureIndexSchema(destConn);
                CopyTables(srcConn, destConn, indexTables, mode, onStatus);
            }

            if (tradeTables.Count > 0 && !string.IsNullOrEmpty(tradeDbPath))
            {
                onStatus?.Invoke("交易DBにインポート中...");
                using var destConn = new SqliteConnection($"Data Source={tradeDbPath}");
                destConn.Open();
                EnsureTradeSchema(destConn);
                CopyTables(srcConn, destConn, tradeTables, mode, onStatus);
            }
        });

        onStatus?.Invoke("インポート完了");
    }

    private static void EnsureTradeSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS trade_prices (
                commodity_id INTEGER, commodity_name TEXT, commodity_kind TEXT, container_scu INTEGER,
                terminal TEXT, city TEXT, outpost TEXT, moon TEXT, planet TEXT, star_system TEXT, location_short TEXT,
                price_buy REAL, price_sell REAL, price_buy_avg REAL, price_sell_avg REAL,
                scu_buy INTEGER, scu_sell INTEGER, scu_buy_avg INTEGER, scu_sell_avg INTEGER,
                date_modified TEXT, fetched_at TEXT, patch TEXT, is_current INTEGER DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS trade_ships (name TEXT, manufacturer TEXT, scu INTEGER, fetched_at TEXT);
            CREATE TABLE IF NOT EXISTS trade_terminals (name TEXT PRIMARY KEY, has_loading_dock INTEGER, has_docking_port INTEGER, is_cargo_center INTEGER);
            CREATE TABLE IF NOT EXISTS trade_meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS my_ships (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL, manufacturer TEXT DEFAULT '', scu INTEGER DEFAULT 0,
                notes TEXT DEFAULT '', added_at TEXT DEFAULT (datetime('now','localtime'))
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void CopyTables(SqliteConnection src, SqliteConnection dest,
        HashSet<string> tables, ImportMode mode, Action<string>? onStatus)
    {
        using (var fk = dest.CreateCommand()) { fk.CommandText = "PRAGMA foreign_keys = OFF"; fk.ExecuteNonQuery(); }
        using var tx = dest.BeginTransaction();
        foreach (var table in tables)
        {
            if (!TableExists(src, table)) continue;

            if (mode == ImportMode.Drop)
            {
                using var delCmd = dest.CreateCommand();
                delCmd.CommandText = $"DELETE FROM {table}";
                delCmd.Transaction = tx;
                delCmd.ExecuteNonQuery();
            }

            using var readCmd = src.CreateCommand();
            readCmd.CommandText = $"SELECT * FROM {table}";
            using var reader = readCmd.ExecuteReader();

            var colCount = reader.FieldCount;
            var colNames = new string[colCount];
            for (int i = 0; i < colCount; i++)
                colNames[i] = reader.GetName(i);

            var colList = string.Join(", ", colNames);
            var paramList = string.Join(", ", colNames.Select((_, i) => $"@p{i}"));

            string insertSql;
            if (mode == ImportMode.Add && table == "translations" && colNames.Contains("japanese"))
            {
                // 追加モード: ローカルが未翻訳ならインポート側で上書き
                insertSql = $"INSERT INTO {table} ({colList}) VALUES ({paramList}) ON CONFLICT(key) DO UPDATE SET japanese = excluded.japanese, source = excluded.source, translator = excluded.translator, modified_at = excluded.modified_at WHERE {table}.japanese IS NULL OR {table}.japanese = ''";
            }
            else
            {
                var insertVerb = mode == ImportMode.Add ? "INSERT OR IGNORE" : "INSERT OR REPLACE";
                insertSql = $"{insertVerb} INTO {table} ({colList}) VALUES ({paramList})";
            }

            int count = 0;
            while (reader.Read())
            {
                using var insertCmd = dest.CreateCommand();
                insertCmd.CommandText = insertSql;
                insertCmd.Transaction = tx;
                for (int i = 0; i < colCount; i++)
                    insertCmd.Parameters.AddWithValue($"@p{i}", reader.GetValue(i));
                insertCmd.ExecuteNonQuery();
                count++;

                if (count % 5000 == 0)
                    onStatus?.Invoke($"{table}: {count} 件処理中...");
            }
            onStatus?.Invoke($"{table}: {count} 件完了");
        }
        tx.Commit();
    }

    public static async Task ImportAsync(string backupPath, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, ImportMode mode, Action<string>? onStatus = null)
    {
        var cats = categories.ToHashSet();
        var sql = await ReadCompressedSqlAsync(backupPath);
        var statements = ParseStatements(sql);

        var translationNeeded = cats.Contains(BackupCategory.Translations) || cats.Contains(BackupCategory.Glossary);
        var indexNeeded = cats.Contains(BackupCategory.Index) || cats.Contains(BackupCategory.Knowledge);

        var allowedTables = new HashSet<string>();
        foreach (var cat in cats)
            foreach (var t in GetTables(cat))
                allowedTables.Add(t);

        if (translationNeeded)
        {
            onStatus?.Invoke("翻訳DBにインポート中...");
            using var db = new TranslationDatabase(translationDbPath);
            ExecuteFiltered(db.Connection, statements, allowedTables, mode, onStatus);
        }

        if (indexNeeded)
        {
            onStatus?.Invoke("インデックスDBにインポート中...");
            using var conn = new SqliteConnection($"Data Source={indexDbPath}");
            conn.Open();
            EnsureIndexSchema(conn);
            ExecuteFiltered(conn, statements, allowedTables, mode, onStatus);
        }

        onStatus?.Invoke("インポート完了");
    }

    private static void DumpTables(SqliteConnection conn, string[] tables, StringBuilder sb)
    {
        foreach (var table in tables)
        {
            if (!TableExists(conn, table)) continue;

            sb.AppendLine($"-- TABLE: {table}");

            var createSql = GetCreateTableSql(conn, table);
            if (!string.IsNullOrEmpty(createSql))
            {
                sb.AppendLine($"CREATE TABLE IF NOT EXISTS {table} ({ExtractColumnDefs(createSql)});");
            }

            sb.AppendLine($"DELETE FROM {table};");

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table}";
            using var reader = cmd.ExecuteReader();

            var colCount = reader.FieldCount;
            var colNames = new string[colCount];
            for (int i = 0; i < colCount; i++)
                colNames[i] = reader.GetName(i);

            var colList = string.Join(", ", colNames);

            while (reader.Read())
            {
                var values = new string[colCount];
                for (int i = 0; i < colCount; i++)
                {
                    if (reader.IsDBNull(i))
                        values[i] = "NULL";
                    else
                    {
                        var fieldType = reader.GetFieldType(i);
                        if (fieldType == typeof(long) || fieldType == typeof(int) || fieldType == typeof(double) || fieldType == typeof(float))
                            values[i] = reader.GetValue(i).ToString()!;
                        else
                            values[i] = $"'{EscapeSql(reader.GetString(i))}'";
                    }
                }
                sb.AppendLine($"INSERT OR REPLACE INTO {table} ({colList}) VALUES ({string.Join(", ", values)});");
            }
            sb.AppendLine();
        }
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return (long)(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static string GetCreateTableSql(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return cmd.ExecuteScalar()?.ToString() ?? "";
    }

    private static string ExtractColumnDefs(string createSql)
    {
        var start = createSql.IndexOf('(');
        var end = createSql.LastIndexOf(')');
        if (start < 0 || end < 0) return "";
        return createSql[(start + 1)..end].Trim();
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static async Task<string> ReadCompressedSqlAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static int CountInserts(string sql, string table)
    {
        int count = 0;
        var pattern = $"INSERT OR REPLACE INTO {table} ";
        int idx = 0;
        while ((idx = sql.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    private static List<string> ParseStatements(string sql)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inString = false;

        for (int i = 0; i < sql.Length; i++)
        {
            var c = sql[i];

            if (c == '\'' && !inString)
            {
                inString = true;
                sb.Append(c);
            }
            else if (c == '\'' && inString)
            {
                sb.Append(c);
                if (i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i++;
                }
                else
                {
                    inString = false;
                }
            }
            else if (c == ';' && !inString)
            {
                var stmt = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(stmt) && !stmt.StartsWith("--"))
                    result.Add(stmt);
                sb.Clear();
            }
            else if (c == '\n' && !inString)
            {
                var current = sb.ToString().TrimStart();
                if (current.StartsWith("--"))
                    sb.Clear();
                else
                    sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        var last = sb.ToString().Trim();
        if (!string.IsNullOrEmpty(last) && !last.StartsWith("--"))
            result.Add(last);

        return result;
    }

    private static void ExecuteFiltered(SqliteConnection conn, List<string> statements,
        HashSet<string> allowedTables, ImportMode mode, Action<string>? onStatus)
    {
        using var tx = conn.BeginTransaction();
        var deletedTables = new HashSet<string>();
        int executed = 0;

        foreach (var stmt in statements)
        {
            var tableName = ExtractTableName(stmt);
            if (tableName == null || !allowedTables.Contains(tableName))
                continue;

            var upper = stmt.TrimStart().ToUpperInvariant();

            if (upper.StartsWith("DELETE FROM"))
            {
                if (mode == ImportMode.Drop)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt;
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                    deletedTables.Add(tableName);
                }
                continue;
            }

            if (upper.StartsWith("INSERT OR REPLACE"))
            {
                if (mode == ImportMode.Add)
                {
                    var adjusted = stmt.Replace("INSERT OR REPLACE INTO", "INSERT OR IGNORE INTO");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = adjusted;
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                else
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = stmt;
                    cmd.Transaction = tx;
                    cmd.ExecuteNonQuery();
                }
                executed++;
                if (executed % 5000 == 0)
                    onStatus?.Invoke($"{executed} 件処理中...");
                continue;
            }

            using var other = conn.CreateCommand();
            other.CommandText = stmt;
            other.Transaction = tx;
            other.ExecuteNonQuery();
        }
        tx.Commit();
    }

    private static string? ExtractTableName(string stmt)
    {
        var upper = stmt.ToUpperInvariant();
        string[] patterns = ["INSERT OR REPLACE INTO ", "DELETE FROM ", "CREATE TABLE IF NOT EXISTS "];
        foreach (var p in patterns)
        {
            var idx = upper.IndexOf(p, StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = stmt[(idx + p.Length)..].TrimStart();
            var end = rest.IndexOfAny([' ', '(', ';']);
            return end > 0 ? rest[..end] : rest.TrimEnd(';');
        }
        return null;
    }

    private static void EnsureIndexSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS gamedata_cache (
                query_key TEXT PRIMARY KEY,
                result_json TEXT NOT NULL,
                cached_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS gamedata_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ships (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                manufacturer TEXT,
                career TEXT,
                role TEXT,
                crew_size INTEGER,
                size INTEGER,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ship_ports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ship_record_name TEXT NOT NULL,
                port_name TEXT,
                item_type TEXT,
                size INTEGER
            );
            CREATE TABLE IF NOT EXISTS items (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                item_type TEXT,
                item_sub_type TEXT,
                size INTEGER,
                grade INTEGER,
                manufacturer TEXT,
                component_type TEXT,
                component_json TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS missions (
                record_name TEXT PRIMARY KEY,
                title TEXT,
                title_hud TEXT,
                mission_type TEXT,
                difficulty TEXT,
                mission_giver TEXT,
                location_label TEXT,
                description TEXT,
                reward_min REAL,
                reward_max REAL,
                required_reputation TEXT,
                lawfulness_type TEXT,
                jurisdiction TEXT,
                time_limit TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS commodities (
                record_name TEXT PRIMARY KEY,
                name TEXT,
                symbol TEXT,
                volatility TEXT,
                raw_json TEXT NOT NULL,
                extracted_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS knowledge (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                category TEXT NOT NULL DEFAULT 'general',
                content TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
        """;
        cmd.ExecuteNonQuery();
    }
}
