using System.Text;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

public class TranslationDatabase : IDisposable
{
    private readonly SqliteConnection _conn;

    public TranslationDatabase(string dbPath)
    {
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS translations (
                key TEXT PRIMARY KEY,
                english TEXT NOT NULL,
                japanese TEXT,
                source TEXT DEFAULT 'official',
                modified_at TEXT DEFAULT (datetime('now', 'localtime'))
            );
            CREATE INDEX IF NOT EXISTS idx_source ON translations(source);
            """;
        cmd.ExecuteNonQuery();
    }

    public void ImportFromIni(Dictionary<string, string> english, Dictionary<string, string> japanese)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO translations (key, english, japanese, source)
            VALUES ($key, $en, $ja, $src)
            ON CONFLICT(key) DO UPDATE SET
                english = excluded.english,
                japanese = CASE WHEN excluded.japanese IS NOT NULL AND excluded.japanese != '' THEN excluded.japanese ELSE translations.japanese END,
                modified_at = datetime('now', 'localtime')
            """;
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);

        int count = 0;
        foreach (var (key, enVal) in english)
        {
            pKey.Value = key;
            pEn.Value = enVal;
            pJa.Value = japanese.TryGetValue(key, out var jaVal) && !string.IsNullOrWhiteSpace(jaVal) ? jaVal : (object)DBNull.Value;
            pSrc.Value = japanese.ContainsKey(key) ? "official" : "untranslated";
            cmd.ExecuteNonQuery();
            count++;
        }

        tx.Commit();
        Console.WriteLine($"  DB imported: {count} entries");
    }

    public void ImportAiTranslations(string jsonlPath)
    {
        if (!File.Exists(jsonlPath)) return;

        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE translations SET japanese = $ja, source = 'ai', modified_at = datetime('now', 'localtime')
            WHERE key = $key AND (japanese IS NULL OR japanese = '' OR source = 'ai' OR source = 'untranslated')
            """;
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);

        int count = 0;
        foreach (var line in File.ReadLines(jsonlPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                var key = doc.RootElement.GetProperty("key").GetString() ?? "";
                var ja = doc.RootElement.GetProperty("ja").GetString() ?? "";
                if (key.Length > 0 && ja.Length > 0)
                {
                    pKey.Value = key;
                    pJa.Value = ja;
                    count += cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }
        tx.Commit();
        Console.WriteLine($"  DB updated with AI translations: {count} entries");
    }

    public void UpdateTranslation(string key, string japanese, string source = "manual")
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE translations SET japanese = $ja, source = $src, modified_at = datetime('now', 'localtime') WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$ja", japanese);
        cmd.Parameters.AddWithValue("$src", source);
        cmd.ExecuteNonQuery();
    }

    public Dictionary<string, string> GetAllTranslations()
    {
        var result = new Dictionary<string, string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, japanese FROM translations WHERE japanese IS NOT NULL AND japanese != ''";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    public (int total, int translated, int official, int ai, int manual, int untranslated) GetStats()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                COUNT(CASE WHEN japanese IS NOT NULL AND japanese != '' THEN 1 END) as translated,
                COUNT(CASE WHEN source = 'official' AND japanese IS NOT NULL AND japanese != '' THEN 1 END) as official,
                COUNT(CASE WHEN source = 'ai' THEN 1 END) as ai,
                COUNT(CASE WHEN source = 'manual' THEN 1 END) as manual,
                COUNT(CASE WHEN japanese IS NULL OR japanese = '' THEN 1 END) as untranslated
            FROM translations
            """;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
    }

    public void ExportCsv(string csvPath)
    {
        using var writer = new StreamWriter(csvPath, false, new UTF8Encoding(true));
        writer.WriteLine("key,english,japanese,source,modified_at");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT key, english, japanese, source, modified_at FROM translations ORDER BY key";
        using var reader = cmd.ExecuteReader();
        int count = 0;
        while (reader.Read())
        {
            var key = EscapeCsv(reader.GetString(0));
            var en = EscapeCsv(reader.GetString(1));
            var ja = EscapeCsv(reader.IsDBNull(2) ? "" : reader.GetString(2));
            var src = EscapeCsv(reader.GetString(3));
            var mod = EscapeCsv(reader.IsDBNull(4) ? "" : reader.GetString(4));
            writer.WriteLine($"{key},{en},{ja},{src},{mod}");
            count++;
        }
        Console.WriteLine($"  CSV exported: {count} entries -> {csvPath}");
    }

    public int ImportCsv(string csvPath)
    {
        using var reader = new StreamReader(csvPath, Encoding.UTF8);
        var header = reader.ReadLine();
        if (header == null) return 0;

        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO translations (key, english, japanese, source, modified_at)
            VALUES ($key, $en, $ja, $src, datetime('now', 'localtime'))
            ON CONFLICT(key) DO UPDATE SET
                japanese = excluded.japanese,
                source = 'csv',
                modified_at = datetime('now', 'localtime')
            """;
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pEn = cmd.Parameters.Add("$en", SqliteType.Text);
        var pJa = cmd.Parameters.Add("$ja", SqliteType.Text);
        var pSrc = cmd.Parameters.Add("$src", SqliteType.Text);

        int count = 0;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var fields = ParseCsvLine(line);
            if (fields.Count < 3) continue;

            pKey.Value = fields[0];
            pEn.Value = fields[1];
            pJa.Value = string.IsNullOrEmpty(fields[2]) ? (object)DBNull.Value : fields[2];
            pSrc.Value = fields.Count > 3 ? fields[3] : "csv";
            cmd.ExecuteNonQuery();
            count++;
        }

        tx.Commit();
        Console.WriteLine($"  CSV imported: {count} entries from {csvPath}");
        return count;
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}
