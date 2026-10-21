using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace StarCitizenJapaneseTextCreater;

// ［ゲームログ］の読み上げの場面 1 つの設定 (Config.GameLogVoiceItems の 1 件)。
// Kind = 場面の鍵 (GameLogVoiceScenes.All の Key)、Enabled = 読むか、Text = 読み上げる文 (空なら既定の文)、SpeakerId = 声 (null = 既定の声 Config.GameLogVoiceVoxSpeakerId)。
// 休戦ゾーン進入と到着の Enabled は使わず、Config.ArmisticeSoundEnabled / ArrivalSoundEnabled を使う (今までの入切を引き継ぐため)
public sealed class GameLogVoiceItem
{
    public string Kind { get; set; } = "";
    public bool Enabled { get; set; }
    public string Text { get; set; } = "";
    public int? SpeakerId { get; set; }
}

// 読み上げの場面の定義 (Key, 表の場面の欄, 既定の文, 既定の入切)
public sealed record GameLogVoiceScene(string Key, string Label, string DefaultText, bool DefaultEnabled);

// 読み上げの場面の一覧と、ゲームログの行 → 場面・読み上げる文
public static class GameLogVoiceScenes
{
    // 休戦ゾーン進入 (種類「区域」で GameLogParser.ArmisticeEnterHeadings) と、管轄区域に進入 (種類「区域」で「…管轄区域に進入」) は行の種類と別の鍵にする
    public const string Armistice = "休戦ゾーン進入";
    public const string Arrival = "到着";
    public const string Jurisdiction = "管轄区域に進入";
    // 復活 (種類「復活」。{場所} は到着と同じ規則で埋める: PlaceOf)
    public const string Respawn = "復活";
    // 量子ジャンプの到着 (種類「量子ジャンプ」。表の場面の欄は「量子ジャンプ離脱」。鍵は保存済みの設定のため「量子ジャンプ」のまま)
    public const string QuantumArrived = "量子ジャンプ";
    // ハンガー進入・退出 (種類「ハンガー進入」「ハンガー退出」。{場所} は到着と同じ規則で埋める: PlaceOf。
    // 読む範囲は格納と同じ GameLogSoundJudge.StowLiveWindow (ハンガー退出の行は通知から 10 秒たってから足されるため。ハンガー進入も同じ範囲にしてある))
    public const string HangarEnter = "ハンガー進入";
    public const string HangarExit = "ハンガー退出";
    // 既定の文を変えた場面の前の既定の文。保存された文がこれと同じなら、今の既定の文として扱う (ItemOf)
    private static readonly Dictionary<string, string> OldDefaultTexts = new()
    {
        [QuantumArrived] = "量子ジャンプします。",
        // 「量子ジャンプ開始」の行は目的地を選んだ行 (Player has selected point …) で作る。実際に跳んだ時の行は Game.log に無い
        ["量子ジャンプ開始"] = "量子ジャンプします。",
        ["降りた"] = "操縦席を離れました。",
    };
    // {船} の船が分からないときに {船} に入れる言葉 (場面の鍵 → 言葉)。無い場面は {船} を直後の助詞ごと取り除く
    private static readonly Dictionary<string, string> ShipFallbacks = new()
    {
        ["降りた"] = "船",   // 「から降りました。」にせず「船から降りました。」にする
    };

