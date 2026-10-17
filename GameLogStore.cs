using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarCitizenJapaneseTextCreater;

// trade_cache.db に残した［ゲームログ］の 1 行 (game_log_entries)。Machine は保存した PC (Environment.MachineName)。
// Entry は payload_json から復元した行 (Source は Machine にしてある: 表の「ログ」の欄でどの PC の行か分かるように)
public sealed record StoredGameLogEntry(string Machine, GameLogEntry Entry);

// ［ゲームログ］の行を trade_cache.db (game_log_entries) に残し、別の PC でも見られるようにするための、WPF に依存しない部分。
//   鍵:   (time_utc, kind, content)。time_utc は行頭の時刻の文字 (GameLogParser.LogTimeText と同じ "yyyy-MM-ddTHH:mm:ss.fff"、UTC)。
//         同じ行が Game.log と logbackups の両方に出ても同じ鍵になる
//   確定: 内容が後から書き換わる行 (船名待ち・場所待ち・ミッション名待ち) は、書き換えが済むまで保存しない (IsSettled)。
//         今の Game.log の行は、続きの行で書き換わるもの (購入の結果で「購入失敗」になる、取引の失敗、衝突のまとめ、採掘の所持数の添え書き、
//         同じ場所に 5 分 (GameLogParser.ReentryWindow) 以内に戻ったときの出発の取り消し) があるので、行の時刻から SettleWindow 過ぎるまで保存しない。
//         過去のログの行はファイルを読み終えているので、名前待ちで無ければすぐ保存する
//   中身: payload_json は GameLogEntry をそのまま System.Text.Json にしたもの (ImportItems / Trade / Mining / Purchase / ShipClass / ShipName / Seq /
//         BP受信の行の Blueprint (品名・同定した BP の uuid とできる品の record) …)。
//         復元した行で「所持」のボタン (ClassifyImports) とダブルクリックと利益の添え書きが手元の行と同じように効く。
//         売却の行に表示時に添える利益 (GameLogPanel.ContentText) は content に入れない (Content そのもの)
//   表示: 手元の解析の行 ∪ DB の行のうち自分以外の PC の行 (Merge)。同じ鍵は手元の行を優先。手元の Game.log が無い PC では DB の自分の行も出す
public static class GameLogStore
{
    public const string Table = "game_log_entries";

    // 今の Game.log の行を「書き換えが済んだ」と見るまでの時間。いちばん長い書き換えの窓 (出発の取り消し: GameLogParser.ReentryWindow = 5 分) より長く
    public static readonly TimeSpan SettleWindow = TimeSpan.FromMinutes(6);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string TimeText(DateTime timeUtc) => timeUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    // 行の鍵 (主キーの組を U+001F でつないだ文字。メモリで「保存した」「同じ鍵」を見分けるのに使う)
    public static string Key(GameLogEntry e) => string.Join('\u001f', TimeText(e.TimeUtc), e.Kind, e.Content);

    // 保存してよい (書き換えが済んだ) 行か。current は今の Game.log の行 (過去のログの行は false)
    public static bool IsSettled(GameLogEntry e, bool current, DateTime nowUtc)
    {
        if (e.PendingMissionId != null || e.PendingPlaceId != null || e.PendingVehicleId != null) return false;
        return !current || nowUtc - e.TimeUtc >= SettleWindow;
    }

    public static string ToJson(GameLogEntry e) => JsonSerializer.Serialize(e, JsonOptions);

    // game_log_entries に入れる 1 行の写し (列の値と payload_json まで作ったもの)。UI のスレッドで GameLogEntry から作り (ToRow)、裏のスレッドで DB に入れる
    public sealed record Row(string TimeText, string Kind, string Content, double? Amount, string? ShipClass, string? ShipName, string PayloadJson);

    public static Row ToRow(GameLogEntry e) => new(TimeText(e.TimeUtc), e.Kind, e.Content, e.Amount, e.ShipClass, e.ShipName, ToJson(e));

    // payload_json → 行。Source は保存した PC の名前にする。読めなければ null
    public static GameLogEntry? FromJson(string json, string machine)
    {
        try
        {
            var e = JsonSerializer.Deserialize<GameLogEntry>(json, JsonOptions);
            if (e == null) return null;
            e.Source = machine;
            return e;
        }
        catch (JsonException) { return null; }
    }

    // 表に出す行: 手元の行 (local) と DB の行 (stored) のうち machine が自分 (ownMachine) 以外のもの (includeOwn なら自分のものも) を合わせ、
    // 同じ鍵は手元の行を優先し、新しい順 (同じ時刻は Seq の逆順) に並べる。DB の行が無ければ local をそのまま (並べ替え済みのこと) 返す
    public static List<GameLogEntry> Merge(List<GameLogEntry> local, IReadOnlyList<StoredGameLogEntry> stored, string ownMachine, bool includeOwn)
    {
        var picked = new List<GameLogEntry>();
        HashSet<string>? localKeys = null;
        foreach (var s in stored)
        {
            if (!includeOwn && string.Equals(s.Machine, ownMachine, StringComparison.OrdinalIgnoreCase)) continue;
            localKeys ??= new HashSet<string>(local.Select(Key), StringComparer.Ordinal);
            if (localKeys.Contains(Key(s.Entry))) continue;
            picked.Add(s.Entry);
        }
        if (picked.Count == 0) return local;
        return local.Concat(picked).OrderByDescending(e => e.TimeUtc).ThenByDescending(e => e.Seq).ToList();
    }
}
