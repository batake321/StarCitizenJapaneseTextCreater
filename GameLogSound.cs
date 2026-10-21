using System;
using System.Collections.Generic;
using System.IO;

namespace StarCitizenJapaneseTextCreater;

// ［ゲームログ］の音の種類。Armistice = 休戦ゾーンに進入 (船を操縦中)、Arrival = 到着、
// Voice = ほかの場面の読み上げ (GameLogVoiceScenes。VOICEVOX で読むときだけ。読めなくても音のファイルは鳴らさない)
public enum GameLogSoundKind { Armistice, Arrival, Voice }

// 鳴らす音 1 つ。Scene = 読み上げの場面の鍵 (GameLogVoiceScenes)、Entry = その行
public sealed record GameLogSoundEvent(GameLogSoundKind Kind, string Scene, GameLogEntry Entry);

// ［ゲームログ］の追い読みで新しく足された行から、鳴らす音を決める。WPF にも音の再生にも依存しない (コンソールから試せる)。
// 鳴らすのは、ライブの追い読みで新しく足された行 (起動時の最初の Poll と、ゲームの再起動 (reset) の読み直しの分は除く) のうち、
// 行の TimeUtc が今から LiveWindow 以内のものだけ:
//   (a) 種類「区域」で内容が休戦ゾーンへの進入 (GameLogParser.ArmisticeEnterHeadings) で、船を操縦中か船に乗っている (他人の船への同乗も含む。GameLogEntry.Piloting) → 休戦の音
//       (徒歩 (操縦中でも船に乗ってもいない) の進入は鳴らさない)
//   (b) 種類「到着」→ 到着の音 (ただし直前 RepeatWindow 以内に休戦の音を鳴らしていれば鳴らさない)
// どちらも、着陸場所がジャンプポイント脇の休憩所 RR_JP_… のとき (GameLogEntry.AtJumpPointStop) は鳴らさない。
// 到着の行は、着いた時点では場所の名前 (RR_JP_… か) がまだ分からないことがある (直後の RequestLocationInventory で分かる。
// GameLogEntry.PendingRawPlaceId)。そのときはすぐには判定せず保留し、次の Poll 以降 (Game.log が変わっていなくても) で判定し直す。
// 名前が分かれば上のとおり、行の時刻から ArrivalNameWait たっても分からなければ鳴らす (判定の今は判定し直した時刻)。
// どちらも、直前の同じ音から RepeatWindow 以内は鳴らさない。鳴らさない設定 (enabled = false) の音は鳴らしたことにもしない
// (休戦の音を切っていれば、直後の到着の音は鳴る)
// (c) ほかの読み上げの場面 (GameLogVoiceScenes.SceneOf。sceneEnabled が渡され、その場面を読む設定のときだけ) → Voice。
//     同じ場面の行が SceneWindow 以内に続いたら (前の行の時刻から測る)、2 つ目以降は読まない (採掘は 1 個ごとに行が出るため)
public sealed class GameLogSoundJudge
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);
    // 到着の行で場所の名前を待つ長さ (行の時刻から)
    public static readonly TimeSpan ArrivalNameWait = TimeSpan.FromSeconds(3);
    // ほかの読み上げの場面で、同じ場面が続いたとみなす間隔
    public static readonly TimeSpan SceneWindow = TimeSpan.FromSeconds(10);
    // 格納の行は昇降台が下がった時刻で、船が決まるまで (最大 60 秒) 遅れて足されるので、読む範囲を広げる。
    // ハンガー退出の行も通知の時刻で 10 秒より後に足されるので、同じ範囲にする (ハンガー進入も同じ範囲)
    public static readonly TimeSpan StowLiveWindow = TimeSpan.FromSeconds(75);

    // 基準 (この番号までの行は鳴らさない) が決まったか。最初に Game.log が見つかった Poll で決める
    private bool _armed;
    private long _lastSeq;
    private DateTime? _lastArmistice;
    private DateTime? _lastArrival;
    // 場所の名前を待って保留している到着の行 (古い順)。次の Poll で判定し直す
    private readonly List<GameLogEntry> _pendingArrivals = new();
    // ほかの読み上げの場面 → 直前のその場面の行の時刻
    private readonly Dictionary<string, DateTime> _lastScene = new(StringComparer.Ordinal);

    // 読み上げの記録の出し先 (null なら出さない)。場面に当たる行を読まないと決めたら「[読み上げ] 読まない（<場面>）: <理由>」を 1 行渡す
    public Action<string>? Log { get; set; }

    private void Skip(string scene, string reason) => Log?.Invoke($"[読み上げ] 読まない（{GameLogVoiceScenes.LabelOf(scene)}）: {reason}");

    private static string OldReason(TimeSpan window) => $"時刻が古い (行の時刻から {window.TotalSeconds:0} 秒より後)";
    private static string RepeatReason(string what) => $"直前の{what}から {RepeatWindow.TotalSeconds:0} 秒以内";

    // Poll の結果から鳴らす音を返す (古い順)。found = Game.log があった、reset = 読み直した (それまでの行は捨てた)、
    // rows = 今の Game.log の行 (reset か変わったときだけ渡す。変わっていなければ null)、nowUtc = 今 (UTC)、
    // sceneEnabled = ほかの読み上げの場面を読むか (場面の鍵 → 読むなら true。null ならほかの場面は見ない)
    public List<GameLogSoundEvent> OnPoll(bool found, bool reset, IReadOnlyList<GameLogEntry>? rows, DateTime nowUtc, bool armisticeEnabled, bool arrivalEnabled,
        Func<string, bool>? sceneEnabled = null)
    {
        var result = new List<GameLogSoundEvent>();
        if (!found) return result;
        if (!_armed || reset)
        {
            // 最初の Poll (既存の Game.log を先頭から全部読む) と読み直しの分は鳴らさない: 基準にするだけ
            var firstRead = !_armed;
            _armed = true;
            _lastSeq = MaxSeq(rows);
            _pendingArrivals.Clear();
            // 読み上げの記録: 場面に当たる行の数を 1 行にまとめて出す
            if (Log != null && rows != null)
            {
                var skipped = rows.Count(e => GameLogVoiceScenes.SceneOf(e) != null);
                Log($"[読み上げ] {(firstRead ? "最初の読み込みの分" : "ゲームの再起動で読み直した分")} {skipped} 行は読みません");
            }
            return result;
        }
        // 保留している到着の行と、新しく足された行 (基準より後の通し番号) を古い順に見る
        var targets = new List<GameLogEntry>(_pendingArrivals);
        _pendingArrivals.Clear();
        if (rows != null)
            foreach (var e in rows)
                if (e.Seq > _lastSeq) targets.Add(e);
        targets.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        foreach (var e in targets)
        {
            _lastSeq = Math.Max(_lastSeq, e.Seq);
            // 到着の行で場所の名前がまだ分からない: ArrivalNameWait まで保留する
            if (e.Kind == "到着" && e.PendingRawPlaceId != null && nowUtc - e.TimeUtc < ArrivalNameWait)
            {
                _pendingArrivals.Add(e);
                continue;
            }
            if (Judge(e, nowUtc, armisticeEnabled, arrivalEnabled) is { } kind)
                result.Add(new GameLogSoundEvent(kind, kind == GameLogSoundKind.Armistice ? GameLogVoiceScenes.Armistice : GameLogVoiceScenes.Arrival, e));
            else if (sceneEnabled != null && JudgeScene(e, nowUtc, sceneEnabled) is { } scene)
                result.Add(new GameLogSoundEvent(GameLogSoundKind.Voice, scene, e));
            else if (sceneEnabled == null && GameLogVoiceScenes.SceneOf(e) is { } other && other != GameLogVoiceScenes.Armistice && other != GameLogVoiceScenes.Arrival)
                Skip(other, "VOICEVOX で読み上げない設定");
        }
        return result;
    }

    // 1 行の判定 (鳴らさなければ null)。鳴らすと決めたら、その音の時刻 (nowUtc) を覚える。到着の行の保留は OnPoll が行う
    public GameLogSoundKind? Judge(GameLogEntry e, DateTime nowUtc, bool armisticeEnabled, bool arrivalEnabled)
    {
        var armistice = e.Kind == "区域" && Array.IndexOf(GameLogParser.ArmisticeEnterHeadings, e.Content) >= 0;
        var arrival = e.Kind == "到着";
        if ((nowUtc - e.TimeUtc).Duration() > LiveWindow)
        {
            if (armistice) Skip(GameLogVoiceScenes.Armistice, OldReason(LiveWindow));
            else if (arrival) Skip(GameLogVoiceScenes.Arrival, OldReason(LiveWindow));
            return null;
        }
        if (armistice)
        {
            // 徒歩 (操縦中でも船に乗ってもいない) の進入と、ジャンプポイント脇の休憩所 (RR_JP_…) にいるあいだの進入は鳴らさない
            string? reason = !armisticeEnabled ? "休戦ゾーン進入を鳴らさない設定"
                : !e.Piloting ? "船を操縦中でも船に乗ってもいない (徒歩)"
                : e.AtJumpPointStop ? "ジャンプポイント脇の休憩所 (RR_JP_…) にいる"
                : Within(_lastArmistice, nowUtc) ? RepeatReason("休戦ゾーン進入") : null;
            if (reason != null)
            {
                Skip(GameLogVoiceScenes.Armistice, reason);
                return null;
            }
            _lastArmistice = nowUtc;
            return GameLogSoundKind.Armistice;
        }
        if (arrival)
        {
            // ジャンプポイント脇の休憩所 (RR_JP_…) への到着は鳴らさない
            string? reason = !arrivalEnabled ? "到着を鳴らさない設定"
                : e.AtJumpPointStop ? "ジャンプポイント脇の休憩所 (RR_JP_…) への到着"
                : Within(_lastArmistice, nowUtc) ? RepeatReason("休戦ゾーン進入")
                : Within(_lastArrival, nowUtc) ? RepeatReason("到着") : null;
            if (reason != null)
            {
                Skip(GameLogVoiceScenes.Arrival, reason);
                return null;
            }
            _lastArrival = nowUtc;
            return GameLogSoundKind.Arrival;
        }
        return null;
    }

    // ほかの読み上げの場面の判定 (読まなければ null)。休戦ゾーン進入と到着は Judge が決めるのでここでは見ない
    private string? JudgeScene(GameLogEntry e, DateTime nowUtc, Func<string, bool> sceneEnabled)
    {
        if (GameLogVoiceScenes.SceneOf(e) is not { } scene || scene == GameLogVoiceScenes.Armistice || scene == GameLogVoiceScenes.Arrival) return null;
        // 格納・ハンガー退出の行は遅れて足されるので StowLiveWindow まで読む (ハンガー進入も同じ範囲)
        var live = scene == "格納" || scene == GameLogVoiceScenes.HangarEnter || scene == GameLogVoiceScenes.HangarExit ? StowLiveWindow : LiveWindow;
        if ((nowUtc - e.TimeUtc).Duration() > live)
        {
            Skip(scene, OldReason(live));
            return null;
        }
        if (!sceneEnabled(scene))
        {
            Skip(scene, "場面を読まない設定");
            return null;
        }
        // BP受信は 1 行ごとに別の BP なので、続いても全部読む (契約の報酬で 2 つ続けて届くことがある)
        var continued = scene != "BP受信" && _lastScene.TryGetValue(scene, out var prev) && (e.TimeUtc - prev).Duration() <= SceneWindow;
        _lastScene[scene] = e.TimeUtc;
        if (continued) Skip(scene, $"直前の同じ場面の行から {SceneWindow.TotalSeconds:0} 秒以内");
        return continued ? null : scene;
    }

    private static bool Within(DateTime? last, DateTime nowUtc) => last is { } t && nowUtc - t <= RepeatWindow;

    private static long MaxSeq(IReadOnlyList<GameLogEntry>? rows)
    {
        long max = 0;
        if (rows == null) return max;
        foreach (var e in rows)
            if (e.Seq > max) max = e.Seq;
        return max;
    }
}

