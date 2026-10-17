using System;
using System.Collections.Generic;
using System.IO;

namespace StarCitizenJapaneseTextCreater;

// ［ゲームログ］の音の種類。Armistice = 休戦ゾーンに進入 (船を操縦中)、ArmisticeOnFoot = 休戦ゾーンに進入 (徒歩)、Arrival = 到着
public enum GameLogSoundKind { Armistice, ArmisticeOnFoot, Arrival }

// ［ゲームログ］の追い読みで新しく足された行から、鳴らす音を決める。WPF にも音の再生にも依存しない (コンソールから試せる)。
// 鳴らすのは、ライブの追い読みで新しく足された行 (起動時の最初の Poll と、ゲームの再起動 (reset) の読み直しの分は除く) のうち、
// 行の TimeUtc が今から LiveWindow 以内のものだけ:
//   (a) 種類「区域」で内容が休戦ゾーンへの進入 (GameLogParser.ArmisticeEnterHeadings) → 休戦の音
//       (行の Piloting (船を操縦中) なら Armistice、徒歩なら ArmisticeOnFoot。入切と RepeatWindow の抑止は 2 つで共通)
//   (b) 種類「到着」→ 到着の音 (ただし直前 RepeatWindow 以内に休戦の音 (どちらか) を鳴らしていれば鳴らさない)
// どちらも、直前の同じ音から RepeatWindow 以内は鳴らさない。鳴らさない設定 (enabled = false) の音は鳴らしたことにもしない
// (休戦の音を切っていれば、直後の到着の音は鳴る)
public sealed class GameLogSoundJudge
{
    public static readonly TimeSpan LiveWindow = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(60);

    // 基準 (この番号までの行は鳴らさない) が決まったか。最初に Game.log が見つかった Poll で決める
    private bool _armed;
    private long _lastSeq;
    private DateTime? _lastArmistice;
    private DateTime? _lastArrival;

    // Poll の結果から鳴らす音を返す (古い順)。found = Game.log があった、reset = 読み直した (それまでの行は捨てた)、
    // rows = 今の Game.log の行 (reset か変わったときだけ渡す。変わっていなければ null)、nowUtc = 今 (UTC)
    public List<GameLogSoundKind> OnPoll(bool found, bool reset, IReadOnlyList<GameLogEntry>? rows, DateTime nowUtc, bool armisticeEnabled, bool arrivalEnabled)
    {
        var result = new List<GameLogSoundKind>();
        if (!found) return result;
        if (!_armed || reset)
        {
            // 最初の Poll (既存の Game.log を先頭から全部読む) と読み直しの分は鳴らさない: 基準にするだけ
            _armed = true;
            _lastSeq = MaxSeq(rows);
            return result;
        }
        if (rows == null) return result;
        // 新しく足された行 (基準より後の通し番号) を古い順に見る
        var fresh = new List<GameLogEntry>();
        foreach (var e in rows)
            if (e.Seq > _lastSeq) fresh.Add(e);
        fresh.Sort((a, b) => a.Seq.CompareTo(b.Seq));
        foreach (var e in fresh)
        {
            _lastSeq = Math.Max(_lastSeq, e.Seq);
            if (Judge(e, nowUtc, armisticeEnabled, arrivalEnabled) is { } kind) result.Add(kind);
        }
        return result;
    }

    // 1 行の判定 (鳴らさなければ null)。鳴らすと決めたら、その音の時刻 (nowUtc) を覚える
    public GameLogSoundKind? Judge(GameLogEntry e, DateTime nowUtc, bool armisticeEnabled, bool arrivalEnabled)
    {
        if ((nowUtc - e.TimeUtc).Duration() > LiveWindow) return null;
        if (e.Kind == "区域" && Array.IndexOf(GameLogParser.ArmisticeEnterHeadings, e.Content) >= 0)
        {
            if (!armisticeEnabled || Within(_lastArmistice, nowUtc)) return null;
            _lastArmistice = nowUtc;
            return e.Piloting ? GameLogSoundKind.Armistice : GameLogSoundKind.ArmisticeOnFoot;
        }
        if (e.Kind == "到着")
        {
            if (!arrivalEnabled || Within(_lastArmistice, nowUtc) || Within(_lastArrival, nowUtc)) return null;
            _lastArrival = nowUtc;
            return GameLogSoundKind.Arrival;
        }
        return null;
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
    public const string DefaultArmisticeOnFootFile = "armistice_zone_short.wav";
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