    public static readonly GameLogVoiceScene[] All =
    [
        new(Armistice, "休戦ゾーン進入（操縦中）", "休戦ゾーンに侵入しました。ハンガーリクエスト可能です。", true),
        new(Arrival, "到着", "{場所}に到着しました。ハンガーリクエスト可能です。", true),
        new("乗船", "乗船（自分の船）", "{船}に乗船しました。", false),
        new("下船", "下船", "{船}から降りました。", false),
        new("降りた", "操縦席を離れた", "{船}から降りました。", false),
        new("操縦", "操縦（操縦席に座った）", "{船}を操縦します。", false),
        new("呼び出し完了", "呼び出し完了", "{船}の準備ができました。", false),
        new("格納", "格納", "{船}を格納しました。", false),
        // 「ハンガーリクエスト完了」の通知 (離陸前と着陸時の両方に同じ文面で出る)
        new("ハンガー要求", "ハンガーリクエスト完了", "ハンガーが許可されました。", false),
        // GameLogParser の「ハンガー進入」「ハンガー退出」(船: 申請の後の管轄区域の通知ですぐ。徒歩: 管轄区域の通知の後 10 秒 Location が変わらなかったとき。
        // 決まりは GameLogParser の _hangarRequestTime の説明)
        new(HangarEnter, "ハンガーに入った", "ハンガーに入りました。", false),
        new(HangarExit, "ハンガーを出た", "ハンガーを出ました。", false),
        new("量子ジャンプ開始", "量子ジャンプの目的地を設定", "量子ジャンプの目的地を設定しました。", false),
        new(QuantumArrived, "量子ジャンプ離脱", "量子ジャンプから離脱します。", false),
        new("契約受注", "契約受注", "{内容}", false),
        new("契約完了", "契約完了", "{内容}", false),
        new("契約失敗", "契約失敗", "{内容}", false),
        new("納品", "納品", "{内容}", false),
        new("目標達成", "目標達成", "{内容}", false),
        new("死亡", "死亡", "死亡しました。", false),
        new(Respawn, "復活", "{場所}で復活しました。", false),
        new("燃料低下", "燃料低下", "燃料が少なくなっています。", false),
        new("購入", "購入", "{内容}", false),
        new("商品購入", "商品購入", "{内容}", false),
        new("商品売却", "商品売却", "{内容}", false),
        new("採掘", "採掘", "{内容}", false),
        new("貨物エレベーター", "貨物エレベーター", "{内容}", false),
        new("保険請求", "保険請求", "{内容}", false),
        new("保険請求失敗", "保険請求失敗", "{内容}", false),
        new("衝突", "衝突", "{内容}", false),
        new("出発", "出発", "{内容}", false),
        new(Jurisdiction, "管轄区域に進入", "{内容}", false),
        new("BP受信", "BP受信", "設計図を受信しました。{内容}", false),
    ];

    public static GameLogVoiceScene? Find(string key)
    {
        foreach (var s in All)
            if (s.Key == key) return s;
        return null;
    }

    // 場面の表示名 (設定の表の場面の欄。場面でなければ鍵のまま)。読み上げの記録に使う
    public static string LabelOf(string key) => Find(key)?.Label ?? key;

    // 行の場面の鍵。読み上げの場面でない行は null
    public static string? SceneOf(GameLogEntry e)
    {
        if (e.Kind == "区域")
        {
            if (Array.IndexOf(GameLogParser.ArmisticeEnterHeadings, e.Content) >= 0) return Armistice;
            if (e.Content.EndsWith("管轄区域に進入", StringComparison.Ordinal)) return Jurisdiction;
            return null;
        }
        return Find(e.Kind) != null ? e.Kind : null;
    }

    // 保存された場面の設定 (無ければ既定)。文が前の既定の文のままなら、文を空 (= 今の既定の文) にした写しを返す
    public static GameLogVoiceItem ItemOf(AppConfig config, string key)
    {
        foreach (var it in config.GameLogVoiceItems)
            if (it.Kind == key)
                return OldDefaultTexts.TryGetValue(it.Kind, out var old) && it.Text == old
                    ? new GameLogVoiceItem { Kind = it.Kind, Enabled = it.Enabled, Text = "", SpeakerId = it.SpeakerId }
                    : it;
        var s = Find(key);
        return new GameLogVoiceItem { Kind = key, Enabled = s?.DefaultEnabled ?? false, Text = "" };
    }

