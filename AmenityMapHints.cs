using System.IO;

namespace StarCitizenJapaneseTextCreater;

// 場所の施設の手がかり (［詳細］の施設のアイコン・星系図の施設のアイコンと施設での検索)。
// (2026-11-04、利用者の指示「2D 地図は消して」) 前は取り込んだ 2D の地図の JSON から読んでいた。今は 3D (新しい方式) の区画の置き方の一覧の DB
// (view3d_layout.db。場所の中身は Data.p4k の区画の .soc の Entity・metadata から作ったもの) から読む:
//   精製所 (Refinery)          ← 場所の端末に kind "refinery" (精錬の端末) がある
//   積載ドック (Exterior Freight) ← 外の貨物エレベーターの台 (exterior の freight) が 1 つ以上ある
// DB が無い・まだ作っていない場所は「手がかり無し」(null)。DB の版の印は見ない (パッチの直後で作り直す前でも、前の版の手がかりを使う)
internal static class AmenityMapHints
{
    public sealed record Hints(bool Refinery, bool ExteriorFreight);

    private static readonly object Lock = new();
    private static (DateTime Stamp, long Length, Dictionary<string, Hints> Map)? _cache;

    // 星図の場所の record 名 (StarMapObject.…) の手がかり。DB に無ければ null
    public static Hints? Of(string recordName) => All().TryGetValue(recordName, out var h) ? h : null;

    // DB にある場所の全部 (record 名 → 手がかり)。DB のファイルの更新日時と大きさが同じ間は、読んだものを使い回す (裏のスレッドで呼んでよい)
    public static Dictionary<string, Hints> All()
    {
        var path = StationView3DLayout.DbPath;
        DateTime stamp; long len;
        try { var fi = new FileInfo(path); if (!fi.Exists) return new(StringComparer.Ordinal); stamp = fi.LastWriteTimeUtc; len = fi.Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(StringComparer.Ordinal); }
        lock (Lock) if (_cache is { } c && c.Stamp == stamp && c.Length == len) return c.Map;
        var map = StationView3D.LayoutDbSummary(path).ToDictionary(kv => kv.Key, kv => new Hints(kv.Value.Refinery, kv.Value.Freight > 0), StringComparer.Ordinal);
        lock (Lock) _cache = (stamp, len, map);
        return map;
    }
}
