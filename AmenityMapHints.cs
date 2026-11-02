using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace StarCitizenJapaneseTextCreater;

// 取り込み済みのステーションの地図 (StationMapExports の JSON) から、星図の施設の一覧に無い施設を推し量る手がかり
// (2026-11-02、利用者の決定「B で(推測)をつける」。［詳細］の【基本】の「あり（推測）」と、［マップ］の施設の検索で使う):
//   精製所 (Refinery)          ← terminals に kind "refinery" (精錬の端末。EntityLinks の先が EntityClass Refinery。取り込みが構造で判定したもの) がある
//   積載ドック (Loading Dock)   ← exterior.freight (外の貨物エレベーターの台。LoadingPlatformManager) が 1 つ以上ある
//   (乗り物サービスは地図のデータに手がかりが無いので使わない)
// 地図と場所の結び付けは［詳細］の地図のバーと同じ: 場所の record 名の短い名前 (StationMapExports.ShortName) の JSON (StationMapExports.JsonPath)。
// 頭の印 (app) が無い・record がファイル名と違う・形 (format) が今の版 (StationMapExports.Format) でない JSON は「地図のデータ無し」(null)。
// 大きな JSON (Levski は約 3.9 MB) を DOM にせず、Utf8JsonReader で頭・terminals の kind・exterior.freight の数だけを読み、ほかの値は読み飛ばす。
// 結果はファイルの大きさと更新日時ごとに覚える (同じファイルは 2 回目から読まない)
internal static class AmenityMapHints
{
    public sealed record Hints(bool Refinery, bool ExteriorFreight);

    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, Hints? Hints)> s_cache = new(StringComparer.OrdinalIgnoreCase);

    // 星図の場所の record 名 (StarMapObject.…) の地図の手がかり。地図が無い・読めない・今の形でなければ null
    public static Hints? Of(string recordName)
    {
        try
        {
            var shortName = StationMapExports.ShortName(recordName);
            return OfFile(StationMapExports.JsonPath(shortName), shortName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // 置き場所の地図のすべて (record 名 → 手がかり。地図のデータ無しのものは入れない)。［マップ］の施設の検索で使う (裏のスレッドで呼ぶ)
    public static Dictionary<string, Hints> All()
    {
        var result = new Dictionary<string, Hints>(StringComparer.Ordinal);
        try
        {
            var dir = new DirectoryInfo(StationMapExports.Folder);
            if (!dir.Exists) return result;
            foreach (var f in dir.GetFiles("*.json", SearchOption.TopDirectoryOnly))
            {
                var shortName = Path.GetFileNameWithoutExtension(f.Name);
                if (OfFile(f.FullName, shortName) is { } h) result["StarMapObject." + shortName] = h;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return result;
    }

    // JSON 1 つの手がかり (覚えたものがあればそれ)。shortName は record の短い名前と同じでなければならない (StationMapExports の頭の判定と同じ)
    internal static Hints? OfFile(string path, string shortName)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return null;
        var key = fi.FullName;
        if (s_cache.TryGetValue(key, out var c) && c.Length == fi.Length && c.Ticks == fi.LastWriteTimeUtc.Ticks) return c.Hints;
        var sw = Stopwatch.StartNew();
        Hints? h;
        try { h = Read(File.ReadAllBytes(fi.FullName), shortName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        s_cache[key] = (fi.Length, fi.LastWriteTimeUtc.Ticks, h);
        if (sw.ElapsedMilliseconds >= 200)
            App.AppendStartupLog(App.Config.WorkingDirectory, $"[施設の推測] 地図 {shortName} を読むのに {sw.ElapsedMilliseconds} ms かかりました");
        return h;
    }

    // JSON の中身から手がかりを読む。頭の印・record・形が合わなければ null
    internal static Hints? Read(byte[] json, string shortName)
    {
        try
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            string app = "", record = "";
            int format = 0;
            bool refinery = false, freight = false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var name = r.GetString();
                if (!r.Read()) return null;
                switch (name)
                {
                    case "app": app = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "record": record = r.TokenType == JsonTokenType.String ? r.GetString() ?? "" : ""; break;
                    case "format": if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var fv)) format = fv; break;
                    case "terminals":
                        if (r.TokenType != JsonTokenType.StartArray) { r.Skip(); break; }
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); continue; }
                            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                            {
                                var pn = r.GetString();
                                if (!r.Read()) return null;
                                if (pn == "kind" && r.TokenType == JsonTokenType.String && r.ValueTextEquals("refinery")) refinery = true;
                                else r.Skip();
                            }
                        }
                        break;
                    case "exterior":
                        if (r.TokenType != JsonTokenType.StartObject) { r.Skip(); break; }
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            var pn = r.GetString();
                            if (!r.Read()) return null;
                            if (pn == "freight" && r.TokenType == JsonTokenType.StartArray)
                            {
                                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                                {
                                    freight = true;
                                    r.Skip();
                                }
                            }
                            else r.Skip();
                        }
                        break;
                    default: r.Skip(); break;
                }
            }
            if (app != StationMapExports.Mark || format != StationMapExports.Format) return null;
            if (!string.Equals(StationMapExports.ShortName(record), shortName, StringComparison.OrdinalIgnoreCase)) return null;
            return new Hints(refinery, freight);
        }
        catch (JsonException) { return null; }
    }
}