    // 場面を読むか (休戦ゾーン進入と到着は今までの入切 ArmisticeSoundEnabled / ArrivalSoundEnabled)
    public static bool IsEnabled(AppConfig config, string key) => key switch
    {
        Armistice => config.ArmisticeSoundEnabled,
        Arrival => config.ArrivalSoundEnabled,
        _ => ItemOf(config, key).Enabled,
    };

    // 場面の読み上げる文の型 (保存された文。空なら既定の文)
    public static string TemplateOf(AppConfig config, string key)
    {
        var text = ItemOf(config, key).Text;
        return string.IsNullOrWhiteSpace(text) ? Find(key)?.DefaultText ?? "" : text;
    }

    // 場面の声 (場面に声の指定が無ければ既定の声)
    public static int SpeakerOf(AppConfig config, string key) => ItemOf(config, key).SpeakerId ?? config.GameLogVoiceVoxSpeakerId;

    // 置き換えの欄の直後の助詞 (欄を取り除くときに一緒に取り除く。「{船}に」「{船}から」「{場所}に」など)
    private const string Particles = "(から|まで|に|を|の|が|は|へ|で|と)?";
    private static readonly Regex PlaceField = new(@"\{場所\}" + Particles, RegexOptions.Compiled);
    private static readonly Regex ShipField = new(@"\{船\}" + Particles, RegexOptions.Compiled);
    private static readonly Regex AmountField = new(@"\{金額\}" + Particles, RegexOptions.Compiled);

    // 書式のタグ (<EM4>…</EM4> 等)。GameLogParser.StripTags (MarkupTagRegex) と同じ形。読み上げる文に入れる値からだけ外す (表示・DB の内容は変えない)
    private static readonly Regex MarkupTagRegex = new(@"</?[A-Za-z][^>]*>", RegexOptions.Compiled);
    private static string StripTags(string text) => MarkupTagRegex.Replace(text, "").Trim();

    // 英字だけでできた 5 文字以上の全部大文字の語 (ORISON 等)。4 文字以下 (UEE・GEO・MISC・RSI 等の略語) と、
    // 前後が英数字・記号 (- _ . ' &) の語 (ARC-L1、aUEC 等) は対象にしない
    private static readonly Regex UpperWordRegex = new(@"(?<![A-Za-z0-9\-_.'&])[A-Z]{5,}(?![A-Za-z0-9\-_.'&])", RegexOptions.Compiled);
    private static string CapitalizeUpperWords(string text) =>
        UpperWordRegex.Replace(text, m => m.Value[0] + m.Value[1..].ToLowerInvariant());

    // 読み上げる文を作る。{場所} = place (内部名のまま (「_」を含む・「場所 ID n」) か無ければ取り除く)、
    // {船} = 行の ShipName (内部名のまま (「_」を含む) か無ければ取り除く)、{内容} = 行の Content、
    // {金額} = 行の Amount を整数にした「n aUEC」(無ければ取り除く)。取り除くときは直後の助詞も取り除く。
    // 入れる値からは書式のタグを外し、できた文の 5 文字以上の全部大文字の英字の語は頭だけ大文字にする (ORISON → Orison。VOICEVOX が 1 字ずつ読むため。UEE などの 4 文字以下は変えない)
    // shipSpoken: {船} に入れる船の名前を読み上げ用にする (会社名を除く: StripManufacturer)。null ならそのまま。表の表示 (行の ShipName) は変えない。
    // {船} の船が分からない (読めない) とき、場面に ShipFallbacks の言葉があればそれを入れる (「降りた」は「船」)
    public static string BuildText(string template, GameLogEntry e, string? place, Func<string, string>? shipSpoken = null)
    {
        var text = template;
        var placeText = place != null ? StripTags(place) : null;
        var shipText = e.ShipName != null ? StripTags(e.ShipName) : null;
        if (shipSpoken != null && IsReadableShip(shipText)) shipText = shipSpoken(shipText!);
        text = IsReadablePlace(placeText) ? text.Replace("{場所}", placeText, StringComparison.Ordinal) : PlaceField.Replace(text, "");
        text = IsReadableShip(shipText) ? text.Replace("{船}", shipText, StringComparison.Ordinal)
            : ShipFallbacks.TryGetValue(e.Kind, out var fallback) ? text.Replace("{船}", fallback, StringComparison.Ordinal)
            : ShipField.Replace(text, "");
        text = text.Replace("{内容}", StripTags(e.Content), StringComparison.Ordinal);
        text = e.Amount is double a
            ? text.Replace("{金額}", Math.Round(a).ToString("0", CultureInfo.InvariantCulture) + " aUEC", StringComparison.Ordinal)
            : AmountField.Replace(text, "");
        return CapitalizeUpperWords(text.Trim());
    }

