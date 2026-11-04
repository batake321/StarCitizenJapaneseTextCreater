namespace StarCitizenJapaneseTextCreater;

// アプリの中で使い回す DataCore (Game2.dcb) (2026-11-04、利用者の決定「starbreaker の処理を自前で持つ」)。
// starbreaker は dcb query・dcb extract のたびに Game2.dcb を読み直していた (1 回 約 2 秒) ので、1 回読んだら使い回す。
// 読み元は、取り出した Game2.dcb のファイル (dcbFile。構築の間) か Data.p4k の中の Data\Game2.dcb。
// 使わないまま IdleSeconds 経ったら捨てる (メモリ 約 340 MB を返す)
internal static class DataCoreShared
{
    public const int IdleSeconds = 120;
    private static readonly object Lock = new();
    private static string? _key;
    private static DataCoreDb? _db;
    private static long _lastUse;
    private static Timer? _timer;

    public static DataCoreDb Get(string? dcbFile, string p4kPath)
    {
        string key;
        if (dcbFile != null) { var fi = new FileInfo(dcbFile); key = $"dcb|{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; }
        else { var fi = new FileInfo(p4kPath); key = $"p4k|{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}"; }
        lock (Lock)
        {
            _lastUse = Environment.TickCount64;
            if (_db != null && _key == key) return _db;
            _db = null; _key = null;
            var db = dcbFile != null ? DataCoreDb.Load(File.ReadAllBytes(dcbFile)) : DataCoreDb.FromP4k(P4kArchive.Shared(p4kPath));
            _db = db; _key = key;
            _timer ??= new Timer(_ =>
            {
                lock (Lock) if (_db != null && Environment.TickCount64 - _lastUse > IdleSeconds * 1000L) { _db = null; _key = null; }
            }, null, 15_000, 15_000);
            return db;
        }
    }

    // dcb query の置き換え: 標準出力と同じ文字 (行は「\n」)。当てはまるレコードが無ければ空、そのほかの失敗は例外 (InvalidOperationException)
    public static string Query(string? dcbFile, string p4kPath, string path, string? filter)
    {
        try { return DataCoreCli.Query(Get(dcbFile, p4kPath), path, filter); }
        catch (DataCoreCli.NoRecordsException) { return ""; }
        catch (Exception e) when (e is DataCoreDb.QueryException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(e.Message, e);
        }
    }
}
