using System.IO;

namespace StarCitizenJapaneseTextCreater;

// ステーションの 3D の歩くモードのコメントの印 (2026-11-05、利用者の指示「enter で文字入れて印付けるのは、いつでもできて入力したら即反映してほしい」
// 「DB格納でいつでも」): 場所 (record) ごとに、場所の座標 (絶対) と文を view3d_notes.db に入れ、3D を開くたびに出す。
// 配る DB (view3d_layout.db) はアプリの更新で丸ごと入れ替わるので、個人のデータは別のファイルに入れる (view3d_fill_walk.db と同じ流儀)。
//   notes(id INTEGER PRIMARY KEY, record TEXT NOT NULL, x REAL, y REAL, z REAL, text TEXT NOT NULL, created_utc TEXT NOT NULL)
//   record ごとに取り出せる形 (場所ごとの書き出し・読み込みは次の版)。マイハンガーの中の印も record は同じ (座標は場所の座標)
// 読みは Mode=ReadOnly、書きは Mode=ReadWriteCreate、どちらも Pooling=False で終わりに ClearAllPools
internal static class StationNotes
{
    public const string DbName = "view3d_notes.db";
    public static string DbPath => Path.Combine(StationPlaces.Folder, DbName);
    public sealed record Note(long Id, double[] Pos, string Text);
    private static readonly object WriteLock = new();
    private const string CreateSql = "CREATE TABLE IF NOT EXISTS notes(id INTEGER PRIMARY KEY, record TEXT NOT NULL, x REAL, y REAL, z REAL, text TEXT NOT NULL, created_utc TEXT NOT NULL)";

    // 場所 (record) の印を全部 (古い順)。DB が無ければ空
    public static List<Note> Read(string record)
    {
        var res = new List<Note>();
        var path = DbPath;
        if (!File.Exists(path)) return res;
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, x, y, z, text FROM notes WHERE record = $r ORDER BY id";
            cmd.Parameters.AddWithValue("$r", record);
            using var r = cmd.ExecuteReader();
            while (r.Read()) res.Add(new Note(r.GetInt64(0), [r.GetDouble(1), r.GetDouble(2), r.GetDouble(3)], r.GetString(4)));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        return res;
    }

    // 入れて、付いた id を返す
    public static long Add(string record, double[] pos, string text)
    {
        if (pos.Length != 3) throw new ArgumentException("座標は 3 つ要ります", nameof(pos));
        lock (WriteLock)
        {
            try
            {
                var path = DbPath;
                if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
                c.Open();
                using (var cmd = c.CreateCommand()) { cmd.CommandText = CreateSql; cmd.ExecuteNonQuery(); }
                using var ins = c.CreateCommand();
                ins.CommandText = "INSERT INTO notes(record, x, y, z, text, created_utc) VALUES($r, $x, $y, $z, $t, $u); SELECT last_insert_rowid()";
                ins.Parameters.AddWithValue("$r", record);
                ins.Parameters.AddWithValue("$x", pos[0]);
                ins.Parameters.AddWithValue("$y", pos[1]);
                ins.Parameters.AddWithValue("$z", pos[2]);
                ins.Parameters.AddWithValue("$t", text);
                ins.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
                return Convert.ToInt64(ins.ExecuteScalar() ?? 0L);
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        }
    }

    // 文を直す (無ければ何もしない。2026-11-06)
    public static void Update(long id, string text)
    {
        lock (WriteLock)
        {
            try
            {
                var path = DbPath;
                if (!File.Exists(path)) return;
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "UPDATE notes SET text = $t WHERE id = $i";
                cmd.Parameters.AddWithValue("$t", text);
                cmd.Parameters.AddWithValue("$i", id);
                cmd.ExecuteNonQuery();
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        }
    }

    // 消す (無ければ何もしない)
    public static void Delete(long id)
    {
        lock (WriteLock)
        {
            try
            {
                var path = DbPath;
                if (!File.Exists(path)) return;
                using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "DELETE FROM notes WHERE id = $i";
                cmd.Parameters.AddWithValue("$i", id);
                cmd.ExecuteNonQuery();
            }
            finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        }
    }
}