    // 場所の表示名が読める名前か (内部名のまま (「_」を含む) や「場所 ID n」は読まない)
    public static bool IsReadablePlace(string? place) =>
        place is { Length: > 0 } p && !p.Contains('_') && !p.StartsWith("場所 ID", StringComparison.Ordinal);

    // 船の名前が読める名前か (内部名のまま (「_」を含む) は読まない)
    public static bool IsReadableShip(string? ship) => ship is { Length: > 0 } s && !s.Contains('_');

    // 船の名前の頭の会社名を除く (「Drake Cutlass Black」→「Cutlass Black」)。prefixes = 会社名の候補 (ゲームデータの船のメーカー名と、その短い名前)。
    // 名前が「候補 + 空白」で始まり (大文字小文字は区別しない)、残りが空でなければ残りを返す。長い候補から見る。一致しなければそのまま
    public static string StripManufacturer(string ship, IEnumerable<string> prefixes)
    {
        foreach (var p in prefixes.Where(p => !string.IsNullOrWhiteSpace(p)).OrderByDescending(p => p.Length))
        {
            if (ship.Length <= p.Length + 1 || !ship.StartsWith(p, StringComparison.OrdinalIgnoreCase) || ship[p.Length] != ' ') continue;
            var rest = ship[(p.Length + 1)..].Trim();
            if (rest.Length > 0) return rest;
        }
        return ship;
    }

    // 読み上げる文の {場所} に入れる場所 (到着の音と、復活・ハンガー進入・ハンガー退出の場面は ArrivalPlace。ほかは null)
    public static string? PlaceOf(GameLogSoundEvent ev, GameLogPlaceNames? sharedPlaceNames) =>
        ev.Kind == GameLogSoundKind.Arrival || ev.Scene == Respawn || ev.Scene == HangarEnter || ev.Scene == HangarExit
            ? ArrivalPlace(ev.Entry, sharedPlaceNames) : null;

    // 到着の行の場所の表示名。名前待ち (PendingPlaceId) なら共有の場所名から引き (無ければ null)、名前が入っていれば内容から船の部分 (" / 船: …") を除いたもの
    public static string? ArrivalPlace(GameLogEntry e, GameLogPlaceNames? sharedPlaceNames)
    {
        if (e.PendingPlaceId is { } id)
            return sharedPlaceNames != null && sharedPlaceNames.TryGet(id, out var name) ? name : null;
        var cut = e.Content.IndexOf(" / 船: ", StringComparison.Ordinal);
        return cut >= 0 ? e.Content[..cut] : e.Content;
    }
}

// VOICEVOX の話者のスタイル 1 つ (GET /speakers の styles の 1 件)。Label は「キャラクター名（スタイル名）」
public sealed record GameLogVoiceSpeaker(int Id, string Label);

