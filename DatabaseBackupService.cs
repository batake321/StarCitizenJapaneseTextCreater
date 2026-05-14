using System.IO.Compression;
using System.Text;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public enum BackupCategory
{
    Translations,
    Glossary,
    Index
}

public static class DatabaseBackupService
{
    private static readonly string[] TranslationTables = ["translations"];
    private static readonly string[] GlossaryTables = ["glossary"];
    private static readonly string[] IndexTables = ["ships", "ship_ports", "items", "missions", "commodities", "gamedata_meta", "gamedata_cache"];

    public static string[] GetTables(BackupCategory category) => category switch
    {
        BackupCategory.Translations => TranslationTables,
        BackupCategory.Glossary => GlossaryTables,
        BackupCategory.Index => IndexTables,
        _ => []
    };

    public static async Task ExportAsync(string translationDbPath, string indexDbPath, string outputPath,
        IEnumerable<BackupCategory> categories, Action<string>? onStatus = null)
    {
        var cats = categories.ToHashSet();
        var sb = new StringBuilder();
        sb.AppendLine("-- StarCitizenJapaneseTextCreater Database Backup");
        sb.AppendLine($"-- Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        if (cats.Contains(BackupCategory.Translations) || cats.Contains(BackupCategory.Glossary))
        {
            if (File.Exists(translationDbPath))
            {
                using var conn = new SqliteConnection($"Data Source={translationDbPath};Mode=ReadOnly");
                conn.Open();
                if (cats.Contains(BackupCategory.Translations))
                {
                    onStatus?.Invoke("翻訳データをエクスポート中...");
                    DumpTables(conn, TranslationTables, sb);
                }
                if (cats.Contains(BackupCategory.Glossary))
                {
                    onStatus?.Invoke("用語集をエクスポート中...");
                    DumpTables(conn, GlossaryTables, sb);
                }
            }
        }

        if (cats.Contains(BackupCategory.Index))
        {
            if (File.Exists(indexDbPath))
            {
                onStatus?.Invoke("インデックスデータをエクスポート中...");
                using var conn = new SqliteConnection($"Data Source={indexDbPath};Mode=ReadOnly");
                conn.Open();
                DumpTables(conn, IndexTables, sb);
            }
        }

        onStatus?.Invoke("圧縮中...");
        var sql = sb.ToString();
        await using var fs = File.Create(outputPath);
        await using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        await gz.WriteAsync(Encoding.UTF8.GetBytes(sql));

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

    public static async Task ImportAsync(string backupPath, string translationDbPath, string indexDbPath,
        IEnumerable<BackupCategory> categories, ImportMode mode, Action<string>? onStatus = null)
    {
        var cats = categories.ToHashSet();
        var sql = await ReadCompressedSqlAsync(backupPath);
        var statements = ParseStatements(sql);

        var translationNeeded = cats.Contains(BackupCategory.Translations) || cats.Contains(BackupCategory.Glossary);
        var indexNeeded = cats.Contains(BackupCategory.Index);

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
        foreach (var line in sql.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("--"))
                continue;
            result.Add(trimmed);
        }
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
        """;
        cmd.ExecuteNonQuery();
    }
}
