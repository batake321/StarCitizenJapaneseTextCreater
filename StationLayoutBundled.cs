using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace StarCitizenJapaneseTextCreater;

// 同梱の区画の置き方の一覧 (インストール先の view3d_layout.db) を、置き場所の DB が今のゲームの版のときにも写すかの決まり (2026-11-05、親の決定)。
//   写す: 置き場所の DB も同梱の DB も今のゲームの版 (meta の format と stamp が同じ。StationView3D.LayoutDbIsCurrent と同じ見方) で、
//         同梱の DB の作った時刻 (meta の built_utc) が置き場所の DB のものより新しいとき。
//         → DB だけの更新 (release_db.ps1) やアプリの更新で、同じパッチのまま配置を作り直して配ったものが入る。
//   写さない: 同梱が古いか同じ時刻 (利用者が後から［マップをまとめて取り込む］で作り直した DB を、古い同梱の物で替えない)・
//             どちらかが今の版でない (そのときの動きは今までどおり StationView3DLayout.Start が決める)・built_utc が無い・読めない。
// built_utc は、一括で作ったとき (StationView3D.BuildLayoutDb) だけ書く。ハンガーの中を 1 か所足す処理 (StationView3D.PutLayoutDb) では変わらないので、
// ハンガーを開いただけの DB は、同じ同梱の物と時刻が同じままで、アプリの更新のたびに写し直されることはない。
// 確かめの道具 (小さな偽の DB) から直接呼べるよう、DB の読み取りもここに持つ (ほかの型に寄らない)
internal static class StationLayoutBundled
{
    public static bool IsNewer(string workPath, string bundledPath, string format, string stampKey) =>
        WhyNot(workPath, bundledPath, format, stampKey) == null;

    // 写さない理由 (写すなら null)
    public static string? WhyNot(string workPath, string bundledPath, string format, string stampKey)
    {
        if (!File.Exists(bundledPath)) return "同梱の DB が無い";
        var work = ReadMeta(workPath);
        if (work == null) return "置き場所の DB が無い・読めない";
        var bundled = ReadMeta(bundledPath);
        if (bundled == null) return "同梱の DB を読めない";
        if (!IsCurrent(work, format, stampKey)) return "置き場所の DB が今のゲームの版でない";
        if (!IsCurrent(bundled, format, stampKey)) return "同梱の DB が今のゲームの版でない";
        if (BuiltUtc(work) is not { } workBuilt) return "置き場所の DB に作った時刻 (built_utc) が無い・読めない";
        if (BuiltUtc(bundled) is not { } bundledBuilt) return "同梱の DB に作った時刻 (built_utc) が無い・読めない";
        if (bundledBuilt <= workBuilt) return "同梱の DB の作った時刻が、置き場所の DB より新しくない";
        return null;
    }

    private static bool IsCurrent(Dictionary<string, string> meta, string format, string stampKey) =>
        meta.TryGetValue("format", out var f) && f == format && meta.TryGetValue("stamp", out var s) && s == stampKey;

    // built_utc は DateTime.UtcNow.ToString("o") の形 (StationView3D.BuildLayoutDb)
    private static DateTime? BuiltUtc(Dictionary<string, string> meta) =>
        meta.TryGetValue("built_utc", out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? t.ToUniversalTime() : null;

    // DB の meta の表 (key → value)。DB が無い・読めない・表が無ければ null。読み取り専用で開く
    private static Dictionary<string, string>? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT key, value FROM meta";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0) && !r.IsDBNull(1)) meta[r.GetString(0)] = r.GetString(1);
            }
            return meta;
        }
        catch (SqliteException) { return null; }
    }
}