// VOICEVOX で wav を作る (POST /audio_query → POST /synthesis)。作った wav は URL・話者・速度・文を鍵にメモリに覚えて使い回す
public static class GameLogVoiceVox
{
    // 1 回の読み上げ (audio_query と synthesis の合計) の待ち時間
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    // 覚えておく wav の数 (超えたら古いものから捨てる)
    private const int CacheMax = 100;

    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private static readonly object CacheLock = new();
    private static readonly Dictionary<(string Url, int Speaker, double Speed, string Text), byte[]> Cache = new();
    private static readonly LinkedList<(string Url, int Speaker, double Speed, string Text)> CacheOrder = new();

    public static string NormalizeUrl(string url) => url.Trim().TrimEnd('/');

    // 覚えた wav を捨てる (設定を保存したとき)
    public static void ClearCache()
    {
        lock (CacheLock)
        {
            Cache.Clear();
            CacheOrder.Clear();
        }
    }

    // wav を返す (覚えていればそれ、無ければ VOICEVOX で作る)。失敗は例外 (理由は Reason で文にする)
    // speedScale: 読み上げ速度 (audio_query の speedScale に入れる)。null なら設定 (App.Config.VoiceVoxSpeedScale)
    public static async Task<byte[]> GetWavAsync(string url, int speaker, string text, CancellationToken ct, double? speedScale = null)
    {
        url = NormalizeUrl(url);
        var speed = speedScale ?? App.Config.VoiceVoxSpeedScale;
        var key = (url, speaker, speed, text);
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit;
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var spk = speaker.ToString(CultureInfo.InvariantCulture);
        using var queryRes = await Http.PostAsync($"{url}/audio_query?text={Uri.EscapeDataString(text)}&speaker={spk}", null, cts.Token);
        if (!queryRes.IsSuccessStatusCode) throw new VoiceVoxException($"audio_query が HTTP {(int)queryRes.StatusCode} を返しました");
        var query = await queryRes.Content.ReadAsStringAsync(cts.Token);
        // 速度だけ書き換える (ほかの値は audio_query の応答のまま)
        if (System.Text.Json.Nodes.JsonNode.Parse(query) is not System.Text.Json.Nodes.JsonObject queryNode)
            throw new VoiceVoxException("audio_query の応答の形が違います");
        queryNode["speedScale"] = speed;
        query = queryNode.ToJsonString();
        using var body = new StringContent(query, Encoding.UTF8, "application/json");
        using var synthRes = await Http.PostAsync($"{url}/synthesis?speaker={spk}", body, cts.Token);
        if (!synthRes.IsSuccessStatusCode) throw new VoiceVoxException($"synthesis が HTTP {(int)synthRes.StatusCode} を返しました");
        var wav = await synthRes.Content.ReadAsByteArrayAsync(cts.Token);
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') throw new VoiceVoxException("synthesis の応答が wav ではありません");
        lock (CacheLock)
        {
            if (Cache.TryAdd(key, wav))
            {
                CacheOrder.AddLast(key);
                while (CacheOrder.Count > CacheMax)
                {
                    Cache.Remove(CacheOrder.First!.Value);
                    CacheOrder.RemoveFirst();
                }
            }
        }
        return wav;
    }