// 音のファイル (wav) を鳴らす。System.Media.SoundPlayer で非同期に鳴らし、鳴っている最中に次が来たら前のを止めて新しいのを鳴らす。
// どのスレッドからでも呼べる。ファイルが無い・鳴らせないときは例外にせず false を返す (理由は error)
public static class GameLogSoundPlayer
{
    public const string DefaultArmisticeFile = "armistice_zone.wav";
    public const string DefaultArrivalFile = "arrival.wav";

    private static readonly object Lock = new();
    private static System.Media.SoundPlayer? _current;

    // 設定のパス (空なら同梱の sounds\<既定のファイル名>)
    public static string ResolvePath(string? configured, string defaultFile) =>
        string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "sounds", defaultFile) : configured.Trim();

    public static bool Play(string path, out string? error)
    {
        error = null;
        lock (Lock)
        {
            try
            {
                if (!File.Exists(path))
                {
                    error = $"音のファイルがありません: {path}";
                    return false;
                }
                _current?.Stop();
                _current?.Dispose();
                _current = null;
                var player = new System.Media.SoundPlayer(path);
                player.Load();   // 読めない (wav でない等) ときはここで例外になる
                player.Play();
                _current = player;
                return true;
            }
            catch (Exception ex)
            {
                error = $"音を鳴らせませんでした: {path} ({ex.Message})";
                return false;
            }
        }
    }
}