    // 話者の一覧 (GET /speakers)。スタイルの type が "talk" (または type の無い古い版) のものだけ (読み上げ (audio_query) に使えるもの)
    public static async Task<List<GameLogVoiceSpeaker>> GetSpeakersAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var res = await Http.GetAsync($"{NormalizeUrl(url)}/speakers", cts.Token);
        if (!res.IsSuccessStatusCode) throw new VoiceVoxException($"speakers が HTTP {(int)res.StatusCode} を返しました");
        var json = await res.Content.ReadAsStringAsync(cts.Token);
        var list = new List<GameLogVoiceSpeaker>();
        using var doc = JsonDocument.Parse(json);
        foreach (var sp in doc.RootElement.EnumerateArray())
        {
            var name = sp.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!sp.TryGetProperty("styles", out var styles) || styles.ValueKind != JsonValueKind.Array) continue;
            foreach (var st in styles.EnumerateArray())
            {
                if (!st.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                if (st.TryGetProperty("type", out var type) && type.GetString() is { } t && t != "talk") continue;
                var style = st.TryGetProperty("name", out var sn) ? sn.GetString() ?? "" : "";
                list.Add(new GameLogVoiceSpeaker(id, style.Length > 0 ? $"{name}（{style}）" : name));
            }
        }
        return list;
    }

    // 失敗の理由の文 (接続できない・HTTP エラー・タイムアウト)
    public static string Reason(Exception ex) => ex switch
    {
        VoiceVoxException v => v.Message,
        OperationCanceledException => $"{Timeout.TotalSeconds:0} 秒以内に応答がありませんでした",
        HttpRequestException h => $"接続できません ({h.Message})",
        JsonException => "応答を読めませんでした",
        _ => ex.Message,
    };

    private sealed class VoiceVoxException(string message) : Exception(message);
}

// 読み上げの順番待ち。裏のスレッドで 1 つずつ VOICEVOX の wav を作って最後まで鳴らす (前の声を途中で切らない)。
// 待ちが MaxWaiting を超えたら古いものから捨てる。VOICEVOX で作れなかったときは、FallbackPath (休戦ゾーン進入・到着の音のファイル) があればそれを鳴らす。
// 失敗の理由は Log に渡す (直前と同じ理由なら渡さない)
public static class GameLogVoicePlayer
{
    public const int MaxWaiting = 3;

    // Scene = 場面の表示名 (読み上げの記録「[読み上げ] 読まない（場面）: 理由」に使う。空なら記録に場面を出さない)
    private sealed record Request(string Url, int Speaker, string Text, string? FallbackPath, string Scene);

    private static readonly object Lock = new();
    private static readonly LinkedList<Request> Queue = new();
    private static Thread? _worker;
    private static string? _lastError;

    // 失敗の知らせ (どのスレッドからも呼ばれる。呼ぶ側で UI のスレッドへ回す)
    public static Action<string>? Log { get; set; }

    // scene = 場面の表示名 (読み上げの記録用)
    public static void Enqueue(string url, int speaker, string text, string? fallbackPath, string scene = "")
    {
        lock (Lock)
        {
            Queue.AddLast(new Request(url, speaker, text, fallbackPath, scene));
            while (Queue.Count > MaxWaiting)
            {
                var dropped = Queue.First!.Value;
                Queue.RemoveFirst();
                Report($"[読み上げ] 読まない（{dropped.Scene}）: 読み上げの順番待ちが {MaxWaiting} 件を超えたので古いものを捨てた");
            }
            if (_worker == null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "GameLogVoice" };
                _worker.Start();
            }
            Monitor.Pulse(Lock);
        }
    }

    private static void Run()
    {
        while (true)
        {
            Request r;
            lock (Lock)
            {
                while (Queue.Count == 0) Monitor.Wait(Lock);
                r = Queue.First!.Value;
                Queue.RemoveFirst();
            }
            try { Play(r); }
            catch (Exception ex) { Report($"[ゲームログ] 読み上げで例外が出ました: {ex.Message}"); }
        }
    }

    private static void Play(Request r)
    {
        byte[]? wav = null;
        string? error = null;
        if (r.Text.Length > 0)
        {
            try { wav = GameLogVoiceVox.GetWavAsync(r.Url, r.Speaker, r.Text, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { error = GameLogVoiceVox.Reason(ex); }
        }
        else error = "読み上げる文が空です";
        // 読み上げの記録: 読めなかったら毎回 1 行 (下の失敗の知らせは直前と同じ理由なら出さないので別に出す)
        if (wav == null)
            Report($"[読み上げ] 読まない（{r.Scene}）: VOICEVOX で読み上げられませんでした: {error}" + (r.FallbackPath != null ? " (音のファイルを鳴らします)" : ""));
        if (wav != null)
        {
            _lastError = null;
            try
            {
                using var ms = new MemoryStream(wav);
                using var player = new System.Media.SoundPlayer(ms);
                player.PlaySync();
            }
            catch (Exception ex) { Report($"[ゲームログ] 読み上げの音を鳴らせませんでした: {ex.Message}"); }
            return;
        }
        if (error != _lastError)
        {
            _lastError = error;
            Report($"[ゲームログ] VOICEVOX で読み上げられませんでした: {error}" + (r.FallbackPath != null ? " (音のファイルを鳴らします)" : ""));
        }
        if (r.FallbackPath is not { } path) return;
        try
        {
            if (!File.Exists(path))
            {
                Report($"[ゲームログ] 音のファイルがありません: {path}");
                return;
            }
            using var player = new System.Media.SoundPlayer(path);
            player.Load();
            player.PlaySync();
        }
        catch (Exception ex) { Report($"[ゲームログ] 音を鳴らせませんでした: {path} ({ex.Message})"); }
    }

    private static void Report(string message)
    {
        try { Log?.Invoke(message); }
        catch { }
    }

    // 試聴: wav を作ってすぐ鳴らす (順番待ちを通さない)。失敗したら例外 (理由は GameLogVoiceVox.Reason)
    private static System.Media.SoundPlayer? _testPlayer;
    public static async Task TestAsync(string url, int speaker, string text, double? speedScale = null)
    {
        var wav = await GameLogVoiceVox.GetWavAsync(url, speaker, text, CancellationToken.None, speedScale);
        _testPlayer?.Stop();
        _testPlayer?.Dispose();
        var player = new System.Media.SoundPlayer(new MemoryStream(wav));
        player.Load();
        player.Play();
        _testPlayer = player;
    }
}

// ─── 設定タブの表 ───

// 声の選択肢 1 つ。Key = "" (既定の声) か話者のスタイルの id
public sealed class GameLogVoiceChoice
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
}

// 設定タブの「ゲームログの音」の表の 1 行
public sealed class GameLogVoiceRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string DefaultText { get; init; } = "";
    public ObservableCollection<GameLogVoiceChoice> Choices { get; init; } = new();

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; Notify(nameof(Enabled)); }
    }

    private string _text = "";
    public string Text
    {
        get => _text;
        set { value ??= ""; if (_text == value) return; _text = value; Notify(nameof(Text)); }
    }

    // 声 ("" = 既定)。選択肢を作り直す間にコンボボックスから来る null は無視する (保存済みの指定を消さないため)
    private string _speakerKey = "";
    public string? SpeakerKey
    {
        get => _speakerKey;
        set { if (value == null || _speakerKey == value) return; _speakerKey = value; Notify(nameof(SpeakerKey)); }
    }

    internal void RefreshSpeakerKey() => Notify(nameof(SpeakerKey));
}

// 設定タブの「ゲームログの音」の表と既定の声の選択肢 (MainWindow が持つ)
public sealed class GameLogVoiceSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ObservableCollection<GameLogVoiceRow> Rows { get; } = new();
    // 表の行の声の選択肢 (先頭が「既定」)
    public ObservableCollection<GameLogVoiceChoice> RowChoices { get; } = new();
    // 既定の声の選択肢
    public ObservableCollection<GameLogVoiceChoice> DefaultChoices { get; } = new();
    // 話者の一覧を VOICEVOX から取れたか
    public bool SpeakersLoaded { get; private set; }
    private List<GameLogVoiceSpeaker> _speakers = new();

    private string _defaultSpeakerKey = "16";
    public string? DefaultSpeakerKey
    {
        get => _defaultSpeakerKey;
        set { if (value == null || _defaultSpeakerKey == value) return; _defaultSpeakerKey = value; Notify(nameof(DefaultSpeakerKey)); }
    }

    public GameLogVoiceRow ArmisticeRow => Rows.First(r => r.Key == GameLogVoiceScenes.Armistice);
    public GameLogVoiceRow ArrivalRow => Rows.First(r => r.Key == GameLogVoiceScenes.Arrival);

    // 設定から表を作る (文が空なら既定の文を出す)
    public void Load(AppConfig config)
    {
        Rows.Clear();
        foreach (var s in GameLogVoiceScenes.All)
        {
            var item = GameLogVoiceScenes.ItemOf(config, s.Key);
            Rows.Add(new GameLogVoiceRow
            {
                Key = s.Key, Label = s.Label, DefaultText = s.DefaultText, Choices = RowChoices,
                Enabled = GameLogVoiceScenes.IsEnabled(config, s.Key),
                Text = string.IsNullOrWhiteSpace(item.Text) ? s.DefaultText : item.Text,
                SpeakerKey = item.SpeakerId is { } id ? id.ToString(CultureInfo.InvariantCulture) : "",
            });
        }
        DefaultSpeakerKey = config.GameLogVoiceVoxSpeakerId.ToString(CultureInfo.InvariantCulture);
        RebuildChoices();
    }

    // 表を設定に書き戻す (休戦ゾーン進入・到着の「読む」は ArmisticeSoundEnabled / ArrivalSoundEnabled にも)
    public void Save(AppConfig config)
    {
        var items = new List<GameLogVoiceItem>();
        foreach (var r in Rows)
        {
            items.Add(new GameLogVoiceItem
            {
                Kind = r.Key, Enabled = r.Enabled, Text = r.Text.Trim(),
                SpeakerId = int.TryParse(r.SpeakerKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null,
            });
            if (r.Key == GameLogVoiceScenes.Armistice) config.ArmisticeSoundEnabled = r.Enabled;
            if (r.Key == GameLogVoiceScenes.Arrival) config.ArrivalSoundEnabled = r.Enabled;
        }
        config.GameLogVoiceItems = items;
        if (int.TryParse(DefaultSpeakerKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var def)) config.GameLogVoiceVoxSpeakerId = def;
    }

    // VOICEVOX から取った話者の一覧を選択肢にする
    public void SetSpeakers(List<GameLogVoiceSpeaker> speakers)
    {
        _speakers = speakers;
        SpeakersLoaded = true;
        RebuildChoices();
    }

    // 選択肢を作り直す。一覧に無い保存済みの id は「話者 ID n」として足す (一覧が取れないときもそのまま使えるように)
    private void RebuildChoices()
    {
        var known = new Dictionary<int, string>();
        foreach (var s in _speakers) known.TryAdd(s.Id, s.Label);
        var extra = new SortedSet<int>();
        if (int.TryParse(DefaultSpeakerKey, out var d) && !known.ContainsKey(d)) extra.Add(d);
        foreach (var r in Rows)
            if (int.TryParse(r.SpeakerKey, out var id) && !known.ContainsKey(id)) extra.Add(id);

        var list = new List<GameLogVoiceChoice>();
        foreach (var id in extra) list.Add(new GameLogVoiceChoice { Key = id.ToString(CultureInfo.InvariantCulture), Label = $"話者 ID {id}" });
        foreach (var s in _speakers) list.Add(new GameLogVoiceChoice { Key = s.Id.ToString(CultureInfo.InvariantCulture), Label = s.Label });

        DefaultChoices.Clear();
        foreach (var c in list) DefaultChoices.Add(c);
        RowChoices.Clear();
        RowChoices.Add(new GameLogVoiceChoice { Key = "", Label = "既定" });
        foreach (var c in list) RowChoices.Add(c);
        // 選択肢を作り直すとコンボボックスの選択が外れるので、覚えている値を選び直させる
        Notify(nameof(DefaultSpeakerKey));
        foreach (var r in Rows) r.RefreshSpeakerKey();
    }
}
